// ============================================================
//  ProjectStartupResolver.cs — 起動時に「どのプロジェクトを開くか」を決める
//
//  【役割】
//  起動引数・環境変数・最近のプロジェクト一覧を突き合わせ、
//  「開くプロジェクト」「スタート画面を出す」「エラーで終了」のどれかへ落とす。
//  ウィンドウを作る前に呼ばれるので WPF へは依存しない。
//
//  【解決順】
//   1. --project / 位置引数 .seedproj / 環境変数 SEED_PROJECT（EditorStartupOptions が解決済み）
//      → 指定があれば必ずそれを使う。開けなければ **エラー**（黙って別のプロジェクトへ
//        倒すと、利用者は意図しないプロジェクトを編集してしまう）。
//   2. 指定が無く、ヘッドレスなら「最近のプロジェクトの先頭で実在するもの」
//      （画面を出せないため、対話で選ばせることができない）。
//   3. 指定が無く、通常起動ならスタート画面。
// ============================================================

using System;
using System.IO;
using System.Linq;
using SEEDEditor.Project;

namespace SEEDEditor.Startup;

/// <summary>起動時のプロジェクト解決結果の種別。</summary>
public enum ProjectStartupKind
{
    /// <summary>プロジェクトが確定した。エディタ本体を開く。</summary>
    OpenProject,

    /// <summary>確定できなかった。スタート画面を出す。</summary>
    ShowStartWindow,

    /// <summary>確定できず、画面も出せない（ヘッドレス）。エラー終了する。</summary>
    FailAndExit,
}

/// <summary>
/// 起動時のプロジェクト解決結果。
/// </summary>
/// <param name="Kind">結果の種別。</param>
/// <param name="ProjectFilePath">開く .seedproj の絶対パス（<see cref="ProjectStartupKind.OpenProject"/> のとき）。</param>
/// <param name="ErrorMessage">
/// 利用者へ見せるエラー文（スタート画面に出す／ヘッドレスならログに書く）。
/// エラーが無ければ null。
/// </param>
public readonly record struct ProjectStartupResult(
    ProjectStartupKind Kind,
    string?            ProjectFilePath,
    string?            ErrorMessage);

/// <summary>
/// 起動時にどのプロジェクトを開くかを決める（純粋な判定。副作用なし）。
/// </summary>
public static class ProjectStartupResolver
{
    /// <summary>
    /// 起動オプションと最近の一覧から、開くべきプロジェクトを決める。
    /// </summary>
    /// <param name="requestedProjectPath">
    /// 起動引数・環境変数で指定されたプロジェクト（<c>EditorStartupOptions.ProjectFilePath</c>）。
    /// 指定が無ければ null。実在は保証されない。
    /// </param>
    /// <param name="isHeadless">ヘッドレス起動か（画面を出せないか）。</param>
    /// <param name="recentProjectPaths">最近開いたプロジェクトのパス（新しい順）。</param>
    /// <returns>解決結果。</returns>
    public static ProjectStartupResult Resolve(
        string?                     requestedProjectPath,
        bool                        isHeadless,
        System.Collections.Generic.IEnumerable<string> recentProjectPaths)
    {
        // ── 1. 明示指定があるなら、それ以外へは絶対に倒さない ────
        if (!string.IsNullOrWhiteSpace(requestedProjectPath))
        {
            if (File.Exists(requestedProjectPath))
            {
                return new ProjectStartupResult(
                    ProjectStartupKind.OpenProject, requestedProjectPath, null);
            }

            var message = $"指定されたプロジェクトが見つかりません: {requestedProjectPath}";
            return new ProjectStartupResult(
                isHeadless ? ProjectStartupKind.FailAndExit : ProjectStartupKind.ShowStartWindow,
                null, message);
        }

        // ── 2. ヘッドレスは対話できないので最近の一覧から拾う ────
        if (isHeadless)
        {
            var last = recentProjectPaths?.FirstOrDefault(
                p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
            if (last is not null)
            {
                return new ProjectStartupResult(ProjectStartupKind.OpenProject, last, null);
            }

            return new ProjectStartupResult(
                ProjectStartupKind.FailAndExit, null,
                "ヘッドレス起動にはプロジェクトの指定が必要です"
              + "（--project <path> または環境変数 SEED_PROJECT）。"
              + "最近開いたプロジェクトも見つかりませんでした。");
        }

        // ── 3. 通常起動はスタート画面から選んでもらう ────────────
        return new ProjectStartupResult(ProjectStartupKind.ShowStartWindow, null, null);
    }
}
