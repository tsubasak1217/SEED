// ============================================================
//  AndroidAssetOverlayState.cs — 端末の上書き層（files/assets）へ送ったアセットの記録（実行中の差し替え。docs/android.md §23）
//
//  【何のためか】
//  差し替えで「変わったアセットだけ」を送るには、端末がいま持っている中身を知る必要がある。端末の中身は
//    上書き層 files/assets に送ったもの（ここに記録する。相対パス → 送った中身の SHA-256）
//    それ以外は APK の pak（run のときに入れたもの。pak のエントリの中身は HotReload/AndroidPakContentIndex で調べる）
//  の 2 段なので、送った記録と、「記録の土台にした pak」の目印（大きさ・更新時刻）を端末ごとに持つ。
//
//  【置き場】<プロジェクト>/cache/android/asset_overlay.json（run_state.json とは別のファイル。エディタの実行中は中核が
//  run_state.json を持ったまま最後に書き戻すので、同じファイルに書くと差し替えの記録が消される）。
//  プロジェクトが無いときは runtime/android/app/build/seed/asset_overlay.json。
//
//  【いつ変わるか】
//    run の起動の工程（Steps/LaunchStep）… 端末の files/assets を消したので、その端末の記録を「送ったもの無し・今の pak」にする
//    差し替えで送った（HotReload/AndroidAssetOverlaySync）… 送ったアセットを足す
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Android.State;

/// <summary>端末 1 台の上書き層の記録。</summary>
public sealed record AndroidAssetOverlayRecord
{
    /// <summary>アプリ ID（違うアプリの記録は使わない）。</summary>
    [JsonPropertyName("application_id")] public required string ApplicationId { get; init; }

    /// <summary>記録の土台にした APK の pak の大きさ（バイト。pak が無ければ null）。</summary>
    [JsonPropertyName("pak_size")] public long? PakSize { get; init; }

    /// <summary>記録の土台にした APK の pak の更新時刻（UTC。pak が無ければ null）。</summary>
    [JsonPropertyName("pak_write_time_utc")] public DateTime? PakWriteTimeUtc { get; init; }

    /// <summary>上書き層へ送ったアセット（アセットルートからの相対パス → 送った中身の SHA-256）。</summary>
    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最後に更新した日時。</summary>
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>端末ごとの上書き層の記録（プロジェクトごとの 1 ファイル）。</summary>
public sealed class AndroidAssetOverlayState
{
    /// <summary>記録の書式の版（変えたら上げる。違う版は読まない）。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>プロジェクトの cache/ の中の置き場。</summary>
    public static readonly string ProjectRelativePath = Path.Combine("cache", "android", "asset_overlay.json");

    /// <summary>プロジェクトが無いときのファイル名（エンジン側の作業フォルダの中）。</summary>
    private const string FallbackFileName = "asset_overlay.json";

    /// <summary>書き出しの設定（人が読める整形。日本語のパスをエスケープしない）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>書式の版。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>端末のシリアル → 記録。</summary>
    [JsonPropertyName("devices")]
    public Dictionary<string, AndroidAssetOverlayRecord> Devices { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 記録のファイルの場所を決める（プロジェクトがあればその cache/、無ければエンジン側の作業フォルダ）。
    /// </summary>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <returns>記録のファイル。</returns>
    public static string PathFor(AndroidProjectInfo? project, AndroidEnginePaths engine) =>
        project is not null
            ? Path.Combine(project.Folder.ProjectRoot, ProjectRelativePath)
            : Path.Combine(engine.WorkDir, FallbackFileName);

    /// <summary>
    /// その端末・そのアプリの記録（アプリ ID が違えば無いものとする）。
    /// </summary>
    /// <param name="serial">シリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <returns>記録（無ければ null）。</returns>
    public AndroidAssetOverlayRecord? RecordFor(string serial, string applicationId) =>
        Devices.TryGetValue(serial, out var record) && string.Equals(record.ApplicationId, applicationId, StringComparison.Ordinal)
            ? record
            : null;

    /// <summary>
    /// 記録を読む（無い・壊れている・版が違うときは空の記録）。
    /// </summary>
    /// <param name="path">記録のファイル。</param>
    /// <returns>記録。</returns>
    public static AndroidAssetOverlayState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AndroidAssetOverlayState();
            var loaded = JsonSerializer.Deserialize<AndroidAssetOverlayState>(File.ReadAllText(path), JsonOptions);
            if (loaded is not { FormatVersion: CurrentFormatVersion }) return new AndroidAssetOverlayState();
            // JSON から作った辞書は大文字小文字を区別するので、区別しない辞書へ詰め直す（パスは大文字小文字を問わずに引く）
            foreach (var serial in loaded.Devices.Keys.ToList())
            {
                var record = loaded.Devices[serial];
                var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (relative, digest) in record.Files) files[relative] = digest;
                loaded.Devices[serial] = record with { Files = files };
            }
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AndroidAssetOverlayState();
        }
    }

    /// <summary>記録を書く（一時ファイルへ書き切ってから置き換える）。</summary>
    /// <param name="path">記録のファイル。</param>
    public void Save(string path) => AtomicJsonFile.Write(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>
    /// pak のファイルの目印（大きさと更新時刻）を読む（無ければ両方 null）。
    /// </summary>
    /// <param name="pakPath">pak のパス。</param>
    /// <returns>（大きさ, 更新時刻 UTC）。</returns>
    public static (long? Size, DateTime? WriteTimeUtc) PakIdentity(string pakPath)
    {
        var info = new FileInfo(pakPath);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (null, null);
    }
}
