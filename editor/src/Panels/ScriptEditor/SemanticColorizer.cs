using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// Roslyn のセマンティック分類結果に基づき、型名・メソッド名・フィールド名など
/// 「意味的に識別が必要なトークン」の前景色を上書きする LineTransformer。
///
/// AvalonEdit 標準の C# ハイライトは正規表現ベースのため、ユーザー定義の
/// クラス名やフィールド名を識別できない。本クラスは Roslyn の分類スパンを
/// 受け取り、該当範囲だけ前景色を差し替える（キーワード・文字列・コメント等の
/// 正規表現ハイライトはそのまま活かす）。
/// </summary>
public sealed class SemanticColorizer : DocumentColorizingTransformer
{
    /// <summary>1 つの分類スパン（文書オフセット・長さ・着色ブラシ）。</summary>
    public readonly record struct Span(int Offset, int Length, Brush Brush);

    // 描画対象の分類スパン一覧。テキスト変更のたびに差し替えられる。
    private IReadOnlyList<Span> _spans = Array.Empty<Span>();

    /// <summary>分類スパンを差し替える（呼び出し後に TextView.Redraw が必要）。</summary>
    public void SetSpans(IReadOnlyList<Span> spans) => _spans = spans;

    /// <summary>全スパンをクリアする。</summary>
    public void Clear() => _spans = Array.Empty<Span>();

    /// <summary>行単位で、交差する分類スパンの前景色を適用する。</summary>
    protected override void ColorizeLine(DocumentLine line)
    {
        if (_spans.Count == 0) return;

        int lineStart = line.Offset;
        int lineEnd   = line.EndOffset;

        foreach (var s in _spans)
        {
            int start = s.Offset;
            int end   = s.Offset + s.Length;

            // この行と交差しないスパンはスキップ
            if (end <= lineStart || start >= lineEnd) continue;

            int cs = Math.Max(start, lineStart);
            int ce = Math.Min(end, lineEnd);
            if (cs >= ce) continue;

            var brush = s.Brush;
            ChangeLinePart(cs, ce, el => el.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
