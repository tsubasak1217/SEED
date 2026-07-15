using System;

namespace SEED;

/// <summary>
/// 2 次元ベクトル（float x, y）。座標・方向・UV などに使う不変値型。
///
/// 演算子（+, -, *, /）とよく使う幾何関数（内積・距離・補間・正規化）を備える。
/// 不変（readonly）なので、変更したい場合は新しい値を代入する。
/// </summary>
public readonly struct Vector2 : IEquatable<Vector2>
{
    /// <summary>X 成分。</summary>
    public readonly float x;
    /// <summary>Y 成分。</summary>
    public readonly float y;

    public Vector2(float x, float y) { this.x = x; this.y = y; }

    // ── 定義済みベクトル ─────────────────────────────────────
    public static Vector2 Zero  => new(0f, 0f);
    public static Vector2 One   => new(1f, 1f);
    public static Vector2 Up    => new(0f, 1f);
    public static Vector2 Down  => new(0f, -1f);
    public static Vector2 Left  => new(-1f, 0f);
    public static Vector2 Right => new(1f, 0f);

    // ── 長さ・正規化 ─────────────────────────────────────────
    /// <summary>ベクトルの長さ。</summary>
    public float Magnitude => Mathf.Sqrt(x * x + y * y);
    /// <summary>長さの 2 乗（平方根を省け、比較が速い）。</summary>
    public float SqrMagnitude => x * x + y * y;
    /// <summary>長さ 1 に正規化したベクトル（長さ 0 のときは Zero）。</summary>
    public Vector2 Normalized
    {
        get
        {
            float m = Magnitude;
            return m > Mathf.Epsilon ? new Vector2(x / m, y / m) : Zero;
        }
    }

    // ── 演算子 ──────────────────────────────────────────────
    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y);
    public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.x - b.x, a.y - b.y);
    public static Vector2 operator -(Vector2 a)            => new(-a.x, -a.y);
    public static Vector2 operator *(Vector2 a, float s)   => new(a.x * s, a.y * s);
    public static Vector2 operator *(float s, Vector2 a)   => new(a.x * s, a.y * s);
    public static Vector2 operator /(Vector2 a, float s)   => new(a.x / s, a.y / s);
    public static bool operator ==(Vector2 a, Vector2 b)   => a.Equals(b);
    public static bool operator !=(Vector2 a, Vector2 b)   => !a.Equals(b);

    // ── 幾何関数 ────────────────────────────────────────────
    /// <summary>内積。</summary>
    public static float Dot(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
    /// <summary>2 点間の距離。</summary>
    public static float Distance(Vector2 a, Vector2 b) => (a - b).Magnitude;
    /// <summary>成分ごとの積（スケール）。</summary>
    public static Vector2 Scale(Vector2 a, Vector2 b) => new(a.x * b.x, a.y * b.y);
    /// <summary>a→b を t（0..1、クランプ）で線形補間する。</summary>
    public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
    {
        t = Mathf.Clamped01(t);
        return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
    }
    /// <summary>成分ごとの最小値。</summary>
    public static Vector2 Min(Vector2 a, Vector2 b) => new(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
    /// <summary>成分ごとの最大値。</summary>
    public static Vector2 Max(Vector2 a, Vector2 b) => new(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

    // ── 等価・文字列化 ───────────────────────────────────────
    public bool Equals(Vector2 other)
        => Mathf.Approximately(x, other.x) && Mathf.Approximately(y, other.y);
    public override bool Equals(object? obj) => obj is Vector2 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(x, y);
    public override string ToString() => $"({x:F2}, {y:F2})";
}
