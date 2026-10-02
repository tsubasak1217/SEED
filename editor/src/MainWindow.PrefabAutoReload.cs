// ============================================================
//  MainWindow.PrefabAutoReload.cs — プレハブ（.actor / .actor2d）の外部変更の取り込み
//
//  担当:
//   - PrefabAutoReloader（src/Reload/PrefabAutoReloader.cs）の生成と依存注入
//   - 外部の変更と判定されたプレハブを、再生状態と設定に従ってランタイムへ送る
//       Edit・自動反映オン  → PREFAB_REAPPLY_PATH（Undo 1 操作・件数のトースト）
//       Edit・自動反映オフ  → PREFAB_STATUS（版ずれのバナーだけ。シーンに触れない）
//       Play / Pause        → PREFAB_LIVE_PATCH_PATH（状態を保つ当て直し）＋停止後に Edit へ反映するため覚える
//     判定は AutoReloadPolicy.DecidePrefabExternalChange（純粋な関数。テストあり）
//   - エディタ自身の書き込み（SAVE_ACTOR・PREFAB_WRITE_BACK・EXPORT_ACTOR）を自己書き込みとして知らせる橋渡し
//   - 「表示 > シーン > プレハブを自動再読込」のトグル
//
//  正典: docs/editor_auto_reload.md §7.1、docs/editor_prefab.md 8 章。
// ============================================================

using System.IO;
using System.Windows;
using SEEDEditor.Reload;

namespace SEEDEditor;

public partial class MainWindow
{
    /// <summary>
    /// プレハブの外部変更の監視（アセットルート配下の .actor / .actor2d）。
    /// 監視の起動に失敗した場合は内部の watcher が null＝この機能だけ無効のまま動く。
    /// </summary>
    private PrefabAutoReloader? _prefabAutoReloader;

    /// <summary>
    /// Play 中の変更の書き戻し（PREFAB_WRITE_BACK）で書き込み中のプレハブの絶対パス（書き戻していなければ null）。
    /// 完了・失敗の応答で自己書き込みの窓を閉じるために覚えておく。
    /// </summary>
    private string? _prefabWriteBackPath;

    /// <summary>
    /// プレハブの外部変更の監視を初期化する（<see cref="InitSceneAutoReloader"/> と同じ時点で 1 回だけ呼ぶ）。
    /// </summary>
    private void InitPrefabAutoReloader()
    {
        _prefabAutoReloader = new PrefabAutoReloader(
            AssetsPath,
            Dispatcher,
            // 設定トグル（表示 > シーン > プレハブを自動再読込）
            isEnabled: () => EditorPreferences.Instance.AutoReloadPrefabs,
            onExternalChange: OnPrefabFileChangedExternally,
            log: EditorLog.Write);

        // アクタファイル化（EXPORT_ACTOR）はパネルが直接送るので開始は知らない。ランタイムは書き込みの直後に
        // EXPORT_ACTOR_OK を返すため、その時点で自己書き込みの終了として知らせる（判定はデバウンスの満了時なので間に合う）。
        if (_runtimeManager is not null)
        {
            _runtimeManager.ExportActorCompleted += (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path)) return;
                Dispatcher.InvokeAsync(() => _prefabAutoReloader?.NotifySelfWriteFinished(path));
            };
        }
    }

    /// <summary>
    /// 外部の変更と判定されたプレハブを、再生状態と設定に従ってランタイムへ送る（UI スレッドで呼ばれる）。
    /// </summary>
    /// <param name="absolutePath">プレハブの絶対パス。</param>
    private void OnPrefabFileChangedExternally(string absolutePath)
    {
        var name   = Path.GetFileName(absolutePath);
        var action = AutoReloadPolicy.DecidePrefabExternalChange(
            CurrentPlaybackState,
            autoReloadEnabled: EditorPreferences.Instance.AutoReloadPrefabs,
            autoPropagate:     EditorPreferences.Instance.PrefabAutoPropagateOnSave);

        switch (action)
        {
            case PrefabExternalChangeAction.None:
                return;

            case PrefabExternalChangeAction.ReapplyNow:
                // 画面プレビューは設定に関わらず作り直す（保存したときと同じ。シーンの内容を変えないため）
                RequestPreviewRefresh(absolutePath);
                if (_runtimeManager is null) return;
                // 件数のトースト・未保存の印は PREFAB_REAPPLY_DONE（OnPrefabReapplyCompleted）が出す（0 件なら黙る）
                _runtimeManager.SendToRuntime($"{PrefabReapplyPathCommandPrefix}{absolutePath}");
                ShowReloadStatusText(ScriptStatusBrushSuccess, string.Format(AutoReloadPolicy.MessagePrefabReappliedFormat, name));
                EditorLog.Write($"[PrefabAutoReload] 外部変更をシーンのインスタンスへ反映: {absolutePath}");
                return;

            case PrefabExternalChangeAction.StatusOnly:
                RequestPreviewRefresh(absolutePath);
                // 再展開はせず、版ずれの問い合わせだけ送る（stale が 1 件以上ならバナーが出る）
                RequestPrefabStatus();
                ShowReloadStatusText(ScriptStatusBrushWarn, string.Format(AutoReloadPolicy.MessagePrefabStatusOnlyFormat, name));
                EditorLog.Write($"[PrefabAutoReload] 外部変更を検出（自動反映オフのため版ずれの問い合わせのみ）: {absolutePath}");
                return;

            case PrefabExternalChangeAction.LivePatchAndRemember:
                // Play 中のインスタンスへ状態を保ったまま当て直し、停止後に Edit のシーンへ反映するため覚える
                // （件数のトーストは PREFAB_LIVE_PATCH_DONE。画面プレビューは停止後にまとめて作り直す）
                RequestPrefabLivePatch(absolutePath);
                ShowReloadStatusText(ScriptStatusBrushSuccess, string.Format(AutoReloadPolicy.MessagePrefabLivePatchedFormat, name));
                return;
        }
    }

    // ── エディタ自身の書き込み（自己書き込みの除外）──────────────────

    /// <summary>
    /// アクタータブの保存（SAVE_ACTOR）が終わったことを監視へ知らせる（成功・失敗どちらでも。
    /// <c>OnSaveCompleted</c> の冒頭、保存先のパスを使い終わる前に呼ぶ）。
    /// </summary>
    private void NotifyActorSaveFinishedToPrefabWatcher()
    {
        if (_savingActorPath is { } path) _prefabAutoReloader?.NotifySelfWriteFinished(path);
    }

    /// <summary>
    /// Play 中の変更の書き戻し（PREFAB_WRITE_BACK）を送ったことを監視へ知らせる。
    /// </summary>
    /// <param name="source">書き戻し先のプレハブ（assets:// 仮想パス or 絶対パス。分からなければ null＝窓を開けない）。</param>
    private void NotifyPrefabWriteBackStartedToPrefabWatcher(string? source)
    {
        if (string.IsNullOrEmpty(source)) return;
        _prefabWriteBackPath = VirtualPath.ToAbsolute(source, AssetsPath);
        _prefabAutoReloader?.NotifySelfWriteStarted(_prefabWriteBackPath);
    }

    /// <summary>
    /// Play 中の変更の書き戻しが終わったことを監視へ知らせる（完了・失敗どちらでも。UI スレッドで呼ぶ）。
    /// </summary>
    /// <param name="writtenSource">ランタイムが書いたプレハブ（完了の応答の仮想パス。失敗なら null）。</param>
    private void NotifyPrefabWriteBackFinishedToPrefabWatcher(string? writtenSource)
    {
        if (!string.IsNullOrEmpty(writtenSource))
            _prefabAutoReloader?.NotifySelfWriteFinished(VirtualPath.ToAbsolute(writtenSource, AssetsPath));
        // 送るときに覚えたパス（応答のパスと同じはずだが、違っても窓を開けっぱなしにしない）
        if (_prefabWriteBackPath is { } started) _prefabAutoReloader?.NotifySelfWriteFinished(started);
        _prefabWriteBackPath = null;
    }

    // ── 設定メニュー ─────────────────────────────────────────────

    /// <summary>
    /// 「表示 &gt; シーン &gt; プレハブを自動再読込」トグル。
    /// <see cref="EditorPreferences.AutoReloadPrefabs"/> へ永続化する。
    /// </summary>
    private void OnToggleAutoReloadPrefabs(object sender, RoutedEventArgs e)
    {
        bool on = MenuItemAutoReloadPrefabs.IsChecked;
        EditorPreferences.Instance.AutoReloadPrefabs = on;
        EditorPreferences.Save();
        EditorLog.Write($"AutoReloadPrefabs = {on}");
    }
}
