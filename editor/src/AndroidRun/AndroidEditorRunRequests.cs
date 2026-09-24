// ============================================================
//  AndroidEditorRunRequests.cs — エディタが中核へ渡す Android の実行の指定（実行ボタン・パッケージ化ウィンドウ）
//
//  指定の中身（何を行い、何を省くか）をここ 1 か所で決める。中核の型（AndroidRunRequest）は SeedAndroid と共有。
//    実行ボタン（Play）    … Goal = Run: プロジェクトの pak とスクリプトを APK に入れて端末へ入れ、起動して止めるまで logcat
//    パッケージ化ウィンドウ … Goal = Build: 端末を使わずに APK を作るだけ（ABI と Rust の最適化を指定）
//  どちらも変更の無い工程は中核が自動で飛ばす（エディタの実行とパッケージ化とで置き場を共有する）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System.Collections.Generic;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.AndroidRun;

/// <summary>エディタが使う Android の実行の指定を作る。</summary>
public static class AndroidEditorRunRequests
{
    /// <summary>
    /// 実行ボタン（Play）の指定。ABI は端末から決め、Rust は debug（SeedAndroid の既定と同じ。最適化したものは
    /// パッケージ化ウィンドウで作る）。logcat は止めるまで流す。
    /// </summary>
    /// <param name="projectDir">プロジェクトのルート。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <returns>指定。</returns>
    public static AndroidRunRequest ForPlay(string projectDir, string serial) => new()
    {
        Goal = AndroidRunGoal.Run,
        ProjectDir = projectDir,
        Serial = serial,
    };

    /// <summary>
    /// パッケージ化ウィンドウの指定（端末を使わずに APK を作る。できる APK はデバッグ署名）。
    /// </summary>
    /// <param name="projectDir">プロジェクトのルート。</param>
    /// <param name="abis">APK に詰める ABI の名前。</param>
    /// <param name="release">Rust を --release でビルドするか。</param>
    /// <returns>指定。</returns>
    public static AndroidRunRequest ForPackage(string projectDir, IReadOnlyList<string> abis, bool release) => new()
    {
        Goal = AndroidRunGoal.Build,
        ProjectDir = projectDir,
        Abis = abis,
        Release = release,
    };
}
