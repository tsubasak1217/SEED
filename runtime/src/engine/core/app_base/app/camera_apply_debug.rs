// ============================================================
//  camera_apply_debug.rs — 「デバッグカメラの値を反映」操作
//
//  エディタのメインビューポートを映しているデバッグカメラ（App::camera）の
//  視点・投影パラメータを、選択中の CameraComponent とその所有アクタへ焼き付ける。
//
//  【含む処理】
//  - 純関数（規約変換）: debug_camera_euler_deg / fov_y_rad_to_component_deg /
//                        ortho_half_h_to_component_height
//  - handle_camera_apply_debug: IPC CAMERA_APPLY_DEBUG の本体
//
//  【規約の対応表】
//  | 項目       | DebugCamera                      | CameraComponent / Transform          |
//  |------------|----------------------------------|--------------------------------------|
//  | 位置       | base.transform.position (world)   | Transform.position（**ワールド空間**）|
//  | 向き       | yaw(Y) / pitch(X)、Roll なし      | Transform.rotation = YXZ オイラー(度) |
//  | 前方向     | +Z（左手系）                      | +Z（rotation_basis()[2]）             |
//  | FOV        | base.projection.fov_y_rad（垂直） | fov_y_deg（垂直）                     |
//  | near/far   | base.projection.near / far        | near / far                            |
//  | 投影       | ortho_target(0/1) + ortho_half_h  | projection + ortho_height（**全高**） |
//
//  向きの規約は両者とも「R = Ry(yaw) * Rx(pitch)」で一致しているため、
//  オイラー角は [pitch, yaw, 0]（度）をそのまま書けばよい（ロールは常に 0）。
//
//  【ワールド／ローカルについて】
//  SEED の `components::Transform` は**ワールド空間**で保持され、ローカル座標系も
//  親子行列合成も存在しない（core/transform_sync.rs のヘッダが正典）。
//  したがって親の逆行列を掛ける「ローカル化」は不要かつ有害であり、
//  デバッグカメラのワールド値をそのまま書いて `set_actor_world_transform` に
//  子孫への伝播を任せるのが正しい。
//
//  【Undo】
//  1 クリック = Undo 1 件。アクタ Transform（＋配下 Model インスタンス・子孫）の
//  ActorGroupTransformCommand と、カメラスロット値の SlotFieldEditCommand を
//  CompositeCommand で束ねて履歴へ 1 件だけ積む。
// ============================================================

use crate::engine::components::{
    CameraComponent, CameraProjection, ComponentKind, Transform as ActorTransform,
};
use crate::engine::core::app_base::undo::{
    ActorGroupTransformCommand, CompositeCommand, SlotFieldEditCommand,
};
use crate::engine::core::transform_sync::set_actor_world_transform;
use crate::engine::structs::objects::actor::slot_to_data;

use super::{App, RuntimeMode, find_actor_by_dfs};

// ─── 規約変換の純関数 ─────────────────────────────────────────

/// CameraComponent の FOV に許される下限（度）。`handle_set_camera_fov` と同じ値。
const CAMERA_FOV_DEG_MIN: f32 = 1.0;
/// CameraComponent の FOV に許される上限（度）。`handle_set_camera_fov` と同じ値。
const CAMERA_FOV_DEG_MAX: f32 = 179.0;
/// CameraComponent の near clip の下限。`handle_set_camera_near` と同じ値。
const CAMERA_NEAR_MIN: f32 = 0.0001;
/// far は near からこの分だけ離す（`handle_set_camera_far` と同じ規約）。
const CAMERA_FAR_MARGIN: f32 = 0.1;
/// CameraComponent の正射高さ（全高）の下限。`handle_set_camera_ortho_height` と同じ値。
const CAMERA_ORTHO_HEIGHT_MIN: f32 = 0.01;

/// デバッグカメラの yaw / pitch（ラジアン）を
/// アクタ Transform の YXZ オイラー角（度）へ変換する。
///
/// 両者の回転合成順は `R = Ry(yaw) * Rx(pitch)` で一致しており、
/// デバッグカメラはロール（Z 回転）を持たないので Z は常に 0 になる。
#[inline]
pub fn debug_camera_euler_deg(yaw_rad: f32, pitch_rad: f32) -> [f32; 3] {
    [pitch_rad.to_degrees(), yaw_rad.to_degrees(), 0.0]
}

/// デバッグカメラの垂直 FOV（ラジアン）を CameraComponent の `fov_y_deg` へ変換する。
///
/// **どちらも垂直画角**なので軸の読み替えは不要で、単位（rad→deg）とクランプだけを行う。
#[inline]
pub fn fov_y_rad_to_component_deg(fov_y_rad: f32) -> f32 {
    fov_y_rad
        .to_degrees()
        .clamp(CAMERA_FOV_DEG_MIN, CAMERA_FOV_DEG_MAX)
}

/// デバッグカメラの正射描画範囲（`ortho_half_h` = **半分**の高さ）を
/// CameraComponent の `ortho_height`（**全高**）へ変換する。
#[inline]
pub fn ortho_half_h_to_component_height(ortho_half_h: f32) -> f32 {
    (ortho_half_h * 2.0).max(CAMERA_ORTHO_HEIGHT_MIN)
}

// ─── ハンドラ ─────────────────────────────────────────────────

impl App {
    /// デバッグカメラの視点・投影パラメータを CameraComponent へ焼き付ける
    /// （IPC `CAMERA_APPLY_DEBUG:{actor_dfs_id},{slot_idx}`）。
    ///
    /// Play 中は「実行時の一時状態」であってシーンの編集ではないため何もしない
    /// （エディタ側でもボタンを無効化しているが、こちらが最終的な防御線）。
    pub(super) fn handle_camera_apply_debug(&mut self, actor_dfs_id: u32, slot_idx: u32) {
        // ── Edit モード限定 ────────────────────────────────────────────
        if self.mode != RuntimeMode::Edit {
            return;
        }

        let wl = self.active_world_line;

        // ── 反映するデバッグカメラの値を先に取り出す ───────────────────
        // （self.scene を可変借用する前に、カメラ側の読み取りを終わらせる）
        let cam_pos = self.camera.base.transform.position;
        let new_rotation = debug_camera_euler_deg(self.camera.yaw, self.camera.pitch);
        let new_fov_deg = fov_y_rad_to_component_deg(self.camera.base.projection.fov_y_rad);
        let new_near = self.camera.base.projection.near.max(CAMERA_NEAR_MIN);
        let new_far = self
            .camera
            .base
            .projection
            .far
            .max(new_near + CAMERA_FAR_MARGIN);
        let new_projection = if self.camera.is_ortho() {
            CameraProjection::Orthographic
        } else {
            CameraProjection::Perspective
        };
        let new_ortho_height = ortho_half_h_to_component_height(self.camera.ortho_half_h);

        // ── 対象スロットの実体（entity）と編集前スナップショットを取得する ──
        let Some(scene) = &self.scene else { return };
        let mut c = 0u32;
        let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) else {
            return;
        };
        let Some(camera_slot) = actor.slots().get(slot_idx as usize) else {
            return;
        };
        if camera_slot.kind != ComponentKind::Camera {
            return;
        }
        let camera_entity = camera_slot.entity;
        let Some(slot_before) = slot_to_data(&scene.world, camera_slot) else {
            return;
        };

        // 位置・回転はデバッグカメラのワールド値をそのまま採用し、
        // スケールは現在値を保つ（カメラの見え方に無関係なので勝手に潰さない）。
        let old_tf = scene
            .world
            .get::<ActorTransform>(actor.entity)
            .cloned()
            .unwrap_or_default();
        let new_tf = ActorTransform {
            position: [cam_pos.x, cam_pos.y, cam_pos.z],
            rotation: new_rotation,
            scale: old_tf.scale,
        };

        // ── 1. アクタ Transform を適用する（子孫・Model インスタンスへ伝播）──
        let sync = {
            let Some(scene) = &mut self.scene else { return };
            let (actors, world) = (&scene.actors, &mut scene.world);
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(actors, wl, actor_dfs_id, &mut c) else {
                return;
            };
            set_actor_world_transform(actor, world, new_tf, actor_dfs_id + 1)
        };

        // ── 2. CameraComponent の投影パラメータを適用する ─────────────
        {
            let Some(scene) = &mut self.scene else { return };
            let Some(cc) = scene.world.get_mut::<CameraComponent>(camera_entity) else {
                return;
            };
            cc.fov_y_deg = new_fov_deg;
            cc.near = new_near;
            cc.far = new_far;
            cc.projection = new_projection;
            cc.ortho_height = new_ortho_height;
            // アスペクト比表記（target_width / target_height）は据え置く。
            // ウィンドウ解像度に依存する別概念であり、
            // 「ウィンドウアスペクト比を適用」ボタンの責務だからである。
        }

        // ── 3. 編集後スナップショットを取り、1 コマンドへ束ねて Undo へ積む ──
        let slot_after = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .and_then(|s| slot_to_data(&scene.world, s))
        };

        let transform_changed = sync.old_tf != sync.new_tf
            || !sync.self_instance_changes.is_empty()
            || !sync.child_changes.is_empty();
        let slot_changed = slot_after
            .as_ref()
            .map(|a| {
                serde_json::to_string(a).ok() != serde_json::to_string(&slot_before).ok()
            })
            .unwrap_or(false);

        if transform_changed || slot_changed {
            let mut commands: Vec<Box<dyn crate::engine::core::app_base::undo::Command>> =
                Vec::new();
            if transform_changed {
                commands.push(Box::new(ActorGroupTransformCommand {
                    wl,
                    dfs_id: actor_dfs_id,
                    old_tf: sync.old_tf.clone(),
                    new_tf: sync.new_tf.clone(),
                    transforms: sync.self_instance_changes,
                    child_transforms: sync.child_changes,
                    extra_slot_transforms: vec![],
                }));
            }
            if slot_changed {
                if let Some(after) = slot_after {
                    commands.push(Box::new(SlotFieldEditCommand {
                        world_line: wl,
                        actor_dfs_id,
                        slot_idx,
                        before: slot_before,
                        after,
                    }));
                }
            }
            // 直前のフィールド編集セッションへ吸収されないよう、マージ状態を切る
            // （そうしないと「スライダー → このボタン」が 1 手にまとまってしまう）。
            self.reset_field_edit_session();
            self.undo_history
                .record(Box::new(CompositeCommand { commands }));
        }

        // ── 4. インスペクタへ再送してシーンをダーティ化する ────────────
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }
}

// ─── テスト ───────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::structs::tensor::Vector3;
    use crate::engine::structs::transforms::Quaternion;

    /// 方向ベクトル比較の許容誤差（f32 の三角関数誤差を吸収する）。
    const EPS: f32 = 1e-5;

    /// DebugCamera が yaw / pitch から作る回転（Ry * Rx）の前方向を返す。
    /// `DebugCamera::update_rotation` と同じ式（＝比較の基準）。
    fn debug_camera_forward(yaw: f32, pitch: f32) -> [f32; 3] {
        let yaw_q = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), yaw);
        let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), pitch);
        let f = (yaw_q * pitch_q).forward();
        [f.x, f.y, f.z]
    }

    fn assert_vec3_near(actual: [f32; 3], expected: [f32; 3], label: &str) {
        for i in 0..3 {
            assert!(
                (actual[i] - expected[i]).abs() < EPS,
                "{label}: 成分 {i} が不一致 actual={actual:?} expected={expected:?}"
            );
        }
    }

    /// デバッグカメラの向きが、変換後の Transform の前方向・上方向と一致する。
    /// これが「反映後に同じ画が見える」ことの根拠になる。
    #[test]
    fn euler_conversion_preserves_camera_orientation() {
        // 代表的な yaw / pitch の組み合わせ（正負・0・大きい yaw を含む）
        let cases = [
            (0.0_f32, 0.0_f32),
            (0.7_f32, 0.3_f32),
            (-2.5_f32, -0.9_f32),
            (3.0_f32, 1.2_f32),
        ];
        for (yaw, pitch) in cases {
            let tf = ActorTransform {
                rotation: debug_camera_euler_deg(yaw, pitch),
                ..Default::default()
            };
            let expected = debug_camera_forward(yaw, pitch);
            assert_vec3_near(tf.forward(), expected, &format!("forward yaw={yaw} pitch={pitch}"));
            // ロール 0 を保証する（カメラの上方向が水平線と平行）
            assert_eq!(tf.rotation[2], 0.0, "ロールは常に 0 であること");
        }
    }

    /// 変換した Transform から `sync_debug_camera_to_main_camera` と同じ式で
    /// yaw / pitch を逆算すると元の値へ戻る（往復で情報が落ちない）。
    #[test]
    fn euler_conversion_round_trips_yaw_pitch() {
        // pitch は DebugCamera のクランプ範囲（±(π/2 − 0.02)）内で選ぶ。
        // yaw も atan2 の主値範囲（±π）内で選ぶ（範囲外は同じ向きの別表現になる）。
        let cases = [(0.0_f32, 0.0_f32), (0.7_f32, 0.3_f32), (-2.5_f32, -0.9_f32)];
        for (yaw, pitch) in cases {
            let tf = ActorTransform {
                rotation: debug_camera_euler_deg(yaw, pitch),
                ..Default::default()
            };
            let [fx, fy, fz] = tf.forward();
            let back_pitch = (-fy).clamp(-1.0, 1.0).asin();
            let back_yaw = fx.atan2(fz);
            assert!((back_pitch - pitch).abs() < 1e-4, "pitch 往復 {back_pitch} != {pitch}");
            assert!((back_yaw - yaw).abs() < 1e-4, "yaw 往復 {back_yaw} != {yaw}");
        }
    }

    /// 位置は「ワールドのまま書く」— SEED の Transform はワールド空間なので、
    /// 親の有無に関わらずデバッグカメラのワールド位置がそのまま入る。
    /// （親のローカル化を入れるとカメラが親のオフセットぶんズレる。）
    #[test]
    fn position_is_written_in_world_space() {
        let cam_pos = [3.0_f32, -2.0, 8.5];
        let tf = ActorTransform {
            position: cam_pos,
            rotation: debug_camera_euler_deg(0.4, -0.2),
            scale: [2.0, 2.0, 2.0],
        };
        // to_mat4 の平行移動成分がそのままカメラ位置であること
        let m = tf.to_mat4();
        assert_vec3_near([m[0][3], m[1][3], m[2][3]], cam_pos, "world position");
    }

    /// FOV は「垂直 → 垂直」なので単位変換のみ。範囲外はクランプされる。
    #[test]
    fn fov_conversion_is_vertical_to_vertical_with_clamp() {
        let deg = fov_y_rad_to_component_deg(std::f32::consts::FRAC_PI_4);
        assert!((deg - 45.0).abs() < 1e-3, "45° になること: {deg}");
        // 下限・上限クランプ
        assert_eq!(fov_y_rad_to_component_deg(0.0), CAMERA_FOV_DEG_MIN);
        assert_eq!(
            fov_y_rad_to_component_deg(std::f32::consts::PI),
            CAMERA_FOV_DEG_MAX
        );
    }

    /// 正射は「半高 → 全高」なので 2 倍する。下限クランプも効く。
    #[test]
    fn ortho_height_converts_half_to_full() {
        assert!((ortho_half_h_to_component_height(5.0) - 10.0).abs() < 1e-5);
        assert_eq!(
            ortho_half_h_to_component_height(0.0),
            CAMERA_ORTHO_HEIGHT_MIN
        );
    }
}
