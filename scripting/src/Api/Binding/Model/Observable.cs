using System;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  Observable.cs — 観測できる値（Flutter の ValueNotifier・UniRx の ReactiveProperty の最小部分）
//
//  【使い方】画面のスクリプトが状態を Observable で持ち、UI は Bind.* で結ぶ（いつ UI を直すかを書かない）。
//      private readonly Observable<int> _count = new(0);
//      Bind.Text(this, label, _count, n => $"{n} 回");   // 値 → 文字（今の値ですぐ当て、変わるたびに当て直す）
//      _count.Value++;                                    // 書き換えると、その場で購読と結び付けへ届く
//  【約束】
//    - Value の set は、今の値と等しければ何もしない（知らせない）。比べ方は既定で EqualityComparer<T>.Default。
//    - 知らせは即時・同期（フレームを待たない）。まとめたいときは Bind.Deferred。
//    - 購読の中で値を変える再入は、配り終えてからもう 1 周（BindingLimits.MaxReentrantDepth 段まで。ValueNotifier）。
//    - スクリプトのフェーズ（メインスレッド）だけから使う。
//  正典は docs/ui_binding.md §3。
// ============================================================

/// <summary>観測できる値（変わったら購読と結び付けへ即座に知らせる）。</summary>
/// <typeparam name="T">値の型。</typeparam>
public sealed class Observable<T> : IReadOnlyObservable<T>
{
    /// <summary>警告に出す種類の名前。</summary>
    private const string KindName = "Observable";

    /// <summary>今の値。</summary>
    private T _value;

    /// <summary>値の比べ方（等しければ知らせない）。</summary>
    private readonly IEqualityComparer<T> _comparer;

    /// <summary>知らせ方。</summary>
    private readonly ValueNotifier<T> _notifier;

    /// <summary>観測値を作る。</summary>
    /// <param name="initial">最初の値（知らせない）。</param>
    /// <param name="comparer">値の比べ方（null なら EqualityComparer&lt;T&gt;.Default）。</param>
    public Observable(T initial = default!, IEqualityComparer<T>? comparer = null)
    {
        _value = initial;
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _notifier = new ValueNotifier<T>(() => _value, _comparer, KindName);
    }

    /// <summary>
    /// 今の値。set は今の値と等しければ何もせず、違えば入れてから購読と結び付けへ即座に知らせる。
    /// </summary>
    public T Value
    {
        get => _value;
        set
        {
            // 等しければ知らせない（結び付けの往復もここで止まる）
            if (_comparer.Equals(_value, value)) return;
            _value = value;
            _notifier.Publish();
        }
    }

    /// <summary>今の購読の数（結び付けを含む。診断・テスト用）。</summary>
    public int SubscriberCount => _notifier.SubscriberCount;

    /// <summary>
    /// 値の比べ方（作るときに渡したもの。無ければ EqualityComparer&lt;T&gt;.Default）。双方向の結び付けが、部品へ書き戻すかを
    /// この観測値と同じ比べ方で決めるのに使う（2026-10-03。2 回目のレビュー #25）。
    /// </summary>
    internal IEqualityComparer<T> Comparer => _comparer;

    /// <summary>
    /// 値の変化を購読する（変わったときだけ呼ぶ。購読した時点の値では呼ばない）。戻り値を Dispose すると解除する。
    /// スクリプトの寿命に合わせるなら <c>Subscribe(owner: this, handler)</c>。
    /// </summary>
    /// <param name="handler">新しい値を受け取る処理。</param>
    /// <returns>解除の口（null は返さない）。</returns>
    public IDisposable Subscribe(Action<T> handler) => _notifier.Subscribe(handler);

    /// <summary>
    /// 値は同じままで購読へ知らせる（参照型の中身を書き換えたときなど。Flutter の notifyListeners）。
    /// </summary>
    public void Notify() => _notifier.Publish(force: true);

    /// <summary>今の値の文字列（ログ用）。</summary>
    public override string ToString() => _value?.ToString() ?? string.Empty;
}
