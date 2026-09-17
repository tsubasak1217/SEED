// ============================================================
//  AuthEndpointResolver.cs — 発行窓口とリモートのアドレス組み立て
//
//  【役割】
//  契約（docs/seed_accounts.md 6 章）の
//  「窓口は既定でリモートと同じホストの 41350。利用者設定で上書き可」
//  という決め方を、1 か所の純関数にする。
//
//  【なぜ 1 か所にするのか】
//  「リモート URL からホストを取り出す」処理を Hub の参加画面・
//  プロジェクトを開いたときのログイン・オーナー向けダイアログの
//  3 か所で書くと、片方だけポートを取り違えて
//  「参加はできるのにログインできない」状態が簡単に作れる。
//
//  【受け付ける書き方】
//    リモート     : `lore://host:41337/Project` / `lore://host/Project`
//    利用者の上書き: `http://host:41350` / `host:41350` / `host` / IP アドレス
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.Accounts.Http;

/// <summary>
/// 発行窓口（seed-auth）と Lore リモートの URL を組み立てる。
/// </summary>
public static class AuthEndpointResolver
{
    /// <summary>URL のホストとポートの区切り。</summary>
    private const char HOST_PORT_SEPARATOR = ':';

    /// <summary>スキームとホストの区切り。</summary>
    private const string SCHEME_SEPARATOR = "://";

    /// <summary>URL のパス区切り。</summary>
    private const char PATH_SEPARATOR = '/';

    /// <summary>
    /// Lore のリモート URL（`lore://host:port/Project`）からホスト名を取り出す。
    /// </summary>
    /// <param name="remoteUrl">リモート URL。</param>
    /// <returns>ホスト名。取り出せなければ空文字。</returns>
    public static string ExtractHost(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return string.Empty;

        var value = remoteUrl.Trim();

        // スキームがあれば落とす（lore:// でも http:// でも同じ扱いでよい）。
        var schemeIndex = value.IndexOf(SCHEME_SEPARATOR, StringComparison.Ordinal);
        if (schemeIndex >= 0) value = value[(schemeIndex + SCHEME_SEPARATOR.Length)..];

        // パス以降を落とす。
        var pathIndex = value.IndexOf(PATH_SEPARATOR);
        if (pathIndex >= 0) value = value[..pathIndex];

        // ポートを落とす。IPv6 の `[::1]:port` は SEED の運用に無いので扱わない
        // （扱うふりをすると中途半端に壊れるため、素直にホストとして返す）。
        var portIndex = value.LastIndexOf(HOST_PORT_SEPARATOR);
        if (portIndex > 0) value = value[..portIndex];

        return value.Trim();
    }

    /// <summary>
    /// 発行窓口の URL を決める。
    /// </summary>
    /// <param name="remoteUrl">
    /// Lore のリモート URL（`.lore/config.toml` の remote_url）。既定のホスト元。
    /// </param>
    /// <param name="userOverride">
    /// 利用者設定の上書き（空なら使わない）。`http://host:port` / `host:port` / `host`。
    /// </param>
    /// <returns>窓口の URL。決められなければ null。</returns>
    public static Uri? ResolveGateway(string? remoteUrl, string? userOverride)
    {
        // 上書きがあれば最優先（リモートが無い環境でも窓口だけ指せる）。
        if (!string.IsNullOrWhiteSpace(userOverride))
            return BuildHttpUri(userOverride!, AccountSettings.DEFAULT_AUTH_PORT);

        var host = ExtractHost(remoteUrl);
        if (host.Length == 0) return null;

        return BuildHttpUri(host, AccountSettings.DEFAULT_AUTH_PORT);
    }

    /// <summary>
    /// ホスト（またはアドレス表記）から窓口の HTTP URL を作る。
    /// </summary>
    /// <param name="hostOrUrl">`http://host:port` / `host:port` / `host`。</param>
    /// <param name="defaultPort">ポートが書かれていないときに使うポート。</param>
    /// <returns>組み立てた URL。書式が読めなければ null。</returns>
    public static Uri? BuildHttpUri(string hostOrUrl, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl)) return null;

        var value = hostOrUrl.Trim();

        // スキームが書かれていれば、そのまま解釈を任せる。
        if (value.Contains(SCHEME_SEPARATOR, StringComparison.Ordinal))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute : null;
        }

        // `host` または `host:port`。ポートが無ければ既定を足す。
        var portIndex = value.LastIndexOf(HOST_PORT_SEPARATOR);
        var hasPort   = portIndex > 0
                        && int.TryParse(value[(portIndex + 1)..], NumberStyles.Integer,
                                        CultureInfo.InvariantCulture, out _);

        var text = hasPort
            ? $"{AccountSettings.AUTH_URL_SCHEME}{SCHEME_SEPARATOR}{value}"
            : $"{AccountSettings.AUTH_URL_SCHEME}{SCHEME_SEPARATOR}{value}"
              + HOST_PORT_SEPARATOR
              + defaultPort.ToString(CultureInfo.InvariantCulture);

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>
    /// 参加時のクローン元 URL（`lore://host:<lorePort>/<プロジェクト名>`）を作る。
    ///
    /// <para>
    /// ★<paramref name="host"/> に書かれたポートは **発行窓口のもの** として
    /// <see cref="BuildHttpUri"/> が使う（参加画面はサーバのアドレスを 1 つしか受け取らない）。
    /// Lore 本体のポートは別物なので、ここでは必ず <paramref name="lorePort"/> を使い、
    /// ホストに付いているポートは落とす。
    /// **ここを取り違えると、別ポートで動かしているサーバへ参加したつもりが
    /// 既定ポート（41337）の別のサーバへクローンしにいく。**
    /// </para>
    /// </summary>
    /// <param name="host">サーバのホスト名か IP（ポートが付いていても落とす）。</param>
    /// <param name="projectName">サーバ上のプロジェクト名（リポジトリ名）。</param>
    /// <param name="lorePort">
    /// Lore 本体のポート。0 以下なら契約の既定
    /// （<see cref="AccountSettings.DEFAULT_LORE_PORT"/>）を使う。
    /// </param>
    /// <returns>クローン元 URL。ホストかプロジェクト名が空なら空文字。</returns>
    public static string BuildLoreRemoteUrl(
        string? host, string? projectName, int lorePort = 0)
    {
        var trimmedHost    = ExtractHost(host);
        var trimmedProject = projectName?.Trim() ?? string.Empty;
        if (trimmedHost.Length == 0 || trimmedProject.Length == 0) return string.Empty;

        var port = lorePort > 0 ? lorePort : AccountSettings.DEFAULT_LORE_PORT;

        return AccountSettings.LORE_URL_SCHEME + SCHEME_SEPARATOR
               + trimmedHost + HOST_PORT_SEPARATOR
               + port.ToString(CultureInfo.InvariantCulture)
               + PATH_SEPARATOR + trimmedProject;
    }
}
