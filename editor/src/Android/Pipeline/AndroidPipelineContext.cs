// ============================================================
//  AndroidPipelineContext.cs — 1 回の実行の間、工程どうしで共有する値
//
//  準備（AndroidRunPipeline.PrepareAsync）で決まった値（道具・プロジェクト・端末・ABI・記録）を持ち、
//  各工程（Steps/）はここから読み、結果（記録の更新・logcat の起点の時刻）をここへ書く。
//  工程は順に 1 つずつ動くので、書き込みの競合は無い。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Android.Pipeline;

/// <summary>1 回の実行の共有の値。</summary>
public sealed class AndroidPipelineContext
{
    /// <summary>指定。</summary>
    public required AndroidRunRequest Request { get; init; }

    /// <summary>エンジン側の置き場。</summary>
    public required AndroidEnginePaths Engine { get; init; }

    /// <summary>道具の場所。</summary>
    public required AndroidToolchain Toolchain { get; init; }

    /// <summary>プロジェクト（アセットを指定しない開発用のビルドでは null）。</summary>
    public AndroidProjectInfo? Project { get; init; }

    /// <summary>アプリの識別情報。</summary>
    public required AndroidAppIdentity Identity { get; init; }

    /// <summary>画面の向き（正規化済みの設定値）。</summary>
    public required string ScreenOrientation { get; init; }

    /// <summary>今回の ABI。</summary>
    public required IReadOnlyList<AndroidAbi> Abis { get; init; }

    /// <summary>対象の端末（端末の工程が無ければ null）。</summary>
    public AdbDevice? Device { get; init; }

    /// <summary>adb（端末の工程が無ければ null）。</summary>
    public AdbClient? Adb { get; init; }

    /// <summary>置き場の中身の記録（エンジン側）。</summary>
    public required AndroidStepStamps Stamps { get; init; }

    /// <summary>プロジェクトの実行状態。</summary>
    public required AndroidRunState RunState { get; init; }

    /// <summary>実行状態の置き場。</summary>
    public required string RunStatePath { get; init; }

    /// <summary>準備の時点の各工程の指紋（計画の材料。工程を終えたら新しい値で記録する）。</summary>
    public Dictionary<string, AndroidStepFingerprint> CurrentFingerprints { get; } = new();

    /// <summary>logcat の起点の端末の時刻（起動の直前に控える）。</summary>
    public string? LogcatSince { get; set; }

    /// <summary>対象の端末（無ければ例外）。</summary>
    /// <returns>端末。</returns>
    public AdbDevice RequireDevice() =>
        Device ?? throw new AndroidPipelineException(AndroidFailureKind.Device, "端末が決まっていません。");

    /// <summary>adb（無ければ例外）。</summary>
    /// <returns>adb。</returns>
    public AdbClient RequireAdb() =>
        Adb ?? throw new AndroidPipelineException(AndroidFailureKind.Device, "adb が準備されていません。");

    /// <summary>起動する Activity（&lt;アプリ ID&gt;/&lt;完全修飾のクラス名&gt;）。</summary>
    public string LaunchComponent => $"{Identity.ApplicationId}/{AndroidRuntimeContract.LaunchActivityClassName}";
}
