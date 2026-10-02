using System;
using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ToastHost.cs — トースト（短い知らせ）を下に並べる所（シーンに 1 つ。W2-7。docs/ui_navigation.md §3.5）
//
//  【作り】templates/ui/prefabs/toast_host.actor（ModalHost より後ろ＝木の順で最後に置く。いちばん手前）:
//      ToastHost（Canvas・親に合わせる・CanvasStack〈縦・下寄せ・幅いっぱい・間隔・余白〉・このスクリプト）
//  トーストのプレハブ（toast.actor・SEED.UI.Toast）をここの子として作る（新しいものが下＝画面の下端に近い）。
//  並びと時間は ToastQueue（純粋な計算）: 同時に count.toast_visible 個まで、あふれた分は待たせる。見せる時間は
//  motion.toast_short / motion.toast_long。時間が来たら下へ出ていき、スワイプ（横）で消せる（押さえている間は時間を止める）。
//  見せる時間は実時間で数え、描画を止めている間（render_policy: on_demand）も次に消える時刻に Redraw.RequestAfter で起きる。
//  トーストは戻るを受けない（戻るの段に入らない）。下の余白 = 安全領域の下の余白 + space.l + BottomOffset（タブのバーの高さなど）。
// ============================================================

/// <summary>トーストを見せる長さ。</summary>
public enum ToastLength
{
    /// <summary>短い（motion.toast_short）。</summary>
    Short = 0,
    /// <summary>長い（motion.toast_long）。</summary>
    Long = 1,
}

/// <summary>トーストを下に並べる所。</summary>
public sealed class ToastHost : UiWidget
{
    /// <summary>トーストのプレハブの既定（templates/ui を assets/ui へ取り込んだ置き場）。</summary>
    public const string DefaultToastPrefab = "assets://ui/prefabs/toast.actor";
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] toast:";
    /// <summary>余白が変わったとみなす差（キャンバスの単位）。</summary>
    private const float InsetEpsilon = 0.5f;

    /// <summary>トーストのプレハブ。</summary>
    [SerializeField(Label = "トースト")]
    public string ToastPrefab = DefaultToastPrefab;

    /// <summary>下に足す余白（dp。下のタブのバーの上に出すときはバーの高さ）。</summary>
    [SerializeField(Label = "下の余白")]
    public float BottomOffset;

    /// <summary>今の ToastHost（最後に始まったもの。無ければ null）。</summary>
    public static ToastHost? Current { get; private set; }

    /// <summary>並びと時間。</summary>
    private readonly ToastQueue _queue = new();
    /// <summary>作ったトーストのノード → 項目（トーストのスクリプトが受け取るまで）。</summary>
    private readonly Dictionary<(uint, uint), ToastItem> _pending = new();
    /// <summary>項目の番号 → トーストの部品。</summary>
    private readonly Dictionary<int, Toast> _views = new();
    /// <summary>当てた下の余白（0 = まだ）。</summary>
    private float _bottomInset = -1f;
    /// <summary>前に時間を進めた実時間（Stopwatch の刻み。0 = まだ）。</summary>
    private long _lastTickStamp;

    /// <summary>見せている・出ていく途中のトーストの数。</summary>
    public int ActiveCount => _queue.Active.Count;
    /// <summary>待っているトーストの数。</summary>
    public int PendingCount => _queue.PendingCount;

    /// <summary>トーストを出す（空きが無ければ待たせる）。</summary>
    public ToastItem Show(string message, ToastLength length = ToastLength.Short) => Show(message, null, length);

    /// <summary>先頭のアイコンつきのトーストを出す（2026-10-02。アイコンは図形か画像。null なら文字だけ）。</summary>
    /// <param name="message">文字。</param>
    /// <param name="icon">先頭のアイコン。</param>
    /// <param name="length">見せる長さ。</param>
    public ToastItem Show(string message, UiIcon? icon, ToastLength length = ToastLength.Short)
        => Show(message, icon, Theme.Number(length == ToastLength.Long ? NavTokens.MotionToastLong : NavTokens.MotionToastShort));

    /// <summary>トーストを出す（見せる秒を指定）。</summary>
    public ToastItem Show(string message, float seconds) => Show(message, null, seconds);

    /// <summary>先頭のアイコンつきのトーストを出す（見せる秒を指定。2026-10-02）。</summary>
    /// <param name="message">文字。</param>
    /// <param name="icon">先頭のアイコン（null なら文字だけ）。</param>
    /// <param name="seconds">見せる秒。</param>
    public ToastItem Show(string message, UiIcon? icon, float seconds)
    {
        _queue.MaxVisible = (int)Theme.Number(NavTokens.CountToastVisible, ToastQueue.DefaultMaxVisible);
        var item = _queue.Enqueue(message, seconds, icon);
        Debug.Log($"{LogPrefix} show #{item.Id} \"{message}\" {(item.Phase == ToastPhase.Shown ? "shown" : "pending")}");
        if (item.Phase == ToastPhase.Shown) Spawn(item);
        Redraw.Request();
        return item;
    }

    /// <summary>トーストを消す（スクリプトから。見せていれば下へ出ていく）。</summary>
    public bool Dismiss(ToastItem item)
    {
        if (!_queue.Dismiss(item.Id)) return false;
        if (_views.TryGetValue(item.Id, out var view)) view.AnimateOut(swipeDirection: 0f);
        return true;
    }

    /// <summary>トーストのノードを作る（スクリプトが受け取るまで項目を覚える）。</summary>
    private void Spawn(ToastItem item)
    {
        var node = GameObject.Instantiate(ToastPrefab, gameObject);
        if (!node.IsValid)
        {
            Debug.LogError($"{LogPrefix} トーストのプレハブを作れません: {ToastPrefab}");
            _queue.Remove(item.Id);
            return;
        }
        // スクリプトが動く前のフレームに既定の見た目で出ないよう隠す（トーストが準備したら見せる）
        node.Visible = false;
        _pending[NavNode.Key(node)] = item;
    }

    /// <summary>トーストのスクリプトが項目を受け取る（無ければ null）。</summary>
    internal ToastItem? Claim(Toast toast)
    {
        if (!_pending.Remove(NavNode.Key(toast.Owner), out var item)) return null;
        _views[item.Id] = toast;
        return item;
    }

    /// <summary>押さえる・放す（押さえている間は時間を止める）。</summary>
    internal void SetHeld(ToastItem item, bool held) => _queue.SetHeld(item.Id, held);

    /// <summary>スワイプで消した。</summary>
    internal void OnSwiped(Toast toast, ToastItem item, float direction)
    {
        if (_queue.Dismiss(item.Id, swiped: true)) toast.AnimateOut(direction);
        Debug.Log($"{LogPrefix} swiped #{item.Id}");
    }

    /// <summary>出ていく動きが終わった（トーストが自分を消す）。空いた分を待っているものから出す。</summary>
    internal void OnExitDone(ToastItem item)
    {
        _views.Remove(item.Id);
        foreach (var shown in _queue.Remove(item.Id)) Spawn(shown);
        Redraw.Request();
    }

    // ── 部品の土台 ──────────────────────────────────────────

    /// <inheritdoc />
    protected override void OnWidgetStart() => Current = this;

    /// <inheritdoc />
    protected override void OnWidgetDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        BackDispatcher.PollBackKey();
        ApplyInset();
        // 見せる時間は実時間で数える（render_policy: on_demand で描画を止めている間はフレームが来ず、起きた最初のフレームの
        // DeltaTime は 1/60 秒に切り詰められるため、フレームの時間では 2 秒のトーストがいつまでも消えない。redraw_policy.md §5）
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float elapsed = _lastTickStamp == 0 ? 0f : (float)((now - _lastTickStamp) / (double)System.Diagnostics.Stopwatch.Frequency);
        _lastTickStamp = now;
        var tick = _queue.Tick(elapsed);
        foreach (var expired in tick.Expired)
        {
            Debug.Log($"{LogPrefix} expired #{expired.Id}");
            if (_views.TryGetValue(expired.Id, out var view)) view.AnimateOut(swipeDirection: 0f);
        }
        foreach (var shown in tick.Shown) Spawn(shown);
        ScheduleWake();
    }

    /// <summary>次に消える時刻に起きる（描画を止めている間も時間が進むように）。</summary>
    private void ScheduleWake()
    {
        float next = float.PositiveInfinity;
        foreach (var t in _queue.Active)
            if (t.Phase == ToastPhase.Shown && !t.Held) next = Math.Min(next, t.Remaining);
        if (float.IsFinite(next)) Redraw.RequestAfter(next);
    }

    /// <summary>下の余白（安全領域 + space.l）と帯の底上げを当てる。</summary>
    private void ApplyInset()
    {
        float inset = SafeInsets.BottomUnits();
        if (MathF.Abs(inset - _bottomInset) < InsetEpsilon) return;
        _bottomInset = inset;
        float space = Theme.Number(UiTokens.SpaceL);
        float bottom = space + inset + Math.Max(0f, BottomOffset);
        if (gameObject.GetComponent<CanvasStack>() is { } stack) stack.Padding = new CanvasPadding(space, space, space, bottom);
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        NavNode.SetBias(gameObject, UiLayers.ToastBand(Theme));
        if (gameObject.GetComponent<CanvasStack>() is { } stack) stack.Spacing = Theme.Number(UiTokens.SpaceS);
        _bottomInset = -1f;
    }
}
