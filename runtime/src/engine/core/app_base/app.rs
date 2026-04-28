use std::sync::Arc;
use std::path::Path;

use winit::application::ApplicationHandler;
use winit::event::{DeviceEvent, DeviceId, ElementState, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::keyboard::{KeyCode, PhysicalKey};
use winit::window::{Window, WindowId};

use crate::engine::core::clock::{Clock, FrameContext};
use crate::engine::core::input::Input;
use crate::engine::core::loader::load_model;
use crate::engine::core::renderer::Renderer;
use crate::engine::core::window::{create_window, WindowConfig};
use crate::engine::core::app_base::ipc::{IpcClient, IpcCommand, ToolMode};
use crate::engine::core::app_base::scene::{Scene, DebugCameraData, CanvasCameraData};
use crate::engine::methods::drawer::{
    DrawContext, CameraBuffer, CameraUniform,
    draw_model_indirect, draw_id_pass,
    draw_outline_multi, draw_stencil_mask_multi,
    extract_frustum_planes, IdBuffer, GizmoBatch, draw_gizmo_batch,
    LineBatch, draw_line_batch,
};
use crate::engine::methods::gizmo_interact::{
    GizmoDrag, GizmoPart, screen_to_ray, hit_test_gizmo, start_drag, update_drag,
    mat4x4_mul, mat4x4_inv,
};
use crate::engine::core::app_base::undo::{UndoHistory, TransformCommand, MultiTransformCommand, SceneSnapshotCommand, SelectionCommand, ActorTreeSnapshotCommand, ComponentSlotsSnapshotCommand, ActorTransformCommand, ActorGroupTransformCommand, MultiActorDragTransformCommand, ActorDfsSelectionCommand, CompositeCommand};
use crate::engine::core::app_base::scene::build_actor;
use crate::engine::core::scripting::ScriptingHost;
use crate::engine::ecs::{Entity, World};
use crate::engine::components::{
    ModelComponent, Transform as ActorTransform, ComponentKind, ComponentData,
    ScriptComponent, PlaceholderScriptSlot,
    GroupMeta, GROUP_ID_BASE,
    CanvasTransform, CanvasComponent,
};
use crate::engine::structs::objects::{Actor, DebugCamera};
use crate::engine::structs::objects::actor::{ActorData, ComponentSlotData, ComponentSlot};
use crate::engine::structs::objects::camera::debug_camera::CameraInput;
use crate::engine::structs::tensor::{Vector3, Mat4x4};
use crate::engine::structs::transforms::{Quaternion, Transform};
use crate::engine::structs::utils::Color;

// ============================================================
//  クリップボードアイテム
// ============================================================

struct ClipboardItem {
    name:         String,
    mat:          [[f32; 4]; 4],
    /// clipboard 配列内でのローカル親インデックス（None = ペースト時ルート）
    local_parent: Option<usize>,
    /// コピー元の安定アニメーション位相シード（ペースト時にそのまま引き継ぐ）
    anim_seed:    u32,
}

// ============================================================
//  起動設定
// ============================================================

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum RuntimeMode {
    /// デバッグカメラ・エディタ埋め込み
    Edit,
    /// 通常ゲームプレイ・独立ウィンドウ
    Play,
}

pub struct LaunchArgs {
    pub parent_hwnd: Option<isize>,
    pub mode:        RuntimeMode,
    pub pipe_name:   Option<String>,
}

// ============================================================
//  InspectorTransformDrag — インスペクターフィールドドラッグ Undo 単一化
// ============================================================

enum InspectorTransformDrag {
    Instance   { idx: u32, old_mat: [[f32; 4]; 4] },
    Actor      { wl: u32, dfs_id: u32, old_tf: ActorTransform },
    /// actor edit モード + ModelComponent あり: 全インスタンスと actor.transform の事前スナップショット
    ActorGroup {
        dfs_id: u32,
        old_mats: Vec<[[f32; 4]; 4]>,
        old_tf: ActorTransform,
        /// 子孫アクターの事前スナップショット (child_dfs_id, old_tf, old_mc_mat)
        child_old_states: Vec<(u32, ActorTransform, [[f32; 4]; 4])>,
    },
}

// ============================================================
//  App
// ============================================================

pub struct App {
    window:         Option<Arc<Window>>,
    renderer:       Option<Renderer>,
    input:          Input,
    cam_input:      CameraInput,
    camera:         DebugCamera,
    clock:          Clock,
    draw_ctx:       Option<DrawContext>,
    scene:          Option<Scene>,
    camera_buf:     Option<CameraBuffer>,
    scripting_host: Option<Arc<ScriptingHost>>,

    parent_hwnd: Option<isize>,
    mode:        RuntimeMode,
    ipc:         Option<IpcClient>,
    paused:      bool,

    /// RMB 押下時のスクリーン座標。カーソルロック解除後に復元する。
    cam_grab_screen_pos: Option<(i32, i32)>,

    /// エディタから PLAY_CLAMP:1 を受け取ったとき true。
    /// 毎フレーム ClipCursor を貼り直してカーソルをウィンドウ内に閉じ込める。
    play_clamp: bool,

    // ── ピッキング / ギズモ ──────────────────────────────────
    /// Actor 選択用 ID バッファ（Edit/Pause モードのみ使用）。
    id_buffer:          Option<IdBuffer>,
    /// 現在選択中のインスタンスインデックス（複数選択対応）。
    selected_instances: Vec<u32>,
    /// LMB クリック時のビューポートピクセル座標（次フレームで処理）。
    pending_pick:       Option<(u32, u32)>,
    /// 直前フレームのカーソル座標（ビューポートローカル）。
    last_cursor_pos:    Option<(f32, f32)>,
    /// ギズモ描画用の単位行列モデルバッファ。
    line_model_buf:     Option<(wgpu::Buffer, wgpu::BindGroup)>,
    /// 現在のエディタツールモード。
    tool_mode:          ToolMode,
    /// 進行中のギズモドラッグ状態。
    gizmo_drag:         Option<GizmoDrag>,
    /// マウスホバー中のギズモパーツ（ハイライト表示用）。
    hovered_gizmo_part: Option<GizmoPart>,
    /// Undo/Redo 履歴。
    undo_history:       UndoHistory,
    /// Ctrl キーが押されているか。
    ctrl_held:          bool,
    /// ドラッグ開始時の「ルート選択インスタンス」初期行列（親子フィルタ済み）。
    drag_root_starts:   Vec<(u32, [[f32; 4]; 4])>,
    /// ドラッグ開始時の子孫インスタンス初期行列（ルート以外の追従対象）。
    drag_child_starts:  Vec<(u32, [[f32; 4]; 4])>,
    /// アクター編集モードのギズモドラッグ開始時の子アクター MC 行列 (child_dfs_id, mat)。
    actor_child_drag_starts: Vec<(u32, [[f32; 4]; 4])>,
    /// LMB 押下中フラグ。
    lmb_held:           bool,
    /// LMB 押下時のビューポート座標。
    lmb_press_pos:      Option<(f32, f32)>,
    /// 矩形選択ドラッグ中フラグ。
    rect_selecting:     bool,
    /// LMB 押下時の Ctrl 状態（ピック結果でトグル判定に使用）。
    ctrl_at_press:      bool,
    /// ヒエラルキー更新が保留中（スロットリング用）。
    hierarchy_dirty:     bool,
    /// 最後にヒエラルキーを送信した時刻（スロットリング用）。
    last_hierarchy_send: Option<std::time::Instant>,
    /// コピー&ペースト用クリップボード（アクター編集モード: MC インスタンス単位）。
    clipboard: Vec<ClipboardItem>,
    /// コピー&ペースト用クリップボード（シーンモード: ActorData 単位）。
    actor_clipboard: Vec<ActorData>,
    /// RMB 押下開始座標（短押し判定用）。
    rmb_press_pos: Option<(f32, f32)>,
    /// RMB 押下中にカーソルが閾値以上移動したか。
    rmb_moved: bool,
    /// FIRST_FRAME シグナル送信済みフラグ（デバッグビルド・埋め込みモードのみ使用）。
    first_frame_sent: bool,
    /// 軸ギズモ（エディタモードのみ使用）。
    axis_gizmo: Option<crate::engine::core::font::axis_gizmo::AxisGizmo>,
    /// アイコンオーバーレイ（エディタモードのみ使用）。
    icon_overlay: Option<crate::engine::core::font::icon_overlay::IconOverlay>,
    /// 矩形選択開始時の選択状態（Undo 記録用）。
    selection_before_rect: Vec<u32>,
    /// 矩形選択開始時のアクター DFS 選択状態（Undo 記録用）。
    selection_before_rect_dfs: Vec<usize>,
    selection_before_rect_primary: Option<usize>,
    /// グリッド描画フラグ（エディタモードのみ）。
    show_grid: bool,
    /// 軸ギズモ表示フラグ（エディタモードのみ）。
    show_axis_gizmo: bool,
    /// 仮想アクターノードが選択されているとき Some(dfs_id)（プライマリ選択）。
    /// ModelComponent なしアクターでもアイコン・インスペクターを表示するために使う。
    actor_virtual_selected_idx: Option<usize>,
    /// 複数アクターが選択されているときの全 DFS id リスト（マルチ選択）。
    /// 単一選択時は [primary] の 1 要素、空のとき未選択。
    selected_actor_dfs_ids: Vec<usize>,
    /// 選択中の ModelComponent スロットインデックス（Model スロット内の連番）。
    /// ピッキング時にどの MC スロットが選択されたかを記憶し、アウトライン描画・Inspectorハイライトに使う。
    actor_virtual_selected_slot_idx: usize,
    /// アクタートランスフォームをギズモでドラッグ中に保持する開始状態 (dfs_id, old_transform)。
    actor_transform_drag_start: Option<(u32, ActorTransform)>,
    /// ギズモドラッグ開始時の追加 MC スロット開始行列。
    /// タプル: (slot_i, 全インスタンス開始行列 Vec)
    /// 選択スロット以外の MC を選択スロットと一緒に動かすために使う。
    actor_extra_mc_drag_starts: Vec<(usize, Vec<[[f32; 4]; 4]>)>,
    /// マルチ選択ギズモドラッグ時の非プライマリ選択アクター開始行列（dfs_id, start_mat）。
    multi_actor_drag_starts: Vec<(u32, [[f32; 4]; 4])>,
    /// インスペクターフィールドドラッグ中の事前状態（Undo 1 コマンド化のために使用）。
    inspector_transform_drag: Option<InspectorTransformDrag>,

    // ── 世界線システム ───────────────────────────────────────────
    /// 現在アクティブな世界線 (0=通常シーン, N=アクター編集タブ)。
    /// active_world_line と一致する world_line を持つ Actor のみ描画・操作される。
    active_world_line: u32,
    /// 世界線切り替え時に退避するカメラ状態。キーが世界線番号。
    saved_cameras: std::collections::HashMap<u32, DebugCameraData>,
    /// 2D キャンバスモードの世界線番号セット（Ortho カメラを使用する世界線）。
    /// OpenActor で 2D Actor をロードした世界線がここに登録される。
    canvas_world_lines: std::collections::HashSet<u32>,
    /// 2D キャンバスカメラ状態（世界線番号 → CanvasCameraData）。
    /// pan_x, pan_y, ortho_half_h を保持する。
    canvas_cameras: std::collections::HashMap<u32, CanvasCameraData>,

    // ── ドラッグ&ドロップ ───────────────────────────────────────
    /// DROP_ACTOR コマンドを受け取ったときに設定する。
    /// 次フレームの ID パス後にワールド座標を読み出してアクターを配置する。
    /// タプル: (actor_path, screen_x, screen_y)
    pending_drop: Option<(String, u32, u32)>,
    /// DRAG_HOVER コマンドを受け取ったときに設定する。
    /// 次フレームの ID パスでワールド座標を解決してプレビュー球体位置を更新する。
    /// タプル: (viewport_x, viewport_y)
    pending_drop_hover: Option<(u32, u32)>,
    /// ドラッグ中の配置プレビュー球体を描画するワールド座標。
    /// DragHoverEnd または DROP_ACTOR でクリアされる。
    drop_preview_pos: Option<[f32; 3]>,
}

impl App {
    pub fn new(args: LaunchArgs) -> Self {
        let ipc = args.pipe_name.as_deref()
            .and_then(|name| IpcClient::connect(name).ok());

        let scripting_host = match ScriptingHost::load(&ScriptingHost::resolve_dll_path()) {
            Ok(host) => {
                eprintln!("[SEED] ScriptingHost loaded");
                Some(host)
            }
            Err(e) => {
                eprintln!("[SEED] ScriptingHost load failed (scripting disabled): {e}");
                None
            }
        };

        Self {
            window:         None,
            renderer:       None,
            input:          Input::new(),
            cam_input:      CameraInput::default(),
            camera:         DebugCamera::default(),
            clock:          Clock::new(),
            draw_ctx:       None,
            scene:          None,
            camera_buf:     None,
            scripting_host,
            parent_hwnd: args.parent_hwnd,
            mode:        args.mode,
            ipc,
            paused: false,
            cam_grab_screen_pos: None,
            play_clamp: false,
            id_buffer:          None,
            selected_instances: Vec::new(),
            pending_pick:       None,
            last_cursor_pos:    None,
            line_model_buf:     None,
            tool_mode:          ToolMode::Select,
            gizmo_drag:         None,
            hovered_gizmo_part: None,
            undo_history:       UndoHistory::new(),
            ctrl_held:          false,
            drag_root_starts:   Vec::new(),
            drag_child_starts:  Vec::new(),
            actor_child_drag_starts: Vec::new(),
            lmb_held:           false,
            lmb_press_pos:      None,
            rect_selecting:     false,
            ctrl_at_press:      false,
            hierarchy_dirty:     false,
            last_hierarchy_send: None,
            clipboard:           Vec::new(),
            actor_clipboard:     Vec::new(),
            rmb_press_pos:         None,
            rmb_moved:             false,
            first_frame_sent:      false,
            axis_gizmo:            None,
            icon_overlay:          None,
            selection_before_rect:         Vec::new(),
            selection_before_rect_dfs:     Vec::new(),
            selection_before_rect_primary: None,
            show_grid:       true,
            show_axis_gizmo: true,
            actor_virtual_selected_idx:      None,
            selected_actor_dfs_ids:          Vec::new(),
            actor_virtual_selected_slot_idx: 0,
            actor_transform_drag_start:   None,
            actor_extra_mc_drag_starts:   Vec::new(),
            multi_actor_drag_starts:      Vec::new(),
            inspector_transform_drag:     None,
            active_world_line: 0,
            saved_cameras: std::collections::HashMap::new(),
            canvas_world_lines: std::collections::HashSet::new(),
            canvas_cameras:     std::collections::HashMap::new(),
            pending_drop:       None,
            pending_drop_hover: None,
            drop_preview_pos:   None,
        }
    }

    /// エントリポイント。EventLoop を生成して実行する。
    pub fn run(args: LaunchArgs) {
        let event_loop: EventLoop<()> =
            EventLoop::new().expect("Failed to create event loop");
        event_loop.set_control_flow(ControlFlow::Poll);
        let mut app = App::new(args);
        event_loop.run_app(&mut app).expect("Failed to run app");
    }

    fn is_embedded(&self) -> bool { self.parent_hwnd.is_some() }

    fn window_hwnd(&self) -> isize {
        #[cfg(target_os = "windows")]
        {
            use raw_window_handle::{HasWindowHandle, RawWindowHandle};
            if let Some(win) = &self.window {
                if let Ok(handle) = win.window_handle() {
                    if let RawWindowHandle::Win32(h) = handle.as_raw() {
                        return h.hwnd.get();
                    }
                }
            }
        }
        0
    }

    fn process_ipc(&mut self, event_loop: &ActiveEventLoop) {
        // コマンドを先にすべて収集してから処理する。
        // &self.ipc の不変借用を処理ループ内に持ち込まないことで、
        // apply_delete 等の &mut self メソッド呼び出しを可能にする。
        let cmds: Vec<_> = {
            let Some(ipc) = &self.ipc else { return };
            std::iter::from_fn(|| ipc.try_recv()).collect()
        };
        for cmd in cmds {
            match cmd {
                IpcCommand::CtrlDown           => self.ctrl_held = true,
                IpcCommand::CtrlUp             => self.ctrl_held = false,
                IpcCommand::Pause              => self.paused = true,
                IpcCommand::Resume             => self.paused = false,
                IpcCommand::Stop               => event_loop.exit(),
                IpcCommand::CamKeyDown(k)      => self.cam_input.set_key(&k, true),
                IpcCommand::CamKeyUp(k)        => self.cam_input.set_key(&k, false),
                IpcCommand::SetToolMode(m)     => self.tool_mode = m,
                IpcCommand::PlayClamp(v)       => {
                    self.play_clamp = v;
                    if !v { release_window_clamp(); }
                }
                IpcCommand::Undo => {
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
                            self.sync_anim_seeds();
                            self.send_hierarchy();
                        }
                    }
                }
                IpcCommand::Redo => {
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
                            self.sync_anim_seeds();
                            self.send_hierarchy();
                        }
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
                        // アクターツリーノード選択（シーンモード・アクター編集モード共通）。
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
                    // シーンモード・アクター編集モード共通:
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
                    // インスペクタへプライマリ選択アクターのコンポーネントをプッシュ
                    if let Some(idx) = self.actor_virtual_selected_idx {
                        self.send_actor_components(idx as u32, self.actor_virtual_selected_slot_idx);
                    }
                }
                IpcCommand::Reparent { child, new_parent } => {
                    // シーンモード・アクター編集モード共通: child / new_parent は DFS id として扱い
                    // actor.children ツリー自体を変更する（ECS 統一後の正式パス）
                    self.handle_reparent_actor(child, new_parent);
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
                IpcCommand::SaveActor(path) => {
                    let wl = self.active_world_line;
                    let result: Result<(), String> = (|| {
                        let scene = self.scene.as_ref().ok_or("シーンが読み込まれていません")?;
                        let actor = scene.actors.iter()
                            .find(|a| a.world_line == wl)
                            .ok_or("アクティブ世界線にアクターがありません")?;
                        let data = actor.to_data(&scene.world);
                        let json = serde_json::to_string_pretty(&data).map_err(|e| e.to_string())?;
                        std::fs::write(&path, json).map_err(|e| e.to_string())?;
                        Ok(())
                    })();
                    if let Some(ipc) = &self.ipc {
                        match result {
                            Ok(())   => ipc.send("SAVE_OK"),
                            Err(e)   => ipc.send(&format!("SAVE_ERROR:{e}")),
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
                            self.inspector_transform_drag = Some(InspectorTransformDrag::Instance {
                                idx: target_id, old_mat,
                            });
                        }
                    } else {
                        // actor edit モード: アクターを DFS で探してスナップショットを作成
                        if wl != 0 {
                            let mut c = 0u32;
                            let snapshot = self.scene.as_ref().and_then(|s| {
                                find_actor_by_dfs(&s.actors, wl, target_id, &mut c).map(|actor| {
                                    let old_mats = actor.mc_entity()
                                        .and_then(|e| s.world.get::<ModelComponent>(e))
                                        .map(|mc| mc.instance_mats.clone())
                                        .unwrap_or_default();
                                    let old_tf = s.world.get::<ActorTransform>(actor.entity)
                                        .cloned().unwrap_or_default();
                                    let mut child_old_states = Vec::new();
                                    let mut child_dfs = target_id + 1;
                                    collect_child_actor_old_states(actor, &s.world, &mut child_dfs, &mut child_old_states);
                                    (old_mats, old_tf, child_old_states)
                                })
                            });
                            if let Some((old_mats, old_tf, child_old_states)) = snapshot {
                                if !old_mats.is_empty() {
                                    self.inspector_transform_drag = Some(InspectorTransformDrag::ActorGroup {
                                        dfs_id: target_id, old_mats, old_tf, child_old_states,
                                    });
                                } else {
                                    // ModelComponent なし（スクリプト専用アクター等）
                                    self.inspector_transform_drag = Some(InspectorTransformDrag::Actor {
                                        wl, dfs_id: target_id, old_tf,
                                    });
                                }
                            }
                        } else {
                            let old_tf = self.scene.as_ref().and_then(|s| {
                                let mut c = 0u32;
                                find_actor_by_dfs(&s.actors, wl, target_id, &mut c)
                                    .and_then(|a| s.world.get::<ActorTransform>(a.entity).cloned())
                            });
                            if let Some(old_tf) = old_tf {
                                self.inspector_transform_drag = Some(InspectorTransformDrag::Actor {
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
                            InspectorTransformDrag::Instance { idx, old_mat } => {
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
                            InspectorTransformDrag::Actor { wl: drag_wl, dfs_id, old_tf } => {
                                {
                                    if let Some(scene) = &self.scene {
                                        let mut c = 0u32;
                                        let new_tf_opt = find_actor_by_dfs(&scene.actors, drag_wl, dfs_id, &mut c)
                                            .and_then(|a| scene.world.get::<ActorTransform>(a.entity).cloned());
                                        if let Some(new_tf) = new_tf_opt {
                                            if old_tf != new_tf {
                                                self.undo_history.record(Box::new(ActorTransformCommand {
                                                    world_line: drag_wl, dfs_id,
                                                    old_transform: old_tf, new_transform: new_tf,
                                                }));
                                            }
                                        }
                                    }
                                }
                                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                            }
                            InspectorTransformDrag::ActorGroup { dfs_id, old_mats, old_tf, child_old_states } => {
                                let mut c = 0u32;
                                let transforms: Vec<(u32, [[f32;4];4], [[f32;4];4])>;
                                let new_tf: ActorTransform;
                                let child_transforms: Vec<(u32, ActorTransform, ActorTransform, [[f32;4];4], [[f32;4];4])>;
                                {
                                    let scene_ref = self.scene.as_ref();
                                    // 選択アクターの MC をスロット entity 経由で取得する
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
                                                .and_then(|a| s.world.get::<ActorTransform>(a.entity).cloned())
                                        })
                                        .unwrap_or_default();
                                    // 子孫アクターの変化を収集
                                    child_transforms = child_old_states.iter().filter_map(|(child_dfs, old_child_tf, old_mc_mat)| {
                                        let mut cc = 0u32;
                                        let new_child_tf = scene_ref
                                            .and_then(|s| {
                                                find_actor_by_dfs(&s.actors, wl, *child_dfs, &mut cc)
                                                    .and_then(|a| s.world.get::<ActorTransform>(a.entity).cloned())
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
                                    self.undo_history.record(Box::new(ActorGroupTransformCommand {
                                        wl, dfs_id, old_tf, new_tf, transforms, child_transforms,
                                        extra_slot_transforms: vec![],
                                    }));
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
                IpcCommand::SetShowAxisGizmo(v) => {
                    self.show_axis_gizmo = v;
                }
                IpcCommand::GetCamState => {
                    let (pos, yaw, pitch, fov, far, spd) = self.cam_state_tuple();
                    if let Some(ipc) = &self.ipc {
                        ipc.send(&format!("CAM_STATE:{pos},{yaw},{pitch},{fov},{far},{spd}"));
                    }
                }
                IpcCommand::SetCameraTransform { px, py, pz, yaw, pitch } => {
                    self.apply_camera_transform(px, py, pz, yaw, pitch);
                }
                IpcCommand::SetCameraSpeed(speed) => {
                    self.camera.move_speed = speed.clamp(0.1, 500.0);
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
                    self.saved_cameras.remove(&0); // シーンカメラのみリセット
                    let result = if let Some(ctx) = &self.draw_ctx {
                        Some(Scene::load(
                            std::path::Path::new(&path),
                            ctx,
                            self.scripting_host.as_ref(),
                        ))
                    } else { None };
                    match result {
                        Some(Ok((mut new_scene, cam_data))) => {
                            // world_line > 0 のアクター（アクター編集タブ）を保持する
                            if let Some(old_scene) = self.scene.take() {
                                for actor in old_scene.actors.into_iter().filter(|a| a.world_line > 0) {
                                    new_scene.actors.push(actor);
                                }
                            }
                            self.scene = Some(new_scene);
                            self.selected_instances.clear();
                            self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
                            self.undo_history = UndoHistory::new();
                            if let Some(cam) = cam_data {
                                self.apply_camera_data(&cam);
                            }
                            self.sync_anim_seeds();
                            self.send_selected();
                            self.send_hierarchy();
                            if let Some(ipc) = &self.ipc {
                                ipc.send("SCENE_LOADED");
                                let (pos, yaw, pitch, fov, far, spd) = self.cam_state_tuple();
                                ipc.send(&format!("CAM_STATE:{pos},{yaw},{pitch},{fov},{far},{spd}"));
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

                        // main_scene を確保し、同じ world_line の古いアクターを World ごと除去する。
                        // ※ 独自 World を作らず main_scene.world に直接ロードするため、
                        //   まず既存エンティティを despawn してからロードする。
                        let main_scene = self.scene.get_or_insert_with(|| Scene::new("main"));
                        {
                            // 除去対象エンティティを再帰的に収集（actor.entity + スロット entity を含む）
                            fn collect_entities(actor: &Actor, out: &mut Vec<crate::engine::ecs::Entity>) {
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

                        // アクターを main_scene.world に直接ロードする。
                        // entity が main_scene.world に登録されるため、
                        // 再オープン後も to_data() がコンポーネントを正しく参照できる。
                        let result = Scene::load_actor_into(
                            std::path::Path::new(&path),
                            self.draw_ctx.as_ref().unwrap(),
                            &mut main_scene.world,
                            self.scripting_host.as_ref(),
                            world_line,
                        );

                        match result {
                            Ok(actor) => {
                                // 2D Actor かどうかをプッシュ前に判定してキャンバス世界線に登録する
                                if actor.is_2d() {
                                    self.canvas_world_lines.insert(world_line);
                                } else {
                                    self.canvas_world_lines.remove(&world_line);
                                }
                                main_scene.actors.push(actor);
                                // この世界線に切り替え
                                self.active_world_line = world_line;
                                // カメラを復元（初回はデフォルト）
                                let cam = self.saved_cameras.get(&world_line).cloned()
                                    .unwrap_or_else(DebugCameraData::default);
                                self.apply_camera_data(&cam);
                                // 選択・Undo をリセット
                                self.selected_instances.clear();
                                self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
                                self.undo_history = UndoHistory::new();
                                self.sync_anim_seeds();
                                self.send_selected();
                                self.do_send_hierarchy();
                                self.send_world_line_info();
                                if let Some(ipc) = &self.ipc {
                                    ipc.send("ACTOR_EDIT_STARTED");
                                    let (pos, yaw, pitch, fov, far, spd) = self.cam_state_tuple();
                                    ipc.send(&format!("CAM_STATE:{pos},{yaw},{pitch},{fov},{far},{spd}"));
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
                IpcCommand::SetActiveWorldLine(wl) => {
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
                    // カメラを復元
                    if let Some(cam) = self.saved_cameras.get(&wl).cloned() {
                        self.apply_camera_data(&cam);
                    }
                    self.selected_instances.clear();
                    self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
                    self.undo_history = UndoHistory::new();
                    self.sync_anim_seeds();
                    self.send_selected();
                    self.do_send_hierarchy();
                    self.send_world_line_info();
                    if let Some(ipc) = &self.ipc {
                        if wl == 0 {
                            ipc.send("ACTOR_EDIT_ENDED");
                        } else {
                            ipc.send("ACTOR_EDIT_STARTED");
                        }
                        let (pos, yaw, pitch, fov, far, spd) = self.cam_state_tuple();
                        ipc.send(&format!("CAM_STATE:{pos},{yaw},{pitch},{fov},{far},{spd}"));
                    }
                }
                IpcCommand::RemoveWorldLine(wl) => {
                    if let Some(scene) = &mut self.scene {
                        scene.actors.retain(|a| a.world_line != wl);
                    }
                    self.saved_cameras.remove(&wl);
                    self.canvas_world_lines.remove(&wl);
                    self.canvas_cameras.remove(&wl);
                }
                IpcCommand::AddComponent { actor_dfs_id, component_type, slot_name, args } => {
                    let ct = component_type.clone();
                    let sn = slot_name.clone();
                    let a  = args.clone();
                    self.handle_add_component_to_actor(actor_dfs_id, &ct, &sn, &a);
                }
                IpcCommand::GetActorComponents(dfs_id) => {
                    self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                }
                IpcCommand::AddActor { world_line, parent_dfs_id } => {
                    self.handle_add_actor(world_line, parent_dfs_id);
                }
                IpcCommand::AddActor2D { world_line, parent_dfs_id } => {
                    self.handle_add_actor_2d(world_line, parent_dfs_id);
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
                IpcCommand::SetCanvasTransform { dfs_id, px, py, rotation, sx, sy } => {
                    self.handle_set_canvas_transform(dfs_id, px, py, rotation, sx, sy);
                }
                IpcCommand::SetCanvasSize { actor_dfs_id, slot_idx, width, height } => {
                    self.handle_set_canvas_size(actor_dfs_id, slot_idx, width, height);
                }
                IpcCommand::SetModelPath { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_model_path(actor_dfs_id, slot_idx, &p);
                }
                IpcCommand::DuplicateComponent { actor_dfs_id, slot_idx } => {
                    self.handle_duplicate_component(actor_dfs_id, slot_idx);
                }
                IpcCommand::DropActor { path, screen_x, screen_y } => {
                    // 次フレームのIDパスでワールド座標を読み出すためにキューイングする
                    self.pending_drop       = Some((path, screen_x, screen_y));
                    // ドロップ確定でホバープレビューをクリアする
                    self.pending_drop_hover = None;
                    self.drop_preview_pos   = None;
                }
                IpcCommand::DragHover { x, y } => {
                    // ドラッグ中カーソル位置を記録し、次フレームのレイキャストで球体位置を解決する
                    eprintln!("[Hover] DragHover received: ({x},{y})");
                    self.pending_drop_hover = Some((x, y));
                }
                IpcCommand::DragHoverEnd => {
                    // ドラッグ離脱: プレビュー球体を消す
                    eprintln!("[Hover] DragHoverEnd received");
                    self.pending_drop_hover = None;
                    self.drop_preview_pos   = None;
                }
            }
        }
    }

    /// ヒエラルキーを JSON にシリアライズしてエディタへ送信する（実装本体）。
    fn do_send_hierarchy(&self) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };

        // シーンモード・アクター編集モード共通: アクターツリーを DFS 順に送信する。
        // エディタ側の VirtualActorNodeIdBase (999_000_000) と組み合わせて選択を行う。
        let wl = self.active_world_line;
        let roots: Vec<&Actor> = scene.actors.iter()
            .filter(|a| a.world_line == wl)
            .collect();

        let mut nodes: Vec<(u32, String, Option<u32>)> = Vec::new();
        let mut counter = 0u32;
        for root in &roots {
            collect_actor_nodes(root, None, &mut counter, &mut nodes);
        }

        let json = build_hierarchy_json(&nodes);
        ipc.send(&format!("HIERARCHY:{json}"));
    }

    /// ヒエラルキー送信（スロットリング付き）。
    /// 100ms 以内に連続呼び出しされた場合はフラグを立てて遅延送信する。
    fn send_hierarchy(&mut self) {
        let now = std::time::Instant::now();
        if let Some(last) = self.last_hierarchy_send {
            if now.duration_since(last).as_millis() < 100 {
                self.hierarchy_dirty = true;
                return;
            }
        }
        self.last_hierarchy_send = Some(now);
        self.hierarchy_dirty = false;
        self.do_send_hierarchy();
    }

    /// アクターデータを JSON でエディタへ送信する。
    fn send_actor_data(&self, idx: u32) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };

        // 仮想アクターノード（ModelComponent なし）のケース
        if self.active_world_line != 0 && idx >= 999_000_000 {
            let actor_idx = (idx - 999_000_000) as usize;
            let wl = self.active_world_line;
            if let Some(actor) = scene.actors.iter().filter(|a| a.world_line == wl).nth(actor_idx) {
                let name = serde_json::to_string(&actor.name).unwrap_or_default();
                let json = format!(
                    r#"{{"id":{idx},"name":{name},"transform":{{"px":0.0,"py":0.0,"pz":0.0,"ex":0.0,"ey":0.0,"ez":0.0,"sx":1.0,"sy":1.0,"sz":1.0}},"model_path":null}}"#
                );
                ipc.send(&format!("ACTOR_DATA:{json}"));
            }
            return;
        }

        let Some(mc)    = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) else { return };

        let i = idx as usize;
        if i >= mc.instance_mats.len() { return; }

        let mat  = mc.instance_mats[i];
        let meta = &mc.instance_meta[i];

        // 位置: 第 4 列
        let (px, py, pz) = (mat[0][3], mat[1][3], mat[2][3]);

        // スケール: 各列ベクトルの長さ
        let scale_x = (mat[0][0]*mat[0][0] + mat[1][0]*mat[1][0] + mat[2][0]*mat[2][0]).sqrt();
        let scale_y = (mat[0][1]*mat[0][1] + mat[1][1]*mat[1][1] + mat[2][1]*mat[2][1]).sqrt();
        let scale_z = (mat[0][2]*mat[0][2] + mat[1][2]*mat[1][2] + mat[2][2]*mat[2][2]).sqrt();

        // 正規化された純回転行列を構築し Shepperd 法でクォータニオン → YXZ オイラー角
        let inv_x = if scale_x > 1e-10 { 1.0 / scale_x } else { 0.0 };
        let inv_y = if scale_y > 1e-10 { 1.0 / scale_y } else { 0.0 };
        let inv_z = if scale_z > 1e-10 { 1.0 / scale_z } else { 0.0 };
        let rot_mat = Mat4x4::new(
            mat[0][0]*inv_x, mat[0][1]*inv_y, mat[0][2]*inv_z, 0.0,
            mat[1][0]*inv_x, mat[1][1]*inv_y, mat[1][2]*inv_z, 0.0,
            mat[2][0]*inv_x, mat[2][1]*inv_y, mat[2][2]*inv_z, 0.0,
            0.0, 0.0, 0.0, 1.0,
        );
        let euler  = Quaternion::from_matrix(&rot_mat).to_euler(); // YXZ ラジアン
        const DEG: f32 = 180.0 / std::f32::consts::PI;
        let (ex, ey, ez) = (euler.x * DEG, euler.y * DEG, euler.z * DEG);

        let name       = serde_json::to_string(&meta.name).unwrap_or_default();
        let model_path = serde_json::to_string(&mc.source_path).unwrap_or_default();

        let json = format!(
            r#"{{"id":{idx},"name":{name},"transform":{{"px":{px:.4},"py":{py:.4},"pz":{pz:.4},"ex":{ex:.4},"ey":{ey:.4},"ez":{ez:.4},"sx":{scale_x:.4},"sy":{scale_y:.4},"sz":{scale_z:.4}}},"model_path":{model_path}}}"#
        );
        ipc.send(&format!("ACTOR_DATA:{json}"));
    }

    /// インスタンスのトランスフォームを書き換える。
    fn apply_set_transform(&mut self, id: u32, px: f32, py: f32, pz: f32,
                           ex: f32, ey: f32, ez: f32, sx: f32, sy: f32, sz: f32) {
        let i = id as usize;
        let wl           = self.active_world_line;
        let selected_dfs = self.actor_virtual_selected_idx;

        const RAD: f32 = std::f32::consts::PI / 180.0;
        let q = Vector3::new(ex * RAD, ey * RAD, ez * RAD).to_quaternion();
        let transform = Transform {
            position: Vector3::new(px, py, pz),
            rotation: q,
            scale:    Vector3::new(sx, sy, sz),
        };
        let new_mat = transform.to_matrix().data;

        // Phase 1: 選択 MC のインスタンスを更新してデルタを計算
        let (old_mat, delta) = {
            let Some(scene) = &mut self.scene else { return };
            // Entity を先に取得（actors の不変借用を解放してから world を可変借用）
            let entity_opt = if wl != 0 {
                let dfs = match selected_dfs { Some(d) => d as u32, None => return };
                let mut c = 0u32;
                find_actor_by_dfs(&scene.actors, wl, dfs, &mut c).map(|a| a.entity)
            } else {
                scene.actors.iter().find(|a| a.world_line == wl).map(|a| a.entity)
            };
            let entity = match entity_opt { Some(e) => e, None => return };
            let mc = scene.world.get_mut::<ModelComponent>(entity);
            let Some(mc) = mc else { return };
            if i >= mc.instance_mats.len() { return; }
            let old_mat = mc.instance_mats[i];
            let delta   = mat4x4_mul(new_mat, mat4x4_inv(old_mat));
            mc.instance_mats[i] = new_mat;
            let descendants = mc.all_descendants(id);
            for d in descendants {
                if let Some(m) = mc.instance_mats.get_mut(d as usize) {
                    *m = mat4x4_mul(delta, *m);
                }
            }
            mc.mark_batch_dirty();
            (old_mat, delta)
        };

        // Phase 2: アクター編集モードでは actor.transform と子アクター MC を伝播する
        let (selected_old_tf, selected_new_tf, child_changes) = if wl != 0 {
            let dfs = match selected_dfs { Some(d) => d as u32, None => return };
            let Some(scene) = &mut self.scene else { return };
            // entity を先取得（actors の不変借用を解放）
            let entity = {
                let mut c = 0u32;
                match find_actor_by_dfs(&scene.actors, wl, dfs, &mut c).map(|a| a.entity) {
                    Some(e) => e,
                    None => return,
                }
            };
            let old_tf = scene.world.get::<ActorTransform>(entity).cloned().unwrap_or_default();
            let new_tf = ActorTransform::from_mat4(&mat4x4_mul(delta, old_tf.to_mat4()));
            if let Some(t) = scene.world.get_mut::<ActorTransform>(entity) { *t = new_tf.clone(); }
            let mut child_dfs_counter = dfs + 1;
            let mut child_changes = Vec::new();
            {
                // actors と world を別フィールドとして同時可変借用する
                let (actors, world) = (&mut scene.actors, &mut scene.world);
                let mut c2 = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(actors, wl, dfs, &mut c2) {
                    apply_delta_to_actor_children(actor, world, delta, &mut child_dfs_counter, &mut child_changes);
                }
            }
            (old_tf, new_tf, child_changes)
        } else {
            (ActorTransform::default(), ActorTransform::default(), Vec::new())
        };

        // Phase 3: ドラッグ中は EndTransformDrag でまとめて記録するためここでは記録しない
        if self.inspector_transform_drag.is_none() {
            if wl != 0 {
                let dfs = selected_dfs.unwrap() as u32;
                self.undo_history.record(Box::new(ActorGroupTransformCommand {
                    wl,
                    dfs_id: dfs,
                    old_tf: selected_old_tf,
                    new_tf: selected_new_tf,
                    transforms: vec![(id, old_mat, new_mat)],
                    child_transforms: child_changes,
                    extra_slot_transforms: vec![],
                }));
            } else {
                self.undo_history.record(Box::new(TransformCommand {
                    instance_idx: id,
                    old_mat,
                    new_mat,
                }));
            }
        }
    }

    /// 現在の世界線情報をエディタへ送信する（デバッグログ用）。
    fn send_world_line_info(&self) {
        let Some(ipc) = &self.ipc else { return };
        let wl = self.active_world_line;
        let actor_name = self.scene.as_ref()
            .and_then(|s| s.actors.iter().find(|a| a.world_line == wl))
            .map(|a| a.name.clone())
            .unwrap_or_else(|| if wl == 0 { "Scene".to_string() } else { "<none>".to_string() });
        let inst_count = self.scene.as_ref()
            .and_then(|s| s.find_component_in_world_line::<ModelComponent>(wl))
            .map(|mc| mc.instance_mats.len())
            .unwrap_or(0);
        ipc.send(&format!("WORLD_LINE_INFO:WL:{wl} | Actor:{actor_name} | Instances:{inst_count}"));
    }

    /// InstanceMeta::anim_seed を instanced_batch に同期する。
    /// 構造変更（追加・削除・Undo/Redo）後に呼び出す。
    fn sync_anim_seeds(&mut self) {
        let wl = self.active_world_line;
        if let Some(scene) = &mut self.scene {
            if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(wl) {
                let seeds: Vec<u32> = mc.instance_meta.iter().map(|m| m.anim_seed).collect();
                mc.set_batch_anim_seeds(&seeds);
            }
        }
    }

    /// 選択アクター / 選択インスタンスをクリップボードへコピーする。
    ///
    /// - アクターツリー選択（selected_actor_dfs_ids が非空）→ ActorData をコピー
    /// - レガシー MC インスタンス選択（selected_instances が非空）→ ClipboardItem をコピー（後方互換）
    fn do_copy(&mut self) {
        // シーンモード / アクターツリー選択: ActorData 単位でコピーする
        if !self.selected_actor_dfs_ids.is_empty() {
            let Some(scene) = &self.scene else { return };
            let wl = self.active_world_line;
            let mut new_clipboard: Vec<ActorData> = Vec::new();
            for &dfs_id in &self.selected_actor_dfs_ids {
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id as u32, &mut c) {
                    new_clipboard.push(actor.to_data(&scene.world));
                }
            }
            if !new_clipboard.is_empty() {
                self.actor_clipboard = new_clipboard;
                // MC クリップボードはクリアしておく（混在防止）
                self.clipboard.clear();
            }
            return;
        }

        // レガシー: MC インスタンス直接選択（アクター編集モード等）
        use std::collections::{HashMap, HashSet};
        let Some(scene) = &self.scene else { return };
        let Some(mc)    = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) else { return };
        if self.selected_instances.is_empty() { return; }

        let mut copy_set: HashSet<u32> = self.selected_instances.iter().copied().collect();
        for &root in &self.selected_instances {
            copy_set.extend(mc.all_descendants(root));
        }
        let mut copy_list: Vec<u32> = copy_set.into_iter().collect();
        copy_list.sort_unstable();

        let orig_to_local: HashMap<u32, usize> = copy_list.iter()
            .enumerate().map(|(i, &orig)| (orig, i)).collect();

        self.clipboard = copy_list.iter().map(|&orig| {
            let meta         = &mc.instance_meta[orig as usize];
            let local_parent = meta.parent
                .filter(|&p| p < GROUP_ID_BASE)
                .and_then(|p| orig_to_local.get(&p).copied());
            ClipboardItem {
                name:         meta.name.clone(),
                mat:          mc.instance_mats[orig as usize],
                local_parent,
                anim_seed:    meta.anim_seed,
            }
        }).collect();
        // アクタークリップボードはクリアしておく（混在防止）
        self.actor_clipboard.clear();
    }

    /// クリップボードの内容をシーンへペーストする。
    ///
    /// - actor_clipboard が非空 → アクターとして復元（シーンモード）
    /// - clipboard が非空 → MC インスタンスとして復元（レガシー/後方互換）
    fn do_paste(&mut self) {
        // シーンモード: ActorData クリップボードからアクターを復元する
        if !self.actor_clipboard.is_empty() {
            let wl = self.active_world_line;
            if self.scene.is_none() || self.draw_ctx.is_none() { return; }

            let before_actors = self.snapshot_actors_for_wl(wl);
            let data_list = self.actor_clipboard.clone();

            // ペースト後に新規アクターを選択するための DFS ベース位置を計算する
            let dfs_start = {
                let scene = self.scene.as_ref().unwrap();
                let mut c = 0usize;
                for a in scene.actors.iter().filter(|a| a.world_line == wl) {
                    count_actor_dfs_nodes(a, &mut c);
                }
                c
            };

            {
                let ctx  = self.draw_ctx.as_ref().unwrap();
                let host = self.scripting_host.as_ref();
                let scene = self.scene.as_mut().unwrap();

                // 元の位置から少しずらしてペーストする
                const PASTE_OFFSET: f32 = 0.5;
                for data in data_list {
                    let mut paste_data = data;
                    paste_data.name = format!("{} (copy)", paste_data.name);
                    if let Some(ref mut tf) = paste_data.transform {
                        tf.position[0] += PASTE_OFFSET;
                        tf.position[2] += PASTE_OFFSET;
                    }
                    match build_actor(paste_data, ctx, &mut scene.world, host) {
                        Ok(mut actor) => {
                            actor.set_world_line_recursive(wl);
                            scene.actors.push(actor);
                        }
                        Err(e) => eprintln!("[SEED] do_paste: build_actor error: {e}"),
                    }
                }
            }

            let clipboard_count = self.actor_clipboard.len();
            let after_actors = self.snapshot_actors_for_wl(wl);
            self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
                world_line: wl,
                before_actors,
                after_actors,
            }));

            // 新規追加されたアクターを選択状態にする
            self.selected_actor_dfs_ids = (dfs_start .. dfs_start + clipboard_count).collect();
            self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
            self.selected_instances.clear();

            self.send_selected();
            self.send_hierarchy();
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            return;
        }

        // レガシー: MC インスタンスクリップボードから復元する（アクター編集モード等）
        use crate::engine::structs::components::model_component::InstanceMeta;
        if self.clipboard.is_empty() { return; }

        let before_selection = self.selected_instances.clone();

        let new_indices = {
            let wl = self.active_world_line;
            let Some(scene) = &mut self.scene else { return };
            let Some(mc)    = scene.find_component_in_world_line_mut::<ModelComponent>(wl) else { return };

            let before_mats   = mc.instance_mats.clone();
            let before_meta   = mc.instance_meta.clone();
            let before_groups = mc.group_meta.clone();
            let before_gid    = mc.next_group_id;

            let base_idx = mc.instance_mats.len() as u32;
            let mut new_indices = Vec::with_capacity(self.clipboard.len());

            for (i, item) in self.clipboard.iter().enumerate() {
                mc.instance_mats.push(item.mat);
                mc.instance_meta.push(InstanceMeta {
                    name:      format!("{}(1)", item.name),
                    parent:    item.local_parent.map(|lp| base_idx + lp as u32),
                    anim_seed: item.anim_seed,
                });
                new_indices.push(base_idx + i as u32);
            }
            mc.mark_batch_dirty();

            let after_mats   = mc.instance_mats.clone();
            let after_meta   = mc.instance_meta.clone();
            let after_groups = mc.group_meta.clone();
            let after_gid    = mc.next_group_id;

            self.undo_history.record(Box::new(SceneSnapshotCommand {
                before_mats, before_meta, before_groups, before_gid,
                after_mats,  after_meta,  after_groups,  after_gid,
                before_selection: before_selection.clone(),
                after_selection:  new_indices.clone(),
            }));

            new_indices
        };

        self.selected_instances = new_indices;
        self.sync_anim_seeds();
        self.send_selected();
        self.send_hierarchy();
    }

    /// 現在の選択インスタンスをエディタへ通知する。
    fn send_selected(&self) {
        let Some(ipc) = &self.ipc else { return };
        // マルチ選択: selected_actor_dfs_ids が 2 件以上の場合は SELECTED_MULTI で全仮想 ID を送る
        if self.selected_actor_dfs_ids.len() > 1 {
            let ids = self.selected_actor_dfs_ids.iter()
                .map(|&dfs| (999_000_000u64 + dfs as u64).to_string())
                .collect::<Vec<_>>().join(",");
            ipc.send(&format!("SELECTED_MULTI:{ids}"));
            return;
        }
        // 単一選択: プライマリ actor_virtual_selected_idx で通知
        if let Some(actor_idx) = self.actor_virtual_selected_idx {
            ipc.send(&format!("SELECTED:{}", 999_000_000u64 + actor_idx as u64));
            return;
        }
        // 未選択
        ipc.send("SELECTED:-1");
    }

    /// インスペクターの「コンポーネントを追加」リクエストを処理する。
    fn handle_add_component(&mut self, actor_id: u32, component_type: &str, args: &str) {
        if self.draw_ctx.is_none() || self.ipc.is_none() || self.scene.is_none() { return; }

        let wl = self.active_world_line;
        let actor_idx = if actor_id >= 999_000_000 {
            (actor_id - 999_000_000) as usize
        } else {
            return;
        };

        match component_type {
            "ModelComponent" => {
                let path = std::path::Path::new(args);
                let model = match crate::engine::core::loader::load_model(path) {
                    Ok(m) => m,
                    Err(e) => {
                        if let Some(ipc) = &self.ipc { ipc.send(&format!("LOAD_ERROR:{e}")); }
                        return;
                    }
                };

                // GPU リソース構築（ctx の借用はここで完結させる）
                let (gpu_model, instanced_batch) = {
                    let ctx = self.draw_ctx.as_ref().unwrap();
                    (ctx.upload_model(&model), ctx.create_instanced_batch(&model, 1))
                };
                // Arc 化してキャッシュにも登録する（今後の Undo/Redo リビルドで再利用可能にする）
                let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
                    let ctx = self.draw_ctx.as_ref().unwrap();
                    let arc = std::sync::Arc::new(model);
                    ctx.model_cache.borrow_mut()
                        .entry(args.to_string())
                        .or_insert_with(|| std::sync::Arc::clone(&arc));
                    arc
                };

                // アクターの現在 Transform（World から取得）を初期インスタンス位置に使う
                let initial_mat: [[f32; 4]; 4] = {
                    let scene = self.scene.as_ref().unwrap();
                    scene.actors.iter()
                        .filter(|a| a.world_line == wl)
                        .nth(actor_idx)
                        .and_then(|a| scene.world.get::<ActorTransform>(a.entity))
                        .map(|tf| tf.to_mat4())
                        .unwrap_or([[1.0,0.0,0.0,0.0],[0.0,1.0,0.0,0.0],[0.0,0.0,1.0,0.0],[0.0,0.0,0.0,1.0]])
                };
                let mc = ModelComponent {
                    source_path:     args.to_string(),
                    model:           Some(model_arc),
                    gpu_model:       Some(gpu_model),
                    instanced_batch: Some(instanced_batch),
                    instance_mats:   vec![initial_mat],
                    instance_meta:   vec![crate::engine::components::InstanceMeta::new("Instance_0")],
                    group_meta:      Vec::new(),
                    next_group_id:   GROUP_ID_BASE,
                };

                // スロット専用エンティティを spawn して world に insert し、スロットを登録する
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, mc);
                    if let Some(actor) = scene.actors.iter_mut()
                        .filter(|a| a.world_line == wl)
                        .nth(actor_idx)
                    {
                        actor.add_slot_typed::<ModelComponent>("ModelComponent".to_string(), ComponentKind::Model, slot_entity);
                        true
                    } else {
                        scene.world.despawn(slot_entity);
                        false
                    }
                };

                if found {
                    self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
                    self.selected_instances = vec![0];
                    self.sync_anim_seeds();
                    self.send_selected();
                    self.send_hierarchy();
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            _ => {}
        }
    }

    // ── アクター編集モード用ハンドラー群 ─────────────────────────

    /// アクタースロットのコンポーネント一覧を送信する。
    /// 選択中アクターのコンポーネント情報をエディタへ送信する。
    /// selected_slot_idx: ピッキングで選択された MC スロットの連番（Inspector ハイライト用）。
    fn send_actor_components(&self, dfs_id: u32, selected_slot_idx: usize) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };
        let wl = self.active_world_line;

        let mut c = 0u32;
        let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c) else { return };

        // 2D Actor は CanvasTransform、3D Actor は Transform を World から取得する
        let is_2d = actor.is_2d();
        let transform_json = if is_2d {
            // CanvasTransform: position(XY), rotation(Z 回転), scale(XY)
            let ct = scene.world.get::<CanvasTransform>(actor.entity).cloned().unwrap_or_default();
            format!(
                r#","canvas_transform":{{"px":{:.4},"py":{:.4},"rotation":{:.4},"sx":{:.4},"sy":{:.4}}}"#,
                ct.position[0], ct.position[1], ct.rotation, ct.scale[0], ct.scale[1],
            )
        } else {
            let tf = scene.world.get::<ActorTransform>(actor.entity).cloned().unwrap_or_default();
            let [px, py, pz] = tf.position;
            let [ex, ey, ez] = tf.rotation;
            let [sx, sy, sz] = tf.scale;
            format!(
                r#","transform":{{"px":{px:.4},"py":{py:.4},"pz":{pz:.4},"ex":{ex:.4},"ey":{ey:.4},"ez":{ez:.4},"sx":{sx:.4},"sy":{sy:.4},"sz":{sz:.4}}}"#
            )
        };

        // actor.to_data() でシリアライズ済みコンポーネント一覧を取得
        let actor_data = actor.to_data(&scene.world);

        let mut comps_json = String::from("[");
        for (i, slot_data) in actor_data.components.iter().enumerate() {
            if i > 0 { comps_json.push(','); }
            let (type_name, extra) = match &slot_data.component {
                ComponentData::ModelComponent(d) => {
                    let path_json = serde_json::to_string(&d.model_path).unwrap_or_default();
                    ("ModelComponent", format!(r#","model_path":{path_json}"#))
                }
                ComponentData::ScriptComponent(d) => {
                    let path_json = serde_json::to_string(&d.type_name).unwrap_or_default();
                    ("ScriptComponent", format!(r#","model_path":{path_json}"#))
                }
                ComponentData::CanvasComponent(d) => {
                    // width / height をインスペクター用に送信する
                    ("CanvasComponent", format!(r#","width":{:.4},"height":{:.4}"#, d.width, d.height))
                }
            };
            comps_json.push_str(&format!(
                r#"{{"slot":{},"name":{},"type":"{}"{}}}"#,
                i,
                serde_json::to_string(&slot_data.name).unwrap_or_default(),
                type_name,
                extra,
            ));
        }
        comps_json.push(']');

        let name_json = serde_json::to_string(&actor.name).unwrap_or_default();
        // selected_slot_idx: Inspector 側でどのコンポーネントスロットを選択状態にするかを示す
        // transform_json は 3D: "transform":{...}、2D: "canvas_transform":{...} のいずれか
        let json = format!(
            r#"{{"id":{dfs_id},"name":{name_json},"selected_slot":{selected_slot_idx}{transform_json},"components":{comps_json}}}"#
        );
        ipc.send(&format!("ACTOR_COMPONENTS:{json}"));
    }

    /// コンポーネントをアクターに追加する（新アーキテクチャ版）。
    fn handle_add_component_to_actor(
        &mut self,
        actor_dfs_id:   u32,
        component_type: &str,
        slot_name:      &str,
        args:           &str,
    ) {
        if self.draw_ctx.is_none() || self.ipc.is_none() || self.scene.is_none() { return; }
        let wl = self.active_world_line;

        // actor.entity を先に取得（Transform 参照のみに使用）
        let actor_entity_opt = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c).map(|a| a.entity)
        };
        let Some(actor_entity) = actor_entity_opt else { return };

        let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);

        match component_type {
            "ModelComponent" => {
                use crate::engine::components::InstanceMeta;
                let mc = if args.is_empty() {
                    ModelComponent::empty()
                } else {
                    let path = std::path::Path::new(args);
                    let model = match crate::engine::core::loader::load_model(path) {
                        Ok(m) => m,
                        Err(e) => {
                            if let Some(ipc) = &self.ipc { ipc.send(&format!("LOAD_ERROR:{e}")); }
                            return;
                        }
                    };
                    let (gpu_model, instanced_batch) = {
                        let ctx = self.draw_ctx.as_ref().unwrap();
                        (ctx.upload_model(&model), ctx.create_instanced_batch(&model, 1))
                    };
                    // Arc 化してキャッシュに登録する
                    let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
                        let ctx = self.draw_ctx.as_ref().unwrap();
                        let arc = std::sync::Arc::new(model);
                        ctx.model_cache.borrow_mut()
                            .entry(args.to_string())
                            .or_insert_with(|| std::sync::Arc::clone(&arc));
                        arc
                    };
                    // アクターの現在 transform を初期インスタンス位置に使う
                    let initial_mat: [[f32; 4]; 4] = {
                        let scene = self.scene.as_ref().unwrap();
                        scene.world.get::<ActorTransform>(actor_entity)
                            .map(|t| t.to_mat4())
                            .unwrap_or([[1.0,0.0,0.0,0.0],[0.0,1.0,0.0,0.0],[0.0,0.0,1.0,0.0],[0.0,0.0,0.0,1.0]])
                    };
                    ModelComponent {
                        source_path:     args.to_string(),
                        model:           Some(model_arc),
                        gpu_model:       Some(gpu_model),
                        instanced_batch: Some(instanced_batch),
                        instance_mats:   vec![initial_mat],
                        instance_meta:   vec![InstanceMeta::new("Instance_0")],
                        group_meta:      Vec::new(),
                        next_group_id:   GROUP_ID_BASE,
                    }
                };
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    // スロット専用エンティティを spawn してコンポーネントを格納する
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, mc);
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<ModelComponent>(name, ComponentKind::Model, slot_entity);
                        true
                    } else {
                        scene.world.despawn(slot_entity);
                        false
                    }
                };
                if found {
                    let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
                    self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
                        world_line: wl, actor_dfs_id, before_slots, after_slots,
                    }));
                    self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
                    self.selected_instances.clear();
                    self.send_hierarchy();
                    self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            "ScriptComponent" => {
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    // スロット専用エンティティを spawn してコンポーネントを格納する
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, PlaceholderScriptSlot { script_path: String::new() });
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<PlaceholderScriptSlot>(name, ComponentKind::Placeholder, slot_entity);
                        true
                    } else {
                        scene.world.despawn(slot_entity);
                        false
                    }
                };
                if found {
                    let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
                    self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
                        world_line: wl, actor_dfs_id, before_slots, after_slots,
                    }));
                    self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
                    self.selected_instances.clear();
                    self.send_hierarchy();
                    self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            "CanvasComponent" => {
                // デフォルトサイズ（1920×1080）の CanvasComponent を追加する。
                // サイズはインスペクターから後で変更可能。
                let name = slot_name.to_string();
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let slot_entity = scene.world.spawn();
                    scene.world.insert(slot_entity, CanvasComponent::default());
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_slot_typed::<CanvasComponent>(name, ComponentKind::Canvas, slot_entity);
                        true
                    } else {
                        scene.world.despawn(slot_entity);
                        false
                    }
                };
                if found {
                    let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
                    self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
                        world_line: wl, actor_dfs_id, before_slots, after_slots,
                    }));
                    self.actor_virtual_selected_slot_idx = 0;
                    self.selected_instances.clear();
                    self.send_hierarchy();
                    self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            _ => {}
        }
    }

    /// コンポーネントスロットのパスを後から設定する（ModelComponent / PlaceholderScriptSlot 共通）。
    fn handle_set_model_path(&mut self, actor_dfs_id: u32, slot_idx: u32, path: &str) {
        if self.draw_ctx.is_none() || self.ipc.is_none() || self.scene.is_none() { return; }
        let wl = self.active_world_line;

        // 対象スロットの entity と kind、および actor entity（Transform 参照用）を取得する
        let (actor_entity, slot_entity, slot_kind) = {
            let scene = self.scene.as_ref().unwrap();
            let mut c = 0u32;
            let actor = match find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) {
                Some(a) => a,
                None => return,
            };
            let slot = actor.slots().get(slot_idx as usize);
            match slot {
                Some(s) => (actor.entity, s.entity, s.kind),
                None => return,
            }
        };

        // PlaceholderScriptSlot の場合はスロット entity のパスのみ更新して早期リターン
        if slot_kind == ComponentKind::Placeholder {
            let scene = self.scene.as_mut().unwrap();
            if let Some(ps) = scene.world.get_mut::<PlaceholderScriptSlot>(slot_entity) {
                ps.script_path = path.to_string();
            }
            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            return;
        }

        // ModelComponent の場合: モデルをロードして GPU リソース再構築
        let model = match crate::engine::core::loader::load_model(std::path::Path::new(path)) {
            Ok(m) => m,
            Err(e) => {
                if let Some(ipc) = &self.ipc { ipc.send(&format!("LOAD_ERROR:{e}")); }
                return;
            }
        };
        let (gpu_model, instanced_batch) = {
            let ctx = self.draw_ctx.as_ref().unwrap();
            (ctx.upload_model(&model), ctx.create_instanced_batch(&model, 1))
        };
        // Arc 化してキャッシュに登録する
        let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
            let ctx = self.draw_ctx.as_ref().unwrap();
            let arc = std::sync::Arc::new(model);
            ctx.model_cache.borrow_mut()
                .entry(path.to_string())
                .or_insert_with(|| std::sync::Arc::clone(&arc));
            arc
        };
        use crate::engine::components::InstanceMeta;
        let scene = self.scene.as_mut().unwrap();
        // actor transform を先取得（actor.entity から Transform を参照）
        let initial_mat = scene.world.get::<ActorTransform>(actor_entity)
            .map(|t| t.to_mat4())
            .unwrap_or([[1.0,0.0,0.0,0.0],[0.0,1.0,0.0,0.0],[0.0,0.0,1.0,0.0],[0.0,0.0,0.0,1.0]]);
        // スロット専用 entity の ModelComponent を更新する
        let found = if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
            if mc.instance_mats.is_empty() {
                mc.instance_mats.push(initial_mat);
                mc.instance_meta.push(InstanceMeta::new("Instance_0"));
            }
            mc.source_path     = path.to_string();
            mc.model           = Some(model_arc);
            mc.gpu_model       = Some(gpu_model);
            mc.instanced_batch = Some(instanced_batch);
            true
        } else { false };
        if found {
            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
        }
    }

    /// 子アクターを追加する。
    fn handle_add_actor(&mut self, world_line: u32, parent_dfs_id: Option<u32>) {
        if self.scene.is_none() { return; }

        let before_actors = self.snapshot_actors_for_wl(world_line);

        let Some(scene) = &mut self.scene else { return };

        // World にエンティティを生成してデフォルト Transform を挿入する。
        // Actor::with_name() が使う Entity::default() は index=0 で全アクターが衝突するため
        // 必ず world.spawn() で一意な entity を取得する。
        let entity = scene.world.spawn();
        scene.world.insert(entity, ActorTransform::default());

        let new_actor = {
            let mut a = Actor::new(entity, "Actor");
            a.world_line = world_line;
            a
        };

        if let Some(pid) = parent_dfs_id {
            let mut c = 0u32;
            if let Some(parent) = find_actor_by_dfs_mut(&mut scene.actors, world_line, pid, &mut c) {
                parent.add_child(new_actor);
            }
        } else {
            scene.actors.push(new_actor);
        }

        let after_actors = self.snapshot_actors_for_wl(world_line);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line,
            before_actors,
            after_actors,
        }));

        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 2D Actor（CanvasTransform を持つ）をシーンに追加する。
    ///
    /// handle_add_actor の 2D 版。World に CanvasTransform を挿入し、
    /// Actor::new_2d でアクターを生成する。
    fn handle_add_actor_2d(&mut self, world_line: u32, parent_dfs_id: Option<u32>) {
        if self.scene.is_none() { return; }

        let before_actors = self.snapshot_actors_for_wl(world_line);

        let Some(scene) = &mut self.scene else { return };

        // 2D Actor 専用エンティティを spawn して CanvasTransform を挿入する
        let entity = scene.world.spawn();
        scene.world.insert(entity, CanvasTransform::default());

        let new_actor = {
            let mut a = Actor::new_2d(entity, "Actor");
            a.world_line = world_line;
            a
        };

        if let Some(pid) = parent_dfs_id {
            let mut c = 0u32;
            if let Some(parent) = find_actor_by_dfs_mut(&mut scene.actors, world_line, pid, &mut c) {
                parent.add_child(new_actor);
            }
        } else {
            scene.actors.push(new_actor);
        }

        let after_actors = self.snapshot_actors_for_wl(world_line);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line,
            before_actors,
            after_actors,
        }));

        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// .actor ファイルをシーンにドロップ配置する。
    ///
    /// `path` から Actor をロードし、`spawn_pos` にトランスフォームを設定して
    /// world_line=0 のシーンに追加する。配置操作は Undo/Redo の対象。
    fn handle_drop_actor(&mut self, path: &str, spawn_pos: [f32; 3]) {
        eprintln!("[Drop] handle_drop_actor path={path} spawn_pos={spawn_pos:?}");
        if self.draw_ctx.is_none() || self.scene.is_none() {
            eprintln!("[Drop] ABORTED: draw_ctx or scene is None");
            return;
        }

        // Undo のために配置前の world_line=0 アクターをスナップショットする
        let before_actors = self.snapshot_actors_for_wl(0);

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
                0, // world_line = 通常シーン
            )
        };

        match load_result {
            Ok(mut actor) => {
                eprintln!("[Drop] load_actor_into OK entity={:?} name={}", actor.entity, actor.name);
                // ドロップ位置への ActorTransform 設定と instance_mats 同期
                {
                    let scene = self.scene.as_mut().unwrap();
                    let tf = ActorTransform {
                        position: spawn_pos,
                        rotation: [0.0, 0.0, 0.0],
                        scale:    [1.0, 1.0, 1.0],
                    };
                    let spawn_mat = tf.to_mat4();
                    scene.world.insert(actor.entity, tf);
                    // GPU 描画に使われる instance_mats も spawn 位置行列で更新する
                    // （ActorTransform だけでは描画には反映されないため）
                    for slot in actor.slots() {
                        if slot.kind == ComponentKind::Model {
                            if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot.entity) {
                                for m in mc.instance_mats.iter_mut() {
                                    *m = spawn_mat;
                                }
                                mc.mark_batch_dirty();
                            }
                        }
                    }
                    scene.actors.push(actor);
                    eprintln!("[Drop] actors.len()={}", scene.actors.len());
                } // ← ここで scene の可変借用が解放される

                // 配置後スナップショットを取得し、Undo 履歴に記録する
                let after_actors = self.snapshot_actors_for_wl(0);
                self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
                    world_line: 0,
                    before_actors,
                    after_actors,
                }));

                self.send_hierarchy();
                if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            }
            Err(e) => {
                eprintln!("[Drop] load_actor_into ERR: {e}");
                if let Some(ipc) = &self.ipc {
                    ipc.send(&format!("LOAD_ERROR:{e}"));
                }
            }
        }
    }

    /// アクターを削除する（DFS id で特定）。
    fn handle_remove_actor(&mut self, dfs_id: u32) {
        let Some(_scene) = &self.scene else { return };
        let wl = self.active_world_line;

        let before_actors = self.snapshot_actors_for_wl(wl);

        {
            let scene = self.scene.as_mut().unwrap();
            let mut c = 0u32;
            remove_actor_by_dfs(&mut scene.actors, wl, dfs_id, &mut c);
        }

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        self.selected_instances.clear();
        self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
        if let Some(ipc) = &self.ipc { ipc.send("SELECTED:-1"); }
        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// アクター編集モードでアクターツリーのペアレント関係を変更する。
    ///
    /// child_dfs / new_parent_dfs は変更前のアクターツリー上の DFS id。
    /// 実際にアクターを取り出して新しい親の下へ移動するため、ドラッグ追跡が正しく機能するようになる。
    fn handle_reparent_actor(&mut self, child_dfs: u32, new_parent_dfs: Option<u32>) {
        let wl = self.active_world_line;
        let Some(_scene) = &self.scene else { return };

        let before_actors = self.snapshot_actors_for_wl(wl);

        {
            let scene = self.scene.as_mut().unwrap();

            // child のサブツリーサイズを先に算出する（取り出し後の DFS 補正に使う）
            let child_subtree_size = {
                let mut c = 0u32;
                find_actor_by_dfs(&scene.actors, wl, child_dfs, &mut c)
                    .map(|a| actor_subtree_size(a))
                    .unwrap_or(0)
            };
            if child_subtree_size == 0 { return; }

            // child をツリーから取り出す
            let mut extracted: Option<Actor> = None;
            let mut c = 0u32;
            extract_actor_by_dfs(&mut scene.actors, wl, child_dfs, &mut c, &mut extracted);
            let Some(mut child_actor) = extracted else { return };
            child_actor.set_world_line_recursive(wl);

            // child が new_parent より前（DFS 順）にある場合、取り出し後に new_parent の
            // DFS id が child_subtree_size 分ずれるため補正する
            let adjusted_parent_dfs = new_parent_dfs.map(|pid| {
                if child_dfs < pid { pid - child_subtree_size } else { pid }
            });

            // 新しい親へ挿入する（None の場合はルートへ追加）
            if let Some(pid) = adjusted_parent_dfs {
                let mut c2 = 0u32;
                if let Some(parent) = find_actor_by_dfs_mut(&mut scene.actors, wl, pid, &mut c2) {
                    parent.add_child(child_actor);
                } else {
                    // 親が見つからない場合はルートへフォールバック
                    scene.actors.push(child_actor);
                }
            } else {
                scene.actors.push(child_actor);
            }
        }

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    fn handle_rename_actor(&mut self, dfs_id: u32, name: &str) {
        let Some(scene) = &mut self.scene else { return };
        let wl = self.active_world_line;
        let mut c = 0u32;
        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, dfs_id, &mut c) {
            actor.name = name.to_string();
        }
        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    fn handle_remove_component_slot(&mut self, actor_dfs_id: u32, slot_idx: u32) {
        let Some(_scene) = &self.scene else { return };
        let wl = self.active_world_line;

        let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);

        {
            let scene = self.scene.as_mut().unwrap();
            // スロットの entity と kind を先に取り出して actors の borrow を解放する
            let removal_info = {
                let mut c = 0u32;
                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                    .and_then(|a| a.slots().get(slot_idx as usize).map(|s| (s.entity, s.kind)))
            };
            if let Some((slot_entity, kind)) = removal_info {
                // スロット専用エンティティからコンポーネントを除去して despawn する。
                // 各スロットは独自 entity を持つため、is_last_of_kind チェックは不要。
                match kind {
                    ComponentKind::Model       => { scene.world.remove::<ModelComponent>(slot_entity); }
                    ComponentKind::Script      => { scene.world.remove::<ScriptComponent>(slot_entity); }
                    ComponentKind::Placeholder => { scene.world.remove::<PlaceholderScriptSlot>(slot_entity); }
                    ComponentKind::Canvas      => { scene.world.remove::<CanvasComponent>(slot_entity); }
                }
                scene.world.despawn(slot_entity);
                // アクターのスロットリストから削除
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.remove_slot_at(slot_idx as usize);
                }
            }
        }

        let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: wl,
            actor_dfs_id,
            before_slots,
            after_slots,
        }));

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    fn handle_rename_component_slot(&mut self, actor_dfs_id: u32, slot_idx: u32, name: &str) {
        let Some(scene) = &mut self.scene else { return };
        let wl = self.active_world_line;
        let mut c = 0u32;
        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
            if let Some(slot) = actor.slots_mut().get_mut(slot_idx as usize) {
                slot.name = name.to_string();
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 2D Actor の CanvasTransform を更新する。
    ///
    /// 3D の handle_set_actor_transform と異なり、MC への delta 適用は行わない。
    /// CanvasTransform は位置・回転・スケールを独立して保持するためデルタ計算不要。
    fn handle_set_canvas_transform(
        &mut self,
        dfs_id: u32,
        px: f32, py: f32, rotation: f32, sx: f32, sy: f32,
    ) {
        let wl = self.active_world_line;

        // entity を先に取得して borrow を解放
        let entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c).map(|a| a.entity)
        };
        let Some(entity) = entity else { return };

        // CanvasTransform を更新する
        if let Some(scene) = &mut self.scene {
            if let Some(ct) = scene.world.get_mut::<CanvasTransform>(entity) {
                ct.position = [px, py];
                ct.rotation = rotation;
                ct.scale    = [sx, sy];
            }
        }

        // インスペクターを最新値で更新し、シーン変更を通知する
        self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// CanvasComponent のサイズを更新する。
    fn handle_set_canvas_size(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        width:        f32,
        height:       f32,
    ) {
        let wl = self.active_world_line;

        // スロットの entity を取得する
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .map(|s| s.entity)
        };
        let Some(slot_entity) = slot_entity else { return };

        // CanvasComponent を更新する
        if let Some(scene) = &mut self.scene {
            if let Some(cc) = scene.world.get_mut::<CanvasComponent>(slot_entity) {
                cc.width  = width;
                cc.height = height;
            }
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    fn handle_set_actor_transform(
        &mut self,
        dfs_id: u32,
        px: f32, py: f32, pz: f32,
        ex: f32, ey: f32, ez: f32,
        sx: f32, sy: f32, sz: f32,
    ) {
        let wl = self.active_world_line;
        let new_tf = ActorTransform { position: [px, py, pz], rotation: [ex, ey, ez], scale: [sx, sy, sz] };
        // entity を先に取得して borrow を解放
        let entity = {
            let Some(scene) = &mut self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c).map(|a| a.entity)
        };
        let Some(entity) = entity else {
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            return;
        };
        let old_tf = {
            let Some(scene) = &mut self.scene else { return };
            let old = scene.world.get::<ActorTransform>(entity).cloned().unwrap_or_default();
            if let Some(t) = scene.world.get_mut::<ActorTransform>(entity) { *t = new_tf.clone(); }
            old
        };

        // シーンモード・アクター編集モード共通: delta を選択アクターの MC と子アクターに適用する
        let delta = mat4x4_mul(new_tf.to_mat4(), mat4x4_inv(old_tf.to_mat4()));
        // Phase A: 選択アクターの全 ModelComponent スロットに delta を適用する。
        // ModelComponent は actor.entity ではなく各スロット専用の entity に格納されているため、
        // スロット entity を先に収集してから world を可変借用する。
        let mc_transforms = if let Some(scene) = &mut self.scene {
            let slot_entities: Vec<Entity> = {
                let mut c = 0u32;
                find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c)
                    .map(|a| a.slots().iter()
                        .filter(|s| s.kind == ComponentKind::Model)
                        .map(|s| s.entity)
                        .collect())
                    .unwrap_or_default()
            };
            let mut all_changes: Vec<(u32, [[f32;4];4], [[f32;4];4])> = Vec::new();
            for slot_entity in slot_entities {
                if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
                    let old_mats = mc.instance_mats.clone();
                    for m in &mut mc.instance_mats { *m = mat4x4_mul(delta, *m); }
                    mc.mark_batch_dirty();
                    for (i, &old) in old_mats.iter().enumerate() {
                        if let Some(new) = mc.instance_mats.get(i).copied() {
                            if new != old { all_changes.push((i as u32, old, new)); }
                        }
                    }
                }
            }
            all_changes
        } else { Vec::new() };
        // Phase B: 子アクターに delta を伝播
        let mut child_changes = Vec::new();
        if let Some(scene) = &mut self.scene {
            let (actors, world) = (&mut scene.actors, &mut scene.world);
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(actors, wl, dfs_id, &mut c) {
                let mut child_dfs_counter = dfs_id + 1;
                apply_delta_to_actor_children(actor, world, delta, &mut child_dfs_counter, &mut child_changes);
            }
        }
        let (mc_transforms, child_changes) = (mc_transforms, child_changes);

        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
        // ドラッグ中は EndTransformDrag でまとめて1コマンド記録するためここでは記録しない
        // シーンモード・アクター編集モード共通: MC 変化または Transform 変化があれば記録する
        if self.inspector_transform_drag.is_none() {
            if !mc_transforms.is_empty() || !child_changes.is_empty() || old_tf != new_tf {
                self.undo_history.record(Box::new(ActorGroupTransformCommand {
                    wl, dfs_id,
                    old_tf: old_tf.clone(), new_tf: new_tf.clone(),
                    transforms: mc_transforms,
                    child_transforms: child_changes,
                    extra_slot_transforms: vec![],
                }));
            }
            self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
        }
    }

    /// 仮想選択中のアクターのワールド座標（transform.position）を返す。
    /// シーンモード（wl==0）・アクター編集モード（wl!=0）共通で動作する。
    fn actor_virtual_world_pos(&self) -> Option<[f32; 3]> {
        let dfs_id = self.actor_virtual_selected_idx? as u32;
        let wl = self.active_world_line;
        let scene = self.scene.as_ref()?;
        let mut c = 0u32;
        let actor = find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c)?;
        Some(scene.world.get::<ActorTransform>(actor.entity)?.position)
    }

    /// 指定世界線のアクターツリー全体をデータとしてスナップショットする。
    fn snapshot_actors_for_wl(&self, wl: u32) -> Vec<ActorData> {
        self.scene.as_ref().map(|s| {
            s.actors.iter()
                .filter(|a| a.world_line == wl)
                .map(|a| a.to_data(&s.world))
                .collect()
        }).unwrap_or_default()
    }

    /// 指定アクターのコンポーネントスロット一覧をデータとしてスナップショットする。
    fn snapshot_actor_slots(&self, wl: u32, actor_dfs_id: u32) -> Vec<ComponentSlotData> {
        let Some(scene) = &self.scene else { return Vec::new() };
        let mut c = 0u32;
        find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
            .map(|actor| actor.to_data(&scene.world).components)
            .unwrap_or_default()
    }

    /// 指定世界線のアクターを data から再構築する（Undo/Redo 用）。
    fn rebuild_actors_for_wl(&mut self, wl: u32, actors_data: Vec<ActorData>) {
        let host = self.scripting_host.clone();
        if self.draw_ctx.is_none() { return; }

        // scene を一時的に取り出して draw_ctx との同時借用問題を回避
        let mut scene = self.scene.take().unwrap_or_else(|| Scene::new("main"));

        // 既存の wl アクターエンティティを despawn して削除
        let old_entities: Vec<_> = collect_entities_for_wl(&scene.actors, wl);
        for e in old_entities { scene.world.despawn(e); }
        scene.actors.retain(|a| a.world_line != wl);

        // 新アクターを構築
        let ctx = self.draw_ctx.as_ref().unwrap();
        for data in actors_data {
            match build_actor(data, ctx, &mut scene.world, host.as_ref()) {
                Ok(mut a) => { a.set_world_line_recursive(wl); scene.actors.push(a); }
                Err(e) => eprintln!("[SEED] rebuild_actors_for_wl error: {e}"),
            }
        }

        self.scene = Some(scene);
        self.selected_instances.clear();
        self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
    }

    /// 指定アクターのコンポーネントスロットを data から再構築する（Undo/Redo 用）。
    fn rebuild_actor_slots(&mut self, wl: u32, actor_dfs_id: u32, slots_data: Vec<ComponentSlotData>) {
        use crate::engine::core::loader::load_model;

        let host = self.scripting_host.clone();
        if self.draw_ctx.is_none() { return; }

        // scene を一時取り出し draw_ctx との借用競合を回避
        let mut scene = match self.scene.take() {
            Some(s) => s,
            None => return,
        };
        let ctx = self.draw_ctx.as_ref().unwrap();

        // 既存スロットの entity を全て despawn して削除する
        let existing_slot_entities: Vec<crate::engine::ecs::Entity> = {
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .map(|a| a.slot_entities().collect())
                .unwrap_or_default()
        };
        for e in existing_slot_entities { scene.world.despawn(e); }

        // 新コンポーネントを world に insert してスロット目録を更新
        let mut new_slots = Vec::new();
        for slot_data in slots_data {
            // スロット専用エンティティを新しく spawn する
            let slot_entity = scene.world.spawn();
            match slot_data.component {
                ComponentData::ModelComponent(mc_data) => {
                    let mc = if mc_data.model_path.is_empty() {
                        ModelComponent {
                            source_path:     String::new(),
                            model:           None,
                            gpu_model:       None,
                            instanced_batch: None,
                            instance_mats:   mc_data.instances,
                            instance_meta:   mc_data.meta,
                            group_meta:      mc_data.groups,
                            next_group_id:   mc_data.next_group_id,
                        }
                    } else {
                        let path = std::path::Path::new(&mc_data.model_path);
                        // キャッシュから CPU モデルを取得するか、ディスクから読み込む
                        let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
                            let mut cache = ctx.model_cache.borrow_mut();
                            if let Some(cached) = cache.get(&mc_data.model_path) {
                                std::sync::Arc::clone(cached)
                            } else {
                                match load_model(path) {
                                    Ok(m) => {
                                        let arc = std::sync::Arc::new(m);
                                        cache.insert(mc_data.model_path.clone(), std::sync::Arc::clone(&arc));
                                        arc
                                    }
                                    Err(_) => { scene.world.despawn(slot_entity); continue; }
                                }
                            }
                        };
                        let gpu_model       = ctx.upload_model(&*model_arc);
                        let instanced_batch = ctx.create_instanced_batch(&*model_arc, mc_data.instances.len() as u32);
                        ModelComponent {
                            source_path:     mc_data.model_path,
                            model:           Some(model_arc),
                            gpu_model:       Some(gpu_model),
                            instanced_batch: Some(instanced_batch),
                            instance_mats:   mc_data.instances,
                            instance_meta:   mc_data.meta,
                            group_meta:      mc_data.groups,
                            next_group_id:   mc_data.next_group_id,
                        }
                    };
                    scene.world.insert(slot_entity, mc);
                    new_slots.push(ComponentSlot::new::<ModelComponent>(slot_data.name, ComponentKind::Model, slot_entity));
                }
                ComponentData::ScriptComponent(sc_data) => {
                    if let Some(h) = &host {
                        if let Some(sc) = ScriptComponent::new(std::sync::Arc::clone(h), sc_data.type_name.clone()) {
                            scene.world.insert(slot_entity, sc);
                            new_slots.push(ComponentSlot::new::<ScriptComponent>(slot_data.name, ComponentKind::Script, slot_entity));
                        } else {
                            scene.world.insert(slot_entity, PlaceholderScriptSlot { script_path: sc_data.type_name });
                            new_slots.push(ComponentSlot::new::<PlaceholderScriptSlot>(slot_data.name, ComponentKind::Placeholder, slot_entity));
                        }
                    } else {
                        scene.world.insert(slot_entity, PlaceholderScriptSlot { script_path: sc_data.type_name });
                        new_slots.push(ComponentSlot::new::<PlaceholderScriptSlot>(slot_data.name, ComponentKind::Placeholder, slot_entity));
                    }
                }
                ComponentData::CanvasComponent(cc_data) => {
                    // CanvasComponent をスロット専用エンティティに insert
                    scene.world.insert(slot_entity, CanvasComponent { width: cc_data.width, height: cc_data.height });
                    new_slots.push(ComponentSlot::new::<CanvasComponent>(slot_data.name, ComponentKind::Canvas, slot_entity));
                }
            }
        }

        // actor.slots を更新
        {
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                actor.replace_slots(new_slots);
            }
        }

        self.scene = Some(scene);
    }

    /// コンポーネントを複製する（DUPLICATE_COMPONENT）。
    fn handle_duplicate_component(&mut self, actor_dfs_id: u32, slot_idx: u32) {
        if self.draw_ctx.is_none() { return; }
        let wl = self.active_world_line;
        let host = self.scripting_host.clone();

        // スロットデータを先にスナップショット（actors/world の借用を解放）
        let slot_data_opt = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|actor| actor.to_data(&scene.world).components.into_iter().nth(slot_idx as usize))
                .map(|mut sd| { sd.name = format!("{} Copy", sd.name); sd })
        };
        let Some(slot_data) = slot_data_opt else { return };

        let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);

        use crate::engine::core::loader::load_model;

        if self.draw_ctx.is_none() { return; }
        let mut scene = match self.scene.take() {
            Some(s) => s,
            None => return,
        };
        let ctx = self.draw_ctx.as_ref().unwrap();

        // 新コンポーネントを world に insert してスロット追加
        // スロット専用エンティティを spawn し、各スロットが独立したコンポーネントを持つ。
        let slot_added = match slot_data.component {
            ComponentData::ModelComponent(mc_data) => {
                let mc = if mc_data.model_path.is_empty() {
                    ModelComponent {
                        source_path:     String::new(),
                        model:           None,
                        gpu_model:       None,
                        instanced_batch: None,
                        instance_mats:   mc_data.instances,
                        instance_meta:   mc_data.meta,
                        group_meta:      mc_data.groups,
                        next_group_id:   mc_data.next_group_id,
                    }
                } else {
                    let path = std::path::Path::new(&mc_data.model_path);
                    // キャッシュから CPU モデルを取得するか、ディスクから読み込む
                    let model_arc: std::sync::Arc<crate::engine::core::loader::model::Model> = {
                        let mut cache = ctx.model_cache.borrow_mut();
                        if let Some(cached) = cache.get(&mc_data.model_path) {
                            std::sync::Arc::clone(cached)
                        } else {
                            match load_model(path) {
                                Ok(m) => {
                                    let arc = std::sync::Arc::new(m);
                                    cache.insert(mc_data.model_path.clone(), std::sync::Arc::clone(&arc));
                                    arc
                                }
                                Err(e) => {
                                    if let Some(ipc) = &self.ipc { ipc.send(&format!("LOAD_ERROR:{e}")); }
                                    self.scene = Some(scene);
                                    return;
                                }
                            }
                        }
                    };
                    let gpu_model       = ctx.upload_model(&*model_arc);
                    let instanced_batch = ctx.create_instanced_batch(&*model_arc, mc_data.instances.len() as u32);
                    ModelComponent {
                        source_path:     mc_data.model_path,
                        model:           Some(model_arc),
                        gpu_model:       Some(gpu_model),
                        instanced_batch: Some(instanced_batch),
                        instance_mats:   mc_data.instances,
                        instance_meta:   mc_data.meta,
                        group_meta:      mc_data.groups,
                        next_group_id:   mc_data.next_group_id,
                    }
                };
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, mc);
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<ModelComponent>(slot_data.name, ComponentKind::Model, slot_entity);
                } else {
                    scene.world.despawn(slot_entity);
                }
                true
            }
            ComponentData::ScriptComponent(sc_data) => {
                let slot_entity = scene.world.spawn();
                if let Some(h) = &host {
                    if let Some(sc) = ScriptComponent::new(std::sync::Arc::clone(h), sc_data.type_name.clone()) {
                        scene.world.insert(slot_entity, sc);
                        let mut c = 0u32;
                        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                            actor.add_slot_typed::<ScriptComponent>(slot_data.name, ComponentKind::Script, slot_entity);
                        } else { scene.world.despawn(slot_entity); }
                        true
                    } else {
                        scene.world.insert(slot_entity, PlaceholderScriptSlot { script_path: sc_data.type_name });
                        let mut c = 0u32;
                        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                            actor.add_slot_typed::<PlaceholderScriptSlot>(slot_data.name, ComponentKind::Placeholder, slot_entity);
                        } else { scene.world.despawn(slot_entity); }
                        true
                    }
                } else {
                    scene.world.despawn(slot_entity);
                    false
                }
            }
            ComponentData::CanvasComponent(cc_data) => {
                // CanvasComponent を複製して新スロット専用エンティティに insert
                let slot_entity = scene.world.spawn();
                scene.world.insert(slot_entity, CanvasComponent { width: cc_data.width, height: cc_data.height });
                let mut c = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                    actor.add_slot_typed::<CanvasComponent>(slot_data.name, ComponentKind::Canvas, slot_entity);
                } else { scene.world.despawn(slot_entity); }
                true
            }
        };

        self.scene = Some(scene);
        if !slot_added { return; }

        let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: wl,
            actor_dfs_id,
            before_slots,
            after_slots,
        }));

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// カメラ状態をタプル文字列形式で返す（IPC 送信用）。
    /// 戻り値: (pos_str, yaw_deg_str, pitch_deg_str, fov_deg_str, far_str, speed_str)
    fn cam_state_tuple(&self) -> (String, String, String, String, String, String) {
        let pos   = self.camera.base.transform.position;
        let yaw   = self.camera.yaw.to_degrees();
        let pitch = self.camera.pitch.to_degrees();
        let fov   = self.camera.base.projection.fov_y_rad.to_degrees();
        let far   = self.camera.base.projection.far;
        let spd   = self.camera.move_speed;
        (
            format!("{},{},{}", pos.x, pos.y, pos.z),
            format!("{yaw}"),
            format!("{pitch}"),
            format!("{fov}"),
            format!("{far}"),
            format!("{spd}"),
        )
    }

    /// カメラのトランスフォームを位置・yaw/pitch（度）から設定する。
    fn apply_camera_transform(&mut self, px: f32, py: f32, pz: f32, yaw_deg: f32, pitch_deg: f32) {
        const PITCH_LIMIT: f32 = std::f32::consts::FRAC_PI_2 - 0.02;
        self.camera.base.transform.position = Vector3::new(px, py, pz);
        self.camera.yaw   = yaw_deg.to_radians();
        self.camera.pitch = pitch_deg.to_radians().clamp(-PITCH_LIMIT, PITCH_LIMIT);
        let yaw_q   = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), self.camera.yaw);
        let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), self.camera.pitch);
        self.camera.base.transform.rotation = yaw_q * pitch_q;
    }

    /// DebugCameraData をカメラに一括適用する。
    fn apply_camera_data(&mut self, cam: &DebugCameraData) {
        const PITCH_LIMIT: f32 = std::f32::consts::FRAC_PI_2 - 0.02;
        self.camera.base.transform.position = Vector3::new(cam.position[0], cam.position[1], cam.position[2]);
        self.camera.yaw   = cam.yaw;
        self.camera.pitch = cam.pitch.clamp(-PITCH_LIMIT, PITCH_LIMIT);
        let yaw_q   = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), self.camera.yaw);
        let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), self.camera.pitch);
        self.camera.base.transform.rotation = yaw_q * pitch_q;
        self.camera.base.projection.fov_y_rad = cam.fov_deg.to_radians();
        self.camera.base.projection.far = cam.far;
        self.camera.move_speed = cam.speed;
    }

    /// 選択インスタンス／グループを削除し Undo 履歴に記録する。
    ///
    /// - `recursive = true`  → 子孫ごと削除（アクターツリーでは常に子孫ごと削除）
    /// - `recursive = false` → 指定ノードのみ削除（子孫は root へ移動）※現在は recursive と同一動作
    fn apply_delete(&mut self, base_ids: &[u32], _recursive: bool) {
        let wl = self.active_world_line;
        if self.scene.is_none() { return; }

        let before_actors = self.snapshot_actors_for_wl(wl);

        // DFS id を降順にソートし、後ろから削除することで前方のアクターの id ズレを防ぐ
        let mut sorted_desc: Vec<u32> = base_ids.to_vec();
        sorted_desc.sort_unstable_by(|a, b| b.cmp(a));

        {
            let scene = self.scene.as_mut().unwrap();
            for &dfs_id in &sorted_desc {
                // アクターをツリーから取り出し、エンティティを World から despawn する
                let mut extracted: Option<Actor> = None;
                let mut c = 0u32;
                extract_actor_by_dfs(&mut scene.actors, wl, dfs_id, &mut c, &mut extracted);
                if let Some(actor) = extracted {
                    despawn_actor_recursive(&actor, &mut scene.world);
                }
            }
        }

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        // 選択をクリアしてエディタへ通知
        self.selected_instances.clear();
        self.actor_virtual_selected_idx = None;
        self.actor_virtual_selected_slot_idx = 0;
        if let Some(ipc) = &self.ipc { ipc.send("SELECTED:-1"); }
        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 全選択アクター（selected_actor_dfs_ids）のワールド位置重心を返す。
    /// 単一選択・マルチ選択共通で使用する。
    fn selected_actors_centroid(&self) -> Option<[f32; 3]> {
        if self.selected_actor_dfs_ids.is_empty() { return None; }
        let scene = self.scene.as_ref()?;
        let wl = self.active_world_line;
        let mut sum = [0.0f32; 3];
        let mut count = 0usize;
        for &dfs_id in &self.selected_actor_dfs_ids {
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id as u32, &mut c) {
                // MC の instance_mats[0] を優先、なければ ActorTransform.position を使う
                let pos = actor.mc_entity()
                    .and_then(|e| scene.world.get::<ModelComponent>(e))
                    .and_then(|mc| mc.instance_mats.first())
                    .map(|m| [m[0][3], m[1][3], m[2][3]])
                    .or_else(|| scene.world.get::<ActorTransform>(actor.entity).map(|tf| tf.position));
                if let Some(p) = pos {
                    sum[0] += p[0]; sum[1] += p[1]; sum[2] += p[2];
                    count += 1;
                }
            }
        }
        if count > 0 { Some([sum[0] / count as f32, sum[1] / count as f32, sum[2] / count as f32]) }
        else { None }
    }

    /// 選択中アクター/インスタンスのギズモ中心位置を返す共通ヘルパー。
    /// マルチ選択時は全選択アクターの重心を返す。
    fn current_gizmo_pos(&self) -> Option<[f32; 3]> {
        // マルチ選択: 全選択アクターの重心
        if !self.selected_actor_dfs_ids.is_empty() {
            return self.selected_actors_centroid();
        }
        // 単一選択: selected_instances 重心、なければ ActorTransform 位置
        let scene = self.scene.as_ref()?;
        if let Some(dfs) = self.actor_virtual_selected_idx {
            let mut c = 0u32;
            let mc_slot_entity = find_actor_by_dfs(
                &scene.actors, self.active_world_line, dfs as u32, &mut c
            ).and_then(|a| a.mc_entity_at(self.actor_virtual_selected_slot_idx));
            let mc_pos = mc_slot_entity.and_then(|e| scene.world.get::<ModelComponent>(e))
                .and_then(|mc| {
                    if self.selected_instances.is_empty() {
                        mc.instance_mats.first().map(|m| [m[0][3], m[1][3], m[2][3]])
                    } else {
                        selection_centroid(&self.selected_instances, &mc.instance_mats)
                    }
                });
            mc_pos.or_else(|| self.actor_virtual_world_pos())
        } else {
            self.actor_virtual_world_pos()
        }
    }

    /// カーソル座標でギズモのヒットテストを行い、当たったパーツを返す。
    fn compute_gizmo_hover(&self, cx: f32, cy: f32) -> Option<GizmoPart> {
        if self.tool_mode == ToolMode::Select { return None; }
        let gizmo_pos = self.current_gizmo_pos()?;

        let window_size = self.window.as_ref()?.inner_size();
        let cam_pos_v   = self.camera.position();
        let cam_pos     = [cam_pos_v.x, cam_pos_v.y, cam_pos_v.z];

        let d    = [gizmo_pos[0]-cam_pos[0], gizmo_pos[1]-cam_pos[1], gizmo_pos[2]-cam_pos[2]];
        let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
        let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
        let radius   = dist * half_fov.tan() * 0.233;

        let view = self.camera.view_matrix();
        let proj = self.camera.projection_matrix();
        let (ray_o, ray_d) = screen_to_ray(
            cx, cy,
            window_size.width as f32, window_size.height as f32,
            &view.data, &proj.data, cam_pos,
        );
        hit_test_gizmo(ray_o, ray_d, gizmo_pos, radius, self.tool_mode)
    }

    /// カーソル座標でギズモのヒットテストを行い、当たった場合は GizmoDrag を返す。
    /// start_mat には重心を平行移動成分とする単位行列を使う（回転・スケールは各インスタンスが保持）。
    fn try_gizmo_hit_and_start(&self, cx: f32, cy: f32) -> Option<GizmoDrag> {
        if self.tool_mode == ToolMode::Select { return None; }
        let gizmo_pos = self.current_gizmo_pos()?;

        // ギズモ自体の「基準行列」= 重心位置 + 単位回転
        let centroid_mat = [
            [1.0, 0.0, 0.0, gizmo_pos[0]],
            [0.0, 1.0, 0.0, gizmo_pos[1]],
            [0.0, 0.0, 1.0, gizmo_pos[2]],
            [0.0, 0.0, 0.0, 1.0f32],
        ];

        let window_size = self.window.as_ref()?.inner_size();
        let vp_w = window_size.width  as f32;
        let vp_h = window_size.height as f32;

        let cam_pos_v = self.camera.position();
        let cam_pos   = [cam_pos_v.x, cam_pos_v.y, cam_pos_v.z];

        let d    = [gizmo_pos[0]-cam_pos[0], gizmo_pos[1]-cam_pos[1], gizmo_pos[2]-cam_pos[2]];
        let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
        let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
        let radius   = dist * half_fov.tan() * 0.233;

        let view = self.camera.view_matrix();
        let proj = self.camera.projection_matrix();
        let (ray_o, ray_d) = screen_to_ray(cx, cy, vp_w, vp_h, &view.data, &proj.data, cam_pos);

        let part = hit_test_gizmo(ray_o, ray_d, gizmo_pos, radius, self.tool_mode)?;
        Some(start_drag(part, self.tool_mode, ray_o, ray_d, gizmo_pos, radius, centroid_mat))
    }

}

// ============================================================
//  ApplicationHandler
// ============================================================

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        eprintln!("[SEED] resumed() start  parent_hwnd={:?}", self.parent_hwnd);

        let window = Arc::new(create_window(event_loop, &WindowConfig {
            parent_hwnd: self.parent_hwnd,
            ..WindowConfig::default()
        }));
        eprintln!("[SEED] window created");

        let mut renderer = Renderer::new(window.clone());
        eprintln!("[SEED] renderer created");

        let size = window.inner_size();
        self.camera.set_aspect_ratio(size.width, size.height);

        self.camera.base.transform.position = Vector3::new(0.0, 2.0, -10.0);

        let ctx = DrawContext::new(
            renderer.device(),
            renderer.queue(),
            renderer.surface_format(),
            renderer.depth_format(),
        );
        eprintln!("[SEED] DrawContext created");

        let scene = Scene::new("Untitled");
        let camera_buf = ctx.create_camera_buffer();
        let id_buffer  = IdBuffer::new(&ctx.device, size.width, size.height);
        let line_model_buf = ctx.create_identity_model_bg_for_unlit();
        eprintln!("[SEED] scene ready");

        if self.is_embedded() {
            // 非表示ウィンドウへの request_redraw は WM_PAINT が配送されず
            // RedrawRequested が発火しないため、常に可視化してから redraw を要求する。
            // 起動中の白フラッシュはエディタ側コンテナの WM_ERASEBKGND 黒塗りで対処する。
            window.set_visible(true);
            window.request_redraw();
        } else {
            if let Ok(frame) = renderer.begin_frame() { frame.finish(); }
            window.set_visible(true);
        }

        self.draw_ctx      = Some(ctx);
        self.scene         = Some(scene);
        self.camera_buf    = Some(camera_buf);
        self.id_buffer     = Some(id_buffer);
        self.line_model_buf = Some(line_model_buf);

        // 軸ギズモ・アイコンオーバーレイ（エディタモードのみ初期化）
        if self.mode == RuntimeMode::Edit {
            use crate::engine::core::font::axis_gizmo::AxisGizmo;
            use crate::engine::core::font::icon_overlay::IconOverlay;
            let dev = &self.draw_ctx.as_ref().unwrap().device;
            let que = &self.draw_ctx.as_ref().unwrap().queue;
            self.axis_gizmo = Some(AxisGizmo::new(
                dev,
                renderer.surface_format(),
                renderer.depth_format(),
            ));
            self.icon_overlay = Some(IconOverlay::new(
                dev,
                que,
                renderer.surface_format(),
                renderer.depth_format(),
            ));
        }

        self.renderer      = Some(renderer);
        self.window        = Some(window);
        self.clock         = Clock::new();

        self.sync_anim_seeds();

        let hwnd = self.window_hwnd();
        eprintln!("[SEED] sending READY:{hwnd}");
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("READY:{hwnd}"));
        }
        self.send_hierarchy();
        eprintln!("[SEED] resumed() done");
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _id: WindowId,
        event: WindowEvent,
    ) {
        match event {
            WindowEvent::CloseRequested if !self.is_embedded() => {
                if let Some(ipc) = &self.ipc { ipc.send("STOPPED"); }
                event_loop.exit();
            }

            WindowEvent::Resized(size) => {
                if let Some(r) = &mut self.renderer { r.resize(size); }
                self.camera.set_aspect_ratio(size.width, size.height);
                if size.width > 0 && size.height > 0 {
                    if let Some(dc) = &self.draw_ctx {
                        self.id_buffer = Some(IdBuffer::new(&dc.device, size.width, size.height));
                    }
                }
            }

            WindowEvent::KeyboardInput { event, .. } => {
                let pressed = event.state == ElementState::Pressed;
                if let PhysicalKey::Code(key) = event.physical_key {
                    self.input.process_key(key, pressed);

                    match key {
                        KeyCode::ControlLeft | KeyCode::ControlRight => {
                            self.ctrl_held = pressed;
                        }
                        KeyCode::KeyZ if pressed && self.ctrl_held => {
                            let result = if let Some(scene) = &mut self.scene {
                                self.undo_history.undo(scene)
                            } else { None };
                            if let Some((structural, sel)) = result {
                                if let Some(ids) = sel {
                                    self.selected_instances = ids;
                                    self.send_selected();
                                }
                                if structural {
                                    self.sync_anim_seeds();
                                    self.send_hierarchy();
                                }
                            }
                        }
                        KeyCode::KeyY if pressed && self.ctrl_held => {
                            let result = if let Some(scene) = &mut self.scene {
                                self.undo_history.redo(scene)
                            } else { None };
                            if let Some((structural, sel)) = result {
                                if let Some(ids) = sel {
                                    self.selected_instances = ids;
                                    self.send_selected();
                                }
                                if structural {
                                    self.sync_anim_seeds();
                                    self.send_hierarchy();
                                }
                            }
                        }
                        _ => {}
                    }
                }
            }
            WindowEvent::MouseInput { button, state, .. } => {
                let pressed = state == ElementState::Pressed;
                self.input.process_mouse_button(button, pressed);

                if button == winit::event::MouseButton::Left {
                    if pressed
                        && (self.mode == RuntimeMode::Edit || self.paused)
                        && !self.cam_input.rmb
                    {
                        if let Some((cx, cy)) = self.last_cursor_pos {
                            self.lmb_held      = true;
                            self.lmb_press_pos = Some((cx, cy));
                            self.ctrl_at_press = self.ctrl_held;

                            // ギズモヒットを優先。外れた場合は release 時にピックまたは矩形選択。
                            if let Some(drag) = self.try_gizmo_hit_and_start(cx, cy) {
                                let wl                = self.active_world_line;
                                let selected_dfs      = self.actor_virtual_selected_idx;
                                let selected_slot_i   = self.actor_virtual_selected_slot_idx;
                                self.multi_actor_drag_starts.clear();
                                if let Some(scene) = self.scene.as_ref() {
                                    // 選択スロット entity を取得する
                                    // シーンモード・アクター編集モード共通: selected_dfs で選択アクタを特定する
                                    let mc_entity: Option<crate::engine::ecs::Entity> =
                                        selected_dfs.and_then(|dfs| {
                                            let mut c = 0u32;
                                            find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c)
                                                .and_then(|a| a.mc_entity_at(selected_slot_i))
                                        });
                                    if let Some(mc) = mc_entity.and_then(|e| scene.world.get::<ModelComponent>(e)) {
                                        // selected_instances が空（矩形選択・マルチ選択後）の場合は
                                        // インスタンス 0 をドラッグ対象として扱う
                                        let inst_slice: &[u32] = if self.selected_instances.is_empty() { &[0] } else { &self.selected_instances };
                                        let roots = mc.filter_selection_roots(inst_slice);
                                        self.drag_root_starts = roots.iter()
                                            .filter_map(|&i| mc.instance_mats.get(i as usize).map(|&m| (i, m)))
                                            .collect();
                                        self.drag_child_starts = mc.collect_non_root_descendants(&roots);
                                    }
                                    // 選択スロット以外の MC スロット開始行列を収集する
                                    // シーンモード・アクター編集モード共通
                                    if let Some(dfs) = selected_dfs {
                                        let mut c = 0u32;
                                        if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                                            self.actor_extra_mc_drag_starts = actor.slots().iter()
                                                .filter(|s| s.kind == ComponentKind::Model)
                                                .enumerate()
                                                .filter(|&(i, _)| i != selected_slot_i)
                                                .filter_map(|(i, s)| {
                                                    scene.world.get::<ModelComponent>(s.entity)
                                                        .map(|mc| (i, mc.instance_mats.clone()))
                                                })
                                                .collect();
                                        }
                                    }
                                    // 子アクター MC の開始行列を収集する（シーンモード・アクター編集モード共通）
                                    if let Some(dfs) = selected_dfs {
                                        let mut c = 0u32;
                                        if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                                            let mut child_dfs_counter = dfs as u32 + 1;
                                            collect_child_actor_mc_starts(actor, &scene.world, &mut child_dfs_counter, &mut self.actor_child_drag_starts);
                                        }
                                    }
                                    // MC なし（または空）のアクターは Transform を直接動かす（シーンモード・アクター編集モード共通）
                                    if self.drag_root_starts.is_empty() {
                                        if let Some(dfs) = selected_dfs {
                                            let mut c = 0u32;
                                            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                                                let old_tf = scene.world.get::<ActorTransform>(actor.entity)
                                                    .cloned().unwrap_or_default();
                                                self.actor_transform_drag_start = Some((dfs as u32, old_tf));
                                            }
                                        }
                                    }
                                    // マルチ選択: プライマリ以外の選択アクターの開始行列を収集する
                                    if self.selected_actor_dfs_ids.len() > 1 {
                                        for &other_dfs in &self.selected_actor_dfs_ids {
                                            if Some(other_dfs) == selected_dfs { continue; }
                                            let mut c = 0u32;
                                            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, other_dfs as u32, &mut c) {
                                                let start_mat = actor.mc_entity()
                                                    .and_then(|e| scene.world.get::<ModelComponent>(e))
                                                    .and_then(|mc| mc.instance_mats.first().copied())
                                                    .unwrap_or_else(|| {
                                                        scene.world.get::<ActorTransform>(actor.entity)
                                                            .map(|tf| tf.to_mat4())
                                                            .unwrap_or_default()
                                                    });
                                                self.multi_actor_drag_starts.push((other_dfs as u32, start_mat));
                                            }
                                        }
                                    }
                                }
                                self.gizmo_drag = Some(drag);
                            }
                        }
                    }
                    if !pressed {
                        self.lmb_held = false;

                        if self.rect_selecting {
                            // 矩形選択終了: SelectionCommand と ActorDfsSelectionCommand を記録してエディタへ通知
                            let before_inst = std::mem::take(&mut self.selection_before_rect);
                            let after_inst  = self.selected_instances.clone();
                            if before_inst != after_inst {
                                self.undo_history.record(Box::new(SelectionCommand { before: before_inst, after: after_inst }));
                            }
                            let before_dfs     = std::mem::take(&mut self.selection_before_rect_dfs);
                            let before_primary = self.selection_before_rect_primary.take();
                            let after_dfs      = self.selected_actor_dfs_ids.clone();
                            let after_primary  = self.actor_virtual_selected_idx;
                            if before_dfs != after_dfs || before_primary != after_primary {
                                self.undo_history.record(Box::new(ActorDfsSelectionCommand {
                                    before_dfs_ids: before_dfs,
                                    after_dfs_ids:  after_dfs,
                                    before_primary,
                                    after_primary,
                                }));
                            }
                            self.send_selected();
                            // インスペクタへプライマリ選択アクターのコンポーネントをプッシュ
                            if let Some(idx) = self.actor_virtual_selected_idx {
                                self.send_actor_components(idx as u32, self.actor_virtual_selected_slot_idx);
                            }
                            self.rect_selecting = false;
                        } else if self.gizmo_drag.is_none() && self.lmb_press_pos.is_some()
                            && (self.mode == RuntimeMode::Edit || self.paused)
                        {
                            // クリック: ID ピックをスケジュール
                            if let Some((cx, cy)) = self.last_cursor_pos {
                                self.pending_pick = Some((cx as u32, cy as u32));
                            }
                        }
                        self.lmb_press_pos = None;

                        // ドラッグで変化があれば Undo 履歴に一括記録
                        // マルチ選択ドラッグ時にプライマリと非プライマリを CompositeCommand で統合するためのフラグ。
                        // マルチ選択ブロックは gizmo_drag ブロックの外にあるためここで宣言する。
                        let mut primary_recorded = false;
                        if self.gizmo_drag.is_some() {
                            if let Some((dfs_id, old_transform)) = self.actor_transform_drag_start.take() {
                                // アクター編集モード: MC なしアクターの Transform ドラッグ終了
                                let wl = self.active_world_line;
                                let child_drag_starts = std::mem::take(&mut self.actor_child_drag_starts);
                                // entity を取り出してから World で Transform を取得
                                let new_transform_opt = self.scene.as_ref().and_then(|s| {
                                    let mut c = 0u32;
                                    find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c)
                                        .and_then(|a| s.world.get::<ActorTransform>(a.entity).cloned())
                                });
                                if let Some(new_transform) = new_transform_opt {
                                    let delta = mat4x4_mul(new_transform.to_mat4(), mat4x4_inv(old_transform.to_mat4()));
                                    let mut child_transforms: Vec<(u32, ActorTransform, ActorTransform, [[f32;4];4], [[f32;4];4])> = Vec::new();
                                    for (child_dfs, start_mat) in child_drag_starts {
                                        let new_mc_mat = mat4x4_mul(delta, start_mat);
                                        if let Some(scene) = &mut self.scene {
                                            // entity を先に取り出して actors の borrow を解放する
                                            let child_entity = {
                                                let mut c = 0u32;
                                                find_actor_by_dfs(&scene.actors, wl, child_dfs, &mut c)
                                                    .map(|a| a.entity)
                                            };
                                            if let Some(child_entity) = child_entity {
                                                // child.transform はドラッグ中は未更新のため現在値が old_child_tf
                                                let old_child_tf = scene.world.get::<ActorTransform>(child_entity)
                                                    .cloned().unwrap_or_default();
                                                let new_child_tf = ActorTransform::from_mat4(&new_mc_mat);
                                                if let Some(tf) = scene.world.get_mut::<ActorTransform>(child_entity) {
                                                    *tf = new_child_tf.clone();
                                                }
                                                if let Some(mc) = scene.world.get_mut::<ModelComponent>(child_entity) {
                                                    if let Some(m) = mc.instance_mats.first_mut() { *m = new_mc_mat; }
                                                    mc.mark_batch_dirty();
                                                }
                                                child_transforms.push((child_dfs, old_child_tf, new_child_tf, start_mat, new_mc_mat));
                                            }
                                        }
                                    }
                                    if old_transform != new_transform || !child_transforms.is_empty() {
                                        self.undo_history.record(Box::new(ActorGroupTransformCommand {
                                            wl, dfs_id,
                                            old_tf: old_transform,
                                            new_tf: new_transform,
                                            transforms: vec![],
                                            child_transforms,
                                            extra_slot_transforms: vec![],
                                        }));
                                        primary_recorded = true;
                                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                                    }
                                }
                                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                            } else {
                                let mut transforms: Vec<(u32, [[f32;4];4], [[f32;4];4])> = Vec::new();
                                let root_starts       = std::mem::take(&mut self.drag_root_starts);
                                let child_starts      = std::mem::take(&mut self.drag_child_starts);
                                let extra_mc_starts   = std::mem::take(&mut self.actor_extra_mc_drag_starts);
                                let wl_end            = self.active_world_line;
                                let selected_dfs_end  = self.actor_virtual_selected_idx;
                                let selected_slot_i_e = self.actor_virtual_selected_slot_idx;
                                // 選択スロット entity を取得して old→new 変換を収集する
                                // シーンモード・アクター編集モード共通: selected_dfs で選択アクタを特定する
                                let mc_entity: Option<crate::engine::ecs::Entity> = self.scene.as_ref().and_then(|s| {
                                    selected_dfs_end.and_then(|dfs| {
                                        let mut c = 0u32;
                                        find_actor_by_dfs(&s.actors, wl_end, dfs as u32, &mut c)
                                            .and_then(|a| a.mc_entity_at(selected_slot_i_e))
                                    })
                                });
                                if let Some(mc) = mc_entity.and_then(|e| self.scene.as_ref()?.world.get::<ModelComponent>(e)) {
                                    for (idx, old_mat) in root_starts.into_iter().chain(child_starts) {
                                        if let Some(&new_mat) = mc.instance_mats.get(idx as usize) {
                                            if new_mat != old_mat {
                                                transforms.push((idx, old_mat, new_mat));
                                            }
                                        }
                                    }
                                }
                                // 追加 MC スロットの old→new 変換を収集する（Undo 用）
                                // シーンモード・アクター編集モード共通
                                let extra_slot_transforms: Vec<(usize, u32, [[f32;4];4], [[f32;4];4])> =
                                    extra_mc_starts.iter().flat_map(|(slot_i, start_mats)| {
                                        let cur_mats: Vec<[[f32;4];4]> = selected_dfs_end.and_then(|dfs| {
                                            let mut c = 0u32;
                                            self.scene.as_ref().and_then(|s| {
                                                find_actor_by_dfs(&s.actors, wl_end, dfs as u32, &mut c)
                                                    .and_then(|a| a.mc_entity_at(*slot_i))
                                                    .and_then(|e| s.world.get::<ModelComponent>(e))
                                                    .map(|mc| mc.instance_mats.clone())
                                            })
                                        }).unwrap_or_default();
                                        start_mats.iter().zip(cur_mats.iter()).enumerate()
                                            .filter_map(|(i, (old, &new))| {
                                                if *old != new { Some((*slot_i, i as u32, *old, new)) } else { None }
                                            })
                                            .collect::<Vec<_>>()
                                    }).collect();
                                let wl = self.active_world_line;
                                if self.actor_virtual_selected_idx.is_some() {
                                    // 仮想ノード選択中（シーンモード・アクター編集モード共通）:
                                    // delta を Transform と子アクターに反映して ActorGroupTransformCommand で記録。
                                    let dfs_id = self.actor_virtual_selected_idx.unwrap() as u32;
                                    let child_drag_starts = std::mem::take(&mut self.actor_child_drag_starts);
                                    let (old_tf, new_tf, child_transforms) = if let Some(&(_, old_mat, new_mat)) = transforms.first() {
                                        let delta = mat4x4_mul(new_mat, mat4x4_inv(old_mat));
                                        let mut old_v = ActorTransform::default();
                                        let mut new_v = ActorTransform::default();
                                        if let Some(scene) = &mut self.scene {
                                            // entity を先に取り出して actors の borrow を解放する
                                            let entity = {
                                                let mut c = 0u32;
                                                find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c)
                                                    .map(|a| a.entity)
                                            };
                                            if let Some(entity) = entity {
                                                old_v = scene.world.get::<ActorTransform>(entity).cloned().unwrap_or_default();
                                                new_v = ActorTransform::from_mat4(&mat4x4_mul(delta, old_v.to_mat4()));
                                                if let Some(tf) = scene.world.get_mut::<ActorTransform>(entity) { *tf = new_v.clone(); }
                                            }
                                        }
                                        // 子アクターの Transform を更新し Undo 用データを収集
                                        let mut child_transforms = Vec::new();
                                        for (child_dfs, start_mat) in child_drag_starts {
                                            if let Some(scene) = &mut self.scene {
                                                // actor.entity（Transform 用）とスロット entity（MC 用）を別々に取得する
                                                let (child_entity_opt, mc_slot_entity) = {
                                                    let mut c = 0u32;
                                                    find_actor_by_dfs(&scene.actors, wl, child_dfs, &mut c)
                                                        .map(|a| (Some(a.entity), a.mc_entity()))
                                                        .unwrap_or((None, None))
                                                };
                                                if let Some(child_entity) = child_entity_opt {
                                                    let old_child_tf = scene.world.get::<ActorTransform>(child_entity)
                                                        .cloned().unwrap_or_default();
                                                    let new_child_tf = ActorTransform::from_mat4(
                                                        &mat4x4_mul(delta, old_child_tf.to_mat4()));
                                                    if let Some(tf) = scene.world.get_mut::<ActorTransform>(child_entity) {
                                                        *tf = new_child_tf.clone();
                                                    }
                                                    let new_mc_mat = mc_slot_entity
                                                        .and_then(|e| scene.world.get::<ModelComponent>(e))
                                                        .and_then(|mc| mc.instance_mats.first().copied())
                                                        .unwrap_or(start_mat);
                                                    child_transforms.push((child_dfs, old_child_tf, new_child_tf, start_mat, new_mc_mat));
                                                }
                                            }
                                        }
                                        (old_v, new_v, child_transforms)
                                    } else {
                                        // インスタンス変化なし: 現在の Transform を取得
                                        let tf = self.scene.as_ref().and_then(|s| {
                                            let mut c = 0u32;
                                            find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c)
                                                .and_then(|a| s.world.get::<ActorTransform>(a.entity).cloned())
                                        }).unwrap_or_default();
                                        (tf.clone(), tf, Vec::new())
                                    };
                                    self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                                    if !transforms.is_empty() || !extra_slot_transforms.is_empty() {
                                        self.undo_history.record(Box::new(ActorGroupTransformCommand {
                                            wl, dfs_id, old_tf, new_tf, transforms,
                                            child_transforms, extra_slot_transforms,
                                        }));
                                        primary_recorded = true;
                                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                                    }
                                } else {
                                    if !transforms.is_empty() {
                                        self.undo_history.record(Box::new(MultiTransformCommand { transforms }));
                                        primary_recorded = true;
                                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                                    }
                                    if self.selected_instances.len() == 1 {
                                        self.send_actor_data(self.selected_instances[0]);
                                    }
                                }
                            }
                        } else {
                            self.actor_transform_drag_start = None;
                            self.drag_root_starts.clear();
                            self.drag_child_starts.clear();
                            self.actor_child_drag_starts.clear();
                            self.actor_extra_mc_drag_starts.clear();
                        }
                        // マルチ選択ドラッグ終了: 非プライマリアクターの Transform を記録する
                        // プライマリのコマンドが直前に記録済みなら CompositeCommand で一括化して
                        // Ctrl+Z 1回で全アクターの変換が同時に Undo できるようにする。
                        if !self.multi_actor_drag_starts.is_empty() {
                            let wl = self.active_world_line;
                            let mut drag_transforms: Vec<(u32, [[f32; 4]; 4], [[f32; 4]; 4])> = Vec::new();
                            if let Some(scene) = self.scene.as_ref() {
                                for &(other_dfs, old_mat) in &self.multi_actor_drag_starts {
                                    let mut c = 0u32;
                                    let new_mat = find_actor_by_dfs(&scene.actors, wl, other_dfs, &mut c)
                                        .and_then(|a| a.mc_entity()
                                            .and_then(|e| scene.world.get::<ModelComponent>(e))
                                            .and_then(|mc| mc.instance_mats.first().copied())
                                            .or_else(|| scene.world.get::<ActorTransform>(a.entity)
                                                .map(|tf| tf.to_mat4())))
                                        .unwrap_or(old_mat);
                                    drag_transforms.push((other_dfs, old_mat, new_mat));
                                }
                            }
                            if !drag_transforms.is_empty() {
                                let multi_cmd: Box<dyn crate::engine::core::app_base::undo::Command> =
                                    Box::new(MultiActorDragTransformCommand { wl, transforms: drag_transforms });
                                if primary_recorded {
                                    // プライマリのコマンドを取り出して CompositeCommand に統合する
                                    if let Some(primary_cmd) = self.undo_history.pop_last() {
                                        self.undo_history.record(Box::new(CompositeCommand {
                                            commands: vec![primary_cmd, multi_cmd],
                                        }));
                                    } else {
                                        // pop できなかった場合は単独で記録
                                        self.undo_history.record(multi_cmd);
                                    }
                                } else {
                                    self.undo_history.record(multi_cmd);
                                }
                                if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                            }
                            self.multi_actor_drag_starts.clear();
                        }
                        self.gizmo_drag = None;
                        // ドラッグ終了後はホバーを再評価する
                        self.hovered_gizmo_part = self.last_cursor_pos
                            .and_then(|(cx, cy)| self.compute_gizmo_hover(cx, cy));
                    }
                }

                if button == winit::event::MouseButton::Right {
                    self.cam_input.rmb = pressed;
                    // カメラ grab は Edit / Pause モードのみ。
                    // Play モードでは editor 側が ClipCursor を管理するため
                    // ここで ClipCursor(null) を呼ばないようにする。
                    if self.mode == RuntimeMode::Edit || self.paused {
                        if let Some(window) = &self.window {
                            if pressed {
                                self.rmb_press_pos = self.last_cursor_pos;
                                self.rmb_moved     = false;
                                self.cam_grab_screen_pos =
                                    camera_grab_start(self.window_hwnd());
                                window.set_cursor_visible(false);
                            } else {
                                window.set_cursor_visible(true);
                                if let Some((x, y)) = self.cam_grab_screen_pos.take() {
                                    camera_grab_end(x, y);
                                    // 短押し（カーソル移動なし）→ コンテキストメニュー
                                    if !self.rmb_moved {
                                        if let Some(ipc) = &self.ipc {
                                            ipc.send("CONTEXT_MENU");
                                        }
                                    }
                                } else {
                                    // RMB_DOWN が処理されていない（コンテキストメニューの
                                    // ポップアップが WM_RBUTTONDOWN を横取りした等）。
                                    // ClipCursor のみ解除し SetCursorPos(0,0) は呼ばない。
                                    release_window_clamp();
                                }
                                self.rmb_press_pos = None;
                                self.rmb_moved     = false;
                            }
                        }
                    }
                }
            }
            WindowEvent::CursorMoved { position, .. } => {
                let cx = position.x as f32;
                let cy = position.y as f32;
                self.input.process_cursor_moved(cx, cy);
                self.last_cursor_pos = Some((cx, cy));

                // RMB 押下中に移動量が閾値を超えたらカメラ grab とみなす
                if self.cam_input.rmb && !self.rmb_moved {
                    if let Some((px, py)) = self.rmb_press_pos {
                        let dx = cx - px;
                        let dy = cy - py;
                        if dx * dx + dy * dy > 25.0 {
                            self.rmb_moved = true;
                        }
                    }
                }

                // 矩形選択の更新（LMB 押下中かつギズモドラッグなし）
                if self.lmb_held && self.gizmo_drag.is_none() {
                    if let Some((px, py)) = self.lmb_press_pos {
                        let dx = cx - px;
                        let dy = cy - py;
                        if !self.rect_selecting && dx * dx + dy * dy > 25.0 {
                            self.rect_selecting = true;
                            self.selection_before_rect         = self.selected_instances.clone();
                            self.selection_before_rect_dfs     = self.selected_actor_dfs_ids.clone();
                            self.selection_before_rect_primary = self.actor_virtual_selected_idx;
                        }
                        if self.rect_selecting {
                            let sx_min = px.min(cx);
                            let sx_max = px.max(cx);
                            let sy_min = py.min(cy);
                            let sy_max = py.max(cy);
                            if let Some(scene) = &self.scene {
                                // アクターツリー全 MC を走査して矩形内のアクターを収集する
                                if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                                    let wl   = self.active_world_line;
                                    let vp_w = ws.width as f32;
                                    let vp_h = ws.height as f32;
                                    let view = self.camera.view_matrix();
                                    let proj = self.camera.projection_matrix();
                                    let all_mcs = collect_mcs_in_world_line(&scene.actors, &scene.world, wl);
                                    let mut rect_dfs: Vec<usize> = Vec::new();
                                    for (_base, dfs_id, _slot_i, mc) in all_mcs {
                                        let in_rect = mc.instance_mats.iter().any(|m| {
                                            let world = [m[0][3], m[1][3], m[2][3]];
                                            world_to_screen(world, &view.data, &proj.data, vp_w, vp_h)
                                                .map(|(sx, sy)| sx >= sx_min && sx <= sx_max && sy >= sy_min && sy <= sy_max)
                                                .unwrap_or(false)
                                        });
                                        if in_rect && !rect_dfs.contains(&(dfs_id as usize)) {
                                            rect_dfs.push(dfs_id as usize);
                                        }
                                    }
                                    self.selected_actor_dfs_ids     = rect_dfs.clone();
                                    self.actor_virtual_selected_idx = rect_dfs.last().copied();
                                    self.selected_instances.clear();
                                }
                            }
                        }
                    }
                }

                // ホバーパーツを更新（ドラッグ中はドラッグパーツを維持）
                self.hovered_gizmo_part = if let Some(drag) = &self.gizmo_drag {
                    Some(drag.part)
                } else {
                    self.compute_gizmo_hover(cx, cy)
                };

                // ギズモドラッグ中: 新しい変換行列を計算してインスタンスに適用する
                let new_mat_opt = if let Some(drag) = &self.gizmo_drag {
                    if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                        let cam_v  = self.camera.position();
                        let cam    = [cam_v.x, cam_v.y, cam_v.z];
                        let view   = self.camera.view_matrix();
                        let proj   = self.camera.projection_matrix();
                        let (ro, rd) = screen_to_ray(
                            cx, cy,
                            ws.width as f32, ws.height as f32,
                            &view.data, &proj.data, cam,
                        );
                        Some(update_drag(drag, ro, rd))
                    } else { None }
                } else { None };

                if let Some(new_mat) = new_mat_opt {
                    if let Some(drag) = &self.gizmo_drag {
                        let delta        = mat4x4_mul(new_mat, mat4x4_inv(drag.start_mat));
                        let wl           = self.active_world_line;
                        let selected_dfs = self.actor_virtual_selected_idx;

                        if let Some(scene) = &mut self.scene {
                            // 選択スロット entity を取得して MC 行列にデルタを適用する
                            // シーンモード・アクター編集モード共通: selected_dfs で選択アクタを特定する
                            let selected_slot_i = self.actor_virtual_selected_slot_idx;
                            let mc_entity: Option<crate::engine::ecs::Entity> =
                                if let Some(dfs) = selected_dfs {
                                    let mut c = 0u32;
                                    find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c)
                                        .and_then(|a| a.mc_entity_at(selected_slot_i))
                                } else { None };
                            if let Some(mc) = mc_entity.and_then(|e| scene.world.get_mut::<ModelComponent>(e)) {
                                for &(idx, ref start) in &self.drag_root_starts {
                                    if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
                                        *m = mat4x4_mul(delta, *start);
                                    }
                                }
                                for &(idx, ref start) in &self.drag_child_starts {
                                    if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
                                        *m = mat4x4_mul(delta, *start);
                                    }
                                }
                                mc.mark_batch_dirty();
                            }
                            // 追加 MC スロット（選択スロット以外）にも同デルタを適用する
                            // 追加 MC スロット（選択スロット以外）にも同デルタを適用する
                            // シーンモード・アクター編集モード共通
                            if let Some(dfs) = selected_dfs {
                                let extra_starts = self.actor_extra_mc_drag_starts.clone();
                                for (slot_i, start_mats) in &extra_starts {
                                    let mut c = 0u32;
                                    let slot_entity = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c)
                                        .and_then(|a| a.mc_entity_at(*slot_i));
                                    if let Some(entity) = slot_entity {
                                        if let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) {
                                            for (m, start) in mc.instance_mats.iter_mut().zip(start_mats.iter()) {
                                                *m = mat4x4_mul(delta, *start);
                                            }
                                            mc.mark_batch_dirty();
                                        }
                                    }
                                }
                            }
                            // MC なし（インスタンス空含む）のアクターは
                            // Transform を直接ドラッグ開始時の値にデルタを掛けて更新する（共通）
                            if self.drag_root_starts.is_empty() {
                                if let Some((drag_dfs, ref start_tf)) = self.actor_transform_drag_start.clone() {
                                    let new_mat = mat4x4_mul(delta, start_tf.to_mat4());
                                    let entity = {
                                        let mut c = 0u32;
                                        find_actor_by_dfs(&scene.actors, wl, drag_dfs, &mut c)
                                            .map(|a| a.entity)
                                    };
                                    if let Some(entity) = entity {
                                        if let Some(tf) = scene.world.get_mut::<ActorTransform>(entity) {
                                            *tf = ActorTransform::from_mat4(&new_mat);
                                        }
                                    }
                                }
                            }
                            // 子アクター MC にも同デルタを適用する（共通）
                            {
                                let child_starts = self.actor_child_drag_starts.clone();
                                for (child_dfs, start_mat) in &child_starts {
                                    // スロット entity 経由で子アクターの MC を更新する
                                    let mc_slot_entity = {
                                        let mut c = 0u32;
                                        find_actor_by_dfs(&scene.actors, wl, *child_dfs, &mut c)
                                            .and_then(|a| a.mc_entity())
                                    };
                                    if let Some(entity) = mc_slot_entity {
                                        if let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) {
                                            if let Some(m) = mc.instance_mats.first_mut() {
                                                *m = mat4x4_mul(delta, *start_mat);
                                            }
                                            mc.mark_batch_dirty();
                                        }
                                    }
                                }
                            }
                            // マルチ選択: プライマリ以外の全選択アクターにも同デルタを適用する
                            if !self.multi_actor_drag_starts.is_empty() {
                                let multi_starts = self.multi_actor_drag_starts.clone();
                                for (other_dfs, start_mat) in &multi_starts {
                                    let mut c = 0u32;
                                    if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, *other_dfs, &mut c) {
                                        let new_mat = mat4x4_mul(delta, *start_mat);
                                        let actor_entity = actor.entity;
                                        let mc_entity = actor.mc_entity();
                                        // MC の instance_mats を更新する（GPU 描画位置）
                                        if let Some(me) = mc_entity {
                                            if let Some(mc) = scene.world.get_mut::<ModelComponent>(me) {
                                                if let Some(m) = mc.instance_mats.first_mut() {
                                                    *m = new_mat;
                                                }
                                                mc.mark_batch_dirty();
                                            }
                                        }
                                        // ActorTransform も更新する（Inspector 反映用）
                                        if let Some(tf) = scene.world.get_mut::<ActorTransform>(actor_entity) {
                                            *tf = ActorTransform::from_mat4(&new_mat);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    // ドラッグ中のリアルタイム IPC 送信は廃止（ドラッグ終了時に送信）。
                    // 毎マウスムーブで ipc.send() するとブロッキングで重くなるため。
                }
            }
            WindowEvent::MouseWheel { delta, .. } => {
                self.input.process_scroll(&delta);
                let lines = match delta {
                    winit::event::MouseScrollDelta::LineDelta(_, y) => y,
                    winit::event::MouseScrollDelta::PixelDelta(p) => p.y as f32 / 20.0,
                };
                self.cam_input.scroll += lines;
            }

            // ─── メインループ ─────────────────────────────────
            WindowEvent::RedrawRequested => {
                self.process_ipc(event_loop);

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

                // ── 時間 ──────────────────────────────────────
                let time_running = self.mode == RuntimeMode::Play && !self.paused;
                let ctx: FrameContext = self.clock.tick(time_running);
                let in_editor = self.mode == RuntimeMode::Edit || self.paused;
                // 現在の世界線が 2D キャンバスモードかどうか
                let is_canvas = self.canvas_world_lines.contains(&self.active_world_line);

                if in_editor {
                    if is_canvas {
                        // 2D キャンバスモード: RMB ドラッグで XY パン、スクロールでズーム
                        if self.cam_input.rmb {
                            // ウィンドウサイズからピクセルあたりのワールドユニットを算出
                            let ws = self.window.as_ref().map(|w| {
                                let s = w.inner_size();
                                [s.width as f32, s.height as f32]
                            }).unwrap_or([1280.0, 720.0]);
                            let cam_2d = self.canvas_cameras
                                .entry(self.active_world_line)
                                .or_insert_with(CanvasCameraData::default);
                            // ビューポート高さあたりのワールドユニット（スケール係数）
                            let scale = (2.0 * cam_2d.ortho_half_h) / ws[1];
                            // raw delta: right/up が正なので pan は符号反転する
                            cam_2d.pan_x -= self.cam_input.mouse_dx * scale;
                            cam_2d.pan_y += self.cam_input.mouse_dy * scale;
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
                        // 3D モード: 通常のデバッグカメラ更新
                        self.camera.update(&self.cam_input, ctx.delta_time);
                    }
                }

                // ─ 1-6. ゲームロジック（Play 時のみ）─────────
                if time_running {
                    if let Some(scene) = &mut self.scene { scene.begin_frame(&ctx); }
                    if let Some(scene) = &mut self.scene { scene.early_update(&ctx); }
                    if let Some(scene) = &mut self.scene { scene.update(&ctx); }
                    for fixed_ctx in self.clock.drain_fixed() {
                        if let Some(scene) = &mut self.scene { scene.constant_update(&fixed_ctx); }
                    }
                    if let Some(scene) = &mut self.scene { scene.late_update(&ctx); }
                    if let Some(scene) = &mut self.scene { scene.render(&ctx); }
                }

                // ── GPU カメラ・インスタンスバッファ更新 ──────
                let window_size = self.window.as_ref().map(|w| w.inner_size());
                let queue = self.draw_ctx.as_ref().map(|c| c.queue.clone());

                if let (Some(scene), Some(camera_buf), Some(queue)) =
                    (&mut self.scene, &self.camera_buf, queue)
                {
                    // 2D キャンバスモードと 3D モードでビュー行列・射影行列を切り替える
                    let (view, proj, cam_pos_arr) = if is_canvas {
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
                        // near=0, far=200 で Z=0 作業平面（z_view=100）を視野中央に収める
                        let p = Mat4x4::orthographic_lh(-half_w, half_w, -half_h, half_h, 0.0, 200.0);
                        (v, p, [cam_2d.pan_x, cam_2d.pan_y, -100.0])
                    } else {
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

                    let frustum_planes = extract_frustum_planes(&view_proj.data);
                    let camera_pos     = cam_pos_arr;

                    // シーンモード・アクター編集モード共通: world_line 全 MC を DFS で更新する
                    let (actors, world) = (&mut scene.actors, &mut scene.world);
                    update_all_mc_batches_for_wl(
                        actors, world, self.active_world_line,
                        &queue, &frustum_planes, camera_pos, self.clock.anim_time(),
                    );
                }

                // ── ギズモ位置：全選択アクターの重心（マルチ選択対応） ──
                let gizmo_pos = self.selected_actors_centroid()
                    .or_else(|| self.actor_virtual_world_pos());

                // アクター仮想選択のワールド位置（レンダラー借用外で取得）
                // シーンモード・アクター編集モード共通: actor_virtual_selected_idx があれば取得する
                let actor_virtual_pos: Option<[f32; 3]> = if self.actor_virtual_selected_idx.is_some() {
                    self.actor_virtual_world_pos()
                } else { None };

                // ── ドラッグホバープレビュー位置の更新（レンダー前）──────────
                // GPU リードバックを使わずレイキャストで直接ワールド座標を算出する。
                // y=0 平面との交差を優先し、カメラが平行または後方なら DEFAULT_DIST 先。
                // レンダー前に計算することで同一フレーム内に球体を描画できる。
                if let Some((hsx, hsy)) = self.pending_drop_hover.take() {
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
                                // 地面と交差する場合: その交点
                                [cam[0]+rd[0]*t, 0.0, cam[2]+rd[2]*t]
                            } else {
                                // カメラが地面以下または後方交差: DEFAULT_DIST 先
                                [cam[0]+rd[0]*DEFAULT_DIST, cam[1]+rd[1]*DEFAULT_DIST, cam[2]+rd[2]*DEFAULT_DIST]
                            }
                        } else {
                            // レイが y 軸と平行: DEFAULT_DIST 先
                            [cam[0]+rd[0]*DEFAULT_DIST, cam[1]+rd[1]*DEFAULT_DIST, cam[2]+rd[2]*DEFAULT_DIST]
                        };
                        self.drop_preview_pos = Some(pos);
                    }
                }

                // ピック要求を取り出す（描画ブロック内で使用）
                let pick_pos = self.pending_pick.take();
                let mut did_pick = false;

                // ピック結果デコード用 MC 情報 (base, dfs_id, slot_i, instance_count)
                // render block 後に参照するためここで収集しておく
                // シーンモード・アクター編集モード共通で全 MC を収集する
                let wl_mc_pick_infos: Vec<(u32, u32, usize, usize)> = {
                    if let Some(scene) = &self.scene {
                        collect_mcs_in_world_line(&scene.actors, &scene.world, self.active_world_line)
                            .into_iter()
                            .map(|(base, dfs, slot_i, mc)| (base, dfs, slot_i, mc.instance_mats.len()))
                            .collect()
                    } else { vec![] }
                };

                if let (Some(renderer), Some(scene), Some(camera_buf), Some(draw_ctx)) =
                    (&mut self.renderer, &self.scene, &self.camera_buf, &self.draw_ctx)
                {
                    match renderer.begin_frame() {
                        Ok(mut frame) => {
                            // シーンモード・アクター編集モード共通: world_line の全 MC を収集する
                            // タプル: (id_base, dfs_id, slot_i, &ModelComponent)
                            let all_mcs: Vec<(u32, u32, usize, &ModelComponent)> =
                                collect_mcs_in_world_line(&scene.actors, &scene.world, self.active_world_line);
                            // 後方互換: 単一 MC として使う箇所用（シーン編集モード or 先頭 MC）
                            let mc = all_mcs.first().map(|&(_, _, _, mc)| mc);
                            // 選択中アクターの MC（アウトライン・アイコン用）
                            // シーンモード・アクター編集モード共通: DFS id と slot_i で一致する MC を取得する
                            let selected_slot_i = self.actor_virtual_selected_slot_idx;
                            let selected_mc: Option<&ModelComponent> =
                                self.actor_virtual_selected_idx
                                    .and_then(|dfs| all_mcs.iter()
                                        .find(|&&(_, d, si, _)| d == dfs as u32 && si == selected_slot_i)
                                        .map(|&(_, _, _, mc)| mc));

                            // スキンメッシュコンピュート: 全 MC に対して実行
                            for &(_, _, _, amc) in &all_mcs {
                                if let Some(batch) = amc.instanced_batch.as_ref() {
                                    batch.dispatch_skin(
                                        frame.encoder_mut(),
                                        &draw_ctx.pipelines.skin_compute,
                                    );
                                }
                            }

                                // ギズモ GPU バッファ（レンダーパスの前に生成）
                                let show_gizmo_pre = self.mode == RuntimeMode::Edit || self.paused;
                                let gizmo_gpu_batch = if show_gizmo_pre
                                    && self.tool_mode != ToolMode::Select
                                {
                                    gizmo_pos.map(|pos| {
                                        // カメラ距離に比例したスクリーン固定サイズ
                                        // radius = dist * tan(fov_y/2) * NDC_fraction
                                        let cam_pos  = self.camera.position();
                                        let d = [pos[0]-cam_pos.x, pos[1]-cam_pos.y, pos[2]-cam_pos.z];
                                        let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
                                        let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
                                        let radius = dist * half_fov.tan() * 0.233;

                                        let cam_pos_arr = [cam_pos.x, cam_pos.y, cam_pos.z];
                                        let hov  = self.hovered_gizmo_part;
                                        let drag_part = self.gizmo_drag.as_ref().map(|d| d.part);
                                        let mut batch = GizmoBatch::new();
                                        match self.tool_mode {
                                            ToolMode::Move   => batch.add_gizmo_translate(pos, radius, hov),
                                            ToolMode::Rotate => batch.add_gizmo_rotate(pos, radius, 64, cam_pos_arr, hov, drag_part),
                                            ToolMode::Scale  => batch.add_gizmo_scale(pos, radius, hov),
                                            ToolMode::Select => {}
                                        }
                                        batch.build(&draw_ctx.device)
                                    })
                                } else { None };

                                // 矩形選択ビジュアル（レンダーパスの前に GPU バッファを生成）
                                let rect_gpu_batch = if in_editor && self.rect_selecting {
                                    if let (Some((px, py)), Some((cx, cy))) =
                                        (self.lmb_press_pos, self.last_cursor_pos)
                                    {
                                        let vp_w  = window_size.map_or(1280.0, |s| s.width  as f32);
                                        let vp_h  = window_size.map_or(720.0,  |s| s.height as f32);
                                        let view  = self.camera.view_matrix();
                                        let proj  = self.camera.projection_matrix();
                                        let cam_pv = self.camera.position();
                                        let cam_pos = [cam_pv.x, cam_pv.y, cam_pv.z];
                                        // near plane より少し前に置く。
                                        // view-space 深度 = near * 1.05 で確実に near 面の内側に収まる。
                                        let near_vs = self.camera.base.projection.near * 1.05;
                                        let sc = [
                                            (px.min(cx), py.min(cy)), // TL
                                            (px.max(cx), py.min(cy)), // TR
                                            (px.max(cx), py.max(cy)), // BR
                                            (px.min(cx), py.max(cy)), // BL
                                        ];
                                        let p = &proj.data;
                                        let v = &view.data;
                                        let mut wp = [[0.0f32; 3]; 4];
                                        for (i, &(sx, sy)) in sc.iter().enumerate() {
                                            // スクリーン → NDC
                                            let nx = 2.0 * sx / vp_w - 1.0;
                                            let ny = 1.0 - 2.0 * sy / vp_h;
                                            // ビュー空間方向（z=1 スケール）
                                            let vdx = nx / p[0][0];
                                            let vdy = ny / p[1][1];
                                            // ビュー空間座標を near_vs にスケール（z = near_vs）
                                            let vpx = vdx * near_vs;
                                            let vpy = vdy * near_vs;
                                            let vpz = near_vs;
                                            // ビュー空間 → ワールド空間: world = cam_pos + V^T * vp
                                            wp[i] = [
                                                cam_pos[0] + v[0][0]*vpx + v[1][0]*vpy + v[2][0]*vpz,
                                                cam_pos[1] + v[0][1]*vpx + v[1][1]*vpy + v[2][1]*vpz,
                                                cam_pos[2] + v[0][2]*vpx + v[1][2]*vpy + v[2][2]*vpz,
                                            ];
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
                                // 固定半径のソリッドスフィアを配置予定位置に描画する
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
                                // アクター編集モードはグリッドを常時表示し、紺背景に映えるよう色を調整
                                // 2D キャンバスモードは XY 平面グリッド（Z=0）、3D は XZ 平面グリッド（Y=0）
                                let grid_gpu_batch = if in_editor && (self.show_grid || self.active_world_line != 0) {
                                    let mut lb = LineBatch::new();
                                    let (minor, major): ([f32; 4], [f32; 4]) = if self.active_world_line != 0 {
                                        ([0.22, 0.25, 0.40, 1.0], [0.32, 0.36, 0.55, 1.0])
                                    } else {
                                        ([0.18, 0.18, 0.18, 1.0], [0.30, 0.30, 0.30, 1.0])
                                    };
                                    let ax_x: [f32; 4] = [0.50, 0.10, 0.10, 1.0];

                                    if is_canvas {
                                        // 2D モード: XY 平面グリッド（Z=0）
                                        // ax_y: 緑色の Y 軸線
                                        let ax_y: [f32; 4] = [0.10, 0.50, 0.10, 1.0];
                                        let n    = 10i32;
                                        let step = 5.0f32;
                                        let ext  = n as f32 * step;
                                        for i in -n..=n {
                                            let p = i as f32 * step;
                                            if i == 0 {
                                                lb.add_line([-ext, 0.0, 0.0], [ext, 0.0, 0.0], ax_x);
                                                lb.add_line([0.0, -ext, 0.0], [0.0, ext, 0.0], ax_y);
                                            } else {
                                                let c = if i % 2 == 0 { major } else { minor };
                                                // 水平線（Y 方向に並ぶ）
                                                lb.add_line([-ext, p, 0.0], [ext, p, 0.0], c);
                                                // 垂直線（X 方向に並ぶ）
                                                lb.add_line([p, -ext, 0.0], [p, ext, 0.0], c);
                                            }
                                        }
                                    } else {
                                        // ──────────────────────────────────────────────────────
                                        // 3D モード: XZ 平面グリッド（Y=0）
                                        //
                                        // ■ カメラ追従: step 単位にスナップしてカメラ XZ に追従。
                                        // ■ 深度フェード: 各頂点の 3D 距離で alpha を計算し
                                        //   add_line_grad で両端点に渡す。GPU 補間で自然な消え。
                                        // ■ スケール: カメラ Y 絶対値でグリッド単位を段階切り替え。
                                        //   minor ライン alpha は区間内で lerp（始端=濃, 終端=淡）。
                                        // ──────────────────────────────────────────────────────
                                        let cam_pos = self.camera.base.transform.position;
                                        let cam_y   = cam_pos[1].abs();
                                        let cam_far = self.camera.base.projection.far;
                                        let ax_z: [f32; 4] = [0.10, 0.10, 0.50, 1.0];

                                        // ── グリッドスケール選択 ─────────────────────────────
                                        // (step, thick_period, tier_y_start, tier_y_end)
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

                                        // ── minor ライン alpha: 区間始端=1.0 → 区間終端≈0.0 ──
                                        let minor_alpha_base: f32 = if tier_end.is_finite() {
                                            (1.0 - (cam_y - tier_start) / (tier_end - tier_start)).max(0.0)
                                        } else {
                                            1.0
                                        };

                                        // ── グリッド範囲とカメラ追従スナップ ────────────────
                                        // n_half を cam_far / step まで伸ばすことで、グリッドが
                                        // far 距離まで描画され四隅の硬い切り口が出なくなる。
                                        // step が小さい（0.1）場合は 300 本上限で十分（近接作業）。
                                        let max_lines: i32 = if step < 0.5 { 300 } else { 2000 };
                                        let n_half: i32 = ((cam_far / step) as i32).min(max_lines);
                                        let ext    = n_half as f32 * step;
                                        let snap_x = (cam_pos[0] / step).floor() * step;
                                        let snap_z = (cam_pos[2] / step).floor() * step;

                                        // ── XZ 距離ベースフェード ────────────────────────────
                                        // グリッドライン端点はどちらも XZ 距離 ≥ ext になるため
                                        // 端点 alpha だけを見ると常に 0 になる問題がある。
                                        //
                                        // 解決策: 各ラインをカメラの XZ 投影点でスプリットし
                                        // 「端=0 → スプリット点=peak → 端=0」の2セグメント描画。
                                        // peak_a = fade_xz(垂直距離) でライン種別 × 側方距離に依存。
                                        //
                                        // fade_xz: pow=2（2乗フェード）でフェード変化を視覚的に
                                        // 分かりやすくする（地平線方向からでも差異が見えやすい）
                                        let fade_xz = |d: f32| -> f32 {
                                            (1.0 - (d / ext).powi(2)).clamp(0.0, 1.0)
                                        };
                                        // カメラ XZ 投影点（グリッド範囲内にクランプ）
                                        let split_x = cam_pos[0].clamp(snap_x - ext, snap_x + ext);
                                        let split_z = cam_pos[2].clamp(snap_z - ext, snap_z + ext);

                                        for i in -n_half..=n_half {
                                            // ── Z 方向ライン（X = world_x で固定）────────────
                                            // 垂直距離 = |world_x - cam_x|（これがピーク alpha を決める）
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
                                                    // セグメント1: 後端(0) → スプリット点(peak_a)
                                                    lb.add_line_grad(
                                                        [world_x, 0.0, snap_z - ext],
                                                        [world_x, 0.0, split_z],
                                                        [rgb[0], rgb[1], rgb[2], 0.0],
                                                        [rgb[0], rgb[1], rgb[2], peak_a],
                                                    );
                                                    // セグメント2: スプリット点(peak_a) → 前端(0)
                                                    lb.add_line_grad(
                                                        [world_x, 0.0, split_z],
                                                        [world_x, 0.0, snap_z + ext],
                                                        [rgb[0], rgb[1], rgb[2], peak_a],
                                                        [rgb[0], rgb[1], rgb[2], 0.0],
                                                    );
                                                }
                                            }

                                            // ── X 方向ライン（Z = world_z で固定）────────────
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
                                                    // セグメント1: 左端(0) → スプリット点(peak_a)
                                                    lb.add_line_grad(
                                                        [snap_x - ext, 0.0, world_z],
                                                        [split_x,      0.0, world_z],
                                                        [rgb[0], rgb[1], rgb[2], 0.0],
                                                        [rgb[0], rgb[1], rgb[2], peak_a],
                                                    );
                                                    // セグメント2: スプリット点(peak_a) → 右端(0)
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

                                // CanvasComponent 矩形アウトラインバッチ（エディタモード + 2D キャンバス世界線のみ）
                                // CanvasComponent を持つ各 Actor の CanvasTransform 位置を中心に
                                // width × height の矩形を破線ではなく実線で描画する。
                                let canvas_rect_batch = if in_editor && is_canvas {
                                    if let Some(scene) = &self.scene {
                                        let wl = self.active_world_line;
                                        let mut lb = LineBatch::new();
                                        // 矩形色: 明るいシアン（グリッドの青系と区別しやすい）
                                        let rect_col: [f32; 4] = [0.85, 0.95, 1.0, 0.9];

                                        // DFS で世界線内の全 Actor を走査して CanvasComponent を収集する
                                        fn collect_canvas_rects(
                                            actors: &[crate::engine::structs::objects::Actor],
                                            world:  &crate::engine::ecs::World,
                                            wl:     u32,
                                            lb:     &mut LineBatch,
                                            col:    [f32; 4],
                                        ) {
                                            for actor in actors {
                                                if actor.world_line != wl { continue; }
                                                // CanvasTransform から中心位置を取得
                                                let pos = world.get::<CanvasTransform>(actor.entity)
                                                    .map(|ct| ct.position)
                                                    .unwrap_or([0.0, 0.0]);
                                                let (cx, cy) = (pos[0], pos[1]);
                                                // Canvas スロットを探して矩形を描画する
                                                for slot in actor.slots() {
                                                    if slot.kind != ComponentKind::Canvas { continue; }
                                                    if let Some(cc) = world.get::<CanvasComponent>(slot.entity) {
                                                        let hw = cc.width  * 0.5;
                                                        let hh = cc.height * 0.5;
                                                        let bl = [cx - hw, cy - hh, 0.0f32];
                                                        let br = [cx + hw, cy - hh, 0.0f32];
                                                        let tr = [cx + hw, cy + hh, 0.0f32];
                                                        let tl = [cx - hw, cy + hh, 0.0f32];
                                                        lb.add_line(bl, br, col);
                                                        lb.add_line(br, tr, col);
                                                        lb.add_line(tr, tl, col);
                                                        lb.add_line(tl, bl, col);
                                                    }
                                                }
                                                collect_canvas_rects(&actor.children, world, wl, lb, col);
                                            }
                                        }

                                        collect_canvas_rects(&scene.actors, &scene.world, wl, &mut lb, rect_col);
                                        if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) }
                                    } else { None }
                                } else { None };

                                // 軸ギズモバッチ（エディタモード + show_axis_gizmo のみ）
                                let axis_gizmo_batch = if in_editor && self.show_axis_gizmo {
                                    let sw  = window_size.map_or(1280.0, |s| s.width  as f32);
                                    let sh  = window_size.map_or(720.0,  |s| s.height as f32);
                                    let rot = self.camera.base.transform.rotation;
                                    self.axis_gizmo.as_mut().map(|ag| {
                                        ag.build(rot, sw, sh, &draw_ctx.device, &draw_ctx.queue)
                                    })
                                } else { None };

                                // アイコンオーバーレイバッチ（エディタモードのみ）
                                let icon_overlay_batch = if in_editor {
                                    let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                                    let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                                    let view = self.camera.view_matrix();
                                    let proj = self.camera.projection_matrix();
                                    let positions: Vec<(f32, f32)> = if !self.selected_instances.is_empty() {
                                        // インスタンスあり: 選択中 MC のインスタンス位置にアイコン
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
                                    } else if self.actor_virtual_selected_idx.is_some() && self.active_world_line != 0 {
                                        // 仮想アクターノード選択: actor.transform.position にアイコン
                                        let pos = actor_virtual_pos.unwrap_or([0.0, 0.0, 0.0]);
                                        world_to_screen(pos, &view.data, &proj.data, vp_w, vp_h)
                                            .map(|p| vec![p])
                                            .unwrap_or_default()
                                    } else {
                                        Vec::new()
                                    };
                                    crate::engine::core::font::icon_overlay::IconOverlay::build(
                                        &positions, vp_w, vp_h, &draw_ctx.device,
                                    )
                                } else { None };

                                // ── メインレンダーパス ────────────────
                                {
                                    // アクター編集モード: 紺色背景、通常: ダークグレー
                                    let clear_color = if self.active_world_line != 0 {
                                        wgpu::Color { r: 0.05, g: 0.08, b: 0.18, a: 1.0 }
                                    } else {
                                        wgpu::Color { r: 0.1,  g: 0.1,  b: 0.1,  a: 1.0 }
                                    };
                                    let mut pass = frame.begin_render_pass(clear_color);
                                    // 全 MC を描画（子アクターの MC も含む）
                                    for &(_, _, _, amc) in &all_mcs {
                                        if let Some((gpu, batch)) = amc.rendering_refs() {
                                            draw_model_indirect(
                                                &mut pass, gpu, batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                            );
                                        }
                                    }
                                    // アウトライン: 全選択アクター（マルチ選択対応）
                                    if in_editor {
                                        if !self.selected_actor_dfs_ids.is_empty() {
                                            // Phase 1: 全選択アクターのステンシルマスクを書き込む
                                            for &dfs_id in &self.selected_actor_dfs_ids {
                                                if let Some(&(_, _, _, mc)) = all_mcs.iter()
                                                    .find(|&&(_, d, si, _)| d == dfs_id as u32 && si == 0)
                                                {
                                                    if let Some((gpu, batch)) = mc.rendering_refs() {
                                                        draw_stencil_mask_multi(
                                                            &mut pass, gpu, batch,
                                                            &camera_buf.bind_group, &draw_ctx.pipelines,
                                                            &[0],
                                                        );
                                                    }
                                                }
                                            }
                                            // Phase 2: 全選択アクターのアウトラインを描画
                                            for &dfs_id in &self.selected_actor_dfs_ids {
                                                if let Some(&(_, _, _, mc)) = all_mcs.iter()
                                                    .find(|&&(_, d, si, _)| d == dfs_id as u32 && si == 0)
                                                {
                                                    if let Some((gpu, batch)) = mc.rendering_refs() {
                                                        draw_outline_multi(
                                                            &mut pass, gpu, batch,
                                                            &camera_buf.bind_group, &draw_ctx.pipelines,
                                                            &[0],
                                                        );
                                                    }
                                                }
                                            }
                                        } else if !self.selected_instances.is_empty() {
                                            // レガシー: インスタンス直接選択（後方互換）
                                            if let Some(sel_mc) = selected_mc {
                                                if let Some((gpu, batch)) = sel_mc.rendering_refs() {
                                                    draw_stencil_mask_multi(
                                                        &mut pass, gpu, batch,
                                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                                        &self.selected_instances,
                                                    );
                                                    draw_outline_multi(
                                                        &mut pass, gpu, batch,
                                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                                        &self.selected_instances,
                                                    );
                                                }
                                            }
                                        }
                                    }

                                    // ギズモ（Edit/Pause + Move/Rotate/Scale モードのみ）
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

                                    // 矩形選択ビジュアル
                                    if let (Some(rect_batch), Some((_, line_bg))) =
                                        (&rect_gpu_batch, &self.line_model_buf)
                                    {
                                        draw_line_batch(
                                            &mut pass, rect_batch,
                                            &camera_buf.bind_group, line_bg,
                                            &draw_ctx.pipelines,
                                        );
                                    }

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

                                    // グリッド描画
                                    if let (Some(grid_batch), Some((_, line_bg))) =
                                        (&grid_gpu_batch, &self.line_model_buf)
                                    {
                                        draw_line_batch(
                                            &mut pass, grid_batch,
                                            &camera_buf.bind_group, line_bg,
                                            &draw_ctx.pipelines,
                                        );
                                    }

                                    // CanvasComponent 矩形アウトライン描画（2D キャンバスモードのみ）
                                    if let (Some(rect_batch), Some((_, line_bg))) =
                                        (&canvas_rect_batch, &self.line_model_buf)
                                    {
                                        draw_line_batch(
                                            &mut pass, rect_batch,
                                            &camera_buf.bind_group, line_bg,
                                            &draw_ctx.pipelines,
                                        );
                                    }

                                    // 軸ギズモ（エディタモードのみ）
                                    if let (Some(batch), Some(ag)) =
                                        (&axis_gizmo_batch, &self.axis_gizmo)
                                    {
                                        ag.draw(batch, &mut pass);
                                    }

                                    // アイコンオーバーレイ（選択アクター位置マーカー）
                                    if let (Some(batch), Some(io)) =
                                        (&icon_overlay_batch, &self.icon_overlay)
                                    {
                                        io.draw(batch, &mut pass);
                                    }
                                }

                                // ── ID パス（Edit/Pause のみ）──────────
                                if in_editor {
                                    if let Some(id_buf) = &self.id_buffer {
                                        {
                                            // BindGroup は RenderPass より長く生きる必要があるので先に生成する
                                            let id_base_bgs: Vec<Option<(wgpu::Buffer, wgpu::BindGroup)>> =
                                                all_mcs.iter()
                                                    .map(|&(base, _, _, amc)| {
                                                        if amc.rendering_refs().is_some() {
                                                            Some(draw_ctx.create_id_base_bg(base))
                                                        } else {
                                                            None
                                                        }
                                                    })
                                                    .collect();

                                            let mut id_pass = frame.begin_id_pass(&id_buf.view);
                                            for (&(_, _, _, amc), bg_opt) in all_mcs.iter().zip(id_base_bgs.iter()) {
                                                if let (Some((gpu, batch)), Some((_, id_base_bg))) =
                                                    (amc.rendering_refs(), bg_opt.as_ref())
                                                {
                                                    draw_id_pass(
                                                        &mut id_pass, gpu, batch,
                                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                                        id_base_bg,
                                                    );
                                                }
                                            }
                                        }
                                        // readback 優先度: drop > pick
                                        // ホバープレビューはレイキャストで処理するため GPU リードバック不要
                                        let drop_pos = self.pending_drop
                                            .as_ref()
                                            .map(|&(_, sx, sy)| (sx, sy));
                                        let readback_pos = drop_pos.or(pick_pos);
                                        if let Some((px, py)) = readback_pos {
                                            let px = px.min(id_buf.width.saturating_sub(1));
                                            let py = py.min(id_buf.height.saturating_sub(1));
                                            frame.schedule_id_copy(
                                                &id_buf.texture, px, py, &id_buf.readback_buf,
                                            );
                                            // readback が drop ではなく pick のためかを記録するフラグ
                                            did_pick = drop_pos.is_none() && pick_pos.is_some();
                                        }
                                    }
                                }

                                frame.finish();

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
                            Err(wgpu::SurfaceError::Lost) => {
                                if let Some(size) = window_size {
                                    renderer.resize(size);
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
                            if !self.ctrl_at_press {
                                self.actor_virtual_selected_idx      = None;
                                self.actor_virtual_selected_slot_idx = 0;
                                self.selected_actor_dfs_ids.clear();
                                self.selected_instances.clear();
                            }
                        } else if !wl_mc_pick_infos.is_empty() {
                            // シーンモード・アクター編集モード共通:
                            // global ID から所有 MC を特定し、アクターと MC スロットを仮想選択として設定する
                            let global = raw - 1; // global instance ID (0-based)
                            if let Some(&(base, dfs_id, slot_i, _)) = wl_mc_pick_infos.iter()
                                .find(|&&(base, _, _, count)| global >= base && (global - base) < count as u32)
                            {
                                let dfs_usize = dfs_id as usize;
                                let local_idx = global - base;
                                self.actor_virtual_selected_slot_idx = slot_i;
                                if self.ctrl_at_press {
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
                        eprintln!("[SEED] pick: raw={raw}, selected={:?}", self.selected_instances);
                        self.send_selected();
                    }
                }

                // ── ドロップ処理（GPU サブミット後）─────────────
                if let Some((path, sx, sy)) = self.pending_drop.take() {
                    // ピッキングと同じピクセルを読み出す（同フレームで pick_pos も処理された可能性あり）
                    // ドロップ専用に再読み出しが必要な場合は次フレームで処理するため、
                    // did_pick が false のとき（= ピック処理をしていない）に限り読み出す
                    eprintln!("[Drop] Processing pending_drop pos=({sx},{sy}) did_pick={did_pick}");
                    let world_pos = if !did_pick {
                        if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
                            let (wpos, raw_id) = id_buf.read_pixel(&draw_ctx.device);
                            eprintln!("[Drop] read_pixel => world_pos={wpos:?} raw_id={raw_id}");
                            wpos
                        } else { None }
                    } else {
                        // ピック処理でバッファが読み出し済みのため別途取得できない。
                        // pending_drop を再キューイングして次フレームで処理する。
                        eprintln!("[Drop] did_pick=true, re-queuing for next frame");
                        self.pending_drop = Some((path.clone(), sx, sy));
                        None
                    };

                    // world_pos が取れた場合はその場所に、なければカーソルレイ方向へ DEFAULT_DIST 進んだ位置に配置する
                    const DEFAULT_DIST: f32 = 10.0;
                    let spawn_pos = world_pos.unwrap_or_else(|| {
                        let cam_v = self.camera.position();
                        let cam   = [cam_v.x, cam_v.y, cam_v.z];
                        // ウィンドウサイズが取れる場合はカーソルレイ方向を使う
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
                            // フォールバック: カメラ前方
                            let yaw   = self.camera.yaw.to_radians();
                            let pitch = self.camera.pitch.to_radians();
                            [
                                cam[0] + yaw.sin() * pitch.cos() * DEFAULT_DIST,
                                cam[1] + pitch.sin()              * DEFAULT_DIST,
                                cam[2] + yaw.cos() * pitch.cos() * DEFAULT_DIST,
                            ]
                        }
                    });

                    if world_pos.is_some() || !did_pick {
                        self.handle_drop_actor(&path, spawn_pos);
                    }
                }

                // ホバー処理はレンダリング前（process_ipc 直後）に実行するため、ここでは何もしない。

                // ─ 7. EndFrame（Play 時のみ）─────────────────
                if time_running {
                    if let Some(scene) = &mut self.scene { scene.end_frame(&ctx); }
                }

                self.input.end_frame();
                self.cam_input.end_frame();

                if let Some(window) = &self.window { window.request_redraw(); }
            }

            _ => {}
        }
    }

    fn device_event(
        &mut self,
        _event_loop: &ActiveEventLoop,
        _device_id: DeviceId,
        event: DeviceEvent,
    ) {
        if let DeviceEvent::MouseMotion { delta: (dx, dy) } = event {
            self.input.process_mouse_motion(dx, dy);
            self.cam_input.mouse_dx += dx as f32;
            self.cam_input.mouse_dy += dy as f32;
        }
    }
}

// ============================================================
//  カーソルロック / 復元（Windows のみ）
// ============================================================

/// RMB 押下時: カーソルをビューポート内に ClipCursor で閉じ込め、
/// 押下前のスクリーン座標を返す。
fn camera_grab_start(hwnd: isize) -> Option<(i32, i32)> {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::Foundation::{POINT, RECT};
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            ClipCursor, GetCursorPos, GetWindowRect,
        };

        let mut pt = POINT { x: 0, y: 0 };
        if GetCursorPos(&mut pt) == 0 { return None; }

        // ビューポートウィンドウの画面矩形を取得して ClipCursor で閉じ込める。
        // これによりカーソルがビューポート外に出なくなり、
        // set_cursor_visible(false) が常に有効になる。
        let mut rect = RECT { left: 0, top: 0, right: 0, bottom: 0 };
        GetWindowRect(hwnd as _, &mut rect);
        ClipCursor(&rect);

        return Some((pt.x, pt.y));
    }
    #[cfg(not(target_os = "windows"))]
    let _ = hwnd;
    None
}

/// RMB リリース時: ClipCursor を解除してカーソルを元の座標へ戻す。
fn camera_grab_end(x: i32, y: i32) {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::UI::WindowsAndMessaging::{ClipCursor, SetCursorPos};
        ClipCursor(core::ptr::null()); // クリップ解除
        SetCursorPos(x, y);
    }
    #[cfg(not(target_os = "windows"))]
    let _ = (x, y);
}

/// Play クランプ: 毎フレーム呼び出し、ウィンドウ矩形へ ClipCursor を再適用する。
fn apply_window_clamp(hwnd: isize) {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::Foundation::RECT;
        use windows_sys::Win32::UI::WindowsAndMessaging::{ClipCursor, GetWindowRect};
        let mut rect = RECT { left: 0, top: 0, right: 0, bottom: 0 };
        GetWindowRect(hwnd as _, &mut rect);
        ClipCursor(&rect);
    }
    #[cfg(not(target_os = "windows"))]
    let _ = hwnd;
}

// ============================================================
//  選択ユーティリティ
// ============================================================

/// 選択インスタンスのワールド位置の重心を返す。
/// 選択が空またはすべて範囲外の場合は None。
fn selection_centroid(instances: &[u32], mats: &[[[f32; 4]; 4]]) -> Option<[f32; 3]> {
    if instances.is_empty() { return None; }
    let (mut sx, mut sy, mut sz) = (0.0f32, 0.0, 0.0);
    let mut cnt = 0u32;
    for &i in instances {
        if let Some(m) = mats.get(i as usize) {
            sx += m[0][3]; sy += m[1][3]; sz += m[2][3];
            cnt += 1;
        }
    }
    if cnt == 0 { None } else { Some([sx / cnt as f32, sy / cnt as f32, sz / cnt as f32]) }
}

/// インスタンス削除後の親参照を修正する。
///
/// - 親が削除される場合（非再帰）: 削除チェーンを辿り最初の生存祖先（または None）にリマップ
/// - 親が削除される場合（再帰）: None にリセット（子孫も削除済みのはずだが念のため）
/// - 親が生存する場合: インデックスシフトを適用
fn fix_parent(
    parent:         Option<u32>,
    delete_set:     &std::collections::HashSet<u32>,
    deleted_parent: &std::collections::HashMap<u32, Option<u32>>,
    sorted_asc:     &[u32],
    recursive:      bool,
) -> Option<u32> {
    use crate::engine::structs::components::model_component::GROUP_ID_BASE;
    let p = parent?;

    if p >= GROUP_ID_BASE {
        return Some(p); // グループ ID は変化しない
    }

    if delete_set.contains(&p) {
        if recursive {
            return None;
        }
        // 非再帰: 削除チェーンを辿って最初の生存祖先を探す
        let mut cur = deleted_parent.get(&p).copied().flatten();
        loop {
            match cur {
                None => return None,
                Some(c) if c >= GROUP_ID_BASE => return Some(c),
                Some(c) if !delete_set.contains(&c) => {
                    let shift = sorted_asc.partition_point(|&d| d < c) as u32;
                    return Some(c - shift);
                }
                Some(c) => cur = deleted_parent.get(&c).copied().flatten(),
            }
        }
    }

    // 親は生存 → インデックスをシフト
    let shift = sorted_asc.partition_point(|&d| d < p) as u32;
    Some(p - shift)
}

// ============================================================
//  アクターツリーユーティリティ（アクター編集モード用）
// ============================================================

/// Actor ツリーを DFS 順にフラット化し (id, name, parent_id) を収集する。
fn collect_actor_nodes(
    actor:   &Actor,
    parent:  Option<u32>,
    counter: &mut u32,
    out:     &mut Vec<(u32, String, Option<u32>)>,
) {
    let id = *counter;
    *counter += 1;
    out.push((id, actor.name.clone(), parent));
    for child in actor.children() {
        collect_actor_nodes(child, Some(id), counter, out);
    }
}

/// フラットリストから HIERARCHY JSON を生成する。
fn build_hierarchy_json(nodes: &[(u32, String, Option<u32>)]) -> String {
    let mut json  = String::from("[");
    let mut first = true;
    for (id, name, parent) in nodes {
        if !first { json.push(','); }
        first = false;
        let parent_str = match parent {
            Some(p) => p.to_string(),
            None    => "null".to_string(),
        };
        json.push_str(&format!(
            r#"{{"id":{},"name":{},"parent":{},"is_group":false}}"#,
            id,
            serde_json::to_string(name).unwrap_or_default(),
            parent_str,
        ));
    }
    json.push(']');
    json
}

/// DFS id でアクターへの可変参照を取得する。
/// actors は world_line でフィルタ済みのルートスライス想定。
fn find_actor_by_dfs_mut<'a>(
    actors:  &'a mut Vec<Actor>,
    wl:      u32,
    dfs_id:  u32,
    counter: &mut u32,
) -> Option<&'a mut Actor> {
    for actor in actors.iter_mut() {
        if actor.world_line != wl { continue; }
        if *counter == dfs_id { return Some(actor); }
        *counter += 1;
        if let Some(found) = find_actor_child_by_dfs_mut(actor, dfs_id, counter) {
            return Some(found);
        }
    }
    None
}

fn find_actor_child_by_dfs_mut<'a>(
    actor:   &'a mut Actor,
    dfs_id:  u32,
    counter: &mut u32,
) -> Option<&'a mut Actor> {
    for child in actor.children_mut().iter_mut() {
        if *counter == dfs_id { return Some(child); }
        *counter += 1;
        if let Some(found) = find_actor_child_by_dfs_mut(child, dfs_id, counter) {
            return Some(found);
        }
    }
    None
}

/// DFS id でアクターへの不変参照を取得する。
fn find_actor_by_dfs<'a>(
    actors:  &'a Vec<Actor>,
    wl:      u32,
    dfs_id:  u32,
    counter: &mut u32,
) -> Option<&'a Actor> {
    for actor in actors.iter() {
        if actor.world_line != wl { continue; }
        if *counter == dfs_id { return Some(actor); }
        *counter += 1;
        if let Some(found) = find_actor_child_by_dfs(actor, dfs_id, counter) {
            return Some(found);
        }
    }
    None
}

fn find_actor_child_by_dfs<'a>(
    actor:   &'a Actor,
    dfs_id:  u32,
    counter: &mut u32,
) -> Option<&'a Actor> {
    for child in actor.children().iter() {
        if *counter == dfs_id { return Some(child); }
        *counter += 1;
        if let Some(found) = find_actor_child_by_dfs(child, dfs_id, counter) {
            return Some(found);
        }
    }
    None
}

/// インスタンスインデックスからそのインスタンスを持つアクターの DFS インデックスを返す。
/// インスタンスインデックスからそのインスタンスを持つアクターの DFS インデックスを返す。
#[allow(dead_code)]
fn find_actor_dfs_by_instance(
    actors:       &[Actor],
    world:        &crate::engine::ecs::World,
    wl:           u32,
    instance_idx: u32,
) -> Option<u32> {
    let mut counter = 0u32;
    for root in actors.iter().filter(|a| a.world_line == wl) {
        if let Some(dfs) = find_actor_dfs_by_instance_in(root, world, instance_idx, &mut counter) {
            return Some(dfs);
        }
    }
    None
}

fn find_actor_dfs_by_instance_in(
    actor:        &Actor,
    world:        &crate::engine::ecs::World,
    instance_idx: u32,
    counter:      &mut u32,
) -> Option<u32> {
    let my_dfs = *counter;
    *counter += 1;
    // スロット専用 entity の全 MC インスタンス数を合算して判定する
    let total: usize = actor.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .filter_map(|s| world.get::<ModelComponent>(s.entity))
        .map(|mc| mc.instance_mats.len())
        .sum();
    if (instance_idx as usize) < total {
        return Some(my_dfs);
    }
    for child in actor.children() {
        if let Some(dfs) = find_actor_dfs_by_instance_in(child, world, instance_idx, counter) {
            return Some(dfs);
        }
    }
    None
}

/// world_line の全アクターから ModelComponent を DFS 順で収集する（不変参照版）。
/// 戻り値: Vec<(id_base, dfs_id, &ModelComponent)>
/// id_base は先行するすべての MC のインスタンス数の累計。
/// world_line に属する全 Actor の ModelComponent を DFS 順に収集する。
/// タプル: (id_base, dfs_id, slot_i, &ModelComponent)
///   id_base  … この MC の先頭インスタンス ID（ID パスのピック計算に使う）
///   dfs_id   … アクターの DFS 順番号（アクター特定に使う）
///   slot_i   … このアクターの Model スロット内連番（複数 MC の区別に使う）
fn collect_mcs_in_world_line<'a>(
    actors: &'a [Actor],
    world:  &'a crate::engine::ecs::World,
    wl:     u32,
) -> Vec<(u32, u32, usize, &'a ModelComponent)> {
    let mut result = Vec::new();
    let mut base    = 0u32;
    let mut counter = 0u32;
    for root in actors.iter().filter(|a| a.world_line == wl) {
        collect_mcs_in_actor(root, world, &mut counter, &mut base, &mut result);
    }
    result
}

/// 再帰的に Actor の全 ModelComponent を収集する（collect_mcs_in_world_line の実装）。
fn collect_mcs_in_actor<'a>(
    actor:   &'a Actor,
    world:   &'a crate::engine::ecs::World,
    counter: &mut u32,
    base:    &mut u32,
    result:  &mut Vec<(u32, u32, usize, &'a ModelComponent)>,
) {
    let dfs = *counter;
    *counter += 1;
    // スロット専用 entity から ModelComponent を収集する（複数スロット対応）
    // slot_i はこのアクター内の Model スロット連番（0-indexed）
    for (slot_i, slot) in actor.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .enumerate()
    {
        if let Some(mc) = world.get::<ModelComponent>(slot.entity) {
            result.push((*base, dfs, slot_i, mc));
            *base += mc.instance_mats.len() as u32;
        }
    }
    for child in actor.children() {
        collect_mcs_in_actor(child, world, counter, base, result);
    }
}

/// world_line の全アクターの MC バッチを DFS 順で更新する（可変参照版）。
fn update_all_mc_batches_for_wl(
    actors:         &mut Vec<Actor>,
    world:          &mut crate::engine::ecs::World,
    wl:             u32,
    queue:          &wgpu::Queue,
    frustum_planes: &[[f32; 4]; 6],
    camera_pos:     [f32; 3],
    anim_time:      f32,
) {
    for actor in actors.iter_mut().filter(|a| a.world_line == wl) {
        update_mc_batch_recursive(actor, world, queue, frustum_planes, camera_pos, anim_time);
    }
}

fn update_mc_batch_recursive(
    actor:          &mut Actor,
    world:          &mut crate::engine::ecs::World,
    queue:          &wgpu::Queue,
    frustum_planes: &[[f32; 4]; 6],
    camera_pos:     [f32; 3],
    anim_time:      f32,
) {
    // スロット専用 entity の全 ModelComponent バッチを更新する（複数スロット対応）
    let slot_entities: Vec<crate::engine::ecs::Entity> = actor.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .map(|s| s.entity)
        .collect();
    for slot_entity in slot_entities {
        if let Some(mc) = world.get_mut::<ModelComponent>(slot_entity) {
            if let (Some(batch), Some(model)) = (&mut mc.instanced_batch, mc.model.as_deref()) {
                batch.update(queue, model, &mc.instance_mats, frustum_planes, camera_pos, anim_time);
            }
        }
    }
    for child in actor.children_mut().iter_mut() {
        update_mc_batch_recursive(child, world, queue, frustum_planes, camera_pos, anim_time);
    }
}

/// DFS id でアクターを削除する。
fn remove_actor_by_dfs(actors: &mut Vec<Actor>, wl: u32, dfs_id: u32, counter: &mut u32) -> bool {
    let mut i = 0;
    while i < actors.len() {
        if actors[i].world_line != wl { i += 1; continue; }
        if *counter == dfs_id { actors.remove(i); return true; }
        *counter += 1;
        if remove_actor_children_by_dfs(&mut actors[i], dfs_id, counter) { return true; }
        i += 1;
    }
    false
}

fn remove_actor_children_by_dfs(actor: &mut Actor, dfs_id: u32, counter: &mut u32) -> bool {
    let mut i = 0;
    while i < actor.children_mut().len() {
        if *counter == dfs_id { actor.children_mut().remove(i); return true; }
        *counter += 1;
        if remove_actor_children_by_dfs(&mut actor.children_mut()[i], dfs_id, counter) { return true; }
        i += 1;
    }
    false
}

/// アクターとその全子孫を合わせたノード数（自身を含む）を返す。
/// handle_reparent_actor での取り出し後 DFS id 補正に使用する。
fn actor_subtree_size(actor: &Actor) -> u32 {
    1 + actor.children().iter().map(|c| actor_subtree_size(c)).sum::<u32>()
}

/// 指定 world_line のアクターとその全子孫のエンティティを収集する（World.despawn 用）。
fn collect_entities_for_wl(actors: &[Actor], wl: u32) -> Vec<crate::engine::ecs::Entity> {
    let mut result = Vec::new();
    for actor in actors.iter().filter(|a| a.world_line == wl) {
        collect_actor_entities_recursive(actor, &mut result);
    }
    result
}

fn collect_actor_entities_recursive(actor: &Actor, result: &mut Vec<crate::engine::ecs::Entity>) {
    result.push(actor.entity);
    // スロット専用エンティティも含めて収集する（World.despawn 対象）
    result.extend(actor.slot_entities());
    for child in actor.children() {
        collect_actor_entities_recursive(child, result);
    }
}

/// アクターとその全子孫の World エンティティを再帰的に despawn する。
fn despawn_actor_recursive(actor: &Actor, world: &mut crate::engine::ecs::World) {
    for slot_entity in actor.slot_entities() {
        world.despawn(slot_entity);
    }
    world.despawn(actor.entity);
    for child in actor.children() {
        despawn_actor_recursive(child, world);
    }
}

/// アクター 1 本の DFS ノード数（自身 + 全子孫）を count に加算する。
/// do_paste でペースト後の新規 DFS id を求めるために使用する。
fn count_actor_dfs_nodes(actor: &Actor, count: &mut usize) {
    *count += 1;
    for child in actor.children() {
        count_actor_dfs_nodes(child, count);
    }
}

/// DFS id でアクターをツリーから取り出して out へ格納する。
/// ルートスライス（world_line でフィルタ前）を渡す。
fn extract_actor_by_dfs(
    actors:  &mut Vec<Actor>,
    wl:      u32,
    dfs_id:  u32,
    counter: &mut u32,
    out:     &mut Option<Actor>,
) -> bool {
    let mut i = 0;
    while i < actors.len() {
        if actors[i].world_line != wl { i += 1; continue; }
        if *counter == dfs_id {
            *out = Some(actors.remove(i));
            return true;
        }
        *counter += 1;
        if extract_actor_child_by_dfs(&mut actors[i], dfs_id, counter, out) {
            return true;
        }
        i += 1;
    }
    false
}

fn extract_actor_child_by_dfs(
    actor:   &mut Actor,
    dfs_id:  u32,
    counter: &mut u32,
    out:     &mut Option<Actor>,
) -> bool {
    let mut i = 0;
    while i < actor.children_mut().len() {
        if *counter == dfs_id {
            *out = Some(actor.children_mut().remove(i));
            return true;
        }
        *counter += 1;
        if extract_actor_child_by_dfs(&mut actor.children_mut()[i], dfs_id, counter, out) {
            return true;
        }
        i += 1;
    }
    false
}

/// ワールド座標をビューポートのスクリーン座標へ投影する。
/// カメラ後方（cw ≤ 0）の場合は None を返す。
/// view / proj は row-major（`data[row][col]`）。
fn world_to_screen(
    world: [f32; 3],
    view:  &[[f32; 4]; 4],
    proj:  &[[f32; 4]; 4],
    vp_w:  f32,
    vp_h:  f32,
) -> Option<(f32, f32)> {
    let [wx, wy, wz] = world;
    let vx = view[0][0]*wx + view[0][1]*wy + view[0][2]*wz + view[0][3];
    let vy = view[1][0]*wx + view[1][1]*wy + view[1][2]*wz + view[1][3];
    let vz = view[2][0]*wx + view[2][1]*wy + view[2][2]*wz + view[2][3];
    let vw = view[3][0]*wx + view[3][1]*wy + view[3][2]*wz + view[3][3];
    let cx = proj[0][0]*vx + proj[0][1]*vy + proj[0][2]*vz + proj[0][3]*vw;
    let cy = proj[1][0]*vx + proj[1][1]*vy + proj[1][2]*vz + proj[1][3]*vw;
    let cw = proj[3][0]*vx + proj[3][1]*vy + proj[3][2]*vz + proj[3][3]*vw;
    if cw <= 0.0 { return None; }
    let nx = cx / cw;
    let ny = cy / cw;
    Some(((nx + 1.0) * 0.5 * vp_w, (1.0 - ny) * 0.5 * vp_h))
}

/// Play クランプ解除。
fn release_window_clamp() {
    #[cfg(target_os = "windows")]
    unsafe {
        windows_sys::Win32::UI::WindowsAndMessaging::ClipCursor(core::ptr::null());
    }
}

/// アクター編集モードのドラッグ開始時: 子孫アクターの MC 初期行列を収集する。
/// dfs_counter は選択アクターの DFS + 1 から始める。
fn collect_child_actor_mc_starts(
    actor:       &Actor,
    world:       &crate::engine::ecs::World,
    dfs_counter: &mut u32,
    result:      &mut Vec<(u32, [[f32; 4]; 4])>,
) {
    for child in actor.children() {
        let child_dfs = *dfs_counter;
        *dfs_counter += 1;
        // スロット entity 経由で MC の最初のインスタンス行列を取得する
        if let Some(mc_e) = child.mc_entity() {
            if let Some(mc) = world.get::<ModelComponent>(mc_e) {
                if let Some(&mat) = mc.instance_mats.first() {
                    result.push((child_dfs, mat));
                }
            }
        }
        collect_child_actor_mc_starts(child, world, dfs_counter, result);
    }
}

/// インスペクタードラッグ開始時: 子孫アクターの (dfs_id, old_tf, old_mc_mat) を収集する。
fn collect_child_actor_old_states(
    actor:       &Actor,
    world:       &crate::engine::ecs::World,
    dfs_counter: &mut u32,
    result:      &mut Vec<(u32, ActorTransform, [[f32; 4]; 4])>,
) {
    for child in actor.children() {
        let child_dfs = *dfs_counter;
        *dfs_counter += 1;
        let old_tf = world.get::<ActorTransform>(child.entity).cloned().unwrap_or_default();
        // スロット entity 経由で MC の最初のインスタンス行列を取得する
        let old_mc_mat = child.mc_entity()
            .and_then(|e| world.get::<ModelComponent>(e))
            .and_then(|mc| mc.instance_mats.first().copied())
            .unwrap_or([[0.0; 4]; 4]);
        result.push((child_dfs, old_tf, old_mc_mat));
        collect_child_actor_old_states(child, world, dfs_counter, result);
    }
}

/// ギズモドラッグまたはインスペクタードラッグ中: delta を子孫アクター全体に適用し、
/// Undo 用の変更データ (child_dfs, old_tf, new_tf, old_mc_mat, new_mc_mat) を収集する。
fn apply_delta_to_actor_children(
    actor:       &mut Actor,
    world:       &mut crate::engine::ecs::World,
    delta:       [[f32; 4]; 4],
    dfs_counter: &mut u32,
    result:      &mut Vec<(u32, ActorTransform, ActorTransform, [[f32; 4]; 4], [[f32; 4]; 4])>,
) {
    let identity: [[f32; 4]; 4] = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ];
    for child in actor.children_mut().iter_mut() {
        let child_dfs = *dfs_counter;
        *dfs_counter += 1;
        let child_entity     = child.entity;
        // スロット entity を Copy で取り出す（child の borrow が続くが Entity は Copy）
        let mc_slot_entity   = child.mc_entity();

        // MC の更新: スロット entity 経由でアクセスする
        let (old_mc_mat, new_mc_mat) = if let Some(mc_e) = mc_slot_entity {
            if let Some(mc) = world.get_mut::<ModelComponent>(mc_e) {
                let old = mc.instance_mats.first().copied().unwrap_or(identity);
                if let Some(m) = mc.instance_mats.first_mut() { *m = mat4x4_mul(delta, *m); }
                mc.mark_batch_dirty();
                let new = mc.instance_mats.first().copied().unwrap_or(identity);
                (old, new)
            } else {
                (identity, identity)
            }
        } else {
            (identity, identity)
        };

        // Transform の更新（actor.entity から Transform を参照）
        let old_tf = world.get::<ActorTransform>(child_entity).cloned().unwrap_or_default();
        let new_tf = ActorTransform::from_mat4(&mat4x4_mul(delta, old_tf.to_mat4()));
        if let Some(tf) = world.get_mut::<ActorTransform>(child_entity) { *tf = new_tf.clone(); }

        result.push((child_dfs, old_tf, new_tf, old_mc_mat, new_mc_mat));
        apply_delta_to_actor_children(child, world, delta, dfs_counter, result);
    }
}
