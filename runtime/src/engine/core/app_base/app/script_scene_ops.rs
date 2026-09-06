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

use std::collections::HashMap;
use std::sync::Arc;

use crate::engine::components::{ComponentKind, ModelComponent, ScriptComponent, Transform};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::scripting::{
    take_scene_commands, with_actors, with_world, ScriptSceneCommand, ScriptingHost,
    PHYSICS_EVENT_COLLISION_ENTER, PHYSICS_EVENT_COLLISION_STAY, PHYSICS_EVENT_COLLISION_EXIT,
    PHYSICS_EVENT_TRIGGER_ENTER, PHYSICS_EVENT_TRIGGER_EXIT, PHYSICS_EVENT_TRIGGER_STAY,
};
use crate::engine::ecs::{Entity, World};
use crate::engine::physics::{CollisionEvent, CollisionPhase, TriggerEvent, TriggerPhase};
use crate::engine::structs::objects::Actor;

use super::{
    App, attach_actor_under, despawn_actor_recursive, extract_actor_by_entity,
    find_actor_by_entity, reparent_actor_by_entity,
};

impl App {
    /// スクリプトが積んだシーン操作コマンド（Instantiate / Destroy）を適用する。
    ///
    /// frame_renderer のゲームロジックブロック（Play・非ポーズ時）の直後に呼ばれる。
    /// コマンドが無ければ何もしない。適用後はエディタのヒエラルキー表示を同期する。
    pub(super) fn apply_script_scene_commands(&mut self) {
        let commands = take_scene_commands();
        if commands.is_empty() { return; }

        // TransitionScene が含まれる場合はシーン遷移のみを実行し、他のコマンドは破棄する。
        // 旧ワールドで予約されたエンティティ (index, generation) が新ワールドの
        // 別実体を指してしまう危険を防ぐため（Instantiate の予約エンティティ等）。
        if let Some(name) = commands.iter().find_map(|c| match c {
            ScriptSceneCommand::TransitionScene { name_or_path } => Some(name_or_path.clone()),
            _ => None,
        }) {
            // 遷移前にカーソルロックを解除する。新シーンのスクリプトが張り直さない限り
            // ロックを持ち越さない（前シーンの都合でカーソルが消えたままになるのを防ぐ）。
            self.release_script_cursor_lock();
            // 旧シーンのアクター entity を指す JointAttach 子孫キャッシュを破棄する
            //（新シーンで entity が再利用されると別実体の相対位置として誤適用される）。
            self.joint_attach_child_locals.clear();
            self.apply_script_transition_scene(&name);
            self.send_hierarchy();
            return;
        }

        for cmd in commands {
            match cmd {
                ScriptSceneCommand::Instantiate { path, entity, parent } => {
                    self.apply_script_instantiate(&path, entity, parent);
                }
                ScriptSceneCommand::Destroy { entity } => {
                    self.apply_script_destroy(entity);
                }
                ScriptSceneCommand::Reparent { entity, new_parent } => {
                    self.apply_script_reparent(entity, new_parent);
                }
                ScriptSceneCommand::PreloadScene { name_or_path } => {
                    self.apply_script_preload_scene(&name_or_path);
                }
                ScriptSceneCommand::TransitionScene { .. } => unreachable!("上で処理済み"),
            }
        }

        // エディタのヒエラルキーパネルへ最新のツリーを送る（Play 中の生成/破棄も可視化）
        self.send_hierarchy();
    }

    /// Instantiate コマンドを適用する: 予約済みルートエンティティへ .actor を構築し、
    /// シーンの Actor ツリーへ追加する。
    ///
    /// `parent` が Some のときは、その親アクターの **末尾の子** として追加する。
    /// 親が破棄済み・シーンに無い・2D/3D 種別が不整合の場合はルートへフォールバックし、
    /// `[Script]` 警告を出す（生成そのものは成功させる。ハンドルが無効化されると
    /// スクリプト側が同フレームで設定した Transform も無駄になるため）。
    ///
    /// 読み込みに失敗した場合は予約エンティティを despawn してリークを防ぐ
    /// （スクリプト側のハンドルは無効になり、以降のアクセスは既定値扱い）。
    fn apply_script_instantiate(&mut self, path: &str, root: Entity, parent: Option<Entity>) {
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
                // 親指定があれば末尾の子として取り付ける（失敗時はルートへフォールバック）
                if !attach_actor_under(&mut scene.actors, parent, actor) {
                    eprintln!(
                        "[Script] Instantiate: 指定した親が無効（破棄済み／種別不整合）のため                          ルート直下へ生成しました ({path})"
                    );
                }
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

    /// シーン参照（シーンマネージャ登録名または assets:// パス）を実パスへ解決する。
    ///
    /// 1. レジストリに名前が登録されていればそのパス
    /// 2. ".scene" 拡張子付きの登録名（"game.scene" → "game"）でも解決を試みる
    /// 3. どちらでもなければパス直接指定とみなしてそのまま返す
    fn resolve_scene_ref(&self, name_or_path: &str) -> String {
        if let Some(path) = self.scene_registry.get(name_or_path) {
            return path.clone();
        }
        // "name.scene" 形式でも登録名にマッチさせる（パス区切りを含まない場合のみ）
        if !name_or_path.contains('/') && !name_or_path.contains('\\') {
            if let Some(stem) = name_or_path.strip_suffix(".scene") {
                if let Some(path) = self.scene_registry.get(stem) {
                    return path.clone();
                }
            }
        }
        name_or_path.to_string()
    }

    /// PreloadScene コマンドを適用する: シーンを読み込んで保持する（遷移はしない）。
    ///
    /// Transition 前の暇なタイミング（フェードアウト中など）で呼んでおくことで、
    /// 遷移フレームのロード時間をなくす。保持できる事前読み込みは 1 つで、
    /// 直後の Transition で消費される（別のシーンを Preload すると置き換わる）。
    fn apply_script_preload_scene(&mut self, name_or_path: &str) {
        if self.draw_ctx.is_none() { return; }
        let path = self.resolve_scene_ref(name_or_path);

        // 同じシーンが事前読み込み済みなら何もしない
        if self.preloaded_scene.as_ref().is_some_and(|(p, _, _)| *p == path) {
            return;
        }

        let host = self.scripting_host.clone();
        let load_result = {
            let ctx = self.draw_ctx.as_ref().unwrap();
            Scene::load(std::path::Path::new(&path), ctx, host.as_ref())
        };
        match load_result {
            Ok((scene, cam)) => {
                self.preloaded_scene = Some((path, scene, cam));
            }
            Err(e) => {
                eprintln!("[Script] Scene.Load 失敗 ({name_or_path} → {path}): {e}");
            }
        }
    }

    /// TransitionScene コマンドを適用する: シーン全体を切り替える。
    ///
    /// 事前読み込み済み（Preload 済み）ならロードなしで即座に切り替え、
    /// 未読み込みならここで読み込んでから切り替える。
    /// 旧シーンの破棄（スクリプトインスタンスは ScriptComponent の Drop で CLR 側も解放）、
    /// 物理スレッドの再起動（起動していた場合のみ）、キャンバス世界線の再判定を行う。
    /// パスは assets:// 仮想パスのまま Scene::load へ渡す（PAK モード対応）。
    fn apply_script_transition_scene(&mut self, name_or_path: &str) {
        if self.draw_ctx.is_none() { return; }
        let path = self.resolve_scene_ref(name_or_path);

        // 事前読み込み済みならそれを消費し、なければここで読み込む（自動 Load）
        let loaded = match self.preloaded_scene.take() {
            Some((p, scene, cam)) if p == path => Some((scene, cam)),
            other => {
                // 別シーンの事前読み込みが残っていた場合は破棄する
                drop(other);
                let host = self.scripting_host.clone();
                let ctx  = self.draw_ctx.as_ref().unwrap();
                match Scene::load(std::path::Path::new(&path), ctx, host.as_ref()) {
                    Ok((scene, cam)) => Some((scene, cam)),
                    Err(e) => {
                        eprintln!("[Script] Scene.Transition 失敗 ({name_or_path} → {path}): {e}");
                        None
                    }
                }
            }
        };
        let Some((new_scene, cam_data)) = loaded else { return };

        // 物理スレッドの起動状態を記録してから停止する（新シーンで再収集するため）
        let had_physics    = self.physics_thread.is_some();
        let had_physics_2d = self.physics_thread_2d.is_some();
        self.stop_physics();
        self.stop_physics_2d();

        // シーンに保存されたデバッグカメラ位置を適用する（メインカメラ不在時のフォールバック）
        if let Some(cam) = cam_data {
            self.apply_camera_data(&cam);
        }
        // 2D アクターが含まれていれば WL 0 をキャンバス世界線として登録する
        fn has_any_2d_actor(actors: &[Actor]) -> bool {
            actors.iter().any(|a| a.is_2d() || has_any_2d_actor(a.children()))
        }
        if has_any_2d_actor(&new_scene.actors) {
            self.canvas_world_lines.insert(0);
        } else {
            self.canvas_world_lines.remove(&0);
        }
        // 旧シーンを置き換える（World の Drop で旧スクリプトインスタンスも解放される）
        self.scene = Some(new_scene);
        // 速度バッファ: シーン遷移で前フレームとの連続性が切れる ⇒ 次フレームは prev=curr。
        self.request_velocity_reset();

        // GPU パーティクルを全解放する（生存エミッタ＋孤児プールとも）。
        // 孤児（エミッタ消滅後も寿命まで生き残る粒子群）はシーンを跨いで残さない。
        self.particle_system.clear_all();
        // インタラクションソースの位置履歴も捨てる（Phase I1）。
        // 残したままシーンが入れ替わると「旧シーンの位置 → 新シーンの位置」の
        // 巨大な速度が 1 フレームだけ場へ焼かれ、草が一斉になぎ倒される。
        self.interaction_velocity.clear();
        // キャンバス UI のポインタ状態も捨てる（旧シーンのアクターへ Exit を飛ばさない）。
        self.pointer.reset();

        // コンポーネント音源を停止し、play_on_start の発火記録をリセットする
        //（新シーンの play_on_start を再発火させるため。BGM は継続する）
        if let Some(audio) = &mut self.audio {
            audio.reset_components();
        }

        // 物理を新シーンの内容で再起動する
        if had_physics    { self.start_physics(); }
        if had_physics_2d { self.start_physics_2d(); }
    }

    /// 物理イベント（衝突・トリガー）をスクリプトのコールバックへ配信する。
    ///
    /// update_physics が物理スレッドから受信したイベントを、DFS 順 ID から
    /// アクターへ逆引きし、両アクターのスクリプトへ OnCollisionEnter 等を呼ぶ。
    /// Play モードのゲームロジック前に呼ばれる（イベントは当該フレームの
    /// スクリプト更新から見える）。
    pub(super) fn dispatch_physics_events_to_scripts(
        &mut self,
        collision_events: &[CollisionEvent],
        trigger_events:   &[TriggerEvent],
    ) {
        let pairs = normalize_physics_pairs(collision_events, trigger_events);
        self.dispatch_physics_event_pairs(pairs);
    }

    /// 2D 物理イベント（衝突・トリガー）をスクリプトのコールバックへ配信する。
    ///
    /// 3D 版と同じコールバック（OnCollisionEnter 等）を使う。DFS 順 ID の採番は
    /// collect_actor2d_contexts（physics2d_ops.rs）も同一規則のため対応表を共用できる。
    pub(super) fn dispatch_physics2d_events_to_scripts(
        &mut self,
        collision_events: &[crate::engine::physics::CollisionEvent2d],
        trigger_events:   &[crate::engine::physics::TriggerEvent2d],
    ) {
        let pairs = normalize_physics2d_pairs(collision_events, trigger_events);
        self.dispatch_physics_event_pairs(pairs);
    }

    /// 正規化済みイベントペア（自分 DFS, 相手 DFS, 種別）をスクリプトへ配信する共通コア。
    fn dispatch_physics_event_pairs(&mut self, pairs: Vec<(u64, u64, i32)>) {
        use crate::engine::core::scripting::{publish_input, publish_physics_sender};

        if pairs.is_empty() { return; }
        let wl = self.active_world_line;

        // コールバック内から Input / Physics.Raycast も使えるように公開する
        //（物理同期はゲームロジックフェーズより前に走るため、ここで独自に公開する）
        publish_input(Some(&self.input));
        publish_physics_sender(self.physics_thread.as_ref().map(|t| t.command_sender()));

        let Some(scene) = &mut self.scene else {
            publish_input(None);
            publish_physics_sender(None);
            return;
        };

        // DFS 順 ID → (アクターエンティティ, スクリプトハンドル群) の対応表。
        // 物理スレッドの entity_id は collect_physics_objects と同じ DFS 順で振られる。
        let map = build_dfs_script_map(&scene.actors, &scene.world, wl);

        // 実行する呼び出しへ展開する（ここで World の借用は不要になる）。
        // スクリプトを持たないアクター宛のイベントはスキップする。
        let mut invocations: Vec<(Arc<ScriptingHost>, isize, i32, Entity, Option<Entity>)> = Vec::new();
        for (self_id, other_id, kind) in pairs {
            let Some((self_entity, handles)) = map.get(&self_id) else { continue };
            let other_entity = map.get(&other_id).map(|(e, _)| *e);
            for (host, handle) in handles {
                invocations.push((Arc::clone(host), *handle, kind, *self_entity, other_entity));
            }
        }
        // コールバック内から transform / Find 等のアクセサが使えるよう
        // World と Actor ツリーのポインタを公開しながら実行する。
        if !invocations.is_empty() {
            let Scene { actors, world, .. } = scene;
            with_actors(actors, || {
                with_world(world, || {
                    for (host, handle, kind, self_e, other_e) in &invocations {
                        ScriptComponent::run_physics_event_raw(host, *handle, *kind, *self_e, *other_e);
                    }
                });
            });
        }

        // 公開を解除する（フェーズ外でのアクセスを防ぐ）
        publish_input(None);
        publish_physics_sender(None);
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

    /// Reparent コマンドを適用する: アクターを新しい親の末尾の子へ移動する
    /// （new_parent = None ならシーンのルートへ）。
    ///
    /// 判定（存在・循環・2D/3D 種別整合）は `reparent_actor_by_entity` に集約しており、
    /// エディタの `handle_reparent_actor` と同じルールで動く。拒否された場合は
    /// ツリーを一切変更せず `[Script]` 警告を出す（no-op）。
    ///
    /// # 変換について
    /// - 3D: 子アクターの Transform はワールド空間で保持されるため、付け替えで
    ///   ワールド変換は保たれる（値を触る必要がない）。
    /// - 2D: CanvasTransform は親相対なので、ローカル値をそのまま維持する
    ///   （＝画面上の位置は新しい親を基準に変わる）。
    ///
    /// # キャッシュの無効化
    /// - JointAttach の子孫相対ローカルキャッシュは「どの祖先に付いていたか」を前提に
    ///   採取されているため、移動したサブツリーぶんだけ破棄する（放置すると、後で
    ///   元の親へ戻したときに古い相対位置で貼り付いてしまう）。
    /// - DFS 順 ID（物理・スクリプトの対応表）は毎フレーム構築されるため無効化不要。
    /// - canvas_world_lines は「その世界線に 2D アクターが存在するか」だけを表し、
    ///   付け替えでは増減しないため更新不要（Instantiate 経路のみが更新する）。
    fn apply_script_reparent(&mut self, entity: Entity, new_parent: Option<Entity>) {
        // キャンバス編集タブのルート（トップレベル唯一のアクター）は移動させない。
        // 移動できてしまうと handle_edit_canvas_end の前提（ルート 1 体）が壊れる。
        if self.is_script_reparent_reserved_root(entity) {
            eprintln!("[Script] SetParent 拒否: キャンバス編集中のルートは移動できません");
            return;
        }

        // 移動対象サブツリーの entity を控える（JointAttach キャッシュ破棄に使う）
        let moved: Vec<Entity> = {
            let Some(scene) = self.scene.as_ref() else { return };
            match find_actor_by_entity(&scene.actors, entity) {
                Some(a) => {
                    let mut v = Vec::new();
                    collect_actor_entities_for_cache(a, &mut v);
                    v
                }
                None => {
                    eprintln!("[Script] SetParent 拒否: 対象アクターがシーンに存在しません");
                    return;
                }
            }
        };

        {
            let Some(scene) = self.scene.as_mut() else { return };
            if let Err(rej) = reparent_actor_by_entity(&mut scene.actors, entity, new_parent) {
                eprintln!("[Script] SetParent 拒否: {}", rej.message());
                return;
            }
        }

        // JointAttach の相対ローカルキャッシュから移動したアクターぶんを破棄する
        self.joint_attach_child_locals
            .retain(|(_, child), _| !moved.contains(child));

        // エディタのヒエラルキー表示は apply_script_scene_commands が
        // コマンド適用の最後に send_hierarchy() でまとめて同期する（ここでは呼ばない）。
    }

    /// 対象アクターが「キャンバス編集タブのルート」かどうかを判定する。
    ///
    /// canvas_edit_sessions に登録された world_line ではトップレベルアクターが
    /// 常に 1 体だけという不変条件があり（is_canvas_edit_root と同じ前提）、
    /// その 1 体が対象かどうかを Entity で照合する。
    fn is_script_reparent_reserved_root(&self, entity: Entity) -> bool {
        let Some(scene) = self.scene.as_ref() else { return false };
        scene.actors.iter().any(|a| {
            a.entity == entity && self.canvas_edit_sessions.contains_key(&a.world_line)
        })
    }
}

/// アクター自身と全子孫のルートエンティティを集める（キャッシュ無効化の対象範囲）。
fn collect_actor_entities_for_cache(actor: &Actor, out: &mut Vec<Entity>) {
    out.push(actor.entity);
    for c in actor.children() {
        collect_actor_entities_for_cache(c, out);
    }
}

// ─── 物理イベントの正規化（種別変換＋対称配送）───────────────

/// 3D 衝突フェーズをスクリプトへ渡すイベント種別へ変換する。
fn collision_event_kind(phase: CollisionPhase) -> i32 {
    match phase {
        CollisionPhase::Enter => PHYSICS_EVENT_COLLISION_ENTER,
        CollisionPhase::Stay  => PHYSICS_EVENT_COLLISION_STAY,
        CollisionPhase::Exit  => PHYSICS_EVENT_COLLISION_EXIT,
    }
}

/// 3D トリガーフェーズをスクリプトへ渡すイベント種別へ変換する。
fn trigger_event_kind(phase: TriggerPhase) -> i32 {
    match phase {
        TriggerPhase::Enter => PHYSICS_EVENT_TRIGGER_ENTER,
        TriggerPhase::Stay  => PHYSICS_EVENT_TRIGGER_STAY,
        TriggerPhase::Exit  => PHYSICS_EVENT_TRIGGER_EXIT,
    }
}

/// 2D 衝突フェーズをスクリプトへ渡すイベント種別へ変換する（3D と同一の種別を使う）。
fn collision2d_event_kind(phase: crate::engine::physics::CollisionPhase2d) -> i32 {
    use crate::engine::physics::CollisionPhase2d;
    match phase {
        CollisionPhase2d::Enter => PHYSICS_EVENT_COLLISION_ENTER,
        CollisionPhase2d::Stay  => PHYSICS_EVENT_COLLISION_STAY,
        CollisionPhase2d::Exit  => PHYSICS_EVENT_COLLISION_EXIT,
    }
}

/// 2D トリガーフェーズをスクリプトへ渡すイベント種別へ変換する（3D と同一の種別を使う）。
fn trigger2d_event_kind(phase: crate::engine::physics::TriggerPhase2d) -> i32 {
    use crate::engine::physics::TriggerPhase2d;
    match phase {
        TriggerPhase2d::Enter => PHYSICS_EVENT_TRIGGER_ENTER,
        TriggerPhase2d::Stay  => PHYSICS_EVENT_TRIGGER_STAY,
        TriggerPhase2d::Exit  => PHYSICS_EVENT_TRIGGER_EXIT,
    }
}

/// 3D 物理イベントを（自分 DFS, 相手 DFS, 種別）へ正規化する。
/// 衝突・トリガーとも両方のアクターへ対称に通知する（Unity と同じ挙動）。
fn normalize_physics_pairs(
    collision_events: &[CollisionEvent],
    trigger_events:   &[TriggerEvent],
) -> Vec<(u64, u64, i32)> {
    let mut pairs: Vec<(u64, u64, i32)> = Vec::new();
    for ev in collision_events {
        let kind = collision_event_kind(ev.phase);
        pairs.push((ev.entity_a, ev.entity_b, kind));
        pairs.push((ev.entity_b, ev.entity_a, kind));
    }
    for ev in trigger_events {
        let kind = trigger_event_kind(ev.phase);
        pairs.push((ev.trigger_entity, ev.other_entity, kind));
        pairs.push((ev.other_entity, ev.trigger_entity, kind));
    }
    pairs
}

/// 2D 物理イベントを（自分 DFS, 相手 DFS, 種別）へ正規化する（3D と同一規約）。
fn normalize_physics2d_pairs(
    collision_events: &[crate::engine::physics::CollisionEvent2d],
    trigger_events:   &[crate::engine::physics::TriggerEvent2d],
) -> Vec<(u64, u64, i32)> {
    let mut pairs: Vec<(u64, u64, i32)> = Vec::new();
    for ev in collision_events {
        let kind = collision2d_event_kind(ev.phase);
        pairs.push((ev.entity_a, ev.entity_b, kind));
        pairs.push((ev.entity_b, ev.entity_a, kind));
    }
    for ev in trigger_events {
        let kind = trigger2d_event_kind(ev.phase);
        pairs.push((ev.trigger_entity, ev.other_entity, kind));
        pairs.push((ev.other_entity, ev.trigger_entity, kind));
    }
    pairs
}

// ─── DFS スクリプト対応表 ────────────────────────────────────

/// DFS 順 ID → (アクターのルートエンティティ, スクリプト (host, handle) 群) の対応表を構築する。
///
/// 走査順は collect_physics_objects（physics_ops.rs）と同一:
/// world_line が一致するルートから始まる先行順 DFS で、全アクターがカウント対象
/// （ID は 1 始まり。コライダーの有無に関係なくカウントは進む）。
fn build_dfs_script_map(
    actors: &[Actor],
    world:  &World,
    wl:     u32,
) -> HashMap<u64, (Entity, Vec<(Arc<ScriptingHost>, isize)>)> {
    /// 再帰走査の実装（counter を進めながら map へ登録する）
    fn walk(
        actor:   &Actor,
        world:   &World,
        counter: &mut u64,
        map:     &mut HashMap<u64, (Entity, Vec<(Arc<ScriptingHost>, isize)>)>,
    ) {
        *counter += 1;
        // このアクターのスクリプトスロットから (host, handle) を収集する。
        // 実効非アクティブのスクリプト（sc.active=false、Scene が毎フレーム同期）には
        // 物理イベントコールバックも配信しない（ライフサイクルと同じ扱い）。
        let mut handles = Vec::new();
        for slot in actor.slots() {
            if slot.kind == ComponentKind::Script {
                if let Some(sc) = world.get::<ScriptComponent>(slot.entity) {
                    if sc.active {
                        handles.push((Arc::clone(&sc.host), sc.handle));
                    }
                }
            }
        }
        map.insert(*counter, (actor.entity, handles));
        for child in actor.children() {
            walk(child, world, counter, map);
        }
    }

    let mut map     = HashMap::new();
    let mut counter = 0u64;
    for root in actors.iter().filter(|a| a.world_line == wl) {
        walk(root, world, &mut counter, &mut map);
    }
    map
}

// ─── テスト ──────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::physics::{
        CollisionEvent2d, CollisionPhase2d, TriggerEvent2d, TriggerPhase2d,
    };

    /// 物理イベント種別の定数が Rust / C# 双方で一意（重複なし）であること。
    /// C# 側 ScriptBridge の PhysicsEvent* 定数と 1 対 1 対応する値なので、
    /// 重複するとコールバックの取り違えが起きる。
    #[test]
    fn physics_event_kinds_are_unique() {
        let kinds = [
            PHYSICS_EVENT_COLLISION_ENTER,
            PHYSICS_EVENT_COLLISION_STAY,
            PHYSICS_EVENT_COLLISION_EXIT,
            PHYSICS_EVENT_TRIGGER_ENTER,
            PHYSICS_EVENT_TRIGGER_EXIT,
            PHYSICS_EVENT_TRIGGER_STAY,
        ];
        let mut sorted = kinds.to_vec();
        sorted.sort_unstable();
        sorted.dedup();
        assert_eq!(sorted.len(), kinds.len(), "物理イベント種別の値が重複している");
        // C# 側の定数値（0..5）と一致していること
        assert_eq!(PHYSICS_EVENT_TRIGGER_STAY, 5);
    }

    /// フェーズ → イベント種別の変換が 3D / 2D で一致していること（Stay を含む）。
    #[test]
    fn phase_to_kind_matches_between_3d_and_2d() {
        assert_eq!(collision_event_kind(CollisionPhase::Enter), collision2d_event_kind(CollisionPhase2d::Enter));
        assert_eq!(collision_event_kind(CollisionPhase::Stay),  collision2d_event_kind(CollisionPhase2d::Stay));
        assert_eq!(collision_event_kind(CollisionPhase::Exit),  collision2d_event_kind(CollisionPhase2d::Exit));
        assert_eq!(trigger_event_kind(TriggerPhase::Enter), trigger2d_event_kind(TriggerPhase2d::Enter));
        assert_eq!(trigger_event_kind(TriggerPhase::Stay),  trigger2d_event_kind(TriggerPhase2d::Stay));
        assert_eq!(trigger_event_kind(TriggerPhase::Exit),  trigger2d_event_kind(TriggerPhase2d::Exit));
        // Stay が Enter/Exit と混ざっていないこと
        assert_eq!(trigger_event_kind(TriggerPhase::Stay), PHYSICS_EVENT_TRIGGER_STAY);
    }

    /// 3D トリガー Stay が双方向（トリガー側・相手側）へ対称に配送されること。
    #[test]
    fn trigger_stay_is_dispatched_symmetrically_3d() {
        let triggers = vec![TriggerEvent {
            trigger_entity: 7, other_entity: 12, phase: TriggerPhase::Stay,
        }];
        let pairs = normalize_physics_pairs(&[], &triggers);
        assert_eq!(pairs.len(), 2);
        assert!(pairs.contains(&(7, 12, PHYSICS_EVENT_TRIGGER_STAY)));
        assert!(pairs.contains(&(12, 7, PHYSICS_EVENT_TRIGGER_STAY)));
    }

    /// 2D トリガー Stay も同様に双方向へ配送されること。
    #[test]
    fn trigger_stay_is_dispatched_symmetrically_2d() {
        let triggers = vec![TriggerEvent2d {
            trigger_entity: 3, other_entity: 4, phase: TriggerPhase2d::Stay,
        }];
        let pairs = normalize_physics2d_pairs(&[], &triggers);
        assert_eq!(pairs.len(), 2);
        assert!(pairs.contains(&(3, 4, PHYSICS_EVENT_TRIGGER_STAY)));
        assert!(pairs.contains(&(4, 3, PHYSICS_EVENT_TRIGGER_STAY)));
    }

    /// 衝突とトリガーが混在しても、各イベントが 2 ペアずつ正しい種別で並ぶこと。
    #[test]
    fn mixed_events_are_normalized_with_correct_kinds() {
        let collisions = vec![
            CollisionEvent { entity_a: 1, entity_b: 2, phase: CollisionPhase::Stay },
        ];
        let triggers = vec![
            TriggerEvent { trigger_entity: 5, other_entity: 6, phase: TriggerPhase::Enter },
            TriggerEvent { trigger_entity: 5, other_entity: 6, phase: TriggerPhase::Stay },
        ];
        let pairs = normalize_physics_pairs(&collisions, &triggers);
        assert_eq!(pairs.len(), 6, "イベント 3 件 × 双方向 = 6 ペア");
        assert!(pairs.contains(&(1, 2, PHYSICS_EVENT_COLLISION_STAY)));
        assert!(pairs.contains(&(2, 1, PHYSICS_EVENT_COLLISION_STAY)));
        assert!(pairs.contains(&(5, 6, PHYSICS_EVENT_TRIGGER_ENTER)));
        assert!(pairs.contains(&(6, 5, PHYSICS_EVENT_TRIGGER_STAY)));
    }

    /// 2D 混在ケース: 2D の衝突 Stay と トリガー Stay が同じ種別値で配送されること。
    #[test]
    fn mixed_events_are_normalized_2d() {
        let collisions = vec![
            CollisionEvent2d { entity_a: 8, entity_b: 9, phase: CollisionPhase2d::Exit },
        ];
        let triggers = vec![
            TriggerEvent2d { trigger_entity: 10, other_entity: 11, phase: TriggerPhase2d::Stay },
        ];
        let pairs = normalize_physics2d_pairs(&collisions, &triggers);
        assert_eq!(pairs.len(), 4);
        assert!(pairs.contains(&(8, 9, PHYSICS_EVENT_COLLISION_EXIT)));
        assert!(pairs.contains(&(10, 11, PHYSICS_EVENT_TRIGGER_STAY)));
    }
}
