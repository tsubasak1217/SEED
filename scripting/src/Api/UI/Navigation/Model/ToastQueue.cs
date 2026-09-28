using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ToastQueue.cs — トースト（短い知らせ）の並びと時間（W2-7。純粋な計算）
//
//  - 同時に見せるのは最大 MaxVisible 個（count.toast_visible）。それより多い分は待たせ、空いたら古い順に出す
//  - 見せている並びは古い順（ToastHost は下から積むので、いちばん新しいものが画面の下端のいちばん近く）
//  - 見せている時間（motion.toast_short / motion.toast_long）が過ぎたら「出ていく」へ移す（Tick が返す）。
//    出ていく動きが終わったら ToastHost が Remove で消す（空いた分を待っているものから出す）
//  - 指で押さえている（スワイプの途中）間は時間を止める（Hold）。スワイプで消したら Dismiss（すぐ出ていく）
// ============================================================

/// <summary>トーストの段階。</summary>
public enum ToastPhase
{
    /// <summary>待っている（まだ見せていない）。</summary>
    Pending = 0,
    /// <summary>見せている（時間を数えている）。</summary>
    Shown = 1,
    /// <summary>出ていく（動きの途中。終わったら Remove）。</summary>
    Exiting = 2,
}

/// <summary>トースト 1 つ。</summary>
public sealed class ToastItem
{
    /// <summary>通し番号（1 から）。</summary>
    public int Id { get; }
    /// <summary>文字。</summary>
    public string Message { get; }
    /// <summary>見せておく時間（秒）。</summary>
    public float Duration { get; }
    /// <summary>残りの時間（秒）。</summary>
    public float Remaining { get; internal set; }
    /// <summary>段階。</summary>
    public ToastPhase Phase { get; internal set; }
    /// <summary>押さえている（時間を止める）か。</summary>
    public bool Held { get; internal set; }
    /// <summary>スワイプで消した（出ていく向きを部品が決める）。</summary>
    public bool Swiped { get; internal set; }

    internal ToastItem(int id, string message, float duration)
    {
        Id = id;
        Message = message;
        Duration = duration;
        Remaining = duration;
        Phase = ToastPhase.Pending;
    }
}

/// <summary>1 フレームぶん時間を進めた結果。</summary>
/// <param name="Expired">時間が過ぎて出ていくへ移ったもの。</param>
/// <param name="Shown">待っていて見せ始めたもの。</param>
public readonly record struct ToastTick(IReadOnlyList<ToastItem> Expired, IReadOnlyList<ToastItem> Shown);

/// <summary>トーストの並び。</summary>
public sealed class ToastQueue
{
    /// <summary>同時に見せる数の既定（count.toast_visible が無いとき）。</summary>
    public const int DefaultMaxVisible = 3;

    /// <summary>見せている・出ていく途中のもの（古い順）。</summary>
    private readonly List<ToastItem> _active = new();
    /// <summary>待っているもの（古い順）。</summary>
    private readonly Queue<ToastItem> _pending = new();
    /// <summary>次の番号。</summary>
    private int _nextId = 1;
    /// <summary>同時に見せる数。</summary>
    private int _maxVisible = DefaultMaxVisible;

    /// <summary>同時に見せる数（1 以上）。</summary>
    public int MaxVisible
    {
        get => _maxVisible;
        set => _maxVisible = Math.Max(1, value);
    }

    /// <summary>見せている・出ていく途中のもの（古い順）。</summary>
    public IReadOnlyList<ToastItem> Active => _active;
    /// <summary>待っている数。</summary>
    public int PendingCount => _pending.Count;
    /// <summary>見せている数（出ていく途中を含まない）。</summary>
    public int ShownCount => _active.FindAll(t => t.Phase == ToastPhase.Shown).Count;

    /// <summary>
    /// 足す。空きがあればすぐ見せる（Phase = Shown）、無ければ待たせる。
    /// </summary>
    /// <param name="message">文字。</param>
    /// <param name="duration">見せておく時間（秒。0 以下は 0）。</param>
    public ToastItem Enqueue(string message, float duration)
    {
        var item = new ToastItem(_nextId++, message ?? string.Empty, Math.Max(0f, float.IsFinite(duration) ? duration : 0f));
        if (_active.Count < _maxVisible) Show(item);
        else _pending.Enqueue(item);
        return item;
    }

    /// <summary>時間を進める（見せている間だけ数える。押さえている間は止める）。</summary>
    public ToastTick Tick(float dt)
    {
        var expired = new List<ToastItem>();
        float step = float.IsFinite(dt) ? Math.Max(0f, dt) : 0f;
        foreach (var t in _active)
        {
            if (t.Phase != ToastPhase.Shown || t.Held) continue;
            t.Remaining -= step;
            if (t.Remaining <= 0f)
            {
                t.Remaining = 0f;
                t.Phase = ToastPhase.Exiting;
                expired.Add(t);
            }
        }
        return new ToastTick(expired, Promote());
    }

    /// <summary>押さえる・放す（押さえている間は時間を止める）。</summary>
    public void SetHeld(int id, bool held)
    {
        var t = _active.Find(x => x.Id == id);
        if (t is not null) t.Held = held;
    }

    /// <summary>
    /// 消す（スワイプ・スクリプト）。見せていれば出ていくへ移す（true）。待っているものは並びから外す（true）。
    /// </summary>
    public bool Dismiss(int id, bool swiped = false)
    {
        var t = _active.Find(x => x.Id == id);
        if (t is not null)
        {
            if (t.Phase == ToastPhase.Exiting) return false;
            t.Phase = ToastPhase.Exiting;
            t.Swiped = swiped;
            return true;
        }
        int before = _pending.Count;
        var keep = new Queue<ToastItem>();
        foreach (var p in _pending)
            if (p.Id != id) keep.Enqueue(p);
        _pending.Clear();
        foreach (var p in keep) _pending.Enqueue(p);
        return _pending.Count != before;
    }

    /// <summary>出ていく動きが終わったものを消し、空いた分を待っているものから出す（出し始めたものを返す）。</summary>
    public IReadOnlyList<ToastItem> Remove(int id)
    {
        _active.RemoveAll(x => x.Id == id);
        return Promote();
    }

    /// <summary>空きの分だけ待っているものを見せ始める。</summary>
    private List<ToastItem> Promote()
    {
        var shown = new List<ToastItem>();
        while (_pending.Count > 0 && _active.Count < _maxVisible)
        {
            var next = _pending.Dequeue();
            Show(next);
            shown.Add(next);
        }
        return shown;
    }

    /// <summary>見せ始める。</summary>
    private void Show(ToastItem item)
    {
        item.Phase = ToastPhase.Shown;
        _active.Add(item);
    }
}
