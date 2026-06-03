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

use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;

// ─── GravityMode ─────────────────────────────────────────────────────────────

/// 2D 物理シミュレーションの重力方向モード。
///
/// `ScreenDown`（デフォルト）: 常にスクリーン下方向を重力正方向とする。
///   3D キャンバスの場合はキャンバスの Z 軸回転を参照して重力方向を補正する
///   （ゲーム画面をそのまま回転させても、プレイヤー視点の「下方向」は変わらない）。
///
/// `CanvasDown`: キャンバスの下方向（ローカル Y+）を重力正方向とする。
///   3D キャンバスを回転させると重力もキャンバスに追従する。
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum GravityMode {
    /// スクリーン下方向を重力正方向とする（デフォルト）
    #[default]
    ScreenDown,
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
    fn default() -> Self { Self::Width }
}

// ─── CanvasViewportRef ────────────────────────────────────────────────────────

/// キャンバスのアンカー計算・自動スケールで参照するビューポートの種別。
///
/// `Window`（デフォルト）: ウィンドウ全体のサイズを基準とする。
/// `Camera { actor_name, slot_name }`: 指定カメラコンポーネントの描画範囲を基準とする。
/// - 編集モード: カメラの target_width × target_height（設計解像度）
/// - プレイモード: カメラが実際に描画するビューポートのピクセルサイズ
#[derive(Clone, Serialize, Deserialize, PartialEq, Debug)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum CanvasViewportRef {
    /// ウィンドウ全体をアンカー/スケール基準とする（デフォルト）
    Window,
    /// 指定カメラの描画範囲をアンカー/スケール基準とする
    Camera {
        /// 参照するカメラアクターの名前
        actor_name: String,
        /// 参照するカメラコンポーネントのスロット名
        slot_name:  String,
    },
}

impl Default for CanvasViewportRef {
    fn default() -> Self { Self::Window }
}

// ─── CanvasComponentData ──────────────────────────────────────────────────────

/// CanvasComponent のシリアライズ用データ。
#[derive(Clone, Serialize, Deserialize)]
pub struct CanvasComponentData {
    /// キャンバスの基準幅（ワールドユニット）
    pub width:  f32,
    /// キャンバスの基準高さ（ワールドユニット）
    pub height: f32,
    /// 子UIのサイズをキャンバスのスケールに追従させるか。false = サイズ固定。
    #[serde(default)]
    pub scale_size:      bool,
    /// 子UIのトランスフォーム（位置）をキャンバスのスケールに追従させるか。false = 絶対座標固定。
    #[serde(default)]
    pub scale_transform: bool,
    /// 画面サイズに自動スケール。親キャンバスを持たないルートキャンバスにのみ有効。
    /// true のとき、ビューポートサイズ変化に応じて子 UI を proportional にスケールする。
    #[serde(default = "default_auto_scale")]
    pub auto_scale: bool,
    /// アンカー/スケール計算で参照するビューポートの種別。
    #[serde(default)]
    pub viewport_ref: CanvasViewportRef,
    /// scale_size=true のとき、子アイテムのサイズをアスペクト比維持でスケールするか。
    #[serde(default)]
    pub keep_aspect_ratio: bool,
    /// アスペクト比維持の基準軸（keep_aspect_ratio=true のときのみ有効）。
    #[serde(default)]
    pub aspect_ratio_axis: AspectRatioAxis,
    /// 2D 物理シミュレーションの重力方向モード。
    #[serde(default)]
    pub gravity_mode: GravityMode,
    /// 3D キャンバス専用ピボット（正規化値 [0,1]×[0,1]）。
    /// アクター位置がキャンバスのどの点に対応するかを指定する。
    /// (0,0) = 左上、(0.5,0.5) = 中央、(1,1) = 右下。
    /// Actor2D にアタッチした場合は無視される。
    #[serde(default)]
    pub pivot: [f32; 2],
}

fn default_auto_scale() -> bool { true }

// ─── CanvasComponent ─────────────────────────────────────────────────────────

/// UI キャンバスコンポーネント。
///
/// Actor2D にアタッチして UI レイアウトの基準サイズを指定する。
/// Actor3D にアタッチすると 3D ワールド空間にキャンバスを配置する。
/// エディタ上では CanvasTransform.position を中心に width × height の
/// 矩形アウトラインが表示される。
///
/// # スケールモード
/// キャンバスのスケール（CanvasTransform.scale）が変化したとき、
/// 直接の子 UI の挙動を以下の 2 フラグで制御する。
///   - scale_transform: true → 子の位置にスケールを乗算（10,10 → 20,20）
///   - scale_size:      true → 子のサイズにスケールを乗算（100px → 200px）
/// 回転は常に追従する。デフォルトは両方 false（絶対座標・絶対サイズ）。
///
/// # 画面サイズ自動スケール
/// auto_scale=true（デフォルト）かつ親キャンバスを持たないルートキャンバスのとき、
/// ビューポートサイズに応じて子 UI を proportional にスケールする。
/// Actor3D アタッチ時は使用しない。
///
/// # ビューポート参照
/// viewport_ref でアンカー計算・自動スケールの基準を「ウィンドウ全体」か
/// 「指定カメラの描画範囲」かを選択できる。
///
/// # 3D キャンバスのピボット
/// pivot（Actor3D アタッチ時のみ有効）でアクター位置がキャンバスのどの点に
/// 対応するかを指定する。(0,0)=左上, (0.5,0.5)=中央, (1,1)=右下。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CanvasComponent {
    /// キャンバスの基準幅（ワールドユニット）
    pub width:  f32,
    /// キャンバスの基準高さ（ワールドユニット）
    pub height: f32,
    /// 子UIのサイズをキャンバスのスケールに追従させるか。false = サイズ固定。
    #[serde(default)]
    pub scale_size:      bool,
    /// 子UIのトランスフォーム（位置）をキャンバスのスケールに追従させるか。false = 絶対座標固定。
    #[serde(default)]
    pub scale_transform: bool,
    /// 画面サイズに自動スケール（デフォルト true）。
    /// 親キャンバスを持たないルートキャンバスにのみ有効。Actor3D アタッチ時は無効。
    #[serde(default = "default_auto_scale")]
    pub auto_scale: bool,
    /// アンカー/スケール計算で参照するビューポートの種別。
    #[serde(default)]
    pub viewport_ref: CanvasViewportRef,
    /// scale_size=true のとき、子アイテムのサイズをアスペクト比維持でスケールするか。
    #[serde(default)]
    pub keep_aspect_ratio: bool,
    /// アスペクト比維持の基準軸（keep_aspect_ratio=true のときのみ有効）。
    #[serde(default)]
    pub aspect_ratio_axis: AspectRatioAxis,
    /// 2D 物理シミュレーションの重力方向モード。
    #[serde(default)]
    pub gravity_mode: GravityMode,
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
            width:             self.width,
            height:            self.height,
            scale_size:        self.scale_size,
            scale_transform:   self.scale_transform,
            auto_scale:        self.auto_scale,
            viewport_ref:      self.viewport_ref.clone(),
            keep_aspect_ratio: self.keep_aspect_ratio,
            aspect_ratio_axis: self.aspect_ratio_axis.clone(),
            gravity_mode:      self.gravity_mode,
            pivot:             self.pivot,
        }
    }
}

impl Default for CanvasComponent {
    fn default() -> Self {
        Self {
            width:             1920.0,
            height:            1080.0,
            scale_size:        false,
            scale_transform:   false,
            auto_scale:        true,
            viewport_ref:      CanvasViewportRef::Window,
            keep_aspect_ratio: false,
            aspect_ratio_axis: AspectRatioAxis::Width,
            gravity_mode:      GravityMode::ScreenDown,
            pivot:             [0.0, 0.0],
        }
    }
}

impl Component for CanvasComponent {}
