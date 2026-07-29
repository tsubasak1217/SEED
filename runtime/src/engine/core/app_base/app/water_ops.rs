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
// 岸波（W1.5）の下限は描画側（GPU パラメータ生成）と同じ定数を使う。
// 二重定義すると「インスペクタでは通るが描画側で切られる」ずれが起きる。
use crate::engine::core::renderer::water::params::{
    SHORE_WAVE_LENGTH_MIN, SHORE_WAVE_PERIOD_MIN,
};
// 川幅の下限も同じ理由でエンジン層（スプライン幾何）の定数を共有する。
use crate::engine::water::{RIVER_SEGMENT_LENGTH_MIN, RIVER_WIDTH_MIN};

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
/// 川の制御点リスト（"x,y,z;x,y,z;..."）の点区切り文字。
const POINT_LIST_SEPARATOR: char = ';';
/// 地形スナップのカラム走査で、地形の上下端へ足す余白（m）。
/// 境界ちょうどから走査を始めると AIR→SOLID の遷移を拾い損ねることがある。
const SNAP_SCAN_MARGIN_M: f32 = 1.0;

impl App {
    /// インスペクタからの WaterVolumeComponent フィールド更新（SET_WATER_FIELD IPC）。
    ///
    /// key: kind / surface_height / region_half_extents / ocean_extent /
    ///      shallow_color / deep_color / absorption_distance / surface_opacity /
    ///      foam_color / foam_width / foam_intensity / wave_amplitude / wave_scale /
    ///      wave_speed / ripple_strength / ripple_foam_threshold /
    ///      fresnel_power / fresnel_strength / reflection_color /
    ///      refraction_distortion / shore_wave_*（W1.5）/
    ///      river_width / flow_speed / river_depth / spline_points /
    ///      spline_snap_terrain（W4）/
    ///      river_segment_length / control_point_ref（W4.1）。
    /// ベクタ系（region_half_extents / *_color）は "x,y,z" 形式。
    /// 川の制御点 `spline_points` は "x,y,z;x,y,z;..." で**リスト全体**を置き換える。
    /// `spline_snap_terrain` は値をオフセット Y（m）として制御点を地形へ落とす。
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
        // 併せてアクタのワールド位置も取っておく（川の制御点はアクタ相対なので、
        // 地形スナップでワールド座標へ出すのに要る）。
        let (slot_entity, actor_pos) = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                else { return };
            let entity = actor.slots().get(slot_idx as usize)
                .filter(|s| s.kind == ComponentKind::WaterVolume)
                .map(|s| s.entity);
            let pos = scene.world.get::<crate::engine::components::Transform>(actor.entity)
                .map(|t| t.position)
                .unwrap_or([0.0, 0.0, 0.0]);
            (entity, pos)
        };
        let Some(entity) = slot_entity else { return };

        // 「制御点を地形へスナップ」（W4）だけは地形データ（App 側）を読むため、
        // コンポーネントを可変借用する前に片付ける。
        if key == "spline_snap_terrain" {
            let Ok(offset_y) = value.parse::<f32>() else { return };
            if !self.snap_water_spline_to_terrain(entity, actor_pos, offset_y) { return; }
            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            return;
        }

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
            // ── 岸波（Phase W1.5）──────────────────────────────────
            "shore_wave_strength" => {
                // 0 で完全無効（W1 と同一出力）。負値は波を逆位相にするだけなので 0 で切る。
                if let Ok(v) = value.parse::<f32>() {
                    w.shore_wave_strength = v.max(NON_NEGATIVE_MIN);
                }
            }
            "shore_wave_length" => {
                // 波長 0 は位相が発散する。描画側と同じ下限で締める。
                if let Ok(v) = value.parse::<f32>() {
                    w.shore_wave_length = v.max(SHORE_WAVE_LENGTH_MIN);
                }
            }
            "shore_wave_period" => {
                // 周期 0 も同様に発散する。
                if let Ok(v) = value.parse::<f32>() {
                    w.shore_wave_period = v.max(SHORE_WAVE_PERIOD_MIN);
                }
            }
            "shore_wave_foam" => {
                if let Ok(v) = value.parse::<f32>() {
                    w.shore_wave_foam = v.clamp(NORMALIZED_MIN, NORMALIZED_MAX);
                }
            }
            // ── 川（Phase W4）────────────────────────────────────
            "river_width" => {
                // 幅 0 のリボンは面積を持たず判定も常に外れるため、描画側と同じ下限で締める。
                if let Ok(v) = value.parse::<f32>() { w.river_width = v.max(RIVER_WIDTH_MIN); }
            }
            "flow_speed" => {
                // 逆流（上流へ流す演出）を許すため負値も受け付ける。
                if let Ok(v) = value.parse::<f32>() { w.flow_speed = v; }
            }
            "river_depth" => {
                // 深さ 0 は「水面だけで中身の無い川」になり水中判定が成立しないが、
                // それ自体は破綻ではない（＝流されるだけの薄い水）。負値だけ弾く。
                if let Ok(v) = value.parse::<f32>() { w.river_depth = v.max(NON_NEGATIVE_MIN); }
            }
            "river_segment_length" => {
                // 分割 1 つぶんの目標長（m。W4.1）。0 や負値で分割数が発散しないよう下限で締める。
                if let Ok(v) = value.parse::<f32>() {
                    w.river_segment_length = v.max(RIVER_SEGMENT_LENGTH_MIN);
                }
            }
            "control_point_ref" => {
                // 参照先アクタ名（W4.1）。空文字列 = 参照解除（spline_points 経路へ戻る）。
                // 存在しない名前でも**そのまま保存する**（保存 → アクタ生成の順で
                // 組み立てる作業を許すため。解決できない間は spline_points が使われる）。
                // 前後の空白だけは落とす（D&D と手入力で差が出ないように）。
                w.control_point_ref = value.trim().to_string();
            }
            "spline_points" => {
                // **リスト全体の置き換え**（追加・削除・編集のいずれもこの 1 キーで来る）。
                // インデックス指定の部分更新にしないのは、UI 側の行番号と
                // ランタイムの配列がずれる余地を無くすため。
                // 1 点でもパースに失敗したら**丸ごと無視**する（半端な川を作らない）。
                let Some(points) = parse_point_list(value) else { return };
                w.spline_points = points;
            }
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 川の制御点をすべて「地形高さ + オフセット」の Y へ合わせる（Phase W4）。
    ///
    /// 川は地形の谷筋に沿って引くものなので、XZ だけ置いてから一括で
    /// 地面へ落とせないと実用にならない。地形高さの取得は
    /// `terrain::scatter::generate::surface_hit_down`（散布プロップの接地判定・
    /// 岸波のショアフィールドと**同一関数**）を使う。別実装にすると
    /// 「草は生えているのに川底が浮く」ようなずれが出る。
    ///
    /// 地形の当たらなかった制御点（地形の外・空洞の上）は**元の Y のまま残す**
    /// （0 に落とすと川が地面を突き抜けて跳ねるため）。
    ///
    /// 戻り値 `false` は「何も変更しなかった」で、呼び出し側は再送信もしない。
    fn snap_water_spline_to_terrain(
        &mut self,
        slot_entity: crate::engine::ecs::Entity,
        actor_pos:   [f32; 3],
        offset_y:    f32,
    ) -> bool {
        use crate::engine::terrain::scatter::generate::surface_hit_down;
        use super::terrain_scatter_ops::TerrainScatterField;

        // ── ① 対象の制御点と相対水位を読み出す（可変借用の前に済ませる）──
        let (points, surface_height) = {
            let Some(scene) = &self.scene else { return false };
            let Some(w) = scene.world.get::<WaterVolumeComponent>(slot_entity)
                else { return false };
            (w.spline_points.clone(), w.surface_height)
        };
        if points.is_empty() { return false; }

        // ── ② 地形チャンクの Y 範囲（カラム走査の開始・終了高さ）を求める ──
        //     チャンクが 1 つも無ければ地形高さは定義できない。
        if self.terrain.chunks.is_empty() { return false; }
        let extent = self.terrain.settings.chunk_extent();
        let mut min_y = f32::INFINITY;
        let mut max_y = f32::NEG_INFINITY;
        for coord in self.terrain.chunks.keys() {
            let o = coord.world_origin(&self.terrain.settings);
            min_y = min_y.min(o[1]);
            max_y = max_y.max(o[1] + extent);
        }
        // 走査は地形の最上端より少し上から始める（境界ちょうどだと
        // 開始点が既に SOLID 側に入っていて遷移を拾い損ねることがある）。
        let y_top    = max_y + SNAP_SCAN_MARGIN_M;
        let y_bottom = min_y - SNAP_SCAN_MARGIN_M;

        // ── ③ 制御点ごとにカラム走査して Y を差し替える ──
        //     制御点の世界での水面 Y は「アクタ Y + 制御点 Y + surface_height」なので、
        //     目標（地形 Y + オフセット）から逆算して制御点 Y を決める。
        let mut snapped = points.clone();
        let mut changed = false;
        {
            let field = TerrainScatterField::from_state(&self.terrain);
            for p in snapped.iter_mut() {
                let wx = actor_pos[0] + p[0];
                let wz = actor_pos[2] + p[2];
                let Some((hit, _normal)) = surface_hit_down(&field, wx, wz, y_top, y_bottom)
                    else { continue };
                let want = hit[1] + offset_y - actor_pos[1] - surface_height;
                if (want - p[1]).abs() > f32::EPSILON {
                    p[1] = want;
                    changed = true;
                }
            }
        }
        if !changed { return false; }

        // ── ④ 書き戻す ──
        let Some(scene) = &mut self.scene else { return false };
        let Some(w) = scene.world.get_mut::<WaterVolumeComponent>(slot_entity)
            else { return false };
        w.spline_points = snapped;
        true
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

/// 川の制御点リスト `"x,y,z;x,y,z;..."` をパースする（Phase W4）。
///
/// 空文字列は「制御点なし」（空 Vec）として成功扱いにする
/// （＝インスペクタから全削除できる）。
/// 1 点でも壊れていれば **None**（呼び出し側は丸ごと無視する。
/// 半端に適用すると川の形が壊れたまま保存されてしまうため）。
fn parse_point_list(value: &str) -> Option<Vec<[f32; 3]>> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return Some(Vec::new());
    }
    let mut out = Vec::new();
    for chunk in trimmed.split(POINT_LIST_SEPARATOR) {
        // 末尾のセミコロンなどで生じる空要素は読み飛ばす（区切りの揺れに強くする）。
        if chunk.trim().is_empty() { continue; }
        out.push(parse_vec3(chunk)?);
    }
    Some(out)
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

    /// 川の制御点リストがパースできること（末尾セミコロン・空白入りも許容）。
    #[test]
    fn parse_point_list_accepts_valid_lists() {
        assert_eq!(parse_point_list("0,0,0;1,2,3"),
            Some(vec![[0.0, 0.0, 0.0], [1.0, 2.0, 3.0]]));
        assert_eq!(parse_point_list(" 1,2,3 ; 4,5,6 ; "),
            Some(vec![[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]));
    }

    /// 空文字列は「制御点なし」として成功する（インスペクタからの全削除）。
    #[test]
    fn parse_point_list_accepts_empty_as_clear() {
        assert_eq!(parse_point_list(""), Some(Vec::new()));
        assert_eq!(parse_point_list("   "), Some(Vec::new()));
    }

    /// 1 点でも壊れていればリスト全体を拒否する（半端な川を作らない）。
    #[test]
    fn parse_point_list_rejects_partially_malformed() {
        assert_eq!(parse_point_list("0,0,0;1,2"), None,      "要素不足の点がある");
        assert_eq!(parse_point_list("0,0,0;a,b,c"), None,    "非数値の点がある");
        assert_eq!(parse_point_list("0,0,0,0"), None,        "要素過多");
    }

    /// 下限クランプが 3 成分すべてに掛かること。
    #[test]
    fn clamp_vec3_min_applies_to_all_channels() {
        assert_eq!(clamp_vec3_min([-1.0, 0.5, -0.001], COLOR_CHANNEL_MIN), [0.0, 0.5, 0.0]);
        assert_eq!(clamp_vec3_min([1.0, 2.0, 3.0], NON_NEGATIVE_MIN), [1.0, 2.0, 3.0]);
    }
}
