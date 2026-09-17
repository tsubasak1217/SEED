// ============================================================
//  AccountExportFile.cs — アカウントの書き出し／読み込み（別 PC への移動用）
//
//  【役割】
//  契約（docs/seed_accounts.md 2 章）の「別 PC へは『書き出し／読み込み』で移す」を実装する。
//  DPAPI は同じ PC・同じ Windows ユーザーでしか解けないため、移動には別の包み方が要る。
//
//  【包み方】
//    パスフレーズ ──PBKDF2-HMAC-SHA256（60 万回・16 バイトソルト）──▶ 32 バイト鍵
//    PKCS#8 の秘密鍵 ──AES-256-GCM（12 バイトノンス・16 バイトタグ）──▶ 暗号文
//
//  【なぜ平文の書き出しを作らないのか】
//  「とりあえず平文で出して後で暗号化」を許すと、秘密鍵がそのまま
//  共有フォルダやチャットへ流れる。**パスフレーズ無しの経路は用意しない。**
//
//  【AAD（追加認証データ）に名前と公開鍵を入れる理由】
//  名前・公開鍵はファイルに平文で入る（読み込む前に誰のものか見せたいため）。
//  AAD に含めておくと、**それらを書き換えたファイルは復号に失敗する**。
//  他人の名前を貼り付けた偽の書き出しファイルを作れないようにするため。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Accounts.Crypto;
using SEEDEditor.Accounts.Model;

namespace SEEDEditor.Accounts.Storage;

/// <summary>
/// 書き出しファイルの中身（JSON）。
/// </summary>
public sealed class AccountExportPayload
{
    /// <summary>形式識別子（<see cref="AccountSettings.EXPORT_FORMAT_ID"/>）。</summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    /// <summary>形式の版。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>アカウント名（平文。読み込む前に誰のものか見せるため）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>公開鍵の base64url（平文）。</summary>
    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>鍵導出関数の名前。</summary>
    [JsonPropertyName("kdf")]
    public string Kdf { get; set; } = string.Empty;

    /// <summary>PBKDF2 の反復回数。</summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; set; }

    /// <summary>PBKDF2 のソルト（base64url）。</summary>
    [JsonPropertyName("salt")]
    public string Salt { get; set; } = string.Empty;

    /// <summary>AES-GCM のノンス（base64url）。</summary>
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    /// <summary>暗号文（base64url）。</summary>
    [JsonPropertyName("ciphertext")]
    public string CipherText { get; set; } = string.Empty;

    /// <summary>認証タグ（base64url）。</summary>
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;
}

/// <summary>
/// パスフレーズで包んだアカウントの書き出し／読み込み。
/// </summary>
public static class AccountExportFile
{
    /// <summary>JSON の書式。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>AAD の区切り文字。</summary>
    private const char AAD_SEPARATOR = ':';

    /// <summary>AAD の版を表す接頭辞（`…:v1` の "v"）。</summary>
    private const string AAD_VERSION_MARK = "v";

    // ── 書き出し ────────────────────────────────────────────

    /// <summary>
    /// アカウントをパスフレーズで包んでファイルへ書き出す。
    /// </summary>
    /// <param name="account">書き出すアカウント。</param>
    /// <param name="passphrase">パスフレーズ。</param>
    /// <param name="filePath">書き出し先の絶対パス。</param>
    /// <exception cref="AccountStoreException">
    /// パスフレーズが短い、または書き込めないとき。
    /// </exception>
    public static void Export(SeedAccount account, string passphrase, string filePath)
    {
        ArgumentNullException.ThrowIfNull(account);
        CheckPassphrase(passphrase);

        byte[]? pkcs8 = null;
        byte[]? key   = null;
        try
        {
            pkcs8 = account.ExportPkcs8PrivateKey();

            var salt  = RandomNumberGenerator.GetBytes(AccountSettings.EXPORT_SALT_BYTE_LENGTH);
            var nonce = RandomNumberGenerator.GetBytes(AccountSettings.EXPORT_NONCE_BYTE_LENGTH);
            key       = DeriveKey(passphrase, salt, AccountSettings.EXPORT_KDF_ITERATIONS);

            var cipherText = new byte[pkcs8.Length];
            var tag        = new byte[AccountSettings.EXPORT_TAG_BYTE_LENGTH];
            var aad        = BuildAad(AccountSettings.EXPORT_FORMAT_VERSION,
                                      account.Name, account.PublicKeyBase64Url);

            using (var gcm = new AesGcm(key, AccountSettings.EXPORT_TAG_BYTE_LENGTH))
            {
                gcm.Encrypt(nonce, pkcs8, cipherText, tag, aad);
            }

            var payload = new AccountExportPayload
            {
                Format     = AccountSettings.EXPORT_FORMAT_ID,
                Version    = AccountSettings.EXPORT_FORMAT_VERSION,
                Name       = account.Name,
                PublicKey  = account.PublicKeyBase64Url,
                Kdf        = AccountSettings.EXPORT_KDF_NAME,
                Iterations = AccountSettings.EXPORT_KDF_ITERATIONS,
                Salt       = Base64Url.Encode(salt),
                Nonce      = Base64Url.Encode(nonce),
                CipherText = Base64Url.Encode(cipherText),
                Tag        = Base64Url.Encode(tag),
            };

            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(filePath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (AccountStoreException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AccountStoreException(
                string.Format(CultureInfo.CurrentCulture,
                              AccountMessages.EXPORT_FAILED_FORMAT, ex.Message), ex);
        }
        finally
        {
            if (pkcs8 is not null) CryptographicOperations.ZeroMemory(pkcs8);
            if (key   is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    // ── 読み込み ────────────────────────────────────────────

    /// <summary>
    /// 書き出しファイルをパスフレーズで開く。
    /// </summary>
    /// <param name="filePath">読み込むファイルの絶対パス。</param>
    /// <param name="passphrase">パスフレーズ。</param>
    /// <returns>復元したアカウント（呼び出し側が破棄する）。</returns>
    /// <exception cref="AccountStoreException">
    /// パスフレーズ違い・形式違い・破損のとき。理由は区別して伝えるが、
    /// **パスフレーズ違いと破損は区別しない**（総当たりの手がかりを与えないため）。
    /// </exception>
    public static SeedAccount Import(string filePath, string passphrase)
    {
        AccountExportPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AccountExportPayload>(
                File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new AccountStoreException(AccountMessages.IMPORT_FORMAT_MISMATCH, ex);
        }

        if (payload is null
            || !string.Equals(payload.Format, AccountSettings.EXPORT_FORMAT_ID, StringComparison.Ordinal))
        {
            throw new AccountStoreException(AccountMessages.IMPORT_FORMAT_MISMATCH);
        }

        // 版が新しいファイルは黙って読まない（鍵導出のパラメータが変わっている可能性がある）。
        if (payload.Version > AccountSettings.EXPORT_FORMAT_VERSION)
        {
            throw new AccountStoreException(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.IMPORT_VERSION_UNSUPPORTED_FORMAT, payload.Version));
        }

        byte[]? key   = null;
        byte[]? pkcs8 = null;
        try
        {
            var salt       = Base64Url.Decode(payload.Salt);
            var nonce      = Base64Url.Decode(payload.Nonce);
            var cipherText = Base64Url.Decode(payload.CipherText);
            var tag        = Base64Url.Decode(payload.Tag);

            // 反復回数はファイルの値を使う（将来ここを増やしても古いファイルが読める）。
            // ただし 0 以下なら壊れているので既定値へ落とさず失敗させる。
            if (payload.Iterations <= 0) throw new CryptographicException();

            key   = DeriveKey(passphrase, salt, payload.Iterations);
            pkcs8 = new byte[cipherText.Length];

            var aad = BuildAad(payload.Version, payload.Name, payload.PublicKey);

            using (var gcm = new AesGcm(key, tag.Length))
            {
                // パスフレーズが違う／ファイルが改竄されていると、ここで
                // AuthenticationTagMismatchException（CryptographicException の派生）になる。
                gcm.Decrypt(nonce, cipherText, tag, pkcs8, aad);
            }

            var keyPair = AccountKeyPair.FromPkcs8(pkcs8);

            // 復号できた鍵が、ファイルに書かれた公開鍵と一致するかを必ず確かめる。
            // ここが食い違うファイルは、名前と鍵の対応が壊れている。
            if (!string.Equals(keyPair.PublicKeyBase64Url, payload.PublicKey, StringComparison.Ordinal))
            {
                keyPair.Dispose();
                throw new AccountStoreException(AccountMessages.IMPORT_FAILED);
            }

            return new SeedAccount(payload.Name, keyPair);
        }
        catch (AccountStoreException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // パスフレーズ違い・形式崩れ・鍵の壊れを 1 つの文言に畳む。
            throw new AccountStoreException(AccountMessages.IMPORT_FAILED, ex);
        }
        finally
        {
            if (key   is not null) CryptographicOperations.ZeroMemory(key);
            if (pkcs8 is not null) CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>
    /// パスフレーズが規則を満たすか確かめる。
    /// </summary>
    /// <param name="passphrase">確かめるパスフレーズ。</param>
    /// <exception cref="AccountStoreException">短すぎるとき。</exception>
    public static void CheckPassphrase(string? passphrase)
    {
        if ((passphrase?.Length ?? 0) < AccountSettings.EXPORT_PASSPHRASE_MIN_LENGTH)
        {
            throw new AccountStoreException(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.PASSPHRASE_TOO_SHORT_FORMAT,
                AccountSettings.EXPORT_PASSPHRASE_MIN_LENGTH));
        }
    }

    /// <summary>
    /// パスフレーズから AES の鍵を導出する。
    /// </summary>
    /// <param name="passphrase">パスフレーズ。</param>
    /// <param name="salt">ソルト。</param>
    /// <param name="iterations">反復回数。</param>
    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase ?? string.Empty),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            AccountSettings.EXPORT_KEY_BYTE_LENGTH);

    /// <summary>
    /// 追加認証データ（AAD）を作る。形式・版・名前・公開鍵を暗号文へ縛り付ける。
    ///
    /// <para>
    /// ★版は **ファイルに書かれている値** から作る。現在の版で固定すると、
    /// 将来 v2 を足したときに v1 のファイルが復号できなくなる。
    /// </para>
    /// </summary>
    /// <param name="version">書き出しファイルの形式版。</param>
    /// <param name="name">アカウント名。</param>
    /// <param name="publicKey">公開鍵の base64url。</param>
    private static byte[] BuildAad(int version, string name, string publicKey)
        => Encoding.UTF8.GetBytes(
            AccountSettings.EXPORT_FORMAT_ID
            + AAD_SEPARATOR + AAD_VERSION_MARK
            + version.ToString(CultureInfo.InvariantCulture)
            + AAD_SEPARATOR + (name ?? string.Empty)
            + AAD_SEPARATOR + (publicKey ?? string.Empty));
}
