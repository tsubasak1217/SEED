// ============================================================
//  TerrainSettingsWindow.Scatter.cs — 地形設定ウィンドウ「散布」タブ（Terrain T3）
//
//  【責務】
//    props.json（地形プロップ定義）の編集 UI と、ルールによる再散布指示。
//    TerrainSettingsWindow の partial として実装し、レイヤタブと
//    行生成ヘルパ（MakeRow / MakeNumericRow など）を共有する。
//
//  【なぜ別ファイルなのか】
//    レイヤ編集（layers.json）と散布編集（props.json）は扱うデータも
//    ランタイム側の正典ファイルも別であり、片方の変更が他方の差分に
//    混ざるのを避けたい。ウィンドウ本体（xaml.cs）は共通の器と
//    レイヤタブに専念させ、散布タブの状態と処理はここへ閉じる。
//
//  【条件付き表示の方針】
//    kind（草／モデル）によって意味を持たないパラメータは
//    「無効化」ではなく「非表示」にする（プロジェクトのインスペクタ方針）。
//    ただし値そのものは props.json に保持され続けるため、kind を戻せば
//    以前の設定がそのまま復帰する（Rust 側も全フィールドを常に持つ設計）。
//
//  【ランタイムとの関係】
//    - 保存: props.json を書き出す（ウィンドウ下部の「保存して適用」）。
//    - 再散布: TERRAIN_SCATTER_RULES:{prop_id},{seed} を送る。
//      prop_id が空文字なら全プロップが対象。seed は u64。
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

public partial class TerrainSettingsWindow
{
    // ── 定数（マジックナンバー禁止）────────────────────────────

    /// <summary>プロップ ID の自動採番に使う基底名（「追加」で作る新規プロップ）。</summary>
    private const string NewPropIdBase = "new_prop";

    /// <summary>新規プロップの既定表示名。</summary>
    private const string NewPropDefaultName = "新しいプロップ";

    /// <summary>レイヤ条件を追加したときの既定レイヤ名（layers.json に該当が無ければ空扱い）。</summary>
    private const string NewLayerConditionFallbackName = "";

    /// <summary>レイヤ条件行の「削除」ボタン幅（px）。</summary>
    private const double ConditionRemoveButtonWidth = 28;

    /// <summary>レイヤ条件のレイヤ名コンボ幅（px）。</summary>
    private const double ConditionLayerComboWidth = 140;

    /// <summary>レイヤ条件の重み入力ボックス幅（px）。</summary>
    private const double ConditionWeightBoxWidth = 60;

    /// <summary>シード入力欄が空／u64 として解釈できないときに使う既定シード。</summary>
    private const ulong SeedFallback = 0;

    /// <summary>「対象」コンボで全プロップを指すときの Tag 値。</summary>
    private const string ScatterTargetAllTag = "all";

    /// <summary>散布密度の UI 上限（1 m² あたりの候補点数）。これ以上はチャンクあたりの点数が現実的でない。</summary>
    private const double ScatterDensityUiMax = 64.0;

    /// <summary>スケール倍率の UI 上限。</summary>
    private const double ScaleUiMax = 100.0;

    /// <summary>草の寸法（幅・高さ）の UI 上限（メートル）。</summary>
    private const double GrassSizeUiMax = 100.0;

    /// <summary>風パラメータ（速度・周波数）の UI 上限。</summary>
    private const double WindUiMax = 100.0;

    // ── 状態 ──────────────────────────────────────────────────

    /// <summary>編集中のドキュメント（プロップ定義）。</summary>
    private TerrainPropsDocument _props = null!;

    /// <summary>プロップ一覧を組み立て中かどうか（選択変更イベントの再入ガード）。</summary>
    private bool _rebuildingProps;

    // ── 初期化 ────────────────────────────────────────────────

    /// <summary>
    /// 散布タブを初期化する（コンストラクタから呼ぶ）。
    /// props.json を読み込み、一覧と初期シードを用意する。
    /// </summary>
    private void InitScatterTab()
    {
        _props = TerrainPropsDocument.Load(_assetsRoot);
        RefreshPropList(selectIndex: 0);
        // 起動直後にシード欄が 0 固定だと「ランダム」を押し忘れて同じ配置になりやすいため、
        // 最初から 1 つ振っておく（固定したい場合は手で書き換えられる）。
        TxtScatterSeed.Text = GenerateSeed().ToString(CultureInfo.InvariantCulture);
    }

    // ── プロップ一覧 ──────────────────────────────────────────

    /// <summary>
    /// プロップ一覧 ListBox を現在のドキュメントから作り直す。
    /// </summary>
    /// <param name="selectIndex">再構築後に選択する行（範囲外はクランプ）。</param>
    private void RefreshPropList(int selectIndex)
    {
        _rebuildingProps = true;
        LstProps.Items.Clear();
        for (int i = 0; i < _props.Props.Count; i++)
            LstProps.Items.Add(FormatPropListEntry(i, _props.Props[i].Name, _props.Props[i].Id));
        _rebuildingProps = false;

        LstProps.SelectedIndex = _props.Props.Count == 0
            ? -1
            : Math.Clamp(selectIndex, 0, _props.Props.Count - 1);
        RebuildPropPropertyPanel();
        UpdatePropButtonStates();
    }

    /// <summary>プロップ一覧の表示名フォーマット（一覧用。番号付き）。</summary>
    private static string FormatPropListEntry(int index, string name, string id)
        => $"{index}: {FormatPropComboEntry(name, id)}";

    /// <summary>
    /// ツールバーのプロップ選択コンボと共有する表示名フォーマット。
    /// ID は IPC の識別子そのものなので、表示名と併記して取り違えを防ぐ。
    /// </summary>
    public static string FormatPropComboEntry(string name, string id)
        => string.IsNullOrWhiteSpace(name) ? (string.IsNullOrWhiteSpace(id) ? "(無名)" : id) : $"{name} ({id})";

    /// <summary>現在選択中のプロップ（未選択なら null）。</summary>
    private TerrainPropEdit? SelectedProp
    {
        get
        {
            int i = LstProps.SelectedIndex;
            return i >= 0 && i < _props.Props.Count ? _props.Props[i] : null;
        }
    }

    /// <summary>プロップ数・選択状態に応じて操作ボタンの有効／無効を更新する。</summary>
    private void UpdatePropButtonStates()
    {
        int i     = LstProps.SelectedIndex;
        int count = _props.Props.Count;
        bool has  = i >= 0;

        // 上限に達したら追加・複製を止める（超過分はランタイム読み込み時に切り捨てられるため）。
        bool canGrow = count < TerrainPropDefaults.MaxProps;
        BtnAddProp.IsEnabled       = canGrow;
        BtnDuplicateProp.IsEnabled = has && canGrow;
        // 最低 1 種は残す（props が空の props.json はランタイムが既定セットへフォールバックしてしまう）。
        BtnRemoveProp.IsEnabled    = has && count > 1;
        BtnMovePropUp.IsEnabled    = has && i > 0;
        BtnMovePropDown.IsEnabled  = has && i >= 0 && i < count - 1;
    }

    private void OnPropSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingProps) return;
        RebuildPropPropertyPanel();
        UpdatePropButtonStates();
    }

    // ── プロップ操作（追加／複製／削除／並べ替え）──────────────

    private void OnAddProp(object sender, RoutedEventArgs e)
    {
        if (_props.Props.Count >= TerrainPropDefaults.MaxProps) return;
        _props.Props.Add(TerrainPropEdit.CreateNew(MakeUniquePropId(NewPropIdBase), NewPropDefaultName));
        RefreshPropList(selectIndex: _props.Props.Count - 1);
        SetStatus("プロップを追加しました（未保存）", ok: true);
    }

    private void OnDuplicateProp(object sender, RoutedEventArgs e)
    {
        int i = LstProps.SelectedIndex;
        if (i < 0 || _props.Props.Count >= TerrainPropDefaults.MaxProps) return;
        var src = _props.Props[i];
        _props.Props.Insert(i + 1, src.Duplicate(MakeUniquePropId(src.Id), src.Name));
        RefreshPropList(selectIndex: i + 1);
        SetStatus("プロップを複製しました（未保存）", ok: true);
    }

    private void OnRemoveProp(object sender, RoutedEventArgs e)
    {
        int i = LstProps.SelectedIndex;
        if (i < 0 || _props.Props.Count <= 1) return;

        // プロップ削除は、既に散布済みのインスタンス（prop_id 参照）を宙に浮かせる。
        // 破壊的な操作なので明示的に確認する。
        var r = MessageBox.Show(this,
            $"プロップ「{_props.Props[i].Name}（{_props.Props[i].Id}）」を削除します。\n"
            + "既にこのプロップで散布済みのインスタンスは、参照先を失って表示されなくなります。続行しますか？",
            "プロップの削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;

        _props.Props.RemoveAt(i);
        RefreshPropList(selectIndex: i);
        SetStatus("プロップを削除しました（未保存）", ok: true);
    }

    private void OnMovePropUp(object sender, RoutedEventArgs e)   => MoveSelectedProp(-1);
    private void OnMovePropDown(object sender, RoutedEventArgs e) => MoveSelectedProp(+1);

    /// <summary>選択プロップを delta 行ぶん移動する。</summary>
    private void MoveSelectedProp(int delta)
    {
        int i = LstProps.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _props.Props.Count) return;

        (_props.Props[i], _props.Props[j]) = (_props.Props[j], _props.Props[i]);
        RefreshPropList(selectIndex: j);
        SetStatus("プロップ順を入れ替えました（未保存）", ok: true);
    }

    /// <summary>既存プロップ ID と衝突しない ID を作る（末尾に連番を付ける）。</summary>
    private string MakeUniquePropId(string baseId)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in _props.Props) used.Add(p.Id);
        if (!used.Contains(baseId)) return baseId;
        for (int n = 1; ; n++)
        {
            var candidate = $"{baseId}_{n}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    // ── プロパティ欄の組み立て ────────────────────────────────

    /// <summary>
    /// 選択プロップのプロパティ UI を組み立て直す。
    /// kind に応じて表示するセクションを切り替える（草パラメータ／モデルパス）。
    /// </summary>
    private void RebuildPropPropertyPanel()
    {
        PanelPropProperties.Children.Clear();
        var prop = SelectedProp;
        if (prop is null) return;

        // ── 基本情報 ──
        PanelPropProperties.Children.Add(MakeSectionHeader("基本"));
        PanelPropProperties.Children.Add(MakeTextRow("ID", prop.Id, v =>
        {
            prop.Id = v;
            RefreshPropListEntryText(prop);
        }, "散布インスタンスと IPC（再散布・ブラシ）が参照する安定キー。変更すると散布済みの対応が切れる。"));
        PanelPropProperties.Children.Add(MakeHint(
            "ID は散布インスタンスと IPC（再散布・ブラシ）が参照する安定キー。変更すると散布済みの対応が切れる。"));
        PanelPropProperties.Children.Add(MakeTextRow("名前", prop.Name, v =>
        {
            prop.Name = v;
            RefreshPropListEntryText(prop);
        }, "一覧表示用の名前。散布結果そのものには影響しない。"));
        PanelPropProperties.Children.Add(MakeDisplayComboRow("種別",
            TerrainPropDefaults.Kinds, TerrainPropDefaults.KindDisplayNames, prop.Kind,
            v =>
            {
                if (string.Equals(prop.Kind, v, StringComparison.Ordinal)) return;
                prop.Kind = v;
                // 種別で表示項目そのものが変わるためパネルごと作り直す。
                RebuildPropPropertyPanel();
            }, "草＝数値から手続き生成（メッシュ不要）。モデル＝glTF/OBJ の実アセットを配置する。"));

        // ── モデル（kind=model のときのみ）──
        if (prop.IsModel)
        {
            PanelPropProperties.Children.Add(MakeSectionHeader("モデル"));
            PanelPropProperties.Children.Add(MakeModelPathRow(prop));
        }

        // ── 草パラメータ（kind=grass のときのみ）──
        if (prop.IsGrass)
        {
            var g = prop.Grass;
            PanelPropProperties.Children.Add(MakeSectionHeader("草（手続き生成）"));
            PanelPropProperties.Children.Add(MakeHint(
                "草はメッシュアセットではなく、以下の数値からランタイムが生成する。"));
            PanelPropProperties.Children.Add(MakeNumericRow("葉幅（m）", g.Width,
                TerrainPropDefaults.NonNegativeMin, GrassSizeUiMax, v => g.Width = v,
                "葉 1 枚の横幅（メートル）。"));
            PanelPropProperties.Children.Add(MakeNumericRow("高さ（m）", g.Height,
                TerrainPropDefaults.NonNegativeMin, GrassSizeUiMax, v => g.Height = v,
                "葉の基準の高さ（メートル）。実際の高さは下の変動率で上下する。"));
            PanelPropProperties.Children.Add(MakeNumericRow("高さの変動率", g.HeightVariance,
                TerrainPropDefaults.UnitRangeMin, TerrainPropDefaults.UnitRangeMax, v => g.HeightVariance = v,
                "個体ごとの高さのばらつき。0＝一定、1＝およそ 0〜2 倍まで変動。"));
            PanelPropProperties.Children.Add(MakeIntRow("縦分割数", g.Segments,
                TerrainPropDefaults.GrassMinSegments, TerrainPropDefaults.GrassMaxSegments, v => g.Segments = v,
                "葉の縦方向の分割数。多いほど滑らかに曲がるが頂点数が増える。"));
            PanelPropProperties.Children.Add(MakeBoolRow("十字 2 枚構成", g.CrossPlanes, v => g.CrossPlanes = v,
                "葉を十字に 2 枚重ねて密度感を出す（頂点数は倍になる）。"));
            PanelPropProperties.Children.Add(MakeNumericRow("垂れ（静止時の曲げ）", g.Bend,
                TerrainPropDefaults.UnitRangeMin, TerrainPropDefaults.UnitRangeMax, v => g.Bend = v,
                "静止時に葉先が垂れる量。0＝直立、1＝大きく垂れる。"));
            PanelPropProperties.Children.Add(MakeLinearColorRow("根元色", g.ColorBottom, c => g.ColorBottom = c,
                "葉の根元のリニア RGB 色。先端色との間をグラデーションで補間する。"));
            PanelPropProperties.Children.Add(MakeLinearColorRow("先端色", g.ColorTop,    c => g.ColorTop    = c,
                "葉の先端のリニア RGB 色。根元色との間をグラデーションで補間する。"));
            PanelPropProperties.Children.Add(MakeNumericRow("ラフネス", g.Roughness,
                TerrainPropDefaults.UnitRangeMin, TerrainPropDefaults.UnitRangeMax, v => g.Roughness = v,
                "表面の粗さ。0＝つやあり（反射が鋭い）、1＝つや消し。"));
            PanelPropProperties.Children.Add(MakeNumericRow("先端アルファ閾値", g.TipAlphaCutoff,
                TerrainPropDefaults.UnitRangeMin, TerrainPropDefaults.UnitRangeMax, v => g.TipAlphaCutoff = v,
                "葉先を透過で細らせる閾値。0＝そのまま（細らせない）。"));
            PanelPropProperties.Children.Add(MakeNumericRow("法線の地表寄せ", g.NormalUpBlend,
                TerrainPropDefaults.UnitRangeMin, TerrainPropDefaults.UnitRangeMax, v => g.NormalUpBlend = v,
                "葉の法線を地表向きへ寄せる割合。直立葉の N·L≈0 による黒ずみを防ぐ。0＝寄せない、1＝完全に地表向き。"));
        }

        // ── 風 ──
        var w = prop.Wind;
        PanelPropProperties.Children.Add(MakeSectionHeader("風"));
        PanelPropProperties.Children.Add(MakeHint(
            "基本振動（速く細かい）と突風（遅く大きい）の 2 成分で揺らす。強度 0 で揺れなし。"));
        PanelPropProperties.Children.Add(MakeNumericRow("強度", w.Strength,
            TerrainPropDefaults.NonNegativeMin, TerrainPropDefaults.UnitRangeMax, v => w.Strength = v,
            "揺れの全体量。0 で揺れなし。"));
        PanelPropProperties.Children.Add(MakeNumericRow("速度", w.Speed,
            TerrainPropDefaults.NonNegativeMin, WindUiMax, v => w.Speed = v,
            "基本振動（速く細かい成分）の速さ。"));
        PanelPropProperties.Children.Add(MakeNumericRow("空間周波数", w.Frequency,
            TerrainPropDefaults.NonNegativeMin, WindUiMax, v => w.Frequency = v,
            "隣り合う株どうしの位相差。大きいほど細かく波打つ。"));
        PanelPropProperties.Children.Add(MakeNumericRow("突風の強さ", w.GustStrength,
            TerrainPropDefaults.NonNegativeMin, TerrainPropDefaults.UnitRangeMax, v => w.GustStrength = v,
            "遅く大きなうねり成分の量。"));
        PanelPropProperties.Children.Add(MakeNumericRow("突風の速度", w.GustSpeed,
            TerrainPropDefaults.NonNegativeMin, WindUiMax, v => w.GustSpeed = v,
            "うねり成分の周期の速さ。"));

        // ── 散布（密度・姿勢）──
        var s = prop.Scatter;
        PanelPropProperties.Children.Add(MakeSectionHeader("散布（密度・姿勢）"));
        PanelPropProperties.Children.Add(MakeNumericRow("密度（点/m²）", s.Density,
            TerrainPropDefaults.NonNegativeMin, ScatterDensityUiMax, v => s.Density = v,
            "1m² あたりの候補点数。実際に生えるのは自動散布ルールの判定を通った候補だけ。"));
        PanelPropProperties.Children.Add(MakeHint(
            "密度は「候補点」の数。実際に生えるのは下のルール判定を通った候補だけ。"));
        PanelPropProperties.Children.Add(MakeNumericRow("スケール min", s.ScaleMin,
            TerrainPropDefaults.NonNegativeMin, ScaleUiMax, v => s.ScaleMin = v,
            "個体の大きさ倍率の下限。各個体は min〜max の範囲でランダムに決まる。"));
        PanelPropProperties.Children.Add(MakeNumericRow("スケール max", s.ScaleMax,
            TerrainPropDefaults.NonNegativeMin, ScaleUiMax, v => s.ScaleMax = v,
            "個体の大きさ倍率の上限。各個体は min〜max の範囲でランダムに決まる。"));
        PanelPropProperties.Children.Add(MakeBoolRow("地表法線に沿わせる", s.AlignToNormal, v => s.AlignToNormal = v,
            "地面の傾きに合わせて個体を傾ける。オフだと常に垂直に立つ。"));
        PanelPropProperties.Children.Add(MakeNumericRow("ランダム傾き上限（度）", s.TiltMaxDeg,
            TerrainPropDefaults.NonNegativeMin, TerrainPropDefaults.TiltRangeMax, v => s.TiltMaxDeg = v,
            "垂直からランダムに倒す最大角度。自然なばらつきを与える。"));
        PanelPropProperties.Children.Add(MakeBoolRow("ランダム Y 回転", s.RandomYaw, v => s.RandomYaw = v,
            "水平方向の向き（Y 軸まわりの回転）をランダム化する。"));

        // ── 自動散布ルール ──
        var r = prop.Rule;
        PanelPropProperties.Children.Add(MakeSectionHeader("自動散布ルール"));
        PanelPropProperties.Children.Add(MakeHint(
            "斜度ウィンドウ × 高度ウィンドウ × 全レイヤ条件の積が「その地点に生える確率」になる。"
            + "フェード幅は境界をぼかす幅（0 でハードエッジ）。"));
        PanelPropProperties.Children.Add(MakeNumericRow("斜度 min（度）", r.SlopeMinDeg,
            TerrainPropDefaults.SlopeRangeMin, TerrainPropDefaults.SlopeRangeMax, v => r.SlopeMinDeg = v,
            "生える地面の傾きの下限。これより緩い斜面には生えない。"));
        PanelPropProperties.Children.Add(MakeNumericRow("斜度 max（度）", r.SlopeMaxDeg,
            TerrainPropDefaults.SlopeRangeMin, TerrainPropDefaults.SlopeRangeMax, v => r.SlopeMaxDeg = v,
            "生える地面の傾きの上限。これより急な斜面には生えない。"));
        PanelPropProperties.Children.Add(MakeNumericRow("斜度フェード（度）", r.SlopeFadeDeg,
            TerrainPropDefaults.SlopeRangeMin, TerrainPropDefaults.SlopeRangeMax, v => r.SlopeFadeDeg = v,
            "斜度の境界をぼかす幅。0 でハードエッジ（境界でスパッと切れる）。"));
        PanelPropProperties.Children.Add(MakeNumericRow("高度 min（m）", r.HeightMin,
            null, null, v => r.HeightMin = v,
            "生えるワールド Y 高さの下限（メートル）。"));
        PanelPropProperties.Children.Add(MakeNumericRow("高度 max（m）", r.HeightMax,
            null, null, v => r.HeightMax = v,
            "生えるワールド Y 高さの上限（メートル）。"));
        PanelPropProperties.Children.Add(MakeNumericRow("高度フェード（m）", r.HeightFade,
            null, null, v => r.HeightFade = v,
            "高度の境界をぼかす幅（メートル）。0 でハードエッジ。"));
        PanelPropProperties.Children.Add(MakeNumericRow("閾値（足切り）", r.Threshold,
            TerrainPropDefaults.UnitRangeMin, TerrainPropDefaults.UnitRangeMax, v => r.Threshold = v,
            "算出確率がこの値未満の点は生やさない。まばら／密の最終調整に使う。"));

        // ── レイヤ条件 ──
        PanelPropProperties.Children.Add(MakeSectionHeader("レイヤ条件"));
        PanelPropProperties.Children.Add(MakeHint(
            "指定レイヤの重みが min 以上の場所にだけ生える。複数指定すると全て満たす必要がある（AND）。"
            + "条件が 0 件ならレイヤを問わない。"));
        BuildLayerConditionRows(r);
    }

    /// <summary>
    /// レイヤ条件の行群（各条件 1 行＋「条件を追加」ボタン）をプロパティ欄へ足す。
    /// 条件の増減は行構成そのものが変わるため、変更時はプロパティ欄ごと作り直す。
    /// </summary>
    private void BuildLayerConditionRows(ScatterRuleEdit rule)
    {
        // 選択肢は layers.json の現在のレイヤ名。名前で紐付ける仕様なので、
        // 並べ替えに強い代わりに「存在しない名前」も入りうる（その場合は重み 0 扱い）。
        var layerNames = new List<string>();
        foreach (var l in _doc.Layers) layerNames.Add(l.Name);

        for (int i = 0; i < rule.LayerConditions.Count; i++)
        {
            var cond = rule.LayerConditions[i];
            int index = i; // クロージャキャプチャ用

            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            // レイヤ名コンボ（layers.json に無い名前も表示できるよう、必要なら候補へ足す）。
            var cmb = new ComboBox
            {
                Width             = ConditionLayerComboWidth,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip           = "条件に使う地形レイヤ名（layers.json の名前と一致させる）",
            };
            foreach (var n in layerNames) cmb.Items.Add(n);
            if (!layerNames.Contains(cond.Layer)) cmb.Items.Add(cond.Layer);
            cmb.SelectedItem = cond.Layer;
            cmb.SelectionChanged += (_, _) =>
            {
                if (cmb.SelectedItem is string sel) cond.Layer = sel;
            };
            panel.Children.Add(cmb);

            // 最小重み。
            var lblWeight = new TextBlock
            {
                Text              = "min",
                Foreground        = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 4, 0),
            };
            panel.Children.Add(lblWeight);

            var boxWeight = MakeStyledTextBox(cond.MinWeight.ToString("G", CultureInfo.InvariantCulture));
            boxWeight.Width = ConditionWeightBoxWidth;
            boxWeight.ToolTip = "指定レイヤの塗り重みがこの値以上の場所にだけ生える（0〜1）。";
            boxWeight.TextChanged += (_, _) =>
            {
                if (!double.TryParse(boxWeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return; // 入力途中は確定させない
                cond.MinWeight = Math.Clamp(v, TerrainPropDefaults.UnitRangeMin, TerrainPropDefaults.UnitRangeMax);
            };
            panel.Children.Add(boxWeight);

            // 削除ボタン。
            var btnRemove = new Button
            {
                Style   = (Style)FindResource("ToolButtonStyle"),
                Content = "×",
                Width   = ConditionRemoveButtonWidth,
                Margin  = new Thickness(8, 0, 0, 0),
                ToolTip = "この条件を削除する",
            };
            btnRemove.Click += (_, _) =>
            {
                rule.LayerConditions.RemoveAt(index);
                RebuildPropPropertyPanel();
            };
            panel.Children.Add(btnRemove);

            PanelPropProperties.Children.Add(MakeRow($"条件 {index}", panel));
        }

        var btnAdd = new Button
        {
            Style               = (Style)FindResource("ToolButtonStyle"),
            Content             = "条件を追加",
            Margin              = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip             = "レイヤ条件を 1 件追加する",
        };
        btnAdd.Click += (_, _) =>
        {
            // 既定は先頭レイヤ（layers.json が空という異常時のみ空文字）。
            string defaultLayer = layerNames.Count > 0 ? layerNames[0] : NewLayerConditionFallbackName;
            rule.LayerConditions.Add(LayerConditionEdit.CreateNew(defaultLayer));
            RebuildPropPropertyPanel();
        };
        PanelPropProperties.Children.Add(btnAdd);
    }

    /// <summary>
    /// 選択行の一覧表示名だけを更新する（選択は維持する）。
    ///
    /// ListBox.Items[i] への代入は「削除＋挿入」として扱われるため選択が外れ（SelectedIndex = -1）、
    /// OnPropSelectionChanged 経由で RebuildPropPropertyPanel が走り、
    /// SelectedProp が null になってプロパティ欄が空になってしまう
    /// （ID／名前を 1 文字打つたびに編集欄が消える症状の直接原因）。
    /// 再入ガード（_rebuildingProps）を立てて差し替え、選択を元の行へ戻すことでこれを防ぐ。
    /// </summary>
    /// <param name="prop">
    /// 表示名を更新したいプロップ。確定は LostFocus でも起きるため、対象行は「選択行」ではなく
    /// 編集していたプロップ自身の位置から引く（フォーカス移動先が別の行でもずれない）。
    /// </param>
    private void RefreshPropListEntryText(TerrainPropEdit prop)
    {
        int idx = _props.Props.IndexOf(prop);
        if (idx < 0 || idx >= LstProps.Items.Count) return;
        // 差し替え前の選択をそのまま復元する。
        int prevSelected = LstProps.SelectedIndex;
        _rebuildingProps = true;
        LstProps.Items[idx]    = FormatPropListEntry(idx, prop.Name, prop.Id);
        LstProps.SelectedIndex = prevSelected;
        _rebuildingProps = false;
    }

    // ── 行生成ヘルパ（散布タブ専用）──────────────────────────

    /// <summary>真偽値のチェックボックス行を作る。</summary>
    private static Grid MakeBoolRow(string label, bool value, Action<bool> onChanged, string? tooltip = null)
    {
        var chk = new CheckBox
        {
            IsChecked           = value,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground          = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
        };
        chk.Checked   += (_, _) => onChanged(true);
        chk.Unchecked += (_, _) => onChanged(false);
        return MakeRow(label, chk, tooltip);
    }

    /// <summary>
    /// 「表示名は日本語・保存値は英小文字」の選択コンボ行を作る（kind など）。
    /// <paramref name="values"/> と <paramref name="displayNames"/> は同じ並びであること。
    /// </summary>
    private static Grid MakeDisplayComboRow(
        string label, string[] values, string[] displayNames, string current, Action<string> onChanged, string? tooltip = null)
    {
        var cmb = new ComboBox
        {
            Width               = NumericBoxWidth * 2,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var d in displayNames) cmb.Items.Add(d);
        cmb.SelectedIndex = Math.Max(0, Array.IndexOf(values, current));
        cmb.SelectionChanged += (_, _) =>
        {
            int i = cmb.SelectedIndex;
            if (i >= 0 && i < values.Length) onChanged(values[i]);
        };
        return MakeRow(label, cmb, tooltip);
    }

    /// <summary>
    /// リニア RGB 色の行を作る。スウォッチのクリックでカラーピッカーを開く。
    /// アルファは持たないため、ピッカーへは不透明を渡して結果の A は捨てる。
    /// </summary>
    private Grid MakeLinearColorRow(string label, double[] color, Action<double[]> onChanged, string? tooltip = null)
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
            Background          = new SolidColorBrush(Color.FromRgb(
                ToByte(color[0]), ToByte(color[1]), ToByte(color[2]))),
        };

        swatch.MouseLeftButtonUp += (_, _) =>
        {
            const float OpaqueAlpha = 1.0f;
            var picked = ColorPickerWindow.ShowDialog(this,
                (float)color[0], (float)color[1], (float)color[2], OpaqueAlpha);
            if (picked is null) return;
            onChanged(new double[] { picked.Value.r, picked.Value.g, picked.Value.b });
            // スウォッチの色を反映させるため行ごと作り直す。
            RebuildPropPropertyPanel();
        };
        // スウォッチ自身は「クリックでピッカーを開く」という操作説明の ToolTip を既に持つため、
        // MakeRow 側の「content の既存 ToolTip は上書きしない」規則により、
        // スウォッチ上＝操作説明／ラベル・行の余白＝色の意味説明、という住み分けになる。
        return MakeRow(label, swatch, tooltip);
    }

    /// <summary>
    /// モデルアセット参照の行を作る（kind=model のときのみ表示される）。
    /// レイヤタブのテクスチャ行と同じく FileRefBuilder を使い、
    /// 選ばれた絶対パスを assets ルート基準の相対パスへ変換して保存する。
    /// </summary>
    private UIElement MakeModelPathRow(TerrainPropEdit prop)
    {
        // 対応拡張子はランタイムのモデルローダが読めるものに合わせる。
        string[] acceptedExtensions = { ".gltf", ".glb", ".obj" };

        var row = FileRefBuilder.Build(
            "モデル",
            prop.ModelPath,
            acceptedExtensions,
            browseFn: () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title            = "モデルを選択",
                    Filter           = "モデルファイル|*.gltf;*.glb;*.obj",
                    InitialDirectory = Directory.Exists(_assetsRoot) ? _assetsRoot : Environment.CurrentDirectory,
                };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            },
            onPathSet: path =>
            {
                prop.ModelPath = ToAssetRelativePath(path);
                RebuildPropPropertyPanel();
            });

        // FileRefBuilder は「解除」手段を持たないため、右クリックでクリアできるようにする。
        if (row is FrameworkElement fe)
        {
            fe.ToolTip = (fe.ToolTip as string) ?? "右クリックでモデル指定を解除";
            fe.MouseRightButtonUp += (_, e) =>
            {
                prop.ModelPath = null;
                RebuildPropPropertyPanel();
                e.Handled = true;
            };
        }
        return row;
    }

    // ── 再散布（TERRAIN_SCATTER_RULES）──────────────────────────

    /// <summary>「ランダム」ボタン: 新しいシードを生成して入力欄へ書き込む。</summary>
    private void OnScatterRandomSeed(object sender, RoutedEventArgs e)
    {
        TxtScatterSeed.Text = GenerateSeed().ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// u64 の乱数シードを 1 つ生成する。
    /// Random.NextInt64 は符号付き 64bit を返すため、ビットパターンを保ったまま u64 へ読み替える。
    /// </summary>
    private static ulong GenerateSeed()
        => unchecked((ulong)Random.Shared.NextInt64());

    /// <summary>
    /// シード入力欄を u64 として解釈する。空／不正な入力は 0 へフォールバックする
    /// （送信を失敗させるより、決定的な既定シードで撒けたほうが編集が止まらない）。
    /// </summary>
    private ulong ParseScatterSeed()
        => ulong.TryParse(TxtScatterSeed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : SeedFallback;

    /// <summary>
    /// 「ルールで再散布」ボタン: TERRAIN_SCATTER_RULES:{prop_id},{seed} を送る。
    /// 対象コンボが「すべて」なら prop_id を空文字にして全プロップを撒き直す。
    /// </summary>
    private void OnScatterRules(object sender, RoutedEventArgs e)
    {
        bool allProps = (CmbScatterTarget.SelectedItem as ComboBoxItem)?.Tag as string == ScatterTargetAllTag;
        string propId = allProps ? "" : (SelectedProp?.Id ?? "");

        if (!allProps && string.IsNullOrEmpty(propId))
        {
            SetStatus("再散布するプロップが選択されていません", ok: false);
            return;
        }

        ulong seed = ParseScatterSeed();
        _sendToRuntime($"TERRAIN_SCATTER_RULES:{propId},{seed.ToString(CultureInfo.InvariantCulture)}");
        SetStatus(allProps
            ? $"全プロップの再散布を送信しました（seed={seed}）"
            : $"「{propId}」の再散布を送信しました（seed={seed}）", ok: true);
    }
}
