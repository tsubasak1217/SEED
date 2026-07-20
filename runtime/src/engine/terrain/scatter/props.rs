// ============================================================
//  terrain/scatter/props.rs — 地形プロップ定義（データドリブン）
//
//  【責務】
//    地形に散布する「プロップ」（草・木などのモデル）の定義と、
//    斜度／高度／レイヤ重みから「そこに生えるか」の確率を求める純粋関数を提供する。
//    ファイル IO・GPU・ECS への依存は一切持たない（JSON 文字列 in / 構造体 out）。
//
//  【なぜ草をパラメータで持つのか】
//    草はメッシュアセットではなく、幅・高さ・分割数・曲げ量から
//    **手続き的に生成**する。アセットを差し替えずに props.json の数値だけで
//    見た目を作り込めるようにするための設計判断である
//    （kind=model のプロップだけが model_path で実アセットを参照する）。
//
//  【ルール評価の式を layers.rs と共有している件】
//    斜度／高度の台形ウィンドウは layers.rs の `window()` / `smoothstep()` を
//    **そのまま再利用**している（複製していない）。レイヤの塗り分け境界と
//    草の生え際が同じ式で決まるため、片方だけ直してずれる事故を構造的に防ぐ。
// ============================================================

use serde::{Deserialize, Serialize};

use super::super::layers::{smoothstep, window};

// ─── プロップ数の上限（マジックナンバー禁止のため定数化）───────────────────

/// props.json に定義できるプロップ数の上限。
///
/// 散布インスタンスは `prop_id: u32` でプロップを指すため理論上の上限は広いが、
/// エディタの一覧 UI と GPU 側のプロップ別ドローコール数を現実的な範囲に
/// 抑えるための運用上限。これを超える定義は読み込み時に切り詰められる
/// （エラーにはしない＝データ差し替えでの試行錯誤を妨げない）。
pub const TERRAIN_MAX_PROPS: usize = 64;

/// 1 本の草を構成する縦分割数の上限。
///
/// 分割数は 1 本あたりの頂点数に直結する（三角形数 ≒ segments × 2 × 板枚数）。
/// 草は 1 チャンクに数千本生えるため、上限を切らないと頂点バッファが破綻する。
pub const GRASS_MAX_SEGMENTS: u32 = 8;

// ─── 草パラメータの既定値 ───────────────────────────────────────────────────

/// 草の根元の葉幅の既定値（メートル）。
const DEFAULT_GRASS_WIDTH: f32 = 0.055;
/// 草の高さの既定値（メートル）。
const DEFAULT_GRASS_HEIGHT: f32 = 0.38;
/// 草の高さのランダム変動率の既定値（0=全て同じ高さ, 1=0〜2 倍まで振れる）。
const DEFAULT_GRASS_HEIGHT_VARIANCE: f32 = 0.35;
/// 草 1 本の縦分割数の既定値（曲げが滑らかに見える最小限）。
const DEFAULT_GRASS_SEGMENTS: u32 = 4;
/// 十字 2 枚構成にするかの既定値（true = 見る角度によらず密度感が出る）。
const DEFAULT_GRASS_CROSS_PLANES: bool = true;
/// 静止時の自然な垂れの既定値（0=直立。先端がこの割合だけ横へ倒れる）。
const DEFAULT_GRASS_BEND: f32 = 0.25;
/// 草の根元色の既定値（リニア RGB・影に沈んだ暗い緑）。
const DEFAULT_GRASS_COLOR_BOTTOM: [f32; 3] = [0.08, 0.20, 0.05];
/// 草の先端色の既定値（リニア RGB・光を受けた明るい黄緑）。
const DEFAULT_GRASS_COLOR_TOP: [f32; 3] = [0.34, 0.58, 0.16];
/// 草のラフネスの既定値（植物は粗い拡散反射）。
const DEFAULT_GRASS_ROUGHNESS: f32 = 0.85;
/// 先端アルファカットアウト閾値の既定値（0 = カットアウト無効）。
const DEFAULT_GRASS_TIP_ALPHA_CUTOFF: f32 = 0.0;
/// 陰影用法線を地表法線へ寄せる割合の既定値（0 = 真の幾何法線, 1 = 完全に地表法線）。
///
/// 板ポリの葉の幾何法線は**水平**を向くため、そのまま陰影に使うと高い位置の
/// 太陽に対して N·L ≒ 0 となり草がほぼ真っ黒に落ちる（実測された症状）。
/// 地表法線側へ寄せるのがリアルタイム草の定番手法（normal flattening）である。
/// 既定を 1.0 ではなく 0.7 にしているのは、完全に地表法線へ潰すと草原が
/// 「緑に塗った地面」に見えて葉ごとの立体感が消えるため。
/// 0.7 は葉の向きによる陰影差を残しつつ、黒落ちが解消する値として実画像で選定した。
const DEFAULT_GRASS_NORMAL_UP_BLEND: f32 = 0.7;

// ─── 風パラメータの既定値 ───────────────────────────────────────────────────

/// 風による横揺れ幅の既定値（草の高さに対する比率）。
const DEFAULT_WIND_STRENGTH: f32 = 0.16;
/// 風の基本振動速度の既定値（ラジアン毎秒相当）。
const DEFAULT_WIND_SPEED: f32 = 1.4;
/// 風の空間周波数の既定値（ワールド 1m あたりの位相進み）。
const DEFAULT_WIND_FREQUENCY: f32 = 0.35;
/// 突風（ゆっくり大きく波打つ成分）の強さの既定値。
const DEFAULT_WIND_GUST_STRENGTH: f32 = 0.35;
/// 突風の進行速度の既定値。
const DEFAULT_WIND_GUST_SPEED: f32 = 0.25;

// ─── 散布パラメータの既定値 ─────────────────────────────────────────────────

/// 1 m² あたりの候補点数の既定値。
const DEFAULT_SCATTER_DENSITY: f32 = 4.0;
/// スケール下限の既定値。
const DEFAULT_SCATTER_SCALE_MIN: f32 = 0.85;
/// スケール上限の既定値。
const DEFAULT_SCATTER_SCALE_MAX: f32 = 1.25;
/// 地表法線に沿わせるかの既定値（true = 斜面で寝る）。
const DEFAULT_SCATTER_ALIGN_TO_NORMAL: bool = true;
/// ランダム傾き上限の既定値（度）。0 だと全個体が同じ姿勢で人工的に見える。
const DEFAULT_SCATTER_TILT_MAX_DEG: f32 = 8.0;
/// Y 軸まわりのランダム回転を掛けるかの既定値。
const DEFAULT_SCATTER_RANDOM_YAW: bool = true;

// ─── 散布ルールの既定値 ─────────────────────────────────────────────────────

/// 斜度ウィンドウ下限の既定値（度）。0 度 = 完全な平地。
const DEFAULT_RULE_SLOPE_MIN_DEG: f32 = 0.0;
/// 斜度ウィンドウ上限の既定値（度）。90 度 = 垂直な崖。
const DEFAULT_RULE_SLOPE_MAX_DEG: f32 = 90.0;
/// 斜度ウィンドウの縁をぼかす幅の既定値（度）。
const DEFAULT_RULE_SLOPE_FADE_DEG: f32 = 8.0;
/// 高度ウィンドウ下限の既定値（メートル）。実質「制限なし」の下限。
const DEFAULT_RULE_HEIGHT_MIN: f32 = -1.0e6;
/// 高度ウィンドウ上限の既定値（メートル）。実質「制限なし」の上限。
const DEFAULT_RULE_HEIGHT_MAX: f32 = 1.0e6;
/// 高度ウィンドウの縁をぼかす幅の既定値（メートル）。
const DEFAULT_RULE_HEIGHT_FADE: f32 = 2.0;
/// 最終確率の足切り閾値の既定値。
///
/// 0 だと窓の外縁で確率 0.001 のような「ごく稀に 1 本だけ生える」個体が
/// 広範囲に散らばり、レイヤ境界がぼやける。既定でわずかに足切りする。
const DEFAULT_RULE_THRESHOLD: f32 = 0.02;
/// レイヤ条件の既定要求重み（0 = 事実上そのレイヤがあれば通る）。
const DEFAULT_LAYER_CONDITION_MIN_WEIGHT: f32 = 0.5;

/// レイヤ条件のフェード幅（重み単位）。
///
/// `min_weight - LAYER_CONDITION_FADE` から `min_weight` にかけて 0→1 へ
/// smoothstep する。フェードが無いとレイヤ境界で草が直線状に切れて目立つ。
/// `window()` の立ち上がり側とまったく同じ流儀（下側にぼかす）である。
const LAYER_CONDITION_FADE: f32 = 0.15;

// ─── 既定プロップセットの値（フォールバック定義）───────────────────────────

/// 既定の草プロップ ID。エンジン層はこの ID で「標準の草」を参照してよい。
const DEFAULT_GRASS_PROP_ID: &str = "grass_field";
/// 既定の草プロップの散布密度（1 m² あたり）。フィールド一面を覆う密度。
const DEFAULT_GRASS_PROP_DENSITY: f32 = 8.0;
/// 既定の草プロップが載る斜度上限（度）。これより急な面には生えない。
const DEFAULT_GRASS_PROP_SLOPE_MAX_DEG: f32 = 32.0;
/// 既定の草プロップが要求する "grass" レイヤ重み。
const DEFAULT_GRASS_PROP_LAYER_MIN_WEIGHT: f32 = 0.35;

/// 既定の木プロップ ID。
const DEFAULT_TREE_PROP_ID: &str = "tree_pine";
/// 既定の木プロップの散布密度（1 m² あたり）。50m² に 1 本程度の疎らさ。
const DEFAULT_TREE_PROP_DENSITY: f32 = 0.02;
/// 既定の木プロップが載る斜度上限（度）。木は草より平坦な面を要求する。
const DEFAULT_TREE_PROP_SLOPE_MAX_DEG: f32 = 20.0;
/// 既定の木プロップが要求する "grass" レイヤ重み（草地にだけ生える）。
const DEFAULT_TREE_PROP_LAYER_MIN_WEIGHT: f32 = 0.6;
/// 既定の木プロップのスケール下限。
const DEFAULT_TREE_PROP_SCALE_MIN: f32 = 0.8;
/// 既定の木プロップのスケール上限。
const DEFAULT_TREE_PROP_SCALE_MAX: f32 = 1.4;
/// 既定の木プロップのランダム傾き上限（度）。木は大きく傾くと不自然。
const DEFAULT_TREE_PROP_TILT_MAX_DEG: f32 = 4.0;
/// 既定の木プロップの足切り閾値（疎らなので確率の裾を強めに切る）。
const DEFAULT_TREE_PROP_THRESHOLD: f32 = 0.2;

/// 既定プロップが参照する地形レイヤ名（layers.rs の既定セットの草地レイヤ）。
const DEFAULT_GRASS_LAYER_NAME: &str = "grass";

// ─── serde default 用関数 ───────────────────────────────────────────────────

fn default_grass_width() -> f32 { DEFAULT_GRASS_WIDTH }
fn default_grass_height() -> f32 { DEFAULT_GRASS_HEIGHT }
fn default_grass_height_variance() -> f32 { DEFAULT_GRASS_HEIGHT_VARIANCE }
fn default_grass_segments() -> u32 { DEFAULT_GRASS_SEGMENTS }
fn default_grass_cross_planes() -> bool { DEFAULT_GRASS_CROSS_PLANES }
fn default_grass_bend() -> f32 { DEFAULT_GRASS_BEND }
fn default_grass_color_bottom() -> [f32; 3] { DEFAULT_GRASS_COLOR_BOTTOM }
fn default_grass_color_top() -> [f32; 3] { DEFAULT_GRASS_COLOR_TOP }
fn default_grass_roughness() -> f32 { DEFAULT_GRASS_ROUGHNESS }
fn default_grass_tip_alpha_cutoff() -> f32 { DEFAULT_GRASS_TIP_ALPHA_CUTOFF }
fn default_grass_normal_up_blend() -> f32 { DEFAULT_GRASS_NORMAL_UP_BLEND }

fn default_wind_strength() -> f32 { DEFAULT_WIND_STRENGTH }
fn default_wind_speed() -> f32 { DEFAULT_WIND_SPEED }
fn default_wind_frequency() -> f32 { DEFAULT_WIND_FREQUENCY }
fn default_wind_gust_strength() -> f32 { DEFAULT_WIND_GUST_STRENGTH }
fn default_wind_gust_speed() -> f32 { DEFAULT_WIND_GUST_SPEED }

fn default_scatter_density() -> f32 { DEFAULT_SCATTER_DENSITY }
fn default_scatter_scale_min() -> f32 { DEFAULT_SCATTER_SCALE_MIN }
fn default_scatter_scale_max() -> f32 { DEFAULT_SCATTER_SCALE_MAX }
fn default_scatter_align_to_normal() -> bool { DEFAULT_SCATTER_ALIGN_TO_NORMAL }
fn default_scatter_tilt_max_deg() -> f32 { DEFAULT_SCATTER_TILT_MAX_DEG }
fn default_scatter_random_yaw() -> bool { DEFAULT_SCATTER_RANDOM_YAW }

fn default_rule_slope_min_deg() -> f32 { DEFAULT_RULE_SLOPE_MIN_DEG }
fn default_rule_slope_max_deg() -> f32 { DEFAULT_RULE_SLOPE_MAX_DEG }
fn default_rule_slope_fade_deg() -> f32 { DEFAULT_RULE_SLOPE_FADE_DEG }
fn default_rule_height_min() -> f32 { DEFAULT_RULE_HEIGHT_MIN }
fn default_rule_height_max() -> f32 { DEFAULT_RULE_HEIGHT_MAX }
fn default_rule_height_fade() -> f32 { DEFAULT_RULE_HEIGHT_FADE }
fn default_rule_threshold() -> f32 { DEFAULT_RULE_THRESHOLD }
fn default_layer_condition_min_weight() -> f32 { DEFAULT_LAYER_CONDITION_MIN_WEIGHT }

// ============================================================
//  PropKind — プロップの種別
// ============================================================

/// プロップの種別。props.json の "kind" フィールド。
///
/// 種別によって「どのパラメータが意味を持つか」が変わる。
/// grass なら `TerrainProp::grass` と `wind`、model なら `model_path` が効く。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum PropKind {
    /// 手続き生成の草（既定）。メッシュアセット不要。
    #[default]
    Grass,
    /// 外部モデルアセット（木・岩など）。`model_path` を参照する。
    Model,
}

// ============================================================
//  GrassParams — 手続き生成される草 1 本の形状パラメータ
// ============================================================

/// 手続き生成される草 1 本の形状・見た目パラメータ。
///
/// `kind = Grass` のときだけ意味を持つ（model のときは無視される）。
/// 実際のメッシュ生成はレンダリング層の責務であり、本構造体は数値の器に徹する。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct GrassParams {
    /// 根元の葉幅（メートル）。先端に向かって 0 へ収束する前提。
    #[serde(default = "default_grass_width")]
    pub width: f32,
    /// 葉の高さ（メートル）。
    #[serde(default = "default_grass_height")]
    pub height: f32,
    /// 高さのランダム変動率（0..1）。個体ごとに height を ±この割合で振る。
    #[serde(default = "default_grass_height_variance")]
    pub height_variance: f32,
    /// 葉を構成する縦分割数（曲げの滑らかさ。1..=GRASS_MAX_SEGMENTS）。
    #[serde(default = "default_grass_segments")]
    pub segments: u32,
    /// true なら十字 2 枚、false なら 1 枚板。
    #[serde(default = "default_grass_cross_planes")]
    pub cross_planes: bool,
    /// 静止時の自然な垂れ（0=直立）。風とは別の、常時掛かる曲げ量。
    #[serde(default = "default_grass_bend")]
    pub bend: f32,
    /// 根元色（リニア RGB）。
    #[serde(default = "default_grass_color_bottom")]
    pub color_bottom: [f32; 3],
    /// 先端色（リニア RGB）。根元色との縦グラデーションになる。
    #[serde(default = "default_grass_color_top")]
    pub color_top: [f32; 3],
    /// ラフネス（0=鏡面, 1=完全拡散）。
    #[serde(default = "default_grass_roughness")]
    pub roughness: f32,
    /// 先端のアルファカットアウト閾値（0=無効）。
    #[serde(default = "default_grass_tip_alpha_cutoff")]
    pub tip_alpha_cutoff: f32,
    /// 陰影用法線を地表法線へ寄せる割合（0..1）。
    ///
    /// 0 = 真の幾何法線をそのまま使う（板ポリなので水平を向き、太陽が高いと黒落ちする）。
    /// 1 = 完全に地表法線へ置き換える（黒落ちしないが葉ごとの立体感が消える）。
    /// 形状は一切変えず、G-Buffer へ書く法線だけを曲げるパラメータである。
    #[serde(default = "default_grass_normal_up_blend")]
    pub normal_up_blend: f32,
}

impl Default for GrassParams {
    fn default() -> Self {
        Self {
            width:            DEFAULT_GRASS_WIDTH,
            height:           DEFAULT_GRASS_HEIGHT,
            height_variance:  DEFAULT_GRASS_HEIGHT_VARIANCE,
            segments:         DEFAULT_GRASS_SEGMENTS,
            cross_planes:     DEFAULT_GRASS_CROSS_PLANES,
            bend:             DEFAULT_GRASS_BEND,
            color_bottom:     DEFAULT_GRASS_COLOR_BOTTOM,
            color_top:        DEFAULT_GRASS_COLOR_TOP,
            roughness:        DEFAULT_GRASS_ROUGHNESS,
            tip_alpha_cutoff: DEFAULT_GRASS_TIP_ALPHA_CUTOFF,
            normal_up_blend:  DEFAULT_GRASS_NORMAL_UP_BLEND,
        }
    }
}

impl GrassParams {
    /// 実際に使う縦分割数（1..=GRASS_MAX_SEGMENTS へ丸めた値）。
    ///
    /// props.json に 0 や 1000 が書かれても頂点バッファが破綻しないよう、
    /// 参照側は必ずこのアクセサ経由で分割数を取ること。
    pub fn clamped_segments(&self) -> u32 {
        self.segments.clamp(1, GRASS_MAX_SEGMENTS)
    }
}

// ============================================================
//  WindParams — 風による揺れのパラメータ
// ============================================================

/// 風による揺れのパラメータ（頂点シェーダの揺らし式へ渡す係数）。
///
/// 「基本振動（速く細かい）」＋「突風（遅く大きい）」の 2 成分で構成する。
/// 単一の正弦波だけだと草原全体が同位相で揺れて機械的に見えるため。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct WindParams {
    /// 横揺れ幅（草の高さに対する比率）。0 で風の影響なし。
    #[serde(default = "default_wind_strength")]
    pub strength: f32,
    /// 基本振動の時間速度。
    #[serde(default = "default_wind_speed")]
    pub speed: f32,
    /// 基本振動の空間周波数（ワールド 1m あたりの位相進み）。
    #[serde(default = "default_wind_frequency")]
    pub frequency: f32,
    /// 突風成分の強さ（strength に対する追加分の比率）。
    #[serde(default = "default_wind_gust_strength")]
    pub gust_strength: f32,
    /// 突風成分の進行速度。
    #[serde(default = "default_wind_gust_speed")]
    pub gust_speed: f32,
}

impl Default for WindParams {
    fn default() -> Self {
        Self {
            strength:      DEFAULT_WIND_STRENGTH,
            speed:         DEFAULT_WIND_SPEED,
            frequency:     DEFAULT_WIND_FREQUENCY,
            gust_strength: DEFAULT_WIND_GUST_STRENGTH,
            gust_speed:    DEFAULT_WIND_GUST_SPEED,
        }
    }
}

// ============================================================
//  ScatterParams — 散布の姿勢・密度パラメータ
// ============================================================

/// 散布インスタンスの密度と姿勢のばらつきを決めるパラメータ。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ScatterParams {
    /// 1 m² あたりの候補点数。
    ///
    /// 「候補」であって確定本数ではない点に注意。各候補は
    /// `ScatterRule` の確率判定を通ったものだけがインスタンス化される。
    #[serde(default = "default_scatter_density")]
    pub density: f32,
    /// スケール下限（1.0 = 定義そのままのサイズ）。
    #[serde(default = "default_scatter_scale_min")]
    pub scale_min: f32,
    /// スケール上限。
    #[serde(default = "default_scatter_scale_max")]
    pub scale_max: f32,
    /// 地表法線に沿わせるか（false なら常に真上を向く）。
    ///
    /// 草は true（斜面で寝る）、木は false（斜面でも垂直に立つ）が自然。
    #[serde(default = "default_scatter_align_to_normal")]
    pub align_to_normal: bool,
    /// ランダム傾き上限（度）。基準軸からこの角度までランダムに倒す。
    #[serde(default = "default_scatter_tilt_max_deg")]
    pub tilt_max_deg: f32,
    /// Y 軸まわりのランダム回転を掛けるか。
    #[serde(default = "default_scatter_random_yaw")]
    pub random_yaw: bool,
}

impl Default for ScatterParams {
    fn default() -> Self {
        Self {
            density:         DEFAULT_SCATTER_DENSITY,
            scale_min:       DEFAULT_SCATTER_SCALE_MIN,
            scale_max:       DEFAULT_SCATTER_SCALE_MAX,
            align_to_normal: DEFAULT_SCATTER_ALIGN_TO_NORMAL,
            tilt_max_deg:    DEFAULT_SCATTER_TILT_MAX_DEG,
            random_yaw:      DEFAULT_SCATTER_RANDOM_YAW,
        }
    }
}

// ============================================================
//  LayerCondition — 地形レイヤ重みによる散布条件
// ============================================================

/// 「指定レイヤの重みが一定以上の場所にだけ生える」という条件。
///
/// レイヤ番号ではなく **レイヤ名** で指定するのは、layers.json の並び替えで
/// 散布定義が壊れないようにするため（データドリブンの前提）。
/// 名前が解決できないレイヤは重み 0 として扱われ、条件は不成立になる。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct LayerCondition {
    /// 対象レイヤ名（layers.json の `TerrainLayer::name` と一致させる）。
    pub layer: String,
    /// 要求する最小重み（0..1）。この値以上で条件が完全に成立する。
    #[serde(default = "default_layer_condition_min_weight")]
    pub min_weight: f32,
}

impl LayerCondition {
    /// 与えられたレイヤ重みに対する条件の成立度（0..1）を返す。
    ///
    /// `min_weight` 以上で 1、`min_weight - LAYER_CONDITION_FADE` 以下で 0、
    /// その間を smoothstep で補間する（境界で草が直線状に切れないようにする）。
    fn gate(&self, weight: f32) -> f32 {
        // フェード幅が意味を持たないほど狭い min_weight（0 付近）でも
        // ゼロ除算しないよう、LAYER_CONDITION_FADE は正の定数固定にしてある。
        smoothstep((weight - (self.min_weight - LAYER_CONDITION_FADE)) / LAYER_CONDITION_FADE)
    }
}

// ============================================================
//  ScatterRule — 斜度／高度／レイヤによる自動散布ルール
// ============================================================

/// 1 プロップの「どこに自動で生えるか」を表すルール。
///
/// 斜度ウィンドウ（度）×高度ウィンドウ（ワールド Y メートル）×
/// 全レイヤ条件の成立度、の積が「その地点に生える確率」になる。
/// ウィンドウの縁は `*_fade` 幅で smoothstep 補間される
/// （layers.rs の `LayerRule` と同じ流儀・同じ関数を使う）。
///
/// 例（草）: slope_min=0, slope_max=32, slope_fade=8,
///           layer_conditions=[{layer:"grass", min_weight:0.35}]
///   → 32 度より緩い草地レイヤの上にだけ生え、境界は滑らかに減る。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ScatterRule {
    /// 斜度ウィンドウ下限（度）。0 = 水平面。
    #[serde(default = "default_rule_slope_min_deg")]
    pub slope_min_deg: f32,
    /// 斜度ウィンドウ上限（度）。90 = 垂直面。
    #[serde(default = "default_rule_slope_max_deg")]
    pub slope_max_deg: f32,
    /// 斜度ウィンドウ両端のぼかし幅（度）。
    #[serde(default = "default_rule_slope_fade_deg")]
    pub slope_fade_deg: f32,
    /// 高度ウィンドウ下限（ワールド Y・メートル）。
    #[serde(default = "default_rule_height_min")]
    pub height_min: f32,
    /// 高度ウィンドウ上限（ワールド Y・メートル）。
    #[serde(default = "default_rule_height_max")]
    pub height_max: f32,
    /// 高度ウィンドウ両端のぼかし幅（メートル）。
    #[serde(default = "default_rule_height_fade")]
    pub height_fade: f32,
    /// 地形レイヤ重み条件（空なら無条件＝レイヤを問わない）。
    ///
    /// 複数指定した場合は **すべて** 満たす必要がある（成立度は積で合成）。
    #[serde(default)]
    pub layer_conditions: Vec<LayerCondition>,
    /// 最終確率がこの値未満なら生やさない（裾の切り落とし）。
    #[serde(default = "default_rule_threshold")]
    pub threshold: f32,
}

impl Default for ScatterRule {
    fn default() -> Self {
        Self {
            slope_min_deg:    DEFAULT_RULE_SLOPE_MIN_DEG,
            slope_max_deg:    DEFAULT_RULE_SLOPE_MAX_DEG,
            slope_fade_deg:   DEFAULT_RULE_SLOPE_FADE_DEG,
            height_min:       DEFAULT_RULE_HEIGHT_MIN,
            height_max:       DEFAULT_RULE_HEIGHT_MAX,
            height_fade:      DEFAULT_RULE_HEIGHT_FADE,
            layer_conditions: Vec::new(),
            threshold:        DEFAULT_RULE_THRESHOLD,
        }
    }
}

impl ScatterRule {
    /// 斜度（度）・高度（ワールド Y）・レイヤ重みから生える確率（0..1）を返す。
    ///
    /// - `slope_deg`: 地表法線から求めた斜度（0=水平, 90=垂直）。
    /// - `height`   : 接地点のワールド Y（メートル）。
    /// - `layer_weights_by_name`: レイヤ名 → その地点での重み（0..1）を返す関数。
    ///   未知のレイヤ名には 0 を返す実装であること（条件不成立になる）。
    ///
    /// 戻り値が `threshold` 未満のときは 0 に落とす（＝生やさない）。
    pub fn evaluate(
        &self,
        slope_deg: f32,
        height: f32,
        layer_weights_by_name: &dyn Fn(&str) -> f32,
    ) -> f32 {
        // ─── 斜度ウィンドウ × 高度ウィンドウ（layers.rs と同一の台形関数）───
        let s = window(slope_deg, self.slope_min_deg, self.slope_max_deg, self.slope_fade_deg);
        let h = window(height, self.height_min, self.height_max, self.height_fade);
        let mut p = s * h;

        // ─── レイヤ条件をすべて掛け合わせる（AND 合成）───
        //   1 つでも完全に不成立（gate=0）なら全体が 0 になる。
        for cond in &self.layer_conditions {
            // 早期脱出。ゼロ確定後に未使用のレイヤ重み問い合わせを走らせない
            // （エンジン層の layer_weight_at はチャンク検索を伴い安くはない）。
            if p <= 0.0 {
                return 0.0;
            }
            p *= cond.gate(layer_weights_by_name(&cond.layer));
        }

        let p = p.clamp(0.0, 1.0);
        // ─── 裾の切り落とし（threshold 未満は生やさない）───
        if p < self.threshold { 0.0 } else { p }
    }
}

// ============================================================
//  TerrainProp — 散布プロップ 1 種の定義
// ============================================================

/// 散布プロップ 1 種の定義（props.json の要素 1 つ）。
///
/// `kind` によって意味を持つフィールドが変わるが、未使用フィールドも
/// 既定値で必ず存在する（エディタで kind を切り替えたときに
/// 設定値が消えないようにするための設計判断）。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TerrainProp {
    /// 一意なプロップ ID（散布データやスクリプトから参照する安定キー）。
    pub id: String,
    /// 表示名（エディタの一覧に出す）。
    pub name: String,
    /// プロップ種別（grass / model）。
    #[serde(default)]
    pub kind: PropKind,
    /// モデルアセットの相対パス（`kind = Model` のときのみ意味を持つ）。
    #[serde(default)]
    pub model_path: Option<String>,
    /// 草の形状パラメータ（`kind = Grass` のときのみ意味を持つ）。
    #[serde(default)]
    pub grass: GrassParams,
    /// 風による揺れのパラメータ。
    #[serde(default)]
    pub wind: WindParams,
    /// 散布の密度・姿勢パラメータ。
    #[serde(default)]
    pub scatter: ScatterParams,
    /// 自動散布ルール。
    #[serde(default)]
    pub rule: ScatterRule,
}

impl Default for TerrainProp {
    /// 名前なし・草・既定パラメータのプロップ。
    ///
    /// 構造体更新記法（`..TerrainProp::default()`）で組み立てられるようにし、
    /// フィールド追加のたびに全リテラルを直さなくて済むようにする。
    fn default() -> Self {
        Self {
            id: String::new(),
            name: String::new(),
            kind: PropKind::Grass,
            model_path: None,
            grass: GrassParams::default(),
            wind: WindParams::default(),
            scatter: ScatterParams::default(),
            rule: ScatterRule::default(),
        }
    }
}

// ============================================================
//  TerrainPropSet — プロップ定義一式（props.json の中身）
// ============================================================

/// 散布プロップ定義の集合。`assets/terrain/props.json` の直列化対象。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TerrainPropSet {
    /// プロップ定義。最大 TERRAIN_MAX_PROPS 種まで持てる。
    pub props: Vec<TerrainProp>,
}

impl TerrainPropSet {
    /// JSON 文字列からプロップ定義を読む。
    ///
    /// プロップ数が TERRAIN_MAX_PROPS を超える場合は先頭 TERRAIN_MAX_PROPS 種へ
    /// 切り詰める（データ差し替えでの試行錯誤を止めないため、エラーにはしない）。
    /// プロップが 1 つも無い場合は既定セットへフォールバックする
    /// （props.json を空にしても地形が丸裸にならないための規約）。
    pub fn from_json_str(s: &str) -> Result<Self, serde_json::Error> {
        let mut set: TerrainPropSet = serde_json::from_str(s)?;
        set.props.truncate(TERRAIN_MAX_PROPS);
        if set.props.is_empty() {
            set = Self::default();
        }
        Ok(set)
    }

    /// ID からプロップを探す（添字と参照を返す）。
    ///
    /// 添字は散布インスタンスの `prop_id` に対応する。
    /// 同一 ID が複数あった場合は先頭を返す（props.json 側の不備扱い）。
    pub fn find_by_id(&self, id: &str) -> Option<(usize, &TerrainProp)> {
        self.props.iter().enumerate().find(|(_, p)| p.id == id)
    }

    /// 実際に扱われるプロップ数（= min(定義数, TERRAIN_MAX_PROPS)）。
    pub fn active_count(&self) -> usize {
        self.props.len().min(TERRAIN_MAX_PROPS)
    }
}

impl Default for TerrainPropSet {
    /// props.json が見つからない／壊れているときのフォールバック定義。
    ///
    /// 草 1 種（`grass_field`）＋木 1 種（`tree_pine`）。
    /// 草は layers.rs の既定レイヤセットの "grass" レイヤ（平地に載る）へ
    /// 紐付けてあるので、初期地面（Y=0 の平坦面）でそのまま草原が見える。
    /// 木はモデルアセットに依存しないよう `model_path: None`（エンジン層が
    /// プレースホルダを出すか、単に無視する）。
    fn default() -> Self {
        Self {
            props: vec![
                TerrainProp {
                    id: DEFAULT_GRASS_PROP_ID.to_string(),
                    name: "Grass Field".to_string(),
                    kind: PropKind::Grass,
                    scatter: ScatterParams {
                        density: DEFAULT_GRASS_PROP_DENSITY,
                        ..ScatterParams::default()
                    },
                    rule: ScatterRule {
                        slope_min_deg: DEFAULT_RULE_SLOPE_MIN_DEG,
                        slope_max_deg: DEFAULT_GRASS_PROP_SLOPE_MAX_DEG,
                        layer_conditions: vec![LayerCondition {
                            layer: DEFAULT_GRASS_LAYER_NAME.to_string(),
                            min_weight: DEFAULT_GRASS_PROP_LAYER_MIN_WEIGHT,
                        }],
                        ..ScatterRule::default()
                    },
                    ..TerrainProp::default()
                },
                TerrainProp {
                    id: DEFAULT_TREE_PROP_ID.to_string(),
                    name: "Pine Tree".to_string(),
                    kind: PropKind::Model,
                    model_path: None,
                    scatter: ScatterParams {
                        density: DEFAULT_TREE_PROP_DENSITY,
                        scale_min: DEFAULT_TREE_PROP_SCALE_MIN,
                        scale_max: DEFAULT_TREE_PROP_SCALE_MAX,
                        // 木は斜面でも垂直に立たせる（地表法線に沿わせない）。
                        align_to_normal: false,
                        tilt_max_deg: DEFAULT_TREE_PROP_TILT_MAX_DEG,
                        ..ScatterParams::default()
                    },
                    rule: ScatterRule {
                        slope_min_deg: DEFAULT_RULE_SLOPE_MIN_DEG,
                        slope_max_deg: DEFAULT_TREE_PROP_SLOPE_MAX_DEG,
                        layer_conditions: vec![LayerCondition {
                            layer: DEFAULT_GRASS_LAYER_NAME.to_string(),
                            min_weight: DEFAULT_TREE_PROP_LAYER_MIN_WEIGHT,
                        }],
                        threshold: DEFAULT_TREE_PROP_THRESHOLD,
                        ..ScatterRule::default()
                    },
                    ..TerrainProp::default()
                },
            ],
        }
    }
}
