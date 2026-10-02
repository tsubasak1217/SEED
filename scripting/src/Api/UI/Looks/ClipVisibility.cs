using System;

namespace SEED.UI;

// ============================================================
//  ClipVisibility.cs — ノードが「画面と祖先の切り抜きの積」の中に見えるか（2026-10-03。純粋な計算。docs/ui_components.md §6）
//
//  動き続ける部品（ProgressSpinner）が、見えていない間に描き続け（Redraw.KeepAlive）を頼まないための判定。以前は画面との重なりだけを
//  見ていたので、スクロールの窓（CanvasClip・CanvasScroll）の外へ流れた・畳んだ枠の中で見えないだけの所でも回り続けた
//  （backlog の lane2 残件 (3)）。エンジンの切り抜き（canvas_layout/clip.rs）は CanvasClip を持つ祖先のレイアウトの矩形
//  （回転していれば 4 隅の外接矩形）の積で子孫を切るので、同じ矩形（CanvasTransform.LayoutRect。画面の画素）を順に重ねる:
//    窓 = 画面の矩形 ∩ 切り抜きの祖先の矩形 …（いちばん外から内へ。積なので順は問わない）
//    見える = ノードの矩形が窓と重なる（境目で接するだけは重ならない）
//  【見えるほうへ倒す】ノードの矩形が測れていない（作った直後でレイアウトが無い）なら見えるとみなす（最初のフレームを描かせる）。
//  切り抜きの矩形が測れていない・面積が無い（幅か高さが 0 以下・NaN）なら、その切り抜きは数えない（エンジンも Canvas も Sprite も無い
//  ノードの切り抜きは切らない。畳んで高さ 0 にした枠の中は回り続ける＝以前と同じ）。見えるのに止まる誤りより、見えないのに回る誤り
//  （電池の無駄。以前と同じ）を選ぶ。角丸・楕円の切り抜きも外接矩形で見る（角の外だけに見える部品は回り続ける）。
//  エンジンの API に触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>ノードが画面と祖先の切り抜きの積の中に見えるかの判定。</summary>
public static class ClipVisibility
{
    /// <summary>画面の矩形（左上が原点・画面の画素。負・NaN の大きさは 0）。</summary>
    /// <param name="width">画面の幅（画素）。</param>
    /// <param name="height">画面の高さ（画素）。</param>
    public static Rect ScreenRect(float width, float height)
        => new(0f, 0f, PositiveOrZero(width), PositiveOrZero(height));

    /// <summary>切り抜きとして数える矩形か（位置と大きさが有限で、幅と高さが正）。</summary>
    /// <param name="clip">切り抜きの祖先のレイアウトの矩形。</param>
    public static bool IsUsableClip(Rect clip)
        => float.IsFinite(clip.x) && float.IsFinite(clip.y) && float.IsFinite(clip.width) && float.IsFinite(clip.height)
           && clip.width > 0f && clip.height > 0f;

    /// <summary>
    /// 窓を切り抜きで狭める（積）。数えない切り抜き（<see cref="IsUsableClip"/> が false）なら窓のまま。重ならなければ大きさ 0 の窓。
    /// </summary>
    /// <param name="window">今の窓。</param>
    /// <param name="clip">切り抜きの矩形。</param>
    public static Rect Narrow(Rect window, Rect clip)
    {
        if (!IsUsableClip(clip)) return window;
        float left = Math.Max(window.XMin, clip.XMin);
        float top = Math.Max(window.YMin, clip.YMin);
        float right = Math.Min(window.XMax, clip.XMax);
        float bottom = Math.Min(window.YMax, clip.YMax);
        return new Rect(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
    }

    /// <summary>
    /// ノードの矩形が窓と重なるか（境目で接するだけは重ならない）。大きさ 0 の窓とは重ならない。
    /// 大きさ 0 のノード（点）は、窓の内側（境目を除く）にあれば重なる。
    /// </summary>
    /// <param name="node">ノードの矩形（画面の画素）。</param>
    /// <param name="window">窓。</param>
    public static bool Overlaps(Rect node, Rect window)
    {
        if (!(window.width > 0f) || !(window.height > 0f)) return false;
        return node.XMin < window.XMax && node.XMax > window.XMin && node.YMin < window.YMax && node.YMax > window.YMin;
    }

    /// <summary>
    /// ノードが見えるか（窓 = 画面 ∩ 数える切り抜きのすべて。ノードの矩形が測れていなければ見えるとみなす）。
    /// </summary>
    /// <param name="node">ノードの矩形（画面の画素）。</param>
    /// <param name="nodeHasLayout">ノードの矩形が測れているか（CanvasTransform.HasLayout）。</param>
    /// <param name="screen">画面の矩形（<see cref="ScreenRect"/>）。</param>
    /// <param name="clips">切り抜きの祖先の矩形（順は問わない）。</param>
    public static bool IsVisible(Rect node, bool nodeHasLayout, Rect screen, ReadOnlySpan<Rect> clips)
    {
        if (!nodeHasLayout) return true;
        var window = screen;
        foreach (var clip in clips) window = Narrow(window, clip);
        return Overlaps(node, window);
    }

    /// <summary>0 以上の有限の値（負・NaN・無限は 0）。</summary>
    private static float PositiveOrZero(float v) => float.IsFinite(v) && v > 0f ? v : 0f;
}
