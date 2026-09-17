// ============================================================
//  AuthFailureText.cs — 窓口のエラーコード → 利用者向けの 1 行
//
//  【役割】
//  契約 3 章のエラーコード（`invalid_request` / `login_failed` / …）を、
//  **利用者が次に何をすればよいか分かる日本語**へ写す、ただ 1 か所。
//
//  【なぜ 1 か所にするのか】
//  同じコードを Hub の参加画面・オーナー向けダイアログ・自動ログインの 3 か所で
//  解釈すると、片方だけ言い回しが古くなる。加えて「サーバの生のメッセージを
//  そのまま出す」箇所が混ざると、利用者には英語や実装語が漏れ得る。
//
//  【サーバのメッセージを捨てない】
//  ここで言い換えないコードは、サーバが返した日本語をそのまま使う
//  （契約上サーバは日本語で説明を返す）。**握りつぶさない**。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.Accounts.Http;

/// <summary>
/// 窓口の失敗を利用者向けの 1 行に写す。
/// </summary>
public static class AuthFailureText
{
    /// <summary>
    /// 失敗を 1 行の日本語にする。
    /// </summary>
    /// <param name="error">窓口が返した失敗。</param>
    /// <returns>そのまま画面へ出してよい 1 行。</returns>
    public static string Describe(AuthGatewayException error)
    {
        // 窓口へ繋がらないのはコードの問題ではない（サーバが居ない・アドレス違い）。
        if (error.IsUnreachable) return AccountMessages.GATEWAY_UNREACHABLE;

        return error.ErrorCode switch
        {
            // ログイン失敗の理由はサーバが一切区別しない（存在を漏らさないため）。
            // こちらで「鍵が違う」等と推測しない。
            AuthErrorCodes.LOGIN_FAILED      => AccountMessages.AUTH_REJECTED,

            AuthErrorCodes.UNAUTHORIZED      => AccountMessages.OWNER_SESSION_EXPIRED,
            AuthErrorCodes.FORBIDDEN         => AccountMessages.OWNER_PERMISSION_REQUIRED,
            AuthErrorCodes.INVITE_INVALID    => AccountMessages.JOIN_INVITE_INVALID,
            AuthErrorCodes.LOOPBACK_ONLY     => AccountMessages.OWNER_BOOTSTRAP_LOOPBACK_REQUIRED,
            AuthErrorCodes.NOT_FOUND         => AccountMessages.OWNER_MEMBER_NOT_FOUND,
            AuthErrorCodes.TOO_MANY_REQUESTS => AccountMessages.GATEWAY_TOO_MANY_REQUESTS,
            AuthErrorCodes.INTERNAL          => AccountMessages.GATEWAY_INTERNAL_ERROR,

            // owner_exists と name_taken は「どの操作で起きたか」で意味が変わるので、
            // 呼び出し側が言い換える（ここでは既定としてサーバの説明を出す）。
            _ => string.IsNullOrWhiteSpace(error.Message)
                ? AccountMessages.GATEWAY_UNEXPECTED_RESPONSE
                : error.Message,
        };
    }
}
