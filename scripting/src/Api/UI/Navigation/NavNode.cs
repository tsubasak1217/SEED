namespace SEED.UI;

// ============================================================
//  NavNode.cs — 画面の組み立てが画面・幕・板のノードへ当てる小道具（W2-7）
//
//  CanvasLayoutItem の実行中だけの見た目の上書き（TranslateFraction・Translate・LayerBias・VisualScale。runtime の
//  canvas_layout の translate_placement・layer_bias・scale_placement）と、Sprite の濃さを 1 か所で当てる。
//  ノードに該当のコンポーネントが無ければ何もしない（プレハブの作りが違っても落ちない）。
//  書き込みは前の値と同じなら FFI を呼ばない（止まっている画面は何も書かない）。
// ============================================================

/// <summary>ノードへ見た目を当てる小道具。</summary>
internal static class NavNode
{
    /// <summary>自分の大きさに対するずらしを当てる。</summary>
    public static void SetFraction(GameObject node, Vector2 fraction)
    {
        if (node.GetComponent<CanvasLayoutItem>() is { } item && item.TranslateFraction != fraction) item.TranslateFraction = fraction;
    }

    /// <summary>キャンバスの単位のずらしを当てる。</summary>
    public static void SetTranslate(GameObject node, Vector2 translate)
    {
        if (node.GetComponent<CanvasLayoutItem>() is { } item && item.Translate != translate) item.Translate = translate;
    }

    /// <summary>レイヤーの底上げを当てる。</summary>
    public static void SetBias(GameObject node, int bias)
    {
        if (node.GetComponent<CanvasLayoutItem>() is { } item && item.LayerBias != bias) item.LayerBias = bias;
    }

    /// <summary>
    /// 見た目の倍率を当てる（自分の矩形の中心の周り。子孫も一体で縮む。W2 の手直し 3b の予測型の戻るのプレビューと、
    /// P2-1 のダイアログの出入り。保存される CanvasTransform.Scale は入れ子のキャンバスの子が左上へ寄るので、出入りの動きには使わない）。
    /// </summary>
    public static void SetVisualScale(GameObject node, Vector2 scale)
    {
        if (node.GetComponent<CanvasLayoutItem>() is { } item && item.VisualScale != scale) item.VisualScale = scale;
    }

    /// <summary>見せる・隠す（前の値と同じなら書かない）。</summary>
    public static void SetVisible(GameObject node, bool visible)
    {
        if (node.IsValid && node.Visible != visible) node.Visible = visible;
    }

    /// <summary>Sprite の色（濃さを掛けて）を当てる。</summary>
    public static void SetSpriteColor(GameObject node, Color color)
    {
        if (node.GetComponent<Sprite>() is { } sprite && !sprite.Color.Equals(color)) sprite.Color = color;
    }

    /// <summary>作ったノードができあがったか（2D のアクターは構築のとき CanvasTransform を持つ）。</summary>
    public static bool IsBuilt(GameObject node) => node.IsValid && node.GetComponent<CanvasTransform>() is not null;

    /// <summary>祖先をたどる回数の上限（壊れた木で回り続けない）。</summary>
    private const int MaxAncestorHops = 64;

    /// <summary>ノード自身のレイヤーの底上げ（CanvasLayoutItem が無ければ 0。2026-10-02）。</summary>
    public static int OwnBias(GameObject node) =>
        node.IsValid && node.GetComponent<CanvasLayoutItem>() is { } item ? item.LayerBias : 0;

    /// <summary>ノードの実効の底上げ（自分と祖先の底上げの和。描画・当たり判定が使う足し方と同じ。2026-10-02）。</summary>
    public static int EffectiveBias(GameObject node)
    {
        int sum = 0;
        var current = node;
        for (int i = 0; i < MaxAncestorHops && current.IsValid; i++)
        {
            sum += OwnBias(current);
            current = current.Parent;
        }
        return sum;
    }

    /// <summary>ノードの祖先の底上げの和（自分を含まない。2026-10-02）。</summary>
    public static int AncestorsBias(GameObject node) => node.IsValid ? EffectiveBias(node.Parent) : 0;

    /// <summary>エンティティの鍵（登録簿の辞書の鍵）。</summary>
    public static (uint, uint) Key(GameObject node) => (node.Entity.Index, node.Entity.Generation);
}
