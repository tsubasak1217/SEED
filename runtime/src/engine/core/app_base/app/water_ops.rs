// ============================================================
//  water_ops.rs — WaterVolumeComponent のインスペクタ更新
//
//  ・handle_set_water_field: インスペクタ（C#）からの SET_WATER_FIELD IPC を
//    受けて WaterVolumeComponent のフィールドを更新する
//    （AudioComponent / LightComponent と同流儀）。
//
//  値の解釈規則:
//    ・kind は文字列（"Ocean" / "Region" / "Spline"）
//    ・ベクタ系（region_half_extents / *_color）は "x,y,z" 形式
//    ・0..1 の正規化パラメータは clamp、距離・サイズ系は 0 未満を許さない
// ============================================================

use crate::engine::components::water_volume_component::{
    WaterVolumeComponent, WaterVolumeKind,
};
use crate::engine::components::ComponentKind;

use super::App;

// ─── クランプ境界の定数 ───────────────────────────────────────
// マジックナンバー禁止のため、クランプ境界はすべて定数化する。

/// 正規化パラメータ（不透明度・フォーム強度・フレネル寄与）の下限。
const NORMALIZED_MIN: f32 = 0.0;
/// 正規化パラメータの上限。
const NORMALIZED_MAX: f32 = 1.0;
/// 距離・サイズ・強度など「負値に意味が無い」パラメータの下限。
const NON_NEGATIVE_MIN: f32 = 0.0;
/// 色成分の下限（リニア色。負のエネルギーは持てない）。上限は設けない（HDR 許容）。
const COLOR_CHANNEL_MIN: f32 = 0.0;
/// "x,y,z" 形式の要素数。
const VEC3_COMPONENT_COUNT: usize = 3;

impl App {
    /// インスペクタからの WaterVolumeComponent フィールド更新（SET_WATER_FIELD IPC）。
    ///
    /// key: kind / surface_height / region_half_extents / ocean_extent /
    ///      shallow_color / deep_color / absorption_distance / surface_opacity /
    ///      foam_color / foam_width / foam_intensity / wave_amplitude / wave_scale /
    ///      wave_speed / ripple_strength / ripple_foam_threshold /
    ///      fresnel_power / fresnel_strength / reflection_color /
    ///      refraction_distortion。
    /// ベクタ系（region_half_extents / *_color）は "x,y,z" 形式。
    /// 不正な key・value は無視する（インスペクタへの再送信も行わない）。
    pub(super) fn handle_set_water_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        key:          &str,
        value:        &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_audio_field と同流儀）。
        // kind が WaterVolume でないスロットへの誤配は弾く。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::WaterVolume)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(w) = scene.world.get_mut::<WaterVolumeComponent>(entity) else { return };

        // key ごとに値を解釈して反映する（パース失敗は無視）。
        match key {
            "kind" => {
                if let Some(k) = WaterVolumeKind::from_str_opt(value) { w.kind = k; }
            }
            "surface_height" => {
                // 水面高さは Ocean=ワールド絶対 / Region=相対。どちらも負値を許す。
                if let Ok(v) = value.parse::<f32>() { w.surface_height = v; }
            }
            "region_half_extents" => {
                // AABB 半径。反転 AABB を作らないよう負値は 0 に丸める。
                if let Some(v) = parse_vec3(value) {
                    w.region_half_extents = clamp_vec3_min(v, NON_NEGATIVE_MIN);
                }
            }
            "ocean_extent" => {
                if let Ok(v) = value.parse::<f32>() { w.ocean_extent = v.max(NON_NEGATIVE_MIN); }
            }
            "shallow_color" => {
                if let Some(v) = parse_vec3(value) {
                    w.shallow_color = clamp_vec3_min(v, COLOR_CHANNEL_MIN);
                }
            }
            "deep_color" => {
                if let Some(v) = parse_vec3(value) {
                    w.deep_color = clamp_vec3_min(v, COLOR_CHANNEL_MIN);
                }
            }
            "absorption_distance" => {
                if let Ok(v) = value.parse::<f32>() {
                    w.absorption_distance = v.max(NON_NEGATIVE_MIN);
                }
            }
            "surface_opacity" => {
                if let Ok(v) = value.parse::<f32>() {
                    w.surface_opacity = v.clamp(NORMALIZED_MIN, NORMALIZED_MAX);
                }
            }
            "foam_color" => {
                if let Some(v) = parse_vec3(value) {
                    w.foam_color = clamp_vec3_min(v, COLOR_CHANNEL_MIN);
                }
            }
            "foam_width" => {
                if let Ok(v) = value.parse::<f32>() { w.foam_width = v.max(NON_NEGATIVE_MIN); }
            }
            "foam_intensity" => {
                if let Ok(v) = value.parse::<f32>() {
                    w.foam_intensity = v.clamp(NORMALIZED_MIN, NORMALIZED_MAX);
                }
            }
            "wave_amplitude" => {
                if let Ok(v) = value.parse::<f32>() { w.wave_amplitude = v.max(NON_NEGATIVE_MIN); }
            }
            "wave_scale" => {
                if let Ok(v) = value.parse::<f32>() { w.wave_scale = v.max(NON_NEGATIVE_MIN); }
            }
            "wave_speed" => {
                // 逆流方向の波を許すため負値も受け付ける（速度は符号を持つ）。
                if let Ok(v) = value.parse::<f32>() { w.wave_speed = v; }
            }
            "fresnel_power" => {
                if let Ok(v) = value.parse::<f32>() { w.fresnel_power = v.max(NON_NEGATIVE_MIN); }
            }
            "fresnel_strength" => {
                if let Ok(v) = value.parse::<f32>() {
                    w.fresnel_strength = v.clamp(NORMALIZED_MIN, NORMALIZED_MAX);
                }
            }
            "reflection_color" => {
                if let Some(v) = parse_vec3(value) {
                    w.reflection_color = clamp_vec3_min(v, COLOR_CHANNEL_MIN);
                }
            }
            "refraction_distortion" => {
                if let Ok(v) = value.parse::<f32>() {
                    w.refraction_distortion = v.max(NON_NEGATIVE_MIN);
                }
            }
            // ── 波紋・航跡（Phase I2）────────────────────────────────
            "ripple_strength" => {
                // 負値は法線を逆向きに歪めるだけで意味を持たないため 0 で下限を切る。
                if let Ok(v) = value.parse::<f32>() { w.ripple_strength = v.max(NON_NEGATIVE_MIN); }
            }
            "ripple_foam_threshold" => {
                // 0 だと静水面まで泡だらけになるため、描画側と同じ下限で締める。
                if let Ok(v) = value.parse::<f32>() {
                    w.ripple_foam_threshold = v.max(NON_NEGATIVE_MIN);
                }
            }
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}

// ─── パースヘルパー ──────────────────────────────────────────

/// "x,y,z" 形式の文字列を [f32; 3] へパースする。
/// 要素数違い・数値でない要素があれば None（呼び出し側で無視する）。
fn parse_vec3(value: &str) -> Option<[f32; 3]> {
    let parts: Vec<&str> = value.split(',').collect();
    if parts.len() != VEC3_COMPONENT_COUNT { return None; }
    Some([
        parts[0].trim().parse::<f32>().ok()?,
        parts[1].trim().parse::<f32>().ok()?,
        parts[2].trim().parse::<f32>().ok()?,
    ])
}

/// 3 成分すべてに下限クランプを掛ける。
fn clamp_vec3_min(v: [f32; 3], min: f32) -> [f32; 3] {
    [v[0].max(min), v[1].max(min), v[2].max(min)]
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// "x,y,z" が正しくパースされること（空白入りも許容）。
    #[test]
    fn parse_vec3_accepts_valid_triples() {
        assert_eq!(parse_vec3("1,2,3"), Some([1.0, 2.0, 3.0]));
        assert_eq!(parse_vec3(" 0.5 , -1.5 , 2 "), Some([0.5, -1.5, 2.0]));
    }

    /// 要素数違い・非数値は None になること。
    #[test]
    fn parse_vec3_rejects_malformed() {
        assert_eq!(parse_vec3("1,2"), None,        "要素不足");
        assert_eq!(parse_vec3("1,2,3,4"), None,    "要素過多");
        assert_eq!(parse_vec3("1,abc,3"), None,    "非数値");
        assert_eq!(parse_vec3(""), None,           "空文字列");
    }

    /// 下限クランプが 3 成分すべてに掛かること。
    #[test]
    fn clamp_vec3_min_applies_to_all_channels() {
        assert_eq!(clamp_vec3_min([-1.0, 0.5, -0.001], COLOR_CHANNEL_MIN), [0.0, 0.5, 0.0]);
        assert_eq!(clamp_vec3_min([1.0, 2.0, 3.0], NON_NEGATIVE_MIN), [1.0, 2.0, 3.0]);
    }
}
