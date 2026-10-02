// ============================================================
//  RuntimeManager.AiToolReplies.cs — AI ツールが待ち合わせる応答の行の受け口（RuntimeManager の部分クラス）
//
//  【役割】
//  MCP の seed_platform_sim / seed_gpu_mem_report が送る命令の応答の行を、振り分けで「知っている行」として
//  ログへ残す（何もしないと末尾の else で "unknown:" と記録され、調べる人を迷わせる）。
//    PLATFORM_SIM_OK:{json} / PLATFORM_SIM_ERROR:{reason}            … SEED.Platform のデスクトップの模擬
//    GPU_MEM_REPORT_DONE:{path} / GPU_MEM_REPORT_ERROR:{reason}       … GPU メモリの内訳
//
//  【待ち合わせはここでしない】
//  応答を待つのは送った側（MainWindow.AiHost.Tools.cs の SendIpcAwaitReplyAsync）で、
//  個別のイベントより先に上がる RawMessageReceived を購読して拾う（端末の写しの閲覧 SNAPSHOT_VIEW_* と同じ流儀）。
//  書式の解釈は WPF 非依存の AI/Tools/RuntimeIpc/（PlatformSimIpc・GpuMemReportIpc）。
// ============================================================

using System;
using SEEDEditor.AI.Tools.RuntimeIpc;

namespace SEEDEditor.Runtime;

public sealed partial class RuntimeManager
{
    /// <summary>ログの行の頭（Output パネルで探しやすくする）。</summary>
    private const string AiToolReplyLogPrefix = "[Runtime→Editor]";

    /// <summary>
    /// AI ツールが待ち合わせる応答の行なら記録して true を返す（OnPipeMessage の振り分けから呼ぶ）。
    /// 待ち合わせは RawMessageReceived の受け手が済ませているので、ここでは記録だけ。
    /// </summary>
    /// <param name="msg">受け取った 1 行。</param>
    /// <returns>AI ツールの応答の行として扱ったら true（ほかの分岐へ回さない）。</returns>
    private bool TryHandleAiToolReplyMessage(string msg)
    {
        if (!PlatformSimIpc.TryParseReply(msg, out _) && !GpuMemReportIpc.TryParseReply(msg, out _, out _))
            return false;

        EditorLog.Write($"{AiToolReplyLogPrefix} {msg}");
        return true;
    }
}
