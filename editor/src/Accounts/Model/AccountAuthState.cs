// ============================================================
//  AccountAuthState.cs — ログイン状態（購読できる 1 つの値）
//
//  【役割】
//  「未ログイン／ログイン処理中／ログイン中／失敗（理由つき）」を 1 つの不変値で表す。
//  パネルのヘッダー・Hub のアカウント欄・ログが同じ値を見る。
//
//  【なぜ 1 つの値にまとめるのか】
//  「ログイン中か」「名前は」「失敗の理由は」を別々のプロパティにすると、
//  イベントが飛んだ瞬間に一部だけ更新された中途半端な状態を購読側が読める。
//  不変のレコードを丸ごと差し替えれば、その隙間が生まれない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.Accounts.Model;

/// <summary>ログインの段階。</summary>
public enum AccountAuthPhase
{
    /// <summary>ログインしていない（アカウントが無い・窓口が無い場合を含む）。匿名で動く。</summary>
    NotSignedIn = 0,

    /// <summary>ログイン処理中。</summary>
    SigningIn = 1,

    /// <summary>ログイン済み。トークンを持っている。</summary>
    SignedIn = 2,

    /// <summary>ログインに失敗した（理由は <see cref="AccountAuthState.Reason"/>）。</summary>
    Failed = 3,
}

/// <summary>
/// ログイン状態（不変）。
/// </summary>
/// <param name="Phase">段階。</param>
/// <param name="AccountName">アカウント名（未ログインなら空）。</param>
/// <param name="Reason">失敗や補足の理由（無ければ空）。</param>
/// <param name="ExpiresAtUtc">トークンの期限（ログイン中のみ意味を持つ）。</param>
public sealed record AccountAuthState(
    AccountAuthPhase Phase,
    string AccountName,
    string Reason,
    DateTime ExpiresAtUtc)
{
    /// <summary>未ログイン（理由なし）。</summary>
    public static AccountAuthState NotSignedIn { get; } =
        new(AccountAuthPhase.NotSignedIn, string.Empty, string.Empty, default);

    /// <summary>理由つきの未ログイン状態を作る（窓口が無い場合など）。</summary>
    /// <param name="reason">理由。</param>
    public static AccountAuthState Anonymous(string reason)
        => new(AccountAuthPhase.NotSignedIn, string.Empty, reason, default);

    /// <summary>ログイン処理中の状態を作る。</summary>
    /// <param name="accountName">試している名前。</param>
    public static AccountAuthState SigningIn(string accountName)
        => new(AccountAuthPhase.SigningIn, accountName, string.Empty, default);

    /// <summary>ログイン済みの状態を作る。</summary>
    /// <param name="accountName">名前。</param>
    /// <param name="expiresAtUtc">トークンの期限。</param>
    public static AccountAuthState SignedIn(string accountName, DateTime expiresAtUtc)
        => new(AccountAuthPhase.SignedIn, accountName, string.Empty, expiresAtUtc);

    /// <summary>失敗した状態を作る。</summary>
    /// <param name="accountName">試した名前。</param>
    /// <param name="reason">理由（利用者向けの日本語）。</param>
    public static AccountAuthState Failed(string accountName, string reason)
        => new(AccountAuthPhase.Failed, accountName, reason, default);

    /// <summary>ログイン中か（トークンを持っているか）。</summary>
    public bool IsSignedIn => Phase == AccountAuthPhase.SignedIn;

    /// <summary>
    /// 画面へ出す 1 行の説明。
    /// </summary>
    public string Description => Phase switch
    {
        AccountAuthPhase.SignedIn  => string.Format(
            CultureInfo.CurrentCulture, AccountMessages.AUTH_SIGNED_IN_FORMAT, AccountName),
        AccountAuthPhase.SigningIn => AccountMessages.AUTH_SIGNING_IN,
        AccountAuthPhase.Failed    => string.Format(
            CultureInfo.CurrentCulture, AccountMessages.AUTH_FAILED_FORMAT, Reason),
        _ => string.IsNullOrWhiteSpace(Reason) ? AccountMessages.AUTH_NOT_SIGNED_IN : Reason,
    };
}
