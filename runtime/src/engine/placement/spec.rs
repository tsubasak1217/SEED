// ============================================================
//  placement/spec.rs — ロジック配置のパターン指定（データ定義）
//
//  【責務】
//  「どう並べるか」を表すデータだけを持つ。生成アルゴリズムは generate.rs、
//  シーンへの反映（アクタ生成・地形接地）は app::logic_placement_ops が持つ。
//
//  【平面の規約 — 全パターン共通】
//  パターンは常に **XZ 平面** 上に生成し、Y は「段（高さ）」として扱う。
//    ・3D 配置: そのまま (x, y, z) をアクタのワールド位置に使う
//    ・2D 配置: (x, z) を CanvasTransform の (X, Y) に写す（Y=段は使わない）
//  こうしておくと 2D/3D でパターン生成器を分けずに済み、エディタのプレビュー
//  （常に俯瞰の 2D 図）も 1 実装で足りる。
//
//  【角度の規約】
//  すべての角度は **度**。ヨー（Y 軸回り）は `yaw = atan2(dir.x, dir.z)` で定義する
//  （＝ヨー 0 のとき +Z 方向を向く）。エンジンの Transform.rotation は YXZ オイラー
//  の度表記なので、生成した yaw をそのまま rotation[1] に入れられる。
//
//  【フラットな 1 構造体にしている理由】
//  パターンごとに別構造体（enum のバリアント内包）にすると、エディタ側の
//  ダイアログが「パターンを切り替えるたびに前の入力値を失う」か、
//  C# 側で同型の enum を組み直す必要が出る。ユーザーがパターンを行き来しながら
//  値を詰める使い方（＝プレビューを見ながら決める）を優先し、
//  **全パターンのパラメータを 1 枚に持ち、使うものだけ読む**方式にする。
//  未使用フィールドは単に無視されるので、生成結果に影響しない。
// ============================================================

use serde::{Deserialize, Serialize};

// ─── 既定値関数（serde default 用。非ゼロ既定は関数で明示する）───────────

/// 個数の既定値。
fn default_count() -> u32 { 8 }
/// 半径の既定値 [m]。
fn default_radius() -> f32 { 5.0 }
/// 角度範囲の既定値 [度]（全周）。
fn default_angle_span() -> f32 { 360.0 }
/// グリッドの行数・列数の既定値。
fn default_grid_axis() -> u32 { 3 }
/// グリッドの段数の既定値（1 段＝平面）。
fn default_grid_layers() -> u32 { 1 }
/// 間隔の既定値 [m]。
fn default_spacing() -> f32 { 2.0 }
/// 中心揃えの既定値（グリッド・直線とも既定でオン）。
fn default_true() -> bool { true }
/// ランダム散布の範囲サイズ既定値 [m]。
fn default_area_size() -> f32 { 10.0 }
/// スケールばらつきの既定値（0 = ばらつかない）。
fn default_zero() -> f32 { 0.0 }

// ─── パターン種別 ─────────────────────────────────────────────

/// 配置パターンの種別。
///
/// serde 表現は**バリアント名そのままの文字列**（"Circle" 等）。
/// C# 側 `PlacementPattern` の `ToString()` と一致させること。
#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize, Deserialize)]
pub enum PlacementPattern {
    /// 円形・円弧（中心から半径 r・角度範囲 span に等間隔）。
    Circle,
    /// グリッド（行 × 列 × 段）。
    Grid,
    /// 直線（方向角と間隔で等間隔）。
    Line,
    /// ランダム散布（拒否サンプリングで最小間隔を保証する）。
    Random,
}

impl Default for PlacementPattern {
    fn default() -> Self { Self::Circle }
}

impl PlacementPattern {
    /// 生成されるグループフォルダの既定名に使う日本語表示名。
    pub fn display_name(self) -> &'static str {
        match self {
            Self::Circle => "円形配置",
            Self::Grid   => "グリッド配置",
            Self::Line   => "直線配置",
            Self::Random => "ランダム配置",
        }
    }
}

// ─── PlacementSpec ────────────────────────────────────────────

/// 配置パターンとそのパラメータ一式。
///
/// 全フィールドに `#[serde(default)]` 相当を付け、エディタが一部フィールドを
/// 送らなくても既定値で生成できるようにする（IPC の後方互換の要）。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PlacementSpec {
    // ── パターン種別 ────────────────────────────────────────
    /// 使用するパターン。
    #[serde(default)]
    pub pattern: PlacementPattern,

    // ── 共通 ────────────────────────────────────────────────
    /// 生成個数（Grid は行×列×段で決まるためこの値を使わない）。
    #[serde(default = "default_count")]
    pub count: u32,
    /// 乱数シード（ジッター・ランダム散布が使う）。同じ値なら常に同じ結果。
    #[serde(default)]
    pub seed: u64,
    /// 位置ジッターの振れ幅 [m]（各軸 ±この値）。0 でジッター無し。
    #[serde(default = "default_zero")]
    pub jitter_pos: f32,
    /// 回転ジッターの振れ幅 [度]（ヨー ±この値）。0 でジッター無し。
    #[serde(default = "default_zero")]
    pub jitter_rot: f32,
    /// 進行方向（点列の進む向き）を向かせるか。
    ///
    /// 円形では接線方向、直線では線の方向、グリッド・ランダムでは
    /// 「1 つ前の点から自分へ向かうベクトル」を向く。
    #[serde(default)]
    pub face_forward: bool,

    // ── 円形／円弧 ──────────────────────────────────────────
    /// 半径 [m]。
    #[serde(default = "default_radius")]
    pub radius: f32,
    /// 開始角 [度]。
    #[serde(default)]
    pub start_angle: f32,
    /// 角度範囲 [度]（360 で全周）。
    #[serde(default = "default_angle_span")]
    pub angle_span: f32,
    /// 中心を向かせるか（`face_forward` より優先される）。
    #[serde(default)]
    pub face_center: bool,

    // ── グリッド ────────────────────────────────────────────
    /// 行数（Z 方向の個数）。
    #[serde(default = "default_grid_axis")]
    pub rows: u32,
    /// 列数（X 方向の個数）。
    #[serde(default = "default_grid_axis")]
    pub cols: u32,
    /// 段数（Y 方向の個数。2D 配置では 1 として扱う）。
    #[serde(default = "default_grid_layers")]
    pub layers: u32,
    /// X 方向の間隔 [m]。
    #[serde(default = "default_spacing")]
    pub spacing_x: f32,
    /// Z 方向の間隔 [m]。
    #[serde(default = "default_spacing")]
    pub spacing_z: f32,
    /// Y 方向（段）の間隔 [m]。
    #[serde(default = "default_spacing")]
    pub spacing_y: f32,
    /// 基準点をグリッドの中心に置くか（false なら基準点が隅になる）。
    /// 直線パターンでも「線の中心を基準点に置くか」として共用する。
    #[serde(default = "default_true")]
    pub center_align: bool,
    /// 市松オフセット（奇数行を X 方向へ半間隔ずらす）。
    #[serde(default)]
    pub checker_offset: bool,

    // ── 直線 ────────────────────────────────────────────────
    /// 直線の方向角 [度]（`yaw = atan2(dir.x, dir.z)` 規約）。
    #[serde(default)]
    pub line_angle: f32,
    /// 直線上の点間隔 [m]。
    #[serde(default = "default_spacing")]
    pub line_spacing: f32,

    // ── ランダム散布 ────────────────────────────────────────
    /// 範囲の形状。true = 円、false = 矩形。
    #[serde(default = "default_true")]
    pub area_circle: bool,
    /// 円範囲の半径 [m]。
    #[serde(default = "default_radius")]
    pub area_radius: f32,
    /// 矩形範囲の X 幅 [m]。
    #[serde(default = "default_area_size")]
    pub area_size_x: f32,
    /// 矩形範囲の Z 幅 [m]。
    #[serde(default = "default_area_size")]
    pub area_size_z: f32,
    /// 点同士の最小間隔 [m]（XZ 距離）。0 で無制限。
    #[serde(default = "default_zero")]
    pub min_spacing: f32,
    /// ヨーを 0..360 度でランダム化するか。
    #[serde(default)]
    pub random_rotation: bool,
    /// スケールのばらつき（±この割合。0.2 なら 0.8〜1.2 倍）。
    #[serde(default = "default_zero")]
    pub scale_variance: f32,
}

impl Default for PlacementSpec {
    fn default() -> Self {
        Self {
            pattern:         PlacementPattern::default(),
            count:           default_count(),
            seed:            0,
            jitter_pos:      default_zero(),
            jitter_rot:      default_zero(),
            face_forward:    false,
            radius:          default_radius(),
            start_angle:     0.0,
            angle_span:      default_angle_span(),
            face_center:     false,
            rows:            default_grid_axis(),
            cols:            default_grid_axis(),
            layers:          default_grid_layers(),
            spacing_x:       default_spacing(),
            spacing_z:       default_spacing(),
            spacing_y:       default_spacing(),
            center_align:    default_true(),
            checker_offset:  false,
            line_angle:      0.0,
            line_spacing:    default_spacing(),
            area_circle:     default_true(),
            area_radius:     default_radius(),
            area_size_x:     default_area_size(),
            area_size_z:     default_area_size(),
            min_spacing:     default_zero(),
            random_rotation: false,
            scale_variance:  default_zero(),
        }
    }
}

// ─── PlacementPoint / PlacementResult ─────────────────────────

/// 生成された配置点 1 個（基準点を原点とするローカル座標）。
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct PlacementPoint {
    /// 位置（基準点相対。XZ 平面 + Y は段）。
    pub position: [f32; 3],
    /// 姿勢（度・YXZ オイラー）。生成器はヨー（`rotation[1]`）のみを設定する。
    pub rotation: [f32; 3],
    /// 拡縮倍率（ランダム散布のばらつき以外は等倍）。
    pub scale: [f32; 3],
}

impl Default for PlacementPoint {
    fn default() -> Self {
        Self {
            position: [0.0, 0.0, 0.0],
            rotation: [0.0, 0.0, 0.0],
            scale:    [1.0, 1.0, 1.0],
        }
    }
}

/// 生成結果。点列と、生成側が伝えたい警告（達成できなかった要求）を持つ。
#[derive(Clone, Debug, Default)]
pub struct PlacementResult {
    /// 生成された点列（先頭から配置順）。
    pub points: Vec<PlacementPoint>,
    /// 要求を満たせなかった場合の警告文（無ければ None）。
    ///
    /// 例: 最小間隔が厳しすぎて要求個数を置けなかった場合。
    /// **黙って減らさない**ためにエディタへそのまま表示する。
    pub warning: Option<String>,
}
