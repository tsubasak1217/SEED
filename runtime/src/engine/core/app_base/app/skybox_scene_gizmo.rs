// ============================================================
//  skybox_scene_gizmo.rs — スカイボックスアクターの選択時ギズモ
//
//  選択中のアクターが WorldAnchored の SkyboxComponent を持つとき、その配置・向き・
//  スケールを示すワイヤー球（3 平面の楕円）をシーンに描画する。CameraLocked は
//  無限遠（カメラ追従）で実体を持たないためギズモは描かない。
//
//  light_scene_gizmo と同じく LineBatch → GpuLineBatch を構築して返す。実際の描画
//  （line パイプラインでの draw）は呼び出し元 frame_renderer が行う。
// ============================================================

use std::f32::consts::PI;

use crate::engine::components::{ComponentKind, SkyboxComponent, SkyboxMode, Transform};
use crate::engine::ecs::World;
use crate::engine::methods::drawer::{GpuLineBatch, LineBatch};
use crate::engine::structs::objects::Actor;

// ── ギズモ寸法・色定数 ────────────────────────────────────────

/// ギズモの色（天球らしい淡い水色）。
const SKYBOX_GIZMO_COLOR: [f32; 4] = [0.45, 0.75, 1.0, 0.95];
/// 円ワイヤの分割数。
const CIRCLE_SEGS: usize = 48;

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

/// 中心 + 2 基底ベクトル（各々スケール済み）で定義される楕円ワイヤを追加する。
fn add_ellipse(lb: &mut LineBatch, center: [f32; 3], u: [f32; 3], v: [f32; 3]) {
    let mut prev = add3(center, u);
    for i in 1..=CIRCLE_SEGS {
        let t = 2.0 * PI * (i as f32) / (CIRCLE_SEGS as f32);
        let (s, c) = t.sin_cos();
        let p = add3(center, add3(scale3(u, c), scale3(v, s)));
        lb.add_line(prev, p, SKYBOX_GIZMO_COLOR);
        prev = p;
    }
}

// ── 公開 API ──────────────────────────────────────────────────

/// 選択中アクター（DFS 番号 `selected_dfs`）が WorldAnchored の SkyboxComponent を
/// 持つ場合、その配置ワイヤ球の GpuLineBatch を構築して返す。
///
/// バッチが空（対象なし・非選択・CameraLocked のみ）の場合は None を返す。
pub fn build_selected_skybox_gizmo_batch(
    actors:       &[Actor],
    world:        &World,
    wl:           u32,
    selected_dfs: Option<usize>,
    device:       &wgpu::Device,
) -> Option<GpuLineBatch> {
    let dfs = selected_dfs? as u32;
    let mut lb = LineBatch::new();
    let mut counter = 0u32;
    add_skybox_gizmo_for_dfs(actors, world, wl, dfs, &mut counter, &mut lb);
    if lb.is_empty() { None } else { Some(lb.build(device)) }
}

/// DFS 走査して対象アクターの全 WorldAnchored Skybox スロットのギズモを追加する。
fn add_skybox_gizmo_for_dfs(
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
            if let Some(tf) = world.get::<Transform>(actor.entity) {
                for slot in actor.slots() {
                    if slot.kind != ComponentKind::Skybox { continue; }
                    if let Some(sb) = world.get::<SkyboxComponent>(slot.entity) {
                        // 実体を持つ WorldAnchored のみギズモを描く。
                        if sb.mode == SkyboxMode::WorldAnchored {
                            add_one_skybox_gizmo(lb, tf);
                        }
                    }
                }
            }
            return true;
        }

        if add_skybox_gizmo_for_dfs(actor.children(), world, wl, dfs, counter, lb) {
            return true;
        }
    }
    false
}

/// 1 スカイボックス分の配置ワイヤ球（3 平面の楕円）を LineBatch に追加する。
///
/// 単位球（半径 1）がアクターの回転＋スケールで変形した楕円体を、
/// 回転後の 3 基底軸（各々スケール済み）で 3 平面の楕円として表示する。
fn add_one_skybox_gizmo(lb: &mut LineBatch, tf: &Transform) {
    let pos = tf.position;
    // 回転後の直交基底（単位）。
    let fwd   = normalize3(tf.forward());
    let up    = normalize3(tf.up());
    let right = normalize3(cross3(fwd, up));
    let up_o  = normalize3(cross3(right, fwd));

    // 各軸をスケール（単位球半径 1 × scale）で伸ばした基底ベクトル。
    let rx = scale3(right, tf.scale[0].abs().max(1e-4));
    let uy = scale3(up_o,  tf.scale[1].abs().max(1e-4));
    let fz = scale3(fwd,   tf.scale[2].abs().max(1e-4));

    // 3 平面の楕円（xy / yz / xz 相当）。
    add_ellipse(lb, pos, rx, uy);
    add_ellipse(lb, pos, uy, fz);
    add_ellipse(lb, pos, rx, fz);
}
