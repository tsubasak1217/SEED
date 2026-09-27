using System;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 時刻ホイール（W2-5）の純粋な計算のテスト（docs/ui_components.md §11）:
/// 行の見た目（WheelLook）・循環の添字と位置（WheelLoop）・12/24 時間と連動と分の刻み（TimeWheelMath）。
/// </summary>
public static class WheelTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;
    /// <summary>ギャラリー・Wake or Pay と同じ窓の高さ（190）と行の高さ（32）。</summary>
    private const float Viewport = 190f;
    private const float Extent = 32f;

    /// <summary>
    /// Flutter の MatrixUtils.createCylindricalProjectionTransform（perspective × view × rotationX(角度) × translation(0, 0, r)）を
    /// そのまま 4×4 で掛けて、行のローカルの点 (0, y, 0) が映る y を返す（WheelLook の閉じた式を独立に検算するため）。
    /// </summary>
    private static double ProjectY(double y, double angle, double radius, double perspective)
    {
        // 4×4（行優先）: result = PV × Rx × T
        double[,] pv =
        {
            { 1, 0, 0, 0 },
            { 0, 1, 0, 0 },
            { 0, 0, 1, -radius },
            { 0, 0, -perspective, perspective * radius + 1.0 },
        };
        double c = Math.Cos(angle), s = Math.Sin(angle);
        double[,] rx =
        {
            { 1, 0, 0, 0 },
            { 0, c, -s, 0 },
            { 0, s, c, 0 },
            { 0, 0, 0, 1 },
        };
        double[,] t =
        {
            { 1, 0, 0, 0 },
            { 0, 1, 0, 0 },
            { 0, 0, 1, radius },
            { 0, 0, 0, 1 },
        };
        var m = Mul(Mul(pv, rx), t);
        double[] p = { 0, y, 0, 1 };
        double outY = m[1, 0] * p[0] + m[1, 1] * p[1] + m[1, 2] * p[2] + m[1, 3] * p[3];
        double outW = m[3, 0] * p[0] + m[3, 1] * p[1] + m[3, 2] * p[2] + m[3, 3] * p[3];
        return outY / outW;
    }

    /// <summary>4×4 の積。</summary>
    private static double[,] Mul(double[,] a, double[,] b)
    {
        var r = new double[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++)
                    r[i, j] += a[i, k] * b[k, j];
        return r;
    }

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        var look = WheelLookParams.DatePicker;

        // ── 行の見た目（距離 → 位置・縮み・拡大・濃さ）──────────────
        h.Add("ホイール: 中央の行はそのまま（位置 0・縮みなし）で拡大・不透明", () =>
        {
            var c = WheelLook.Resolve(0f, Viewport, Extent, look);
            Check.True(c.Visible, "描く");
            Check.Close(0, c.Offset, Eps, "中央に映る");
            Check.Close(1, c.ScaleY, Eps, "縦に縮まない");
            Check.Close(1, c.ScaleX, Eps, "横に縮まない");
            Check.Close(WheelLookParams.DatePickerMagnification, c.Magnify, Eps, "中央は拡大（2.35 / 2.1）");
            Check.Close(1, c.Emphasis, Eps, "帯の中の度合い 1");
            Check.Close(1, c.Opacity, Eps, "不透明");
        });

        h.Add("ホイール: 映る位置と縦の縮みは Flutter の円柱の投影の行列と一致する", () =>
        {
            float radius = WheelLook.Radius(Viewport, look.DiameterRatio);
            foreach (float d in new[] { 8f, 32f, 64f, 96f, 118f, -40f, -100f })
            {
                var r = WheelLook.Resolve(d, Viewport, Extent, look);
                // Flutter の角度は下の行が負（angle = −(fractionalY − 0.5) × 2 × θmax / squeeze）。WheelLook は下が正なので符号を返す
                double angle = -WheelLook.AngleOf(d, Viewport, look);
                double center = ProjectY(0, angle, radius, look.Perspective);
                // 行の中心での縦の倍率 = 映る位置の微分（中心差分）
                const double H = 1e-3;
                double slope = (ProjectY(H, angle, radius, look.Perspective) - ProjectY(-H, angle, radius, look.Perspective)) / (2 * H);
                Check.Close(center, r.Offset, 1e-3, $"d={d} の映る位置");
                Check.Close(slope, r.ScaleY, 1e-3, $"d={d} の縦の倍率");
            }
        });

        h.Add("ホイール: 離れるほど縦に縮み・薄く・外へ寄り、上下は対称", () =>
        {
            float prevScale = float.MaxValue, prevOpacity = float.MaxValue, prevOffset = -1f;
            for (int i = 0; i <= 4; i++)
            {
                var r = WheelLook.Resolve(i * Extent, Viewport, Extent, look);
                if (!r.Visible) break;
                Check.True(r.ScaleY < prevScale, $"行 {i} の縦の縮みは内側より大きい（{r.ScaleY}）");
                Check.True(r.Opacity < prevOpacity, $"行 {i} は内側より薄い（{r.Opacity}）");
                Check.True(r.Offset > prevOffset, $"行 {i} は内側より外に映る（{r.Offset}）");
                prevScale = r.ScaleY;
                prevOpacity = r.Opacity;
                prevOffset = r.Offset;
                var up = WheelLook.Resolve(-i * Extent, Viewport, Extent, look);
                Check.Close(-r.Offset, up.Offset, Eps, $"行 −{i} は上へ同じだけ");
                Check.Close(r.ScaleY, up.ScaleY, Eps, $"行 −{i} の縮みは同じ");
                Check.Close(r.Opacity, up.Opacity, Eps, $"行 −{i} の濃さは同じ");
            }
            // 1 行下: 中央の帯の外の濃さ（0.447）× 面の傾き（cos）。中央付近は平らに並べたときとほぼ同じ間隔（詰め具合 1.25 の CupertinoDatePicker）
            var one = WheelLook.Resolve(Extent, Viewport, Extent, look);
            Check.Close(0, one.Emphasis, Eps, "1 行離れると帯の外");
            Check.Close(WheelLookParams.CupertinoDimOpacity * Math.Cos(one.Angle), one.Opacity, Eps, "帯の外の濃さ × cos");
            Check.Close(Extent, one.Offset, 1.0, $"中央付近の間隔はほぼ 1 行（{one.Offset}）");
            // 半分だけ帯に入っている行は拡大・濃さが中間
            var half = WheelLook.Resolve(Extent * 0.5f, Viewport, Extent, look);
            Check.Close(0.5, half.Emphasis, Eps, "半分の行の度合い 0.5");
            Check.True(half.Magnify > 1f && half.Magnify < WheelLookParams.DatePickerMagnification, "拡大は中間");
        });

        h.Add("ホイール: 円柱の裏へ回った行は描かない・描ける距離の上限が境目・窓が 0 なら平ら", () =>
        {
            float max = WheelLook.MaxVisibleDistance(Viewport, look);
            Check.True(WheelLook.Resolve(max - 0.5f, Viewport, Extent, look).Visible, $"上限の少し内側は描く（{max}）");
            Check.True(!WheelLook.Resolve(max + 0.5f, Viewport, Extent, look).Visible, "上限の少し外は描かない");
            Check.True(!WheelLook.Resolve(-max - 0.5f, Viewport, Extent, look).Visible, "上も同じ");
            Check.True(!WheelLook.Resolve(1000f, Viewport, Extent, look).Visible, "ずっと遠い行は描かない");
            // 描ける行はすべて窓の中に映る（円柱が窓より小さく映る）
            var edge = WheelLook.Resolve(max - 0.5f, Viewport, Extent, look);
            Check.True(Math.Abs(edge.Offset) < Viewport / 2, $"いちばん外の行も窓の中（{edge.Offset}）");
            var flat = WheelLook.Resolve(40f, 0f, Extent, look);
            Check.True(flat.Visible && Math.Abs(flat.Offset - 40f) < Eps && Math.Abs(flat.ScaleY - 1f) < Eps, "窓の大きさが分からない間は平ら");
            Check.True(!WheelLook.Resolve(float.NaN, Viewport, Extent, look).Visible, "NaN は描かない");
            var noShade = WheelLook.Resolve(Extent * 2, Viewport, Extent, look with { EdgeShade = 0f });
            Check.Close(WheelLookParams.CupertinoDimOpacity, noShade.Opacity, Eps, "面の傾きの暗さ 0 なら帯の外は 0.447 のまま");
            foreach (float d in new[] { 0f, Extent, -Extent * 3 })
            {
                var broken = WheelLook.Resolve(d, Viewport, Extent, new WheelLookParams());
                Check.True(float.IsFinite(broken.Offset) && float.IsFinite(broken.ScaleY) && float.IsFinite(broken.Opacity),
                    $"0 ばかりの値でも NaN を出さない（d={d}）");
            }
            Check.True(WheelLook.Resolve(0f, Viewport, Extent, new WheelLookParams()).Visible, "0 ばかりの値でも中央の行は描く");
        });

        h.Add("ホイール: 映る位置 → 平らな距離（タップした行）は見た目の逆・外は上限", () =>
        {
            foreach (float d in new[] { 0f, 5f, 16f, 31f, 64f, 100f, -12f, -90f })
            {
                var r = WheelLook.Resolve(d, Viewport, Extent, look);
                Check.Close(d, WheelLook.DistanceAtOffset(r.Offset, Viewport, look), 1e-2, $"d={d} を往復");
            }
            float max = WheelLook.MaxVisibleDistance(Viewport, look);
            Check.Close(max, WheelLook.DistanceAtOffset(Viewport, Viewport, look), 1e-2, "映る位置の外（窓の端）は上限の距離");
            Check.Close(-max, WheelLook.DistanceAtOffset(-Viewport, Viewport, look), 1e-2, "上も");
            Check.Close(Extent * WheelLookParams.DatePickerMagnification, WheelLook.BandHeight(Extent, look), Eps, "帯の高さ = 行 × 拡大");
        });

        // ── 循環の添字と位置 ───────────────────────────────────────
        h.Add("ホイール: 循環の添字（負・大きな値でも 0..count−1）", () =>
        {
            Check.Equal(59, WheelLoop.ItemOfRow(-1, 60), "−1 → 59");
            Check.Equal(0, WheelLoop.ItemOfRow(-60, 60), "−60 → 0");
            Check.Equal(59, WheelLoop.ItemOfRow(-61, 60), "−61 → 59");
            Check.Equal(5, WheelLoop.ItemOfRow(125, 60), "125 → 5");
            Check.Equal(7, WheelLoop.ItemOfRow(int.MaxValue, 60), "int の最大 → 7");
            Check.Equal(52, WheelLoop.ItemOfRow(int.MinValue, 60), "int の最小 → 52");
            Check.Equal(0, WheelLoop.ItemOfRow(5, 0), "項目 0 個は 0");
        });

        h.Add("ホイール: 周の数（中身 ≒ 20 万・奇数・下限 3）と真ん中の周", () =>
        {
            int cycles = WheelLoop.CycleCount(60, Extent, looping: true);
            Check.Equal(103, cycles, "60 項目 × 32 → 104 周 → 奇数へ 103");
            Check.True(cycles % 2 == 1, "奇数");
            Check.Equal(6180, WheelLoop.TotalRows(60, cycles), "行の数");
            Check.True(WheelLoop.TotalRows(60, cycles) * Extent <= WheelLoop.LoopTargetExtent, "中身は目安以下");
            Check.Equal(1, WheelLoop.CycleCount(2, Extent, looping: false), "つながないなら 1 周");
            Check.Equal(WheelLoop.MinCycles, WheelLoop.CycleCount(100_000, Extent, looping: true), "項目が多くても 3 周");
            Check.Equal(1, WheelLoop.CycleCount(0, Extent, looping: true), "項目 0 個は 1");
            Check.Equal(60 * 51, WheelLoop.CenterCycleFirstRow(60, cycles), "真ん中の周の先頭");
            Check.Equal(60 * 51 + 7, WheelLoop.CenterRowOfItem(7, 60, cycles), "真ん中の周の 7");
            Check.Equal(60 * 51 + 59, WheelLoop.CenterRowOfItem(-1, 60, cycles), "負の項目も回す");
        });

        h.Add("ホイール: 近い向きの行（59 → 00 は 1 行進む・00 → 59 は 1 行戻る・半周は進む・一覧の端では内側）", () =>
        {
            int total = 6180;
            int row59 = 3060 + 59;
            Check.Equal(row59 + 1, WheelLoop.NearestRowOfItem(0, row59, 60, total), "59 → 00 は次の行");
            Check.Equal(3060 - 1, WheelLoop.NearestRowOfItem(59, 3060, 60, total), "00 → 59 は前の行");
            Check.Equal(3060 + 30, WheelLoop.NearestRowOfItem(30, 3060, 60, total), "ちょうど半周は進む");
            Check.Equal(3060 - 29, WheelLoop.NearestRowOfItem(31, 3060, 60, total), "半周より遠い向きは戻る");
            Check.Equal(3065, WheelLoop.NearestRowOfItem(5, 3065, 60, total), "同じ項目はその行");
            Check.Equal(59, WheelLoop.NearestRowOfItem(59, 0, 60, total), "先頭の行から戻る向きは 1 周内側");
            Check.Equal(total - 60, WheelLoop.NearestRowOfItem(0, total - 1, 60, total), "末尾の行から進む向きも内側");
            Check.Equal(1, WheelLoop.NearestRowOfItem(1, 0, 2, 2), "2 行（午前/午後・つながない）");
        });

        h.Add("ホイール: 真ん中から遠く止まったら真ん中の周へ（見た目は同じ項目）", () =>
        {
            int cycles = 103, total = 6180;
            Check.True(!WheelLoop.ShouldRecenter(3060 + 5, 60, cycles), "真ん中の周はそのまま");
            Check.True(!WheelLoop.ShouldRecenter(3060 + 60 * 20, 60, cycles), "20 周ずれは戻さない（全体の 1/4 = 1,545 行まで）");
            int far = 3060 + 60 * 30 + 5;
            Check.True(WheelLoop.ShouldRecenter(far, 60, cycles), "30 周ずれは戻す");
            Check.Equal(3065, WheelLoop.RecenteredRow(far, 60, cycles), "同じ項目の真ん中の周の行");
            Check.True(WheelLoop.ShouldRecenter(0, 60, cycles) && WheelLoop.ShouldRecenter(total - 1, 60, cycles), "端の行は戻す");
            Check.True(!WheelLoop.ShouldRecenter(1, 2, 1), "つながない（1 周）は戻さない");
        });

        h.Add("ホイール: 値の設定から位置（行 × 行の高さ）・位置から中央の行・余白", () =>
        {
            Check.Close(79, WheelLoop.LeadPadding(Viewport, Extent), Eps, "先頭の余白 =（190 − 32）/ 2");
            int cycles = WheelLoop.CycleCount(24, Extent, looping: true);
            int total = WheelLoop.TotalRows(24, cycles);
            int row = WheelLoop.CenterRowOfItem(7, 24, cycles);
            float pos = WheelLoop.PositionOfRow(row, Extent);
            Check.Close(row * 32.0, pos, Eps, "7 時の位置 = 行 × 32");
            Check.Equal(0.0, pos % Extent, "スナップの間隔（32）の倍数＝スナップと一致");
            Check.Equal(row, WheelLoop.RowAtPosition(pos, Extent, total), "位置 → 行（往復）");
            Check.Equal(7, WheelLoop.ItemOfRow(WheelLoop.RowAtPosition(pos + 15.9f, Extent, total), 24), "半分未満のずれは同じ行");
            Check.Equal(8, WheelLoop.ItemOfRow(WheelLoop.RowAtPosition(pos + 16f, Extent, total), 24), "ちょうど半分は先の行（Flutter の round）");
            Check.Equal(0, WheelLoop.RowAtPosition(-100f, Extent, total), "範囲の前は先頭");
            Check.Equal(total - 1, WheelLoop.RowAtPosition(1e9f, Extent, total), "範囲の後は末尾");
            // 23:59 → 0:00（分の列 59 から 1 行進む）の目標の位置は 1 行ぶん先
            int mCycles = WheelLoop.CycleCount(60, Extent, true);
            int m59 = WheelLoop.CenterRowOfItem(59, 60, mCycles);
            int next = WheelLoop.NearestRowOfItem(0, m59, 60, WheelLoop.TotalRows(60, mCycles));
            Check.Close(WheelLoop.PositionOfRow(m59, Extent) + Extent, WheelLoop.PositionOfRow(next, Extent), Eps, "59 → 00 は 1 行ぶん下へ");
            Check.Close(WheelLoop.MinItemExtent, WheelLoop.SanitizeExtent(float.NaN), Eps, "壊れた行の高さは下限");
        });

        h.Add("ホイール: 選べる項目（範囲の外は最も近い選べる項目へ・同じ近さは進む向き・キーの 1 歩は選べない項目を飛ばす）", () =>
        {
            Func<int, bool> upTo19 = i => i <= 19;
            Check.Equal(19, WheelLoop.NearestEnabledItem(25, 30, looping: false, upTo19), "範囲の外 → 最も近い端");
            Check.Equal(12, WheelLoop.NearestEnabledItem(12, 30, looping: false, upTo19), "選べるならそのまま");
            Func<int, bool> not58or59 = i => i != 58 && i != 59;
            Check.Equal(0, WheelLoop.NearestEnabledItem(59, 60, looping: true, not58or59), "つなげるなら 59 → 00（1 つ先）");
            Func<int, bool> not30 = i => i != 30;
            Check.Equal(31, WheelLoop.NearestEnabledItem(30, 60, looping: true, not30), "同じ近さは進む向き");
            Check.Equal(-1, WheelLoop.NearestEnabledItem(3, 10, looping: true, _ => false), "選べる項目が無ければ −1");
            Check.Equal(19, WheelLoop.StepItem(19, 1, 30, looping: false, upTo19), "選べない向きへは進まない");
            Check.Equal(0, WheelLoop.StepItem(57, 1, 60, looping: true, not58or59), "58・59 を飛ばして 00");
            Check.Equal(57, WheelLoop.StepItem(0, -1, 60, looping: true, not58or59), "戻る向きも飛ばす");
            Check.Equal(29, WheelLoop.StepItem(29, 1, 30, looping: false, _ => true), "つながないなら端で止まる");
            Check.Equal(3, WheelLoop.StepItem(0, 3, 30, looping: false, _ => true), "3 歩");
        });

        // ── 12/24 時間と午前/午後の連動・分の刻み ─────────────────────
        h.Add("時刻ホイール: 12/24 時間の表示の時", () =>
        {
            Check.Equal(12, TimeWheelMath.DisplayHour(0, use24Hour: false), "0 時は 12（午前）");
            Check.Equal(11, TimeWheelMath.DisplayHour(11, false), "11 時は 11");
            Check.Equal(12, TimeWheelMath.DisplayHour(12, false), "12 時は 12（午後）");
            Check.Equal(1, TimeWheelMath.DisplayHour(13, false), "13 時は 1");
            Check.Equal(11, TimeWheelMath.DisplayHour(23, false), "23 時は 11");
            Check.Equal(0, TimeWheelMath.DisplayHour(0, true), "24 時間表記の 0");
            Check.Equal(23, TimeWheelMath.DisplayHour(23, true), "24 時間表記の 23");
            Check.Equal(1, TimeWheelMath.DisplayHour(25, true), "範囲の外は 24 で回す");
            Check.Equal(TimeWheelMath.Am, TimeWheelMath.MeridiemOf(11), "11 時は午前");
            Check.Equal(TimeWheelMath.Pm, TimeWheelMath.MeridiemOf(12), "12 時は午後");
        });

        h.Add("時刻ホイール: 時の列が 11 → 12・23 → 0 を越えると午前/午後が入れ替わる（12 時間表記の連動）", () =>
        {
            var s = TimeWheelMath.StateFor(11);
            Check.True(!s.Flipped && s.AmPm == TimeWheelMath.Am, "11 時は午前・入れ替わりなし");
            Check.True(TimeWheelMath.OnHourItemChanged(ref s, 12), "11 → 12 で入れ替わる");
            Check.Equal(TimeWheelMath.Pm, s.AmPm, "午後になる");
            Check.Equal(12, TimeWheelMath.HourOfItem(12, s), "12 時（午後 12 時）");
            Check.True(!TimeWheelMath.OnHourItemChanged(ref s, 13), "12 → 13 は同じ半日");
            Check.Equal(13, TimeWheelMath.HourOfItem(13, s), "13 時");
            // 23 → 0（端をつなげた時の列の継ぎ目）
            s = TimeWheelMath.StateFor(23);
            Check.True(TimeWheelMath.OnHourItemChanged(ref s, 0), "23 → 0 で入れ替わる");
            Check.Equal(TimeWheelMath.Am, s.AmPm, "午前になる");
            Check.Equal(0, TimeWheelMath.HourOfItem(0, s), "0 時（午前 12 時）");
        });

        h.Add("時刻ホイール: 午前/午後の列を指で変えると時が 12 ずれ、その後の時の列の連動も保たれる", () =>
        {
            var s = TimeWheelMath.StateFor(7);
            TimeWheelMath.OnMeridiemSelected(ref s, TimeWheelMath.Pm);
            Check.True(s.Flipped, "入れ替わる");
            Check.Equal(19, TimeWheelMath.HourOfItem(7, s), "午後 7 時 = 19 時（時の列は動かない）");
            Check.True(!TimeWheelMath.OnHourItemChanged(ref s, 8), "7 → 8 は同じ半日");
            Check.Equal(20, TimeWheelMath.HourOfItem(8, s), "20 時");
            Check.True(TimeWheelMath.OnHourItemChanged(ref s, 12), "11 → 12 の行へ");
            Check.Equal(TimeWheelMath.Am, s.AmPm, "午後 11 時 → 午前 12 時（日付をまたぐ）");
            Check.Equal(0, TimeWheelMath.HourOfItem(12, s), "0 時");
            var r = TimeWheelMath.Reconcile(12, TimeWheelMath.Am, use24Hour: false);
            Check.True(r.Flipped && TimeWheelMath.HourOfItem(12, r) == 0, "列の見た目から作り直しても同じ（行 12・午前 = 0 時）");
            var r24 = TimeWheelMath.Reconcile(19, TimeWheelMath.Am, use24Hour: true);
            Check.True(!r24.Flipped && TimeWheelMath.HourOfItem(19, r24) == 19, "24 時間表記は入れ替えない");
        });

        h.Add("時刻ホイール: 分の刻み（1 時間を割り切る数だけ）・丸め（繰り上がり・日をまたぐ）・行と分", () =>
        {
            Check.Equal(5, TimeWheelMath.NormalizeMinuteStep(5), "5 分");
            Check.Equal(1, TimeWheelMath.NormalizeMinuteStep(7), "割り切れない 7 は 1");
            Check.Equal(1, TimeWheelMath.NormalizeMinuteStep(0), "0 は 1");
            Check.Equal(1, TimeWheelMath.NormalizeMinuteStep(-5), "負は 1");
            Check.Equal(60, TimeWheelMath.NormalizeMinuteStep(60), "60 は 1 時間ごと");
            Check.Equal(12, TimeWheelMath.MinuteRows(5), "5 分刻みは 12 行");
            Check.Equal(60, TimeWheelMath.MinuteRows(1), "1 分刻みは 60 行");
            Check.Equal(new TimeOnly(7, 30), TimeWheelMath.RoundToStep(new TimeOnly(7, 32), 5), "7:32 → 7:30");
            Check.Equal(new TimeOnly(7, 35), TimeWheelMath.RoundToStep(new TimeOnly(7, 33), 5), "7:33 → 7:35");
            Check.Equal(new TimeOnly(8, 0), TimeWheelMath.RoundToStep(new TimeOnly(7, 58), 5), "7:58 → 8:00（繰り上がり）");
            Check.Equal(new TimeOnly(0, 0), TimeWheelMath.RoundToStep(new TimeOnly(23, 58), 5), "23:58 → 0:00（日をまたぐ）");
            Check.Equal(new TimeOnly(7, 32), TimeWheelMath.RoundToStep(new TimeOnly(7, 31), 2), "ちょうど間は遅い方");
            Check.Equal(new TimeOnly(7, 31), TimeWheelMath.RoundToStep(new TimeOnly(7, 31, 45), 1), "秒は捨てる");
            Check.Equal(55, TimeWheelMath.MinuteOfItem(11, 5), "5 分刻みの行 11 = 55 分");
            Check.Equal(5, TimeWheelMath.MinuteOfItem(13, 5), "行は 12 で回す");
            Check.Equal(7, TimeWheelMath.ItemOfMinute(35, 5), "35 分 = 行 7");
            Check.Equal(new TimeOnly(23, 59), TimeWheelMath.Compose(-1, 59), "時・分は回す");
        });
    }
}
