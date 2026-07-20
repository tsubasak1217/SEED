// ============================================================
//  terrain/layers.rs — 地形マテリアルレイヤ定義（データドリブン）
//
//  【責務】
//    地形の「スプラットレイヤ」（草地・岩・土・砂 …）の定義と、
//    斜度／高度ルールからレイヤ重みを求める純粋関数を提供する。
//    ファイル IO・GPU・ECS への依存は一切持たない（JSON 文字列 in / 構造体 out）。
//
//  【なぜレイヤ数を定数で固定するか】
//    レイヤ重みは頂点属性（Vertex.color の RGBA 4 成分）で GPU へ渡す。
//    頂点フォーマットを一切変更せずにスプラットを運ぶための設計判断であり、
//    その結果 GPU が同時にブレンドできるレイヤ数は 4 に固定される。
//    JSON 側に 4 を超えるレイヤが書かれていた場合は先頭 4 層だけを採用する
//    （エラーにはしない＝データ差し替えでの試行錯誤を妨げない）。
//
//  【重みの規約】
//    weights[i] は 0..=1 で、常に総和 1 に正規化されている（全レイヤの
//    重みが 0 になる縮退ケースではレイヤ 0 に 1.0 を寄せる）。
// ============================================================

use serde::{Deserialize, Serialize};

// ─── レイヤ数（GPU ブレンド予算。マジックナンバー禁止のため定数化）───────────

/// 同時にブレンドできる地形レイヤ数。
///
/// 頂点カラー（vec4）にスプラット重みを載せる設計上の上限であり、
/// WGSL 側 `TERRAIN_LAYER_COUNT`（terrain_gbuffer_write.wgsl）と一致必須。
/// 一致は `tests.rs::terrain_layer_count_matches_shader` が検証する。
pub const TERRAIN_LAYER_COUNT: usize = 4;

/// レイヤ重みベクトル（総和 1 に正規化済み）。
pub type LayerWeights = [f32; TERRAIN_LAYER_COUNT];

// ─── ルール既定値（名前付き const）─────────────────────────────────────────

/// 斜度ウィンドウ下限の既定値（度）。0 度 = 完全な平地。
const DEFAULT_SLOPE_MIN_DEG: f32 = 0.0;
/// 斜度ウィンドウ上限の既定値（度）。90 度 = 垂直な崖。
const DEFAULT_SLOPE_MAX_DEG: f32 = 90.0;
/// 斜度ウィンドウの縁をぼかす幅の既定値（度）。0 だと境目がハードエッジになる。
const DEFAULT_SLOPE_FADE_DEG: f32 = 8.0;
/// 高度ウィンドウ下限の既定値（メートル）。実質「制限なし」の下限。
const DEFAULT_HEIGHT_MIN: f32 = -1.0e6;
/// 高度ウィンドウ上限の既定値（メートル）。実質「制限なし」の上限。
const DEFAULT_HEIGHT_MAX: f32 = 1.0e6;
/// 高度ウィンドウの縁をぼかす幅の既定値（メートル）。
const DEFAULT_HEIGHT_FADE: f32 = 2.0;
/// ルールの基礎重み（優先度）の既定値。大きいほどそのレイヤが勝つ。
const DEFAULT_PRIORITY: f32 = 1.0;

// ─── レイヤ既定値 ───────────────────────────────────────────────────────────

/// レイヤ base_color の既定値（中間グレー）。テクスチャ未指定でも視認できる。
const DEFAULT_LAYER_BASE_COLOR: [f32; 3] = [0.5, 0.5, 0.5];
/// レイヤ roughness の既定値（自然物は概ね粗い）。
const DEFAULT_LAYER_ROUGHNESS: f32 = 0.9;
/// レイヤ metallic の既定値（自然物は非金属）。
const DEFAULT_LAYER_METALLIC: f32 = 0.0;
/// レイヤ UV スケールの既定値（1 テクセル周期あたりのワールドメートル逆数）。
/// triplanar は world_pos * uv_scale を UV に使うため、0.25 なら 4m でタイル 1 周。
const DEFAULT_LAYER_UV_SCALE: f32 = 0.25;

/// ルール評価で「重み無し」とみなす下限。数値誤差で微小な重みが残るのを潰す。
const RULE_WEIGHT_EPSILON: f32 = 1.0e-6;

// ─── serde default 用関数 ───────────────────────────────────────────────────

fn default_slope_min_deg() -> f32 { DEFAULT_SLOPE_MIN_DEG }
fn default_slope_max_deg() -> f32 { DEFAULT_SLOPE_MAX_DEG }
fn default_slope_fade_deg() -> f32 { DEFAULT_SLOPE_FADE_DEG }
fn default_height_min() -> f32 { DEFAULT_HEIGHT_MIN }
fn default_height_max() -> f32 { DEFAULT_HEIGHT_MAX }
fn default_height_fade() -> f32 { DEFAULT_HEIGHT_FADE }
fn default_priority() -> f32 { DEFAULT_PRIORITY }
fn default_layer_base_color() -> [f32; 3] { DEFAULT_LAYER_BASE_COLOR }
fn default_layer_roughness() -> f32 { DEFAULT_LAYER_ROUGHNESS }
fn default_layer_metallic() -> f32 { DEFAULT_LAYER_METALLIC }
fn default_layer_uv_scale() -> f32 { DEFAULT_LAYER_UV_SCALE }

// ============================================================
//  LayerRule — 斜度／高度による自動下地生成ルール
// ============================================================

/// 1 レイヤの「どこに自動で載るか」を表すルール。
///
/// 斜度ウィンドウ（度）と高度ウィンドウ（ワールド Y メートル）の積に
/// `priority` を掛けたものが、そのレイヤの生の重みになる。
/// ウィンドウの縁は `*_fade` 幅で smoothstep 補間され、層境界が滑らかに溶ける。
///
/// 例（岩）: slope_min=30, slope_max=90, slope_fade=12
///   → 30 度より急な面でだんだん岩が強くなる。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct LayerRule {
    /// 斜度ウィンドウ下限（度）。0 = 水平面。
    #[serde(default = "default_slope_min_deg")]
    pub slope_min_deg: f32,
    /// 斜度ウィンドウ上限（度）。90 = 垂直面。
    #[serde(default = "default_slope_max_deg")]
    pub slope_max_deg: f32,
    /// 斜度ウィンドウ両端のぼかし幅（度）。
    #[serde(default = "default_slope_fade_deg")]
    pub slope_fade_deg: f32,
    /// 高度ウィンドウ下限（ワールド Y・メートル）。
    #[serde(default = "default_height_min")]
    pub height_min: f32,
    /// 高度ウィンドウ上限（ワールド Y・メートル）。
    #[serde(default = "default_height_max")]
    pub height_max: f32,
    /// 高度ウィンドウ両端のぼかし幅（メートル）。
    #[serde(default = "default_height_fade")]
    pub height_fade: f32,
    /// 基礎重み（優先度）。同じ条件で複数レイヤが立つとき、この比で配分される。
    #[serde(default = "default_priority")]
    pub priority: f32,
}

impl Default for LayerRule {
    fn default() -> Self {
        Self {
            slope_min_deg:  DEFAULT_SLOPE_MIN_DEG,
            slope_max_deg:  DEFAULT_SLOPE_MAX_DEG,
            slope_fade_deg: DEFAULT_SLOPE_FADE_DEG,
            height_min:     DEFAULT_HEIGHT_MIN,
            height_max:     DEFAULT_HEIGHT_MAX,
            height_fade:    DEFAULT_HEIGHT_FADE,
            priority:       DEFAULT_PRIORITY,
        }
    }
}

impl LayerRule {
    /// 斜度（度）・高度（ワールド Y）に対する生の重みを返す（正規化前・0 以上）。
    pub fn evaluate(&self, slope_deg: f32, height: f32) -> f32 {
        // ─── 斜度ウィンドウ × 高度ウィンドウ × 優先度 ───
        let s = window(slope_deg, self.slope_min_deg, self.slope_max_deg, self.slope_fade_deg);
        let h = window(height, self.height_min, self.height_max, self.height_fade);
        (self.priority.max(0.0) * s * h).max(0.0)
    }
}

// ============================================================
//  TerrainLayer — 1 レイヤのマテリアル定義
// ============================================================

/// 地形マテリアルレイヤ 1 枚の定義。
///
/// テクスチャは任意（`base_color_texture` が None なら `base_color` の単色レイヤ）。
/// テクスチャの解決（アセットルート基準の相対パス→実ファイル）はエンジン層の責務で、
/// 本構造体はパス文字列をそのまま保持するだけ（純粋データ層）。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TerrainLayer {
    /// レイヤ名（エディタのレイヤ選択 UI に出す表示名）。
    pub name: String,
    /// ベースカラー係数（リニア RGB）。テクスチャがあれば乗算される。
    #[serde(default = "default_layer_base_color")]
    pub base_color: [f32; 3],
    /// ラフネス（0=鏡面, 1=完全拡散）。
    #[serde(default = "default_layer_roughness")]
    pub roughness: f32,
    /// メタリック（0=非金属, 1=金属）。
    #[serde(default = "default_layer_metallic")]
    pub metallic: f32,
    /// triplanar UV スケール（ワールド 1m あたりの UV 進み量）。
    #[serde(default = "default_layer_uv_scale")]
    pub uv_scale: f32,
    /// ベースカラーテクスチャのアセット相対パス（省略時は単色レイヤ）。
    #[serde(default)]
    pub base_color_texture: Option<String>,
    /// 斜度／高度による自動下地生成ルール。
    #[serde(default)]
    pub rule: LayerRule,
}

// ============================================================
//  TerrainLayerSet — レイヤ定義一式（layers.json の中身）
// ============================================================

/// 地形レイヤ定義の集合。`assets/terrain/layers.json` の直列化対象。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TerrainLayerSet {
    /// レイヤ定義。先頭から最大 TERRAIN_LAYER_COUNT 層までが GPU へ載る。
    pub layers: Vec<TerrainLayer>,
}

impl TerrainLayerSet {
    /// JSON 文字列からレイヤ定義を読む。
    ///
    /// レイヤ数が TERRAIN_LAYER_COUNT を超える場合は先頭 4 層へ切り詰める
    /// （データ差し替えでの試行錯誤を止めないため、エラーにはしない）。
    /// レイヤが 1 つも無い場合は既定セットへフォールバックする。
    pub fn from_json_str(s: &str) -> Result<Self, serde_json::Error> {
        let mut set: TerrainLayerSet = serde_json::from_str(s)?;
        set.layers.truncate(TERRAIN_LAYER_COUNT);
        if set.layers.is_empty() {
            set = Self::default();
        }
        Ok(set)
    }

    /// 実際に GPU へ載るレイヤ数（= min(定義数, TERRAIN_LAYER_COUNT)）。
    pub fn active_count(&self) -> usize {
        self.layers.len().min(TERRAIN_LAYER_COUNT)
    }

    /// 斜度（法線の Y 成分）と高度（ワールド Y）から、ルールによるレイヤ重みを求める。
    ///
    /// - `normal_y`: 頂点法線の Y 成分（-1..=1・正規化済み前提）。
    /// - `world_y` : 頂点のワールド Y（メートル）。
    ///
    /// 戻り値は総和 1 に正規化済み。すべてのルールが 0 を返した場合は
    /// レイヤ 0 に 1.0 を寄せる（穴＝真っ黒を出さないための縮退規約）。
    pub fn rule_weights(&self, normal_y: f32, world_y: f32) -> LayerWeights {
        // ─── 法線 Y から斜度（度）を求める。上向き=0 度、水平方向=90 度 ───
        //   |n.y| を使うことで、天井（下向き法線）も同じ斜度として扱う。
        let slope_deg = normal_y.abs().clamp(0.0, 1.0).acos().to_degrees();

        let mut w: LayerWeights = [0.0; TERRAIN_LAYER_COUNT];
        for (i, layer) in self.layers.iter().take(TERRAIN_LAYER_COUNT).enumerate() {
            w[i] = layer.rule.evaluate(slope_deg, world_y);
        }
        normalize_weights(&mut w);
        w
    }
}

impl Default for TerrainLayerSet {
    /// layers.json が見つからない／壊れているときのフォールバック定義。
    ///
    /// 単色 4 層（草地・土・岩・砂）。テクスチャアセットに依存せず、
    /// 斜度・高度で塗り分けが目視できる配色にしてある。
    fn default() -> Self {
        Self {
            layers: vec![
                TerrainLayer {
                    name: "grass".to_string(),
                    base_color: [0.16, 0.38, 0.12],
                    roughness: 0.95,
                    metallic: 0.0,
                    uv_scale: DEFAULT_LAYER_UV_SCALE,
                    base_color_texture: None,
                    // 平地（0〜22 度）に載る下地。
                    rule: LayerRule {
                        slope_min_deg: 0.0,
                        slope_max_deg: 22.0,
                        slope_fade_deg: 10.0,
                        priority: 1.0,
                        ..LayerRule::default()
                    },
                },
                TerrainLayer {
                    name: "dirt".to_string(),
                    base_color: [0.33, 0.22, 0.12],
                    roughness: 0.9,
                    metallic: 0.0,
                    uv_scale: DEFAULT_LAYER_UV_SCALE,
                    base_color_texture: None,
                    // 草地と岩の中間の傾斜（18〜42 度）。
                    rule: LayerRule {
                        slope_min_deg: 18.0,
                        slope_max_deg: 42.0,
                        slope_fade_deg: 10.0,
                        priority: 1.0,
                        ..LayerRule::default()
                    },
                },
                TerrainLayer {
                    name: "rock".to_string(),
                    base_color: [0.40, 0.40, 0.42],
                    roughness: 0.7,
                    metallic: 0.0,
                    uv_scale: DEFAULT_LAYER_UV_SCALE,
                    base_color_texture: None,
                    // 急斜面（38 度以上）は岩。
                    rule: LayerRule {
                        slope_min_deg: 38.0,
                        slope_max_deg: 90.0,
                        slope_fade_deg: 10.0,
                        priority: 1.2,
                        ..LayerRule::default()
                    },
                },
                TerrainLayer {
                    name: "sand".to_string(),
                    base_color: [0.76, 0.68, 0.45],
                    roughness: 0.85,
                    metallic: 0.0,
                    uv_scale: DEFAULT_LAYER_UV_SCALE,
                    base_color_texture: None,
                    // 低地（Y <= -2m）の平坦部＝水際の砂。
                    // height_fade は「地表 Y=0 の平地に砂が滲み出さない」幅に取る
                    // （fade が広いと Y=0 でも砂が数割混じり、草地が濁って見える）。
                    rule: LayerRule {
                        slope_min_deg: 0.0,
                        slope_max_deg: 25.0,
                        slope_fade_deg: 10.0,
                        height_min: DEFAULT_HEIGHT_MIN,
                        height_max: -2.0,
                        height_fade: 1.0,
                        priority: 1.5,
                    },
                },
            ],
        }
    }
}

// ============================================================
//  重みの合成（ルール自動生成 × 手ペイント）
// ============================================================

/// ルール由来の重みと手ペイント由来の重みを合成する。
///
/// 手ペイントは「上書き」ではなく「優先ブレンド」で共存させる:
///   result = lerp(rule_weights, paint_weights, paint_amount)
///
/// - `paint_amount == 0` … 一度もペイントされていない ⇒ 完全にルール任せ。
///   地形を掘って斜面ができれば自動で岩になる（自動下地が生き続ける）。
/// - `paint_amount == 1` … 完全にペイント済み ⇒ ルールを無視して手描き優先。
///   ブラシで塗った箇所は、その後に地形を変形してもルールに塗り戻されない。
/// - 中間値 … ブラシ縁のフェード。ペイント領域が地形へ自然に溶け込む。
///
/// この 1 本の式が「自動生成を上書きしない仕組み」の実体であり、
/// ペイント済みフラグ（真偽値）ではなく連続値にすることでブラシ縁の段差を消している。
pub fn blend_rule_and_paint(
    rule: LayerWeights,
    paint: LayerWeights,
    paint_amount: f32,
) -> LayerWeights {
    let t = paint_amount.clamp(0.0, 1.0);
    let mut out: LayerWeights = [0.0; TERRAIN_LAYER_COUNT];
    for i in 0..TERRAIN_LAYER_COUNT {
        out[i] = rule[i] * (1.0 - t) + paint[i] * t;
    }
    normalize_weights(&mut out);
    out
}

/// 重みベクトルを総和 1 へ正規化する（総和が 0 ならレイヤ 0 に寄せる）。
pub fn normalize_weights(w: &mut LayerWeights) {
    // ─── 負値を潰してから総和を取る（ルールは非負だが lerp 誤差の保険）───
    let mut sum = 0.0;
    for v in w.iter_mut() {
        if *v < 0.0 {
            *v = 0.0;
        }
        sum += *v;
    }
    if sum <= RULE_WEIGHT_EPSILON {
        // 縮退（どのルールも立たない）: レイヤ 0 を下地として敷く。
        *w = [0.0; TERRAIN_LAYER_COUNT];
        w[0] = 1.0;
        return;
    }
    let inv = 1.0 / sum;
    for v in w.iter_mut() {
        *v *= inv;
    }
}

// ─── ウィンドウ関数 ─────────────────────────────────────────────────────────

/// smoothstep（3t²-2t³）。t は [0,1] にクランプして評価する。
#[inline]
fn smoothstep(t: f32) -> f32 {
    let x = t.clamp(0.0, 1.0);
    x * x * (3.0 - 2.0 * x)
}

/// [min, max] の内側で 1、外側で 0 になり、両端を `fade` 幅でぼかす台形ウィンドウ。
///
/// `fade <= 0` のときはハードな矩形ウィンドウになる。
fn window(v: f32, min: f32, max: f32, fade: f32) -> f32 {
    // ─── 区間が潰れている（max < min）なら常に 0 ───
    if max < min {
        return 0.0;
    }
    if fade <= 0.0 {
        // ハードウィンドウ。
        return if v >= min && v <= max { 1.0 } else { 0.0 };
    }
    // 下端の立ち上がり: v が min-fade → min の間で 0 → 1。
    let rise = smoothstep((v - (min - fade)) / fade);
    // 上端の立ち下がり: v が max → max+fade の間で 1 → 0。
    let fall = 1.0 - smoothstep((v - max) / fade);
    (rise * fall).clamp(0.0, 1.0)
}

// ============================================================
//  u8 量子化（.tvox v2 のディスク表現・スプラットグリッド保持）
// ============================================================

/// 重み 1.0 に対応する u8 の値（量子化のフルスケール）。
pub const WEIGHT_QUANT_MAX: f32 = 255.0;

/// 正規化済み重み（0..=1）を u8 へ量子化する。
#[inline]
pub fn quantize_weight(w: f32) -> u8 {
    (w.clamp(0.0, 1.0) * WEIGHT_QUANT_MAX + 0.5) as u8
}

/// u8 量子化された重みを f32（0..=1）へ復元する。
#[inline]
pub fn dequantize_weight(q: u8) -> f32 {
    q as f32 / WEIGHT_QUANT_MAX
}
