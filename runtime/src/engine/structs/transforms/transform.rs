use crate::engine::structs::tensor::{Vector3, Mat4x4};
use super::quaternion::Quaternion;

/// オブジェクトのワールド空間における位置・回転・スケールを保持する構造体。
///
/// # フィールド
/// - `position` : ワールド座標（Vector3<f32>）
/// - `rotation` : 回転（Quaternion）
/// - `scale`    : 各軸のスケール（Vector3<f32>）
///
/// # TRS 行列
/// `to_matrix()` で Translation * Rotation * Scale の順に合成した 4x4 行列を取得できる。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Transform {
    pub position: Vector3<f32>,
    pub rotation: Quaternion,
    pub scale:    Vector3<f32>,
}

impl Transform {
    /// 成分を直接指定して生成する。
    pub fn new(position: Vector3<f32>, rotation: Quaternion, scale: Vector3<f32>) -> Self {
        Self { position, rotation, scale }
    }

    /// 位置 = 原点、回転 = 無回転、スケール = 1.0 の恒等変換を返す。
    pub fn identity() -> Self {
        Self {
            position: Vector3::zero(),
            rotation: Quaternion::identity(),
            scale:    Vector3::new(1.0, 1.0, 1.0),
        }
    }

    // ─── 行列変換 ─────────────────────────────────────────────

    /// TRS 行列（Translation * Rotation * Scale）を返す。
    ///
    /// ワールド空間へのオブジェクト変換行列として使用する。
    pub fn to_matrix(self) -> Mat4x4<f32> {
        let t = Mat4x4::translation(self.position.x, self.position.y, self.position.z);
        let r = self.rotation.to_rotation_matrix();
        let s = Mat4x4::scale(self.scale.x, self.scale.y, self.scale.z);
        t * r * s
    }

    // ─── 方向ベクトル ─────────────────────────────────────────

    /// オブジェクトの前方向（+Z）を返す。
    #[inline]
    pub fn forward(self) -> Vector3<f32> { self.rotation.forward() }

    /// オブジェクトの右方向（+X）を返す。
    #[inline]
    pub fn right(self) -> Vector3<f32> { self.rotation.right() }

    /// オブジェクトの上方向（+Y）を返す。
    #[inline]
    pub fn up(self) -> Vector3<f32> { self.rotation.up() }
}

impl Default for Transform {
    fn default() -> Self { Self::identity() }
}
