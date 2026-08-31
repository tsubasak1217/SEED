// ============================================================
//  ipc_handler.rs — IPC コマンド受信・ディスパッチ
//
//  process_ipc() はすべての IpcCommand を受け取り、
//  対応する App メソッドへルーティングする。
// ============================================================

use crate::engine::core::app_base::ipc::IpcCommand;
use crate::engine::core::app_base::scene::{Scene, DebugCameraData, CanvasCameraData};
use crate::engine::core::app_base::app::RuntimeMode;
use crate::engine::core::app_base::undo::TransformCommand;
use crate::engine::components::{ModelComponent, GroupMeta, GROUP_ID_BASE};
use winit::event_loop::ActiveEventLoop;

use super::{
    App,
    find_actor_by_dfs,
    release_window_clamp,
};
use super::field_edit::{FieldEditTarget, field_edit_target, is_recording_suppressed};

// ── フレーム途絶中の IPC ポンプ（about_to_wait 経路）のチューニング定数 ──────────
//
// 【背景】埋め込みランタイムの子ウィンドウがエディタの別ドキュメントタブ（例: .wgsl の
// スクリプトエディタ）の裏へ隠れると、OS は WM_PAINT を配送しなくなり winit の
// RedrawRequested が止まる。IPC の処理はフレーム内（handle_redraw_requested →
// process_ipc）でしか走らないため、この間に届いた VALIDATE_WGSL 等が
// キューに積まれたまま処理されず、エディタ側が応答タイムアウトする。
// そこで about_to_wait（イベントループは回っている）から IPC だけをポンプする。

/// 「フレームループが止まっている」とみなす、最終フレームからの無更新時間 [ms]。
/// 通常描画中（60FPS = 約 16ms 間隔）では決して超えないため、
/// 表示中は about_to_wait 側のポンプが自然に不発になる値を選ぶ。
const IPC_PUMP_FRAME_STALL_MS: u64 = 100;

/// フレーム途絶中に IPC をポンプする最小間隔 [ms]。
/// ControlFlow::Poll のため about_to_wait は毎秒数十万回呼ばれる。
/// 時間ゲートを入れて、およそ 60Hz 相当までポンプ頻度を落とす。
const IPC_PUMP_INTERVAL_MS: u64 = 16;

impl App {
    /// フレームループが止まっている間だけ、イベントループ側から IPC を処理する。
    ///
    /// winit の `about_to_wait` から毎周呼ばれる。以下をすべて満たすときだけ
    /// `process_ipc` を 1 回走らせる:
    ///   1. ウィンドウ・レンダラーが初期化済み（`resumed` 完了後）であること。
    ///      未初期化のまま IPC ハンドラを走らせると、描画資源前提のハンドラが壊れる。
    ///   2. 最後のフレームから `IPC_PUMP_FRAME_STALL_MS` 以上経過していること
    ///      （＝ RedrawRequested が配送されていない＝描画が止まっている）。
    ///   3. 前回のポンプから `IPC_PUMP_INTERVAL_MS` 以上経過していること（スピン抑制）。
    ///
    /// 処理後に `request_redraw` を 1 回出しておく。非表示中は配送されないため無害だが、
    /// 表示が復帰した瞬間に最新状態で再描画されるようにする効果がある。
    /// ただし IPC で `Stop` を受けて `event_loop.exit()` 済みの場合は要求しない。
    pub(super) fn pump_ipc_while_frames_stalled(&mut self, event_loop: &ActiveEventLoop) {
        // 1. 初期化前は何もしない（resumed 前に about_to_wait が来る場合がある）。
        if self.window.is_none() || self.renderer.is_none() {
            return;
        }

        // 2. フレームがまだ回っているなら通常経路（フレーム内 process_ipc）に任せる。
        let stalled_ms = self.last_frame_at.elapsed().as_millis() as u64;
        if stalled_ms < IPC_PUMP_FRAME_STALL_MS {
            return;
        }

        // 3. スピン抑制の時間ゲート。
        if (self.last_ipc_pump_at.elapsed().as_millis() as u64) < IPC_PUMP_INTERVAL_MS {
            return;
        }
        self.last_ipc_pump_at = std::time::Instant::now();

        // 描画に依存しないコマンド（WGSL 検証・各種 SET・保存要求等）をここで消化する。
        self.process_ipc(event_loop);

        // 表示復帰時に最新状態で描かれるよう再描画を要求する（非表示中は配送されず無害）。
        if !event_loop.exiting() {
            if let Some(window) = &self.window {
                window.request_redraw();
            }
        }
    }


    /// IPC コマンドをすべて収集してから順に処理する。
    ///
    /// &self.ipc の不変借用を処理ループ内に持ち込まないことで、
    /// apply_delete 等の &mut self メソッド呼び出しを可能にする。
    ///
    /// 【フレーム末の集約処理】コマンドループを抜けた後に
    /// `flush_terrain_pending_remesh` を必ず呼ぶ。ドラッグ中は 1 フレームに複数の
    /// TERRAIN_BRUSH / TERRAIN_PAINT が届くため、各ハンドラは密度・スプラットの
    /// 書き換えだけを即時に行い、重い再メッシュはここで 1 回にまとめて消化する。
    ///
    /// IPC が未接続（`self.ipc == None`。スモークテスト等のスタンドアロン実行）でも
    /// **早期 return せず** flush まで到達させること。地形編集はフックから直接
    /// 呼ばれる経路もあり、そこで積まれた保留が永久に消化されなくなるため。
    pub(super) fn process_ipc(&mut self, event_loop: &ActiveEventLoop) {
        let cmds: Vec<_> = match &self.ipc {
            Some(ipc) => std::iter::from_fn(|| ipc.try_recv()).collect(),
            None => Vec::new(),
        };
        for cmd in cmds {
            // ── インスペクタのフィールド編集を Undo 履歴へ載せる（汎用機構） ──
            //   コマンドを分類し、対象があれば適用「前」の値をスナップショットしておく。
            //   個別ハンドラには一切手を入れず、この 1 箇所で全 SET_* 経路を拾う
            //   （詳細と対象外の判断根拠は field_edit.rs）。
            //   Play 中はシーンの編集ではなく実行時の一時変更なので記録しない。
            let fe_target = if is_recording_suppressed(self.mode) {
                FieldEditTarget::None
            } else {
                field_edit_target(&cmd)
            };
            let fe_before = self.field_edit_snapshot(&fe_target);

            match cmd {
                IpcCommand::CtrlDown           => self.ctrl_held = true,
                IpcCommand::CtrlUp             => self.ctrl_held = false,
                IpcCommand::Pause => {
                    // Play モード中にメインカメラが存在する場合、
                    // デバッグカメラをその視点に同期してから Pause に入る。
                    if self.mode == RuntimeMode::Play {
                        self.sync_debug_camera_to_main_camera();
                    }
                    self.paused = true;
                }
                IpcCommand::Resume             => self.paused = false,
                // AI 実行中レンダリング停止（GPU リソースを LLM に解放するため）
                IpcCommand::PauseRender        => self.render_paused = true,
                // AI 応答完了後にレンダリングを再開する
                IpcCommand::ResumeRender       => self.render_paused = false,
                // 未保存のセーブデータを書き出してから終了する（明示 Save() の取りこぼし対策）。
                IpcCommand::Stop               => {
                    crate::engine::core::save::flush_if_dirty();
                    event_loop.exit();
                }
                // 内蔵デバッガのアタッチ/デタッチに応じてブレークポイント停止ガードを切り替える。
                IpcCommand::SetDebugGuard(v)   => self.clock.set_debug_guard(v),
                IpcCommand::CamKeyDown(k)      => self.cam_input.set_key(&k, true),
                IpcCommand::CamKeyUp(k)        => self.cam_input.set_key(&k, false),
                // Play 切替等でキー UP が届かずスタックした移動キーを一括リセットする。
                // スタックすると「RMB 押下だけでカメラが滑る」「軸スナップが即キャンセル
                // される（any_move_key が真のため）」という不具合になる。
                IpcCommand::CamKeysClear       => self.cam_input.clear_keys(),
                IpcCommand::SetToolMode(m)     => self.tool_mode = m,
                IpcCommand::SetGizmoSpace(s)   => self.gizmo_space = s,
                IpcCommand::PlayClamp(v)       => {
                    self.play_clamp = v;
                    if !v { release_window_clamp(); }
                }
                IpcCommand::Undo => {
                    // Undo を跨いで直前のフィールド編集へマージしない（編集の連続性が切れる）。
                    self.reset_field_edit_session();
                    let result = if let Some(scene) = &mut self.scene {
                        self.undo_history.undo(scene)
                    } else { None };
                    if let Some((structural, sel)) = result {
                        // アクターツリー再構築
                        if let Some((wl, actors_data)) = self.undo_history.peek_undone_actor_rebuild() {
                            self.rebuild_actors_for_wl(wl, actors_data);
                        }
                        // コンポーネントスロット再構築
                        if let Some((wl, actor_dfs_id, slots_data)) = self.undo_history.peek_undone_component_rebuild() {
                            self.rebuild_actor_slots(wl, actor_dfs_id, slots_data);
                            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                        }
                        // インスペクタのフィールド編集（1 スロットだけ値を戻す）
                        if let Some((wl, actor_dfs_id, slot_idx, slot_data)) = self.undo_history.peek_undone_slot_data() {
                            self.apply_slot_data(wl, actor_dfs_id, slot_idx, &slot_data);
                        }
                        // 地表カバー場（CoverFieldEditCommand）を編集前へ戻す。
                        // 実体は Scene の外（App.terrain）なので、コマンド自身ではなく
                        // ここで書き戻す（apply_slot_data と同じ流儀）。
                        if let Some(fields) = self.undo_history.peek_undone_cover_fields() {
                            self.restore_cover_snapshots(&fields);
                        }
                        // アクタートランスフォーム変更 → インスペクター通知
                        if let Some((_wl, dfs_id)) = self.undo_history.peek_undone_actor_inspect() {
                            self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                        }
                        if let Some(ids) = sel {
                            self.selected_instances = ids;
                            self.send_selected();
                        }
                        // アクター DFS 選択の復元（ActorDfsSelectionCommand）
                        if let Some((dfs_ids, primary)) = self.undo_history.peek_undone_actor_dfs_selection() {
                            self.selected_actor_dfs_ids     = dfs_ids;
                            self.actor_virtual_selected_idx = primary;
                            self.selected_instances.clear();
                            self.send_selected();
                        }
                        if structural {
                            self.send_hierarchy();
                        }
                        // シーン設定ウィンドウの「シェーダ」行（SceneShadingCommand）は
                        // ACTOR_COMPONENTS に載らないので、専用に送り直す。
                        // 対象外のコマンドを Undo した場合でも内容が変わらないだけで害は無い。
                        self.send_scene_shading_params();
                    }
                }
                IpcCommand::Redo => {
                    // Redo を跨いで直前のフィールド編集へマージしない。
                    self.reset_field_edit_session();
                    let result = if let Some(scene) = &mut self.scene {
                        self.undo_history.redo(scene)
                    } else { None };
                    if let Some((structural, sel)) = result {
                        // アクターツリー再構築
                        if let Some((wl, actors_data)) = self.undo_history.peek_redone_actor_rebuild() {
                            self.rebuild_actors_for_wl(wl, actors_data);
                        }
                        // コンポーネントスロット再構築
                        if let Some((wl, actor_dfs_id, slots_data)) = self.undo_history.peek_redone_component_rebuild() {
                            self.rebuild_actor_slots(wl, actor_dfs_id, slots_data);
                            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                        }
                        // インスペクタのフィールド編集（1 スロットだけ値を進める）
                        if let Some((wl, actor_dfs_id, slot_idx, slot_data)) = self.undo_history.peek_redone_slot_data() {
                            self.apply_slot_data(wl, actor_dfs_id, slot_idx, &slot_data);
                        }
                        // Undo と対称に、地表カバー場を編集後へ進める。
                        if let Some(fields) = self.undo_history.peek_redone_cover_fields() {
                            self.restore_cover_snapshots(&fields);
                        }
                        // アクタートランスフォーム変更 → インスペクター通知
                        if let Some((_wl, dfs_id)) = self.undo_history.peek_redone_actor_inspect() {
                            self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                        }
                        if let Some(ids) = sel {
                            self.selected_instances = ids;
                            self.send_selected();
                        }
                        // アクター DFS 選択の復元（ActorDfsSelectionCommand）
                        if let Some((dfs_ids, primary)) = self.undo_history.peek_redone_actor_dfs_selection() {
                            self.selected_actor_dfs_ids     = dfs_ids;
                            self.actor_virtual_selected_idx = primary;
                            self.selected_instances.clear();
                            self.send_selected();
                        }
                        if structural {
                            self.send_hierarchy();
                        }
                        // Undo 側と同じ理由でシーン設定のシェーダ行を送り直す。
                        self.send_scene_shading_params();
                    }
                }
                IpcCommand::Copy  => { self.do_copy(); }
                IpcCommand::Paste => { self.do_paste(); }
                IpcCommand::Delete(ids) => {
                    self.apply_delete(&ids, false);
                }
                IpcCommand::DeleteRecursive(ids) => {
                    self.apply_delete(&ids, true);
                }
                IpcCommand::Select(idx) => {
                    if idx >= 999_000_000 {
                        // アクターツリーノード選択（シーンモード・アクター編集モード共通）
                        let actor_dfs = (idx - 999_000_000) as u32;
                        self.actor_virtual_selected_idx = Some(actor_dfs as usize);
                        self.selected_actor_dfs_ids     = vec![actor_dfs as usize];
                        self.selected_instances.clear();
                    } else {
                        // 後方互換: インスタンスインデックス直接指定（レガシーパス）
                        self.actor_virtual_selected_idx = None;
                        self.actor_virtual_selected_slot_idx = 0;
                        self.selected_actor_dfs_ids.clear();
                        self.selected_instances.clear();
                    }
                    self.send_selected();
                    // 仮想ノード選択時はインスペクタへ即時プッシュ
                    if idx >= 999_000_000 {
                        let actor_dfs = (idx - 999_000_000) as u32;
                        self.send_actor_components(actor_dfs, self.actor_virtual_selected_slot_idx);
                    }
                }
                IpcCommand::SelectMulti(ids) => {
                    // 仮想 ID (999_000_000 + dfs) をパースして selected_actor_dfs_ids を更新する
                    let dfs_ids: Vec<usize> = ids.iter()
                        .filter(|&&id| id >= 999_000_000)
                        .map(|&id| (id - 999_000_000) as usize)
                        .collect();
                    if !dfs_ids.is_empty() {
                        self.actor_virtual_selected_idx = dfs_ids.last().copied();
                        self.selected_actor_dfs_ids     = dfs_ids;
                        self.selected_instances.clear();
                    }
                    self.send_selected();
                    if let Some(idx) = self.actor_virtual_selected_idx {
                        self.send_actor_components(idx as u32, self.actor_virtual_selected_slot_idx);
                    }
                }
                IpcCommand::Reparent { child, new_parent, anchor_sibling, place_before } => {
                    self.handle_reparent_actor(child, new_parent, anchor_sibling, place_before);
                }
                IpcCommand::Rename { idx, name } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(self.active_world_line) {
                            if let Some(meta) = mc.instance_meta.get_mut(idx as usize) {
                                meta.name = name;
                            }
                        }
                    }
                    self.send_hierarchy();
                }
                IpcCommand::CreateGroup { name, parent } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(self.active_world_line) {
                            let id = mc.next_group_id;
                            mc.next_group_id += 1;
                            mc.group_meta.push(GroupMeta { id, name, parent });
                        }
                    }
                    self.send_hierarchy();
                }
                IpcCommand::CreateGroupWithChildren { name, parent, children } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(self.active_world_line) {
                            let gid = mc.next_group_id;
                            mc.next_group_id += 1;
                            mc.group_meta.push(GroupMeta { id: gid, name, parent });
                            for child in children {
                                if child >= GROUP_ID_BASE {
                                    if let Some(g) = mc.group_meta.iter_mut().find(|g| g.id == child) {
                                        g.parent = Some(gid);
                                    }
                                } else if (child as usize) < mc.instance_meta.len() {
                                    mc.instance_meta[child as usize].parent = Some(gid);
                                }
                            }
                        }
                    }
                    self.send_hierarchy();
                }
                IpcCommand::SaveScene(path) => {
                    // ── 地形の実体（.tvox / .tscatter / .tcover）も一緒にフラッシュする ──
                    //   地形は .scene の外に住んでいるため、ここで書かないと
                    //   「Ctrl+S したのに掘った地形・積もった雪が消える」ことになる。
                    //   書くのはダーティなチャンクだけなので、地形を触っていない
                    //   セッションでは 1 バイトも触らない（保存が遅くならない）。
                    //   .scene 本体より先に書き、最後の .scene 書き込みを確定操作にする。
                    if let Err(e) = self.flush_dirty_terrain() {
                        if let Some(ipc) = &self.ipc {
                            ipc.send(&format!("TERRAIN_SAVE_ERROR:{e}"));
                        }
                    }
                    if let Some(scene) = &self.scene {
                        let pos = self.camera.base.transform.position;
                        let cam_data = DebugCameraData {
                            position: [pos.x, pos.y, pos.z],
                            yaw:      self.camera.yaw,
                            pitch:    self.camera.pitch,
                            fov_deg:  self.camera.base.projection.fov_y_rad.to_degrees(),
                            far:      self.camera.base.projection.far,
                            speed:    self.camera.move_speed,
                        };
                        match scene.save(std::path::Path::new(&path), &cam_data) {
                            Ok(())   => { if let Some(ipc) = &self.ipc { ipc.send("SAVE_OK"); } }
                            Err(e)   => { if let Some(ipc) = &self.ipc { ipc.send(&format!("SAVE_ERROR:{e}")); } }
                        }
                    }
                }
                IpcCommand::TerrainInit { config } => {
                    // ボクセル地形を初期化する（地形ツリー生成＋初期地面のメッシュ化）。
                    // config が付いていればチャンク構成（枚数・分割数・ボクセルサイズ）を
                    // 先に反映する。既存地形は丸ごと作り直されるので、分割数変更もここでは安全。
                    self.handle_terrain_init(config);
                }
                IpcCommand::TerrainAddChunks { min_x, min_z, max_x, max_z } => {
                    // 編集中の地形へチャンクを追加する（既存チャンクは温存）。
                    self.handle_terrain_add_chunks(min_x, min_z, max_x, max_z);
                }
                IpcCommand::TerrainBrush { op, screen_x, screen_y, radius, strength } => {
                    // op を BrushOp へ復元（未知値は Add にフォールバック）。
                    let brush_op = crate::engine::terrain::BrushOp::from_u32(op)
                        .unwrap_or(crate::engine::terrain::BrushOp::Add);
                    self.handle_terrain_brush(brush_op, screen_x, screen_y, radius, strength);
                }
                IpcCommand::TerrainPaint { layer, screen_x, screen_y, radius, strength } => {
                    // 地形レイヤペイント（密度は変えずスプラット重みだけを押し上げる）。
                    self.handle_terrain_paint(layer as usize, screen_x, screen_y, radius, strength);
                }
                IpcCommand::TerrainBrushMask { path } => {
                    // 地形ペイント系ブラシの形状マスクを設定・解除する（空文字で解除）。
                    self.handle_terrain_brush_mask(path);
                }
                IpcCommand::TerrainSave => {
                    // ボクセル地形の全チャンクを .tvox として保存する（現在の地形フォルダへ）。
                    self.handle_terrain_save();
                }
                IpcCommand::TerrainSaveAs { dir } => {
                    // 地形一式を別フォルダへ保存し、シーンの地形フォルダ参照を切り替える。
                    self.handle_terrain_save_as(dir);
                }
                IpcCommand::TerrainBrushPreview { screen_x, screen_y, radius, strength } => {
                    // ホバー位置のブラシ範囲プレビュー（ワイヤスフィア）を更新する。
                    // strength は frame_renderer 側でプレビュー球の色（低強度=水色〜高強度=オレンジ）
                    // へ反映される。
                    self.handle_terrain_brush_preview(screen_x, screen_y, radius, strength);
                }
                IpcCommand::TerrainBrushPreviewOff => {
                    // ブラシ範囲プレビューを非表示にする。
                    self.handle_terrain_brush_preview_off();
                }
                IpcCommand::TerrainUndo => {
                    // terrain 専用 undo（シーン全体の Undo とは別スタック）。
                    self.handle_terrain_undo();
                }
                IpcCommand::TerrainRedo => {
                    // terrain 専用 redo。
                    self.handle_terrain_redo();
                }
                IpcCommand::TerrainStrokeEnd => {
                    // 進行中のブラシストロークを 1 undo エントリとして確定する。
                    self.handle_terrain_stroke_end();
                }
                IpcCommand::TerrainCoverSimStart => {
                    // カバー場（I3.1）のリアルタイム連続シミュレート開始。
                    self.handle_terrain_cover_sim_start();
                }
                IpcCommand::TerrainCoverSimStop => {
                    // 連続シミュレート停止（開始〜停止で 1 undo エントリが確定する）。
                    self.handle_terrain_cover_sim_stop();
                }
                IpcCommand::TerrainCoverStep { seconds } => {
                    // 指定秒数ぶんを即時計算して停止する。
                    self.handle_terrain_cover_step(seconds);
                }
                IpcCommand::TerrainCoverClear => {
                    self.handle_terrain_cover_clear();
                }
                IpcCommand::TerrainCoverBrush {
                    material_id, screen_x, screen_y, radius, strength, target_amount, erase,
                } => {
                    // カバー場の手編集（消しゴム／塗り）。Undo は密度ブラシと同じ
                    // terrain 専用スタックで、TERRAIN_STROKE_END が 1 ストロークを確定する。
                    self.handle_terrain_cover_brush(
                        material_id, screen_x, screen_y, radius, strength, target_amount, erase,
                    );
                }
                IpcCommand::TerrainReloadLayers => {
                    // layers.json を読み直し、レイヤテクスチャと全チャンクを作り直す
                    // （エディタの地形設定ウィンドウでレイヤを保存した直後の即時反映）。
                    self.handle_terrain_reload_layers();
                }
                IpcCommand::TerrainScatterRules { prop_id, seed } => {
                    // 散布プロップをルールで全チャンクへ自動散布し直す。
                    // prop_id が空文字なら全プロップが対象。
                    self.handle_terrain_scatter_rules(prop_id, seed);
                }
                IpcCommand::TerrainScatterBrush {
                    prop_id, screen_x, screen_y, radius, density, erase,
                } => {
                    // 散布プロップの手描き追加／消去（球ブラシ）。
                    self.handle_terrain_scatter_brush(
                        prop_id, screen_x, screen_y, radius, density, erase,
                    );
                }
                IpcCommand::TerrainHeightmap { path, height_scale, config } => {
                    // ハイトマップ画像から地形を敷き直す（config 付きなら構成も反映）。
                    self.handle_terrain_heightmap(path, height_scale, config);
                }
                IpcCommand::SaveActor(path) => {
                    let wl = self.active_world_line;
                    let result: Result<(), String> = (|| {
                        let scene = self.scene.as_ref().ok_or("シーンが読み込まれていません")?;
                        let actor = scene.actors.iter()
                            .find(|a| a.world_line == wl)
                            .ok_or("アクティブ世界線にアクターがありません")?;
                        let mut data = actor.to_data(&scene.world);
                        // 2D アクター: ルート CanvasTransform.position は 0 で保存する
                        // （handle_export_actor と同じ規則。ドロップ配置時に位置を設定し直すため）。
                        // rotation / scale / pivot / anchor は維持する。
                        if let Some(ref mut ct) = data.canvas_transform {
                            ct.position = [0.0, 0.0];
                        }
                        // .actor ファイルはプレハブのテンプレート。ルートの参照リンクは
                        // 書き出さない（テンプレートへの自己参照・二重リンク混入を防ぐ）。
                        data.prefab_source = None;
                        let json = serde_json::to_string_pretty(&data).map_err(|e| e.to_string())?;
                        std::fs::write(&path, json).map_err(|e| e.to_string())?;
                        Ok(())
                    })();
                    match result {
                        Ok(())   => {
                            if let Some(ipc) = &self.ipc { ipc.send("SAVE_OK"); }
                            // ── シーン側インスタンスへの自動再展開は「行わない」──────
                            // 以前はここで propagate_prefab_change(&path) を呼び、保存した
                            // .actor と同じ参照パスを持つ全インスタンスを問答無用で
                            // 再展開していた。そのため、プレハブ本体を保存した瞬間に
                            // シーン側でインスタンスへ加えた変更（コンポーネント追加・
                            // 値変更・子の追加）が消えるデータ損失が起きていた。
                            // 方針: 「シーンに保存されている内容を正とする」。反映したい
                            // ときだけユーザーが明示的に「プレハブから更新」
                            // （PREFAB_REAPPLY）／「シーン内の全プレハブを更新」
                            // （PREFAB_REAPPLY_ALL）を実行する。
                        }
                        Err(e)   => {
                            if let Some(ipc) = &self.ipc { ipc.send(&format!("SAVE_ERROR:{e}")); }
                        }
                    }
                }
                IpcCommand::BeginTransformDrag { is_actor, target_id } => {
                    let wl = self.active_world_line;
                    if !is_actor {
                        let old_mat = self.scene.as_ref()
                            .and_then(|s| s.find_component_in_world_line::<ModelComponent>(wl))
                            .and_then(|mc| mc.instance_mats.get(target_id as usize).copied());
                        if let Some(old_mat) = old_mat {
                            self.inspector_transform_drag = Some(super::InspectorTransformDrag::Instance {
                                idx: target_id, old_mat,
                            });
                        }
                    } else {
                        if wl != 0 {
                            let mut c = 0u32;
                            let snapshot = self.scene.as_ref().and_then(|s| {
                                find_actor_by_dfs(&s.actors, wl, target_id, &mut c).map(|actor| {
                                    let old_mats = actor.mc_entity()
                                        .and_then(|e| s.world.get::<ModelComponent>(e))
                                        .map(|mc| mc.instance_mats.clone())
                                        .unwrap_or_default();
                                    let old_tf = s.world.get::<crate::engine::components::Transform>(actor.entity)
                                        .cloned().unwrap_or_default();
                                    let mut child_old_states = Vec::new();
                                    let mut child_dfs = target_id + 1;
                                    super::collect_child_actor_old_states(actor, &s.world, &mut child_dfs, &mut child_old_states);
                                    (old_mats, old_tf, child_old_states)
                                })
                            });
                            if let Some((old_mats, old_tf, child_old_states)) = snapshot {
                                if !old_mats.is_empty() {
                                    self.inspector_transform_drag = Some(super::InspectorTransformDrag::ActorGroup {
                                        dfs_id: target_id, old_mats, old_tf, child_old_states,
                                    });
                                } else if self.canvas_world_lines.contains(&wl) {
                                    // 2D キャンバスアクター: CanvasTransform のスナップショットを記録する
                                    let old_ct = self.scene.as_ref().and_then(|s| {
                                        let mut c2 = 0u32;
                                        find_actor_by_dfs(&s.actors, wl, target_id, &mut c2)
                                            .and_then(|a| s.world.get::<crate::engine::components::CanvasTransform>(a.entity).cloned())
                                    });
                                    if let Some(old_ct) = old_ct {
                                        self.inspector_transform_drag = Some(super::InspectorTransformDrag::CanvasActor {
                                            wl, dfs_id: target_id, old_ct,
                                        });
                                    }
                                } else {
                                    self.inspector_transform_drag = Some(super::InspectorTransformDrag::Actor {
                                        wl, dfs_id: target_id, old_tf,
                                    });
                                }
                            }
                        } else {
                            let old_tf = self.scene.as_ref().and_then(|s| {
                                let mut c = 0u32;
                                find_actor_by_dfs(&s.actors, wl, target_id, &mut c)
                                    .and_then(|a| s.world.get::<crate::engine::components::Transform>(a.entity).cloned())
                            });
                            if let Some(old_tf) = old_tf {
                                self.inspector_transform_drag = Some(super::InspectorTransformDrag::Actor {
                                    wl, dfs_id: target_id, old_tf,
                                });
                            }
                        }
                    }
                }
                IpcCommand::EndTransformDrag => {
                    if let Some(drag) = self.inspector_transform_drag.take() {
                        let wl = self.active_world_line;
                        match drag {
                            super::InspectorTransformDrag::Instance { idx, old_mat } => {
                                if let Some(mc) = self.scene.as_ref()
                                    .and_then(|s| s.find_component_in_world_line::<ModelComponent>(wl))
                                {
                                    if let Some(&new_mat) = mc.instance_mats.get(idx as usize) {
                                        if old_mat != new_mat {
                                            self.undo_history.record(Box::new(TransformCommand {
                                                instance_idx: idx, old_mat, new_mat,
                                            }));
                                        }
                                    }
                                }
                            }
                            super::InspectorTransformDrag::Actor { wl: drag_wl, dfs_id, old_tf } => {
                                {
                                    if let Some(scene) = &self.scene {
                                        let mut c = 0u32;
                                        let new_tf_opt = find_actor_by_dfs(&scene.actors, drag_wl, dfs_id, &mut c)
                                            .and_then(|a| scene.world.get::<crate::engine::components::Transform>(a.entity).cloned());
                                        if let Some(new_tf) = new_tf_opt {
                                            if old_tf != new_tf {
                                                self.undo_history.record(Box::new(crate::engine::core::app_base::undo::ActorTransformCommand {
                                                    world_line: drag_wl, dfs_id,
                                                    old_transform: old_tf, new_transform: new_tf,
                                                }));
                                            }
                                        }
                                    }
                                }
                                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                            }
                            super::InspectorTransformDrag::ActorGroup { dfs_id, old_mats, old_tf, child_old_states } => {
                                let c = 0u32;
                                let transforms: Vec<(u32, [[f32;4];4], [[f32;4];4])>;
                                let new_tf: crate::engine::components::Transform;
                                let child_transforms: Vec<(u32, crate::engine::components::Transform, crate::engine::components::Transform, [[f32;4];4], [[f32;4];4])>;
                                {
                                    let scene_ref = self.scene.as_ref();
                                    let mc_opt = scene_ref.and_then(|s| {
                                        let mut c_ = c;
                                        find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c_)
                                            .and_then(|a| a.mc_entity())
                                            .and_then(|e| s.world.get::<ModelComponent>(e))
                                    });
                                    transforms = mc_opt.map(|mc| {
                                        old_mats.iter().enumerate().filter_map(|(i, &old)| {
                                            mc.instance_mats.get(i).copied()
                                                .filter(|&new| new != old)
                                                .map(|new| (i as u32, old, new))
                                        }).collect()
                                    }).unwrap_or_default();
                                    let mut c2 = 0u32;
                                    new_tf = scene_ref
                                        .and_then(|s| {
                                            find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c2)
                                                .and_then(|a| s.world.get::<crate::engine::components::Transform>(a.entity).cloned())
                                        })
                                        .unwrap_or_default();
                                    child_transforms = child_old_states.iter().filter_map(|(child_dfs, old_child_tf, old_mc_mat)| {
                                        let mut cc = 0u32;
                                        let new_child_tf = scene_ref
                                            .and_then(|s| {
                                                find_actor_by_dfs(&s.actors, wl, *child_dfs, &mut cc)
                                                    .and_then(|a| s.world.get::<crate::engine::components::Transform>(a.entity).cloned())
                                            })?;
                                        let mut cc2 = 0u32;
                                        let new_mc_mat = scene_ref
                                            .and_then(|s| {
                                                find_actor_by_dfs(&s.actors, wl, *child_dfs, &mut cc2)
                                                    .and_then(|a| a.mc_entity())
                                                    .and_then(|e| s.world.get::<ModelComponent>(e))
                                                    .and_then(|mc| mc.instance_mats.first().copied())
                                            })
                                            .unwrap_or(*old_mc_mat);
                                        if old_child_tf != &new_child_tf || old_mc_mat != &new_mc_mat {
                                            Some((*child_dfs, old_child_tf.clone(), new_child_tf, *old_mc_mat, new_mc_mat))
                                        } else {
                                            None
                                        }
                                    }).collect();
                                }
                                if !transforms.is_empty() || old_tf != new_tf || !child_transforms.is_empty() {
                                    self.undo_history.record(Box::new(crate::engine::core::app_base::undo::ActorGroupTransformCommand {
                                        wl, dfs_id, old_tf, new_tf, transforms, child_transforms,
                                        extra_slot_transforms: vec![],
                                    }));
                                }
                                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                            }
                            super::InspectorTransformDrag::CanvasActor { wl: drag_wl, dfs_id, old_ct } => {
                                // 2D キャンバスアクター: 現在の CanvasTransform と比較して差異があれば記録する
                                let new_ct_opt = self.scene.as_ref().and_then(|s| {
                                    let mut c = 0u32;
                                    find_actor_by_dfs(&s.actors, drag_wl, dfs_id, &mut c)
                                        .and_then(|a| s.world.get::<crate::engine::components::CanvasTransform>(a.entity).cloned())
                                });
                                if let Some(new_ct) = new_ct_opt {
                                    // position/rotation/scale/pivot のいずれかが変化していれば Undo 記録する
                                    let changed = new_ct.position != old_ct.position
                                        || new_ct.rotation != old_ct.rotation
                                        || new_ct.scale    != old_ct.scale
                                        || new_ct.pivot    != old_ct.pivot;
                                    if changed {
                                        self.undo_history.record(Box::new(
                                            crate::engine::core::app_base::undo::CanvasTransformCommand {
                                                world_line: drag_wl, dfs_id, old_ct, new_ct,
                                            }
                                        ));
                                    }
                                }
                                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                            }
                        }
                    }
                }
                IpcCommand::GetActorData(idx) => {
                    self.send_actor_data(idx);
                }
                IpcCommand::SetTransform { id, px, py, pz, ex, ey, ez, sx, sy, sz } => {
                    self.apply_set_transform(id, px, py, pz, ex, ey, ez, sx, sy, sz);
                }
                IpcCommand::SetCameraFov(deg) => {
                    self.camera.base.projection.fov_y_rad =
                        deg * std::f32::consts::PI / 180.0;
                }
                IpcCommand::SetCameraFar(far) => {
                    self.camera.base.projection.far = far;
                }
                IpcCommand::SetShowGrid(v) => {
                    self.show_grid = v;
                }
                IpcCommand::SetRtShadows(v) => {
                    // 影方式をエディタからのライブ切替で更新する（旧 RT_SHADOWS:1/0 互換）。
                    self.render_features.shadow = if v {
                        crate::engine::core::renderer::ShadowMode::Rt
                    } else {
                        crate::engine::core::renderer::ShadowMode::ShadowMap
                    };
                    self.log_render_features_if_changed();
                }
                IpcCommand::SetPostFx { bloom, fxaa, bloom_intensity, transparency, deferred, refract_sequential_grab, view_mode, gi, reflection_intensity, ao_intensity, features, legacy_gi_enabled } => {
                    // ブルーム／FXAA をエディタからのライブ切替で更新する（Phase R4）。
                    self.post_fx.bloom_enabled   = bloom;
                    self.post_fx.fxaa_enabled    = fxaa;
                    self.post_fx.bloom_intensity = bloom_intensity;
                    // 透明描画方式（距離ソート / WBOIT）のライブ切替（Phase R5）。
                    self.post_fx.transparency    = transparency;
                    // メッシュレットカリングは常時有効化したためライブ切替は撤去（設定 meshlet_cull 廃止）。
                    // Deferred（G-Buffer）レンダリングのライブ切替（Phase D3 Deferred Phase B）。
                    self.post_fx.deferred        = deferred;
                    // RT屈折の逐次グラブ（ガラス越しガラスの多重屈折）のライブ切替。既定 OFF の重量オプション。
                    self.post_fx.refract_sequential_grab = refract_sequential_grab;
                    // シーンビュー表示モード（Lit / Unlit / Wireframe）のライブ切替。
                    self.scene_view_mode         = view_mode;
                    // DDGI の数値設定（強度／プローブ数等）のライブ切替（Phase RT-GI）。
                    self.post_fx.gi              = gi;
                    // 反射（SSR / RT）強度のライブ切替（Phase D6）。
                    self.post_fx.reflection_intensity = reflection_intensity;
                    // AO（SSAO / RT-AO）強度のライブ切替（Phase D4）。
                    self.post_fx.ao_intensity = ao_intensity;
                    // レンダリング機能マトリクス。新エディタは features を送る（全機能を一括更新）。
                    if let Some(f) = features {
                        self.render_features = f;
                    } else if let Some(en) = legacy_gi_enabled {
                        // 旧エディタ互換: features 無し・gi_enabled のみ → GI モードだけ反映。
                        // 影など他機能は据え置く（RT_SHADOWS コマンドが別途担う）。
                        self.render_features.gi = if en {
                            crate::engine::core::renderer::GiMode::Rt
                        } else {
                            crate::engine::core::renderer::GiMode::Flat
                        };
                    }
                    // 実効モード（降格・未実装を含む）が変わっていればログする。
                    self.log_render_features_if_changed();
                }
                IpcCommand::SetAmbient { color, intensity } => {
                    // 環境光（アンビエント）をエディタからのライブ切替で更新する（Phase R1.5）。
                    // 次フレームの LightBuffer::update で LightMeta へ反映される。
                    self.ambient_color     = color;
                    self.ambient_intensity = intensity;
                }
                IpcCommand::SetShowSpriteBones(v) => {
                    self.show_sprite_bones = v;
                }
                IpcCommand::SetShowAxisGizmo(v) => {
                    self.show_axis_gizmo = v;
                }
                IpcCommand::SetCanvasScreenSpaceOverlay(v) => {
                    // キャンバス表示モード切り替え（false=ワールドスペース、true=スクリーンスペース）
                    self.canvas_screen_space_overlay = v;
                }
                IpcCommand::SetEditViewMode { is_2d } => {
                    // Edit ビューモード切替（エディタの 3Dシーン / 2Dシーンタブ）。
                    // Edit モード限定機能のため Play モード中は無視する。
                    if self.mode == RuntimeMode::Edit {
                        let new_mode = if is_2d {
                            super::EditViewMode::View2D
                        } else {
                            super::EditViewMode::View3D
                        };
                        // ── タブ切替フック（物理状態のタブごと保持）─────────────
                        // ビュータブ（3Dシーン / 2Dシーン）が実際に切り替わる場合のみ、
                        // 現タブの物理状態（スナップショット・速度）を退避する。
                        // 同タブ再送（new_mode == 現 edit_view_mode）は物理に一切触れない。
                        // leave は edit_view_mode 更新前に呼ぶ（current_tab_key が旧タブを指すため）。
                        let tab_changed = new_mode != self.edit_view_mode;
                        if tab_changed { self.leave_current_tab_physics(); }
                        // ビューモードが実際に変わる場合は選択状態をリセットする。
                        // ワールド / ビューポートタブを直接切り替えたとき、旧ビューで
                        // 選択していたアクターのギズモが新ビューに残留するのを防ぐ。
                        if new_mode != self.edit_view_mode {
                            self.selected_actor_dfs_ids.clear();
                            self.selected_instances.clear();
                            self.actor_virtual_selected_idx = None;
                            self.actor_virtual_selected_slot_idx = 0;
                            self.hovered_gizmo_part = None;
                            self.drag.gizmo_drag = None;
                            // エディタのヒエラルキーパネルにも選択解除を通知する
                            if let Some(ipc) = &self.ipc { ipc.send("SELECTED:-1"); }
                        }
                        self.edit_view_mode = new_mode;
                        // ピッキング・ギズモ・ドラッグの各ハンドラは
                        // canvas_screen_space_overlay から use_screen_space を計算するため、
                        // ビューモードとフラグを同期させて座標系の不整合を防ぐ。
                        // （View2D = スクリーンスペース / View3D = SS キャンバス非表示）
                        self.canvas_screen_space_overlay = is_2d;
                        // 2D シーンビューの初回表示時は WYSIWYG（1 キャンバスピクセル = 1 画面
                        // ピクセル・画面中央原点）になるよう 2D カメラを初期化する。
                        // 既にカメラ状態がある場合はユーザーのパン・ズームを維持する。
                        if is_2d && !self.canvas_cameras.contains_key(&0) {
                            // ortho_half_h = ビューポート高さの半分 → キャンバス座標が実ピクセルと一致
                            let half_h = self.window.as_ref()
                                .map(|w| w.inner_size().height as f32 / 2.0)
                                .unwrap_or(360.0);
                            self.canvas_cameras.insert(0, CanvasCameraData {
                                pan_x: 0.0,
                                pan_y: 0.0,
                                ortho_half_h: half_h,
                            });
                        }
                        // 移動先タブの物理状態を復元する（edit_view_mode 更新後に呼ぶ）。
                        // 保存済みなら続き（位置＋速度）から一時停止状態で復帰し、
                        // 未保存なら現状態を初期フレームとして新規初期化する。
                        if tab_changed { self.enter_tab_physics(); }
                    }
                }
                IpcCommand::SetEditorCameraOrtho(v) => {
                    // エディタのデバッグカメラを正射/透視に切り替える（Edit モード限定）。
                    // 視点は維持し、0.3 秒かけて投影方式を補間する。
                    if self.mode == RuntimeMode::Edit {
                        self.camera.set_ortho(v);
                    }
                }
                IpcCommand::GetCamState => {
                    let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
                    if let Some(ipc) = &self.ipc {
                        ipc.send(&format!("CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"));
                    }
                }
                IpcCommand::SetCameraTransform { px, py, pz, euler_x, euler_y, euler_z } => {
                    self.apply_camera_euler(px, py, pz, euler_x, euler_y, euler_z);
                }
                IpcCommand::SetCameraSpeed(speed) => {
                    // クランプ範囲はシーン設定の適用（apply_scene_settings）と共用の定数を使う
                    self.camera.move_speed = speed.clamp(
                        super::app_init::CAM_SPEED_MIN,
                        super::app_init::CAM_SPEED_MAX,
                    );
                }
                IpcCommand::LoadScene(path) => {
                    // アクター編集中なら現在のカメラを退避してからシーンモードに切り替える
                    {
                        let pos = self.camera.base.transform.position;
                        self.saved_cameras.insert(self.active_world_line, DebugCameraData {
                            position: [pos.x, pos.y, pos.z],
                            yaw:      self.camera.yaw,
                            pitch:    self.camera.pitch,
                            fov_deg:  self.camera.base.projection.fov_y_rad.to_degrees(),
                            far:      self.camera.base.projection.far,
                            speed:    self.camera.move_speed,
                        });
                    }
                    self.active_world_line = 0;
                    self.saved_cameras.remove(&0);
                    let result = if let Some(ctx) = &self.draw_ctx {
                        Some(Scene::load(
                            std::path::Path::new(&path),
                            ctx,
                            self.scripting_host.as_ref(),
                        ))
                    } else { None };
                    match result {
                        Some(Ok((mut new_scene, cam_data))) => {
                            // world_line=0 が 2D キャンバスモードかどうかを
                            // ロードしたアクター（すべて world_line=0）の種別を走査して判定する。
                            // world_line > 0 のアクターを追加する前にチェックすることで
                            // アクター編集タブの 2D アクターに誤判定しない。
                            fn has_any_2d_actor(actors: &[crate::engine::structs::objects::Actor]) -> bool {
                                actors.iter().any(|a| a.is_2d() || has_any_2d_actor(a.children()))
                            }
                            if has_any_2d_actor(&new_scene.actors) {
                                self.canvas_world_lines.insert(0);
                            } else {
                                self.canvas_world_lines.remove(&0);
                            }
                            // world_line > 0 のアクター（アクター編集タブ）を保持する
                            if let Some(old_scene) = self.scene.take() {
                                for actor in old_scene.actors.into_iter().filter(|a| a.world_line > 0) {
                                    new_scene.actors.push(actor);
                                }
                            }
                            self.scene = Some(new_scene);
                            // 速度バッファ: シーン総入れ替え ⇒ 次フレームは prev=curr（速度 0）。
                            self.request_velocity_reset();
                            // GPU パーティクルを全解放する（生存エミッタ＋孤児プールとも）。
                            // 孤児（エミッタ消滅後も寿命まで生き残る粒子群）が旧シーンから
                            // 新シーンへ持ち越されないようにする。
                            self.particle_system.clear_all();
                            // インタラクションソースの位置履歴も捨てる（Phase I1）。
                            // 残したままシーンが入れ替わると「旧シーンの位置 → 新シーンの位置」の
                            // 巨大な速度が 1 フレームだけ場へ焼かれ、草が一斉になぎ倒される。
                            self.interaction_velocity.clear();
                            self.selected_instances.clear();
                            self.actor_virtual_selected_idx = None;
                            self.actor_virtual_selected_slot_idx = 0;
                            // ── プレハブ参照リンクのロード時再展開は「行わない」 ────────
                            // 【データ損失バグの修正】
                            // 以前はここで apply_prefab_links_on_load() を呼び、prefab_source を
                            // 持つアクタの子ツリー・コンポーネントを .actor ファイルの内容で丸ごと
                            // 差し替えていた。そのため、シーン上でインスタンスに加えた変更
                            // （Collider の値変更・ScriptComponent の追加・子への Camera 追加など）が
                            // .scene には正しく保存されているにもかかわらず、シーンを開き直すたびに
                            // 破棄されていた。
                            //
                            // 方針: 「シーンに丸ごと保存されている内容を正とする」。
                            // ロード時に自動で上書きすることはせず、プレハブ本体の内容を反映したい
                            // ときだけ、ユーザーが明示的に「プレハブから更新」
                            // （IPC: PREFAB_REAPPLY → handle_reapply_prefab）を実行する。
                            // 地形チャンク（TerrainChunkComponent 付き）を .tvox から復元し、
                            // terrain:// ガードで model=None のまま読まれた ModelComponent を埋める。
                            self.rebuild_terrain_after_load();
                            // Play モード（常駐 Play プロセス再利用の LOAD_SCENE）: 地形 LOD を
                            // メインカメラ位置で事前収束させる。ウィンドウ Play 新規起動
                            // （app_init.rs load_play_scene）・埋め込み ENTER_PLAY
                            // （play_mode_ops.rs enter_play）と同じ扱いで、Play 開始直後の
                            // 一斉 LOD 遷移による低 fps 張り付きを防ぐ。Edit のロードは従来どおり。
                            if self.mode == RuntimeMode::Play {
                                let cam_pos = self.play_converge_camera_pos();
                                self.converge_terrain_lod_blocking(cam_pos);
                            }
                            self.undo_history = crate::engine::core::app_base::undo::UndoHistory::new();
                            if let Some(cam) = cam_data {
                                self.apply_camera_data(&cam);
                            }
                            // シーン単位のビューポート／レンダリング設定を適用する。
                            // 起動時に読んだ project_settings.json（load_graphics_settings）の値を
                            // ここで上書きする。settings 節を持たない旧 .scene では None のため
                            // project 設定がそのまま残る（フォールバック）。
                            //
                            // apply_camera_data の「後」に置くこと。apply_camera_data は
                            // debug_camera 節の fov/far/speed でカメラを上書きするため、先に
                            // 適用するとシーン設定側の値が潰される（load_play_scene も同じ順序）。
                            if let Some(s) = self.scene.as_ref().and_then(|sc| sc.settings.clone()) {
                                self.apply_scene_settings(&s);
                            }
                            self.send_selected();
                            self.send_hierarchy();
                            // 全 world_line を作り直して Entity が再生成されるため、
                            // タブごとに退避していた物理状態（旧 Entity キー）はすべて破棄する。
                            // 破棄しないと、別タブ復帰時に旧 Entity のスナップショット・速度で
                            // ECS を誤って上書きしてしまう。
                            self.tab_physics.clear();
                            self.current_vel_cache_3d.clear();
                            self.current_vel_cache_2d.clear();
                            self.pending_restore_vel_3d = None;
                            self.pending_restore_vel_2d = None;
                            // 物理タイムラインをリセットしてシーンロード後の初期状態に戻す
                            self.reset_physics_timeline();
                            // 有効な物理スレッドをシーン初期状態で再起動する
                            if self.edit_physics_enabled {
                                self.stop_physics();
                                self.start_physics();
                            }
                            if self.edit_physics_2d_enabled {
                                self.stop_physics_2d();
                                self.start_physics_2d();
                            }
                            // いずれかの物理が有効な場合はタイムラインを初期化して物理スレッドを Pause する。
                            // init_physics_timeline を呼ばないと Pause が送られず、シーンロード直後から
                            // 物理演算が走り続けて再生時に落下済み状態になってしまう。
                            // ただし常時押し戻しモード（RigidBody 無効）は Pause せず走らせ続ける。
                            if self.is_edit_physics_pushback_mode() {
                                self.enter_edit_physics_pushback();
                            } else if self.edit_physics_enabled || self.edit_physics_2d_enabled {
                                self.init_physics_timeline();
                            }
                            // ── Play モードでのシーン再ロード（常駐 Play プロセスの再利用）────
                            // 上の物理再起動は Edit モード用フラグ（edit_physics_enabled 等）で
                            // ゲートされており Play では発火しない。エディタが Play プロセスを
                            // Kill せず常駐保持し、2 回目以降の Play で LOAD_SCENE を送って
                            // シーンだけ差し替える高速起動を実現するため、Play モードでは
                            // 「新規 Play 起動と同じ初期状態」へ明示的にリセットする。
                            //
                            //   1. 物理スレッドを停止する。frame_renderer の初回フレーム末尾の
                            //      起動ロジックが physics_thread.is_none() を検出し、次フレームで
                            //      新シーンのコライダーから物理ワールドを再構築する。
                            //   2. ゲーム内時間（clock.anim_time）を 0 に戻す。スクリプトが読む
                            //      Time.time / deltaTime を新規 Play と揃える。Clock::new() は
                            //      debug_guard を false に戻すが、デバッガ再アタッチ時に
                            //      DBG_GUARD IPC で再設定されるため問題ない。
                            //   3. paused を解除する（保持中に PAUSE 相当を受けていた場合の保険）。
                            //
                            // パーティクル全解放（particle_system.clear_all）とスクリプト
                            // インスタンス再生成（Scene::load が新規 C# インスタンスを生成し、
                            // 旧インスタンスは旧シーンの Drop で破棄）は上の共通処理で
                            // 実施済みのため、ここでは追加不要。
                            if self.mode == RuntimeMode::Play {
                                self.stop_physics();
                                self.stop_physics_2d();
                                self.clock = crate::engine::core::clock::Clock::new();
                                self.paused = false;
                            }
                            if let Some(ipc) = &self.ipc {
                                ipc.send("SCENE_LOADED");
                                let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
                                ipc.send(&format!("CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"));
                            }
                        }
                        Some(Err(e)) => {
                            if let Some(ipc) = &self.ipc {
                                ipc.send(&format!("LOAD_ERROR:{e}"));
                            }
                        }
                        None => {}
                    }
                }
                IpcCommand::EnterPlay => {
                    // 埋め込みインプレース Play 開始（フェーズ2）。詳細は play_mode_ops.rs。
                    self.enter_play();
                }
                IpcCommand::ExitPlay => {
                    // 埋め込みインプレース Play 停止（フェーズ2）。詳細は play_mode_ops.rs。
                    self.exit_play();
                }
                IpcCommand::OpenActor { path, world_line } => {
                    if self.draw_ctx.is_some() {
                        // カメラ状態を退避
                        let pos = self.camera.base.transform.position;
                        self.saved_cameras.insert(self.active_world_line, DebugCameraData {
                            position: [pos.x, pos.y, pos.z],
                            yaw:      self.camera.yaw,
                            pitch:    self.camera.pitch,
                            fov_deg:  self.camera.base.projection.fov_y_rad.to_degrees(),
                            far:      self.camera.base.projection.far,
                            speed:    self.camera.move_speed,
                        });

                        // main_scene を確保し、同じ world_line の古いアクターを World ごと除去する
                        let main_scene = self.scene.get_or_insert_with(|| Scene::new("main"));
                        {
                            fn collect_entities(actor: &crate::engine::structs::objects::Actor, out: &mut Vec<crate::engine::ecs::Entity>) {
                                out.push(actor.entity);
                                out.extend(actor.slot_entities());
                                for child in actor.children() { collect_entities(child, out); }
                            }
                            let mut to_despawn = Vec::new();
                            for a in main_scene.actors.iter().filter(|a| a.world_line == world_line) {
                                collect_entities(a, &mut to_despawn);
                            }
                            for e in to_despawn { main_scene.world.despawn(e); }
                            main_scene.actors.retain(|a| a.world_line != world_line);
                        }

                        let result = Scene::load_actor_into(
                            std::path::Path::new(&path),
                            self.draw_ctx.as_ref().unwrap(),
                            &mut main_scene.world,
                            self.scripting_host.as_ref(),
                            world_line,
                            None, // ルートエンティティは新規 spawn
                        );

                        match result {
                            Ok(actor) => {
                                if actor.is_2d() {
                                    self.canvas_world_lines.insert(world_line);
                                    // アクター編集タブの 2D 世界線として登録（常にスクリーンスペース）
                                    self.actor_edit_canvas_wls.insert(world_line);
                                } else {
                                    self.canvas_world_lines.remove(&world_line);
                                    self.actor_edit_canvas_wls.remove(&world_line);
                                }
                                main_scene.actors.push(actor);
                                self.active_world_line = world_line;
                                let cam = self.saved_cameras.get(&world_line).cloned()
                                    .unwrap_or_else(DebugCameraData::default);
                                self.apply_camera_data(&cam);
                                self.selected_instances.clear();
                                self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
                                self.undo_history = crate::engine::core::app_base::undo::UndoHistory::new();
                                self.send_selected();
                                self.do_send_hierarchy();
                                self.send_world_line_info();
                                if let Some(ipc) = &self.ipc {
                                    ipc.send("ACTOR_EDIT_STARTED");
                                    let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
                                    ipc.send(&format!("CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"));
                                }
                            }
                            Err(e) => {
                                if let Some(ipc) = &self.ipc {
                                    ipc.send(&format!("LOAD_ERROR:{e}"));
                                }
                            }
                        }
                    }
                }
                IpcCommand::EditCanvasBegin { world_line, actor_dfs_id } => {
                    // シーン内キャンバスアクターの隔離編集タブを開始する
                    self.handle_edit_canvas_begin(world_line, actor_dfs_id);
                }
                IpcCommand::EditCanvasEnd(wl) => {
                    // キャンバス編集タブを終了してアクターをシーンへ戻す
                    self.handle_edit_canvas_end(wl);
                }
                IpcCommand::SetActiveWorldLine(wl) => {
                    // ── タブ切替フック（物理状態のタブごと保持）─────────────
                    // 別の world_line（アクター編集タブ/シーン）へ移る場合のみ、
                    // 現タブの物理状態を退避する。同一 wl 再送は物理に触れない。
                    // leave は active_world_line 差し替え前に呼ぶ（旧タブを指すため）。
                    let tab_changed = self.active_world_line != wl;
                    if tab_changed { self.leave_current_tab_physics(); }
                    // 現在の世界線のカメラを退避
                    let pos = self.camera.base.transform.position;
                    self.saved_cameras.insert(self.active_world_line, DebugCameraData {
                        position: [pos.x, pos.y, pos.z],
                        yaw:      self.camera.yaw,
                        pitch:    self.camera.pitch,
                        fov_deg:  self.camera.base.projection.fov_y_rad.to_degrees(),
                        far:      self.camera.base.projection.far,
                        speed:    self.camera.move_speed,
                    });
                    self.active_world_line = wl;
                    if let Some(cam) = self.saved_cameras.get(&wl).cloned() {
                        self.apply_camera_data(&cam);
                    }
                    self.selected_instances.clear();
                    self.actor_virtual_selected_idx = None;
                    self.actor_virtual_selected_slot_idx = 0;
                    self.undo_history = crate::engine::core::app_base::undo::UndoHistory::new();
                    self.send_selected();
                    self.do_send_hierarchy();
                    self.send_world_line_info();
                    if let Some(ipc) = &self.ipc {
                        if wl == 0 {
                            ipc.send("ACTOR_EDIT_ENDED");
                        } else {
                            ipc.send("ACTOR_EDIT_STARTED");
                        }
                        let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
                        ipc.send(&format!("CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"));
                    }
                    // 移動先タブの物理状態を復元する（active_world_line 差し替え後に呼ぶ）。
                    if tab_changed { self.enter_tab_physics(); }
                }
                IpcCommand::RemoveWorldLine(wl) => {
                    if let Some(scene) = &mut self.scene {
                        scene.actors.retain(|a| a.world_line != wl);
                    }
                    self.saved_cameras.remove(&wl);
                    self.canvas_world_lines.remove(&wl);
                    self.actor_edit_canvas_wls.remove(&wl);
                    self.canvas_cameras.remove(&wl);
                }
                IpcCommand::AddComponent { actor_dfs_id, component_type, slot_name, args } => {
                    // Canvas 上に配置するコンポーネント（SpriteComponent 等）は
                    // 自動親子化ロジックを経由して追加する
                    let ct = component_type.clone();
                    let sn = slot_name.clone();
                    let a  = args.clone();
                    match ct.as_str() {
                        // Canvas 上に配置するコンポーネント → 親子化ロジック経由
                        "SpriteComponent" | "SkinnedSpriteComponent" | "Collider2dComponent" => {
                            self.handle_add_canvas_child_component(actor_dfs_id, &ct, &sn, &a);
                        }
                        _ => {
                            self.handle_add_component_to_actor(actor_dfs_id, &ct, &sn, &a);
                        }
                    }
                }
                IpcCommand::GetActorComponents(dfs_id) => {
                    self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                }
                IpcCommand::AddActor { world_line, parent_dfs_id } => {
                    if let Some((sx, sy)) = self.context_menu_screen_pos.take() {
                        // コンテキストメニュー経由: D&D と同じ IDバッファ読み取り方式で
                        // スポーン位置を次フレームで確定する
                        self.pending_add_actor = Some((false, world_line, parent_dfs_id, sx, sy));
                    } else {
                        self.handle_add_actor(world_line, parent_dfs_id, None);
                    }
                }
                IpcCommand::AddActor2D { world_line, parent_dfs_id } => {
                    if let Some(_) = self.context_menu_screen_pos.take() {
                        // 2D アクターはスポーン位置を使わないが pending 方式で処理タイミングを統一する
                        self.pending_add_actor = Some((true, world_line, parent_dfs_id, 0, 0));
                    } else {
                        self.handle_add_actor_2d(world_line, parent_dfs_id);
                    }
                }
                IpcCommand::AddActorChild { parent_dfs_id } => {
                    self.handle_add_actor_child(parent_dfs_id);
                }
                IpcCommand::AddActor2dChild { parent_dfs_id } => {
                    self.handle_add_actor_2d_child(parent_dfs_id);
                }
                IpcCommand::WrapActor { child_dfs, is_2d } => {
                    self.handle_wrap_actor(child_dfs, is_2d);
                }
                IpcCommand::RemoveActor(dfs_id) => {
                    self.handle_remove_actor(dfs_id);
                }
                IpcCommand::RenameActor { dfs_id, name } => {
                    let n = name.clone();
                    self.handle_rename_actor(dfs_id, &n);
                }
                IpcCommand::RemoveComponentSlot { actor_dfs_id, slot_idx } => {
                    self.handle_remove_component_slot(actor_dfs_id, slot_idx);
                }
                IpcCommand::RenameComponentSlot { actor_dfs_id, slot_idx, name } => {
                    let n = name.clone();
                    self.handle_rename_component_slot(actor_dfs_id, slot_idx, &n);
                }
                IpcCommand::SetActorTransform { dfs_id, px, py, pz, ex, ey, ez, sx, sy, sz } => {
                    self.handle_set_actor_transform(dfs_id, px, py, pz, ex, ey, ez, sx, sy, sz);
                }
                IpcCommand::SetCanvasTransform { dfs_id, px, py, rotation, sx, sy, pivot_x, pivot_y } => {
                    self.handle_set_canvas_transform(dfs_id, px, py, rotation, sx, sy, pivot_x, pivot_y);
                }
                IpcCommand::SetCanvasSize { actor_dfs_id, slot_idx, width, height } => {
                    self.handle_set_canvas_size(actor_dfs_id, slot_idx, width, height);
                }
                IpcCommand::SetModelPath { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_model_path(actor_dfs_id, slot_idx, &p);
                }
                IpcCommand::SetModelField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_model_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetMaterialOverride { actor_dfs_id, slot_idx, mat_slot, json } => {
                    self.handle_set_material_override(actor_dfs_id, slot_idx, mat_slot, &json);
                }
                IpcCommand::SetScriptField { actor_dfs_id, slot_idx, field, value } => {
                    self.handle_set_script_field(actor_dfs_id, slot_idx, &field, &value);
                }
                IpcCommand::ReloadScripts => {
                    self.handle_reload_scripts();
                }
                IpcCommand::DuplicateComponent { actor_dfs_id, slot_idx } => {
                    self.handle_duplicate_component(actor_dfs_id, slot_idx);
                }
                IpcCommand::DropActor { path, screen_x, screen_y } => {
                    self.pending_drop       = Some((path, screen_x, screen_y));
                    self.pending_drop_hover = None;
                    self.drop_preview_pos   = None;
                }
                IpcCommand::DragHover { x, y } => {
                    self.pending_drop_hover = Some((x, y));
                }
                IpcCommand::DragHoverEnd => {
                    self.pending_drop_hover = None;
                    self.drop_preview_pos   = None;
                    // 2D シーンビューのキャンバスホバーハイライトも解除する
                    self.drag_hover_canvas_entity = None;
                }
                IpcCommand::SetSpritePath { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_sprite_path(actor_dfs_id, slot_idx, &p);
                }
                IpcCommand::SetSpritePostfx { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_sprite_postfx(actor_dfs_id, slot_idx, &p);
                }
                IpcCommand::SetSpriteColor { actor_dfs_id, slot_idx, r, g, b, a } => {
                    self.handle_set_sprite_color(actor_dfs_id, slot_idx, r, g, b, a);
                }
                IpcCommand::SetSpriteSize { actor_dfs_id, slot_idx, width, height } => {
                    self.handle_set_sprite_size(actor_dfs_id, slot_idx, width, height);
                }
                IpcCommand::SetSpriteLayer { actor_dfs_id, slot_idx, layer } => {
                    self.handle_set_sprite_layer(actor_dfs_id, slot_idx, layer);
                }
                IpcCommand::SetSpriteField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_sprite_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetLightField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_light_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetWaterField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_water_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetWaterShaderParam { actor_dfs_id, slot_idx, name, value } => {
                    // 水面シェーディングアセットのパラメータ値（W8.2）。
                    self.handle_set_water_shader_param(actor_dfs_id, slot_idx, &name, &value);
                }
                IpcCommand::ResetWaterShaderParam { actor_dfs_id, slot_idx, name } => {
                    // 「デフォルトに戻す」ボタン（W8.2）。上書き値を消して既定値へ落とす。
                    self.handle_reset_water_shader_param(actor_dfs_id, slot_idx, &name);
                }
                IpcCommand::ResetComponentField { actor_dfs_id, slot_idx, field } => {
                    // インスペクタ各行の「⟲ デフォルトに戻す」ボタン（全コンポーネント共通）。
                    self.handle_reset_component_field(actor_dfs_id, slot_idx, &field);
                }
                IpcCommand::SetWaterShaderBinding { actor_dfs_id, slot_idx, name, binding } => {
                    // `@ref` パラメータのバインド設定・解除（W8.3）。
                    self.handle_set_water_shader_binding(actor_dfs_id, slot_idx, &name, &binding);
                }
                IpcCommand::GetBindableSources { actor_dfs_id, value_type } => {
                    // バインド元候補の問い合わせ（W8.3）。読み取りのみで状態は変えない。
                    self.handle_get_bindable_sources(actor_dfs_id, &value_type);
                }
                IpcCommand::SetWaterLinkField { actor_dfs_id, slot_idx, key, value } => {
                    // 水位グラフの開口（W2.5）。バルブ開閉・寸法・接続先の更新。
                    self.handle_set_water_link_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetControlPoints { actor_dfs_id, slot_idx, json } => {
                    self.handle_set_control_points(actor_dfs_id, slot_idx, &json);
                }
                IpcCommand::SetControlPointPos { actor_dfs_id, slot_idx, index, x, y, z } => {
                    self.handle_set_control_point_pos(actor_dfs_id, slot_idx, index, [x, y, z]);
                }
                IpcCommand::SelectControlPoint { actor_dfs_id, slot_idx, index } => {
                    self.handle_select_control_point(actor_dfs_id, slot_idx, index);
                }
                IpcCommand::AddControlPointAtScreen { actor_dfs_id, slot_idx, screen_x, screen_y } => {
                    // ID バッファの読み戻しはフレーム内（GPU サブミット後）でしか行えないため、
                    // ここでは解決せずキューに積むだけにする（pending_drop と同じ「次フレームで解決」方式）。
                    self.pending_control_point_drop = Some((actor_dfs_id, slot_idx, screen_x, screen_y));
                    // 実機での切り分け用に常時 1 行だけ出す（ドロップ時のみなのでログは荒れない）。
                    // 対になる着弾結果は frame_renderer.rs が `→ hit=` として出す。
                    eprintln!(
                        "[CTRL_POINT] drop request actor={} slot={} x={} y={}",
                        actor_dfs_id, slot_idx, screen_x, screen_y
                    );
                }
                IpcCommand::ControlPointDragHover { screen_x, screen_y } => {
                    // 解決はフレームループ側（ID バッファ読み戻しが要るため）。
                    // ここでは最新の 1 件だけを保持する（古い座標は捨てる）。
                    self.pending_control_point_hover = Some((screen_x, screen_y));
                }
                IpcCommand::ControlPointDragEnd => {
                    // 未解決の問い合わせと配置予定マーカーの両方を捨てる。
                    self.pending_control_point_hover = None;
                    self.control_point_drop_preview  = None;
                }
                IpcCommand::SetCoverField { actor_dfs_id, slot_idx, key, value } => {
                    // カバーエミッタ（I3.1）のインスペクタ更新。
                    self.handle_set_cover_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetInteractionField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_interaction_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetJointAttachField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_jointattach_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetSkyboxField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_skybox_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetParticleField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_particle_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetParticleCurve { actor_dfs_id, slot_idx, curve_id, json } => {
                    self.handle_set_particle_curve(actor_dfs_id, slot_idx, &curve_id, &json);
                }
                IpcCommand::SetAudioField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_audio_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetLineRendererField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_line_renderer_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetTextField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_text_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetSkinnedSpriteField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_skinned_sprite_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetSkinnedSpriteBoneOverrides { actor_dfs_id, slot_idx, json } => {
                    let j = json.clone();
                    self.handle_set_skinned_sprite_bone_overrides(actor_dfs_id, slot_idx, &j);
                }
                IpcCommand::CreateSpriteBoneActors { actor_dfs_id, slot_idx } => {
                    self.handle_create_sprite_bone_actors(actor_dfs_id, slot_idx);
                }
                IpcCommand::SetAnimatorClips { actor_dfs_id, slot_idx, json } => {
                    self.handle_set_animator_clips(actor_dfs_id, slot_idx, &json);
                }
                IpcCommand::AnimPreview { actor_dfs_id, clip_path, time } => {
                    self.handle_anim_preview(actor_dfs_id, &clip_path, time);
                }
                IpcCommand::AnimPreviewStop { actor_dfs_id } => {
                    self.handle_anim_preview_stop(actor_dfs_id);
                }
                IpcCommand::AnimReload { clip_path } => {
                    self.handle_anim_reload(&clip_path);
                }
                IpcCommand::SetCanvasAnchor { actor_dfs_id, ax, ay } => {
                    self.handle_set_canvas_anchor(actor_dfs_id, ax, ay);
                }
                IpcCommand::SetCanvasTransformScaleMode { actor_dfs_id, scale_transform, scale_size, keep_aspect, axis } => {
                    self.handle_set_canvas_transform_scale_mode(actor_dfs_id, scale_transform, scale_size, keep_aspect, axis);
                }
                IpcCommand::SetCanvasAutoScale { actor_dfs_id, slot_idx, auto_scale } => {
                    self.handle_set_canvas_auto_scale(actor_dfs_id, slot_idx, auto_scale);
                }
                IpcCommand::SetCanvasGravityMode { actor_dfs_id, slot_idx, mode } => {
                    self.handle_set_canvas_gravity_mode(actor_dfs_id, slot_idx, mode);
                }
                IpcCommand::SetCanvasDrawZone { actor_dfs_id, slot_idx, zone } => {
                    let z = zone.clone();
                    self.handle_set_canvas_draw_zone(actor_dfs_id, slot_idx, &z);
                }
                IpcCommand::SetCollider2dAspectRatio { actor_dfs_id, slot_idx, keep, axis } => {
                    let a = axis.clone();
                    self.handle_set_collider2d_aspect_ratio(actor_dfs_id, slot_idx, keep, &a);
                }
                IpcCommand::SetCanvasViewportRefWindow { actor_dfs_id, slot_idx } => {
                    self.handle_set_canvas_viewport_ref_window(actor_dfs_id, slot_idx);
                }
                IpcCommand::SetCanvasViewportRefMainCamera { actor_dfs_id, slot_idx } => {
                    self.handle_set_canvas_viewport_ref_main_camera(actor_dfs_id, slot_idx);
                }
                IpcCommand::SetCanvasViewportRefCamera { actor_dfs_id, slot_idx, actor_name, slot_name } => {
                    let an = actor_name.clone();
                    let sn = slot_name.clone();
                    self.handle_set_canvas_viewport_ref_camera(actor_dfs_id, slot_idx, an, sn);
                }
                IpcCommand::SetCanvas3dPivot { actor_dfs_id, slot_idx, pivot_x, pivot_y } => {
                    self.handle_set_canvas_3d_pivot(actor_dfs_id, slot_idx, pivot_x, pivot_y);
                }
                IpcCommand::SetInputMapPath { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_inputmap_path(actor_dfs_id, slot_idx, &p);
                }
                // ── CameraComponent プロパティ設定 ───────────────────────────
                IpcCommand::SetCameraComponentFov { actor_dfs_id, slot_idx, value } => {
                    self.handle_set_camera_fov(actor_dfs_id, slot_idx, value);
                }
                IpcCommand::SetCameraComponentNear { actor_dfs_id, slot_idx, value } => {
                    self.handle_set_camera_near(actor_dfs_id, slot_idx, value);
                }
                IpcCommand::SetCameraComponentFar { actor_dfs_id, slot_idx, value } => {
                    self.handle_set_camera_far(actor_dfs_id, slot_idx, value);
                }
                IpcCommand::SetCameraComponentMain { actor_dfs_id, slot_idx, is_main } => {
                    self.handle_set_camera_main(actor_dfs_id, slot_idx, is_main);
                }
                IpcCommand::SetCameraComponentClearColor { actor_dfs_id, slot_idx, r, g, b, a } => {
                    self.handle_set_camera_clear_color(actor_dfs_id, slot_idx, r, g, b, a);
                }
                IpcCommand::SetCameraComponentScalingMode { actor_dfs_id, slot_idx, mode } => {
                    let m = mode.clone();
                    self.handle_set_camera_scaling_mode(actor_dfs_id, slot_idx, &m);
                }
                IpcCommand::SetCameraComponentTargetSize { actor_dfs_id, slot_idx, width, height } => {
                    self.handle_set_camera_target_size(actor_dfs_id, slot_idx, width, height);
                }
                IpcCommand::SetCameraBarColor { actor_dfs_id, slot_idx, r, g, b, a } => {
                    self.handle_set_camera_bar_color(actor_dfs_id, slot_idx, r, g, b, a);
                }
                IpcCommand::SetCameraComponentProjection { actor_dfs_id, slot_idx, mode } => {
                    let m = mode.clone();
                    self.handle_set_camera_projection(actor_dfs_id, slot_idx, &m);
                }
                IpcCommand::SetCameraComponentOrthoHeight { actor_dfs_id, slot_idx, value } => {
                    self.handle_set_camera_ortho_height(actor_dfs_id, slot_idx, value);
                }
                IpcCommand::SetCameraComponentShadingAsset { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_camera_shading_asset(actor_dfs_id, slot_idx, &p);
                }
                IpcCommand::SetCameraShadingParam { actor_dfs_id, slot_idx, name, value } => {
                    let (n, v) = (name.clone(), value.clone());
                    self.handle_set_camera_shading_param(actor_dfs_id, slot_idx, &n, &v);
                }
                IpcCommand::ResetCameraShadingParam { actor_dfs_id, slot_idx, name } => {
                    let n = name.clone();
                    self.handle_reset_camera_shading_param(actor_dfs_id, slot_idx, &n);
                }
                IpcCommand::SetCameraShadingBinding { actor_dfs_id, slot_idx, name, binding } => {
                    let (n, b) = (name.clone(), binding.clone());
                    self.handle_set_camera_shading_binding(actor_dfs_id, slot_idx, &n, &b);
                }
                IpcCommand::SetSceneShadingParam { name, value } => {
                    let (n, v) = (name.clone(), value.clone());
                    self.handle_set_scene_shading_param(&n, &v);
                }
                IpcCommand::ResetSceneShadingParam { name } => {
                    let n = name.clone();
                    self.handle_reset_scene_shading_param(&n);
                }
                IpcCommand::SetSceneShadingBinding { name, binding } => {
                    let (n, b) = (name.clone(), binding.clone());
                    self.handle_set_scene_shading_binding(&n, &b);
                }
                IpcCommand::GetSceneShadingParams => {
                    // シーン設定ウィンドウのパラメータ行を作るための問い合わせ（読み取りのみ）。
                    self.send_scene_shading_params();
                }
                IpcCommand::SetSceneShadingAsset { path } => {
                    // シーン既定のシェーディングアセットを更新する（空文字は未設定＝None）
                    let trimmed = path.trim();
                    if let Some(scene) = self.scene.as_mut() {
                        scene.shading_asset = if trimmed.is_empty() {
                            None
                        } else {
                            Some(trimmed.to_string())
                        };
                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                    }
                    // アセットが変われば宣言も変わるので、パラメータ行を作り直させる。
                    self.send_scene_shading_params();
                }
                IpcCommand::ValidateWgsl { request_id, source } => {
                    // シェーディングアセットの未保存 WGSL を検証して診断を返す（保存・GPU 不要）。
                    self.handle_validate_wgsl(request_id, &source);
                }
                IpcCommand::SetSceneSettings { json } => {
                    // シーン単位のビューポート／レンダリング設定（.scene の settings 節）を更新する。
                    //
                    // ── ここで設定をランタイムへ「適用しない」理由 ──────────────────
                    // エディタは値を変更した時点で既存の個別 IPC（SET_POST_FX / VIEWPORT_FOV /
                    // SHOW_GRID / SET_AMBIENT 等）を送ってライブ適用済みである。ここで
                    // apply_scene_settings を再実行すると二重適用になるうえ、本スキーマに
                    // 含まれない view_mode（シーンビュー表示モード。セッション限りの非永続設定）が
                    // 既定値で潰されてしまう。よってこのコマンドは「保存用データの格納」のみ行う。
                    // 実際の適用はシーンロード時（load_play_scene / LOAD_SCENE）だけで行う。
                    match serde_json::from_str::<
                        crate::engine::core::app_base::scene_settings::SceneSettingsData,
                    >(&json) {
                        Ok(parsed) => {
                            if let Some(scene) = self.scene.as_mut() {
                                scene.settings = Some(parsed);
                                if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                            }
                        }
                        Err(e) => {
                            eprintln!("[SEED IPC] SET_SCENE_SETTINGS の JSON 解析に失敗（無視）: {e}");
                        }
                    }
                }
                IpcCommand::SetActorActive { dfs_id, active } => {
                    // アクターのアクティブ切替（Unity の SetActive 相当）。
                    // 子孫への影響（実効アクティブ）は各収集処理が親の active を
                    // 継承して判定するため、自身のフラグのみ書き換えればよい。
                    let wl = self.active_world_line;
                    if let Some(scene) = self.scene.as_mut() {
                        let mut c = 0u32;
                        if let Some(actor) = super::find_actor_by_dfs_mut(&mut scene.actors, wl, dfs_id, &mut c) {
                            actor.active = active;
                            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                            // ヒエラルキーの淡色表示を更新するため再送する
                            self.send_hierarchy();
                        }
                    }
                }
                IpcCommand::SetSlotEnabled { actor_dfs_id, slot_idx, enabled } => {
                    // コンポーネントスロットの有効切替（Unity の enabled 相当）
                    let wl = self.active_world_line;
                    if let Some(scene) = self.scene.as_mut() {
                        let mut c = 0u32;
                        if let Some(actor) = super::find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                            if let Some(slot) = actor.slots_mut().get_mut(slot_idx as usize) {
                                slot.enabled = enabled;
                                if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                            }
                        }
                    }
                }
                // ── 物理コンポーネント ──────────────────────────────────────
                IpcCommand::SetColliderData { actor_dfs_id, slot_idx, json } => {
                    let j = json.clone();
                    self.handle_set_collider_data(actor_dfs_id, slot_idx, &j);
                }
                IpcCommand::SetCollider2dData { actor_dfs_id, slot_idx, json } => {
                    // 2D コライダーコンポーネントデータを JSON でまとめて更新する
                    let j = json.clone();
                    self.handle_set_collider2d_data(actor_dfs_id, slot_idx, &j);
                }
                // ── プラグインコンポーネント ─────────────────────────────────
                IpcCommand::SetPluginField { actor_dfs_id, slot_idx, key, value } => {
                    let k = key.clone();
                    let v = value.clone();
                    self.handle_set_plugin_field(actor_dfs_id, slot_idx, &k, &v);
                }
                IpcCommand::GetPluginList => {
                    self.send_plugin_list();
                }

                // ── AI アシスタント用コマンド ─────────────────────────────────
                IpcCommand::GetSceneInfo => {
                    self.send_scene_info();
                }
                IpcCommand::AiAddActor { name, x, y, z } => {
                    self.handle_ai_add_actor(&name.clone(), x, y, z);
                }
                IpcCommand::AiRemoveActor { actor_dfs_id } => {
                    self.apply_delete(&[actor_dfs_id], true);
                    self.send_hierarchy();
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
                IpcCommand::AiMoveActor { actor_dfs_id, x, y, z } => {
                    self.handle_ai_move_actor(actor_dfs_id, x, y, z);
                }
                IpcCommand::AiAddComponent { actor_dfs_id, component_type, params_json } => {
                    let ct = component_type.clone();
                    let pj = params_json.clone();
                    self.handle_ai_add_component(actor_dfs_id, &ct, &pj);
                }
                IpcCommand::AiSetValue { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_component_value(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::ExportActor { dfs_id, path } => {
                    self.handle_export_actor(dfs_id, &path);
                }
                IpcCommand::UnlinkPrefab { actor_dfs } => {
                    self.handle_unlink_prefab(actor_dfs);
                }
                IpcCommand::ReapplyPrefab { actor_dfs } => {
                    // 明示操作による「プレハブから更新」。シーン側の変更を破棄して
                    // .actor の内容で再展開する（確認ダイアログはエディタ側で表示済み）。
                    self.handle_reapply_prefab(actor_dfs);
                }
                IpcCommand::ReapplyAllPrefabs => {
                    // 明示操作による「シーン内の全プレハブを更新」。
                    // シーン側の変更を破棄して .actor の内容で全インスタンスを再展開する
                    // （確認ダイアログはエディタ側で表示済み）。
                    self.handle_reapply_all_prefabs();
                }

                // ── 物理シミュレーション設定 ─────────────────────────────────
                IpcCommand::SetEditPhysics { enabled, with_rigidbody } => {
                    // Play モードでは無視する（Play 起動時の SyncViewportSettings による誤停止を防ぐ）
                    if self.mode != RuntimeMode::Edit { continue; }
                    // 編集時の物理シミュレーション設定を更新する
                    self.edit_physics_enabled        = enabled;
                    self.edit_physics_with_rigidbody = with_rigidbody;
                    if enabled {
                        // 既存スレッドを停止してから新しい設定で再起動する
                        self.stop_physics();
                        self.start_physics();
                        if self.is_edit_physics_pushback_mode() {
                            // 【変更3(a)】RigidBody 無効: 常時押し戻しモード。
                            // タイムラインを使わず、物理を Pause せずに走らせ続ける
                            // （起動直後の paused=false のまま）ことでドラッグ押し戻しを常時有効化する。
                            self.enter_edit_physics_pushback();
                        } else {
                            // タイムラインを初期化する（初期状態をフレーム0として記録・Pause 送信）
                            self.init_physics_timeline();
                        }
                    } else {
                        self.stop_physics();
                        // 無効化時は衝突中 ID セットをクリアして描画色をリセットする
                        self.active_collision_dfs_ids.clear();
                        // タイムラインをリセットする
                        self.reset_physics_timeline();
                    }
                }

                // ── 編集時物理シミュレーション（2D/3D 統合）────────────────────
                // エディタの単一チェックボックスから届く統合コマンド。
                // 3D と 2D の有効／RigidBody を常に同値でミラーし、タイムラインは
                // 3D・2D 共通の 1 本として扱う（タブごと状態保持と組み合わせて使う）。
                IpcCommand::SetEditPhysicsAll { enabled, with_rigidbody } => {
                    // Play モードでは無視する（Play 起動時の再同期による誤停止を防ぐ）
                    if self.mode != RuntimeMode::Edit { continue; }
                    // 3D・2D の設定を同値で更新する
                    self.edit_physics_enabled           = enabled;
                    self.edit_physics_with_rigidbody    = with_rigidbody;
                    self.edit_physics_2d_enabled        = enabled;
                    self.edit_physics_2d_with_rigidbody = with_rigidbody;
                    if enabled {
                        // 既存スレッドを停止し、初速なしで 3D・2D を再起動する
                        self.stop_physics();
                        self.stop_physics_2d();
                        self.start_physics();
                        self.start_physics_2d();
                        if self.is_edit_physics_pushback_mode() {
                            // RigidBody 無効: 常時押し戻しモード（Pause せず走らせ続ける）
                            self.enter_edit_physics_pushback();
                        } else {
                            // RigidBody 有効: タイムラインを初期化（フレーム0記録・Pause 送信）
                            self.init_physics_timeline();
                        }
                    } else {
                        self.stop_physics();
                        self.stop_physics_2d();
                        // 無効化時は衝突中 ID セットをクリアして描画色をリセットする
                        self.active_collision_dfs_ids.clear();
                        self.active_collision_2d_dfs_ids.clear();
                        self.reset_physics_timeline();
                        // 退避済みのタブ物理状態も破棄する（次回有効化はクリーンな初期状態から）。
                        // 破棄しないと、無効化を跨いだ古いスナップショット・速度で
                        // 別タブ復帰時に ECS を誤って上書きしてしまう。
                        self.tab_physics.clear();
                        self.current_vel_cache_3d.clear();
                        self.current_vel_cache_2d.clear();
                    }
                }

                // ── 編集時物理タイムライン操作 ──────────────────────────────
                IpcCommand::EditPhysicsPlayPause => {
                    if self.mode == RuntimeMode::Edit {
                        self.handle_edit_physics_play_pause();
                    }
                }
                IpcCommand::EditPhysicsStep { step } => {
                    if self.mode == RuntimeMode::Edit {
                        self.handle_edit_physics_step(step);
                    }
                }
                IpcCommand::EditPhysicsSeek { frame } => {
                    if self.mode == RuntimeMode::Edit {
                        self.handle_edit_physics_seek(frame);
                    }
                }
                IpcCommand::EditPhysicsApplyFrame => {
                    if self.mode == RuntimeMode::Edit {
                        self.handle_edit_physics_apply_frame();
                    }
                }
                IpcCommand::SetPlayColliderDraw(v) => {
                    // 実行時コライダー描画フラグを更新する
                    self.play_collider_draw = v;
                }
                IpcCommand::SetPlayShaderHotReload(v) => {
                    // Play 中のシェーディングアセット・ホットリロード可否を更新する。
                    // Edit モードのホットリロードはこのフラグに関係なく常に有効なので、
                    // ここで false にしても Edit 中の編集フローには影響しない。
                    self.play_shader_hot_reload = v;
                }
                IpcCommand::SetProfilerEnabled(v) => {
                    // プロファイラパネルの購読状態に追従して計測の有効／無効を切り替える。
                    // 無効化時は集計器も破棄されるため、次に開いたとき古い窓のデータが混ざらない。
                    crate::engine::core::profiling::set_profiling_enabled(v);
                }

                // ── 2D 物理シミュレーション設定 ──────────────────────────────
                IpcCommand::SetEditPhysics2d { enabled, with_rigidbody } => {
                    // Play モードでは無視する（Play 起動時の SyncViewportSettings による誤停止を防ぐ）
                    if self.mode != RuntimeMode::Edit { continue; }
                    // 編集時の 2D 物理シミュレーション設定を更新する
                    self.edit_physics_2d_enabled        = enabled;
                    self.edit_physics_2d_with_rigidbody = with_rigidbody;
                    if enabled {
                        // 既存 2D スレッドを停止してから新しい設定で再起動する
                        self.stop_physics_2d();
                        self.start_physics_2d();
                        if self.is_edit_physics_pushback_mode() {
                            // 【変更3(a)】RigidBody 無効（3D/2D ともタイムライン非使用）:
                            // 常時押し戻しモード。物理を Pause せず走らせ続ける。
                            self.enter_edit_physics_pushback();
                        } else if self.edit_physics_enabled && self.edit_physics_with_rigidbody {
                            // 3D 物理が「RigidBody 有効＝タイムラインモード」で既に動いている場合のみ、
                            // 既存タイムラインが存在するとみなして Pause のみ送信する。
                            // 【バグ修正】従来は self.edit_physics_enabled のみで判定していたため、
                            // 3D が「常時押し戻しモード」（RigidBody 無効）で有効な状態から
                            // 2D を RigidBody 有効で ON にすると、実際にはタイムラインが
                            // 初期化されていない（reset_physics_timeline のみでスナップショット
                            // 空）にもかかわらずこの分岐に入り、init_physics_timeline が
                            // 呼ばれず・エディタへ状態通知もされずタイムライン UI が機能しなかった。
                            // 2D スレッドにも Pause を送って再生ボタンまで動かさない
                            if let Some(thread) = &self.physics_thread_2d {
                                use crate::engine::physics::PhysicsCommand2d;
                                thread.send(PhysicsCommand2d::Pause);
                            }
                        } else {
                            // 3D がタイムラインモードで動いていない（無効 or 押し戻しモード）・
                            // 2D が RigidBody 有効: タイムラインを新規初期化する
                            self.init_physics_timeline();
                        }
                    } else {
                        self.stop_physics_2d();
                        // 無効化時は衝突中 ID セットをクリアして描画色をリセットする
                        self.active_collision_2d_dfs_ids.clear();
                    }
                }
            }

            // ── 適用後の値と比較し、変化していれば Undo 履歴へ積む ──
            //   同一フィールドへの連続更新（スライダー 1 ドラッグ）は 1 コマンドへマージされる。
            if let Some(before) = fe_before {
                self.field_edit_record(&fe_target, before);
            }
        }

        // ── フレーム末の地形ダーティ集約を消化する ──
        //   このフレームに届いたブラシ／ペイントが触れた全チャンクを重複無しで
        //   1 回だけ再メッシュする。IPC 応答（TERRAIN_BRUSH_OK 等）は各コマンド処理時に
        //   既に返してあるので、ここで送信タイミング・文言が変わることはない。
        self.flush_terrain_pending_remesh();
    }

    // ============================================================
    //  AI フィールド変更ディスパッチャ
    // ============================================================

    /// AI からの AI_SET_VALUE コマンドを、コンポーネント型に応じて各ハンドラへ振り分ける。
    fn handle_set_component_value(&mut self, actor_dfs_id: u32, slot_idx: u32, key: &str, value: &str) {
        use crate::engine::components::ComponentKind;

        let wl = self.active_world_line;
        let kind = {
            let scene = match self.scene.as_ref() { Some(s) => s, None => return };
            let mut c = 0u32;
            let actor = match super::find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) {
                Some(a) => a,
                None => return,
            };
            actor.slots().get(slot_idx as usize).map(|s| s.kind)
        };

        let Some(kind) = kind else { return };

        match kind {
            // ModelComponent: "model_path"
            ComponentKind::Model if key == "model_path" => {
                self.handle_set_model_path(actor_dfs_id, slot_idx, value);
            }

            // CameraComponent: "fov", "near", "far", "is_main", "clear_color"
            ComponentKind::Camera => {
                match key {
                    "fov" | "fov_y_deg" => {
                        if let Ok(v) = value.parse::<f32>() { self.handle_set_camera_fov(actor_dfs_id, slot_idx, v); }
                    }
                    "near" => {
                        if let Ok(v) = value.parse::<f32>() { self.handle_set_camera_near(actor_dfs_id, slot_idx, v); }
                    }
                    "far" => {
                        if let Ok(v) = value.parse::<f32>() { self.handle_set_camera_far(actor_dfs_id, slot_idx, v); }
                    }
                    "is_main" => {
                        let b = value == "true" || value == "1";
                        self.handle_set_camera_main(actor_dfs_id, slot_idx, b);
                    }
                    "clear_color" => {
                        // "r,g,b,a" 形式を想定
                        let parts: Vec<f32> = value.split(',').filter_map(|s| s.trim().parse().ok()).collect();
                        if parts.len() == 4 {
                            self.handle_set_camera_clear_color(actor_dfs_id, slot_idx, parts[0], parts[1], parts[2], parts[3]);
                        }
                    }
                    _ => {}
                }
            }

            // SpriteComponent: "texture_path", "color", "width", "height"
            ComponentKind::Sprite => {
                match key {
                    "texture_path" | "path" => {
                        self.handle_set_sprite_path(actor_dfs_id, slot_idx, value);
                    }
                    "color" => {
                        let parts: Vec<f32> = value.split(',').filter_map(|s| s.trim().parse().ok()).collect();
                        if parts.len() == 4 {
                            self.handle_set_sprite_color(actor_dfs_id, slot_idx, parts[0], parts[1], parts[2], parts[3]);
                        }
                    }
                    "width" => {
                        if let Ok(v) = value.parse::<f32>() {
                            let h = {
                                let scene = self.scene.as_ref().unwrap();
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                                    .and_then(|a| a.slots().get(slot_idx as usize))
                                    .and_then(|s| scene.world.get::<crate::engine::components::SpriteComponent>(s.entity))
                                    .map(|s| s.height)
                                    .unwrap_or(100.0)
                            };
                            self.handle_set_sprite_size(actor_dfs_id, slot_idx, v, h);
                        }
                    }
                    "height" => {
                        if let Ok(v) = value.parse::<f32>() {
                            let w = {
                                let scene = self.scene.as_ref().unwrap();
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                                    .and_then(|a| a.slots().get(slot_idx as usize))
                                    .and_then(|s| scene.world.get::<crate::engine::components::SpriteComponent>(s.entity))
                                    .map(|s| s.width)
                                    .unwrap_or(100.0)
                            };
                            self.handle_set_sprite_size(actor_dfs_id, slot_idx, w, v);
                        }
                    }
                    _ => {}
                }
            }

            // CanvasComponent: "width", "height", "auto_scale"
            // （スケールモードは CanvasTransform 側へ移動。SET_CANVAS_TRANSFORM_SCALE_MODE で設定する）
            ComponentKind::Canvas => {
                match key {
                    "width" => {
                        if let Ok(v) = value.parse::<f32>() {
                            let h = {
                                let scene = self.scene.as_ref().unwrap();
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                                    .and_then(|a| a.slots().get(slot_idx as usize))
                                    .and_then(|s| scene.world.get::<crate::engine::components::CanvasComponent>(s.entity))
                                    .map(|s| s.height)
                                    .unwrap_or(1080.0)
                            };
                            self.handle_set_canvas_size(actor_dfs_id, slot_idx, v, h);
                        }
                    }
                    "height" => {
                        if let Ok(v) = value.parse::<f32>() {
                            let w = {
                                let scene = self.scene.as_ref().unwrap();
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                                    .and_then(|a| a.slots().get(slot_idx as usize))
                                    .and_then(|s| scene.world.get::<crate::engine::components::CanvasComponent>(s.entity))
                                    .map(|s| s.width)
                                    .unwrap_or(1920.0)
                            };
                            self.handle_set_canvas_size(actor_dfs_id, slot_idx, w, v);
                        }
                    }
                    "auto_scale" => {
                        let b = value == "true" || value == "1";
                        self.handle_set_canvas_auto_scale(actor_dfs_id, slot_idx, b);
                    }
                    _ => {}
                }
            }

            // InputMapComponent: "asset_path"
            ComponentKind::InputMap if key == "asset_path" || key == "path" => {
                self.handle_set_inputmap_path(actor_dfs_id, slot_idx, value);
            }

            // PluginComponent: 全フィールド
            ComponentKind::Plugin => {
                self.handle_set_plugin_field(actor_dfs_id, slot_idx, key, value);
            }

            _ => {
                // その他のコンポーネントや未知のキーは現状無視（必要に応じて拡張）
            }
        }
    }

    // ============================================================
    //  プラグインフィールド変更処理
    // ============================================================

    /// プラグインコンポーネントのフィールド値を変更する。
    /// Undo 記録は field_edit.rs の汎用機構が担当する（ここでは記録しない）。
    fn handle_set_plugin_field(&mut self, actor_dfs_id: u32, slot_idx: u32, key: &str, new_value: &str) {
        use crate::engine::components::{PluginComponent, ComponentKind};

        let wl    = self.active_world_line;
        let scene = match self.scene.as_mut() { Some(s) => s, None => return };

        // スロットエンティティと plugin_name を取得する
        let (slot_entity, plugin_name, old_value) = {
            let mut c = 0u32;
            let actor = match super::find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) {
                Some(a) => a,
                None    => return,
            };
            let slot = match actor.slots().get(slot_idx as usize) {
                Some(s) if s.kind == ComponentKind::Plugin => s,
                _ => return,
            };
            let entity = slot.entity;
            let (pname, oval) = if let Some(pc) = scene.world.get::<PluginComponent>(entity) {
                let old = pc.fields.get(key).cloned().unwrap_or_default();
                (pc.plugin_name.clone(), old)
            } else { return };
            (entity, pname, oval)
        };

        // 値が変わっていなければ何もしない
        if old_value == new_value { return; }

        // プラグインの on_field_changed を呼び出す（副作用・バリデーション）
        if let Some(lp) = self.plugin_registry.get(&plugin_name) {
            lp.plugin.on_field_changed(key, &old_value, new_value);
        }

        // フィールド値を更新する
        if let Some(pc) = scene.world.get_mut::<PluginComponent>(slot_entity) {
            pc.set_field(key, new_value);
        }

        // Undo の記録はここでは行わない。
        // インスペクタのフィールド編集はすべて field_edit.rs の汎用機構
        //（IPC ディスパッチ入口での前後スナップショット）が 1 箇所で拾う。
        // ここでも記録すると 1 回の編集で履歴が 2 段積まれ、Ctrl+Z が 2 回必要になる。

        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// ロード済みプラグイン一覧をエディタへ送信する。
    fn send_plugin_list(&self) {
        if let Some(ipc) = &self.ipc {
            let json = self.plugin_registry.to_json();
            ipc.send(&format!("PLUGIN_LIST:{json}"));
        }
    }
}
