using System;

namespace SEED.Binding;

// ============================================================
//  IReadOnlyObservable.cs — 読むだけの観測値（Observable・Computed・Bind.Deferred の共通の口）
//
//  「今の値」と「変わったら知らせる購読」だけを持つ。一方向の結び付け（Bind.Text・Bind.Visible…）はこれを受けるので、
//  Observable も Computed も Bind.Deferred で包んだものもそのまま渡せる。書き込める観測値は Observable<T>。
// ============================================================

/// <summary>読むだけの観測値（今の値と変化の購読）。</summary>
/// <typeparam name="T">値の型。</typeparam>
public interface IReadOnlyObservable<T>
{
    /// <summary>今の値。</summary>
    T Value { get; }

    /// <summary>
    /// 値の変化を購読する（変わったときだけ呼ぶ。購読した時点の値では呼ばない）。
    /// 戻り値を Dispose すると解除する（二重の解除は無害）。スクリプトの寿命に合わせるなら
    /// <c>Subscribe(owner: this, handler)</c>（BindingOwner）を使う。
    /// </summary>
    /// <param name="handler">新しい値を受け取る処理。</param>
    /// <returns>解除に使う口。</returns>
    IDisposable Subscribe(Action<T> handler);
}
