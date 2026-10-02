// ============================================================
//  RuntimeManager.ScreenPreview.cs — 画面プレビューの知らせの振り分け（RuntimeManager の部分クラス）
//
//  【役割】
//  ランタイムから届く画面プレビュー（保存されないプレビュー。docs/editor_screen_preview.md）の行を読み、
//  イベントとして上げる。書式の解釈は WPF 非依存の Preview/ScreenPreviewIpc。
//    HIERARCHY_QUIET                  → HierarchyQuietAnnounced（直後の HIERARCHY 1 通は未保存の印を付けない）
//    PREVIEW_ADDED:{wl},{root_dfs}    → PreviewAdded
//    PREVIEW_CLEARED:{count}          → PreviewCleared
//    PREVIEW_REFRESHED:{count},{path} → PreviewRefreshed
//    PREVIEW_ERROR:{message}          → PreviewError
//
//  【スレッド】
//  どれもパイプの受信スレッドから**同期的に**上げる（Dispatcher を通さない）。
//  とくに HIERARCHY_QUIET は、直後に届く HIERARCHY の未保存の判定（MainWindow.MarkDirtyFromHierarchy。
//  同じ受信スレッドで HierarchyUpdated から呼ばれる）より先に数を足さなければならないので、
//  HIERARCHY_RESET と同じ形で同じスレッド・同じ順に上げる。UI に触る受け手は自分で Dispatcher へ渡す。
// ============================================================

using System;
using SEEDEditor.Preview;

namespace SEEDEditor.Runtime;

public sealed partial class RuntimeManager
{
    /// <summary>ログの行の頭（Output パネルで探しやすくする）。</summary>
    private const string ScreenPreviewLogPrefix = "[Runtime→Editor]";

    /// <summary>
    /// 直後の HIERARCHY 1 通は未保存の印を付けない、という知らせ（HIERARCHY_QUIET）。
    /// 受信スレッドから同期的に上がる（直後の HierarchyUpdated より必ず先）。
    /// </summary>
    public event Action? HierarchyQuietAnnounced;

    /// <summary>プレビューを作った（PREVIEW_ADDED）。引数は (世界線, 根の DFS 番号)。受信スレッドから上がる。</summary>
    public event Action<uint, int>? PreviewAdded;

    /// <summary>プレビューを消した（PREVIEW_CLEARED）。引数は消した数（0 もある）。受信スレッドから上がる。</summary>
    public event Action<int>? PreviewCleared;

    /// <summary>プレビューを作り直した（PREVIEW_REFRESHED）。引数は (作り直した数, 送ったパス)。受信スレッドから上がる。</summary>
    public event Action<int, string>? PreviewRefreshed;

    /// <summary>プレビューの命令の失敗・拒否（PREVIEW_ERROR）。引数は理由。受信スレッドから上がる。</summary>
    public event Action<string>? PreviewError;

    /// <summary>
    /// 画面プレビューの行なら読んでイベントを上げる（OnPipeMessage の振り分けから呼ぶ）。
    /// </summary>
    /// <param name="msg">受け取った 1 行。</param>
    /// <returns>画面プレビューの行として処理したら true（ほかの分岐へ回さない）。</returns>
    private bool TryHandleScreenPreviewMessage(string msg)
    {
        if (ScreenPreviewIpc.IsHierarchyQuiet(msg))
        {
            EditorLog.Write($"{ScreenPreviewLogPrefix} {ScreenPreviewIpc.HierarchyQuiet}");
            HierarchyQuietAnnounced?.Invoke();
            return true;
        }
        if (ScreenPreviewIpc.TryParseAdded(msg, out var worldLine, out var rootDfs))
        {
            EditorLog.Write($"{ScreenPreviewLogPrefix} PREVIEW_ADDED wl={worldLine} root={rootDfs}");
            PreviewAdded?.Invoke(worldLine, rootDfs);
            return true;
        }
        if (ScreenPreviewIpc.TryParseCleared(msg, out var cleared))
        {
            EditorLog.Write($"{ScreenPreviewLogPrefix} PREVIEW_CLEARED count={cleared}");
            PreviewCleared?.Invoke(cleared);
            return true;
        }
        if (ScreenPreviewIpc.TryParseRefreshed(msg, out var refreshed, out var path))
        {
            EditorLog.Write($"{ScreenPreviewLogPrefix} PREVIEW_REFRESHED count={refreshed} path={path}");
            PreviewRefreshed?.Invoke(refreshed, path);
            return true;
        }
        if (ScreenPreviewIpc.TryParseError(msg, out var reason))
        {
            EditorLog.Write($"{ScreenPreviewLogPrefix} PREVIEW_ERROR {reason}");
            PreviewError?.Invoke(reason);
            return true;
        }
        return false;
    }
}
