using System;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  BindingFrame.cs — 結び付けのフレームの区切り（Bind.Deferred のまとめ・当てる先の用意を待つ）
//
//  【呼ばれる時】エンジン（ScriptBridge）がフレームに 1 回、LateUpdate のフェーズの頭（全スクリプトの Update の後・描画の前）で
//  Tick を呼ぶ。テストは Tick を直接呼んで 1 フレームを進める。
//  【仕事】
//    - Bind.Deferred: そのフレームに何度変わっても、区切りで最新の値を 1 回だけ当てる。
//    - 当てる先の用意を待つ: 部品のスクリプトの OnStart の前（UiWidget.Of が null）・選択の項目がまだ集まっていない間は、
//      区切りごとに確かめ、用意ができたら最新の値を当てる。
//  【約束】区切りの途中で積まれた仕事は次の区切りで呼ぶ（同じ区切りで回り続けない）。仕事の例外はエラーログへ出して捨てる。
//  正典は docs/ui_binding.md §5。
// ============================================================

/// <summary>結び付けのフレームの区切り。</summary>
public static class BindingFrame
{
    /// <summary>次の区切りで呼ぶ仕事。</summary>
    private static List<IFrameTask> _queued = new();

    /// <summary>今の区切りで呼んでいる仕事（使い回す入れ物）。</summary>
    private static List<IFrameTask> _running = new();

    /// <summary>区切りの途中か（Tick の中から Tick を呼んでも二重に回さない）。</summary>
    private static bool _ticking;

    /// <summary>次の区切りを待っている仕事の数（診断・テスト用）。</summary>
    public static int PendingCount => _queued.Count;

    /// <summary>次の区切りで呼ぶ仕事を積む（同じ仕事を二重に積まないのは積む側の責任）。</summary>
    /// <param name="task">仕事。</param>
    internal static void Enqueue(IFrameTask task) => _queued.Add(task);

    /// <summary>
    /// フレームの区切り: 積まれた仕事を積んだ順に呼ぶ（エンジンがフレームに 1 回呼ぶ。テストは直接呼ぶ）。
    /// 待ちを続ける仕事（true を返した）と、区切りの途中で積まれた仕事は次の区切りへ回す。
    /// </summary>
    public static void Tick()
    {
        if (_ticking || _queued.Count == 0) return;
        _ticking = true;

        // 入れ物を入れ替える（区切りの途中で積まれた仕事は _queued へ入り、次の区切りで呼ばれる）
        (_running, _queued) = (_queued, _running);
        try
        {
            foreach (var task in _running)
            {
                bool keep;
                try
                {
                    keep = task.RunFrameTask();
                }
                catch (Exception ex)
                {
                    // 1 つの仕事の失敗で他の仕事を止めない（その仕事は捨てる）
                    BindingLog.Error($"結び付けのフレームの区切りの仕事で例外が起きました（その仕事は捨てます）: {ex}");
                    keep = false;
                }
                if (keep) _queued.Add(task);
            }
        }
        finally
        {
            _running.Clear();
            _ticking = false;
        }
    }

    /// <summary>
    /// 積まれた仕事を全部捨てる（スクリプトの読み直し〈ホットリロード〉の前に ScriptBridge が呼ぶ。
    /// 旧アセンブリのデリゲートを握ったまま ALC が解放されない事故を防ぐ）。
    /// </summary>
    internal static void ResetForReload()
    {
        _queued.Clear();
        _running.Clear();
        _ticking = false;
    }
}
