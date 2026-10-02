// ============================================================
//  PlatformSimIpc.cs — SEED.Platform のデスクトップの模擬の操作（PLATFORM_SIM）の組み立てと応答の解釈
//
//  【ワイヤ形式】（正典はランタイム: runtime/src/engine/core/app_base/ipc.rs の parse_platform_sim と
//               app/platform_sim_ops.rs の動詞の表。docs/scripting_api.md「デスクトップの模擬の操作」）
//    エディタ → ランタイム: PLATFORM_SIM:{verb},{arg1},{arg2}…
//    ランタイム → エディタ: PLATFORM_SIM_OK:{模擬の命令の返答の JSON} / PLATFORM_SIM_ERROR:{reason}
//
//  【ここで確かめること・確かめないこと】
//  ランタイムは書式の壊れた行（動詞が空・空白を含む）を**黙って捨てる**（応答が来ない＝待ちが時間切れになる）。
//  そこで「1 行の命令として壊れないか」だけをここで確かめ、送る前に理由つきで断る。
//  動詞の種類・引数の数・値の名前（post_notifications / denied …）は確かめない
//  （正典はランタイムの表。ランタイムが unknown_verb / bad_arguments / invalid_argument で答える）。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/AiSafetyTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.AI.Tools.RuntimeIpc;

/// <summary>
/// PLATFORM_SIM の応答 1 行を読んだ結果。
/// </summary>
/// <param name="Ok">受け付けられたか（PLATFORM_SIM_OK）。</param>
/// <param name="Payload">OK なら模擬の命令の返答の JSON、ERROR なら理由の名前。</param>
public readonly record struct PlatformSimReply(bool Ok, string Payload);

/// <summary>
/// PLATFORM_SIM の命令の組み立てと応答の解釈。状態を持たない。
/// </summary>
public static class PlatformSimIpc
{
    // ── ワイヤの頭（ランタイムの ipc.rs / platform_sim_ops.rs と一致させる）──────

    /// <summary>命令の頭。</summary>
    public const string CommandPrefix = "PLATFORM_SIM:";

    /// <summary>受け付けたときの応答の頭（後ろに返答の JSON）。</summary>
    public const string OkPrefix = "PLATFORM_SIM_OK:";

    /// <summary>断ったときの応答の頭（後ろに理由の名前）。</summary>
    public const string ErrorPrefix = "PLATFORM_SIM_ERROR:";

    /// <summary>待ち合わせで拾う応答の頭（どちらか一方が必ず 1 行届く）。</summary>
    public static IReadOnlyList<string> ReplyPrefixes { get; } = [OkPrefix, ErrorPrefix];

    // ── ランタイムが返す理由の名前（platform_sim_ops.rs・wire の ERROR_*）──────────

    /// <summary>Play 中でない。</summary>
    public const string ReasonNotPlaying = "not_playing";

    /// <summary>基盤が模擬でない（Android の端末など）。</summary>
    public const string ReasonNotSimulated = "not_simulated";

    /// <summary>表に無い動詞。</summary>
    public const string ReasonUnknownVerb = "unknown_verb";

    /// <summary>引数の数が表と違う。</summary>
    public const string ReasonBadArguments = "bad_arguments";

    /// <summary>種類・状態の名前の誤り（v2 の予約の種類を含む）。</summary>
    public const string ReasonInvalidArgument = "invalid_argument";

    // ── 書式 ────────────────────────────────────────────────

    /// <summary>動詞と引数・引数どうしの区切り（値に含めてはいけない）。</summary>
    private const char Separator = ',';

    /// <summary>1 行の命令に載せられない文字（IPC は 1 行 = 1 通）。</summary>
    private static readonly char[] LineBreaks = ['\r', '\n'];

    // ============================================================
    //  組み立て（エディタ → ランタイム）
    // ============================================================

    /// <summary>
    /// 命令の 1 行を組み立てる。1 行の命令として壊れる入力なら null と理由を返す。
    /// </summary>
    /// <param name="verb">動詞（permission / permission_answer / lifecycle など）。</param>
    /// <param name="args">引数の並び（無ければ空）。</param>
    /// <param name="error">組み立てられなかった理由（組み立てられたら null）。</param>
    /// <returns>ランタイムへ送る 1 行。組み立てられなければ null。</returns>
    public static string? TryBuildCommand(string? verb, IReadOnlyList<string>? args, out string? error)
    {
        // ── 動詞: 空・空白入り・区切り入りは、ランタイムが捨てる（応答も来ない）ので先に断る ──
        var trimmedVerb = verb?.Trim() ?? "";
        if (trimmedVerb.Length == 0)
        {
            error = "'verb' が必要です（permission / permission_answer / lifecycle など）。";
            return null;
        }
        if (trimmedVerb.Any(char.IsWhiteSpace) || trimmedVerb.Contains(Separator))
        {
            error = $"'verb' に空白・カンマは使えません: '{trimmedVerb}'";
            return null;
        }

        // ── 引数: 区切り・改行が入ると数がずれる・行が割れる ──
        var list = args ?? Array.Empty<string>();
        for (int i = 0; i < list.Count; i++)
        {
            var value = list[i] ?? "";
            if (value.Contains(Separator) || value.IndexOfAny(LineBreaks) >= 0)
            {
                error = $"'args[{i}]' にカンマ・改行は使えません: '{value}'";
                return null;
            }
        }

        error = null;
        var parts = new List<string>(list.Count + 1) { trimmedVerb };
        parts.AddRange(list.Select(a => (a ?? "").Trim()));
        return CommandPrefix + string.Join(Separator, parts);
    }

    // ============================================================
    //  解釈（ランタイム → エディタ）
    // ============================================================

    /// <summary>
    /// 応答の 1 行を読む。
    /// </summary>
    /// <param name="line">届いた行。</param>
    /// <param name="reply">読めた結果。</param>
    /// <returns>PLATFORM_SIM の応答として読めたら true。</returns>
    public static bool TryParseReply(string line, out PlatformSimReply reply)
    {
        if (line.StartsWith(OkPrefix, StringComparison.Ordinal))
        {
            reply = new PlatformSimReply(true, line[OkPrefix.Length..]);
            return true;
        }
        if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            reply = new PlatformSimReply(false, line[ErrorPrefix.Length..]);
            return true;
        }
        reply = default;
        return false;
    }

    /// <summary>
    /// ランタイムの理由の名前を、AI がそのまま読んで対処できる日本語にする。
    /// 知らない理由は名前を添えて返す（情報を落とさない）。
    /// </summary>
    /// <param name="reason">理由の名前。</param>
    /// <returns>説明。</returns>
    public static string DescribeReason(string reason) => reason switch
    {
        ReasonNotPlaying      => "Play 中ではないため模擬を変えられません（Edit で変えても Play の開始で起動時の設定へ戻るため、"
                               + "ランタイムが断ります）。seed_play(action:\"play\") の後で呼んでください。",
        ReasonNotSimulated    => "基盤が模擬ではありません（Android の端末には模擬だけの命令がありません）。",
        ReasonUnknownVerb     => "知らない動詞です（使えるのは permission / permission_answer / lifecycle。"
                               + "表の正典は runtime/src/engine/core/app_base/app/platform_sim_ops.rs）。",
        ReasonBadArguments    => "引数の数が動詞の表と違います（permission と permission_answer は 2 つ、lifecycle は 1 つ）。",
        ReasonInvalidArgument => "種類・状態の名前が正しくありません（種類: post_notifications / exact_alarm / full_screen_intent、"
                               + "状態: granted / denied / denied_permanently / needs_settings / not_applicable、"
                               + "答えはさらに none、前面と背面: resumed / paused）。",
        _                     => $"ランタイムが断りました（理由: {reason}）。",
    };
}
