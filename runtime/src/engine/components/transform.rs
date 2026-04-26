// ============================================================
//  transform.rs — Transform コンポーネント
//
//  Actor のワールド空間における位置・回転・スケールを保持する。
//  旧 ActorTransform と同等だが ECS Component として再定義。
//
//  回転表現: YXZ オイラー角（度）— エディタ UI との互換性のため。
//  行列変換: to_mat4() / from_mat4() でレンダリング系と橋渡し。
// ============================================================

use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;

// ─── Transform ────────────────────────────────────────────────────────────────

#[derive(Clone, PartialEq, Serialize, Deserialize, Debug)]
pub struct Transform {
    #[serde(default)]
    pub position: [f32; 3],
    /// YXZ オイラー角（度）
    #[serde(default = "default_rotation")]
    pub rotation: [f32; 3],
    #[serde(default = "default_scale")]
    pub scale:    [f32; 3],
}

fn default_rotation() -> [f32; 3] { [0.0; 3] }
fn default_scale()    -> [f32; 3] { [1.0, 1.0, 1.0] }

impl Default for Transform {
    fn default() -> Self {
        Self { position: [0.0; 3], rotation: [0.0; 3], scale: [1.0, 1.0, 1.0] }
    }
}

impl Transform {
    pub fn identity() -> Self { Self::default() }

    /// TRS 行列（行優先・GPU 慣習）を生成する。
    /// YXZ: Ry * Rx * Rz の順で合成する。
    pub fn to_mat4(&self) -> [[f32; 4]; 4] {
        let [ex, ey, ez] = self.rotation.map(f32::to_radians);
        let (cx, sx) = (ex.cos(), ex.sin());
        let (cy, sy) = (ey.cos(), ey.sin());
        let (cz, sz) = (ez.cos(), ez.sin());

        let r00 = cy * cz + sy * sx * sz;
        let r01 = -cy * sz + sy * sx * cz;
        let r02 = sy * cx;
        let r10 = cx * sz;
        let r11 = cx * cz;
        let r12 = -sx;
        let r20 = -sy * cz + cy * sx * sz;
        let r21 = sy * sz + cy * sx * cz;
        let r22 = cy * cx;

        let [svx, svy, svz] = self.scale;
        let [tx, ty, tz]    = self.position;

        [
            [r00 * svx, r01 * svy, r02 * svz, tx],
            [r10 * svx, r11 * svy, r12 * svz, ty],
            [r20 * svx, r21 * svy, r22 * svz, tz],
            [0.0,       0.0,       0.0,        1.0],
        ]
    }

    /// 行列から位置・YXZ オイラー角（度）・スケールを取り出す（近似）。
    pub fn from_mat4(m: &[[f32; 4]; 4]) -> Self {
        let tx = m[0][3]; let ty = m[1][3]; let tz = m[2][3];

        let sx = (m[0][0]*m[0][0] + m[1][0]*m[1][0] + m[2][0]*m[2][0]).sqrt();
        let sy = (m[0][1]*m[0][1] + m[1][1]*m[1][1] + m[2][1]*m[2][1]).sqrt();
        let sz = (m[0][2]*m[0][2] + m[1][2]*m[1][2] + m[2][2]*m[2][2]).sqrt();

        let r02 = m[0][2] / sz;
        let r12 = m[1][2] / sz;
        let r22 = m[2][2] / sz;
        let r00 = m[0][0] / sx;
        let r01 = m[0][1] / sy;
        let r10 = m[1][0] / sx;
        let r11 = m[1][1] / sy;

        let ey = r02.asin();
        let (ex, ez) = if ey.cos().abs() > 1e-4 {
            ((-r12).atan2(r22), (-r01).atan2(r00))
        } else {
            (r10.atan2(r11), 0.0)
        };

        const DEG: f32 = 180.0 / std::f32::consts::PI;
        Self {
            position: [tx, ty, tz],
            rotation: [ex * DEG, ey * DEG, ez * DEG],
            scale:    [sx, sy, sz],
        }
    }
}

// ECS コンポーネントとして登録
impl Component for Transform {}
