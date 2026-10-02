namespace SEED.UI;

// ============================================================
//  FocusRingMath.cs — フォーカスの枠（focus ring）の置き場・大きさ・角丸（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  枠は部品の矩形（画面の画素）を、枠の親のキャンバス（UiNavigator のノード）の単位へ写し、間（size.focus_ring_gap）と
//  線の太さ（size.focus_ring_width）の分だけ外へ広げた角丸の矩形。Sprite の縁は形の内側に引かれるので、線の内側の辺は
//  部品の辺から「間」だけ離れる。角丸は部品の角丸 + 間 + 太さ（同心の角丸）、部品が角丸でなければ radius.focus_ring。
//  座標: 画面の画素（左上が原点・Y 下向き）→ 親のキャンバスの単位（親の左上が原点）= (画素 − 親の左上の画素) ÷ 1 単位の画素数。
//  エンジンに触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>フォーカスの枠の置き場の計算（純粋な計算）。</summary>
public static class FocusRingMath
{
    /// <summary>
    /// 1 単位の画素数（親の画面の幅 ÷ 親のレイアウトの幅）。測れていなければ fallback（Screen.DpScale など）、それも使えなければ 1。
    /// </summary>
    /// <param name="rectPixels">親の画面の上の幅（画素）。</param>
    /// <param name="sizeUnits">親のレイアウトの幅（キャンバスの単位）。</param>
    /// <param name="fallback">測れないときの値。</param>
    public static float PxPerUnit(float rectPixels, float sizeUnits, float fallback)
    {
        if (float.IsFinite(rectPixels) && float.IsFinite(sizeUnits) && rectPixels > 0f && sizeUnits > 0f) return rectPixels / sizeUnits;
        return float.IsFinite(fallback) && fallback > 0f ? fallback : 1f;
    }

    /// <summary>
    /// 枠の矩形（親のキャンバスの単位。左上と大きさ）。部品の矩形を親の単位へ写し、間 + 太さの分だけ外へ広げる。
    /// </summary>
    /// <param name="target">部品の矩形（画面の画素）。</param>
    /// <param name="parentOrigin">親の左上（画面の画素）。</param>
    /// <param name="pxPerUnit">1 単位の画素数（正）。</param>
    /// <param name="gap">部品の辺と線の内側の間（単位）。</param>
    /// <param name="width">線の太さ（単位）。</param>
    public static Rect RingRect(Rect target, Vector2 parentOrigin, float pxPerUnit, float gap, float width)
    {
        float scale = float.IsFinite(pxPerUnit) && pxPerUnit > 0f ? pxPerUnit : 1f;
        float outset = NonNegative(gap) + NonNegative(width);
        float x = (target.x - parentOrigin.x) / scale - outset;
        float y = (target.y - parentOrigin.y) / scale - outset;
        float w = target.width / scale + outset * 2f;
        float h = target.height / scale + outset * 2f;
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// 枠の角丸（部品の角丸が正なら同心の角丸 = 部品の角丸 + 間 + 太さ、そうでなければ既定の角丸）。
    /// </summary>
    /// <param name="targetRadius">部品の角丸（単位。0 以下・NaN = 角丸なし）。</param>
    /// <param name="gap">間（単位）。</param>
    /// <param name="width">線の太さ（単位）。</param>
    /// <param name="fallback">部品が角丸でないときの角丸（radius.focus_ring）。</param>
    public static float RingRadius(float targetRadius, float gap, float width, float fallback)
        => float.IsFinite(targetRadius) && targetRadius > 0f ? targetRadius + NonNegative(gap) + NonNegative(width) : NonNegative(fallback);

    /// <summary>0 以上の有限の値（負・NaN は 0）。</summary>
    private static float NonNegative(float v) => float.IsFinite(v) && v > 0f ? v : 0f;
}
