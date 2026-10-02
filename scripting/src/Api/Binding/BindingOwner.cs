using System;
using SEEDEditor.Scripting;

namespace SEED.Binding;

// ============================================================
//  BindingOwner.cs — 購読・結び付けの寿命をスクリプトに合わせる（this.On と同じ仕組み）
//
//  スクリプト（SEEDScript）に預けた物は、そのスクリプトの破棄（OnDestroy の直後・DestroyComponent）で自動で Dispose される
//  （SEEDScript.UnsubscribeAllEvents が this.On の購読と一緒に外す）。
//      _count.Subscribe(this, n => Debug.Log($"{n}"));          // 観測値の購読（破棄で自動で外れる）
//      _items.Subscribe(this, change => …);                      // 一覧の購読
//      var total = Computed.From(_a, _b, (a, b) => a + b);       // 作るだけなら預けなくてよい（購読が無ければ依存をつながない）
//      _bag.AddTo(this);                                         // 任意の IDisposable を預ける（UniRx の AddTo）
//  Bind.*(owner: this, …) も中でここを通る。
// ============================================================

/// <summary>購読・結び付けの寿命をスクリプトに合わせる拡張。</summary>
public static class BindingOwner
{
    /// <summary>
    /// 観測値の変化を購読する（スクリプトの破棄で自動で外れる）。変わったときだけ呼ぶ（購読した時点の値では呼ばない）。
    /// </summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="source">観測値。</param>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="handler">新しい値を受け取る処理。</param>
    /// <returns>早く外したいときに Dispose する口。</returns>
    public static IDisposable Subscribe<T>(this IReadOnlyObservable<T> source, SEEDScript owner, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Track(owner, source.Subscribe(handler));
    }

    /// <summary>
    /// 一覧の変化を購読する（スクリプトの破棄で自動で外れる）。変化を起きた順に 1 件ずつ受け取る。
    /// </summary>
    /// <typeparam name="T">項目の型。</typeparam>
    /// <param name="source">一覧。</param>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="handler">変化を受け取る処理。</param>
    /// <returns>早く外したいときに Dispose する口。</returns>
    public static IDisposable Subscribe<T>(this ObservableList<T> source, SEEDScript owner, Action<ListChange<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Track(owner, source.Subscribe(handler));
    }

    /// <summary>
    /// 任意の IDisposable をスクリプトに預ける（破棄で自動で Dispose される。UniRx の AddTo）。
    /// </summary>
    /// <typeparam name="TDisposable">預ける物の型。</typeparam>
    /// <param name="disposable">預ける物。</param>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <returns>預けた物（続けて書けるようにそのまま返す）。</returns>
    public static TDisposable AddTo<TDisposable>(this TDisposable disposable, SEEDScript owner) where TDisposable : IDisposable
        => Track(owner, disposable);

    /// <summary>スクリプトに預ける（破棄の後に預けた物はその場で Dispose される）。</summary>
    /// <typeparam name="TDisposable">預ける物の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト。</param>
    /// <param name="disposable">預ける物。</param>
    /// <returns>預けた物。</returns>
    internal static TDisposable Track<TDisposable>(SEEDScript owner, TDisposable disposable) where TDisposable : IDisposable
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(disposable);
        owner.TrackOwned(disposable);
        return disposable;
    }
}
