// ============================================================
//  app/mod.rs — App 本体の定義と共有ユーティリティ
//
//  【構成】
//  各機能は役割別サブモジュールに分割されている。
//  このファイルには以下を格納する:
//    - App 構造体定義
//    - 補助型（ClipboardItem, RuntimeMode, LaunchArgs, InspectorTransformDrag）
//    - App::new / run / is_embedded / window_hwnd
//    - アクターツリー操作ユーティリティ（フリー関数）
//    - カーソルロック・ウィンドウクランプユーティリティ
//    - 選択・座標変換ユーティリティ
// ============================================================

// ── サブモジュール ──────────────────────────────────────────
mod ipc_handler;
mod hierarchy_sync;
mod clipboard;
mod actor_ops;
mod component_ops;
mod transform_ops;
mod camera_ops;
mod gizmo_handler;
mod pick_2d;
mod render;

// ── 外部クレート・標準ライブラリ ────────────────────────────
use std::sync::Arc;

use winit::application::ApplicationHandler;
use winit::event::{DeviceEvent, DeviceId, ElementState, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::keyboard::{KeyCode, PhysicalKey};
use winit::window::{Window, WindowId};

// ── エンジン内部モジュール ───────────────────────────────────
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
    GizmoDrag, GizmoPart, screen_to_ray, screen_to_ray_ortho, hit_test_gizmo, start_drag, update_drag,
    mat4x4_mul, mat4x4_inv,
};
use crate::engine::core::app_base::undo::{
    UndoHistory, TransformCommand, MultiTransformCommand, SceneSnapshotCommand,
    SelectionCommand, ActorTreeSnapshotCommand, ComponentSlotsSnapshotCommand,
    ActorTransformCommand, ActorGroupTransformCommand, MultiActorDragTransformCommand,
    ActorDfsSelectionCommand, CompositeCommand,
};
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

/// MC インスタンスのコピー&ペースト単位。
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

/// ランタイムの動作モード。
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum RuntimeMode {
    /// デバッグカメラ・エディタ埋め込み
    Edit,
    /// 通常ゲームプレイ・独立ウィンドウ
    Play,
}

/// App::new / App::run への引数。
pub struct LaunchArgs {
    pub parent_hwnd:  Option<isize>,
    pub mode:         RuntimeMode,
    pub pipe_name:    Option<String>,
    /// アセットルートディレクトリの絶対パス（Play / パッケージモードで使用）。
    /// None の場合は実行ファイルの隣に assets/ or assets.pak があると仮定する。
    pub assets_root:  Option<String>,
}

// ============================================================
//  InspectorTransformDrag — インスペクターフィールドドラッグ Undo 単一化
// ============================================================

/// インスペクターフィールドのドラッグ中状態（EndTransformDrag で Undo 1 コマンド化）。
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
    /// 2D キャンバスアクターの CanvasTransform ドラッグ
    CanvasActor { wl: u32, dfs_id: u32, old_ct: CanvasTransform },
}

// ============================================================
//  App
// ============================================================

/// エンジンのメインアプリケーション。
/// ECS ワールド・レンダラー・カメラ・IPC クライアントを統括する。
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
    /// シーンのスクリーンスペースキャンバスオーバーレイ専用カメラバッファ。
    /// 3D メインカメラの上に 2D キャンバス要素を重ねるために使う（シーンSS専用）。
    /// アクター編集タブは camera_buf 自体が 2D なので不要。
    canvas_overlay_camera_buf: Option<CameraBuffer>,
    scripting_host: Option<Arc<ScriptingHost>>,

    parent_hwnd:  Option<isize>,
    mode:         RuntimeMode,
    ipc:          Option<IpcClient>,
    paused:       bool,
    /// アセットルートのパス（Playモード・パッケージモードでのシーン自動ロードに使用）。
    assets_root:  Option<String>,

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
    /// 2D アクターの CanvasTransform をギズモでドラッグ中に保持する開始状態 (dfs_id, old_canvas_transform)。
    canvas_transform_drag_start: Option<(u32, CanvasTransform)>,
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
    /// アクター編集タブの 2D キャンバス世界線セット。
    /// これに含まれる世界線は canvas_screen_space_overlay フラグに関わらず
    /// 常にスクリーンスペースで描画する（アクター編集パネルは従来の 2D 表示を維持）。
    actor_edit_canvas_wls: std::collections::HashSet<u32>,
    /// 2D キャンバスカメラ状態（世界線番号 → CanvasCameraData）。
    /// pan_x, pan_y, ortho_half_h を保持する。
    canvas_cameras: std::collections::HashMap<u32, CanvasCameraData>,
    /// キャンバスをスクリーンスペースオーバーレイで表示するフラグ。
    /// false（デフォルト）= ワールドスペース、true = スクリーンスペースオーバーレイ。
    /// エディタのビューポートオプションから切り替え可能。実行時は常に true。
    canvas_screen_space_overlay: bool,

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
    /// App インスタンスを生成する（EventLoop は run() で生成される）。
    pub fn new(args: LaunchArgs) -> Self {
        let ipc = args.pipe_name.as_deref()
            .and_then(|name| IpcClient::connect(name).ok());

        let t0 = std::time::Instant::now();
        let dll_path = ScriptingHost::resolve_dll_path();
        eprintln!("[SEED] ScriptingHost DLL path: {:?} (exists={})", dll_path, dll_path.exists());

        let scripting_host = if dll_path.exists() {
            // DLL が存在する場合のみ CLR ロードを試みる（存在しない場合は hostfxr 検索で遅延するため）
            match ScriptingHost::load(&dll_path) {
                Ok(host) => {
                    eprintln!("[SEED] ScriptingHost loaded ({:.1}ms)", t0.elapsed().as_millis());
                    Some(host)
                }
                Err(e) => {
                    eprintln!("[SEED] ScriptingHost load failed ({:.1}ms): {e}", t0.elapsed().as_millis());
                    None
                }
            }
        } else {
            eprintln!("[SEED] ScriptingHost skipped — DLL not found ({:.1}ms)", t0.elapsed().as_millis());
            None
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
            canvas_overlay_camera_buf: None,
            scripting_host,
            parent_hwnd:  args.parent_hwnd,
            mode:         args.mode,
            ipc,
            paused:       false,
            assets_root:  args.assets_root,
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
            canvas_transform_drag_start:  None,
            actor_extra_mc_drag_starts:   Vec::new(),
            multi_actor_drag_starts:      Vec::new(),
            inspector_transform_drag:     None,
            active_world_line: 0,
            saved_cameras: std::collections::HashMap::new(),
            canvas_world_lines:    std::collections::HashSet::new(),
            actor_edit_canvas_wls: std::collections::HashSet::new(),
            canvas_cameras:        std::collections::HashMap::new(),
            canvas_screen_space_overlay: false,
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

    /// エディタ埋め込みモードかどうかを返す。
    fn is_embedded(&self) -> bool { self.parent_hwnd.is_some() }

    /// ウィンドウの HWND（Windows）を返す。非 Windows では 0。
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
}

// ============================================================
//  アクターツリーユーティリティ（DFS 探索・収集・変更）
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

/// find_actor_by_dfs_mut の再帰実装（子ノード用）。
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

/// find_actor_by_dfs の再帰実装（子ノード用）。
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

/// find_actor_dfs_by_instance の再帰実装。
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

/// 2D キャンバスモード用: 指定 DFS ID のアクターに適用すべきアンカーオフセットを返す。
///
/// アンカーオフセット = 親 CanvasComponent サイズ × CanvasTransform.anchor。
/// ルートアクター（親なし）または CanvasComponent を持たない親の場合は [0.0, 0.0] を返す。
/// render.rs の collect_sprite_items / collect_canvas_rects と同じロジックで計算する。
pub(super) fn canvas_anchor_offset_for_dfs(
    actors:     &[Actor],
    world:      &World,
    wl:         u32,
    target_dfs: u32,
) -> [f32; 2] {
    let mut counter = 0u32;
    for actor in actors.iter() {
        if actor.world_line != wl { continue; }
        // ルートアクター自身が target の場合はアンカー適用なし
        if counter == target_dfs { return [0.0, 0.0]; }
        counter += 1;
        // このアクターの CanvasComponent サイズを子アクターへ渡す
        let canvas_size = actor.slots().iter()
            .filter(|s| s.kind == ComponentKind::Canvas)
            .find_map(|s| world.get::<CanvasComponent>(s.entity))
            .map(|cc| [cc.width, cc.height]);
        if let Some(off) = find_canvas_anchor_in_children(actor, world, target_dfs, &mut counter, canvas_size) {
            return off;
        }
    }
    [0.0, 0.0]
}

/// canvas_anchor_offset_for_dfs の再帰実装（子ノード探索）。
fn find_canvas_anchor_in_children(
    parent:             &Actor,
    world:              &World,
    target_dfs:         u32,
    counter:            &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
) -> Option<[f32; 2]> {
    for child in parent.children().iter() {
        if *counter == target_dfs {
            // ターゲットが見つかった。親の Canvas サイズ × anchor でオフセットを計算する。
            let offset = if let Some([pw, ph]) = parent_canvas_size {
                world.get::<CanvasTransform>(child.entity)
                    .map(|ct| [pw * ct.anchor[0], ph * ct.anchor[1]])
                    .unwrap_or([0.0, 0.0])
            } else {
                [0.0, 0.0]
            };
            return Some(offset);
        }
        *counter += 1;
        // 子の CanvasComponent サイズを孫へ渡す
        let child_canvas_size = child.slots().iter()
            .filter(|s| s.kind == ComponentKind::Canvas)
            .find_map(|s| world.get::<CanvasComponent>(s.entity))
            .map(|cc| [cc.width, cc.height]);
        if let Some(off) = find_canvas_anchor_in_children(child, world, target_dfs, counter, child_canvas_size) {
            return Some(off);
        }
    }
    None
}

/// world_line の全アクターから ModelComponent を DFS 順で収集する（不変参照版）。
///
/// 戻り値: Vec<(id_base, dfs_id, slot_i, &ModelComponent)>
///   id_base … この MC の先頭インスタンス ID（ID パスのピック計算に使う）
///   dfs_id  … アクターの DFS 順番号
///   slot_i  … このアクターの Model スロット内連番（複数 MC の区別に使う）
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

/// collect_mcs_in_world_line の再帰実装。
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

/// 2D キャンバスモードの矩形選択用: CanvasTransform を持つアクタを DFS 順に走査し、
/// ワールド矩形 [wx_min, wx_max] × [wy_min, wy_max] 内の DFS ID を result に追加する。
pub(super) fn collect_canvas_actors_in_rect(
    actor:   &Actor,
    world:   &crate::engine::ecs::World,
    counter: &mut u32,
    wx_min: f32, wx_max: f32,
    wy_min: f32, wy_max: f32,
    result:  &mut Vec<usize>,
) {
    let dfs_id = *counter as usize;
    *counter += 1;
    if let Some(ct) = world.get::<crate::engine::components::CanvasTransform>(actor.entity) {
        let [px, py] = ct.position;
        if px >= wx_min && px <= wx_max && py >= wy_min && py <= wy_max {
            if !result.contains(&dfs_id) { result.push(dfs_id); }
        }
    }
    for child in actor.children() {
        collect_canvas_actors_in_rect(child, world, counter, wx_min, wx_max, wy_min, wy_max, result);
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

/// update_all_mc_batches_for_wl の再帰実装。
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

/// remove_actor_by_dfs の再帰実装（子ノード用）。
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

/// 指定 DFS id のアクターの親情報を返す。
///
/// 返り値: (親の DFS id, 親が CanvasComponent を持つか)
/// - ルートアクター（親なし）の場合: (None, false)
/// - 対象が見つからない場合: (None, false) を返す
///
/// Sprite など Canvas 上に配置するコンポーネントを追加する際の
/// 自動親子化判定に使用する。
fn find_parent_canvas_info(actors: &[Actor], wl: u32, target_dfs: u32) -> (Option<u32>, bool) {
    let mut counter = 0u32;
    find_parent_canvas_info_root(actors, wl, target_dfs, &mut counter)
        .unwrap_or((None, false))
}

/// find_parent_canvas_info のルートレベル探索実装。
fn find_parent_canvas_info_root(
    actors:     &[Actor],
    wl:         u32,
    target_dfs: u32,
    counter:    &mut u32,
) -> Option<(Option<u32>, bool)> {
    for actor in actors.iter() {
        if actor.world_line != wl { continue; }
        let my_dfs = *counter;
        if my_dfs == target_dfs {
            // このアクターが対象 → 親なし
            return Some((None, false));
        }
        *counter += 1;
        let my_has_canvas = actor.has_kind(ComponentKind::Canvas);
        if let Some(result) = find_parent_canvas_info_children(
            actor.children(), target_dfs, counter, my_dfs, my_has_canvas,
        ) {
            return Some(result);
        }
    }
    None
}

/// find_parent_canvas_info の子孫レベル再帰探索実装。
fn find_parent_canvas_info_children(
    children:          &[Actor],
    target_dfs:        u32,
    counter:           &mut u32,
    parent_dfs:        u32,
    parent_has_canvas: bool,
) -> Option<(Option<u32>, bool)> {
    for child in children.iter() {
        let my_dfs = *counter;
        if my_dfs == target_dfs {
            return Some((Some(parent_dfs), parent_has_canvas));
        }
        *counter += 1;
        let my_has_canvas = child.has_kind(ComponentKind::Canvas);
        if let Some(result) = find_parent_canvas_info_children(
            child.children(), target_dfs, counter, my_dfs, my_has_canvas,
        ) {
            return Some(result);
        }
    }
    None
}

/// 指定 world_line のアクターとその全子孫のエンティティを収集する（World.despawn 用）。
fn collect_entities_for_wl(actors: &[Actor], wl: u32) -> Vec<crate::engine::ecs::Entity> {
    let mut result = Vec::new();
    for actor in actors.iter().filter(|a| a.world_line == wl) {
        collect_actor_entities_recursive(actor, &mut result);
    }
    result
}

/// collect_entities_for_wl の再帰実装。
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

/// extract_actor_by_dfs の再帰実装（子ノード用）。
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

// ============================================================
//  座標変換・選択ユーティリティ
// ============================================================

/// 選択インスタンスのワールド位置の重心を返す。
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
#[allow(dead_code)]
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

// ============================================================
//  ドラッグ開始・終了時の子アクター状態収集ユーティリティ
// ============================================================

/// ドラッグ開始時: 子孫アクターの MC 初期行列を収集する。
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
        ClipCursor(core::ptr::null());
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

/// Play クランプ解除。
fn release_window_clamp() {
    #[cfg(target_os = "windows")]
    unsafe {
        windows_sys::Win32::UI::WindowsAndMessaging::ClipCursor(core::ptr::null());
    }
}
