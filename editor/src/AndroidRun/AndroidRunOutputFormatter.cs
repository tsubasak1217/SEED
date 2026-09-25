// ============================================================
//  AndroidRunOutputFormatter.cs — Android の実行のイベントを Output パネルの行（本文＋色＋出どころ）にする（純粋な処理）
//
//  【書式】（色の規約の正典は docs/editor_ui_style.md 7 章。PC の実行の [cargo]・[Runtime→Editor] と同じ色分け）
//    [Android] 実行を始めます: Pixel_6a（実機）…                       … 実行先からの通知（水色）
//    [Android] [準備] 道具・プロジェクト・端末を確かめ、実行計画を立てます … 工程の見出し（黄）
//    [Android]     準備完了: 2 工程を行い、5 工程を飛ばします（0.3 秒）  … 工程の結果（灰）
//    [Android] [1/7] libSEED.so のビルド（cargo ndk） — 変更なし          … 飛ばした工程は 1 行（灰）
//    [Android] [4/7] APK の作成（Gradle） — 上流の工程…を作り直すため     … 工程の見出し（黄）
//    [Android]     > Task :app:assembleDebug                              … 子プロセスの出力（黄。エラーらしい行は赤）
//    [Android]     失敗: gradlew assembleDebug が失敗しました（12.3 秒）   … 失敗（赤）
//    [logcat] I/SEED: [SEED INIT] …                                     … logcat（重要度 E/F は赤・W は黄・他は灰）
//    [logcat] I/DOTNET: [Script] …                                      … スクリプトのログ（出どころ＝ゲーム）
//  進み具合（AndroidProgressChanged）は行にしない（ツールバーの小さな表示へ出す。PlayBarPolicy）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SEEDEditor.Android;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Logging;

namespace SEEDEditor.AndroidRun;

/// <summary>Output パネルへ出す 1 行。</summary>
/// <param name="Text">本文（Output パネルでは前に時刻が付く）。</param>
/// <param name="Style">色と出どころ。</param>
public sealed record AndroidRunOutputLine(string Text, OutputLineStyle Style);

/// <summary>Android の実行のイベントを Output パネルの行にする。</summary>
public static class AndroidRunOutputFormatter
{
    // ── 行の印と字下げ ──────────────────────────────────

    /// <summary>Android の実行の行の印（PC の実行の行と見分けるため）。</summary>
    public const string Prefix = "[Android]";

    /// <summary>logcat の行の印。</summary>
    public const string LogcatPrefix = "[logcat]";

    /// <summary>工程の中の行（結果・説明・子プロセスの出力）の字下げ。</summary>
    private const string DetailIndent = "    ";

    /// <summary>複数行の本文の 2 行目以降の字下げ（1 行目の字下げに足す）。</summary>
    private const string ContinuationIndent = "  ";

    /// <summary>準備の工程の番号の代わりの見出し。</summary>
    public const string PrepareLabel = "[準備]";

    /// <summary>工程の番号の書式（{0}=何番目、{1}=工程の数）。</summary>
    private const string StepLabelFormat = "[{0}/{1}]";

    /// <summary>秒数の書式（小数 1 桁）。</summary>
    private const string SecondsFormat = "F1";

    /// <summary>C# スクリプトのログのタグ（CoreCLR の Android 版 Console。docs/android.md §6）。これの行は出どころ＝ゲーム。</summary>
    public const string ScriptLogTag = "DOTNET";

    // ── 文言 ───────────────────────────────────────────

    /// <summary>準備の工程の見出しの説明。</summary>
    private const string PrepareDescription = "道具・プロジェクト・端末を確かめ、実行計画を立てます";

    /// <summary>飛ばした工程（入力も出力も前回と同じ）の書き方。</summary>
    public const string UnchangedText = "変更なし";

    /// <summary>それ以外の理由で飛ばした工程の書式（{0}=理由）。</summary>
    private const string SkippedFormat = "飛ばしました（{0}）";

    /// <summary>工程を終えた行の書式（{0}=一行の結果、{1}=秒数）。</summary>
    private const string SucceededFormat = "完了: {0}（{1} 秒）";

    /// <summary>準備を終えた行の書式（{0}=一行の結果、{1}=秒数）。</summary>
    private const string PreparedFormat = "準備完了: {0}（{1} 秒）";

    /// <summary>工程が失敗した行の書式（{0}=理由、{1}=秒数）。</summary>
    private const string FailedFormat = "失敗: {0}（{1} 秒）";

    /// <summary>工程を中断した行の書式（{0}=秒数）。</summary>
    private const string CanceledFormat = "中断しました（{0} 秒）";

    /// <summary>警告の行の書式（{0}=本文）。</summary>
    private const string WarningFormat = "警告: {0}";

    /// <summary>失敗の種類の行の書式（{0}=種類、{1}=説明）。</summary>
    private const string PipelineErrorFormat = "エラー（{0}）: {1}";

    /// <summary>準備で決まった値の行の書式（{0}=端末、{1}=ABI、{2}=アプリ ID）。</summary>
    private const string PreparedTargetFormat = "端末: {0}・ABI: {1}・アプリ ID: {2}";

    /// <summary>端末の説明の書式（{0}=シリアル、{1}=種類、{2}=機種）。</summary>
    private const string DeviceFormat = "{0}（{1}・{2}）";

    /// <summary>端末が無いときの端末の説明。</summary>
    private const string NoDeviceText = "なし（ビルドだけ）";

    /// <summary>機種が分からないときの表記。</summary>
    private const string UnknownModel = "機種不明";

    /// <summary>行が 1 つも無いときの戻り値。</summary>
    private static readonly IReadOnlyList<AndroidRunOutputLine> NoLines = Array.Empty<AndroidRunOutputLine>();

    /// <summary>失敗の種類ごとの表示名と、何を確かめればよいか。</summary>
    private static readonly IReadOnlyDictionary<AndroidFailureKind, (string Label, string Hint)> FailureTexts =
        new Dictionary<AndroidFailureKind, (string Label, string Hint)>
        {
            [AndroidFailureKind.InvalidRequest]  = ("指定の誤り", "プロジェクト設定（Android アプリ情報・画面の向き）と実行の指定を確認してください。"),
            [AndroidFailureKind.Toolchain]       = ("道具", "Android SDK・NDK・JDK・cargo（cargo-ndk）・dotnet の場所を確認してください（docs/android.md §3）。"),
            [AndroidFailureKind.Device]          = ("端末", "端末の接続と状態（USB デバッグの許可・エミュレータの起動）を確認し、実行先を選び直してください。"),
            [AndroidFailureKind.Build]           = ("ビルド", "上のビルドの出力を確認してください。"),
            [AndroidFailureKind.DeviceOperation] = ("端末の操作", "インストール・起動・転送に失敗しました。端末の接続と空き容量を確認してください。"),
        };

    // ── パイプラインのイベント ──────────────────────────────

    /// <summary>
    /// パイプラインのイベントを行にする（進み具合のイベントは行にしない）。
    /// </summary>
    /// <param name="pipelineEvent">イベント。</param>
    /// <returns>行（0 行以上）。</returns>
    public static IReadOnlyList<AndroidRunOutputLine> Format(AndroidPipelineEvent pipelineEvent) => pipelineEvent switch
    {
        AndroidPrepared prepared   => Lines(OutputLineStyle.Engine(OutputTone.Runtime), $"{Prefix} ", DescribePrepared(prepared)),
        AndroidPhaseStarted started => FormatStarted(started),
        AndroidPhaseFinished finished => FormatFinished(finished),
        AndroidLogLine line        => FormatLogLine(line),
        AndroidPipelineError error => Lines(OutputLineStyle.Engine(OutputTone.Error), $"{Prefix} ",
            string.Format(PipelineErrorFormat, FailureLabel(error.Kind), error.Message)),
        _ => NoLines,
    };

    /// <summary>工程を始めた行（見出し）。</summary>
    private static IReadOnlyList<AndroidRunOutputLine> FormatStarted(AndroidPhaseStarted started)
    {
        var heading = started.Phase == AndroidPipelinePhase.Prepare
            ? $"{PrepareLabel} {PrepareDescription}"
            : $"{StepLabel(started.Index, started.Count)} {started.Title} — {started.Reason}";
        return Lines(OutputLineStyle.Engine(OutputTone.Build), $"{Prefix} ", heading);
    }

    /// <summary>工程を終えた行（飛ばした工程は見出しを兼ねた 1 行）。</summary>
    private static IReadOnlyList<AndroidRunOutputLine> FormatFinished(AndroidPhaseFinished finished)
    {
        var seconds = Seconds(finished.Elapsed);
        var detailPrefix = $"{Prefix} {DetailIndent}";
        switch (finished.Outcome)
        {
            case AndroidPhaseOutcome.Skipped:
                return Lines(OutputLineStyle.Engine(OutputTone.Default), $"{Prefix} ",
                    $"{StepLabel(finished.Index, finished.Count)} {finished.Title} — {DescribeSkip(finished.Summary)}");
            case AndroidPhaseOutcome.Succeeded:
                var format = finished.Phase == AndroidPipelinePhase.Prepare ? PreparedFormat : SucceededFormat;
                return Lines(OutputLineStyle.Engine(OutputTone.Default), detailPrefix, string.Format(format, finished.Summary, seconds));
            case AndroidPhaseOutcome.Canceled:
                return Lines(OutputLineStyle.Engine(OutputTone.Warning), detailPrefix, string.Format(CanceledFormat, seconds));
            default:
                return Lines(OutputLineStyle.Engine(OutputTone.Error), detailPrefix, string.Format(FailedFormat, finished.Summary, seconds));
        }
    }

    /// <summary>
    /// 飛ばした理由の書き方（入力も出力も前回と同じなら「変更なし」、それ以外は理由を添える）。
    /// </summary>
    /// <param name="reason">計画の理由。</param>
    /// <returns>書き方。</returns>
    public static string DescribeSkip(string reason) =>
        reason == AndroidBuildPlan.UnchangedReason ? UnchangedText : string.Format(SkippedFormat, reason);

    /// <summary>1 行のログ。</summary>
    private static IReadOnlyList<AndroidRunOutputLine> FormatLogLine(AndroidLogLine line)
    {
        var detailPrefix = $"{Prefix} {DetailIndent}";
        return line.Level switch
        {
            AndroidLogLevel.Logcat  => new[] { FormatLogcat(line.Text) },
            AndroidLogLevel.Warning => Lines(OutputLineStyle.Engine(OutputTone.Warning), detailPrefix, string.Format(WarningFormat, line.Text)),
            AndroidLogLevel.Error   => Lines(OutputLineStyle.Engine(OutputTone.Error), detailPrefix, line.Text),
            AndroidLogLevel.ProcessOutput or AndroidLogLevel.ProcessError =>
                // 子プロセス（cargo・Gradle・SeedPak・adb）の出力は PC の [cargo] と同じビルドの色。エラーらしい行だけ赤
                Lines(OutputLineStyle.Engine(OutputLineClassifier.LooksLikeError(line.Text) ? OutputTone.Error : OutputTone.Build),
                    detailPrefix, line.Text),
            _ => Lines(OutputLineStyle.Engine(OutputTone.Default), detailPrefix, line.Text),
        };
    }

    /// <summary>
    /// logcat の 1 行（重要度・タグ・本文に分けて「[logcat] I/SEED: 本文」の形にする。形に合わなければそのまま）。
    /// </summary>
    /// <param name="text">logcat の 1 行（-v threadtime）。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine FormatLogcat(string text)
    {
        var parsed = LogcatLineParser.Parse(text);
        if (parsed is null)
        {
            // 「--------- beginning of main」等の区切り
            return new AndroidRunOutputLine($"{LogcatPrefix} {text.TrimEnd('\r')}", OutputLineStyle.Engine(OutputTone.Default));
        }

        var source = string.Equals(parsed.Tag, ScriptLogTag, StringComparison.Ordinal)
                     || parsed.Message.Contains(OutputLineClassifier.GameMarker, StringComparison.Ordinal)
            ? OutputSource.Game
            : OutputSource.Engine;
        var tone = parsed.IsError ? OutputTone.Error
            : parsed.IsWarning ? OutputTone.Warning
            : OutputLineClassifier.LooksLikeError(parsed.Message) ? OutputTone.Error
            : OutputTone.Default;
        return new AndroidRunOutputLine($"{LogcatPrefix} {parsed.Level}/{parsed.Tag}: {parsed.Message}", new OutputLineStyle(tone, source));
    }

    // ── 実行の段取り（AndroidRunController が出す行）──────────────────

    /// <summary>実行を始めた行。</summary>
    /// <param name="targetText">実行先の表示名。</param>
    /// <param name="projectDir">プロジェクト。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine Started(string targetText, string? projectDir) =>
        Notice(OutputTone.Runtime, $"実行を始めます: {targetText}（プロジェクト {projectDir ?? "なし"}）。ビルド → インストール → 起動 → logcat");

    /// <summary>アプリを起動した行。</summary>
    /// <param name="targetText">実行先の表示名。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine Launched(string? targetText) =>
        Notice(OutputTone.Runtime, $"{targetText} でアプリが動いています。logcat を流します（停止ボタンでアプリを止めます）。");

    /// <summary>止め始めた行。</summary>
    /// <param name="reason">止める理由。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine Stopping(AndroidRunStopReason reason) => reason switch
    {
        AndroidRunStopReason.AppExited => Notice(OutputTone.Runtime, "端末でアプリのプロセスが見つからなくなりました。logcat を止めます…"),
        _ => Notice(OutputTone.Runtime, "停止しています…（ビルド中なら子プロセスの終了を待ちます）"),
    };

    /// <summary>未保存の変更を保存せずに実行する警告（APK はディスク上のファイルから作る）。</summary>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine UnsavedChangesWarning() =>
        Notice(OutputTone.Warning, "警告: 保存せずに実行します。APK はディスク上のファイルから作るため、保存していない変更は Android の実行に含まれません。");

    /// <summary>保存してから実行する行（保存が終わると実行を始める。段階C-3）。</summary>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine SavingBeforeRun() =>
        Notice(OutputTone.Runtime, "保存してから Android で実行します（保存が終わると実行を始めます）…");

    /// <summary>保存に失敗して実行を取りやめた行。</summary>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine SaveFailedRunCanceled() =>
        Notice(OutputTone.Warning, "保存に失敗したため、Android の実行を取りやめました。");

    /// <summary>保存を始められず（読み取り専用・ロック・名前を付けて保存の取り消し等）実行を取りやめた行。</summary>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine SaveNotStartedRunCanceled() =>
        Notice(OutputTone.Warning, "保存しなかった（読み取り専用・ロック・名前を付けて保存の取り消し等）ため、Android の実行を取りやめました。");

    /// <summary>未保存の変更の確認でキャンセルした行。</summary>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine UnsavedPromptCanceled() =>
        Notice(OutputTone.Default, "Android の実行を取りやめました（未保存の変更の確認でキャンセル）。");

    /// <summary>
    /// 起動するシーンの行（開いているシーン・開始シーンと、開始シーンにした理由。段階C-3）。
    /// </summary>
    /// <param name="choice">起動するシーン。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine SceneChosen(AndroidRunSceneChoice choice) => choice.Source switch
    {
        AndroidRunSceneSource.OpenScene =>
            Notice(OutputTone.Runtime, $"起動するシーン: {choice.ScenePath}（開いているシーン。PC の Play と同じ）"),
        AndroidRunSceneSource.StartSceneByOption =>
            Notice(OutputTone.Runtime, "起動するシーン: 開始シーン（「開始シーンからプレイ」がオン）"),
        AndroidRunSceneSource.StartSceneUnsaved =>
            Notice(OutputTone.Warning, "開いているシーンはまだファイルに保存されていないため、開始シーンで起動します。"),
        _ => Notice(OutputTone.Warning, $"開いているシーンを端末で開けないため、開始シーンで起動します: {choice.Detail}"),
    };

    /// <summary>実行を始められなかった行（動いている途中など）。</summary>
    /// <param name="reason">理由。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine NotStarted(string reason) => Notice(OutputTone.Warning, $"実行を始めませんでした: {reason}");

    /// <summary>
    /// 実行を終えた行（終わり方の 1 行と、工程の数・所要時間の 1 行）。
    /// </summary>
    /// <param name="completion">終わり方。</param>
    /// <param name="stopAppError">アプリを止められなかった理由（止めた・止めなかったなら null）。</param>
    /// <returns>行。</returns>
    public static IReadOnlyList<AndroidRunOutputLine> Completed(AndroidRunCompletion completion, string? stopAppError)
    {
        var lines = new List<AndroidRunOutputLine> { DescribeOutcome(completion, stopAppError) };
        var steps = completion.Result.Steps.Where(step => step.Phase != AndroidPipelinePhase.Prepare).ToList();
        if (steps.Count > 0)
        {
            var ran = steps.Count(step => step.Outcome == AndroidPhaseOutcome.Succeeded);
            var skipped = steps.Count(step => step.Outcome == AndroidPhaseOutcome.Skipped);
            lines.Add(Notice(OutputTone.Default,
                $"工程: {ran} を行い、{skipped} を飛ばしました（始めてから {Seconds(completion.Result.Elapsed)} 秒）"));
        }
        return lines;
    }

    /// <summary>終わり方の 1 行。</summary>
    private static AndroidRunOutputLine DescribeOutcome(AndroidRunCompletion completion, string? stopAppError) => completion.Outcome switch
    {
        AndroidRunOutcome.StoppedByUser when stopAppError is not null =>
            Notice(OutputTone.Error, $"logcat は止めましたが、端末のアプリを止められませんでした: {stopAppError}"),
        AndroidRunOutcome.StoppedByUser => Notice(OutputTone.Runtime, "停止しました（端末のアプリを止めました）。"),
        AndroidRunOutcome.BuildCanceled => Notice(OutputTone.Runtime, "ビルドを中止しました（端末のアプリには触っていません）。"),
        AndroidRunOutcome.AppExited => Notice(OutputTone.Runtime, "端末でアプリが終わったので実行を終えました（戻るキー・クラッシュ等。直前の logcat を確認してください）。"),
        AndroidRunOutcome.LogcatEnded => Notice(OutputTone.Warning, "logcat が終わりました（端末が外れた・adb が終了した等）。実行を終えます。"),
        AndroidRunOutcome.Canceled => Notice(OutputTone.Warning, "中断しました。"),
        _ => Notice(OutputTone.Error, DescribeFailure(completion.Result)),
    };

    /// <summary>失敗の 1 行（種類と、何を確かめればよいか）。</summary>
    /// <param name="result">パイプラインの結果。</param>
    /// <returns>本文。</returns>
    public static string DescribeFailure(AndroidPipelineResult result)
    {
        if (result.FailureKind is { } kind && FailureTexts.TryGetValue(kind, out var texts))
        {
            return $"実行できませんでした（{texts.Label}）: {texts.Hint}";
        }
        return $"実行できませんでした: {result.FailureMessage ?? "理由が分かりません"}";
    }

    /// <summary>失敗の種類の表示名。</summary>
    /// <param name="kind">種類。</param>
    /// <returns>表示名。</returns>
    public static string FailureLabel(AndroidFailureKind kind) =>
        FailureTexts.TryGetValue(kind, out var texts) ? texts.Label : kind.ToString();

    // ── 部品 ───────────────────────────────────────────

    /// <summary>準備で決まった値の説明。</summary>
    private static string DescribePrepared(AndroidPrepared prepared)
    {
        var device = prepared.Device is { } d
            ? string.Format(DeviceFormat, d.Serial, d.KindLabel, d.Model ?? UnknownModel)
            : NoDeviceText;
        return string.Format(PreparedTargetFormat, device, AndroidAbis.Describe(prepared.Abis), prepared.Identity.ApplicationId);
    }

    /// <summary>工程の番号の見出し（準備は [準備]）。</summary>
    private static string StepLabel(int index, int count) =>
        index <= 0 ? PrepareLabel : string.Format(CultureInfo.InvariantCulture, StepLabelFormat, index, count);

    /// <summary>秒数（小数 1 桁）。</summary>
    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString(SecondsFormat, CultureInfo.InvariantCulture);

    /// <summary>段取りの行（印付き・字下げなし）。</summary>
    private static AndroidRunOutputLine Notice(OutputTone tone, string text) =>
        new($"{Prefix} {text}", OutputLineStyle.Engine(tone));

    /// <summary>
    /// 本文を行にする（本文が複数行なら行ごとに分け、2 行目以降は字下げを足す）。
    /// </summary>
    /// <param name="style">見た目。</param>
    /// <param name="prefix">1 行目の前に付けるもの（印と字下げ）。</param>
    /// <param name="text">本文。</param>
    /// <returns>行。</returns>
    private static IReadOnlyList<AndroidRunOutputLine> Lines(OutputLineStyle style, string prefix, string text)
    {
        var parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lines = new AndroidRunOutputLine[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            lines[i] = new AndroidRunOutputLine(i == 0 ? prefix + parts[i] : prefix + ContinuationIndent + parts[i], style);
        }
        return lines;
    }
}
