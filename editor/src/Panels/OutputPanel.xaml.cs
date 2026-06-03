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
    /// <summary>
    /// スクロール位置が最下部にあるかどうか。
    /// true のときのみ新しいログが追加されると自動スクロールする。
    /// ユーザーが上にスクロールした場合は false になり、自動スクロールが停止する。
    /// </summary>
    private       bool _atBottom = true;

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

        // ScrollViewer の ScrollChangedEvent を RichTextBox からバブリングで受け取る。
        // RichTextBox 自体は ScrollChanged を持たないため AddHandler 経由で購読する。
        LogBox.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnLogBoxScrollChanged));
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

        // 最下部にいるときのみ自動スクロールする。
        // ユーザーが上にスクロールして過去のログを参照中は ScrollToEnd を呼ばない。
        if (!_scrollPending && _atBottom)
        {
            _scrollPending = true;
            // Background キューの末尾に追加することで、直前の AppendLine がすべて終わってから
            // 1 度だけ ScrollToEnd を呼ぶ（Render レイアウトの発生を最小限に抑える）。
            // ラムダ実行時に _atBottom を再チェックする：
            // スケジュール後にユーザーが上スクロールして _atBottom=false になった場合は
            // ScrollToEnd をスキップする（スケジュール時点の状態だけで判断しない）。
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                _scrollPending = false;
                if (_atBottom)
                    LogBox.ScrollToEnd();
            }));
        }
    }

    /// <summary>
    /// スクロール位置の変化を受け取り <see cref="_atBottom"/> を更新する。
    ///
    /// ExtentHeightChange != 0: ドキュメントへの行追加・削除によるコンテンツ高さの変化。
    ///   → _atBottom は変更しない（ScrollToEnd による「擬似的な最下部移動」で誤って
    ///     false になるのを防ぐ）。
    ///
    /// ExtentHeightChange == 0: ユーザー操作または ScrollToEnd() によるスクロール位置のみの変化。
    ///   → VerticalOffset が最下部か否かで _atBottom を確定する。
    /// </summary>
    private void OnLogBoxScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // コンテンツの高さ変化（行追加・削除）はスキップする
        if (e.ExtentHeightChange != 0) return;

        var scrollable = e.ExtentHeight - e.ViewportHeight;
        // scrollable <= 0: コンテンツがビューポート内に収まっている（常に最下部）
        _atBottom = scrollable <= 0 || e.VerticalOffset >= scrollable - 1.0;
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
