// ============================================================
//  MergeEditorWindows.cs — マージエディタを「1 ファイルにつき 1 つ」開く窓口
//
//  【なぜ窓口が要るのか】
//  1. 同じファイルのマージエディタを 2 つ開くと、片方で確定した後にもう片方が
//     古い解析結果のまま上書きし得る（＝解決したはずの内容が巻き戻る）。
//     開いているものがあれば前面に出すだけにする。
//  2. ヘッドレス起動でも **窓は出す**。非モーダル（Show）なので UI スレッドを
//     止めず、自動操作（UI Automation / MCP）からの実機確認に使える。
//     EditorDialogs が抑止するのは「返事を待って止まる」モーダルだけで、
//     この窓はその対象ではない。開いたことはログに残す。
//  3. 開けない理由（バイナリ・印が無い・印が壊れている）は、窓を出さずに
//     その場で利用者へ伝える。ここが唯一の判断箇所。
//
//  【表示名の決め方】
//  「取り込み元 / 現在」の呼び名は出どころで変わる。推測を画面側に持ち込まないよう、
//  ここで 1 か所に決める（<see cref="IncomingName"/> / <see cref="CurrentName"/>）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using SEEDEditor.Headless;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Merge;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.Panels.VersionControl.MergeEditor;

/// <summary>
/// マージエディタの開閉をまとめる窓口。
/// </summary>
public static class MergeEditorWindows
{
    /// <summary>ヘッドレス時にログへ付ける接頭辞（自動操作の記録から「開いた」ことが分かるように）。</summary>
    private const string LOG_PREFIX = "[ヘッドレス:マージエディタ表示]";

    /// <summary>いま開いているウィンドウ（絶対パス → ウィンドウ）。</summary>
    private static readonly Dictionary<string, MergeEditorWindow> Opened =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// マージエディタを開く（既に開いていれば前面に出すだけ）。
    /// </summary>
    /// <param name="absolutePath">競合したファイルの絶対パス。</param>
    /// <param name="context">進行中のマージの向き（出どころと取り込み元のブランチ名）。</param>
    /// <param name="currentBranch">現在のブランチ名（ブランチのマージのときだけ使う）。</param>
    /// <param name="owner">親ウィンドウ（中央に出すために使う）。</param>
    /// <param name="onCommit">確定したときに呼ぶ処理。</param>
    /// <param name="reason">開けなかった理由（開けたときは空文字）。</param>
    /// <returns>開けたか（既に開いていて前面に出した場合も真）。</returns>
    public static bool TryOpen(
        string absolutePath,
        MergeContext context,
        string currentBranch,
        Window? owner,
        MergeEditorCommitHandler onCommit,
        out string reason)
    {
        reason = string.Empty;

        // ヘッドレスでも窓は出す（非モーダルなので止まらない）。記録だけ残す。
        if (EditorStartupOptions.IsHeadless)
        {
            EditorLog.Write($"{LOG_PREFIX} {absolutePath}");
        }

        if (Opened.TryGetValue(absolutePath, out var existing))
        {
            // 同じファイルを 2 つ開かない（後から確定した方が前の結果を巻き戻す）。
            existing.Activate();
            return true;
        }

        try
        {
            var window = new MergeEditorWindow(
                absolutePath,
                context.Origin,
                IncomingName(context),
                CurrentName(context, currentBranch),
                onCommit)
            {
                Owner = owner ?? Application.Current?.MainWindow,
            };

            var key = absolutePath;
            window.Closed += (_, _) => Opened.Remove(key);
            Opened[key] = window;

            window.Show();
            return true;
        }
        catch (MergeParseException ex)
        {
            // 中身を並べられない（バイナリ・印が無い・印が壊れている）。
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 開けなかったことを利用者へ伝える（ヘッドレスではログだけになる）。
    /// </summary>
    /// <param name="reason">開けなかった理由。</param>
    public static void ReportUnavailable(string reason)
        => EditorDialogs.Show(
            reason,
            VersionControlMessages.MERGE_EDITOR_UNAVAILABLE_TITLE,
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    /// <summary>
    /// 「取り込み元」の呼び名を決める。
    /// sync ならリモート、ブランチのマージなら取り込み元のブランチ名。
    /// </summary>
    /// <param name="context">進行中のマージの向き。</param>
    public static string IncomingName(MergeContext context)
        => context.Origin == MergeOrigin.BranchMerge && context.SourceBranch.Length > 0
            ? string.Format(
                VersionControlMessages.MERGE_EDITOR_SOURCE_BRANCH_FORMAT, context.SourceBranch)
            : VersionControlMessages.MERGE_EDITOR_SOURCE_REMOTE;

    /// <summary>
    /// 「現在」の呼び名を決める。
    /// sync なら自分の変更、ブランチのマージなら取り込み先＝現在のブランチ名。
    /// </summary>
    /// <param name="context">進行中のマージの向き。</param>
    /// <param name="currentBranch">現在のブランチ名（分からなければ空文字）。</param>
    public static string CurrentName(MergeContext context, string? currentBranch)
        => context.Origin == MergeOrigin.BranchMerge && !string.IsNullOrEmpty(currentBranch)
            ? string.Format(
                VersionControlMessages.MERGE_EDITOR_SOURCE_BRANCH_FORMAT, currentBranch)
            : VersionControlMessages.MERGE_EDITOR_SOURCE_LOCAL;
}
