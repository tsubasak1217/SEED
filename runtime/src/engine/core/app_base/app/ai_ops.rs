// ============================================================
//  ai_ops.rs — AI アシスタント用エディタ操作
//
//  AI が IPC 経由で要求するシーン操作を実装する。
//  ・send_scene_info  : 現在シーンの JSON をエディタへ返送
//  ・handle_ai_add_actor     : 名前・位置を指定してアクターを追加
//  ・handle_ai_move_actor    : DFS ID で指定したアクターを移動
//  ・handle_ai_add_component : DFS ID で指定したアクターにコンポーネントを追加
// ============================================================

use crate::engine::components::{Transform as ActorTransform, CanvasTransform};
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;
use crate::engine::structs::objects::Actor;

use super::{
    App,
    find_actor_by_dfs,
    find_actor_by_dfs_mut,
};

impl App {
    // ============================================================
    //  send_scene_info — シーン全体情報をエディタへ送信
    // ============================================================

    /// 現在の世界線（WL=0）のアクターツリーを ActorData としてシリアライズし、
    /// `SCENE_INFO:{json}` としてエディタに送信する。
    ///
    /// AI がシーンの現状を把握するために呼ばれる。
    pub(super) fn send_scene_info(&self) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else {
            eprintln!("[AI] send_scene_info: シーンなし → SCENE_INFO:[]");
            ipc.send("SCENE_INFO:[]");
            return;
        };

        // 世界線 0（通常シーン）のアクターのみ対象にする。
        // シリアライズ時に DFS ID を付与するためにカウンタを用意する。
        let mut counter = Some(0u32);
        let actors_data: Vec<_> = scene.actors.iter()
            .filter(|a| a.world_line == 0)
            .map(|a| a.to_data_recursive(&scene.world, &mut counter))
            .collect();

        eprintln!("[AI] send_scene_info: actor_count={}", actors_data.len());

        match serde_json::to_string(&actors_data) {
            Ok(json) => {
                eprintln!("[AI] send_scene_info: 送信 {} bytes", json.len() + 11);
                ipc.send(&format!("SCENE_INFO:{json}"));
            }
            Err(e) => {
                // シリアライズ失敗時も空配列を返してタイムアウトを防ぐ
                eprintln!("[AI] send_scene_info: シリアライズ失敗: {e} → フォールバック SCENE_INFO:[]");
                ipc.send("SCENE_INFO:[]");
            }
        }
    }

    // ============================================================
    //  handle_ai_add_actor — AI によるアクター追加
    // ============================================================

    /// AI の指示でアクターを追加し、指定の位置に配置する。
    ///
    /// - `name` : アクター名
    /// - `x/y/z`: ワールド空間の初期位置
    ///
    /// Undo 対応済み。追加後はヒエラルキー・SCENE_MODIFIED を送信。
    pub(super) fn handle_ai_add_actor(&mut self, name: &str, x: f32, y: f32, z: f32) {
        if self.scene.is_none() { return; }

        // Undo 用スナップショット（変更前）
        let before_actors = self.snapshot_actors_for_wl(0);

        let Some(scene) = &mut self.scene else { return };

        // エンティティを生成し、指定位置の Transform を挿入する
        let entity = scene.world.spawn();
        scene.world.insert(entity, ActorTransform {
            position: [x, y, z],
            rotation: [0.0, 0.0, 0.0],
            scale:    [1.0, 1.0, 1.0],
        });

        // アクターをルートに追加する（AI はネスト先を指定しない）
        let mut new_actor = Actor::new(entity, name);
        new_actor.world_line = 0;
        scene.actors.push(new_actor);

        // Undo 用スナップショット（変更後）
        let after_actors = self.snapshot_actors_for_wl(0);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line:    0,
            before_actors,
            after_actors,
        }));

        // エディタへ変更を通知する
        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    // ============================================================
    //  handle_ai_move_actor — AI によるアクター移動
    // ============================================================

    /// AI の指示で指定 DFS ID のアクターを移動する。
    ///
    /// 3D アクター（Transform）のみ対応。2D アクターには未対応。
    /// Undo は記録しない（AI からの一括変更時に履歴が肥大化するため）。
    pub(super) fn handle_ai_move_actor(&mut self, actor_dfs_id: u32, x: f32, y: f32, z: f32) {
        let Some(scene) = &mut self.scene else { return };
        let wl = self.active_world_line;

        // scene.actors（不変）と scene.world（可変）を同時借用してアクタを解決する
        let (actors, world) = (&scene.actors, &mut scene.world);
        let mut c = 0u32;
        let Some(actor) = find_actor_by_dfs(actors, wl, actor_dfs_id, &mut c) else { return };

        // 現在の Transform の position だけを差し替えた新しいワールド Transform を作る
        let mut new_tf = world.get::<ActorTransform>(actor.entity).cloned().unwrap_or_default();
        new_tf.position = [x, y, z];

        // 集約関数へ委譲する。
        // 単に Transform の数値を書くだけでは描画実体（instance_mats）も子アクタも
        // 追従しないため、必ずこの経路を通す。
        // Undo は記録しない（AI からの一括変更で履歴が肥大化するため）。
        crate::engine::core::transform_sync::set_actor_world_transform(
            actor, world, new_tf, actor_dfs_id + 1,
        );

        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    // ============================================================
    //  handle_ai_add_component — AI によるコンポーネント追加
    // ============================================================

    /// AI の指示で指定 DFS ID のアクターにコンポーネントを追加する。
    ///
    /// `component_type` は既存の ADD_COMPONENT コマンドと同じフォーマット
    /// （例: "Transform", "Plugin:SamplePlugin"）を使用する。
    /// 内部的に `handle_add_component_to_actor` に委譲する。
    pub(super) fn handle_ai_add_component(
        &mut self,
        actor_dfs_id: u32,
        component_type: &str,
        _params_json:  &str,   // 現在は未使用（将来の拡張用）
    ) {
        // AI が使いやすい短い名前を、エンジン内部の正規名称（...Component）に変換する
        let normalized_type = match component_type {
            "Model"   => "ModelComponent",
            "Script"  => "ScriptComponent",
            "Canvas"  => "CanvasComponent",
            "Sprite"  => "SpriteComponent",
            "Camera"  => "CameraComponent",
            "InputMap" => "InputMapComponent",
            _ => component_type,
        };

        // スロット名はコンポーネントタイプ名から自動生成する
        let slot_name = if normalized_type.starts_with("Plugin:") {
            &normalized_type["Plugin:".len()..]
        } else {
            normalized_type
        }.to_string();

        // 第2引数が component_type, 第3引数が slot_name
        self.handle_add_component_to_actor(actor_dfs_id, normalized_type, &slot_name, "");
    }
}
