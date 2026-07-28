using System;
using System.Linq;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// <see cref="WgslCompletion.Entry"/> を AvalonEdit の補完項目へ橋渡しするデータ。
///
/// 通常の候補は名前をそのまま挿入し、スニペット候補（shade_default など）は
/// 関数の骨格を挿入したうえでキャレットを本体行へ移動する。
/// Roslyn には依存しないため、.wgsl タブのみで使用する。
/// </summary>
public sealed class WgslCompletionData : ICompletionData
{
    private readonly WgslCompletion.Entry _entry;

    public WgslCompletionData(WgslCompletion.Entry entry) => _entry = entry;

    public ImageSource? Image => null;

    /// <summary>フィルタ・既定挿入に使う文字列（シンボル名）。</summary>
    public string Text => _entry.Name;

    /// <summary>リスト表示（種別記号 + 名前）。</summary>
    public object Content => $"{Glyph(_entry.Kind)} {_entry.Name}";

    /// <summary>ツールチップに出す詳細（型・シグネチャ＋日本語説明）。</summary>
    public object Description => _entry.Detail;

    /// <summary>表示順の優先度（大きいほど上に出る）。</summary>
    public double Priority => _entry.Priority;

    /// <summary>
    /// 候補を確定して文書へ挿入する。
    /// スニペットの場合は、挿入位置の行頭インデントを 2 行目以降へ引き継ぎ、
    /// 本文中のキャレットマーカー位置へキャレットを移動する。
    /// </summary>
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var document = textArea.Document;

        // 通常候補: 名前をそのまま置換挿入する（キャレットは挿入末尾＝AvalonEdit 既定）。
        if (_entry.SnippetBody is null)
        {
            document.Replace(completionSegment, _entry.Name);
            return;
        }

        // ── スニペット挿入 ─────────────────────────────────
        // 挿入位置の行から行頭インデント（空白・タブ）を取得する。
        var line     = document.GetLineByOffset(completionSegment.Offset);
        var lineText = document.GetText(line.Offset, line.Length);
        int indentLength = 0;
        while (indentLength < lineText.Length && (lineText[indentLength] == ' ' || lineText[indentLength] == '\t'))
            indentLength++;
        var indent = lineText[..indentLength];

        // 文書の改行コードに合わせて連結し、2 行目以降へインデントを付与する。
        var newLine = TextUtilities.GetNewLineFromDocument(document, line.LineNumber);
        var body = string.Join(
            newLine,
            _entry.SnippetBody.Split('\n').Select((text, index) => index == 0 ? text : indent + text));

        // キャレットマーカーの位置を控えてから取り除く。
        int caretIndex = body.IndexOf(WgslCompletion.CaretMarker, StringComparison.Ordinal);
        body = body.Replace(WgslCompletion.CaretMarker, string.Empty);

        int insertStart = completionSegment.Offset;
        document.Replace(completionSegment, body);
        // マーカーが無い場合は挿入末尾へ置く（保険）。
        textArea.Caret.Offset = caretIndex >= 0 ? insertStart + caretIndex : insertStart + body.Length;
    }

    /// <summary>候補種別に対応する表示記号（C# 側の SymbolCompletionData と同様の見た目）。</summary>
    private static string Glyph(WgslSymbolKind kind) => kind switch
    {
        WgslSymbolKind.Snippet      => "▶",
        WgslSymbolKind.Function     => "◆",
        WgslSymbolKind.Field        => "▪",
        WgslSymbolKind.Type         => "🅒",
        WgslSymbolKind.Keyword      => "◇",
        WgslSymbolKind.Constant     => "▫",
        WgslSymbolKind.DocumentWord => "•",
        _                           => "•",
    };
}
