using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using SEEDEditor;
using SEEDEditor.Panels.ScriptEditor;
using SEEDEditor.Scripting;

namespace SEEDEditor.Panels;

/// <summary>開いているドキュメント 1 件の情報（「タブ」パネル表示用）。</summary>
public sealed record OpenDocInfo(string FilePath, bool IsDirty, bool IsActive);

/// <summary>診断（エラー・警告）1 件（「エラー一覧」パネル表示用）。</summary>
public sealed record ScriptDiagnostic(bool IsError, string Id, string Message, int Line, int Column, int Offset, string FilePath);

/// <summary>
/// 内蔵 C# スクリプトエディタパネル。
///
/// 【機能】
/// - タブ式で複数の .cs ファイルを同時に開ける（AvalonEdit / C# ハイライト / 行番号）
/// - Ctrl+S で保存 → Roslyn でコンパイルチェック → エラーを Output に表示
/// - エラー・警告・文法エラーを波線でリアルタイム表示（ホバーで内容表示）
/// - Ctrl+F 検索 / Ctrl+H 置換
/// - Ctrl+K,D でコード整形（Roslyn Formatter）
/// - Ctrl+ホイールで表示倍率変更
/// - 保存成功時に ScriptSaved イベントを発火（MainWindow が RELOAD_SCRIPTS を送信）
/// - 未保存タブには ● マークを表示し、閉じるときに保存確認を出す
/// </summary>
public class ScriptEditorPanel : UserControl
{
    // ── 表示倍率の下限・上限（Ctrl+ホイール）──
    private const double MinFontSize = 8.0;
    private const double MaxFontSize = 40.0;
    // 診断（エラー・警告）の再計算を遅延させるデバウンス時間
    private static readonly TimeSpan DiagnosticsDebounce = TimeSpan.FromMilliseconds(400);
    // ── カラーテーマ（エディタ全体のダークテーマに合わせる）──
    private static readonly SolidColorBrush BrushBg        = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly SolidColorBrush BrushEditorBg  = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly SolidColorBrush BrushText      = new(Color.FromRgb(0xDC, 0xDC, 0xDC));
    private static readonly SolidColorBrush BrushTabBg     = new(Color.FromRgb(0x2D, 0x2D, 0x2D));
    private static readonly SolidColorBrush BrushTabActive = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly SolidColorBrush BrushBorder    = new(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly SolidColorBrush BrushDim       = new(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly SolidColorBrush BrushAccent    = new(Color.FromRgb(0x55, 0xAA, 0xFF));
    private static readonly SolidColorBrush BrushError     = new(Color.FromRgb(0xF4, 0x47, 0x47)); // エラーアイコン=赤
    private static readonly SolidColorBrush BrushWarning   = new(Color.FromRgb(0xD7, 0xBA, 0x36)); // 警告アイコン=黄
    // 予測（補完）ウィンドウの配色: 背景はエディタ(#1E1E1E)より少し明るい黒、文字は白
    private static readonly SolidColorBrush BrushCompletionBg = new(Color.FromRgb(0x2A, 0x2A, 0x2B));
    private static readonly SolidColorBrush BrushCompletionFg = new(Color.FromRgb(0xFF, 0xFF, 0xFF));

    /// <summary>開いているドキュメント 1 件の状態。</summary>
    private sealed class DocTab
    {
        public required string            FilePath;
        public required TextEditor        Editor;
        public required FrameworkElement  Content;        // エディタ + ルーラーのコンテナ
        public required TextMarkerService Markers;
        public required DispatcherTimer   DiagTimer;
        public required HighlightRenderer SelectionHi;   // 選択単語の一致ハイライト
        public required HighlightRenderer SearchHi;       // 検索一致ハイライト（オレンジ）
        public required OverviewRuler     Ruler;          // 右側の概観ルーラー
        public required SemanticColorizer Semantic;      // 型名・メソッド名などの意味的着色
        public List<ScriptDiagnostic> Diagnostics = new();
        public bool IsDirty;
    }

    private readonly List<DocTab> _docs = new();
    private readonly Decorator _editorHost;   // アクティブドキュメントの表示領域
    private readonly TextBlock _emptyHint;
    private readonly FindReplaceBar _findBar;
    private DocTab? _activeDoc;

    /// <summary>開いているドキュメントの一覧が変化したときに発火（「タブ」パネル更新用）。</summary>
    public event Action? DocumentsChanged;
    /// <summary>診断が変化したときに発火（「エラー一覧」パネル更新用）。</summary>
    public event Action? DiagnosticsChanged;
    /// <summary>左下アイコンのクリックで「エラー一覧」パネルの表示を要求する。</summary>
    public event Action? ShowErrorListRequested;
    /// <summary>ドキュメントがアクティブ表示されたときに発火（「タブ」パネルの自動表示用）。</summary>
    public event Action? DocumentActivated;

    /// <summary>左下ステータスバーのエラー/警告カウント表示（常時表示）。</summary>
    private TextBlock _statusCounts = null!;
    // ステータスバー内の色分け Run（アイコン=赤/黄・カウント=白）
    private Run _statusErrIcon   = null!;
    private Run _statusErrCount  = null!;
    private Run _statusWarnIcon  = null!;
    private Run _statusWarnCount = null!;

    /// <summary>スクリプト全体の意味解析ワークスペース（IntelliSense / F12 用）。遅延生成。</summary>
    private ScriptWorkspace? _workspace;
    private string? _assetsRoot;
    /// <summary>現在表示中の補完ウィンドウ（多重表示防止）。</summary>
    private CompletionWindow? _completionWindow;

    /// <summary>書式・配色設定と、その保存先ディレクトリ。</summary>
    private ScriptEditorSettings _settings = new();
    private string? _settingsDir;

    /// <summary>診断ホバー用の共有ツールチップ（ホバーが外れたら閉じる）。</summary>
    private ToolTip? _diagToolTip;

    // ハイライト色
    private static readonly Color OccurrenceColor = Color.FromRgb(0x40, 0x40, 0x40); // 選択単語一致（薄いグレー）
    private static readonly Color SearchColor     = Color.FromRgb(0xE5, 0x8A, 0x2E); // 検索一致（オレンジ）

    /// <summary>ファイルの保存が完了したときに発火する（フルパス）。コンパイル成否に関わらず発火。</summary>
    public event Action<string>? ScriptSaved;

    public ScriptEditorPanel()
    {
        Background = BrushBg;

        // アクティブドキュメントのみを表示する領域（タブは別パネルで管理）
        _editorHost = new Decorator();

        // ファイルが 1 つも開かれていないときの案内表示
        _emptyHint = new TextBlock
        {
            Text                = "プロジェクトパネルで .cs ファイルをダブルクリックすると、ここで編集できます",
            Foreground          = BrushDim,
            FontSize            = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        // 検索・置換バー（上部に配置、初期は非表示）
        _findBar = new FindReplaceBar();

        var body = new Grid();
        body.Children.Add(_editorHost);
        body.Children.Add(_emptyHint);

        var toolbar   = BuildToolbar();
        var statusBar = BuildStatusBar();

        var root = new DockPanel();
        DockPanel.SetDock(toolbar,   Dock.Top);
        DockPanel.SetDock(_findBar,  Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(_findBar);
        root.Children.Add(statusBar);   // 左下のエラー/警告アイコン（常時表示）
        root.Children.Add(body);        // 残り全体（最後 = フィル）
        Content = root;

        // 検索語が変わったら現在のエディタの検索ハイライトを更新する
        _findBar.SearchChanged += () =>
        {
            if (_activeDoc is not null) UpdateSearchHighlight(_activeDoc);
        };

        // キーボードショートカット
        PreviewKeyDown += OnPanelKeyDown;

        // パネルがキーボードフォーカスを持たない（=アクティブでない）ときは
        // 全エディタを読み取り専用にし、テキスト入力を受け付けないようにする。
        // ビューポート（HWND）操作中のキー入力がエディタに紛れ込むのを防ぐ。
        IsKeyboardFocusWithinChanged += (_, _) => UpdateEditability();

        UpdateEmptyHint();
    }

    /// <summary>
    /// パネルのアクティブ状態（キーボードフォーカスの有無）に応じて
    /// 全エディタの読み取り専用フラグを更新する。
    /// フォーカスがない間は編集不可 → キー入力を無視する。
    /// </summary>
    private void UpdateEditability()
    {
        var editable = IsKeyboardFocusWithin;
        foreach (var doc in _docs)
            doc.Editor.IsReadOnly = !editable;
    }

    /// <summary>現在アクティブなエディタ（なければ null）。</summary>
    private TextEditor? CurrentEditor() => _activeDoc?.Editor;

    /// <summary>指定ドキュメントをアクティブにして表示領域へ載せる。</summary>
    private void ActivateDoc(DocTab? doc)
    {
        _activeDoc = doc;
        _editorHost.Child = doc?.Content;
        _findBar.SetTarget(doc?.Editor);
        if (doc is not null)
        {
            ApplySettingsToEditor(doc.Editor);
            UpdateSearchHighlight(doc);
            UpdateOccurrenceHighlight(doc);
            doc.Editor.Focus();
        }
        UpdateEmptyHint();
        DocumentsChanged?.Invoke();
        // ドキュメントを表示したら「タブ」パネルの自動表示を要求する
        if (doc is not null) DocumentActivated?.Invoke();
    }

    // ── 「タブ」パネル / 「エラー一覧」パネル向け 公開 API ────

    /// <summary>開いているドキュメントの一覧を返す（「タブ」パネル用）。</summary>
    public IReadOnlyList<OpenDocInfo> GetOpenDocuments()
        => _docs.Select(d => new OpenDocInfo(d.FilePath, d.IsDirty, d == _activeDoc)).ToList();

    /// <summary>指定パスのドキュメントをアクティブにする（「タブ」パネルからの選択）。</summary>
    public void ActivateFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var doc = _docs.FirstOrDefault(d => string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (doc is not null) ActivateDoc(doc);
    }

    /// <summary>指定パスのドキュメントを閉じる（「タブ」パネルの ✕ から）。</summary>
    public void CloseFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var doc = _docs.FirstOrDefault(d => string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (doc is not null) CloseTab(doc);
    }

    /// <summary>全ドキュメントの診断を返す（「エラー一覧」パネル用）。</summary>
    public IReadOnlyList<ScriptDiagnostic> GetDiagnostics()
        => _docs.SelectMany(d => d.Diagnostics).ToList();

    /// <summary>指定診断の該当箇所へジャンプする（「エラー一覧」パネルのダブルクリック）。</summary>
    public void GoToDiagnostic(ScriptDiagnostic d)
    {
        OpenFile(d.FilePath);
        var target = _docs.FirstOrDefault(x =>
            string.Equals(x.FilePath, Path.GetFullPath(d.FilePath), StringComparison.OrdinalIgnoreCase));
        if (target is null) return;
        int off = Math.Min(d.Offset, target.Editor.Document.TextLength);
        target.Editor.CaretOffset = off;
        target.Editor.ScrollToLine(target.Editor.Document.GetLineByOffset(off).LineNumber);
        target.Editor.Focus();
    }

    /// <summary>左下ステータスバー（エラー/警告アイコン。クリックでエラー一覧を開く）を生成する。</summary>
    private UIElement BuildStatusBar()
    {
        var bar = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
            BorderBrush     = BrushBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding         = new Thickness(8, 2, 8, 2),
            Cursor          = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip         = "クリックでエラー一覧を開く",
        };
        // アイコン(色付き) と カウント(白) を Run で分けて色を独立させる。
        //   エラーアイコン(⊘)=赤 / 警告アイコン(△)=黄 / 各カウント=白
        _statusErrIcon   = new Run("⊘ ")  { Foreground = BrushError };
        _statusErrCount  = new Run("0")   { Foreground = BrushText };
        _statusWarnIcon  = new Run("   △ ") { Foreground = BrushWarning };
        _statusWarnCount = new Run("0")   { Foreground = BrushText };
        _statusCounts = new TextBlock
        {
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _statusCounts.Inlines.Add(_statusErrIcon);
        _statusCounts.Inlines.Add(_statusErrCount);
        _statusCounts.Inlines.Add(_statusWarnIcon);
        _statusCounts.Inlines.Add(_statusWarnCount);
        bar.Child = _statusCounts;
        bar.MouseLeftButtonUp += (_, _) => ShowErrorListRequested?.Invoke();
        return bar;
    }

    /// <summary>左下ステータスバーのエラー/警告カウントを更新する。</summary>
    private void RefreshStatusCounts()
    {
        int errors   = _docs.Sum(d => d.Diagnostics.Count(x => x.IsError));
        int warnings = _docs.Sum(d => d.Diagnostics.Count(x => !x.IsError));
        // アイコン色は固定（赤/黄）、カウントは常に白のまま件数のみ更新する
        _statusErrCount.Text  = errors.ToString();
        _statusWarnCount.Text = warnings.ToString();
    }

    /// <summary>診断変化を各所へ通知する（左下カウント + エラー一覧パネル）。</summary>
    private void NotifyDiagnosticsChanged()
    {
        RefreshStatusCounts();
        DiagnosticsChanged?.Invoke();
    }

    /// <summary>上部ツールバー（設定・整形ボタン）を生成する。</summary>
    private UIElement BuildToolbar()
    {
        var bar = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2E)),
            BorderBrush     = BrushBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(4, 2, 4, 2),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        Button ToolBtn(string content, string tooltip, Action onClick)
        {
            var b = new Button
            {
                Content = content, ToolTip = tooltip,
                Foreground = BrushText, Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                BorderBrush = BrushBorder, Margin = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(6, 1, 6, 1), FontSize = 11,
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        sp.Children.Add(ToolBtn("⚙ 設定",   "書式・配色設定 (Ctrl+,)",        OpenSettings));
        sp.Children.Add(ToolBtn("整形",      "コード整形 (Ctrl+K,D)",          FormatCurrent));
        sp.Children.Add(ToolBtn("検索",      "検索 (Ctrl+F)",                  () => { _findBar.SetTarget(CurrentEditor()); _findBar.ShowFind(); }));
        bar.Child = sp;
        return bar;
    }

    // ── 設定（書式・配色）─────────────────────────────────────

    /// <summary>設定ディレクトリを指定して設定を読み込み、全エディタへ適用する。</summary>
    public void InitSettings(string settingsDir)
    {
        _settingsDir = settingsDir;
        _settings    = ScriptEditorSettings.Load(settingsDir);
        ApplyColorsToHighlighting(_settings);
        foreach (var doc in _docs) ApplySettingsToEditor(doc.Editor);
    }

    /// <summary>設定ダイアログを開く。</summary>
    private void OpenSettings()
    {
        var dlg = new ScriptEditorSettingsWindow(_settings) { Owner = Window.GetWindow(this) };
        dlg.Applied += s =>
        {
            _settings = s;
            if (_settingsDir is not null) _settings.Save(_settingsDir);
            ApplyColorsToHighlighting(_settings);
            // セマンティック着色のブラシキャッシュを破棄し、新しい設定色で再計算する
            _semanticBrushCache.Clear();
            foreach (var doc in _docs)
            {
                ApplySettingsToEditor(doc.Editor);
                _ = RunSemanticColorizeAsync(doc);
                doc.Editor.TextArea.TextView.Redraw();
            }
        };
        dlg.ShowDialog();
    }

    /// <summary>1 つのエディタに書式設定（インデント・フォント）を適用する。</summary>
    private void ApplySettingsToEditor(TextEditor editor)
    {
        editor.Options.IndentationSize    = _settings.IndentationSize;
        editor.Options.ConvertTabsToSpaces = _settings.ConvertTabsToSpaces;
        editor.FontSize                   = _settings.FontSize;
    }

    /// <summary>キーボードショートカット処理。</summary>
    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!ctrl) return;

        switch (e.Key)
        {
            case Key.S:                       // Ctrl+S=編集中を保存 / Ctrl+Shift+S=全て保存
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SaveAll();
                else                                                 SaveCurrent();
                e.Handled = true;
                break;
            case Key.F:                       // 検索
                _findBar.SetTarget(CurrentEditor());
                _findBar.ShowFind();
                e.Handled = true;
                break;
            case Key.H:                       // 置換
                _findBar.SetTarget(CurrentEditor());
                _findBar.ShowReplace();
                e.Handled = true;
                break;
            case Key.K:                       // Ctrl+K,D の 1 打鍵目（次の D を待つ）
                _awaitingFormatChord = true;
                e.Handled = true;
                break;
            case Key.D:                       // Ctrl+K の直後の Ctrl+D で整形
                if (_awaitingFormatChord)
                {
                    FormatCurrent();
                    _awaitingFormatChord = false;
                    e.Handled = true;
                }
                break;
            case Key.OemComma:                // Ctrl+, で設定を開く
                OpenSettings();
                e.Handled = true;
                break;
            default:
                _awaitingFormatChord = false;
                break;
        }
    }

    /// <summary>Ctrl+K の直後で Ctrl+D を待っている状態か（コード整形のコード）。</summary>
    private bool _awaitingFormatChord;

    // ── 公開 API ─────────────────────────────────────────────

    /// <summary>
    /// スクリプトのアセットルートを設定する。
    /// IntelliSense / F12 用の Roslyn ワークスペースをこのルート配下の
    /// 全 .cs から構築する。
    /// </summary>
    public void SetAssetsPath(string assetsRoot)
    {
        _assetsRoot = assetsRoot;
        try
        {
            _workspace = new ScriptWorkspace(assetsRoot);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"スクリプト解析の初期化に失敗しました（補完・F12 が無効）: {ex.Message}");
            _workspace = null;
        }
    }

    /// <summary>ファイルを開く（既に開いていればそのタブをアクティブにする）。</summary>
    public void OpenFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);

        var existing = _docs.FirstOrDefault(d =>
            string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateDoc(existing);
            return;
        }

        string text;
        try { text = File.ReadAllText(full); }
        catch (Exception ex)
        {
            EditorLog.Write($"スクリプトを開けませんでした: {ex.Message}");
            return;
        }

        var editor = CreateEditor(text);

        // 診断（波線）用マーカーサービスを TextView に登録する
        var markers = new TextMarkerService(editor.Document);
        editor.TextArea.TextView.BackgroundRenderers.Add(markers);

        // 一致ハイライト（選択単語・検索）のレイヤーを登録する。
        // 検索（オレンジ）を上に描くため後に追加する。
        var selectionHi = new HighlightRenderer(OccurrenceColor, 0xFF, Color.FromRgb(0x56, 0x56, 0x56));
        var searchHi    = new HighlightRenderer(SearchColor, 0xAA);
        editor.TextArea.TextView.BackgroundRenderers.Add(selectionHi);
        editor.TextArea.TextView.BackgroundRenderers.Add(searchHi);

        // セマンティック着色（型名・メソッド名・フィールド名など）。
        // 正規表現ハイライトの後に適用したいので LineTransformers の末尾に追加する。
        var semantic = new SemanticColorizer();
        editor.TextArea.TextView.LineTransformers.Add(semantic);

        // 右側の概観ルーラー（マーククリックでジャンプ）
        var ruler = new OverviewRuler(editor);

        // エディタ本体 + ルーラーを横並びにしてコンテンツにする
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(editor, 0);
        Grid.SetColumn(ruler, 1);
        content.Children.Add(editor);
        content.Children.Add(ruler);

        // 診断の再計算を遅延実行するデバウンスタイマー
        var diagTimer = new DispatcherTimer { Interval = DiagnosticsDebounce };

        var doc = new DocTab
        {
            FilePath    = full,
            Editor      = editor,
            Content     = content,
            Markers     = markers,
            DiagTimer   = diagTimer,
            SelectionHi = selectionHi,
            SearchHi    = searchHi,
            Ruler       = ruler,
            Semantic    = semantic,
        };

        // 編集のたびにダーティ化し、ワークスペースへ反映し、診断を予約する
        editor.TextChanged += (_, _) =>
        {
            SetDirty(doc, true);
            _workspace?.UpsertText(doc.FilePath, editor.Text);
            diagTimer.Stop();
            diagTimer.Start();
        };

        // IntelliSense: 文字入力に応じて補完ウィンドウを開く
        editor.TextArea.TextEntered += (_, e) => OnTextEntered(doc, e.Text);
        // F12（定義へ移動）・Ctrl+Space（補完を手動起動）
        editor.PreviewKeyDown += (_, e) => OnEditorKeyDown(doc, e);
        diagTimer.Tick += (_, _) =>
        {
            diagTimer.Stop();
            _ = RunDiagnosticsAsync(doc);
            _ = RunSemanticColorizeAsync(doc);
        };

        // マーカーにホバーしたら診断メッセージをツールチップ表示し、外れたら閉じる
        editor.MouseHover        += (_, e) => ShowDiagnosticToolTip(doc, e);
        editor.MouseHoverStopped += (_, _) => HideDiagnosticToolTip();

        // 選択単語の一致ハイライト（キャレット移動・選択変更で更新）
        editor.TextArea.Caret.PositionChanged += (_, _) => UpdateOccurrenceHighlight(doc);
        editor.TextArea.SelectionChanged      += (_, _) => UpdateOccurrenceHighlight(doc);

        // Ctrl+ホイールで表示倍率を変更する
        editor.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            editor.FontSize = Math.Clamp(editor.FontSize + (e.Delta > 0 ? 1 : -1), MinFontSize, MaxFontSize);
            e.Handled = true;
        };

        // 書式・配色設定を適用する
        ApplySettingsToEditor(editor);

        // ワークスペースに最新テキストを反映する（開いた瞬間の内容で同期）
        _workspace?.UpsertText(full, text);

        _docs.Add(doc);
        // 初期状態を現在のフォーカス状態に合わせる（フォーカスが移るまで読み取り専用）
        UpdateEditability();
        ActivateDoc(doc);
        // 初回の診断・セマンティック着色を実行する
        _ = RunDiagnosticsAsync(doc);
        _ = RunSemanticColorizeAsync(doc);
    }

    /// <summary>現在アクティブなドキュメントを保存する。</summary>
    public void SaveCurrent()
    {
        if (_activeDoc is not null) Save(_activeDoc);
    }

    /// <summary>開いている全ドキュメント（タブウィンドウの全スクリプト）を保存する。</summary>
    public void SaveAll()
    {
        // Save は SetDirty で _docs を変更しないため列挙中に走らせても安全だが、
        // 念のためスナップショットしてから保存する。
        foreach (var doc in _docs.ToList()) Save(doc);
    }

    /// <summary>
    /// スクリプトエディタが「アクティブ」（保存対象）かどうか。
    /// エディタ内にキーボードフォーカスがある場合をアクティブとみなす。
    /// </summary>
    public bool IsActiveForSave => IsKeyboardFocusWithin;

    /// <summary>未保存の変更があるタブが存在するか。</summary>
    public bool HasUnsavedChanges => _docs.Any(d => d.IsDirty);

    // ── 診断（エラー・警告・文法エラー）─────────────────────

    // Roslyn の参照アセンブリはコンパイルごとに再取得すると重いためキャッシュする
    private static readonly Lazy<List<MetadataReference>> _diagRefs = new(() =>
    {
        // SEEDScripting.dll（SEEDScript 基底クラス・SerializeField 属性を含む）の
        // ロードを強制する。参照アセンブリは遅延ロードのため、これを行わないと
        // 診断コンパイル時に SEEDScript / SerializeField が「型が見つからない」と
        // 誤判定されてしまう。
        _ = typeof(global::SEEDEditor.Scripting.SEEDScript).Assembly;

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
    });

    /// <summary>
    /// エディタのテキストを Roslyn で解析し、エラー・警告の波線を更新する。
    /// 解析は別スレッドで行い、UI 更新のみディスパッチャに戻す。
    /// </summary>
    private async Task RunDiagnosticsAsync(DocTab doc)
    {
        var source = doc.Editor.Text;

        var diags = await Task.Run(() =>
        {
            try
            {
                var tree = CSharpSyntaxTree.ParseText(source);
                var comp = CSharpCompilation.Create(
                    "SEEDScriptDiag",
                    new[] { tree },
                    _diagRefs.Value,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                return comp.GetDiagnostics()
                    .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                    .Select(d =>
                    {
                        var lineSpan = d.Location.GetLineSpan();
                        return (
                            span:    d.Location.SourceSpan,
                            id:      d.Id,
                            body:    d.GetMessage(),
                            isError: d.Severity == DiagnosticSeverity.Error,
                            line:    lineSpan.StartLinePosition.Line + 1,
                            col:     lineSpan.StartLinePosition.Character + 1);
                    })
                    .ToList();
            }
            catch
            {
                return null;
            }
        });

        if (diags is null) return;
        // テキストが解析後に変わっていたら破棄（次の Tick で再計算される）
        if (doc.Editor.Text != source) return;

        doc.Markers.RemoveAll();
        doc.Diagnostics.Clear();
        foreach (var (span, id, body, isError, line, col) in diags)
        {
            var color = isError
                ? Color.FromRgb(0xF4, 0x47, 0x47)   // 赤
                : Color.FromRgb(0xD7, 0xBA, 0x36);   // 黄
            var label = $"{(isError ? "エラー" : "警告")} {id}: {body}";
            doc.Markers.Create(span.Start, Math.Max(span.Length, 1), color, label);
            doc.Diagnostics.Add(new ScriptDiagnostic(isError, id, body, line, col, span.Start, doc.FilePath));
        }
        doc.Editor.TextArea.TextView.Redraw();

        // 概観ルーラーのエラー/警告マークと、左下カウント・エラー一覧パネルを更新する
        RefreshRuler(doc);
        NotifyDiagnosticsChanged();
    }

    // ── セマンティック着色（型名・メソッド名・フィールド名など）─────

    // Roslyn 分類種別 → 既定色（VS ダークテーマ準拠）。
    // 正規表現ハイライトでは識別できない「識別子系」トークンのみを対象にする
    // （キーワード・文字列・コメントは標準ハイライトに委ねる）。
    private static readonly Dictionary<string, Color> _semanticDefaults = new()
    {
        ["class name"]            = Color.FromRgb(0x4E, 0xC9, 0xB0), // 型名=ティール
        ["record class name"]     = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["delegate name"]         = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["struct name"]           = Color.FromRgb(0x86, 0xC6, 0x91), // 構造体=緑系
        ["record struct name"]    = Color.FromRgb(0x86, 0xC6, 0x91),
        ["enum name"]             = Color.FromRgb(0xB8, 0xD7, 0xA3),
        ["interface name"]        = Color.FromRgb(0xB8, 0xD7, 0xA3),
        ["type parameter name"]   = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["method name"]           = Color.FromRgb(0xDC, 0xDC, 0xAA), // メソッド=薄黄
        ["extension method name"] = Color.FromRgb(0xDC, 0xDC, 0xAA),
        ["field name"]            = Color.FromRgb(0x9C, 0xDC, 0xFE), // フィールド=水色
        ["constant name"]         = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["enum member name"]      = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["local name"]            = Color.FromRgb(0x9C, 0xDC, 0xFE),
        ["parameter name"]        = Color.FromRgb(0x9C, 0xDC, 0xFE),
    };

    // 分類種別 → ブラシのキャッシュ（設定色を反映済み）。設定変更時にクリアされる。
    private readonly Dictionary<string, Brush> _semanticBrushCache = new();

    /// <summary>分類種別に対応するブラシを返す（対象外なら null）。設定色があれば優先する。</summary>
    private Brush? SemanticBrush(string classification)
    {
        if (_semanticBrushCache.TryGetValue(classification, out var cached)) return cached;
        if (!_semanticDefaults.TryGetValue(classification, out var def)) return null;

        // 設定に同名キーがあれば上書き（データドリブンに配色を差し替え可能）
        var color = def;
        if (_settings is not null && _settings.Colors.TryGetValue(classification, out var hex))
        {
            try { color = (Color)ColorConverter.ConvertFromString(hex); } catch { /* 無効値は既定 */ }
        }
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _semanticBrushCache[classification] = brush;
        return brush;
    }

    /// <summary>
    /// Roslyn のセマンティック分類を計算し、型名・メソッド名などを着色する。
    /// ワークスペース（意味解析）が無いファイルでは何もしない。
    /// </summary>
    private async Task RunSemanticColorizeAsync(DocTab doc)
    {
        if (_workspace is null) return;
        var document = _workspace.GetDocument(doc.FilePath);
        if (document is null) return;

        var source = doc.Editor.Text;
        List<SemanticColorizer.Span>? spans = null;
        try
        {
            var text = await document.GetTextAsync();
            var classified = await Classifier.GetClassifiedSpansAsync(
                document, new TextSpan(0, text.Length));

            spans = new List<SemanticColorizer.Span>();
            foreach (var cs in classified)
            {
                var brush = SemanticBrush(cs.ClassificationType);
                if (brush is null) continue;
                spans.Add(new SemanticColorizer.Span(cs.TextSpan.Start, cs.TextSpan.Length, brush));
            }
        }
        catch
        {
            return; // 解析途中の不整合などは無視（次回更新で再計算）
        }

        // テキストが解析後に変わっていたら破棄する
        if (doc.Editor.Text != source) return;

        doc.Semantic.SetSpans(spans);
        doc.Editor.TextArea.TextView.Redraw();
    }

    // ── 診断ツールチップ（ホバー表示、外れたら閉じる）─────────

    /// <summary>マウス下のマーカーがあれば診断メッセージをツールチップ表示する。</summary>
    private void ShowDiagnosticToolTip(DocTab doc, MouseEventArgs e)
    {
        var pos = doc.Editor.GetPositionFromPoint(e.GetPosition(doc.Editor));
        if (pos is null) { HideDiagnosticToolTip(); return; }

        int offset = doc.Editor.Document.GetOffset(pos.Value.Location);
        var marker = doc.Markers.GetMarkersAtOffset(offset).FirstOrDefault();
        if (marker?.ToolTip is null) { HideDiagnosticToolTip(); return; }

        // 共有ツールチップを使い回す（毎回 new すると閉じられなくなるため）
        _diagToolTip ??= new ToolTip
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
            Foreground = BrushText,
            BorderBrush= BrushBorder,
        };
        _diagToolTip.PlacementTarget = doc.Editor;
        _diagToolTip.Content = new TextBlock { Text = marker.ToolTip, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        _diagToolTip.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>診断ツールチップを閉じる（ホバーが外れたとき）。</summary>
    private void HideDiagnosticToolTip()
    {
        if (_diagToolTip is not null) _diagToolTip.IsOpen = false;
    }

    // ── 一致ハイライト（選択単語・検索）と概観ルーラー ────────

    /// <summary>キャレット位置または選択中の単語に一致する全箇所をハイライトする。</summary>
    private void UpdateOccurrenceHighlight(DocTab doc)
    {
        var editor = doc.Editor;
        string word = GetHighlightWord(editor);
        var matches = string.IsNullOrEmpty(word)
            ? new List<(int, int)>()
            : FindAllMatches(editor.Text, word, matchCase: true, wholeWord: true);

        doc.SelectionHi.SetSegments(matches);
        editor.TextArea.TextView.Redraw();
        RefreshRuler(doc);
    }

    /// <summary>検索バーの語に一致する全箇所をオレンジでハイライトする。</summary>
    private void UpdateSearchHighlight(DocTab doc)
    {
        var editor = doc.Editor;
        var matches = _findBar.IsSearching
            ? FindAllMatches(editor.Text, _findBar.SearchTerm, _findBar.MatchCase, wholeWord: false)
            : new List<(int, int)>();

        doc.SearchHi.SetSegments(matches);
        editor.TextArea.TextView.Redraw();
        RefreshRuler(doc);
    }

    // 概観ルーラーのマーク色
    private static readonly Color RulerErrorColor   = Color.FromRgb(0xF4, 0x47, 0x47); // エラー行（赤）
    private static readonly Color RulerWarningColor = Color.FromRgb(0xD7, 0xBA, 0x36); // 警告行（黄）
    private static readonly Color RulerOccurColor   = Color.FromRgb(0x88, 0x88, 0x88); // 選択単語一致（グレー）

    /// <summary>
    /// 概観ルーラーのマークを更新する。
    /// 常にエラー行を赤・警告行を黄で表示し、加えて検索中はオレンジ、
    /// それ以外は選択単語一致をグレーで重ねる。
    /// </summary>
    private void RefreshRuler(DocTab doc)
    {
        var editor = doc.Editor;
        var marks = new List<OverviewRuler.Mark>();

        // 診断（エラー→赤 / 警告→黄）を常に表示する
        foreach (var diag in doc.Diagnostics)
            marks.Add(new OverviewRuler.Mark(diag.Line, diag.IsError ? RulerErrorColor : RulerWarningColor));

        // 検索中はオレンジ、それ以外は選択単語一致をグレーで重ねる
        IEnumerable<(int Start, int Length)> active = _findBar.IsSearching ? doc.SearchHi.Segments : doc.SelectionHi.Segments;
        var overlayColor = _findBar.IsSearching ? SearchColor : RulerOccurColor;
        foreach (var (start, _) in active)
        {
            if (start < 0 || start > editor.Document.TextLength) continue;
            int line = editor.Document.GetLineByOffset(start).LineNumber;
            marks.Add(new OverviewRuler.Mark(line, overlayColor));
        }

        doc.Ruler.SetMarks(marks);
    }

    /// <summary>ハイライト対象の単語を決定する（選択が単一識別子ならそれ、無ければキャレット下の識別子）。</summary>
    private static string GetHighlightWord(TextEditor editor)
    {
        // 選択があれば、それが 1 単語（識別子）ならハイライト対象にする
        if (editor.SelectionLength > 0)
        {
            var sel = editor.SelectedText;
            return IsIdentifier(sel) ? sel : "";
        }
        // 選択が無ければキャレット下の識別子を対象にする
        var text = editor.Text;
        int caret = editor.CaretOffset;
        int start = caret, end = caret;
        while (start > 0 && IsIdentChar(text[start - 1])) start--;
        while (end < text.Length && IsIdentChar(text[end])) end++;
        return end > start ? text.Substring(start, end - start) : "";
    }

    private static bool IsIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s) if (!IsIdentChar(ch)) return false;
        return true;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>文字列中の全一致範囲を返す。wholeWord=true なら単語境界の一致のみ。</summary>
    private static List<(int Start, int Length)> FindAllMatches(string text, string keyword, bool matchCase, bool wholeWord)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrEmpty(keyword)) return result;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int idx = 0;
        while (true)
        {
            int next = text.IndexOf(keyword, idx, cmp);
            if (next < 0) break;
            idx = next + keyword.Length;
            if (wholeWord)
            {
                bool leftOk  = next == 0 || !IsIdentChar(text[next - 1]);
                bool rightOk = next + keyword.Length >= text.Length || !IsIdentChar(text[next + keyword.Length]);
                if (!leftOk || !rightOk) continue;
            }
            result.Add((next, keyword.Length));
        }
        return result;
    }

    // ── IntelliSense 補完 / F12 定義ジャンプ ─────────────────

    /// <summary>入力文字に応じて補完ウィンドウを起動する。</summary>
    private async void OnTextEntered(DocTab doc, string enteredText)
    {
        if (_workspace is null || enteredText.Length == 0) return;

        char c = enteredText[0];

        // "." （メンバーアクセス）は文脈が変わるので毎回計算し直す
        if (c == '.')
        {
            await ShowCompletionAsync(doc);
            return;
        }

        // 識別子の入力: ウィンドウが未表示のときだけ起動する。
        // 既に表示中なら AvalonEdit 側が入力済みプレフィックスで絞り込むため
        // 再計算しない（＝毎打鍵で全件が出る問題を防ぐ）。
        if (char.IsLetter(c) || c == '_')
        {
            if (_completionWindow is not null) return;
            await ShowCompletionAsync(doc);
        }
        // それ以外の記号では補完を出さない
    }

    /// <summary>エディタ上のキー処理（F12 / Ctrl+Space）。</summary>
    private async void OnEditorKeyDown(DocTab doc, KeyEventArgs e)
    {
        // Ctrl+Space: 補完を手動起動
        if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await ShowCompletionAsync(doc);
            return;
        }
        // F12: 定義へ移動
        if (e.Key == Key.F12)
        {
            e.Handled = true;
            await GoToDefinitionAsync(doc);
        }
    }

    /// <summary>現在のキャレット位置で補完候補を計算し、ウィンドウ表示する。</summary>
    private async Task ShowCompletionAsync(DocTab doc)
    {
        if (_workspace is null) return;
        // 最新テキストをワークスペースへ反映してから解析する
        _workspace.UpsertText(doc.FilePath, doc.Editor.Text);

        var document = _workspace.GetDocument(doc.FilePath);
        if (document is null) return;

        var editor   = doc.Editor;
        int position = editor.CaretOffset;

        // 入力済みの識別子（キャレット直前の英数字列）を置換対象・フィルタ語にする
        var text = editor.Text;
        int wordStart = position;
        while (wordStart > 0 && IsIdentChar(text[wordStart - 1])) wordStart--;
        string prefix = text.Substring(wordStart, position - wordStart);

        // 自作補完: スコープ内の実シンボル（変数・型・メソッド等）を取得する
        var entries = await CustomCompletion.GetEntriesAsync(document, position, prefix.Length);

        // キャレットが解析中に動いていたら破棄する
        if (editor.CaretOffset != position) return;

        // 予測できる候補が無ければ表示しない
        if (entries.Count == 0)
        {
            _completionWindow?.Close();
            return;
        }

        // 既存のウィンドウを閉じてから新規表示する
        _completionWindow?.Close();

        var window = new CompletionWindow(editor.TextArea)
        {
            CloseAutomatically        = true,
            CloseWhenCaretAtBeginning = true,
            Width                     = 340,
            // 置換範囲を「入力済み識別子の先頭〜キャレット」にする（フィルタの起点）
            StartOffset               = wordStart,
            EndOffset                 = position,
        };
        // 入力済み文字による絞り込みを有効化する（追加入力・削除に追従）
        window.CompletionList.IsFiltering = true;

        foreach (var entry in entries)
            window.CompletionList.CompletionData.Add(new SymbolCompletionData(entry));

        // 入力済みプレフィックスで初期フィルタ・最良候補を選択する
        if (prefix.Length > 0)
            window.CompletionList.SelectItem(prefix);

        // 配色（背景=少し明るい黒 / 文字=白）を適用する
        ApplyCompletionColors(window);

        window.Closed += (_, _) => _completionWindow = null;
        window.Show();
        _completionWindow = window;
    }

    /// <summary>予測ウィンドウの配色（背景=少し明るい黒・文字=白）を適用する。</summary>
    private static void ApplyCompletionColors(CompletionWindow window)
    {
        window.Background            = BrushCompletionBg;
        window.Foreground            = BrushCompletionFg;
        window.BorderBrush           = BrushBorder;
        window.CompletionList.Background = BrushCompletionBg;
        window.CompletionList.Foreground = BrushCompletionFg;

        // 内部 ListBox はテンプレート適用後（表示後）に確実に取得できるため、
        // Loaded 後にも色を適用しておく。
        window.CompletionList.Loaded += (_, _) =>
        {
            var lb = window.CompletionList.ListBox;
            if (lb is null) return;
            lb.Background      = BrushCompletionBg;
            lb.Foreground      = BrushCompletionFg;
            lb.BorderThickness = new Thickness(0);
        };
    }

    /// <summary>F12: キャレット位置のシンボル定義へジャンプする（ファイルまたぎ対応）。</summary>
    private async Task GoToDefinitionAsync(DocTab doc)
    {
        if (_workspace is null) return;
        _workspace.UpsertText(doc.FilePath, doc.Editor.Text);

        var document = _workspace.GetDocument(doc.FilePath);
        if (document is null) return;

        var result = await RoslynCompletion.ResolveDefinitionAsync(document, doc.Editor.CaretOffset);
        if (result is null)
        {
            EditorLog.Write("定義が見つかりませんでした（外部ライブラリの型はジャンプ対象外です）");
            return;
        }

        var (filePath, offset) = result.Value;

        // 別ファイルなら開いてから、同一ファイルならそのまま、該当位置へキャレットを移動する
        OpenFile(filePath);
        var target = _docs.FirstOrDefault(d =>
            string.Equals(d.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        int clamped = Math.Min(offset, target.Editor.Document.TextLength);
        target.Editor.CaretOffset = clamped;
        var line = target.Editor.Document.GetLineByOffset(clamped).LineNumber;
        target.Editor.ScrollToLine(line);
        target.Editor.Focus();
    }

    // ── コード整形（Ctrl+K,D）────────────────────────────────

    /// <summary>現在のエディタの内容を Roslyn Formatter で整形する。</summary>
    private void FormatCurrent()
    {
        var editor = CurrentEditor();
        if (editor is null || editor.IsReadOnly) return;
        try
        {
            var tree = CSharpSyntaxTree.ParseText(editor.Text);
            var root = tree.GetRoot();
            using var ws = new Microsoft.CodeAnalysis.AdhocWorkspace();
            var options = ws.Options
                .WithChangedOption(FormattingOptions.UseTabs,       LanguageNames.CSharp, false)
                .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, editor.Options.IndentationSize);
            var formatted = Formatter.Format(root, ws, options).ToFullString();

            if (formatted != editor.Text)
            {
                int caret = editor.CaretOffset;
                editor.Document.Text = formatted;
                editor.CaretOffset = Math.Min(caret, editor.Document.TextLength);
            }
        }
        catch (Exception ex)
        {
            EditorLog.Write($"コード整形に失敗しました: {ex.Message}");
        }
    }

    // ── 保存・コンパイルチェック ─────────────────────────────

    private void Save(DocTab doc)
    {
        try
        {
            File.WriteAllText(doc.FilePath, doc.Editor.Text);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"スクリプト保存失敗 [{Path.GetFileName(doc.FilePath)}]: {ex.Message}");
            return;
        }
        SetDirty(doc, false);

        // Roslyn でコンパイルチェックし、結果を Output パネルに表示する
        var (type, errors) = ScriptCompiler.CompileFile(doc.FilePath);
        if (type is null)
        {
            EditorLog.Write($"スクリプトコンパイルエラー [{Path.GetFileName(doc.FilePath)}]:");
            foreach (var err in errors) EditorLog.Write($"  {err}");
        }
        else
        {
            EditorLog.Write($"スクリプト保存・コンパイル成功 [{Path.GetFileName(doc.FilePath)}] → {type.Name}");
        }

        // 保存自体は成功しているためイベントは常に発火する
        // （コンパイルエラー時も runtime 側は旧アセンブリを維持して動き続ける）
        ScriptSaved?.Invoke(doc.FilePath);
    }

    private void CloseTab(DocTab doc)
    {
        if (doc.IsDirty)
        {
            var result = MessageBox.Show(
                $"{Path.GetFileName(doc.FilePath)} には未保存の変更があります。保存しますか？",
                "スクリプトエディタ",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes)    Save(doc);
        }
        doc.DiagTimer.Stop();
        int idx = _docs.IndexOf(doc);
        _docs.Remove(doc);
        // アクティブを閉じたら隣のドキュメントへ切り替える
        if (_activeDoc == doc)
            ActivateDoc(_docs.Count == 0 ? null : _docs[Math.Clamp(idx, 0, _docs.Count - 1)]);
        UpdateEmptyHint();
        DocumentsChanged?.Invoke();
        NotifyDiagnosticsChanged();
    }

    private void SetDirty(DocTab doc, bool dirty)
    {
        if (doc.IsDirty == dirty) return;
        doc.IsDirty = dirty;
        DocumentsChanged?.Invoke();   // 「タブ」パネルの未保存マークを更新
    }

    private void UpdateEmptyHint()
        => _emptyHint.Visibility = _docs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── UI 生成 ──────────────────────────────────────────────

    /// <summary>ダークテーマ調整済みの AvalonEdit エディタを生成する。</summary>
    private static TextEditor CreateEditor(string text)
    {
        var editor = new TextEditor
        {
            Text                     = text,
            FontFamily               = new FontFamily("Cascadia Mono, Consolas"),
            FontSize                 = 13,
            Background               = BrushEditorBg,
            Foreground               = BrushText,
            ShowLineNumbers          = true,
            LineNumbersForeground    = BrushDim,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            Options = { ConvertTabsToSpaces = true, IndentationSize = 4 },
        };
        editor.SyntaxHighlighting = BuildDarkCSharpHighlighting();
        editor.TextArea.Caret.CaretBrush = BrushText;
        editor.TextArea.SelectionBrush   = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x99, 0xFF));
        return editor;
    }

    /// <summary>
    /// AvalonEdit 標準の C# 定義（ライトテーマ向け配色）をダークテーマ向けに調整する。
    /// 定義はグローバル共有のため、初回のみ色を書き換える。
    /// </summary>
    private static IHighlightingDefinition? _darkCSharp;
    private static IHighlightingDefinition? BuildDarkCSharpHighlighting()
    {
        if (_darkCSharp is not null) return _darkCSharp;
        var def = HighlightingManager.Instance.GetDefinition("C#");
        if (def is null) return null;

        // VS ダークテーマ準拠の配色マッピング
        var colorMap = new Dictionary<string, Color>
        {
            ["Comment"]              = Color.FromRgb(0x6A, 0x99, 0x55),
            ["String"]               = Color.FromRgb(0xD6, 0x9D, 0x85),
            ["Char"]                 = Color.FromRgb(0xD6, 0x9D, 0x85),
            ["Preprocessor"]         = Color.FromRgb(0x9B, 0x9B, 0x9B),
            ["Punctuation"]          = Color.FromRgb(0xDC, 0xDC, 0xDC),
            ["ValueTypeKeywords"]    = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["ReferenceTypeKeywords"]= Color.FromRgb(0x56, 0x9C, 0xD6),
            ["MethodCall"]           = Color.FromRgb(0xDC, 0xDC, 0xAA),
            ["NumberLiteral"]        = Color.FromRgb(0xB5, 0xCE, 0xA8),
            ["ThisOrBaseReference"]  = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["NullOrValueKeywords"]  = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["Keywords"]             = Color.FromRgb(0xC5, 0x86, 0xC0),
            ["GotoKeywords"]         = Color.FromRgb(0xC5, 0x86, 0xC0),
            ["ContextKeywords"]      = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["ExceptionKeywords"]    = Color.FromRgb(0xC5, 0x86, 0xC0),
            ["CheckedKeyword"]       = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["UnsafeKeywords"]       = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["OperatorKeywords"]     = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["ParameterModifiers"]   = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["Modifiers"]            = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["Visibility"]           = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["NamespaceKeywords"]    = Color.FromRgb(0xC5, 0x86, 0xC0),
            ["GetSetAddRemove"]      = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["TrueFalse"]            = Color.FromRgb(0x56, 0x9C, 0xD6),
            ["TypeKeywords"]         = Color.FromRgb(0x56, 0x9C, 0xD6),
        };
        foreach (var namedColor in def.NamedHighlightingColors)
        {
            if (colorMap.TryGetValue(namedColor.Name, out var c))
                namedColor.Foreground = new SimpleHighlightingBrush(c);
        }
        _darkCSharp = def;
        return def;
    }

    /// <summary>
    /// ユーザー設定の配色を共有ハイライト定義へ反映する。
    /// 定義は全エディタ共有のため、呼び出し後に各エディタを Redraw すれば即反映される。
    /// </summary>
    private static void ApplyColorsToHighlighting(ScriptEditorSettings settings)
    {
        var def = BuildDarkCSharpHighlighting();
        if (def is null) return;

        // 参照型キーワードの色は type 系キーワード全般に反映する
        foreach (var named in def.NamedHighlightingColors)
        {
            if (settings.Colors.TryGetValue(named.Name, out var hex))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    named.Foreground = new SimpleHighlightingBrush(color);
                }
                catch { /* 不正な色は無視 */ }
            }
        }
    }
}
