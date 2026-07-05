using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using SEEDEditor;
using SEEDEditor.AI.LocalLlm;
using SEEDEditor.Panels.ScriptEditor;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion;
using SEEDEditor.Scripting;

namespace SEEDEditor.Panels;

/// <summary>開いているドキュメント 1 件の情報（「タブ」パネル表示用）。</summary>
public sealed record OpenDocInfo(string FilePath, bool IsDirty, bool IsActive, bool IsReadOnly);

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
    // 選択単語ハイライトのデバウンス間隔（キャレット移動のたびの全文走査を避ける）
    private static readonly TimeSpan OccurrenceDebounce   = TimeSpan.FromMilliseconds(180);
    // これを超える文字数のファイルでは選択単語ハイライトを無効化する（巨大ファイルの操作性優先）
    private const int OccurrenceMaxTextLength = 200_000;
    // これを超える文字数のファイルではセマンティック着色を無効化する（毎編集の全文分類が重いため）
    private const int SemanticMaxTextLength   = 200_000;
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
        public required DispatcherTimer   OccurTimer;    // 選択単語ハイライトのデバウンス
        public required HighlightRenderer SelectionHi;   // 選択単語の一致ハイライト
        public required HighlightRenderer SearchHi;       // 検索一致ハイライト（オレンジ）
        public required OverviewRuler     Ruler;          // 右側の概観ルーラー
        public required SemanticColorizer Semantic;      // 型名・メソッド名などの意味的着色
        public InlineCompletionController? Inline;        // AI インライン補完（Copilot 風・任意）
        public List<ScriptDiagnostic> Diagnostics = new();
        public bool IsDirty;
        /// <summary>
        /// 読み取り専用タブか。エンジン API（SEEDScripting のソース）を定義ジャンプで
        /// 開いた場合に true。機能保証のため編集・保存・ワークスペース登録を行わない。
        /// </summary>
        public bool IsReadOnly;
        /// <summary>このドキュメントに設定されたブレークポイント集合（行追従）。</summary>
        public BreakpointSet? Breaks;
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

    /// <summary>AI インライン補完の共有プロバイダ（Groq クラウド）。遅延生成。</summary>
    private IInlineCompletionProvider? _inlineProvider;

    /// <summary>書式・配色設定と、その保存先ディレクトリ。</summary>
    private ScriptEditorSettings _settings = new();
    private string? _settingsDir;
    /// <summary>未保存編集のクラッシュ復元ストア（InitSettings で生成）。</summary>
    private ScriptRecoveryStore? _recovery;
    /// <summary>ブレークポイントの永続化ストア（設定ディレクトリ配下）。</summary>
    private BreakpointStore? _breakpointStore;

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
            // 読み取り専用タブ（エンジン API のソース）は常に編集不可を維持する。
            doc.Editor.IsReadOnly = doc.IsReadOnly || !editable;
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
        => _docs.Select(d => new OpenDocInfo(d.FilePath, d.IsDirty, d == _activeDoc, d.IsReadOnly)).ToList();

    /// <summary>
    /// 開いている各ドキュメントの現在のブレークポイント（ファイルパス→行番号）。
    /// 将来のデバッガバックエンドがアタッチ時の設定に使う。永続分は BreakpointStore を参照。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> GetBreakpoints()
        => _docs.Where(d => d.Breaks is not null)
                .ToDictionary(d => d.FilePath, d => (IReadOnlyList<int>)d.Breaks!.Lines());

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
        // 未保存編集のクラッシュ復元ストアを用意する
        _recovery    = new ScriptRecoveryStore(Path.Combine(settingsDir, "script_recovery"));
        _breakpointStore = new BreakpointStore(settingsDir);
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

    /// <summary>AI インライン補完のプロバイダを取得する（初回に生成）。</summary>
    /// <remarks>
    /// バックエンドは Groq（クラウド・OpenAI 互換）。API キー・モデルは設定から
    /// 都度読むため、設定変更は再生成なしで即反映される。
    /// </remarks>
    private IInlineCompletionProvider GetInlineProvider()
        => _inlineProvider ??= new GroqInlineCompletionProvider(
            apiKey: () => _settings.GroqApiKey,
            model:  () => _settings.GroqModel);

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
    public void OpenFile(string filePath) => OpenFileInternal(filePath, readOnly: false);

    /// <summary>
    /// ファイルを読み取り専用タブで開く（エンジン API のソースなど、機能保証のため
    /// 編集させたくないファイル向け）。定義ジャンプ（F12）から使う。
    /// </summary>
    public void OpenFileReadOnly(string filePath) => OpenFileInternal(filePath, readOnly: true);

    private void OpenFileInternal(string filePath, bool readOnly)
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
        var diagTimer  = new DispatcherTimer { Interval = DiagnosticsDebounce };
        // 選択単語ハイライトのデバウンスタイマー
        var occurTimer = new DispatcherTimer { Interval = OccurrenceDebounce };

        var doc = new DocTab
        {
            FilePath    = full,
            Editor      = editor,
            Content     = content,
            Markers     = markers,
            DiagTimer   = diagTimer,
            OccurTimer  = occurTimer,
            SelectionHi = selectionHi,
            SearchHi    = searchHi,
            Ruler       = ruler,
            Semantic    = semantic,
            IsReadOnly  = readOnly,
        };

        // ブレークポイント用ガター（行番号の左）。クリックでトグルし、変更を永続化する。
        // 保存済みのブレークポイント行があれば復元する。
        var breaks = new BreakpointSet(editor.Document, _breakpointStore?.Get(full));
        var bpMargin = new BreakpointMargin(breaks, () =>
        {
            _breakpointStore?.Set(full, breaks.Lines());
            _breakpointStore?.Save();
        });
        editor.TextArea.LeftMargins.Insert(0, bpMargin);
        doc.Breaks = breaks;

        // 編集のたびにダーティ化し、ワークスペースへ反映し、診断を予約する。
        // 読み取り専用タブ（エンジン API のソース）はユーザーのワークスペースへ登録しない
        // （型の二重定義を避けるため）。通常は編集されないが念のためガードする。
        editor.TextChanged += (_, _) =>
        {
            if (doc.IsReadOnly) return;
            SetDirty(doc, true);
            _workspace?.UpsertText(doc.FilePath, editor.Text);
            diagTimer.Stop();
            diagTimer.Start();
        };

        // AI インライン補完（Copilot 風ゴーストテキスト）。設定で有効なときだけ動く。
        // IntelliSense（補完ウィンドウ）とは共存させる（Copilot と同様）。Tab の競合は
        // OnEditorKeyDown 側で解決済み（ウィンドウ表示中は Tab=IntelliSense 確定、
        // 非表示時のみ Tab=ゴースト確定）。よって抑止はしない（常に false）。
        doc.Inline = new InlineCompletionController(
            editor,
            GetInlineProvider(),
            isEnabled:    () => _settings.InlineCompletionEnabled,
            isSuppressed: () => false);

        // IntelliSense: 文字入力に応じて補完ウィンドウを開く
        editor.TextArea.TextEntered += (_, e) => OnTextEntered(doc, e.Text);
        // F12（定義へ移動）・Ctrl+Space（補完を手動起動）・Tab/Esc（インライン補完）
        editor.PreviewKeyDown += (_, e) => OnEditorKeyDown(doc, e);
        diagTimer.Tick += (_, _) =>
        {
            diagTimer.Stop();
            _ = RunDiagnosticsAsync(doc);
            _ = RunSemanticColorizeAsync(doc);
            // 未保存なら内容を退避、保存済みなら退避を消す（クラッシュ復元用）
            UpdateRecovery(doc);
        };
        occurTimer.Tick += (_, _) =>
        {
            occurTimer.Stop();
            UpdateOccurrenceHighlight(doc);
        };

        // マーカーにホバーしたら診断メッセージをツールチップ表示し、外れたら閉じる
        editor.MouseHover        += (_, e) => ShowDiagnosticToolTip(doc, e);
        editor.MouseHoverStopped += (_, _) => HideDiagnosticToolTip();

        // 選択単語の一致ハイライトはデバウンスして更新する（キャレット移動のたびの
        // 全文走査＋全再描画を避け、大きなファイルでも軽快に動かす）。
        void ScheduleOccurrence() { occurTimer.Stop(); occurTimer.Start(); }
        editor.TextArea.Caret.PositionChanged += (_, _) => ScheduleOccurrence();
        editor.TextArea.SelectionChanged      += (_, _) => ScheduleOccurrence();

        // コピー/カットをプレーンテキストのみに上書きする。
        // AvalonEdit 既定は選択範囲全体に RTF/HTML（ハイライト付き）を生成するため
        // 数万行のコピーが非常に遅い。プレーンテキストだけ載せれば VS 並みに速い。
        editor.TextArea.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Copy,
            (_, e) => { CopyPlainText(editor); e.Handled = true; },
            (_, e) => { e.CanExecute = true; e.Handled = true; }));
        editor.TextArea.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Cut,
            (_, e) => { CutPlainText(editor); e.Handled = true; },
            (_, e) => { e.CanExecute = !editor.IsReadOnly; e.Handled = true; }));

        // Ctrl+ホイールで表示倍率を変更する
        editor.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            editor.FontSize = Math.Clamp(editor.FontSize + (e.Delta > 0 ? 1 : -1), MinFontSize, MaxFontSize);
            e.Handled = true;
        };

        // 書式・配色設定を適用する
        ApplySettingsToEditor(editor);

        // ワークスペースに最新テキストを反映する（開いた瞬間の内容で同期）。
        // 読み取り専用（エンジン API のソース）はユーザーのワークスペースへ入れない。
        if (!readOnly)
            _workspace?.UpsertText(full, text);

        _docs.Add(doc);
        // 初期状態を現在のフォーカス状態に合わせる（読み取り専用タブは常に編集不可）
        UpdateEditability();
        ActivateDoc(doc);
        // 初回の診断・セマンティック着色を実行する（読み取り専用はワークスペース外なので不要）。
        // 構文ハイライト（正規表現ベース）は読み取り専用でもそのまま効く。
        if (!readOnly)
        {
            _ = RunDiagnosticsAsync(doc);
            _ = RunSemanticColorizeAsync(doc);
        }
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

    // ── 終了時の未保存確認 ────────────────────────────────────

    /// <summary>
    /// アプリ終了時に、未保存スクリプトがあれば保存確認する。
    /// 戻り値 false = ユーザーがキャンセル（終了を中止すべき）。
    /// シーンの未保存確認とは独立して呼び出す想定。
    /// </summary>
    public bool PromptSaveOnExit()
    {
        var dirty = _docs.Where(d => d.IsDirty).ToList();
        if (dirty.Count == 0) return true;

        var names = string.Join("\n", dirty.Select(d => "・" + Path.GetFileName(d.FilePath)));
        var result = MessageBox.Show(
            $"未保存のスクリプトがあります。保存しますか？\n\n{names}",
            "スクリプトエディタ",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        switch (result)
        {
            case MessageBoxResult.Cancel: return false;          // 終了中止
            case MessageBoxResult.Yes:    foreach (var d in dirty) Save(d); break;
            // No = 破棄して終了（何もしない）
        }
        return true;
    }

    // ── クラッシュ復元 ────────────────────────────────────────

    /// <summary>ドキュメントの退避データを最新化する（未保存なら保存、保存済みなら削除）。</summary>
    private void UpdateRecovery(DocTab doc)
    {
        if (_recovery is null) return;
        if (doc.IsDirty) _recovery.Save(doc.FilePath, doc.Editor.Text);
        else             _recovery.Remove(doc.FilePath);
    }

    /// <summary>全未保存ドキュメントの内容を即座に退避する（クラッシュ直前のフラッシュ用）。</summary>
    public void FlushRecovery()
    {
        if (_recovery is null) return;
        // クラッシュ経路（別スレッド含む）から呼ばれ得るため防御的に処理する
        foreach (var doc in _docs.Where(d => d.IsDirty))
        {
            try { _recovery.Save(doc.FilePath, doc.Editor.Text); }
            catch { /* UI スレッド外アクセス等は無視（ベストエフォート） */ }
        }
    }

    /// <summary>全退避データを削除する（アプリの正常終了時に呼ぶ）。</summary>
    public void ClearRecovery() => _recovery?.Clear();

    /// <summary>
    /// 起動時に呼ぶ。前回クラッシュの退避データがあれば復元を提案し、
    /// 承諾されたら各スクリプトを開いて未保存内容を復元する。
    /// </summary>
    public void CheckCrashRecovery()
    {
        if (_recovery is null || !_recovery.HasAny()) return;

        var entries = _recovery.LoadAll();
        if (entries.Count == 0) { _recovery.Clear(); return; }

        var names = string.Join("\n", entries.Select(e => "・" + Path.GetFileName(e.FilePath)));
        var result = MessageBox.Show(
            "前回のセッションが正常に終了しませんでした。\n" +
            $"編集中だったスクリプトの内容を復元しますか？\n\n{names}",
            "スクリプトエディタ - 復元",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            foreach (var e in entries) RestoreDoc(e.FilePath, e.Content);
        }
        // 復元してもしなくても退避データは一旦消す（復元後は編集で再退避される）
        _recovery.Clear();
    }

    /// <summary>退避内容で 1 ファイルを復元する（開いて内容を差し替え、未保存扱いにする）。</summary>
    private void RestoreDoc(string filePath, string content)
    {
        try
        {
            if (File.Exists(filePath))
            {
                OpenFile(filePath);
                var doc = _docs.FirstOrDefault(d =>
                    string.Equals(d.FilePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
                if (doc is not null && doc.Editor.Text != content)
                {
                    // Text 変更で TextChanged が発火し、ダーティ化・再退避される
                    doc.Editor.Text = content;
                }
            }
            else
            {
                EditorLog.Write($"復元スキップ（元ファイルが存在しません）: {filePath}");
            }
        }
        catch (Exception ex)
        {
            EditorLog.Write($"スクリプト復元に失敗: {Path.GetFileName(filePath)} - {ex.Message}");
        }
    }

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

    // Roslyn 分類種別 → 論理配色キー（ScriptEditorSettings.SemKeys）。
    // 正規表現ハイライトでは識別できない「識別子系」トークンのみを対象にする
    // （キーワード・文字列・コメントは標準ハイライトに委ねる）。
    // 実際の色は設定（_settings.Colors）から論理キーで引くため、メンバ変数と
    // 一時変数（ローカル・引数）を別色に分けられる。
    private static readonly Dictionary<string, string> SemanticKeyMap = new()
    {
        ["class name"]            = ScriptEditorSettings.SemKeys.Type,   // 型名
        ["record class name"]     = ScriptEditorSettings.SemKeys.Type,
        ["delegate name"]         = ScriptEditorSettings.SemKeys.Type,
        ["type parameter name"]   = ScriptEditorSettings.SemKeys.Type,
        ["struct name"]           = ScriptEditorSettings.SemKeys.Struct, // 構造体
        ["record struct name"]    = ScriptEditorSettings.SemKeys.Struct,
        ["enum name"]             = ScriptEditorSettings.SemKeys.EnumInterface,
        ["interface name"]        = ScriptEditorSettings.SemKeys.EnumInterface,
        ["method name"]           = ScriptEditorSettings.SemKeys.Method, // メソッド
        ["extension method name"] = ScriptEditorSettings.SemKeys.Method,
        ["field name"]            = ScriptEditorSettings.SemKeys.Field,  // メンバ変数（フィールド）
        ["constant name"]         = ScriptEditorSettings.SemKeys.Field,
        ["enum member name"]      = ScriptEditorSettings.SemKeys.Field,
        ["local name"]            = ScriptEditorSettings.SemKeys.Local,     // ローカル（一時）変数
        ["parameter name"]        = ScriptEditorSettings.SemKeys.Parameter, // 引数
    };

    // 分類種別 → ブラシのキャッシュ（設定色を反映済み）。設定変更時にクリアされる。
    private readonly Dictionary<string, Brush> _semanticBrushCache = new();

    /// <summary>分類種別に対応するブラシを返す（対象外なら null）。設定色があれば優先する。</summary>
    private Brush? SemanticBrush(string classification)
    {
        if (_semanticBrushCache.TryGetValue(classification, out var cached)) return cached;
        if (!SemanticKeyMap.TryGetValue(classification, out var key)) return null;

        // 論理キーに対応する色を設定から取得。無ければ既定配色にフォールバック。
        string? hex = null;
        _settings?.Colors.TryGetValue(key, out hex);
        if (hex is null) ScriptEditorSettings.DefaultColors().TryGetValue(key, out hex);
        if (hex is null) return null;

        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(hex); }
        catch { return null; } // 無効な色文字列は着色しない

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

        // 巨大ファイルでは全文分類（数万スパン）の再計算が重いので着色を無効化する
        if (doc.Editor.Document.TextLength > SemanticMaxTextLength)
        {
            doc.Semantic.Clear();
            doc.Editor.TextArea.TextView.Redraw();
            return;
        }

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

        // 巨大ファイルでは全文走査が重いので選択単語ハイライトを行わない
        if (editor.Document.TextLength > OccurrenceMaxTextLength)
        {
            if (doc.SelectionHi.Segments.Any())
            {
                doc.SelectionHi.SetSegments(new List<(int, int)>());
                editor.TextArea.TextView.Redraw();
                RefreshRuler(doc);
            }
            return;
        }

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
        if ((char.IsLetter(c) || c == '_') && _completionWindow is null)
        {
            await ShowCompletionAsync(doc);
            return;
        }

        // 表示中にテキストが変わったら（英字・記号・空白いずれでも）絞り込み結果を確認し、
        // 候補が 0 件なら空の白バーが残らないようウィンドウを閉じる。
        if (_completionWindow is not null)
            ScheduleCloseCompletionIfEmpty();
    }

    /// <summary>
    /// 絞り込み後の補完ウィンドウに表示候補が 1 件も無ければ閉じる。
    /// AvalonEdit のフィルタ適用はこの入力イベントの中で行われるため、
    /// Dispatcher で 1 段遅らせて（フィルタ確定後に）判定する。
    /// </summary>
    private void ScheduleCloseCompletionIfEmpty()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            var w = _completionWindow;
            if (w is null) return;

            // ListBox 未生成（テンプレート未適用）なら判定を先送りする
            var listBox = w.CompletionList.ListBox;
            if (listBox is null) { ScheduleCloseCompletionIfEmpty(); return; }

            // 絞り込み後の表示件数が 0 なら空バーになるので閉じる
            if (listBox.Items.Count == 0)
                w.Close();
        });
    }

    /// <summary>エディタ上のキー処理（インライン補完 Tab/Esc / F12 / Ctrl+Space / Alt+↑↓ 行移動）。</summary>
    private async void OnEditorKeyDown(DocTab doc, KeyEventArgs e)
    {
        // Alt 併用時、WPF は e.Key=Key.System・実キーを e.SystemKey に入れる
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // AI インライン補完の操作（IntelliSense 補完ウィンドウ非表示時のみ）。
        // Tab: 予測を確定して挿入。Esc: 予測を却下。予測が無ければ既定動作へ流す。
        if (_completionWindow is null && doc.Inline is { } inline && inline.HasSuggestion
            && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (key == Key.Tab)
            {
                if (inline.AcceptSuggestion()) { e.Handled = true; return; }
            }
            else if (key == Key.Escape)
            {
                inline.Dismiss();
                e.Handled = true;
                return;
            }
        }

        // Alt+↑ / Alt+↓: 現在行（複数行選択時はその範囲）を丸ごと上下へ移動する
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Up or Key.Down)
        {
            e.Handled = true;
            MoveLines(doc.Editor, key == Key.Up ? -1 : +1);
            return;
        }

        // Ctrl+C / Ctrl+X: 高速なプレーンテキストコピー（AvalonEdit のリッチ生成を回避）。
        // PreviewKeyDown で Handled にすることでコマンド変換を止め、二重処理を防ぐ。
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            CopyPlainText(doc.Editor);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X)
        {
            CutPlainText(doc.Editor);
            e.Handled = true;
            return;
        }

        // Enter: 直前が「{」なら閉じ括弧を自動生成し、間へインデント付きで改行する。
        // 補完ウィンドウ表示中は Enter が候補確定に使われるので対象外にする。
        if (key is Key.Enter or Key.Return
            && _completionWindow is null
            && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            if (TryExpandBraceOnEnter(doc.Editor))
            {
                e.Handled = true;
                return;
            }
        }

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

    /// <summary>
    /// キャレット直前が「{」のとき Enter で閉じ括弧「}」を自動生成し、
    /// 括弧の間へインデント付きの空行を作ってキャレットを置く。
    /// 例: <c>if (x) {|</c> で Enter →
    /// <code>
    /// if (x) {
    ///     |
    /// }
    /// </code>
    /// 直後に既に「}」がある場合は二重生成せず、間を広げるだけにする。
    /// 処理した場合は true（呼び出し側が既定の改行を抑止する）。
    /// </summary>
    private bool TryExpandBraceOnEnter(TextEditor editor)
    {
        // 範囲選択中は既定動作（選択置換）に任せる
        if (editor.SelectionLength > 0) return false;

        var document = editor.Document;
        int caret = editor.CaretOffset;
        if (caret == 0) return false;

        // 直前の文字が「{」でなければ対象外
        if (document.GetCharAt(caret - 1) != '{') return false;

        // 閉じ括弧を新規生成すべきか判定する。
        // 単に直後の 1 文字を見るだけでは「{ 改行 }」のように別行に対応する「}」が
        // ある場合を見落とすため、ドキュメント全体で「{」と「}」の数を比較し、
        // 「{」が多い（未対応の開き括弧が残る）ときだけ閉じ括弧を生成する。
        var    docText = document.Text;
        bool   needClose = CountChar(docText, '{') > CountChar(docText, '}');
        // 直後に既に「}」がある（同一行内 "{}"）ときは、その括弧を独立行へ展開する
        char   after      = caret < document.TextLength ? document.GetCharAt(caret) : '\0';
        bool   splitClose = !needClose && after == '}';

        // 現在行の先頭インデントを引き継ぎ、内側は 1 段深くする
        var    line       = document.GetLineByOffset(caret);
        string lineText   = document.GetText(line.Offset, line.Length);
        string baseIndent = GetLeadingWhitespace(lineText);
        string indentUnit = _settings.ConvertTabsToSpaces
            ? new string(' ', Math.Max(1, _settings.IndentationSize))
            : "\t";
        string innerIndent = baseIndent + indentUnit;
        string nl          = GetLineDelimiter(document, line);

        // 生成パターン:
        //   needClose  : 改行＋内側インデント＋改行＋閉じ括弧（「}」を自動生成）
        //   splitClose : 改行＋内側インデント＋改行＋インデント（既存「}」を独立行へ）
        //   それ以外   : 改行＋内側インデント（対応済みなので普通のインデント改行のみ）
        string insertion =
            needClose  ? nl + innerIndent + nl + baseIndent + "}" :
            splitClose ? nl + innerIndent + nl + baseIndent :
                         nl + innerIndent;
        document.Insert(caret, insertion);

        // キャレットを内側インデント行の末尾へ置く
        editor.CaretOffset = caret + nl.Length + innerIndent.Length;
        return true;
    }

    /// <summary>文字列中に指定文字が現れる回数を数える。</summary>
    private static int CountChar(string s, char target)
    {
        int n = 0;
        foreach (var ch in s) if (ch == target) n++;
        return n;
    }

    /// <summary>文字列先頭の空白（スペース・タブ）だけを返す。</summary>
    private static string GetLeadingWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s.Substring(0, i);
    }

    /// <summary>指定行の改行文字列を返す（無ければ既定の CRLF）。</summary>
    private static string GetLineDelimiter(ICSharpCode.AvalonEdit.Document.TextDocument document,
                                           ICSharpCode.AvalonEdit.Document.DocumentLine line)
    {
        // DelimiterLength==0（最終行）のときは前行の改行を採用、無ければ CRLF
        if (line.DelimiterLength > 0)
            return document.GetText(line.Offset + line.Length, line.DelimiterLength);
        return document.TextLength >= 2 && document.Text.Contains('\r') ? "\r\n" : "\n";
    }

    /// <summary>
    /// 選択範囲（無ければ現在行）をプレーンテキストとしてクリップボードへコピーする。
    /// AvalonEdit 既定のリッチテキスト生成を回避し、数万行でも高速にコピーする。
    /// </summary>
    private static void CopyPlainText(TextEditor editor)
    {
        var text = GetCopyText(editor);
        if (!string.IsNullOrEmpty(text)) SetClipboardText(text);
    }

    /// <summary>コピー相当の内容を切り取る（プレーンテキスト）。</summary>
    private static void CutPlainText(TextEditor editor)
    {
        if (editor.IsReadOnly) return;
        var text = GetCopyText(editor);
        if (string.IsNullOrEmpty(text)) return;
        SetClipboardText(text);

        if (editor.SelectionLength > 0)
        {
            // 選択範囲を削除する
            var seg = editor.TextArea.Selection.SurroundingSegment;
            editor.Document.Remove(seg.Offset, seg.Length);
        }
        else
        {
            // 選択が無ければ現在行を丸ごと削除する（改行込み）
            var line = editor.Document.GetLineByOffset(editor.CaretOffset);
            editor.Document.Remove(line.Offset, line.TotalLength);
        }
    }

    /// <summary>コピー対象文字列を返す（選択があればそれ、無ければ現在行＋改行）。</summary>
    private static string GetCopyText(TextEditor editor)
    {
        if (editor.SelectionLength > 0) return editor.SelectedText;
        // 選択が無い場合は VS 同様に現在行を丸ごと（改行含む）コピーする
        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
        return editor.Document.GetText(line.Offset, line.TotalLength);
    }

    /// <summary>クリップボードへプレーンテキストを設定する（一時的な失敗はリトライ）。</summary>
    private static void SetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch { System.Threading.Thread.Sleep(10); }
        }
    }

    /// <summary>
    /// 選択中の行（または現在行）を丸ごと上下に移動する（Alt+↑ / Alt+↓）。
    /// delta = -1 で上へ、+1 で下へ。選択とキャレット位置は移動後も相対的に保つ。
    /// </summary>
    private static void MoveLines(TextEditor editor, int delta)
    {
        var doc  = editor.Document;
        var area = editor.TextArea;

        // 選択範囲（無ければキャレット位置）から対象行の範囲を求める
        int selStart = area.Selection.IsEmpty ? editor.CaretOffset : area.Selection.SurroundingSegment.Offset;
        int selEnd   = area.Selection.IsEmpty ? editor.CaretOffset : area.Selection.SurroundingSegment.EndOffset;

        var firstLine = doc.GetLineByOffset(selStart);
        var lastLine  = doc.GetLineByOffset(selEnd);
        // 選択末尾が行頭（次行の先頭）に達している場合は、その行を含めない
        if (lastLine != firstLine && selEnd == lastLine.Offset && selEnd > selStart)
            lastLine = lastLine.PreviousLine;

        int startNum = firstLine.LineNumber;
        int endNum   = lastLine.LineNumber;

        // 端では移動できない
        if (delta < 0 && startNum <= 1)             return;
        if (delta > 0 && endNum   >= doc.LineCount) return;

        // 移動対象ブロックに対する選択・キャレットの相対オフセットを記録する
        int blockOffset = firstLine.Offset;
        int caretRel    = editor.CaretOffset - blockOffset;
        int anchorRel   = selStart - blockOffset;
        int headRel     = selEnd   - blockOffset;
        bool hadSelection = !area.Selection.IsEmpty;

        // 隣接行を含めた領域 [lo..hi] を、行内容を並べ替えて 1 回で置換する
        int lo = delta < 0 ? startNum - 1 : startNum;
        int hi = delta < 0 ? endNum       : endNum + 1;
        var loLine = doc.GetLineByNumber(lo);
        var hiLine = doc.GetLineByNumber(hi);

        // 領域内の改行文字（lo 行は必ず後続行があるので区切りを持つ）
        string delim = doc.GetText(loLine.Offset + loLine.Length, loLine.DelimiterLength);

        // 領域各行の内容（改行を除く）を取得する
        var contents = new List<string>();
        for (var l = loLine; l != null && l.LineNumber <= hi; l = l.NextLine)
            contents.Add(doc.GetText(l.Offset, l.Length));

        // 並べ替え: 上移動は先頭（隣接行）を末尾へ、下移動は末尾（隣接行）を先頭へ
        if (delta < 0) { var top = contents[0]; contents.RemoveAt(0); contents.Add(top); }
        else           { var bot = contents[^1]; contents.RemoveAt(contents.Count - 1); contents.Insert(0, bot); }

        int regionStart = loLine.Offset;
        int regionLen   = hiLine.EndOffset - loLine.Offset;
        doc.Replace(regionStart, regionLen, string.Join(delim, contents));

        // 移動後のブロック先頭を求め、選択・キャレットを相対位置で復元する
        int newStartNum = startNum + delta;
        int newBlockOffset = doc.GetLineByNumber(newStartNum).Offset;
        editor.CaretOffset = newBlockOffset + caretRel;
        if (hadSelection)
            area.Selection = Selection.Create(area, newBlockOffset + anchorRel, newBlockOffset + headRel);
        else
            area.ClearSelection();
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

    /// <summary>エンジン API を提供するアセンブリ名（このソースへは読み取り専用で開く）。</summary>
    private const string EngineAssemblyName = "SEEDScripting";

    /// <summary>
    /// F12: キャレット位置のシンボル定義へジャンプする（ファイルまたぎ対応）。
    ///
    /// 1) ユーザースクリプト内の定義 → 通常タブで開いて移動。
    /// 2) エンジン API（SEEDScripting）の定義 → そのソースを読み取り専用タブで開いて移動。
    ///    機能保証のため編集はできない。
    /// </summary>
    private async Task GoToDefinitionAsync(DocTab doc)
    {
        if (_workspace is null) return;
        _workspace.UpsertText(doc.FilePath, doc.Editor.Text);

        var document = _workspace.GetDocument(doc.FilePath);
        if (document is null) return;

        int caret = doc.Editor.CaretOffset;

        // 1) ユーザースクリプト（プロジェクト内ソース）の定義
        var inSource = await RoslynCompletion.ResolveDefinitionAsync(document, caret);
        if (inSource is { } r)
        {
            NavigateToDefinition(r.filePath, r.offset, readOnly: false);
            return;
        }

        // 2) エンジン API（SEEDScripting）の定義 → ソースを読み取り専用で開く
        var meta = await RoslynCompletion.ResolveMetadataDefinitionAsync(document, caret);
        if (meta is { } m &&
            string.Equals(m.assembly, EngineAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            var src = ResolveEngineSource(m.typeName, m.memberName);
            if (src is { } s)
            {
                NavigateToDefinition(s.filePath, s.offset, readOnly: true);
                return;
            }
            EditorLog.Write($"エンジン API '{m.typeName}' のソースが見つかりませんでした。");
            return;
        }

        EditorLog.Write("定義が見つかりませんでした（.NET 標準ライブラリなど外部の型はジャンプ対象外です）");
    }

    /// <summary>
    /// 指定ファイルを（必要なら開いて）該当オフセットへジャンプする。
    /// readOnly の場合はエンジン API のソースとして読み取り専用タブで開く。
    /// </summary>
    private void NavigateToDefinition(string filePath, int offset, bool readOnly)
    {
        if (readOnly) OpenFileReadOnly(filePath);
        else          OpenFile(filePath);

        var full = Path.GetFullPath(filePath);
        var target = _docs.FirstOrDefault(d =>
            string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        int clamped = Math.Min(offset, target.Editor.Document.TextLength);
        target.Editor.CaretOffset = clamped;
        var line = target.Editor.Document.GetLineByOffset(clamped).LineNumber;
        target.Editor.ScrollToLine(line);
        target.Editor.Focus();
    }

    /// <summary>
    /// エンジン API（SEEDScripting）の型名から、対応するソース .cs ファイルと、
    /// その中の定義位置（型宣言・メンバ宣言）のオフセットを引き当てる。
    /// ソースが見つからなければ null。
    /// </summary>
    private (string filePath, int offset)? ResolveEngineSource(string typeName, string? memberName)
    {
        var srcDir = FindEngineSourceDir();
        if (srcDir is null) return null;

        try
        {
            // まずファイル名が型名と一致する .cs を探す（SEED API は 1 型 1 ファイル命名）
            string? file = Directory
                .EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), typeName, StringComparison.OrdinalIgnoreCase));

            // 見つからなければ、型宣言を含むファイルを走査して探す
            file ??= Directory
                .EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                .FirstOrDefault(f => Regex.IsMatch(
                    File.ReadAllText(f),
                    $@"\b(class|struct|interface|enum)\s+{Regex.Escape(typeName)}\b"));

            if (file is null) return null;

            var text = File.ReadAllText(file);
            return (file, FindDeclarationOffset(text, typeName, memberName));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ソーステキスト中の定義位置オフセットを求める。メンバ名があればそれを優先し、
    /// なければ型宣言、いずれも無ければ先頭（0）を返す。
    /// </summary>
    private static int FindDeclarationOffset(string text, string typeName, string? memberName)
    {
        // メンバ（メソッド/プロパティ/フィールド）名の宣言らしき位置
        if (!string.IsNullOrEmpty(memberName))
        {
            var m = Regex.Match(text, $@"\b{Regex.Escape(memberName!)}\b");
            if (m.Success) return m.Index;
        }
        // 型宣言（class/struct/interface/enum TypeName）
        var t = Regex.Match(text, $@"\b(class|struct|interface|enum)\s+{Regex.Escape(typeName)}\b");
        if (t.Success)
        {
            // 型名そのものの先頭に合わせる
            int nameIdx = text.IndexOf(typeName, t.Index, StringComparison.Ordinal);
            return nameIdx >= 0 ? nameIdx : t.Index;
        }
        return 0;
    }

    // エンジンソースディレクトリ（scripting/src）の探索結果キャッシュ（"" は探索失敗）
    private string? _engineSrcDir;

    /// <summary>
    /// エンジン API のソースディレクトリ（リポジトリの scripting/src）を特定する。
    /// 実行ディレクトリから親を遡って scripting/src を探す。見つからなければ null。
    /// </summary>
    private string? FindEngineSourceDir()
    {
        if (_engineSrcDir is not null)
            return _engineSrcDir.Length == 0 ? null : _engineSrcDir;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripting", "src");
            if (Directory.Exists(candidate))
            {
                _engineSrcDir = candidate;
                return candidate;
            }
            dir = dir.Parent;
        }
        _engineSrcDir = "";   // 失敗を記憶して次回以降の走査を避ける
        return null;
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
        // 読み取り専用タブ（エンジン API のソース）は保存しない（機能保証のため）。
        if (doc.IsReadOnly) return;
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

        // 編集で移動したブレークポイント位置を保存時に永続化する（行番号が最新になる）
        if (_breakpointStore is not null && doc.Breaks is not null)
        {
            _breakpointStore.Set(doc.FilePath, doc.Breaks.Lines());
            _breakpointStore.Save();
        }

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

        // 保存できたので退避データは不要（クラッシュ復元対象から外す）
        _recovery?.Remove(doc.FilePath);

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
        // 閉じたドキュメントの退避データは不要（保存済み or 破棄を選択済み）
        _recovery?.Remove(doc.FilePath);
        doc.DiagTimer.Stop();
        doc.OccurTimer.Stop();
        doc.Inline?.Dispose();   // インライン補完のタイマー・購読・レンダラを解放
        doc.Inline = null;
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
