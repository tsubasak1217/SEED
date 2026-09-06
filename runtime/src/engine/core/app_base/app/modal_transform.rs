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
//  【3D と 2D の 2 経路】
//  3D（Transform アクタ）に加え、2D（CanvasTransform アクタ）でも動く。
//  2D 編集のギズモ・ピック・ドラッグはすべて `screen_to_ray_ortho` が作る
//  「キャンバス px 空間」（+X = 画面右 / +Y = 画面下 / +Z = 画面奥）で計算されており、
//  この空間は 3D ワールドと同じ 3 次元アフィン空間として扱える。
//  そのため移動・回転・拡縮の数学（レイ×平面 / レイ×直線 / スクリーン角度 /
//  スクリーン距離比）は**まったく同じものを流用**でき、differ するのは 4 点だけ:
//    1. レイの作り方（2D ortho か 3D カメラか）
//    2. ビュー法線（2D は常にキャンバス法線 +Z）
//    3. 回転の符号（2D は「画面上の時計回り = 正」でそのまま一致する）
//    4. 軸拘束の可否（2D は X/Y のみ。R 中は軸拘束なし）
//  書き戻し先の分岐（apply_gizmo_new_mat の 2D パス）は
//  `App::effective_canvas_tool_mode()` がモーダル種別を返すことで切り替わる。
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
use crate::engine::methods::gizmo_interact::{
    GizmoDrag, GizmoPart, mat4x4_mul, screen_to_ray_ortho,
};

use super::canvas_gizmo_basis::{canvas_gizmo_axes_from_rot, canvas_gizmo_axes_world};
use super::modal_transform_state::{
    FINE_SENSITIVITY, ModalAxis, ModalAxisSpace, ModalKind, ModalTransform, NUMERIC_DOT,
    NUMERIC_SIGN, ROTATION_SIGN_2D, canvas_px_to_screen, dot3, normalize3, ray_line_closest_t,
    ray_plane_intersect, screen_angle, screen_distance,
};
use super::{App, RuntimeMode, world_to_screen};

/// 軸拘束線をピボットから左右に伸ばす長さ（ワールド単位）。
/// カメラ距離に比例させるとズームに依らず画面上でほぼ同じ長さに見える。
const AXIS_LINE_LENGTH_RATIO: f32 = 40.0;

/// 軸拘束線の最小長（ピボットにカメラが極端に近い場合の下限）。
const AXIS_LINE_MIN_LENGTH: f32 = 5.0;

/// 2D キャンバス編集での軸拘束線の長さ（ortho 半高に対する倍率）。
/// ビュー高さの数倍にすることで、拘束線が常に画面を貫いて見える。
const AXIS_LINE_2D_HALF_H_RATIO: f32 = 4.0;

/// 2D カメラ情報が無い世界線のフォールバック ortho 半高。
/// ギズモのヒットテスト（gizmo_handler）と同じ既定値を使う。
const CANVAS_FALLBACK_ORTHO_HALF_H: f32 = 10.0;

/// 2D キャンバス編集モーダルが使うビュー情報（2D ortho カメラのパンとズーム）。
///
/// スクリーン座標 ↔ キャンバス px 空間の相互変換に必要な最小限の値だけを持つ。
/// ギズモドラッグ（drag_handler / gizmo_handler）が `screen_to_ray_ortho` へ
/// 渡している値とまったく同じものを、同じ条件で組み立てる。
#[derive(Debug, Clone, Copy)]
pub(super) struct CanvasModalView {
    pub pan_x: f32,
    pub pan_y: f32,
    pub half_w: f32,
    pub half_h: f32,
    pub vp_w: f32,
    pub vp_h: f32,
}

/// モーダル中の数値入力キーを 1 文字へ写す（該当しないキーは `None`）。
///
/// 数字はメイン列とテンキーの両方、小数点は `.` と テンキー `.`、
/// 符号は `-` と テンキー `-` を受け付ける（Blender と同じ）。
/// 単体起動 / インプレース Play のようにランタイムが直接キーを受け取る
/// 経路で使う。エディタ埋め込み時は同じ文字が IPC (`MODAL:NUM:{c}`) で届く。
fn modal_numeric_char_from_keycode(key: KeyCode) -> Option<char> {
    let c = match key {
        KeyCode::Digit0 | KeyCode::Numpad0 => '0',
        KeyCode::Digit1 | KeyCode::Numpad1 => '1',
        KeyCode::Digit2 | KeyCode::Numpad2 => '2',
        KeyCode::Digit3 | KeyCode::Numpad3 => '3',
        KeyCode::Digit4 | KeyCode::Numpad4 => '4',
        KeyCode::Digit5 | KeyCode::Numpad5 => '5',
        KeyCode::Digit6 | KeyCode::Numpad6 => '6',
        KeyCode::Digit7 | KeyCode::Numpad7 => '7',
        KeyCode::Digit8 | KeyCode::Numpad8 => '8',
        KeyCode::Digit9 | KeyCode::Numpad9 => '9',
        KeyCode::Period | KeyCode::NumpadDecimal => NUMERIC_DOT,
        KeyCode::Minus | KeyCode::NumpadSubtract => NUMERIC_SIGN,
        _ => return None,
    };
    Some(c)
}

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

    /// 2D キャンバス編集モーダルのビュー情報を返す（2D で扱えない場合は None）。
    ///
    /// 【Some になる条件】
    /// 選択プライマリが 2D アクターで、かつ「2D ortho レイで操作する表示状態」
    /// （= ギズモドラッグの `use_ss_drag` と同一条件）であること。
    /// 具体的には 2D シーンビュー（View2D は `canvas_screen_space_overlay` を
    /// 立てる）・アクター編集/キャンバス編集タブ・Play 中・SS オーバーレイ ON。
    ///
    /// ワールドスペース表示中の 2D アクター（3D パースカメラで描かれる）は
    /// None を返す。この状態はギズモドラッグも 3D レイ経路になるため、
    /// モーダルも従来どおり開始しない。
    pub(super) fn canvas_modal_view(&self) -> Option<CanvasModalView> {
        if !self.selected_primary_actor_is_2d() {
            return None;
        }
        let wl = self.active_world_line;
        let in_editor = self.mode == RuntimeMode::Edit || self.paused;
        let use_ss = self.canvas_screen_space_overlay
            || !in_editor
            || self.actor_edit_canvas_wls.contains(&wl);
        if !use_ss {
            return None;
        }
        let ws = self.window.as_ref()?.inner_size();
        let (vp_w, vp_h) = (ws.width as f32, ws.height as f32);
        if vp_w <= 0.0 || vp_h <= 0.0 {
            return None;
        }
        let cam_2d = self.canvas_cameras.get(&wl);
        let pan_x = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
        let pan_y = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
        let half_h = cam_2d
            .map(|c| c.ortho_half_h)
            .unwrap_or(CANVAS_FALLBACK_ORTHO_HALF_H);
        Some(CanvasModalView {
            pan_x,
            pan_y,
            half_w: half_h * (vp_w / vp_h),
            half_h,
            vp_w,
            vp_h,
        })
    }

    /// 選択プライマリ 2D アクターのローカル軸基底（累積ワールド回転に沿った基底）。
    ///
    /// ツールバーの World/Local トグル（`canvas_gizmo_axes_2d`）とは独立に、
    /// **常にアクター自身の回転**から作る。モーダルの軸拘束は
    /// 「1 回押す = ワールド軸 / 2 回押す = ローカル軸」という Blender 規約であり、
    /// ツールバーの状態に左右されてはならないため。
    fn canvas_modal_local_axes(&self) -> [[f32; 3]; 3] {
        canvas_gizmo_axes_from_rot(self.selected_canvas_world_rot_rad().unwrap_or(0.0))
    }

    /// キャンバス（2D）書き戻しで使う実効ツールモード。
    ///
    /// `apply_gizmo_new_mat` の 2D 分岐は「移動 / 回転 / 拡縮」をツールバーの
    /// `tool_mode` で切り替えるが、モーダル中はツールバーではなく
    /// **モーダル種別（G/R/S）** が変形の種類を決める。
    /// 3D 分岐はデルタ行列をそのまま適用するためこの区別が要らない。
    pub(super) fn effective_canvas_tool_mode(&self) -> ToolMode {
        match self.modal_transform.as_ref().map(|m| m.kind) {
            Some(ModalKind::Move) => ToolMode::Move,
            Some(ModalKind::Rotate) => ToolMode::Rotate,
            Some(ModalKind::Scale) => ToolMode::Scale,
            None => self.tool_mode,
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
    /// - アクタが 1 つ以上選択されている
    /// - ギズモドラッグ・カメラ操作・制御点選択が進行中でない
    /// - カーソルがビューポート内にあり、ピボットが画面に投影できる
    /// - ビューモードによるギズモ抑制（`gizmo_suppressed_by_edit_view`）が掛かっていない
    ///
    /// # 3D と 2D の 2 経路
    /// プライマリ選択が 2D アクタで、かつ 2D ortho 表示（`canvas_modal_view` が Some）
    /// なら **キャンバス px 空間**のモーダルとして開始する。
    /// 2D ortho レイはキャンバス px 座標をそのまま返すため、
    /// 3D 用の数学（レイ×平面 / レイ×直線 / スクリーン角度 / スクリーン距離比）が
    /// そのまま流用できる。異なるのはレイの作り方・ビュー法線・回転符号だけ。
    ///
    /// # マルチ選択・2D3D 混在
    /// ピボットは 3D と同じく全選択アクタの重心（`current_gizmo_pos`）だが、
    /// **書き戻しはプライマリ 1 体のみ**に効く。これは 2D ギズモドラッグの
    /// 既存挙動と同一で（`apply_gizmo_new_mat` の 2D 分岐が
    /// `canvas_transform_drag_start` だけを見るため）、
    /// 2D/3D 混在選択でもプライマリの種別の経路しか動かない。
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
        // 2D キャンバス編集の文脈かどうかを判定する。
        // 2D 文脈なのに 2D ビュー情報が組めない（＝ワールドスペース表示の 2D アクタ等、
        // ギズモドラッグも 2D ortho レイを使わない状態）ときは従来どおり開始しない。
        let canvas_view = self.canvas_modal_view();
        let in_2d_context = self.edit_view_is_2d()
            || self.actor_edit_canvas_wls.contains(&self.active_world_line)
            || self.selected_primary_actor_is_2d();
        if in_2d_context && canvas_view.is_none() {
            return false;
        }
        // ビューモードによるギズモ抑制（3D ビューでの SS キャンバス・
        // ビューポートのルートキャンバス等）はモーダルにも等しく効かせる。
        if self.gizmo_suppressed_by_edit_view() {
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

        // 回転角・拡縮距離の中心となるピボットのスクリーン座標と、
        // 拘束なし時の平面法線 / 回転軸、ローカル軸拘束用の基底を求める。
        let (pivot_screen, view_forward, local_axes) = if let Some(v) = canvas_view {
            // 2D: ピボットはキャンバス px 座標。相似変換でスクリーンへ写す。
            // ビュー法線はキャンバス法線 +Z（画面奥）で固定。
            let ps = canvas_px_to_screen(
                [pivot[0], pivot[1]],
                [v.pan_x, v.pan_y],
                [v.half_w, v.half_h],
                [v.vp_w, v.vp_h],
            );
            (ps, [0.0f32, 0.0, 1.0], self.canvas_modal_local_axes())
        } else {
            let view = self.camera.view_matrix();
            let proj = self.camera.projection_matrix();
            // カメラ背面にあると投影できないので開始しない。
            let Some(ps) = world_to_screen(pivot, &view.data, &proj.data, vp_w, vp_h) else {
                return false;
            };
            let fwd = self.camera.base.transform.forward();
            // ローカル軸拘束用の軸（取得できない場合はワールド軸で代用する）
            let axes = self.primary_actor_local_axes().unwrap_or([
                [1.0, 0.0, 0.0],
                [0.0, 1.0, 0.0],
                [0.0, 0.0, 1.0],
            ]);
            (ps, normalize3([fwd.x, fwd.y, fwd.z]), axes)
        };

        let modal = ModalTransform::new(kind, pivot, pivot_screen, view_forward, local_axes);
        let modal = if canvas_view.is_some() {
            modal.into_2d()
        } else {
            modal
        };

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
            // 2D の拡縮書き戻し（apply_gizmo_new_mat）は `drag.axes` の方向で
            // スケール係数を取り出す。開始時はツールバーの World/Local トグルに
            // 合わせておき、軸拘束キーを押したら拘束の座標系へ同期させる
            // （modal_transform_press_axis を参照）。
            axes: if canvas_view.is_some() {
                self.canvas_gizmo_axes_2d()
            } else {
                None
            },
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
        // レイ計算は &self 借用なので、modal の可変借用より前に済ませる。
        // 2D モーダルはギズモドラッグ（drag_handler）とまったく同じ 2D ortho レイを使う。
        // このレイの原点 XY はカーソル直下のキャンバス px 座標そのもので、
        // 方向は常に [0, 0, 1]（画面奥）である。
        let is_2d_modal = self
            .modal_transform
            .as_ref()
            .is_some_and(|m| m.is_2d);
        let (ray_o, ray_d) = if is_2d_modal {
            let Some(v) = self.canvas_modal_view() else {
                // 表示状態が 2D ortho でなくなった（タブ切替等）→ 今回の更新は捨てる
                return;
            };
            screen_to_ray_ortho(cx, cy, vp_w, vp_h, v.pan_x, v.pan_y, v.half_w, v.half_h)
        } else {
            self.editor_3d_ray(cx, cy, vp_w, vp_h)
        };
        // 数値入力中はマウスで値を動かさない。ただし「前回参照値」の追跡は
        // 続ける必要があるため、感度 0 で累積器を空回しする。
        // （追跡を止めると、Backspace で数値を消してマウス駆動へ戻った瞬間に
        //   その間のカーソル移動量がまとめて一気に載ってしまう）
        let sensitivity = if self
            .modal_transform
            .as_ref()
            .is_some_and(|m| m.numeric_active())
        {
            0.0
        } else {
            self.modal_sensitivity()
        };

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
                    let sign = if modal.is_2d {
                        // 2D: キャンバス px 空間は +Y = 画面下で、スクリーン座標とは
                        // 反転のない相似変換で結ばれる。よって「マウスの時計回り =
                        // 正の回転（= 正の CanvasTransform.rotation）」が既に一致する。
                        ROTATION_SIGN_2D
                    } else if dot3(modal.rotation_axis(), modal.view_forward) >= 0.0 {
                        // 3D: 回転軸が画面奥を向く（カメラ前方向と同じ側）なら、
                        // スクリーン角度（Y-down）の増加は右手系回転の負方向にあたる。
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
        // 2D の拡縮は「拘束の座標系」と「スケール係数を取り出す基底」が
        // 一致していなければならない（drag.axes で取り出すため）。
        // 例: Local 拘束なのに World 基底で取り出すと、回転したアクタで
        // 倍率が cos 成分ぶん目減りする。ここで両者を同期させる。
        let axes_2d = if modal.is_2d && modal.kind == ModalKind::Scale {
            Some(match modal.constraint.map(|c| c.space) {
                Some(ModalAxisSpace::World) => Some(canvas_gizmo_axes_world()),
                Some(ModalAxisSpace::Local) => Some(modal.local_axes),
                // 拘束なし（均一拡縮）は基底に依らず同じ倍率になるため
                // ツールバーの表示基底へ戻しておく
                None => None,
            })
        } else {
            None
        };
        if let Some(axes) = axes_2d {
            let axes = axes.or_else(|| self.canvas_gizmo_axes_2d());
            if let Some(drag) = self.drag.gizmo_drag.as_mut() {
                drag.axes = axes;
            }
        }
        // 累積リセット直後は単位デルタ = 開始スナップショットへの復元
        self.apply_gizmo_new_mat(new_mat);
    }

    // ============================================================
    //  数値入力
    // ============================================================

    /// モーダル中の数値入力（数字 / 小数点 / 符号）を 1 文字適用する。
    ///
    /// 適用できたら即座にプレビューを更新する（Blender と同じく
    /// 打ち込んだ瞬間に結果が見える）。
    pub(super) fn modal_transform_numeric_char(&mut self, c: char) {
        let Some(modal) = self.modal_transform.as_mut() else {
            return;
        };
        if !modal.apply_numeric_char(c) {
            return;
        }
        let new_mat = mat4x4_mul(modal.delta_matrix(), modal.start_mat);
        self.apply_gizmo_new_mat(new_mat);
    }

    /// モーダル中の数値入力を 1 文字削除する（Backspace）。
    ///
    /// 全部消えるとバッファが空になり、以降はマウス駆動へ戻る
    /// （`delta_matrix()` が累積量ベースへ切り替わる）。
    pub(super) fn modal_transform_numeric_backspace(&mut self) {
        let Some(modal) = self.modal_transform.as_mut() else {
            return;
        };
        if !modal.numeric_backspace() {
            return;
        }
        let new_mat = mat4x4_mul(modal.delta_matrix(), modal.start_mat);
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
        let Some(start_mat) = self.modal_transform.as_ref().map(|m| m.start_mat) else {
            return;
        };
        // 単位デルタ（= new_mat が start_mat そのもの）を適用すると、
        // すべての書き戻し先が開始スナップショットの値へ戻る。
        //
        // 【モーダル状態を落とす前に適用する理由】
        // 2D（CanvasTransform）の書き戻しは `effective_canvas_tool_mode()` を見て
        // 移動 / 回転 / 拡縮のどれとして書くかを決める。先に `take()` してしまうと
        // ツールバーの `tool_mode` で復元することになり、種別が食い違う。
        self.apply_gizmo_new_mat(start_mat);
        self.modal_transform = None;

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
                KeyCode::Backspace => self.modal_transform_numeric_backspace(),
                // 数値入力（数字 / 小数点 / 符号）。テンキーも同じ扱い。
                other => {
                    if let Some(c) = modal_numeric_char_from_keycode(other) {
                        self.modal_transform_numeric_char(c);
                    }
                    // それ以外のキーはモーダル中は無効（飲み込むだけ）
                }
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
        // 拘束線の長さ。
        // - 2D: ortho 半高の定数倍（キャンバス px 空間。ズームに追従して画面を貫く）
        // - 3D: カメラ距離に比例（ズームしても画面上の長さがほぼ一定になる）
        let len = if modal.is_2d {
            let half_h = self
                .canvas_modal_view()
                .map(|v| v.half_h)
                .unwrap_or(CANVAS_FALLBACK_ORTHO_HALF_H);
            (half_h * AXIS_LINE_2D_HALF_H_RATIO).max(AXIS_LINE_MIN_LENGTH)
        } else {
            let cam = self.camera.base.transform.position;
            let d = [
                modal.pivot[0] - cam.x,
                modal.pivot[1] - cam.y,
                modal.pivot[2] - cam.z,
            ];
            (dot3(d, d).sqrt() * AXIS_LINE_LENGTH_RATIO / 100.0).max(AXIS_LINE_MIN_LENGTH)
        };
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
