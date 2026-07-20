// ============================================================
//  TerrainSettingsWindow.xaml.cs — 地形設定ウィンドウ
//
//  【責務】
//    地形（terrain）に関する設定をまとめて編集する独立ウィンドウ。
//    現在は「レイヤ」タブ（assets/terrain/layers.json の編集）のみが実装済みで、
//    「ブラシ」タブは将来拡張のための器として置いてある。
//
//  【なぜドッキングパネルではなく独立ウィンドウなのか】
//    レイヤ編集は「一覧 ＋ 多数のパラメータ」で横幅を要求する一方、
//    シーンビューを見ながら常時開いておく種類の UI ではない。
//    ドッキングすると編集中ずっとビューポートを圧迫するため、
//    非モーダルの別ウィンドウとして開き、閉じても terrain モードは維持する。
//
//  【ランタイムとの関係】
//    layers.json はランタイム（Rust）が読む正典データ。エディタは
//    「保存して適用」で JSON を書き、IPC `TERRAIN_RELOAD_LAYERS` を送るだけで、
//    レイヤテクスチャの再構築と全チャンク再メッシュはランタイムが行う。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Panels;

namespace SEEDEditor.Terrain;

/// <summary>
/// 地形設定ウィンドウ（非モーダル）。レイヤ定義（layers.json）の編集と、
/// ランタイムへの即時反映指示を行う。
/// </summary>
public partial class TerrainSettingsWindow : Window
{
    // ── レイアウト定数（マジックナンバー禁止）────────────────

    /// <summary>プロパティ行のラベル列幅（px）。</summary>
    private const double PropertyLabelWidth = 140;

    /// <summary>プロパティ行の縦マージン（px）。</summary>
    private const double PropertyRowMargin = 2;

    /// <summary>数値入力ボックスの幅（px）。</summary>
    private const double NumericBoxWidth = 90;

    /// <summary>カラープレビューのスウォッチ幅（px）。</summary>
    private const double ColorSwatchWidth = 60;

    /// <summary>斜度ウィンドウのプレビュー帯の高さ（px）。</summary>
    private const double SlopePreviewHeight = 18;

    /// <summary>斜度ウィンドウのプレビュー帯の分割数（描画セル数）。細かいほど滑らかだが重い。</summary>
    private const int SlopePreviewSegments = 90;

    /// <summary>数値の丸め表示に使う小数桁数指定（"0.###" = 末尾 0 を省いた最大 3 桁）。</summary>
    private const string DerivedValueFormat = "0.###";

    /// <summary>警告ボックスの内側余白（px）。</summary>
    private const double WarningBoxPadding = 8;

    /// <summary>警告ボックスの外側マージン（px）。</summary>
    private const double WarningBoxMargin = 6;

    /// <summary>「チャンクを追加」入力欄（min_x 等）の既定値。</summary>
    private const int AddChunksDefaultCoord = 0;

    // ── 依存（生成時に受け取る）──────────────────────────────

    /// <summary>assets ディレクトリの絶対パス（layers.json とテクスチャ相対パスの基準）。</summary>
    private readonly string _assetsRoot;

    /// <summary>ランタイムへ IPC 行を送るコールバック（ランタイム未起動時は何もしない実装が渡される）。</summary>
    private readonly Action<string> _sendToRuntime;

    /// <summary>保存が完了したときに呼ばれるコールバック（ツールバーのレイヤコンボ再構築に使う）。</summary>
    private readonly Action _onSaved;

    /// <summary>編集中のドキュメント（レイヤ）。</summary>
    private TerrainLayersDocument _doc;

    /// <summary>編集中のドキュメント（チャンク構成）。</summary>
    private TerrainChunkConfigDocument _chunkConfig;

    /// <summary>「チャンクを追加」入力欄の値（min_x/min_z/max_x/max_z・チャンク座標・両端含む範囲）。</summary>
    private int _addChunksMinX = AddChunksDefaultCoord;
    private int _addChunksMinZ = AddChunksDefaultCoord;
    private int _addChunksMaxX = AddChunksDefaultCoord;
    private int _addChunksMaxZ = AddChunksDefaultCoord;

    /// <summary>プロパティ欄を組み立て中かどうか（イベント再入で編集値を壊さないためのガード）。</summary>
    private bool _rebuilding;

    // ── 生成 ──────────────────────────────────────────────────

    /// <summary>
    /// 地形設定ウィンドウを生成し、layers.json を読み込む。
    /// </summary>
    /// <param name="assetsRoot">assets ディレクトリの絶対パス。</param>
    /// <param name="sendToRuntime">ランタイムへ IPC 行を送る処理。</param>
    /// <param name="onSaved">保存完了時に呼ばれる処理（レイヤ一覧の変更をエディタ UI へ伝える）。</param>
    public TerrainSettingsWindow(string assetsRoot, Action<string> sendToRuntime, Action onSaved)
    {
        InitializeComponent();
        _assetsRoot    = assetsRoot;
        _sendToRuntime = sendToRuntime;
        _onSaved       = onSaved;

        _doc = TerrainLayersDocument.Load(_assetsRoot);
        RefreshLayerList(selectIndex: 0);

        _chunkConfig = TerrainChunkConfigDocument.Load(_assetsRoot);
        RebuildTerrainConfigPanel();

        // 散布タブ（props.json）。レイヤ一覧を参照するため、_doc の読み込み後に初期化する
        // （レイヤ条件のレイヤ名候補を layers.json から作るため）。
        InitScatterTab();

        if (_doc.WasMissingOrInvalid)
        {
            SetStatus($"{TerrainLayersDocument.ResolvePath(_assetsRoot)} が読めなかったため、新規レイヤ 1 枚で開始します", ok: false);
        }
        else
        {
            SetStatus($"{_doc.Layers.Count} レイヤを読み込みました", ok: true);
        }
    }

    // ── 地形タブ（チャンク構成）────────────────────────────────

    /// <summary>
    /// 「地形」タブの中身を組み立て直す。チャンク構成（chunks_x/chunks_z/chunk_cells/voxel_size）の
    /// 編集欄、派生値（実寸・フットプリント・総チャンク数）の読み取り専用表示、
    /// 非互換化に関する警告、そして「チャンクを追加」セクションで構成する。
    /// </summary>
    private void RebuildTerrainConfigPanel()
    {
        PanelTerrainConfig.Children.Clear();

        // ── 警告（常時表示。チャンク分割数・ボクセルサイズの変更は初期化/ハイトマップ読込でのみ反映される）──
        var warningText = new TextBlock
        {
            Text = "チャンク分割数・ボクセルサイズの変更は、「地形を初期化」または「ハイトマップ読込」で"
                 + "地形を作り直したときにのみ反映されます。既存の地形（保存済み .tvox ファイル）とは"
                 + "ボクセル配置が非互換になるため、変更後は必ず地形を初期化し直してください。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
            FontWeight   = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        };
        var warningBox = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x4A, 0x36, 0x08)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(WarningBoxPadding),
            Margin          = new Thickness(0, 0, 0, WarningBoxMargin),
            Child           = warningText,
        };
        PanelTerrainConfig.Children.Add(warningBox);

        // ── チャンク構成の編集欄 ──
        PanelTerrainConfig.Children.Add(MakeSectionHeader("チャンク構成"));

        // 派生値（実寸・フットプリント・総チャンク数）の表示欄。編集欄の onChanged から再計算する。
        var txtChunkEdge   = MakeReadonlyValueText();
        var txtFootprint   = MakeReadonlyValueText();
        var txtTotalChunks = MakeReadonlyValueText();
        void RecomputeDerived()
        {
            var ci = CultureInfo.InvariantCulture;
            double edge = _chunkConfig.VoxelSize * _chunkConfig.ChunkCells;
            txtChunkEdge.Text = $"{edge.ToString(DerivedValueFormat, ci)} m";

            double footprintX = edge * _chunkConfig.ChunksX;
            double footprintZ = edge * _chunkConfig.ChunksZ;
            txtFootprint.Text = $"{footprintX.ToString(DerivedValueFormat, ci)} m × {footprintZ.ToString(DerivedValueFormat, ci)} m";

            txtTotalChunks.Text = $"{_chunkConfig.ChunksX * _chunkConfig.ChunksZ} 個";
        }

        PanelTerrainConfig.Children.Add(MakeIntRow("チャンク数 X", _chunkConfig.ChunksX,
            TerrainChunkConfigDocument.ChunksAxisMin, TerrainChunkConfigDocument.ChunksAxisMax,
            v => { _chunkConfig.ChunksX = v; RecomputeDerived(); }));
        PanelTerrainConfig.Children.Add(MakeIntRow("チャンク数 Z", _chunkConfig.ChunksZ,
            TerrainChunkConfigDocument.ChunksAxisMin, TerrainChunkConfigDocument.ChunksAxisMax,
            v => { _chunkConfig.ChunksZ = v; RecomputeDerived(); }));
        PanelTerrainConfig.Children.Add(MakeIntRow("チャンク分割数（chunk_cells）", _chunkConfig.ChunkCells,
            TerrainChunkConfigDocument.ChunkCellsMin, TerrainChunkConfigDocument.ChunkCellsMax,
            v => { _chunkConfig.ChunkCells = v; RecomputeDerived(); }));
        PanelTerrainConfig.Children.Add(MakeNumericRow("ボクセルサイズ（m）", _chunkConfig.VoxelSize,
            TerrainChunkConfigDocument.VoxelSizeMin, TerrainChunkConfigDocument.VoxelSizeMax,
            v => { _chunkConfig.VoxelSize = v; RecomputeDerived(); }));

        PanelTerrainConfig.Children.Add(MakeSectionHeader("計算値（読み取り専用）"));
        PanelTerrainConfig.Children.Add(MakeRow("チャンク 1 辺の実寸", txtChunkEdge));
        PanelTerrainConfig.Children.Add(MakeRow("地形全体のフットプリント", txtFootprint));
        PanelTerrainConfig.Children.Add(MakeRow("総チャンク数", txtTotalChunks));
        RecomputeDerived();

        // ── チャンクを追加 ──
        PanelTerrainConfig.Children.Add(MakeSectionHeader("チャンクを追加"));
        PanelTerrainConfig.Children.Add(MakeHint(
            "指定したチャンク座標範囲（両端含む）へチャンクを追加する。既存チャンクは温存される "
            + "（地形の作り直しにはならない・保存済みの編集内容はそのまま残る）。"));

        PanelTerrainConfig.Children.Add(MakeIntRow("min_x", _addChunksMinX, null, null, v => _addChunksMinX = v));
        PanelTerrainConfig.Children.Add(MakeIntRow("min_z", _addChunksMinZ, null, null, v => _addChunksMinZ = v));
        PanelTerrainConfig.Children.Add(MakeIntRow("max_x", _addChunksMaxX, null, null, v => _addChunksMaxX = v));
        PanelTerrainConfig.Children.Add(MakeIntRow("max_z", _addChunksMaxZ, null, null, v => _addChunksMaxZ = v));

        var btnAddChunks = new Button
        {
            Style               = (Style)FindResource("ToolButtonStyle"),
            Content             = "追加",
            Padding             = new Thickness(14, 4, 14, 4),
            Margin              = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip             = "TERRAIN_ADD_CHUNKS を送信し、指定範囲のチャンクを追加する（既存チャンクは温存される）",
        };
        btnAddChunks.Click += (_, _) =>
        {
            _sendToRuntime($"TERRAIN_ADD_CHUNKS:{_addChunksMinX},{_addChunksMinZ},{_addChunksMaxX},{_addChunksMaxZ}");
            SetStatus("チャンク追加を送信しました", ok: true);
        };
        PanelTerrainConfig.Children.Add(btnAddChunks);
    }

    /// <summary>読み取り専用の派生値表示用 TextBlock を作る（エディタ配色に合わせる）。</summary>
    private static TextBlock MakeReadonlyValueText() => new()
    {
        Text              = "",
        Foreground        = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
        FontFamily        = new FontFamily("Consolas"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ── レイヤ一覧 ────────────────────────────────────────────

    /// <summary>
    /// レイヤ一覧 ListBox を現在のドキュメントから作り直す。
    /// 表示は「番号: 名前」でツールバーのレイヤコンボと表記を揃える。
    /// </summary>
    /// <param name="selectIndex">再構築後に選択する行（範囲外はクランプ）。</param>
    private void RefreshLayerList(int selectIndex)
    {
        _rebuilding = true;
        LstLayers.Items.Clear();
        for (int i = 0; i < _doc.Layers.Count; i++)
            LstLayers.Items.Add(FormatLayerListEntry(i, _doc.Layers[i].Name));
        _rebuilding = false;

        LstLayers.SelectedIndex = _doc.Layers.Count == 0
            ? -1
            : Math.Clamp(selectIndex, 0, _doc.Layers.Count - 1);
        RebuildPropertyPanel();
        UpdateButtonStates();
    }

    /// <summary>レイヤ一覧・レイヤ選択コンボで共有する表示名フォーマット。</summary>
    public static string FormatLayerListEntry(int index, string name)
        => $"{index}: {(string.IsNullOrWhiteSpace(name) ? "(無名)" : name)}";

    /// <summary>現在選択中のレイヤ（未選択なら null）。</summary>
    private TerrainLayerEdit? SelectedLayer
    {
        get
        {
            int i = LstLayers.SelectedIndex;
            return i >= 0 && i < _doc.Layers.Count ? _doc.Layers[i] : null;
        }
    }

    /// <summary>レイヤ数・選択状態に応じて操作ボタンの有効／無効を更新する。</summary>
    private void UpdateButtonStates()
    {
        int i     = LstLayers.SelectedIndex;
        int count = _doc.Layers.Count;
        bool has  = i >= 0;

        // 上限に達したら追加・複製を止める（超過分はランタイム読み込み時に切り捨てられるため）。
        bool canGrow = count < TerrainLayerDefaults.MaxLayers;
        BtnAddLayer.IsEnabled       = canGrow;
        BtnDuplicateLayer.IsEnabled = has && canGrow;
        // 最低 1 枚は残す（レイヤ 0 枚の layers.json はランタイムが既定セットへフォールバックしてしまう）。
        BtnRemoveLayer.IsEnabled    = has && count > 1;
        BtnMoveLayerUp.IsEnabled    = has && i > 0;
        BtnMoveLayerDown.IsEnabled  = has && i >= 0 && i < count - 1;
    }

    private void OnLayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding) return;
        RebuildPropertyPanel();
        UpdateButtonStates();
    }

    // ── レイヤ操作（追加／複製／削除／並べ替え）──────────────

    private void OnAddLayer(object sender, RoutedEventArgs e)
    {
        if (_doc.Layers.Count >= TerrainLayerDefaults.MaxLayers) return;
        _doc.Layers.Add(TerrainLayerEdit.CreateNew(MakeUniqueLayerName("new_layer")));
        RefreshLayerList(selectIndex: _doc.Layers.Count - 1);
        SetStatus("レイヤを追加しました（未保存）", ok: true);
    }

    private void OnDuplicateLayer(object sender, RoutedEventArgs e)
    {
        int i = LstLayers.SelectedIndex;
        if (i < 0 || _doc.Layers.Count >= TerrainLayerDefaults.MaxLayers) return;
        // 複製は選択行の直後へ入れる（優先度評価の並びを保ったまま派生を作れるようにする）。
        _doc.Layers.Insert(i + 1, _doc.Layers[i].Duplicate(MakeUniqueLayerName(_doc.Layers[i].Name)));
        RefreshLayerList(selectIndex: i + 1);
        SetStatus("レイヤを複製しました（未保存）", ok: true);
    }

    private void OnRemoveLayer(object sender, RoutedEventArgs e)
    {
        int i = LstLayers.SelectedIndex;
        if (i < 0 || _doc.Layers.Count <= 1) return;

        // レイヤ削除は以降のレイヤ番号をずらすため、既存の手ペイント結果の対応が崩れる。
        // 破壊的な操作なので明示的に確認する。
        var r = MessageBox.Show(this,
            $"レイヤ「{_doc.Layers[i].Name}」を削除します。\n"
            + "以降のレイヤ番号が 1 つずつ繰り上がるため、"
            + "この地形に既に手で塗ったレイヤ重みの割り当てがずれます。続行しますか？",
            "レイヤの削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;

        _doc.Layers.RemoveAt(i);
        RefreshLayerList(selectIndex: i);
        SetStatus("レイヤを削除しました（未保存）", ok: true);
    }

    private void OnMoveLayerUp(object sender, RoutedEventArgs e)   => MoveSelectedLayer(-1);
    private void OnMoveLayerDown(object sender, RoutedEventArgs e) => MoveSelectedLayer(+1);

    /// <summary>選択レイヤを delta 行ぶん移動する（レイヤ番号が入れ替わる）。</summary>
    private void MoveSelectedLayer(int delta)
    {
        int i = LstLayers.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _doc.Layers.Count) return;

        (_doc.Layers[i], _doc.Layers[j]) = (_doc.Layers[j], _doc.Layers[i]);
        RefreshLayerList(selectIndex: j);
        SetStatus("レイヤ順を入れ替えました（未保存）", ok: true);
    }

    /// <summary>既存レイヤ名と衝突しない名前を作る（末尾に連番を付ける）。</summary>
    private string MakeUniqueLayerName(string baseName)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in _doc.Layers) used.Add(l.Name);
        if (!used.Contains(baseName)) return baseName;
        for (int n = 1; ; n++)
        {
            var candidate = $"{baseName}_{n}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    // ── プロパティ欄の組み立て ────────────────────────────────

    /// <summary>
    /// 選択レイヤのプロパティ UI を組み立て直す。
    /// XAML ではなくコードで組むことで、フィールド追加時に行生成ヘルパを 1 行呼ぶだけで済む。
    /// </summary>
    private void RebuildPropertyPanel()
    {
        PanelLayerProperties.Children.Clear();
        var layer = SelectedLayer;
        if (layer is null) return;

        // ── 基本情報 ──
        PanelLayerProperties.Children.Add(MakeSectionHeader("基本"));
        PanelLayerProperties.Children.Add(MakeTextRow("名前", layer.Name, v =>
        {
            layer.Name = v;
            // 一覧の表示名を即座に追従させる（選択は維持する）。
            int idx = LstLayers.SelectedIndex;
            if (idx >= 0) LstLayers.Items[idx] = FormatLayerListEntry(idx, v);
        }));
        PanelLayerProperties.Children.Add(MakeColorRow("ベースカラー", layer));
        PanelLayerProperties.Children.Add(MakeNumericRow("ラフネス", layer.Roughness,
            TerrainLayerDefaults.UnitRangeMin, TerrainLayerDefaults.UnitRangeMax, v => layer.Roughness = v));
        PanelLayerProperties.Children.Add(MakeNumericRow("メタリック", layer.Metallic,
            TerrainLayerDefaults.UnitRangeMin, TerrainLayerDefaults.UnitRangeMax, v => layer.Metallic = v));
        PanelLayerProperties.Children.Add(MakeNumericRow("UV スケール", layer.UvScale,
            null, null, v => layer.UvScale = v));

        // ── テクスチャ（アセット相対パスで保存する）──
        PanelLayerProperties.Children.Add(MakeSectionHeader("テクスチャ"));
        PanelLayerProperties.Children.Add(MakeTextureRow("ベースカラー", layer.BaseColorTexture, v => layer.BaseColorTexture = v));
        PanelLayerProperties.Children.Add(MakeTextureRow("法線",         layer.NormalTexture,    v => layer.NormalTexture    = v));
        PanelLayerProperties.Children.Add(MakeTextureRow("ラフネス",     layer.RoughnessTexture, v => layer.RoughnessTexture = v));

        // ── タイリング解消 ──
        PanelLayerProperties.Children.Add(MakeSectionHeader("タイリング解消"));
        PanelLayerProperties.Children.Add(MakeComboRow("モード", TerrainLayerDefaults.DetileModes,
            layer.Detile, v => layer.Detile = v));
        PanelLayerProperties.Children.Add(MakeNumericRow("強度", layer.DetileStrength,
            TerrainLayerDefaults.UnitRangeMin, TerrainLayerDefaults.UnitRangeMax, v => layer.DetileStrength = v));

        // ── 自動ペイント条件（斜度・高度ルール）──
        PanelLayerProperties.Children.Add(MakeSectionHeader("自動ペイント条件"));
        PanelLayerProperties.Children.Add(MakeHint(
            "斜度ウィンドウと高度ウィンドウの積 × 優先度が、そのレイヤの自動下地としての重みになる。"
            + "フェード幅は境界をぼかす幅（0 でハードエッジ）。"));

        // 斜度ウィンドウのプレビュー帯。値を変えたら描き直すため、生成器をローカル関数で持つ。
        var slopePreview = new Border
        {
            Height          = SlopePreviewHeight,
            Margin          = new Thickness(0, 4, 0, 6),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            BorderThickness = new Thickness(1),
        };
        void RedrawSlopePreview() => slopePreview.Child = BuildSlopeWindowPreview(layer);
        RedrawSlopePreview();

        PanelLayerProperties.Children.Add(MakeNumericRow("斜度 min（度）", layer.SlopeMinDeg,
            TerrainLayerDefaults.SlopeRangeMin, TerrainLayerDefaults.SlopeRangeMax,
            v => { layer.SlopeMinDeg = v; RedrawSlopePreview(); }));
        PanelLayerProperties.Children.Add(MakeNumericRow("斜度 max（度）", layer.SlopeMaxDeg,
            TerrainLayerDefaults.SlopeRangeMin, TerrainLayerDefaults.SlopeRangeMax,
            v => { layer.SlopeMaxDeg = v; RedrawSlopePreview(); }));
        PanelLayerProperties.Children.Add(MakeNumericRow("斜度フェード（度）", layer.SlopeFadeDeg,
            TerrainLayerDefaults.SlopeRangeMin, TerrainLayerDefaults.SlopeRangeMax,
            v => { layer.SlopeFadeDeg = v; RedrawSlopePreview(); }));
        PanelLayerProperties.Children.Add(MakeHint("斜度 0°=平地 〜 90°=垂直の崖。下は現在の斜度ウィンドウのプレビュー。"));
        PanelLayerProperties.Children.Add(slopePreview);

        PanelLayerProperties.Children.Add(MakeNumericRow("高度 min（m）", layer.HeightMin,
            null, null, v => layer.HeightMin = v));
        PanelLayerProperties.Children.Add(MakeNumericRow("高度 max（m）", layer.HeightMax,
            null, null, v => layer.HeightMax = v));
        PanelLayerProperties.Children.Add(MakeNumericRow("高度フェード（m）", layer.HeightFade,
            null, null, v => layer.HeightFade = v));
        PanelLayerProperties.Children.Add(MakeNumericRow("優先度", layer.Priority,
            null, null, v => layer.Priority = v));
    }

    /// <summary>
    /// 斜度ウィンドウ（min/max/fade）の重みを 0〜90 度で可視化した帯を作る。
    /// ランタイム layers.rs の window() と同じ台形＋smoothstep を再現する
    /// （実描画の正典はランタイム側。ここはあくまで入力補助のプレビュー）。
    /// </summary>
    private static UIElement BuildSlopeWindowPreview(TerrainLayerEdit layer)
    {
        var grid = new Grid();
        // 明るさで重み（0=黒〜1=レイヤのベースカラー）を表す帯を等幅セルで敷き詰める。
        for (int i = 0; i < SlopePreviewSegments; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            double deg = TerrainLayerDefaults.SlopeRangeMin
                + (TerrainLayerDefaults.SlopeRangeMax - TerrainLayerDefaults.SlopeRangeMin)
                  * (i + 0.5) / SlopePreviewSegments;
            double w = EvaluateWindow(deg, layer.SlopeMinDeg, layer.SlopeMaxDeg, layer.SlopeFadeDeg);

            // レイヤのベースカラー（リニア）を重みで減光して塗る。sRGB 変換は行わず、
            // あくまで「どの斜度で乗るか」の相対表示に徹する。
            var cell = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(
                    ToByte(layer.BaseColor[0] * w),
                    ToByte(layer.BaseColor[1] * w),
                    ToByte(layer.BaseColor[2] * w))),
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    /// <summary>0〜1 の値を 0〜255 のバイトへクランプ変換する。</summary>
    private static byte ToByte(double v)
        => (byte)Math.Clamp(v * byte.MaxValue, 0, byte.MaxValue);

    /// <summary>
    /// ランタイム layers.rs の window() と同じ台形ウィンドウ関数。
    /// [min, max] の内側で 1、外側で 0 になり、両端を fade 幅で smoothstep 補間する。
    /// </summary>
    internal static double EvaluateWindow(double v, double min, double max, double fade)
    {
        if (max < min) return 0.0;
        if (fade <= 0.0) return (v >= min && v <= max) ? 1.0 : 0.0;
        double rise = Smoothstep((v - (min - fade)) / fade);
        double fall = 1.0 - Smoothstep((v - max) / fade);
        return Math.Clamp(rise * fall, 0.0, 1.0);
    }

    /// <summary>smoothstep(3t²-2t³)。t は [0,1] にクランプして評価する。</summary>
    private static double Smoothstep(double t)
    {
        double x = Math.Clamp(t, 0.0, 1.0);
        return x * x * (3.0 - 2.0 * x);
    }

    // ── 行生成ヘルパ（フィールド追加を 1 行で済ませるための共通化）──

    /// <summary>セクション見出しを作る。</summary>
    private static TextBlock MakeSectionHeader(string text) => new()
    {
        Text       = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xB4, 0xE0)),
        FontWeight = FontWeights.Bold,
        Margin     = new Thickness(0, 10, 0, 4),
    };

    /// <summary>補足説明テキストを作る。</summary>
    private static TextBlock MakeHint(string text) => new()
    {
        Text         = text,
        Foreground   = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
        TextWrapping = TextWrapping.Wrap,
        Margin       = new Thickness(0, 2, 0, 2),
    };

    /// <summary>「ラベル ＋ 任意コントロール」の 1 行を作る共通レイアウト。</summary>
    private static Grid MakeRow(string label, UIElement content)
    {
        var grid = new Grid { Margin = new Thickness(0, PropertyRowMargin, 0, PropertyRowMargin) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PropertyLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);     grid.Children.Add(lbl);
        Grid.SetColumn(content, 1); grid.Children.Add(content);
        return grid;
    }

    /// <summary>文字列入力の行を作る（入力のたびに onChanged が呼ばれる）。</summary>
    private static Grid MakeTextRow(string label, string value, Action<string> onChanged)
    {
        var box = MakeStyledTextBox(value);
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        box.TextChanged += (_, _) => onChanged(box.Text);
        return MakeRow(label, box);
    }

    /// <summary>
    /// 数値入力の行を作る。パースできない入力は無視し（直前の値を保つ）、
    /// min/max が指定されていればクランプする。
    /// </summary>
    private static Grid MakeNumericRow(string label, double value, double? min, double? max, Action<double> onChanged)
    {
        var box = MakeStyledTextBox(value.ToString("G", CultureInfo.InvariantCulture));
        box.Width               = NumericBoxWidth;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.TextChanged += (_, _) =>
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return; // 入力途中（"-" や "" 等）は確定させない
            if (min.HasValue) v = Math.Max(v, min.Value);
            if (max.HasValue) v = Math.Min(v, max.Value);
            onChanged(v);
        };
        return MakeRow(label, box);
    }

    /// <summary>
    /// 整数入力の行を作る。パースできない入力は無視し（直前の値を保つ）、
    /// min/max が指定されていればクランプする。負数（"-" 始まり）の入力途中も許容する。
    /// </summary>
    private static Grid MakeIntRow(string label, int value, int? min, int? max, Action<int> onChanged)
    {
        var box = MakeStyledTextBox(value.ToString(CultureInfo.InvariantCulture));
        box.Width               = NumericBoxWidth;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.TextChanged += (_, _) =>
        {
            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return; // 入力途中（"-" や "" 等）は確定させない
            if (min.HasValue) v = Math.Max(v, min.Value);
            if (max.HasValue) v = Math.Min(v, max.Value);
            onChanged(v);
        };
        return MakeRow(label, box);
    }

    /// <summary>選択肢コンボの行を作る（detile モード等）。</summary>
    private static Grid MakeComboRow(string label, string[] options, string current, Action<string> onChanged)
    {
        var cmb = new ComboBox
        {
            Width               = NumericBoxWidth * 2,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var o in options) cmb.Items.Add(o);
        cmb.SelectedIndex = Math.Max(0, Array.IndexOf(options, current));
        cmb.SelectionChanged += (_, _) =>
        {
            if (cmb.SelectedItem is string s) onChanged(s);
        };
        return MakeRow(label, cmb);
    }

    /// <summary>
    /// ベースカラー（リニア RGB）の行を作る。スウォッチのクリックでカラーピッカーを開く。
    /// アルファは地形レイヤに存在しないため、ピッカーへは不透明を渡して結果の A は捨てる。
    /// </summary>
    private Grid MakeColorRow(string label, TerrainLayerEdit layer)
    {
        var swatch = new Border
        {
            Width               = ColorSwatchWidth,
            Height              = SlopePreviewHeight,
            BorderBrush         = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness     = new Thickness(1),
            CornerRadius        = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor              = System.Windows.Input.Cursors.Hand,
            ToolTip             = "クリックしてカラーピッカーを開く（リニア RGB）",
        };
        void Paint() => swatch.Background = new SolidColorBrush(Color.FromRgb(
            ToByte(layer.BaseColor[0]), ToByte(layer.BaseColor[1]), ToByte(layer.BaseColor[2])));
        Paint();

        swatch.MouseLeftButtonUp += (_, _) =>
        {
            const float OpaqueAlpha = 1.0f;
            var picked = ColorPickerWindow.ShowDialog(this,
                (float)layer.BaseColor[0], (float)layer.BaseColor[1], (float)layer.BaseColor[2], OpaqueAlpha);
            if (picked is null) return;
            layer.BaseColor = new double[] { picked.Value.r, picked.Value.g, picked.Value.b };
            Paint();
            // 斜度プレビュー帯はベースカラーで塗るため、色変更を反映させる。
            RebuildPropertyPanel();
        };
        return MakeRow(label, swatch);
    }

    /// <summary>
    /// テクスチャ参照の行を作る。参照ボタンとドラッグ＆ドロップの双方を
    /// FileRefBuilder（インスペクタと共通）で用意し、選ばれた絶対パスを
    /// assets ルート基準の相対パスへ変換して保存する。
    /// </summary>
    private UIElement MakeTextureRow(string label, string? current, Action<string?> onChanged)
    {
        // 対応拡張子はランタイムの image クレートが読める代表的なものに絞る。
        string[] acceptedExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp" };

        var row = FileRefBuilder.Build(
            label,
            current,
            acceptedExtensions,
            browseFn: () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title            = "テクスチャを選択",
                    Filter           = "画像ファイル|*.png;*.jpg;*.jpeg;*.tga;*.bmp",
                    InitialDirectory = Directory.Exists(_assetsRoot) ? _assetsRoot : Environment.CurrentDirectory,
                };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            },
            onPathSet: path =>
            {
                onChanged(ToAssetRelativePath(path));
                // 表示（ファイル名・ツールチップ）を更新するため行ごと作り直す。
                RebuildPropertyPanel();
            });

        // FileRefBuilder は「解除」手段を持たないため、右クリックでクリアできるようにする。
        if (row is FrameworkElement fe)
        {
            fe.ToolTip = (fe.ToolTip as string) ?? "右クリックでテクスチャ指定を解除";
            fe.MouseRightButtonUp += (_, e) =>
            {
                onChanged(null);
                RebuildPropertyPanel();
                e.Handled = true;
            };
        }
        return row;
    }

    /// <summary>
    /// 絶対パスを assets ルート基準の相対パスへ変換する（区切りは JSON 慣例に合わせて '/'）。
    /// assets の外を指すファイルはそのまま絶対パスで保持する（ランタイム側の asset_fs が
    /// 絶対パスも解決できるため。ただし配布時に壊れるので UI 上は assets 内を推奨する）。
    /// </summary>
    private string ToAssetRelativePath(string absolutePath)
    {
        try
        {
            var full = Path.GetFullPath(absolutePath);
            var root = Path.GetFullPath(_assetsRoot);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception)
        {
            // 不正なパス文字などは変換を諦めて元の文字列を使う。
        }
        return absolutePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>エディタ配色に合わせた TextBox を作る。</summary>
    private static TextBox MakeStyledTextBox(string text) => new()
    {
        Text              = text,
        Background        = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
        Foreground        = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
        BorderBrush       = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
        BorderThickness   = new Thickness(1),
        Padding           = new Thickness(3, 1, 3, 1),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ── 保存・適用 ────────────────────────────────────────────

    /// <summary>
    /// 「保存して適用」: layers.json を書き出し、ランタイムへ再読込を指示する。
    /// ウィンドウは開いたままにする（続けて調整できるようにするため）。
    /// </summary>
    private void OnApply(object sender, RoutedEventArgs e)
    {
        try
        {
            _doc.Save(_assetsRoot);
            // チャンク構成は保存するだけで、この時点では TERRAIN_INIT は送らない
            // （破壊的な操作のため、反映は「地形を初期化」／「ハイトマップ読込」ボタン経由に限定する）。
            _chunkConfig.Save(_assetsRoot);
            // 散布プロップ定義（props.json）。ランタイムはこのファイルを
            // 再散布（TERRAIN_SCATTER_RULES）や散布ブラシの実行時に参照するため、
            // レイヤと同じタイミングで書き出しておく。
            _props.Save(_assetsRoot);
        }
        catch (Exception ex)
        {
            SetStatus($"保存に失敗しました: {ex.Message}", ok: false);
            return;
        }

        // ランタイムへ即時反映を指示する（レイヤテクスチャ再構築＋全チャンク再メッシュ）。
        _sendToRuntime("TERRAIN_RELOAD_LAYERS");
        // ツールバーのレイヤ選択コンボ・プロップ選択コンボなど、エディタ側 UI へ構成変更を伝える。
        _onSaved();
        SetStatus(
            $"保存しました（{_doc.Layers.Count} レイヤ／{_props.Props.Count} プロップ／チャンク構成）。"
            + "レイヤはシーンビューへ反映済み。散布定義の反映は「ルールで再散布」で行います", ok: true);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    // ── ステータス表示 ────────────────────────────────────────

    private void SetStatus(string text, bool ok)
    {
        TxtStatus.Text = text;
        TxtStatus.Foreground = new SolidColorBrush(
            ok ? Color.FromRgb(0x88, 0xCC, 0x88) : Color.FromRgb(0xE0, 0x6C, 0x6C));
    }
}
