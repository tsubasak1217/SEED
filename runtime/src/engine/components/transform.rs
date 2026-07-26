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

/// Actor のワールド空間トランスフォーム（位置・回転・スケール）を保持するコンポーネント。
///
/// 回転は YXZ オイラー角（度）で表現する。`to_mat4()` / `from_mat4()` で
/// レンダリング系（行列表現）との相互変換を行う。
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

    /// ワールド前方向ベクトルを返す（スケール無視）。
    ///
    /// YXZ オイラー角から +Z forward を計算する。
    /// カメラのビュー行列構築などに使用する。
    pub fn forward(&self) -> [f32; 3] {
        let [ex, ey, _] = self.rotation.map(f32::to_radians);
        let cx = ex.cos();
        let sx = ex.sin();
        let cy = ey.cos();
        let sy = ey.sin();
        [sy * cx, -sx, cy * cx]
    }

    /// ワールド上方向ベクトルを返す（スケール無視）。
    pub fn up(&self) -> [f32; 3] {
        let [ex, ey, ez] = self.rotation.map(f32::to_radians);
        let cx = ex.cos();
        let sx = ex.sin();
        let cy = ey.cos();
        let sy = ey.sin();
        let cz = ez.cos();
        let sz = ez.sin();
        [
            -cy * sz + sy * sx * cz,
            cx * cz,
            sy * sz + cy * sx * cz,
        ]
    }

    /// 行列から位置・YXZ オイラー角（度）・スケールを取り出す。
    ///
    /// YXZ 分解 (R = Ry * Rx * Rz) の正確な逆演算。
    ///
    /// to_mat4() が生成する行列の各要素:
    ///   r02 = sin(Y)*cos(X),  r12 = -sin(X),   r22 = cos(Y)*cos(X)
    ///   r10 = cos(X)*sin(Z),  r11 = cos(X)*cos(Z)
    ///
    /// 抽出手順:
    ///   1. r12 = -sin(X)                                → X = asin(-r12)
    ///   2. r02/r22 = sin(Y)/cos(Y)（cos(X) が約分）    → Y = atan2(r02, r22)
    ///   3. r10/r11 = sin(Z)/cos(Z)（cos(X) が約分）    → Z = atan2(r10, r11)
    ///
    /// 旧実装は r02 = sy*cx を sin(Y) と誤認していたため、
    /// X 回転が非ゼロの場合に誤ったオイラー角を生成するバグがあった。
    /// この修正により to_mat4() と from_mat4() が真の逆関数となり、
    /// apply_physics_transform が SeedQuat::to_euler() で設定した
    /// オイラー角と一致する値が得られる。
    pub fn from_mat4(m: &[[f32; 4]; 4]) -> Self {
        let tx = m[0][3]; let ty = m[1][3]; let tz = m[2][3];

        // ── スケール抽出（各列の長さ）──────────────────────────────────────────
        // 列 0 の長さ = X スケール、列 1 の長さ = Y スケール、列 2 の長さ = Z スケール
        let scale_x = (m[0][0]*m[0][0] + m[1][0]*m[1][0] + m[2][0]*m[2][0]).sqrt();
        let scale_y = (m[0][1]*m[0][1] + m[1][1]*m[1][1] + m[2][1]*m[2][1]).sqrt();
        let scale_z = (m[0][2]*m[0][2] + m[1][2]*m[1][2] + m[2][2]*m[2][2]).sqrt();

        // ── 回転行列成分（スケール除去済み）──────────────────────────────────────
        // 行列が TRS 形式 (to_mat4 で生成) の場合、各列をそのスケールで割ると純回転成分が得られる
        let r02 = m[0][2] / scale_z;  // = sin(Y) * cos(X)
        let r12 = m[1][2] / scale_z;  // = -sin(X)
        let r22 = m[2][2] / scale_z;  // = cos(Y) * cos(X)
        let r10 = m[1][0] / scale_x;  // = cos(X) * sin(Z)
        let r11 = m[1][1] / scale_y;  // = cos(X) * cos(Z)
        // ジンバルロック判定用（X ≈ ±90° 時に Y と Z の合成角を求めるために使用）
        let r00 = m[0][0] / scale_x;  // = cos(Y)*cos(Z) + sin(Y)*sin(X)*sin(Z)
        let r20 = m[2][0] / scale_x;  // = -sin(Y)*cos(Z) + cos(Y)*sin(X)*sin(Z)

        // ── YXZ オイラー角の抽出 ────────────────────────────────────────────────
        // r12 = -sin(X) → X を先に確定する
        let ex = (-r12).asin();
        let (ey, ez) = if ex.cos().abs() > 1e-4 {
            // 通常ケース:
            //   r02 / r22 = sin(Y)*cos(X) / cos(Y)*cos(X) = tan(Y) → Y = atan2(r02, r22)
            //   r10 / r11 = cos(X)*sin(Z) / cos(X)*cos(Z) = tan(Z) → Z = atan2(r10, r11)
            (r02.atan2(r22), r10.atan2(r11))
        } else {
            // ジンバルロック（X ≈ ±90°）: Y と Z は独立に決定できないので Y-Z（または Y+Z）のみ確定する
            //   X = +90° 時: r00 = cos(Y-Z), r20 = -sin(Y-Z) → atan2(-r20, r00) = Y - Z
            //   X = -90° 時: r00 = cos(Y+Z), r20 = -sin(Y+Z) → atan2(-r20, r00) = Y + Z
            //   いずれも Z = 0 として Y に吸収する
            ((-r20).atan2(r00), 0.0)
        };

        const DEG: f32 = 180.0 / std::f32::consts::PI;
        Self {
            position: [tx, ty, tz],
            rotation: [ex * DEG, ey * DEG, ez * DEG],
            scale:    [scale_x, scale_y, scale_z],
        }
    }
}

// ECS コンポーネントとして登録
impl Component for Transform {}
