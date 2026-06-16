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
mod drag_state;
mod ipc_handler;
mod hierarchy_sync;
mod clipboard;
mod actor_ops;
mod ai_ops;
mod component_ops;
mod canvas_component_ops;
mod camera_component_ops;
mod physics_component_ops;
mod physics2d_ops;
mod physics2d_component_ops;
mod transform_ops;
mod camera_ops;
mod gizmo_handler;
mod pick_2d;
mod render;
mod frame_renderer;
mod canvas_collect;
mod app_init;
mod event_handler;
mod drag_handler;
mod physics_ops;
mod physics_timeline;
pub(crate) mod camera_scene_gizmo;

// ── 外部クレート・標準ライブラリ ────────────────────────────
use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use winit::event_loop::{ControlFlow, EventLoop};
use winit::window::Window;

// ── エンジン内部モジュール ───────────────────────────────────
use crate::engine::core::clock::Clock;
use crate::engine::core::input::Input;
use crate::engine::core::renderer::Renderer;
use crate::engine::core::app_base::ipc::{IpcClient, ToolMode};
use crate::engine::core::app_base::scene::{Scene, DebugCameraData, CanvasCameraData};
use crate::engine::methods::drawer::{DrawContext, CameraBuffer, IdBuffer, InstancedModelBatch};
use crate::engine::methods::gizmo_interact::GizmoPart;
use crate::engine::core::app_base::undo::UndoHistory;
use crate::engine::core::scripting::ScriptingHost;
use crate::engine::components::{
    Transform as ActorTransform,
    CanvasTransform,
};
use crate::engine::structs::objects::DebugCamera;
use crate::engine::structs::objects::actor::ActorData;
use crate::engine::structs::objects::camera::debug_camera::CameraInput;

// ============================================================
//  モジュール定数
// ============================================================

/// BlitRect バッファのバイトサイズ ([f32; 4] = 16 bytes)
const BLIT_RECT_BUFFER_SIZE: u64 = 16;

/// キャンバス座標（ピクセル）→ 3D ワールド座標の変換スケール係数。
/// ワールドスペースモード時に CanvasTransform 座標をワールド空間へスケールするために使う。
/// 例: position=100px → 1.0 ワールドユニット
pub(super) const CANVAS_WORLD_SCALE: f32 = 1.0 / 100.0;

/// 4×4 単位行列
const MAT4_IDENTITY: [[f32; 4]; 4] = [
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
];

// ============================================================
//  CameraPreviewResources — カメラプレビュー描画リソース
// ============================================================

/// カメラアクター選択時のビューポートプレビュー描画リソースまとめ。
///
/// オフスクリーンテクスチャへのレンダーパスと、スクリーンへのブリットパスで使用する。
struct CameraPreviewResources {
    /// オフスクリーンカラーテクスチャ（プレビューのレンダーターゲット）
    color_texture: wgpu::Texture,
    /// カラーテクスチャのビュー（レンダーターゲット兼サンプリング用）
    color_view:    wgpu::TextureView,
    /// オフスクリーン深度テクスチャ
    depth_texture: wgpu::Texture,
    /// 深度テクスチャのビュー
    depth_view:    wgpu::TextureView,
    /// ブリット用テクスチャバインドグループ（Group 1: テクスチャ + サンプラー）
    blit_tex_bg:   wgpu::BindGroup,
    /// ブリット矩形ユニフォームバッファ（NDC 座標、毎フレーム更新）
    blit_rect_buf: wgpu::Buffer,
    /// ブリット矩形バインドグループ（Group 0）
    blit_rect_bg:  wgpu::BindGroup,
}

impl CameraPreviewResources {
    /// カメラプレビューリソースを生成する。
    ///
    /// - `width` / `height` : プレビューテクスチャ解像度（ピクセル）
    fn new(
        device: &wgpu::Device,
        blit_pipeline: &crate::engine::methods::drawer::CameraPreviewBlitPipeline,
        width:  u32,
        height: u32,
    ) -> Self {
        // オフスクリーンカラーテクスチャ（Bgra8UnormSrgb: スワップチェーンと同フォーマット）
        let color_texture = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Camera Preview Color"),
            size:            wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Bgra8UnormSrgb,
            usage:           wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats:    &[],
        });
        let color_view = color_texture.create_view(&wgpu::TextureViewDescriptor::default());

        // オフスクリーン深度テクスチャ
        let depth_texture = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Camera Preview Depth"),
            size:            wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Depth24PlusStencil8,
            usage:           wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats:    &[],
        });
        let depth_view = depth_texture.create_view(&wgpu::TextureViewDescriptor::default());

        // サンプラー（線形フィルタリング）
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Camera Preview Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // ブリット用テクスチャバインドグループ (Group 1)
        let blit_tex_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Camera Preview Blit Tex BG"),
            layout:  &blit_pipeline.tex_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding:  0,
                    resource: wgpu::BindingResource::TextureView(&color_view),
                },
                wgpu::BindGroupEntry {
                    binding:  1,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
            ],
        });

        // ブリット矩形ユニフォームバッファ (Group 0)
        let blit_rect_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("Camera Preview Blit Rect"),
            size:               BLIT_RECT_BUFFER_SIZE,
            usage:              wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let blit_rect_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Camera Preview Blit Rect BG"),
            layout:  &blit_pipeline.rect_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: blit_rect_buf.as_entire_binding(),
            }],
        });

        Self {
            color_texture, color_view,
            depth_texture, depth_view,
            blit_tex_bg,
            blit_rect_buf, blit_rect_bg,
        }
    }

    /// ブリット矩形ユニフォームをビューポートサイズに基づいて GPU に書き込む。
    ///
    /// プレビューはビューポート右下に配置される（マージン: 8 px）。
    fn update_blit_rect(
        &self,
        queue:    &wgpu::Queue,
        vp_w:     f32,
        vp_h:     f32,
        prev_w:   f32,
        prev_h:   f32,
    ) {
        // プレビュー右下マージン（px）
        const MARGIN: f32 = 8.0;

        let sx0 = vp_w - prev_w - MARGIN;
        let sx1 = vp_w - MARGIN;
        let sy0 = vp_h - prev_h - MARGIN;  // 上端（screen Y が小さい）
        let sy1 = vp_h - MARGIN;           // 下端

        // NDC 変換（x: [-1,1], y: [-1,1]）
        let nx0 = 2.0 * sx0 / vp_w - 1.0;
        let nx1 = 2.0 * sx1 / vp_w - 1.0;
        let ny0 = 1.0 - 2.0 * sy1 / vp_h;  // 下端 NDC y
        let ny1 = 1.0 - 2.0 * sy0 / vp_h;  // 上端 NDC y

        let rect: [f32; 4] = [nx0, ny0, nx1, ny1];
        queue.write_buffer(&self.blit_rect_buf, 0, bytemuck::cast_slice(&rect));
    }
}

// ============================================================
//  CameraGizmoResources — カメラアイコン GLB モデル描画リソース
// ============================================================

/// カメラコンポーネントを持つアクターの位置に表示するアイコンモデルのリソース。
///
/// editor/resources/models/camera.glb を InstancedModelBatch として管理し、
/// カメラアクターの数に応じてバッチを再生成する。
pub(crate) struct CameraGizmoResources {
    /// カメラ.glb の CPU モデル（バッチ更新に使用）。
    pub cpu_model: std::sync::Arc<crate::engine::core::loader::model::Model>,
    /// カメラ.glb の GPU モデル（頂点・マテリアルデータ）。
    pub gpu_model: crate::engine::methods::drawer::GpuModel,
    /// インスタンスバッチ（最大 capacity インスタンスを保持）。
    pub batch:     crate::engine::methods::drawer::InstancedModelBatch,
    /// 現在のバッチ容量。アクター数がこれを超えると再生成する。
    pub capacity:  usize,
}

// ============================================================
//  SharedModelData — 同一モデル統合バッチキャッシュ
// ============================================================

/// 同一モデルパスを参照する全アクターインスタンスを 1 つの GPU バッチに統合した
/// キャッシュエントリ。
///
/// 同じモデルを使う N 体のアクターを個別に描画すると RenderPass コマンドが N × P 個
/// （P = プリミティブ数）になるが、このバッチに統合することで P 個に削減できる。
struct SharedModelData {
    /// batch.update() に必要な CPU モデル（Arc で参照コスト=ゼロ）
    cpu_model: std::sync::Arc<crate::engine::core::loader::model::Model>,
    /// 統合インスタンスバッチ（全アクターの行列・スキンデータを保持）
    batch:     InstancedModelBatch,
    /// バッチの割り当て済みインスタンス上限（超えたら再生成する）
    capacity:  usize,
    /// ID パス用ベースオフセット=0 のバインドグループ。
    /// lod_id_buffers には絶対 ID を書き込むため base=0 で識別可能。
    id_zero_bg: (wgpu::Buffer, wgpu::BindGroup),
}

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
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum RuntimeMode {
    /// デバッグカメラ・エディタ埋め込み
    Edit,
    /// 通常ゲームプレイ・独立ウィンドウ
    Play,
}

/// App::new / App::run への引数。
pub struct LaunchArgs {
    pub parent_hwnd:      Option<isize>,
    /// 親プロセス（エディタ）のプロセス ID。
    /// 設定時は親が終了したタイミングで自プロセスも自動終了する。
    pub parent_pid:       Option<u32>,
    pub mode:             RuntimeMode,
    pub pipe_name:        Option<String>,
    /// アセットルートディレクトリの絶対パス（Play / パッケージモードで使用）。
    /// None の場合は実行ファイルの隣に assets/ or assets.pak があると仮定する。
    pub assets_root:      Option<String>,
    /// エディタリソースディレクトリの絶対パス（Edit モードで使用）。
    /// カメラギズモ等エディタ専用モデルのパス解決に使用する。
    pub editor_resources: Option<String>,
    /// Play モードで読み込むシーンのパス（仮想パス or 絶対パス）。
    /// None の場合は project_settings.json の start_scene を使用する。
    pub scene_path: Option<String>,
    /// Play 起動時に実行時コライダー描画を有効化するか（SyncViewportSettings の遅延を回避）。
    pub play_collider_draw: bool,
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
//  CameraSnapAnim — 軸ギズモクリック時のカメラ回転補間
// ============================================================

/// 軸ギズモドットをクリックした際の、カメラ回転スナップアニメーション状態。
///
/// 補間は Quaternion Slerp で行い、完了時に target_yaw/pitch を直接代入する。
pub(super) struct CameraSnapAnim {
    /// 補間開始時の回転クォータニオン
    pub start_rot:    crate::engine::structs::transforms::Quaternion,
    /// 補間目標の回転クォータニオン
    pub target_rot:   crate::engine::structs::transforms::Quaternion,
    /// 完了時に camera.yaw に直接代入する正確な値（ラジアン）
    pub target_yaw:   f32,
    /// 完了時に camera.pitch に直接代入する正確な値（ラジアン）
    pub target_pitch: f32,
    /// アニメーション継続時間（秒）
    pub duration: f32,
    /// 経過時間（秒）
    pub elapsed:  f32,
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
    /// MMB スティック HUD 描画用スクリーンスペースカメラバッファ。
    mmb_hud_cam_buf: Option<CameraBuffer>,
    scripting_host: Option<Arc<ScriptingHost>>,

    parent_hwnd:  Option<isize>,
    mode:         RuntimeMode,
    ipc:          Option<IpcClient>,
    paused:       bool,
    /// AI 実行中にレンダリングを停止して GPU リソースを LLM に解放するフラグ。
    /// PAUSE_RENDER / RESUME_RENDER IPC コマンドで切り替える。
    render_paused: bool,
    /// アセットルートのパス（Playモード・パッケージモードでのシーン自動ロードに使用）。
    assets_root:  Option<String>,
    /// エディタリソースディレクトリ（カメラギズモモデル等の読み込みに使用）。
    editor_resources: Option<String>,
    /// Play モードで読み込むシーンパス。None なら project_settings.json の start_scene を使う。
    scene_path: Option<String>,

    /// RMB 押下時のスクリーン座標。カーソルロック解除後に復元する。
    cam_grab_screen_pos: Option<(i32, i32)>,
    /// MMB 押下時のスクリーン座標。MMB 離し時にカーソルをここへ戻す。
    mmb_grab_screen_pos: Option<(i32, i32)>,

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
    /// LMB ドラッグに関連する全状態（ギズモドラッグ・矩形選択・入力状態）。
    drag:               drag_state::DragState,
    /// マウスホバー中のギズモパーツ（ハイライト表示用）。
    hovered_gizmo_part: Option<GizmoPart>,
    /// Undo/Redo 履歴。
    undo_history:       UndoHistory,
    /// Ctrl キーが押されているか。
    ctrl_held:          bool,
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
    /// Pause モードでのデバッグカメラ回転用ピボット座標（ウィンドウローカル）。
    /// RMB 押下時にウィンドウ中央へカーソルをワープして座標を固定する。
    /// DeviceEvent::MouseMotion は WS_CHILD に届かないため CursorMoved で代替する。
    pause_cam_pivot: Option<(f32, f32)>,
    /// ワープ後に OS が生成する CursorMoved イベントをスキップするためのカウンタ。
    pause_cam_warp_pending: u32,
    /// FIRST_FRAME シグナル送信済みフラグ（デバッグビルド・埋め込みモードのみ使用）。
    first_frame_sent: bool,
    /// 軸ギズモ（エディタモードのみ使用）。
    axis_gizmo: Option<crate::engine::core::font::axis_gizmo::AxisGizmo>,
    /// アイコンオーバーレイ（エディタモードのみ使用）。
    icon_overlay: Option<crate::engine::core::font::icon_overlay::IconOverlay>,
    /// 平滑化済み FPS（表示値）。0.0 = 未計算。
    fps_display: f32,
    /// FPS 計測ウィンドウ内のフレーム完了カウント。
    fps_frame_count: u32,
    /// FPS 計測ウィンドウの開始時刻。
    fps_frame_start: std::time::Instant,
    /// グリッド描画フラグ（エディタモードのみ）。
    show_grid: bool,
    /// 軸ギズモ表示フラグ（エディタモードのみ）。
    show_axis_gizmo: bool,
    /// 軸ギズモのホバー状態（どのドットにカーソルが当たっているか）。
    axis_gizmo_hovered: Option<crate::engine::core::font::axis_gizmo::AxisHit>,
    /// 軸ギズモクリック時のカメラ回転スナップアニメーション状態。
    camera_snap_anim: Option<CameraSnapAnim>,
    /// 仮想アクターノードが選択されているとき Some(dfs_id)（プライマリ選択）。
    /// ModelComponent なしアクターでもアイコン・インスペクターを表示するために使う。
    actor_virtual_selected_idx: Option<usize>,
    /// 複数アクターが選択されているときの全 DFS id リスト（マルチ選択）。
    /// 単一選択時は [primary] の 1 要素、空のとき未選択。
    selected_actor_dfs_ids: Vec<usize>,
    /// 選択中の ModelComponent スロットインデックス（Model スロット内の連番）。
    /// ピッキング時にどの MC スロットが選択されたかを記憶し、アウトライン描画・Inspectorハイライトに使う。
    actor_virtual_selected_slot_idx: usize,
    /// インスペクターフィールドドラッグ中の事前状態（Undo 1 コマンド化のために使用）。
    inspector_transform_drag: Option<InspectorTransformDrag>,

    // ── 世界線システム ───────────────────────────────────────────
    /// 現在アクティブな世界線 (0=通常シーン, N=アクター編集タブ)。
    /// active_world_line と一致する world_line を持つ Actor のみ描画・操作される。
    active_world_line: u32,
    /// 世界線切り替え時に退避するカメラ状態。キーが世界線番号。
    saved_cameras: HashMap<u32, DebugCameraData>,
    /// 2D キャンバスモードの世界線番号セット（Ortho カメラを使用する世界線）。
    /// OpenActor で 2D Actor をロードした世界線がここに登録される。
    canvas_world_lines: HashSet<u32>,
    /// アクター編集タブの 2D キャンバス世界線セット。
    /// これに含まれる世界線は canvas_screen_space_overlay フラグに関わらず
    /// 常にスクリーンスペースで描画する（アクター編集パネルは従来の 2D 表示を維持）。
    actor_edit_canvas_wls: HashSet<u32>,
    /// 2D キャンバスカメラ状態（世界線番号 → CanvasCameraData）。
    /// pan_x, pan_y, ortho_half_h を保持する。
    canvas_cameras: HashMap<u32, CanvasCameraData>,
    /// キャンバスをスクリーンスペースオーバーレイで表示するフラグ。
    /// false（デフォルト）= ワールドスペース、true = スクリーンスペースオーバーレイ。
    /// エディタのビューポートオプションから切り替え可能。実行時は常に true。
    canvas_screen_space_overlay: bool,

    // ── カメラプレビュー ─────────────────────────────────────────
    /// カメラアクター選択時のビューポートプレビュー描画リソース。
    /// 選択時に初期化され、選択解除時には None のまま。
    camera_preview: Option<CameraPreviewResources>,
    /// 現在のプレビューテクスチャ生成時のカメラターゲットサイズ (w, h)。
    /// アスペクト比変更を検出して再生成するために保持する。
    camera_preview_target_size: Option<(u32, u32)>,

    // ── カメラギズモアイコン ─────────────────────────────────────
    /// camera.glb モデルで全カメラアクター位置にアイコンを描画するリソース。
    /// 3D 編集モードの初回フレームで遅延初期化される。
    camera_gizmo: Option<CameraGizmoResources>,

    // ── 統合モデルバッチキャッシュ ──────────────────────────────────
    /// モデルパス → 統合バッチのキャッシュ。
    /// 同一モデルを参照する全アクターの行列を 1 つの InstancedModelBatch に統合し、
    /// draw call 数を「アクター数 × プリミティブ数」→「ユニークモデル数 × プリミティブ数」
    /// に削減することで RenderPass::drop() のオーバーヘッドを大幅に低減する。
    shared_model_batches: HashMap<String, SharedModelData>,

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
    /// コンテキストメニューを開いた時点のビューポートスクリーン座標。
    /// ADD_ACTOR / ADD_ACTOR_2D 受信時に pending_add_actor へ移送される。
    context_menu_screen_pos: Option<(u32, u32)>,
    /// コンテキストメニュー経由のアクター追加を次フレームで処理するためのキュー。
    /// pending_drop と同様に IDバッファ座標読み取り後にスポーン位置を確定する。
    /// タプル: (is_2d, world_line, parent_dfs_id, screen_x, screen_y)
    pending_add_actor: Option<(bool, u32, Option<u32>, u32, u32)>,

    // ── プラグインシステム ─────────────────────────────────────────
    /// ロード済みプラグインのレジストリ。
    /// 起動時に handle_resumed で初期化される。
    pub(super) plugin_registry: crate::engine::plugin::registry::PluginRegistry,

    // ── 物理システム ──────────────────────────────────────────────
    /// 固定ステップ物理スレッド。Play モード開始時に起動、停止時に Drop する。
    /// 編集時の物理シミュレーションが有効な場合は Edit モードでも起動する。
    pub(super) physics_thread: Option<crate::engine::physics::PhysicsThread>,

    /// 編集時の物理シミュレーション有効フラグ。
    /// true のとき Edit モードでも物理スレッドを起動して衝突検出を行う。
    pub(super) edit_physics_enabled: bool,

    /// 編集時の物理シミュレーションで RigidBody ダイナミクス（重力・慣性）を有効にするか。
    /// false = 全ボディを kinematic として衝突検出のみ（アクターは移動しない）。
    /// true  = 重力・動的応答も有効（アクターが物理挙動で移動する）。
    pub(super) edit_physics_with_rigidbody: bool,

    // ─── 編集時物理タイムライン ───────────────────────────────────────────────

    /// タイムラインが停止中かどうか。true = 停止、false = 再生中。
    /// edit_physics_enabled が true になった時点で true に初期化される。
    pub(super) edit_physics_paused: bool,

    /// 「最新フレームで停止」状態かどうか。
    /// true のとき、再生ボタンが押されるまでシミュレーションを進めない。
    pub(super) edit_physics_at_latest: bool,

    /// フレームスナップショットのリスト（インデックス=フレーム番号）。
    pub(super) edit_physics_snapshots: Vec<physics_timeline::PhysicsSnapshot>,

    /// 現在表示中のフレームインデックス。
    pub(super) edit_physics_current_frame: usize,

    /// 累積シミュレーション時間（秒）。スナップショット記録時に更新。
    pub(super) edit_physics_sim_time: f64,

    /// 連続して変化なしだったフレーム数。
    /// 物理スレッド起動直後は結果が遅延するため、数フレームは猶予を設ける。
    pub(super) edit_physics_no_change_count: u32,

    /// 物理エンジン再起動後のウォームアップ残フレーム数。
    /// 最新フレームから物理エンジンを再起動した直後に使用する。
    /// この値が 0 より大きい間は「変化なし停止判定」をスキップする。
    pub(super) edit_physics_warmup_frames: u32,

    /// スナップショットプレイバックモードかどうか。
    /// 過去フレームから再生する場合に true になる。
    /// 物理エンジンは動かさず、記録済みスナップショットをコマ送りで再生する。
    pub(super) edit_physics_in_playback: bool,

    /// 実行時コライダー描画フラグ。
    /// true のとき Play モードでもコライダーワイヤーフレームを描画する。
    pub(super) play_collider_draw: bool,

    /// 現在フレームで衝突中のエンティティ DFS ID セット。
    /// 物理スレッドの衝突イベント（Enter/Stay）から毎フレーム更新され、
    /// コライダーワイヤーフレームの色変更に使用する。
    pub(super) active_collision_dfs_ids: std::collections::HashSet<u64>,

    /// ギズモドラッグ中に一時的に kinematic 化している物理エンティティ ID。
    /// None = ドラッグなし。ドラッグ開始時に SetBodyKinematic(true)、
    /// 終了時に SetBodyKinematic(false) を物理スレッドへ送信するために使用する。
    pub(super) dragging_physics_entity_id: Option<u64>,

    /// コライダーのみ編集モード（edit_physics_with_rigidbody=false）でのドラッグ中の
    /// 「衝突なしの最終有効位置」。衝突検出で押し戻すときにここへ戻る。
    /// (position [x,y,z], quaternion [x,y,z,w]) の形式。
    pub(super) drag_collider_last_valid_pos: Option<([f32; 3], [f32; 4])>,

    /// コライダーのみ編集モードのドラッグ中、前フレーム末に物理スレッドへ送信した位置。
    /// 「衝突なし」の物理結果を受け取った際に last_valid_pos を更新するために使う。
    /// on_cursor_moved が update_physics より前に走り AT を更新してしまうため、
    /// 現フレームの AT ではなく「前フレームに送った位置」を安全確認済み位置として使う。
    pub(super) drag_prev_at_pos: Option<([f32; 3], [f32; 4])>,

    // ── 2D 物理システム ───────────────────────────────────────────
    /// 2D 固定ステップ物理スレッド（rapier2d バックエンド）。
    /// Play モード開始時または編集時 2D 物理有効化時に起動し、停止時に Drop する。
    pub(super) physics_thread_2d: Option<crate::engine::physics::PhysicsThread2d>,

    /// 3D キャンバスごとの 2D 物理スレッド（canvas root の Entity → PhysicsThread2d）。
    /// 各 3D キャンバスは他のキャンバスと物理が干渉しないよう独立したスレッドを持つ。
    pub(super) canvas_3d_physics: std::collections::HashMap<crate::engine::ecs::Entity, crate::engine::physics::PhysicsThread2d>,

    /// 編集時の 2D 物理シミュレーション有効フラグ。
    /// true のとき Edit モードでも 2D 物理スレッドを起動して衝突検出を行う。
    pub(super) edit_physics_2d_enabled: bool,

    /// 編集時の 2D 物理シミュレーションで RigidBody ダイナミクス（重力・慣性）を有効にするか。
    /// false = 全ボディを kinematic として衝突検出のみ。
    /// true  = 重力・動的応答も有効（キャンバスアクターが物理挙動で移動する）。
    pub(super) edit_physics_2d_with_rigidbody: bool,

    /// 現在フレームで 2D 衝突中のエンティティ DFS ID セット。
    /// 2D 物理スレッドの衝突イベント（Enter/Stay）から毎フレーム更新される。
    pub(super) active_collision_2d_dfs_ids: std::collections::HashSet<u64>,

    /// 2D ギズモドラッグ中に一時的に kinematic 化している 2D 物理エンティティ ID。
    /// None = ドラッグなし。ドラッグ開始時に SetBodyKinematic(true)、
    /// 終了時に SetBodyKinematic(false) を 2D 物理スレッドへ送信する。
    pub(super) dragging_physics_2d_entity_id: Option<u64>,

    /// 直前フレームで 2D 物理に使用したビューポートサイズ。
    /// ウィンドウリサイズ検出に使用し、変化時に static 物理ボディを再登録する。
    pub(super) last_physics_2d_viewport: Option<[f32; 2]>,
}

impl App {
    /// App インスタンスを生成する（EventLoop は run() で生成される）。
    pub fn new(args: LaunchArgs) -> Self {
        // 親プロセス（エディタ）の監視を開始する。
        // 親が終了した際に自プロセスも自動終了するバックグラウンドスレッドが起動する。
        crate::engine::core::parent_guard::watch(args.parent_pid);

        let ipc = args.pipe_name.as_deref()
            .and_then(|name| IpcClient::connect(name).ok());

        let dll_path = ScriptingHost::resolve_dll_path();
        let scripting_host = if dll_path.exists() {
            // DLL が存在する場合のみ CLR ロードを試みる（存在しない場合は hostfxr 検索で遅延するため）
            match ScriptingHost::load(&dll_path) {
                Ok(host) => Some(host),
                Err(_)   => None,
            }
        } else {
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
            mmb_hud_cam_buf: None,
            scripting_host,
            parent_hwnd:  args.parent_hwnd,
            mode:         args.mode,
            ipc,
            paused:        false,
            render_paused: false,
            assets_root:      args.assets_root,
            editor_resources: args.editor_resources,
            scene_path:       args.scene_path,
            cam_grab_screen_pos: None,
            mmb_grab_screen_pos: None,
            play_clamp: false,
            id_buffer:          None,
            selected_instances: Vec::new(),
            pending_pick:       None,
            last_cursor_pos:    None,
            line_model_buf:     None,
            tool_mode:          ToolMode::Select,
            drag:               drag_state::DragState::new(),
            hovered_gizmo_part: None,
            undo_history:       UndoHistory::new(),
            ctrl_held:          false,
            hierarchy_dirty:     false,
            last_hierarchy_send: None,
            clipboard:           Vec::new(),
            actor_clipboard:     Vec::new(),
            rmb_press_pos:          None,
            rmb_moved:              false,
            pause_cam_pivot:        None,
            pause_cam_warp_pending: 0,
            first_frame_sent:       false,
            axis_gizmo:            None,
            icon_overlay:          None,
            fps_display:           0.0,
            fps_frame_count:       0,
            fps_frame_start:       std::time::Instant::now(),
            show_grid:       true,
            show_axis_gizmo: true,
            axis_gizmo_hovered: None,
            camera_snap_anim:   None,
            actor_virtual_selected_idx:      None,
            selected_actor_dfs_ids:          Vec::new(),
            actor_virtual_selected_slot_idx: 0,
            inspector_transform_drag:     None,
            edit_physics_enabled:        false,
            edit_physics_with_rigidbody: false,
            edit_physics_paused:          true,
            edit_physics_at_latest:       true,
            edit_physics_snapshots:       Vec::new(),
            edit_physics_current_frame:   0,
            edit_physics_sim_time:        0.0,
            edit_physics_no_change_count: 0,
            edit_physics_warmup_frames:   0,
            edit_physics_in_playback:     false,
            // Play 起動時に --play-collider-draw=1 が渡された場合は即有効化する
            play_collider_draw:          args.play_collider_draw,
            active_collision_dfs_ids:     std::collections::HashSet::new(),
            dragging_physics_entity_id:   None,
            drag_collider_last_valid_pos: None,
            drag_prev_at_pos:             None,
            active_world_line: 0,
            saved_cameras: HashMap::new(),
            canvas_world_lines:    HashSet::new(),
            actor_edit_canvas_wls: HashSet::new(),
            canvas_cameras:        HashMap::new(),
            canvas_screen_space_overlay: false,
            camera_preview:              None,
            camera_preview_target_size:  None,
            camera_gizmo:                None,
            shared_model_batches:    HashMap::new(),
            pending_drop:            None,
            pending_drop_hover: None,
            drop_preview_pos:   None,
            context_menu_screen_pos: None,
            pending_add_actor:  None,
            plugin_registry:  crate::engine::plugin::registry::PluginRegistry::empty(),
            physics_thread:   None,
            physics_thread_2d:           None,
            canvas_3d_physics:           std::collections::HashMap::new(),
            edit_physics_2d_enabled:     false,
            edit_physics_2d_with_rigidbody: false,
            active_collision_2d_dfs_ids: std::collections::HashSet::new(),
            dragging_physics_2d_entity_id: None,
            last_physics_2d_viewport: None,
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

    /// 親ウィンドウ（埋め込みコンテナ）のクライアント領域サイズを返す。
    ///
    /// SetParent() でエディタコンテナに埋め込まれた後、Vulkan の currentExtent は
    /// 親コンテナの可視領域に一致する。GetParent → GetClientRect で取得することで
    /// surface.configure() に渡すべき正確なサイズが得られる。
    /// 親ウィンドウが存在しない場合（スタンドアロン Play など）は None を返す。
    fn get_parent_client_size(&self) -> Option<winit::dpi::PhysicalSize<u32>> {
        #[cfg(target_os = "windows")]
        {
            use windows_sys::Win32::Foundation::RECT;
            use windows_sys::Win32::UI::WindowsAndMessaging::{GetClientRect, GetParent};
            let hwnd = self.window_hwnd();
            if hwnd == 0 { return None; }
            unsafe {
                let parent = GetParent(hwnd as _);
                if parent.is_null() { return None; }
                let mut rect = RECT { left: 0, top: 0, right: 0, bottom: 0 };
                if GetClientRect(parent, &mut rect) == 0 { return None; }
                let w = (rect.right  - rect.left) as u32;
                let h = (rect.bottom - rect.top)  as u32;
                if w > 0 && h > 0 {
                    return Some(winit::dpi::PhysicalSize { width: w, height: h });
                }
            }
        }
        None
    }
}

// ============================================================
//  アクター・ユーティリティ（別モジュールに分離）
// ============================================================

mod actor_utils;
mod platform_utils;
mod slot_ops;

// actor_utils / platform_utils の関数を親名前空間に再エクスポートする。
// サブモジュール（render.rs 等）は既存の `use super::fn_name` のまま使用可能。
use actor_utils::{
    collect_actor_nodes, build_hierarchy_json,
    find_actor_by_dfs, find_actor_by_dfs_mut,
    canvas_anchor_offset_for_dfs, collect_canvas_actors_in_rect,
    collect_transform_only_in_rect,
    collect_mcs_in_world_line, update_all_mc_batches_for_wl,
    remove_actor_by_dfs, actor_subtree_size, find_parent_canvas_info,
    collect_entities_for_wl, despawn_actor_recursive,
    count_actor_dfs_nodes, extract_actor_by_dfs,
    selection_centroid, world_to_screen,
    collect_child_actor_mc_starts, collect_child_actor_old_states,
    apply_delta_to_actor_children,
    find_parent_actor_of_dfs, get_3d_canvas_world_mat,
};
use platform_utils::{
    camera_grab_start, camera_grab_end, apply_window_clamp, release_window_clamp,
    warp_cursor_to_local, mmb_grab_start, mmb_grab_end,
};

