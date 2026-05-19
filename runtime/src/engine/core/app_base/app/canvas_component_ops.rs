// ============================================================
//  canvas_component_ops.rs — Canvas / Sprite コンポーネント操作
//
//  【含む処理】
//  - handle_add_canvas_child_component: Sprite/Canvas 系コンポーネント追加ルーティング
//  - spawn_canvas_child_with_component: 新規子 Actor2D を生成してコンポーネントを追加
//  - add_canvas_then_child_with_component: Canvas を追加してから子 Actor2D を生成
//  - insert_canvas_component_slot: スロット挿入の内部ヘルパー
//  - handle_set_sprite_path / color / size: SpriteComponent プロパティ設定
//  - handle_set_canvas_anchor: CanvasTransform のアンカー設定
// ============================================================

use crate::engine::components::{
    ComponentKind, CanvasComponent, CanvasTransform, SpriteComponent,
};
use crate::engine::structs::objects::Actor;
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;

use super::{
    App,
    find_actor_by_dfs, find_actor_by_dfs_mut,
    actor_subtree_size, find_parent_canvas_info,
};

impl App {
    /// Sprite / Canvas 系コンポーネントを追加する際のルーティングメソッド。
    ///
    /// 以下の 3 ケースを判定してそれぞれ適切な処理へ委譲する:
    /// 1. 対象アクターが Canvas を持つ → 新規子 Actor2D を作成してそこに追加
    /// 2. 対象アクターの親が Canvas を持つ → 対象アクターに直接追加
    /// 3. どちらでもない → Canvas を追加してから子 Actor2D を作成して追加
    pub(super) fn handle_add_canvas_child_component(
        &mut self,
        actor_dfs_id:   u32,
        component_type: &str,
        slot_name:      &str,
        args:           &str,
    ) {
        if self.scene.is_none() { return; }
        let wl = self.active_world_line;

        // 対象アクターが Canvas を持つか確認
        let target_has_canvas = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .map(|a| a.has_kind(ComponentKind::Canvas))
                .unwrap_or(false)
        };

        // 親アクターが Canvas を持つか確認
        let (_parent_dfs, parent_has_canvas) = {
            let scene = self.scene.as_ref().unwrap();
            find_parent_canvas_info(&scene.actors, wl, actor_dfs_id)
        };

        if target_has_canvas {
            // Case 1: 対象が Canvas → 新規子アクターを作成してそこに追加
            self.spawn_canvas_child_with_component(actor_dfs_id, component_type, slot_name);
        } else if parent_has_canvas {
            // Case 2: 親が Canvas → 対象アクターに直接追加（通常フロー）
            self.handle_add_component_to_actor(actor_dfs_id, component_type, slot_name, args);
        } else {
            // Case 3: Canvas なし → Canvas を追加してから子アクターを作成して追加
            self.add_canvas_then_child_with_component(actor_dfs_id, component_type, slot_name);
        }
    }

    /// 指定アクターの子として新規 Actor2D を生成し、そこにコンポーネントを追加する。
    ///
    /// アクターツリーが変更されるため ActorTreeSnapshotCommand で Undo を記録する。
    fn spawn_canvas_child_with_component(
        &mut self,
        parent_dfs_id:  u32,
        component_type: &str,
        slot_name:      &str,
    ) {
        let wl = self.active_world_line;
        let before_actors = self.snapshot_actors_for_wl(wl);

        // 親のサブツリーサイズを記録（子を追加した後の child DFS id 計算用）
        let parent_size_before = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, parent_dfs_id, &mut c)
                .map(|a| actor_subtree_size(a))
                .unwrap_or(1)
        };

        // 新規子 Actor2D のエンティティを生成して CanvasTransform を挿入する
        let child_entity = {
            let scene = self.scene.as_mut().unwrap();
            let e = scene.world.spawn();
            scene.world.insert(e, CanvasTransform::default());
            e
        };

        // 子アクターを構築して親の children に追加する
        let child_added = {
            let scene = self.scene.as_mut().unwrap();
            let mut child = Actor::new_2d(child_entity, slot_name);
            child.world_line = wl;
            let mut c = 0u32;
            if let Some(parent) = find_actor_by_dfs_mut(&mut scene.actors, wl, parent_dfs_id, &mut c) {
                parent.add_child(child);
                true
            } else {
                scene.world.despawn(child_entity);
                false
            }
        };
        if !child_added { return; }

        // 新規子の DFS id = 親 DFS id + 追加前の親サブツリーサイズ
        let child_dfs_id = parent_dfs_id + parent_size_before;

        // 子アクターにコンポーネントスロットを追加する
        self.insert_canvas_component_slot(wl, child_dfs_id, component_type, slot_name);

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        // 子アクターを選択状態にしてエディタへ通知する
        self.actor_virtual_selected_idx      = Some(child_dfs_id as usize);
        self.selected_actor_dfs_ids          = vec![child_dfs_id as usize];
        self.selected_instances.clear();
        self.send_selected();
        self.send_hierarchy();
        self.send_actor_components(child_dfs_id, 0);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 対象アクターに Canvas を追加し、さらに子 Actor2D を生成してコンポーネントを追加する。
    ///
    /// 対象アクターに Canvas も親 Canvas もない場合（空のアクター等）に呼ばれる。
    /// アクターツリーとコンポーネントの両方が変わるため ActorTreeSnapshotCommand で記録する。
    fn add_canvas_then_child_with_component(
        &mut self,
        actor_dfs_id:   u32,
        component_type: &str,
        slot_name:      &str,
    ) {
        let wl = self.active_world_line;
        let before_actors = self.snapshot_actors_for_wl(wl);

        // 対象アクターに Canvas スロットを追加する
        {
            let scene = self.scene.as_mut().unwrap();
            let slot_entity = scene.world.spawn();
            scene.world.insert(slot_entity, CanvasComponent::default());
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                actor.add_slot_typed::<CanvasComponent>(
                    "Canvas".to_string(), ComponentKind::Canvas, slot_entity,
                );
            } else {
                scene.world.despawn(slot_entity);
                return;
            }
        }

        // Canvas 追加後の対象アクターのサブツリーサイズを取得する
        // （Canvas スロット追加はノード数に影響しないためサブツリーサイズは変わらない）
        let parent_size = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .map(|a| actor_subtree_size(a))
                .unwrap_or(1)
        };

        // 新規子 Actor2D のエンティティを生成して CanvasTransform を挿入する
        let child_entity = {
            let scene = self.scene.as_mut().unwrap();
            let e = scene.world.spawn();
            scene.world.insert(e, CanvasTransform::default());
            e
        };

        // 子アクターを構築して対象アクターの children に追加する
        let child_added = {
            let scene = self.scene.as_mut().unwrap();
            let mut child = Actor::new_2d(child_entity, slot_name);
            child.world_line = wl;
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                actor.add_child(child);
                true
            } else {
                scene.world.despawn(child_entity);
                false
            }
        };
        if !child_added { return; }

        let child_dfs_id = actor_dfs_id + parent_size;

        // 子アクターにコンポーネントスロットを追加する
        self.insert_canvas_component_slot(wl, child_dfs_id, component_type, slot_name);

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        // 子アクターを選択状態にしてエディタへ通知する
        self.actor_virtual_selected_idx      = Some(child_dfs_id as usize);
        self.selected_actor_dfs_ids          = vec![child_dfs_id as usize];
        self.selected_instances.clear();
        self.send_selected();
        self.send_hierarchy();
        self.send_actor_components(child_dfs_id, 0);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 指定アクターの指定スロットに Canvas 2D コンポーネントを挿入する内部ヘルパー。
    ///
    /// 現在は SpriteComponent のみ対応。
    /// 今後 Canvas 上に配置するコンポーネント（Image など）を追加したらここに case を足す。
    fn insert_canvas_component_slot(
        &mut self,
        wl:             u32,
        actor_dfs_id:   u32,
        component_type: &str,
        slot_name:      &str,
    ) {
        match component_type {
            "SpriteComponent" => {
                let scene = self.scene.as_mut().unwrap();
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, SpriteComponent::default());
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<SpriteComponent>(
                        slot_name.to_string(), ComponentKind::Sprite, slot_entity,
                    );
                } else {
                    scene.world.despawn(slot_entity);
                }
            }
            _ => {}
        }
    }

    // ── SpriteComponent プロパティ設定 ────────────────────────────

    /// SpriteComponent のテクスチャパスを更新する。
    pub(super) fn handle_set_sprite_path(&mut self, actor_dfs_id: u32, slot_idx: u32, path: &str) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Sprite)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(sc) = scene.world.get_mut::<SpriteComponent>(entity) {
                sc.texture_path = path.to_string();
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// SpriteComponent のカラーを更新する（RGBA 正規化値）。
    pub(super) fn handle_set_sprite_color(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        r: f32, g: f32, b: f32, a: f32,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Sprite)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(sc) = scene.world.get_mut::<SpriteComponent>(entity) {
                sc.color = [r, g, b, a];
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// SpriteComponent のサイズを更新する（キャンバスユニット）。
    pub(super) fn handle_set_sprite_size(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        width: f32,
        height: f32,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Sprite)
                .map(|s| s.entity)
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(sc) = scene.world.get_mut::<SpriteComponent>(entity) {
                sc.width  = width;
                sc.height = height;
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// CanvasTransform の anchor を更新する。
    ///
    /// anchor は親 Canvas における position 基準点（[0,1]×[0,1]）。
    /// (0,0) = 左上, (0.5,0.5) = 中央, (1,1) = 右下。
    pub(super) fn handle_set_canvas_anchor(&mut self, actor_dfs_id: u32, ax: f32, ay: f32) {
        let wl = self.active_world_line;
        let Some(scene) = &mut self.scene else { return };
        let mut c = 0u32;
        let actor = match find_actor_by_dfs_mut(
            &mut scene.actors, wl, actor_dfs_id, &mut c,
        ) {
            Some(a) => a,
            None    => return,
        };
        if let Some(ct) = scene.world.get_mut::<CanvasTransform>(actor.entity) {
            ct.anchor = [ax.clamp(0.0, 1.0), ay.clamp(0.0, 1.0)];
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}
