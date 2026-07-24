// ============================================================
//  physics_ops.rs — 物理システムのライフサイクル管理
//
//  【責務】
//    start_physics()  : Play 開始時（または編集時物理 ON 時）に物理スレッドを起動し、
//                       シーン内の全 Collider Actor を物理ワールドに登録する
//    stop_physics()   : Play 停止時に物理スレッドを終了する
//    update_physics() : 毎フレーム呼び出し。
//                       Kinematic Transform を物理スレッドに送信し、
//                       結果（動的 Rigidbody の Transform + 衝突イベント）を受信して適用する
//
//  【編集時物理シミュレーション】
//    edit_physics_with_rigidbody=false : 全ボディを kinematic + 重力なし で起動。
//                                        衝突検出のみ行い、Transform は更新しない。
//    edit_physics_with_rigidbody=true  : 通常の Play 物理と同様（重力・ダイナミクスあり）。
//
//  【フロー】
//    メインスレッド (Play/EditPhysics フレーム毎)
//      ① 物理結果受信 → Transform を ECS World に反映（Play or edit+rigidbody 時のみ）
//      ② 衝突イベント → C# スクリプトのコールバック呼び出し（ScriptingHost）
//      ③ 衝突中エンティティセット更新（コライダーワイヤー色変更用）
//      ④ Kinematic Actor の最新 Transform を物理スレッドへ送信
//
//  【クォータニオン規約】
//    SEED 内部・物理型ともに [x, y, z, w] 配列を使用する。
//    オイラー角（度）との変換には engine::structs::transforms::Quaternion を使う。
// ============================================================

use crate::engine::components::{
    ColliderComponent, Transform as ActorTransform, ComponentKind, ModelComponent,
};
use crate::engine::structs::objects::actor::Actor;
use crate::engine::structs::tensor::Vector3 as SeedVec3;
use crate::engine::structs::transforms::Quaternion as SeedQuat;
use crate::engine::physics::{
    PhysicsThread, PhysicsCommand, PhysicsObject,
};
use crate::engine::core::app_base::scene::Scene;
use super::{App, RuntimeMode, InspectorTransformDrag};

/// ドラッグ押し戻しの同期オーバーラップ問い合わせのタイムアウト（ミリ秒）。
///
/// 物理スレッドはこのクエリをループ先頭（コマンドドレイン）で即応するため、
/// 健全時は 1ms 未満で返る。応答がない＝スレッドが重いステップ実行中で忙しい場合、
/// この時間だけレンダースレッドを同期ブロックしてしまう。
///
/// 【実測に基づく短縮（20ms → 4ms）】
///   高負荷時（2D 物理 + スナップショット記録と競合）に物理スレッドが 20ms 以内に
///   応答できず、[PERF] ログで 3d=40ms のフレームスパイクが観測された。40ms という
///   ほぼ一定値は「固定タイムアウト待ち」の証拠で、Windows の既定タイマ粒度(約15.6ms)が
///   recv_timeout(20ms) を実測約 40ms に膨張させていた。
///   タイムアウトを 4ms に短縮することで最悪スタール時間を大幅に抑える。押し戻しは
///   タイムアウト時に「今フレームは保留（未検証位置を安全位置に採用しない）」で安全側に
///   倒れ、次フレームで最新の提案位置を検証し直すため、稀に 1 フレーム壁へめり込むだけで
///   トンネリング（すり抜け）は起きない。健全時（大多数）は 1ms 未満応答で挙動不変。
const OVERLAP_REPLY_TIMEOUT_MS: u64 = 4;

/// キャラクターコントローラー同期解決（ResolveCharacter）の reply 待ちタイムアウト（ms）。
///
/// ドラッグ押し戻し（4ms）と違い、キャラ解決は「補正後位置を必ず ECS へ反映する」ことが
/// 目的で、タイムアウトすると貫通位置がそのまま描画されてしまう（＝諦められない）。
///
/// 【即応化により 40ms → 16ms へ短縮】
///   以前は物理スレッドが待機を `std::thread::sleep(1ms)` で行っており、Windows のタイマ
///   粒度（約15.6ms）により実際には約 15ms 眠るため、コマンドのドレイン間隔が ~15ms に
///   なり、40ms でも取りこぼす（実測で 100% タイムアウト）ケースがあった。
///   物理スレッドの待機を `cmd_rx.recv_timeout` 化（thread.rs 参照）してコマンド到着で
///   即起きる構造に変えたため、ResolveCharacter は健全時 <1ms で処理・返信される。
///   よってタイムアウトは物理 1 ステップ相当の 16ms で十分（それでも取りこぼすなら物理
///   スレッドが重いステップ実行中で忙しい＝別要因）。最悪スタール時間も 40ms → 16ms に縮む。
const CHAR_RESOLVE_REPLY_TIMEOUT_MS: u64 = 16;

/// 物理の毎フレーム診断ログ（`[Physics]` 行）を出力するかどうか。既定オフ。
/// プロファイル/デバッグしたいときのみ環境変数 SEED_PHYS_LOG を設定して有効化する。
///
/// エディタはランタイムの stderr をパイプで取り込むため、毎フレーム大量に eprintln! すると
/// パイプバッファが詰まってランタイム側の書き込みが数十 ms ブロックし、Play 開始直後に
/// FPS が激減する（開始 120 フレームで顕著）。既定で出力しないことでこれを防ぐ。
static PHYS_LOG_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var_os("SEED_PHYS_LOG").is_some());

/// キャラクターコントローラー同期解決の診断ログ（`[CharCtl]` 行）を出すか。
/// 【一時診断】キャラクター貫通バグの切り分けのため、env 変数の有無に関わらず常時 ON。
/// （洪水は CHAR_LOG_MAX_FRAMES=240 フレームの上限で防ぐ。原因特定後に env ゲートへ戻す）
/// 物理スレッド側（thread.rs）も同様に `[CharCtl]` 行を出し、1 フレームのキャラ解決を切り分ける。
static CHAR_LOG_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| true || std::env::var_os("SEED_CHAR_LOG").is_some());

/// これまでに出力した `[CharCtl]`（メイン側）行のフレーム数（洪水防止の上限判定に使う）。
static CHAR_LOG_FRAMES: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

/// メイン側 `[CharCtl]` 行を出す最大フレーム数（起動直後の切り分けに十分な数だけ出す）。
const CHAR_LOG_MAX_FRAMES: u32 = 240;

impl App {
    // ─── 起動 ────────────────────────────────────────────────────

    /// 物理スレッドを起動し、シーン内の全 Collider Actor を物理ワールドに登録する。
    ///
    /// Play モード: 通常の物理シミュレーション（重力・ダイナミクスあり）。
    /// Edit モード (edit_physics_with_rigidbody=false):
    ///   全ボディを kinematic にして重力を無効化し、衝突検出のみ行う。
    /// Edit モード (edit_physics_with_rigidbody=true):
    ///   通常と同様（重力・ダイナミクスあり）。
    pub(super) fn start_physics(&mut self) {
        let Some(scene) = &self.scene else { return };

        // 編集時コライダーのみモード: 全ボディを kinematic 扱い
        let force_kinematic = self.mode == RuntimeMode::Edit
            && !self.edit_physics_with_rigidbody;

        let thread = PhysicsThread::spawn();

        // シーン内の全 Actor を走査して ColliderComponent を持つものを収集・登録する。
        // タブ復帰時（pending_restore_vel_3d が Some）は該当 Entity の初速を上書きして
        // 「続き」の速度で再開する。それ以外の呼び出しでは None なので初速なし。
        let objects = collect_physics_objects(
            scene, self.active_world_line, force_kinematic,
            self.pending_restore_vel_3d.as_ref(),
        );
        if *PHYS_LOG_ENABLED {
            eprintln!("[Physics] start_physics: {} objects collected (world_line={}, force_kinematic={})",
                objects.len(), self.active_world_line, force_kinematic);
            for obj in &objects {
                eprintln!("[Physics]   entity_id={} pos=({:.3},{:.3},{:.3}) rigidbody={} kinematic={}",
                    obj.entity_id, obj.position[0], obj.position[1], obj.position[2],
                    obj.rigidbody.is_some(),
                    obj.rigidbody.as_ref().map_or(false, |rb| rb.is_kinematic));
            }
        }
        for obj in objects {
            thread.send(PhysicsCommand::AddObject(obj));
        }

        // コライダーのみモードでは重力を無効化する
        if force_kinematic {
            thread.send(PhysicsCommand::SetGravity { gravity: [0.0, 0.0, 0.0] });
        }

        self.physics_thread = Some(thread);

        // 地形の静的トライメッシュコライダーを物理ワールドへ登録する。
        // 地形は ECS の ColliderComponent を持たない（terrain 側で内部管理する）ため、
        // 通常アクターの収集（collect_physics_objects）とは別経路でここから登録する。
        self.register_all_terrain_colliders();
    }

    // ─── 停止 ────────────────────────────────────────────────────

    /// Play モード停止時に物理スレッドを終了する（Drop で Stop 送信・join）。
    pub(super) fn stop_physics(&mut self) {
        self.physics_thread = None;
    }

    // ─── 毎フレーム更新 ──────────────────────────────────────────

    /// 物理スレッドから最新結果を受信し、ECS と衝突状態を同期する。
    ///
    /// 1. 物理スレッドの最新結果を受信して Transform を書き戻す
    ///    （Edit モードのコライダーのみ設定時は Transform 更新をスキップ）
    /// 2. 衝突イベントを C# スクリプトへ通知する
    /// 3. 衝突中エンティティ DFS ID セットを更新する（コライダー色変更用）
    /// 4. Kinematic Actor の Transform を物理スレッドへ送信する
    pub(super) fn update_physics(&mut self) {
        // 物理スレッドが未起動の場合はスキップする。
        // Play モードの初回起動は handle_redraw_requested() のフレーム末尾で行う。
        // （フレーム先頭で起動すると初回 GPU 処理の間に物理が先行してしまうため）
        // ※ ドラッグ状態変化ブロック内で self の可変メソッド（自動再開）を呼ぶため、
        //   thread はここでは束縛せず、送信のたびに狭いスコープで借用する。
        if self.physics_thread.is_none() { return; }

        // 診断ログ（最初の 120 フレームのみ）
        static DIAG_FRAME: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
        let diag_n = DIAG_FRAME.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        // 既定オフ（SEED_PHYS_LOG 設定時のみ）。毎フレーム eprintln! による stderr パイプ
        // 詰まりで Play 開始直後に FPS が落ちるのを防ぐ。
        let diag = *PHYS_LOG_ENABLED && diag_n < 120;

        // edit コライダーのみモードでは Transform 更新をスキップする
        let should_apply_transforms = self.mode != RuntimeMode::Edit
            || self.edit_physics_with_rigidbody;

        // ── ギズモドラッグ中アクターの kinematic 切り替え ──────────
        // ビューポートのギズモドラッグ中は self.drag.gizmo_drag が Some になり、
        // 選択中アクターの DFS ID は self.actor_virtual_selected_idx（0-indexed）に入っている。
        // 物理 entity_id は 1-indexed なので +1 して変換する。
        // 2D アクター・Canvas アクターは物理を持たないため対象外とする。
        let new_drag_entity_id: Option<u64> = if self.drag.gizmo_drag.is_some() {
            // ビューポートギズモドラッグ: 選択中アクターを kinematic 化
            self.actor_virtual_selected_idx.map(|dfs_id| dfs_id as u64 + 1)
        } else {
            // インスペクタ経由の Transform ドラッグ（BeginTransformDrag IPC）
            match &self.inspector_transform_drag {
                Some(InspectorTransformDrag::Actor     { dfs_id, .. }) |
                Some(InspectorTransformDrag::ActorGroup{ dfs_id, .. }) => Some(*dfs_id as u64 + 1),
                _ => None,
            }
        };

        if new_drag_entity_id != self.dragging_physics_entity_id {
            // 【変更3(b)フォールバック】ドラッグ終了自動再開の判定。
            // RigidBody タイムラインモードで最新フレーム停止中に、Collider を持つアクターの
            // ドラッグが「停止したまま」終了した場合、「最終フレームの続き」として物理を自動再開する。
            // 通常はドラッグ開始時にライブシミュレーション（下記 start_live_sim）が始まって
            // paused=false になるため、この分岐はユーザーがドラッグ中に手動で一時停止した場合等の
            // フォールバック。過去フレームへシーク中（!at_latest）は誤爆防止で対象外。
            let drag_ended = self.dragging_physics_entity_id.is_some()
                && new_drag_entity_id.is_none();
            let auto_resume = drag_ended
                && self.mode == RuntimeMode::Edit
                && self.edit_physics_enabled
                && self.edit_physics_with_rigidbody
                && self.edit_physics_paused
                && self.edit_physics_at_latest
                // Collider を持たないアクターの移動では物理を再起動しない（無意味な再構築を防ぐ）
                && self.dragging_physics_entity_id.map_or(false, |id| {
                    self.scene.as_ref().map_or(false, |s| {
                        actor_has_enabled_collider(s, self.active_world_line, id)
                    })
                });

            // ドラッグ状態が変化した場合にボディタイプを切り替える
            if let Some(old_id) = self.dragging_physics_entity_id {
                // ドラッグ終了: 現在のアクター座標を final_position として渡し Dynamic に戻す。
                // 【ドラッグ中ライブシミュレーション】が走っている場合は、この Dynamic 復帰
                // だけで演算がそのまま継続する（物理スレッドの再起動は行わない）。
                // 自動再開（フォールバック）の場合は直後に stop→start で作り直すため、
                // この SetBodyKinematic 送信は省略してムダな再構築を避ける。
                if !auto_resume {
                    let final_position = self.scene.as_ref().and_then(|s| {
                        get_actor_transform_by_entity_id(s, self.active_world_line, old_id)
                    });
                    if let Some(thread) = &self.physics_thread {
                        thread.send(PhysicsCommand::SetBodyKinematic {
                            entity_id: old_id,
                            is_kinematic: false,
                            final_position,
                            // Dynamic 復帰時は smooth は無視される（値は任意）
                            smooth: false,
                        });
                    }

                    // 【案B: ドラッグ解放直後の収束停止抑止】
                    // ライブシミュレーション中（paused=false）のドラッグを解放した直後は、
                    // 上の Dynamic 復帰コマンドを物理スレッドが処理して落下速度を報告するまで
                    // 最大 1 物理周期のラグがある。その窓では解放したボディの速度が 0 のまま
                    // 残留するため、収束停止（自動 Pause）に誤って掛かって落下が始まらなく
                    // なる。案A で根治済みだが、二重の保険としてフォールバック経路
                    // （resume_edit_physics_from_current_ecs）と同じウォームアップ保護を
                    // 明示的に付与し、解放直後を「移動中」とみなして停止判定をスキップさせる。
                    if self.mode == RuntimeMode::Edit
                        && self.edit_physics_enabled
                        && self.edit_physics_with_rigidbody
                        && !self.edit_physics_paused
                    {
                        self.protect_edit_physics_after_drag_release();
                    }
                }
                // ドラッグ終了: 押し戻し用の最終有効位置をクリアする
                self.drag_collider_last_valid_pos = None;
            }
            if let Some(new_id) = new_drag_entity_id {
                // ドラッグ開始: Kinematic に変更して重力・衝突力を一時停止する。
                // 現在の ECS 位置を final_position として渡すことで、接触補正などで
                // Rapier 内部位置が ECS とわずかにズレている場合でも
                // 持ち上げ開始時の回転ジャークを防ぐ。
                let ecs_start_pos = self.scene.as_ref()
                    .and_then(|s| get_actor_transform_by_entity_id(s, self.active_world_line, new_id));

                // 【ドラッグ中ライブシミュレーション】の発動判定を先に行う（smooth 指定に使う）。
                // RigidBody タイムラインモードで最新フレーム停止中に Collider 付きアクターの
                // ドラッグが開始された場合、物理を Pause 解除してドラッグ中も演算を継続する。
                // - 自身は kinematic としてギズモに追従し、力学的影響（回転・跳ね返り）を受けない
                // - 他の Dynamic ボディは Rapier の kinematic→dynamic 相互作用で押しのけられる
                // - フレームは加算され、try_record_physics_snapshot が記録・STATE 通知を継続する
                // 過去フレームへシーク中（!at_latest）は従来通り発動しない（誤爆防止）。
                let start_live_sim = self.mode == RuntimeMode::Edit
                    && self.edit_physics_enabled
                    && self.edit_physics_with_rigidbody
                    && self.edit_physics_paused
                    && self.edit_physics_at_latest
                    && self.scene.as_ref().map_or(false, |s| {
                        actor_has_enabled_collider(s, self.active_world_line, new_id)
                    });

                // ライブシミュレーション時のみ smooth=true（目標追従・速度クランプ）にして、
                // 床など可動 kinematic が乗っている Dynamic ボディを吹き飛ばすのを防ぐ。
                // 押し戻しのみモード（RigidBody 無効）では接触相手が動かず伝達問題がないため
                // false（即時反映）にする。
                if let Some(thread) = &self.physics_thread {
                    thread.send(PhysicsCommand::SetBodyKinematic {
                        entity_id: new_id,
                        is_kinematic: true,
                        final_position: ecs_start_pos,
                        smooth: start_live_sim,
                    });
                }
                // ドラッグ開始: 現在位置を押し戻し用の最終有効位置として初期化する。
                // RigidBody 有効/無効どちらのモードでもドラッグ中の押し戻し判定に使用する。
                if self.mode == RuntimeMode::Edit {
                    self.drag_collider_last_valid_pos = ecs_start_pos;
                }

                if start_live_sim {
                    self.begin_edit_physics_drag_live_sim();
                }
            }
            self.dragging_physics_entity_id = new_drag_entity_id;

            // 自動再開（フォールバック）: 現在の ECS 位置から物理を再起動して続きを開始する。
            // ここで物理スレッドは作り直されるため、このフレームの結果受信は行わず終了する。
            if auto_resume {
                self.resume_edit_physics_from_current_ecs();
                return;
            }
        }

        // ── ドラッグ中押し戻し（同期オーバーラップ判定）──────────────────────
        //
        // 【間欠バグ修正】従来は物理スレッドの非同期結果（recv_latest）に含まれる
        // 接触情報で押し戻しを判定していたが、メインフレームと物理ステップ
        // （壁時計 60Hz）は非同期のため、
        //   (a) 前フレームからステップが 1 回も回っていないフレームでは結果が None に
        //       なり判定自体がスキップされる（その間もカーソルは壁の奥へ進む）、
        //   (b) 「前フレーム末に送った位置 = 受信結果が参照した位置」という対応付けが
        //       ステップ 0 回/2 回のフレームで崩れ、未検証の壁内位置を安全位置
        //       （drag_collider_last_valid_pos）として採用してしまう、
        // の 2 点により押し戻しが間欠的にしか効かなかった。
        //
        // 現在は Raycast と同じ同期問い合わせ（CheckKinematicOverlap）で
        // 「今フレームの提案位置そのもの」を毎フレーム必ず検証するため、
        // フレーム落ち・位置対応ズレが構造的に発生しない。
        // RigidBody 有効モードのドラッグにも同じ判定を適用する（Dynamic ボディとの
        // 重なりは物理スレッド側で除外されるため、押しのけ挙動は阻害しない）。
        if self.mode == RuntimeMode::Edit {
            if let Some(drag_id) = self.dragging_physics_entity_id {
                self.apply_drag_pushback(drag_id);
            }
        }

        // 結果受信のためにここで改めて物理スレッドを借用する。
        let Some(thread) = &self.physics_thread else { return };
        let result = thread.recv_latest();

        // 【案A】このフレームで新しい物理結果を受信できたかを退避する。
        // try_record_physics_snapshot（収束停止判定）が edit_physics_results_fresh 経由で
        // 参照し、結果待ちの窓を静止と誤カウントしないようにする。update_physics は
        // try_record より前に走るため、この 3D フラグは常に同フレームの最新値になる。
        self.store_edit_physics_result_received_3d(result.is_some());

        if let Some(ref result) = result {
            if diag {
                eprintln!("[Physics] frame={} transform_updates={}", diag_n, result.transform_updates.len());
                for (id, pos, _) in &result.transform_updates {
                    eprintln!("[Physics]   entity_id={} new_pos=({:.3},{:.3},{:.3})", id, pos[0], pos[1], pos[2]);
                }
            }

            // ① Dynamic Rigidbody の Transform を ECS に書き戻す
            // ドラッグ中のアクターは gizmo が位置を制御するため物理結果を無視する
            if should_apply_transforms {
                if let Some(scene) = &mut self.scene {
                    for (entity_id, new_pos, new_rot) in &result.transform_updates {
                        if Some(*entity_id) == self.dragging_physics_entity_id { continue; }
                        apply_physics_transform(scene, self.active_world_line, *entity_id, *new_pos, *new_rot);
                    }
                }
            }

            // 【撤去】キャラクターコントローラーの補正後位置反映は、フレーム末尾
            // （スクリプトフェーズ実行後）の sync_character_controllers による同期解決へ
            // 一本化した。ここ（update_physics = フレーム先頭・スクリプト実行前）で
            // 非同期ストリーム（character_updates）を反映していた旧経路は、補正が描画前に
            // 確定せずスクリプトの貫通直書きで毎フレーム上書きされる設計欠陥だったため削除した。

            // ②-0 Play モード時: 衝突・トリガーイベントをスクリプトのコールバックへ配信する
            //（OnCollisionEnter / OnTriggerEnter 等。Edit モードの物理プレビューでは呼ばない）
            if self.mode == RuntimeMode::Play
                && (!result.collision_events.is_empty() || !result.trigger_events.is_empty())
            {
                self.dispatch_physics_events_to_scripts(
                    &result.collision_events,
                    &result.trigger_events,
                );
            }

            // ② 衝突/トリガーイベントの IPC 転送は廃止した。
            //
            // 【理由】以前は scripting_host 存在時に COLLISION_EVENT / TRIGGER_EVENT を
            //   接触継続中（Stay）も含め毎フレーム・接触数ぶんエディタへ送っていたが、
            //   エディタ側にこれらのハンドラは存在せず（EDIT_PHYSICS_STATE のみ処理）、
            //   完全に消費されない死んだトラフィックだった。物理シミュレーション中に
            //   毎秒数百件がパイプへ流れ、エディタの読み取りループを圧迫して入力応答を
            //   カクつかせていた（実測 [IPC] sent 約300-390/s のうち COLLISION_* が大半）。
            //   Play モードのスクリプト OnCollision コールバックは上の
            //   dispatch_physics_events_to_scripts でプロセス内配信済みで、IPC 転送は不要。
            //   将来エディタ側で接触表示が必要になれば、そのとき必要フェーズだけを
            //   明示的にリクエストして送る設計にする。

            // ③ 衝突中エンティティ DFS ID セットを更新する（コライダーワイヤー色変更用）
            //
            // 【修正2】色判定の情報源を毎フレームの NarrowPhase 直接クエリへ寄せる。
            // collision_events の Stay は active_contacts（イベントベース）に依存し、
            // Stopped イベントの取りこぼしで残留（接触していないのに接触色）しうる。
            // 一方 active_contact_entity_ids は has_any_active_contact() を毎フレーム
            // 問い合わせた結果であり、押し戻し判定と同一の確実な情報源。
            // トリガーは active_contact_entity_ids から除外されているため、
            // 毎フレーム送られる active_trigger_entity_ids と和集合を取る。
            let mut frame_colliding: std::collections::HashSet<u64> = std::collections::HashSet::new();
            for &eid in &result.active_contact_entity_ids {
                frame_colliding.insert(eid);
            }
            // active_trigger_entity_ids は現在オーバーラップ中の全トリガー関係を毎フレーム持つ
            for &eid in &result.active_trigger_entity_ids {
                frame_colliding.insert(eid);
            }
            self.active_collision_dfs_ids = frame_colliding;

            // 収束停止判定用に、全 Dynamic ボディの最大速度を退避する。
            // タイムライン（stop 判定）が「実際に静止したか」を速度で確認するために使う。
            self.store_edit_physics_rest_speeds_3d(result.max_linear_speed, result.max_angular_speed);

            // ⑥ タブ退避用の速度キャッシュを丸ごと更新する。
            // DFS entity_id → ECS Entity 変換をこの 1 箇所（結果受信時）に閉じ込め、
            // 以降のキャッシュ／退避／復元はすべて安定な ECS Entity をキーにする。
            // ドラッグ中エンティティ（kinematic 化・速度≈0）はスキップする。
            if let Some(scene) = &self.scene {
                let dfs_to_entity = build_dfs_entity_map_3d(scene, self.active_world_line);
                let drag = self.dragging_physics_entity_id;
                let mut cache = std::collections::HashMap::new();
                for (dfs_id, lin, ang) in &result.body_velocities {
                    if Some(*dfs_id) == drag { continue; }
                    if let Some(&ent) = dfs_to_entity.get(dfs_id) {
                        cache.insert(ent, (*lin, *ang));
                    }
                }
                self.current_vel_cache_3d = cache;
            }
        }

        // ④ Kinematic Actor の Transform を物理スレッドへ送信する
        let Some(scene) = &self.scene else { return };
        let Some(thread) = &self.physics_thread else { return };

        collect_kinematic_updates(scene, self.active_world_line)
            .into_iter()
            .for_each(|(entity_id, pos, rot)| {
                thread.send(PhysicsCommand::UpdateKinematic { entity_id, position: pos, rotation: rot });
            });

        // ⑤ ドラッグ中アクターの現在位置を kinematic 更新として送信する。
        // 一時的に Kinematic 化されているため UpdateKinematic で gizmo 位置を物理スレッドに反映する。
        // 押し戻し（apply_drag_pushback）が発動したフレームでは ECS が安全位置へ復元済みのため、
        // ここで送信されるのも検証済みの安全位置となる（送信経路はこの 1 箇所に集約）。
        if let Some(drag_entity_id) = self.dragging_physics_entity_id {
            if let Some((pos, rot)) = get_actor_transform_by_entity_id(scene, self.active_world_line, drag_entity_id) {
                thread.send(PhysicsCommand::UpdateKinematic { entity_id: drag_entity_id, position: pos, rotation: rot });
            }
        }
    }

    // ─── キャラクターコントローラー同期解決（スクリプト実行後・描画前）──────────────

    /// キャラクターコントローラーの衝突解決を **同期・フレーム 1 回** 実行し、補正後位置を
    /// ECS の確定値として描画前に書き戻す。
    ///
    /// **必ずスクリプトフェーズ実行後・描画前**に呼ぶこと（frame_renderer のスクリプト
    /// フェーズ直後）。スクリプトが Transform に直書きした希望位置（地形を貫通しうる値）を
    /// そのまま拾い、物理スレッドへ reply 付きの `ResolveCharacter` を送って前回解決済み位置
    /// との差分を KCC で衝突解決させ、**補正後位置＋接地をその場で受け取る**。
    ///
    /// 【なぜ同期・フレーム 1 回か】
    ///   旧設計は `StepCharacter` を投げっぱなし（非同期）で送り、補正後位置を次フレームの
    ///   ステップ結果で受けていた。しかし補正が描画前に確定せず、同フレームの描画は貫通位置を
    ///   読み、次フレームの補正もスクリプトの直書きで上書きされるため、実機で全く押し戻され
    ///   なかった。ここで同期解決に一本化し、受け取った補正後位置を `set_actor_world_transform`
    ///   経由で即 ECS へ書き戻すことで、スクリプトの貫通直書きを同フレーム・描画前に上書きする。
    ///   解決は「毎 move ごと」ではなく「フレーム 1 回」なので、往復コストは 1 キャラ 1 回で済む。
    ///
    /// reply のタイムアウトは既存の同期問い合わせ（ドラッグ押し戻し）と同じ作法・定数
    /// （`OVERLAP_REPLY_TIMEOUT_MS`）を流用する。タイムアウト時は今フレームの補正を諦め、
    /// ECS はスクリプト値のまま（＝従来同等）とし、`SEED_CHAR_LOG` 設定時のみログに出す。
    pub(super) fn sync_character_controllers(&mut self) {
        if self.physics_thread.is_none() { return; }

        // ① スクリプトが書いた希望位置（＋回転）をキャラごとに収集する。
        let Some(scene) = &self.scene else { return };
        let desired = collect_character_step_updates(scene, self.active_world_line);

        // 診断ログ（対象キャラ数）。SEED_CHAR_LOG 設定時のみ、最初の N 回だけ出す。
        let diag = *CHAR_LOG_ENABLED
            && CHAR_LOG_FRAMES.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
                < CHAR_LOG_MAX_FRAMES;
        if diag {
            eprintln!("[CharCtl] sync_character_controllers: collected {} character(s)", desired.len());
        }
        if desired.is_empty() { return; }

        // ② 各キャラを同期解決する。物理スレッドへ reply 付きコマンドを送り、補正後位置を
        //    受け取ってから ECS へ書き戻す。DFS ID → ECS Entity 変換は接地公開のために保持する。
        let mut grounded_states: Vec<(crate::engine::ecs::Entity, bool)> = Vec::new();
        let dfs_to_entity = build_dfs_entity_map_3d(scene, self.active_world_line);

        for (entity_id, desired_position, rotation) in desired {
            // 物理スレッドへ同期問い合わせ（Raycast / CheckKinematicOverlap と同じ reply パターン）。
            let resolved = {
                let Some(thread) = &self.physics_thread else { return };
                let (reply_tx, reply_rx) = crossbeam_channel::bounded::<Option<([f32; 3], bool)>>(1);
                thread.send(PhysicsCommand::ResolveCharacter {
                    entity_id,
                    desired_position,
                    rotation,
                    reply: reply_tx,
                });
                match reply_rx.recv_timeout(
                    std::time::Duration::from_millis(CHAR_RESOLVE_REPLY_TIMEOUT_MS),
                ) {
                    Ok(v) => v,
                    // 応答なし（スレッド高負荷・終了中等）: 今フレームの補正を諦める。
                    // ECS はスクリプト値のまま＝従来同等。次フレームで最新の希望位置を再解決する。
                    Err(_) => {
                        if diag {
                            eprintln!("[CharCtl] id={entity_id} reply timeout（今フレームの補正を諦め）");
                        }
                        continue;
                    }
                }
            };

            // 解決失敗（キャラ未登録・コライダー無し）はスクリプト値のまま据え置く。
            let Some((corrected_pos, grounded)) = resolved else { continue };

            // 補正後位置を ECS の確定値として書き戻す（子孫アクタ＝カメラ等も追従）。
            if let Some(scene) = &mut self.scene {
                apply_character_transform(scene, self.active_world_line, entity_id, corrected_pos);
            }
            // 接地状態を IsGrounded 用に集約する（DFS ID → ECS Entity 変換つき）。
            if let Some(&ent) = dfs_to_entity.get(&entity_id) {
                grounded_states.push((ent, grounded));
            }
        }

        // ③ 接地状態をスクリプトへ公開する（Physics.IsGrounded 用）。
        crate::engine::core::scripting::host_api::publish_grounded_states(grounded_states);
    }

    // ─── ドラッグ中押し戻し（同期オーバーラップ判定）──────────────────────────

    /// ドラッグ中アクターの押し戻し判定を同期実行する。
    ///
    /// 1. ギズモ／インスペクタが書き込んだ現在の ECS 位置（＝今フレームの提案位置）を取得する。
    /// 2. CheckKinematicOverlap で物理スレッドへ同期問い合わせし、提案位置で他の
    ///    非 Dynamic・非センサーコライダーとめり込むかを判定する。
    /// 3. めり込む場合: 最終有効位置（drag_collider_last_valid_pos）へ ECS を復元し、
    ///    ドラッグ開始行列をシフトしてチラつきを防止する。
    /// 4. めり込まない場合: 提案位置を検証済みの最終有効位置として採用する。
    ///
    /// 物理スレッドへの位置送信は呼び出し元（update_physics ⑤）が行う。
    fn apply_drag_pushback(&mut self, drag_id: u64) {
        // ① 提案位置 = on_cursor_moved / インスペクタ編集が書き込み済みの現在 ECS 位置
        let Some((prop_pos, prop_rot)) = self.scene.as_ref()
            .and_then(|s| get_actor_transform_by_entity_id(s, self.active_world_line, drag_id))
        else { return };

        // ② 物理スレッドへ同期問い合わせ（Raycast と同じ reply チャンネルパターン）
        let overlapping = {
            let Some(thread) = &self.physics_thread else { return };
            let (reply_tx, reply_rx) = crossbeam_channel::bounded::<bool>(1);
            thread.send(PhysicsCommand::CheckKinematicOverlap {
                entity_id: drag_id,
                position:  prop_pos,
                rotation:  prop_rot,
                reply:     reply_tx,
            });
            match reply_rx.recv_timeout(
                std::time::Duration::from_millis(OVERLAP_REPLY_TIMEOUT_MS),
            ) {
                Ok(v) => v,
                // 応答なし（スレッド高負荷・終了中等）: 今フレームは判定を保留する。
                // 未検証の位置を安全位置として採用しないため last_valid_pos は更新しない。
                Err(_) => return,
            }
        };

        if !overlapping {
            // ── めり込みなし: 提案位置を検証済みの安全位置として採用する ─────────
            self.drag_collider_last_valid_pos = Some((prop_pos, prop_rot));
            return;
        }

        // ── めり込みあり: 最終有効位置に押し戻す ─────────────────────────────
        let Some((last_pos, last_rot)) = self.drag_collider_last_valid_pos else { return };

        // ECS に安全位置を書き戻す（物理スレッドへの送信は update_physics ⑤ が行う）
        if let Some(scene) = &mut self.scene {
            apply_physics_transform(scene, self.active_world_line, drag_id, last_pos, last_rot);
        }

        // ドラッグ開始行列をシフトしてチラつきを防止する。
        //
        // 問題: カーソルが壁位置に留まる限り on_cursor_moved が毎フレーム
        //       AT を壁侵入位置に戻してしまい、押し戻しと交互にチラつく。
        //
        // 解決: 壁侵入量 offset = 提案位置 - 安全位置 を各ドラッグ開始行列の
        //       平行移動成分から引くことで、カーソル静止時の計算結果を
        //       「new_pos = delta * new_start = delta * (old_start - offset) = safe_pos」
        //       にする。これ以降カーソルを戻した方向へは自然に動く。
        let off = [
            prop_pos[0] - last_pos[0],
            prop_pos[1] - last_pos[1],
            prop_pos[2] - last_pos[2],
        ];
        // MC ドラッグ開始行列群を調整する
        for (_, sm) in &mut self.drag.drag_root_starts {
            sm[0][3] -= off[0]; sm[1][3] -= off[1]; sm[2][3] -= off[2];
        }
        for (_, sm) in &mut self.drag.drag_child_starts {
            sm[0][3] -= off[0]; sm[1][3] -= off[1]; sm[2][3] -= off[2];
        }
        for (_, mats) in &mut self.drag.actor_extra_mc_drag_starts {
            for sm in mats.iter_mut() {
                sm[0][3] -= off[0]; sm[1][3] -= off[1]; sm[2][3] -= off[2];
            }
        }
        // 子孫アクタの開始行列は MC 用・Transform 用の 2 本あるため両方を調整する
        for cs in &mut self.drag.actor_child_drag_starts {
            if let Some(sm) = &mut cs.mc_start {
                sm[0][3] -= off[0]; sm[1][3] -= off[1]; sm[2][3] -= off[2];
            }
            cs.tf_start[0][3] -= off[0];
            cs.tf_start[1][3] -= off[1];
            cs.tf_start[2][3] -= off[2];
        }
        // Transform-only ドラッグ（MC なし）の場合も調整する
        if let Some((_, start_tf)) = &mut self.drag.actor_transform_drag_start {
            start_tf.position[0] -= off[0];
            start_tf.position[1] -= off[1];
            start_tf.position[2] -= off[2];
        }
    }
}

// ─── シーン走査ユーティリティ ────────────────────────────────────────────────

/// DFS entity_id（1 始まり）→ ECS Entity の対応表を構築する。
///
/// collect_physics_objects / apply_physics_transform と同一の DFS 順（非アクティブ
/// アクターも含めてカウンタを進める）で走査するため、物理スレッドが返す entity_id を
/// そのまま安定な ECS Entity へ変換できる。速度キャッシュの取り違え防止に使う。
fn build_dfs_entity_map_3d(
    scene:      &Scene,
    world_line: u32,
) -> std::collections::HashMap<u64, crate::engine::ecs::Entity> {
    let mut map = std::collections::HashMap::new();
    let mut dfs_counter = 0u32;
    let mut stack: Vec<&Actor> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .collect();
    while let Some(actor) = stack.pop() {
        dfs_counter += 1;
        for child in actor.children.iter().rev() { stack.push(child); }
        map.insert(dfs_counter as u64, actor.entity);
    }
    map
}

/// シーン内の全 Actor を DFS 順に走査して PhysicsObject リストを生成する。
///
/// entity_id は DFS カウンタ（1 始まり）で、apply_physics_transform と同一の順序を保つ。
///
/// `force_kinematic`: true の場合、use_rigidbody=true であっても全ボディを kinematic にする。
/// 編集時コライダーのみモードで使用する（衝突検出のみ・Transform 更新なし）。
///
/// `vel_override`: Some のとき、走査中のアクター（`actor.entity`）がマップに存在すれば
/// RigidBody 生成時の初速（linear/angular velocity）をその値で上書きする。
/// タブ復帰時に「続き」の速度でシミュレーションを再開するために使う。None なら初速なし。
fn collect_physics_objects(
    scene:        &Scene,
    world_line:   u32,
    force_kinematic: bool,
    vel_override: Option<&std::collections::HashMap<crate::engine::ecs::Entity, ([f32; 3], [f32; 3])>>,
) -> Vec<PhysicsObject> {
    let mut objects      = Vec::new();
    let mut dfs_counter  = 0u32;

    // (アクター, 親までの実効アクティブ) をスタックで運ぶ（DFS 先行順を維持）
    let mut stack: Vec<(&Actor, bool)> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .map(|a| (a, true))
        .collect();

    while let Some((actor, parent_active)) = stack.pop() {
        dfs_counter += 1;
        let dfs_id = dfs_counter as u64;
        let active = parent_active && actor.active;

        for child in actor.children.iter().rev() {
            stack.push((child, active));
        }

        // 非アクティブアクターは物理シミュレーションへ登録しない
        // （DFS カウントは entity_id 整合のため上で進めている）
        if !active { continue; }

        let Some(tf) = scene.world.get::<ActorTransform>(actor.entity) else { continue };

        // Collider スロットを探す（enabled=false のスロットは対象外）
        let Some(collider_slot) = actor.slots().iter()
            .find(|s| s.kind == ComponentKind::Collider && s.enabled) else { continue };
        let Some(collider) = scene.world.get::<ColliderComponent>(collider_slot.entity) else { continue };

        let position = [tf.position[0], tf.position[1], tf.position[2]];
        let scale    = [tf.scale[0],    tf.scale[1],    tf.scale[2]];
        let rotation = euler_to_quat_arr(tf.rotation);
        let offset   = collider.offset;
        let shape    = collider.shape.to_physics_shape();

        let rigidbody = if collider.use_rigidbody {
            let mut rb = collider.to_rigidbody_state();
            // force_kinematic 時は動的ボディを kinematic に変換する
            if force_kinematic {
                rb.is_kinematic = true;
            }
            // タブ復帰時の初速上書き（Entity をキーに退避速度を積む）。
            // kinematic 時は速度が無視されるため実質 Dynamic ボディのみ有効。
            if let Some(map) = vel_override {
                if let Some((lin, ang)) = map.get(&actor.entity) {
                    rb.linear_velocity  = *lin;
                    rb.angular_velocity = *ang;
                }
            }
            Some(rb)
        } else {
            None
        };

        objects.push(PhysicsObject {
            entity_id:       dfs_id,
            position,
            rotation,
            scale,
            collider:        shape,
            collider_offset: offset,
            rigidbody,
            is_trigger:      collider.is_trigger,
            physics_layer:   collider.physics_layer,
            layer_mask:      collider.layer_mask,
            is_character_controller: collider.is_character_controller,
        });
    }
    objects
}

/// Kinematic オブジェクトの現在 Transform を DFS 順に収集する。
///
/// collect_physics_objects と同一の DFS 順を使うことで entity_id が対応する。
fn collect_kinematic_updates(
    scene:      &Scene,
    world_line: u32,
) -> Vec<(u64, [f32; 3], [f32; 4])> {
    let mut updates     = Vec::new();
    let mut dfs_counter = 0u32;

    let mut stack: Vec<&Actor> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .collect();

    while let Some(actor) = stack.pop() {
        dfs_counter += 1;
        let dfs_id = dfs_counter as u64;

        for child in actor.children.iter().rev() {
            stack.push(child);
        }

        let Some(collider_slot) = actor.slots().iter().find(|s| s.kind == ComponentKind::Collider) else { continue };
        let Some(collider) = scene.world.get::<ColliderComponent>(collider_slot.entity) else { continue };
        // キャラクターコントローラーは単純位置コピーではなく StepCharacter（KCC 差分解決）経路に
        // 一本化するため、ここでは対象外にする（二重に位置を動かさないため）。
        if collider.is_character_controller { continue; }
        if !collider.use_rigidbody || !collider.is_kinematic { continue; }

        let Some(tf) = scene.world.get::<ActorTransform>(actor.entity) else { continue };
        let position = [tf.position[0], tf.position[1], tf.position[2]];
        let rotation = euler_to_quat_arr(tf.rotation);

        updates.push((dfs_id, position, rotation));
    }
    updates
}

/// 物理スレッドから受け取った Transform を対応 Actor の ECS に書き戻す。
///
/// entity_id は collect_physics_objects と同一の DFS 順カウンタで照合する。
/// ActorTransform を更新した後、レンダラーが参照する ModelComponent の行列も同期する。
fn apply_physics_transform(
    scene:      &mut Scene,
    world_line: u32,
    entity_id:  u64,
    new_pos:    [f32; 3],
    new_rot:    [f32; 4],
) {
    let mut dfs_counter = 0u32;

    // 安全な借用のため actor の情報を先にフラット化する
    let actor_info: Vec<_> = {
        let mut stack: Vec<&Actor> = scene.actors.iter()
            .filter(|a| a.world_line == world_line)
            .rev()
            .collect();
        let mut result = Vec::new();
        while let Some(actor) = stack.pop() {
            for child in actor.children.iter().rev() { stack.push(child); }
            let model_slot = actor.slots().iter()
                .find(|s| s.kind == ComponentKind::Model)
                .map(|s| s.entity);
            result.push((actor.entity, model_slot));
        }
        result
    };

    for (actor_entity, model_slot) in actor_info {
        dfs_counter += 1;
        if dfs_counter as u64 != entity_id { continue; }

        // ActorTransform を更新し TRS 行列を計算する（スケールは物理で変化しないので維持）
        let new_mat = if let Some(tf) = scene.world.get_mut::<ActorTransform>(actor_entity) {
            tf.position = new_pos;
            tf.rotation = quat_arr_to_euler_deg(new_rot);
            tf.to_mat4()
        } else {
            return;
        };

        // ModelComponent の行列を更新し GPU キャッシュを無効化する
        if let Some(slot_entity) = model_slot {
            if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
                if let Some(m) = mc.instance_mats.first_mut() {
                    *m = new_mat;
                }
                if let Some(batch) = mc.instanced_batch.as_mut() {
                    batch.mark_dirty();
                }
            }
        }
        return;
    }
}

/// キャラクターコントローラーの現在 Transform（希望位置・回転）を DFS 順に収集する。
///
/// collect_physics_objects と同一の DFS 順を使うことで entity_id が対応する。
/// is_character_controller=true かつ有効な Collider スロットを持つアクターのみが対象。
/// 収集した希望位置は StepCharacter として物理スレッドへ送られ、前回位置との差分が
/// KCC で衝突解決される。
fn collect_character_step_updates(
    scene:      &Scene,
    world_line: u32,
) -> Vec<(u64, [f32; 3], [f32; 4])> {
    let mut updates     = Vec::new();
    let mut dfs_counter = 0u32;

    let mut stack: Vec<&Actor> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .collect();

    while let Some(actor) = stack.pop() {
        dfs_counter += 1;
        let dfs_id = dfs_counter as u64;

        for child in actor.children.iter().rev() {
            stack.push(child);
        }

        // 有効な Collider スロットを持ち、キャラクターコントローラー指定のアクターのみ対象
        let Some(collider_slot) = actor.slots().iter()
            .find(|s| s.kind == ComponentKind::Collider && s.enabled) else { continue };
        let Some(collider) = scene.world.get::<ColliderComponent>(collider_slot.entity) else { continue };
        if !collider.is_character_controller { continue; }

        let Some(tf) = scene.world.get::<ActorTransform>(actor.entity) else { continue };
        let position = [tf.position[0], tf.position[1], tf.position[2]];
        let rotation = euler_to_quat_arr(tf.rotation);

        updates.push((dfs_id, position, rotation));
    }
    updates
}

/// キャラクターコントローラーの補正後ワールド位置を対応 Actor の ECS に書き戻す。
///
/// entity_id は collect_physics_objects と同一の DFS 順カウンタで照合する。
/// apply_physics_transform（自 Model スロットのみ更新）と異なり、集約関数
/// set_actor_world_transform を経由して子孫アクタ（カメラ等）まで伝播させる。
/// 回転・スケールは既存値を維持し、位置だけ差し替える。
fn apply_character_transform(
    scene:      &mut Scene,
    world_line: u32,
    entity_id:  u64,
    new_pos:    [f32; 3],
) {
    // DFS 順で対象アクターの参照を探す（roots は world_line でフィルタ、全子孫をカウント）。
    // 見つけた &Actor は scene.actors を借用し、後段の scene.world 可変借用とは別フィールドの
    // ため共存できる。
    fn walk<'a>(actors: &'a [Actor], counter: &mut u64, target: u64) -> Option<&'a Actor> {
        for a in actors {
            *counter += 1;
            if *counter == target { return Some(a); }
            if let Some(found) = walk(a.children(), counter, target) { return Some(found); }
        }
        None
    }
    let target_actor: Option<&Actor> = {
        let mut counter = 0u64;
        let mut found: Option<&Actor> = None;
        for root in scene.actors.iter().filter(|a| a.world_line == world_line) {
            counter += 1;
            if counter == entity_id { found = Some(root); break; }
            if let Some(f) = walk(root.children(), &mut counter, entity_id) { found = Some(f); break; }
        }
        found
    };
    let Some(actor) = target_actor else { return };

    // 位置だけ差し替えた新しいワールド Transform を作る（回転・スケールは維持）
    let cur = scene.world.get::<ActorTransform>(actor.entity).cloned().unwrap_or_default();
    let mut next = cur;
    next.position = new_pos;

    // 集約関数経由で反映（自 instance_mats・全子孫アクタへ伝播）。
    // child_dfs_start はスクリプト／物理経路では Undo を記録しないため 0 でよい。
    crate::engine::core::transform_sync::set_actor_world_transform(actor, &mut scene.world, next, 0);
}

// ─── 数学ユーティリティ ──────────────────────────────────────────────────────

/// YXZ オイラー角（度）を SEED のクォータニオン [x, y, z, w] に変換する。
fn euler_to_quat_arr(euler_deg: [f32; 3]) -> [f32; 4] {
    let euler_rad = SeedVec3::new(
        euler_deg[0].to_radians(),
        euler_deg[1].to_radians(),
        euler_deg[2].to_radians(),
    );
    let q = SeedQuat::from_euler(euler_rad);
    [q.x, q.y, q.z, q.w]
}

/// SEED のクォータニオン [x, y, z, w] を YXZ オイラー角（度）に変換する。
fn quat_arr_to_euler_deg(q_arr: [f32; 4]) -> [f32; 3] {
    let q = SeedQuat::new(q_arr[0], q_arr[1], q_arr[2], q_arr[3]);
    let euler = q.to_euler();
    [euler.x.to_degrees(), euler.y.to_degrees(), euler.z.to_degrees()]
}

/// 物理 entity_id（1-indexed DFS）に対応するアクターが有効な Collider スロットを持つかを返す。
///
/// ドラッグ中ライブシミュレーション開始・ドラッグ終了自動再開のトリガー判定に使用する。
/// Collider を持たないアクターの移動では物理の再開・再起動を行わない。
fn actor_has_enabled_collider(
    scene:      &Scene,
    world_line: u32,
    entity_id:  u64,
) -> bool {
    let mut dfs_counter = 0u32;
    let mut stack: Vec<&Actor> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .collect();
    while let Some(actor) = stack.pop() {
        dfs_counter += 1;
        for child in actor.children.iter().rev() { stack.push(child); }
        if dfs_counter as u64 != entity_id { continue; }
        return actor.slots().iter()
            .any(|s| s.kind == ComponentKind::Collider && s.enabled);
    }
    false
}

/// 物理 entity_id（1-indexed DFS）に対応するアクターの現在 Transform を取得する。
///
/// ドラッグ中アクターの gizmo 位置を物理スレッドへ送信するために使用する。
fn get_actor_transform_by_entity_id(
    scene:      &Scene,
    world_line: u32,
    entity_id:  u64,
) -> Option<([f32; 3], [f32; 4])> {
    let mut dfs_counter = 0u32;
    let mut stack: Vec<&crate::engine::structs::objects::actor::Actor> = scene.actors.iter()
        .filter(|a| a.world_line == world_line)
        .rev()
        .collect();
    while let Some(actor) = stack.pop() {
        dfs_counter += 1;
        for child in actor.children.iter().rev() { stack.push(child); }
        if dfs_counter as u64 != entity_id { continue; }
        if let Some(tf) = scene.world.get::<ActorTransform>(actor.entity) {
            return Some((
                [tf.position[0], tf.position[1], tf.position[2]],
                euler_to_quat_arr(tf.rotation),
            ));
        }
    }
    None
}
