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
use crate::engine::core::app_base::scene::Scene;
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
use crate::engine::core::app_base::undo::{UndoHistory, MultiTransformCommand, SceneSnapshotCommand};
use crate::engine::structs::components::model_component::GROUP_ID_BASE as INST_GROUP_ID_BASE;
use crate::engine::core::scripting::{ScriptingHost, ScriptComponent};
use crate::engine::structs::components::ModelComponent;
use crate::engine::structs::components::model_component::{GroupMeta, GROUP_ID_BASE};
use crate::engine::structs::objects::{Actor, DebugCamera};
use crate::engine::structs::objects::camera::debug_camera::CameraInput;
use crate::engine::structs::tensor::Vector3;

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
//  定数（デモシーン用）
// ============================================================

const INSTANCE_DIM:     usize = 10;
const INSTANCE_SPACING: f32   = 3.0;

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
            rmb_press_pos:       None,
            rmb_moved:           false,
            first_frame_sent:    false,
            axis_gizmo:          None,
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
                    let structural = if let Some(scene) = &mut self.scene {
                        self.undo_history.undo(scene)
                    } else { None };
                    if structural == Some(true) {
                        self.selected_instances.clear();
                        self.sync_anim_seeds();
                        self.send_selected();
                        self.send_hierarchy();
                    }
                }
                IpcCommand::Redo => {
                    let structural = if let Some(scene) = &mut self.scene {
                        self.undo_history.redo(scene)
                    } else { None };
                    if structural == Some(true) {
                        self.selected_instances.clear();
                        self.sync_anim_seeds();
                        self.send_selected();
                        self.send_hierarchy();
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
                    if let Some(scene) = &self.scene {
                        if let Some(mc) = scene.find_component::<ModelComponent>() {
                            if (idx as usize) < mc.instance_mats.len() {
                                self.selected_instances = vec![idx];
                            } else {
                                self.selected_instances.clear();
                            }
                        }
                    }
                }
                IpcCommand::SelectMulti(ids) => {
                    if let Some(scene) = &self.scene {
                        if let Some(mc) = scene.find_component::<ModelComponent>() {
                            self.selected_instances = ids.into_iter()
                                .filter(|&i| (i as usize) < mc.instance_mats.len())
                                .collect();
                        }
                    }
                }
                IpcCommand::Reparent { child, new_parent } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
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
                        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
                            if let Some(meta) = mc.instance_meta.get_mut(idx as usize) {
                                meta.name = name;
                            }
                        }
                    }
                    self.send_hierarchy();
                }
                IpcCommand::CreateGroup { name, parent } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
                            let id = mc.next_group_id;
                            mc.next_group_id += 1;
                            mc.group_meta.push(GroupMeta { id, name, parent });
                        }
                    }
                    self.send_hierarchy();
                }
                IpcCommand::CreateGroupWithChildren { name, parent, children } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
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
                        match scene.save(std::path::Path::new(&path)) {
                            Ok(())   => { if let Some(ipc) = &self.ipc { ipc.send("SAVE_OK"); } }
                            Err(e)   => { if let Some(ipc) = &self.ipc { ipc.send(&format!("SAVE_ERROR:{e}")); } }
                        }
                    }
                }
            }
        }
    }

    /// ヒエラルキーを JSON にシリアライズしてエディタへ送信する（実装本体）。
    fn do_send_hierarchy(&self) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };
        let Some(mc)    = scene.find_component::<ModelComponent>() else { return };

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

    /// InstanceMeta::anim_seed を instanced_batch に同期する。
    /// 構造変更（追加・削除・Undo/Redo）後に呼び出す。
    fn sync_anim_seeds(&mut self) {
        if let Some(scene) = &mut self.scene {
            if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
                let seeds: Vec<u32> = mc.instance_meta.iter().map(|m| m.anim_seed).collect();
                mc.instanced_batch.set_anim_seeds(&seeds);
            }
        }
    }

    /// 選択インスタンス（+ 全子孫）をクリップボードへコピーする。
    fn do_copy(&mut self) {
        use std::collections::{HashMap, HashSet};
        let Some(scene) = &self.scene else { return };
        let Some(mc)    = scene.find_component::<ModelComponent>() else { return };
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

        let new_indices = {
            let Some(scene) = &mut self.scene else { return };
            let Some(mc)    = scene.find_component_mut::<ModelComponent>() else { return };

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
            mc.instanced_batch.mark_dirty();

            let after_mats   = mc.instance_mats.clone();
            let after_meta   = mc.instance_meta.clone();
            let after_groups = mc.group_meta.clone();
            let after_gid    = mc.next_group_id;

            self.undo_history.record(Box::new(SceneSnapshotCommand {
                before_mats, before_meta, before_groups, before_gid,
                after_mats,  after_meta,  after_groups,  after_gid,
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
            [] => ipc.send("SELECTED:-1"),
            [idx] => ipc.send(&format!("SELECTED:{idx}")),
            ids => ipc.send(&format!("SELECTED_MULTI:{}",
                ids.iter().map(|i| i.to_string()).collect::<Vec<_>>().join(","))),
        }
    }

    /// 選択インスタンス／グループを削除し Undo 履歴に記録する。
    ///
    /// - `recursive = true`  → 子孫ごと削除
    /// - `recursive = false` → 指定ノードのみ削除、子は親を切り離してルートへ
    fn apply_delete(&mut self, base_ids: &[u32], recursive: bool) {
        use std::collections::{HashMap, HashSet, VecDeque};

        let Some(scene) = &mut self.scene else { return };
        let Some(mc)    = scene.find_component_mut::<ModelComponent>() else { return };

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
        mc.instanced_batch.mark_dirty();

        // ── ⑤b グループを削除 ───────────────────────────────
        mc.group_meta.retain(|g| !grp_del.contains(&g.id));

        // ── ⑥ スナップショット（削除後）・Undo 記録 ─────────
        let after_mats   = mc.instance_mats.clone();
        let after_meta   = mc.instance_meta.clone();
        let after_groups = mc.group_meta.clone();
        let after_gid    = mc.next_group_id;

        self.undo_history.record(Box::new(SceneSnapshotCommand {
            before_mats, before_meta, before_groups, before_gid,
            after_mats,  after_meta,  after_groups,  after_gid,
        }));

        // ── ⑦ 選択状態を更新 ────────────────────────────────
        // 削除されていないインスタンスはインデックスがシフトするため再マップ
        self.selected_instances = self.selected_instances.iter()
            .filter(|&&i| !inst_del.contains(&i))
            .map(|&i| {
                let shift = sorted_asc.partition_point(|&d| d < i) as u32;
                i - shift
            })
            .collect();

        self.sync_anim_seeds();
        self.send_selected();
        self.send_hierarchy();
    }

    /// デモシーンを構築する。
    /// 将来的にはシーンファイルのロードに置き換える。
    /// カーソル座標でギズモのヒットテストを行い、当たったパーツを返す。
    fn compute_gizmo_hover(&self, cx: f32, cy: f32) -> Option<GizmoPart> {
        if self.tool_mode == ToolMode::Select { return None; }
        let mc        = self.scene.as_ref()?.find_component::<ModelComponent>()?;
        let gizmo_pos = selection_centroid(&self.selected_instances, &mc.instance_mats)?;

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
        let mc        = self.scene.as_ref()?.find_component::<ModelComponent>()?;
        let gizmo_pos = selection_centroid(&self.selected_instances, &mc.instance_mats)?;

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

    fn build_demo_scene(ctx: &DrawContext, scripting_host: Option<&Arc<ScriptingHost>>) -> Scene {
        let mut scene = Scene::new("Demo");

        let model_path = Path::new("assets/models/BrainStem.glb");
        let model      = load_model(model_path)
            .unwrap_or_else(|e| panic!("BrainStem.glb のロード失敗: {e}"));
        let gpu_model       = ctx.upload_model(&model);
        let total           = INSTANCE_DIM.pow(3);
        let instanced_batch = ctx.create_instanced_batch(&model, total as u32);

        let mut instance_mats = Vec::with_capacity(total);
        let mut instance_meta = Vec::with_capacity(total);
        let mut idx = 0usize;
        for z in 0..INSTANCE_DIM {
            for y in 0..INSTANCE_DIM {
                for x in 0..INSTANCE_DIM {
                    let (tx, ty, tz) = (
                        x as f32 * INSTANCE_SPACING,
                        y as f32 * INSTANCE_SPACING,
                        z as f32 * INSTANCE_SPACING,
                    );
                    instance_mats.push([
                        [1.0, 0.0, 0.0, tx],
                        [0.0, 1.0, 0.0, ty],
                        [0.0, 0.0, 1.0, tz],
                        [0.0, 0.0, 0.0, 1.0f32],
                    ]);
                    let mut meta = crate::engine::structs::components::model_component::InstanceMeta::new(
                        format!("BrainStem_{idx}")
                    );
                    meta.anim_seed = idx as u32;
                    instance_meta.push(meta);
                    idx += 1;
                }
            }
        }

        let mut actor = Actor::with_name("BrainStem");
        actor.add_component(ModelComponent {
            source_path:   model_path.to_string_lossy().into_owned(),
            model,
            gpu_model,
            instanced_batch,
            instance_mats,
            instance_meta,
            group_meta:    Vec::new(),
            next_group_id: GROUP_ID_BASE,
        });
        scene.add_actor(actor);

        if let Some(host) = scripting_host {
            if let Some(script) = ScriptComponent::new(Arc::clone(host), "TestRotator") {
                let mut script_actor = Actor::with_name("TestRotator");
                script_actor.add_component(script);
                scene.add_actor(script_actor);
                eprintln!("[SEED] TestRotator script component created");
            } else {
                eprintln!("[SEED] TestRotator: CreateComponent returned null");
            }
        }

        scene
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

        let center = (INSTANCE_DIM as f32 - 1.0) * INSTANCE_SPACING * 0.5;
        self.camera.base.transform.position = Vector3::new(center, center, -10.0);

        let ctx = DrawContext::new(
            renderer.device(),
            renderer.queue(),
            renderer.surface_format(),
            renderer.depth_format(),
        );
        eprintln!("[SEED] DrawContext created");

        let scene      = Self::build_demo_scene(&ctx, self.scripting_host.as_ref());
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

        // 軸ギズモ（エディタモードのみ初期化）
        if self.mode == RuntimeMode::Edit {
            use crate::engine::core::font::axis_gizmo::AxisGizmo;
            self.axis_gizmo = Some(AxisGizmo::new(
                &self.draw_ctx.as_ref().unwrap().device,
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
                            let structural = if let Some(scene) = &mut self.scene {
                                self.undo_history.undo(scene)
                            } else { None };
                            if structural == Some(true) {
                                self.selected_instances.clear();
                                self.sync_anim_seeds();
                                self.send_selected();
                                self.send_hierarchy();
                            }
                        }
                        KeyCode::KeyY if pressed && self.ctrl_held => {
                            let structural = if let Some(scene) = &mut self.scene {
                                self.undo_history.redo(scene)
                            } else { None };
                            if structural == Some(true) {
                                self.selected_instances.clear();
                                self.sync_anim_seeds();
                                self.send_selected();
                                self.send_hierarchy();
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
                                if let Some(mc) = self.scene.as_ref()
                                    .and_then(|s| s.find_component::<ModelComponent>())
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
                            // 矩形選択終了: 確定してエディタへ通知
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
                            let mut transforms: Vec<(u32, [[f32;4];4], [[f32;4];4])> = Vec::new();
                            let root_starts  = std::mem::take(&mut self.drag_root_starts);
                            let child_starts = std::mem::take(&mut self.drag_child_starts);
                            if let Some(mc) = self.scene.as_ref()
                                .and_then(|s| s.find_component::<ModelComponent>())
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
                            }
                        } else {
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
                                } else {
                                    camera_grab_end(0, 0);
                                }
                                // 短押し（カーソル移動なし）→ コンテキストメニュー
                                if !self.rmb_moved {
                                    if let Some(ipc) = &self.ipc {
                                        ipc.send("CONTEXT_MENU");
                                    }
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
                        }
                        if self.rect_selecting {
                            let sx_min = px.min(cx);
                            let sx_max = px.max(cx);
                            let sy_min = py.min(cy);
                            let sy_max = py.max(cy);
                            if let Some(scene) = &self.scene {
                                if let Some(mc) = scene.find_component::<ModelComponent>() {
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
                        // delta = new_gizmo_mat * inv(start_gizmo_mat)
                        // 全ルートおよび子孫に同一デルタを適用する
                        let delta = mat4x4_mul(new_mat, mat4x4_inv(drag.start_mat));
                        if let Some(scene) = &mut self.scene {
                            if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
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
                                mc.instanced_batch.mark_dirty();
                            }
                        }
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

                    if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
                        mc.instanced_batch.update(
                            &queue, &mc.model, &mc.instance_mats,
                            &frustum_planes, camera_pos, self.clock.anim_time(),
                        );
                    }
                }

                // ── 選択インスタンス群の重心（ギズモ位置）──
                let gizmo_pos = self.scene.as_ref()
                    .and_then(|s| s.find_component::<ModelComponent>())
                    .and_then(|mc| selection_centroid(&self.selected_instances, &mc.instance_mats));

                // ピック要求を取り出す（描画ブロック内で使用）
                let pick_pos = self.pending_pick.take();
                let mut did_pick = false;

                if let (Some(renderer), Some(scene), Some(camera_buf), Some(draw_ctx)) =
                    (&mut self.renderer, &self.scene, &self.camera_buf, &self.draw_ctx)
                {
                    if let Some(mc) = scene.find_component::<ModelComponent>() {
                        match renderer.begin_frame() {
                            Ok(mut frame) => {
                                mc.instanced_batch.dispatch_skin(
                                    frame.encoder_mut(),
                                    &draw_ctx.pipelines.skin_compute,
                                );

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

                                // 軸ギズモバッチ（レンダーパス前に構築）
                                let axis_gizmo_batch = if in_editor {
                                    let sw  = window_size.map_or(1280.0, |s| s.width  as f32);
                                    let sh  = window_size.map_or(720.0,  |s| s.height as f32);
                                    let rot = self.camera.base.transform.rotation;
                                    self.axis_gizmo.as_mut().map(|ag| {
                                        ag.build(rot, sw, sh, &draw_ctx.device, &draw_ctx.queue)
                                    })
                                } else { None };

                                // ── メインレンダーパス ────────────────
                                {
                                    let mut pass = frame.begin_render_pass();
                                    draw_model_indirect(
                                        &mut pass, &mc.gpu_model, &mc.instanced_batch,
                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                    );

                                    // アウトライン（Edit/Pause + 選択中のみ）
                                    // ① 全選択インスタンスのステンシルマスクを一括書き込み
                                    // ② 合成シルエット外縁にアウトラインを一括描画
                                    if in_editor && !self.selected_instances.is_empty() {
                                        draw_stencil_mask_multi(
                                            &mut pass, &mc.gpu_model, &mc.instanced_batch,
                                            &camera_buf.bind_group, &draw_ctx.pipelines,
                                            &self.selected_instances,
                                        );
                                        draw_outline_multi(
                                            &mut pass, &mc.gpu_model, &mc.instanced_batch,
                                            &camera_buf.bind_group, &draw_ctx.pipelines,
                                            &self.selected_instances,
                                        );
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

                                    // 軸ギズモ（エディタモードのみ）
                                    if let (Some(batch), Some(ag)) =
                                        (&axis_gizmo_batch, &self.axis_gizmo)
                                    {
                                        ag.draw(batch, &mut pass);
                                    }
                                }

                                // ── ID パス（Edit/Pause のみ）──────────
                                if in_editor {
                                    if let Some(id_buf) = &self.id_buffer {
                                        {
                                            let mut id_pass = frame.begin_id_pass(&id_buf.view);
                                            draw_id_pass(
                                                &mut id_pass, &mc.gpu_model, &mc.instanced_batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                            );
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
                }

                // ── ピック結果の読み出し（GPU サブミット後）─────
                if did_pick {
                    if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
                        let raw     = id_buf.read_pixel(&draw_ctx.device);
                        let new_idx = if raw > 0 { Some(raw - 1) } else { None };
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
