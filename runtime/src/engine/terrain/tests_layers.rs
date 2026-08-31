// ============================================================
//  terrain/tests_layers.rs — レイヤブレンド（T2）のユニットテスト
//
//  1. レイヤ重みの正規化（縮退ケース含む）
//  2. 斜度／高度ルールの期待値
//  3. ルール自動生成と手ペイントの共存（ペイント優先ブレンド）
//  4. ペイントブラシの重み押し上げ・正規化減衰
//  5. tvox v3 ラウンドトリップと v2/v1 後方互換
//  6. triplanar ブレンドの CPU 参照（シェーダ式との対応）
//  7. T2b: 上位 4 層選択（BlendSlots）・DetileMode・可変レイヤ数・チャンクパレット
// ============================================================

use std::collections::HashMap;

use super::brush::SphereBrush;
use super::chunk_coord::ChunkCoord;
use super::chunk_data::TerrainChunkData;
use super::layers::{
    BlendSlots, DetileMode, LayerRule, LayerWeights, TERRAIN_BLEND_SLOTS, TERRAIN_MAX_LAYERS,
    TerrainLayer, TerrainLayerSet, blend_rule_and_paint_all, blend_rule_and_paint_all_into,
    dequantize_weight, expand_slots, expand_slots_into, normalize_weights_slice, quantize_weight,
    select_top_slots,
};
use super::paint::{PaintField, apply_paint};
use super::settings::TerrainSettings;
use super::tvox::{read_chunk, write_chunk, write_chunk_v1, write_chunk_v2};

// ─── テスト用定数（マジックナンバー回避） ───────────────────────────────────

/// 重み比較の許容誤差（f32 の丸めを許容する）。
const WEIGHT_EPS: f32 = 1e-4;
/// u8 量子化を経る比較の許容誤差（1/255 ≒ 0.0039 相当）。
const QUANT_EPS: f32 = 5.0e-3;
/// triplanar ブレンドの鋭さ（terrain_gbuffer.rs の DEFAULT_TRIPLANAR_SHARPNESS と対応）。
const TRIPLANAR_SHARPNESS: f32 = 4.0;
/// ペイントテスト用のブラシ中心（チャンク中央・ローカル座標メートル）。
const PAINT_CENTER: [f32; 3] = [8.0, 8.0, 8.0];
/// ペイントテスト用のブラシ半径（メートル）。
const PAINT_RADIUS: f32 = 3.0;

/// テスト用のレイヤ既定値（rule 以外を埋めるヘルパ）。
fn default_test_layer() -> TerrainLayer {
    TerrainLayer {
        base_color: [1.0, 1.0, 1.0],
        roughness: 1.0,
        uv_scale: 1.0,
        ..TerrainLayer::default()
    }
}

/// レイヤ名だけを与えたテスト用レイヤ（ルールは既定＝常に重み 1）。
fn named_layer(name: &str) -> TerrainLayer {
    TerrainLayer {
        name: name.to_string(),
        ..default_test_layer()
    }
}

/// テスト用の 2 層セット（平地=レイヤ0 赤 / 急斜面=レイヤ1 青）。
/// 境界のぼかしを 0 にして、期待値を解析的に書けるようにする。
fn two_layer_set() -> TerrainLayerSet {
    TerrainLayerSet {
        layers: vec![
            TerrainLayer {
                name: "flat".to_string(),
                base_color: [1.0, 0.0, 0.0],
                roughness: 0.5,
                rule: LayerRule {
                    slope_min_deg: 0.0,
                    slope_max_deg: 30.0,
                    slope_fade_deg: 0.0,
                    priority: 1.0,
                    ..LayerRule::default()
                },
                ..default_test_layer()
            },
            TerrainLayer {
                name: "steep".to_string(),
                base_color: [0.0, 0.0, 1.0],
                roughness: 0.25,
                rule: LayerRule {
                    slope_min_deg: 60.0,
                    slope_max_deg: 90.0,
                    slope_fade_deg: 0.0,
                    priority: 1.0,
                    ..LayerRule::default()
                },
                ..default_test_layer()
            },
        ],
    }
}

// ============================================================
//  1. レイヤ重みの正規化
// ============================================================

/// 重みの総和が常に 1 に正規化されること（縮退ケース含む）。
#[test]
fn layer_weights_are_normalized() {
    // ─── 通常ケース: 比が保たれたまま総和 1 になる ───
    let mut w = [1.0, 3.0, 0.0, 0.0];
    normalize_weights_slice(&mut w);
    assert!((w.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS);
    assert!((w[0] - 0.25).abs() < WEIGHT_EPS, "w={w:?}");
    assert!((w[1] - 0.75).abs() < WEIGHT_EPS, "w={w:?}");

    // ─── 負値は 0 に潰される ───
    let mut w = [-5.0, 1.0, 0.0, 0.0];
    normalize_weights_slice(&mut w);
    assert_eq!(w[0], 0.0);
    assert!((w[1] - 1.0).abs() < WEIGHT_EPS);

    // ─── 縮退（全 0）: レイヤ 0 を下地に敷く（黒落ち防止の規約）───
    let mut w: LayerWeights = [0.0; TERRAIN_BLEND_SLOTS];
    normalize_weights_slice(&mut w);
    assert_eq!(w, [1.0, 0.0, 0.0, 0.0]);
}

/// u8 量子化の往復で重みが視覚的に一致すること。
#[test]
fn weight_quantization_round_trip() {
    for &v in &[0.0f32, 0.1, 0.333, 0.5, 0.75, 1.0] {
        let back = dequantize_weight(quantize_weight(v));
        assert!((back - v).abs() < QUANT_EPS, "v={v} back={back}");
    }
    // 範囲外はクランプされる。
    assert_eq!(quantize_weight(-1.0), 0);
    assert_eq!(quantize_weight(2.0), 255);
}

// ============================================================
//  2. 斜度／高度ルールの期待値
// ============================================================

/// 斜度ルールが期待どおりのレイヤを選ぶこと。
///
/// 4 層固定 API `rule_weights` は T2b でデッドコード化したため削除し、
/// 任意レイヤ数版 `rule_weights_all` へ移植した（two_layer_set は 2 層なので
/// 戻り値 Vec の長さは 2 になり、検証している意味は変わらない）。
#[test]
fn rule_weights_match_expected_slope_selection() {
    let set = two_layer_set();

    // ─── 真上向き法線（斜度 0 度）→ 平地レイヤ 100% ───
    let w = set.rule_weights_all(1.0, 0.0);
    assert!((w[0] - 1.0).abs() < WEIGHT_EPS, "flat: {w:?}");
    assert!(w[1] < WEIGHT_EPS, "flat: {w:?}");

    // ─── 水平向き法線（斜度 90 度）→ 急斜面レイヤ 100% ───
    let w = set.rule_weights_all(0.0, 0.0);
    assert!(w[0] < WEIGHT_EPS, "steep: {w:?}");
    assert!((w[1] - 1.0).abs() < WEIGHT_EPS, "steep: {w:?}");

    // ─── 斜度 45 度はどちらのウィンドウにも入らない（縮退→レイヤ 0 へ寄る）───
    let w = set.rule_weights_all(std::f32::consts::FRAC_1_SQRT_2, 0.0);
    assert_eq!(w, vec![1.0, 0.0], "45deg fallback: {w:?}");

    // ─── 下向き法線（天井）も |n.y| で同じ斜度として扱う ───
    assert_eq!(
        set.rule_weights_all(-1.0, 0.0),
        set.rule_weights_all(1.0, 0.0)
    );
}

/// 高度ウィンドウが効くこと（低地だけに載るレイヤ）。
#[test]
fn rule_weights_respect_height_window() {
    let set = TerrainLayerSet {
        layers: vec![
            TerrainLayer {
                name: "base".to_string(),
                rule: LayerRule {
                    priority: 1.0,
                    ..LayerRule::default()
                },
                ..default_test_layer()
            },
            TerrainLayer {
                name: "lowland".to_string(),
                rule: LayerRule {
                    height_min: -1000.0,
                    height_max: 0.0,
                    height_fade: 0.0,
                    priority: 3.0,
                    ..LayerRule::default()
                },
                ..default_test_layer()
            },
        ],
    };

    // ─── Y = -5（低地）: base 1 : lowland 3 → 0.25 / 0.75 ───
    let w = set.rule_weights_all(1.0, -5.0);
    assert!((w[0] - 0.25).abs() < WEIGHT_EPS, "lowland: {w:?}");
    assert!((w[1] - 0.75).abs() < WEIGHT_EPS, "lowland: {w:?}");

    // ─── Y = +5（高所）: lowland のウィンドウ外 → base 100% ───
    let w = set.rule_weights_all(1.0, 5.0);
    assert!((w[0] - 1.0).abs() < WEIGHT_EPS, "highland: {w:?}");
}

/// 既定レイヤセット（layers.json 不在時のフォールバック）が
/// 「平地は草・急斜面は岩」になっていること（目視スクリーンショットの期待値と対応）。
#[test]
fn default_layer_set_paints_grass_on_flat_and_rock_on_steep() {
    let set = TerrainLayerSet::default();
    assert_eq!(set.active_count(), TERRAIN_BLEND_SLOTS);

    // ─── 平地（斜度 0・高度 0）: レイヤ 0（grass）が支配的 ───
    let flat = set.rule_weights_all(1.0, 0.0);
    assert!(flat[0] > 0.9, "平地が草地になっていない: {flat:?}");

    // ─── 急斜面（斜度 90 度・高度 0）: レイヤ 2（rock）が支配的 ───
    let steep = set.rule_weights_all(0.0, 0.0);
    assert!(steep[2] > 0.9, "急斜面が岩になっていない: {steep:?}");
}

/// layers.json 相当の JSON がパースでき、5 層でも切り詰められないこと（T2b）。
///
/// T2b でレイヤ総数の上限は TERRAIN_MAX_LAYERS へ拡張された。
/// 「同時ブレンド数 4」と「定義できる総数 16」が分離されたことの回帰テスト。
#[test]
fn layer_set_json_parses_more_than_blend_slots() {
    let json = r#"{
        "layers": [
            { "name": "a", "base_color": [1.0, 0.0, 0.0] },
            { "name": "b", "base_color": [0.0, 1.0, 0.0], "rule": { "slope_min_deg": 40.0 } },
            { "name": "c" }, { "name": "d" }, { "name": "e" }
        ]
    }"#;
    let set = TerrainLayerSet::from_json_str(json).expect("layers.json のパースに失敗");
    assert_eq!(set.layers.len(), 5, "5 層がそのまま読めていない");
    assert_eq!(set.layers[0].name, "a");
    // 省略されたフィールドは serde default が入る。
    assert_eq!(set.layers[2].base_color, [0.5, 0.5, 0.5]);
    assert!((set.layers[1].rule.slope_min_deg - 40.0).abs() < WEIGHT_EPS);
}

// ============================================================
//  3. ルール自動生成と手ペイントの共存
// ============================================================

/// ルール自動生成と手ペイントの共存（ペイント優先ブレンド）が仕様どおりであること。
///
/// 4 層固定 API `blend_rule_and_paint` は T2b でデッドコード化したため削除し、
/// 任意レイヤ数版 `blend_rule_and_paint_all` へ移植した。ペイント側は
/// `select_top_slots` で BlendSlots 化してから渡す（本番の呼び出し経路と同じ形）。
/// 検証している lerp の性質（t=0でルール・t=1でペイント・中間で按分・総和1）は
/// そのまま保たれている。
#[test]
fn paint_blends_over_rule_without_overwriting_when_unpainted() {
    let rule = vec![1.0, 0.0, 0.0, 0.0];
    let paint_dense = [0.0, 1.0, 0.0, 0.0];
    let paint = select_top_slots(&paint_dense);

    // ─── 未ペイント（amount=0）: 完全にルール任せ（自動下地が生き続ける）───
    let w = blend_rule_and_paint_all(&rule, &paint, 0.0);
    assert!((w[0] - 1.0).abs() < WEIGHT_EPS, "{w:?}");

    // ─── 完全ペイント（amount=1）: ルールを無視して手描き優先 ───
    let w = blend_rule_and_paint_all(&rule, &paint, 1.0);
    assert!((w[1] - 1.0).abs() < WEIGHT_EPS, "{w:?}");

    // ─── 中間（amount=0.25）: 線形補間（ブラシ縁のフェード）───
    let w = blend_rule_and_paint_all(&rule, &paint, 0.25);
    assert!((w[0] - 0.75).abs() < WEIGHT_EPS, "{w:?}");
    assert!((w[1] - 0.25).abs() < WEIGHT_EPS, "{w:?}");
    assert!((w.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS);
}

// ============================================================
//  4. ペイントブラシ
// ============================================================

/// ペイント用の最小 PaintField 実装（単一チャンク・境界重複なし）。
///
/// ブラシ形状マスクのテスト（`tests_brush_mask.rs`）も同じ器を使うため `pub(super)`。
/// テスト用の場を 2 つ書くと「片方だけ規約がずれる」危険があるので共有する。
pub(super) struct PaintView<'a> {
    pub(super) settings: &'a TerrainSettings,
    pub(super) chunks: &'a mut HashMap<ChunkCoord, TerrainChunkData>,
}

impl<'a> PaintView<'a> {
    /// グローバルサンプル座標 → (チャンク座標, ローカル添字)。
    fn split(&self, gx: i32, gy: i32, gz: i32) -> (ChunkCoord, usize, usize, usize) {
        let cells = self.settings.chunk_cells as i32;
        (
            ChunkCoord::new(
                gx.div_euclid(cells),
                gy.div_euclid(cells),
                gz.div_euclid(cells),
            ),
            gx.rem_euclid(cells) as usize,
            gy.rem_euclid(cells) as usize,
            gz.rem_euclid(cells) as usize,
        )
    }
}

impl<'a> PaintField for PaintView<'a> {
    fn settings(&self) -> &TerrainSettings {
        self.settings
    }
    fn read_paint_global(&self, gx: i32, gy: i32, gz: i32) -> (BlendSlots, f32) {
        let (c, lx, ly, lz) = self.split(gx, gy, gz);
        match self.chunks.get(&c) {
            Some(chunk) => (
                chunk.paint_slots(lx, ly, lz),
                chunk.paint_amount(lx, ly, lz),
            ),
            None => (
                BlendSlots {
                    index: [0; TERRAIN_BLEND_SLOTS],
                    weight: [0.0; TERRAIN_BLEND_SLOTS],
                },
                0.0,
            ),
        }
    }
    fn write_paint_global(&mut self, gx: i32, gy: i32, gz: i32, slots: &BlendSlots, amount: f32) {
        let (c, lx, ly, lz) = self.split(gx, gy, gz);
        if let Some(chunk) = self.chunks.get_mut(&c) {
            chunk.set_paint_slots(lx, ly, lz, slots);
            chunk.set_paint_amount(lx, ly, lz, amount);
        }
    }
    fn world_of_global(&self, gx: i32, gy: i32, gz: i32) -> [f32; 3] {
        let vs = self.settings.voxel_size;
        [gx as f32 * vs, gy as f32 * vs, gz as f32 * vs]
    }
}

/// ペイントブラシがブラシ中心のレイヤ重みを押し上げ、他レイヤを正規化で減衰させること。
#[test]
fn paint_brush_raises_target_layer_and_normalizes() {
    let settings = TerrainSettings::default();
    let mut chunks: HashMap<ChunkCoord, TerrainChunkData> = HashMap::new();
    chunks.insert(
        ChunkCoord::new(0, 0, 0),
        TerrainChunkData::new_filled(&settings, 0.0),
    );

    // チャンク中央にレイヤ 1 を強くペイントする（dt=1.0・中心 falloff=1 → delta=1）。
    let brush = SphereBrush {
        center: PAINT_CENTER,
        radius: PAINT_RADIUS,
        strength: 1.0,
    };
    let affected = {
        let mut view = PaintView {
            settings: &settings,
            chunks: &mut chunks,
        };
        apply_paint(&mut view, &brush, 1, 1.0)
    };
    assert!(
        !affected.is_empty(),
        "ペイントがどのチャンクにも届いていない"
    );

    // ─── ブラシ中心のサンプルはレイヤ 1 が支配的・総和 1 ───
    let chunk = chunks.get(&ChunkCoord::new(0, 0, 0)).unwrap();
    let idx = |v: f32| (v / settings.voxel_size) as usize;
    let (cx, cy, cz) = (
        idx(PAINT_CENTER[0]),
        idx(PAINT_CENTER[1]),
        idx(PAINT_CENTER[2]),
    );
    let slots = chunk.paint_slots(cx, cy, cz);
    // 重み降順ソート済みなので、支配的な層はスロット 0 に来る。
    assert_eq!(
        slots.index[0], 1,
        "中心のスロット 0 がレイヤ 1 でない: {slots:?}"
    );
    assert!(slots.weight[0] > 0.99, "中心のレイヤ1重みが弱い: {slots:?}");
    assert!(
        (slots.weight.iter().sum::<f32>() - 1.0).abs() < QUANT_EPS,
        "{slots:?}"
    );
    assert!(
        chunk.paint_amount(cx, cy, cz) > 0.99,
        "中心のペイント量が弱い"
    );

    // ─── ブラシ半径の外は未ペイントのまま（ルール任せ）───
    assert_eq!(chunk.paint_amount(0, 0, 0), 0.0);

    // ─── 不正なレイヤ番号（定義上限以上）は無操作 ───
    let noop = {
        let mut view = PaintView {
            settings: &settings,
            chunks: &mut chunks,
        };
        apply_paint(&mut view, &brush, TERRAIN_MAX_LAYERS as u32, 1.0)
    };
    assert!(noop.is_empty(), "範囲外レイヤ番号で書き込みが起きている");
}

/// 4 スロットが埋まった状態で 5 番目の層を塗ると、最小重みスロットが置換されること。
///
/// 4 スロットを「重みがすべて異なる」状態に作り込むことで、置換されるスロットを
/// 一意に確定させている（同点だと最初に見つかった最小スロットが選ばれ、
/// どちらが落ちるかは実装依存になるため）。
#[test]
fn paint_brush_replaces_weakest_slot_when_full() {
    let settings = TerrainSettings::default();
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);

    // ─── ブラシ中心のサンプル添字（ワールド座標 → グリッド添字）───
    let idx = |v: f32| (v / settings.voxel_size) as usize;
    let (cx, cy, cz) = (
        idx(PAINT_CENTER[0]),
        idx(PAINT_CENTER[1]),
        idx(PAINT_CENTER[2]),
    );

    // ─── 4 スロットを重み降順（すべて異なる値）で埋めておく ───
    //   最弱スロットはレイヤ 5（重み 0.1）で一意。
    let seeded = BlendSlots {
        index: [1, 2, 3, 5],
        weight: [0.4, 0.3, 0.2, 0.1],
    };
    chunk.set_paint_slots(cx, cy, cz, &seeded);
    chunk.set_paint_amount(cx, cy, cz, 1.0);

    let mut chunks: HashMap<ChunkCoord, TerrainChunkData> = HashMap::new();
    chunks.insert(ChunkCoord::new(0, 0, 0), chunk);

    // ─── 5 番目の層（レイヤ 8）を弱めに塗る（delta = strength * falloff(0) * dt = 0.2）───
    //   delta を小さくすることで「新しい層が全部を塗り潰す」ケースと区別する。
    let brush = SphereBrush {
        center: PAINT_CENTER,
        radius: PAINT_RADIUS,
        strength: 0.2,
    };
    {
        let mut view = PaintView {
            settings: &settings,
            chunks: &mut chunks,
        };
        apply_paint(&mut view, &brush, 8, 1.0);
    }
    let after = chunks
        .get(&ChunkCoord::new(0, 0, 0))
        .unwrap()
        .paint_slots(cx, cy, cz);

    // 新しい層が入り、最弱だったレイヤ 5 が落ちている。
    assert!(
        after.index.contains(&8),
        "新しい層が入っていない: {after:?}"
    );
    assert!(
        !after.index.contains(&5),
        "最小重みスロットが置換されていない: {after:?}"
    );
    // 残りの 3 層はそのまま残る。
    for layer in [1u32, 2, 3] {
        assert!(after.index.contains(&layer), "強い層が落ちた: {after:?}");
    }
    // 正規化されている（総和 1・降順）。
    assert!(
        (after.weight.iter().sum::<f32>() - 1.0).abs() < QUANT_EPS,
        "{after:?}"
    );
    for k in 1..TERRAIN_BLEND_SLOTS {
        assert!(
            after.weight[k - 1] >= after.weight[k],
            "降順でない: {after:?}"
        );
    }
}

// ============================================================
//  5. tvox v3 / v2 / v1 後方互換
// ============================================================

/// tvox v3 がスプラット（レイヤ番号＋重み）を含めてビット一致でラウンドトリップすること。
#[test]
fn tvox_v3_round_trip_includes_splat() {
    let settings = TerrainSettings::default();
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
    chunk.set_sample(1, 2, 3, -1.25);
    // レイヤ番号 4 桁（TERRAIN_BLEND_SLOTS を超える番号）も往復できることを確かめる。
    chunk.set_paint_slots(
        1,
        2,
        3,
        &BlendSlots {
            index: [7, 0, 0, 0],
            weight: [1.0, 0.0, 0.0, 0.0],
        },
    );
    chunk.set_paint_amount(1, 2, 3, 1.0);
    chunk.set_paint_slots(
        4,
        5,
        6,
        &BlendSlots {
            index: [2, 9, 0, 0],
            weight: [0.5, 0.5, 0.0, 0.0],
        },
    );
    chunk.set_paint_amount(4, 5, 6, 0.5);

    let coord = ChunkCoord::new(-2, 0, 7);
    let bytes = write_chunk(&chunk, coord, &settings);
    let (restored, restored_coord) = read_chunk(&bytes).expect("v3 読み込みに失敗");

    assert_eq!(restored_coord, coord);
    assert_eq!(restored.raw_density(), chunk.raw_density());
    assert_eq!(restored.raw_paint_index(), chunk.raw_paint_index());
    assert_eq!(restored.raw_paint_weight(), chunk.raw_paint_weight());
    assert_eq!(restored.raw_paint_amount(), chunk.raw_paint_amount());

    // アクセサ経由でも同じスロットが返る（u8 量子化往復）。
    let s = restored.paint_slots(4, 5, 6);
    assert_eq!(s.index, [2, 9, 0, 0]);
    assert!((s.weight[0] - 0.5).abs() < QUANT_EPS, "{s:?}");
    assert!((s.weight[1] - 0.5).abs() < QUANT_EPS, "{s:?}");
}

/// tvox v2（レイヤ番号なし・密な重み並び）を読むと index=[0,1,2,3] とみなされること。
///
/// T2b 以前に保存された .tvox がそのまま開けることの回帰テスト。
#[test]
fn tvox_v2_is_readable_with_identity_layer_indices() {
    let settings = TerrainSettings::default();
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
    chunk.set_sample(3, 3, 3, -2.5);
    chunk.set_paint_amount(3, 3, 3, 1.0);

    // ─── v2 当時の「レイヤ番号を添字とする密な u8 重み」を用意する ───
    //   サンプル (3,3,3) だけレイヤ 2 を 100%（= 255）にする。
    let total = chunk.raw_density().len();
    let samples = chunk.samples_per_axis();
    let flat = 3 + 3 * samples + 3 * samples * samples;
    let mut dense = vec![[0u8; TERRAIN_BLEND_SLOTS]; total];
    dense[flat] = [0, 0, 255, 0];

    let coord = ChunkCoord::new(1, -1, 2);
    let v2_bytes = write_chunk_v2(&chunk, coord, &settings, &dense);
    let (restored, restored_coord) = read_chunk(&v2_bytes).expect("v2 読み込みに失敗");

    assert_eq!(restored_coord, coord);
    assert_eq!(restored.raw_density(), chunk.raw_density());

    // ─── スロットのレイヤ番号は恒等 [0,1,2,3]、重みはファイルのまま ───
    let s = restored.paint_slots(3, 3, 3);
    assert_eq!(
        s.index,
        [0, 1, 2, 3],
        "v2 のレイヤ番号が恒等になっていない: {s:?}"
    );
    assert!(
        (s.weight[2] - 1.0).abs() < QUANT_EPS,
        "レイヤ 2 の重みが復元されていない: {s:?}"
    );
    assert!(
        s.weight[0] < QUANT_EPS && s.weight[1] < QUANT_EPS && s.weight[3] < QUANT_EPS,
        "{s:?}"
    );
    assert!(restored.paint_amount(3, 3, 3) > 0.99);

    // 全サンプルで恒等インデックスが入っている。
    assert!(
        restored
            .raw_paint_index()
            .iter()
            .all(|i| *i == [0, 1, 2, 3]),
        "v2 読み込みで恒等インデックスになっていないサンプルがある"
    );
}

/// tvox v1（密度のみ）を読むと、スプラットが「全面未ペイント」として復元されること。
///
/// 未ペイント＝ルール自動生成が全面に適用されるため、旧セーブデータでも
/// レイヤブレンドされた地形になる（重み欠落による黒落ちが起きない）。
#[test]
fn tvox_v1_is_readable_and_defaults_to_rule_generated_splat() {
    let settings = TerrainSettings::default();
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
    chunk.set_sample(3, 3, 3, -2.5);
    // v1 に無いはずのスプラットをあえて書いておき、v1 経由で消えることを確かめる。
    chunk.set_paint_slots(
        3,
        3,
        3,
        &BlendSlots {
            index: [2, 0, 0, 0],
            weight: [1.0, 0.0, 0.0, 0.0],
        },
    );
    chunk.set_paint_amount(3, 3, 3, 1.0);

    let coord = ChunkCoord::new(1, -1, 2);
    let v1_bytes = write_chunk_v1(&chunk, coord, &settings);
    let (restored, restored_coord) = read_chunk(&v1_bytes).expect("v1 読み込みに失敗");

    assert_eq!(restored_coord, coord);
    // 密度は完全一致。
    assert_eq!(restored.raw_density(), chunk.raw_density());
    // スプラットは全面未ペイント（= 0）。
    assert!(
        restored.raw_paint_amount().iter().all(|&a| a == 0),
        "v1 読み込みでペイント量が 0 になっていない"
    );
    assert!(
        restored
            .raw_paint_weight()
            .iter()
            .all(|w| w.iter().all(|&q| q == 0)),
        "v1 読み込みでペイント重みが 0 になっていない"
    );

    // 未ペイントなので、最終重みは 100% ルール由来になる。
    let set = two_layer_set();
    let rule = set.rule_weights_all(1.0, 0.0);
    let final_w = blend_rule_and_paint_all(
        &rule,
        &restored.paint_slots(3, 3, 3),
        restored.paint_amount(3, 3, 3),
    );
    assert_eq!(final_w, rule);
}

// ============================================================
//  6. triplanar ブレンドの CPU 参照
// ============================================================

/// terrain_gbuffer_write.wgsl の `triplanar_blend_weights` の CPU 参照実装。
///
/// シェーダ側と式を 1:1 に保つ（pow(|n|, sharpness) を総和で正規化）。
/// シェーダを書き換えたらこちらも合わせること。
fn triplanar_blend_weights_ref(n: [f32; 3], sharpness: f32) -> [f32; 3] {
    let w = [
        n[0].abs().powf(sharpness),
        n[1].abs().powf(sharpness),
        n[2].abs().powf(sharpness),
    ];
    let sum = w[0] + w[1] + w[2];
    // シェーダ側 TRIPLANAR_MIN_SUM と同じ縮退規約（XZ 平面＝真上投影へ倒す）。
    if sum < 1e-5 {
        return [0.0, 1.0, 0.0];
    }
    [w[0] / sum, w[1] / sum, w[2] / sum]
}

/// triplanar ブレンド重みが「総和 1・支配軸が最大・縮退で真上投影」を満たすこと。
#[test]
fn triplanar_blend_weights_reference() {
    // ─── 真上向き（平地）: XZ 平面（Y 成分）が 100% ───
    let w = triplanar_blend_weights_ref([0.0, 1.0, 0.0], TRIPLANAR_SHARPNESS);
    assert!((w[1] - 1.0).abs() < WEIGHT_EPS, "{w:?}");

    // ─── 45 度斜面（X と Y が同じ大きさ）: X と Y が等分、Z は 0 ───
    let s = std::f32::consts::FRAC_1_SQRT_2;
    let w = triplanar_blend_weights_ref([s, s, 0.0], TRIPLANAR_SHARPNESS);
    assert!((w[0] - 0.5).abs() < WEIGHT_EPS, "{w:?}");
    assert!((w[1] - 0.5).abs() < WEIGHT_EPS, "{w:?}");
    assert!(w[2] < WEIGHT_EPS, "{w:?}");

    // ─── 総和は常に 1 ───
    for n in [[1.0, 0.0, 0.0], [0.3, 0.9, 0.31], [-0.5, -0.5, 0.7071]] {
        let w = triplanar_blend_weights_ref(n, TRIPLANAR_SHARPNESS);
        assert!(
            (w.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS,
            "n={n:?} w={w:?}"
        );
    }

    // ─── 縮退（0 ベクトル）: XZ 平面（真上投影）へフォールバック ───
    assert_eq!(
        triplanar_blend_weights_ref([0.0, 0.0, 0.0], TRIPLANAR_SHARPNESS),
        [0.0, 1.0, 0.0]
    );
}

/// 単色レイヤの albedo 合成（シェーダの Σ w_i * c_i と同じ式）。
///
/// 重みは `rule_weights_all` / `blend_rule_and_paint_all` が返す任意長 `Vec<f32>`
/// を受け取れるよう `&[f32]` で受ける（4 層固定 `LayerWeights` 版は削除済み）。
fn blend_layer_albedo_ref(set: &TerrainLayerSet, w: &[f32]) -> [f32; 3] {
    let mut out = [0.0f32; 3];
    for (i, layer) in set.layers.iter().take(TERRAIN_BLEND_SLOTS).enumerate() {
        for k in 0..3 {
            out[k] += layer.base_color[k] * w[i];
        }
    }
    out
}

/// スプラット重みによるレイヤ合成（シェーダの最終合成式）の CPU 参照。
///
/// 単色レイヤ（テクスチャ無し）では albedo = Σ w_i * base_color_i になる。
/// 層が塗り分かれていれば albedo が層色に一致することを固定する。
#[test]
fn layer_blend_composition_reference() {
    let set = two_layer_set();

    // ─── 平地（斜度 0）: レイヤ 0（赤）が 100% ───
    let w = set.rule_weights_all(1.0, 0.0);
    let albedo = blend_layer_albedo_ref(&set, &w);
    assert!(
        (albedo[0] - 1.0).abs() < WEIGHT_EPS,
        "flat albedo={albedo:?}"
    );
    assert!(albedo[2] < WEIGHT_EPS, "flat albedo={albedo:?}");

    // ─── 崖（斜度 90）: レイヤ 1（青）が 100% ───
    let w = set.rule_weights_all(0.0, 0.0);
    let albedo = blend_layer_albedo_ref(&set, &w);
    assert!(albedo[0] < WEIGHT_EPS, "steep albedo={albedo:?}");
    assert!(
        (albedo[2] - 1.0).abs() < WEIGHT_EPS,
        "steep albedo={albedo:?}"
    );

    // ─── 半々に手ペイントした場合は色が中間になる ───
    let rule = vec![1.0, 0.0, 0.0, 0.0];
    let paint = select_top_slots(&[0.0, 1.0, 0.0, 0.0]);
    let w = blend_rule_and_paint_all(&rule, &paint, 0.5);
    let albedo = blend_layer_albedo_ref(&set, &w);
    assert!(
        (albedo[0] - 0.5).abs() < WEIGHT_EPS,
        "mix albedo={albedo:?}"
    );
    assert!(
        (albedo[2] - 0.5).abs() < WEIGHT_EPS,
        "mix albedo={albedo:?}"
    );
}

// ============================================================
//  7. T2b — 上位 4 層選択 / DetileMode / 可変レイヤ数 / チャンクパレット
// ============================================================

/// select_top_slots が「上位 4 層・重み降順・総和 1」を満たすこと。
#[test]
fn select_top_slots_picks_four_strongest_in_descending_order() {
    // ─── 7 層の重み分布から上位 4 層（レイヤ 3,5,1,6）が選ばれる ───
    //   値:            0    1    2    3    4    5    6
    let weights = [0.05, 0.20, 0.01, 0.40, 0.00, 0.30, 0.10];
    let slots = select_top_slots(&weights);

    assert_eq!(
        slots.index,
        [3, 5, 1, 6],
        "上位 4 層の選択が誤り: {slots:?}"
    );

    // 重みは降順で、上位 4 層だけで総和 1 に再正規化されている
    //（落ちたレイヤ 0/2/4 のぶん 0.06 が配分されるので元の値そのままではない）。
    for k in 1..TERRAIN_BLEND_SLOTS {
        assert!(
            slots.weight[k - 1] >= slots.weight[k],
            "降順でない: {slots:?}"
        );
    }
    assert!(
        (slots.weight.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS,
        "{slots:?}"
    );
    // 0.40 / (0.40+0.30+0.20+0.10) = 0.40
    assert!((slots.weight[0] - 0.4).abs() < WEIGHT_EPS, "{slots:?}");
}

/// select_top_slots の同点・全 0・短いスライスの扱い。
#[test]
fn select_top_slots_handles_ties_degenerate_and_short_input() {
    // ─── 同点はレイヤ番号の小さい方を優先（安定ソート）───
    let slots = select_top_slots(&[0.25, 0.25, 0.25, 0.25, 0.25]);
    assert_eq!(
        slots.index,
        [0, 1, 2, 3],
        "同点でレイヤ番号昇順になっていない: {slots:?}"
    );

    // ─── 全 0（縮退）: レイヤ 0 に 1.0 を寄せる（黒落ち防止の規約）───
    let slots = select_top_slots(&[0.0; 7]);
    assert_eq!(slots.index, [0, 0, 0, 0]);
    assert_eq!(slots.weight, [1.0, 0.0, 0.0, 0.0]);
    assert_eq!(slots, BlendSlots::default());

    // ─── 空スライスも縮退扱い（パニックしない）───
    assert_eq!(select_top_slots(&[]), BlendSlots::default());

    // ─── len < 4: 余ったスロットは weight=0 のまま ───
    let slots = select_top_slots(&[1.0, 3.0]);
    assert_eq!(slots.index[0], 1, "{slots:?}");
    assert!((slots.weight[0] - 0.75).abs() < WEIGHT_EPS, "{slots:?}");
    assert!((slots.weight[1] - 0.25).abs() < WEIGHT_EPS, "{slots:?}");
    assert_eq!(slots.weight[2], 0.0);
    assert_eq!(slots.weight[3], 0.0);

    // ─── 負値は 0 として扱われる ───
    let slots = select_top_slots(&[-1.0, 2.0, 0.0]);
    assert_eq!(slots.index[0], 1, "{slots:?}");
    assert!((slots.weight[0] - 1.0).abs() < WEIGHT_EPS, "{slots:?}");
}

/// expand_slots ↔ select_top_slots が往復すること（4 層以内なら情報が落ちない）。
#[test]
fn expand_and_select_slots_round_trip() {
    // ─── 4 層以内の分布は展開 → 再選択で元に戻る ───
    let original = select_top_slots(&[0.0, 0.5, 0.0, 0.0, 0.25, 0.0, 0.25]);
    let dense = expand_slots(&original, TERRAIN_MAX_LAYERS);
    assert_eq!(dense.len(), TERRAIN_MAX_LAYERS);
    assert!((dense.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS);
    assert!((dense[1] - 0.5).abs() < WEIGHT_EPS, "{dense:?}");

    let back = select_top_slots(&dense);
    assert_eq!(back.index, original.index, "往復でスロット構成が変わった");
    for k in 0..TERRAIN_BLEND_SLOTS {
        assert!(
            (back.weight[k] - original.weight[k]).abs() < WEIGHT_EPS,
            "{back:?}"
        );
    }

    // ─── 範囲外 index は無視される（レイヤ定義が減ったケース）───
    let slots = BlendSlots {
        index: [0, 9, 0, 0],
        weight: [0.5, 0.5, 0.0, 0.0],
    };
    let dense = expand_slots(&slots, 3);
    assert_eq!(dense.len(), 3);
    assert!((dense[0] - 0.5).abs() < WEIGHT_EPS, "{dense:?}");
    // レイヤ 9 は範囲外なので落ちる → 総和は 0.5 のまま（正規化はしない）。
    assert!(
        (dense.iter().sum::<f32>() - 0.5).abs() < WEIGHT_EPS,
        "{dense:?}"
    );
}

/// BlendSlots が chunk_data の u8 量子化を経ても往復すること。
#[test]
fn blend_slots_quantization_round_trip_through_chunk() {
    let settings = TerrainSettings::default();
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);

    // レイヤ番号が 4 を超える（可変レイヤ数）ケースも含める。
    let slots = BlendSlots {
        index: [12, 3, 7, 0],
        weight: [0.5, 0.25, 0.125, 0.125],
    };
    chunk.set_paint_slots(2, 4, 6, &slots);
    let back = chunk.paint_slots(2, 4, 6);

    assert_eq!(
        back.index, slots.index,
        "レイヤ番号が往復していない: {back:?}"
    );
    for k in 0..TERRAIN_BLEND_SLOTS {
        assert!(
            (back.weight[k] - slots.weight[k]).abs() < QUANT_EPS,
            "スロット {k} の重みが往復していない: {back:?}"
        );
    }

    // 未書き込みのサンプルは「全 0」のまま（縮退補正を掛けない規約）。
    let empty = chunk.paint_slots(0, 0, 0);
    assert_eq!(
        empty.weight, [0.0; TERRAIN_BLEND_SLOTS],
        "未ペイントが 0 でない: {empty:?}"
    );
}

/// blend_rule_and_paint_all が任意レイヤ数で lerp できること。
#[test]
fn blend_rule_and_paint_all_lerps_dense_weights() {
    // ルールは 6 層ぶん（レイヤ 0 が 100%）、ペイントはレイヤ 5 が 100%。
    let rule = vec![1.0, 0.0, 0.0, 0.0, 0.0, 0.0];
    let paint = BlendSlots {
        index: [5, 0, 0, 0],
        weight: [1.0, 0.0, 0.0, 0.0],
    };

    // ─── 未ペイント（amount=0）: 完全にルール任せ ───
    let w = blend_rule_and_paint_all(&rule, &paint, 0.0);
    assert_eq!(w.len(), rule.len());
    assert!((w[0] - 1.0).abs() < WEIGHT_EPS, "{w:?}");

    // ─── 完全ペイント（amount=1）: レイヤ 5 が 100% ───
    let w = blend_rule_and_paint_all(&rule, &paint, 1.0);
    assert!((w[5] - 1.0).abs() < WEIGHT_EPS, "{w:?}");

    // ─── 中間（amount=0.25）: 線形補間・総和 1 ───
    let w = blend_rule_and_paint_all(&rule, &paint, 0.25);
    assert!((w[0] - 0.75).abs() < WEIGHT_EPS, "{w:?}");
    assert!((w[5] - 0.25).abs() < WEIGHT_EPS, "{w:?}");
    assert!((w.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS, "{w:?}");
}

// ============================================================
//  確保なし版（`*_into`）と Vec 版の等価性
//
//  頂点カラー計算のホットパスは `*_into` 版だけを呼ぶ。両者がビット単位で
//  一致しなくなると、メッシュ出力の指紋テストやペイント高速パスの一致テストが
//  静かに崩れる（＝再メッシュのたびに色がちらつく）。ここでその一致を固定する。
// ============================================================

/// `*_into` 等価性テストで走査するレイヤ数（1 層・2 層・4 層・上限 16 層）。
const INTO_LAYER_COUNTS: [usize; 4] = [1, 2, 4, TERRAIN_MAX_LAYERS];
/// `*_into` 等価性テストで走査するペイント量（未ペイント・中間・完全ペイント）。
const INTO_PAINT_AMOUNTS: [f32; 3] = [0.0, 0.5, 1.0];
/// 等価性テスト用の法線 Y サンプル（平地・中腹・垂直面）。斜度ルールを一通り踏む。
const INTO_NORMAL_YS: [f32; 3] = [1.0, 0.5, 0.0];
/// 等価性テスト用のワールド Y サンプル（低地・地表・高地）。高度ルールを一通り踏む。
const INTO_WORLD_YS: [f32; 3] = [-5.0, 0.0, 5.0];

/// n 層の等価性テスト用レイヤセット（既定ルール 4 層を繰り返して埋める）。
///
/// 既定セットのルール（草地・土・岩・砂）を使い回すことで、
/// 「全層同条件」ではない＝重みに差が出る状況を作る。
fn into_test_layer_set(n: usize) -> TerrainLayerSet {
    let base = TerrainLayerSet::default();
    TerrainLayerSet {
        layers: (0..n)
            .map(|i| TerrainLayer {
                name: format!("layer{i}"),
                ..base.layers[i % base.layers.len()].clone()
            })
            .collect(),
    }
}

/// `expand_slots_into` が `expand_slots` とビット単位で一致すること。
#[test]
fn expand_slots_into_matches_vec_version() {
    // スロット構成をいくつか用意する（重複レイヤ・範囲外レイヤを含む）。
    let cases = [
        BlendSlots::default(),
        BlendSlots {
            index: [0, 1, 2, 3],
            weight: [0.4, 0.3, 0.2, 0.1],
        },
        BlendSlots {
            index: [5, 5, 0, 0],
            weight: [0.5, 0.25, 0.25, 0.0],
        },
        BlendSlots {
            index: [15, 14, 13, 12],
            weight: [0.7, 0.1, 0.1, 0.1],
        },
    ];
    // 出力バッファは常に上限長で確保し、len だけを変えて呼ぶ。
    let mut buf = [0.0f32; TERRAIN_MAX_LAYERS];
    for slots in cases.iter() {
        for &len in INTO_LAYER_COUNTS.iter() {
            let expected = expand_slots(slots, len);
            // 前回の残骸が残っていても結果が変わらないこと（全上書きの担保）を兼ねる。
            buf = [f32::NAN; TERRAIN_MAX_LAYERS];
            expand_slots_into(slots, len, &mut buf);
            for k in 0..len {
                assert_eq!(
                    buf[k].to_bits(),
                    expected[k].to_bits(),
                    "len={len} slot={k} が不一致: {} vs {}",
                    buf[k],
                    expected[k]
                );
            }
        }
    }
}

/// `rule_weights_all_into` が `rule_weights_all` とビット単位で一致すること。
#[test]
fn rule_weights_all_into_matches_vec_version() {
    let mut buf = [0.0f32; TERRAIN_MAX_LAYERS];
    for &n in INTO_LAYER_COUNTS.iter() {
        let set = into_test_layer_set(n);
        for &ny in INTO_NORMAL_YS.iter() {
            for &wy in INTO_WORLD_YS.iter() {
                let expected = set.rule_weights_all(ny, wy);
                buf = [f32::NAN; TERRAIN_MAX_LAYERS];
                let written = set.rule_weights_all_into(ny, wy, &mut buf);
                assert_eq!(written, expected.len(), "n={n}: 書き込み数が不一致");
                for k in 0..written {
                    assert_eq!(
                        buf[k].to_bits(),
                        expected[k].to_bits(),
                        "n={n} ny={ny} wy={wy} layer={k} が不一致: {} vs {}",
                        buf[k],
                        expected[k]
                    );
                }
            }
        }
    }
}

/// `blend_rule_and_paint_all_into` が `blend_rule_and_paint_all` とビット単位で一致すること。
///
/// ルール重みは実際のルール評価から取り、ペイント量 0 / 0.5 / 1 を全て踏む
/// （＝ホットパスが実際に通る組み合わせをそのまま再現する）。
#[test]
fn blend_rule_and_paint_all_into_matches_vec_version() {
    let paint = BlendSlots {
        index: [2, 0, 1, 3],
        weight: [0.5, 0.2, 0.2, 0.1],
    };
    let mut rule_buf = [0.0f32; TERRAIN_MAX_LAYERS];
    let mut blend_buf = [0.0f32; TERRAIN_MAX_LAYERS];

    for &n in INTO_LAYER_COUNTS.iter() {
        let set = into_test_layer_set(n);
        for &ny in INTO_NORMAL_YS.iter() {
            for &amount in INTO_PAINT_AMOUNTS.iter() {
                // ルール重みも `*_into` 経路で作る（ホットパスと同じ組み立て）。
                let written = set.rule_weights_all_into(ny, 0.0, &mut rule_buf);
                let rule = &rule_buf[..written];

                let expected = blend_rule_and_paint_all(rule, &paint, amount);
                blend_buf = [f32::NAN; TERRAIN_MAX_LAYERS];
                blend_rule_and_paint_all_into(rule, &paint, amount, &mut blend_buf);
                for k in 0..written {
                    assert_eq!(
                        blend_buf[k].to_bits(),
                        expected[k].to_bits(),
                        "n={n} ny={ny} amount={amount} layer={k} が不一致: {} vs {}",
                        blend_buf[k],
                        expected[k]
                    );
                }
            }
        }
    }
}

/// DetileMode が JSON からパースでき、未指定は None・不正値はエラーになること。
#[test]
fn detile_mode_parses_from_json() {
    let json = r#"{
        "layers": [
            { "name": "plain" },
            { "name": "s", "detile": "stochastic", "detile_strength": 0.5 },
            { "name": "m", "detile": "macro" },
            { "name": "n", "detile": "none" }
        ]
    }"#;
    let set = TerrainLayerSet::from_json_str(json).expect("detile 付き layers.json のパースに失敗");

    // ─── 未指定は None・強度は既定 1.0 ───
    assert_eq!(set.layers[0].detile, DetileMode::None);
    assert!((set.layers[0].detile_strength - 1.0).abs() < WEIGHT_EPS);

    // ─── 明示指定 ───
    assert_eq!(set.layers[1].detile, DetileMode::Stochastic);
    assert!((set.layers[1].detile_strength - 0.5).abs() < WEIGHT_EPS);
    assert_eq!(set.layers[2].detile, DetileMode::Macro);
    assert_eq!(set.layers[3].detile, DetileMode::None);

    // ─── GPU コードの対応（WGSL uniform の値と一致必須）───
    assert_eq!(DetileMode::None.to_gpu_code(), 0);
    assert_eq!(DetileMode::Stochastic.to_gpu_code(), 1);
    assert_eq!(DetileMode::Macro.to_gpu_code(), 2);

    // ─── 不正値はパースエラー ───
    let bad = r#"{ "layers": [ { "name": "x", "detile": "bogus" } ] }"#;
    assert!(
        TerrainLayerSet::from_json_str(bad).is_err(),
        "不正な detile がエラーにならない"
    );
}

/// 既存の 4 層 layers.json（detile / normal / roughness テクスチャ未指定）が
/// そのまま読め、値が期待どおりであること（**後方互換の要**）。
#[test]
fn legacy_four_layer_json_is_still_readable() {
    // T2 時代に書かれていた形の layers.json（新フィールドは一切無い）。
    let legacy = r#"{
        "layers": [
            { "name": "grass", "base_color": [0.16, 0.38, 0.12], "roughness": 0.95,
              "uv_scale": 0.25, "base_color_texture": "terrain/grass.png",
              "rule": { "slope_min_deg": 0.0, "slope_max_deg": 22.0, "slope_fade_deg": 10.0 } },
            { "name": "dirt",  "base_color": [0.33, 0.22, 0.12],
              "rule": { "slope_min_deg": 18.0, "slope_max_deg": 42.0, "slope_fade_deg": 10.0 } },
            { "name": "rock",  "base_color": [0.40, 0.40, 0.42],
              "rule": { "slope_min_deg": 38.0, "slope_fade_deg": 10.0, "priority": 1.2 } },
            { "name": "sand",  "base_color": [0.76, 0.68, 0.45],
              "rule": { "slope_max_deg": 25.0, "slope_fade_deg": 10.0,
                        "height_max": -2.0, "height_fade": 1.0, "priority": 1.5 } }
        ]
    }"#;
    let set = TerrainLayerSet::from_json_str(legacy).expect("旧 layers.json が読めない");

    assert_eq!(set.layers.len(), 4);
    assert_eq!(set.layers[0].name, "grass");
    assert_eq!(
        set.layers[0].base_color_texture.as_deref(),
        Some("terrain/grass.png")
    );
    assert!((set.layers[0].roughness - 0.95).abs() < WEIGHT_EPS);
    assert!((set.layers[2].rule.priority - 1.2).abs() < WEIGHT_EPS);

    // ─── 新フィールドはすべて既定値へフォールバックする ───
    for layer in &set.layers {
        assert_eq!(layer.detile, DetileMode::None, "{}", layer.name);
        assert!(
            (layer.detile_strength - 1.0).abs() < WEIGHT_EPS,
            "{}",
            layer.name
        );
        assert!(layer.normal_texture.is_none(), "{}", layer.name);
        assert!(layer.roughness_texture.is_none(), "{}", layer.name);
    }

    // ─── 旧来どおりの塗り分け（平地=grass / 急斜面=rock）が保たれる ───
    let flat = set.rule_weights_all(1.0, 0.0);
    assert!(flat[0] > 0.9, "平地が草地でない: {flat:?}");
    let steep = set.rule_weights_all(0.0, 0.0);
    assert!(steep[2] > 0.9, "急斜面が岩でない: {steep:?}");
}

/// 7 層は 7 層として読め、17 層は TERRAIN_MAX_LAYERS へ切り詰められること。
#[test]
fn layer_set_supports_many_layers_and_truncates_at_max() {
    // ─── 7 層 ───
    let set = TerrainLayerSet {
        layers: (0..7).map(|i| named_layer(&format!("L{i}"))).collect(),
    };
    let json = serde_json::to_string(&set).expect("直列化に失敗");
    let back = TerrainLayerSet::from_json_str(&json).expect("7 層の読み込みに失敗");
    assert_eq!(back.layers.len(), 7, "7 層が保たれていない");
    assert_eq!(back.layers[6].name, "L6");
    // ルール重みも 7 要素返る。
    assert_eq!(back.rule_weights_all(1.0, 0.0).len(), 7);

    // ─── 17 層 → 16 層へ切り詰め ───
    let set = TerrainLayerSet {
        layers: (0..TERRAIN_MAX_LAYERS + 1)
            .map(|i| named_layer(&format!("L{i}")))
            .collect(),
    };
    let json = serde_json::to_string(&set).expect("直列化に失敗");
    let back = TerrainLayerSet::from_json_str(&json).expect("17 層の読み込みに失敗");
    assert_eq!(
        back.layers.len(),
        TERRAIN_MAX_LAYERS,
        "16 層へ切り詰められていない"
    );
    assert_eq!(back.active_count(), TERRAIN_MAX_LAYERS);
}

/// チャンクパレット決定（terrain_mesh_build.rs のパス 2）の CPU 参照。
///
/// 頂点ごとの密重みをチャンク合計して上位 4 層を選ぶ、という手順そのものを固定する。
/// 実関数は engine 層（terrain_mesh_build）にありここからは呼べないため、
/// 同じ 2 段階（合計 → select_top_slots）をここで再現して期待値を固定する。
#[test]
fn chunk_palette_selects_dominant_layers_from_vertex_weights() {
    // ─── 人工的な重み分布: 6 層・4 頂点 ───
    //   レイヤ 1 と 4 はどの頂点でも強く、レイヤ 0/2 は弱く、
    //   レイヤ 5 は 1 頂点だけ局所的に立つ（＝落ちる可能性がある層）。
    const LAYER_COUNT: usize = 6;
    let vertex_weights: [[f32; LAYER_COUNT]; 4] = [
        [0.05, 0.50, 0.00, 0.15, 0.30, 0.00],
        [0.00, 0.60, 0.05, 0.10, 0.25, 0.00],
        [0.05, 0.45, 0.00, 0.20, 0.30, 0.00],
        [0.00, 0.10, 0.00, 0.05, 0.05, 0.80], // 局所的にレイヤ 5 が支配
    ];

    // ─── チャンク合計 ───
    let mut chunk_total = [0.0f32; LAYER_COUNT];
    for w in &vertex_weights {
        for k in 0..LAYER_COUNT {
            chunk_total[k] += w[k];
        }
    }
    // 合計: [0.10, 1.65, 0.05, 0.50, 0.90, 0.80]

    // ─── パレット = 上位 4 層（1, 4, 5, 3）───
    let palette = select_top_slots(&chunk_total);
    assert_eq!(
        palette.index,
        [1, 4, 5, 3],
        "パレット選択が期待と異なる: {palette:?}"
    );

    // ─── パス 2 相当: 頂点 0 の重みをパレットへ射影して再正規化 ───
    let v = &vertex_weights[0];
    let mut w = [0.0f32; TERRAIN_BLEND_SLOTS];
    let mut sum = 0.0f32;
    for slot in 0..TERRAIN_BLEND_SLOTS {
        w[slot] = v[palette.index[slot] as usize];
        sum += w[slot];
    }
    assert!(sum > 0.0);
    for x in w.iter_mut() {
        *x /= sum;
    }
    // 頂点 0 は [L1=0.50, L4=0.30, L5=0.00, L3=0.15] → 総和 0.95 で正規化。
    assert!((w.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS, "{w:?}");
    assert!((w[0] - 0.50 / 0.95).abs() < WEIGHT_EPS, "{w:?}");
    assert!(w[2] < WEIGHT_EPS, "レイヤ 5 は頂点 0 では 0 のはず: {w:?}");

    // ─── 設計上の限界の明示: レイヤ 0/2 はパレットから落ちる ───
    assert!(
        !palette.index.contains(&0),
        "弱い層が残っている: {palette:?}"
    );
    assert!(
        !palette.index.contains(&2),
        "弱い層が残っている: {palette:?}"
    );
}

// ============================================================
//  新規レイヤ追加による自動ペイント汚染（不具合再発防止）
// ============================================================

/// エディタ「レイヤ追加」で作られる新規レイヤの優先度。
/// エディタ側定数 `TerrainLayerDefaults.NewLayerPriority`（C#）と一致必須。
const NEW_LAYER_PRIORITY: f32 = 0.0;

/// 自動ペイントで「全域に効く下地」として使うレイヤの優先度（テスト用）。
const BASE_LAYER_PRIORITY: f32 = 2.0;

/// 平地（法線 +Y）を表す法線 Y 成分。
const FLAT_NORMAL_Y: f32 = 1.0;
/// テスト評価に使うワールド高度（どのレイヤの高度ウィンドウにも入る値）。
const TEST_WORLD_Y: f32 = 0.0;

/// テクスチャ未設定の新規レイヤを模した定義を作る。
///
/// ルールは `LayerRule::default()`（斜度 0〜90 度・高度無制限）のままで、
/// 優先度だけを引数で与える。エディタが作る `{"name": "new_layer_1"}` と
/// 同じ「全フィールド既定値」のレイヤを表現している。
fn placeholder_layer(name: &str, priority: f32) -> TerrainLayer {
    TerrainLayer {
        name: name.to_string(),
        rule: LayerRule {
            priority,
            ..LayerRule::default()
        },
        ..TerrainLayer::default()
    }
}

/// 【不具合再発防止】既定ルールのまま優先度 1 でレイヤを追加すると、
/// 地形全域の自動ペイントがその無地レイヤに食われる（＝灰色化する）こと。
///
/// このテストは「壊れた側」の挙動を固定するためのものではなく、
/// 次のテスト（優先度 0 なら食われない）と対にして
/// **エディタが新規レイヤへ優先度 0 を与えなければならない理由**を明文化する。
#[test]
fn placeholder_layer_with_default_priority_steals_auto_paint_weight() {
    let set = TerrainLayerSet {
        layers: vec![
            // 全域に効く下地（テクスチャ付きレイヤ相当）。
            placeholder_layer("beach", BASE_LAYER_PRIORITY),
            // 「レイヤ追加」直後の無地レイヤ 2 枚（優先度は既定の 1.0）。
            placeholder_layer("new_layer_1", 1.0),
            placeholder_layer("new_layer_2", 1.0),
        ],
    };

    let w = set.rule_weights_all(FLAT_NORMAL_Y, TEST_WORLD_Y);
    // 2 : 1 : 1 → 下地は半分しか残らない。
    assert!(
        (w[0] - 0.5).abs() < WEIGHT_EPS,
        "下地レイヤが無地レイヤに食われていない前提が崩れた: {w:?}"
    );
    assert!(
        w[1] > WEIGHT_EPS && w[2] > WEIGHT_EPS,
        "無地レイヤが自動ペイントに載っていない: {w:?}"
    );
}

/// 優先度 0（エディタが新規レイヤへ与える値）のレイヤは、
/// 既定ルール（全域で成立）のままでも自動ペイントへ一切載らないこと。
#[test]
fn new_layer_priority_keeps_auto_paint_untouched() {
    let set = TerrainLayerSet {
        layers: vec![
            placeholder_layer("beach", BASE_LAYER_PRIORITY),
            placeholder_layer("new_layer_1", NEW_LAYER_PRIORITY),
            placeholder_layer("new_layer_2", NEW_LAYER_PRIORITY),
        ],
    };

    let w = set.rule_weights_all(FLAT_NORMAL_Y, TEST_WORLD_Y);
    assert!(
        (w[0] - 1.0).abs() < WEIGHT_EPS,
        "新規レイヤ追加で下地の自動ペイント重みが変わった: {w:?}"
    );
    for (i, &v) in w.iter().enumerate().skip(1) {
        assert!(
            v < WEIGHT_EPS,
            "優先度 0 のレイヤ {i} が自動ペイントに載っている: {w:?}"
        );
    }
}

/// 優先度 0 でも **手動ペイントなら** そのレイヤを 100% まで塗れること。
///
/// 「自動ペイントから外す」ことと「そのレイヤを使えなくする」ことは別物であり、
/// 前者だけを満たしているかを確認する。ペイント量 1.0（＝手動で塗り切った状態）で
/// 合成すると、ルール重みは完全に無視されて手ペイントのスロットが残る。
#[test]
fn priority_zero_layer_is_still_paintable_by_hand() {
    /// 手で塗る対象レイヤ番号（優先度 0 の新規レイヤ）。
    const HAND_PAINTED: usize = 1;
    /// 塗り切った状態のペイント量。
    const FULL_PAINT_AMOUNT: f32 = 1.0;

    let set = TerrainLayerSet {
        layers: vec![
            placeholder_layer("beach", BASE_LAYER_PRIORITY),
            placeholder_layer("new_layer_1", NEW_LAYER_PRIORITY),
            placeholder_layer("new_layer_2", NEW_LAYER_PRIORITY),
        ],
    };

    // 自動ペイント（ルール）だけの重み。
    let rule = set.rule_weights_all(FLAT_NORMAL_Y, TEST_WORLD_Y);

    // 手動ペイント: 対象レイヤを強度 1 で塗った状態のスロット。
    let mut paint = BlendSlots::default();
    paint.index[0] = HAND_PAINTED as u32;
    paint.weight[0] = 1.0;
    for slot in 1..TERRAIN_BLEND_SLOTS {
        paint.weight[slot] = 0.0;
    }

    let blended = blend_rule_and_paint_all(&rule, &paint, FULL_PAINT_AMOUNT);
    assert!(
        (blended[HAND_PAINTED] - 1.0).abs() < WEIGHT_EPS,
        "優先度 0 のレイヤが手動ペイントで塗れていない: {blended:?}"
    );
    assert!(
        blended[0] < WEIGHT_EPS,
        "手動ペイント量 1.0 なのにルール重みが残っている: {blended:?}"
    );
}
