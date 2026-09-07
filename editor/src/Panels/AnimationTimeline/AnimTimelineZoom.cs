// ============================================================
//  AnimTimelineZoom.cs — 時間軸のズーム・スクロール算術（純ロジック）
//
//  ドープシートのナビゲーション（Ctrl+ホイールでカーソル位置を軸にズーム、
//  ホイールで横スクロール、ホイールボタンドラッグでパン、F で全体表示）に
//  必要な計算だけを集めた純粋クラス。
//
//  【カーソル位置を軸にしたズームの考え方】
//   ビューポート左端のキャンバス X = scrollX、カーソルのビューポート内 X = cursorX。
//   ズーム前にカーソル下にあった時刻 t = (scrollX + cursorX) / oldPps を固定したいので、
//   ズーム後は t * newPps - cursorX が新しい scrollX になる（負にならないようクランプ）。
//   この 1 行が「拡大するとカーソル下の内容が逃げる」バグの発生源なので、
//   UI から切り離して単体テストで固定する。
//
//  本クラスは WPF に依存しない（editor/tests から直接リンクしてテストする）。
// ============================================================

using System;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>時間軸のズーム倍率・スクロール位置を求める純粋な計算クラス。</summary>
internal static class AnimTimelineZoom
{
    /// <summary>水平スケールを許容範囲へクランプする。</summary>
    public static double ClampPixelsPerSecond(double pixelsPerSecond)
        => Math.Clamp(pixelsPerSecond,
                      AnimationTimelineConstants.MinPixelsPerSecond,
                      AnimationTimelineConstants.MaxPixelsPerSecond);

    /// <summary>
    /// ホイール 1 ノッチぶんのズーム後スケールを求める。
    /// notches が正なら拡大、負なら縮小。範囲外はクランプされる。
    /// </summary>
    public static double ZoomedPixelsPerSecond(double pixelsPerSecond, int notches)
        => ClampPixelsPerSecond(pixelsPerSecond * Math.Pow(AnimationTimelineConstants.WheelZoomFactor, notches));

    /// <summary>
    /// カーソル下の時刻を固定したままズームするための、新しい横スクロール量を求める。
    /// </summary>
    /// <param name="oldPixelsPerSecond">ズーム前の水平スケール。</param>
    /// <param name="newPixelsPerSecond">ズーム後の水平スケール。</param>
    /// <param name="scrollX">ズーム前の横スクロール量（ピクセル）。</param>
    /// <param name="cursorX">カーソルのビューポート内 X 座標（ピクセル）。</param>
    /// <returns>ズーム後の横スクロール量（0 以上）。</returns>
    public static double ScrollXForZoomAtCursor(
        double oldPixelsPerSecond, double newPixelsPerSecond, double scrollX, double cursorX)
    {
        if (oldPixelsPerSecond <= 0 || newPixelsPerSecond <= 0) return Math.Max(scrollX, 0);
        var timeUnderCursor = (scrollX + cursorX) / oldPixelsPerSecond;
        return Math.Max(timeUnderCursor * newPixelsPerSecond - cursorX, 0);
    }

    /// <summary>
    /// 「F」（全キー／クリップ全体を画面幅に収める）ときの水平スケールを求める。
    /// 右端に余白を残し、クリップ末尾の◆が画面際で切れないようにする。
    /// </summary>
    /// <param name="viewportWidth">表示領域の幅（ピクセル）。</param>
    /// <param name="duration">収めたい長さ（秒）。</param>
    public static double FitPixelsPerSecond(double viewportWidth, float duration)
    {
        var usable = viewportWidth - AnimationTimelineConstants.FitContentMarginPx;
        var span   = Math.Max(duration, AnimationTimelineConstants.MinDuration);
        if (usable <= 0) return ClampPixelsPerSecond(AnimationTimelineConstants.DefaultPixelsPerSecond);
        return ClampPixelsPerSecond(usable / span);
    }

    /// <summary>横スクロール量を [0, コンテンツ幅 - ビューポート幅] へクランプする。</summary>
    public static double ClampScrollX(double scrollX, double contentWidth, double viewportWidth)
        => Math.Clamp(scrollX, 0, Math.Max(contentWidth - viewportWidth, 0));

    /// <summary>縦スクロール量を [0, 行の総高さ - ビューポート高さ] へクランプする。</summary>
    public static double ClampScrollY(double scrollY, double contentHeight, double viewportHeight)
        => Math.Clamp(scrollY, 0, Math.Max(contentHeight - viewportHeight, 0));
}
