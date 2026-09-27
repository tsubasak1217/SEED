using System;

namespace SEED;

// ============================================================
//  SpriteStyleTypes.cs — スプライトの形と塗り・切り抜きの形（W2-4）の列挙と値の型
//
//  Sprite（形・塗り・9 スライス・影）と CanvasClip（切り抜きの形）の欄の型。
//  【重要】列挙の数値は Rust 側 runtime/src/engine/core/scripting/sprite_style_api.rs の
//  IndexedEnum::ALL の並びと必ず一致させること（FFI では数値で受け渡す。ずれると別の値として読まれる）。
//  足すときは末尾へ（並びを変えると既存のスクリプトの値が変わる）。
//  長さの単位はスプライトの幅・高さと同じキャンバスの単位（dp のキャンバスなら dp）。角度は度（0 = +X・時計回り）。
// ============================================================

/// <summary>スプライトの形。</summary>
public enum SpriteShapeKind
{
    /// <summary>矩形（四隅ごとの角丸。既定）。</summary>
    Rect = 0,
    /// <summary>矩形に内接する楕円（正方形なら円）。</summary>
    Ellipse = 1,
    /// <summary>矩形の中心の円の弧（リング。進捗の輪）。</summary>
    Arc = 2,
}

/// <summary>スプライトの塗り。</summary>
public enum SpriteFillKind
{
    /// <summary>単色（Sprite.Color。既定）。</summary>
    Solid = 0,
    /// <summary>線形グラデーション（2〜4 色・角度）。</summary>
    Linear = 1,
    /// <summary>放射グラデーション（2〜4 色・中心・半径）。</summary>
    Radial = 2,
}

/// <summary>9 スライスの辺・中央の埋め方。</summary>
public enum NineSliceMode
{
    /// <summary>伸ばす（既定）。</summary>
    Stretch = 0,
    /// <summary>繰り返す（回数を丸めて、端で切れたタイルを作らない）。</summary>
    Repeat = 1,
}

/// <summary>切り抜きの形（CanvasClip）。</summary>
public enum ClipShape
{
    /// <summary>矩形（既定）。</summary>
    Rect = 0,
    /// <summary>四隅ごとの角丸（CanvasClip.CornerRadii）。</summary>
    RoundedRect = 1,
    /// <summary>矩形に内接する楕円（丸いアイコン）。</summary>
    Ellipse = 2,
    /// <summary>最初の有効な Sprite の形（角丸・楕円）に合わせる（角丸のカード）。</summary>
    SpriteShape = 3,
}

/// <summary>
/// 四隅の角丸の半径（左上・右上・右下・左下。キャンバスの単位）。不変値型。
/// </summary>
public readonly struct CornerRadii : IEquatable<CornerRadii>
{
    /// <summary>左上。</summary>
    public readonly float TopLeft;
    /// <summary>右上。</summary>
    public readonly float TopRight;
    /// <summary>右下。</summary>
    public readonly float BottomRight;
    /// <summary>左下。</summary>
    public readonly float BottomLeft;

    /// <summary>角ごとに指定して生成する。</summary>
    public CornerRadii(float topLeft, float topRight, float bottomRight, float bottomLeft)
    {
        TopLeft = topLeft; TopRight = topRight; BottomRight = bottomRight; BottomLeft = bottomLeft;
    }

    /// <summary>四隅とも同じ半径。</summary>
    public static CornerRadii All(float radius) => new(radius, radius, radius, radius);

    /// <summary>上の 2 つと下の 2 つ（シートの上だけ丸い角など）。</summary>
    public static CornerRadii Vertical(float top, float bottom) => new(top, top, bottom, bottom);

    /// <summary>角丸なし。</summary>
    public static CornerRadii Zero => new(0f, 0f, 0f, 0f);

    /// <summary>FFI の並び（左上・右上・右下・左下）の配列へ。</summary>
    internal void CopyTo(Span<float> dst)
    {
        dst[0] = TopLeft; dst[1] = TopRight; dst[2] = BottomRight; dst[3] = BottomLeft;
    }

    public static bool operator ==(CornerRadii a, CornerRadii b) => a.Equals(b);
    public static bool operator !=(CornerRadii a, CornerRadii b) => !a.Equals(b);
    public bool Equals(CornerRadii other)
        => TopLeft == other.TopLeft && TopRight == other.TopRight
        && BottomRight == other.BottomRight && BottomLeft == other.BottomLeft;
    public override bool Equals(object? obj) => obj is CornerRadii r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(TopLeft, TopRight, BottomRight, BottomLeft);
    public override string ToString() => $"(TL {TopLeft}, TR {TopRight}, BR {BottomRight}, BL {BottomLeft})";
}

/// <summary>
/// 9 スライスの枠の 4 辺の幅（左・上・右・下。テクスチャの画素）。不変値型。
/// </summary>
public readonly struct NineSliceBorder : IEquatable<NineSliceBorder>
{
    /// <summary>左。</summary>
    public readonly float Left;
    /// <summary>上。</summary>
    public readonly float Top;
    /// <summary>右。</summary>
    public readonly float Right;
    /// <summary>下。</summary>
    public readonly float Bottom;

    /// <summary>辺ごとに指定して生成する。</summary>
    public NineSliceBorder(float left, float top, float right, float bottom)
    {
        Left = left; Top = top; Right = right; Bottom = bottom;
    }

    /// <summary>4 辺とも同じ幅。</summary>
    public static NineSliceBorder All(float width) => new(width, width, width, width);

    public static bool operator ==(NineSliceBorder a, NineSliceBorder b) => a.Equals(b);
    public static bool operator !=(NineSliceBorder a, NineSliceBorder b) => !a.Equals(b);
    public bool Equals(NineSliceBorder other)
        => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
    public override bool Equals(object? obj) => obj is NineSliceBorder b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    public override string ToString() => $"(L {Left}, T {Top}, R {Right}, B {Bottom})";
}

/// <summary>
/// 形と塗りの欄の読み書き（四隅・枠・色の列）の共通処理（ラッパーの内部用）。
/// 数値の表現は Rust 側 sprite_style_api.rs と一致させる。
/// </summary>
internal static class SpriteStyleFieldAccess
{
    /// <summary>四隅・枠の要素数。</summary>
    private const int QuadLength = 4;
    /// <summary>色の要素数。</summary>
    private const int ColorLength = 4;
    /// <summary>グラデーションの色の数の上限（Rust 側 GRADIENT_MAX_COLORS と一致）。</summary>
    public const int MaxGradientColors = 4;
    /// <summary>グラデーションの色の数の下限（Rust 側 GRADIENT_MIN_COLORS と一致）。</summary>
    public const int MinGradientColors = 2;

    /// <summary>四隅の角丸を読む（失敗時は角丸なし）。</summary>
    public static CornerRadii GetRadii(Entity e, string comp, string field)
    {
        Span<float> buf = stackalloc float[QuadLength];
        return ScriptHost.TryGetFloats(e, comp, field, buf) == QuadLength
            ? new CornerRadii(buf[0], buf[1], buf[2], buf[3])
            : CornerRadii.Zero;
    }

    /// <summary>四隅の角丸を書く。</summary>
    public static void SetRadii(Entity e, string comp, string field, CornerRadii value)
    {
        Span<float> buf = stackalloc float[QuadLength];
        value.CopyTo(buf);
        ScriptHost.TrySetFloats(e, comp, field, buf);
    }

    /// <summary>9 スライスの枠を読む（失敗時は 0）。</summary>
    public static NineSliceBorder GetBorder(Entity e, string comp, string field)
    {
        Span<float> buf = stackalloc float[QuadLength];
        return ScriptHost.TryGetFloats(e, comp, field, buf) == QuadLength
            ? new NineSliceBorder(buf[0], buf[1], buf[2], buf[3])
            : NineSliceBorder.All(0f);
    }

    /// <summary>9 スライスの枠を書く。</summary>
    public static void SetBorder(Entity e, string comp, string field, NineSliceBorder value)
    {
        Span<float> buf = stackalloc float[QuadLength];
        buf[0] = value.Left; buf[1] = value.Top; buf[2] = value.Right; buf[3] = value.Bottom;
        ScriptHost.TrySetFloats(e, comp, field, buf);
    }

    /// <summary>グラデーションの色を読む（fill_color_count 色）。</summary>
    public static Color[] GetGradientColors(Entity e, string comp)
    {
        if (!ScriptHost.TryGetFloat(e, comp, "fill_color_count", out var countF)) return Array.Empty<Color>();
        int count = Math.Clamp((int)countF, 0, MaxGradientColors);
        var colors = new Color[count];
        for (int i = 0; i < count; i++)
            colors[i] = ScriptHost.TryGetColor(e, comp, $"fill_color{i}", out var c) ? c : Color.White;
        return colors;
    }

    /// <summary>グラデーションの色を書く（2〜4 色。範囲外の数は Rust 側が拒否する＝何も変わらない）。</summary>
    public static bool SetGradientColors(Entity e, string comp, ReadOnlySpan<Color> colors)
    {
        if (colors.Length is < MinGradientColors or > MaxGradientColors) return false;
        Span<float> buf = stackalloc float[MaxGradientColors * ColorLength];
        for (int i = 0; i < colors.Length; i++)
        {
            buf[i * ColorLength + 0] = colors[i].r;
            buf[i * ColorLength + 1] = colors[i].g;
            buf[i * ColorLength + 2] = colors[i].b;
            buf[i * ColorLength + 3] = colors[i].a;
        }
        return ScriptHost.TrySetFloats(e, comp, "fill_colors", buf[..(colors.Length * ColorLength)]);
    }

    /// <summary>色の位置を読む（空 = 等間隔）。</summary>
    public static float[] GetStops(Entity e, string comp)
    {
        Span<float> buf = stackalloc float[MaxGradientColors];
        int n = ScriptHost.TryGetFloats(e, comp, "fill_stops", buf);
        return buf[..n].ToArray();
    }

    /// <summary>色の位置を書く（null・空 = 等間隔へ戻す）。</summary>
    public static void SetStops(Entity e, string comp, ReadOnlySpan<float> stops)
    {
        if (stops.IsEmpty)
        {
            ScriptHost.TrySetBool(e, comp, "fill_stops_clear", true);
            return;
        }
        ScriptHost.TrySetFloats(e, comp, "fill_stops", stops[..Math.Min(stops.Length, MaxGradientColors)]);
    }
}
