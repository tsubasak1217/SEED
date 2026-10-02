using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  UiNavigator.cs — 方向キー・パッドで UI 部品のフォーカスを移して押す仕組みの入口（シーンに 1 つ。2026-10-03。L3-6）
//
//  【置き方】アプリの根（画面いっぱいの Canvas。見本の App・ui_navigation.scene の根）か、その直下の画面いっぱいの Canvas に付ける。
//  フォーカスの枠（focus_ring.actor）はこのアクターの子として 1 つだけ作る（部品の木には入れない。いちばん手前のレイヤー）。
//  入力を InputMap で割り当てるなら、同じアクターに InputMap（templates/input/uiNavigation.inputmap）を付ける。
//  【毎フレーム】入力を読み（NavInputReader）→ UiNavigation.Process（移動・値の増減・決定・キャンセル・範囲の同期）→ 枠を重ねる（FocusRing）。
//  スクリプトからの口は UiNavigation（Focus・Current・Enabled・FocusFirstIn・Submit・Cancel・Move）。正典は docs/ui_navigation.md §7.2。
//  2 つ置いたら 2 つ目は動かない（警告）。消えたら今のフォーカスと覚えを捨て、ホイールは以前どおり自分で矢印キーを読む。
// ============================================================

/// <summary>方向キー・パッドの移動の入口（シーンに 1 つ）。</summary>
public sealed class UiNavigator : SEEDScript
{
    /// <summary>設定（入力の出所・アクション名・端で回るか・キャンセル・スクロール・枠のプレハブ）。</summary>
    [SerializeField(Label = "設定")]
    public UiNavigationOptions Options = new();

    /// <summary>入力の読み手。</summary>
    private readonly NavInputReader _reader = new();
    /// <summary>フォーカスの枠。</summary>
    private readonly FocusRing _ring = new();
    /// <summary>シーンの UiNavigator として動いているか（2 つ目は false）。</summary>
    private bool _active;

    /// <inheritdoc />
    public override void OnStart()
    {
        Options ??= new UiNavigationOptions();
        _active = UiNavigation.Attach(this);
    }

    /// <inheritdoc />
    public override void OnDestroy()
    {
        _ring.Destroy();
        if (_active) UiNavigation.Detach(this);
        _active = false;
    }

    /// <inheritdoc />
    public override void Update(ref NativeFrameContext ctx)
    {
        if (!_active) return;
        // 連続移動の時計は時間の倍率（一時停止の 0 など）に左右されない秒で進める
        var input = _reader.Read(gameObject, Options, Time.UnscaledDeltaTime);
        if (UiNavigation.Process(input, Options)) _reader.ResetRepeat();
        _ring.Update(gameObject, Options.FocusRingPrefab, UiNavigation.Current, UiNavigation.IsRingVisible, UiTheme.Current);
    }
}
