using System;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  Computed.cs — 導いた観測値（1〜3 個の観測値から計算する。作り方は Computed.From）
//
//  【約束】
//    - 依存（元の観測値）が変わったら計算し直し、結果が前と違えば購読へ知らせる（等しければ知らせない）。
//    - 購読がある間だけ依存を購読する（最後の購読が外れたら依存の購読も外す）。なので結び付けに直接渡して捨ててよい:
//        Bind.Visible(this, badge, Computed.From(_count, n => n > 0));   // 結び付けが外れれば依存も外れる（漏れない）
//    - 購読が無い間の Value は呼ばれるたびに計算する（覚えておかない）。購読がある間は最後に計算した値を返す。
//    - 計算で例外が起きたら（依存の変化から）エラーログへ出し、前の値のままにする。
//    - 依存がひし形（A → B、A と B → C）だと、C は A の変化のたびに途中の値を 1 回知らせることがある（最後は正しい値になる）。
//  正典は docs/ui_binding.md §3.2。
// ============================================================

/// <summary>導いた観測値（依存が変わったら計算し直して知らせる）。<see cref="Computed.From{TA, T}"/> で作る。</summary>
/// <typeparam name="T">値の型。</typeparam>
public sealed class Computed<T> : IReadOnlyObservable<T>
{
    /// <summary>警告に出す種類の名前。</summary>
    private const string KindName = "Computed";

    /// <summary>値を計算する。</summary>
    private readonly Func<T> _compute;

    /// <summary>依存をつなぐ口（変わったら呼ぶ処理を受けて、依存の購読を返す）。</summary>
    private readonly Func<Action, IDisposable>[] _connectors;

    /// <summary>値の比べ方（前と等しければ知らせない）。</summary>
    private readonly IEqualityComparer<T> _comparer;

    /// <summary>知らせ方。</summary>
    private readonly ValueNotifier<T> _notifier;

    /// <summary>依存の購読（購読がある間だけ。無い間は null）。</summary>
    private IDisposable[]? _connections;

    /// <summary>最後に計算した値（依存をつないでいる間だけ意味がある）。</summary>
    private T _cached = default!;

    /// <summary>導いた観測値を作る（Computed.From から）。</summary>
    /// <param name="compute">値を計算する処理（依存の Value を読む）。</param>
    /// <param name="connectors">依存をつなぐ口。</param>
    internal Computed(Func<T> compute, Func<Action, IDisposable>[] connectors)
    {
        _compute = compute;
        _connectors = connectors;
        _comparer = EqualityComparer<T>.Default;
        _notifier = new ValueNotifier<T>(() => _cached, _comparer, KindName, Disconnect);
    }

    /// <summary>今の値（購読がある間は最後に計算した値、無い間は呼ばれるたびに計算した値）。</summary>
    public T Value => _connections is null ? _compute() : _cached;

    /// <summary>今の購読の数（診断・テスト用）。</summary>
    public int SubscriberCount => _notifier.SubscriberCount;

    /// <summary>依存をつないでいるか（購読があるか）。</summary>
    public bool IsConnected => _connections is not null;

    /// <summary>
    /// 値の変化を購読する（最初の購読で依存をつなぎ、今の値を計算して覚える）。戻り値を Dispose すると解除する。
    /// </summary>
    /// <param name="handler">新しい値を受け取る処理。</param>
    /// <returns>解除の口。</returns>
    public IDisposable Subscribe(Action<T> handler)
    {
        // null の処理は登録されない（SubscriberList が警告して解除済みの口を返す）ので、そのときは依存もつながない
        if (handler is not null && _connections is null) Connect();
        return _notifier.Subscribe(handler!);
    }

    /// <summary>依存をつなぐ（今の値を計算して覚えてから、各依存を購読する）。</summary>
    private void Connect()
    {
        // 先に計算する（例外なら依存をつながずに呼び出し元へ返す＝半端につながない）
        _cached = _compute();
        var connections = new IDisposable[_connectors.Length];
        for (int i = 0; i < _connectors.Length; i++) connections[i] = _connectors[i](OnDependencyChanged);
        _connections = connections;
    }

    /// <summary>依存の購読を外す（最後の購読が外れたとき）。</summary>
    private void Disconnect()
    {
        var connections = _connections;
        _connections = null;
        if (connections is null) return;
        foreach (var connection in connections) connection.Dispose();
    }

    /// <summary>依存が変わった: 計算し直し、前と違えば知らせる。</summary>
    private void OnDependencyChanged()
    {
        // 依存を外した後に届いた知らせ（配っている途中に外れた）は無視する
        if (_connections is null) return;

        T next;
        try
        {
            next = _compute();
        }
        catch (Exception ex)
        {
            // 計算の失敗で依存元の知らせを止めない（前の値のまま）
            BindingLog.Error($"Computed の計算で例外が起きました（前の値のままにします）: {ex}");
            return;
        }

        // 等しければ知らせない
        if (_comparer.Equals(next, _cached)) return;
        _cached = next;
        _notifier.Publish();
    }

    /// <summary>今の値の文字列（ログ用）。</summary>
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
