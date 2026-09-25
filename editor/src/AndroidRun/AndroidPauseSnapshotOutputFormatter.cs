// ============================================================
//  AndroidPauseSnapshotOutputFormatter.cs — 一時停止中の端末のシーンの写し（取り出し・シーンパネルへの表示・戻し）の
//                                           Output パネルの行（純粋な処理。docs/android.md §20.17）
//
//  【書式】（色の規約の正典は docs/editor_ui_style.md 7 章。Android の実行の行と同じ [Android] の印）
//    [Android] 端末のシーンの写しを取り出しています（Pixel_6a（実機））…                                … 水色
//    [Android] 写しを取り出しました: アクタ 123（飛ばした 0）・0.8 MB（端末 35 ms・合計 1.2 秒）。メインカメラ 位置 (…)   … 水色
//    [Android] 写しをシーンパネルに出しました（閲覧専用・アクタ 123・0.6 秒）。…                        … 水色
//    [Android] 編集中のシーンへ戻しました（0.4 秒）。                                                  … 水色
//    [Android] 端末のシーンの写しを取り出せませんでした（一時停止は続いています）: <理由>                   … 黄
//    [Android] 保存済みのシーンを読み直しました（メモリから戻せなかったため。未保存の変更は戻せませんでした） … 黄
//    [Android] 編集中のシーンへ戻せませんでした: <理由>。…                                              … 赤
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Logging;
using SEEDEditor.SceneSnapshot;

namespace SEEDEditor.AndroidRun;

/// <summary>一時停止中の端末のシーンの写しの Output パネルの行。</summary>
public static class AndroidPauseSnapshotOutputFormatter
{
    /// <summary>1 MB のバイト数（ファイルの大きさの表示）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>秒数の書式（小数 1 桁）。</summary>
    private const string SecondsFormat = "F1";

    /// <summary>大きさ（MB）の書式（小数 2 桁）。</summary>
    private const string MegabytesFormat = "F2";

    /// <summary>端末での所要ミリ秒の書式（整数）。</summary>
    private const string MillisecondsFormat = "F0";

    // ── 取り出し ───────────────────────────────────────────

    /// <summary>取り出し始めの行の書式（{0}=実行先）。</summary>
    private const string FetchingFormat = "一時停止中の端末のシーンを写しとして取り出しています（{0}）…";

    /// <summary>取り出せた行の書式（{0}=アクタ数、{1}=飛ばした数、{2}=MB、{3}=端末のミリ秒、{4}=合計秒、{5}=カメラ）。</summary>
    private const string FetchedFormat = "写しを取り出しました: アクタ {0}（保存できずに飛ばした {1}）・{2} MB（端末 {3} ms・合計 {4} 秒）。{5}";

    /// <summary>メインカメラの説明の書式（{0}=位置と向き）。</summary>
    private const string CameraFormat = "端末のメインカメラ: {0}";

    /// <summary>メインカメラが無いときの説明。</summary>
    private const string NoCameraText = "メインカメラはありません（デバッグカメラは今の位置のまま）";

    /// <summary>飛ばしたアクタがあったときの行の書式（{0}=数）。</summary>
    private const string SkippedWarningFormat =
        "{0} 体のアクタを写しに入れられませんでした（値が数でない・書き出せない等。端末の logcat の [SEED SNAPSHOT] に名前があります）。";

    /// <summary>取り出せなかった行の書式（{0}=理由）。</summary>
    private const string FetchFailedFormat =
        "端末のシーンの写しを取り出せませんでした（一時停止は続いています。シーンパネルは案内のままです）: {0}";

    // ── シーンパネルへの表示・戻し ─────────────────────────────

    /// <summary>表示した行の書式（{0}=アクタ数、{1}=秒、{2}=カメラの説明）。</summary>
    private const string ViewShownFormat =
        "写しをシーンパネルに出しました（閲覧専用・アクタ {0}・{1} 秒）。{2}編集中のシーンは退避してあり、再開・停止で戻ります。";

    /// <summary>カメラを合わせた旨。</summary>
    private const string CameraAppliedText = "デバッグカメラは端末のメインカメラの位置から動かせます。";

    /// <summary>表示できなかった行の書式（{0}=理由）。</summary>
    private const string ViewFailedFormat = "写しをシーンパネルに出せませんでした（シーンパネルは案内のままです）: {0}";

    /// <summary>退避できず、保存もしなかったので出さない行。</summary>
    public const string ViewSkippedUnsavedText =
        "編集中のシーンを退避できず、保存もしなかったため、写しは出しません（未保存の変更を守るため）。";

    /// <summary>出せる状態でない行の書式（{0}=理由）。</summary>
    private const string ViewUnavailableFormat = "写しをシーンパネルに出しません: {0}";

    /// <summary>メモリから戻した行の書式（{0}=秒）。</summary>
    private const string RestoredFromMemoryFormat = "写しの表示をやめ、編集中のシーンへ戻しました（{0} 秒）。";

    /// <summary>ファイルを読み直して戻した行の書式（{0}=シーン、{1}=秒）。</summary>
    private const string RestoredFromFileFormat =
        "写しの表示をやめ、保存済みのシーン {0} を読み直しました（メモリへ退避した状態を戻せなかったため。未保存の変更は戻せませんでした・{1} 秒）。";

    /// <summary>戻せなかった行の書式（{0}=理由）。</summary>
    private const string RestoreFailedFormat =
        "編集中のシーンへ戻せませんでした: {0}。シーンを開き直してください（写しの内容で元のシーンを上書きすることはありません）。";

    /// <summary>編集用ランタイムが終わって退避が失われた行。</summary>
    public const string EditRuntimeLostText =
        "写しを出している間にエディタの編集用ランタイムが終わったため、退避していた未保存の変更は戻せませんでした（ランタイムは保存済みのシーンで起動し直します）。";

    /// <summary>閲覧専用のため命令を捨てた行の書式（{0}=命令の名前）。</summary>
    private const string RefusedFormat = "閲覧専用（端末の写し）のため反映しませんでした: {0}";

    // ── 取り出し ───────────────────────────────────────────

    /// <summary>取り出し始めの行。</summary>
    /// <param name="targetText">実行先の表示名。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine Fetching(string? targetText) =>
        Notice(OutputTone.Runtime, string.Format(FetchingFormat, targetText));

    /// <summary>取り出せた行（飛ばしたアクタがあれば、その旨の警告の行も）。</summary>
    /// <param name="result">取り出した結果。</param>
    /// <returns>行。</returns>
    public static IReadOnlyList<AndroidRunOutputLine> Fetched(AndroidSceneSnapshotResult result)
    {
        var ci = CultureInfo.InvariantCulture;
        var camera = result.Reply.Camera is { } pose ? string.Format(CameraFormat, pose.Describe()) : NoCameraText;
        var lines = new List<AndroidRunOutputLine>
        {
            Notice(OutputTone.Runtime, string.Format(FetchedFormat,
                result.Reply.Actors,
                result.Reply.Skipped,
                (result.Bytes / BytesPerMegabyte).ToString(MegabytesFormat, ci),
                result.Reply.RuntimeMilliseconds.ToString(MillisecondsFormat, ci),
                Seconds(result.Elapsed),
                camera)),
        };
        if (result.Reply.Skipped > 0)
        {
            lines.Add(Notice(OutputTone.Warning, string.Format(SkippedWarningFormat, result.Reply.Skipped)));
        }
        return lines;
    }

    /// <summary>取り出せなかった行。</summary>
    /// <param name="reason">理由。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine FetchFailed(string reason) =>
        Notice(OutputTone.Warning, string.Format(FetchFailedFormat, reason));

    // ── シーンパネルへの表示・戻し ─────────────────────────────

    /// <summary>表示を始めた結果の行（出せた・出せなかった）。</summary>
    /// <param name="result">結果。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine ViewBegun(SceneSnapshotViewBeginResult result) =>
        result.Reply.IsReady
            ? Notice(OutputTone.Runtime, string.Format(ViewShownFormat,
                result.Reply.Actors, Seconds(result.Elapsed), result.CameraApplied ? CameraAppliedText : string.Empty))
            : Notice(OutputTone.Warning, string.Format(ViewFailedFormat, result.Reply.Reason));

    /// <summary>出せる状態でないので出さない行。</summary>
    /// <param name="reason">理由。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine ViewUnavailable(string reason) =>
        Notice(OutputTone.Warning, string.Format(ViewUnavailableFormat, reason));

    /// <summary>退避できず保存もしなかったので出さない行。</summary>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine ViewSkippedUnsaved() => Notice(OutputTone.Warning, ViewSkippedUnsavedText);

    /// <summary>表示をやめた結果の行（戻し方ごと）。表示していなかったなら null。</summary>
    /// <param name="result">結果。</param>
    /// <returns>行（出さないなら null）。</returns>
    public static AndroidRunOutputLine? ViewEnded(SceneSnapshotViewEndResult result) => result.Reply.Restore switch
    {
        SceneSnapshotRestoreKind.Memory => Notice(OutputTone.Runtime, string.Format(RestoredFromMemoryFormat, Seconds(result.Elapsed))),
        SceneSnapshotRestoreKind.File => Notice(OutputTone.Warning,
            string.Format(RestoredFromFileFormat, result.Reply.Detail, Seconds(result.Elapsed))),
        SceneSnapshotRestoreKind.Failed => Notice(OutputTone.Error, string.Format(RestoreFailedFormat, result.Reply.Detail)),
        _ => null,
    };

    /// <summary>編集用ランタイムが終わって退避が失われた行。</summary>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine EditRuntimeLost() => Notice(OutputTone.Warning, EditRuntimeLostText);

    /// <summary>閲覧専用のため命令を捨てた行。</summary>
    /// <param name="command">命令の名前（ランタイムの SNAPSHOT_VIEW_REFUSED の中身）。</param>
    /// <returns>行。</returns>
    public static AndroidRunOutputLine Refused(string command) => Notice(OutputTone.Warning, string.Format(RefusedFormat, command));

    // ── 部品 ───────────────────────────────────────────

    /// <summary>秒数（小数 1 桁）。</summary>
    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString(SecondsFormat, CultureInfo.InvariantCulture);

    /// <summary>段取りの行（Android の実行の行と同じ印・出どころ）。</summary>
    private static AndroidRunOutputLine Notice(OutputTone tone, string text) =>
        new($"{AndroidRunOutputFormatter.Prefix} {text}", OutputLineStyle.Engine(tone));
}
