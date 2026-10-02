using System;
using SEEDEditor.Scripting;

namespace SEED.Binding;

// ============================================================
//  Bind.cs — 結び付けの静的ヘルパーのうち、持ち主のスクリプト（owner）を受ける芯の多重定義（実行中だけの部分）
//
//  Bind.X(owner: this, …) は Bind.X(…) と同じ結び付けを作り、owner の破棄（OnDestroy の直後）で自動で外す
//  （this.On と同じ仕組み。SEEDScript.UnsubscribeAllEvents）。早く外したいときは戻り値を Dispose してよい（二重の解除は無害）。
//  UI 部品向けは Bind.Text.cs・Bind.Node.cs・Bind.Widgets.cs・Bind.List.cs。エンジンに依らない芯は Model/Bind.Core.cs。
//
//  【名前の衝突】画面のスクリプトに Bind という名前のメソッドがあると、その中の Bind.Text(…) はメソッドを指して
//  コンパイルできない（CS0119）。SEED.Binding.Bind.Text(…) と書くか、using BindTo = SEED.Binding.Bind; のように別名を付ける。
// ============================================================

public static partial class Bind
{
    /// <summary>観測値が変わるたびに処理を呼ぶ（作った時点の値でも 1 回呼ぶ。owner の破棄で自動で外れる）。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="source">観測値。</param>
    /// <param name="apply">値を受け取る処理。</param>
    /// <returns>外す口。</returns>
    public static IDisposable To<T>(SEEDScript owner, IReadOnlyObservable<T> source, Action<T> apply)
        => Own(owner, To(source, apply));

    /// <summary>一方向に結ぶ（自作の当てる先。owner の破棄で自動で外れる）。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト。</param>
    /// <param name="target">当てる先。</param>
    /// <param name="source">観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable OneWay<T>(SEEDScript owner, IBindTarget<T> target, IReadOnlyObservable<T> source)
        => Own(owner, OneWay(target, source));

    /// <summary>一方向に結ぶ（自作の当てる先・変換つき。owner の破棄で自動で外れる）。</summary>
    /// <typeparam name="TSource">観測値の型。</typeparam>
    /// <typeparam name="TTarget">当てる値の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト。</param>
    /// <param name="target">当てる先。</param>
    /// <param name="source">観測値。</param>
    /// <param name="convert">観測値の値 → 当てる値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable OneWay<TSource, TTarget>(
        SEEDScript owner, IBindTarget<TTarget> target, IReadOnlyObservable<TSource> source, Func<TSource, TTarget> convert)
        => Own(owner, OneWay(target, source, convert));

    /// <summary>双方向に結ぶ（自作の部品。owner の破棄で自動で外れる）。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト。</param>
    /// <param name="target">当てる先（部品）。</param>
    /// <param name="source">観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable TwoWay<T>(SEEDScript owner, ITwoWayBindTarget<T> target, Observable<T> source)
        => Own(owner, TwoWay(target, source));

    /// <summary>一覧を行の並びへ結ぶ（自作の並び。owner の破棄で自動で外れる）。</summary>
    /// <typeparam name="T">項目の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト。</param>
    /// <param name="target">行の並び。</param>
    /// <param name="items">一覧。</param>
    /// <returns>外す口。</returns>
    public static IDisposable List<T>(SEEDScript owner, IListBindTarget target, ObservableList<T> items)
        => Own(owner, List(target, items));

    /// <summary>結び付けを owner に預ける（owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト。</param>
    /// <param name="binding">結び付け。</param>
    /// <returns>結び付け（そのまま返す）。</returns>
    private static IDisposable Own(SEEDScript owner, IDisposable binding) => BindingOwner.Track(owner, binding);
}
