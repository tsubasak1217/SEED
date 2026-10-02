using System;

namespace SEED.UI;

// ============================================================
//  NavInputMath.cs — 押している向き（キー・D-pad）とスティックを、1 つの移動の向きへまとめる（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  - キー・D-pad（ResolveDigital）: このフレームに押し始めた向きを優先し、前の向きを押し続けていればそれを保つ。
//    反対向きの同時押し（上と下・左と右）は打ち消す。決まらなければ優先の順（上・下・左・右）の最初
//  - スティック（StickDirection）: 主な軸（大きい方。同じなら縦）の向き。押したとみなすのは閾値（StickPressThreshold）以上、
//    放したとみなすのは閾値（StickReleaseThreshold）未満（ヒステリシス。閾値の近くで震えて連続移動が途切れない）。
//    スティックの値は InputMap の Axis2D（各 [-1, 1]）で、Y の正 = 上（SEED の入力マップの慣例: W・LeftStickY が y の正）
//  - 合わせる（Combine）: キー・D-pad を優先し、押していなければスティック
//  エンジンに触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>押している向きとスティックを 1 つの移動の向きへまとめる（純粋な計算）。</summary>
public static class NavInputMath
{
    /// <summary>スティックを倒したとみなす大きさ（0..1。Unity の既定の deadzone より深め。記憶による）。</summary>
    public const float StickPressThreshold = 0.5f;

    /// <summary>倒したままとみなす大きさの下限（これ未満で放したとみなす。押す閾値より小さくして震えで途切れない）。</summary>
    public const float StickReleaseThreshold = 0.3f;

    /// <summary>
    /// キー・D-pad の押している向きを 1 つにまとめる。
    /// </summary>
    /// <param name="held">押している向き（このフレーム）。</param>
    /// <param name="pressed">このフレームに押し始めた向き。</param>
    /// <param name="previous">前のフレームの向き（まとめた結果）。</param>
    /// <returns>このフレームの向き（何も押していない・打ち消し合えば None）。</returns>
    public static FocusDirection ResolveDigital(FocusDirectionSet held, FocusDirectionSet pressed, FocusDirection previous)
    {
        var active = Cancel(held | pressed);
        if (active == FocusDirectionSet.None) return FocusDirection.None;
        // このフレームに押し始めた向き（打ち消されていないもの）を優先
        foreach (var d in FocusDirections.PriorityOrder)
            if (FocusDirections.Contains(pressed, d) && FocusDirections.Contains(active, d)) return d;
        // 前の向きを押し続けていれば保つ
        if (FocusDirections.Contains(active, previous)) return previous;
        foreach (var d in FocusDirections.PriorityOrder)
            if (FocusDirections.Contains(active, d)) return d;
        return FocusDirection.None;
    }

    /// <summary>
    /// スティックの向き（主な軸。押す・放すの閾値のヒステリシスつき）。
    /// </summary>
    /// <param name="stick">スティック（各 [-1, 1]。Y の正 = 上）。</param>
    /// <param name="previous">前のフレームのスティックの向き。</param>
    public static FocusDirection StickDirection(Vector2 stick, FocusDirection previous)
    {
        float x = float.IsFinite(stick.x) ? stick.x : 0f;
        float y = float.IsFinite(stick.y) ? stick.y : 0f;
        float ax = MathF.Abs(x);
        float ay = MathF.Abs(y);
        if (previous != FocusDirection.None)
        {
            // 前の向きの成分（その向きへの倒れ具合）が放す閾値以上で、もう一方の軸がそれを超えて押す閾値以上でなければ保つ
            float along = AlongComponent(x, y, previous);
            float other = FocusDirections.AxisOf(previous) == NavAxis.Horizontal ? ay : ax;
            if (along >= StickReleaseThreshold && !(other >= StickPressThreshold && other > along)) return previous;
        }
        if (MathF.Max(ax, ay) < StickPressThreshold) return FocusDirection.None;
        if (ax > ay) return x > 0f ? FocusDirection.Right : FocusDirection.Left;
        // 縦（同じ大きさなら縦を選ぶ）。Y の正 = 上
        return y > 0f ? FocusDirection.Up : FocusDirection.Down;
    }

    /// <summary>キー・D-pad を優先し、押していなければスティックの向き。</summary>
    /// <param name="digital">キー・D-pad の向き。</param>
    /// <param name="stick">スティックの向き。</param>
    public static FocusDirection Combine(FocusDirection digital, FocusDirection stick)
        => digital != FocusDirection.None ? digital : stick;

    /// <summary>反対向きの同時押しを打ち消す（上と下・左と右の両方を外す）。</summary>
    private static FocusDirectionSet Cancel(FocusDirectionSet set)
    {
        const FocusDirectionSet Vertical = FocusDirectionSet.Up | FocusDirectionSet.Down;
        const FocusDirectionSet Horizontal = FocusDirectionSet.Left | FocusDirectionSet.Right;
        if ((set & Vertical) == Vertical) set &= ~Vertical;
        if ((set & Horizontal) == Horizontal) set &= ~Horizontal;
        return set;
    }

    /// <summary>向きへの倒れ具合（上 = +y・下 = −y・右 = +x・左 = −x）。</summary>
    private static float AlongComponent(float x, float y, FocusDirection direction) => direction switch
    {
        FocusDirection.Up => y,
        FocusDirection.Down => -y,
        FocusDirection.Right => x,
        FocusDirection.Left => -x,
        _ => 0f,
    };
}
