using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Ipc;

/// <summary>
/// Named Pipe サーバー。Runtime（クライアント）からの接続を待ち受け、
/// 双方向でメッセージを送受信する。
///
/// 行の送受信（受信ループ・送信・文字コード）は通信路に依存しない <see cref="IpcLineChannel"/> が行う
/// （Android の TCP の通信路と共有。段階D-1）。ここは名前付きパイプの作成と接続待ちだけを持つ。
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly NamedPipeServerStream _pipe;

    /// <summary>行の送受信（接続するまでは null）。</summary>
    private IpcLineChannel? _channel;

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
        // 受信ループが UI スレッドコンテキストで動くと、UIスレッドがブロック中に
        // ReadLineAsync の継続が実行されず、Rust→C# バッファが詰まってデッドロックになる。
        await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        // 読み続ける条件にパイプの接続状態を渡す（従来の受信ループと同じく、切れたら止める）
        var channel = new IpcLineChannel(_pipe, nameof(PipeServer), () => _pipe.IsConnected);
        channel.MessageReceived += line => MessageReceived?.Invoke(line);
        _channel = channel;
        channel.Start();
    }

    /// <summary>Runtime にコマンドを送信する。</summary>
    public void Send(string message)
    {
        if (!_pipe.IsConnected) return;
        try
        {
            _channel?.Send(message);
        }
        catch (IOException ex)
        {
            SEEDEditor.EditorLog.Write($"[Pipe.Send ERR ] IOException: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _pipe.Dispose();
    }
}
