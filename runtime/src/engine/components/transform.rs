// ============================================================
//  transform.rs — Transform コンポーネント
//
//  Actor のワールド空間における位置・回転・スケールを保持する。
//  旧 ActorTransform と同等だが ECS Component として再定義。
//
//  回転表現: YXZ オイラー角（度）— エディタ UI との互換性のため。
//  行列変換: to_mat4() / from_mat4() でレンダリング系と橋渡し。
// ============================================================

use crate::engine::ecs::Component;
use serde::{Deserialize, Serialize};

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
    pub scale: [f32; 3],
}

fn default_rotation() -> [f32; 3] {
    [0.0; 3]
}
fn default_scale() -> [f32; 3] {
    [1.0, 1.0, 1.0]
}

impl Default for Transform {
    fn default() -> Self {
        Self {
            position: [0.0; 3],
            rotation: [0.0; 3],
            scale: [1.0, 1.0, 1.0],
        }
    }
}

impl Transform {
    pub fn identity() -> Self {
        Self::default()
    }

    /// 回転行列の 3 本の基底ベクトル（列）を返す。スケール・平行移動は含まない。
    ///
    /// 戻り値は `[右(+X), 上(+Y), 前(+Z)]` の順で、いずれも単位長（純回転行列の列のため）。
    /// YXZ オイラー角（度）から `R = Ry * Rx * Rz` を合成した結果の各列に対応する。
    ///
    /// **SEED のローカル前方向は +Z**（左手系）である。回転 0 のとき
    /// 右 = (1,0,0) / 上 = (0,1,0) / 前 = (0,0,1) になる。
    ///
    /// `to_mat4()` / `forward()` / `up()` / `right()` はすべてこの 1 関数を経由するため、
    /// 回転規約はここだけが正典であり、多重管理にならない。
    pub fn rotation_basis(&self) -> [[f32; 3]; 3] {
        let [ex, ey, ez] = self.rotation.map(f32::to_radians);
        let (cx, sx) = (ex.cos(), ex.sin());
        let (cy, sy) = (ey.cos(), ey.sin());
        let (cz, sz) = (ez.cos(), ez.sin());

        // 列 0 = 右方向、列 1 = 上方向、列 2 = 前方向
        [
            [cy * cz + sy * sx * sz, cx * sz, -sy * cz + cy * sx * sz],
            [-cy * sz + sy * sx * cz, cx * cz, sy * sz + cy * sx * cz],
            [sy * cx, -sx, cy * cx],
        ]
    }

    /// TRS 行列（行優先・GPU 慣習）を生成する。
    /// YXZ: Ry * Rx * Rz の順で合成する。
    pub fn to_mat4(&self) -> [[f32; 4]; 4] {
        // 回転部分は rotation_basis()（正典）の列をそのまま使う
        let [[r00, r10, r20], [r01, r11, r21], [r02, r12, r22]] = self.rotation_basis();

        let [svx, svy, svz] = self.scale;
        let [tx, ty, tz] = self.position;

        [
            [r00 * svx, r01 * svy, r02 * svz, tx],
            [r10 * svx, r11 * svy, r12 * svz, ty],
            [r20 * svx, r21 * svy, r22 * svz, tz],
            [0.0, 0.0, 0.0, 1.0],
        ]
    }

    /// ワールド前方向ベクトルを返す（スケール無視・単位長）。
    ///
    /// YXZ オイラー角から +Z forward を計算する（回転 0 で (0,0,1)）。
    /// カメラのビュー行列構築などに使用する。
    pub fn forward(&self) -> [f32; 3] {
        self.rotation_basis()[2]
    }

    /// ワールド上方向ベクトルを返す（スケール無視・単位長。回転 0 で (0,1,0)）。
    pub fn up(&self) -> [f32; 3] {
        self.rotation_basis()[1]
    }

    /// ワールド右方向ベクトルを返す（スケール無視・単位長。回転 0 で (1,0,0)）。
    pub fn right(&self) -> [f32; 3] {
        self.rotation_basis()[0]
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
        let tx = m[0][3];
        let ty = m[1][3];
        let tz = m[2][3];

        // ── スケール抽出（各列の長さ）──────────────────────────────────────────
        // 列 0 の長さ = X スケール、列 1 の長さ = Y スケール、列 2 の長さ = Z スケール
        let scale_x = (m[0][0] * m[0][0] + m[1][0] * m[1][0] + m[2][0] * m[2][0]).sqrt();
        let scale_y = (m[0][1] * m[0][1] + m[1][1] * m[1][1] + m[2][1] * m[2][1]).sqrt();
        let scale_z = (m[0][2] * m[0][2] + m[1][2] * m[1][2] + m[2][2] * m[2][2]).sqrt();

        // ── 回転行列成分（スケール除去済み）──────────────────────────────────────
        // 行列が TRS 形式 (to_mat4 で生成) の場合、各列をそのスケールで割ると純回転成分が得られる
        let r02 = m[0][2] / scale_z; // = sin(Y) * cos(X)
        let r12 = m[1][2] / scale_z; // = -sin(X)
        let r22 = m[2][2] / scale_z; // = cos(Y) * cos(X)
        let r10 = m[1][0] / scale_x; // = cos(X) * sin(Z)
        let r11 = m[1][1] / scale_y; // = cos(X) * cos(Z)
        // ジンバルロック判定用（X ≈ ±90° 時に Y と Z の合成角を求めるために使用）
        let r00 = m[0][0] / scale_x; // = cos(Y)*cos(Z) + sin(Y)*sin(X)*sin(Z)
        let r20 = m[2][0] / scale_x; // = -sin(Y)*cos(Z) + cos(Y)*sin(X)*sin(Z)

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
            scale: [scale_x, scale_y, scale_z],
        }
    }
}

// ECS コンポーネントとして登録
impl Component for Transform {}

// ─── テスト ───────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 方向ベクトル比較の許容誤差（f32 の三角関数誤差を吸収する）
    const EPS: f32 = 1e-5;

    /// 3 要素ベクトルが期待値と一致するか（許容誤差付き）を検証する。
    fn assert_vec3_near(actual: [f32; 3], expected: [f32; 3], label: &str) {
        for i in 0..3 {
            assert!(
                (actual[i] - expected[i]).abs() < EPS,
                "{label}: 成分 {i} が不一致 actual={actual:?} expected={expected:?}"
            );
        }
    }

    /// 回転を指定した Transform を作る（位置・スケールは既定値）。
    fn with_rotation(rotation: [f32; 3]) -> Transform {
        Transform { rotation, ..Default::default() }
    }

    /// 回転 0 のとき、基底は世界軸に一致する（＝エンジンの前方向は +Z）。
    #[test]
    fn basis_at_identity_rotation_is_world_axes() {
        let t = Transform::identity();
        assert_vec3_near(t.forward(), [0.0, 0.0, 1.0], "forward(+Z)");
        assert_vec3_near(t.up(), [0.0, 1.0, 0.0], "up(+Y)");
        assert_vec3_near(t.right(), [1.0, 0.0, 0.0], "right(+X)");
    }

    /// Y 軸 +90 度回転で、前方向が回転前の右方向（+X）へ移る（左手系・時計回り）。
    #[test]
    fn yaw_90_moves_forward_to_world_right() {
        let t = with_rotation([0.0, 90.0, 0.0]);
        assert_vec3_near(t.forward(), [1.0, 0.0, 0.0], "yaw90 forward");
        assert_vec3_near(t.right(), [0.0, 0.0, -1.0], "yaw90 right");
        assert_vec3_near(t.up(), [0.0, 1.0, 0.0], "yaw90 up");
    }

    /// X 軸 +90 度回転（見下ろし）で、前方向が -Y、上方向が +Z を向く。
    #[test]
    fn pitch_90_looks_down() {
        let t = with_rotation([90.0, 0.0, 0.0]);
        assert_vec3_near(t.forward(), [0.0, -1.0, 0.0], "pitch90 forward");
        assert_vec3_near(t.up(), [0.0, 0.0, 1.0], "pitch90 up");
        assert_vec3_near(t.right(), [1.0, 0.0, 0.0], "pitch90 right");
    }

    /// Z 軸 +90 度回転（ロール）で、上方向が -X、右方向が +Y へ移り、前方向は不変。
    #[test]
    fn roll_90_rotates_up_and_right_only() {
        let t = with_rotation([0.0, 0.0, 90.0]);
        assert_vec3_near(t.forward(), [0.0, 0.0, 1.0], "roll90 forward");
        assert_vec3_near(t.up(), [-1.0, 0.0, 0.0], "roll90 up");
        assert_vec3_near(t.right(), [0.0, 1.0, 0.0], "roll90 right");
    }

    /// 任意回転でも基底は正規直交（単位長かつ互いに直交）であることを保証する。
    /// スクリプト API が「正規化済み」と約束しているため、その根拠を固定する。
    #[test]
    fn basis_is_orthonormal_for_arbitrary_rotation() {
        let t = with_rotation([37.0, -128.0, 64.0]);
        let axes = [t.right(), t.up(), t.forward()];
        let dot = |a: [f32; 3], b: [f32; 3]| a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        for (i, a) in axes.iter().enumerate() {
            assert!((dot(*a, *a) - 1.0).abs() < EPS, "軸 {i} が単位長でない: {a:?}");
            for (j, b) in axes.iter().enumerate().skip(i + 1) {
                assert!(dot(*a, *b).abs() < EPS, "軸 {i} と軸 {j} が直交していない");
            }
        }
        // 左手系の関係（右 = 上 × 前）を確認する
        let (u, f) = (t.up(), t.forward());
        let cross = [
            u[1] * f[2] - u[2] * f[1],
            u[2] * f[0] - u[0] * f[2],
            u[0] * f[1] - u[1] * f[0],
        ];
        assert_vec3_near(cross, t.right(), "up × forward == right");
    }

    /// 基底は to_mat4() の回転部分（各列）と一致する。
    /// 方向ベクトルと描画用行列で回転規約がずれていないことを固定する。
    #[test]
    fn basis_matches_to_mat4_columns() {
        // スケールを掛けても列方向が変わらないことを見るため非一様スケールを与える
        let t = Transform {
            position: [3.0, -2.0, 8.0],
            rotation: [37.0, -128.0, 64.0],
            scale: [2.0, 3.0, 4.0],
        };
        let m = t.to_mat4();
        let axes = [t.right(), t.up(), t.forward()];
        for (col, axis) in axes.iter().enumerate() {
            // 行列の列 = 基底 × その軸のスケール
            let s = t.scale[col];
            let column = [m[0][col] / s, m[1][col] / s, m[2][col] / s];
            assert_vec3_near(column, *axis, "to_mat4 列と基底の一致");
        }
    }
}
