// ============================================================
//  terrain/layers.rs — 地形マテリアルレイヤ定義（データドリブン）
//
//  【責務】
//    地形の「スプラットレイヤ」（草地・岩・土・砂 …）の定義と、
//    斜度／高度ルールからレイヤ重みを求める純粋関数を提供する。
//    ファイル IO・GPU・ECS への依存は一切持たない（JSON 文字列 in / 構造体 out）。
//
//  【レイヤ総数と「同時ブレンド数」の分離 — T2b】
//    レイヤ定義（layers.json）は最大 TERRAIN_MAX_LAYERS 層まで持てるが、
//    1 頂点／1 チャンクが同時にブレンドできるのは TERRAIN_BLEND_SLOTS 層だけ。
//    これはレイヤ重みを頂点カラー（Vertex.color の RGBA 4 成分）で運ぶという
//    設計判断に由来する（頂点フォーマットを増やすと全パイプラインへ波及するため）。
//
//    そのため「どのレイヤ番号を 4 スロットへ載せるか」を持ち回る必要があり、
//    その表現が `BlendSlots`（レイヤ番号 4 個 ＋ 重み 4 個）である。
//
//  【重みの規約】
//    weights[i] は 0..=1 で、常に総和 1 に正規化されている（全レイヤの
//    重みが 0 になる縮退ケースではレイヤ 0 に 1.0 を寄せる）。
// ============================================================

use serde::{Deserialize, Serialize};

// ─── レイヤ数（GPU ブレンド予算。マジックナンバー禁止のため定数化）───────────

/// 1 頂点／1 チャンクで同時にブレンドできる地形レイヤ数（＝スロット数）。
///
/// 頂点カラー（vec4）にスプラット重みを載せる設計上の上限であり、
/// WGSL 側 `TERRAIN_BLEND_SLOTS`（terrain_gbuffer_write.wgsl）と一致必須。
pub const TERRAIN_BLEND_SLOTS: usize = 4;

/// layers.json に定義できるレイヤ数の上限。
///
/// GPU 側のテクスチャ配列枚数の上限と一致させること。これを超える定義は
/// 読み込み時に切り詰められる（エラーにはしない＝データ差し替えを妨げない）。
pub const TERRAIN_MAX_LAYERS: usize = 16;

/// スロット重みベクトル（総和 1 に正規化済み）。
///
/// 添字は「レイヤ番号」ではなく **スロット番号** であることに注意
/// （どのレイヤがどのスロットに載るかは `BlendSlots::index` が持つ）。
pub type LayerWeights = [f32; TERRAIN_BLEND_SLOTS];

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

/// レイヤ detile 強度の既定値（1.0 = モードが指定する効果をフル適用）。
const DEFAULT_LAYER_DETILE_STRENGTH: f32 = 1.0;

/// ルール評価で「重み無し」とみなす下限。数値誤差で微小な重みが残るのを潰す。
const RULE_WEIGHT_EPSILON: f32 = 1.0e-6;

// ─── DetileMode の GPU コード（WGSL uniform へ渡す数値。マジックナンバー禁止）───

/// `DetileMode::None` の GPU コード。
const DETILE_CODE_NONE: u32 = 0;
/// `DetileMode::Stochastic` の GPU コード。
const DETILE_CODE_STOCHASTIC: u32 = 1;
/// `DetileMode::Macro` の GPU コード。
const DETILE_CODE_MACRO: u32 = 2;

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
fn default_layer_detile_strength() -> f32 { DEFAULT_LAYER_DETILE_STRENGTH }

// ============================================================
//  DetileMode — タイリング（繰り返し模様）解消モード
// ============================================================

/// タイリング（繰り返し模様）解消モード。layers.json の "detile" フィールド。
///
/// 地形は triplanar で同一テクスチャを広範囲へ敷くため、素直にタイルすると
/// 一定周期の格子模様が目に付く。それを崩す手法をレイヤ単位で選べるようにする。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum DetileMode {
    /// 従来どおり単純タイリング（既定）。最も安価。
    #[default]
    None,
    /// 六角格子の確率的タイリング（Heitz-Neyret 風）。品質は高いがサンプル数が増える。
    Stochastic,
    /// 大スケールノイズ変調＋マルチスケール重ね（安価）。
    Macro,
}

impl DetileMode {
    /// WGSL uniform へ渡す数値コード。0=None, 1=Stochastic, 2=Macro。
    pub fn to_gpu_code(self) -> u32 {
        match self {
            DetileMode::None => DETILE_CODE_NONE,
            DetileMode::Stochastic => DETILE_CODE_STOCHASTIC,
            DetileMode::Macro => DETILE_CODE_MACRO,
        }
    }
}

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
    /// 法線マップのアセット相対パス（省略時は法線マップ無し＝ジオメトリ法線のまま）。
    #[serde(default)]
    pub normal_texture: Option<String>,
    /// ラフネスマップのアセット相対パス（省略時はスカラ `roughness` を使う）。
    #[serde(default)]
    pub roughness_texture: Option<String>,
    /// タイリング解消モード（省略時は None＝従来どおり単純タイリング）。
    #[serde(default)]
    pub detile: DetileMode,
    /// タイリング解消の強度（0=無効〜1=フル適用）。モードが None のときは無視される。
    #[serde(default = "default_layer_detile_strength")]
    pub detile_strength: f32,
    /// 斜度／高度による自動下地生成ルール。
    #[serde(default)]
    pub rule: LayerRule,
}

impl Default for TerrainLayer {
    /// 名前なし・単色グレー・テクスチャ無しのレイヤ。
    ///
    /// 構造体更新記法（`..TerrainLayer::default()`）でレイヤを組み立てられるようにし、
    /// フィールド追加のたびに全リテラルを直さなくて済むようにする。
    fn default() -> Self {
        Self {
            name: String::new(),
            base_color: DEFAULT_LAYER_BASE_COLOR,
            roughness: DEFAULT_LAYER_ROUGHNESS,
            metallic: DEFAULT_LAYER_METALLIC,
            uv_scale: DEFAULT_LAYER_UV_SCALE,
            base_color_texture: None,
            normal_texture: None,
            roughness_texture: None,
            detile: DetileMode::None,
            detile_strength: DEFAULT_LAYER_DETILE_STRENGTH,
            rule: LayerRule::default(),
        }
    }
}

// ============================================================
//  TerrainLayerSet — レイヤ定義一式（layers.json の中身）
// ============================================================

/// 地形レイヤ定義の集合。`assets/terrain/layers.json` の直列化対象。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TerrainLayerSet {
    /// レイヤ定義。最大 TERRAIN_MAX_LAYERS 層まで持てる。
    pub layers: Vec<TerrainLayer>,
}

impl TerrainLayerSet {
    /// JSON 文字列からレイヤ定義を読む。
    ///
    /// レイヤ数が TERRAIN_MAX_LAYERS を超える場合は先頭 TERRAIN_MAX_LAYERS 層へ
    /// 切り詰める（データ差し替えでの試行錯誤を止めないため、エラーにはしない）。
    /// レイヤが 1 つも無い場合は既定セットへフォールバックする。
    pub fn from_json_str(s: &str) -> Result<Self, serde_json::Error> {
        let mut set: TerrainLayerSet = serde_json::from_str(s)?;
        set.layers.truncate(TERRAIN_MAX_LAYERS);
        if set.layers.is_empty() {
            set = Self::default();
        }
        Ok(set)
    }

    /// 実際に扱われるレイヤ数（= min(定義数, TERRAIN_MAX_LAYERS)）。
    pub fn active_count(&self) -> usize {
        self.layers.len().min(TERRAIN_MAX_LAYERS)
    }

    /// 全レイヤ分のルール重みを求める（len = layers.len()、総和 1 に正規化済み）。
    ///
    /// - `normal_y`: 頂点法線の Y 成分（-1..=1・正規化済み前提）。
    /// - `world_y` : 頂点のワールド Y（メートル）。
    ///
    /// すべてのルールが 0 を返した場合はレイヤ 0 に 1.0 を寄せる
    /// （穴＝真っ黒を出さないための縮退規約）。
    pub fn rule_weights_all(&self, normal_y: f32, world_y: f32) -> Vec<f32> {
        // 確保なし版へ委譲するだけの薄いラッパ（演算は 1 箇所に集約する）。
        let mut w = vec![0.0f32; self.layers.len()];
        let written = self.rule_weights_all_into(normal_y, world_y, &mut w);
        debug_assert_eq!(written, w.len(), "rule_weights_all_into の書き込み数が層数と不一致");
        w
    }

    /// `rule_weights_all` の **確保なし版**（呼び出し側のスクラッチバッファへ書き込む）。
    ///
    /// 頂点ごとに呼ばれるホットパス（頂点カラー計算）で `Vec` の新規確保を無くすために
    /// 存在する。演算内容・順序は `rule_weights_all` と完全に同一であり、
    /// 結果は 1 ビットも変わらない。
    ///
    /// - `out`: 書き込み先。**`self.layers.len()` 以上の長さが必要**
    ///          （足りない場合は debug ビルドで assert、release では書ける分だけ書く形に
    ///           ならないよう `panic` するので、呼び出し側で層数を保証すること）。
    ///
    /// 戻り値は書き込んだ要素数（= `self.layers.len()`）。`out` の残り（それ以降の
    /// 要素）は触らないため、呼び出し側は `&out[..written]` だけを使うこと。
    pub fn rule_weights_all_into(&self, normal_y: f32, world_y: f32, out: &mut [f32]) -> usize {
        let n = self.layers.len();
        assert!(
            out.len() >= n,
            "rule_weights_all_into: 出力バッファが短い（{} < {n}）", out.len()
        );

        // ─── 法線 Y から斜度（度）を求める。上向き=0 度、水平方向=90 度 ───
        //   |n.y| を使うことで、天井（下向き法線）も同じ斜度として扱う。
        let slope_deg = normal_y.abs().clamp(0.0, 1.0).acos().to_degrees();

        // ─── 各レイヤのルールを評価して先頭 n 要素を全て上書きする ───
        for (slot, layer) in self.layers.iter().enumerate() {
            out[slot] = layer.rule.evaluate(slope_deg, world_y);
        }
        normalize_weights_slice(&mut out[..n]);
        n
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
                    // 平地（0〜22 度）に載る下地。
                    rule: LayerRule {
                        slope_min_deg: 0.0,
                        slope_max_deg: 22.0,
                        slope_fade_deg: 10.0,
                        priority: 1.0,
                        ..LayerRule::default()
                    },
                    ..TerrainLayer::default()
                },
                TerrainLayer {
                    name: "dirt".to_string(),
                    base_color: [0.33, 0.22, 0.12],
                    roughness: 0.9,
                    // 草地と岩の中間の傾斜（18〜42 度）。
                    rule: LayerRule {
                        slope_min_deg: 18.0,
                        slope_max_deg: 42.0,
                        slope_fade_deg: 10.0,
                        priority: 1.0,
                        ..LayerRule::default()
                    },
                    ..TerrainLayer::default()
                },
                TerrainLayer {
                    name: "rock".to_string(),
                    base_color: [0.40, 0.40, 0.42],
                    roughness: 0.7,
                    // 急斜面（38 度以上）は岩。
                    rule: LayerRule {
                        slope_min_deg: 38.0,
                        slope_max_deg: 90.0,
                        slope_fade_deg: 10.0,
                        priority: 1.2,
                        ..LayerRule::default()
                    },
                    ..TerrainLayer::default()
                },
                TerrainLayer {
                    name: "sand".to_string(),
                    base_color: [0.76, 0.68, 0.45],
                    roughness: 0.85,
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
                    ..TerrainLayer::default()
                },
            ],
        }
    }
}

// ============================================================
//  重みの正規化
// ============================================================

/// 任意長の重みベクトルを総和 1 へ正規化する（総和が 0 なら先頭要素に寄せる）。
///
/// 縮退規約: 全要素が 0（または全負）のときはレイヤ 0（先頭要素）に 1.0 を寄せる
/// （穴＝真っ黒を出さないための規約）。
/// 空スライスは何もしない（呼び出し側でレイヤ 0 個は起こらない前提だが、
/// パニックさせないための防御）。
pub fn normalize_weights_slice(w: &mut [f32]) {
    if w.is_empty() {
        return;
    }
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
        for v in w.iter_mut() {
            *v = 0.0;
        }
        w[0] = 1.0;
        return;
    }
    let inv = 1.0 / sum;
    for v in w.iter_mut() {
        *v *= inv;
    }
}

// ============================================================
//  BlendSlots — 「上位 4 層」のレイヤ番号＋重み
// ============================================================

/// 上位 TERRAIN_BLEND_SLOTS 層の「レイヤ番号＋重み」。
///
/// weight は総和 1 に正規化済みで、weight 降順にソート済み。
/// 未使用スロットは weight=0, index=0。
///
/// 【なぜこの表現が必要か】
///   レイヤ定義は最大 TERRAIN_MAX_LAYERS 層まで持てるが、GPU へ運べる重みは
///   頂点カラーの 4 成分しかない。よって「どのレイヤ番号を 4 成分へ載せたか」を
///   重みと一緒に持ち回る必要がある。それがこの構造体。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct BlendSlots {
    /// 各スロットが指すレイヤ番号（未使用スロットは 0）。
    pub index: [u32; TERRAIN_BLEND_SLOTS],
    /// 各スロットの重み（総和 1・降順。未使用スロットは 0）。
    pub weight: [f32; TERRAIN_BLEND_SLOTS],
}

impl Default for BlendSlots {
    /// 縮退状態（レイヤ 0 が 100%）。全 0 の重みベクトルを畳んだ結果と一致する。
    fn default() -> Self {
        let mut weight = [0.0f32; TERRAIN_BLEND_SLOTS];
        weight[0] = 1.0;
        Self { index: [0; TERRAIN_BLEND_SLOTS], weight }
    }
}

/// 任意長の重みベクトルから上位 TERRAIN_BLEND_SLOTS 層を選び、正規化して返す。
///
/// 同点の場合はレイヤ番号の小さい方を優先する（安定ソート）。
/// 全要素が 0（または空・全負）の縮退時はレイヤ 0 に 1.0 を寄せる。
pub fn select_top_slots(weights: &[f32]) -> BlendSlots {
    // ─── (レイヤ番号, 重み) のペアを作り、負値は 0 に潰す ───
    let mut pairs: Vec<(usize, f32)> = weights
        .iter()
        .enumerate()
        .map(|(i, &w)| (i, if w > 0.0 { w } else { 0.0 }))
        .collect();

    // ─── 重み降順に安定ソート（同点はレイヤ番号昇順が保たれる）───
    //   f32 は NaN を含み得ないので partial_cmp の unwrap_or は保険。
    pairs.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));

    // ─── 上位 4 件を取り出して総和を求める ───
    let mut index = [0u32; TERRAIN_BLEND_SLOTS];
    let mut weight = [0.0f32; TERRAIN_BLEND_SLOTS];
    let mut sum = 0.0f32;
    for (slot, &(layer, w)) in pairs.iter().take(TERRAIN_BLEND_SLOTS).enumerate() {
        index[slot] = layer as u32;
        weight[slot] = w;
        sum += w;
    }

    // ─── 縮退（上位 4 層の総和が 0）: レイヤ 0 を下地として敷く ───
    if sum <= RULE_WEIGHT_EPSILON {
        return BlendSlots::default();
    }

    // ─── 上位 4 層だけで総和 1 になるよう正規化（落とした層のぶんは配分される）───
    let inv = 1.0 / sum;
    for w in weight.iter_mut() {
        *w *= inv;
    }
    BlendSlots { index, weight }
}

/// BlendSlots を len 個の密ベクトルへ展開する（範囲外 index は無視する）。
///
/// 展開結果の総和は 1 になる（範囲外 index を落とした場合を除く）。
pub fn expand_slots(slots: &BlendSlots, len: usize) -> Vec<f32> {
    // 確保なし版へ委譲するだけの薄いラッパ。
    let mut out = vec![0.0f32; len];
    expand_slots_into(slots, len, &mut out);
    out
}

/// `expand_slots` の **確保なし版**（呼び出し側のスクラッチバッファへ書き込む）。
///
/// `out[..len]` は本関数が必ず全要素を上書きする（先に 0 クリアしてから加算するため、
/// 呼び出し側でバッファを 0 埋めし直す必要はない）。`out` の `len` 以降は触らない。
///
/// 演算内容・順序は `expand_slots` と完全に同一。
pub fn expand_slots_into(slots: &BlendSlots, len: usize, out: &mut [f32]) {
    assert!(
        out.len() >= len,
        "expand_slots_into: 出力バッファが短い（{} < {len}）", out.len()
    );
    // ─── 先頭 len 要素を 0 クリア（vec![0.0; len] と同じ初期状態を作る）───
    for v in out[..len].iter_mut() {
        *v = 0.0;
    }
    for slot in 0..TERRAIN_BLEND_SLOTS {
        let layer = slots.index[slot] as usize;
        // 範囲外（レイヤ定義が減った等）は捨てる。パニックさせない。
        if layer < len {
            out[layer] += slots.weight[slot];
        }
    }
}

/// ルール重み（密ベクトル）とペイント（BlendSlots）を paint_amount で lerp する。
///
/// `blend_rule_and_paint` の任意レイヤ数版。戻り値は len = rule.len() の密ベクトルで、
/// 総和 1 に正規化済み（縮退時はレイヤ 0 へ寄せる）。
pub fn blend_rule_and_paint_all(
    rule: &[f32],
    paint: &BlendSlots,
    paint_amount: f32,
) -> Vec<f32> {
    // 確保なし版へ委譲するだけの薄いラッパ。
    let mut out = vec![0.0f32; rule.len()];
    blend_rule_and_paint_all_into(rule, paint, paint_amount, &mut out);
    out
}

/// `blend_rule_and_paint_all` の **確保なし版**（呼び出し側のスクラッチバッファへ書き込む）。
///
/// `out[..rule.len()]` は本関数が必ず全要素を上書きする。`out` のそれ以降は触らない。
///
/// 【中間バッファを持たない理由】
///   まず `out` 自体へペイントの密展開を書き、その場で `rule` と lerp する。
///   `expand_slots` → 一時 Vec → lerp という元実装と **演算の順序も内容も同一**
///   （成分ごとに `r * (1 - t) + p * t`）でありながら、確保が 1 回も起きない。
pub fn blend_rule_and_paint_all_into(
    rule: &[f32],
    paint: &BlendSlots,
    paint_amount: f32,
    out: &mut [f32],
) {
    let len = rule.len();
    assert!(
        out.len() >= len,
        "blend_rule_and_paint_all_into: 出力バッファが短い（{} < {len}）", out.len()
    );
    let t = paint_amount.clamp(0.0, 1.0);
    // ペイントをルールと同じ次元へ展開（out をそのまま展開先に使う）。
    expand_slots_into(paint, len, out);
    // 成分ごとに lerp（out には展開済みのペイント重みが入っている）。
    for (slot, &r) in rule.iter().enumerate() {
        let p = out[slot];
        out[slot] = r * (1.0 - t) + p * t;
    }
    normalize_weights_slice(&mut out[..len]);
}

// ─── ウィンドウ関数 ─────────────────────────────────────────────────────────

/// smoothstep（3t²-2t³）。t は [0,1] にクランプして評価する。
///
/// `pub(crate)` なのは、散布ルール（terrain/scatter/props.rs）が
/// **まったく同じ台形ウィンドウ演算**を使うためである。式を複製すると
/// 片方だけ直したときにレイヤ境界と散布境界がずれるので、定義は 1 箇所に保つ。
#[inline]
pub(crate) fn smoothstep(t: f32) -> f32 {
    let x = t.clamp(0.0, 1.0);
    x * x * (3.0 - 2.0 * x)
}

/// [min, max] の内側で 1、外側で 0 になり、両端を `fade` 幅でぼかす台形ウィンドウ。
///
/// `fade <= 0` のときはハードな矩形ウィンドウになる。
///
/// `pub(crate)` の理由は `smoothstep` と同じ（散布ルールが同一式を再利用する）。
pub(crate) fn window(v: f32, min: f32, max: f32, fade: f32) -> f32 {
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
