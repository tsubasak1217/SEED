// ============================================================
//  UiBindingDemo.cs — データバインディング（SEED.Binding）の最小の見本（docs/ui_binding.md §8）
//
//  状態を観測値で持ち、UI は Bind.* で結ぶ。「状態が変わったら UI を直す」Refresh() を 1 つも書かない:
//    - カウンタ: Observable<int> → 文字（Bind.Text の書式）・10 回以上でバッジを見せる（Bind.Visible の判定つき）
//    - トグル  : Observable<bool> ⇔ トグル（Bind.Toggle。部品の OnStart の前でもノードから結べる）・状態の文字
//    - 一覧    : ObservableList<string> → ListView（Bind.RowBinder ＋ Bind.List）。＋1 を押すたびに先頭へ 1 行
//    - まとめ  : Computed（回数と通知の 2 つから導く文字）
//  結び付けは全部 owner: this なので、このスクリプトの破棄で自動で外れる（OnDestroy に後片付けを書かない）。
//
//  シーンの根（このスクリプトのアクタ）の子に置くもの（無い子は飛ばす）:
//    CounterLabel（Text）・CounterSlot（＋1 のボタンを作る枠）・Badge（10 回以上で見せる印）・
//    ToggleSlot（トグルを作る枠）・ToggleLabel（Text）・Summary（Text）・
//    List（CanvasScroll ＋ CanvasClip の窓。行は templates/ui/prefabs/list_row.actor）
//  デバッグの命令（SCRIPT_DEBUG:binding,<命令>）: plus（＋1）・toggle（通知の切り替え）・clear（一覧を空に）・
//  burst（1 フレームで 100 回足す。Bind.Deferred の見出しは 1 回だけ書く）・state（今の値をログへ）。
// ============================================================
using System;
using SEED;
using SEED.Binding;
using SEED.UI;
using SEEDEditor.Scripting;

public class UiBindingDemo : SEEDScript
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] binding:";
    /// <summary>デバッグの命令の名前。</summary>
    private const string CommandName = "binding";

    // ── プレハブ ──
    private const string ButtonPrefab = "assets://ui/prefabs/button.actor";
    private const string TogglePrefab = "assets://ui/prefabs/toggle.actor";
    private const string RowPrefab = "assets://ui/prefabs/list_row.actor";

    // ── シーンの子の名前 ──
    private const string CounterLabelName = "CounterLabel";
    private const string CounterSlotName = "CounterSlot";
    private const string BadgeName = "Badge";
    private const string ToggleSlotName = "ToggleSlot";
    private const string ToggleLabelName = "ToggleLabel";
    private const string SummaryName = "Summary";
    private const string ListName = "List";
    /// <summary>行の子の名前（list_row.actor）。</summary>
    private const string RowTitleName = "Title", RowSubName = "Sub";

    // ── 見本の値 ──
    /// <summary>バッジを見せ始める回数。</summary>
    private const int BadgeThreshold = 10;
    /// <summary>行の高さ（dp。list_row.actor の行の高さと同じ）。</summary>
    private const float RowExtent = 56f;
    /// <summary>burst の命令で 1 フレームに足す回数。</summary>
    private const int BurstCount = 100;
    /// <summary>＋1 のボタンの文字。</summary>
    private const string PlusText = "＋1";

    // ── 状態（観測値）。UI はこれを読むだけ ──
    /// <summary>押した回数。</summary>
    private readonly Observable<int> _count = new(0);
    /// <summary>通知のオン・オフ（トグルと双方向）。</summary>
    private readonly Observable<bool> _notify = new(true);
    /// <summary>押した記録（新しい順）。</summary>
    private readonly ObservableList<string> _history = new();

    /// <summary>一覧（ListView の Update は今までどおり毎フレーム呼ぶ）。</summary>
    private ListView? _list;
    /// <summary>＋1 のボタン（クリックは出来事なので、部品が始まってから従来どおりつなぐ）。</summary>
    private Button? _plus;
    /// <summary>＋1 のボタンのノード。</summary>
    private GameObject _plusNode;
    /// <summary>最後に見た登録簿の版（ボタンの OnStart の後につなぐ）。</summary>
    private int _registryVersion = -1;
    /// <summary>デバッグの命令の受け口。</summary>
    private Action<string>? _cmd;

    public override void OnStart()
    {
        _cmd = OnCommand;
        SEED.Debug.OnCommand(CommandName, _cmd);

        // ── カウンタ: 回数 → 文字（Deferred で 1 フレームのまとめ。burst で 100 回足しても書くのは 1 回）──
        if (TextAt(CounterLabelName) is { } counterLabel)
            Bind.Text(this, counterLabel, Bind.Deferred(_count), n => $"押した回数: {n}");
        Bind.Visible(this, gameObject.FindChild(BadgeName), _count, n => n >= BadgeThreshold);
        var counterSlot = gameObject.FindChild(CounterSlotName);
        if (counterSlot.IsValid) _plusNode = GameObject.Instantiate(ButtonPrefab, counterSlot);

        // ── トグル: 通知 ⇔ トグル（作ったばかりのプレハブのトグルは次のフレームに始まる。結び付けが待って当てる）──
        var toggleSlot = gameObject.FindChild(ToggleSlotName);
        if (toggleSlot.IsValid) Bind.Toggle(this, GameObject.Instantiate(TogglePrefab, toggleSlot), _notify);
        if (TextAt(ToggleLabelName) is { } toggleLabel)
            Bind.Text(this, toggleLabel, _notify, on => on ? "通知: オン" : "通知: オフ");

        // ── まとめ: 2 つの観測値から導く（結び付けが外れれば依存の購読も外れる）──
        if (TextAt(SummaryName) is { } summary)
            Bind.Text(this, summary, Computed.From(_count, _notify, (n, on) => $"{n} 回・通知{(on ? "あり" : "なし")}"));

        // ── 一覧: 行の入れ方は一覧から（RowBinder）、数・中身の変化は Bind.List が ListView へ写す ──
        var listNode = gameObject.FindChild(ListName);
        if (listNode.IsValid)
        {
            _list = new ListView(listNode, RowPrefab, _history.Count, RowExtent, Bind.RowBinder(_history, BindRow));
            Bind.List(this, _list, _history, BindRow);
        }

        // 観測値の購読もスクリプトの寿命に合わせられる（ログは確かめ用）
        _notify.Subscribe(this, on => Report($"notify={on}"));
    }

    public override void OnDestroy()
    {
        // 結び付けは owner: this で自動で外れる。デバッグの命令だけ外す
        if (_cmd is not null) SEED.Debug.OffCommand(CommandName, _cmd);
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        _list?.Update();
        if (_plus is not null || _registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        if (UiWidget.Of<Button>(_plusNode) is not { } plus) return;
        _plus = plus;
        plus.SetText(PlusText);
        plus.Clicked += _ => Plus();
    }

    /// <summary>＋1: 状態を変えるだけ（文字・バッジ・まとめ・一覧は結び付けが直す）。</summary>
    private void Plus()
    {
        _count.Value++;
        _history.Insert(0, $"{_count.Value} 回目");
    }

    /// <summary>行へ項目を入れる（ListView が見えた行へ・Bind.List が置き換えた行へ呼ぶ）。</summary>
    private static void BindRow(GameObject row, string item, int index)
    {
        if (row.FindChild(RowTitleName).GetComponent<Text>() is { } title) title.Content = item;
        if (row.FindChild(RowSubName).GetComponent<Text>() is { } sub) sub.Content = $"#{index}";
    }

    /// <summary>子の Text（無ければ null）。</summary>
    private Text? TextAt(string name) => gameObject.FindChild(name).GetComponent<Text>();

    /// <summary>デバッグの命令（SCRIPT_DEBUG:binding,<命令>）。</summary>
    private void OnCommand(string args)
    {
        switch (args.Trim())
        {
            case "plus":
                Plus();
                break;
            case "toggle":
                _notify.Value = !_notify.Value;
                break;
            case "clear":
                _history.Clear();
                break;
            case "burst":
                for (int i = 0; i < BurstCount; i++) _count.Value++;
                break;
        }
        Report($"state count={_count.Value} notify={_notify.Value} history={_history.Count}");
    }

    /// <summary>ログへ出す。</summary>
    private static void Report(string message) => SEED.Debug.Log($"{LogPrefix} {message}");
}
