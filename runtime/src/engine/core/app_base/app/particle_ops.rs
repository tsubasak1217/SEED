// ============================================================
//  particle_ops.rs — ParticleEmitterComponent のインスペクタ編集
//
//  ・handle_set_particle_field: インスペクタ（C#）からの SET_PARTICLE_FIELD IPC を
//    受けて ParticleEmitterComponent のフィールドを更新する（light_ops と同流儀）。
//
//  パーティクルのシミュレーション（GPU compute）・描画はレンダラ側が別管理する。
//  本ファイルはあくまでエディタからのパラメータ編集の反映のみを担う。
// ============================================================

use crate::engine::components::{
    ComponentKind, ParticleBlend, ParticleEmitterComponent, ParticleSimSpace,
    MAX_PARTICLES_PER_EMITTER,
};

use super::App;

impl App {
    /// インスペクタからの ParticleEmitterComponent フィールド更新（SET_PARTICLE_FIELD IPC）。
    ///
    /// key: emit_rate / burst / max_particles / lifetime_min / lifetime_max /
    ///      speed_min / speed_max / spread_angle / dir_x / dir_y / dir_z /
    ///      gravity_x / gravity_y / gravity_z / drag / size_min / size_max /
    ///      end_size_scale / sc_r / sc_g / sc_b / sc_a / ec_r / ec_g / ec_b / ec_a /
    ///      texture_path / blend / sim_space / playing / loop_emit。
    /// 不正な key・value は無視する（パース失敗時も無視）。
    pub(super) fn handle_set_particle_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        key:          &str,
        value:        &str,
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
        let Some(pe) = scene.world.get_mut::<ParticleEmitterComponent>(entity) else { return };

        // key ごとに値を解釈して反映する（パース失敗は無視）。
        // 数値は用途に応じて clamp する（lifetime/speed/size の min<=max は強制しない）。
        match key {
            // ─── 放出 ───────────────────────────────────────────
            "emit_rate"      => if let Ok(v) = value.parse::<f32>() { pe.emit_rate = v.max(0.0); },
            "burst"          => if let Ok(v) = value.parse::<u32>() { pe.burst = v; },
            "max_particles"  => if let Ok(v) = value.parse::<u32>() {
                // GPU プールサイズは 1..=上限 に clamp する。
                pe.max_particles = v.clamp(1, MAX_PARTICLES_PER_EMITTER);
            },
            // ─── 寿命 ───────────────────────────────────────────
            "lifetime_min"   => if let Ok(v) = value.parse::<f32>() { pe.lifetime[0] = v.max(0.0); },
            "lifetime_max"   => if let Ok(v) = value.parse::<f32>() { pe.lifetime[1] = v.max(0.0); },
            // ─── 初速 ───────────────────────────────────────────
            "speed_min"      => if let Ok(v) = value.parse::<f32>() { pe.initial_speed[0] = v.max(0.0); },
            "speed_max"      => if let Ok(v) = value.parse::<f32>() { pe.initial_speed[1] = v.max(0.0); },
            // ─── 放出形状 ───────────────────────────────────────
            "spread_angle"   => if let Ok(v) = value.parse::<f32>() { pe.spread_angle_deg = v.clamp(0.0, 180.0); },
            "dir_x"          => if let Ok(v) = value.parse::<f32>() { pe.direction_local[0] = v; },
            "dir_y"          => if let Ok(v) = value.parse::<f32>() { pe.direction_local[1] = v; },
            "dir_z"          => if let Ok(v) = value.parse::<f32>() { pe.direction_local[2] = v; },
            // ─── 力学 ───────────────────────────────────────────
            "gravity_x"      => if let Ok(v) = value.parse::<f32>() { pe.gravity[0] = v; },
            "gravity_y"      => if let Ok(v) = value.parse::<f32>() { pe.gravity[1] = v; },
            "gravity_z"      => if let Ok(v) = value.parse::<f32>() { pe.gravity[2] = v; },
            "drag"           => if let Ok(v) = value.parse::<f32>() { pe.drag = v.max(0.0); },
            // ─── サイズ ─────────────────────────────────────────
            "size_min"       => if let Ok(v) = value.parse::<f32>() { pe.start_size[0] = v.max(0.0); },
            "size_max"       => if let Ok(v) = value.parse::<f32>() { pe.start_size[1] = v.max(0.0); },
            "end_size_scale" => if let Ok(v) = value.parse::<f32>() { pe.end_size_scale = v.max(0.0); },
            // ─── 開始色（RGBA、各成分 0 以上）───────────────────
            "sc_r"           => if let Ok(v) = value.parse::<f32>() { pe.start_color[0] = v.max(0.0); },
            "sc_g"           => if let Ok(v) = value.parse::<f32>() { pe.start_color[1] = v.max(0.0); },
            "sc_b"           => if let Ok(v) = value.parse::<f32>() { pe.start_color[2] = v.max(0.0); },
            "sc_a"           => if let Ok(v) = value.parse::<f32>() { pe.start_color[3] = v.max(0.0); },
            // ─── 終了色（RGBA、各成分 0 以上）───────────────────
            "ec_r"           => if let Ok(v) = value.parse::<f32>() { pe.end_color[0] = v.max(0.0); },
            "ec_g"           => if let Ok(v) = value.parse::<f32>() { pe.end_color[1] = v.max(0.0); },
            "ec_b"           => if let Ok(v) = value.parse::<f32>() { pe.end_color[2] = v.max(0.0); },
            "ec_a"           => if let Ok(v) = value.parse::<f32>() { pe.end_color[3] = v.max(0.0); },
            // ─── その他 ─────────────────────────────────────────
            "texture_path"   => pe.texture_path = value.to_string(),
            "blend"          => if let Some(b) = ParticleBlend::from_str_opt(value) { pe.blend = b; },
            "sim_space"      => if let Some(s) = ParticleSimSpace::from_str_opt(value) { pe.sim_space = s; },
            "playing"        => pe.playing = value == "1",
            "loop_emit"      => pe.loop_emit = value == "1",
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}
