using System;

namespace SEED.UI;

// ============================================================
//  SliderTicks.cs — スライダの刻みの点（Flutter の Slider の divisions の目盛り）の数・置き場・色の決め方（2026-10-03。純粋な計算）
//
//  Slider.TickCount（刻みの数。0 = 点なし）が 1 以上なら、溝の上に両端を含めて TickCount + 1 個の点を等間隔に描く
//  （Wake or Pay の FullWidthSlider の TickCount と同じ意味。backlog lane2 残件 (2)）。値の段階（Slider.Step）とは独立
//  （点を描くだけで値は寄せない。寄せたいなら Step = (Max − Min) / TickCount を別に指定する）。
//    - 点の中心 = 溝の左端 + 溝の長さ × i / 刻みの数（i = 0..刻みの数）、高さは溝の中心
//    - 色: 塗り（値まで）の上の点は color.on_primary、塗りの外の点は color.on_surface_muted（Wake or Pay と同じ）。無効の部品は opacity.disabled で薄める
//    - 点の直径は size.slider_tick（既定 3）。隣の点との間隔が直径 × MinSpacingRatio に満たない（点がくっつく）なら点を描かない
//    - 刻みの数は MaxTickCount まで（誤って大きな数を渡しても点のノードを作りすぎない）
//  点のノードは Slider の子 Ticks の下に点のプレハブ（slider_tick.actor）から作る（SliderTickDots。ここはエンジンの API に触れない）。
// ============================================================

/// <summary>スライダの刻みの点の数・置き場・色の決め方。</summary>
public static class SliderTicks
{
    /// <summary>刻みの数の上限（点は両端を含めてこれ ＋ 1 個まで。幅 240 の部品で点の間隔 2 dp 程度＝実用の上限より十分大きい）。</summary>
    public const int MaxTickCount = 100;

    /// <summary>隣の点の中心の間隔 ÷ 点の直径の下限（これより詰まると点がくっついて溝の模様に見えるので描かない。2 = 点 1 つぶんの隙間）。</summary>
    public const float MinSpacingRatio = 2f;

    /// <summary>半分（点の直径 → 半径）。</summary>
    private const float Half = 0.5f;

    /// <summary>塗りの上かを比べるときの許す差（値の割合 × 刻みの数の丸めの誤差。ちょうど点の上の値でその点を塗りの上とみなす）。</summary>
    private const float ActiveEpsilon = 1e-4f;

    /// <summary>刻みの数を使える範囲（0〜<see cref="MaxTickCount"/>）へ収める。</summary>
    /// <param name="tickCount">刻みの数（Slider.TickCount）。</param>
    public static int Normalize(int tickCount) => Math.Clamp(tickCount, 0, MaxTickCount);

    /// <summary>
    /// 描く点の数（両端を含めて 刻みの数 ＋ 1）。刻みの数が 0 以下・溝の長さが無い・点がくっつく（間隔 &lt; 直径 × <see cref="MinSpacingRatio"/>）なら 0。
    /// </summary>
    /// <param name="tickCount">刻みの数。</param>
    /// <param name="trackLength">溝の長さ（キャンバスの単位）。</param>
    /// <param name="dotSize">点の直径（size.slider_tick）。</param>
    public static int DotCount(int tickCount, float trackLength, float dotSize)
    {
        int divisions = Normalize(tickCount);
        if (divisions == 0 || !float.IsFinite(trackLength) || trackLength <= 0f) return 0;
        // 点がくっつくほど詰まるなら描かない（直径が 0 以下・壊れた値なら間隔を見ない）
        if (float.IsFinite(dotSize) && dotSize > 0f && trackLength / divisions < dotSize * MinSpacingRatio) return 0;
        return divisions + 1;
    }

    /// <summary>点 i の中心の x（部品の左端から。溝の左端 ＋ 溝の長さ × i / 刻みの数）。</summary>
    /// <param name="index">点の番号（0 = 左端）。</param>
    /// <param name="tickCount">刻みの数（1 以上）。</param>
    /// <param name="track">溝の置き場。</param>
    public static float CenterX(int index, int tickCount, SliderTrack track)
    {
        int divisions = Math.Max(1, Normalize(tickCount));
        return track.X + track.Length * index / divisions;
    }

    /// <summary>点 i が塗り（値まで）の上か（点の位置の割合 i / 刻みの数 が値の割合以下。ちょうど同じなら上）。</summary>
    /// <param name="index">点の番号。</param>
    /// <param name="tickCount">刻みの数（1 以上）。</param>
    /// <param name="fraction">値の割合（ValueMath.Fraction。0〜1）。</param>
    public static bool IsActive(int index, int tickCount, float fraction)
    {
        int divisions = Math.Max(1, Normalize(tickCount));
        float t = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0f;
        return index <= t * divisions + ActiveEpsilon;
    }

    /// <summary>
    /// 点の色（塗りの上 = color.on_primary、外 = color.on_surface_muted。無効の部品は opacity.disabled で薄める）。
    /// </summary>
    /// <param name="theme">テーマ。</param>
    /// <param name="active">塗りの上か。</param>
    /// <param name="enabled">部品が押せるか。</param>
    public static Color ColorOf(UiThemeData theme, bool active, bool enabled)
    {
        var color = theme.Color(active ? UiTokens.ColorOnPrimary : UiTokens.ColorOnSurfaceMuted);
        return enabled ? color : UiColorMath.FadeAlpha(color, theme.Number(UiTokens.OpacityDisabled));
    }

    /// <summary>点の左上の置き場（中心から直径の半分ずつ戻す。点のプレハブは pivot 0・anchor 0）。</summary>
    /// <param name="centerX">点の中心の x。</param>
    /// <param name="centerY">点の中心の y（溝の中心の高さ）。</param>
    /// <param name="dotSize">点の直径。</param>
    public static Vector2 TopLeft(float centerX, float centerY, float dotSize)
    {
        float half = Math.Max(0f, dotSize) * Half;
        return new Vector2(centerX - half, centerY - half);
    }
}
