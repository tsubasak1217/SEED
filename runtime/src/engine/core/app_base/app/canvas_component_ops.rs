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
    CanvasComponent, CanvasDrawZone, CanvasTransform, Collider2dComponent, ComponentKind,
    SpriteComponent,
};
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;
use crate::engine::structs::objects::Actor;

use super::{
    App, actor_subtree_size, find_actor_by_dfs, find_actor_by_dfs_mut, find_parent_canvas_info,
};

impl App {
    /// Sprite / Canvas 系コンポーネントを追加する際のルーティングメソッド。
    ///
    /// 以下の 3 ケースを判定してそれぞれ適切な処理へ委譲する:
    /// 1. 対象アクターが Canvas を持つ → 新規子 Actor2D を作成してそこに追加
    /// 2. 対象アクターの親が Canvas を持つ → 対象アクターに直接追加
    /// 3. どちらでもない
    ///    - 対象が 2D（Actor2D）: **Canvas を作らず** 新規子 Actor2D を作成してそこに追加。
    ///      2D スプライトはルートレベル（parent_canvas_size=None）でも描画されるため、
    ///      Canvas スロットを強制追加せずに済む（ユーザー要望: 勝手に Canvas を作らない）。
    ///    - 対象が 3D（Actor3D）: 3D ワールド空間にスプライトを描くには対象アクターへ
    ///      CanvasComponent が必須（frame_renderer の 3D Canvas 収集経路）。このケースに
    ///      限り Canvas を追加してから子 Actor2D を作成する（従来動作を維持）。
    pub(super) fn handle_add_canvas_child_component(
        &mut self,
        actor_dfs_id: u32,
        component_type: &str,
        slot_name: &str,
        args: &str,
    ) {
        if self.scene.is_none() {
            return;
        }
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

        // 対象アクターが 3D（Actor3D）かどうか（Case3 の分岐に使用）
        let is_target_3d = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .map(|a| !a.is_2d())
                .unwrap_or(false)
        };

        if target_has_canvas {
            // Case 1: 対象が Canvas → 新規子アクターを作成してそこに追加
            self.spawn_canvas_child_with_component(actor_dfs_id, component_type, slot_name);
        } else if parent_has_canvas {
            // Case 2: 親が Canvas → 対象アクターに直接追加（通常フロー）
            self.handle_add_component_to_actor(actor_dfs_id, component_type, slot_name, args);
        } else if is_target_3d {
            // Case 3a: Canvas なし・対象が 3D → 3D ワールドキャンバス描画に必要なため
            // Canvas を追加してから子 Actor2D を作成して追加する（従来動作）
            self.add_canvas_then_child_with_component(actor_dfs_id, component_type, slot_name);
        } else {
            // Case 3b: Canvas なし・対象が 2D → Canvas を作らず、新規子 Actor2D を
            // 作成してそこにコンポーネントを追加する（Case1 と同じ子作成ロジックを流用）。
            // 子スプライトはルートレベル描画で表示されるため Canvas は不要。
            self.spawn_canvas_child_with_component(actor_dfs_id, component_type, slot_name);
        }
    }

    /// 指定アクターの子として新規 Actor2D を生成し、そこにコンポーネントを追加する。
    ///
    /// アクターツリーが変更されるため ActorTreeSnapshotCommand で Undo を記録する。
    fn spawn_canvas_child_with_component(
        &mut self,
        parent_dfs_id: u32,
        component_type: &str,
        slot_name: &str,
    ) {
        let wl = self.active_world_line;
        let before_actors = self.snapshot_actors_for_wl(wl);

        // 親のサブツリーサイズを記録（子を追加した後の child DFS id 計算用）
        // また、親が 3D アクターかどうかを事前確認する（3D Canvas の場合は選択を親に維持）
        let (parent_size_before, is_parent_3d) = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            if let Some(a) = find_actor_by_dfs(&scene.actors, wl, parent_dfs_id, &mut c) {
                (actor_subtree_size(a), !a.is_2d())
            } else {
                (1, false)
            }
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
            if let Some(parent) =
                find_actor_by_dfs_mut(&mut scene.actors, wl, parent_dfs_id, &mut c)
            {
                parent.add_child(child);
                true
            } else {
                scene.world.despawn(child_entity);
                false
            }
        };
        if !child_added {
            return;
        }

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

        // 選択状態を更新してエディタへ通知する。
        // 3D Canvas（親が Actor3D）の場合は、子 Actor2D の gizmo は 3D 親の世界変換を
        // 考慮していないため、親を選択状態のまま維持して混乱を防ぐ。
        // 2D Canvas の場合は従来通り子を選択して編集しやすくする。
        let select_dfs = if is_parent_3d {
            parent_dfs_id
        } else {
            child_dfs_id
        };
        self.actor_virtual_selected_idx = Some(select_dfs as usize);
        self.selected_actor_dfs_ids = vec![select_dfs as usize];
        self.selected_instances.clear();
        self.send_selected();
        self.send_hierarchy();
        self.send_actor_components(select_dfs, 0);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// 対象アクターに Canvas を追加し、さらに子 Actor2D を生成してコンポーネントを追加する。
    ///
    /// 対象アクターに Canvas も親 Canvas もない場合（空のアクター等）に呼ばれる。
    /// アクターツリーとコンポーネントの両方が変わるため ActorTreeSnapshotCommand で記録する。
    fn add_canvas_then_child_with_component(
        &mut self,
        actor_dfs_id: u32,
        component_type: &str,
        slot_name: &str,
    ) {
        let wl = self.active_world_line;
        let before_actors = self.snapshot_actors_for_wl(wl);

        // 対象アクターが 3D かどうかを事前確認する（デフォルトサイズと選択対象の切り替え用）
        let is_actor_3d = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .map(|a| !a.is_2d())
                .unwrap_or(false)
        };

        // 対象アクターに Canvas スロットを追加する
        {
            let scene = self.scene.as_mut().unwrap();
            // 3D キャンバス: デフォルト解像度 640×360、自動スケール無効
            // 2D キャンバス: 従来通り 1920×1080、自動スケール有効
            let cc = if is_actor_3d {
                CanvasComponent {
                    width: 640.0,
                    height: 360.0,
                    auto_scale: false,
                    ..CanvasComponent::default()
                }
            } else {
                CanvasComponent::default()
            };
            let slot_entity = scene.world.spawn();
            scene.world.insert(slot_entity, cc);
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c)
            {
                actor.add_slot_typed::<CanvasComponent>(
                    "Canvas".to_string(),
                    ComponentKind::Canvas,
                    slot_entity,
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
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c)
            {
                actor.add_child(child);
                true
            } else {
                scene.world.despawn(child_entity);
                false
            }
        };
        if !child_added {
            return;
        }

        let child_dfs_id = actor_dfs_id + parent_size;

        // 子アクターにコンポーネントスロットを追加する
        self.insert_canvas_component_slot(wl, child_dfs_id, component_type, slot_name);

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        // 選択状態を更新してエディタへ通知する。
        // 3D Canvas の場合は親アクターを選択維持（子 Actor2D の gizmo は親の世界変換を
        // 未考慮のため、親を選択して gizmo による移動を正常にする）。
        let select_dfs = if is_actor_3d {
            actor_dfs_id
        } else {
            child_dfs_id
        };
        self.actor_virtual_selected_idx = Some(select_dfs as usize);
        self.selected_actor_dfs_ids = vec![select_dfs as usize];
        self.selected_instances.clear();
        self.send_selected();
        self.send_hierarchy();
        self.send_actor_components(select_dfs, 0);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// 指定アクターの指定スロットに Canvas 2D コンポーネントを挿入する内部ヘルパー。
    ///
    /// Canvas 系コンポーネントを 2D Actor に挿入する内部ヘルパー。
    ///
    /// Canvas 上に新しいコンポーネント種別を追加する場合はここに case を追加する。
    fn insert_canvas_component_slot(
        &mut self,
        wl: u32,
        actor_dfs_id: u32,
        component_type: &str,
        slot_name: &str,
    ) {
        match component_type {
            "SpriteComponent" => {
                let scene = self.scene.as_mut().unwrap();
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, SpriteComponent::default());
                let mut c = 0u32;
                if let Some(actor) =
                    find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c)
                {
                    actor.add_slot_typed::<SpriteComponent>(
                        slot_name.to_string(),
                        ComponentKind::Sprite,
                        slot_entity,
                    );
                } else {
                    scene.world.despawn(slot_entity);
                }
            }
            "Collider2dComponent" => {
                let scene = self.scene.as_mut().unwrap();
                let slot_entity = scene.world.spawn();
                scene
                    .world
                    .insert(slot_entity, Collider2dComponent::default());
                let mut c = 0u32;
                if let Some(actor) =
                    find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c)
                {
                    actor.add_slot_typed::<Collider2dComponent>(
                        slot_name.to_string(),
                        ComponentKind::Collider2d,
                        slot_entity,
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
        // パスが変更されたときはテクスチャキャッシュの該当エントリを削除して再ロードを強制する。
        // キャッシュに古い（失敗した）テクスチャが残ったままになるのを防ぐ。
        if let Some(ctx) = &self.draw_ctx {
            ctx.sprite_tex_cache.borrow_mut().remove(path);
            // 元テクスチャが変わると焼き込み済みポストエフェクトも無効化する。
            // （キーは (texture_path, postfx_path)。texture_path で消せないため全 postfx を消す簡易対応でも
            //   よいが、ここでは新旧テクスチャに紐づくものを含め安全側で mtime 差により自然再焼きされる。）
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// SpriteComponent のポストエフェクト（.postfx）参照パスを更新する（空文字列で無効化）。
    pub(super) fn handle_set_sprite_postfx(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        path: &str,
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
                sc.postfx_path = path.to_string();
            }
        }
        // .postfx 参照が変わったら該当アセットの焼き込みキャッシュを破棄して焼き直しを強制する。
        if let Some(ctx) = &self.draw_ctx {
            if !path.is_empty() {
                ctx.sprite_postfx_cache.invalidate_postfx(path);
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// SpriteComponent のカラーを更新する（RGBA 正規化値）。
    pub(super) fn handle_set_sprite_color(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        r: f32,
        g: f32,
        b: f32,
        a: f32,
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
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
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
                sc.width = width;
                sc.height = height;
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// SpriteComponent の描画優先度レイヤーを更新する（大きいほど手前・同値は DFS 順）。
    pub(super) fn handle_set_sprite_layer(&mut self, actor_dfs_id: u32, slot_idx: u32, layer: i32) {
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
                sc.layer = layer;
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    // ── CanvasComponent プロパティ設定 ────────────────────────────

    /// CanvasComponent の描画ゾーンを更新する（ビューポート・ルートキャンバス用）。
    ///
    /// zone: "background" = 3D ワールドの奥（クリアカラーの上）、
    ///       それ以外    = "foreground"（3D ワールドの手前・従来動作）。
    pub(super) fn handle_set_canvas_draw_zone(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        zone: &str,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Canvas)
                .map(|s| s.entity)
        };
        let draw_zone = if zone == "background" {
            CanvasDrawZone::Background
        } else {
            CanvasDrawZone::Foreground
        };
        if let Some(entity) = slot_entity {
            let Some(scene) = &mut self.scene else { return };
            if let Some(cc) = scene.world.get_mut::<CanvasComponent>(entity) {
                cc.draw_zone = draw_zone;
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// CanvasTransform の anchor を更新する。
    ///
    /// anchor は親 Canvas における position 基準点（[0,1]×[0,1]）。
    /// (0,0) = 左上, (0.5,0.5) = 中央, (1,1) = 右下。
    pub(super) fn handle_set_canvas_anchor(&mut self, actor_dfs_id: u32, ax: f32, ay: f32) {
        let wl = self.active_world_line;
        let Some(scene) = &mut self.scene else { return };
        let mut c = 0u32;
        let actor = match find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
            Some(a) => a,
            None => return,
        };
        if let Some(ct) = scene.world.get_mut::<CanvasTransform>(actor.entity) {
            ct.anchor = [ax.clamp(0.0, 1.0), ay.clamp(0.0, 1.0)];
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }
}
