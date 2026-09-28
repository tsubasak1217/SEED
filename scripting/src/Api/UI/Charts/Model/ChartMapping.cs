namespace SEED.UI;

// ============================================================
//  ChartMapping.cs — 値 ⇔ 描く位置の変換（W2-8。docs/ui_charts.md §3）
//
//  【空間】描く位置は「描く面（Plot）の左上が原点・Y 下向き・ノードの単位（dp のキャンバスなら dp）」。
//  X の値は左 → 右、Y の値は下 → 上（大きいほど上。InvertY で逆＝大きいほど下）。
//  横の棒（BarOrientation.Horizontal）は呼び出し側が X（列）を縦・値を横に置くので、この変換を 2 本（Across・Along）持つ
//  （BarChart）。見える範囲（パン・ズーム後）の X を渡すので、範囲の外の値は面の外の位置になる（切り抜きで切る）。
// ============================================================

/// <summary>値 ⇔ 描く位置の変換（純粋な値）。</summary>
public readonly struct ChartMapping
{
    /// <summary>X の範囲（見える範囲）。</summary>
    public readonly ChartRange X;
    /// <summary>Y の範囲。</summary>
    public readonly ChartRange Y;
    /// <summary>面の幅（描く空間の単位）。</summary>
    public readonly float Width;
    /// <summary>面の高さ。</summary>
    public readonly float Height;
    /// <summary>Y の値が大きいほど下（既定は上）。</summary>
    public readonly bool InvertY;

    /// <summary>変換を作る（面の左上が原点）。</summary>
    public ChartMapping(ChartRange x, ChartRange y, float width, float height, bool invertY = false)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        InvertY = invertY;
    }

    /// <summary>X の値 → 横の位置。</summary>
    public float XToLocal(double x) => (float)(X.Fraction(x) * Width);

    /// <summary>Y の値 → 縦の位置（Y 下向き）。</summary>
    public float YToLocal(double y)
    {
        float f = (float)(Y.Fraction(y) * Height);
        return InvertY ? f : Height - f;
    }

    /// <summary>値 → 位置。</summary>
    public Vector2 ToLocal(double x, double y) => new(XToLocal(x), YToLocal(y));

    /// <summary>横の位置 → X の値。</summary>
    public double LocalToX(float localX) => X.Lerp(Width > 0 ? localX / Width : 0);

    /// <summary>縦の位置 → Y の値。</summary>
    public double LocalToY(float localY)
    {
        double f = Height > 0 ? localY / Height : 0;
        return Y.Lerp(InvertY ? f : 1.0 - f);
    }

    /// <summary>X の値の 1 単位が何 dp か（列の幅の見積もり）。</summary>
    public float XUnitLength => (float)(Width / X.SafeSpan);
}
