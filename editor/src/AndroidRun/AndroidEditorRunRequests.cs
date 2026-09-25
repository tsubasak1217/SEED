// ============================================================
//  AndroidEditorRunRequests.cs — エディタが中核へ渡す Android の実行の指定（実行ボタン・パッケージ化ウィンドウ）
//
//  指定の中身（何を行い、何を省くか）をここ 1 か所で決める。中核の型（AndroidRunRequest）は SeedAndroid と共有。
//    実行ボタン（Play）    … Goal = Run: プロジェクトの pak とスクリプトを APK に入れて端末へ入れ、起動して止めるまで logcat
//                            実行先が Android（自動）なら serial = "auto"（実機 → 起動中のエミュレータ → AVD を起動）、
//                            端末なら そのシリアル＋emulator_fallback（見えなければエミュレータで実行。段階C-3）。
//                            起動するシーン（開いているシーン）とエミュレータの AVD（エディタの設定）も渡す。
//                            開いているシーンがシーンマネージャに未登録なら、中核の準備がそれを pak の収録の起点に足す
//                            （ScenePath から決める。Android/Project/AndroidPakSceneSeeds。SeedAndroid の --scene と同じ経路。段階C-4）
//    パッケージ化ウィンドウ … Goal = Build: 端末を使わずに APK を作るだけ（ABI と Rust の最適化を指定）。
//                            シーンを渡さないので追加の起点は無く、pak は登録シーンだけから作る（配布物と同じ中身）
//  どちらも変更の無い工程は中核が自動で飛ばす（エディタの実行とパッケージ化とで置き場を共有する）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System.Collections.Generic;
using SEEDEditor.Android.Adb;
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
    /// <param name="target">実行先（Android（自動）か端末の行）。</param>
    /// <param name="scenePath">
    /// 起動するシーン（アセットルートからの相対パス。null なら開始シーン。AndroidRunSceneChoice）。
    /// シーンマネージャに未登録なら中核が pak の収録の起点に足す（段階C-4）。
    /// </param>
    /// <param name="emulatorAvd">エミュレータを起動するときの AVD（エディタの設定 android.emulator_avd。未設定なら null）。</param>
    /// <returns>指定。</returns>
    public static AndroidRunRequest ForPlay(string projectDir, RunTargetEntry target, string? scenePath, string? emulatorAvd) => new()
    {
        Goal = AndroidRunGoal.Run,
        ProjectDir = projectDir,
        Serial = target.IsAndroidAuto ? AndroidDeviceTarget.AutoSerial : target.Serial,
        // 端末を選んでいて見えないときは、エミュレータで実行する（利用者の要望。Output に 1 行出す）
        EmulatorFallback = !target.IsAndroidAuto,
        Avd = string.IsNullOrWhiteSpace(emulatorAvd) ? null : emulatorAvd.Trim(),
        ScenePath = scenePath,
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
