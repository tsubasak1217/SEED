namespace SEED.UI;

// ============================================================
//  UiNavigation.Frame.cs — 方向キー・パッドのフォーカスの毎フレームの処理（UiNavigator.Update から。2026-10-03。L3-6）
//
//  1 フレームの順:
//    1. キャンセル（文字入力の間も。戻るの段の Focus の層が入力欄を外す）: Escape は BackDispatcher.PollBackKey（フレームに 1 回だけ
//       配る既存の口。画面の組み立ての部品も同じ口を呼ぶので二重にならない）、それ以外（パッドの East など）は BackDispatcher.Dispatch
//    2. 指・マウスで触った → 枠を隠す（今のフォーカスは保つ）
//    3. 範囲の同期: 今のフォーカスが消えた → 近くの部品へ（枠を出していれば）。入っていた範囲が外れた（ダイアログを閉じた・画面を下ろした）、
//       いちばん前の範囲から移れなくなった（ダイアログを開いた）→ いちばん前の範囲の覚えへ戻る（無く枠を出していれば最初の部品へ）
//    4. 測れるのを待っている「最初の部品へ」を解く
//    5. 文字入力の間（とその次のフレーム）はここまで（方向・決定を読まない）
//    6. 方向（連続移動の時計を通った後の向き）→ 値の増減か移動。決定 → 押す（NavInputPolicy で枠を出すだけの 1 回を挟む）
// ============================================================

public static partial class UiNavigation
{
    /// <summary>「最初の部品へ」を測れるまで待つフレームの上限（60 fps で 0.5 秒）。</summary>
    private const int MaxPendingFrames = 30;
    /// <summary>いちばん前の範囲の部品が測れるまで、ほかの範囲の部品で代えずに待つフレーム（画面を積んだ直後に外の部品を選ばない）。</summary>
    private const int ScopeOnlyGraceFrames = 10;
    /// <summary>待っていない印。</summary>
    private const int NoPending = -1;

    /// <summary>前のフレームにキーボードを入力欄などが受けていたか。</summary>
    private static bool _capturedLastFrame;
    /// <summary>前のフレームのいちばん前の範囲。</summary>
    private static FocusScope? _lastTop;
    /// <summary>今のフォーカスが入っていた範囲（外れたら閉じた＝覚えへ戻る）。</summary>
    private static FocusScope? _currentScope;
    /// <summary>今のフォーカスの最後の矩形（消えたときに近くの部品を選ぶ）。</summary>
    private static Rect _lastRect;

    /// <summary>待っている「最初の部品へ」の根（無効 = 範囲で探す）。</summary>
    private static GameObject _pendingRoot;
    /// <summary>待っているのが範囲で探す方か。</summary>
    private static bool _pendingScopeOnly;
    /// <summary>待っているのが枠を出す方か。</summary>
    private static bool _pendingShowRing;
    /// <summary>待ったフレームの数（NoPending = 待っていない）。</summary>
    private static int _pendingFrames = NoPending;

    /// <summary>
    /// 1 フレームの処理（UiNavigator.Update から）。文字入力の間で方向・決定を読まなかったら true（連続移動の時計を戻す合図）。
    /// </summary>
    /// <param name="input">このフレームの入力。</param>
    /// <param name="options">設定。</param>
    internal static bool Process(in NavFrameInput input, UiNavigationOptions options)
    {
        if (!_enabled) return true;
        // 1. キャンセル（文字入力の間も読む: 戻るの段の Focus の層が入力欄を外す）
        if (options.CancelDispatchesBack && input.Cancel)
        {
            if (input.EscapeDown) BackDispatcher.PollBackKey();
            else BackDispatcher.Dispatch();
        }
        // 2. 指・マウス → 枠を隠す
        if (input.Pointer && (_keyboardMode || _armed))
        {
            _keyboardMode = false;
            _armed = false;
            Redraw.Request();
        }
        // 3・4. 範囲の同期と、待っている「最初の部品へ」
        SyncScope();
        ResolvePending();
        // 5. 文字入力の間（とその次のフレーム）は方向・決定を読まない
        bool captured = IsKeyboardCaptured();
        bool suspended = NavInputPolicy.IsSuspended(captured, _capturedLastFrame);
        _capturedLastFrame = captured;
        if (suspended) return true;
        // 6. 方向・決定
        if (input.Move != FocusDirection.None) Navigate(input.Move, forced: null, options.Wrap);
        if (input.Submit) SubmitFromInput();
        return false;
    }

    /// <summary>今のフォーカスが使えるか（ノードが生きていて、自分と祖先が表示）。</summary>
    private static bool IsAlive(IUiNavigable item) => item.NavNode.IsValid && UiVisibility.IsShownInHierarchy(item.NavNode);

    /// <summary>キーボードを入力欄など（ホイール以外の UiFocus の相手）が受けているか。</summary>
    private static bool IsKeyboardCaptured() => UiFocus.Current is { } focus && focus is not WheelPicker;

    /// <summary>範囲の同期（毎フレーム）。</summary>
    private static void SyncScope()
    {
        var top = UiFocus.TopScope;
        bool topChanged = !ReferenceEquals(top, _lastTop);
        _lastTop = top;
        if (_current is { } current)
        {
            if (!IsAlive(current))
            {
                // 隠れた部品の範囲がもういちばん前でない（画面を積んで覆われた・タブを離れた）: その範囲の覚えに残し、いちばん前の範囲の覚えへ戻る
                // （画面を下ろす・タブへ戻ると、また見えたその部品へ戻れる）
                if (current.NavNode.IsValid && UiFocus.ScopeOf(current.NavNode) is var hiddenScope && !ReferenceEquals(hiddenScope, top))
                {
                    Memory.Remember(hiddenScope, current);
                    SetCurrent(null, showRing: false, reveal: false);
                    RestoreForTop(top);
                    return;
                }
                // 消えた・同じ範囲の中で隠れた（一覧の行を消した・枠を畳んだ）: 枠を出していれば近くの部品へ、そうでなければ外す
                Memory.Forget(current);
                SetCurrent(null, showRing: false, reveal: false);
                if (_keyboardMode) FocusNearest(_lastRect);
                return;
            }
            var scope = UiFocus.ScopeOf(current.NavNode);
            bool closed = _currentScope is { } previous && !UiFocus.IsLiveScope(previous);
            bool allowed = !closed && IsAllowedScope(scope, top);
            if (!allowed)
            {
                // 入っていた範囲が閉じた・いちばん前から移れなくなった: 今の部品は元の範囲の覚えに残し、いちばん前の範囲の覚えへ戻る
                if (!closed) Memory.Remember(scope, current);
                else Memory.Forget(current);
                SetCurrent(null, showRing: false, reveal: false);
                RestoreForTop(top);
                return;
            }
            _currentScope = scope;
            if (current.CanvasRect is var rect && NavigationMath.IsUsable(rect) && rect.width > 0f) _lastRect = rect;
            return;
        }
        // 今のフォーカスが無い: いちばん前の範囲が替わったら、その範囲の覚えへ戻る
        if (topChanged) RestoreForTop(top);
    }

    /// <summary>いちばん前の範囲の覚えへ戻る（無く、枠を出していれば最初の部品を待つ）。</summary>
    private static void RestoreForTop(FocusScope? top)
    {
        var restored = Memory.Recall(top, item => IsAlive(item) && item.IsNavigable && IsAllowedScope(UiFocus.ScopeOf(item.NavNode), top));
        if (restored is not null)
        {
            SetCurrent(restored, showRing: false, reveal: true);
            return;
        }
        if (_keyboardMode) SetPending(default, scopeOnly: true, showRing: true);
    }

    /// <summary>消えた部品の最後の矩形に最も近い部品へ移す（候補が無ければ何もしない）。</summary>
    private static void FocusNearest(Rect reference)
    {
        var candidates = CollectAllowed(exclude: null);
        if (candidates.Count == 0) return;
        int i = NavigationMath.PickNearest(reference, RectsOf(candidates));
        if (i >= 0) SetCurrent(candidates[i], showRing: true, reveal: true);
    }

    /// <summary>
    /// 向きの入力を扱う（forced = スクリプトの Move のように決まりを飛ばして効かせる）。動いた・値を変えた・最初へ移ったら true。
    /// </summary>
    private static bool Navigate(FocusDirection direction, NavInputDecision? forced, bool wrap)
    {
        bool hasCurrent = HasUsableCurrent();
        var decision = !hasCurrent ? NavInputDecision.FocusFirst : forced ?? NavInputPolicy.Decide(true, IsRingVisible, _armed);
        switch (decision)
        {
            case NavInputDecision.FocusFirst:
                ClearPending();
                return TryFocusFirst(default, scopeOnly: false, showRing: true);
            case NavInputDecision.Reveal:
                Reveal();
                return true;
        }
        var current = _current!;
        _keyboardMode = true;
        _armed = false;
        // 値の軸の向き: 部品が受ければフォーカスは移さない
        if (NavInputPolicy.RoutesToAdjust(current.AdjustAxis, direction) && current.OnNavAdjust(FocusDirections.SignOf(direction)))
        {
            Redraw.Request();
            return true;
        }
        var candidates = CollectAllowed(exclude: current);
        int i = NavigationMath.PickNext(current.CanvasRect, RectsOf(candidates), direction, wrap);
        if (i < 0)
        {
            // 端（動かない）。枠は出たまま
            Redraw.Request();
            return false;
        }
        SetCurrent(candidates[i], showRing: true, reveal: true);
        return true;
    }

    /// <summary>決定の入力を扱う（枠を出すだけの 1 回・最初の部品へ・押す）。</summary>
    private static void SubmitFromInput()
    {
        bool hasCurrent = HasUsableCurrent();
        switch (NavInputPolicy.Decide(hasCurrent, IsRingVisible, _armed))
        {
            case NavInputDecision.FocusFirst:
                ClearPending();
                TryFocusFirst(default, scopeOnly: false, showRing: true);
                return;
            case NavInputDecision.Reveal:
                Reveal();
                return;
        }
        _keyboardMode = true;
        _armed = false;
        _current!.OnNavSubmit();
        Redraw.Request();
    }

    /// <summary>枠を出すだけ（今のフォーカスを見える位置へスクロールする）。</summary>
    private static void Reveal()
    {
        _keyboardMode = true;
        _armed = false;
        if (_current is { } current && (_navigator?.Options.ScrollIntoView ?? true))
            NavScroll.Reveal(current.NavNode, RingOutset(), RevealSeconds());
        Redraw.Request();
    }

    /// <summary>今のフォーカスがあり、生きていて、矩形が測れているか。</summary>
    private static bool HasUsableCurrent()
        => _current is { } current && IsAlive(current) && current.CanvasRect is var rect && NavigationMath.IsUsable(rect) && rect.width > 0f;

    /// <summary>指でホイール・入力欄を選んだ（UiFocus の今の相手が変わった）: その部品を今のフォーカスにし、次のキーをすぐ効かせる。</summary>
    private static void OnUiFocusChanged(IFocusable? now)
    {
        if (now is not UiWidget widget || NavAdapters.For(widget) is not { } nav) return;
        if (!ReferenceEquals(nav, _current) && nav.IsNavigable) SetCurrent(nav, showRing: false, reveal: false);
        if (ReferenceEquals(nav, _current)) _armed = true;
    }

    // ── 「最初の部品へ」を待つ ─────────────────────────────

    /// <summary>測れるまで待つ「最初の部品へ」を置く（前の待ちは捨てる）。</summary>
    private static void SetPending(GameObject root, bool scopeOnly, bool showRing)
    {
        _pendingRoot = root;
        _pendingScopeOnly = scopeOnly;
        _pendingShowRing = showRing;
        _pendingFrames = 0;
        Redraw.Request();
    }

    /// <summary>待ちを捨てる。</summary>
    private static void ClearPending()
    {
        _pendingFrames = NoPending;
        _pendingRoot = default;
    }

    /// <summary>待っている「最初の部品へ」を試す（選べたら・上限を超えたら終わる。待つ間は描き続けを頼む＝レイアウトが測られる）。</summary>
    private static void ResolvePending()
    {
        if (_pendingFrames == NoPending) return;
        if (_pendingRoot.IsValid || _pendingScopeOnly)
        {
            if (TryFocusFirst(_pendingRoot, _pendingScopeOnly, _pendingShowRing, allowOtherScopes: _pendingFrames >= ScopeOnlyGraceFrames))
            {
                ClearPending();
                return;
            }
        }
        else
        {
            // 根が消えた（画面がすぐ閉じた）
            ClearPending();
            return;
        }
        if (++_pendingFrames > MaxPendingFrames)
        {
            ClearPending();
            return;
        }
        Redraw.Request();
    }
}
