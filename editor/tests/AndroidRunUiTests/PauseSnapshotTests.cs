using System.Collections.Concurrent;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.AndroidRun;
using SEEDEditor.Logging;
using SEEDEditor.SceneSnapshot;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// 一時停止中の端末のシーンの写し（docs/android.md §20.17）: 状態機械（取り出しの回数と、Paused を出たら捨てる）・
/// 段取り（PAUSE の後に取り出す・取り出せないときは一時停止のまま・再開で取り消す）・ビューポート（取り出し中／読み込み中の案内、
/// 写しを出している間はランタイムを見せてバナー）・Output の行。端末・adb は使わない（偽の中核と偽の通信路）。
/// </summary>
public static class PauseSnapshotTests
{
    /// <summary>テストの時間の決まり（見張り 10 ms）。</summary>
    private static readonly AndroidRunTimings FastTimings = new(TimeSpan.FromMilliseconds(10), 2, TimeSpan.FromSeconds(5))
    {
        DisconnectExitCheckInterval = TimeSpan.FromMilliseconds(5),
    };

    /// <summary>テストのプロジェクト（偽の中核はファイルを書かない）。</summary>
    private const string ProjectDir = "D:/proj";

    /// <summary>実行先の表示名。</summary>
    private const string PhoneText = "Pixel_6a（実機）";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("写し（§20.17）: 状態機械 — 一時停止中だけ取り出し始め、回数で照合し、再開・切断・停止・実行の終わりで None に戻す", StateMachine);
        harness.Add("写し（§20.17）: 段取り — PAUSE を送れたら同じ通信路で取り出す（書き先は <プロジェクト>/cache/android/snapshot/paused.scene）→ Ready（カメラ付き）", ControllerFetchesAfterPause);
        harness.Add("写し（§20.17）: 段取り — 取り出せなくても一時停止のまま（Failed と理由・警告の 1 行）", ControllerFetchFailureKeepsPaused);
        harness.Add("写し（§20.17）: 段取り — 取り出しの途中で再開したら取り消し、遅れて届いた結果は捨てる", ControllerResumeCancelsFetch);
        harness.Add("写し（§20.17）: ビューポート — 取り出し中・読み込み中は案内、出している間はランタイムを見せてバナー、あきらめた回は一時停止中の案内", ViewportScenes);
        harness.Add("写し（§20.17）: Output の行（取り出し・飛ばしたアクタ・失敗・表示・戻し方ごと・閲覧専用で捨てた命令）", OutputLines);
    }

    /// <summary>取り出せた結果を作る。</summary>
    private static AndroidSceneSnapshotResult Result(string localPath, int skipped = 0, SceneSnapshotCameraPose? camera = null) =>
        new(localPath, new SceneSnapshotReply("/data/user/0/x/cache/seed_ipc_snapshot.scene", 12, skipped, 35.2, camera), 2048, TimeSpan.FromMilliseconds(900));

    /// <summary>状態機械。</summary>
    private static void StateMachine()
    {
        var machine = new AndroidRunStateMachine();
        machine.Start(PhoneText, Fixtures.PhoneSerial);
        Check.True(machine.BeginPauseSnapshot() is null, "Building では取り出さない");
        machine.Apply(new AndroidPhaseStarted(AndroidPipelinePhase.Launch, 2, 3, "起動", "止めてから起動し直す"));
        machine.Apply(new AndroidPhaseFinished(AndroidPipelinePhase.Launch, 2, 3, "起動", AndroidPhaseOutcome.Succeeded, TimeSpan.Zero, "ok"));
        Check.True(machine.BeginIpcConnect() && machine.IpcConnected(), "つながった");
        Check.True(machine.BeginPauseSnapshot() is null, "Running では取り出さない");

        Check.True(machine.RequestPause(), "一時停止");
        var first = machine.BeginPauseSnapshot();
        Check.True(first is not null, "一時停止中は取り出す");
        Check.Equal(AndroidPauseSnapshotStatus.Fetching, machine.Snapshot.PauseSnapshot.Status, "Fetching");
        Check.True(!machine.PauseSnapshotFetched(first!.Value + 1, "x.scene", null), "違う回の結果は受け付けない");
        var pose = new SceneSnapshotCameraPose(1, 2, 3, 10, 20, 0);
        Check.True(machine.PauseSnapshotFetched(first.Value, "x.scene", pose), "同じ回の結果を受け付ける");
        Check.True(machine.Snapshot.PauseSnapshot.IsReady, "Ready");
        Check.Equal(pose, machine.Snapshot.PauseSnapshot.Camera, "メインカメラの姿勢を持つ");
        Check.True(!machine.PauseSnapshotFailed(first.Value, "late"), "取り出し終えた後の失敗は受け付けない");

        Check.True(machine.RequestResume(), "再開");
        Check.Equal(AndroidPauseSnapshotStatus.None, machine.Snapshot.PauseSnapshot.Status, "再開で写しを捨てる");
        Check.True(!machine.PauseSnapshotFetched(first.Value, "x.scene", null), "再開した後に届いた結果は捨てる");

        machine.RequestPause();
        var second = machine.BeginPauseSnapshot();
        Check.True(second > first, "一時停止のたびに回数が進む");
        Check.True(machine.PauseSnapshotFailed(second!.Value, "理由"), "失敗を受け付ける");
        Check.Equal(AndroidPauseSnapshotStatus.Failed, machine.Snapshot.PauseSnapshot.Status, "Failed");
        Check.Equal("理由", machine.Snapshot.PauseSnapshot.Note, "理由");
        Check.Equal(AndroidRunPhase.Paused, machine.Phase, "一時停止は続く");

        Check.True(machine.IpcLost(), "切断");
        Check.Equal(AndroidPauseSnapshotStatus.None, machine.Snapshot.PauseSnapshot.Status, "切断で写しを捨てる（端末は再開している）");

        machine.IpcConnected();
        machine.RequestPause();
        machine.BeginPauseSnapshot();
        Check.True(machine.RequestStop(AndroidRunStopReason.User), "停止");
        Check.Equal(AndroidPauseSnapshotStatus.None, machine.Snapshot.PauseSnapshot.Status, "停止で写しを捨てる");
    }

    /// <summary>一時停止すると取り出して Ready になる。</summary>
    private static void ControllerFetchesAfterPause()
    {
        var link = new FakeIpcLink();
        var pose = new SceneSnapshotCameraPose(0, 5, -10, 20, 0, 0);
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            ConnectIpc = (_, _) => Task.FromResult<IAndroidIpcLink>(link),
            FetchSnapshot = (_, localPath, _) => Task.FromResult(Result(localPath, camera: pose)),
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        controller.TryStart(AndroidEditorRunRequests.ForPlay(ProjectDir, Fixtures.PhoneTarget(), null, null), PhoneText);
        Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つながる");

        Check.True(controller.TryPause(), "一時停止");
        Fixtures.WaitUntil(() => controller.Snapshot.PauseSnapshot.IsReady, "写しを取り出せた");
        var fetch = backend.SnapshotFetches.Single();
        Check.Equal(Fixtures.PhoneSerial, fetch.Serial, "準備で決まった端末");
        Check.Equal(Fixtures.ApplicationId, fetch.ApplicationId, "アプリ ID");
        Check.Equal(
            Path.Combine(Path.GetFullPath(ProjectDir), "cache", "android", "snapshot", "paused.scene"), fetch.LocalPath,
            "プロジェクトの cache/android/snapshot/paused.scene（アセットには書かない）");
        Check.Equal(fetch.LocalPath, controller.Snapshot.PauseSnapshot.LocalPath, "写しのパス");
        Check.Equal(pose, controller.Snapshot.PauseSnapshot.Camera, "メインカメラの姿勢");
        Check.Equal("PAUSE", string.Join(",", link.Sent), "一時停止の命令は PC の Play と同じ（写しの命令は中核が同じ通信路で送る）");

        var text = Texts(lines);
        Check.True(text.Contains("写しとして取り出しています") && text.Contains("写しを取り出しました: アクタ 12"), $"取り出しの行:\n{text}");
        Fixtures.Await(controller.StopAsync(), "停止");
        Check.Equal(AndroidPauseSnapshotStatus.None, controller.Snapshot.PauseSnapshot.Status, "止めたら写しを捨てる");
    }

    /// <summary>取り出せなくても一時停止のまま。</summary>
    private static void ControllerFetchFailureKeepsPaused()
    {
        var link = new FakeIpcLink();
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            ConnectIpc = (_, _) => Task.FromResult<IAndroidIpcLink>(link),
            FetchSnapshot = (_, _, _) => Task.FromException<AndroidSceneSnapshotResult>(
                new AndroidIpcException(AndroidIpcFailureKind.NoReply, "端末のアプリが写しを書き出せませんでした: テスト")),
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        controller.TryStart(AndroidEditorRunRequests.ForPlay(ProjectDir, Fixtures.PhoneTarget(), null, null), PhoneText);
        Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つながる");
        Check.True(controller.TryPause(), "一時停止は成功");
        Fixtures.WaitUntil(() => controller.Snapshot.PauseSnapshot.Status == AndroidPauseSnapshotStatus.Failed, "Failed");
        Check.Equal(AndroidRunPhase.Paused, controller.Snapshot.Phase, "一時停止は続く");
        Check.True(controller.Snapshot.PauseSnapshot.Note!.Contains("書き出せませんでした"), "理由を写しへ");
        var warnings = lines.Where(line => line.Text.Contains("写しを取り出せませんでした")).ToList();
        Check.Equal(1, warnings.Count, "警告は 1 行");
        Check.Equal(OutputTone.Warning, warnings[0].Style.Tone, "黄（警告）");
        Check.True(controller.TryResume(), "再開できる");
        Fixtures.Await(controller.StopAsync(), "停止");
    }

    /// <summary>取り出しの途中で再開すると取り消す。</summary>
    private static void ControllerResumeCancelsFetch()
    {
        var link = new FakeIpcLink();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            ConnectIpc = (_, _) => Task.FromResult<IAndroidIpcLink>(link),
            FetchSnapshot = async (_, localPath, token) =>
            {
                token.Register(() => canceled.TrySetResult());
                // 取り消されても結果を返す（遅れて届いた結果を段取りが捨てることを確かめる）
                await release.Task;
                return Result(localPath);
            },
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        controller.TryStart(AndroidEditorRunRequests.ForPlay(ProjectDir, Fixtures.PhoneTarget(), null, null), PhoneText);
        Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つながる");
        controller.TryPause();
        Fixtures.WaitUntil(() => controller.Snapshot.PauseSnapshot.Status == AndroidPauseSnapshotStatus.Fetching, "取り出し中");
        Check.True(controller.TryResume(), "再開");
        Fixtures.Await(canceled.Task, "取り出しの取り消し");
        release.TrySetResult();
        Thread.Sleep(50);
        Check.Equal(AndroidRunPhase.Running, controller.Snapshot.Phase, "Running");
        Check.Equal(AndroidPauseSnapshotStatus.None, controller.Snapshot.PauseSnapshot.Status, "遅れて届いた結果は捨てる");
        Fixtures.Await(controller.StopAsync(), "停止");
    }

    /// <summary>ビューポートの場面。</summary>
    private static void ViewportScenes()
    {
        AndroidRunSnapshot Paused(AndroidPauseSnapshotStatus status, int generation = 1) => new()
        {
            Phase = AndroidRunPhase.Paused,
            TargetText = PhoneText,
            PauseSnapshot = new AndroidPauseSnapshotState(
                status, generation, status == AndroidPauseSnapshotStatus.Ready ? "C:/p/paused.scene" : null, null, null),
        };

        var fetching = AndroidViewportPolicy.Compute(Paused(AndroidPauseSnapshotStatus.Fetching));
        Check.True(fetching.HidesRuntime, "取り出し中はランタイムを隠す");
        Check.Equal(string.Format(AndroidViewportPolicy.SnapshotFetchingNoticeFormat, PhoneText), fetching.NoticeText, "取り出し中の案内");

        var ready = AndroidViewportPolicy.Compute(Paused(AndroidPauseSnapshotStatus.Ready));
        Check.Equal(string.Format(AndroidViewportPolicy.SnapshotLoadingNoticeFormat, PhoneText), ready.NoticeText, "読み込みを始める前も読み込み中");
        var beginning = AndroidViewportPolicy.Compute(Paused(AndroidPauseSnapshotStatus.Ready), SceneSnapshotViewPhase.Beginning);
        Check.Equal(ready.NoticeText, beginning.NoticeText, "読み込み中の案内");

        var showing = AndroidViewportPolicy.Compute(Paused(AndroidPauseSnapshotStatus.Ready), SceneSnapshotViewPhase.Showing);
        Check.Equal(ViewportContent.AndroidSnapshot, showing.Content, "写しを見せる");
        Check.True(!showing.HidesRuntime, "ランタイム（写し）を見せる");
        Check.True(showing.IsAndroidOwned, "Android の実行がビューポートを使っている");
        Check.True(showing.NoticeText is null, "案内は出さない");
        Check.Equal(string.Format(SEEDEditor.Scene.EditorReadOnlyPolicy.SnapshotBannerFormat, PhoneText), showing.BannerText, "閲覧専用のバナー");

        var givenUp = AndroidViewportPolicy.Compute(Paused(AndroidPauseSnapshotStatus.Ready, 3), SceneSnapshotViewPhase.Idle, givenUpGeneration: 3);
        Check.Equal(string.Format(AndroidViewportPolicy.PausedNoticeFormat, PhoneText), givenUp.NoticeText, "あきらめた回は一時停止中の案内");
        var failed = AndroidViewportPolicy.Compute(Paused(AndroidPauseSnapshotStatus.Failed));
        Check.Equal(givenUp.NoticeText, failed.NoticeText, "取り出せなかったときも一時停止中の案内");

        // 再開した直後（戻している途中）は実行中の案内（写しのままのランタイムは見せない）
        var ending = AndroidViewportPolicy.Compute(
            new AndroidRunSnapshot { Phase = AndroidRunPhase.Running, TargetText = PhoneText }, SceneSnapshotViewPhase.Ending);
        Check.True(ending.HidesRuntime, "戻している途中はランタイムを隠す");
        Check.Equal(string.Format(AndroidViewportPolicy.RunningNoticeFormat, PhoneText), ending.NoticeText, "実行中の案内");
        Check.Equal(ViewportContent.Runtime, AndroidViewportPolicy.Compute(AndroidRunSnapshot.Idle, SceneSnapshotViewPhase.Showing).Content,
            "Idle なら PC の表示へ戻す");
    }

    /// <summary>Output の行。</summary>
    private static void OutputLines()
    {
        var fetched = AndroidPauseSnapshotOutputFormatter.Fetched(Result("C:/p/paused.scene", skipped: 2, camera: new SceneSnapshotCameraPose(1, 2, 3, 4, 5, 6)));
        Check.Equal(2, fetched.Count, "飛ばしたアクタがあれば警告の行を足す");
        Check.True(fetched[0].Text.Contains("アクタ 12（保存できずに飛ばした 2）") && fetched[0].Text.Contains("端末 35 ms"), fetched[0].Text);
        Check.True(fetched[0].Text.Contains("位置 (1.00, 2.00, 3.00)"), "メインカメラの位置");
        Check.Equal(OutputTone.Warning, fetched[1].Style.Tone, "飛ばしたアクタは黄");
        Check.Equal(1, AndroidPauseSnapshotOutputFormatter.Fetched(Result("C:/p/paused.scene")).Count, "飛ばしていなければ 1 行");

        var shown = AndroidPauseSnapshotOutputFormatter.ViewBegun(new SceneSnapshotViewBeginResult(
            new SceneSnapshotViewBeginReply(SceneSnapshotViewBeginOutcome.Ready, "C:/p/paused.scene", 12, 30, null), TimeSpan.FromSeconds(0.6), true));
        Check.True(shown.Text.Contains("写しをシーンパネルに出しました（閲覧専用・アクタ 12・0.6 秒）") && shown.Text.Contains("デバッグカメラ"), shown.Text);
        var notShown = AndroidPauseSnapshotOutputFormatter.ViewBegun(new SceneSnapshotViewBeginResult(
            new SceneSnapshotViewBeginReply(SceneSnapshotViewBeginOutcome.LoadFailed, "C:/p/paused.scene", 0, 0, "壊れている"), TimeSpan.Zero, false));
        Check.Equal(OutputTone.Warning, notShown.Style.Tone, "出せなければ黄");

        SceneSnapshotViewEndResult End(SceneSnapshotRestoreKind kind, string detail) =>
            new(new SceneSnapshotViewEndReply(kind, detail, 5), TimeSpan.FromSeconds(0.4), false);
        Check.True(AndroidPauseSnapshotOutputFormatter.ViewEnded(End(SceneSnapshotRestoreKind.Memory, "C:/p/a.scene"))!.Text.Contains("編集中のシーンへ戻しました（0.4 秒）"), "メモリから");
        Check.Equal(OutputTone.Warning, AndroidPauseSnapshotOutputFormatter.ViewEnded(End(SceneSnapshotRestoreKind.File, "C:/p/a.scene"))!.Style.Tone, "ファイルから戻したら黄");
        Check.Equal(OutputTone.Error, AndroidPauseSnapshotOutputFormatter.ViewEnded(End(SceneSnapshotRestoreKind.Failed, "理由"))!.Style.Tone, "戻せなければ赤");
        Check.True(AndroidPauseSnapshotOutputFormatter.ViewEnded(End(SceneSnapshotRestoreKind.None, ""))is null, "出していなければ行を出さない");
        Check.True(AndroidPauseSnapshotOutputFormatter.Refused("SAVE_SCENE").Text.Contains("閲覧専用（端末の写し）のため反映しませんでした: SAVE_SCENE"), "捨てた命令");
    }

    /// <summary>出した行を集める。</summary>
    private static ConcurrentQueue<AndroidRunOutputLine> Collect(AndroidRunController controller)
    {
        var lines = new ConcurrentQueue<AndroidRunOutputLine>();
        controller.OutputWritten += lines.Enqueue;
        return lines;
    }

    /// <summary>行の本文をまとめる。</summary>
    private static string Texts(IEnumerable<AndroidRunOutputLine> lines) => string.Join("\n", lines.Select(line => line.Text));
}
