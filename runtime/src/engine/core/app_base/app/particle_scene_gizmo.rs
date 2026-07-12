// ============================================================
//  particle_scene_gizmo.rs — パーティクルエミッタの選択時ギズモ
//
//  選択中のアクターが ParticleEmitterComponent を持つとき、その放出円錐を
//  ワイヤーフレームで描画する。円錐の:
//    - 頂点(apex) = アクター位置
//    - 軸         = Transform で回した direction_local
//    - 半頂角     = spread_angle_deg
//    - 長さ       = initial_speed.max × lifetime.max（放出粒子の到達距離の目安）
//
//  light_scene_gizmo と同じく LineBatch → GpuLineBatch を構築して返す。
//  実際の描画（line パイプラインでの draw）は呼び出し元 frame_renderer が行う。
// ============================================================

use std::f32::consts::PI;

use crate::engine::components::{ComponentKind, ParticleEmitterComponent, Transform};
use crate::engine::ecs::World;
use crate::engine::methods::drawer::{GpuLineBatch, LineBatch};
use crate::engine::structs::objects::Actor;

// ── ギズモ寸法・色定数（マジックナンバー禁止）─────────────────

/// ギズモの色（パーティクルらしい淡いシアン）。
const PARTICLE_GIZMO_COLOR: [f32; 4] = [0.35, 0.90, 1.0, 0.95];
/// 円ワイヤの分割数。
const CIRCLE_SEGS: usize = 32;
/// 円錐母線の本数。
const CONE_RIB_COUNT: usize = 4;
/// 放出円錐の長さ係数（length = initial_speed.max × lifetime.max × これ）。
const CONE_LEN_FACTOR: f32 = 1.0;
/// 円錐長さの下限（速度・寿命が極小でもギズモが見えるように）。
const CONE_LEN_MIN: f32 = 0.2;
/// 半頂角のクランプ上限（度）。90 度以上は tan が発散するため。
const CONE_HALF_ANGLE_MAX_DEG: f32 = 89.0;

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
    if len < 1e-6 { [0.0, 1.0, 0.0] } else { [v[0] / len, v[1] / len, v[2] / len] }
}

/// Transform の回転（＋スケール）でローカル方向ベクトルをワールドへ回す（正規化する）。
fn rotate_dir_by_transform(tf: &Transform, dir_local: [f32; 3]) -> [f32; 3] {
    let m = tf.to_mat4(); // 行優先 TRS
    // 方向ベクトル（w=0）なので上 3x3 のみ適用する。
    let x = m[0][0] * dir_local[0] + m[0][1] * dir_local[1] + m[0][2] * dir_local[2];
    let y = m[1][0] * dir_local[0] + m[1][1] * dir_local[1] + m[1][2] * dir_local[2];
    let z = m[2][0] * dir_local[0] + m[2][1] * dir_local[1] + m[2][2] * dir_local[2];
    normalize3([x, y, z])
}

/// 中心 + 2 基底ベクトルで定義される平面上に半径 r の円ワイヤを追加する。
fn add_circle(lb: &mut LineBatch, center: [f32; 3], u: [f32; 3], v: [f32; 3], r: f32) {
    let mut prev = add3(center, scale3(u, r));
    for i in 1..=CIRCLE_SEGS {
        let t = 2.0 * PI * (i as f32) / (CIRCLE_SEGS as f32);
        let (s, c) = t.sin_cos();
        let p = add3(center, add3(scale3(u, r * c), scale3(v, r * s)));
        lb.add_line(prev, p, PARTICLE_GIZMO_COLOR);
        prev = p;
    }
}

// ── 公開 API ──────────────────────────────────────────────────

/// 選択中アクター（DFS 番号 `selected_dfs`）が ParticleEmitterComponent を持つ場合、
/// その放出円錐ギズモの GpuLineBatch を構築して返す。
///
/// バッチが空（エミッタなし・非選択）の場合は None を返す。
pub fn build_selected_particle_gizmo_batch(
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

/// DFS 走査して対象アクターの全 ParticleEmitter スロットのギズモを追加する。
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
            if let Some(tf) = world.get::<Transform>(actor.entity) {
                for slot in actor.slots() {
                    if slot.kind != ComponentKind::ParticleEmitter { continue; }
                    if let Some(pe) = world.get::<ParticleEmitterComponent>(slot.entity) {
                        add_one_emitter_gizmo(lb, tf, pe);
                    }
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

/// 1 エミッタ分の放出円錐ギズモを LineBatch に追加する。
fn add_one_emitter_gizmo(lb: &mut LineBatch, tf: &Transform, pe: &ParticleEmitterComponent) {
    let apex = tf.position;
    // 放出軸（Transform で回した direction_local）。
    let axis = rotate_dir_by_transform(tf, pe.direction_local);
    // 軸に直交する基底を作る。
    let up_ref = if axis[1].abs() > 0.99 { [1.0, 0.0, 0.0] } else { [0.0, 1.0, 0.0] };
    let right  = normalize3(cross3(up_ref, axis));
    let up     = normalize3(cross3(axis, right));

    // 円錐長さ＝初速 max × 寿命 max（放出粒子の到達距離の目安）。
    let len = (pe.initial_speed[1] * pe.lifetime[1] * CONE_LEN_FACTOR).max(CONE_LEN_MIN);
    // 半頂角（direction_randomness*180 度→ラジアン、発散回避のためクランプ）。
    // 新スキーマでは spread_angle_deg を廃し direction_randomness(0..1) で表す。
    let half_deg = pe.direction_randomness
        * crate::engine::components::DIRECTION_RANDOMNESS_MAX_HALF_ANGLE_DEG;
    let half = half_deg.clamp(0.0, CONE_HALF_ANGLE_MAX_DEG).to_radians();
    let base_r = len * half.tan();
    let base_c = add3(apex, scale3(axis, len));

    // 底面円。
    add_circle(lb, base_c, right, up, base_r);
    // 母線（apex → 底面円の CONE_RIB_COUNT 点）。
    for i in 0..CONE_RIB_COUNT {
        let t = 2.0 * PI * (i as f32) / (CONE_RIB_COUNT as f32);
        let (s, c) = t.sin_cos();
        let rim = add3(base_c, add3(scale3(right, base_r * c), scale3(up, base_r * s)));
        lb.add_line(apex, rim, PARTICLE_GIZMO_COLOR);
    }
    // 中心軸線（apex → 底面中心）。
    lb.add_line(apex, base_c, PARTICLE_GIZMO_COLOR);
}
