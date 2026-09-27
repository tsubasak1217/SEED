using System;

namespace SEED;

// ============================================================
//  CanvasLayoutTypes.cs — キャンバスのレイアウトの部品（W2-1b）が共有する列挙と余白の型
//
//  CanvasStack・CanvasWrap・CanvasGrid・CanvasLayoutItem の欄の型。
//  【重要】列挙の数値は Rust 側 runtime/src/engine/components/canvas_layout_params.rs の
//  IndexedEnum::ALL の並びと必ず一致させること（FFI では数値で受け渡す。ずれると別の値として読まれる）。
//  足すときは末尾へ（並びを変えると既存のスクリプトの値が変わる）。
// ============================================================

/// <summary>並べる向き（主軸）。</summary>
public enum LayoutDirection
{
    /// <summary>縦に並べる（上から下）。</summary>
    Vertical = 0,
    /// <summary>横に並べる（左から右）。</summary>
    Horizontal = 1,
}

/// <summary>主軸の揃え（子を並べ終えて余った長さの配り方）。</summary>
public enum MainAlign
{
    /// <summary>先頭へ寄せる。</summary>
    Start = 0,
    /// <summary>中央へ寄せる。</summary>
    Center = 1,
    /// <summary>末尾へ寄せる。</summary>
    End = 2,
    /// <summary>両端をそろえ、余りを子の間へ等分する。</summary>
    SpaceBetween = 3,
    /// <summary>余りを子の前後へ等分する（端は子の間の半分）。</summary>
    SpaceAround = 4,
    /// <summary>余りを端と子の間へすべて等分する。</summary>
    SpaceEvenly = 5,
}

/// <summary>交差軸の揃え（コンテナの既定）。</summary>
public enum CrossAlign
{
    /// <summary>先頭（縦に並べるなら左・横に並べるなら上）へ寄せる。</summary>
    Start = 0,
    /// <summary>中央へ寄せる。</summary>
    Center = 1,
    /// <summary>末尾へ寄せる。</summary>
    End = 2,
    /// <summary>交差軸いっぱいに伸ばす（子の Sprite もその大きさで描く）。</summary>
    Stretch = 3,
}

/// <summary>子の側の揃えの上書き（<see cref="CanvasLayoutItem.AlignSelf"/>）。</summary>
public enum ItemAlign
{
    /// <summary>コンテナの揃えに従う。</summary>
    Auto = 0,
    /// <summary>先頭へ寄せる。</summary>
    Start = 1,
    /// <summary>中央へ寄せる。</summary>
    Center = 2,
    /// <summary>末尾へ寄せる。</summary>
    End = 3,
    /// <summary>いっぱいに伸ばす。</summary>
    Stretch = 4,
}

/// <summary>非表示（Visible = false）・無効（Active = false）の子の扱い。</summary>
public enum HiddenChildren
{
    /// <summary>詰める（並べる対象から外す）。</summary>
    Collapse = 0,
    /// <summary>場所を残す（見えている子と同じように並べる）。</summary>
    KeepSpace = 1,
}

/// <summary>
/// 内側の余白（上下左右。コンテナのキャンバスの単位＝子の位置と同じ単位）。不変値型。
/// </summary>
public readonly struct CanvasPadding : IEquatable<CanvasPadding>
{
    /// <summary>左の余白。</summary>
    public readonly float Left;
    /// <summary>上の余白。</summary>
    public readonly float Top;
    /// <summary>右の余白。</summary>
    public readonly float Right;
    /// <summary>下の余白。</summary>
    public readonly float Bottom;

    /// <summary>辺ごとに指定して生成する。</summary>
    public CanvasPadding(float left, float top, float right, float bottom)
    {
        Left = left; Top = top; Right = right; Bottom = bottom;
    }

    /// <summary>4 辺とも同じ余白。</summary>
    public static CanvasPadding All(float value) => new(value, value, value, value);

    /// <summary>左右と上下をそれぞれ同じ余白。</summary>
    public static CanvasPadding Symmetric(float horizontal, float vertical) => new(horizontal, vertical, horizontal, vertical);

    /// <summary>余白なし。</summary>
    public static CanvasPadding Zero => new(0f, 0f, 0f, 0f);

    public static bool operator ==(CanvasPadding a, CanvasPadding b) => a.Equals(b);
    public static bool operator !=(CanvasPadding a, CanvasPadding b) => !a.Equals(b);
    public bool Equals(CanvasPadding other)
        => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
    public override bool Equals(object? obj) => obj is CanvasPadding p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    public override string ToString() => $"(L {Left}, T {Top}, R {Right}, B {Bottom})";
}

/// <summary>
/// レイアウトの部品の欄の読み書き（列挙・余白・2 要素の値）の共通処理（ラッパーの内部用）。
/// 数値の表現は Rust 側 runtime/src/engine/core/scripting/canvas_layout_api.rs と一致させる。
/// </summary>
internal static class CanvasLayoutFieldAccess
{
    /// <summary>余白の要素数（左・上・右・下）。</summary>
    private const int PaddingLength = 4;

    /// <summary>列挙の欄を読む（失敗時は既定値）。</summary>
    public static TEnum GetEnum<TEnum>(Entity e, string comp, string field, TEnum fallback) where TEnum : struct, Enum
        => ScriptHost.TryGetFloat(e, comp, field, out var f) ? (TEnum)Enum.ToObject(typeof(TEnum), (int)f) : fallback;

    /// <summary>列挙の欄へ書く（範囲外の値は Rust 側が拒否する）。</summary>
    public static void SetEnum<TEnum>(Entity e, string comp, string field, TEnum value) where TEnum : struct, Enum
        => ScriptHost.TrySetFloat(e, comp, field, Convert.ToInt32(value));

    /// <summary>余白の欄を読む（失敗時は余白なし）。</summary>
    public static CanvasPadding GetPadding(Entity e, string comp, string field)
    {
        Span<float> buf = stackalloc float[PaddingLength];
        return ScriptHost.TryGetFloats(e, comp, field, buf) == PaddingLength
            ? new CanvasPadding(buf[0], buf[1], buf[2], buf[3])
            : CanvasPadding.Zero;
    }

    /// <summary>余白の欄へ書く。</summary>
    public static void SetPadding(Entity e, string comp, string field, CanvasPadding value)
    {
        Span<float> buf = stackalloc float[PaddingLength];
        buf[0] = value.Left; buf[1] = value.Top; buf[2] = value.Right; buf[3] = value.Bottom;
        ScriptHost.TrySetFloats(e, comp, field, buf);
    }
}
