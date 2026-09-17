// ============================================================
//  AccountEditorSettings.cs — 利用者が変えられるアカウント関連の設定
//
//  【役割】
//  「発行窓口の URL を既定から変えたい」という上書きだけを持つ。
//  既定はリモートと同じホストの 41350（契約 6 章）なので、
//  普通の利用者はこのファイルを触らない。
//
//  【置き場】
//  `editor/settings/accounts.json`。スクリプトエディタの設定
//  （`editor/settings/script_editor.json`）と同じ流儀にしてある。
//  プロジェクトを跨いで共有される値なのでプロジェクトフォルダには置かない。
//
//  【ここに秘密を置かない】
//  トークンも秘密鍵もこのファイルには書かない。トークンはメモリだけ、
//  秘密鍵は `%APPDATA%\SEED\account\account.json`（DPAPI）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Accounts;

/// <summary>
/// アカウント関連の利用者設定（`editor/settings/accounts.json`）。
/// </summary>
public sealed class AccountEditorSettings
{
    /// <summary>設定ファイル名。</summary>
    public const string FILE_NAME = "accounts.json";

    /// <summary>JSON の書式（人が直接編集することがあるので整形する）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 発行窓口の URL の上書き。空なら
    /// 「リモートと同じホストの <see cref="AccountSettings.DEFAULT_AUTH_PORT"/>」を使う。
    /// `http://host:port` / `host:port` / `host` のいずれでも書ける。
    /// </summary>
    [JsonPropertyName("auth_url_override")]
    public string AuthUrlOverride { get; set; } = string.Empty;

    /// <summary>
    /// プロジェクトを開いたときに自動でログインするか。
    /// 既定は真。切ると常に匿名で動く（トラブル時の切り分け用）。
    /// </summary>
    [JsonPropertyName("auto_sign_in")]
    public bool AutoSignIn { get; set; } = true;

    /// <summary>設定ファイルの絶対パスを返す。</summary>
    /// <param name="settingsDir">エディタの設定フォルダ。</param>
    public static string FilePath(string settingsDir) => Path.Combine(settingsDir, FILE_NAME);

    /// <summary>
    /// 設定を読む。無い・壊れている場合は既定値を返す
    /// （設定ファイルが 1 つ壊れただけでエディタが起動できなくなってはいけない）。
    /// </summary>
    /// <param name="settingsDir">エディタの設定フォルダ。</param>
    public static AccountEditorSettings Load(string settingsDir)
    {
        try
        {
            var path = FilePath(settingsDir);
            if (!File.Exists(path)) return new AccountEditorSettings();

            return JsonSerializer.Deserialize<AccountEditorSettings>(
                       File.ReadAllText(path), JsonOptions)
                   ?? new AccountEditorSettings();
        }
        catch (Exception)
        {
            return new AccountEditorSettings();
        }
    }

    /// <summary>
    /// 設定を書く。失敗しても例外を投げない（保存できないのは致命的ではない）。
    /// </summary>
    /// <param name="settingsDir">エディタの設定フォルダ。</param>
    /// <returns>書けたら真。</returns>
    public bool Save(string settingsDir)
    {
        try
        {
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(FilePath(settingsDir), JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
