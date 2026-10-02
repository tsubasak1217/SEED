using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  UiNavigation.cs — 方向キー・パッドのフォーカス（どの部品を選んでいるか）の管理と、スクリプトからの口（2026-10-03。L3-6）
//
//  正典は docs/ui_navigation.md §7.2。手本は Unity の EventSystem（Navigation = Automatic）と Flutter の FocusTraversal。
//  【しくみ】シーンの UiNavigator（App の根に 1 つ）が毎フレーム入力を読み（NavInputReader）、ここの Process へ渡す:
//    - 方向: 今の部品の値の軸の向きなら値の増減（スライダ・ホイール…）、それ以外は向きの最寄りの部品へ移る（NavigationMath）。
//      候補はいちばん前のフォーカスの範囲（とそれを中に含む範囲）の部品だけ（NavScopeFilter。ダイアログの下へ移らない）。
//      移った部品がスクロールの中なら見える位置までスクロールする（NavScroll）
//    - 決定: 今の部品を押す（IUiNavigable.OnNavSubmit）。キャンセル: 戻るの段（BackDispatcher）へ
//    - 枠: 方向キー・決定で出し、指・マウスで触ったら隠す（NavInputPolicy。UiNavigator が FocusRing を重ねる）
//    - 範囲が替わったら（ダイアログを開いた・閉じた）範囲ごとに覚えた部品へ戻る（NavScopeMemory）。覚えが無く枠を出していれば最初の部品へ
//  既存の UiFocus（文字入力・ホイールのキーボードの相手と戻るの層）は壊さずその上に乗る: 入力欄が文字を受けている間は読まず、
//  指でホイール・入力欄を選んだら（UiFocus.Changed）そこを今のフォーカスにする（枠は出さない）。
//  UiNavigator が無い・Enabled = false なら何もしない（ホイール・時刻ホイールは以前どおり自分で矢印キーを読む）。
// ============================================================

/// <summary>方向キー・パッドのフォーカスの管理（シーンに 1 つ。静的）。</summary>
public static partial class UiNavigation
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] nav:";

    /// <summary>今のフォーカス（無ければ null）。</summary>
    private static IUiNavigable? _current;
    /// <summary>枠を出す状態か（方向キー・決定で true、指・マウスで false）。</summary>
    private static bool _keyboardMode;
    /// <summary>枠を隠していても次の入力をすぐ効かせるか（指でホイール・入力欄を選んだ直後）。</summary>
    private static bool _armed;
    /// <summary>動かすか（false なら入力を読まず枠も隠す）。</summary>
    private static bool _enabled = true;
    /// <summary>動いている UiNavigator（無ければ null＝何もしない）。</summary>
    private static UiNavigator? _navigator;
    /// <summary>範囲ごとに最後にフォーカスした部品。</summary>
    private static readonly NavScopeMemory<IUiNavigable> Memory = new();
    /// <summary>スクリプトが足した部品。</summary>
    private static readonly List<IUiNavigable> Custom = new();

    /// <summary>今のフォーカス（方向キーで選んでいる部品。無ければ null）。</summary>
    public static IUiNavigable? Current => _current;

    /// <summary>今のフォーカスのノード（無ければ無効な GameObject）。</summary>
    public static GameObject CurrentNode => _current?.NavNode ?? default;

    /// <summary>
    /// 動かすか（既定 true）。false にすると方向キー・決定・キャンセルを読まず、枠を隠す（ゲームの操作中など）。
    /// false の間はホイール・時刻ホイールが以前どおり自分で矢印キーを読む。
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Redraw.Request();
        }
    }

    /// <summary>UiNavigator がシーンで動いているか（無ければこの口は入力を読まない。スクリプトからの Focus などは効く）。</summary>
    public static bool HasNavigator => _navigator is not null;

    /// <summary>枠を見せているか（方向キーで操作している間で、今のフォーカスがある）。</summary>
    public static bool IsRingVisible => _enabled && _keyboardMode && _current is not null;

    /// <summary>今のフォーカスが変わった（新しい相手。無ければ null）。</summary>
    public static event Action<IUiNavigable?>? CurrentChanged;

    /// <summary>
    /// UiNavigator が方向キーを読んでいるか（ホイール・時刻ホイールが自分の矢印キーの読み取りを止める合図。二重に動かない）。
    /// </summary>
    internal static bool DrivesKeys => _navigator is not null && _enabled;

    // ── スクリプトからの口 ──────────────────────────────────

    /// <summary>
    /// ノードの部品へフォーカスする（ノードそのもの、無ければいちばん近い祖先の部品）。移れない部品なら何もしない（false）。
    /// </summary>
    /// <param name="node">部品のノード（子でもよい）。</param>
    /// <param name="showRing">枠を出すか（既定 true）。</param>
    public static bool Focus(GameObject node, bool showRing = true)
    {
        var item = FindNavigable(node);
        return item is not null && Focus(item, showRing);
    }

    /// <summary>部品へフォーカスする（移れない部品なら何もしない＝false）。</summary>
    /// <param name="item">部品。</param>
    /// <param name="showRing">枠を出すか（既定 true）。</param>
    public static bool Focus(IUiNavigable item, bool showRing = true)
    {
        if (item is null || !item.IsNavigable) return false;
        ClearPending();
        SetCurrent(item, showRing, reveal: true);
        return true;
    }

    /// <summary>
    /// ノードの下（ノード自身を含む）で、読む順の最初の部品へフォーカスする（画面を開いたら最初の部品へ）。
    /// まだ測れていない（作った直後の）部品しか無ければ、測れるまで数フレーム待ってから選ぶ（その間は false）。
    /// </summary>
    /// <param name="root">画面・ダイアログの根など。</param>
    /// <param name="showRing">枠を出すか（既定 true）。</param>
    public static bool FocusFirstIn(GameObject root, bool showRing = true)
    {
        if (!root.IsValid) return false;
        if (TryFocusFirst(root, scopeOnly: false, showRing)) return true;
        SetPending(root, scopeOnly: false, showRing);
        return false;
    }

    /// <summary>
    /// フォーカスの範囲（null = 今いちばん前の範囲から移れる部品すべて）で、読む順の最初の部品へフォーカスする。
    /// 測れるまで待つのは <see cref="FocusFirstIn(GameObject, bool)"/> と同じ。
    /// </summary>
    /// <param name="scope">範囲（画面・ダイアログ。null = いちばん前）。</param>
    /// <param name="showRing">枠を出すか（既定 true）。</param>
    public static bool FocusFirstIn(FocusScope? scope, bool showRing = true)
    {
        if (scope is not null) return FocusFirstIn(scope.Owner, showRing);
        if (TryFocusFirst(default, scopeOnly: true, showRing)) return true;
        SetPending(default, scopeOnly: true, showRing);
        return false;
    }

    /// <summary>
    /// 向きへ移る（スクリプトから。値の軸の向きなら値を増減する）。今のフォーカスが無ければ最初の部品へ。枠を出す。
    /// 動いた・値を変えた・最初の部品へ移ったら true。
    /// </summary>
    /// <param name="direction">向き。</param>
    public static bool Move(FocusDirection direction)
    {
        if (direction == FocusDirection.None) return false;
        return Navigate(direction, NavInputDecision.Act, wrap: _navigator?.Options.Wrap ?? false);
    }

    /// <summary>今のフォーカスを押す（スクリプトから。決定のキーと同じ。フォーカスが無ければ false）。</summary>
    public static bool Submit()
    {
        if (_current is null || !IsAlive(_current)) return false;
        _current.OnNavSubmit();
        Redraw.Request();
        return true;
    }

    /// <summary>キャンセル（戻るの段へ配る。BackDispatcher.Dispatch と同じ）。</summary>
    public static BackDispatchResult Cancel() => BackDispatcher.Dispatch();

    /// <summary>フォーカスを外す（枠も消える）。</summary>
    public static void Clear()
    {
        ClearPending();
        SetCurrent(null, showRing: false, reveal: false);
        _keyboardMode = false;
    }

    /// <summary>
    /// スクリプト独自の部品を候補に足す（戻り値を Dispose すると外れる。スクリプトの OnDestroy で外すこと）。
    /// </summary>
    /// <param name="item">部品。</param>
    public static IDisposable Register(IUiNavigable item)
    {
        if (item is not null && !Custom.Contains(item)) Custom.Add(item);
        return new Registration(item);
    }

    /// <summary>
    /// スクリプト独自の部品の型（UiWidget の派生）のアダプタの作り方を足す（同じ型は置き換える）。組み込みの部品より先に見る。
    /// </summary>
    /// <typeparam name="T">部品の型。</typeparam>
    /// <param name="factory">部品 → アダプタ（null を返すと移り先にしない）。</param>
    public static void RegisterAdapter<T>(Func<T, IUiNavigable?> factory) where T : UiWidget => NavAdapters.Register(factory);

    /// <summary>今いちばん前の範囲から移れる部品（診断・テスト用。呼ぶたびに集める）。</summary>
    public static IReadOnlyList<IUiNavigable> Candidates() => CollectAllowed(exclude: null);

    /// <summary>状態の 1 行（診断・ログ）。</summary>
    public static string Describe()
        => $"current={(_current?.ToString() ?? "-")} ring={IsRingVisible} armed={_armed} enabled={_enabled} navigator={(_navigator is not null)} scope={UiFocus.TopScope?.Name ?? "root"}";

    // ── UiNavigator・部品から ─────────────────────────────

    /// <summary>UiNavigator が動き始めた（2 つ目以降は警告して無視）。</summary>
    internal static bool Attach(UiNavigator navigator)
    {
        if (_navigator is not null && !ReferenceEquals(_navigator, navigator))
        {
            Debug.LogWarning($"{LogPrefix} UiNavigator はシーンに 1 つだけ置いてください（2 つ目は動きません）");
            return false;
        }
        _navigator = navigator;
        UiFocus.Changed -= OnUiFocusChanged;
        UiFocus.Changed += OnUiFocusChanged;
        return true;
    }

    /// <summary>UiNavigator が消えた（今のフォーカスと覚えを捨てる）。</summary>
    internal static void Detach(UiNavigator navigator)
    {
        if (!ReferenceEquals(_navigator, navigator)) return;
        _navigator = null;
        UiFocus.Changed -= OnUiFocusChanged;
        ResetState();
    }

    /// <summary>
    /// 画面が上になった（UiScreen の AutoFocusFirst。ScreenStack の OnScreenShown の直後）: 画面の最初の部品へ（測れるまで待つ）。
    /// </summary>
    internal static void RequestFocusFirst(GameObject root)
    {
        if (!root.IsValid) return;
        if (!TryFocusFirst(root, scopeOnly: false, showRing: true)) SetPending(root, scopeOnly: false, showRing: true);
    }

    /// <summary>スクリプトを読み直す前（ScriptBridge から）: 状態・足した部品と作り方を捨てる（旧アセンブリを握らない）。</summary>
    internal static void ResetForReload()
    {
        UiFocus.Changed -= OnUiFocusChanged;
        _navigator = null;
        _enabled = true;
        Custom.Clear();
        NavAdapters.Reset();
        ListViewNavSource.Reset();
        ResetState();
        CurrentChanged = null;
    }

    // ── 内側 ──────────────────────────────────────────────

    /// <summary>今のフォーカスを替える（変われば外れた相手・新しい相手へ知らせる）。覚え・枠・スクロールも扱う。</summary>
    private static void SetCurrent(IUiNavigable? item, bool showRing, bool reveal)
    {
        if (!ReferenceEquals(item, _current))
        {
            var previous = _current;
            _current = item;
            previous?.OnNavFocus(false);
            item?.OnNavFocus(true);
            CurrentChanged?.Invoke(item);
        }
        if (showRing && item is not null) _keyboardMode = true;
        _currentScope = item is not null ? UiFocus.ScopeOf(item.NavNode) : null;
        if (item is not null)
        {
            Memory.Remember(_currentScope, item);
            if (reveal && (_navigator?.Options.ScrollIntoView ?? true)) NavScroll.Reveal(item.NavNode, RingOutset(), RevealSeconds());
        }
        Redraw.Request();
    }

    /// <summary>状態を捨てる（UiNavigator が消えた・読み直し）。</summary>
    private static void ResetState()
    {
        _current = null;
        _keyboardMode = false;
        _armed = false;
        _capturedLastFrame = false;
        _lastTop = null;
        _currentScope = null;
        _lastRect = Rect.Zero;
        Memory.Clear();
        ClearPending();
    }

    /// <summary>ノードかその祖先の部品（候補にいる部品のうち、ノードがいちばん近いもの）。</summary>
    private static IUiNavigable? FindNavigable(GameObject node)
    {
        if (!node.IsValid) return null;
        var all = CollectRaw();
        var current = node;
        for (int depth = 0; depth < UiVisibility.MaxDepth && current.IsValid; depth++)
        {
            var key = NavNode.Key(current);
            foreach (var item in all)
                if (item.NavNode.IsValid && NavNode.Key(item.NavNode) == key) return item;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>枠の外への広がり（間 + 太さ。キャンバスの単位。スクロールで見せるときの余白）。</summary>
    private static float RingOutset()
    {
        var theme = UiTheme.Current;
        return theme.Number(NavTokens.SizeFocusRingGap) + theme.Number(NavTokens.SizeFocusRingWidth);
    }

    /// <summary>スクロールで見せる時間（motion.short。連続移動の間隔より長すぎないように）。</summary>
    private static float RevealSeconds() => UiTheme.Current.Number(UiTokens.MotionShort);

    /// <summary>Register の戻り値（Dispose で外す）。</summary>
    private sealed class Registration : IDisposable
    {
        /// <summary>外す部品。</summary>
        private IUiNavigable? _item;

        public Registration(IUiNavigable? item) { _item = item; }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_item is null) return;
            Custom.Remove(_item);
            Memory.Forget(_item);
            if (ReferenceEquals(_current, _item)) SetCurrent(null, showRing: false, reveal: false);
            _item = null;
        }
    }
}
