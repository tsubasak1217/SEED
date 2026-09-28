using System;
using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  TabHost.cs — 下のタブで画面を切り替える（タブごとに画面のスタックを持つ。W2-7。docs/ui_navigation.md §4）
//
//  【作り】templates/ui/prefabs/tab_host.actor:
//      TabHost（Canvas・親に合わせる・CanvasStack〈縦・幅いっぱい〉・このスクリプト）
//      ├─ Pages（Canvas・伸ばす）
//      │   ├─ Tab0（screen_stack.actor の作り。SEED.UI.ScreenStack〈根の画面・1 段のレイヤー 1000〉）
//      │   └─ Tab1・Tab2 …
//      └─ TabBar（SEED.UI.TabBar）└─ Item0..（SEED.UI.TabItem。Index がタブの番号）
//  - 選んでいないタブのスタックは隠して保つ（描かない・入力を受けない・状態はそのまま。タブを戻ったとき前の位置）
//  - 選んでいるタブをもう一度押すと根へ戻り（PopToRoot）、TabReselected を出す（根にいる画面は先頭へスクロールする合図に使う）
//  - 戻る: 選んでいるタブのスタック（内側のナビゲーターなので先に尋ねられる）→ 最初のタブ以外なら最初のタブへ（BackToFirstTab）
//    → 受けない（アプリを背面へ）。決め方は TabModel（純粋な計算）
//  - ResetOnLeave: タブを離れたらそのタブのスタックを根へ戻す（既定 false = 状態を保つ）
// ============================================================

/// <summary>下のタブで画面を切り替える。</summary>
public sealed class TabHost : UiWidget, INavigator
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] tab:";

    /// <summary>タブのスタックを置く子の名前。</summary>
    [SerializeField(Label = "ページの子")]
    public string PagesChild = "Pages";

    /// <summary>バーの子の名前。</summary>
    [SerializeField(Label = "バーの子")]
    public string TabBarChild = "TabBar";

    /// <summary>タブのスタックのノードの名前（Pages の子。並びがタブの番号）。</summary>
    [SerializeField(Label = "タブ")]
    public string[] TabNames = { "Tab0", "Tab1", "Tab2" };

    /// <summary>最初に選ぶタブ。</summary>
    [SerializeField(Label = "最初のタブ")]
    public int InitialTab;

    /// <summary>最初のタブ以外で戻るを押したら最初のタブへ戻るか。</summary>
    [SerializeField(Label = "戻るで最初のタブへ")]
    public bool BackToFirstTab = true;

    /// <summary>タブを離れたらそのタブのスタックを根へ戻すか（既定 false = 前の位置のまま）。</summary>
    [SerializeField(Label = "離れたら根へ")]
    public bool ResetOnLeave;

    /// <summary>選択が変わった（新しいタブの番号）。</summary>
    public event Action<TabHost, int>? TabChanged;

    /// <summary>選んでいるタブをもう一度押した（根へ戻した後。先頭へスクロールの合図）。</summary>
    public event Action<TabHost, int>? TabReselected;

    /// <summary>選択の決め方。</summary>
    private readonly TabModel _model = new(0);
    /// <summary>タブのノード（並びがタブの番号）。</summary>
    private readonly List<GameObject> _pages = new();
    /// <summary>タブのスタック（見つかるまで null）。</summary>
    private readonly List<ScreenStack?> _stacks = new();
    /// <summary>つないだバー。</summary>
    private TabBar? _bar;
    /// <summary>引き直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>最初の選択を当てたか。</summary>
    private bool _initialApplied;

    /// <summary>タブの数。</summary>
    public int Count => _pages.Count;
    /// <summary>選んでいるタブ（無ければ −1）。</summary>
    public int SelectedIndex => _model.Selected;
    /// <summary>選んでいるタブのスタック（無ければ null）。</summary>
    public ScreenStack? CurrentStack => StackAt(_model.Selected);

    /// <summary>タブのスタック（範囲の外・見つかっていなければ null）。</summary>
    public ScreenStack? StackAt(int index) => index >= 0 && index < _stacks.Count ? _stacks[index] : null;

    /// <summary>
    /// タブを選ぶ（バーのタップと同じ。選んでいるタブならもう一度押した扱い＝根へ戻す）。
    /// </summary>
    public void Select(int index)
    {
        var result = _model.Select(index);
        if (result.Reselected)
        {
            var stack = CurrentStack;
            bool popped = stack is not null && stack.PopToRoot();
            Debug.Log($"{LogPrefix} reselect {index} pop_to_root={popped}");
            TabReselected?.Invoke(this, index);
            return;
        }
        if (!result.Changed) return;
        Debug.Log($"{LogPrefix} select {result.Previous} -> {index}");
        ShowOnly(index, result.Previous);
        TabChanged?.Invoke(this, index);
    }

    /// <summary>選んだタブだけを見せ、上の画面へ知らせる（離れたタブは状態を保つか根へ戻す）。</summary>
    private void ShowOnly(int index, int previous)
    {
        for (int i = 0; i < _pages.Count; i++) NavNode.SetVisible(_pages[i], i == index);
        if (StackAt(previous) is { } left)
        {
            left.NotifyShownByHost(false);
            if (ResetOnLeave) left.PopToRoot();
        }
        StackAt(index)?.NotifyShownByHost(true);
        if (_bar is not null) _bar.SelectedIndex = index;
        Redraw.Request();
    }

    // ── 部品の土台 ──────────────────────────────────────────

    /// <inheritdoc />
    GameObject INavigator.NavigatorNode => Owner;

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        NavigatorRegistry.Register(this);
        var pages = gameObject.FindChild(PagesChild);
        var parent = pages.IsValid ? pages : gameObject;
        foreach (var name in TabNames)
        {
            _pages.Add(parent.FindChild(name));
            _stacks.Add(null);
        }
        _model.Resize(_pages.Count, InitialTab);
    }

    /// <inheritdoc />
    protected override void OnWidgetDestroy() => NavigatorRegistry.Unregister(this);

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        BackDispatcher.PollBackKey();
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        Bind();
    }

    /// <summary>タブのスタックとバーを引く（部品の OnStart の順は決まっていないので、登録簿が変わるたびに）。</summary>
    private void Bind()
    {
        for (int i = 0; i < _pages.Count; i++)
            _stacks[i] ??= Of<ScreenStack>(_pages[i]);
        if (_bar is null && Of<TabBar>(gameObject.FindChild(TabBarChild)) is { } bar)
        {
            _bar = bar;
            bar.ItemTapped += (_, index) => Select(index);
            bar.SelectedIndex = _model.Selected;
        }
        if (!_initialApplied && _pages.Count > 0)
        {
            _initialApplied = true;
            for (int i = 0; i < _pages.Count; i++) NavNode.SetVisible(_pages[i], i == _model.Selected);
        }
    }

    /// <inheritdoc />
    bool INavigator.HandleBack()
    {
        // 選んでいるタブのスタックは内側のナビゲーターとして先に尋ねられている（下ろせるならそこで受けている）
        switch (_model.DecideBack(CurrentStack?.Depth ?? 1, BackToFirstTab))
        {
            case TabBackAction.PopStack:
                return CurrentStack?.Pop() ?? false;
            case TabBackAction.SelectFirst:
                Select(TabModel.FirstTab);
                return true;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    protected override void ApplyLook() { }
}
