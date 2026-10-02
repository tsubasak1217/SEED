// ============================================================
//  PrefabPreviewPickerWindow.xaml.cs — プレビューするプレハブを選ぶモーダル窓
//
//  【役割】（docs/editor_screen_preview.md §1）
//  ヒエラルキーの右クリック「プレハブをプレビュー」→「プロジェクトから選ぶ...」と、
//  インスペクタの差し込み先の案内の［選ぶ...］から開く（MainWindow.ScreenPreview.cs の RequestPreview）。
//  アセットルートの下の .actor / .actor2d を「最近使ったもの」→「プロジェクトのプレハブ」の順に並べ、
//  検索欄で絞り込み、選んだものの仮想パスを返す（キャンセルは null）。
//  列挙・検索・並びは WPF 非依存の PrefabPreviewCatalog（単体テストあり）。ここは表示とキーボードだけ。
//
//  【重い処理は UI スレッドの外】
//  アセットルートの列挙は Task.Run で行う（プロジェクトが大きいと時間がかかるため）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SEEDEditor.Theme;

namespace SEEDEditor.Preview;

/// <summary>
/// プレビューするプレハブを選ぶモーダル窓。
/// </summary>
public partial class PrefabPreviewPickerWindow : Window
{
    // ── 文言 ────────────────────────────────────────────────

    /// <summary>最近使ったものの区切りの見出し。</summary>
    private const string RecentSection = "最近使ったもの";

    /// <summary>残りの区切りの見出し。</summary>
    private const string OthersSection = "プロジェクトのプレハブ";

    /// <summary>読み込み中の案内。</summary>
    private const string LoadingText = "プレハブを探しています…";

    /// <summary>プレハブが 1 つも無いときの案内。</summary>
    private const string NoPrefabText = "プロジェクトにプレハブ（.actor / .actor2d）がありません。";

    /// <summary>検索に何も当たらないときの案内の書式（{0} = 検索の文字）。</summary>
    private const string NoMatchFormat = "「{0}」に当たるプレハブはありません。";

    /// <summary>件数の書式（{0} = 出している数、{1} = 全部の数）。</summary>
    private const string CountFormat = "{0} / {1} 件";

    /// <summary>列挙に失敗したときの案内の書式（{0} = 理由）。</summary>
    private const string ScanFailedFormat = "プレハブを探せませんでした: {0}";

    /// <summary>何も選んでいないときの［プレビュー］の説明。</summary>
    private const string SelectPrompt = "プレハブを選んでください";

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>アセットルートの絶対パス。</summary>
    private readonly string _assetsRoot;

    /// <summary>最近使ったプレハブ（仮想パス。最近の順）。</summary>
    private readonly IReadOnlyList<string> _recent;

    /// <summary>アセットルートの下の全部のプレハブ（読み込むまでは空）。</summary>
    private IReadOnlyList<PrefabPreviewItem> _allItems = Array.Empty<PrefabPreviewItem>();

    /// <summary>列挙が終わったか（終わるまでは「探しています」を出す）。</summary>
    private bool _scanned;

    /// <summary>列挙に失敗した理由（失敗していなければ null。件数の代わりに出す）。</summary>
    private string? _scanError;

    /// <summary>選んだプレハブの仮想パス（決定したときだけ入る）。</summary>
    public string? SelectedVirtualPath { get; private set; }

    // ============================================================
    //  開き方
    // ============================================================

    /// <summary>
    /// 窓をモーダルで開き、選んだプレハブを返す。
    /// </summary>
    /// <param name="owner">持ち主の窓（エディタの主窓）。</param>
    /// <param name="label">見出し（差し込み先。PreviewInsertTarget.Label）。</param>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="recent">最近使ったプレハブ（仮想パス。最近の順）。</param>
    /// <returns>選んだプレハブの仮想パス（キャンセルなら null）。</returns>
    public static string? Pick(Window owner, string label, string assetsRoot, IReadOnlyList<string> recent)
    {
        var window = new PrefabPreviewPickerWindow(label, assetsRoot, recent) { Owner = owner };
        return window.ShowDialog() == true ? window.SelectedVirtualPath : null;
    }

    /// <summary>窓を作る（開くのは <see cref="Pick"/> から）。</summary>
    /// <param name="label">見出し。</param>
    /// <param name="assetsRoot">アセットルート。</param>
    /// <param name="recent">最近使ったプレハブ。</param>
    private PrefabPreviewPickerWindow(string label, string assetsRoot, IReadOnlyList<string> recent)
    {
        InitializeComponent();
        _assetsRoot       = assetsRoot;
        _recent           = recent;
        LblTarget.Text    = label;
        LblTarget.ToolTip = label;
        BtnAccept.ToolTip = SelectPrompt;
    }

    // ============================================================
    //  読み込み
    // ============================================================

    /// <summary>窓が出たらアセットルートの下を裏で列挙し、一覧を作る。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        TxtSearch.Focus();
        ShowEmpty(LoadingText);
        LblStatus.Text = LoadingText;

        var root = _assetsRoot;
        try
        {
            _allItems = await Task.Run(() => PrefabPreviewCatalog.Scan(root));
        }
        catch (Exception ex)
        {
            _allItems  = Array.Empty<PrefabPreviewItem>();
            _scanError = string.Format(ScanFailedFormat, ex.Message);
        }
        _scanned = true;
        RefreshList();
    }

    // ============================================================
    //  絞り込み
    // ============================================================

    /// <summary>検索欄が変わるたびに絞り込む（空のときだけ案内の文字を見せる）。</summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        LblSearchHint.Visibility = TxtSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_scanned) RefreshList();
    }

    /// <summary>
    /// 検索の文字で一覧を作り直す（最近使ったもの → 残り。選んでいた行がまだ見えていれば選択を保つ）。
    /// </summary>
    private void RefreshList()
    {
        var ordering = PrefabPreviewCatalog.Order(PrefabPreviewCatalog.Filter(_allItems, TxtSearch.Text), _recent);
        var rows = ordering.Recent.Select(i => new PrefabPreviewPickerRow(i, RecentSection))
            .Concat(ordering.Others.Select(i => new PrefabPreviewPickerRow(i, OthersSection)))
            .ToList();

        // 区切りは「最近使ったもの」→「プロジェクトのプレハブ」（行の並びの順にグループが出る）
        var previous = (ListPrefabs.SelectedItem as PrefabPreviewPickerRow)?.Item.VirtualPath;
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PrefabPreviewPickerRow.Section)));
        ListPrefabs.ItemsSource  = view;
        ListPrefabs.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Item.VirtualPath, previous, StringComparison.OrdinalIgnoreCase))
                                   ?? rows.FirstOrDefault();
        if (ListPrefabs.SelectedItem is not null) ListPrefabs.ScrollIntoView(ListPrefabs.SelectedItem);

        // ── 空のときの案内と件数 ──
        if (_allItems.Count == 0) ShowEmpty(NoPrefabText);
        else if (rows.Count == 0) ShowEmpty(string.Format(NoMatchFormat, TxtSearch.Text.Trim()));
        else LblEmpty.Visibility = Visibility.Collapsed;
        // 列挙に失敗していれば件数の代わりに理由を出す（エラーの色）
        LblStatus.Text       = _scanError ?? string.Format(CountFormat, rows.Count, _allItems.Count);
        LblStatus.Foreground = _scanError is null ? SeedDialogTheme.DimText : SeedDialogTheme.ErrorText;

        UpdateAcceptButton();
    }

    /// <summary>一覧の上に案内を出す。</summary>
    /// <param name="text">案内の文。</param>
    private void ShowEmpty(string text)
    {
        LblEmpty.Text       = text;
        LblEmpty.Visibility = Visibility.Visible;
    }

    /// <summary>選択が変わったら［プレビュー］を押せるかを出し直す。</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAcceptButton();

    /// <summary>［プレビュー］を押せるか（選んでいるときだけ）と説明を更新する。</summary>
    private void UpdateAcceptButton()
    {
        var row = ListPrefabs.SelectedItem as PrefabPreviewPickerRow;
        BtnAccept.IsEnabled = row is not null;
        BtnAccept.ToolTip   = row?.Item.VirtualPath ?? SelectPrompt;
    }

    // ============================================================
    //  決定・キーボード
    // ============================================================

    /// <summary>［プレビュー］（Enter も IsDefault でここへ来る）。</summary>
    private void OnAccept(object sender, RoutedEventArgs e) => AcceptSelection();

    /// <summary>一覧の行のダブルクリックで決定する（行の外・区切りの見出しは無視）。</summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(ListPrefabs, source) is not ListBoxItem) return;
        e.Handled = true;
        AcceptSelection();
    }

    /// <summary>選んでいるプレハブで閉じる（選んでいなければ何もしない）。</summary>
    private void AcceptSelection()
    {
        if (ListPrefabs.SelectedItem is not PrefabPreviewPickerRow row) return;
        SelectedVirtualPath = row.Item.VirtualPath;
        DialogResult        = true;
    }

    /// <summary>検索欄: ↓ で一覧へ移る（↑↓ で続けて選べるように）。</summary>
    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || ListPrefabs.Items.Count == 0) return;
        e.Handled = true;
        if (ListPrefabs.SelectedIndex < 0) ListPrefabs.SelectedIndex = 0;
        FocusSelectedRow();
    }

    /// <summary>選択中の行へキーボードのフォーカスを移す。</summary>
    private void FocusSelectedRow()
    {
        if (ListPrefabs.SelectedItem is null) return;
        ListPrefabs.ScrollIntoView(ListPrefabs.SelectedItem);
        if (ListPrefabs.ItemContainerGenerator.ContainerFromItem(ListPrefabs.SelectedItem) is ListBoxItem row)
            row.Focus();
        else
            ListPrefabs.Focus();
    }
}
