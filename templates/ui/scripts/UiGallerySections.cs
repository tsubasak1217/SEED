// ============================================================
//  UiGallerySections.cs — ギャラリーのページの W2-9 で足した段（templates/ui/scenes/ui_gallery.scene の Page。docs/ui_theme.md §9）
//
//  ページ（縦のスクロール）のノードに付ける。W2-4・W2-5 の段は UiGalleryDemo（根）がつなぐので、ここは次の段だけ:
//    - 一覧（W2-3）: List の窓へ 100 行の SEED.UI.ListView（行のプレハブ list_row.actor。行の色・文字は ThemeStyle でテーマに結び付く）
//    - 画面の組み立て（W2-7）: NavDialog・NavSheet・NavOverlay・NavToast のボタンで Dialog・BottomSheet・TopSheet・Toast を開く
//      （根の ModalHost・ToastHost が受ける。シート・覆いの中身は nav_sheet_content・nav_overlay_content）
//    - グラフ（W2-8）: GalleryLine（起床時間の 30 日・滑らかな曲線・平均の基準線）・GalleryBars（12 か月の積み上げ）
//  デバッグの命令（SCRIPT_DEBUG:gallery,<名前>）: scroll,<位置>（ページを位置へすぐ移す。スクリーンショットの場面を作る）・
//  open,<dialog|sheet|overlay|toast>（ボタンと同じ）・stats（一覧の作った行の数と見えている範囲）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using SEED;
using SEED.UI;
using SEEDEditor.Scripting;

public class UiGallerySections : SEEDScript
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] gallery:";
    /// <summary>デバッグの命令の名前。</summary>
    private const string CommandName = "gallery";
    /// <summary>一覧の行のプレハブ。</summary>
    private const string RowPrefab = "assets://ui/prefabs/list_row.actor";
    /// <summary>シートの中身のプレハブ（W2-7 の見本）。</summary>
    private const string SheetContentPrefab = "assets://ui/prefabs/nav_sheet_content.actor";
    /// <summary>覆いの中身のプレハブ（W2-7 の見本）。</summary>
    private const string OverlayContentPrefab = "assets://ui/prefabs/nav_overlay_content.actor";
    /// <summary>一覧の行の数・行の高さ（dp。list_row.actor と同じ）。</summary>
    private const int ListCount = 100;
    private const float RowExtent = 56f;
    /// <summary>行の時刻の見本（時の始まり・時の幅・分の刻み）。</summary>
    private const int FirstHour = 5, HourSpan = 4, MinuteStride = 7, MinutesPerHour = 60;
    /// <summary>曜日の並びの見本。</summary>
    private static readonly string[] RepeatLabels = { "平日", "毎日", "土日", "月・水・金", "一度だけ" };

    /// <summary>サンプルデータの乱数の種（実行のたびに同じ絵にする）。</summary>
    private const int Seed = 20260928;
    /// <summary>サンプルの「今日」。</summary>
    private static readonly DateOnly Today = new(2026, 9, 28);
    /// <summary>折れ線の日数・起床時間の中心（分）と揺れ（分）・記録の無い日の間隔。</summary>
    private const int LineDays = 30, WakeCenter = 420, WakeJitter = 40, MissingEvery = 7, MissingOffset = 3;
    /// <summary>棒の月の数・月のコインと円の上限・円のある月の割合。</summary>
    private const int Months = 12, MonthlyCoinsMax = 3000, MonthlyYenMax = 2000;
    private const double YenRatio = 0.5;

    /// <summary>一覧。</summary>
    private ListView? _list;
    /// <summary>つないだ部品（二重につながない）。</summary>
    private readonly HashSet<UiWidget> _bound = new();
    /// <summary>データを入れたグラフの名前。</summary>
    private readonly HashSet<string> _charts = new();
    /// <summary>最後につなぎ直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>デバッグの命令の受け口。</summary>
    private Action<string>? _cmd;

    public override void OnStart()
    {
        _cmd = OnCommand;
        SEED.Debug.OnCommand(CommandName, _cmd);
        var listNode = gameObject.FindChild("List");
        if (listNode.IsValid) _list = new ListView(listNode, RowPrefab, ListCount, RowExtent, BindRow);
    }

    public override void OnDestroy()
    {
        if (_cmd is not null) SEED.Debug.OffCommand(CommandName, _cmd);
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        _list?.Update();
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        BindButtons();
        BindCharts();
    }

    /// <summary>行へデータを入れる（色・大きさは行のプレハブの ThemeStyle がテーマから当てる）。</summary>
    private static void BindRow(GameObject row, int index)
    {
        int hour = FirstHour + index % HourSpan, minute = index * MinuteStride % MinutesPerHour;
        if (row.FindChild("Title").GetComponent<Text>() is { } title)
            title.Content = $"{hour}:{minute:00}　アラーム {index + 1}";
        if (row.FindChild("Sub").GetComponent<Text>() is { } sub)
            sub.Content = RepeatLabels[index % RepeatLabels.Length];
    }

    /// <summary>画面の組み立ての呼び出しのボタンをつなぐ。</summary>
    private void BindButtons()
    {
        foreach (var (name, what) in new[] { ("NavDialog", "dialog"), ("NavSheet", "sheet"), ("NavOverlay", "overlay"), ("NavToast", "toast") })
        {
            if (UiWidget.Of<Button>(gameObject.FindChild(name)) is { } b && _bound.Add(b))
            {
                string kind = what;
                b.Clicked += _ => Open(kind);
            }
        }
    }

    /// <summary>重ねる面・トーストを開く（W2-7 の API）。</summary>
    private static void Open(string what)
    {
        switch (what)
        {
            case "dialog":
                var dialog = Dialog.Show(new DialogOptions { Title = "テーマの確認", Message = "ダイアログの面・文字・ボタンもテーマの色になる。", PositiveText = "OK", NegativeText = "やめる" });
                if (dialog is not null) dialog.Closed += h => Report($"dialog closed result={h.Result ?? "null"}");
                break;
            case "sheet":
                BottomSheet.Show(new SheetOptions { ContentPrefab = SheetContentPrefab });
                break;
            case "overlay":
                TopSheet.Show(new OverlayOptions { ContentPrefab = OverlayContentPrefab });
                break;
            case "toast":
                Toast.Show("トーストもテーマの色になる");
                break;
        }
        Report($"open {what}");
    }

    /// <summary>グラフへサンプルデータを入れる（見つけたら 1 度だけ）。</summary>
    private void BindCharts()
    {
        if (!_charts.Contains("GalleryLine") && UiWidget.Of<LineChart>(gameObject.FindChild("GalleryLine")) is { } line)
        {
            _charts.Add("GalleryLine");
            SetupLine(line);
        }
        if (!_charts.Contains("GalleryBars") && UiWidget.Of<BarChart>(gameObject.FindChild("GalleryBars")) is { } bars)
        {
            _charts.Add("GalleryBars");
            SetupBars(bars);
        }
    }

    /// <summary>起床時間の 30 日（欠けた日はつなぐ・平均の基準線）。</summary>
    private static void SetupLine(LineChart chart)
    {
        var rng = new System.Random(Seed);
        var points = new List<ChartPoint>();
        var first = Today.AddDays(-(LineDays - 1));
        double sum = 0;
        int n = 0;
        for (int d = 0; d < LineDays; d++)
        {
            bool missing = d % MissingEvery == MissingOffset;
            double minutes = WakeCenter + rng.Next(-WakeJitter, WakeJitter + 1);
            points.Add(ChartPoint.Minutes(first.AddDays(d), missing ? null : TimeSpan.FromMinutes(minutes)));
            if (!missing) { sum += minutes; n++; }
        }
        chart.FixedXRange = new ChartRange(first.DayNumber, Today.DayNumber);
        chart.SetSeries(points);
        if (n > 0) chart.SetReferenceLine(sum / n, $"平均 {ChartFormat.TimeOfDay(sum / n)}");
    }

    /// <summary>月ごとの積み上げ（コインと円）。</summary>
    private static void SetupBars(BarChart chart)
    {
        var rng = new System.Random(Seed + 1);
        var bars = new List<BarDatum>();
        var firstMonth = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-(Months - 1));
        for (int m = 0; m < Months; m++)
        {
            double coins = rng.Next(0, MonthlyCoinsMax), yen = rng.NextDouble() < YenRatio ? rng.Next(0, MonthlyYenMax) : 0;
            bars.Add(new BarDatum(m, coins, yen));
        }
        chart.XLabelFormatter = (v, _) => $"{firstMonth.AddMonths((int)Math.Round(v)).Month}月";
        chart.XSteps = "1,2,3,6";
        chart.SetData(bars);
    }

    /// <summary>デバッグの命令（SCRIPT_DEBUG:gallery,…）。</summary>
    private void OnCommand(string arg)
    {
        var p = arg.Split(',');
        string At(int i) => p.Length > i ? p[i].Trim() : "";
        switch (At(0))
        {
            case "scroll" when float.TryParse(At(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var y):
                if (gameObject.GetComponent<CanvasScroll>() is { } scroll)
                {
                    scroll.JumpTo(new Vector2(0f, y));
                    Redraw.Request();
                    Report($"scroll {y}");
                }
                break;
            case "open":
                Open(At(1));
                break;
            case "stats":
                Report($"stats list created={_list?.CreatedRowCount ?? 0} visible={_list?.VisibleRange.First ?? -1}..{_list?.VisibleRange.Last ?? -1}");
                break;
        }
    }

    /// <summary>知らせをログと画面の Log へ出す。</summary>
    private static void Report(string message)
    {
        SEED.Debug.Log($"{LogPrefix} {message}");
        if (GameObject.Find("Log").GetComponent<Text>() is { } log) log.Content = message;
    }
}
