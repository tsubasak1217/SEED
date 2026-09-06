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
