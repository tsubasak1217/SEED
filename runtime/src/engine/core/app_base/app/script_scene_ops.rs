// ============================================================
//  script_scene_ops.rs — スクリプト発行のシーン操作コマンド適用
//
//  C# スクリプトの GameObject.Instantiate / Destroy は、フェーズ実行中に
//  Actor ツリーを直接変更できないため host_api のコマンドキューに積まれる。
//  本モジュールの apply_script_scene_commands がフレームのゲームロジック後に
//  それらをまとめて適用する（Unity の遅延 Destroy と同じタイミングモデル）。
//
//  - Instantiate: ffi_instantiate が予約したルートエンティティへ .actor を構築。
//    予約エンティティにはデフォルト Transform が挿入済みで、スクリプトが
//    生成直後に設定した Position 等はそのまま優先される。
//  - Destroy: ルートエンティティで Actor をツリーから取り出し、全エンティティを despawn。
// ============================================================

use crate::engine::components::{ComponentKind, ModelComponent, Transform};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::scripting::{take_scene_commands, ScriptSceneCommand};
use crate::engine::ecs::Entity;

use super::{App, despawn_actor_recursive, extract_actor_by_entity};

impl App {
    /// スクリプトが積んだシーン操作コマンド（Instantiate / Destroy）を適用する。
    ///
    /// frame_renderer のゲームロジックブロック（Play・非ポーズ時）の直後に呼ばれる。
    /// コマンドが無ければ何もしない。適用後はエディタのヒエラルキー表示を同期する。
    pub(super) fn apply_script_scene_commands(&mut self) {
        let commands = take_scene_commands();
        if commands.is_empty() { return; }

        for cmd in commands {
            match cmd {
                ScriptSceneCommand::Instantiate { path, entity } => {
                    self.apply_script_instantiate(&path, entity);
                }
                ScriptSceneCommand::Destroy { entity } => {
                    self.apply_script_destroy(entity);
                }
            }
        }

        // エディタのヒエラルキーパネルへ最新のツリーを送る（Play 中の生成/破棄も可視化）
        self.send_hierarchy();
    }

    /// Instantiate コマンドを適用する: 予約済みルートエンティティへ .actor を構築し、
    /// シーンの Actor ツリーへ追加する。
    ///
    /// 読み込みに失敗した場合は予約エンティティを despawn してリークを防ぐ
    /// （スクリプト側のハンドルは無効になり、以降のアクセスは既定値扱い）。
    fn apply_script_instantiate(&mut self, path: &str, root: Entity) {
        if self.draw_ctx.is_none() || self.scene.is_none() {
            return;
        }

        let host = self.scripting_host.clone();

        // load_actor_into は draw_ctx と scene.world を同時に参照するため
        // ブロックスコープで借用ライフタイムを制限する
        let load_result = {
            let ctx   = self.draw_ctx.as_ref().unwrap();
            let scene = self.scene.as_mut().unwrap();
            Scene::load_actor_into(
                std::path::Path::new(path),
                ctx,
                &mut scene.world,
                host.as_ref(),
                0,          // world_line = 通常シーン（Play シーン）
                Some(root), // ffi_instantiate が予約したルートエンティティを使う
            )
        };

        match load_result {
            Ok(actor) => {
                let scene = self.scene.as_mut().unwrap();
                if actor.is_2d() {
                    // 2D アクター: world_line=0 を 2D キャンバスモードとして登録する
                    self.canvas_world_lines.insert(0);
                } else {
                    // 3D アクター: スクリプトが設定した（または identity の）現在の
                    // Transform からモデルの instance_mats を同期する。
                    // Transform だけでは GPU 描画に反映されないため（drop 配置と同じ処理）。
                    let spawn_mat = scene.world.get::<Transform>(actor.entity)
                        .map(|t| t.to_mat4());
                    if let Some(mat) = spawn_mat {
                        for slot in actor.slots() {
                            if slot.kind == ComponentKind::Model {
                                if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot.entity) {
                                    for m in mc.instance_mats.iter_mut() {
                                        *m = mat;
                                    }
                                    mc.mark_batch_dirty();
                                }
                            }
                        }
                    }
                }
                scene.actors.push(actor);
            }
            Err(e) => {
                // 失敗時: 予約エンティティを破棄してハンドルを無効化する。
                // [Script] プレフィックスでゲーム側出力として分類させる。
                eprintln!("[Script] Instantiate 失敗 ({path}): {e}");
                if let Some(scene) = self.scene.as_mut() {
                    scene.world.despawn(root);
                }
            }
        }
    }

    /// Destroy コマンドを適用する: ルートエンティティで Actor をツリーから取り出し、
    /// 自身と全子孫のエンティティ（スロット含む）を despawn する。
    ///
    /// Actor ツリーに見つからない場合（Instantiate と同フレームで Destroy された等）は
    /// 予約エンティティ単体を despawn する。
    ///
    /// 注意: 物理スレッドのコライダーは Play 開始時に一括収集されるため、
    /// ここでは除去されない（物理イベント API 実装時に対応予定）。
    fn apply_script_destroy(&mut self, entity: Entity) {
        let Some(scene) = self.scene.as_mut() else { return };

        match extract_actor_by_entity(&mut scene.actors, entity) {
            Some(actor) => despawn_actor_recursive(&actor, &mut scene.world),
            None        => scene.world.despawn(entity),
        }
    }
}
