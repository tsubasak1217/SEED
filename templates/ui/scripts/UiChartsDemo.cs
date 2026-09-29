// ============================================================
//  UiChartsDemo.cs — グラフの見本（templates/ui/scenes/ui_charts.scene）の根（W2-8。docs/ui_charts.md §8）
//
//  見本のシーンの根に付ける。5 つのグラフ（ノードの名前で引く）へ、Wake or Pay のアクティビティと同じ形のサンプルデータ
//  （種の固定の乱数。実行のたびに同じ）を入れる:
//    - WakeWeek       … 起床時間の遷移（30 日・固定。滑らかな曲線・線の下の塗り・平均の基準線「平均 H:mm」・欠けた日はつなぐ）
//    - WakeHistory    … 起床時間の全期間（365 日・パン・ピンチ・± のボタン。点をタップで「M/d HH:mm」）
//    - MonthlyPenalty … 月ごとの寝坊ペナルティ（12 か月・コインとカードの積み上げ。横軸の文字は「9月」）
//    - PenaltyHistory … ペナルティ履歴（365 日・積み上げ・0 の日も最低の高さ・既定は最後に何か失った日を選ぶ）
//    - WeekdayBars    … 曜日ごとの寝坊（横の棒）
//  W2 の手直し P2-5 でシーンを画面の幅と安全領域に合わせる作り（Body〈安全領域・縦の Stack〉→ Page → Content → 見出しとグラフの組）にした。
//  グラフは GameObject.Find の名前で引くので、組の入れ物の下でも同じ名前で届く（docs/ui_charts.md §8）。
//  デバッグの命令（SCRIPT_DEBUG:chart,<名前>[,<値>]）: stats（グラフごとの倍率・見える範囲・選び・作り直しの回数・図形の数・時間）・
//  zoom <グラフ>,<倍率>（± のボタンと同じ動き）・show <グラフ>,<最初の日の番号からの日数>,<日数>・select <グラフ>,<添字>・
//  clear <グラフ>・mark <文字>（ログの区切り）・only <グラフ|none|all>（計測: ほかのグラフを隠す）・
//  style <グラフ>,<滑らか 0/1>,<線の下の塗り 0/1>（計測: 折れ線の描き方を切り替える）。
// ============================================================
using System;
using System.Collections.Generic;
using SEED;
using SEED.UI;
using SEEDEditor.Scripting;

public class UiChartsDemo : SEEDScript
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] charts:";
    /// <summary>サンプルデータの乱数の種（実行のたびに同じ絵にする）。</summary>
    private const int Seed = 20260928;
    /// <summary>サンプルの「今日」（Wake or Pay の W3 の時点）。</summary>
    private static readonly DateOnly Today = new(2026, 9, 28);
    /// <summary>30 日・全期間の日数。</summary>
    private const int WeekDays = 30, HistoryDays = 365;
    /// <summary>起床時間の中心（7:00 = 420 分）と揺れ（分）・季節の揺れ（分）。</summary>
    private const int WakeCenter = 420, WakeJitter = 40, WakeSeason = 30;
    /// <summary>記録の無い日の割合（全期間）。</summary>
    private const double MissingRatio = 0.12;
    /// <summary>ペナルティの無い日の割合・コインの額の上限・カードの日の割合と額の上限。</summary>
    private const double NoPenaltyRatio = 0.7, CardRatio = 0.25;
    private const int MaxCoins = 300, MaxYen = 500;
    /// <summary>月の数。</summary>
    private const int Months = 12;
    /// <summary>曜日の名前（横の棒の列）。</summary>
    private static readonly string[] Weekdays = { "月", "火", "水", "木", "金", "土", "日" };

    private Action<string>? _cmd;
    private int _registryVersion = -1;
    private readonly Dictionary<string, ChartView> _charts = new();

    public override void OnStart()
    {
        _cmd = OnCommand;
        SEED.Debug.OnCommand("chart", _cmd);
    }

    public override void OnDestroy()
    {
        if (_cmd is not null) SEED.Debug.OffCommand("chart", _cmd);
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        // グラフのスクリプトの OnStart の順は決まっていないので、登録簿が変わるたびに引き直す（見つけたら 1 度だけデータを入れる）
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        Bind<LineChart>("WakeWeek", SetupWakeWeek);
        Bind<LineChart>("WakeHistory", SetupWakeHistory);
        Bind<BarChart>("MonthlyPenalty", SetupMonthly);
        Bind<BarChart>("PenaltyHistory", SetupPenaltyHistory);
        Bind<BarChart>("WeekdayBars", SetupWeekdays);
    }

    /// <summary>名前のノードのグラフを引いて、まだなら準備する。</summary>
    private void Bind<T>(string name, Action<T> setup) where T : ChartView
    {
        if (_charts.ContainsKey(name)) return;
        if (UiWidget.Of<T>(GameObject.Find(name)) is not { } chart) return;
        _charts[name] = chart;
        setup(chart);
        Debug.Log($"{LogPrefix} bound {name}");
    }

    // ── サンプルデータ ────────────────────────────────────────

    /// <summary>起床時間（分）。季節の揺れ + 乱数。</summary>
    private static double WakeMinutes(System.Random rng, int dayIndex)
        => WakeCenter + WakeSeason * Math.Sin(2 * Math.PI * dayIndex / HistoryDays) + rng.Next(-WakeJitter, WakeJitter + 1);

    /// <summary>吹き出しの文字（Wake or Pay の起床時間の全期間: M/d HH:mm）。</summary>
    private static string WakeTooltip(ChartPoint p) => $"{ChartFormat.Date(p.X)} {ChartFormat.TimeOfDay(p.Y ?? 0, padHour: true)}";

    /// <summary>平均の基準線の文字（平均 H:mm）。</summary>
    private static void SetAverage(LineChart chart, IReadOnlyList<ChartPoint> points)
    {
        double sum = 0;
        int n = 0;
        foreach (var p in points)
            if (p.HasValue) { sum += p.Y!.Value; n++; }
        if (n > 0) chart.SetReferenceLine(sum / n, $"平均 {ChartFormat.TimeOfDay(sum / n)}");
    }

    private void SetupWakeWeek(LineChart chart)
    {
        var rng = new System.Random(Seed);
        var points = new List<ChartPoint>();
        var first = Today.AddDays(-(WeekDays - 1));
        for (int d = 0; d < WeekDays; d++)
        {
            // 記録の無い日（点を打たずに線をつなぐ）: 7 日に 1 度
            const int missingEvery = 7, missingOffset = 3;
            bool missing = d % missingEvery == missingOffset;
            points.Add(ChartPoint.Minutes(first.AddDays(d), missing ? null : TimeSpan.FromMinutes(WakeMinutes(rng, HistoryDays - WeekDays + d))));
        }
        chart.FixedXRange = new ChartRange(first.DayNumber, Today.DayNumber);
        chart.TooltipFormatter = WakeTooltip;
        chart.SetSeries(points);
        SetAverage(chart, points);
    }

    private void SetupWakeHistory(LineChart chart)
    {
        var rng = new System.Random(Seed + 1);
        var points = new List<ChartPoint>();
        var first = Today.AddDays(-(HistoryDays - 1));
        for (int d = 0; d < HistoryDays; d++)
        {
            bool missing = rng.NextDouble() < MissingRatio;
            points.Add(ChartPoint.Minutes(first.AddDays(d), missing ? null : TimeSpan.FromMinutes(WakeMinutes(rng, d))));
        }
        chart.TooltipFormatter = WakeTooltip;
        chart.SetSeries(points);
        SetAverage(chart, points);
    }

    private void SetupMonthly(BarChart chart)
    {
        var rng = new System.Random(Seed + 2);
        var bars = new List<BarDatum>();
        var firstMonth = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-(Months - 1));
        for (int m = 0; m < Months; m++)
        {
            const int monthlyCoinsMax = 3000, monthlyYenMax = 2000;
            double coins = rng.Next(0, monthlyCoinsMax), yen = rng.NextDouble() < CardRatio * 2 ? rng.Next(0, monthlyYenMax) : 0;
            bars.Add(new BarDatum(m, coins, yen));
        }
        // 横軸の文字は「9月」（X は 0..11 の月の番号）
        chart.XLabelFormatter = (v, _) => $"{firstMonth.AddMonths((int)Math.Round(v)).Month}月";
        chart.XSteps = "1,2,3,6";
        chart.TooltipFormatter = b => $"{firstMonth.AddMonths((int)b.X).Month}月 {b.Values[0]:#,0} コイン・{b.Values[1]:#,0} 円";
        chart.SetData(bars);
    }

    private void SetupPenaltyHistory(BarChart chart)
    {
        var rng = new System.Random(Seed + 3);
        var bars = new List<BarDatum>();
        var first = Today.AddDays(-(HistoryDays - 1));
        int lastLoss = -1;
        for (int d = 0; d < HistoryDays; d++)
        {
            double coins = 0, yen = 0;
            if (rng.NextDouble() >= NoPenaltyRatio)
            {
                coins = rng.Next(1, MaxCoins);
                if (rng.NextDouble() < CardRatio) yen = rng.Next(1, MaxYen);
                lastLoss = d;
            }
            bars.Add(new BarDatum(first.AddDays(d).DayNumber, coins, yen));
        }
        chart.TooltipFormatter = b => $"{ChartFormat.Date(b.X)} {b.Values[0]:#,0} コイン・{b.Values[1]:#,0} 円";
        chart.SetData(bars);
        // 既定は最後に何か失った日（Wake or Pay の S-05a）
        if (lastLoss >= 0) chart.Select(lastLoss);
    }

    private void SetupWeekdays(BarChart chart)
    {
        var rng = new System.Random(Seed + 4);
        var bars = new List<BarDatum>();
        const int maxPerDay = 9;
        for (int i = 0; i < Weekdays.Length; i++) bars.Add(new BarDatum(i, rng.Next(0, maxPerDay)));
        chart.XLabelFormatter = (v, _) => Weekdays[Math.Clamp((int)Math.Round(v), 0, Weekdays.Length - 1)];
        chart.XSteps = "1";
        chart.TooltipFormatter = b => $"{Weekdays[(int)b.X]}曜 {b.Total:0} 回";
        chart.SetData(bars);
    }

    // ── デバッグの命令 ────────────────────────────────────────

    /// <summary>デバッグの命令（SCRIPT_DEBUG:chart,<名前>[,<値>]）。</summary>
    private void OnCommand(string arg)
    {
        var parts = arg.Split(',');
        string name = parts[0].Trim();
        string At(int i) => parts.Length > i ? parts[i].Trim() : "";
        switch (name)
        {
            case "stats":
                foreach (var (key, chart) in _charts) LogStats(key, chart);
                break;
            case "zoom":
                if (_charts.TryGetValue(At(1), out var z) && double.TryParse(At(2), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var factor))
                    z.ZoomBy(factor);
                break;
            case "show":
                if (_charts.TryGetValue(At(1), out var s) && int.TryParse(At(2), out var from) && int.TryParse(At(3), out var days))
                {
                    double start = s.Viewport.Full.Min + from;
                    s.ShowRange(new ChartRange(start, start + days));
                }
                break;
            case "select":
                if (_charts.TryGetValue(At(1), out var sel) && int.TryParse(At(2), out var index))
                {
                    if (sel is BarChart bc) bc.Select(index, notify: true);
                    else if (sel is LineChart lc) lc.Select(0, index, notify: true);
                }
                break;
            case "clear":
                if (_charts.TryGetValue(At(1), out var c)) c.ClearSelection();
                break;
            case "mark":
                Debug.Log($"{LogPrefix} ---- {At(1)} ----");
                break;
            case "only":
                // 計測用: 指定のグラフ（と見出し）だけを見せる（none = すべて隠す・all = すべて見せる）
                foreach (var (key, chart) in _charts)
                {
                    var owner = chart.Owner;
                    owner.Visible = At(1) == "all" || key == At(1);
                }
                Redraw.Request();
                break;
            case "style":
                if (_charts.TryGetValue(At(1), out var st) && st is LineChart line)
                {
                    line.Smooth = At(2) == "1";
                    line.FillArea = At(3) == "1";
                    line.MarkDirty();
                }
                break;
            default:
                Debug.LogWarning($"{LogPrefix} 知らない命令: {arg}");
                break;
        }
    }

    /// <summary>グラフ 1 つの状態をログへ。</summary>
    private static void LogStats(string name, ChartView chart)
    {
        var v = chart.Viewport;
        string selected = chart switch
        {
            LineChart lc => $"{lc.Selected.Series}/{lc.Selected.Index}",
            BarChart bc => bc.SelectedIndex.ToString(),
            _ => "-",
        };
        Debug.Log($"{LogPrefix} stats {name} zoom={v.Zoom:0.###} visible=[{v.Visible.Min - v.Full.Min:0.##}, {v.Visible.Max - v.Full.Min:0.##}] " +
                  $"canPan={v.CanPan} selected={selected} rebuilds={chart.RebuildCount} draws={chart.LastDrawCount} " +
                  $"rebuildMs={chart.LastRebuildMs:0.###} paintMs={chart.LastPaintMs:0.###} dragging={chart.IsDragging} animating={chart.IsAnimating}");
    }
}
