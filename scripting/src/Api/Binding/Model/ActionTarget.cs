using System;

namespace SEED.Binding;

// ============================================================
//  ActionTarget.cs — 処理（Action<T>）を当てる先にする（Bind.To 用）
//
//  部品の型に依らず「値が変わったらこれをする」を結び付けにする。いつも生きていて、いつでも当てられる。
//  寿命はスクリプトに合わせる（Bind.To(owner: this, …)）か、戻り値を Dispose する。
// ============================================================

/// <summary>処理を当てる先にする。</summary>
/// <typeparam name="T">値の型。</typeparam>
internal sealed class ActionTarget<T> : IBindTarget<T>
{
    /// <summary>値を受け取る処理。</summary>
    private readonly Action<T> _apply;

    /// <summary>当てる先を作る。</summary>
    /// <param name="apply">値を受け取る処理。</param>
    internal ActionTarget(Action<T> apply)
    {
        _apply = apply;
    }

    /// <inheritdoc />
    public bool IsAlive => true;

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public void Write(T value) => _apply(value);
}
