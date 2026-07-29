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

    /// <summary>
    /// 指定した方向を SEED の前方（+Z, <see cref="Vector3.Forward"/>）へ向ける回転を作る。
    /// up はワールド上方向（<see cref="Vector3.Up"/>）を基準に、視線に垂直な向きへ直交化する。
    ///
    /// 縮退時の扱い（Unity の Quaternion.LookRotation と同等の方針）:
    /// ・forward がほぼゼロ長 → 向きが定義できないため Identity（無回転）を返す。
    /// ・forward が up とほぼ平行（真上/真下を向く場合）→ up との外積で right を作れないため、
    ///   代替の上方向（forward が +Y 寄りなら Vector3.Back、-Y 寄りなら Vector3.Forward）を用いて
    ///   right/up を作り直す。forward は常に Y 軸上のベクトルになる分岐なので、Z 軸方向の
    ///   Forward/Back のどちらを使っても forward とは平行にならず、直交基底を安全に作れる。
    /// </summary>
    public static Quaternion LookRotation(Vector3 forward)
    {
        // forward がゼロ長 → 向きを定義できないので無回転を返す
        float fwdLenSq = forward.SqrMagnitude;
        if (fwdLenSq < Mathf.Epsilon) return Identity;

        Vector3 fwd = forward.Normalized;
        Vector3 up = Vector3.Up;

        // forward と up がほぼ平行（|dot|が1に近い＝真上/真下を向く）場合は
        // right = cross(up, fwd) がほぼゼロになり基底を作れない。代替 up に差し替える。
        if (Mathf.Abs(Vector3.Dot(fwd, up)) > 0.999f)
        {
            up = fwd.y > 0f ? Vector3.Back : Vector3.Forward;
        }

        // 正規直交基底を構築: right は up と fwd の両方に垂直、newUp は fwd と right の両方に垂直
        Vector3 right = Vector3.Cross(up, fwd).Normalized;
        Vector3 newUp = Vector3.Cross(fwd, right);

        return BasisToQuaternion(right, newUp, fwd);
    }

    /// <summary>
    /// <see cref="LookRotation(Vector3)"/> に加え、視線軸（結果の forward）まわりに
    /// rollDegrees 回転を合成する（カメラのロール／Z 回転に相当）。
    /// 合成順は「まず視線を向け、その後ローカル Z 軸（視線軸）まわりに回す」ため
    /// look * AngleAxis(roll, Vector3.Forward) の順（右側が先に適用される）で掛け合わせる。
    /// </summary>
    public static Quaternion LookRotation(Vector3 forward, float rollDegrees)
    {
        Quaternion look = LookRotation(forward);
        Quaternion roll = AngleAxis(rollDegrees, Vector3.Forward);
        return look * roll;
    }

    /// <summary>
    /// 正規直交基底（right, up, forward）から回転行列を経由してクォータニオンへ変換する。
    /// trace（対角成分の和）で分岐する数値安定な標準アルゴリズム（Shoemake 法）。
    /// トレースが小さい（=対角成分が負に近い）場合に単純な公式を使うとゼロ除算に近づき
    /// 精度が落ちるため、最大の対角成分に応じて 4 通りに分岐する。
    /// </summary>
    private static Quaternion BasisToQuaternion(Vector3 right, Vector3 up, Vector3 fwd)
    {
        // 列ベクトルが right/up/fwd の回転行列（m[行,列]）
        float m00 = right.x, m01 = up.x, m02 = fwd.x;
        float m10 = right.y, m11 = up.y, m12 = fwd.y;
        float m20 = right.z, m21 = up.z, m22 = fwd.z;

        float trace = m00 + m11 + m22;
        if (trace > 0f)
        {
            float s = Mathf.Sqrt(trace + 1f) * 2f; // s = 4 * w
            float w = 0.25f * s;
            float x = (m21 - m12) / s;
            float y = (m02 - m20) / s;
            float z = (m10 - m01) / s;
            return new Quaternion(x, y, z, w);
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f; // s = 4 * x
            float w = (m21 - m12) / s;
            float x = 0.25f * s;
            float y = (m01 + m10) / s;
            float z = (m02 + m20) / s;
            return new Quaternion(x, y, z, w);
        }
        else if (m11 > m22)
        {
            float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f; // s = 4 * y
            float w = (m02 - m20) / s;
            float x = (m01 + m10) / s;
            float y = 0.25f * s;
            float z = (m12 + m21) / s;
            return new Quaternion(x, y, z, w);
        }
        else
        {
            float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f; // s = 4 * z
            float w = (m10 - m01) / s;
            float x = (m02 + m20) / s;
            float y = (m12 + m21) / s;
            float z = 0.25f * s;
            return new Quaternion(x, y, z, w);
        }
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
