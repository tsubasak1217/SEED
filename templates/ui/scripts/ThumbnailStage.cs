// ============================================================
//  ThumbnailStage.cs — テンプレートアクタの見本の画像（サムネイル）を撮る「舞台」のスクリプト
//
//  【誰が使うか】
//  サムネイルの生成ツール（editor/tools/SeedTemplateThumbnails。docs/template_library.md §9.10）が作る
//  一時のシーンの根（舞台）に付ける。利用者のゲームには入らない（どのテンプレートからも参照されないので、
//  テンプレートアクタの追加でプロジェクトへコピーされることもない）。
//
//  【何をするか】
//  1. 見本の操作（Action）を行う。シーンに置いただけでは見えない部品を「見える状態」にするため:
//       dialog     … 確認のダイアログを開く（受け皿 ModalHost が要る）
//       menu       … 選択肢の一覧のダイアログを開く（項目は Items。'|' 区切り）
//       progress   … 進捗の札を開く
//       sheet      … 下からのシートを開く（中身は ContentPrefab）
//       overlay    … 上からの覆いを開く（中身は ContentPrefab）
//       popup      … 中央のポップアップを開く（中身は ContentPrefab。2026-10-02）
//       toast      … トーストを出す（受け皿 ToastHost が要る）
//       focus      … 方向キー・パッドのフォーカスの枠を出す（舞台の読む順の最初の部品へ UiNavigation.FocusFirstIn。
//                    シーンに UiNavigator が無ければ ContentPrefab〈ui_navigator.actor〉を舞台の根に作る。2026-10-03）
//       line_chart … 折れ線グラフ（Target のノード）へ値を入れる（Values = 分。空の値は記録の無い日）
//       bar_chart  … 棒グラフ（Target のノード）へ値を入れる（Values = ',' で棒、';' で積み上げ）
//       none       … 何もしない（置いただけで見える部品）
//     受け皿・部品のスクリプトの OnStart の順は決まっていないので、できるまで毎フレームやり直す。
//  2. できたら SettleSeconds（実時間）だけ待って動き（開く動き・伸び縮み）を落ち着かせ、
//     「[THUMB] ready <Ticket>」をログへ出す。ツールはこの行を合図にスクリーンショットを撮る。
//     GiveUpSeconds を過ぎてもできなければ「[THUMB] failed <Ticket> <理由>」を出す（ツールはその件を失敗として飛ばす）。
//
//  【データドリブン】
//  欄の値はすべてカタログ（templates/<フォルダ>/template_actors.json）の thumbnail_sample.script から
//  ツールが書き込む。見本の文言や値を変えるときはカタログだけを直す（このスクリプトは触らない）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using SEED;
using SEED.UI;
using SEEDEditor.Scripting;

public class ThumbnailStage : SEEDScript
{
    // ── ログの合図（ツールの ThumbnailRuntimeSession と同じ文字列）────────

    /// <summary>ログの接頭辞（ツールはこの語を含む行だけを見る）。</summary>
    private const string LogPrefix = "[THUMB]";
    /// <summary>撮ってよい合図の語。</summary>
    private const string ReadyWord = "ready";
    /// <summary>諦めた合図の語。</summary>
    private const string FailedWord = "failed";

    // ── 見本の操作の名前 ────────────────────────────────

    private const string ActionNone = "none";
    private const string ActionDialog = "dialog";
    private const string ActionMenu = "menu";
    private const string ActionProgress = "progress";
    private const string ActionSheet = "sheet";
    private const string ActionOverlay = "overlay";
    private const string ActionPopup = "popup";
    private const string ActionToast = "toast";
    private const string ActionFocus = "focus";
    private const string ActionLineChart = "line_chart";
    private const string ActionBarChart = "bar_chart";

    // ── 値の書き方 ─────────────────────────────────────

    /// <summary>項目（選択肢の一覧）の区切り。</summary>
    private const char ItemSeparator = '|';
    /// <summary>項目の先頭に付けると危険（color.error）の項目になる印。</summary>
    private const char DangerMark = '!';
    /// <summary>項目の先頭に付けると選べない（灰色）の項目になる印。</summary>
    private const char DisabledMark = '~';
    /// <summary>値（点・棒）の区切り。</summary>
    private const char ValueSeparator = ',';
    /// <summary>棒の積み上げの区切り。</summary>
    private const char StackSeparator = ';';
    /// <summary>始まりの日の書式。</summary>
    private const string DateFormat = "yyyy-MM-dd";
    /// <summary>始まりの日が空・読めないときの日（見本の値なので固定。実行のたびに同じ絵にする）。</summary>
    private static readonly DateOnly DefaultStartDate = new(2026, 9, 1);
    /// <summary>撮る前に待つ秒の既定（ツールは毎回書き込むので、手で置いたときの値）。</summary>
    private const float DefaultSettleSeconds = 0.8f;
    /// <summary>やり直しの上限の秒の既定。</summary>
    private const float DefaultGiveUpSeconds = 10f;
    /// <summary>選択肢の一覧の項目に順に当てる図形のアイコンの数（丸・四角・輪）。</summary>
    private const int MenuIconShapeCount = 3;
    /// <summary>図形のアイコンの並びの番号（丸・四角）。それ以外は輪。</summary>
    private const int CircleIconIndex = 0, SquareIconIndex = 1;

    // ── 欄（ツールが thumbnail_sample.script から書き込む）──────────

    /// <summary>見本の操作（none・dialog・menu・progress・sheet・overlay・popup・toast・focus・line_chart・bar_chart）。</summary>
    [SerializeField(Label = "見本の操作")]
    public string Action = ActionNone;

    /// <summary>値を入れる部品のノードの名前（グラフ。深さ優先で探す）。</summary>
    [SerializeField(Label = "対象のノードの名前")]
    public string Target = "";

    /// <summary>ダイアログ・選択肢の一覧・進捗の札の題。</summary>
    [SerializeField(Label = "題")]
    public string Title = "";

    /// <summary>ダイアログ・進捗の札・トーストの本文。</summary>
    [SerializeField(Label = "本文")]
    public string Message = "";

    /// <summary>ダイアログの肯定のボタンの文字（空なら出さない）。</summary>
    [SerializeField(Label = "肯定のボタン")]
    public string PositiveText = "";

    /// <summary>ダイアログの否定のボタンの文字（空なら出さない）。</summary>
    [SerializeField(Label = "否定のボタン")]
    public string NegativeText = "";

    /// <summary>選択肢の一覧の項目（'|' 区切り。先頭の '!' で危険、'~' で選べない）。</summary>
    [SerializeField(Label = "項目（| 区切り）")]
    public string Items = "";

    /// <summary>シート・覆い・ポップアップの中身のプレハブ（assets://…）。focus では UiNavigator が無いときに作る入口のプレハブ。</summary>
    [SerializeField(Label = "中身のプレハブ")]
    public string ContentPrefab = "";

    /// <summary>グラフの値（',' 区切り。棒の積み上げは ';'。折れ線の空の値は記録の無い日）。</summary>
    [SerializeField(Label = "値（, 区切り。積み上げは ;）")]
    public string Values = "";

    /// <summary>グラフの最初の値の日（yyyy-MM-dd。1 つ目の値をこの日に置き、以後 1 日ずつ）。</summary>
    [SerializeField(Label = "始まりの日（yyyy-MM-dd）")]
    public string StartDate = "";

    /// <summary>見本の操作ができてから撮る合図を出すまでの秒（実時間。開く動きなどを落ち着かせる）。</summary>
    [SerializeField(Label = "撮る前に待つ秒")]
    public float SettleSeconds = DefaultSettleSeconds;

    /// <summary>見本の操作をやり直す上限の秒（実時間。過ぎたら諦めた合図を出す）。</summary>
    [SerializeField(Label = "やり直しの上限の秒")]
    public float GiveUpSeconds = DefaultGiveUpSeconds;

    /// <summary>合図に添える札（ツールが件ごとに付ける。前の件の合図と取り違えないため）。</summary>
    [SerializeField(Label = "合図の札")]
    public string Ticket = "";

    // ── 状態 ────────────────────────────────────────

    /// <summary>見本の操作ができたか。</summary>
    private bool _performed;
    /// <summary>合図（ready / failed）を出したか（1 度だけ出す）。</summary>
    private bool _reported;
    /// <summary>見本の操作を始めてからの秒（実時間）。</summary>
    private float _sinceStart;
    /// <summary>見本の操作ができてからの秒（実時間）。</summary>
    private float _sincePerformed;
    /// <summary>最後にやり直せなかった理由（諦めたときに添える）。</summary>
    private string _lastReason = "";
    /// <summary>focus で入口（UiNavigator）のプレハブを作ったか（作るのは 1 度だけ。動き始めるのは次のフレーム）。</summary>
    private bool _navigatorCreated;

    /// <summary>
    /// 毎フレーム: 見本の操作をできるまでやり直し、できたら待ってから合図を出す。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (_reported) return;
        float dt = MathF.Max(0f, ctx.UnscaledDeltaTime);

        if (!_performed)
        {
            _sinceStart += dt;
            _performed = TryPerform(out _lastReason);
            if (!_performed && _sinceStart >= GiveUpSeconds)
            {
                Report(FailedWord + " " + Ticket + " " + _lastReason);
            }
            return;
        }

        _sincePerformed += dt;
        if (_sincePerformed >= SettleSeconds) Report(ReadyWord + " " + Ticket);
    }

    // ============================================================
    //  見本の操作
    // ============================================================

    /// <summary>
    /// 見本の操作を 1 度試す。
    /// </summary>
    /// <param name="reason">できなかった理由（できたら空）。</param>
    /// <returns>できたら true（受け皿・部品がまだ無ければ false。次のフレームでやり直す）。</returns>
    private bool TryPerform(out string reason)
    {
        reason = "";
        switch (Action.Trim())
        {
            case "":
            case ActionNone:
                return true;
            case ActionDialog:
                return Opened(Dialog.Show(new DialogOptions
                {
                    Title = Title, Message = Message, PositiveText = PositiveText, NegativeText = NegativeText,
                }), "ModalHost", out reason);
            case ActionMenu:
                return Opened(Dialog.ShowMenu(Title, ParseMenuItems(Items)), "ModalHost", out reason);
            case ActionProgress:
                return Opened(Dialog.ShowProgress(Message, Title), "ModalHost", out reason);
            case ActionSheet:
                return Opened(BottomSheet.Show(new SheetOptions { ContentPrefab = ContentPrefab }), "ModalHost", out reason);
            case ActionOverlay:
                return Opened(TopSheet.Show(new OverlayOptions { ContentPrefab = ContentPrefab }), "ModalHost", out reason);
            case ActionPopup:
                return Opened(Popup.Show(new PopupOptions { ContentPrefab = ContentPrefab }), "ModalHost", out reason);
            case ActionToast:
                return Opened(Toast.Show(Message, UiIcon.Ring(), ToastLength.Long), "ToastHost", out reason);
            case ActionFocus:
                return ShowFocusRing(out reason);
            case ActionLineChart:
                return FillLineChart(out reason);
            case ActionBarChart:
                return FillBarChart(out reason);
            default:
                // 知らない操作はやり直しても変わらないので、すぐ諦める
                reason = "unknown action: " + Action;
                _sinceStart = GiveUpSeconds;
                return false;
        }
    }

    /// <summary>開いた結果（null = 受け皿がまだ無い）を「できたか」に直す。</summary>
    private static bool Opened(object? handle, string hostName, out string reason)
    {
        reason = handle is null ? hostName + " is not ready" : "";
        return handle is not null;
    }

    /// <summary>項目の文字列（'|' 区切り）を選択肢の一覧の項目にする（図形のアイコンを順に当てる）。</summary>
    private static DialogMenuItem[] ParseMenuItems(string text)
    {
        var items = new List<DialogMenuItem>();
        int index = 0;
        foreach (var raw in text.Split(ItemSeparator))
        {
            var label = raw.Trim();
            if (label.Length == 0) continue;
            bool danger = label[0] == DangerMark;
            bool disabled = label[0] == DisabledMark;
            if (danger || disabled) label = label.Substring(1).Trim();
            var item = new DialogMenuItem(label, IconAt(index), danger ? DialogButtonKind.Danger : DialogButtonKind.Default)
            {
                Enabled = !disabled,
            };
            items.Add(item);
            index++;
        }
        return items.ToArray();
    }

    /// <summary>項目の並びの番号から図形のアイコンを選ぶ（丸 → 四角 → 輪 の繰り返し）。</summary>
    private static UiIcon IconAt(int index) => (index % MenuIconShapeCount) switch
    {
        CircleIconIndex => UiIcon.Circle(),
        SquareIconIndex => UiIcon.Square(),
        _ => UiIcon.Ring(),
    };

    // ============================================================
    //  フォーカスの枠（方向キー・パッドの操作）
    // ============================================================

    /// <summary>
    /// 方向キー・パッドのフォーカスの枠を出す: 舞台（このスクリプトの付いた根）の下で読む順の最初の部品へ
    /// 枠つきでフォーカスする（UiNavigation.FocusFirstIn）。枠を重ねるのは UiNavigator なので、
    /// シーンに無ければ ContentPrefab（ui_navigator.actor）を舞台の根の子に 1 度だけ作る（動き始めるのは次のフレーム）。
    /// 枠が見える状態（UiNavigation.IsRingVisible）になったらできたとする。
    /// </summary>
    /// <param name="reason">できなかった理由（できたら空）。</param>
    /// <returns>枠が見える状態なら true（入口がまだ動いていない・部品がまだ測れていなければ false。次のフレームでやり直す）。</returns>
    private bool ShowFocusRing(out string reason)
    {
        if (!UiNavigation.HasNavigator)
        {
            var prefab = ContentPrefab.Trim();
            if (!_navigatorCreated && prefab.Length > 0)
            {
                GameObject.Instantiate(prefab, gameObject);
                _navigatorCreated = true;
            }
            reason = "UiNavigator is not ready";
            return false;
        }
        // 部品が作った直後でまだ測れていなければ FocusFirstIn は待ちに入って false を返す。毎フレーム頼み直してよい
        if (!UiNavigation.IsRingVisible) UiNavigation.FocusFirstIn(gameObject, showRing: true);
        if (UiNavigation.IsRingVisible)
        {
            reason = "";
            return true;
        }
        reason = "no navigable widget is ready";
        return false;
    }

    // ============================================================
    //  グラフ
    // ============================================================

    /// <summary>折れ線グラフへ値（分）を入れる。</summary>
    private bool FillLineChart(out string reason)
    {
        if (UiWidget.Of<LineChart>(gameObject.FindChild(Target)) is not { } chart)
        {
            reason = "LineChart '" + Target + "' is not ready";
            return false;
        }
        var start = ParseStartDate();
        var points = new List<ChartPoint>();
        var cells = Values.Split(ValueSeparator);
        for (int i = 0; i < cells.Length; i++)
        {
            double? minutes = TryNumber(cells[i], out var value) ? value : null;
            points.Add(ChartPoint.Minutes(start.AddDays(i), minutes is { } m ? TimeSpan.FromMinutes(m) : null));
        }
        if (points.Count > 0)
            chart.FixedXRange = new ChartRange(start.DayNumber, start.AddDays(points.Count - 1).DayNumber);
        chart.SetSeries(points);
        reason = "";
        return true;
    }

    /// <summary>棒グラフへ値を入れる（',' で棒、';' で積み上げ）。</summary>
    private bool FillBarChart(out string reason)
    {
        if (UiWidget.Of<BarChart>(gameObject.FindChild(Target)) is not { } chart)
        {
            reason = "BarChart '" + Target + "' is not ready";
            return false;
        }
        var start = ParseStartDate();
        var bars = new List<BarDatum>();
        var cells = Values.Split(ValueSeparator);
        for (int i = 0; i < cells.Length; i++)
        {
            var stack = new List<double>();
            foreach (var part in cells[i].Split(StackSeparator))
                if (TryNumber(part, out var value)) stack.Add(value);
            bars.Add(new BarDatum(start.AddDays(i).DayNumber, stack.ToArray()));
        }
        chart.SetData(bars);
        reason = "";
        return true;
    }

    /// <summary>始まりの日を読む（空・読めなければ既定の日）。</summary>
    private DateOnly ParseStartDate() =>
        DateOnly.TryParseExact(StartDate.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : DefaultStartDate;

    /// <summary>数を読む（空・読めなければ false）。</summary>
    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ============================================================
    //  合図
    // ============================================================

    /// <summary>合図をログへ出す（1 度だけ）。</summary>
    private void Report(string body)
    {
        _reported = true;
        SEED.Debug.Log(LogPrefix + " " + body);
    }
}
