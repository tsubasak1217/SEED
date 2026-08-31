// ============================================================
//  skinned_sprite_ops.rs — SkinnedSpriteComponent のフィールド編集ハンドラ
//
//  エディタのインスペクタから飛ぶ `SET_SKINNED_SPRITE_FIELD:` を処理する。
//  Undo は field_edit.rs の汎用機構が IPC ディスパッチの入口で面倒を見るため、
//  ここは「値をワールドへ書き戻す」ことだけに責任を持つ（単一責任）。
//
//  対応キー（C# 側 InspectorPanel と一致必須）:
//    mesh_path    : `.sprite_mesh` アセットパス（空文字列 = 未設定）
//    texture_path : テクスチャパス（空文字列 = 単色）
//    color        : "r,g,b,a"（正規化値）
//    layer        : 整数（描画優先度）
// ============================================================

use crate::engine::components::{ComponentKind, SkinnedSpriteComponent};

use super::App;

/// `color` キーの値に含まれる成分数（RGBA）。
const COLOR_COMPONENT_COUNT: usize = 4;

impl App {
    /// SkinnedSpriteComponent の 1 フィールドを更新する。
    ///
    /// 対象スロットが見つからない・種別違い・値のパース失敗のいずれでも
    /// 何もせずに戻る（不正入力でシーンを壊さない）。
    pub(super) fn handle_set_skinned_sprite_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        key: &str,
        value: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する（種別も確認する）
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::SkinnedSprite)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(ss) = scene.world.get_mut::<SkinnedSpriteComponent>(entity) else {
            return;
        };

        // メッシュパスが変わったか（変わったら CPU メッシュキャッシュを捨てる）
        let mut mesh_changed = false;

        match key {
            "mesh_path" => {
                ss.mesh_path = value.to_string();
                // メッシュを差し替えた瞬間は「同じパスへ内容だけ書き換えた」場合も
                // 含めて読み直したい（オーサリング中はここが唯一の再読込トリガー）。
                // 描画側キャッシュはパス変更を自前で検出するので、CPU 側だけ捨てる。
                mesh_changed = true;
            }
            "texture_path" => ss.texture_path = value.to_string(),
            "color" => {
                // "r,g,b,a" を厳密に 4 成分でパースする（欠けていたら丸めずに無視）
                let parts: Vec<&str> = value.split(',').collect();
                if parts.len() == COLOR_COMPONENT_COUNT {
                    let mut rgba = [0.0f32; COLOR_COMPONENT_COUNT];
                    for (i, p) in parts.iter().enumerate() {
                        let Ok(v) = p.trim().parse::<f32>() else { return };
                        rgba[i] = v;
                    }
                    ss.color = rgba;
                }
            }
            "layer" => {
                if let Ok(v) = value.trim().parse::<i32>() {
                    ss.layer = v;
                }
            }
            // ポインタイベントのヒットテスト対象か（"0" / "1" で送られてくる）
            "raycast_target" => {
                if let Ok(v) = value.trim().parse::<i32>() {
                    ss.raycast_target = v != 0;
                }
            }
            _ => {}
        }

        if mesh_changed {
            self.clear_sprite_mesh_cpu_cache();
        }

        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
        // ボーン対応表（メッシュのボーン一覧）が入れ替わるので、
        // メッシュ差し替え時はインスペクタへ最新の表を送り直す。
        if mesh_changed {
            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        }
    }
}
