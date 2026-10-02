// ============================================================
//  UiGallerySections.cs — ギャラリーのページの W2-9 で足した段（templates/ui/scenes/ui_gallery.scene の Page。docs/ui_theme.md §9）
//
//  ページ（縦のスクロール）のノードに付ける。W2-4・W2-5 の段は UiGalleryDemo（根）がつなぐので、ここは次の段だけ:
//    - 一覧（W2-3）: List の窓へ 100 行の SEED.UI.ListView（行のプレハブ list_row.actor。行の色・文字は ThemeStyle でテーマに結び付く）。
//      W2 の手直し P2-3 で行をフルスワイプで削除できる「一覧の見本」にした（行のスクリプト UiGalleryListRow.cs）:
//        データは項目の番号の列（_items）で持ち、行が流し切ったら（UiGalleryListRow.FullSwipedEvent）その行の高さを
//        motion.swipe_collapse の時間で 0 へ畳み（ListView.SetExtentOf の行ごとの長さ＋行の見た目の縦の縮み）、畳み終わったら
//        データから消して SetCount・Refresh（書き直す行は項目が変わるので UiGalleryListRow.Bound がスワイプを Reset）。
//        一覧かページのスクロールが始まったら開いている行を閉じる（SwipeGroup.CloseAll）。
//    - 画面の組み立て（W2-7）: NavDialog・NavSheet・NavOverlay・NavToast のボタンで Dialog・BottomSheet・TopSheet・Toast を開く
//      （根の ModalHost・ToastHost が受ける。シート・覆いの中身は nav_sheet_content・nav_overlay_content）
//    - 2026-10-02 の部品の拡充（NavButtons2）: 危険のボタンのダイアログ（NavDanger）・選択肢の一覧のダイアログ（NavMenu。長押しのメニュー）・
//      ボタンが入らず縦に積むダイアログ（NavStacked）・進捗の札（NavProgress。本文を途中で変えて 2.5 秒後に外から閉じる）・
//      本文が長くスクロールするダイアログ（NavLong）・先頭のアイコンつきのトースト（NavToastIcon）
//    - グラフ（W2-8）: GalleryLine（起床時間の 30 日・滑らかな曲線・平均の基準線）・GalleryBars（12 か月の積み上げ）
//  W2 の手直し P2-5 でページの中身を画面の幅に合わせる作り（Content の縦の CanvasStack の中に段〈ShapesSection など〉、段の中に行ごとの
//  CanvasWrap）にした。部品は名前で深さ優先に引くので、段・行の入れ物の下へ移っても同じ名前で届く。段の縦の位置は画面の幅で変わる
//  （折り返しの行の数が変わる）ので、ページの位置の命令は数の位置のほかにノードの名前でも指せる。一覧もページの幅に合わせて伸び縮みするので、
//  一覧の幅が変わったら行の文字の枠を合わせ直させる（UiGalleryListRow.RefitTextBoxes。行は毎フレーム自分の幅を読まない）。
//  デバッグの命令（SCRIPT_DEBUG:gallery,<名前>）: scroll,<位置>（ページを位置へすぐ移す。スクリーンショットの場面を作る）・
//  scroll,<ノードの名前>（そのノードの上の端がページの窓の上の端に来る位置へすぐ移す。範囲へ収める。P2-5）・
//  open,<dialog|sheet|overlay|toast|danger|menu|stacked|progress|long|toast-icon>（ボタンと同じ）・stats（一覧の作った行の数と見えている範囲）・
//  haptic,<none|tap|vibrate>（行のフルスワイプで構えたときの触感。P2-3）・list（件数・開いている行・畳んでいる行・一覧の位置と画面の矩形。P2-3）・
//  wide（幅いっぱいのスライダの溝の長さ・ボタンの幅と文字の枠の幅。2026-10-02）・rows（開いている選択肢の一覧の行の画面の矩形。2026-10-02）・
//  where,<ノードの名前>（ノードの画面の矩形〈画素〉。2026-10-02）。
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
    /// <summary>一覧の窓のノードの名前。</summary>
    private const string ListName = "List";
    /// <summary>シートの中身のプレハブ（W2-7 の見本）。</summary>
    private const string SheetContentPrefab = "assets://ui/prefabs/nav_sheet_content.actor";
    /// <summary>覆いの中身のプレハブ（W2-7 の見本）。</summary>
    private const string OverlayContentPrefab = "assets://ui/prefabs/nav_overlay_content.actor";
    /// <summary>一覧の項目の数（始め）・行の高さ（dp。list_row.actor の行の高さと同じ）。</summary>
    private const int ListCount = 100;
    private const float RowExtent = 56f;
    /// <summary>行の時刻の見本（時の始まり・時の幅・分の刻み）。</summary>
    private const int FirstHour = 5, HourSpan = 4, MinuteStride = 7, MinutesPerHour = 60;
    /// <summary>曜日の並びの見本。</summary>
    private static readonly string[] RepeatLabels = { "平日", "毎日", "土日", "月・水・金", "一度だけ" };
    /// <summary>行・項目が無い印（ログ）。</summary>
    private const int NoItem = -1;
    /// <summary>一覧の幅をまだ見ていない印。</summary>
    private const float NoListWidth = -1f;
    /// <summary>一覧の幅が変わったとみなす差の下限（dp。浮動小数の丸めの差では合わせ直さない）。</summary>
    private const float ListWidthEpsilon = 0.01f;
    /// <summary>進捗の札の見本: 本文を変えるまでの秒・閉じるまでの秒（2026-10-02）。</summary>
    private const float ProgressMessageAt = 1.2f, ProgressCloseAt = 2.5f;
    /// <summary>長い本文の見本の条の数（札に入りきらずスクロールする長さ。2026-10-02）。</summary>
    private const int LongArticles = 30;
    /// <summary>ダイアログの選択肢の一覧の窓（dialog.actor の Card/Items/Viewport）。</summary>
    private const string MenuViewportPath = "Card/Items/Viewport";

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
    /// <summary>一覧の窓（List）。</summary>
    private GameObject _listNode;
    /// <summary>一覧の行のスワイプの組（開いている行を 1 つにする・スクロールで閉じる）。</summary>
    private SwipeGroup? _swipeGroup;
    /// <summary>一覧のデータ（行の番号 → 項目の番号。消すと後ろが詰まる）。</summary>
    private readonly List<int> _items = new();
    /// <summary>畳んでいる項目（項目の番号 → 畳み始めてからの秒）。</summary>
    private readonly Dictionary<int, float> _collapsing = new();
    /// <summary>畳み終わった項目（毎フレーム使い回す作業用）。</summary>
    private readonly List<int> _collapsed = new();
    /// <summary>行ごとの長さ（畳んでいる間だけ ListView へ渡す）。</summary>
    private Func<int, float>? _extentOf;
    /// <summary>前のフレームに一覧を指でドラッグしていたか（ドラッグの始まりで開いている行を閉じる）。</summary>
    private bool _listWasDragging;
    /// <summary>最後に見た一覧の幅（dp。NoListWidth = まだ）。変わったら行の文字の枠を合わせ直させる（W2 の手直し P2-5）。</summary>
    private float _listWidth = NoListWidth;
    /// <summary>つないだ部品（二重につながない）。</summary>
    private readonly HashSet<UiWidget> _bound = new();
    /// <summary>データを入れたグラフの名前。</summary>
    private readonly HashSet<string> _charts = new();
    /// <summary>最後につなぎ直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>デバッグの命令の受け口。</summary>
    private Action<string>? _cmd;
    /// <summary>開いている進捗の札の見本（無ければ null）と開いてからの秒（2026-10-02）。</summary>
    private DialogHandle? _progress;
    private float _progressElapsed;
    /// <summary>進捗の札の見本の本文を変えたか。</summary>
    private bool _progressMessageChanged;
    /// <summary>最後に開いた選択肢の一覧のダイアログ（行の矩形の命令 rows が引く。2026-10-02）。</summary>
    private DialogHandle? _menu;

    public override void OnStart()
    {
        _cmd = OnCommand;
        SEED.Debug.OnCommand(CommandName, _cmd);
        for (int i = 0; i < ListCount; i++) _items.Add(i);
        _extentOf = ExtentOfIndex;
        var listNode = gameObject.FindChild(ListName);
        if (listNode.IsValid)
        {
            _listNode = listNode;
            _swipeGroup = SwipeGroup.For(listNode);
            _list = new ListView(listNode, RowPrefab, _items.Count, RowExtent, BindRow)
            {
                // 使い回す行（番号から外れる行）はスワイプを戻す（W2-3。P2-3 で構え・確定・文字の位置も戻る）
                Recycled = (row, _) => UiGalleryListRow.Released(row),
            };
        }
        // 行が流し切った（フルスワイプ・削除の面のタップ）→ 高さを畳んでからデータを消す
        On(UiGalleryListRow.FullSwipedEvent, (GameObject row) => OnRowFullSwiped(row));
    }

    public override void OnDestroy()
    {
        if (_cmd is not null) SEED.Debug.OffCommand(CommandName, _cmd);
        if (_listNode.IsValid) SwipeGroup.Release(_listNode);
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        CloseSwipesOnListDrag();
        WatchListWidth();
        AdvanceCollapse(SEED.Time.UnscaledDeltaTime);
        AdvanceProgressDemo(SEED.Time.UnscaledDeltaTime);
        _list?.Update();
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        BindButtons();
        BindCharts();
    }

    /// <summary>ページのスクロールが始まった: 一覧の開いている行を閉じる（行ごとページが動くので）。</summary>
    public override void OnScrollStart(ScrollEvent e) => _swipeGroup?.CloseAll();

    /// <summary>行へデータを入れる（色・大きさは行のプレハブの ThemeStyle がテーマから当てる。項目が変われば行のスワイプを戻す）。</summary>
    private void BindRow(GameObject row, int index)
    {
        int item = _items[index];
        int hour = FirstHour + item % HourSpan, minute = item * MinuteStride % MinutesPerHour;
        if (row.FindChild("Title").GetComponent<Text>() is { } title)
            title.Content = $"{hour}:{minute:00}　アラーム {item + 1}";
        if (row.FindChild("Sub").GetComponent<Text>() is { } sub)
            sub.Content = RepeatLabels[item % RepeatLabels.Length];
        UiGalleryListRow.Bound(row, item, ExtentOfItem(item), RowExtent);
    }

    /// <summary>一覧の窓を指でドラッグし始めたら、開いている行を閉じる（W2-3 の「スクロールが始まったら閉じる」。窓は別のノードなので見張る）。</summary>
    private void CloseSwipesOnListDrag()
    {
        if (!_listNode.IsValid || _listNode.GetComponent<CanvasScroll>() is not { } scroll) return;
        bool dragging = scroll.IsDragging;
        if (dragging && !_listWasDragging) _swipeGroup?.CloseAll();
        _listWasDragging = dragging;
    }

    /// <summary>
    /// 一覧の幅（レイアウトの大きさ。前のフレームの描画の値）が変わったら、行の文字の枠を合わせ直させる（W2 の手直し P2-5。
    /// 一覧はページの幅に合わせて伸び縮みする＝画面の回転・大きさの変化。行は毎フレーム自分の幅を読まない）。
    /// </summary>
    private void WatchListWidth()
    {
        if (!_listNode.IsValid || _listNode.GetComponent<CanvasTransform>() is not { HasLayout: true } ct) return;
        float width = ct.LayoutSize.x;
        if (MathF.Abs(width - _listWidth) < ListWidthEpsilon) return;
        _listWidth = width;
        UiGalleryListRow.RefitTextBoxes();
    }

    /// <summary>行が流し切った: その行の項目を畳み始める（畳み終わったら AdvanceCollapse がデータから消す）。</summary>
    private void OnRowFullSwiped(GameObject row)
    {
        if (_list is null) return;
        int index = _list.IndexOf(row);
        if (index < 0 || index >= _items.Count) return;
        int item = _items[index];
        if (!_collapsing.TryAdd(item, 0f)) return;
        _list.SetExtentOf(_extentOf);
        Report($"swipe collapse item={item} index={index}");
        Redraw.Request();
    }

    /// <summary>
    /// 畳む動きを進める: 畳み終わった項目をデータから消して行を書き直し（SetCount・Refresh）、畳んでいる行があれば
    /// 行ごとの長さを渡し直して（ListView が付いたままの行も置き直す）行の見た目を縮める。
    /// </summary>
    private void AdvanceCollapse(float dt)
    {
        if (_list is null || _collapsing.Count == 0) return;
        float duration = CollapseSeconds;
        _collapsed.Clear();
        foreach (var item in new List<int>(_collapsing.Keys))
        {
            float elapsed = _collapsing[item] + MathF.Max(0f, dt);
            _collapsing[item] = elapsed;
            if (elapsed >= duration) _collapsed.Add(item);
        }
        foreach (var item in _collapsed)
        {
            _collapsing.Remove(item);
            int index = _items.IndexOf(item);
            if (index < 0) continue;
            _items.RemoveAt(index);
            Report($"swipe removed item={item} index={index} count={_items.Count}");
        }
        // 行ごとの長さ: 畳んでいる項目が残れば渡し直す（並びを作り直させる）、無くなれば固定の長さへ戻す
        _list.SetExtentOf(_collapsing.Count > 0 ? _extentOf : null);
        if (_collapsed.Count > 0)
        {
            // データが詰まった: 付いている行をすべて書き直す（BindRow が項目の変わった行のスワイプを戻し、高さの見た目も当て直す）
            _list.SetCount(_items.Count);
            _list.Refresh();
        }
        else
        {
            // 畳んでいる行の見た目を縮める（行の番号 = 今のデータの番号。まだ詰めていないので付いている行と食い違わない）
            foreach (var (item, _) in _collapsing)
            {
                if (_list.RowOf(_items.IndexOf(item)) is { } row) UiGalleryListRow.SetHeight(row, ExtentOfItem(item), RowExtent);
            }
        }
        Redraw.Request();
    }

    /// <summary>行の番号の長さ（畳んでいる項目は縮んだ長さ）。</summary>
    private float ExtentOfIndex(int index) => index >= 0 && index < _items.Count ? ExtentOfItem(_items[index]) : RowExtent;

    /// <summary>項目の行の長さ（畳んでいる途中なら SwipeMath.CollapsedExtent）。</summary>
    private float ExtentOfItem(int item)
        => _collapsing.TryGetValue(item, out var elapsed) ? SwipeMath.CollapsedExtent(RowExtent, elapsed, CollapseSeconds) : RowExtent;

    /// <summary>消した行を畳む時間（テーマの motion.swipe_collapse）。</summary>
    private static float CollapseSeconds => UiTheme.Number(UiTokens.MotionSwipeCollapse, SwipeMath.DefaultCollapseSeconds);

    /// <summary>画面の組み立ての呼び出しのボタンをつなぐ（2026-10-02 の拡充の見本のボタンも）。</summary>
    private void BindButtons()
    {
        var buttons = new[]
        {
            ("NavDialog", "dialog"), ("NavSheet", "sheet"), ("NavOverlay", "overlay"), ("NavToast", "toast"),
            ("NavDanger", "danger"), ("NavMenu", "menu"), ("NavStacked", "stacked"), ("NavProgress", "progress"),
            ("NavLong", "long"), ("NavToastIcon", "toast-icon"),
        };
        foreach (var (name, what) in buttons)
        {
            if (UiWidget.Of<Button>(gameObject.FindChild(name)) is { } b && _bound.Add(b))
            {
                string kind = what;
                b.Clicked += _ => Open(kind);
            }
        }
    }

    /// <summary>重ねる面・トーストを開く（W2-7 の API。2026-10-02 の拡充: 危険・選択肢の一覧・縦積み・進捗の札・長い本文・アイコンのトースト）。</summary>
    private void Open(string what)
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
            // ── 2026-10-02 の拡充 ──
            case "danger":
                // 危険（取り消せない操作）のボタン: Positive の塗りが color.error になる
                ReportResult("danger", Dialog.Show(new DialogOptions
                {
                    Title = "アラームを削除しますか？", Message = "削除したアラームは元に戻せません。",
                    PositiveText = "削除", PositiveKind = DialogButtonKind.Danger, NegativeText = "やめる",
                }));
                break;
            case "menu":
                // 選択肢の一覧（長押しのメニュー）: 押した項目で閉じる。選べない項目は灰色・危険の項目は color.error
                _menu = Dialog.ShowMenu("7:30 のアラーム",
                    new DialogMenuItem("編集する", UiIcon.Circle()),
                    new DialogMenuItem("複製する", UiIcon.Square()),
                    new DialogMenuItem("共有する（準備中）", UiIcon.Ring()) { Enabled = false },
                    new DialogMenuItem("削除する", UiIcon.Ring(), DialogButtonKind.Danger));
                ReportResult("menu", _menu);
                break;
            case "stacked":
                // ボタンの文字が長く札の中の幅（264）に入らない: 縦に積んで右へ寄せる（Flutter の OverflowBar）
                ReportResult("stacked", Dialog.Show(new DialogOptions
                {
                    Title = "アラームが鳴らないときは", Message = "端末の省電力の設定で止められていることがあります。",
                    NeutralText = "あとで確かめる", NegativeText = "通知の設定を開く", PositiveText = "電池の最適化を開く",
                }));
                break;
            case "progress":
                // 進捗の札: ボタンなし・幕と戻るでは閉じない。本文を途中で変えて、終わったら外から閉じる（AdvanceProgressDemo）
                _progress = Dialog.ShowProgress("購入の手続きをしています…", "購入");
                _progressElapsed = 0f;
                _progressMessageChanged = false;
                ReportResult("progress", _progress);
                break;
            case "long":
                // 本文が長い: 札が画面に入らない分だけ本文の窓を縮めてスクロールにする（題とボタンは見えたまま）
                ReportResult("long", Dialog.Show(new DialogOptions { Title = "利用規約", Message = LongMessage(), PositiveText = "同意する", NegativeText = "閉じる" }));
                break;
            case "toast-icon":
                // 先頭のアイコン（図形か画像）つきのトースト
                Toast.Show("保存しました", UiIcon.Ring());
                Toast.Show("通信に失敗しました", UiIcon.Circle(UiTheme.Current.Color(UiTokens.ColorError)), ToastLength.Long);
                break;
        }
        Report($"open {what}");
    }

    /// <summary>ダイアログの結果（と選んだ項目の番号）をログへ出す。</summary>
    private static void ReportResult(string what, DialogHandle? handle)
    {
        if (handle is null) return;
        handle.Completed += r => Report($"{what} result={r} selected={handle.SelectedIndex}");
    }

    /// <summary>長い本文の見本（条を並べた文。札に入りきらない長さ）。</summary>
    private static string LongMessage()
    {
        var lines = new List<string>();
        for (int i = 1; i <= LongArticles; i++)
            lines.Add($"第 {i} 条　アラームの鳴動と、寝坊したときの支払いの決まりを定めます。");
        return string.Join("\n", lines);
    }

    /// <summary>進捗の札の見本: 途中で本文を変え、決めた秒が過ぎたら外から Positive で閉じる（実時間）。</summary>
    private void AdvanceProgressDemo(float dt)
    {
        if (_progress is null) return;
        if (_progress.IsClosed)
        {
            _progress = null;
            return;
        }
        _progressElapsed += MathF.Max(0f, dt);
        if (!_progressMessageChanged && _progressElapsed >= ProgressMessageAt)
        {
            _progressMessageChanged = true;
            _progress.SetMessage("もう少しで終わります。通信が終わるまでアプリを閉じないでください。");
            Report("progress message");
        }
        if (_progressElapsed >= ProgressCloseAt)
        {
            _progress.Close(DialogResult.Positive);
            _progress = null;
        }
        Redraw.Request();
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
            // W2 の手直し P2-5: 段の位置は画面の幅で変わるので、ノードの名前で指す
            case "scroll":
                ScrollToNode(At(1));
                break;
            case "open":
                Open(At(1));
                break;
            case "stats":
                Report($"stats list created={_list?.CreatedRowCount ?? 0} visible={_list?.VisibleRange.First ?? -1}..{_list?.VisibleRange.Last ?? -1}");
                break;
            // W2 の手直し P2-3: 行のフルスワイプで構えたときの触感（none・tap・vibrate）
            case "haptic" when Enum.TryParse<SwipeHaptic>(At(1), ignoreCase: true, out var haptic):
                UiGalleryListRow.Haptic = haptic;
                Report($"haptic {haptic}");
                break;
            // W2 の手直し P2-3: 一覧の件数・開いている行・畳んでいる行・位置と画面の矩形（実機・PC の確かめで行の位置を求める）
            case "list":
                ReportList();
                break;
            // 2026-10-02: 幅いっぱいのスライダの溝の長さ・ボタンの文字の枠（窓の幅を変えて追従を確かめる）
            case "wide":
                ReportWide();
                break;
            // 2026-10-02: 開いている選択肢の一覧の行の画面の矩形（行のタップの位置を求める）
            case "rows":
                ReportRows();
                break;
            case "where":
                ReportWhere(At(1));
                break;
        }
    }

    /// <summary>幅いっぱいのスライダとボタンの寸法を出す（溝の長さ・部品のレイアウトの幅・文字の枠の幅）。</summary>
    private void ReportWide()
    {
        var sliderNode = gameObject.FindChild("WideSlider");
        var buttonNode = gameObject.FindChild("WideButton");
        float sliderWidth = sliderNode.GetComponent<CanvasTransform>() is { HasLayout: true } st ? st.LayoutSize.x : -1f;
        float buttonWidth = buttonNode.GetComponent<CanvasTransform>() is { HasLayout: true } bt ? bt.LayoutSize.x : -1f;
        float track = UiWidget.Of<Slider>(sliderNode)?.TrackLength ?? -1f;
        float labelBox = buttonNode.FindChild("Label").GetComponent<Text>() is { } label ? label.BoxWidth : -1f;
        Report($"wide slider={sliderWidth:0.##} track={track:0.##} button={buttonWidth:0.##} labelBox={labelBox:0.##}");
    }

    /// <summary>
    /// 開いている選択肢の一覧のダイアログの窓の画面の矩形（画素）と、1 行の高さ（画素）を出す（行は窓の上から行の高さごとに並ぶ。
    /// 同じ名前の行はノードの名前で引き分けられないので、窓の矩形から行の位置を求めてもらう）。
    /// </summary>
    private void ReportRows()
    {
        if (_menu is not { Root.IsValid: true } menu
            || menu.Root.FindChild(MenuViewportPath).GetComponent<CanvasTransform>() is not { HasLayout: true } ct || ct.LayoutSize.y <= 0f)
        {
            Report("rows none");
            return;
        }
        var rect = ct.LayoutRect;
        float rowPx = UiTheme.Number(NavTokens.SizeDialogItemHeight) * rect.height / ct.LayoutSize.y;
        Report($"rows viewport={rect.x:0.#},{rect.y:0.#},{rect.width:0.#},{rect.height:0.#} row={rowPx:0.##}");
    }

    /// <summary>名前のノードの画面の矩形（画素）を出す。</summary>
    private static void ReportWhere(string name)
    {
        var node = GameObject.Find(name);
        var rect = node.GetComponent<CanvasTransform>() is { HasLayout: true } ct ? ct.LayoutRect : Rect.Zero;
        Report($"where {name} {rect.x:0.#},{rect.y:0.#},{rect.width:0.#},{rect.height:0.#}");
    }

    /// <summary>
    /// ページを名前のノードの上の端へすぐ移す（W2 の手直し P2-5）。前のフレームの描画のレイアウトの矩形（LayoutRect。画面の画素）の
    /// ページの窓からの差を、窓の画素 ÷ 窓の大きさ（キャンバスの単位）でページの単位へ直して今の位置へ足し、0〜MaxPosition へ収める。
    /// 矩形は 1 フレーム遅れの値なので、ページが止まっている間に使う（スクリーンショットの場面を作る命令）。
    /// </summary>
    /// <param name="name">ノードの名前（ページの下を深さ優先で引く）。</param>
    private void ScrollToNode(string name)
    {
        var target = gameObject.FindChild(name);
        if (gameObject.GetComponent<CanvasScroll>() is not { } scroll || gameObject.GetComponent<CanvasTransform>() is not { } page
            || target.GetComponent<CanvasTransform>() is not { } node || !page.HasLayout || !node.HasLayout || page.LayoutSize.y <= 0f)
        {
            Report($"scroll failed {name}");
            return;
        }
        float pixelsPerUnit = page.LayoutRect.height / page.LayoutSize.y;
        float y = scroll.Position.y + (node.LayoutRect.y - page.LayoutRect.y) / pixelsPerUnit;
        y = Math.Clamp(y, 0f, MathF.Max(0f, scroll.MaxPosition.y));
        scroll.JumpTo(new Vector2(0f, y));
        Redraw.Request();
        Report($"scroll {name} {y:0.#} max={scroll.MaxPosition.y:0.#} content={scroll.ContentSize.y:0.#} viewport={scroll.ViewportSize.y:0.#}");
    }

    /// <summary>一覧の様子を 1 行で出す（件数・開いている行の項目・畳んでいる数・見えている範囲・一覧の位置・画面の矩形〈画素〉・触感）。</summary>
    private void ReportList()
    {
        int openItem = _swipeGroup?.OpenMember is { } open ? UiGalleryListRow.ItemOf(open.Front.Parent) : NoItem;
        var scroll = _listNode.IsValid ? _listNode.GetComponent<CanvasScroll>() : null;
        var rect = _listNode.IsValid && _listNode.GetComponent<CanvasTransform>() is { } ct ? ct.LayoutRect : Rect.Zero;
        var range = _list?.VisibleRange ?? ListRange.Empty;
        Report($"list count={_items.Count} open={openItem} collapsing={_collapsing.Count} visible={range.First}..{range.Last} "
               + $"pos={(scroll?.Position.y ?? 0f):0.#} rect={rect.x:0.#},{rect.y:0.#},{rect.width:0.#},{rect.height:0.#} "
               + $"haptic={UiGalleryListRow.Haptic}");
    }

    /// <summary>知らせをログと画面の Log へ出す。</summary>
    private static void Report(string message)
    {
        SEED.Debug.Log($"{LogPrefix} {message}");
        if (GameObject.Find("Log").GetComponent<Text>() is { } log) log.Content = message;
    }
}
