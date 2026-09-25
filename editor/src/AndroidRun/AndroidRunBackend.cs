// ============================================================
//  AndroidRunBackend.cs — エディタの Android の実行が使う中核の入口（差し替え可能な窓口）
//
//  AndroidRunController（状態機械・見張り・停止・一時停止の段取り）は、ビルド・端末の操作・端末のアプリとの IPC
//  （段階D-1）をこの窓口越しに呼ぶ。本番は中核（editor/src/Android/。SeedAndroid と同じクラス）をそのまま呼び、
//  単体テストは偽物に差し替えて「停止ボタン・アプリの終了・失敗・一時停止・切断のときに状態がどう動くか」を端末なしで確かめる。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Ipc;
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

    /// <summary>
    /// 端末のアプリの IPC へつなぐ（adb forward ＋ TCP。接続トークンを示し、挨拶まで確かめる。段階D-1）。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="devicePort">端末でランタイムが待ち受けているポート。</param>
    /// <param name="token">接続トークン（この実行の指定に入れて端末へ渡したもの）。</param>
    /// <param name="cancellationToken">中断の合図（実行を止めたら取り消す）。</param>
    /// <returns>つながった通信路。</returns>
    /// <exception cref="AndroidIpcException">時間内につながらない（古い APK 等）・断られた。</exception>
    Task<IAndroidIpcLink> ConnectIpcAsync(string serial, int devicePort, string token, CancellationToken cancellationToken);
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

    /// <inheritdoc />
    public async Task<IAndroidIpcLink> ConnectIpcAsync(string serial, int devicePort, string token, CancellationToken cancellationToken) =>
        await _deviceActions.ConnectIpcAsync(serial, devicePort, token, AndroidIpcTimings.Default, cancellationToken).ConfigureAwait(false);
}
