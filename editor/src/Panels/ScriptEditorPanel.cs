using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using SEEDEditor;
using SEEDEditor.Scripting;

namespace SEEDEditor.Panels;

/// <summary>
/// 内蔵 C# スクリプトエディタパネル。
///
/// 【機能】
/// - タブ式で複数の .cs ファイルを同時に開ける（AvalonEdit / C# ハイライト / 行番号）
/// - Ctrl+S で保存 → Roslyn でコンパイルチェック → エラーを Output に表示
/// - 保存成功時に ScriptSaved イベントを発火（MainWindow が RELOAD_SCRIPTS を送信）
/// - 未保存タブには ● マークを表示し、閉じるときに保存確認を出す
/// </summary>
public class ScriptEditorPanel : UserControl
{
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
        public required string     FilePath;
        public required TextEditor Editor;
        public required TabItem    Item;
        public required TextBlock  DirtyMark;
        public bool IsDirty;
    }

    private readonly TabControl _tabs;
    private readonly List<DocTab> _docs = new();
    private readonly TextBlock _emptyHint;

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

        var root = new Grid();
        root.Children.Add(_tabs);
        root.Children.Add(_emptyHint);
        Content = root;

        // Ctrl+S で現在のタブを保存する
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SaveCurrent();
                e.Handled = true;
            }
        };

        UpdateEmptyHint();
    }

    // ── 公開 API ─────────────────────────────────────────────

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

        var doc = new DocTab { FilePath = full, Editor = editor, Item = item, DirtyMark = dirtyMark };
        editor.TextChanged += (_, _) => SetDirty(doc, true);

        // タブヘッダーの ✕ ボタン（BuildTabHeader 内で作った closeBtn を後付けで配線）
        if (header.Children[^1] is TextBlock closeBtn)
            closeBtn.MouseLeftButtonDown += (_, e) => { CloseTab(doc); e.Handled = true; };
        // 中クリックでも閉じられるようにする
        item.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { CloseTab(doc); e.Handled = true; }
        };

        _docs.Add(doc);
        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;
        editor.Focus();
        UpdateEmptyHint();
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
