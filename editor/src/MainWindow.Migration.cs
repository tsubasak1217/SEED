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

        var window = new ProjectUpgradeWindow(target, AssetsPath) { Owner = this };
        window.ShowDialog();
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
