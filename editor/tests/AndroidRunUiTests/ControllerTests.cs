using System.Collections.Concurrent;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.AndroidRun;
using SEEDEditor.Logging;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// Android の実行の段取り（AndroidRunController）。偽の中核で、停止ボタン・アプリの終了（pidof）・失敗・エディタを閉じる
/// ときに、状態・アプリの停止・Output パネルの行がどうなるかを確かめる（端末・adb は使わない）。
/// </summary>
public static class ControllerTests
{
    /// <summary>テストの見張りの間隔（短くして素早く確かめる）。</summary>
    private static readonly AndroidRunTimings FastTimings = new(TimeSpan.FromMilliseconds(10), 2, TimeSpan.FromSeconds(5));

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("段取り: 実行 → 起動 → 停止ボタンで、アプリを新しい合図で止めて Idle（行: 開始・変更なし・logcat・停止）", RunThenStop);
        harness.Add("段取り: ビルド中の停止は子プロセスの終了（中核が戻る）を待って Idle・アプリには触らない", StopDuringBuild);
        harness.Add("段取り: アプリ側の終了（pidof が続けて空）で自動的に Idle", AppExitDetected);
        harness.Add("段取り: pidof の一時的な取りこぼし・adb の失敗では止めない", TransientMissesDoNotStop);
        harness.Add("段取り: 失敗は Idle へ戻し、種類と対処の行を出す（アプリには触らない）", FailureReturnsToIdle);
        harness.Add("段取り: logcat が自分で終わったら Idle（端末が外れた等）", LogcatEndsByItself);
        harness.Add("段取り: アプリを止められなくても理由を出して Idle", StopAppFailureIsReported);
        harness.Add("段取り: 動いている途中は二重に始めない・エディタを閉じたら中断して以後は始めない", NoDoubleStartAndDispose);
    }

    /// <summary>行を集める。</summary>
    private static ConcurrentQueue<AndroidRunOutputLine> Collect(AndroidRunController controller)
    {
        var lines = new ConcurrentQueue<AndroidRunOutputLine>();
        controller.OutputWritten += lines.Enqueue;
        return lines;
    }

    /// <summary>行の本文をまとめる（確認用）。</summary>
    private static string Texts(IEnumerable<AndroidRunOutputLine> lines) => string.Join("\n", lines.Select(line => line.Text));

    /// <summary>実行 → 停止。</summary>
    private static void RunThenStop()
    {
        var backend = new FakeBackend { Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token) };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        var stateChanges = 0;
        controller.StateChanged += () => Interlocked.Increment(ref stateChanges);

        var request = AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneSerial);
        Check.True(controller.TryStart(request, "Pixel_6a（実機）"), "始められる");
        Fixtures.WaitUntil(() => controller.Snapshot.Phase == AndroidRunPhase.Running, "Running");
        var running = controller.Snapshot;
        Check.Equal(Fixtures.ApplicationId, running.ApplicationId, "アプリ ID は準備で分かる");
        Check.Equal(Fixtures.PhoneSerial, running.Serial, "端末");
        Check.True(backend.Requests.Single().Goal == AndroidRunGoal.Run, "中核へ Run を渡す");

        var stop = controller.StopAsync();
        Fixtures.Await(stop, "停止");
        Check.Equal(AndroidRunPhase.Idle, controller.Snapshot.Phase, "停止の後は Idle");
        var call = backend.StopCalls.Single();
        Check.Equal(Fixtures.PhoneSerial, call.Serial, "止める端末");
        Check.Equal(Fixtures.ApplicationId, call.ApplicationId, "止めるアプリ");
        Check.True(!call.TokenCanceled, "アプリの停止には実行の合図とは別の（取り消されていない）合図を渡す");
        Check.True(stateChanges > 0, "状態の変化を知らせる");

        var text = Texts(lines);
        Check.True(text.Contains("[Android] 実行を始めます: Pixel_6a（実機）"), $"開始の行:\n{text}");
        Check.True(text.Contains("libSEED.so のビルド（cargo ndk） — 変更なし"), "飛ばした工程は「変更なし」の 1 行");
        Check.True(text.Contains("[logcat] I/DOTNET: [Script] hello"), "logcat の行");
        Check.True(text.Contains("停止しました（端末のアプリを止めました）"), "停止の行");
        var logcatLine = lines.First(line => line.Text.StartsWith(AndroidRunOutputFormatter.LogcatPrefix, StringComparison.Ordinal));
        Check.Equal(OutputSource.Game, logcatLine.Style.Source, "スクリプトのログはゲーム");
    }

    /// <summary>ビルド中の停止。</summary>
    private static void StopDuringBuild()
    {
        var childExited = false;
        var backend = new FakeBackend
        {
            Run = async (_, progress, token) =>
            {
                PipelineScript.Prepare(progress);
                progress.Report(new AndroidPhaseStarted(AndroidPipelinePhase.Gradle, 2, 3, "APK の作成（Gradle）", "入力が変わった"));
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    // 子プロセス（Gradle）とその子孫の終了を待つ間（中核の ChildProcessRunner）
                    await Task.Delay(50);
                    childExited = true;
                }
                progress.Report(new AndroidPhaseFinished(AndroidPipelinePhase.Gradle, 2, 3, "APK の作成（Gradle）",
                    AndroidPhaseOutcome.Canceled, TimeSpan.FromSeconds(3), "中断しました"));
                return new AndroidPipelineResult { Canceled = true, Steps = PipelineScript.Steps(AndroidPhaseOutcome.Canceled) };
            },
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneSerial), "Pixel_6a（実機）");
        Fixtures.WaitUntil(() => controller.Snapshot.StepTitle == "APK の作成（Gradle）", "Gradle の工程");
        Check.Equal(AndroidRunPhase.Building, controller.Snapshot.Phase, "Building");

        var stop = controller.StopAsync();
        Check.Equal(AndroidRunPhase.Stopping, controller.Snapshot.Phase, "押した直後は Stopping");
        Fixtures.Await(stop, "ビルドの中止");
        Check.True(childExited, "中核が戻る（子プロセスが終わる）まで Idle にしない");
        Check.Equal(AndroidRunPhase.Idle, controller.Snapshot.Phase, "Idle");
        Check.Equal(0, backend.StopCalls.Count, "起動前なのでアプリには触らない");
        var text = Texts(lines);
        Check.True(text.Contains("停止しています"), "止め始めた行");
        Check.True(text.Contains("中断しました（3.0 秒）"), "工程の中断の行");
        Check.True(text.Contains("ビルドを中止しました"), $"中止の行:\n{text}");
    }

    /// <summary>アプリ側の終了。</summary>
    private static void AppExitDetected()
    {
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            Running = _ => false,
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        Check.True(controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneSerial), "Pixel_6a（実機）"), "始める");
        // TryStart が戻った時点で Completion はこの実行の完了を表す
        Fixtures.Await(controller.Completion, "アプリの終了による Idle");
        Check.Equal(AndroidRunPhase.Idle, controller.Snapshot.Phase, "Idle");
        Check.True(backend.RunningQueries >= FastTimings.AppExitConfirmations, "続けて見つからないことを確かめてから止める");
        Check.Equal(0, backend.StopCalls.Count, "既に終わったアプリは止めない");
        var text = Texts(lines);
        Check.True(text.Contains("アプリのプロセスが見つからなくなりました"), "見つからない旨");
        Check.True(text.Contains("端末でアプリが終わったので実行を終えました"), $"終わり方の行:\n{text}");
    }

    /// <summary>一時的な取りこぼし。</summary>
    private static void TransientMissesDoNotStop()
    {
        // 1 回目は空、2 回目は adb の失敗（数えない）、以後は動いている
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            Running = count => count switch { 1 => false, 2 => null, _ => true },
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneSerial), "t");
        Fixtures.WaitUntil(() => backend.RunningQueries >= 6, "何度か確かめる");
        Check.Equal(AndroidRunPhase.Running, controller.Snapshot.Phase, "止めない");
        Fixtures.Await(controller.StopAsync(), "停止");
        Check.Equal(1, backend.StopCalls.Count, "停止ボタンではアプリを止める");
    }

    /// <summary>失敗。</summary>
    private static void FailureReturnsToIdle()
    {
        var backend = new FakeBackend
        {
            Run = (_, progress, _) =>
            {
                PipelineScript.Prepare(progress);
                progress.Report(new AndroidPhaseStarted(AndroidPipelinePhase.Gradle, 2, 3, "APK の作成（Gradle）", "入力が変わった"));
                progress.Report(new AndroidLogLine(AndroidPipelinePhase.Gradle, AndroidLogLevel.ProcessError, "FAILURE: Build failed with an exception."));
                progress.Report(new AndroidPhaseFinished(AndroidPipelinePhase.Gradle, 2, 3, "APK の作成（Gradle）", AndroidPhaseOutcome.Failed,
                    TimeSpan.FromSeconds(12.3), "gradlew assembleDebug が失敗しました"));
                progress.Report(new AndroidPipelineError(AndroidPipelinePhase.Gradle, AndroidFailureKind.Build, "gradlew assembleDebug が失敗しました"));
                return Task.FromResult(new AndroidPipelineResult
                {
                    FailureKind = AndroidFailureKind.Build, FailureMessage = "gradlew assembleDebug が失敗しました",
                    Steps = PipelineScript.Steps(AndroidPhaseOutcome.Failed),
                });
            },
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        Check.True(controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneSerial), "t"), "始める");
        Fixtures.Await(controller.Completion, "失敗");
        Check.Equal(AndroidRunPhase.Idle, controller.Snapshot.Phase, "Idle へ戻る");
        Check.Equal(0, backend.StopCalls.Count, "アプリには触らない");
        var list = lines.ToList();
        var failedStep = list.Single(line => line.Text.Contains("失敗: gradlew assembleDebug が失敗しました（12.3 秒）"));
        Check.Equal(OutputTone.Error, failedStep.Style.Tone, "工程の失敗は赤");
        var process = list.Single(line => line.Text.Contains("FAILURE: Build failed"));
        Check.Equal(OutputTone.Build, process.Style.Tone, "子プロセスの出力はビルドの色（error の語を含まない）");
        var kind = list.Single(line => line.Text.Contains("エラー（ビルド）"));
        Check.Equal(OutputTone.Error, kind.Style.Tone, "失敗の種類の行は赤");
        var outcome = list.Single(line => line.Text.Contains("実行できませんでした（ビルド）"));
        Check.True(outcome.Text.Contains("上のビルドの出力を確認"), "何を確かめればよいか");
    }

    /// <summary>logcat が自分で終わった。</summary>
    private static void LogcatEndsByItself()
    {
        var backend = new FakeBackend
        {
            Run = (_, progress, _) =>
            {
                PipelineScript.Prepare(progress);
                PipelineScript.Launch(progress);
                progress.Report(new AndroidLogLine(AndroidPipelinePhase.Logcat, AndroidLogLevel.Warning, "adb logcat が終了コード 1 で終わりました（端末が外れた等）。"));
                return Task.FromResult(new AndroidPipelineResult { Steps = PipelineScript.Steps(AndroidPhaseOutcome.Succeeded) });
            },
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        Check.True(controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneSerial), "t"), "始める");
        Fixtures.Await(controller.Completion, "logcat の終わり");
        Check.Equal(AndroidRunPhase.Idle, controller.Snapshot.Phase, "Idle");
        var text = Texts(lines);
        Check.True(text.Contains("警告: adb logcat が終了コード 1"), "中核の警告の行");
        Check.True(text.Contains("logcat が終わりました"), $"終わり方の行:\n{text}");
        Check.True(text.Contains("工程: 2 を行い、1 を飛ばしました"), "工程の数の行");
    }

    /// <summary>アプリを止められない。</summary>
    private static void StopAppFailureIsReported()
    {
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            StopFailure = new AndroidPipelineException(AndroidFailureKind.Device, "指定した端末 2B011JEGR02535 が adb につながっていません。"),
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneSerial), "t");
        Fixtures.WaitUntil(() => controller.Snapshot.Phase == AndroidRunPhase.Running, "Running");
        Fixtures.Await(controller.StopAsync(), "停止");
        Check.Equal(AndroidRunPhase.Idle, controller.Snapshot.Phase, "止められなくても Idle へ戻る");
        var failure = lines.Single(line => line.Text.Contains("端末のアプリを止められませんでした"));
        Check.True(failure.Text.Contains("つながっていません"), "理由");
        Check.Equal(OutputTone.Error, failure.Style.Tone, "赤");
    }

    /// <summary>二重に始めない・閉じたら中断。</summary>
    private static void NoDoubleStartAndDispose()
    {
        var backend = new FakeBackend { Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token) };
        var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        var request = AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneSerial);
        Check.True(controller.TryStart(request, "t"), "1 回目は始める");
        Check.True(!controller.TryStart(request, "t"), "動いている途中は始めない");
        Fixtures.WaitUntil(() => controller.Snapshot.Phase == AndroidRunPhase.Running, "Running");

        var completion = controller.Completion;
        controller.Dispose();
        Fixtures.Await(completion, "閉じたときの中断");
        Check.Equal(AndroidRunPhase.Idle, controller.Snapshot.Phase, "Idle");
        Check.Equal(0, backend.StopCalls.Count, "閉じるときはアプリを止めない");
        Check.True(Texts(lines).Contains("中断しました。"), "中断の行");
        Check.True(!controller.TryStart(request, "t"), "閉じた後は始めない");
        Check.Equal(1, backend.Requests.Count, "中核を呼んだのは 1 回だけ");
    }
}
