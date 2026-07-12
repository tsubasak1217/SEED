// ============================================================
//  light_scene_gizmo.rs — ライトアクターの選択時ギズモ
//
//  選択中のアクターが LightComponent を持つとき、その種別に応じた
//  ワイヤーフレームギズモをシーンに描画する。
//    - directional: 照射方向を示す矢印
//    - point      : range 半径の球ワイヤ（3 円）
//    - spot       : 外側コーン角の円錐ワイヤ
//    - rect       : 発光矩形ワイヤ＋法線
//
//  camera_scene_gizmo と同じく LineBatch → GpuLineBatch を構築して返す。
//  実際の描画（line パイプラインでの draw）は呼び出し元 frame_renderer が行う。
// ============================================================

use std::f32::consts::PI;

use crate::engine::components::{ComponentKind, LightComponent, LightKind, Transform};
use crate::engine::ecs::World;
use crate::engine::methods::drawer::{GpuLineBatch, LineBatch};
use crate::engine::structs::objects::Actor;

// ── ギズモ寸法・色定数 ────────────────────────────────────────

/// ギズモの色（ライトらしい淡い黄）。
const LIGHT_GIZMO_COLOR: [f32; 4] = [1.0, 0.90, 0.35, 0.95];
/// 円ワイヤの分割数。
const CIRCLE_SEGS: usize = 32;
/// 平行光の矢印の長さ（ワールド単位）。
const DIR_ARROW_LEN: f32 = 2.0;
/// 平行光の矢じりの長さ。
const DIR_HEAD_LEN: f32 = 0.4;
/// 平行光の矢じりの開き幅。
const DIR_HEAD_W: f32 = 0.18;
/// スポットコーンの母線本数。
const SPOT_RIB_COUNT: usize = 4;

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

/// 中心 + 2 基底ベクトルで定義される平面上に半径 r の円ワイヤを追加する。
fn add_circle(lb: &mut LineBatch, center: [f32; 3], u: [f32; 3], v: [f32; 3], r: f32) {
    let mut prev = add3(center, scale3(u, r));
    for i in 1..=CIRCLE_SEGS {
        let t = 2.0 * PI * (i as f32) / (CIRCLE_SEGS as f32);
        let (s, c) = t.sin_cos();
        let p = add3(center, add3(scale3(u, r * c), scale3(v, r * s)));
        lb.add_line(prev, p, LIGHT_GIZMO_COLOR);
        prev = p;
    }
}

/// アイコンのワールドスケール係数（アクターのスケールとは独立した固定サイズ）。
/// アイコン GLB は camera_scene_gizmo と同じく camera.glb を暫定流用する。
const LIGHT_ICON_SCALE: f32 = 0.35;

/// アクター Transform からスケールを除いた回転+平行移動のみの 4x4 行列を返す。
/// ライトアイコンは常に LIGHT_ICON_SCALE で固定描画するためスケールは無視する。
fn icon_matrix(tf: &Transform) -> [[f32; 4]; 4] {
    Transform {
        position: tf.position,
        rotation: tf.rotation,
        scale:    [LIGHT_ICON_SCALE, LIGHT_ICON_SCALE, LIGHT_ICON_SCALE],
    }.to_mat4()
}

// ── 公開 API ──────────────────────────────────────────────────

/// LightComponent を持つ全アクターの (DFS ID, アイコン変換行列) リストを返す。
///
/// アイコン変換行列はアクターの位置・回転を保持しつつ
/// `LIGHT_ICON_SCALE` で固定スケールを適用した 4x4 行列。
/// GLB モデルを InstancedModelBatch で描画する際の `root_transforms` に使用する。
///
/// # 引数
/// - `actors` : ルートアクターのスライス
/// - `world`  : ECS ワールド（コンポーネント参照に使用）
/// - `wl`     : 対象の世界線番号
pub fn collect_light_actor_matrices(
    actors: &[Actor],
    world:  &World,
    wl:     u32,
) -> Vec<(usize, [[f32; 4]; 4])> {
    let mut result  = Vec::new();
    let mut counter = 0usize;
    collect_light_matrices_recursive(actors, world, wl, &mut counter, &mut result);
    result
}

/// アクターツリーを DFS 走査して LightComponent を持つ全アクターの
/// (DFS ID, アイコン行列) を収集する。
///
/// DFS カウンタはすべての world_line 一致アクターを数えるため、
/// LightComponent 非保持アクターもカウントのみ行う。
fn collect_light_matrices_recursive(
    actors:  &[Actor],
    world:   &World,
    wl:      u32,
    counter: &mut usize,
    result:  &mut Vec<(usize, [[f32; 4]; 4])>,
) {
    for actor in actors {
        if actor.world_line != wl { continue; }
        let dfs_id = *counter;
        *counter += 1;

        // LightComponent を持つアクターのみアイコン行列を追加する
        if actor.has_kind(ComponentKind::Light) {
            if let Some(light_entity) = actor.first_slot_entity_of_kind(ComponentKind::Light) {
                if world.get::<LightComponent>(light_entity).is_some() {
                    if let Some(tf) = world.get::<Transform>(actor.entity) {
                        result.push((dfs_id, icon_matrix(tf)));
                    }
                }
            }
        }
        // 子アクターを再帰処理
        collect_light_matrices_recursive(actor.children(), world, wl, counter, result);
    }
}

/// 選択中アクター（DFS 番号 `selected_dfs`）が LightComponent を持つ場合、
/// その種別に応じたギズモの GpuLineBatch を構築して返す。
///
/// バッチが空（Light なし・非選択）の場合は None を返す。
pub fn build_selected_light_gizmo_batch(
    actors:       &[Actor],
    world:        &World,
    wl:           u32,
    selected_dfs: Option<usize>,
    device:       &wgpu::Device,
) -> Option<GpuLineBatch> {
    let dfs = selected_dfs? as u32;
    let mut lb = LineBatch::new();
    let mut counter = 0u32;
    add_light_gizmo_for_dfs(actors, world, wl, dfs, &mut counter, &mut lb);
    if lb.is_empty() { None } else { Some(lb.build(device)) }
}

/// DFS 走査して対象アクターの全 Light スロットのギズモを追加する。
fn add_light_gizmo_for_dfs(
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
                    if slot.kind != ComponentKind::Light { continue; }
                    if let Some(lc) = world.get::<LightComponent>(slot.entity) {
                        add_one_light_gizmo(lb, tf, lc);
                    }
                }
            }
            return true;
        }

        if add_light_gizmo_for_dfs(actor.children(), world, wl, dfs, counter, lb) {
            return true;
        }
    }
    false
}

/// 1 ライト分のギズモを LineBatch に追加する。
fn add_one_light_gizmo(lb: &mut LineBatch, tf: &Transform, lc: &LightComponent) {
    let pos = tf.position;
    let fwd = normalize3(tf.forward());
    let up  = normalize3(tf.up());
    // 面の右方向（forward × up）。
    let right = normalize3(cross3(fwd, up));
    // up を直交化し直す。
    let up_o  = normalize3(cross3(right, fwd));

    match lc.kind {
        LightKind::Directional => {
            // 照射方向の矢印（pos → pos+fwd*len）＋矢じり。
            let tip = add3(pos, scale3(fwd, DIR_ARROW_LEN));
            lb.add_line(pos, tip, LIGHT_GIZMO_COLOR);
            let back = add3(tip, scale3(fwd, -DIR_HEAD_LEN));
            for &side in &[1.0f32, -1.0] {
                lb.add_line(tip, add3(back, scale3(right, DIR_HEAD_W * side)), LIGHT_GIZMO_COLOR);
                lb.add_line(tip, add3(back, scale3(up_o,  DIR_HEAD_W * side)), LIGHT_GIZMO_COLOR);
            }
        }
        LightKind::Point => {
            // range 半径の球ワイヤ（3 平面の円）。
            let r = lc.range.max(1e-3);
            add_circle(lb, pos, [1.0, 0.0, 0.0], [0.0, 1.0, 0.0], r);
            add_circle(lb, pos, [0.0, 1.0, 0.0], [0.0, 0.0, 1.0], r);
            add_circle(lb, pos, [1.0, 0.0, 0.0], [0.0, 0.0, 1.0], r);
        }
        LightKind::Spot => {
            // 外側コーン角・range で決まる円錐ワイヤ。
            let len   = lc.range.max(1e-3);
            let half  = lc.outer_angle_deg.clamp(0.0, 89.0).to_radians();
            let base_r = len * half.tan();
            let base_c = add3(pos, scale3(fwd, len));
            // 底面円。
            add_circle(lb, base_c, right, up_o, base_r);
            // 母線（apex → 底面円の 4 点）。
            for i in 0..SPOT_RIB_COUNT {
                let t = 2.0 * PI * (i as f32) / (SPOT_RIB_COUNT as f32);
                let (s, c) = t.sin_cos();
                let rim = add3(base_c, add3(scale3(right, base_r * c), scale3(up_o, base_r * s)));
                lb.add_line(pos, rim, LIGHT_GIZMO_COLOR);
            }
        }
        LightKind::Rect => {
            // 発光矩形ワイヤ（right/up 半extents）＋法線。
            let hw = (lc.rect_width * 0.5).max(1e-4);
            let hh = (lc.rect_height * 0.5).max(1e-4);
            let r  = scale3(right, hw);
            let u  = scale3(up_o,  hh);
            let c0 = add3(add3(pos, scale3(r, -1.0)), scale3(u, -1.0));
            let c1 = add3(add3(pos, r),               scale3(u, -1.0));
            let c2 = add3(add3(pos, r),               u);
            let c3 = add3(add3(pos, scale3(r, -1.0)), u);
            lb.add_line(c0, c1, LIGHT_GIZMO_COLOR);
            lb.add_line(c1, c2, LIGHT_GIZMO_COLOR);
            lb.add_line(c2, c3, LIGHT_GIZMO_COLOR);
            lb.add_line(c3, c0, LIGHT_GIZMO_COLOR);
            // 法線（発光方向）。
            lb.add_line(pos, add3(pos, scale3(fwd, DIR_ARROW_LEN * 0.5)), LIGHT_GIZMO_COLOR);
        }
    }
}
