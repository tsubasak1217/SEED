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
using SEEDEditor.Android.Icons;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Release;
using SEEDEditor.Android.Signing;
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

    /// <summary>
    /// 端末で起動するシーン（アセットルートからの相対パス。null なら開始シーン）。起動の工程が am start の extra
    /// （seed.scene）にして渡す（段階C-3）。
    /// </summary>
    public string? LaunchScene { get; init; }

    /// <summary>
    /// APK の pak の収録の起点に足すシーン（アセットルートからの相対パス。無ければ空）。起動するシーンがシーンマネージャに
    /// 未登録のとき準備で決め（Project/AndroidPakSceneSeeds）、pak とスクリプトの工程が SeedPak の --extra-scene で渡す。
    /// pak の指紋にも入る（段階C-4）。
    /// </summary>
    public IReadOnlyList<string> PakExtraScenes { get; init; } = System.Array.Empty<string>();

    /// <summary>logcat の起点の端末の時刻（起動の直前に控える）。</summary>
    public string? LogcatSince { get; set; }

    /// <summary>
    /// 端末のランタイムがエディタとの IPC を待ち受けるポート（null なら渡さない）。起動の工程が am start の extra
    /// （seed.ipc_port）にして渡す（段階D-1。指定から Ipc/AndroidIpcSettings.ResolveDevicePort で決める）。
    /// </summary>
    public int? IpcDevicePort { get; init; }

    /// <summary>
    /// IPC の接続トークン（ポートを渡すときだけ。指定のトークンか、無ければ準備で作ったもの。段階D-1）。起動の工程が
    /// am start の extra（seed.ipc_token）にして渡し、実行状態（run_state.json の ipc_launches）へ記録する。
    /// </summary>
    public string? IpcToken { get; init; }

    /// <summary>
    /// ランチャーのアイコンの元（プロジェクト設定 android.icon。無ければ null＝システムの既定のアイコン。段階D）。
    /// APK の工程が Gradle の前に生成物を置く（Icons/LauncherIconStager）。
    /// </summary>
    public LauncherIconSource? LauncherIcon { get; init; }

    /// <summary>配布用（release）の署名（開発用のビルドでは null。段階D）。</summary>
    public AndroidSigningConfig? Signing { get; init; }

    /// <summary>配布用の署名の鍵の証明書の要点（keytool で確かめたもの。開発用のビルドでは null。段階D）。</summary>
    public AndroidKeystoreCertificate? SigningCertificate { get; init; }

    /// <summary>Google Play の要件の表（配布用のビルドだけ。読めなければ null。段階D）。</summary>
    public PlayRequirements? PlayRequirements { get; init; }

    /// <summary>
    /// Google Play の要件チェックの結果（配布用のビルドだけ。準備でビルドの前の判定を入れ、要件の確認の工程ができた配布物の判定で上書きする。段階D）。
    /// </summary>
    public AndroidRequirementReport? RequirementReport { get; init; }

    /// <summary>配布用ビルドの記録の置き場（versionCode の単調増加の材料。段階D）。</summary>
    public required string ReleaseHistoryPath { get; init; }

    /// <summary>今回の Gradle の出力（ビルドの種類と形式ごと。段階D）。</summary>
    public string ArtifactPath => Engine.ArtifactPath(Request.Variant, Request.Format);

    /// <summary>今回の Gradle の出力の記録のキー（Plan/AndroidStepKeys.GradleFor。段階D）。</summary>
    public string GradleKey => AndroidStepKeys.GradleFor(Request.Variant, Request.Format);

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
