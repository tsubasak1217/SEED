// ============================================================
//  AndroidScenePath.cs — 端末で起動するシーンの指定を「アセットルートからの相対パス」に揃える（純粋な処理）
//
//  【受け付ける形】（エディタの「開いているシーン」・SeedAndroid の --scene・設定 JSON の "scene"）
//    scenes/Main.scene                 … アセットルートからの相対パス（そのまま。\ は / にする）
//    assets://scenes/Main.scene        … エンジンの仮想パス（assets:// を外す）
//    D:\Game\assets\scenes\Main.scene  … アセットルートの中の絶対パス（相対に直す。エディタの現在のシーンはこの形）
//  アセットルートの外の絶対パス・.. を含むパス・空は受け付けない（APK の pak に入らない・端末で読めないため）。
//
//  【端末側】
//  揃えた相対パスは am start の extra（seed.scene）で端末へ渡り、ネイティブ（runtime/android/native/src/launch.rs）が
//  assets://<相対パス> にして LaunchArgs.scene_path へ入れる（pak に無ければ logcat に警告を出して開始シーンで起動する）。
//
//  WPF に依存しない（コンソールツール・エディタ・単体テストからリンクされる）。
// ============================================================

using System;
using System.IO;
using System.Linq;

namespace SEEDEditor.Android.Project;

/// <summary>シーンの指定を揃えた結果。</summary>
/// <param name="Relative">アセットルートからの相対パス（/ 区切り。指定なし・誤りなら null）。</param>
/// <param name="Error">受け付けない理由（誤りのときだけ）。</param>
public sealed record AndroidScenePathResult(string? Relative, string? Error)
{
    /// <summary>指定なし（開始シーンで起動する）。</summary>
    public static readonly AndroidScenePathResult None = new(null, null);
}

/// <summary>起動するシーンの指定を揃える。</summary>
public static class AndroidScenePath
{
    /// <summary>エンジンの仮想パスの接頭辞（runtime/src/engine/asset_fs.rs の ASSETS_SCHEME と同じ）。</summary>
    public const string AssetsScheme = "assets://";

    /// <summary>相対パスの区切り（端末・pak のエントリと同じ）。</summary>
    private const char RelativeSeparator = '/';

    /// <summary>親フォルダを表す区切り（アセットルートの外へ出るので受け付けない）。</summary>
    private const string ParentSegment = "..";

    /// <summary>今のフォルダを表す区切り（読み飛ばす）。</summary>
    private const string CurrentSegment = ".";

    /// <summary>指定を囲んでいることがある引用符（コマンドラインからの貼り付け）。</summary>
    private const char QuoteChar = '"';

    /// <summary>
    /// シーンの指定をアセットルートからの相対パスに揃える。
    /// </summary>
    /// <param name="requested">指定（null・空なら指定なし）。</param>
    /// <param name="assetsRoot">プロジェクトのアセットルート（絶対パスの指定を相対に直すのに使う。無ければ null）。</param>
    /// <returns>揃えた結果。</returns>
    public static AndroidScenePathResult Normalize(string? requested, string? assetsRoot)
    {
        if (string.IsNullOrWhiteSpace(requested)) return AndroidScenePathResult.None;
        var value = requested.Trim().Trim(QuoteChar).Trim();

        if (value.StartsWith(AssetsScheme, StringComparison.OrdinalIgnoreCase))
        {
            value = value[AssetsScheme.Length..];
        }
        else if (Path.IsPathFullyQualified(value))
        {
            if (string.IsNullOrWhiteSpace(assetsRoot))
            {
                return Error($"シーン {requested} は、プロジェクトが無いのでアセットフォルダからの相対パスで指定してください（例 scenes/Main.scene）。");
            }
            var relative = Path.GetRelativePath(Path.GetFullPath(assetsRoot), Path.GetFullPath(value));
            if (Path.IsPathRooted(relative) || FirstSegment(relative) == ParentSegment)
            {
                return Error($"シーン {requested} はアセットフォルダ（{assetsRoot}）の外にあります（APK に入らないため、端末では開けません）。");
            }
            value = relative;
        }

        var segments = value.Replace('\\', RelativeSeparator)
            .Split(RelativeSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment != CurrentSegment)
            .ToList();
        if (segments.Contains(ParentSegment))
        {
            return Error($"シーンのパスに {ParentSegment} は使えません（アセットフォルダの中のパスで指定してください）: {requested}");
        }
        if (segments.Count == 0) return Error($"シーンのパスが空です: {requested}");
        return new AndroidScenePathResult(string.Join(RelativeSeparator, segments), null);
    }

    /// <summary>相対パスからエンジンの仮想パス（assets://…）を作る。</summary>
    /// <param name="relative">アセットルートからの相対パス。</param>
    /// <returns>仮想パス。</returns>
    public static string ToVirtualPath(string relative) => AssetsScheme + relative;

    /// <summary>
    /// 相対パスのファイルがアセットルートにあるか（PC 側の事前の確かめ。無くても端末へは渡し、端末が警告して開始シーンで起動する）。
    /// </summary>
    /// <param name="assetsRoot">アセットルート。</param>
    /// <param name="relative">アセットルートからの相対パス。</param>
    /// <returns>あれば true。</returns>
    public static bool ExistsUnder(string assetsRoot, string relative) =>
        File.Exists(Path.Combine(assetsRoot, relative.Replace(RelativeSeparator, Path.DirectorySeparatorChar)));

    /// <summary>パスの最初の区切り（/ と \ のどちらでも区切る）。</summary>
    private static string FirstSegment(string path) =>
        path.Split(new[] { '\\', RelativeSeparator }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    /// <summary>受け付けない結果。</summary>
    private static AndroidScenePathResult Error(string message) => new(null, message);
}
