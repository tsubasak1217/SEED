// ============================================================
//  LauncherIconSettings.cs — プロジェクト設定の android.icon / icon_background から、アイコンの元の画像と背景色を決める（段階D）
//
//  【決まり】
//    icon            … アセットルートからの相対パスか絶対パス（project_settings.json の他のパスと同じ基準）。PNG だけ。
//                      空なら「アイコンを作らない」（システムの既定のアイコン。従来どおり）
//    icon_background … #RRGGBB / #AARRGGBB（Android の色の書式）。空なら白
//  値の検査はビルド（中核）とプロジェクト設定ウィンドウの保存で同じ関数（Validate）を使う。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Icons;

/// <summary>決まったアイコンの元。</summary>
/// <param name="IconPath">元の PNG（絶対パス）。</param>
/// <param name="Background">背景色。</param>
/// <param name="ConfiguredPath">設定に書かれたパス（ログ用）。</param>
public sealed record LauncherIconSource(string IconPath, RgbaColor Background, string ConfiguredPath);

/// <summary>アイコンの設定の読み取りと検査。</summary>
public static class LauncherIconSettings
{
    /// <summary>元の画像の拡張子（PNG だけを読む）。</summary>
    public const string PngExtension = ".png";

    /// <summary>アセットルートからのパスを表す接頭辞（project_settings.json の start_scene と同じ書き方も受け付ける）。</summary>
    public const string AssetsSchemePrefix = "assets://";

    /// <summary>
    /// アイコンの元を決める（icon が空なら null ＝ アイコンを作らない）。
    /// </summary>
    /// <param name="android">"android" 節（無ければ null）。</param>
    /// <param name="assetsRoot">アセットルート（相対パスの基準。無ければ null）。</param>
    /// <returns>アイコンの元（作らないなら null）。</returns>
    /// <exception cref="AndroidPipelineException">設定の誤り（ファイルが無い・PNG でない・色の書式）。</exception>
    public static LauncherIconSource? Resolve(AndroidAppSettings? android, string? assetsRoot)
    {
        var errors = Validate(android, assetsRoot);
        if (errors.Count > 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                "プロジェクト設定の Android のアイコン（project_settings.json の \"android\"）に誤りがあります: " + string.Join(" ", errors));
        }
        var configured = AndroidAppSettings.NormalizeText(android?.Icon);
        if (configured is null) return null;
        var background = RgbaColor.White;
        if (AndroidAppSettings.NormalizeText(android?.IconBackground) is { } text) RgbaColor.TryParse(text, out background);
        return new LauncherIconSource(ResolvePath(configured, assetsRoot), background, configured);
    }

    /// <summary>
    /// 設定の検査（ビルドとプロジェクト設定ウィンドウの保存で共通。誤りが無ければ空）。
    /// </summary>
    /// <param name="android">"android" 節（無ければ null）。</param>
    /// <param name="assetsRoot">アセットルート（無ければ null。相対パスはカレントフォルダから）。</param>
    /// <returns>誤りの説明。</returns>
    public static IReadOnlyList<string> Validate(AndroidAppSettings? android, string? assetsRoot)
    {
        var errors = new List<string>();
        var background = AndroidAppSettings.NormalizeText(android?.IconBackground);
        if (background is not null && !RgbaColor.TryParse(background, out _))
        {
            errors.Add($"アイコンの背景色「{background}」は #RRGGBB か #AARRGGBB で書いてください（例 #FFFFFF）。");
        }
        var configured = AndroidAppSettings.NormalizeText(android?.Icon);
        if (configured is null) return errors;

        string path;
        try
        {
            path = ResolvePath(configured, assetsRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"アイコンのパス「{configured}」を読めません: {ex.Message}");
            return errors;
        }
        if (!string.Equals(Path.GetExtension(path), PngExtension, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"アイコン「{configured}」は PNG にしてください（{PngExtension}）。");
        }
        else if (!File.Exists(path))
        {
            errors.Add($"アイコンの PNG がありません: {path}（アセットルートからの相対パスか絶対パスで書きます）。");
        }
        return errors;
    }

    /// <summary>
    /// 書かれたパスを絶対パスにする（assets:// と相対パスはアセットルートから。アセットルートが無ければカレントフォルダから）。
    /// </summary>
    /// <param name="configured">書かれたパス。</param>
    /// <param name="assetsRoot">アセットルート（無ければ null）。</param>
    /// <returns>絶対パス。</returns>
    public static string ResolvePath(string configured, string? assetsRoot)
    {
        var trimmed = configured.Trim();
        if (trimmed.StartsWith(AssetsSchemePrefix, StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[AssetsSchemePrefix.Length..];
        return Path.IsPathRooted(trimmed) || assetsRoot is null
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(Path.Combine(assetsRoot, trimmed));
    }
}
