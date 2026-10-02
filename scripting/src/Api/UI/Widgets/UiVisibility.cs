namespace SEED.UI;

// ============================================================
//  UiVisibility.cs — ノードが祖先まで含めて表示され、画面と祖先の切り抜きの中に見えているか（2026-10-02。切り抜きは 2026-10-03）
//
//  GameObject.Visible は自分のフラグだけ（Unity の activeSelf と同じ流儀）なので、祖先が非表示（隠した画面・閉じたタブ・
//  作り置きの置き場）の部品も true を返す。動き続ける部品（ProgressSpinner）が、見えていない間に描き続けを頼まない
//  （on_demand の電池の無駄にならない）ように、祖先をたどって確かめる。深さに上限を置く（壊れた木で回り続けない）。
//  2026-10-03: 祖先の切り抜き（CanvasClip。スクロールの窓・ダイアログの本文の窓など）の外にあるだけで見えない部品も見分ける
//  （backlog の lane2 残件 (3)。決め方は ClipVisibility。矩形は前のフレームの描画のレイアウト＝CanvasTransform.LayoutRect）。
// ============================================================

/// <summary>ノードが祖先まで含めて表示され、画面と祖先の切り抜きの中に見えているか。</summary>
internal static class UiVisibility
{
    /// <summary>たどる深さの上限（UI の木はふつう 20 段に届かない。超えたら表示とみなす）。</summary>
    public const int MaxDepth = 64;

    /// <summary>自分と祖先がすべて表示（Visible）か。無効なノードは表示でない。</summary>
    /// <param name="node">ノード。</param>
    public static bool IsShownInHierarchy(GameObject node)
    {
        if (!node.IsValid) return false;
        var current = node;
        for (int depth = 0; depth < MaxDepth && current.IsValid; depth++)
        {
            if (!current.Visible) return false;
            current = current.Parent;
        }
        return true;
    }

    /// <summary>
    /// 自分と祖先がすべて表示で、ノードの矩形（前のフレームの描画のレイアウト。画面の画素）が「画面 ∩ 祖先の切り抜き」と重なるか
    /// （2026-10-03。祖先を 1 回たどって表示のフラグと切り抜きを見る）。スクロールで画面の外・窓の外へ出た部品は描き続けを頼まない。
    /// まだレイアウトが無い（作った直後）ノードは重なるとみなす（最初のフレームを描かせる）。測れていない・面積の無い切り抜きは数えない
    /// （見えるほうへ倒す。ClipVisibility）。自分の切り抜きは自分を切らない（エンジンも子孫だけを切る）ので祖先だけを見る。
    /// </summary>
    /// <param name="node">ノード。</param>
    public static bool IsVisibleInView(GameObject node)
    {
        if (!node.IsValid) return false;
        var window = ClipVisibility.ScreenRect(Screen.Width, Screen.Height);
        var current = node;
        for (int depth = 0; depth < MaxDepth && current.IsValid; depth++)
        {
            if (!current.Visible) return false;
            // 祖先の有効な切り抜きで窓を狭める（積。いちばん内側から外へ重ねても同じ）
            if (depth > 0 && ClipRectOf(current) is { } clip) window = ClipVisibility.Narrow(window, clip);
            current = current.Parent;
        }
        if (node.GetComponent<CanvasTransform>() is not { HasLayout: true } t) return true;
        return ClipVisibility.Overlaps(t.LayoutRect, window);
    }

    /// <summary>有効な切り抜き（CanvasClip.Enabled）を持ち、レイアウトのあるノードの矩形（画面の画素。無ければ null）。</summary>
    /// <param name="node">ノード。</param>
    private static Rect? ClipRectOf(GameObject node)
    {
        if (node.GetComponent<CanvasClip>() is not { Enabled: true }) return null;
        if (node.GetComponent<CanvasTransform>() is not { HasLayout: true } t) return null;
        return t.LayoutRect;
    }
}
