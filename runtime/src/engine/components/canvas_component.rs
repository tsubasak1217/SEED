// ============================================================
//  canvas_component.rs — UI キャンバスコンポーネント
//
//  Actor2D にアタッチすると 2D スクリーンスペース / ワールドスペース Canvas として動作する。
//  Actor3D にアタッチすると 3D ワールド空間に配置される Canvas として動作する。
//
//  3D Canvas（Actor3D にアタッチ）:
//   - Actor3D の ActorTransform（位置・回転・スケール）でワールド空間に配置される。
//   - 子アクターは従来通り CanvasTransform（2D ローカル座標）を使用する。
//   - 1px = 1cm（= CANVAS_WORLD_SCALE = 0.01 ワールド単位）でレンダリングされる。
//   - 2D 物理はキャンバスローカル座標で完結し、他のキャンバスと干渉しない。
// ============================================================

use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

// ─── GravityMode ─────────────────────────────────────────────────────────────

/// 2D 物理シミュレーションの重力方向モード。
///
/// `WorldDown`（デフォルト）: 常にワールド下方向を重力正方向とする。
///   3D キャンバスの 2D 物理はキャンバスローカル空間で走るため、キャンバスの
///   ワールド姿勢からワールド下方向 [0,-1,0] をローカル 2D 軸へ射影して重力を求める。
///   これによりキャンバスを 3D 空間で回転させても、中のオブジェクトは常に
///   ワールドの下（＝薄い箱を傾けたときにビー玉が転がる先）へ落ちる。
///
/// `CanvasDown`: キャンバスの下方向（ローカル Y+）を重力正方向とする。
///   ワールドで回転させても重力はキャンバスローカル下のまま（箱の同じ壁へ落ち続ける）。
///
/// # serde 互換
/// 旧名 `screen_down` は `WorldDown` の alias として読み込む（既存 .scene 互換）。
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum GravityMode {
    /// ワールド下方向を重力正方向とする（デフォルト）
    #[default]
    #[serde(alias = "screen_down")]
    WorldDown,
    /// キャンバス下方向（ローカル Y+）を重力正方向とする
    CanvasDown,
}

// ─── AspectRatioAxis ──────────────────────────────────────────────────────────

/// scale_size=true のとき、アスペクト比維持に使用する基準軸。
///
/// `Width`: 幅のスケール係数を縦横両方に適用する（幅基準）。
/// `Height`: 高さのスケール係数を縦横両方に適用する（高さ基準）。
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum AspectRatioAxis {
    Width,
    Height,
}

impl Default for AspectRatioAxis {
    fn default() -> Self {
        Self::Width
    }
}

// ─── CanvasDrawZone ───────────────────────────────────────────────────────────

/// ビューポート所属キャンバスの描画ゾーン（canvas_camera_rework.md 第 4 節）。
///
/// 描画順（奥→手前）: zfar ← 背景キャンバス | 3D ワールド | 前面キャンバス → znear
///
/// `Foreground`（デフォルト）: 従来どおり 3D ワールドの上に重ねるオーバーレイ。
/// `Background`: 3D ワールドより奥（カメラのクリアカラーの上・ワールドの下）に描画する。
///
/// ゾーンは**ルートキャンバス**にのみ意味を持ち、子キャンバスはルートに従属する。
/// ワールド配置（Actor3D アタッチ）のキャンバスには適用されない（3D 深度で決まるため）。
///
/// # serde 互換性
/// `#[serde(default)]` により、`draw_zone` フィールドを持たない既存の保存データは
/// `Foreground`（従来動作）として読み込まれる。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum CanvasDrawZone {
    /// 3D ワールドより手前に描画するオーバーレイ（デフォルト・従来動作）
    #[default]
    Foreground,
    /// 3D ワールドより奥（クリアカラーの上）に描画する背景
    Background,
}

// ─── CanvasViewportRef ────────────────────────────────────────────────────────

/// キャンバスのアンカー計算・自動スケールで参照する基準領域の種別。
///
/// `MainCamera`（新規作成時のデフォルト）: 同一世界線内で最初に見つかった
///   `is_main = true` のカメラの実効描画領域（レターボックス等の黒帯を除いた
///   ゲーム表示矩形）を基準とする。メインカメラが存在しない場合は自動的に
///   ウィンドウ全体へフォールバックする。
/// `Window`: ウィンドウ全体のサイズを基準とする。
/// `Camera { actor_name, slot_name }`: 指定カメラコンポーネントの描画範囲を基準とする。
/// - 編集モード: カメラの target_width × target_height（設計解像度）
/// - プレイモード: カメラが実際に描画するビューポートのピクセルサイズ
///
/// # serde 互換性
/// `Default` トレイト実装（= `#[serde(default)]` が使う値）は `Window` のまま維持する。
/// これにより `viewport_ref` フィールドを持たない既存の保存データは従来どおり
/// `Window` として読み込まれる。新規作成コンポーネントのデフォルトのみ
/// `CanvasComponent::default()` 側で `MainCamera` を指定する。
#[derive(Clone, Serialize, Deserialize, PartialEq, Debug)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum CanvasViewportRef {
    /// ウィンドウ全体をアンカー/スケール基準とする
    Window,
    /// メインカメラ（is_main = true）の実効表示領域を基準とする。
    /// メインカメラが無い場合はウィンドウ基準として扱う。
    MainCamera,
    /// 指定カメラの描画範囲をアンカー/スケール基準とする
    Camera {
        /// 参照するカメラアクターの名前
        actor_name: String,
        /// 参照するカメラコンポーネントのスロット名
        slot_name: String,
    },
}

impl Default for CanvasViewportRef {
    // 注意: これは「フィールド欠落時の deserialization デフォルト」。
    // 既存保存データとの互換のため Window を維持する（上記 doc コメント参照）。
    fn default() -> Self {
        Self::Window
    }
}

// ─── CanvasComponentData ──────────────────────────────────────────────────────

/// CanvasComponent のシリアライズ用データ。
#[derive(Clone, Serialize, Deserialize)]
pub struct CanvasComponentData {
    /// キャンバスの基準幅（ワールドユニット）
    pub width: f32,
    /// キャンバスの基準高さ（ワールドユニット）
    pub height: f32,
    /// 画面サイズに自動スケール。親キャンバスを持たないルートキャンバスにのみ有効。
    /// true のとき、ビューポートサイズ変化に応じて子 UI を proportional にスケールする。
    #[serde(default = "default_auto_scale")]
    pub auto_scale: bool,
    /// アンカー/スケール計算で参照するビューポートの種別。
    #[serde(default)]
    pub viewport_ref: CanvasViewportRef,
    /// 2D 物理シミュレーションの重力方向モード。
    #[serde(default)]
    pub gravity_mode: GravityMode,
    /// 描画ゾーン（ビューポート所属のルートキャンバスのみ有効）。
    /// 旧データ互換のため #[serde(default)] で Foreground（従来動作）として読み込む。
    #[serde(default)]
    pub draw_zone: CanvasDrawZone,
    /// 3D キャンバス専用ピボット（正規化値 [0,1]×[0,1]）。
    /// アクター位置がキャンバスのどの点に対応するかを指定する。
    /// (0,0) = 左上、(0.5,0.5) = 中央、(1,1) = 右下。
    /// Actor2D にアタッチした場合は無視される。
    #[serde(default)]
    pub pivot: [f32; 2],
}

fn default_auto_scale() -> bool {
    true
}

// ─── CanvasComponent ─────────────────────────────────────────────────────────

/// UI キャンバスコンポーネント。
///
/// Actor2D にアタッチして UI レイアウトの基準サイズを指定する。
/// Actor3D にアタッチすると 3D ワールド空間にキャンバスを配置する。
/// エディタ上では CanvasTransform.position を中心に width × height の
/// 矩形アウトラインが表示される。
///
/// # スケールモード
/// 位置・サイズをスケールに追従させるか（scale_transform / scale_size /
/// keep_aspect_ratio / aspect_ratio_axis）は**各ノードの CanvasTransform**が
/// 自己決定する（旧モデルでは親 Canvas がこれを子へ宣言していた）。
///
/// # 画面サイズ自動スケール
/// auto_scale=true（デフォルト）かつ親キャンバスを持たないルートキャンバスのとき、
/// ビューポートサイズに応じて子 UI を proportional にスケールする。
/// Actor3D アタッチ時は使用しない。
///
/// # 基準領域（ビューポート参照）
/// viewport_ref でアンカー計算・自動スケールの基準を「メインカメラの実効表示領域
/// （新規作成時のデフォルト）」「ウィンドウ全体」「指定カメラの描画範囲」から選択できる。
///
/// # 3D キャンバスのピボット
/// pivot（Actor3D アタッチ時のみ有効）でアクター位置がキャンバスのどの点に
/// 対応するかを指定する。(0,0)=左上, (0.5,0.5)=中央, (1,1)=右下。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CanvasComponent {
    /// キャンバスの基準幅（ワールドユニット）
    pub width: f32,
    /// キャンバスの基準高さ（ワールドユニット）
    pub height: f32,
    /// 画面サイズに自動スケール（デフォルト true）。
    /// 親キャンバスを持たないルートキャンバスにのみ有効。Actor3D アタッチ時は無効。
    #[serde(default = "default_auto_scale")]
    pub auto_scale: bool,
    /// アンカー/スケール計算で参照するビューポートの種別。
    #[serde(default)]
    pub viewport_ref: CanvasViewportRef,
    /// 2D 物理シミュレーションの重力方向モード。
    #[serde(default)]
    pub gravity_mode: GravityMode,
    /// 描画ゾーン（ビューポート所属のルートキャンバスのみ有効）。
    /// Foreground = 3D ワールドの手前（従来のオーバーレイ・デフォルト）、
    /// Background = 3D ワールドの奥（クリアカラーの上）。
    /// 子キャンバス・ワールド配置キャンバスでは無視される。
    #[serde(default)]
    pub draw_zone: CanvasDrawZone,
    /// 3D キャンバス専用ピボット（正規化値 [0,1]×[0,1]）。
    /// アクター位置がキャンバスのどの点に対応するかを指定する。
    /// (0,0) = 左上（デフォルト）、(0.5,0.5) = 中央、(1,1) = 右下。
    /// Actor2D にアタッチした場合は無視される。
    #[serde(default)]
    pub pivot: [f32; 2],
}

impl CanvasComponent {
    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> CanvasComponentData {
        CanvasComponentData {
            width: self.width,
            height: self.height,
            auto_scale: self.auto_scale,
            viewport_ref: self.viewport_ref.clone(),
            gravity_mode: self.gravity_mode,
            draw_zone: self.draw_zone,
            pivot: self.pivot,
        }
    }
}

impl Default for CanvasComponent {
    fn default() -> Self {
        Self {
            width: 1920.0,
            height: 1080.0,
            auto_scale: true,
            // 新規作成コンポーネントはメインカメラ基準をデフォルトとする
            // （メインカメラが無い場合は実行時にウィンドウ基準へフォールバック）
            viewport_ref: CanvasViewportRef::MainCamera,
            gravity_mode: GravityMode::WorldDown,
            // 新規作成時は従来どおり前面（オーバーレイ）
            draw_zone: CanvasDrawZone::Foreground,
            pivot: [0.0, 0.0],
        }
    }
}

impl Component for CanvasComponent {}
