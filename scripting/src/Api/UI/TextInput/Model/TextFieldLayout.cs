using System;

namespace SEED.UI;

// ============================================================
//  TextFieldLayout.cs — 入力欄の中の文字・カーソルの置き場の計算（W2-6b。純粋な計算。editor/tests/UiComponentsTests で検算）
//
//  座標は入力欄の「内側の枠」（左右の余白を除いた枠。左端が 0・キャンバスの単位）。文字の送り幅は SEED.TextMeasure の
//  カーソルの位置の並び（stops。長さ = 本文の長さ + 1。stops[i] = 添字 i の文字の前の端の x）で受ける。
//    - 文字の左端: 枠に収まれば揃え（左・中央）、はみ出すなら横のスクロールの分だけ左へ（Flutter の 1 行の TextField と同じ）
//    - スクロール: カーソルが枠の外へ出たときだけ、カーソルが枠の端に来るまで動かす（はみ出さなければ 0）
//    - タップ: タップの x にいちばん近いカーソルの位置（サロゲートの組の間は選ばない）
//    - 点滅: 周期ごとに見える・見えないを切り替える（打鍵・移動のたびに見える側から数え直す）
// ============================================================

/// <summary>入力欄の文字の揃え。</summary>
public enum TextFieldAlign
{
    /// <summary>左寄せ（文字の欄の既定）。</summary>
    Left = 0,
    /// <summary>中央（数字の欄。Wake or Pay の数値の欄）。</summary>
    Center = 1,
}

/// <summary>入力欄の中の置き場の計算。</summary>
public static class TextFieldLayout
{
    /// <summary>中央揃えの半分。</summary>
    private const float Half = 0.5f;
    /// <summary>点滅の「見える・見えない」の 2 つの相。</summary>
    private const int BlinkPhases = 2;

    /// <summary>
    /// 文字の左端の x（内側の枠の中）。収まれば揃えに従い、はみ出すなら <paramref name="scroll"/> だけ左へずらす。
    /// </summary>
    /// <param name="textWidth">文字の幅。</param>
    /// <param name="innerWidth">内側の枠の幅。</param>
    /// <param name="align">揃え。</param>
    /// <param name="scroll">横のスクロール（0 以上。はみ出すときだけ効く）。</param>
    public static float TextStartX(float textWidth, float innerWidth, TextFieldAlign align, float scroll)
    {
        if (textWidth <= innerWidth)
            return align == TextFieldAlign.Center ? (innerWidth - textWidth) * Half : 0f;
        return -Math.Max(0f, scroll);
    }

    /// <summary>
    /// カーソルが枠の中に見えるように横のスクロールを決める（はみ出さなければ 0。今のスクロールからの最小の動き）。
    /// </summary>
    /// <param name="currentScroll">今のスクロール。</param>
    /// <param name="caretX">カーソルの x（文字の左端から。stops[caret]）。</param>
    /// <param name="textWidth">文字の幅。</param>
    /// <param name="innerWidth">内側の枠の幅。</param>
    /// <param name="caretWidth">カーソルの太さ（末尾のカーソルも枠に収める）。</param>
    public static float ScrollToReveal(float currentScroll, float caretX, float textWidth, float innerWidth, float caretWidth)
    {
        if (!(innerWidth > 0f) || textWidth <= innerWidth) return 0f;
        float maxScroll = Math.Max(0f, textWidth + caretWidth - innerWidth);
        float scroll = Math.Clamp(float.IsFinite(currentScroll) ? currentScroll : 0f, 0f, maxScroll);
        if (caretX < scroll) scroll = caretX;
        else if (caretX + caretWidth > scroll + innerWidth) scroll = caretX + caretWidth - innerWidth;
        return Math.Clamp(scroll, 0f, maxScroll);
    }

    /// <summary>
    /// x（文字の左端から）にいちばん近いカーソルの位置の添字。サロゲートの組の間（下位のサロゲートの前）は選ばない。
    /// </summary>
    /// <param name="stops">カーソルの位置の並び（長さ = 本文の長さ + 1）。</param>
    /// <param name="x">x（文字の左端から。負なら先頭、幅より右なら末尾）。</param>
    /// <param name="text">本文（サロゲートの組を見分ける）。</param>
    public static int CaretIndexAt(ReadOnlySpan<float> stops, float x, string text)
    {
        int last = Math.Min(stops.Length - 1, text.Length);
        if (last <= 0) return 0;
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i <= last; i++)
        {
            // 下位のサロゲートの前の添字は組の途中なので飛ばす
            if (i > 0 && i < text.Length && char.IsLowSurrogate(text[i])) continue;
            float distance = Math.Abs(stops[i] - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>点滅のカーソルが見えているか（数え直してからの秒と周期。周期が 0 以下なら常に見える）。</summary>
    public static bool CaretVisible(float sinceReset, float period)
    {
        if (!(period > 0f) || !(sinceReset > 0f)) return true;
        return (int)(sinceReset / period) % BlinkPhases == 0;
    }

    /// <summary>次に見える・見えないが切り替わるまでの秒（周期が 0 以下なら切り替わらない＝∞）。</summary>
    public static float UntilBlinkToggle(float sinceReset, float period)
    {
        if (!(period > 0f)) return float.PositiveInfinity;
        float t = Math.Max(0f, sinceReset);
        return period - t % period;
    }
}
