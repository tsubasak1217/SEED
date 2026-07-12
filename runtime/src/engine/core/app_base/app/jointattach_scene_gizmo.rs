// ============================================================
//  jointattach_scene_gizmo.rs — ジョイントアタッチ選択時ギズモ
//
//  選択中のアクターが JointAttachComponent を持つとき、そのアクターの
//  現在位置（追従後＝ジョイント×オフセットの結果）に小さな RGB 軸十字を
//  描画してソケット位置を可視化する。
//
//  追従結果は update_joint_attachments が毎フレーム自アクターの Transform へ
//  書き込むため、ここでは自アクターの Transform をそのまま基準に軸を描く
//  （ジョイント行列の再計算は不要）。
//
//  light_scene_gizmo と同じく LineBatch → GpuLineBatch を構築して返す。
//  実際の描画（line パイプラインでの draw）は呼び出し元 frame_renderer が行う。
// ============================================================

use crate::engine::components::{ComponentKind, JointAttachComponent, Transform};
use crate::engine::ecs::World;
use crate::engine::methods::drawer::{GpuLineBatch, LineBatch};
use crate::engine::structs::objects::Actor;

// ── ギズモ寸法・色定数 ────────────────────────────────────────

/// 軸の長さ（ワールド単位・固定サイズ）。
const AXIS_LEN: f32 = 0.3;
/// X 軸の色（赤）。
const AXIS_COLOR_X: [f32; 4] = [1.0, 0.25, 0.25, 0.95];
/// Y 軸の色（緑）。
const AXIS_COLOR_Y: [f32; 4] = [0.25, 1.0, 0.25, 0.95];
/// Z 軸の色（青）。
const AXIS_COLOR_Z: [f32; 4] = [0.35, 0.55, 1.0, 0.95];

// ── ベクトルヘルパー ──────────────────────────────────────────

#[inline]
fn add3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] { [a[0] + b[0], a[1] + b[1], a[2] + b[2]] }
#[inline]
fn scale3(v: [f32; 3], s: f32) -> [f32; 3] { [v[0] * s, v[1] * s, v[2] * s] }
#[inline]
fn cross3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
}
#[inline]
fn normalize3(v: [f32; 3]) -> [f32; 3] {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if len < 1e-6 { [0.0, 0.0, 1.0] } else { [v[0] / len, v[1] / len, v[2] / len] }
}

// ── 公開 API ──────────────────────────────────────────────────

/// 選択中アクター（DFS 番号 `selected_dfs`）が JointAttachComponent を持つ場合、
/// ソケット位置に RGB 軸十字を描くギズモの GpuLineBatch を構築して返す。
///
/// バッチが空（JointAttach なし・非選択）の場合は None を返す。
pub fn build_selected_jointattach_gizmo_batch(
    actors:       &[Actor],
    world:        &World,
    wl:           u32,
    selected_dfs: Option<usize>,
    device:       &wgpu::Device,
) -> Option<GpuLineBatch> {
    let dfs = selected_dfs? as u32;
    let mut lb = LineBatch::new();
    let mut counter = 0u32;
    add_gizmo_for_dfs(actors, world, wl, dfs, &mut counter, &mut lb);
    if lb.is_empty() { None } else { Some(lb.build(device)) }
}

/// DFS 走査して対象アクターが JointAttach を持てば軸ギズモを追加する。
fn add_gizmo_for_dfs(
    actors:  &[Actor],
    world:   &World,
    wl:      u32,
    dfs:     u32,
    counter: &mut u32,
    lb:      &mut LineBatch,
) -> bool {
    for actor in actors {
        if actor.world_line != wl { continue; }
        let current = *counter;
        *counter += 1;

        if current == dfs {
            // JointAttach スロットを持つときのみ、自アクターの Transform 基準で軸を描く
            let has_attach = actor.slots().iter().any(|s|
                s.kind == ComponentKind::JointAttach
                && world.get::<JointAttachComponent>(s.entity).is_some());
            if has_attach {
                if let Some(tf) = world.get::<Transform>(actor.entity) {
                    add_axes(lb, tf);
                }
            }
            return true;
        }

        if add_gizmo_for_dfs(actor.children(), world, wl, dfs, counter, lb) {
            return true;
        }
    }
    false
}

/// アクター Transform の位置・向きに RGB 軸十字（各軸 AXIS_LEN）を追加する。
fn add_axes(lb: &mut LineBatch, tf: &Transform) {
    let pos = tf.position;
    // Transform の基底ベクトル（スケール無視の純方向）。
    let fwd   = normalize3(tf.forward());          // +Z
    let up    = normalize3(tf.up());               // +Y
    let right = normalize3(cross3(fwd, up));       // +X（左手 → forward×up）
    // up を直交化し直す。
    let up_o  = normalize3(cross3(right, fwd));

    lb.add_line(pos, add3(pos, scale3(right, AXIS_LEN)), AXIS_COLOR_X);
    lb.add_line(pos, add3(pos, scale3(up_o,  AXIS_LEN)), AXIS_COLOR_Y);
    lb.add_line(pos, add3(pos, scale3(fwd,   AXIS_LEN)), AXIS_COLOR_Z);
}
