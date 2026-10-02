using System;

namespace SEED.UI;

// ============================================================
//  WheelRowWindow.cs — ホイールの「どの行を作って置くか」の計算（2026-10-03。純粋な計算。docs/ui_components.md §11.3）
//
//  ホイールの列（WheelPicker）は W2-5 から、行を W2-3 の ListView で作って使い回している（列の全項目ぶんは作らない）。
//  ListView は「窓 [位置, 位置 + 窓の高さ] を前後に CacheExtent だけ広げた範囲と交わる行」だけをプレハブから作り、範囲から外れた行を
//  入ってきた行へ付け替える（ListViewLayout.VisibleRange・ListViewRecycler）。ここはホイールに固有の 2 つを決める:
//    - CacheExtent … 窓の外に前もって置く長さ。ホイールは平らな並びを円柱に巻いて映すので、窓の外（平らな距離で窓の高さの半分より先）
//      の行も曲面で内側に映る。描ける上限（WheelLook.MaxVisibleDistance。円柱の裏へ回る手前）まで ＋ 1 行の余裕を置く
//      （余裕の 1 行は、行が描ける角度へ入る前に作り終えておくため。作った行は次のフレームから使える）
//    - MaxCreatedRows … 作る行の数の上限（使い回しの入れ物の数。項目の数・周の数によらず、窓の高さ・行の高さ・見た目の値だけで決まる）
//  【言葉】行の中心と窓の中心の平らな距離 d = 行 × 行の高さ − 位置（WheelRows.Apply と同じ）。描く行は |d| < 描ける上限、
//  作る行は |d| ≦ CacheExtent ＋ 窓の高さ / 2 ＋ 行の高さ / 2（範囲と交わる行）。
//  エンジンの API に触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>ホイールの作って置く行の範囲の計算（窓の外の余裕と、作る行の数の上限）。</summary>
public static class WheelRowWindow
{
    /// <summary>半分。</summary>
    private const float Half = 0.5f;

    /// <summary>
    /// 描ける上限の先に置く余裕の行の数（行が描ける角度へ入る前に作り終えておく。作った行は次のフレームから使える）。
    /// </summary>
    public const int SpareRows = 1;

    /// <summary>
    /// 範囲の端が行の途中に掛かったときに、両端の行を数える分（長さ W の範囲と交わる行は最大 ceil(W / 行の高さ) ＋ 1）。
    /// </summary>
    private const int PartialRowAtEdges = 1;

    /// <summary>
    /// 窓の外に前もって置く長さ（ListView.CacheExtent）: 描ける上限（円柱の裏へ回る手前）まで ＋ 余裕の行。少なくとも 1 行。
    /// 以前は WheelPicker.ApplyLayout の中の式（値は同じ）。
    /// </summary>
    /// <param name="viewportExtent">窓の高さ（キャンバスの単位）。</param>
    /// <param name="itemExtent">行の高さ。</param>
    /// <param name="look">曲面の見た目の値。</param>
    public static float CacheExtent(float viewportExtent, float itemExtent, in WheelLookParams look)
    {
        float extent = WheelLoop.SanitizeExtent(itemExtent);
        float viewport = float.IsFinite(viewportExtent) ? Math.Max(0f, viewportExtent) : 0f;
        // 描ける上限の平らな距離から、窓が元から含む半分を引き、余裕の行を足す（窓の内側だけで描き切れる浅い曲面でも 1 行は置く）
        float beyondWindow = WheelLook.MaxVisibleDistance(viewport, look) - viewport * Half;
        return Math.Max(extent, beyondWindow + extent * SpareRows);
    }

    /// <summary>
    /// 行が作られる（範囲と交わる）平らな距離の上限: |d| がこれ以下の行は作って置かれる（CacheExtent ＋ 窓の半分 ＋ 行の半分）。
    /// </summary>
    /// <param name="viewportExtent">窓の高さ。</param>
    /// <param name="itemExtent">行の高さ。</param>
    /// <param name="look">曲面の見た目の値。</param>
    public static float CreatedDistance(float viewportExtent, float itemExtent, in WheelLookParams look)
    {
        float extent = WheelLoop.SanitizeExtent(itemExtent);
        float viewport = float.IsFinite(viewportExtent) ? Math.Max(0f, viewportExtent) : 0f;
        return CacheExtent(viewport, extent, look) + (viewport + extent) * Half;
    }

    /// <summary>
    /// 作る行の数の上限（使い回しの入れ物の数）: 長さ「窓の高さ ＋ 前後の CacheExtent」の範囲と交わる行の最大の数
    /// （ceil(長さ ÷ 行の高さ) ＋ 1）を、一覧の行の数で頭打ちにしたもの。項目の数・周の数（端をつなげる）によらない。
    /// </summary>
    /// <param name="viewportExtent">窓の高さ。</param>
    /// <param name="itemExtent">行の高さ。</param>
    /// <param name="look">曲面の見た目の値。</param>
    /// <param name="totalRows">一覧の行の数（項目の数 × 周の数）。</param>
    public static int MaxCreatedRows(float viewportExtent, float itemExtent, in WheelLookParams look, int totalRows)
    {
        if (totalRows <= 0) return 0;
        float extent = WheelLoop.SanitizeExtent(itemExtent);
        float viewport = float.IsFinite(viewportExtent) ? Math.Max(0f, viewportExtent) : 0f;
        double span = viewport + 2.0 * CacheExtent(viewport, extent, look);
        double rows = Math.Ceiling(span / extent) + PartialRowAtEdges;
        return (int)Math.Min(totalRows, rows);
    }

    /// <summary>
    /// 描ける行の数の上限（円柱の裏へ回らない行。|d| &lt; 描ける上限の行の最大の数 = ceil(2 × 上限 ÷ 行の高さ)）。一覧の行の数で頭打ち。
    /// 窓の高さが 0（まだ測れていない）なら 0（ListView は窓の大きさが分かるまで行を置かない）。
    /// </summary>
    /// <param name="viewportExtent">窓の高さ。</param>
    /// <param name="itemExtent">行の高さ。</param>
    /// <param name="look">曲面の見た目の値。</param>
    /// <param name="totalRows">一覧の行の数。</param>
    public static int MaxDrawnRows(float viewportExtent, float itemExtent, in WheelLookParams look, int totalRows)
    {
        if (totalRows <= 0) return 0;
        float extent = WheelLoop.SanitizeExtent(itemExtent);
        float viewport = float.IsFinite(viewportExtent) ? Math.Max(0f, viewportExtent) : 0f;
        double rows = Math.Ceiling(2.0 * WheelLook.MaxVisibleDistance(viewport, look) / extent);
        return (int)Math.Min(totalRows, rows);
    }
}
