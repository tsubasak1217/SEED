// ============================================================
//  AccountRecord.cs — account.json の中身（ディスク上の形）
//
//  【役割】
//  保存形式（JSON）と、メモリ上のモデル（SeedAccount）を分ける。
//  ディスクの形をモデルへ直接持ち込むと、保存形式を変えるたびに
//  署名やログインの側まで巻き込まれる。
//
//  【保存する内容】
//    version                 … 形式の版（読み込み時に見る）
//    name                    … アカウント名
//    public_key              … SEC1 非圧縮点の base64url
//    protected_private_key   … PKCS#8 を DPAPI で包んだものの base64（標準 base64）
//    created_at              … 作成日時（診断用。ログインには使わない）
//
//  ★`protected_private_key` は **DPAPI で包んだ後の値**。ここへ平文を入れない。
//  ★base64url ではなく標準 base64 を使う。URL に載せる値ではないし、
//    このファイルは SEED のエディタしか読まないため。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System.Text.Json.Serialization;

namespace SEEDEditor.Accounts.Storage;

/// <summary>
/// `account.json` に書く内容。
/// </summary>
public sealed class AccountRecord
{
    /// <summary>現在の保存形式の版。</summary>
    public const int CURRENT_VERSION = 1;

    /// <summary>保存形式の版。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = CURRENT_VERSION;

    /// <summary>アカウント名。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>公開鍵（SEC1 非圧縮点）の base64url。</summary>
    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>PKCS#8 を DPAPI で包んだもの（標準 base64）。</summary>
    [JsonPropertyName("protected_private_key")]
    public string ProtectedPrivateKey { get; set; } = string.Empty;

    /// <summary>作成日時（ISO 8601 / UTC）。診断用。</summary>
    [JsonPropertyName("created_at")]
    public string CreatedAtUtc { get; set; } = string.Empty;
}
