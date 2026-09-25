// ============================================================
//  AndroidSigningSecretStore.cs — 配布用（release）の署名のパスワードのエディタ設定への保護保存（段階D。docs/android.md §24）
//
//  【置き場】editor/settings/android_signing_secrets.json（editor/settings/ はリポジトリで追跡しない。EditorPaths.SettingsDir）
//  【中身】キーストア（絶対パス。大文字小文字を区別しない）× 別名ごとに、パスワードを ISecretProtector で包んだものを
//         Base64 で持つ。本番の包み方は DPAPI（CurrentUser。AndroidSigningDpapiProtector）なので、この PC のこの Windows
//         ユーザーでしか解けない（ファイルを写しても別の PC・別のユーザーでは使えない）。アカウントの秘密鍵の保存
//         （Accounts/Storage の DpapiSecretProtector）と同じ流儀で、混ぜないよう追加エントロピーは別の文字列にしている。
//  【決まり】パスワードをプロジェクトのファイル（packaging_settings.json）・ログに書かない。解けない記録（別の PC で包んだ等）は
//         「無い」として扱い、利用者に入れ直してもらう。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests から偽の ISecretProtector でリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Accounts.Storage;
using SEEDEditor.Android.Signing;

namespace SEEDEditor.Settings;

/// <summary>保存した 1 件（パスワードは包んだもの）。</summary>
public sealed class AndroidSigningSecretEntry
{
    /// <summary>キーストア（絶対パス）。</summary>
    [JsonPropertyName("keystore")] public string Keystore { get; set; } = "";

    /// <summary>キーの別名。</summary>
    [JsonPropertyName("alias")] public string Alias { get; set; } = "";

    /// <summary>キーストアのパスワード（包んだものの Base64）。</summary>
    [JsonPropertyName("store_password")] public string StorePassword { get; set; } = "";

    /// <summary>キーのパスワード（包んだものの Base64。キーストアと同じなら null）。</summary>
    [JsonPropertyName("key_password")] public string? KeyPassword { get; set; }

    /// <summary>保存した日時。</summary>
    [JsonPropertyName("saved_at")] public DateTimeOffset SavedAt { get; set; }
}

/// <summary>署名のパスワードの保護保存。</summary>
public sealed class AndroidSigningSecretStore
{
    /// <summary>保存のファイル名（editor/settings の中）。</summary>
    public const string FileName = "android_signing_secrets.json";

    /// <summary>エディタの保護保存から来たパスワードの出どころの表示。</summary>
    public const string Origin = "エディタの保護保存（この PC のこの Windows ユーザーだけが解ける）";

    /// <summary>書式の版。</summary>
    private const int CurrentFormatVersion = 1;

    /// <summary>書き出しの設定。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>保存のファイル。</summary>
    private readonly string _path;

    /// <summary>包み方。</summary>
    private readonly ISecretProtector _protector;

    /// <summary>
    /// 保存のファイルと包み方を指定して作る。
    /// </summary>
    /// <param name="path">保存のファイル（本番は EditorPaths.SettingsDir の <see cref="FileName"/>）。</param>
    /// <param name="protector">包み方（本番は AndroidSigningDpapiProtector）。</param>
    public AndroidSigningSecretStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    /// <summary>ファイルの中身。</summary>
    private sealed class Document
    {
        [JsonPropertyName("format_version")] public int FormatVersion { get; set; } = CurrentFormatVersion;

        [JsonPropertyName("entries")] public List<AndroidSigningSecretEntry> Entries { get; set; } = new();
    }

    /// <summary>そのキーストア・別名のパスワードを保存しているか。</summary>
    /// <param name="keystorePath">キーストア。</param>
    /// <param name="alias">別名。</param>
    /// <returns>保存していれば true。</returns>
    public bool Has(string keystorePath, string alias) => Find(Read(), keystorePath, alias) is not null;

    /// <summary>
    /// パスワードを取り出す（無い・解けないときは null）。
    /// </summary>
    /// <param name="keystorePath">キーストア。</param>
    /// <param name="alias">別名。</param>
    /// <returns>パスワード。</returns>
    public AndroidSigningSecrets? Load(string keystorePath, string alias)
    {
        var entry = Find(Read(), keystorePath, alias);
        if (entry is null) return null;
        try
        {
            var store = Decode(entry.StorePassword);
            var key = entry.KeyPassword is null ? null : Decode(entry.KeyPassword);
            return string.IsNullOrEmpty(store) ? null : new AndroidSigningSecrets(store, key, Origin);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // 別の PC・別のユーザーで包んだもの等。無いものとして入れ直してもらう
            return null;
        }
    }

    /// <summary>
    /// パスワードを保存する（同じキーストア・別名の前の記録は置き換える）。
    /// </summary>
    /// <param name="keystorePath">キーストア。</param>
    /// <param name="alias">別名。</param>
    /// <param name="secrets">パスワード。</param>
    public void Save(string keystorePath, string alias, AndroidSigningSecrets secrets)
    {
        var document = Read();
        document.Entries.RemoveAll(entry => Matches(entry, keystorePath, alias));
        document.Entries.Add(new AndroidSigningSecretEntry
        {
            Keystore = Normalize(keystorePath),
            Alias = alias.Trim(),
            StorePassword = Encode(secrets.KeystorePassword),
            KeyPassword = secrets.KeyPassword == secrets.KeystorePassword ? null : Encode(secrets.KeyPassword),
            SavedAt = DateTimeOffset.Now,
        });
        Write(document);
    }

    /// <summary>
    /// パスワードの記録を消す（無ければ何もしない）。
    /// </summary>
    /// <param name="keystorePath">キーストア。</param>
    /// <param name="alias">別名。</param>
    /// <returns>消したら true。</returns>
    public bool Remove(string keystorePath, string alias)
    {
        var document = Read();
        var removed = document.Entries.RemoveAll(entry => Matches(entry, keystorePath, alias)) > 0;
        if (removed) Write(document);
        return removed;
    }

    /// <summary>ファイルを読む（無い・壊れている・版が違うときは空）。</summary>
    private Document Read()
    {
        try
        {
            if (!File.Exists(_path)) return new Document();
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions);
            return document is { FormatVersion: CurrentFormatVersion } ? document : new Document();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Document();
        }
    }

    /// <summary>ファイルへ書く（一時ファイルへ書き切ってから置き換える）。</summary>
    private void Write(Document document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }

    /// <summary>キーストア・別名の記録を探す。</summary>
    private static AndroidSigningSecretEntry? Find(Document document, string keystorePath, string alias) =>
        document.Entries.FirstOrDefault(entry => Matches(entry, keystorePath, alias));

    /// <summary>記録がキーストア・別名に当たるか（パスは大文字小文字を区別しない。別名は区別しない＝keytool と同じ）。</summary>
    private static bool Matches(AndroidSigningSecretEntry entry, string keystorePath, string alias) =>
        string.Equals(entry.Keystore, Normalize(keystorePath), StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Alias, alias.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>パスをそろえる（絶対パス）。</summary>
    private static string Normalize(string keystorePath) => Path.GetFullPath(keystorePath.Trim());

    /// <summary>平文を包んで Base64 にする。</summary>
    private string Encode(string secret) => Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Base64 を解いて平文にする。</summary>
    private string Decode(string encoded) => Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(encoded)));
}
