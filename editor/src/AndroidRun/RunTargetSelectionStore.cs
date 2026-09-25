// ============================================================
//  RunTargetSelectionStore.cs — 実行先セレクタの「前回の選択」をプロジェクトごとに覚える
//
//  【置き場】プロジェクトの実行状態 &lt;プロジェクト&gt;/cache/android/run_state.json（State/AndroidRunState.cs）の
//    editor_target … エディタで最後に選んだもの（"pc"・"auto"（Android（自動））・端末のシリアル。PC を選んだことも覚える）
//    last_target   … 最後に Android で実行した端末（パイプラインが実行のたびに書く。SeedAndroid で実行した分も入る）
//  を読む。editor_target が無ければ last_target を「前回の選択」とみなす（SeedAndroid だけで使っていたプロジェクトを
//  エディタで開いたときも、前回の端末が既定になる）。
//
//  書くのは editor_target だけ（他の記録は読み込んだまま書き戻す）。実行中はセレクタを変えられないので、
//  パイプラインが同じファイルを書くのと重ならない。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using SEEDEditor.Android.State;

namespace SEEDEditor.AndroidRun;

/// <summary>前回の選択。</summary>
/// <param name="PreferredId">選んでおきたいもの（"pc"・"auto"・端末のシリアル。記録が無ければ null）。</param>
/// <param name="PreferredName">端末の名前（機種。分かれば。見えなくなった端末の行の文言に使う）。</param>
public sealed record RunTargetMemory(string? PreferredId, string? PreferredName)
{
    /// <summary>記録なし。</summary>
    public static readonly RunTargetMemory None = new(null, null);

    /// <summary>
    /// 前回の選択が特定の Android の端末か（起動時に端末の一覧を取りに行くかの判断に使う。PC と Android（自動）は
    /// 一覧が無くても戻せるので adb を呼ばない）。
    /// </summary>
    public bool PrefersAndroidDevice =>
        !string.IsNullOrWhiteSpace(PreferredId) && PreferredId != RunTargetEntry.PcId && PreferredId != RunTargetEntry.AndroidAutoId;
}

/// <summary>実行先セレクタの前回の選択の読み書き。</summary>
public static class RunTargetSelectionStore
{
    /// <summary>
    /// 前回の選択を読む（プロジェクトが無い・記録が無い・壊れていれば記録なし）。
    /// </summary>
    /// <param name="projectRoot">プロジェクトのルート（空なら記録なし）。</param>
    /// <returns>前回の選択。</returns>
    public static RunTargetMemory Load(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return RunTargetMemory.None;
        var state = AndroidRunState.Load(AndroidRunState.PathForProject(projectRoot));
        var last = state.LastTarget;
        if (!string.IsNullOrWhiteSpace(state.EditorTarget))
        {
            // 機種は、同じ端末で実行した記録があるときだけ分かる
            var name = last is not null && string.Equals(last.Serial, state.EditorTarget, StringComparison.Ordinal) ? last.Model : null;
            return new RunTargetMemory(state.EditorTarget, name);
        }
        return last is not null ? new RunTargetMemory(last.Serial, last.Model) : RunTargetMemory.None;
    }

    /// <summary>
    /// 選択を書く（変わっていなければ書かない）。
    /// </summary>
    /// <param name="projectRoot">プロジェクトのルート（空なら書かない）。</param>
    /// <param name="targetId">選んだもの（"pc" か端末のシリアル）。</param>
    /// <returns>書いたら true。</returns>
    /// <exception cref="System.IO.IOException">書けなかったとき（呼び出し側がログに出す）。</exception>
    /// <exception cref="UnauthorizedAccessException">書けなかったとき。</exception>
    public static bool Save(string? projectRoot, string targetId)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(targetId)) return false;
        var path = AndroidRunState.PathForProject(projectRoot);
        var state = AndroidRunState.Load(path);
        if (string.Equals(state.EditorTarget, targetId, StringComparison.Ordinal)) return false;
        state.EditorTarget = targetId;
        state.Save(path);
        return true;
    }
}
