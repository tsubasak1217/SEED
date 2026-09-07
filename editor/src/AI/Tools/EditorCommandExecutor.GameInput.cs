// ============================================================
//  EditorCommandExecutor.GameInput.cs — ゲーム入力の注入コマンド（game_input_*）
//
//  外部エージェント（MCP）が「実際にゲームを遊んで」結果をスクリーンショットで
//  確認できるようにするためのコマンド群。EditorCommandExecutor の partial 実装。
//
//  【役割】
//   ・ツール引数（JSON）を検証し、ランタイムの INPUT_* IPC 文字列 1 行へ組み立てる。
//   ・送信後、ランタイムの 1 行応答（INPUT_OK / INPUT_ERROR:{reason} /
//     INPUT_SEQUENCE_DONE）を待ち、機械可読な JSON へ整形して返す。
//   ・ランタイム側の実装は runtime/src/engine/core/input/inject/ 一式。
//     コマンド／応答の仕様は docs/editor_mcp.md 9 章が正典。
//
//  【対応コマンド】
//    game_input_key         : INPUT_KEY:{key},{down|up}
//    game_input_mouse       : INPUT_MOUSE_BUTTON / INPUT_MOUSE_MOVE /
//                             INPUT_MOUSE_POS / INPUT_SCROLL のいずれか 1 件
//    game_input_sequence    : INPUT_SEQUENCE:{json}（既定で INPUT_SEQUENCE_DONE まで待つ）
//    game_input_release_all : INPUT_RELEASE_ALL
//
//  【安全性】
//   これらはすべて「変更系」コマンドであり、AiOperationPolicy の読み取り専用
//   インスタンスでは拒否される（利用者が開いているエディタのゲームを
//   外部エージェントが勝手に操作しないようにするため）。
// ============================================================

using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;

namespace SEEDEditor.AI.Tools;

public partial class EditorCommandExecutor
{
    // ── 定数: IPC コマンド名 ─────────────────────────────────────

    /// <summary>キー押下 / 解放の IPC 接頭辞。</summary>
    private const string GameInputKeyPrefix = "INPUT_KEY:";

    /// <summary>マウスボタン押下 / 解放の IPC 接頭辞。</summary>
    private const string GameInputMouseButtonPrefix = "INPUT_MOUSE_BUTTON:";

    /// <summary>マウス相対移動の IPC 接頭辞。</summary>
    private const string GameInputMouseMovePrefix = "INPUT_MOUSE_MOVE:";

    /// <summary>マウス絶対座標の IPC 接頭辞。</summary>
    private const string GameInputMousePosPrefix = "INPUT_MOUSE_POS:";

    /// <summary>ホイールの IPC 接頭辞。</summary>
    private const string GameInputScrollPrefix = "INPUT_SCROLL:";

    /// <summary>入力シーケンス再生の IPC 接頭辞。</summary>
    private const string GameInputSequencePrefix = "INPUT_SEQUENCE:";

    /// <summary>注入中の押下をすべて解放する IPC コマンド（引数なし）。</summary>
    private const string GameInputReleaseAllCommand = "INPUT_RELEASE_ALL";

    /// <summary>IPC 引数の区切り文字。</summary>
    private const string GameInputArgSeparator = ",";

    /// <summary>押下を表す状態語。</summary>
    private const string GameInputStateDown = "down";

    /// <summary>解放を表す状態語。</summary>
    private const string GameInputStateUp = "up";

    // ── 定数: 引数名 ─────────────────────────────────────────────

    /// <summary>game_input_key: キー名（InputMap と同じ表記）。</summary>
    private const string GameInputKeyArg = "key";

    /// <summary>game_input_key / game_input_mouse: 押す(true) / 離す(false)。</summary>
    private const string GameInputDownArg = "down";

    /// <summary>game_input_mouse: ボタン名（left / right / middle）。</summary>
    private const string GameInputButtonArg = "button";

    /// <summary>game_input_mouse: 相対移動量 X。</summary>
    private const string GameInputDxArg = "dx";

    /// <summary>game_input_mouse: 相対移動量 Y。</summary>
    private const string GameInputDyArg = "dy";

    /// <summary>game_input_mouse: 絶対座標 X。</summary>
    private const string GameInputXArg = "x";

    /// <summary>game_input_mouse: 絶対座標 Y。</summary>
    private const string GameInputYArg = "y";

    /// <summary>game_input_mouse: ホイール量（ライン数）。</summary>
    private const string GameInputScrollArg = "scroll";

    /// <summary>game_input_sequence: イベント配列。</summary>
    private const string GameInputEventsArg = "events";

    /// <summary>game_input_sequence: 完了まで待つか。</summary>
    private const string GameInputWaitArg = "wait";

    /// <summary>game_input_sequence: 各イベントの発火時刻（秒）。</summary>
    private const string GameInputEventTimeKey = "t";

    // ── 定数: タイムアウト ───────────────────────────────────────

    /// <summary>
    /// 単発コマンド（key / mouse / release_all）の応答待ちタイムアウト（ミリ秒）。
    /// ランタイムは IPC を受けたフレーム内で即応答するため、
    /// フレーム落ちを見込んだ余裕があれば十分。
    /// </summary>
    private const int GameInputSingleTimeoutMs = 5_000;

    /// <summary>
    /// シーケンス完了待ちに、シーケンス長（最大 t 秒）へ上乗せする余裕（ミリ秒）。
    /// 最後のイベントは t の直後のフレームで発火するため、低フレームレートを見込む。
    /// </summary>
    private const int GameInputSequenceMarginMs = 5_000;

    /// <summary>
    /// シーケンスの最大 t として認める上限（秒）。
    /// これを超える待ちは MCP / HTTP のタイムアウトに掛かるため、
    /// 「wait:false で投げて別途 seed_screenshot で確認する」運用へ誘導する。
    /// </summary>
    private const double GameInputMaxSequenceSeconds = 60.0;

    // ── 定数: 応答理由の言い換え ─────────────────────────────────

    /// <summary>ランタイムが返す理由コード: Play 中でない。</summary>
    private const string GameInputReasonNotPlaying = "not_playing";

    /// <summary>ランタイムが返す理由コード: 別シーケンス再生中。</summary>
    private const string GameInputReasonSequenceBusy = "sequence_busy";

    /// <summary>エディタ側で作る擬似理由コード: ランタイム未接続。</summary>
    private const string GameInputReasonNotConnected = "runtime_not_connected";

    // ── ディスパッチ ─────────────────────────────────────────────

    /// <summary>
    /// ゲーム入力注入系コマンドを実行する。
    /// 扱わないコマンド名の場合は null を返し、呼び出し元（ExecuteVisualToolAsync）が
    /// 他のグループへ委ねる。
    /// </summary>
    /// <param name="command">コマンド名（game_input_*）。</param>
    /// <param name="args">ツール引数。</param>
    private Task<string>? ExecuteGameInputTool(string command, JsonElement args)
        => command switch
        {
            "game_input_key"         => ExecuteGameInputKeyAsync(args),
            "game_input_mouse"       => ExecuteGameInputMouseAsync(args),
            "game_input_sequence"    => ExecuteGameInputSequenceAsync(args),
            "game_input_release_all" => ExecuteGameInputReleaseAllAsync(),
            _                        => null,
        };

    // ── コマンド実装 ─────────────────────────────────────────────

    /// <summary>
    /// キーを押す / 離す（INPUT_KEY）。
    /// 押下は明示の up / release_all / Play 停止まで保持される。
    /// </summary>
    private Task<string> ExecuteGameInputKeyAsync(JsonElement args)
    {
        var key = GetString(args, GameInputKeyArg);
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(Error(
                "'key' が必要です（InputMap と同じキー名。例: \"W\" / \"Space\" / \"LeftShift\"）。"));

        // IPC は「1 行 1 コマンド・カンマ区切り」なので、区切りを壊す文字は入口で弾く。
        if (ContainsIpcBreakingChars(key))
            return Task.FromResult(Error("'key' にカンマ・改行は使えません。"));

        var down = GetBoolOrNull(args, GameInputDownArg);
        if (down is null)
            return Task.FromResult(Error("'down' が必要です（true = 押す / false = 離す）。"));

        var command = GameInputKeyPrefix + key + GameInputArgSeparator + StateWord(down.Value);
        return SendGameInputAsync(command, GameInputSingleTimeoutMs, waitSequenceDone: false);
    }

    /// <summary>
    /// マウス操作を 1 件だけ実行する。
    ///
    /// <para>
    /// 指定された引数の組み合わせで送信先コマンドを決める。
    ///   button(+down) → INPUT_MOUSE_BUTTON / dx,dy → INPUT_MOUSE_MOVE /
    ///   x,y → INPUT_MOUSE_POS / scroll → INPUT_SCROLL
    /// 2 種類以上を同時に書くと「どれを先に撃つか」が曖昧になるため拒否する
    /// （同時に行いたい場合は game_input_sequence を使う）。
    /// </para>
    /// </summary>
    private Task<string> ExecuteGameInputMouseAsync(JsonElement args)
    {
        var hasButton = !string.IsNullOrWhiteSpace(GetString(args, GameInputButtonArg));
        var hasMove   = HasProperty(args, GameInputDxArg) || HasProperty(args, GameInputDyArg);
        var hasPos    = HasProperty(args, GameInputXArg)  || HasProperty(args, GameInputYArg);
        var hasScroll = HasProperty(args, GameInputScrollArg);

        var kinds = (hasButton ? 1 : 0) + (hasMove ? 1 : 0) + (hasPos ? 1 : 0) + (hasScroll ? 1 : 0);
        if (kinds == 0)
            return Task.FromResult(Error(
                "操作の指定がありません。button(+down) / dx,dy / x,y / scroll の"
              + "いずれか 1 種類を指定してください。"));
        if (kinds > 1)
            return Task.FromResult(Error(
                "1 回の呼び出しで指定できる操作は 1 種類だけです"
              + "（button(+down) / dx,dy / x,y / scroll）。"
              + "同時に行いたい場合は game_input_sequence を使ってください。"));

        string command;

        if (hasButton)
        {
            var button = GetString(args, GameInputButtonArg)!;
            if (ContainsIpcBreakingChars(button))
                return Task.FromResult(Error("'button' にカンマ・改行は使えません。"));

            var down = GetBoolOrNull(args, GameInputDownArg);
            if (down is null)
                return Task.FromResult(Error(
                    "'button' を指定する場合は 'down' も必要です（true = 押す / false = 離す）。"));

            command = GameInputMouseButtonPrefix + button + GameInputArgSeparator + StateWord(down.Value);
        }
        else if (hasMove)
        {
            // 片方だけの指定は「もう片方は 0」とみなす（横だけ振る用途が多いため）。
            var dx = GetDouble(args, GameInputDxArg) ?? 0.0;
            var dy = GetDouble(args, GameInputDyArg) ?? 0.0;
            command = GameInputMouseMovePrefix + Num(dx) + GameInputArgSeparator + Num(dy);
        }
        else if (hasPos)
        {
            // 絶対座標は片方だけでは意味を成さないので両方必須にする。
            var x = GetDouble(args, GameInputXArg);
            var y = GetDouble(args, GameInputYArg);
            if (x is null || y is null)
                return Task.FromResult(Error("絶対座標の指定には 'x' と 'y' の両方が必要です。"));
            command = GameInputMousePosPrefix + Num(x.Value) + GameInputArgSeparator + Num(y.Value);
        }
        else
        {
            var scroll = GetDouble(args, GameInputScrollArg);
            if (scroll is null)
                return Task.FromResult(Error("'scroll' は数値で指定してください（ライン数。負値で下方向）。"));
            command = GameInputScrollPrefix + Num(scroll.Value);
        }

        return SendGameInputAsync(command, GameInputSingleTimeoutMs, waitSequenceDone: false);
    }

    /// <summary>
    /// 時間軸付きの操作列をまとめて再生する（INPUT_SEQUENCE）。
    ///
    /// <para>
    /// events の書式は docs/editor_mcp.md 9.3 節。中身の検証はランタイム側の
    /// パーサ（純粋関数・単体テスト付き）が正典なので、ここでは
    /// 「1 行の IPC に載せられる形か」だけを見て素通しする。
    /// wait:true（既定）では INPUT_SEQUENCE_DONE まで待ってから返す。
    /// </para>
    /// </summary>
    private Task<string> ExecuteGameInputSequenceAsync(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(GameInputEventsArg, out var events))
            return Task.FromResult(Error("'events' が必要です（イベントの JSON 配列）。"));

        // 文字列で渡された JSON 配列も受け付ける
        // （クライアントによっては配列を文字列化して送ってくるため）。
        JsonDocument? parsedFromString = null;
        if (events.ValueKind == JsonValueKind.String)
        {
            var text = events.GetString() ?? "";
            try
            {
                parsedFromString = JsonDocument.Parse(text);
                events           = parsedFromString.RootElement;
            }
            catch (JsonException ex)
            {
                return Task.FromResult(Error($"'events' を JSON として解釈できません: {ex.Message}"));
            }
        }

        try
        {
            if (events.ValueKind != JsonValueKind.Array)
                return Task.FromResult(Error("'events' は JSON 配列で指定してください。"));
            if (events.GetArrayLength() == 0)
                return Task.FromResult(Error("'events' が空です（1 件以上のイベントが必要）。"));

            // 最大 t からシーケンス長を見積もり、完了待ちのタイムアウトを決める。
            var maxSeconds = MaxEventTimeSeconds(events);
            if (maxSeconds > GameInputMaxSequenceSeconds)
                return Task.FromResult(Error(
                    $"シーケンスが長すぎます（最大 t = {Num(maxSeconds)} 秒 > "
                  + $"{Num(GameInputMaxSequenceSeconds)} 秒）。分割するか、"
                  + "wait:false で投げて seed_screenshot で結果を確認してください。"));

            // IPC は 1 行 1 コマンド。整形済み（改行入り）で渡されても壊れないよう、
            // ここで必ずコンパクトな 1 行 JSON へ直列化し直す。
            var json    = JsonSerializer.Serialize(events, GameInputJsonOptions);
            var command = GameInputSequencePrefix + json;

            var wait      = GetBoolOrNull(args, GameInputWaitArg) ?? true;
            var timeoutMs = wait
                ? (int)(maxSeconds * 1000.0) + GameInputSequenceMarginMs
                : GameInputSingleTimeoutMs;

            return SendGameInputAsync(command, timeoutMs, waitSequenceDone: wait);
        }
        finally
        {
            parsedFromString?.Dispose();
        }
    }

    /// <summary>注入中の押下をすべて解放する（INPUT_RELEASE_ALL）。操作後の後始末に使う。</summary>
    private Task<string> ExecuteGameInputReleaseAllAsync()
        => SendGameInputAsync(GameInputReleaseAllCommand, GameInputSingleTimeoutMs,
                              waitSequenceDone: false);

    // ── 送信と応答整形 ───────────────────────────────────────────

    /// <summary>
    /// 組み立てた INPUT_* コマンドを送り、応答を JSON へ整形して返す。
    /// 成功: <c>{"ok":true,"sent":...,"reply":...}</c>
    /// 失敗: <c>{"ok":false,"sent":...,"reply":...,"error":"..."}</c>
    /// </summary>
    /// <param name="command">送信する IPC 文字列。</param>
    /// <param name="timeoutMs">応答待ちのタイムアウト（ミリ秒）。</param>
    /// <param name="waitSequenceDone">INPUT_SEQUENCE_DONE まで待つか。</param>
    private async Task<string> SendGameInputAsync(string command, int timeoutMs, bool waitSequenceDone)
    {
        var host = Host;
        if (host is null) return Error("エディタ本体へ接続されていません（host 未設定）。");

        var reply = await host.InjectGameInputAsync(command, timeoutMs, waitSequenceDone);
        _log($"[AI ツール] {command} → {reply ?? "(タイムアウト)"}");

        if (reply is null)
        {
            return Json(new
            {
                ok    = false,
                sent  = command,
                reply = (string?)null,
                error = $"ランタイムから応答が {timeoutMs} ms 以内に返りませんでした"
                      + "（ランタイム未接続、または描画ループが止まっている可能性）。",
            });
        }

        if (reply.StartsWith(Runtime.RuntimeManager.INPUT_ERROR_PREFIX, StringComparison.Ordinal))
        {
            var reason = reply[Runtime.RuntimeManager.INPUT_ERROR_PREFIX.Length..];
            return Json(new
            {
                ok    = false,
                sent  = command,
                reply,
                error = DescribeGameInputError(reason),
            });
        }

        return Json(new { ok = true, sent = command, reply });
    }

    /// <summary>
    /// ランタイムの拒否理由コードを、そのまま読んで対処できる日本語へ言い換える。
    /// 未知の理由コードは原文を添えて返す（情報を落とさないため）。
    /// </summary>
    private static string DescribeGameInputError(string reason) => reason switch
    {
        GameInputReasonNotPlaying =>
            "Play 中ではないため入力を注入できません（Edit 中は一律で拒否されます）。"
          + "seed_play(action:\"play\") で再生してから呼んでください。",

        GameInputReasonSequenceBusy =>
            "別の入力シーケンスを再生中です。完了（INPUT_SEQUENCE_DONE）を待ってから"
          + "送り直してください（wait:true で呼べば自動で待ちます）。",

        GameInputReasonNotConnected =>
            "ランタイムへ接続されていません（未起動 / 起動中）。seed_state で状態を確認してください。",

        _ => $"ランタイムが入力注入を拒否しました: {reason}"
           + "（理由コードの意味は docs/editor_mcp.md 9.2 節を参照）。",
    };

    // ── ヘルパー ─────────────────────────────────────────────────

    /// <summary>押下 / 解放を IPC の状態語へ変換する。</summary>
    private static string StateWord(bool down) => down ? GameInputStateDown : GameInputStateUp;

    /// <summary>数値を IPC 用の文字列にする（ロケール非依存）。</summary>
    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>IPC の「1 行・カンマ区切り」構造を壊す文字が含まれるか。</summary>
    private static bool ContainsIpcBreakingChars(string value)
        => value.Contains(',') || value.Contains('\n') || value.Contains('\r');

    /// <summary>引数にそのプロパティが（null 以外の値として）存在するか。</summary>
    private static bool HasProperty(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var el)
        && el.ValueKind != JsonValueKind.Null
        && el.ValueKind != JsonValueKind.Undefined;

    /// <summary>
    /// 真偽値プロパティを取り出す。未指定・解釈不能なら null。
    /// 文字列 "true"/"false" と数値 0/1 も受け付ける（クライアント差を吸収するため）。
    /// </summary>
    private static bool? GetBoolOrNull(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : null,
            JsonValueKind.Number => el.TryGetDouble(out var d) ? d != 0.0 : null,
            _                    => null,
        };
    }

    /// <summary>
    /// イベント配列から最大の t（秒）を求める。t を持たない要素は 0 とみなす。
    /// 数値でない t はランタイム側が bad_time で弾くので、ここでは無視する。
    /// </summary>
    private static double MaxEventTimeSeconds(JsonElement events)
    {
        var max = 0.0;
        foreach (var ev in events.EnumerateArray())
        {
            if (ev.ValueKind != JsonValueKind.Object) continue;
            if (!ev.TryGetProperty(GameInputEventTimeKey, out var t)) continue;
            if (t.ValueKind != JsonValueKind.Number) continue;
            if (!t.TryGetDouble(out var seconds)) continue;
            if (seconds > max) max = seconds;
        }
        return max;
    }

    /// <summary>
    /// INPUT_SEQUENCE の JSON 直列化設定。
    /// インデントなしなので必ず 1 行になり、文字列内の改行も \n へエスケープされるため、
    /// 「1 行 1 コマンド」の IPC 規約を満たせる。
    /// </summary>
    private static readonly JsonSerializerOptions GameInputJsonOptions = new()
    {
        WriteIndented = false,
    };
}
