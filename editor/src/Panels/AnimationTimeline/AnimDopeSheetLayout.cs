// ============================================================
//  AnimDopeSheetLayout.cs — ドープシートの座標計算とヒットテスト（純ロジック）
//
//  「時刻 ⇔ X 座標」「トラック添字 ⇔ Y 座標」「点／矩形からキーを拾う」という
//  幾何だけを担当する値型。DopeSheetPanel が持っていた計算をここへ抜き出し、
//  ラバーバンド選択（矩形 ⇔ キー位置）の判定を WPF 抜きで単体テストできるようにする。
//
//  【座標系】
//   X: キャンバス座標（0 = クリップ 0 秒）。横スクロールは ScrollViewer が担当するため、
//      レイアウト側は横オフセットを持たない（マウス座標もキャンバス基準で取れる）。
//   Y: 表示座標。上端にルーラー（固定）、その直下にサマリー行（「全チャンネル」）、
//      以降が実トラック行。縦スクロール量 ScrollY だけ行全体を上へずらす。
//
//  本クラスは WPF に依存しない（editor/tests から直接リンクしてテストする）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>
/// ドープシートの寸法・スクロール状態から座標を求める値型。
/// 状態を持たない計算のかたまりなので、描画のたびに作り直して使う。
/// </summary>
/// <param name="PixelsPerSecond">水平スケール（1 秒あたりのピクセル数）。</param>
/// <param name="ScrollY">縦スクロール量（ピクセル。行を上へずらす量）。</param>
/// <param name="RulerHeight">ルーラーの高さ（ピクセル）。</param>
/// <param name="RowHeight">1 行の高さ（ピクセル）。</param>
internal readonly record struct AnimDopeSheetLayout(
    double PixelsPerSecond,
    double ScrollY,
    double RulerHeight,
    double RowHeight)
{
    /// <summary>既定の寸法（定数）でレイアウトを作る。</summary>
    public static AnimDopeSheetLayout Create(double pixelsPerSecond, double scrollY)
        => new(pixelsPerSecond, scrollY,
               AnimationTimelineConstants.RulerHeight,
               AnimationTimelineConstants.TrackRowHeight);

    // ── 時間軸 ──────────────────────────────────────────────────

    /// <summary>時刻（秒）→ X 座標。</summary>
    public double TimeToX(float time) => time * PixelsPerSecond;

    /// <summary>X 座標 → 時刻（秒）。</summary>
    public float XToTime(double x) => (float)(x / PixelsPerSecond);

    /// <summary>1 フレームぶんの横幅（ピクセル）。</summary>
    public double PixelsPerFrame(float fps) => PixelsPerSecond / AnimFrameMath.NormalizeFps(fps);

    // ── 行 ──────────────────────────────────────────────────────

    /// <summary>サマリー行（「全チャンネル」）の上端 Y。</summary>
    public double SummaryRowTop => RulerHeight - ScrollY;

    /// <summary>実トラック行の上端 Y（サマリー行 1 行ぶん下から積む）。</summary>
    public double TrackRowTop(int trackIndex) => RulerHeight + RowHeight * (1 + trackIndex) - ScrollY;

    /// <summary>実トラック行の中心 Y（◆はここに描く）。</summary>
    public double TrackRowCenterY(int trackIndex) => TrackRowTop(trackIndex) + RowHeight / 2.0;

    /// <summary>サマリー行の中心 Y。</summary>
    public double SummaryRowCenterY => SummaryRowTop + RowHeight / 2.0;

    /// <summary>全行（サマリー + トラック）の合計高さ。</summary>
    public double ContentHeight(int trackCount) => RulerHeight + RowHeight * (1 + trackCount);

    /// <summary>Y 座標が属するトラック行の添字（ルーラー・サマリー行・範囲外は -1）。</summary>
    public int HitTestTrackRow(double y, int trackCount)
    {
        var top = TrackRowTop(0);
        if (y < top) return -1;
        var idx = (int)((y - top) / RowHeight);
        return idx >= 0 && idx < trackCount ? idx : -1;
    }

    /// <summary>Y 座標がサマリー行の帯の中にあるか。</summary>
    public bool IsInSummaryRow(double y) => y >= SummaryRowTop && y < SummaryRowTop + RowHeight;

    /// <summary>Y 座標がルーラー（常に最上段・固定）の中にあるか。</summary>
    public bool IsInRuler(double y) => y < RulerHeight;

    // ── ヒットテスト ────────────────────────────────────────────

    /// <summary>座標からキー◆を特定する（見つからなければ null）。</summary>
    /// <param name="tracks">クリップの全トラック。</param>
    /// <param name="x">キャンバス X 座標。</param>
    /// <param name="y">表示 Y 座標。</param>
    /// <param name="hitRadius">ヒット判定半径（ピクセル）。</param>
    public AnimKeyRef? HitTestKey(IReadOnlyList<AnimTrack> tracks, double x, double y, double hitRadius)
    {
        for (int ti = 0; ti < tracks.Count; ti++)
        {
            if (Math.Abs(y - TrackRowCenterY(ti)) > hitRadius) continue;
            var keys = tracks[ti].Keys;
            for (int ki = 0; ki < keys.Count; ki++)
                if (Math.Abs(x - TimeToX(keys[ki].Time)) <= hitRadius)
                    return new AnimKeyRef(ti, ki);
        }
        return null;
    }

    /// <summary>
    /// 矩形（ラバーバンド）に入るキーをすべて拾う。
    /// 判定は◆の中心が矩形内にあるかどうか（Blender と同じく中心基準）。
    /// 矩形は始点・終点の順序を問わない（内部で正規化する）。
    /// </summary>
    /// <param name="tracks">クリップの全トラック。</param>
    /// <param name="ax">矩形の一方の角 X。</param>
    /// <param name="ay">矩形の一方の角 Y。</param>
    /// <param name="bx">矩形のもう一方の角 X。</param>
    /// <param name="by">矩形のもう一方の角 Y。</param>
    public List<AnimKeyRef> KeysInRect(IReadOnlyList<AnimTrack> tracks, double ax, double ay, double bx, double by)
    {
        var x0 = Math.Min(ax, bx);
        var x1 = Math.Max(ax, bx);
        var y0 = Math.Min(ay, by);
        var y1 = Math.Max(ay, by);

        var hits = new List<AnimKeyRef>();
        for (int ti = 0; ti < tracks.Count; ti++)
        {
            var cy = TrackRowCenterY(ti);
            if (cy < y0 || cy > y1) continue;
            var keys = tracks[ti].Keys;
            for (int ki = 0; ki < keys.Count; ki++)
            {
                var cx = TimeToX(keys[ki].Time);
                if (cx >= x0 && cx <= x1) hits.Add(new AnimKeyRef(ti, ki));
            }
        }
        return hits;
    }
}
