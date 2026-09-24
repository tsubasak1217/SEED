using System;

namespace SEED;

/// <summary>
/// 2 次元の矩形（左上の位置 x, y と幅 width・高さ height）。不変値型。
///
/// 座標は <see cref="Input.MousePos"/> と同じ画面座標（ピクセル・左上原点・Y 下向き）で使うことを想定している
/// （<see cref="Screen.SafeArea"/> など）。<c>x</c> / <c>y</c> が左上、<c>x + width</c> / <c>y + height</c> が右下。
/// </summary>
public readonly struct Rect : IEquatable<Rect>
{
    /// <summary>左上の X（ピクセル）。</summary>
    public readonly float x;
    /// <summary>左上の Y（ピクセル。下向きが正）。</summary>
    public readonly float y;
    /// <summary>幅（ピクセル）。</summary>
    public readonly float width;
    /// <summary>高さ（ピクセル）。</summary>
    public readonly float height;

    /// <summary>左上の位置と大きさを指定して生成する。</summary>
    public Rect(float x, float y, float width, float height)
    {
        this.x = x; this.y = y; this.width = width; this.height = height;
    }

    /// <summary>左上の位置と大きさ（ベクトル）を指定して生成する。</summary>
    public Rect(Vector2 position, Vector2 size) : this(position.x, position.y, size.x, size.y) { }

    /// <summary>大きさ 0 の矩形（原点）。</summary>
    public static Rect Zero => new(0f, 0f, 0f, 0f);

    /// <summary>中心を求める係数（幅・高さの半分）。</summary>
    private const float Half = 0.5f;

    // ── 端・位置・大きさ ─────────────────────────────────────

    /// <summary>左端の X（= x）。</summary>
    public float XMin => x;
    /// <summary>上端の Y（= y）。</summary>
    public float YMin => y;
    /// <summary>右端の X（= x + width）。</summary>
    public float XMax => x + width;
    /// <summary>下端の Y（= y + height）。</summary>
    public float YMax => y + height;
    /// <summary>左上の位置。</summary>
    public Vector2 Position => new(x, y);
    /// <summary>大きさ（幅, 高さ）。</summary>
    public Vector2 Size => new(width, height);
    /// <summary>中心の位置。</summary>
    public Vector2 Center => new(x + width * Half, y + height * Half);

    /// <summary>点が矩形の内側（境界を含む）にあるか。</summary>
    public bool Contains(Vector2 point)
        => point.x >= XMin && point.x <= XMax && point.y >= YMin && point.y <= YMax;

    // ── 等価・表示 ───────────────────────────────────────────
    public static bool operator ==(Rect a, Rect b) => a.Equals(b);
    public static bool operator !=(Rect a, Rect b) => !a.Equals(b);
    public bool Equals(Rect other)
        => x == other.x && y == other.y && width == other.width && height == other.height;
    public override bool Equals(object? obj) => obj is Rect r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(x, y, width, height);
    /// <summary>デバッグ表示用（例: <c>Rect(x=0.00, y=136.00, w=1080.00, h=2201.00)</c>）。</summary>
    public override string ToString() => $"Rect(x={x:F2}, y={y:F2}, w={width:F2}, h={height:F2})";
}
