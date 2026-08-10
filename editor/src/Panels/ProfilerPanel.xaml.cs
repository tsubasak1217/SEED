using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SEEDEditor.Runtime;

namespace SEEDEditor.Panels;

/// <summary>
/// ランタイムのフレームプロファイラ計測結果を表示するドッキングパネル。
///
/// 【全体構成】
/// - ツールバー: 一時停止トグル・ソート順選択・概況テキスト（FPS/フレーム時間）。
/// - グラフ: 直近サンプルのフレーム時間推移を折れ線で表示（60fps ガイドライン付き）。
/// - 階層ツリー: セクションごとの平均/最大/自己時間・フレーム比・呼び出し回数を表形式で表示。
///
/// 【購読の ON/OFF】
/// ランタイム側は SET_PROFILER で明示的に有効化しない限り計測自体を行わない
/// （パネルを開いていないときのオーバーヘッドをゼロにするため）。そのため本パネルは
/// IsVisibleChanged を購読し、表示状態と RuntimeManager.SetProfilerEnabled を連動させる。
///
/// 【展開状態の維持】
/// ランタイムからレポートを受信するたびにツリーを完全に作り直すため、そのままでは
/// 毎回すべて展開状態に戻ってしまう。HierarchyPanel.Expansion.cs の前例に倣い、
/// 「折りたたまれているノードの安定キー集合」を保持し、再構築のたびに復元する。
/// 安定キーはルートからの名前パス（例: "/Frame/描画/G-Bufferパス"）。
/// </summary>
public partial class ProfilerPanel : UserControl
{
    // ============================================================
    //  受信 JSON のデータモデル（PROFILER:{json} — ランタイムから 0.5 秒ごとに届く）
    // ============================================================

    /// <summary>
    /// プロファイラレポート全体（1回分の受信データ）。
    /// フィールド名はランタイム（Rust）側の JSON スキーマに合わせて snake_case。
    /// </summary>
    private sealed class ProfilerReport
    {
        /// <summary>この窓に含まれるフレーム数。</summary>
        [JsonPropertyName("frames")] public int Frames { get; set; }
        /// <summary>集計窓の長さ（ミリ秒、およそ 500ms）。</summary>
        [JsonPropertyName("window_ms")] public double WindowMs { get; set; }
        /// <summary>窓内の平均 FPS。</summary>
        [JsonPropertyName("fps")] public double Fps { get; set; }
        /// <summary>1 フレームあたりの平均時間（ミリ秒）。</summary>
        [JsonPropertyName("frame_avg_ms")] public double FrameAvgMs { get; set; }
        /// <summary>窓内 1 フレームでの最大時間（ミリ秒）。</summary>
        [JsonPropertyName("frame_max_ms")] public double FrameMaxMs { get; set; }
        /// <summary>窓内の各フレーム時間（ミリ秒、実行順）。</summary>
        [JsonPropertyName("samples")] public List<double> Samples { get; set; } = new();
        /// <summary>計測階層のルートノード（"Frame"）。</summary>
        [JsonPropertyName("root")] public ProfilerNode? Root { get; set; }
    }

    /// <summary>計測階層の 1 ノード（セクション）。子を持てる木構造。</summary>
    private sealed class ProfilerNode
    {
        /// <summary>セクション名。</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        /// <summary>1 フレームあたりの平均時間（ミリ秒）。</summary>
        [JsonPropertyName("avg_ms")] public double AvgMs { get; set; }
        /// <summary>窓内 1 フレームでの最大時間（ミリ秒）。</summary>
        [JsonPropertyName("max_ms")] public double MaxMs { get; set; }
        /// <summary>子を除いた自己時間（ミリ秒）。</summary>
        [JsonPropertyName("self_ms")] public double SelfMs { get; set; }
        /// <summary>フレーム全体に対する割合（%）。</summary>
        [JsonPropertyName("share")] public double Share { get; set; }
        /// <summary>1 フレームあたりの平均呼び出し回数。</summary>
        [JsonPropertyName("calls")] public double Calls { get; set; }
        /// <summary>窓全体での呼び出し回数の合計。</summary>
        [JsonPropertyName("calls_total")] public int CallsTotal { get; set; }
        /// <summary>子ノード（実行順）。</summary>
        [JsonPropertyName("children")] public List<ProfilerNode> Children { get; set; } = new();
    }

    // ── 定数（マジックナンバー排除） ──────────────────────────────

    /// <summary>フレーム時間推移グラフで保持する直近サンプル数（リングバッファ容量）。</summary>
    private const int MaxGraphSamples = 240;

    /// <summary>60fps 相当のフレーム時間（ミリ秒）。グラフのガイドライン位置に使う。</summary>
    private const double GuideLine60FpsMs = 1000.0 / 60.0;

    /// <summary>グラフの縦軸オートスケールの下限（30fps 相当、ミリ秒）。実測最大値とこちらの大きい方を上限にする。</summary>
    private const double GraphAutoScaleFloorMs = 1000.0 / 30.0;

    /// <summary>ツリー行: 平均時間がこの値を超えたら「重い」(黄色)として強調する閾値（ミリ秒）。</summary>
    private const double AvgWarnThresholdMs = 2.0;

    /// <summary>ツリー行: 平均時間がこの値を超えたら「非常に重い」(赤色)として強調する閾値（ミリ秒）。</summary>
    private const double AvgCriticalThresholdMs = 5.0;

    /// <summary>ツリー行の数値列（平均/最大/自己/比率/呼び出し回数）の固定幅（ピクセル）。</summary>
    private const double NumericColumnWidth = 76.0;

    /// <summary>安定キーの階層区切り文字（HierarchyPanel.Expansion.cs の方式に合わせる）。</summary>
    private const string StableKeySeparator = "/";

    /// <summary>安定キーのルート階層における親キー（親なし）。</summary>
    private const string StableKeyRootParent = "";

    // ── 配色（ダークテーマ + 重い行の強調） ──────────────────────────

    private static readonly SolidColorBrush BrushNormal    = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush BrushWarn      = new(Color.FromRgb(0xE5, 0xC0, 0x7B));
    private static readonly SolidColorBrush BrushCritical  = new(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly SolidColorBrush BrushHeaderFg  = new(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly SolidColorBrush BrushGraphLine = new(Color.FromRgb(0x6C, 0xD5, 0xF5));
    private static readonly SolidColorBrush BrushGuideLine = new(Color.FromArgb(0x60, 0xCC, 0xCC, 0xCC));

    static ProfilerPanel()
    {
        BrushNormal.Freeze(); BrushWarn.Freeze(); BrushCritical.Freeze();
        BrushHeaderFg.Freeze(); BrushGraphLine.Freeze(); BrushGuideLine.Freeze();
    }

    /// <summary>JSON 解析オプション（キー名は snake_case を JsonPropertyName で明示しているため既定のままでよい）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>ソート順。ComboBox の選択インデックスと一致させる。</summary>
    private enum SortMode { Execution = 0, TotalTime = 1, SelfTime = 2 }

    // ── 状態 ──────────────────────────────────────────────────────

    /// <summary>購読 ON/OFF 送信先の RuntimeManager（SetRuntime で注入）。</summary>
    private RuntimeManager? _runtime;

    /// <summary>一時停止中か。true の間は受信データを破棄し表示を凍結する。</summary>
    private bool _paused;

    /// <summary>現在のツリー表示ソート順。</summary>
    private SortMode _sortMode = SortMode.Execution;

    /// <summary>直近に受信・表示したレポート（ソート順変更時の再描画に使う）。</summary>
    private ProfilerReport? _lastReport;

    /// <summary>フレーム時間推移グラフのリングバッファ（直近 <see cref="MaxGraphSamples"/> 件）。</summary>
    private readonly List<double> _graphSamples = new();

    /// <summary>折りたたまれているノードの安定キー集合。ここに無いノードは展開扱い（既定）。</summary>
    private readonly HashSet<string> _collapsedKeys = new();

    // ── 初期化 ────────────────────────────────────────────────────

    public ProfilerPanel()
    {
        InitializeComponent();

        BuildColumnHeaderRow();

        // ツリー全体の展開／折りたたみイベントを購読し、_collapsedKeys を追従させる
        // （TreeViewItem.Expanded/Collapsed はバブリングするため TreeView 側で一括購読する）。
        Tree.AddHandler(TreeViewItem.ExpandedEvent,  new RoutedEventHandler(OnItemExpanded));
        Tree.AddHandler(TreeViewItem.CollapsedEvent, new RoutedEventHandler(OnItemCollapsed));

        // パネルの表示/非表示に応じてランタイムへプロファイラ計測の購読 ON/OFF を送る。
        IsVisibleChanged += OnPanelVisibleChanged;
    }

    /// <summary>
    /// ランタイムへの参照を設定する（MainWindow から他パネル同様 SetRuntime(...) で注入される）。
    /// 設定時点でのパネルの表示状態を即座にランタイムへ伝える（表示中に接続された場合に備える）。
    /// </summary>
    public void SetRuntime(RuntimeManager? runtime)
    {
        _runtime = runtime;
        _runtime?.SetProfilerEnabled(IsVisible);
    }

    /// <summary>パネルの表示状態が変わるたびに、ランタイムへ計測の購読 ON/OFF を送る。</summary>
    private void OnPanelVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool visible = e.NewValue is true;
        _runtime?.SetProfilerEnabled(visible);
    }

    // ── データ受信 ────────────────────────────────────────────────

    /// <summary>
    /// ランタイムから PROFILER:{json} を受信するたびに MainWindow から呼ばれる。
    /// IPC コールバックは任意スレッドから来るため、必ず Dispatcher 経由で UI スレッドへマーシャルする。
    /// </summary>
    public void ApplyReport(string json)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 一時停止中は受信データを破棄し、表示を直前の状態のまま凍結する。
            if (_paused) return;

            ProfilerReport? report;
            try
            {
                report = JsonSerializer.Deserialize<ProfilerReport>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                EditorLog.Write($"ProfilerPanel — PROFILER JSON の解析に失敗: {ex.Message}");
                return;
            }
            if (report is null) return;

            _lastReport = report;

            AppendGraphSamples(report.Samples);
            DrawGraph();
            UpdateSummaryText(report);
            RebuildTree(report);

            TxtPlaceholder.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>概況テキスト（FPS・フレーム平均/最大・窓のフレーム数）を更新する。</summary>
    private void UpdateSummaryText(ProfilerReport report)
    {
        TxtSummary.Text = string.Format(CultureInfo.InvariantCulture,
            "FPS: {0:F1}  フレーム: {1:F1}ms (最大 {2:F1}ms)  窓: {3} フレーム",
            report.Fps, report.FrameAvgMs, report.FrameMaxMs, report.Frames);
    }

    // ── ツールバー操作 ────────────────────────────────────────────

    /// <summary>一時停止トグル: ON の間は ApplyReport が受信データを破棄する。</summary>
    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        _paused = BtnPause.IsChecked == true;
    }

    /// <summary>ソート順 ComboBox の選択変更: 直近レポートがあれば即座に並べ替えて再描画する。</summary>
    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        // InitializeComponent 中（Tree 生成前）にも発火し得るためガードする。
        if (Tree is null) return;
        _sortMode = (SortMode)CmbSort.SelectedIndex;
        if (_lastReport is not null) RebuildTree(_lastReport);
    }

    // ── フレーム時間推移グラフ ────────────────────────────────────

    /// <summary>リングバッファへ新規サンプルを追記し、上限を超えた分は先頭から捨てる。</summary>
    private void AppendGraphSamples(List<double> samples)
    {
        _graphSamples.AddRange(samples);
        int overflow = _graphSamples.Count - MaxGraphSamples;
        if (overflow > 0)
            _graphSamples.RemoveRange(0, overflow);
    }

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    /// <summary>
    /// フレーム時間推移グラフを描画する。
    /// 縦軸は「実測最大値と <see cref="GraphAutoScaleFloorMs"/> の大きい方」を上限にオートスケールし、
    /// 60fps 相当（<see cref="GuideLine60FpsMs"/>）に薄色の水平ガイド線を引く。
    /// </summary>
    private void DrawGraph()
    {
        GraphCanvas.Children.Clear();

        double width  = GraphCanvas.ActualWidth;
        double height = GraphCanvas.ActualHeight;
        if (width <= 0 || height <= 0 || _graphSamples.Count == 0) return;

        double observedMaxMs = _graphSamples.Max();
        double scaleMaxMs    = Math.Max(observedMaxMs, GraphAutoScaleFloorMs);

        // 60fps ガイドライン（薄色の破線）
        double guideY = height - (GuideLine60FpsMs / scaleMaxMs) * height;
        var guide = new Line
        {
            X1 = 0, Y1 = guideY, X2 = width, Y2 = guideY,
            Stroke = BrushGuideLine, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
        };
        GraphCanvas.Children.Add(guide);

        // フレーム時間の折れ線（サンプル数ぶんで幅いっぱいに引き伸ばす）
        int count = _graphSamples.Count;
        double stepX = count > 1 ? width / (count - 1) : 0;
        var points = new PointCollection(count);
        for (int i = 0; i < count; i++)
        {
            double x = i * stepX;
            double normalized = Math.Min(1.0, _graphSamples[i] / scaleMaxMs);
            double y = height - normalized * height;
            points.Add(new Point(x, y));
        }
        var polyline = new Polyline
        {
            Stroke = BrushGraphLine, StrokeThickness = 1.5,
            Points = points,
        };
        GraphCanvas.Children.Add(polyline);
    }

    // ── 階層ツリー ────────────────────────────────────────────────

    /// <summary>
    /// 列ヘッダー行（セクション名 / 平均ms / 最大ms / 自己ms / フレーム比% / 呼び出し回数）を
    /// ツリー本体の行と全く同じ列幅で構築する（コード側で揃えることで確実に一致させる）。
    /// </summary>
    private void BuildColumnHeaderRow()
    {
        var grid = BuildRowGrid();
        SetCell(grid, 0, "セクション名",   BrushHeaderFg, TextAlignment.Left);
        SetCell(grid, 1, "平均 ms",       BrushHeaderFg, TextAlignment.Right);
        SetCell(grid, 2, "最大 ms",       BrushHeaderFg, TextAlignment.Right);
        SetCell(grid, 3, "自己 ms",       BrushHeaderFg, TextAlignment.Right);
        SetCell(grid, 4, "比率 %",        BrushHeaderFg, TextAlignment.Right);
        SetCell(grid, 5, "回数",          BrushHeaderFg, TextAlignment.Right);
        grid.Margin = new Thickness(4, 3, 4, 3);
        ColumnHeaderHost.Child = grid;
    }

    /// <summary>受信したレポートから階層ツリーを完全に作り直す（展開状態は安定キーで復元する）。</summary>
    private void RebuildTree(ProfilerReport report)
    {
        Tree.Items.Clear();
        if (report.Root is null) return;

        var rootItem = BuildTreeItem(report.Root, StableKeyRootParent);
        Tree.Items.Add(rootItem);
    }

    /// <summary>1 ノード分の TreeViewItem を再帰的に構築する。</summary>
    /// <param name="node">対象ノード。</param>
    /// <param name="parentKey">親ノードの安定キー（ルートでは空文字）。</param>
    private TreeViewItem BuildTreeItem(ProfilerNode node, string parentKey)
    {
        string key = parentKey + StableKeySeparator + node.Name;

        var item = new TreeViewItem
        {
            Tag        = key,
            IsExpanded = !_collapsedKeys.Contains(key),   // 既定は展開（折りたたみ集合に無ければ開く）
            Header     = BuildRowHeader(node),
        };

        foreach (var child in GetOrderedChildren(node))
            item.Items.Add(BuildTreeItem(child, key));

        return item;
    }

    /// <summary>現在のソート順に従って子ノードの列挙順を返す（実行順の場合は JSON 順のまま）。</summary>
    private IEnumerable<ProfilerNode> GetOrderedChildren(ProfilerNode node) => _sortMode switch
    {
        SortMode.TotalTime => node.Children.OrderByDescending(c => c.AvgMs),
        SortMode.SelfTime  => node.Children.OrderByDescending(c => c.SelfMs),
        _                  => node.Children,   // Execution: 実行順（JSON の並び）
    };

    /// <summary>ユーザーがノードを開いた: 折りたたみ記録から外す。</summary>
    private void OnItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: string key })
            _collapsedKeys.Remove(key);
    }

    /// <summary>ユーザーがノードを閉じた: 折りたたみとして記録する。</summary>
    private void OnItemCollapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: string key })
            _collapsedKeys.Add(key);
    }

    /// <summary>1 行分の表（Grid）を構築する。列: 名前(*) / 数値列 x5（固定幅）。</summary>
    private Grid BuildRowHeader(ProfilerNode node)
    {
        var grid = BuildRowGrid();
        var brush = PickRowBrush(node.AvgMs);

        SetCell(grid, 0, node.Name,                                              brush, TextAlignment.Left);
        SetCell(grid, 1, node.AvgMs.ToString("F2", CultureInfo.InvariantCulture),   brush, TextAlignment.Right);
        SetCell(grid, 2, node.MaxMs.ToString("F2", CultureInfo.InvariantCulture),   brush, TextAlignment.Right);
        SetCell(grid, 3, node.SelfMs.ToString("F2", CultureInfo.InvariantCulture),  brush, TextAlignment.Right);
        SetCell(grid, 4, node.Share.ToString("F2", CultureInfo.InvariantCulture),   brush, TextAlignment.Right);
        SetCell(grid, 5, node.Calls.ToString("F2", CultureInfo.InvariantCulture),   brush, TextAlignment.Right);
        return grid;
    }

    /// <summary>列ヘッダー・データ行の両方で共有する列構成（名前 * / 数値列 x5 固定幅）を持つ Grid を作る。</summary>
    private static Grid BuildRowGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NumericColumnWidth) });
        return grid;
    }

    /// <summary>Grid の指定列へ 1 つのテキストセルを配置する。</summary>
    private static void SetCell(Grid grid, int column, string text, Brush foreground, TextAlignment alignment)
    {
        var tb = new TextBlock
        {
            Text                = text,
            Foreground          = foreground,
            TextAlignment       = alignment,
            VerticalAlignment   = VerticalAlignment.Center,
            Padding             = new Thickness(4, 1, 4, 1),
            FontFamily          = new FontFamily("Consolas"),
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    /// <summary>平均時間のしきい値に応じて行の強調色を選ぶ（重いセクションほど赤・黄で目立たせる）。</summary>
    private static Brush PickRowBrush(double avgMs) => avgMs switch
    {
        > AvgCriticalThresholdMs => BrushCritical,
        > AvgWarnThresholdMs     => BrushWarn,
        _                        => BrushNormal,
    };
}
