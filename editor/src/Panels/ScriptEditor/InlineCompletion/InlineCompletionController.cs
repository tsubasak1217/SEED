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
    /// <summary>
    /// 補完リクエストのデバウンス（入力停止から発火までの待ち時間）。
    /// トリガを絞ってリクエスト数（＝トークン消費）を抑えるため、状況で待ち時間を変える。
    ///
    /// - <see cref="FastDebounce"/>: 「コメント行を書いて改行した直後」など意図が強い場面。素早く出す。
    /// - <see cref="SlowDebounce"/>: それ以外の一般的な入力停止。しっかり手が止まってから出すことで
    ///   行末で止まるたびに毎回投げてしまうのを防ぎ、Groq の TPM（トークン/分）超過を減らす。
    /// </summary>
    private static readonly TimeSpan FastDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SlowDebounce = TimeSpan.FromMilliseconds(600);

    /// <summary>ゴーストテキストで表示・確定する最大行数（過大な予測を抑える）。</summary>
    private const int MaxGhostLines = 8;

    private readonly TextEditor                 _editor;
    private readonly IInlineCompletionProvider  _provider;
    private readonly GhostTextRenderer          _renderer;
    private readonly DispatcherTimer            _debounceTimer;
    private readonly Func<bool>                 _isEnabled;    // 設定でインライン補完が有効か
    private readonly Func<bool>                 _isSuppressed; // IntelliSense 表示中など抑止すべきか
    private readonly Func<bool>                 _isAutoTrigger; // 入力中に自動発火するか（false=手動キーのみ）

    private CancellationTokenSource? _cts;
    private bool _accepting;   // 自前の確定挿入中は再トリガ・却下を抑止する

    public InlineCompletionController(
        TextEditor editor,
        IInlineCompletionProvider provider,
        Func<bool> isEnabled,
        Func<bool> isSuppressed,
        Func<bool> isAutoTrigger)
    {
        _editor        = editor;
        _provider      = provider;
        _isEnabled     = isEnabled;
        _isSuppressed  = isSuppressed;
        _isAutoTrigger = isAutoTrigger;

        _renderer = new GhostTextRenderer(editor);
        editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);

        _debounceTimer = new DispatcherTimer { Interval = SlowDebounce };
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

        // 複数行のときは予測内の LF をドキュメントの改行コードへ合わせて挿入する
        // （CRLF ファイルへ LF を混ぜないようにする）。
        string toInsert = text.Replace("\n", DocNewLine());

        _accepting = true;
        try
        {
            _editor.Document.Insert(offset, toInsert);
            _editor.CaretOffset = offset + toInsert.Length;
        }
        finally { _accepting = false; }

        Dismiss();
        return true;
    }

    /// <summary>ドキュメントの改行コードを返す（既定は CRLF）。</summary>
    private string DocNewLine()
    {
        var doc = _editor.Document;
        if (doc.LineCount >= 1)
        {
            var first = doc.GetLineByNumber(1);
            if (first.DelimiterLength == 2) return "\r\n";
            if (first.DelimiterLength == 1) return "\n";
        }
        return "\r\n";
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
        // 手動トリガモードでは入力中に自動発火しない（キー押下＝RequestNow のみ）。
        if (!_isAutoTrigger()) return;
        // 入力が続く間はリクエストを遅延させる（デバウンス）。
        // 意図が強い場面（コメント行の直後で改行）は短く、それ以外は長く待って発火を絞る。
        _debounceTimer.Stop();
        _debounceTimer.Interval = IsAfterCommentNewline() ? FastDebounce : SlowDebounce;
        _debounceTimer.Start();
    }

    /// <summary>
    /// ショートカットからの手動発火。デバウンスを介さず即座に補完をリクエストする。
    /// 自動発火が無効（手動モード）でも動作する。
    /// </summary>
    public void RequestNow()
    {
        if (!_isEnabled()) return;
        _debounceTimer.Stop();
        if (_renderer.HasText)          // 既存ゴーストは消してから出し直す
        {
            _renderer.Clear();
            _editor.TextArea.TextView.Redraw();
        }
        _ = RequestAsync();
    }

    /// <summary>
    /// 直前に「コメント行を書いて改行した」直後か（処理内容をコメントで書き、続きのコード生成を
    /// 待っている状況）。カーソル行が空白のみ（改行直後の新規行）で、1 つ上の行が
    /// <c>//</c> コメントのときに真とする。強いトリガとして短いデバウンスで発火させる。
    /// </summary>
    private bool IsAfterCommentNewline()
    {
        // ここで例外が漏れると OnTextChanged の _debounceTimer.Start() に到達せず、
        // 補完が二度と発火しなくなる。安全側に倒して必ず bool を返す。
        try
        {
            var doc   = _editor.Document;
            int caret = _editor.CaretOffset;
            if (caret < 0 || caret > doc.TextLength) return false;

            var line = doc.GetLineByOffset(caret);
            // カーソル行の行頭からカーソルまでが空白のみ（＝改行直後の新しい行）か
            if (doc.GetText(line.Offset, caret - line.Offset).Trim().Length != 0) return false;

            // 1 つ上の行が // コメントか
            if (line.LineNumber <= 1) return false;
            var prev = doc.GetLineByNumber(line.LineNumber - 1);
            return doc.GetText(prev.Offset, prev.Length).TrimStart().StartsWith("//");
        }
        catch { return false; }
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
        // どのゲートで止まったかを追えるよう、抑止理由をログに残す（原因調査用）
        if (!_isEnabled())        { Log("skip: 設定オフ");             return; }
        if (_isSuppressed())      { Log("skip: IntelliSense表示中");   return; }
        if (!_provider.IsAvailable) { Log("skip: サーバー未起動");     return; }

        var document = _editor.Document;
        int caret    = _editor.CaretOffset;

        // カーソル以降の行内に非空白があると、ゴーストが既存テキストへ重なって
        // 見づらいので、行末（後続が空白のみ）のときだけ表示する。
        var line = document.GetLineByOffset(caret);
        if (caret < line.EndOffset)
        {
            string rest = document.GetText(caret, line.EndOffset - caret);
            if (rest.TrimEnd().Length > 0) { Log("skip: 行の途中"); return; }
        }

        string text   = _editor.Text;
        string prefix = text[..caret];
        string suffix = text[caret..];

        CancelPending();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Log($"request: caret={caret} prefixLen={prefix.Length}");
        string? result = await _provider.GetCompletionAsync(prefix, suffix, token);
        if (result is null || token.IsCancellationRequested) { Log("result: null/cancel"); return; }

        // リクエスト中に編集・カーソル移動があれば破棄する（陳腐化防止）
        if (_editor.CaretOffset != caret || _editor.Document.TextLength != text.Length) { Log("result: 陳腐化"); return; }

        string suggestion = CleanSuggestion(result);
        if (suggestion.Length == 0) { Log("result: 空"); return; }   // 空白・改行のみは出さない

        // このエンジンに存在しない Unity API を含む予測は誤りなので出さない
        // （小型モデルは C# ゲームコードを Unity と誤認しがち）。
        if (LooksLikeWrongApi(suggestion)) { Log("result: Unity API のため破棄"); return; }

        // 表示は既存コードを覆わない範囲（下の空行分）に制限し、見た目と挿入内容を一致させる
        suggestion = CapToAvailableLines(suggestion, caret);
        if (suggestion.Length == 0) { Log("result: 表示可能行なし"); return; }

        Log($"show: {suggestion.Split('\n').Length}行");
        _renderer.SetText(suggestion, caret);
        _editor.TextArea.TextView.Redraw();
    }

    /// <summary>SEED エンジンに存在しない明らかな Unity 依存 API を含むか。</summary>
    private static bool LooksLikeWrongApi(string s)
        => s.Contains("UnityEngine") || s.Contains("MonoBehaviour");

    /// <summary>
    /// 予測を「カーソル行の下にある空行数＋1 行目」までに制限する。
    /// これにより 2 行目以降が既存コードを覆わず、プレビューと確定内容が一致する。
    /// 下が EOF まで空（＝以降は自由に書ける）の場合は制限しない。
    /// </summary>
    private string CapToAvailableLines(string suggestion, int caret)
    {
        var lines = suggestion.Split('\n');
        if (lines.Length <= 1) return suggestion;

        var doc        = _editor.Document;
        int caretLine  = doc.GetLineByOffset(caret).LineNumber;
        bool hitCode   = false;
        int  available = 0;
        for (int ln = caretLine + 1; ln <= doc.LineCount; ln++)
        {
            var dl = doc.GetLineByNumber(ln);
            if (doc.GetText(dl.Offset, dl.Length).Trim().Length > 0) { hitCode = true; break; }
            available++;
        }

        // 既存コードに当たる場合のみ「1 行目＋空行数」に制限（EOF まで空なら無制限）
        int maxLines = hitCode ? 1 + available : lines.Length;
        if (lines.Length > maxLines) lines = lines[..maxLines];
        return string.Join("\n", lines);
    }

    /// <summary>インライン補完の調査用ログ（[インライン補完] 接頭辞で追える）。</summary>
    private static void Log(string message) => SEEDEditor.EditorLog.Write($"[インライン補完] {message}");

    /// <summary>
    /// 予測文字列を表示・挿入用に整形する。改行を LF に正規化し、末尾の空白・改行を
    /// 除いたうえで、長すぎる予測は先頭 <see cref="MaxGhostLines"/> 行までに制限する。
    /// </summary>
    private static string CleanSuggestion(string s)
    {
        s = s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
        if (s.Length == 0) return "";

        var lines = s.Split('\n');
        if (lines.Length > MaxGhostLines)
            lines = lines[..MaxGhostLines];
        return string.Join("\n", lines);
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
