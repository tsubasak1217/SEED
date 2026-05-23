using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Ipc;

/// <summary>
/// Named Pipe サーバー。Runtime（クライアント）からの接続を待ち受け、
/// 双方向でメッセージを送受信する。
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private StreamReader?                  _reader;
    private StreamWriter?                  _writer;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Runtime に渡すパイプ名（\\.\pipe\ 以降の部分）。</summary>
    public string PipeName { get; }

    public bool IsConnected => _pipe.IsConnected;

    /// <summary>Runtime からメッセージを受信したときに発火する。</summary>
    public event Action<string>? MessageReceived;

    public PipeServer()
    {
        PipeName = $"SEED_{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    /// <summary>Runtime が接続してくるまで非同期に待機する。</summary>
    public async Task WaitForConnectionAsync(CancellationToken ct = default)
    {
        // ConfigureAwait(false) でUIスレッドのSyncContextを引き継がないようにする。
        // ReadLoopAsync が UI スレッドコンテキストで動くと、UIスレッドがブロック中に
        // ReadLineAsync の継続が実行されず、Rust→C# バッファが詰まってデッドロックになる。
        await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        _reader = new StreamReader(_pipe,  leaveOpen: true);
        _writer = new StreamWriter(_pipe,  leaveOpen: true) { AutoFlush = true };
        _ = ReadLoopAsync(_cts.Token);
    }

    /// <summary>Runtime にコマンドを送信する。</summary>
    public void Send(string message)
    {
        if (!_pipe.IsConnected) return;
        try
        {
            _writer?.WriteLine(message);
        }
        catch (IOException ex)
        {
            SEEDEditor.EditorLog.Write($"[Pipe.Send ERR ] IOException: {ex.Message}");
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _pipe.IsConnected)
        {
            string? line;
            try
            {
                // ConfigureAwait(false): UIスレッドのSyncContextを引き継がない。
                // これにより「UIスレッドブロック中も読み取りループが継続」し、
                // Rust→C# バッファが詰まってデッドロックになるのを防ぐ。
                line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // パイプ切断・キャンセル: ループを終了する
                break;
            }

            if (line == null) break;

            try
            {
                // イベントハンドラの例外がループを止めないよう個別に保護する
                MessageReceived?.Invoke(line.Trim());
            }
            catch (Exception ex)
            {
                // ハンドラ例外はログに記録してループを継続する
                System.Diagnostics.Debug.WriteLine($"[PipeServer] MessageReceived handler threw: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipe.Dispose();
        _cts.Dispose();
    }
}
