using System;

namespace SEED.UI;

// ============================================================
//  PopupCardMath.cs — 中央のポップアップの札の大きさ（2026-10-02。lane3。純粋な計算。docs/ui_navigation.md §3.6）
//
//  幅  = 指定（> 0）か「覆う領域の幅 − 余白 × 2」を、上限（size.popup_max_width。0 以下なら無し）と領域の幅で切る（0 未満にしない）
//  高さ = 指定の中身の高さ（> 0）か中身が知らせる高さに内側の余白 × 2 を足したものを、上限で切る。中身の高さが分からない（0 以下）なら上限。
//  上限 = min(安全領域の高さ × ratio.popup_max_height, 安全領域の高さ − 余白 × 2)（0 未満にしない）。切ったら Clipped（中身は自分でスクロールする）。
//  札は安全領域の真ん中に置く（ポップアップの根の縦の並びの上下の余白を安全領域にする。置くのは部品）。
// ============================================================

/// <summary>札の大きさ（幅・高さ）と、上限で切ったか。</summary>
/// <param name="Width">幅（キャンバスの単位）。</param>
/// <param name="Height">高さ（キャンバスの単位。内側の余白を含む）。</param>
/// <param name="Clipped">中身の高さが上限を超えて切ったか。</param>
public readonly record struct PopupCardSize(float Width, float Height, bool Clipped);

/// <summary>中央のポップアップの札の大きさの計算。</summary>
public static class PopupCardMath
{
    /// <summary>高さの割合が正しくないときの割合（安全領域いっぱい）。</summary>
    private const float FullRatio = 1f;

    /// <summary>
    /// 札の大きさを決める。
    /// </summary>
    /// <param name="areaWidth">覆う領域（ポップアップの根）の幅。</param>
    /// <param name="areaHeight">覆う領域の高さ。</param>
    /// <param name="topInset">上の安全領域（キャンバスの単位）。</param>
    /// <param name="bottomInset">下の安全領域。</param>
    /// <param name="margin">札と画面の端の余白（size.popup_margin）。</param>
    /// <param name="maxWidth">幅の上限（size.popup_max_width。0 以下なら無し）。</param>
    /// <param name="maxHeightRatio">安全領域の高さに対する高さの上限の割合（ratio.popup_max_height。0〜1 の外なら 1）。</param>
    /// <param name="padding">札の内側の余白（size.popup_padding。上下左右）。</param>
    /// <param name="requestedWidth">指定の幅（PopupOptions.Width。0 以下なら決める）。</param>
    /// <param name="contentHeight">中身の高さ（指定か中身が知らせる値。0 以下なら分からない＝上限の高さ）。</param>
    /// <returns>札の大きさ。</returns>
    public static PopupCardSize Card(float areaWidth, float areaHeight, float topInset, float bottomInset, float margin,
        float maxWidth, float maxHeightRatio, float padding, float requestedWidth, float contentHeight)
    {
        float area = Positive(areaWidth);
        float m = Positive(margin);
        float pad = Positive(padding);

        // 幅: 指定か領域 − 余白 × 2（上限と領域の幅で切る）
        float width = requestedWidth > 0f && float.IsFinite(requestedWidth) ? requestedWidth : area - m * 2f;
        if (maxWidth > 0f && float.IsFinite(maxWidth)) width = Math.Min(width, maxWidth);
        width = Math.Clamp(width, 0f, area);

        // 高さ: 中身 + 余白 × 2（分からなければ上限）を上限で切る
        float maxHeight = MaxHeight(areaHeight, topInset, bottomInset, m, maxHeightRatio);
        bool known = contentHeight > 0f && float.IsFinite(contentHeight);
        float natural = known ? contentHeight + pad * 2f : maxHeight;
        bool clipped = natural > maxHeight;
        return new PopupCardSize(width, Math.Min(natural, maxHeight), known && clipped);
    }

    /// <summary>札の高さの上限（安全領域の高さ × 割合 と 安全領域の高さ − 余白 × 2 の小さい方。0 未満にしない）。</summary>
    /// <param name="areaHeight">覆う領域の高さ。</param>
    /// <param name="topInset">上の安全領域。</param>
    /// <param name="bottomInset">下の安全領域。</param>
    /// <param name="margin">札と画面の端の余白。</param>
    /// <param name="maxHeightRatio">安全領域の高さに対する割合（0〜1 の外なら 1）。</param>
    public static float MaxHeight(float areaHeight, float topInset, float bottomInset, float margin, float maxHeightRatio)
    {
        float safe = Math.Max(0f, Positive(areaHeight) - Positive(topInset) - Positive(bottomInset));
        float ratio = maxHeightRatio > 0f && maxHeightRatio <= FullRatio ? maxHeightRatio : FullRatio;
        return Math.Max(0f, Math.Min(safe * ratio, safe - Positive(margin) * 2f));
    }

    /// <summary>有限の正の値（それ以外は 0）。</summary>
    private static float Positive(float value) => float.IsFinite(value) && value > 0f ? value : 0f;
}
