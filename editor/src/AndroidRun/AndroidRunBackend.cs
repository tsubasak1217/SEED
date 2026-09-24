// ============================================================
//  AndroidRunBackend.cs — エディタの Android の実行が使う中核の入口（差し替え可能な窓口）
//
//  AndroidRunController（状態機械・見張り・停止の段取り）は、ビルド・端末の操作をこの窓口越しに呼ぶ。
//  本番は中核（editor/src/Android/。SeedAndroid と同じクラス）をそのまま呼び、単体テストは偽物に差し替えて
//  「停止ボタン・アプリの終了・失敗のときに状態がどう動くか」を端末なしで確かめる。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.AndroidRun;

/// <summary>Android の実行が使う中核の入口。</summary>
public interface IAndroidRunBackend
{
    /// <summary>
    /// ビルド・配置・起動・logcat を行う（失敗しても例外は投げず結果に入れる。中核の約束）。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="progress">進み具合・ログの送り先（複数のスレッドから呼ばれる）。</param>
    /// <param name="cancellationToken">中断の合図（子プロセスとその子孫を止める。logcat の途中なら「止めた」＝成功）。</param>
    /// <returns>結果。</returns>
    Task<AndroidPipelineResult> RunAsync(AndroidRunRequest request, IProgress<AndroidPipelineEvent> progress, CancellationToken cancellationToken);

    /// <summary>端末のアプリを止める（am force-stop）。</summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="cancellationToken">中断の合図（実行の中断とは別の、新しい合図を渡す）。</param>
    /// <returns>完了。</returns>
    Task StopAppAsync(string serial, string applicationId, CancellationToken cancellationToken);

    /// <summary>端末でアプリが動いているか（pidof）。</summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>動いていれば true。</returns>
    Task<bool> IsAppRunningAsync(string serial, string applicationId, CancellationToken cancellationToken);
}

/// <summary>本番の入口（中核の AndroidRunPipeline / AndroidDeviceActions をそのまま呼ぶ）。</summary>
public sealed class AndroidRunBackend : IAndroidRunBackend
{
    /// <summary>ビルド・配置・起動の本体。</summary>
    private readonly AndroidRunPipeline _pipeline;

    /// <summary>端末の操作。</summary>
    private readonly AndroidDeviceActions _deviceActions;

    /// <summary>置き場と道具を指定して作る。</summary>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="toolchain">道具の場所。</param>
    public AndroidRunBackend(AndroidEnginePaths engine, AndroidToolchain toolchain)
    {
        _pipeline = new AndroidRunPipeline(engine, toolchain);
        _deviceActions = new AndroidDeviceActions(toolchain);
    }

    /// <inheritdoc />
    public Task<AndroidPipelineResult> RunAsync(
        AndroidRunRequest request, IProgress<AndroidPipelineEvent> progress, CancellationToken cancellationToken) =>
        _pipeline.RunAsync(request, progress, cancellationToken);

    /// <inheritdoc />
    public Task StopAppAsync(string serial, string applicationId, CancellationToken cancellationToken) =>
        _deviceActions.StopAppAsync(serial, applicationId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsAppRunningAsync(string serial, string applicationId, CancellationToken cancellationToken) =>
        _deviceActions.IsAppRunningAsync(serial, applicationId, cancellationToken);
}
