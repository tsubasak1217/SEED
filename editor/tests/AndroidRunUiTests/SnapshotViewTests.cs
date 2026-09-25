using System.Collections.Concurrent;
using SEEDEditor.AndroidRun;
using SEEDEditor.Scene;
using SEEDEditor.SceneSnapshot;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// 写しをシーンパネルへ閲覧専用で出す段取り（docs/android.md §20.17）: 応答の読み方（SceneSnapshotReply・閲覧の応答・カメラの姿勢）、
/// 編集用ランタイムへの命令と待ち合わせ（SceneSnapshotViewSession。偽のランタイム）、一時停止に合わせて出す・戻す段取り
/// （AndroidPauseSnapshotViewCoordinator。偽の窓口）、閲覧専用の判断（EditorReadOnlyPolicy）。エディタ・ランタイムは使わない。
/// </summary>
public static class SnapshotViewTests
{
    /// <summary>写しのパス。</summary>
    private const string SnapshotPath = @"C:\p\cache\android\snapshot\paused.scene";

    /// <summary>実行先の表示名。</summary>
    private const string PhoneText = "Pixel_6a（実機）";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("写しの応答（§20.17）: SNAPSHOT_DONE / FAILED を読む（欄の順に依らない・cam=none・書式違いは理由付きの失敗）", ParsesSnapshotReplies);
        harness.Add("写しの応答（§20.17）: メインカメラの姿勢を読み、既存の CAM_TRANSFORM の書式で当てる（有限でない値は捨てる）", CameraPoseRoundTrip);
        harness.Add("写しの応答（§20.17）: 閲覧の応答（READY / FAILED の段階 / ENDED の戻し方）を読む", ParsesViewReplies);
        harness.Add("写しの閲覧（§20.17）: 出す → カメラを CAM_TRANSFORM で合わせる → 戻すと表示の前の未保存フラグを返す", SessionBeginAndEnd);
        harness.Add("写しの閲覧（§20.17）: 未保存の変更が無ければ file_ok を付け、ファイルから戻したら未保存なしにする", SessionFileRestore);
        harness.Add("写しの閲覧（§20.17）: 出せない応答なら出していないまま・応答が無ければ END を送って戻させる・送れなければ出さない", SessionFailures);
        harness.Add("写しの段取り（§20.17）: 次の 1 手の表（Paused で写しがあれば出す・同じ回は 2 度出さない・Paused を出たら戻す）", DecideTable);
        harness.Add("写しの段取り（§20.17）: 一時停止 → 出す（編集の途中を閉じる）→ 再開 → 戻す（未保存フラグを元へ・Output）", CoordinatorPauseResume);
        harness.Add("写しの段取り（§20.17）: 退避できず未保存の変更があれば保存を尋ね、保存したら file_ok でもう 1 度だけ・保存しなければ出さない", CoordinatorStashFailure);
        harness.Add("写しの段取り（§20.17）: 出せる状態でなければあきらめて理由を出す・編集用ランタイムが終わったら出していない状態へ", CoordinatorBlockedAndLost);
        harness.Add("閲覧専用（§20.17）: 写しの表示中は保存・編集・シーンの切り替えを拒否しバナーと印を出す／ロックは保存だけ拒否（従来どおり）", ReadOnlyPolicy);
    }

    // ── 偽物 ───────────────────────────────────────────

    /// <summary>偽の編集用ランタイム（送った命令を記録し、決めた応答を返す）。</summary>
    private sealed class FakeViewRuntime : ISceneSnapshotViewRuntime
    {
        /// <summary>送った命令。</summary>
        public ConcurrentQueue<string> Sent { get; } = new();

        /// <summary>命令に返す行（null なら返さない）。</summary>
        public Func<string, string?> Reply { get; set; } = command => command.StartsWith(SceneSnapshotWire.ViewBeginPrefix, StringComparison.Ordinal)
            ? $"{SceneSnapshotWire.ViewReadyPrefix}{SnapshotPath}|actors=12|ms=30.0"
            : command == SceneSnapshotWire.ViewEnd ? $"{SceneSnapshotWire.ViewEndedPrefix}memory|C:/p/Main.scene|ms=5.0" : null;

        /// <inheritdoc />
        public bool CanSend { get; set; } = true;

        /// <inheritdoc />
        public event Action<string>? MessageReceived;

        /// <inheritdoc />
        public bool Send(string command)
        {
            if (!CanSend) return false;
            Sent.Enqueue(command);
            if (Reply(command) is { } line) MessageReceived?.Invoke(line);
            return true;
        }
    }

    /// <summary>偽のエディタの窓口。</summary>
    private sealed class FakeViewHost : IAndroidSnapshotViewHost
    {
        /// <inheritdoc />
        public bool IsEditorDirty { get; set; }

        /// <inheritdoc />
        public string? SnapshotViewBlocker { get; set; }

        /// <summary>編集の途中を閉じた回数。</summary>
        public int Prepared;

        /// <summary>当てた未保存フラグ。</summary>
        public ConcurrentQueue<bool> AppliedDirty { get; } = new();

        /// <summary>保存を尋ねた理由。</summary>
        public ConcurrentQueue<string> SavePrompts { get; } = new();

        /// <summary>保存を尋ねたときの答え（保存が済んだら true）。</summary>
        public bool SaveAnswer { get; set; }

        /// <summary>写しを出した後の知らせの回数。</summary>
        public int Shown;

        /// <inheritdoc />
        public void PrepareForSnapshotView() => Interlocked.Increment(ref Prepared);

        /// <inheritdoc />
        public void OnSnapshotShown() => Interlocked.Increment(ref Shown);

        /// <inheritdoc />
        public void ApplyDirtyAfterRestore(bool dirty) => AppliedDirty.Enqueue(dirty);

        /// <inheritdoc />
        public Task<bool> SaveBeforeViewAsync(string reason)
        {
            SavePrompts.Enqueue(reason);
            if (SaveAnswer) IsEditorDirty = false;
            return Task.FromResult(SaveAnswer);
        }
    }

    /// <summary>写しを取り出せた一時停止中の写し。</summary>
    private static AndroidRunSnapshot PausedReady(int generation, SceneSnapshotCameraPose? camera = null) => new()
    {
        Phase = AndroidRunPhase.Paused,
        TargetText = PhoneText,
        PauseSnapshot = new AndroidPauseSnapshotState(AndroidPauseSnapshotStatus.Ready, generation, SnapshotPath, camera, null),
    };

    /// <summary>実行中の写し。</summary>
    private static AndroidRunSnapshot Running() => new() { Phase = AndroidRunPhase.Running, TargetText = PhoneText };

    // ── 応答 ───────────────────────────────────────────

    /// <summary>写しの応答。</summary>
    private static void ParsesSnapshotReplies()
    {
        Check.True(SceneSnapshotReply.TryParse(
            "SNAPSHOT_DONE:/data/user/0/x/cache/seed_ipc_snapshot.scene|actors=12|skipped=1|ms=35.2|cam=1,2.5,-3,10,-20,0",
            out var reply, out var failure), $"読める: {failure}");
        Check.Equal("/data/user/0/x/cache/seed_ipc_snapshot.scene", reply!.RuntimePath, "パス");
        Check.Equal(12, reply.Actors, "アクタ数");
        Check.Equal(1, reply.Skipped, "飛ばした数");
        Check.Close(35.2, reply.RuntimeMilliseconds, 1e-9, "端末でのミリ秒");
        Check.Equal(new SceneSnapshotCameraPose(1, 2.5f, -3, 10, -20, 0), reply.Camera, "メインカメラ");

        Check.True(SceneSnapshotReply.TryParse("SNAPSHOT_DONE:/d/s.scene|cam=none|future=1|ms=1.0|skipped=0|actors=3", out var reordered, out _),
            "欄の順・知らない欄に依らない");
        Check.True(reordered!.Camera is null, "cam=none はカメラ無し");
        Check.Equal(3, reordered.Actors, "アクタ数");

        Check.True(!SceneSnapshotReply.TryParse("SNAPSHOT_FAILED:/d/s.scene|シーンが読み込まれていません", out _, out var reason), "失敗");
        Check.Equal("シーンが読み込まれていません", reason, "理由");
        Check.True(!SceneSnapshotReply.TryParse("SNAPSHOT_DONE:/d/s.scene|actors=x", out _, out var malformed), "書式違い");
        Check.True(malformed!.Contains("読めません"), "書式違いの理由");
        Check.Equal("SNAPSHOT_SCENE:/data/user/0/x/cache/seed_ipc_snapshot.scene",
            SceneSnapshotWire.SnapshotScene("/data/user/0/x/cache/seed_ipc_snapshot.scene"), "命令");
        Check.True(SceneSnapshotWire.IsSnapshotReply("SNAPSHOT_FAILED:a|b") && !SceneSnapshotWire.IsSnapshotReply("SCREENSHOT_DONE:a"), "応答の見分け");
    }

    /// <summary>カメラの姿勢。</summary>
    private static void CameraPoseRoundTrip()
    {
        var pose = SceneSnapshotCameraPose.TryParse(" 0, 5 ,-10,20.5,0,0 ");
        Check.Equal(new SceneSnapshotCameraPose(0, 5, -10, 20.5f, 0, 0), pose, "空白を許して読む");
        Check.Equal("CAM_TRANSFORM:0,5,-10,20.5,0,0", pose!.ToCameraTransformCommand(), "MainWindow.SendCameraTransform と同じ書式");
        Check.True(SceneSnapshotCameraPose.TryParse("none") is null, "none");
        Check.True(SceneSnapshotCameraPose.TryParse("1,2,3") is null, "欄の数が違う");
        Check.True(SceneSnapshotCameraPose.TryParse("NaN,0,0,0,0,0") is null, "有限でない値は捨てる");
        Check.True(SceneSnapshotCameraPose.TryParse(null) is null, "無い");
    }

    /// <summary>閲覧の応答。</summary>
    private static void ParsesViewReplies()
    {
        var ready = SceneSnapshotViewBeginReply.Parse($"SNAPSHOT_VIEW_READY:{SnapshotPath}|actors=12|ms=30.5");
        Check.True(ready.IsReady && ready.Actors == 12 && ready.Path == SnapshotPath, "READY");
        var stash = SceneSnapshotViewBeginReply.Parse($"SNAPSHOT_VIEW_FAILED:{SnapshotPath}|stash|直列化できません: a|b");
        Check.Equal(SceneSnapshotViewBeginOutcome.StashFailed, stash.Outcome, "退避できない");
        Check.Equal("直列化できません: a|b", stash.Reason, "理由の | も残す");
        Check.Equal(SceneSnapshotViewBeginOutcome.LoadFailed, SceneSnapshotViewBeginReply.Parse($"SNAPSHOT_VIEW_FAILED:{SnapshotPath}|load|x").Outcome, "読めない");
        Check.Equal(SceneSnapshotViewBeginOutcome.WrongMode, SceneSnapshotViewBeginReply.Parse($"SNAPSHOT_VIEW_FAILED:{SnapshotPath}|mode|x").Outcome, "Edit でない");
        Check.Equal(SceneSnapshotViewBeginOutcome.Malformed, SceneSnapshotViewBeginReply.Parse("SNAPSHOT_VIEW_FAILED:x|?|y").Outcome, "知らない段階");

        var memory = SceneSnapshotViewEndReply.Parse("SNAPSHOT_VIEW_ENDED:memory|C:/p/Main.scene|ms=4.5");
        Check.Equal(SceneSnapshotRestoreKind.Memory, memory.Restore, "メモリから");
        Check.Equal("C:/p/Main.scene", memory.Detail, "戻したシーン");
        Check.Close(4.5, memory.RuntimeMilliseconds, 1e-9, "ミリ秒");
        Check.Equal(SceneSnapshotRestoreKind.None, SceneSnapshotViewEndReply.Parse("SNAPSHOT_VIEW_ENDED:none||ms=0.0").Restore, "出していなかった");
        Check.Equal(SceneSnapshotRestoreKind.Failed, SceneSnapshotViewEndReply.Parse("SNAPSHOT_VIEW_ENDED:weird|x").Restore, "知らない戻し方は失敗");
        Check.Equal("SNAPSHOT_VIEW_BEGIN:C:/a.scene|file_ok", SceneSnapshotWire.ViewBegin("C:/a.scene", allowFileRestore: true), "file_ok");
        Check.Equal("SNAPSHOT_VIEW_BEGIN:C:/a.scene", SceneSnapshotWire.ViewBegin("C:/a.scene", allowFileRestore: false), "file_ok 無し");
    }

    // ── 編集用ランタイムへの段取り ─────────────────────────────

    /// <summary>出して戻す。</summary>
    private static void SessionBeginAndEnd()
    {
        var runtime = new FakeViewRuntime();
        var session = new SceneSnapshotViewSession(runtime);
        var phases = new ConcurrentQueue<SceneSnapshotViewPhase>();
        session.PhaseChanged += () => phases.Enqueue(session.Phase);

        var pose = new SceneSnapshotCameraPose(0, 5, -10, 20, 0, 0);
        var begun = session.BeginAsync(new SceneSnapshotViewRequest(SnapshotPath, pose, EditorDirty: true), CancellationToken.None).Result;
        Check.True(begun.Reply.IsReady && begun.CameraApplied, "出してカメラを合わせた");
        Check.Equal(SceneSnapshotViewPhase.Showing, session.Phase, "Showing");
        Check.Equal(SnapshotPath, session.ShownPath, "出している写し");
        Check.Equal($"SNAPSHOT_VIEW_BEGIN:{SnapshotPath},CAM_TRANSFORM:0,5,-10,20,0,0", string.Join(",", runtime.Sent),
            "未保存の変更があるので file_ok は付けない・カメラは既存の CAM_TRANSFORM");

        var ended = session.EndAsync(CancellationToken.None).Result;
        Check.Equal(SceneSnapshotRestoreKind.Memory, ended.Reply.Restore, "メモリから戻した");
        Check.True(ended.EditorDirty, "表示の前の未保存フラグ（true）を返す");
        Check.Equal(SceneSnapshotViewPhase.Idle, session.Phase, "Idle");
        Check.Equal("Beginning,Showing,Ending,Idle", string.Join(",", phases), "状態の移り変わり");

        var again = session.EndAsync(CancellationToken.None).Result;
        Check.Equal(SceneSnapshotRestoreKind.None, again.Reply.Restore, "出していなければ何もしない");
        Check.Equal(3, runtime.Sent.Count, "END を二重に送らない");
    }

    /// <summary>未保存の変更が無いとき。</summary>
    private static void SessionFileRestore()
    {
        var runtime = new FakeViewRuntime();
        runtime.Reply = command => command.StartsWith(SceneSnapshotWire.ViewBeginPrefix, StringComparison.Ordinal)
            ? $"SNAPSHOT_VIEW_READY:{SnapshotPath}|actors=1|ms=1.0"
            : "SNAPSHOT_VIEW_ENDED:file|C:/p/Main.scene|ms=2.0";
        var session = new SceneSnapshotViewSession(runtime);
        var begun = session.BeginAsync(new SceneSnapshotViewRequest(SnapshotPath, null, EditorDirty: false), CancellationToken.None).Result;
        Check.True(begun.Reply.IsReady && !begun.CameraApplied, "カメラが無ければ合わせない");
        Check.Equal($"SNAPSHOT_VIEW_BEGIN:{SnapshotPath}|file_ok", runtime.Sent.First(), "未保存の変更が無いので file_ok");
        var ended = session.EndAsync(CancellationToken.None).Result;
        Check.Equal(SceneSnapshotRestoreKind.File, ended.Reply.Restore, "ファイルから");
        Check.True(!ended.EditorDirty, "ファイルを読み直したので未保存なし");
        Check.True(!SceneSnapshotViewSession.DecideDirtyAfterRestore(SceneSnapshotRestoreKind.File, true), "表: ファイルなら false");
        Check.True(SceneSnapshotViewSession.DecideDirtyAfterRestore(SceneSnapshotRestoreKind.Failed, true), "表: 戻せなければ元の値");
    }

    /// <summary>出せないとき。</summary>
    private static void SessionFailures()
    {
        // 出せない応答: 出していない状態のまま
        var failing = new FakeViewRuntime { Reply = _ => $"SNAPSHOT_VIEW_FAILED:{SnapshotPath}|load|壊れています" };
        var session = new SceneSnapshotViewSession(failing);
        var result = session.BeginAsync(new SceneSnapshotViewRequest(SnapshotPath, null, false), CancellationToken.None).Result;
        Check.Equal(SceneSnapshotViewBeginOutcome.LoadFailed, result.Reply.Outcome, "読めない");
        Check.Equal(SceneSnapshotViewPhase.Idle, session.Phase, "出していないまま");

        // 応答が無い: END を送って戻させ、出していない状態へ
        var silent = new FakeViewRuntime { Reply = _ => null };
        var timeoutSession = new SceneSnapshotViewSession(silent, TimeSpan.FromMilliseconds(50));
        var timedOut = timeoutSession.BeginAsync(new SceneSnapshotViewRequest(SnapshotPath, null, false), CancellationToken.None).Result;
        Check.True(!timedOut.Reply.IsReady && timedOut.Reply.Reason!.Contains("応答しませんでした"), "時間切れの理由");
        Check.Equal(SceneSnapshotWire.ViewEnd, silent.Sent.Last(), "後から読み込み終えても戻るよう END を送る");
        Check.Equal(SceneSnapshotViewPhase.Idle, timeoutSession.Phase, "出していない状態");

        // 送れない（編集用ランタイムが Edit でない）: 何も送らない
        var offline = new FakeViewRuntime { CanSend = false };
        var offlineSession = new SceneSnapshotViewSession(offline);
        var notSent = offlineSession.BeginAsync(new SceneSnapshotViewRequest(SnapshotPath, null, false), CancellationToken.None).Result;
        Check.Equal(SceneSnapshotViewBeginOutcome.WrongMode, notSent.Reply.Outcome, "送れない");
        Check.True(offline.Sent.IsEmpty, "何も送らない");
    }

    // ── 一時停止に合わせた段取り ───────────────────────────────

    /// <summary>次の 1 手の表。</summary>
    private static void DecideTable()
    {
        Check.Equal(SnapshotViewStep.Begin, AndroidPauseSnapshotViewCoordinator.Decide(PausedReady(1), SceneSnapshotViewPhase.Idle, 0), "出す");
        Check.Equal(SnapshotViewStep.None, AndroidPauseSnapshotViewCoordinator.Decide(PausedReady(1), SceneSnapshotViewPhase.Idle, 1), "同じ回は 2 度出さない");
        Check.Equal(SnapshotViewStep.None, AndroidPauseSnapshotViewCoordinator.Decide(PausedReady(2), SceneSnapshotViewPhase.Showing, 2), "出している");
        Check.Equal(SnapshotViewStep.End, AndroidPauseSnapshotViewCoordinator.Decide(Running(), SceneSnapshotViewPhase.Showing, 2), "再開したら戻す");
        Check.Equal(SnapshotViewStep.End, AndroidPauseSnapshotViewCoordinator.Decide(AndroidRunSnapshot.Idle, SceneSnapshotViewPhase.Showing, 2), "止めたら戻す");
        var fetching = PausedReady(3) with
        {
            PauseSnapshot = new AndroidPauseSnapshotState(AndroidPauseSnapshotStatus.Fetching, 3, null, null, null),
        };
        Check.Equal(SnapshotViewStep.None, AndroidPauseSnapshotViewCoordinator.Decide(fetching, SceneSnapshotViewPhase.Idle, 2), "取り出し中は待つ");
        Check.Equal(SnapshotViewStep.None, AndroidPauseSnapshotViewCoordinator.Decide(Running(), SceneSnapshotViewPhase.Beginning, 2), "出している途中は待つ（終わってから決め直す）");
    }

    /// <summary>一時停止 → 出す → 再開 → 戻す。</summary>
    private static void CoordinatorPauseResume()
    {
        var runtime = new FakeViewRuntime();
        var host = new FakeViewHost { IsEditorDirty = true };
        var coordinator = new AndroidPauseSnapshotViewCoordinator(new SceneSnapshotViewSession(runtime), host);
        var lines = new ConcurrentQueue<AndroidRunOutputLine>();
        coordinator.OutputWritten += lines.Enqueue;

        coordinator.OnRunStateChanged(PausedReady(1, new SceneSnapshotCameraPose(1, 2, 3, 0, 90, 0)));
        Fixtures.WaitUntil(() => coordinator.ViewPhase == SceneSnapshotViewPhase.Showing, "写しを出す");
        Check.True(coordinator.IsViewActive, "閲覧専用の範囲");
        Check.Equal(PhoneText, coordinator.ShownTargetText, "実行先の表示名（バナー用）");
        Check.Equal(1, host.Prepared, "出す前に編集の途中を閉じた");
        Check.Equal(1, host.Shown, "出した後にビューポートの設定を揃え直す");
        Check.True(runtime.Sent.Contains("CAM_TRANSFORM:1,2,3,0,90,0"), "デバッグカメラを端末のメインカメラへ");

        // 同じ写しの知らせがもう 1 度来ても出し直さない
        coordinator.OnRunStateChanged(PausedReady(1));
        Thread.Sleep(20);
        Check.Equal(1, runtime.Sent.Count(command => command.StartsWith(SceneSnapshotWire.ViewBeginPrefix, StringComparison.Ordinal)), "1 度だけ出す");

        host.IsEditorDirty = true;
        coordinator.OnRunStateChanged(Running());
        Fixtures.WaitUntil(() => coordinator.ViewPhase == SceneSnapshotViewPhase.Idle && !host.AppliedDirty.IsEmpty, "戻す");
        Check.Equal(SceneSnapshotWire.ViewEnd, runtime.Sent.Last(), "END を送った");
        Check.Equal("True", string.Join(",", host.AppliedDirty), "表示の前の未保存フラグを当てる");
        var text = string.Join("\n", lines.Select(line => line.Text));
        Check.True(text.Contains("写しをシーンパネルに出しました") && text.Contains("編集中のシーンへ戻しました"), $"Output:\n{text}");

        // 次の一時停止では新しい写しを出す
        coordinator.OnRunStateChanged(PausedReady(2));
        Fixtures.WaitUntil(() => coordinator.ViewPhase == SceneSnapshotViewPhase.Showing, "次の写しを出す");
        Fixtures.Await(coordinator.EndForShutdownAsync(), "閉じる前に戻す");
        Check.Equal(SceneSnapshotViewPhase.Idle, coordinator.ViewPhase, "閉じる前に戻した");
    }

    /// <summary>退避できないとき。</summary>
    private static void CoordinatorStashFailure()
    {
        var runtime = new FakeViewRuntime
        {
            Reply = command => command.StartsWith(SceneSnapshotWire.ViewBeginPrefix, StringComparison.Ordinal)
                ? command.EndsWith("|file_ok", StringComparison.Ordinal)
                    ? $"SNAPSHOT_VIEW_READY:{SnapshotPath}|actors=3|ms=1.0"
                    : $"SNAPSHOT_VIEW_FAILED:{SnapshotPath}|stash|編集中のシーンを直列化できません"
                : "SNAPSHOT_VIEW_ENDED:file|C:/p/Main.scene|ms=1.0",
        };
        // 保存する: 保存してから file_ok でもう 1 度
        var host = new FakeViewHost { IsEditorDirty = true, SaveAnswer = true };
        var coordinator = new AndroidPauseSnapshotViewCoordinator(new SceneSnapshotViewSession(runtime), host);
        coordinator.OnRunStateChanged(PausedReady(1));
        Fixtures.WaitUntil(() => coordinator.ViewPhase == SceneSnapshotViewPhase.Showing, "保存してから出す");
        Check.Equal("編集中のシーンを直列化できません", host.SavePrompts.Single(), "理由を添えて尋ねた");
        Check.True(runtime.Sent.Any(command => command.EndsWith("|file_ok", StringComparison.Ordinal)), "保存したので file_ok");
        coordinator.OnRunStateChanged(Running());
        Fixtures.WaitUntil(() => coordinator.ViewPhase == SceneSnapshotViewPhase.Idle && !host.AppliedDirty.IsEmpty, "戻す");
        Check.Equal("False", string.Join(",", host.AppliedDirty), "ファイルから戻したので未保存なし");

        // 保存しない: 出さずにあきらめる
        var declined = new FakeViewHost { IsEditorDirty = true, SaveAnswer = false };
        var declinedCoordinator = new AndroidPauseSnapshotViewCoordinator(new SceneSnapshotViewSession(runtime), declined);
        var lines = new ConcurrentQueue<AndroidRunOutputLine>();
        declinedCoordinator.OutputWritten += lines.Enqueue;
        declinedCoordinator.OnRunStateChanged(PausedReady(5));
        Fixtures.WaitUntil(() => declinedCoordinator.GivenUpGeneration == 5, "あきらめる");
        Check.Equal(SceneSnapshotViewPhase.Idle, declinedCoordinator.ViewPhase, "出さない");
        Check.True(lines.Any(line => line.Text.Contains(AndroidPauseSnapshotOutputFormatter.ViewSkippedUnsavedText)), "理由を出す");
    }

    /// <summary>出せない状態・編集用ランタイムの終わり。</summary>
    private static void CoordinatorBlockedAndLost()
    {
        var runtime = new FakeViewRuntime();
        var host = new FakeViewHost { SnapshotViewBlocker = "エディタの編集用ランタイムが Edit ではありません（Building）" };
        var coordinator = new AndroidPauseSnapshotViewCoordinator(new SceneSnapshotViewSession(runtime), host);
        var lines = new ConcurrentQueue<AndroidRunOutputLine>();
        coordinator.OutputWritten += lines.Enqueue;
        coordinator.OnRunStateChanged(PausedReady(7));
        Fixtures.WaitUntil(() => coordinator.GivenUpGeneration == 7, "あきらめる");
        Check.True(runtime.Sent.IsEmpty, "何も送らない");
        Check.True(lines.Any(line => line.Text.Contains("Edit ではありません")), "理由を出す");

        // 出している間に編集用ランタイムが終わった
        host.SnapshotViewBlocker = null;
        coordinator.OnRunStateChanged(PausedReady(8));
        Fixtures.WaitUntil(() => coordinator.ViewPhase == SceneSnapshotViewPhase.Showing, "出す");
        coordinator.OnEditRuntimeLost();
        Check.Equal(SceneSnapshotViewPhase.Idle, coordinator.ViewPhase, "出していない状態へ");
        Check.Equal(8, coordinator.GivenUpGeneration, "その回はあきらめる");
        Check.True(lines.Any(line => line.Text.Contains(AndroidPauseSnapshotOutputFormatter.EditRuntimeLostText)), "理由を出す");
        coordinator.OnRunStateChanged(Running());
        Thread.Sleep(20);
        Check.True(!runtime.Sent.Contains(SceneSnapshotWire.ViewEnd), "相手がいないので END は送らない");

        // 閲覧専用で捨てた知らせは間引く
        Check.True(coordinator.OnRefused("SAVE_SCENE"), "最初は出す");
        Check.True(!coordinator.OnRefused("DELETE"), "続けて届いた知らせは間引く");
    }

    // ── 閲覧専用の判断 ─────────────────────────────────────

    /// <summary>閲覧専用の判断。</summary>
    private static void ReadOnlyPolicy()
    {
        var writable = EditorReadOnlyPolicy.Decide(new EditorReadOnlyInput());
        Check.True(ReferenceEquals(EditorReadOnlyState.Writable, writable), "どちらでもなければ保存も編集もできる");
        Check.True(!writable.DeniesSave && !writable.DeniesEdit && writable.EditingUiEnabled && writable.SceneSwitchAllowed, "既定");

        var snapshot = EditorReadOnlyPolicy.Decide(new EditorReadOnlyInput { SnapshotViewActive = true, SnapshotTargetText = PhoneText });
        Check.Equal(EditorReadOnlyCause.DeviceSnapshotView, snapshot.Cause, "写しの表示");
        Check.True(snapshot.DeniesSave && snapshot.DeniesEdit, "保存も編集も拒否");
        Check.True(!snapshot.EditingUiEnabled && !snapshot.SceneSwitchAllowed, "編集の UI を無効・シーンを切り替えない");
        Check.Equal("端末の一時停止の写し（閲覧専用）— Pixel_6a（実機）。再開・停止で編集中のシーンへ戻ります", snapshot.BannerText, "バナー");
        Check.Equal(EditorReadOnlyPolicy.SnapshotTitleMark, snapshot.TitleMark, "タイトルの印");

        var locked = EditorReadOnlyPolicy.Decide(new EditorReadOnlyInput { SceneLocked = true, SceneLockReason = "別のエディタ（PID 1）が開いています" });
        Check.Equal(EditorReadOnlyCause.SceneLockedByOtherEditor, locked.Cause, "ロック");
        Check.Equal("別のエディタ（PID 1）が開いています", locked.SaveDenialReason, "従来の理由のまま");
        Check.True(!locked.DeniesEdit && locked.EditingUiEnabled && locked.SceneSwitchAllowed, "ロックは保存だけを拒否（従来どおり）");
        Check.Equal(EditorReadOnlyPolicy.LockTitleMark, locked.TitleMark, "従来の [読み取り専用]");
        Check.Equal(EditorReadOnlyPolicy.LockSaveDeniedToast, locked.SaveDeniedToast, "従来のトースト");

        var both = EditorReadOnlyPolicy.Decide(new EditorReadOnlyInput { SceneLocked = true, SnapshotViewActive = true });
        Check.Equal(EditorReadOnlyCause.DeviceSnapshotView, both.Cause, "写しの表示がロックより強い");
        Check.True(both.BannerText!.Contains(EditorReadOnlyPolicy.UnknownTargetText), "端末名が分からなければ「端末」");
    }
}
