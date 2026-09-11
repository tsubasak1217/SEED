// ============================================================
//  StartWindow.xaml.cs — スタート画面（Visual Studio のスタートウィンドウ相当）
//
//  【役割】
//  引数なしで起動したときに最初に出る画面。ここから
//    ・最近開いたプロジェクトを選ぶ
//    ・.seedproj を選んで開く
//    ・新規プロジェクトを作る
//    ・.seedproj をこのエディタへ関連付ける
//  を行い、プロジェクトが決まったら MainWindow を開いて自分は閉じる。
//
//  【ウィンドウの寿命について】
//  ShutdownMode は既定（OnLastWindowClose）。必ず「MainWindow を Show してから
//  自分を Close する」順で切り替えること。逆順にするとウィンドウが 0 個になった
//  瞬間にアプリが終了する。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using SEEDEditor.Project;

namespace SEEDEditor.Startup;

/// <summary>
/// 起動時のスタート画面。
/// </summary>
public partial class StartWindow : Window
{
    // ── P/Invoke（ダークタイトルバー）────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>ダークタイトルバー属性 ID（Windows 10 20H1+）。</summary>
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>キャプション色を指定する属性 ID（Windows 11+）。</summary>
    private const int DWMWA_CAPTION_COLOR = 35;

    /// <summary>ウィンドウ枠色を指定する属性 ID（Windows 11+）。</summary>
    private const int DWMWA_BORDER_COLOR = 34;

    /// <summary>キャプション色（COLORREF = 0x00BBGGRR）。エディタ本体と同じ #1D1D1D。</summary>
    private const int CAPTION_COLOR = 0x001D1D1D;

    /// <summary>ウィンドウ枠色（COLORREF）。エディタ本体と同じ #3A3A3A。</summary>
    private const int BORDER_COLOR = 0x003A3A3A;

    // ── 定数 ────────────────────────────────────────────────

    /// <summary>「開く」ダイアログのフィルタ。</summary>
    private const string OPEN_DIALOG_FILTER = "SEED プロジェクト (*.seedproj)|*.seedproj";

    /// <summary>関連付け済みのときのボタン見出し。</summary>
    private const string ASSOCIATE_TITLE_REGISTERED = ".seedproj の関連付けを解除する";

    /// <summary>関連付け済みのときのボタン注記。</summary>
    private const string ASSOCIATE_NOTE_REGISTERED =
        "現在このエディタに関連付けられています（ダブルクリックで開けます）";

    /// <summary>未関連付けのときのボタン見出し。</summary>
    private const string ASSOCIATE_TITLE_UNREGISTERED = ".seedproj をこのエディタに関連付ける";

    /// <summary>未関連付けのときのボタン注記。</summary>
    private const string ASSOCIATE_NOTE_UNREGISTERED =
        "ダブルクリックで開けるようになります（管理者権限は不要）";

    /// <summary>エクスプローラーで対象を選択状態で開くためのコマンド引数書式。</summary>
    private const string EXPLORER_SELECT_ARG_FORMAT = "/select,\"{0}\"";

    /// <summary>エクスプローラーの実行ファイル名。</summary>
    private const string EXPLORER_EXE = "explorer.exe";

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>最近のプロジェクト一覧の永続化。</summary>
    private readonly RecentProjectsStore _recentStore =
        new(SEEDEditor.Settings.EditorPaths.SettingsDir);

    /// <summary>一覧に表示中の項目（ListBox.ItemsSource の実体）。</summary>
    private readonly List<RecentProjectItem> _items = new();

    /// <summary>起動時に表示するエラー文（無ければ null）。</summary>
    private readonly string? _startupError;

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// スタート画面を生成する。
    /// </summary>
    /// <param name="startupError">
    /// 起動時に発生したエラー（指定プロジェクトが開けなかった等）。
    /// null なら何も表示しない。
    /// </param>
    public StartWindow(string? startupError = null)
    {
        InitializeComponent();
        _startupError = startupError;
    }

    /// <summary>ウィンドウ表示時の初期化（タイトルバー・一覧・関連付け状態）。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDarkTitleBar();

        TxtVersion.Text = EditorVersion.Current;

        if (!string.IsNullOrWhiteSpace(_startupError))
        {
            TxtError.Text        = _startupError;
            ErrorBanner.Visibility = Visibility.Visible;
        }

        RefreshRecentList();
        RefreshAssociationButton();
    }

    /// <summary>タイトルバー・枠をエディタ本体と同じダーク配色にする。</summary>
    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, Marshal.SizeOf(dark));

        int caption = CAPTION_COLOR;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, Marshal.SizeOf(caption));

        int border = BORDER_COLOR;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, Marshal.SizeOf(border));
    }

    // ── 最近のプロジェクト一覧 ───────────────────────────────

    /// <summary>一覧をファイルから読み直して描画する。</summary>
    private void RefreshRecentList()
    {
        _items.Clear();
        foreach (var entry in _recentStore.Load())
            _items.Add(new RecentProjectItem(entry));

        // ItemsSource へ同じ List を差し直すと更新されないため、一旦外してから入れる。
        ListRecent.ItemsSource = null;
        ListRecent.ItemsSource = _items;

        // 1 件も無いときは案内文だけを出し、空の枠線だけが残らないよう箱ごと畳む。
        var empty = _items.Count == 0;
        TxtNoRecent.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RecentBox.Visibility   = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>選択中の行（未選択なら null）。</summary>
    private RecentProjectItem? SelectedItem => ListRecent.SelectedItem as RecentProjectItem;

    /// <summary>ダブルクリックで開く。</summary>
    private void OnRecentDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenSelectedRecent();

    /// <summary>
    /// 右クリックした行を選択状態にする。
    ///
    /// WPF の ListBox は右クリックでは選択が動かないため、これが無いと
    /// コンテキストメニューが「直前に左クリックした別の行」に対して働いてしまう。
    /// </summary>
    private void OnRecentRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // クリック位置の視覚要素から ListBoxItem を遡って探す。
        var element = e.OriginalSource as System.Windows.DependencyObject;
        while (element is not null && element is not ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);

        if (element is ListBoxItem item) item.IsSelected = true;
    }

    /// <summary>Enter で開く / Delete で一覧から外す。</summary>
    private void OnRecentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { OpenSelectedRecent();  e.Handled = true; }
        if (e.Key == Key.Delete) { RemoveSelectedRecent(); e.Handled = true; }
    }

    /// <summary>コンテキストメニュー「開く」。</summary>
    private void OnRecentOpenClick(object sender, RoutedEventArgs e)
        => OpenSelectedRecent();

    /// <summary>コンテキストメニュー「フォルダを開く」。</summary>
    private void OnRecentRevealClick(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item is null) return;

        try
        {
            // ファイルが残っていればそれを選択状態で、無ければ親フォルダだけを開く。
            if (File.Exists(item.ProjectFilePath))
            {
                Process.Start(new ProcessStartInfo(
                    EXPLORER_EXE,
                    string.Format(EXPLORER_SELECT_ARG_FORMAT, item.ProjectFilePath)));
                return;
            }

            var dir = Path.GetDirectoryName(item.ProjectFilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                return;
            }
            ShowStatus($"フォルダが見つかりません: {item.ProjectFilePath}", isError: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"フォルダを開けませんでした: {ex.Message}", isError: true);
        }
    }

    /// <summary>コンテキストメニュー「一覧から外す」。</summary>
    private void OnRecentRemoveClick(object sender, RoutedEventArgs e)
        => RemoveSelectedRecent();

    /// <summary>選択中のプロジェクトを開く。</summary>
    private void OpenSelectedRecent()
    {
        var item = SelectedItem;
        if (item is null) return;

        if (!item.Exists)
        {
            ShowStatus($"プロジェクトが見つかりません: {item.ProjectFilePath}", isError: true);
            return;
        }
        OpenProject(item.ProjectFilePath);
    }

    /// <summary>選択中のプロジェクトを一覧から外す（ファイルは消さない）。</summary>
    private void RemoveSelectedRecent()
    {
        var item = SelectedItem;
        if (item is null) return;

        _recentStore.Remove(item.ProjectFilePath);
        // タスクバーの「最近」欄からも外す。
        ProjectJumpList.Refresh(_recentStore);
        RefreshRecentList();
    }

    // ── 右ペインの操作 ───────────────────────────────────────

    /// <summary>「プロジェクトを開く...」。</summary>
    private void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title      = "プロジェクトを開く",
            Filter     = OPEN_DIALOG_FILTER,
            DefaultExt = SeedProjectFile.EXTENSION,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        OpenProject(dialog.FileName);
    }

    /// <summary>「新規プロジェクトを作成...」。</summary>
    private void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CreatedProject is null) return;

        OpenProject(dialog.CreatedProject.ProjectFilePath);
    }

    /// <summary>「.seedproj を関連付ける / 解除する」。</summary>
    private void OnAssociateClick(object sender, RoutedEventArgs e)
    {
        var exePath = FileAssociation.CurrentExePath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            ShowStatus("エディタの実行ファイルパスを取得できませんでした。", isError: true);
            return;
        }

        bool ok;
        string? error;
        if (FileAssociation.IsRegistered(exePath))
        {
            ok = FileAssociation.Unregister(out error);
            if (ok) ShowStatus(".seedproj の関連付けを解除しました。", isError: false);
        }
        else
        {
            ok = FileAssociation.Register(exePath, out error);
            if (ok) ShowStatus(".seedproj をこのエディタに関連付けました。", isError: false);
        }

        if (!ok) ShowStatus(error ?? "関連付けの変更に失敗しました。", isError: true);
        RefreshAssociationButton();
    }

    /// <summary>関連付けの現在状態に合わせてボタンの文言を切り替える。</summary>
    private void RefreshAssociationButton()
    {
        var registered = FileAssociation.IsRegistered(FileAssociation.CurrentExePath);
        TxtAssociateTitle.Text = registered
            ? ASSOCIATE_TITLE_REGISTERED : ASSOCIATE_TITLE_UNREGISTERED;
        TxtAssociateNote.Text = registered
            ? ASSOCIATE_NOTE_REGISTERED : ASSOCIATE_NOTE_UNREGISTERED;
    }

    // ── プロジェクトを開く ───────────────────────────────────

    /// <summary>
    /// プロジェクトを確定させて MainWindow を開き、このウィンドウを閉じる。
    /// </summary>
    /// <param name="projectFilePath">開く .seedproj の絶対パス。</param>
    private void OpenProject(string projectFilePath)
    {
        ProjectPaths paths;
        try
        {
            paths = ProjectContext.OpenFromFile(projectFilePath);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
            EditorLog.Write($"スタート画面からプロジェクトを開けませんでした: {ex.Message}");
            return;
        }

        try { _recentStore.Add(paths.ProjectFilePath, paths.DisplayName); }
        catch (Exception ex) { EditorLog.Write($"最近のプロジェクトを更新できませんでした: {ex.Message}"); }

        // ShutdownMode = OnLastWindowClose のため、必ず「開いてから閉じる」順にする。
        var main = new SEEDEditor.MainWindow();
        Application.Current.MainWindow = main;
        main.Show();
        Close();
    }

    // ── 表示ヘルパー ────────────────────────────────────────

    /// <summary>右下のステータス行にメッセージを出す。</summary>
    /// <param name="message">表示する文言。</param>
    /// <param name="isError">true なら警告色で出す。</param>
    private void ShowStatus(string message, bool isError)
    {
        TxtStatus.Text       = message;
        TxtStatus.Foreground = isError
            ? System.Windows.Media.Brushes.IndianRed
            : new System.Windows.Media.SolidColorBrush(
                  System.Windows.Media.Color.FromRgb(0x8A, 0xCB, 0x8A));
    }
}
