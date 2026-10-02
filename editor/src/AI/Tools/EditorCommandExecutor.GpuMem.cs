// ============================================================
//  EditorCommandExecutor.GpuMem.cs — GPU メモリの内訳（gpu_mem_report）
//
//  外部エージェント（MCP の seed_gpu_mem_report）が GPU メモリの内訳を取るためのコマンド。
//  EditorCommandExecutor の partial 実装。
//
//  【流れ】
//   1. 書き出し先を OS の一時フォルダの下（スクリーンショットと同じ seed_mcp/）に決め、フォルダを作る
//      （ランタイムの書き出しは親フォルダを作らない。外から任意のパスは受け取らない。GpuMemReportIpc 冒頭）
//   2. GPU_MEM_REPORT:{パス} を送り、GPU_MEM_REPORT_DONE / GPU_MEM_REPORT_ERROR の 1 行を待つ
//   3. DONE なら書かれた JSON を読んで report に埋め込む（要約の表は MCP サーバー側の GpuMemReportFormatter が付ける）
//  ランタイム側は runtime/src/engine/core/app_base/app/gpu_mem_ops.rs（JSON の正典は renderer/gpu_mem/report.rs）。
//
//  【安全性】
//   観測系（ランタイムの記録を読んで一時フォルダへ書くだけ。シーン・プロジェクトのファイルに触れない）。
//   AiOperationPolicy の ReadOnlyCommands に入れてある。計測そのものは起動時に有効にしておく必要がある
//   （seed_launch(gpu_mem_log:true)。無効なら GPU_MEM_REPORT_ERROR が返る）。
// ============================================================

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SEEDEditor.AI.Tools.RuntimeIpc;

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>
    /// 応答待ちのタイムアウト（ミリ秒）。
    /// ランタイムは IPC を受けたフレームで記録の写しを集計してファイルへ書く（数 ms〜数十 ms）。
    /// 大きなシーンの読み込み直後でフレームが遅れることを見込んで余裕を持たせる。
    /// </summary>
    private const int GpuMemReportTimeoutMs = 10_000;

    /// <summary>計測が無効なときに添える、MCP からの有効にし方。</summary>
    private const string GpuMemReportEnableHint =
        "計測が無効なら、seed_shutdown してから seed_launch(gpu_mem_log:true) で起動し直してください"
      + "（計測は起動時にだけ有効にできます。docs/rendering_profiles.md §4）。";

    // ── ディスパッチ ─────────────────────────────────────────────

    /// <summary>
    /// GPU メモリの内訳のコマンドを実行する。扱わないコマンド名なら null を返し、呼び出し元が他のグループへ委ねる。
    /// </summary>
    /// <param name="command">コマンド名。</param>
    /// <param name="args">ツール引数（使わない。要約の件数 top は MCP サーバー側が読む）。</param>
    private Task<string>? ExecuteGpuMemTool(string command, JsonElement args)
        => command switch
        {
            "gpu_mem_report" => ExecuteGpuMemReportAsync(),
            _                => null,
        };

    // ── コマンド実装 ─────────────────────────────────────────────

    /// <summary>
    /// GPU_MEM_REPORT を送り、書かれた内訳の JSON を読んで返す。
    /// </summary>
    private async Task<string> ExecuteGpuMemReportAsync()
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        // ── 1. 書き出し先（スクリーンショットと同じ一時フォルダの下）。フォルダは先に作る ──
        var directory = Path.Combine(Path.GetTempPath(), VisualScreenshotSubDir);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error($"書き出し先のフォルダを作れませんでした（{directory}）: {ex.Message}");
        }
        var command = GpuMemReportIpc.BuildCommand(GpuMemReportIpc.MakeOutputPath(directory, DateTime.Now));

        // ── 2. 送って 1 行の応答を待つ ──
        var reply = await host.SendIpcAwaitReplyAsync(command, GpuMemReportIpc.ReplyPrefixes, GpuMemReportTimeoutMs);
        _log($"[AI ツール] {command} → {reply.Line ?? reply.Status.ToString()}");

        if (!reply.HasLine || !GpuMemReportIpc.TryParseReply(reply.Line!, out var ok, out var payload))
            return Json(new { ok = false, sent = command, error = reply.DescribeFailure(GpuMemReportTimeoutMs) });
        if (!ok)
            return Json(new { ok = false, sent = command, error = payload, hint = GpuMemReportEnableHint });

        // ── 3. 書かれた JSON を読む（ランタイムは同じマシン・同じ利用者で動くので、そのまま読める）──
        string json;
        try
        {
            json = await File.ReadAllTextAsync(payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Json(new { ok = false, path = payload, error = $"内訳の JSON を読めませんでした: {ex.Message}" });
        }

        return Json(new
        {
            ok     = true,
            path   = payload,
            state  = host.RuntimeState.ToString(),
            report = RawJson(json),
        });
    }
}
