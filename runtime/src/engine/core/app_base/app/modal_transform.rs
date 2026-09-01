// ============================================================
//  modal_transform.rs — Blender 風モーダルトランスフォームの App 統合
//
//  【責務】
//  - G / R / S でのモーダル開始条件の判定とスナップショット取得
//  - モーダル中のマウス移動 → デルタ行列 → シーンへの書き戻し
//  - 確定（左クリック / Enter）と取消（右クリック / Esc）
//  - 軸拘束線のライン頂点生成（X=赤 / Y=緑 / Z=青）
//
//  【設計方針】
//  数値計算そのものは modal_transform_state.rs の純関数に置き、
//  ここでは「カメラ・選択状態・シーン」との接続だけを行う。
//
//  【既存ギズモ機構の再利用】
//  書き戻し（MC インスタンス行列・ActorTransform・子孫アクタ・マルチ選択）と
//  Undo 記録は、ギズモドラッグとまったく同じ経路を通す。
//    - 開始: collect_transform_drag_starts()（drag_handler.rs）
//    - 適用: apply_gizmo_new_mat()（drag_handler.rs）
//    - 確定: finish_gizmo_drag_and_record()（drag_handler.rs）
//  そのため `drag.gizmo_drag` にダミーの GizmoDrag を積み、
//  start_mat には「ピボットを平行移動成分に持つ単位行列」を入れる
//  （apply 側は new_mat * inv(start_mat) をデルタとして使うため、
//    ここで組み立てたデルタ行列がそのまま伝わる）。
// ============================================================

use winit::keyboard::KeyCode;

use crate::engine::core::app_base::ipc::ToolMode;
use crate::engine::methods::drawer::LineBatch;
use crate::engine::methods::gizmo_interact::{GizmoDrag, GizmoPart, mat4x4_mul};

use super::modal_transform_state::{
    FINE_SENSITIVITY, ModalAxis, ModalKind, ModalTransform, dot3, normalize3, ray_line_closest_t,
    ray_plane_intersect, screen_angle, screen_distance,
};
use super::{App, RuntimeMode, world_to_screen};

/// 軸拘束線をピボットから左右に伸ばす長さ（ワールド単位）。
/// カメラ距離に比例させるとズームに依らず画面上でほぼ同じ長さに見える。
const AXIS_LINE_LENGTH_RATIO: f32 = 40.0;

/// 軸拘束線の最小長（ピボットにカメラが極端に近い場合の下限）。
const AXIS_LINE_MIN_LENGTH: f32 = 5.0;

impl App {
    // ============================================================
    //  状態の問い合わせ
    // ============================================================

    /// モーダルトランスフォームが進行中か。
    ///
    /// 進行中は通常の選択クリック・ギズモドラッグ・カメラ操作・
    /// ツールホットキーをすべて無効化する（呼び出し側で本関数を見る）。
    pub(super) fn modal_transform_active(&self) -> bool {
        self.modal_transform.is_some()
    }

    /// モーダルの進行状態をエディタへ通知する（`MODAL_STATE:1` / `MODAL_STATE:0`）。
    ///
    /// 【なぜ必要か】
    /// 埋め込み Edit モードではキーボードフォーカスがエディタ側にあり、
    /// キー入力はエディタのフックが拾って IPC で転送してくる。
    /// エディタは「モーダル中は X/Y/Z・Enter・Esc をモーダルへ回し、
    /// それ以外の既定動作（Esc=削除ダイアログ等）を抑止する」判断が要るため、
    /// 進行状態を共有する。
    pub(super) fn send_modal_transform_state(&self, active: bool) {
        if let Some(ipc) = &self.ipc {
            ipc.send(if active { "MODAL_STATE:1" } else { "MODAL_STATE:0" });
        }
    }

    /// モーダル中の感度倍率（Shift 押下中は 1/10 の微調整）。
    ///
    /// Shift 状態は「ランタイム直接のキーイベント」と
    /// 「エディタが転送するカメラキー（CAM_KEY_DOWN:SHIFT）」の
    /// どちらから来ても拾えるよう、両方を見る。
    fn modal_sensitivity(&self) -> f32 {
        if self.shift_held || self.cam_input.shift {
            FINE_SENSITIVITY
        } else {
            1.0
        }
    }

    // ============================================================
    //  開始
    // ============================================================

    /// モーダルトランスフォームの開始を試みる。開始できたら `true`。
    ///
    /// 開始条件:
    /// - Edit モード（または Play の一時停止中）
    /// - 3D ワールドビュー（2D シーンビュー・アクター編集タブは対象外）
    /// - アクタが 1 つ以上選択されている（プライマリが 2D アクタでない）
    /// - ギズモドラッグ・カメラ操作・制御点選択が進行中でない
    /// - カーソルがビューポート内にあり、ピボットが画面に投影できる
    pub(super) fn try_begin_modal_transform(&mut self, kind: ModalKind) -> bool {
        // ── 前提条件 ──────────────────────────────────────────
        if self.modal_transform.is_some() {
            return false;
        }
        // ロジック配置モードとは排他（モード中はクリックもキーも配置側が消費する）。
        if self.placement_mode_active() {
            return false;
        }
        if !(self.mode == RuntimeMode::Edit || self.paused) {
            return false;
        }
        // 2D 側（2D シーンビュー / アクター編集・キャンバス編集タブ）は対象外
        if self.edit_view_is_2d() || self.actor_edit_canvas_wls.contains(&self.active_world_line) {
            return false;
        }
        if self.selected_primary_actor_is_2d() {
            return false;
        }
        // 進行中の他操作とは排他
        if self.drag.gizmo_drag.is_some()
            || self.drag.lmb_held
            || self.drag.rect_selecting
            || self.selected_control_point.is_some()
            || self.cam_input.rmb
            || self.cam_input.mmb
        {
            return false;
        }
        // 編集時物理タイムラインで過去フレーム表示中は編集不可（ギズモと同条件）
        if self.edit_physics_enabled && !self.edit_physics_at_latest {
            return false;
        }
        // 選択がなければ何も動かせない
        if self.selected_actor_dfs_ids.is_empty() && self.actor_virtual_selected_idx.is_none() {
            return false;
        }

        // ── ピボットと画面情報 ────────────────────────────────
        let Some(pivot) = self.current_gizmo_pos() else {
            return false;
        };
        // カーソルがビューポート内にあること（座標が一度も来ていなければ開始しない）
        if self.last_cursor_pos.is_none() {
            return false;
        }
        let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) else {
            return false;
        };
        let vp_w = ws.width as f32;
        let vp_h = ws.height as f32;
        let view = self.camera.view_matrix();
        let proj = self.camera.projection_matrix();
        // 回転角・拡縮距離の中心。カメラ背面にあると投影できないので開始しない。
        let Some(pivot_screen) = world_to_screen(pivot, &view.data, &proj.data, vp_w, vp_h) else {
            return false;
        };

        // カメラ前方向（拘束なし時の移動平面法線 / 回転軸）
        let fwd = self.camera.base.transform.forward();
        let view_forward = normalize3([fwd.x, fwd.y, fwd.z]);
        // ローカル軸拘束用の軸（取得できない場合はワールド軸で代用する）
        let local_axes = self.primary_actor_local_axes().unwrap_or([
            [1.0, 0.0, 0.0],
            [0.0, 1.0, 0.0],
            [0.0, 0.0, 1.0],
        ]);

        let modal = ModalTransform::new(kind, pivot, pivot_screen, view_forward, local_axes);

        // ── ギズモドラッグ機構へダミーのドラッグを積む ────────
        // 書き戻し・Undo をギズモと共通化するための足場。
        // part / tool は書き戻し経路の分岐に使われないが（2D 専用分岐のみ）、
        // 意味が通るよう Center とモーダル種別に対応するツールを入れておく。
        let drag = GizmoDrag {
            part: GizmoPart::Center,
            tool: match kind {
                ModalKind::Move => ToolMode::Move,
                ModalKind::Rotate => ToolMode::Rotate,
                ModalKind::Scale => ToolMode::Scale,
            },
            start_mat: modal.start_mat,
            gizmo_pos: pivot,
            radius: self.editor_3d_gizmo_radius(pivot),
            ref_point: pivot,
            plane_normal: view_forward,
            axes: None,
            full_axes: false,
        };
        // 全選択アクタ分の開始 Transform スナップショットを収集する
        // （ギズモドラッグ開始とまったく同じ収集処理）。
        self.collect_transform_drag_starts();
        self.drag.gizmo_drag = Some(drag);
        self.modal_transform = Some(modal);
        // モーダル中はギズモのホバー表示を消す（掴んでいないことを明示する）
        self.hovered_gizmo_part = None;
        // エディタへモーダル開始を通知する（キー横取りの判断に使う）
        self.send_modal_transform_state(true);
        true
    }

    // ============================================================
    //  更新（マウス移動）
    // ============================================================

    /// モーダル中のカーソル移動を処理してシーンへ反映する。
    ///
    /// マウスの移動量は「前回イベントからの差分」として累積する
    /// （Shift 微調整と軸拘束の切り替えを素直に扱うため）。
    pub(super) fn update_modal_transform(&mut self, cx: f32, cy: f32) {
        if self.modal_transform.is_none() {
            return;
        }
        let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) else {
            return;
        };
        let vp_w = ws.width as f32;
        let vp_h = ws.height as f32;
        // レイ計算は &self 借用なので、modal の可変借用より前に済ませる
        let (ray_o, ray_d) = self.editor_3d_ray(cx, cy, vp_w, vp_h);
        let sensitivity = self.modal_sensitivity();

        let new_mat = {
            let Some(modal) = self.modal_transform.as_mut() else {
                return;
            };
            match modal.kind {
                // ── G: 移動 ──────────────────────────────────
                ModalKind::Move => {
                    if let Some(dir) = modal.constraint_dir() {
                        // 軸拘束あり: マウスレイと拘束軸直線の最近接点でスライドする
                        if let Some(t) = ray_line_closest_t(ray_o, ray_d, modal.pivot, dir) {
                            modal.accumulate_move_axis(t, dir, sensitivity);
                        }
                    } else {
                        // 拘束なし: カメラに正対する平面（ピボットの深度）上で動かす
                        let plane_n = modal.view_forward;
                        if let Some(p) = ray_plane_intersect(ray_o, ray_d, modal.pivot, plane_n) {
                            modal.accumulate_move_plane(p, sensitivity);
                        }
                    }
                }
                // ── R: 回転 ──────────────────────────────────
                ModalKind::Rotate => {
                    let angle = screen_angle(modal.pivot_screen, (cx, cy));
                    // 画面上のマウスの回り方と実際の回転方向を一致させる符号。
                    // 回転軸が画面奥を向く（カメラ前方向と同じ側）なら、
                    // スクリーン角度（Y-down）の増加は右手系回転の負方向にあたる。
                    let axis = modal.rotation_axis();
                    let sign = if dot3(axis, modal.view_forward) >= 0.0 {
                        -1.0
                    } else {
                        1.0
                    };
                    modal.accumulate_rotation(angle, sign, sensitivity);
                }
                // ── S: 拡縮 ──────────────────────────────────
                ModalKind::Scale => {
                    let dist = screen_distance(modal.pivot_screen, (cx, cy));
                    modal.accumulate_scale(dist, sensitivity);
                }
            }
            mat4x4_mul(modal.delta_matrix(), modal.start_mat)
        };

        // ギズモドラッグとまったく同じ書き戻し経路へ流す
        self.apply_gizmo_new_mat(new_mat);
    }

    // ============================================================
    //  外部カーソル座標（エディタ由来）
    // ============================================================

    /// エディタから転送されたカーソル座標（`MODAL:CURSOR:x,y`）でモーダルを更新する。
    ///
    /// 【なぜ必要か】
    /// OS はマウスイベントをカーソル直下のウィンドウにしか配送しない。
    /// そのためカーソルがランタイム子ウィンドウの外（エディタの他パネル上・
    /// 画面外縁）へ出ると `CursorMoved` が途絶え、Blender と違って
    /// トランスフォームの更新が止まってしまう。
    /// エディタはモーダル中だけ低レベルマウスフックでグローバルなカーソル位置を
    /// 追跡し、ビューポートのクライアント座標へ変換して送ってくる。
    ///
    /// 【座標の範囲】
    /// ウィンドウ外を表す負値・幅/高さ超えの座標がそのまま来る。
    /// レイ生成（`screen_to_ray` 系）は NDC への線形変換なのでクランプ不要で、
    /// ビューポート外へも素直に外挿される。ここでも一切クランプしない。
    ///
    /// 【二重適用の防止】
    /// 一度でも外部座標を受け取ったら、以降このモーダルが終わるまで
    /// 自前の `CursorMoved` は採用しない（`accepts_window_cursor()` が false）。
    pub(super) fn on_modal_external_cursor(&mut self, cx: f32, cy: f32) {
        if let Some(modal) = self.modal_transform.as_mut() {
            modal.mark_external_cursor();
        } else {
            // モーダルが終わった後に遅れて届いた座標は捨てる
            return;
        }
        self.update_modal_transform(cx, cy);
    }

    /// ウィンドウ自前の `CursorMoved` をモーダルへ流してよいか。
    ///
    /// 外部カーソル座標源へ切り替わっている間は false（二重適用を防ぐ）。
    pub(super) fn modal_accepts_window_cursor(&self) -> bool {
        self.modal_transform
            .as_ref()
            .map(|m| m.accepts_window_cursor())
            .unwrap_or(true)
    }

    // ============================================================
    //  軸拘束キー
    // ============================================================

    /// モーダル中の軸キー（X/Y/Z）を適用する。
    ///
    /// 押すたびに ワールド → ローカル → 解除 と巡回し、
    /// 累積量はリセットして開始時の姿勢から取り直す。
    pub(super) fn modal_transform_press_axis(&mut self, axis: ModalAxis) {
        let Some(modal) = self.modal_transform.as_mut() else {
            return;
        };
        modal.press_axis(axis);
        let new_mat = mat4x4_mul(modal.delta_matrix(), modal.start_mat);
        // 累積リセット直後は単位デルタ = 開始スナップショットへの復元
        self.apply_gizmo_new_mat(new_mat);
    }

    // ============================================================
    //  確定 / 取消
    // ============================================================

    /// モーダルを確定する（Undo 1 件を記録し、インスペクタへ通知する）。
    pub(super) fn confirm_modal_transform(&mut self) {
        if self.modal_transform.take().is_none() {
            return;
        }
        // ギズモドラッグ終了とまったく同じ記録処理（複数選択も 1 エントリに束ねる）
        self.finish_gizmo_drag_and_record();
        // エディタへモーダル終了を通知する（キー横取りを解除させる）
        self.send_modal_transform_state(false);
    }

    /// モーダルを取消する（開始時スナップショットへ完全復元し、Undo へは積まない）。
    pub(super) fn cancel_modal_transform(&mut self) {
        let Some(modal) = self.modal_transform.take() else {
            return;
        };
        // 単位デルタ（= new_mat が start_mat そのもの）を適用すると、
        // すべての書き戻し先が開始スナップショットの値へ戻る。
        self.apply_gizmo_new_mat(modal.start_mat);

        // Undo へ積まずにドラッグ状態だけ破棄する
        self.drag.gizmo_drag = None;
        self.drag.drag_root_starts.clear();
        self.drag.drag_child_starts.clear();
        self.drag.actor_child_drag_starts.clear();
        self.drag.actor_extra_mc_drag_starts.clear();
        self.drag.multi_actor_drag_starts.clear();
        self.drag.actor_transform_drag_start = None;
        self.drag.canvas_transform_drag_start = None;

        // インスペクタを開始時の値へ戻す（モーダル中は更新していないため）
        if let Some(dfs) = self.actor_virtual_selected_idx {
            self.send_actor_components(dfs as u32, self.actor_virtual_selected_slot_idx);
        }
        // ホバー表示を再評価する
        self.hovered_gizmo_part = self
            .last_cursor_pos
            .and_then(|(cx, cy)| self.compute_gizmo_hover(cx, cy));
        // エディタへモーダル終了を通知する（キー横取りを解除させる）
        self.send_modal_transform_state(false);
    }

    // ============================================================
    //  毎フレームの前提条件チェック
    // ============================================================

    /// モーダルの前提条件（Edit/Pause であること・シーンが存在すること）を点検し、
    /// 崩れていたら **適用も復元もせずに** 静かに破棄する。
    ///
    /// モーダル中に Play 開始やシーン再読み込みが起きると、
    /// 開始スナップショットが指す DFS ID が別のアクタを指しかねない。
    /// その状態で書き戻すと無関係なアクタを壊すため、何もせず捨てる。
    pub(super) fn tick_modal_transform_guard(&mut self) {
        if self.modal_transform.is_none() {
            return;
        }
        let still_valid = (self.mode == RuntimeMode::Edit || self.paused) && self.scene.is_some();
        if still_valid {
            return;
        }
        self.modal_transform = None;
        self.drag.gizmo_drag = None;
        self.drag.drag_root_starts.clear();
        self.drag.drag_child_starts.clear();
        self.drag.actor_child_drag_starts.clear();
        self.drag.actor_extra_mc_drag_starts.clear();
        self.drag.multi_actor_drag_starts.clear();
        self.drag.actor_transform_drag_start = None;
        self.drag.canvas_transform_drag_start = None;
        self.send_modal_transform_state(false);
    }

    // ============================================================
    //  キー入力の受け取り
    // ============================================================

    /// キー押下をモーダルトランスフォームとして処理する。
    ///
    /// 戻り値 `true` = このキーはモーダルが消費したので、
    /// 呼び出し側（on_keyboard_input）は以降の処理を行わない。
    ///
    /// **モーダル中はすべてのキーを飲み込む**（Ctrl+Z 等が
    /// 中途半端な状態のまま走らないようにするため）。
    pub(super) fn handle_modal_transform_key(&mut self, key: KeyCode) -> bool {
        if self.modal_transform.is_some() {
            match key {
                KeyCode::KeyX => self.modal_transform_press_axis(ModalAxis::X),
                KeyCode::KeyY => self.modal_transform_press_axis(ModalAxis::Y),
                KeyCode::KeyZ => self.modal_transform_press_axis(ModalAxis::Z),
                KeyCode::Enter | KeyCode::NumpadEnter => self.confirm_modal_transform(),
                KeyCode::Escape => self.cancel_modal_transform(),
                // それ以外のキーはモーダル中は無効（飲み込むだけ）
                _ => {}
            }
            return true;
        }

        // モーダル開始キー。開始条件を満たさない場合でも G/R/S は
        // モーダル専用に予約しているので、他の処理へは流さない。
        match key {
            KeyCode::KeyG => {
                self.try_begin_modal_transform(ModalKind::Move);
                true
            }
            KeyCode::KeyR => {
                self.try_begin_modal_transform(ModalKind::Rotate);
                true
            }
            KeyCode::KeyS => {
                self.try_begin_modal_transform(ModalKind::Scale);
                true
            }
            _ => false,
        }
    }

    // ============================================================
    //  軸拘束線の描画
    // ============================================================

    /// 軸拘束中の拘束軸を示すラインバッチを組む（拘束なし・非モーダル時は None）。
    ///
    /// ピボットを中心に拘束軸方向へ線を伸ばす。色は X=赤 / Y=緑 / Z=青。
    pub(super) fn build_modal_axis_line_batch(&self) -> Option<LineBatch> {
        let modal = self.modal_transform.as_ref()?;
        let constraint = modal.constraint?;
        let dir = modal.constraint_dir()?;
        // カメラ距離に比例した長さ（ズームしても画面上の長さがほぼ一定になる）
        let cam = self.camera.base.transform.position;
        let d = [
            modal.pivot[0] - cam.x,
            modal.pivot[1] - cam.y,
            modal.pivot[2] - cam.z,
        ];
        let len = (dot3(d, d).sqrt() * AXIS_LINE_LENGTH_RATIO / 100.0).max(AXIS_LINE_MIN_LENGTH);
        let from = [
            modal.pivot[0] - dir[0] * len,
            modal.pivot[1] - dir[1] * len,
            modal.pivot[2] - dir[2] * len,
        ];
        let to = [
            modal.pivot[0] + dir[0] * len,
            modal.pivot[1] + dir[1] * len,
            modal.pivot[2] + dir[2] * len,
        ];
        let mut lb = LineBatch::new();
        lb.add_line(from, to, constraint.axis.line_color());
        Some(lb)
    }
}
