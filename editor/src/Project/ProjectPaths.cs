// ============================================================
//  ProjectPaths.cs — プロジェクト配下の各フォルダの絶対パス導出
//
//  【役割】
//  .seedproj 1 つから「このプロジェクトのどこに何があるか」を確定させる。
//  パスの組み立てはここだけで行い、他所で Path.Combine(assets, "..", ...) のような
//  相対の積み上げを書かない（ずれた瞬間に別プロジェクトを壊すため）。
//
//  【ランタイムとの契約（重要）】
//  ランタイムは --assets-root で受け取ったアセットルートの **親フォルダ** から
//  cache / save / plugins を導出する:
//    ・cache … runtime/src/engine/core/loader/asset_cache.rs cache_dir()
//    ・save  … runtime/src/engine/core/save/path.rs decide_save_dir()
//    ・plugins … runtime/src/engine/core/app_base/app/app_init.rs（"plugins" 固定）
//  そのため assets_dir は必ずプロジェクトルート直下の 1 階層にする。
//  ここで導出する CacheDir / SaveDir / PluginsDir はランタイムの導出結果と
//  一致していなければならない（エディタ側の表示・掃除がずれないようにするため）。
//
//  WPF に一切依存しない（単体テストからそのままリンクして使える）。
// ============================================================

using System;
using System.IO;

namespace SEEDEditor.Project;

/// <summary>
/// 1 つのプロジェクトに属するフォルダの絶対パス一式（不変）。
///
/// <para>
/// 生成後に値は変わらない。プロジェクトを切り替えるときは新しいインスタンスを作る
/// （同じインスタンスを書き換えると、参照を持っている側が古い前提で動き続けるため）。
/// </para>
/// </summary>
public sealed class ProjectPaths
{
    // ── 実行時生成物のフォルダ名（ランタイム側の定数と一致させること）──

    /// <summary>モデル等の変換キャッシュ置き場（ランタイムが生成する）。</summary>
    public const string CACHE_DIR_NAME = "cache";

    /// <summary>セーブデータ置き場（ランタイムが生成する）。</summary>
    public const string SAVE_DIR_NAME = "save";

    /// <summary>ゲーム実行ログ置き場。</summary>
    public const string LOGS_DIR_NAME = "logs";

    /// <summary>パッケージ化の出力先ルート（プラットフォームごとのサブフォルダを持つ）。</summary>
    public const string BUILD_DIR_NAME = "build";

    // ── プロパティ ──────────────────────────────────────────

    /// <summary>プロジェクトルート（.seedproj のあるフォルダ）。</summary>
    public string RootDir { get; }

    /// <summary>.seedproj の絶対パス。</summary>
    public string ProjectFilePath { get; }

    /// <summary>アセットルート（assets:// の実体。ランタイムへ --assets-root で渡す）。</summary>
    public string AssetsDir { get; }

    /// <summary>プラグイン DLL 置き場。</summary>
    public string PluginsDir { get; }

    /// <summary>変換キャッシュ置き場（ランタイムが生成）。</summary>
    public string CacheDir { get; }

    /// <summary>セーブデータ置き場（ランタイムが生成）。</summary>
    public string SaveDir { get; }

    /// <summary>ログ置き場。</summary>
    public string LogsDir { get; }

    /// <summary>パッケージ化出力ルート。</summary>
    public string BuildDir { get; }

    /// <summary>プロジェクト識別名（.seedproj の name）。</summary>
    public string Name { get; }

    /// <summary>画面に出す表示名（.seedproj の display_name、空なら name）。</summary>
    public string DisplayName { get; }

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// 全フィールドを指定して生成する（外部からは
    /// <see cref="FromProjectFile"/> を使うこと）。
    /// </summary>
    private ProjectPaths(
        string rootDir, string projectFilePath, string assetsDir, string pluginsDir,
        string name, string displayName)
    {
        RootDir         = rootDir;
        ProjectFilePath = projectFilePath;
        AssetsDir       = assetsDir;
        PluginsDir      = pluginsDir;
        Name            = name;
        DisplayName     = displayName;

        // 実行時生成物はプロジェクトルート直下に固定する（ランタイムの導出と一致させる）。
        CacheDir = Path.Combine(rootDir, CACHE_DIR_NAME);
        SaveDir  = Path.Combine(rootDir, SAVE_DIR_NAME);
        LogsDir  = Path.Combine(rootDir, LOGS_DIR_NAME);
        BuildDir = Path.Combine(rootDir, BUILD_DIR_NAME);
    }

    /// <summary>
    /// .seedproj のパスとその内容から、各フォルダの絶対パスを導出する。
    /// </summary>
    /// <param name="projectFilePath">.seedproj のパス（相対でも可。内部で絶対化する）。</param>
    /// <param name="file">.seedproj の内容。</param>
    /// <returns>導出結果（不変）。</returns>
    /// <exception cref="SeedProjectFileException">パスが不正でルートを決められないとき。</exception>
    public static ProjectPaths FromProjectFile(string projectFilePath, SeedProjectFile file)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
            throw new SeedProjectFileException("プロジェクトファイルのパスが指定されていません。");

        var fullProjectPath = Path.GetFullPath(projectFilePath);
        var rootDir         = Path.GetDirectoryName(fullProjectPath);
        if (string.IsNullOrEmpty(rootDir))
        {
            throw new SeedProjectFileException(
                $"プロジェクトルートを決定できません: {projectFilePath}");
        }

        // assets_dir / plugins_dir は相対指定を前提とするが、絶対パスが入っていても
        // Path.Combine が「絶対側を優先」して正しく解決するのでそのまま通す。
        var assetsDir  = Path.GetFullPath(Path.Combine(
            rootDir, string.IsNullOrWhiteSpace(file.AssetsDir)
                     ? SeedProjectFile.DEFAULT_ASSETS_DIR : file.AssetsDir));
        var pluginsDir = Path.GetFullPath(Path.Combine(
            rootDir, string.IsNullOrWhiteSpace(file.PluginsDir)
                     ? SeedProjectFile.DEFAULT_PLUGINS_DIR : file.PluginsDir));

        var name = string.IsNullOrWhiteSpace(file.Name)
            ? Path.GetFileNameWithoutExtension(fullProjectPath)
            : file.Name;

        return new ProjectPaths(
            rootDir, fullProjectPath, assetsDir, pluginsDir,
            name,
            string.IsNullOrWhiteSpace(file.DisplayName) ? name : file.DisplayName);
    }

    // ── 操作 ────────────────────────────────────────────────

    /// <summary>
    /// プロジェクトに必要なフォルダを作る。
    ///
    /// 作るのは assets と plugins だけ。cache / save / logs は
    /// 「使われたときにランタイムが作る」ものなので、空フォルダを先回りして
    /// 置かない（何も起きていないのに生成物フォルダが並ぶのを避ける）。
    /// </summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(AssetsDir);
        Directory.CreateDirectory(PluginsDir);
    }

    /// <summary>
    /// パッケージ化の既定出力先（<c>&lt;ProjectRoot&gt;/build/&lt;platform&gt;</c>）を返す。
    /// </summary>
    /// <param name="platformFolderName">プラットフォームのフォルダ名（例 "windows"）。</param>
    public string BuildDirFor(string platformFolderName)
        => Path.Combine(BuildDir, platformFolderName);

    /// <summary>ログ・診断表示用の 1 行表現。</summary>
    public override string ToString()
        => $"{Name} root={RootDir} assets={AssetsDir}";
}
