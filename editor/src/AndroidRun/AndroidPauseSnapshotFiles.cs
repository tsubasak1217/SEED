// ============================================================
//  AndroidPauseSnapshotFiles.cs — 一時停止中の端末のシーンの写しを PC に置く場所（純粋な処理。docs/android.md §20.17）
//
//  <プロジェクト>/cache/android/snapshot/paused.scene（docs/project_system.md の cache/ の規約。実行時に作られる生成物で、
//  バージョン管理に入れない）。一時停止のたびに上書きする（編集用ランタイムは表示を始めるときに読み切るので、
//  表示中に上書きされても困らない）。プロジェクトのアセット（assets/）には書かない。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System.IO;

namespace SEEDEditor.AndroidRun;

/// <summary>一時停止中の端末のシーンの写しの置き場。</summary>
public static class AndroidPauseSnapshotFiles
{
    /// <summary>プロジェクトからの相対パス（cache/android/snapshot/paused.scene）。</summary>
    public static readonly string ProjectRelativePath = Path.Combine("cache", "android", "snapshot", "paused.scene");

    /// <summary>
    /// 写しを置く絶対パス（プロジェクトが分からなければ null＝写しを取り出さない）。
    /// </summary>
    /// <param name="projectDir">プロジェクトのルート。</param>
    /// <returns>絶対パス（無ければ null）。</returns>
    public static string? LocalPathFor(string? projectDir) =>
        string.IsNullOrWhiteSpace(projectDir) ? null : Path.Combine(Path.GetFullPath(projectDir), ProjectRelativePath);
}
