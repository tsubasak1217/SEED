// ============================================================
//  MainWindow.AiHost.Tools.cs — IEditorAiHost の「応答を待つ」系の実装（MainWindow の部分クラス。2026-10-02）
//
//  【役割】
//  MCP の seed_platform_sim / seed_gpu_mem_report / seed_preview / seed_template_actor が使う窓口。
//    - SendIpcAwaitReplyAsync  … 1 行送って、決まった頭の応答 1 行を待つ（PLATFORM_SIM・GPU_MEM_REPORT）
//    - AddScreenPreviewAsync    … 画面プレビューを差し込む（右クリック／インスペクタの案内と同じ道筋 → PREVIEW_ADDED を待つ）
//    - ClearScreenPreviewAsync  … 画面プレビューを消す（同じ道筋 → PREVIEW_CLEARED を待つ）
//    - AddTemplateActorAsync    … テンプレートアクタを追加する（窓と同じ TemplateActorAddFlow → SCENE_MODIFIED を待つ）
//
//  【設計方針】（MainWindow.AiHost.cs と同じ）
//   ・既存の操作経路を再利用する。AI 専用の別経路を作らない（判定・引き直し・送信は UI の関数そのもの）。
//   ・応答は RuntimeManager.RawMessageReceived（受信スレッドで、個別のイベントより先に上がる）を
//     送る前に購読して拾う。いずれもタイムアウト付きで UI スレッドを固めない。
//   ・応答には相関 ID が無いので、同じ頭の応答を待つ命令を並行して送ると取り違えうる
//     （AI ツールは 1 コールずつ逐次に呼ばれる前提。docs/editor_mcp.md §6.5）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SEEDEditor.AI.Tools;
using SEEDEditor.AI.Tools.RuntimeIpc;
using SEEDEditor.Preview;
using SEEDEditor.Templates.Actors;

namespace SEEDEditor;

public partial class MainWindow
{
    /// <summary>親が木に無いときの理由の書式（{0} = DFS 番号）。</summary>
    private const string AiParentNotFoundFormat =
        "親の DFS {0} が表示中のヒエラルキーに見つかりません（seed_hierarchy で番号を確かめてください）";

    // ── 応答の待ち合わせ ─────────────────────────────────────────

    /// <inheritdoc/>
    Task<AiIpcReply> IEditorAiHost.SendIpcAwaitReplyAsync(
        string command, IReadOnlyList<string> replyPrefixes, int timeoutMs) =>
        AwaitRuntimeReplyAsync(
            send: () =>
            {
                _runtimeManager!.SendToRuntime(command);
                return null;
            },
            isReply: line => replyPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)),
            timeoutMs);

    /// <summary>
    /// 送る前に応答の行の購読を始め、送り、目的の行が届くか時間切れになるまで待つ（AI ツールの共通の待ち合わせ）。
    /// </summary>
    /// <param name="send">送る処理（送れなかったら理由を返す。送れたら null）。</param>
    /// <param name="isReply">待っている応答の行か（受信スレッドで呼ばれる。軽い判定だけにする）。</param>
    /// <param name="timeoutMs">応答待ちのタイムアウト（ミリ秒）。</param>
    /// <returns>結果（応答行・時間切れ・未接続・送る前に断った理由）。</returns>
    private async Task<AiIpcReply> AwaitRuntimeReplyAsync(Func<string?> send, Func<string, bool> isReply, int timeoutMs)
    {
        // 未接続なら送っても誰も応答しない。タイムアウトを待たせず即座に返す。
        if (_runtimeManager is null || !_runtimeManager.IsPipeConnected) return AiIpcReply.Disconnected();

        // 受信スレッドから完了させるので、継続は非同期にして UI スレッドの再入を避ける（SelectActorAsync と同じ）。
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string line)
        {
            if (isReply(line)) tcs.TrySetResult(line);
        }
        _runtimeManager.RawMessageReceived += OnLine;

        try
        {
            if (send() is { } refused) return AiIpcReply.RefusedBecause(refused);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return completed == tcs.Task ? AiIpcReply.FromLine(await tcs.Task) : AiIpcReply.Timeout();
        }
        finally
        {
            _runtimeManager.RawMessageReceived -= OnLine;
        }
    }

    // ── 画面プレビュー ───────────────────────────────────────────

    /// <inheritdoc/>
    PreviewHostCatalog IEditorAiHost.ScreenPreviewHosts => _previewHosts;

    /// <inheritdoc/>
    async Task<AiIpcReply> IEditorAiHost.AddScreenPreviewAsync(
        int parentDfs, string prefab, ResolvedPreviewSlot? slot, int timeoutMs)
    {
        // UI と同じ判定（Edit か・閲覧専用の表示中でないか）
        if (PreviewNotEditableReason() is { } notEditable) return AiIpcReply.RefusedBecause(notEditable);

        // 差し込み先を UI と同じ形で作る（右クリック = 枠なし・親の直下 / 案内の行 = 差し込む子・枠・底上げ）
        var target = slot is null
            ? PanelHierarchy.TryCreatePreviewTargetFor(parentDfs, prefab)
            : CreateAiHostSlotPreviewTarget(parentDfs, prefab, slot);
        if (target is null) return AiIpcReply.RefusedBecause(string.Format(AiParentNotFoundFormat, parentDfs));

        return await AwaitRuntimeReplyAsync(
            send: () => SendPreviewRequest(target, prefab, rememberRecent: false),
            isReply: line => line.StartsWith(ScreenPreviewIpc.AddedPrefix, StringComparison.Ordinal)
                          || line.StartsWith(ScreenPreviewIpc.ErrorPrefix, StringComparison.Ordinal),
            timeoutMs);
    }

    /// <inheritdoc/>
    async Task<AiIpcReply> IEditorAiHost.ClearScreenPreviewAsync(int? dfs, int timeoutMs) =>
        await AwaitRuntimeReplyAsync(
            send: () => SendPreviewClear(dfs),
            isReply: line => line.StartsWith(ScreenPreviewIpc.ClearedPrefix, StringComparison.Ordinal)
                          || line.StartsWith(ScreenPreviewIpc.ErrorPrefix, StringComparison.Ordinal),
            timeoutMs);

    /// <summary>
    /// 差し込み先の案内の行（欄の値を当てたもの）から差し込み先を作る
    /// （インスペクタの CreateHostTarget → ヒエラルキーの DescribePreviewParent と同じ組み立て）。
    /// </summary>
    /// <param name="parentDfs">親（案内のスクリプトを持つアクタ）の DFS 番号。</param>
    /// <param name="prefab">中身のプレハブ。</param>
    /// <param name="slot">欄の値を当てた差し込み先の行。</param>
    /// <returns>差し込み先。親が木に無ければ null。</returns>
    private PreviewInsertTarget? CreateAiHostSlotPreviewTarget(int parentDfs, string prefab, ResolvedPreviewSlot slot)
    {
        // 木に無い親は、送る直前の引き直しでも見失うので、ここで断る
        if (!PanelHierarchy.ContainsNode(parentDfs)) return null;

        var described = PanelHierarchy.DescribePreviewParent(new PreviewInsertTarget
        {
            ParentDfs = parentDfs,
            Under     = slot.Under,
            Frame     = slot.Frame,
            FrameBody = slot.FrameBody,
            LayerBias = slot.LayerBias,
            Prefab    = prefab,
        });
        return described with
        {
            Label = PreviewInsertTarget.DescribeLabel(described.ParentName, slot.Under, slot.Frame is not null),
        };
    }

    // ── テンプレートアクタ ───────────────────────────────────────

    /// <inheritdoc/>
    async Task<(TemplateActorAddOutcome Outcome, AiIpcReply Reply)> IEditorAiHost.AddTemplateActorAsync(
        string libraryRoot, TemplateActorEntry entry, int? parentDfs, int timeoutMs)
    {
        // 追加先を右クリックと同じ形で作る（親あり = そのノードの子、親なし = ルート）
        var target = parentDfs is int dfs
            ? PanelHierarchy.TryCreateTemplateActorTargetFor(dfs)
            : PanelHierarchy.CreateRootTemplateActorTarget();
        if (target is null)
        {
            var lost = new TemplateActorAddOutcome
            {
                Status  = TemplateActorAddStatus.TargetLost,
                Message = string.Format(AiParentNotFoundFormat, parentDfs),
                Target  = new TemplateActorTarget { ParentDfs = parentDfs },
            };
            return (lost, AiIpcReply.RefusedBecause(lost.Message));
        }

        // 送信だけを「購読してから送る」に包む（準備〈数秒かかりうる〉の間に届いた無関係の知らせを拾わないため、
        // 購読は送る直前に始める）。手順そのものは窓と同じ TemplateActorAddFlow。
        TaskCompletionSource<string>? tcs = null;
        void OnLine(string line)
        {
            if (line == TemplateActorIpc.AddedNotice
                || line.StartsWith(TemplateActorIpc.RejectedPrefix, StringComparison.Ordinal))
                tcs?.TrySetResult(line);
        }
        var context = CreateTemplateActorContext(libraryRoot, sendToRuntime: command =>
        {
            if (_runtimeManager is null) return;
            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runtimeManager.RawMessageReceived += OnLine;
            _runtimeManager.SendToRuntime(command);
        });

        try
        {
            var outcome = await TemplateActorAddFlow.RunAsync(context, target, entry);
            if (!outcome.IsSent)
                return (outcome, AiIpcReply.RefusedBecause(outcome.Message ?? TemplateActorAddFlow.TargetLostFallbackMessage));
            if (tcs is null) return (outcome, AiIpcReply.Disconnected());

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return (outcome, completed == tcs.Task ? AiIpcReply.FromLine(await tcs.Task) : AiIpcReply.Timeout());
        }
        finally
        {
            if (_runtimeManager is not null) _runtimeManager.RawMessageReceived -= OnLine;
        }
    }
}
