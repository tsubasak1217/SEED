namespace SEED.Binding;

// ============================================================
//  IFrameTask.cs — フレームの区切り（BindingFrame.Tick）で呼ぶ仕事（内部用）
//
//  Bind.Deferred のまとめた値を当てる・当てる先の用意（部品の OnStart）を待つ、の 2 つに使う。
// ============================================================

/// <summary>フレームの区切りで呼ぶ仕事。</summary>
internal interface IFrameTask
{
    /// <summary>区切りで 1 回呼ばれる。</summary>
    /// <returns>次の区切りでもまた呼んでほしければ true（待ちを続ける）、終わったら false。</returns>
    bool RunFrameTask();
}
