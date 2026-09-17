// ============================================================
//  AccountPaths.cs — アカウントの保管場所の解決
//
//  【役割】
//  契約（docs/seed_accounts.md 2 章）の
//  `%APPDATA%\SEED\account\account.json` を解決する、ただ 1 か所。
//
//  【なぜ差し替えられる必要があるのか】
//  テストが本物の `%APPDATA%\SEED\account\` へ書いてしまうと、
//  **利用者の実アカウントを壊す**（鍵を失うと参加中のプロジェクトへ入れなくなる）。
//  環境変数 <see cref="AccountSettings.ENV_ACCOUNT_DIR"/> で保管先を差し替えられるようにし、
//  テストは必ず一時フォルダを指すようにする。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.IO;

namespace SEEDEditor.Accounts.Storage;

/// <summary>
/// アカウントファイルの置き場を解決する。
/// </summary>
public static class AccountPaths
{
    /// <summary>
    /// アカウントの保管フォルダ（絶対パス）を解決する。
    ///
    /// <para>
    /// 環境変数 <see cref="AccountSettings.ENV_ACCOUNT_DIR"/> があればそれを使い、
    /// 無ければ `%APPDATA%\SEED\account`。フォルダの作成は行わない
    /// （読むだけのときに空フォルダを作らないため）。
    /// </para>
    /// </summary>
    /// <returns>保管フォルダの絶対パス。</returns>
    public static string ResolveAccountDir()
    {
        var overridden = Environment.GetEnvironmentVariable(AccountSettings.ENV_ACCOUNT_DIR);
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        return Path.Combine(appData, AccountSettings.APPDATA_APP_DIR_NAME,
                            AccountSettings.ACCOUNT_DIR_NAME);
    }

    /// <summary>
    /// アカウントファイル（account.json）の絶対パスを解決する。
    /// </summary>
    /// <param name="accountDir">保管フォルダ（省略時は <see cref="ResolveAccountDir"/>）。</param>
    /// <returns>アカウントファイルの絶対パス。</returns>
    public static string ResolveAccountFile(string? accountDir = null)
        => Path.Combine(accountDir ?? ResolveAccountDir(), AccountSettings.ACCOUNT_FILE_NAME);
}
