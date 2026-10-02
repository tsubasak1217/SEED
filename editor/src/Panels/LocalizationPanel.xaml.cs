// ============================================================
//  LocalizationPanel.xaml.cs — 文字列表（ローカライズ）パネル（正典 docs/localization.md §15）
//
//  【役割】
//  多言語の置き場（既定 assets://locale）の index.json と言語の表 <code>.json を、行 = キー・列 = 言語の表で編集する。
//  計算はすべて WPF に依らないモデル（editor/src/Localization の LocaleTableModel。単体テスト LocalizationPanelTests）が持ち、
//  ここは画面の組み立てと操作の受け渡しだけをする。
//
//  【ファイルの分け方】
//    LocalizationPanel.xaml.cs   … 置き場の開き方・保存・読み直し・未保存の確認・状態の表示（このファイル）
//    LocalizationPanel.Grid.cs     … 表の列と行・絞り込み・選んだ行の説明・升目の編集の確定
//    LocalizationPanel.Commands.cs … キーの追加・名前の変更・削除・複数形の形・言語のメニュー・見本から作る
//    LocalizationPanel.DiskSync.cs … 置き場の外部変更の監視と帯
//
//  【ContentId】"localization"（editor/settings/layout.xml の鍵。変えない）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SEEDEditor.Headless;
using SEEDEditor.Localization.IO;
using SEEDEditor.Localization.Model;
using SEEDEditor.Panels.Localization;

namespace SEEDEditor.Panels;

/// <summary>
/// 文字列表（ローカライズ）パネル。行 = キー・列 = 言語の表で多言語のデータファイルを編集する。
/// </summary>
public partial class LocalizationPanel : UserControl, IFileOpenablePanel
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[文字列表]";

    /// <summary>プロジェクトのアセットのフォルダ（SetAssetsPath で受け取る）。</summary>
    private string? _assetsRoot;

    /// <summary>今開いている置き場（絶対パス。まだ開いていなければ null）。</summary>
    private string? _folder;

    /// <summary>表のモデル（置き場を開いていなければ null）。</summary>
    private LocaleTableModel? _model;

    /// <summary>タブ名が変わった（未保存の印 * の付け外し）。MainWindow が LayoutAnchorable の Title へ写す。</summary>
    public event Action<string>? TitleChanged;

    /// <summary>見本の表を取り込んでファイルを作った（MainWindow がプロジェクトパネルを作り直す）。</summary>
    public event Action? FilesCreated;

    /// <summary>パネルを作る。</summary>
    public LocalizationPanel()
    {
        InitializeComponent();
        InitDiskSync();
        BtnNoticeReload.Content = LocalizationPanelMessages.ChangedReload;
        BtnNoticeKeep.Content = LocalizationPanelMessages.ChangedKeep;
        // 表が見えるようになったとき、置き場が無くて案内を出していたら作られていないか確かめ直す
        // （ファイル → テンプレートをインポート で locale を取り込んだ後など）
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshIfMissing(); };
        ShowDetailsForSelection();
    }

    // ============================================================
    //  公開の口（MainWindow から呼ぶ）
    // ============================================================

    /// <summary>未保存の変更があるか。</summary>
    public bool HasUnsavedChanges => _model?.IsDirty == true;

    /// <summary>タブ名（未保存なら末尾に *）。</summary>
    public string Title => LocalizationPanelMessages.PanelTitle + (HasUnsavedChanges ? LocalizationPanelMessages.DirtyMark : string.Empty);

    /// <summary>
    /// プロジェクトのアセットのフォルダを受け取り、既定の置き場（assets://locale）を開く（まだ何も開いていなければ）。
    /// </summary>
    /// <param name="assetsRoot">アセットのフォルダの絶対パス。</param>
    public void SetAssetsPath(string assetsRoot)
    {
        _assetsRoot = assetsRoot;
        if (_folder is null && LocaleFolderLocator.DefaultFolder(assetsRoot) is { } folder) LoadFolder(folder);
    }

    /// <summary>
    /// ファイルを開く（プロジェクトパネルのダブルクリックから。ファイルのあるフォルダを置き場として開く）。
    /// </summary>
    /// <param name="path">locale フォルダの .json の絶対パス。</param>
    public void OpenFile(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is null) return;
        OpenFolder(folder);

        // 言語の表なら、その言語の列へ目を向けさせる（列の見出しに印は無いので、最初の行のその列を今の升目にする）。
        // パネルを前に出した直後でまだ並べ終えていないので、レイアウトの後に行う
        string code = Path.GetFileNameWithoutExtension(path);
        Dispatcher.BeginInvoke(() => FocusLanguageColumn(code), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>置き場を開く（今の置き場に未保存の変更があれば確認する）。</summary>
    /// <param name="folder">置き場の絶対パス。</param>
    public void OpenFolder(string folder)
    {
        if (_folder is not null && string.Equals(Path.GetFullPath(_folder), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) return;
        if (!ConfirmLeaveUnsaved()) return;
        LoadFolder(folder);
    }

    /// <summary>
    /// 終了時の未保存の確認。戻り値 false は「終了をやめる」。ヘッドレスでは確認を出さずに捨てる
    /// （シーンの未保存と同じ方針。モーダルを出すと誰も閉じられない）。
    /// </summary>
    /// <returns>終了してよければ true。</returns>
    public bool PromptSaveOnExit()
    {
        if (!HasUnsavedChanges) return true;
        if (EditorStartupOptions.IsHeadless)
        {
            EditorLog.Write($"{LogPrefix} [ヘッドレス] 未保存の変更を破棄して終了します（確認ダイアログは出しません）。");
            return true;
        }

        var answer = EditorDialogs.Show(LocalizationPanelMessages.UnsavedOnExit, LocalizationPanelMessages.PanelTitle,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>
    /// 表が見えるようになったとき・テンプレートを取り込んだ後に、置き場が無くて案内を出していたら確かめ直す。
    /// </summary>
    public void RefreshIfMissing()
    {
        if (_folder is null || _model is null) return;
        if (MissingView.Visibility != Visibility.Visible) return;
        if (File.Exists(LocaleFolderLocator.IndexPath(_folder))) LoadFolder(_folder);
    }

    // ============================================================
    //  読み込み・保存
    // ============================================================

    /// <summary>置き場を読み込んで表を組み直す（未保存の確認はしない。呼び出し側の責任）。</summary>
    /// <param name="folder">置き場の絶対パス。</param>
    private void LoadFolder(string folder)
    {
        _folder = folder;
        _model = LocaleFolderIO.Load(folder);
        HideNotice();
        WatchFolder(folder);
        RebuildAll();
        EditorLog.Write($"{LogPrefix} 置き場を開きました: {folder}（言語 {_model.Languages.Count}・キー {_allRows.Count}）");
    }

    /// <summary>
    /// 保存する（変わったファイルだけ。UTF-8・BOM 無し・旧版を退避）。
    /// </summary>
    /// <returns>保存できた（または保存するものが無かった）なら true。</returns>
    public bool Save()
    {
        if (_model is null || _folder is null) return true;
        CommitPendingEdit();
        try
        {
            var written = LocaleFolderIO.Save(_folder, _model, _assetsRoot);
            AcceptDiskState();
            HideNotice();
            if (written.Count == 0)
            {
                ShowStatus(LocalizationPanelMessages.NothingToSave, isError: false);
            }
            else
            {
                var names = written.Select(Path.GetFileName).OfType<string>().ToList();
                ShowStatus(LocalizationPanelMessages.Saved(names), isError: false);
                EditorLog.Write($"{LogPrefix} 保存しました: {string.Join(", ", written)}");
            }
            RefreshAfterEdit(structureChanged: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(LocalizationPanelMessages.SaveFailed(ex.Message), isError: true);
            EditorLog.Write($"{LogPrefix} 保存に失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>ディスクから読み直す（未保存の変更があれば確認する）。</summary>
    private void ReloadWithConfirm()
    {
        if (_folder is null) return;
        if (!ConfirmLeaveUnsaved()) return;
        ReloadFromDisk();
    }

    /// <summary>ディスクから読み直す（確認しない。選んでいた行・絞り込みは保つ）。</summary>
    private void ReloadFromDisk()
    {
        if (_folder is null) return;
        string? selectedKey = SelectedRows().FirstOrDefault()?.Key;
        LoadFolder(_folder);
        SelectKey(selectedKey);
        ShowStatus(LocalizationPanelMessages.Reloaded, isError: false);
    }

    /// <summary>
    /// 未保存の変更があれば「保存して続ける / 捨てて続ける / やめる」を尋ねる。
    /// </summary>
    /// <returns>続けてよければ true。</returns>
    private bool ConfirmLeaveUnsaved()
    {
        if (!HasUnsavedChanges) return true;
        var answer = EditorDialogs.Show(LocalizationPanelMessages.UnsavedConfirm, LocalizationPanelMessages.UnsavedCaption,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // ============================================================
    //  表示
    // ============================================================

    /// <summary>モデルから画面を全部組み直す（列・行・案内・題）。</summary>
    private void RebuildAll()
    {
        bool hasTable = _model is not null && (_model.IndexExists || _model.Languages.Count > 0 || _model.IsDirty);
        MissingView.Visibility = hasTable || _model is null ? Visibility.Collapsed : Visibility.Visible;
        Table.Visibility = hasTable ? Visibility.Visible : Visibility.Collapsed;
        if (!hasTable && _folder is not null)
        {
            string display = LocaleFolderLocator.DisplayName(_assetsRoot, _folder);
            MissingText.Text = LocalizationPanelMessages.MissingFolder(display);
            MissingHint.Text = LocalizationPanelMessages.MissingFolderHint;
        }

        RebuildColumns();
        RebuildRows();
        UpdateChrome();
    }

    /// <summary>題・要約・警告・置き場・ボタンの有効無効を今の状態にそろえる。</summary>
    private void UpdateChrome()
    {
        bool editable = _model is not null && !_model.IsReadOnly;
        BtnSave.IsEnabled = _model?.IsDirty == true;
        BtnReload.IsEnabled = _folder is not null;
        BtnAddKey.IsEnabled = editable;
        TxtNewKey.IsEnabled = editable;
        bool hasSelection = editable && SelectedRows().Any();
        BtnRename.IsEnabled = hasSelection;
        BtnDelete.IsEnabled = hasSelection;
        BtnPlural.IsEnabled = hasSelection;
        BtnLanguages.IsEnabled = editable;
        BtnCreate.IsEnabled = _assetsRoot is not null;

        TxtFolder.Text = _folder is null ? string.Empty : LocaleFolderLocator.DisplayName(_assetsRoot, _folder);
        TxtFolder.ToolTip = _folder;

        var warnings = _model?.Warnings ?? Array.Empty<string>();
        TxtWarnings.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtWarnings.Text = LocalizationPanelMessages.WarningCount(warnings.Count);
        TxtWarnings.ToolTip = warnings.Count > 0 ? string.Join(Environment.NewLine, warnings) : null;

        UpdateSummary();
        TitleChanged?.Invoke(Title);
    }

    /// <summary>操作の結果を説明の帯に出す。</summary>
    /// <param name="message">文言。</param>
    /// <param name="isError">失敗か（赤い文字）。</param>
    private void ShowStatus(string message, bool isError)
    {
        TxtStatus.Text = message;
        TxtStatus.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "L10n.ErrorText" : "L10n.SuccessText");
        TxtStatus.Visibility = Visibility.Visible;
    }

    // ============================================================
    //  キー操作
    // ============================================================

    /// <summary>Ctrl+S（パネルにフォーカスがあるとき）で保存する。MainWindow のシーンの保存はこのパネルの間は動かない。</summary>
    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Save();
            e.Handled = true;
        }
    }

    /// <summary>［保存］。</summary>
    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    /// <summary>［読み直す］。</summary>
    private void OnReloadClick(object sender, RoutedEventArgs e) => ReloadWithConfirm();
}
