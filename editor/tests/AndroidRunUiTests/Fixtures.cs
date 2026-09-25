using System.Collections.Concurrent;
using System.Diagnostics;
using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.AndroidRun;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>テストで使う端末・アプリ・待ち合わせの部品。</summary>
public static class Fixtures
{
    /// <summary>実機のシリアル（Pixel 6a の形）。</summary>
    public const string PhoneSerial = "2B011JEGR02535";

    /// <summary>エミュレータのシリアル。</summary>
    public const string EmulatorSerial = "emulator-5554";

    /// <summary>テストのアプリ ID。</summary>
    public const string ApplicationId = "com.seedengine.uitest";

    /// <summary>待ち合わせの上限（ミリ秒）。遅い PC でも通るよう長めにとる（通常は数十ミリ秒で満たされる）。</summary>
    public const int WaitTimeoutMs = 10_000;

    /// <summary>待ち合わせの確認の間隔（ミリ秒）。</summary>
    private const int WaitPollMs = 5;

    /// <summary>使える実機（arm64）。</summary>
    public static AndroidDeviceEntry ReadyPhone() => Ready(PhoneSerial, "Pixel_6a", AdbDeviceKind.Physical, "arm64-v8a,armeabi-v7a,armeabi");

    /// <summary>使えるエミュレータ（x86_64）。</summary>
    public static AndroidDeviceEntry ReadyEmulator() => Ready(EmulatorSerial, "sdk_gphone64_x86_64", AdbDeviceKind.Emulator, "x86_64,arm64-v8a");

    /// <summary>使える端末。</summary>
    /// <param name="serial">シリアル。</param>
    /// <param name="model">機種。</param>
    /// <param name="kind">種類。</param>
    /// <param name="abiList">端末の abilist（null なら読めなかった）。</param>
    /// <returns>端末。</returns>
    public static AndroidDeviceEntry Ready(string serial, string? model, AdbDeviceKind kind, string? abiList) =>
        new(new AdbDevice(serial, AdbDeviceState.Ready, "device", kind, model, null, null, null),
            abiList, abiList is null ? null : AndroidAbis.ChooseForDevice(abiList));

    /// <summary>使えない状態の端末。</summary>
    /// <param name="serial">シリアル。</param>
    /// <param name="state">状態。</param>
    /// <param name="stateText">adb の状態の文字列。</param>
    /// <returns>端末。</returns>
    public static AndroidDeviceEntry NotReady(string serial, AdbDeviceState state, string stateText) =>
        new(new AdbDevice(serial, state, stateText, AdbDeviceKind.Physical, null, null, null, null), null, null);

    /// <summary>テストのアプリの識別情報。</summary>
    public static AndroidAppIdentity Identity() => new(
        ApplicationId, AndroidIdentitySource.ProjectDefault,
        new AndroidIdentityValue<string>("UI テスト", AndroidIdentitySource.ProjectDefault),
        new AndroidIdentityValue<int?>(1, AndroidIdentitySource.FixedDefault),
        new AndroidIdentityValue<string>("1.0", AndroidIdentitySource.FixedDefault));

    /// <summary>実行先の行（端末）。</summary>
    public static RunTargetEntry PhoneTarget() => RunTargetCatalogBuilder.FromDevice(ReadyPhone());

    /// <summary>条件が満たされるまで待つ（満たされなければ失敗）。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="what">条件の説明（失敗時に出す）。</param>
    public static void WaitUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > WaitTimeoutMs) throw new AssertionException($"{WaitTimeoutMs} ms 以内に {what} になりません");
            Thread.Sleep(WaitPollMs);
        }
    }

    /// <summary>タスクの完了を待つ（時間内に終わらなければ失敗）。</summary>
    /// <param name="task">タスク。</param>
    /// <param name="what">説明。</param>
    public static void Await(Task task, string what)
    {
        if (!task.Wait(WaitTimeoutMs)) throw new AssertionException($"{WaitTimeoutMs} ms 以内に {what} が終わりません");
    }
}

/// <summary>
/// 偽の中核（端末・adb を使わない）。RunAsync の中身をテストごとに差し替え、停止・pidof の呼ばれ方を記録する。
/// </summary>
public sealed class FakeBackend : IAndroidRunBackend
{
    /// <summary>RunAsync の中身。</summary>
    public Func<AndroidRunRequest, IProgress<AndroidPipelineEvent>, CancellationToken, Task<AndroidPipelineResult>> Run { get; set; } =
        (_, _, _) => Task.FromResult(new AndroidPipelineResult());

    /// <summary>pidof の答え（呼ばれた回数を渡す。null を返すと adb の失敗＝例外）。</summary>
    public Func<int, bool?> Running { get; set; } = _ => true;

    /// <summary>アプリの停止を失敗させる例外（null なら成功）。</summary>
    public Exception? StopFailure { get; set; }

    /// <summary>受け取った指定。</summary>
    public ConcurrentQueue<AndroidRunRequest> Requests { get; } = new();

    /// <summary>アプリの停止の呼ばれ方（シリアル・アプリ ID・呼ばれた時点で合図が取り消されていたか）。</summary>
    public ConcurrentQueue<(string Serial, string ApplicationId, bool TokenCanceled)> StopCalls { get; } = new();

    /// <summary>pidof が呼ばれた回数。</summary>
    public int RunningQueries => _runningQueries;

    /// <summary>pidof が呼ばれた回数（Interlocked）。</summary>
    private int _runningQueries;

    /// <inheritdoc />
    public Task<AndroidPipelineResult> RunAsync(AndroidRunRequest request, IProgress<AndroidPipelineEvent> progress, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Run(request, progress, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAppAsync(string serial, string applicationId, CancellationToken cancellationToken)
    {
        StopCalls.Enqueue((serial, applicationId, cancellationToken.IsCancellationRequested));
        return StopFailure is null ? Task.CompletedTask : Task.FromException(StopFailure);
    }

    /// <inheritdoc />
    public Task<bool> IsAppRunningAsync(string serial, string applicationId, CancellationToken cancellationToken)
    {
        var count = Interlocked.Increment(ref _runningQueries);
        var answer = Running(count);
        return answer is bool running
            ? Task.FromResult(running)
            : Task.FromException<bool>(new AndroidPipelineException(AndroidFailureKind.DeviceOperation, "adb: device offline"));
    }

    /// <summary>
    /// IPC の接続の中身（呼ばれた回数を渡す。既定は「つながらない」＝段階D-1 より前と同じく一時停止できない）。
    /// </summary>
    public Func<int, CancellationToken, Task<IAndroidIpcLink>> ConnectIpc { get; set; } =
        (_, _) => Task.FromException<IAndroidIpcLink>(new AndroidIpcException(AndroidIpcFailureKind.NotListening, "テスト: 待ち受けていません"));

    /// <summary>IPC の接続の呼ばれ方（シリアル・端末側のポート）。</summary>
    public ConcurrentQueue<(string Serial, int DevicePort)> IpcConnects { get; } = new();

    /// <summary>IPC の接続が呼ばれた回数（Interlocked）。</summary>
    private int _ipcConnects;

    /// <inheritdoc />
    public Task<IAndroidIpcLink> ConnectIpcAsync(string serial, int devicePort, CancellationToken cancellationToken)
    {
        IpcConnects.Enqueue((serial, devicePort));
        return ConnectIpc(Interlocked.Increment(ref _ipcConnects), cancellationToken);
    }
}

/// <summary>
/// 偽の IPC の通信路（端末・adb を使わない）。送った命令を記録し、テストから切断を起こせる。
/// </summary>
public sealed class FakeIpcLink : IAndroidIpcLink
{
    /// <summary>切れると完了する。</summary>
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>送った命令。</summary>
    public ConcurrentQueue<string> Sent { get; } = new();

    /// <summary>閉じ方（CloseAsync の detach）。閉じていなければ空。</summary>
    public ConcurrentQueue<bool> Closes { get; } = new();

    /// <summary>送るのを失敗させるか（切れている扱い）。</summary>
    public bool FailSend { get; set; }

    /// <inheritdoc />
    public Task Closed => _closed.Task;

    /// <inheritdoc />
    public bool Send(string command)
    {
        if (FailSend || _closed.Task.IsCompleted) return false;
        Sent.Enqueue(command);
        return true;
    }

    /// <inheritdoc />
    public Task CloseAsync(bool detach)
    {
        Closes.Enqueue(detach);
        _closed.TrySetResult();
        return Task.CompletedTask;
    }

    /// <summary>端末側から切れた（アプリが終わった・adb が切れた）ことにする。</summary>
    public void Disconnect() => _closed.TrySetResult();
}

/// <summary>偽のパイプラインの台本（中核の RunAsync と同じ順でイベントを出す）。</summary>
public static class PipelineScript
{
    /// <summary>工程の数（libSEED.so を飛ばす・起動・logcat の 3 つ）。</summary>
    public const int StepCount = 3;

    /// <summary>準備（Prepare の始まり・決まった値・終わり）と、飛ばした 1 工程を出す。</summary>
    /// <param name="progress">送り先。</param>
    public static void Prepare(IProgress<AndroidPipelineEvent> progress)
    {
        progress.Report(new AndroidPhaseStarted(AndroidPipelinePhase.Prepare, 0, 0, "準備", "Run"));
        progress.Report(new AndroidPrepared(Fixtures.ReadyPhone().Device, Fixtures.Identity(), new[] { AndroidAbis.Arm64 }));
        progress.Report(new AndroidPhaseFinished(AndroidPipelinePhase.Prepare, 0, StepCount, "準備", AndroidPhaseOutcome.Succeeded,
            TimeSpan.FromMilliseconds(100), "2 工程を行い、1 工程を飛ばします"));
        progress.Report(new AndroidPhaseFinished(AndroidPipelinePhase.NativeBuild, 1, StepCount, "libSEED.so のビルド（cargo ndk）",
            AndroidPhaseOutcome.Skipped, TimeSpan.Zero, "変更なし"));
        progress.Report(new AndroidProgressChanged(AndroidPipelinePhase.NativeBuild, 1.0 / StepCount, "libSEED.so のビルド（cargo ndk）"));
    }

    /// <summary>起動（成功）を出す。</summary>
    /// <param name="progress">送り先。</param>
    public static void Launch(IProgress<AndroidPipelineEvent> progress)
    {
        progress.Report(new AndroidPhaseStarted(AndroidPipelinePhase.Launch, 2, StepCount, "起動", "止めてから起動し直す"));
        progress.Report(new AndroidPhaseFinished(AndroidPipelinePhase.Launch, 2, StepCount, "起動", AndroidPhaseOutcome.Succeeded,
            TimeSpan.FromSeconds(1), "LaunchState: COLD"));
        progress.Report(new AndroidProgressChanged(AndroidPipelinePhase.Launch, 2.0 / StepCount, "起動"));
    }

    /// <summary>
    /// 準備 → 起動 → logcat（止められるまで流し、止められたら「止めた」＝成功で戻る。中核の LogcatStep と同じ）。
    /// </summary>
    /// <param name="progress">送り先。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>結果。</returns>
    public static async Task<AndroidPipelineResult> RunUntilCanceledAsync(IProgress<AndroidPipelineEvent> progress, CancellationToken cancellationToken)
    {
        Prepare(progress);
        Launch(progress);
        progress.Report(new AndroidPhaseStarted(AndroidPipelinePhase.Logcat, 3, StepCount, "logcat", "止めるまで流す"));
        progress.Report(new AndroidLogLine(AndroidPipelinePhase.Logcat, AndroidLogLevel.Logcat,
            "09-25 14:03:12.345  1234  1250 I DOTNET  : [Script] hello"));
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // logcat の途中の中断は「止めた」＝成功
        }
        progress.Report(new AndroidPhaseFinished(AndroidPipelinePhase.Logcat, 3, StepCount, "logcat", AndroidPhaseOutcome.Succeeded,
            TimeSpan.FromSeconds(2), "1 行"));
        return new AndroidPipelineResult { Steps = Steps(AndroidPhaseOutcome.Succeeded), Elapsed = TimeSpan.FromSeconds(3) };
    }

    /// <summary>結果の工程の並び（最後の工程の終わり方を指定）。</summary>
    /// <param name="last">最後の工程の終わり方。</param>
    /// <returns>工程の結果。</returns>
    public static IReadOnlyList<AndroidStepOutcome> Steps(AndroidPhaseOutcome last) => new[]
    {
        new AndroidStepOutcome(AndroidPipelinePhase.Prepare, AndroidPhaseOutcome.Succeeded, "準備", TimeSpan.Zero),
        new AndroidStepOutcome(AndroidPipelinePhase.NativeBuild, AndroidPhaseOutcome.Skipped, "変更なし", TimeSpan.Zero),
        new AndroidStepOutcome(AndroidPipelinePhase.Launch, AndroidPhaseOutcome.Succeeded, "LaunchState: COLD", TimeSpan.FromSeconds(1)),
        new AndroidStepOutcome(AndroidPipelinePhase.Logcat, last, "1 行", TimeSpan.FromSeconds(2)),
    };
}
