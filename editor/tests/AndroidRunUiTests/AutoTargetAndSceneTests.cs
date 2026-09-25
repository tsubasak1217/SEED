using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.AndroidRun;
using SEEDEditor.Logging;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// 段階C-3 のエディタ側の判断: Android（自動）・エミュレータへの切り替えの状態と表示、起動するシーン（開いているシーン）、
/// 未保存の変更の確認（保存して実行 / 保存せず実行 / キャンセル）の文言と Output の行。
/// </summary>
public static class AutoTargetAndSceneTests
{
    /// <summary>テストの見張りの間隔（短くして素早く確かめる）。</summary>
    private static readonly AndroidRunTimings FastTimings = new(TimeSpan.FromMilliseconds(10), 2, TimeSpan.FromSeconds(5));

    /// <summary>テストのアセットルート。</summary>
    private static readonly string AssetsRoot = Path.Combine(Path.GetTempPath(), "seed_c3_ui", "Game", "assets");

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("状態機械: 自動（端末未定）で始め、準備で決まった端末をシリアルと表示名（例 emulator-5554（エミュレータ））に入れる", AutoTargetResolvedOnPrepared);
        harness.Add("状態機械: 選んだ端末がそのまま使われたら表示名は変えない・準備の詳細（エミュレータの起動待ち）を写しへ", PrepareDetailAndSameTarget);
        harness.Add("段取り: 自動の指定は中核へ serial=auto で渡し、停止は準備で決まった端末のアプリを止める", ControllerAutoStopsResolvedDevice);
        harness.Add("起動するシーン: 開いているシーンを相対パスで・「開始シーンからプレイ」・保存前の新規・アセットの外", SceneChoice);
        harness.Add("未保存の確認: 他の確認と同じ言い回し・ボタンは 保存して実行／保存せず実行／キャンセル・変更があるときだけ", UnsavedPrompt);
        harness.Add("Output: 起動するシーン・保存して実行・保存に失敗・保存せず実行・キャンセルの行と色", OutputLines);
    }

    /// <summary>自動: 準備で端末が決まる。</summary>
    private static void AutoTargetResolvedOnPrepared()
    {
        var machine = new AndroidRunStateMachine();
        machine.Start(RunTargetCatalogBuilder.AndroidAutoText, null);
        Check.True(machine.Serial is null, "自動は始めた時点では端末が決まっていない");
        machine.Apply(new AndroidPrepared(Fixtures.ReadyEmulator().Device, Fixtures.Identity(), new[] { AndroidAbis.X86_64 }));
        Check.Equal(Fixtures.EmulatorSerial, machine.Serial, "準備で決まったエミュレータ");
        Check.Equal("emulator-5554（エミュレータ）", machine.TargetText, "表示名もその端末（進捗・Output の「で実行中」）");

        // 選んだ実機が見えずエミュレータへ切り替えた: 指定のシリアルと違う端末になったら表示名を変える
        var fallback = new AndroidRunStateMachine();
        fallback.Start("Pixel_6a（未接続）", Fixtures.PhoneSerial);
        fallback.Apply(new AndroidPrepared(Fixtures.ReadyEmulator().Device, Fixtures.Identity(), new[] { AndroidAbis.X86_64 }));
        Check.Equal(Fixtures.EmulatorSerial, fallback.Serial, "切り替えたエミュレータのシリアル");
        Check.Equal("emulator-5554（エミュレータ）", fallback.TargetText, "表示名もエミュレータ");
    }

    /// <summary>同じ端末なら表示名を変えない・準備の詳細。</summary>
    private static void PrepareDetailAndSameTarget()
    {
        var machine = new AndroidRunStateMachine();
        machine.Start("Pixel_6a（実機）", Fixtures.PhoneSerial);
        machine.Apply(new AndroidProgressChanged(AndroidPipelinePhase.Prepare, 0.0, "エミュレータの起動を待っています（12 秒）"));
        Check.Equal("エミュレータの起動を待っています（12 秒）", machine.Snapshot.PrepareDetail, "準備の詳細を写しへ");
        Check.Equal("準備中: エミュレータの起動を待っています（12 秒）", PlayBarPolicy.BuildingProgressText(machine.Snapshot), "進捗の文言");
        machine.Apply(new AndroidPrepared(Fixtures.ReadyPhone().Device, Fixtures.Identity(), new[] { AndroidAbis.Arm64 }));
        Check.Equal("Pixel_6a（実機）", machine.TargetText, "選んだ端末がそのまま使われたら表示名は変えない");
        machine.Apply(new AndroidProgressChanged(AndroidPipelinePhase.Gradle, 0.5, "APK"));
        Check.Equal("エミュレータの起動を待っています（12 秒）", machine.Snapshot.PrepareDetail, "工程の進み具合では準備の詳細を変えない");

        machine.RequestStop(AndroidRunStopReason.User);
        machine.Complete(new AndroidPipelineResult { Canceled = true });
        machine.FinishStopApp();
        machine.Start("x", null);
        Check.True(machine.Snapshot.PrepareDetail is null, "新しい実行では準備の詳細を消す");
    }

    /// <summary>段取り: 自動 → 準備で決まった端末を止める。</summary>
    private static void ControllerAutoStopsResolvedDevice()
    {
        var backend = new FakeBackend
        {
            Run = async (_, progress, token) =>
            {
                progress.Report(new AndroidPhaseStarted(AndroidPipelinePhase.Prepare, 0, 0, "準備", "Run"));
                progress.Report(new AndroidProgressChanged(AndroidPipelinePhase.Prepare, 0.0, "エミュレータの起動を待っています（4 秒）"));
                progress.Report(new AndroidPrepared(Fixtures.ReadyEmulator().Device, Fixtures.Identity(), new[] { AndroidAbis.X86_64 }));
                PipelineScript.Launch(progress);
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    // logcat の途中の中断は「止めた」＝成功
                }
                return new AndroidPipelineResult { Steps = PipelineScript.Steps(AndroidPhaseOutcome.Succeeded) };
            },
        };
        using var controller = new AndroidRunController(backend, FastTimings);
        var request = AndroidEditorRunRequests.ForPlay("D:/proj", RunTargetCatalogBuilder.AndroidAuto(null), "scenes/Second.scene", null);
        Check.True(controller.TryStart(request, RunTargetCatalogBuilder.AndroidAutoText), "始める");
        Fixtures.WaitUntil(() => controller.Snapshot.Phase == AndroidRunPhase.Running, "Running");
        Check.Equal("auto", backend.Requests.Single().Serial, "中核へは serial=auto");
        Check.Equal("scenes/Second.scene", backend.Requests.Single().ScenePath, "起動するシーンも渡す");
        Check.Equal(Fixtures.EmulatorSerial, controller.Snapshot.Serial, "見張り・停止の対象は準備で決まった端末");
        Check.Equal("emulator-5554（エミュレータ） で実行中",
            PlayBarPolicy.Compute(new PlayBarInput(SEEDEditor.Runtime.EditorState.Edit, RunTargetCatalogBuilder.AndroidAuto(null), controller.Snapshot)).ProgressText,
            "実行中の表示は決まった端末");

        Fixtures.Await(controller.StopAsync(), "停止");
        Check.Equal(Fixtures.EmulatorSerial, backend.StopCalls.Single().Serial, "決まった端末のアプリを止める（auto という名の端末を探さない）");
    }

    /// <summary>起動するシーン。</summary>
    private static void SceneChoice()
    {
        var open = AndroidRunSceneChoice.Decide(false, Path.Combine(AssetsRoot, "scenes", "Stage 2.scene"), AssetsRoot);
        Check.Equal(AndroidRunSceneSource.OpenScene, open.Source, "開いているシーン（PC の Play と同じ）");
        Check.Equal("scenes/Stage 2.scene", open.ScenePath, "アセットルートからの相対パス（/ 区切り・空白はそのまま）");
        Check.True(!open.IsFallback, "警告ではない");

        var japanese = AndroidRunSceneChoice.Decide(false, Path.Combine(AssetsRoot, "シーン", "森.scene"), AssetsRoot);
        Check.Equal("シーン/森.scene", japanese.ScenePath, "日本語のパスもそのまま");

        var byOption = AndroidRunSceneChoice.Decide(true, Path.Combine(AssetsRoot, "scenes", "Main.scene"), AssetsRoot);
        Check.Equal(AndroidRunSceneSource.StartSceneByOption, byOption.Source, "「開始シーンからプレイ」がオンなら開始シーン");
        Check.True(byOption.ScenePath is null && !byOption.IsFallback, "extra を渡さない・警告ではない");

        var unsaved = AndroidRunSceneChoice.Decide(false, null, AssetsRoot);
        Check.Equal(AndroidRunSceneSource.StartSceneUnsaved, unsaved.Source, "保存前の新規シーンは開始シーン");
        Check.True(unsaved.IsFallback, "警告");

        var outside = AndroidRunSceneChoice.Decide(false, Path.Combine(Path.GetTempPath(), "elsewhere", "X.scene"), AssetsRoot);
        Check.Equal(AndroidRunSceneSource.StartSceneUnreachable, outside.Source, "アセットフォルダの外は開始シーン");
        Check.True(outside.IsFallback && (outside.Detail ?? string.Empty).Contains("外"), $"理由: {outside.Detail}");
    }

    /// <summary>未保存の確認。</summary>
    private static void UnsavedPrompt()
    {
        Check.True(AndroidUnsavedChangesPrompt.Message.StartsWith("未保存の変更があります。", StringComparison.Ordinal),
            "シーンの切り替え・終了の確認と同じ書き出し");
        Check.True(AndroidUnsavedChangesPrompt.Message.Contains("保存しますか？") && AndroidUnsavedChangesPrompt.Message.Contains("PC の Play"),
            $"尋ね方と PC の Play との違い: {AndroidUnsavedChangesPrompt.Message}");
        Check.Equal("保存して実行", AndroidUnsavedChangesPrompt.SaveAndRunText, "主操作");
        Check.Equal("保存せず実行", AndroidUnsavedChangesPrompt.RunWithoutSavingText, "もう 1 つ");
        Check.Equal("キャンセル", AndroidUnsavedChangesPrompt.CancelText, "取り消し");
        Check.True(AndroidUnsavedChangesPrompt.NeedsPrompt(true) && !AndroidUnsavedChangesPrompt.NeedsPrompt(false), "変更があるときだけ尋ねる");
        Check.Equal(AndroidUnsavedChoice.Cancel, default(AndroidUnsavedChoice), "既定（閉じた・ヘッドレス）は何もしない");
    }

    /// <summary>Output の行。</summary>
    private static void OutputLines()
    {
        var open = AndroidRunOutputFormatter.SceneChosen(new AndroidRunSceneChoice("scenes/Second.scene", AndroidRunSceneSource.OpenScene));
        Check.Equal("[Android] 起動するシーン: scenes/Second.scene（開いているシーン。PC の Play と同じ）", open.Text, "開いているシーン");
        Check.Equal(OutputTone.Runtime, open.Style.Tone, "水色（実行先からの通知）");

        var option = AndroidRunOutputFormatter.SceneChosen(new AndroidRunSceneChoice(null, AndroidRunSceneSource.StartSceneByOption));
        Check.True(option.Text.Contains("開始シーン") && option.Text.Contains("開始シーンからプレイ"), option.Text);
        Check.Equal(OutputTone.Runtime, option.Style.Tone, "設定どおりなので通知の色");

        var unsaved = AndroidRunOutputFormatter.SceneChosen(new AndroidRunSceneChoice(null, AndroidRunSceneSource.StartSceneUnsaved));
        Check.Equal(OutputTone.Warning, unsaved.Style.Tone, "開いているシーンを使えないときは黄");
        var unreachable = AndroidRunOutputFormatter.SceneChosen(
            new AndroidRunSceneChoice(null, AndroidRunSceneSource.StartSceneUnreachable, "アセットフォルダの外にあります"));
        Check.True(unreachable.Text.EndsWith("アセットフォルダの外にあります", StringComparison.Ordinal), $"理由を添える: {unreachable.Text}");

        Check.Equal(OutputTone.Runtime, AndroidRunOutputFormatter.SavingBeforeRun().Style.Tone, "保存してから実行");
        Check.Equal(OutputTone.Warning, AndroidRunOutputFormatter.SaveFailedRunCanceled().Style.Tone, "保存に失敗して取りやめ");
        Check.Equal(OutputTone.Warning, AndroidRunOutputFormatter.SaveNotStartedRunCanceled().Style.Tone, "保存できず取りやめ");
        Check.True(AndroidRunOutputFormatter.UnsavedChangesWarning().Text.Contains("保存せずに実行します"), "保存せず実行の警告");
        Check.Equal(OutputTone.Default, AndroidRunOutputFormatter.UnsavedPromptCanceled().Style.Tone, "キャンセルは灰");
    }
}
