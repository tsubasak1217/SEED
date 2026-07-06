using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
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
    /// ログの発生源カテゴリ。
    /// - <see cref="Game"/>: ユーザースクリプトの Debug.Log 出力（<c>[Script]</c> 系）。
    /// - <see cref="Engine"/>: それ以外（Rust エンジン・エディタ内部・ビルド・ランタイム通知など）。
    /// </summary>
    private enum LogCategory { Engine, Game }

    /// <summary>
    /// 表示フィルタ。ComboBox の選択インデックスと一致させる（0=すべて, 1=エンジン, 2=ゲーム）。
    /// </summary>
    private enum LogFilter { All = 0, Engine = 1, Game = 2 }

    /// <summary>現在の表示フィルタ。</summary>
    private LogFilter _filter = LogFilter.All;

    /// <summary>
    /// 追加された全ログ行の履歴（カテゴリ付き）。フィルタ切り替え時に、
    /// ここから該当行だけを再構築して表示する。<see cref="MaxLines"/> 件で先頭から破棄する。
    /// </summary>
    private readonly List<(string line, LogCategory cat)> _entries = new();

    // ── バッチ描画（バックログ抑制・高速化）────────────────────────────────
    // ログは任意スレッドから 1 行ずつ届く。以前は 1 行ごとに Dispatcher.BeginInvoke
    // していたため、毎フレーム出力のような大量ログで UI ディスパッチャキューが溢れ、
    // ゲームを停止しても積み残しの描画が延々と続いていた。
    // ここではスレッドセーフなキューへ貯め、UI 側で「1 回の予約」でまとめて描画する。

    /// <summary>UI へ未反映の保留ログ行（producer=任意スレッド / consumer=UI スレッド）。</summary>
    private readonly ConcurrentQueue<string> _pending = new();

    /// <summary>保留件数の概算（Interlocked 管理）。上限判定に使う。</summary>
    private int _pendingCount;

    /// <summary>フラッシュ予約中フラグ（0=未予約, 1=予約済み。Interlocked で 1 回に集約）。</summary>
    private int _flushScheduled;

    /// <summary>
    /// 保留キューの上限。これを超えたら古い行から捨てる。
    /// 表示は最新 <see cref="MaxLines"/> 行のみで、全文は EditorLog のファイルログに残るため、
    /// あふれた古い保留行を捨てても実害は小さい。停止後のドレイン時間を有界化する狙い。
    /// </summary>
    private const int MaxPending = 6000;

    /// <summary>1 回のフラッシュで処理する最大行数（UI を長く占有しないよう分割する）。</summary>
    private const int FlushChunk = 1500;
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
        // 任意スレッドから届く。キューへ積むだけにして、UI への反映は 1 回の予約に集約する。
        _pending.Enqueue(line);
        int n = Interlocked.Increment(ref _pendingCount);

        // 上限超過分は古い行から捨てる（描画は最新 MaxLines 行のみ・全文はファイルログに残る）。
        // これにより、大量出力が続いてもキューが無限に伸びず、停止後のドレインが有界になる。
        while (n > MaxPending && _pending.TryDequeue(out _))
            n = Interlocked.Decrement(ref _pendingCount);

        // フラッシュがまだ予約されていなければ 1 回だけ予約する（Background 優先度で UI 操作を妨げない）。
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(FlushPending));
    }

    /// <summary>
    /// 保留キューの行をまとめて UI へ反映する（UI スレッドで実行）。
    /// 1 回で最大 <see cref="FlushChunk"/> 行だけ処理し、残りがあれば次のフラッシュを予約する。
    /// これにより「1 行 1 ディスパッチ」による大量バックログを解消し、停止後も速やかに描画が収束する。
    /// </summary>
    private void FlushPending()
    {
        // 予約フラグを解除。処理中に届いた行は下の再予約か、producer 側の予約で拾う。
        Interlocked.Exchange(ref _flushScheduled, 0);

        int processed = 0;
        while (processed < FlushChunk && _pending.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingCount);
            AppendLine(line);
            processed++;
        }

        // まだ残っていれば次のフラッシュを予約する（1 回の処理量を抑えて UI 応答を保つ）。
        if (!_pending.IsEmpty && Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(FlushPending));
    }

    private void AppendLine(string line)
    {
        // 履歴へカテゴリ付きで蓄積する（フィルタ切り替え時の再構築に使う）。
        var cat = Classify(line);
        _entries.Add((line, cat));
        while (_entries.Count > MaxLines)
            _entries.RemoveAt(0);

        // 現在のフィルタに一致しない行は表示しない（履歴には残す）。
        if (!MatchesFilter(cat)) return;

        AppendParagraph(line);
    }

    /// <summary>
    /// 1 行を RichTextBox の末尾へ段落として追加し、最下部にいれば自動スクロールする。
    /// 表示行数を <see cref="MaxLines"/> に制限する。
    /// </summary>
    private void AppendParagraph(string line)
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

    /// <summary>
    /// ログ行の発生源を判定する。
    /// ユーザースクリプトの Debug.Log は <c>[Script]</c> / <c>[Script:警告]</c> / <c>[Script:エラー]</c>
    /// を前置してランタイム標準出力へ流れるため、<c>[Script</c> を含む行をゲーム側とみなす。
    /// それ以外はすべてエンジン側（Rust エンジン・エディタ内部・ビルド・ランタイム通知）とする。
    /// </summary>
    private static LogCategory Classify(string line)
        => line.Contains("[Script") ? LogCategory.Game : LogCategory.Engine;

    /// <summary>現在のフィルタでこのカテゴリを表示するかどうか。</summary>
    private bool MatchesFilter(LogCategory cat) => _filter switch
    {
        LogFilter.Engine => cat == LogCategory.Engine,
        LogFilter.Game   => cat == LogCategory.Game,
        _                => true,   // All
    };

    /// <summary>フィルタ ComboBox の選択が変わったら表示を再構築する。</summary>
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // InitializeComponent 中（LogBox 生成前）にも発火し得るためガードする。
        if (LogBox is null) return;
        _filter = (LogFilter)CmbFilter.SelectedIndex;
        RebuildFromEntries();
    }

    /// <summary>履歴（<see cref="_entries"/>）から現在のフィルタに一致する行だけを再表示する。</summary>
    private void RebuildFromEntries()
    {
        LogBox.Document.Blocks.Clear();
        _lineCount = 0;
        _atBottom  = true;   // 再構築後は最下部へ追従させる
        foreach (var (line, cat) in _entries)
            if (MatchesFilter(cat))
                AppendParagraph(line);
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
        // 未反映の保留行も含めて捨てる。
        while (_pending.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _pendingCount, 0);

        LogBox.Document.Blocks.Clear();
        _lineCount = 0;
        _entries.Clear();
    }
}
