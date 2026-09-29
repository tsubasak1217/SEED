namespace SEED.UI;

// ============================================================
//  DialogLayout.cs — 文字の行の数・高さ・幅の見積もり（W2-7。W2 の手直し P2-1 でエンジンの規則と送り幅に合わせた。純粋な計算）
//
//  文字の寸法を測る API（Text.Measure）は W2-6c なので、それまではここで見積もる。使う所:
//    - Dialog: 本文の行の数 → 本文の枠の高さ・札の高さ（DialogMetrics）、ボタンの文字の幅 → ボタンの幅
//    - グラフの吹き出し（ChartView.PlaceTooltip）: 文字の幅・行の数 → 吹き出しの札の大きさ
//  見積もりの作り（2026-09-29。それまでは全角 1 em・半角 0.55 em の 2 種類で、1 行の本文を 2 行と見積もっていた。roadmap §3.9.2 (a)）:
//    - 1 文字の送り幅: 組み込みの書体の表（BuiltInFontAdvance。SEED の文字の大きさ 1 = 書体の ascent − descent なので全角は 0.7168 em）
//    - 折り返し: エンジンと同じ規則（TextWrapEstimate。語のまとまり・空白のぶら下げ・日本語は 1 文字ずつ・禁則・強制分割）
//    - 収まりの判定だけ FitSlackEm の余裕を枠から引く（浮動小数の丸めの差で行が足りなくなる〈本文がボタンに重なる〉より、1 行余る方へ倒す）
//    - 行の高さ: 大きさ × LineHeightEm。Dialog は題と本文の Text.LineSpacing をこの値にして、描く行送りと見積もりを一致させる
//      （エンジンは枠ありの Text で行送り = 大きさ × 行間、行送りの余白を行の上下へ半分ずつ配る。text_layout.rs の resolve_layout）
//  テーマの font.family でほかの書体を当てたときは送り幅が違うので、見積もりはずれる（docs/ui_navigation.md §13）。
// ============================================================

/// <summary>文字の行の数・高さ・幅の見積もり（Text.Measure〈W2-6c〉までの仮。組み込みの書体とエンジンの折り返しの規則に合わせてある）。</summary>
public static class DialogLayout
{
    /// <summary>
    /// 行の高さ（文字の大きさに対する割合）。Dialog は題と本文の Text.LineSpacing をこの値にする（描く行送り = 見積もりの行の高さ）。
    /// 1.4 は Material 3 の本文の行の高さ（body medium 14 / 20 ≒ 1.43）に近い値。
    /// </summary>
    public const float LineHeightEm = 1.4f;

    /// <summary>
    /// 収まりの判定で枠の幅から引く余裕（文字の大きさに対する割合。大きさ 16 で 0.016）。エンジン（f32 の ab_glyph）と
    /// この見積もりの浮動小数の丸めの差で「エンジンは折り返すのに見積もりは収まる」（行が足りない）が起きないよう、
    /// 枠にぎりぎりの行は折れる側へ倒す（そのときは 1 行余る）。
    /// </summary>
    private const float FitSlackEm = 1e-3f;

    /// <summary>折り返さないときの枠の幅（エンジンは 0 以下を「折り返さない＝改行だけで分ける」とみなす）。</summary>
    public const float NoWrapWidth = 0f;

    /// <summary>
    /// 行の数の見積もり（改行と、枠の幅での折り返し。エンジンの規則）。空文字は 0 行（描かれない）。
    /// 大きさが 0 以下・枠の幅が 0 以下（<see cref="NoWrapWidth"/>）・有限でないなら折り返さない（改行だけで数える）。
    /// </summary>
    /// <param name="text">文字列。</param>
    /// <param name="fontSize">文字の大きさ。</param>
    /// <param name="boxWidth">枠の幅（文字の大きさと同じ単位）。</param>
    public static int EstimateLines(string text, float fontSize, float boxWidth)
        => TextWrapEstimate.CountLines(text, fontSize, boxWidth, Slack(fontSize), BuiltInFontAdvance.Em);

    /// <summary>行の文字列の見積もり（行末の空白を除く。検算・診断用）。</summary>
    /// <param name="text">文字列。</param>
    /// <param name="fontSize">文字の大きさ。</param>
    /// <param name="boxWidth">枠の幅。</param>
    public static System.Collections.Generic.IReadOnlyList<string> EstimateLineTexts(string text, float fontSize, float boxWidth)
        => TextWrapEstimate.Lines(text, fontSize, boxWidth, Slack(fontSize), BuiltInFontAdvance.Em);

    /// <summary>高さの見積もり（行の数 × 大きさ × <see cref="LineHeightEm"/>）。</summary>
    /// <param name="text">文字列。</param>
    /// <param name="fontSize">文字の大きさ。</param>
    /// <param name="boxWidth">枠の幅（<see cref="NoWrapWidth"/> なら折り返さない）。</param>
    public static float EstimateHeight(string text, float fontSize, float boxWidth)
        => fontSize > 0f ? EstimateLines(text, fontSize, boxWidth) * fontSize * LineHeightEm : 0f;

    /// <summary>
    /// 1 行の文字の幅の見積もり（ボタンの幅・吹き出しの幅を文字に合わせる）。改行を含むなら最も広い行の幅。
    /// </summary>
    /// <param name="text">文字列。</param>
    /// <param name="fontSize">文字の大きさ。</param>
    public static float EstimateWidth(string text, float fontSize)
        => TextWrapEstimate.WidestParagraph(text, fontSize, BuiltInFontAdvance.Em);

    /// <summary>収まりの判定の余裕（大きさ × FitSlackEm。大きさが有限の正でなければ 0）。</summary>
    private static float Slack(float fontSize) => fontSize > 0f && float.IsFinite(fontSize) ? fontSize * FitSlackEm : 0f;
}
