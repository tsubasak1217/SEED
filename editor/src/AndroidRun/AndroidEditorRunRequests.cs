// ============================================================
//  AndroidEditorRunRequests.cs — エディタが中核へ渡す Android の実行の指定（実行ボタン・パッケージ化ウィンドウ）
//
//  指定の中身（何を行い、何を省くか）をここ 1 か所で決める。中核の型（AndroidRunRequest）は SeedAndroid と共有。
//    実行ボタン（Play）    … Goal = Run: プロジェクトの pak とスクリプトを APK に入れて端末へ入れ、起動して止めるまで logcat
//                            実行先が Android（自動）なら serial = "auto"（実機 → 起動中のエミュレータ → AVD を起動）、
//                            端末なら そのシリアル＋emulator_fallback（見えなければエミュレータで実行。段階C-3）。
//                            起動するシーン（開いているシーン）とエミュレータの AVD（エディタの設定）も渡す。
//                            一時停止などの IPC のポート（エディタの設定 android.ipc_port。未設定なら既定。段階D-1）も渡す。
//                            libSEED.so の構成はツールバーのランタイムのビルド構成（PC の Play と同じ id。既定 develop）を渡す
//                            （2026-09-28。Android/Native/AndroidNativeProfile.cs）。
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
using SEEDEditor.Android.Signing;
using SEEDEditor.Packaging;

namespace SEEDEditor.AndroidRun;

/// <summary>エディタが使う Android の実行の指定を作る。</summary>
public static class AndroidEditorRunRequests
{
    /// <summary>
    /// 実行ボタン（Play）の指定。ABI は端末から決め、Rust はツールバーのランタイムのビルド構成（<paramref name="nativeProfile"/>。
    /// 未指定なら構成の表の既定＝develop。SeedAndroid の既定と同じ）。logcat は止めるまで流す。
    /// </summary>
    /// <param name="projectDir">プロジェクトのルート。</param>
    /// <param name="target">実行先（Android（自動）か端末の行）。</param>
    /// <param name="scenePath">
    /// 起動するシーン（アセットルートからの相対パス。null なら開始シーン。AndroidRunSceneChoice）。
    /// シーンマネージャに未登録なら中核が pak の収録の起点に足す（段階C-4）。
    /// </param>
    /// <param name="emulatorAvd">エミュレータを起動するときの AVD（エディタの設定 android.emulator_avd。未設定なら null）。</param>
    /// <param name="ipcPort">
    /// 端末のランタイムが一時停止などの IPC を待ち受けるポート（エディタの設定 android.ipc_port。未設定なら null＝既定のポート、
    /// 0 なら使わない。段階D-1）。
    /// </param>
    /// <param name="nativeProfile">
    /// libSEED.so の構成の id（ツールバーのランタイムのビルド構成。editor/config/runtime_build_configs.json の debug / develop / release。
    /// null なら表の既定＝develop）。PC の Play と Android の実行で同じ構成を使う（2026-09-28）。
    /// </param>
    /// <returns>指定。</returns>
    public static AndroidRunRequest ForPlay(
        string projectDir, RunTargetEntry target, string? scenePath, string? emulatorAvd, int? ipcPort = null,
        string? nativeProfile = null) => new()
    {
        Goal = AndroidRunGoal.Run,
        ProjectDir = projectDir,
        Serial = target.IsAndroidAuto ? AndroidDeviceTarget.AutoSerial : target.Serial,
        // 端末を選んでいて見えないときは、エミュレータで実行する（利用者の要望。Output に 1 行出す）
        EmulatorFallback = !target.IsAndroidAuto,
        Avd = string.IsNullOrWhiteSpace(emulatorAvd) ? null : emulatorAvd.Trim(),
        ScenePath = scenePath,
        IpcPort = ipcPort,
        NativeProfile = string.IsNullOrWhiteSpace(nativeProfile) ? null : nativeProfile.Trim(),
    };

    /// <summary>
    /// パッケージ化ウィンドウの指定（端末を使わずに APK を作る。できる APK はデバッグ署名）。
    /// </summary>
    /// <param name="projectDir">プロジェクトのルート。</param>
    /// <param name="abis">APK に詰める ABI の名前。</param>
    /// <param name="release">Rust を --release でビルドするか（しなければ構成の表の既定＝develop。2026-09-28 まで dev）。</param>
    /// <returns>指定。</returns>
    public static AndroidRunRequest ForPackage(string projectDir, IReadOnlyList<string> abis, bool release) => new()
    {
        Goal = AndroidRunGoal.Build,
        ProjectDir = projectDir,
        Abis = abis,
        Release = release,
    };

    /// <summary>
    /// パッケージ化ウィンドウの配布用（release）の指定（段階D。端末を使わずに APK / AAB を作る。Rust は常に --release）。
    /// キーストア・別名は指定が無ければ中核がプロジェクトの packaging_settings.json の android.signing から読む。
    /// パスワードはエディタの保護保存から取り出したもの（無ければ中核が環境変数を見る）。
    /// </summary>
    /// <param name="projectDir">プロジェクトのルート。</param>
    /// <param name="abis">詰める ABI の名前。</param>
    /// <param name="format">形式。</param>
    /// <param name="keystorePath">キーストア（null なら設定ファイルから）。</param>
    /// <param name="keyAlias">別名（null なら設定ファイルから）。</param>
    /// <param name="secrets">パスワード（null なら環境変数）。</param>
    /// <returns>指定。</returns>
    public static AndroidRunRequest ForReleasePackage(
        string projectDir, IReadOnlyList<string> abis, AndroidPackageFormat format,
        string? keystorePath, string? keyAlias, AndroidSigningSecrets? secrets) => new()
    {
        Goal = AndroidRunGoal.Build,
        ProjectDir = projectDir,
        Abis = abis,
        Release = true,
        Variant = AndroidBuildVariant.Release,
        Format = format,
        KeystorePath = string.IsNullOrWhiteSpace(keystorePath) ? null : keystorePath.Trim(),
        KeyAlias = string.IsNullOrWhiteSpace(keyAlias) ? null : keyAlias.Trim(),
        SigningSecrets = secrets,
    };
}
