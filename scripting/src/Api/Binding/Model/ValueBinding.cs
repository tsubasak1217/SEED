using System;

namespace SEED.Binding;

// ============================================================
//  ValueBinding.cs — 一方向の結び付け（観測値 → 変換 → 当てる先）
//
//  Bind.Text・Bind.Visible・Bind.Color・Bind.To・Bind.OneWay の本体。
//    - 作った時点で今の値を当て、観測値が変わるたびに当て直す。
//    - 当てる先が無くなったら（IsAlive が false）自分を外す。
//    - 当てる先の用意がまだなら（IsReady が false）書かずに待ち、区切りで用意ができたら最新の値を当てる。
//    - Reapply: 値は同じままで変換し直して当てる（言語の切り替えで文を引き直すとき。Bind.Text の L10n 版）。
// ============================================================

/// <summary>一方向の結び付け（観測値 → 変換 → 当てる先）。</summary>
/// <typeparam name="TSource">観測値の型。</typeparam>
/// <typeparam name="TTarget">当てる値の型。</typeparam>
internal sealed class ValueBinding<TSource, TTarget> : BindingBase
{
    /// <summary>観測値。</summary>
    private readonly IReadOnlyObservable<TSource> _source;

    /// <summary>観測値の値 → 当てる値。</summary>
    private readonly Func<TSource, TTarget> _convert;

    /// <summary>当てる先。</summary>
    private readonly IBindTarget<TTarget> _target;

    /// <summary>結び付けを作り、今の値を当てる（当てる先の用意がまだなら待つ）。</summary>
    /// <param name="source">観測値。</param>
    /// <param name="convert">観測値の値 → 当てる値。</param>
    /// <param name="target">当てる先。</param>
    internal ValueBinding(IReadOnlyObservable<TSource> source, Func<TSource, TTarget> convert, IBindTarget<TTarget> target)
    {
        _source = source;
        _convert = convert;
        _target = target;

        // 先に購読してから今の値を読む（Computed・Deferred は最初の購読で依存をつなぎ、今の値を覚える）
        Own(source.Subscribe(Apply));
        try
        {
            Apply(source.Value);
        }
        catch
        {
            // 最初の当てが例外（変換・当てる先の書き込み）: 呼び手は結び付けを受け取れず owner にも預けられないので、
            // 購読を残さずに外してから投げ直す（以前は購読だけが残り、その後も書き続けた。2026-10-03。2 回目のレビュー #32）
            Dispose();
            throw;
        }
    }

    /// <summary>今の値を変換し直して当てる（値は同じでも当てる。言語の切り替えなど）。</summary>
    internal void Reapply()
    {
        if (IsDisposed) return;
        Apply(_source.Value);
    }

    /// <summary>値を当てる（当てる先が無ければ外れ、用意がまだなら待つ）。</summary>
    /// <param name="value">観測値の値。</param>
    private void Apply(TSource value)
    {
        if (IsDisposed) return;

        // 当てる先が無くなった（破棄した GameObject・部品）: 自分を外す
        if (!_target.IsAlive)
        {
            Dispose();
            return;
        }

        // 用意がまだ（部品の OnStart の前など）: 区切りで確かめ、用意ができたら最新の値を当てる
        if (!_target.IsReady)
        {
            WaitForTarget();
            return;
        }
        _target.Write(_convert(value));
    }

    /// <inheritdoc />
    protected override bool OnFrame()
    {
        if (!_target.IsAlive)
        {
            Dispose();
            return false;
        }
        if (!_target.IsReady) return true;

        // 待っている間に何度変わっても、当てるのは今の値 1 回だけ
        _target.Write(_convert(_source.Value));
        return false;
    }
}
