// ============================================================
//  EditorCommandExecutor.PlatformSim.cs — SEED.Platform のデスクトップの模擬の操作（platform_sim）
//
//  外部エージェント（MCP の seed_platform_sim）が、PC の Play の模擬の権限の状態と答え・前面と背面を
//  実行中に変えるためのコマンド。EditorCommandExecutor の partial 実装。
//
//  【流れ】
//   1. 引数（verb・args）を 1 行の命令 PLATFORM_SIM:{verb},{args…} にする（PlatformSimIpc。壊れる入力は送る前に断る）
//   2. IEditorAiHost.SendIpcAwaitReplyAsync で送り、PLATFORM_SIM_OK / PLATFORM_SIM_ERROR の 1 行を待つ
//   3. OK なら模擬の命令の返答の JSON をそのまま reply に入れ、ERROR なら理由を日本語に言い換えて返す
//  ランタイム側は runtime/src/engine/core/app_base/app/platform_sim_ops.rs（動詞の表の正典）。
//
//  【安全性】
//   変更系（ゲームの状態を変える）。AiOperationPolicy の ReadOnlyCommands に入れていないので、
//   読み取り専用の対話エディタでは拒否される。ランタイムも Play 中以外を not_playing で断る。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SEEDEditor.AI.Tools.RuntimeIpc;

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>platform_sim: 動詞の引数名。</summary>
    private const string PlatformSimVerbArg = "verb";

    /// <summary>platform_sim: 動詞の引数の並びの引数名。</summary>
    private const string PlatformSimArgsArg = "args";

    /// <summary>
    /// 応答待ちのタイムアウト（ミリ秒）。
    /// ランタイムは IPC を受けたフレームの中で模擬の命令を呼んで即答するので、入力注入の単発と同じ余裕で足りる。
    /// </summary>
    private const int PlatformSimTimeoutMs = 5_000;

    // ── ディスパッチ ─────────────────────────────────────────────

    /// <summary>
    /// 模擬の操作のコマンドを実行する。扱わないコマンド名なら null を返し、呼び出し元が他のグループへ委ねる。
    /// </summary>
    /// <param name="command">コマンド名。</param>
    /// <param name="args">ツール引数。</param>
    private Task<string>? ExecutePlatformSimTool(string command, JsonElement args)
        => command switch
        {
            "platform_sim" => ExecutePlatformSimAsync(args),
            _              => null,
        };

    // ── コマンド実装 ─────────────────────────────────────────────

    /// <summary>
    /// PLATFORM_SIM を送り、応答を JSON へ整形して返す。
    /// </summary>
    /// <param name="args">ツール引数（verb・args）。</param>
    private async Task<string> ExecutePlatformSimAsync(JsonElement args)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        // ── 1. 1 行の命令にする（壊れる入力は送らない。送るとランタイムが黙って捨て、待ちが時間切れになる）──
        if (!TryReadStringArray(args, PlatformSimArgsArg, out var simArgs, out var argsError))
            return Error(argsError!);
        var command = PlatformSimIpc.TryBuildCommand(GetString(args, PlatformSimVerbArg), simArgs, out var buildError);
        if (command is null) return Error(buildError ?? "命令を組み立てられませんでした。");

        // ── 2. 送って 1 行の応答を待つ ──
        var reply = await host.SendIpcAwaitReplyAsync(command, PlatformSimIpc.ReplyPrefixes, PlatformSimTimeoutMs);
        _log($"[AI ツール] {command} → {reply.Line ?? reply.Status.ToString()}");

        if (!reply.HasLine || !PlatformSimIpc.TryParseReply(reply.Line!, out var parsed))
            return Json(new { ok = false, sent = command, error = reply.DescribeFailure(PlatformSimTimeoutMs) });

        // ── 3. 整形（OK = 返答の JSON をそのまま埋め込む / ERROR = 理由を言い換える）──
        if (!parsed.Ok)
        {
            return Json(new
            {
                ok     = false,
                sent   = command,
                reason = parsed.Payload,
                error  = PlatformSimIpc.DescribeReason(parsed.Payload),
                state  = host.RuntimeState.ToString(),
            });
        }
        return Json(new { ok = true, sent = command, reply = RawJson(parsed.Payload) });
    }

    // ── 小道具 ───────────────────────────────────────────────────

    /// <summary>
    /// 文字列の配列の引数を読む（無ければ空。数値・真偽値の要素は JSON の表記のまま文字列にする）。
    /// </summary>
    /// <param name="args">ツール引数。</param>
    /// <param name="name">引数名。</param>
    /// <param name="values">読めた値。</param>
    /// <param name="error">読めなかった理由（読めたら null）。</param>
    /// <returns>読めたら true。</returns>
    private static bool TryReadStringArray(JsonElement args, string name, out List<string> values, out string? error)
    {
        values = new List<string>();
        error  = null;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        if (el.ValueKind != JsonValueKind.Array)
        {
            error = $"'{name}' は文字列の配列で指定してください（例 [\"post_notifications\",\"denied\"]）。";
            return false;
        }

        foreach (var item in el.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    values.Add(item.GetString() ?? "");
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    values.Add(item.GetRawText());
                    break;
                default:
                    error = $"'{name}' の要素は文字列で指定してください（オブジェクト・配列・null は使えません）。";
                    return false;
            }
        }
        return true;
    }
}
