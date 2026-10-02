// ============================================================
//  StageProject.cs — 撮影用の一時のプロジェクト（作業フォルダの assets/）を作る
//
//  【中身】
//    <作業フォルダ>/assets/project_settings.json   … 窓の大きさ・毎フレーム描く（render_policy continuous）
//    <作業フォルダ>/assets/__thumbnails/*.scene     … 舞台のシーン（起動用の _boot.scene と、件ごとの舞台）
//    <作業フォルダ>/assets/<ライブラリと同じ相対パス> … 舞台が参照するファイル（テンプレートアクタの追加と同じ計画で写す）
//  ライブラリ相対パスとアセット相対パスは同じ（ライブラリの assets://ui/... は取り込み先でも assets://ui/...）なので、
//  ライブラリの中身をそのままの相対パスで写せば、テンプレートの参照はそのまま解決する。
//
//  ライブラリ（templates/）にもリポジトリにも何も書かない。書くのは作業フォルダの中だけ。
// ============================================================

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Templates.Actors;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Stage;

/// <summary>撮影用の一時のプロジェクト。</summary>
public sealed class StageProject
{
    /// <summary>アセットルートのフォルダ名（作業フォルダの下）。</summary>
    private const string AssetsFolderName = "assets";

    /// <summary>プロジェクト設定のファイル名。</summary>
    private const string ProjectSettingsFileName = "project_settings.json";

    /// <summary>起動用の舞台（背景だけ）のシーンのファイル名。</summary>
    private const string BootSceneFileName = "_boot.scene";

    /// <summary>起動用の舞台の札（合図「[THUMB] ready boot」= スクリプトが動き始めた）。</summary>
    public const string BootTicket = "boot";

    /// <summary>プロジェクト設定の版。</summary>
    private const int ProjectSettingsFormatVersion = 1;

    /// <summary>プロジェクト設定のゲーム名（窓のタイトル）。</summary>
    private const string GameName = "SeedTemplateThumbnails";

    /// <summary>目標のフレームレート。</summary>
    private const int TargetFps = 60;

    /// <summary>アセットの仮想パスの頭。</summary>
    private const string AssetsScheme = "assets://";

    /// <summary>書き出す JSON の書き方。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>ライブラリルートの絶対パス。</summary>
    private readonly string _libraryRoot;

    /// <summary>アセットルート（作業フォルダの assets/）の絶対パス。</summary>
    public string AssetsRoot { get; }

    /// <summary>起動用の舞台のシーンの絶対パス。</summary>
    public string BootScenePath => Path.Combine(AssetsRoot, StageSceneBuilder.SceneFolder, BootSceneFileName);

    /// <summary>起動用の舞台のシーンの仮想パス（--scene に渡す）。</summary>
    public string BootSceneVirtualPath => AssetsScheme + StageSceneBuilder.SceneFolder + "/" + BootSceneFileName;

    /// <summary>
    /// 作業フォルダの下にアセットルートを用意する（中身は空にする）。
    /// </summary>
    /// <param name="libraryRoot">ライブラリルート。</param>
    /// <param name="workRoot">作業フォルダ。</param>
    public StageProject(string libraryRoot, string workRoot)
    {
        _libraryRoot = Path.GetFullPath(libraryRoot);
        AssetsRoot = Path.Combine(Path.GetFullPath(workRoot), AssetsFolderName);
        if (Directory.Exists(AssetsRoot)) Directory.Delete(AssetsRoot, recursive: true);
        Directory.CreateDirectory(Path.Combine(AssetsRoot, StageSceneBuilder.SceneFolder));
    }

    /// <summary>
    /// プロジェクト設定を書く（窓の大きさが違う舞台ごとに書き直してから起動する）。
    /// </summary>
    /// <param name="windowWidth">窓の幅（画素）。</param>
    /// <param name="windowHeight">窓の高さ（画素）。</param>
    public void WriteSettings(int windowWidth, int windowHeight)
    {
        var settings = new JsonObject
        {
            ["format_version"] = ProjectSettingsFormatVersion,
            ["game_name"] = GameName,
            ["start_scene"] = BootSceneVirtualPath,
            ["window_width"] = windowWidth,
            ["window_height"] = windowHeight,
            ["render_resolution_mode"] = "window",
            ["target_fps"] = TargetFps,
            ["vsync"] = "auto",
            ["plugins"] = new JsonArray(),
            // 毎フレーム描く（描く理由の無いフレームを止める on_demand だと、撮る前の動きが進まないことがある）
            ["render_policy"] = "continuous",
        };
        File.WriteAllText(Path.Combine(AssetsRoot, ProjectSettingsFileName), settings.ToJsonString(JsonOptions));
    }

    /// <summary>
    /// 起動用の舞台（背景も部品も無い dp のルートと舞台のスクリプト）を書く。
    /// 合図「[THUMB] ready boot」= スクリプトのコンパイルが通って動き始めた。
    /// </summary>
    public void WriteBoot()
    {
        var fields = new Dictionary<string, string> { ["Ticket"] = BootTicket, ["SettleSeconds"] = "0" };
        var boot = new JsonObject
        {
            ["name"] = Path.GetFileNameWithoutExtension(BootSceneFileName),
            ["actors"] = new JsonArray(StageNodes.Node2D(ThumbnailStageDefaults.StageRootName,
            [
                // dp のルートの大きさは窓から決まる（ここの値は使われない）ので、UI の舞台の既定の大きさを書いておく
                StageNodes.Canvas(ThumbnailStageDefaults.StageWidthDp, ThumbnailStageDefaults.StageHeightDp, dpUnit: true),
                StageNodes.Script(ThumbnailStageDefaults.StageScriptComponentName,
                    AssetsScheme + ThumbnailStageDefaults.StageScriptRelPath, fields),
            ])),
        };
        var bootJson = boot.ToJsonString(JsonOptions);
        File.WriteAllText(BootScenePath, bootJson);
        CopyDependencies(StageSceneBuilder.SceneFolder + "/" + BootSceneFileName, bootJson,
            [ThumbnailStageDefaults.StageScriptRelPath]);
    }

    /// <summary>
    /// 1 件の舞台のシーンを書き、参照するファイルをライブラリから写す。
    /// </summary>
    /// <param name="plan">撮る計画。</param>
    /// <returns>シーンの絶対パスと、ライブラリにもプロジェクトにも無かった参照（警告）。</returns>
    public (string ScenePath, IReadOnlyList<string> Missing) WriteStage(StagePlan plan)
    {
        var scenePath = AssetPathUtil.ToAbsolute(AssetsRoot, plan.SceneRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(scenePath)!);
        File.WriteAllText(scenePath, plan.SceneJson);
        var missing = CopyDependencies(plan.SceneRelPath, plan.SceneJson, plan.RequiredRelPaths);
        return (scenePath, missing);
    }

    /// <summary>
    /// シーンの JSON が参照するファイル（と明示のファイル）を、テンプレートアクタの追加と同じ計画で写す。
    /// 既にあるファイルは上書きしない（同じ部品を何件も撮るので 2 件目からは写さない）。
    /// </summary>
    /// <returns>見つからなかった参照。</returns>
    private IReadOnlyList<string> CopyDependencies(string sceneRelPath, string sceneJson, IEnumerable<string> required)
    {
        var plan = TemplateActorDependencyPlanner.Plan(_libraryRoot, AssetsRoot, sceneRelPath, sceneJson, required);
        var result = TemplateActorDependencyPlanner.Execute(plan);
        var problems = new List<string>(plan.Missing);
        foreach (var failure in result.Failures) problems.Add($"{failure.RelPath}（写せません: {failure.Message}）");
        return problems;
    }
}
