namespace SEED.UI;

// ============================================================
//  NavScroll.cs — 方向キーで移った部品を、祖先のスクロールの窓の中へ見せる（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  祖先を内から外へたどり、スクロール（CanvasScroll と有効な CanvasClip を持つ窓）ごとに、部品の矩形を窓の中へ入れる量
//  （NavigationMath.RevealDelta。画面の画素）を求めて CanvasScroll.ScrollTo（既存の口。Flutter の animateTo と同じ easeInOut）で送る。
//  量は窓の 1 単位の画素数（窓の画面の高さ ÷ ViewportSize）で単位へ直し、スクロールの向き（縦・横・両方）の成分だけを使う
//  （KeyboardAvoidance と同じ写し方）。外側のスクロールには、内側で送った分だけずらした矩形を渡す（入れ子の一覧）。
//  余白は枠の分（size.focus_ring_gap + size.focus_ring_width）で、枠も窓の中に入る。
//  矩形は前のフレームの描画の値なので、動いている間に続けて押すと 1 フレーム分ずれることがある（次の押下で直る）。
// ============================================================

/// <summary>移った部品をスクロールの窓の中へ見せる。</summary>
internal static class NavScroll
{
    /// <summary>送る量がこれ未満（画素）なら動かさない（丸めの誤差で毎回 ScrollTo しない）。</summary>
    private const float MinScrollPixels = 0.5f;

    /// <summary>
    /// 部品を祖先のスクロールの窓の中へ見せる（スクロールの外の部品・測れていない部品は何もしない）。
    /// </summary>
    /// <param name="node">部品のノード。</param>
    /// <param name="marginUnits">窓の辺との余白（キャンバスの単位）。</param>
    /// <param name="duration">送る時間（秒。0 以下ならすぐ移す）。</param>
    public static void Reveal(GameObject node, float marginUnits, float duration)
    {
        if (!NavVisibility.TryGetRect(node, out var rect)) return;
        var current = node.Parent;
        for (int depth = 0; depth < UiVisibility.MaxDepth && current.IsValid; depth++)
        {
            if (current.GetComponent<CanvasScroll>() is { Enabled: true } scroll
                && current.GetComponent<CanvasClip>() is { Enabled: true }
                && current.GetComponent<CanvasTransform>() is { HasLayout: true } transform
                && scroll.HasMetrics)
            {
                var viewport = transform.LayoutRect;
                var size = scroll.ViewportSize;
                float pxPerUnitX = FocusRingMath.PxPerUnit(viewport.width, size.x, Screen.DpScale);
                float pxPerUnitY = FocusRingMath.PxPerUnit(viewport.height, size.y, Screen.DpScale);
                float marginPx = marginUnits * pxPerUnitY;
                var delta = NavigationMath.RevealDelta(rect, viewport, marginPx);
                // スクロールの向きの成分だけ使う
                var direction = scroll.Direction;
                float dx = direction == ScrollDirection.Vertical ? 0f : delta.x;
                float dy = direction == ScrollDirection.Horizontal ? 0f : delta.y;
                if (System.MathF.Abs(dx) >= MinScrollPixels || System.MathF.Abs(dy) >= MinScrollPixels)
                {
                    var position = scroll.Position;
                    var target = new Vector2(position.x + dx / pxPerUnitX, position.y + dy / pxPerUnitY);
                    if (duration > 0f) scroll.ScrollTo(target, duration);
                    else scroll.JumpTo(target);
                    Redraw.Request();
                    // 外側の窓には、中身が送られた後の位置（送った分だけ戻した矩形）を渡す
                    rect = new Rect(rect.x - dx, rect.y - dy, rect.width, rect.height);
                }
            }
            current = current.Parent;
        }
    }
}
