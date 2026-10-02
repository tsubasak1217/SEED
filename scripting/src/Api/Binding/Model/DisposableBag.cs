using System;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  DisposableBag.cs — 購読・結び付けをまとめて預かる袋（UniRx の CompositeDisposable の最小部分）
//
//  スクリプト（SEEDScript）は、Bind.*(owner: this, …)・Subscribe(owner: this, …)・AddTo(this) で預かった結び付けを
//  この袋に入れ、破棄のときに全部 Dispose する（this.On の自動解除と同じ時）。画面より短い寿命のまとまり（開いている間だけの
//  パネルなど）を作るときは、利用者がこの袋を直接使ってもよい。
//    - Dispose した後に Add した物は、その場で Dispose する（破棄の後で結び付けを作っても漏れない）。
//    - 当てる先が消えて自分で外れた結び付けは、袋が大きくなったときに捨てる（長生きの持ち主で袋が膨らみ続けない）。
//    - 1 つの Dispose の例外はエラーログへ出して、残りは続ける。
// ============================================================

/// <summary>購読・結び付けをまとめて預かる袋。</summary>
public sealed class DisposableBag : IDisposable
{
    /// <summary>外れた結び付けを捨てる見直しを始める数。</summary>
    private const int PruneThreshold = 32;

    /// <summary>見直しの後、次の見直しまでに許す伸び（残った数の何倍まで）。</summary>
    private const int PruneGrowthFactor = 2;

    /// <summary>預かった物（最初に預かるまで作らない）。</summary>
    private List<IDisposable>? _items;

    /// <summary>次に見直す数。</summary>
    private int _pruneAt = PruneThreshold;

    /// <summary>Dispose したか。</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>預かっている数（自分で外れた結び付けは、見直しまで数に残る）。</summary>
    public int Count => _items?.Count ?? 0;

    /// <summary>預ける（Dispose した後なら、その場で Dispose する）。</summary>
    /// <param name="item">預ける物（null は無視）。</param>
    public void Add(IDisposable? item)
    {
        if (item is null) return;
        if (IsDisposed)
        {
            DisposeSafely(item);
            return;
        }
        (_items ??= new List<IDisposable>()).Add(item);
        if (_items.Count >= _pruneAt) Prune(_items);
    }

    /// <summary>預かった物を全部 Dispose する（預かった順。二重の Dispose は無害）。</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        var items = _items;
        _items = null;
        if (items is null) return;
        foreach (var item in items) DisposeSafely(item);
    }

    /// <summary>自分で外れた結び付け・購読を捨てる（次の見直しの数を伸ばす）。</summary>
    /// <param name="items">預かった物。</param>
    private void Prune(List<IDisposable> items)
    {
        items.RemoveAll(item => item is IBindingHandle { IsDisposed: true });
        _pruneAt = Math.Max(PruneThreshold, items.Count * PruneGrowthFactor);
    }

    /// <summary>Dispose する（例外はログへ出して続ける）。</summary>
    /// <param name="item">Dispose する物。</param>
    private static void DisposeSafely(IDisposable item)
    {
        try
        {
            item.Dispose();
        }
        catch (Exception ex)
        {
            BindingLog.Error($"預かった結び付けを外す処理で例外が起きました: {ex}");
        }
    }
}
