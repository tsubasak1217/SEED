using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using SEEDEditor;
using SEEDEditor.Panels.ScriptEditor;
using SEEDEditor.Scripting;

namespace SEEDEditor.Panels;

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

    /// <summary>タブ 1 枚分の状態。</summary>
    private sealed class DocTab
    {
        public required string            FilePath;
        public required TextEditor        Editor;
        public required TabItem           Item;
        public required TextBlock         DirtyMark;
        public required TextMarkerService Markers;
        public required DispatcherTimer   DiagTimer;
        public bool IsDirty;
    }

    private readonly TabControl _tabs;
    private readonly List<DocTab> _docs = new();
    private readonly TextBlock _emptyHint;
    private readonly FindReplaceBar _findBar;

    /// <summary>スクリプト全体の意味解析ワークスペース（IntelliSense / F12 用）。遅延生成。</summary>
    private ScriptWorkspace? _workspace;
    private string? _assetsRoot;
    /// <summary>現在表示中の補完ウィンドウ（多重表示防止）。</summary>
    private CompletionWindow? _completionWindow;

    /// <summary>書式・配色設定と、その保存先ディレクトリ。</summary>
    private ScriptEditorSettings _settings = new();
    private string? _settingsDir;

    /// <summary>ファイルの保存が完了したときに発火する（フルパス）。コンパイル成否に関わらず発火。</summary>
    public event Action<string>? ScriptSaved;

    public ScriptEditorPanel()
    {
        Background = BrushBg;

        _tabs = new TabControl
        {
            Background      = BrushBg,
            BorderThickness = new Thickness(0),
        };

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
        body.Children.Add(_tabs);
        body.Children.Add(_emptyHint);

        var toolbar = BuildToolbar();

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_findBar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(_findBar);
        root.Children.Add(body);
        Content = root;

        // タブ切り替え時に検索バーの対象エディタを更新する
        _tabs.SelectionChanged += (_, _) => _findBar.SetTarget(CurrentEditor());

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

    /// <summary>現在アクティブなタブのエディタ（なければ null）。</summary>
    private TextEditor? CurrentEditor()
        => (_tabs.SelectedItem as TabItem)?.Content as TextEditor;

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
            foreach (var doc in _docs)
            {
                ApplySettingsToEditor(doc.Editor);
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
            case Key.S:                       // 保存
                SaveCurrent();
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
            _tabs.SelectedItem = existing.Item;
            existing.Editor.Focus();
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
        var (header, dirtyMark) = BuildTabHeader(Path.GetFileName(full));
        var item = new TabItem
        {
            Header     = header,
            Content    = editor,
            Background = BrushTabBg,
        };

        // 診断（波線）用マーカーサービスを TextView に登録する
        var markers = new TextMarkerService(editor.Document);
        editor.TextArea.TextView.BackgroundRenderers.Add(markers);

        // 診断の再計算を遅延実行するデバウンスタイマー
        var diagTimer = new DispatcherTimer { Interval = DiagnosticsDebounce };

        var doc = new DocTab
        {
            FilePath  = full,
            Editor    = editor,
            Item      = item,
            DirtyMark = dirtyMark,
            Markers   = markers,
            DiagTimer = diagTimer,
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
        };

        // マーカーにホバーしたら診断メッセージをツールチップ表示する
        editor.MouseHover        += (_, e) => ShowDiagnosticToolTip(doc, e);
        editor.MouseHoverStopped += (_, _) => editor.ToolTip = null;

        // Ctrl+ホイールで表示倍率を変更する
        editor.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            editor.FontSize = Math.Clamp(editor.FontSize + (e.Delta > 0 ? 1 : -1), MinFontSize, MaxFontSize);
            e.Handled = true;
        };

        // タブヘッダーの ✕ ボタン（BuildTabHeader 内で作った closeBtn を後付けで配線）
        if (header.Children[^1] is TextBlock closeBtn)
            closeBtn.MouseLeftButtonDown += (_, e) => { CloseTab(doc); e.Handled = true; };
        // 中クリックでも閉じられるようにする
        item.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { CloseTab(doc); e.Handled = true; }
        };

        // 書式・配色設定を適用する
        ApplySettingsToEditor(editor);

        // ワークスペースに最新テキストを反映する（開いた瞬間の内容で同期）
        _workspace?.UpsertText(full, text);

        _docs.Add(doc);
        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;
        editor.Focus();
        // 初期状態を現在のフォーカス状態に合わせる（フォーカスが移るまで読み取り専用）
        UpdateEditability();
        UpdateEmptyHint();
        // 初回の診断を実行する
        _ = RunDiagnosticsAsync(doc);
    }

    /// <summary>現在アクティブなタブを保存する。</summary>
    public void SaveCurrent()
    {
        if (_tabs.SelectedItem is not TabItem item) return;
        var doc = _docs.FirstOrDefault(d => d.Item == item);
        if (doc is not null) Save(doc);
    }

    /// <summary>未保存の変更があるタブが存在するか。</summary>
    public bool HasUnsavedChanges => _docs.Any(d => d.IsDirty);

    // ── 診断（エラー・警告・文法エラー）─────────────────────

    // Roslyn の参照アセンブリはコンパイルごとに再取得すると重いためキャッシュする
    private static readonly Lazy<List<MetadataReference>> _diagRefs = new(() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList());

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
                    .Select(d => (
                        span:     d.Location.SourceSpan,
                        message:  $"{(d.Severity == DiagnosticSeverity.Error ? "エラー" : "警告")} {d.Id}: {d.GetMessage()}",
                        isError:  d.Severity == DiagnosticSeverity.Error))
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
        int errorCount = 0, warnCount = 0;
        foreach (var (span, message, isError) in diags)
        {
            var color = isError
                ? Color.FromRgb(0xF4, 0x47, 0x47)   // 赤
                : Color.FromRgb(0xD7, 0xBA, 0x36);   // 黄
            doc.Markers.Create(span.Start, Math.Max(span.Length, 1), color, message);
            if (isError) errorCount++; else warnCount++;
        }
        doc.Editor.TextArea.TextView.Redraw();

        // タブヘッダーにエラー/警告数を反映する（アイコン代わり）
        UpdateTabDiagnosticBadge(doc, errorCount, warnCount);
    }

    /// <summary>マウス下のマーカーがあれば診断メッセージをツールチップ表示する。</summary>
    private static void ShowDiagnosticToolTip(DocTab doc, MouseEventArgs e)
    {
        var pos = doc.Editor.GetPositionFromPoint(e.GetPosition(doc.Editor));
        if (pos is null) { doc.Editor.ToolTip = null; return; }

        int offset = doc.Editor.Document.GetOffset(pos.Value.Location);
        var marker = doc.Markers.GetMarkersAtOffset(offset).FirstOrDefault();
        if (marker?.ToolTip is null) { doc.Editor.ToolTip = null; return; }

        doc.Editor.ToolTip = new ToolTip
        {
            Content = new TextBlock { Text = marker.ToolTip, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 },
        };
        (doc.Editor.ToolTip as ToolTip)!.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>タブ名の横にエラー/警告数バッジを表示する。</summary>
    private void UpdateTabDiagnosticBadge(DocTab doc, int errors, int warnings)
    {
        if (doc.Item.Header is not StackPanel header) return;
        // 既存バッジ（Tag="diag"）を除去
        var old = header.Children.OfType<TextBlock>().FirstOrDefault(t => (t.Tag as string) == "diag");
        if (old is not null) header.Children.Remove(old);
        if (errors == 0 && warnings == 0) return;

        var badge = new TextBlock
        {
            Tag               = "diag",
            Text              = errors > 0 ? $" ⛔{errors}" : $" ⚠{warnings}",
            Foreground        = new SolidColorBrush(errors > 0
                                    ? Color.FromRgb(0xF4, 0x47, 0x47)
                                    : Color.FromRgb(0xD7, 0xBA, 0x36)),
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // ● 未保存マークの前（ファイル名の直後）に挿入する
        header.Children.Insert(1, badge);
    }

    // ── IntelliSense 補完 / F12 定義ジャンプ ─────────────────

    /// <summary>入力文字に応じて補完ウィンドウを起動する。</summary>
    private async void OnTextEntered(DocTab doc, string enteredText)
    {
        if (_workspace is null || enteredText.Length == 0) return;

        char c = enteredText[0];
        // 識別子文字または "." のときのみ補完を試みる（記号入力では出さない）
        bool trigger = char.IsLetter(c) || c == '_' || c == '.';
        if (!trigger) return;

        await ShowCompletionAsync(doc);
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

        int position = doc.Editor.CaretOffset;
        var list = await RoslynCompletion.GetCompletionsAsync(document, position);
        if (list is null || list.ItemsList.Count == 0) return;

        // 既存のウィンドウを閉じてから新規表示する
        _completionWindow?.Close();

        var window = new CompletionWindow(doc.Editor.TextArea)
        {
            CloseAutomatically = true,
            Width              = 340,
        };
        foreach (var item in list.ItemsList.Take(200))
            window.CompletionList.CompletionData.Add(new RoslynCompletionData(item));

        window.Closed += (_, _) => _completionWindow = null;
        window.Show();
        _completionWindow = window;
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
        _docs.Remove(doc);
        _tabs.Items.Remove(doc.Item);
        UpdateEmptyHint();
    }

    private void SetDirty(DocTab doc, bool dirty)
    {
        if (doc.IsDirty == dirty) return;
        doc.IsDirty = dirty;
        doc.DirtyMark.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>タブヘッダー（ファイル名 + 未保存マーク + 閉じるボタン）を生成する。</summary>
    private static (StackPanel header, TextBlock dirtyMark) BuildTabHeader(string fileName)
    {
        var name = new TextBlock
        {
            Text              = fileName,
            Foreground        = BrushText,
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var dirty = new TextBlock
        {
            Text              = "●",
            Foreground        = BrushAccent,
            FontSize          = 9,
            Margin            = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility        = Visibility.Collapsed,
        };
        var close = new TextBlock
        {
            Text              = "✕",
            Foreground        = BrushDim,
            FontSize          = 10,
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = Cursors.Hand,
        };
        close.MouseEnter += (_, _) => close.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66));
        close.MouseLeave += (_, _) => close.Foreground = BrushDim;

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(name);
        sp.Children.Add(dirty);
        sp.Children.Add(close);  // 最後の子要素として追加（OpenFile 側で配線される）
        return (sp, dirty);
    }
}
