// ============================================================
//  AccountFileStore.cs — account.json の読み書き（DPAPI ＋ tmp → rename）
//
//  【役割】
//  契約（docs/seed_accounts.md 2 章）の
//  「`%APPDATA%\SEED\account\account.json`。PKCS#8 を DPAPI（CurrentUser）で暗号化して保存」
//  を実装する。
//
//  【tmp → rename にする理由】
//  アカウントファイルは **失うと参加中のプロジェクトへ入れなくなる**。
//  直接上書きしている最中に落ちると、名前だけあって鍵が壊れたファイルが残る。
//  一時ファイルへ書き切ってから rename すれば、途中で落ちても
//  「前の正しいファイル」がそのまま残る。
//
//  【秘密の扱い】
//  ・平文の PKCS#8 はローカル変数にしか置かず、使い終わったらゼロクリアする。
//  ・例外メッセージへ鍵の中身を入れない（そのまま画面へ出るため）。
//
//  【依存】
//  ISecretProtector 経由でのみ暗号に触れる。WPF にも LoreVcs にも依存しない。
// ============================================================

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SEEDEditor.Accounts.Crypto;
using SEEDEditor.Accounts.Model;

namespace SEEDEditor.Accounts.Storage;

/// <summary>
/// ファイルに保管するアカウントストア。
/// </summary>
public sealed class AccountFileStore : IAccountStore
{
    /// <summary>JSON の書式（人が覗いて確認できるよう整形する）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>秘密鍵を包む実装。</summary>
    private readonly ISecretProtector _protector;

    /// <summary>アカウントファイルの絶対パス。</summary>
    public string FilePath { get; }

    /// <summary>
    /// 保管フォルダと包む実装を指定して生成する。
    /// </summary>
    /// <param name="accountDir">
    /// 保管フォルダ。null なら <see cref="AccountPaths.ResolveAccountDir"/>
    /// （環境変数 <see cref="AccountSettings.ENV_ACCOUNT_DIR"/> で差し替え可）。
    /// </param>
    /// <param name="protector">
    /// 秘密鍵を包む実装。null なら DPAPI（CurrentUser）。
    /// </param>
    public AccountFileStore(string? accountDir = null, ISecretProtector? protector = null)
    {
        FilePath   = AccountPaths.ResolveAccountFile(accountDir);
        _protector = protector ?? DpapiSecretProtector.Instance;
    }

    /// <summary>アカウントが保存されているか。</summary>
    public bool Exists => File.Exists(FilePath);

    // ── 読み込み ────────────────────────────────────────────

    /// <summary>保存されているアカウントを読む。</summary>
    /// <returns>アカウント（未作成なら null）。</returns>
    public SeedAccount? Load()
    {
        if (!Exists) return null;

        AccountRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<AccountRecord>(
                File.ReadAllText(FilePath), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AccountStoreException(AccountMessages.ACCOUNT_FILE_BROKEN, ex);
        }
        catch (IOException ex)
        {
            throw new AccountStoreException(
                string.Format(CultureInfo.CurrentCulture,
                              AccountMessages.ACCOUNT_LOAD_FAILED_FORMAT, ex.Message), ex);
        }

        if (record is null
            || string.IsNullOrWhiteSpace(record.Name)
            || string.IsNullOrWhiteSpace(record.ProtectedPrivateKey))
        {
            throw new AccountStoreException(AccountMessages.ACCOUNT_FILE_BROKEN);
        }

        byte[] wrapped;
        try
        {
            wrapped = Convert.FromBase64String(record.ProtectedPrivateKey);
        }
        catch (FormatException ex)
        {
            throw new AccountStoreException(AccountMessages.ACCOUNT_FILE_BROKEN, ex);
        }

        byte[]? pkcs8 = null;
        try
        {
            // 別の PC・別の Windows ユーザーで保存されたものはここで失敗する。
            // 「壊れている」ではなく「この PC では解けない」と案内し、
            // 書き出しファイルからの復帰へ誘導する。
            pkcs8 = _protector.Unprotect(wrapped);

            var keyPair = AccountKeyPair.FromPkcs8(pkcs8);
            return new SeedAccount(record.Name, keyPair);
        }
        catch (CryptographicException ex)
        {
            throw new AccountStoreException(AccountMessages.ACCOUNT_DECRYPT_FAILED, ex);
        }
        finally
        {
            // 平文の秘密鍵をヒープへ残さない。
            if (pkcs8 is not null) CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    // ── 書き込み ────────────────────────────────────────────

    /// <summary>アカウントを保存する（tmp → rename）。</summary>
    /// <param name="account">保存するアカウント。</param>
    public void Save(SeedAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        byte[]? pkcs8 = null;
        try
        {
            pkcs8 = account.ExportPkcs8PrivateKey();

            var record = new AccountRecord
            {
                Version             = AccountRecord.CURRENT_VERSION,
                Name                = account.Name,
                PublicKey           = account.PublicKeyBase64Url,
                ProtectedPrivateKey = Convert.ToBase64String(_protector.Protect(pkcs8)),
                CreatedAtUtc        = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };

            WriteAtomically(JsonSerializer.Serialize(record, JsonOptions));
        }
        catch (AccountStoreException)
        {
            // 既に利用者向けの文言になっているものはそのまま通す。
            throw;
        }
        catch (Exception ex)
        {
            throw new AccountStoreException(
                string.Format(CultureInfo.CurrentCulture,
                              AccountMessages.ACCOUNT_SAVE_FAILED_FORMAT, ex.Message), ex);
        }
        finally
        {
            if (pkcs8 is not null) CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// 一時ファイルへ書き切ってから置き換える（途中で落ちても前の内容が残る）。
    /// </summary>
    /// <param name="json">書き込む JSON。</param>
    private void WriteAtomically(string json)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = FilePath + AccountSettings.TEMP_FILE_SUFFIX;

        File.WriteAllText(tempPath, json);
        // overwrite: true にしないと、既存ファイルがあるときに失敗する。
        File.Move(tempPath, FilePath, overwrite: true);
    }
}
