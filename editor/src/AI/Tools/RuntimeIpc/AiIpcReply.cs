// ============================================================
//  AiIpcReply.cs — AI ツールが「IPC を 1 行送って応答の 1 行を待つ」ときの結果
//
//  【役割】
//  IEditorAiHost の待ち合わせ（SendIpcAwaitReplyAsync・画面プレビューの差し込み／消去）が返す値。
//  「応答が来た」「待ち切れなかった」「ランタイムに繋がっていない」「送る前に断った」を
//  呼び出し側（EditorCommandExecutor の各 partial）が区別し、AI へ理由をそのまま返せるようにする。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/AiSafetyTests）がリンクして試すので WPF 型を使わない。
// ============================================================

namespace SEEDEditor.AI.Tools.RuntimeIpc;

/// <summary>
/// 待ち合わせの結末の種類。
/// </summary>
public enum AiIpcReplyStatus
{
    /// <summary>目的の応答行が届いた（<see cref="AiIpcReply.Line"/> に入っている）。</summary>
    Replied,

    /// <summary>制限時間内に応答が届かなかった。</summary>
    TimedOut,

    /// <summary>ランタイムへ繋がっていない（送っても誰も答えないので送らなかった）。</summary>
    NotConnected,

    /// <summary>送る前にエディタ側で断った（Edit でない・閲覧専用・宛先を見失った など）。</summary>
    Refused,
}

/// <summary>
/// 「IPC を送って応答を待つ」の結果。
/// </summary>
/// <param name="Status">結末の種類。</param>
/// <param name="Line">届いた応答行（<see cref="AiIpcReplyStatus.Replied"/> のときだけ）。</param>
/// <param name="Reason">断った理由（<see cref="AiIpcReplyStatus.Refused"/> のときだけ。利用者向けの文）。</param>
public readonly record struct AiIpcReply(AiIpcReplyStatus Status, string? Line, string? Reason)
{
    /// <summary>応答行が届いた結果を作る。</summary>
    /// <param name="line">届いた行。</param>
    /// <returns>結果。</returns>
    public static AiIpcReply FromLine(string line) => new(AiIpcReplyStatus.Replied, line, null);

    /// <summary>待ち切れなかった結果を作る。</summary>
    /// <returns>結果。</returns>
    public static AiIpcReply Timeout() => new(AiIpcReplyStatus.TimedOut, null, null);

    /// <summary>ランタイムへ繋がっていない結果を作る。</summary>
    /// <returns>結果。</returns>
    public static AiIpcReply Disconnected() => new(AiIpcReplyStatus.NotConnected, null, null);

    /// <summary>送る前に断った結果を作る。</summary>
    /// <param name="reason">理由（利用者向けの文）。</param>
    /// <returns>結果。</returns>
    public static AiIpcReply RefusedBecause(string reason) => new(AiIpcReplyStatus.Refused, null, reason);

    /// <summary>応答行が届いたか（<see cref="Line"/> を読んでよいか）。</summary>
    public bool HasLine => Status == AiIpcReplyStatus.Replied && Line is not null;

    /// <summary>
    /// 応答行が届かなかった理由を、AI がそのまま読んで次の手を選べる日本語にする。
    /// 届いているときは null。
    /// </summary>
    /// <param name="timeoutMs">待った時間（ミリ秒。文言に入れる）。</param>
    /// <returns>理由。応答が届いていれば null。</returns>
    public string? DescribeFailure(int timeoutMs) => Status switch
    {
        AiIpcReplyStatus.Replied      => null,
        AiIpcReplyStatus.TimedOut     => $"ランタイムから応答が {timeoutMs} ms 以内に返りませんでした"
                                       + "（描画ループが止まっている・Pause 中・命令の書式が受け付けられずに捨てられた、のいずれか）。"
                                       + "seed_log で [STDERR] を確認してください。",
        AiIpcReplyStatus.NotConnected => "ランタイムへ接続されていません（起動中・再起動中、またはランタイムが落ちています）。"
                                       + "seed_state の runtime_connected を確認してください。",
        AiIpcReplyStatus.Refused      => Reason ?? "エディタが命令を断りました。",
        _                             => $"想定外の結末です: {Status}",
    };
}
