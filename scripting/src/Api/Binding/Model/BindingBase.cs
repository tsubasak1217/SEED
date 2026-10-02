using System;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  BindingBase.cs — 結び付けの共通の土台（解除・預かった購読・当てる先の用意を待つ）
//
//  一方向（ValueBinding）・双方向（TwoWayBinding）・一覧（ListBinding）が受け継ぐ。
//    - Dispose: 二重の解除は無害。預かった購読（観測値の購読・部品の知らせ・L10n.Changed など）を全部外す。
//    - 当てる先の用意を待つ: WaitForTarget で BindingFrame へ 1 回だけ積み、区切りごとに OnFrame で確かめる。
//  Bind.* が返す IDisposable の正体。
// ============================================================

/// <summary>結び付けの共通の土台。</summary>
internal abstract class BindingBase : IDisposable, IBindingHandle, IFrameTask
{
    /// <summary>この結び付けと一緒に外す購読（観測値・部品・言語の切り替え…）。</summary>
    private List<IDisposable>? _owned;

    /// <summary>フレームの区切りを待っているか（BindingFrame へ二重に積まない）。</summary>
    private bool _waiting;

    /// <summary>解除済みか。</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>この結び付けと一緒に外す購読を預ける（解除済みなら、その場で外す）。</summary>
    /// <param name="resource">預ける購読。</param>
    internal void Own(IDisposable resource)
    {
        if (IsDisposed)
        {
            DisposeSafely(resource);
            return;
        }
        (_owned ??= new List<IDisposable>()).Add(resource);
    }

    /// <summary>結び付けを外す（預かった購読を全部外す。二重の解除は無害）。</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        OnDisposed();
        var owned = _owned;
        _owned = null;
        if (owned is null) return;
        foreach (var resource in owned) DisposeSafely(resource);
    }

    /// <summary>派生の後片付け（Dispose の頭で 1 回。預かった購読はこの後に外れる）。</summary>
    protected virtual void OnDisposed() { }

    /// <summary>当てる先の用意を待つ（次の区切りから OnFrame が呼ばれる。待っている間は二重に積まない）。</summary>
    protected void WaitForTarget()
    {
        if (_waiting || IsDisposed) return;
        _waiting = true;
        BindingFrame.Enqueue(this);
    }

    /// <summary>フレームの区切り: 解除済みなら終わり、そうでなければ派生に確かめさせる。</summary>
    /// <returns>まだ待つなら true。</returns>
    bool IFrameTask.RunFrameTask()
    {
        if (IsDisposed)
        {
            _waiting = false;
            return false;
        }
        bool keepWaiting = OnFrame();
        _waiting = keepWaiting;
        return keepWaiting;
    }

    /// <summary>区切りごとに当てる先の用意を確かめる（用意ができたら当てて false、まだなら true）。</summary>
    /// <returns>まだ待つなら true。</returns>
    protected abstract bool OnFrame();

    /// <summary>購読を外す（外す処理の例外はログへ出して続ける）。</summary>
    /// <param name="resource">外す購読。</param>
    private static void DisposeSafely(IDisposable resource)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception ex)
        {
            BindingLog.Error($"結び付けの購読を外す処理で例外が起きました: {ex}");
        }
    }
}
