using SEEDEditor.Android;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.AndroidRun;
using SEEDEditor.Logging;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>Output パネルへ出す文言と色（Android の実行の行・logcat・従来の色分け）。</summary>
public static class OutputFormattingTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("Output: 飛ばした工程は 1 行（入力も出力も同じなら「変更なし」、それ以外は理由）", SkippedStepIsOneLine);
        harness.Add("Output: 工程の見出しは [i/n] 工程名 — 理由（ビルドの色）・準備は [準備]", StepHeadings);
        harness.Add("Output: 工程の結果（完了は灰・中断は黄・失敗は赤）と準備で決まった端末・アプリの行", StepResults);
        harness.Add("Output: 子プロセスの出力はビルドの色（エラーらしい行は赤）・警告は黄・複数行は行ごと", LogLines);
        harness.Add("Output: logcat は「[logcat] 重要度/タグ: 本文」で、E/F は赤・W は黄、DOTNET と [Script] はゲーム", LogcatLines);
        harness.Add("logcat の解析: threadtime の各欄・本文の中の「: 」・空の本文・形に合わない行", ParsesLogcat);
        harness.Add("Output: 終わり方の行（停止・中止・アプリの終了・logcat の終わり・失敗の種類と対処）", CompletionLines);
        harness.Add("従来の色分け（見た目の指定が無い行）: [Runtime→Editor] が最優先・error/失敗/EXCEPTION・[cargo] 等・[Script はゲーム", LegacyClassifier);
    }

    /// <summary>1 行だけ返ることを確かめて取り出す。</summary>
    private static AndroidRunOutputLine Single(AndroidPipelineEvent pipelineEvent)
    {
        var lines = AndroidRunOutputFormatter.Format(pipelineEvent);
        Check.Equal(1, lines.Count, $"{pipelineEvent.GetType().Name} は 1 行");
        return lines[0];
    }

    /// <summary>飛ばした工程。</summary>
    private static void SkippedStepIsOneLine()
    {
        var unchanged = Single(new AndroidPhaseFinished(AndroidPipelinePhase.PackageContent, 2, 7, "APK に入れる pak とスクリプト（SeedPak）",
            AndroidPhaseOutcome.Skipped, TimeSpan.Zero, "変更なし"));
        Check.Equal("[Android] [2/7] APK に入れる pak とスクリプト（SeedPak） — 変更なし", unchanged.Text, "変更なし");
        Check.Equal(OutputTone.Default, unchanged.Style.Tone, "灰");
        Check.Equal(OutputSource.Engine, unchanged.Style.Source, "エンジン");

        var install = Single(new AndroidPhaseFinished(AndroidPipelinePhase.Install, 5, 7, "インストール（adb install）",
            AndroidPhaseOutcome.Skipped, TimeSpan.Zero, "端末に同じ APK が入っている"));
        Check.Equal("[Android] [5/7] インストール（adb install） — 飛ばしました（端末に同じ APK が入っている）", install.Text, "理由を添える");
        Check.Equal(AndroidRunOutputFormatter.UnchangedText, AndroidRunOutputFormatter.DescribeSkip(SEEDEditor.Android.Plan.AndroidBuildPlan.UnchangedReason),
            "計画の「変更なし」の理由と同じ値");
    }

    /// <summary>工程の見出し。</summary>
    private static void StepHeadings()
    {
        var gradle = Single(new AndroidPhaseStarted(AndroidPipelinePhase.Gradle, 4, 7, "APK の作成（Gradle）", "入力が変わった"));
        Check.Equal("[Android] [4/7] APK の作成（Gradle） — 入力が変わった", gradle.Text, "見出し");
        Check.Equal(OutputTone.Build, gradle.Style.Tone, "ビルドの色");
        var prepare = Single(new AndroidPhaseStarted(AndroidPipelinePhase.Prepare, 0, 0, "準備", "Run"));
        Check.True(prepare.Text.StartsWith("[Android] [準備] "), $"準備の見出し: {prepare.Text}");
        Check.Equal(0, AndroidRunOutputFormatter.Format(new AndroidProgressChanged(AndroidPipelinePhase.Gradle, 0.5, "x")).Count,
            "進み具合は行にしない（ツールバーへ）");
    }

    /// <summary>工程の結果。</summary>
    private static void StepResults()
    {
        var done = Single(new AndroidPhaseFinished(AndroidPipelinePhase.Gradle, 4, 7, "APK の作成（Gradle）",
            AndroidPhaseOutcome.Succeeded, TimeSpan.FromSeconds(11.74), "app-debug.apk 57.8 MB（x86_64・com.a.b）"));
        Check.Equal("[Android]     完了: app-debug.apk 57.8 MB（x86_64・com.a.b）（11.7 秒）", done.Text, "完了");
        Check.Equal(OutputTone.Default, done.Style.Tone, "灰");

        var prepared = Single(new AndroidPhaseFinished(AndroidPipelinePhase.Prepare, 0, 7, "準備",
            AndroidPhaseOutcome.Succeeded, TimeSpan.FromSeconds(0.26), "2 工程を行い、5 工程を飛ばします"));
        Check.True(prepared.Text.Contains("準備完了: 2 工程を行い、5 工程を飛ばします（0.3 秒）"), $"準備完了: {prepared.Text}");

        var canceled = Single(new AndroidPhaseFinished(AndroidPipelinePhase.Gradle, 4, 7, "APK", AndroidPhaseOutcome.Canceled, TimeSpan.FromSeconds(2), "中断しました"));
        Check.Equal(OutputTone.Warning, canceled.Style.Tone, "中断は黄");
        Check.True(canceled.Text.EndsWith("中断しました（2.0 秒）"), canceled.Text);

        var failed = Single(new AndroidPhaseFinished(AndroidPipelinePhase.Install, 5, 7, "インストール", AndroidPhaseOutcome.Failed,
            TimeSpan.FromSeconds(1), "adb install が失敗しました"));
        Check.Equal(OutputTone.Error, failed.Style.Tone, "失敗は赤");

        var target = Single(new AndroidPrepared(Fixtures.ReadyPhone().Device, Fixtures.Identity(), new[] { AndroidAbis.Arm64 }));
        Check.Equal("[Android] 端末: 2B011JEGR02535（実機・Pixel_6a）・ABI: arm64-v8a・アプリ ID: com.seedengine.uitest", target.Text, "準備で決まった値");
        Check.Equal(OutputTone.Runtime, target.Style.Tone, "実行先の通知の色");
        var noDevice = Single(new AndroidPrepared(null, Fixtures.Identity(), AndroidAbis.Supported));
        Check.True(noDevice.Text.Contains("なし（ビルドだけ）") && noDevice.Text.Contains("arm64-v8a,x86_64"), noDevice.Text);
    }

    /// <summary>1 行のログ。</summary>
    private static void LogLines()
    {
        var compiling = Single(new AndroidLogLine(AndroidPipelinePhase.NativeBuild, AndroidLogLevel.ProcessError, "   Compiling SEED v0.1.0"));
        Check.Equal(OutputTone.Build, compiling.Style.Tone, "cargo の進捗（標準エラー）はビルドの色");
        Check.Equal("[Android]        Compiling SEED v0.1.0", compiling.Text, "字下げ");
        var error = Single(new AndroidLogLine(AndroidPipelinePhase.NativeBuild, AndroidLogLevel.ProcessError, "error[E0425]: cannot find value `x`"));
        Check.Equal(OutputTone.Error, error.Style.Tone, "エラーらしい行は赤");
        var warning = Single(new AndroidLogLine(AndroidPipelinePhase.Prepare, AndroidLogLevel.Warning, "前回の APK には x86_64 が入っていません"));
        Check.Equal("[Android]     警告: 前回の APK には x86_64 が入っていません", warning.Text, "警告の印");
        Check.Equal(OutputTone.Warning, warning.Style.Tone, "黄");
        var info = Single(new AndroidLogLine(AndroidPipelinePhase.Prepare, AndroidLogLevel.Info, "ABI: x86_64（端末から判定）"));
        Check.Equal(OutputTone.Default, info.Style.Tone, "説明は灰");

        var multi = AndroidRunOutputFormatter.Format(new AndroidPipelineError(AndroidPipelinePhase.Prepare, AndroidFailureKind.Device,
            "端末が 2 台つながっています。シリアルで対象を指定してください:\r\n  emulator-5554  エミュレータ  -\n  2B01  実機  Pixel_6a"));
        Check.Equal(3, multi.Count, "複数行は行ごと");
        Check.Equal("[Android] エラー（端末）: 端末が 2 台つながっています。シリアルで対象を指定してください:", multi[0].Text, "1 行目に種類");
        Check.Equal("[Android]     emulator-5554  エミュレータ  -", multi[1].Text, "2 行目以降は字下げを足す");
        Check.True(multi.All(line => line.Style.Tone == OutputTone.Error), "すべて赤");
    }

    /// <summary>logcat の行。</summary>
    private static void LogcatLines()
    {
        var engine = AndroidRunOutputFormatter.FormatLogcat("09-25 14:03:12.345  1234  1250 I SEED    : [SEED INIT] backend=Vulkan");
        Check.Equal("[logcat] I/SEED: [SEED INIT] backend=Vulkan", engine.Text, "重要度/タグ: 本文");
        Check.Equal(new OutputLineStyle(OutputTone.Default, OutputSource.Engine), engine.Style, "エンジンの灰");

        var script = AndroidRunOutputFormatter.FormatLogcat("09-25 14:03:13.000  1234  1300 I DOTNET  : [Script] OnStart");
        Check.Equal(OutputSource.Game, script.Style.Source, "タグ DOTNET はゲーム");
        var mono = AndroidRunOutputFormatter.FormatLogcat("09-25 14:03:13.000  1234  1300 I SEED    : [Script] OnStart (mono)");
        Check.Equal(OutputSource.Game, mono.Style.Source, "Mono（タグ SEED）でも [Script] はゲーム");
        var host = AndroidRunOutputFormatter.FormatLogcat("09-25 14:03:13.000  1234  1300 I DOTNET  : [SEEDScripting] loaded");
        Check.Equal(OutputSource.Game, host.Style.Source, "スクリプトホストの行もゲーム側");

        Check.Equal(OutputTone.Error,
            AndroidRunOutputFormatter.FormatLogcat("09-25 14:03:14.000  1234  1234 E AndroidRuntime: FATAL EXCEPTION: main").Style.Tone, "E は赤");
        Check.Equal(OutputTone.Error,
            AndroidRunOutputFormatter.FormatLogcat("09-25 14:03:14.000  1234  1234 F libc    : Fatal signal 11 (SIGSEGV)").Style.Tone, "F は赤");
        Check.Equal(OutputTone.Warning,
            AndroidRunOutputFormatter.FormatLogcat("09-25 14:03:14.000  1234  1234 W vulkan  : slow").Style.Tone, "W は黄");
        Check.Equal(OutputTone.Error,
            AndroidRunOutputFormatter.FormatLogcat("09-25 14:03:14.000  1234  1234 I SEED    : [App][ERROR] assets.pak を開けません").Style.Tone,
            "I でも本文がエラーなら赤（エンジンの標準エラーは I で届く）");

        var divider = AndroidRunOutputFormatter.FormatLogcat("--------- beginning of main");
        Check.Equal("[logcat] --------- beginning of main", divider.Text, "形に合わない行はそのまま");
        Check.Equal(OutputTone.Default, divider.Style.Tone, "灰");
    }

    /// <summary>logcat の解析。</summary>
    private static void ParsesLogcat()
    {
        var line = LogcatLineParser.Parse("09-25 14:03:12.345  1234  1250 I SEED    : 時刻: 12:00: ok\r");
        Check.True(line is not null, "読める");
        Check.Equal("09-25 14:03:12.345", line!.Time, "時刻");
        Check.Equal(1234, line.ProcessId, "PID");
        Check.Equal('I', line.Level, "重要度");
        Check.Equal("SEED", line.Tag, "タグ（詰め物の空白を落とす）");
        Check.Equal("時刻: 12:00: ok", line.Message, "本文の中の「: 」はそのまま");

        var empty = LogcatLineParser.Parse("09-25 14:03:12.345  1234  1250 D SEED    :");
        Check.True(empty is not null && empty.Message.Length == 0, "空の本文");
        var spacedTag = LogcatLineParser.Parse("09-25 14:03:12.345  1234  1250 I ActivityTaskManager: Displayed com.a.b/.Main: +1s");
        Check.Equal("ActivityTaskManager", spacedTag!.Tag, "長いタグ");
        Check.Equal("Displayed com.a.b/.Main: +1s", spacedTag.Message, "本文");
        Check.True(LogcatLineParser.Parse("adb: device offline") is null, "形に合わない");
        Check.True(LogcatLineParser.Parse(string.Empty) is null, "空");
    }

    /// <summary>終わり方の行。</summary>
    private static void CompletionLines()
    {
        var steps = PipelineScript.Steps(AndroidPhaseOutcome.Succeeded);
        var ok = new AndroidPipelineResult { Steps = steps, Elapsed = TimeSpan.FromSeconds(15.24) };
        IReadOnlyList<AndroidRunOutputLine> Lines(AndroidRunOutcome outcome, AndroidPipelineResult? result = null, string? stopError = null) =>
            AndroidRunOutputFormatter.Completed(new AndroidRunCompletion(outcome, false, result ?? ok), stopError);

        var stopped = Lines(AndroidRunOutcome.StoppedByUser);
        Check.Equal("[Android] 停止しました（端末のアプリを止めました）。", stopped[0].Text, "停止");
        Check.Equal(OutputTone.Runtime, stopped[0].Style.Tone, "実行先の通知の色");
        Check.Equal("[Android] 工程: 2 を行い、1 を飛ばしました（始めてから 15.2 秒）", stopped[1].Text, "工程の数（準備は数えない）");

        Check.True(Lines(AndroidRunOutcome.BuildCanceled)[0].Text.Contains("ビルドを中止しました"), "中止");
        var appExited = Lines(AndroidRunOutcome.AppExited)[0];
        Check.Equal("[Android] 端末でアプリが終わったので実行を終えました（最近のタスクから消した・強制停止・クラッシュ等。直前の logcat を確認してください）。",
            appExited.Text, "アプリの終了（プロセスが終わる操作を挙げる）");
        Check.True(!appExited.Text.Contains("戻るキー"), "戻るキーではアプリは終わらないので理由に挙げない（段階C-4 に実機で確認）");
        Check.Equal(OutputTone.Runtime, appExited.Style.Tone, "アプリの終了は実行先の通知の色");
        Check.Equal(OutputTone.Warning, Lines(AndroidRunOutcome.LogcatEnded)[0].Style.Tone, "logcat の終わりは黄");
        Check.Equal(OutputTone.Error, Lines(AndroidRunOutcome.StoppedByUser, stopError: "timeout")[0].Style.Tone, "アプリを止められないのは赤");

        var toolchain = Lines(AndroidRunOutcome.Failed,
            new AndroidPipelineResult { FailureKind = AndroidFailureKind.Toolchain, FailureMessage = "JDK が見つかりません" });
        Check.True(toolchain[0].Text.Contains("実行できませんでした（道具）") && toolchain[0].Text.Contains("JDK"), $"道具の対処: {toolchain[0].Text}");
        Check.Equal(1, toolchain.Count, "工程が無ければ工程の数の行は出さない");
        foreach (var kind in Enum.GetValues<AndroidFailureKind>())
        {
            Check.True(AndroidRunOutputFormatter.FailureLabel(kind) != kind.ToString(), $"{kind} の表示名がある");
        }
        Check.True(AndroidRunOutputFormatter.DescribeFailure(new AndroidPipelineResult { FailureMessage = "謎" }).Contains("謎"),
            "種類が無ければ説明をそのまま");
    }

    /// <summary>従来の色分け（OutputPanel.PickBrush / Classify から切り出したもの）。</summary>
    private static void LegacyClassifier()
    {
        Check.Equal(OutputTone.Runtime, OutputLineClassifier.ToneOf("12:00:00.000  [Runtime→Editor] SAVE_ERROR: x"), "ランタイムの通知が最優先（error を含んでも）");
        Check.Equal(OutputTone.Error, OutputLineClassifier.ToneOf("Build ERROR: x"), "error は大文字小文字を問わない");
        Check.Equal(OutputTone.Error, OutputLineClassifier.ToneOf("保存に失敗"), "失敗");
        Check.Equal(OutputTone.Error, OutputLineClassifier.ToneOf("OnPlayPause(Play) EXCEPTION: x"), "EXCEPTION");
        Check.Equal(OutputTone.Default, OutputLineClassifier.ToneOf("an exception occurred"), "exception（小文字）は従来どおり当たらない");
        Check.Equal(OutputTone.Error, OutputLineClassifier.ToneOf("[cargo] error[E0308]"), "エラーはビルドの印より優先");
        Check.Equal(OutputTone.Build, OutputLineClassifier.ToneOf("[cargo]    Compiling seed"), "[cargo]");
        Check.Equal(OutputTone.Build, OutputLineClassifier.ToneOf("state BUILDING"), "BUILDING");
        Check.Equal(OutputTone.Build, OutputLineClassifier.ToneOf("RuntimeManager.BuildAsync start"), "BuildAsync");
        Check.Equal(OutputTone.Default, OutputLineClassifier.ToneOf("[STDOUT] hello"), "それ以外は灰");
        Check.Equal(OutputSource.Game, OutputLineClassifier.SourceOf("[STDERR] [Script] hi"), "[Script] はゲーム");
        Check.Equal(OutputSource.Game, OutputLineClassifier.SourceOf("[ScriptCompileError] x"), "[Script… はゲーム");
        Check.Equal(OutputSource.Engine, OutputLineClassifier.SourceOf("[SEED INIT] x"), "それ以外はエンジン");
        Check.True(OutputLineClassifier.LooksLikeError("gradle: error: x") && !OutputLineClassifier.LooksLikeError("[cargo] ok"), "エラーらしさ");
    }
}
