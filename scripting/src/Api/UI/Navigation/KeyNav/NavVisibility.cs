namespace SEED.UI;

// ============================================================
//  NavVisibility.cs — 方向キーで移れる位置に部品があるか（見えていて・切り抜きの外でない。2026-10-03。L3-6）
//
//  UiVisibility.IsVisibleInView（スピナーの描き続けの判定）と違い、スクロールの窓（CanvasClip と CanvasScroll を両方持つ祖先）の
//  外にあるだけの部品は「移れる」とみなす（移ったらスクロールして見せる。一覧を方向キーで下まで送れる）。決め方:
//    1. 自分と祖先がすべて表示（Visible）でなければ移れない（隠した画面・選んでいないタブ・閉じた枠）
//    2. 祖先を内から外へたどり、ふつうの切り抜き（スクロールでない CanvasClip）は窓を狭める
//    3. スクロールの窓に当たったら、そこまでの窓と自分の矩形が重なるかを確かめ（畳んだ枠の中は移れない）、
//       以後は「スクロールの窓そのものが見えているか」を問う（窓の外の中身はスクロールで見せられる）
//    4. 最後に画面と重なるかを確かめる
//  矩形は前のフレームの描画のレイアウト（CanvasTransform.LayoutRect）。測れていない・面積の無いノードは移れない
//  （作った直後の部品は次のフレームから候補になる）。重なりの判定は ClipVisibility（純粋な計算）。
// ============================================================

/// <summary>方向キーで移れる位置に部品があるかの判定。</summary>
internal static class NavVisibility
{
    /// <summary>
    /// ノードの画面の矩形（測れていれば true）。面積の無い（幅か高さが 0 以下）矩形は測れていないとみなす。
    /// </summary>
    /// <param name="node">ノード。</param>
    /// <param name="rect">画面の画素の矩形。</param>
    public static bool TryGetRect(GameObject node, out Rect rect)
    {
        rect = Rect.Zero;
        if (!node.IsValid || node.GetComponent<CanvasTransform>() is not { HasLayout: true } t) return false;
        rect = t.LayoutRect;
        return NavigationMath.IsUsable(rect) && rect.width > 0f && rect.height > 0f;
    }

    /// <summary>
    /// 方向キーで移れる位置にあるか（表示されていて、スクロール以外の切り抜きと画面の外でない。スクロールで隠れているだけなら true）。
    /// </summary>
    /// <param name="node">部品のノード。</param>
    public static bool IsReachable(GameObject node)
    {
        if (!TryGetRect(node, out var rect)) return false;
        // 今確かめている矩形（最初は部品、スクロールの窓を越えたら窓）と、それを狭める切り抜きの窓（null = まだ狭めていない）
        var subject = rect;
        Rect? window = null;
        var current = node;
        for (int depth = 0; depth < UiVisibility.MaxDepth && current.IsValid; depth++)
        {
            if (!current.Visible) return false;
            if (depth > 0 && ClipOf(current, out var clip, out bool isScroll))
            {
                if (isScroll)
                {
                    // スクロールの窓: 中の切り抜きで隠れていないかを確かめ、以後は窓そのものが見えるかを問う
                    if (window is { } w && !ClipVisibility.Overlaps(subject, w)) return false;
                    subject = clip;
                    window = null;
                }
                else
                {
                    window = window is { } w ? ClipVisibility.Narrow(w, clip) : clip;
                }
            }
            current = current.Parent;
        }
        var screen = ClipVisibility.ScreenRect(Screen.Width, Screen.Height);
        var final = window is { } narrowed ? ClipVisibility.Narrow(screen, narrowed) : screen;
        return ClipVisibility.Overlaps(subject, final);
    }

    /// <summary>
    /// 有効な切り抜き（CanvasClip.Enabled）と測れた矩形を持つか。持つならその矩形と、スクロールの窓（CanvasScroll も持つ）かを返す。
    /// </summary>
    private static bool ClipOf(GameObject node, out Rect clip, out bool isScroll)
    {
        clip = Rect.Zero;
        isScroll = false;
        if (node.GetComponent<CanvasClip>() is not { Enabled: true }) return false;
        if (node.GetComponent<CanvasTransform>() is not { HasLayout: true } t) return false;
        clip = t.LayoutRect;
        if (!ClipVisibility.IsUsableClip(clip)) return false;
        isScroll = node.GetComponent<CanvasScroll>() is { Enabled: true };
        return true;
    }
}
