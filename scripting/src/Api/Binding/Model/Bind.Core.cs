using System;

namespace SEED.Binding;

// ============================================================
//  Bind.Core.cs — 結び付けの静的ヘルパーのうち、エンジンに依らない芯（partial。テストもこの部分を使う）
//
//  UI 部品向けの Bind.Text・Bind.Visible・Bind.Toggle・Bind.List…（Binding/Bind.*.cs）は、ここの芯へ当てる先
//  （IBindTarget・ITwoWayBindTarget・IListBindTarget）を渡すだけの薄い口。持ち主のスクリプト（owner）を受ける多重定義は
//  Binding/Bind.cs にある（SEEDScript はエンジンの型なのでこの部分には置かない）。
//  どれも IDisposable を返し、Dispose で結び付けを外す（二重の解除は無害）。
//  正典は docs/ui_binding.md §4。
// ============================================================

/// <summary>観測値を UI へ結び付ける静的ヘルパー。</summary>
public static partial class Bind
{
    /// <summary>
    /// 観測値が変わるたびに処理を呼ぶ（作った時点の値でも 1 回呼ぶ）。部品の型に依らない結び付け。
    /// </summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="source">観測値。</param>
    /// <param name="apply">値を受け取る処理。</param>
    /// <returns>外す口。</returns>
    public static IDisposable To<T>(IReadOnlyObservable<T> source, Action<T> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return OneWay(new ActionTarget<T>(apply), source);
    }

    /// <summary>
    /// 一方向に結ぶ（値 → 当てる先。作った時点の値ですぐ当て、変わるたびに当て直す）。自作の当てる先に使う。
    /// </summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="target">当てる先。</param>
    /// <param name="source">観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable OneWay<T>(IBindTarget<T> target, IReadOnlyObservable<T> source)
        => OneWay(target, source, Identity<T>.Convert);

    /// <summary>
    /// 一方向に結ぶ（値 → 変換 → 当てる先）。自作の当てる先に使う。
    /// </summary>
    /// <typeparam name="TSource">観測値の型。</typeparam>
    /// <typeparam name="TTarget">当てる値の型。</typeparam>
    /// <param name="target">当てる先。</param>
    /// <param name="source">観測値。</param>
    /// <param name="convert">観測値の値 → 当てる値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable OneWay<TSource, TTarget>(
        IBindTarget<TTarget> target, IReadOnlyObservable<TSource> source, Func<TSource, TTarget> convert)
        => CreateOneWay(target, source, convert);

    /// <summary>
    /// 双方向に結ぶ（観測値 ⇔ 当てる先。作った時点で観測値の値を当てる）。自作の部品に使う。
    /// </summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="target">当てる先（部品）。</param>
    /// <param name="source">観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable TwoWay<T>(ITwoWayBindTarget<T> target, Observable<T> source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        return new TwoWayBinding<T>(source, target);
    }

    /// <summary>
    /// 一覧を行の並びへ結ぶ（数・中身の変化を行の並びの口へ写す）。自作の並びに使う（SEED.UI.ListView には Bind.List(ListView, …)）。
    /// </summary>
    /// <typeparam name="T">項目の型。</typeparam>
    /// <param name="target">行の並び。</param>
    /// <param name="items">一覧。</param>
    /// <returns>外す口。</returns>
    public static IDisposable List<T>(IListBindTarget target, ObservableList<T> items)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(items);
        return new ListBinding<T>(items, target);
    }

    /// <summary>
    /// 観測値をフレームの区切りでまとめる（1 フレームに何度変わっても、区切りで最新の値を 1 回だけ知らせる）。
    /// 一方向の結び付けへそのまま渡す: <c>Bind.Text(this, label, Bind.Deferred(_score), s =&gt; $"{s}")</c>。
    /// 結び付けが外れれば元の購読も外れるので、作って渡して捨ててよい。
    /// </summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="source">元の観測値。</param>
    /// <returns>まとめる観測値（読むだけ）。</returns>
    public static IReadOnlyObservable<T> Deferred<T>(IReadOnlyObservable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new DeferredObservable<T>(source);
    }

    /// <summary>一方向の結び付けを作る（実行中の口〈Bind.Text の L10n 版など〉が Reapply を使うため型のまま返す）。</summary>
    /// <typeparam name="TSource">観測値の型。</typeparam>
    /// <typeparam name="TTarget">当てる値の型。</typeparam>
    /// <param name="target">当てる先。</param>
    /// <param name="source">観測値。</param>
    /// <param name="convert">観測値の値 → 当てる値。</param>
    /// <returns>結び付け。</returns>
    internal static ValueBinding<TSource, TTarget> CreateOneWay<TSource, TTarget>(
        IBindTarget<TTarget> target, IReadOnlyObservable<TSource> source, Func<TSource, TTarget> convert)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(convert);
        return new ValueBinding<TSource, TTarget>(source, convert, target);
    }

    /// <summary>そのまま返す変換（型ごとに 1 つだけ作る）。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    private static class Identity<T>
    {
        /// <summary>値をそのまま返す。</summary>
        internal static readonly Func<T, T> Convert = value => value;
    }
}
