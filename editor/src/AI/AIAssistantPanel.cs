// ============================================================
//  AIAssistantPanel.cs — AI アシスタントチャットパネル（WPF 動的構築）
//
//  機能:
//   - 複数 AI プロバイダー対応（ローカル AI / OpenAI 互換 / Anthropic / Gemini）
//   - ツール呼び出しによるエディタ操作（エージェントループ）
//   - チャット履歴ダッシュボード（セッション切り替え・削除・永続化）
//   - 設定の永続化（プロバイダー・モデル・API キー）
//   - 全メッセージにわたるテキスト選択（RichTextBox 使用）
//   - 応答中の経過時間表示
// ============================================================

using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SEEDEditor.AI.LocalLlm;
using SEEDEditor.AI.Models;
using SEEDEditor.AI.Providers;
using SEEDEditor.AI.Tools;

namespace SEEDEditor.AI;

/// <summary>
/// AI アシスタントチャットパネルを生成・管理するクラス。
/// MainWindow のサイドパネルまたは下部ドックとして組み込む。
/// </summary>
public class AIAssistantPanel
{
    // ── プロバイダーインデックス定数 ─────────────────────────────
    /// <summary>Ollama なし。インデックスは 0〜3 の 4 種類。</summary>
    private const int PROVIDER_LOCAL_AI  = 0;
    private const int PROVIDER_OPENAI    = 1;
    private const int PROVIDER_ANTHROPIC = 2;
    private const int PROVIDER_GEMINI    = 3;

    // ── メッセージカラー定数 ─────────────────────────────────────
    /// <summary>システムメッセージ色（緑）</summary>
    private static readonly Color COLOR_SYSTEM = Color.FromRgb(80,  200, 80);
    /// <summary>ユーザーメッセージ色（青）</summary>
    private static readonly Color COLOR_USER   = Color.FromRgb(30,  144, 255);
    /// <summary>AI 応答色（紫）</summary>
    private static readonly Color COLOR_AI     = Color.FromRgb(160, 100, 220);
    /// <summary>エラーメッセージ色（赤）</summary>
    private static readonly Color COLOR_ERROR  = Color.FromRgb(220, 80,  60);
    /// <summary>ツールログ色（グレー）</summary>
    private static readonly Color COLOR_TOOL   = Color.FromRgb(120, 120, 120);

    // ── その他定数 ────────────────────────────────────────────────
    /// <summary>シーン情報応答のタイムアウト（秒）</summary>
    private const int SCENE_INFO_TIMEOUT_SEC  = 10;
    /// <summary>ローカル AI 向けの履歴トリム上限（文字数）</summary>
    private const int MAX_HISTORY_CHARS_LOCAL = 16_000;
    /// <summary>保持する最大チャットセッション数</summary>
    private const int MAX_SESSIONS            = 50;
    /// <summary>セッションタイトルに使用する先頭文字数</summary>
    private const int SESSION_TITLE_MAX_CHARS = 30;

    /// <summary>AI へ渡すシステムプロンプト。エンジン全知識をコンパクトに与える。</summary>
    private const string SYSTEM_PROMPT =
        "You are the SEED game engine editor assistant. Edit scenes with tools. Reply in Japanese.\n" +
        "\n" +
        "## Rules\n" +
        "1. Call get_scene_info before any edit and after every add_actor/remove_actor/add_component.\n" +
        "2. DFS IDs are re-indexed after structural changes — always re-fetch before use.\n" +
        "3. slot_idx is 0-based. Verify it from get_scene_info before calling set_value.\n" +
        "4. All set_value values are strings (numbers: \"45.0\", bool: \"true\"/\"false\", color: \"r,g,b,a\").\n" +
        "5. NEVER guess file paths. Before setting model_path / texture_path / asset_path, ALWAYS call list_asset_files first to get real absolute paths. Using a made-up path causes a runtime error.\n" +
        "\n" +
        "## Component types and set_value keys\n" +
        "\"Model\"     → model_path (absolute path to .glb — use list_asset_files subdirectory:'models' to find it)\n" +
        "\"Camera\"    → fov (deg, default 45), near (default 0.1), far (default 1000), is_main (\"true\"/\"false\"), clear_color (\"r,g,b,a\")\n" +
        "\"Sprite\"    → texture_path (absolute path — use list_asset_files), color (\"r,g,b,a\"), width, height  [must be child of Canvas actor]\n" +
        "\"Canvas\"    → width (default 1920), height (default 1080), auto_scale (\"true\"/\"false\")\n" +
        "\"InputMap\"  → asset_path (absolute path to .inputmap — use list_asset_files)\n" +
        "\"Script\"    → model_path (absolute path to .cs file — use list_asset_files subdirectory:'scripts')\n" +
        "\"Collider\"  → set_value NOT supported; tell user to configure in inspector\n" +
        "\"Rigidbody\" → set_value NOT supported; tell user to configure in inspector\n" +
        "\"Plugin:{Name}\" → any field key declared by the plugin (see get_scene_info for field names)\n" +
        "\n" +
        "## Coordinate system (Left-handed, Y-up, +Z forward)\n" +
        "+X = right, +Y = up, +Z = forward (the direction a camera with zero rotation looks).\n" +
        "To view an object placed at the origin, position the camera at NEGATIVE Z (e.g. z=-4.0, NOT z=+4.0).\n" +
        "Rotation: X = pitch (up/down tilt), Y = yaw (left/right turn), Z = roll.\n" +
        "\n" +
        "After all tool calls, briefly summarize in Japanese what was done.";

    // ── 永続化パス ────────────────────────────────────────────────
    private static readonly string APP_DATA_DIR  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SEED");
    private static readonly string SETTINGS_PATH = Path.Combine(APP_DATA_DIR, "ai_settings.json");
    private static readonly string SESSIONS_PATH = Path.Combine(APP_DATA_DIR, "ai_sessions.json");

    // ── チャット表示 UI ──────────────────────────────────────────
    /// <summary>会話テキスト表示（全メッセージにわたるテキスト選択に対応）</summary>
    private readonly RichTextBox _chatBox;
    /// <summary>メッセージ入力テキストボックス</summary>
    private readonly TextBox     _inputBox;
    /// <summary>送信ボタン</summary>
    private readonly Button      _sendButton;
    /// <summary>送信からの経過時間表示ラベル</summary>
    private readonly TextBlock   _elapsedLabel;
    /// <summary>ツールログ表示チェックボックス</summary>
    private readonly CheckBox    _showToolLogCheck;

    // ── ステータスバー UI ─────────────────────────────────────────
    /// <summary>現在のプロバイダー/モデル名を表示するラベル</summary>
    private readonly TextBlock _statusLabel;
    /// <summary>設定ポップアップを開く歯車ボタン</summary>
    private readonly Button    _gearButton;

    // ── 設定ポップアップ UI ───────────────────────────────────────
    /// <summary>設定ポップアップ（歯車ボタンの上に展開）</summary>
    private readonly Popup     _settingsPopup;
    private readonly ComboBox  _providerCombo;
    private readonly ComboBox  _modelCombo;
    private readonly TextBox   _apiKeyBox;
    private readonly TextBox   _endpointBox;
    private readonly Button    _fetchModelsButton;
    /// <summary>API キーラベル（ローカル AI では非表示）</summary>
    private readonly TextBlock _popupApiKeyLabel;
    /// <summary>エンドポイントラベル（OpenAI 互換向けのみ表示）</summary>
    private readonly TextBlock _popupEndpointLabel;

    // ── 履歴ダッシュボード UI ─────────────────────────────────────
    /// <summary>チャット表示パネル（通常時に表示）</summary>
    private readonly Grid       _chatPanel;
    /// <summary>履歴一覧パネル（履歴ボタン押下時に表示）</summary>
    private readonly Grid       _historyPanel;
    /// <summary>セッション一覧 StackPanel（動的にセッションエントリを追加）</summary>
    private readonly StackPanel _sessionListPanel;
    /// <summary>履歴/戻るトグルボタン（ヘッダーに配置）</summary>
    private readonly Button     _historyToggleButton;

    // ── 状態 ──────────────────────────────────────────────────────
    private readonly Runtime.RuntimeManager?     _runtime;
    private readonly string                      _assetsPath;
    private readonly LocalLlmManager             _localLlmManager;
    /// <summary>チャットセッション一覧（セッション永続化の対象）</summary>
    private readonly List<ChatSession>            _sessions = new();
    /// <summary>現在アクティブなセッション</summary>
    private          ChatSession?                 _currentSession;
    /// <summary>シーン情報の非同期受信用 TaskCompletionSource</summary>
    private          TaskCompletionSource<string>? _sceneInfoTcs;
    /// <summary>経過時間計測用タイマー</summary>
    private          DispatcherTimer?             _elapsedTimer;
    /// <summary>AI 送信開始時刻</summary>
    private          DateTime                     _sendStartTime;
    /// <summary>思考中インジケーターの段落参照（非 null = 表示中）</summary>
    private          Paragraph?                   _thinkingParagraph;

    // ── コンストラクタ ───────────────────────────────────────────

    /// <summary>
    /// AI アシスタントパネルを初期化する。
    /// 設定・セッション履歴を読み込み、UI 要素を生成する。
    /// </summary>
    public AIAssistantPanel(Runtime.RuntimeManager? runtime, string assetsPath)
    {
        _runtime    = runtime;
        _assetsPath = assetsPath;

        // llama-server.exe はエディタ実行ファイルと同じディレクトリに配置される想定
        var editorDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
        _localLlmManager = new LocalLlmManager(editorDir);

        // ランタイムからの SCENE_INFO イベントを購読する
        if (_runtime is not null)
            _runtime.SceneInfoReceived += OnSceneInfoReceived;

        // ── UI 要素の生成 ──────────────────────────────────────────
        // チャット表示エリア: RichTextBox で全メッセージにわたるテキスト選択を可能にする
        _chatBox = new RichTextBox
        {
            IsReadOnly        = true,
            Background        = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
            Foreground        = Brushes.White,
            BorderThickness   = new Thickness(0),
            Padding           = new Thickness(4),
            IsDocumentEnabled = true,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // PageWidth をコントロール幅に追従させて自動折り返しを有効にする
        // （固定値を設定するとその幅まで折り返されなくなるため SizeChanged で更新する）
        _chatBox.SizeChanged += (_, e) =>
            _chatBox.Document.PageWidth = Math.Max(1, e.NewSize.Width);
        _chatBox.Document.Blocks.Clear();

        // メッセージ入力ボックス（AcceptsReturn=true を維持し、Shift/Ctrl+Enter で改行）
        _inputBox = new TextBox
        {
            AcceptsReturn  = true,
            TextWrapping   = TextWrapping.Wrap,
            MinHeight      = 60,
            MaxHeight      = 120,
            Background     = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            Foreground     = Brushes.White,
            BorderBrush    = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            Padding        = new Thickness(6),
            FontSize       = 13,
            CaretBrush     = Brushes.White,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _sendButton = new Button
        {
            Content         = "送信",
            Width           = 60,
            Background      = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            Foreground      = Brushes.White,
            BorderThickness = new Thickness(0),
            Margin          = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        _elapsedLabel = new TextBlock
        {
            Foreground        = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            FontSize          = 10,
            Margin            = new Thickness(4, 0, 0, 2),
            Visibility        = Visibility.Collapsed,
        };

        _showToolLogCheck = new CheckBox
        {
            Content           = "ツールログ",
            Foreground        = Brushes.LightGray,
            IsChecked         = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 6, 0),
        };

        _statusLabel = new TextBlock
        {
            Foreground        = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(6, 0, 0, 0),
        };

        _gearButton = new Button
        {
            Content           = "⚙",
            Width             = 26,
            Height            = 26,
            FontSize          = 15,
            Background        = Brushes.Transparent,
            Foreground        = Brushes.LightGray,
            BorderThickness   = new Thickness(0),
            Cursor            = Cursors.Hand,
            Margin            = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "AI 設定",
        };

        // 設定ポップアップ内コントロール
        _providerCombo = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 0, 6) };
        _modelCombo    = new ComboBox { MinWidth = 160, IsEditable = true, Margin = new Thickness(0, 0, 0, 6) };
        _apiKeyBox = new TextBox
        {
            Width       = 200, Margin = new Thickness(0, 0, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            Foreground  = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
        };
        _endpointBox = new TextBox
        {
            Width       = 200, Margin = new Thickness(0, 0, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            Foreground  = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
        };
        _fetchModelsButton = new Button
        {
            Content         = "モデル取得",
            FontSize        = 11,
            Padding         = new Thickness(8, 3, 8, 3),
            Background      = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground      = Brushes.LightGray,
            BorderThickness = new Thickness(0),
            Margin          = new Thickness(0, 2, 0, 0),
        };
        _popupApiKeyLabel   = MakeSettingsLabel("API Key");
        _popupEndpointLabel = MakeSettingsLabel("Endpoint (OpenAI 互換)");
        _settingsPopup      = BuildSettingsPopup();

        // 履歴ダッシュボード関連
        _historyToggleButton = new Button
        {
            Content         = "履歴",
            FontSize        = 11,
            Padding         = new Thickness(8, 2, 8, 2),
            Background      = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground      = Brushes.LightGray,
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
        };
        _sessionListPanel = new StackPanel { Orientation = Orientation.Vertical };
        _chatPanel        = new Grid();
        _historyPanel     = BuildHistoryPanel();

        // 設定・セッション履歴を読み込む
        InitProviderCombo();
        LoadSettings();
        LoadSessions();

        // セッションがなければ新規作成する
        if (_sessions.Count == 0)
            NewSession();
        else
            SwitchSession(_sessions[^1]); // 最新セッションを読み込む

        WireEvents();
        UpdateStatusLabel();
        _ = CheckLocalAiStatusAsync();
    }

    // ── 公開メソッド ─────────────────────────────────────────────

    /// <summary>
    /// パネルの WPF 要素ツリーを構築して返す。
    /// MainWindow のグリッドや DockPanel に追加して使用する。
    /// </summary>
    public UIElement Build()
    {
        var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ヘッダー
        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // チャットパネル（_chatPanel に RichTextBox を配置）
        BuildChatPanelContent();
        Grid.SetRow(_chatPanel, 1);
        root.Children.Add(_chatPanel);

        // 履歴パネル（初期は非表示）
        Grid.SetRow(_historyPanel, 1);
        _historyPanel.Visibility = Visibility.Collapsed;
        root.Children.Add(_historyPanel);

        // 入力エリア
        var inputRow = BuildInputRow();
        Grid.SetRow(inputRow, 2);
        root.Children.Add(inputRow);

        // ステータスバー
        var statusBar = BuildStatusBar();
        Grid.SetRow(statusBar, 3);
        root.Children.Add(statusBar);

        return root;
    }

    // ── UI 構築メソッド ──────────────────────────────────────────

    /// <summary>ヘッダー行（タイトル + 履歴トグルボタン）を構築する。</summary>
    private UIElement BuildHeader()
    {
        var grid = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text              = "AI アシスタント",
            Foreground        = Brushes.White,
            FontSize          = 13,
            FontWeight        = FontWeights.SemiBold,
            Padding           = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        _historyToggleButton.Margin = new Thickness(0, 4, 8, 4);
        Grid.SetColumn(_historyToggleButton, 1);
        grid.Children.Add(_historyToggleButton);

        return grid;
    }

    /// <summary>_chatPanel に RichTextBox を配置する。</summary>
    private void BuildChatPanelContent()
    {
        // RichTextBox を直接 _chatPanel に追加する（内蔵スクロールを使用）
        _chatPanel.Children.Add(_chatBox);
    }

    /// <summary>メッセージ入力行（経過時間ラベル + 入力ボックス + 送信ボタン）を構築する。</summary>
    private UIElement BuildInputRow()
    {
        var outer = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin      = new Thickness(4, 2, 4, 2),
        };

        // 送信中のみ表示される経過時間ラベル
        outer.Children.Add(_elapsedLabel);

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_inputBox,   0);
        Grid.SetColumn(_sendButton, 1);
        inner.Children.Add(_inputBox);
        inner.Children.Add(_sendButton);

        outer.Children.Add(inner);
        return outer;
    }

    /// <summary>ステータスバー（プロバイダー情報 + 歯車ボタン + ツールログ）を構築する。</summary>
    private UIElement BuildStatusBar()
    {
        var panel = new DockPanel
        {
            Background    = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
            LastChildFill = false,
            Height        = 30,
        };

        // ツールログチェックボックス（右端）
        DockPanel.SetDock(_showToolLogCheck, Dock.Right);
        panel.Children.Add(_showToolLogCheck);

        // 歯車ボタン（右端から 2 番目）
        DockPanel.SetDock(_gearButton, Dock.Right);
        panel.Children.Add(_gearButton);

        // プロバイダー/モデルラベル（左詰め）
        DockPanel.SetDock(_statusLabel, Dock.Left);
        panel.Children.Add(_statusLabel);

        return panel;
    }

    /// <summary>
    /// 設定ポップアップを構築する。
    /// 歯車ボタンの上に展開し、プロバイダー・モデル・API キーを編集できる。
    /// </summary>
    private Popup BuildSettingsPopup()
    {
        var popup = new Popup
        {
            AllowsTransparency = true,
            PopupAnimation     = PopupAnimation.Fade,
            StaysOpen          = false, // ポップアップ外クリックで自動的に閉じる
            Placement          = PlacementMode.Top,
        };

        var border = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(12, 10, 12, 12),
            CornerRadius    = new CornerRadius(4),
            MinWidth        = 260,
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        // タイトル
        stack.Children.Add(new TextBlock
        {
            Text       = "AI 設定",
            Foreground = Brushes.White,
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 8),
        });

        stack.Children.Add(MakeSettingsLabel("プロバイダー"));
        stack.Children.Add(_providerCombo);
        stack.Children.Add(MakeSettingsLabel("モデル"));
        stack.Children.Add(_modelCombo);
        stack.Children.Add(_popupApiKeyLabel);
        stack.Children.Add(_apiKeyBox);
        stack.Children.Add(_popupEndpointLabel);
        stack.Children.Add(_endpointBox);
        stack.Children.Add(_fetchModelsButton);

        border.Child = stack;
        popup.Child  = border;

        return popup;
    }

    /// <summary>
    /// 履歴ダッシュボードパネルを構築する。
    /// セッション一覧と「新しいチャット」ボタンを含む。
    /// </summary>
    private Grid BuildHistoryPanel()
    {
        var panel = new Grid { Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 「新しいチャット」ボタン
        var newChatBtn = new Button
        {
            Content         = "+ 新しいチャット",
            FontSize        = 12,
            Padding         = new Thickness(10, 6, 10, 6),
            Margin          = new Thickness(8, 8, 8, 4),
            Background      = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            Foreground      = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        newChatBtn.Click += (_, _) =>
        {
            NewSession();
            ShowChatPanel();
        };
        Grid.SetRow(newChatBtn, 0);
        panel.Children.Add(newChatBtn);

        // セッション一覧スクロールエリア
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _sessionListPanel,
            Margin  = new Thickness(4, 0, 4, 4),
        };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);

        return panel;
    }

    // ── セッション管理 ──────────────────────────────────────────

    /// <summary>
    /// 新しいチャットセッションを作成し、アクティブにする。
    /// セッション数が上限を超えた場合は最古のセッションを削除する。
    /// </summary>
    private void NewSession()
    {
        var session = new ChatSession();
        _sessions.Add(session);

        // 上限を超えた場合は古いセッションを削除する
        while (_sessions.Count > MAX_SESSIONS)
            _sessions.RemoveAt(0);

        SwitchSession(session);
        SaveSessions();
    }

    /// <summary>
    /// 指定セッションに切り替え、チャット表示を再レンダリングする。
    /// </summary>
    private void SwitchSession(ChatSession session)
    {
        _currentSession = session;
        // チャットエリアをリセットして再レンダリングする
        _chatBox.Document.Blocks.Clear();
        foreach (var item in session.DisplayItems)
            RenderDisplayItem(item);
        // RenderDisplayItem は BeginInvoke で非同期に段落を追加するため、
        // すべての追加が完了した後（Loaded 優先度）にスクロールする
        Application.Current.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(_chatBox.ScrollToEnd));
    }

    /// <summary>指定セッションを削除し、必要に応じて新しいセッションに切り替える。</summary>
    private void DeleteSession(ChatSession session)
    {
        _sessions.Remove(session);
        RefreshSessionList();

        // 削除したセッションが現在のセッションだった場合は別セッションに切り替える
        if (_currentSession == session)
        {
            if (_sessions.Count > 0)
                SwitchSession(_sessions[^1]);
            else
                NewSession();
        }
        SaveSessions();
    }

    /// <summary>セッション一覧 StackPanel を再構築する。</summary>
    private void RefreshSessionList()
    {
        _sessionListPanel.Children.Clear();

        // 新しいセッション順（末尾 = 最新）で表示する
        for (int i = _sessions.Count - 1; i >= 0; i--)
        {
            var session = _sessions[i];
            _sessionListPanel.Children.Add(BuildSessionEntry(session));
        }
    }

    /// <summary>セッション一覧の 1 エントリを構築して返す。</summary>
    private UIElement BuildSessionEntry(ChatSession session)
    {
        var isActive = session == _currentSession;

        var border = new Border
        {
            Background      = isActive
                ? new SolidColorBrush(Color.FromArgb(60, 160, 100, 220)) // アクティブは紫薄背景
                : Brushes.Transparent,
            BorderBrush     = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(8, 6, 8, 6),
            Cursor          = Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // タイトルと日時
        var infoStack = new StackPanel();
        infoStack.Children.Add(new TextBlock
        {
            Text         = session.Title,
            Foreground   = Brushes.White,
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        infoStack.Children.Add(new TextBlock
        {
            Text       = session.CreatedAt,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130)),
            FontSize   = 10,
        });
        Grid.SetColumn(infoStack, 0);
        grid.Children.Add(infoStack);

        // 削除ボタン
        var deleteBtn = new Button
        {
            Content         = "×",
            Width           = 22,
            Height          = 22,
            FontSize        = 12,
            Background      = Brushes.Transparent,
            Foreground      = new SolidColorBrush(Color.FromRgb(180, 80, 80)),
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        deleteBtn.Click += (_, e) =>
        {
            e.Handled = true;
            DeleteSession(session);
        };
        Grid.SetColumn(deleteBtn, 1);
        grid.Children.Add(deleteBtn);

        border.Child = grid;

        // クリックでセッション切り替えしてチャットパネルを表示する
        border.MouseLeftButtonUp += (_, _) =>
        {
            SwitchSession(session);
            ShowChatPanel();
        };

        return border;
    }

    /// <summary>チャットパネルを表示し、履歴パネルを非表示にする。</summary>
    private void ShowChatPanel()
    {
        _chatPanel.Visibility    = Visibility.Visible;
        _historyPanel.Visibility = Visibility.Collapsed;
        _historyToggleButton.Content = "履歴";
    }

    /// <summary>履歴パネルを表示し、チャットパネルを非表示にする。</summary>
    private void ShowHistoryPanel()
    {
        RefreshSessionList();
        _chatPanel.Visibility    = Visibility.Collapsed;
        _historyPanel.Visibility = Visibility.Visible;
        _historyToggleButton.Content = "← 戻る";
    }

    // ── 設定管理 ─────────────────────────────────────────────────

    /// <summary>設定を AppData の JSON ファイルから読み込み、UI に反映する。</summary>
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SETTINGS_PATH)) return;

            var json     = File.ReadAllText(SETTINGS_PATH);
            var settings = JsonSerializer.Deserialize<AIPanelSettings>(json);
            if (settings is null) return;

            // プロバイダーインデックスの範囲チェック
            if (settings.ProviderIndex >= 0 && settings.ProviderIndex < _providerCombo.Items.Count)
                _providerCombo.SelectedIndex = settings.ProviderIndex;

            _modelCombo.Text          = settings.Model;
            _apiKeyBox.Text           = settings.ApiKey;
            _endpointBox.Text         = settings.Endpoint;
            _showToolLogCheck.IsChecked = settings.ShowToolLog;
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AIAssistantPanel — 設定読み込み失敗: {ex.Message}");
        }
    }

    /// <summary>現在の UI 状態を設定ファイルに保存する。</summary>
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(APP_DATA_DIR);
            var settings = new AIPanelSettings
            {
                ProviderIndex = _providerCombo.SelectedIndex,
                Model         = _modelCombo.Text ?? "",
                ApiKey        = _apiKeyBox.Text  ?? "",
                Endpoint      = _endpointBox.Text ?? "",
                ShowToolLog   = _showToolLogCheck.IsChecked == true,
            };
            File.WriteAllText(SETTINGS_PATH,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AIAssistantPanel — 設定保存失敗: {ex.Message}");
        }
    }

    /// <summary>セッション履歴を JSON ファイルから読み込む。</summary>
    private void LoadSessions()
    {
        try
        {
            if (!File.Exists(SESSIONS_PATH)) return;

            var json     = File.ReadAllText(SESSIONS_PATH);
            var sessions = JsonSerializer.Deserialize<List<ChatSession>>(json);
            if (sessions is null) return;

            _sessions.Clear();
            _sessions.AddRange(sessions);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AIAssistantPanel — セッション読み込み失敗: {ex.Message}");
        }
    }

    /// <summary>全セッションを JSON ファイルに保存する。</summary>
    private void SaveSessions()
    {
        try
        {
            Directory.CreateDirectory(APP_DATA_DIR);
            File.WriteAllText(SESSIONS_PATH,
                JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AIAssistantPanel — セッション保存失敗: {ex.Message}");
        }
    }

    /// <summary>ステータスバーの表示ラベルを現在の設定に基づいて更新する。</summary>
    private void UpdateStatusLabel()
    {
        var providerName = _providerCombo.SelectedIndex switch
        {
            PROVIDER_LOCAL_AI  => "ローカル AI",
            PROVIDER_OPENAI    => "OpenAI 互換",
            PROVIDER_ANTHROPIC => "Anthropic",
            PROVIDER_GEMINI    => "Gemini",
            _                  => "不明",
        };
        var model = _modelCombo.Text ?? "";
        _statusLabel.Text = string.IsNullOrWhiteSpace(model)
            ? providerName
            : $"{providerName} / {model}";
    }

    /// <summary>プロバイダーコンボボックスの項目を初期化する（Ollama なし）。</summary>
    private void InitProviderCombo()
    {
        _providerCombo.Items.Add("ローカル AI (Qwen2.5-Coder)"); // PROVIDER_LOCAL_AI  = 0
        _providerCombo.Items.Add("OpenAI 互換");                  // PROVIDER_OPENAI    = 1
        _providerCombo.Items.Add("Anthropic");                    // PROVIDER_ANTHROPIC = 2
        _providerCombo.Items.Add("Google Gemini");                // PROVIDER_GEMINI    = 3
        _providerCombo.SelectedIndex = PROVIDER_LOCAL_AI;

        UpdateModelCombo();
        UpdateProviderUi(_popupApiKeyLabel, _popupEndpointLabel);
    }

    /// <summary>
    /// 選択中のプロバイダーに応じてポップアップ内の表示要素を切り替える。
    /// ローカル AI では API キーと Endpoint を非表示にする。
    /// </summary>
    private void UpdateProviderUi(TextBlock apiKeyLabel, TextBlock endpointLabel)
    {
        var idx       = _providerCombo.SelectedIndex;
        var isLocalAi = idx == PROVIDER_LOCAL_AI;
        var isOpenAi  = idx == PROVIDER_OPENAI;
        var isGemini  = idx == PROVIDER_GEMINI;

        // API キー欄: ローカル AI では不要
        var apiKeyVis = isLocalAi ? Visibility.Collapsed : Visibility.Visible;
        apiKeyLabel.Visibility = apiKeyVis;
        _apiKeyBox.Visibility  = apiKeyVis;

        // Endpoint 欄: OpenAI 互換のみ表示
        var endpointVis = isOpenAi ? Visibility.Visible : Visibility.Collapsed;
        endpointLabel.Visibility  = endpointVis;
        _endpointBox.Visibility   = endpointVis;

        // モデル取得ボタン: Gemini のみ表示
        _fetchModelsButton.Visibility = isGemini ? Visibility.Visible : Visibility.Collapsed;

        UpdateModelCombo();
        UpdateStatusLabel();
    }

    /// <summary>選択中のプロバイダーに合わせたモデル候補をコンボに設定する。</summary>
    private void UpdateModelCombo()
    {
        var prevModel = _modelCombo.Text;
        _modelCombo.Items.Clear();

        switch (_providerCombo.SelectedIndex)
        {
            case PROVIDER_LOCAL_AI:
                _modelCombo.Items.Add("qwen2.5-coder");
                break;
            case PROVIDER_ANTHROPIC:
                _modelCombo.Items.Add("claude-opus-4-7");
                _modelCombo.Items.Add("claude-sonnet-4-6");
                _modelCombo.Items.Add("claude-haiku-4-5-20251001");
                break;
            case PROVIDER_GEMINI:
                _modelCombo.Items.Add("gemini-2.5-flash-preview-04-17");
                _modelCombo.Items.Add("gemini-2.5-pro-preview-05-06");
                _modelCombo.Items.Add("gemini-2.0-flash");
                _modelCombo.Items.Add("gemini-1.5-flash-latest");
                _modelCombo.Items.Add("gemini-1.5-pro-latest");
                break;
            default: // PROVIDER_OPENAI
                _modelCombo.Items.Add("gpt-4o");
                _modelCombo.Items.Add("gpt-4o-mini");
                _modelCombo.Items.Add("o3-mini");
                break;
        }

        // 以前の選択を可能な限り復元する
        if (!string.IsNullOrEmpty(prevModel) && _modelCombo.Items.Contains(prevModel))
            _modelCombo.Text = prevModel;
        else if (_modelCombo.Items.Count > 0)
            _modelCombo.SelectedIndex = 0;
    }

    // ── Gemini モデル取得 ────────────────────────────────────────

    /// <summary>
    /// Gemini の /v1beta/models エンドポイントから利用可能モデル一覧を取得する。
    /// generateContent をサポートするモデルのみリストに表示する。
    /// </summary>
    private async Task FetchGeminiModelsAsync(bool showMessageOnSuccess = false)
    {
        var apiKey = _apiKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (showMessageOnSuccess)
                AppendSystemMessage("API キーを入力してから「モデル取得」を押してください。");
            UpdateModelCombo();
            return;
        }

        _fetchModelsButton.IsEnabled = false;
        _fetchModelsButton.Content   = "取得中...";
        var prevModel = _modelCombo.Text;
        SetModelComboError("取得中...");
        _modelCombo.IsEnabled = false;

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
            using var http   = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var responseText = await http.GetStringAsync(url);
            using var doc    = System.Text.Json.JsonDocument.Parse(responseText);

            if (!doc.RootElement.TryGetProperty("models", out var models))
            {
                SetModelComboError("取得失敗");
                return;
            }

            _modelCombo.Items.Clear();
            foreach (var m in models.EnumerateArray())
            {
                if (!m.TryGetProperty("supportedGenerationMethods", out var methods)) continue;
                var supportsGenerate = methods.EnumerateArray()
                    .Any(x => x.GetString() == "generateContent");
                if (!supportsGenerate) continue;

                if (!m.TryGetProperty("name", out var nameEl)) continue;
                var full  = nameEl.GetString() ?? "";
                var short_ = full.StartsWith("models/") ? full["models/".Length..] : full;
                if (!string.IsNullOrEmpty(short_))
                    _modelCombo.Items.Add(short_);
            }

            if (_modelCombo.Items.Count == 0) { SetModelComboError("モデルなし"); return; }

            if (!string.IsNullOrEmpty(prevModel) && _modelCombo.Items.Contains(prevModel))
                _modelCombo.Text = prevModel;
            else
                _modelCombo.SelectedIndex = 0;

            if (showMessageOnSuccess)
                AppendSystemMessage($"Gemini から {_modelCombo.Items.Count} 件のモデルを取得しました。");
        }
        catch (Exception ex)
        {
            SetModelComboError("取得失敗");
            if (showMessageOnSuccess)
                AppendErrorMessage($"モデル一覧の取得に失敗しました。\n{ex.Message}");
            else
                UpdateModelCombo();
        }
        finally
        {
            _modelCombo.IsEnabled        = true;
            _fetchModelsButton.IsEnabled = true;
            _fetchModelsButton.Content   = "モデル取得";
        }
    }

    /// <summary>モデルコンボにエラープレースホルダーを設定する。</summary>
    private void SetModelComboError(string placeholder)
    {
        _modelCombo.Items.Clear();
        _modelCombo.Items.Add(placeholder);
        _modelCombo.SelectedIndex = 0;
    }

    // ── イベント接続 ─────────────────────────────────────────────

    /// <summary>各 UI 要素のイベントハンドラを接続する。</summary>
    private void WireEvents()
    {
        // 設定ポップアップの PlacementTarget を歯車ボタンに設定する
        _settingsPopup.PlacementTarget = _gearButton;

        // 歯車ボタン: 設定ポップアップの開閉
        _gearButton.Click += (_, _) => _settingsPopup.IsOpen = !_settingsPopup.IsOpen;

        // 履歴トグルボタン: チャット/履歴パネルを切り替える
        _historyToggleButton.Click += (_, _) =>
        {
            if (_historyPanel.Visibility == Visibility.Visible)
                ShowChatPanel();
            else
                ShowHistoryPanel();
        };

        // 送信ボタン
        _sendButton.Click += async (_, _) => await SendMessageAsync();

        // Enter キーで送信、Shift+Enter / Ctrl+Enter で改行
        _inputBox.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Return)
            {
                var hasModifier = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
                if (!hasModifier)
                {
                    // 修飾キーなしの Enter → 送信
                    e.Handled = true;
                    await SendMessageAsync();
                }
                // Shift+Enter / Ctrl+Enter はデフォルト動作（改行挿入）を通す
            }
        };

        // プロバイダー変更時の自動更新と設定保存
        _providerCombo.SelectionChanged += (_, _) =>
        {
            UpdateProviderUi(_popupApiKeyLabel, _popupEndpointLabel);
            SaveSettings();
            UpdateStatusLabel();

            // Gemini 選択時に API キーがあればモデルを自動取得する
            if (_providerCombo.SelectedIndex == PROVIDER_GEMINI
                && !string.IsNullOrWhiteSpace(_apiKeyBox.Text))
                _ = FetchGeminiModelsAsync();
        };

        // モデル変更時の自動保存
        _modelCombo.SelectionChanged += (_, _) => { SaveSettings(); UpdateStatusLabel(); };
        _modelCombo.LostFocus        += (_, _) => { SaveSettings(); UpdateStatusLabel(); };

        // API キー / Endpoint 変更時の自動保存
        _apiKeyBox.LostFocus    += (_, _) => SaveSettings();
        _endpointBox.LostFocus  += (_, _) => SaveSettings();

        // ツールログチェックボックス変更時の自動保存
        _showToolLogCheck.Checked   += (_, _) => SaveSettings();
        _showToolLogCheck.Unchecked += (_, _) => SaveSettings();

        // モデル取得ボタン（Gemini のみ）
        _fetchModelsButton.Click += async (_, _) => await FetchGeminiModelsAsync(showMessageOnSuccess: true);
    }

    // ── メッセージ送受信 ──────────────────────────────────────────

    /// <summary>
    /// ユーザーのメッセージを AI に送信し、応答を処理するメインエージェントループ。
    /// ツール呼び出しが返ってきた場合は実行して結果を返し、再度 AI に送信する。
    /// </summary>
    private async Task SendMessageAsync()
    {
        var userText = _inputBox.Text.Trim();
        if (string.IsNullOrEmpty(userText)) return;
        if (_currentSession is null) return;

        // ローカル AI: サーバーが未起動の場合は起動する
        if (_providerCombo.SelectedIndex == PROVIDER_LOCAL_AI && !_localLlmManager.IsServerRunning)
        {
            _sendButton.IsEnabled = false;
            _inputBox.IsEnabled   = false;
            _runtime?.PauseRendering();

            var started = await _localLlmManager.EnsureServerRunningAsync(
                onProgress: msg => AppendSystemMessage(msg));
            if (!started)
            {
                _runtime?.ResumeRendering();
                _sendButton.IsEnabled = true;
                _inputBox.IsEnabled   = true;
                return;
            }
            _sendButton.IsEnabled = true;
            _inputBox.IsEnabled   = true;
        }

        // 入力をクリアして UI を無効化する
        _inputBox.Clear();
        _sendButton.IsEnabled = false;
        _inputBox.IsEnabled   = false;

        var isLocalAi = _providerCombo.SelectedIndex == PROVIDER_LOCAL_AI;
        if (isLocalAi) _runtime?.PauseRendering();

        // 経過時間タイマーを開始する
        StartElapsedTimer();

        try
        {
            // ユーザーメッセージを履歴に追加して表示する
            var userMsg = new ChatMessage { Role = "user", Content = userText };
            _currentSession.Messages.Add(userMsg);

            // セッションタイトルを最初のメッセージから設定する
            if (_currentSession.Title == "新しいチャット" && !string.IsNullOrEmpty(userText))
            {
                _currentSession.Title = userText.Length > SESSION_TITLE_MAX_CHARS
                    ? userText[..SESSION_TITLE_MAX_CHARS] + "…"
                    : userText;
            }

            AppendUserMessage(userText);

            var provider = CreateProvider();
            var systemMsg = new ChatMessage { Role = "system", Content = SYSTEM_PROMPT };
            var messagesWithSystem = new List<ChatMessage> { systemMsg };
            messagesWithSystem.AddRange(
                TrimHistory(_currentSession.Messages, isLocalAi ? MAX_HISTORY_CHARS_LOCAL : int.MaxValue));

            var tools    = EditorToolDefinitions.All();
            var executor = CreateExecutor();
            const int MAX_TOOL_ROUNDS = 10;

            for (int round = 0; round < MAX_TOOL_ROUNDS; round++)
            {
                // 思考中インジケーターを表示する
                AppendThinkingIndicator(isLocalAi);
                AIResponse response;
                try
                {
                    response = await provider.ChatAsync(messagesWithSystem, tools);
                }
                finally
                {
                    RemoveThinkingIndicator();
                }

                // アシスタントメッセージを履歴に記録する
                var assistantMsg = new ChatMessage
                {
                    Role               = "assistant",
                    Content            = response.TextContent ?? "",
                    ToolCalls          = response.ToolCalls.Count > 0 ? response.ToolCalls : null,
                    RawGeminiPartsJson = response.RawGeminiPartsJson,
                };
                _currentSession.Messages.Add(assistantMsg);
                messagesWithSystem.Add(assistantMsg);

                if (!string.IsNullOrEmpty(response.TextContent))
                    AppendAiMessage(response.TextContent);

                // ツール呼び出しがなければ完了
                if (!response.HasToolCalls) break;

                // 各ツールを実行して結果を履歴に追加する
                foreach (var tc in response.ToolCalls)
                {
                    var result = await executor.ExecuteAsync(tc);

                    if (_showToolLogCheck.IsChecked == true)
                        AppendToolLog(tc.FunctionName, result);

                    var toolResultMsg = new ChatMessage
                    {
                        Role       = "tool",
                        Content    = result,
                        ToolCallId = tc.Id,
                    };
                    _currentSession.Messages.Add(toolResultMsg);
                    messagesWithSystem.Add(toolResultMsg);
                }
            }

            // セッションを保存する
            SaveSessions();
        }
        catch (Exception ex)
        {
            AppendErrorMessage(ex.Message);
        }
        finally
        {
            StopElapsedTimer();
            if (isLocalAi) _runtime?.ResumeRendering();
            _sendButton.IsEnabled = true;
            _inputBox.IsEnabled   = true;
            _inputBox.Focus();
        }
    }

    // ── プロバイダー・エグゼキューター生成 ──────────────────────

    /// <summary>選択中のプロバイダーを生成して返す。</summary>
    private IAIProvider CreateProvider()
    {
        var idx       = _providerCombo.SelectedIndex;
        var isLocalAi = idx == PROVIDER_LOCAL_AI;

        var settings = new AISettings
        {
            ApiKey         = isLocalAi ? "" : _apiKeyBox.Text.Trim(),
            Endpoint       = isLocalAi ? _localLlmManager.Endpoint : _endpointBox.Text.Trim(),
            Model          = (_modelCombo.Text ?? "").Trim(),
            // ローカル AI はモデルロード＋推論に時間がかかるため長めのタイムアウトを設定する
            TimeoutSeconds = isLocalAi ? 600 : 120,
        };

        return idx switch
        {
            PROVIDER_ANTHROPIC => new AnthropicProvider(settings),
            PROVIDER_GEMINI    => new GeminiProvider(settings),
            _                  => new OpenAICompatibleProvider(settings), // LOCAL_AI と OPENAI
        };
    }

    /// <summary>エディタコマンド実行エンジンを生成して返す。</summary>
    private EditorCommandExecutor CreateExecutor()
    {
        return new EditorCommandExecutor(
            sendToRuntime:     msg => _runtime?.SendToRuntime(msg),
            getSceneInfoAsync: GetSceneInfoAsync,
            assetsPath:        _assetsPath,
            log:               msg => Application.Current.Dispatcher.BeginInvoke(
                                   () => AppendToolLog("LOG", msg)));
    }

    // ── チャット表示ヘルパー ─────────────────────────────────────

    /// <summary>ユーザーメッセージをチャットに追加し、セッション DisplayItems にも記録する。</summary>
    private void AppendUserMessage(string text)
        => AppendAndRecord("あなた", text, "user", COLOR_USER);

    /// <summary>AI 応答をチャットに追加し、セッション DisplayItems にも記録する。</summary>
    private void AppendAiMessage(string text)
        => AppendAndRecord("AI", text, "ai", COLOR_AI);

    /// <summary>システムメッセージをチャットに追加し、セッション DisplayItems にも記録する。</summary>
    private void AppendSystemMessage(string text)
        => AppendAndRecord("システム", text, "system", COLOR_SYSTEM);

    /// <summary>エラーメッセージをチャットに追加し、セッション DisplayItems にも記録する。</summary>
    private void AppendErrorMessage(string text)
        => AppendAndRecord("エラー", text, "error", COLOR_ERROR);

    /// <summary>
    /// メッセージを RichTextBox に追加し、セッションの DisplayItems に記録する。
    /// セッション切り替え時の再レンダリングに使用する。
    /// </summary>
    private void AppendAndRecord(string sender, string text, string colorType, Color color)
    {
        if (_currentSession is not null)
        {
            _currentSession.DisplayItems.Add(new ChatDisplayItem
            {
                Sender    = sender,
                Text      = text,
                ColorType = colorType,
            });
        }
        RenderParagraph(sender, text, color);
    }

    /// <summary>
    /// ツール実行ログをチャットに表示する（DisplayItems には記録しない）。
    /// </summary>
    private void AppendToolLog(string toolName, string result)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var para = new Paragraph
            {
                Margin = new Thickness(16, 0, 4, 0),
            };
            para.Inlines.Add(new Run($"[{toolName}] {result}")
            {
                Foreground = new SolidColorBrush(COLOR_TOOL),
                FontSize   = 10,
                FontFamily = new FontFamily("Consolas"),
            });
            _chatBox.Document.Blocks.Add(para);
            Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(_chatBox.ScrollToEnd));
        });
    }

    /// <summary>思考中インジケーター段落をチャットに追加する。</summary>
    private void AppendThinkingIndicator(bool isLocalAi)
    {
        var text  = isLocalAi ? "AI 実行中... (レンダリング停止中)" : "AI が考えています...";
        var color = isLocalAi
            ? Color.FromRgb(220, 160, 60)  // オレンジ: レンダリング停止中を強調
            : Color.FromRgb(120, 120, 120); // グレー: 通常の思考中

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _thinkingParagraph = new Paragraph
            {
                Margin = new Thickness(4, 2, 4, 2),
            };
            _thinkingParagraph.Inlines.Add(new Run(text)
            {
                Foreground = new SolidColorBrush(color),
                FontStyle  = FontStyles.Italic,
                FontSize   = 11,
            });
            _chatBox.Document.Blocks.Add(_thinkingParagraph);
            Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(_chatBox.ScrollToEnd));
        });
    }

    /// <summary>思考中インジケーター段落をチャットから削除する。</summary>
    private void RemoveThinkingIndicator()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_thinkingParagraph is null) return;
            _chatBox.Document.Blocks.Remove(_thinkingParagraph);
            _thinkingParagraph = null;
        });
    }

    /// <summary>
    /// ChatDisplayItem を RichTextBox に段落として追加する（セッション復元用）。
    /// </summary>
    private void RenderDisplayItem(ChatDisplayItem item)
    {
        var color = item.ColorType switch
        {
            "user"  => COLOR_USER,
            "ai"    => COLOR_AI,
            "error" => COLOR_ERROR,
            "tool"  => COLOR_TOOL,
            _       => COLOR_SYSTEM,
        };
        RenderParagraph(item.Sender, item.Text, color);
    }

    /// <summary>
    /// 発話者名 + テキストを 1 段落として RichTextBox に追加する。
    /// メッセージ色を半透明にした背景をつけることで、送信者をひと目で識別しやすくする。
    /// UI スレッドで実行する必要がある（BeginInvoke 経由で呼び出す）。
    /// </summary>
    private void RenderParagraph(string sender, string text, Color color)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // メッセージ背景: テキスト色を半透明化して帯状に表示する
            var bg   = Color.FromArgb(40, color.R, color.G, color.B);
            var para = new Paragraph
            {
                Margin     = new Thickness(4, 6, 4, 6),
                Padding    = new Thickness(6, 5, 6, 6),
                Background = new SolidColorBrush(bg),
            };

            // 発話者名（太字・カラー付き）
            para.Inlines.Add(new Run(sender)
            {
                Foreground = new SolidColorBrush(color),
                FontWeight = FontWeights.Bold,
                FontSize   = 11,
            });
            para.Inlines.Add(new Run(": "));

            // メッセージ本文
            para.Inlines.Add(new Run(text)
            {
                Foreground = Brushes.WhiteSmoke,
                FontSize   = 12,
            });

            _chatBox.Document.Blocks.Add(para);
            // レイアウト確定後にスクロール（BeginInvoke 内で直接 ScrollToEnd すると
            // ドキュメント高さ未確定のまま実行され最終行が見切れるため二段ディスパッチにする）
            Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(_chatBox.ScrollToEnd));
        });
    }

    // ── 経過時間タイマー ─────────────────────────────────────────

    /// <summary>送信からの経過時間タイマーを開始し、ラベルを表示する。</summary>
    private void StartElapsedTimer()
    {
        _sendStartTime        = DateTime.UtcNow;
        _elapsedLabel.Text    = "送信から 0.0 秒";
        _elapsedLabel.Visibility = Visibility.Visible;

        _elapsedTimer          = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _elapsedTimer.Tick    += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - _sendStartTime).TotalSeconds;
            _elapsedLabel.Text = $"送信から {elapsed:F1} 秒";
        };
        _elapsedTimer.Start();
    }

    /// <summary>経過時間タイマーを停止し、ラベルを非表示にする。</summary>
    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
        Application.Current.Dispatcher.BeginInvoke(
            () => _elapsedLabel.Visibility = Visibility.Collapsed);
    }

    // ── 履歴トリム ───────────────────────────────────────────────

    /// <summary>
    /// 履歴を末尾から maxChars 文字以内に収まるようにトリムして返す。
    /// ユーザーメッセージ境界で切り捨てることで役割整合性を保つ。
    /// maxChars が int.MaxValue の場合は全メッセージをそのまま返す。
    /// </summary>
    private static IEnumerable<ChatMessage> TrimHistory(List<ChatMessage> history, int maxChars)
    {
        if (maxChars == int.MaxValue || history.Count == 0)
            return history;

        // 末尾から文字数を積算し、上限に収まる先頭インデックスを求める
        int total      = 0;
        int startIndex = history.Count;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            int msgLen = (history[i].Content?.Length ?? 0)
                       + (history[i].ToolCalls?.Sum(tc =>
                               (tc.FunctionName?.Length ?? 0) + (tc.ArgumentsJson?.Length ?? 0))
                           ?? 0);

            if (total + msgLen > maxChars) break;
            total     += msgLen;
            startIndex = i;
        }

        // 最低でも最新 1 件は残す
        if (startIndex >= history.Count)
            startIndex = history.Count - 1;

        // ユーザーメッセージから開始するよう境界を調整する
        while (startIndex < history.Count - 1 && history[startIndex].Role != "user")
            startIndex++;

        return history.Skip(startIndex);
    }

    // ── シーン情報非同期取得 ────────────────────────────────────

    /// <summary>
    /// GET_SCENE_INFO コマンドを送信し、ランタイムからの応答を非同期で待機する。
    /// タイムアウト（10 秒）を超えた場合はエラーメッセージを返す。
    /// </summary>
    private async Task<string> GetSceneInfoAsync()
    {
        // TCS を作成して待機する。コマンド送信は EditorCommandExecutor 側で行う。
        _sceneInfoTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(SCENE_INFO_TIMEOUT_SEC));
        var completed   = await Task.WhenAny(_sceneInfoTcs.Task, timeoutTask);

        if (completed == timeoutTask)
            return """{"error":"タイムアウト: シーン情報を取得できませんでした"}""";

        return await _sceneInfoTcs.Task;
    }

    /// <summary>ランタイムから SCENE_INFO を受信したときのハンドラ。</summary>
    private void OnSceneInfoReceived(string json)
    {
        _sceneInfoTcs?.TrySetResult(json);
    }

    // ── ローカル AI ステータス確認 ────────────────────────────────

    /// <summary>
    /// ローカル AI の状態をチェックし、必要に応じてチャットにステータスを表示する。
    /// llama-server.exe の有無とモデルのダウンロード状況を確認する。
    /// </summary>
    private async Task CheckLocalAiStatusAsync()
    {
        if (_providerCombo.SelectedIndex != PROVIDER_LOCAL_AI) return;
        await Task.Yield();

        if (!_localLlmManager.IsServerExeAvailable)
        {
            AppendSystemMessage("llama-server.exe が見つかりません。エディタを再インストールしてください。");
            return;
        }
        if (!_localLlmManager.IsModelDownloaded)
        {
            AppendSystemMessage(
                "ローカル AI モデル (Qwen2.5-Coder-7B) が未ダウンロードです。\n" +
                "初回メッセージ送信時に自動でダウンロードします（約 4.4GB）。\n" +
                "ライセンス: Apache 2.0（商用利用可）");
        }
    }

    // ── ユーティリティ ───────────────────────────────────────────

    /// <summary>設定ポップアップ内ラベルのスタイルを統一するファクトリメソッド。</summary>
    private static TextBlock MakeSettingsLabel(string text)
    {
        return new TextBlock
        {
            Text       = text,
            Foreground = Brushes.LightGray,
            FontSize   = 11,
            Margin     = new Thickness(0, 4, 0, 2),
        };
    }
}
