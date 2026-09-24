// ============================================================
//  AndroidPipelinePhase.cs — Android のビルド・配置・起動の工程（段階）の一覧と表示名
//
//  並びは実行の順（従来の build_and_run.ps1 の [1/7]〜[7/7] と同じ流れ）。
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

namespace SEEDEditor.Android.Pipeline;

/// <summary>工程。</summary>
public enum AndroidPipelinePhase
{
    /// <summary>準備（道具・プロジェクト・端末の確認と実行計画）。</summary>
    Prepare,

    /// <summary>cargo ndk で libSEED.so を作る。</summary>
    NativeBuild,

    /// <summary>APK に入れる配布物（assets.pak・スクリプトの bin/）を SeedPak で作る。</summary>
    PackageContent,

    /// <summary>同梱 .NET（CoreCLR / Mono）を組み立てる。</summary>
    DotnetBundle,

    /// <summary>Gradle で APK を作る。</summary>
    Gradle,

    /// <summary>adb install で端末へ入れる。</summary>
    Install,

    /// <summary>開発用: アセットのフォルダを端末へ送る（run-as）。</summary>
    PushAssets,

    /// <summary>開発用: スクリプトの DLL だけを作り直して端末へ送る（run-as）。</summary>
    PushScripts,

    /// <summary>アプリを止めて起動し直す。</summary>
    Launch,

    /// <summary>logcat を流す。</summary>
    Logcat,

    /// <summary>アプリを止める（stop コマンド）。</summary>
    Stop,
}

/// <summary>工程の表示名。</summary>
public static class AndroidPipelinePhaseNames
{
    /// <summary>工程の表示名（ログ・エディタの Output パネル用）。</summary>
    /// <param name="phase">工程。</param>
    /// <returns>表示名。</returns>
    public static string Title(AndroidPipelinePhase phase) => phase switch
    {
        AndroidPipelinePhase.Prepare        => "準備",
        AndroidPipelinePhase.NativeBuild    => "libSEED.so のビルド（cargo ndk）",
        AndroidPipelinePhase.PackageContent => "APK に入れる pak とスクリプト（SeedPak）",
        AndroidPipelinePhase.DotnetBundle   => "同梱 .NET の組み立て",
        AndroidPipelinePhase.Gradle         => "APK の作成（Gradle）",
        AndroidPipelinePhase.Install        => "インストール（adb install）",
        AndroidPipelinePhase.PushAssets     => "アセットの転送（run-as）",
        AndroidPipelinePhase.PushScripts    => "スクリプトの DLL の転送（run-as）",
        AndroidPipelinePhase.Launch         => "起動",
        AndroidPipelinePhase.Logcat         => "logcat",
        AndroidPipelinePhase.Stop           => "停止",
        _                                   => phase.ToString(),
    };
}
