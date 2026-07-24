// ============================================================
//  clipboard.rs — コピー&ペースト処理
//
//  do_copy / do_paste
// ============================================================

use crate::engine::components::{GROUP_ID_BASE, ModelComponent};
use crate::engine::core::app_base::scene::build_actor;
use crate::engine::core::app_base::undo::{ActorTreeSnapshotCommand, SceneSnapshotCommand};

use super::{App, ClipboardItem, count_actor_dfs_nodes, find_actor_by_dfs};

impl App {
    /// 選択アクター / 選択インスタンスをクリップボードへコピーする。
    ///
    /// - アクターツリー選択（selected_actor_dfs_ids が非空）→ ActorData をコピー
    /// - レガシー MC インスタンス選択（selected_instances が非空）→ ClipboardItem をコピー（後方互換）
    pub(super) fn do_copy(&mut self) {
        // シーンモード / アクターツリー選択: ActorData 単位でコピーする
        if !self.selected_actor_dfs_ids.is_empty() {
            let Some(scene) = &self.scene else { return };
            let wl = self.active_world_line;
            let mut new_clipboard = Vec::new();
            for &dfs_id in &self.selected_actor_dfs_ids {
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id as u32, &mut c) {
                    new_clipboard.push(actor.to_data(&scene.world));
                }
            }
            if !new_clipboard.is_empty() {
                self.actor_clipboard = new_clipboard;
                // MC クリップボードはクリアしておく（混在防止）
                self.clipboard.clear();
            }
            return;
        }

        // レガシー: MC インスタンス直接選択（アクター編集モード等）
        use std::collections::{HashMap, HashSet};
        let Some(scene) = &self.scene else { return };
        let Some(mc) = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line)
        else {
            return;
        };
        if self.selected_instances.is_empty() {
            return;
        }

        let mut copy_set: HashSet<u32> = self.selected_instances.iter().copied().collect();
        for &root in &self.selected_instances {
            copy_set.extend(mc.all_descendants(root));
        }
        let mut copy_list: Vec<u32> = copy_set.into_iter().collect();
        copy_list.sort_unstable();

        let orig_to_local: HashMap<u32, usize> = copy_list
            .iter()
            .enumerate()
            .map(|(i, &orig)| (orig, i))
            .collect();

        self.clipboard = copy_list
            .iter()
            .map(|&orig| {
                let meta = &mc.instance_meta[orig as usize];
                let local_parent = meta
                    .parent
                    .filter(|&p| p < GROUP_ID_BASE)
                    .and_then(|p| orig_to_local.get(&p).copied());
                ClipboardItem {
                    name: meta.name.clone(),
                    mat: mc.instance_mats[orig as usize],
                    local_parent,
                    anim_seed: meta.anim_seed,
                }
            })
            .collect();
        // アクタークリップボードはクリアしておく（混在防止）
        self.actor_clipboard.clear();
    }

    /// クリップボードの内容をシーンへペーストする。
    ///
    /// - actor_clipboard が非空 → アクターとして復元（シーンモード）
    /// - clipboard が非空 → MC インスタンスとして復元（レガシー/後方互換）
    pub(super) fn do_paste(&mut self) {
        // シーンモード: ActorData クリップボードからアクターを復元する
        if !self.actor_clipboard.is_empty() {
            let wl = self.active_world_line;
            if self.scene.is_none() || self.draw_ctx.is_none() {
                return;
            }

            let before_actors = self.snapshot_actors_for_wl(wl);
            let data_list = self.actor_clipboard.clone();

            // ペースト後に新規アクターを選択するための DFS ベース位置を計算する
            let dfs_start = {
                let scene = self.scene.as_ref().unwrap();
                let mut c = 0usize;
                for a in scene.actors.iter().filter(|a| a.world_line == wl) {
                    count_actor_dfs_nodes(a, &mut c);
                }
                c
            };

            {
                let ctx = self.draw_ctx.as_ref().unwrap();
                let host = self.scripting_host.as_ref();
                let scene = self.scene.as_mut().unwrap();

                // 元の位置から少しずらしてペーストする
                const PASTE_OFFSET: f32 = 0.5;
                for data in data_list {
                    let mut paste_data = data;
                    paste_data.name = format!("{} (copy)", paste_data.name);
                    if let Some(ref mut tf) = paste_data.transform {
                        tf.position[0] += PASTE_OFFSET;
                        tf.position[2] += PASTE_OFFSET;
                    }
                    match build_actor(paste_data, ctx, &mut scene.world, host, None) {
                        Ok(mut actor) => {
                            actor.set_world_line_recursive(wl);
                            scene.actors.push(actor);
                        }
                        Err(e) => eprintln!("[SEED] do_paste: build_actor error: {e}"),
                    }
                }
            }

            let clipboard_count = self.actor_clipboard.len();
            let after_actors = self.snapshot_actors_for_wl(wl);
            self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
                world_line: wl,
                before_actors,
                after_actors,
            }));

            // 新規追加されたアクターを選択状態にする
            self.selected_actor_dfs_ids = (dfs_start..dfs_start + clipboard_count).collect();
            self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
            self.selected_instances.clear();

            self.send_selected();
            self.send_hierarchy();
            if let Some(ipc) = &self.ipc {
                ipc.send("SCENE_MODIFIED");
            }
            return;
        }

        // レガシー: MC インスタンスクリップボードから復元する（アクター編集モード等）
        use crate::engine::structs::components::model_component::InstanceMeta;
        if self.clipboard.is_empty() {
            return;
        }

        let before_selection = self.selected_instances.clone();

        let new_indices = {
            let wl = self.active_world_line;
            let Some(scene) = &mut self.scene else { return };
            let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(wl) else {
                return;
            };

            let before_mats = mc.instance_mats.clone();
            let before_meta = mc.instance_meta.clone();
            let before_groups = mc.group_meta.clone();
            let before_gid = mc.next_group_id;

            let base_idx = mc.instance_mats.len() as u32;
            let mut new_indices = Vec::with_capacity(self.clipboard.len());

            for (i, item) in self.clipboard.iter().enumerate() {
                mc.instance_mats.push(item.mat);
                mc.instance_meta.push(InstanceMeta {
                    name: format!("{}(1)", item.name),
                    parent: item.local_parent.map(|lp| base_idx + lp as u32),
                    anim_seed: item.anim_seed,
                });
                new_indices.push(base_idx + i as u32);
            }
            mc.mark_batch_dirty();

            let after_mats = mc.instance_mats.clone();
            let after_meta = mc.instance_meta.clone();
            let after_groups = mc.group_meta.clone();
            let after_gid = mc.next_group_id;

            self.undo_history.record(Box::new(SceneSnapshotCommand {
                before_mats,
                before_meta,
                before_groups,
                before_gid,
                after_mats,
                after_meta,
                after_groups,
                after_gid,
                before_selection: before_selection.clone(),
                after_selection: new_indices.clone(),
            }));

            new_indices
        };

        self.selected_instances = new_indices;
        self.send_selected();
        self.send_hierarchy();
    }
}
