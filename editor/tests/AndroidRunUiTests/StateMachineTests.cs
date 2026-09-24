using SEEDEditor.Android;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.AndroidRun;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>Android の実行の状態機械（Idle → Building → Running → Stopping → Idle）。</summary>
public static class StateMachineTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("状態機械: Start は Idle のときだけ（実行中に二重に始めない）", StartsOnlyFromIdle);
        harness.Add("状態機械: 起動の工程が成功すると Running（見張りの合図）・準備の値と進み具合を写しへ", EntersRunningOnLaunch);
        harness.Add("状態機械: 起動前の停止はビルドの中止（アプリに触らない）", StopBeforeLaunchCancelsBuild);
        harness.Add("状態機械: 起動後の停止はアプリも止める（止め終えるまで Stopping）", StopAfterLaunchStopsApp);
        harness.Add("状態機械: アプリの終了は Running のときだけ受け付ける", AppExitOnlyWhileRunning);
        harness.Add("状態機械: 止めていない終わり（logcat の終わり・失敗・中断）とエディタを閉じたとき", NaturalEndings);
        harness.Add("状態機械: Idle に戻った後に届いたイベントは無視する", IgnoresLateEvents);
    }

    /// <summary>起動の工程の成功のイベント。</summary>
    private static AndroidPhaseFinished LaunchSucceeded() =>
        new(AndroidPipelinePhase.Launch, 2, 3, "起動", AndroidPhaseOutcome.Succeeded, TimeSpan.FromSeconds(1), "LaunchState: COLD");

    /// <summary>準備で決まった値のイベント。</summary>
    private static AndroidPrepared Prepared() => new(Fixtures.ReadyPhone().Device, Fixtures.Identity(), new[] { AndroidAbis.Arm64 });

    /// <summary>Start は Idle のときだけ。</summary>
    private static void StartsOnlyFromIdle()
    {
        var machine = new AndroidRunStateMachine();
        Check.Equal(AndroidRunPhase.Idle, machine.Phase, "最初は Idle");
        Check.True(machine.Start("Pixel_6a（実機）", Fixtures.PhoneSerial), "Idle から始められる");
        Check.Equal(AndroidRunPhase.Building, machine.Phase, "Building");
        Check.True(!machine.Start("x", "y"), "動いている途中は始められない");
        Check.Equal(Fixtures.PhoneSerial, machine.Serial, "二重に始めても値は変わらない");
    }

    /// <summary>Running へ。</summary>
    private static void EntersRunningOnLaunch()
    {
        var machine = new AndroidRunStateMachine();
        machine.Start("Pixel_6a（実機）", null);
        Check.True(!machine.Apply(Prepared()), "準備の値では Running に入らない");
        Check.Equal(Fixtures.PhoneSerial, machine.Serial, "端末は準備で決まる（指定が無いとき）");
        Check.Equal(Fixtures.ApplicationId, machine.ApplicationId, "アプリ ID");
        machine.Apply(new AndroidPhaseStarted(AndroidPipelinePhase.Gradle, 1, 3, "APK の作成（Gradle）", "入力が変わった"));
        machine.Apply(new AndroidProgressChanged(AndroidPipelinePhase.Gradle, 1.5, "APK"));
        var snapshot = machine.Snapshot;
        Check.Equal("APK の作成（Gradle）", snapshot.StepTitle, "いまの工程");
        Check.Equal(1, snapshot.StepIndex, "何番目");
        Check.Equal(3, snapshot.StepCount, "工程の数");
        Check.Close(1.0, snapshot.Fraction, 1e-9, "進み具合は 0〜1 に収める");

        machine.Apply(new AndroidPhaseStarted(AndroidPipelinePhase.Launch, 2, 3, "起動", "止めてから起動し直す"));
        Check.True(machine.LaunchStarted, "起動の工程に入った");
        Check.Equal(AndroidRunPhase.Building, machine.Phase, "起動が終わるまでは Building");
        Check.True(!machine.Apply(new AndroidPhaseFinished(AndroidPipelinePhase.Launch, 2, 3, "起動", AndroidPhaseOutcome.Failed, TimeSpan.Zero, "x")),
            "起動の失敗では Running に入らない");
        Check.True(machine.Apply(LaunchSucceeded()), "起動の成功で Running（見張りの合図）");
        Check.Equal(AndroidRunPhase.Running, machine.Phase, "Running");
        Check.True(!machine.Apply(LaunchSucceeded()), "2 回目は合図を出さない");
    }

    /// <summary>起動前の停止。</summary>
    private static void StopBeforeLaunchCancelsBuild()
    {
        var machine = new AndroidRunStateMachine();
        machine.Start("t", Fixtures.PhoneSerial);
        machine.Apply(Prepared());
        Check.True(machine.RequestStop(AndroidRunStopReason.User), "ビルド中に止められる");
        Check.Equal(AndroidRunPhase.Stopping, machine.Phase, "Stopping");
        Check.True(!machine.RequestStop(AndroidRunStopReason.User), "止めている途中の二度押しは受け付けない");

        // 子プロセスが止まって中核が「中断」で戻る
        var completion = machine.Complete(new AndroidPipelineResult { Canceled = true });
        Check.Equal(AndroidRunOutcome.BuildCanceled, completion.Outcome, "ビルドの中止");
        Check.True(!completion.NeedsStopApp, "アプリには触らない");
        Check.Equal(AndroidRunPhase.Idle, machine.Phase, "Idle へ");

        // 停止の後に中核が（子プロセスの終了コードなどで）失敗を返しても、止めた理由が優先
        machine.Start("t", Fixtures.PhoneSerial);
        machine.RequestStop(AndroidRunStopReason.User);
        Check.Equal(AndroidRunOutcome.BuildCanceled,
            machine.Complete(new AndroidPipelineResult { FailureKind = AndroidFailureKind.Build, FailureMessage = "exit 1" }).Outcome,
            "止めた理由が失敗より優先");
    }

    /// <summary>起動後の停止。</summary>
    private static void StopAfterLaunchStopsApp()
    {
        var machine = new AndroidRunStateMachine();
        machine.Start("t", Fixtures.PhoneSerial);
        machine.Apply(Prepared());
        machine.Apply(new AndroidPhaseStarted(AndroidPipelinePhase.Launch, 2, 3, "起動", "r"));
        machine.Apply(LaunchSucceeded());
        Check.True(machine.RequestStop(AndroidRunStopReason.User), "実行中に止められる");

        // logcat の途中の中断は「止めた」＝成功で戻る
        var completion = machine.Complete(new AndroidPipelineResult());
        Check.Equal(AndroidRunOutcome.StoppedByUser, completion.Outcome, "停止");
        Check.True(completion.NeedsStopApp, "アプリも止める");
        Check.Equal(AndroidRunPhase.Stopping, machine.Phase, "アプリを止め終えるまで Stopping");
        machine.FinishStopApp();
        Check.Equal(AndroidRunPhase.Idle, machine.Phase, "止め終えたら Idle");

        // 起動の工程の途中（am start の最中）の停止もアプリを止める（起動してしまっているかもしれない）
        machine.Start("t", Fixtures.PhoneSerial);
        machine.Apply(Prepared());
        machine.Apply(new AndroidPhaseStarted(AndroidPipelinePhase.Launch, 2, 3, "起動", "r"));
        machine.RequestStop(AndroidRunStopReason.User);
        var duringLaunch = machine.Complete(new AndroidPipelineResult { Canceled = true });
        Check.Equal(AndroidRunOutcome.StoppedByUser, duringLaunch.Outcome, "起動の途中の停止");
        Check.True(duringLaunch.NeedsStopApp, "アプリも止める");
        machine.FinishStopApp();

        // アプリ ID が分からない（準備の前に止めた）ならアプリは止めない
        machine.Start("t", Fixtures.PhoneSerial);
        machine.Apply(new AndroidPhaseStarted(AndroidPipelinePhase.Launch, 2, 3, "起動", "r"));
        machine.RequestStop(AndroidRunStopReason.User);
        Check.True(!machine.Complete(new AndroidPipelineResult { Canceled = true }).NeedsStopApp, "アプリ ID が無ければ止めない");
        Check.Equal(AndroidRunPhase.Idle, machine.Phase, "Idle へ");
    }

    /// <summary>アプリの終了。</summary>
    private static void AppExitOnlyWhileRunning()
    {
        var machine = new AndroidRunStateMachine();
        machine.Start("t", Fixtures.PhoneSerial);
        Check.True(!machine.RequestStop(AndroidRunStopReason.AppExited), "ビルド中は受け付けない（起動前はアプリが無い）");
        machine.Apply(Prepared());
        machine.Apply(LaunchSucceeded());
        Check.True(machine.RequestStop(AndroidRunStopReason.AppExited), "実行中は受け付ける");
        var completion = machine.Complete(new AndroidPipelineResult());
        Check.Equal(AndroidRunOutcome.AppExited, completion.Outcome, "アプリの終了");
        Check.True(!completion.NeedsStopApp, "既に終わっているので止めない");
        Check.Equal(AndroidRunPhase.Idle, machine.Phase, "Idle へ");
        Check.True(!machine.RequestStop(AndroidRunStopReason.None), "理由なしの停止は受け付けない");
    }

    /// <summary>止めていない終わり方。</summary>
    private static void NaturalEndings()
    {
        AndroidRunOutcome End(AndroidPipelineResult result)
        {
            var machine = new AndroidRunStateMachine();
            machine.Start("t", Fixtures.PhoneSerial);
            machine.Apply(Prepared());
            machine.Apply(LaunchSucceeded());
            var completion = machine.Complete(result);
            Check.Equal(AndroidRunPhase.Idle, machine.Phase, "Idle へ");
            Check.True(!completion.NeedsStopApp, "アプリは止めない");
            return completion.Outcome;
        }

        Check.Equal(AndroidRunOutcome.LogcatEnded, End(new AndroidPipelineResult()), "成功で戻った＝logcat が自分で終わった");
        Check.Equal(AndroidRunOutcome.Failed,
            End(new AndroidPipelineResult { FailureKind = AndroidFailureKind.DeviceOperation, FailureMessage = "x" }), "失敗");
        Check.Equal(AndroidRunOutcome.Canceled, End(new AndroidPipelineResult { Canceled = true }), "理由の無い中断");

        var shutdown = new AndroidRunStateMachine();
        shutdown.Start("t", Fixtures.PhoneSerial);
        shutdown.Apply(Prepared());
        shutdown.Apply(LaunchSucceeded());
        Check.True(shutdown.RequestStop(AndroidRunStopReason.Shutdown), "エディタを閉じる");
        var closing = shutdown.Complete(new AndroidPipelineResult());
        Check.Equal(AndroidRunOutcome.Canceled, closing.Outcome, "閉じたときは中断");
        Check.True(!closing.NeedsStopApp, "閉じるときはアプリを止めない（adb で待たせない）");
    }

    /// <summary>遅れて届いたイベント。</summary>
    private static void IgnoresLateEvents()
    {
        var machine = new AndroidRunStateMachine();
        Check.True(!machine.Apply(LaunchSucceeded()), "始める前のイベントは無視");
        Check.Equal(AndroidRunPhase.Idle, machine.Phase, "Idle のまま");
        machine.Start("t", Fixtures.PhoneSerial);
        machine.Complete(new AndroidPipelineResult());
        Check.True(!machine.Apply(LaunchSucceeded()), "戻った後のイベントは無視");
        Check.Equal(AndroidRunPhase.Idle, machine.Phase, "Idle のまま");
        machine.FinishStopApp();
        Check.Equal(AndroidRunPhase.Idle, machine.Phase, "Idle での FinishStopApp は何もしない");
    }
}
