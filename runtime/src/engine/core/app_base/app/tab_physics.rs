// ============================================================
//  tab_physics.rs — 編集時物理シミュレーションのタブごと状態保持
//
//  【責務】
//    ビュータブ（3Dシーン / 2Dシーン）とアクター編集タブ（world_line）ごとに、
//    物理タイムライン状態と Dynamic ボディの速度を退避・復元する。
//    これによりタブを切り替えて戻ってきたとき、位置（ECS が world_line ごとに保持）
//    に加えて「速度」も復元し、シミュレーションの「続き」から再開できる。
//
//  【なぜ速度だけ別管理か】
//    位置・回転は ECS（world_line ごと）に保持されるため失われない。
//    タイムライン UI 状態（スナップショット・現在フレーム等）は App の単一
//    フィールドで保持され、タブをまたぐと上書きされる。
//    Rapier の linvel/angvel は物理スレッドを停止（stop_physics）すると失われる。
//    そこで「タイムライン状態」＋「速度キャッシュ」を TabPhysicsState に退避し、
//    復帰時に start_physics の初速として速度を積み直す。
//
//  【エントリポイント】
//    leave_current_tab_physics(): タブ離脱直前（状態差し替え前）に呼ぶ。
//    enter_tab_physics():          タブ進入直後（状態差し替え後）に呼ぶ。
//    どちらも SetEditViewMode / SetActiveWorldLine ハンドラのフックから呼ばれる。
// ============================================================

use std::collections::HashMap;
use crate::engine::ecs::Entity;
use super::App;
use super::physics_timeline::PhysicsSnapshot;

// ─── タブ識別キー ────────────────────────────────────────────────────────────

/// 編集タブを一意に識別するキー。
///
/// `world_line`: アクター編集タブは world_line で一意（>0）。シーンは 0。
/// `view_2d`: シーン（world_line=0）における 3Dシーン/2Dシーンビューの区別。
///   `edit_view_is_2d()` は world_line>0 では常に false を返すため、
///   アクター編集タブどうしは world_line だけで分離され、view_2d は常に false になる。
///   シーン（world_line=0）だけが view_2d の true/false で 2 タブに分かれる。
#[derive(Clone, Copy, PartialEq, Eq, Hash)]
pub struct TabKey {
    world_line: u32,
    view_2d:    bool,
}

// ─── 退避する物理状態 ────────────────────────────────────────────────────────

/// タブ離脱時に退避し、復帰時に復元する物理状態。
///
/// 位置・回転（ECS）は含まない（world_line ごとに ECS が保持するため）。
/// タイムライン UI 状態と Dynamic ボディ速度のみを保持する。
pub struct TabPhysicsState {
    /// フレームスナップショット列
    snapshots:     Vec<PhysicsSnapshot>,
    /// 現在表示フレーム
    current_frame: usize,
    /// 累積シミュレーション時間（秒）
    sim_time:      f64,
    /// 停止中フラグ
    paused:        bool,
    /// 最新フレーム停止フラグ
    at_latest:     bool,
    /// 3D Dynamic ボディ速度（ECS Entity → (linvel, angvel)）
    vel_3d:        HashMap<Entity, ([f32; 3], [f32; 3])>,
    /// 2D Dynamic ボディ速度（ECS Entity → (linvel, angvel スカラー)）
    vel_2d:        HashMap<Entity, ([f32; 2], f32)>,
}

impl App {
    // ─── タブキー ──────────────────────────────────────────────────────────

    /// 現在アクティブなタブのキーを返す。
    pub(super) fn current_tab_key(&self) -> TabKey {
        TabKey {
            world_line: self.active_world_line,
            view_2d:    self.edit_view_is_2d(),
        }
    }

    // ─── 離脱 ──────────────────────────────────────────────────────────────

    /// 現在タブの物理状態を退避してから物理スレッドを停止する。
    ///
    /// タブ切替（SetEditViewMode / SetActiveWorldLine）で「別タブへ移る」ときに、
    /// 状態差し替えの前（current_tab_key が旧タブを指すうち）に呼ぶ。
    /// 編集時物理が無効なら何もしない（切替が物理に触れないようにする）。
    pub(super) fn leave_current_tab_physics(&mut self) {
        if !(self.edit_physics_enabled || self.edit_physics_2d_enabled) { return; }

        // 状態差し替え前のキーで退避する
        let key = self.current_tab_key();
        let state = TabPhysicsState {
            snapshots:     std::mem::take(&mut self.edit_physics_snapshots),
            current_frame: self.edit_physics_current_frame,
            sim_time:      self.edit_physics_sim_time,
            paused:        self.edit_physics_paused,
            at_latest:     self.edit_physics_at_latest,
            vel_3d:        std::mem::take(&mut self.current_vel_cache_3d),
            vel_2d:        std::mem::take(&mut self.current_vel_cache_2d),
        };
        self.tab_physics.insert(key, state);

        // 物理スレッドを停止し、接触色・ドラッグ登録・プレイバック状態を掃除する。
        self.stop_physics();
        self.stop_physics_2d();
        self.active_collision_dfs_ids.clear();
        self.active_collision_2d_dfs_ids.clear();
        // 念のため: タブをまたいだドラッグ登録の取り残しを防ぐ
        self.dragging_physics_entity_id    = None;
        self.dragging_physics_2d_entity_id = None;
        // プレイバック/シーク中の離脱でもフラグを落とし、復帰は停止フレーム表示から始める
        self.edit_physics_in_playback     = false;
        // タイムラインの一時状態をクリーンにしておく（未保存タブへ進んだ場合の初期値）
        self.edit_physics_current_frame   = 0;
        self.edit_physics_sim_time        = 0.0;
        self.edit_physics_no_change_count = 0;
        self.edit_physics_warmup_frames   = 0;
    }

    // ─── 進入 ──────────────────────────────────────────────────────────────

    /// 移動先タブの物理状態を復元して物理スレッドを再起動する。
    ///
    /// タブ切替で「別タブへ移った」あと、状態差し替えの後（current_tab_key が
    /// 新タブを指すようになってから）に呼ぶ。
    /// 保存済み状態があれば「続き」（位置＋速度）を復元して一時停止状態で復帰し、
    /// 無ければ現状態を初期フレームとして新規初期化する。
    pub(super) fn enter_tab_physics(&mut self) {
        if !(self.edit_physics_enabled || self.edit_physics_2d_enabled) { return; }

        let key = self.current_tab_key();

        // 常時押し戻しモード（RigidBody 無効）はタイムライン・速度を使わず物理を
        // 毎フレーム走らせる。保存状態（あれば空）に関わらず押し戻しで初期化し、
        // Pause は送らない（送ると押し戻しが止まる）。
        if self.is_edit_physics_pushback_mode() {
            self.tab_physics.remove(&key);
            self.start_physics();
            self.start_physics_2d();
            self.enter_edit_physics_pushback();
            return;
        }

        if let Some(st) = self.tab_physics.remove(&key) {
            // ── 保存済みタブ: 続き（位置＋速度）から一時停止状態で復帰 ──────────
            self.edit_physics_snapshots       = st.snapshots;
            self.edit_physics_current_frame   = st.current_frame;
            self.edit_physics_sim_time        = st.sim_time;
            self.edit_physics_paused          = st.paused;
            self.edit_physics_at_latest       = st.at_latest;
            self.edit_physics_in_playback     = false;
            self.edit_physics_no_change_count = 0;
            self.edit_physics_warmup_frames   = 0;

            // 退避した速度を「次の start で積む初速」として渡す。
            // キャッシュ側にも同値を復元しておくことで、再生前にもう一度タブを
            // 離脱しても速度が失われないようにする（Rapier 側の速度は stop で消えるため）。
            self.current_vel_cache_3d      = st.vel_3d.clone();
            self.current_vel_cache_2d      = st.vel_2d.clone();
            self.pending_restore_vel_3d    = Some(st.vel_3d);
            self.pending_restore_vel_2d    = Some(st.vel_2d);

            self.start_physics();
            self.start_physics_2d();

            // 一回性の初速指定を解除する（他の start 呼び出しは初速なしを保つ）
            self.pending_restore_vel_3d = None;
            self.pending_restore_vel_2d = None;

            // 続き位置＋速度を凍結（戻った直後は必ず一時停止。明示再生で続行）
            self.send_pause_to_physics_threads();
            // C# スライダーへ状態同期
            self.send_physics_timeline_state();
            // 収束停止の残留誤発火防止（実測速度受信まで「移動中」とみなす）
            self.mark_edit_physics_moving();
        } else {
            // ── 未保存タブ: 現状態を初期フレームとして新規初期化 ────────────────
            self.start_physics();
            self.start_physics_2d();
            if self.is_edit_physics_pushback_mode() {
                self.enter_edit_physics_pushback();
            } else {
                self.init_physics_timeline();
            }
        }
    }
}
