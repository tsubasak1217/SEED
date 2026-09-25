// ============================================================
//  AndroidHotReloadOutputFormatter.cs — 実行中の差し替えの開始・結果を Output パネルの行にする（純粋な処理。docs/android.md §23）
//
//  【書式】（色の規約は docs/editor_ui_style.md 7 章。Android の実行の行と同じ [Android] の印）
//    [Android] 差し替え: 2 件の変更（scenes/Main.scene ほか）を端末へ送ります             … 水色（実行先への通知）
//    [Android]     スクリプト: DLL を作り直して送りました（5 ファイル・9.1 MB…・6.2 秒）   … 灰
//    [Android]     アセット: 2 ファイル・1.3 MB を送りました（候補 5・端末と同じ 3・0.4 秒）  … 灰
//    [Android]     反映: scenes/Main.scene — シーンを読み直しました（端末 123.4 ms）         … 水色
//    [Android]     反映できません: fonts/a.ttf — 起動時に 1 回だけ読む…                     … 黄（RELOAD_SKIPPED）
//    [Android]     失敗: models/a.glb — シーンを読めません…                                 … 赤（RELOAD_FAILED・応答なし・転送の失敗）
//    [Android] 差し替え完了（1.2 秒）                                                     … 水色（失敗があれば赤で「失敗がありました」）
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SEEDEditor.Android.HotReload;
using SEEDEditor.Ipc;
using SEEDEditor.Logging;

namespace SEEDEditor.AndroidRun;

/// <summary>実行中の差し替えの行。</summary>
public static class AndroidHotReloadOutputFormatter
{
    /// <summary>工程の中の行の字下げ（Android の実行の行と同じ）。</summary>
    private const string DetailIndent = "    ";

    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>秒数の書式（小数 1 桁）。</summary>
    private const string SecondsFormat = "F1";

    /// <summary>開始の行で名前を出すファイルの数（それより多ければ「ほか」）。</summary>
    private const int NamedFilesInHeader = 1;

    /// <summary>無条件のシーンの読み直しの宛先の表示名。</summary>
    private const string CurrentSceneLabel = "今のシーン";

    /// <summary>スクリプトの宛先の表示名。</summary>
    private const string ScriptsLabel = "スクリプト";

    /// <summary>
    /// 差し替えを始めた行。
    /// </summary>
    /// <param name="changed">変わったファイル（相対パス）。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine Started(IReadOnlyList<string> changed)
    {
        var names = string.Join("・", changed.Take(NamedFilesInHeader));
        var rest = changed.Count > NamedFilesInHeader ? " ほか" : string.Empty;
        return Notice(OutputTone.Runtime, $"差し替え: {changed.Count} 件の変更（{names}{rest}）を端末へ送ります");
    }

    /// <summary>
    /// 端末のアプリとつながっていないので差し替えられない行（変更は捨てる。run で起動し直すと入る）。
    /// </summary>
    /// <param name="changed">変わったファイル（相対パス）。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine NotConnected(IReadOnlyList<string> changed) =>
        Notice(OutputTone.Warning,
            $"差し替えできません（端末のアプリとの通信路がつながっていません。段階D-1 より前の APK・起動の途中など）: " +
            $"{changed.Count} 件の変更は、次の実行（APK を作り直す）で入ります");

    /// <summary>差し替えの途中の説明（SeedPak の出力など）の行（Android の実行の子プロセスの出力と同じ見た目）。</summary>
    /// <param name="text">本文。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine Info(string text) => Detail(OutputTone.Build, $"> {text}");

    /// <summary>差し替えの途中で予期しない失敗があった行。</summary>
    /// <param name="reason">理由。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine Crashed(string reason) =>
        Notice(OutputTone.Error, $"差し替えの途中で予期しないエラー: {reason}");

    /// <summary>
    /// 差し替えの結果の行（スクリプト・アセットの転送、命令ごとの応答、まとめ）。
    /// </summary>
    /// <param name="result">結果。</param>
    /// <returns>行。</returns>
    public static IReadOnlyList<AndroidRunOutputLine> Completed(AndroidHotReloadResult result)
    {
        var lines = new List<AndroidRunOutputLine>();
        if (result.NothingToDo)
        {
            lines.Add(Detail(OutputTone.Default, $"端末に関係する変更はありません（{result.Ignored} 件を飛ばしました）"));
            return lines;
        }

        if (result.ScriptsPushed is { } pushed)
        {
            lines.Add(Detail(OutputTone.Default, $"スクリプト: DLL を作り直して送りました（{pushed}・{Seconds(result.ScriptsElapsed)} 秒）"));
        }

        if (result.Overlay is { } overlay)
        {
            var plan = overlay.Plan;
            lines.Add(plan.ToPush.Count > 0
                ? Detail(OutputTone.Default,
                    $"アセット: {plan.ToPush.Count} ファイル・{plan.PushBytes / BytesPerMegabyte:F1} MB を送りました" +
                    $"（候補 {overlay.CandidateCount}・端末と同じ {plan.Unchanged.Count}・{Seconds(overlay.Elapsed)} 秒）")
                : Detail(OutputTone.Default, $"アセット: 端末と同じなので送るものはありません（候補 {overlay.CandidateCount}）"));
            if (overlay.PakBaselineNote is { } note) lines.Add(Detail(OutputTone.Warning, note));
            lines.AddRange(plan.Unreadable.Select(skip => Detail(OutputTone.Warning, $"読めないため送りません: {skip.Relative}（{skip.Reason}）")));
        }

        lines.AddRange(result.Failures.Select(failure => Detail(OutputTone.Error, $"失敗: {failure}")));
        lines.AddRange(result.Replies.Select(Reply));

        var failed = result.Failures.Count > 0
                     || result.Replies.Any(reply => reply.Outcome is AndroidReloadOutcome.Failed or AndroidReloadOutcome.NoReply);
        lines.Add(failed
            ? Notice(OutputTone.Error, $"差し替えで失敗がありました（{Seconds(result.Elapsed)} 秒。上の行を確認してください）")
            : Notice(OutputTone.Runtime, $"差し替え完了（{Seconds(result.Elapsed)} 秒）"));
        return lines;
    }

    /// <summary>命令 1 件の応答の行。</summary>
    /// <param name="reply">応答。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine Reply(AndroidReloadReply reply)
    {
        var target = TargetLabel(reply.Target);
        return reply.Outcome switch
        {
            AndroidReloadOutcome.Done => Detail(OutputTone.Runtime, $"反映: {target} — {DoneDetail(reply)}"),
            AndroidReloadOutcome.Skipped => Detail(OutputTone.Warning, $"反映しませんでした: {target} — {reply.Detail}"),
            _ => Detail(OutputTone.Error, $"失敗: {target} — {reply.Detail}"),
        };
    }

    /// <summary>適用した応答の説明（詳細の種類ごと）。</summary>
    private static string DoneDetail(AndroidReloadReply reply)
    {
        var elapsed = reply.ElapsedMs is { } ms
            ? string.Format(CultureInfo.InvariantCulture, "（端末 {0:F1} ms）", ms)
            : string.Empty;
        if (reply.Target == AndroidReloadReplies.ScriptsTarget) return $"スクリプトを読み直しました（{reply.Detail}）";
        if (reply.Detail == AndroidReloadReplies.InPlaceDetail) return $"キャッシュを捨てて差し替えました{elapsed}";
        if (reply.Detail.StartsWith(AndroidReloadReplies.SceneDetailPrefix, StringComparison.Ordinal))
        {
            return $"シーンを読み直しました（{reply.Detail[AndroidReloadReplies.SceneDetailPrefix.Length..]}）{elapsed}";
        }
        return $"{reply.Detail}{elapsed}";
    }

    /// <summary>宛先の表示名（asset:{p} / scene:{p} → {p}、scene → 今のシーン、scripts → スクリプト）。</summary>
    public static string TargetLabel(string target)
    {
        if (target == AndroidReloadReplies.ScriptsTarget) return ScriptsLabel;
        if (target == RuntimeIpcCommands.ReloadSceneTarget) return CurrentSceneLabel;
        foreach (var prefix in new[] { RuntimeIpcCommands.ReloadAssetTargetPrefix, RuntimeIpcCommands.ReloadSceneTargetPrefix })
        {
            if (target.StartsWith(prefix, StringComparison.Ordinal)) return target[prefix.Length..];
        }
        return target;
    }

    /// <summary>秒数（小数 1 桁。無ければ 0）。</summary>
    private static string Seconds(TimeSpan? elapsed) =>
        (elapsed ?? TimeSpan.Zero).TotalSeconds.ToString(SecondsFormat, CultureInfo.InvariantCulture);

    /// <summary>[Android] の行。</summary>
    private static AndroidRunOutputLine Notice(OutputTone tone, string text) =>
        new($"{AndroidRunOutputFormatter.Prefix} {text}", OutputLineStyle.Engine(tone));

    /// <summary>[Android] の字下げした行。</summary>
    private static AndroidRunOutputLine Detail(OutputTone tone, string text) =>
        new($"{AndroidRunOutputFormatter.Prefix} {DetailIndent}{text}", OutputLineStyle.Engine(tone));
}
