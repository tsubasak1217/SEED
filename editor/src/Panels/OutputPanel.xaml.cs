using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Panels;

public partial class OutputPanel : UserControl
{
    private const int MaxLines  = 1000;
    private       int _lineCount    = 0;
    /// <summary>
    /// ScrollToEnd のスケジュール済みフラグ。
    /// Background キューに複数の AppendLine が積まれていても ScrollToEnd は 1 回にまとめる。
    /// ScrollToEnd は内部で InvalidateMeasure を呼び Render 優先度（7）のレイアウトを発生させる。
    /// Render > Input（5）のため多重呼び出しがマウスクリック応答を遅延させる原因になる。
    /// </summary>
    private       bool _scrollPending = false;

    // ログ種別ごとの文字色
    private static readonly SolidColorBrush BrushDefault = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush BrushRuntime = new(Color.FromRgb(0x6C, 0xD5, 0xF5)); // 水色
    private static readonly SolidColorBrush BrushError   = new(Color.FromRgb(0xF4, 0x84, 0x84)); // 赤
    private static readonly SolidColorBrush BrushBuild   = new(Color.FromRgb(0xCC, 0xCC, 0x55)); // 黄

    public OutputPanel()
    {
        InitializeComponent();
        EditorLog.LogWritten += OnLogWritten;
        Unloaded += (_, _) => EditorLog.LogWritten -= OnLogWritten;
    }

    private void OnLogWritten(string line)
    {
        // Background 優先度で遅延実行することで、UI 操作（ComboBox 等）の妨げにならないようにする。
        // Normal 優先度だと WPF が ComboBox の Items 更新中にキューを処理してしまい
        // AppendLine + ScrollToEnd が多数実行されて UI がフリーズする原因となる。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => AppendLine(line));
    }

    private void AppendLine(string line)
    {
        var doc = LogBox.Document;

        // 行数制限: 先頭段落を削除
        while (_lineCount >= MaxLines && doc.Blocks.Count > 0)
        {
            doc.Blocks.Remove(doc.Blocks.FirstBlock);
            _lineCount--;
        }

        var brush = PickBrush(line);
        var para  = new Paragraph(new Run(line))
        {
            Foreground = brush,
            Margin     = new Thickness(0),
            Padding    = new Thickness(0),
            LineHeight = 16,
        };
        doc.Blocks.Add(para);
        _lineCount++;

        // 連続ログ追加時に ScrollToEnd を 1 回にまとめる。
        // すでにスクロールがスケジュール済みであれば追加しない。
        if (!_scrollPending)
        {
            _scrollPending = true;
            // Background キューの末尾に追加することで、直前の AppendLine がすべて終わってから
            // 1 度だけ ScrollToEnd を呼ぶ（Render レイアウトの発生を最小限に抑える）。
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                _scrollPending = false;
                LogBox.ScrollToEnd();
            }));
        }
    }

    private static SolidColorBrush PickBrush(string line)
    {
        if (line.Contains("[Runtime→Editor]"))  return BrushRuntime;
        if (line.Contains("error", System.StringComparison.OrdinalIgnoreCase) ||
            line.Contains("失敗") || line.Contains("EXCEPTION")) return BrushError;
        if (line.Contains("[cargo]") || line.Contains("BUILDING") ||
            line.Contains("BuildAsync"))        return BrushBuild;
        return BrushDefault;
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        LogBox.SelectAll();
        ApplicationCommands.Copy.Execute(null, LogBox);
        // 選択解除
        LogBox.Selection.Select(LogBox.Document.ContentStart, LogBox.Document.ContentStart);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
        _lineCount = 0;
    }
}
