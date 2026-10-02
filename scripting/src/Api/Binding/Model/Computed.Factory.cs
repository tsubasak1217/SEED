using System;

namespace SEED.Binding;

// ============================================================
//  Computed.Factory.cs — 導いた観測値（Computed<T>）の作り方（依存 1〜3 個）
//
//      var total  = Computed.From(_price, _count, (p, n) => p * n);
//      var label  = Computed.From(total, t => $"{t:N0} 円");
//  依存は IReadOnlyObservable（Observable・Computed・Bind.Deferred）。依存が 4 個以上なら Computed を重ねる。
// ============================================================

/// <summary>導いた観測値（<see cref="Computed{T}"/>）の作り方。</summary>
public static class Computed
{
    /// <summary>1 個の観測値から導く。</summary>
    /// <typeparam name="TA">依存の値の型。</typeparam>
    /// <typeparam name="T">結果の型。</typeparam>
    /// <param name="a">依存。</param>
    /// <param name="compute">依存の値から結果を計算する。</param>
    /// <returns>導いた観測値。</returns>
    public static Computed<T> From<TA, T>(IReadOnlyObservable<TA> a, Func<TA, T> compute)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(compute);
        return new Computed<T>(() => compute(a.Value), new[] { Connector(a) });
    }

    /// <summary>2 個の観測値から導く。</summary>
    /// <typeparam name="TA">1 つ目の依存の値の型。</typeparam>
    /// <typeparam name="TB">2 つ目の依存の値の型。</typeparam>
    /// <typeparam name="T">結果の型。</typeparam>
    /// <param name="a">1 つ目の依存。</param>
    /// <param name="b">2 つ目の依存。</param>
    /// <param name="compute">依存の値から結果を計算する。</param>
    /// <returns>導いた観測値。</returns>
    public static Computed<T> From<TA, TB, T>(IReadOnlyObservable<TA> a, IReadOnlyObservable<TB> b, Func<TA, TB, T> compute)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(compute);
        return new Computed<T>(() => compute(a.Value, b.Value), new[] { Connector(a), Connector(b) });
    }

    /// <summary>3 個の観測値から導く。</summary>
    /// <typeparam name="TA">1 つ目の依存の値の型。</typeparam>
    /// <typeparam name="TB">2 つ目の依存の値の型。</typeparam>
    /// <typeparam name="TC">3 つ目の依存の値の型。</typeparam>
    /// <typeparam name="T">結果の型。</typeparam>
    /// <param name="a">1 つ目の依存。</param>
    /// <param name="b">2 つ目の依存。</param>
    /// <param name="c">3 つ目の依存。</param>
    /// <param name="compute">依存の値から結果を計算する。</param>
    /// <returns>導いた観測値。</returns>
    public static Computed<T> From<TA, TB, TC, T>(
        IReadOnlyObservable<TA> a, IReadOnlyObservable<TB> b, IReadOnlyObservable<TC> c, Func<TA, TB, TC, T> compute)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(compute);
        return new Computed<T>(() => compute(a.Value, b.Value, c.Value), new[] { Connector(a), Connector(b), Connector(c) });
    }

    /// <summary>依存をつなぐ口を作る（変わったら引数なしの処理を呼ぶ購読）。</summary>
    /// <typeparam name="TSource">依存の値の型。</typeparam>
    /// <param name="source">依存。</param>
    /// <returns>つなぐ口。</returns>
    private static Func<Action, IDisposable> Connector<TSource>(IReadOnlyObservable<TSource> source)
        => changed => source.Subscribe(_ => changed());
}
