// ============================================================
//  cover_emitter_ops.rs — CoverEmitterComponent のインスペクタ更新（Phase I3.1）
//
//  ・handle_set_cover_field: インスペクタ（C#）からの SET_COVER_FIELD IPC を受けて
//    `CoverEmitterComponent` のフィールドを更新する
//    （interaction_ops.rs / water_ops.rs と同流儀）。
//
//  値の解釈規則:
//    ・range_kind   … "Global" / "Region" / "TextureMask"（未知の文字列は無視）
//    ・extents_*    … 直方体の各軸半径（m）。0 以下は「その軸に広がらない」＝
//                     範囲が消えて分かりにくいので下限でクランプする
//    ・fade         … 境界フェード幅（m）。負値は 0（硬い境界）へ
//    ・mask_path    … 文字列そのまま（空 = マスク無効）
//    ・mask_size_*  … マスク矩形の XZ サイズ（m）。下限クランプ
//    ・material_id  … 文字列そのまま（未定義 ID は積算時に無視される）
//    ・strength     … 量/秒。負値は 0 へ（自然減衰は本フェーズのスコープ外）
//    ・enabled      … "true" / "false"
//
//  【積算への反映】
//    値を変えても、その場ではカバー場は変化しない（積算はシミュレート実行中と
//    Play 中にしか進まない）。これは意図した設計であり、
//    「パラメータをいじるたびに地形が勝手に雪化する」事故を防いでいる。
// ============================================================

use crate::engine::components::cover_emitter_component::{
    CoverEmitterComponent, CoverEmitterRangeKind,
};
use crate::engine::components::ComponentKind;

use super::App;

// ─── クランプ境界の定数（マジックナンバー禁止）─────────────────

/// 範囲半径・マスクサイズの下限（メートル）。
///
/// カバー場のテクセルは既定 0.5m 刻みなので、これより小さい範囲は
/// 「値は入るのに 1 テクセルにも当たらない」分かりにくい状態になる。
/// カバー場の実効解像度を実質的な下限として採用する。
const EXTENT_MIN: f32 = 0.01;

/// 境界フェード幅の下限（メートル）。0 = 硬い境界。
const FADE_MIN: f32 = 0.0;

/// 強度（量/秒）の下限。負の強度＝自然減衰は本フェーズのスコープ外。
const STRENGTH_MIN: f32 = 0.0;

impl App {
    /// インスペクタからの CoverEmitterComponent フィールド更新（SET_COVER_FIELD IPC）。
    ///
    /// 不正な key・value は無視する（インスペクタへの再送信も行わない）。
    pub(super) fn handle_set_cover_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        key: &str,
        value: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（kind 違いのスロットへの誤配は弾く）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::CoverEmitter)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(c) = scene.world.get_mut::<CoverEmitterComponent>(entity) else { return };

        match key {
            "range_kind" => {
                let Some(k) = CoverEmitterRangeKind::from_str_opt(value.trim()) else { return };
                c.range_kind = k;
            }
            "extents_x" | "extents_y" | "extents_z" => {
                let Ok(v) = value.parse::<f32>() else { return };
                if !v.is_finite() {
                    return;
                }
                let axis = match key {
                    "extents_x" => 0,
                    "extents_y" => 1,
                    _ => 2,
                };
                c.extents[axis] = v.max(EXTENT_MIN);
            }
            "fade" => {
                let Ok(v) = value.parse::<f32>() else { return };
                if !v.is_finite() {
                    return;
                }
                c.fade = v.max(FADE_MIN);
            }
            "mask_path" => {
                c.mask_path = value.to_string();
                // マスク画像を差し替えたので、次の積算でロードし直させる。
                self.terrain.mask_cache.clear();
                self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                if let Some(ipc) = &self.ipc {
                    ipc.send("SCENE_MODIFIED");
                }
                return;
            }
            "mask_size_x" | "mask_size_z" => {
                let Ok(v) = value.parse::<f32>() else { return };
                if !v.is_finite() {
                    return;
                }
                let axis = if key == "mask_size_x" { 0 } else { 1 };
                c.mask_size[axis] = v.max(EXTENT_MIN);
            }
            "material_id" => {
                c.material_id = value.trim().to_string();
            }
            "strength" => {
                let Ok(v) = value.parse::<f32>() else { return };
                if !v.is_finite() {
                    return;
                }
                c.strength = v.max(STRENGTH_MIN);
            }
            "enabled" => {
                let Some(v) = parse_bool(value) else { return };
                c.enabled = v;
            }
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }
}

// ─── パースヘルパー ──────────────────────────────────────────

/// "true" / "false"（大文字小文字と前後空白を許容）を bool へ。それ以外は None。
///
/// C# の `bool.ToString()` は "True"/"False" を返すため、大文字小文字を無視する必要がある。
fn parse_bool(value: &str) -> Option<bool> {
    match value.trim().to_ascii_lowercase().as_str() {
        "true" => Some(true),
        "false" => Some(false),
        _ => None,
    }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// C# 由来の "True"/"False" を含めて bool が解釈できること。
    #[test]
    fn parse_bool_accepts_both_casings() {
        assert_eq!(parse_bool("true"), Some(true));
        assert_eq!(parse_bool("True"), Some(true));
        assert_eq!(parse_bool(" FALSE "), Some(false));
        assert_eq!(parse_bool("1"), None);
    }

    /// 範囲種別の文字列が C# ドロップダウンの Tag と一致すること。
    #[test]
    fn range_kind_strings_are_stable() {
        assert_eq!(CoverEmitterRangeKind::Global.as_str(), "Global");
        assert_eq!(CoverEmitterRangeKind::Region.as_str(), "Region");
        assert_eq!(CoverEmitterRangeKind::TextureMask.as_str(), "TextureMask");
    }
}
