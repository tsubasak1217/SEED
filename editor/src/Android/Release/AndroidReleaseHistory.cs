// ============================================================
//  AndroidReleaseHistory.cs — 配布用（release）のビルドの記録（アプリ ID・形式ごとの最後の versionCode。段階D）
//
//  【目的】要件チェックの「versionCode の単調増加」の材料。Google Play は同じ・小さい versionCode の更新を受け付けず、
//  端末も小さい versionCode への上書きを拒む。前回の配布用ビルドより小さければ不合格、同じなら注意にする
//  （同じ版を作り直すことはあるので止めない）。記録は配布用のビルドに成功したときに書く。
//
//  【置き場】&lt;プロジェクト&gt;/cache/android/release_history.json（run_state.json と同じ cache/android/。プロジェクトが無ければ
//  runtime/android/app/build/seed/release_history.json）。失っても「記録なし」と知らせるだけ（本当の正は Google Play Console の
//  最後にアップロードした versionCode）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Release;

/// <summary>1 回の配布用ビルドの記録。</summary>
public sealed record AndroidReleaseRecord
{
    /// <summary>versionCode。</summary>
    [JsonPropertyName("version_code")] public required int VersionCode { get; init; }

    /// <summary>versionName。</summary>
    [JsonPropertyName("version_name")] public string? VersionName { get; init; }

    /// <summary>作った日時。</summary>
    [JsonPropertyName("built_at")] public DateTimeOffset BuiltAt { get; init; }

    /// <summary>配布物の SHA-256（16 進の小文字）。</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
}

/// <summary>配布用ビルドの記録。</summary>
public sealed class AndroidReleaseHistory
{
    /// <summary>記録の書式の版（変えたら上げる。違う版は読まない）。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>プロジェクトのルートからの置き場。</summary>
    public static readonly string ProjectRelativePath = Path.Combine("cache", "android", "release_history.json");

    /// <summary>プロジェクトが無いときの置き場のファイル名（エンジンの作業フォルダの中）。</summary>
    private const string FallbackFileName = "release_history.json";

    /// <summary>書き出しの設定（人が読める整形）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>書式の版。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>アプリ ID → 形式（apk / aab）→ 最後の配布用ビルド。</summary>
    [JsonPropertyName("releases")]
    public Dictionary<string, Dictionary<string, AndroidReleaseRecord>> Releases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 記録の置き場（プロジェクトがあれば cache/android/、無ければエンジンの作業フォルダ）。
    /// </summary>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <returns>ファイルのパス。</returns>
    public static string PathFor(AndroidProjectInfo? project, AndroidEnginePaths engine) =>
        project is not null
            ? Path.Combine(project.Folder.ProjectRoot, ProjectRelativePath)
            : Path.Combine(engine.WorkDir, FallbackFileName);

    /// <summary>形式の記録のキー（apk / aab）。</summary>
    /// <param name="format">形式。</param>
    /// <returns>キー。</returns>
    public static string FormatKey(AndroidPackageFormat format) => format.ToString().ToLowerInvariant();

    /// <summary>
    /// そのアプリ・形式の最後の記録（無ければ null）。
    /// </summary>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="format">形式。</param>
    /// <returns>記録。</returns>
    public AndroidReleaseRecord? Last(string applicationId, AndroidPackageFormat format) =>
        Releases.TryGetValue(applicationId, out var byFormat) && byFormat.TryGetValue(FormatKey(format), out var record) ? record : null;

    /// <summary>
    /// 記録を足す（同じアプリ・形式の前の記録を置き換える）。
    /// </summary>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="format">形式。</param>
    /// <param name="record">記録。</param>
    public void Record(string applicationId, AndroidPackageFormat format, AndroidReleaseRecord record)
    {
        if (!Releases.TryGetValue(applicationId, out var byFormat))
        {
            byFormat = new Dictionary<string, AndroidReleaseRecord>(StringComparer.Ordinal);
            Releases[applicationId] = byFormat;
        }
        byFormat[FormatKey(format)] = record;
    }

    /// <summary>
    /// 記録を読む（無い・壊れている・版が違うときは空の記録）。
    /// </summary>
    /// <param name="path">記録のファイル。</param>
    /// <returns>記録。</returns>
    public static AndroidReleaseHistory Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AndroidReleaseHistory();
            var loaded = JsonSerializer.Deserialize<AndroidReleaseHistory>(File.ReadAllText(path), JsonOptions);
            return loaded is { FormatVersion: CurrentFormatVersion } ? loaded : new AndroidReleaseHistory();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AndroidReleaseHistory();
        }
    }

    /// <summary>記録を書く（一時ファイルへ書き切ってから置き換える）。</summary>
    /// <param name="path">記録のファイル。</param>
    public void Save(string path) => AtomicJsonFile.Write(path, JsonSerializer.Serialize(this, JsonOptions));
}
