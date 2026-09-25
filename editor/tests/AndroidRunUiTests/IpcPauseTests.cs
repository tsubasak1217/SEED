using System.Collections.Concurrent;
using SEEDEditor.Android;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.AndroidRun;
using SEEDEditor.Ipc;
using SEEDEditor.Runtime;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// 段階D-1: Android の実行中の一時停止・再開（端末のアプリとの IPC）。プレイバーの判断（接続あり／なし／つないでいる途中／
/// 一時停止中）、状態機械（Running ⇄ Paused・切断）、段取り（偽の中核と偽の通信路で、送る命令・切断のときのアプリの終了の
/// 検知とつなぎ直し・停止で通信路を閉じる）。端末・adb は使わない。
/// </summary>
public static class IpcPauseTests
{
    /// <summary>テストの時間の決まり（見張り 10 ms・切断の後の確かめ 3 回 × 5 ms）。</summary>
    private static readonly AndroidRunTimings FastTimings = new(TimeSpan.FromMilliseconds(10), 2, TimeSpan.FromSeconds(5))
    {
        DisconnectExitCheckInterval = TimeSpan.FromMilliseconds(5),
    };

    /// <summary>見張りを実質止める時間の決まり（切断の検知が pidof の見張りより先に働くことを確かめる）。</summary>
    private static readonly AndroidRunTimings SlowMonitorTimings = new(TimeSpan.FromMinutes(10), 2, TimeSpan.FromSeconds(5))
    {
        DisconnectExitCheckInterval = TimeSpan.FromMilliseconds(5),
    };

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("プレイバー（D-1）: 接続ありの実行中は PC の PLAY と同じく一時停止の絵柄で押せる（ANDROID PLAY・水色）", RunningConnectedCanPause);
        harness.Add("プレイバー（D-1）: つないでいる途中・つながらない（古い APK）・使わない指定は押せず理由をツールチップに（ANDROID RUN）", RunningWithoutIpcCannotPause);
        harness.Add("プレイバー（D-1）: 一時停止中は PC の PAUSE と同じ絵柄（再生）・橙・Icon.Pause で再開（ANDROID PAUSE）・停止は押せる", PausedCanResume);
        harness.Add("状態機械（D-1）: Running ⇄ Paused は接続ありのときだけ・切断で Paused は Running へ・止めている途中の接続は受け付けない", StateMachineIpcTransitions);
        harness.Add("段取り（D-1）: 起動したらつなぎ、実行バーで PAUSE / RESUME を送る（Output の行）・停止で通信路を黙って閉じる", ControllerPausesAndResumes);
        harness.Add("段取り（D-1）: つながらなければ理由を出して実行は続ける・ポート 0 の指定ならつながない", ControllerWithoutIpc);
        harness.Add("段取り（D-1）: 切断でアプリが終わっていれば pidof の見張りより先に Idle へ（一時停止中でも）", DisconnectDetectsAppExit);
        harness.Add("段取り（D-1）: 切断でもアプリが動いていれば一時停止を解いた扱いにしてつなぎ直す", DisconnectReconnectsWhileAppAlive);
        harness.Add("段取り（D-1）: PAUSE を送れなければ実行中へ戻す", PauseSendFailureRevertsToRunning);
        harness.Add("段取り（D-1）: 接続トークンは実行ごとに作り直す（前の実行のトークンを使い回さない）", TokenIsFreshPerRun);
    }

    /// <summary>実行ごとに違うトークン。</summary>
    private static void TokenIsFreshPerRun()
    {
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            ConnectIpc = (_, _) => Task.FromResult<IAndroidIpcLink>(new FakeIpcLink()),
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var tokens = new List<string>();
        for (var run = 0; run < 2; run++)
        {
            controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneTarget(), null, null), "Pixel_6a（実機）");
            Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つながる");
            Fixtures.Await(controller.StopAsync(), "停止");
            tokens.Add(backend.IpcConnects.Last().Token);
        }
        Check.True(tokens.All(AndroidIpcToken.IsValid), "トークンの書式");
        Check.True(tokens[0] != tokens[1], "実行ごとに違うトークン");
        Check.Equal(string.Join(",", tokens), string.Join(",", backend.Requests.Select(request => request.IpcToken)), "指定のトークンでつなぐ");
    }

    /// <summary>材料を作る。</summary>
    private static PlayBarInput Input(AndroidRunSnapshot android) => new(EditorState.Edit, Fixtures.PhoneTarget(), android);

    /// <summary>実行中の写し。</summary>
    private static AndroidRunSnapshot Running(AndroidIpcStatus ipc, string? note = null) =>
        new() { Phase = AndroidRunPhase.Running, TargetText = "Pixel_6a（実機）", Ipc = ipc, IpcNote = note };

    /// <summary>接続ありの実行中。</summary>
    private static void RunningConnectedCanPause()
    {
        var view = PlayBarPolicy.Compute(Input(Running(AndroidIpcStatus.Connected)));
        Check.Equal(PlayBarAction.PauseAndroid, view.PlayAction, "実行ボタン＝一時停止");
        Check.Equal(PlayGlyph.Pause, view.PlayGlyph, "PC の PLAY と同じ一時停止の絵柄");
        Check.True(view.PlayToolTip.Contains("一時停止"), $"一時停止の説明: {view.PlayToolTip}");
        Check.Equal(StopBarAction.StopAndroid, view.StopAction, "停止＝アプリを止める");
        Check.Equal(PlayBarPolicy.AndroidPlayLabel, view.StateLabel, "ANDROID PLAY");
        Check.Equal("ANDROID PLAY", view.StateLabel, "状態表示の文言");
        Check.Equal(PlayBarTone.Running, view.StateTone, "PC の PLAY と同じ水色");
        Check.Equal("Icon.Platform.Android", view.StateIconKey, "Android のアイコン");
        Check.True(!view.TargetSelectorEnabled, "実行先は変えられない");
        Check.Equal("Pixel_6a（実機） で実行中", view.ProgressText, "進捗");
    }

    /// <summary>接続なしの実行中。</summary>
    private static void RunningWithoutIpcCannotPause()
    {
        var connecting = PlayBarPolicy.Compute(Input(Running(AndroidIpcStatus.Connecting)));
        Check.Equal(PlayBarAction.None, connecting.PlayAction, "つないでいる途中は押せない");
        Check.Equal(PlayGlyph.Pause, connecting.PlayGlyph, "一時停止の絵柄のまま");
        Check.True(connecting.PlayToolTip.Contains("つないでいます"), $"つないでいる旨: {connecting.PlayToolTip}");
        Check.Equal(PlayBarPolicy.AndroidRunningLabel, connecting.StateLabel, "ANDROID RUN");

        var unavailable = PlayBarPolicy.Compute(Input(Running(AndroidIpcStatus.Unavailable, "端末のアプリがポート 52735 で待ち受けていません")));
        Check.Equal(PlayBarAction.None, unavailable.PlayAction, "つながらなければ押せない");
        Check.True(unavailable.PlayToolTip.Contains("一時停止できません") && unavailable.PlayToolTip.Contains("52735"),
            $"理由をツールチップに: {unavailable.PlayToolTip}");
        Check.Equal("ANDROID RUN", unavailable.StateLabel, "状態表示");
        Check.Equal(StopBarAction.StopAndroid, unavailable.StopAction, "停止は押せる");

        var off = PlayBarPolicy.Compute(Input(Running(AndroidIpcStatus.Off, AndroidRunOutputFormatter.IpcDisabledReason)));
        Check.Equal(PlayBarAction.None, off.PlayAction, "使わない指定");
        Check.True(off.PlayToolTip.Contains("ipc_port = 0"), $"使わない理由: {off.PlayToolTip}");
    }

    /// <summary>一時停止中。</summary>
    private static void PausedCanResume()
    {
        var paused = new AndroidRunSnapshot
        {
            Phase = AndroidRunPhase.Paused, TargetText = "Pixel_6a（実機）", Ipc = AndroidIpcStatus.Connected,
        };
        var view = PlayBarPolicy.Compute(Input(paused));
        Check.Equal(PlayBarAction.ResumeAndroid, view.PlayAction, "実行ボタン＝再開");
        Check.Equal(PlayGlyph.Play, view.PlayGlyph, "PC の PAUSE と同じ再生の絵柄");
        Check.True(view.PlayToolTip.Contains("再開"), $"再開の説明: {view.PlayToolTip}");
        Check.Equal("ANDROID PAUSE", view.StateLabel, "状態表示");
        Check.Equal(PlayBarTone.Paused, view.StateTone, "PC の PAUSE と同じ橙");
        Check.Equal("Icon.Pause", view.StateIconKey, "PC の PAUSE と同じアイコン");
        Check.Equal(StopBarAction.StopAndroid, view.StopAction, "一時停止中も止められる");
        Check.True(!view.TargetSelectorEnabled, "実行先は変えられない");
        Check.Equal("Pixel_6a（実機） で一時停止中", view.ProgressText, "進捗");

        // PC の PAUSE の行と同じ見た目（絵柄・色・アイコン）
        var pc = PlayBarPolicy.Compute(new PlayBarInput(EditorState.Pause, RunTargetCatalogBuilder.Pc, AndroidRunSnapshot.Idle));
        Check.Equal(pc.PlayGlyph, view.PlayGlyph, "PC の PAUSE と同じ絵柄");
        Check.Equal(pc.StateTone, view.StateTone, "PC の PAUSE と同じ色");
        Check.Equal(pc.StateIconKey, view.StateIconKey, "PC の PAUSE と同じアイコン");
    }

    /// <summary>起動の工程の始まりと成功を反映する（中核と同じ順）。</summary>
    private static void EnterRunning(AndroidRunStateMachine machine)
    {
        machine.Apply(new AndroidPhaseStarted(AndroidPipelinePhase.Launch, 2, 3, "起動", "止めてから起動し直す"));
        machine.Apply(new AndroidPhaseFinished(AndroidPipelinePhase.Launch, 2, 3, "起動", AndroidPhaseOutcome.Succeeded, TimeSpan.Zero, "ok"));
    }

    /// <summary>状態機械の IPC の遷移。</summary>
    private static void StateMachineIpcTransitions()
    {
        var machine = new AndroidRunStateMachine();
        machine.Start("Pixel_6a（実機）", Fixtures.PhoneSerial);
        Check.True(!machine.BeginIpcConnect(), "起動前はつながない");
        Check.True(!machine.RequestPause(), "起動前は一時停止できない");
        EnterRunning(machine);
        Check.Equal(AndroidRunPhase.Running, machine.Phase, "Running");
        Check.Equal(AndroidIpcStatus.Off, machine.Ipc, "つなぎ始めるまでは Off");
        Check.True(!machine.RequestPause(), "つながっていなければ一時停止できない");

        Check.True(machine.BeginIpcConnect(), "つなぎ始める");
        Check.Equal(AndroidIpcStatus.Connecting, machine.Snapshot.Ipc, "写しにも Connecting");
        Check.True(machine.IpcConnected(), "つながった");
        Check.True(machine.CanPause && machine.RequestPause(), "一時停止");
        Check.Equal(AndroidRunPhase.Paused, machine.Phase, "Paused");
        Check.True(machine.Snapshot.IsActive && machine.Snapshot.IsAppAlive, "一時停止中もアプリは動いている扱い");
        Check.True(!machine.RequestPause(), "二重に一時停止しない");
        Check.True(machine.RequestResume(), "再開");
        Check.Equal(AndroidRunPhase.Running, machine.Phase, "Running");
        Check.True(!machine.RequestResume(), "実行中に再開は無い");

        machine.RequestPause();
        Check.True(machine.IpcLost(), "切断");
        Check.Equal(AndroidRunPhase.Running, machine.Phase, "端末は切断で一時停止を解くので Running");
        Check.Equal(AndroidIpcStatus.Connecting, machine.Ipc, "つなぎ直す");
        Check.True(machine.IpcFailed("時間切れ"), "つなぎ直しに失敗");
        Check.Equal(AndroidIpcStatus.Unavailable, machine.Ipc, "Unavailable");
        Check.Equal("時間切れ", machine.Snapshot.IpcNote, "理由");

        // 一時停止中のアプリの終了・停止ボタン
        machine.BeginIpcConnect();
        machine.IpcConnected();
        machine.RequestPause();
        Check.True(machine.RequestStop(AndroidRunStopReason.User), "一時停止中も停止ボタンで止まる");
        Check.True(!machine.IpcConnected(), "止めている途中に届いた接続は受け付けない");
        var completion = machine.Complete(new AndroidPipelineResult());
        Check.Equal(AndroidRunOutcome.StoppedByUser, completion.Outcome, "停止");
        Check.Equal(AndroidIpcStatus.Off, machine.Ipc, "終わったら Off");

        var exiting = new AndroidRunStateMachine();
        exiting.Start("x", Fixtures.PhoneSerial);
        EnterRunning(exiting);
        exiting.BeginIpcConnect();
        exiting.IpcConnected();
        exiting.RequestPause();
        Check.True(exiting.RequestStop(AndroidRunStopReason.AppExited), "一時停止中のアプリの終了も受け付ける");
    }

    /// <summary>一時停止と再開。</summary>
    private static void ControllerPausesAndResumes()
    {
        var link = new FakeIpcLink();
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            ConnectIpc = (_, _) => Task.FromResult<IAndroidIpcLink>(link),
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneTarget(), null, null), "Pixel_6a（実機）");
        Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つながる");
        var connect = backend.IpcConnects.Single();
        Check.Equal(Fixtures.PhoneSerial, connect.Serial, "準備で決まった端末へつなぐ");
        Check.Equal(AndroidIpcSettings.DefaultDevicePort, connect.DevicePort, "既定のポート（起動の工程が渡すのと同じ）");
        // 接続トークン: 実行ごとに作って中核への指定に入れ（起動の工程が端末へ渡す）、同じ値でつなぐ
        var passedToken = backend.Requests.Single().IpcToken;
        Check.True(AndroidIpcToken.IsValid(passedToken), $"実行ごとの接続トークンを指定に入れる: {passedToken}");
        Check.Equal(passedToken, connect.Token, "端末へ渡したのと同じトークンでつなぐ");

        Check.True(controller.TryPause(), "一時停止");
        Check.Equal(AndroidRunPhase.Paused, controller.Snapshot.Phase, "Paused");
        Check.True(!controller.TryPause(), "一時停止中は二度送らない");
        Check.True(controller.TryResume(), "再開");
        Check.Equal(AndroidRunPhase.Running, controller.Snapshot.Phase, "Running");
        Check.Equal("PAUSE,RESUME", string.Join(",", link.Sent), "PC の Play と同じ命令");

        controller.TryPause();
        Fixtures.Await(controller.StopAsync(), "一時停止中の停止");
        Check.Equal(AndroidRunPhase.Idle, controller.Snapshot.Phase, "Idle");
        Check.Equal("False", string.Join(",", link.Closes), "停止では通信路を黙って閉じる（DETACH しない＝端末は一時停止を解く）");
        Check.Equal(1, backend.StopCalls.Count, "アプリも止める");

        var text = Texts(lines);
        Check.True(text.Contains("のアプリとつながりました") && text.Contains("端末のポート 52735"), $"つながった行:\n{text}");
        Check.True(text.Contains("一時停止しました") && text.Contains("再開しました"), "一時停止・再開の行");
        Check.True(!text.Contains("通信路が切れました"), "自分で閉じた切断は知らせない");
    }

    /// <summary>つながらない・使わない。</summary>
    private static void ControllerWithoutIpc()
    {
        // つながらない（既定の偽の中核は「待ち受けていません」）
        var backend = new FakeBackend { Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token) };
        using (var controller = new AndroidRunController(backend, FastTimings))
        {
            var lines = Collect(controller);
            controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneTarget(), null, null), "Pixel_6a（実機）");
            Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Unavailable, "つながらない");
            Check.Equal(AndroidRunPhase.Running, controller.Snapshot.Phase, "実行は続ける");
            Check.True(controller.Snapshot.IpcNote!.Contains("待ち受けていません"), "理由を写しへ");
            Check.True(!controller.TryPause(), "一時停止できない");
            Check.True(Texts(lines).Contains("一時停止は使えません（実行は続けます）"), "Output に理由");
            Fixtures.Await(controller.StopAsync(), "停止");
        }

        // ポート 0 の指定: つながない
        var offBackend = new FakeBackend { Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token) };
        using (var controller = new AndroidRunController(offBackend, FastTimings))
        {
            var request = AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneTarget(), null, null, AndroidIpcSettings.DisabledPort);
            Check.Equal<int?>(0, request.IpcPort, "エディタの設定 ipc_port = 0 を中核へ渡す");
            controller.TryStart(request, "Pixel_6a（実機）");
            Fixtures.WaitUntil(() => controller.Snapshot.Phase == AndroidRunPhase.Running && controller.Snapshot.IpcNote is not null, "Running");
            Check.Equal(AndroidIpcStatus.Off, controller.Snapshot.Ipc, "Off");
            Check.Equal(0, offBackend.IpcConnects.Count, "つながない");
            Fixtures.Await(controller.StopAsync(), "停止");
        }
    }

    /// <summary>切断でアプリの終了に気付く。</summary>
    private static void DisconnectDetectsAppExit()
    {
        var link = new FakeIpcLink();
        var appAlive = true;
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            ConnectIpc = (_, _) => Task.FromResult<IAndroidIpcLink>(link),
            Running = _ => Volatile.Read(ref appAlive),
        };
        // pidof の見張りは 10 分おき（実質止めている）: 切断の検知だけで Idle へ戻ることを確かめる
        using var controller = new AndroidRunController(backend, SlowMonitorTimings);
        var lines = Collect(controller);
        controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneTarget(), null, null), "Pixel_6a（実機）");
        Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つながる");
        controller.TryPause();

        Volatile.Write(ref appAlive, false);
        link.Disconnect();
        Fixtures.Await(controller.Completion, "切断からのアプリの終了で Idle");
        Check.Equal(AndroidRunPhase.Idle, controller.Snapshot.Phase, "Idle");
        Check.Equal(0, backend.StopCalls.Count, "既に終わったアプリは止めない");
        var text = Texts(lines);
        Check.True(text.Contains(AndroidRunOutputFormatter.AppExitedText), $"アプリの終了の行:\n{text}");
    }

    /// <summary>切断してもアプリが動いていればつなぎ直す。</summary>
    private static void DisconnectReconnectsWhileAppAlive()
    {
        var first = new FakeIpcLink();
        var second = new FakeIpcLink();
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            ConnectIpc = (count, _) => Task.FromResult<IAndroidIpcLink>(count == 1 ? first : second),
            Running = _ => true,
        };
        using var controller = new AndroidRunController(backend, SlowMonitorTimings);
        var lines = Collect(controller);
        controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneTarget(), null, null), "Pixel_6a（実機）");
        Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つながる");
        controller.TryPause();
        Check.Equal(AndroidRunPhase.Paused, controller.Snapshot.Phase, "Paused");

        first.Disconnect();
        Fixtures.WaitUntil(() => backend.IpcConnects.Count == 2 && controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つなぎ直す");
        Check.Equal(AndroidRunPhase.Running, controller.Snapshot.Phase, "端末は切断で一時停止を解いたので Running");
        Check.True(controller.TryPause(), "つなぎ直した通信路で一時停止できる");
        Check.Equal("PAUSE", string.Join(",", second.Sent), "新しい通信路へ送る");
        Check.True(Texts(lines).Contains("端末のゲームは一時停止を解いて続いています"), "切断で再開した旨");
        Fixtures.Await(controller.StopAsync(), "停止");
        Check.Equal("False", string.Join(",", second.Closes), "停止で閉じる");
    }

    /// <summary>PAUSE を送れない。</summary>
    private static void PauseSendFailureRevertsToRunning()
    {
        var link = new FakeIpcLink { FailSend = true };
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            ConnectIpc = (_, _) => Task.FromResult<IAndroidIpcLink>(link),
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var lines = Collect(controller);
        controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneTarget(), null, null), "Pixel_6a（実機）");
        Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つながる");
        Check.True(!controller.TryPause(), "送れなければ一時停止にしない");
        Check.Equal(AndroidRunPhase.Running, controller.Snapshot.Phase, "Running へ戻す");
        Check.True(Texts(lines).Contains("一時停止を送れませんでした"), "Output に理由");
        Fixtures.Await(controller.StopAsync(), "停止");
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
}
