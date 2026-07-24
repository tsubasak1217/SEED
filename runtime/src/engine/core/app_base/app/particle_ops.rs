// ============================================================
//  particle_ops.rs — ParticleEmitterComponent のインスペクタ編集
//
//  ・handle_set_particle_field: インスペクタ（C#）からの SET_PARTICLE_FIELD IPC を
//    受けて ParticleEmitterComponent のスカラー/enum フィールドを更新する
//    （light_ops と同流儀）。
//  ・handle_set_particle_curve : インスペクタ（C# カーブエディタ、次実装）からの
//    SET_PARTICLE_CURVE IPC を受けてカーブ（ParamCurve / Vec<ParamCurve>）を
//    差し替える。差し替え後は必ず `bump_curve_generation()` を呼び、レンダラの
//    GPU LUT 再焼き判定に反映する。
//
//  パーティクルのシミュレーション（GPU compute）・描画はレンダラ側が別管理する。
//  本ファイルはあくまでエディタからのパラメータ編集の反映のみを担う。
// ============================================================

use crate::engine::components::particle_emitter_component::MAX_PARTICLE_TEXTURES;
use crate::engine::components::{
    ComponentKind, EmitMode, MAX_PARTICLES_PER_EMITTER, ParamCurve, ParticleBlend,
    ParticleEmitterComponent, ParticleShape, ParticleSimSpace, SpawnVolume,
};

use super::App;

impl App {
    /// インスペクタからの ParticleEmitterComponent フィールド更新（SET_PARTICLE_FIELD IPC）。
    ///
    /// key: max_particles / shape / shape_model_path / spawn_volume / spawn_box_x /
    ///      spawn_box_y / spawn_box_z / spawn_sphere_radius / emit_mode /
    ///      emit_count_total / initial_delay / prewarm_time / emit_interval /
    ///      particles_per_emit / lifetime_min / lifetime_max / speed_min / speed_max /
    ///      dir_x / dir_y / dir_z / direction_randomness / gravity_x / gravity_y /
    ///      gravity_z / drag / rot_speed_min / rot_speed_max / size_min / size_max /
    ///      texture_path / blend / sim_space / playing。
    /// 不正な key・value は無視する（パース失敗時も無視）。
    pub(super) fn handle_set_particle_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        key: &str,
        value: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_light_field と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::ParticleEmitter)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(pe) = scene.world.get_mut::<ParticleEmitterComponent>(entity) else {
            return;
        };

        // key ごとに値を解釈して反映する（パース失敗は無視）。
        // 数値は用途に応じて clamp する（lifetime/speed/size の min<=max は強制しない）。
        match key {
            // ─── 基本 ───────────────────────────────────────────
            "max_particles" => {
                if let Ok(v) = value.parse::<u32>() {
                    // GPU プールサイズは 1..=上限 に clamp する。
                    pe.max_particles = v.clamp(1, MAX_PARTICLES_PER_EMITTER);
                }
            }
            // ─── 形状 ───────────────────────────────────────────
            // shape: tag（point|sphere|box|plane|model）から作る。Model 化する場合は
            // 現在の model_path を引き継ぐ（新規 Model 化は空パスから始まり、
            // shape_model_path で後から設定する運用）。
            "shape" => {
                if let Some(s) = ParticleShape::from_tag(value, pe.shape.model_path()) {
                    pe.shape = s;
                }
            }
            // shape_model_path: 現在 Model 形状ならパスを差し替え、Model 以外なら
            // Model 形状へ切り替えつつパスを設定する。
            "shape_model_path" => {
                pe.shape = ParticleShape::Model {
                    path: value.to_string(),
                };
            }
            // ─── 出現範囲（スポーン体積） ─────────────────────────
            "spawn_volume" => {
                if let Some(v) = SpawnVolume::from_tag(
                    value,
                    pe.spawn_volume.box_half_extents(),
                    pe.spawn_volume.sphere_radius(),
                ) {
                    pe.spawn_volume = v;
                }
            }
            "spawn_box_x" => {
                if let Ok(v) = value.parse::<f32>() {
                    let mut half = pe.spawn_volume.box_half_extents();
                    half[0] = v.max(0.0);
                    pe.spawn_volume = SpawnVolume::Box { half_extents: half };
                }
            }
            "spawn_box_y" => {
                if let Ok(v) = value.parse::<f32>() {
                    let mut half = pe.spawn_volume.box_half_extents();
                    half[1] = v.max(0.0);
                    pe.spawn_volume = SpawnVolume::Box { half_extents: half };
                }
            }
            "spawn_box_z" => {
                if let Ok(v) = value.parse::<f32>() {
                    let mut half = pe.spawn_volume.box_half_extents();
                    half[2] = v.max(0.0);
                    pe.spawn_volume = SpawnVolume::Box { half_extents: half };
                }
            }
            "spawn_sphere_radius" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.spawn_volume = SpawnVolume::Sphere { radius: v.max(0.0) };
                }
            }
            // ─── 放出制御 ───────────────────────────────────────
            "emit_mode" => {
                if let Some(m) = EmitMode::from_tag(value, pe.emit_mode.count_total()) {
                    pe.emit_mode = m;
                }
            }
            "emit_count_total" => {
                if let Ok(v) = value.parse::<u32>() {
                    pe.emit_mode = EmitMode::Count { total: v };
                }
            }
            "initial_delay" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.initial_delay = v.max(0.0);
                }
            }
            "prewarm_time" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.prewarm_time = v.max(0.0);
                }
            }
            "emit_interval" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.emit_interval = v.max(0.0);
                }
            }
            "particles_per_emit" => {
                if let Ok(v) = value.parse::<u32>() {
                    pe.particles_per_emit = v;
                }
            }
            // ─── 寿命 ───────────────────────────────────────────
            "lifetime_min" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.lifetime[0] = v.max(0.0);
                }
            }
            "lifetime_max" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.lifetime[1] = v.max(0.0);
                }
            }
            // ─── 初速・方向 ─────────────────────────────────────
            "speed_min" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.initial_speed[0] = v.max(0.0);
                }
            }
            "speed_max" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.initial_speed[1] = v.max(0.0);
                }
            }
            "dir_x" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.direction_local[0] = v;
                }
            }
            "dir_y" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.direction_local[1] = v;
                }
            }
            "dir_z" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.direction_local[2] = v;
                }
            }
            "direction_randomness" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.direction_randomness = v.clamp(0.0, 1.0);
                }
            }
            // ─── 力学 ───────────────────────────────────────────
            "gravity_x" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.gravity[0] = v;
                }
            }
            "gravity_y" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.gravity[1] = v;
                }
            }
            "gravity_z" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.gravity[2] = v;
                }
            }
            "drag" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.drag = v.max(0.0);
                }
            }
            // ─── 回転・サイズ ───────────────────────────────────
            // 回転速度（度/秒）は符号付き（逆回転もありうる）ため clamp しない。
            "rot_speed_min" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.rot_speed_range[0] = v;
                }
            }
            "rot_speed_max" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.rot_speed_range[1] = v;
                }
            }
            // 初期回転角（度）は符号付き（負角もありうる）ため clamp しない。
            "initial_rot_min" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.initial_rotation_range[0] = v;
                }
            }
            "initial_rot_max" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.initial_rotation_range[1] = v;
                }
            }
            "size_min" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.size_range[0] = v.max(0.0);
                }
            }
            "size_max" => {
                if let Ok(v) = value.parse::<f32>() {
                    pe.size_range[1] = v.max(0.0);
                }
            }
            // ─── その他 ─────────────────────────────────────────
            // texture_paths: JSON 文字列配列（例 ["assets://a.png","assets://b.png"]）。
            // 最大 MAX_PARTICLE_TEXTURES 枚に丸め、空文字要素は除く。
            "texture_paths" => {
                if let Ok(list) = serde_json::from_str::<Vec<String>>(value) {
                    pe.texture_paths = list
                        .into_iter()
                        .filter(|p| !p.is_empty())
                        .take(MAX_PARTICLE_TEXTURES)
                        .collect();
                }
            }
            // 旧単数キー（互換）: 空文字は空リスト、非空は 1 要素リストにする。
            "texture_path" => {
                pe.texture_paths = if value.is_empty() {
                    Vec::new()
                } else {
                    vec![value.to_string()]
                };
            }
            "blend" => {
                if let Some(b) = ParticleBlend::from_str_opt(value) {
                    pe.blend = b;
                }
            }
            "sim_space" => {
                if let Some(s) = ParticleSimSpace::from_str_opt(value) {
                    pe.sim_space = s;
                }
            }
            "playing" => pe.playing = value == "1",
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// インスペクタ（C# カーブエディタ）からのカーブ差し替え（SET_PARTICLE_CURVE IPC）。
    ///
    /// curve_id: speed | rot_speed | color | scale | random_colors。
    /// json は `ParamCurve` の serde JSON（random_colors のみ `Vec<ParamCurve>` の JSON 配列）。
    /// パース失敗・不正な curve_id は無視する。差し替え成功時は
    /// `bump_curve_generation()` を呼び、レンダラへ GPU LUT 再焼きが必要なことを伝える。
    pub(super) fn handle_set_particle_curve(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        curve_id: &str,
        json: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_particle_field と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::ParticleEmitter)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(pe) = scene.world.get_mut::<ParticleEmitterComponent>(entity) else {
            return;
        };

        // curve_id ごとに JSON をパースして該当フィールドへ差し替える。
        // パース失敗はログを出して無視する（SET_MATERIAL_OVERRIDE と同流儀）。
        let ok = match curve_id {
            "speed" => match serde_json::from_str(json) {
                Ok(c) => {
                    pe.speed_curve = c;
                    true
                }
                Err(e) => {
                    eprintln!("[SEED] SET_PARTICLE_CURVE(speed): json パース失敗（{e}）: {json}");
                    false
                }
            },
            "rot_speed" => match serde_json::from_str(json) {
                Ok(c) => {
                    pe.rot_speed_curve = c;
                    true
                }
                Err(e) => {
                    eprintln!(
                        "[SEED] SET_PARTICLE_CURVE(rot_speed): json パース失敗（{e}）: {json}"
                    );
                    false
                }
            },
            "scale" => match serde_json::from_str(json) {
                Ok(c) => {
                    pe.scale_curve = c;
                    true
                }
                Err(e) => {
                    eprintln!("[SEED] SET_PARTICLE_CURVE(scale): json パース失敗（{e}）: {json}");
                    false
                }
            },
            // 色カーブリスト全体（Vec<ParamCurve>）。最低 1 本を保証する（空なら白フェード補完）。
            "colors" => match serde_json::from_str::<Vec<ParamCurve>>(json) {
                Ok(list) => {
                    pe.color_curves = if list.is_empty() {
                        ParticleEmitterComponent::default().color_curves
                    } else {
                        list
                    };
                    true
                }
                Err(e) => {
                    eprintln!("[SEED] SET_PARTICLE_CURVE(colors): json パース失敗（{e}）: {json}");
                    false
                }
            },
            // 旧 curve_id 互換: "color"（単一）→ リストを 1 本で置換。
            "color" => match serde_json::from_str::<ParamCurve>(json) {
                Ok(c) => {
                    pe.color_curves = vec![c];
                    true
                }
                Err(e) => {
                    eprintln!("[SEED] SET_PARTICLE_CURVE(color): json パース失敗（{e}）: {json}");
                    false
                }
            },
            // 旧 curve_id 互換: "random_colors"（Vec）→ リスト置換（空なら既定で補完）。
            "random_colors" => match serde_json::from_str::<Vec<ParamCurve>>(json) {
                Ok(list) => {
                    pe.color_curves = if list.is_empty() {
                        ParticleEmitterComponent::default().color_curves
                    } else {
                        list
                    };
                    true
                }
                Err(e) => {
                    eprintln!(
                        "[SEED] SET_PARTICLE_CURVE(random_colors): json パース失敗（{e}）: {json}"
                    );
                    false
                }
            },
            _ => false,
        };
        if !ok {
            return;
        }

        // GPU LUT 再焼きが必要なことをレンダラへ伝える世代カウンタを進める。
        pe.bump_curve_generation();

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }
}
