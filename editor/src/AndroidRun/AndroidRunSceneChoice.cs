// ============================================================
//  AndroidRunSceneChoice.cs — エディタの Android の実行で、端末で起動するシーンを決める（純粋な処理。段階C-3）
//
//  【決め方】PC の Play と同じ（MainWindow.OnPlayPause）:
//    「開始シーンからプレイ」がオン … 開始シーン（project_settings.json の start_scene）
//    それ以外                      … 開いているシーン（アセットルートからの相対パスにして渡す）
//  PC の Play は保存していない新規シーン・アセットフォルダの外のシーンも一時ファイルで動かせるが、Android は APK の
//  pak（保存済みのアセット）から読むので、その 2 つは開始シーンで起動し、理由を Output に 1 行出す。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using SEEDEditor.Android.Project;

namespace SEEDEditor.AndroidRun;

/// <summary>起動するシーンの出どころ。</summary>
public enum AndroidRunSceneSource
{
    /// <summary>開いているシーン（PC の Play と同じ）。</summary>
    OpenScene,

    /// <summary>開始シーン（「開始シーンからプレイ」がオン）。</summary>
    StartSceneByOption,

    /// <summary>開始シーン（開いているシーンがまだファイルに保存されていない新規シーン）。</summary>
    StartSceneUnsaved,

    /// <summary>開始シーン（開いているシーンがアセットフォルダの外など、端末で開けない）。</summary>
    StartSceneUnreachable,
}

/// <summary>端末で起動するシーン。</summary>
/// <param name="ScenePath">アセットルートからの相対パス（開始シーンなら null）。</param>
/// <param name="Source">出どころ。</param>
/// <param name="Detail">開始シーンにした理由の詳細（端末で開けないときだけ）。</param>
public sealed record AndroidRunSceneChoice(string? ScenePath, AndroidRunSceneSource Source, string? Detail = null)
{
    /// <summary>開いているシーンを使えず開始シーンにしたか（Output に警告の色で出す）。</summary>
    public bool IsFallback => Source is AndroidRunSceneSource.StartSceneUnsaved or AndroidRunSceneSource.StartSceneUnreachable;

    /// <summary>
    /// 起動するシーンを決める。
    /// </summary>
    /// <param name="playFromStartScene">「開始シーンからプレイ」がオンか。</param>
    /// <param name="currentScenePath">開いているシーン（絶対パス。保存していない新規シーンなら null）。</param>
    /// <param name="assetsRoot">アセットルート。</param>
    /// <returns>起動するシーン。</returns>
    public static AndroidRunSceneChoice Decide(bool playFromStartScene, string? currentScenePath, string? assetsRoot)
    {
        if (playFromStartScene) return new AndroidRunSceneChoice(null, AndroidRunSceneSource.StartSceneByOption);
        if (string.IsNullOrWhiteSpace(currentScenePath)) return new AndroidRunSceneChoice(null, AndroidRunSceneSource.StartSceneUnsaved);

        var normalized = AndroidScenePath.Normalize(currentScenePath, assetsRoot);
        return normalized.Relative is null
            ? new AndroidRunSceneChoice(null, AndroidRunSceneSource.StartSceneUnreachable, normalized.Error)
            : new AndroidRunSceneChoice(normalized.Relative, AndroidRunSceneSource.OpenScene);
    }
}
