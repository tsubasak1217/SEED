// ============================================================
//  PakInputResolver.cs — SeedPak の入力（アセットルート・runtime/src・収録ルール）を決める
//
//  【役割】
//  解釈済みの引数（SeedPakOptions）から、PAK 作りに要る入力をファイルシステムを見て確定させる。
//    - アセットルート … --assets ならそのまま。--project なら、エディタと同じく .seedproj の
//                       assets_dir から導く（ProjectPaths。.seedproj が無ければ <フォルダ>/assets、
//                       それも無くフォルダ自体に project_settings.json があればそのフォルダ。
//                       規則は editor/src/Project/ProjectFolderResolver.cs の 1 か所で、SeedAndroid と共有する）
//    - runtime/src    … --runtime-src か、このツールの位置・カレントから上へ辿って見つけたリポジトリの runtime/src
//    - 収録ルール     … <アセットルート>/packaging_settings.json（パッケージ化ウィンドウと同じファイル）
//
//  【なぜアセットルートの表記を揃えるのか】
//  PAK へ入れるときにシーン等の絶対パス参照を assets:// へ書き換えるが、その照合は
//  アセットルートの文字列表記を基準にする（AssetPathRewriter）。エディタと同じ導き方
//  （Path.GetFullPath）にしておかないと、ウィンドウとツールで出力が変わってしまう。
// ============================================================

using System;
using System.IO;
using SEEDEditor.Packaging;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Project;

namespace SEEDEditor.Tools.SeedPak;

/// <summary>PAK 作りの入力一式（確定済み）。</summary>
/// <param name="AssetsRoot">アセットルートの絶対パス。</param>
/// <param name="AssetsRootOrigin">アセットルートをどう決めたか（ログ用）。</param>
/// <param name="RuntimeSourceRoot">runtime/src の絶対パス（見つからなければ null）。</param>
/// <param name="Settings">収録ルール。</param>
/// <param name="SettingsPath">収録ルールを読んだ（読もうとした）ファイル。</param>
/// <param name="SettingsFound">収録ルールのファイルがあったか（無ければ既定値）。</param>
/// <param name="OutDir">出力フォルダの絶対パス。</param>
public sealed record PakInputs(
    string AssetsRoot,
    string AssetsRootOrigin,
    string? RuntimeSourceRoot,
    AssetPackagingSettings Settings,
    string SettingsPath,
    bool SettingsFound,
    string OutDir);

/// <summary>SeedPak の入力を確定させる。</summary>
public static class PakInputResolver
{
    /// <summary>プロジェクト設定のファイル名（アセットルートの目印）。ランタイムが必ず読むファイル。</summary>
    private const string ProjectSettingsFileName = "project_settings.json";

    /// <summary>リポジトリ直下のランタイムのフォルダ名。</summary>
    private const string RuntimeDirName = "runtime";

    /// <summary>ランタイムのソースフォルダ名（runtime/ の下）。</summary>
    private const string RuntimeSourceDirName = "src";

    /// <summary>ランタイムのフォルダであることの目印（cargo のマニフェスト）。</summary>
    private const string CargoManifestFileName = "Cargo.toml";

    /// <summary>
    /// 入力を確定させる。失敗したら理由を <paramref name="error"/> へ書いて null を返す。
    /// </summary>
    /// <param name="options">解釈済みの引数。</param>
    /// <param name="error">失敗理由の出力先。</param>
    /// <returns>確定した入力。失敗時は null。</returns>
    public static PakInputs? Resolve(SeedPakOptions options, Action<string> error)
    {
        // ── アセットルート ──
        string? assetsRoot;
        string origin;
        if (options.AssetsDir is not null)
        {
            assetsRoot = NormalizeDirectory(options.AssetsDir);
            origin     = $"{SeedPakArguments.AssetsOption} で指定";
        }
        else
        {
            (assetsRoot, origin) = ResolveFromProject(options.ProjectDir!, error);
            if (assetsRoot is null) return null;
        }

        if (!Directory.Exists(assetsRoot))
        {
            error($"アセットルートが見つかりません: {assetsRoot}");
            return null;
        }
        if (!File.Exists(Path.Combine(assetsRoot, ProjectSettingsFileName)))
        {
            // 収集の起点（登録シーン）が取れないので 0 件になる。止めずに警告だけ出す（ウィンドウと同じ扱い）。
            error($"⚠ {ProjectSettingsFileName} がアセットルートにありません: {assetsRoot}");
        }

        // ── runtime/src（エンジンに焼き込まれた assets:// 参照を起点へ加えるため）──
        var runtimeSource = options.RuntimeSourceDir is not null
            ? NormalizeDirectory(options.RuntimeSourceDir)
            : FindRuntimeSource(AppContext.BaseDirectory) ?? FindRuntimeSource(Directory.GetCurrentDirectory());
        if (runtimeSource is not null && !Directory.Exists(runtimeSource))
        {
            error($"{SeedPakArguments.RuntimeSourceOption} のフォルダが見つかりません: {runtimeSource}");
            return null;
        }

        // ── 収録ルール（パッケージ化ウィンドウと同じファイル・同じ読み方）──
        var settingsPath = Path.Combine(assetsRoot, PackagingData.SettingsFileName);
        var settings     = PackagingData.LoadFrom(settingsPath).Assets;

        return new PakInputs(
            assetsRoot, origin, runtimeSource, settings, settingsPath, File.Exists(settingsPath),
            NormalizeDirectory(options.OutDir));
    }

    /// <summary>
    /// プロジェクトフォルダからアセットルートを決める（エディタと同じ導き方。規則の正典は
    /// editor/src/Project/ProjectFolderResolver.cs で、Android の SeedAndroid も同じ規則を使う）。
    /// </summary>
    /// <param name="projectDir">--project の値。</param>
    /// <param name="error">失敗理由の出力先。</param>
    /// <returns>(アセットルート, 決め方の説明)。決められなければアセットルートは null。</returns>
    private static (string? AssetsRoot, string Origin) ResolveFromProject(string projectDir, Action<string> error)
    {
        var resolved = ProjectFolderResolver.Resolve(projectDir, out var message);
        if (resolved is null)
        {
            error($"{message}（{SeedPakArguments.AssetsOption} でアセットルートを直接指定することもできます）");
            return (null, "");
        }
        return (resolved.AssetsRoot, resolved.Origin);
    }

    /// <summary>
    /// 指定フォルダから上へ辿り、runtime/Cargo.toml を持つリポジトリの runtime/src を探す。
    /// </summary>
    /// <param name="startDir">探し始めるフォルダ。</param>
    /// <returns>runtime/src の絶対パス。見つからなければ null。</returns>
    public static string? FindRuntimeSource(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var runtime = Path.Combine(dir.FullName, RuntimeDirName);
            var source  = Path.Combine(runtime, RuntimeSourceDirName);
            if (File.Exists(Path.Combine(runtime, CargoManifestFileName)) && Directory.Exists(source))
                return source;
        }
        return null;
    }

    /// <summary>フォルダのパスを絶対パスにし、末尾の区切り文字を落とす。</summary>
    /// <param name="path">フォルダのパス（相対可）。</param>
    /// <returns>正規化した絶対パス。</returns>
    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
