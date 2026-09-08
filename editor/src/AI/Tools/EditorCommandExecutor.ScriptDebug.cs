// ============================================================
//  EditorCommandExecutor.ScriptDebug.cs — デバッグコマンド（script_debug）
//
//  外部エージェント（MCP）が「ゲームの途中の状態を人の操作なしで作る」ための
//  コマンド。EditorCommandExecutor の partial 実装。
//
//  【役割】
//   ・ツール引数（JSON）を検証し、ランタイムの `SCRIPT_DEBUG:{name},{arg}` 1 行へ組み立てる。
//   ・送信後、ランタイムの 1 行応答（SCRIPT_DEBUG_OK / SCRIPT_DEBUG_ERROR:{reason}）を
//     待ち、機械可読な JSON へ整形して返す。
//   ・ランタイム側の実装は runtime/src/engine/core/app_base/app/script_debug_ops.rs と
//     runtime/src/engine/core/scripting/debug_command.rs。
//     コマンド／応答の仕様は docs/editor_mcp.md 10 章が正典。
//
//  【なぜ入力注入（game_input_*）と別に要るのか】
//   入力注入は「人の操作を真似る」ので、ゲームの奥まった状態
//   （例: 釣り上げ演出）まで辿り着くのに何十秒もの手順が要り、
//   途中で失敗すると原因の切り分けもできない。デバッグコマンドは
//   ゲーム側のスクリプトが用意した入口を直接叩くので、
//   検証したい場面だけを 1 手で再現できる。
//
//  【安全性】
//   「変更系」コマンドであり、AiOperationPolicy の読み取り専用インスタンスでは
//   拒否される（ReadOnlyCommands に入れていないため既定で変更系）。
//   さらにランタイム側が Play 中以外を一律で拒否する。
// ============================================================

using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>デバッグコマンドの IPC 接頭辞。</summary>
    private const string ScriptDebugPrefix = "SCRIPT_DEBUG:";

    /// <summary>IPC 引数の区切り文字（name と arg を分ける最初の 1 個）。</summary>
    private const string ScriptDebugArgSeparator = ",";

    /// <summary>script_debug: コマンド名。</summary>
    private const string ScriptDebugNameArg = "name";

    /// <summary>script_debug: コマンド引数（省略可）。</summary>
    private const string ScriptDebugArgArg = "arg";

    /// <summary>
    /// 応答待ちのタイムアウト（ミリ秒）。
    /// ランタイムは IPC を受けたフレーム内で即応答するので、
    /// 入力注入の単発コマンドと同じ余裕があれば十分。
    /// </summary>
    private const int ScriptDebugTimeoutMs = 5_000;

    /// <summary>ランタイムが返す理由コード: Play 中でない。</summary>
    private const string ScriptDebugReasonNotPlaying = "not_playing";

    // ── ディスパッチ ─────────────────────────────────────────────

    /// <summary>
    /// デバッグコマンド系を実行する。
    /// 扱わないコマンド名の場合は null を返し、呼び出し元（ExecuteVisualToolAsync）が
    /// 他のグループへ委ねる。
    /// </summary>
    /// <param name="command">コマンド名。</param>
    /// <param name="args">ツール引数。</param>
    private Task<string>? ExecuteScriptDebugTool(string command, JsonElement args)
        => command switch
        {
            "script_debug" => ExecuteScriptDebugAsync(args),
            _              => null,
        };

    // ── コマンド実装 ─────────────────────────────────────────────

    /// <summary>
    /// 実行中のスクリプトへ名前付きのデバッグ指示を送る（SCRIPT_DEBUG）。
    ///
    /// 名前に対応するハンドラは、ゲーム側の C# が
    /// <c>SEED.Debug.OnCommand(name, handler)</c> で登録しておく。
    /// 未登録の名前を送っても IPC としては受理される（ランタイムはコマンドの
    /// 意味を知らないため）。その場合はスクリプト側が警告ログを出すので、
    /// <c>seed_log</c> で確認できる。
    /// </summary>
    private Task<string> ExecuteScriptDebugAsync(JsonElement args)
    {
        var name = GetString(args, ScriptDebugNameArg);
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(Error(
                "'name' が必要です（ゲーム側が SEED.Debug.OnCommand で登録したコマンド名）。"));

        // IPC は「1 行 1 コマンド・最初のカンマで name と arg を割る」ので、
        // name 側に区切りを壊す文字が入っていたら入口で弾く。
        if (ContainsIpcBreakingChars(name))
            return Task.FromResult(Error("'name' にカンマ・改行は使えません。"));

        // arg は省略可（空文字として送る）。arg 側のカンマは分割に使われないので許す。
        var arg = GetString(args, ScriptDebugArgArg) ?? string.Empty;
        if (arg.IndexOf('\n') >= 0 || arg.IndexOf('\r') >= 0)
            return Task.FromResult(Error("'arg' に改行は使えません（IPC は 1 行 1 コマンド）。"));

        var command = ScriptDebugPrefix + name.Trim() + ScriptDebugArgSeparator + arg;
        return SendScriptDebugAsync(command);
    }

    // ── 送信と応答の整形 ─────────────────────────────────────────

    /// <summary>
    /// 組み立てた <c>SCRIPT_DEBUG:</c> 行を送り、1 行応答を待って JSON へ整形する。
    ///
    /// 待ち合わせは入力注入と同じ経路（<c>InjectGameInputAsync</c>）を使う。
    /// ランタイムの応答はどちらも「1 行・即時」で、待ち合わせの仕組みを
    /// 2 つ持つ理由が無いため意図的に相乗りさせている。
    /// </summary>
    private async Task<string> SendScriptDebugAsync(string command)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var reply = await host.InjectGameInputAsync(command, ScriptDebugTimeoutMs, waitSequenceDone: false);
        _log($"[AI ツール] {command} → {reply ?? "(タイムアウト)"}");

        if (reply is null)
        {
            return Json(new
            {
                ok    = false,
                sent  = command,
                reply = (string?)null,
                error = $"ランタイムから応答が {ScriptDebugTimeoutMs} ms 以内に返りませんでした"
                      + "（ランタイム未接続、または描画ループが止まっている可能性）。",
            });
        }

        // 入力注入の擬似応答（未接続）もここへ来るので、両方の拒否接頭辞を見る。
        if (reply.StartsWith(Runtime.RuntimeManager.SCRIPT_DEBUG_ERROR_PREFIX, StringComparison.Ordinal))
        {
            var reason = reply[Runtime.RuntimeManager.SCRIPT_DEBUG_ERROR_PREFIX.Length..];
            return Json(new { ok = false, sent = command, reply, error = DescribeScriptDebugError(reason) });
        }
        if (reply.StartsWith(Runtime.RuntimeManager.INPUT_ERROR_PREFIX, StringComparison.Ordinal))
        {
            var reason = reply[Runtime.RuntimeManager.INPUT_ERROR_PREFIX.Length..];
            return Json(new { ok = false, sent = command, reply, error = DescribeScriptDebugError(reason) });
        }

        return Json(new { ok = true, sent = command, reply });
    }

    /// <summary>
    /// ランタイムの拒否理由コードを、そのまま読んで対処できる日本語へ言い換える。
    /// 未知の理由コードは原文を添えて返す（情報を落とさないため）。
    /// </summary>
    private static string DescribeScriptDebugError(string reason) => reason switch
    {
        ScriptDebugReasonNotPlaying =>
            "Play 中ではないためデバッグコマンドを送れません（Edit 中はスクリプトが走っていません）。"
          + "seed_play(action:\"play\") で再生してから呼んでください。",

        GameInputReasonNotConnected =>
            "ランタイムへ接続されていません（Play していない、またはランタイムが落ちています）。",

        _ => $"ランタイムがコマンドを拒否しました（理由: {reason}）。",
    };
}
