// ============================================================
//  SeedInstance.cs — MCP サーバーが話す相手（エディタインスタンス）の束縛
//
//  【なぜ必要か】
//   かつて MCP サーバーはポート 7234 決め打ちで「そこに居る誰か」へコマンドを
//   投げていた。seed_launch がヘッドレスエディタを起動しても、そのエディタは
//   ポートを掴めず（利用者の対話エディタが先に掴んでいた）、以降のコマンドは
//   すべて**利用者のエディタ**に届いた。結果、開いていたシーンとは別シーンの
//   内容で .scene が上書きされ、最後に seed_shutdown でそのエディタが閉じられた。
//
//  【対策】
//   ・seed_launch は空きポートとランダムトークンを選び、それをエディタへ渡す。
//   ・起動後 GET /state で「pid とトークンが自分の起動したプロセスのものか」を確認する。
//   ・以後の全リクエストにトークンを載せ、エディタ側も一致を要求する。
//   ・自分が起動していない（束縛が無い）状態では、変更系ツールを一切実行しない。
//     観測系だけは既定ポートへ問い合わせることを許す（読み取り専用なので無害）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SeedMcpServer;

/// <summary>
/// この MCP サーバーが操作対象としているエディタインスタンス。
/// プロセス内に 1 つだけ持つ状態（stdio サーバーは 1 クライアント 1 プロセスのため）。
/// </summary>
internal static class SeedInstance
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>束縛が無いときに観測系ツールが問い合わせる既定ポート。</summary>
    public const int DEFAULT_PORT = 7234;

    /// <summary>リクエストにトークンを載せる HTTP ヘッダー名（エディタ側と一致させること）。</summary>
    public const string TOKEN_HEADER = "X-Seed-Token";

    /// <summary>束縛が無いのに変更系ツールを呼ばれたときのメッセージ。</summary>
    public const string DENY_NOT_BOUND = "seed_launch で起動したインスタンスのみ操作できます";

    /// <summary>
    /// 束縛が無くても実行してよいツール（観測のみ）。
    /// ここに足すときは「エディタの状態を一切変えないか」を必ず確認すること。
    /// </summary>
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "seed_query",
        "seed_state",
        "seed_hierarchy",
        "seed_log",
        "seed_screenshot",
        // 束縛そのものを作る／調べるツールは当然ここに含める。
        "seed_launch",
        "seed_attach",
        "seed_instance",
    };

    // ── 状態 ─────────────────────────────────────────────────────

    /// <summary>束縛先のポート。null なら未束縛。</summary>
    public static int? Port { get; private set; }

    /// <summary>束縛先のインスタンストークン。</summary>
    public static string? Token { get; private set; }

    /// <summary>束縛先のプロセス ID。</summary>
    public static int? Pid { get; private set; }

    /// <summary>束縛先がヘッドレス起動か。</summary>
    public static bool Headless { get; private set; }

    /// <summary>
    /// 利用者が明示的に <c>seed_attach</c> で繋いだ相手か
    /// （false なら <c>seed_launch</c> が起動した使い捨てインスタンス）。
    /// </summary>
    public static bool Attached { get; private set; }

    /// <summary>束縛済みか。</summary>
    public static bool IsBound => Port.HasValue;

    // ── 導出値 ───────────────────────────────────────────────────

    /// <summary>
    /// リクエスト先のベース URL。
    /// 未束縛のときは既定ポート（観測系ツールのみが到達する）。
    /// </summary>
    public static string ApiBase => BaseUrlFor(Port ?? DEFAULT_PORT);

    /// <summary>指定ポートのベース URL を組み立てる。</summary>
    public static string BaseUrlFor(int port) => $"http://localhost:{port}/seed-ai";

    // ── 束縛操作 ─────────────────────────────────────────────────

    /// <summary>インスタンスを束縛する。</summary>
    public static void Bind(int port, string token, int pid, bool headless, bool attached)
    {
        Port     = port;
        Token    = token;
        Pid      = pid;
        Headless = headless;
        Attached = attached;
    }

    /// <summary>束縛を解除する（shutdown 後など）。</summary>
    public static void Clear()
    {
        Port     = null;
        Token    = null;
        Pid      = null;
        Headless = false;
        Attached = false;
    }

    // ── 判定 ─────────────────────────────────────────────────────

    /// <summary>
    /// ツールを実行してよいかを判定する。
    /// </summary>
    /// <returns>許可なら null、拒否ならその理由。</returns>
    public static string? CheckToolAllowed(string toolName)
    {
        if (IsBound) return null;
        if (ReadOnlyTools.Contains(toolName)) return null;
        return $"ERROR: {DENY_NOT_BOUND}。"
             + "seed_launch(headless:true, scene:\"...\") でヘッドレスインスタンスを起動するか、"
             + "利用者が開いているエディタを操作したい場合は、そのエディタの"
             + "「編集 → 環境設定」で「AI 操作を許可（このインスタンス）」をオンにしてもらい、"
             + "表示されたポートとトークンで seed_attach(port, token) を実行してください。";
    }

    /// <summary>現在の束縛状態を JSON で返す（seed_instance / 各ツールの応答用）。</summary>
    public static string ToJson()
    {
        var payload = new
        {
            ok       = true,
            bound    = IsBound,
            port     = Port,
            pid      = Pid,
            headless = Headless,
            attached = Attached,
            // トークンそのものは返さない（ログや会話履歴に残さないため）。
            has_token = !string.IsNullOrEmpty(Token),
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }
}
