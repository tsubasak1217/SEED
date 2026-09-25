using System.Collections.Concurrent;
using SEEDEditor.Android.HotReload;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.AndroidRun;
using SEEDEditor.Logging;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// 実行中の差し替え（docs/android.md §23）のエディタ側の段取り: 変わったファイルのまとめ方（デバウンス・差し替えの途中の変更）、
/// 監視の始め方・止め方（Android の実行の状態に合わせる）、関係ないファイルを落とすこと、つながっていないときの扱い、
/// Output の行、実行中の実行と差し替えの相手（AndroidRunController.HotReloadTarget）。端末・adb・ファイル監視は使わない
/// （時計は手で進め、見回りは TickAsync を直接呼ぶ）。
/// </summary>
public static class HotReloadControllerTests
{
    /// <summary>静かな時間（テスト用。既定と同じ 0.6 秒）。</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);

    /// <summary>テストのアセットルート（ファイル監視はしないので実在しなくてよい）。</summary>
    private static readonly string AssetsRoot = Path.Combine(Path.GetTempPath(), "SEED_HotReloadUiTest", "assets");

    /// <summary>テストの時計の起点。</summary>
    private static readonly DateTime T0 = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("差し替え（§23）: まとめ役は最後の変更から静かな時間が過ぎたら 1 組にする（同じファイルは 1 件・連続保存は 1 回）", QueueDebounces);
        harness.Add("差し替え（§23）: 差し替えの途中に来た変更は覚えておき、終わった後に静かな時間を待って次の組にする", QueueKeepsChangesDuringFlight);
        harness.Add("差し替え（§23）: 段取りは連続保存を 1 回の差し替えにまとめ、関係ないファイル・アセットルートの外を落とす", ControllerCoalescesAndFilters);
        harness.Add("差し替え（§23）: 端末のアプリとつながっていなければ差し替えずに理由を 1 行出す", ControllerReportsNotConnected);
        harness.Add("差し替え（§23）: 種類ごとの設定がオフ（スクリプト・シーンの自動再読込）なら、その種類の変更は覚えない", ControllerHonorsKindSettings);
        harness.Add("差し替え（§23）: 監視は端末のアプリが動いている間だけ（Running / Paused で始め、止めたら変更を捨てる）", ControllerFollowsRunState);
        harness.Add("差し替え（§23）: 中核の予期しない失敗でも監視を続け、理由を赤で出す", ControllerSurvivesBackendCrash);
        harness.Add("差し替え（§23）: Output の行（送った・同じ・応答ごとの反映／反映しない／失敗・まとめ）と宛先の表示名", FormatsResultLines);
        harness.Add("差し替え（§23）: 実行中の差し替えの相手は、アプリが動いていて IPC がつながっているときだけ", RunControllerExposesTarget);
    }

    /// <summary>デバウンス。</summary>
    private static void QueueDebounces()
    {
        var queue = new AndroidHotReloadChangeQueue(Quiet);
        Check.True(!queue.IsDue(T0), "何も覚えていなければ出さない");
        queue.Add("scenes/Main.scene", T0);
        queue.Add("textures/a.png", T0.AddMilliseconds(300));
        queue.Add("SCENES/main.scene", T0.AddMilliseconds(400));
        Check.True(!queue.IsDue(T0.AddMilliseconds(900)), "最後の変更（0.4 秒）から 0.6 秒経っていない");
        Check.True(queue.TakeBatch(T0.AddMilliseconds(900)).Count == 0, "早すぎれば空");
        Check.True(queue.IsDue(T0.AddMilliseconds(1000)), "最後の変更から 0.6 秒");
        var batch = queue.TakeBatch(T0.AddMilliseconds(1000));
        Check.Equal("scenes/Main.scene,textures/a.png", string.Join(",", batch), "最初に来た順・大文字小文字だけ違う同じファイルは 1 件");
        Check.True(queue.InFlight && !queue.HasPending, "取り出したら差し替えの途中・覚えたものは空");
    }

    /// <summary>差し替えの途中の変更。</summary>
    private static void QueueKeepsChangesDuringFlight()
    {
        var queue = new AndroidHotReloadChangeQueue(Quiet);
        queue.Add("a.png", T0);
        _ = queue.TakeBatch(T0.AddSeconds(1));
        queue.Add("b.png", T0.AddSeconds(2));
        Check.True(!queue.IsDue(T0.AddSeconds(10)), "差し替えの途中は次の組を出さない（同時に 2 つ走らせない）");
        queue.Complete(T0.AddSeconds(11));
        Check.True(!queue.IsDue(T0.AddSeconds(11.5)), "終わってから静かな時間を待つ");
        Check.Equal("b.png", string.Join(",", queue.TakeBatch(T0.AddSeconds(11.7))), "途中に来た変更は取りこぼさない");
        queue.Complete(T0.AddSeconds(12));
        queue.Add("c.png", T0.AddSeconds(13));
        queue.Clear();
        Check.True(!queue.HasPending && !queue.IsDue(T0.AddSeconds(20)), "Clear で覚えたものを捨てる");
    }

    /// <summary>まとめて 1 回・関係ないものを落とす。</summary>
    private static void ControllerCoalescesAndFilters()
    {
        var clock = new ManualClock(T0);
        var backend = new RecordingBackend();
        var link = new FakeIpcLink();
        using var controller = new AndroidHotReloadController(
            backend, () => new AndroidHotReloadTarget(Fixtures.PhoneSerial, Fixtures.ApplicationId, link),
            Quiet, pollInterval: null, utcNow: () => clock.Now, watchFileSystem: false);
        var lines = Collect(controller);
        controller.Start(AssetsRoot);

        controller.NotifyChanged(Path.Combine(AssetsRoot, "scenes", "Main.scene"));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        controller.NotifyChanged(Path.Combine(AssetsRoot, "scenes", "Main.scene"));
        controller.NotifyChanged(Path.Combine(AssetsRoot, "packaging_settings.json"));
        controller.NotifyChanged(Path.Combine(AssetsRoot, "scripts", "obj", "x.cs"));
        controller.NotifyChanged(Path.Combine(AssetsRoot, "scenes", "Main.scene.tmp"));
        controller.NotifyChanged(Path.Combine(Path.GetTempPath(), "outside", "a.png"));
        controller.NotifyChanged(Path.Combine(AssetsRoot, "scripts", "Player.cs"));

        Fixtures.Await(controller.TickAsync(), "静かな時間の前の見回り");
        Check.Equal(0, backend.Calls.Count, "静かな時間の前は差し替えない");
        clock.Advance(Quiet);
        Fixtures.Await(controller.TickAsync(), "見回り");
        Check.Equal(1, backend.Calls.Count, "連続保存は 1 回にまとめる");
        Check.Equal("scenes/Main.scene,scripts/Player.cs", string.Join(",", backend.Calls.Single().Changed),
            "関係ないもの（パッケージ化の設定・obj・一時ファイル・アセットルートの外）は落とす。区切りは /");
        Check.Equal(Fixtures.PhoneSerial, backend.Calls.Single().Target.Serial, "差し替える相手");
        Fixtures.Await(controller.TickAsync(), "もう一度の見回り");
        Check.Equal(1, backend.Calls.Count, "同じ変更で二度差し替えない");
        var text = string.Join("\n", lines.Select(line => line.Text));
        Check.True(text.Contains("差し替え: 2 件の変更（scenes/Main.scene ほか）"), $"始めた行:\n{text}");
        Check.True(text.Contains("差し替え完了"), $"終わりの行:\n{text}");
    }

    /// <summary>つながっていない。</summary>
    private static void ControllerReportsNotConnected()
    {
        var clock = new ManualClock(T0);
        var backend = new RecordingBackend();
        using var controller = new AndroidHotReloadController(
            backend, () => null, Quiet, pollInterval: null, utcNow: () => clock.Now, watchFileSystem: false);
        var lines = Collect(controller);
        controller.Start(AssetsRoot);
        controller.NotifyChanged(Path.Combine(AssetsRoot, "textures", "a.png"));
        clock.Advance(Quiet);
        Fixtures.Await(controller.TickAsync(), "見回り");
        Check.Equal(0, backend.Calls.Count, "つながっていなければ差し替えない");
        var line = lines.Single();
        Check.Equal(OutputTone.Warning, line.Style.Tone, "警告の色");
        Check.True(line.Text.Contains("差し替えできません"), line.Text);
    }

    /// <summary>種類ごとの設定。</summary>
    private static void ControllerHonorsKindSettings()
    {
        var clock = new ManualClock(T0);
        var backend = new RecordingBackend();
        var scriptsEnabled = false;
        using var controller = new AndroidHotReloadController(
            backend, () => new AndroidHotReloadTarget(Fixtures.PhoneSerial, Fixtures.ApplicationId, new FakeIpcLink()),
            Quiet, pollInterval: null, utcNow: () => clock.Now, watchFileSystem: false,
            kindEnabled: kind => kind != AndroidHotReloadKind.Scripts || scriptsEnabled);
        controller.Start(AssetsRoot);
        controller.NotifyChanged(Path.Combine(AssetsRoot, "scripts", "Player.cs"));
        controller.NotifyChanged(Path.Combine(AssetsRoot, "textures", "a.png"));
        clock.Advance(Quiet);
        Fixtures.Await(controller.TickAsync(), "見回り");
        Check.Equal("textures/a.png", string.Join(",", backend.Calls.Single().Changed), "オフの種類（スクリプト）は覚えない");
        scriptsEnabled = true;
        controller.NotifyChanged(Path.Combine(AssetsRoot, "scripts", "Player.cs"));
        clock.Advance(Quiet);
        Fixtures.Await(controller.TickAsync(), "見回り（設定をオンにした後）");
        Check.Equal("scripts/Player.cs", string.Join(",", backend.Calls.Last().Changed), "設定の切り替えはすぐ効く");
    }

    /// <summary>実行の状態に合わせて監視する。</summary>
    private static void ControllerFollowsRunState()
    {
        var clock = new ManualClock(T0);
        var backend = new RecordingBackend();
        using var controller = new AndroidHotReloadController(
            backend, () => null, Quiet, pollInterval: null, utcNow: () => clock.Now, watchFileSystem: false);
        controller.OnRunStateChanged(new AndroidRunSnapshot { Phase = AndroidRunPhase.Building }, AssetsRoot);
        Check.True(!controller.IsWatching, "ビルド中は監視しない");
        controller.OnRunStateChanged(new AndroidRunSnapshot { Phase = AndroidRunPhase.Running }, AssetsRoot);
        Check.True(controller.IsWatching, "実行中は監視する");
        controller.OnRunStateChanged(new AndroidRunSnapshot { Phase = AndroidRunPhase.Paused }, AssetsRoot);
        Check.True(controller.IsWatching, "一時停止中も監視する（一時停止中に差し替えてもよい）");
        controller.NotifyChanged(Path.Combine(AssetsRoot, "textures", "a.png"));
        controller.OnRunStateChanged(new AndroidRunSnapshot { Phase = AndroidRunPhase.Stopping }, AssetsRoot);
        Check.True(!controller.IsWatching, "止めている途中からは監視しない");
        controller.OnRunStateChanged(new AndroidRunSnapshot { Phase = AndroidRunPhase.Running }, AssetsRoot);
        clock.Advance(Quiet);
        Fixtures.Await(controller.TickAsync(), "見回り");
        Check.Equal(0, backend.Calls.Count, "止めたときに覚えた変更は捨てる（次の実行へ持ち越さない）");
        controller.NotifyChanged(Path.Combine(AssetsRoot, "textures", "b.png"));
        controller.OnRunStateChanged(AndroidRunSnapshot.Idle, AssetsRoot);
        controller.NotifyChanged(Path.Combine(AssetsRoot, "textures", "c.png"));
        Check.True(!controller.IsWatching, "Idle では監視しない（変更は覚えない）");
    }

    /// <summary>中核の予期しない失敗。</summary>
    private static void ControllerSurvivesBackendCrash()
    {
        var clock = new ManualClock(T0);
        var backend = new RecordingBackend { Throw = new InvalidOperationException("テスト: 中核の不具合") };
        using var controller = new AndroidHotReloadController(
            backend, () => new AndroidHotReloadTarget(Fixtures.PhoneSerial, Fixtures.ApplicationId, new FakeIpcLink()),
            Quiet, pollInterval: null, utcNow: () => clock.Now, watchFileSystem: false);
        var lines = Collect(controller);
        controller.Start(AssetsRoot);
        controller.NotifyChanged(Path.Combine(AssetsRoot, "a.png"));
        clock.Advance(Quiet);
        Fixtures.Await(controller.TickAsync(), "見回り（失敗）");
        Check.True(lines.Any(line => line.Style.Tone == OutputTone.Error && line.Text.Contains("テスト: 中核の不具合")), "理由を赤で出す");

        backend.Throw = null;
        controller.NotifyChanged(Path.Combine(AssetsRoot, "b.png"));
        clock.Advance(Quiet);
        Fixtures.Await(controller.TickAsync(), "見回り（次）");
        Check.Equal(2, backend.Calls.Count, "失敗の後も監視を続け、次の保存で差し替える");
    }

    /// <summary>Output の行。</summary>
    private static void FormatsResultLines()
    {
        var plan = new AndroidOverlayPlan(
            new[] { new AndroidOverlayItem("scenes/Main.scene", new AndroidLocalAssetContent("D", 2 * 1024 * 1024, null, "x"), AndroidOverlayChange.Changed) },
            new[] { "textures/same.png" },
            Array.Empty<AndroidOverlaySkip>());
        var result = new AndroidHotReloadResult
        {
            Scripts = new[] { "scripts/P.cs" },
            Assets = new[] { "scenes/Main.scene" },
            ScriptsPushed = "5 ファイル・9.1 MB を送りました",
            ScriptsElapsed = TimeSpan.FromSeconds(6.2),
            Overlay = new AndroidOverlaySyncResult(plan, 2, AndroidAssetOverlaySync.PakChangedNote, TimeSpan.FromSeconds(0.4)),
            Replies = new[]
            {
                new AndroidReloadReply(AndroidReloadReplies.ScriptsTarget, AndroidReloadOutcome.Done, null, "3 型・再生成 5 件"),
                new AndroidReloadReply("scene:scenes/Main.scene", AndroidReloadOutcome.Done, 123.4, "scene:assets://scenes/Main.scene"),
                new AndroidReloadReply("asset:fonts/a.ttf", AndroidReloadOutcome.Skipped, null, "起動時に 1 回だけ読む"),
                new AndroidReloadReply("asset:models/a.glb", AndroidReloadOutcome.NoReply, null, "応答しませんでした"),
            },
            Elapsed = TimeSpan.FromSeconds(7.5),
        };
        var lines = AndroidHotReloadOutputFormatter.Completed(result);
        var text = string.Join("\n", lines.Select(line => line.Text));
        Check.True(text.Contains("スクリプト: DLL を作り直して送りました（5 ファイル・9.1 MB を送りました・6.2 秒）"), text);
        Check.True(text.Contains("アセット: 1 ファイル・2.0 MB を送りました（候補 2・端末と同じ 1・0.4 秒）"), text);
        Check.True(text.Contains("反映: スクリプト — スクリプトを読み直しました（3 型・再生成 5 件）"), text);
        Check.True(text.Contains("反映: scenes/Main.scene — シーンを読み直しました（assets://scenes/Main.scene）（端末 123.4 ms）"), text);
        Check.Equal(OutputTone.Warning, lines.Single(line => line.Text.Contains("fonts/a.ttf")).Style.Tone, "反映しなかった（SKIPPED）は黄");
        Check.Equal(OutputTone.Error, lines.Single(line => line.Text.Contains("models/a.glb")).Style.Tone, "応答なしは赤");
        Check.Equal(OutputTone.Warning, lines.Single(line => line.Text.Contains(AndroidAssetOverlaySync.PakChangedNote)).Style.Tone, "pak と比べなかった理由は黄");
        Check.True(lines[^1].Text.Contains("差し替えで失敗がありました") && lines[^1].Style.Tone == OutputTone.Error, "失敗があればまとめも赤");

        Check.Equal("今のシーン", AndroidHotReloadOutputFormatter.TargetLabel("scene"), "無条件のシーン");
        Check.Equal("ui/a.png", AndroidHotReloadOutputFormatter.TargetLabel("asset:ui/a.png"), "アセット");

        var nothing = AndroidHotReloadOutputFormatter.Completed(new AndroidHotReloadResult { Ignored = 3 });
        Check.True(nothing.Single().Text.Contains("端末に関係する変更はありません（3 件を飛ばしました）"), "関係する変更が無いとき");
        var ok = AndroidHotReloadOutputFormatter.Completed(new AndroidHotReloadResult
        {
            Assets = new[] { "ui/a.png" },
            Replies = new[] { new AndroidReloadReply("asset:ui/a.png", AndroidReloadOutcome.Done, 1.5, "inplace") },
            Elapsed = TimeSpan.FromSeconds(0.3),
        });
        Check.True(ok.Any(line => line.Text.Contains("反映: ui/a.png — キャッシュを捨てて差し替えました（端末 1.5 ms）")), "InPlace の反映");
        Check.True(ok[^1].Text.Contains("差し替え完了（0.3 秒）") && ok[^1].Style.Tone == OutputTone.Runtime, "成功のまとめは水色");
    }

    /// <summary>実行中の差し替えの相手。</summary>
    private static void RunControllerExposesTarget()
    {
        var link = new FakeIpcLink();
        var backend = new FakeBackend
        {
            Run = (_, progress, token) => PipelineScript.RunUntilCanceledAsync(progress, token),
            ConnectIpc = (_, _) => Task.FromResult<IAndroidIpcLink>(link),
        };
        using var controller = new AndroidRunController(backend, new AndroidRunTimings(TimeSpan.FromMilliseconds(10), 2, TimeSpan.FromSeconds(5)));
        Check.True(controller.HotReloadTarget is null, "動いていなければ相手はいない");
        controller.TryStart(AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneTarget(), null, null), "Pixel_6a（実機）");
        Fixtures.WaitUntil(() => controller.Snapshot.Ipc == AndroidIpcStatus.Connected, "つながる");
        var target = controller.HotReloadTarget;
        Check.True(target is not null && ReferenceEquals(target.Link, link), "つながっている間は IPC の通信路を渡す");
        Check.Equal(Fixtures.PhoneSerial, target!.Serial, "端末");
        Check.Equal(Fixtures.ApplicationId, target.ApplicationId, "アプリ ID");
        Fixtures.Await(controller.StopAsync(), "停止");
        Check.True(controller.HotReloadTarget is null, "止めたら相手はいない");
    }

    /// <summary>Output の行を集める。</summary>
    private static ConcurrentQueue<AndroidRunOutputLine> Collect(AndroidHotReloadController controller)
    {
        var lines = new ConcurrentQueue<AndroidRunOutputLine>();
        controller.OutputWritten += lines.Enqueue;
        return lines;
    }

    /// <summary>手で進める時計。</summary>
    private sealed class ManualClock(DateTime start)
    {
        /// <summary>今の時刻。</summary>
        public DateTime Now { get; private set; } = start;

        /// <summary>進める。</summary>
        public void Advance(TimeSpan span) => Now += span;
    }

    /// <summary>呼ばれ方を記録する偽の中核（端末を使わない）。</summary>
    private sealed class RecordingBackend : IAndroidHotReloadBackend
    {
        /// <summary>呼ばれ方。</summary>
        public ConcurrentQueue<(IReadOnlyList<string> Changed, AndroidHotReloadTarget Target)> Calls { get; } = new();

        /// <summary>投げる例外（null なら成功の結果を返す）。</summary>
        public Exception? Throw { get; set; }

        /// <inheritdoc />
        public Task<AndroidHotReloadResult> ApplyAsync(
            IReadOnlyList<string> changed, AndroidHotReloadTarget target, Action<string> info, CancellationToken cancellationToken)
        {
            Calls.Enqueue((changed, target));
            if (Throw is not null) return Task.FromException<AndroidHotReloadResult>(Throw);
            return Task.FromResult(new AndroidHotReloadResult
            {
                Assets = changed,
                Replies = changed.Select(relative => new AndroidReloadReply("asset:" + relative, AndroidReloadOutcome.Done, 1.0, "inplace")).ToList(),
                Elapsed = TimeSpan.FromMilliseconds(50),
            });
        }
    }
}
