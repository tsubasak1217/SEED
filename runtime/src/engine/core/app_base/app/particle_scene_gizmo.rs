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

use crate::engine::components::{ComponentKind, ParticleEmitterComponent, SpawnVolume, Transform};
use crate::engine::ecs::World;
use crate::engine::methods::drawer::{GpuLineBatch, LineBatch};
use crate::engine::structs::objects::Actor;

// ── ギズモ寸法・色定数（マジックナンバー禁止）─────────────────

/// ギズモの色（パーティクルらしい淡いシアン）。
const PARTICLE_GIZMO_COLOR: [f32; 4] = [0.35, 0.90, 1.0, 0.95];
/// 出現範囲（spawn_volume）ギズモの色（円錐と区別する淡い黄）。
const SPAWN_GIZMO_COLOR: [f32; 4] = [1.0, 0.90, 0.35, 0.95];
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
/// Point 出現範囲マーカー（小十字）の半径。
const POINT_CROSS_SIZE: f32 = 0.15;

// ── ベクトルヘルパー ──────────────────────────────────────────

#[inline]
fn add3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}
#[inline]
fn scale3(v: [f32; 3], s: f32) -> [f32; 3] {
    [v[0] * s, v[1] * s, v[2] * s]
}
#[inline]
fn cross3(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}
#[inline]
fn normalize3(v: [f32; 3]) -> [f32; 3] {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if len < 1e-6 {
        [0.0, 1.0, 0.0]
    } else {
        [v[0] / len, v[1] / len, v[2] / len]
    }
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

/// Transform の 3 つのワールド基底ベクトル（スケール込み）を返す。
/// 列 j＝ローカル軸 j のワールド像。box/sphere の出現範囲を実寸で描くのに使う。
fn world_basis(tf: &Transform) -> ([f32; 3], [f32; 3], [f32; 3]) {
    let m = tf.to_mat4(); // 行優先 TRS
    let ex = [m[0][0], m[1][0], m[2][0]];
    let ey = [m[0][1], m[1][1], m[2][1]];
    let ez = [m[0][2], m[1][2], m[2][2]];
    (ex, ey, ez)
}

/// 中心＋2 基底ベクトル（既にスケール済み）で円ワイヤを追加する（色指定）。
fn add_circle_vec(lb: &mut LineBatch, center: [f32; 3], u: [f32; 3], v: [f32; 3], color: [f32; 4]) {
    let mut prev = add3(center, u);
    for i in 1..=CIRCLE_SEGS {
        let t = 2.0 * PI * (i as f32) / (CIRCLE_SEGS as f32);
        let (s, c) = t.sin_cos();
        let p = add3(center, add3(scale3(u, c), scale3(v, s)));
        lb.add_line(prev, p, color);
        prev = p;
    }
}

/// 出現範囲（spawn_volume）のデバッグワイヤを追加する。
/// Point=小十字 / Box=ワイヤ箱 / Sphere=3 円（軸別）。中心はエミッタ位置。
fn add_spawn_volume_gizmo(lb: &mut LineBatch, tf: &Transform, spawn: &SpawnVolume) {
    let center = tf.position;
    let (ex, ey, ez) = world_basis(tf);
    match spawn {
        // Point: エミッタ原点に小さな十字（各軸方向。スケール込みだと潰れるため正規化）。
        SpawnVolume::Point => {
            let axes = [normalize3(ex), normalize3(ey), normalize3(ez)];
            for a in axes {
                let arm = scale3(a, POINT_CROSS_SIZE);
                lb.add_line(
                    add3(center, scale3(arm, -1.0)),
                    add3(center, arm),
                    SPAWN_GIZMO_COLOR,
                );
            }
        }
        // Box: ローカル軸並行ボックス（±half_extents）の 12 辺。
        SpawnVolume::Box { half_extents } => {
            let hx = scale3(ex, half_extents[0]);
            let hy = scale3(ey, half_extents[1]);
            let hz = scale3(ez, half_extents[2]);
            // 8 コーナー（符号の組み合わせ）。
            let mut corner = [[0.0f32; 3]; 8];
            for i in 0..8 {
                let sx = if i & 1 == 0 { -1.0 } else { 1.0 };
                let sy = if i & 2 == 0 { -1.0 } else { 1.0 };
                let sz = if i & 4 == 0 { -1.0 } else { 1.0 };
                corner[i] = add3(
                    center,
                    add3(scale3(hx, sx), add3(scale3(hy, sy), scale3(hz, sz))),
                );
            }
            // 各辺は 1 ビットだけ異なるコーナー対（12 辺）。
            for i in 0..8usize {
                for bit in [1usize, 2, 4] {
                    let j = i ^ bit;
                    if i < j {
                        lb.add_line(corner[i], corner[j], SPAWN_GIZMO_COLOR);
                    }
                }
            }
        }
        // Sphere: 半径 r を各基底方向へスケールした 3 円（非一様スケールは楕円になる）。
        SpawnVolume::Sphere { radius } => {
            let rx = scale3(ex, *radius);
            let ry = scale3(ey, *radius);
            let rz = scale3(ez, *radius);
            add_circle_vec(lb, center, rx, ry, SPAWN_GIZMO_COLOR);
            add_circle_vec(lb, center, ry, rz, SPAWN_GIZMO_COLOR);
            add_circle_vec(lb, center, rz, rx, SPAWN_GIZMO_COLOR);
        }
    }
}

/// アイコンのワールドスケール係数（アクターのスケールとは独立した固定サイズ）。
/// アイコン GLB は camera_scene_gizmo と同じく camera.glb を暫定流用する。
const PARTICLE_ICON_SCALE: f32 = 0.35;

/// アクター Transform からスケールを除いた回転+平行移動のみの 4x4 行列を返す。
/// エミッタアイコンは常に PARTICLE_ICON_SCALE で固定描画するためスケールは無視する。
fn icon_matrix(tf: &Transform) -> [[f32; 4]; 4] {
    Transform {
        position: tf.position,
        rotation: tf.rotation,
        scale: [
            PARTICLE_ICON_SCALE,
            PARTICLE_ICON_SCALE,
            PARTICLE_ICON_SCALE,
        ],
    }
    .to_mat4()
}

// ── 公開 API ──────────────────────────────────────────────────

/// ParticleEmitterComponent を持つ全アクターの (DFS ID, アイコン変換行列) リストを返す。
///
/// アイコン変換行列はアクターの位置・回転を保持しつつ
/// `PARTICLE_ICON_SCALE` で固定スケールを適用した 4x4 行列。
/// GLB モデルを InstancedModelBatch で描画する際の `root_transforms` に使用する。
///
/// # 引数
/// - `actors` : ルートアクターのスライス
/// - `world`  : ECS ワールド（コンポーネント参照に使用）
/// - `wl`     : 対象の世界線番号
pub fn collect_particle_actor_matrices(
    actors: &[Actor],
    world: &World,
    wl: u32,
) -> Vec<(usize, [[f32; 4]; 4])> {
    let mut result = Vec::new();
    let mut counter = 0usize;
    collect_particle_matrices_recursive(actors, world, wl, &mut counter, &mut result);
    result
}

/// アクターツリーを DFS 走査して ParticleEmitterComponent を持つ全アクターの
/// (DFS ID, アイコン行列) を収集する。
///
/// DFS カウンタはすべての world_line 一致アクターを数えるため、
/// ParticleEmitterComponent 非保持アクターもカウントのみ行う。
fn collect_particle_matrices_recursive(
    actors: &[Actor],
    world: &World,
    wl: u32,
    counter: &mut usize,
    result: &mut Vec<(usize, [[f32; 4]; 4])>,
) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let dfs_id = *counter;
        *counter += 1;

        // ParticleEmitterComponent を持つアクターのみアイコン行列を追加する
        if actor.has_kind(ComponentKind::ParticleEmitter) {
            if let Some(pe_entity) = actor.first_slot_entity_of_kind(ComponentKind::ParticleEmitter)
            {
                if world.get::<ParticleEmitterComponent>(pe_entity).is_some() {
                    if let Some(tf) = world.get::<Transform>(actor.entity) {
                        result.push((dfs_id, icon_matrix(tf)));
                    }
                }
            }
        }
        // 子アクターを再帰処理
        collect_particle_matrices_recursive(actor.children(), world, wl, counter, result);
    }
}

/// 選択中アクター（DFS 番号 `selected_dfs`）が ParticleEmitterComponent を持つ場合、
/// その放出円錐ギズモの GpuLineBatch を構築して返す。
///
/// バッチが空（エミッタなし・非選択）の場合は None を返す。
pub fn build_selected_particle_gizmo_batch(
    actors: &[Actor],
    world: &World,
    wl: u32,
    selected_dfs: Option<usize>,
    device: &wgpu::Device,
) -> Option<GpuLineBatch> {
    let dfs = selected_dfs? as u32;
    let mut lb = LineBatch::new();
    let mut counter = 0u32;
    add_gizmo_for_dfs(actors, world, wl, dfs, &mut counter, &mut lb);
    if lb.is_empty() {
        None
    } else {
        Some(lb.build(device))
    }
}

/// DFS 走査して対象アクターの全 ParticleEmitter スロットのギズモを追加する。
fn add_gizmo_for_dfs(
    actors: &[Actor],
    world: &World,
    wl: u32,
    dfs: u32,
    counter: &mut u32,
    lb: &mut LineBatch,
) -> bool {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let current = *counter;
        *counter += 1;

        if current == dfs {
            if let Some(tf) = world.get::<Transform>(actor.entity) {
                for slot in actor.slots() {
                    if slot.kind != ComponentKind::ParticleEmitter {
                        continue;
                    }
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
    let up_ref = if axis[1].abs() > 0.99 {
        [1.0, 0.0, 0.0]
    } else {
        [0.0, 1.0, 0.0]
    };
    let right = normalize3(cross3(up_ref, axis));
    let up = normalize3(cross3(axis, right));

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
        let rim = add3(
            base_c,
            add3(scale3(right, base_r * c), scale3(up, base_r * s)),
        );
        lb.add_line(apex, rim, PARTICLE_GIZMO_COLOR);
    }
    // 中心軸線（apex → 底面中心）。
    lb.add_line(apex, base_c, PARTICLE_GIZMO_COLOR);

    // 出現範囲（spawn_volume）のデバッグワイヤを併せて描く。
    add_spawn_volume_gizmo(lb, tf, &pe.spawn_volume);
}
