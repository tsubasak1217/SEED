using System;

namespace SEED;

/// <summary>
/// RGBA カラー（各成分 0.0〜1.0 の正規化 float）。スプライトの表示色や
/// カメラのクリアカラーなどに使う不変値型。
///
/// 定義済みカラー（White / Black / Red など）と、補間・乗算などの
/// よく使う操作を備える。不変（readonly）なので、変更したい場合は新しい値を代入する。
/// </summary>
public readonly struct Color : IEquatable<Color>
{
    /// <summary>赤成分（0.0〜1.0）。</summary>
    public readonly float r;
    /// <summary>緑成分（0.0〜1.0）。</summary>
    public readonly float g;
    /// <summary>青成分（0.0〜1.0）。</summary>
    public readonly float b;
    /// <summary>アルファ成分（0.0=透明〜1.0=不透明）。</summary>
    public readonly float a;

    /// <summary>RGBA を指定して生成する。アルファ省略時は不透明（1.0）。</summary>
    public Color(float r, float g, float b, float a = 1f)
    {
        this.r = r; this.g = g; this.b = b; this.a = a;
    }

    // ── 定義済みカラー ───────────────────────────────────────
    public static Color White       => new(1f, 1f, 1f, 1f);
    public static Color Black       => new(0f, 0f, 0f, 1f);
    public static Color Red         => new(1f, 0f, 0f, 1f);
    public static Color Green       => new(0f, 1f, 0f, 1f);
    public static Color Blue        => new(0f, 0f, 1f, 1f);
    public static Color Yellow      => new(1f, 1f, 0f, 1f);
    public static Color Cyan        => new(0f, 1f, 1f, 1f);
    public static Color Magenta     => new(1f, 0f, 1f, 1f);
    public static Color Gray        => new(0.5f, 0.5f, 0.5f, 1f);
    public static Color Transparent => new(0f, 0f, 0f, 0f);

    // ── 操作 ────────────────────────────────────────────────

    /// <summary>アルファだけを差し替えた新しいカラーを返す。</summary>
    public Color WithAlpha(float alpha) => new(r, g, b, alpha);

    /// <summary>a→b を t（0..1、クランプ）で線形補間する。</summary>
    public static Color Lerp(Color a, Color b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Color(
            a.r + (b.r - a.r) * t,
            a.g + (b.g - a.g) * t,
            a.b + (b.b - a.b) * t,
            a.a + (b.a - a.a) * t);
    }

    // ── 演算子 ──────────────────────────────────────────────
    /// <summary>成分ごとの乗算（ティント合成）。</summary>
    public static Color operator *(Color x, Color y) => new(x.r * y.r, x.g * y.g, x.b * y.b, x.a * y.a);
    /// <summary>スカラー倍（アルファも含む）。</summary>
    public static Color operator *(Color c, float s) => new(c.r * s, c.g * s, c.b * s, c.a * s);
    public static bool operator ==(Color x, Color y) => x.Equals(y);
    public static bool operator !=(Color x, Color y) => !x.Equals(y);

    // ── 等価・表示 ───────────────────────────────────────────
    public bool Equals(Color other) => r == other.r && g == other.g && b == other.b && a == other.a;
    public override bool Equals(object? obj) => obj is Color c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(r, g, b, a);
    public override string ToString() => $"RGBA({r:F3}, {g:F3}, {b:F3}, {a:F3})";
}
