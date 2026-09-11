// ============================================================
//  NewProjectDialog.xaml.cs — 新規プロジェクト作成ダイアログ
//
//  【役割】
//  「プロジェクト名」と「作成先フォルダ」を受け取り、入力の妥当性を逐次表示して、
//  OK なら ProjectCreator へ生成を任せる。生成規則そのものは一切ここに書かない
//  （フォルダ構成の正典は ProjectCreator 側に 1 つだけ置く）。
// ============================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SEEDEditor.Project;

namespace SEEDEditor.Startup;

/// <summary>
/// 新規プロジェクト作成ダイアログ。
/// <see cref="ShowDialog"/> が true を返したとき <see cref="CreatedProject"/> に
/// 生成結果が入る。
/// </summary>
public partial class NewProjectDialog : Window
{
    // ── P/Invoke（ダークタイトルバー）────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>ダークタイトルバー属性 ID。</summary>
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // ── 定数 ────────────────────────────────────────────────

    /// <summary>プロジェクト名の初期値。</summary>
    private const string DEFAULT_PROJECT_NAME = "MyGame";

    /// <summary>作成先フォルダの初期値に使う「マイドキュメント」配下のフォルダ名。</summary>
    private const string DEFAULT_PARENT_FOLDER_NAME = "SEED Projects";

    /// <summary>生成先プレビューの書式（{0} = プロジェクトルート、{1} = プロジェクト名）。</summary>
    private const string PREVIEW_FORMAT =
        "{0}\\\n"
      + "  {1}.seedproj\n"
      + "  assets\\  (project_settings.json / packaging_settings.json / scenes\\Main.scene)\n"
      + "  plugins\\";

    // ── 結果 ────────────────────────────────────────────────

    /// <summary>生成されたプロジェクト。キャンセル・失敗時は null。</summary>
    public ProjectPaths? CreatedProject { get; private set; }

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>ダイアログを生成する。</summary>
    public NewProjectDialog()
    {
        InitializeComponent();
    }

    /// <summary>初期値を入れて検証表示を整える。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDarkTitleBar();

        TxtName.Text      = DEFAULT_PROJECT_NAME;
        TxtParentDir.Text = ResolveDefaultParentDir();

        // 名前をすぐ書き換えられるよう、開いた瞬間は名前欄を全選択しておく。
        TxtName.Focus();
        TxtName.SelectAll();

        UpdateValidation();
    }

    /// <summary>タイトルバーをダーク配色にする。</summary>
    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, Marshal.SizeOf(dark));
    }

    /// <summary>
    /// 作成先フォルダの初期値を決める。
    /// マイドキュメント配下の「SEED Projects」を既定にする（実在しなくてもよい。
    /// 生成時に作られる）。
    /// </summary>
    private static string ResolveDefaultParentDir()
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(documents))
                return Path.Combine(documents, DEFAULT_PARENT_FOLDER_NAME);
        }
        catch { /* 取得できない環境ではカレントで代用する */ }

        return Directory.GetCurrentDirectory();
    }

    // ── 入力の検証 ──────────────────────────────────────────

    /// <summary>入力欄が変わるたびに検証と生成先プレビューを更新する。</summary>
    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateValidation();

    /// <summary>
    /// 入力内容を検証し、エラー文・プレビュー・作成ボタンの活殺を更新する。
    /// </summary>
    private void UpdateValidation()
    {
        // XAML の初期化順によっては TextChanged が要素の生成前に飛ぶことがあるため守る。
        if (TxtName is null || TxtParentDir is null || TxtValidation is null
            || TxtPreview is null || BtnCreate is null) return;

        var name      = TxtName.Text;
        var parentDir = TxtParentDir.Text;

        var error = ProjectCreator.ValidateDestination(parentDir, name);
        TxtValidation.Text = error ?? string.Empty;
        BtnCreate.IsEnabled = error is null;

        // プレビューは「名前が使える」段階で出す（作成先が未入力でも形は見せる）。
        if (ProjectCreator.ValidateName(name) is null && !string.IsNullOrWhiteSpace(parentDir))
        {
            try
            {
                var root = Path.Combine(Path.GetFullPath(parentDir), name);
                TxtPreview.Text = string.Format(PREVIEW_FORMAT, root, name);
                return;
            }
            catch { /* パスとして不正。下のフォールバックへ落とす */ }
        }
        TxtPreview.Text = string.Empty;
    }

    // ── 操作 ────────────────────────────────────────────────

    /// <summary>「参照...」で作成先フォルダを選ぶ。</summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "プロジェクトの作成先フォルダを選択",
            InitialDirectory = Directory.Exists(TxtParentDir.Text) ? TxtParentDir.Text : null,
        };
        if (dialog.ShowDialog(this) != true) return;

        TxtParentDir.Text = dialog.FolderName;
        UpdateValidation();
    }

    /// <summary>「作成」。生成に成功したらダイアログを閉じる。</summary>
    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var name      = TxtName.Text;
        var parentDir = TxtParentDir.Text;

        var error = ProjectCreator.ValidateDestination(parentDir, name);
        if (error is not null)
        {
            TxtValidation.Text = error;
            return;
        }

        try
        {
            CreatedProject = ProjectCreator.Create(parentDir, name);
            EditorLog.Write($"新規プロジェクトを作成しました: {CreatedProject}");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            TxtValidation.Text = $"作成に失敗しました: {ex.Message}";
            EditorLog.Write($"新規プロジェクトの作成に失敗しました: {ex}");
        }
    }

    /// <summary>「キャンセル」。</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
