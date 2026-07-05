using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Debugger;

/// <summary>
/// Debug Adapter Protocol（DAP）のクライアント実装。
///
/// netcoredbg（--interpreter=vscode）と標準入出力で DAP メッセージをやり取りする。
/// メッセージは「Content-Length: N\r\n\r\n{JSON}」のフレームで区切られる。
///
/// 責務:
///   - リクエスト送信と、対応するレスポンスの待ち合わせ（request_seq で相関）
///   - イベント（stopped / output / terminated など）の配信
///   - バックグラウンド読み取りループとフレーム解析
///
/// このクラスはプロトコル層のみ。アタッチ手順やブレークポイント設定などの
/// 手続きは <see cref="ScriptDebugSession"/> が組み立てる。
/// </summary>
public sealed class DapClient : IDisposable
{
    private readonly Stream _reader;   // アダプタ → クライアント（標準出力）
    private readonly Stream _writer;   // クライアント → アダプタ（標準入力）
    private readonly object _writeLock = new();

    // request seq → レスポンス待ち
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();

    private int _seq;   // 送信シーケンス番号（1 から単調増加）

    /// <summary>イベント受信（イベント名, body）。UI スレッドとは限らないので注意。</summary>
    public event Action<string, JsonNode?>? EventReceived;

    /// <summary>プロトコル層のログ（送受信・エラー）。調査用。</summary>
    public event Action<string>? Log;

    public DapClient(Stream reader, Stream writer)
    {
        _reader = reader;
        _writer = writer;
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// リクエストを送信し、レスポンスの body を待って返す。
    /// success=false のレスポンスは <see cref="DapException"/> を投げる。
    /// </summary>
    public async Task<JsonNode?> SendRequestAsync(string command, JsonObject? arguments, CancellationToken ct = default)
    {
        int seq = Interlocked.Increment(ref _seq);
        var msg = new JsonObject
        {
            ["seq"]     = seq,
            ["type"]    = "request",
            ["command"] = command,
        };
        if (arguments is not null) msg["arguments"] = arguments;

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;

        WriteMessage(msg);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        using var link = _cts.Token.Register(() => tcs.TrySetException(new DapException($"デバッガ接続が閉じられました（{command}）")));
        return await tcs.Task.ConfigureAwait(false);
    }

    // ── 送信 ────────────────────────────────────────────────
    private void WriteMessage(JsonObject msg)
    {
        var json  = msg.ToJsonString();
        var body  = Encoding.UTF8.GetBytes(json);
        var head  = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_writeLock)
        {
            _writer.Write(head, 0, head.Length);
            _writer.Write(body, 0, body.Length);
            _writer.Flush();
        }
        Log?.Invoke($"→ {json}");
    }

    // ── 受信ループ ───────────────────────────────────────────
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int contentLength = await ReadHeaderAsync().ConfigureAwait(false);
                if (contentLength < 0) break;   // ストリーム終端

                var body = await ReadExactAsync(contentLength).ConfigureAwait(false);
                if (body is null) break;

                var json = Encoding.UTF8.GetString(body);
                Log?.Invoke($"← {json}");
                Dispatch(json);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[DAP] 読み取りループ終了: {ex.Message}");
        }
        finally
        {
            // 接続断: 未完了のリクエストをすべて失敗させる
            foreach (var kv in _pending)
                kv.Value.TrySetException(new DapException("デバッガ接続が終了しました"));
            _pending.Clear();
        }
    }

    /// <summary>ヘッダ部を読み、Content-Length を返す（終端なら -1）。</summary>
    private async Task<int> ReadHeaderAsync()
    {
        // "\r\n\r\n" までを 1 バイトずつ読む（ヘッダは短いので十分）
        var sb = new StringBuilder();
        var one = new byte[1];
        int matched = 0;   // 直近に一致した "\r\n\r\n" の桁数
        while (true)
        {
            int n = await _reader.ReadAsync(one.AsMemory(0, 1), _cts.Token).ConfigureAwait(false);
            if (n == 0) return -1;

            char c = (char)one[0];
            sb.Append(c);
            matched = (c == "\r\n\r\n"[matched]) ? matched + 1 : (c == '\r' ? 1 : 0);
            if (matched == 4) break;
        }

        // ヘッダ行から Content-Length を抽出する
        foreach (var line in sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim();
            if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(val, out var len))
                return len;
        }
        return 0;
    }

    /// <summary>ちょうど count バイト読む（終端なら null）。</summary>
    private async Task<byte[]?> ReadExactAsync(int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _reader.ReadAsync(buffer.AsMemory(read, count - read), _cts.Token).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return buffer;
    }

    /// <summary>受信 JSON を種別（response / event）で振り分ける。</summary>
    private void Dispatch(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (Exception ex) { Log?.Invoke($"[DAP] JSON 解析失敗: {ex.Message}"); return; }
        if (node is not JsonObject obj) return;

        var type = obj["type"]?.GetValue<string>();
        switch (type)
        {
            case "response":
            {
                int requestSeq = obj["request_seq"]?.GetValue<int>() ?? -1;
                if (!_pending.TryRemove(requestSeq, out var tcs)) return;

                bool success = obj["success"]?.GetValue<bool>() ?? false;
                if (success)
                {
                    tcs.TrySetResult(obj["body"]);
                }
                else
                {
                    var message = obj["message"]?.GetValue<string>() ?? "リクエスト失敗";
                    tcs.TrySetException(new DapException(message));
                }
                break;
            }
            case "event":
            {
                var ev = obj["event"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(ev))
                    EventReceived?.Invoke(ev!, obj["body"]);
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>DAP のリクエスト失敗・接続断を表す例外。</summary>
public sealed class DapException : Exception
{
    public DapException(string message) : base(message) { }
}
