using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SEEDEditor.Panels;

/// <summary>
/// エンジン／ゲームのログを表示する Output パネル。
///
/// 大量ログ（毎フレーム出力など）でも UI が固まらないよう、次の 2 段構えにしている。
/// 1. 受信は任意スレッドから来るのでスレッドセーフキューへ貯めるだけにする。
/// 2. 一定間隔のタイマで少量ずつ、仮想化 ListBox へ反映する。
///    ListBox は VirtualizingStackPanel により画面内の数十行だけを実体化するため、
///    行数・追記頻度に関係なく追加・トリム・スクロールが軽量に保たれる。
/// </summary>
public partial class OutputPanel : UserControl
{
    private const int MaxLines = 1000;

    /// <summary>
    /// 上へスクロールして閲覧中（最下部に追従していない）は先頭トリムを保留する上限。
    /// この間はここに達するまで行を捨てず、閲覧位置を固定する。仮想化により表示コストは
    /// 一定なので大きめに取れる（毎秒30行のフラッドでも十数分ぶんの余裕がある）。
    /// 追従へ復帰すると保留分は一括で MaxLines まで間引かれる。
    /// </summary>
    private const int MaxLinesWhileScrolledUp = MaxLines * 30;

    /// <summary>
    /// ログの発生源カテゴリ。
    /// - <see cref="Game"/>: ユーザースクリプトの Debug.Log 出力（<c>[Script]</c> 系）。
    /// - <see cref="Engine"/>: それ以外（Rust エンジン・エディタ内部・ビルド・ランタイム通知など）。
    /// </summary>
    private enum LogCategory { Engine, Game }

    /// <summary>表示フィルタ。ComboBox の選択インデックスと一致させる（0=すべて, 1=エンジン, 2=ゲーム）。</summary>
    private enum LogFilter { All = 0, Engine = 1, Game = 2 }

    /// <summary>現在の表示フィルタ。</summary>
    private LogFilter _filter = LogFilter.All;

    /// <summary>1 行分の表示データ（テキストと文字色）。生成後は不変。</summary>
    private sealed class LogRow
    {
        public string Text { get; }
        public Brush  Color { get; }
        public LogRow(string text, Brush color) { Text = text; Color = color; }
    }

    /// <summary>
    /// 先頭からの一括削除を 1 回の Reset 通知で行える ObservableCollection。
    /// 追従復帰時に、保留していた大量の超過行を効率よく（O(n) 1 回・通知 1 回で）間引くために使う。
    /// 通常の <c>RemoveAt(0)</c> の繰り返しは O(n^2) かつ削除数ぶんの通知が発生してヒッチする。
    /// </summary>
    private sealed class LogRowCollection : ObservableCollection<LogRow>
    {
        /// <summary>先頭から <paramref name="count"/> 件をまとめて削除する。</summary>
        public void TrimFront(int count)
        {
            if (count <= 0) return;
            if (count >= Count) { Clear(); return; }
            // 既定の内部バッキングは List<LogRow>。RemoveRange で 1 回のシフトにまとめる。
            ((List<LogRow>)Items).RemoveRange(0, count);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>ListBox にバインドする表示中の行（現在のフィルタに一致するもののみ）。</summary>
    private readonly LogRowCollection _rows = new();

    /// <summary>
    /// 追加された全ログ行の履歴（カテゴリ付き）。フィルタ切り替え時にここから再構築する。
    /// 先頭破棄を O(1) にするため Queue を使う。
    /// </summary>
    private readonly Queue<(string line, LogCategory cat)> _entries = new();

    // ── バッチ描画（大量ログ時も UI 応答を保つ）──────────────────────────────
    /// <summary>UI へ未反映の保留ログ行（producer=任意スレッド / consumer=UI スレッド）。</summary>
    private readonly ConcurrentQueue<string> _pending = new();

    /// <summary>保留件数の概算（Interlocked 管理）。上限判定に使う。</summary>
    private int _pendingCount;

    /// <summary>一定間隔で保留キューを少量ずつ UI へ反映するタイマ（UI スレッド駆動）。</summary>
    private readonly DispatcherTimer _flushTimer;

    /// <summary>
    /// 保留キューの上限。これを超えたら古い行から捨てる。
    /// 表示は最新 <see cref="MaxLines"/> 行のみで、全文は EditorLog のファイルログに残るため、
    /// あふれた古い保留行を捨てても実害は小さい。停止後のドレイン時間を有界化する狙い。
    /// </summary>
    private const int MaxPending = 6000;

    /// <summary>1 ティックで処理する最大行数（ティックを短く保ち、間に入力・描画を通す）。</summary>
    private const int FlushChunk = 500;

    /// <summary>フラッシュ間隔（ミリ秒）。</summary>
    private const int FlushIntervalMs = 80;

    /// <summary>
    /// スクロール位置が最下部にあるか。true のときのみ新規行追加で自動スクロールする。
    /// ユーザーが上へスクロールしたら false になり、自動追従を止める。
    /// </summary>
    private bool _atBottom = true;

    /// <summary>ListBox 内部の ScrollViewer（スクロール位置補正に使う。初回スクロール時に取得）。</summary>
    private ScrollViewer? _scrollViewer;

    /// <summary>このティックで先頭から捨てた行数（閲覧中のスクロール位置補正に使う）。</summary>
    private int _frontTrimmedThisTick;

    // ログ種別ごとの文字色
    private static readonly SolidColorBrush BrushDefault = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush BrushRuntime = new(Color.FromRgb(0x6C, 0xD5, 0xF5)); // 水色
    private static readonly SolidColorBrush BrushError   = new(Color.FromRgb(0xF4, 0x84, 0x84)); // 赤
    private static readonly SolidColorBrush BrushBuild   = new(Color.FromRgb(0xCC, 0xCC, 0x55)); // 黄

    static OutputPanel()
    {
        // 色ブラシは複数スレッドから参照され得ないが、Freeze しておくと描画が軽くなる。
        BrushDefault.Freeze(); BrushRuntime.Freeze(); BrushError.Freeze(); BrushBuild.Freeze();
    }

    public OutputPanel()
    {
        InitializeComponent();
        LogList.ItemsSource = _rows;

        EditorLog.LogWritten += OnLogWritten;

        // 一定間隔で保留ログを少量ずつ描画するタイマ。Background 優先度で入力を妨げない。
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FlushIntervalMs),
        };
        _flushTimer.Tick += (_, _) => FlushTick();
        _flushTimer.Start();

        Unloaded += (_, _) =>
        {
            EditorLog.LogWritten -= OnLogWritten;
            _flushTimer.Stop();
        };

        // ListBox 内の ScrollViewer から ScrollChanged をバブリングで受け取り、最下部追従を判定する。
        LogList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnScrollChanged));
    }

    private void OnLogWritten(string line)
    {
        // 任意スレッドから届く。キューへ積むだけにして、UI への反映はタイマ（FlushTick）に任せる。
        _pending.Enqueue(line);
        int n = Interlocked.Increment(ref _pendingCount);

        // 上限超過分は古い行から捨てる（描画は最新 MaxLines 行のみ・全文はファイルログに残る）。
        while (n > MaxPending && _pending.TryDequeue(out _))
            n = Interlocked.Decrement(ref _pendingCount);
    }

    /// <summary>
    /// タイマ 1 ティック分の処理。保留キューから最大 <see cref="FlushChunk"/> 行だけ UI へ反映する。
    /// 残りは次ティックへ持ち越すため、ティック間に入力・描画が処理される隙間ができる。
    /// </summary>
    private void FlushTick()
    {
        if (_pending.IsEmpty) return;

        _frontTrimmedThisTick = 0;
        int added = 0;
        while (added < FlushChunk && _pending.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingCount);
            AppendLine(line);
            added++;
        }

        if (added > 0 && _atBottom)
        {
            // 追従中は最下部へ 1 回だけスクロールする（行ごとにやらない）。
            ScrollToBottom();
        }
        else if (_frontTrimmedThisTick > 0 && _scrollViewer is not null)
        {
            // 閲覧中（非追従）にハード上限で先頭を捨てた場合、捨てた行数ぶんスクロール位置を
            // 繰り下げて表示内容を固定する（＝最大数を超えても表示が流れない）。
            // 捨てた行は画面より上なので、補正すれば見た目は一切動かない。
            double target = _scrollViewer.VerticalOffset - _frontTrimmedThisTick;
            _scrollViewer.ScrollToVerticalOffset(target < 0 ? 0 : target);
        }
    }

    /// <summary>1 行を履歴へ蓄積し、フィルタに一致すれば表示行へ追加する。</summary>
    private void AppendLine(string line)
    {
        var cat = Classify(line);
        _entries.Enqueue((line, cat));
        while (_entries.Count > MaxLines)
            _entries.Dequeue();

        if (MatchesFilter(cat))
            AddRow(line);
    }

    /// <summary>表示行コレクションへ 1 行追加し、上限を超えたら先頭を捨てる（スクロールは呼び出し側でまとめて行う）。</summary>
    private void AddRow(string line)
    {
        _rows.Add(new LogRow(line, PickBrush(line)));

        if (_atBottom)
        {
            // 追従中は上限 MaxLines を保つ。定常状態では 1 行/追加なのでヒッチしない
            // （追従復帰直後の大量超過は OnScrollChanged で一括間引き済み）。
            while (_rows.Count > MaxLines)
                _rows.RemoveAt(0);
        }
        else if (_rows.Count > MaxLinesWhileScrolledUp)
        {
            // 閲覧中（上スクロール中）はハード上限までトリムを保留するが、上限に達したら
            // メモリ保護のため最古の行を捨てる。捨てた行数はカウントしておき、ティック末で
            // スクロール位置を同数だけ繰り下げて表示内容を固定する（最大数を超えても流れない）。
            _rows.RemoveAt(0);
            _frontTrimmedThisTick++;
        }
    }

    /// <summary>最下部（最新行）までスクロールする。</summary>
    private void ScrollToBottom()
    {
        if (_rows.Count > 0)
            LogList.ScrollIntoView(_rows[_rows.Count - 1]);
    }

    /// <summary>
    /// スクロール位置の変化から <see cref="_atBottom"/> を更新する。
    /// ExtentHeightChange != 0（行の追加・削除によるコンテンツ高さ変化）のときは判定を変えず、
    /// == 0（ユーザー操作 or ScrollIntoView による位置のみの変化）のときに最下部か否かを確定する。
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // スクロール位置補正用に内部 ScrollViewer を初回に捕捉する。
        _scrollViewer ??= e.OriginalSource as ScrollViewer;

        if (e.ExtentHeightChange != 0) return;

        var scrollable = e.ExtentHeight - e.ViewportHeight;
        bool nowBottom = scrollable <= 0 || e.VerticalOffset >= scrollable - 1.0;
        _atBottom = nowBottom;

        // 追従へ復帰した瞬間に、閲覧中に保留していた超過行を一括で MaxLines まで間引く。
        // Reset 1 回で済むため大量でもヒッチしない。間引き後は最下部へ再スクロールする。
        if (nowBottom && _rows.Count > MaxLines)
        {
            _rows.TrimFront(_rows.Count - MaxLines);
            ScrollToBottom();
        }
    }

    /// <summary>
    /// ログ行の発生源を判定する。
    /// ユーザースクリプトの Debug.Log は <c>[Script]</c> 系を前置してランタイム標準出力へ流れるため、
    /// <c>[Script</c> を含む行をゲーム側、それ以外をエンジン側とする。
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
        // InitializeComponent 中（LogList 生成前）にも発火し得るためガードする。
        if (LogList is null) return;
        _filter = (LogFilter)CmbFilter.SelectedIndex;
        RebuildFromEntries();
    }

    /// <summary>履歴（<see cref="_entries"/>）から現在のフィルタに一致する行だけを再表示する。</summary>
    private void RebuildFromEntries()
    {
        _rows.Clear();
        foreach (var (line, cat) in _entries)
            if (MatchesFilter(cat))
                AddRow(line);

        _atBottom = true;   // 再構築後は最下部へ追従させる
        ScrollToBottom();
    }

    private static SolidColorBrush PickBrush(string line)
    {
        if (line.Contains("[Runtime→Editor]"))  return BrushRuntime;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("失敗") || line.Contains("EXCEPTION")) return BrushError;
        if (line.Contains("[cargo]") || line.Contains("BUILDING") ||
            line.Contains("BuildAsync"))        return BrushBuild;
        return BrushDefault;
    }

    /// <summary>Ctrl+C で選択行をコピーする。</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            OnCopySelected(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>選択中の行をクリップボードへコピーする（表示順を維持する）。</summary>
    private void OnCopySelected(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItems.Count == 0) return;
        var selected = new HashSet<object>(LogList.SelectedItems.Cast<object>());
        var sb = new StringBuilder();
        foreach (var r in _rows)
            if (selected.Contains(r))
                sb.AppendLine(r.Text);
        TrySetClipboard(sb.ToString());
    }

    /// <summary>表示中の全行をクリップボードへコピーする。</summary>
    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var r in _rows)
            sb.AppendLine(r.Text);
        TrySetClipboard(sb.ToString());
    }

    private static void TrySetClipboard(string text)
    {
        // クリップボードは他プロセスがロックしていると例外を投げることがあるので握りつぶす。
        try { if (text.Length > 0) Clipboard.SetText(text); } catch { /* ignore */ }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        // 未反映の保留行も含めて捨てる。
        while (_pending.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _pendingCount, 0);

        _rows.Clear();
        _entries.Clear();
    }
}
