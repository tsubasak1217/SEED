using System;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// 設定の「バックエンド種別」に応じて、ローカル LLM 版と Groq 版のプロバイダを
/// リクエスト毎に振り分けるルーター。コントローラはこの 1 つを保持すればよく、
/// 設定でバックエンドを切り替えても再生成は不要（都度セレクタを読む）。
/// </summary>
public sealed class RoutingInlineCompletionProvider : IInlineCompletionProvider
{
    /// <summary>補完バックエンドの種別。</summary>
    public enum Backend { Local, Groq }

    private readonly Func<Backend>              _selector;
    private readonly IInlineCompletionProvider  _local;
    private readonly IInlineCompletionProvider  _groq;

    public RoutingInlineCompletionProvider(
        Func<Backend> selector,
        IInlineCompletionProvider local,
        IInlineCompletionProvider groq)
    {
        _selector = selector;
        _local    = local;
        _groq     = groq;
    }

    /// <summary>現在の設定で選ばれているプロバイダ。</summary>
    private IInlineCompletionProvider Current
        => _selector() == Backend.Groq ? _groq : _local;

    public bool IsAvailable => Current.IsAvailable;

    public Task<string?> GetCompletionAsync(string prefix, string suffix, CancellationToken ct)
        => Current.GetCompletionAsync(prefix, suffix, ct);
}
