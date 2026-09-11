// ============================================================
//  ProjectCreator.cs — 新規プロジェクトの生成
//
//  【役割】
//  「名前」と「置き場所」だけを受け取り、すぐ開いて遊べる最小構成を作る。
//
//    <parentDir>/<Name>/
//      <Name>.seedproj
//      assets/
//        project_settings.json      ← ゲーム名・開始シーン・シーン一覧・解像度
//        packaging_settings.json    ← パッケージ化の既定値
//        scenes/Main.scene          ← 空のシーン 1 つ（開始シーン）
//      plugins/
//
//  【拡張点】
//  テンプレートライブラリからの初期コンテンツ投入は別担当が後から足す。
//  そのため生成の最後に <see cref="ProjectCreated"/> を発火させ、
//  「作り終わったプロジェクトへ追加で書き込む」処理を外から差せるようにしてある。
//
//  WPF に一切依存しない（単体テストからそのままリンクして使える）。
// ============================================================

using System;
using System.IO;
using System.Linq;
using SEEDEditor.Packaging;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Project;

/// <summary>
/// 新規プロジェクトのフォルダ・ファイル一式を生成する。
/// </summary>
public static class ProjectCreator
{
    // ── 定数（生成される構成の正典）──────────────────────────

    /// <summary>プロジェクト設定ファイル名（アセットルート直下）。</summary>
    public const string PROJECT_SETTINGS_FILE_NAME = "project_settings.json";

    /// <summary>パッケージ化設定ファイル名（アセットルート直下）。</summary>
    public const string PACKAGING_SETTINGS_FILE_NAME = "packaging_settings.json";

    /// <summary>初期シーンを置くアセット内フォルダ名。</summary>
    public const string SCENES_DIR_NAME = "scenes";

    /// <summary>初期シーンのファイル名（拡張子なし）。</summary>
    public const string INITIAL_SCENE_NAME = "Main";

    /// <summary>シーンファイルの拡張子（ドット付き）。</summary>
    public const string SCENE_EXTENSION = ".scene";

    /// <summary>アセット仮想パスの接頭辞（project_settings.json の記法）。</summary>
    public const string ASSETS_URI_PREFIX = "assets://";

    /// <summary>新規プロジェクトの既定ウィンドウ幅（物理ピクセル）。</summary>
    public const int DEFAULT_WINDOW_WIDTH = 1280;

    /// <summary>新規プロジェクトの既定ウィンドウ高さ（物理ピクセル）。</summary>
    public const int DEFAULT_WINDOW_HEIGHT = 720;

    /// <summary>プロジェクト名に使えない文字（ファイル名として不正なもの）。</summary>
    private static readonly char[] InvalidNameChars =
        Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).Distinct().ToArray();

    // ── 拡張フック ──────────────────────────────────────────

    /// <summary>
    /// プロジェクトの生成が完了した直後に呼ばれる。
    ///
    /// テンプレートライブラリからの初期コンテンツ投入など、
    /// 「最小構成の上に足す」処理をここから差し込む。
    /// 例外を投げても生成自体は成功扱いにする（追加投入の失敗で
    /// 作ったばかりのプロジェクトを捨てさせないため）。
    /// </summary>
    public static event Action<ProjectPaths>? ProjectCreated;

    /// <summary>
    /// 診断メッセージの書き出し先。エディタ起動時に EditorLog.Write を差す。
    ///
    /// このクラス自身が EditorLog を直接呼ばないのは、単体テストへリンクしたときに
    /// テスト実行だけでログファイルが作られてしまうのを避けるため
    /// （PackageLayout が AppendLog デリゲートを受け取るのと同じ流儀）。
    /// </summary>
    public static Action<string>? Log { get; set; }

    // ── 検証 ────────────────────────────────────────────────

    /// <summary>
    /// プロジェクト名として使えるかを調べる。
    /// </summary>
    /// <param name="name">入力された名前。</param>
    /// <returns>使えない理由（日本語）。問題なければ null。</returns>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "プロジェクト名を入力してください。";
        if (name.IndexOfAny(InvalidNameChars) >= 0)
            return "プロジェクト名にファイル名として使えない文字が含まれています。";
        if (name.Trim() != name) return "プロジェクト名の前後に空白は使えません。";
        if (name.EndsWith('.')) return "プロジェクト名の末尾にピリオドは使えません。";
        return null;
    }

    /// <summary>
    /// 生成先フォルダとして使えるかを調べる。
    /// </summary>
    /// <param name="parentDir">プロジェクトフォルダを作る親フォルダ。</param>
    /// <param name="name">プロジェクト名。</param>
    /// <returns>使えない理由（日本語）。問題なければ null。</returns>
    public static string? ValidateDestination(string? parentDir, string? name)
    {
        if (string.IsNullOrWhiteSpace(parentDir)) return "作成先フォルダを選択してください。";

        var nameError = ValidateName(name);
        if (nameError is not null) return nameError;

        string target;
        try { target = Path.Combine(Path.GetFullPath(parentDir), name!); }
        catch (Exception ex) { return $"作成先のパスが不正です: {ex.Message}"; }

        // 既存フォルダに中身があると、生成物と既存物が混ざって何が起きたか分からなくなる。
        if (Directory.Exists(target)
            && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return $"作成先に空でないフォルダが既にあります: {target}";
        }
        return null;
    }

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// 新規プロジェクトを生成する。
    /// </summary>
    /// <param name="parentDir">プロジェクトフォルダを作る親フォルダ。</param>
    /// <param name="name">プロジェクト名（フォルダ名・.seedproj 名になる）。</param>
    /// <param name="displayName">表示名。null / 空なら <paramref name="name"/> を使う。</param>
    /// <returns>生成したプロジェクトのパス一式。</returns>
    /// <exception cref="SeedProjectFileException">名前・作成先が不正なとき。</exception>
    public static ProjectPaths Create(string parentDir, string name, string? displayName = null)
    {
        var error = ValidateDestination(parentDir, name);
        if (error is not null) throw new SeedProjectFileException(error);

        // ── 1. フォルダ構成を作る ────────────────────────────
        var rootDir     = Path.Combine(Path.GetFullPath(parentDir), name);
        var projectFile = SeedProjectFile.Create(name, displayName);
        var projectPath = Path.Combine(rootDir, name + SeedProjectFile.EXTENSION);
        var paths       = ProjectPaths.FromProjectFile(projectPath, projectFile);

        Directory.CreateDirectory(rootDir);
        paths.EnsureDirectories();

        // ── 2. .seedproj を書く ─────────────────────────────
        projectFile.Save(projectPath);

        // ── 3. 初期シーンを置く ─────────────────────────────
        // シーンの最小構成はランタイムの SceneData（runtime/.../scene.rs）に合わせる。
        // #[serde(default)] が無い必須キーは name と actors の 2 つだけ。
        var scenesDir = Path.Combine(paths.AssetsDir, SCENES_DIR_NAME);
        Directory.CreateDirectory(scenesDir);
        var scenePath = Path.Combine(scenesDir, INITIAL_SCENE_NAME + SCENE_EXTENSION);
        File.WriteAllText(scenePath, BuildEmptySceneJson(INITIAL_SCENE_NAME));

        // ── 4. project_settings.json を書く ──────────────────
        var sceneVirtualPath = $"{ASSETS_URI_PREFIX}{SCENES_DIR_NAME}/{INITIAL_SCENE_NAME}{SCENE_EXTENSION}";
        var settings = new ProjectSettingsData
        {
            GameName     = string.IsNullOrWhiteSpace(displayName) ? name : displayName!,
            StartScene   = sceneVirtualPath,
            WindowWidth  = DEFAULT_WINDOW_WIDTH,
            WindowHeight = DEFAULT_WINDOW_HEIGHT,
        };
        settings.Scenes.Add(new SceneEntry { Name = INITIAL_SCENE_NAME, Path = sceneVirtualPath });
        settings.SaveTo(Path.Combine(paths.AssetsDir, PROJECT_SETTINGS_FILE_NAME));

        // ── 5. packaging_settings.json を既定値で書く ─────────
        // 出力先は空のままにしておく（空なら <ProjectRoot>/build/<platform> が既定になる。
        // PackagingOutputDefaults がその解決を担当する）。
        var packaging = new PackagingData
        {
            GameName = settings.GameName,
        };
        packaging.SaveTo(Path.Combine(paths.AssetsDir, PACKAGING_SETTINGS_FILE_NAME));

        // ── 6. 生成後フック（テンプレート投入などの拡張点）────
        try { ProjectCreated?.Invoke(paths); }
        catch (Exception ex)
        {
            // 追加投入の失敗でプロジェクト生成そのものを失敗にはしない。
            Log?.Invoke($"プロジェクト生成後フックで例外: {ex.Message}");
        }

        return paths;
    }

    /// <summary>
    /// 空のシーンファイルの JSON を組み立てる。
    /// </summary>
    /// <param name="sceneName">シーン名（.scene の "name"）。</param>
    private static string BuildEmptySceneJson(string sceneName)
        => $$"""
            {
              "name": "{{sceneName}}",
              "actors": []
            }
            """;
}
