// ============================================================
//  LoginSignaturePayload.cs — ログインで署名する文字列の組み立て
//
//  【役割】
//  契約（docs/seed_accounts.md 2 章）が定める
//    UTF-8 の `seed-auth-login:v1:<challenge_id>:<nonce>`
//  を作る、ただ 1 か所。サーバ側とここで **1 バイトでも違うと必ずログインできない**
//  ので、組み立てを画面やクライアントへ散らさない。
//
//  【nonce はサーバが返した文字列そのまま】
//  base64url の文字列として返ってくるが、**復号して署名対象にしない**。
//  受け取った文字列をそのまま連結する（契約の表にそう書かれている）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System.Text;

namespace SEEDEditor.Accounts.Crypto;

/// <summary>
/// ログインチャレンジに署名する文字列を作る。
/// </summary>
public static class LoginSignaturePayload
{
    /// <summary>
    /// 署名対象の文字列を組み立てる。
    /// </summary>
    /// <param name="challengeId">サーバが返した challenge_id。</param>
    /// <param name="nonce">サーバが返した nonce（base64url 文字列のまま）。</param>
    /// <returns>署名対象の文字列。</returns>
    public static string Build(string challengeId, string nonce)
        => AccountSettings.LOGIN_SIGNATURE_PREFIX
           + AccountSettings.LOGIN_SIGNATURE_SEPARATOR + (challengeId ?? string.Empty)
           + AccountSettings.LOGIN_SIGNATURE_SEPARATOR + (nonce ?? string.Empty);

    /// <summary>
    /// 署名対象の文字列を UTF-8 のバイト列で作る（署名 API へ渡す形）。
    /// </summary>
    /// <param name="challengeId">サーバが返した challenge_id。</param>
    /// <param name="nonce">サーバが返した nonce。</param>
    /// <returns>UTF-8 のバイト列。</returns>
    public static byte[] BuildBytes(string challengeId, string nonce)
        => Encoding.UTF8.GetBytes(Build(challengeId, nonce));
}
