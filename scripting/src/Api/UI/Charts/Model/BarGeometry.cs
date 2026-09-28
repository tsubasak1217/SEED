using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  BarGeometry.cs — 棒の形（幅・積み上げ・角丸の輪郭。W2-8。docs/ui_charts.md §3）
//
//  - 太さ（Thickness）: 列の幅 × 割合（既定 0.7 = Flutter 版の penalty_bar_chart.dart と同じ）。下限（既定 2 dp）・列の幅を超えない
//  - 積み上げ（Stack）: 値を下から順に積んだ区間（負・NaN は 0。Wake or Pay はコインの上にカード）
//  - 空の棒の高さ（FloorValue）: 合計 0 の日も「最低の高さ」で描く（値の軸の幅 × 割合。既定 0.015 = Flutter 版の _floorFraction）
//  - 角丸の輪郭（RoundedEndOutline）: 棒の先（縦の棒は上・横の棒は右）の 2 つの角だけを丸めた多角形の点（根元は軸に接するので角のまま）。
//    半径は棒の太さの半分と長さを超えない。SEED.Draw.Polygon で塗る（凸なので耳刈りはすぐ終わる）
// ============================================================

/// <summary>棒の先の向き（角を丸める側）。</summary>
public enum BarEnd
{
    /// <summary>上（縦の棒）。</summary>
    Top = 0,
    /// <summary>右（横の棒）。</summary>
    Right = 1,
}

/// <summary>棒の形の計算（純粋な計算）。</summary>
public static class BarGeometry
{
    /// <summary>角の弧を近似する線分の数の上限（大きな角丸）。</summary>
    public const int CornerSegments = 6;
    /// <summary>角の弧と線分のずれの許容量（キャンバスの単位＝dp。1/4 dp）。</summary>
    public const float CornerTolerance = 0.25f;
    /// <summary>角を丸めない半径（これ未満は矩形のまま。1 画素ほどの角丸は見えない）。</summary>
    public const float MinCornerRadius = 0.5f;
    /// <summary>四分円の角度（ラジアン）。</summary>
    private const float QuarterTurn = MathF.PI * 0.5f;
    /// <summary>半分。</summary>
    private const float Half = 0.5f;

    /// <summary>負・NaN・無限を 0 にした値（積み上げの 1 段）。</summary>
    public static double Positive(double v) => double.IsFinite(v) && v > 0 ? v : 0;

    /// <summary>棒の太さ（列の幅 × 割合。下限 <paramref name="minThickness"/>、列の幅を超えない）。</summary>
    public static float Thickness(float slotLength, float fraction, float minThickness)
    {
        if (!(slotLength > 0)) return 0;
        float t = MathF.Max(slotLength * Math.Clamp(fraction, 0f, 1f), minThickness);
        return MathF.Min(t, slotLength);
    }

    /// <summary>
    /// 値を下から積んだ区間を <paramref name="into"/> へ入れる（(下の値, 上の値)。中身は置き換える）。負・NaN は高さ 0 の段になる。
    /// </summary>
    /// <returns>合計。</returns>
    public static double Stack(IReadOnlyList<double> values, List<(double From, double To)> into)
    {
        into.Clear();
        double top = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double v = Positive(values[i]);
            into.Add((top, top + v));
            top += v;
        }
        return top;
    }

    /// <summary>空の棒（合計 0）の高さ（値の単位。値の軸の幅 × 割合）。</summary>
    public static double FloorValue(ChartRange valueRange, float fraction) => valueRange.SafeSpan * Math.Max(0f, fraction);

    /// <summary>
    /// 先の 2 つの角だけを丸めた棒の輪郭を <paramref name="outline"/> へ入れる（中身は置き換える。時計回り・Y 下向き）。
    /// 矩形は (x0, y0)〜(x1, y1)（x0 &lt; x1・y0 &lt; y1。y0 が上）。
    /// </summary>
    /// <param name="radius">角の半径（太さの半分と長さを超えない）。0 以下なら矩形の 4 点。</param>
    /// <param name="end">丸める側。</param>
    public static void RoundedEndOutline(float x0, float y0, float x1, float y1, float radius, BarEnd end, List<Vector2> outline)
    {
        outline.Clear();
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (y1 < y0) (y0, y1) = (y1, y0);
        float w = x1 - x0, h = y1 - y0;
        // 太さの半分・長さを超えない半径
        float r = end == BarEnd.Top ? MathF.Min(radius, MathF.Min(w * Half, h)) : MathF.Min(radius, MathF.Min(h * Half, w));
        if (!(r >= MinCornerRadius))
        {
            outline.Add(new Vector2(x0, y1));
            outline.Add(new Vector2(x0, y0));
            outline.Add(new Vector2(x1, y0));
            outline.Add(new Vector2(x1, y1));
            return;
        }
        int segments = ArcSegments(r);
        if (end == BarEnd.Top)
        {
            // 左下 → 左上の角（中心 (x0+r, y0+r)・180° → 270°）→ 右上の角（中心 (x1−r, y0+r)・270° → 360°）→ 右下
            outline.Add(new Vector2(x0, y1));
            AddArc(outline, new Vector2(x0 + r, y0 + r), r, 2f * QuarterTurn, 3f * QuarterTurn, segments);
            AddArc(outline, new Vector2(x1 - r, y0 + r), r, 3f * QuarterTurn, 4f * QuarterTurn, segments);
            outline.Add(new Vector2(x1, y1));
        }
        else
        {
            // 左上 → 右上の角（中心 (x1−r, y0+r)・270° → 360°）→ 右下の角（中心 (x1−r, y1−r)・0° → 90°）→ 左下
            outline.Add(new Vector2(x0, y0));
            AddArc(outline, new Vector2(x1 - r, y0 + r), r, 3f * QuarterTurn, 4f * QuarterTurn, segments);
            AddArc(outline, new Vector2(x1 - r, y1 - r), r, 0f, QuarterTurn, segments);
            outline.Add(new Vector2(x0, y1));
        }
    }

    /// <summary>
    /// 四分円の弧を何本の線分で近似するか（弦と弧のずれ r(1 − cos(θ/2)) が CornerTolerance 以下になる最小の数。1〜CornerSegments）。
    /// 例: 半径 1 dp（2 dp の細い棒）は 2 本、半径 4 dp は 3 本。
    /// </summary>
    public static int ArcSegments(float radius)
    {
        if (!(radius > CornerTolerance)) return 1;
        float step = 2f * MathF.Acos(1f - CornerTolerance / radius);
        return Math.Clamp((int)MathF.Ceiling(QuarterTurn / step), 1, CornerSegments);
    }

    /// <summary>弧の点（開始角 → 終了角。Y 下向きなので角度が増えると時計回り）を足す。両端を含む。</summary>
    private static void AddArc(List<Vector2> outline, Vector2 center, float r, float from, float to, int segments)
    {
        for (int i = 0; i <= segments; i++)
        {
            float a = from + (to - from) * i / segments;
            outline.Add(new Vector2(center.x + MathF.Cos(a) * r, center.y + MathF.Sin(a) * r));
        }
    }
}
