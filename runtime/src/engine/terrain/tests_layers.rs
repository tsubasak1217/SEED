// ============================================================
//  terrain/tests_layers.rs — レイヤブレンド（T2）のユニットテスト
//
//  1. レイヤ重みの正規化（縮退ケース含む）
//  2. 斜度／高度ルールの期待値
//  3. ルール自動生成と手ペイントの共存（ペイント優先ブレンド）
//  4. ペイントブラシの重み押し上げ・正規化減衰
//  5. tvox v2 ラウンドトリップと v1→v2 後方互換
//  6. triplanar ブレンドの CPU 参照（シェーダ式との対応）
// ============================================================

use std::collections::HashMap;

use super::brush::SphereBrush;
use super::chunk_coord::ChunkCoord;
use super::chunk_data::TerrainChunkData;
use super::layers::{
    blend_rule_and_paint, dequantize_weight, normalize_weights, quantize_weight, LayerRule,
    LayerWeights, TerrainLayer, TerrainLayerSet, TERRAIN_LAYER_COUNT,
};
use super::paint::{apply_paint, PaintField};
use super::settings::TerrainSettings;
use super::tvox::{read_chunk, write_chunk, write_chunk_v1};

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
        name: String::new(),
        base_color: [1.0, 1.0, 1.0],
        roughness: 1.0,
        metallic: 0.0,
        uv_scale: 1.0,
        base_color_texture: None,
        rule: LayerRule::default(),
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
    normalize_weights(&mut w);
    assert!((w.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS);
    assert!((w[0] - 0.25).abs() < WEIGHT_EPS, "w={w:?}");
    assert!((w[1] - 0.75).abs() < WEIGHT_EPS, "w={w:?}");

    // ─── 負値は 0 に潰される ───
    let mut w = [-5.0, 1.0, 0.0, 0.0];
    normalize_weights(&mut w);
    assert_eq!(w[0], 0.0);
    assert!((w[1] - 1.0).abs() < WEIGHT_EPS);

    // ─── 縮退（全 0）: レイヤ 0 を下地に敷く（黒落ち防止の規約）───
    let mut w: LayerWeights = [0.0; TERRAIN_LAYER_COUNT];
    normalize_weights(&mut w);
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
#[test]
fn rule_weights_match_expected_slope_selection() {
    let set = two_layer_set();

    // ─── 真上向き法線（斜度 0 度）→ 平地レイヤ 100% ───
    let w = set.rule_weights(1.0, 0.0);
    assert!((w[0] - 1.0).abs() < WEIGHT_EPS, "flat: {w:?}");
    assert!(w[1] < WEIGHT_EPS, "flat: {w:?}");

    // ─── 水平向き法線（斜度 90 度）→ 急斜面レイヤ 100% ───
    let w = set.rule_weights(0.0, 0.0);
    assert!(w[0] < WEIGHT_EPS, "steep: {w:?}");
    assert!((w[1] - 1.0).abs() < WEIGHT_EPS, "steep: {w:?}");

    // ─── 斜度 45 度はどちらのウィンドウにも入らない（縮退→レイヤ 0 へ寄る）───
    let w = set.rule_weights(std::f32::consts::FRAC_1_SQRT_2, 0.0);
    assert_eq!(w, [1.0, 0.0, 0.0, 0.0], "45deg fallback: {w:?}");

    // ─── 下向き法線（天井）も |n.y| で同じ斜度として扱う ───
    assert_eq!(set.rule_weights(-1.0, 0.0), set.rule_weights(1.0, 0.0));
}

/// 高度ウィンドウが効くこと（低地だけに載るレイヤ）。
#[test]
fn rule_weights_respect_height_window() {
    let set = TerrainLayerSet {
        layers: vec![
            TerrainLayer {
                name: "base".to_string(),
                rule: LayerRule { priority: 1.0, ..LayerRule::default() },
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
    let w = set.rule_weights(1.0, -5.0);
    assert!((w[0] - 0.25).abs() < WEIGHT_EPS, "lowland: {w:?}");
    assert!((w[1] - 0.75).abs() < WEIGHT_EPS, "lowland: {w:?}");

    // ─── Y = +5（高所）: lowland のウィンドウ外 → base 100% ───
    let w = set.rule_weights(1.0, 5.0);
    assert!((w[0] - 1.0).abs() < WEIGHT_EPS, "highland: {w:?}");
}

/// 既定レイヤセット（layers.json 不在時のフォールバック）が
/// 「平地は草・急斜面は岩」になっていること（目視スクリーンショットの期待値と対応）。
#[test]
fn default_layer_set_paints_grass_on_flat_and_rock_on_steep() {
    let set = TerrainLayerSet::default();
    assert_eq!(set.active_count(), TERRAIN_LAYER_COUNT);

    // ─── 平地（斜度 0・高度 0）: レイヤ 0（grass）が支配的 ───
    let flat = set.rule_weights(1.0, 0.0);
    assert!(flat[0] > 0.9, "平地が草地になっていない: {flat:?}");

    // ─── 急斜面（斜度 90 度・高度 0）: レイヤ 2（rock）が支配的 ───
    let steep = set.rule_weights(0.0, 0.0);
    assert!(steep[2] > 0.9, "急斜面が岩になっていない: {steep:?}");
}

/// layers.json 相当の JSON がパースでき、4 層を超える定義は切り詰められること。
#[test]
fn layer_set_json_parses_and_truncates() {
    let json = r#"{
        "layers": [
            { "name": "a", "base_color": [1.0, 0.0, 0.0] },
            { "name": "b", "base_color": [0.0, 1.0, 0.0], "rule": { "slope_min_deg": 40.0 } },
            { "name": "c" }, { "name": "d" }, { "name": "e" }
        ]
    }"#;
    let set = TerrainLayerSet::from_json_str(json).expect("layers.json のパースに失敗");
    assert_eq!(set.layers.len(), TERRAIN_LAYER_COUNT, "4 層へ切り詰められていない");
    assert_eq!(set.layers[0].name, "a");
    // 省略されたフィールドは serde default が入る。
    assert_eq!(set.layers[2].base_color, [0.5, 0.5, 0.5]);
    assert!((set.layers[1].rule.slope_min_deg - 40.0).abs() < WEIGHT_EPS);
}

// ============================================================
//  3. ルール自動生成と手ペイントの共存
// ============================================================

/// ルール自動生成と手ペイントの共存（ペイント優先ブレンド）が仕様どおりであること。
#[test]
fn paint_blends_over_rule_without_overwriting_when_unpainted() {
    let rule = [1.0, 0.0, 0.0, 0.0];
    let paint = [0.0, 1.0, 0.0, 0.0];

    // ─── 未ペイント（amount=0）: 完全にルール任せ（自動下地が生き続ける）───
    let w = blend_rule_and_paint(rule, paint, 0.0);
    assert!((w[0] - 1.0).abs() < WEIGHT_EPS, "{w:?}");

    // ─── 完全ペイント（amount=1）: ルールを無視して手描き優先 ───
    let w = blend_rule_and_paint(rule, paint, 1.0);
    assert!((w[1] - 1.0).abs() < WEIGHT_EPS, "{w:?}");

    // ─── 中間（amount=0.25）: 線形補間（ブラシ縁のフェード）───
    let w = blend_rule_and_paint(rule, paint, 0.25);
    assert!((w[0] - 0.75).abs() < WEIGHT_EPS, "{w:?}");
    assert!((w[1] - 0.25).abs() < WEIGHT_EPS, "{w:?}");
    assert!((w.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS);
}

// ============================================================
//  4. ペイントブラシ
// ============================================================

/// ペイント用の最小 PaintField 実装（単一チャンク・境界重複なし）。
struct PaintView<'a> {
    settings: &'a TerrainSettings,
    chunks: &'a mut HashMap<ChunkCoord, TerrainChunkData>,
}

impl<'a> PaintView<'a> {
    /// グローバルサンプル座標 → (チャンク座標, ローカル添字)。
    fn split(&self, gx: i32, gy: i32, gz: i32) -> (ChunkCoord, usize, usize, usize) {
        let cells = self.settings.chunk_cells as i32;
        (
            ChunkCoord::new(gx.div_euclid(cells), gy.div_euclid(cells), gz.div_euclid(cells)),
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
    fn read_paint_global(&self, gx: i32, gy: i32, gz: i32) -> (LayerWeights, f32) {
        let (c, lx, ly, lz) = self.split(gx, gy, gz);
        match self.chunks.get(&c) {
            Some(chunk) => (chunk.paint_weights(lx, ly, lz), chunk.paint_amount(lx, ly, lz)),
            None => ([0.0; TERRAIN_LAYER_COUNT], 0.0),
        }
    }
    fn write_paint_global(&mut self, gx: i32, gy: i32, gz: i32, w: LayerWeights, amount: f32) {
        let (c, lx, ly, lz) = self.split(gx, gy, gz);
        if let Some(chunk) = self.chunks.get_mut(&c) {
            chunk.set_paint_weights(lx, ly, lz, w);
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
    chunks.insert(ChunkCoord::new(0, 0, 0), TerrainChunkData::new_filled(&settings, 0.0));

    // チャンク中央にレイヤ 1 を強くペイントする（dt=1.0・中心 falloff=1 → delta=1）。
    let brush = SphereBrush { center: PAINT_CENTER, radius: PAINT_RADIUS, strength: 1.0 };
    let affected = {
        let mut view = PaintView { settings: &settings, chunks: &mut chunks };
        apply_paint(&mut view, &brush, 1, 1.0)
    };
    assert!(!affected.is_empty(), "ペイントがどのチャンクにも届いていない");

    // ─── ブラシ中心のサンプルはレイヤ 1 が支配的・総和 1 ───
    let chunk = chunks.get(&ChunkCoord::new(0, 0, 0)).unwrap();
    let idx = |v: f32| (v / settings.voxel_size) as usize;
    let (cx, cy, cz) = (idx(PAINT_CENTER[0]), idx(PAINT_CENTER[1]), idx(PAINT_CENTER[2]));
    let w = chunk.paint_weights(cx, cy, cz);
    assert!(w[1] > 0.99, "中心のレイヤ1重みが弱い: {w:?}");
    assert!((w.iter().sum::<f32>() - 1.0).abs() < QUANT_EPS, "{w:?}");
    assert!(chunk.paint_amount(cx, cy, cz) > 0.99, "中心のペイント量が弱い");

    // ─── ブラシ半径の外は未ペイントのまま（ルール任せ）───
    assert_eq!(chunk.paint_amount(0, 0, 0), 0.0);

    // ─── 不正なレイヤ番号は無操作 ───
    let noop = {
        let mut view = PaintView { settings: &settings, chunks: &mut chunks };
        apply_paint(&mut view, &brush, TERRAIN_LAYER_COUNT, 1.0)
    };
    assert!(noop.is_empty(), "範囲外レイヤ番号で書き込みが起きている");
}

// ============================================================
//  5. tvox v2 / v1 後方互換
// ============================================================

/// tvox v2 がスプラットを含めてビット一致でラウンドトリップすること。
#[test]
fn tvox_v2_round_trip_includes_splat() {
    let settings = TerrainSettings::default();
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
    chunk.set_sample(1, 2, 3, -1.25);
    chunk.set_paint_weights(1, 2, 3, [0.0, 1.0, 0.0, 0.0]);
    chunk.set_paint_amount(1, 2, 3, 1.0);
    chunk.set_paint_weights(4, 5, 6, [0.5, 0.5, 0.0, 0.0]);
    chunk.set_paint_amount(4, 5, 6, 0.5);

    let coord = ChunkCoord::new(-2, 0, 7);
    let bytes = write_chunk(&chunk, coord, &settings);
    let (restored, restored_coord) = read_chunk(&bytes).expect("v2 読み込みに失敗");

    assert_eq!(restored_coord, coord);
    assert_eq!(restored.raw_density(), chunk.raw_density());
    assert_eq!(restored.raw_paint(), chunk.raw_paint());
    assert_eq!(restored.raw_paint_amount(), chunk.raw_paint_amount());
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
    chunk.set_paint_weights(3, 3, 3, [0.0, 0.0, 1.0, 0.0]);
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
        restored.raw_paint().iter().all(|w| w.iter().all(|&q| q == 0)),
        "v1 読み込みでペイント重みが 0 になっていない"
    );

    // 未ペイントなので、最終重みは 100% ルール由来になる。
    let set = two_layer_set();
    let rule = set.rule_weights(1.0, 0.0);
    let final_w = blend_rule_and_paint(
        rule,
        restored.paint_weights(3, 3, 3),
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
        assert!((w.iter().sum::<f32>() - 1.0).abs() < WEIGHT_EPS, "n={n:?} w={w:?}");
    }

    // ─── 縮退（0 ベクトル）: XZ 平面（真上投影）へフォールバック ───
    assert_eq!(
        triplanar_blend_weights_ref([0.0, 0.0, 0.0], TRIPLANAR_SHARPNESS),
        [0.0, 1.0, 0.0]
    );
}

/// 単色レイヤの albedo 合成（シェーダの Σ w_i * c_i と同じ式）。
fn blend_layer_albedo_ref(set: &TerrainLayerSet, w: LayerWeights) -> [f32; 3] {
    let mut out = [0.0f32; 3];
    for (i, layer) in set.layers.iter().take(TERRAIN_LAYER_COUNT).enumerate() {
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
    let w = set.rule_weights(1.0, 0.0);
    let albedo = blend_layer_albedo_ref(&set, w);
    assert!((albedo[0] - 1.0).abs() < WEIGHT_EPS, "flat albedo={albedo:?}");
    assert!(albedo[2] < WEIGHT_EPS, "flat albedo={albedo:?}");

    // ─── 崖（斜度 90）: レイヤ 1（青）が 100% ───
    let w = set.rule_weights(0.0, 0.0);
    let albedo = blend_layer_albedo_ref(&set, w);
    assert!(albedo[0] < WEIGHT_EPS, "steep albedo={albedo:?}");
    assert!((albedo[2] - 1.0).abs() < WEIGHT_EPS, "steep albedo={albedo:?}");

    // ─── 半々に手ペイントした場合は色が中間になる ───
    let w = blend_rule_and_paint([1.0, 0.0, 0.0, 0.0], [0.0, 1.0, 0.0, 0.0], 0.5);
    let albedo = blend_layer_albedo_ref(&set, w);
    assert!((albedo[0] - 0.5).abs() < WEIGHT_EPS, "mix albedo={albedo:?}");
    assert!((albedo[2] - 0.5).abs() < WEIGHT_EPS, "mix albedo={albedo:?}");
}
