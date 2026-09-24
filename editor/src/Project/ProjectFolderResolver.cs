// ============================================================
//  ProjectFolderResolver.cs — 「プロジェクトフォルダ」指定からアセットルートとプロジェクト名を決める
//
//  【役割】
//  コンソールツール（SeedPak の --project、SeedAndroid の --project）が受け取るフォルダから、
//  エディタと同じ導き方でアセットルートを決める唯一の規則。以前は SeedPak（PakInputResolver）と
//  runtime/android/build_and_run.ps1（Resolve-ProjectAssetsRoot）に同じ規則が 2 つあった。
//
//  【規則（上から順に最初に当てはまったもの）】
//    1. フォルダに .seedproj があれば、その assets_dir（エディタが開くときと同じ ProjectPaths の導出）。
//       .seedproj が複数あれば、フォルダ名と同じ名前のもの、無ければ名前順の先頭（SeedProjectFile.FindInDirectory）。
//    2. .seedproj が無ければ <フォルダ>/assets
//    3. それも無く、フォルダ自体に project_settings.json があれば、そのフォルダ（アセットルートそのものを渡された）
//
//  【プロジェクトルート】
//  ランタイムはアセットルートの「親フォルダ」から cache / save を導く（docs/project_system.md）。
//  .seedproj があればそのフォルダ、無ければアセットルートの親をプロジェクトルートとする。
//
//  WPF に一切依存しない（ツール・単体テストからそのままリンクして使える）。
// ============================================================

using System;
using System.IO;

namespace SEEDEditor.Project;

/// <summary>プロジェクトフォルダの解決結果。</summary>
/// <param name="AssetsRoot">アセットルートの絶対パス（.seedproj があるときはエディタの ProjectPaths.AssetsDir と同じ表記）。</param>
/// <param name="ProjectRoot">プロジェクトルートの絶対パス（.seedproj のフォルダ。無ければアセットルートの親）。</param>
/// <param name="ProjectFilePath">使った .seedproj の絶対パス（無ければ null）。</param>
/// <param name="ProjectName">.seedproj の name（無ければ null）。</param>
/// <param name="ProjectDisplayName">.seedproj の表示名（display_name、空なら name。無ければ null）。</param>
/// <param name="Origin">アセットルートをどう決めたか（ログ用の説明）。</param>
public sealed record ResolvedProjectFolder(
    string AssetsRoot,
    string ProjectRoot,
    string? ProjectFilePath,
    string? ProjectName,
    string? ProjectDisplayName,
    string Origin);

/// <summary>プロジェクトフォルダ指定からアセットルートとプロジェクト名を決める。</summary>
public static class ProjectFolderResolver
{
    /// <summary>プロジェクト設定のファイル名（アセットルートの目印。ランタイムが必ず読むファイル）。</summary>
    public const string ProjectSettingsFileName = "project_settings.json";

    /// <summary>
    /// フォルダからアセットルートを決める。決められなければ理由を <paramref name="error"/> に入れて null を返す。
    /// </summary>
    /// <param name="folder">プロジェクトフォルダ（相対可。.seedproj か assets/ を持つフォルダ、またはアセットルートそのもの）。</param>
    /// <param name="error">失敗理由（成功時は null）。</param>
    /// <returns>解決結果。失敗時は null。</returns>
    public static ResolvedProjectFolder? Resolve(string folder, out string? error)
    {
        error = null;
        string root;
        try
        {
            root = NormalizeDirectory(folder);
        }
        catch (Exception ex)
        {
            error = $"プロジェクトフォルダのパスが不正です: {folder}（{ex.Message}）";
            return null;
        }

        if (!Directory.Exists(root))
        {
            error = $"プロジェクトフォルダが見つかりません: {root}";
            return null;
        }

        // ── 1. .seedproj があれば、その assets_dir（エディタが開くときと同じ導出）──
        var projectFile = SeedProjectFile.FindInDirectory(root);
        if (projectFile is not null)
        {
            if (!SeedProjectFile.TryLoad(projectFile, out var file, out var loadError))
            {
                error = loadError ?? $"プロジェクトファイルを読めません: {projectFile}";
                return null;
            }
            // アセットルートの表記はエディタ（ProjectPaths.AssetsDir）と 1 文字も変えない。
            // PAK へ入れるときの絶対パス参照の照合・書き換えがこの表記を基準にするため（AssetPathRewriter）。
            var paths = ProjectPaths.FromProjectFile(projectFile, file!);
            return new ResolvedProjectFolder(
                paths.AssetsDir,
                paths.RootDir,
                paths.ProjectFilePath,
                paths.Name,
                paths.DisplayName,
                $"{Path.GetFileName(projectFile)} の assets_dir");
        }

        // ── 2. .seedproj が無ければ既定の <フォルダ>/assets ──
        var defaultAssets = Path.Combine(root, SeedProjectFile.DEFAULT_ASSETS_DIR);
        if (Directory.Exists(defaultAssets))
        {
            return new ResolvedProjectFolder(
                defaultAssets, root, null, null, null, $"{SeedProjectFile.DEFAULT_ASSETS_DIR}/（.seedproj なし）");
        }

        // ── 3. フォルダ自体がアセットルート（project_settings.json を直下に持つ）──
        if (File.Exists(Path.Combine(root, ProjectSettingsFileName)))
        {
            return new ResolvedProjectFolder(
                root, ParentOrSelf(root), null, null, null, $"指定したフォルダ自体（{ProjectSettingsFileName} あり）");
        }

        error = $"アセットルートを決められません: {root} に .seedproj も {SeedProjectFile.DEFAULT_ASSETS_DIR}/ も " +
                $"{ProjectSettingsFileName} もありません";
        return null;
    }

    /// <summary>
    /// アセットルートを直接指定されたときの解決結果を作る（プロジェクトルートは親フォルダ。親に .seedproj があれば名前も読む）。
    /// </summary>
    /// <param name="assetsRoot">アセットルート（相対可）。</param>
    /// <param name="error">失敗理由（成功時は null）。</param>
    /// <returns>解決結果。フォルダが無ければ null。</returns>
    public static ResolvedProjectFolder? FromAssetsRoot(string assetsRoot, out string? error)
    {
        error = null;
        string root;
        try
        {
            root = NormalizeDirectory(assetsRoot);
        }
        catch (Exception ex)
        {
            error = $"アセットフォルダのパスが不正です: {assetsRoot}（{ex.Message}）";
            return null;
        }
        if (!Directory.Exists(root))
        {
            error = $"アセットフォルダが見つかりません: {root}";
            return null;
        }

        // 親フォルダに .seedproj があり、その assets_dir がこのフォルダを指すなら、プロジェクト名もそこから取る
        // （既定のアプリ ID・アプリ名をプロジェクト名から作るため）。
        var parent = ParentOrSelf(root);
        var projectFile = SeedProjectFile.FindInDirectory(parent);
        if (projectFile is not null
            && SeedProjectFile.TryLoad(projectFile, out var file, out _)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(ProjectPaths.FromProjectFile(projectFile, file!).AssetsDir),
                root, StringComparison.OrdinalIgnoreCase))
        {
            var paths = ProjectPaths.FromProjectFile(projectFile, file!);
            return new ResolvedProjectFolder(root, parent, paths.ProjectFilePath, paths.Name, paths.DisplayName, "アセットフォルダを直接指定");
        }
        return new ResolvedProjectFolder(root, parent, null, null, null, "アセットフォルダを直接指定");
    }

    /// <summary>フォルダのパスを絶対パスにし、末尾の区切り文字を落とす。</summary>
    /// <param name="path">フォルダのパス（相対可）。</param>
    /// <returns>正規化した絶対パス。</returns>
    public static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>親フォルダ（ドライブのルートなど親が無ければ自分自身）。</summary>
    /// <param name="directory">正規化済みのフォルダ。</param>
    /// <returns>親フォルダの絶対パス。</returns>
    private static string ParentOrSelf(string directory) =>
        Path.GetDirectoryName(directory) is { Length: > 0 } parent ? parent : directory;
}
