// ============================================================
//  TemplateImportWindow.xaml.cs — テンプレートインポート画面の制御
//
//  【役割】
//  ライブラリの走査結果（TemplateLibrary）をツリーへ並べ、
//  チェックされたエントリから計画（TemplateImporter.CreatePlan）を作って要約を見せ、
//  実行（TemplateImporter.Execute）を呼ぶ。判断のロジックはすべて
//  Templates/ の非 UI クラス側にあり、ここは「見せる・聞く・呼ぶ」だけを行う。
//
//  【重い処理はすべて UI スレッドの外へ】
//  ライブラリは数百 MB 規模（地形・フォント）になりうるので、
//  閉包計算とコピーは Task.Run でバックグラウンドに出す。
//  閉包計算はチェックのたびに走らせず、短い待ち時間でまとめる（デバウンス）。
//
//  【MainWindow への組み込み】
//  このウィンドウは MainWindow を知らない。静的入口 ShowFor() に
//  「ライブラリのルート」「プロジェクトのアセットルート」を渡して開く。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SEEDEditor.Controls;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Templates;

/// <summary>
/// テンプレートライブラリから、選んだテンプレートを依存ごとプロジェクトへコピーするダイアログ。
/// </summary>
public partial class TemplateImportWindow : Window
{
    // ============================================================
    //  定数
    // ============================================================

    /// <summary>ダークタイトルバーを有効にする DWM 属性 ID。</summary>
    private const int DwmUseImmersiveDarkMode = 20;

    /// <summary>
    /// チェック変更から閉包計算を始めるまでの待ち時間（ミリ秒）。
    /// カテゴリ単位のチェックで数十回連続して変更通知が飛ぶため、まとめてから 1 回だけ計算する。
    /// </summary>
    private const int PlanDebounceMilliseconds = 180;

    /// <summary>右ペインに並べる衝突ファイルの最大表示件数（超過分は件数だけ出す）。</summary>
    private const int ConflictListLimit = 30;

    /// <summary>右ペインに並べる欠落参照の最大表示件数。</summary>
    private const int MissingListLimit = 20;

    // ============================================================
    //  状態
    // ============================================================

    /// <summary>走査済みのテンプレートライブラリ。</summary>
    private readonly TemplateLibrary _library;

    /// <summary>コピー先のプロジェクトアセットルート（絶対パス）。</summary>
    private readonly string _assetsRoot;

    /// <summary>ツリーの根（カテゴリノード）。</summary>
    private readonly ObservableCollection<TemplateTreeNode> _rootNodes = [];

    /// <summary>チェック変更をまとめてから計算するためのタイマー。</summary>
    private readonly DispatcherTimer _planTimer;

    /// <summary>
    /// 直近の計画。インポート実行時にそのまま使う。
    /// 選択が空のときは null。
    /// </summary>
    private TemplateImportPlan? _plan;

    /// <summary>
    /// 計画計算の世代番号。計算はバックグラウンドで走るため、
    /// 古い計算の結果が新しい選択を上書きしないよう、開始時の番号と突き合わせて捨てる。
    /// </summary>
    private int _planGeneration;

    /// <summary>インポート実行中なら true（多重実行と閉じる操作を止める）。</summary>
    private bool _isImporting;

    /// <summary>インポートの結果（未実行なら null）。<see cref="ShowFor"/> の戻り値になる。</summary>
    public TemplateImportResult? ImportResult { get; private set; }

    // ============================================================
    //  構築
    // ============================================================

    /// <summary>
    /// ライブラリとコピー先を指定してウィンドウを作る。
    /// </summary>
    /// <param name="libraryRoot">テンプレートライブラリのルート（絶対パス）。</param>
    /// <param name="assetsRoot">コピー先のプロジェクトアセットルート（絶対パス）。</param>
    public TemplateImportWindow(string libraryRoot, string assetsRoot)
    {
        InitializeComponent();

        _library    = TemplateLibrary.Load(libraryRoot);
        _assetsRoot = Path.GetFullPath(assetsRoot);

        // チェック変更をまとめるためのワンショットタイマー
        _planTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(PlanDebounceMilliseconds),
        };
        _planTimer.Tick += OnPlanTimerTick;

        BuildTree();
        UpdatePathLabels();
        UpdateOverwriteHint();
        RequestPlanUpdate();
    }

    /// <summary>
    /// インポート画面を開く静的入口。
    ///
    /// <para>
    /// 呼び出し側（MainWindow 等）はライブラリの場所解決を
    /// <see cref="TemplateLibraryLocator"/> に任せ、その結果とアセットルートを渡すだけでよい。
    /// </para>
    /// </summary>
    /// <param name="owner">親ウィンドウ。</param>
    /// <param name="libraryRoot">テンプレートライブラリのルート。</param>
    /// <param name="assetsRoot">コピー先のプロジェクトアセットルート。</param>
    /// <returns>
    /// インポートを実行した場合はその結果。キャンセルされた（1 件もコピーしなかった）場合は null。
    /// 呼び出し側はこれを見てファイル一覧の再読み込みを判断する。
    /// </returns>
    public static TemplateImportResult? ShowFor(Window owner, string libraryRoot, string assetsRoot)
    {
        var window = new TemplateImportWindow(libraryRoot, assetsRoot) { Owner = owner };
        window.ShowDialog();
        return window.ImportResult;
    }

    /// <summary>ウィンドウ枠をダークテーマに合わせる（他ダイアログと同じ扱い）。</summary>
    /// <param name="hwnd">ウィンドウハンドル。</param>
    /// <param name="attr">DWM 属性 ID。</param>
    /// <param name="value">設定値。</param>
    /// <param name="size">設定値のバイト数。</param>
    /// <returns>HRESULT。</returns>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>表示直後の初期化（タイトルバーの配色と初期フォーカス）。</summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        int dark = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                              DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        TxtSearch.Focus();
    }

    // ============================================================
    //  ツリーの構築
    // ============================================================

    /// <summary>走査結果からツリーを組み立てる。</summary>
    private void BuildTree()
    {
        foreach (var category in _library.Categories)
        {
            // カテゴリノード（アイコンはフォルダ。空フォルダは別絵になる）
            var categoryNode = TemplateTreeNode.CreateCategory(
                category,
                FileTypeIcons.GetFolderImage(category.Entries.Count == 0),
                RequestPlanUpdate);

            foreach (var entry in category.Entries)
            {
                var icon = entry.IsFolder
                    ? FileTypeIcons.GetFolderImage(isEmpty: entry.FileCount == 0)
                    : FileTypeIcons.GetImage(Path.GetExtension(entry.DisplayName));
                categoryNode.AddChild(TemplateTreeNode.CreateEntry(entry, icon, RequestPlanUpdate));
            }

            // 最初はカテゴリを畳んでおく（カテゴリ数より項目数の方がずっと多いため）
            categoryNode.IsExpanded = false;
            _rootNodes.Add(categoryNode);
        }

        TreeCategories.ItemsSource = _rootNodes;

        LblLibraryState.Text = _library.IsEmpty
            ? "テンプレートが見つかりません（ライブラリの場所を確認してください）"
            : $"{_library.Categories.Count} カテゴリ / " +
              $"{_library.Categories.Sum(c => c.Entries.Count)} 項目";
    }

    /// <summary>ライブラリとコピー先のパス表示を更新する。</summary>
    private void UpdatePathLabels()
    {
        LblLibraryPath.Text = $"ライブラリ: {_library.Root}";
        LblAssetsPath.Text  = $"コピー先:  {_assetsRoot}";
    }

    // ============================================================
    //  検索（表示の絞り込み）
    // ============================================================

    /// <summary>検索文字列の変更で表示を絞り込む。</summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter(TxtSearch.Text.Trim());

    /// <summary>
    /// 検索語でツリーの表示・非表示を切り替える。
    /// カテゴリは「名前が一致する」か「一致する子を持つ」なら表示し、一致時は自動で展開する。
    /// チェック状態には触れない（隠れた選択が黙って消えないようにするため）。
    /// </summary>
    /// <param name="query">検索語（空なら全表示）。</param>
    private void ApplyFilter(string query)
    {
        bool showAll = query.Length == 0;

        foreach (var category in _rootNodes)
        {
            bool categoryMatches = showAll || Matches(category.DisplayName, query);
            bool anyChildVisible = false;

            foreach (var child in category.Children)
            {
                bool visible = showAll || categoryMatches || Matches(child.DisplayName, query);
                child.IsVisible = visible;
                anyChildVisible |= visible;
            }

            category.IsVisible = showAll || categoryMatches || anyChildVisible;

            // 検索中は一致した枝を開いて見せる。検索語を消したら畳んだ状態へ戻す。
            category.IsExpanded = !showAll && category.IsVisible;
        }
    }

    /// <summary>表示名が検索語を含むか（大文字小文字を無視）。</summary>
    /// <param name="text">照合対象。</param>
    /// <param name="query">検索語。</param>
    /// <returns>含んでいれば true。</returns>
    private static bool Matches(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

    // ============================================================
    //  計画の再計算
    // ============================================================

    /// <summary>
    /// チェック変更をまとめるため、再計算を予約する。
    /// 選択が変わった時点で直前のインポート結果は古い情報になるので、結果表示は畳む
    /// （インポート直後の自動再計算はこの経路を通らないため、完了表示は残る）。
    /// </summary>
    private void RequestPlanUpdate()
    {
        if (_isImporting) return;
        PanelResult.Visibility = Visibility.Collapsed;
        _planTimer.Stop();
        _planTimer.Start();
        LblStatus.Text = "選択内容を計算しています…";
    }

    /// <summary>デバウンス待ちが明けたら再計算を始める。</summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnPlanTimerTick(object? sender, EventArgs e)
    {
        _planTimer.Stop();
        await UpdatePlanAsync();
    }

    /// <summary>
    /// チェックされたエントリから計画を作り直し、要約表示を更新する。
    /// 閉包計算はディスク走査を伴うのでバックグラウンドで行う。
    /// </summary>
    /// <returns>完了を表すタスク。</returns>
    private async Task UpdatePlanAsync()
    {
        // ── 選択を集める ───────────────────────────────────────
        var selected = new List<string>();
        foreach (var node in _rootNodes) node.CollectCheckedEntries(selected);

        if (selected.Count == 0)
        {
            _plan = null;
            ShowEmptySummary();
            return;
        }

        // ── バックグラウンドで閉包を取る ───────────────────────
        int generation   = ++_planGeneration;
        var libraryRoot  = _library.Root;
        var assetsRoot   = _assetsRoot;
        var plan = await Task.Run(() =>
            TemplateImporter.CreatePlan(libraryRoot, assetsRoot, selected));

        // 計算中に選択が変わっていたら、この結果は捨てる
        if (generation != _planGeneration) return;

        _plan = plan;
        ShowSummary(plan);
    }

    /// <summary>選択が空のときの要約表示。</summary>
    private void ShowEmptySummary()
    {
        LblSelectedCount.Text = "選択: 0 件";
        LblFileCount.Text     = "コピーするファイル: 0";
        LblTotalSize.Text     = "合計サイズ: " + ByteSizeText.Format(0);
        PanelConflicts.Visibility = Visibility.Collapsed;
        PanelMissing.Visibility   = Visibility.Collapsed;
        BtnImport.IsEnabled       = false;
        SetIdleStatus("取り込みたいテンプレートにチェックを入れてください。");
        UpdateOverwriteHint();
    }

    /// <summary>計画の内容を右ペインへ反映する。</summary>
    /// <param name="plan">表示する計画。</param>
    private void ShowSummary(TemplateImportPlan plan)
    {
        LblSelectedCount.Text = $"選択: {plan.SelectedEntryPaths.Count} 件";
        LblFileCount.Text     = $"コピーするファイル: {plan.FileCount}";
        LblTotalSize.Text     = "合計サイズ: " + ByteSizeText.Format(plan.TotalBytes);

        // ── 衝突（コピー先に同名ファイルがある） ────────────────
        var conflicts = plan.ConflictPaths;
        if (conflicts.Count > 0)
        {
            LblConflictHeader.Text = $"既存ファイルと重複: {conflicts.Count} 件";
            ListConflicts.ItemsSource = BuildLimitedList(conflicts, ConflictListLimit);
            PanelConflicts.Visibility = Visibility.Visible;
        }
        else
        {
            PanelConflicts.Visibility = Visibility.Collapsed;
        }

        // ── ライブラリ内で解決できなかった参照 ──────────────────
        if (plan.MissingReferences.Count > 0)
        {
            LblMissingHeader.Text = $"参照先が見つかりません: {plan.MissingReferences.Count} 件";
            var lines = plan.MissingReferences
                .Select(m => $"{m.ReferencePath}  ← {m.SourceRelPath}")
                .ToList();
            ListMissing.ItemsSource = BuildLimitedList(lines, MissingListLimit);
            PanelMissing.Visibility = Visibility.Visible;
        }
        else
        {
            PanelMissing.Visibility = Visibility.Collapsed;
        }

        BtnImport.IsEnabled = plan.FileCount > 0;
        SetIdleStatus("");
        UpdateOverwriteHint();
    }

    /// <summary>
    /// 待機中の状態メッセージを書く。
    /// インポート結果を表示している間は、直前の完了メッセージを消さないよう何もしない
    /// （コピー後の再計算が完了メッセージを上書きしてしまうのを防ぐ）。
    /// </summary>
    /// <param name="message">表示するメッセージ。</param>
    private void SetIdleStatus(string message)
    {
        if (PanelResult.Visibility == Visibility.Visible) return;
        LblStatus.Text = message;
    }

    /// <summary>一覧を上限まで切り詰め、超過分は「ほか N 件」の 1 行にまとめる。</summary>
    /// <param name="source">元の一覧。</param>
    /// <param name="limit">表示する最大件数。</param>
    /// <returns>表示用の一覧。</returns>
    private static List<string> BuildLimitedList(IReadOnlyList<string> source, int limit)
    {
        var list = new List<string>(Math.Min(source.Count, limit) + 1);
        for (int i = 0; i < source.Count && i < limit; i++) list.Add(source[i]);
        if (source.Count > limit) list.Add($"…ほか {source.Count - limit} 件");
        return list;
    }

    /// <summary>衝突方針の説明文を、現在の選択に合わせて書き換える。</summary>
    private void UpdateOverwriteHint()
    {
        int conflicts = _plan?.ConflictCount ?? 0;
        if (conflicts == 0)
        {
            LblOverwriteHint.Text = "重複するファイルはありません。";
            return;
        }

        LblOverwriteHint.Text = ChkOverwrite.IsChecked == true
            ? $"重複する {conflicts} 件をライブラリの内容で置き換えます。"
            : $"重複する {conflicts} 件はコピーせず、現在のファイルを残します。";
    }

    /// <summary>上書きチェックの変更で説明文を更新する。</summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnOverwriteChanged(object sender, RoutedEventArgs e) => UpdateOverwriteHint();

    /// <summary>選択をすべて解除する。</summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var node in _rootNodes) node.ClearChecks();
        RequestPlanUpdate();
    }

    // ============================================================
    //  インポートの実行
    // ============================================================

    /// <summary>計画に従ってコピーを実行する。</summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (_isImporting) return;

        // 待ち時間中でまだ反映されていない選択があれば、先に確定させる
        if (_planTimer.IsEnabled)
        {
            _planTimer.Stop();
            await UpdatePlanAsync();
        }

        var plan = _plan;
        if (plan is null || plan.FileCount == 0) return;

        var policy = ChkOverwrite.IsChecked == true
            ? TemplateImportConflictPolicy.Overwrite
            : TemplateImportConflictPolicy.Skip;

        SetImportingState(true);
        var result = await Task.Run(() => TemplateImporter.Execute(plan, policy));
        SetImportingState(false);

        // 続けて別のテンプレートを入れられるので、結果は累積して持つ
        ImportResult = Merge(ImportResult, result);
        ShowResult(result);
    }

    /// <summary>
    /// 同一ウィンドウ内で複数回インポートしたときのために、結果を足し合わせる。
    /// 欠落参照は最後の計画のものを残す（画面に出ているのが最新の状態のため）。
    /// </summary>
    /// <param name="previous">これまでの累計（初回は null）。</param>
    /// <param name="latest">今回の結果。</param>
    /// <returns>累計した結果。</returns>
    private static TemplateImportResult Merge(TemplateImportResult? previous, TemplateImportResult latest)
    {
        if (previous is null) return latest;

        var failures = new List<TemplateCopyFailure>(previous.Failures);
        failures.AddRange(latest.Failures);

        return new TemplateImportResult
        {
            CopiedCount       = previous.CopiedCount      + latest.CopiedCount,
            OverwrittenCount  = previous.OverwrittenCount + latest.OverwrittenCount,
            SkippedCount      = previous.SkippedCount     + latest.SkippedCount,
            CopiedBytes       = previous.CopiedBytes      + latest.CopiedBytes,
            Failures          = failures,
            MissingReferences = latest.MissingReferences,
        };
    }

    /// <summary>実行中の UI 状態を切り替える。</summary>
    /// <param name="importing">実行中なら true。</param>
    private void SetImportingState(bool importing)
    {
        _isImporting            = importing;
        BtnImport.IsEnabled     = !importing;
        BtnCancel.IsEnabled     = !importing;
        TreeCategories.IsEnabled = !importing;
        LblStatus.Text          = importing ? "インポート中…" : "";
    }

    /// <summary>インポート結果を右ペインへ表示する。</summary>
    /// <param name="result">実行結果。</param>
    private void ShowResult(TemplateImportResult result)
    {
        var lines = new List<string>
        {
            $"コピー: {result.CopiedCount} 件（{ByteSizeText.Format(result.CopiedBytes)}）",
        };
        if (result.OverwrittenCount > 0) lines.Add($"うち上書き: {result.OverwrittenCount} 件");
        if (result.SkippedCount > 0)     lines.Add($"重複のためスキップ: {result.SkippedCount} 件");
        if (result.Failures.Count > 0)   lines.Add($"失敗: {result.Failures.Count} 件");
        foreach (var f in result.Failures.Take(MissingListLimit))
            lines.Add($"  {f.RelPath}: {f.Message}");

        LblResult.Text         = string.Join(Environment.NewLine, lines);
        PanelResult.Visibility = Visibility.Visible;

        // コピー済みのファイルは「既存」になるので、衝突表示を現状に合わせ直す。
        // ウィンドウは開いたままにしてあり、続けて別のテンプレートを選べる。
        _ = UpdatePlanAsync();

        // 閉じる操作はキャンセルボタンが兼ねる（Esc でも閉じられる）
        BtnCancel.Content = "閉じる";
        LblStatus.Text    = result.Failures.Count == 0
            ? "インポートが完了しました。続けて選ぶこともできます。"
            : "一部のファイルをコピーできませんでした。";
    }

    /// <summary>画面を閉じる（IsCancel=True のため Esc でも呼ばれる）。</summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>実行中は閉じさせない（コピー途中でウィンドウが消えるのを防ぐ）。</summary>
    /// <param name="e">イベント引数。</param>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isImporting) e.Cancel = true;
        base.OnClosing(e);
    }
}
