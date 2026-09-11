// ============================================================
//  play_mode_ops.rs — 埋め込みインプレース Play（フェーズ2）
//
//  【目的】
//  エディタのシーンパネル用に立ち上がっている Edit ランタイムを **再ロードせず**、
//  構築済みの地形（TerrainState・チャンクメッシュ・GPU）・散布・モデルキャッシュ・
//  レンダラをそのまま保持したまま Edit ↔ Play を切り替える。
//  地形の再構築（tvox→MC 全チャンク・散布・コライダー ≒17s）と初回フレームの
//  BLAS/草構築（≒17s）を丸ごとスキップするのが高速化の本体。
//
//  【状態遷移】
//    ENTER_PLAY: 現アクター状態を ActorData へスナップショット（メモリ内）→ mode=Play
//                → 物理は次フレームで frame_renderer が自動起動 → 応答 PLAY_ENTERED
//    EXIT_PLAY:  スナップショットから非地形アクターを再構築 → mode=Edit
//                → 編集時物理を再初期化 → 応答 PLAY_EXITED
//
//  【地形アクタの扱い（重要）】
//  スナップショット/復元の対象は「シーン世界線（world_line == 0）の非地形アクター」のみ。
//  地形ルート（TERRAIN_ROOT_NAME）とその全チャンクサブツリー、およびアクター編集タブ
//  （world_line > 0）は **現物を保持（Keep）** し、entity・GpuModel・TerrainState を
//  一切作り直さない。TerrainState.chunk_slot_entity が実 entity を参照しているため、
//  地形アクタを despawn/rebuild すると参照が全て無効化され、rebuild_terrain_after_load
//  による 17s の再構築が必要になってしまう。Play 中に地形は変化しない（スクリプトに
//  地形編集 API が無い）ため、保持で状態は完全に保たれる。
// ============================================================

use std::collections::HashMap;
use std::time::Instant;

use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;
use crate::engine::core::clock::Clock;

use super::{App, RuntimeMode, PlaySnapshot, PlaySnapshotEntry, despawn_actor_recursive};
use super::play_snapshot::PlayStartState;
use super::terrain_ops::TERRAIN_ROOT_NAME;

impl App {
    /// 時間スケール（`SEED.Time.Scale`）を等速へ戻し、物理スレッドへも通知する。
    ///
    /// Play 開始・Play 停止・シーン遷移で必ず呼ぶ。スケールは「その Play・その
    /// シーンだけの揮発状態」であり、持ち越すと
    /// 「ヒットストップ中に遷移して戻し忘れ、次のシーンが永久にスロー」という
    /// 復帰不能な事故になるため、境界で必ずリセットする。
    pub(super) fn reset_time_scale_for_play(&mut self) {
        use crate::engine::core::clock::TIME_SCALE_DEFAULT;
        crate::engine::core::scripting::host_api::reset_time_scale();
        // 物理スレッドは独立して回っているので、キャッシュとコマンドの両方を戻す。
        self.physics_time_scale = TIME_SCALE_DEFAULT;
        if let Some(t) = self.physics_thread.as_ref() {
            t.send(crate::engine::physics::PhysicsCommand::SetTimeScale {
                scale: TIME_SCALE_DEFAULT,
            });
        }
        if let Some(t) = self.physics_thread_2d.as_ref() {
            t.send(crate::engine::physics::PhysicsCommand2d::SetTimeScale {
                scale: TIME_SCALE_DEFAULT,
            });
        }
    }

    /// あるトップレベルアクターが「地形ルート」か（world_line == 0 かつ名前が terrain）。
    /// スナップショット/復元で Keep（現物保持）とすべきかの判定に使う。
    ///
    /// Play の Enter/Exit だけでなく、Undo/Redo のアクターツリー復元
    /// （`actor_ops::snapshot_actors_for_wl` / `rebuild_actors_for_wl`）も
    /// 同じ判定で地形を Keep するため `pub(super)` で共有する。
    pub(super) fn is_terrain_root(actor: &Actor) -> bool {
        actor.world_line == 0 && actor.name == TERRAIN_ROOT_NAME
    }

    /// そのトップレベルアクターがスナップショット/再構築の対象か
    /// （world_line == 0 の非地形アクター）。false のものは現物保持（Keep）する。
    fn is_snapshot_target(actor: &Actor) -> bool {
        actor.world_line == 0 && !Self::is_terrain_root(actor)
    }

    // ── ENTER_PLAY ────────────────────────────────────────────

    /// 埋め込みインプレース Play を開始する（IPC: ENTER_PLAY）。
    ///
    /// 現シーンのトップレベルアクターを Restore（wl0 非地形）/ Keep（地形・編集タブ）に
    /// 分類してスナップショットし、mode を Play へ切り替える。地形・散布・GPU・モデル
    /// キャッシュ・レンダラには一切触れない。物理スレッドは次フレームで frame_renderer の
    /// 「Play 初回フレーム末尾起動」ロジックが現シーンのコライダーから自動起動する。
    /// project_settings.json のシーン登録表は Play 開始のたびに読み直す（下記コメント参照）。
    pub(super) fn enter_play(&mut self) {
        // project_settings.json の scenes 配列はエディタのシーンマネージャで Play 中以外に
        // 更新される。起動時に一度だけ読み込んだ登録表（self.scene_registry）のままだと、
        // エディタ起動後に project_settings.json を編集してもスクリプトの
        // Scene.Load("name") / Scene.Transition("name") が名前解決できず失敗する。
        // Play を開始するたびに読み直すことで最新のシーン一覧を反映する
        // （小さな JSON 1 つの読み込みなのでコストは無視できる）。
        self.load_scene_registry();
        // ポインタ状態を捨てる（前回 Play のホバー/押下対象は破棄済みエンティティ）。
        // 二重開始のべき等パスより前に置いて、どの経路でも必ず初期化されるようにする。
        self.pointer.reset();
        // 前回 Play の取り残しのデバッグコマンド（SCRIPT_DEBUG）を捨てる。
        // 残すと、Play を開始した瞬間に前回送った指示が突然走ってしまう。
        self.clear_script_debug_commands();
        // JointAttach 子孫の相対ローカルは Play 開始時点の姿勢から採り直す
        // （Edit で竿先を動かした結果を必ず反映させるため）。
        self.joint_attach_child_locals.clear();
        // 二重開始防止: 既に Play なら応答だけ返す（べき等）。
        if self.mode == RuntimeMode::Play {
            if let Some(ipc) = &self.ipc { ipc.send("PLAY_ENTERED"); }
            return;
        }
        // ── Play 開始状態の器を作る ───────────────────────────────────────
        // Play 中に `SEED.Scene.Transition` で別シーンへ遷移すると
        // `install_loaded_scene` がシーンを丸ごと差し替え、`loaded_scene_path` も
        // 遷移先へ書き換わる。EXIT_PLAY で「Play 開始直前の編集状態へ戻す」ために、
        // ここで開始時点のパスを記録しておく（シーン本体の退避は下の
        // `capture_play_start_scene` が埋める。シーン未ロードでも遷移判定が
        // できるよう、パスの記録だけは先に済ませる）。
        // 二重開始（べき等パス）の後ろに置いてあるので、Play 中に ENTER_PLAY が
        // 重複して届いても記録が遷移先パスで上書きされることはない。
        self.play_start = Some(PlayStartState::new(self.loaded_scene_path.clone()));

        if self.scene.is_none() {
            // シーン未ロードでは Play しても意味がないが、エディタの状態機械を進めるため
            // 応答は返す（アクター無しの空 Play）。
            self.mode = RuntimeMode::Play;
            self.paused = false;
            self.reset_time_scale_for_play();
            if let Some(ipc) = &self.ipc { ipc.send("PLAY_ENTERED"); }
            return;
        }

        // ── 0-a) Undo/Redo 履歴を退避する（Play をまたいだ履歴の連続性）───────
        //   Unity と同じく、Play を停止したあとも Play 前の操作を Ctrl+Z で遡れるようにする。
        //   Play 停止時にアクターツリーは Play 開始直前の状態へ完全復元されるので、
        //   ここで退けておいた履歴は EXIT_PLAY 後にそのまま使える（各 Undo コマンドは
        //   対象を entity ではなく (world_line, DFS ID) で持ち、適用時に引き直すため、
        //   entity が作り直されても解決できる。詳細は App::undo_history_before_play）。
        //   Play 中に何かが履歴へ積まれても、それは Play 用の空履歴に積まれて
        //   EXIT_PLAY で丸ごと捨てられる（Play 中の状態変化は編集操作ではない）。
        self.undo_history_before_play = Some(std::mem::replace(
            &mut self.undo_history,
            crate::engine::core::app_base::undo::UndoHistory::new(),
        ));

        // ── 0-b) 地表カバー場（I3.1）を退避する ────────────────────────
        //   Play 中の積算はゲーム状態であって編集データではない。Stop したときに
        //   Edit 時の保存状態へ戻せるよう、ここで丸ごと複製しておく
        //   （水位 `sim_level_y` が Play 中だけ揮発するのと同じ考え方）。
        //   1 チャンク 2KB 強なので複製コストは無視できる。
        self.snapshot_cover_for_play();

        // ── 1) Play 開始状態のスナップショット ──────────────────────────────
        // トップレベルアクターを並び順に走査し、wl0 非地形は ActorData へシリアライズ、
        // 地形ルート・編集タブは Keep(entity) として現物を退避する（`play_snapshot`）。
        // 順序を保つことで復元後の DFS ID 対応（物理・スクリプトイベント配信が依存）が
        // Play 前と一致する。
        // 同じ 1 回の走査で、シーン遷移が起きたとき用の完全スナップショット
        // （シーンの直列化 JSON ＋ 編集タブのアクターデータ）も `play_start` へ作る。
        // 詳細は play_snapshot.rs を参照。
        self.capture_play_start_scene();

        // ── 2) 編集専用状態のリセット（選択・ギズモ・ホバー・ドラッグ）──────────
        self.selected_instances.clear();
        self.selected_actor_dfs_ids.clear();
        self.actor_virtual_selected_idx = None;
        self.actor_virtual_selected_slot_idx = 0;
        self.hovered_gizmo_part = None;
        self.drag.gizmo_drag = None;
        if let Some(ipc) = &self.ipc { ipc.send("SELECTED:-1"); }

        // ── 3) 編集時物理を停止しタイムラインを畳む ─────────────────────────
        // Play は独自に物理を回す（frame_renderer が次フレーム末で自動起動）。
        // 編集時物理スレッド（押し戻し/タイムライン）はここで確実に落とす。
        self.stop_physics();
        self.stop_physics_2d();
        self.reset_physics_timeline();
        // タブごと退避の物理状態は編集専用。Play 中は使わないのでクリアしておく。
        self.tab_physics.clear();
        self.current_vel_cache_3d.clear();
        self.current_vel_cache_2d.clear();
        self.pending_restore_vel_3d = None;
        self.pending_restore_vel_2d = None;

        // ── 4) パーティクル全解放（前セッションの粒子を持ち越さない）──────────
        self.particle_system.clear_all();
        // インタラクションソースの位置履歴も捨てる（Phase I1）。
        // 残したままシーンが入れ替わると「旧シーンの位置 → 新シーンの位置」の
        // 巨大な速度が 1 フレームだけ場へ焼かれ、草が一斉になぎ倒される。
        self.interaction_velocity.clear();

        // ── 5) オーディオのコンポーネント音源リセット（play_on_start 再発火用）───
        if let Some(audio) = &mut self.audio { audio.reset_components(); }

        // ── 6) 時間リセット・ポーズ解除・モード切替 ─────────────────────────
        // Clock::new() で Time.time / deltaTime を新規 Play と揃える。
        self.clock  = Clock::new();
        // 時間スケール（SEED.Time.Scale）も等速へ戻す。前回 Play で
        // ヒットストップ中に停止した場合でも、次の Play は必ず等速で始まる。
        self.reset_time_scale_for_play();
        self.paused = false;
        self.mode   = RuntimeMode::Play;

        // 【高速化の本体】地形（TerrainState・チャンクメッシュ・GPU）・散布・
        // モデルキャッシュ・レンダラ・shared_model_batches には一切触れていない。
        // これらは構築済みのまま Play に引き継がれる。

        // ── Play 開始前の地形 LOD 事前収束（埋め込みインプレース Play 経路）──────────
        // Edit ランタイムをその場で Play 化するため、Edit のデバッグカメラ位置基準で組まれた
        // チャンク LOD がそのまま残っている。Play ではメインカメラへ視点が飛ぶため、そのまま
        // 描画に入ると初回以降の毎フレーム分割収束（tick_terrain_lod）で数百チャンクが一斉に
        // 遷移し約 20 秒間 8〜10fps に律速される。ここでメインカメラ位置基準の目標 LOD を
        // 1 回でまとめて再メッシュし、Play の最初の描画フレーム前に backlog をゼロにする。
        let cam_pos = self.play_converge_camera_pos();
        self.converge_terrain_lod_blocking(cam_pos);

        // 【一時・診断】ENTER_PLAY から一定時間、届いた WindowEvent 種別を [PLAY_EV] へ出力開始
        // （RedrawRequested の配達が止まる直前に Focused/Occluded 等が来ていないかを見る）。
        super::play_diag::begin_event_trace();

        if let Some(ipc) = &self.ipc { ipc.send("PLAY_ENTERED"); }
    }

    // ── EXIT_PLAY ─────────────────────────────────────────────

    /// スクリプトのカーソルロックを強制解除し、カーソルを表示へ戻す。
    ///
    /// Play 停止・シーン遷移など「スクリプトが解除する機会を失う」経路から呼ぶ。
    /// ロックしていない場合も安全（冪等）。
    pub(super) fn release_script_cursor_lock(&mut self) {
        crate::engine::core::scripting::clear_cursor_lock_request();
        if let Some(window) = self.window.clone() {
            // set_cursor_lock(false) が OS の表示カウンタも 0 以上へ戻すので、
            // ここで追加の後始末は要らない（ClipCursor はロックでは使っていない）。
            self.input.set_cursor_lock(false, &window);
        } else {
            // ウィンドウがまだ／もう無い経路でも、表示カウンタだけは必ず戻す
            //（隠れたままプロセスが残ると OS のカーソルが消えたままになる）。
            crate::engine::core::input::cursor_visibility::force_shown();
        }
    }

    /// ENTER_PLAY で退避した Undo/Redo 履歴を編集側へ戻す（EXIT_PLAY 内部ヘルパ）。
    ///
    /// 退避が無い場合（ウィンドウ Play 等の想定外経路、またはファイル読み直しへ
    /// フォールバックして退避を破棄した場合）は現在の履歴をそのまま使う。
    /// 何度呼んでも安全（退避は take で 1 度きり消費される）。
    fn restore_undo_history_after_play(&mut self) {
        if let Some(history) = self.undo_history_before_play.take() {
            self.undo_history = history;
        }
    }

    /// 埋め込みインプレース Play を停止して編集状態へ復帰する（IPC: EXIT_PLAY）。
    ///
    /// ENTER_PLAY で取ったスナップショットから wl0 非地形アクターを再構築し、mode を
    /// Edit へ戻す。地形・散布・GPU リソースには触れない（Keep 分は現物のまま）。
    pub(super) fn exit_play(&mut self) {
        // セーブデータを自動フラッシュする（Play 中にスクリプトが Set した進行を守る）。
        // セーブはシーンの編集データではなくゲーム進行なので、Edit へ戻しても
        // 巻き戻さず実ファイルへ確定させる（save/mod.rs の方針コメント参照）。
        // Play でなかった場合（べき等パス）も dirty なら書き出すため先頭に置く。
        crate::engine::core::save::flush_if_dirty();
        // ポインタ状態を捨てる（Edit へ戻ったあとに Exit が飛ばないようにする）。
        self.pointer.reset();
        // スクリプトが張ったカーソルロックを必ず解除する（Play 停止で自動解放）。
        // 解除しないと Edit へ戻ってもカーソルが隠れたまま中央へワープし続け、
        // エディタが操作不能になる。未処理の要求も同時に捨てる。
        self.release_script_cursor_lock();
        // JointAttach 子孫の相対ローカルキャッシュを破棄する（Play 専用の揮発状態）。
        // 残すと、次の Play で「前回 Play 終了時の相対関係」が使われてしまう。
        self.joint_attach_child_locals.clear();
        // 誰にも配られなかったデバッグコマンドを捨てる（次の Play へ持ち越さない）。
        self.clear_script_debug_commands();
        // Play でなければ mode だけ Edit に寄せて応答（べき等）。
        // 開始状態の記録も必ず捨てる（次の Play へ持ち越さない）。
        if self.mode != RuntimeMode::Play {
            self.play_start = None;
            // 退避した履歴が残っていれば戻す（Play に入れていない＝シーンは無傷なので、
            // そのまま使える）。持ち越すと次の Play 開始で上書きされ、
            // 本来の履歴が失われるため必ずここで回収する。
            self.restore_undo_history_after_play();
            self.mode = RuntimeMode::Edit;
            self.reset_time_scale_for_play();
            if let Some(ipc) = &self.ipc { ipc.send("PLAY_EXITED"); }
            return;
        }

        let snapshot = self.play_snapshot.take();

        // 【計測】Stop→Edit 復帰の所要時間内訳。ユーザー報告「復帰が遅い」の主因
        //   （アクター再構築 vs 編集物理再起動＝地形コライダー再登録）を実機 1 回で切り分けるため、
        //   常時 ON の [FPS_PHASE] exit_play 行を末尾に出す。各区間を Instant で個別計測する。
        let t_total = Instant::now();

        // ── 1) 物理・パーティクル・オーディオを停止/解放 ─────────────────────
        // スクリプトインスタンスは下のアクター破棄（ScriptComponent Drop）で解放される。
        self.stop_physics();
        self.stop_physics_2d();
        self.particle_system.clear_all();
        // インタラクションソースの位置履歴も捨てる（Phase I1）。
        // 残したままシーンが入れ替わると「旧シーンの位置 → 新シーンの位置」の
        // 巨大な速度が 1 フレームだけ場へ焼かれ、草が一斉になぎ倒される。
        self.interaction_velocity.clear();
        if let Some(audio) = &mut self.audio {
            audio.reset_components();
            // スクリプトが鳴らした BGM（駆け引きのドラムループ等）は Play を抜けたら必ず止める。
            // 速度はスクリプトが変えたまま残るため、次の Play が等倍から始まるよう戻しておく。
            audio.stop_bgm();
            audio.set_bgm_speed(1.0);
        }

        // 地表カバー場を Edit の保存状態へ戻す（Play 中に積もったぶんは捨てる。I3.1）。
        self.restore_cover_after_play();

        // ── 2) Play 中にシーンの差し替え（遷移）が起きたかを判定する ─────────
        // 判定の第一根拠は差し替えフックが立てるフラグ、保険としてパス比較も見る
        // （`PlayStartState::is_scene_replaced`）。
        let play_start = self.play_start.take();
        let scene_replaced = play_start
            .as_ref()
            .map(|s| s.is_scene_replaced(self.loaded_scene_path.as_deref()))
            .unwrap_or(false);

        // ── 3) アクターツリーを復元 ─────────────────────────────────────
        // (a) 遷移が起きていない通常の Play 停止（従来どおり・最速）:
        //     スナップショットから wl0 非地形アクターを再構築する。地形・編集タブは
        //     現物を保持しているので触らない。スナップショットが無い
        //     （ウィンドウ Play 等、想定外経路）場合は何もしない。
        // (b) 遷移が起きた場合:
        //     軽量スナップショットは使えない。Keep エントリが指す entity は差し替えで
        //     破棄された旧 World のものであり、復元しても実体が存在しないため。
        //     Play 開始時にメモリへ取った完全スナップショットから編集状態を組み直す
        //     （ファイルを読まないので、Play 開始前の未保存編集もそのまま戻る）。
        //     それに失敗したときだけ、最後の手段として開始シーンのファイルを読み直す。
        let t_restore = Instant::now();
        if scene_replaced {
            self.restore_edit_state_after_scene_replaced(play_start);
        } else if let (Some(snapshot), true) =
            (snapshot, self.draw_ctx.is_some() && self.scene.is_some())
        {
            self.restore_actors_from_snapshot(snapshot);
        }
        let restore_ms = t_restore.elapsed().as_secs_f64() * 1000.0;

        // ── 3-b) Undo/Redo 履歴を Play 開始前のものへ戻す ─────────────────
        //   アクターツリーの復元が終わったこの時点で戻す（復元経路の途中には
        //   `install_loaded_scene` を通る道があり、その呼び出し元が履歴を破棄するため、
        //   必ず復元の「後」で書き戻す必要がある）。
        //   Play 中に積まれた履歴（通常は空）はここで捨てられる。
        //   例外的に、ファイル読み直しへフォールバックした場合は
        //   `reload_scene_file_after_play` が退避を破棄しているので履歴は空のままとなる。
        self.restore_undo_history_after_play();

        // ── 4) 編集状態へ復帰 ───────────────────────────────────────────
        self.mode = RuntimeMode::Edit;
        // 時間スケールを等速へ戻す。Play 中に 0（停止）にしたまま Stop しても
        // 編集操作・物理プレビューが凍ったままにならないようにする。
        self.reset_time_scale_for_play();
        // clock は編集用にそのまま継続する（Edit は経過時間の連続性を持つ）。

        // 選択状態をクリアしてヒエラルキーを送り直す。
        self.selected_instances.clear();
        self.selected_actor_dfs_ids.clear();
        self.actor_virtual_selected_idx = None;
        self.actor_virtual_selected_slot_idx = 0;

        // アクター（entity）が作り直されたため、タブごと退避物理状態はすべて破棄する
        // （旧 entity キーで ECS を誤って上書きしないため。LOAD_SCENE と同じ扱い）。
        self.tab_physics.clear();
        self.current_vel_cache_3d.clear();
        self.current_vel_cache_2d.clear();
        self.pending_restore_vel_3d = None;
        self.pending_restore_vel_2d = None;

        // 物理タイムラインをリセットし、編集時物理が有効なら初期状態で再起動する
        // （LOAD_SCENE の Edit 復帰処理と同一手順）。
        //   【計測注意】edit_physics_enabled のとき start_physics 内で register_all_terrain_colliders が
        //   走り、地形 322 チャンク分の再登録に数秒かかりうる（別途 [FPS_PHASE] start_physics 行で内訳が出る）。
        //   これが exit_play の主因か否かを下の physics_ms で切り分ける。
        let t_phys = Instant::now();
        self.reset_physics_timeline();
        if self.edit_physics_enabled {
            self.stop_physics();
            self.start_physics();
        }
        if self.edit_physics_2d_enabled {
            self.stop_physics_2d();
            self.start_physics_2d();
        }
        if self.is_edit_physics_pushback_mode() {
            self.enter_edit_physics_pushback();
        } else if self.edit_physics_enabled || self.edit_physics_2d_enabled {
            self.init_physics_timeline();
        }
        let physics_ms = t_phys.elapsed().as_secs_f64() * 1000.0;

        self.send_selected();
        // 復元後のツリーは Play 中の木とは別物（生成分が消え、遷移していれば
        // シーンごと別）なので、スロットリングで潰されない即時送信＋
        // 「入れ替わった」通知を使う。ここを取りこぼすと、Stop 後の
        // ヒエラルキーが Play 中の木のまま残り、クリックすると別アクターが出る。
        self.send_hierarchy_reset();

        // ── 復帰所要時間の内訳ログ（常時 ON・Stop 1 回につき 1 行）──
        //   total   : exit_play 全体（EXIT_PLAY 受信からアクター/物理復元完了まで）。
        //   restore : スナップショットからのアクター再構築（build_actor の GPU 再アップロード含む）。
        //   physics : 編集物理の再起動（有効時は地形コライダー再登録が支配的になりうる）。
        //   なお EXIT_PLAY 送信〜本行までの体感遅延には、コマンドが次フレームで処理されるまでの
        //   キュー待ち（直前フレームが LOD スパイク中なら最大そのフレーム時間）も加わる。
        let total_ms = t_total.elapsed().as_secs_f64() * 1000.0;
        eprintln!(
            "[FPS_PHASE] exit_play total={total_ms:.1}ms restore={restore_ms:.1}ms physics={physics_ms:.1}ms edit_phys={}",
            self.edit_physics_enabled
        );

        if let Some(ipc) = &self.ipc { ipc.send("PLAY_EXITED"); }
    }

    /// Play 中にシーンが差し替わった状態から、Play 開始直前の編集状態へ戻す
    /// （EXIT_PLAY 内部ヘルパ）。
    ///
    /// Unity の Play 停止と同じく **必ず Play 開始直前の状態へ戻す** のがここの責務。
    ///   ① Play 開始時にメモリへ取った完全スナップショットから復元する（第一選択）。
    ///      ファイルを読まないので、Play 開始前の未保存編集もそのまま戻る。
    ///   ② ①が失敗したときだけ、最後の手段として開始シーンのファイルを読み直す。
    ///      このときは未保存編集が失われるためエラーログを出す。
    ///   ③ どちらも不可能なら遷移先のシーンのまま Edit へ戻る。保存は
    ///      scene_save_ops の path_mismatch 保護に弾かれるので誤上書き事故にはならない。
    ///
    /// 復元処理の実体は play_snapshot.rs にある（退避と復元を 1 ファイルへ集約するため）。
    fn restore_edit_state_after_scene_replaced(&mut self, play_start: Option<PlayStartState>) {
        // 開始状態が無い（ウィンドウ Play 等の想定外経路）なら何もできない。
        let Some(start) = play_start else {
            eprintln!(
                "[SEED PLAY] Play 中にシーンが差し替わりましたが、Play 開始状態の記録が \
                 無いため復元できません"
            );
            return;
        };
        // 最後の手段で使うので、消費される前にパスだけ控えておく。
        let start_path = start.scene_path.clone();

        // ① メモリ上の完全スナップショットから復元する。
        if self.restore_from_play_start(start) {
            eprintln!(
                "[SEED PLAY] Play 中にシーン遷移が起きたため Play 開始時の編集状態へ復元しました"
            );
            return;
        }

        // ② ファイル読み直しへフォールバック（未保存編集は失われる）。
        let Some(path) = start_path else {
            eprintln!(
                "[SEED PLAY] Play 開始時の編集状態を復元できず、開始シーンのパスも不明なため \
                 遷移先のシーンのまま Edit へ戻ります"
            );
            return;
        };
        if self.reload_scene_file_after_play(&path) {
            eprintln!(
                "[SEED PLAY] Play 開始時の編集状態を復元できなかったため、開始シーン({path})を \
                 ファイルから読み直しました（Play 開始前に保存していなかった編集は失われています）"
            );
        } else {
            eprintln!(
                "[SEED PLAY] Play 開始シーン({path})の読み直しにも失敗したため、遷移先の \
                 シーンのまま Edit へ戻ります"
            );
        }
    }

    /// スナップショットからトップレベルアクター列を復元する（EXIT_PLAY 内部ヘルパ）。
    ///
    /// - Keep（地形・編集タブ）: 現物をそのまま順序どおりに戻す。
    /// - Restore（wl0 非地形）: 現物（Play 中に変化・新規生成されたもの含む）を despawn し、
    ///   スナップショットの ActorData から build_actor で作り直す（新規 entity・
    ///   新規スクリプトインスタンス）。
    ///
    /// 呼び出し前提: `self.draw_ctx` と `self.scene` がともに Some。
    fn restore_actors_from_snapshot(&mut self, snapshot: PlaySnapshot) {
        let host = self.scripting_host.clone();

        // 現在のトップレベルアクターを取り出す。
        let old_actors = {
            let scene = self.scene.as_mut().unwrap();
            std::mem::take(&mut scene.actors)
        };

        // Restore 対象（wl0 非地形。Play 中に新規生成された分も該当）は despawn し、
        // Keep 対象は entity キーで引けるよう退避する。
        let mut keep_map: HashMap<Entity, Actor> = HashMap::new();
        {
            let scene = self.scene.as_mut().unwrap();
            for a in old_actors {
                if Self::is_snapshot_target(&a) {
                    despawn_actor_recursive(&a, &mut scene.world);
                } else {
                    keep_map.insert(a.entity, a);
                }
            }
        }

        // スナップショットの順序どおりに再構築する。
        let mut new_actors: Vec<Actor> = Vec::with_capacity(snapshot.entries.len());
        for entry in snapshot.entries {
            match entry {
                PlaySnapshotEntry::Keep(e) => {
                    if let Some(a) = keep_map.remove(&e) {
                        new_actors.push(a);
                    }
                    // 取りこぼし（地形/編集タブが Play 中に消えた等、通常起きない）は
                    // 単にスキップする。
                }
                PlaySnapshotEntry::Restore(data) => {
                    // draw_ctx（不変）と scene.world（可変）は別フィールドなので分割借用できる。
                    let build_result = {
                        let ctx   = self.draw_ctx.as_ref().unwrap();
                        let scene = self.scene.as_mut().unwrap();
                        crate::engine::core::app_base::scene::build_actor(
                            data, ctx, &mut scene.world, host.as_ref(), None,
                        )
                    };
                    match build_result {
                        Ok(mut a) => {
                            // シーン世界線（0）を自身と全子孫へ再設定する。
                            a.set_world_line_recursive(0);
                            new_actors.push(a);
                        }
                        Err(e) => {
                            eprintln!("[SEED play] EXIT_PLAY アクター復元失敗: {e}");
                        }
                    }
                }
            }
        }

        // keep_map に残った Keep（順序に現れなかった＝異常時のみ）を末尾へ戻す。
        // entity を宙に浮かせて ECS リークさせないための保険。
        for (_e, a) in keep_map.drain() {
            new_actors.push(a);
        }

        // 復元したツリーに 2D アクターがあれば wl0 をキャンバス世界線として再判定する。
        fn has_any_2d_actor(actors: &[Actor]) -> bool {
            actors.iter().any(|a| a.is_2d() || has_any_2d_actor(a.children()))
        }
        if has_any_2d_actor(&new_actors) {
            self.canvas_world_lines.insert(0);
        } else {
            self.canvas_world_lines.remove(&0);
        }

        let scene = self.scene.as_mut().unwrap();
        scene.actors = new_actors;
    }
}

// ============================================================
//  テスト
// ============================================================
//
//  スナップショットの純粋部分（アクター分類・ActorData シリアライズの忠実性）を検証する。
//  復元方向（build_actor）は DrawContext（GPU デバイス）を要するため単体テストでは
//  検証しない。GPU 非依存の「どのアクターを退避/保持するか」と「to_data が位置・
//  子・コンポーネント名を落とさないか」を対象にする。
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;
    use crate::engine::components::Transform;

    /// Transform 付きの通常アクターをワールドに 1 体作るヘルパ。
    fn make_actor(world: &mut World, name: &str, wl: u32, pos: [f32; 3]) -> Actor {
        let e = world.spawn();
        let mut tf = Transform::default();
        tf.position = pos;
        world.insert(e, tf);
        let mut a = Actor::new(e, name);
        a.world_line = wl;
        a
    }

    /// 分類ロジック: 地形ルート・編集タブは Keep（保持）、wl0 非地形のみ Restore（退避）。
    #[test]
    fn classify_terrain_and_snapshot_targets() {
        let mut world = World::new();
        let normal = make_actor(&mut world, "Player", 0, [0.0; 3]);
        let terrain = {
            let e = world.spawn();
            let mut a = Actor::new_folder(e, TERRAIN_ROOT_NAME);
            a.world_line = 0;
            a
        };
        let edit_tab = make_actor(&mut world, "EditMe", 3, [0.0; 3]);

        // is_terrain_root: 名前 == terrain かつ wl0 のときだけ真。
        assert!(App::is_terrain_root(&terrain));
        assert!(!App::is_terrain_root(&normal));

        // is_snapshot_target: wl0 非地形のみ退避対象。地形・編集タブは保持（現物のまま）。
        assert!(App::is_snapshot_target(&normal), "wl0 非地形はスナップショット対象");
        assert!(!App::is_snapshot_target(&terrain), "地形ルートは保持（退避しない）");
        assert!(!App::is_snapshot_target(&edit_tab), "wl>0（編集タブ）は保持（退避しない）");
    }

    /// スナップショットの to_data が位置・子・名前を保持することを検証する。
    #[test]
    fn to_data_roundtrip_preserves_transform_and_children() {
        let mut world = World::new();
        let mut parent = make_actor(&mut world, "Parent", 0, [1.0, 2.0, 3.0]);
        let child = make_actor(&mut world, "Child", 0, [4.0, 5.0, 6.0]);
        parent.add_child(child);

        // スナップショット相当のシリアライズ。
        let data = parent.to_data(&world);
        assert_eq!(data.name, "Parent");
        assert_eq!(data.transform.as_ref().unwrap().position, [1.0, 2.0, 3.0]);
        assert_eq!(data.children.len(), 1);
        assert_eq!(data.children[0].name, "Child");
        assert_eq!(
            data.children[0].transform.as_ref().unwrap().position,
            [4.0, 5.0, 6.0],
        );

        // メモリ内スナップショットは serde を通さないが、ActorData の忠実性を
        // JSON 往復でも確認しておく（データ損失が無いこと）。
        let json = serde_json::to_string(&data).unwrap();
        let back: crate::engine::structs::objects::actor::ActorData =
            serde_json::from_str(&json).unwrap();
        assert_eq!(
            back.children[0].transform.as_ref().unwrap().position,
            [4.0, 5.0, 6.0],
        );
    }
}
