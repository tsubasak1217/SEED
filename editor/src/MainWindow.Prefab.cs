// ============================================================
//  MainWindow.Prefab.cs — プレハブ変更のシーンへの反映（伝播）
//
//  担当:
//   - プレハブ（.actor / .actor2d）をアクタータブで保存したときの、
//     今開いているシーン内インスタンスへの自動反映（設定でオン／オフ）
//   - シーンを開いた直後の「版ずれ」検出と、非モーダルバナーでの提示
//   - バナーの［更新する］［無視］操作
//   - Play 中のホットリロード（docs/editor_prefab.md 8 章）:
//       保存したら Play 中のインスタンスへ状態を保ったまま当て直す（PREFAB_LIVE_PATCH_PATH）、
//       「Play 中の変更をプレハブへ書き戻す」の確認と送信（PREFAB_WRITE_BACK）、
//       Play 停止後に Edit のシーンへ反映する（PrefabPlayReapplyQueue → PREFAB_REAPPLY_PATH / PREFAB_STATUS）、
//       書き戻しで古くなったアクタータブの読み直し
//
//  設計の前提（正典: runtime/src/engine/core/app_base/app/prefab_ops.rs 冒頭、
//  および docs/editor_prefab.md）:
//   - ランタイムはロード時にもプレハブ保存時にも**自動では再展開しない**。
//     過去にそれをやってインスタンス側の編集が黙って消えるデータ損失を起こしたため。
//   - 反映は必ずエディタからの明示コマンド（PREFAB_REAPPLY_PATH）で行い、
//     (1) 設定でオフにできる (2) Undo 1 操作で戻せる (3) 件数を必ず知らせる
//     の 3 条件を満たす。ロード時は**検出して知らせるだけ**で、決して上書きしない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── IPC コマンド文字列 ───────────────────────────────────────
    // マジックストリングを散らさないよう、送信するコマンド名はここへ集約する。

    /// <summary>指定した 1 本のプレハブを参照する全インスタンスを再展開する IPC の接頭辞。</summary>
    private const string PrefabReapplyPathCommandPrefix = "PREFAB_REAPPLY_PATH:";

    /// <summary>シーン内プレハブの版ずれを問い合わせる IPC（引数なし・読み取りのみ）。</summary>
    private const string PrefabStatusCommand = "PREFAB_STATUS";

    /// <summary>Play 中のインスタンスへ状態を保ったまま当て直す IPC の接頭辞（docs/editor_prefab.md 8 章）。</summary>
    private const string PrefabLivePatchPathCommandPrefix = "PREFAB_LIVE_PATCH_PATH:";

    /// <summary>Play 中の変更をプレハブへ書き戻す IPC の接頭辞（後ろにインスタンスの根の DFS ID）。</summary>
    private const string PrefabWriteBackCommandPrefix = "PREFAB_WRITE_BACK:";

    // ── 通知の文言（Play 中の当て直し・書き戻し）────────────────

    /// <summary>当て直しの結果のトースト（{0}=プレハブ名・{1}=件数・{2}=停止後の扱い）。</summary>
    private const string LivePatchToastFormat = "プレハブ {0} を Play 中の {1} 個のインスタンスへ当て直しました（{2}）";

    /// <summary>停止後に Edit のシーンへも反映するとき（設定オン）の添え書き。</summary>
    private const string AfterStopReapplyNote = "停止後に Edit のシーンへも反映";

    /// <summary>停止後に Edit のシーンへ自動では反映しないとき（設定オフ）の添え書き。</summary>
    private const string AfterStopBannerNote = "Edit のシーンへは停止後に更新のお知らせを出します";

    /// <summary>当て直しの失敗のトースト（{0}=理由）。</summary>
    private const string LivePatchFailedToastFormat = "Play 中の当て直しに失敗しました: {0}";

    /// <summary>書き戻しの確認ダイアログの本文（{0}=書き戻し先）。</summary>
    private const string WriteBackConfirmFormat =
        "Play 中の変更を {0} へ書き戻します。\n" +
        "元に戻せません（ファイルが上書きされます）。\n" +
        "スクリプトが Play 中に生成した部分（積まれた画面・リストの行など）は書き込みません。\n\n" +
        "続行しますか？";

    /// <summary>書き戻しの確認ダイアログの題名。</summary>
    private const string WriteBackConfirmTitle = "Play 中の変更をプレハブへ書き戻す";

    /// <summary>書き戻しの結果のトースト（{0}=プレハブ名・{1}=当て直した件数・{2}=停止後の扱い）。</summary>
    private const string WriteBackToastFormat = "Play 中の変更を {0} へ書き戻しました（Play 中の {1} 個へ当て直し・{2}）";

    /// <summary>書き戻しの失敗のダイアログの本文（{0}=理由）。</summary>
    private const string WriteBackFailedFormat = "Play 中の変更を書き戻せませんでした。\n\n{0}";

    // ── 通知の文言（書き戻しで古くなったタブ・停止後の反映）────────────

    /// <summary>
    /// 書き戻しで古くなったアクタータブを、未保存の編集があるときに読み直してよいか確かめる本文（{0}=タブの名前）。
    /// 未保存の印はシーンとタブで 1 つなので、シーンだけの編集でもこの確認が出る（2 回目のレビュー #14）。
    /// </summary>
    private const string StaleTabReloadConfirmFormat =
        "{0} のファイルは Play 中の書き戻しで変わっています。\n" +
        "ファイルから読み直すと、このタブの未保存の編集と Undo の履歴は失われます。\n" +
        "（未保存の印はシーンとタブで共通のため、シーンだけを編集したときもこの確認が出ます。読み直してもシーンの編集は消えません）\n\n" +
        "［はい］ファイルから読み直す\n" +
        "［いいえ］今の中身のまま表示する（このタブを保存すると、書き戻した内容を上書きします）";

    /// <summary>上の確認の題名。</summary>
    private const string StaleTabReloadConfirmTitle = "書き戻しで変わったアクタータブ";

    /// <summary>
    /// 停止時にアクタータブを表示中だったので、Edit のシーンへの自動の再展開を見送ったときのトースト（{0}=プレハブ名か本数）。
    /// アクタータブの表示中は Undo の履歴がタブの切り替えで作り直され「Ctrl+Z で戻せます」を約束できないため（2 回目のレビュー #16）。
    /// </summary>
    private const string ReapplyDeferredForActorTabToastFormat =
        "アクタータブの表示中のため、{0} の変更は Edit のシーンへ自動では反映していません（Ctrl+Z で戻せないため）。" +
        "シーンのタブで、バナーの［更新する］から反映してください";

    /// <summary>再展開の結果のトースト（{0}=プレハブ名・{1}=件数）。シーンのタブを表示中（Undo 1 操作で戻せる）。</summary>
    private const string ReapplyDoneToastFormat = "プレハブ {0} の変更を {1} 個のインスタンスへ反映しました（Ctrl+Z で戻せます）";

    /// <summary>
    /// 再展開の結果のトースト（{0}=プレハブ名・{1}=件数）。アクタータブを表示中（保存に続く自動反映など）。
    /// シーンのタブへ移ると SET_ACTIVE_WORLD_LINE が Undo の履歴を作り直すので、Ctrl+Z を約束しない（2 回目のレビュー #16）。
    /// </summary>
    private const string ReapplyDoneInActorTabToastFormat =
        "プレハブ {0} の変更を Edit のシーンの {1} 個のインスタンスへ反映しました（アクタータブの表示中のため、シーンのタブへ移ると Ctrl+Z では戻せません）";

    /// <summary>
    /// Play 中に変わったプレハブを覚えておき、Play 停止後に Edit のシーンへ反映する待ち行列
    /// （判定は純粋なクラス <see cref="SEEDEditor.Reload.PrefabPlayReapplyQueue"/>。テストあり）。
    /// </summary>
    private readonly SEEDEditor.Reload.PrefabPlayReapplyQueue _prefabPlayQueue = new();

    /// <summary>
    /// Play 中の書き戻しでファイルが変わったアクタータブの「読み直しが要る」印。タブの中身は古い版のままなので、
    /// Edit へ戻って次にそのタブを表示するときに読み直す（<see cref="TryReloadStaleActorTab"/>）。
    /// タブを閉じたとき・新しく開いたときは印を消す（<see cref="ForgetStaleActorTab"/>。2 回目のレビュー #14。
    /// 判定は純粋なクラス <see cref="SEEDEditor.Reload.StaleActorTabs"/>。テストあり）。
    /// </summary>
    private readonly SEEDEditor.Reload.StaleActorTabs _staleActorTabs = new();

    /// <summary>
    /// 直近に保存したプレハブ（.actor / .actor2d）の絶対パス。
    /// 保存は非同期（SAVE_ACTOR → SAVE_OK）なので、完了時に「どのファイルを保存したか」を
    /// 思い出すために保持する。保存を開始していないときは null。
    /// </summary>
    private string? _savingActorPath;

    /// <summary>
    /// 版ずれバナーで［更新する］を押したときに再展開する対象の参照パス一覧。
    /// バナーを出した時点の PREFAB_STATUS の結果（stale が 1 件以上のものだけ）を保持する。
    /// </summary>
    private readonly List<string> _stalePrefabSources = new();

    // ── プレハブ保存時の自動反映 ─────────────────────────────────

    /// <summary>
    /// プレハブの保存を開始したことを記録する（<c>ExecuteActorSave</c> から呼ぶ）。
    /// </summary>
    /// <param name="path">保存先の絶対パス。</param>
    private void NotifyActorSaveStarted(string path)
    {
        _savingActorPath = path;
        // プレハブの外部変更の監視へ「これから自分が書く」と伝える（書き込むのはランタイム。SAVE_OK / SAVE_ERROR まで＋余韻。
        // 自分の保存を外部変更と取り違えて二重に再展開・当て直ししないため。MainWindow.PrefabAutoReload.cs）
        _prefabAutoReloader?.NotifySelfWriteStarted(path);
    }

    /// <summary>
    /// プレハブの保存が完了したときに、シーン内インスタンスへの自動反映を行う
    /// （<c>OnSaveCompleted</c> の成功経路から呼ぶ）。
    ///
    /// 設定「プレハブ保存時にシーンのインスタンスへ自動反映」がオフのときは何もしない。
    /// 反映結果（件数）は <see cref="OnPrefabReapplyCompleted"/> がトーストで知らせる。
    /// </summary>
    private void PropagateSavedPrefabToScene()
    {
        var path = _savingActorPath;
        _savingActorPath = null;

        if (string.IsNullOrEmpty(path)) return;
        var state = CurrentPlaybackState;
        // 画面プレビューは設定に関わらず作り直す（シーンの内容を変えないため。MainWindow.ScreenPreview.cs）。
        // Play 中はプレビューが外れている（停止で戻る）ので、停止後にまとめて作り直す（OnReturnedToEditForPrefabs）。
        if (!SEEDEditor.Reload.AutoReloadPolicy.IsPlaying(state)) RequestPreviewRefresh(path);

        // ランタイムがシーンを持っていなければ反映先が無い（アクタータブ単独編集など）。
        if (_runtimeManager is null) return;

        switch (SEEDEditor.Reload.PrefabPlayReapplyQueue.DecideOnSave(
                    state, EditorPreferences.Instance.PrefabAutoPropagateOnSave))
        {
            case SEEDEditor.Reload.PrefabSaveAction.ReapplyNow:
                _runtimeManager.SendToRuntime($"{PrefabReapplyPathCommandPrefix}{path}");
                EditorLog.Write($"[Prefab] 保存に続けて自動反映を要求: {path}");
                break;
            case SEEDEditor.Reload.PrefabSaveAction.LivePatchAndRemember:
                // Play 中: 丸ごとの再展開は OnStart が走り直して画面が初期化されるので、状態を保つ当て直しにする。
                // Edit のシーンへは Play 停止後に反映する（Play の世界は停止で Play 前へ戻るため）。
                RequestPrefabLivePatch(path);
                break;
            case SEEDEditor.Reload.PrefabSaveAction.None:
                break;
        }
    }

    // ── Play 中の当て直し（PREFAB_LIVE_PATCH_PATH）──────────────────

    /// <summary>
    /// Play 中のインスタンスへ、プレハブの今の中身を状態を保ったまま当て直すよう要求し、
    /// Play 停止後に Edit のシーンへ反映するために覚えておく。
    /// </summary>
    /// <param name="path">プレハブの絶対パス or assets:// 仮想パス。</param>
    private void RequestPrefabLivePatch(string path)
    {
        if (_runtimeManager is null) return;
        _runtimeManager.SendToRuntime($"{PrefabLivePatchPathCommandPrefix}{path}");
        RememberPrefabChangedDuringPlay(path);
        EditorLog.Write($"[Prefab] Play 中の当て直しを要求: {path}");
    }

    /// <summary>
    /// Play 中に変わったプレハブを覚える（絶対パスへ揃えてから。停止後の反映とプレビューの作り直しに使う）。
    /// </summary>
    /// <param name="path">プレハブの絶対パス or assets:// 仮想パス。</param>
    private void RememberPrefabChangedDuringPlay(string path)
    {
        var absolute = SEEDEditor.VirtualPath.ToAbsolute(path, AssetsPath);
        if (_prefabPlayQueue.Remember(absolute, CurrentPlaybackState))
            EditorLog.Write($"[Prefab] Play 停止後に Edit のシーンへ反映するよう覚えました: {absolute}");
    }

    /// <summary>
    /// Play 中の当て直しが終わったときの通知（IPC <c>PREFAB_LIVE_PATCH_DONE</c>）。
    /// 0 件（Play 中のワールドにそのプレハブのインスタンスが無い）のときは黙っている。
    /// </summary>
    /// <param name="count">当て直したインスタンス数。</param>
    /// <param name="source">プレハブの assets:// 仮想パス。</param>
    private void OnPrefabLivePatchCompleted(int count, string source)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (count <= 0) return;
            ShowToast(string.Format(LivePatchToastFormat, PrefabDisplayName(source), count, AfterStopNote()));
            EditorLog.Write($"[Prefab] Play 中の当て直し: {source} → {count} 件");
        });
    }

    /// <summary>Play 中の当て直しが失敗したときの通知（IPC <c>PREFAB_LIVE_PATCH_ERROR</c>）。トーストで知らせる。</summary>
    /// <param name="reason">ランタイムが返した理由。</param>
    private void OnPrefabLivePatchFailed(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ShowToast(string.Format(LivePatchFailedToastFormat, reason));
            EditorLog.Write($"[Prefab] Play 中の当て直しに失敗: {reason}");
        });
    }

    /// <summary>停止後の扱いの添え書き（設定「プレハブ保存時にシーンのインスタンスへ自動反映」で変わる）。</summary>
    private static string AfterStopNote() =>
        EditorPreferences.Instance.PrefabAutoPropagateOnSave ? AfterStopReapplyNote : AfterStopBannerNote;

    // ── Play 中の変更の書き戻し（PREFAB_WRITE_BACK）────────────────

    /// <summary>
    /// 「Play 中の変更をプレハブへ書き戻す」（ヒエラルキーの右クリック・インスペクタのプレハブの帯から）。
    /// 利用者のファイルを上書きするので、確認ダイアログを 1 回出してから送る。
    /// </summary>
    /// <param name="actorDfsId">プレハブのインスタンスの根の DFS ID。</param>
    /// <param name="source">プレハブの参照パス（assets:// 仮想パス。分からなければ null）。</param>
    private void RequestPrefabWriteBack(int actorDfsId, string? source)
    {
        if (_runtimeManager is null) return;
        // Play / Pause 中だけの操作（メニュー・ボタンも Play 中だけ出すが、状態が変わった直後の押下に備える）
        if (!SEEDEditor.Reload.AutoReloadPolicy.IsPlaying(CurrentPlaybackState)) return;

        var target = string.IsNullOrEmpty(source)
            ? "プレハブ"
            : SEEDEditor.VirtualPath.ToDisplay(source, AssetsPath);
        var answer = MessageBox.Show(
            string.Format(WriteBackConfirmFormat, target), WriteBackConfirmTitle,
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        _runtimeManager.SendToRuntime($"{PrefabWriteBackCommandPrefix}{actorDfsId}");
        // 書き込むのはランタイム。監視が自分の書き戻しを外部変更と取り違えて当て直しを二重に送らないよう知らせる
        NotifyPrefabWriteBackStartedToPrefabWatcher(source);
        EditorLog.Write($"[Prefab] Play 中の変更の書き戻しを要求: DFS {actorDfsId}（{target}）");
    }

    /// <summary>
    /// 書き戻しが終わったときの通知（IPC <c>PREFAB_WRITE_BACK_DONE</c>）。
    /// (1) そのファイルを開いているアクタータブを Edit へ戻ってから読み直す印を付け、
    /// (2) Play 停止後に Edit のシーンへ反映するよう覚え、(3) トーストで知らせる。
    /// </summary>
    /// <param name="count">続けて当て直した Play 中のインスタンス数（書いた本人を含む）。</param>
    /// <param name="source">書き戻したプレハブの assets:// 仮想パス。</param>
    private void OnPrefabWriteBackCompleted(int count, string source)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 書いたのはランタイム（＝自分の書き込み）。監視の窓を閉じ、今の内容を「知っている内容」として覚えさせる
            NotifyPrefabWriteBackFinishedToPrefabWatcher(source);
            MarkActorTabStale(source);
            RememberPrefabChangedDuringPlay(source);
            ShowToast(string.Format(WriteBackToastFormat, PrefabDisplayName(source), count, AfterStopNote()));
            EditorLog.Write($"[Prefab] Play 中の変更を書き戻し: {source}（当て直し {count} 件）");
        });
    }

    /// <summary>書き戻しが失敗したときの通知（IPC <c>PREFAB_WRITE_BACK_ERROR</c>）。ファイルは書かれていない。</summary>
    /// <param name="reason">ランタイムが返した理由。</param>
    private void OnPrefabWriteBackFailed(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // ファイルは書かれていないが、開けた自己書き込みの窓は閉じる（開けっぱなしだと外部変更を取りこぼす）
            NotifyPrefabWriteBackFinishedToPrefabWatcher(null);
            EditorLog.Write($"[Prefab] 書き戻しに失敗: {reason}");
            MessageBox.Show(string.Format(WriteBackFailedFormat, reason), WriteBackConfirmTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }

    // ── Play 停止後の反映 ───────────────────────────────────────

    /// <summary>
    /// Play が止まって Edit へ戻ったとき（<c>OnStateChanged</c> の Edit）に、Play 中に変わったプレハブを
    /// Edit のシーンへ反映する。設定オンでシーンのタブを表示中なら <c>PREFAB_REAPPLY_PATH</c>（パスごとに Undo 1 操作・
    /// 件数のトースト）、設定オフまたはアクタータブを表示中なら <c>PREFAB_STATUS</c> だけ（版ずれのバナーで知らせる）。
    /// 画面プレビューは設定に関わらず作り直し、表示中のアクタータブが書き戻しで古くなっていれば読み直す。
    ///
    /// <para>
    /// 【送る順（2026-10-03 の 2 回目のレビュー #16）】
    /// 表示中のアクタータブの読み直し（<c>OPEN_ACTOR</c>）を**先に**送る。<c>OPEN_ACTOR</c> は Undo の履歴を作り直すので、
    /// 再展開の後に送ると再展開の Undo を消したうえで「Ctrl+Z で戻せます」と出していた。さらにアクタータブの表示中は、
    /// シーンのタブへ移る <c>SET_ACTIVE_WORLD_LINE</c> も履歴を作り直すので、自動の再展開そのものを見送ってバナーに落とす
    /// （判定は <see cref="SEEDEditor.Reload.PrefabPlayReapplyQueue.TakeOnReturnToEdit"/>）。
    /// </para>
    /// </summary>
    private void OnReturnedToEditForPrefabs()
    {
        // ① いま表示しているアクタータブが古ければ、再展開より前に読み直す（ほかのタブは表示したときに読み直す）
        if (_activeActorPath is not null) TryReloadStaleActorTab(_activeActorPath);

        // ② Edit のシーンへの反映。アクタータブ（キャンバス編集タブを含む）の表示中は自動では再展開しない
        var plan = _prefabPlayQueue.TakeOnReturnToEdit(
            EditorPreferences.Instance.PrefabAutoPropagateOnSave, actorTabShown: _activeActorPath is not null);
        foreach (var path in plan.ChangedPaths)
            RequestPreviewRefresh(path);
        if (_runtimeManager is not null)
        {
            foreach (var path in plan.ReapplyPaths)
            {
                _runtimeManager.SendToRuntime($"{PrefabReapplyPathCommandPrefix}{path}");
                EditorLog.Write($"[Prefab] Play 停止後に Edit のシーンへ反映: {path}");
            }
            if (plan.RequestStatus) RequestPrefabStatus();
        }
        if (plan.DeferredByActorTab)
        {
            var target = plan.ChangedPaths.Count == 1
                ? PrefabDisplayName(plan.ChangedPaths[0])
                : $"{plan.ChangedPaths.Count} 個のプレハブ";
            ShowToast(string.Format(ReapplyDeferredForActorTabToastFormat, target));
            EditorLog.Write($"[Prefab] アクタータブの表示中のため停止後の自動反映を見送り、版ずれのバナーに落としました: {target}");
        }
    }

    // ── 書き戻しで古くなったアクタータブ ─────────────────────────

    /// <summary>書き戻したファイルを開いているアクタータブに「読み直しが要る」印を付ける。</summary>
    /// <param name="source">書き戻したプレハブ（assets:// 仮想パス or 絶対パス）。</param>
    private void MarkActorTabStale(string source)
    {
        var absolute = SEEDEditor.VirtualPath.ToAbsolute(source, AssetsPath);
        var tab = _actorTabs.FirstOrDefault(t =>
            !t.IsSceneCanvas && string.Equals(NormalizeTabPath(t.Path), NormalizeTabPath(absolute), StringComparison.OrdinalIgnoreCase));
        if (tab is null) return;
        _staleActorTabs.Mark(tab.Path);
        EditorLog.Write($"[Prefab] 書き戻しでタブの中身が古くなりました（Edit へ戻って表示するときに読み直します）: {tab.Path}");
    }

    /// <summary>
    /// アクタータブの「読み直しが要る」印を消す（タブを閉じたとき・同じファイルを新しいタブで開いたとき。MainWindow.FileOps.cs）。
    /// 以前は印が残り、閉じた後に開き直して編集したタブを、次に表示したとき確認なしで読み直して編集と Undo を消していた（2 回目のレビュー #14）。
    /// </summary>
    /// <param name="path">アクタータブのパス（<c>ActorTab.Path</c>）。</param>
    private void ForgetStaleActorTab(string path)
    {
        if (_staleActorTabs.Forget(path))
            EditorLog.Write($"[Prefab] タブを閉じた・開き直したので読み直しの印を消しました: {path}");
    }

    /// <summary>
    /// そのアクタータブに読み直しの印があれば、ファイルから読み直して表示する（<c>OPEN_ACTOR</c> は同じ世界線を
    /// 読み直してそのタブを表示する）。印が無ければ何もしない。Edit 中だけ（Play 中はタブを表示できない）。
    ///
    /// <para>
    /// 未保存の編集があるときは読み直す前に確かめる（2 回目のレビュー #14。読み直すとそのタブの未保存の編集と Undo が消える）。
    /// タブ単位の未保存の印は無いので、エディタ全体の未保存の印（<c>_isDirty</c>。シーンとタブで共通）で代用する
    /// （シーンだけの編集でも確かめる。安全側）。［いいえ］なら今の中身のまま表示し、印は消える（聞くのは 1 回だけ）。
    /// </para>
    /// </summary>
    /// <param name="path">アクタータブのパス（<c>ActorTab.Path</c>）。</param>
    /// <returns>読み直したら true（呼び出し側は SET_ACTIVE_WORLD_LINE を送らなくてよい）。</returns>
    private bool TryReloadStaleActorTab(string path)
    {
        var tab = _actorTabs.FirstOrDefault(t => t.Path == path);
        var action = _staleActorTabs.TakeOnShow(
            path,
            canReloadNow: _runtimeManager?.State == EditorState.Edit && tab is not null,
            hasUnsavedEdits: _isDirty);
        switch (action)
        {
            case SEEDEditor.Reload.StaleActorTabShowAction.ShowAsIs:
                return false;

            case SEEDEditor.Reload.StaleActorTabShowAction.AskBeforeReload:
                var answer = MessageBox.Show(
                    string.Format(StaleTabReloadConfirmFormat, tab!.Name), StaleTabReloadConfirmTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    EditorLog.Write($"[Prefab] 書き戻しで変わったアクタータブを読み直さずに表示しました（利用者が選択）: {tab.Path}");
                    return false;
                }
                break;

            case SEEDEditor.Reload.StaleActorTabShowAction.Reload:
                break;
        }

        SendNavCommand($"OPEN_ACTOR:{tab!.WorldLine},{tab.Path}");
        EditorLog.Write($"[Prefab] 書き戻しで変わったアクタータブを読み直しました: {tab.Path}");
        return true;
    }

    /// <summary>タブのパスの比較用（区切りを揃える）。</summary>
    private static string NormalizeTabPath(string path) => path.Replace('/', '\\');

    /// <summary>
    /// 参照パス指定の再展開が終わったときの通知（IPC <c>PREFAB_REAPPLY_DONE</c>）。
    ///
    /// 0 件（＝このシーンにそのプレハブのインスタンスが無い）のときは黙っている。
    /// 1 件以上ならトーストで件数と「Ctrl+Z で戻せる」ことを知らせ、シーンを未保存扱いにする
    /// （再展開の結果は .scene を保存して初めて残るため）。
    /// アクタータブ（キャンバス編集タブを含む）の表示中（保存に続く自動反映など）は、シーンのタブへ移ると
    /// <c>SET_ACTIVE_WORLD_LINE</c> が Undo の履歴を作り直すので、Ctrl+Z を約束しない文言にする（2 回目のレビュー #16）。
    /// </summary>
    /// <param name="count">再展開したインスタンス数。</param>
    /// <param name="source">プレハブの assets:// 仮想パス。</param>
    private void OnPrefabReapplyCompleted(int count, string source)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (count <= 0) return;

            var name = PrefabDisplayName(source);
            var format = _activeActorPath is null ? ReapplyDoneToastFormat : ReapplyDoneInActorTabToastFormat;
            ShowToast(string.Format(format, name, count));
            // 再展開はシーンの内容を変えるので、保存を促すために未保存扱いにする。
            MarkDirty();
            EditorLog.Write($"[Prefab] 自動反映: {source} → {count} 件");
        });
    }

    // ── シーンを開いた直後の版ずれ検出 ───────────────────────────

    /// <summary>
    /// 今開いているシーンのプレハブ版ずれをランタイムへ問い合わせる
    /// （シーン読み込み完了 <c>SCENE_LOADED</c> の後に呼ぶ）。
    ///
    /// 読み取りのみのコマンドで、シーンには一切触れない。
    /// </summary>
    private void RequestPrefabStatus()
    {
        HidePrefabStaleBanner();
        _runtimeManager?.SendToRuntime(PrefabStatusCommand);
    }

    /// <summary>
    /// 版ずれ問い合わせの応答（IPC <c>PREFAB_STATUS</c>）を受けてバナーを出す。
    ///
    /// stale（＝取り込んだ版とファイルの現在の版が食い違う）が 1 件以上あるときだけ出す。
    /// 版が不明な旧シーン由来のインスタンス（unknown）は対象にしない
    /// ＝ 勝手に「更新しますか」と促さない。次に再展開したときに版が記録される。
    /// </summary>
    /// <param name="json">
    /// <c>[{"source":..,"total":N,"stale":N,"unknown":N,"missing":bool}, ..]</c> の JSON。
    /// </param>
    private void OnPrefabStatusReceived(string json)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _stalePrefabSources.Clear();
            int staleInstances = 0;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("stale",  out var staleEl)) continue;
                    if (!item.TryGetProperty("source", out var srcEl))   continue;
                    int stale = staleEl.TryGetInt32(out int v) ? v : 0;
                    if (stale <= 0) continue;

                    var source = srcEl.GetString();
                    if (string.IsNullOrEmpty(source)) continue;

                    _stalePrefabSources.Add(source);
                    staleInstances += stale;
                }
            }
            catch (JsonException e)
            {
                EditorLog.Write($"[Prefab] PREFAB_STATUS の解析に失敗: {e.Message}");
                return;
            }

            if (_stalePrefabSources.Count == 0) { HidePrefabStaleBanner(); return; }

            ShowPrefabStaleBanner(staleInstances);
        });
    }

    /// <summary>版ずれバナーを、対象プレハブ名とインスタンス数付きで表示する。</summary>
    /// <param name="staleInstances">更新が来ているインスタンスの合計数。</param>
    private void ShowPrefabStaleBanner(int staleInstances)
    {
        if (PrefabStaleBanner is null || PrefabStaleText is null) return;

        // 対象が 1 本ならファイル名を、複数なら本数を出す（バナーを 1 行に収めるため）。
        var target = _stalePrefabSources.Count == 1
            ? PrefabDisplayName(_stalePrefabSources[0])
            : $"{_stalePrefabSources.Count} 個のプレハブ";

        PrefabStaleText.Text = $"プレハブが更新されています: {target}（インスタンス {staleInstances} 個）";
        PrefabStaleBanner.Visibility = Visibility.Visible;
        EditorLog.Write($"[Prefab] 版ずれ検出: {target} / インスタンス {staleInstances} 個");
    }

    /// <summary>版ずれバナーを閉じる。</summary>
    private void HidePrefabStaleBanner()
    {
        if (PrefabStaleBanner is not null)
            PrefabStaleBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 版ずれバナーの［更新する］。検出した参照パスごとに再展開を要求する。
    ///
    /// 破壊的操作（インスタンス側の変更がファイル内容で上書きされる）だが、
    /// Undo 1 操作で戻せることを結果のトーストで案内する。
    /// </summary>
    private void OnPrefabStaleUpdate(object sender, RoutedEventArgs e)
    {
        HidePrefabStaleBanner();
        if (_runtimeManager is null) return;

        foreach (var source in _stalePrefabSources)
            _runtimeManager.SendToRuntime($"{PrefabReapplyPathCommandPrefix}{source}");

        _stalePrefabSources.Clear();
    }

    /// <summary>版ずれバナーの［無視］。今回の表示を閉じるだけで、シーンには触れない。</summary>
    private void OnPrefabStaleIgnore(object sender, RoutedEventArgs e)
    {
        HidePrefabStaleBanner();
        _stalePrefabSources.Clear();
    }

    // ── 設定メニュー ─────────────────────────────────────────────

    /// <summary>
    /// 「表示 &gt; シーン &gt; プレハブ保存時にシーンのインスタンスへ自動反映」トグル。
    /// <see cref="EditorPreferences.PrefabAutoPropagateOnSave"/> へ永続化する。
    /// </summary>
    private void OnTogglePrefabAutoPropagate(object sender, RoutedEventArgs e)
    {
        bool on = MenuItemPrefabAutoPropagate.IsChecked;
        EditorPreferences.Instance.PrefabAutoPropagateOnSave = on;
        EditorPreferences.Save();
        EditorLog.Write($"PrefabAutoPropagateOnSave = {on}");
    }

    // ── 表示用ヘルパ ─────────────────────────────────────────────

    /// <summary>
    /// プレハブ参照パス（assets:// 仮想パス or 絶対パス）から、通知に出す短い名前を取り出す。
    /// 区切りは Windows / 仮想パス両方を考慮して '/' と '\\' の両方を見る。
    /// </summary>
    private static string PrefabDisplayName(string source)
    {
        if (string.IsNullOrEmpty(source)) return "(不明なプレハブ)";
        int cut = source.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 && cut + 1 < source.Length ? source[(cut + 1)..] : source;
    }
}
