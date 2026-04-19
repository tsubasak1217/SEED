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
        await _pipe.WaitForConnectionAsync(ct);
        _reader = new StreamReader(_pipe,  leaveOpen: true);
        _writer = new StreamWriter(_pipe,  leaveOpen: true) { AutoFlush = true };
        _ = ReadLoopAsync(_cts.Token);
    }

    /// <summary>Runtime にコマンドを送信する。</summary>
    public void Send(string message)
    {
        if (!_pipe.IsConnected) return;
        try { _writer?.WriteLine(message); }
        catch (IOException) { /* パイプ切断 */ }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _pipe.IsConnected)
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line == null) break;
                MessageReceived?.Invoke(line.Trim());
            }
        }
        catch { /* パイプ切断・キャンセル */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pipe.Dispose();
        _cts.Dispose();
    }
}
