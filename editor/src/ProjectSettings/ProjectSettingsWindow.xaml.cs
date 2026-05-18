using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// プロジェクト設定ウィンドウ。
/// 左パネルのカテゴリツリーで大項目を展開し、小項目を選択すると
/// 右パネルに対応する設定 UI が表示される。
/// 「保存して閉じる」でファイルに永続化、「キャンセル」で変更を破棄する。
/// </summary>
public partial class ProjectSettingsWindow : Window
{
    // ── P/Invoke ─────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>ダークタイトルバー属性 ID。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    // ── カテゴリ定義 ─────────────────────────────────────────

    /// <summary>
    /// 小項目の定義。
    /// IsImplemented = true の項目のみ右パネルに実際の設定 UI を表示する。
    /// false の場合はプレースホルダーを表示する。
    /// </summary>
    private record SubItem(string Id, string Label, bool IsImplemented = false);

    /// <summary>大項目の定義。SubItems リストで対応する小項目を管理する。</summary>
    private record Category(string Id, string Label, List<SubItem> SubItems);

    /// <summary>
    /// カテゴリ定義テーブル。
    /// 新たな設定カテゴリを追加する場合はここにエントリを追加するだけでよい。
    /// </summary>
    private static readonly List<Category> Categories = new()
    {
        // ── 必須設定（ゲームとして機能するために必ず設定すべき項目）──
        new("required", "必須", new()
        {
            new("game_name",   "ゲーム名",         IsImplemented: true),
            new("start_scene", "ゲーム開始シーン", IsImplemented: true),
        }),
        // ── グラフィックス設定（将来実装）──────────────────────────
        new("graphics", "グラフィックス", new()
        {
            new("resolution",     "解像度設定"),
            new("render_quality", "レンダリング品質"),
        }),
        // ── オーディオ設定（将来実装）──────────────────────────────
        new("audio", "オーディオ", new()
        {
            new("master_volume", "マスター音量"),
        }),
        // ── 物理設定（将来実装）────────────────────────────────────
        new("physics", "物理", new()
        {
            new("gravity", "重力"),
        }),
        // ── 入力設定（将来実装）────────────────────────────────────
        new("input", "入力", new()
        {
            new("input_mapping", "入力マッピング"),
        }),
        // ── ビルド設定（将来実装）──────────────────────────────────
        new("build", "ビルド", new()
        {
            new("target_platform", "ターゲットプラットフォーム"),
        }),
        // ── タグ＆レイヤー設定（将来実装）──────────────────────────
        new("tags_layers", "タグ＆レイヤー", new()
        {
            new("tags",   "タグ"),
            new("layers", "レイヤー"),
        }),
    };

    // ── ブラシ定数 ────────────────────────────────────────────

    private static readonly SolidColorBrush BrushSelected   = new(Color.FromRgb(0x09, 0x4D, 0x80));
    private static readonly SolidColorBrush BrushHover      = new(Color.FromRgb(0x30, 0x30, 0x32));
    private static readonly SolidColorBrush BrushCatHover   = new(Color.FromRgb(0x2E, 0x2E, 0x30));
    private static readonly SolidColorBrush BrushCategoryFg = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush BrushSubItemFg  = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush BrushTransp     = Brushes.Transparent;

    // ── 状態フィールド ────────────────────────────────────────

    /// <summary>プロジェクト設定ファイルの絶対パス。</summary>
    private readonly string _settingsPath;

    /// <summary>アセットディレクトリのパス（ファイルダイアログの初期ディレクトリとして使用）。</summary>
    private readonly string _assetsPath;

    /// <summary>ロード済みの設定データ。「保存して閉じる」時にファイルへ書き出す。</summary>
    private readonly ProjectSettingsData _data;

    /// <summary>現在展開中の大項目 ID セット。初期状態では「必須」を展開する。</summary>
    private readonly HashSet<string> _expandedCategories = new() { "required" };

    /// <summary>現在選択中の小項目 ID。</summary>
    private string? _selectedSubItemId;

    /// <summary>現在ハイライト表示中の小項目 Border（解除時に背景をリセットするために保持）。</summary>
    private Border? _selectedBorder;

    // ── 設定パネル内コントロール参照 ─────────────────────────
    // 「保存して閉じる」押下時に CollectSettingsFromUi() で値を収集する。

    /// <summary>「ゲーム名」パネルの入力フィールド。</summary>
    private TextBox? _tbGameName;

    /// <summary>「ゲーム開始シーン」パネルのシーンパス入力フィールド。</summary>
    private TextBox? _tbStartScene;

    // ── コンストラクタ ────────────────────────────────────────

    /// <summary>
    /// プロジェクト設定ウィンドウを生成する。
    /// </summary>
    /// <param name="assetsPath">アセットディレクトリのパス。project_settings.json はここに置かれる。</param>
    public ProjectSettingsWindow(string assetsPath)
    {
        InitializeComponent();
        _assetsPath   = assetsPath;
        _settingsPath = Path.Combine(assetsPath, "project_settings.json");
        _data         = ProjectSettingsData.LoadFrom(_settingsPath);
    }

    // ── ウィンドウ初期化 ─────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ダークタイトルバーを適用する
        var helper = new WindowInteropHelper(this);
        int dark = 1;
        DwmSetWindowAttribute(helper.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        // カテゴリツリーを構築し、デフォルト項目（ゲーム開始シーン）を選択する
        BuildCategoryPanel();
        SelectSubItem("start_scene");
    }

    // ── 左パネル: カテゴリツリー構築 ────────────────────────

    /// <summary>
    /// 左パネルのカテゴリツリーを再構築する。
    /// Categories リストをデータソースとして、大項目ヘッダーと小項目行を動的生成する。
    /// _expandedCategories と _selectedSubItemId の状態を反映した UI を生成する。
    /// </summary>
    private void BuildCategoryPanel()
    {
        CategoryPanel.Children.Clear();
        _selectedBorder = null;

        foreach (var category in Categories)
        {
            bool expanded = _expandedCategories.Contains(category.Id);

            // ── 大項目ヘッダー行 ──────────────────────────
            var headerBorder = BuildCategoryHeader(category.Id, category.Label, expanded);
            CategoryPanel.Children.Add(headerBorder);

            // 折りたたみ中は小項目を表示しない
            if (!expanded) continue;

            // ── 小項目行 ──────────────────────────────────
            foreach (var sub in category.SubItems)
            {
                bool isSelected = sub.Id == _selectedSubItemId;
                var subBorder   = BuildSubItemRow(sub.Id, sub.Label, isSelected);
                CategoryPanel.Children.Add(subBorder);

                // 選択中の項目は Border 参照を保持する（次の選択解除に使用）
                if (isSelected) _selectedBorder = subBorder;
            }
        }
    }

    /// <summary>大項目ヘッダー Border を生成する。クリックで展開/折りたたみを切り替える。</summary>
    private Border BuildCategoryHeader(string categoryId, string label, bool expanded)
    {
        var border = new Border
        {
            Padding    = new Thickness(12, 7, 8, 7),
            Cursor     = Cursors.Hand,
            Background = BrushTransp,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            // ▼: 展開中, ▶: 折りたたみ中
            Text              = expanded ? "▼" : "▶",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            FontSize          = 8,
            Width             = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text              = label,
            Foreground        = BrushCategoryFg,
            FontSize          = 12,
            FontWeight        = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        border.Child = content;

        // ホバー色: 大項目は薄めのハイライト
        border.MouseEnter += (_, _) => border.Background = BrushCatHover;
        border.MouseLeave += (_, _) => border.Background = BrushTransp;

        // クリックで展開/折りたたみを切り替えてカテゴリパネルを再描画する
        border.MouseLeftButtonDown += (_, _) =>
        {
            if (_expandedCategories.Contains(categoryId))
                _expandedCategories.Remove(categoryId);
            else
                _expandedCategories.Add(categoryId);
            BuildCategoryPanel();
        };

        return border;
    }

    /// <summary>小項目行 Border を生成する。クリックで対応する設定パネルを表示する。</summary>
    private Border BuildSubItemRow(string subItemId, string label, bool isSelected)
    {
        var border = new Border
        {
            Padding    = new Thickness(30, 5, 8, 5),
            Cursor     = Cursors.Hand,
            Background = isSelected ? BrushSelected : BrushTransp,
        };
        border.Child = new TextBlock
        {
            Text      = label,
            Foreground = BrushSubItemFg,
            FontSize   = 12,
        };

        // ホバー色: 選択中は色を維持する
        border.MouseEnter += (_, _) =>
        {
            if (border != _selectedBorder) border.Background = BrushHover;
        };
        border.MouseLeave += (_, _) =>
        {
            if (border != _selectedBorder) border.Background = BrushTransp;
        };

        // クリックで当該小項目の設定パネルを表示する
        border.MouseLeftButtonDown += (_, _) => SelectSubItem(subItemId);

        return border;
    }

    // ── 右パネル: 設定コンテンツ切り替え ────────────────────

    /// <summary>
    /// 指定した小項目を選択状態にし、右パネルに対応する設定 UI を表示する。
    /// 切り替え前に現在パネルの入力値を _data に保存してデータロストを防ぐ。
    /// </summary>
    /// <param name="subItemId">選択する小項目の ID。</param>
    private void SelectSubItem(string subItemId)
    {
        // パネル切り替え前に現在表示中のパネルから値を収集する
        CollectSettingsFromUi();

        _selectedSubItemId = subItemId;

        // 選択ハイライトを反映するためカテゴリパネルを再描画する
        BuildCategoryPanel();

        // 右パネルのコンテンツを対応する設定 UI に差し替える
        SettingsContent.Content = subItemId switch
        {
            "game_name"   => BuildGameNamePanel(),
            "start_scene" => BuildStartScenePanel(),
            _             => BuildPlaceholderPanel(GetSubItemLabel(subItemId)),
        };
    }

    /// <summary>小項目 ID からラベル文字列を取得する。定義に存在しない場合は ID をそのまま返す。</summary>
    private static string GetSubItemLabel(string subItemId)
    {
        foreach (var cat in Categories)
            foreach (var sub in cat.SubItems)
                if (sub.Id == subItemId) return sub.Label;
        return subItemId;
    }

    // ── 設定パネル構築 ────────────────────────────────────────

    /// <summary>「ゲーム名」設定パネルを構築して返す。</summary>
    private UIElement BuildGameNamePanel()
    {
        var panel = new StackPanel();

        panel.Children.Add(BuildPanelHeader(
            "ゲーム名",
            "パッケージ化時のフォルダ名やウィンドウタイトルとして使用されるゲームの名前。\n" +
            "半角英数字とアンダースコアのみ推奨です。"));

        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Margin     = new Thickness(0, 0, 0, 16),
        });

        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text              = "ゲーム名",
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        _tbGameName = new TextBox
        {
            Text  = _data.GameName,
            Style = (Style)Resources["SettingTextBox"],
        };
        Grid.SetColumn(_tbGameName, 1);
        row.Children.Add(_tbGameName);

        panel.Children.Add(row);

        panel.Children.Add(new TextBlock
        {
            Text         = "パッケージ化すると「出力フォルダ/{ゲーム名}/」に実行ファイルとアセットが出力されます。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(120, 4, 0, 0),
        });

        return panel;
    }

    /// <summary>「ゲーム開始シーン」設定パネルを構築して返す。</summary>
    private UIElement BuildStartScenePanel()
    {
        var panel = new StackPanel();

        // ヘッダー（タイトル + 説明）
        panel.Children.Add(BuildPanelHeader(
            "ゲーム開始シーン",
            "ゲーム起動時に最初にロードするシーンを指定します。\n" +
            "指定されたシーンが Play 実行の起点となります。"));

        // セパレーター
        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Margin     = new Thickness(0, 0, 0, 16),
        });

        // シーンパス入力行（ラベル | テキストボックス | 参照ボタン）
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ラベル
        var label = new TextBlock
        {
            Text              = "開始シーン",
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        // シーンパス テキストボックス（現在の設定値を初期表示する）
        _tbStartScene = new TextBox
        {
            Text  = _data.StartScene,
            Style = (Style)Resources["SettingTextBox"],
        };
        Grid.SetColumn(_tbStartScene, 1);
        row.Children.Add(_tbStartScene);

        // 参照ボタン
        var browseBtn = new Button
        {
            Content = "参照...",
            Style   = (Style)Resources["BrowseButton"],
            Margin  = new Thickness(6, 0, 0, 0),
        };
        browseBtn.Click += OnBrowseStartScene;
        Grid.SetColumn(browseBtn, 2);
        row.Children.Add(browseBtn);

        panel.Children.Add(row);

        // 補足説明
        panel.Children.Add(new TextBlock
        {
            Text         = "アセットフォルダ内の .scene ファイルを指定してください。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(120, 4, 0, 0),
        });

        return panel;
    }

    /// <summary>
    /// 未実装の設定項目に表示するプレースホルダーパネルを構築して返す。
    /// </summary>
    /// <param name="itemLabel">設定項目のラベル名。</param>
    private static UIElement BuildPlaceholderPanel(string itemLabel)
    {
        var panel = new StackPanel();

        panel.Children.Add(BuildPanelHeader(
            itemLabel,
            "この設定は現在準備中です。今後のバージョンで実装される予定です。"));

        return panel;
    }

    /// <summary>
    /// 右パネルの共通ヘッダー（タイトル + 説明文）を構築して返す。
    /// </summary>
    /// <param name="title">設定項目のタイトル文字列。</param>
    /// <param name="description">設定項目の説明文。</param>
    private static StackPanel BuildPanelHeader(string title, string description)
    {
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        header.Children.Add(new TextBlock
        {
            Text       = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            FontSize   = 16,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 6),
        });

        header.Children.Add(new TextBlock
        {
            Text         = description,
            Foreground   = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        return header;
    }

    // ── イベントハンドラ ─────────────────────────────────────

    /// <summary>「参照...」ボタン: .scene ファイルを選択するダイアログを表示し、結果をテキストボックスに反映する。</summary>
    private void OnBrowseStartScene(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title            = "開始シーンを選択",
            Filter           = "Scene Files (*.scene)|*.scene|All Files (*.*)|*.*",
            InitialDirectory = Directory.Exists(_assetsPath) ? _assetsPath : Environment.CurrentDirectory,
        };

        if (dlg.ShowDialog(this) == true && _tbStartScene != null)
        {
            // 選択した絶対パスを仮想パスに変換して表示する
            _tbStartScene.Text = VirtualPath.ToVirtual(dlg.FileName, _assetsPath);
        }
    }

    /// <summary>「保存して閉じる」ボタン: UI から値を収集してファイルに保存し、ウィンドウを閉じる。</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        // 現在表示中のパネルのコントロールから最新値を収集する
        CollectSettingsFromUi();

        try
        {
            _data.SaveTo(_settingsPath);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"設定の保存に失敗しました:\n{ex.Message}",
                "保存エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>「キャンセル」ボタン: 変更を破棄してウィンドウを閉じる。</summary>
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    // ── 設定値収集 ────────────────────────────────────────────

    /// <summary>
    /// 現在表示されている設定パネルのコントロールから値を収集し _data に反映する。
    /// パネル切り替え前および保存前に呼び出すことでデータロストを防ぐ。
    /// </summary>
    private void CollectSettingsFromUi()
    {
        // 「ゲーム名」パネルの入力値を収集する
        if (_tbGameName != null)
        {
            var name = _tbGameName.Text.Trim();
            if (!string.IsNullOrEmpty(name))
                _data.GameName = name;
        }

        // 「ゲーム開始シーン」パネルの入力値を収集する（絶対パスを仮想パスに変換）
        if (_tbStartScene != null)
        {
            var raw = _tbStartScene.Text.Trim();
            // 絶対パスが入力された場合は仮想パスへ変換して保存する
            _data.StartScene = VirtualPath.IsVirtual(raw)
                ? raw
                : VirtualPath.ToVirtual(raw, _assetsPath);
        }
    }
}
