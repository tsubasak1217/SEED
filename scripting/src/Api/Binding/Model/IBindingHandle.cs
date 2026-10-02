namespace SEED.Binding;

// ============================================================
//  IBindingHandle.cs — 解除済みかを問える口（購読・結び付けの共通）
//
//  持ち主のスクリプトに預けた結び付け（DisposableBag）は、当てる先が消えて自分で外れた分を
//  袋が大きくなったときに捨てる。その判定に使う（利用者のコードは使わない）。
// ============================================================

/// <summary>解除済みかを問える口（購読・結び付け）。</summary>
internal interface IBindingHandle
{
    /// <summary>もう解除されたか。</summary>
    bool IsDisposed { get; }
}
