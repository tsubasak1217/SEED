// ============================================================
//  interaction_ops.rs — InteractionSourceComponent のインスペクタ更新（Phase I1）
//
//  ・handle_set_interaction_field: インスペクタ（C#）からの SET_INTERACTION_FIELD IPC を
//    受けて InteractionSourceComponent のフィールドを更新する
//    （water_ops.rs / light_ops.rs と同流儀）。
//
//  値の解釈規則:
//    ・radius   … 半径（m）。0 以下は場に何も書けないので下限でクランプする
//    ・strength … 0..1 の正規化パラメータ。範囲外はクランプする
//    ・enabled  … "true" / "false"
// ============================================================

use crate::engine::components::interaction_source_component::InteractionSourceComponent;
use crate::engine::components::ComponentKind;

use super::App;

// ─── クランプ境界の定数（マジックナンバー禁止）─────────────────

/// 正規化パラメータ（強さ）の下限。
const NORMALIZED_MIN: f32 = 0.0;
/// 正規化パラメータ（強さ）の上限。
const NORMALIZED_MAX: f32 = 1.0;
/// 半径の下限（m）。
///
/// 0 を許すと `collect_interaction_sources` が落として「値は入るのに何も起きない」
/// 分かりにくい状態になる。1 テクセル（0.125m）より細かくしても場には焼けないため、
/// 場のテクセルサイズを実質的な下限として採用する。
const RADIUS_MIN: f32 =
    crate::engine::core::renderer::interaction::INTERACTION_FIELD_TEXEL_SIZE;

impl App {
    /// インスペクタからの InteractionSourceComponent フィールド更新
    /// （SET_INTERACTION_FIELD IPC）。
    ///
    /// key: radius / strength / enabled。
    /// 不正な key・value は無視する（インスペクタへの再送信も行わない）。
    pub(super) fn handle_set_interaction_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        key:          &str,
        value:        &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（kind 違いのスロットへの誤配は弾く）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::InteractionSource)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(c) = scene.world.get_mut::<InteractionSourceComponent>(entity) else { return };

        match key {
            "radius" => {
                if let Ok(v) = value.parse::<f32>() { c.radius = v.max(RADIUS_MIN); }
            }
            "strength" => {
                if let Ok(v) = value.parse::<f32>() {
                    c.strength = v.clamp(NORMALIZED_MIN, NORMALIZED_MAX);
                }
            }
            "enabled" => {
                if let Some(v) = parse_bool(value) { c.enabled = v; }
            }
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}

// ─── パースヘルパー ──────────────────────────────────────────

/// "true" / "false"（大文字小文字と前後空白を許容）を bool へ。それ以外は None。
///
/// C# の `bool.ToString()` は "True"/"False" を返すため、大文字小文字を無視する必要がある。
fn parse_bool(value: &str) -> Option<bool> {
    match value.trim().to_ascii_lowercase().as_str() {
        "true"  => Some(true),
        "false" => Some(false),
        _       => None,
    }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// C# 由来の "True"/"False" を含めて bool が解釈できること。
    #[test]
    fn parse_bool_accepts_both_casings() {
        assert_eq!(parse_bool("true"),  Some(true));
        assert_eq!(parse_bool("True"),  Some(true));
        assert_eq!(parse_bool(" FALSE "), Some(false));
    }

    /// 不正値は None（値を書き換えない）。
    #[test]
    fn parse_bool_rejects_others() {
        assert_eq!(parse_bool("1"), None);
        assert_eq!(parse_bool(""),  None);
        assert_eq!(parse_bool("yes"), None);
    }

    /// 半径の下限が場のテクセルサイズ（0.125m）であること
    /// （これより細かい半径は場に焼けないため、UI 側で入れても意味がない）。
    #[test]
    fn radius_min_is_one_texel() {
        assert!((RADIUS_MIN - 0.125).abs() < 1e-6);
    }
}
