using System;

namespace SEED.Binding;

// ============================================================
//  DisposableAction.cs — Dispose で 1 回だけ処理を呼ぶ口（部品のイベントの購読を外す口などに使う）
// ============================================================

/// <summary>Dispose で 1 回だけ処理を呼ぶ口（二重の Dispose は無害）。</summary>
public sealed class DisposableAction : IDisposable, IBindingHandle
{
    /// <summary>何もしない口（共有）。</summary>
    public static readonly IDisposable Empty = new DisposableAction(null);

    /// <summary>Dispose で呼ぶ処理（呼んだら null）。</summary>
    private Action? _onDispose;

    /// <summary>口を作る。</summary>
    /// <param name="onDispose">Dispose で 1 回だけ呼ぶ処理（null なら何もしない）。</param>
    public DisposableAction(Action? onDispose)
    {
        _onDispose = onDispose;
    }

    /// <inheritdoc />
    public bool IsDisposed => _onDispose is null;

    /// <summary>処理を 1 回だけ呼ぶ。</summary>
    public void Dispose()
    {
        var onDispose = _onDispose;
        _onDispose = null;
        onDispose?.Invoke();
    }
}
