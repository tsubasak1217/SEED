using System;
using System.Collections.Generic;
using System.Text.Json;
using SEED;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// グラフ（W2-8）の純粋な計算のテスト（docs/ui_charts.md）: 目盛りの自動（範囲 → 切りの良い刻み）・軸の範囲の自動・書式（時刻・日付・数）・
/// 座標の変換・パンとズームの範囲の制限・慣性・単調な 3 次補間（行き過ぎない）・最寄りの点と棒の列・棒の形・枠の割り付けと吹き出し・
/// 見える範囲の点と欠けた値・塊。
/// W2 の手直し P2-4: 大きさの読み方（レイアウトの大きさ・Sprite・最初のレイアウトを待つ）、日付線のハンドルの吸い付き（最寄りの値のある点）・
/// 置き場・指の X、ハンドルのトークン、プレハブ（line_chart.actor）と見本の折れ線（ui_charts.scene・ui_gallery.scene）の子 Handle の作り。
/// </summary>
public static class ChartTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-6;
    /// <summary>ハンドルの見た目の直径の範囲（dp。利用者の要望の案〈docs/backlog.md〉の 16〜20）。</summary>
    private const double HandleMinDiameter = 16, HandleMaxDiameter = 20;
    /// <summary>ハンドルの当たりの最小の大きさ（dp。Material・Android のアクセシビリティの 48。docs/input_gestures.md §6）。</summary>
    private const double HandleHitSize = 48;
    /// <summary>吹き出し・ハンドルのレイヤーの足し分（ChartView.OverlayLayerOffset と同じ 1。プレハブの初めの値）。</summary>
    private const int OverlayLayer = 1;

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h, UiThemeData theme)
    {
        // ── テーマ ────────────────────────────────────────────
        h.Add("W2-8 テーマ: 既定のテーマはグラフのトークンをすべて持つ", () =>
        {
            foreach (var token in ChartTokens.All) Check.True(theme.Has(token), $"既定のテーマに {token} がある");
            Check.Close(2.0, theme.Number(ChartTokens.SizeLine), Eps, "線の太さは Flutter 版の 2");
            Check.Close(0.7, theme.Number(ChartTokens.RatioBarWidth), Eps, "棒の太さは列の 0.7");
            Check.Equal(ChartTokens.ColorSeries4, ChartTokens.SeriesColor(9), "系列の色は足りなければ最後");
        });

        // ── 目盛りの自動（範囲 → 切りの良い刻み）────────────────
        h.Add("W2-8 目盛り: 数は 1・2・5 × 10^n の最小の刻み", () =>
        {
            Check.Close(500, ChartTicks.NiceStep(1340, 4), Eps, "0〜1,340 を 4 区間以下 → 500");
            Check.Close(2, ChartTicks.NiceStep(10, 5), Eps, "幅 10 を 5 区間 → 2");
            Check.Close(50, ChartTicks.NiceStep(100, 4), Eps, "幅 100 を 4 区間（25 は 1・2・5 でない）→ 50");
            Check.Close(0.5, ChartTicks.NiceStep(0.9, 4), Eps, "小さい幅 → 0.5");
            Check.Close(1, ChartTicks.NiceStep(0, 4), Eps, "幅 0 → 1");
            Check.Close(1, ChartTicks.NiceStep(double.NaN, 4), Eps, "非数 → 1");
            // どの幅でも区間の数は上限以下
            var rng = new Random(8);
            for (int i = 0; i < 500; i++)
            {
                double span = Math.Pow(10, rng.NextDouble() * 8 - 3);
                int max = rng.Next(1, 10);
                double step = ChartTicks.NiceStep(span, max);
                Check.True(span / step <= max + 1e-9, $"幅 {span} を {max} 区間以下（刻み {step}）");
            }
        });

        h.Add("W2-8 目盛り: 時刻は 15・30・60・120・180・360・720 分から（Flutter 版の _clockInterval と同じ）", () =>
        {
            var steps = ChartTicks.TimeOfDaySteps;
            Check.Close(30, ChartTicks.StepFromCandidates(120, 4, steps), Eps, "最小の幅 2 時間 → 30 分");
            Check.Close(120, ChartTicks.StepFromCandidates(300, 4, steps), Eps, "5 時間 → 2 時間");
            Check.Close(360, ChartTicks.StepFromCandidates(1440, 4, steps), Eps, "1 日 → 6 時間");
            Check.Close(720, ChartTicks.StepFromCandidates(1500, 4, steps), Eps, "1 日を超える → 半日（Flutter 版の 720）");
            Check.Close(1440, ChartTicks.StepFromCandidates(5000, 4, steps), Eps, "どれでも多すぎる → 最後の候補の切りの良い倍数");
            Check.Close(61, ChartTicks.StepFromCandidates(365, 6, ChartTicks.DateSteps), Eps, "1 年を 6 区間 → 約 2 か月");
            Check.Close(7, ChartTicks.StepFromCandidates(30, 6, ChartTicks.DateSteps), Eps, "30 日を 6 区間 → 1 週");
            Check.Close(1, ChartTicks.StepFromCandidates(6, 6, ChartTicks.DateSteps), Eps, "6 日を 6 区間 → 1 日");
            Check.Close(1, ChartTicks.StepFromCandidates(0, 4, ChartTicks.DateSteps), Eps, "幅 0 → 最初の候補");
        });

        h.Add("W2-8 目盛り: 位置は起点 + 刻みの倍数のうち範囲の中（両端を含む・丸めの誤差を寄せる）", () =>
        {
            var into = new List<double>();
            Check.Equal(6, ChartTicks.Generate(new ChartRange(415, 575), 30, 0, into), "7:00 前〜9:35 の 30 分刻み");
            Check.Close(420, into[0], Eps, "最初は 7:00");
            Check.Close(570, into[^1], Eps, "最後は 9:30");
            ChartTicks.Generate(new ChartRange(0, 1), 0.1, 0, into);
            Check.Equal(11, into.Count, "0〜1 の 0.1 刻みは両端を含めて 11");
            Check.Close(0.3, into[3], 1e-12, "丸めの誤差（0.30000000000000004）は刻みの倍数へ寄せる");
            // 日付: 起点（データの最初の日）からの倍数（パンで跳ねない）
            ChartTicks.Generate(new ChartRange(739000.5, 739030.5), 7, 738990, into);
            Check.Close(739004, into[0], Eps, "起点 738990 から 7 日ごと");
            Check.Equal(0, ChartTicks.Generate(new ChartRange(0, 1), 0, 0, into), "刻み 0 は空");
            Check.True(ChartTicks.Generate(new ChartRange(0, 1e9), 1, 0, into, 64) == 64, "上限で止まる");
            var nice = ChartTicks.NiceBounds(new ChartRange(3, 1340), 500);
            Check.Close(0, nice.Min, Eps, "下は切り下げ");
            Check.Close(1500, nice.Max, Eps, "上は切り上げ");
            Check.Equal(6, ChartTicks.IntervalsForLength(300, 48), "横 300 dp・文字の間隔 48 dp → 6 区間");
            Check.Equal(1, ChartTicks.IntervalsForLength(10, 48), "少なくとも 1");
        });

        // ── 軸の範囲の自動 ────────────────────────────────────
        h.Add("W2-8 軸の範囲: 時刻は上下 30 分の余白・最小 2 時間・0〜24 時へ幅を保って収める", () =>
        {
            var o = new AutoRangeOptions { Padding = 30, MinSpan = 120, ClampMin = 0, ClampMax = 1440, Empty = new ChartRange(240, 720) };
            var r = ChartAutoRange.FromValues(new double[] { 420, 435, 450 }, o);
            Check.Close(375, r.Min, Eps, "7:00〜7:30 → 中央から 2 時間");
            Check.Close(495, r.Max, Eps, "…8:15");
            r = ChartAutoRange.FromValues(new double[] { 360, 600 }, o);
            Check.Close(330, r.Min, Eps, "幅が十分なら余白だけ");
            Check.Close(630, r.Max, Eps, "");
            r = ChartAutoRange.FromValues(new double[] { 5, 20 }, o);
            Check.Close(0, r.Min, Eps, "0:05 の朝: 下へはみ出した分を上へずらす（Flutter 版は切って幅 72 分になった）");
            Check.Close(120, r.Max, Eps, "幅 2 時間を保つ");
            r = ChartAutoRange.FromValues(new double[] { 1430 }, o);
            Check.Close(1320, r.Min, Eps, "23:50 → 上へはみ出した分を下へずらす");
            Check.Close(1440, r.Max, Eps, "");
            r = ChartAutoRange.FromValues(Array.Empty<double>(), o);
            Check.Close(240, r.Min, Eps, "記録が無ければ 4:00〜12:00（Flutter 版と同じ）");
            r = ChartAutoRange.FromValues(new[] { double.NaN, 450 }, o);
            Check.Close(390, r.Min, Eps, "非数は読まない");
        });

        h.Add("W2-8 軸の範囲: 棒は 0 から（0 の側に余白を足さない）・全部 0 でも幅がある", () =>
        {
            var o = new AutoRangeOptions { PaddingFraction = 0.12, IncludeZero = true, ClampMin = double.NaN, ClampMax = double.NaN, Empty = new ChartRange(0, 1) };
            var r = ChartAutoRange.FromValues(new double[] { 3, 12, 0 }, o);
            Check.Close(0, r.Min, Eps, "0 から");
            Check.Close(13.44, r.Max, Eps, "上に 12% の余白（Flutter 版の worst × 1.12）");
            r = ChartAutoRange.FromValues(new double[] { 0, 0 }, o);
            Check.Close(0, r.Min, Eps, "全部 0");
            Check.True(r.Max > r.Min, "幅 0 にしない");
            r = ChartAutoRange.FromValues(new double[] { -4, -1 }, o);
            Check.Close(0, r.Max, Eps, "負だけなら 0 が上の端");
        });

        // ── 書式 ──────────────────────────────────────────────
        h.Add("W2-8 書式: 時刻 H:mm・HH:mm・24:00、日付 M/d、数の 3 桁区切りと小数の桁", () =>
        {
            Check.Equal("7:30", ChartFormat.TimeOfDay(450), "7:30");
            Check.Equal("07:05", ChartFormat.TimeOfDay(425, padHour: true), "ゼロ詰め");
            Check.Equal("0:00", ChartFormat.TimeOfDay(-5), "負は 0:00");
            Check.Equal("24:00", ChartFormat.TimeOfDay(1440), "1440 は 24:00（23:59 にしない）");
            Check.Equal("24:00", ChartFormat.TimeOfDay(1439.6), "丸めて 24:00");
            Check.Equal("-", ChartFormat.TimeOfDay(double.NaN), "非数");
            Check.Equal("9/28", ChartFormat.Date(new DateOnly(2026, 9, 28).DayNumber), "日付");
            Check.Equal("1/1", ChartFormat.Date(new DateOnly(2027, 1, 1).DayNumber + 0.4), "日の番号は丸める");
            Check.Equal("-", ChartFormat.Date(-1), "範囲の外");
            Check.Equal("1,234,567", ChartFormat.Number(1234567, 1000), "3 桁区切り");
            Check.Equal("0.25", ChartFormat.Number(0.25, 0.25), "刻み 0.25 → 2 桁");
            Check.Equal("1.5", ChartFormat.Number(1.5, 0.5), "刻み 0.5 → 1 桁");
            Check.Equal("0", ChartFormat.Number(-1e-12, 5), "-0 にしない");
            Check.Equal(0, ChartFormat.DecimalsFor(5), "刻み 5 → 0 桁");
            Check.Equal(1, ChartFormat.DecimalsFor(0.1), "刻み 0.1 → 1 桁（浮動小数の誤差を許す）");
            Check.Equal("6:30", ChartFormat.Format(ChartValueFormat.TimeOfDay, 390, 30), "書式で選ぶ");
        });

        // ── 座標の変換 ────────────────────────────────────────
        h.Add("W2-8 座標: 値 ⇔ 位置（X は左 → 右、Y は下 → 上・逆も）の往復", () =>
        {
            var m = new ChartMapping(new ChartRange(0, 10), new ChartRange(0, 100), 200, 50);
            Check.Close(100, m.XToLocal(5), Eps, "X の真ん中");
            Check.Close(0, m.YToLocal(100), Eps, "Y の最大は上");
            Check.Close(50, m.YToLocal(0), Eps, "Y の最小は下");
            Check.Close(7.5, m.LocalToX(m.XToLocal(7.5)), 1e-5, "X の往復");
            Check.Close(33, m.LocalToY(m.YToLocal(33)), 1e-5, "Y の往復");
            var inv = new ChartMapping(new ChartRange(0, 10), new ChartRange(0, 100), 200, 50, invertY: true);
            Check.Close(50, inv.YToLocal(100), Eps, "逆なら最大は下");
            Check.Close(20, m.XUnitLength, Eps, "X の 1 単位は 20");
            var degenerate = new ChartMapping(new ChartRange(5, 5), new ChartRange(1, 1), 100, 100);
            Check.True(float.IsFinite(degenerate.XToLocal(6)) && float.IsFinite(degenerate.YToLocal(2)), "幅 0 の範囲でも非数にしない");
        });

        // ── パンとズーム ──────────────────────────────────────
        h.Add("W2-8 パンとズーム: 倍率 1〜6・端で止まる・フォーカスの下の値は動かない", () =>
        {
            var v = new ChartViewport();
            v.SetFull(new ChartRange(0, 365), ViewportAnchor.Reset);
            Check.Close(365, v.VisibleSpan, Eps, "倍率 1 は全体");
            Check.True(!v.CanPan && !v.PanBy(10), "全体が見えているときは動かない");
            Check.True(v.ZoomAround(2, 0.5), "2 倍");
            Check.Close(91.25, v.Start, Eps, "真ん中（182.5）の下は動かない");
            Check.True(v.PanByLocal(100, 300), "指を右へ 100（面の幅 300）");
            Check.Close(91.25 - 100 * 182.5 / 300, v.Start, 1e-6, "前の値が見える向きへ");
            v.PanByLocal(10000, 300);
            Check.True(v.AtStart && v.Start == 0, "左の端で止まる");
            v.PanBy(1e9);
            Check.True(v.AtEnd, "右の端で止まる");
            Check.Close(365 - 182.5, v.Start, 1e-6, "");
            v.ZoomAround(100, 0.5);
            Check.Close(6, v.Zoom, Eps, "上限 6 倍");
            v.ZoomAround(0.2, 0.5);
            Check.Close(1, v.Zoom, Eps, "下限 1 倍");
            Check.Close(0, v.Start, Eps, "全体へ戻ると左端は 0");
        });

        h.Add("W2-8 パンとズーム: ピンチ（始めのフォーカスの値を今のフォーカスへ）・範囲を見せる・データが増えたら右端に付く", () =>
        {
            var v = new ChartViewport();
            v.SetFull(new ChartRange(0, 365), ViewportAnchor.Reset);
            // ピンチ: 値 100 を見える範囲の 1/4 の位置へ、倍率 4
            v.ApplyZoom(4, 100, 0.25);
            Check.Close(91.25, v.VisibleSpan, Eps, "4 倍");
            Check.Close(100 - 91.25 * 0.25, v.Start, 1e-6, "値 100 が 1/4 の位置");
            // 2 本指のパン: 同じ倍率でフォーカスだけ動く
            v.ApplyZoom(4, 100, 0.75);
            Check.Close(100 - 91.25 * 0.75, v.Start, 1e-6, "フォーカスを右へ動かすと中身も右へ");
            v.Show(new ChartRange(300, 330));
            Check.Close(6, v.Zoom, Eps, "30 日だけ見せたくても上限の 6 倍");
            Check.Close(300 + (30 - 365 / 6.0) / 2, v.Start, 1e-6, "範囲の真ん中が真ん中");
            v.Show(new ChartRange(275, 365));
            Check.True(v.AtEnd, "右の端へ寄る");
            Check.Close(90, v.VisibleSpan, 1e-6, "90 日（4.06 倍）");
            v.SetFull(new ChartRange(0, 366), ViewportAnchor.StickToEnd);
            Check.True(v.AtEnd, "右端を見ていたら、1 日増えても右端");
            Check.Close(90, v.VisibleSpan, 1e-6, "見えていた幅は保つ（倍率の上下限の中なら）");
            v.Show(new ChartRange(300, 366));
            v.SetFull(new ChartRange(0, 400), ViewportAnchor.StickToEnd);
            Check.Close(400 / 6.0, v.VisibleSpan, 1e-6, "幅を保つと上限（6 倍）を超えるなら上限の幅");
            v.PanBy(-100);
            v.SetFull(new ChartRange(0, 367), ViewportAnchor.StickToEnd);
            Check.True(!v.AtEnd, "右端を見ていなければ左端を保つ");
            v.SetZoomLimits(1, 3);
            Check.Close(3, v.Zoom, Eps, "上限を下げると今の倍率も収める");
            v.SetZoomLimits(0.1, double.NaN);
            Check.Close(1, v.MinZoom, Eps, "下限は 1 以上");
        });

        h.Add("W2-8 パンとズーム: float の往復で 1.0000001 倍になっても全体が見えているならパンしない", () =>
        {
            var v = new ChartViewport();
            v.SetFull(new ChartRange(0, 364), ViewportAnchor.Reset);
            v.ApplyZoom(Math.Exp((float)Math.Log(1.0)) * 1.0000001, 182, 0.5);
            Check.True(!v.CanPan, "ほぼ 1 倍はパンできない（ドラッグを親へ渡す）");
            v.ApplyZoom(1.01, 182, 0.5);
            Check.True(v.CanPan, "1.01 倍ならパンできる");
        });

        h.Add("W2-8 見た目: テーマの値を読む（線の太さ・棒の割合・系列の色・差し替えたテーマ）", () =>
        {
            var look = ChartLook.From(theme);
            Check.Close(theme.Number(ChartTokens.SizeLine), look.Line, Eps, "線の太さ");
            Check.Close(0.7, look.BarWidth, 1e-6, "棒の割合");
            Check.Equal(4, look.YIntervals, "縦軸の区間の上限");
            Check.Close(theme.Color(ChartTokens.ColorSeries2).r, look.SeriesColor(1).r, Eps, "系列 2 の色");
            Check.Close(theme.Color(ChartTokens.ColorSeries4).r, look.SeriesColor(99).r, Eps, "足りなければ最後の色");
            var custom = UiThemeData.Parse("{\"size\":{\"chart_line\":3},\"color\":{\"chart_series_1\":\"#FF0000\"}}", theme, out _);
            var look2 = ChartLook.From(custom);
            Check.Close(3, look2.Line, Eps, "差し替えた線の太さ");
            Check.Close(1, look2.SeriesColor(0).r, Eps, "差し替えた系列の色（線形でも赤は 1）");
        });

        h.Add("W2-8 慣性: 摩擦で減速（FrictionSimulation の式）・止まる時刻・フレームの刻みに依らない", () =>
        {
            var f = new ChartFling(0, 1000, 0.135, 20);
            Check.Close(Math.Log(20.0 / 1000) / Math.Log(0.135), f.Duration, 1e-9, "止まる時刻");
            double end = f.PositionAt(f.Duration);
            Check.Close(1000 * (0.02 - 1) / Math.Log(0.135), end, 1e-6, "止まるまでの距離（約 489）");
            Check.True(f.PositionAt(0.1) < f.PositionAt(0.2) && f.PositionAt(0.2) < end, "進み続ける");
            Check.True(f.VelocityAt(0.5) < f.VelocityAt(0.1), "減速する");
            Check.Close(end, f.PositionAt(99), Eps, "止まった後は動かない");
            Check.Close(0, f.VelocityAt(99), Eps, "");
            // 60 fps と 30 fps で同じ時刻は同じ位置（閉じた式）
            double at60 = 0, at30 = 0;
            for (int i = 1; i <= 30; i++) at60 = f.PositionAt(i / 60.0);
            for (int i = 1; i <= 15; i++) at30 = f.PositionAt(i / 30.0);
            Check.Close(at60, at30, 1e-9, "フレームの刻みに依らない");
            var slow = new ChartFling(5, 10, 0.135, 20);
            Check.True(slow.IsDone(0) && slow.PositionAt(1) == 5, "止まる速さより遅ければ動かない");
            var neg = new ChartFling(0, -1000, 0.135, 20);
            Check.Close(-end, neg.PositionAt(neg.Duration), 1e-6, "左向きも同じ距離");
        });

        // ── 単調な 3 次補間 ───────────────────────────────────
        h.Add("W2-8 滑らかな曲線: 点を通り、点と点の間で行き過ぎない（単調な区間は単調のまま）", () =>
        {
            var cases = new[]
            {
                new[] { V(0, 0), V(1, 10), V(2, 10), V(3, 0), V(4, 5) },
                new[] { V(0, 0), V(1, 0), V(2, 1), V(3, 1) },           // 段（Catmull-Rom なら行き過ぎる）
                new[] { V(0, 420), V(1, 425), V(3, 600), V(4, 430), V(10, 440) }, // 起床時間（欠けた日を飛ばした間隔）
            };
            var rng = new Random(28);
            var random = new Vector2[40];
            float x = 0;
            for (int i = 0; i < random.Length; i++) { x += 0.2f + (float)rng.NextDouble() * 3; random[i] = V(x, (float)rng.NextDouble() * 100); }
            foreach (var pts in new List<Vector2[]>(cases) { random })
            {
                var outp = new List<Vector2>();
                MonotoneCubic.Sample(pts, 0.05f, 400, outp);
                // 元の点をすべて通る
                int k = 0;
                foreach (var p in outp) if (k < pts.Length && p.x == pts[k].x && p.y == pts[k].y) k++;
                Check.Equal(pts.Length, k, "元の点をすべて通る");
                // 区間ごとに、曲線の点は両端の値の間
                int seg = 0;
                foreach (var p in outp)
                {
                    while (seg < pts.Length - 2 && p.x > pts[seg + 1].x) seg++;
                    float lo = MathF.Min(pts[seg].y, pts[seg + 1].y), hi = MathF.Max(pts[seg].y, pts[seg + 1].y);
                    Check.True(p.y >= lo - 1e-3f && p.y <= hi + 1e-3f, $"x={p.x} の {p.y} は {lo}〜{hi} の間（行き過ぎない）");
                }
            }
            Span<float> m = stackalloc float[5];
            MonotoneCubic.Tangents(cases[0], m);
            Check.Close(0, m[1], Eps, "山（増える → 平ら）の接線は 0");
            Check.Close(0, m[3], Eps, "谷の接線は 0");
            Check.Equal(1, MonotoneCubic.Subdivisions(3, 4, 16), "横に詰まった区間は分けない");
            Check.Equal(16, MonotoneCubic.Subdivisions(1000, 4, 16), "上限");
            var two = new List<Vector2>();
            MonotoneCubic.Sample(new[] { V(0, 0), V(10, 10) }, 1, 100, two);
            Check.Equal(2, two.Count, "2 点は直線のまま");
            // 横に 1.2 ずつの点が縦に大きく揺れても、横の刻み 2 より詰まっていれば点は増えない（365 日を縮めて見たとき）
            var dense = new Vector2[365];
            for (int i = 0; i < dense.Length; i++) dense[i] = V(i * 1.2f, (i % 2) * 40f);
            var denseOut = new List<Vector2>();
            MonotoneCubic.Sample(dense, 2, 16, denseOut);
            Check.Equal(dense.Length, denseOut.Count, "詰まった点は分けない");
        });

        // ── 当たり ────────────────────────────────────────────
        h.Add("W2-8 当たり: 最寄りの点（横の距離・同じなら縦）・棒の列（列の幅の中なら縦は問わない）", () =>
        {
            var pts = new[] { V(0, 50), V(10, 80), V(20, 10), V(20, 60) };
            Check.Equal(1, ChartHit.Nearest(pts, V(13, 0), 24), "横 3 の点");
            Check.Equal(3, ChartHit.Nearest(pts, V(20, 55), 24), "横が同じなら縦が近い方");
            Check.Equal(-1, ChartHit.Nearest(pts, V(100, 0), 24), "遠ければ無し");
            Check.Equal(-1, ChartHit.Nearest(ReadOnlySpan<Vector2>.Empty, V(0, 0), 24), "空");
            var xs = new double[] { 1, 2, 3, 5 };
            Check.Equal(3, ChartHit.NearestX(xs, 4.6, 0.5), "5 の列（距離 0.4）");
            Check.Equal(-1, ChartHit.NearestX(xs, 4.0, 0.5), "列の間は無し");
            Check.Equal(0, ChartHit.NearestX(xs, 0.6, 0.5), "先頭");
            Check.Equal(3, ChartHit.SlotAt(3.4, 0, 1, 10), "列 3");
            Check.Equal(4, ChartHit.SlotAt(3.6, 0, 1, 10), "列 4");
            Check.Equal(-1, ChartHit.SlotAt(-0.6, 0, 1, 10), "外");
            Check.Equal(-1, ChartHit.SlotAt(9.6, 0, 1, 10), "外");
        });

        // ── 棒の形 ────────────────────────────────────────────
        h.Add("W2-8 棒: 太さ（割合・下限・列の幅まで）・積み上げ（負は 0）・先だけ角丸の輪郭", () =>
        {
            Check.Close(7, BarGeometry.Thickness(10, 0.7f, 2), Eps, "列 10 の 0.7");
            Check.Close(2, BarGeometry.Thickness(2.5f, 0.7f, 2), Eps, "下限 2");
            Check.Close(1, BarGeometry.Thickness(1, 0.7f, 2), Eps, "列の幅を超えない");
            var stack = new List<(double From, double To)>();
            Check.Close(5, BarGeometry.Stack(new double[] { 3, -1, 2 }, stack), Eps, "合計（負は 0）");
            Check.Close(3, stack[1].From, Eps, "負の段は高さ 0");
            Check.Close(5, stack[2].To, Eps, "最後の段の上 = 合計");
            Check.Close(0.015 * 200, BarGeometry.FloorValue(new ChartRange(0, 200), 0.015f), Eps, "空の棒の高さ");
            var outline = new List<Vector2>();
            BarGeometry.RoundedEndOutline(10, 20, 20, 100, 4, BarEnd.Top, outline);
            Check.Equal(2 + 2 * (BarGeometry.ArcSegments(4) + 1), outline.Count, "根元 2 点 + 角 2 つ");
            Check.Equal(2, BarGeometry.ArcSegments(1), "細い棒（半径 1）の角は 2 本");
            Check.Equal(3, BarGeometry.ArcSegments(4), "半径 4 の角は 3 本");
            Check.Equal(BarGeometry.CornerSegments, BarGeometry.ArcSegments(1000), "上限");
            var thin = new List<Vector2>();
            BarGeometry.RoundedEndOutline(0, 0, 0.8f, 50, 4, BarEnd.Top, thin);
            Check.Equal(4, thin.Count, "半径 0.4（0.5 未満）は矩形のまま");
            foreach (var p in outline) Check.True(p.x >= 10 - 1e-4 && p.x <= 20 + 1e-4 && p.y >= 20 - 1e-4 && p.y <= 100 + 1e-4, $"{p} は棒の中");
            Check.True(outline[0].x == 10 && outline[0].y == 100 && outline[^1].x == 20 && outline[^1].y == 100, "根元は角のまま");
            BarGeometry.RoundedEndOutline(0, 98, 10, 100, 4, BarEnd.Top, outline);
            foreach (var p in outline) Check.True(p.y >= 98 - 1e-4, "短い棒は半径を長さまで縮める");
            BarGeometry.RoundedEndOutline(0, 0, 50, 10, 0, BarEnd.Right, outline);
            Check.Equal(4, outline.Count, "半径 0 は矩形");
        });

        // ── 枠の割り付けと吹き出し ──────────────────────────────
        h.Add("W2-8 枠: 軸の文字の列・行と余白を除いた面、吹き出しは点の上（入らなければ下）・左右は枠の中", () =>
        {
            var plot = ChartLayout.Plot(300, 200, new ChartFrameSpec { YAxisWidth = 46, XAxisHeight = 26, PadTop = 8, PadRight = 8 });
            Check.Close(46, plot.X, Eps, "左");
            Check.Close(8, plot.Y, Eps, "上");
            Check.Close(246, plot.Width, Eps, "幅");
            Check.Close(166, plot.Height, Eps, "高さ");
            var tiny = ChartLayout.Plot(20, 20, new ChartFrameSpec { YAxisWidth = 46, XAxisHeight = 26 });
            Check.True(tiny.Width == 0 && tiny.Height == 0, "小さすぎれば 0（負にしない）");
            var bounds = new ChartRect(0, 0, 300, 200);
            var size = V(80, 30);
            var pos = ChartLayout.TooltipPosition(V(150, 100), size, bounds, 10);
            Check.Close(110, pos.x, Eps, "真ん中に合わせる");
            Check.Close(60, pos.y, Eps, "点の上");
            pos = ChartLayout.TooltipPosition(V(150, 20), size, bounds, 10);
            Check.Close(30, pos.y, Eps, "上に入らなければ下");
            pos = ChartLayout.TooltipPosition(V(295, 100), size, bounds, 10);
            Check.Close(220, pos.x, Eps, "右の端で収める");
            pos = ChartLayout.TooltipPosition(V(2, 100), size, bounds, 10);
            Check.Close(0, pos.x, Eps, "左の端で収める");
        });

        // ── 見える範囲の点・欠けた値・塊 ─────────────────────────
        h.Add("W2-8 線の点: 見える範囲 ± 1 点・欠けた値はつなぐ（または切る）・塊は 1 点重ねる", () =>
        {
            var series = new List<ChartPoint>();
            for (int i = 0; i < 10; i++) series.Add(new ChartPoint(i, i == 4 ? null : i * 10));
            var map = new ChartMapping(new ChartRange(2.5, 6.5), new ChartRange(0, 100), 400, 100);
            var pts = new List<Vector2>();
            var runs = new List<int>();
            var src = new List<int>();
            LinePath.Build(series, map, GapMode.Connect, pts, runs, src);
            Check.Equal(1, runs.Count, "つなぐ: 区間は 1 つ");
            Check.Equal("2,3,5,6,7", string.Join(",", src), "見える範囲の外の 1 点ずつ（2 と 7）を含め、欠けた 4 は飛ばす");
            Check.Close(map.XToLocal(2), pts[0].x, Eps, "外側の点は面の外の位置");
            LinePath.Build(series, map, GapMode.Break, pts, runs, src);
            Check.Equal(2, runs.Count, "切る: 区間は 2 つ");
            Check.Equal(2, LinePath.RunLength(runs, pts.Count, 0), "2・3");
            Check.Equal(3, LinePath.RunLength(runs, pts.Count, 1), "5・6・7");
            // 左の外側の点が欠けていたら、さらに外の値のある点まで伸ばす
            series[1] = new ChartPoint(1, null);
            series[2] = new ChartPoint(2, null);
            LinePath.Build(series, map, GapMode.Connect, pts, runs, src);
            Check.Equal(0, src[0], "外側の値のある点（0）まで");
            LinePath.Build(new List<ChartPoint>(), map, GapMode.Connect, pts, runs, src);
            Check.True(pts.Count == 0 && runs.Count == 0, "空");
            Check.Equal(1, LinePath.ChunkCount(1024, 1024), "上限ちょうどは 1 つ");
            Check.Equal(2, LinePath.ChunkCount(1025, 1024), "1 点超えたら 2 つ");
            Check.Equal(3, LinePath.ChunkCount(2048, 1024), "2048 点は 1023 ずつ進んで 3 つ");
            var c1 = LinePath.Chunk(1025, 1024, 1);
            Check.True(c1.Start == 1023 && c1.Length == 2, "2 つ目は 1 点重ねて 1023 から");
        });

        h.Add("W2-8 軸: 書式ごとの刻みの候補・目盛りの文字・候補の文字の読み方", () =>
        {
            var axis = new ChartAxis { Format = ChartValueFormat.TimeOfDay };
            axis.Compute(new ChartRange(375, 495), 4);
            Check.Close(30, axis.Step, Eps, "2 時間の幅を 4 区間以下 → 30 分");
            Check.Equal("6:30,7:00,7:30,8:00", string.Join(",", axis.Values.ConvertAll(axis.Label)), "目盛りの文字");
            var date = new ChartAxis { Format = ChartValueFormat.Date, Origin = new DateOnly(2026, 9, 1).DayNumber };
            date.Compute(new ChartRange(new DateOnly(2026, 9, 1).DayNumber, new DateOnly(2026, 9, 30).DayNumber), 5);
            Check.Close(7, date.Step, Eps, "29 日を 5 区間以下 → 1 週");
            Check.Equal("9/1,9/8,9/15,9/22,9/29", string.Join(",", date.Values.ConvertAll(date.Label)), "最初の日から 1 週ごと");
            var num = new ChartAxis { Steps = ChartAxis.ParseSteps("5, 25,abc,-1,100") };
            num.Compute(new ChartRange(0, 180), 4);
            Check.Close(100, num.Step, Eps, "候補（5・25・100）から");
            Check.True(ChartAxis.ParseSteps("  ") == null, "空は null");
            var custom = new ChartAxis { Formatter = (v, s) => $"{v}円" };
            Check.Equal("5円", custom.Label(5), "文字を独自に");
        });

        // ── W2 の手直し P2-4: 大きさの読み方 ─────────────────────
        h.Add("P2-4 大きさ: レイアウトの表にあれば LayoutSize・無ければ Sprite（壊れた値も Sprite）", () =>
        {
            var sprite = V(508, 170);
            var laid = V(379, 170);
            Check.True(ChartSizing.Choose(true, laid, sprite) == laid, "表にある: コンテナに伸ばされた大きさ（411 dp の画面の 379）");
            Check.True(ChartSizing.Choose(false, laid, sprite) == sprite, "表に無い（最初のフレーム・3D ワールドキャンバス）: Sprite");
            Check.True(ChartSizing.Choose(false, Vector2.Zero, sprite) == sprite, "表に無いときの LayoutSize（0）は使わない");
            Check.True(ChartSizing.Choose(true, V(0, 0), sprite) == V(0, 0), "表にあって 0（畳まれた）: 0 のまま（描かない）");
            Check.True(ChartSizing.Choose(true, V(float.NaN, 170), sprite) == sprite, "非数は Sprite");
            Check.True(ChartSizing.Choose(true, V(float.PositiveInfinity, 170), sprite) == sprite, "無限は Sprite");
            Check.True(ChartSizing.Choose(true, V(-1, 170), sprite) == sprite, "負は Sprite");
        });

        h.Add("P2-4 大きさ: 最初のレイアウトを読めるまで描かない（上限のフレームを超えたら描く・一度描いたら戻らない）", () =>
        {
            const int max = ChartSizing.DefaultMaxLayoutWaitFrames;
            // 普通: 最初のフレームは表に無く、次のフレームで読める
            var wait = new ChartLayoutWait();
            Check.True(!wait.Step(false, max), "1 フレーム目（表に無い）は描かない");
            Check.True(wait.Step(true, max), "2 フレーム目（読めた）から描く");
            Check.True(wait.Step(false, max), "一度描いたら、後で表に無くなっても描き続ける（Sprite の大きさ）");
            // ずっと表に無い（3D ワールドキャンバスの下）: 上限まで待ってから描く
            var never = new ChartLayoutWait();
            int waited = 0;
            while (!never.Step(false, max)) waited++;
            Check.Equal(max, waited, $"最大 {max} フレーム待つ");
            Check.True(never.Ready, "その次のフレームから描く");
            // 上限 0 以下は待たない
            var none = new ChartLayoutWait();
            Check.True(none.Step(false, 0), "上限 0 は最初のフレームから描く");
            Check.True(max >= 1 && max <= 5, "上限は次のフレームの表（1）を待てて、描かない時間が短い（5 フレーム以内）");
        });

        // ── W2 の手直し P2-4: 日付線のハンドル ────────────────────
        h.Add("P2-4 ハンドルの吸い付き: X だけで最寄り・値の無い点を飛ばす・同じ距離は小さい添字・範囲の外は端の点・点 0 個・1 個", () =>
        {
            var pts = new List<ChartPoint>
            {
                new(0, 50), new(1, null), new(2, 10), new(3, 90), new(4, null), new(5, null), new(6, 70), new(8, 30),
            };
            Check.Equal(2, ChartHit.NearestValuedX(pts, 2.4, 0, 8), "X だけで最寄り（縦の値は見ない）");
            Check.Equal(0, ChartHit.NearestValuedX(pts, 0.9, 0, 8), "値の無い 1 を飛ばして 0（距離 0.9 < 1.1）");
            Check.Equal(2, ChartHit.NearestValuedX(pts, 1.2, 0, 8), "値の無い 1 を飛ばして 2（距離 0.8 < 1.2）");
            Check.Equal(3, ChartHit.NearestValuedX(pts, 4.4, 0, 8), "値の無い 4・5 を飛ばして 3（距離 1.4 < 1.6）");
            Check.Equal(6, ChartHit.NearestValuedX(pts, 4.6, 0, 8), "値の無い 4・5 を飛ばして 6（距離 1.4 < 1.6）");
            Check.Equal(0, ChartHit.NearestValuedX(pts, 1.0, 0, 8), "同じ距離（0 と 2 から 1）は小さい添字");
            Check.Equal(6, ChartHit.NearestValuedX(pts, 7.0, 0, 8), "同じ距離（6 と 8 から 1）は小さい添字");
            Check.Equal(3, ChartHit.NearestValuedX(pts, 3.0, 0, 8), "ちょうど点の上");
            // 見えている範囲 [2, 6] の外の指は端の点で止まる（範囲の外の点は選ばない）
            Check.Equal(2, ChartHit.NearestValuedX(pts, -100, 2, 6), "左の外 → 見えている左端の点（範囲の外の 0 は選ばない）");
            Check.Equal(6, ChartHit.NearestValuedX(pts, 100, 2, 6), "右の外 → 見えている右端の点（範囲の外の 8 は選ばない）");
            Check.Equal(2, ChartHit.NearestValuedX(pts, 0.1, 1.5, 6), "範囲の左の外で、範囲の外の近い点（0）より範囲の中の端の点");
            Check.Equal(6, ChartHit.NearestValuedX(pts, 7.9, 2, 7.5), "範囲の右の外で、範囲の外の近い点（8）より範囲の中の端の点");
            Check.Equal(3, ChartHit.NearestValuedX(pts, 5, 2.5, 5.5), "範囲の中に値のある点が 1 つ（3）");
            Check.Equal(-1, ChartHit.NearestValuedX(pts, 4.5, 4, 5), "範囲の中に値のある点が無い（4・5 は値なし）");
            Check.Equal(-1, ChartHit.NearestValuedX(pts, 4.5, 6.5, 7.5), "範囲の中に点が無い");
            Check.Equal(-1, ChartHit.NearestValuedX(new List<ChartPoint>(), 1, 0, 8), "点 0 個");
            var one = new List<ChartPoint> { new(5, 42) };
            Check.Equal(0, ChartHit.NearestValuedX(one, -3, 0, 10), "点 1 個（どこを指しても）");
            Check.Equal(0, ChartHit.NearestValuedX(one, 99, 0, 10), "点 1 個（右の外）");
            Check.Equal(-1, ChartHit.NearestValuedX(one, 5, 6, 10), "点 1 個が範囲の外");
            Check.Equal(-1, ChartHit.NearestValuedX(new List<ChartPoint> { new(5, null) }, 5, 0, 10), "値の無い点 1 個");
            Check.Equal(-1, ChartHit.NearestValuedX(pts, double.NaN, 0, 8), "非数の指");
            Check.Equal(-1, ChartHit.NearestValuedX(pts, 3, 6, 2), "逆の範囲");
            // 同じ X の点が並ぶ（値あり・なし）: 小さい添字
            var dup = new List<ChartPoint> { new(1, 5), new(3, 1), new(3, 2), new(3, null), new(6, 9) };
            Check.Equal(1, ChartHit.NearestValuedX(dup, 3.4, 0, 10), "同じ X が並べば小さい添字（右から）");
            Check.Equal(1, ChartHit.NearestValuedX(dup, 2.9, 0, 10), "同じ X が並べば小さい添字（左から）");
            Check.Equal(1, ChartHit.NearestValuedX(dup, 4.4, 0, 10), "左の候補が同じ X の並びなら小さい添字まで戻る");
            // 指を右へ小刻みに送ると 1 点ずつ変わる（30 日の見本と同じく 7 日に 1 日〈3・10・17・24〉記録が無い）
            var days = new List<ChartPoint>();
            for (int d = 0; d < 30; d++) days.Add(new ChartPoint(d, d % 7 == 3 ? null : 420 + d));
            var seen = new List<int>();
            for (double x = 11; x <= 15; x += 0.25)
            {
                int k = ChartHit.NearestValuedX(days, x, 0, 29);
                if (seen.Count == 0 || seen[^1] != k) seen.Add(k);
            }
            Check.Equal("11,12,13,14,15", string.Join(",", seen), "記録のある日を 1 つずつ（11〜15 に欠けた日なし）");
            seen.Clear();
            for (double x = 15; x <= 19; x += 0.25)
            {
                int k = ChartHit.NearestValuedX(days, x, 0, 29);
                if (seen.Count == 0 || seen[^1] != k) seen.Add(k);
            }
            Check.Equal("15,16,18,19", string.Join(",", seen), "欠けた日（17）は飛ばす");
        });

        h.Add("P2-4 ハンドルの置き場: 丸の中心の X = 日付線・丸の下端 = 面の下の縁・面の中の判定（端の半単位）・指の X", () =>
        {
            // 見本の WakeWeek（508×170・縦軸の列 46・横軸の行 26・余白 8）の面
            var plot = ChartLayout.Plot(508, 170, new ChartFrameSpec { YAxisWidth = 46, XAxisHeight = 26, PadTop = 8, PadRight = 8 });
            var pos = ChartLayout.HandlePosition(plot, 100, 18);
            Check.Close(46 + 100 - 9, pos.x, Eps, "左 = 面の左 + 日付線の X − 半径（中心の X = 日付線）");
            Check.Close(plot.Bottom - 18, pos.y, Eps, "上 = 面の下端 − 直径（丸の下端 = 面の下の縁 = 横軸の線）");
            Check.True(pos.y + 18 <= plot.Bottom + 1e-4, "横軸の文字の行（面の下）に重ならない");
            var edge = ChartLayout.HandlePosition(plot, 0, 18);
            Check.Close(plot.X - 9, edge.x, Eps, "面の左端の点では丸の左半分が縦軸の列へ出る（グラフの子なので切れない）");
            Check.True(ChartLayout.HandlePosition(plot, 10, -5) == V(plot.X + 10, plot.Bottom), "負の直径は 0（点）");
            // 面の中の判定（吹き出しと同じ半単位の許容）
            Check.True(ChartLayout.InsidePlot(0, 454) && ChartLayout.InsidePlot(454, 454), "両端の点は出す");
            Check.True(ChartLayout.InsidePlot(-0.5f, 454) && ChartLayout.InsidePlot(454.5f, 454), "半単位の許容");
            Check.True(!ChartLayout.InsidePlot(-0.51f, 454) && !ChartLayout.InsidePlot(454.51f, 454), "外へ出たら隠す");
            // 指の X = ハンドルの左上 + LocalPosition − 面の左（ハンドルがどこにあっても同じ指の位置は同じ X）
            Check.Close(137 + 9 - 46, ChartLayout.HandleFingerPlotX(V(137, 128), V(9, 9), plot), Eps, "ハンドルの真ん中を押した指 = 日付線の X");
            Check.Close(137 - 15 - 46, ChartLayout.HandleFingerPlotX(V(137, 128), V(-15, 30), plot), Eps, "当たりの広がり（見た目の外）も同じ式");
            float a = ChartLayout.HandleFingerPlotX(V(137, 128), V(40, 0), plot);
            float b = ChartLayout.HandleFingerPlotX(V(157, 128), V(20, 0), plot);
            Check.Close(a, b, Eps, "ハンドルが 20 動いても、同じ指の位置（LocalPosition が 20 減る）なら同じ X");
        });

        h.Add("P2-4 テーマ: ハンドルの直径は 16〜20・縁は半径より細い・見た目の値（ChartLook）が読む・縁の色は面の色", () =>
        {
            double d = theme.Number(ChartTokens.SizeHandle), border = theme.Number(ChartTokens.SizeHandleBorder);
            Check.True(d >= HandleMinDiameter && d <= HandleMaxDiameter, $"直径 {d} は 16〜20");
            Check.True(border >= 0 && border < d / 2, $"縁 {border} は半径より細い");
            var look = ChartLook.From(theme);
            Check.Close(d, look.Handle, Eps, "ChartLook.Handle");
            Check.Close(border, look.HandleBorder, Eps, "ChartLook.HandleBorder");
            Check.True(look.HandleBorderColor == theme.Color(UiTokens.ColorSurface), "縁の色 = color.surface（グラフの面）");
            Check.Equal(UiTokens.ColorSurface, ChartTokens.ColorHandleBorder, "縁の色のトークンは color.surface の別名");
        });

        h.Add("P2-4 プレハブ: line_chart.actor と見本の折れ線（WakeWeek・WakeHistory・GalleryLine）の子 Handle の作り", () =>
        {
            using var prefab = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "prefabs", "line_chart.actor")));
            var handle = CheckHandleChild(prefab.RootElement, "line_chart.actor");
            // 見本の折れ線はプレハブの参照ではなく中身の写し: 同じ Handle を持つ（配置の違いは無い）
            using var charts = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenes", "ui_charts.scene")));
            using var gallery = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenes", "ui_gallery.scene")));
            foreach (var (doc, name) in new[] { (charts, "WakeWeek"), (charts, "WakeHistory"), (gallery, "GalleryLine") })
            {
                var node = FindNode(doc.RootElement.GetProperty("actors"), name);
                Check.True(node.HasValue, $"見本に {name} がある");
                if (!node.HasValue) continue;
                var copy = CheckHandleChild(node.Value, name);
                Check.True(JsonElement.DeepEquals(handle, copy), $"{name} の Handle はプレハブと同じ");
            }
        });
    }

    /// <summary>グラフのノードの子 Handle を確かめて返す（Tooltip の直前・非表示・楕円・横のドラッグだけ・当たり 48・GestureRelay・手前のレイヤー）。</summary>
    private static JsonElement CheckHandleChild(JsonElement chart, string what)
    {
        var names = new List<string>();
        foreach (var c in chart.GetProperty("children").EnumerateArray()) names.Add(c.GetProperty("name").GetString() ?? "");
        int at = names.IndexOf("Handle");
        Check.True(at >= 0 && at + 1 < names.Count && names[at + 1] == "Tooltip",
            $"{what}: Handle は Tooltip の直前（同じレイヤーで吹き出しがハンドルの上に描かれる）: {string.Join(",", names)}");
        var handle = Child(chart, "Handle");
        var tooltip = Child(chart, "Tooltip");
        Check.True(handle.TryGetProperty("visible", out var visible) && !visible.GetBoolean(), $"{what}: はじめは非表示（選んだ点が無い）");
        var transform = handle.GetProperty("canvas_transform");
        Check.True(transform.GetProperty("pivot")[0].GetSingle() == 0 && transform.GetProperty("pivot")[1].GetSingle() == 0
                   && transform.GetProperty("anchor")[0].GetSingle() == 0 && transform.GetProperty("anchor")[1].GetSingle() == 0,
            $"{what}: pivot・anchor は 0（位置 = 左上。ChartLayout.HandlePosition の前提）");
        var sprite = Data(handle, "SpriteComponent");
        Check.Equal("ellipse", sprite.GetProperty("shape").GetProperty("kind").GetString(), $"{what}: 丸（楕円）");
        Check.Equal(Data(tooltip, "SpriteComponent").GetProperty("layer").GetInt32(), sprite.GetProperty("layer").GetInt32(),
            $"{what}: 吹き出しと同じ手前のレイヤー");
        Check.Equal(OverlayLayer, sprite.GetProperty("layer").GetInt32(), $"{what}: レイヤー 1（面の図形より手前）");
        var gesture = Data(handle, "CanvasGestureComponent");
        Check.True(gesture.GetProperty("drag").GetBoolean(), $"{what}: ドラッグを受ける");
        Check.Equal("horizontal", gesture.GetProperty("drag_axis").GetString(), $"{what}: 横だけ（縦の移動は縦のスクロールへ渡る）");
        foreach (var off in new[] { "tap", "long_press", "fling", "pinch", "press_feedback" })
            Check.True(!gesture.TryGetProperty(off, out var flag) || !flag.GetBoolean(), $"{what}: {off} は受けない（タップはグラフへ届く）");
        Check.Close(HandleHitSize, gesture.GetProperty("min_hit_size_dp").GetDouble(), Eps, $"{what}: 当たりは 48 dp");
        bool relay = false;
        foreach (var c in handle.GetProperty("components").EnumerateArray())
        {
            var comp = c.GetProperty("component");
            if (comp.GetProperty("type").GetString() == "ScriptComponent"
                && comp.GetProperty("data").GetProperty("type_name").GetString() == "SEED.UI.GestureRelay") relay = true;
        }
        Check.True(relay, $"{what}: ドラッグをグラフへ渡す GestureRelay");
        return handle;
    }

    /// <summary>JSON の子（名前で引く）。</summary>
    private static JsonElement Child(JsonElement node, string name)
    {
        foreach (var c in node.GetProperty("children").EnumerateArray())
            if (c.GetProperty("name").GetString() == name) return c;
        throw new InvalidOperationException($"子 {name} が無い");
    }

    /// <summary>JSON のコンポーネントの data（型の名前で引く）。</summary>
    private static JsonElement Data(JsonElement node, string type)
    {
        foreach (var c in node.GetProperty("components").EnumerateArray())
        {
            var comp = c.GetProperty("component");
            if (comp.GetProperty("type").GetString() == type) return comp.GetProperty("data");
        }
        throw new InvalidOperationException($"{type} が無い");
    }

    /// <summary>シーンの木から名前のノードを探す（深さ優先）。</summary>
    private static JsonElement? FindNode(JsonElement nodes, string name)
    {
        foreach (var n in nodes.EnumerateArray())
        {
            if (n.GetProperty("name").GetString() == name) return n;
            if (n.TryGetProperty("children", out var kids) && FindNode(kids, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>点を作る（短く書くため）。</summary>
    private static Vector2 V(float x, float y) => new(x, y);
}
