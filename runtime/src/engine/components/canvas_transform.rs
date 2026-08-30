// ============================================================
//  canvas_transform.rs — 2D キャンバス空間トランスフォーム
//
//  Actor2D のデフォルトコンポーネント。
//  XY 平面上の位置・回転・スケール・ピボットを保持する。
//  3D の Transform に相当するが Z 軸方向の移動を持たない。
// ============================================================

use crate::engine::components::AspectRatioAxis;
use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

/// serde デフォルト用: true を返す（scale_transform / scale_size の既定値）。
fn default_true() -> bool {
    true
}

/// 2D キャンバス空間のトランスフォーム。
///
/// Actor2D が持つデフォルトコンポーネント。
/// 単位: position / pivot はワールドユニット（ピクセル換算はカメラの ortho スケールで決まる）。
/// rotation は Z 軸周りの回転（度）。
///
/// # ピボットの意味
/// pivot はオブジェクトローカル空間（スケール前）における回転・スケールの基準点オフセット。
/// 変換行列は T(position) * Rz(rotation) * S(scale) * T(-pivot) で計算される。
/// pivot = [0, 0] の場合は従来と同じ挙動（position が変換の起点）。
///
/// # アンカーの意味
/// anchor は親 Canvas（CanvasComponent）における position の基準点を [0,1] で指定する。
/// (0,0) = 親 Canvas の左上、(0.5,0.5) = 中央、(1,1) = 右下。
/// 実際の描画位置 = anchor * 親 Canvas サイズ + position。
/// 描画ループで親の CanvasComponent サイズを掛けて適用するため、
/// to_mat4_sized 自体には影響しない。
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CanvasTransform {
    /// XY 平面上の位置（ワールドユニット）。ピボット点のワールド座標。
    pub position: [f32; 2],
    /// Z 軸周りの回転（度）
    pub rotation: f32,
    /// XY スケール
    pub scale: [f32; 2],
    /// ローカル空間（スケール前）におけるピボットオフセット（ワールドユニット）。
    /// 回転・スケールの基準点を position からずらすために使用する。
    pub pivot: [f32; 2],
    /// 親 Canvas 内の基準点（正規化 [0,1]×[0,1]）。
    /// (0,0) = 左上、(0.5,0.5) = 中央、(1,1) = 右下。
    #[serde(default)]
    pub anchor: [f32; 2],
    /// このノードの**位置**を親の累積スケールに追従させるか（既定 true）。
    /// true  → eff_pos = position * parent_cumul_scale + anchor_off
    /// false → eff_pos = position + anchor_off（絶対座標）
    ///
    /// 旧モデルでは親 Canvas（CanvasComponent）がこのフラグを子へ宣言していたが、
    /// 現行モデルでは各ノードが自身の CanvasTransform で自己決定する。
    #[serde(default = "default_true")]
    pub scale_transform: bool,
    /// このノードの**サイズ**（スプライト／コライダー寸法）を親の累積スケールに
    /// 追従させるか（既定 true）。false = サイズ固定。
    #[serde(default = "default_true")]
    pub scale_size: bool,
    /// scale_size=true のとき、サイズスケールをアスペクト比維持で適用するか（既定 false）。
    #[serde(default)]
    pub keep_aspect_ratio: bool,
    /// アスペクト比維持の基準軸（keep_aspect_ratio=true のときのみ有効。既定 Width）。
    #[serde(default)]
    pub aspect_ratio_axis: AspectRatioAxis,
}

impl CanvasTransform {
    pub fn new(position: [f32; 2], rotation: f32, scale: [f32; 2]) -> Self {
        Self {
            position,
            rotation,
            scale,
            pivot: [0.0, 0.0],
            anchor: [0.0, 0.0],
            scale_transform: true,
            scale_size: true,
            keep_aspect_ratio: false,
            aspect_ratio_axis: AspectRatioAxis::Width,
        }
    }

    /// ワールド変換行列を計算する（CanvasComponent のサイズ指定あり）。
    ///
    /// pivot は [0, 1] に正規化された値で、実際のオフセット = pivot * [width, height]。
    /// - pivot (0, 0): コンテンツ左上が position に対応
    /// - pivot (0.5, 0.5): コンテンツ中央が position に対応
    /// - pivot (1, 1): コンテンツ右下が position に対応
    ///
    /// 計算式: T(position) * Rz(rotation) * S(scale) * T(-pivot_x*width, -pivot_y*height)
    pub fn to_mat4_sized(&self, width: f32, height: f32) -> [[f32; 4]; 4] {
        let rad = self.rotation.to_radians();
        let cos = rad.cos();
        let sin = rad.sin();
        let [sx, sy] = self.scale;
        let [px, py] = self.position;
        // 正規化 pivot をサイズで実際のローカルオフセットに変換する
        let pvx = self.pivot[0] * width;
        let pvy = self.pivot[1] * height;

        // T(position) * Rz * S * T(-pvx, -pvy) を展開した結果:
        // row0: [cos*sx,  -sin*sy,  0,  px - cos*sx*pvx + sin*sy*pvy]
        // row1: [sin*sx,   cos*sy,  0,  py - sin*sx*pvx - cos*sy*pvy]
        [
            [
                cos * sx,
                -sin * sy,
                0.0,
                px - cos * sx * pvx + sin * sy * pvy,
            ],
            [
                sin * sx,
                cos * sy,
                0.0,
                py - sin * sx * pvx - cos * sy * pvy,
            ],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ]
    }

    /// ワールド変換行列を計算する（サイズ 1×1 の単位キャンバス用）。
    ///
    /// CanvasComponent を持たないアクターや、ギズモ位置計算などで使用する。
    /// pivot が実際のサイズ依存オフセットになる必要がある場合は to_mat4_sized を使うこと。
    pub fn to_mat4(&self) -> [[f32; 4]; 4] {
        self.to_mat4_sized(1.0, 1.0)
    }

    /// スプライト描画用のサイズ付き行優先ローカル行列を返す。
    ///
    /// `to_mat4_sized` と同じ計算だが、X 基底に `width`、Y 基底に `height` を乗じることで
    /// ユニットクワッド [0,1]×[0,1] がスプライトのワールド矩形にマッピングされる行列を生成する。
    /// ペアレント行列（親のワールド行列）との乗算に使用する。
    ///
    /// 計算式:
    ///   col0 = cos*sx*w, col1 = -sin*sy*h, translation = T(position) * T(-pivot*size) で決定
    pub fn to_sprite_mat4(&self, width: f32, height: f32) -> [[f32; 4]; 4] {
        let rad = self.rotation.to_radians();
        let cos = rad.cos();
        let sin = rad.sin();
        let [sx, sy] = self.scale;
        let [px, py] = self.position;
        // ピボットをサイズで実際のオフセットに変換する
        let pvx = self.pivot[0] * width;
        let pvy = self.pivot[1] * height;
        // X 基底に width、Y 基底に height を乗じることで
        // ユニットクワッド(u,v)→スプライトサイズの変換を表す行優先行列。
        // 平行移動成分は to_mat4_sized と同じ（ピボット調整済み）。
        [
            [
                cos * sx * width,
                -sin * sy * height,
                0.0,
                px - cos * sx * pvx + sin * sy * pvy,
            ],
            [
                sin * sx * width,
                cos * sy * height,
                0.0,
                py - sin * sx * pvx - cos * sy * pvy,
            ],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ]
    }

    /// スキンメッシュ（`.sprite_mesh`）描画用のローカル行列を返す。
    ///
    /// `to_sprite_mat4` との違いは**引数の意味だけ**である:
    /// - `to_sprite_mat4(width, height)`: ユニットクワッド [0,1]² を実寸へ引き伸ばす。
    ///   ＝ 引数はスプライトの「サイズ」。
    /// - `to_mesh_mat4(scale_x, scale_y)`: 頂点が既にキャンバスピクセル座標で
    ///   実寸を持っているため、引数は**追加スケール係数**（既定 1.0）である。
    ///   親キャンバスの `scale_size` 追従分だけを掛ける用途に使う。
    ///
    /// この違いにより `pivot` の解釈も変わる: スキンメッシュでは
    /// `pivot * scale` ＝ **メッシュローカルのピクセルオフセット**として効く
    /// （scale=1 のとき pivot はそのままピクセル値）。
    ///
    /// 実装は `to_sprite_mat4` に委譲する（式が同一のため二重実装しない）。
    #[inline]
    pub fn to_mesh_mat4(&self, scale_x: f32, scale_y: f32) -> [[f32; 4]; 4] {
        self.to_sprite_mat4(scale_x, scale_y)
    }
}

impl Default for CanvasTransform {
    fn default() -> Self {
        Self {
            position: [0.0, 0.0],
            rotation: 0.0,
            scale: [1.0, 1.0],
            pivot: [0.0, 0.0],
            anchor: [0.0, 0.0],
            scale_transform: true,
            scale_size: true,
            keep_aspect_ratio: false,
            aspect_ratio_axis: AspectRatioAxis::Width,
        }
    }
}

impl Component for CanvasTransform {}
