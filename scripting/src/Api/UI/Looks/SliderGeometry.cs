using System;

namespace SEED.UI;

// ============================================================
//  SliderGeometry.cs — スライダの溝の置き場をレイアウトの大きさに合わせる（2026-10-02。純粋な計算）
//
//  以前の Slider は溝の左端と長さを部品の開始時にプレハブの Track から 1 回だけ読み、レイアウトが幅を変えても
//  （コンテナの fill_width・Stretch・flex で伸ばした・画面の幅が変わった）溝は伸び縮みしなかった（Wake or Pay の W3-1 (1)。
//  プロジェクトは FullWidthSlider を自作して回避した）。ここではプレハブの寸法を「部品の大きさに対する余白と割合」として読み、
//  レイアウトの大きさから溝を決め直す:
//    左の余白 = プレハブの Track の左端、右の余白 = プレハブの部品の幅 − Track の右端（どちらもプレハブのまま。溝の端のつまみの半分の空き）
//    溝の長さ = レイアウトの幅 − 左右の余白（0 未満にしない）
//    溝の中心の高さ = プレハブの「溝の中心 ÷ 部品の高さ」の割合 × レイアウトの高さ
//  レイアウトの大きさがプレハブの大きさと同じ（コンテナが伸ばしていない）なら、溝はプレハブと同じ値になる（既定の見た目は変わらない）。
// ============================================================

/// <summary>スライダのプレハブの寸法（部品の開始時に読む）。</summary>
/// <param name="Width">部品（当たりの帯の Sprite）の幅。</param>
/// <param name="Height">部品の高さ。</param>
/// <param name="TrackX">溝の左端（部品の左端から）。</param>
/// <param name="TrackWidth">溝の長さ。</param>
/// <param name="TrackCenterY">溝の中心の高さ（部品の上端から）。</param>
public readonly record struct SliderBase(float Width, float Height, float TrackX, float TrackWidth, float TrackCenterY);

/// <summary>溝の置き場。</summary>
/// <param name="X">溝の左端（部品の左端から）。</param>
/// <param name="Length">溝の長さ（つまみが動く範囲）。</param>
/// <param name="CenterY">溝の中心の高さ（部品の上端から）。</param>
public readonly record struct SliderTrack(float X, float Length, float CenterY);

/// <summary>スライダの溝の置き場の計算。</summary>
public static class SliderGeometry
{
    /// <summary>
    /// プレハブのままの溝（レイアウトの大きさが分からないとき）。
    /// </summary>
    /// <param name="b">プレハブの寸法。</param>
    public static SliderTrack FromBase(SliderBase b) => new(b.TrackX, Math.Max(0f, b.TrackWidth), b.TrackCenterY);

    /// <summary>
    /// レイアウトの大きさに合わせた溝。
    /// </summary>
    /// <param name="b">プレハブの寸法。</param>
    /// <param name="layoutWidth">部品のレイアウトの幅（CanvasTransform.LayoutSize.x。0 以下・有限でない値ならプレハブのまま）。</param>
    /// <param name="layoutHeight">部品のレイアウトの高さ（同上。0 以下・有限でない値・プレハブの高さが 0 なら溝の高さはプレハブのまま）。</param>
    public static SliderTrack Resolve(SliderBase b, float layoutWidth, float layoutHeight)
    {
        var track = FromBase(b);
        if (IsPositive(layoutWidth))
        {
            float right = b.Width - (b.TrackX + b.TrackWidth);
            track = track with { Length = Math.Max(0f, layoutWidth - b.TrackX - right) };
        }
        if (IsPositive(layoutHeight) && IsPositive(b.Height))
            track = track with { CenterY = b.TrackCenterY / b.Height * layoutHeight };
        return track;
    }

    /// <summary>有限の正の数か。</summary>
    private static bool IsPositive(float value) => float.IsFinite(value) && value > 0f;
}
