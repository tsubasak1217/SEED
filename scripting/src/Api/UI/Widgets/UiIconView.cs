namespace SEED.UI;

// ============================================================
//  UiIconView.cs — アイコンの見た目（UiIconLook）を部品の子の Sprite へ当てる（2026-10-02）
//
//  見た目の決め方（画像か図形か・色・大きさ）は Looks/UiIcon.cs の純粋な計算。ここはエンジンの欄へ書くだけ:
//    画像 … TexturePath = 画像、形は矩形・縁なし・色 = 掛ける色
//    円   … 画像なし・楕円・色 = 塗り
//    輪   … 画像なし・楕円・塗りは透明・縁の太さと色
//    四角 … 画像なし・矩形の角丸・色 = 塗り
//  アイコンの指定が無ければノードを隠す。位置は呼ぶ側が決める（行の高さの真ん中など）。
// ============================================================

/// <summary>アイコンの見た目を Sprite へ当てる。</summary>
internal static class UiIconView
{
    /// <summary>
    /// アイコンのノードへ見た目を当てる（指定が無ければ隠す）。
    /// </summary>
    /// <param name="node">アイコンのノード（Sprite を持つ）。</param>
    /// <param name="look">見た目。</param>
    /// <param name="position">左上の位置（親のローカル。キャンバスの単位）。</param>
    public static void Apply(GameObject node, UiIconLook look, Vector2 position)
    {
        if (!node.IsValid) return;
        NavNode.SetVisible(node, look.Visible);
        if (!look.Visible || node.GetComponent<Sprite>() is not { } sprite) return;
        if (look.IsImage)
        {
            if (sprite.TexturePath != look.ImagePath) sprite.TexturePath = look.ImagePath;
            sprite.Shape = SpriteShapeKind.Rect;
            sprite.CornerRadius = 0f;
            sprite.BorderWidth = 0f;
            sprite.Color = look.Color;
        }
        else
        {
            if (sprite.TexturePath.Length > 0) sprite.TexturePath = string.Empty;
            bool ring = look.Shape == UiIconShape.Ring;
            sprite.Shape = look.Shape == UiIconShape.Square ? SpriteShapeKind.Rect : SpriteShapeKind.Ellipse;
            sprite.CornerRadius = look.CornerRadius;
            // 輪は塗りを透明にして縁だけ（縁の色は塗りと独立。W2-4 の形と塗り）
            sprite.Color = ring ? UiColorMath.Transparent : look.Color;
            sprite.BorderWidth = ring ? look.RingWidth : 0f;
            sprite.BorderColor = look.Color;
        }
        sprite.Size = new Vector2(look.Size, look.Size);
        if (node.GetComponent<CanvasTransform>() is { } t && t.Position != position) t.Position = position;
    }
}
