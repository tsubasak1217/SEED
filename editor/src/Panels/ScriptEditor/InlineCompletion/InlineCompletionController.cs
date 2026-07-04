using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// 1 つのエディタに対するインライン補完（Copilot 風ゴーストテキスト）の制御役。
///
/// 責務:
///   - 入力が一定時間止まったら（デバウンス）補完をリクエストする
///   - 得られた予測を <see cref="GhostTextRenderer"/> でカーソル後ろに表示する
///   - Tab で確定（実挿入）、Esc・カーソル移動・再入力で却下する
/// 補完バックエンド（ローカル LLM 通信）は <see cref="InlineCompletionProvider"/> に委譲する。
/// </summary>
public sealed class InlineCompletionController : IDisposable
{
    /// <summary>入力停止から補完リクエストまでの待ち時間。</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);

    private readonly TextEditor                _editor;
    private readonly InlineCompletionProvider  _provider;
    private readonly GhostTextRenderer         _renderer;
    private readonly DispatcherTimer           _debounceTimer;
    private readonly Func<bool>                _isEnabled;    // 設定でインライン補完が有効か
    private readonly Func<bool>                _isSuppressed; // IntelliSense 表示中など抑止すべきか

    private CancellationTokenSource? _cts;
    private bool _accepting;   // 自前の確定挿入中は再トリガ・却下を抑止する

    public InlineCompletionController(
        TextEditor editor,
        InlineCompletionProvider provider,
        Func<bool> isEnabled,
        Func<bool> isSuppressed)
    {
        _editor       = editor;
        _provider     = provider;
        _isEnabled    = isEnabled;
        _isSuppressed = isSuppressed;

        _renderer = new GhostTextRenderer(editor);
        editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);

        _debounceTimer = new DispatcherTimer { Interval = Debounce };
        _debounceTimer.Tick += OnDebounceTick;

        editor.TextChanged                    += OnTextChanged;
        editor.TextArea.Caret.PositionChanged += OnCaretMoved;
    }

    /// <summary>現在ゴーストテキストを表示中か。</summary>
    public bool HasSuggestion => _renderer.HasText;

    /// <summary>
    /// 表示中の予測を確定してドキュメントへ挿入する（Tab 用）。
    /// 予測が無ければ false（呼び出し側は既定の Tab 動作へ）。
    /// </summary>
    public bool AcceptSuggestion()
    {
        if (!_renderer.HasText || _renderer.Text is not { } text) return false;

        int offset = _renderer.Offset;
        if (offset < 0 || offset > _editor.Document.TextLength) { Dismiss(); return false; }

        _accepting = true;
        try
        {
            _editor.Document.Insert(offset, text);
            _editor.CaretOffset = offset + text.Length;
        }
        finally { _accepting = false; }

        Dismiss();
        return true;
    }

    /// <summary>予測表示を消す（Esc・却下・状態変化時）。</summary>
    public void Dismiss()
    {
        _debounceTimer.Stop();
        CancelPending();
        if (_renderer.HasText)
        {
            _renderer.Clear();
            _editor.TextArea.TextView.Redraw();
        }
    }

    // ── イベント ─────────────────────────────────────────────

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_accepting) return;          // 自前の確定挿入は無視する
        Dismiss();                        // 既存の予測は一旦消す
        if (!_isEnabled()) return;
        // 入力が続く間はリクエストを遅延させる（デバウンス）
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        // 確定挿入以外でカーソルが動いたら予測は無効化する
        if (_accepting) return;
        if (_renderer.HasText && _editor.CaretOffset != _renderer.Offset)
            Dismiss();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        _ = RequestAsync();
    }

    // ── 補完リクエスト ───────────────────────────────────────

    private async Task RequestAsync()
    {
        if (!_isEnabled() || _isSuppressed() || !_provider.IsAvailable) return;

        var document = _editor.Document;
        int caret    = _editor.CaretOffset;

        // カーソル以降の行内に非空白があると、ゴーストが既存テキストへ重なって
        // 見づらいので、行末（後続が空白のみ）のときだけ表示する。
        var line = document.GetLineByOffset(caret);
        if (caret < line.EndOffset)
        {
            string rest = document.GetText(caret, line.EndOffset - caret);
            if (rest.TrimEnd().Length > 0) return;
        }

        string text   = _editor.Text;
        string prefix = text[..caret];
        string suffix = text[caret..];

        CancelPending();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        string? result = await _provider.GetCompletionAsync(prefix, suffix, token);
        if (result is null || token.IsCancellationRequested) return;

        // リクエスト中に編集・カーソル移動があれば破棄する（陳腐化防止）
        if (_editor.CaretOffset != caret || _editor.Document.TextLength != text.Length) return;

        string firstLine = FirstLine(result);
        if (firstLine.Length == 0) return;   // 改行のみ等の無意味な予測は出さない

        _renderer.SetText(firstLine, caret);
        _editor.TextArea.TextView.InvalidateLayer(_renderer.Layer);
    }

    /// <summary>予測文字列の先頭 1 行を取り出す（末尾空白は除去、v1 は 1 行表示）。</summary>
    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\n');
        string first = nl >= 0 ? s[..nl] : s;
        return first.TrimEnd('\r', ' ', '\t');
    }

    private void CancelPending()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTick;
        _editor.TextChanged                    -= OnTextChanged;
        _editor.TextArea.Caret.PositionChanged -= OnCaretMoved;
        CancelPending();
        _editor.TextArea.TextView.BackgroundRenderers.Remove(_renderer);
    }
}
