using System;

namespace SEED;

/// <summary>
/// 3 次元ベクトル（float x, y, z）。位置・方向・スケールに使う不変値型。
///
/// 演算子（+, -, *, /）と幾何関数（内積・外積・距離・補間・正規化・角度）を備える。
/// SEED の Transform（位置・スケール）とやり取りする基本型でもある。
/// </summary>
public readonly struct Vector3 : IEquatable<Vector3>
{
    /// <summary>X 成分。</summary>
    public readonly float x;
    /// <summary>Y 成分。</summary>
    public readonly float y;
    /// <summary>Z 成分。</summary>
    public readonly float z;

    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    /// <summary>Z=0 として 2D ベクトルから生成する。</summary>
    public Vector3(float x, float y) : this(x, y, 0f) { }

    // ── 定義済みベクトル ─────────────────────────────────────
    public static Vector3 Zero    => new(0f, 0f, 0f);
    public static Vector3 One     => new(1f, 1f, 1f);
    public static Vector3 Up      => new(0f, 1f, 0f);
    public static Vector3 Down    => new(0f, -1f, 0f);
    public static Vector3 Left    => new(-1f, 0f, 0f);
    public static Vector3 Right   => new(1f, 0f, 0f);
    public static Vector3 Forward => new(0f, 0f, 1f);
    public static Vector3 Back    => new(0f, 0f, -1f);

    // ── 長さ・正規化 ─────────────────────────────────────────
    /// <summary>ベクトルの長さ。</summary>
    public float Magnitude => Mathf.Sqrt(x * x + y * y + z * z);
    /// <summary>長さの 2 乗（平方根を省け、比較が速い）。</summary>
    public float SqrMagnitude => x * x + y * y + z * z;
    /// <summary>長さ 1 に正規化したベクトル（長さ 0 のときは Zero）。</summary>
    public Vector3 Normalized
    {
        get
        {
            float m = Magnitude;
            return m > Mathf.Epsilon ? new Vector3(x / m, y / m, z / m) : Zero;
        }
    }

    // ── 演算子 ──────────────────────────────────────────────
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Vector3 operator -(Vector3 a)            => new(-a.x, -a.y, -a.z);
    public static Vector3 operator *(Vector3 a, float s)   => new(a.x * s, a.y * s, a.z * s);
    public static Vector3 operator *(float s, Vector3 a)   => new(a.x * s, a.y * s, a.z * s);
    public static Vector3 operator /(Vector3 a, float s)   => new(a.x / s, a.y / s, a.z / s);
    public static bool operator ==(Vector3 a, Vector3 b)   => a.Equals(b);
    public static bool operator !=(Vector3 a, Vector3 b)   => !a.Equals(b);

    // ── 幾何関数 ────────────────────────────────────────────
    /// <summary>内積。</summary>
    public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    /// <summary>外積（a×b。両者に垂直なベクトル）。</summary>
    public static Vector3 Cross(Vector3 a, Vector3 b)
        => new(a.y * b.z - a.z * b.y,
               a.z * b.x - a.x * b.z,
               a.x * b.y - a.y * b.x);
    /// <summary>2 点間の距離。</summary>
    public static float Distance(Vector3 a, Vector3 b) => (a - b).Magnitude;
    /// <summary>成分ごとの積（スケール）。</summary>
    public static Vector3 Scale(Vector3 a, Vector3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);
    /// <summary>a→b を t（0..1、クランプ）で線形補間する。</summary>
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Vector3(a.x + (b.x - a.x) * t,
                           a.y + (b.y - a.y) * t,
                           a.z + (b.z - a.z) * t);
    }
    /// <summary>current から target へ最大 maxDistanceDelta だけ近づける。</summary>
    public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
    {
        Vector3 diff = target - current;
        float dist = diff.Magnitude;
        if (dist <= maxDistanceDelta || dist < Mathf.Epsilon) return target;
        return current + diff / dist * maxDistanceDelta;
    }
    /// <summary>2 ベクトルのなす角（度）。</summary>
    public static float Angle(Vector3 a, Vector3 b)
    {
        float denom = a.Magnitude * b.Magnitude;
        if (denom < Mathf.Epsilon) return 0f;
        float cos = Mathf.Clamp(Dot(a, b) / denom, -1f, 1f);
        return Mathf.Acos(cos) * Mathf.Rad2Deg;
    }
    /// <summary>成分ごとの最小値。</summary>
    public static Vector3 Min(Vector3 a, Vector3 b)
        => new(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Min(a.z, b.z));
    /// <summary>成分ごとの最大値。</summary>
    public static Vector3 Max(Vector3 a, Vector3 b)
        => new(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y), Mathf.Max(a.z, b.z));

    // ── 等価・文字列化 ───────────────────────────────────────
    public bool Equals(Vector3 other)
        => Mathf.Approximately(x, other.x)
        && Mathf.Approximately(y, other.y)
        && Mathf.Approximately(z, other.z);
    public override bool Equals(object? obj) => obj is Vector3 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(x, y, z);
    public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
}
