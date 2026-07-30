// ============================================================
//  clipboard.rs — コピー&ペースト処理
//
//  do_copy / do_paste
// ============================================================

use crate::engine::components::{ModelComponent, GROUP_ID_BASE};
use crate::engine::core::app_base::undo::{
    SceneSnapshotCommand, ActorTreeSnapshotCommand,
};
use crate::engine::core::app_base::scene::build_actor;

use super::{
    App, find_actor_by_dfs, actor_subtree_size, insert_actors_after_dfs, ClipboardItem,
};
use crate::engine::structs::objects::Actor;

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
            // ペースト時に「選択群の並び順」をそのまま再現できるよう、DFS 昇順（＝ヒエラルキー
            // 表示順）に並べてからコピーする。選択順（クリック順）に依存させない。
            let mut src_dfs_ids = self.selected_actor_dfs_ids.clone();
            src_dfs_ids.sort_unstable();
            for &dfs_id in &src_dfs_ids {
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
        let Some(mc)    = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) else { return };
        if self.selected_instances.is_empty() { return; }

        let mut copy_set: HashSet<u32> = self.selected_instances.iter().copied().collect();
        for &root in &self.selected_instances {
            copy_set.extend(mc.all_descendants(root));
        }
        let mut copy_list: Vec<u32> = copy_set.into_iter().collect();
        copy_list.sort_unstable();

        let orig_to_local: HashMap<u32, usize> = copy_list.iter()
            .enumerate().map(|(i, &orig)| (orig, i)).collect();

        self.clipboard = copy_list.iter().map(|&orig| {
            let meta         = &mc.instance_meta[orig as usize];
            let local_parent = meta.parent
                .filter(|&p| p < GROUP_ID_BASE)
                .and_then(|p| orig_to_local.get(&p).copied());
            ClipboardItem {
                name:         meta.name.clone(),
                mat:          mc.instance_mats[orig as usize],
                local_parent,
                anim_seed:    meta.anim_seed,
            }
        }).collect();
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
            if self.scene.is_none() || self.draw_ctx.is_none() { return; }

            let before_actors = self.snapshot_actors_for_wl(wl);
            let data_list = self.actor_clipboard.clone();

            // ── 挿入位置の基準（アンカー）を決める ──────────────────────────
            // 複製は「複製元の直下（兄弟として一つ下）」へ入れる。基準は現在の選択群の中で
            // 「自身 + 子孫の DFS 範囲の終端」が最も後ろのアクター。こうすると
            //   ・単一選択     → その直後
            //   ・複数選択     → 選択群全体の直後（順序を維持してまとめて挿入）
            //   ・親と子を同時選択 → 子の内側ではなく親サブツリーの直後
            // が一手で満たせる。未選択なら None（従来どおり末尾追加へフォールバック）。
            let anchor_dfs: Option<u32> = {
                let scene = self.scene.as_ref().unwrap();
                let mut best: Option<(u32, u32)> = None;   // (サブツリー終端, DFS id)
                for &dfs_id in &self.selected_actor_dfs_ids {
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id as u32, &mut c) {
                        let end = dfs_id as u32 + actor_subtree_size(actor);
                        let is_better = match best {
                            None                 => true,
                            Some((best_end, _))  => end > best_end,
                        };
                        if is_better { best = Some((end, dfs_id as u32)); }
                    }
                }
                best.map(|(_, dfs_id)| dfs_id)
            };

            // ── 新規アクターを構築する（ツリーへの挿入はまとめて後段で行う）──────
            let mut new_actors: Vec<Actor> = Vec::new();
            {
                let ctx  = self.draw_ctx.as_ref().unwrap();
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
                            new_actors.push(actor);
                        }
                        Err(e) => eprintln!("[SEED] do_paste: build_actor error: {e}"),
                    }
                }
            }
            if new_actors.is_empty() { return; }

            // 各新規アクターのサブツリーノード数（挿入後の DFS id 算出に使う）。
            // 子を持つアクターをペーストすると DFS id は連番にならないため、
            // ノード数を積み上げて先頭 id を求める必要がある。
            let new_sizes: Vec<u32> = new_actors.iter().map(actor_subtree_size).collect();

            // ── ツリーへ挿入し、新規先頭アクターの DFS id を得る ────────────────
            let base_dfs = {
                let scene = self.scene.as_mut().unwrap();
                let inserted = anchor_dfs.and_then(|anchor| {
                    insert_actors_after_dfs(&mut scene.actors, wl, anchor, &mut new_actors)
                });
                match inserted {
                    Some(base) => base,
                    None => {
                        // 未選択・アンカー消失時のフォールバック: 末尾（ルート最後）へ追加する
                        let tail_dfs: u32 = scene.actors.iter()
                            .filter(|a| a.world_line == wl)
                            .map(actor_subtree_size)
                            .sum();
                        scene.actors.append(&mut new_actors);
                        tail_dfs
                    }
                }
            };

            let after_actors = self.snapshot_actors_for_wl(wl);
            self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
                world_line: wl,
                before_actors,
                after_actors,
            }));

            // 新規追加されたアクター（各サブツリーのルート）を選択状態にする
            let mut new_dfs_ids = Vec::with_capacity(new_sizes.len());
            let mut cursor = base_dfs as usize;
            for size in &new_sizes {
                new_dfs_ids.push(cursor);
                cursor += *size as usize;
            }
            self.selected_actor_dfs_ids = new_dfs_ids;
            self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
            self.selected_instances.clear();

            self.send_selected();
            self.send_hierarchy();
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            return;
        }

        // レガシー: MC インスタンスクリップボードから復元する（アクター編集モード等）
        use crate::engine::structs::components::model_component::InstanceMeta;
        if self.clipboard.is_empty() { return; }

        let before_selection = self.selected_instances.clone();

        let new_indices = {
            let wl = self.active_world_line;
            let Some(scene) = &mut self.scene else { return };
            let Some(mc)    = scene.find_component_in_world_line_mut::<ModelComponent>(wl) else { return };

            let before_mats   = mc.instance_mats.clone();
            let before_meta   = mc.instance_meta.clone();
            let before_groups = mc.group_meta.clone();
            let before_gid    = mc.next_group_id;

            let base_idx = mc.instance_mats.len() as u32;
            let mut new_indices = Vec::with_capacity(self.clipboard.len());

            for (i, item) in self.clipboard.iter().enumerate() {
                mc.instance_mats.push(item.mat);
                mc.instance_meta.push(InstanceMeta {
                    name:      format!("{}(1)", item.name),
                    parent:    item.local_parent.map(|lp| base_idx + lp as u32),
                    anim_seed: item.anim_seed,
                });
                new_indices.push(base_idx + i as u32);
            }
            mc.mark_batch_dirty();

            let after_mats   = mc.instance_mats.clone();
            let after_meta   = mc.instance_meta.clone();
            let after_groups = mc.group_meta.clone();
            let after_gid    = mc.next_group_id;

            self.undo_history.record(Box::new(SceneSnapshotCommand {
                before_mats, before_meta, before_groups, before_gid,
                after_mats,  after_meta,  after_groups,  after_gid,
                before_selection: before_selection.clone(),
                after_selection:  new_indices.clone(),
            }));

            new_indices
        };

        self.selected_instances = new_indices;
        self.send_selected();
        self.send_hierarchy();
    }
}
