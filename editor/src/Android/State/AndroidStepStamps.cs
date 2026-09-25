// ============================================================
//  AndroidStepStamps.cs — Gradle の置き場に「いま何が入っているか」の記録（エンジン側・置き場の隣）
//
//  【なぜプロジェクトの cache/ ではなく置き場の隣か】
//  cargo ndk の .so（app/src/main/jniLibs）・SeedPak の pak（app/src/main/assets/seed）・同梱 .NET（app/src/seedDotnet）・
//  APK（app/build/outputs）の置き場はリポジトリに 1 つずつしかなく、別のプロジェクトをビルドすると中身が入れ替わる。
//  「この置き場の中身は、どの入力から作ったか」は置き場と一緒に持たないと、プロジェクトを切り替えたときに
//  別のプロジェクトの pak のまま「変更なし」と判断してしまう。プロジェクト側の記録（前回の実行先・前回の指紋）は
//  AndroidRunState（&lt;プロジェクト&gt;/cache/android/run_state.json）。
//  置き場は runtime/android/app/build/seed/step_stamps.json（gradlew clean で消える。消えたら全部作り直すだけ）。
//
//  壊れた・版の違う記録は「記録なし」として扱う（作り直すだけで安全）。書き込みは一時ファイルからの置き換え。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Android.Plan;

namespace SEEDEditor.Android.State;

/// <summary>前回作った APK の記録。</summary>
/// <param name="Identity">APK の同一性（大きさ:更新時刻。SHA-256 を計算し直さずに済ませるため）。</param>
/// <param name="Sha256">APK の SHA-256。</param>
/// <param name="Abis">詰めた ABI。</param>
/// <param name="ApplicationId">アプリ ID。</param>
public sealed record AndroidApkStamp(
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("abis")] IReadOnlyList<string> Abis,
    [property: JsonPropertyName("application_id")] string ApplicationId);

/// <summary>置き場の中身の記録。</summary>
public sealed class AndroidStepStamps
{
    /// <summary>記録の書式の版（変えたら上げる。違う版は読まない）。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>書き出しの設定（人が読める整形。パスの日本語等をエスケープしない）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>書式の版。</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>工程のキー → 最後に成功したときの入力の指紋と出力の同一性。</summary>
    [JsonPropertyName("steps")]
    public Dictionary<string, AndroidStepFingerprint> Steps { get; set; } = new(StringComparer.Ordinal);

    /// <summary>最後に作った APK（開発用＝debug の APK）。</summary>
    [JsonPropertyName("apk")]
    public AndroidApkStamp? Apk { get; set; }

    /// <summary>
    /// 最後に作った配布用（release）の APK / AAB（キーは Plan/AndroidStepKeys.GradleFor。段階D。古い記録には無い＝空）。
    /// </summary>
    [JsonPropertyName("artifacts")]
    public Dictionary<string, AndroidApkStamp> Artifacts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// その Gradle の出力のキーの配布物の記録（debug の APK は <see cref="Apk"/>。無ければ null）。
    /// </summary>
    /// <param name="gradleKey">Plan/AndroidStepKeys.GradleFor のキー。</param>
    /// <returns>記録。</returns>
    public AndroidApkStamp? ArtifactFor(string gradleKey) =>
        gradleKey == AndroidStepKeys.Gradle ? Apk : Artifacts.GetValueOrDefault(gradleKey);

    /// <summary>
    /// その Gradle の出力のキーの配布物の記録を書く（debug の APK は <see cref="Apk"/>）。
    /// </summary>
    /// <param name="gradleKey">Plan/AndroidStepKeys.GradleFor のキー。</param>
    /// <param name="stamp">記録。</param>
    public void SetArtifact(string gradleKey, AndroidApkStamp stamp)
    {
        if (gradleKey == AndroidStepKeys.Gradle) Apk = stamp;
        else Artifacts[gradleKey] = stamp;
    }

    /// <summary>
    /// 記録を読む（無い・壊れている・版が違うときは空の記録）。
    /// </summary>
    /// <param name="path">記録のファイル。</param>
    /// <returns>記録。</returns>
    public static AndroidStepStamps Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AndroidStepStamps();
            var loaded = JsonSerializer.Deserialize<AndroidStepStamps>(File.ReadAllText(path), JsonOptions);
            if (loaded is not { FormatVersion: CurrentFormatVersion }) return new AndroidStepStamps();
            // 段階D より前の記録には artifacts が無い（null で読まれる）
            loaded.Artifacts ??= new Dictionary<string, AndroidApkStamp>(StringComparer.Ordinal);
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AndroidStepStamps();
        }
    }

    /// <summary>
    /// 記録を書く（一時ファイルへ書き切ってから置き換える）。
    /// </summary>
    /// <param name="path">記録のファイル。</param>
    public void Save(string path) => AtomicJsonFile.Write(path, JsonSerializer.Serialize(this, JsonOptions));
}

/// <summary>JSON の記録ファイルを原子的に書く（一時ファイル → 置き換え）。</summary>
internal static class AtomicJsonFile
{
    /// <summary>一時ファイルの接尾辞。</summary>
    private const string TemporarySuffix = ".tmp";

    /// <summary>書く。</summary>
    /// <param name="path">書き先。</param>
    /// <param name="json">中身。</param>
    public static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + TemporarySuffix;
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
