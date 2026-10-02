using System;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  DeferredObservable.cs — フレームの区切りでまとめて知らせる観測値（Bind.Deferred の正体）
//
//  元の観測値が 1 フレームに何度変わっても、フレームの区切り（BindingFrame.Tick）で最新の値を 1 回だけ知らせる。
//      Bind.Text(this, label, Bind.Deferred(_score), s => $"{s:N0}");   // 1 フレームに 100 回足しても書くのは 1 回
//    - 区切りまでの間の Value は最後に知らせた値（UI に当たっている値）。
//    - 区切りで知らせるのは「そのフレームに元が知らせてきた」とき（値が元へ戻っていても 1 回知らせる。書くのは最新の値）。
//    - 購読がある間だけ元を購読する（最後の購読が外れたら元の購読も外す）ので、結び付けに直接渡して捨ててよい。
//    - 購読が無い間の Value は元の今の値。
// ============================================================

/// <summary>フレームの区切りでまとめて知らせる観測値。</summary>
/// <typeparam name="T">値の型。</typeparam>
internal sealed class DeferredObservable<T> : IReadOnlyObservable<T>, IFrameTask
{
    /// <summary>警告に出す種類の名前。</summary>
    private const string KindName = "Bind.Deferred";

    /// <summary>元の観測値。</summary>
    private readonly IReadOnlyObservable<T> _source;

    /// <summary>知らせ方。</summary>
    private readonly ValueNotifier<T> _notifier;

    /// <summary>元の購読（購読がある間だけ。無い間は null）。</summary>
    private IDisposable? _connection;

    /// <summary>最後に知らせた値（元をつないでいる間だけ意味がある）。</summary>
    private T _delivered = default!;

    /// <summary>区切りを待っているか（BindingFrame へ二重に積まない）。</summary>
    private bool _queued;

    /// <summary>まとめる観測値を作る。</summary>
    /// <param name="source">元の観測値。</param>
    internal DeferredObservable(IReadOnlyObservable<T> source)
    {
        _source = source;
        _notifier = new ValueNotifier<T>(() => _delivered, EqualityComparer<T>.Default, KindName, Disconnect);
    }

    /// <summary>今の値（元をつないでいる間は最後に知らせた値、無い間は元の今の値）。</summary>
    public T Value => _connection is null ? _source.Value : _delivered;

    /// <summary>今の購読の数（診断・テスト用）。</summary>
    public int SubscriberCount => _notifier.SubscriberCount;

    /// <summary>値の変化を購読する（最初の購読で元をつなぎ、元の今の値を覚える）。</summary>
    /// <param name="handler">新しい値を受け取る処理（区切りで呼ばれる）。</param>
    /// <returns>解除の口。</returns>
    public IDisposable Subscribe(Action<T> handler)
    {
        // null の処理は登録されない（SubscriberList が警告して解除済みの口を返す）ので、そのときは元もつながない
        if (handler is not null && _connection is null)
        {
            _delivered = _source.Value;
            _connection = _source.Subscribe(_ => MarkChanged());
        }
        return _notifier.Subscribe(handler!);
    }

    /// <summary>元が変わった: 区切りを待つ（1 フレームに何度来ても 1 回だけ積む）。</summary>
    private void MarkChanged()
    {
        if (_queued) return;
        _queued = true;
        BindingFrame.Enqueue(this);
    }

    /// <summary>フレームの区切り: 元の最新の値を 1 回だけ知らせる。</summary>
    /// <returns>いつも false（1 回で終わり）。</returns>
    bool IFrameTask.RunFrameTask()
    {
        _queued = false;

        // 区切りまでに購読が全部外れた: 何もしない
        if (_connection is null) return false;
        _delivered = _source.Value;
        _notifier.Publish(force: true);
        return false;
    }

    /// <summary>元の購読を外す（最後の購読が外れたとき）。</summary>
    private void Disconnect()
    {
        var connection = _connection;
        _connection = null;
        connection?.Dispose();
    }
}
