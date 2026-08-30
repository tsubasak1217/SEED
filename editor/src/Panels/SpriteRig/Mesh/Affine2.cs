using System;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// 2D アフィン変換（2×2 の線形部 + 平行移動）。
///
/// <para>
/// ボーンのバインドポーズはランタイム側（<c>runtime/src/engine/core/loader/sprite_mesh.rs</c> の
/// <c>trs_to_mat4</c>）で行優先 4×4 行列として組まれるが、2D では 6 成分しか意味を持たない。
/// エディタ側では 4×4 を持ち回らず、その 6 成分だけを持つこの型で計算する。
/// <b>成分の並びと符号はランタイムの <c>trs_to_mat4</c> と完全に一致させてある</b>
/// （ここがずれるとエディタ表示とランタイム描画で骨の位置が食い違う）。
/// </para>
///
/// <para>変換式:</para>
/// <code>
///   x' = A * x + C * y + Tx
///   y' = B * x + D * y + Ty
/// </code>
///
/// <para>
/// 座標系は <c>.sprite_mesh</c> と同じ「左上原点・+X 右・+Y 下」。
/// この向きでは回転角が正のとき画面上では時計回りに見える。
/// </para>
/// </summary>
public readonly struct Affine2
{
    /// <summary>線形部 [0][0]（X 軸の X 成分）。</summary>
    public readonly double A;

    /// <summary>線形部 [1][0]（X 軸の Y 成分）。</summary>
    public readonly double B;

    /// <summary>線形部 [0][1]（Y 軸の X 成分）。</summary>
    public readonly double C;

    /// <summary>線形部 [1][1]（Y 軸の Y 成分）。</summary>
    public readonly double D;

    /// <summary>平行移動の X 成分。</summary>
    public readonly double Tx;

    /// <summary>平行移動の Y 成分。</summary>
    public readonly double Ty;

    /// <summary>行列式がこの絶対値未満なら退化とみなす（ランタイムの MIN_DETERMINANT と同趣旨）。</summary>
    public const double MinDeterminant = 1.0e-12;

    /// <summary>全成分を指定して生成する。</summary>
    /// <param name="a">線形部 [0][0]。</param>
    /// <param name="b">線形部 [1][0]。</param>
    /// <param name="c">線形部 [0][1]。</param>
    /// <param name="d">線形部 [1][1]。</param>
    /// <param name="tx">平行移動 X。</param>
    /// <param name="ty">平行移動 Y。</param>
    public Affine2(double a, double b, double c, double d, double tx, double ty)
    {
        A = a;
        B = b;
        C = c;
        D = d;
        Tx = tx;
        Ty = ty;
    }

    /// <summary>恒等変換。</summary>
    public static Affine2 Identity => new(1.0, 0.0, 0.0, 1.0, 0.0, 0.0);

    /// <summary>
    /// 平行移動・回転・スケールから変換を組む（ランタイムの <c>trs_to_mat4</c> と同一）。
    /// </summary>
    /// <param name="position">平行移動（親ローカル）。</param>
    /// <param name="rotationDegrees">Z 軸まわりの回転（度）。</param>
    /// <param name="scale">スケール。</param>
    public static Affine2 FromTrs(Vec2 position, double rotationDegrees, Vec2 scale)
    {
        double radians = rotationDegrees * Math.PI / 180.0;
        double sin = Math.Sin(radians);
        double cos = Math.Cos(radians);
        return new Affine2(
            cos * scale.X,     // A
            sin * scale.X,     // B
            -sin * scale.Y,    // C
            cos * scale.Y,     // D
            position.X,
            position.Y);
    }

    /// <summary>
    /// 変換の合成（<c>parent * child</c>）。子ローカルの点を親の空間へ写す変換を返す。
    /// </summary>
    /// <param name="parent">外側（親）の変換。</param>
    /// <param name="child">内側（子ローカル）の変換。</param>
    public static Affine2 Multiply(Affine2 parent, Affine2 child) => new(
        parent.A * child.A + parent.C * child.B,
        parent.B * child.A + parent.D * child.B,
        parent.A * child.C + parent.C * child.D,
        parent.B * child.C + parent.D * child.D,
        parent.A * child.Tx + parent.C * child.Ty + parent.Tx,
        parent.B * child.Tx + parent.D * child.Ty + parent.Ty);

    /// <summary>点を変換する。</summary>
    /// <param name="p">変換前の点。</param>
    public Vec2 Transform(Vec2 p) => new(A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty);

    /// <summary>ベクトル（平行移動を無視した向き）を変換する。</summary>
    /// <param name="v">変換前のベクトル。</param>
    public Vec2 TransformVector(Vec2 v) => new(A * v.X + C * v.Y, B * v.X + D * v.Y);

    /// <summary>線形部の行列式。</summary>
    public double Determinant => A * D - B * C;

    /// <summary>
    /// 逆変換を返す。線形部が退化している場合は<b>恒等変換</b>を返す
    /// （ランタイムの <c>invert_affine2d</c> と同じ「落ちない」方針）。
    /// </summary>
    public Affine2 Inverse()
    {
        double det = Determinant;
        if (Math.Abs(det) < MinDeterminant) return Identity;

        double inv = 1.0 / det;
        double ia = D * inv;
        double ib = -B * inv;
        double ic = -C * inv;
        double id = A * inv;
        // 平行移動は -(逆線形部 * t)
        return new Affine2(ia, ib, ic, id,
            -(ia * Tx + ic * Ty),
            -(ib * Tx + id * Ty));
    }
}
