using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
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
            new("game_name",     "ゲーム名",         IsImplemented: true),
            // 開始シーンの選択はシーンマネージャに統合されている（一覧のラジオボタンで選択）
            new("scene_manager", "シーンマネージャ", IsImplemented: true),
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
        // ── プラグイン設定 ─────────────────────────────────────────
        new("plugins", "プラグイン", new()
        {
            new("plugin_manage", "プラグイン管理", IsImplemented: true),
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

    /// <summary>
    /// エディタ同梱のプラグインライブラリディレクトリ（editor/plugins/）。
    /// ライブラリタブでインポート可能なプラグインの一覧表示に使用する。
    /// </summary>
    private readonly string _editorPluginsPath;

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

    /// <summary>現在ビューポートで開いているシーンの絶対パス（未保存なら null）。</summary>
    private readonly string? _currentScenePath;

    /// <summary>シーンマネージャの一覧表示先パネル（行の再構築に使用）。</summary>
    private StackPanel? _sceneListPanel;

    /// <summary>
    /// プラグイン管理パネルのチェックボックスリスト。
    /// Key = プラグイン名, Value = IsChecked バインド元 CheckBox。
    /// CollectSettingsFromUi() で有効/無効状態を収集する。
    /// </summary>
    private Dictionary<string, CheckBox> _pluginCheckBoxes = new();

    // ── コンストラクタ ────────────────────────────────────────

    /// <summary>
    /// プロジェクト設定ウィンドウを生成する。
    /// </summary>
    /// <param name="assetsPath">アセットディレクトリのパス。project_settings.json はここに置かれる。</param>
    /// <param name="editorPluginsPath">エディタ同梱プラグインライブラリのディレクトリ（editor/plugins/）。</param>
    /// <param name="currentScenePath">
    /// 現在ビューポートで開いているシーンの絶対パス（未保存なら null）。
    /// シーンマネージャの「現在のシーンを追加」ボタンで使用する。
    /// </param>
    public ProjectSettingsWindow(string assetsPath, string editorPluginsPath = "", string? currentScenePath = null)
    {
        InitializeComponent();
        _assetsPath        = assetsPath;
        _editorPluginsPath = editorPluginsPath;
        _currentScenePath  = currentScenePath;
        _settingsPath      = Path.Combine(assetsPath, "project_settings.json");
        _data              = ProjectSettingsData.LoadFrom(_settingsPath);
    }

    // ── ウィンドウ初期化 ─────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ダークタイトルバーを適用する
        var helper = new WindowInteropHelper(this);
        int dark = 1;
        DwmSetWindowAttribute(helper.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        // カテゴリツリーを構築し、デフォルト項目（シーンマネージャ）を選択する
        BuildCategoryPanel();
        SelectSubItem("scene_manager");
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
            "game_name"      => BuildGameNamePanel(),
            "scene_manager"  => BuildSceneManagerPanel(),
            "plugin_manage"  => BuildPluginManagePanel(),
            _                => BuildPlaceholderPanel(GetSubItemLabel(subItemId)),
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

    // ── シーンマネージャパネル ────────────────────────────────
    // 開始シーンの設定はシーンマネージャに統合されている
    //（登録シーン一覧のラジオボタンで選択する）。

    /// <summary>「シーンマネージャ」設定パネルを構築して返す。</summary>
    ///
    /// 登録したシーンはスクリプトから名前で参照できる:
    ///   SEED.Scene.Transition("シーン名") / SEED.Scene.Load("シーン名")
    /// 追加方法は「現在のシーンを追加」ボタン、またはボタンへの .scene ファイルドロップ。
    private UIElement BuildSceneManagerPanel()
    {
        var panel = new StackPanel();

        panel.Children.Add(BuildPanelHeader(
            "シーンマネージャ",
            "スクリプトから名前で遷移できるシーンを登録します。\n" +
            "SEED.Scene.Transition(\"シーン名\") / SEED.Scene.Load(\"シーン名\") で参照されます。\n" +
            "「開始」列のラジオボタンでゲーム起動時に最初にロードするシーンを選択します。"));

        // 旧設定の移行: start_scene に登録済みのパスが一覧に無ければ自動でエントリ化する
        //（開始シーン設定をシーンマネージャへ統合したことによる後方互換）
        if (!string.IsNullOrEmpty(_data.StartScene) &&
            !_data.Scenes.Any(s => s.Path == _data.StartScene))
        {
            var stem = Path.GetFileNameWithoutExtension(_data.StartScene);
            // 名前が重複する場合は AddSceneEntry と同じ規則で一意化する
            var name   = stem;
            int suffix = 1;
            while (_data.Scenes.Any(s => s.Name == name))
            {
                name = $"{stem}({suffix})";
                suffix++;
            }
            _data.Scenes.Add(new SceneEntry { Name = name, Path = _data.StartScene });
        }

        panel.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Margin     = new Thickness(0, 0, 0, 12),
        });

        // 追加ボタン（クリック = 現在のシーンを追加 / .scene ファイルのドロップ先も兼ねる）
        var addBtn = new Button
        {
            Content   = "＋ 追加（現在のシーン）",
            Style     = (Style)Resources["BrowseButton"],
            AllowDrop = true,
            Padding   = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip   = "クリック: ビューポートで開いているシーンを登録\nドロップ: .scene ファイルをここへドラッグして登録",
        };
        addBtn.Click    += OnAddCurrentScene;
        addBtn.DragOver += (_, e) =>
        {
            // エクスプローラ（FileDrop）とプロジェクトパネル（SEEDProjectPaths）の両方を受け付ける
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                     || e.Data.GetDataPresent(ProjectPanelDragFormat)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        };
        addBtn.Drop += OnDropSceneFiles;
        panel.Children.Add(addBtn);

        panel.Children.Add(new TextBlock
        {
            Text         = "ボタンへ .scene ファイルをドロップしても登録できます。名前は一覧で編集できます。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 11,
            Margin       = new Thickness(0, 6, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        });

        // 登録済みシーンの一覧
        _sceneListPanel = new StackPanel();
        RebuildSceneList();
        panel.Children.Add(_sceneListPanel);

        return panel;
    }

    /// <summary>「現在のシーンを追加」: ビューポートで開いているシーンをレジストリへ登録する。</summary>
    private void OnAddCurrentScene(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentScenePath))
        {
            MessageBox.Show(this,
                "現在開いているシーンがありません。先にシーンを保存してください。",
                "シーンマネージャ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddSceneEntry(_currentScenePath);
        RebuildSceneList();
    }

    /// <summary>プロジェクトパネルのドラッグデータ形式名（ProjectPanel.xaml.cs と一致させる）。</summary>
    private const string ProjectPanelDragFormat = "SEEDProjectPaths";

    /// <summary>
    /// 追加ボタンへの .scene ファイルドロップ: ドロップされた全 .scene を登録する。
    /// エクスプローラからの FileDrop と、エディタのプロジェクトパネルからの
    /// SEEDProjectPaths 形式の両方に対応する。
    /// </summary>
    private void OnDropSceneFiles(object sender, DragEventArgs e)
    {
        // どちらの形式でもパス配列として取り出す
        var files = e.Data.GetData(DataFormats.FileDrop) as string[]
                 ?? e.Data.GetData(ProjectPanelDragFormat) as string[];
        if (files is null) return;

        foreach (var f in files)
        {
            if (Path.GetExtension(f).Equals(".scene", StringComparison.OrdinalIgnoreCase))
                AddSceneEntry(f);
        }
        RebuildSceneList();
    }

    /// <summary>
    /// シーンファイル（絶対パス）をレジストリへ追加する。
    /// パスは仮想パス（assets://）へ変換し、既登録パスは無視する。
    /// 名前はファイル名（拡張子なし）を既定とし、重複時は "(1)" 等を付与して一意化する。
    /// </summary>
    private void AddSceneEntry(string absolutePath)
    {
        var virtualPath = VirtualPath.ToVirtual(absolutePath, _assetsPath);

        // 同じパスが登録済みなら何もしない
        if (_data.Scenes.Any(s => s.Path == virtualPath)) return;

        // ファイル名（拡張子なし）を基に一意な名前を決める
        var baseName = Path.GetFileNameWithoutExtension(absolutePath);
        var name     = baseName;
        int suffix   = 1;
        while (_data.Scenes.Any(s => s.Name == name))
        {
            name = $"{baseName}({suffix})";
            suffix++;
        }

        _data.Scenes.Add(new SceneEntry { Name = name, Path = virtualPath });
    }

    /// <summary>登録済みシーンの一覧 UI を再構築する。</summary>
    private void RebuildSceneList()
    {
        if (_sceneListPanel is null) return;
        _sceneListPanel.Children.Clear();

        if (_data.Scenes.Count == 0)
        {
            _sceneListPanel.Children.Add(new TextBlock
            {
                Text       = "登録されたシーンはありません。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize   = 11,
            });
            return;
        }

        // 列ヘッダー行（開始 | 名前 | パス）
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        var hStart = new TextBlock { Text = "開始", Foreground = headerBrush, FontSize = 11 };
        var hName  = new TextBlock { Text = "名前", Foreground = headerBrush, FontSize = 11 };
        var hPath  = new TextBlock { Text = "パス", Foreground = headerBrush, FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(hStart, 0); header.Children.Add(hStart);
        Grid.SetColumn(hName,  1); header.Children.Add(hName);
        Grid.SetColumn(hPath,  2); header.Children.Add(hPath);
        _sceneListPanel.Children.Add(header);

        foreach (var entry in _data.Scenes)
        {
            var captured = entry;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 開始シーン選択ラジオボタン（選択したエントリのパスが start_scene になる）
            var startRadio = new RadioButton
            {
                GroupName           = "StartSceneSelect",
                IsChecked           = captured.Path == _data.StartScene,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip             = "ゲーム起動時に最初にロードするシーンにする",
            };
            startRadio.Checked += (_, _) => _data.StartScene = captured.Path;
            Grid.SetColumn(startRadio, 0);
            row.Children.Add(startRadio);

            // 名前（編集可。スクリプトが Transition("名前") で参照するキー）
            var nameBox = new TextBox
            {
                Text    = captured.Name,
                Style   = (Style)Resources["SettingTextBox"],
                ToolTip = "スクリプトから参照する名前（SEED.Scene.Transition のキー）",
            };
            nameBox.LostFocus += (_, _) =>
            {
                var newName = nameBox.Text.Trim();
                // 空・他エントリとの重複は元の名前へ戻す
                if (newName.Length == 0 ||
                    _data.Scenes.Any(s => !ReferenceEquals(s, captured) && s.Name == newName))
                {
                    nameBox.Text = captured.Name;
                    return;
                }
                captured.Name = newName;
            };
            Grid.SetColumn(nameBox, 1);
            row.Children.Add(nameBox);

            // パス表示（読み取り専用）
            var pathText = new TextBlock
            {
                Text              = captured.Path,
                Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 8, 0),
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(pathText, 2);
            row.Children.Add(pathText);

            // 削除ボタン
            var removeBtn = new Button
            {
                Content = "削除",
                Style   = (Style)Resources["BrowseButton"],
            };
            removeBtn.Click += (_, _) =>
            {
                // 開始シーンに選択中のエントリを削除した場合は開始シーン設定も解除する
                if (_data.StartScene == captured.Path)
                {
                    _data.StartScene = "";
                }
                _data.Scenes.Remove(captured);
                RebuildSceneList();
            };
            Grid.SetColumn(removeBtn, 3);
            row.Children.Add(removeBtn);

            _sceneListPanel.Children.Add(row);
        }
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

        // 開始シーンはシーンマネージャのラジオボタン選択時に _data.StartScene へ即時反映される

        // プラグイン有効/無効状態を収集する（CheckBox が存在する場合のみ）
        if (_pluginCheckBoxes.Count > 0)
        {
            // 既存エントリを更新し、新規エントリを追加する
            foreach (var (name, cb) in _pluginCheckBoxes)
            {
                var enabled = cb.IsChecked == true;
                var entry   = _data.Plugins.FirstOrDefault(p => p.Name == name);
                if (entry is null)
                    _data.Plugins.Add(new PluginEntry { Name = name, Enabled = enabled });
                else
                    entry.Enabled = enabled;
            }
        }
    }

    // ── プラグイン管理パネル ──────────────────────────────────

    // タブ選択状態（true = ライブラリ, false = インポート済み）
    private bool _pluginTabIsLibrary = false;

    /// <summary>
    /// プラグイン管理パネルを構築して返す。
    /// 「ライブラリ」「インポート済み」の 2 タブを持つ。
    /// </summary>
    private UIElement BuildPluginManagePanel()
    {
        _pluginCheckBoxes.Clear();

        var root = new StackPanel();

        // ── ヘッダー ──────────────────────────────────────────────
        root.Children.Add(BuildPanelHeader(
            "プラグイン管理",
            "ライブラリからプラグインをインポートしてプロジェクトに追加します。\n" +
            "インポート済みプラグインの有効/無効は「インポート済み」タブで切り替えます。"));

        root.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Margin     = new Thickness(0, 0, 0, 0),
        });

        // ── タブバー ──────────────────────────────────────────────
        var tabContent = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Margin     = new Thickness(0),
        };

        Border? tabLibBorder    = null;
        Border? tabImportBorder = null;

        void RefreshTabContent()
        {
            tabContent.Child = _pluginTabIsLibrary
                ? BuildLibraryTabContent(RefreshTabContent)
                : BuildImportedTabContent();

            // タブ選択状態のスタイルを更新する
            if (tabLibBorder is not null)
                tabLibBorder.Background = _pluginTabIsLibrary
                    ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))
                    : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            if (tabImportBorder is not null)
                tabImportBorder.Background = !_pluginTabIsLibrary
                    ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))
                    : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        }

        tabLibBorder    = BuildTabHeader("ライブラリ",    isSelected: _pluginTabIsLibrary,  onClick: () => { _pluginTabIsLibrary = true;  RefreshTabContent(); });
        tabImportBorder = BuildTabHeader("インポート済み", isSelected: !_pluginTabIsLibrary, onClick: () => { _pluginTabIsLibrary = false; RefreshTabContent(); });

        var tabBar = new Grid();
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(tabLibBorder,    0);
        Grid.SetColumn(tabImportBorder, 1);
        tabBar.Children.Add(tabLibBorder);
        tabBar.Children.Add(tabImportBorder);
        root.Children.Add(tabBar);

        root.Children.Add(new Border
        {
            Height     = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
        });

        // 初期タブコンテンツを設定する
        RefreshTabContent();
        root.Children.Add(tabContent);

        return root;
    }

    /// <summary>タブヘッダー Border を生成する。</summary>
    private static Border BuildTabHeader(string label, bool isSelected, Action onClick)
    {
        var bg = isSelected
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))
            : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));

        var border = new Border
        {
            Background  = bg,
            Padding     = new Thickness(0, 8, 0, 8),
            Cursor      = Cursors.Hand,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
        };

        // 選択中はアクセントカラーのアンダーラインを表示する
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text              = label,
            Foreground        = isSelected
                ? new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE))
                : new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            FontSize          = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (isSelected)
        {
            stack.Children.Add(new Border
            {
                Height     = 2,
                Margin     = new Thickness(12, 4, 12, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
            });
        }
        border.Child = stack;

        border.MouseLeftButtonDown += (_, _) => onClick();
        border.MouseEnter += (_, _) =>
        {
            if (border.Background is SolidColorBrush bg2 &&
                bg2.Color == Color.FromRgb(0x2A, 0x2A, 0x2A))
                border.Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
        };
        border.MouseLeave += (_, _) =>
        {
            if (border.Background is SolidColorBrush bg2 &&
                bg2.Color == Color.FromRgb(0x30, 0x30, 0x30))
                border.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        };

        return border;
    }

    // ── ライブラリタブ ────────────────────────────────────────

    /// <summary>
    /// 「ライブラリ」タブのコンテンツを構築して返す。
    /// editor/plugins/ にある未インポートのプラグインを一覧表示し、
    /// インポートボタンでプロジェクトの plugins/ フォルダへコピーする。
    /// </summary>
    private UIElement BuildLibraryTabContent(Action refreshPanel)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };

        // ライブラリディレクトリが未設定または存在しない場合
        if (string.IsNullOrEmpty(_editorPluginsPath) || !Directory.Exists(_editorPluginsPath))
        {
            sp.Children.Add(new TextBlock
            {
                Text         = "ライブラリフォルダが見つかりません。\neditor/plugins/ にプラグインを配置してください。",
                Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(16, 8, 16, 0),
            });
            return sp;
        }

        // インポート済みプラグイン名セット（重複インポート防止）
        var projectPluginsDir = Path.GetFullPath(Path.Combine(_assetsPath, "..", "plugins"));
        var importedNames = Directory.Exists(projectPluginsDir)
            ? Directory.GetDirectories(projectPluginsDir)
                  .Select(d => ReadPluginName(d))
                  .Where(n => n is not null)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase)!
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ライブラリフォルダをスキャンして plugin.json を持つフォルダを列挙する
        var libDirs = Directory.GetDirectories(_editorPluginsPath)
            .Where(d => File.Exists(Path.Combine(d, "plugin.json")))
            .ToArray();

        if (libDirs.Length == 0)
        {
            sp.Children.Add(new TextBlock
            {
                Text         = "ライブラリにプラグインがありません。",
                Foreground   = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize     = 11,
                Margin       = new Thickness(16, 8, 16, 0),
            });
            return sp;
        }

        foreach (var dir in libDirs)
        {
            var (name, version, desc) = ReadPluginManifest(dir);
            var alreadyImported = importedNames.Contains(name);

            sp.Children.Add(BuildPluginRow(
                name, version, desc,
                rightControl: BuildImportButton(dir, name, alreadyImported, projectPluginsDir, refreshPanel)));
        }

        sp.Children.Add(new TextBlock
        {
            Text         = "※ インポートするとプロジェクトの plugins/ フォルダにコピーされます。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(16, 12, 16, 0),
        });

        return sp;
    }

    /// <summary>
    /// インポートボタンを生成する。
    /// インポート済みの場合は「インポート済み」ラベルを表示する（非活性）。
    /// </summary>
    private UIElement BuildImportButton(
        string srcDir, string pluginName, bool alreadyImported,
        string destPluginsDir, Action refreshPanel)
    {
        if (alreadyImported)
        {
            return new TextBlock
            {
                Text              = "インポート済み",
                Foreground        = new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0x44)),
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 4, 0),
            };
        }

        var btn = new Button
        {
            Content             = "インポート",
            Padding             = new Thickness(10, 4, 10, 4),
            FontSize            = 11,
            Background          = new SolidColorBrush(Color.FromRgb(0x09, 0x4D, 0x80)),
            Foreground          = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness     = new Thickness(0),
            Cursor              = Cursors.Hand,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        btn.Click += (_, _) =>
        {
            try
            {
                // プラグインフォルダをプロジェクトの plugins/ へコピーする
                var destDir = Path.Combine(destPluginsDir, Path.GetFileName(srcDir));
                CopyDirectory(srcDir, destDir);

                // project_settings.json の plugins リストに追加（デフォルト有効）
                if (_data.Plugins.All(p => p.Name != pluginName))
                    _data.Plugins.Add(new PluginEntry { Name = pluginName, Enabled = true });

                // パネルを再描画する
                refreshPanel();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"インポートに失敗しました:\n{ex.Message}",
                    "インポートエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        return btn;
    }

    // ── インポート済みタブ ────────────────────────────────────

    /// <summary>
    /// 「インポート済み」タブのコンテンツを構築して返す。
    /// {assetsRoot}/../plugins/ にある各プラグインを一覧表示し、
    /// 有効/無効 CheckBox で切り替えられる。
    /// </summary>
    private UIElement BuildImportedTabContent()
    {
        _pluginCheckBoxes.Clear();

        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };

        var projectPluginsDir = Path.GetFullPath(Path.Combine(_assetsPath, "..", "plugins"));
        var pluginDirs = Directory.Exists(projectPluginsDir)
            ? Directory.GetDirectories(projectPluginsDir)
                  .Where(d => File.Exists(Path.Combine(d, "plugin.json")))
                  .ToArray()
            : Array.Empty<string>();

        if (pluginDirs.Length == 0)
        {
            sp.Children.Add(new TextBlock
            {
                Text         = "インポート済みのプラグインがありません。\n「ライブラリ」タブからインポートしてください。",
                Foreground   = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(16, 8, 16, 0),
            });
            return sp;
        }

        foreach (var dir in pluginDirs)
        {
            var (name, version, desc) = ReadPluginManifest(dir);
            var existingEntry = _data.Plugins.FirstOrDefault(p => p.Name == name);
            var isEnabled     = existingEntry?.Enabled ?? true;

            // 有効/無効チェックボックスを右側コントロールとして渡す
            var cb = new CheckBox
            {
                IsChecked         = isEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip           = "有効にするとエンジン起動時にロードされます",
            };
            _pluginCheckBoxes[name] = cb;

            sp.Children.Add(BuildPluginRow(name, version, desc, rightControl: cb));
        }

        sp.Children.Add(new TextBlock
        {
            Text         = "※ 変更はプロジェクト設定の保存後、次回エンジン起動時に反映されます。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(16, 12, 16, 0),
        });

        return sp;
    }

    // ── 共通ヘルパー ──────────────────────────────────────────

    /// <summary>
    /// プラグイン 1 行分の UI（名前・バージョン・説明 + 右側コントロール）を生成する。
    /// </summary>
    private static Border BuildPluginRow(string name, string version, string desc, UIElement rightControl)
    {
        var row = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(16, 9, 16, 9),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 情報
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // バージョン + 右コントロール

        // 名前・説明列
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(new TextBlock
        {
            Text       = name,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
        });
        if (!string.IsNullOrEmpty(desc))
        {
            infoStack.Children.Add(new TextBlock
            {
                Text         = desc,
                Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize     = 10,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        Grid.SetColumn(infoStack, 0);
        grid.Children.Add(infoStack);

        // バージョン + 右コントロール列
        var rightStack = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
        };
        if (!string.IsNullOrEmpty(version))
        {
            rightStack.Children.Add(new TextBlock
            {
                Text              = $"v{version}",
                Foreground        = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize          = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 10, 0),
            });
        }
        rightStack.Children.Add(rightControl);
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(rightStack);

        row.Child = grid;
        return row;
    }

    /// <summary>
    /// plugin.json のマニフェストを読んで (name, version, description) を返す。
    /// 読み取れない場合はフォルダ名を name として使用する。
    /// </summary>
    private static (string name, string version, string desc) ReadPluginManifest(string dir)
    {
        var name    = Path.GetFileName(dir);
        var version = "";
        var desc    = "";
        try
        {
            var json = File.ReadAllText(Path.Combine(dir, "plugin.json"));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("name",        out var n)) name    = n.GetString() ?? name;
            if (root.TryGetProperty("version",     out var v)) version = v.GetString() ?? "";
            if (root.TryGetProperty("description", out var d)) desc    = d.GetString() ?? "";
        }
        catch { /* manifest が読めなかった場合はフォルダ名を使用する */ }
        return (name, version, desc);
    }

    /// <summary>
    /// plugin.json が存在するフォルダからプラグイン名を読む。
    /// 読み取れない場合は null を返す。
    /// </summary>
    private static string? ReadPluginName(string dir)
    {
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("name", out var n))
                return n.GetString();
        }
        catch { }
        return Path.GetFileName(dir);
    }

    /// <summary>
    /// ディレクトリを再帰的にコピーする。destDir が存在する場合は上書きする。
    /// </summary>
    private static void CopyDirectory(string srcDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(srcDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var subDir in Directory.GetDirectories(srcDir))
            CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }
}
