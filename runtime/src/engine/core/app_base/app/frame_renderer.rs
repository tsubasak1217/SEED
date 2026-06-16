// ============================================================
//  frame_renderer.rs — メインフレームレンダリングループ
//
//  》含む処理「
//  - handle_redraw_requested: RedrawRequested イベントのフレームレンダリング全体
//    1. IPC 処理・ヒエラルキー遅延フラッシュ・クランプ再適用
//    2. 時間計測・GPU カメラ・インスタンスバッファ更新
//    3. GPU バッチ生成（グリッド・ギズモ・スプライト・矩形選択等）
//    4. メインレンダーパス・カメラプレビューブリット
//    5. ID パス（Edit/Pause のみ）・ピック結果デコード・選択更新
//    6. ドロップ処理・FPS 計測・EndFrame
// ============================================================

use std::sync::atomic::{AtomicU64, Ordering};
use winit::event_loop::ActiveEventLoop;

use crate::engine::components::ModelComponent;

/// デバッグログ用フレームカウンター（ログを出力する最大フレーム数）。
static DEBUG_FRAME: AtomicU64 = AtomicU64::new(0);
/// このフレーム数だけ詳細ログを出力する。
const DEBUG_LOG_FRAMES: u64 = 10;

/// パフォーマンスログ用フレームカウンター。
/// 60 フレームごとに各処理の CPU 消費時間と MC/スキン数をログ出力する。
static PERF_FRAME: AtomicU64 = AtomicU64::new(0);
/// パフォーマンスログを出力する間隔（フレーム数）。
const PERF_LOG_INTERVAL: u64 = 60;
use crate::engine::components::{ColliderComponent, ColliderShapeData, ComponentKind};
use crate::engine::components::{Collider2dComponent, ColliderShape2dData, CanvasTransform};
use crate::engine::components::Transform as ActorTransform;
use crate::engine::structs::transforms::Quaternion;
use crate::engine::structs::objects::actor::Actor;
use crate::engine::core::app_base::ipc::ToolMode;
use crate::engine::core::app_base::scene::CanvasCameraData;
use crate::engine::core::clock::FrameContext;
use crate::engine::methods::drawer::{
    CameraBuffer, CameraUniform,
    draw_model_indirect, draw_id_pass, draw_canvas_id_items, prepare_canvas_id_bg,
    draw_outline_multi, draw_stencil_mask_multi,
    extract_frustum_planes, GizmoBatch, draw_gizmo_batch,
    LineBatch, draw_line_batch,
    prepare_sprites_from_mats, draw_sprites, draw_sprite_outline, GpuSpriteTexture,
    NUM_LODS,
};
use crate::engine::methods::gizmo_interact::screen_to_ray;
use crate::engine::core::app_base::undo::{SelectionCommand, ActorDfsSelectionCommand};
use crate::engine::structs::tensor::{Vector3, Mat4x4};
use crate::engine::structs::utils::Color;

use super::{
    App, RuntimeMode,
    collect_mcs_in_world_line,
    find_actor_by_dfs,
    find_parent_actor_of_dfs,
    get_3d_canvas_world_mat,
    world_to_screen,
    apply_window_clamp,
    camera_scene_gizmo,
    CameraPreviewResources,
    CameraGizmoResources,
    CANVAS_WORLD_SCALE,
};

// ============================================================
//  compute_game_viewport — スケーリングモード別ビューポート計算
// ============================================================

/// スケーリングモードに応じたビューポート矩形・射影アスペクト比・実効 FOV_Y を計算する。
///
/// 戻り値: `(vp_x, vp_y, vp_w, vp_h, proj_aspect, fov_y_rad)`
/// - vp_x/y: ウィンドウ左上を原点としたビューポートのオフセット（黒帯の幅）
/// - vp_w/h: ゲーム描画領域のサイズ
/// - proj_aspect: 射影行列に渡すアスペクト比
/// - fov_y_rad: 射影行列に渡す実効縦 FOV（ラジアン）
use super::canvas_collect::{
    collect_sprite_items, collect_canvas_rects, collect_canvas_id_items,
    collect_3d_canvas_child_id_items, sprite_world_corners,
    compute_game_viewport, build_canvas_viewport_map,
};

/// カメラプレビューのテクスチャ幅（ピクセル）。
const CAMERA_PREVIEW_W: u32 = 320;
/// カメラプレビューのテクスチャ高さ（ピクセル）。
const CAMERA_PREVIEW_H: u32 = 180;

impl App {
    /// スクリーン座標のワールドスポーン位置を IDバッファ読み取りで解決する。
    ///
    /// `did_pick` が true の場合はピック処理でバッファ消費済みのため None を返す
    /// （呼び出し元は次フレームに再キューすること）。
    ///
    /// IDバッファからメッシュ面のワールド座標を取得できた場合はその位置を、
    /// 取得できない場合はレイキャスト（カメラ前方 DEFAULT_DIST）にフォールバックする。
    /// D&D（pending_drop）とコンテキストメニュー追加（pending_add_actor）の両方で使用する。
    fn resolve_spawn_pos(&self, sx: u32, sy: u32, did_pick: bool) -> Option<[f32; 3]> {
        /// IDバッファがない・メッシュに当たらない場合のレイ方向への代替距離
        const DEFAULT_DIST: f32 = 10.0;

        // ピック処理がバッファを使用済みのため今フレームでは読み取れない
        if did_pick { return None; }

        // IDバッファからメッシュ面のワールド座標を取得する
        let world_pos = if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
            let (wpos, _raw_id) = id_buf.read_pixel(&draw_ctx.device);
            wpos
        } else {
            None
        };

        // ワールド座標が取れた場合はそのまま使い、なければレイキャストにフォールバック
        Some(world_pos.unwrap_or_else(|| {
            let cam_v = self.camera.position();
            let cam   = [cam_v.x, cam_v.y, cam_v.z];
            if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                let view = self.camera.view_matrix();
                let proj = self.camera.projection_matrix();
                let (_ro, rd) = screen_to_ray(
                    sx as f32, sy as f32,
                    ws.width as f32, ws.height as f32,
                    &view.data, &proj.data, cam,
                );
                [
                    cam[0] + rd[0] * DEFAULT_DIST,
                    cam[1] + rd[1] * DEFAULT_DIST,
                    cam[2] + rd[2] * DEFAULT_DIST,
                ]
            } else {
                // ウィンドウサイズが取れない場合はカメラ前方向に配置する
                let yaw   = self.camera.yaw.to_radians();
                let pitch = self.camera.pitch.to_radians();
                [
                    cam[0] + yaw.sin() * pitch.cos() * DEFAULT_DIST,
                    cam[1] + pitch.sin()              * DEFAULT_DIST,
                    cam[2] + yaw.cos() * pitch.cos() * DEFAULT_DIST,
                ]
            }
        }))
    }

    /// RedrawRequested イベント処理: 1 フレーム分のレンダリング全体を担う。
    ///
    /// render.rs の window_event から委譲される。
    pub(super) fn handle_redraw_requested(&mut self, event_loop: &ActiveEventLoop) {
        let dbg_frame = DEBUG_FRAME.fetch_add(1, Ordering::Relaxed);
        let dbg = dbg_frame < DEBUG_LOG_FRAMES;
        if dbg { eprintln!("[SEED FRAME {dbg_frame}] start  mode={:?}  paused={}", self.mode, self.paused); }

        // ── パフォーマンス計測変数 ─────────────────────────────────────────────
        // 60 フレームごとに各処理の CPU 消費時間を eprintln! でログ出力する。
        // GPU コマンド記録時間（CPU 側）を計測するため、実際の GPU 実行時間は含まない。
        // ただし total_ms と begin_frame_ms は GPU バックプレッシャー（get_current_texture 待機）も含む。
        let perf_idx = PERF_FRAME.fetch_add(1, Ordering::Relaxed);
        let do_perf  = perf_idx % PERF_LOG_INTERVAL == 0;
        // フレーム全体の経過時間 [ms]（begin_frame の GPU 待機 + コマンド記録 + submit を含む）
        let perf_t_total = std::time::Instant::now();
        // begin_frame（get_current_texture）にかかった時間 [ms]（GPU バックプレッシャーの指標）
        let mut perf_begin_frame_ms: f64 = 0.0;
        // process_ipc にかかった時間 [ms]
        let mut perf_ipc_ms:        f64 = 0.0;
        // MCバッチ更新（視錐台カリング + write_buffer記録）にかかった CPU 時間 [ms]
        let mut perf_batch_ms:      f64 = 0.0;
        // スキンコンピュートコマンド記録にかかった CPU 時間 [ms]
        let mut perf_skin_ms:       f64 = 0.0;
        // メインパスの draw_model_indirect コマンド記録にかかった CPU 時間 [ms]
        let mut perf_draw_ms:       f64 = 0.0;
        // ID パスコマンド記録にかかった CPU 時間 [ms]
        let mut perf_id_ms:         f64 = 0.0;
        // グリッド GPU バッチ生成（CPU 線生成 + device.create_buffer_init）にかかった時間 [ms]
        let mut perf_grid_ms:       f64 = 0.0;
        // コライダーワイヤーフレームバッチ生成にかかった時間 [ms]
        let mut perf_collider_ms:   f64 = 0.0;
        // メインレンダーパス全体（begin〜pass drop）にかかった時間 [ms]
        let mut perf_main_pass_ms:  f64 = 0.0;
        // pass.drop() だけにかかった時間 [ms]（wgpu デバッグ検証オーバーヘッドの指標）
        let mut perf_pass_drop_ms:  f64 = 0.0;
        // frame.finish()（encoder.finish + queue.submit + surface.present）にかかった時間 [ms]
        let mut perf_finish_ms:     f64 = 0.0;
        // このフレームの ModelComponent 総数
        let mut perf_mc_count:      usize = 0;
        // うちスキン（アニメーション）付き MC 数
        let mut perf_skin_mc_count: usize = 0;
        // 実際に dispatch したスキン LOD 数（visible_count > 0 のもの）
        let mut perf_skin_dispatches: u32 = 0;

        let _perf_t_ipc = std::time::Instant::now();
        self.process_ipc(event_loop);
        perf_ipc_ms = _perf_t_ipc.elapsed().as_secs_f64() * 1000.0;
        if dbg { eprintln!("[SEED FRAME {dbg_frame}] process_ipc done"); }

        // AI 実行中はレンダリングをスキップして GPU リソースを LLM に解放する。
        // IPC は process_ipc で処理済みなので RESUME_RENDER を受け取れる。
        // request_redraw() でポーリングを継続し、RESUME_RENDER 受信後に即復帰できるようにする。
        if self.render_paused {
            if let Some(w) = &self.window { w.request_redraw(); }
            return;
        }

        // ヒエラルキー遅延フラッシュ（スロットリングで保留されていた送信）
        if self.hierarchy_dirty {
            let now = std::time::Instant::now();
            let ready = self.last_hierarchy_send
                .map(|t| now.duration_since(t).as_millis() >= 100)
                .unwrap_or(true);
            if ready {
                self.hierarchy_dirty = false;
                self.last_hierarchy_send = Some(now);
                self.do_send_hierarchy();
            }
        }

        // Play クランプが有効な間は毎フレーム ClipCursor を再適用する。
        if self.play_clamp {
            apply_window_clamp(self.window_hwnd());
        }

        // ── 物理同期（Play フレームまたは編集時物理シミュレーション再生中）────────────
        // ゲームロジック・スクリプト更新より前に物理結果を取り込む
        if self.mode == RuntimeMode::Edit && self.edit_physics_in_playback {
            // プレイバックモード: 記録済みスナップショットをコマ送りで再生する。
            // 物理エンジンは動かさない。時間表示もスナップショットの time_secs に従う。
            self.step_physics_playback();
        } else {
            let is_edit_physics_stepping = self.should_step_edit_physics();
            let should_update_physics = (self.mode == RuntimeMode::Play && !self.paused)
                || (self.mode == RuntimeMode::Edit && self.edit_physics_enabled && is_edit_physics_stepping);
            if should_update_physics {
                if dbg { eprintln!("[SEED FRAME {dbg_frame}] update_physics start"); }
                self.update_physics();
                if dbg { eprintln!("[SEED FRAME {dbg_frame}] update_physics done"); }
                // 編集時のみスナップショットを記録する（変化なしなら自動停止）
                if self.mode == RuntimeMode::Edit && self.edit_physics_enabled {
                    let dt = 1.0 / 60.0f64; // 固定タイムステップ（物理スレッドと同期）
                    self.try_record_physics_snapshot(dt);
                }
            }

            // ── 2D 物理同期（Play フレームまたは編集時 2D 物理シミュレーション有効時）─────
            // 2D 物理はタイムラインと連動する（3D タイムラインと同期）
            let is_edit_physics_stepping = self.should_step_edit_physics();
            let should_update_physics_2d = (self.mode == RuntimeMode::Play && !self.paused)
                || (self.mode == RuntimeMode::Edit && self.edit_physics_2d_enabled && is_edit_physics_stepping);
            if should_update_physics_2d {
                self.update_physics_2d();
            }
        }

        // ── 時間 ──────────────────────────────────────
        let time_running = self.mode == RuntimeMode::Play && !self.paused;
        let ctx: FrameContext = self.clock.tick(time_running);
        let in_editor = self.mode == RuntimeMode::Edit || self.paused;
        // 現在の世界線が 2D キャンバスモードかどうか
        let is_canvas = self.canvas_world_lines.contains(&self.active_world_line);
        // スクリーンスペースモード:
        //   - チェックボックス ON: スクリーンスペース
        //   - プレイ中: 常にスクリーンスペース
        //   - アクター編集タブの 2D 世界線: 常にスクリーンスペース（編集パネルは従来通り）
        let use_screen_space = self.canvas_screen_space_overlay || !in_editor
            || self.actor_edit_canvas_wls.contains(&self.active_world_line);

        // アクター編集タブの 2D キャンバスのみ 2D オルソカメラを使用する。
        // シーン上のキャンバスは screenSpace チェック ON でも 3D カメラを維持する。
        let is_actor_edit_2d = self.actor_edit_canvas_wls.contains(&self.active_world_line);
        // シーンのスクリーンスペースキャンバス: 3D メインカメラ + 2D オーバーレイ合成。
        // アクター編集タブは camera_buf 自体が 2D なのでオーバーレイ不要。
        let scene_canvas_ss = is_canvas && use_screen_space && !is_actor_edit_2d;

        if in_editor {
            if is_actor_edit_2d {
                // 2D アクター編集タブ: RMB ドラッグで XY パン、スクロールでズーム。
                if self.cam_input.rmb {
                    let ws = self.window.as_ref().map(|w| {
                        let s = w.inner_size();
                        [s.width as f32, s.height as f32]
                    }).unwrap_or([1280.0, 720.0]);
                    let cam_2d = self.canvas_cameras
                        .entry(self.active_world_line)
                        .or_insert_with(CanvasCameraData::default);
                    // ビューポート高さあたりのワールドユニット（スケール係数）
                    let scale = (2.0 * cam_2d.ortho_half_h) / ws[1];
                    // raw delta: right/up が正。X は符号反転、Y は Y-down 座標系のため同符号
                    cam_2d.pan_x -= self.cam_input.mouse_dx * scale;
                    cam_2d.pan_y -= self.cam_input.mouse_dy * scale;
                }
                if self.cam_input.scroll != 0.0 {
                    let cam_2d = self.canvas_cameras
                        .entry(self.active_world_line)
                        .or_insert_with(CanvasCameraData::default);
                    // scroll 正 = ホイール上 = ズームイン（half_h を小さくする）
                    cam_2d.ortho_half_h = (cam_2d.ortho_half_h
                        * 0.9_f32.powf(self.cam_input.scroll))
                        .clamp(0.5, 1000.0);
                }
            } else {
                // 3D モード（ワールドスペースキャンバス含む）: 通常のデバッグカメラ更新
                // MMB パン（絶対差分方式）のために現在カーソル位置を毎フレーム同期する
                if let Some((cx, cy)) = self.last_cursor_pos {
                    self.cam_input.cursor_x = cx;
                    self.cam_input.cursor_y = cy;
                }
                // スナップアニメーション中は回転を補間する（RMB/移動キーでキャンセル）
                self.update_camera_snap_anim(ctx.delta_time);
                self.camera.update(&self.cam_input, ctx.delta_time);
            }
        }

        // ─ 1-6. ゲームロジック（Play 時のみ）─────────
        if time_running {
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] begin_frame"); }
            if let Some(scene) = &mut self.scene { scene.begin_frame(&ctx); }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] early_update"); }
            if let Some(scene) = &mut self.scene { scene.early_update(&ctx); }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] update"); }
            if let Some(scene) = &mut self.scene { scene.update(&ctx); }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] constant_update"); }
            for fixed_ctx in self.clock.drain_fixed() {
                if let Some(scene) = &mut self.scene { scene.constant_update(&fixed_ctx); }
            }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] late_update"); }
            if let Some(scene) = &mut self.scene { scene.late_update(&ctx); }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] scene.render"); }
            if let Some(scene) = &mut self.scene { scene.render(&ctx); }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] game logic done"); }
        }

        // ── GPU カメラ・インスタンスバッファ更新 ──────
        let window_size = self.window.as_ref().map(|w| w.inner_size());
        // Outdated ハンドラ用: &mut self.renderer を借用するブロック内では
        // self メソッドが呼べないため、HWND を事前にコピーしておく。
        // Outdated 発生時（SetParent 後）に GetParent(my_hwnd) で
        // 正確なコンテナサイズを取得する。
        let my_hwnd = self.window_hwnd();
        let queue = self.draw_ctx.as_ref().map(|c| c.queue.clone());

        // Play モードのスケーリングモードに応じたビューポート矩形とクリアカラー。
        // カメラ選択ブロック内で上書きされ、メインレンダーパスで適用する。
        let win_w_f = window_size.map_or(1280.0_f32, |s| s.width  as f32);
        let win_h_f = window_size.map_or(720.0_f32,  |s| s.height as f32);

        // Edit モードで選択中のカメラの視錐台プレーンを事前に計算する。
        //
        // update_all_mc_batches_for_wl では scene への可変借用が必要なため、
        // ここで不変借用のうちにフラスタムを取得しておく。
        // デバッグカメラ OR 選択カメラのいずれかに入っていれば描画する（OR カリング）。
        let preview_frustum: Option<[[f32; 4]; 6]> = {
            // 3D Edit シーン（アクター編集 2D タブ以外）のみ対象
            // WL 0 に 2D アクターが混在していても 3D カメラ視錐台を計算する
            let is_3d_edit = in_editor && !is_actor_edit_2d;
            if is_3d_edit {
                self.scene.as_ref().and_then(|scene| {
                    camera_scene_gizmo::get_selected_camera_data(
                        &scene.actors, &scene.world,
                        self.active_world_line,
                        self.actor_virtual_selected_idx,
                    )
                }).map(|cam_data| {
                    // アスペクト比はエディタビューポートではなく cam_data.target_aspect() から導出する
                    camera_scene_gizmo::compute_frustum_planes(&cam_data)
                })
            } else { None }
        };
        let mut game_viewport:    (f32, f32, f32, f32) = (0.0, 0.0, win_w_f, win_h_f);
        let mut game_clear_color: [f32; 4]             = [0.1, 0.1, 0.1, 1.0];
        // LetterBox / PillarBox 時の帯カラー。デフォルト黒。
        let mut game_bar_color:   [f32; 4]             = [0.0, 0.0, 0.0, 1.0];
        // LetterBox / PillarBox 選択フラグ（帯カラーを clear に使用する）
        let mut uses_bar_mode:    bool                 = false;

        // 統合バッチ更新で使うためここで宣言しておく（begin_frame ブロック外でも参照できるよう）
        let mut saved_frustum_planes: [[f32; 4]; 6] = [[0.0; 4]; 6];
        let mut saved_camera_pos:     [f32; 3]       = [0.0; 3];

        if let (Some(scene), Some(camera_buf), Some(queue)) =
            (&mut self.scene, &self.camera_buf, queue)
        {
            // カメラ選択:
            //   - 2D アクター編集タブ → 2D オルソカメラ
            //   - Play モード         → シーン内 is_main=true の CameraComponent
            //                           （見つからなければデバッグカメラにフォールバック）
            //   - Edit モード         → デバッグカメラ
            let (view, proj, cam_pos_arr) = if is_actor_edit_2d {
                let cam_2d = self.canvas_cameras
                    .entry(self.active_world_line)
                    .or_insert_with(CanvasCameraData::default);
                let aspect = window_size.map_or(16.0 / 9.0, |s| {
                    s.width as f32 / s.height as f32
                });
                let half_h = cam_2d.ortho_half_h;
                let half_w = half_h * aspect;
                // カメラは Z=-100 から XY 平面（Z=0）を見下ろす（LH forward = +Z）
                let eye    = Vector3::new(cam_2d.pan_x, cam_2d.pan_y, -100.0);
                let center = Vector3::new(cam_2d.pan_x, cam_2d.pan_y, 0.0);
                let up     = Vector3::new(0.0, 1.0, 0.0);
                let v = Mat4x4::look_at_lh(eye, center, up);
                // Y-down: bottom=+half_h, top=-half_h でワールド Y 正方向 = スクリーン下
                let p = Mat4x4::orthographic_lh(-half_w, half_w, half_h, -half_h, 0.0, 200.0);
                (v, p, [cam_2d.pan_x, cam_2d.pan_y, -100.0])
            } else if self.mode == RuntimeMode::Play && !self.paused {
                // Play モード（非ポーズ時）: is_main=true の CameraComponent を探す
                // スケーリングモードに応じたビューポート矩形・射影アスペクト比・実効 FOV を計算する
                let game_cam = scene.find_main_camera().map(|(tf, cd)| {
                    let (vp_x, vp_y, vp_w, vp_h, proj_aspect, fov_y_rad) = compute_game_viewport(
                        &cd.scaling_mode, win_w_f, win_h_f,
                        cd.target_width, cd.target_height, cd.fov_y_deg,
                    );
                    game_viewport    = (vp_x, vp_y, vp_w.max(1.0), vp_h.max(1.0));
                    game_clear_color = cd.clear_color;
                    game_bar_color   = cd.bar_color;
                    // LetterBox / PillarBox の場合は帯カラーを LoadOp::Clear に使用する。
                    // ゲームエリア内は scene オブジェクトがクリアカラーを上書きする想定。
                    uses_bar_mode = matches!(
                        cd.scaling_mode,
                        crate::engine::components::ScalingMode::LetterBox
                        | crate::engine::components::ScalingMode::PillarBox
                        | crate::engine::components::ScalingMode::LetterPillarBox
                    );
                    let [px, py, pz] = tf.position;
                    let [fx, fy, fz] = tf.forward();
                    let [ux, uy, uz] = tf.up();
                    let pos    = Vector3::new(px, py, pz);
                    let target = pos + Vector3::new(fx, fy, fz);
                    let up_vec = Vector3::new(ux, uy, uz);
                    let v = Mat4x4::look_at_lh(pos, target, up_vec);
                    let p = Mat4x4::perspective_lh(fov_y_rad, proj_aspect, cd.near, cd.far);
                    (v, p, [px, py, pz])
                });
                // メインカメラが未配置の場合はデバッグカメラにフォールバック
                game_cam.unwrap_or_else(|| {
                    let v  = self.camera.view_matrix();
                    let p  = self.camera.projection_matrix();
                    let cp = self.camera.position();
                    (v, p, [cp.x, cp.y, cp.z])
                })
            } else {
                // Edit モード: デバッグカメラ
                let v  = self.camera.view_matrix();
                let p  = self.camera.projection_matrix();
                let cp = self.camera.position();
                (v, p, [cp.x, cp.y, cp.z])
            };

            let view_proj = proj * view;

            let res = window_size.map_or([1280.0, 720.0], |s| {
                [s.width as f32, s.height as f32]
            });
            camera_buf.update(&queue, &CameraUniform {
                view_proj:  view_proj.transpose().data,
                view:       view.transpose().data,
                position:   cam_pos_arr,
                _pad:       0.0,
                resolution: res,
                _pad2:      [0.0; 2],
            });

            // シーンスクリーンスペース専用: 2D オルソオーバーレイカメラを更新する。
            // 3D メインカメラの上に 2D キャンバス要素を重ねて描画するために使う。
            // アクター編集タブは camera_buf 自体が 2D なのでここでは更新しない。
            if scene_canvas_ss {
                if let Some(canvas_cam_buf) = &self.canvas_overlay_camera_buf {
                    let (vp_w, vp_h) = window_size.map_or(
                        (1280.0f32, 720.0f32),
                        |s| (s.width as f32, s.height as f32),
                    );
                    let half_h = vp_h / 2.0;
                    let half_w = vp_w / 2.0;
                    let eye_c    = Vector3::new(0.0, 0.0, -100.0);
                    let center_c = Vector3::new(0.0, 0.0, 0.0);
                    let up_c     = Vector3::new(0.0, 1.0, 0.0);
                    let cv  = Mat4x4::look_at_lh(eye_c, center_c, up_c);
                    let cp2 = Mat4x4::orthographic_lh(-half_w, half_w, half_h, -half_h, 0.0, 200.0);
                    let cvp = cp2 * cv;
                    canvas_cam_buf.update(&queue, &CameraUniform {
                        view_proj:  cvp.transpose().data,
                        view:       cv.transpose().data,
                        position:   [0.0, 0.0, -100.0],
                        _pad:       0.0,
                        resolution: [vp_w, vp_h],
                        _pad2:      [0.0; 2],
                    });
                }
            }

            // MMB スティック HUD カメラを常に更新する（MMB 状態に関わらず毎フレーム更新）
            if let Some(mmb_cam) = &self.mmb_hud_cam_buf {
                let (vp_w, vp_h) = window_size.map_or(
                    (1280.0f32, 720.0f32),
                    |s| (s.width as f32, s.height as f32),
                );
                let half_w = vp_w / 2.0;
                let half_h = vp_h / 2.0;
                let eye_m    = Vector3::new(0.0, 0.0, -100.0);
                let center_m = Vector3::new(0.0, 0.0, 0.0);
                let up_m     = Vector3::new(0.0, 1.0, 0.0);
                let mv  = Mat4x4::look_at_lh(eye_m, center_m, up_m);
                let mp  = Mat4x4::orthographic_lh(-half_w, half_w, half_h, -half_h, 0.0, 200.0);
                let mvp = mp * mv;
                mmb_cam.update(&queue, &CameraUniform {
                    view_proj:  mvp.transpose().data,
                    view:       mv.transpose().data,
                    position:   [0.0, 0.0, -100.0],
                    _pad:       0.0,
                    resolution: [vp_w, vp_h],
                    _pad2:      [0.0; 2],
                });
            }

            let frustum_planes = extract_frustum_planes(&view_proj.data);
            let camera_pos     = cam_pos_arr;
            // 統合バッチ更新のためにブロック外へ保存する
            saved_frustum_planes = frustum_planes;
            saved_camera_pos     = camera_pos;

            // シーンモード・アクター編集モード共通: world_line 全 MC を DFS で更新する
            // preview_frustum が Some の場合: デバッグカメラ OR プレビューカメラの OR カリング。
            let (actors, world) = (&mut scene.actors, &mut scene.world);
            let _perf_t_batch = std::time::Instant::now();
            super::update_all_mc_batches_for_wl(
                actors, world, self.active_world_line,
                &queue, &frustum_planes, preview_frustum.as_ref(), camera_pos, self.clock.anim_time(),
            );
            perf_batch_ms = _perf_t_batch.elapsed().as_secs_f64() * 1000.0;
        }

        // ── ギズモ位置：全選択アクターの重心（マルチ選択対応） ──
        let gizmo_pos = self.selected_actors_centroid()
            .or_else(|| self.actor_virtual_world_pos());

        // アクター仮想選択のワールド位置（レンダラー借用外で取得）
        let actor_virtual_pos: Option<[f32; 3]> = if self.actor_virtual_selected_idx.is_some() {
            self.actor_virtual_world_pos()
        } else { None };

        // ── ドラッグホバープレビュー位置の更新（レンダー前）──────────
        // アクター編集 2D タブの場合、アクターの配置位置は CanvasTransform で固定のため
        // 3D プレビュー球体は表示しない。3D モードのみレイキャストで位置を算出する。
        // 3D+2D 混在シーン（is_canvas=true）では 3D レイキャストを引き続き使用する。
        if let Some((hsx, hsy)) = self.pending_drop_hover.take() {
            if !is_actor_edit_2d {
                // 3D モード: GPU リードバックを使わずレイキャストでワールド座標を算出する。
                // y=0 平面との交差を優先し、カメラが平行または後方なら DEFAULT_DIST 先。
                const DEFAULT_DIST: f32 = 10.0;
                if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                    let cam_v = self.camera.position();
                    let cam   = [cam_v.x, cam_v.y, cam_v.z];
                    let view  = self.camera.view_matrix();
                    let proj  = self.camera.projection_matrix();
                    let (_ro, rd) = screen_to_ray(
                        hsx as f32, hsy as f32,
                        ws.width as f32, ws.height as f32,
                        &view.data, &proj.data, cam,
                    );
                    // y=0 平面との交差を試みる（地面への自然な配置）
                    let pos = if rd[1].abs() > 0.001 {
                        let t = -cam[1] / rd[1];
                        if t > 0.5 {
                            [cam[0]+rd[0]*t, 0.0, cam[2]+rd[2]*t]
                        } else {
                            [cam[0]+rd[0]*DEFAULT_DIST, cam[1]+rd[1]*DEFAULT_DIST, cam[2]+rd[2]*DEFAULT_DIST]
                        }
                    } else {
                        [cam[0]+rd[0]*DEFAULT_DIST, cam[1]+rd[1]*DEFAULT_DIST, cam[2]+rd[2]*DEFAULT_DIST]
                    };
                    self.drop_preview_pos = Some(pos);
                }
            }
            // 2D キャンバスモード: pending_drop_hover は消費されるが drop_preview_pos は更新しない
        }

        // ピック要求を取り出す（描画ブロック内で使用）
        let pick_pos = self.pending_pick.take();
        let mut did_pick = false;

        // ピック結果デコード用 MC 情報 (base, dfs_id, slot_i, instance_count)
        let wl_mc_pick_infos: Vec<(u32, u32, usize, usize)> = {
            if let Some(scene) = &self.scene {
                collect_mcs_in_world_line(&scene.actors, &scene.world, self.active_world_line)
                    .into_iter()
                    .map(|(base, dfs, slot_i, mc)| (base, dfs, slot_i, mc.instance_mats.len()))
                    .collect()
            } else { vec![] }
        };
        // MC インスタンスの総数（キャンバス ID オフセット計算用）
        // 全 MC の (base + count) の最大値 = 割り当て済み ID の上限
        let mc_total_instances: u32 = wl_mc_pick_infos.iter()
            .map(|&(base, _, _, count)| base + count as u32)
            .max()
            .unwrap_or(0);

        // カメラギズモの (DFS ID, アイコン行列) リスト（3D 編集モードのみ）。
        // ピック情報として mc_total_instances の直後の ID 範囲に割り当てる。
        let cam_gizmo_actor_mats: Vec<(usize, [[f32; 4]; 4])> = if in_editor && !is_actor_edit_2d {
            if let Some(scene) = &self.scene {
                camera_scene_gizmo::collect_camera_actor_matrices(
                    &scene.actors, &scene.world, self.active_world_line,
                )
            } else { vec![] }
        } else { vec![] };
        let camera_gizmo_count: u32 = cam_gizmo_actor_mats.len() as u32;
        // キャンバス ID のベースオフセット（MC + カメラギズモの後）
        let canvas_id_offset: u32 = mc_total_instances + camera_gizmo_count;

        // 選択アクターの種別（2D/3D）を可変借用の前に確定する。
        // self.renderer を可変借用した後は self の不変借用が取れないため。
        // ワールドスペース表示中（use_screen_space = false）の 2D アクターはパースペクティブ
        // カメラで描画されるため、ortho 半径ではなく 3D 半径を使う必要がある。
        let gizmo_actor_is_2d = is_actor_edit_2d
            || (self.selected_primary_actor_is_2d() && use_screen_space);
        // 3D Canvas 子アクター軸をレンダーパス開始前（可変借用前）に事前計算する。
        // レンダーパス内では &mut self.renderer の可変借用が続くため self の不変借用が取れない。
        let canvas_child_axes_pre = self.selected_canvas_child_axes();

        if let (Some(renderer), Some(scene), Some(camera_buf), Some(draw_ctx)) =
            (&mut self.renderer, &self.scene, &self.camera_buf, &self.draw_ctx)
        {
            // begin_frame = get_current_texture(): GPU バックプレッシャーでここが長くなる
            let _perf_t_bf = std::time::Instant::now();
            let begin_frame_result = renderer.begin_frame();
            perf_begin_frame_ms = _perf_t_bf.elapsed().as_secs_f64() * 1000.0;
            match begin_frame_result {
                Ok(mut frame) => {
                    // シーンモード・アクター編集モード共通: world_line の全 MC を収集する
                    // タプル: (id_base, dfs_id, slot_i, &ModelComponent)
                    let all_mcs: Vec<(u32, u32, usize, &ModelComponent)> =
                        collect_mcs_in_world_line(&scene.actors, &scene.world, self.active_world_line);
                    // 後方互換: 単一 MC として使う箇所用（シーン編集モード or 先頭 MC）
                    let _mc = all_mcs.first().map(|&(_, _, _, mc)| mc);
                    // 選択中アクターの MC（アウトライン・アイコン用）
                    let selected_slot_i = self.actor_virtual_selected_slot_idx;
                    let selected_mc: Option<&ModelComponent> =
                        self.actor_virtual_selected_idx
                            .and_then(|dfs| all_mcs.iter()
                                .find(|&&(_, d, si, _)| d == dfs as u32 && si == selected_slot_i)
                                .map(|&(_, _, _, mc)| mc));

                    // ─── 統合モデルバッチ更新 ─────────────────────────────────────────────
                    // 同一 source_path を持つ全 MC の行列・アニメーションを 1 バッチに統合する。
                    // draw call 数: N_actors × P_prims → N_unique_models × P_prims
                    // RenderPass::drop() のコマンド処理コストが N 倍の差として現れるため、
                    // 50 アクター × 1 モデルの場合は理論上 ~50 倍の高速化が見込まれる。
                    //
                    // ① MC を source_path でグループ化（CPU データのみ収集）
                    struct MergeInfo {
                        cpu_model: std::sync::Arc<crate::engine::core::loader::model::Model>,
                        mats:      Vec<[[f32; 4]; 4]>,
                        seeds:     Vec<u32>,
                        /// 統合インスタンス i の絶対 ID（元 MC の id_base + 元インスタンス idx）
                        abs_ids:   Vec<u32>,
                    }
                    // (dfs_id, slot_i) → (source_path, merged_start, n_instances)
                    // アウトライン描画時に統合バッチ内のインスタンス範囲を特定するために使う。
                    // merged_start: このMCの先頭インスタンスの統合バッチ内インデックス
                    // n_instances:  このMCのインスタンス数
                    let mut mc_outline_map: std::collections::HashMap<
                        (u32, usize),
                        (String, u32, u32),
                    > = std::collections::HashMap::new();
                    let merge_map: std::collections::HashMap<String, MergeInfo> = {
                        let mut map: std::collections::HashMap<String, MergeInfo>
                            = std::collections::HashMap::new();
                        for &(id_base, dfs_id, slot_i, amc) in &all_mcs {
                            if amc.source_path.is_empty()  { continue; }
                            if amc.gpu_model.is_none()     { continue; }
                            let Some(arc_m) = amc.model.as_ref() else { continue };
                            let e = map.entry(amc.source_path.clone())
                                .or_insert_with(|| MergeInfo {
                                    cpu_model: arc_m.clone(),
                                    mats:      Vec::new(),
                                    seeds:     Vec::new(),
                                    abs_ids:   Vec::new(),
                                });
                            // このMCが統合バッチに追加される前の先頭インデックスを記録する
                            let merged_start = e.mats.len() as u32;
                            let n_insts      = amc.instance_mats.len() as u32;
                            mc_outline_map.insert(
                                (dfs_id, slot_i),
                                (amc.source_path.clone(), merged_start, n_insts),
                            );
                            for (inst_i, &mat) in amc.instance_mats.iter().enumerate() {
                                e.mats.push(mat);
                                e.seeds.push(
                                    amc.instance_meta.get(inst_i)
                                       .map(|m| m.anim_seed)
                                       .unwrap_or(0)
                                );
                                // abs_id = MC の id_base + このインスタンスのオフセット
                                e.abs_ids.push(id_base + inst_i as u32);
                            }
                        }
                        map
                    };

                    // ② 統合バッチ生成/更新（容量不足時は再生成）
                    for (path, info) in &merge_map {
                        let total = info.mats.len();
                        let need_reinit = self.shared_model_batches.get(path)
                            .map(|s| s.capacity < total)
                            .unwrap_or(true);
                        if need_reinit {
                            let cap        = (total * 2).max(4);
                            let new_batch  = draw_ctx.create_instanced_batch(
                                &info.cpu_model, cap as u32
                            );
                            let id_zero_bg = draw_ctx.create_id_base_bg(0);
                            self.shared_model_batches.insert(path.clone(), super::SharedModelData {
                                cpu_model:  info.cpu_model.clone(),
                                batch:      new_batch,
                                capacity:   cap,
                                id_zero_bg,
                            });
                        }
                        if let Some(sd) = self.shared_model_batches.get_mut(path) {
                            // フィールド分割借用: batch は可変、cpu_model は不変
                            let batch     = &mut sd.batch;
                            let cpu_model = &sd.cpu_model;
                            batch.set_anim_seeds(&info.seeds);
                            batch.mark_dirty();
                            batch.update(
                                &draw_ctx.queue,
                                cpu_model,
                                &info.mats,
                                &saved_frustum_planes,
                                preview_frustum.as_ref(),
                                saved_camera_pos,
                                self.clock.anim_time(),
                            );
                            // lod_id_buffers を絶対 ID で上書きする
                            // update() が書いた「統合バッチ内 compact インデックス」を
                            // 元 MC の id_base + 元インスタンスインデックスに差し替え、
                            // CPU ピッキングのデコードロジックをそのまま使えるようにする。
                            for lod in 0..NUM_LODS {
                                if batch.lod_visible_counts[lod] > 0 {
                                    let remapped: Vec<u32> = batch.lod_compact_insts[lod]
                                        .iter()
                                        .map(|&merged_idx| info.abs_ids[merged_idx])
                                        .collect();
                                    draw_ctx.queue.write_buffer(
                                        &batch.lod_id_buffers[lod],
                                        0,
                                        bytemuck::cast_slice(&remapped),
                                    );
                                }
                            }
                        }
                    }

                    // ③ draw 時に参照する source_path → &GpuModel マッピング
                    // all_mcs の最初の該当 MC の GpuModel を借用する（全 MC が同一 GPU データを持つ）
                    let gpu_model_by_path: std::collections::HashMap<
                        &str,
                        &crate::engine::methods::drawer::GpuModel,
                    > = all_mcs.iter()
                        .filter_map(|&(_, _, _, amc)| {
                            if amc.source_path.is_empty() { return None; }
                            amc.gpu_model.as_ref()
                                .map(|gpu| (amc.source_path.as_str(), gpu))
                        })
                        .collect();
                    // ─── 統合モデルバッチ更新 終了 ──────────────────────────────────────────

                    // スキンメッシュコンピュート: 全 MC に対して実行
                    // ─ 全アクターで 1 つの ComputePass を共有する ─
                    // 以前は MC ごとに begin_compute_pass → end を繰り返していたため、
                    // 25 アクターで 25 × begin/end のオーバーヘッドが発生していた（~15ms/frame）。
                    // 1 パスにまとめることでコマンド記録コストを大幅に削減する。
                    // ─ パフォーマンス計測 ─
                    perf_mc_count = all_mcs.len();
                    let _perf_t_skin = std::time::Instant::now();
                    {
                        let mut skin_pass = frame.encoder_mut().begin_compute_pass(
                            &wgpu::ComputePassDescriptor {
                                label:            Some("Skin Compute Pass"),
                                timestamp_writes: None,
                            },
                        );
                        // 統合バッチのみをディスパッチする（per-MC バッチは使用しない）
                        // N_actors 回 → N_unique_models 回に削減
                        for sd in self.shared_model_batches.values() {
                            if sd.batch.skin.is_some() {
                                perf_skin_mc_count += 1;
                                perf_skin_dispatches += sd.batch.lod_visible_counts
                                    .iter().filter(|&&c| c > 0).count() as u32;
                            }
                            sd.batch.dispatch_skin(
                                &mut skin_pass,
                                &draw_ctx.pipelines.skin_compute,
                            );
                        }
                    } // skin_pass がここでドロップされ ComputePass が終了する
                    perf_skin_ms = _perf_t_skin.elapsed().as_secs_f64() * 1000.0;

                    // ── カメラシーンギズモ（Edit モード・3D シーンのみ）──────────
                    // カメラアイコン / フラスタム / プレビューはアクター編集 2D タブ以外で表示する。
                    // WL 0 に 2D アクターが混在していても 3D カメラギズモを表示する。
                    let is_3d_scene = in_editor && !is_actor_edit_2d;
                    let (vp_w_f, vp_h_f) = window_size.map_or(
                        (1280.0_f32, 720.0_f32),
                        |s| (s.width as f32, s.height as f32),
                    );

                    // カメラギズモモデルのレイジーロードと InstancedModelBatch 更新
                    // assets_root が設定されている場合のみ実行する
                    if is_3d_scene && !cam_gizmo_actor_mats.is_empty() {
                        if let Some(ar) = self.editor_resources.clone() {
                            let model_path = format!("{}/models/camera.glb", ar);
                            // CPU モデルをキャッシュから取得するか、なければロードする
                            let cpu_model_opt: Option<std::sync::Arc<crate::engine::core::loader::model::Model>> = {
                                let mut cache = draw_ctx.model_cache.borrow_mut();
                                if !cache.contains_key(&model_path) {
                                    let path = std::path::Path::new(&model_path);
                                    match crate::engine::core::loader::load_model(path) {
                                        Ok(m)  => { cache.insert(model_path.clone(), std::sync::Arc::new(m)); }
                                        Err(e) => { eprintln!("[SEED] camera gizmo model load failed: {e}"); }
                                    }
                                }
                                cache.get(&model_path).cloned()
                            };
                            if let Some(cpu) = cpu_model_opt {
                                // バッチ容量が不足している場合は再生成する
                                let need_reinit = self.camera_gizmo.as_ref()
                                    .map(|g| g.capacity < cam_gizmo_actor_mats.len())
                                    .unwrap_or(true);
                                if need_reinit {
                                    let cap = (cam_gizmo_actor_mats.len() * 2).max(4);
                                    let gpu_model = draw_ctx.upload_model(&cpu);
                                    let batch     = draw_ctx.create_instanced_batch(&cpu, cap as u32);
                                    self.camera_gizmo = Some(CameraGizmoResources {
                                        cpu_model: cpu, gpu_model, batch, capacity: cap,
                                    });
                                }
                            }
                        }
                    }
                    // カメラギズモバッチを毎フレーム更新する（インスタンス変換・視錐台カリング）
                    if is_3d_scene && !cam_gizmo_actor_mats.is_empty() {
                        let cp  = self.camera.position();
                        let v   = self.camera.view_matrix();
                        let p   = self.camera.projection_matrix();
                        let fp  = extract_frustum_planes(&(p * v).data);
                        let cpo = [cp.x, cp.y, cp.z];
                        let transforms: Vec<[[f32; 4]; 4]> =
                            cam_gizmo_actor_mats.iter().map(|&(_, m)| m).collect();
                        if let Some(gizmo) = &mut self.camera_gizmo {
                            gizmo.batch.mark_dirty();
                            // カメラアイコンは常に表示するため extra_frustum なし
                            gizmo.batch.update(
                                &draw_ctx.queue,
                                &gizmo.cpu_model,
                                &transforms,
                                &fp,
                                None,
                                cpo,
                                0.0,
                            );
                        }
                    }

                    // 選択中アクターのカメラデータ取得
                    let selected_cam_data = if is_3d_scene {
                        camera_scene_gizmo::get_selected_camera_data(
                            &scene.actors, &scene.world,
                            self.active_world_line,
                            self.actor_virtual_selected_idx,
                        )
                    } else { None };

                    // フラスタムラインバッチ（選択カメラアクターのみ）
                    // アスペクト比は cam_data.target_aspect() から導出する（エディタビューポート非依存）
                    let frustum_batch = if let Some(ref cam_data) = selected_cam_data {
                        camera_scene_gizmo::build_camera_frustum_batch(
                            cam_data, &draw_ctx.device,
                        )
                    } else { None };

                    // カメラプレビューリソースを初期化・更新する
                    if selected_cam_data.is_some() {
                        // リソースが未初期化の場合は生成する
                        if self.camera_preview.is_none() {
                            self.camera_preview = Some(CameraPreviewResources::new(
                                &draw_ctx.device,
                                &draw_ctx.pipelines.camera_preview_blit,
                                CAMERA_PREVIEW_W, CAMERA_PREVIEW_H,
                            ));
                        }
                        // ブリット矩形を更新する
                        if let Some(ref preview) = self.camera_preview {
                            preview.update_blit_rect(
                                &draw_ctx.queue, vp_w_f, vp_h_f,
                                CAMERA_PREVIEW_W as f32, CAMERA_PREVIEW_H as f32,
                            );
                        }
                    } else {
                        // 選択解除時はリソースを破棄する（メモリ節約）
                        self.camera_preview = None;
                    }

                    // カメラプレビューレンダーパス（選択カメラのビューで全 MC を描画）
                    if let (Some(cam_data), Some(preview)) =
                        (selected_cam_data.as_ref(), self.camera_preview.as_ref())
                    {
                        let res = [CAMERA_PREVIEW_W as f32, CAMERA_PREVIEW_H as f32];
                        // ゲームカメラの target_width/height からアスペクト比を導出する
                        let preview_aspect = cam_data.target_aspect();
                        let cam_uniform = camera_scene_gizmo::build_camera_uniform(
                            cam_data, preview_aspect, res,
                        );
                        // プレビュー用一時カメラバッファを生成する
                        let preview_cam_buf = CameraBuffer::new(
                            &draw_ctx.device,
                            &draw_ctx.pipelines.unlit_line.camera_bgl,
                        );
                        preview_cam_buf.update(&draw_ctx.queue, &cam_uniform);

                        // メッシュパイプライン用カメラバッファも生成する（PBR mesh 描画用）
                        let preview_mesh_cam_buf = CameraBuffer::new(
                            &draw_ctx.device,
                            &draw_ctx.pipelines.mesh.camera_bgl,
                        );
                        preview_mesh_cam_buf.update(&draw_ctx.queue, &cam_uniform);

                        // オフスクリーンプレビューパスで全 MC を描画する
                        let clear_col = {
                            let [r, g, b, a] = cam_data.clear_color;
                            wgpu::Color { r: r as f64, g: g as f64, b: b as f64, a: a as f64 }
                        };
                        // プレビュー用 3D Canvas スプライトを収集する。
                        // メインパスのスプライト収集（sprite_prepared_3d）より先にこのパスが実行されるため、
                        // ここで独立して収集する。SpritePipeline は mesh と同一カメラ BGL を使用するため
                        // preview_mesh_cam_buf.bind_group をそのまま使用できる。
                        let preview_sprite_3d = {
                            let mut items = Vec::new();
                            if let Some(s) = &self.scene {
                                let wl = self.active_world_line;
                                for actor in &s.actors {
                                    if actor.world_line != wl { continue; }
                                    if actor.is_2d() { continue; }
                                    let cs = actor.slots().iter()
                                        .find(|s| s.kind == crate::engine::components::ComponentKind::Canvas);
                                    let Some(cs) = cs else { continue };
                                    let Some(cc) = s.world.get::<crate::engine::components::CanvasComponent>(cs.entity) else { continue };
                                    let Some(tf) = s.world.get::<crate::engine::components::Transform>(actor.entity) else { continue };
                                    let cws = CANVAS_WORLD_SCALE;
                                    let (piv_x, piv_y) = (cc.pivot[0], cc.pivot[1]);
                                    let ctw = crate::engine::methods::gizmo_interact::mat4x4_mul(
                                        tf.to_mat4(),
                                        [
                                            [ cws,  0.0, 0.0, -piv_x * cc.width  * cws],
                                            [ 0.0, -cws, 0.0,  piv_y * cc.height * cws],
                                            [ 0.0,  0.0, 1.0,  0.0                    ],
                                            [ 0.0,  0.0, 0.0,  1.0                    ],
                                        ],
                                    );
                                    let child_sm = (
                                        cc.scale_transform, cc.scale_size,
                                        cc.keep_aspect_ratio,
                                        matches!(cc.aspect_ratio_axis, crate::engine::components::AspectRatioAxis::Width),
                                    );
                                    collect_sprite_items(
                                        &actor.children, &s.world, wl, draw_ctx,
                                        Some([cc.width, cc.height]),
                                        ctw, [1.0, 1.0], child_sm,
                                        1.0, 1.0,
                                        None, &std::collections::HashMap::new(), &mut items,
                                    );
                                }
                            }
                            prepare_sprites_from_mats(&draw_ctx.device, &draw_ctx.pipelines.sprite, &items)
                        };

                        {
                            let mut preview_pass = frame.begin_offscreen_pass(
                                &preview.color_view,
                                &preview.depth_view,
                                clear_col,
                            );
                            // モデルを統合バッチで描画（per-MC バッチは使用しない）
                            for (path, sd) in &self.shared_model_batches {
                                if let Some(&gpu) = gpu_model_by_path.get(path.as_str()) {
                                    draw_model_indirect(
                                        &mut preview_pass, gpu, &sd.batch,
                                        &preview_mesh_cam_buf.bind_group,
                                        &draw_ctx.pipelines,
                                    );
                                }
                            }
                            // 3D Canvas スプライトをプレビューカメラで描画する
                            // SpritePipeline は mesh と同一カメラ BGL のため preview_mesh_cam_buf を流用する
                            if !preview_sprite_3d.is_empty() {
                                draw_sprites(
                                    &mut preview_pass,
                                    &draw_ctx.pipelines.sprite,
                                    &preview_mesh_cam_buf.bind_group,
                                    &preview_sprite_3d,
                                );
                            }
                        }
                    }

                    // ギズモ GPU バッファ（レンダーパスの前に生成）
                    // 選択アクターの種別（2D/3D）でギズモ形状を決定する。
                    // gizmo_actor_is_2d は可変借用前に確定済み（self.renderer との競合回避）。
                    // 編集時物理タイムラインで過去フレームを表示中はGizmoを非表示にする
                    let physics_timeline_locked = self.mode == RuntimeMode::Edit
                        && self.edit_physics_enabled
                        && !self.edit_physics_at_latest;
                    let show_gizmo_pre = (self.mode == RuntimeMode::Edit || self.paused)
                        && !physics_timeline_locked;
                    let gizmo_gpu_batch = if show_gizmo_pre
                        && self.tool_mode != ToolMode::Select
                    {
                        gizmo_pos.map(|pos| {
                            // 2D アクター編集タブ・2D アクター選択時 / それ以外でギズモ半径を切り替える
                            let (radius, cam_pos_arr) = if gizmo_actor_is_2d {
                                // 2D スクリーンスペース: ビューポート高さの 15% をギズモ半径とする。
                                // ortho_half_h = vp_h/2 なので * 0.15 = vp_h * 0.075 px
                                let cam_2d = self.canvas_cameras.get(&self.active_world_line);
                                let r = cam_2d.map(|c| c.ortho_half_h * 0.15).unwrap_or(54.0);
                                (r, [0.0f32, 0.0, -100.0])
                            } else {
                                // 3D perspective（通常3D または ワールドスペースキャンバス）: 距離と FOV からギズモ半径を計算する
                                let cam_pos = self.camera.position();
                                let d = [pos[0]-cam_pos.x, pos[1]-cam_pos.y, pos[2]-cam_pos.z];
                                let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
                                let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
                                let r = dist * half_fov.tan() * 0.233;
                                (r, [cam_pos.x, cam_pos.y, cam_pos.z])
                            };

                            let hov  = self.hovered_gizmo_part;
                            let drag_part = self.drag.gizmo_drag.as_ref().map(|d| d.part);
                            let mut batch = GizmoBatch::new();
                            // 3D Canvas 子アクター: 事前計算したキャンバス軸を使用する
                            if let Some([ax, ay, az]) = canvas_child_axes_pre {
                                // 3D Canvas 子アクター: キャンバス平面に沿った 2 軸ギズモ
                                match self.tool_mode {
                                    ToolMode::Move   => batch.add_gizmo_translate_canvas(pos, radius, hov, ax, ay),
                                    ToolMode::Rotate => batch.add_gizmo_rotate_canvas(pos, radius, 64, cam_pos_arr, hov, drag_part, az, ax, ay),
                                    ToolMode::Scale  => batch.add_gizmo_scale_canvas(pos, radius, hov, ax, ay),
                                    ToolMode::Select => {}
                                }
                            } else if gizmo_actor_is_2d {
                                // 2D: Z 軸・平面ハンドル不要、Rotate は完全円
                                match self.tool_mode {
                                    ToolMode::Move   => batch.add_gizmo_translate_2d(pos, radius, hov),
                                    ToolMode::Rotate => batch.add_gizmo_rotate_2d(pos, radius, 64, hov),
                                    ToolMode::Scale  => batch.add_gizmo_scale_2d(pos, radius, hov),
                                    ToolMode::Select => {}
                                }
                            } else {
                                // 3D: 全軸・平面ハンドル、Rotate は半円
                                match self.tool_mode {
                                    ToolMode::Move   => batch.add_gizmo_translate(pos, radius, hov),
                                    ToolMode::Rotate => batch.add_gizmo_rotate(pos, radius, 64, cam_pos_arr, hov, drag_part),
                                    ToolMode::Scale  => batch.add_gizmo_scale(pos, radius, hov),
                                    ToolMode::Select => {}
                                }
                            }
                            batch.build(&draw_ctx.device)
                        })
                    } else { None };

                    // 矩形選択ビジュアル（レンダーパスの前に GPU バッファを生成）
                    let rect_gpu_batch = if in_editor && self.drag.rect_selecting {
                        if let (Some((px, py)), Some((cx, cy))) =
                            (self.drag.lmb_press_pos, self.last_cursor_pos)
                        {
                            let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                            let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                            let sc = [
                                (px.min(cx), py.min(cy)), // TL
                                (px.max(cx), py.min(cy)), // TR
                                (px.max(cx), py.max(cy)), // BR
                                (px.min(cx), py.max(cy)), // BL
                            ];
                            let mut wp = [[0.0f32; 3]; 4];
                            if is_actor_edit_2d || scene_canvas_ss {
                                // 2D スクリーンスペース（アクター編集タブ・シーンSS共通）:
                                // 2D ortho でスクリーン座標をキャンバス XY に変換する
                                let cam_2d = self.canvas_cameras.get(&self.active_world_line);
                                let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
                                let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
                                let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(360.0);
                                let half_w = half_h * (vp_w / vp_h);
                                for (i, &(sx, sy)) in sc.iter().enumerate() {
                                    let nx = 2.0 * sx / vp_w - 1.0;
                                    let ny = 2.0 * sy / vp_h - 1.0; // Y-down
                                    wp[i] = [pan_x + nx * half_w, pan_y + ny * half_h, 0.0];
                                }
                            } else {
                                // 3D perspective: near plane の手前にワールド座標を投影する
                                let view    = self.camera.view_matrix();
                                let proj    = self.camera.projection_matrix();
                                let cam_pv  = self.camera.position();
                                let cam_pos = [cam_pv.x, cam_pv.y, cam_pv.z];
                                let near_vs = self.camera.base.projection.near * 1.05;
                                let p = &proj.data;
                                let v = &view.data;
                                for (i, &(sx, sy)) in sc.iter().enumerate() {
                                    let nx  = 2.0 * sx / vp_w - 1.0;
                                    let ny  = 1.0 - 2.0 * sy / vp_h;
                                    let vpx = (nx / p[0][0]) * near_vs;
                                    let vpy = (ny / p[1][1]) * near_vs;
                                    let vpz = near_vs;
                                    wp[i] = [
                                        cam_pos[0] + v[0][0]*vpx + v[1][0]*vpy + v[2][0]*vpz,
                                        cam_pos[1] + v[0][1]*vpx + v[1][1]*vpy + v[2][1]*vpz,
                                        cam_pos[2] + v[0][2]*vpx + v[1][2]*vpy + v[2][2]*vpz,
                                    ];
                                }
                            }
                            let color = [0.3, 0.7, 1.0, 1.0f32];
                            let mut lb = LineBatch::new();
                            lb.add_line(wp[0], wp[1], color);
                            lb.add_line(wp[1], wp[2], color);
                            lb.add_line(wp[2], wp[3], color);
                            lb.add_line(wp[3], wp[0], color);
                            Some(lb.build(&draw_ctx.device))
                        } else { None }
                    } else { None };

                    // ドロッププレビュー球体バッチ（ドラッグ中のみ）
                    const PREVIEW_SPHERE_RADIUS: f32 = 0.5;
                    let drop_preview_batch = if let Some(pos) = self.drop_preview_pos {
                        let mut gb = GizmoBatch::new();
                        gb.add_solid_sphere(
                            pos,
                            PREVIEW_SPHERE_RADIUS,
                            16,  // stacks
                            24,  // slices
                            Color::new(0.3, 0.8, 1.0, 0.85),
                        );
                        Some(gb.build(&draw_ctx.device))
                    } else { None };

                    // グリッド描画バッチ（エディタモード + show_grid のみ）
                    let _perf_t_grid = std::time::Instant::now();
                    // アクター編集タブの 2D キャンバス: XY 平面グリッド（常時表示）
                    // その他（シーン上のキャンバス含む 3D 系）: XZ 平面グリッド
                    // シーン上に canvas があっても 3D グリッドを維持する（is_actor_edit_canvas で判定）
                    let is_actor_edit_canvas = is_canvas && self.actor_edit_canvas_wls.contains(&self.active_world_line);
                    let grid_gpu_batch = if in_editor && (self.show_grid || (self.active_world_line != 0 && !is_actor_edit_canvas)) {
                        let mut lb = LineBatch::new();
                        // モード別グリッド色
                        // 2D アクター編集: 薄い青系（mine: 薄く, major: 中程度）
                        // 3D アクター編集: 紺背景に映える青系
                        // 3D シーン: ダークグレー
                        let (minor, major): ([f32; 4], [f32; 4]) = if is_actor_edit_canvas {
                            ([0.22, 0.25, 0.40, 0.20], [0.32, 0.40, 0.60, 0.55])
                        } else if self.active_world_line != 0 {
                            ([0.22, 0.25, 0.40, 1.0], [0.32, 0.36, 0.55, 1.0])
                        } else {
                            ([0.18, 0.18, 0.18, 1.0], [0.30, 0.30, 0.30, 1.0])
                        };
                        let ax_x: [f32; 4] = [0.60, 0.15, 0.15, 0.90];

                        if is_actor_edit_canvas {
                            // 2D モード: XY 平面グリッド（Z=0）
                            // カメラ追従 + 可視範囲に応じたステップ自動選択（Y-down 座標系）
                            // Y 軸（X=0 の縦線）: 緑、X 軸（Y=0 の横線）: 赤
                            let ax_y: [f32; 4] = [0.10, 0.55, 0.10, 0.90];

                            // 2D カメラの状態を取得（view/proj 計算で entry 生成済みのため get で OK）
                            let cam_2d = self.canvas_cameras
                                .get(&self.active_world_line)
                                .cloned()
                                .unwrap_or_default();
                            let aspect = window_size.map_or(16.0 / 9.0, |s| {
                                s.width as f32 / s.height as f32
                            });
                            let half_h_2d = cam_2d.ortho_half_h;
                            let half_w_2d = half_h_2d * aspect;

                            // 可視高さを TARGET_DIVS 分割するステップを 1/2/5 × 10^n の「良い数」に丸める
                            const TARGET_DIVS: f32 = 8.0;
                            let raw_step = (2.0 * half_h_2d) / TARGET_DIVS;
                            let exp = raw_step.log10().floor();
                            let base = 10.0f32.powf(exp);
                            let step = {
                                let m = raw_step / base;
                                if m < 1.5 { base } else if m < 3.5 { base * 2.0 } else { base * 5.0 }
                            };

                            // major ライン周期（minor 5本ごとに major 1本 → step の 5 倍ごと）
                            const MAJOR_PERIOD: i64 = 5;

                            // 可視範囲 + 1 セル分マージン（グリッドが画面端で途切れないよう）
                            let vis_x0 = cam_2d.pan_x - half_w_2d - step;
                            let vis_x1 = cam_2d.pan_x + half_w_2d + step;
                            let vis_y0 = cam_2d.pan_y - half_h_2d - step;
                            let vis_y1 = cam_2d.pan_y + half_h_2d + step;

                            // 可視範囲に含まれるグリッドインデックス（step 単位）
                            let ix_start = (vis_x0 / step).floor() as i64;
                            let ix_end   = (vis_x1 / step).ceil()  as i64;
                            let iy_start = (vis_y0 / step).floor() as i64;
                            let iy_end   = (vis_y1 / step).ceil()  as i64;

                            // 縦ライン（X = 定数, Y 方向に伸ばす）
                            for ix in ix_start..=ix_end {
                                let world_x = ix as f32 * step;
                                let is_axis  = ix == 0;
                                let is_major = !is_axis && (ix.rem_euclid(MAJOR_PERIOD) == 0);
                                let col = if is_axis { ax_x } else if is_major { major } else { minor };
                                lb.add_line([world_x, vis_y0, 0.0], [world_x, vis_y1, 0.0], col);
                            }

                            // 横ライン（Y = 定数, X 方向に伸ばす）
                            for iy in iy_start..=iy_end {
                                let world_y = iy as f32 * step;
                                let is_axis  = iy == 0;
                                let is_major = !is_axis && (iy.rem_euclid(MAJOR_PERIOD) == 0);
                                let col = if is_axis { ax_y } else if is_major { major } else { minor };
                                lb.add_line([vis_x0, world_y, 0.0], [vis_x1, world_y, 0.0], col);
                            }
                        } else {
                            // 3D モード: XZ 平面グリッド（Y=0）
                            // カメラ追従＋深度フェード＋スケール段階切り替え
                            let cam_pos = self.camera.base.transform.position;
                            let cam_y   = cam_pos[1].abs();
                            let cam_far = self.camera.base.projection.far;
                            let ax_z: [f32; 4] = [0.10, 0.10, 0.50, 1.0];

                            // グリッドスケール選択: (step, thick_period, tier_y_start, tier_y_end)
                            let (step, thick_period, tier_start, tier_end): (f32, i64, f32, f32) =
                                if cam_y < 1.0 {
                                    (0.1,  10, 0.0,  1.0)
                                } else if cam_y < 10.0 {
                                    (1.0,   5, 1.0, 10.0)
                                } else if cam_y < 30.0 {
                                    (5.0,   2, 10.0, 30.0)
                                } else {
                                    (10.0,  5, 30.0, f32::INFINITY)
                                };

                            // minor ライン alpha: 区間始端=1.0 → 区間終端≈0.0
                            let minor_alpha_base: f32 = if tier_end.is_finite() {
                                (1.0 - (cam_y - tier_start) / (tier_end - tier_start)).max(0.0)
                            } else {
                                1.0
                            };

                            // n_half を cam_far / step まで伸ばしてグリッドが far 距離まで描画される
                            let max_lines: i32 = if step < 0.5 { 300 } else { 2000 };
                            let n_half: i32 = ((cam_far / step) as i32).min(max_lines);
                            let ext    = n_half as f32 * step;
                            let snap_x = (cam_pos[0] / step).floor() * step;
                            let snap_z = (cam_pos[2] / step).floor() * step;

                            // XZ 距離ベースフェード: 各ラインをカメラの XZ 投影点でスプリット
                            // して「端=0 → スプリット点=peak → 端=0」の2セグメント描画
                            let fade_xz = |d: f32| -> f32 {
                                (1.0 - (d / ext).powi(2)).clamp(0.0, 1.0)
                            };
                            let split_x = cam_pos[0].clamp(snap_x - ext, snap_x + ext);
                            let split_z = cam_pos[2].clamp(snap_z - ext, snap_z + ext);

                            for i in -n_half..=n_half {
                                // Z 方向ライン（X = world_x で固定）
                                let world_x  = snap_x + i as f32 * step;
                                let perp_dx  = (world_x - cam_pos[0]).abs();
                                if perp_dx < ext {
                                    let idx_x    = (world_x / step).round() as i64;
                                    let is_axis  = idx_x == 0;
                                    let is_major = !is_axis && idx_x % thick_period == 0;
                                    let base_a = if is_axis {
                                        ax_z[3]
                                    } else if is_major {
                                        major[3]
                                    } else {
                                        minor[3] * minor_alpha_base
                                    };
                                    if base_a > 0.005 {
                                        let peak_a = fade_xz(perp_dx) * base_a;
                                        let rgb = if is_axis { [ax_z[0], ax_z[1], ax_z[2]] }
                                            else if is_major  { [major[0], major[1], major[2]] }
                                            else               { [minor[0], minor[1], minor[2]] };
                                        lb.add_line_grad(
                                            [world_x, 0.0, snap_z - ext],
                                            [world_x, 0.0, split_z],
                                            [rgb[0], rgb[1], rgb[2], 0.0],
                                            [rgb[0], rgb[1], rgb[2], peak_a],
                                        );
                                        lb.add_line_grad(
                                            [world_x, 0.0, split_z],
                                            [world_x, 0.0, snap_z + ext],
                                            [rgb[0], rgb[1], rgb[2], peak_a],
                                            [rgb[0], rgb[1], rgb[2], 0.0],
                                        );
                                    }
                                }

                                // X 方向ライン（Z = world_z で固定）
                                let world_z  = snap_z + i as f32 * step;
                                let perp_dz  = (world_z - cam_pos[2]).abs();
                                if perp_dz < ext {
                                    let idx_z    = (world_z / step).round() as i64;
                                    let is_axis  = idx_z == 0;
                                    let is_major = !is_axis && idx_z % thick_period == 0;
                                    let base_a = if is_axis {
                                        ax_x[3]
                                    } else if is_major {
                                        major[3]
                                    } else {
                                        minor[3] * minor_alpha_base
                                    };
                                    if base_a > 0.005 {
                                        let peak_a = fade_xz(perp_dz) * base_a;
                                        let rgb = if is_axis { [ax_x[0], ax_x[1], ax_x[2]] }
                                            else if is_major  { [major[0], major[1], major[2]] }
                                            else               { [minor[0], minor[1], minor[2]] };
                                        lb.add_line_grad(
                                            [snap_x - ext, 0.0, world_z],
                                            [split_x,      0.0, world_z],
                                            [rgb[0], rgb[1], rgb[2], 0.0],
                                            [rgb[0], rgb[1], rgb[2], peak_a],
                                        );
                                        lb.add_line_grad(
                                            [split_x,      0.0, world_z],
                                            [snap_x + ext, 0.0, world_z],
                                            [rgb[0], rgb[1], rgb[2], peak_a],
                                            [rgb[0], rgb[1], rgb[2], 0.0],
                                        );
                                    }
                                }
                            }
                        }

                        Some(lb.build(&draw_ctx.device))
                    } else { None };
                    perf_grid_ms = _perf_t_grid.elapsed().as_secs_f64() * 1000.0;

                    // ── コライダーワイヤーフレームバッチ ──────────────────────────────
                    let _perf_t_collider = std::time::Instant::now();
                    // 描画条件:
                    //   - エディタモード（3D シーンのみ）: 常に表示
                    //   - Play モード: play_collider_draw フラグが有効な場合のみ表示
                    // トリガーコライダー: 黄色 / 通常コライダー: 緑 / 衝突中: 赤
                    const COLLIDER_COLOR_NORMAL:    [f32; 4] = [0.0, 1.0, 0.2, 1.0];
                    const COLLIDER_COLOR_TRIGGER:   [f32; 4] = [1.0, 0.9, 0.0, 1.0];
                    const COLLIDER_COLOR_COLLISION: [f32; 4] = [1.0, 0.2, 0.0, 1.0];

                    let draw_colliders = (in_editor && !is_actor_edit_2d)
                        || (!in_editor && self.play_collider_draw);

                    let collider_wireframe_batch = if draw_colliders {
                        let wl = self.active_world_line;
                        let mut lb = LineBatch::new();

                        // DFS でアクターツリーを走査（子 Actor を含む）
                        // dfs_counter は physics_ops.rs の entity_id と一致させるため
                        // コライダーを持たないアクターも含めて 1-indexed でカウントする
                        let mut dfs_counter: u64 = 0;
                        let mut stack: Vec<&Actor> = scene.actors.iter()
                            .filter(|a| a.world_line == wl)
                            .rev()
                            .collect();

                        while let Some(actor) = stack.pop() {
                            // 先にインクリメント（physics_ops.rs と同一の 1-indexed カウント）
                            dfs_counter += 1;
                            let dfs_id = dfs_counter;

                            // 子を DFS スタックに追加
                            for child in actor.children.iter().rev() {
                                stack.push(child);
                            }

                            // Transform は actor.entity から取得
                            let Some(tf) = scene.world.get::<ActorTransform>(actor.entity) else { continue };

                            // Collider スロットエンティティから ColliderComponent を取得
                            let collider_slot = actor.slots().iter()
                                .find(|s| s.kind == ComponentKind::Collider);
                            let Some(cs) = collider_slot else { continue };
                            let Some(collider) = scene.world.get::<ColliderComponent>(cs.entity) else { continue };

                            // コライダー色: トリガーなら黄色、衝突中なら赤、通常なら緑
                            let color = if collider.is_trigger {
                                COLLIDER_COLOR_TRIGGER
                            } else if self.active_collision_dfs_ids.contains(&dfs_id) {
                                COLLIDER_COLOR_COLLISION
                            } else {
                                COLLIDER_COLOR_NORMAL
                            };

                            // Transform のオイラー角（YXZ 度数）からクォータニオンを生成
                            let q = Quaternion::from_euler(Vector3::new(
                                tf.rotation[0].to_radians(),
                                tf.rotation[1].to_radians(),
                                tf.rotation[2].to_radians(),
                            ));
                            let rot   = [q.x, q.y, q.z, q.w];
                            let scale = tf.scale;

                            // コライダーオフセットをワールド空間に変換して中心座標を計算
                            let off = collider.offset;
                            let off_world = q.rotate(Vector3::new(off[0], off[1], off[2]));
                            let pos = [
                                tf.position[0] + off_world.x,
                                tf.position[1] + off_world.y,
                                tf.position[2] + off_world.z,
                            ];

                            match &collider.shape {
                                ColliderShapeData::Box { half_extents } => {
                                    // スケールを半サイズに適用
                                    let he = [
                                        half_extents[0] * scale[0].abs(),
                                        half_extents[1] * scale[1].abs(),
                                        half_extents[2] * scale[2].abs(),
                                    ];
                                    lb.add_obb(pos, rot, he, color);
                                }
                                ColliderShapeData::Sphere { radius } => {
                                    // 最大スケール軸を半径に適用
                                    let r = radius * scale[0].abs()
                                        .max(scale[1].abs())
                                        .max(scale[2].abs());
                                    lb.add_sphere_at(pos, r, 24, color);
                                }
                                ColliderShapeData::Capsule { radius, half_height } => {
                                    let r  = radius * scale[0].abs().max(scale[2].abs());
                                    let hh = half_height * scale[1].abs();
                                    lb.add_capsule_wireframe(pos, rot, r, hh, 24, color);
                                }
                                ColliderShapeData::Cylinder { radius, half_height } => {
                                    let r  = radius * scale[0].abs().max(scale[2].abs());
                                    let hh = half_height * scale[1].abs();
                                    lb.add_cylinder_wireframe(pos, rot, r, hh, 24, color);
                                }
                                ColliderShapeData::Cone { radius, half_height } => {
                                    let r  = radius * scale[0].abs().max(scale[2].abs());
                                    let hh = half_height * scale[1].abs();
                                    lb.add_cone_wireframe(pos, rot, r, hh, 24, color);
                                }
                                ColliderShapeData::ConvexHull { vertices } => {
                                    // 全頂点をスケール・回転・平行移動でワールド空間に変換
                                    let world_verts: Vec<[f32; 3]> = vertices.iter()
                                        .map(|&[x, y, z]| {
                                            let sv = Vector3::new(
                                                x * scale[0], y * scale[1], z * scale[2],
                                            );
                                            let rv = q.rotate(sv);
                                            [pos[0] + rv.x, pos[1] + rv.y, pos[2] + rv.z]
                                        })
                                        .collect();
                                    lb.add_convex_hull_wireframe(&world_verts, color);
                                }
                                ColliderShapeData::TriangleMesh { triangles } => {
                                    // 全三角形頂点をワールド空間に変換
                                    let world_tris: Vec<[[f32; 3]; 3]> = triangles.iter()
                                        .map(|tri| {
                                            tri.map(|[x, y, z]| {
                                                let sv = Vector3::new(
                                                    x * scale[0], y * scale[1], z * scale[2],
                                                );
                                                let rv = q.rotate(sv);
                                                [pos[0] + rv.x, pos[1] + rv.y, pos[2] + rv.z]
                                            })
                                        })
                                        .collect();
                                    lb.add_triangle_mesh_wireframe(&world_tris, color);
                                }
                            }
                        }

                        if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) }
                    } else { None };
                    perf_collider_ms = _perf_t_collider.elapsed().as_secs_f64() * 1000.0;

                    // ── 2D コライダーワイヤーフレームバッチ ──────────────────────────────
                    // 描画条件:
                    //   - 2D キャンバス世界線（is_canvas）のみ
                    //   - エディタモード: 常に表示
                    //   - Play モード: play_collider_draw フラグが有効な場合のみ表示
                    // トリガーコライダー: 黄色 / 通常コライダー: 緑 / 衝突中: 赤
                    let draw_colliders_2d = is_canvas
                        && (in_editor || self.play_collider_draw);

                    let collider_2d_wireframe_batch = if draw_colliders_2d {
                        // キャンバス座標 → レンダリング座標変換スケール
                        let canvas_scale = if use_screen_space { 1.0f32 } else { CANVAS_WORLD_SCALE };
                        // Y 軸方向: スクリーンスペース時は Y+ が下（CSS と同方向）
                        let y_sign = if use_screen_space { 1.0f32 } else { -1.0 };

                        let mut lb = LineBatch::new();

                        // collect_actor2d_contexts に viewport_size を渡す。
                        // canvas_collect.rs と同一の変換チェーンで body_pos_px が計算される。
                        // SS モード時は ortho 空間（ビューポート中心が原点）で返ってくるため、
                        // ワイヤーフレーム描画はコライダーオフセットを加算するだけでよい。
                        let vp_wf = window_size.map_or(1280.0f32, |s| s.width  as f32);
                        let vp_hf = window_size.map_or(720.0f32,  |s| s.height as f32);
                        let viewport_size_2d = if scene_canvas_ss { Some([vp_wf, vp_hf]) } else { None };
                        // CanvasViewportRef::Camera を持つルートキャンバスのビューポートサイズを解決する
                        let canvas_vp_overrides_2d = if scene_canvas_ss {
                            build_canvas_viewport_map(
                                &scene.actors, &scene.world,
                                self.active_world_line, vp_wf, vp_hf,
                                if !in_editor { Some(game_viewport) } else { None },
                            )
                        } else {
                            std::collections::HashMap::new()
                        };
                        let ctx2d_list = crate::engine::core::app_base::app::physics2d_ops::collect_actor2d_contexts(
                            scene, self.active_world_line, viewport_size_2d, &canvas_vp_overrides_2d,
                        );

                        for ctx in &ctx2d_list {
                            let Some(slot_entity) = ctx.collider_slot_entity else { continue };
                            let Some(collider) = scene.world.get::<Collider2dComponent>(slot_entity) else { continue };

                            // コライダー色: トリガーなら黄色、衝突中なら赤、通常なら緑
                            let color = if collider.is_trigger {
                                COLLIDER_COLOR_TRIGGER
                            } else if self.active_collision_2d_dfs_ids.contains(&ctx.dfs_id) {
                                COLLIDER_COLOR_COLLISION
                            } else {
                                COLLIDER_COLOR_NORMAL
                            };

                            let rot_rad = ctx.rot_rad;
                            let scale   = ctx.scale;
                            let (sin, cos) = rot_rad.sin_cos();

                            // コライダーオフセットをボディ回転で変換する（キャンバスピクセル単位）
                            let [ox, oy] = collider.offset;
                            let off_wx = cos * ox - sin * oy;
                            let off_wy = sin * ox + cos * oy;

                            // body_pos_px は canvas_collect.rs と同一の変換で ortho 空間で計算済み。
                            // コライダーオフセットは ctx.size_sx/size_sy でスケールして加算する。
                            let (cx, cy, eff_sx, eff_sy) = if scene_canvas_ss {
                                let cx = ctx.body_pos_px[0] + off_wx * ctx.size_sx;
                                let cy = (ctx.body_pos_px[1] + off_wy * ctx.size_sy) * y_sign;
                                (cx, cy, ctx.size_sx, ctx.size_sy)
                            } else {
                                (
                                    (ctx.body_pos_px[0] + off_wx) * canvas_scale,
                                    (ctx.body_pos_px[1] + off_wy) * canvas_scale * y_sign,
                                    canvas_scale,
                                    canvas_scale,
                                )
                            };

                            match &collider.shape {
                                ColliderShape2dData::Box { half_extents } => {
                                    let hx = half_extents[0] * scale[0].abs() * eff_sx;
                                    let hy = half_extents[1] * scale[1].abs() * eff_sy;
                                    lb.add_box_2d([cx, cy], rot_rad * y_sign, [hx, hy], 0.0, color);
                                }
                                ColliderShape2dData::Circle { radius } => {
                                    let r = radius * scale[0].abs().max(scale[1].abs()) * eff_sx.max(eff_sy);
                                    lb.add_circle_2d([cx, cy], r, 32, 0.0, color);
                                }
                                ColliderShape2dData::Capsule { radius, half_height } => {
                                    let r  = radius * scale[0].abs().max(scale[1].abs()) * eff_sx.max(eff_sy);
                                    let hh = half_height * scale[1].abs() * eff_sy;
                                    lb.add_capsule_2d([cx, cy], rot_rad * y_sign, r, hh, 16, 0.0, color);
                                }
                                ColliderShape2dData::ConvexHull { vertices } => {
                                    let world_verts: Vec<[f32; 2]> = vertices.iter()
                                        .map(|&[vx, vy]| {
                                            let svx = vx * scale[0];
                                            let svy = vy * scale[1];
                                            let rwx = cos * svx - sin * svy;
                                            let rwy = sin * svx + cos * svy;
                                            [
                                                cx + rwx * eff_sx,
                                                cy + rwy * eff_sy * y_sign,
                                            ]
                                        })
                                        .collect();
                                    lb.add_convex_hull_2d(&world_verts, 0.0, color);
                                }
                            }
                        }

                        if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) }
                    } else { None };

                    // スプライト描画リソース収集（render pass 前に GPU バッファを準備する）
                    // CanvasTransform + SpriteComponent を持つアクターを列挙し、
                    // テクスチャをキャッシュから取得または新規ロードして SpritePrepared を生成する。
                    // Edit / Play 両モードで収集する（in_editor チェックなし）。
                    //
                    // 【重要】2D スプライトと 3D Canvas スプライトを分離して収集する。
                    //   - sprite_prepared_2d: Actor2D（CanvasTransform）のスプライト。
                    //     scene_canvas_ss=true のときはオーバーレイパス（2D オルソカメラ）で描画。
                    //   - sprite_prepared_3d: Actor3D + CanvasComponent の子スプライト。
                    //     scene_canvas_ss の値に関わらず「常に」メインパス（3D カメラ）で描画する。
                    //     2D アクターが混在するシーンで scene_canvas_ss=true になっても、
                    //     3D Canvas が 2D オルソカメラで極小点として映るバグを防ぐ。
                    let (sprite_prepared_2d, sprite_prepared_3d) = {
                        // 2D キャンバスアクターのスプライト（オルソ／ワールドスペース 2D 用）
                        let mut items_2d = Vec::new();
                        // 3D Canvas（Actor3D + CanvasComponent）の子スプライト（3D 透視カメラ用）
                        let mut items_3d = Vec::new();

                        if let Some(scene) = &self.scene {
                            let wl = self.active_world_line;

                            // ── 2D キャンバス世界線のスプライト ──────────────────────────
                            if is_canvas {
                                // ワールドスペース時はキャンバス座標をワールドユニットへスケールする
                                let canvas_scale = if use_screen_space { 1.0f32 } else { CANVAS_WORLD_SCALE };
                                // 単位行列・初期累積スケール（ルートレベル用）
                                const IDENTITY: [[f32; 4]; 4] = [
                                    [1.0, 0.0, 0.0, 0.0],
                                    [0.0, 1.0, 0.0, 0.0],
                                    [0.0, 0.0, 1.0, 0.0],
                                    [0.0, 0.0, 0.0, 1.0],
                                ];
                                // Y 軸符号とビューポートサイズを決定する
                                let y_sign = if use_screen_space { 1.0f32 } else { -1.0 };
                                let is_scene_ss = use_screen_space && !self.actor_edit_canvas_wls.contains(&wl);
                                let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                                let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                                let viewport_size = if is_scene_ss { Some([vp_w, vp_h]) } else { None };
                                let play_gvp = if is_scene_ss && !in_editor { Some(game_viewport) } else { None };
                                let canvas_vp_overrides = if is_scene_ss {
                                    build_canvas_viewport_map(&scene.actors, &scene.world, wl, vp_w, vp_h, play_gvp)
                                } else {
                                    std::collections::HashMap::new()
                                };
                                collect_sprite_items(
                                    &scene.actors, &scene.world, wl, draw_ctx,
                                    None, IDENTITY, [1.0, 1.0], (false, false, false, true),
                                    canvas_scale, y_sign, viewport_size, &canvas_vp_overrides, &mut items_2d,
                                );
                            }

                            // ── 3D Canvas のスプライト（is_canvas に関わらず常に収集）──
                            // Actor3D + CanvasComponent を持つアクターをワールド空間で描画する。
                            //
                            // 座標変換の設計（3D 透視カメラは Vulkan Y-DOWN：world +Y → screen 下）:
                            //   canvas_to_world = actor_3d_mat × Scale(CANVAS_WORLD_SCALE, CANVAS_WORLD_SCALE, 1)
                            //   Y 反転なし — キャンバス Y+（下）はワールド Y+（3D カメラで screen 下）に対応 ✓
                            //   1px = 1cm（CANVAS_WORLD_SCALE = 0.01m）
                            for actor in &scene.actors {
                                if actor.world_line != wl { continue; }
                                if actor.is_2d() { continue; } // Actor3D のみ
                                let canvas_slot = actor.slots().iter()
                                    .find(|s| s.kind == crate::engine::components::ComponentKind::Canvas);
                                let Some(canvas_slot) = canvas_slot else { continue };
                                let Some(cc) = scene.world.get::<crate::engine::components::CanvasComponent>(canvas_slot.entity) else { continue };

                                // Actor3D の ActorTransform からワールド行列を取得する
                                let Some(tf) = scene.world.get::<crate::engine::components::Transform>(actor.entity) else { continue };
                                let actor_3d_mat = tf.to_mat4();

                                let cws = CANVAS_WORLD_SCALE;
                                let (piv_x, piv_y) = (cc.pivot[0], cc.pivot[1]);
                                // キャンバス Y+（下）→ ワールド Y-（Y-UP カメラで screen 下）
                                // pivot でアクター位置がキャンバスのどの点に対応するかを決める。
                                // (0,0)=左上原点（デフォルト）: アクター位置がキャンバス左上
                                // (0.5,0.5)=中央: アクター位置がキャンバス中心
                                // ローカル行列: [[cws,0,0,-piv_x*w*cws],[0,-cws,0,piv_y*h*cws],[0,0,1,0],[0,0,0,1]]
                                let canvas_to_world = crate::engine::methods::gizmo_interact::mat4x4_mul(
                                    actor_3d_mat,
                                    [
                                        [ cws,  0.0, 0.0, -piv_x * cc.width  * cws],
                                        [ 0.0, -cws, 0.0,  piv_y * cc.height * cws],
                                        [ 0.0,  0.0, 1.0,  0.0                    ],
                                        [ 0.0,  0.0, 0.0,  1.0                    ],
                                    ],
                                );
                                let child_scale_mode = (
                                    cc.scale_transform, cc.scale_size,
                                    cc.keep_aspect_ratio,
                                    matches!(cc.aspect_ratio_axis, crate::engine::components::AspectRatioAxis::Width),
                                );
                                collect_sprite_items(
                                    &actor.children, &scene.world, wl, draw_ctx,
                                    Some([cc.width, cc.height]),
                                    canvas_to_world, [1.0, 1.0], child_scale_mode,
                                    1.0, 1.0,
                                    None, &std::collections::HashMap::new(), &mut items_3d,
                                );
                            }
                        }
                        (
                            prepare_sprites_from_mats(&draw_ctx.device, &draw_ctx.pipelines.sprite, &items_2d),
                            prepare_sprites_from_mats(&draw_ctx.device, &draw_ctx.pipelines.sprite, &items_3d),
                        )
                    };

                    // CanvasComponent 矩形アウトラインバッチ（エディタモード + 2D キャンバス世界線のみ）
                    // Canvas のアウトラインは常に表示、Sprite のアウトラインは選択時のみ表示する。
                    let canvas_rect_batch = if in_editor && is_canvas {
                        if let Some(scene) = &self.scene {
                            let wl = self.active_world_line;
                            let mut lb = LineBatch::new();
                            let rect_col: [f32; 4] = [0.85, 0.95, 1.0, 0.9];
                            // ワールドスペース時はキャンバス座標をワールドユニットへスケールする
                            let canvas_scale_rect = if use_screen_space { 1.0f32 } else { CANVAS_WORLD_SCALE };


                            const IDENTITY_RECT: [[f32; 4]; 4] = [
                                [1.0, 0.0, 0.0, 0.0],
                                [0.0, 1.0, 0.0, 0.0],
                                [0.0, 0.0, 1.0, 0.0],
                                [0.0, 0.0, 0.0, 1.0],
                            ];
                            let mut counter: u32 = 0;
                            // rect アウトライン用 y_sign と viewport_size
                            let y_sign_rect = if use_screen_space { 1.0f32 } else { -1.0 };
                            let is_scene_ss_rect = use_screen_space && !self.actor_edit_canvas_wls.contains(&wl);
                            let vp_w_r = window_size.map_or(1280.0, |s| s.width  as f32);
                            let vp_h_r = window_size.map_or(720.0,  |s| s.height as f32);
                            let viewport_size_rect = if is_scene_ss_rect { Some([vp_w_r, vp_h_r]) } else { None };
                            // Camera 参照のルートキャンバスはビューポートオーバーライドマップを使用する
                            let play_gvp_r = if is_scene_ss_rect && !in_editor { Some(game_viewport) } else { None };
                            let canvas_vp_overrides_r = if is_scene_ss_rect {
                                build_canvas_viewport_map(&scene.actors, &scene.world, wl, vp_w_r, vp_h_r, play_gvp_r)
                            } else {
                                std::collections::HashMap::new()
                            };
                            collect_canvas_rects(
                                &scene.actors, &scene.world, wl, &mut lb, rect_col,
                                &self.selected_actor_dfs_ids, &mut counter,
                                None, IDENTITY_RECT, [1.0, 1.0], (false, false, false, true),
                                canvas_scale_rect, y_sign_rect, viewport_size_rect, &canvas_vp_overrides_r,
                            );
                            if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) }
                        } else { None }
                    } else { None };

                    // ── 3D Canvas アウトライン（エディタモード）──────────────────────────────
                    // Actor3D + CanvasComponent を持つアクターの矩形境界を 3D ワールド空間で描画する。
                    // 選択時はオレンジ（Sprite アウトラインと同色）、非選択時は青白。
                    let canvas_3d_rect_batch = if in_editor {
                        if let Some(scene) = &self.scene {
                            let wl = self.active_world_line;
                            let mut lb = LineBatch::new();
                            const RECT_COL_NORMAL:   [f32; 4] = [0.85, 0.95, 1.0, 0.9];
                            const RECT_COL_SELECTED: [f32; 4] = [1.0, 0.5, 0.05, 1.0];

                            // find_actor_by_dfs と同じ規則で DFS ID を計算するため、
                            // アクター本体 + 全子孫の数をカウントするヘルパー（world_line 無関係）
                            fn count_descendants(actor: &Actor) -> u32 {
                                actor.children.len() as u32
                                    + actor.children.iter().map(count_descendants).sum::<u32>()
                            }

                            let mut dfs_counter: u32 = 0;
                            for actor in &scene.actors {
                                if actor.world_line != wl {
                                    // world_line 不一致: DFS カウンタを進めずスキップ
                                    continue;
                                }
                                let my_dfs = dfs_counter;
                                // このアクター (1) + 全子孫分カウンタを進める
                                dfs_counter += 1 + count_descendants(actor);

                                if actor.is_2d() { continue; } // Actor3D のみ
                                let canvas_slot = actor.slots().iter()
                                    .find(|s| s.kind == crate::engine::components::ComponentKind::Canvas);
                                let Some(canvas_slot) = canvas_slot else { continue };
                                let Some(cc) = scene.world.get::<crate::engine::components::CanvasComponent>(canvas_slot.entity) else { continue };
                                let Some(tf) = scene.world.get::<crate::engine::components::Transform>(actor.entity) else { continue };

                                // 選択中かどうかで色を切り替える
                                let rect_col = if self.selected_actor_dfs_ids.contains(&(my_dfs as usize)) {
                                    RECT_COL_SELECTED
                                } else {
                                    RECT_COL_NORMAL
                                };

                                let cws = CANVAS_WORLD_SCALE;
                                let (piv_x, piv_y) = (cc.pivot[0], cc.pivot[1]);
                                let w = cc.width;
                                let h = cc.height;
                                let m = crate::engine::methods::gizmo_interact::mat4x4_mul(
                                    tf.to_mat4(),
                                    [[ cws,  0.0, 0.0, -piv_x * w * cws],
                                     [ 0.0, -cws, 0.0,  piv_y * h * cws],
                                     [ 0.0,  0.0, 1.0,  0.0            ],
                                     [ 0.0,  0.0, 0.0,  1.0            ]],
                                );
                                let tp = |cx: f32, cy: f32| -> [f32; 3] {
                                    [m[0][0]*cx + m[0][1]*cy + m[0][3],
                                     m[1][0]*cx + m[1][1]*cy + m[1][3],
                                     m[2][0]*cx + m[2][1]*cy + m[2][3]]
                                };
                                let tl = tp(0.0, 0.0);
                                let tr = tp(w,   0.0);
                                let br = tp(w,   h  );
                                let bl = tp(0.0, h  );
                                lb.add_line(tl, tr, rect_col);
                                lb.add_line(tr, br, rect_col);
                                lb.add_line(br, bl, rect_col);
                                lb.add_line(bl, tl, rect_col);
                            }

                            if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) }
                        } else { None }
                    } else { None };

                    // ── 選択 3D Canvas 子スプライトのアウトライン（sprite_outline パイプライン使用）──
                    // sprite_outline.wgsl がクリップ空間でコーナーを押し出すため、
                    // 3D アウトラインと同一の OUTLINE_THICKNESS = 0.0075（NDC 幅）を達成する。
                    // モデル行列は実スプライトと同じ（シェーダー側で拡大するため Rust 側の計算不要）。
                    // 描画順: アウトライン Quad → 実スプライト → 外枠だけがオレンジとして見える。
                    const ORANGE: [f32; 4] = [1.0, 0.5, 0.05, 1.0];

                    let sprite_3d_outline_items:
                        Vec<([[f32; 4]; 4], [f32; 4], Option<std::sync::Arc<GpuSpriteTexture>>)> =
                    if in_editor && !self.selected_actor_dfs_ids.is_empty() {
                        if let Some(scene) = &self.scene {
                            let wl = self.active_world_line;
                            let mut items = Vec::new();

                            for &dfs_id in &self.selected_actor_dfs_ids {
                                let mut c = 0u32;
                                let Some(actor) = find_actor_by_dfs(
                                    &scene.actors, wl, dfs_id as u32, &mut c,
                                ) else { continue };

                                // 2D アクター（CanvasTransform 持ち）のみ対象
                                if !actor.is_2d() { continue; }

                                // SpriteComponent を持つか確認（テクスチャあり・なし両方対象）
                                let sprite_slot = actor.slots().iter()
                                    .find(|s| s.kind == ComponentKind::Sprite);
                                let Some(ss) = sprite_slot else { continue };
                                let Some(sc) = scene.world.get::<
                                    crate::engine::components::SpriteComponent>(ss.entity)
                                else { continue };

                                // 親が Actor3D + CanvasComponent か確認し、ctw と親 CC サイズを取得する
                                let mut c_p = 0u32;
                                let parent_actor = find_parent_actor_of_dfs(
                                    &scene.actors, wl, dfs_id as u32, &mut c_p, None,
                                );
                                let Some(parent) = parent_actor else { continue };
                                let Some(ctw) = get_3d_canvas_world_mat(parent, &scene.world)
                                    else { continue };

                                // 親の CanvasComponent からアンカーオフセット計算用サイズを取得する
                                let parent_cc_size: Option<[f32; 2]> = parent.slots().iter()
                                    .find(|s| s.kind == ComponentKind::Canvas)
                                    .and_then(|s| scene.world.get::<
                                        crate::engine::components::CanvasComponent>(s.entity))
                                    .map(|pcc| [pcc.width, pcc.height]);

                                let Some(ct) = scene.world.get::<CanvasTransform>(actor.entity)
                                    .cloned() else { continue };

                                // アンカーオフセットを適用した有効 CanvasTransform を構築する
                                // （collect_sprite_items / walk_3d_canvas_children_id と同じロジック）
                                let eff_ct = if let Some([pw, ph]) = parent_cc_size {
                                    let off_x = pw * ct.anchor[0];
                                    let off_y = ph * ct.anchor[1];
                                    CanvasTransform {
                                        position: [ct.position[0] + off_x, ct.position[1] + off_y],
                                        anchor:   [0.0, 0.0],
                                        ..ct
                                    }
                                } else {
                                    ct
                                };

                                // スプライトのワールド行列（outline シェーダーが拡大するため同行列を使う）
                                let sw = crate::engine::methods::gizmo_interact::mat4x4_mul(
                                    ctw,
                                    eff_ct.to_sprite_mat4(sc.width, sc.height),
                                );
                                // GPU 列優先モデル行列（WGSL col0〜col3 = Rust row0〜row3）
                                let gpu_mat = [
                                    [sw[0][0], sw[1][0], sw[2][0], 0.0],
                                    [sw[0][1], sw[1][1], sw[2][1], 0.0],
                                    [sw[0][2], sw[1][2], sw[2][2], 0.0],
                                    [sw[0][3], sw[1][3], sw[2][3], 1.0],
                                ];
                                items.push((gpu_mat, ORANGE, None));
                            }
                            items
                        } else { vec![] }
                    } else { vec![] };

                    // outline パイプライン用の GPU リソースを準備する（SpriteUniform のみ）
                    let sprite_3d_outline_prepared = prepare_sprites_from_mats(
                        &draw_ctx.device, &draw_ctx.pipelines.sprite, &sprite_3d_outline_items,
                    );

                    // 軸ギズモバッチ（エディタモード + show_axis_gizmo のみ）
                    let axis_gizmo_batch = if in_editor && self.show_axis_gizmo {
                        let sw  = window_size.map_or(1280.0, |s| s.width  as f32);
                        let sh  = window_size.map_or(720.0,  |s| s.height as f32);
                        let rot = self.camera.base.transform.rotation;
                        // ホバー判定を毎フレーム更新する
                        self.axis_gizmo_hovered = self.last_cursor_pos.and_then(|(cx, cy)| {
                            crate::engine::core::font::axis_gizmo::AxisGizmo::hit_test(
                                cx, cy, rot, sw, sh,
                            )
                        });
                        self.axis_gizmo.as_mut().map(|ag| {
                            ag.build(rot, sw, sh, &draw_ctx.device, &draw_ctx.queue,
                                self.axis_gizmo_hovered)
                        })
                    } else {
                        self.axis_gizmo_hovered = None;
                        None
                    };



                    // アイコンオーバーレイバッチ（エディタモードのみ）
                    // 全選択アクター（マルチ選択対応）の 3D Transform 位置をスクリーン投影してアイコンを表示する。
                    // キャンバスアクター（2D）はスクリーンスペース描画のため 3D 投影をスキップする。
                    let icon_overlay_batch = if in_editor {
                        let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                        let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                        let (view, proj) = (self.camera.view_matrix(), self.camera.projection_matrix());
                        let positions: Vec<(f32, f32)> = if !self.selected_instances.is_empty() {
                            // レガシーインスタンス選択（ModelComponent クリック）
                            selected_mc
                                .map(|mc| {
                                    self.selected_instances.iter()
                                        .filter_map(|&i| {
                                            let mat = mc.instance_mats.get(i as usize)?;
                                            let world = [mat[0][3], mat[1][3], mat[2][3]];
                                            world_to_screen(world, &view.data, &proj.data, vp_w, vp_h)
                                        })
                                        .collect()
                                })
                                .unwrap_or_default()
                        } else if !self.selected_actor_dfs_ids.is_empty() {
                            // DFS 選択（シーンモード・アクター編集モード共通）
                            // world_line != 0 の制限を撤廃してメインシーンでも表示する
                            if let Some(scene) = &self.scene {
                                let wl = self.active_world_line;
                                self.selected_actor_dfs_ids.iter()
                                    .filter_map(|&dfs_id| {
                                        let mut c = 0u32;
                                        let actor = find_actor_by_dfs(
                                            &scene.actors, wl, dfs_id as u32, &mut c,
                                        )?;
                                        // 3D アクターの Transform 位置を使う（2D はスキップ）
                                        let pos = scene.world
                                            .get::<ActorTransform>(actor.entity)?
                                            .position;
                                        world_to_screen(pos, &view.data, &proj.data, vp_w, vp_h)
                                    })
                                    .collect()
                            } else {
                                Vec::new()
                            }
                        } else {
                            Vec::new()
                        };
                        crate::engine::core::font::icon_overlay::IconOverlay::build(
                            &positions, vp_w, vp_h, &draw_ctx.device,
                        )
                    } else { None };

                    // ── MMB スティック HUD バッチ事前構築 ──────────────────────
                    // GpuLineBatch はパスより長いライフタイムが必要なため、pass 開始前に構築する
                    let mmb_stick_gpu = if in_editor && self.cam_input.mmb {
                        let (vp_w, vp_h) = window_size.map_or(
                            (1280.0f32, 720.0f32),
                            |s| (s.width as f32, s.height as f32),
                        );
                        // ビューポートローカル座標（左上原点）→ オーソグラフィック中心原点（Y-down）
                        let half_w   = vp_w / 2.0;
                        let half_h   = vp_h / 2.0;
                        let origin_x = self.cam_input.mmb_origin_x - half_w;
                        let origin_y = self.cam_input.mmb_origin_y - half_h;
                        let offset_x = self.cam_input.cursor_x - self.cam_input.mmb_origin_x;
                        let offset_y = self.cam_input.cursor_y - self.cam_input.mmb_origin_y;
                        use crate::engine::structs::objects::camera::debug_camera::MMB_OUTER_RADIUS;
                        // HUD 表示はクランプ半径と独立したコンパクトなサイズで描く
                        const HUD_OUTER_R: f32 = 40.0;
                        const INNER_R:     f32 =  8.0;
                        // クランプ空間→HUD空間へオフセットをスケールして内円位置を正確に反映する
                        let hud_scale   = HUD_OUTER_R / MMB_OUTER_RADIUS;
                        let hud_off_x   = offset_x * hud_scale;
                        let hud_off_y   = offset_y * hud_scale;
                        let mut sb = LineBatch::new();
                        sb.add_mmb_stick([origin_x, origin_y], hud_off_x, hud_off_y, HUD_OUTER_R, INNER_R);
                        if sb.is_empty() { None } else { Some(sb.build(&draw_ctx.device)) }
                    } else { None };

                    // ── メインレンダーパス ────────────────
                    let _perf_t_main = std::time::Instant::now();
                    {
                        // Play モード: ゲームカメラのクリアカラーで全体クリア
                        // （帯エリアは begin_render_pass 後に BarFillPipeline で別途塗りつぶす）
                        // Edit モード: アクター編集タブは紺色、通常はダークグレー
                        let clear_color = if self.mode == RuntimeMode::Play && !self.paused {
                            let [r, g, b, a] = game_clear_color;
                            wgpu::Color { r: r as f64, g: g as f64, b: b as f64, a: a as f64 }
                        } else if self.active_world_line != 0 {
                            wgpu::Color { r: 0.05, g: 0.08, b: 0.18, a: 1.0 }
                        } else {
                            wgpu::Color { r: 0.1,  g: 0.1,  b: 0.1,  a: 1.0 }
                        };
                        let mut pass = frame.begin_render_pass(clear_color);

                        // LetterBox / PillarBox 時: ビューポート設定前に帯エリアを帯カラーで塗る。
                        // LoadOp::Clear はサーフェス全体をクリアするため、ゲーム以外のエリアを
                        // BarFillPipeline で上書きすることで正しい帯カラーを適用する。
                        if self.mode == RuntimeMode::Play && !self.paused && uses_bar_mode {
                            let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                            // ピクセル座標 → NDC 変換
                            // ndc_x(px) = px / win_w * 2 - 1
                            // ndc_y(py) = 1 - py / win_h * 2  (ピクセル Y は上が 0、NDC Y は上が +1)
                            let to_ndc_x = |px: f32| px / win_w_f * 2.0 - 1.0;
                            let to_ndc_y = |py: f32| 1.0 - py / win_h_f * 2.0;

                            // 4 辺の帯候補（面積 0 の帯は描画スキップ）
                            let bar_rects = [
                                // 上帯: Y=0〜vp_y
                                (0.0, 0.0, win_w_f, vp_y),
                                // 下帯: Y=(vp_y+vp_h)〜win_h
                                (0.0, vp_y + vp_h, win_w_f, win_h_f),
                                // 左帯: X=0〜vp_x
                                (0.0, 0.0, vp_x, win_h_f),
                                // 右帯: X=(vp_x+vp_w)〜win_w
                                (vp_x + vp_w, 0.0, win_w_f, win_h_f),
                            ];
                            for (px0, py0, px1, py1) in bar_rects {
                                // 面積 0 の帯はスキップ（LetterBox なら左右帯が幅 0 になる等）
                                if px1 - px0 < 0.5 || py1 - py0 < 0.5 { continue; }
                                // ピクセル矩形を NDC 矩形に変換（y は上下反転）
                                let ndc_x0 = to_ndc_x(px0);
                                let ndc_x1 = to_ndc_x(px1);
                                let ndc_y0 = to_ndc_y(py1); // py が大きいほど NDC y が小さい
                                let ndc_y1 = to_ndc_y(py0);
                                draw_ctx.pipelines.bar_fill.draw(
                                    &mut pass, &draw_ctx.device,
                                    game_bar_color, ndc_x0, ndc_y0, ndc_x1, ndc_y1,
                                );
                            }
                        }

                        // Play モード: スケーリングモードに応じてビューポートを設定する。
                        // LetterBox/PillarBox では set_scissor_rect によって黒帯へのはみ出しをクリップする。
                        // VertMinus/HorPlus/FullScale は全画面ビューポートのままなので実質ノーオペレーション。
                        if self.mode == RuntimeMode::Play && !self.paused {
                            let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                            pass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                            pass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                        }
                        // 全 MC を統合バッチで描画（N_actors → N_unique_models 回の draw call）
                        let _perf_t_draw = std::time::Instant::now();
                        for (path, sd) in &self.shared_model_batches {
                            if let Some(&gpu) = gpu_model_by_path.get(path.as_str()) {
                                draw_model_indirect(
                                    &mut pass, gpu, &sd.batch,
                                    &camera_buf.bind_group, &draw_ctx.pipelines,
                                );
                            }
                        }
                        perf_draw_ms = _perf_t_draw.elapsed().as_secs_f64() * 1000.0;

                        // ドロッププレビュー球体描画（ドラッグ中のみ）
                        if let (Some(preview_batch), Some((_, line_bg))) =
                            (&drop_preview_batch, &self.line_model_buf)
                        {
                            draw_gizmo_batch(
                                &mut pass, preview_batch,
                                &camera_buf.bind_group, line_bg,
                                &draw_ctx.pipelines,
                            );
                        }

                        // グリッド描画（スプライト・選択矩形より先に描画）
                        // sprite/unlit パイプラインは depth_write=false かつ LessEqual のため、
                        // グリッドより後に描画することでグリッドの上に正しく重なる。
                        if let (Some(grid_batch), Some((_, line_bg))) =
                            (&grid_gpu_batch, &self.line_model_buf)
                        {
                            draw_line_batch(
                                &mut pass, grid_batch,
                                &camera_buf.bind_group, line_bg,
                                &draw_ctx.pipelines,
                            );
                        }

                        // 3D Canvas 子スプライト選択アウトライン（sprite_outline パイプライン）
                        // クリップ空間でコーナーを押し出し 3D モデルと同一幅のアウトラインを実現する。
                        // 実スプライトより先に描画することで、外枠だけがオレンジとして残る。
                        if !sprite_3d_outline_prepared.is_empty() {
                            draw_sprite_outline(
                                &mut pass,
                                &draw_ctx.pipelines.sprite,
                                &draw_ctx.pipelines.sprite_outline,
                                &camera_buf.bind_group,
                                &sprite_3d_outline_prepared,
                            );
                        }

                        // スプライト画像描画（アウトラインより後に描画し、アウトラインの内側を覆う）
                        //
                        // 3D Canvas スプライト: scene_canvas_ss に関わらず常にメインパスで描画する。
                        // 2D アクターが混在するシーン（scene_canvas_ss=true）でも 3D カメラを使うため。
                        if !sprite_prepared_3d.is_empty() {
                            draw_sprites(
                                &mut pass,
                                &draw_ctx.pipelines.sprite,
                                &camera_buf.bind_group,
                                &sprite_prepared_3d,
                            );
                        }
                        // 2D キャンバススプライト: scene_canvas_ss の場合はオーバーレイパスで描画する。
                        if !scene_canvas_ss && !sprite_prepared_2d.is_empty() {
                            draw_sprites(
                                &mut pass,
                                &draw_ctx.pipelines.sprite,
                                &camera_buf.bind_group,
                                &sprite_prepared_2d,
                            );
                        }

                        // CanvasComponent 矩形アウトライン描画（グリッドより前面）
                        // Canvas: 常に表示, Sprite: 選択時のみ表示
                        // scene_canvas_ss の場合はオーバーレイパスで描画するためスキップ
                        if !scene_canvas_ss {
                            if let (Some(rect_batch), Some((_, line_bg))) =
                                (&canvas_rect_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut pass, rect_batch,
                                    &camera_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }
                        }

                        // 3D Canvas アウトライン（is_canvas に関わらず常に描画）
                        if let (Some(rect_batch), Some((_, line_bg))) =
                            (&canvas_3d_rect_batch, &self.line_model_buf)
                        {
                            draw_line_batch(
                                &mut pass, rect_batch,
                                &camera_buf.bind_group, line_bg,
                                &draw_ctx.pipelines,
                            );
                        }

                        // 矩形選択ビジュアル（グリッドより後に描画し前面に表示）
                        // scene_canvas_ss はオーバーレイパスで描画するためスキップ
                        if !scene_canvas_ss {
                            if let (Some(rect_batch), Some((_, line_bg))) =
                                (&rect_gpu_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut pass, rect_batch,
                                    &camera_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }
                        }

                        // アウトライン: 全選択アクター（マルチ選択対応）※グリッドより前面に描画
                        // 統合バッチを使用することで、スキンアニメーション済みの
                        // ジョイント行列が正しく反映されたアウトラインが得られる。
                        if in_editor {
                            if !self.selected_actor_dfs_ids.is_empty() {
                                // Phase 1: 全選択アクターのステンシルマスクを書き込む
                                for &dfs_id in &self.selected_actor_dfs_ids {
                                    if let Some((path, &merged_start, &n_insts)) =
                                        mc_outline_map.get(&(dfs_id as u32, 0usize))
                                            .map(|(p, s, n)| (p, s, n))
                                    {
                                        if let (Some(&gpu), Some(sd)) = (
                                            gpu_model_by_path.get(path.as_str()),
                                            self.shared_model_batches.get(path),
                                        ) {
                                            // 統合バッチ内のこのアクターのインスタンスインデックス列
                                            let merged_insts: Vec<u32> =
                                                (merged_start..merged_start + n_insts).collect();
                                            draw_stencil_mask_multi(
                                                &mut pass, gpu, &sd.batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                                &merged_insts,
                                            );
                                        }
                                    }
                                }
                                // Phase 2: 全選択アクターのアウトラインを描画
                                for &dfs_id in &self.selected_actor_dfs_ids {
                                    if let Some((path, &merged_start, &n_insts)) =
                                        mc_outline_map.get(&(dfs_id as u32, 0usize))
                                            .map(|(p, s, n)| (p, s, n))
                                    {
                                        if let (Some(&gpu), Some(sd)) = (
                                            gpu_model_by_path.get(path.as_str()),
                                            self.shared_model_batches.get(path),
                                        ) {
                                            let merged_insts: Vec<u32> =
                                                (merged_start..merged_start + n_insts).collect();
                                            draw_outline_multi(
                                                &mut pass, gpu, &sd.batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                                &merged_insts,
                                            );
                                        }
                                    }
                                }
                            } else if !self.selected_instances.is_empty() {
                                // レガシー: インスタンス直接選択（後方互換）
                                // selected_instances は per-MC インスタンスインデックスなので、
                                // 統合バッチ内インデックスへ merged_start だけオフセットする
                                if let Some(dfs) = self.actor_virtual_selected_idx {
                                    let key = (dfs as u32, self.actor_virtual_selected_slot_idx);
                                    if let Some((path, &merged_start, _)) =
                                        mc_outline_map.get(&key).map(|(p, s, n)| (p, s, n))
                                    {
                                        if let (Some(&gpu), Some(sd)) = (
                                            gpu_model_by_path.get(path.as_str()),
                                            self.shared_model_batches.get(path),
                                        ) {
                                            let merged_selected: Vec<u32> =
                                                self.selected_instances.iter()
                                                    .map(|&inst_i| merged_start + inst_i)
                                                    .collect();
                                            draw_stencil_mask_multi(
                                                &mut pass, gpu, &sd.batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                                &merged_selected,
                                            );
                                            draw_outline_multi(
                                                &mut pass, gpu, &sd.batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                                &merged_selected,
                                            );
                                        }
                                    }
                                }
                            }
                        }

                        // カメラフラスタムライン（選択中カメラアクターのみ、3D シーン）
                        if !scene_canvas_ss {
                            if let (Some(frustum), Some((_, line_bg))) =
                                (&frustum_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut pass, frustum,
                                    &camera_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }
                        }

                        // コライダーワイヤーフレーム（エディタモード + 3D シーン）
                        // scene_canvas_ss=true（3D + スクリーンスペース 2D の合成）でも
                        // 3D コライダーは 3D カメラパスで描画するためガードしない
                        if let (Some(coll_batch), Some((_, line_bg))) =
                            (&collider_wireframe_batch, &self.line_model_buf)
                        {
                            draw_line_batch(
                                &mut pass, coll_batch,
                                &camera_buf.bind_group, line_bg,
                                &draw_ctx.pipelines,
                            );
                        }

                        // 2D コライダーワイヤーフレーム（アクター編集 2D タブ + ワールドスペースキャンバス）
                        // scene_canvas_ss の場合はオーバーレイパスで描画するためスキップする
                        if !scene_canvas_ss {
                            if let (Some(coll2d_batch), Some((_, line_bg))) =
                                (&collider_2d_wireframe_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut pass, coll2d_batch,
                                    &camera_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }
                        }

                        // カメラアイコンモデル（全カメラアクター、3D シーン）
                        // camera.glb を InstancedModelBatch で描画する
                        if !scene_canvas_ss && !cam_gizmo_actor_mats.is_empty() {
                            if let Some(gizmo) = &self.camera_gizmo {
                                draw_model_indirect(
                                    &mut pass, &gizmo.gpu_model, &gizmo.batch,
                                    &camera_buf.bind_group, &draw_ctx.pipelines,
                                );
                            }
                        }

                        // ギズモ（グリッド・アウトラインより前面、アイコンより背面）
                        // 3D アクター選択中はメインパスで描画（scene_canvas_ss でもスキップしない）。
                        // 2D アクター選択中 + scene_canvas_ss の場合のみオーバーレイパスへ移動。
                        if !scene_canvas_ss || !gizmo_actor_is_2d {
                            let show_gizmo = in_editor && self.tool_mode != ToolMode::Select;
                            if show_gizmo {
                                if let (Some(gpu_batch), Some((_, line_bg))) =
                                    (&gizmo_gpu_batch, &self.line_model_buf)
                                {
                                    draw_gizmo_batch(
                                        &mut pass, gpu_batch,
                                        &camera_buf.bind_group, line_bg,
                                        &draw_ctx.pipelines,
                                    );
                                }
                            }
                        }

                        // 軸ギズモ（エディタモードのみ）
                        // scene_canvas_ss 時はオーバーレイパスの末尾で最前面描画するためスキップ
                        if !scene_canvas_ss {
                            if let (Some(batch), Some(ag)) =
                                (&axis_gizmo_batch, &self.axis_gizmo)
                            {
                                ag.draw(batch, &mut pass);
                            }
                        }

                        // アイコンオーバーレイ（最前面：選択アクター位置マーカー）
                        if let (Some(batch), Some(io)) =
                            (&icon_overlay_batch, &self.icon_overlay)
                        {
                            io.draw(batch, &mut pass);
                        }

                        // MMB スティック HUD（最前面・スクリーンスペース）
                        // passより前で構築済みの mmb_stick_gpu を描画する
                        if let (Some(stick), Some(mmb_cam), Some((_, line_bg))) =
                            (&mmb_stick_gpu, &self.mmb_hud_cam_buf, &self.line_model_buf)
                        {
                            draw_line_batch(
                                &mut pass, stick,
                                &mmb_cam.bind_group, line_bg,
                                &draw_ctx.pipelines,
                            );
                        }

                        // pass.drop() の時間を明示計測する。
                        // wgpu デバッグモードでは drop() 時に全コマンドの検証が走るため、
                        // 多数のアクターがある場合にここが大きなボトルネックになりうる。
                        let _perf_t_drop = std::time::Instant::now();
                        drop(pass);
                        perf_pass_drop_ms = _perf_t_drop.elapsed().as_secs_f64() * 1000.0;
                    }

                    perf_main_pass_ms = _perf_t_main.elapsed().as_secs_f64() * 1000.0;
                    // ── シーンキャンバスオーバーレイパス（シーンSS専用）──────────────
                    // 3D シーンのカラーを保持しつつ、2D キャンバス要素を最前面に合成する。
                    // アクター編集タブは camera_buf が 2D なのでメインパスで済む。
                    if scene_canvas_ss {
                        if let Some(canvas_cam_buf) = self.canvas_overlay_camera_buf.as_ref() {
                            let mut overlay_pass = frame.begin_canvas_overlay_pass();

                            // 2D キャンバススプライト（アウトラインより前に描画してアウトラインを前面に）
                            // 3D Canvas スプライトはメインパスで 3D カメラ描画済みのためここでは不要。
                            if !sprite_prepared_2d.is_empty() {
                                draw_sprites(
                                    &mut overlay_pass,
                                    &draw_ctx.pipelines.sprite,
                                    &canvas_cam_buf.bind_group,
                                    &sprite_prepared_2d,
                                );
                            }

                            // CanvasComponent 矩形アウトライン
                            if let (Some(rect_batch), Some((_, line_bg))) =
                                (&canvas_rect_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut overlay_pass, rect_batch,
                                    &canvas_cam_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }

                            // 2D コライダーワイヤーフレーム（シーン SS オーバーレイパス）
                            if let (Some(coll2d_batch), Some((_, line_bg))) =
                                (&collider_2d_wireframe_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut overlay_pass, coll2d_batch,
                                    &canvas_cam_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }

                            // 矩形選択ビジュアル
                            if let (Some(rect_batch), Some((_, line_bg))) =
                                (&rect_gpu_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut overlay_pass, rect_batch,
                                    &canvas_cam_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }

                            // ギズモ（スプライト・矩形より前面）
                            // 2D アクター選択中のみ 2D オルソカメラで描画する。
                            // 3D アクター選択中はメインパスで描画済みのためスキップ。
                            let show_gizmo = in_editor && self.tool_mode != ToolMode::Select && gizmo_actor_is_2d;
                            if show_gizmo {
                                if let (Some(gpu_batch), Some((_, line_bg))) =
                                    (&gizmo_gpu_batch, &self.line_model_buf)
                                {
                                    draw_gizmo_batch(
                                        &mut overlay_pass, gpu_batch,
                                        &canvas_cam_buf.bind_group, line_bg,
                                        &draw_ctx.pipelines,
                                    );
                                }
                            }

                            // 軸ギズモ（オーバーレイ最前面）
                            // scene_canvas_ss 時にメインパスから移動し、常に最前面に表示する
                            if let (Some(batch), Some(ag)) =
                                (&axis_gizmo_batch, &self.axis_gizmo)
                            {
                                ag.draw(batch, &mut overlay_pass);
                            }

                        }
                    }

                    // ── カメラプレビューブリット（選択カメラがある場合のみ）──────
                    // メインパスの後に、オフスクリーンプレビューをビューポート右下に貼り付ける。
                    if selected_cam_data.is_some() {
                        if let Some(ref preview) = self.camera_preview {
                            let mut blit_pass = frame.begin_blit_pass();
                            blit_pass.set_pipeline(&draw_ctx.pipelines.camera_preview_blit.pipeline);
                            blit_pass.set_bind_group(0, &preview.blit_rect_bg, &[]);
                            blit_pass.set_bind_group(1, &preview.blit_tex_bg, &[]);
                            blit_pass.draw(0..6, 0..1);
                        }
                    }

                    // ── ID パス（Edit/Pause のみ）──────────
                    if in_editor {
                        if let Some(id_buf) = &self.id_buffer {
                            let _perf_t_id = std::time::Instant::now();
                            {
                                // BindGroup は RenderPass より長く生きる必要があるので先に生成する

                                // 3D MC ID バインドグループ:
                                // 統合バッチを使うため per-MC の id_base_bgs は不要。
                                // 各統合バッチの lod_id_buffers には絶対 ID が書き込まれており、
                                // id_zero_bg (base=0) との組み合わせで正しい ID が出力される。

                                // カメラギズモ ID バインドグループ（RenderPass より先に生成してライフタイムを確保）
                                let cam_gizmo_id_base_opt: Option<(wgpu::Buffer, wgpu::BindGroup)> =
                                    if !cam_gizmo_actor_mats.is_empty() && self.camera_gizmo.is_some() {
                                        Some(draw_ctx.create_id_base_bg(mc_total_instances))
                                    } else { None };

                                // キャンバスアクター ID アイテム収集
                                // scene canvas モードのみ実行（actor edit 2D タブは CPU picking 専用）
                                // DFS カウンタは find_actor_by_dfs と同じ規則で全アクターを数える。
                                let canvas_id_is_ss = scene_canvas_ss;
                                let canvas_id_raw_items: Vec<(u32, [[f32; 4]; 4], Option<String>)> =
                                    if is_canvas && !is_actor_edit_2d {
                                        if let Some(scene) = &self.scene {
                                            let wl = self.active_world_line;
                                            let canvas_scale = if use_screen_space { 1.0f32 } else { CANVAS_WORLD_SCALE };
                                            let y_sign = if use_screen_space { 1.0f32 } else { -1.0 };
                                            let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                                            let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                                            let viewport_size: Option<[f32; 2]> =
                                                if scene_canvas_ss { Some([vp_w, vp_h]) } else { None };
                                            // Camera 参照のルートキャンバス用ビューポートオーバーライドマップ
                                            let play_gvp_id = if scene_canvas_ss && !in_editor { Some(game_viewport) } else { None };
                                            let canvas_vp_overrides_id = if scene_canvas_ss {
                                                build_canvas_viewport_map(&scene.actors, &scene.world, wl, vp_w, vp_h, play_gvp_id)
                                            } else {
                                                std::collections::HashMap::new()
                                            };

                                            let mut items = Vec::new();
                                            let mut ctr   = 0u32;
                                            const IDENTITY: [[f32; 4]; 4] = [
                                                [1.0, 0.0, 0.0, 0.0],
                                                [0.0, 1.0, 0.0, 0.0],
                                                [0.0, 0.0, 1.0, 0.0],
                                                [0.0, 0.0, 0.0, 1.0],
                                            ];
                                            collect_canvas_id_items(
                                                &scene.actors, &scene.world, wl,
                                                &mut ctr, None, IDENTITY,
                                                [1.0, 1.0], (false, false, false, true),
                                                canvas_scale, y_sign, viewport_size,
                                                &canvas_vp_overrides_id,
                                                canvas_id_offset, &mut items,
                                            );
                                            items
                                        } else { vec![] }
                                    } else { vec![] };

                                // 3D Canvas 子スプライト ID アイテム収集
                                // Actor3D + CanvasComponent を持つアクターの 2D 子スプライトを WS で pick できるようにする。
                                // is_canvas に関わらず常に収集する（3D シーン中の 3D Canvas 対応）。
                                // actor edit 2D タブは CPU picking 専用のため除外する。
                                let canvas_3d_child_id_raw_items: Vec<(u32, [[f32; 4]; 4], Option<String>)> =
                                    if !is_actor_edit_2d {
                                        if let Some(scene) = &self.scene {
                                            let wl = self.active_world_line;
                                            let mut items = Vec::new();
                                            let mut ctr   = 0u32;
                                            collect_3d_canvas_child_id_items(
                                                &scene.actors, &scene.world, wl,
                                                &mut ctr, canvas_id_offset, &mut items,
                                            );
                                            items
                                        } else { vec![] }
                                    } else { vec![] };

                                // キャンバス ID GPU バインドグループ（render pass より長く生きる）
                                let canvas_id_bgs: Vec<(wgpu::Buffer, wgpu::BindGroup)> =
                                    canvas_id_raw_items.iter()
                                        .map(|&(raw_id, gpu_mat, _)| {
                                            prepare_canvas_id_bg(
                                                &draw_ctx.device, &draw_ctx.pipelines,
                                                gpu_mat, raw_id,
                                            )
                                        })
                                        .collect();

                                // 3D Canvas 子スプライト ID GPU バインドグループ（WS）
                                let canvas_3d_child_id_bgs: Vec<(wgpu::Buffer, wgpu::BindGroup)> =
                                    canvas_3d_child_id_raw_items.iter()
                                        .map(|&(raw_id, gpu_mat, _)| {
                                            prepare_canvas_id_bg(
                                                &draw_ctx.device, &draw_ctx.pipelines,
                                                gpu_mat, raw_id,
                                            )
                                        })
                                        .collect();

                                // スプライトテクスチャ Arc を保持してライフタイムを確保する
                                // （render pass 中に参照するため drop されないようにする）
                                let canvas_sprite_arcs: Vec<Option<std::sync::Arc<GpuSpriteTexture>>> = {
                                    let cache = draw_ctx.sprite_tex_cache.borrow();
                                    canvas_id_raw_items.iter()
                                        .map(|(_, _, path_opt): &(u32, [[f32;4];4], Option<String>)| {
                                            // path_opt は常に Some（テクスチャありのみ out に追加される）
                                            path_opt.as_deref().and_then(|path| {
                                                cache.get(path).and_then(|opt| opt.clone())
                                            })
                                        })
                                        .collect()
                                };

                                // 3D Canvas 子スプライトテクスチャ Arc
                                let canvas_3d_child_sprite_arcs: Vec<Option<std::sync::Arc<GpuSpriteTexture>>> = {
                                    let cache = draw_ctx.sprite_tex_cache.borrow();
                                    canvas_3d_child_id_raw_items.iter()
                                        .map(|(_, _, path_opt): &(u32, [[f32;4];4], Option<String>)| {
                                            path_opt.as_deref().and_then(|path| {
                                                cache.get(path).and_then(|opt| opt.clone())
                                            })
                                        })
                                        .collect()
                                };

                                // テクスチャ BG をアイテムごとに生成する
                                // スプライトありは Arc から view を取得、なしは白テクスチャ（全面選択可能）
                                let canvas_id_tex_bgs: Vec<wgpu::BindGroup> =
                                    canvas_sprite_arcs.iter()
                                        .map(|arc_opt: &Option<std::sync::Arc<GpuSpriteTexture>>| {
                                            let view = arc_opt.as_ref()
                                                .map(|arc| &arc.view)
                                                .unwrap_or(&draw_ctx.pipelines.canvas_id.white_view);
                                            draw_ctx.device.create_bind_group(&wgpu::BindGroupDescriptor {
                                                label:   Some("CanvasId Tex BG"),
                                                layout:  &draw_ctx.pipelines.canvas_id.tex_bgl,
                                                entries: &[
                                                    wgpu::BindGroupEntry {
                                                        binding:  0,
                                                        resource: wgpu::BindingResource::TextureView(view),
                                                    },
                                                    wgpu::BindGroupEntry {
                                                        binding:  1,
                                                        resource: wgpu::BindingResource::Sampler(
                                                            &draw_ctx.pipelines.canvas_id.sampler
                                                        ),
                                                    },
                                                ],
                                            })
                                        })
                                        .collect();
                                let canvas_id_tex_bg_refs: Vec<&wgpu::BindGroup> =
                                    canvas_id_tex_bgs.iter().collect();

                                // 3D Canvas 子スプライト テクスチャ BG（WS 用）
                                let canvas_3d_child_id_tex_bgs: Vec<wgpu::BindGroup> =
                                    canvas_3d_child_sprite_arcs.iter()
                                        .map(|arc_opt: &Option<std::sync::Arc<GpuSpriteTexture>>| {
                                            let view = arc_opt.as_ref()
                                                .map(|arc| &arc.view)
                                                .unwrap_or(&draw_ctx.pipelines.canvas_id.white_view);
                                            draw_ctx.device.create_bind_group(&wgpu::BindGroupDescriptor {
                                                label:   Some("Canvas3dChildId Tex BG"),
                                                layout:  &draw_ctx.pipelines.canvas_id.tex_bgl,
                                                entries: &[
                                                    wgpu::BindGroupEntry {
                                                        binding:  0,
                                                        resource: wgpu::BindingResource::TextureView(view),
                                                    },
                                                    wgpu::BindGroupEntry {
                                                        binding:  1,
                                                        resource: wgpu::BindingResource::Sampler(
                                                            &draw_ctx.pipelines.canvas_id.sampler
                                                        ),
                                                    },
                                                ],
                                            })
                                        })
                                        .collect();
                                let canvas_3d_child_id_tex_bg_refs: Vec<&wgpu::BindGroup> =
                                    canvas_3d_child_id_tex_bgs.iter().collect();

                                let mut id_pass = frame.begin_id_pass(&id_buf.view);

                                // 3D MC ID 描画（統合バッチ使用）
                                // lod_id_buffers に絶対 ID が書き込まれているため
                                // id_zero_bg (base=0) で CPU デコードが正しく機能する
                                for (path, sd) in &self.shared_model_batches {
                                    if let Some(&gpu) = gpu_model_by_path.get(path.as_str()) {
                                        draw_id_pass(
                                            &mut id_pass, gpu, &sd.batch,
                                            &camera_buf.bind_group, &draw_ctx.pipelines,
                                            &sd.id_zero_bg.1,
                                        );
                                    }
                                }

                                // カメラギズモ ID 描画
                                // base = mc_total_instances で全インスタンスを一括描画する。
                                // インスタンス local_idx → cam_gizmo_actor_mats[local_idx].0 = dfs_id
                                if let (Some(gizmo), Some((_, cam_id_bg))) =
                                    (&self.camera_gizmo, &cam_gizmo_id_base_opt)
                                {
                                    draw_id_pass(
                                        &mut id_pass,
                                        &gizmo.gpu_model, &gizmo.batch,
                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                        cam_id_bg,
                                    );
                                }

                                // 3D Canvas 子スプライト ID 描画（WS / perspective camera）
                                // 3D MC・カメラギズモの後で描画し最前面に上書きする。
                                // canvas_id_offset 以上の ID 範囲を使用するため既存の decode ロジックと互換。
                                if !canvas_3d_child_id_bgs.is_empty() {
                                    draw_canvas_id_items(
                                        &mut id_pass, &draw_ctx.pipelines,
                                        &camera_buf.bind_group, None,
                                        &canvas_3d_child_id_bgs,
                                        &canvas_3d_child_id_tex_bg_refs,
                                        &[], &[],
                                    );
                                }

                                // キャンバスアクター ID 描画（3D MC より後で常に上書き）
                                // WS: perspective camera、SS: 2D ortho カメラを使用する
                                let ss_camera_bg: Option<&wgpu::BindGroup> = if canvas_id_is_ss {
                                    self.canvas_overlay_camera_buf.as_ref().map(|b| &b.bind_group)
                                } else { None };
                                if canvas_id_is_ss {
                                    draw_canvas_id_items(
                                        &mut id_pass, &draw_ctx.pipelines,
                                        &camera_buf.bind_group, ss_camera_bg,
                                        &[], &[],
                                        &canvas_id_bgs, &canvas_id_tex_bg_refs,
                                    );
                                } else {
                                    draw_canvas_id_items(
                                        &mut id_pass, &draw_ctx.pipelines,
                                        &camera_buf.bind_group, None,
                                        &canvas_id_bgs, &canvas_id_tex_bg_refs,
                                        &[], &[],
                                    );
                                }
                            }
                            perf_id_ms = _perf_t_id.elapsed().as_secs_f64() * 1000.0;
                            // readback 優先度: drop > add_actor > pick
                            let drop_pos = self.pending_drop
                                .as_ref()
                                .map(|&(_, sx, sy)| (sx, sy));
                            let add_actor_pos = self.pending_add_actor
                                .as_ref()
                                .and_then(|&(is_2d, _, _, sx, sy)| {
                                    // 2D アクターはスポーン位置不要なので readback しない
                                    if is_2d { None } else { Some((sx, sy)) }
                                });
                            let readback_pos = drop_pos.or(add_actor_pos).or(pick_pos);
                            if let Some((px, py)) = readback_pos {
                                let px = px.min(id_buf.width.saturating_sub(1));
                                let py = py.min(id_buf.height.saturating_sub(1));
                                frame.schedule_id_copy(
                                    &id_buf.texture, px, py, &id_buf.readback_buf,
                                );
                                // readback が drop/add_actor ではなく pick のためかを記録するフラグ
                                did_pick = drop_pos.is_none() && add_actor_pos.is_none() && pick_pos.is_some();
                            }
                        }
                    }

                    let _perf_t_finish = std::time::Instant::now();
                    frame.finish();
                    perf_finish_ms = _perf_t_finish.elapsed().as_secs_f64() * 1000.0;

                    // ── パフォーマンスログ ─────────────────────────────────────────
                    // 60 フレームごとに CPU タイミングを eprintln! で出力する。
                    // total:       フレーム全体（begin_frame GPU 待機 + 記録 + submit）
                    // begin_frame: get_current_texture 待機（GPU バックプレッシャー指標）
                    // ipc:         process_ipc の時間
                    // batch_upd:   MC バッチ更新（view frustum カリング + write_buffer）
                    // skin_cmds:   スキンコンピュートコマンド記録
                    // draw:        draw_model_indirect コマンド記録（メインパス）
                    // id_pass:     ID パスコマンド記録（Edit モードのみ）
                    // grid:        グリッドGPU バッチ生成
                    // finish:      encoder.finish + queue.submit + surface.present
                    // other = total - 上記全て（残りは未計測のコライダー・ギズモ等）
                    if do_perf {
                        let total_ms = perf_t_total.elapsed().as_secs_f64() * 1000.0;
                        // main_pass は draw を内包するので draw を除いた残り = main_pass - draw = 他の描画コマンド記録
                        let main_rest_ms = (perf_main_pass_ms - perf_draw_ms).max(0.0);
                        let other_ms = (total_ms
                            - perf_begin_frame_ms - perf_ipc_ms - perf_batch_ms
                            - perf_skin_ms - perf_main_pass_ms - perf_id_ms
                            - perf_grid_ms - perf_collider_ms - perf_finish_ms).max(0.0);
                        eprintln!(
                            "[PERF f={perf_idx}] MC={perf_mc_count} skin_MC={perf_skin_mc_count} dispatches={perf_skin_dispatches} \
                             | total={total_ms:.3}ms bf={perf_begin_frame_ms:.3}ms ipc={perf_ipc_ms:.3}ms \
                             batch={perf_batch_ms:.3}ms skin={perf_skin_ms:.3}ms \
                             main_pass={perf_main_pass_ms:.3}ms(draw={perf_draw_ms:.3}ms+pass_drop={perf_pass_drop_ms:.3}ms+rest={main_rest_ms:.3}ms) \
                             id={perf_id_ms:.3}ms grid={perf_grid_ms:.3}ms collider={perf_collider_ms:.3}ms \
                             finish={perf_finish_ms:.3}ms other={other_ms:.3}ms"
                        );
                    }

                    // FPS 計測: frame.finish() 後に壁時計でカウント
                    // delta_time ではなく present 完了回数/秒 で計測することで
                    // non-blocking な GPU submit でも正確な fps が得られる。
                    {
                        const FPS_WINDOW_SECS: f32 = 0.5;
                        self.fps_frame_count += 1;
                        let elapsed = self.fps_frame_start.elapsed().as_secs_f32();
                        if elapsed >= FPS_WINDOW_SECS {
                            self.fps_display = self.fps_frame_count as f32 / elapsed;
                            self.fps_frame_count = 0;
                            self.fps_frame_start = std::time::Instant::now();
                            if let Some(ipc) = &self.ipc {
                                ipc.send(&format!("FPS:{:.1}", self.fps_display));
                            }
                        }
                    }

                    // 実際にポリゴンが描画された最初のフレームでエディタへ通知する
                    // （デバッグビルド・埋め込みモード限定）。
                    #[cfg(debug_assertions)]
                    if !self.first_frame_sent && self.parent_hwnd.is_some() {
                        self.first_frame_sent = true;
                        if let Some(ipc) = &self.ipc {
                            ipc.send("FIRST_FRAME");
                        }
                    }
                }
                // Lost: GPU がサーフェスを解放した（フルスクリーン切替など）
                // Outdated: 親ウィンドウ変更・リサイズでサーフェスが無効化された
                // どちらも surface.configure() をやり直せば回復する。
                //
                // C# の EmbedRuntimeWindow は SetParent を最初に呼ぶ。
                // このため Outdated が発生する時点では必ず SetParent 済みであり、
                // GetParent(my_hwnd) は正確な埋め込みコンテナを返す。
                // GetClientRect(parent) = Vulkan の currentExtent と一致するサイズ
                // で resize することで depth/color attachment の不一致を防ぐ。
                //
                // my_hwnd を事前コピーしているのは &mut self.renderer を借用した
                // ブロック内で self メソッドを呼べない borrow checker の制約のため。
                Err(wgpu::SurfaceError::Lost | wgpu::SurfaceError::Outdated) => {
                    #[cfg(target_os = "windows")]
                    let resize_to = {
                        use windows_sys::Win32::Foundation::RECT;
                        use windows_sys::Win32::UI::WindowsAndMessaging::{
                            GetClientRect, GetParent,
                        };
                        let parent = unsafe { GetParent(my_hwnd as _) };
                        let mut sz = None;
                        if !parent.is_null() {
                            let mut r = RECT { left: 0, top: 0, right: 0, bottom: 0 };
                            if unsafe { GetClientRect(parent, &mut r) } != 0 {
                                let w = (r.right  - r.left) as u32;
                                let h = (r.bottom - r.top)  as u32;
                                if w > 0 && h > 0 {
                                    sz = Some(winit::dpi::PhysicalSize { width: w, height: h });
                                }
                            }
                        }
                        sz.or(window_size)
                    };
                    #[cfg(not(target_os = "windows"))]
                    let resize_to = window_size;
                    // 0x0 は最小化中に発生するケース。サイズ変更不要なのでスキップ。
                    let is_zero = resize_to.map_or(true, |s| s.width == 0 || s.height == 0);
                    if let Some(size) = resize_to {
                        if !is_zero {
                            renderer.resize(size);
                        }
                    }
                }
                Err(wgpu::SurfaceError::OutOfMemory) => event_loop.exit(),
                Err(e) => eprintln!("Render error: {e:?}"),
            }
        }

        // ── ピック結果の読み出し（GPU サブミット後）─────
        if did_pick {
            if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
                let (_world_pos, raw) = id_buf.read_pixel(&draw_ctx.device);
                // 選択変更前の状態を保存する（Undo 記録用）
                let before_inst       = self.selected_instances.clone();
                let before_dfs_ids    = self.selected_actor_dfs_ids.clone();
                let before_primary    = self.actor_virtual_selected_idx;

                if raw == 0 {
                    // 空クリック: 選択解除（Ctrl 押下時は何もしない）
                    if !self.drag.ctrl_at_press {
                        self.actor_virtual_selected_idx      = None;
                        self.actor_virtual_selected_slot_idx = 0;
                        self.selected_actor_dfs_ids.clear();
                        self.selected_instances.clear();
                    }
                } else {
                    let global = raw - 1; // 0 始まりグローバル ID
                    // 3D MC アクター判定（raw_id = base + local + 1 のいずれかの MC 範囲に入るか）
                    let mc_hit = wl_mc_pick_infos.iter()
                        .find(|&&(base, _, _, count)| global >= base && (global - base) < count as u32);

                    if let Some(&(base, dfs_id, slot_i, _)) = mc_hit {
                        // 3D MC アクター選択
                        let dfs_usize = dfs_id as usize;
                        let local_idx = global - base;
                        self.actor_virtual_selected_slot_idx = slot_i;
                        if self.drag.ctrl_at_press {
                            // Ctrl+クリック: アクターをマルチ選択リストでトグルする
                            if self.selected_actor_dfs_ids.contains(&dfs_usize) {
                                self.selected_actor_dfs_ids.retain(|&x| x != dfs_usize);
                                if self.actor_virtual_selected_idx == Some(dfs_usize) {
                                    self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
                                }
                            } else {
                                self.selected_actor_dfs_ids.push(dfs_usize);
                                self.actor_virtual_selected_idx = Some(dfs_usize);
                            }
                            self.selected_instances = vec![local_idx];
                        } else {
                            // 通常クリック: 単一選択
                            self.actor_virtual_selected_idx = Some(dfs_usize);
                            self.selected_actor_dfs_ids     = vec![dfs_usize];
                            self.selected_instances          = vec![local_idx];
                        }
                        self.send_actor_components(dfs_id, slot_i);
                    } else if global >= mc_total_instances
                        && global < mc_total_instances + camera_gizmo_count
                        && !cam_gizmo_actor_mats.is_empty()
                    {
                        // カメラギズモアイコン選択
                        // global - mc_total_instances = カメラギズモのローカルインスタンスインデックス
                        let cam_local_idx = (global - mc_total_instances) as usize;
                        if let Some(&(dfs_id, _)) = cam_gizmo_actor_mats.get(cam_local_idx) {
                            self.actor_virtual_selected_slot_idx = 0;
                            if self.drag.ctrl_at_press {
                                if self.selected_actor_dfs_ids.contains(&dfs_id) {
                                    self.selected_actor_dfs_ids.retain(|&x| x != dfs_id);
                                    if self.actor_virtual_selected_idx == Some(dfs_id) {
                                        self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
                                    }
                                } else {
                                    self.selected_actor_dfs_ids.push(dfs_id);
                                    self.actor_virtual_selected_idx = Some(dfs_id);
                                }
                            } else {
                                self.actor_virtual_selected_idx = Some(dfs_id);
                                self.selected_actor_dfs_ids     = vec![dfs_id];
                            }
                            self.selected_instances.clear();
                            self.send_actor_components(dfs_id as u32, 0);
                        }
                    } else if global >= canvas_id_offset {
                        // キャンバスアクター選択
                        // raw_id = canvas_id_offset + dfs_id + 1 → canvas_dfs_id = global - canvas_id_offset
                        let canvas_dfs_id  = global - canvas_id_offset;
                        let dfs_usize      = canvas_dfs_id as usize;
                        self.actor_virtual_selected_slot_idx = 0;
                        if self.drag.ctrl_at_press {
                            // Ctrl+クリック: マルチ選択トグル
                            if self.selected_actor_dfs_ids.contains(&dfs_usize) {
                                self.selected_actor_dfs_ids.retain(|&x| x != dfs_usize);
                                if self.actor_virtual_selected_idx == Some(dfs_usize) {
                                    self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
                                }
                            } else {
                                self.selected_actor_dfs_ids.push(dfs_usize);
                                self.actor_virtual_selected_idx = Some(dfs_usize);
                            }
                        } else {
                            // 通常クリック: 単一選択
                            self.actor_virtual_selected_idx = Some(dfs_usize);
                            self.selected_actor_dfs_ids     = vec![dfs_usize];
                        }
                        self.selected_instances.clear();
                        self.send_actor_components(canvas_dfs_id, 0);
                    }
                }

                // MC インスタンス選択の Undo 記録
                let after_inst = self.selected_instances.clone();
                if before_inst != after_inst {
                    self.undo_history.record(Box::new(SelectionCommand {
                        before: before_inst,
                        after:  after_inst,
                    }));
                }
                // アクター DFS 選択の Undo 記録
                let after_dfs_ids = self.selected_actor_dfs_ids.clone();
                let after_primary = self.actor_virtual_selected_idx;
                if before_dfs_ids != after_dfs_ids || before_primary != after_primary {
                    self.undo_history.record(Box::new(ActorDfsSelectionCommand {
                        before_dfs_ids,
                        after_dfs_ids,
                        before_primary,
                        after_primary,
                    }));
                }
                self.send_selected();
            }
        }

        // ── ドロップ処理（GPU サブミット後）─────────────
        if let Some((path, sx, sy)) = self.pending_drop.take() {
            match self.resolve_spawn_pos(sx, sy, did_pick) {
                // ピック処理でバッファ読み出し済みのため次フレームで再試行する
                None => self.pending_drop = Some((path, sx, sy)),
                Some(spawn_pos) => self.handle_drop_actor(&path, spawn_pos),
            }
        }

        // ── コンテキストメニュー経由のアクター追加（GPU サブミット後）─────────────
        // D&D と同じ IDバッファ読み取りでスポーン位置を確定する
        if let Some((is_2d, world_line, parent_dfs_id, sx, sy)) = self.pending_add_actor.take() {
            if is_2d {
                // 2D アクターはスポーン位置不要なので即座に追加する
                self.handle_add_actor_2d(world_line, parent_dfs_id);
            } else {
                match self.resolve_spawn_pos(sx, sy, did_pick) {
                    // 再キューイング
                    None => self.pending_add_actor = Some((false, world_line, parent_dfs_id, sx, sy)),
                    Some(spawn_pos) => self.handle_add_actor(world_line, parent_dfs_id, Some(spawn_pos)),
                }
            }
        }

        // ─ 7. EndFrame（Play 時のみ）─────────────────
        if time_running {
            if let Some(scene) = &mut self.scene { scene.end_frame(&ctx); }
        }

        self.input.end_frame();
        self.cam_input.end_frame();

        // ── Play モード初回フレーム末尾で物理スレッドを起動する ─────────────────
        // フレーム先頭（update_physics/update_physics_2d）で起動すると、
        // wgpu の初回シェーダーコンパイル・テクスチャアップロード等（~1 秒程度）の間に
        // 物理スレッドが 60Hz で走り続け、1 秒分の先行が生じる。
        // フレーム末尾（GPU present 後）に起動することで、次フレームまでの ~16ms しか
        // 物理が進まないため、初期位置のズレが発生しない。
        if self.mode == RuntimeMode::Play && !self.paused {
            if self.physics_thread.is_none() {
                eprintln!("[PHYS3D] 初回フレーム末に 3D 物理スレッドを起動");
                self.start_physics();
            }
            if self.physics_thread_2d.is_none() {
                eprintln!("[PHYS2D] 初回フレーム末に 2D 物理スレッドを起動");
                self.start_physics_2d();
            }
        }

        if dbg { eprintln!("[SEED FRAME {dbg_frame}] end"); }
        if let Some(window) = &self.window { window.request_redraw(); }
    }
}
