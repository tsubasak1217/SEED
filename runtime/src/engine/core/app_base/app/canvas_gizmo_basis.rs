// ============================================================
//  canvas_gizmo_basis.rs — 2D キャンバスギズモの軸基底（World / Local）
//
//  【役割】
//  ツールバーの World/Local トグル（App::gizmo_space）を 2D キャンバス編集の
//  ギズモへ反映するための「軸基底」を 1 か所で生成する。
//
//  - World : キャンバス軸そのもの（+X = 右 / +Y = 下、キャンバス px 空間）
//  - Local : 選択アクターの累積回転（自身の CanvasTransform.rotation ＋
//            親チェーンの回転）に沿った軸
//
//  描画（frame_renderer の 2D ギズモ）とヒットテスト（gizmo_handler）は
//  必ずこのモジュールが返す同一の基底を使う。別々に組むと
//  「見た目と当たり判定がズレる」不具合が構造的に発生するため。
//
//  【座標系の前提】
//  2D 編集時のギズモ空間は `screen_to_ray_ortho` が作るキャンバス px 空間で、
//  +X = 画面右 / +Y = 画面下 / +Z = 画面奥。
//  `CanvasTransform::to_mat4_sized` の回転は col0 = [cos, sin] なので、
//  正の rotation は +X → +Y（画面上では時計回り）へ回る。
// ============================================================

use crate::engine::components::CanvasTransform;
use crate::engine::core::app_base::ipc::GizmoSpace;
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::canvas_collect::canvas_node_is_transparent;
use super::App;

/// スケール係数として意味を持つ下限（これ以下は 0 除算回避のため 1.0 とみなす）。
const SCALE_EPS: f32 = f32::EPSILON;

// ============================================================
//  純粋関数（単体テスト対象）
// ============================================================

/// 累積ワールド回転（ラジアン）から 2D ギズモの軸基底 [ax, ay, az] を作る。
///
/// - `ax` = アクターのローカル +X がキャンバス空間で向く方向
/// - `ay` = 同じくローカル +Y（キャンバスでは「下」）方向
/// - `az` = キャンバス法線（回転しても常に +Z。2D の回転ギズモ軸）
///
/// `rot_rad = 0` のとき World モードの基底（恒等）と完全に一致する。
pub(crate) fn canvas_gizmo_axes_from_rot(rot_rad: f32) -> [[f32; 3]; 3] {
    let (sin, cos) = rot_rad.sin_cos();
    [
        [cos, sin, 0.0],  // ローカル X（World: [1, 0, 0]）
        [-sin, cos, 0.0], // ローカル Y（World: [0, 1, 0]）
        [0.0, 0.0, 1.0],  // キャンバス法線（回転の影響を受けない）
    ]
}

/// World モード（軸整列）の 2D ギズモ基底。
pub(crate) fn canvas_gizmo_axes_world() -> [[f32; 3]; 3] {
    canvas_gizmo_axes_from_rot(0.0)
}

/// 変換行列の線形部から「指定軸方向のスケール係数」を取り出す。
///
/// 向き付きギズモ（Local モード）のスケール行列は
/// `S = fx·(ax⊗ax) + fy·(ay⊗ay) + fz·(az⊗az)` の形で組まれるため、
/// `|S·ax| = fx` が成り立つ。World モード（軸整列 = 対角行列）では
/// 従来の「列ベクトル長」と同値になるので、両モードを 1 本の式で扱える。
pub(crate) fn axis_scale_factor(mat: &[[f32; 4]; 4], axis: [f32; 3]) -> f32 {
    let mut v = [0.0f32; 3];
    for (r, item) in v.iter_mut().enumerate() {
        *item = mat[r][0] * axis[0] + mat[r][1] * axis[1] + mat[r][2] * axis[2];
    }
    (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt()
}

/// ギズモが動かした「キャンバス空間ワールド座標」を
/// `CanvasTransform.position`（＝親ローカル座標）へ逆変換する。
///
/// 変換の順序は描画チェーン（collect_sprite_items / collect_actor2d_contexts）の逆で、
///   1. 親キャンバス原点を引く
///   2. 親の累積回転 `parent_world_rot` を逆回転する
///   3. アンカーオフセットを引く
///   4. scale_mode = transform のときのみ親累積スケールで割る
/// の順に行う。Local モードでは 1 の時点のデルタが「回転済みローカル軸方向」に
/// 乗っているため、2 で親回転を外すだけで正しい親ローカル移動量になる
/// （アクター自身の回転は position には掛からない ＝ 親ローカル空間の値だから）。
pub(crate) fn canvas_world_to_parent_local_pos(
    world_pos: [f32; 2],
    parent_canvas_origin: [f32; 2],
    parent_world_rot: f32,
    anchor_off: [f32; 2],
    cumul_scale: [f32; 2],
    sm_transform: bool,
) -> [f32; 2] {
    // world → 親キャンバスローカル（親累積回転の逆適用）
    let dx = world_pos[0] - parent_canvas_origin[0];
    let dy = world_pos[1] - parent_canvas_origin[1];
    let (sin_p, cos_p) = parent_world_rot.sin_cos();
    let local_x = cos_p * dx + sin_p * dy;
    let local_y = -sin_p * dx + cos_p * dy;
    // アンカーオフセットを除去する
    let px = local_x - anchor_off[0];
    let py = local_y - anchor_off[1];
    if sm_transform {
        // scale_mode = transform: position に親累積スケールが掛かっているため割り戻す
        let sx = if cumul_scale[0].abs() > SCALE_EPS {
            cumul_scale[0]
        } else {
            1.0
        };
        let sy = if cumul_scale[1].abs() > SCALE_EPS {
            cumul_scale[1]
        } else {
            1.0
        };
        [px / sx, py / sy]
    } else {
        [px, py]
    }
}

// ============================================================
//  アクターツリー走査（フォールバック経路）
// ============================================================

/// 指定 DFS ID の 2D アクターの累積ワールド回転（ラジアン）を返す。
///
/// `canvas_anchor_offset_for_dfs` と同一の走査規則（フォルダはレイアウト透明・
/// DFS 番号は全ノードで消費）で、自身を含む祖先の `CanvasTransform.rotation` を
/// 合算する。`App::actor_2d_layout_ctx` が None（アクター編集タブ・
/// ワールドスペース表示）のときのフォールバックとして使用する。
pub(crate) fn canvas_world_rot_for_dfs(
    actors: &[Actor],
    world: &World,
    wl: u32,
    target_dfs: u32,
) -> f32 {
    let mut counter = 0u32;
    for actor in actors.iter() {
        if actor.world_line != wl {
            continue;
        }
        if let Some(rot) = accumulate_canvas_rot(actor, world, target_dfs, &mut counter, 0.0) {
            return rot;
        }
    }
    0.0
}

/// `canvas_world_rot_for_dfs` の再帰実装（自身に番号を振ってから子へ降りる）。
///
/// 戻り値 Some(累積回転ラジアン) = ターゲット発見、None = このサブツリーに無し。
fn accumulate_canvas_rot(
    actor: &Actor,
    world: &World,
    target_dfs: u32,
    counter: &mut u32,
    parent_rot: f32,
) -> Option<f32> {
    // フォルダはレイアウト透明: 自身の CanvasTransform を読まず、親の回転を素通しする。
    // DFS 番号だけは 1 ノード分消費する（find_actor_by_dfs と同じ規則）。
    let my_rot = if canvas_node_is_transparent(actor) {
        parent_rot
    } else {
        parent_rot
            + world
                .get::<CanvasTransform>(actor.entity)
                .map(|ct| ct.rotation.to_radians())
                .unwrap_or(0.0)
    };
    if *counter == target_dfs {
        return Some(my_rot);
    }
    *counter += 1;
    for child in actor.children().iter() {
        if let Some(rot) = accumulate_canvas_rot(child, world, target_dfs, counter, my_rot) {
            return Some(rot);
        }
    }
    None
}

// ============================================================
//  App 側インターフェース
// ============================================================

impl App {
    /// 2D キャンバスギズモの軸基底 [ax, ay, az] を返す（描画・ヒットテスト共通）。
    ///
    /// - `GizmoSpace::World` → None（呼び出し側は従来の軸整列 2D ギズモを使う）
    /// - `GizmoSpace::Local` → Some(選択プライマリアクターの累積回転に沿った基底)
    ///
    /// マルチ選択時は 3D ギズモと同じ優先順位（`actor_virtual_selected_idx` →
    /// `selected_actor_dfs_ids` の先頭）でプライマリ選択アクターの基底を採用する。
    pub(super) fn canvas_gizmo_axes_2d(&self) -> Option<[[f32; 3]; 3]> {
        if self.gizmo_space != GizmoSpace::Local {
            return None;
        }
        // 2D アクター以外（3D アクター選択中）は対象外
        if !self.selected_primary_actor_is_2d() {
            return None;
        }
        let rot = self.selected_canvas_world_rot_rad()?;
        Some(canvas_gizmo_axes_from_rot(rot))
    }

    /// 選択中プライマリ 2D アクターの累積ワールド回転（ラジアン）を返す。
    ///
    /// 描画と同一チェーンのレイアウトコンテキスト（`actor_2d_layout_ctx`）が
    /// 得られる場合はその `rot_rad` を使う（自動解像度・ルート恒等化を反映済み）。
    /// アクター編集タブなど取得できない経路ではアクターツリーを直接走査して合算する。
    pub(super) fn selected_canvas_world_rot_rad(&self) -> Option<f32> {
        let primary = self
            .actor_virtual_selected_idx
            .or_else(|| self.selected_actor_dfs_ids.first().copied())?;
        let dfs = primary as u32;
        if let Some(ctx) = self.actor_2d_layout_ctx(dfs) {
            return Some(ctx.rot_rad);
        }
        let scene = self.scene.as_ref()?;
        Some(canvas_world_rot_for_dfs(
            &scene.actors,
            &scene.world,
            self.active_world_line,
            dfs,
        ))
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 浮動小数の近似比較（軸ベクトル用）。
    fn assert_axis_near(actual: [f32; 3], expect: [f32; 3]) {
        for i in 0..3 {
            assert!(
                (actual[i] - expect[i]).abs() < 1e-5,
                "axis[{i}] actual={actual:?} expect={expect:?}"
            );
        }
    }

    /// 回転 0（World 相当）では基底が恒等になる。
    #[test]
    fn axes_rot0_is_identity() {
        let [ax, ay, az] = canvas_gizmo_axes_from_rot(0.0);
        assert_axis_near(ax, [1.0, 0.0, 0.0]);
        assert_axis_near(ay, [0.0, 1.0, 0.0]);
        assert_axis_near(az, [0.0, 0.0, 1.0]);
        // World ヘルパーとも一致すること
        let w = canvas_gizmo_axes_world();
        assert_axis_near(w[0], ax);
        assert_axis_near(w[1], ay);
    }

    /// 回転 90°: ローカル X はキャンバスの +Y（画面下）を向く。
    #[test]
    fn axes_rot90_x_points_down() {
        let [ax, ay, az] = canvas_gizmo_axes_from_rot(90f32.to_radians());
        assert_axis_near(ax, [0.0, 1.0, 0.0]);
        assert_axis_near(ay, [-1.0, 0.0, 0.0]);
        // 法線はモードに依らず +Z（2D 回転ギズモ軸）
        assert_axis_near(az, [0.0, 0.0, 1.0]);
    }

    /// 親 45° + 子 45° の累積 = 90°（合算後の基底が 90° 版と一致する）。
    #[test]
    fn axes_parent45_child45_equals_90() {
        let accum = 45f32.to_radians() + 45f32.to_radians();
        let [ax, ay, _] = canvas_gizmo_axes_from_rot(accum);
        let [ax90, ay90, _] = canvas_gizmo_axes_from_rot(90f32.to_radians());
        assert_axis_near(ax, ax90);
        assert_axis_near(ay, ay90);
    }

    /// 基底は正規直交（ax·ay = 0、長さ 1）。
    #[test]
    fn axes_are_orthonormal() {
        let [ax, ay, az] = canvas_gizmo_axes_from_rot(37f32.to_radians());
        let dot = ax[0] * ay[0] + ax[1] * ay[1] + ax[2] * ay[2];
        assert!(dot.abs() < 1e-6);
        for a in [ax, ay, az] {
            let len = (a[0] * a[0] + a[1] * a[1] + a[2] * a[2]).sqrt();
            assert!((len - 1.0).abs() < 1e-6);
        }
    }

    /// World モード（親回転なし・スケールなし）の移動デルタはそのまま position デルタ。
    #[test]
    fn delta_world_no_parent_transform() {
        let start = canvas_world_to_parent_local_pos(
            [100.0, 50.0],
            [0.0, 0.0],
            0.0,
            [0.0, 0.0],
            [1.0, 1.0],
            false,
        );
        let moved = canvas_world_to_parent_local_pos(
            [110.0, 50.0],
            [0.0, 0.0],
            0.0,
            [0.0, 0.0],
            [1.0, 1.0],
            false,
        );
        assert!((moved[0] - start[0] - 10.0).abs() < 1e-4);
        assert!((moved[1] - start[1]).abs() < 1e-4);
    }

    /// Local モード: 回転 90° のローカル X（＝キャンバス +Y）方向へ 10px 動かすと、
    /// 親が無回転なら position は Y に +10 される（＝スクリーンデルタがそのまま親空間）。
    #[test]
    fn delta_local_axis_moves_along_rotated_axis() {
        let [ax, _, _] = canvas_gizmo_axes_from_rot(90f32.to_radians());
        let dist = 10.0f32;
        let world_start = [100.0f32, 50.0];
        let world_end = [
            world_start[0] + ax[0] * dist,
            world_start[1] + ax[1] * dist,
        ];
        let p0 = canvas_world_to_parent_local_pos(
            world_start,
            [0.0, 0.0],
            0.0,
            [0.0, 0.0],
            [1.0, 1.0],
            false,
        );
        let p1 = canvas_world_to_parent_local_pos(
            world_end,
            [0.0, 0.0],
            0.0,
            [0.0, 0.0],
            [1.0, 1.0],
            false,
        );
        assert!((p1[0] - p0[0]).abs() < 1e-4, "X は動かない");
        assert!((p1[1] - p0[1] - dist).abs() < 1e-4, "Y に +10");
    }

    /// 親が 90° 回転している場合、キャンバス空間のデルタは親ローカルでは回転して入る。
    #[test]
    fn delta_parent_rotation_is_inverted() {
        let parent_rot = 90f32.to_radians();
        let p0 = canvas_world_to_parent_local_pos(
            [0.0, 0.0],
            [0.0, 0.0],
            parent_rot,
            [0.0, 0.0],
            [1.0, 1.0],
            false,
        );
        // キャンバス空間で +Y に 10 動かす → 親ローカルでは -X 方向へ 10
        let p1 = canvas_world_to_parent_local_pos(
            [0.0, 10.0],
            [0.0, 0.0],
            parent_rot,
            [0.0, 0.0],
            [1.0, 1.0],
            false,
        );
        assert!((p1[0] - p0[0] - 10.0).abs() < 1e-4, "親ローカル X に +10");
        assert!((p1[1] - p0[1]).abs() < 1e-4);
    }

    /// 親累積スケール 2（scale_mode = transform）では position のデルタは半分になる。
    #[test]
    fn delta_parent_scale_halves_position() {
        let p0 = canvas_world_to_parent_local_pos(
            [100.0, 0.0],
            [0.0, 0.0],
            0.0,
            [0.0, 0.0],
            [2.0, 2.0],
            true,
        );
        let p1 = canvas_world_to_parent_local_pos(
            [120.0, 0.0],
            [0.0, 0.0],
            0.0,
            [0.0, 0.0],
            [2.0, 2.0],
            true,
        );
        assert!(
            (p1[0] - p0[0] - 10.0).abs() < 1e-4,
            "ワールド 20px の移動 → position は +10"
        );
    }

    /// 軸スケール係数: World（対角行列）では列長と一致する。
    #[test]
    fn axis_scale_factor_world_matches_column_length() {
        let mut m = [[0.0f32; 4]; 4];
        m[0][0] = 2.0;
        m[1][1] = 3.0;
        m[2][2] = 1.0;
        m[3][3] = 1.0;
        assert!((axis_scale_factor(&m, [1.0, 0.0, 0.0]) - 2.0).abs() < 1e-5);
        assert!((axis_scale_factor(&m, [0.0, 1.0, 0.0]) - 3.0).abs() < 1e-5);
    }

    /// 軸スケール係数: Local（回転した基底で組んだスケール行列）でも係数を取り出せる。
    #[test]
    fn axis_scale_factor_local_extracts_factor() {
        let [ax, ay, az] = canvas_gizmo_axes_from_rot(45f32.to_radians());
        let (fx, fy, fz) = (2.0f32, 1.0f32, 1.0f32);
        // S = fx*(ax⊗ax) + fy*(ay⊗ay) + fz*(az⊗az)（update_drag の apply_oriented と同型）
        let mut m = [[0.0f32; 4]; 4];
        for r in 0..3 {
            for c in 0..3 {
                m[r][c] = fx * ax[r] * ax[c] + fy * ay[r] * ay[c] + fz * az[r] * az[c];
            }
        }
        m[3][3] = 1.0;
        assert!((axis_scale_factor(&m, ax) - fx).abs() < 1e-5);
        assert!((axis_scale_factor(&m, ay) - fy).abs() < 1e-5);
        // 列長で取ると誤った値になる（Local で列長を使ってはいけない根拠）
        let col0_len = (m[0][0] * m[0][0] + m[1][0] * m[1][0]).sqrt();
        assert!((col0_len - fx).abs() > 0.1);
    }
}
