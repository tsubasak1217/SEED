// ============================================================
//  light_ops.rs — LightComponent のインスペクタ編集と
//                 シーンライトの GPU ライト配列への収集
//
//  ・handle_set_light_field: インスペクタ（C#）からの SET_LIGHT_FIELD IPC を
//    受けて LightComponent のフィールドを更新する（AudioComponent と同流儀）。
//  ・collect_gpu_lights: シーンの Light スロットを走査し、Transform から
//    位置・向きを解決して GpuLight 配列を構築する。ライトが 1 灯も無い場合は
//    後方互換フォールバックの方向光（旧ハードコード値）を 1 灯投入する。
//
//  ライトの位置・向きは Actor の Transform から解決する（camera_scene_gizmo と
//  同様、アクター自身の Transform を使う。R1 では親トランスフォームの合成は
//  行わない＝トップレベル配置を前提とする）。
// ============================================================

use crate::engine::components::{ComponentKind, LightComponent, LightKind, Transform};
use crate::engine::ecs::World;
use crate::engine::methods::drawer::GpuLight;
use crate::engine::structs::objects::Actor;

use super::App;

// ─── 後方互換フォールバック方向光（旧シェーダのハードコード値）─────

/// フォールバック方向光の「光源への方向（L）」。旧 shader_fragment.wgsl の
/// `light_dir = normalize(0.5, 1.0, -0.3)` と一致させる。
const FALLBACK_LIGHT_L: [f32; 3] = [0.5, 1.0, -0.3];
/// フォールバック方向光の色（白）。旧 `light_color = vec3(3.0)` は色白×強度3。
const FALLBACK_LIGHT_COLOR: [f32; 3] = [1.0, 1.0, 1.0];
/// フォールバック方向光の強度（旧 vec3(3.0) 相当）。
const FALLBACK_LIGHT_INTENSITY: f32 = 3.0;

impl App {
    /// インスペクタからの LightComponent フィールド更新（SET_LIGHT_FIELD IPC）。
    ///
    /// key: kind / color / intensity / range / inner_angle / outer_angle /
    ///      rect_width / rect_height / cast_shadows。
    /// color は "r,g,b" 形式。不正な key・value は無視する。
    pub(super) fn handle_set_light_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        key: &str,
        value: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_audio_field と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Light)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(lc) = scene.world.get_mut::<LightComponent>(entity) else {
            return;
        };

        // key ごとに値を解釈して反映する（パース失敗は無視）。
        match key {
            "kind" => {
                if let Some(k) = LightKind::from_str_opt(value) {
                    lc.kind = k;
                }
            }
            "color" => {
                // "r,g,b"（リニア）をパースする。
                let parts: Vec<&str> = value.split(',').collect();
                if parts.len() == 3 {
                    if let (Ok(r), Ok(g), Ok(b)) = (
                        parts[0].parse::<f32>(),
                        parts[1].parse::<f32>(),
                        parts[2].parse::<f32>(),
                    ) {
                        lc.color = [r.max(0.0), g.max(0.0), b.max(0.0)];
                    }
                }
            }
            "intensity" => {
                if let Ok(v) = value.parse::<f32>() {
                    lc.intensity = v.max(0.0);
                }
            }
            "range" => {
                if let Ok(v) = value.parse::<f32>() {
                    lc.range = v.max(0.0);
                }
            }
            "inner_angle" => {
                if let Ok(v) = value.parse::<f32>() {
                    lc.inner_angle_deg = v.clamp(0.0, 89.0);
                }
            }
            "outer_angle" => {
                if let Ok(v) = value.parse::<f32>() {
                    lc.outer_angle_deg = v.clamp(0.0, 89.0);
                }
            }
            "rect_width" => {
                if let Ok(v) = value.parse::<f32>() {
                    lc.rect_width = v.max(0.0);
                }
            }
            "rect_height" => {
                if let Ok(v) = value.parse::<f32>() {
                    lc.rect_height = v.max(0.0);
                }
            }
            "soft_radius" => {
                if let Ok(v) = value.parse::<f32>() {
                    lc.soft_radius = v.max(0.0);
                }
            }
            // 疑似バウンス（間接光近似）の強度。負値は 0 にクランプ（上限は設けない）。
            "bounce_intensity" => {
                if let Ok(v) = value.parse::<f32>() {
                    lc.bounce_intensity = v.max(0.0);
                }
            }
            "cast_shadows" => lc.cast_shadows = value == "1" || value == "true",
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }
}

// ─── ライト収集（GPU ライト配列の構築）─────────────────────────

/// シーンの全 Light スロットを走査して GpuLight 配列を構築する。
///
/// - `wl` に属し、`active` なアクターの `enabled` な Light スロットのみを対象とする
///   （非アクティブアクターはサブツリーごとスキップする）。
/// - 位置・向きはアクターの Transform から解決する。
/// - ライトが 1 灯も無い場合は後方互換フォールバックの方向光を 1 灯返す
///   （旧シェーダのハードコード光と同一の見た目を保つ）。
pub(crate) fn collect_gpu_lights(actors: &[Actor], world: &World, wl: u32) -> Vec<GpuLight> {
    let mut out = Vec::new();
    collect_lights_recursive(actors, world, wl, &mut out);

    // フォールバック: ライトが無いシーンは旧ハードコード方向光を投入して
    // 従来の見た目（暗くならない）を維持する。
    if out.is_empty() {
        // FALLBACK_LIGHT_L は「光源への方向（L）」なので、GpuLight.direction
        // （光が進む向き）はその反転で渡す（シェーダが L = -direction を取る）。
        out.push(GpuLight::directional(
            [
                -FALLBACK_LIGHT_L[0],
                -FALLBACK_LIGHT_L[1],
                -FALLBACK_LIGHT_L[2],
            ],
            FALLBACK_LIGHT_COLOR,
            FALLBACK_LIGHT_INTENSITY,
        ));
    }

    out
}

/// アクターツリーを DFS 走査して Light スロットを GpuLight に変換する。
fn collect_lights_recursive(actors: &[Actor], world: &World, wl: u32, out: &mut Vec<GpuLight>) {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        // 非アクティブアクターはサブツリーごと除外する。
        if !actor.active {
            continue;
        }

        // アクターの Transform（位置・向き）。無い（2D 等）場合は原点・+Z 前方。
        let tf = world.get::<Transform>(actor.entity);

        for slot in actor.slots() {
            if slot.kind != ComponentKind::Light || !slot.enabled {
                continue;
            }
            let Some(lc) = world.get::<LightComponent>(slot.entity) else {
                continue;
            };

            let position = tf.map(|t| t.position).unwrap_or([0.0, 0.0, 0.0]);
            // forward() = +Z 前方（光が進む向き）。up()/right() で rect 面軸を作る。
            let direction = tf.map(|t| t.forward()).unwrap_or([0.0, 0.0, 1.0]);

            let mut gpu = match lc.kind {
                LightKind::Directional => GpuLight::directional(direction, lc.color, lc.intensity),
                LightKind::Point => GpuLight::point(position, lc.color, lc.intensity, lc.range),
                LightKind::Spot => GpuLight::spot(
                    position,
                    direction,
                    lc.color,
                    lc.intensity,
                    lc.range,
                    lc.inner_angle_deg,
                    lc.outer_angle_deg,
                ),
                LightKind::Rect => {
                    let up = tf.map(|t| t.up()).unwrap_or([0.0, 1.0, 0.0]);
                    // right = forward × up（面の横軸）。
                    let right = cross(direction, up);
                    GpuLight::rect(
                        position,
                        direction,
                        right,
                        up,
                        lc.color,
                        lc.intensity,
                        lc.range,
                        lc.rect_width,
                        lc.rect_height,
                    )
                }
            };
            // 影希望のセンチネル（Phase R2）: cast_shadows=true なら shadow_index=1.0。
            // 実スロット（方向光 0 / スポット 0..3）は ShadowResources::prepare_frame が
            // 採用可否とともに確定させる。false は -1.0（影なし）のまま。
            gpu.shadow_index = if lc.cast_shadows { 1.0 } else { -1.0 };

            // ソフト影の見込み半径（Phase R8）。種別で意味が異なるためここで GpuLight 用へ換算する:
            //   - directional: soft_radius は角径（度）。シェーダは tan(角径) を分散スロープに使うため
            //     ここで度→ラジアン→tan を事前計算する（微小角では ≒ ラジアン値）。距離非依存。
            //   - point/spot/rect: soft_radius はワールド半径。シェーダ側で radius/距離＝見込み角に
            //     換算するため、そのまま渡す。
            // 0 のときは 0 のまま（シェーダはハードシャドウ 1 本へ分岐）。
            let sr = lc.soft_radius.max(0.0);
            gpu.soft_radius = if sr <= 0.0 {
                0.0
            } else if lc.kind == LightKind::Directional {
                sr.to_radians().tan()
            } else {
                sr
            };

            // 疑似バウンス（間接光近似）の強度を GpuLight へ反映する。
            // directional は減衰の基準距離が無いため対象外＝常に 0 に固定する
            // （Inspector 側でも項目を隠すが、データ層でも二重に保証する）。
            gpu.bounce_intensity = if lc.kind == LightKind::Directional {
                0.0
            } else {
                lc.bounce_intensity.max(0.0)
            };

            out.push(gpu);
        }

        // 子アクターを再帰処理（アクティブなアクターのみ到達する）。
        collect_lights_recursive(actor.children(), world, wl, out);
    }
}

/// 3D ベクトルの外積。
fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}
