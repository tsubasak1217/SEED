using System;

namespace SEED;

/// <summary>
/// 回転を表す四元数（x, y, z, w）。ジンバルロックを避けた回転合成に使う。
///
/// SEED の Transform.Rotation は「YXZ オイラー角（度）」だが、回転を合成・補間したい
/// 場合はこの Quaternion を使い、<see cref="Euler(float,float,float)"/> と
/// <see cref="EulerAngles"/> で相互変換する。
/// </summary>
public readonly struct Quaternion : IEquatable<Quaternion>
{
    public readonly float x;
    public readonly float y;
    public readonly float z;
    public readonly float w;

    public Quaternion(float x, float y, float z, float w)
    {
        this.x = x; this.y = y; this.z = z; this.w = w;
    }

    /// <summary>無回転（単位四元数）。</summary>
    public static Quaternion Identity => new(0f, 0f, 0f, 1f);

    // ── 生成 ────────────────────────────────────────────────
    /// <summary>オイラー角（度・Vector3）から回転を生成する。</summary>
    public static Quaternion Euler(Vector3 eulerDegrees)
        => Euler(eulerDegrees.x, eulerDegrees.y, eulerDegrees.z);

    /// <summary>
    /// オイラー角（度）から回転を生成する。適用順は SEED の Transform に合わせ YXZ
    /// （Z→X→Y の順に掛ける）。
    /// </summary>
    public static Quaternion Euler(float xDeg, float yDeg, float zDeg)
    {
        float hx = xDeg * Mathf.Deg2Rad * 0.5f;
        float hy = yDeg * Mathf.Deg2Rad * 0.5f;
        float hz = zDeg * Mathf.Deg2Rad * 0.5f;

        float cx = Mathf.Cos(hx), sx = Mathf.Sin(hx);
        float cy = Mathf.Cos(hy), sy = Mathf.Sin(hy);
        float cz = Mathf.Cos(hz), sz = Mathf.Sin(hz);

        // qY * qX * qZ（YXZ 順）
        var qx = new Quaternion(sx, 0f, 0f, cx);
        var qy = new Quaternion(0f, sy, 0f, cy);
        var qz = new Quaternion(0f, 0f, sz, cz);
        return qy * qx * qz;
    }

    /// <summary>軸（正規化される）まわりに angle 度回転する四元数。</summary>
    public static Quaternion AngleAxis(float angleDegrees, Vector3 axis)
    {
        Vector3 a = axis.Normalized;
        float half = angleDegrees * Mathf.Deg2Rad * 0.5f;
        float s = Mathf.Sin(half);
        return new Quaternion(a.x * s, a.y * s, a.z * s, Mathf.Cos(half));
    }

    // ── 合成・回転適用 ───────────────────────────────────────
    /// <summary>回転の合成（lhs の後に rhs… ではなく lhs*rhs は rhs を先に適用）。</summary>
    public static Quaternion operator *(Quaternion a, Quaternion b)
        => new(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

    /// <summary>点／方向ベクトルをこの回転で回す。</summary>
    public static Vector3 operator *(Quaternion q, Vector3 v)
    {
        // v' = v + 2*cross(q.xyz, cross(q.xyz, v) + w*v)
        var u = new Vector3(q.x, q.y, q.z);
        Vector3 t = Vector3.Cross(u, v) + v * q.w;
        return v + Vector3.Cross(u, t) * 2f;
    }

    // ── 変換・正規化 ─────────────────────────────────────────
    /// <summary>長さ 1 に正規化した四元数。</summary>
    public Quaternion Normalized
    {
        get
        {
            float m = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            return m > Mathf.Epsilon ? new Quaternion(x / m, y / m, z / m, w / m) : Identity;
        }
    }

    /// <summary>
    /// この回転を YXZ オイラー角（度）へ変換する。
    /// Transform.Rotation へ書き戻すときに使う。
    /// </summary>
    public Vector3 EulerAngles
    {
        get
        {
            // YXZ 順の分解（X がピッチ、Y がヨー、Z がロール）
            float sinX = 2f * (w * x - y * z);
            sinX = Mathf.Clamped(sinX, -1f, 1f);
            float ex = Mathf.Asin(sinX);
            float ey = Mathf.Atan2(2f * (w * y + x * z), 1f - 2f * (x * x + y * y));
            float ez = Mathf.Atan2(2f * (w * z + x * y), 1f - 2f * (x * x + z * z));
            return new Vector3(ex * Mathf.Rad2Deg, ey * Mathf.Rad2Deg, ez * Mathf.Rad2Deg);
        }
    }

    // ── 等価・文字列化 ───────────────────────────────────────
    public bool Equals(Quaternion o)
        => Mathf.Approximately(x, o.x) && Mathf.Approximately(y, o.y)
        && Mathf.Approximately(z, o.z) && Mathf.Approximately(w, o.w);
    public override bool Equals(object? obj) => obj is Quaternion q && Equals(q);
    public override int GetHashCode() => HashCode.Combine(x, y, z, w);
    public override string ToString() => $"({x:F2}, {y:F2}, {z:F2}, {w:F2})";
}
