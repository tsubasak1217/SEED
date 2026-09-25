// ============================================================
//  AndroidPakSceneSeeds.cs — 起動するシーンを APK の pak の収録の起点に足すかを決める（純粋な処理。段階C-4）
//
//  【なぜ要るか】
//  APK の pak はプロジェクト設定の開始シーン・シーン一覧（シーンマネージャ）から参照をたどって作る
//  （editor/src/Packaging/Collect/AssetCollector.cs。パッケージ化ウィンドウ・SeedPak と同じ）。エディタの Android の実行は
//  PC の Play と同じく「開いているシーン」から起動する（段階C-3）が、シーンマネージャに登録していないシーンは pak に入らず、
//  端末は警告して開始シーンで起動していた。そこで起動するシーンが未登録なら、SeedPak の --extra-scene でそのシーンを
//  収録の起点に足す（登録シーンと同じく、そのシーンから参照をたどれるものも入る）。
//
//  【決め方】（ファイルの有無だけは呼び出し側の関数で見る）
//    起動するシーンの指定が無い（開始シーン）             … 足さない
//    APK に pak を入れない（プロジェクト無し・--assets-dir） … 足さない（開発用の APK はアセットをフォルダごと送る）
//    アセットルートに無いシーン                           … 足さない（入れようが無い。端末が警告して開始シーンで起動する）
//    登録シーン（start_scene・scenes[].path）              … 足さない（既に起点。区切り・大小文字・assets:// の有無は問わない）
//    それ以外（未登録）                                   … 足す
//  足したシーンは pak の指紋（Plan/AndroidStepFingerprints.PackageContent）にも入るので、未登録のシーンへ切り替えた最初の
//  実行で pak・APK・インストールをやり直し、同じシーンのままなら飛ばす。登録済みのシーン（か開始シーン）へ戻すと、足さない
//  pak（パッケージ化ウィンドウと同じ中身）に作り直す（開発中のシーンが残った pak をパッケージ化の APK に使わないため）。
//
//  エディタの実行（AndroidRun/AndroidEditorRunRequests の ScenePath）と SeedAndroid の --scene は、どちらも中核の準備
//  （Pipeline/AndroidRunPipeline）でここを通る。
//
//  WPF に依存しない（コンソールツール・エディタ・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Android.Project;

/// <summary>起動するシーンと pak の収録の起点の関係。</summary>
public enum AndroidPakSceneSeedStatus
{
    /// <summary>起動するシーンの指定が無い（開始シーンで起動する）。</summary>
    NoLaunchScene,

    /// <summary>APK に pak を入れない（プロジェクトが無い・開発用の --assets-dir）。</summary>
    NotPackaged,

    /// <summary>アセットルートに無い（足しても入らない。端末が警告して開始シーンで起動する）。</summary>
    NotOnDisk,

    /// <summary>シーンマネージャに登録済み（既に収録の起点）。</summary>
    Registered,

    /// <summary>未登録なので収録の起点に足す。</summary>
    AddedAsSeed,
}

/// <summary>起動するシーンを pak の収録の起点に足すかの判断。</summary>
/// <param name="Status">起動するシーンと pak の起点の関係。</param>
/// <param name="ExtraScenes">pak の収録の起点に足すシーン（アセットルートからの相対パス。足さなければ空）。</param>
public sealed record AndroidPakSceneSeedDecision(AndroidPakSceneSeedStatus Status, IReadOnlyList<string> ExtraScenes)
{
    /// <summary>足さない判断を作る。</summary>
    /// <param name="status">足さない理由。</param>
    /// <returns>判断。</returns>
    public static AndroidPakSceneSeedDecision NotAdded(AndroidPakSceneSeedStatus status) => new(status, Array.Empty<string>());
}

/// <summary>起動するシーンを APK の pak の収録の起点に足すかを決める。</summary>
public static class AndroidPakSceneSeeds
{
    /// <summary>
    /// 今回のプロジェクトについて決める（シーンの有無はアセットルートのファイルで見る）。
    /// </summary>
    /// <param name="launchScene">起動するシーン（アセットルートからの相対パス等。null なら開始シーン）。</param>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <returns>判断。</returns>
    public static AndroidPakSceneSeedDecision Decide(string? launchScene, AndroidProjectInfo? project)
    {
        if (string.IsNullOrWhiteSpace(launchScene)) return AndroidPakSceneSeedDecision.NotAdded(AndroidPakSceneSeedStatus.NoLaunchScene);
        if (project is not { Mode: AndroidProjectMode.Packaged }) return AndroidPakSceneSeedDecision.NotAdded(AndroidPakSceneSeedStatus.NotPackaged);

        var assetsRoot = project.Folder.AssetsRoot;
        return Decide(launchScene, project.Settings.RegisteredScenes, assetsRoot,
            relative => AndroidScenePath.ExistsUnder(assetsRoot, relative));
    }

    /// <summary>
    /// 材料を渡して決める（純粋な処理）。
    /// </summary>
    /// <param name="launchScene">起動するシーン（アセットルートからの相対パス・assets://…・アセットルート内の絶対パス。null なら開始シーン）。</param>
    /// <param name="registeredScenes">登録シーン（project_settings.json の start_scene と scenes[].path。書かれた表記のまま）。</param>
    /// <param name="assetsRoot">アセットルート（絶対パスの指定・登録を相対に直すのに使う）。</param>
    /// <param name="existsUnderAssets">相対パスのファイルがアセットルートにあるか。</param>
    /// <returns>判断。</returns>
    public static AndroidPakSceneSeedDecision Decide(
        string? launchScene, IEnumerable<string> registeredScenes, string assetsRoot, Func<string, bool> existsUnderAssets)
    {
        // 指定の形の誤り（アセットルートの外・..）は準備（ResolveLaunchScene）で弾き済み。ここでは「指定なし」と同じに扱う
        var scene = AndroidScenePath.Normalize(launchScene, assetsRoot);
        if (scene.Relative is null) return AndroidPakSceneSeedDecision.NotAdded(AndroidPakSceneSeedStatus.NoLaunchScene);
        if (!existsUnderAssets(scene.Relative)) return AndroidPakSceneSeedDecision.NotAdded(AndroidPakSceneSeedStatus.NotOnDisk);
        if (IsRegistered(scene.Relative, registeredScenes, assetsRoot)) return AndroidPakSceneSeedDecision.NotAdded(AndroidPakSceneSeedStatus.Registered);
        return new AndroidPakSceneSeedDecision(AndroidPakSceneSeedStatus.AddedAsSeed, new[] { scene.Relative });
    }

    /// <summary>
    /// 相対パスのシーンが登録シーンか（登録の書き方 assets://・相対・アセットルート内の絶対パスを揃えてから比べる。
    /// 大文字小文字は問わない＝収録の起点の照合〈AssetCollector〉と端末の pak の引き方と同じ）。
    /// </summary>
    /// <param name="relative">アセットルートからの相対パス（/ 区切り）。</param>
    /// <param name="registeredScenes">登録シーン（書かれた表記のまま）。</param>
    /// <param name="assetsRoot">アセットルート。</param>
    /// <returns>登録シーンなら true。</returns>
    public static bool IsRegistered(string relative, IEnumerable<string> registeredScenes, string assetsRoot) =>
        registeredScenes.Any(entry => AndroidScenePath.Normalize(entry, assetsRoot).Relative is { } registered
                                      && string.Equals(registered, relative, StringComparison.OrdinalIgnoreCase));
}
