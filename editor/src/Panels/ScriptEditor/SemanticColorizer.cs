using System;
using System.Collections.Generic;
using System.Linq;
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

    // 描画対象の分類スパン（Offset 昇順・非重複）。テキスト変更のたびに差し替えられる。
    // 大きなファイルでは数万件になるため、行描画では二分探索で該当分だけ処理する。
    private Span[] _spans = Array.Empty<Span>();

    /// <summary>分類スパンを差し替える（呼び出し後に TextView.Redraw が必要）。</summary>
    public void SetSpans(IReadOnlyList<Span> spans)
        // 二分探索の前提として Offset 昇順に整列させる（Roslyn は基本整列済みだが防御的に）
        => _spans = spans.OrderBy(s => s.Offset).ToArray();

    /// <summary>全スパンをクリアする。</summary>
    public void Clear() => _spans = Array.Empty<Span>();

    /// <summary>
    /// 行単位で、交差する分類スパンの前景色を適用する。
    /// スパンは Offset 昇順なので、行開始位置を二分探索してその行の分だけ走査する
    /// （全スパンの線形走査を避け、行数×スパン数の重さを解消する）。
    /// </summary>
    protected override void ColorizeLine(DocumentLine line)
    {
        if (_spans.Length == 0) return;

        int lineStart = line.Offset;
        int lineEnd   = line.EndOffset;

        // Offset >= lineStart となる最初のスパンを二分探索する
        int idx = LowerBound(lineStart);
        // 直前のスパンが行内へ伸びている可能性があるので 1 つ戻る
        if (idx > 0) idx--;

        for (int i = idx; i < _spans.Length; i++)
        {
            var s = _spans[i];
            // これ以降のスパンは行の右端より後にあるため打ち切る
            if (s.Offset >= lineEnd) break;

            int end = s.Offset + s.Length;
            if (end <= lineStart) continue;   // まだ行に届いていない

            int cs = Math.Max(s.Offset, lineStart);
            int ce = Math.Min(end, lineEnd);
            if (cs >= ce) continue;

            var brush = s.Brush;
            ChangeLinePart(cs, ce, el => el.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    /// <summary>_spans 中で Offset >= value となる最初の位置を返す（二分探索）。</summary>
    private int LowerBound(int value)
    {
        int lo = 0, hi = _spans.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_spans[mid].Offset < value) lo = mid + 1;
            else                            hi = mid;
        }
        return lo;
    }
}
