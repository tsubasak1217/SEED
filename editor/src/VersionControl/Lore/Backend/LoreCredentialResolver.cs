// ============================================================
//  LoreCredentialResolver.cs — Lore の共通引数に載せる資格情報の決め方
//
//  【役割】
//  「アクセストークンがあるときは AccessToken に入れ、**Identity は空にする**。
//    無いときは `.lore/config.toml` の identity を使う」という 1 つの規則を、
//  純関数として固定する。
//
//  【なぜ規則を切り出すのか（間違えると全操作が失敗する）】
//  LoreVcs の XML ドキュメントに、AccessToken / IdentityToken どちらについても
//  次の一文がある:
//
//    "Supplying either token puts the call in external-credential mode:
//     `identity` must be left empty, since it is read from the token."
//
//  つまりトークンを渡すときに Identity も一緒に埋めてはいけない
//  （identity はトークンの `sub` から読まれる。渡すと「トークンが identity を
//   名乗っている」という排他エラーで弾かれる）。この判断が
//  LoreNativeBackend の中（LoreVcs 依存のファイル）にだけ書かれていると、
//  **LoreVcs を参照しないテストから固定できない**。ここへ出して固定する。
//
//  【★AccessToken だけでは足りない（実サーバで確認済み）】
//  docs/seed_accounts.md 6 章のとおり、**IdentityToken と AccessToken の
//  両方に同じ JWT** を入れる。Lore v0.9.0 の `auth_exchange_for_identity` は、
//  リポジトリ ID が確定していない呼び出し
//  （`repository create` / `repository list` / **`clone`**）で authorization token を
//  空にするため、AccessToken が Authorization ヘッダに載らず
//  `authorization header required` で失敗する。その経路は IdentityToken 由来の
//  authentication token を使う。「プロジェクトに参加」のクローンがまさにこれ。
//
//  【表示用の identity は別】
//  ロックの「自分／他の人」の判定とパネルのヘッダーは、
//  トークンを送っているときは **アカウント名** を自分の identity として使う
//  （サーバがロックの所有者にその名前を記録するため）。
//  Lore へ渡す Identity（空にする）とは意味が違うので、関数を分けてある。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.VersionControl.Lore.Backend;

/// <summary>
/// ログイン中のアカウント（トークンと名前）。未ログインなら
/// <see cref="None"/>（どちらも空）。
/// </summary>
/// <param name="AccessToken">アクセストークン（JWT）。未ログインなら空。</param>
/// <param name="AccountName">アカウント名（トークンの sub と同じ）。未ログインなら空。</param>
public readonly record struct LoreAccountCredential(string AccessToken, string AccountName)
{
    /// <summary>未ログインを表す値。</summary>
    public static LoreAccountCredential None => new(string.Empty, string.Empty);

    /// <summary>トークンを持っているか。</summary>
    public bool HasToken => !string.IsNullOrWhiteSpace(AccessToken);
}

/// <summary>
/// Lore の共通引数へ載せる資格情報。
///
/// <para>
/// ログイン中は <see cref="IdentityToken"/> と <see cref="AccessToken"/> に
/// **同じ JWT** が入り、<see cref="Identity"/> は空になる。
/// 未ログインなら 2 つのトークンが空で、<see cref="Identity"/> に
/// `.lore/config.toml` の値が入る。
/// </para>
/// </summary>
/// <param name="IdentityToken">
/// Lore の <c>LoreGlobalArgs.IdentityToken</c> に入れる値。
/// リポジトリ ID が確定していない呼び出し（clone など）はこちらを使う。
/// </param>
/// <param name="AccessToken">Lore の <c>LoreGlobalArgs.AccessToken</c> に入れる値。</param>
/// <param name="Identity">Lore の <c>LoreGlobalArgs.Identity</c> に入れる値。</param>
public readonly record struct LoreGlobalCredentials(
    string IdentityToken, string AccessToken, string Identity);

/// <summary>
/// 資格情報の決め方（純関数）。
/// </summary>
public static class LoreCredentialResolver
{
    /// <summary>
    /// 共通引数に載せる <c>IdentityToken</c> / <c>AccessToken</c> / <c>Identity</c> を決める。
    /// </summary>
    /// <param name="account">ログイン中のアカウント（未ログインなら <see cref="LoreAccountCredential.None"/>）。</param>
    /// <param name="configIdentity">`.lore/config.toml` の identity（無ければ空）。</param>
    /// <returns>共通引数へそのまま写せる値。</returns>
    public static LoreGlobalCredentials Resolve(
        LoreAccountCredential account, string? configIdentity)
    {
        if (account.HasToken)
        {
            var token = account.AccessToken.Trim();

            // ★2 つのトークンへ同じ JWT を入れる。
            //   AccessToken だけだと clone（リポジトリ ID が未確定の呼び出し）で
            //   Authorization ヘッダが空になり `authorization header required` で落ちる。
            // ★identity は **必ず空**。渡すと排他エラーで弾かれる。
            return new LoreGlobalCredentials(token, token, string.Empty);
        }

        return new LoreGlobalCredentials(
            string.Empty, string.Empty, configIdentity?.Trim() ?? string.Empty);
    }

    /// <summary>
    /// 「自分は誰か」を表示・比較するための identity を決める。
    ///
    /// <para>
    /// ログイン中はアカウント名。未ログインなら `.lore/config.toml` の identity。
    /// ロックの所有者比較（<c>LoreLockTranslator.ResolveHolder</c>）はこれを使う。
    /// </para>
    /// </summary>
    /// <param name="account">ログイン中のアカウント。</param>
    /// <param name="configIdentity">`.lore/config.toml` の identity。</param>
    /// <returns>表示・比較に使う identity（不明なら空文字）。</returns>
    public static string ResolveDisplayIdentity(
        LoreAccountCredential account, string? configIdentity)
    {
        // トークンを送っているなら、サーバはトークンの sub を identity として記録する。
        // その名前で比較しないと、自分が取ったロックが「他の人」に見える。
        if (account.HasToken && !string.IsNullOrWhiteSpace(account.AccountName))
            return account.AccountName.Trim();

        return configIdentity?.Trim() ?? string.Empty;
    }
}
