// ============================================================
//  LoreConnectionDiagnosis.cs — 失敗が「サーバに繋がらない」せいかの判定
//
//  【役割】
//  履歴やロックのようにサーバ必須の操作が失敗したとき、
//  「接続できないだけ」と「本当の失敗」を切り分ける。
//  前者なら UI は「オフラインでは取得できません」と案内でき、
//  利用者は自分の操作ミスだと誤解しない。
//
//  【文字列一致に頼ることの危うさ（承知の上での選択）】
//  Lore のメッセージ文言は pre-1.0 なので変わり得る。変わった場合、
//  この判定は RequiresConnection ではなく通常の失敗を返す。
//  誤って「接続の問題」と言い切って本当の失敗を隠すより安全側。
//  マーカーは下の配列 1 か所にまとめてあるので、文言が変わったらここだけ直す。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using SEEDEditor.VersionControl.Lore.Backend;

namespace SEEDEditor.VersionControl.Lore;

/// <summary>
/// サーバへ到達できないことを示す失敗かを判定する純粋関数。
/// </summary>
public static class LoreConnectionDiagnosis
{
    /// <summary>
    /// 「サーバへ到達できない」ことを示す Lore のメッセージ断片。
    /// 部分一致・大文字小文字を区別しない比較で使う。
    /// </summary>
    public static readonly string[] CONNECTION_MARKERS =
    {
        "offline",
        "not connected",
        "connection",
        "connect",
        "unreachable",
        "unavailable",
        "transport",
        "timed out",
        "timeout",
        "no remote",
        "remote url",
    };

    /// <summary>
    /// 失敗が「サーバへ到達できない」ことによるものか判定する。
    /// </summary>
    /// <param name="result">呼び出しの結末。</param>
    /// <returns>接続できないことが原因と見られるなら真。</returns>
    public static bool IsConnectionFailure(LoreCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // 成功・中断は接続の問題ではない。
        if (result.Succeeded || result.WasCanceled) return false;

        foreach (var marker in CONNECTION_MARKERS)
        {
            if (result.MessagesContain(marker)) return true;
        }
        return false;
    }
}
