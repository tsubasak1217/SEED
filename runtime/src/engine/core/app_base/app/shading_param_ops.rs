// ============================================================
//  shading_param_ops.rs — L3 シェーディングアセットのパラメータ編集（IPC ハンドラ）
//
//  ## 役割（単一責任）
//  カメラ添付／シーン添付のシェーディングアセットが宣言した `override` パラメータについて、
//  「値の上書き」「既定値へ戻す」「`@ref` バインドの設定・解除」の 3 操作と、
//  シーン設定ウィンドウ向けの一覧問い合わせを処理する。
//  WGSL の解析も GPU 転送も行わない（前者は `renderer::shade_params`、
//  後者は `frame_renderer.rs` の責務）。
//
//  ## 保存先が 2 つある理由
//  シェーディングアセットは「カメラごと」と「シーン既定」の 2 段で指定でき、
//  描画側は **カメラ → シーン → 標準 PBR** の順に採る。値の持ち主も同じ規則に従うので、
//  カメラ用（`CameraComponent::shading_params`）とシーン用（`Scene::shading_params`）を
//  別々に持つ。同じ形のマップなので、下請けの純関数（`set_param` 等）は共有する。
//
//  ## 宣言の妥当性をここで検証しない
//  アセットは編集中に書き換わる（ホットリロード）。「今の宣言に無い名前は拒否する」と
//  すると、値を入れる → アセットを直す → 値が消えている、という順序依存の事故が起きる。
//  孤児の値は**描画側が無視する**（`shade_params::build_block`）ので、持っていても害が無く、
//  アセットを戻せば値も戻る。ただし**文字列として壊れているバインドは受け付けない**
//  （保存しても永久に解決できないゴミにしかならないため）。
//
//  ## Undo
//  `field_edit.rs` の共通機構が担当する。カメラ側は `FieldEditTarget::Slot`、
//  シーン側は `FieldEditTarget::SceneShading` に分類済みなので、
//  **このファイルに Undo 記録を書いてはならない**（二重記録になる）。
// ============================================================

use std::collections::BTreeMap;

use crate::engine::binding::resolve::parse_binding;
use crate::engine::binding::shade_bindings::resolve_bindings;
use crate::engine::components::{CameraComponent, ComponentKind};
use crate::engine::core::renderer::shade_params::{self, PARAM_VALUE_COMPONENTS};
use crate::engine::core::renderer::shading_asset;

use super::{App, find_actor_by_dfs};

// ─── 定数 ─────────────────────────────────────────────────────

/// IPC で運ぶパラメータ値の成分数（型に依らず常に 4 成分）。
const VEC4_COMPONENT_COUNT: usize = PARAM_VALUE_COMPONENTS;

/// シーン設定ウィンドウへ返すパラメータ一覧のレスポンス接頭辞。
const SCENE_SHADING_PARAMS_RESPONSE: &str = "SCENE_SHADING_PARAMS:";

// ─── 値のパース（純関数）──────────────────────────────────────

/// `"x,y,z,w"` 形式の文字列を `[f32; 4]` へパースする。
///
/// 成分数を型で変えないのは、アセットの宣言が変わっても IPC の形が変わらないようにするため。
fn parse_vec4(value: &str) -> Option<[f32; PARAM_VALUE_COMPONENTS]> {
    let parts: Vec<&str> = value.split(',').collect();
    if parts.len() != VEC4_COMPONENT_COUNT { return None; }
    Some([
        parts[0].trim().parse::<f32>().ok()?,
        parts[1].trim().parse::<f32>().ok()?,
        parts[2].trim().parse::<f32>().ok()?,
        parts[3].trim().parse::<f32>().ok()?,
    ])
}

// ─── マップ操作（純関数。カメラ用とシーン用で共有する）────────

/// 上書き値を 1 個入れる。値が壊れていれば何もしない。戻り値は「変化したか」。
fn set_param(
    params: &mut BTreeMap<String, [f32; PARAM_VALUE_COMPONENTS]>,
    name:   &str,
    value:  &str,
) -> bool {
    let key = name.trim();
    if key.is_empty() { return false; }
    let Some(v) = parse_vec4(value) else { return false };
    params.insert(key.to_string(), v) != Some(v)
}

/// 上書き値を 1 個消す（＝アセットの既定値へ戻す）。戻り値は「変化したか」。
///
/// 既定値を書き込むのではなく**消す**のは、後からアセット側の既定値を書き換えたときに
/// 「戻した対象」が新しい既定値へ追随するようにするため（アセットが正典であり続ける）。
fn reset_param(
    params: &mut BTreeMap<String, [f32; PARAM_VALUE_COMPONENTS]>,
    name:   &str,
) -> bool {
    let key = name.trim();
    if key.is_empty() { return false; }
    params.remove(key).is_some()
}

/// `@ref` バインドを設定・解除する。戻り値は「変化したか」。
///
/// `binding` が空文字列なら解除（キーごと消す）。解除しても上書き値には触れないので、
/// バインド前に手で入れた値がそのまま復活する。
fn set_binding(
    bindings: &mut BTreeMap<String, String>,
    name:     &str,
    binding:  &str,
) -> bool {
    let key = name.trim();
    if key.is_empty() { return false; }
    let binding = binding.trim();
    // 空 = 解除。それ以外は 3 要素へ割れることを確認する（割れないものは永久に解決できない）。
    if !binding.is_empty() && parse_binding(binding).is_none() { return false; }
    if binding.is_empty() {
        bindings.remove(key).is_some()
    } else {
        bindings.insert(key.to_string(), binding.to_string()).as_deref() != Some(binding)
    }
}

// ─── App 側のハンドラ ─────────────────────────────────────────

impl App {
    /// 対象スロットが CameraComponent であることを確かめ、その entity を返す。
    fn camera_slot_entity(
        &self,
        actor_dfs_id: u32,
        slot_idx:     u32,
    ) -> Option<crate::engine::ecs::Entity> {
        let scene = self.scene.as_ref()?;
        let mut c = 0u32;
        find_actor_by_dfs(&scene.actors, self.active_world_line, actor_dfs_id, &mut c)?
            .slots().get(slot_idx as usize)
            .filter(|s| s.kind == ComponentKind::Camera)
            .map(|s| s.entity)
    }

    /// カメラのシェーディングパラメータを書き換える共通処理。
    ///
    /// `edit` が false（何も変わらなかった）を返したときは再送も `SCENE_MODIFIED` も
    /// 出さない（無意味な「未保存」印を付けないため）。
    fn edit_camera_shading(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        edit:         impl FnOnce(&mut CameraComponent) -> bool,
    ) {
        let Some(entity) = self.camera_slot_entity(actor_dfs_id, slot_idx) else { return };
        let changed = {
            let Some(scene) = &mut self.scene else { return };
            let Some(cc) = scene.world.get_mut::<CameraComponent>(entity) else { return };
            edit(cc)
        };
        if !changed { return; }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// SET_CAMERA_SHADING_PARAM: カメラのパラメータ 1 個を更新する。
    pub(super) fn handle_set_camera_shading_param(
        &mut self, actor_dfs_id: u32, slot_idx: u32, name: &str, value: &str,
    ) {
        self.edit_camera_shading(actor_dfs_id, slot_idx,
            |cc| set_param(&mut cc.shading_params, name, value));
    }

    /// RESET_CAMERA_SHADING_PARAM: カメラのパラメータ 1 個をアセット既定値へ戻す。
    pub(super) fn handle_reset_camera_shading_param(
        &mut self, actor_dfs_id: u32, slot_idx: u32, name: &str,
    ) {
        self.edit_camera_shading(actor_dfs_id, slot_idx,
            |cc| reset_param(&mut cc.shading_params, name));
    }

    /// SET_CAMERA_SHADING_BINDING: カメラの `@ref` バインドを設定・解除する。
    pub(super) fn handle_set_camera_shading_binding(
        &mut self, actor_dfs_id: u32, slot_idx: u32, name: &str, binding: &str,
    ) {
        self.edit_camera_shading(actor_dfs_id, slot_idx,
            |cc| set_binding(&mut cc.shading_bindings, name, binding));
    }

    /// シーンのシェーディングパラメータを書き換える共通処理。
    ///
    /// 変化があればシーン設定ウィンドウへ一覧を送り直す（カメラ側と違い、
    /// シーン設定は `ACTOR_COMPONENTS` に載らないので専用の再送が要る）。
    fn edit_scene_shading(
        &mut self,
        edit: impl FnOnce(&mut crate::engine::core::app_base::scene::Scene) -> bool,
    ) {
        let changed = {
            let Some(scene) = &mut self.scene else { return };
            edit(scene)
        };
        if !changed { return; }
        self.send_scene_shading_params();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// SET_SCENE_SHADING_PARAM: シーン既定のパラメータ 1 個を更新する。
    pub(super) fn handle_set_scene_shading_param(&mut self, name: &str, value: &str) {
        self.edit_scene_shading(|s| set_param(&mut s.shading_params, name, value));
    }

    /// RESET_SCENE_SHADING_PARAM: シーン既定のパラメータ 1 個をアセット既定値へ戻す。
    pub(super) fn handle_reset_scene_shading_param(&mut self, name: &str) {
        self.edit_scene_shading(|s| reset_param(&mut s.shading_params, name));
    }

    /// SET_SCENE_SHADING_BINDING: シーン既定の `@ref` バインドを設定・解除する。
    pub(super) fn handle_set_scene_shading_binding(&mut self, name: &str, binding: &str) {
        self.edit_scene_shading(|s| set_binding(&mut s.shading_bindings, name, binding));
    }

    /// GET_SCENE_SHADING_PARAMS / 値の変更時: シーン既定のパラメータ一覧を送る。
    ///
    /// 応答は `SCENE_SHADING_PARAMS:{json}`。JSON は水面のパラメータ行と**同一のワイヤ表現**
    /// （`shade_params::params_json`）なので、エディタ側は同じ行生成コードを使い回せる。
    pub(super) fn send_scene_shading_params(&self) {
        let Some(ipc) = &self.ipc else { return };
        let json = match &self.scene {
            Some(scene) => {
                let path = scene.shading_asset.as_deref().unwrap_or("");
                shading_asset::with_cached_declarations(path, |decls| {
                    let live = resolve_bindings(
                        &scene.actors, &scene.world, self.active_world_line,
                        decls, &scene.shading_bindings,
                    );
                    shade_params::params_json(
                        decls, &scene.shading_params, &scene.shading_bindings, &live)
                })
            }
            None => "[]".to_string(),
        };
        ipc.send(&format!("{SCENE_SHADING_PARAMS_RESPONSE}{json}"));
    }

}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 4 成分の値がパースでき、成分数違い・非数値は拒否されること。
    #[test]
    fn vec4_parsing_requires_exactly_four_numbers() {
        assert_eq!(parse_vec4("1,2,3,4"), Some([1.0, 2.0, 3.0, 4.0]));
        assert_eq!(parse_vec4(" 1.5 , 0 , 0 , 0 "), Some([1.5, 0.0, 0.0, 0.0]));
        assert_eq!(parse_vec4("1,2,3"), None, "成分不足");
        assert_eq!(parse_vec4("1,2,3,4,5"), None, "成分過多");
        assert_eq!(parse_vec4("1,2,3,x"), None, "非数値");
    }

    /// 値の設定・リセットが「差分マップ」として振る舞うこと。
    #[test]
    fn set_and_reset_behave_as_override_diff() {
        let mut p = BTreeMap::new();
        assert!(set_param(&mut p, "glow", "2,0,0,0"), "新規は変化あり");
        assert_eq!(p["glow"], [2.0, 0.0, 0.0, 0.0]);
        assert!(!set_param(&mut p, "glow", "2,0,0,0"), "同値の再設定は変化なし");
        assert!(!set_param(&mut p, "glow", "bad"), "壊れた値は無視");
        assert_eq!(p["glow"], [2.0, 0.0, 0.0, 0.0], "壊れた値で既存を壊さない");
        assert!(reset_param(&mut p, "glow"), "消せたら変化あり");
        assert!(p.is_empty(), "リセットは既定値を書き込むのではなく消す");
        assert!(!reset_param(&mut p, "glow"), "元々無い名前のリセットは変化なし");
        assert!(!set_param(&mut p, "  ", "1,0,0,0"), "空の名前はキーにしない");
    }

    /// バインドの設定・解除と、壊れた文字列の拒否。
    #[test]
    fn binding_set_and_clear() {
        let mut b = BTreeMap::new();
        assert!(set_binding(&mut b, "boost", "Sun|MainLight|intensity"));
        assert_eq!(b["boost"], "Sun|MainLight|intensity");
        assert!(!set_binding(&mut b, "boost", "Sun|MainLight|intensity"), "同値は変化なし");
        assert!(!set_binding(&mut b, "boost", "壊れている"), "3 要素に割れない値は拒否");
        assert_eq!(b["boost"], "Sun|MainLight|intensity", "拒否しても既存を壊さない");
        assert!(set_binding(&mut b, "boost", ""), "空文字列で解除");
        assert!(b.is_empty());
        assert!(!set_binding(&mut b, "boost", ""), "元々無いものの解除は変化なし");
    }
}
