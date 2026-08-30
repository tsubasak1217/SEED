using System;
using System.Globalization;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// スプライトリグ編集で使う 2 次元ベクトル（画像ピクセル座標）。
///
/// WPF の <c>System.Windows.Point</c> を使わないのは、メッシュ生成アルゴリズム一式を
/// UI から切り離し、単体テスト（editor/tests/SpriteRigTests）から直接呼べるようにするため。
/// 座標系は <c>.sprite_mesh</c> と同じ「左上原点・+X 右・+Y 下」。
/// </summary>
public readonly struct Vec2 : IEquatable<Vec2>
{
    /// <summary>X 座標（画像ピクセル・右が正）。</summary>
    public readonly double X;

    /// <summary>Y 座標（画像ピクセル・下が正）。</summary>
    public readonly double Y;

    /// <summary>成分を指定して生成する。</summary>
    public Vec2(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>原点 (0, 0)。</summary>
    public static Vec2 Zero => new(0.0, 0.0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    /// <summary>ベクトルの長さの 2 乗（平方根を避けたい比較用）。</summary>
    public double LengthSquared => X * X + Y * Y;

    /// <summary>ベクトルの長さ。</summary>
    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>内積。</summary>
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>2 次元の外積（z 成分）。符号で左右どちら側かを判定する。</summary>
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>2 点間の距離。</summary>
    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    /// <summary>2 点間の距離の 2 乗。</summary>
    public static double DistanceSquared(Vec2 a, Vec2 b) => (a - b).LengthSquared;

    /// <summary>成分ごとの厳密一致（ハッシュ用。近似比較には使わない）。</summary>
    public bool Equals(Vec2 other) => X.Equals(other.X) && Y.Equals(other.Y);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Vec2 v && Equals(v);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y);

    /// <inheritdoc/>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###})");
}
