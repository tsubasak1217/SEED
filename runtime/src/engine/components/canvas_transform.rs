// ============================================================
//  canvas_transform.rs — 2D キャンバス空間トランスフォーム
//
//  Actor2D のデフォルトコンポーネント。
//  XY 平面上の位置・回転・スケール・ピボットを保持する。
//  3D の Transform に相当するが Z 軸方向の移動を持たない。
// ============================================================

use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;

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
#[derive(Debug, Clone, Serialize, Deserialize)]
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
}

impl CanvasTransform {
    pub fn new(position: [f32; 2], rotation: f32, scale: [f32; 2]) -> Self {
        Self { position, rotation, scale, pivot: [0.0, 0.0], anchor: [0.0, 0.0] }
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
            [cos * sx,  -sin * sy,  0.0,  px - cos * sx * pvx + sin * sy * pvy],
            [sin * sx,   cos * sy,  0.0,  py - sin * sx * pvx - cos * sy * pvy],
            [0.0,        0.0,       1.0,  0.0],
            [0.0,        0.0,       0.0,  1.0],
        ]
    }

    /// ワールド変換行列を計算する（サイズ 1×1 の単位キャンバス用）。
    ///
    /// CanvasComponent を持たないアクターや、ギズモ位置計算などで使用する。
    /// pivot が実際のサイズ依存オフセットになる必要がある場合は to_mat4_sized を使うこと。
    pub fn to_mat4(&self) -> [[f32; 4]; 4] {
        self.to_mat4_sized(1.0, 1.0)
    }
}

impl Default for CanvasTransform {
    fn default() -> Self {
        Self {
            position: [0.0, 0.0],
            rotation: 0.0,
            scale:    [1.0, 1.0],
            pivot:    [0.0, 0.0],
            anchor:   [0.0, 0.0],
        }
    }
}

impl Component for CanvasTransform {}
