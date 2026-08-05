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
// 水域ごとの物性（W5.2 / I2.1）のクランプも、波紋シミュレーション本体と同じ関数・
// 定数を使う（別実装にすると「インスペクタでは通るがシミュレーションで丸められる」ずれが出る）。
use crate::engine::interaction::{
    sanitize_ripple_damping_rate, VISCOSITY_MAX, VISCOSITY_MIN,
};

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
/// "x,y,z,w" 形式の要素数（シェーディングアセットのパラメータ値。Phase W8.2）。
const VEC4_COMPONENT_COUNT: usize = 4;
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
    ///      wave_speed / wave_direction_deg（W6.3）/ ripple_strength / ripple_foam_threshold /
    ///      viscosity / ripple_damping（I2.1 水域ごとの物性）/
    ///      fresnel_power / fresnel_strength /
    ///      reflection_intensity / reflection_roughness（W5.2。旧 reflection_color は撤去）/
    ///      refraction_distortion / shore_wave_*（W1.5）/
    ///      river_width / flow_speed / river_depth / spline_points /
    ///      spline_snap_terrain（W4）/
    ///      river_segment_length / control_point_ref（W4.1）/
    ///      simulate_level（W2.5 水位グラフの対象フラグ）/
    ///      surface_shader（W8 水面シェーディングアセットのパス）。
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
            "wave_direction_deg" => {
                // 方位角（度）。負値・360 超もそのまま受け付ける（三角関数の周期性で自然に働く）。
                if let Ok(v) = value.parse::<f32>() { w.wave_direction_deg = v; }
            }
            "fresnel_power" => {
                if let Ok(v) = value.parse::<f32>() { w.fresnel_power = v.max(NON_NEGATIVE_MIN); }
            }
            "fresnel_strength" => {
                if let Ok(v) = value.parse::<f32>() {
                    w.fresnel_strength = v.clamp(NORMALIZED_MIN, NORMALIZED_MAX);
                }
            }
            // ── 反射（Phase W5.2）────────────────────────────────────
            // 旧 "reflection_color"（固定色の簡易反射）は撤去済み。未知 key は
            // この match の `_ => return` で無視されるので、古いエディタから
            // 送られてきても何も起きない（＝壊れない）。
            "reflection_intensity" => {
                // 負の強度は「反射を反転する」という意味を持たないので 0 で下限を切る
                //（0 = この水域の反射を完全に切る＝レイも飛ばさない）。上限は演出用に開けておく。
                if let Ok(v) = value.parse::<f32>() {
                    w.reflection_intensity = v.max(NON_NEGATIVE_MIN);
                }
            }
            "reflection_roughness" => {
                // 粗さは 0..1 の正規化パラメータ（描画側でも同じ範囲へクランプする）。
                if let Ok(v) = value.parse::<f32>() {
                    w.reflection_roughness = v.clamp(NORMALIZED_MIN, NORMALIZED_MAX);
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
            // ── 水域ごとの物性（Phase I2.1）──────────────────────────
            "viscosity" => {
                // 粘度は 0..1 の正規化パラメータ。負の粘度・粘度 2 に物理的な意味は無く、
                // シミュレーション側でも同じ範囲へクランプされる（二重定義を避けて定数を共有）。
                if let Ok(v) = value.parse::<f32>() {
                    w.viscosity = v.clamp(VISCOSITY_MIN, VISCOSITY_MAX);
                }
            }
            "ripple_damping" => {
                // 波紋の減衰率（1/s）。0 だと波紋が永久に消えず、巨大値は非正規化数を生むため、
                // シミュレーション側と**同じ**許容レンジで締める。
                if let Ok(v) = value.parse::<f32>() {
                    w.ripple_damping = sanitize_ripple_damping_rate(v);
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
            "surface_shader" => {
                // 水面シェーディングアセット（.wgsl）のパス（W8）。
                // **空文字列 = 標準へ戻す**（インスペクタの「×」がこれを送る）。
                // 存在しないパスでも `control_point_ref` と同じくそのまま保存する
                //（アセットを後から作る作業を許すため。読めない間は標準で描かれ、
                //  LOAD_ERROR が 1 度だけ通知される）。
                // 前後の空白だけは落とす（D&D と手入力で差が出ないように）。
                w.surface_shader = value.trim().to_string();
            }
            "spline_points" => {
                // **リスト全体の置き換え**（追加・削除・編集のいずれもこの 1 キーで来る）。
                // インデックス指定の部分更新にしないのは、UI 側の行番号と
                // ランタイムの配列がずれる余地を無くすため。
                // 1 点でもパースに失敗したら**丸ごと無視**する（半端な川を作らない）。
                let Some(points) = parse_point_list(value) else { return };
                w.spline_points = points;
            }
            "simulate_level" => {
                // 水位グラフ（W2.5）の対象にするか。C# のチェックボックスは "true"/"false" を送る。
                // OFF に戻したときは**揮発の現在水位も消す**（そうしないと、Play 中に
                // フラグを落とした水域が最後の水位のまま固まって見える）。
                let on = value.eq_ignore_ascii_case("true") || value == "1";
                w.simulate_level = on;
                if !on { w.sim_level_y = None; }
            }
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 水面シェーディングアセットのパラメータ 1 個を更新する
    /// （SET_WATER_SHADER_PARAM IPC。Phase W8.2）。
    ///
    /// `name` はアセットの `override` 宣言の識別子、`value` は `"x,y,z,w"`。
    /// 値は `WaterVolumeComponent::shader_params` へ**そのまま**入る
    /// （型ごとの意味づけ＝どの成分を使うかは WGSL 生成側の責務であり、
    ///   ここで色かスカラーかを判定はしない）。
    ///
    /// ## 宣言の検証をここで行わない理由
    /// アセットは編集中に書き換わる（ホットリロード）ため、「今の宣言に無い名前は
    /// 拒否する」とすると、保存 → アセットを直す → 値が消えている、という順序依存の
    /// 事故が起きる。孤児の値は**描画側が無視する**（`shade_params::build_block`）ので、
    /// 保存しておいても害が無く、アセットを戻せば値も戻る。
    ///
    /// 不正な value（4 成分でない・数値でない）は無視する。
    pub(super) fn handle_set_water_shader_param(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        name:         &str,
        value:        &str,
    ) {
        use super::find_actor_by_dfs;

        // 空の名前はキーとして成立しない（マップが壊れる）。
        let key = name.trim();
        if key.is_empty() { return; }
        let Some(v) = parse_vec4(value) else { return };

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_water_field と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                else { return };
            actor.slots().get(slot_idx as usize)
                .filter(|s| s.kind == ComponentKind::WaterVolume)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };

        let Some(scene) = &mut self.scene else { return };
        let Some(w) = scene.world.get_mut::<WaterVolumeComponent>(entity) else { return };
        w.shader_params.insert(key.to_string(), v);

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 水面シェーディングアセットのパラメータ 1 個をアセットの既定値へ戻す
    /// （RESET_WATER_SHADER_PARAM IPC。`@reset` 属性の付いた行のボタン）。
    ///
    /// ## 「既定値を書き込む」ではなく「上書き値を消す」理由
    /// `shader_params` は**上書きだけを持つ差分**であり、値が無い名前は
    /// 描画・インスペクタとも自動的にアセットの既定値へ落ちる
    /// （`shade_params::build_block` / `params_json`）。
    /// 消す実装にしておくと、後からアセット側の既定値を書き換えたときに
    /// 「戻した水域」は新しい既定値へ追随する（既定値を焼き込むと追随しない）。
    ///
    /// Undo は `field_edit.rs` の共通機構が担当する（このコマンドは
    /// `field_edit_target` で `Slot` に分類されており、実行前のコンポーネント全体が
    /// スナップショットされる）。ここに Undo 記録を書いてはならない（二重記録になる）。
    ///
    /// 元々値を持っていなかった名前（＝既に既定値）へのリセットは**何もしない**
    /// （再送信もしないので、無意味な SCENE_MODIFIED で「未保存」印が付かない）。
    pub(super) fn handle_reset_water_shader_param(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        name:         &str,
    ) {
        use super::find_actor_by_dfs;

        let key = name.trim();
        if key.is_empty() { return; }

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_water_shader_param と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                else { return };
            actor.slots().get(slot_idx as usize)
                .filter(|s| s.kind == ComponentKind::WaterVolume)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };

        let Some(scene) = &mut self.scene else { return };
        let Some(w) = scene.world.get_mut::<WaterVolumeComponent>(entity) else { return };
        if w.shader_params.remove(key).is_none() { return; }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// `@ref` パラメータ 1 個のバインド先を設定・解除する
    /// （SET_WATER_SHADER_BINDING IPC。Phase W8.3）。
    ///
    /// `binding` が空文字列なら**解除**（キーごと消す）。
    /// 解除しても `shader_params` の保存値には触れないので、
    /// バインド前に手で入れた値がそのまま復活する。
    ///
    /// ## 妥当性の検証をここで行わない理由
    /// `handle_set_water_shader_param` と同じで、アセットは編集中に書き換わる。
    /// 「今の宣言に `@ref` が無い名前は拒否する」とすると、
    /// バインドを張る → アセットを直す → バインドが消えている、という
    /// 順序依存の事故が起きる。孤児のバインドは**解決側が無視する**
    /// （`water::bindings::resolve_bindings`）ので、持っていても害が無い。
    ///
    /// ただし**文字列として壊れているバインドは受け付けない**
    /// （3 要素に割れないものは、保存しても永久に解決できないゴミにしかならない）。
    pub(super) fn handle_set_water_shader_binding(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        name:         &str,
        binding:      &str,
    ) {
        use super::find_actor_by_dfs;
        use crate::engine::binding::resolve::parse_binding;

        // 空の名前はキーとして成立しない（マップが壊れる）。
        let key = name.trim();
        if key.is_empty() { return; }
        let binding = binding.trim();
        // 空 = 解除。それ以外は 3 要素へ割れることを確認する。
        if !binding.is_empty() && parse_binding(binding).is_none() { return; }

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（handle_set_water_shader_param と同流儀）。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                else { return };
            actor.slots().get(slot_idx as usize)
                .filter(|s| s.kind == ComponentKind::WaterVolume)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };

        let Some(scene) = &mut self.scene else { return };
        let Some(w) = scene.world.get_mut::<WaterVolumeComponent>(entity) else { return };
        let changed = if binding.is_empty() {
            w.bindings.remove(key).is_some()
        } else {
            w.bindings.insert(key.to_string(), binding.to_string()).as_deref() != Some(binding)
        };
        // 何も変わっていないなら再送も SCENE_MODIFIED も出さない
        //（無意味な「未保存」印を付けないため）。
        if !changed { return; }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 指定アクタが供給できるバインド元の候補を返す
    /// （GET_BINDABLE_SOURCES IPC。Phase W8.3）。
    ///
    /// 応答は `BINDABLE_SOURCES:{json}`:
    /// ```json
    /// [{"slot":"MainLight","label":"Light","variables":[{"name":"intensity","label":"強度"}]}]
    /// ```
    /// - `slot`      … バインド文字列の 2 要素目に入る値（スロット名。Transform は "Transform"）
    /// - `label`     … 1 ページ目に出す表示名（コンポーネント種別）
    /// - `variables` … 2 ページ目に出す変数一覧（要求型に**厳密一致**するものだけ）
    ///
    /// **供給値の正典は Rust 側**（`engine::binding::catalog` の表と、
    /// スクリプトの `[SerializeField, Bindable]` フィールド）であり、
    /// エディタはこの応答を並べるだけでよい（ミラー表を持たない）。
    ///
    /// 変数が 1 つも無いスロットは**候補に出さない**（選んでも進めない行を作らない）。
    ///
    /// ## スクリプトの候補が出ない場合
    /// スクリプトのコンパイルに失敗している（CLR インスタンスではなく
    /// プレースホルダになっている）と、`[Bindable]` を問い合わせる相手が居ないため
    /// そのスロットは候補に出ない。まずスクリプトのコンパイルエラーを直すこと。
    pub(super) fn handle_get_bindable_sources(&self, actor_dfs_id: u32, value_type: &str) {
        use super::find_actor_by_dfs;
        use crate::engine::binding::catalog::{self, BindableHost, BindableValueType};
        use crate::engine::components::script_component::ScriptComponent;
        use crate::engine::components::Transform;

        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };
        // 未知の型名は候補ゼロ（＝空配列）で返す。無応答にすると UI が待ち続ける。
        let Some(want) = BindableValueType::from_str(value_type.trim()) else {
            ipc.send("BINDABLE_SOURCES:[]");
            return;
        };

        let wl = self.active_world_line;
        let mut c = 0u32;
        let Some(actor) = find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) else {
            ipc.send("BINDABLE_SOURCES:[]");
            return;
        };

        /// 応答 1 件（1 スロットぶん）を組み立てる小道具。
        fn entry(slot: &str, label: &str, vars: Vec<serde_json::Value>) -> serde_json::Value {
            serde_json::json!({ "slot": slot, "label": label, "variables": vars })
        }

        let mut out: Vec<serde_json::Value> = Vec::new();

        // ── ① アクタのルート直付け（Transform）────────────────
        //     スロット一覧に現れないので先に見る。スロット名は表の表示名をそのまま使う
        //     （解決側 `resolve_binding` と同じ規約）。
        if scene.world.get::<Transform>(actor.entity).is_some() {
            let vars: Vec<serde_json::Value> =
                catalog::variables_for(BindableHost::ActorRoot, want)
                    .map(|v| serde_json::json!({ "name": v.variable, "label": v.variable_label }))
                    .collect();
            if let Some(first) = catalog::variables_for(BindableHost::ActorRoot, want).next()
                && !vars.is_empty()
            {
                out.push(entry(first.component_label, first.component_label, vars));
            }
        }

        // ── ② スロット（有効なもののみ）──────────────────────
        for slot in actor.slots().iter().filter(|s| s.enabled) {
            let vars: Vec<serde_json::Value> = if slot.kind == ComponentKind::Script {
                // スクリプトは C# 側が正典。[SerializeField] の各フィールドへ
                // 実際に読み取りを試し、**要求型と成分数が一致したものだけ**を候補にする
                //（＝[Bindable] の検証も型判定も CLR 側の 1 か所で完結する）。
                let Some(sc) = scene.world.get::<ScriptComponent>(slot.entity) else { continue };
                sc.fields.keys()
                    .filter(|name| sc.read_bindable_field(name, want.components()).is_some())
                    .map(|name| serde_json::json!({ "name": name, "label": name }))
                    .collect()
            } else {
                catalog::variables_for(BindableHost::Slot(slot.kind), want)
                    .map(|v| serde_json::json!({ "name": v.variable, "label": v.variable_label }))
                    .collect()
            };
            if vars.is_empty() { continue; }

            // 1 ページ目の表示名。組込はカタログの表示名、スクリプトはスロット名そのもの。
            let label = catalog::variables_for(BindableHost::Slot(slot.kind), want)
                .next()
                .map(|v| v.component_label.to_string())
                .unwrap_or_else(|| slot.name.clone());
            out.push(entry(&slot.name, &label, vars));
        }

        let json = serde_json::to_string(&out).unwrap_or_else(|_| "[]".to_string());
        ipc.send(&format!("BINDABLE_SOURCES:{json}"));
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

/// "x,y,z,w" 形式の文字列を [f32; 4] へパースする（Phase W8.2）。
///
/// シェーディングアセットのパラメータは型に依らず**常に 4 成分**で運ぶ
/// （色は xyz、スカラーは x のみ意味を持つ）。成分数を型で変えないのは、
/// アセットの宣言が変わっても IPC の形が変わらないようにするためである。
fn parse_vec4(value: &str) -> Option<[f32; 4]> {
    let parts: Vec<&str> = value.split(',').collect();
    if parts.len() != VEC4_COMPONENT_COUNT { return None; }
    Some([
        parts[0].trim().parse::<f32>().ok()?,
        parts[1].trim().parse::<f32>().ok()?,
        parts[2].trim().parse::<f32>().ok()?,
        parts[3].trim().parse::<f32>().ok()?,
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

    /// シェーディングアセットのパラメータ値（"x,y,z,w"）がパースできること。
    #[test]
    fn parse_vec4_accepts_four_components() {
        assert_eq!(parse_vec4("1,2,3,4"), Some([1.0, 2.0, 3.0, 4.0]));
        assert_eq!(parse_vec4(" 0.5 , -1.5 , 2 , 0 "), Some([0.5, -1.5, 2.0, 0.0]));
    }

    /// 成分数違い・非数値は None（＝値を書き換えない）になること。
    #[test]
    fn parse_vec4_rejects_malformed() {
        assert_eq!(parse_vec4("1,2,3"), None,     "3 成分（vec3 の形）は受け付けない");
        assert_eq!(parse_vec4("1,2,3,4,5"), None, "成分過多");
        assert_eq!(parse_vec4("1,2,x,4"), None,   "非数値");
        assert_eq!(parse_vec4(""), None,          "空文字列");
    }

    /// 下限クランプが 3 成分すべてに掛かること。
    #[test]
    fn clamp_vec3_min_applies_to_all_channels() {
        assert_eq!(clamp_vec3_min([-1.0, 0.5, -0.001], COLOR_CHANNEL_MIN), [0.0, 0.5, 0.0]);
        assert_eq!(clamp_vec3_min([1.0, 2.0, 3.0], NON_NEGATIVE_MIN), [1.0, 2.0, 3.0]);
    }
}

// ============================================================
//  契約テスト: エディタの値域表 ⇔ ランタイムの clamp
// ============================================================

/// エディタ（C#）の値域表 `ComponentFieldRanges` と、このファイルの clamp が
/// **食い違わない**ことをビルド時のソース走査で固定する。
///
/// ## なぜ要るか
/// 値域表はインスペクタの行を「スライダーにするか数値入力にするか」を決める。
/// ランタイムの clamp を後から変えても C# 側は何も壊れずに動いてしまうため、
/// ずれは「スライダーの端が効かない」「本来動かせる値に手が届かない」という
/// 気付きにくい不具合として残る。ソース同士を突き合わせて落とすのが唯一の防波堤。
///
/// ## 走査の方針
/// - `include_str!` で両ソースを読み、**`lines()` ベース**で走査する
///   （CRLF で実体化されるリポジトリなので、生の `"\n"` 前提の split は使わない）。
/// - 走査が空振りして「常に成功するテスト」にならないよう、最低件数を assert する。
#[cfg(test)]
mod field_range_contract {
    /// エディタの値域表（C# ソース）。
    /// パスはこのファイル（runtime/src/engine/core/app_base/app/）からの相対。
    const RANGES_CS: &str =
        include_str!("../../../../../../editor/src/Controls/ComponentFieldRanges.cs");
    /// このファイル自身（clamp の正典）。
    const WATER_OPS_RS: &str = include_str!("water_ops.rs");
    /// 粘度の許容レンジ定数の定義元。
    const WATER_PHYSICS_RS: &str = include_str!("../../../interaction/water_physics.rs");

    /// 値域表のキーのうち、この契約テストが対象にするコンポーネント。
    const WATER_KEY_PREFIX: &str = "WaterVolumeComponent.";
    /// 0..1 の正規化パラメータを表す C# 側の表記。
    const NORMALIZED_RANGE_CS: &str = "(0f, 1f)";

    /// 「0..1 へ clamp する」と読める定数対（water_ops.rs のクランプ表記）。
    /// 粘度だけは波紋シミュレーション側と定数を共有しているため別名になる。
    /// どちらも実値が 0.0 / 1.0 であることは
    /// `viscosity_range_constants_are_zero_to_one` が別途固定する。
    const NORMALIZED_CLAMP_EXPRESSIONS: [&str; 2] = [
        "clamp(NORMALIZED_MIN, NORMALIZED_MAX)",
        "clamp(VISCOSITY_MIN, VISCOSITY_MAX)",
    ];

    /// 値域表から (キー, 値域の C# 表記) を取り出す。
    ///
    /// 対象行は `["Type.field"] = (min, max),` の形だけ。コメント行は除く。
    fn parse_ranges_table() -> Vec<(String, String)> {
        let mut out = Vec::new();
        for line in RANGES_CS.lines() {
            let t = line.trim();
            if t.starts_with("//") || !t.starts_with("[\"") {
                continue;
            }
            // キー: 最初の `"` 〜 次の `"`。
            let Some(rest) = t.strip_prefix("[\"") else { continue };
            let Some(quote_end) = rest.find('"') else { continue };
            let key = rest[..quote_end].to_string();
            // 値域: `=` の右側から末尾のカンマを落とす。
            let Some(eq) = t.find('=') else { continue };
            let value = t[eq + 1..].trim().trim_end_matches(',').trim().to_string();
            out.push((key, value));
        }
        out
    }

    /// water_ops.rs の `handle_set_water_field` から、
    /// 「0..1 へ clamp している SET キー」の一覧を取り出す。
    ///
    /// `"key" => {` の行で対象キーを覚え、続く行に clamp 表記が現れたら採用する
    /// （コメント行は無視するので、doc コメント中の key 列挙には反応しない）。
    ///
    /// 走査範囲は **`handle_set_water_field` の match の中だけ**に限る。
    /// ファイル全体を舐めると、このテストモジュール自身が持つ
    /// `NORMALIZED_CLAMP_EXPRESSIONS` の文字列リテラルまで拾ってしまうため。
    fn parse_normalized_clamped_keys() -> Vec<String> {
        /// 走査を開始する行（このハンドラの定義）。
        const SCAN_BEGIN: &str = "pub(super) fn handle_set_water_field(";
        /// 走査を終える行（key の match の既定腕＝表の終わり）。
        const SCAN_END: &str = "_ => return,";

        let mut out: Vec<String> = Vec::new();
        let mut current: Option<String> = None;
        let mut started = false;
        for line in WATER_OPS_RS.lines() {
            let t = line.trim();
            if !started {
                started = t.starts_with(SCAN_BEGIN);
                continue;
            }
            if t == SCAN_END {
                break;
            }
            if t.starts_with("//") {
                continue;
            }
            // `"key" => {` / `"key" | "key2" => {` の形からキーを拾う。
            if t.starts_with('"') && t.contains("=>") {
                let name: String = t[1..].chars().take_while(|c| *c != '"').collect();
                if !name.is_empty() {
                    current = Some(name);
                }
                continue;
            }
            if NORMALIZED_CLAMP_EXPRESSIONS.iter().any(|e| t.contains(e)) {
                if let Some(key) = &current {
                    if !out.contains(key) {
                        out.push(key.clone());
                    }
                }
            }
        }
        out
    }

    /// (a) 値域表に 0..1 として載っている水のフィールドは、
    ///     water_ops.rs で実際に 0..1 へ clamp されていること。
    #[test]
    fn table_entries_are_clamped_in_runtime() {
        let table   = parse_ranges_table();
        let clamped = parse_normalized_clamped_keys();

        let mut checked = 0usize;
        let mut missing = Vec::new();
        for (key, range) in &table {
            let Some(field) = key.strip_prefix(WATER_KEY_PREFIX) else { continue };
            if range != NORMALIZED_RANGE_CS {
                continue;
            }
            checked += 1;
            if !clamped.iter().any(|k| k == field) {
                missing.push(field.to_string());
            }
        }
        assert!(
            missing.is_empty(),
            "値域表が 0..1 としているのに water_ops.rs で 0..1 へ clamp していない: {missing:?}"
        );
        // 走査が空振りしていないことの担保（現状 6 件: surface_opacity / foam_intensity /
        // fresnel_strength / reflection_roughness / shore_wave_foam / viscosity）。
        assert!(
            checked >= 6,
            "値域表の走査が機能していない（水の 0..1 エントリが {checked} 件しか見つからない）"
        );
    }

    /// (b) water_ops.rs で 0..1 へ clamp しているフィールドは、
    ///     値域表にも 0..1 として載っていること（載せ漏れ検出）。
    #[test]
    fn runtime_clamps_are_listed_in_table() {
        let table   = parse_ranges_table();
        let clamped = parse_normalized_clamped_keys();

        let mut missing = Vec::new();
        for field in &clamped {
            let key = format!("{WATER_KEY_PREFIX}{field}");
            let listed = table
                .iter()
                .any(|(k, range)| k == &key && range == NORMALIZED_RANGE_CS);
            if !listed {
                missing.push(field.clone());
            }
        }
        assert!(
            missing.is_empty(),
            "water_ops.rs が 0..1 へ clamp しているのに値域表へ載っていない\
             （インスペクタがスライダーにならない）: {missing:?}"
        );
        // 走査が空振りしていないことの担保。
        assert!(
            clamped.len() >= 6,
            "clamp の走査が機能していない（0..1 クランプが {} 件しか見つからない）",
            clamped.len()
        );
    }

    /// 粘度の定数対が本当に 0.0 / 1.0 であること。
    ///
    /// (a)(b) は `clamp(VISCOSITY_MIN, VISCOSITY_MAX)` を「0..1 の clamp」として
    /// 扱うため、定数側が動いたらこのテストが落ちて気付けるようにしておく。
    #[test]
    fn viscosity_range_constants_are_zero_to_one() {
        /// 定数の宣言行から `= 値;` の値部分を取り出す。
        fn const_value(src: &str, name: &str) -> Option<String> {
            let decl = format!("pub const {name}: f32 =");
            src.lines()
                .map(str::trim)
                .find(|l| l.starts_with(&decl))
                .and_then(|l| l.split('=').nth(1))
                .map(|v| v.trim().trim_end_matches(';').trim().to_string())
        }
        assert_eq!(
            const_value(WATER_PHYSICS_RS, "VISCOSITY_MIN").as_deref(),
            Some("0.0"),
            "VISCOSITY_MIN が 0.0 でなくなった（値域表の 0..1 と食い違う）"
        );
        assert_eq!(
            const_value(WATER_PHYSICS_RS, "VISCOSITY_MAX").as_deref(),
            Some("1.0"),
            "VISCOSITY_MAX が 1.0 でなくなった（値域表の 0..1 と食い違う）"
        );
    }
}
