// ============================================================
//  MainWindow.Migration.cs — アセット形式のマイグレーションの配線
//
//  【役割】
//  1. 読み込みの門（AssetMigrationGateway）へ、この画面が持っているものを差し込む
//     ・ランタイム exe の解決（ビルド構成に追従する）
//     ・利用者への提示（モーダル＝EditorDialogs / 案内＝トースト）
//     ・ログ（Output パネルへ流れる EditorLog）
//  2. プロジェクトを開いた直後の「古い形式が n 件あります」案内を起こす
//  3. 「ツール → プロジェクトの形式をアップグレード...」メニューの受け口
//  4. その前後の開いているシーンの扱い（2026-10-03。レビュー #8。判定は Migration/UpgradeUnsavedPolicy.cs）:
//     未保存なら「保存してから」「破棄して」「やめる」を選ばせ、実行したらシーンをディスクから読み直す
//
//  【なぜ MainWindow に置くのか】
//  差し込む中身（トースト・ビルド構成の選択）はこのウィンドウしか持っていない。
//  マイグレーション側は差し込まれていなくても動く（ログだけ）ので、
//  ヘッドレスや単体テストはこの配線が無い状態で成立する。
//
//  正典: docs/asset_migration.md（6.5 エディタ側の実装メモ）
// ============================================================

using System.Windows;
using SEEDEditor.Migration;
using SEEDEditor.Migration.Presentation;

namespace SEEDEditor;

public partial class MainWindow
{
    /// <summary>
    /// マイグレーションの配線を行い、古い形式の下調べを起こす。
    /// <see cref="OnWindowLoaded"/> から 1 回だけ呼ぶ。
    /// </summary>
    private void InitAssetMigration()
    {
        // ── 差し込み ──
        // exe のパスは「いま選ばれているビルド構成」から毎回導出する。
        // 実行中に構成を切り替えても、次の変換から新しい exe を使う。
        AssetMigrationGateway.RuntimeExePathProvider = () => RuntimeExePath;

        // 止めたとき＝モーダル（ヘッドレスではログへ流れる）。案内＝このウィンドウのトースト。
        AssetMigrationGateway.Notifier = new MigrationNotifier(ShowToast);

        // ログは Output パネルへ流れる EditorLog へ。
        AssetMigrationGateway.Log = EditorLog.Write;

        // ── プロジェクトを開いたときの案内 ──
        // バックグラウンドで dry-run を 1 回だけ走らせる（1 バイトも書き込まない）。
        // ヘッドレス起動では通知しない（ログには残る）。
        // ランタイム exe が無い（未ビルド）ときは黙ってログだけで終わる。
        ProjectUpgradeNotice.ScanInBackground(
            UpgradeTargetPath(),
            notify: !SEEDEditor.Headless.EditorStartupOptions.IsHeadless);
    }

    /// <summary>
    /// 「ツール → プロジェクトの形式をアップグレード...」。
    /// 下調べ（dry-run）の結果を見せてから実行させるダイアログを開く。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnUpgradeProjectFormats(object sender, RoutedEventArgs e)
    {
        var target = UpgradeTargetPath();
        if (string.IsNullOrWhiteSpace(target))
        {
            SEEDEditor.Headless.EditorDialogs.Show(
                MigrationMessages.UPGRADE_NO_PROJECT,
                MigrationMessages.UPGRADE_WINDOW_TITLE,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── アクタータブ表示中は始めない（2026-10-03 のレビュー #6）──
        //   DoQuickSave はアクタータブ表示中はアクターだけを保存し、_isDirty はシーンとタブで 1 つなので、
        //   「保存してから」でもシーンの未保存の編集は保存されず、実行後の読み直しで消える。
        if (_activeActorPath != null)
        {
            SEEDEditor.Headless.EditorDialogs.Show(
                MigrationMessages.UPGRADE_ACTOR_TAB_OPEN,
                MigrationMessages.UPGRADE_WINDOW_TITLE,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ── 未保存の編集があるなら、先に保存か破棄を選ばせる（レビュー #8。判定は UpgradeUnsavedPolicy.cs）──
        //   アップグレードはディスクの .scene の prefab_hash も貼り直す。未保存のまま実行して後で保存すると、
        //   メモリの古い prefab_hash で上書きされ、次に開いたとき偽の「プレハブが更新されています」が出る。
        var choice = _isDirty ? AskUnsavedBeforeUpgrade() : UpgradeUnsavedChoice.Cancel;
        switch (UpgradeUnsavedPolicy.Decide(_isDirty, choice))
        {
            case UpgradeStartAction.Abort:
                return;

            case UpgradeStartAction.SaveThenOpen:
                // 保存は非同期（完了は OnSaveCompleted → ContinuePendingUpgrade）。送れなければ取りやめる
                _pendingUpgradeTarget = target;
                if (!DoQuickSave())
                {
                    _pendingUpgradeTarget = null;
                    ShowToast(MigrationMessages.UPGRADE_SAVE_NOT_STARTED);
                }
                return;

            case UpgradeStartAction.OpenNow:
                OpenUpgradeWindow(target);
                return;
        }
    }

    /// <summary>
    /// 「保存してからアップグレード」で保存を待っている対象（.seedproj か assets ルート）。待っていなければ null。
    /// </summary>
    private string? _pendingUpgradeTarget;

    /// <summary>
    /// 未保存の編集があるときに、アップグレードの前に保存するか・破棄するか・やめるかを聞く。
    /// </summary>
    /// <returns>選んだこと（ダイアログを閉じた・ヘッドレスの既定応答はやめる）。</returns>
    private static UpgradeUnsavedChoice AskUnsavedBeforeUpgrade()
    {
        var answer = SEEDEditor.Headless.EditorDialogs.Show(
            MigrationMessages.UPGRADE_UNSAVED_PROMPT,
            MigrationMessages.UPGRADE_WINDOW_TITLE,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return answer switch
        {
            MessageBoxResult.Yes => UpgradeUnsavedChoice.SaveFirst,
            MessageBoxResult.No  => UpgradeUnsavedChoice.Discard,
            _                    => UpgradeUnsavedChoice.Cancel,
        };
    }

    /// <summary>
    /// 「保存してからアップグレード」の続き（OnSaveCompleted から呼ぶ。UI スレッド）。
    /// 保存できたらアップグレードの窓を開き、失敗したら開かない。待っていなければ何もしない。
    /// </summary>
    /// <param name="saved">保存に成功したか。</param>
    private void ContinuePendingUpgrade(bool saved)
    {
        if (_pendingUpgradeTarget is not { } target) return;
        _pendingUpgradeTarget = null;
        if (!saved)
        {
            ShowToast(MigrationMessages.UPGRADE_SAVE_FAILED);
            return;
        }
        OpenUpgradeWindow(target);
    }

    /// <summary>
    /// アップグレードの窓を開き、実行したなら開いているシーンをディスクから読み直す（レビュー #8）。
    /// </summary>
    /// <param name="target">アップグレード対象（<see cref="UpgradeTargetPath"/> の値）。</param>
    private void OpenUpgradeWindow(string target)
    {
        var window = new ProjectUpgradeWindow(target, AssetsPath) { Owner = this };
        // 一括アップグレードは .actor をまとめて書き換える（中身は同じで形式の版だけが上がる）。
        // プレハブの外部変更の監視がそれを拾ってプレハブごとに再展開（Undo・トースト・未保存の印）を
        // 送らないよう、ダイアログの間（と終わった直後の余韻）は抑止する（MainWindow.PrefabAutoReload.cs）。
        _prefabAutoReloader?.BeginSuppression();
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _prefabAutoReloader?.EndSuppression();
        }

        // ── 実行したなら、貼り直した prefab_hash を取り込むため開いているシーンを読み直す ──
        //   未保存の編集（「破棄して」を選んだもの）はここで捨てる。シーンの自動再読込が先に読み直していても、
        //   同じ内容を読み直すだけなので害は無い（自動再読込は未保存なら見送るので、こちらが確実に読む）。
        if (UpgradeUnsavedPolicy.ShouldReloadScene(window.Executed, _currentScenePath is not null))
        {
            _sceneAutoReloader?.ForceReload();
            ShowToast(MigrationMessages.UPGRADE_SCENE_RELOADED);
        }
    }

    /// <summary>
    /// 一括アップグレードへ渡すパスを決める。
    ///
    /// <para>
    /// ランタイムは <c>.seedproj</c> / プロジェクトフォルダ / assets ルートのどれでも
    /// 受け付ける。ここでは <c>.seedproj</c> を優先し、無ければ assets ルートを渡す
    /// （アセットフォルダの場所が <c>.seedproj</c> で変えられているため、
    ///  プロジェクトフォルダを渡すと既定の <c>assets/</c> を見に行ってしまう）。
    /// </para>
    /// </summary>
    /// <returns>アップグレード対象のパス。プロジェクトが無ければ空文字。</returns>
    private static string UpgradeTargetPath()
    {
        var projectFile = SEEDEditor.Project.ProjectContext.Paths?.ProjectFilePath;
        if (!string.IsNullOrWhiteSpace(projectFile)) return projectFile;
        return SEEDEditor.Project.ProjectContext.AssetsDir;
    }
}
