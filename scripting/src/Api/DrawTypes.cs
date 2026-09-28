namespace SEED;

/// <summary>
/// <see cref="Draw"/> の図形をどう塗るか。
/// </summary>
public enum DrawMode
{
    /// <summary>内側を塗りつぶす。</summary>
    Fill = 0,
    /// <summary>輪郭を太さ thickness の線で描く。</summary>
    Outline = 1,
}

/// <summary>
/// <see cref="Draw"/> の図形の見た目の拡張（W2-8）。既定（<c>default</c>）は従来と同じ見た目
/// （アンチエイリアスの帯 = 描画空間の 1 単位・単色）。
///
/// <list type="bullet">
/// <item><see cref="PixelFeather"/>: アンチエイリアスの帯を<b>画面の 1 画素</b>にする。dp のキャンバス（1 単位 = 2.625 画素の端末など）でも
/// 縁がにじまない。線の太さは描画空間の単位（dp）のまま。3D ワールドキャンバスでは効かない。</item>
/// <item>線形のグラデーション（<see cref="WithLinearGradient"/>）: 始点で図形の色、終点で <see cref="GradientEnd"/>、軸の外は端の色。
/// 頂点の色で塗るので線形のグラデーションは正確（グラフの線の下の塗りを上から下へ薄くする）。</item>
/// </list>
/// </summary>
public readonly struct DrawStyle
{
    /// <summary>アンチエイリアスの帯を画面の 1 画素にする。</summary>
    public readonly bool PixelFeather;
    /// <summary>線形のグラデーションで塗るか。</summary>
    public readonly bool HasGradient;
    /// <summary>グラデーションの終点の色。</summary>
    public readonly Color GradientEnd;
    /// <summary>グラデーションの始点（点列と同じ空間）。</summary>
    public readonly Vector2 GradientFrom;
    /// <summary>グラデーションの終点（点列と同じ空間）。</summary>
    public readonly Vector2 GradientTo;

    /// <summary>すべてを指定して作る。</summary>
    public DrawStyle(bool pixelFeather, bool hasGradient, Color gradientEnd, Vector2 gradientFrom, Vector2 gradientTo)
    {
        PixelFeather = pixelFeather;
        HasGradient = hasGradient;
        GradientEnd = gradientEnd;
        GradientFrom = gradientFrom;
        GradientTo = gradientTo;
    }

    /// <summary>画面の 1 画素のアンチエイリアスだけを使う見た目（単色）。</summary>
    public static DrawStyle Crisp => new(true, false, default, default, default);

    /// <summary>この見た目に線形のグラデーション（始点 <paramref name="from"/> で図形の色 → 終点 <paramref name="to"/> で <paramref name="end"/>）を足す。</summary>
    public DrawStyle WithLinearGradient(Vector2 from, Vector2 to, Color end) => new(PixelFeather, true, end, from, to);
}

/// <summary>
/// 2D の SRT（スケール → 回転 → 平行移動）。
///
/// <see cref="Draw"/> の点列に対して「スケール → 回転 → 平行移動」の順で適用される。
/// 描画空間は Y 下向き（画面座標系）なので、回転角は<b>時計回りが正</b>。
/// </summary>
public readonly struct Transform2D
{
    /// <summary>平行移動（描画空間の px）。</summary>
    public readonly Vector2 Position;
    /// <summary>Z 軸まわりの回転（度・時計回りが正）。</summary>
    public readonly float RotationDegrees;
    /// <summary>XY スケール。</summary>
    public readonly Vector2 Scale;

    /// <summary>位置・回転・スケールをすべて指定する。</summary>
    public Transform2D(Vector2 position, float rotationDegrees, Vector2 scale)
    {
        Position = position;
        RotationDegrees = rotationDegrees;
        Scale = scale;
    }

    /// <summary>位置のみ指定（回転 0・スケール 1）。</summary>
    public Transform2D(Vector2 position) : this(position, 0f, new Vector2(1f, 1f)) { }

    /// <summary>位置と回転を指定（スケール 1）。</summary>
    public Transform2D(Vector2 position, float rotationDegrees)
        : this(position, rotationDegrees, new Vector2(1f, 1f)) { }

    /// <summary>何もしない SRT（原点・回転 0・スケール 1）。</summary>
    public static Transform2D Identity => new(new Vector2(0f, 0f), 0f, new Vector2(1f, 1f));
}
