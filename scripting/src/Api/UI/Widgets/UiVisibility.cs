namespace SEED.UI;

// ============================================================
//  UiVisibility.cs — ノードが祖先まで含めて表示されているか（2026-10-02）
//
//  GameObject.Visible は自分のフラグだけ（Unity の activeSelf と同じ流儀）なので、祖先が非表示（隠した画面・閉じたタブ・
//  作り置きの置き場）の部品も true を返す。動き続ける部品（ProgressSpinner）が、見えていない間に描き続けを頼まない
//  （on_demand の電池の無駄にならない）ように、祖先をたどって確かめる。深さに上限を置く（壊れた木で回り続けない）。
// ============================================================

/// <summary>ノードが祖先まで含めて表示されているか。</summary>
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
    /// ノードの矩形（前のフレームの描画のレイアウト。画面の画素）が画面と重なるか（スクロールで画面の外へ出た部品は描き続けを頼まない）。
    /// まだレイアウトが無い（作った直後）ノードは重なるとみなす（最初のフレームを描かせる）。
    /// </summary>
    /// <param name="node">ノード。</param>
    public static bool IsOnScreen(GameObject node)
    {
        if (node.GetComponent<CanvasTransform>() is not { HasLayout: true } t) return true;
        var rect = t.LayoutRect;
        return rect.x < Screen.Width && rect.x + rect.width > 0f && rect.y < Screen.Height && rect.y + rect.height > 0f;
    }
}
