// ============================================================
//  IpcLineChannel.cs — ランタイムとの IPC の「行の送受信」（通信路に依存しない。段階D-1・2026-09-25）
//
//  【役割】
//  1 行 1 コマンドの文字列プロトコル（書式の正典はランタイムの runtime/src/engine/core/app_base/ipc.rs）を、
//  双方向の Stream の上で送り・受ける。通信路（Stream）は差し替えられる:
//    名前付きパイプ … PipeServer（PC の Edit の埋め込み・PC の Play。RuntimeManager）
//    TCP           … Android/Ipc/AndroidIpcSession（Android の実行。adb forward 越し。エディタの実行バー・SeedAndroid）
//  どちらもこのクラスで読み書きするので、行の区切り・文字コード・受信ループ・例外の扱いが二重にならない。
//
//  【決まり（段階D-1 の前の PipeServer の振る舞いをそのまま移した）】
//  - 受信は専用の非同期ループ（ConfigureAwait(false)。UI スレッドの同期コンテキストに乗せない。UI スレッドが止まっている間も
//    読み続け、ランタイム → エディタの向きが詰まってデッドロックしないように）。1 行ごとに前後の空白を落として MessageReceived へ。
//    受け手の例外はループを止めない（Debug 出力に残して次の行へ）。
//  - 送信は StreamWriter.WriteLine（AutoFlush）。複数のスレッドから呼ばれても行が混ざらないようロックする。
//  - 文字コードは UTF-8（BOM なし）で書き、UTF-8 で読む（BOM があれば従う）。StreamReader / StreamWriter の既定のまま。
//  - 読み取りが終わる（相手が閉じた・切れた・読み取りの失敗・Dispose）と Closed が完了する。
//  - Stream は閉じない（持ち主＝PipeServer / AndroidIpcSession が閉じる）。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Ipc;

/// <summary>ランタイムとの IPC の行の送受信（通信路の Stream に依存しない）。</summary>
public sealed class IpcLineChannel : IDisposable
{
    /// <summary>読み取り口（UTF-8。BOM があれば従う）。</summary>
    private readonly StreamReader _reader;

    /// <summary>書き込み口（UTF-8・BOM なし・AutoFlush）。</summary>
    private readonly StreamWriter _writer;

    /// <summary>書き込みの排他（行が混ざらないように）。</summary>
    private readonly object _writeGate = new();

    /// <summary>受信ループの中断の合図。</summary>
    private readonly CancellationTokenSource _cancellation = new();

    /// <summary>受信ループが終わると完了する。</summary>
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>読み続けてよいかの追加の条件（名前付きパイプの IsConnected 等。null なら常に真）。</summary>
    private readonly Func<bool>? _keepReading;

    /// <summary>Debug 出力に付ける名前（例 PipeServer）。</summary>
    private readonly string _diagnosticName;

    /// <summary>受信ループを始めたか（0 / 1）。</summary>
    private int _started;

    /// <summary>破棄したか（0 / 1）。</summary>
    private int _disposed;

    /// <summary>
    /// 通信路の Stream を指定して作る（受信はまだ始めない。<see cref="Start"/> で始める）。
    /// </summary>
    /// <param name="stream">読み書きできる通信路（閉じるのは持ち主）。</param>
    /// <param name="diagnosticName">Debug 出力に付ける名前。</param>
    /// <param name="keepReading">読み続けてよいかの追加の条件（毎行の前に確かめる。null なら常に真）。</param>
    public IpcLineChannel(Stream stream, string diagnosticName, Func<bool>? keepReading = null)
    {
        _reader = new StreamReader(stream, leaveOpen: true);
        _writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        _diagnosticName = diagnosticName;
        _keepReading = keepReading;
    }

    /// <summary>1 行を受け取った（前後の空白を落とした本文。受信ループのスレッドから）。</summary>
    public event Action<string>? MessageReceived;

    /// <summary>受信ループが終わると完了する（相手が閉じた・切れた・Dispose）。</summary>
    public Task Closed => _closed.Task;

    /// <summary>受信ループが終わったか。</summary>
    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>受信ループを始める（2 回目以降は何もしない）。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = ReadLoopAsync(_cancellation.Token);
    }

    /// <summary>
    /// 1 行を送る（行末の改行を足す）。
    /// </summary>
    /// <param name="message">本文（改行を含めない）。</param>
    /// <exception cref="IOException">通信路へ書けなかった（切れた等）。</exception>
    /// <exception cref="ObjectDisposedException">通信路が閉じられていた。</exception>
    public void Send(string message)
    {
        lock (_writeGate)
        {
            _writer.WriteLine(message);
        }
    }

    /// <summary>
    /// 1 行を送る（失敗しても例外を投げない）。
    /// </summary>
    /// <param name="message">本文（改行を含めない）。</param>
    /// <returns>書けたら true（切れていた・閉じていたら false）。</returns>
    public bool TrySend(string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        try
        {
            Send(message);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>受信ループを止める（Stream は閉じない。持ち主が閉じる）。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel();
        _cancellation.Dispose();
        // 受信ループをまだ始めていなければ、ここで「終わった」にする
        if (Volatile.Read(ref _started) == 0) _closed.TrySetResult();
    }

    /// <summary>受信ループ（相手が閉じる・切れる・中断されるまで 1 行ずつ読む）。</summary>
    /// <param name="cancellationToken">中断の合図。</param>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && (_keepReading?.Invoke() ?? true))
            {
                string? line;
                try
                {
                    // ConfigureAwait(false): UI スレッドの SyncContext を引き継がない。
                    // これにより「UI スレッドがブロック中も読み取りループが継続」し、
                    // ランタイム → エディタのバッファが詰まってデッドロックになるのを防ぐ。
                    line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 切断・中断・読み取りの失敗: ループを終える
                    break;
                }

                if (line is null) break;

                try
                {
                    // 受け手の例外がループを止めないよう個別に保護する
                    MessageReceived?.Invoke(line.Trim());
                }
                catch (Exception ex)
                {
                    // 受け手の例外は Debug 出力に残してループを続ける
                    Debug.WriteLine($"[{_diagnosticName}] MessageReceived handler threw: {ex.Message}");
                }
            }
        }
        finally
        {
            _closed.TrySetResult();
        }
    }
}
