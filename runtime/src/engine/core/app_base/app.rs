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
use crate::engine::core::app_base::scene::{Scene, DebugCameraData};
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
use crate::engine::core::app_base::undo::{UndoHistory, MultiTransformCommand, SceneSnapshotCommand, SelectionCommand, ActorTreeSnapshotCommand, ComponentSlotsSnapshotCommand, ActorTransformCommand};
use crate::engine::core::app_base::scene::build_actor;
use crate::engine::structs::components::model_component::GROUP_ID_BASE as INST_GROUP_ID_BASE;
use crate::engine::core::scripting::{ScriptingHost, ScriptComponent};
use crate::engine::structs::components::ModelComponent;
use crate::engine::structs::components::model_component::{GroupMeta, GROUP_ID_BASE};
use crate::engine::structs::objects::{Actor, DebugCamera};
use crate::engine::structs::objects::actor::{ActorData, ActorTransform, ComponentSlotData};
use crate::engine::structs::objects::camera::debug_camera::CameraInput;
use crate::engine::structs::tensor::{Vector3, Mat4x4};
use crate::engine::structs::transforms::{Quaternion, Transform};

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
    /// コピー&ペースト用クリップボード。
    clipboard: Vec<ClipboardItem>,
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
    /// グリッド描画フラグ（エディタモードのみ）。
    show_grid: bool,
    /// 軸ギズモ表示フラグ（エディタモードのみ）。
    show_axis_gizmo: bool,
    /// アクター編集モードで仮想アクターノードが選択されているとき Some(dfs_id)。
    /// ModelComponent なしアクターでもアイコン・インスペクターを表示するために使う。
    actor_virtual_selected_idx: Option<usize>,
    /// アクタートランスフォームをギズモでドラッグ中に保持する開始状態 (dfs_id, old_transform)。
    actor_transform_drag_start: Option<(u32, ActorTransform)>,

    // ── 世界線システム ───────────────────────────────────────────
    /// 現在アクティブな世界線 (0=通常シーン, N=アクター編集タブ)。
    /// active_world_line と一致する world_line を持つ Actor のみ描画・操作される。
    active_world_line: u32,
    /// 世界線切り替え時に退避するカメラ状態。キーが世界線番号。
    saved_cameras: std::collections::HashMap<u32, DebugCameraData>,
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
            lmb_held:           false,
            lmb_press_pos:      None,
            rect_selecting:     false,
            ctrl_at_press:      false,
            hierarchy_dirty:     false,
            last_hierarchy_send: None,
            clipboard:           Vec::new(),
            rmb_press_pos:         None,
            rmb_moved:             false,
            first_frame_sent:      false,
            axis_gizmo:            None,
            icon_overlay:          None,
            selection_before_rect: Vec::new(),
            show_grid:       true,
            show_axis_gizmo: true,
            actor_virtual_selected_idx: None,
            actor_transform_drag_start: None,
            active_world_line: 0,
            saved_cameras: std::collections::HashMap::new(),
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
                            self.send_actor_components(actor_dfs_id);
                        }
                        // アクタートランスフォーム変更 → インスペクター通知
                        if let Some((_wl, dfs_id)) = self.undo_history.peek_undone_actor_inspect() {
                            self.send_actor_components(dfs_id);
                        }
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
                            self.send_actor_components(actor_dfs_id);
                        }
                        // アクタートランスフォーム変更 → インスペクター通知
                        if let Some((_wl, dfs_id)) = self.undo_history.peek_redone_actor_inspect() {
                            self.send_actor_components(dfs_id);
                        }
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
                IpcCommand::Copy  => { self.do_copy(); }
                IpcCommand::Paste => { self.do_paste(); }
                IpcCommand::Delete(ids) => {
                    self.apply_delete(&ids, false);
                }
                IpcCommand::DeleteRecursive(ids) => {
                    self.apply_delete(&ids, true);
                }
                IpcCommand::Select(idx) => {
                    let before = self.selected_instances.clone();
                    if let Some(scene) = &self.scene {
                        if self.active_world_line != 0 && idx >= 999_000_000 {
                            // 仮想アクターノード選択: actor_virtual_selected_idx をセットし
                            // ModelComponent があれば全インスタンスも選択する。
                            let actor_idx = (idx - 999_000_000) as usize;
                            self.actor_virtual_selected_idx = Some(actor_idx);
                            if let Some(mc) = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) {
                                self.selected_instances = (0..mc.instance_mats.len() as u32).collect();
                            }
                        } else {
                            self.actor_virtual_selected_idx = None;
                            if let Some(mc) = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) {
                                if (idx as usize) < mc.instance_mats.len() {
                                    self.selected_instances = vec![idx];
                                } else {
                                    self.selected_instances.clear();
                                }
                            }
                        }
                    }
                    let after = self.selected_instances.clone();
                    if before != after {
                        self.undo_history.record(Box::new(SelectionCommand { before, after }));
                    }
                    self.send_selected();
                }
                IpcCommand::SelectMulti(ids) => {
                    let before = self.selected_instances.clone();
                    if let Some(scene) = &self.scene {
                        if let Some(mc) = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) {
                            self.selected_instances = ids.into_iter()
                                .filter(|&i| (i as usize) < mc.instance_mats.len())
                                .collect();
                        }
                    }
                    let after = self.selected_instances.clone();
                    if before != after {
                        self.undo_history.record(Box::new(SelectionCommand { before, after }));
                    }
                    self.send_selected();
                }
                IpcCommand::Reparent { child, new_parent } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(self.active_world_line) {
                            if child >= GROUP_ID_BASE {
                                // グループの親変更
                                if let Some(g) = mc.group_meta.iter_mut().find(|g| g.id == child) {
                                    g.parent = new_parent;
                                }
                            } else if (child as usize) < mc.instance_meta.len() {
                                mc.instance_meta[child as usize].parent = new_parent;
                            }
                        }
                    }
                    // C# 側がインプレース更新するため send_hierarchy は不要
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
                    if let Some(ctx) = &self.draw_ctx {
                        match Scene::load_actor(
                            std::path::Path::new(&path),
                            ctx,
                            self.scripting_host.as_ref(),
                        ) {
                            Ok(mut actor_scene) => {
                                // 現在の世界線のカメラ状態を退避する
                                let pos = self.camera.base.transform.position;
                                self.saved_cameras.insert(self.active_world_line, DebugCameraData {
                                    position: [pos.x, pos.y, pos.z],
                                    yaw:      self.camera.yaw,
                                    pitch:    self.camera.pitch,
                                    fov_deg:  self.camera.base.projection.fov_y_rad.to_degrees(),
                                    far:      self.camera.base.projection.far,
                                    speed:    self.camera.move_speed,
                                });
                                // ロードしたアクターに指定の世界線を設定
                                for actor in &mut actor_scene.actors {
                                    actor.world_line = world_line;
                                }
                                // 既存シーンに追加（同じ world_line は一度クリア）
                                let main_scene = self.scene.get_or_insert_with(|| Scene::new("main"));
                                main_scene.actors.retain(|a| a.world_line != world_line);
                                for actor in actor_scene.actors {
                                    main_scene.actors.push(actor);
                                }
                                // この世界線に切り替え
                                self.active_world_line = world_line;
                                // カメラを復元（初回はデフォルト）
                                let cam = self.saved_cameras.get(&world_line).cloned()
                                    .unwrap_or_else(DebugCameraData::default);
                                self.apply_camera_data(&cam);
                                // 選択・Undo をリセット
                                self.selected_instances.clear();
                                self.actor_virtual_selected_idx = None;
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
                }
                IpcCommand::AddComponent { actor_dfs_id, component_type, slot_name, args } => {
                    let ct = component_type.clone();
                    let sn = slot_name.clone();
                    let a  = args.clone();
                    self.handle_add_component_to_actor(actor_dfs_id, &ct, &sn, &a);
                }
                IpcCommand::GetActorComponents(dfs_id) => {
                    self.send_actor_components(dfs_id);
                }
                IpcCommand::AddActor { world_line, parent_dfs_id } => {
                    self.handle_add_actor(world_line, parent_dfs_id);
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
                IpcCommand::SetModelPath { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_model_path(actor_dfs_id, slot_idx, &p);
                }
                IpcCommand::DuplicateComponent { actor_dfs_id, slot_idx } => {
                    self.handle_duplicate_component(actor_dfs_id, slot_idx);
                }
            }
        }
    }

    /// ヒエラルキーを JSON にシリアライズしてエディタへ送信する（実装本体）。
    fn do_send_hierarchy(&self) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };

        if self.active_world_line != 0 {
            // アクター編集モード: アクターツリーをそのまま階層表示する
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
            return;
        }

        // 通常シーンモード: ModelComponent のインスタンス・グループをフラットに表示する
        let Some(mc) = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) else {
            ipc.send("HIERARCHY:[]");
            return;
        };

        let mut json  = String::from("[");
        let mut first = true;

        // インスタンス
        for (i, meta) in mc.instance_meta.iter().enumerate() {
            if !first { json.push(','); }
            first = false;
            let parent_str = match meta.parent {
                Some(p) => p.to_string(),
                None    => "null".to_string(),
            };
            json.push_str(&format!(
                r#"{{"id":{},"name":{},"parent":{},"is_group":false}}"#,
                i,
                serde_json::to_string(&meta.name).unwrap_or_default(),
                parent_str,
            ));
        }

        // グループ
        for g in &mc.group_meta {
            if !first { json.push(','); }
            first = false;
            let parent_str = match g.parent {
                Some(p) => p.to_string(),
                None    => "null".to_string(),
            };
            json.push_str(&format!(
                r#"{{"id":{},"name":{},"parent":{},"is_group":true}}"#,
                g.id,
                serde_json::to_string(&g.name).unwrap_or_default(),
                parent_str,
            ));
        }

        json.push(']');
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
        let Some(scene) = &mut self.scene else { return };
        let Some(mc)    = scene.find_component_in_world_line_mut::<ModelComponent>(self.active_world_line) else { return };

        let i = id as usize;
        if i >= mc.instance_mats.len() { return; }

        const RAD: f32 = std::f32::consts::PI / 180.0;
        // YXZ オイラー角（度）→ クォータニオン（Vector3::to_quaternion は YXZ 規約）
        let q = Vector3::new(ex * RAD, ey * RAD, ez * RAD).to_quaternion();

        let transform = Transform {
            position: Vector3::new(px, py, pz),
            rotation: q,
            scale:    Vector3::new(sx, sy, sz),
        };
        mc.instance_mats[i] = transform.to_matrix().data;
        mc.mark_batch_dirty();
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

    /// 選択インスタンス（+ 全子孫）をクリップボードへコピーする。
    fn do_copy(&mut self) {
        use std::collections::{HashMap, HashSet};
        let Some(scene) = &self.scene else { return };
        let Some(mc)    = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) else { return };
        if self.selected_instances.is_empty() { return; }

        // 選択 + 全子孫を収集して昇順ソート
        let mut copy_set: HashSet<u32> = self.selected_instances.iter().copied().collect();
        for &root in &self.selected_instances {
            copy_set.extend(mc.all_descendants(root));
        }
        let mut copy_list: Vec<u32> = copy_set.into_iter().collect();
        copy_list.sort_unstable();

        // 元インデックス → clipboard 内ローカルインデックス
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
    }

    /// クリップボードの内容をシーンへペーストする。
    /// ペースト後の選択は新規追加インスタンスに移る。
    fn do_paste(&mut self) {
        use crate::engine::structs::components::model_component::InstanceMeta;
        if self.clipboard.is_empty() { return; }

        // ペースト前の選択状態を保存
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
        match self.selected_instances.as_slice() {
            [] => {
                // ModelComponent なし仮想アクターノード選択中は仮想 ID を送る
                if let Some(actor_idx) = self.actor_virtual_selected_idx {
                    ipc.send(&format!("SELECTED:{}", 999_000_000u64 + actor_idx as u64));
                } else {
                    ipc.send("SELECTED:-1");
                }
            }
            [idx] => ipc.send(&format!("SELECTED:{idx}")),
            ids => ipc.send(&format!("SELECTED_MULTI:{}",
                ids.iter().map(|i| i.to_string()).collect::<Vec<_>>().join(","))),
        }
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

                use crate::engine::structs::components::model_component::{InstanceMeta, GROUP_ID_BASE};
                let identity: [[f32; 4]; 4] = [
                    [1.0, 0.0, 0.0, 0.0],
                    [0.0, 1.0, 0.0, 0.0],
                    [0.0, 0.0, 1.0, 0.0],
                    [0.0, 0.0, 0.0, 1.0],
                ];
                let mc = ModelComponent {
                    source_path:     args.to_string(),
                    model:           Some(model),
                    gpu_model:       Some(gpu_model),
                    instanced_batch: Some(instanced_batch),
                    instance_mats:   vec![identity],
                    instance_meta:   vec![InstanceMeta::new("Instance_0")],
                    group_meta:      Vec::new(),
                    next_group_id:   GROUP_ID_BASE,
                };

                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    if let Some(actor) = scene.actors.iter_mut()
                        .filter(|a| a.world_line == wl)
                        .nth(actor_idx)
                    {
                        actor.add_component(mc);
                        true
                    } else {
                        false
                    }
                };

                if found {
                    self.actor_virtual_selected_idx = None;
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
    fn send_actor_components(&self, dfs_id: u32) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };
        let wl = self.active_world_line;

        let mut c = 0u32;
        let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c) else { return };

        let [px, py, pz] = actor.transform.position;
        let [ex, ey, ez] = actor.transform.rotation;
        let [sx, sy, sz] = actor.transform.scale;

        let mut comps_json = String::from("[");
        for (i, slot) in actor.component_slots().iter().enumerate() {
            if i > 0 { comps_json.push(','); }
            let (type_name, extra) = match slot.component.to_data() {
                crate::engine::structs::components::ComponentData::ModelComponent(ref d) => {
                    let path_json = serde_json::to_string(&d.model_path).unwrap_or_default();
                    ("ModelComponent", format!(r#","model_path":{path_json}"#))
                }
                crate::engine::structs::components::ComponentData::ScriptComponent(_) => {
                    ("ScriptComponent", String::new())
                }
            };
            comps_json.push_str(&format!(
                r#"{{"slot":{},"name":{},"type":"{}"{}}}"#,
                i,
                serde_json::to_string(&slot.name).unwrap_or_default(),
                type_name,
                extra,
            ));
        }
        comps_json.push(']');

        let name_json = serde_json::to_string(&actor.name).unwrap_or_default();
        let json = format!(
            r#"{{"id":{dfs_id},"name":{name_json},"transform":{{"px":{px:.4},"py":{py:.4},"pz":{pz:.4},"ex":{ex:.4},"ey":{ey:.4},"ez":{ez:.4},"sx":{sx:.4},"sy":{sy:.4},"sz":{sz:.4}}},"components":{comps_json}}}"#
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

        match component_type {
            "ModelComponent" => {
                use crate::engine::structs::components::model_component::{InstanceMeta, GROUP_ID_BASE};
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
                    let identity = [[1.0f32,0.0,0.0,0.0],[0.0,1.0,0.0,0.0],[0.0,0.0,1.0,0.0],[0.0,0.0,0.0,1.0]];
                    ModelComponent {
                        source_path:     args.to_string(),
                        model:           Some(model),
                        gpu_model:       Some(gpu_model),
                        instanced_batch: Some(instanced_batch),
                        instance_mats:   vec![identity],
                        instance_meta:   vec![InstanceMeta::new("Instance_0")],
                        group_meta:      Vec::new(),
                        next_group_id:   GROUP_ID_BASE,
                    }
                };
                let name = slot_name.to_string();
                let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
                let found = {
                    let scene = self.scene.as_mut().unwrap();
                    let mut c = 0u32;
                    if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                        actor.add_component_named(name, mc);
                        true
                    } else { false }
                };
                if found {
                    let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
                    self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
                        world_line: wl,
                        actor_dfs_id,
                        before_slots,
                        after_slots,
                    }));
                    self.actor_virtual_selected_idx = None;
                    self.selected_instances.clear();
                    self.send_hierarchy();
                    self.send_actor_components(actor_dfs_id);
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
            }
            _ => {}
        }
    }

    /// ModelComponent のモデルパスを後から設定する。
    fn handle_set_model_path(&mut self, actor_dfs_id: u32, slot_idx: u32, path: &str) {
        if self.draw_ctx.is_none() || self.ipc.is_none() || self.scene.is_none() { return; }
        let wl = self.active_world_line;

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
        use crate::engine::structs::components::model_component::InstanceMeta;
        let found = {
            let scene = self.scene.as_mut().unwrap();
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                if let Some(slot) = actor.component_slots_mut().get_mut(slot_idx as usize) {
                    if let Some(mc) = slot.component.as_any_mut().downcast_mut::<ModelComponent>() {
                        if mc.instance_mats.is_empty() {
                            let identity = [[1.0f32,0.0,0.0,0.0],[0.0,1.0,0.0,0.0],[0.0,0.0,1.0,0.0],[0.0,0.0,0.0,1.0]];
                            mc.instance_mats.push(identity);
                            mc.instance_meta.push(InstanceMeta::new("Instance_0"));
                        }
                        mc.source_path   = path.to_string();
                        mc.model         = Some(model);
                        mc.gpu_model     = Some(gpu_model);
                        mc.instanced_batch = Some(instanced_batch);
                        true
                    } else { false }
                } else { false }
            } else { false }
        };
        if found {
            self.send_actor_components(actor_dfs_id);
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
        }
    }

    /// 子アクターを追加する。
    fn handle_add_actor(&mut self, world_line: u32, parent_dfs_id: Option<u32>) {
        if self.scene.is_none() { return; }

        let before_actors = self.snapshot_actors_for_wl(world_line);

        let Some(scene) = &mut self.scene else { return };

        let new_actor = {
            let mut a = Actor::with_name("Actor");
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
        if let Some(ipc) = &self.ipc { ipc.send("SELECTED:-1"); }
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
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                actor.remove_component_at(slot_idx as usize);
            }
        }

        let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: wl,
            actor_dfs_id,
            before_slots,
            after_slots,
        }));

        self.send_actor_components(actor_dfs_id);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    fn handle_rename_component_slot(&mut self, actor_dfs_id: u32, slot_idx: u32, name: &str) {
        let Some(scene) = &mut self.scene else { return };
        let wl = self.active_world_line;
        let mut c = 0u32;
        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
            if let Some(slot) = actor.component_slots_mut().get_mut(slot_idx as usize) {
                slot.name = name.to_string();
            }
        }
        self.send_actor_components(actor_dfs_id);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    fn handle_set_actor_transform(
        &mut self,
        dfs_id: u32,
        px: f32, py: f32, pz: f32,
        ex: f32, ey: f32, ez: f32,
        sx: f32, sy: f32, sz: f32,
    ) {
        let Some(scene) = &mut self.scene else { return };
        let wl = self.active_world_line;
        let mut c = 0u32;
        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, dfs_id, &mut c) {
            actor.transform.position = [px, py, pz];
            actor.transform.rotation = [ex, ey, ez];
            actor.transform.scale    = [sx, sy, sz];
        }
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// アクター編集モードで仮想選択中のアクターのワールド座標（transform.position）を返す。
    fn actor_virtual_world_pos(&self) -> Option<[f32; 3]> {
        let dfs_id = self.actor_virtual_selected_idx? as u32;
        let wl = self.active_world_line;
        if wl == 0 { return None; }
        let scene = self.scene.as_ref()?;
        let mut c = 0u32;
        let actor = find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c)?;
        Some(actor.transform.position)
    }

    /// 指定世界線のアクターツリー全体をデータとしてスナップショットする。
    fn snapshot_actors_for_wl(&self, wl: u32) -> Vec<ActorData> {
        self.scene.as_ref().map(|s| {
            s.actors.iter()
                .filter(|a| a.world_line == wl)
                .map(|a| a.to_data())
                .collect()
        }).unwrap_or_default()
    }

    /// 指定アクターのコンポーネントスロット一覧をデータとしてスナップショットする。
    fn snapshot_actor_slots(&self, wl: u32, actor_dfs_id: u32) -> Vec<ComponentSlotData> {
        let Some(scene) = &self.scene else { return Vec::new() };
        let mut c = 0u32;
        find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
            .map(|actor| actor.component_slots().iter().map(|s| ComponentSlotData {
                name:      s.name.clone(),
                component: s.component.to_data(),
            }).collect())
            .unwrap_or_default()
    }

    /// 指定世界線のアクターを data から再構築する（Undo/Redo 用）。
    fn rebuild_actors_for_wl(&mut self, wl: u32, actors_data: Vec<ActorData>) {
        let host = self.scripting_host.clone();
        // draw_ctx の借用はこのスコープ内で完結させ、scene への書き込みと重ならないようにする。
        let mut rebuilt: Vec<Actor> = {
            let Some(ctx) = &self.draw_ctx else { return };
            actors_data.into_iter()
                .filter_map(|d| build_actor(d, ctx, host.as_ref()).ok())
                .collect()
        };
        for a in &mut rebuilt { a.set_world_line_recursive(wl); }

        let scene = self.scene.get_or_insert_with(|| Scene::new("main"));
        scene.actors.retain(|a| a.world_line != wl);
        scene.actors.extend(rebuilt);

        self.selected_instances.clear();
        self.actor_virtual_selected_idx = None;
    }

    /// 指定アクターのコンポーネントスロットを data から再構築する（Undo/Redo 用）。
    fn rebuild_actor_slots(&mut self, wl: u32, actor_dfs_id: u32, slots_data: Vec<ComponentSlotData>) {
        use crate::engine::structs::objects::actor::ComponentSlot;
        use crate::engine::structs::components::ComponentData;
        use crate::engine::core::scripting::ScriptComponent;
        use crate::engine::core::loader::load_model;

        let host = self.scripting_host.clone();

        // スロットを draw_ctx の借用スコープ内で構築する。
        let mut new_slots: Vec<ComponentSlot> = {
            let Some(ctx) = &self.draw_ctx else { return };
            let mut slots = Vec::new();
            for slot_data in slots_data {
                let component: Box<dyn crate::engine::structs::components::Component> = match slot_data.component {
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
                            let model = match load_model(path) {
                                Ok(m) => m,
                                Err(_) => continue,
                            };
                            let gpu_model       = ctx.upload_model(&model);
                            let instanced_batch = ctx.create_instanced_batch(&model, mc_data.instances.len() as u32);
                            ModelComponent {
                                source_path:     mc_data.model_path,
                                model:           Some(model),
                                gpu_model:       Some(gpu_model),
                                instanced_batch: Some(instanced_batch),
                                instance_mats:   mc_data.instances,
                                instance_meta:   mc_data.meta,
                                group_meta:      mc_data.groups,
                                next_group_id:   mc_data.next_group_id,
                            }
                        };
                        Box::new(mc)
                    }
                    ComponentData::ScriptComponent(sc_data) => {
                        if let Some(host) = &host {
                            if let Some(sc) = ScriptComponent::new(std::sync::Arc::clone(host), sc_data.type_name) {
                                Box::new(sc)
                            } else { continue; }
                        } else { continue; }
                    }
                };
                slots.push(ComponentSlot { name: slot_data.name, component });
            }
            slots
        };

        let Some(scene) = &mut self.scene else { return };
        let mut c = 0u32;
        if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
            actor.replace_components(new_slots);
        }
    }

    /// コンポーネントを複製する（DUPLICATE_COMPONENT）。
    fn handle_duplicate_component(&mut self, actor_dfs_id: u32, slot_idx: u32) {
        if self.draw_ctx.is_none() { return; }
        let wl = self.active_world_line;
        let host = self.scripting_host.clone();

        let slot_data_opt = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|actor| actor.component_slots().get(slot_idx as usize))
                .map(|s| ComponentSlotData {
                    name:      format!("{} Copy", s.name),
                    component: s.component.to_data(),
                })
        };
        let Some(slot_data) = slot_data_opt else { return };

        let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);

        use crate::engine::structs::objects::actor::ComponentSlot;
        use crate::engine::structs::components::ComponentData;
        use crate::engine::core::scripting::ScriptComponent;
        use crate::engine::core::loader::load_model;

        // draw_ctx の借用をスコープで完結させる。
        let new_component_opt: Option<(String, Box<dyn crate::engine::structs::components::Component>)> = {
            let Some(ctx) = &self.draw_ctx else { return };
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
                        let model = match load_model(path) {
                            Ok(m) => m,
                            Err(e) => {
                                if let Some(ipc) = &self.ipc { ipc.send(&format!("LOAD_ERROR:{e}")); }
                                return;
                            }
                        };
                        let gpu_model       = ctx.upload_model(&model);
                        let instanced_batch = ctx.create_instanced_batch(&model, mc_data.instances.len() as u32);
                        ModelComponent {
                            source_path:     mc_data.model_path,
                            model:           Some(model),
                            gpu_model:       Some(gpu_model),
                            instanced_batch: Some(instanced_batch),
                            instance_mats:   mc_data.instances,
                            instance_meta:   mc_data.meta,
                            group_meta:      mc_data.groups,
                            next_group_id:   mc_data.next_group_id,
                        }
                    };
                    Some((slot_data.name, Box::new(mc) as Box<dyn crate::engine::structs::components::Component>))
                }
                ComponentData::ScriptComponent(sc_data) => {
                    if let Some(host) = &host {
                        ScriptComponent::new(std::sync::Arc::clone(host), sc_data.type_name)
                            .map(|sc| (slot_data.name, Box::new(sc) as Box<dyn crate::engine::structs::components::Component>))
                    } else { None }
                }
            }
        };
        let Some((comp_name, new_component)) = new_component_opt else { return };

        {
            let Some(scene) = &mut self.scene else { return };
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                actor.component_slots_mut().push(ComponentSlot {
                    name:      comp_name,
                    component: new_component,
                });
            }
        }

        let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: wl,
            actor_dfs_id,
            before_slots,
            after_slots,
        }));

        self.send_actor_components(actor_dfs_id);
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
    /// - `recursive = true`  → 子孫ごと削除
    /// - `recursive = false` → 指定ノードのみ削除、子は親を切り離してルートへ
    fn apply_delete(&mut self, base_ids: &[u32], recursive: bool) {
        use std::collections::{HashMap, HashSet, VecDeque};

        // 削除前の選択状態を保存
        let before_selection = self.selected_instances.clone();

        let wl = self.active_world_line;
        let Some(scene) = &mut self.scene else { return };
        let Some(mc)    = scene.find_component_in_world_line_mut::<ModelComponent>(wl) else { return };

        // ── ① スナップショット（削除前）─────────────────────
        let before_mats   = mc.instance_mats.clone();
        let before_meta   = mc.instance_meta.clone();
        let before_groups = mc.group_meta.clone();
        let before_gid    = mc.next_group_id;

        // ── ② 削除セット構築（インスタンス / グループ に分離）──
        let mut inst_del: HashSet<u32> = base_ids.iter().copied()
            .filter(|&id| id < GROUP_ID_BASE).collect();
        let mut grp_del:  HashSet<u32> = base_ids.iter().copied()
            .filter(|&id| id >= GROUP_ID_BASE).collect();

        if recursive {
            // インスタンスの子孫インスタンスを展開
            let inst_descs: Vec<u32> = inst_del.iter().copied()
                .flat_map(|id| mc.all_descendants(id)).collect();
            inst_del.extend(inst_descs);

            // グループの子（インスタンス＋サブグループ）を再帰展開
            let mut queue: VecDeque<u32> = grp_del.iter().copied().collect();
            while let Some(gid) = queue.pop_front() {
                for (i, meta) in mc.instance_meta.iter().enumerate() {
                    if meta.parent == Some(gid) {
                        let ii = i as u32;
                        if inst_del.insert(ii) {
                            let descs = mc.all_descendants(ii);
                            inst_del.extend(descs);
                        }
                    }
                }
                for g in &mc.group_meta {
                    if g.parent == Some(gid) && grp_del.insert(g.id) {
                        queue.push_back(g.id);
                    }
                }
            }
        }

        let mut sorted_asc: Vec<u32> = inst_del.iter().copied().collect();
        sorted_asc.sort_unstable();

        // 削除グループの parent マップを先に構築（後で借用競合を避けるため）
        let grp_parent_map: HashMap<u32, Option<u32>> = mc.group_meta.iter()
            .map(|g| (g.id, g.parent)).collect();

        // ── ③ 削除されるインスタンスの元親マップ（非再帰用）──
        let deleted_parent: HashMap<u32, Option<u32>> = sorted_asc.iter()
            .filter_map(|&id| mc.instance_meta.get(id as usize).map(|m| (id, m.parent)))
            .collect();

        // ── ④ 親参照の修正 ───────────────────────────────────
        for meta in mc.instance_meta.iter_mut() {
            meta.parent = fix_parent(meta.parent, &inst_del, &deleted_parent, &sorted_asc, recursive);
            // 親グループが削除される場合の処理
            if let Some(p) = meta.parent {
                if p >= GROUP_ID_BASE && grp_del.contains(&p) {
                    meta.parent = if recursive { None }
                        else { *grp_parent_map.get(&p).unwrap_or(&None) };
                }
            }
        }
        for g in mc.group_meta.iter_mut() {
            if let Some(p) = g.parent {
                if p < GROUP_ID_BASE {
                    g.parent = fix_parent(Some(p), &inst_del, &deleted_parent, &sorted_asc, recursive);
                } else if grp_del.contains(&p) {
                    g.parent = if recursive { None }
                        else { *grp_parent_map.get(&p).unwrap_or(&None) };
                }
            }
        }

        // ── ⑤ インスタンスを降順で削除 ──────────────────────
        let mut sorted_desc = sorted_asc.clone();
        sorted_desc.sort_unstable_by(|a, b| b.cmp(a));
        for &idx in &sorted_desc {
            if (idx as usize) < mc.instance_mats.len() {
                mc.instance_mats.remove(idx as usize);
                mc.instance_meta.remove(idx as usize);
            }
        }
        mc.mark_batch_dirty();

        // ── ⑤b グループを削除 ───────────────────────────────
        mc.group_meta.retain(|g| !grp_del.contains(&g.id));

        // ── ⑥ スナップショット（削除後）・Undo 記録 ─────────
        let after_mats   = mc.instance_mats.clone();
        let after_meta   = mc.instance_meta.clone();
        let after_groups = mc.group_meta.clone();
        let after_gid    = mc.next_group_id;

        // 削除後の選択状態を計算
        let after_selection: Vec<u32> = before_selection.iter()
            .filter(|&&i| !inst_del.contains(&i))
            .map(|&i| {
                let shift = sorted_asc.partition_point(|&d| d < i) as u32;
                i - shift
            })
            .collect();

        self.undo_history.record(Box::new(SceneSnapshotCommand {
            before_mats, before_meta, before_groups, before_gid,
            after_mats,  after_meta,  after_groups,  after_gid,
            before_selection,
            after_selection: after_selection.clone(),
        }));

        // ── ⑦ 選択状態を更新 ────────────────────────────────
        self.selected_instances = after_selection;

        self.sync_anim_seeds();
        self.send_selected();
        self.send_hierarchy();
    }

    /// デモシーンを構築する。
    /// 将来的にはシーンファイルのロードに置き換える。
    /// カーソル座標でギズモのヒットテストを行い、当たったパーツを返す。
    fn compute_gizmo_hover(&self, cx: f32, cy: f32) -> Option<GizmoPart> {
        if self.tool_mode == ToolMode::Select { return None; }
        let gizmo_pos = if self.active_world_line != 0 && self.actor_virtual_selected_idx.is_some() {
            self.actor_virtual_world_pos()?
        } else {
            let mc = self.scene.as_ref()?.find_component_in_world_line::<ModelComponent>(self.active_world_line)?;
            selection_centroid(&self.selected_instances, &mc.instance_mats)?
        };

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
        let gizmo_pos = if self.active_world_line != 0 && self.actor_virtual_selected_idx.is_some() {
            self.actor_virtual_world_pos()?
        } else {
            let mc = self.scene.as_ref()?.find_component_in_world_line::<ModelComponent>(self.active_world_line)?;
            selection_centroid(&self.selected_instances, &mc.instance_mats)?
        };

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
                                if self.active_world_line != 0 && self.actor_virtual_selected_idx.is_some() {
                                    // アクター編集モード: actor.transform をドラッグ対象として記録
                                    let dfs_id = self.actor_virtual_selected_idx.unwrap() as u32;
                                    let wl = self.active_world_line;
                                    if let Some(scene) = &self.scene {
                                        let mut c = 0u32;
                                        if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c) {
                                            self.actor_transform_drag_start = Some((dfs_id, actor.transform.clone()));
                                        }
                                    }
                                } else if let Some(mc) = self.scene.as_ref()
                                    .and_then(|s| s.find_component_in_world_line::<ModelComponent>(self.active_world_line))
                                {
                                    let roots = mc.filter_selection_roots(&self.selected_instances);
                                    self.drag_root_starts = roots.iter()
                                        .filter_map(|&i| mc.instance_mats.get(i as usize).map(|&m| (i, m)))
                                        .collect();
                                    self.drag_child_starts = mc.collect_non_root_descendants(&roots);
                                }
                                self.gizmo_drag = Some(drag);
                            }
                        }
                    }
                    if !pressed {
                        self.lmb_held = false;

                        if self.rect_selecting {
                            // 矩形選択終了: SelectionCommand を記録してエディタへ通知
                            let before = std::mem::take(&mut self.selection_before_rect);
                            let after  = self.selected_instances.clone();
                            if before != after {
                                self.undo_history.record(Box::new(SelectionCommand { before, after }));
                            }
                            self.send_selected();
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
                        if self.gizmo_drag.is_some() {
                            if let Some((dfs_id, old_transform)) = self.actor_transform_drag_start.take() {
                                // アクター編集モード: actor.transform の変化を記録
                                let wl = self.active_world_line;
                                let new_transform_opt = self.scene.as_ref().and_then(|s| {
                                    let mut c = 0u32;
                                    find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c)
                                        .map(|a| a.transform.clone())
                                });
                                if let Some(new_transform) = new_transform_opt {
                                    if old_transform != new_transform {
                                        self.undo_history.record(Box::new(ActorTransformCommand {
                                            world_line: wl, dfs_id,
                                            old_transform, new_transform,
                                        }));
                                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                                    }
                                }
                                self.send_actor_components(dfs_id);
                            } else {
                                let mut transforms: Vec<(u32, [[f32;4];4], [[f32;4];4])> = Vec::new();
                                let root_starts  = std::mem::take(&mut self.drag_root_starts);
                                let child_starts = std::mem::take(&mut self.drag_child_starts);
                                if let Some(mc) = self.scene.as_ref()
                                    .and_then(|s| s.find_component_in_world_line::<ModelComponent>(self.active_world_line))
                                {
                                    for (idx, old_mat) in root_starts.into_iter().chain(child_starts) {
                                        if let Some(&new_mat) = mc.instance_mats.get(idx as usize) {
                                            if new_mat != old_mat {
                                                transforms.push((idx, old_mat, new_mat));
                                            }
                                        }
                                    }
                                }
                                if !transforms.is_empty() {
                                    self.undo_history.record(Box::new(MultiTransformCommand { transforms }));
                                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                                }
                            }
                        } else {
                            self.actor_transform_drag_start = None;
                            self.drag_root_starts.clear();
                            self.drag_child_starts.clear();
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
                            self.selection_before_rect = self.selected_instances.clone();
                        }
                        if self.rect_selecting {
                            let sx_min = px.min(cx);
                            let sx_max = px.max(cx);
                            let sy_min = py.min(cy);
                            let sy_max = py.max(cy);
                            if let Some(scene) = &self.scene {
                                if let Some(mc) = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line) {
                                    if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                                        let vp_w = ws.width as f32;
                                        let vp_h = ws.height as f32;
                                        let view = self.camera.view_matrix();
                                        let proj = self.camera.projection_matrix();
                                        self.selected_instances = (0..mc.instance_mats.len() as u32)
                                            .filter(|&i| {
                                                let m = &mc.instance_mats[i as usize];
                                                let world = [m[0][3], m[1][3], m[2][3]];
                                                if let Some((sx, sy)) = world_to_screen(
                                                    world, &view.data, &proj.data, vp_w, vp_h,
                                                ) {
                                                    sx >= sx_min && sx <= sx_max
                                                        && sy >= sy_min && sy <= sy_max
                                                } else {
                                                    false
                                                }
                                            })
                                            .collect();
                                    }
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
                        let delta = mat4x4_mul(new_mat, mat4x4_inv(drag.start_mat));
                        let wl = self.active_world_line;

                        if wl != 0 && self.actor_transform_drag_start.is_some() {
                            // アクター編集モード: actor.transform に delta を適用する
                            if let Some((dfs_id, ref start_tf)) = self.actor_transform_drag_start {
                                let new_actor_mat = mat4x4_mul(delta, start_tf.to_mat4());
                                let new_tf = ActorTransform::from_mat4(&new_actor_mat);
                                let scene  = self.scene.as_mut().unwrap();
                                let mut c  = 0u32;
                                if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, dfs_id, &mut c) {
                                    actor.transform = new_tf;
                                }
                                // send_actor_components はドラッグ終了時のみ呼ぶ（毎フレーム呼ぶとインスペクター再構築で重い）
                            }
                        } else if let Some(scene) = &mut self.scene {
                            if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(wl) {
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
                        }
                    }
                    // 単一インスタンス選択時のみリアルタイム更新（複数選択は毎フレーム n 件 IPC 送信になり重い）
                    if self.actor_transform_drag_start.is_none() && self.selected_instances.len() == 1 {
                        self.send_actor_data(self.selected_instances[0]);
                    }
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

                if in_editor {
                    self.camera.update(&self.cam_input, ctx.delta_time);
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
                    let view      = self.camera.view_matrix();
                    let proj      = self.camera.projection_matrix();
                    let view_proj = proj * view;
                    let pos       = self.camera.position();

                    let res = window_size.map_or([1280.0, 720.0], |s| {
                        [s.width as f32, s.height as f32]
                    });
                    camera_buf.update(&queue, &CameraUniform {
                        view_proj:  view_proj.transpose().data,
                        view:       view.transpose().data,
                        position:   [pos.x, pos.y, pos.z],
                        _pad:       0.0,
                        resolution: res,
                        _pad2:      [0.0; 2],
                    });

                    let frustum_planes = extract_frustum_planes(&view_proj.data);
                    let camera_pos     = [pos.x, pos.y, pos.z];

                    if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(self.active_world_line) {
                        if let (Some(batch), Some(model)) = (&mut mc.instanced_batch, mc.model.as_ref()) {
                            batch.update(
                                &queue, model, &mc.instance_mats,
                                &frustum_planes, camera_pos, self.clock.anim_time(),
                            );
                        }
                    }
                }

                // ── ギズモ位置：アクター編集モード仮想選択 → actor.transform、それ以外 → インスタンス重心 ──
                let gizmo_pos = if self.active_world_line != 0 && self.actor_virtual_selected_idx.is_some() {
                    self.actor_virtual_world_pos()
                } else {
                    self.scene.as_ref()
                        .and_then(|s| s.find_component_in_world_line::<ModelComponent>(self.active_world_line))
                        .and_then(|mc| selection_centroid(&self.selected_instances, &mc.instance_mats))
                };

                // アクター仮想選択のワールド位置（レンダラー借用外で取得）
                let actor_virtual_pos: Option<[f32; 3]> = if self.active_world_line != 0 && self.actor_virtual_selected_idx.is_some() {
                    self.actor_virtual_world_pos()
                } else { None };

                // ピック要求を取り出す（描画ブロック内で使用）
                let pick_pos = self.pending_pick.take();
                let mut did_pick = false;

                if let (Some(renderer), Some(scene), Some(camera_buf), Some(draw_ctx)) =
                    (&mut self.renderer, &self.scene, &self.camera_buf, &self.draw_ctx)
                {
                    match renderer.begin_frame() {
                        Ok(mut frame) => {
                            let mc = scene.find_component_in_world_line::<ModelComponent>(self.active_world_line);

                            if let Some(mc) = mc {
                                if let Some(batch) = mc.instanced_batch.as_ref() {
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

                                // グリッド描画バッチ（エディタモード + show_grid のみ）
                                // アクター編集モードはグリッドを常時表示し、紺背景に映えるよう色を調整
                                let grid_gpu_batch = if in_editor && (self.show_grid || self.active_world_line != 0) {
                                    let mut lb = LineBatch::new();
                                    let (minor, major): ([f32; 4], [f32; 4]) = if self.active_world_line != 0 {
                                        ([0.22, 0.25, 0.40, 1.0], [0.32, 0.36, 0.55, 1.0])
                                    } else {
                                        ([0.18, 0.18, 0.18, 1.0], [0.30, 0.30, 0.30, 1.0])
                                    };
                                    let ax_x:  [f32; 4] = [0.50, 0.10, 0.10, 1.0];
                                    let ax_z:  [f32; 4] = [0.10, 0.10, 0.50, 1.0];
                                    let n = 10i32;
                                    let step = 5.0f32;
                                    let ext = n as f32 * step;
                                    for i in -n..=n {
                                        let p = i as f32 * step;
                                        if i == 0 {
                                            lb.add_line([-ext, 0.0, 0.0], [ext, 0.0, 0.0], ax_x);
                                            lb.add_line([0.0, 0.0, -ext], [0.0, 0.0, ext], ax_z);
                                        } else {
                                            let c = if i % 2 == 0 { major } else { minor };
                                            lb.add_line([-ext, 0.0, p], [ext, 0.0, p], c);
                                            lb.add_line([p, 0.0, -ext], [p, 0.0, ext], c);
                                        }
                                    }
                                    Some(lb.build(&draw_ctx.device))
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
                                        // インスタンスあり: 各インスタンスの位置にアイコン
                                        scene.find_component_in_world_line::<ModelComponent>(self.active_world_line)
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
                                    if let Some(mc) = mc {
                                        if let Some((gpu, batch)) = mc.rendering_refs() {
                                            draw_model_indirect(
                                                &mut pass, gpu, batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                            );

                                            if in_editor && !self.selected_instances.is_empty() {
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
                                    if let (Some(mc), Some(id_buf)) = (mc, &self.id_buffer) {
                                        {
                                            let mut id_pass = frame.begin_id_pass(&id_buf.view);
                                            if let Some((gpu, batch)) = mc.rendering_refs() {
                                                draw_id_pass(
                                                    &mut id_pass, gpu, batch,
                                                    &camera_buf.bind_group, &draw_ctx.pipelines,
                                                );
                                            }
                                        }
                                        if let Some((px, py)) = pick_pos {
                                            let px = px.min(id_buf.width.saturating_sub(1));
                                            let py = py.min(id_buf.height.saturating_sub(1));
                                            frame.schedule_id_copy(
                                                &id_buf.texture, px, py, &id_buf.readback_buf,
                                            );
                                            did_pick = true;
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
                        let raw     = id_buf.read_pixel(&draw_ctx.device);
                        let new_idx = if raw > 0 { Some(raw - 1) } else { None };
                        let before  = self.selected_instances.clone();
                        if self.ctrl_at_press {
                            // Ctrl+クリック: 個別トグル
                            if let Some(idx) = new_idx {
                                if self.selected_instances.contains(&idx) {
                                    self.selected_instances.retain(|&x| x != idx);
                                } else {
                                    self.selected_instances.push(idx);
                                }
                            }
                        } else {
                            self.selected_instances = new_idx.map(|i| vec![i]).unwrap_or_default();
                        }
                        let after = self.selected_instances.clone();
                        if before != after {
                            self.undo_history.record(Box::new(SelectionCommand { before, after }));
                        }
                        eprintln!("[SEED] pick: raw={raw}, selected={:?}", self.selected_instances);
                        self.send_selected();
                    }
                }

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
