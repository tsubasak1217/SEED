// ============================================================
//  AndroidProjectSettingsReader.cs — project_settings.json から APK に焼き込む値だけを読む
//
//  【読むもの】
//    screen_orientation … 画面の向き（both / portrait / landscape。値の表はエディタの ScreenOrientationSetting）
//    android            … アプリの識別情報（AndroidAppSettings）
//    start_scene / scenes[].path … シーンマネージャに登録したシーン（段階C-4。APK の pak の収録の起点と同じもの。
//                                  起動するシーンが未登録なら pak の起点に足す判断に使う。Project/AndroidPakSceneSeeds）
//  エディタの ProjectSettingsData（形式の変換・保存まで持つ）は使わず、JSON から必要なキーだけを読む
//  （ビルドではファイルを書き換えないし、古い形式の変換にランタイムの exe を起こす必要も無い）。
//  ファイルが無い・JSON として読めないときは既定値（警告付き）。従来の build_and_run.ps1 と同じ扱い。
//
//  【画面の向きの値】
//  知らない値は警告を出して既定値（both）にする。Gradle へは正規化した値だけを渡す
//  （変換表 値 → マニフェストの screenOrientation は runtime/android/app/build.gradle.kts の 1 か所）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Project;

/// <summary>project_settings.json から読んだ、APK に焼き込む値。</summary>
/// <param name="SettingsPath">読んだ（読もうとした）ファイル。</param>
/// <param name="Found">ファイルがあったか。</param>
/// <param name="ScreenOrientation">画面の向き（正規化済み。ScreenOrientationSetting.Choices のどれか）。</param>
/// <param name="Android">"android" 節（無ければ null）。</param>
/// <param name="Warnings">読み取りの警告（壊れた JSON・知らない値）。</param>
public sealed record AndroidProjectSettings(
    string SettingsPath,
    bool Found,
    string ScreenOrientation,
    AndroidAppSettings? Android,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// シーンマネージャに登録したシーン（start_scene と scenes[].path。書かれた表記のまま・書かれた順。無ければ空）。
    /// APK の pak はこれらを起点に参照をたどって作る（Packaging/Collect/AssetCollector と同じキー）ので、
    /// 起動するシーンを pak の収録の起点に足すかの判断に使う（Project/AndroidPakSceneSeeds。段階C-4）。
    /// </summary>
    public IReadOnlyList<string> RegisteredScenes { get; init; } = Array.Empty<string>();
}

/// <summary>project_settings.json から APK に焼き込む値だけを読む。</summary>
public static class AndroidProjectSettingsReader
{
    /// <summary>画面の向きのキー（エディタの ProjectSettingsData.ScreenOrientation と同じ）。</summary>
    public const string ScreenOrientationKey = "screen_orientation";

    /// <summary>開始シーンのキー（エディタの ProjectSettingsData・AssetCollector と同じ）。</summary>
    public const string StartSceneKey = "start_scene";

    /// <summary>シーン一覧のキー（配列。要素は name / path を持つオブジェクト）。</summary>
    public const string ScenesKey = "scenes";

    /// <summary>シーン一覧の要素のうち、シーンのパスのキー。</summary>
    public const string ScenePathKey = "path";

    /// <summary>
    /// アセットルートの project_settings.json を読む（アセットルートが無ければ既定値）。
    /// </summary>
    /// <param name="assetsRoot">アセットルート（null なら既定値）。</param>
    /// <returns>読んだ値。</returns>
    public static AndroidProjectSettings Read(string? assetsRoot)
    {
        var warnings = new List<string>();
        if (assetsRoot is null)
        {
            return new AndroidProjectSettings(string.Empty, false, ScreenOrientationSetting.Default, null, warnings);
        }

        var path = Path.Combine(assetsRoot, SettingsFileName);
        if (!File.Exists(path))
        {
            return new AndroidProjectSettings(path, false, ScreenOrientationSetting.Default, null, warnings);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                // エディタの読み方（System.Text.Json の既定）より少し寛容にする。手で書いたコメント・末尾のカンマで止めない
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{path} を JSON として読めません（画面の向き・アプリの識別情報は既定値にします）: {ex.Message}");
            return new AndroidProjectSettings(path, true, ScreenOrientationSetting.Default, null, warnings);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{path} の中身がオブジェクトではありません（既定値にします）。");
                return new AndroidProjectSettings(path, true, ScreenOrientationSetting.Default, null, warnings);
            }

            // ── 画面の向き ──
            var orientation = ScreenOrientationSetting.Default;
            if (root.TryGetProperty(ScreenOrientationKey, out var orientationElement)
                && orientationElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(orientationElement.GetString()))
            {
                var raw = orientationElement.GetString()!;
                orientation = ScreenOrientationSetting.Normalize(raw);
                if (!string.Equals(orientation, raw.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                {
                    warnings.Add($"{ScreenOrientationKey}=\"{raw}\" は知らない値です（使える値: {DescribeOrientationChoices()}）。\"{orientation}\" として扱います。");
                }
            }

            // ── アプリの識別情報（エディタと同じ変換器で読む。型の違う値は未設定扱い）──
            AndroidAppSettings? android = null;
            if (root.TryGetProperty(AndroidAppSettings.SectionKey, out var androidElement))
            {
                android = androidElement.Deserialize<AndroidAppSettings>();
            }

            return new AndroidProjectSettings(path, true, orientation, android, warnings)
            {
                RegisteredScenes = ReadRegisteredScenes(root),
            };
        }
    }

    /// <summary>
    /// 登録シーン（start_scene と scenes[].path）を書かれた表記のまま読む（AssetCollector の起点の読み方と同じ。
    /// 文字列でない値・空は読み飛ばす）。
    /// </summary>
    /// <param name="root">project_settings.json のルート（オブジェクト）。</param>
    /// <returns>登録シーンの並び。</returns>
    private static IReadOnlyList<string> ReadRegisteredScenes(JsonElement root)
    {
        var scenes = new List<string>();
        if (root.TryGetProperty(StartSceneKey, out var start) && start.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(start.GetString()))
        {
            scenes.Add(start.GetString()!);
        }
        if (root.TryGetProperty(ScenesKey, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(ScenePathKey, out var scenePath)
                    && scenePath.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(scenePath.GetString()))
                {
                    scenes.Add(scenePath.GetString()!);
                }
            }
        }
        return scenes;
    }

    /// <summary>プロジェクト設定のファイル名。</summary>
    private const string SettingsFileName = SEEDEditor.Project.ProjectFolderResolver.ProjectSettingsFileName;

    /// <summary>画面の向きの選べる値（警告の説明用）。</summary>
    /// <returns>「both / portrait / landscape」。</returns>
    private static string DescribeOrientationChoices() =>
        string.Join(" / ", Array.ConvertAll(ScreenOrientationSetting.Choices, c => c.Value));
}
