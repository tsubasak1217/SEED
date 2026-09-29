namespace SEED.UI;

// ============================================================
//  ChartSizing.cs — グラフの大きさの読み方と、最初のレイアウトを待つ判定（W2 の手直し P2-4。docs/ui_charts.md §1・§2・§11）
//
//  【大きさ】グラフのノードの大きさは、レイアウトの表にあれば CanvasTransform.LayoutSize（コンテナ・親に合わせる〈fill〉で
//  伸ばされた大きさ＝描かれる背景のスプライトの大きさ。前のフレームの描画の値）、無ければ Sprite の幅・高さ（従来の読み方）。
//  LayoutSize が壊れた値（有限でない・負）のときも Sprite へ落とす。
//  【最初のレイアウトを待つ】表は描画で作るので、グラフの最初のフレーム（Play の開始・生成した直後）は HasLayout = false。
//  そのまま Sprite の大きさで描くと、コンテナに伸ばされた・縮められたグラフは 1 フレームだけ背景と中身の大きさが食い違う
//  （背景より右へはみ出す・右が空く）。そこで、レイアウトを 1 度読めるまで描かずに待つ（位置の計算は Sprite の大きさで進めてよい）。
//  3D ワールドキャンバスの下など HasLayout がずっと false の所でも描けるよう、待つのは上限のフレーム数まで（超えたら Sprite の大きさで描く）。
//  1 度描き始めたら待ちには戻らない（途中で HasLayout が false になっても Sprite の大きさで描き続ける）。
// ============================================================

/// <summary>グラフの大きさの読み方（純粋な計算）。</summary>
public static class ChartSizing
{
    /// <summary>
    /// 最初のレイアウトを待つフレームの上限。レイアウトの表は描画で作るので、普通は次のフレーム（待ち 1 フレーム）で読める。
    /// 生成したフレームと表に載るフレームのずれ（生成したフレームの表には載らない）を見込んで 3。これを超えて読めなければ
    /// 3D ワールドキャンバスの下などの表に載らない所とみなし、Sprite の大きさで描く（そこでは最初の 3 フレームだけ描かない）。
    /// </summary>
    public const int DefaultMaxLayoutWaitFrames = 3;

    /// <summary>
    /// 使う大きさ: レイアウトの表にあり（<paramref name="hasLayout"/>）値が使えればレイアウトの大きさ、それ以外は Sprite の大きさ。
    /// </summary>
    /// <param name="hasLayout">CanvasTransform.HasLayout（前のフレームの描画の表にノードがあったか）。</param>
    /// <param name="layoutSize">CanvasTransform.LayoutSize（ノードのキャンバスの単位）。</param>
    /// <param name="spriteSize">グラフのノードの Sprite の幅・高さ。</param>
    public static Vector2 Choose(bool hasLayout, Vector2 layoutSize, Vector2 spriteSize)
        => hasLayout && IsUsable(layoutSize) ? layoutSize : spriteSize;

    /// <summary>レイアウトの大きさとして使える値か（有限・負でない）。</summary>
    public static bool IsUsable(Vector2 size)
        => float.IsFinite(size.x) && float.IsFinite(size.y) && size.x >= 0f && size.y >= 0f;
}

/// <summary>
/// 最初のレイアウトを待つ判定（フレームごとに <see cref="Step"/> を 1 回呼ぶ。状態を持つ値）。
/// レイアウトを 1 度読めたか、読めないフレームが上限を超えたら描いてよい（以後は戻らない）。
/// </summary>
public struct ChartLayoutWait
{
    /// <summary>レイアウトを読めなかったフレームの数（描いてよくなるまで）。</summary>
    private int _missed;

    /// <summary>描いてよい。</summary>
    public bool Ready { get; private set; }

    /// <summary>
    /// 1 フレーム進める。<paramref name="hasLayout"/> なら描いてよくなる。読めないフレームが <paramref name="maxFrames"/> を超えたら
    /// （最大 maxFrames フレーム待った次のフレームで）描いてよくなる。0 以下なら待たない。
    /// </summary>
    /// <returns>描いてよいか。</returns>
    public bool Step(bool hasLayout, int maxFrames)
    {
        if (Ready) return true;
        if (hasLayout || ++_missed > maxFrames) Ready = true;
        return Ready;
    }
}
