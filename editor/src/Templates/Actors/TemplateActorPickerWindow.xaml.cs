// ============================================================
//  TemplateActorPickerWindow.xaml.cs — テンプレートアクタを選んで追加する窓
//
//  【役割】
//  エンジン側のテンプレートアクタ（UI 部品など、コンポーネントとスクリプトが付いた状態のアクタ）を
//  カテゴリと検索で選び、「まっさらなアクタ」としてシーンへ入れる。
//   - 開き方: ヒエラルキー／シーンビューの右クリック「アクタを追加」→「テンプレートアクタ...」
//     （MainWindow.TemplateActors.cs が ShowFor を呼ぶ。窓は 1 つだけで、2 回目以降は追加先を差し替える）
//   - 追加: TemplateActorInstaller で準備（まっさらにする・依存ファイルのコピー・一時ファイル）→
//     ADD_TEMPLATE_ACTOR をランタイムへ送る。ランタイムが木へ入れ、選択し、Undo を 1 件積む。
//     手順そのものは TemplateActorAddFlow（MCP の seed_template_actor と共通）にあり、窓は結果を出すだけ。
//   - モードレス。追加しても閉じないので続けて選べる。状態の行に「追加しました: 名前」を出す。
//
//  【重い処理は UI スレッドの外】
//  カタログの読み込み（テンプレートを 1 つずつ開いて 2D/3D を見る）と、追加の準備
//  （参照の閉包でライブラリ全体の索引を作る・ファイルのコピー）は Task.Run で行う。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Theme;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// テンプレートアクタを選んでシーンへ追加するモードレスの窓。
/// </summary>
public partial class TemplateActorPickerWindow : Window
{
    // ============================================================
    //  見た目の寸法（XAML から x:Static で参照する）
    // ============================================================

    /// <summary>一覧のサムネイルの枠の一辺（px）。画像は推奨 192×192 をここへ縮めて出す。</summary>
    public const double ThumbnailDisplaySize = 64;

    /// <summary>画像が無いときに枠へ置く頭文字の大きさ（枠の一辺のおよそ 4 割）。</summary>
    public const double PlaceholderInitialFontSize = 26;

    /// <summary>説明文の表示の高さの上限（11pt でおよそ 2 行。続きは行のツールチップで読む）。</summary>
    public const double DescriptionMaxHeight = 30;

    /// <summary>頭文字の板の地色。</summary>
    public static readonly Brush PlaceholderBackground = Frozen(SeedThemeColors.ThumbnailPlaceholderBg);

    /// <summary>頭文字の色。</summary>
    public static readonly Brush PlaceholderForeground = Frozen(SeedThemeColors.ThumbnailPlaceholderFg);

    // ============================================================
    //  文言・表示の上限
    // ============================================================

    /// <summary>左の木の先頭の「すべて」の行の名前。</summary>
    private const string AllCategoriesName = "すべて";

    /// <summary>状態の行の区切り。</summary>
    private const string StatusSeparator = "　／　";

    /// <summary>状態の行に並べるファイル名の上限（超えた分は「ほか n 件」。全文はツールチップ）。</summary>
    private const int StatusListLimit = 3;

    /// <summary>ログ（Output パネル）の行の頭に付ける印。</summary>
    private const string LogPrefix = "[テンプレートアクタ]";

    /// <summary>状態の行の種類（色の選び分け）。</summary>
    private enum StatusKind
    {
        /// <summary>案内・進行中（補足の色）。</summary>
        Info,
        /// <summary>追加できた（成功の色）。</summary>
        Success,
        /// <summary>追加できない・欠けたものがある（エラーの色）。</summary>
        Error,
    }

    // ============================================================
    //  状態
    // ============================================================

    /// <summary>開いている窓（1 つだけにする。閉じたら null）。</summary>
    private static TemplateActorPickerWindow? _instance;

    /// <summary>外へ頼むこと（MainWindow が詰めて渡す）。</summary>
    private readonly TemplateActorPickerContext _context;

    /// <summary>いまの追加先（右クリックした場所）。</summary>
    private TemplateActorTarget _target;

    /// <summary>カタログの全エントリの表示モデル（カタログの並び）。</summary>
    private List<TemplateActorListItem> _allItems = [];

    /// <summary>各行の検索対象の文字（正規化済み。入力のたびに作り直さない）。</summary>
    private readonly Dictionary<TemplateActorListItem, string> _haystacks = [];

    /// <summary>左の木の行（「すべて」＋ 1 段目。2 段目は各行の Children）。</summary>
    private readonly ObservableCollection<TemplateActorCategoryItem> _categoryItems = [];

    /// <summary>左の木の全行（件数の数え直し用。「すべて」を含む）。</summary>
    private readonly List<TemplateActorCategoryItem> _allCategoryItems = [];

    /// <summary>選択中のカテゴリ（null は「すべて」）。</summary>
    private string? _categoryKey;

    /// <summary>追加の準備中か（二重に押されないようにする）。</summary>
    private bool _busy;

    // ============================================================
    //  開き方
    // ============================================================

    /// <summary>
    /// テンプレートアクタの窓を開く。既に開いていれば追加先を差し替えて前へ出す。
    /// </summary>
    /// <param name="owner">持ち主の窓（エディタの主窓）。</param>
    /// <param name="context">外へ頼むこと。</param>
    /// <param name="target">追加先（右クリックした場所）。</param>
    public static void ShowFor(Window owner, TemplateActorPickerContext context, TemplateActorTarget target)
    {
        if (_instance is { } existing)
        {
            existing.SetTarget(target);
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            existing.TxtSearch.Focus();
            return;
        }

        var window = new TemplateActorPickerWindow(context, target) { Owner = owner };
        _instance = window;
        window.Show();
    }

    /// <summary>窓を作る（開くのは <see cref="ShowFor"/> から）。</summary>
    /// <param name="context">外へ頼むこと。</param>
    /// <param name="target">追加先。</param>
    private TemplateActorPickerWindow(TemplateActorPickerContext context, TemplateActorTarget target)
    {
        InitializeComponent();
        _context = context;
        _target  = target;
        TreeCategories.ItemsSource = _categoryItems;
        UpdateTargetLabel();
    }

    // ============================================================
    //  検証用（editor/tests/TemplateActorPickerPreviewProbe。エディタからは使わない）
    // ============================================================

    /// <summary>
    /// 表示せずに窓を作る（オフスクリーン描画のプローブが、エディタを起動せずに
    /// 実物の XAML・共通書式・テンプレートの解決を確かめるため）。
    /// </summary>
    /// <param name="context">外へ頼むこと（プローブは偽物を渡す）。</param>
    /// <param name="target">追加先。</param>
    /// <returns>表示していない窓。</returns>
    public static TemplateActorPickerWindow CreateForVerification(
        TemplateActorPickerContext context, TemplateActorTarget target) => new(context, target);

    /// <summary>
    /// カタログを同期で当てる（Loaded の裏の読み込みの代わり。プローブ用）。
    /// </summary>
    /// <param name="catalog">読み込んだカタログ。</param>
    public void ApplyCatalogForVerification(TemplateActorCatalog catalog) => ApplyCatalog(catalog);

    /// <summary>追加先を差し替える（窓を開いたまま別の場所を右クリックしたとき）。</summary>
    /// <param name="target">新しい追加先。</param>
    private void SetTarget(TemplateActorTarget target)
    {
        _target = target;
        UpdateTargetLabel();
        UpdateAddButton();
        // 選んでいるテンプレートを新しい追加先へ入れられないなら、切り替えの知らせより理由を優先して出す
        if (!ShowRejectionOfSelection())
            SetStatus($"追加先を切り替えました: {target.Describe()}", StatusKind.Info);
    }

    // ============================================================
    //  読み込み
    // ============================================================

    /// <summary>窓が出たらカタログを裏で読み、一覧を作る。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        TxtSearch.Focus();
        SetStatus("テンプレートアクタを読み込んでいます…", StatusKind.Info);

        TemplateActorCatalog catalog;
        try
        {
            var root = _context.LibraryRoot;
            catalog = await Task.Run(() => TemplateActorCatalog.Load(root));
        }
        catch (Exception ex)
        {
            SetStatus($"テンプレートアクタを読み込めませんでした: {ex.Message}", StatusKind.Error);
            return;
        }
        ApplyCatalog(catalog);
    }

    /// <summary>読み込んだカタログを左の木と一覧へ反映する。</summary>
    /// <param name="catalog">読み込んだカタログ。</param>
    private void ApplyCatalog(TemplateActorCatalog catalog)
    {
        // ── 一覧の行（サムネイルはここで読む。UI スレッド）──
        _allItems = catalog.Entries.Select(e => new TemplateActorListItem(e, catalog.LibraryRoot)).ToList();
        _haystacks.Clear();
        foreach (var item in _allItems) _haystacks[item] = TemplateActorSearch.BuildHaystack(item.Entry);

        // ── 左の木（「すべて」＋ 1 段目 → その子）──
        _categoryItems.Clear();
        _allCategoryItems.Clear();
        var all = new TemplateActorCategoryItem(key: null, AllCategoriesName) { IsSelected = true };
        _categoryItems.Add(all);
        _allCategoryItems.Add(all);
        var tops = new Dictionary<string, TemplateActorCategoryItem>(StringComparer.Ordinal);
        foreach (var category in catalog.Categories)
        {
            var node = new TemplateActorCategoryItem(category.Key, category.Name);
            _allCategoryItems.Add(node);
            if (category.ParentKey is null)
            {
                tops[category.Key] = node;
                _categoryItems.Add(node);
            }
            else if (tops.TryGetValue(category.ParentKey, out var parent))
            {
                parent.Children.Add(node);
            }
        }
        _categoryKey = null;

        // ── カタログの書き損じはログへ（状態の行には件数だけ）──
        foreach (var warning in catalog.Warnings) _context.Log?.Invoke($"{LogPrefix} {warning}");

        RefreshList();

        // 開いた時点で選んでいるものを追加先へ入れられないなら、件数より先にその理由を見せる
        if (ShowRejectionOfSelection()) return;

        var withThumbnail = _allItems.Count(i => i.HasThumbnail);
        var message = $"{_allItems.Count} 件のテンプレートアクタ（見本の画像あり {withThumbnail} 件）。" +
                      "選んで「追加」（ダブルクリック・Enter でも追加）";
        if (catalog.Warnings.Count > 0)
            message += $"{StatusSeparator}カタログの警告 {catalog.Warnings.Count} 件（Output に出しました）";
        SetStatus(message, catalog.Warnings.Count > 0 ? StatusKind.Error : StatusKind.Info);
    }

    // ============================================================
    //  絞り込み
    // ============================================================

    /// <summary>検索欄が変わるたびに絞り込む（空のときだけ案内の文字を見せる）。</summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        LblSearchHint.Visibility = TxtSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshList();
    }

    /// <summary>左の木でカテゴリを選んだら絞り込む。</summary>
    private void OnCategorySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _categoryKey = (e.NewValue as TemplateActorCategoryItem)?.Key;
        RefreshList();
    }

    /// <summary>
    /// 検索語とカテゴリで一覧を作り直し、左の木の件数を数え直す。
    /// 選んでいた行がまだ見えていれば選択を保つ。
    /// </summary>
    private void RefreshList()
    {
        var terms   = TemplateActorSearch.SplitTerms(TxtSearch.Text);
        var matched = _allItems.Where(i => TemplateActorSearch.Matches(_haystacks[i], terms)).ToList();

        // 左の木の件数 = 検索語に当たるもののうち、そのカテゴリに入る数
        foreach (var category in _allCategoryItems)
            category.Count = matched.Count(i => TemplateActorSearch.InCategory(i.Entry, category.Key));

        var visible  = matched.Where(i => TemplateActorSearch.InCategory(i.Entry, _categoryKey)).ToList();
        var previous = ListActors.SelectedItem as TemplateActorListItem;
        ListActors.ItemsSource = visible;
        ListActors.SelectedItem = previous is not null && visible.Contains(previous)
            ? previous
            : visible.FirstOrDefault();
        if (ListActors.SelectedItem is not null) ListActors.ScrollIntoView(ListActors.SelectedItem);

        // 空のときの案内
        if (_allItems.Count == 0)
        {
            LblEmpty.Text = "テンプレートアクタがありません。\n" +
                            $"templates/<フォルダ>/{TemplateLibraryMetadata.TemplateActorCatalogFileName} を確かめてください。";
            LblEmpty.Visibility = Visibility.Visible;
        }
        else if (visible.Count == 0)
        {
            LblEmpty.Text = terms.Count > 0
                ? $"「{TxtSearch.Text.Trim()}」に当たるテンプレートアクタはありません。"
                : "このカテゴリにはテンプレートアクタがありません。";
            LblEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            LblEmpty.Visibility = Visibility.Collapsed;
        }

        UpdateAddButton();
    }

    /// <summary>一覧の選択が変わったら、追加できるかを出し直す。</summary>
    private void OnActorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAddButton();
        // 入れられない組み合わせなら、押す前に理由を見せる
        ShowRejectionOfSelection();
    }

    /// <summary>
    /// 選んでいるテンプレートをいまの追加先へ入れられないなら、その理由を状態の行に出す。
    /// </summary>
    /// <returns>理由を出したなら true（入れられる・選んでいないなら何もしないで false）。</returns>
    private bool ShowRejectionOfSelection()
    {
        if (ListActors.SelectedItem is not TemplateActorListItem item) return false;
        if (_target.RejectReason(item.Entry.Is2D) is not { } reason) return false;
        SetStatus($"「{item.Name}」はここへ追加できません: {reason}", StatusKind.Error);
        return true;
    }

    /// <summary>「追加」を押せるかと、押せない理由のツールチップを更新する。</summary>
    private void UpdateAddButton()
    {
        string? reason = ListActors.SelectedItem is TemplateActorListItem item
            ? _target.RejectReason(item.Entry.Is2D)
            : "テンプレートアクタを選んでください";
        BtnAdd.IsEnabled = !_busy && reason is null;
        BtnAdd.ToolTip   = _busy ? "追加の準備をしています" : reason;
    }

    /// <summary>「追加先」の表示を更新する。</summary>
    private void UpdateTargetLabel()
    {
        LblTarget.Text    = _target.Describe();
        LblTarget.ToolTip = _target.Describe();
    }

    // ============================================================
    //  追加
    // ============================================================

    /// <summary>「追加」ボタン。</summary>
    private async void OnAdd(object sender, RoutedEventArgs e) => await AddSelectedAsync();

    /// <summary>一覧の行のダブルクリックで追加する（行の外の余白は無視）。</summary>
    private async void OnActorDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(ListActors, source) is not ListBoxItem) return;
        await AddSelectedAsync();
    }

    /// <summary>
    /// 選んでいるテンプレートアクタを追加する。
    /// 手順（編集できるか → 追加先の引き直し → 入れてよいか → 裏で準備 → 送る直前の引き直し → 送信）は
    /// MCP の seed_template_actor と共通の <see cref="TemplateActorAddFlow"/> に任せ、ここは結果を状態の行へ出すだけ。
    /// </summary>
    private async Task AddSelectedAsync()
    {
        if (_busy || ListActors.SelectedItem is not TemplateActorListItem item) return;
        var entry = item.Entry;

        TemplateActorAddOutcome outcome;
        try
        {
            outcome = await TemplateActorAddFlow.RunAsync(
                _context, _target, entry,
                // 引き直せたら「追加先」の表示を合わせる
                targetRefreshed: refreshed =>
                {
                    _target = refreshed;
                    UpdateTargetLabel();
                },
                // 準備（裏の処理）の間は「追加」を押せなくする
                preparing: () =>
                {
                    _busy = true;
                    UpdateAddButton();
                    SetStatus($"追加しています: {entry.Name}…", StatusKind.Info);
                });
        }
        finally
        {
            _busy = false;
        }

        if (outcome.IsSent && outcome.Result is { } result)
        {
            var (text, full) = ComposeAddedMessage(entry, result);
            bool hasProblem = result.Missing.Count > 0 || result.Copy.Failures.Count > 0 || result.Warnings.Count > 0;
            SetStatus(text, hasProblem ? StatusKind.Error : StatusKind.Success, full);
        }
        else
        {
            SetStatus(outcome.Message ?? TemplateActorAddFlow.TargetLostFallbackMessage, StatusKind.Error);
        }
        UpdateAddButton();
    }

    /// <summary>
    /// 状態の行の文（短い形）と、ツールチップに出す全文を作る。
    /// </summary>
    /// <param name="entry">追加したテンプレートアクタ。</param>
    /// <param name="result">準備の結果。</param>
    /// <returns>(状態の行の文, 全文)。</returns>
    private static (string Text, string Full) ComposeAddedMessage(TemplateActorEntry entry, TemplateActorInstallResult result)
    {
        var shortParts = new List<string> { $"追加しました: {entry.Name}" };
        var fullLines  = new List<string> { $"追加しました: {entry.Name}（{entry.TemplateRelPath}）" };

        void Section(string label, IReadOnlyList<string> items)
        {
            if (items.Count == 0) return;
            var head = string.Join("、", items.Take(StatusListLimit));
            if (items.Count > StatusListLimit) head += $" ほか {items.Count - StatusListLimit} 件";
            shortParts.Add($"{label}: {head}");
            fullLines.Add($"{label}:");
            fullLines.AddRange(items.Select(i => "  " + i));
        }

        Section("コピーしたファイル", result.Copy.Copied);
        Section("既にあるので触らなかったファイル", result.Copy.SkippedExisting);
        Section("コピーできなかったファイル", result.Copy.Failures.Select(f => $"{f.RelPath}（{f.Message}）").ToList());
        Section("見つからない参照", result.Missing);
        Section("入れ子のプレハブの警告", result.Warnings);

        return (string.Join(StatusSeparator, shortParts), string.Join("\n", fullLines));
    }

    // ============================================================
    //  キーボード・閉じる
    // ============================================================

    /// <summary>Esc で閉じる。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    /// <summary>検索欄: ↓ で一覧へ移る、Enter で追加する。</summary>
    private async void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when ListActors.Items.Count > 0:
                e.Handled = true;
                if (ListActors.SelectedIndex < 0) ListActors.SelectedIndex = 0;
                FocusSelectedRow();
                break;
            case Key.Enter:
                e.Handled = true;
                await AddSelectedAsync();
                break;
        }
    }

    /// <summary>一覧: Enter で追加する。</summary>
    private async void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddSelectedAsync();
    }

    /// <summary>選択中の行へキーボードのフォーカスを移す（↑↓ で続けて選べるように）。</summary>
    private void FocusSelectedRow()
    {
        if (ListActors.SelectedItem is null) return;
        ListActors.ScrollIntoView(ListActors.SelectedItem);
        if (ListActors.ItemContainerGenerator.ContainerFromItem(ListActors.SelectedItem) is ListBoxItem row)
            row.Focus();
        else
            ListActors.Focus();
    }

    /// <summary>「閉じる」ボタン。</summary>
    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>閉じたら「開いている窓」を空にする（次は新しく作る）。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>状態の行を書き換える。</summary>
    /// <param name="text">1 行の文。</param>
    /// <param name="kind">種類（色）。</param>
    /// <param name="fullText">ツールチップに出す全文（省略時は同じ文）。</param>
    private void SetStatus(string text, StatusKind kind, string? fullText = null)
    {
        LblStatus.Text       = text;
        LblStatus.ToolTip    = fullText ?? text;
        LblStatus.Foreground = kind switch
        {
            StatusKind.Success => SeedDialogTheme.SuccessText,
            StatusKind.Error   => SeedDialogTheme.ErrorText,
            _                  => SeedDialogTheme.DimText,
        };
    }

    /// <summary>凍結した単色ブラシを作る。</summary>
    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
