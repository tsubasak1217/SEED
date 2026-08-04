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

/// 統合バッチ／BLAS キャッシュの遅延 prune のしきい値（フレーム数）。
/// batch_key がこの数だけ連続で「そのフレームの描画対象」に不在なら、
/// 対応する `shared_model_batches` エントリと RT の BLAS キャッシュを解放する。
/// 即時解放にしないのは、非同期ロード中に gpu_model が一瞬 None になる等で batch_key が
/// 一時的に消えたフレームでの誤解放→再ロードのスラッシングを避けるため。60fps で約 1 秒。
/// マテリアルのインライン編集を止めれば、この猶予の後に stale バッチがまとめて解放され
/// VRAM が定常状態へ戻る。
const STALE_BATCH_PRUNE_FRAMES: u32 = 60;

/// パフォーマンスログ用フレームカウンター。
/// PERF_LOG_INTERVAL フレームごとに各処理の CPU 消費時間と MC/スキン数をログ出力する。
static PERF_FRAME: AtomicU64 = AtomicU64::new(0);
/// パフォーマンスログを出力する間隔（フレーム数）。
const PERF_LOG_INTERVAL: u64 = 60;
/// パフォーマンスログ（[PERF] 行）を出力するかどうか。
/// 既定では無効。プロファイルしたいときのみ環境変数 SEED_PERF_LOG を設定して有効化する。
/// （常時出力するとエディタログが [PERF] 行で埋まり、スクリプト等の重要ログが埋没するため）
static PERF_LOG_ENABLED: std::sync::LazyLock<bool> =
    std::sync::LazyLock::new(|| std::env::var_os("SEED_PERF_LOG").is_some());

// 埋め込み Play の凍結/黒画面 診断計器は play_diag モジュールへ集約（一時・原因確定後に撤去）。
use super::play_diag::{
    self, FrameStage, PLAY_DIAG_ENABLED, PLAY_DIAG_FRAME, PLAY_DIAG_FRAMES,
    PLAY_HB_EVERY, PLAY_HB_GAP_MS, PLAY_HB_LAST_MS, WD_EPOCH,
    ensure_frame_watchdog, mark_frame_stage,
};
use crate::engine::components::{ColliderComponent, ColliderShapeData, ComponentKind};
use crate::engine::components::{Collider2dComponent, CanvasTransform};
use crate::engine::components::Transform as ActorTransform;
use crate::engine::structs::transforms::Quaternion;
use crate::engine::structs::objects::actor::Actor;
use crate::engine::core::app_base::ipc::ToolMode;
use crate::engine::core::app_base::scene::CanvasCameraData;
use crate::engine::core::clock::FrameContext;
use crate::engine::components::CanvasDrawZone;
use crate::engine::methods::drawer::{
    CameraBuffer, CameraUniform,
    draw_model_indirect, draw_id_pass, draw_canvas_id_items, draw_collider_pick_items, prepare_canvas_id_bg,
    draw_outline_multi, draw_stencil_mask_multi,
    extract_frustum_planes, GizmoBatch, draw_gizmo_batch,
    LineBatch, draw_line_batch, draw_thick_line_batch,
    draw_sprite_batches, draw_sprite_outline_batches, GpuSpriteTexture,
    // group 4（ライト＋シャドウ＋クラスタ複合 BG）を「どのカメラのパスか」で選ぶ型。
    // カメラ固有資源（CSM・クラスタ）の取り違えを防ぐため、BG は必ずこれ経由で取る。
    LightingPass,
    NUM_LODS,
};
use crate::engine::methods::gizmo_interact::{screen_to_ray, GIZMO_SCREEN_RADIUS_RATIO};
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
//  compute_stale_batch_prune — stale 統合バッチの遅延 prune 計算
// ============================================================

/// stale な統合バッチ（batch_key）の遅延 prune を計算する純関数（GPU 非依存・テスト可能）。
///
/// マテリアルのインライン編集は署名が変わるたびに新しい batch_key を生む。対応する
/// `shared_model_batches` エントリと RT の BLAS キャッシュには削除処理が無く、編集のたびに
/// ユニークキーの GPU リソースが無制限に蓄積して VRAM が枯渇していた（本修正の対象）。
/// 誤解放（非同期ロード等で一瞬だけ描画対象から消えたキーを即解放→再ロード）を避けるため、
/// 「`threshold` フレーム連続で不在」を検出してから解放する遅延方式にする。
///
/// - `cache_keys`: 現在キャッシュに存在する batch_key 集合（`shared_model_batches` のキー）。
/// - `alive`:      このフレームの描画対象に存在する batch_key 集合（`merge_map` のキー）。
/// - `absent`:     batch_key → 連続不在フレーム数（呼び出しをまたいで保持する状態マップ）。
/// - `threshold`:  この数だけ連続不在になったら解放対象にする（>= で判定）。
///
/// 返り値: 解放すべき batch_key 一覧。副作用として `absent` を更新する:
///   - alive なキーはカウンタをリセット（`absent` から除去）。
///   - 不在キーはカウント +1。`threshold` 到達で解放対象へ入れ、`absent` から除去。
///   - `cache_keys` に無い（既に外部で消えた）`absent` エントリも掃除し、状態マップの肥大も防ぐ。
fn compute_stale_batch_prune(
    cache_keys: &std::collections::HashSet<String>,
    alive:      &std::collections::HashSet<String>,
    absent:     &mut std::collections::HashMap<String, u32>,
    threshold:  u32,
) -> Vec<String> {
    let mut to_free = Vec::new();
    for key in cache_keys {
        if alive.contains(key) {
            // 今フレーム描画対象に復帰 → 不在カウンタをリセットする。
            absent.remove(key);
        } else {
            // 不在フレームを 1 加算し、しきい値到達で解放対象にする。
            let c = absent.entry(key.clone()).or_insert(0);
            *c += 1;
            if *c >= threshold {
                to_free.push(key.clone());
            }
        }
    }
    // 解放したキーと、既に cache から消えたキーの absent エントリを掃除する。
    for k in &to_free { absent.remove(k); }
    absent.retain(|k, _| cache_keys.contains(k));
    to_free
}

// ============================================================
//  terrain_world_bounds — 地形チャンクの実在範囲（Phase W1.5）
// ============================================================

/// 地形チャンクが実在するワールド AABB を求める。チャンクが 0 個なら `None`。
///
/// 岸波のショアフィールド（`engine::water::shore`）のカラム走査を早期棄却するために使う。
/// チャンクは常にワールド原点基準の固定格子（`ChunkCoord::world_origin`）に乗っており、
/// アクタの Transform でオフセット／スケールされることは無いので、
/// チャンク座標だけから範囲が確定する。
fn terrain_world_bounds(
    terrain: &super::terrain_ops::TerrainState,
) -> Option<crate::engine::water::ShoreTerrainBounds> {
    if terrain.chunks.is_empty() {
        return None;
    }
    let extent = terrain.settings.chunk_extent();
    let mut min = [f32::INFINITY; 3];
    let mut max = [f32::NEG_INFINITY; 3];
    for coord in terrain.chunks.keys() {
        let o = coord.world_origin(&terrain.settings);
        for axis in 0..3 {
            min[axis] = min[axis].min(o[axis]);
            max[axis] = max[axis].max(o[axis] + extent);
        }
    }
    Some(crate::engine::water::ShoreTerrainBounds {
        min_xz: [min[0], min[2]],
        max_xz: [max[0], max[2]],
        min_y:  min[1],
        max_y:  max[1],
    })
}

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
    compute_game_viewport, clamp_viewport_to_target, build_ss_layout_maps_free,
};

/// カメラプレビューのテクスチャ幅（ピクセル）。
const CAMERA_PREVIEW_W: u32 = 320;
/// カメラプレビューのテクスチャ高さ（ピクセル）。
const CAMERA_PREVIEW_H: u32 = 180;

/// 2D 編集オルソカメラのズーム下限（ortho_half_h の最小＝最大ズームイン）。
const CAM2D_ORTHO_HALF_H_MIN: f32 = 0.5;
/// 2D 編集オルソカメラのズーム上限（ortho_half_h の最大＝最大ズームアウト）。
/// 描画範囲（見える世界の広さ）の制限。従来 1000.0 を約 5 倍へ拡張。
const CAM2D_ORTHO_HALF_H_MAX: f32 = 5000.0;

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

    /// フォーカスが無い間はフレームレートを抑える。
    ///
    /// ゲームウィンドウが非アクティブ／遮蔽されると present_mode=Mailbox の present() が
    /// VSync を待たず即座に返るため、`ControlFlow::Poll` + `request_redraw` のループが
    /// 毎秒数千フレームで暴走する。毎フレーム `Debug.Log` するスクリプトでは、これが
    /// エディタの Output を溢れさせ極端に重くする原因になる。フォーカスが無い間だけ
    /// `UNFOCUSED_MAX_FPS` に制限してこの暴走を防ぐ（バックグラウンド描画なので実害はない）。
    /// フォーカス時は present／DWM の VSync に任せて何もしない。
    fn pace_frame_if_unfocused(&self, frame_start: std::time::Instant) {
        // Edit モード（エディタ埋め込みビューポート）は、フォーカスが外れても
        // 編集操作の滑らかさを保ちたいので制限しない。制限対象は Play／スタンドアロン
        // 実行のウィンドウのみ（フラッドが問題になるのはこちら）。
        if self.window_focused || self.mode == RuntimeMode::Edit { return; }
        /// 非フォーカス時のフレームレート上限。
        const UNFOCUSED_MAX_FPS: u64 = 30;
        let target  = std::time::Duration::from_micros(1_000_000 / UNFOCUSED_MAX_FPS);
        let elapsed = frame_start.elapsed();
        if elapsed < target {
            std::thread::sleep(target - elapsed);
        }
    }

    /// RedrawRequested イベント処理: 1 フレーム分のレンダリング全体を担う。
    ///
    /// render.rs の window_event から委譲される。
    pub(super) fn handle_redraw_requested(&mut self, event_loop: &ActiveEventLoop) {
        let dbg_frame = DEBUG_FRAME.fetch_add(1, Ordering::Relaxed);
        let dbg = dbg_frame < DEBUG_LOG_FRAMES;
        if dbg { eprintln!("[SEED FRAME {dbg_frame}] start  mode={:?}  paused={}", self.mode, self.paused); }

        // フレーム凍結ウォッチドッグを初回に 1 度だけ起動する。
        ensure_frame_watchdog();
        // 「ループが生きている」信号: フレーム開始を記録する。ここを毎回更新することで、
        // handle_redraw_requested の途中で凍結すると次フレームの開始が来ず、この値が止まる
        // →ウォッチドッグが「最後に通過したステージ」で凍結を検出する。早期 return 経路
        // （render_paused / 最小化）は request_redraw で次フレームが即来るため誤検出しない。
        play_diag::note_frame_alive();
        // フレームが回っていることを about_to_wait 側へ知らせる時刻印。
        // これが更新され続けている間は about_to_wait の IPC ポンプは不発になり、
        // 従来どおりフレーム内 process_ipc だけが IPC を処理する。
        self.last_frame_at = std::time::Instant::now();
        // ウォッチドッグ（別スレッド）が読むための状態フラグを公開する。
        play_diag::publish_frame_flags(self.window_focused, self.render_paused, self.window_hwnd());
        mark_frame_stage(FrameStage::FrameStart);

        // ── 入力: ゲームパッドをポンプ＋スクリプトフレームカウンタを進める ──
        // キーボード/マウスは winit イベント駆動だが、パッド（gilrs）はここで能動ポンプする。
        // フレーム先頭で行うことで、以降のゲームロジックが今フレームのパッド状態を読める。
        // フレームカウンタは InputMap のアクション状態キャッシュ（Start/End・条件エッジ）が
        // 「新しいフレームか」を判定するために使う（同一フレーム内の複数クエリを一貫させる）。
        self.input.update_gamepad();
        crate::engine::core::scripting::advance_script_frame();
        mark_frame_stage(FrameStage::GamepadDone);

        // レンダリング機能マトリクスの実効モードが変わっていれば [SEED FEATURES] を出す
        // （起動時・スタンドアロン時もここで拾う。IPC 切替は各ハンドラでも即ログ）。
        self.log_render_features_if_changed();

        // LOD 遷移で遅延退役した旧チャンク GPU リソースを解放する（移動時スパイク対策の後片付け）。
        //   必ず begin_frame（描画コマンド記録＝snatch read lock 取得）より前で呼ぶ。ここで
        //   完了済み旧リソースを drop → poll(Poll) で遅延破棄を確定することで、フレーム末尾の
        //   queue.submit() が遅延破棄を処理して snatch lock 再帰パニックする経路を踏まない。
        self.process_terrain_gpu_retire();

        // チャンク単位 地形 LOD: 前フレームのカメラ位置で各チャンクの目標 LOD を選び、
        // 変化ぶんだけ近い順に小分けで再メッシュする（遠いチャンクを低ポリ化して描画三角形を削減）。
        // 描画の借用が始まる前（フレーム先頭）で行うことで scene/draw_ctx の可変借用衝突を避ける。
        self.tick_terrain_lod();
        mark_frame_stage(FrameStage::TerrainLodDone);

        // 地形スモーク（SEED_TERRAIN_SMOKE=1）専用: 描画開始後に 1 度だけ掘るステップ。
        // スモークが無効なら即 return する自己ゲート付きフックなので、通常実行では無害。
        self.tick_terrain_smoke_deferred();
        // 同じくスモーク専用: 指定フレームで草のクローズアップ構図へカメラを寄せるステップ。
        // 環境変数 SEED_SMOKE_CLOSEUP_FRAME 未指定なら即 return する。
        self.tick_terrain_smoke_closeup();
        // 物理コリジョンスモーク（SEED_TERRAIN_PHYS_SMOKE=1）専用: 落下球の Y を毎フレーム監視。
        // 無効なら即 return する自己ゲート付きフック。
        self.tick_terrain_physics_smoke();

        // ── パフォーマンス計測変数 ─────────────────────────────────────────────
        // 60 フレームごとに各処理の CPU 消費時間を eprintln! でログ出力する。
        // GPU コマンド記録時間（CPU 側）を計測するため、実際の GPU 実行時間は含まない。
        // ただし total_ms と begin_frame_ms は GPU バックプレッシャー（get_current_texture 待機）も含む。
        let perf_idx = PERF_FRAME.fetch_add(1, Ordering::Relaxed);
        let do_perf  = *PERF_LOG_ENABLED && perf_idx % PERF_LOG_INTERVAL == 0;
        // フレーム全体の経過時間 [ms]（begin_frame の GPU 待機 + コマンド記録 + submit を含む）
        let perf_t_total = std::time::Instant::now();
        // begin_frame（get_current_texture）にかかった時間 [ms]（GPU バックプレッシャーの指標）
        let mut perf_begin_frame_ms: f64 = 0.0;
        // process_ipc にかかった時間 [ms]
        let mut perf_ipc_ms:        f64 = 0.0;
        // MCバッチ更新（視錐台カリング + write_buffer記録）にかかった CPU 時間 [ms]
        // Phase R7 以降は per-MC batch 更新は廃止（下記 merge バケットへ統合）のため常に 0。
        let mut perf_batch_ms:      f64 = 0.0;
        // 統合バッチ（shared_model_batches）の merge_map 構築＋全バッチ update() 記録に
        // かかった CPU 時間 [ms]。地形 768 チャンクではここが主要 CPU 支配項の一つ。
        // 静的地形チャンクの update スキップ（Fix B）の効果はこのバケットに現れる。
        let mut perf_merge_ms:      f64 = 0.0;
        // スキンコンピュートコマンド記録にかかった CPU 時間 [ms]
        let mut perf_skin_ms:       f64 = 0.0;
        // メインパスの draw_model_indirect コマンド記録にかかった CPU 時間 [ms]
        let mut perf_draw_ms:       f64 = 0.0;
        // メインチャンネルのスプライトバッチ数（= ドローコール数）と総インスタンス数（Phase R6）。
        // 汎用バッチングの効果（N 枚 → 数バッチ）を [PERF] で可視化する。
        let mut perf_sprite_draws:  usize = 0;
        let mut perf_sprite_insts:  usize = 0;
        // ID パスコマンド記録にかかった CPU 時間 [ms]
        let mut perf_id_ms:         f64 = 0.0;
        // グリッド GPU バッチ生成（CPU 線生成 + device.create_buffer_init）にかかった時間 [ms]
        let mut perf_grid_ms:       f64 = 0.0;
        // コライダーワイヤーフレームバッチ生成にかかった時間 [ms]
        let mut perf_collider_ms:   f64 = 0.0;
        // RT 影の TLAS/BLAS 加速構造ビルド（build_acceleration_structures 記録）にかかった時間 [ms]
        let mut perf_tlas_ms:       f64 = 0.0;
        // このフレームで TLAS を実際に再構築したか（false=静止スキップ）
        let mut perf_tlas_built:    bool = false;
        // TLAS に登録されているインスタンス数
        let mut perf_tlas_insts:    u32 = 0;
        // そのうちスキンメッシュ由来のインスタンス数（＝毎フレーム再構築したスキン BLAS 数, Phase RT-Skin）。
        // 毎フレームの BLAS 再構築コストは概ねこの数に比例するので、tlas 時間と並べて読む。
        let mut perf_tlas_skin:     u32 = 0;
        // スキン RT（エントリ確保・変形 compute 記録）の CPU 側時間 [ms]（Phase RT-Skin）。
        let mut perf_tlas_skin_ms:  f64 = 0.0;
        // メインレンダーパス全体（begin〜pass drop）にかかった時間 [ms]
        let mut perf_main_pass_ms:  f64 = 0.0;
        // 水面描画（Phase W1: prepare + 屈折背景グラブ + 水面パス記録）にかかった CPU 時間 [ms]。
        // 水ボリュームが 0 個のフレームは一切実行されないため常に 0。
        let mut perf_water_ms:      f64 = 0.0;
        // 瞬発インタラクションフィールド更新（Phase I1: 収集 + 速度算出 + コンピュート記録）に
        // かかった CPU 時間 [ms]。草もソースも無いフレームは一切実行されないため常に 0。
        let mut perf_interact_ms:   f64 = 0.0;
        // 水面を ID パス（ピッキング）にも描けるか。
        // 水面パスの `prepare` が成功したフレームだけ true になり、後段の ID パスが参照する
        // （prepare 未実行＝パラメータ未アップロードの状態で ID 描画すると不正な ID を書く）。
        let mut water_pick_ready = false;
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
        // GPU メッシュレットカリング（第1弾）: このフレームに考慮した
        // メッシュレット×インスタンス総数（LOD0 不透明・可視インスタンス分）。
        let mut perf_meshlet_considered: u32 = 0;
        // 3D 物理同期 update_physics()（recv/書き戻し/kinematic送信/ドラッグ押し戻し同期問い合わせ）[ms]
        let mut perf_physics_ms:    f64 = 0.0;
        // 編集時スナップショット記録 try_record_physics_snapshot()（ECS 状態のキャプチャ）[ms]
        let mut perf_snapshot_ms:   f64 = 0.0;
        // 2D 物理同期 update_physics_2d() [ms]
        let mut perf_physics2d_ms:  f64 = 0.0;
        // このフレームで物理が実際に更新されたか（[PERF] 自動出力の判定に使う）
        let mut perf_physics_active = false;

        let _perf_t_ipc = std::time::Instant::now();
        self.process_ipc(event_loop);
        perf_ipc_ms = _perf_t_ipc.elapsed().as_secs_f64() * 1000.0;
        mark_frame_stage(FrameStage::IpcDone);
        if dbg { eprintln!("[SEED FRAME {dbg_frame}] process_ipc done"); }

        // AI 実行中はレンダリングをスキップして GPU リソースを LLM に解放する。
        // IPC は process_ipc で処理済みなので RESUME_RENDER を受け取れる。
        // request_redraw() でポーリングを継続し、RESUME_RENDER 受信後に即復帰できるようにする。
        if self.render_paused {
            self.pace_frame_if_unfocused(perf_t_total);
            if let Some(w) = &self.window { w.request_redraw(); }
            return;
        }

        // ── 最小化ガード ────────────────────────────────────────────────
        // ウィンドウが最小化されると winit の inner_size は 0×0 を返す。
        // このまま描画すると、カメラ／ビューポート計算が幅・高さ 0 の矩形を
        // 生成し、wgpu の set_viewport バリデーション
        // （"Viewport has invalid rect ... size is less than or equal to 0"）で
        // パニックする。パニックが巻き戻る際に Surface の drop で
        // 「未 present の SurfaceTexture が残っている」二次パニックを誘発し、
        // プロセスが abort（0xC0000409）してしまう。
        //
        // 最小化中はそもそも表示されないため、フレーム描画を丸ごとスキップし、
        // request_redraw() でポーリングだけ継続して復帰に備える。
        // サーフェスは resize しない（resize() 側も 0 サイズを無視する）ため、
        // 復元時は既存のスワップチェーンでそのまま描画を再開できる。
        if let Some(w) = &self.window {
            let sz = w.inner_size();
            if sz.width == 0 || sz.height == 0 {
                self.pace_frame_if_unfocused(perf_t_total);
                w.request_redraw();
                return;
            }
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
                perf_physics_active = true;
                if dbg { eprintln!("[SEED FRAME {dbg_frame}] update_physics start"); }
                // 3D 物理同期の所要時間を計測（recv・書き戻し・kinematic送信・
                // ドラッグ押し戻しの同期オーバーラップ問い合わせ最大 20ms を含む）
                let _perf_t_phys = std::time::Instant::now();
                self.update_physics();
                perf_physics_ms = _perf_t_phys.elapsed().as_secs_f64() * 1000.0;
                if dbg { eprintln!("[SEED FRAME {dbg_frame}] update_physics done"); }
                // 編集時のみスナップショットを記録する（変化なしなら自動停止）
                if self.mode == RuntimeMode::Edit && self.edit_physics_enabled {
                    let dt = 1.0 / 60.0f64; // 固定タイムステップ（物理スレッドと同期）
                    let _perf_t_snap = std::time::Instant::now();
                    self.try_record_physics_snapshot(dt);
                    perf_snapshot_ms = _perf_t_snap.elapsed().as_secs_f64() * 1000.0;
                }
            }

            // ── 2D 物理同期（Play フレームまたは編集時 2D 物理シミュレーション有効時）─────
            // 2D 物理はタイムラインと連動する（3D タイムラインと同期）
            let is_edit_physics_stepping = self.should_step_edit_physics();
            let should_update_physics_2d = (self.mode == RuntimeMode::Play && !self.paused)
                || (self.mode == RuntimeMode::Edit && self.edit_physics_2d_enabled && is_edit_physics_stepping);
            if should_update_physics_2d {
                perf_physics_active = true;
                let _perf_t_phys2d = std::time::Instant::now();
                self.update_physics_2d();
                perf_physics2d_ms = _perf_t_phys2d.elapsed().as_secs_f64() * 1000.0;
            }
        }
        mark_frame_stage(FrameStage::PhysicsDone);

        // ── 時間 ──────────────────────────────────────
        let time_running = self.mode == RuntimeMode::Play && !self.paused;
        let ctx: FrameContext = self.clock.tick(time_running);
        // ── シェーダへ配る時間（CameraUniform.time）─────────────────────────
        //   Play（非ポーズ）  … ゲーム内時間 `anim_time`。スクリプトの
        //                        SEED.Time.ElapsedTime と位相が揃う（従来仕様）。
        //   Edit / ポーズ中   … 壁時計 `ambient_time`。**編集中も止まらない**。
        // これにより水面の波と L3 シェーディングアセットの時間応答が Edit 中も動く。
        // 切替時に位相は跳ぶが、波・時間応答は位相の連続性を必要としないため許容する。
        // 草・風（GrassUniform.time）と水面・L3 アセットが共通で使うシェーダ時間。
        // Play 中はゲーム内時間（スクリプトの SEED.Time.ElapsedTime と同位相）、
        // Edit・ポーズ中は壁時計（ambient_time）で進み続ける（ユーザー要望:
        // 編集中も波・風が動いて見えること）。
        let shader_time = if time_running { ctx.anim_time } else { ctx.ambient_time };
        let in_editor = self.mode == RuntimeMode::Edit || self.paused;
        // ── Edit ビューモード（3Dシーン / 2Dシーンタブ）判定 ─────────────────
        // edit_view_2d: Edit モード + シーン世界線 + View2D。
        //   3D シーンを非表示にし、スクリーンスペースキャンバスを WYSIWYG で重ね表示する。
        // edit_view_hide_ss: Edit モード + シーン世界線 + View3D。
        //   スクリーンスペースキャンバス（Actor2D）を描画・ピッキングとも非表示にする。
        // どちらも Play モード・アクター編集タブには影響しない。
        let edit_view_2d      = self.edit_view_is_2d();
        let edit_view_hide_ss = self.edit_view_hides_ss_canvas();
        // 現在の世界線が 2D キャンバスモードかどうか。
        // View3D（edit_view_hide_ss）では SS キャンバスを完全に隠すため false 扱いにし、
        // スプライト収集・矩形アウトライン・2D コライダー・GPU ID ピッキングを一括で抑制する。
        let is_canvas = self.canvas_world_lines.contains(&self.active_world_line)
            && !edit_view_hide_ss;
        // スクリーンスペースモード:
        //   - チェックボックス ON: スクリーンスペース
        //   - プレイ中: 常にスクリーンスペース
        //   - アクター編集タブの 2D 世界線: 常にスクリーンスペース（編集パネルは従来通り）
        //   - Edit の 2D シーンビュー: 常にスクリーンスペース（WYSIWYG 表示）
        let use_screen_space = self.canvas_screen_space_overlay || !in_editor
            || self.actor_edit_canvas_wls.contains(&self.active_world_line)
            || edit_view_2d;

        // アクター編集タブの 2D キャンバスのみ 2D オルソカメラを使用する。
        // シーン上のキャンバスは screenSpace チェック ON でも 3D カメラを維持する。
        let is_actor_edit_2d = self.actor_edit_canvas_wls.contains(&self.active_world_line);
        // 2D オルソカメラをメインカメラとして使うビューかどうか。
        //   - アクター編集タブの 2D 世界線（従来動作）
        //   - Edit の 2D シーンビュー（canvas_cameras[0] でパン・ズーム）
        let use_ortho_2d_camera = is_actor_edit_2d || edit_view_2d;
        // SS レイアウト（ビューポート基準のルートアンカー・auto_scale）を適用するか。
        // 従来の scene_canvas_ss と同義（スプライト・コライダー・ID の座標計算に使う）。
        let ss_layout = is_canvas && use_screen_space && !is_actor_edit_2d;
        // シーンのスクリーンスペースキャンバス: 3D メインカメラ + 2D オーバーレイ合成。
        // アクター編集タブは camera_buf 自体が 2D なのでオーバーレイ不要。
        // Edit の 2D シーンビューも camera_buf 自体が 2D（パン・ズーム可能）になるため
        // オーバーレイパスは使わずメインパスで直接描画する。
        let scene_canvas_ss = ss_layout && !edit_view_2d;

        if in_editor {
            if use_ortho_2d_camera {
                // 2D ビュー（アクター編集タブ / 2D シーンビュー）:
                // MMB ドラッグで XY パン、スクロールでズーム。3D デバッグカメラは動かさない。
                // （3D ビューの MMB パンと操作を統一）
                if self.cam_input.mmb {
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
                        .clamp(CAM2D_ORTHO_HALF_H_MIN, CAM2D_ORTHO_HALF_H_MAX);
                }
            } else {
                // 3D モード（ワールドスペースキャンバス含む）: 通常のデバッグカメラ更新
                // スナップアニメーション中は回転を補間する（RMB/移動キーでキャンセル）
                self.update_camera_snap_anim(ctx.delta_time);
                // 透視↔正射の投影切替を 0.3 秒かけて補間する
                self.camera.update_projection_anim(ctx.delta_time);
                self.camera.update(&self.cam_input, ctx.delta_time);
            }
        }

        // ─ 1-6. ゲームロジック（Play 時のみ）─────────
        // Scene の Schedule に登録された ECS システム群（C# スクリプト駆動を含む）を
        // フェーズ順に実行する。
        if time_running {
            use crate::engine::ecs::Phase;
            use crate::engine::core::scripting::{publish_input, publish_physics_sender};
            // アニメーション評価（スクリプト更新より前に実行し、スクリプトが上書き可能にする）。
            // AnimatorComponent のクリップを進めて対象アクターの Transform 等へ書き込む。
            self.update_animations(ctx.delta_time);
            // スクリプトの Input API 用に入力状態への読み取り専用ポインタを公開する。
            // 入力イベントの処理はイベントハンドラ側で行われるため、
            // フェーズ実行中に self.input が変更されることはない。
            publish_input(Some(&self.input));
            // スクリプトの Physics.Raycast 用に物理スレッドへの送信チャンネルを公開する
            publish_physics_sender(self.physics_thread.as_ref().map(|t| t.command_sender()));
            // AudioSource.IsPlaying 判定用に再生中スロット一覧を公開する
            crate::engine::core::scripting::host_api::publish_playing_audio_slots(
                self.playing_audio_slots());
            // CanvasTransform.ScreenPosition 用に 2D アクターのスクリーン座標を公開する
            //（描画と同一の座標変換チェーンでフレームごとに計算する）
            crate::engine::core::scripting::host_api::publish_screen_positions(
                self.collect_2d_screen_positions());
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] begin_frame"); }
            if let Some(scene) = &mut self.scene { scene.run_phase(Phase::BeginFrame, &ctx); }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] early_update"); }
            if let Some(scene) = &mut self.scene { scene.run_phase(Phase::EarlyUpdate, &ctx); }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] update"); }
            if let Some(scene) = &mut self.scene { scene.run_phase(Phase::Update, &ctx); }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] constant_update"); }
            for fixed_ctx in self.clock.drain_fixed() {
                if let Some(scene) = &mut self.scene { scene.run_phase(Phase::ConstantUpdate, &fixed_ctx); }
            }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] late_update"); }
            if let Some(scene) = &mut self.scene { scene.run_phase(Phase::LateUpdate, &ctx); }
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] scene.render"); }
            if let Some(scene) = &mut self.scene { scene.run_phase(Phase::Render, &ctx); }
            mark_frame_stage(FrameStage::ScriptPhaseDone);
            // キャラクターコントローラーの ①希望位置送信（スクリプトが Transform に書いた値を
            // 物理へ非同期送信）＋ ②前フレーム分の補正結果を ECS へ反映（描画直前）を行う。
            // **必ずスクリプトフェーズ実行後・描画前**に呼ぶことで、この フレームでスクリプトが
            // 書いた希望位置を拾い、かつスクリプトの貫通直書きをクランプ済みの補正後位置で上書きする。
            self.sync_character_controllers();
            mark_frame_stage(FrameStage::CharCtrlDone);
            // 入力・物理チャンネルの公開を解除する（フェーズ外でのアクセスを防ぐ）
            publish_input(None);
            publish_physics_sender(None);
            // スクリプトが積んだシーン操作コマンド（Instantiate / Destroy）を適用する。
            // フェーズ実行後にまとめて適用することで、実行中スクリプトとの競合を避ける
            // （生成アクターはこの後の描画収集から同フレームで見える）。
            self.apply_script_scene_commands();
            // スクリプトが積んだオーディオコマンド（Audio.Play 等）を適用する
            self.apply_script_audio_commands();
            // AudioComponent の play_on_start 発火と距離減衰・パンを更新する
            self.update_component_audio();
            if dbg { eprintln!("[SEED FRAME {dbg_frame}] game logic done"); }
        }

        // ─ 1-7. ジョイントアタッチ（ソケット）追従 ─────────
        // モデルアニメ評価（update_animations）後・描画インスタンス収集前に、
        // JointAttach を持つアクターをターゲットモデルのジョイントへ追従させる。
        // Edit / Play 両モードで毎フレーム走る（パーティクル常時プレビューと同様）。
        // Edit では anim_drive が無いためモデル静止＝バインドポーズのジョイントへ吸着する。
        self.update_joint_attachments();

        // ── GPU カメラ・インスタンスバッファ更新 ──────
        let window_size = self.window.as_ref().map(|w| w.inner_size());
        // Outdated ハンドラ用: &mut self.renderer を借用するブロック内では
        // self メソッドが呼べないため、HWND を事前にコピーしておく。
        // Outdated 発生時（SetParent 後）に GetParent(my_hwnd) で
        // 正確なコンテナサイズを取得する。
        let my_hwnd = self.window_hwnd();
        let queue = self.draw_ctx.as_ref().map(|c| c.queue.clone());

        // PLAY_DIAG 用: レンダーターゲット（on_resize は親クライアント矩形で offscreen/surface を
        // サイズする, event_handler.rs:36-38）と game_viewport の元になる window.inner_size() が
        // 埋め込みで一致しているかを後段で突き合わせるため、ここで親クライアント矩形を控える。
        // scene の可変借用（下の if let）に入る前に取得する（借用衝突回避）。
        let diag_parent_client = if *PLAY_DIAG_ENABLED { self.get_parent_client_size() } else { None };

        // Play モードのスケーリングモードに応じたビューポート矩形とクリアカラー。
        // カメラ選択ブロック内で上書きされ、メインレンダーパスで適用する。
        //
        // 【重要】ビューポート／スケーリング計算の基準は「実レンダーターゲットサイズ」であり
        // window.inner_size() ではない。埋め込み Play では on_resize が
        // get_parent_client_size()（親コンテナのクライアント矩形）で surface/offscreen を
        // サイズしており（event_handler.rs:36-38）、子ウィンドウの inner_size はホスト側の
        // 都合で親クライアントと一致しないことがある。inner_size を基準にすると
        //   (1) カメラのスケーリング設定（レターボックス等）が実描画領域とズレて無視されたように見える
        //   (2) リサイズ中に game_viewport が実ターゲットからはみ出し set_viewport でパニックする
        // という 2 つの不具合が起きる。そこで on_resize と同じ導出
        // （get_parent_client_size().or(window_size)）で実ターゲットサイズを求め、これを
        // 射影アスペクト・ビューポート・スケーリング計算の共通基準にする。
        // スタンドアロン（ウィンドウ Play 等）では親が無く None → window_size に落ちるため
        // 従来と同一挙動になる（後退なし）。
        let render_target_size = self.get_parent_client_size().or(window_size);
        let win_w_f = render_target_size.map_or(1280.0_f32, |s| s.width  as f32);
        let win_h_f = render_target_size.map_or(720.0_f32,  |s| s.height as f32);

        // NOTE: オブジェクト単位の視錐台カリングは撤去したため、Edit モードのカメラプレビュー用
        //       OR カリング視錐台（旧 preview_frustum）も不要になった。メッシュレットカリングは
        //       メインカメラ視錐台のみで動作する（プレビュー小窓の描画はカリング対象外）。
        let mut game_viewport:    (f32, f32, f32, f32) = (0.0, 0.0, win_w_f, win_h_f);
        let mut game_clear_color: [f32; 4]             = [0.1, 0.1, 0.1, 1.0];
        // LetterBox / PillarBox 時の帯カラー。デフォルト黒。
        let mut game_bar_color:   [f32; 4]             = [0.0, 0.0, 0.0, 1.0];
        // LetterBox / PillarBox 選択フラグ（帯カラーを clear に使用する）
        let mut uses_bar_mode:    bool                 = false;

        // 統合バッチ更新で使うためここで宣言しておく（begin_frame ブロック外でも参照できるよう）
        let mut saved_frustum_planes: [[f32; 4]; 6] = [[0.0; 4]; 6];
        let mut saved_camera_pos:     [f32; 3]       = [0.0; 3];
        // シャドウ（Phase R2）用のアクティブカメラ情報（3D パースペクティブ時のみ Some）。
        // (view 行列, near, far, fov_y[rad], aspect)。CSM のカスケード分割・視錐台計算に使う。
        // 2D オルソ・正射カメラ時は None（方向光 CSM は透視カメラ前提のため影を落とさない）。
        let mut saved_shadow_cam: Option<(Mat4x4<f32>, f32, f32, f32, f32)> = None;
        // 速度バッファ（モーションベクタ）: このフレームは前フレームとの連続性が切れているか。
        // カメラ更新ブロックで確定し、統合バッチ更新ブロックが各バッチへ伝播する
        // （カメラ由来の速度＝prev_view_proj と、インスタンス由来の速度＝prev_model の
        //  **両方**をリセットしないと、Play 開始時にアクタだけが巨大な速度を吐く）。
        let mut velocity_reset_frame: bool = false;
        // シェーディングアセット（L3-a）のフォールバック連鎖の第 1 段: メインカメラ指定。
        // カメラ選択ブロック（Play モードで is_main カメラを DFS 探索する箇所）で拾い、
        // 描画ブロックの deferred ライティングパスで使う。ここで控えるのは、同じ DFS を
        // フレーム内で 2 度走らせないため（アクタ数が多い地形シーンで効く）。
        // Edit モード・Play ポーズ中はデバッグカメラを使うため None のまま
        // （＝Scene 指定へフォールバックする）。
        let mut main_camera_shading_asset: Option<String> = None;

        if let (Some(scene), Some(camera_buf), Some(queue)) =
            (&mut self.scene, &self.camera_buf, queue)
        {
            // カメラ選択:
            //   - 2D アクター編集タブ → 2D オルソカメラ
            //   - Play モード         → シーン内 is_main=true の CameraComponent
            //                           （見つからなければデバッグカメラにフォールバック）
            //   - Edit モード         → デバッグカメラ
            // 2D オルソカメラビュー（アクター編集 2D タブ / 2D シーンビュー）は
            // canvas_cameras の 2D カメラをメインカメラとして使う。
            let (view, proj, cam_pos_arr, shadow_cam): (Mat4x4<f32>, Mat4x4<f32>, [f32; 3], Option<(f32, f32, f32, f32)>) = if use_ortho_2d_camera {
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
                (v, p, [cam_2d.pan_x, cam_2d.pan_y, -100.0], None) // 2D オルソは影なし
            } else if self.mode == RuntimeMode::Play && !self.paused {
                // Play モード（非ポーズ時）: is_main=true の CameraComponent を探す
                // スケーリングモードに応じたビューポート矩形・射影アスペクト比・実効 FOV を計算する
                let game_cam = scene.find_main_camera().map(|(tf, cd)| {
                    // シェーディングアセット（L3-a）: メインカメラの指定を控える。
                    // ここでしか CameraComponentData に触れないため、この場で拾うのが最も安い。
                    main_camera_shading_asset = cd.shading_asset.clone();
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
                    // 投影方式に応じて透視 / 正射を切り替える（正射は縦 ortho_height 基準）
                    let p = match cd.projection {
                        crate::engine::components::CameraProjection::Perspective => {
                            Mat4x4::perspective_lh(fov_y_rad, proj_aspect, cd.near, cd.far)
                        }
                        crate::engine::components::CameraProjection::Orthographic => {
                            let half_h = cd.ortho_height.max(0.01) * 0.5;
                            let half_w = half_h * proj_aspect;
                            Mat4x4::orthographic_lh(
                                -half_w, half_w, -half_h, half_h,
                                cd.near.max(0.01), cd.far.max(cd.near + 0.1),
                            )
                        }
                    };
                    // CSM は透視カメラ前提。ゲームカメラが正射のときは影なし。
                    let shadow_opt = match cd.projection {
                        crate::engine::components::CameraProjection::Perspective =>
                            Some((cd.near, cd.far, fov_y_rad, proj_aspect)),
                        crate::engine::components::CameraProjection::Orthographic => None,
                    };
                    (v, p, [px, py, pz], shadow_opt)
                });
                // メインカメラが未配置の場合はデバッグカメラにフォールバック
                game_cam.unwrap_or_else(|| {
                    let v  = self.camera.view_matrix();
                    let p  = self.camera.projection_matrix();
                    let cp = self.camera.position();
                    // デバッグカメラ（透視）: near/far/fov/aspect を CSM に渡す。
                    let sc = Some((
                        self.camera.base.projection.near,
                        self.camera.base.projection.far,
                        self.camera.base.projection.fov_y_rad,
                        self.camera.base.projection.aspect_ratio,
                    ));
                    (v, p, [cp.x, cp.y, cp.z], sc)
                })
            } else {
                // Edit モード: デバッグカメラ
                let v  = self.camera.view_matrix();
                let p  = self.camera.projection_matrix();
                let cp = self.camera.position();
                let sc = Some((
                    self.camera.base.projection.near,
                    self.camera.base.projection.far,
                    self.camera.base.projection.fov_y_rad,
                    self.camera.base.projection.aspect_ratio,
                ));
                (v, p, [cp.x, cp.y, cp.z], sc)
            };

            let view_proj = proj * view;

            // シャドウ用にアクティブカメラ情報を保存する（3D 透視カメラ時のみ）。
            if let Some((n, f, fo, a)) = shadow_cam {
                saved_shadow_cam = Some((view, n, f, fo, a));
            }

            // カメラ解像度ユニフォームは実レンダーターゲット基準に揃える。
            // resolution の用途は **テクスチャのアドレッシング**（AO / SSGI / シャドウマスクは
            // RT 全面で生成されるため）であり、深度→ワールド座標の復元には使わない。
            // 復元の正規化に使うのは下の `viewport`（NDC が写像される矩形）である
            // ―― この 2 つを混同すると Play のレターボックス時に復元座標が横滑りする。
            let res = render_target_size.map_or([1280.0, 720.0], |s| {
                [s.width as f32, s.height as f32]
            });
            // 逆 ViewProjection（デファードのライティングパスが深度→ワールド座標復元に使う）。
            // 特異行列（逆行列なし）の場合は単位行列へフォールバックする（パニックさせない）。
            let inv_view_proj = view_proj.inverse().unwrap_or_else(Mat4x4::identity);

            // ── 速度バッファ（モーションベクタ）用の前フレーム ViewProjection ─────────
            //   「前フレームが存在し、かつ前フレームと連続している」ときだけ実際の前フレーム
            //   行列を積む。そうでないフレームは prev = curr（＝静的ジオメトリの速度が
            //   厳密に 0）にする。**速度爆発を防ぐ関門はここ 1 箇所に集約している**。
            //
            //   連続性キー: (Play か, ポーズ中か, RT 幅, RT 高さ)。
            //     - Play⇄Edit / ポーズ切替 … カメラがゲームカメラ⇄デバッグカメラへ飛ぶ
            //     - RT サイズ変化           … 速度 RT が作り直され履歴が無効になる
            //   シーンロード・カメラテレポートは `velocity_reset_requested` で別途扱う。
            let velocity_key = (
                self.mode == RuntimeMode::Play,
                self.paused,
                render_target_size.map_or(0, |s| s.width),
                render_target_size.map_or(0, |s| s.height),
                // ワールドライン（タブ）切替はシーンの総入れ替えに等しい。
                self.active_world_line,
            );
            let velocity_continuous = !self.velocity_reset_requested
                && self.velocity_prev_key == Some(velocity_key);
            self.velocity_reset_requested = false;
            self.velocity_prev_key = Some(velocity_key);
            let prev_view_proj = if velocity_continuous {
                self.velocity_prev_view_proj.unwrap_or(view_proj)
            } else {
                view_proj
            };
            // 次フレームのために今フレームの行列を控える。
            self.velocity_prev_view_proj = Some(view_proj);
            // 統合バッチ側（インスタンスごとの前フレーム行列）へ伝播するためのフラグ。
            velocity_reset_frame = !velocity_continuous;

            camera_buf.update(&queue, &CameraUniform {
                view_proj:      view_proj.transpose().data,
                view:           view.transpose().data,
                position:       cam_pos_arr,
                // シェーダ時間（L3 シェーディング契約 `ShadingSurface.time` と水面の波の供給元）。
                // Play（非ポーズ）はゲーム内時間 = スクリプトの SEED.Time.ElapsedTime と同位相、
                // Edit・ポーズ中は壁時計（ambient_time）で進み続ける。詳細は `shader_time` の定義箇所。
                // 草の揺れ（GrassUniform.time）も同じ shader_time 駆動（Edit中も風が動く）。
                time:           shader_time,
                resolution:     res,
                _pad2:          [0.0; 2],
                inv_view_proj:  inv_view_proj.transpose().data,
                prev_view_proj: prev_view_proj.transpose().data,
                // ビューポート矩形（深度→ワールド復元の正規化に使う。resolution とは別物）。
                // Play（非ポーズ）は set_viewport するゲーム領域、それ以外は RT 全面。
                // ここで入れるのは**クランプ前**の値で、実サーフェスへのクランプ確定後に
                // update_viewport で上書きする（クランプは frame.surface_size() が要るため）。
                viewport:       if self.mode == RuntimeMode::Play && !self.paused {
                    [game_viewport.0, game_viewport.1, game_viewport.2, game_viewport.3]
                } else {
                    [0.0, 0.0, res[0], res[1]]
                },
            });

            // ── 埋め込み Play 黒画面 診断（SEED_PLAY_DIAG）─────────────────────────
            // Play（非ポーズ）フレームの先頭 PLAY_DIAG_FRAMES 回だけ、ゲームカメラ描画に
            // 実際に渡る値を出力する。黒画面が「ビューポート矩形がレンダーターゲットと
            // 不整合（win.inner_size ≠ 親クライアント＝offscreen サイズ）」で起きているかを
            // 一発で切り分ける。矩形が (0,0,0,0) や画面外・極小、または inner_size と
            // parent_client がズレていれば本因が確定する。
            if *PLAY_DIAG_ENABLED && self.mode == RuntimeMode::Play && !self.paused {
                let n = PLAY_DIAG_FRAME.fetch_add(1, Ordering::Relaxed);
                if n < PLAY_DIAG_FRAMES {
                    let (gx, gy, gw, gh) = game_viewport;
                    let inner = window_size.map(|s| (s.width, s.height));
                    let parent = diag_parent_client.map(|s| (s.width, s.height));
                    let has_main = scene.find_main_camera().is_some();
                    eprintln!(
                        "[PLAY_DIAG {n}] main_cam_found={has_main} cam_pos=({:.2},{:.2},{:.2}) \
                         game_viewport=(x{:.1} y{:.1} w{:.1} h{:.1}) res=[{:.0},{:.0}] \
                         win_inner={inner:?} parent_client={parent:?} \
                         scene_canvas_ss={scene_canvas_ss} use_screen_space={use_screen_space} \
                         uses_bar_mode={uses_bar_mode} clear={:?}",
                        cam_pos_arr[0], cam_pos_arr[1], cam_pos_arr[2],
                        gx, gy, gw, gh, res[0], res[1], game_clear_color,
                    );
                }
            }

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
                    // 2D オルソオーバーレイカメラはデファードのライティングパス対象外だが、
                    // CameraUniform 構造体を埋める必要があるため一律で逆行列を計算する。
                    let cvp_inv = cvp.inverse().unwrap_or_else(Mat4x4::identity);
                    canvas_cam_buf.update(&queue, &CameraUniform {
                        view_proj:      cvp.transpose().data,
                        view:           cv.transpose().data,
                        position:       [0.0, 0.0, -100.0],
                        // 2D キャンバスオーバーレイもメインカメラと同じ時刻を配る
                        // （時間はフレーム全体で 1 つの値であり、カメラごとに変える理由がない）。
                        time:           shader_time,
                        resolution:     [vp_w, vp_h],
                        _pad2:          [0.0; 2],
                        inv_view_proj:  cvp_inv.transpose().data,
                        // 2D オルソオーバーレイは G-Buffer（速度）を持たないので prev=curr（速度 0）。
                        prev_view_proj: cvp.transpose().data,
                        // 2D オルソオーバーレイは深度→ワールド復元を行わないが、
                        // 規約どおり「描画対象全面」を入れておく（黒帯の概念が無い）。
                        viewport:       [0.0, 0.0, vp_w, vp_h],
                    });
                }
            }

            let frustum_planes = extract_frustum_planes(&view_proj.data);
            let camera_pos     = cam_pos_arr;
            // 統合バッチ更新のためにブロック外へ保存する
            saved_frustum_planes = frustum_planes;
            saved_camera_pos     = camera_pos;
            // 次フレーム先頭の地形 LOD 選択（tick_terrain_lod）が使うカメラ位置を控える。
            self.last_camera_pos = camera_pos;

            // ── 旧: per-MC `instanced_batch` の毎フレーム更新（廃止）──────────────
            // かつてここで `update_all_mc_batches_for_wl` が world_line 全 MC の
            // `ModelComponent::instanced_batch` を毎フレーム `update()`（距離 LOD 振り分け＋
            // GPU バッファ書き込み）していた。しかし Phase R7 のバッチ統合以降、実際の描画は
            // すべて `shared_model_batches`（batch_key ごとの統合バッチ）を通る。per-MC の
            // `instanced_batch` はどの描画・アウトライン・ピッキング経路からも参照されず
            // （唯一の描画アクセサ `rendering_refs()` は呼び出し 0 件）、その `update()` は
            // 完全な死荷重だった。16×16×3層=768 チャンクの地形では実測 batch≈4ms/フレームを
            // まるごと浪費していた（[PERF] の batch バケット）。統合バッチは下の
            // `shared_model_batches` 更新ループ（merge バケット）で別途更新されるため、
            // ここでの per-MC 更新は不要。呼び出しごと削除して batch バケットを 0 にする。
            perf_batch_ms = 0.0;
        }

        // ── ギズモ位置：全選択アクターの重心（マルチ選択対応） ──
        //
        // 【重要】ここは「ギズモを**描く**位置」であり、当たり判定とドラッグ計算が使う
        // `current_gizmo_pos()` と**必ず同じ優先順位**でなければならない。
        // 制御点選択（フェーズA）は当初 `current_gizmo_pos()` にだけ分岐を入れたため、
        // 「点を掴んでも見た目のギズモはアクターの位置に居座り、当たり判定だけ点の位置にある」
        // という状態になっていた（＝実機で「制御点にギズモが出ない」と報告された不具合の原因）。
        // 判定側と同じく制御点を最優先にする。
        let gizmo_pos = self.selected_control_point_world_pos()
            .or_else(|| self.selected_actors_centroid())
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
            // 2D オルソカメラビュー（アクター編集 2D タブ / 2D シーンビュー）では
            // 3D レイキャストによるプレビュー球体は使わない。
            if !use_ortho_2d_camera {
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
            } else if edit_view_2d {
                // 2D シーンビュー: プレビュー球体の代わりに、カーソルがヒットしている
                // ルートキャンバスを判定して枠線ハイライト対象を更新する。
                // ドロップ時のキャンバスヒット判定（handle_drop_actor_2d）と同一ロジック。
                let hover_pt = self.window_to_canvas_2d(hsx as f32, hsy as f32);
                self.drag_hover_canvas_entity = self.hit_root_canvas_at(hover_pt);
            }
            // アクター編集タブの 2D モード: pending_drop_hover は消費されるが何も更新しない
        }

        // ピック要求を取り出す（描画ブロック内で使用）
        let pick_pos = self.pending_pick.take();
        let mut did_pick = false;
        // 今フレームの ID バッファ読み戻しがコントロールポイント D&D のためだったか。
        // did_pick と同じスコープ・同じ寿命で持ち、GPU サブミット後の解決処理で参照する
        //（読み戻しバッファは 1 フレームに 1 回しか読めないため、用途の排他を表す）。
        let mut did_control_point_readback = false;
        // 今フレームの ID バッファ読み戻しがコントロールポイントの**ホバー**
        //（配置予定マーカー）のためだったか。ドロップ用フラグとは排他。
        let mut did_control_point_hover_readback = false;

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
        let cam_gizmo_actor_mats: Vec<(usize, [[f32; 4]; 4])> = if in_editor && !use_ortho_2d_camera {
            if let Some(scene) = &self.scene {
                camera_scene_gizmo::collect_camera_actor_matrices(
                    &scene.actors, &scene.world, self.active_world_line,
                )
            } else { vec![] }
        } else { vec![] };
        let camera_gizmo_count: u32 = cam_gizmo_actor_mats.len() as u32;

        // ライトギズモアイコンの (DFS ID, アイコン行列) リスト（3D 編集モードのみ）。
        // ピック情報としてカメラギズモの直後の ID 範囲に割り当てる。
        let light_gizmo_actor_mats: Vec<(usize, [[f32; 4]; 4])> = if in_editor && !use_ortho_2d_camera {
            if let Some(scene) = &self.scene {
                super::light_scene_gizmo::collect_light_actor_matrices(
                    &scene.actors, &scene.world, self.active_world_line,
                )
            } else { vec![] }
        } else { vec![] };
        let light_gizmo_count: u32 = light_gizmo_actor_mats.len() as u32;

        // パーティクルエミッタギズモアイコンの (DFS ID, アイコン行列) リスト（3D 編集モードのみ）。
        // ピック情報としてライトギズモの直後の ID 範囲に割り当てる。
        let particle_gizmo_actor_mats: Vec<(usize, [[f32; 4]; 4])> = if in_editor && !use_ortho_2d_camera {
            if let Some(scene) = &self.scene {
                super::particle_scene_gizmo::collect_particle_actor_matrices(
                    &scene.actors, &scene.world, self.active_world_line,
                )
            } else { vec![] }
        } else { vec![] };
        let particle_gizmo_count: u32 = particle_gizmo_actor_mats.len() as u32;

        // ID 空間レイアウト: [MC | カメラギズモ | ライトギズモ | エミッタギズモ | キャンバス]
        // 各ギズモの ID ベースオフセット（ピックのデコードと id_pass 描画で共有する）。
        let light_gizmo_id_base:    u32 = mc_total_instances + camera_gizmo_count;
        let particle_gizmo_id_base: u32 = light_gizmo_id_base + light_gizmo_count;
        // キャンバス ID のベースオフセット（MC + 全ギズモアイコンの後）
        let canvas_id_offset: u32 = particle_gizmo_id_base + particle_gizmo_count;

        // コントロールポイント（汎用パス）のギズモ頂点を組む。
        // 選択中アクタの各制御点にワイヤキューブを、点間に補間方法ごとに描き分けた
        // ラインを出す（Edit の 3D ビューのみ。表示条件は control_points_visible）。
        // レンダラを可変借用する前に `&self` で組んでおき、GPU バッファ化だけを
        // 描画ブロック内で行う（借用の衝突を避けるため）。
        let control_point_lines = self.build_control_point_line_batch();

        // ── ライト収集（メッシュシェーディング用 GPU ライト配列）──────────────
        // シーンの Light スロットを Transform とともに収集する。Play/Edit 両方で反映。
        // ライトが 0 灯なら後方互換フォールバックの方向光が返る（暗転しない）。
        // 可変借用（&mut self.renderer）に入る前に不変借用で確定しておく。
        // collect_gpu_lights は cast_shadows=true のライトへ shadow_index=1.0（影希望の
        // センチネル）を仮設定する。実スロット（方向光 0 / スポット 0..3）は
        // ShadowResources::prepare_frame が採用可否とともに確定させる。
        let mut frame_lights: Vec<crate::engine::methods::drawer::GpuLight> =
            if let Some(scene) = &self.scene {
                super::light_ops::collect_gpu_lights(&scene.actors, &scene.world, self.active_world_line)
            } else {
                Vec::new()
            };

        // ── Clustered Lighting: ライト配列を「平行光 → 局所ライト」へ安定分割（Phase C1）──
        // 平行光は視錐台全体に影響するためクラスタに入れず、フラグメントが常に全ピクセルで
        // 評価する（配列先頭 [0, dir_count) がその範囲）。クラスタ構築 compute は
        // dir_count 以降の局所ライト（point/spot/rect）だけを対象にする。
        //
        // ここで分割してから ShadowResources::prepare_frame（shadow_index 確定）と
        // light_buffer.update（GPU アップロード）を行う。分割は安定（グループ内の相対順序を
        // 保つ）なので、CSM の採用ライト・スポットのシャドウ配列レイヤ割り当ては変わらない。
        let frame_dir_count =
            crate::engine::methods::drawer::partition_directional_first(&mut frame_lights);

        // ── シャドウキャスター収集（Phase R2）──────────────────────
        // cast_shadows=true な ModelComponent の source_path 集合。影パスは
        // この集合に属する統合バッチ（shared_model_batches のキー）のみを描画する。
        // 粒度は「共有バッチ（モデルパス）単位」。同一モデルを共有する複数アクターで
        // cast_shadows が混在する場合、1 つでも true ならそのバッチ全体が影を落とす
        // （インスタンス単位の影除外は R2 では未対応・TODO）。
        let shadow_caster_paths: std::collections::HashSet<String> =
            if let Some(scene) = &self.scene {
                collect_mcs_in_world_line(&scene.actors, &scene.world, self.active_world_line)
                    .into_iter()
                    .filter(|(_, _, _, mc)| mc.cast_shadows && !mc.source_path.is_empty())
                    // Phase R7: シャドウキャスター集合も batch_key で識別する
                    // （shared_model_batches のキーが batch_key のため一致させる）。
                    .map(|(_, _, _, mc)| mc.batch_key())
                    .collect()
            } else {
                std::collections::HashSet::new()
            };
        // 影を落とすモデルが 1 つでもあり、かつ 3D 表示（2D ビューでない）なら影を有効化。
        let shadow_has_casters = !shadow_caster_paths.is_empty() && !edit_view_2d;

        // 選択アクターの種別（2D/3D）を可変借用の前に確定する。
        // self.renderer を可変借用した後は self の不変借用が取れないため。
        // ワールドスペース表示中（use_screen_space = false）の 2D アクターはパースペクティブ
        // カメラで描画されるため、ortho 半径ではなく 3D 半径を使う必要がある。
        let gizmo_actor_is_2d = use_ortho_2d_camera
            || (self.selected_primary_actor_is_2d() && use_screen_space);
        // 3D Canvas 子アクター軸をレンダーパス開始前（可変借用前）に事前計算する。
        // レンダーパス内では &mut self.renderer の可変借用が続くため self の不変借用が取れない。
        let canvas_child_axes_pre = self.selected_canvas_child_axes();
        // gizmo_space = Local のとき、選択中プライマリアクター（3D）のローカル回転軸を
        // レンダーパス開始前（可変借用前）に事前計算する。3D Canvas 子（canvas_child_axes_pre）
        // が優先されるため、そちらが Some の場合はここでは使用されない。
        let local_axes_pre = self.selected_local_axes();
        // 2D シーンビューのドラッグホバー中キャンバス枠ハイライト線分も
        // 可変借用前に事前計算する（キャンバス矩形バッチ構築時に流し込む）。
        let drag_hover_highlight_lines = if edit_view_2d && self.drag_hover_canvas_entity.is_some() {
            self.collect_drag_hover_highlight_lines()
        } else {
            Vec::new()
        };
        // Edit ビューモードによるギズモ抑制（可変借用前に確定する）:
        //   - View3D で SS キャンバスの 2D アクターを選択中: アイテム自体が非表示のため
        //     ギズモも表示しない（3D ワールドキャンバスの子は表示継続）
        //   - View2D で 3D アクターを選択中: 3D シーン非表示のためギズモも表示しない
        // 判定ロジックは gizmo_handler 側の操作抑制と共通（gizmo_suppressed_by_edit_view）。
        let gizmo_suppressed_by_view = self.gizmo_suppressed_by_edit_view();

        // ── GPU パーティクル CPU 更新（Phase RP・フェーズ 1）──────────────
        // 放出個数の決定・リングカーソル前進・pending_burst（スクリプトの Burst 要求）消費を行う。
        // World への &mut が必要なため、描画ブロック（&self.scene で不変借用）に入る前にここで実施する。
        //
        // 【dt は Play / Edit とも実フレーム時間（ctx.delta_time）を使う】
        // かつては Edit モードで固定 1/60 を渡していたが、これは誤りだった。
        // 物理は「固定ステップを accumulator で必要回数だけ刻む」ので固定 dt が正しいが、
        // パーティクルは 1 フレーム 1 ステップしか進めないため、固定 dt にすると経過時間が
        // 「フレーム数 × 1/60 秒」になり、実時間ではなく FPS に比例して速度が変わってしまう
        // （120fps なら 2 倍速）。実フレーム時間を渡すことで実時間基準になる。
        // Edit モードでも常時プレビューする方針は不変（playing=false は放出のみ停止）。
        //
        // 長時間ストール（シーンロード・ブレークポイント等）明けの巨大 dt で粒子が
        // 瞬間移動しないよう上限でクランプする（1 フレームで進める最大シミュレーション時間）。
        {
            /// パーティクルの 1 ステップで進める最大シミュレーション時間 [秒]。
            /// これを超える dt はクランプする（ストール明けの瞬間移動を防ぐ）。
            const PARTICLE_MAX_STEP_SECS: f32 = 1.0 / 15.0;
            let particle_dt = ctx.delta_time.clamp(0.0, PARTICLE_MAX_STEP_SECS);
            let awl = self.active_world_line;
            if let Some(scene) = self.scene.as_mut() {
                self.particle_system.collect_and_consume(
                    &mut scene.world, &scene.actors, awl, particle_dt,
                );
            } else {
                // シーンが無いフレームは孤児（エミッタ消滅後の粒子群）も含めて全解放する
                // （前フレームの描画対象を持ち越して描き続けないため）。
                self.particle_system.clear_all();
            }
        }

        // ── 水位グラフの時間発展（Phase W2.5）────────────────────────
        //
        // 【何をするか】`simulate_level = true` の Region 水域をノード、
        //  `WaterLinkComponent` を開口とする水位グラフを組み、固定 1/60 ステップで
        //  「水位差 × 開口面積 × 係数」の水移動を進める。結果は
        //  `WaterVolumeComponent.sim_level_y`（揮発）へ書き戻される。
        //
        // 【なぜここか】この下の `collect_water_volumes`（→ 描画・WaterQuery・岸波ベイク）
        //  より前でなければならない。ここで水位を更新しておけば、
        //  `ResolvedWaterVolume::surface_y` が現在水位を拾う 1 か所だけで
        //  描画も問い合わせも自動的に追随する（下流に個別の分岐が要らない）。
        //  また `&mut World` を要求するので、`self` を細かく借り続ける描画ブロックへ
        //  入る前に済ませておく必要がある（スカイボックス収集と同じ理由）。
        //
        // 【Play 中のみ】水位はゲーム状態なので `time_running`（Play かつ非ポーズ）
        //  でのみ進む。Play を抜けた最初のフレームで揮発水位が消え、
        //  インスペクタで設定した静止水面へ戻る（波が Edit 中も動くのとは対照的）。
        {
            let awl = self.active_world_line;
            let sim = &mut self.water_level_sim;
            if let Some(scene) = self.scene.as_mut() {
                sim.update(
                    &scene.actors, &mut scene.world, awl, ctx.delta_time, time_running,
                );
            }
        }

        // ── スカイボックス（天球）CPU 収集（Phase R9）──────────────
        // Skybox スロットを走査して描画対象を確定する（CameraLocked は最初の 1 つのみ）。
        // 読み取りのみ（&World）のため描画ブロック前にここで実施する。0 個なら以降即 return。
        {
            let awl = self.active_world_line;
            if let Some(scene) = self.scene.as_ref() {
                self.skybox_system.collect(&scene.world, &scene.actors, awl);
            }
        }

        // ── 草（地形散布プロップ）の GPU バッファ再構築（Terrain T3）────────────
        //   `rebuild_grass_gpu` は `&mut self` を要求する（バッファを作って
        //   self.terrain へ入れるため）。一方この下の描画ブロックは self の
        //   複数フィールドを個別に借り続けるので、そこからは呼べない。
        //   よって「描画ブロックへ入る前」に済ませておく。
        //   device / queue は Arc なので clone は参照カウントの増加だけで済む。
        //   散布が変わっていなければ（grass_gpu_dirty=false）関数内で即 return する。
        //   計測値は下の [PERF] 出力で使う（宣言をここへ置くのは、この時点で
        //   必ず 1 回だけ代入されるため。他の perf_* のように上で 0 初期化すると
        //   「代入した値が読まれない」警告になる）。
        // ── 散布モデル（kind=Model プロップ）の GPU リソース再構築（Terrain T3 第2段）──
        //   草（rebuild_grass_gpu）と同じ grass_gpu_dirty をトリガに、model 実アセットを
        //   ロードしてインスタンス行列をアップロードする。**必ず草より前に呼ぶ**こと:
        //   rebuild_grass_gpu が grass_gpu_dirty をクリアするため、逆順だと model 側が
        //   毎回スキップされる（rebuild_scatter_models_gpu 自身はフラグを寝かせない）。
        //   self.draw_ctx を不変借用しつつ self.terrain を可変で触るため、TerrainState の
        //   メソッドとして呼ぶ（App の &mut self メソッドだと draw_ctx 借用と衝突する）。
        //   camera_pos は距離 LOD の振り分けに使うだけ（見た目の姿勢には影響しない）。
        if let Some(ctx) = self.draw_ctx.as_ref() {
            self.terrain.rebuild_scatter_models_gpu(ctx, saved_camera_pos);
            // ── 散布モデルのチャンク単位フラスタム＋距離カリング（毎フレーム）──
            //   rebuild が用意した chunk_spans を、メインカメラ視錐台＋距離で絞り、
            //   可視チャンクのインスタンス行列だけをバッチへアップロードする。カメラが
            //   動くたびに可視集合が変わるため rebuild（dirty 時のみ）とは別に毎フレーム走らせる。
            //   これで G-Buffer パスもシャドウパスも可視ぶんだけを描く（描画コスト激減）。
            self.terrain.cull_and_update_scatter_models(
                ctx, &saved_frustum_planes, saved_camera_pos,
            );
        }

        let perf_grass_ms: f64 = {
            let t_grass = std::time::Instant::now();
            if let Some((device, queue)) = self
                .draw_ctx
                .as_ref()
                .map(|c| (c.device.clone(), c.queue.clone()))
            {
                self.rebuild_grass_gpu(&device, &queue);
            }
            t_grass.elapsed().as_secs_f64() * 1000.0
        };

        if let (Some(renderer), Some(scene), Some(camera_buf), Some(draw_ctx)) =
            (&mut self.renderer, &self.scene, &self.camera_buf, &self.draw_ctx)
        {
            // begin_frame = get_current_texture(): GPU バックプレッシャーでここが長くなる
            let _perf_t_bf = std::time::Instant::now();
            let begin_frame_result = renderer.begin_frame();
            perf_begin_frame_ms = _perf_t_bf.elapsed().as_secs_f64() * 1000.0;
            match begin_frame_result {
                Ok(mut frame) => {
                    // ── シャドウ行列の準備（Phase R2）──────────────────
                    // カスケード/スポットの light view-proj を計算して UBO・シャドウカメラへ
                    // 書き込み、frame_lights の shadow_index を確定させる（採用/不採用）。
                    // 影パスの実描画は skin compute 後・メインパス直前に record で行う。
                    let (sv, sn, sf, sfov, sasp) = saved_shadow_cam
                        .unwrap_or((Mat4x4::identity(), 0.1, 100.0, std::f32::consts::FRAC_PI_4, 1.0));

                    // カメラプレビュー用 CSM を組むためのライト配列スナップショット。
                    //
                    // prepare_frame は「影を希望する」センチネル（shadow_index≈1.0）を
                    // 採用結果（方向光=0.0 / スポット=レイヤ番号 / 不採用=-1.0）へ**書き換える**。
                    // つまり 2 回目以降の呼び出しではセンチネルが残っておらず、影が 1 つも
                    // 採用されない。プレビュー用の prepare_frame（別カメラ・別シャドウ資源）へは
                    // 必ず「書き換え前」の配列を渡す必要があるため、ここで控えておく。
                    // 採用スロットの割り当てはカメラ非依存なので、両者の shadow_index は一致する。
                    let lights_before_shadow_assign = frame_lights.clone();

                    let shadow_plan = draw_ctx.shadow.prepare_frame(
                        &draw_ctx.queue, &sv, sn, sf, sfov, sasp,
                        &mut frame_lights,
                        shadow_has_casters && saved_shadow_cam.is_some(),
                    );

                    // 実効モード（GPU 対応可否で降格済み）を解決する。以降の RT 影 / GI / TLAS
                    // 構築ゲートはすべてこの resolved_features を参照する（生の render_features は見ない）。
                    // NOTE: self.resolved_features() だと &self 全体を借用し、上位の &mut self.renderer
                    //       借用と衝突する。render_features は Copy な単一フィールドなので disjoint 借用で解決。
                    let resolved_features = self.render_features
                        .resolve(crate::engine::core::renderer::rt_shadow::rt_shadows_supported());
                    // このフレームで RT 影を使うか（実効の影方式が Rt かつ RT 対応 GPU）。
                    // フラグメントの実行時分岐（LightMeta.rt_shadows）と、後段のメインパスでの
                    // RT パイプライン/複合 BindGroup 選択の両方に使う（Phase R8）。
                    let rt_on = draw_ctx.rt_active(resolved_features.rt_shadow());

                    // このフレームで GPU メッシュレットカリング（第1弾）を使うか。
                    // メッシュレットカリングは常時有効。GPU が MULTI_DRAW_INDIRECT_COUNT 対応のときのみ稼働し、
                    // 非対応 GPU では自動的に従来 draw_indexed 経路へフォールバックする（ON/OFF 設定は撤去済み）。
                    let meshlet_active =
                        crate::engine::core::renderer::gpu_resources::meshlet_cull_supported();

                    // エディタのシーンビュー表示モード（デバッグカメラ専用）。
                    // Play 中・非 Edit では Lit（0）に固定し、ゲーム本編の見た目へ一切影響させない。
                    // メインカメラ用 LightMeta にのみ書き込まれ、プレビュー小窓（別 LightMeta・
                    // view_mode=0 固定）は常にライティング表示のまま維持される。
                    let scene_view_mode_code = if self.mode == RuntimeMode::Edit {
                        self.scene_view_mode.to_code()
                    } else {
                        0
                    };

                    // このフレームで RT-Translucency（高品質半透明＝色付き影＋屈折）を使うか。
                    // 実効の半透明方式が Rt（RT 対応 GPU でのみ resolve を通る）のとき有効。
                    // LightMeta.translucency_rt としてフラグメントへ渡り、RT 影シェーダの色付き影・
                    // 半透明フォワードの屈折の両方をゲートする。
                    let translucency_rt_on = resolved_features.translucency
                        == crate::engine::core::renderer::render_features::TranslucencyMode::Rt;

                    // ── RT ソフト影マスク対象ライトの選定（Phase RT-Shadow-Denoise）─────────────
                    // soft_radius>0 の上位 intensity ライト（最大 RT_SHADOW_MASK_LIGHTS 灯）へ
                    // shadow_mask_slot を書き込む。deferred+RT 影+ソフト影ありのフレームで、deferred
                    // ライティングが該当レイヤ（事前計算・デノイズ済みマスク）をサンプルしてディザ状の
                    // ガサガサを解消する。マスク非対象ライトは -1（既定）のまま従来のインライン
                    // rt_shadow_factor 経路になる。GPU アップロード前に書き込むこと。
                    let shadow_mask_selection =
                        crate::engine::core::renderer::assign_shadow_mask_slots(&mut frame_lights);

                    // ライト配列を GPU へアップロードする（全メッシュ描画が group 4 で共用）。
                    // メタ（ライト数・RT 影フラグ・ビューモード・RT-Translucency フラグ）も同時に更新される。
                    // shadow_index 確定後にアップロードする。
                    draw_ctx.light_buffer.update(
                        &draw_ctx.queue, &frame_lights, rt_on,
                        scene_view_mode_code,
                        translucency_rt_on,
                        self.ambient_color, self.ambient_intensity,
                    );

                    // シーンモード・アクター編集モード共通: world_line の全 MC を収集する
                    // タプル: (id_base, dfs_id, slot_i, &ModelComponent)
                    let all_mcs: Vec<(u32, u32, usize, &ModelComponent)> =
                        collect_mcs_in_world_line(&scene.actors, &scene.world, self.active_world_line);

                    // 水ボリューム収集（Phase W1）: WaterVolumeComponent を持つアクタを
                    // ワールド空間の中間表現（ResolvedWaterVolume）へ解決する。所有 Vec を返すので
                    // scene の借用は残らない。描画は WBOIT 合成後の専用パスで行う（下記 [WATER]）。
                    let water_volumes: Vec<crate::engine::water::ResolvedWaterVolume> =
                        crate::engine::water::collect_water_volumes(
                            &scene.actors, &scene.world, self.active_world_line,
                        );
                    // ── 岸波のショアフィールド更新（Phase W1.5）────────────────────────
                    //
                    // 【何をするか】水域ごとに「水深・符号付き岸距離・岸方向」の俯瞰 2D を
                    //  CPU で焼き、水面シェーダが 1 サンプルで岸波を作れる状態にする。
                    //
                    // 【毎フレームは焼かない】`update` は署名（地形編集バージョン・水面 Y・
                    //  窓・Ocean のカメラ位置）を比べるだけで、実際のベイクは
                    //  「署名が変わってから数百 ms 静まった最初のフレーム」1 回だけ走る。
                    //  ブラシのドラッグ中に数百 ms のベイクが挟まらないのが狙い。
                    //  岸波を切っている（strength=0）水域は署名すら作られず、コストは 0。
                    //
                    // 【地形の読み方】散布プロップの接地判定と同一のサンプラ
                    //  （`TerrainScatterField` → `sample_density_world`）を使う。別実装にすると
                    //  「草は生えているのに岸波が陸へ乗る」ずれが出る。
                    {
                        // 地形チャンクの実在範囲（AABB）。カラム走査の早期棄却に使う。
                        // 地形が 1 チャンクも無ければ `None`＝岸が定義できないので焼かない。
                        let shore_bounds = terrain_world_bounds(&self.terrain);
                        let shore_field_view =
                            super::terrain_scatter_ops::TerrainScatterField::from_state(&self.terrain);
                        self.water_shore_fields.update(
                            &water_volumes,
                            [saved_camera_pos[0], saved_camera_pos[2]],
                            &shore_field_view,
                            shore_bounds,
                            self.terrain_edit_version,
                            std::time::Instant::now(),
                        );
                    }

                    // ── 瞬発インタラクションフィールドの更新（Phase I1）────────────────
                    //
                    // 【なぜここか】草は「カメラプレビュー」と「メインパス」の両方で描かれ、
                    //  どちらも場を頂点段でサンプルする。両者より前（＝どのレンダーパスも
                    //  始まっていないこの位置）でコンピュートを記録すれば、1 回の更新で
                    //  両方が同じ場を読める。
                    //
                    // 【Edit でも動く】減衰も速度算出も `ctx.delta_time`（壁時計）で駆動するため、
                    //  Edit 中にアクタをドラッグしても草が反応し、離せば数秒で元へ戻る。
                    //  草の風（GrassUniform.time = shader_time）とは独立した時間源である。
                    //
                    // 【遅延構築】草バッファもソースも無いフレームでは構築すらしない
                    //  （場テクスチャ 4MB とコンピュートパイプラインの常駐コストを回避）。
                    //  草バッファがあるなら必ず構築される＝描画側は場の不在を考えなくてよい。
                    {
                        let perf_t_interact = std::time::Instant::now();
                        // ① シーンから書き手を集めてワールド解決する。
                        let sources = crate::engine::interaction::collect_interaction_sources(
                            &scene.actors, &scene.world, self.active_world_line,
                        );
                        // ② 前フレーム位置との差分から速度（m/s）を確定させる。
                        let mut moving = self.interaction_velocity.update(&sources, ctx.delta_time);
                        // ②' 水面付近のソースへ波の注入量を割り当てる（Phase I2）。
                        //     「水域内かつ水面±半径」にいるソースだけが波を立てる。
                        //     Y の照合は俯瞰 2D の場では原理的にできないため、焼く前に CPU で決める。
                        crate::engine::interaction::apply_water_wave_injection(
                            &mut moving, &water_volumes,
                        );
                        // ②'' 水位グラフ（W2.5）の流量を波源として追加する。
                        //
                        //  【なぜ apply_water_wave_injection の "後" か】
                        //  あの関数は全ソースの `wave_amplitude` を上書きする
                        //  （水面付近にいないソースを 0 に潰す）。先に足すと消されるし、
                        //  そもそも流量由来の波は「ソースが水面付近にいるか」ではなく
                        //  「開口をどれだけの水が通ったか」で決まるので、判定を通す意味が無い。
                        //
                        //  【ECS コンポーネントを経由しない理由】
                        //  波源の実体はシーン上のアクタではなく「今フレームの流量」という
                        //  計算結果である。`MovingInteractionSource` は公開フィールドのみの
                        //  単純な値型なので、その場で組んで同じ配列へ流し込めば、
                        //  以降の焼き込み（`InteractionFieldRenderer::update`）は
                        //  コンポーネント由来のソースと 1 ミリも区別しない。
                        for ev in self.water_level_sim.flow_events() {
                            moving.push(
                                crate::engine::interaction::flow_event_wave_source(ev));
                        }
                        // ③ 必要になった時点で GPU リソースを構築する。
                        //    水ボリュームがあるフレームも構築対象に含める（Phase I2）。
                        //    水面パスは group2 で場を読むため、**水があるなら場は必ず在る**方が
                        //    描画側の分岐が減る（フォールバックは最後の保険として残す）。
                        let need_field = !moving.is_empty()
                            || !self.terrain.grass_buffers.is_empty()
                            || !water_volumes.is_empty();
                        if need_field && self.interaction_field.is_none() {
                            self.interaction_field = Some(
                                crate::engine::core::renderer::InteractionFieldRenderer::new(
                                    &draw_ctx.device, None,
                                ),
                            );
                        }
                        // ④ 場を 1 ディスパッチで更新する（再マップ → 減衰 → スタンプ）。
                        if let Some(field) = self.interaction_field.as_mut() {
                            field.update(
                                &draw_ctx.queue,
                                frame.encoder_mut(),
                                &moving,
                                // 水域ごとの物性（粘度・波紋の減衰率。Phase I2.1）を
                                // 場へ渡す。場は全水域で 1 枚を共有するため、
                                // 「どのテクセルがどの水域か」を毎フレーム教える必要がある。
                                &water_volumes,
                                saved_camera_pos,
                                ctx.delta_time,
                            );
                        }
                        perf_interact_ms = perf_t_interact.elapsed().as_secs_f64() * 1000.0;
                    }

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
                    /// 同一 source_path を持つ全 MC を 1 バッチへ統合するための集約先。
                    struct MergeInfo {
                        cpu_model: std::sync::Arc<crate::engine::core::loader::model::Model>,
                        mats:      Vec<[[f32; 4]; 4]>,
                        /// 統合インスタンス i の Animator 駆動権威時刻（None = 静止・先頭フレーム凍結）。
                        /// ModelComponent::anim_drive 由来。同一 MC の全インスタンスに同じ値を複製する。
                        time_overrides: Vec<Option<f32>>,
                        /// 統合インスタンス i の絶対 ID（元 MC の id_base + 元インスタンス idx）
                        abs_ids:   Vec<u32>,
                        /// 統合インスタンス i のセマンティックタグ（0..15。0 = タグ無し）。
                        /// `ModelComponent::render_tag` 由来で、同一 MC の全インスタンスに同じ値を複製する
                        /// （タグはアクタ単位の属性であり、インスタンス個別には持たせない）。
                        render_tags: Vec<u8>,
                    }
                    // (dfs_id, slot_i) → (source_path, merged_start, n_instances)
                    // アウトライン描画時に統合バッチ内のインスタンス範囲を特定するために使う。
                    // merged_start: このMCの先頭インスタンスの統合バッチ内インデックス
                    // n_instances:  このMCのインスタンス数
                    let mut mc_outline_map: std::collections::HashMap<
                        (u32, usize),
                        (String, u32, u32),
                    > = std::collections::HashMap::new();
                    // merge_map 構築＋全統合バッチ update() の CPU 時間を計測する（merge バケット）。
                    let _perf_t_merge = std::time::Instant::now();
                    let merge_map: std::collections::HashMap<String, MergeInfo> = {
                        let mut map: std::collections::HashMap<String, MergeInfo>
                            = std::collections::HashMap::new();
                        for &(id_base, dfs_id, slot_i, amc) in &all_mcs {
                            if amc.source_path.is_empty()  { continue; }
                            if amc.gpu_model.is_none()     { continue; }
                            let Some(arc_m) = amc.model.as_ref() else { continue };
                            // Phase R7: マージキーは「モデルパス＋マテリアルオーバーライド署名」。
                            // オーバーライド無し（署名空）の MC は batch_key == source_path となり、
                            // 従来どおり 1 バッチへ統合される（描画経路・性能ともに不変）。
                            // オーバーライドを持つ MC は署名が異なるため別バッチへ分離され、
                            // その代表 GpuModel（各 MC が自前で焼き込み済み）で描画される＝方式(a)。
                            let batch_key = amc.batch_key();
                            let e = map.entry(batch_key.clone())
                                .or_insert_with(|| MergeInfo {
                                    cpu_model: arc_m.clone(),
                                    mats:      Vec::new(),
                                    time_overrides: Vec::new(),
                                    abs_ids:   Vec::new(),
                                    render_tags: Vec::new(),
                                });
                            // この MC が Animator 駆動中なら権威時刻を、そうでなければ None を
                            // 全インスタンス分複製する（インスタンスは同一アニメを共有再生する）。
                            let mc_time_override = amc.anim_drive.map(|d| d.time);
                            // このMCが統合バッチに追加される前の先頭インデックスを記録する
                            let merged_start = e.mats.len() as u32;
                            let n_insts      = amc.instance_mats.len() as u32;
                            mc_outline_map.insert(
                                (dfs_id, slot_i),
                                (batch_key, merged_start, n_insts),
                            );
                            for (inst_i, &mat) in amc.instance_mats.iter().enumerate() {
                                e.mats.push(mat);
                                e.time_overrides.push(mc_time_override);
                                // abs_id = MC の id_base + このインスタンスのオフセット
                                e.abs_ids.push(id_base + inst_i as u32);
                                // タグは MC 単位の属性なので、この MC の全インスタンスへ同値を複製する。
                                e.render_tags.push(amc.render_tag);
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
                                // 新規/再生成バッチは未アップロード。次の update で確定させる。
                                uploaded_sig: None,
                            });
                        }
                        if let Some(sd) = self.shared_model_batches.get_mut(path) {
                            // ── 静的地形チャンクの update スキップ（Fix B）──────────────
                            // 地形チャンクは静的で、変形/remesh のときだけメッシュと mats が
                            // 変わる。remesh 時は batch_key（合成 source_path=ジオメトリ由来）
                            // 自体が変わり別バッチになるため、同一キーで mats が前フレームと
                            // 一致するなら描画は完全に不変。毎フレームの mark_dirty()+update()
                            // （rayon 行列再計算・全ノードバッファ書込・id バッファ書込）を丸ごと
                            // 省ける。地形は独自の remesh ベース LOD を持ち、汎用 per-instance
                            // 距離 LOD（バケット振り分け）は 1 インスタンス・単一 LOD メッシュの
                            // チャンクでは描画に影響しないため、カメラ移動時の再バケット省略も
                            // 視覚的に安全。通常モデル/散布/アニメは非地形キー or mats 変化のため
                            // 従来どおり毎フレーム更新される（挙動不変）。
                            let is_terrain = path.starts_with(
                                crate::engine::components::TERRAIN_SOURCE_SCHEME);
                            let unchanged = is_terrain
                                && sd.uploaded_sig.as_ref().is_some_and(|(m, a)| {
                                    m.as_slice() == info.mats.as_slice()
                                        && a.as_slice() == info.abs_ids.as_slice()
                                });
                            if unchanged {
                                // 前フレームと同一（行列・ID とも）: 既存の lod バッファ・
                                // compact をそのまま使い、更新を丸ごと省く。
                                continue;
                            }
                            {
                                // フィールド分割借用: batch は可変、cpu_model は不変
                                let batch     = &mut sd.batch;
                                let cpu_model = &sd.cpu_model;
                                // Animator 駆動の権威時刻を反映する（非駆動インスタンスは静止）。
                                batch.set_anim_time_overrides(&info.time_overrides);
                                batch.mark_dirty();
                                // 速度バッファ: フレーム間の連続性が切れたフレーム
                                // （Play⇄Edit 切替・ポーズ切替・RT リサイズ）は
                                // 前フレーム行列を使わせない（prev=curr ⇒ 速度 0）。
                                if velocity_reset_frame { batch.request_velocity_reset(); }
                                batch.update(
                                    &draw_ctx.queue,
                                    cpu_model,
                                    &info.mats,
                                    // セマンティックタグ（統合インスタンス順・MC 単位の値を複製済み）。
                                    &info.render_tags,
                                    saved_camera_pos,
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
                            // 地形キーは今回アップロードした mats・abs_ids を記録し、
                            // 次フレームの一致判定に使う。非地形は記録しない（None のまま＝常に更新）。
                            if is_terrain {
                                sd.uploaded_sig = Some((info.mats.clone(), info.abs_ids.clone()));
                            }
                        }
                    }
                    perf_merge_ms = _perf_t_merge.elapsed().as_secs_f64() * 1000.0;

                    // ─── stale 統合バッチ／BLAS キャッシュの遅延 prune ──────────────
                    // マテリアルのインライン編集（スライダードラッグ等）は署名が変わるたびに
                    // 新しい batch_key を生む。以前は shared_model_batches も RT の BLAS キャッシュも
                    // 追加のみで削除が無く、編集のたびに巨大な GPU リソースが無制限に蓄積して
                    // 数秒で VRAM が枯渇（OOM パニック）していた。ここで「このフレームの描画対象
                    // （merge_map のキー）に STALE_BATCH_PRUNE_FRAMES 連続で不在」の batch_key を解放する。
                    // alive 集合（merge_map のキー）由来なので、解放するのは「もう描かれていない」
                    // ものだけ＝描画結果は不変。遅延方式で非同期ロード時の誤解放も避ける。
                    {
                        let alive: std::collections::HashSet<String> =
                            merge_map.keys().cloned().collect();
                        let cache_keys: std::collections::HashSet<String> =
                            self.shared_model_batches.keys().cloned().collect();
                        let freed = compute_stale_batch_prune(
                            &cache_keys,
                            &alive,
                            &mut self.batch_absent_frames,
                            STALE_BATCH_PRUNE_FRAMES,
                        );
                        if !freed.is_empty() {
                            // ① 統合バッチ本体（インスタンスバッファ・GpuModel 借用元は MC 側）を解放。
                            for k in &freed {
                                self.shared_model_batches.remove(k);
                            }
                            // ② RT の BLAS キャッシュ／警告集合も同一キー（BlasKey.source_path == batch_key）で
                            //    追従解放する。TLAS は毎フレーム casters から詰め直すため、キャッシュから
                            //    消えれば自然に登録されなくなる（rt_shadow.rs prepare_and_build 参照）。
                            let mut freed_blas = 0usize;
                            if let Some(rt_cell) = draw_ctx.rt_shadow.as_ref() {
                                freed_blas = rt_cell.borrow_mut().prune_source_paths(&freed);
                            }
                            // リーク再発の可視化: 解放が走ったフレームのみログ（定常時は無出力）。
                            eprintln!(
                                "[SEED PRUNE] stale 統合バッチ {} 件を解放（追従 BLAS {} 件）。\
                                 マテリアル編集で蓄積したユニーク署名キーの回収です。",
                                freed.len(), freed_blas
                            );
                        }
                    }

                    // ③ draw 時に参照する batch_key → &GpuModel マッピング
                    // 各 batch_key（モデルパス＋オーバーライド署名）の代表 MC の GpuModel を借用する。
                    // 同一 batch_key の全 MC は同一 GPU データ（オーバーライド焼き込み済み）を持つため
                    // どれを代表に選んでも等価。オーバーライド無しなら batch_key==source_path で従来と同一。
                    // キー型は batch_key() が所有 String を返すため &str ではなく String とする。
                    let gpu_model_by_path: std::collections::HashMap<
                        String,
                        &crate::engine::methods::drawer::GpuModel,
                    > = all_mcs.iter()
                        .filter_map(|&(_, _, _, amc)| {
                            if amc.source_path.is_empty() { return None; }
                            amc.gpu_model.as_ref()
                                .map(|gpu| (amc.batch_key(), gpu))
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
                        // ── 散布モデル（kind=Model）のスキンも同じパスで dispatch する ──────
                        // 【なぜ必要か（Phase RT-Skin で顕在化）】散布モデルのバッチも
                        // `create_instanced_batch` が作るため、glTF にスキン＋アニメーションが
                        // あれば `SkinComputeSystem` が生成される。しかしここまで dispatch 対象は
                        // `shared_model_batches` だけで、散布側のジョイント行列バッファは
                        // **ゼロのまま**だった（＝頂点が原点へ潰れる）。ラスタ描画でも壊れていたが、
                        // 散布モデルも RT キャスターに含まれるため、RT スキン BLAS では
                        // 「原点に潰れた巨大な退化ジオメトリ」が TLAS に入り、反射・GI・影に
                        // シーン全体規模の破綻を出しうる。dispatch はスキン無しバッチでは
                        // 即 return するので、スキンを持たない通常の散布プロップにコスト増は無い。
                        for res in self.terrain.scatter_models.values() {
                            res.batch.dispatch_skin(
                                &mut skin_pass,
                                &draw_ctx.pipelines.skin_compute,
                            );
                        }
                    } // skin_pass がここでドロップされ ComputePass が終了する
                    perf_skin_ms = _perf_t_skin.elapsed().as_secs_f64() * 1000.0;

                    // ── GPU パーティクル: バッファ同期＋シミュレーション dispatch（Phase RP）──
                    // skin compute と同時期に行う。sync_gpu（バッファ確保・params 書込・テクスチャ
                    // 差し替え）→ 専用 compute pass で全エミッタを dispatch する。CPU 側の放出決定・
                    // pending_burst 消費は描画ブロック前（collect_and_consume）で済ませてある。
                    // エミッタ 0 個なら sync_gpu/dispatch とも即 return（バッファ確保なし＝コスト増ゼロ）。
                    if self.particle_system.has_emitters() {
                        self.particle_system.sync_gpu(
                            &draw_ctx.device, &draw_ctx.queue,
                            &draw_ctx.pipelines.particle_compute,
                            &draw_ctx.pipelines.particles,
                        );
                        let mut particle_pass = frame.encoder_mut().begin_compute_pass(
                            &wgpu::ComputePassDescriptor {
                                label:            Some("Particle Sim Pass"),
                                timestamp_writes: None,
                            },
                        );
                        self.particle_system.dispatch(
                            &mut particle_pass, &draw_ctx.pipelines.particle_compute,
                        );
                    } // particle_pass がドロップされ ComputePass が終了する

                    // ── 地形チャンク単位フラスタム＋距離カリング（Terrain 描画最適化）──────
                    //   地形は「terrain:// の独立 ModelComponent バッチ」をチャンク数（16×16 で
                    //   256+）だけ持ち、以前は視界外・背後のチャンクまで毎フレーム全描画していた
                    //   （地形のみで 30fps 割れの直接原因）。ここでメインカメラ視錐台＋距離で
                    //   「完全に外側」のチャンクバッチを集め、この後の G-Buffer 描画・メッシュレット
                    //   カリング前処理からスキップする（可視ぶんだけ描く）。
                    //
                    //   判定 AABB は各バッチの update() が算出済みのワールドメッシュ AABB
                    //   （`world_bounds`）＝実ジオメトリを厳密に包む。したがって p-vertex 法
                    //   （`aabb_outside_frustum`・偽陽性ゼロ）と合わせ、視界内チャンクを誤って
                    //   消すことは絶対に無い（撤去された旧オブジェクト単位カリングの誤棄却は再発しない）。
                    //   対象は「terrain://」で始まる地形バッチのみ（通常アクター・散布は挙動不変）。
                    //   ★影パスはライト視錐台が別物なので、ここ（メインカメラ視錐台）では触らない
                    //     ——カメラ視界外でも視界内へ影を落とすチャンクがあるため、影キャスタを
                    //     カメラ視錐台で棄却すると影が欠ける。影の緩和は今回スコープ外（申し送り）。
                    let terrain_cull_disabled = *super::terrain_scatter_ops::TERRAIN_CULL_DISABLED;
                    let terrain_cull_dist = *super::terrain_scatter_ops::TERRAIN_CHUNK_CULL_DISTANCE;
                    let terrain_cull_dist_sq = terrain_cull_dist * terrain_cull_dist;

                    // ── Hi-Z オクルージョン: 前フレーム結果の取得（1 フレーム遅延・opt-in）──────
                    //   SEED_OCCLUSION_CULL=1 のときのみ。前フレームにディスパッチした地形チャンクの
                    //   可視性（0=遮蔽 / 1=可視）を読み戻し、hiz_prev_keys と同順で対応づけて
                    //   「完全遮蔽」チャンクの path 集合を作る。結果が無い（未収束・未対応・非 deferred）
                    //   チャンクは一切含めない＝「不明なら描く」保守側（誤棄却防止の要）。
                    //   ここで得た遮蔽集合は下でフラスタム／距離カリング集合へ union する。
                    let terrain_occluded: std::collections::HashSet<String> =
                        if *super::terrain_scatter_ops::HIZ_OCCLUSION_ENABLED {
                            if let Some(hiz) = self.hiz.as_mut() {
                                if let Some(vis) = hiz.try_read_results(&draw_ctx.device) {
                                    self.hiz_prev_keys.iter().zip(vis.iter())
                                        .filter(|(_, v)| **v == 0)
                                        .map(|(k, _)| k.clone())
                                        .collect()
                                } else { std::collections::HashSet::new() }
                            } else { std::collections::HashSet::new() }
                        } else { std::collections::HashSet::new() };
                    // 計測: Hi-Z が読み戻した可視性から落とした地形チャンク数（60 フレームに 1 回）。
                    if *super::terrain_scatter_ops::HIZ_OCCLUSION_ENABLED
                        && *super::terrain_scatter_ops::PERF_TERRAIN_LOG_ENABLED {
                        use std::sync::atomic::{AtomicU64, Ordering};
                        static HN: AtomicU64 = AtomicU64::new(0);
                        if HN.fetch_add(1, Ordering::Relaxed) % 60 == 0 {
                            eprintln!(
                                "[PERF hiz] occlusion read: occluded={} tested_prev={}",
                                terrain_occluded.len(), self.hiz_prev_keys.len()
                            );
                        }
                    }
                    // 診断用: このフレームの地形チャンクバッチ総数（描画数ログの分母）。
                    let terrain_chunk_total = self.shared_model_batches.keys()
                        .filter(|p| p.starts_with(
                            crate::engine::components::TERRAIN_SOURCE_SCHEME))
                        .count() as u32;
                    // 視錐台外／距離外の地形チャンク path 集合（描画・前処理でスキップする）。
                    let mut terrain_culled: std::collections::HashSet<String> = if terrain_cull_disabled {
                        std::collections::HashSet::new()
                    } else {
                        self.shared_model_batches.iter()
                            .filter(|(path, _)| path.starts_with(
                                crate::engine::components::TERRAIN_SOURCE_SCHEME))
                            .filter_map(|(path, sd)| {
                                // update() 未実行（world_bounds=None）なら安全側に「描く」扱い。
                                let (min, max) = sd.batch.world_bounds()?;
                                let culled = crate::engine::core::renderer::gpu_resources::chunk_culled_by_camera(
                                    &saved_frustum_planes, saved_camera_pos,
                                    min, max, terrain_cull_dist_sq,
                                );
                                if culled { Some(path.clone()) } else { None }
                            })
                            .collect()
                    };
                    // Hi-Z オクルージョン結果を union（フラスタム／距離 AND 遮蔽）。遮蔽集合は
                    // 保守側（完全遮蔽のみ）なので、視界内でも山の裏なら追加でスキップされる。
                    // 空集合（既定 OFF・未収束）なら何も足さず＝フラスタムのみの従来挙動。
                    terrain_culled.extend(terrain_occluded);
                    // 計測ログ（60 フレームに 1 回・SEED_PERF_TERRAIN 有効時のみ）。
                    //   カリング後に実際に描く地形チャンク数 / 全チャンク数。
                    if *super::terrain_scatter_ops::PERF_TERRAIN_LOG_ENABLED && terrain_chunk_total > 0 {
                        use std::sync::atomic::{AtomicU64, Ordering};
                        static TN: AtomicU64 = AtomicU64::new(0);
                        if TN.fetch_add(1, Ordering::Relaxed) % 60 == 0 {
                            let drawn = terrain_chunk_total - terrain_culled.len() as u32;
                            eprintln!(
                                "[PERF terrain] chunk draw: drawn={drawn}/{terrain_chunk_total} \
                                 (nocull={terrain_cull_disabled})"
                            );
                        }
                    }
                    // PLAY_DIAG: Play 先頭数フレームで「ゲームカメラ視錐台で地形が何チャンク
                    // 描画に残るか」を出す。0/N なら黒画面の原因は『ゲームカメラ視錐台で全地形が
                    // カリングされている』側（カメラ向き・視錐台計算）。N/N 近くなら地形は描かれて
                    // おり原因はビューポート／プレゼント側、と切り分けられる。
                    if *PLAY_DIAG_ENABLED && self.mode == RuntimeMode::Play && !self.paused
                        && PLAY_DIAG_FRAME.load(Ordering::Relaxed) <= PLAY_DIAG_FRAMES
                        && terrain_chunk_total > 0
                    {
                        let drawn = terrain_chunk_total - terrain_culled.len() as u32;
                        let mc_batches = self.shared_model_batches.len();
                        eprintln!(
                            "[PLAY_DIAG] terrain_drawn={drawn}/{terrain_chunk_total} \
                             shared_model_batches={mc_batches} (nocull={terrain_cull_disabled})"
                        );
                    }

                    // ── GPU メッシュレットカリング（第1弾）: 前処理＋ compute ディスパッチ ──
                    // meshlet_active（設定オン かつ MULTI_DRAW_INDIRECT_COUNT 対応）のときのみ。
                    // 前処理: 可視 LOD0 インスタンス × メッシュレットのパラメータ更新・カウント 0 リセット・
                    //         BindGroup 構築。compute: 生存メッシュレットを間接コマンドへ詰める。
                    // 出力（cmd/count バッファ）はメインパスの multi_draw_indexed_indirect_count が読む。
                    if meshlet_active {
                        // 前処理（BindGroup 構築・パラメータ更新）。batch は可変、GpuModel は
                        // gpu_model_by_path から借用（all_mcs 由来＝shared_model_batches と非交差）。
                        for (path, sd) in self.shared_model_batches.iter_mut() {
                            if !sd.batch.has_meshlet_slots() { continue; }
                            // 視錐台外の地形チャンクは描かないので、メッシュレットカリング compute も
                            // 走らせない（スロットをリセットして record 側の dispatch を確実に止める）。
                            if terrain_culled.contains(path.as_str()) {
                                sd.batch.reset_meshlet_cull();
                                continue;
                            }
                            if let Some(&gpu) = gpu_model_by_path.get(path.as_str()) {
                                perf_meshlet_considered += sd.batch.prepare_meshlet_cull(
                                    &draw_ctx.queue,
                                    &draw_ctx.device,
                                    gpu,
                                    &draw_ctx.pipelines.meshlet_cull.bgl,
                                    &saved_frustum_planes,
                                    saved_camera_pos,
                                );
                            }
                        }
                        // ── 散布モデル（kind=Model プロップ）も同じメッシュレットカリングへ乗せる ──
                        //   散布モデルのバッチは create_instanced_batch（enable_meshlet_cull=true）で
                        //   作られ、インスタンス数が上限内ならスロットを確保している（超える prim は
                        //   InstancedModelBatch::new 側の防御でスロット未確保＝通常描画へ自動フォールバック）。
                        //   ここで前処理し、下の compute で可視 LOD0 メッシュレットだけを間接コマンドへ詰める。
                        //   これで近景の高ポリ木がアクター（shared_model_batches）と同等に軽くなる。
                        //   スロットを 1 つも持たないバッチ（大量散布でフォールバックした木・草由来の
                        //   モデル等）は has_meshlet_slots()==false で即スキップ（追加コストゼロ）。
                        //   カリング済み可視集合は cull_and_update_scatter_models が既に update 済み。
                        //   env トグル（SEED_SCATTER_MESHLET=0）で無効化可（計測用・既定 ON）。
                        let scatter_meshlet = *super::terrain_scatter_ops::SCATTER_MESHLET_CULL_ENABLED;
                        if scatter_meshlet {
                            for res in self.terrain.scatter_models.values_mut() {
                                if !res.batch.has_meshlet_slots() { continue; }
                                perf_meshlet_considered += res.batch.prepare_meshlet_cull(
                                    &draw_ctx.queue,
                                    &draw_ctx.device,
                                    &res.gpu_model,
                                    &draw_ctx.pipelines.meshlet_cull.bgl,
                                    &saved_frustum_planes,
                                    saved_camera_pos,
                                );
                            }
                        }
                        // compute ディスパッチ（前処理で active になったスロットのみ）。
                        if perf_meshlet_considered > 0 {
                            let mut cull_pass = frame.encoder_mut().begin_compute_pass(
                                &wgpu::ComputePassDescriptor {
                                    label:            Some("Meshlet Cull Pass"),
                                    timestamp_writes: None,
                                },
                            );
                            for sd in self.shared_model_batches.values() {
                                sd.batch.record_meshlet_cull(
                                    &mut cull_pass, &draw_ctx.pipelines.meshlet_cull,
                                );
                            }
                            // 散布モデルのカリング compute も同じパスで発行する。
                            for res in self.terrain.scatter_models.values() {
                                res.batch.record_meshlet_cull(
                                    &mut cull_pass, &draw_ctx.pipelines.meshlet_cull,
                                );
                            }
                        } // cull_pass ドロップで ComputePass 終了
                    }

                    // ── スカイボックス: GPU 同期（Phase R9）────────────────────
                    // uniform バッファ・BindGroup の確保／更新とテクスチャロードを行う。
                    // 描画はメインパスの最初（begin_scene_pass_to 直後）で行う。0 個なら即 return。
                    if self.skybox_system.has_skyboxes() {
                        self.skybox_system.sync_gpu(
                            &draw_ctx.device, &draw_ctx.queue,
                            &draw_ctx.pipelines.skybox,
                        );
                    }

                    // ── カメラシーンギズモ（Edit モード・3D シーンのみ）──────────
                    // カメラアイコン / フラスタム / プレビューはアクター編集 2D タブ・
                    // 2D シーンビュー以外で表示する。
                    // WL 0 に 2D アクターが混在していても 3D カメラギズモを表示する。
                    let is_3d_scene = in_editor && !use_ortho_2d_camera;
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
                    // ライト/パーティクルギズモモデルのレイジーロードと再生成判定
                    // （カメラギズモと同じ camera.glb を暫定アイコンとして流用する）。
                    // 同一 GLB でもバッチはギズモ種別ごとに独立させる（ID 範囲・行列が別のため）。
                    if is_3d_scene
                        && (!light_gizmo_actor_mats.is_empty() || !particle_gizmo_actor_mats.is_empty())
                    {
                        if let Some(ar) = self.editor_resources.clone() {
                            let model_path = format!("{}/models/camera.glb", ar);
                            // CPU モデルをキャッシュから取得するか、なければロードする
                            let cpu_model_opt: Option<std::sync::Arc<crate::engine::core::loader::model::Model>> = {
                                let mut cache = draw_ctx.model_cache.borrow_mut();
                                if !cache.contains_key(&model_path) {
                                    let path = std::path::Path::new(&model_path);
                                    match crate::engine::core::loader::load_model(path) {
                                        Ok(m)  => { cache.insert(model_path.clone(), std::sync::Arc::new(m)); }
                                        Err(e) => { eprintln!("[SEED] icon gizmo model load failed: {e}"); }
                                    }
                                }
                                cache.get(&model_path).cloned()
                            };
                            if let Some(cpu) = cpu_model_opt {
                                // ライトギズモ: バッチ容量が不足している場合は再生成する
                                if !light_gizmo_actor_mats.is_empty() {
                                    let need_reinit = self.light_gizmo.as_ref()
                                        .map(|g| g.capacity < light_gizmo_actor_mats.len())
                                        .unwrap_or(true);
                                    if need_reinit {
                                        let cap = (light_gizmo_actor_mats.len() * 2).max(4);
                                        let gpu_model = draw_ctx.upload_model(&cpu);
                                        let batch     = draw_ctx.create_instanced_batch(&cpu, cap as u32);
                                        self.light_gizmo = Some(CameraGizmoResources {
                                            cpu_model: cpu.clone(), gpu_model, batch, capacity: cap,
                                        });
                                    }
                                }
                                // パーティクルエミッタギズモ: 同上
                                if !particle_gizmo_actor_mats.is_empty() {
                                    let need_reinit = self.particle_gizmo.as_ref()
                                        .map(|g| g.capacity < particle_gizmo_actor_mats.len())
                                        .unwrap_or(true);
                                    if need_reinit {
                                        let cap = (particle_gizmo_actor_mats.len() * 2).max(4);
                                        let gpu_model = draw_ctx.upload_model(&cpu);
                                        let batch     = draw_ctx.create_instanced_batch(&cpu, cap as u32);
                                        self.particle_gizmo = Some(CameraGizmoResources {
                                            cpu_model: cpu.clone(), gpu_model, batch, capacity: cap,
                                        });
                                    }
                                }
                            }
                        }
                    }

                    // カメラ/ライト/パーティクルギズモバッチを毎フレーム更新する
                    // （インスタンス変換・視錐台カリング）
                    if is_3d_scene
                        && (!cam_gizmo_actor_mats.is_empty()
                            || !light_gizmo_actor_mats.is_empty()
                            || !particle_gizmo_actor_mats.is_empty())
                    {
                        let cp  = self.camera.position();
                        let cpo = [cp.x, cp.y, cp.z];
                        // (行列リスト, 対象ギズモ) の組を順に更新する。
                        // オブジェクト視錐台カリングは撤去済みのためギズモアイコンは常に全表示。
                        let updates: [(&Vec<(usize, [[f32; 4]; 4])>, &mut Option<CameraGizmoResources>); 3] = [
                            (&cam_gizmo_actor_mats,      &mut self.camera_gizmo),
                            (&light_gizmo_actor_mats,    &mut self.light_gizmo),
                            (&particle_gizmo_actor_mats, &mut self.particle_gizmo),
                        ];
                        for (mats, gizmo_slot) in updates {
                            if mats.is_empty() { continue; }
                            let transforms: Vec<[[f32; 4]; 4]> =
                                mats.iter().map(|&(_, m)| m).collect();
                            if let Some(gizmo) = gizmo_slot {
                                gizmo.batch.mark_dirty();
                                gizmo.batch.update(
                                    &draw_ctx.queue,
                                    &gizmo.cpu_model,
                                    &transforms,
                                    // ギズモはアクタではなくタグを持たない（全インスタンス 0 扱い）。
                                    &[],
                                    cpo,
                                );
                            }
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

                    // 選択中ライトアクターのギズモ（種別ごとの範囲ワイヤ・矢印、3D シーン）。
                    let light_gizmo_batch = if is_3d_scene {
                        super::light_scene_gizmo::build_selected_light_gizmo_batch(
                            &scene.actors, &scene.world,
                            self.active_world_line,
                            self.actor_virtual_selected_idx,
                            &draw_ctx.device,
                        )
                    } else { None };

                    // 選択中ジョイントアタッチアクターのギズモ（ソケット位置の RGB 軸十字、3D シーン）。
                    let jointattach_gizmo_batch = if is_3d_scene {
                        super::jointattach_scene_gizmo::build_selected_jointattach_gizmo_batch(
                            &scene.actors, &scene.world,
                            self.active_world_line,
                            self.actor_virtual_selected_idx,
                            &draw_ctx.device,
                        )
                    } else { None };

                    // 選択中パーティクルエミッタアクターのギズモ（放出円錐ワイヤ、3D シーン）。
                    let particle_gizmo_batch = if is_3d_scene {
                        super::particle_scene_gizmo::build_selected_particle_gizmo_batch(
                            &scene.actors, &scene.world,
                            self.active_world_line,
                            self.actor_virtual_selected_idx,
                            &draw_ctx.device,
                        )
                    } else { None };

                    // 選択中スカイボックスアクターのギズモ（WorldAnchored の配置ワイヤ球、3D シーン）。
                    let skybox_gizmo_batch = if is_3d_scene {
                        super::skybox_scene_gizmo::build_selected_skybox_gizmo_batch(
                            &scene.actors, &scene.world,
                            self.active_world_line,
                            self.actor_virtual_selected_idx,
                            &draw_ctx.device,
                        )
                    } else { None };

                    // カメラプレビューリソースを初期化・更新する
                    if let Some(ref cam_data) = selected_cam_data {
                        // プレビューテクスチャサイズをカメラのアスペクト比に合わせて算出する。
                        // 高さを CAMERA_PREVIEW_H に固定し、幅をアスペクト比から導出する。
                        let aspect = cam_data.target_aspect();
                        let preview_h = CAMERA_PREVIEW_H;
                        let preview_w = ((CAMERA_PREVIEW_H as f32 * aspect).round() as u32).max(1);

                        // カメラのターゲットサイズが変わった場合はリソースを作り直す。
                        let needs_recreate = self.camera_preview.is_none()
                            || self.camera_preview_target_size
                                != Some((cam_data.target_width, cam_data.target_height));
                        if needs_recreate {
                            self.camera_preview = Some(CameraPreviewResources::new(
                                &draw_ctx.device,
                                &draw_ctx.pipelines.camera_preview_blit,
                                preview_w, preview_h,
                            ));
                            self.camera_preview_target_size =
                                Some((cam_data.target_width, cam_data.target_height));
                        }
                        // ブリット矩形をカメラのアスペクト比に合わせたサイズで更新する
                        if let Some(ref preview) = self.camera_preview {
                            preview.update_blit_rect(
                                &draw_ctx.queue, vp_w_f, vp_h_f,
                                preview_w as f32, preview_h as f32,
                            );
                        }
                    } else {
                        // 選択解除時はリソースを破棄する（メモリ節約）
                        self.camera_preview = None;
                        self.camera_preview_target_size = None;
                    }

                    // カメラプレビューレンダーパス（選択カメラのビューで全 MC を描画）
                    if let (Some(cam_data), Some(preview)) =
                        (selected_cam_data.as_ref(), self.camera_preview.as_ref())
                    {
                        // テクスチャサイズをアスペクト比から再計算する（上記と同一ロジック）
                        let prev_h = CAMERA_PREVIEW_H as f32;
                        let prev_w = (prev_h * cam_data.target_aspect()).round().max(1.0);
                        let res = [prev_w, prev_h];
                        // プロジェクション行列もテクスチャのアスペクト比に合わせる
                        let preview_aspect = cam_data.target_aspect();
                        let cam_uniform = camera_scene_gizmo::build_camera_uniform(
                            // カメラプレビューもメインカメラと同じ切替（Edit 中は壁時計）に揃える。
                            cam_data, preview_aspect, res, shader_time,
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
                            // (行列, カラー, テクスチャ, 描画ゾーン, レイヤー) の収集バッファ。
                            // ワールドキャンバスはゾーン概念を持たないためゾーンは無視し、
                            // レイヤーのみキャンバス単位で安定ソートに使用する。
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
                                    // このキャンバス内の追加分をレイヤー昇順で安定ソートする
                                    // （ワールドキャンバスのレイヤーはキャンバス内で完結する）
                                    let canvas_start = items.len();
                                    collect_sprite_items(
                                        &actor.children, &s.world, wl, draw_ctx,
                                        Some([cc.width, cc.height]),
                                        ctw, [1.0, 1.0],
                                        1.0, 1.0,
                                        // ワールドキャンバスは自動解像度の対象外（空マップ）・ゾーン概念なし
                                        None, &std::collections::HashMap::new(),
                                        &std::collections::HashMap::new(),
                                        // 3D ワールドキャンバス配下は常に親サイズ Some のためルート分岐に入らず design_space 無関係
                                        CanvasDrawZone::Foreground, false, &mut items,
                                    );
                                    items[canvas_start..].sort_by_key(|&(_, _, _, _, layer)| layer);
                                }
                            }
                            // ゾーン・レイヤーを除いた (行列, カラー, テクスチャ) を
                            // preview チャンネルへ積み、テクスチャ境界でバッチ分割する（Phase R6）。
                            // preview はメインのスプライト収集より前に記録されるため、
                            // メインとは別の永続バッファ（preview ストリーム）を使う。
                            let mut sb = draw_ctx.sprites.borrow_mut();
                            sb.preview.begin();
                            let list = sb.preview.push(
                                items.into_iter().map(|(m, col, tex, _, _)| (m, col, tex))
                            );
                            sb.preview.upload(&draw_ctx.device, &draw_ctx.queue);
                            list
                        };
                        // preview パス記録で 'rp ライフタイムに使うためバッファハンドルを clone
                        let preview_inst_buf = draw_ctx.sprites.borrow().preview.buffer();

                        // ── プレビュー用の半透明対象収集（Phase R5）─────────────
                        // draw_model_indirect が Blend プリミティブをスキップするようになった
                        // ため、プレビューでも透明パスを別途描かないと半透明物が消える。
                        // has_transparent で安価に判定し、全 Opaque シーンではコストゼロ。
                        let preview_tp_models: Vec<(
                            &crate::engine::methods::drawer::GpuModel,
                            &crate::engine::methods::drawer::InstancedModelBatch,
                        )> = {
                            let mut models: Vec<(
                                &crate::engine::methods::drawer::GpuModel,
                                &crate::engine::methods::drawer::InstancedModelBatch,
                            )> = self.shared_model_batches.iter()
                                .filter_map(|(path, sd)| {
                                    gpu_model_by_path.get(path.as_str()).map(|&gpu| (gpu, &sd.batch))
                                })
                                .collect();
                            // ── 地形散布モデル（kind=Model プロップ）の半透明プリミティブもプレビュー透明パスへ ──
                            //   メインパス（transparent_models 収集, 3416〜3427）と同一ロジック。
                            //   散布木の葉・小枝は glTF 上 alphaMode=BLEND であり、不透明ループでは
                            //   スキップされるため、ここへ加えないとプレビューでも幹だけになり葉が消える。
                            //   可視カリングは cull_and_update_scatter_models が毎フレーム更新済み。
                            for res in self.terrain.scatter_models.values() {
                                models.push((&res.gpu_model, &res.batch));
                            }
                            models
                        };
                        let preview_has_tp =
                            crate::engine::core::renderer::transparency::has_transparent(
                                &preview_tp_models,
                            );

                        // ══════════════════════════════════════════════════════════
                        // 以下のマーカー間は「ゲームカメラの映像」を描く区間である。
                        // group 4（ライト＋シャドウ＋クラスタの複合 BG）には**カメラ固有の資源**
                        // （CSM・クラスタ）が入っているため、この区間では必ずプレビュー側
                        // （LightingPass::CameraPreview / draw_ctx.shadow_preview）だけを使うこと。
                        // メインカメラ用の資源を 1 箇所でも混ぜると、プレビューのライティングが
                        // デバッグカメラの向きで変わってしまう（実際に起きたバグ）。
                        // マーカー間にメインカメラ用資源が現れないことは、本ファイル末尾の
                        // ユニットテスト（camera_preview_pass_uses_preview_lighting_resources）が
                        // ソース走査で検証する。
                        // ══════════════════════════════════════════════════════════
                        // [CAMERA-PREVIEW-PASS-BEGIN]

                        // ── プレビューカメラ基準の CSM を構築する ────────────────────
                        // CSM のカスケードはカメラ視錐台にフィットさせる＝**カメラ固有**。
                        // メインカメラ（Edit ではデバッグカメラ）基準の CSM をここで流用すると、
                        // プレビューの影がデバッグカメラの向きで変わってしまう。
                        // 加えて、メインの影パスが記録されるのはこのパスより**後**なので、
                        // 流用すると 1 フレーム前の深度を読むことにもなる。
                        // → 専用資源（draw_ctx.shadow_preview）へプレビューカメラ基準の
                        //   カスケードを組み、この小窓パスの直前に深度を描く。
                        let preview_shadow_plan = {
                            // 正射ゲームカメラは CSM（透視錐台前提の分割）を組めないため影なしへ落とす。
                            let sp = cam_data.shadow_params(preview_aspect);
                            let (pv, pn, pf, pfov, pasp) = sp.unwrap_or((
                                Mat4x4::identity(), 0.1, 100.0, std::f32::consts::FRAC_PI_4, 1.0,
                            ));
                            // prepare_frame はライト配列の shadow_index（影希望センチネル）を
                            // 書き換えるため、GPU へ上げ済みの frame_lights ではなく
                            // 「書き換え前スナップショット」の複製を渡す。
                            // 採用スロットの割り当てはカメラ非依存なので結果の shadow_index は
                            // メイン側と一致する（＝GPU 上のライト配列と齟齬は生じない）。
                            let mut preview_lights = lights_before_shadow_assign.clone();
                            draw_ctx.shadow_preview.prepare_frame(
                                &draw_ctx.queue, &pv, pn, pf, pfov, pasp,
                                &mut preview_lights,
                                shadow_has_casters && sp.is_some(),
                            )
                        };
                        if preview_shadow_plan.any() {
                            // 影キャスター（cast_shadows=true のバッチ）はメインの影パスと同一集合。
                            let preview_casters: Vec<(
                                &crate::engine::methods::drawer::GpuModel,
                                &crate::engine::methods::drawer::InstancedModelBatch,
                            )> = self.shared_model_batches.iter()
                                .filter(|(path, _)| shadow_caster_paths.contains(path.as_str()))
                                .filter_map(|(path, sd)| {
                                    gpu_model_by_path.get(path.as_str()).map(|&gpu| (gpu, &sd.batch))
                                })
                                .collect();
                            if !preview_casters.is_empty() {
                                draw_ctx.shadow_preview.record(
                                    frame.encoder_mut(),
                                    &draw_ctx.pipelines.shadow_depth,
                                    &preview_shadow_plan,
                                    &preview_casters,
                                );
                            }
                        }

                        // プレビュー用の透明 group4 BindGroup（Phase RT-Translucency）。
                        // 透明パイプラインは group4 に屈折背景（binding15/16）を要求するため、
                        // プレビューでも専用 BG が要る（屈折はプレビュー無効＝ダミー背景・LightMeta も屈折ビット 0）。
                        // preview_pass より長生きさせる必要があるためパスの外（この scope）で生成する。
                        let preview_tp_bg = draw_ctx.light_buffer.create_transparent_bind_group(
                            &draw_ctx.device,
                            &draw_ctx.pipelines.transparent.lights_bgl,
                            &draw_ctx.shadow_preview,
                            &draw_ctx.clusters,
                            &draw_ctx.gi,
                            LightingPass::CameraPreview,
                            draw_ctx.pipelines.transparent.dummy_refract_view(),
                            &draw_ctx.pipelines.transparent.refract_sampler,
                        );

                        // ══════════════════════════════════════════════════════════
                        //  案A: カメラプレビューを「本番と同じ Deferred（G-Buffer→ライティング）」で描く。
                        //  （旧実装＝フォワード小窓＋白プレースホルダ地形は撤去）
                        //
                        //  なぜ Deferred か:
                        //   ・地形のレイヤブレンド（terrain_gbuffer_write.wgsl の triplanar）と草
                        //     （grass_gbuffer.wgsl）は **G-Buffer MRT へ焼くパイプラインでしか描けない**。
                        //     フォワード小窓では構造的に描けず、旧実装は地形を白い簡易材質で誤魔化し、
                        //     草は非表示だった。
                        //   ・プレビュー専用の小さな G-Buffer 4 枚＋深度（preview.gbuffer_*）へ本番と同一の
                        //     draw_gbuffer_indirect / draw_grass で焼き、deferred.pipeline（rt_off）で
                        //     ライティング復元する。見た目の一致が構造的に保証され、白プレースホルダも
                        //     material_override も不要になる。
                        //   ・出力アルファは deferred_lighting.wgsl が 1.0 固定で書くため、ブリットの
                        //     AlphaBlending でも地形が透けない（旧不具合＝頂点カラーα透過の構造的解消）。
                        //
                        //  プレビューは簡易経路のため AO / SSGI / RT 影 / シャドウマスクは無効
                        //  （ダミーを渡す）。ライト group4 は CameraPreview 側（クラスタ無効・プレビュー CSM）。
                        // ══════════════════════════════════════════════════════════

                        // プレビューカメラのフラスタム平面（草のチャンクカリング用）。
                        // メインの saved_frustum_planes は別カメラのため使わない。
                        //   【重要】extract_frustum_planes は行優先の数値行列（Mat4x4::data）を要求する。
                        //   cam_uniform.view_proj は GPU アップロード用に転置済み（列優先）のため使えない。
                        //   メインパス（saved_frustum_planes = extract_frustum_planes(&view_proj.data)）と
                        //   同様に、転置前の proj*view を組み直して渡す。
                        let preview_view_proj =
                            cam_data.proj_matrix(preview_aspect) * cam_data.view_matrix();
                        let preview_planes = extract_frustum_planes(&preview_view_proj.data);
                        let preview_cam_pos = cam_uniform.position;

                        // ── プレビュー G-Buffer 入力 BindGroup（deferred group1）───────────────
                        //   AO=Off→白（ao=1.0） / SSGI 無効→ダミー / シャドウマスク非対象→ダミー白。
                        //   深度は DepthOnly aspect ビュー（texture_depth_2d でサンプル）。
                        let preview_gbuffer_bg =
                            crate::engine::core::renderer::deferred::create_gbuffer_bind_group(
                                &draw_ctx.device,
                                &draw_ctx.pipelines.deferred.gbuffer_bgl,
                                &preview.gbuffer_views[0], &preview.gbuffer_views[1],
                                &preview.gbuffer_views[2], &preview.gbuffer_views[3],
                                &preview.depth_sample_view,
                                &draw_ctx.pipelines.deferred.gbuffer_sampler,
                                &draw_ctx.pipelines.ao.white_view,
                                &draw_ctx.pipelines.ao.linear_sampler,
                                &draw_ctx.pipelines.ssgi.dummy_view,
                                &draw_ctx.pipelines.ssgi.linear_sampler,
                                &draw_ctx.pipelines.deferred.mask_dummy_view,
                                &draw_ctx.pipelines.deferred.mask_sampler,
                                // コースティクスはメインビュー専用（プレビューは水面パスも持たない）。
                                &draw_ctx.pipelines.caustics.dummy_view,
                                // 影の屈折オフセット（binding13）も同様に未参照＝ダミー 1x1（ゼロ）でよい。
                                &draw_ctx.pipelines.caustics.dummy_offset_view,
                            );

                        // ── A. G-Buffer パス: 不透明ジオメトリをプレビュー G-Buffer 4 枚＋深度へ焼く ──
                        {
                            let mut gpass = frame.begin_gbuffer_pass_to_depth(
                                &preview.gbuffer_views[0], &preview.gbuffer_views[1],
                                &preview.gbuffer_views[2], &preview.gbuffer_views[3],
                                // RT4（速度）はプレビュー専用の捨て先へ書く（誰も読まない）。
                                // プレビューカメラの uniform は prev=curr なので値は恒等的に 0。
                                &preview.velocity_view,
                                &preview.depth_view,
                            );

                            // 統合バッチ（地形チャンク＋通常モデル）。地形は layer_resources を渡すことで
                            // 本番と同一の地形レイヤブレンドパイプライン（triplanar）へ振り分けられる。
                            // メッシュレットカリングは使わない（間接コマンドはメインカメラ基準で詰められる
                            // ため。draw_gbuffer_indirect は false で CPU カリング済み draw へフォールバック）。
                            for (path, sd) in &self.shared_model_batches {
                                if let Some(&gpu) = gpu_model_by_path.get(path.as_str()) {
                                    crate::engine::core::renderer::gbuffer::draw_gbuffer_indirect(
                                        &mut gpass, gpu, &sd.batch,
                                        &preview_mesh_cam_buf.bind_group,
                                        &draw_ctx.pipelines.gbuffer,
                                        false,
                                        self.terrain.layer_resources.as_ref(),
                                    );
                                }
                            }

                            // ── 草（procedural grass）を G-Buffer へ焼く ──
                            //   本番と同じ grass パイプライン。43 万本規模を全描画しないよう、プレビュー
                            //   カメラのフラスタム＋距離＋密度減衰でチャンクカリングする（メインと同一定数）。
                            //   時刻はプレビューを自己完結させるためここで更新する（メインが forward モードで
                            //   草を描かないフレームでも、プレビューは正しい風で草を描ける）。
                            let grass_cull_dist = *super::terrain_scatter_ops::GRASS_CULL_DISTANCE;
                            let grass_cull_sq = grass_cull_dist * grass_cull_dist;
                            let grass_decay_near = *super::terrain_scatter_ops::GRASS_DECAY_NEAR;
                            let grass_decay_mid = *super::terrain_scatter_ops::GRASS_DECAY_MID;
                            let grass_decay_near_sq = grass_decay_near * grass_decay_near;
                            let grass_decay_mid_sq = grass_decay_mid * grass_decay_mid;
                            // インタラクションフィールドの group2。
                            // 【設計判断】プレビューにも**本物の場をそのまま**バインドする
                            //  （ダミー 0 テクスチャを別途持たない）。場はメインカメラ追従の窓なので、
                            //  プレビューカメラがその窓の外を映していれば草は自然に無反応になる
                            //  （サンプル範囲外 = 0）。同じ場所を映していれば主画面と同じ挙動になり、
                            //  むしろ整合が取れる。専用リソースが不要でコードも分岐しない。
                            let interaction_bg = self.interaction_field.as_ref()
                                .map(|f| f.sample_bind_group());
                            for buf in self.terrain.grass_buffers.values() {
                                buf.update_time(&draw_ctx.queue, shader_time);
                                // 場が未構築（草バッファが空のはずのフレーム）なら草も描かない。
                                let Some(field_bg) = interaction_bg else { continue };
                                crate::engine::core::renderer::grass_gbuffer::draw_grass_culled(
                                    &mut gpass,
                                    &draw_ctx.pipelines.gbuffer.grass,
                                    buf,
                                    &preview_mesh_cam_buf.bind_group,
                                    field_bg,
                                    &preview_planes,
                                    preview_cam_pos,
                                    grass_cull_sq,
                                    grass_decay_near_sq,
                                    grass_decay_mid_sq,
                                );
                            }

                            // ── 散布モデル（kind=Model プロップ）の不透明部を焼く ──
                            //   草と違い実メッシュなので通常の deferred メッシュ経路（terrain_layers=None）。
                            //   葉（Blend）は後段のフォワード透明パスが preview_tp_models 経由で描く。
                            for res in self.terrain.scatter_models.values() {
                                crate::engine::core::renderer::gbuffer::draw_gbuffer_indirect(
                                    &mut gpass,
                                    &res.gpu_model, &res.batch,
                                    &preview_mesh_cam_buf.bind_group,
                                    &draw_ctx.pipelines.gbuffer,
                                    false,
                                    None,
                                );
                            }
                        }

                        // ── B. フルスクリーン・ライティングパス: G-Buffer → HDR プレビューカラー ──
                        //   deferred.pipeline（rt_off）＋ CameraPreview ライト BG（クラスタ無効・プレビュー
                        //   CSM・view_mode=Lit 固定）。背景（深度>=1）はシェーダが discard し clear_col が残る
                        //   （スカイボックスは次の C で上書き）。group0 の mesh カメラ BGL は deferred カメラ
                        //   BGL と構造的に等価（メイン camera_buf も同一 BGL を deferred.pipeline に bind 済み）。
                        {
                            let mut lpass = frame.begin_deferred_lighting_pass_to(
                                &preview.color_view, clear_col,
                            );
                            lpass.set_pipeline(&draw_ctx.pipelines.deferred.pipeline);
                            lpass.set_bind_group(0, &preview_mesh_cam_buf.bind_group, &[]);
                            lpass.set_bind_group(1, &preview_gbuffer_bg, &[]);
                            lpass.set_bind_group(2, &draw_ctx.pipelines.deferred.empty_bg2, &[]);
                            lpass.set_bind_group(3, &draw_ctx.pipelines.deferred.empty_bg3, &[]);
                            lpass.set_bind_group(
                                4,
                                draw_ctx.light_buffer.bind_group(LightingPass::CameraPreview),
                                &[],
                            );
                            lpass.draw(0..3, 0..1);
                        }

                        // ── C. フォワード上書きパス（Load）: スカイボックス・半透明・3D スプライト ──
                        //   B のライティング結果（カラー）と A の深度を保持したまま重ねる。メインパスの
                        //   begin_scene_pass_load_to と同じ位置づけ。
                        {
                            let mut preview_pass = frame.begin_offscreen_load_pass(
                                &preview.color_view,
                                &preview.depth_view,
                            );

                            // スカイボックス（天球・深度 far 固定）。深度 Load のため地形の手前には出ない。
                            //   skybox.wgsl の CameraLocked は球をカメラ位置へ平行移動して描くため、
                            //   プレビューカメラ uniform を積んだ mesh カメラ BG を渡すだけで無限遠背景が成立。
                            if self.skybox_system.has_skyboxes() {
                                self.skybox_system.draw(
                                    &mut preview_pass,
                                    &draw_ctx.pipelines.skybox,
                                    &preview_mesh_cam_buf.bind_group,
                                );
                            }

                            // ── 半透明の距離ソート描画（Phase R5）──
                            //   距離基準はプレビューカメラのワールド位置（cam_uniform.position）。
                            //   group4 は CameraPreview 側（別カメラのため）。
                            if preview_has_tp {
                                crate::engine::core::renderer::transparency::draw_sorted(
                                    &mut preview_pass,
                                    &preview_tp_models,
                                    &preview_mesh_cam_buf.bind_group,
                                    &preview_tp_bg,
                                    &draw_ctx.pipelines.transparent,
                                    cam_uniform.position,
                                );
                            }

                            // 3D Canvas スプライトをプレビューカメラで描画する。
                            if !preview_sprite_3d.is_empty() {
                                draw_sprite_batches(
                                    &mut preview_pass,
                                    &draw_ctx.pipelines.sprite,
                                    &preview_mesh_cam_buf.bind_group,
                                    &preview_inst_buf,
                                    &preview_sprite_3d,
                                );
                            }
                        }
                        // [CAMERA-PREVIEW-PASS-END]
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
                    // Edit ビューモードで非表示対象のアクターを選択中はギズモを生成しない
                    let gizmo_gpu_batch = if show_gizmo_pre
                        && self.tool_mode != ToolMode::Select
                        && !gizmo_suppressed_by_view
                    {
                        gizmo_pos.map(|pos| {
                            // 2D アクター編集タブ・2D アクター選択時 / それ以外でギズモ半径を切り替える
                            let (radius, cam_pos_arr) = if gizmo_actor_is_2d {
                                // 2D スクリーンスペース: ワールド編集（3D ビュー）と同じ
                                // スクリーン占有率でギズモ半径を計算する（見た目の大きさを統一）
                                let cam_2d = self.canvas_cameras.get(&self.active_world_line);
                                let r = cam_2d.map(|c| c.ortho_half_h * GIZMO_SCREEN_RADIUS_RATIO)
                                    .unwrap_or(360.0 * GIZMO_SCREEN_RADIUS_RATIO);
                                (r, [0.0f32, 0.0, -100.0])
                            } else {
                                // 3D デバッグカメラ（通常3D または ワールドスペースキャンバス）:
                                // editor_3d_gizmo_radius と同一の式（self.renderer 可変借用中のため
                                // self メソッドを呼べず、self.camera フィールド経由でインライン計算する）。
                                let cam_pos = self.camera.position();
                                let r = if self.camera.is_ortho() {
                                    // 正射（2D トグル）: 可視半高に比例させて見た目の大きさを一定に保つ
                                    self.camera.ortho_half_h.max(0.01) * GIZMO_SCREEN_RADIUS_RATIO
                                } else {
                                    // 透視: カメラ距離と FOV から見た目の大きさが一定になる半径
                                    let d = [pos[0]-cam_pos.x, pos[1]-cam_pos.y, pos[2]-cam_pos.z];
                                    let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
                                    let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
                                    dist * half_fov.tan() * GIZMO_SCREEN_RADIUS_RATIO
                                };
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
                            } else if let Some([ax, ay, az]) = local_axes_pre {
                                // Local 座標モード（通常 3D アクター）: オブジェクトのローカル回転軸に
                                // 沿った全軸ギズモ（回転リングもオブジェクト回転に追従する）
                                match self.tool_mode {
                                    ToolMode::Move   => batch.add_gizmo_translate_local(pos, radius, hov, ax, ay, az),
                                    ToolMode::Rotate => batch.add_gizmo_rotate_local(pos, radius, 64, cam_pos_arr, hov, drag_part, ax, ay, az),
                                    ToolMode::Scale  => batch.add_gizmo_scale_local(pos, radius, hov, ax, ay, az),
                                    ToolMode::Select => {}
                                }
                            } else {
                                // 3D（World 座標モード）: 全軸・平面ハンドル、Rotate は半円
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
                            if use_ortho_2d_camera || scene_canvas_ss {
                                // 2D スクリーンスペース（アクター編集タブ・2Dシーンビュー・シーンSS共通）:
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
                                // 3D デバッグカメラ: near plane の少し手前にワールド座標を配置する
                                let view    = self.camera.view_matrix();
                                let cam_pv  = self.camera.position();
                                let cam_pos = [cam_pv.x, cam_pv.y, cam_pv.z];
                                let near_vs = self.camera.base.projection.near * 1.05;
                                let v = &view.data;
                                if self.camera.is_ortho() {
                                    // 正射投影（2D トグル）: NDC → ビュー平面上の平行オフセット。
                                    // 透視用の除算（nx / p[0][0]）は正射行列では位置スケールに
                                    // ならないため、ortho_half_w/h を直接掛けて変換する。
                                    let half_h = self.camera.ortho_half_h.max(0.01);
                                    let half_w = half_h * (vp_w / vp_h);
                                    for (i, &(sx, sy)) in sc.iter().enumerate() {
                                        let nx  = 2.0 * sx / vp_w - 1.0;
                                        let ny  = 1.0 - 2.0 * sy / vp_h;
                                        let vpx = nx * half_w;
                                        let vpy = ny * half_h;
                                        let vpz = near_vs;
                                        wp[i] = [
                                            cam_pos[0] + v[0][0]*vpx + v[1][0]*vpy + v[2][0]*vpz,
                                            cam_pos[1] + v[0][1]*vpx + v[1][1]*vpy + v[2][1]*vpz,
                                            cam_pos[2] + v[0][2]*vpx + v[1][2]*vpy + v[2][2]*vpz,
                                        ];
                                    }
                                } else {
                                    // 透視投影: near plane 上へ逆投影する
                                    let proj = self.camera.projection_matrix();
                                    let p = &proj.data;
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
                    // 2D シーンビューでは 3D 配置プレビューは表示しない
                    const PREVIEW_SPHERE_RADIUS: f32 = 0.5;
                    let drop_preview_batch = if let Some(pos) = self.drop_preview_pos.filter(|_| !edit_view_2d) {
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

                    // 地形ブラシ範囲プレビュー（Edit モードのホバー位置のワイヤスフィア）。
                    //   TERRAIN_BRUSH_PREVIEW で terrain.brush_preview に (中心, 半径, 強度) が入る。
                    //   Edit（in_editor）のみ描画し、Play では出さない。
                    //   ⑥ 強度に応じて色を低強度色→高強度色へ線形補間する（視認性重視）。
                    const TERRAIN_PREVIEW_COLOR_LOW: [f32; 4] = [0.40, 0.85, 1.0, 0.80];  // 低強度=薄い水色
                    const TERRAIN_PREVIEW_COLOR_HIGH: [f32; 4] = [1.0, 0.45, 0.10, 0.90]; // 高強度=濃いオレンジ
                    const TERRAIN_PREVIEW_MERIDIANS: usize = 12; // 経線本数
                    const TERRAIN_PREVIEW_PARALLELS: usize = 6;  // 緯線本数
                    const TERRAIN_PREVIEW_RING_SEGS: usize = 32; // 各円の分割数
                    let terrain_preview_batch = if in_editor {
                        self.terrain.brush_preview.map(|(center, radius, strength)| {
                            // t=0（低強度）でシアン寄り、t=1（高強度）でオレンジ寄りへ各チャンネルを lerp。
                            let t = strength.clamp(0.0, 1.0);
                            let mut color = [0.0f32; 4];
                            for i in 0..4 {
                                color[i] = TERRAIN_PREVIEW_COLOR_LOW[i]
                                    + (TERRAIN_PREVIEW_COLOR_HIGH[i] - TERRAIN_PREVIEW_COLOR_LOW[i]) * t;
                            }
                            let mut lb = LineBatch::new();
                            lb.add_wire_sphere_latlong(
                                center, radius,
                                TERRAIN_PREVIEW_MERIDIANS,
                                TERRAIN_PREVIEW_PARALLELS,
                                TERRAIN_PREVIEW_RING_SEGS,
                                color,
                            );
                            lb.build(&draw_ctx.device)
                        })
                    } else { None };

                    // コントロールポイント（汎用パス）のギズモを GPU バッファ化する。
                    // 頂点自体はレンダラ借用前に組んである（control_point_lines）。
                    // 区間ライン（太線）と点キューブ／プレビューマーカー（従来の細線）で
                    // パイプラインが異なるため、2 本の GPU バッファに分けてビルドする。
                    let control_point_batch = control_point_lines.as_ref()
                        .filter(|b| !b.markers.is_empty())
                        .map(|b| b.markers.build(&draw_ctx.device));
                    let control_point_thick_batch = control_point_lines.as_ref()
                        .filter(|b| !b.segment_lines.is_empty())
                        .map(|b| b.segment_lines.build_thick(&draw_ctx.device));

                    // グリッド描画バッチ（エディタモード + show_grid のみ）
                    let _perf_t_grid = std::time::Instant::now();
                    // アクター編集タブの 2D キャンバス: XY 平面グリッド（常時表示）
                    // その他（シーン上のキャンバス含む 3D 系）: XZ 平面グリッド
                    // シーン上に canvas があっても 3D グリッドを維持する（is_actor_edit_canvas で判定）
                    let is_actor_edit_canvas = is_canvas && self.actor_edit_canvas_wls.contains(&self.active_world_line);
                    // 2D グリッド（XY 平面）を使うビューか:
                    //   - アクター編集タブの 2D キャンバス（従来動作）
                    //   - Edit の 2D シーンビュー（2D オルソカメラでパン・ズームするため）
                    let is_2d_grid_view = is_actor_edit_canvas || edit_view_2d;
                    let grid_gpu_batch = if in_editor && (self.show_grid || (self.active_world_line != 0 && !is_actor_edit_canvas)) {
                        let mut lb = LineBatch::new();
                        // モード別グリッド色
                        // 2D アクター編集・2D シーンビュー: 薄い青系（minor: 薄く, major: 中程度）
                        // 3D アクター編集: 紺背景に映える青系
                        // 3D シーン: ダークグレー
                        let (minor, major): ([f32; 4], [f32; 4]) = if is_2d_grid_view {
                            ([0.22, 0.25, 0.40, 0.20], [0.32, 0.40, 0.60, 0.55])
                        } else if self.active_world_line != 0 {
                            ([0.22, 0.25, 0.40, 1.0], [0.32, 0.36, 0.55, 1.0])
                        } else {
                            ([0.18, 0.18, 0.18, 1.0], [0.30, 0.30, 0.30, 1.0])
                        };
                        let ax_x: [f32; 4] = [0.60, 0.15, 0.15, 0.90];

                        if is_2d_grid_view {
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

                    // ── コライダー面ピッキングアイテム ────────────────────────────────
                    // エディタ編集ビューでは 2D コライダーは 1px のワイヤーフレームで描画される。
                    // 線をクリックしても選択できないため、ID パス（canvas_id）へコライダーの
                    // 外接クワッド（面）を追加し、面クリックでそのアクターを選択可能にする。
                    // 各要素 = (raw_id, GPU 列優先モデル行列)。raw_id は該当アクターのスプライトが
                    // 使うピック ID と同一（= canvas_id_offset + ctx.dfs_id）にすることで、
                    // 既存のスプライトピッキングとまったく同じ経路でアクターが選択される。
                    // ID パスは後勝ちのため、スプライト等の canvas_id より「先」に描画して
                    // 最背面のピッキング対象とする（重なり時は既存描画物の選択を優先する）。
                    //   - collider_pick_items_2d:       通常 2D シーン（SS ortho / WS 2D）
                    //   - collider_pick_items_3dcanvas: 3D シーン内キャンバス配下（WS perspective）
                    //   - collider_pick_items_3d:       3D コライダー（WS perspective。
                    //                                   形状ごとの近似クワッド生成は collider3d_pick.rs）
                    let mut collider_pick_items_2d:       Vec<(u32, [[f32; 4]; 4])> = Vec::new();
                    let mut collider_pick_items_3dcanvas: Vec<(u32, [[f32; 4]; 4])> = Vec::new();
                    let mut collider_pick_items_3d:       Vec<(u32, [[f32; 4]; 4])> = Vec::new();

                    // ── コライダーワイヤーフレームバッチ ──────────────────────────────
                    let _perf_t_collider = std::time::Instant::now();
                    // 描画条件:
                    //   - エディタモード（3D シーンのみ）: 常に表示
                    //   - Play モード: play_collider_draw フラグが有効な場合のみ表示
                    // トリガーコライダー: 黄色 / 通常コライダー: 緑 / 衝突中: 赤
                    const COLLIDER_COLOR_NORMAL:    [f32; 4] = [0.0, 1.0, 0.2, 1.0];
                    const COLLIDER_COLOR_TRIGGER:   [f32; 4] = [1.0, 0.9, 0.0, 1.0];
                    const COLLIDER_COLOR_COLLISION: [f32; 4] = [1.0, 0.2, 0.0, 1.0];

                    // 3D コライダーはアクター編集 2D タブ・2D シーンビューでは表示しない
                    let draw_colliders = (in_editor && !use_ortho_2d_camera)
                        || (!in_editor && self.play_collider_draw);

                    let (collider_wireframe_batch, collider_wireframe_sel_batch) = if draw_colliders {
                        let wl = self.active_world_line;
                        let mut lb = LineBatch::new();
                        // 選択中アクターのコライダー線を集める別バッチ（太線として描画する）。
                        let mut lb_sel = LineBatch::new();

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
                            // 選択中アクターのコライダーは明度を上げ、かつ太線で強調する。
                            // selected_actor_dfs_ids はピックのデコード（global - canvas_id_offset）
                            // による 0 始まり DFS を保持するため、1 始まりの dfs_id を -1 して比較する。
                            let is_selected = self.selected_actor_dfs_ids
                                .contains(&(dfs_id as usize).saturating_sub(1));
                            let color = crate::engine::core::app_base::app::collider2d_wireframe::collider_color_for_selection(
                                color, is_selected,
                            );
                            // 選択中は太線バッチ（lb_sel）へ、非選択は通常バッチ（lb）へ振り分ける。
                            let target: &mut LineBatch = if is_selected { &mut lb_sel } else { &mut lb };

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

                            // コライダー面ピッキング（エディタ編集時のみ。ID パスは in_editor 限定）。
                            // ワイヤーフレームと同一の pos / 回転 / スケールから近似クワッドを生成し、
                            // ID パスの最背面に描画する（重なり時は既存メッシュ等の選択を優先）。
                            // raw_id はキャンバスピッキングと同じ DFS 選択経路を使う:
                            //   raw_id = canvas_id_offset + dfs_id（dfs_id は 1 始まりの全アクター DFS）
                            // → decode 側の `global >= canvas_id_offset` 分岐で該当アクターが選択される。
                            if in_editor {
                                let cam_pv  = self.camera.position();
                                let cam_pos = [cam_pv.x, cam_pv.y, cam_pv.z];
                                let mut quads: Vec<[[f32; 4]; 4]> = Vec::new();
                                crate::engine::core::app_base::app::collider3d_pick::collect_collider3d_pick_quads(
                                    &collider.shape, pos, &q, scale, cam_pos, &mut quads,
                                );
                                let raw_id = canvas_id_offset + dfs_id as u32;
                                collider_pick_items_3d.extend(
                                    quads.into_iter().map(|m| (raw_id, m)));
                            }

                            match &collider.shape {
                                ColliderShapeData::Box { half_extents } => {
                                    // スケールを半サイズに適用
                                    let he = [
                                        half_extents[0] * scale[0].abs(),
                                        half_extents[1] * scale[1].abs(),
                                        half_extents[2] * scale[2].abs(),
                                    ];
                                    target.add_obb(pos, rot, he, color);
                                }
                                ColliderShapeData::Sphere { radius } => {
                                    // 最大スケール軸を半径に適用
                                    let r = radius * scale[0].abs()
                                        .max(scale[1].abs())
                                        .max(scale[2].abs());
                                    target.add_sphere_at(pos, r, 24, color);
                                }
                                ColliderShapeData::Capsule { radius, half_height } => {
                                    let r  = radius * scale[0].abs().max(scale[2].abs());
                                    let hh = half_height * scale[1].abs();
                                    target.add_capsule_wireframe(pos, rot, r, hh, 24, color);
                                }
                                ColliderShapeData::Cylinder { radius, half_height } => {
                                    let r  = radius * scale[0].abs().max(scale[2].abs());
                                    let hh = half_height * scale[1].abs();
                                    target.add_cylinder_wireframe(pos, rot, r, hh, 24, color);
                                }
                                ColliderShapeData::Cone { radius, half_height } => {
                                    let r  = radius * scale[0].abs().max(scale[2].abs());
                                    let hh = half_height * scale[1].abs();
                                    target.add_cone_wireframe(pos, rot, r, hh, 24, color);
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
                                    target.add_convex_hull_wireframe(&world_verts, color);
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
                                    target.add_triangle_mesh_wireframe(&world_tris, color);
                                }
                            }
                        }

                        // 通常線（1px）＋ 選択線（太線）の 2 バッチを返す。
                        let base = if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) };
                        let sel  = if lb_sel.is_empty() { None } else { Some(lb_sel.build_thick(&draw_ctx.device)) };
                        (base, sel)
                    } else { (None, None) };
                    perf_collider_ms = _perf_t_collider.elapsed().as_secs_f64() * 1000.0;

                    // ── 2D コライダーワイヤーフレームバッチ ──────────────────────────────
                    // 描画条件:
                    //   - 2D キャンバス世界線（is_canvas）のみ
                    //   - エディタモード: 常に表示
                    //   - Play モード: play_collider_draw フラグが有効な場合のみ表示
                    // トリガーコライダー: 黄色 / 通常コライダー: 緑 / 衝突中: 赤
                    let draw_colliders_2d = is_canvas
                        && (in_editor || self.play_collider_draw);

                    let (collider_2d_wireframe_batch, collider_2d_wireframe_sel_batch) = if draw_colliders_2d {
                        // キャンバス座標 → レンダリング座標変換スケール
                        let canvas_scale = if use_screen_space { 1.0f32 } else { CANVAS_WORLD_SCALE };
                        // Y 軸方向: スクリーンスペース時は Y+ が下（CSS と同方向）
                        let y_sign = if use_screen_space { 1.0f32 } else { -1.0 };

                        let mut lb = LineBatch::new();
                        // 選択中コライダー線を集める別バッチ（太線として描画する）。
                        let mut lb_sel = LineBatch::new();

                        // collect_actor2d_contexts に viewport_size を渡す。
                        // canvas_collect.rs と同一の変換チェーンで body_pos_px が計算される。
                        // SS モード時は ortho 空間（ビューポート中心が原点）で返ってくるため、
                        // ワイヤーフレーム描画はコライダーオフセットを加算するだけでよい。
                        let vp_wf = window_size.map_or(1280.0f32, |s| s.width  as f32);
                        let vp_hf = window_size.map_or(720.0f32,  |s| s.height as f32);
                        // SS レイアウト時（2D シーンビュー含む）はビューポート基準でレイアウトする
                        let viewport_size_2d = if ss_layout { Some([vp_wf, vp_hf]) } else { None };
                        // CanvasViewportRef::Camera を持つルートキャンバスのビューポートサイズを解決する
                        // ビューポート上書き + ルート自動解像度マップ（描画と同一条件・共通ヘルパー）
                        let (canvas_vp_overrides_2d, root_auto_sizes_2d) = if ss_layout {
                            build_ss_layout_maps_free(
                                &scene.actors, &scene.world,
                                self.active_world_line, vp_wf, vp_hf,
                                if !in_editor { Some(game_viewport) } else { None },
                                self.project_resolution, edit_view_2d,
                            )
                        } else {
                            (std::collections::HashMap::new(), std::collections::HashMap::new())
                        };
                        let ctx2d_list = crate::engine::core::app_base::app::physics2d_ops::collect_actor2d_contexts(
                            scene, self.active_world_line, viewport_size_2d, &canvas_vp_overrides_2d,
                            &root_auto_sizes_2d, edit_view_2d,
                        );

                        // 3D シーン内キャンバス（Actor3D + CanvasComponent）配下の Actor2D は、
                        // canvas_to_world 変換を通す専用パス（後述の 3D キャンバス用バッチ）で
                        // 描画するため、この 2D（ortho / ワールドスペース）パスからは除外する。
                        // collect_actor2d_contexts は 3D キャンバス配下の Actor2D もフラットに
                        // 含めるが、その body_pos_px はキャンバスローカル空間のため、ここで
                        // ortho 空間として描画すると誤った位置（原点付近の極小枠）になってしまう。
                        let canvas3d_desc_map =
                            crate::engine::core::app_base::app::collider2d_wireframe::build_3d_canvas_collider_descendant_map(
                                scene, self.active_world_line,
                            );

                        for ctx in &ctx2d_list {
                            // 3D キャンバス配下はこのパスの対象外（専用パスで描画）
                            if canvas3d_desc_map.contains_key(&ctx.actor_entity) { continue; }
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
                            // 選択中アクターのコライダーは明度を上げ、太線で強調する
                            // （selected_actor_dfs_ids は 0 始まり DFS なので ctx.dfs_id を -1 して比較）
                            let is_selected = self.selected_actor_dfs_ids
                                .contains(&(ctx.dfs_id as usize).saturating_sub(1));
                            let color = crate::engine::core::app_base::app::collider2d_wireframe::collider_color_for_selection(
                                color, is_selected,
                            );
                            // 選択中は太線バッチ（lb_sel）へ、非選択は通常バッチ（lb）へ振り分ける。
                            let target: &mut LineBatch = if is_selected { &mut lb_sel } else { &mut lb };

                            let rot_rad = ctx.rot_rad;
                            let (sin, cos) = rot_rad.sin_cos();

                            // コライダーオフセットをボディ回転で変換する（キャンバスピクセル単位）
                            let [ox, oy] = collider.offset;
                            let off_wx = cos * ox - sin * oy;
                            let off_wy = sin * ox + cos * oy;

                            // コライダー中心（Y 未反転の正準キャンバス空間）と実効スケールを求める。
                            // body_pos_px は canvas_collect.rs と同一の変換で ortho 空間で計算済み。
                            // Y 反転（y_sign）は共通関数へ渡す map クロージャ側で行う。
                            let (cx, cy, eff_sx, eff_sy) = if ss_layout {
                                (
                                    ctx.body_pos_px[0] + off_wx * ctx.size_sx,
                                    ctx.body_pos_px[1] + off_wy * ctx.size_sy,
                                    ctx.size_sx, ctx.size_sy,
                                )
                            } else {
                                (
                                    (ctx.body_pos_px[0] + off_wx) * canvas_scale,
                                    (ctx.body_pos_px[1] + off_wy) * canvas_scale,
                                    canvas_scale, canvas_scale,
                                )
                            };

                            // 2D シーンの描画空間: 正準キャンバス空間の Y を y_sign で反転するのみ（Z=0）。
                            // map が Y を y_sign で反転するため、map_y_sign にも同じ y_sign を渡し
                            // 従来実装（回転 rot_rad * y_sign）と同一の頂点列を保証する。
                            crate::engine::core::app_base::app::collider2d_wireframe::emit_collider2d_wireframe(
                                target, &collider.shape, [cx, cy], rot_rad, ctx.scale,
                                eff_sx, eff_sy, color, y_sign,
                                |[x, y]| [x, y * y_sign, 0.0],
                            );

                            // コライダー面ピッキング（エディタ編集時のみ。ID パス自体が
                            // in_editor 限定のため Play 中の play_collider_draw では収集しない）。
                            // アクター編集 2D タブは CPU picking 専用（キャンバス ID パス対象外）
                            // のため、スプライト ID 収集（collect_canvas_id_items）と同様に除外する。
                            // ワイヤーフレームと同一の変換でコライダー外接クワッドを構築する。
                            // raw_id はスプライトピッキングと同じ規則:
                            //   raw_id = canvas_id_offset + dfs(0 始まり) + 1 = canvas_id_offset + ctx.dfs_id
                            // （collect_actor2d_contexts の dfs_id は 1 始まりの同一 DFS 順）。
                            if in_editor && !is_actor_edit_2d {
                                if let Some(model) =
                                    crate::engine::core::app_base::app::collider2d_wireframe::collider2d_pick_quad_model(
                                        &collider.shape, [cx, cy], rot_rad, ctx.scale,
                                        eff_sx, eff_sy, y_sign,
                                        |[x, y]| [x, y * y_sign, 0.0],
                                    )
                                {
                                    collider_pick_items_2d.push((canvas_id_offset + ctx.dfs_id as u32, model));
                                }
                            }
                        }

                        let base = if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) };
                        let sel  = if lb_sel.is_empty() { None } else { Some(lb_sel.build_thick(&draw_ctx.device)) };
                        (base, sel)
                    } else { (None, None) };

                    // ── 3D キャンバス配下 2D コライダーワイヤーフレームバッチ ──────────────
                    // 3D シーン内キャンバス（Actor3D + CanvasComponent）配下の Actor2D が持つ
                    // Collider2d を、スプライトの「3D Canvas 配下収集パス」と同一の
                    // canvas_to_world 変換（キャンバス空間 → 3D 空間）を通して描画する。
                    //
                    // 通常の 2D シーン用バッチ（collider_2d_wireframe_batch）が is_canvas
                    // （= トップレベル Actor2D 世界線）に限定されるのに対し、こちらは
                    // is_canvas に関わらず常に評価する（3D シーンでも 3D キャンバスは存在するため）。
                    // 描画条件（in_editor || play_collider_draw）は 2D パスと揃える。
                    // 2D シーンビュー（edit_view_2d）では 3D シーンごと非表示のため生成しない。
                    let draw_colliders_3d_canvas =
                        (in_editor || self.play_collider_draw) && !edit_view_2d;

                    let (collider_2d_canvas3d_wireframe_batch, collider_2d_canvas3d_wireframe_sel_batch) = if draw_colliders_3d_canvas {
                        if let Some(scene) = &self.scene {
                            // 3D キャンバス配下 Actor2D → 所属キャンバスの canvas_to_world マップ。
                            let canvas3d_desc_map =
                                crate::engine::core::app_base::app::collider2d_wireframe::build_3d_canvas_collider_descendant_map(
                                    scene, self.active_world_line,
                                );

                            if canvas3d_desc_map.is_empty() {
                                (None, None)
                            } else {
                                let mut lb = LineBatch::new();
                                // 選択中コライダー線を集める別バッチ（太線として描画する）。
                                let mut lb_sel = LineBatch::new();

                                // スプライトの 3D キャンバス収集と同一パラメータで body_pos_px を得る:
                                //   viewport_size = None・オーバーライド/自動解像度マップ = 空・
                                //   design_space = false。
                                // これにより 3D キャンバス配下 Actor2D の body_pos_px は
                                // キャンバスローカル空間（キャンバス [0,0] 基準の px、Y+ 下）で返り、
                                // canvas_to_world 行列でスプライトと一致する 3D 位置へ変換できる。
                                let empty_overrides: std::collections::HashMap<crate::engine::ecs::Entity, [f32; 2]> =
                                    std::collections::HashMap::new();
                                let empty_auto: std::collections::HashMap<crate::engine::ecs::Entity, [f32; 2]> =
                                    std::collections::HashMap::new();
                                let ctx3d_list = crate::engine::core::app_base::app::physics2d_ops::collect_actor2d_contexts(
                                    scene, self.active_world_line, None, &empty_overrides,
                                    &empty_auto, false,
                                );

                                for ctx in &ctx3d_list {
                                    // 3D キャンバス配下でなければスキップ（通常の 2D パスが担当）。
                                    let Some(ctw) = canvas3d_desc_map.get(&ctx.actor_entity) else { continue };
                                    let Some(slot_entity) = ctx.collider_slot_entity else { continue };
                                    let Some(collider) = scene.world.get::<Collider2dComponent>(slot_entity) else { continue };

                                    // 接触色・トリガー色分けは通常 2D シーンと同一。
                                    let color = if collider.is_trigger {
                                        COLLIDER_COLOR_TRIGGER
                                    } else if self.active_collision_2d_dfs_ids.contains(&ctx.dfs_id) {
                                        COLLIDER_COLOR_COLLISION
                                    } else {
                                        COLLIDER_COLOR_NORMAL
                                    };
                                    // 選択中アクターのコライダーは明度を上げ、太線で強調する
                                    // （selected_actor_dfs_ids は 0 始まり DFS なので ctx.dfs_id を -1 して比較）
                                    let is_selected = self.selected_actor_dfs_ids
                                        .contains(&(ctx.dfs_id as usize).saturating_sub(1));
                                    let color = crate::engine::core::app_base::app::collider2d_wireframe::collider_color_for_selection(
                                        color, is_selected,
                                    );
                                    // 選択中は太線バッチ（lb_sel）へ、非選択は通常バッチ（lb）へ振り分ける。
                                    let target: &mut LineBatch = if is_selected { &mut lb_sel } else { &mut lb };

                                    let rot_rad = ctx.rot_rad;
                                    let (sin, cos) = rot_rad.sin_cos();

                                    // コライダーオフセットをボディ回転で変換（キャンバスピクセル単位）
                                    let [ox, oy] = collider.offset;
                                    let off_wx = cos * ox - sin * oy;
                                    let off_wy = sin * ox + cos * oy;

                                    // 中心はキャンバスローカル px（Y 未反転）。SS レイアウトと同じく
                                    // オフセットへ size_sx/size_sy を適用する。Y 反転・px→3D 変換は
                                    // canvas_to_world 行列（map クロージャ）が担う。
                                    let cx = ctx.body_pos_px[0] + off_wx * ctx.size_sx;
                                    let cy = ctx.body_pos_px[1] + off_wy * ctx.size_sy;

                                    // canvas_to_world 変換は正準キャンバス空間（Y+ 下）をそのまま
                                    // 3D 平面へ写すため Y 反転は発生しない → map_y_sign は +1.0。
                                    crate::engine::core::app_base::app::collider2d_wireframe::emit_collider2d_wireframe(
                                        target, &collider.shape, [cx, cy], rot_rad, ctx.scale,
                                        ctx.size_sx, ctx.size_sy, color, 1.0,
                                        |p| crate::engine::core::app_base::app::collider2d_wireframe::canvas_point_to_world(ctw, p),
                                    );

                                    // コライダー面ピッキング（3D キャンバス配下・エディタ編集時のみ）。
                                    // アクター編集 2D タブは CPU picking 専用のため除外する
                                    // （3D Canvas 子スプライト ID 収集の !use_ortho_2d_camera と同じ扱い）。
                                    // ワイヤーフレームと同一の canvas_to_world 変換で外接クワッドを
                                    // 3D ワールド空間へ写し、WS perspective カメラの ID パスで描画する。
                                    // raw_id 規則は 2D パスと同一（= canvas_id_offset + ctx.dfs_id）。
                                    if in_editor && !is_actor_edit_2d {
                                        if let Some(model) =
                                            crate::engine::core::app_base::app::collider2d_wireframe::collider2d_pick_quad_model(
                                                &collider.shape, [cx, cy], rot_rad, ctx.scale,
                                                ctx.size_sx, ctx.size_sy, 1.0,
                                                |p| crate::engine::core::app_base::app::collider2d_wireframe::canvas_point_to_world(ctw, p),
                                            )
                                        {
                                            collider_pick_items_3dcanvas.push((canvas_id_offset + ctx.dfs_id as u32, model));
                                        }
                                    }
                                }

                                let base = if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) };
                                let sel  = if lb_sel.is_empty() { None } else { Some(lb_sel.build_thick(&draw_ctx.device)) };
                                (base, sel)
                            }
                        } else { (None, None) }
                    } else { (None, None) };

                    // スプライト描画リソース収集（render pass 前に GPU バッファを準備する）
                    // CanvasTransform + SpriteComponent を持つアクターを列挙し、
                    // テクスチャをキャッシュから取得または新規ロードして SpritePrepared を生成する。
                    // Edit / Play 両モードで収集する（in_editor チェックなし）。
                    //
                    // 【重要】2D スプライトと 3D Canvas スプライトを分離して収集する。
                    //   - sprite_prepared_2d_bg / _fg: Actor2D（CanvasTransform）のスプライトを
                    //     ルートキャンバスの描画ゾーン（Phase C）で分割したもの。
                    //     描画順（奥→手前）: 背景ゾーン | 3D ワールド | 前面ゾーン。
                    //     scene_canvas_ss=true のとき、前面はオーバーレイパス（2D オルソカメラ）、
                    //     背景はメインパス冒頭（3D ワールドより先）で描画する。
                    //     各ゾーン内は全キャンバス横断でレイヤー昇順の安定ソート（Phase D）。
                    //   - sprite_prepared_3d: Actor3D + CanvasComponent の子スプライト。
                    //     scene_canvas_ss の値に関わらず「常に」メインパス（3D カメラ）で描画する。
                    //     2D アクターが混在するシーンで scene_canvas_ss=true になっても、
                    //     3D Canvas が 2D オルソカメラで極小点として映るバグを防ぐ。
                    //     レイヤーはキャンバス単位で安定ソートする（ゾーン概念なし）。
                    let (sprite_prepared_2d_bg, sprite_prepared_2d_fg, sprite_prepared_3d) = {
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
                                // ビューポート上書き + ルート自動解像度マップ
                                // （シーン SS レイアウト時のみ。アクター編集タブは保存値のまま）
                                let (canvas_vp_overrides, root_auto_sizes) = if is_scene_ss {
                                    build_ss_layout_maps_free(
                                        &scene.actors, &scene.world, wl, vp_w, vp_h, play_gvp,
                                        self.project_resolution, edit_view_2d)
                                } else {
                                    (std::collections::HashMap::new(), std::collections::HashMap::new())
                                };
                                collect_sprite_items(
                                    &scene.actors, &scene.world, wl, draw_ctx,
                                    None, IDENTITY, [1.0, 1.0],
                                    canvas_scale, y_sign, viewport_size, &canvas_vp_overrides,
                                    &root_auto_sizes, CanvasDrawZone::Foreground, edit_view_2d, &mut items_2d,
                                );
                            }

                            // ── 3D Canvas のスプライト（is_canvas に関わらず常に収集）──
                            // Actor3D + CanvasComponent を持つアクターをワールド空間で描画する。
                            // ただし 2D シーンビュー（edit_view_2d）では 3D シーンごと非表示のため収集しない。
                            //
                            // 座標変換の設計（3D 透視カメラは Vulkan Y-DOWN：world +Y → screen 下）:
                            //   canvas_to_world = actor_3d_mat × Scale(CANVAS_WORLD_SCALE, CANVAS_WORLD_SCALE, 1)
                            //   Y 反転なし — キャンバス Y+（下）はワールド Y+（3D カメラで screen 下）に対応 ✓
                            //   1px = 1cm（CANVAS_WORLD_SCALE = 0.01m）
                            for actor in scene.actors.iter() {
                                // 2D シーンビューでは 3D Canvas スプライトを収集しない
                                if edit_view_2d { break; }
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
                                // このキャンバス内の追加分をレイヤー昇順で安定ソートする
                                // （ワールドキャンバスのレイヤーはキャンバス内で完結する）
                                let canvas_start = items_3d.len();
                                collect_sprite_items(
                                    &actor.children, &scene.world, wl, draw_ctx,
                                    Some([cc.width, cc.height]),
                                    canvas_to_world, [1.0, 1.0],
                                    1.0, 1.0,
                                    // ワールドキャンバスは自動解像度の対象外（空マップ）・ゾーン概念なし
                                    None, &std::collections::HashMap::new(),
                                    &std::collections::HashMap::new(),
                                    // 3D ワールドキャンバス配下は常に親サイズ Some のためルート分岐に入らず design_space 無関係
                                    CanvasDrawZone::Foreground, false, &mut items_3d,
                                );
                                items_3d[canvas_start..].sort_by_key(|&(_, _, _, _, layer)| layer);
                            }
                        }

                        // ── 2D スプライトを描画ゾーンで分割し、ゾーン内をレイヤーで安定ソートする ──
                        // 分割時に DFS 順が保たれるため、sort_by_key（安定ソート）で
                        // 「同一レイヤーはヒエラルキー順」の仕様を満たす。
                        // ゾーン共有ソート = 全キャンバス横断で同一ゾーンのレイヤーを比較する。
                        let (mut items_2d_bg, mut items_2d_fg): (Vec<_>, Vec<_>) =
                            items_2d.into_iter()
                                .partition(|&(_, _, _, zone, _)| zone == CanvasDrawZone::Background);
                        items_2d_bg.sort_by_key(|&(_, _, _, _, layer)| layer);
                        items_2d_fg.sort_by_key(|&(_, _, _, _, layer)| layer);

                        // ゾーン・レイヤーを除いた (行列, カラー, テクスチャ) を main チャンネルへ積む。
                        // ここでは begin＋3 リスト分の push のみ行い、upload は後段（選択アウトラインを
                        // 積み終えた後）で 1 度だけ行う（Phase R6, 同一 main バッファへ連続配置）。
                        // 各 push はテクスチャ境界でバッチ分割し、描画順（ソート済み）は保つ。
                        let mut sb = draw_ctx.sprites.borrow_mut();
                        sb.main.begin();
                        let list_bg = sb.main.push(items_2d_bg.into_iter().map(|(m, col, tex, _, _)| (m, col, tex)));
                        let list_fg = sb.main.push(items_2d_fg.into_iter().map(|(m, col, tex, _, _)| (m, col, tex)));
                        let list_3d = sb.main.push(items_3d.into_iter().map(|(m, col, tex, _, _)| (m, col, tex)));
                        (list_bg, list_fg, list_3d)
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
                            // ビューポート上書き + ルート自動解像度マップ（描画と同一条件・共通ヘルパー）
                            let (canvas_vp_overrides_r, root_auto_sizes_r) = if is_scene_ss_rect {
                                build_ss_layout_maps_free(
                                    &scene.actors, &scene.world, wl, vp_w_r, vp_h_r, play_gvp_r,
                                    self.project_resolution, edit_view_2d)
                            } else {
                                (std::collections::HashMap::new(), std::collections::HashMap::new())
                            };
                            // アウトラインのリング間隔（描画空間の単位）:
                            // 太線はリングを重ねて表現するため、間隔を「画面 1px 相当」に
                            // 揃えることでズームに依らず隙間なく密着し 1 本の太線に見える。
                            //   - 2D オルソビュー: 可視高（2 * ortho_half_h）/ ビューポート高
                            //   - SS オーバーレイ: 1 キャンバス px = 1 画面 px 固定
                            //   - WS（3D 透視）: 距離依存のため従来の固定間隔を維持
                            let outline_step = if use_ortho_2d_camera {
                                let half_h = self.canvas_cameras.get(&wl)
                                    .map(|c| c.ortho_half_h)
                                    .unwrap_or(vp_h_r / 2.0);
                                (2.0 * half_h) / vp_h_r.max(1.0)
                            } else if use_screen_space {
                                1.0
                            } else {
                                crate::engine::core::app_base::app::canvas_collect::OUTLINE_RING_STEP
                                    * canvas_scale_rect
                            };
                            collect_canvas_rects(
                                &scene.actors, &scene.world, wl, &mut lb, rect_col,
                                &self.selected_actor_dfs_ids, &mut counter,
                                None, IDENTITY_RECT, [1.0, 1.0],
                                canvas_scale_rect, y_sign_rect, viewport_size_rect, &canvas_vp_overrides_r,
                                &root_auto_sizes_r, edit_view_2d, outline_step,
                            );
                            // 2D シーンビューでドラッグホバー中のルートキャンバス枠を
                            // 通常枠より明るく・太くハイライト描画する（Phase 3、事前計算済み）
                            for (from, to, col) in &drag_hover_highlight_lines {
                                lb.add_line(*from, *to, *col);
                            }
                            if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) }
                        } else { None }
                    } else { None };

                    // ── 3D Canvas アウトライン（エディタモード）──────────────────────────────
                    // Actor3D + CanvasComponent を持つアクターの矩形境界を 3D ワールド空間で描画する。
                    // 選択時はオレンジ（選択色は全アウトライン共通）、
                    // 非選択時は緑（2D ルートキャンバス枠の基本色 #00ff00ff と統一）。
                    // 2D シーンビューでは 3D シーンごと非表示のため生成しない。
                    let canvas_3d_rect_batch = if in_editor && !edit_view_2d {
                        if let Some(scene) = &self.scene {
                            let wl = self.active_world_line;
                            let mut lb = LineBatch::new();
                            const RECT_COL_NORMAL:   [f32; 4] = [0.0, 1.0, 0.0, 1.0];
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

                                // ── ネストされた子キャンバスのアウトライン ──────────────────
                                // この 3D キャンバス配下に CanvasComponent を持つ子（Actor2D 等）が
                                // ある場合、その矩形枠も同じ 3D ワールド変換連鎖（canvas_to_world = m）で
                                // 描画する。スプライトの子走査と同一の変換のため、枠は描画スプライトと
                                // 一致する。ルートループは子へ再帰しないため（DFS カウンタは
                                // count_descendants で別途進む）、枠描画専用のローカル DFS カウンタを
                                // my_dfs+1（最初の子の DFS 番号）から開始し、選択色判定に使う。
                                let mut nested_outlines: Vec<([[f32; 3]; 4], u32)> = Vec::new();
                                let mut child_dfs = my_dfs + 1;
                                crate::engine::core::app_base::app::canvas_collect::collect_3d_canvas_child_outlines(
                                    &actor.children, &scene.world, wl, &mut child_dfs,
                                    Some([w, h]), m, [1.0, 1.0], &mut nested_outlines,
                                );
                                for (corners, dfs_id) in nested_outlines {
                                    let col = if self.selected_actor_dfs_ids.contains(&(dfs_id as usize)) {
                                        RECT_COL_SELECTED
                                    } else {
                                        RECT_COL_NORMAL
                                    };
                                    lb.add_line(corners[0], corners[1], col);
                                    lb.add_line(corners[1], corners[2], col);
                                    lb.add_line(corners[2], corners[3], col);
                                    lb.add_line(corners[3], corners[0], col);
                                }
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

                    // 2D シーンビューでは 3D Canvas 子スプライトも非表示のためアウトラインを生成しない
                    let sprite_3d_outline_items:
                        Vec<([[f32; 4]; 4], [f32; 4], Option<std::sync::Arc<GpuSpriteTexture>>)> =
                    if in_editor && !edit_view_2d && !self.selected_actor_dfs_ids.is_empty() {
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

                    // 選択アウトラインを main チャンネルへ積む（全て tex=None → 通常 1 バッチ）。
                    // 2D/3D スプライトと同一 main バッファへ連続配置し、ここで 1 度だけ upload する
                    // （Phase R6）。以降 main パス／オーバーレイパスは main_inst_buf を参照する。
                    let sprite_3d_outline_list = {
                        let mut sb = draw_ctx.sprites.borrow_mut();
                        let list = sb.main.push(sprite_3d_outline_items.into_iter());
                        sb.main.upload(&draw_ctx.device, &draw_ctx.queue);
                        list
                    };
                    // main パス記録で 'rp ライフタイムに使うためバッファハンドルを clone
                    let main_inst_buf = draw_ctx.sprites.borrow().main.buffer();
                    // ドローコール削減効果の [PERF] 可視化: main チャンネルの全リストの
                    // バッチ数（= ドローコール数）と総インスタンス数（= スプライト枚数）を集計する。
                    {
                        let lists = [
                            &sprite_prepared_2d_bg, &sprite_prepared_2d_fg,
                            &sprite_prepared_3d,    &sprite_3d_outline_list,
                        ];
                        perf_sprite_draws = lists.iter().map(|l| l.batches.len()).sum();
                        perf_sprite_insts = lists.iter()
                            .flat_map(|l| l.batches.iter())
                            .map(|b| b.count as usize)
                            .sum();
                    }

                    // 軸ギズモバッチ（エディタモード + show_axis_gizmo のみ）
                    // 2D シーンビューでは 3D カメラ方位ウィジェットは無意味のため非表示にする
                    let axis_gizmo_batch = if in_editor && self.show_axis_gizmo && !edit_view_2d {
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
                    // 2D シーンビューでは 3D カメラ投影が成立しないため生成しない。
                    let icon_overlay_batch = if in_editor && !edit_view_2d {
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

                    // ── HDR オフスクリーンの確保（Phase R3）──────────────
                    // シーン（メインパス＋キャンバスオーバーレイ）を Rgba16Float の HDR
                    // オフスクリーンへ描画し、後段のトーンマップパスでスワップチェーンへ出力する。
                    // ビネット有効時はトーンマップ前段用の HDR 中間も確保する。
                    // ※ ensure（&mut rt_pool）を view 取得（&rt_pool）より前に済ませ、ビュー借用が
                    //   メインパス〜キャンバスオーバーレイ〜トーンマップの全区間で安定するようにする。
                    //   hdr_view/inter_view はメインパスのブロック外でも参照するため、ここで宣言する。
                    let (surf_w, surf_h) = frame.surface_size();

                    // ── G-Buffer デバッグ表示（シーンビュー表示モード「G-Buffer: 〜」）──────
                    //
                    // エディタのシーンビューで G-Buffer の 1 チャンネルを生値のまま見るモード。
                    // 【設計の要点】unlit/wireframe と同じ仕組み（SceneViewMode::is_lit()==false）に
                    // 乗せると deferred_active が落ちてフォワードへ切り替わり、**可視化したい
                    // G-Buffer が生成されなくなる**（自己矛盾）。そこで G-Buffer 系モードは
                    // is_lit()==true を返してデファードを維持し、デファード・ライティングの
                    // フルスクリーンパスだけを可視化パス（gbuffer_debug.wgsl）へ差し替える。
                    //
                    // 有効条件は「Edit モード（＝シーンビュー）」かつ「デファードが設定 ON」かつ
                    // 「2D シーンビューでない」。Play・カメラプレビュー小窓には一切効かせない
                    // （プレビューは別 LightMeta／別パスなので構造上も混ざらない）。
                    // ここで前倒し計算するのは、後段の透明収集・各種 RT 確保の要否判定へ
                    // このフラグを配るため（依存するのは self のフィールドのみでフレーム内不変）。
                    let gbuffer_debug_channel = if self.mode == RuntimeMode::Edit
                        && self.post_fx.deferred
                        && !edit_view_2d
                    {
                        self.scene_view_mode.gbuffer_debug_channel()
                    } else {
                        None
                    };
                    // G-Buffer デバッグ表示中か（後段パスのスキップ判定に使う）。
                    let gbuffer_debug_active = gbuffer_debug_channel.is_some();

                    // ── シーンビュー「ワイヤーフレーム」モードか ─────────────────
                    // Edit モード（＝エディタのシーンビュー）かつ表示モードが Wireframe かつ
                    // GPU が POLYGON_MODE_LINE 対応のときだけ true。Play・非対応 GPU では false。
                    // 【ここで前倒し計算する理由】半透明の描画要否（has_tp）と G-Buffer RT の
                    // 確保要否（deferred_active）の両方がこのフラグに依存するため、透明収集より
                    // 前に確定させる必要がある。依存するのは self のフィールドのみでフレーム内不変。
                    let scene_wireframe = self.mode == RuntimeMode::Edit
                        && self.scene_view_mode.is_wireframe()
                        && crate::engine::core::renderer::wireframe_supported();

                    // ── 透明描画の対象収集（Phase R5）─────────────────────
                    // Blend マテリアルを持つバッチを (GpuModel, Batch) ペアで集める。
                    // 2D シーンビューは 3D を描かないため対象外。has_tp が false のときは
                    // 以降の透明処理（gather / パス / RT 確保）を一切行わずコストゼロにする。
                    let transparency_mode = self.post_fx.transparency;
                    let transparent_models: Vec<(
                        &crate::engine::methods::drawer::GpuModel,
                        &crate::engine::methods::drawer::InstancedModelBatch,
                    )> = if edit_view_2d {
                        Vec::new()
                    } else {
                        let mut models: Vec<(
                            &crate::engine::methods::drawer::GpuModel,
                            &crate::engine::methods::drawer::InstancedModelBatch,
                        )> = self.shared_model_batches.iter()
                            .filter_map(|(path, sd)| {
                                gpu_model_by_path.get(path.as_str()).map(|&gpu| (gpu, &sd.batch))
                            })
                            .collect();
                        // ── 地形散布モデル（kind=Model プロップ）の半透明プリミティブも透明パスへ ──
                        //   散布した木（searsia 等）の葉・小枝は glTF 上 alphaMode=BLEND の両面
                        //   マテリアルであり、不透明 G-Buffer パス（draw_gbuffer_indirect）は Blend を
                        //   スキップする。ここで透明モデル集合へ加えないと、散布木は幹（Opaque）だけが
                        //   描かれ葉が丸ごと消える（アクター配置の同一モデルは shared_model_batches
                        //   経由でここに含まれるため葉が出る＝両者のパリティを取る）。
                        //   バッチはアクターと同じ InstancedModelBatch なので、透明描画側は
                        //   lod_visible_counts / インスタンス行列をそのまま扱える。可視カリングは
                        //   cull_and_update_scatter_models が既に更新済み。
                        for res in self.terrain.scatter_models.values() {
                            models.push((&res.gpu_model, &res.batch));
                        }
                        models
                    };
                    // G-Buffer デバッグ表示中は半透明を一切描かない（生値の上に半透明が重なると
                    // 「G-Buffer に入っている値」が読めなくなるため）。has_tp を落とすことで
                    // 距離ソート／WBOIT／屈折背景ピラミッドの確保・パスがまとめてスキップされる。
                    //
                    // ワイヤーフレーム表示中も同様に「透明パス」は走らせない。半透明は
                    // 不透明フォワードパス（draw_model_indirect）側でワイヤ用パイプラインに
                    // 合流させ、不透明と同じ線として描く（model_drawer.rs の wireframe 引数）。
                    // ここを落とさないと「不透明だけ線・半透明は通常の塗りのまま」という
                    // 混在表示になる（本修正の対象不具合）。ワイヤは不透明な線であり
                    // 前後関係のブレンドが無いため、距離ソート／WBOIT は不要。
                    let has_tp = crate::engine::core::renderer::transparency::has_transparent(
                        &transparent_models,
                    ) && !gbuffer_debug_active && !scene_wireframe;
                    let tp_sorted = has_tp
                        && transparency_mode == crate::engine::core::renderer::TransparencyMode::DistanceSort;
                    let tp_wboit = has_tp
                        && transparency_mode == crate::engine::core::renderer::TransparencyMode::Wboit;

                    // ── デファード（G-Buffer + フルスクリーン・ライティング）経路にするか（Phase D3 Deferred Phase B）
                    // G-Buffer RT の確保要否をこの時点で判定する必要があるため、メインパス直前で
                    // 使う値（use_rt／メインパスの scene_wireframe 等）より前に前倒しで計算する。
                    // デファードはメインカメラの不透明・Lit のみが対象。unlit／ワイヤーフレーム／
                    // 2D シーンビューは常にフォワード（従来経路）で描く（G-Buffer 書き込みは Lit 専用
                    // パスであり、gbuffer.rs の draw_gbuffer_indirect コメント参照）。
                    // scene_wireframe は透明収集より前（gbuffer_debug_active の直後）で算出済み。
                    // scene_is_lit: Play 中・非 Edit は常に Lit 扱い（scene_view_mode_code と同じ規約、
                    // 972-980 行目参照）。Edit 中はシーンビューの表示モードに従う。
                    let scene_is_lit = self.mode != RuntimeMode::Edit || self.scene_view_mode.is_lit();
                    let deferred_active = self.post_fx.deferred
                        && !edit_view_2d && !scene_wireframe && scene_is_lit;
                    // 反射（Phase D6）: deferred 有効時のみ・resolved の実効反射モード。
                    // フォワード（deferred 無効）時は反射パスを一切走らせない（Off）。
                    // G-Buffer デバッグ表示中は反射合成も行わない（HDR へ加算されると生値が濁る）。
                    let reflection_effective = if deferred_active && !gbuffer_debug_active {
                        resolved_features.reflection
                    } else {
                        crate::engine::core::renderer::ReflectionMode::Off
                    };
                    // AO（Phase D4）: deferred 有効時のみ・resolved の実効 AO モード。
                    // フォワード（deferred 無効）時は AO パスを一切走らせない（Off）。
                    // deferred ゲートはここで行う（render_features::resolve は RT 降格のみ担当）。
                    // G-Buffer デバッグ表示中は AO を走らせない（AO はライティングへの入力であり、
                    // 可視化パスは AO を読まないので計算するだけ無駄）。
                    let ao_effective = if deferred_active && !gbuffer_debug_active {
                        resolved_features.ao
                    } else {
                        crate::engine::core::renderer::AoMode::Off
                    };
                    // SSGI（Phase SSGI）: deferred 有効かつ実効 GI モードが Ssgi のときのみ走る独立パス。
                    // フォワード（deferred 無効）時は SSGI を一切走らせず GI はフラットへ倒れる
                    //（Ssgi→Flat の deferred ゲート。render_features::resolve は RT 降格のみ担当）。
                    // G-Buffer デバッグ表示中は SSGI も走らせない（同上。scene_hdr は可視化色で
                    // 上書きされるため、そこから焼いた GI は次フレームの汚染源にしかならない）。
                    let ssgi_active = deferred_active
                        && !gbuffer_debug_active
                        && resolved_features.gi == crate::engine::core::renderer::GiMode::Ssgi;

                    // 水面を描くフレームか（Phase W1 のゲート。Phase W5.3 でここへ前倒し）。
                    // 2D 編集ビュー・ワイヤーフレーム・G-Buffer デバッグ中は水を描かない。
                    // 水ボリュームが 0 個のフレームも同様で、この場合はコースティクス用の
                    // テクスチャ確保も水面パスも一切行わない（＝完全にコスト 0）。
                    // **コースティクスの確保判定（下の ensure 群）より前に必要**なので、
                    // 実際に水面パスを記録する箇所（メインパス中）ではなくここで算出する。
                    let water_gate = !edit_view_2d && !scene_wireframe && !gbuffer_debug_active
                        && !water_volumes.is_empty();
                    // 水中コースティクス（Phase W5.3）を焼きうるフレームか。
                    // deferred のライティングへ入力する機能なので、フォワード時は走らせない。
                    // 実際に走らせるかは `prepare` の成否（＝描画インスタンスが 1 つ以上出たか）
                    // まで見て後段で確定する（caustics_active）。
                    let caustics_possible = deferred_active && !gbuffer_debug_active && water_gate;

                    // G-Buffer デバッグ表示中はビネットも掛けない（画面端が暗くなると
                    // 「チャンネル値が小さい」のか「ビネットで暗い」のか区別できなくなる）。
                    let vignette_on = self.post_vignette_enabled && !gbuffer_debug_active;
                    // Phase R4: ブルーム／FXAA 設定（フレーム内で不変のためコピーしておく）。
                    // G-Buffer デバッグ表示中はブルームを掛けない（高輝度が滲むと生値が読めない）。
                    let bloom_on = self.post_fx.bloom_enabled && !gbuffer_debug_active;
                    let fxaa_on  = self.post_fx.fxaa_enabled;
                    self.rt_pool.ensure(
                        &draw_ctx.device,
                        crate::engine::core::renderer::RT_SCENE_HDR,
                        surf_w, surf_h,
                        crate::engine::core::renderer::HDR_FORMAT,
                    );
                    // R4: トーンマップ後 LDR 中間（常時確保）。2D オーバーレイをこの上へ描き、
                    //     最終段 FXAA／コピーでスワップチェーンへ出す（R3 の 2D 暗化課題を解消）。
                    self.rt_pool.ensure(
                        &draw_ctx.device,
                        crate::engine::core::renderer::RT_LDR,
                        surf_w, surf_h,
                        crate::engine::core::renderer::HDR_FORMAT,
                    );
                    if vignette_on {
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::RT_POST_INTER,
                            surf_w, surf_h,
                            crate::engine::core::renderer::HDR_FORMAT,
                        );
                    }
                    // R4: ブルーム mip 群（有効時のみ確保）。段数・サイズは解像度から算出。
                    let bloom_targets = if bloom_on {
                        crate::engine::core::renderer::BloomPipelines::ensure_targets(
                            &mut self.rt_pool, &draw_ctx.device,
                            crate::engine::core::renderer::HDR_FORMAT, surf_w, surf_h,
                        )
                    } else { Vec::new() };
                    // WBOIT の accum/reveal RT（WBOIT 方式かつ透明物ありのときのみ確保）。
                    if tp_wboit {
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::RT_WBOIT_ACCUM,
                            surf_w, surf_h,
                            crate::engine::core::renderer::WBOIT_ACCUM_FORMAT,
                        );
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::RT_WBOIT_REVEAL,
                            surf_w, surf_h,
                            crate::engine::core::renderer::WBOIT_REVEAL_FORMAT,
                        );
                    }
                    // G-Buffer 4 枚（deferred_active のときのみ確保。OFF 時は 0 コスト）。
                    // フォーマットは gbuffer.rs の GBUFFER0..3_FORMAT（RtPool 側は名前でキャッシュ／
                    // フォーマット変更時に再生成する既存の ensure 流儀）。
                    if deferred_active {
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::gbuffer::GBUFFER0_RT_NAME,
                            surf_w, surf_h,
                            crate::engine::core::renderer::gbuffer::GBUFFER0_FORMAT,
                        );
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::gbuffer::GBUFFER1_RT_NAME,
                            surf_w, surf_h,
                            crate::engine::core::renderer::gbuffer::GBUFFER1_FORMAT,
                        );
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::gbuffer::GBUFFER2_RT_NAME,
                            surf_w, surf_h,
                            crate::engine::core::renderer::gbuffer::GBUFFER2_FORMAT,
                        );
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::gbuffer::GBUFFER3_RT_NAME,
                            surf_w, surf_h,
                            crate::engine::core::renderer::gbuffer::GBUFFER3_FORMAT,
                        );
                        // RT4: 速度（モーションベクタ）。Rg16Float = 4 byte/px。
                        // 消費者（TAA / モーションブラー）はまだ存在しないが、
                        // deferred が有効なフレームでは常に生成しておく
                        // （第2層の生成物として「常にバインド可能」であることが契約）。
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::gbuffer::GBUFFER_VELOCITY_RT_NAME,
                            surf_w, surf_h,
                            crate::engine::core::renderer::gbuffer::GBUFFER_VELOCITY_FORMAT,
                        );
                    }
                    // AO（Phase D4）半解像度テクスチャ（ao_raw/ao_a/ao_b）。deferred 有効かつ
                    // AO モードが Off でないときのみ確保（Off 時は 0 コスト）。STORAGE 用途のため
                    // RtPool ではなく AoTargets が専有する（half-res でコスト 1/4）。
                    if deferred_active && ao_effective != crate::engine::core::renderer::AoMode::Off {
                        let div = crate::engine::core::renderer::AO_RESOLUTION_DIVISOR;
                        self.ao_targets.ensure(
                            &draw_ctx.device,
                            (surf_w / div).max(1),
                            (surf_h / div).max(1),
                        );
                    }
                    // RT ソフト影マスク（Phase RT-Shadow-Denoise）半解像度 4 レイヤテクスチャ。
                    // deferred 有効かつ RT 影オンかつソフト影ライト選定ありかつ RT 対応 GPU
                    // （shadow_mask パイプライン存在）のときだけ確保・生成する（それ以外は 0 コスト）。
                    // このとき scene_lights_bg は RT 複合 BG（TLAS 入り）になる（use_rt=rt_on）。
                    // G-Buffer デバッグ表示中はシャドウマスクも生成しない（ライティング専用の入力）。
                    let shadow_mask_active = deferred_active
                        && !gbuffer_debug_active
                        && rt_on
                        && !shadow_mask_selection.is_empty()
                        && draw_ctx.pipelines.shadow_mask.is_some();
                    if shadow_mask_active {
                        let div = crate::engine::core::renderer::SHADOW_MASK_RESOLUTION_DIVISOR;
                        self.shadow_mask_targets.ensure(
                            &draw_ctx.device,
                            (surf_w / div).max(1),
                            (surf_h / div).max(1),
                        );
                    }
                    // 水中コースティクス（Phase W5.3）のフル解像度 1ch テクスチャ。
                    // 消費側（deferred ライティング）が textureLoad で 1:1 に読むためフル解像度。
                    // R16Float の 1ch なので 1920x1080 でも約 4MB（G-Buffer 1 枚より軽い）。
                    if caustics_possible {
                        self.caustics_targets.ensure(&draw_ctx.device, surf_w, surf_h);
                    }
                    // 水面反射 RT（Phase W5.2）のフル解像度 HDR テクスチャ。
                    // ゲートは「deferred 有効（既定 ON）＋水域があるフレーム」。
                    // **D6 の全体反射モード（既定 Off）には連動させない** — 水面反射は
                    // WaterVolume ごとの reflection_intensity（0 で無効）が独立のスイッチであり、
                    // 全体モード頼みにすると既定設定で水面が一切反射しない罠になる
                    //（実際にユーザーが「何も反射しない」を踏んだため切り離した）。
                    // 水面パスは結果を **textureLoad で 1:1 に読む**ので
                    // 半解像度にはしない（補間で反射像が滲むうえ、波の高周波が失われる）。
                    let water_reflection_possible = deferred_active && water_gate;
                    if water_reflection_possible {
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::RT_WATER_REFLECTION_NAME,
                            surf_w, surf_h,
                            crate::engine::core::renderer::WATER_REFLECTION_FORMAT,
                        );
                    }
                    // ── 粗さブラー（Phase W5.2 品質改善）の半径とターゲット ──
                    //
                    // 【なぜ「可視ボリュームの最大粗さで一様半径」なのか（v1 の制限）】
                    //  反射のぼけは本来ボリュームごと（＝画素ごと）に半径が違うべきだが、
                    //  いもす法の分離ボックスは「走査線全体で同じ半径」を前提とする
                    //  （ランニング和が窓幅一定でしか成立しない）。画素ごと半径にするには
                    //  半径別に複数枚ぼかして合成する必要があり、v1 の価値に対して重い。
                    //  そこで**このフレームに映る水域の最大粗さ**で一様にぼかす。
                    //  粗さの違う水域が同時に映るときは、粗い方に引きずられてぼける。
                    //
                    // 反射強度 0 の水域は反射そのものが出ないので、粗さの集計から外す
                    // （「反射を切った粗い池」が隣の鏡の池をぼかす、という理不尽を防ぐ）。
                    let water_refl_max_roughness = if water_reflection_possible {
                        water_volumes.iter()
                            .filter(|v| v.visual.reflection_intensity > 0.0)
                            .map(|v| v.visual.reflection_roughness.clamp(0.0, 1.0))
                            .fold(0.0f32, f32::max)
                    } else {
                        0.0
                    };
                    let water_refl_blur_radius =
                        crate::engine::core::renderer::water_reflection_blur_radius_px(
                            water_refl_max_roughness, surf_h);
                    // 半径 0（粗さ 0＝完全な鏡面）なら 1 バイトも確保しない。
                    let water_refl_blur_active =
                        water_reflection_possible && water_refl_blur_radius > 0;
                    if water_refl_blur_active {
                        self.water_reflection_blur_targets.ensure(&draw_ctx.device, surf_w, surf_h);
                    }
                    // SSGI（Phase SSGI）半解像度テクスチャ（ssgi_raw/ssgi_a/ssgi_b）。SSGI 有効時のみ確保。
                    // ensure が再確保（初回・リサイズ）を返したら ssgi_b の前フレーム履歴が消えるため、
                    // この 1 フレームは未収束扱い（ssgi_warmed=false）にして GI をフラットに倒す。
                    // 前フレームが SSGI 非 active だった場合も履歴が古い可能性があるため未収束扱いにする。
                    let ssgi_reallocated = if ssgi_active {
                        let div = crate::engine::core::renderer::SSGI_RESOLUTION_DIVISOR;
                        self.ssgi_targets.ensure(
                            &draw_ctx.device,
                            (surf_w / div).max(1),
                            (surf_h / div).max(1),
                        )
                    } else { false };
                    // このフレームで SSGI を実際に「読める」か（前フレームの ssgi_b が有効か）。
                    // ssgi_active かつ 前フレームも収束済み（self.ssgi_warmed）かつ 今フレーム再確保なし。
                    let ssgi_readable = ssgi_active && self.ssgi_warmed && !ssgi_reallocated;
                    // 次フレーム用の収束フラグを更新: 今フレーム SSGI パスを走らせる（ssgi_active）なら、
                    // このフレーム末に ssgi_b へ結果が残るので次フレームは読める。非 active なら false。
                    self.ssgi_warmed = ssgi_active;
                    // 反射 RT（Phase D6）: deferred 有効かつ反射モードが Off でないときのみ確保。
                    // SSR/RT どちらも同じ RT_REFLECTION（HDR）へ描く。
                    if reflection_effective != crate::engine::core::renderer::ReflectionMode::Off {
                        self.rt_pool.ensure(
                            &draw_ctx.device,
                            crate::engine::core::renderer::RT_REFLECTION_NAME,
                            surf_w, surf_h,
                            crate::engine::core::renderer::REFLECTION_FORMAT,
                        );
                    }
                    // 屈折の背景 RT（Phase RT-Translucency）: translucency=Rt かつ deferred 有効かつ
                    // 半透明ありのフレームで確保する。scene_hdr と同サイズ・同フォーマット
                    // （copy_texture_to_texture のコピー元／先を揃えるため）。屈折はスクリーンスペースだが
                    // 背景コピーを deferred の不透明ライティング完成後に置くため deferred 有効を条件にする。
                    let refract_active = translucency_rt_on && deferred_active && has_tp;
                    if refract_active {
                        // すりガラス用の屈折背景ミップチェーン（ガラス表現）を確保する。
                        // mip0=不透明シーン HDR のコピー・以降=ダウンサンプル→いもす法ブラー。
                        self.refract_pyramid.ensure(&draw_ctx.device, surf_w, surf_h);
                    }
                    // sequential grab（屈折の逐次グラブ）を実行するか（Phase RT-Translucency 拡張）。
                    // 距離ソート（tp_sorted）かつ 屈折背景アクティブ（refract_active）かつ 設定 ON のときのみ。
                    // 有効時は、半透明ガラスを 1 つ描くたびに scene_hdr → 屈折背景ピラミッドを再グラブし、
                    // 後続のガラスが「先に描かれたガラス」を背景として拾えるようにする（ガラス越しのガラス）。
                    // WBOIT（tp_wboit）は accum/reveal へ順序独立に蓄積する方式で「先に描いたガラス」という
                    // 概念が無く、逐次背景更新が意味を成さないため、設定 ON でも従来動作（本フラグは false のまま）。
                    // RT/SS どちらの屈折経路も同じ屈折背景ピラミッド（full_view）をサンプルするため、
                    // 本方式は両経路に等しく効く（背景の中身が更新されるだけで BindGroup は不変）。
                    let refract_sequential_active =
                        tp_sorted && refract_active && self.post_fx.refract_sequential_grab;
                    let hdr_view   = self.rt_pool.view(crate::engine::core::renderer::RT_SCENE_HDR);
                    // メインカメラ用の透明 group4 BindGroup（Phase RT-Translucency）。
                    // 透明パイプラインは group4 に屈折背景（binding15/16）を要求するため、距離ソート／WBOIT
                    // 両方で使うこの BG をパスより前に生成して長生きさせる（refract_active なら実背景、
                    // 非 active はダミー 1x1）。屈折ビット（bit1）は背景コピー時に LightMeta へ追記する。
                    let transparent_bg_main = {
                        let refract_view = if refract_active {
                            self.refract_pyramid.full_view()
                        } else {
                            draw_ctx.pipelines.transparent.dummy_refract_view()
                        };
                        draw_ctx.light_buffer.create_transparent_bind_group(
                            &draw_ctx.device,
                            &draw_ctx.pipelines.transparent.lights_bgl,
                            &draw_ctx.shadow,
                            &draw_ctx.clusters,
                            &draw_ctx.gi,
                            LightingPass::MainCamera,
                            refract_view,
                            &draw_ctx.pipelines.transparent.refract_sampler,
                        )
                    };
                    // 本物の RT 屈折を使うか（Phase RT-Translucency）。translucency=Rt で屈折背景アクティブかつ
                    // RT 透明パイプラインが存在（＝RT 対応 GPU）のとき。実 BindGroup は TLAS 構築後（メインパス
                    // 内）に代入する（needs_tlas()＝translucency=Rt で TLAS 構築が保証される）。None のときは
                    // SS 版フォールバック（transparent_bg_main）を使う（非対応 GPU・translucency≠Rt と完全同一経路）。
                    let use_rt_refract = refract_active && draw_ctx.pipelines.transparent.rt.is_some();
                    let mut transparent_rt_bg_main: Option<wgpu::BindGroup> = None;
                    let ldr_view   = self.rt_pool.view(crate::engine::core::renderer::RT_LDR);
                    // WBOIT ターゲットのビュー（確保済みのときのみ Some）。
                    let (wboit_accum_view, wboit_reveal_view) = if tp_wboit {
                        (
                            Some(self.rt_pool.view(crate::engine::core::renderer::RT_WBOIT_ACCUM)),
                            Some(self.rt_pool.view(crate::engine::core::renderer::RT_WBOIT_REVEAL)),
                        )
                    } else {
                        (None, None)
                    };
                    // G-Buffer 4 枚ぶんのビュー（deferred_active のときのみ Some）。
                    // ensure を全て済ませてから view を取る（&mut → & の借用切り替え、既存の WBOIT と同じ規約）。
                    let (g0v, g1v, g2v, g3v, gvel) = if deferred_active {
                        (
                            Some(self.rt_pool.view(crate::engine::core::renderer::gbuffer::GBUFFER0_RT_NAME)),
                            Some(self.rt_pool.view(crate::engine::core::renderer::gbuffer::GBUFFER1_RT_NAME)),
                            Some(self.rt_pool.view(crate::engine::core::renderer::gbuffer::GBUFFER2_RT_NAME)),
                            Some(self.rt_pool.view(crate::engine::core::renderer::gbuffer::GBUFFER3_RT_NAME)),
                            // RT4: 速度（モーションベクタ）。
                            Some(self.rt_pool.view(crate::engine::core::renderer::gbuffer::GBUFFER_VELOCITY_RT_NAME)),
                        )
                    } else {
                        (None, None, None, None, None)
                    };
                    let inter_view = if vignette_on {
                        Some(self.rt_pool.view(crate::engine::core::renderer::RT_POST_INTER))
                    } else { None };
                    // 反射 RT のビュー（確保済みのときのみ Some）。ensure 済み後に view を取る規約。
                    let reflection_view = if reflection_effective != crate::engine::core::renderer::ReflectionMode::Off {
                        Some(self.rt_pool.view(crate::engine::core::renderer::RT_REFLECTION_NAME))
                    } else { None };
                    // 水面反射 RT のビュー（確保済みのときのみ Some。ensure 済み後に取る規約）。
                    let water_reflection_view = if water_reflection_possible {
                        Some(self.rt_pool.view(crate::engine::core::renderer::RT_WATER_REFLECTION_NAME))
                    } else { None };

                    // ── メインレンダーパス ────────────────
                    let _perf_t_main = std::time::Instant::now();
                    {
                        // Play モード: ゲームカメラのクリアカラーで全体クリア
                        // （帯エリアは begin_render_pass 後に BarFillPipeline で別途塗りつぶす）
                        // Edit モード: アクター編集タブ・2D シーンビューは紺色、通常はダークグレー
                        // ── シャドウ深度パス（Phase R2, メインパス直前）──────────────
                        // skin compute（joints 書き込み済み）後・メインパス前に、各カスケード/
                        // スポットへシャドウキャスターの深度を描画する。メインパスは group 4
                        // 複合 BG（binding 2〜5）経由でこの深度をサンプルする。
                        // キャスターが無ければ 0 コストでスキップ。
                        if shadow_plan.any() {
                            let mut shadow_casters: Vec<(
                                &crate::engine::methods::drawer::GpuModel,
                                &crate::engine::methods::drawer::InstancedModelBatch,
                            )> = self.shared_model_batches.iter()
                                .filter(|(path, _)| shadow_caster_paths.contains(path.as_str()))
                                .filter_map(|(path, sd)| {
                                    gpu_model_by_path.get(path.as_str()).map(|&gpu| (gpu, &sd.batch))
                                })
                                .collect();
                            // 散布モデル（kind=Model）も影キャスターに加える（Terrain T3 第2段）。
                            //   TLAS/RT 反射登録は今回スコープ外だが、ラスタのシャドウマップには
                            //   通常メッシュと同じ (GpuModel, InstancedModelBatch) を渡すだけで乗る。
                            for res in self.terrain.scatter_models.values() {
                                shadow_casters.push((&res.gpu_model, &res.batch));
                            }
                            if !shadow_casters.is_empty() {
                                draw_ctx.shadow.record(
                                    frame.encoder_mut(),
                                    &draw_ctx.pipelines.shadow_depth,
                                    &shadow_plan,
                                    &shadow_casters,
                                );
                            }
                        }

                        // ── RT 影の加速構造ビルド（Phase R8, メインパス直前）──────────
                        // RT 影オン時（rt_on）のみ実行。BLAS（メッシュ単位, 初回のみキャッシュ）と
                        // TLAS（cast_shadows=true の全インスタンス, カメラカリング前）をこの command
                        // encoder に記録する。RT 影オフ時は一切ビルドしない（コスト・ログともゼロ）。
                        // RT 用 BindGroup を bind するのも rt_on のときだけなので、bind 時点で必ず
                        // このビルドが同フレーム先行しており TLAS はビルド済みが保証される。
                        // DDGI（Phase RT-GI）を今フレーム走らせるか。RT 対応（attach 済み）かつ
                        // 実効 GI モードが Rt。GI は TLAS を必要とするため、needs_tlas() 経由で
                        // RT 影が無効でも GI が Rt なら TLAS を構築する（下のゲート参照）。
                        let gi_on = draw_ctx.gi.is_attached() && resolved_features.rt_gi();
                        // TLAS 構築ゲートの一般化: いずれかの機能が Rt に解決されれば構築する。
                        // 将来 Reflection/AO/Translucency の Rt が resolve を通れば、ここを触らず
                        // 自動で TLAS が構築される（needs_tlas() に集約）。RT 加速構造リソース
                        // （rt_shadow）が無い GPU では構築しない。
                        if draw_ctx.rt_shadow.is_some() && resolved_features.needs_tlas() {
                            if let Some(rt_cell) = draw_ctx.rt_shadow.as_ref() {
                                let mut rt = rt_cell.borrow_mut();
                                // 散布モデル（kind=Model）の RT キャスターキーを先に確保して寿命を持たせる。
                                //   キーは "scatter://{prop_index}/{model_path}"。
                                //   ECS アクター（shared_model_batches）の batch_key とは名前空間を分けて、
                                //   frame_renderer の stale prune（prune_source_paths）が散布 BLAS を巻き添えで
                                //   解放しない（＝再ビルド churn を避ける）ようにする。
                                //   String は rt_casters（借用）より長生きさせる必要があるため、ここで所有 Vec に集める。
                                //
                                // 【なぜ prop_index を含めるのか（Phase RT-Skin で顕在化した衝突）】
                                //   `scatter_models` は prop_index（プロップ定義の添字）でキー付けされており、
                                //   **同じモデルパスを使うプロップが 2 つあると同一キーのバッチが 2 件並ぶ**。
                                //   非スキンは BLAS をパス単位で共有するだけなので無害だったが、スキンの
                                //   BLAS キーは (batch_key, mesh, prim, inst) なので別プロップの別インスタンスが
                                //   同一キーへ衝突し、同じ出力バッファへバリア無しに 2 回書き込む競合／
                                //   同一 BLAS のビルドエントリ重複／別変換で同じポーズが 2 体出る、という
                                //   実害が出る。prop_index を含めればキーは構造的に一意になる。
                                //
                                // 【副作用（挙動変更・許容と判断）】同一モデルを使う複数プロップ間で
                                //   非スキン BLAS が共有されなくなり、BLAS の VRAM がプロップ数ぶん増える。
                                //   プロップ定義数は少数（数個〜十数個）なので許容範囲。
                                //   docs/rendering_roadmap.md の既知事項にも記載している。
                                let scatter_rt: Vec<(
                                    String,
                                    &crate::engine::methods::drawer::GpuModel,
                                    &crate::engine::methods::drawer::InstancedModelBatch,
                                )> = self.terrain.scatter_models.iter()
                                    .map(|(prop_index, res)| (
                                        format!("scatter://{}/{}", prop_index, res.model_path),
                                        &res.gpu_model,
                                        &res.batch,
                                    ))
                                    .collect();
                                let mut rt_casters: Vec<(
                                    &str,
                                    &crate::engine::methods::drawer::GpuModel,
                                    &crate::engine::methods::drawer::InstancedModelBatch,
                                )> = self.shared_model_batches.iter()
                                    .filter(|(path, _)| shadow_caster_paths.contains(path.as_str()))
                                    .filter_map(|(path, sd)| {
                                        gpu_model_by_path.get(path.as_str())
                                            .map(|&gpu| (path.as_str(), gpu, &sd.batch))
                                    })
                                    .collect();
                                // 散布モデルを TLAS キャスターに追加する。
                                //   res.batch は毎フレームの cull_and_update_scatter_models で「可視チャンクの
                                //   インスタンスだけ」に更新済み（rt_enumerate は num_instances＝可視数のみ列挙）。
                                //   ＝視錐台＋距離カリング済みの可視集合をそのまま流用して TLAS 登録数を抑える
                                //   （近傍・可視に絞る、というタスク要件を既存カリングで満たす）。総数が
                                //   MAX_RT_INSTANCES(4096) を超えた分は prepare_and_build 側で警告付きクランプされる。
                                for (key, gpu, batch) in &scatter_rt {
                                    rt_casters.push((key.as_str(), gpu, batch));
                                }
                                let _perf_t_tlas = std::time::Instant::now();
                                // バインドレス（B2）: 対応 GPU では instance_table も同時に詰めさせる。
                                // rt_shadow とは別 RefCell のため共有借用で共存できる。
                                let bindless_ref = draw_ctx.bindless.as_ref().map(|c| c.borrow());
                                // スキンメッシュ（Phase RT-Skin）: 変形後頂点の書き出し compute を
                                // 渡す。この encoder には既に skin compute（ジョイント行列）が
                                // 積まれているため、prepare_and_build 内で
                                // 「変形 compute → 加速構造ビルド」を続けて積めば依存が満たされる。
                                let stat = rt.prepare_and_build(
                                    &draw_ctx.device, &draw_ctx.queue, frame.encoder_mut(),
                                    &rt_casters, bindless_ref.as_deref(),
                                    Some(&draw_ctx.pipelines.skin_deform),
                                );
                                perf_tlas_ms    = _perf_t_tlas.elapsed().as_secs_f64() * 1000.0;
                                perf_tlas_built = stat.built;
                                perf_tlas_insts = stat.instances;
                                perf_tlas_skin  = stat.skin_instances;
                                perf_tlas_skin_ms = stat.skin_build_ms;
                            }
                        }

                        // ── 本物の RT 屈折の透明用 group4 BG（Phase RT-Translucency）─────────
                        // TLAS 構築の直後に作る（use_rt_refract のときだけ）。屈折レイの TLAS(6)＋界面の
                        // 平均アルベド(14)＋屈折背景(15/16) を含む superset を 1 本にまとめる。BindGroup は
                        // wgpu の内部参照で TLAS を生かすため、rt の共有借用はここで閉じてよい（描画時まで有効）。
                        // use_rt_refract ⟹ refract_active ⟹ refract_pyramid は ensure 済み（full_view が有効）。
                        if use_rt_refract {
                            // RT 屈折パイプライン（rt_tp）はバインドレス対応時のみ構築されるため、
                            // ここでバインドレス資源も必ず Some（界面法線復元の storage を binding17/18/19 に差す）。
                            if let (Some(rt_cell), Some(rt_tp), Some(bl_cell)) = (
                                draw_ctx.rt_shadow.as_ref(),
                                draw_ctx.pipelines.transparent.rt.as_ref(),
                                draw_ctx.bindless.as_ref(),
                            ) {
                                let rt = rt_cell.borrow();
                                let bl = bl_cell.borrow();
                                let refract_view = self.refract_pyramid.full_view();
                                transparent_rt_bg_main = Some(draw_ctx.light_buffer.create_transparent_rt_bind_group(
                                    &draw_ctx.device,
                                    &rt_tp.lights_bgl,
                                    &draw_ctx.shadow,
                                    &draw_ctx.clusters,
                                    &draw_ctx.gi,
                                    rt.tlas(),
                                    rt.albedo_buffer(),
                                    refract_view,
                                    &draw_ctx.pipelines.transparent.refract_sampler,
                                    bl.instance_table_buffer(),
                                    bl.index_buffer(),
                                    bl.normal_buffer(),
                                ));
                            }
                        }

                        // ── DDGI: プローブ更新 compute（Phase RT-GI）─────────────────
                        // 上で構築した TLAS（RT 影と共有）へレイを飛ばし、プローブの八面体アトラス
                        // （放射輝度＋可視性）をローテーション更新する。GiParams は毎フレーム書き込む
                        // （gi_on=false のときは enabled=0 を書き、描画側はフラットアンビエントへ戻る）。
                        {
                            // プローブ格子をシーン（全静的バッチのワールド AABB 合併）へフィットさせる。
                            // world_aabbs は描画カリングで計算・キャッシュされるため 1 フレーム遅れることがある
                            // （静止シーンでは安定。理想はモデル変化時のみ再計算）。
                            let mut any = false;
                            let mut mn = [f32::INFINITY; 3];
                            let mut mx = [f32::NEG_INFINITY; 3];
                            for (_path, sd) in &self.shared_model_batches {
                                if let Some((bmn, bmx)) = sd.batch.world_bounds() {
                                    any = true;
                                    for i in 0..3 {
                                        mn[i] = mn[i].min(bmn[i]);
                                        mx[i] = mx[i].max(bmx[i]);
                                    }
                                }
                            }
                            if any {
                                draw_ctx.gi.fit(mn, mx);
                            }
                            // GI 方式コードと enabled を決める（evaluate_gi_ambient の分岐に使う）。
                            //   DDGI 有効（gi_on）      → enabled=1, gi_mode=DDGI（compute も走らせる）
                            //   SSGI 読み取り可          → enabled=1, gi_mode=SSGI（compute は走らせない）
                            //   それ以外（フラット/未収束）→ enabled=0, gi_mode=FLAT（描画側フラットへ）
                            // ※SSGI の未収束（初回/リサイズ/有効化直後）フレームは ssgi_readable=false と
                            //   なりここでフラットに倒れる（＝初回はゼロクリア＝フラットアンビエントと等価）。
                            use crate::engine::core::renderer::ddgi::{GI_MODE_FLAT, GI_MODE_DDGI, GI_MODE_SSGI};
                            let (gi_enabled, gi_mode_code) = if gi_on {
                                (true, GI_MODE_DDGI)
                            } else if ssgi_readable {
                                (true, GI_MODE_SSGI)
                            } else {
                                (false, GI_MODE_FLAT)
                            };
                            // GiParams を書き込み、更新プローブ数（ディスパッチ数）を得る。
                            let gi_ppf = draw_ctx.gi.update_params(&draw_ctx.queue, &self.post_fx.gi, gi_enabled, gi_mode_code);
                            // DDGI 有効時のみ compute を記録（履歴コピー → プローブ更新）。SSGI は compute 不要。
                            if gi_on {
                                if let Some(gip) = draw_ctx.pipelines.gi_update.as_ref() {
                                    draw_ctx.gi.record(frame.encoder_mut(), &gip.pipeline, gi_ppf);
                                }
                            }
                        }

                        // ── Play ビューポートの実サーフェスへの集約クランプ（set_viewport 安全化）──
                        // ここまでの game_viewport は win_w_f/win_h_f（＝on_resize が surface を
                        // configure した基準サイズ＝親クライアント矩形）から計算している。
                        // ただしスワップチェーンは configure の要求サイズを実ウィンドウの
                        // currentExtent へクランプすることがあり（mod.rs begin_frame 参照）、
                        // リサイズ直後の 1 フレームは実サーフェス（frame.surface_size()）と食い違う。
                        // この食い違いのまま set_viewport すると wgpu バリデーションがパニックし、
                        // 続けて未 present の SurfaceTexture 破棄で二次パニック→プロセス abort する。
                        // そこで実サーフェスサイズへ **一度だけ** クランプし、以降の全 set_viewport
                        // 箇所（G-Buffer/ライティング/反射/メイン/逐次屈折）を一括で安全化する
                        // （対症の if 散乱ではなくこの 1 点に集約）。
                        // 縮退（幅/高さ 1px 未満）時は play_viewport_ok=false とし、各 Play 描画で
                        // set_viewport をスキップさせる（そのフレームはターゲット全面のデフォルト
                        // ビューポートで描く。リサイズ最中の一過性フレームのため実害はない）。
                        let (rt_surf_w, rt_surf_h) = frame.surface_size();
                        let play_viewport_ok = if self.mode == RuntimeMode::Play && !self.paused {
                            match clamp_viewport_to_target(
                                game_viewport, rt_surf_w as f32, rt_surf_h as f32,
                            ) {
                                Some(clamped) => { game_viewport = clamped; true }
                                None          => false,
                            }
                        } else {
                            true
                        };

                        // ── 確定したビューポート矩形をカメラ UBO へ反映（深度→ワールド復元用）──
                        // NDC はビューポート矩形へ写像されるため、フルスクリーン再構成パス
                        // （deferred ライティング / シャドウマスク / AO / SSGI / 反射）は
                        // frag_coord をこの矩形で正規化しなければワールド座標が横滑りする。
                        // クランプ後の値がここで初めて確定するため、この 1 点で上書きする。
                        // play_viewport_ok=false のフレームは set_viewport 自体をスキップする
                        // ＝ NDC が RT 全面へ写像されるので、矩形も RT 全面にしておく。
                        {
                            let vp = if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                [game_viewport.0, game_viewport.1, game_viewport.2, game_viewport.3]
                            } else {
                                [0.0, 0.0, rt_surf_w as f32, rt_surf_h as f32]
                            };
                            camera_buf.update_viewport(&draw_ctx.queue, vp);
                        }

                        // ── Clustered Lighting: クラスタ構築 compute（Phase C1）─────────
                        // メインカメラの視錐台を 16×9×24 の 3D フロクセルへ分割し、各クラスタへ
                        // 影響する局所ライト（point/spot/rect）のインデックスを集める。
                        // メインパス（不透明・距離ソート透明・WBOIT・ギズモアイコン）の
                        // フラグメントはこの結果だけを走査する。
                        //
                        // 【クラスタはカメラごとに固有】ここで作るのは**メインカメラぶんだけ**。
                        // カメラプレビューのパス（上で記録済み）は CameraPreview 側の BindGroup
                        // （ClusterParams.enabled=0）を bind しており、この結果に一切依存しない
                        // （従来の全ライト線形走査）。同様にプレビューの CSM も専用資源
                        // （draw_ctx.shadow_preview）で別に構築済みで、メインカメラに依存しない。
                        //
                        // 【透視カメラ以外は無効化】2D オルソ・正射ゲームカメラでは
                        // saved_shadow_cam が None になる。指数 Z スライスは透視前提のため、
                        // その場合はクラスタを無効化して線形走査へフォールバックする
                        // （＝ライトが落ちて暗転することはない）。
                        {
                            // 実際に set_viewport する矩形。Play のレターボックス時はゲーム領域。
                            // frag_coord はフレームバッファ基準なので、タイル分割の正規化は
                            // この矩形で行わないと構築側（NDC 等分）とズレる。
                            let cluster_vp = if self.mode == RuntimeMode::Play && !self.paused {
                                game_viewport
                            } else {
                                (0.0, 0.0, win_w_f, win_h_f)
                            };
                            let (cview, cnear, cfar, cfov, casp) = saved_shadow_cam
                                .map(|(v, n, f, fo, a)| (v, n, f, fo, a))
                                .unwrap_or((Mat4x4::identity(), 0.0, 0.0, 0.0, 0.0));
                            let cluster_on = draw_ctx.clusters.update(
                                &draw_ctx.queue,
                                saved_shadow_cam.is_some(),
                                // CameraUniform.view と同じ流儀（転置＝列優先アップロード）。
                                cview.transpose().data,
                                cnear, cfar, cfov, casp,
                                cluster_vp,
                                frame_dir_count,
                                frame_lights.len() as u32,
                            );
                            if cluster_on {
                                let mut cpass = frame.encoder_mut().begin_compute_pass(
                                    &wgpu::ComputePassDescriptor {
                                        label:            Some("Cluster Build Pass"),
                                        timestamp_writes: None,
                                    },
                                );
                                draw_ctx.clusters.dispatch(
                                    &mut cpass, &draw_ctx.pipelines.cluster_build.pipeline,
                                );
                            }
                        }

                        let clear_color = if self.mode == RuntimeMode::Play && !self.paused {
                            let [r, g, b, a] = game_clear_color;
                            wgpu::Color { r: r as f64, g: g as f64, b: b as f64, a: a as f64 }
                        } else if self.active_world_line != 0 || edit_view_2d {
                            wgpu::Color { r: 0.05, g: 0.08, b: 0.18, a: 1.0 }
                        } else {
                            wgpu::Color { r: 0.1,  g: 0.1,  b: 0.1,  a: 1.0 }
                        };
                        // RT 影オン時のメインパス用の選択（Phase R8）。
                        // - rt_draw_ref: RT 影リソースの共有借用（bind_group をパス全体で参照するため保持）。
                        // - rt_pipes:    RT バリアントパイプライン（Some のとき draw_model_indirect が使う）。
                        // - scene_lights_bg: メッシュ描画の group 4。RT オン時は TLAS を含む複合 BG。
                        // ワイヤーフレーム表示（エディタのシーンビュー・対応 GPU のみ）。
                        // ワイヤ用パイプラインは非 RT レイアウトのため、ワイヤ時は RT を使わず
                        // 非 RT のメインカメラ用 lights BG と組み合わせる。非対応 GPU では false と
                        // なり、通常の塗り経路（フラグメントは view_mode によりアンリット）へ落ちる。
                        // scene_wireframe は G-Buffer RT の確保要否判定のため既に上（3040 行目付近）で
                        // 算出済み（deferred_active と同時に前倒し計算）。ここでは再計算しない。
                        // ワイヤ時は RT を無効化する（塗り／RT とワイヤの経路を混在させない）。
                        let use_rt = rt_on && !scene_wireframe;
                        let rt_draw_ref = if use_rt { draw_ctx.rt_shadow.as_ref().map(|c| c.borrow()) } else { None };
                        let rt_pipes = if use_rt { draw_ctx.pipelines.rt.as_ref() } else { None };
                        let scene_lights_bg: &wgpu::BindGroup = rt_draw_ref
                            .as_ref()
                            .map(|r| &r.bind_group)
                            .unwrap_or(draw_ctx.light_buffer.bind_group(LightingPass::MainCamera));

                        // ── 水面描画の準備（Phase W1。Phase W5.3 でここへ前倒し）─────────────
                        //
                        // 【なぜ水面パスの直前ではなくここなのか】
                        //  コースティクス生成パス（W5.3）は deferred ライティングより**前**に走るが、
                        //  そのとき水パラメータのストレージバッファが既にアップロード済みでなければならない。
                        //  `prepare` がやるのは「パラメータの収集・アップロード・BindGroup 生成」だけで、
                        //  実際の描画（`draw` / `draw_id`）と屈折背景のグラブ（`record_grab`）は
                        //  従来位置のまま動かしていない。
                        //
                        // 【深度ビューを先に渡してよい理由】
                        //  `prepare` へ渡す `depth_only_view_r()` は**テクスチャビューのハンドル**であって
                        //  中身のコピーではない。BindGroup が参照するのは同じ深度テクスチャなので、
                        //  「いつ BindGroup を作ったか」は「そこに何が書かれているか」と無関係である。
                        //  水面パスが実行される時点では G-Buffer／フォワードの深度書き込みが完了しており、
                        //  水面の手動深度テスト・厚み復元は前倒し前とまったく同じ結果になる。
                        //
                        // 【ゲートは deferred の外】
                        //  水面はフォワード（deferred 無効）でも描くので、この準備は
                        //  `if deferred_active` ブロックの**外**に置くこと。
                        let mut water_prepared = false;
                        if water_gate {
                            // 遅延構築（App::new は device 確立前に走るためここで初期化する）。
                            // パイプラインキャッシュは遅延構築のため None（一度きりの構築コストのみ）。
                            if self.water_renderer.is_none() {
                                self.water_renderer = Some(
                                    crate::engine::core::renderer::WaterRenderer::new(
                                        &draw_ctx.device,
                                        crate::engine::core::renderer::HDR_FORMAT,
                                        None,
                                    ),
                                );
                            }
                            let depth_sample_view = frame.depth_only_view_r();
                            // ホットリロードの可否は `water` の可変借用より **前** に読む
                            // （`self` の可変借用中に `self.mode` を読めないため）。
                            let water_shader_hot_reload =
                                self.mode == RuntimeMode::Edit || self.play_shader_hot_reload;
                            let water = self.water_renderer.as_mut()
                                .expect("water_renderer は直前に構築済み");
                            water_prepared = water.prepare(
                                &draw_ctx.device, &draw_ctx.queue,
                                &water_volumes, saved_camera_pos,
                                surf_w, surf_h, depth_sample_view,
                                // ピッキング ID のベース（キャンバスアクターと同一 ID 空間）。
                                // 水面には専用の ID 帯域を設けず、
                                // 「raw = canvas_id_offset + アクタ DFS + 1」でデコード側の
                                // キャンバス選択分岐（DFS からアクタを引く経路）に相乗りする。
                                canvas_id_offset,
                                // 波紋・航跡の場（Phase I2）。上の I1 節で更新済みのものを読む。
                                self.interaction_field.as_ref(),
                                // 岸波のショアフィールド（Phase W1.5）。上の W1.5 節で更新済み。
                                &self.water_shore_fields,
                                // 水面反射 RT（Phase W5.2）。反射パスを走らせないフレームは
                                // 黒 1x1 のダミーを挿す（**A=0＝反射寄与なし**が中立値なので、
                                // 水面シェーダ側に「反射があるか」の分岐は要らない）。
                                //
                                // 粗さブラーを掛けるフレームは、水面パスが読むのは
                                // **ブラー結果（t1）**の方である（元の反射 RT ではない）。
                                // ブラーの記録は反射パスの直後・水面パスの前に入るので、
                                // ここでビューを焼き込んでおいて順序は満たされる。
                                if water_refl_blur_active {
                                    self.water_reflection_blur_targets.t1_view()
                                } else {
                                    water_reflection_view
                                        .unwrap_or(&draw_ctx.pipelines.water_reflection.dummy_view)
                                },
                                // 水面シェーディングアセット（Phase W8）のビルド結果キャッシュ。
                                // `surface_shader` を設定した水域がある場合だけ触られる。
                                &draw_ctx.water_shading_asset_cache,
                                // ホットリロードの可否は L3-a のシェーディングアセットと
                                // **同じ規約**を共有する（Edit は常に ON、Play はエディタ設定
                                // `play_shader_hot_reload` に従う）。片方だけ挙動が違うと
                                // 「WGSL を保存したのに反映される／されない」が分かれて混乱する。
                                water_shader_hot_reload,
                            );

                            // ── 水面シェーディングアセットのエラー／警告通知 ─────────
                            // レンダラーからは IPC を送れないため、キャッシュがキューに積んだ
                            // 人間可読メッセージをここで drain してエディタへ流す
                            // （L3-a の LOAD_ERROR: と同じ流儀。同一メッセージの連投は抑止済み）。
                            for msg in draw_ctx.water_shading_asset_cache.drain_errors() {
                                eprintln!("[SEED water_shading_asset] {msg}");
                                if let Some(ipc) = &self.ipc {
                                    ipc.send(&format!("LOAD_ERROR:{msg}"));
                                }
                            }
                            // ID パス（後段）で水面クアッドを描いてよいかを伝える。
                            // **この前倒しは ID パス（draw_id）より必ず前**である
                            //（後になるとピッキングが 1 フレーム遅れる／壊れる）。
                            water_pick_ready = water_prepared;
                        }
                        // このフレームで実際にコースティクスを焼くか（Phase W5.3）。
                        // 描画インスタンスが 1 つも出なかった（川の制御点不足など）フレームは焼かない。
                        let caustics_active = caustics_possible && water_prepared;

                        // ── デファード: G-Buffer 書き込み → ライティング復元（Phase D3 Deferred Phase B）
                        // メインパス（フォワード）を開く前に、不透明ジオメトリを G-Buffer へ焼き、
                        // フルスクリーン・ライティングパスで HDR シーンへ復元する。以降のメインパスは
                        // Load で再開し、半透明・スカイボックス・ギズモ等のフォワード要素だけを重ねる。
                        if deferred_active {
                            // g0v..g3v は deferred_active のときのみ Some（RT ensure 済みのため必ず値がある）。
                            let (g0, g1, g2, g3, gv) = (
                                g0v.expect("gbuffer0 view must exist when deferred_active"),
                                g1v.expect("gbuffer1 view must exist when deferred_active"),
                                g2v.expect("gbuffer2 view must exist when deferred_active"),
                                g3v.expect("gbuffer3 view must exist when deferred_active"),
                                gvel.expect("gbuffer velocity view must exist when deferred_active"),
                            );

                            // A. G-Buffer パス: 不透明ジオメトリのみを 4 枚の MRT + 深度へ焼く。
                            //    2D シーンビューは deferred_active=false になるため edit_view_2d 分岐は不要。
                            {
                                let mut gpass = frame.begin_gbuffer_pass_to(g0, g1, g2, g3, gv);
                                // Play時はメインパスと同じ viewport/scissor をG-Bufferにも適用する
                                // （レターボックス帯の外にジオメトリを焼かないため）。
                                if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                    let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                    gpass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                    gpass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                }
                                for (path, sd) in &self.shared_model_batches {
                                    // 視錐台外・遠方の地形チャンクは描画スキップ（Terrain 描画最適化）。
                                    if terrain_culled.contains(path.as_str()) { continue; }
                                    if let Some(&gpu) = gpu_model_by_path.get(path.as_str()) {
                                        crate::engine::core::renderer::gbuffer::draw_gbuffer_indirect(
                                            &mut gpass, gpu, &sd.batch, &camera_buf.bind_group,
                                            &draw_ctx.pipelines.gbuffer, meshlet_active,
                                            // 地形レイヤリソース（group3）。パレット別のバインドグループを内部に持つ。
                                            // 地形が未初期化なら None＝通常マテリアル描画へフォールバック。
                                            self.terrain.layer_resources.as_ref(),
                                        );
                                    }
                                }

                                // ── 草（地形散布プロップ）を G-Buffer へ焼く（Terrain T3）──
                                //   バッファはこのフレームの冒頭で再構築済み（rebuild_grass_gpu）。
                                //   ここでは時刻の 4 バイト部分更新と描画コマンド記録だけを行う。
                                //   時刻は `ctx.anim_time`（Play 中のみ進むゲーム内経過秒）を使う。
                                //   Edit モードで風が止まるのは他のアニメーションと同じ挙動であり、
                                //   独自の時計を持たない（時間源を二重化しないための判断）。
                                //   チャンク単位フラスタム＋距離カリング付きで描く
                                //   （Terrain T3 描画最適化）。可視チャンクの連続区間だけを
                                //   draw する。カリング用の平面・カメラ位置はメインカメラのもの。
                                let grass_cull_dist =
                                    *super::terrain_scatter_ops::GRASS_CULL_DISTANCE;
                                // 計測用 NOCULL 指定時は距離∞ 相当にして全チャンクを描く
                                //   （span 判定は視錐台も見るため、draw_grass=全描画へ切り替える）。
                                let grass_nocull = *super::terrain_scatter_ops::SCATTER_CULL_DISABLED;
                                let grass_cull_sq = grass_cull_dist * grass_cull_dist;
                                // 遠景密度減衰の帯境界（二乗距離）。近=全密度 / 中=1/2 / 遠=1/4。
                                let grass_decay_near = *super::terrain_scatter_ops::GRASS_DECAY_NEAR;
                                let grass_decay_mid = *super::terrain_scatter_ops::GRASS_DECAY_MID;
                                let grass_decay_near_sq = grass_decay_near * grass_decay_near;
                                let grass_decay_mid_sq = grass_decay_mid * grass_decay_mid;
                                // 遠景密度減衰の削減量を決定的に計測する（SEED_PERF_TERRAIN 有効時）。
                                let mut grass_drawn: u32 = 0;
                                let mut grass_total: u32 = 0;
                                // インタラクションフィールドの group2（Phase I1）。
                                // 草バッファが 1 つでもあれば必ず構築済み（フレーム冒頭の遅延構築）。
                                let interaction_bg = self.interaction_field.as_ref()
                                    .map(|f| f.sample_bind_group());
                                for buf in self.terrain.grass_buffers.values() {
                                    buf.update_time(&draw_ctx.queue, shader_time);
                                    grass_total += buf.count();
                                    // 場が未構築なら草も描かない（構造上ここへは来ない安全弁）。
                                    let Some(field_bg) = interaction_bg else { continue };
                                    if grass_nocull {
                                        crate::engine::core::renderer::grass_gbuffer::draw_grass(
                                            &mut gpass,
                                            &draw_ctx.pipelines.gbuffer.grass,
                                            buf,
                                            &camera_buf.bind_group,
                                            field_bg,
                                        );
                                        grass_drawn += buf.count();
                                    } else {
                                        grass_drawn += crate::engine::core::renderer::grass_gbuffer::draw_grass_culled(
                                            &mut gpass,
                                            &draw_ctx.pipelines.gbuffer.grass,
                                            buf,
                                            &camera_buf.bind_group,
                                            field_bg,
                                            &saved_frustum_planes,
                                            saved_camera_pos,
                                            grass_cull_sq,
                                            grass_decay_near_sq,
                                            grass_decay_mid_sq,
                                        );
                                    }
                                }
                                // 計測ログ（60 フレームに 1 回・SEED_PERF_TERRAIN 有効時のみ）。
                                //   カリング＋密度減衰後に実際に描いた草本数 / 全本数。
                                if *super::terrain_scatter_ops::PERF_TERRAIN_LOG_ENABLED && grass_total > 0 {
                                    use std::sync::atomic::{AtomicU64, Ordering};
                                    static GN: AtomicU64 = AtomicU64::new(0);
                                    if GN.fetch_add(1, Ordering::Relaxed) % 60 == 0 {
                                        eprintln!(
                                            "[PERF terrain] grass draw: drawn={grass_drawn}/{grass_total} \
                                             (nocull={grass_nocull})"
                                        );
                                    }
                                }

                                // ── 散布モデル（kind=Model プロップ）を G-Buffer へ焼く（Terrain T3 第2段）──
                                //   草と違い実メッシュなので通常の deferred メッシュパイプラインで
                                //   インスタンス描画する（deferred ライティング・SSAO・影・DDGI 受光が
                                //   通常メッシュと同じく効く）。バッファはフレーム冒頭で再構築済み
                                //   （rebuild_scatter_models_gpu）。meshlet カリングはインスタンス数が
                                //   上限内のバッチで有効（meshlet_active を渡す）:
                                //   上のメッシュレットカリング compute で散布モデルの可視 LOD0 メッシュレット
                                //   だけを間接コマンドへ詰めてある。draw_gbuffer_indirect はプリミティブ単位で
                                //   間接コマンドの有無を見て、スロット未確保（大量散布でフォールバック・スキン・
                                //   Blend）の prim は自動で通常 draw_indexed に落ちる。terrain_layers は None
                                //   （地形レイヤブレンドではなく通常マテリアル）。
                                let scatter_meshlet_draw = meshlet_active
                                    && *super::terrain_scatter_ops::SCATTER_MESHLET_CULL_ENABLED;
                                for res in self.terrain.scatter_models.values() {
                                    crate::engine::core::renderer::gbuffer::draw_gbuffer_indirect(
                                        &mut gpass,
                                        &res.gpu_model,
                                        &res.batch,
                                        &camera_buf.bind_group,
                                        &draw_ctx.pipelines.gbuffer,
                                        scatter_meshlet_draw,
                                        None,
                                    );
                                }
                            }

                            // ── Hi-Z オクルージョン: ピラミッド生成＋ディスパッチ＋読み戻し予約 ─────
                            //   G-Buffer 深度が確定した直後（パス外）に実行する。地形チャンクの
                            //   world AABB を Hi-Z へ投影・比較し、可視性を result_buf → staging へ写す。
                            //   結果は **次フレーム** の try_read_results が受け取る（1 フレーム遅延）。
                            //   readback_idle() が false（前回マップ未消費）のフレームは丸ごと見送る
                            //   （マップ中 staging への二重 COPY / 二重 map を防ぐ）。deferred のみ。
                            if *super::terrain_scatter_ops::HIZ_OCCLUSION_ENABLED {
                                let (sw, sh) = frame.surface_size();
                                // レイジー構築（初回のみ）。以降は ensure_size でサイズ追従する。
                                if self.hiz.is_none() {
                                    self.hiz = Some(crate::engine::core::renderer::hiz::HiZSystem::new(
                                        &draw_ctx.device, sw.max(1), sh.max(1), 256,
                                    ));
                                }
                                if let Some(hiz) = self.hiz.as_mut() {
                                    if hiz.readback_idle() {
                                        hiz.ensure_size(&draw_ctx.device, &draw_ctx.queue, sw, sh);
                                        // 地形チャンクの world AABB と key（path）を同順で収集する。
                                        // world_bounds=None（未 update）のチャンクは対象外＝描く（保守側）。
                                        let mut keys: Vec<String> = Vec::new();
                                        let mut aabbs: Vec<crate::engine::core::renderer::uniforms::GpuCullData>
                                            = Vec::new();
                                        for (path, sd) in &self.shared_model_batches {
                                            if !path.starts_with(
                                                crate::engine::components::TERRAIN_SOURCE_SCHEME) { continue; }
                                            if let Some((mn, mx)) = sd.batch.world_bounds() {
                                                keys.push(path.clone());
                                                aabbs.push(crate::engine::core::renderer::uniforms::GpuCullData {
                                                    aabb_min: mn, _pad0: 0.0, aabb_max: mx, _pad1: 0.0,
                                                });
                                            }
                                        }
                                        if !aabbs.is_empty() {
                                            hiz.set_instances(&draw_ctx.device, &draw_ctx.queue, &aabbs);
                                            // 深度ビューは 'r 寿命で取り出し、エンコーダ借用と両立させる。
                                            let depth_view = frame.depth_only_view_r();
                                            let enc = frame.encoder_mut();
                                            hiz.build_pyramid(enc, depth_view, &draw_ctx.device);
                                            hiz.dispatch_occlusion(enc, &camera_buf.buffer, &draw_ctx.device);
                                            hiz.schedule_readback(enc);
                                            if *super::terrain_scatter_ops::PERF_TERRAIN_LOG_ENABLED {
                                                use std::sync::atomic::{AtomicU64, Ordering};
                                                static DN: AtomicU64 = AtomicU64::new(0);
                                                if DN.fetch_add(1, Ordering::Relaxed) % 60 == 0 {
                                                    eprintln!(
                                                        "[PERF hiz] dispatch: instances={} ({}x{})",
                                                        keys.len(), sw, sh
                                                    );
                                                }
                                            }
                                            self.hiz_prev_keys = keys;
                                            self.hiz_need_map = true;
                                        }
                                    }
                                }
                            }

                            // ── AO 生成パス + いもす法ブラー（Phase D4）─────────────────
                            // G-Buffer 完成後・deferred ライティング前に半解像度 ao_raw へ AO を焼き、
                            // いもす法で ao_b へ均す。ライティングは group1 に ao_b をバイリニアで受け取り
                            // occlusion に乗算する（アンビエント/DDGI/バウンスにのみ効く）。
                            if ao_effective != crate::engine::core::renderer::AoMode::Off {
                                let ao_p = &draw_ctx.pipelines.ao;
                                // RT-AO は TLAS が要る。ao==Rt かつ RT パイプライン存在時のみ RT、
                                // それ以外は SSAO（安全側フォールバック）。半径は方式ごとの定数。
                                let use_rt_ao = ao_effective == crate::engine::core::renderer::AoMode::Rt
                                    && ao_p.rt.is_some();
                                let ao_radius = if use_rt_ao {
                                    crate::engine::core::renderer::AO_RTAO_WORLD_RADIUS
                                } else {
                                    crate::engine::core::renderer::AO_SSAO_WORLD_RADIUS
                                };
                                ao_p.write_params(&draw_ctx.queue, self.post_fx.ao_intensity, ao_radius);
                                // group1（G-Buffer）: AO 生成時点では ao_b 未計算のため t_ao スロットは白。
                                let ao_gbuffer_bg = crate::engine::core::renderer::deferred::create_gbuffer_bind_group(
                                    &draw_ctx.device, &draw_ctx.pipelines.deferred.gbuffer_bgl,
                                    g0, g1, g2, g3,
                                    frame.depth_only_view(), &draw_ctx.pipelines.deferred.gbuffer_sampler,
                                    &ao_p.white_view, &ao_p.linear_sampler,
                                    // AO 生成シェーダは group1 の 0..5 のみ宣言＝SSGI/マスクスロットは未参照。ダミーを渡す。
                                    &draw_ctx.pipelines.ssgi.dummy_view, &draw_ctx.pipelines.ssgi.linear_sampler,
                                    &draw_ctx.pipelines.deferred.mask_dummy_view, &draw_ctx.pipelines.deferred.mask_sampler,
                                    // AO / マスク生成シェーダは group1 の 0..5 のみ宣言＝コースティクスも未参照。
                                    &draw_ctx.pipelines.caustics.dummy_view,
                                    // 影の屈折オフセット（binding13）も同様に未参照＝ダミー 1x1（ゼロ）でよい。
                                    &draw_ctx.pipelines.caustics.dummy_offset_view,
                                );
                                let ao_params_bg = ao_p.create_params_bg(&draw_ctx.device);
                                // RT-AO データ（TLAS）。needs_tlas() 経由で構築済み保証。共有借用（RefCell）。
                                let ao_rt_ref = if use_rt_ao {
                                    draw_ctx.rt_shadow.as_ref().map(|c| c.borrow())
                                } else { None };
                                let ao_rt_bg = ao_rt_ref.as_ref().map(|r| ao_p.create_rt_bg(&draw_ctx.device, r.tlas()));
                                // RT データが得られたら RT、そうでなければ SSAO（安全側フォールバック）。
                                let do_rt_ao = ao_rt_bg.is_some();
                                {
                                    let mut apass = frame.begin_ao_pass_to(self.ao_targets.raw_view());
                                    // 半解像度・UV ベースのため viewport は設定しない（背景は下流で discard）。
                                    apass.set_bind_group(0, &camera_buf.bind_group, &[]);
                                    apass.set_bind_group(1, &ao_gbuffer_bg, &[]);
                                    apass.set_bind_group(2, &ao_params_bg, &[]);
                                    if do_rt_ao {
                                        apass.set_pipeline(ao_p.rt.as_ref().unwrap());
                                        apass.set_bind_group(3, ao_rt_bg.as_ref().unwrap(), &[]);
                                    } else {
                                        apass.set_pipeline(&ao_p.ssao);
                                    }
                                    apass.draw(0..3, 0..1);
                                }
                                // いもす法ブラー（ao_raw → ao_a/ao_b, 結果は必ず ao_b）。
                                ao_p.blur(&draw_ctx.device, frame.encoder_mut(), &self.ao_targets);
                            }

                            // ── 水中コースティクス生成パス（Phase W5.3）─────────────────
                            // G-Buffer 深度が確定した後・deferred ライティングの前に、フル解像度の
                            // MRT 2 枚を焼く。ピクセルごとに深度からワールド座標を復元し、いずれかの
                            // クアッド系水域の水面より下なら、水面の高さ場から
                            //   location0: 透過率 rgb ＋ 集光強度 a（直達光の色と明るさ）
                            //   location1: 影のサンプル位置に足すワールド XZ オフセット（m）
                            // を求める。水面フォワードパスでは水底のピクセルを踏めないため、
                            // ここで焼いてライティングの入力にするしかない（caustics.rs の冒頭コメント参照）。
                            // 水パラメータは上で前倒しした water.prepare がアップロード済み。
                            //
                            // **【順序依存】このパスは RT ソフト影マスク生成パスより前に置くこと。**
                            // マスク生成はレイ原点を location1 のオフセットぶんずらすため、
                            // 同じフレームのオフセットが既に焼けている必要がある（後ろへ動かすと
                            // 1 フレーム遅れのオフセットを読むか、常に 0 になる）。
                            if caustics_active {
                                let cp = &draw_ctx.pipelines.caustics;
                                let water = self.water_renderer.as_ref()
                                    .expect("caustics_active なら water_renderer は構築済み");
                                // 探索範囲は配列先頭のクアッド系インスタンスだけ（川リボンは対象外）。
                                cp.write_params(&draw_ctx.queue, water.quad_count());
                                // group1/2 は水面パスと同じリソースを**本パス専用レイアウト**で組み直す
                                //（BindGroup はレイアウト非互換で使い回せない。ID パスと同じ事情）。
                                let caustics_params_bg = cp.create_params_bg(&draw_ctx.device, water);
                                let caustics_field_bg  = cp.create_field_bg(
                                    &draw_ctx.device, water, self.interaction_field.as_ref());
                                let caustics_depth_bg  = cp.create_depth_bg(
                                    &draw_ctx.device, frame.depth_only_view_r());
                                if let Some(params_bg) = caustics_params_bg.as_ref() {
                                    let mut cpass = frame.begin_caustics_pass_to(
                                        self.caustics_targets.view(),
                                        self.caustics_targets.offset_view());
                                    // Play レターボックス時は他パスと同一のゲームビューポートを適用する
                                    //（帯の外を焼かない＋不透明幾何との位置一致。deferred と同一条件）。
                                    if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                        let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                        cpass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                        cpass.set_scissor_rect(
                                            vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                    }
                                    cp.draw(&mut cpass, &camera_buf.bind_group,
                                            params_bg, &caustics_field_bg, &caustics_depth_bg);
                                }
                            }

                            // コースティクス結果ビュー（group1 binding12 へ渡す。Phase W5.3）。
                            // 焼いたフレームは実テクスチャ、それ以外はダミー 1x1（黒）。
                            // deferred 側は textureLoad で読み、範囲外は WGSL 仕様でゼロを返すため
                            // ダミーでも全画面が「増幅なし」になる（分岐を増やさずに無効化できる）。
                            let caustics_result_view: &wgpu::TextureView = if caustics_active {
                                self.caustics_targets.view()
                            } else {
                                &draw_ctx.pipelines.caustics.dummy_view
                            };
                            // 同パスの 2 枚目の MRT（group1 binding13 へ渡す。Phase W5.3）。
                            // こちらは**加算項**なので中立値は 0＝ダミー 1x1（ゼロ）で
                            // 「影をずらさない＝従来と同一の影」になる。
                            // **RT ソフト影マスク生成パスもこのビューを読む**（レイ原点をずらせるのは
                            // 生成パス側だけのため）。ゆえに上のコースティクス生成パスは
                            // シャドウマスク生成パスより**先に**記録してある（順序依存）。
                            let caustics_offset_result_view: &wgpu::TextureView = if caustics_active {
                                self.caustics_targets.offset_view()
                            } else {
                                &draw_ctx.pipelines.caustics.dummy_offset_view
                            };

                            // ── 色付き影バインドレス group3 BG（B3）───────────────────────────────
                            // RT 対応 かつ バインドレス対応 GPU（deferred.colored_shadow_bgl が Some）のとき、
                            // shadow_mask 生成パスと deferred ライティングパスの group3 に bind する
                            // 色付き影資源（instance_table/UV/index/テクスチャ配列/サンプラー）を 1 回だけ組む。
                            // 非対応 GPU／縮退時は None＝従来の空 gap（empty_bg3）を使う。
                            let colored_shadow_bg: Option<wgpu::BindGroup> = if use_rt {
                                match (&draw_ctx.bindless, draw_ctx.pipelines.deferred.colored_shadow_bgl.as_ref()) {
                                    (Some(bl_cell), Some(cs_bgl)) => {
                                        Some(bl_cell.borrow().create_colored_shadow_bind_group(&draw_ctx.device, cs_bgl))
                                    }
                                    _ => None,
                                }
                            } else {
                                None
                            };

                            // ── RT ソフト影マスク生成パス + バイラテラルデノイズ（Phase RT-Shadow-Denoise）─────
                            // G-Buffer＋TLAS から半解像度 4 レイヤ mask_raw へ選定ソフト影の透過率（.rgb）と
                            // half-res ビュー空間深度（.a に同梱）を焼く。各レイヤを深度ガイド付き separable
                            // バイラテラルで mask_b へ均し（深度エッジを跨がずカーテンのフチのハローを断つ）、
                            // deferred ライティングが mask_b をサンプルする。scene_lights_bg は shadow_mask_active の
                            // 条件（rt_on）から RT 複合 BG（TLAS/平均アルベド入り）であることが保証される。
                            if shadow_mask_active {
                                let smp = draw_ctx.pipelines.shadow_mask.as_ref().unwrap();
                                smp.write_params(&draw_ctx.queue, &shadow_mask_selection);
                                // group1（G-Buffer）: マスク生成は 0..5 のみ参照。AO/SSGI/マスクスロットはダミーを渡す。
                                let mask_gbuffer_bg = crate::engine::core::renderer::deferred::create_gbuffer_bind_group(
                                    &draw_ctx.device, &draw_ctx.pipelines.deferred.gbuffer_bgl,
                                    g0, g1, g2, g3,
                                    frame.depth_only_view(), &draw_ctx.pipelines.deferred.gbuffer_sampler,
                                    &draw_ctx.pipelines.ao.white_view, &draw_ctx.pipelines.ao.linear_sampler,
                                    &draw_ctx.pipelines.ssgi.dummy_view, &draw_ctx.pipelines.ssgi.linear_sampler,
                                    &draw_ctx.pipelines.deferred.mask_dummy_view, &draw_ctx.pipelines.deferred.mask_sampler,
                                    // AO / マスク生成シェーダは group1 の 0..5 のみ宣言＝コースティクスも未参照。
                                    // マスク生成シェーダは group1 の 0..5 と **binding13（影の屈折オフセット）** を宣言する。
                                    // コースティクス本体（binding12）は読まないのでダミーでよい。
                                    &draw_ctx.pipelines.caustics.dummy_view,
                                    // binding13 は本物を渡す。レイ原点を屈折オフセットぶんずらせるのは生成側だけで、
                                    // これが「水中に落ちる影が水面のうねりで揺らぐ」RT マスク経路の実体である。
                                    caustics_offset_result_view,
                                );
                                let mask_params_bg = smp.create_params_bg(&draw_ctx.device);
                                {
                                    let mut mpass = frame.begin_shadow_mask_pass_to(
                                        self.shadow_mask_targets.raw_layer_view(0),
                                        self.shadow_mask_targets.raw_layer_view(1),
                                        self.shadow_mask_targets.raw_layer_view(2),
                                        self.shadow_mask_targets.raw_layer_view(3),
                                    );
                                    // 半解像度・UV ベースのため viewport は設定しない（背景はシェーダが 1 を返す）。
                                    // バインドレス対応時（mask_bindless かつ colored_shadow_bg 確定）は色付き影
                                    // バリアントを使い group3 に色付き影資源を bind、そうでなければ従来の平均色＋空 gap。
                                    let (mask_pipe, mask_g3): (&wgpu::RenderPipeline, &wgpu::BindGroup) =
                                        match (smp.mask_bindless.as_ref(), colored_shadow_bg.as_ref()) {
                                            (Some(mb), Some(bg)) => (mb, bg),
                                            _ => (&smp.mask, &draw_ctx.pipelines.deferred.empty_bg3),
                                        };
                                    mpass.set_pipeline(mask_pipe);
                                    mpass.set_bind_group(0, &camera_buf.bind_group, &[]);
                                    mpass.set_bind_group(1, &mask_gbuffer_bg, &[]);
                                    mpass.set_bind_group(2, &mask_params_bg, &[]);
                                    mpass.set_bind_group(3, mask_g3, &[]);
                                    mpass.set_bind_group(4, scene_lights_bg, &[]);
                                    mpass.draw(0..3, 0..1);
                                }
                                // バイラテラルブラー（各レイヤ mask_raw → mask_a → mask_b, 深度エッジ保持。結果は mask_b）。
                                smp.blur(&draw_ctx.device, frame.encoder_mut(), &self.shadow_mask_targets);
                            }

                            // AO 結果ビュー（ライティングの group1 binding6 へ渡す）。AO=Off 時は白 1x1（ao=1.0）。
                            let ao_sampler = &draw_ctx.pipelines.ao.linear_sampler;
                            let ao_result_view: &wgpu::TextureView = if ao_effective != crate::engine::core::renderer::AoMode::Off {
                                self.ao_targets.b_view()
                            } else {
                                &draw_ctx.pipelines.ao.white_view
                            };

                            // SSGI 入力ビュー（前フレームの ssgi_b）。読み取り可なら実テクスチャ、
                            // 未収束（初回/リサイズ/有効化直後）なら黒ダミー（この 1 フレームは GiParams が
                            // enabled=0＝フラットに倒れているため deferred は screen_gi を無視する）。
                            let ssgi_result_view: &wgpu::TextureView = if ssgi_readable {
                                self.ssgi_targets.b_view()
                            } else {
                                &draw_ctx.pipelines.ssgi.dummy_view
                            };
                            let ssgi_sampler = &draw_ctx.pipelines.ssgi.linear_sampler;
                            // シャドウマスク結果ビュー（group1 binding10 へ渡す, Phase RT-Shadow-Denoise）。
                            // マスク生成したフレームは mask_b の D2Array、非対象フレームはダミー白（-1 スロットで不参照）。
                            let mask_result_view: &wgpu::TextureView = if shadow_mask_active {
                                self.shadow_mask_targets.b_array_view()
                            } else {
                                &draw_ctx.pipelines.deferred.mask_dummy_view
                            };
                            let mask_sampler = &draw_ctx.pipelines.deferred.mask_sampler;
                            // B'. G-Buffer デバッグ可視化パス（シーンビュー表示モード「G-Buffer: 〜」）。
                            //
                            //   ライティングの代わりに、G-Buffer の 1 チャンネルをそのまま HDR へ書く。
                            //   deferred_active は維持しているので G-Buffer は通常どおり焼かれており、
                            //   ここで見えている値は本番ライティングが読む値と完全に同一である。
                            //   チャンネルは uniform（GBufferDebugParams.channel）で切り替えるため、
                            //   モードを増やしてもパイプラインは 1 本のまま。
                            //   near/far は現行のデバッグカメラ設定（深度の線形化に使う）。
                            if let Some(debug_channel) = gbuffer_debug_channel {
                                let gd = &draw_ctx.pipelines.gbuffer_debug;
                                gd.write_params(
                                    &draw_ctx.queue,
                                    debug_channel,
                                    self.camera.base.projection.near,
                                    self.camera.base.projection.far,
                                );
                                let gd_bg = gd.create_bind_group(
                                    &draw_ctx.device,
                                    g0, g1, g2, g3,
                                    frame.depth_only_view(),
                                    // 速度 RT（RT4）。deferred_active のとき必ず確保済み。
                                    gvel.expect("gbuffer velocity view must exist when deferred_active"),
                                );
                                // Clear で開くが、フルスクリーン三角形が全画素を上書きするためクリア色は見えない。
                                // ビューポートは設定しない（Edit モード専用機能であり、Play のレターボックス
                                // 帯という概念が無いため。RT 全面が同じ規約で読める方を優先）。
                                let mut dpass = frame.begin_deferred_lighting_pass_to(
                                    hdr_view, wgpu::Color::BLACK,
                                );
                                dpass.set_pipeline(&gd.pipeline);
                                dpass.set_bind_group(0, &gd_bg, &[]);
                                dpass.draw(0..3, 0..1);
                            }
                            // B. G-Buffer BindGroup 生成 + フルスクリーン・ライティングパス。
                            //    G-Buffer デバッグ表示中は上の可視化パスが代わりを務めるためスキップする
                            //    （ライティング結果を描いてから上書きするのは純粋な無駄）。
                            if !gbuffer_debug_active {
                                // ── シェーディングアセット（L3-a）の解決 ────────────────
                                // フォールバック連鎖:
                                //   Play  : メインカメラの shading_asset → Scene.shading_asset → None
                                //   Edit  : Scene.shading_asset → None（デバッグカメラ含むメインビュー）
                                // どこにも指定が無ければ `None` となり、以降は**従来どおり**
                                // draw_ctx.pipelines.deferred.* をそのまま使う（新経路に一切入らない）。
                                let shading_asset_path: Option<&str> = main_camera_shading_asset
                                    .as_deref()
                                    .or_else(|| scene.shading_asset.as_deref());
                                // ホットリロードの可否:
                                //   - Edit モード : 常に有効（従来どおり）。
                                //   - Play モード : エディタ設定 `play_shader_hot_reload`（既定 ON）に従う。
                                // ポーリング対象は直上で解決した `shading_asset_path` そのものなので、
                                // Play ではメインカメラの shading_asset、Edit ではシーン既定と、
                                // 「そのモードで実際に描画に使われているアセット」が監視される
                                // （Edit と Play で参照アセットが違っても正しい方が追従する）。
                                // 保存を検知したフレームだけパイプライン再コンパイルのヒッチが出るが、
                                // 再生を止めずに画作りを詰められる利点を優先する。
                                let allow_hot_reload =
                                    self.mode == RuntimeMode::Edit || self.play_shader_hot_reload;
                                let shading_pipes = shading_asset_path.and_then(|p| {
                                    crate::engine::core::renderer::shading_asset::resolve(
                                        &draw_ctx.device,
                                        &draw_ctx.pipelines.deferred,
                                        &draw_ctx.shading_asset_cache,
                                        p,
                                        draw_ctx.scene_format,
                                        draw_ctx.depth_format,
                                        allow_hot_reload,
                                    )
                                });

                                let gbuffer_bg = crate::engine::core::renderer::deferred::create_gbuffer_bind_group(
                                    &draw_ctx.device, &draw_ctx.pipelines.deferred.gbuffer_bgl,
                                    g0, g1, g2, g3,
                                    frame.depth_only_view(), &draw_ctx.pipelines.deferred.gbuffer_sampler,
                                    ao_result_view, ao_sampler,
                                    // SSGI（1 フレーム遅延）: deferred_lighting.wgsl の group1 binding8/9。
                                    ssgi_result_view, ssgi_sampler,
                                    // シャドウマスク（Phase RT-Shadow-Denoise）: deferred_lighting.wgsl の group1 binding10/11。
                                    mask_result_view, mask_sampler,
                                    // 水中コースティクス（Phase W5.3）: deferred_lighting.wgsl の group1 binding12。
                                    // 生成したフレームは実テクスチャ、それ以外はダミー 1x1（黒）＝増幅なし。
                                    caustics_result_view,
                                    caustics_offset_result_view,
                                );
                                let mut lpass = frame.begin_deferred_lighting_pass_to(hdr_view, clear_color);
                                if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                    let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                    lpass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                    lpass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                }
                                // RT 対応時は rt バリアントを使う（use_rt と同条件）。scene_lights_bg は
                                // 既に RT複合／MainCamera を選択済み。
                                // 前提: use_rt が真のときは draw_ctx.pipelines.rt が必ず Some
                                // （rt_on は RT 対応 GPU かつ設定オン時のみ真になり、RT 非対応 GPU では
                                // rt_on 自体が偽になるため rt_pipes の Some/None と use_rt は連動する。
                                // pipeline.rs の RtMeshPipelines / deferred.rs の DeferredLightingPipelines.rt
                                // は同じ rt_shadow::rt_shadows_supported() 判定で構築されるため、
                                // use_rt=true のときに deferred.rt が None になることは想定しない）。
                                // ただし万一の不整合（非対応 GPU 等）に備え、deferred.rt が None のときは
                                // 安全側で rt_off パイプライン + 非RT ライト BG へフォールバックする。
                                // lit_pipe が rt_off にフォールバックした場合でも scene_lights_bg が
                                // RT 複合 BG（binding6=TLAS 付き）だと group4 レイアウトが不整合になる。
                                // 前提が保証される通常経路では use_rt=true → deferred.rt=Some のため
                                // 到達しないが、安全側として lights BG もフォールバックを合わせる。
                                let lit_use_rt = use_rt && draw_ctx.pipelines.deferred.rt.is_some();
                                // バインドレス色付き影（B3）: RT かつ colored_shadow_bg 確定かつ rt_bindless 構築済み。
                                // 成立時は group3 に色付き影資源を bind してヒット点テクスチャ実サンプル＋Mask
                                // アルファ抜きの影にする。非成立時は従来の rt / rt_off ＋空 gap。
                                let use_bindless_lit = lit_use_rt
                                    && colored_shadow_bg.is_some()
                                    && draw_ctx.pipelines.deferred.rt_bindless.is_some();
                                // シェーディングアセットが解決できていれば、上と同じ判定
                                // （use_bindless_lit / lit_use_rt）に対応するバリアントを選ぶ。
                                // そのバリアントが無い（RT 非対応・当該バリアントのビルド失敗）
                                // 場合は None になり、直後の match で従来の組み込みへ落ちる。
                                let asset_pipe: Option<&wgpu::RenderPipeline> =
                                    shading_pipes.as_ref().and_then(|ap| {
                                        if use_bindless_lit      { ap.rt_bindless.as_ref() }
                                        else if lit_use_rt       { ap.rt.as_ref() }
                                        else                     { Some(&ap.pipeline) }
                                    });
                                // ── 診断ログ（常時 ON・変化時のみ）─────────────────
                                // 「ロードはできたが、そのバリアントが無くて組み込みへ落ちた」を
                                // 見分けるための行。ロード側の [SHADING] loaded と対で読むこと。
                                if let Some(p) = shading_asset_path {
                                    let variant = if use_bindless_lit { "rt_bindless" }
                                                  else if lit_use_rt  { "rt" }
                                                  else                { "rt_off" };
                                    let mode = if self.mode == RuntimeMode::Edit { "Edit" } else { "Play" };
                                    let line = if asset_pipe.is_some() {
                                        format!("[SHADING] applied mode={mode} path={p} variant={variant}")
                                    } else {
                                        format!(
                                            "[SHADING] fallback mode={mode} path={p} variant={variant} \
                                             （このバリアントのアセット版が無いため組み込み標準で描画）"
                                        )
                                    };
                                    draw_ctx.shading_asset_cache.report(
                                        crate::engine::core::renderer::shading_asset::REPORT_CHANNEL_APPLY,
                                        line,
                                    );
                                }
                                let lit_pipe = match asset_pipe {
                                    Some(p) => p,
                                    // ── 従来経路（アセット未指定・解決失敗時はここだけを通る）──
                                    None if use_bindless_lit => {
                                        draw_ctx.pipelines.deferred.rt_bindless.as_ref().unwrap()
                                    }
                                    None if lit_use_rt => {
                                        draw_ctx.pipelines.deferred.rt.as_ref().unwrap()
                                    }
                                    None => &draw_ctx.pipelines.deferred.pipeline,
                                };
                                let lit_lights_bg: &wgpu::BindGroup = if lit_use_rt {
                                    scene_lights_bg
                                } else {
                                    draw_ctx.light_buffer.bind_group(LightingPass::MainCamera)
                                };
                                // group3: バインドレス時は色付き影資源、通常時は空 gap。
                                let lit_g3: &wgpu::BindGroup = if use_bindless_lit {
                                    colored_shadow_bg.as_ref().unwrap()
                                } else {
                                    &draw_ctx.pipelines.deferred.empty_bg3
                                };
                                lpass.set_pipeline(lit_pipe);
                                lpass.set_bind_group(0, &camera_buf.bind_group, &[]);
                                lpass.set_bind_group(1, &gbuffer_bg, &[]);
                                lpass.set_bind_group(2, &draw_ctx.pipelines.deferred.empty_bg2, &[]);
                                lpass.set_bind_group(3, lit_g3, &[]);
                                lpass.set_bind_group(4, lit_lights_bg, &[]);
                                lpass.draw(0..3, 0..1);
                                // ここで lpass を drop（レンダーパス終了）してから通知を流す。
                                drop(lpass);

                                // ── シェーディングアセットのエラー／警告通知 ─────────────
                                // レンダラーからは IPC を送れないため、キャッシュがキューに積んだ
                                // 人間可読メッセージをここで drain してエディタへ流す
                                // （既存の LOAD_ERROR: プレフィクスの流儀に合わせる）。
                                // 同一メッセージの連投はキャッシュ側で抑止済み。
                                for msg in draw_ctx.shading_asset_cache.drain_errors() {
                                    eprintln!("[SEED shading_asset] {msg}");
                                    if let Some(ipc) = &self.ipc {
                                        ipc.send(&format!("LOAD_ERROR:{msg}"));
                                    }
                                }
                            }

                        }

                        // ── SSGI 生成パス（Phase SSGI, 1 フレーム遅延）────────────────
                        // deferred ライティング完了後・反射より前に、今フレームの scene_hdr（不透明
                        // ライティング済み）＋ G-Buffer から半解像度 ssgi_raw へ 1 バウンス間接光を焼き、
                        // いもす法カラーブラーで ssgi_b へ均す。結果 ssgi_b は **次フレーム** の deferred
                        // ライティング（group1 t_ssgi）が読む（1 フレーム遅延方式）。scene_hdr は入力（読み）・
                        // ssgi_raw は出力（書き）で別テクスチャのため読み書き競合しない。
                        if ssgi_active {
                            let sp = &draw_ctx.pipelines.ssgi;
                            // ミス埋め色＝フラットアンビエント放射照度（ambient_color * ambient_intensity）。
                            // レイが画面外/背景へ抜けたピクセルをこの色で埋め、黒縁を出さない。
                            let amb = [
                                self.ambient_color[0] * self.ambient_intensity,
                                self.ambient_color[1] * self.ambient_intensity,
                                self.ambient_color[2] * self.ambient_intensity,
                            ];
                            sp.write_params(&draw_ctx.queue, amb);
                            let (sg0, sg1, sg2, sg3) = (
                                g0v.expect("gbuffer0 view (ssgi)"),
                                g1v.expect("gbuffer1 view (ssgi)"),
                                g2v.expect("gbuffer2 view (ssgi)"),
                                g3v.expect("gbuffer3 view (ssgi)"),
                            );
                            // group1（G-Buffer）。SSGI 生成は 0..5 のみ参照。AO/SSGI/マスクスロットはダミーを渡す。
                            let ssgi_gbuffer_bg = crate::engine::core::renderer::deferred::create_gbuffer_bind_group(
                                &draw_ctx.device, &draw_ctx.pipelines.deferred.gbuffer_bgl,
                                sg0, sg1, sg2, sg3,
                                frame.depth_only_view(), &draw_ctx.pipelines.deferred.gbuffer_sampler,
                                &draw_ctx.pipelines.ao.white_view, &draw_ctx.pipelines.ao.linear_sampler,
                                &sp.dummy_view, &sp.linear_sampler,
                                &draw_ctx.pipelines.deferred.mask_dummy_view, &draw_ctx.pipelines.deferred.mask_sampler,
                                // SSGI / 反射シェーダは group1 の 0..5 のみ宣言＝コースティクスも未参照。
                                &draw_ctx.pipelines.caustics.dummy_view,
                                // 影の屈折オフセット（binding13）も同様に未参照＝ダミー 1x1（ゼロ）でよい。
                                &draw_ctx.pipelines.caustics.dummy_offset_view,
                            );
                            // group2（SsgiParams + scene_hdr + sampler）。scene_hdr は今フレームの不透明 HDR。
                            let ssgi_input_bg = sp.create_input_bg(&draw_ctx.device, hdr_view);
                            // A. 生成パス（半解像度 ssgi_raw へ Clear0）。半解像度・UV ベースのため viewport 不要。
                            {
                                let mut spass = frame.begin_ssgi_pass_to(self.ssgi_targets.raw_view());
                                spass.set_pipeline(&sp.gen_pipeline);
                                spass.set_bind_group(0, &camera_buf.bind_group, &[]);
                                spass.set_bind_group(1, &ssgi_gbuffer_bg, &[]);
                                spass.set_bind_group(2, &ssgi_input_bg, &[]);
                                spass.draw(0..3, 0..1);
                            }
                            // B. いもす法カラーブラー（ssgi_raw → ssgi_a/ssgi_b, 結果は必ず ssgi_b）。
                            sp.blur(&draw_ctx.device, frame.encoder_mut(), &self.ssgi_targets);
                        }

                        // ── 反射（SSR / RT）パス（Phase D6）──────────────────────────
                        // deferred ライティング完了後・メインフォワード再開前に、G-Buffer＋scene_hdr から
                        // 反射色を RT_REFLECTION へ描き、Additive 合成で scene_hdr へ加算する。
                        // scene_hdr は入力（読み）・RT_REFLECTION は出力（書き）で別テクスチャのため競合しない。
                        if let Some(refl_view) = reflection_view {
                            use crate::engine::core::renderer::ReflectionMode;
                            let refl = &draw_ctx.pipelines.reflection;
                            // intensity を UBO へ反映。
                            refl.write_params(&draw_ctx.queue, self.post_fx.reflection_intensity);
                            // group1（G-Buffer）は deferred の gbuffer_bgl から再作成する
                            // （デファードブロック B のものはスコープ外のため）。
                            let (rg0, rg1, rg2, rg3) = (
                                g0v.expect("gbuffer0 view (reflection)"),
                                g1v.expect("gbuffer1 view (reflection)"),
                                g2v.expect("gbuffer2 view (reflection)"),
                                g3v.expect("gbuffer3 view (reflection)"),
                            );
                            // AO 結果ビュー（反射の group1 にも同じく供給。AO=Off 時は白 1x1）。
                            // ao_effective は上位スコープで算出済み（deferred 有効時のみ非 Off）。
                            let refl_ao_sampler = &draw_ctx.pipelines.ao.linear_sampler;
                            let refl_ao_view: &wgpu::TextureView = if ao_effective != crate::engine::core::renderer::AoMode::Off {
                                self.ao_targets.b_view()
                            } else {
                                &draw_ctx.pipelines.ao.white_view
                            };
                            let refl_gbuffer_bg = crate::engine::core::renderer::deferred::create_gbuffer_bind_group(
                                &draw_ctx.device, &draw_ctx.pipelines.deferred.gbuffer_bgl,
                                rg0, rg1, rg2, rg3,
                                frame.depth_only_view(), &draw_ctx.pipelines.deferred.gbuffer_sampler,
                                refl_ao_view, refl_ao_sampler,
                                // 反射シェーダは group1 の 0..5 のみ宣言＝SSGI/マスクスロットは未参照。ダミーを渡す。
                                &draw_ctx.pipelines.ssgi.dummy_view, &draw_ctx.pipelines.ssgi.linear_sampler,
                                &draw_ctx.pipelines.deferred.mask_dummy_view, &draw_ctx.pipelines.deferred.mask_sampler,
                                // SSGI / 反射シェーダは group1 の 0..5 のみ宣言＝コースティクスも未参照。
                                &draw_ctx.pipelines.caustics.dummy_view,
                                // 影の屈折オフセット（binding13）も同様に未参照＝ダミー 1x1（ゼロ）でよい。
                                &draw_ctx.pipelines.caustics.dummy_offset_view,
                            );
                            // ミス経路（レイが空へ抜けたとき）に映す代表スカイボックス。
                            // 水面反射（W5.2）とまったく同じ入口・同じ WGSL 関数を使うので、
                            // 水面と床・磨かれた面が同じ方向に同じ空の色を映す。
                            // スカイボックスが無いシーンでは None → enabled=0 のダミーが挿さり、
                            // 従来どおり GI プローブ／アンビエントへフォールバックする。
                            //
                            // 【なぜ「画面内の空をグラブから拾う」段が無いのか】
                            // 本パスは skybox の球メッシュを描く**前**に走るため、入力の scene_hdr
                            // には空がまだ描かれていない（水面反射は skybox 描画後にグラブを取るので
                            // その段を持てる）。よって D6 は「天球直接サンプル → GI」の 2 段である。
                            let refl_sky_src = self.skybox_system.reflection_sky_source();
                            refl.update_sky(&draw_ctx.queue, refl_sky_src.as_ref());
                            let input_bg = refl.create_input_bg(
                                &draw_ctx.device, hdr_view, refl_sky_src.as_ref());
                            let gi_bg    = refl.create_gi_bg(&draw_ctx.device, &draw_ctx.gi);

                            // RT 反射は TLAS/平均アルベドが要る。reflection==Rt かつ RT パイプライン存在時のみ
                            // rt_shadow を借用して RT データ BG を作る（TLAS は needs_tlas で構築済み保証）。
                            // 借用は共有（RefCell::borrow）なので既存 rt_draw_ref と共存できる。
                            let use_rt_refl = reflection_effective == ReflectionMode::Rt && refl.rt.is_some();
                            let rt_refl_ref = if use_rt_refl {
                                draw_ctx.rt_shadow.as_ref().map(|c| c.borrow())
                            } else { None };
                            // バインドレス（B2）対応 GPU では、group3 に instance_table・UV・index・
                            // テクスチャ配列・サンプラーを同居させた拡張 BG を作る（refl.rt も拡張
                            // レイアウトで構築済み）。非対応 GPU では従来の 4 binding BG（平均色経路）。
                            let rt_data_bg = rt_refl_ref.as_ref().map(|r| {
                                let lights = draw_ctx.light_buffer.lights_buffer();
                                let meta   = draw_ctx.light_buffer.meta_main_buffer();
                                if let Some(bl_cell) = &draw_ctx.bindless {
                                    let bl = bl_cell.borrow();
                                    refl.create_rt_data_bg_bindless(
                                        &draw_ctx.device, lights, meta,
                                        r.tlas(), r.albedo_buffer(), &bl,
                                    )
                                } else {
                                    refl.create_rt_data_bg(
                                        &draw_ctx.device, lights, meta,
                                        r.tlas(), r.albedo_buffer(),
                                    )
                                }
                            });
                            // RT データが得られたら RT、そうでなければ SSR（安全側フォールバック）。
                            let do_rt = rt_data_bg.is_some();

                            // A. 反射パス（RT_REFLECTION へ Clear0）。
                            {
                                let mut rpass = frame.begin_reflection_pass_to(refl_view);
                                if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                    let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                    rpass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                    rpass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                }
                                rpass.set_bind_group(0, &camera_buf.bind_group, &[]);
                                rpass.set_bind_group(1, &refl_gbuffer_bg, &[]);
                                rpass.set_bind_group(2, &input_bg, &[]);
                                if do_rt {
                                    rpass.set_pipeline(refl.rt.as_ref().unwrap());
                                    rpass.set_bind_group(3, rt_data_bg.as_ref().unwrap(), &[]);
                                    rpass.set_bind_group(4, &gi_bg, &[]);
                                } else {
                                    rpass.set_pipeline(&refl.ssr);
                                    rpass.set_bind_group(3, &gi_bg, &[]);
                                }
                                rpass.draw(0..3, 0..1);
                            }
                            // B. 合成パス（RT_REFLECTION を scene_hdr へ Additive 加算）。
                            {
                                let composite_bg = refl.create_composite_bg(&draw_ctx.device, refl_view);
                                let mut cpass = frame.begin_reflection_composite_pass_to(hdr_view);
                                if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                    let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                    cpass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                    cpass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                }
                                cpass.set_pipeline(&refl.composite);
                                cpass.set_bind_group(0, &composite_bg, &[]);
                                cpass.draw(0..3, 0..1);
                            }
                        }

                        // 屈折の背景ミップチェーン生成（ガラス表現）: 不透明ライティング完成後の scene_hdr を
                        // mip0 へコピーし、以降のミップをダウンサンプル→いもす法ブラーで作る。半透明
                        // フラグメントが roughness からミップを選んでガラス越しに歪めてサンプルできる
                        // （scene_hdr を直接読むとメインパスの書き込みと競合するため別テクスチャへ退避）。
                        // 背景には skybox（メインパスで描画）は含まれない（既知の制限）。deferred 前提。
                        // 併せて LightMeta.translucency_rt に屈折ビット（bit1）を追記する
                        //（bit0＝色付き影は light_buffer.update で設定済み。この追記は queue submit 時に
                        //  透明パスより前へ適用されるため同フレームで有効）。
                        if refract_active {
                            let scene_hdr = self.rt_pool.texture(crate::engine::core::renderer::RT_SCENE_HDR);
                            self.refract_pyramid.record(&draw_ctx.device, frame.encoder_mut(), scene_hdr);
                            // 屈折ビット（bit1）を追記（bit0＝色付き影は既に立っている）。offset 12＝translucency_rt。
                            let mut flag = crate::engine::core::renderer::lighting::TRANSLUCENCY_RT_COLORED_SHADOW
                                     | crate::engine::core::renderer::lighting::TRANSLUCENCY_RT_REFRACTION;
                            // WBOIT（順序独立）方式のときは bit2 を追記する。WBOIT は半透明相互の遮蔽が
                            // 無いため、RT 屈折の界面 tint（奥の半透明レイヤーの透過色）と奥レイヤー自身の
                            // WBOIT 描画とで同じ色が二重計上される（手前ガラス越しの奥ガラスが極端に濃くなる）。
                            // このビットを見て refract_rt.wgsl が界面 tint 累積をスキップし、奥レイヤーの色を
                            // WBOIT 平均で本人 1 回だけに一本化する。距離ソート時は立てない（従来どおり tint 累積）。
                            if tp_wboit {
                                flag |= crate::engine::core::renderer::lighting::TRANSLUCENCY_RT_WBOIT;
                            }
                            draw_ctx.queue.write_buffer(
                                draw_ctx.light_buffer.meta_main_buffer(), 12, bytemuck::bytes_of(&flag),
                            );
                        }

                        // メインパス開始: デファード時は G-Buffer/ライティングパスが書いた HDR・深度・
                        // ステンシルを Load で保持（半透明・スカイボックス・ギズモをその上に重ねる）。
                        // フォワード時は従来どおりクリアして開始する。
                        let mut pass = if deferred_active {
                            frame.begin_scene_pass_load_to(hdr_view)
                        } else {
                            frame.begin_scene_pass_to(hdr_view, clear_color)
                        };

                        // LetterBox / PillarBox 時: ビューポート設定前に帯エリアを帯カラーで塗る。
                        // LoadOp::Clear はサーフェス全体をクリアするため、ゲーム以外のエリアを
                        // BarFillPipeline で上書きすることで正しい帯カラーを適用する。
                        if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok && uses_bar_mode {
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
                        if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                            let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                            pass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                            pass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                        }

                        // ── スカイボックス（天球）：HDR メインパスの最初（不透明より先）に描く（Phase R9）──
                        // CameraLocked は depth 書込 OFF・far 固定で背景として、WorldAnchored は
                        // 通常深度で実体として描く。以降の 3D ワールド／不透明がその上に重なる。
                        // Play のビューポート／シザー適用後に描くことで黒帯へのはみ出しを防ぐ。
                        // 2D シーンビュー（edit_view_2d）では天球を描かない。
                        // G-Buffer デバッグ表示中は天球も描かない。背景ピクセルには G-Buffer が
                        // 無い（＝可視化パスが黒で塗る）ため、そこへ天球を描くと「値の無い領域」と
                        // 「値のある領域」の境界が分からなくなる。
                        if !edit_view_2d && !gbuffer_debug_active && self.skybox_system.has_skyboxes() {
                            self.skybox_system.draw(
                                &mut pass,
                                &draw_ctx.pipelines.skybox,
                                &camera_buf.bind_group,
                            );
                        }

                        // ── 背景ゾーンのキャンバススプライト（Phase C）────────────────
                        // 描画順（奥→手前）: 背景キャンバス | 3D ワールド | 前面キャンバス。
                        // クリア直後・3D ワールドより先に 2D オルソオーバーレイカメラで描画する。
                        // スプライトパイプラインは depth_write=false のため、後続の 3D ワールドが
                        // 深度テストを通過して背景スプライトの上に描画される（= 必ずワールドの背景になる）。
                        // scene_canvas_ss（Play / Edit View3D の SS 合成）時のみ。
                        // 2D シーンビュー（edit_view_2d）はメインパスで bg → fg の順に描画する（後述）。
                        if scene_canvas_ss && !sprite_prepared_2d_bg.is_empty() {
                            if let Some(canvas_cam_buf) = self.canvas_overlay_camera_buf.as_ref() {
                                draw_sprite_batches(
                                    &mut pass,
                                    &draw_ctx.pipelines.sprite,
                                    &canvas_cam_buf.bind_group,
                                    &main_inst_buf,
                                    &sprite_prepared_2d_bg,
                                );
                            }
                        }

                        // 全 MC を統合バッチで描画（N_actors → N_unique_models 回の draw call）
                        // 2D シーンビューでは 3D モデルを描画しない（3D シーン非表示）
                        let _perf_t_draw = std::time::Instant::now();
                        if !edit_view_2d {
                            // デファード時は不透明を G-Buffer 経由で既に描画済み（このメインパスは
                            // Load で再開しており、半透明・スカイボックス・ギズモ等のみを重ねる）。
                            // フォワード時のみ従来どおり draw_model_indirect で不透明を描く。
                            if !deferred_active {
                                for (path, sd) in &self.shared_model_batches {
                                    // 視錐台外・遠方の地形チャンクは描画スキップ（Terrain 描画最適化・
                                    // フォワード経路も G-Buffer 経路と同じ集合でスキップする）。
                                    if terrain_culled.contains(path.as_str()) { continue; }
                                    if let Some(&gpu) = gpu_model_by_path.get(path.as_str()) {
                                        draw_model_indirect(
                                            &mut pass, gpu, &sd.batch,
                                            &camera_buf.bind_group, scene_lights_bg,
                                            &draw_ctx.pipelines, rt_pipes, meshlet_active,
                                            scene_wireframe,
                                            // メインパスはマテリアル差し替え無し（従来どおり）。
                                            None,
                                        );
                                    }
                                }
                            }
                            // ── 水面パス（Phase W7。不透明＋スカイボックスの後・半透明より前）──
                            // 【なぜこの位置か（W1 の「WBOIT 合成後」から移動）】
                            //  ・水面は blend=Replace（背景をシェーダ内で合成し alpha=1 を出す）で描くため、
                            //    半透明の後に置くと **水面より手前の半透明オブジェクトまで上書き**していた
                            //    （W1 の既知の制限）。順序を入れ替えるだけでこれは構造的に消える。
                            //  ・水面を「半透明から見れば不透明な背景」として扱い、不透明・スカイボックスの
                            //    直後に描く。以降の距離ソート半透明／WBOIT は通常のアルファブレンドで
                            //    水面の上に重なり、前後関係が正しくなる。
                            //  ・屈折の背景グラブも直前で取るため「不透明＋スカイボックス」だけを含む。
                            //    半透明は屈折像・水中の見た目に映らなくなるが、半透明が丸ごと消える
                            //    不具合より軽い副作用として受け入れる（docs の既知の制限に記載）。
                            //  ・エディタオーバーレイ（ギズモ／軸／ワイヤ）より前なのは W1 と同じ。
                            //
                            // 【メインパスの分割】水面パスは深度アタッチメントを持てない（下記）ため
                            //  独立したレンダーパスであり、ここでメインパスを一旦閉じ、水面を描いてから
                            //  `begin_scene_pass_load_to`（カラー／深度／ステンシルすべて Load）で
                            //  再開する。逐次グラブ（refract_sequential）が使うのと同じ再開手順である。
                            //
                            // 【深度】水面パスは深度アタッチメントを持たない（begin_water_pass_to）。
                            //  共有深度は他パスで書き込み可能としてアタッチされており、同一パスで
                            //  アタッチメント兼サンプルにするとエイリアシングになるため、DepthOnly ビューを
                            //  サンプルテクスチャとして渡し、シェーダ内で手動深度テスト（遮蔽 → discard）と
                            //  水の厚み復元を行う。水面は元々深度を書かないので通常の Z テストと等価。
                            //  ※ 水面は深度を書かないので、**水面より奥にある半透明**は後続の半透明パスで
                            //    深度テストを通り、水面の上に描かれてしまう（W2 で水中描画を入れる際に
                            //    水面深度の書き込みと併せて解消する。docs の既知の制限を参照）。
                            //
                            // 【カメラプレビューには出さない】カメラプレビュー（別 RT への簡易描画）は
                            //  屈折グラブ・深度サンプルの専用リソースを持たず、W1 のスコープ外とする。
                            //
                            // 【ゲート】2D 編集ビュー・ワイヤーフレーム表示・G-Buffer デバッグ表示中は
                            //  水を描かない。水ボリュームが 0 個のフレームも同様で、この場合はメインパスの
                            //  分割（閉じ／再開）自体を行わない＝完全にコスト 0 である。
                            //  **ゲート `water_gate` と準備 `water.prepare` は Phase W5.3 で
                            //  このフレームの前方（deferred ブロックの直前）へ前倒し済み**である
                            //  （コースティクス生成パスがパラメータのアップロード済みを要求するため）。
                            //  ここに残っているのは「グラブ＋水面パスの記録」だけで、順序は W7 のまま。
                            if water_gate {
                                // 水面パスは別レンダーパスなので、ここでメインパスを一旦閉じる。
                                drop(pass);
                                let perf_t_water = std::time::Instant::now();
                                let water_ready = water_prepared;
                                if water_ready {
                                    // ① 屈折背景のグラブ（シーン HDR → 専用 1 ミップテクスチャ）。
                                    //    レンダーパス外・水面パス直前に 1 回だけコピーする。
                                    let scene_hdr = self.rt_pool
                                        .texture(crate::engine::core::renderer::RT_SCENE_HDR);
                                    let water = self.water_renderer.as_ref()
                                        .expect("water_renderer は直前に構築済み");
                                    water.record_grab(frame.encoder_mut(), scene_hdr);

                                    // ①' 水面反射パス（Phase W5.2）。
                                    //
                                    // 【なぜ「グラブの後・水面パスの前」なのか（順序制約）】
                                    //  ・反射には「不透明＋スカイボックスが描き終わったシーンカラー」が要る。
                                    //    グラブはまさにその **コピー** なので、直後に置けば空が映る反射になる
                                    //    （D6 の反射パスと同じ位置＝メインパス前に置くと、スカイボックスは
                                    //     まだ描かれておらず「空が映らない水」になってしまう）。
                                    //  ・グラブはコピーなので、scene_hdr がアタッチされていても
                                    //    読み書き競合が起きない（追加のコピーも発生しない）。
                                    //  ・水面自身はまだ描かれていない＝反射が水面を映す自己参照が起きない。
                                    //  W5.3（コースティクス）の「深度確定後・ライティング前」と同様に、
                                    //  この前後関係は入れ替えられない類のものである。
                                    if let Some(wr_view) = water_reflection_view {
                                        let wrp = &draw_ctx.pipelines.water_reflection;
                                        // RT 変種を使えるか: RT パイプラインが構築済み
                                        // （＝RAY_QUERY 対応 GPU）かつ TLAS を借りられること。
                                        // D6 の全体反射モードには連動させず GPU 対応だけで自動選択する
                                        //（上の water_reflection_possible のコメント参照）。
                                        // どれか欠ければ SSR 変種へ落ちる（＝非対応 GPU でも反射は出る）。
                                        // バインドレス変種の RT パイプラインは group3 に
                                        // テクスチャ配列一式が要る。BindlessResources が
                                        // 無いフレームでは BindGroup を組めないので、
                                        // 安全側で SSR 変種へ落とす（反射は出続ける）。
                                        let want_rt = wrp.has_rt()
                                            && (!wrp.has_bindless() || draw_ctx.bindless.is_some());
                                        let rt_ref = if want_rt {
                                            draw_ctx.rt_shadow.as_ref().map(|c| c.borrow())
                                        } else { None };
                                        let use_rt = rt_ref.is_some();
                                        // 反射のミス経路（画面外へ抜けたレイ）が映すスカイボックス。
                                        // sync_gpu 済みのこのフレームの代表天球を取り、uniform を
                                        // 先に書いてから BindGroup を作る（無ければ None＝GI へ落ちる）。
                                        let sky_src = self.skybox_system.reflection_sky_source();
                                        wrp.update_sky(&draw_ctx.queue, sky_src.as_ref());
                                        // group1 は水パラメータが要る（prepare 済みなので必ず Some）。
                                        let params_bg =
                                            wrp.create_params_bg(&draw_ctx.device, water, use_rt);
                                        if let (Some(params_bg), Some(grab_view)) =
                                            (params_bg.as_ref(), water.grab_view())
                                        {
                                            let field_bg = wrp.create_field_bg(
                                                &draw_ctx.device, water,
                                                self.interaction_field.as_ref(), use_rt);
                                            // group3: バインドレス変種では画面外ヒットの
                                            // ベースカラーテクスチャ一式（instance_table・UV・
                                            // index・テクスチャ配列・サンプラー）も同居する。
                                            // 借用は BindGroup 生成の間だけ保持する。
                                            let bl_ref = if use_rt && wrp.has_bindless() {
                                                draw_ctx.bindless.as_ref().map(|c| c.borrow())
                                            } else { None };
                                            let scene_bg = wrp.create_scene_bg(
                                                &draw_ctx.device, grab_view,
                                                frame.depth_only_view_r(), sky_src.as_ref(), use_rt,
                                                bl_ref.as_deref());
                                            drop(bl_ref);
                                            let meta = draw_ctx.light_buffer.meta_main_buffer();
                                            let gi_bg = match rt_ref.as_ref() {
                                                Some(r) => wrp.create_rt_gi_bg(
                                                    &draw_ctx.device, &draw_ctx.gi, meta,
                                                    draw_ctx.light_buffer.lights_buffer(),
                                                    r.tlas(), r.albedo_buffer()),
                                                None => wrp.create_gi_bg(
                                                    &draw_ctx.device, &draw_ctx.gi, meta),
                                            };
                                            let mut rpass =
                                                frame.begin_water_reflection_pass_to(wr_view);
                                            // Play レターボックス時は他パスと同一のゲームビューポート
                                            //（水面パスとラスタライズを 1 ピクセルもズラさないため必須）。
                                            if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                                let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                                rpass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                                rpass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                            }
                                            wrp.draw(
                                                &mut rpass, water, &camera_buf.bind_group,
                                                params_bg, &field_bg, &scene_bg, &gi_bg, use_rt);
                                            // ①'' 粗さブラー（Phase W5.2 品質改善）。
                                            //     レンダーパスを閉じてから compute を記録する
                                            //     （同一エンコーダ内でパスは入れ子にできない）。
                                            //     結果は t1 に残り、水面パスはそれを読む
                                            //     （prepare でビューを焼き込み済み）。
                                            //     粗さ 0 のフレームは radius=0 で何も記録されない
                                            //     ＝完全な鏡面のまま（1 画素の差も出ない）。
                                            drop(rpass);
                                            if water_refl_blur_active {
                                                wrp.blur(
                                                    &draw_ctx.device, frame.encoder_mut(),
                                                    wr_view, &self.water_reflection_blur_targets,
                                                    water_refl_blur_radius);
                                            }
                                        }
                                    }

                                    // ② 水面パス（color=hdr を Load・深度アタッチメント無し）。
                                    let mut wpass = frame.begin_water_pass_to(hdr_view);
                                    // Play レターボックス時は他パスと同一のゲームビューポートを適用する
                                    //（帯へのはみ出し防止＋不透明幾何との位置一致。他 10 箇所と同一条件）。
                                    if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                        let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                        wpass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                        wpass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                    }
                                    water.draw(&mut wpass, &camera_buf.bind_group);
                                }
                                perf_water_ms = perf_t_water.elapsed().as_secs_f64() * 1000.0;

                                // メインパスを Load で再開する（以降の距離ソート半透明・
                                // スプライト・オーバーレイはすべて水面の上に重なる）。
                                pass = frame.begin_scene_pass_load_to(hdr_view);
                                if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                    let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                    pass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                    pass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                }
                            }


                            // ── 半透明の距離ソート描画（Phase R5）──────────────
                            // 不透明の直後・オーバーレイより前に、Blend プリミティブを
                            // 背面→前面に並べてアルファブレンドで描く。透明はシャドウマップ
                            // 影のみ受けるため NON-RT ライト BG・NON-RT パイプラインを使う。
                            // デファードでも半透明はフォワードで描く（G-Buffer 深度を Load してテスト、
                            // メインパスが Load で再開しているためこの深度テストは正しく機能する）。
                            if tp_sorted {
                                if refract_sequential_active {
                                    // sequential grab 有効時はメインパス内では描かない。この直後の
                                    // 逐次ループがメインパスを一旦閉じ、屈折アイテムの手前で背景を
                                    // 再グラブしながら 1 アイテムずつ別パスで描く（下の分岐を参照）。
                                } else if let (Some(rt_bg), Some(rt_tp)) = (
                                    // 本物の RT 屈折が使えるフレームは RT パイプライン＋RT superset BG で描く。
                                    // それ以外（非対応 GPU・translucency≠Rt・屈折背景オフ）は SS 版へ完全フォールバック。
                                    transparent_rt_bg_main.as_ref(),
                                    draw_ctx.pipelines.transparent.rt.as_ref(),
                                ) {
                                    crate::engine::core::renderer::transparency::draw_sorted_rt(
                                        &mut pass,
                                        &transparent_models,
                                        &camera_buf.bind_group,
                                        rt_bg,
                                        rt_tp,
                                        saved_camera_pos,
                                    );
                                } else {
                                    crate::engine::core::renderer::transparency::draw_sorted(
                                        &mut pass,
                                        &transparent_models,
                                        &camera_buf.bind_group,
                                        &transparent_bg_main,
                                        &draw_ctx.pipelines.transparent,
                                        saved_camera_pos,
                                    );
                                }
                            }

                            // ── sequential grab（屈折の逐次グラブ）本体 ──────────────────
                            // 半透明ガラスを 1 つ描くたびに scene_hdr → 屈折背景ピラミッドを再グラブし、
                            // 後続のガラスが「先に描かれたガラス」を背景として拾えるようにする。
                            // 実装: メインパスを一旦閉じ、背面→前面ソート済みアイテムを走査。屈折する
                            // アイテムの手前で（直近グラブ以降に半透明が描かれていれば）パスを閉じて
                            // 背景を再グラブしパスを開き直す（パス内では自身の出力由来の背景をサンプル
                            // できないための必須分割）。間に挟まる非屈折半透明はまとめて 1 パスで描く。
                            // 描き終えたらメインパスを Load で再開し、以降のオーバーレイへレイヤリングを
                            // 維持したまま戻る。sorted 時のみ・重い（設定 refract_sequential_grab で明示 ON）。
                            if refract_sequential_active {
                                drop(pass);
                                let seq_plan = crate::engine::core::renderer::transparency::plan_sorted_sequential(
                                    &transparent_models, saved_camera_pos,
                                );
                                if !seq_plan.is_empty() {
                                    // RT/SS のパイプライン・BindGroup 選択は in-pass 版と同一規約。
                                    let use_rt = transparent_rt_bg_main.is_some()
                                        && draw_ctx.pipelines.transparent.rt.is_some();
                                    let seq_lights_bg: &wgpu::BindGroup = if use_rt {
                                        transparent_rt_bg_main.as_ref().unwrap()
                                    } else {
                                        &transparent_bg_main
                                    };
                                    let seq_rt = if use_rt {
                                        draw_ctx.pipelines.transparent.rt.as_ref()
                                    } else {
                                        None
                                    };
                                    // 再グラブのコピー元（半透明の描画先＝屈折背景 mip0 のコピー元）。
                                    let scene_hdr_tex = self.rt_pool.texture(
                                        crate::engine::core::renderer::RT_SCENE_HDR,
                                    );
                                    // 直近グラブ以降に scene_hdr が変化したか。初期は false: メインパス前
                                    // （屈折ビット追記と同時）に不透明のみの背景を既にグラブ済みのため、
                                    // 初回の屈折アイテムはその背景を再利用して余計な再グラブを避ける。
                                    let mut dirty = false;
                                    let mut seq_pass = frame.begin_scene_pass_load_to(hdr_view);
                                    if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                        let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                        seq_pass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                        seq_pass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                    }
                                    for sp in &seq_plan {
                                        if sp.refracts && dirty {
                                            drop(seq_pass);
                                            // mip0 コピー＋ぼかしミップ再生成（フル）。roughness>0 のガラスは
                                            // 下位ミップ（先に描いたガラスを含むぼかし背景）をサンプルするため、
                                            // 逐次でもブラーを毎回更新する（mip0 のみの軽量化は roughness 信号が
                                            // 要るため見送り＝docs のトレードオフ節参照）。
                                            self.refract_pyramid.record(
                                                &draw_ctx.device, frame.encoder_mut(), scene_hdr_tex,
                                            );
                                            seq_pass = frame.begin_scene_pass_load_to(hdr_view);
                                            if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                                let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                                seq_pass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                                seq_pass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                            }
                                            dirty = false;
                                        }
                                        crate::engine::core::renderer::transparency::draw_sequential_one(
                                            &mut seq_pass, sp, &transparent_models,
                                            &camera_buf.bind_group, seq_lights_bg,
                                            &draw_ctx.pipelines.transparent, seq_rt,
                                        );
                                        dirty = true;
                                    }
                                    drop(seq_pass);
                                }
                                // メインパスを Load で再開し、以降のオーバーレイ描画へ戻る（レイヤリング維持）。
                                pass = frame.begin_scene_pass_load_to(hdr_view);
                                if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                    let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                    pass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                    pass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                }
                            }
                        }
                        perf_draw_ms = _perf_t_draw.elapsed().as_secs_f64() * 1000.0;

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
                        if !sprite_3d_outline_list.is_empty() {
                            draw_sprite_outline_batches(
                                &mut pass,
                                &draw_ctx.pipelines.sprite,
                                &draw_ctx.pipelines.sprite_outline,
                                &camera_buf.bind_group,
                                &main_inst_buf,
                                &sprite_3d_outline_list,
                            );
                        }

                        // スプライト画像描画（アウトラインより後に描画し、アウトラインの内側を覆う）
                        //
                        // 3D Canvas スプライト: scene_canvas_ss に関わらず常にメインパスで描画する。
                        // 2D アクターが混在するシーン（scene_canvas_ss=true）でも 3D カメラを使うため。
                        if !sprite_prepared_3d.is_empty() {
                            draw_sprite_batches(
                                &mut pass,
                                &draw_ctx.pipelines.sprite,
                                &camera_buf.bind_group,
                                &main_inst_buf,
                                &sprite_prepared_3d,
                            );
                        }
                        // 2D キャンバススプライト: scene_canvas_ss の場合はオーバーレイパスで描画する
                        // （背景ゾーンはメインパス冒頭で描画済み）。
                        // 2D シーンビュー・アクター編集タブ・ワールドスペース表示では
                        // 背景ゾーン → 前面ゾーンの順に描画してレイヤリングをプレビューする。
                        if !scene_canvas_ss {
                            if !sprite_prepared_2d_bg.is_empty() {
                                draw_sprite_batches(
                                    &mut pass,
                                    &draw_ctx.pipelines.sprite,
                                    &camera_buf.bind_group,
                                    &main_inst_buf,
                                    &sprite_prepared_2d_bg,
                                );
                            }
                            if !sprite_prepared_2d_fg.is_empty() {
                                draw_sprite_batches(
                                    &mut pass,
                                    &draw_ctx.pipelines.sprite,
                                    &camera_buf.bind_group,
                                    &main_inst_buf,
                                    &sprite_prepared_2d_fg,
                                );
                            }
                        }

                        // ── メインパスを閉じる（不透明・スカイボックス・半透明ソート・スプライト
                        //    本体まで描画完了）。以降のエディタオーバーレイ（ギズモ／軸／各種ライン・
                        //    ワイヤー／アイコン／選択アウトライン等）は、WBOIT 合成のフルスクリーン
                        //    no_depth クアッドに上書きされないよう、合成の「後」で別パスに描く。
                        drop(pass);

                        // ── WBOIT 透明描画（Phase R5, WBOIT 方式かつ透明物ありのとき）──────
                        // メインパス drop 後・オーバーレイ前に、accum/reveal へ順序独立蓄積し、
                        // フルスクリーン合成でシーン HDR へ重ねる。合成は no_depth クアッドのため、
                        // オーバーレイより「前」に実行しないとギズモ／ワイヤー等を消してしまう
                        //（本修正の要点：旧構成では合成がオーバーレイの後に走り上書きしていた）。
                        // 無効時は一切実行しない。
                        if tp_wboit {
                            if let (Some(accum_view), Some(reveal_view)) =
                                (wboit_accum_view, wboit_reveal_view)
                            {
                                {
                                    let mut wpass = frame.begin_wboit_pass_to(accum_view, reveal_view);
                                    // ── Play レターボックス時のビューポート適用（半透明ズレ修正）──
                                    // accum/reveal RT は hdr と同じ実サーフェス全面サイズ（surf_w×surf_h）。
                                    // 不透明メインパスは game_viewport（ゲーム領域）へ set_viewport して描くため、
                                    // 同一の射影行列でも幾何がレターボックス領域に収まる。ところが WBOIT の
                                    // 蓄積パスはこれまでビューポート未適用で、透明幾何が accum 全面（ウィンドウ
                                    // 全体基準）に描かれていた。合成（composite_wboit）は accum を 1:1 テクセル
                                    // 対応でフルスクリーン三角形サンプルするため、そのズレがそのまま hdr に載り、
                                    // 「半透明だけ全面基準でズレる」という不具合になっていた。
                                    // ここで不透明メインパスと同一の game_viewport を set_viewport+set_scissor_rect
                                    // することで、透明幾何も accum のゲーム領域に描かれ、opaque と一致する。
                                    // scissor は帯（レターボックス黒帯）への透明フラグメントのはみ出しを防ぐ
                                    //（帯へ漏れると合成 discard を逃れて黒帯上に半透明が滲む）。他の 9 箇所と同一条件。
                                    if self.mode == RuntimeMode::Play && !self.paused && play_viewport_ok {
                                        let (vp_x, vp_y, vp_w, vp_h) = game_viewport;
                                        wpass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                                        wpass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                                    }
                                    // 距離ソートと同じ分岐: RT 屈折が使えるなら RT 版、そうでなければ SS 版。
                                    if let (Some(rt_bg), Some(rt_tp)) = (
                                        transparent_rt_bg_main.as_ref(),
                                        draw_ctx.pipelines.transparent.rt.as_ref(),
                                    ) {
                                        crate::engine::core::renderer::transparency::draw_wboit_rt(
                                            &mut wpass,
                                            &transparent_models,
                                            &camera_buf.bind_group,
                                            rt_bg,
                                            rt_tp,
                                            saved_camera_pos,
                                        );
                                    } else {
                                        crate::engine::core::renderer::transparency::draw_wboit(
                                            &mut wpass,
                                            &transparent_models,
                                            &camera_buf.bind_group,
                                            &transparent_bg_main,
                                            &draw_ctx.pipelines.transparent,
                                            saved_camera_pos,
                                        );
                                    }
                                }
                                // accum/reveal → シーン HDR へアルファブレンド合成（LoadOp::Load）。
                                // 【Play レターボックスでも合成側はビューポート不要（意図的にフルスクリーン）】
                                // composite はフルスクリーン三角形で accum/reveal を uv=0..1（=テクスチャ全面）
                                // へ 1:1 テクセル対応サンプルするため、蓄積側に game_viewport を適用済みなら
                                // 透明内容は accum のゲーム領域にあり、そのまま hdr の同じゲーム領域へ載る。
                                // 帯（レターボックス）領域は accum=Clear(0)/reveal=Clear(1) のままなので、
                                // 背景濾過パスは reveal.a≈1 で discard、自色加算パスは accum.a≈0 で discard し、
                                // hdr の帯は触られない。ここで viewport を set すると uv↔画面の対応が崩れ
                                //（ゲーム領域内で uv が 0..1 に張り直され accum 全面を縮約）二重スケールになるため、
                                // 合成には敢えて viewport/scissor を掛けない。
                                draw_ctx.pipelines.transparent.composite_wboit(
                                    &draw_ctx.device,
                                    frame.encoder_mut(),
                                    hdr_view,
                                    accum_view,
                                    reveal_view,
                                );
                            }
                        }

                        // ── エディタオーバーレイパス（WBOIT 合成後・GPU パーティクル前）────
                        // 深度 = 共有深度を Load（不透明深度でテストのみ・パイプライン側 depth_write=false
                        // のため書込なし）／ステンシル = Clear(0)（選択アウトラインのマスク用に 0 初期化）／
                        // カラー = Load（合成済み HDR を保持して重ねる）。これにより Always ギズモ／軸は
                        // 半透明の上にも常に見え、LessEqual ワイヤは不透明に隠れつつ WBOIT 合成に消されない。
                        // 以降このスコープの `pass` はオーバーレイパスを指す（メインパスは drop 済み）。
                        let mut pass = frame.begin_overlay_pass_to(hdr_view);

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

                        // コントロールポイント（点キューブ＋配置プレビューマーカー）描画。
                        // 他のシーンギズモと同じ 1px ライン経路に合流させる（従来どおりの細さ）。
                        if let (Some(cp_batch), Some((_, line_bg))) =
                            (&control_point_batch, &self.line_model_buf)
                        {
                            draw_line_batch(
                                &mut pass, cp_batch,
                                &camera_buf.bind_group, line_bg,
                                &draw_ctx.pipelines,
                            );
                        }
                        // コントロールポイントの区間ライン（太線）描画。
                        // 経路そのものを太く見せるため、選択強調と同じ太線パイプライン
                        // （深度 LessEqual＝可視物には隠れる）で描く。
                        if let (Some(cp_thick_batch), Some((_, line_bg))) =
                            (&control_point_thick_batch, &self.line_model_buf)
                        {
                            draw_thick_line_batch(
                                &mut pass, cp_thick_batch,
                                &camera_buf.bind_group, line_bg,
                                &draw_ctx.pipelines,
                            );
                        }

                        // 地形ブラシ範囲プレビュー（ワイヤスフィア）描画。
                        if let (Some(preview_batch), Some((_, line_bg))) =
                            (&terrain_preview_batch, &self.line_model_buf)
                        {
                            draw_line_batch(
                                &mut pass, preview_batch,
                                &camera_buf.bind_group, line_bg,
                                &draw_ctx.pipelines,
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
                        // 2D シーンビューでは 3D モデル自体を描画しないためアウトラインも省略する。
                        if in_editor && !edit_view_2d {
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

                        // ライトギズモ（選択中ライトアクターのみ、3D シーン）
                        if !scene_canvas_ss {
                            if let (Some(light_gz), Some((_, line_bg))) =
                                (&light_gizmo_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut pass, light_gz,
                                    &camera_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }
                        }

                        // ジョイントアタッチギズモ（選択中アタッチアクターのみ、3D シーン）
                        if !scene_canvas_ss {
                            if let (Some(ja_gz), Some((_, line_bg))) =
                                (&jointattach_gizmo_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut pass, ja_gz,
                                    &camera_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }
                        }

                        // パーティクルエミッタギズモ（選択中エミッタアクターのみ、3D シーン）
                        if !scene_canvas_ss {
                            if let (Some(particle_gz), Some((_, line_bg))) =
                                (&particle_gizmo_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut pass, particle_gz,
                                    &camera_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }
                        }

                        // スカイボックスギズモ（選択中 WorldAnchored スカイボックスのみ、3D シーン）
                        if !scene_canvas_ss {
                            if let (Some(skybox_gz), Some((_, line_bg))) =
                                (&skybox_gizmo_batch, &self.line_model_buf)
                            {
                                draw_line_batch(
                                    &mut pass, skybox_gz,
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
                        // 選択中コライダーの太線（同カメラ・同深度挙動、1px より太く強調）
                        if let (Some(sel_batch), Some((_, line_bg))) =
                            (&collider_wireframe_sel_batch, &self.line_model_buf)
                        {
                            draw_thick_line_batch(
                                &mut pass, sel_batch,
                                &camera_buf.bind_group, line_bg,
                                &draw_ctx.pipelines,
                            );
                        }

                        // 3D キャンバス配下 2D コライダーワイヤーフレーム
                        // （3D シーン内キャンバス上の Actor2D コライダー）。
                        // canvas_to_world 変換済みで 3D ワールド座標を持つため、
                        // 3D コライダー同様に 3D カメラパスで常に描画する
                        // （scene_canvas_ss でもガードしない・3D Canvas アウトラインと同扱い）。
                        if let (Some(coll2d_c3d_batch), Some((_, line_bg))) =
                            (&collider_2d_canvas3d_wireframe_batch, &self.line_model_buf)
                        {
                            draw_line_batch(
                                &mut pass, coll2d_c3d_batch,
                                &camera_buf.bind_group, line_bg,
                                &draw_ctx.pipelines,
                            );
                        }
                        // 選択中コライダーの太線（3D キャンバス配下・同カメラ）
                        if let (Some(sel_batch), Some((_, line_bg))) =
                            (&collider_2d_canvas3d_wireframe_sel_batch, &self.line_model_buf)
                        {
                            draw_thick_line_batch(
                                &mut pass, sel_batch,
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
                            // 選択中コライダーの太線（2D シーン・メインカメラ）
                            if let (Some(sel_batch), Some((_, line_bg))) =
                                (&collider_2d_wireframe_sel_batch, &self.line_model_buf)
                            {
                                draw_thick_line_batch(
                                    &mut pass, sel_batch,
                                    &camera_buf.bind_group, line_bg,
                                    &draw_ctx.pipelines,
                                );
                            }
                        }

                        // カメラアイコンモデル（全カメラアクター、3D シーン）
                        // camera.glb を InstancedModelBatch で描画する
                        if !scene_canvas_ss && !cam_gizmo_actor_mats.is_empty() {
                            if let Some(gizmo) = &self.camera_gizmo {
                                // カメラギズモモデルは従来パイプライン（RT なし）で描画する。
                                draw_model_indirect(
                                    &mut pass, &gizmo.gpu_model, &gizmo.batch,
                                    &camera_buf.bind_group, draw_ctx.light_buffer.bind_group(LightingPass::MainCamera),
                                    // エディタギズモアイコンはワイヤ化しない（従来どおり塗りで表示）。
                                    &draw_ctx.pipelines, None, false, false,
                                    None,
                                );
                            }
                        }

                        // ライトアイコンモデル（全ライトアクター、3D シーン）
                        if !scene_canvas_ss && !light_gizmo_actor_mats.is_empty() {
                            if let Some(gizmo) = &self.light_gizmo {
                                draw_model_indirect(
                                    &mut pass, &gizmo.gpu_model, &gizmo.batch,
                                    &camera_buf.bind_group, draw_ctx.light_buffer.bind_group(LightingPass::MainCamera),
                                    &draw_ctx.pipelines, None, false, false,
                                    None,
                                );
                            }
                        }

                        // パーティクルエミッタアイコンモデル（全エミッタアクター、3D シーン）
                        if !scene_canvas_ss && !particle_gizmo_actor_mats.is_empty() {
                            if let Some(gizmo) = &self.particle_gizmo {
                                draw_model_indirect(
                                    &mut pass, &gizmo.gpu_model, &gizmo.batch,
                                    &camera_buf.bind_group, draw_ctx.light_buffer.bind_group(LightingPass::MainCamera),
                                    &draw_ctx.pipelines, None, false, false,
                                    None,
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

                        // オーバーレイパスの drop() 時間を計測する（旧構成のメインパス drop 計測を継承）。
                        // wgpu デバッグモードでは drop() 時に全コマンドの検証が走るため、
                        // 多数のアクターがある場合にここが大きなボトルネックになりうる。
                        let _perf_t_drop = std::time::Instant::now();
                        drop(pass);
                        perf_pass_drop_ms = _perf_t_drop.elapsed().as_secs_f64() * 1000.0;
                    }

                    // メインパス〜WBOIT 合成〜オーバーレイパスまでの合計時間（本ブロック全体）。
                    perf_main_pass_ms = _perf_t_main.elapsed().as_secs_f64() * 1000.0;

                    // ── GPU パーティクル描画（Phase RP）────────────────────────────
                    // メインパス／透明合成（距離ソートはメインパス内・WBOIT は上）が完了した後、
                    // ブルーム／トーンマップより前の HDR（トーンマップ前）へ加算/アルファ合成する。
                    // color=hdr_view を LoadOp::Load、深度は共有深度を Load（テストのみ・書込なし）。
                    // エミッタ 0 個ならパス自体を開かない（追加コストゼロ）。
                    // TODO: Alpha ブレンドのエミッタ単位粗ソート（現状は登録順）。indirect draw count 化。
                    // G-Buffer デバッグ表示中は GPU パーティクルも描かない（半透明と同じ理由で、
                    // 生値の上に加算・アルファ合成されると G-Buffer の値が読めなくなる）。
                    if self.particle_system.has_emitters() && !gbuffer_debug_active {
                        // ── Play レターボックス時のビューポート適用（半透明/不透明と一致させる）──
                        // GPU パーティクルは 3D メインカメラ（camera_buf）で hdr_view（実サーフェス全面）
                        // へ直接描くため、ビューポート未適用だとレターボックス時に全面基準へズレる
                        //（WBOIT 半透明メッシュと同一症状）。不透明メインパスと同じ game_viewport を適用する。
                        // ここは play_viewport_ok を宣言した内側ブロック（メインパス）を抜けた外側スコープで、
                        // play_viewport_ok は参照できない。そこでクランプ済みの game_viewport（外側可変変数に
                        // 永続）を実サーフェスへ再クランプして安全性を再確認する（clamp は冪等で、
                        // 元が縮退なら None を返し当該フレームはスキップ＝メインパスの挙動と一致）。
                        // 借用順の都合上、ppass が frame を可変借用する前に surface_size を取得しておく。
                        let particle_vp = if self.mode == RuntimeMode::Play && !self.paused {
                            let (psw, psh) = frame.surface_size();
                            clamp_viewport_to_target(game_viewport, psw as f32, psh as f32)
                        } else {
                            // Edit モードは常にターゲット全面（ビューポート非適用）。エディタ描画へ影響なし。
                            None
                        };
                        let mut ppass = frame.begin_particle_pass_to(hdr_view);
                        if let Some((vp_x, vp_y, vp_w, vp_h)) = particle_vp {
                            ppass.set_viewport(vp_x, vp_y, vp_w, vp_h, 0.0, 1.0);
                            ppass.set_scissor_rect(vp_x as u32, vp_y as u32, vp_w as u32, vp_h as u32);
                        }
                        self.particle_system.draw(
                            &mut ppass,
                            &draw_ctx.pipelines.particles,
                            &camera_buf.bind_group,
                        );
                    }

                    // ── 速度バッファのデバッグ可視化（SEED_DEBUG_VELOCITY=1 のときだけ）──
                    //
                    //   速度 RT（G-Buffer の RT4）を疑似カラーでシーン HDR へ **上書き** する。
                    //   環境変数が未設定なら pipelines.velocity_debug が None なので完全に 0 コスト。
                    //   色の読み方は shaders/velocity_debug.wgsl の冒頭コメントが正典
                    //   （灰色=速度0 / 赤・シアン=水平 / 緑・マゼンタ=垂直 / 青=飽和）。
                    //
                    //   【なぜこの位置なのか】ライティング直後に置くと、後続の SSGI・反射合成・
                    //   半透明・スカイボックス・ギズモが可視化の上へ描き込んでしまい、
                    //   「速度が 0 なのか他の描画で塗り潰されたのか」が判別できなくなる。
                    //   HDR への書き込みが全て終わった **ブルーム／トーンマップの直前** へ置けば、
                    //   画面は速度の疑似カラーだけになる（トーンマップは通るので Unlit 表示と
                    //   同様にやや暗くなるが、色相＝符号と方向の判別には影響しない）。
                    if let (Some(vd), Some(vel_view)) =
                        (draw_ctx.pipelines.velocity_debug.as_ref(), gvel)
                    {
                        let vd_bg = vd.create_bind_group(&draw_ctx.device, vel_view);
                        // Clear で開くが、フルスクリーン三角形が全画素を上書きするため
                        // クリア色は見えない（Load 専用のパスヘルパーを足さずに済ませる）。
                        //   ビューポート（Play のレターボックス帯）は適用しない。
                        //   ブルーム／トーンマップと同じく RT 全面を対象にするため、帯の部分も
                        //   速度 0（灰色）で塗られる。デバッグ表示なので帯色より
                        //   「全面が同じ規約で読める」ことを優先している。
                        let mut vpass = frame.begin_deferred_lighting_pass_to(
                            hdr_view, wgpu::Color::BLACK,
                        );
                        vpass.set_pipeline(&vd.pipeline);
                        vpass.set_bind_group(0, &vd_bg, &[]);
                        vpass.draw(0..3, 0..1);
                    }

                    // ── ブルーム（Phase R4, 有効時のみ）───────────────────────────
                    // メインパス後・トーンマップ前に、シーン HDR から高輝度を抽出して
                    // ダウン／アップサンプルし、intensity 倍でシーン HDR へ加算合成する。
                    // 無効時はパスも RT 確保も一切行わない（コスト増ゼロ）。
                    if bloom_on {
                        let bloom_params = crate::engine::core::renderer::BloomParams {
                            threshold: self.post_fx.bloom_threshold,
                            knee:      self.post_fx.bloom_knee,
                            intensity: self.post_fx.bloom_intensity,
                        };
                        draw_ctx.post.run_bloom(
                            &draw_ctx.device, frame.encoder_mut(),
                            &self.rt_pool, &bloom_targets, hdr_view, bloom_params,
                        );
                    }

                    // ── トーンマップ（HDR → LDR 中間, Phase R4）───────────────────
                    // R3 では直接スワップチェーンへ出していたが、R4 では 2D オーバーレイを
                    // トーンマップ後の LDR へ描くため、いったん LDR 中間 RT へ出す。
                    // ビネット有効時はトーンマップ前段に挿す（チェーン: hdr→ビネット→トーンマップ）。
                    {
                        // ビネット強度（土台のサンプル値。将来はプロジェクト設定でデータ駆動化）。
                        const VIGNETTE_INTENSITY: f32 = 0.4;
                        let vignette_stage = inter_view.map(|iv| {
                            crate::engine::core::renderer::VignetteStage {
                                inter_view: iv,
                                params: crate::engine::core::renderer::VignetteParams {
                                    intensity: VIGNETTE_INTENSITY,
                                    ..Default::default()
                                },
                                mask: None,
                            }
                        });
                        // G-Buffer デバッグ表示中はトーンマップを素通し（TonemapOperator::None）にする。
                        // Reinhard を通すと 0..1 の値（ラフネス等）が一律に暗くなり、
                        // 画面の明度からチャンネル値を読み取れなくなるため。
                        let tonemap_operator = if gbuffer_debug_active {
                            crate::engine::core::renderer::TonemapOperator::None
                        } else {
                            crate::engine::core::renderer::TonemapOperator::ReinhardLuma
                        };
                        frame.tonemap_to_ldr(
                            &draw_ctx.post, &draw_ctx.device, hdr_view, ldr_view, vignette_stage,
                            tonemap_operator,
                        );
                    }

                    // ── シーンキャンバスオーバーレイパス（シーンSS専用）──────────────
                    // 3D シーンのカラーを保持しつつ、2D キャンバス要素を最前面に合成する。
                    // アクター編集タブは camera_buf が 2D なのでメインパスで済む。
                    // Phase R4: 描画先をトーンマップ後の LDR 中間へ移し、UI がトーンマップで
                    // 暗化しないようにした（R3 の既知課題を解消）。オーバーレイ用パイプラインは
                    // HDR フォーマットのまま（LDR 中間も物理は Rgba16Float のため変更不要）。
                    if scene_canvas_ss {
                        if let Some(canvas_cam_buf) = self.canvas_overlay_camera_buf.as_ref() {
                            // トーンマップ後の LDR 中間へ 2D 要素を直描き（トーンマップ非適用）。
                            let mut overlay_pass = frame.begin_canvas_overlay_pass_to(ldr_view);

                            // 前面ゾーンの 2D キャンバススプライト
                            //（アウトラインより前に描画してアウトラインを前面に）。
                            // 背景ゾーンはメインパス冒頭（3D ワールドより先）で描画済み。
                            // 3D Canvas スプライトはメインパスで 3D カメラ描画済みのためここでは不要。
                            if !sprite_prepared_2d_fg.is_empty() {
                                draw_sprite_batches(
                                    &mut overlay_pass,
                                    &draw_ctx.pipelines.sprite,
                                    &canvas_cam_buf.bind_group,
                                    &main_inst_buf,
                                    &sprite_prepared_2d_fg,
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
                            // 選択中コライダーの太線（シーン SS オーバーレイ・2D カメラ）
                            if let (Some(sel_batch), Some((_, line_bg))) =
                                (&collider_2d_wireframe_sel_batch, &self.line_model_buf)
                            {
                                draw_thick_line_batch(
                                    &mut overlay_pass, sel_batch,
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

                    // ── 最終段: FXAA／プレゼントコピー（LDR 中間 → スワップチェーン, Phase R4）
                    // トーンマップ済み LDR（＋2D オーバーレイ）をスワップチェーンへ書き出す。
                    // FXAA 有効時はエッジをなめらかにし、無効時は中央 1 タップのコピー。
                    // この 1 パスがトーンマップ後 LDR → スワップチェーンの橋渡しを兼ねる。
                    frame.present_to_swapchain(
                        &draw_ctx.post, &draw_ctx.device, ldr_view, fxaa_on,
                    );

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

                    // ── ID パス ────────────────────────────────────────────────
                    // Edit / Pause 中は常に描く（ピッキング・D&D のワールド座標取得が依存）。
                    // Play 中は既定でスキップし、`SEED_ID_PASS_IN_PLAY` が設定されたときだけ
                    // 毎フレーム描いて合成（第 3 層）が読める per-actor マスクを供給する。
                    // 判断根拠と使い分けの指針は id_pass.rs の ID_PASS_IN_PLAY コメントを参照。
                    // コストは [PERF] 行の `id=` フィールドで計測できる（SEED_PERF_LOG=1）。
                    let draw_id_pass_this_frame = in_editor
                        || crate::engine::methods::drawer::id_pass::id_pass_enabled_in_play();
                    if draw_id_pass_this_frame {
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
                                // ライトギズモ ID バインドグループ（ベース = light_gizmo_id_base）
                                let light_gizmo_id_base_opt: Option<(wgpu::Buffer, wgpu::BindGroup)> =
                                    if !light_gizmo_actor_mats.is_empty() && self.light_gizmo.is_some() {
                                        Some(draw_ctx.create_id_base_bg(light_gizmo_id_base))
                                    } else { None };
                                // パーティクルエミッタギズモ ID バインドグループ（ベース = particle_gizmo_id_base）
                                let particle_gizmo_id_base_opt: Option<(wgpu::Buffer, wgpu::BindGroup)> =
                                    if !particle_gizmo_actor_mats.is_empty() && self.particle_gizmo.is_some() {
                                        Some(draw_ctx.create_id_base_bg(particle_gizmo_id_base))
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
                                            // SS レイアウト時（2D シーンビュー含む）は描画と同じ
                                            // ビューポート基準レイアウトで ID を配置する
                                            let viewport_size: Option<[f32; 2]> =
                                                if ss_layout { Some([vp_w, vp_h]) } else { None };
                                            // Camera 参照のルートキャンバス用ビューポートオーバーライドマップ
                                            let play_gvp_id = if ss_layout && !in_editor { Some(game_viewport) } else { None };
                                            // ビューポート上書き + ルート自動解像度マップ（描画と同一条件・共通ヘルパー）
                                            let (canvas_vp_overrides_id, root_auto_sizes_id) = if ss_layout {
                                                build_ss_layout_maps_free(
                                                    &scene.actors, &scene.world, wl, vp_w, vp_h, play_gvp_id,
                                                    self.project_resolution, edit_view_2d)
                                            } else {
                                                (std::collections::HashMap::new(), std::collections::HashMap::new())
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
                                                [1.0, 1.0],
                                                canvas_scale, y_sign, viewport_size,
                                                &canvas_vp_overrides_id,
                                                &root_auto_sizes_id,
                                                canvas_id_offset,
                                                // トップレベルは SS サブツリー扱い
                                                // （Actor3D 通過で false になり 3D キャンバス子を除外）
                                                CanvasDrawZone::Foreground, true, edit_view_2d, &mut items,
                                            );
                                            // スプライト描画と同一の順序（背景ゾーン → 前面ゾーン、
                                            // 各ゾーン内はレイヤー昇順の安定ソート）へ並べ替える。
                                            // ID パスは後勝ちのため、この順序で描画すると
                                            // 視覚的最前面のスプライトがピックされる。
                                            let (mut id_bg, mut id_fg): (Vec<_>, Vec<_>) =
                                                items.into_iter().partition(
                                                    |&(_, _, _, zone, _)| zone == CanvasDrawZone::Background);
                                            id_bg.sort_by_key(|&(_, _, _, _, layer)| layer);
                                            id_fg.sort_by_key(|&(_, _, _, _, layer)| layer);
                                            // ゾーン・レイヤーを除いた 3 要素タプルへ戻す
                                            id_bg.into_iter().chain(id_fg)
                                                .map(|(id, m, p, _, _)| (id, m, p))
                                                .collect()
                                        } else { vec![] }
                                    } else { vec![] };

                                // 3D Canvas 子スプライト ID アイテム収集
                                // Actor3D + CanvasComponent を持つアクターの 2D 子スプライトを WS で pick できるようにする。
                                // is_canvas に関わらず常に収集する（3D シーン中の 3D Canvas 対応）。
                                // actor edit 2D タブは CPU picking 専用のため除外する。
                                // 2D シーンビューでは 3D Canvas 自体が非表示のため収集しない
                                // 子スプライト ID アイテムと、3D キャンバスのパネル面ピック
                                // アイテム（透明でも面全体を選択可能にする深度対応クワッド）を
                                // 同一 DFS 走査で同時収集する。
                                let (canvas_3d_child_id_raw_items, canvas_panel_pick_items):
                                    (Vec<(u32, [[f32; 4]; 4], Option<String>)>, Vec<(u32, [[f32; 4]; 4])>) =
                                    if !use_ortho_2d_camera {
                                        if let Some(scene) = &self.scene {
                                            let wl = self.active_world_line;
                                            let mut items  = Vec::new();
                                            let mut panels = Vec::new();
                                            let mut ctr    = 0u32;
                                            collect_3d_canvas_child_id_items(
                                                &scene.actors, &scene.world, wl,
                                                &mut ctr, canvas_id_offset, &mut items, &mut panels,
                                            );
                                            (items, panels)
                                        } else { (vec![], vec![]) }
                                    } else { (vec![], vec![]) };

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

                                // ── コライダー面ピック GPU リソース（render pass より長く生きる）──
                                // ワイヤーフレーム収集時に構築した (raw_id, モデル行列) から
                                // canvas_id パイプライン用バインドグループを生成する。
                                let collider_pick_bgs_2d: Vec<(wgpu::Buffer, wgpu::BindGroup)> =
                                    collider_pick_items_2d.iter()
                                        .map(|&(raw_id, gpu_mat)| {
                                            prepare_canvas_id_bg(
                                                &draw_ctx.device, &draw_ctx.pipelines,
                                                gpu_mat, raw_id,
                                            )
                                        })
                                        .collect();
                                let collider_pick_bgs_3dcanvas: Vec<(wgpu::Buffer, wgpu::BindGroup)> =
                                    collider_pick_items_3dcanvas.iter()
                                        .map(|&(raw_id, gpu_mat)| {
                                            prepare_canvas_id_bg(
                                                &draw_ctx.device, &draw_ctx.pipelines,
                                                gpu_mat, raw_id,
                                            )
                                        })
                                        .collect();
                                let collider_pick_bgs_3d: Vec<(wgpu::Buffer, wgpu::BindGroup)> =
                                    collider_pick_items_3d.iter()
                                        .map(|&(raw_id, gpu_mat)| {
                                            prepare_canvas_id_bg(
                                                &draw_ctx.device, &draw_ctx.pipelines,
                                                gpu_mat, raw_id,
                                            )
                                        })
                                        .collect();
                                // 3D キャンバスのパネル面ピック BG（深度対応・白フォールバック）。
                                let canvas_panel_pick_bgs: Vec<(wgpu::Buffer, wgpu::BindGroup)> =
                                    canvas_panel_pick_items.iter()
                                        .map(|&(raw_id, gpu_mat)| {
                                            prepare_canvas_id_bg(
                                                &draw_ctx.device, &draw_ctx.pipelines,
                                                gpu_mat, raw_id,
                                            )
                                        })
                                        .collect();
                                // コライダー面・パネル面は全域選択可能とするため白 1×1（alpha=1）
                                // テクスチャ BG を 1 つ生成して全アイテムで共有する。
                                let collider_pick_white_bg: Option<wgpu::BindGroup> =
                                    if collider_pick_bgs_2d.is_empty()
                                        && collider_pick_bgs_3dcanvas.is_empty()
                                        && collider_pick_bgs_3d.is_empty()
                                        && canvas_panel_pick_bgs.is_empty() {
                                        None
                                    } else {
                                        Some(draw_ctx.device.create_bind_group(&wgpu::BindGroupDescriptor {
                                            label:   Some("ColliderPick White Tex BG"),
                                            layout:  &draw_ctx.pipelines.canvas_id.tex_bgl,
                                            entries: &[
                                                wgpu::BindGroupEntry {
                                                    binding:  0,
                                                    resource: wgpu::BindingResource::TextureView(
                                                        &draw_ctx.pipelines.canvas_id.white_view
                                                    ),
                                                },
                                                wgpu::BindGroupEntry {
                                                    binding:  1,
                                                    resource: wgpu::BindingResource::Sampler(
                                                        &draw_ctx.pipelines.canvas_id.sampler
                                                    ),
                                                },
                                            ],
                                        }))
                                    };

                                let mut id_pass = frame.begin_id_pass(&id_buf.view);

                                // ── コライダー面ピック描画（深度を加味して可視物と同等に選択）──
                                // WS（3D 透視）カメラのコライダー面は深度対応バリアントで描画する:
                                //   depth_compare=LessEqual / depth_write=true により、メインパスの
                                //   シーン深度に対してテストしつつ自身も深度を書くため、コライダー面
                                //   同士・可視物との重なりが「カメラに近い方優先」で解決される
                                //   （描画順に依存しない = 手前のコライダーが確実に選択される）。
                                // 2D（SS ortho / 2D ortho）は 3D シーン深度と比較不能なため従来どおり
                                // 深度なし（描画順の意味論）で描画する。
                                //   - 3D コライダー:     WS perspective カメラ（camera_buf）・深度あり
                                //   - 3D キャンバス配下: WS perspective カメラ（camera_buf）・深度あり
                                //   - 通常 2D シーン:   SS 時は 2D ortho カメラ、WS 2D 時は camera_buf・深度なし
                                if let Some(white_bg) = &collider_pick_white_bg {
                                    // 3D キャンバスのパネル面（深度あり・WS カメラ）を「最初」に描画する。
                                    // これによりコライダーと同一平面（キャンバス上の子コライダー等）では
                                    // 後から描くコライダーが LessEqual の同値で勝ち（子を選択できる）、
                                    // パネルより奥のコライダーは深度で負ける（手前のパネルが選択される）。
                                    // 透明なパネル領域でも面全体がピック対象となり、奥のコライダーではなく
                                    // キャンバスが選択される。可視スプライトは後段の Always 描画で上書きされる。
                                    if !canvas_panel_pick_bgs.is_empty() {
                                        let tex_refs: Vec<&wgpu::BindGroup> =
                                            vec![white_bg; canvas_panel_pick_bgs.len()];
                                        draw_collider_pick_items(
                                            &mut id_pass, &draw_ctx.pipelines,
                                            &camera_buf.bind_group,
                                            &canvas_panel_pick_bgs, &tex_refs, true,
                                        );
                                    }
                                    // 3D コライダー面クワッド（常に WS カメラ・深度あり）
                                    if !collider_pick_bgs_3d.is_empty() {
                                        let tex_refs: Vec<&wgpu::BindGroup> =
                                            vec![white_bg; collider_pick_bgs_3d.len()];
                                        draw_collider_pick_items(
                                            &mut id_pass, &draw_ctx.pipelines,
                                            &camera_buf.bind_group,
                                            &collider_pick_bgs_3d, &tex_refs, true,
                                        );
                                    }
                                    // 3D キャンバス配下コライダー（常に WS カメラ・深度あり）
                                    if !collider_pick_bgs_3dcanvas.is_empty() {
                                        let tex_refs: Vec<&wgpu::BindGroup> =
                                            vec![white_bg; collider_pick_bgs_3dcanvas.len()];
                                        draw_collider_pick_items(
                                            &mut id_pass, &draw_ctx.pipelines,
                                            &camera_buf.bind_group,
                                            &collider_pick_bgs_3dcanvas, &tex_refs, true,
                                        );
                                    }
                                    // 通常 2D シーンコライダー（キャンバス ID 描画と同じカメラ選択・深度なし）
                                    if !collider_pick_bgs_2d.is_empty() {
                                        let tex_refs: Vec<&wgpu::BindGroup> =
                                            vec![white_bg; collider_pick_bgs_2d.len()];
                                        if scene_canvas_ss {
                                            // シーン SS: 2D ortho（オーバーレイ）カメラで描画する
                                            if let Some(ss_cam) =
                                                self.canvas_overlay_camera_buf.as_ref().map(|b| &b.bind_group)
                                            {
                                                draw_collider_pick_items(
                                                    &mut id_pass, &draw_ctx.pipelines,
                                                    ss_cam,
                                                    &collider_pick_bgs_2d, &tex_refs, false,
                                                );
                                            }
                                        } else {
                                            // WS 2D シーン: メインカメラ（2D ortho / WS）で描画する
                                            draw_collider_pick_items(
                                                &mut id_pass, &draw_ctx.pipelines,
                                                &camera_buf.bind_group,
                                                &collider_pick_bgs_2d, &tex_refs, false,
                                            );
                                        }
                                    }
                                }

                                // 3D MC ID 描画（統合バッチ使用）
                                // lod_id_buffers に絶対 ID が書き込まれているため
                                // id_zero_bg (base=0) で CPU デコードが正しく機能する。
                                // 2D シーンビューでは 3D モデル非表示のためピッキング対象からも外す。
                                if !edit_view_2d {
                                    for (path, sd) in &self.shared_model_batches {
                                        if let Some(&gpu) = gpu_model_by_path.get(path.as_str()) {
                                            draw_id_pass(
                                                &mut id_pass, gpu, &sd.batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                                &sd.id_zero_bg.1,
                                            );
                                        }
                                    }
                                }

                                // ── 水面 ID 描画（Phase W1 フォローアップ）─────────────
                                // 水面クアッドを ID パスにも描き、Edit で水面をクリックしたら
                                // WaterVolume を持つアクタが選択されるようにする。
                                //   ・ID 値       : canvas_id_offset + アクタ DFS + 1（prepare で埋め込み済み）
                                //   ・深度整合    : パイプライン側 LessEqual・深度書込なし。
                                //                   手前の不透明物は ID パスの深度（メインパスのシーン深度を
                                //                   Load 済み）で勝つため、水面より手前をクリックすれば
                                //                   そちらが選択される。
                                //   ・描画順      : 3D モデルの後（水中のモデルより水面を優先）／
                                //                   ギズモアイコンの前（編集ハンドルを優先）。
                                //   ・波の変位無し: W1 の波はフラグメント法線のみなので静的クアッドで一致する。
                                // 2D 編集ビューでは水面自体を描かない（= water_pick_ready が false）。
                                if water_pick_ready {
                                    if let Some(water) = self.water_renderer.as_ref() {
                                        water.draw_id(&mut id_pass, &camera_buf.bind_group);
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

                                // ライトギズモ ID 描画
                                // base = light_gizmo_id_base で全インスタンスを一括描画する。
                                // インスタンス local_idx → light_gizmo_actor_mats[local_idx].0 = dfs_id
                                if let (Some(gizmo), Some((_, light_id_bg))) =
                                    (&self.light_gizmo, &light_gizmo_id_base_opt)
                                {
                                    draw_id_pass(
                                        &mut id_pass,
                                        &gizmo.gpu_model, &gizmo.batch,
                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                        light_id_bg,
                                    );
                                }

                                // パーティクルエミッタギズモ ID 描画
                                // base = particle_gizmo_id_base で全インスタンスを一括描画する。
                                // インスタンス local_idx → particle_gizmo_actor_mats[local_idx].0 = dfs_id
                                if let (Some(gizmo), Some((_, particle_id_bg))) =
                                    (&self.particle_gizmo, &particle_gizmo_id_base_opt)
                                {
                                    draw_id_pass(
                                        &mut id_pass,
                                        &gizmo.gpu_model, &gizmo.batch,
                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                        particle_id_bg,
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
                            // readback 優先度: drop > control_point_drop > add_actor > pick
                            //
                            // コントロールポイント D&D を add_actor / pick より優先するのは、
                            // どちらも「ユーザーがマウスを離した瞬間の 1 回きりの操作」であり、
                            // 後回しにすると次フレームへ再キューされてカーソル位置と着弾点が
                            // ずれる（間にカメラが動くと別の場所に点が置かれる）ため。
                            // 通常ドロップ（drop）より下なのは、そちらが先に実装済みの
                            // 優先度契約であり、同一フレームに両方が飛ぶことは実際上ないため。
                            let drop_pos = self.pending_drop
                                .as_ref()
                                .map(|&(_, sx, sy)| (sx, sy));
                            let cp_pos = self.pending_control_point_drop
                                .as_ref()
                                .map(|&(_, _, sx, sy)| (sx, sy));
                            // コントロールポイントの配置予定マーカー用ホバー。
                            // 実際の追加より優先度を下げ、アクタ追加・ピックより上に置く。
                            // ドラッグ中はクリックもコンテキストメニューも起きないので、
                            // 実質この 3 つが同時に立つことはない。
                            let cp_hover_pos = self.pending_control_point_hover;
                            let add_actor_pos = self.pending_add_actor
                                .as_ref()
                                .and_then(|&(is_2d, _, _, sx, sy)| {
                                    // 2D アクターはスポーン位置不要なので readback しない
                                    if is_2d { None } else { Some((sx, sy)) }
                                });
                            let readback_pos = drop_pos.or(cp_pos).or(cp_hover_pos)
                                .or(add_actor_pos).or(pick_pos);
                            if let Some((px, py)) = readback_pos {
                                let px = px.min(id_buf.width.saturating_sub(1));
                                let py = py.min(id_buf.height.saturating_sub(1));
                                frame.schedule_id_copy(
                                    &id_buf.texture, px, py, &id_buf.readback_buf,
                                );
                                // readback が drop/control_point/add_actor ではなく pick のためかを記録するフラグ
                                did_pick = drop_pos.is_none() && cp_pos.is_none()
                                    && cp_hover_pos.is_none()
                                    && add_actor_pos.is_none() && pick_pos.is_some();
                                // ホバーが読み戻しを取れたか（上位 2 つが立っていないこと）。
                                did_control_point_hover_readback =
                                    drop_pos.is_none() && cp_pos.is_none() && cp_hover_pos.is_some();
                                // readback がコントロールポイント D&D のためだったか。
                                // did_pick とは**同時に true にならない**（＝読み戻しの二重消費が起きない）。
                                did_control_point_readback = drop_pos.is_none() && cp_pos.is_some();
                            }
                        }
                    }

                    mark_frame_stage(FrameStage::EncodeDone);
                    let _perf_t_finish = std::time::Instant::now();
                    frame.finish();
                    perf_finish_ms = _perf_t_finish.elapsed().as_secs_f64() * 1000.0;
                    mark_frame_stage(FrameStage::PresentDone);

                    // ── Hi-Z: submit 後に読み戻しマップを予約する（1 フレーム遅延の要）──────
                    //   schedule_readback で staging へ写した可視性を、次フレームの
                    //   try_read_results が受け取れるようマップ予約する。map_async は submit 後
                    //   （＝ frame.finish() 後）に呼ぶ必要がある（マップ中バッファは COPY 先に使えない）。
                    if self.hiz_need_map {
                        if let Some(hiz) = self.hiz.as_mut() { hiz.map_after_submit(); }
                        self.hiz_need_map = false;
                    }

                    // ── パフォーマンスログ ─────────────────────────────────────────
                    // 60 フレームごとに CPU タイミングを eprintln! で出力する。
                    // total:       フレーム全体（begin_frame GPU 待機 + 記録 + submit）
                    // begin_frame: get_current_texture 待機（GPU バックプレッシャー指標）
                    // ipc:         process_ipc の時間
                    // batch_upd:   MC バッチ更新（LOD 振り分け + write_buffer。オブジェクト視錐台カリングは撤去済み）
                    // skin_cmds:   スキンコンピュートコマンド記録
                    // draw:        draw_model_indirect コマンド記録（メインパス）
                    // id_pass:     ID パスコマンド記録（Edit モードのみ）
                    // grid:        グリッドGPU バッチ生成
                    // finish:      encoder.finish + queue.submit + surface.present
                    // other = total - 上記全て（残りは未計測のコライダー・ギズモ等）
                    // 出力条件: 環境変数 SEED_PERF_LOG 有効時（従来）に加え、
                    // 物理が実際に更新されたフレーム（Play 中/編集時物理稼働中）は
                    // 環境変数なしでも PERF_LOG_INTERVAL ごとに自動出力する。
                    // → 「物理が重い」の切り分けを設定なしで即座に行えるようにする。
                    let do_perf_out = do_perf
                        || (perf_physics_active && perf_idx % PERF_LOG_INTERVAL == 0);
                    if do_perf_out {
                        let total_ms = perf_t_total.elapsed().as_secs_f64() * 1000.0;
                        // 物理更新の合計（3D update + スナップショット記録 + 2D update）
                        let phys_total_ms = perf_physics_ms + perf_snapshot_ms + perf_physics2d_ms;
                        // main_pass は draw を内包するので draw を除いた残り = main_pass - draw = 他の描画コマンド記録
                        let main_rest_ms = (perf_main_pass_ms - perf_draw_ms).max(0.0);
                        let other_ms = (total_ms
                            - perf_begin_frame_ms - perf_ipc_ms - perf_batch_ms - perf_merge_ms
                            - perf_skin_ms - perf_main_pass_ms - perf_id_ms
                            - perf_grid_ms - perf_collider_ms - perf_finish_ms - perf_grass_ms
                            - perf_water_ms - perf_interact_ms
                            - phys_total_ms).max(0.0);
                        eprintln!(
                            "[PERF f={perf_idx}] MC={perf_mc_count} skin_MC={perf_skin_mc_count} dispatches={perf_skin_dispatches} \
                             | total={total_ms:.3}ms \
                             physics={phys_total_ms:.3}ms(3d={perf_physics_ms:.3}ms+snap={perf_snapshot_ms:.3}ms+2d={perf_physics2d_ms:.3}ms) \
                             bf={perf_begin_frame_ms:.3}ms ipc={perf_ipc_ms:.3}ms \
                             batch={perf_batch_ms:.3}ms merge={perf_merge_ms:.3}ms skin={perf_skin_ms:.3}ms \
                             tlas={perf_tlas_ms:.3}ms({}/{perf_tlas_insts}inst,skin={perf_tlas_skin}blas/{perf_tlas_skin_ms:.3}ms) \
                             main_pass={perf_main_pass_ms:.3}ms(draw={perf_draw_ms:.3}ms+pass_drop={perf_pass_drop_ms:.3}ms+rest={main_rest_ms:.3}ms) \
                             sprites={perf_sprite_insts}枚/{perf_sprite_draws}draws \
                             meshlet={perf_meshlet_considered}考慮 \
                             id={perf_id_ms:.3}ms grid={perf_grid_ms:.3}ms collider={perf_collider_ms:.3}ms \
                             grass={perf_grass_ms:.3}ms grass_inst={} \
                             water={perf_water_ms:.3}ms interact={perf_interact_ms:.3}ms \
                             anim_t={:.3}s \
                             finish={perf_finish_ms:.3}ms other={other_ms:.3}ms",
                            if perf_tlas_built { "build" } else { "skip" },
                            // 描画対象の草インスタンス総数（フレーム時間との対応を取るため）。
                            self.terrain.grass_buffers.values().map(|b| b.count()).sum::<u32>(),
                            // 草の風はこの時刻で駆動される。値が止まっていれば風も止まっている。
                            ctx.anim_time,
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
                // 通常のオブジェクトピックが走った時点で、コントロールポイントの選択は必ず解除する。
                // 空クリックでもアクタ選択の変更でも「点はもう掴んでいない」が正しく、
                // 解除しないと別アクタを選んだのに前のアクタの点にギズモが残る。
                self.clear_control_point_selection();
                // 選択変更前の状態を保存する（Undo 記録用）
                let before_inst       = self.selected_instances.clone();
                let before_dfs_ids    = self.selected_actor_dfs_ids.clone();
                let before_primary    = self.actor_virtual_selected_idx;

                if raw == 0 {
                    // GPU ID が背景ヒット: 3D ワールドキャンバス面のレイピックを試みる
                    // （world タブでキャンバス矩形範囲をクリックして選択できるようにする）。
                    let canvas_hit = self.last_cursor_pos
                        .and_then(|(cx, cy)| self.pick_3d_world_canvas(cx, cy));
                    if let Some(dfs_usize) = canvas_hit {
                        self.actor_virtual_selected_slot_idx = 0;
                        if self.drag.ctrl_at_press {
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
                            self.actor_virtual_selected_idx = Some(dfs_usize);
                            self.selected_actor_dfs_ids     = vec![dfs_usize];
                        }
                        self.selected_instances.clear();
                        self.send_actor_components(dfs_usize as u32, 0);
                    } else if !self.drag.ctrl_at_press {
                        // 真の空クリック: 選択解除（Ctrl 押下時は何もしない）
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
                    } else if global >= light_gizmo_id_base
                        && global < light_gizmo_id_base + light_gizmo_count
                        && !light_gizmo_actor_mats.is_empty()
                    {
                        // ライトギズモアイコン選択
                        // global - light_gizmo_id_base = ライトギズモのローカルインスタンスインデックス
                        let light_local_idx = (global - light_gizmo_id_base) as usize;
                        if let Some(&(dfs_id, _)) = light_gizmo_actor_mats.get(light_local_idx) {
                            self.actor_virtual_selected_slot_idx = 0;
                            if self.drag.ctrl_at_press {
                                // Ctrl+クリック: マルチ選択トグル（カメラギズモと同流儀）
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
                    } else if global >= particle_gizmo_id_base
                        && global < particle_gizmo_id_base + particle_gizmo_count
                        && !particle_gizmo_actor_mats.is_empty()
                    {
                        // パーティクルエミッタギズモアイコン選択
                        // global - particle_gizmo_id_base = エミッタギズモのローカルインスタンスインデックス
                        let pe_local_idx = (global - particle_gizmo_id_base) as usize;
                        if let Some(&(dfs_id, _)) = particle_gizmo_actor_mats.get(pe_local_idx) {
                            self.actor_virtual_selected_slot_idx = 0;
                            if self.drag.ctrl_at_press {
                                // Ctrl+クリック: マルチ選択トグル（カメラギズモと同流儀）
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
            // 2D シーンビュー（Edit View2D）へのドロップは拡張子に依らず内容で判定して配置する。
            // 従来は ".actor2d" 拡張子も要求していたが、2D アクターが ".actor" 拡張子で
            // 保存されているケースでは条件を満たせず handle_drop_actor（ルート直置き）へ
            // 誤って落ちてしまい、キャンバスの子にならない不具合があった。
            // handle_drop_actor_2d は中身（is_2d / Canvas コンポーネント有無）で
            //   ・2D アクター（Canvas なし）→ ヒットしたキャンバスの子として挿入
            //   ・2D アクター（ルート Canvas あり）→ シーンルートへ配置
            //   ・非 2D アクター → ルートへフォールバック配置
            // を適切に振り分けるため、拡張子判定を削除して全ドロップをここへ流す。
            // なお 2D ビューでは GPU ピック（3D ワールド座標解決）自体が無意味なため、
            // resolve_spawn_pos の再キュー処理を経由しなくても実害はない。
            if self.edit_view_is_2d() || self.is_2d_edit_tab() {
                // ビューポート（wl==0 の Edit View2D）に加え、2D 系のアクター編集タブ／
                // キャンバス編集タブ（wl>0 かつ actor_edit_canvas_wls）も 2D 経路へ流す。
                // handle_drop_actor_2d 側で active_world_line を基準に配置先を決める。
                self.handle_drop_actor_2d(&path, sx, sy);
            } else {
                match self.resolve_spawn_pos(sx, sy, did_pick) {
                    // ピック処理でバッファ読み出し済みのため次フレームで再試行する
                    None => self.pending_drop = Some((path, sx, sy)),
                    Some(spawn_pos) => self.handle_drop_actor(&path, spawn_pos),
                }
            }
        }

        // ── コントロールポイントの D&D 着弾解決（GPU サブミット後）─────────────
        //
        // 配置順の理由: 通常ドロップ（pending_drop）の**直後**、アクタ追加（pending_add_actor）の
        // **直前**に置く。readback の優先度が「drop > control_point > add_actor」なので、
        // 消費順も同じにしておくと「先に読み戻しを予約した用途が先に読む」が一貫する。
        if let Some((actor_dfs_id, slot_idx, sx, sy)) = self.pending_control_point_drop.take() {
            if self.edit_view_is_2d() || self.is_2d_edit_tab() {
                // 2D ビューではパス（3D の概念）の着弾点を定義できない。
                // take 済みなのでそのまま破棄する（再キューすると永久にキューが残るため）。
            } else if !did_control_point_readback {
                // 今フレームは他用途（通常ドロップ）が読み戻しバッファを使ったので、
                // 解決は次フレームへ回す。
                self.pending_control_point_drop = Some((actor_dfs_id, slot_idx, sx, sy));
            } else {
                // ① メッシュ・水面: ID パスの RGB に焼かれたワールド座標を読む。
                //    背景（id == 0）なら None。
                //    did_control_point_readback が true のとき did_pick は必ず false
                //    （優先度計算上、両者は排他）なので、後段のピック読み出しと
                //    読み戻しバッファを奪い合うことはない。
                let gpu_hit = if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
                    id_buf.read_pixel(&draw_ctx.device).0
                } else {
                    None
                };
                // ② 地形は ID パスに描かれないため、CPU レイマーチで別途取る。
                //    ①② のうちカメラに近い方を採る（詳細は resolve_control_point_drop_hit）。
                //
                // resolve_spawn_pos は流用しない: あちらは何にも当たらないとき
                // 「カメラ前方 10m」へフォールバックしてしまい、
                // 「空をドロップしたら何も起きない」という本機能の要件に反するため。
                let hit = self.resolve_control_point_drop_hit(gpu_hit, sx, sy);
                // 実機での切り分け用の 1 行（ドロップ時のみ・常時 ON）。
                match hit {
                    Some(h) => eprintln!(
                        "[CTRL_POINT] drop request x={} y={} → hit=({:.3},{:.3},{:.3})",
                        sx, sy, h[0], h[1], h[2]
                    ),
                    None => eprintln!("[CTRL_POINT] drop request x={} y={} → hit=none", sx, sy),
                }
                if let Some(hit) = hit {
                    self.handle_add_control_point_world(actor_dfs_id, slot_idx, hit);
                }
                // ヒットが 1 つも無ければ点を追加しない（＝空をドロップしても何も起きない）。
                // 点が置かれた／置かれなかったに関わらず、残っているマーカーは消す。
                self.control_point_drop_preview = None;
            }
        }

        // ── コントロールポイント D&D の「配置予定マーカー」解決（GPU サブミット後）───
        //
        // ドロップと同じ `resolve_control_point_drop_hit` を使うので、
        // 「マーカーが出た位置に必ず点が置かれる」ことが構造的に保証される。
        // 読み戻しを他用途に取られたフレームでは**再キューせず捨てる**
        //（次のホバーが 30Hz で飛んでくるため。古い座標で描くとマーカーが遅れて残る）。
        if let Some((sx, sy)) = self.pending_control_point_hover.take() {
            if self.edit_view_is_2d() || self.is_2d_edit_tab() {
                // 2D ビューではパス（3D の概念）の着弾点を定義できないのでマーカーも出さない。
                self.control_point_drop_preview = None;
            } else if did_control_point_hover_readback {
                let gpu_hit = if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
                    id_buf.read_pixel(&draw_ctx.device).0
                } else {
                    None
                };
                // ヒット無し（空をドラッグ中）なら None のまま入れる ＝ マーカーを消す。
                self.control_point_drop_preview =
                    self.resolve_control_point_drop_hit(gpu_hit, sx, sy);
            }
        }

        // ── コンテキストメニュー経由のアクター追加（GPU サブミット後）─────────────
        // D&D と同じ IDバッファ読み取りでスポーン位置を確定する
        if let Some((is_2d, world_line, parent_dfs_id, sx, sy)) = self.pending_add_actor.take() {
            if is_2d {
                // 2D アクターはスポーン位置不要なので即座に追加する
                self.handle_add_actor_2d(world_line, parent_dfs_id);
            } else {
                // コントロールポイント D&D が読み戻しを使ったフレームでは、
                // 読み戻しバッファにはそちらの座標が入っている。ここで読むと
                // 「点を置いた位置にアクタが湧く」誤配置になるため、ピック時と同じく
                // 解決を諦めて次フレームへ再キューする。
                let readback_consumed =
                    did_pick || did_control_point_readback || did_control_point_hover_readback;
                match self.resolve_spawn_pos(sx, sy, readback_consumed) {
                    // 再キューイング
                    None => self.pending_add_actor = Some((false, world_line, parent_dfs_id, sx, sy)),
                    Some(spawn_pos) => self.handle_add_actor(world_line, parent_dfs_id, Some(spawn_pos)),
                }
            }
        }

        // ─ 7. EndFrame（Play 時のみ）─────────────────
        if time_running {
            use crate::engine::ecs::Phase;
            use crate::engine::core::scripting::publish_input;
            // EndFrame フェーズのスクリプトからも Input API を使えるようにする
            publish_input(Some(&self.input));
            if let Some(scene) = &mut self.scene { scene.run_phase(Phase::EndFrame, &ctx); }
            publish_input(None);
            // EndFrame 中に積まれたシーン操作コマンドも同フレーム内で適用する
            self.apply_script_scene_commands();
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
        // start_physics（初回フレーム末: 地形コライダー再登録＋char_world ミラー322 trimesh
        // QBVH 同期構築）は present より後に走る。ここを通過できずに凍結すると request_redraw に
        // 到達せずループが恒久停止するため、通過を独立ステージとして記録する。
        mark_frame_stage(FrameStage::StartPhysicsDone);

        if dbg { eprintln!("[SEED FRAME {dbg_frame}] end"); }

        // ── フレーム凍結ウォッチドッグ / ハートビート ──────────────────────────
        // request_redraw まで到達＝このフレームが完了。FrameEnd ステージを記録し、
        // Play（非ポーズ）ではハートビートを出す（凍結の「直前まで回っていた」ことの証跡）。
        {
            use std::sync::atomic::Ordering;
            mark_frame_stage(FrameStage::FrameEnd);
            if *PLAY_DIAG_ENABLED && self.mode == RuntimeMode::Play && !self.paused {
                let now_ms  = WD_EPOCH.elapsed().as_millis() as u64;
                let n       = PLAY_DIAG_FRAME.load(Ordering::Relaxed);
                let last_hb = PLAY_HB_LAST_MS.load(Ordering::Relaxed);
                let gap     = now_ms.saturating_sub(last_hb);
                // 60 フレーム毎 or 前フレームから 1 秒以上空いたら出す。
                if n % PLAY_HB_EVERY == 0 || gap >= PLAY_HB_GAP_MS {
                    eprintln!("[PLAY_HB] frame={n} gap={gap}ms");
                    PLAY_HB_LAST_MS.store(now_ms, Ordering::Relaxed);
                }
            }
        }

        // フォーカスが無い間はフレームレートを抑える（遮蔽時の present 即時リターンによる
        // 暴走ループと、それに伴う毎フレーム Debug.Log の氾濫を防ぐ）。
        self.pace_frame_if_unfocused(perf_t_total);
        if let Some(window) = &self.window { window.request_redraw(); }
    }
}

// ============================================================
//  カメラプレビューのライティング資源に関する静的検証
//
//  group 4（ライト＋シャドウ＋クラスタの複合 BindGroup）には**カメラ固有の資源**が
//  含まれる:
//    - CSM（binding 2/5）  : カスケードはカメラ視錐台にフィットさせる
//    - クラスタ（binding 7〜9）: フロクセル分割は near/far/fov/ビューポート依存
//  そのため「ゲームカメラの映像」であるカメラプレビューのパスでメインカメラ用の資源を
//  使うと、プレビューのライティングがデバッグカメラの向きで変わってしまう
//  （＝プレビューがゲームカメラの映像でなくなる）。これは実際に発生したバグである。
//
//  この取りこぼしはコンパイルでも実行時エラーでも検出できない（型は同じ &BindGroup /
//  &ShadowResources）ため、プレビューパス区間をマーカーで囲み、その中にメインカメラ用の
//  資源が現れないことをソース走査で検証する。
// ============================================================
#[cfg(test)]
mod camera_preview_lighting_tests {
    /// プレビューパス区間の開始マーカー（frame_renderer.rs 内のコメント）。
    const PREVIEW_BEGIN: &str = "[CAMERA-PREVIEW-PASS-BEGIN]";
    /// プレビューパス区間の終了マーカー。
    const PREVIEW_END:   &str = "[CAMERA-PREVIEW-PASS-END]";

    /// カメラプレビューのパス区間が、プレビュー専用のライティング資源だけを使うこと。
    ///
    /// 禁止（メインカメラ固有の資源）:
    ///   - `LightingPass::MainCamera` … クラスタ有効＋メインカメラ基準 CSM の group 4 BG
    ///   - `draw_ctx.shadow.`         … メインカメラ基準の CSM 実体（プレビューは shadow_preview）
    /// 必須:
    ///   - `LightingPass::CameraPreview` … プレビュー用 group 4 BG（クラスタ無効＋プレビュー CSM）
    ///   - `shadow_preview`              … プレビューカメラ基準の CSM 構築・記録
    #[test]
    fn camera_preview_pass_uses_preview_lighting_resources() {
        let src = include_str!("frame_renderer.rs");

        let begin = src.find(PREVIEW_BEGIN)
            .expect("プレビューパスの開始マーカーが見つかりません（区間を消さないこと）");
        let end = src.find(PREVIEW_END)
            .expect("プレビューパスの終了マーカーが見つかりません（区間を消さないこと）");
        assert!(begin < end, "プレビューパスのマーカーの順序が逆です");

        // マーカー間＝プレビュー（ゲームカメラ）を描く区間。
        let region = &src[begin..end];

        assert!(
            !region.contains("LightingPass::MainCamera"),
            "カメラプレビューのパスでメインカメラ用の group 4 BindGroup を使っています。\
             クラスタ（カメラ固有）とメインカメラ基準の CSM が適用され、プレビューの\
             ライティングがデバッグカメラの向きで変わります。\
             LightingPass::CameraPreview を使ってください。"
        );
        assert!(
            !region.contains("draw_ctx.shadow."),
            "カメラプレビューのパスでメインカメラ基準のシャドウ資源（draw_ctx.shadow）を\
             使っています。CSM のカスケードはカメラ視錐台にフィットする＝カメラ固有のため、\
             プレビューの影がデバッグカメラの向きで変わります。\
             draw_ctx.shadow_preview を使ってください。"
        );
        assert!(
            region.contains("LightingPass::CameraPreview"),
            "カメラプレビューのパスがプレビュー用の group 4 BindGroup を使っていません"
        );
        assert!(
            region.contains("shadow_preview"),
            "カメラプレビューのパスがプレビューカメラ基準の CSM を構築していません"
        );
    }
}

#[cfg(test)]
mod stale_prune_tests {
    use super::compute_stale_batch_prune;
    use std::collections::{HashMap, HashSet};

    /// テスト用ヘルパ: &str スライスを HashSet<String> にする。
    fn set(keys: &[&str]) -> HashSet<String> {
        keys.iter().map(|s| s.to_string()).collect()
    }

    /// N-1 フレーム連続不在では保持し、N フレーム目で解放されること（境界）。
    #[test]
    fn frees_only_after_threshold_frames() {
        const N: u32 = 60;
        let cache = set(&["modelA"]);      // キャッシュには常に modelA が存在
        let alive = HashSet::<String>::new(); // 一度も描画対象に現れない（不在が続く）
        let mut absent = HashMap::new();

        // N-1 フレームまでは解放されない（保持）。
        for frame in 1..N {
            let freed = compute_stale_batch_prune(&cache, &alive, &mut absent, N);
            assert!(freed.is_empty(), "フレーム{frame}: N未満では解放しない");
            assert_eq!(absent.get("modelA"), Some(&frame), "不在カウンタが加算されること");
        }
        // N フレーム目でちょうど解放される。
        let freed = compute_stale_batch_prune(&cache, &alive, &mut absent, N);
        assert_eq!(freed, vec!["modelA".to_string()], "N フレーム連続不在で解放されること");
        // 解放後は absent からも消えている。
        assert!(!absent.contains_key("modelA"), "解放後は不在カウンタも掃除されること");
    }

    /// 再出現でカウンタがリセットされ、以後 alive の間は決して解放されないこと。
    #[test]
    fn reappearing_resets_counter() {
        const N: u32 = 60;
        let cache = set(&["modelA"]);
        let absent_alive = HashSet::<String>::new();
        let present_alive = set(&["modelA"]);
        let mut absent = HashMap::new();

        // N-1 フレーム不在にしてカウンタを閾値直前まで進める。
        for _ in 1..N {
            let freed = compute_stale_batch_prune(&cache, &absent_alive, &mut absent, N);
            assert!(freed.is_empty());
        }
        assert_eq!(absent.get("modelA"), Some(&(N - 1)));

        // ここで 1 フレームだけ再出現 → カウンタがリセットされる。
        let freed = compute_stale_batch_prune(&cache, &present_alive, &mut absent, N);
        assert!(freed.is_empty(), "再出現フレームでは解放しない");
        assert!(!absent.contains_key("modelA"), "再出現でカウンタがリセット（絶不在エントリ除去）");

        // 再びカウントし直しになる（N-1 フレーム不在ではまだ解放されない）。
        for _ in 1..N {
            let freed = compute_stale_batch_prune(&cache, &absent_alive, &mut absent, N);
            assert!(freed.is_empty(), "リセット後は再度 N フレーム必要");
        }
        let freed = compute_stale_batch_prune(&cache, &absent_alive, &mut absent, N);
        assert_eq!(freed, vec!["modelA".to_string()]);
    }

    /// alive なキーは即座（1 フレーム目）でも解放されず、absent にも積まれないこと。
    #[test]
    fn alive_keys_are_never_pruned() {
        let cache = set(&["a", "b"]);
        let alive = set(&["a", "b"]);
        let mut absent = HashMap::new();
        for _ in 0..1000 {
            let freed = compute_stale_batch_prune(&cache, &alive, &mut absent, 60);
            assert!(freed.is_empty());
        }
        assert!(absent.is_empty(), "alive なキーは不在マップに積まれないこと");
    }

    /// cache から外部で消えたキーの absent エントリは掃除され、状態マップが肥大しないこと。
    #[test]
    fn absent_map_does_not_leak_for_removed_cache_keys() {
        const N: u32 = 60;
        let alive = HashSet::<String>::new();
        let mut absent = HashMap::new();

        // フレーム1: cache に stale が居て不在 → absent に積まれる。
        let cache1 = set(&["stale"]);
        let freed = compute_stale_batch_prune(&cache1, &alive, &mut absent, N);
        assert!(freed.is_empty());
        assert_eq!(absent.get("stale"), Some(&1));

        // フレーム2: cache から "stale" が外部で消えた（別経路で除去）→ absent も掃除される。
        let cache2 = HashSet::<String>::new();
        let freed = compute_stale_batch_prune(&cache2, &alive, &mut absent, N);
        assert!(freed.is_empty());
        assert!(absent.is_empty(), "cache に無いキーの不在エントリは掃除されること（状態マップの肥大防止）");
    }
}
