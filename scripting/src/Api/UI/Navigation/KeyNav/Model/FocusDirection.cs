using System;

namespace SEED.UI;

// ============================================================
//  FocusDirection.cs — 方向キー・パッドでフォーカスを移す向きと、部品が値を増減する軸（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  純粋な値と小道具（エンジンに触れない。editor/tests/UiComponentsTests で検算）。
//  画面の座標は左上が原点・Y 下向き（CanvasTransform.LayoutRect・Input.MousePos と同じ）なので、上の単位ベクトルは (0, −1)。
//  値の増減の符号は「左・上 = −1、右・下 = +1」（ホイールの上の矢印 = 前の行〈WheelPicker.StepBy(−1)〉と同じ向き）。
// ============================================================

/// <summary>フォーカスを移す向き（方向キー・D-pad・スティック）。</summary>
public enum FocusDirection
{
    /// <summary>向きなし（何も押していない）。</summary>
    None = 0,
    /// <summary>上。</summary>
    Up = 1,
    /// <summary>下。</summary>
    Down = 2,
    /// <summary>左。</summary>
    Left = 3,
    /// <summary>右。</summary>
    Right = 4,
}

/// <summary>部品が方向キーで値を増減する軸（その軸の向きはフォーカスを移さず値を変える。None = いつも移す）。</summary>
public enum NavAxis
{
    /// <summary>値を増減しない（方向キーはいつもフォーカスを移す）。</summary>
    None = 0,
    /// <summary>左右で増減する（スライダ・数値欄・セグメント・ラジオ）。</summary>
    Horizontal = 1,
    /// <summary>上下で増減する（ホイール）。</summary>
    Vertical = 2,
}

/// <summary>押している向きの集まり（同時押しを 1 つの向きへまとめる前の生の状態）。</summary>
[Flags]
public enum FocusDirectionSet
{
    /// <summary>何も押していない。</summary>
    None = 0,
    /// <summary>上。</summary>
    Up = 1,
    /// <summary>下。</summary>
    Down = 2,
    /// <summary>左。</summary>
    Left = 4,
    /// <summary>右。</summary>
    Right = 8,
}

/// <summary>向きの小道具（軸・符号・単位ベクトル・集まりとの変換）。</summary>
public static class FocusDirections
{
    /// <summary>押している向きを 1 つに決めるときの優先の順（同時に押し始めた・前の向きを離したとき。上下を左右より先に見る）。</summary>
    public static readonly FocusDirection[] PriorityOrder = { FocusDirection.Up, FocusDirection.Down, FocusDirection.Left, FocusDirection.Right };

    /// <summary>向きの軸（上下 = Vertical・左右 = Horizontal・None = None）。</summary>
    /// <param name="direction">向き。</param>
    public static NavAxis AxisOf(FocusDirection direction) => direction switch
    {
        FocusDirection.Left or FocusDirection.Right => NavAxis.Horizontal,
        FocusDirection.Up or FocusDirection.Down => NavAxis.Vertical,
        _ => NavAxis.None,
    };

    /// <summary>軸に沿った符号（左・上 = −1、右・下 = +1、None = 0）。</summary>
    /// <param name="direction">向き。</param>
    public static int SignOf(FocusDirection direction) => direction switch
    {
        FocusDirection.Left or FocusDirection.Up => -1,
        FocusDirection.Right or FocusDirection.Down => 1,
        _ => 0,
    };

    /// <summary>画面の座標（Y 下向き）の単位ベクトル（None は 0）。</summary>
    /// <param name="direction">向き。</param>
    public static Vector2 UnitOf(FocusDirection direction) => direction switch
    {
        FocusDirection.Up => new Vector2(0f, -1f),
        FocusDirection.Down => new Vector2(0f, 1f),
        FocusDirection.Left => new Vector2(-1f, 0f),
        FocusDirection.Right => new Vector2(1f, 0f),
        _ => Vector2.Zero,
    };

    /// <summary>逆の向き（None は None）。</summary>
    /// <param name="direction">向き。</param>
    public static FocusDirection Opposite(FocusDirection direction) => direction switch
    {
        FocusDirection.Up => FocusDirection.Down,
        FocusDirection.Down => FocusDirection.Up,
        FocusDirection.Left => FocusDirection.Right,
        FocusDirection.Right => FocusDirection.Left,
        _ => FocusDirection.None,
    };

    /// <summary>向き → 集まりの 1 つの印（None は None）。</summary>
    /// <param name="direction">向き。</param>
    public static FocusDirectionSet ToSet(FocusDirection direction) => direction switch
    {
        FocusDirection.Up => FocusDirectionSet.Up,
        FocusDirection.Down => FocusDirectionSet.Down,
        FocusDirection.Left => FocusDirectionSet.Left,
        FocusDirection.Right => FocusDirectionSet.Right,
        _ => FocusDirectionSet.None,
    };

    /// <summary>集まりが向きを含むか（None は含まない）。</summary>
    /// <param name="set">集まり。</param>
    /// <param name="direction">向き。</param>
    public static bool Contains(FocusDirectionSet set, FocusDirection direction)
    {
        var flag = ToSet(direction);
        return flag != FocusDirectionSet.None && (set & flag) == flag;
    }
}
