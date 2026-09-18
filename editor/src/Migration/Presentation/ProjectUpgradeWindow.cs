// ============================================================
//  ProjectUpgradeWindow.cs — 「プロジェクトの形式をアップグレード」ダイアログ
//
//  【役割】
//  ツールメニューから開き、次の 2 段で進める。
//    1. 開いた直後に **--dry-run** を実行し、「何が何件書き換わるか」を見せる
//       （この時点では 1 バイトも書き込まない）
//    2. 利用者が「アップグレードを実行」を押したら、
//       ロックの一括確認 → 実行 → 結果表示 → VCS パネルの取り直し
//
//  【なぜ先に dry-run を見せるのか】
//  この操作はプロジェクト内のアセットを一斉に書き換える。差分が数十ファイルになるので、
//  「何が変わるのか分からないまま押す」状態を作らない（docs/asset_migration.md 6.5 (3)）。
//
//  【ロックの確認を実行の直前に行う理由】
//  下調べから実行までのあいだに、他の人がロックを掛けていることがある。
//  実行の直前に 1 回だけまとめて照会する（LockGatekeeper.DecideForBulkWriteAsync）。
//
//  【XAML を使わない理由】
//  docs/editor_ui_style.md の方針どおり、入力欄とボタンが数個の画面は
//  Theme/SeedDialogTheme.cs を使ってコードで組む（StaticResource の綴り間違いが
//  ビルドで見つかるようにするため）。ボタンの色は共通スタイルが決める。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Theme;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Locking;

namespace SEEDEditor.Migration.Presentation;

/// <summary>
/// プロジェクトの一括アップグレードを下調べ →実行の 2 段で進めるダイアログ。
/// </summary>
public sealed class ProjectUpgradeWindow : Window
{
    // ── 寸法（マジックナンバーの一元化）──────────────────────

    /// <summary>ウィンドウの幅 [px]。相対パスが 1 行に収まる程度。</summary>
    private const double WINDOW_WIDTH_PX = 760;

    /// <summary>ウィンドウの高さ [px]。</summary>
    private const double WINDOW_HEIGHT_PX = 560;

    /// <summary>見出しの文字サイズ。</summary>
    private const double HEADING_FONT_SIZE = 12;

    /// <summary>一覧の行の文字サイズ。</summary>
    private const double LIST_FONT_SIZE = 11;

    /// <summary>節（見出し＋一覧）の上の余白 [px]。</summary>
    private const double SECTION_TOP_MARGIN_PX = 12;

    /// <summary>一覧の行の左の字下げ [px]。</summary>
    private const double LIST_INDENT_PX = 12;

    /// <summary>ボタン列の上の余白 [px]。</summary>
    private const double BUTTON_ROW_TOP_MARGIN_PX = 12;

    /// <summary>
    /// 一覧に並べる行数の上限。
    /// 数百件を全部積むとウィンドウの生成が目に見えて遅くなるため、
    /// 超えたぶんは「ほか N 件」の 1 行にまとめる（全件はログに残る）。
    /// </summary>
    private const int MAX_LIST_ROWS = 200;

    /// <summary>行数が上限を超えたときの締めの 1 行（書式: 残り件数）。</summary>
    private const string MORE_ROWS_FORMAT = "…ほか {0} 件";

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>アップグレード対象のプロジェクト（.seedproj かプロジェクトフォルダ）。</summary>
    private readonly string _projectPath;

    /// <summary>レポートの相対パスを絶対パスへ戻すときの基準（＝アセットルートの親）。</summary>
    private readonly string _pathBase;

    /// <summary>結果を積むパネル。</summary>
    private readonly StackPanel _results = new();

    /// <summary>いま何をしているかを出す 1 行。</summary>
    private readonly TextBlock _status;

    /// <summary>「アップグレードを実行」ボタン。</summary>
    private readonly Button _runButton;

    /// <summary>「閉じる」ボタン。</summary>
    private readonly Button _closeButton;

    /// <summary>下調べで得た「書き換える予定のファイル」の絶対パス。</summary>
    private readonly List<string> _plannedPaths = new();

    /// <summary>実行中か（二重起動と、実行中の×閉じを防ぐ）。</summary>
    private bool _busy;

    // ── 生成 ─────────────────────────────────────────────────

    /// <summary>
    /// ダイアログを作る。
    /// </summary>
    /// <param name="projectPath">
    /// アップグレード対象（<c>.seedproj</c> / プロジェクトフォルダ / assets ルート）。
    /// </param>
    /// <param name="assetsRoot">
    /// アセットルートの絶対パス。レポートの相対パス（<c>assets/…</c>）を
    /// 絶対パスへ戻すために使う（基準はこのフォルダの**親**）。
    /// </param>
    public ProjectUpgradeWindow(string projectPath, string assetsRoot)
    {
        _projectPath = projectPath ?? string.Empty;
        _pathBase    = ResolvePathBase(assetsRoot);

        SeedDialogTheme.ApplyWindowChrome(
            this, MigrationMessages.UPGRADE_WINDOW_TITLE, WINDOW_WIDTH_PX, WINDOW_HEIGHT_PX);
        // 一覧を持つ画面なので、共通設定のあとで大きさを変えられるようにする。
        ResizeMode = ResizeMode.CanResize;

        _status      = SeedDialogTheme.NewLabel(MigrationMessages.UPGRADE_SCANNING,
                                                SeedDialogTheme.DimText, HEADING_FONT_SIZE,
                                                topMargin: SeedDialogTheme.ROW_SPACING_PX);
        _runButton   = SeedDialogTheme.NewButton(
            MigrationMessages.UPGRADE_RUN_BUTTON, OnRunClicked, isPrimary: true);
        _closeButton = SeedDialogTheme.NewButton(
            MigrationMessages.UPGRADE_CLOSE_BUTTON, (_, _) => Close(),
            leftMargin: SeedDialogTheme.BUTTON_GAP_PX);

        Content = BuildLayout();

        // 下調べが終わるまで実行はできない。
        _runButton.IsEnabled = false;

        Loaded  += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    /// <summary>画面全体の骨組みを組む。</summary>
    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(SeedDialogTheme.CONTENT_PADDING_PX) };

        // 上: 説明と状態
        var header = new StackPanel();
        header.Children.Add(SeedDialogTheme.NewLabel(MigrationMessages.UPGRADE_INTRO));
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // 下: ボタン列（右寄せ）
        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, BUTTON_ROW_TOP_MARGIN_PX, 0, 0),
        };
        buttons.Children.Add(_runButton);
        buttons.Children.Add(_closeButton);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        // 中央: 結果の一覧
        root.Children.Add(new ScrollViewer
        {
            Content                       = _results,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin                        = new Thickness(0, SECTION_TOP_MARGIN_PX, 0, 0),
        });

        return root;
    }

    // ── 下調べ（dry-run）─────────────────────────────────────

    /// <summary>ウィンドウが出たら、すぐ下調べを始める。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        => await RunDryRunAsync().ConfigureAwait(true);

    /// <summary>実行中はウィンドウを閉じさせない（書き込みの途中で親を失わせない）。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) e.Cancel = true;
    }

    /// <summary>--dry-run を実行し、結果を画面へ並べる。</summary>
    private async Task RunDryRunAsync()
    {
        BeginBusy(MigrationMessages.UPGRADE_SCANNING);

        var result = await ProjectUpgradeRunner
            .RunAsync(AssetMigrationGateway.ResolveRuntimeExePath(), _projectPath, dryRun: true)
            .ConfigureAwait(true);

        EndBusy();

        if (!result.HasReport)
        {
            ShowRuntimeFailure(result);
            return;
        }

        _plannedPaths.Clear();
        foreach (var file in result.Report.UpgradedFiles)
            _plannedPaths.Add(ToAbsolutePath(file.Path));

        RenderReport(result.Report, isDryRun: true);

        // 書き換えるものが無ければ実行ボタンは出さない（押しても何も起きないため）。
        _runButton.IsEnabled = _plannedPaths.Count > 0;
        SetStatus(
            _plannedPaths.Count > 0
                ? string.Format(CultureInfo.InvariantCulture,
                    MigrationMessages.UPGRADE_DRY_RUN_SUMMARY_FORMAT,
                    result.Report.Summary!.Upgraded, result.Report.Summary!.UpToDate)
                : MigrationMessages.UPGRADE_NOTHING_TO_DO,
            isError: false);
    }

    // ── 実行 ─────────────────────────────────────────────────

    /// <summary>「アップグレードを実行」が押されたとき。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnRunClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // ── 1) ロックの一括確認 ──
        // 下調べから実行までのあいだに誰かがロックを掛けていることがあるので、
        // 実行の直前に 1 回だけまとめて照会する（取りには行かない）。
        BeginBusy(MigrationMessages.UPGRADE_CHECKING_LOCKS);
        var verdict = await LockGatekeeper
            .DecideForBulkWriteAsync(_plannedPaths)
            .ConfigureAwait(true);
        LockGatekeeper.PresentBulkWrite(verdict);

        if (!verdict.CanProceed)
        {
            EndBusy();
            SetStatus(string.Format(CultureInfo.InvariantCulture,
                MigrationMessages.UPGRADE_BLOCKED_BY_LOCKS_FORMAT, verdict.Message), isError: true);
            // ロックが外れたら押し直せるようにする（下調べの結果はそのまま使える）。
            _runButton.IsEnabled = _plannedPaths.Count > 0;
            return;
        }

        // ── 2) 実行 ──
        SetStatus(MigrationMessages.UPGRADE_RUNNING, isError: false);
        var result = await ProjectUpgradeRunner
            .RunAsync(AssetMigrationGateway.ResolveRuntimeExePath(), _projectPath, dryRun: false)
            .ConfigureAwait(true);

        EndBusy();

        if (!result.HasReport)
        {
            ShowRuntimeFailure(result);
            return;
        }

        RenderReport(result.Report, isDryRun: false);

        var summary = result.Report.Summary!;
        SetStatus(
            string.Format(CultureInfo.InvariantCulture,
                MigrationMessages.UPGRADE_RESULT_SUMMARY_FORMAT,
                summary.Upgraded, summary.PrefabHashUpdated),
            isError: summary.HasProblem);

        // ── 3) VCS パネルへ反映 ──
        // 書き換えたファイルは「変更」として並ぶべきなので、状態を取り直させる。
        // .actor の prefab_hash 貼り直しで .scene も変更として出るが、これは正常。
        VersionControlService.RequestRefresh();

        // 同じ操作を続けて押せないようにする（2 回目は全件 up_to_date になるだけ）。
        _runButton.IsEnabled = false;
        _plannedPaths.Clear();
    }

    // ── 結果の描画 ───────────────────────────────────────────

    /// <summary>レポートを画面へ並べ直す。</summary>
    /// <param name="report">解釈済みのレポート。</param>
    /// <param name="isDryRun">下調べの結果か（実行後なら false）。</param>
    private void RenderReport(ProjectUpgradeReport report, bool isDryRun)
    {
        _results.Children.Clear();

        var summary = report.Summary!;

        // ── 形式ごとの内訳 ──
        var byKind = report.CountByKind(UpgradeFileStatus.Upgraded);
        if (byKind.Count > 0)
        {
            AddHeading(isDryRun ? "更新するファイルの内訳" : "更新したファイルの内訳",
                       SeedDialogTheme.Text);
            foreach (var pair in byKind)
            {
                AddListRow(string.Format(CultureInfo.InvariantCulture,
                    MigrationMessages.UPGRADE_KIND_LINE_FORMAT, pair.Key, pair.Value));
            }
        }

        // ── 対象ファイルの一覧 ──
        var upgraded = new List<UpgradeFileLine>(report.UpgradedFiles);
        if (upgraded.Count > 0)
        {
            AddHeading(isDryRun ? "対象ファイル" : "更新したファイル", SeedDialogTheme.Text);
            AddLimitedRows(upgraded.Count, index =>
            {
                var file = upgraded[index];
                return string.Format(CultureInfo.InvariantCulture,
                    MigrationMessages.UPGRADE_FILE_LINE_FORMAT, file.Path, file.From, file.To);
            });
        }

        // ── プレハブの版の貼り直し ──
        if (summary.PrefabHashScenes > 0)
        {
            AddHeading("プレハブの版（prefab_hash）の貼り直し", SeedDialogTheme.Text);
            foreach (var line in report.PrefabHashes)
            {
                AddListRow(string.Format(CultureInfo.InvariantCulture,
                    MigrationMessages.UPGRADE_PROBLEM_LINE_FORMAT,
                    line.Path, $"{line.Updated} 件"));
            }
        }

        // ── 別枠: 未来版 ──
        var future = new List<UpgradeFileLine>(report.FutureVersionFiles);
        if (future.Count > 0)
        {
            AddHeading(string.Format(CultureInfo.InvariantCulture,
                MigrationMessages.UPGRADE_FUTURE_VERSION_HEADER_FORMAT, future.Count),
                SeedDialogTheme.ErrorText);
            AddLimitedRows(future.Count, index => string.Format(CultureInfo.InvariantCulture,
                MigrationMessages.UPGRADE_PROBLEM_LINE_FORMAT,
                future[index].Path, future[index].Message));
        }

        // ── 別枠: 失敗 ──
        var failed = new List<UpgradeFileLine>(report.FailedFiles);
        if (failed.Count > 0)
        {
            AddHeading(string.Format(CultureInfo.InvariantCulture,
                MigrationMessages.UPGRADE_FAILED_HEADER_FORMAT, failed.Count),
                SeedDialogTheme.ErrorText);
            AddLimitedRows(failed.Count, index => string.Format(CultureInfo.InvariantCulture,
                MigrationMessages.UPGRADE_PROBLEM_LINE_FORMAT,
                failed[index].Path, failed[index].Message));
        }

        // ── 実行後の案内 ──
        if (!isDryRun && summary.Upgraded > 0)
        {
            AddHeading(MigrationMessages.UPGRADE_RESULT_VCS_HINT, SeedDialogTheme.DimText);
        }

        // ── 解釈できなかった行（将来の拡張・壊れた出力）──
        if (report.UnparsedLines.Count > 0 || report.Errors.Count > 0)
        {
            AddHeading("ランタイムからの追加の出力", SeedDialogTheme.DimText);
            foreach (var message in report.Errors) AddListRow(message);
            foreach (var line in report.UnparsedLines) AddListRow(line);
        }
    }

    /// <summary>ランタイムを起動できなかった／レポートが出なかったときの表示。</summary>
    /// <param name="result">実行結果。</param>
    private void ShowRuntimeFailure(ProjectUpgradeRunResult result)
    {
        _results.Children.Clear();
        _runButton.IsEnabled = false;

        var detail = result.FailureMessage.Length > 0
            ? result.FailureMessage
            : $"終了コード {result.ExitCode}";
        SetStatus(string.Format(CultureInfo.InvariantCulture,
            MigrationMessages.UPGRADE_RUNTIME_UNAVAILABLE_FORMAT, detail), isError: true);
    }

    // ── 部品 ─────────────────────────────────────────────────

    /// <summary>節の見出しを 1 行足す。</summary>
    /// <param name="text">文言。</param>
    /// <param name="brush">文字色。</param>
    private void AddHeading(string text, Brush brush)
        => _results.Children.Add(SeedDialogTheme.NewLabel(
            text, brush, HEADING_FONT_SIZE, topMargin: SECTION_TOP_MARGIN_PX));

    /// <summary>一覧の 1 行を足す。</summary>
    /// <param name="text">文言。</param>
    private void AddListRow(string text)
    {
        var label = SeedDialogTheme.NewLabel(text, SeedDialogTheme.DimText, LIST_FONT_SIZE);
        label.Margin = new Thickness(LIST_INDENT_PX, 0, 0, 0);
        _results.Children.Add(label);
    }

    /// <summary>
    /// 一覧を <see cref="MAX_LIST_ROWS"/> 行までに切り詰めて足す。
    /// </summary>
    /// <param name="count">全体の件数。</param>
    /// <param name="textAt">i 番目の行の文言を作る関数。</param>
    private void AddLimitedRows(int count, Func<int, string> textAt)
    {
        var shown = Math.Min(count, MAX_LIST_ROWS);
        for (var i = 0; i < shown; i++) AddListRow(textAt(i));
        if (count > shown)
        {
            AddListRow(string.Format(
                CultureInfo.InvariantCulture, MORE_ROWS_FORMAT, count - shown));
        }
    }

    /// <summary>状態行を書き換える。</summary>
    /// <param name="text">文言。</param>
    /// <param name="isError">エラー色にするか。</param>
    private void SetStatus(string text, bool isError)
    {
        _status.Text       = text;
        _status.Foreground = isError ? SeedDialogTheme.ErrorText : SeedDialogTheme.DimText;
    }

    /// <summary>処理中の状態にする（ボタンを止め、閉じさせない）。</summary>
    /// <param name="message">状態行に出す文言。</param>
    private void BeginBusy(string message)
    {
        _busy = true;
        _runButton.IsEnabled   = false;
        _closeButton.IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        SetStatus(message, isError: false);
    }

    /// <summary>処理中の状態を解く。</summary>
    private void EndBusy()
    {
        _busy = false;
        _closeButton.IsEnabled = true;
        Cursor = null;
    }

    // ── パス ─────────────────────────────────────────────────

    /// <summary>
    /// レポートの相対パス（<c>assets/…</c>）を絶対パスへ戻す基準を決める。
    /// ランタイムは**アセットルートの親**を基準に相対化するので、それに合わせる。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    private static string ResolvePathBase(string? assetsRoot)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot)) return string.Empty;
        try { return Directory.GetParent(Path.GetFullPath(assetsRoot))?.FullName ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    /// <summary>レポートの相対パスを絶対パスへ戻す（戻せなければそのまま返す）。</summary>
    /// <param name="reportPath">レポートに出ていた相対パス（スラッシュ区切り）。</param>
    private string ToAbsolutePath(string reportPath)
    {
        if (_pathBase.Length == 0) return reportPath;
        try { return Path.GetFullPath(Path.Combine(_pathBase, reportPath)); }
        catch (Exception) { return reportPath; }
    }
}
