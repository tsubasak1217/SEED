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
/// インスペクタのフィールド編集を汎用的に Undo/Redo へ載せる機構（分類表・スナップショット・適用）
mod field_edit;
/// インスペクタ各行の「⟲ デフォルトに戻す」を賄うコンポーネント非依存のリセット機構
/// （RESET_COMPONENT_FIELD）。
mod component_reset_ops;
mod ipc_handler;
mod hierarchy_sync;
mod clipboard;
mod actor_ops;
mod rename_refs;
mod terrain_scatter_actor_ops;
mod canvas_edit_ops;
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
mod canvas_drop;
mod render;
mod frame_renderer;
mod canvas_collect;
mod collider2d_wireframe;
mod collider3d_pick;
mod app_init;
mod event_handler;
mod drag_handler;
mod physics_ops;
mod physics_timeline;
mod tab_physics;
mod script_scene_ops;
/// 埋め込みインプレース Play（フェーズ2）: ENTER_PLAY / EXIT_PLAY の状態遷移とアクター退避/復元。
mod play_mode_ops;
/// 【一時】埋め込み Play の凍結/黒画面 診断計器（ウォッチドッグ・ステージ印・イベントトレース）。原因確定後に撤去。
mod play_diag;
mod audio_ops;
/// 水ボリューム（WaterVolumeComponent）のインスペクタ更新（SET_WATER_FIELD）
mod water_ops;
/// 水位グラフのリンク（WaterLinkComponent）のインスペクタ更新（SET_WATER_LINK_FIELD。W2.5）
mod water_link_ops;
/// インタラクションソース（InteractionSourceComponent）のインスペクタ更新（SET_INTERACTION_FIELD）
mod interaction_ops;
/// コントロールポイント（ControlPointComponent）の編集・点選択（SET_CONTROL_POINT*）
pub(crate) mod control_point_ops;
/// コントロールポイントのビューポート可視化（点キューブ＋区間ライン。Edit モード限定）
pub(crate) mod control_point_scene_gizmo;
mod animation_ops;
pub(crate) mod light_ops;
pub(crate) mod skybox_ops;
pub(crate) mod particle_ops;
pub(crate) mod light_scene_gizmo;
pub(crate) mod jointattach_ops;
pub(crate) mod jointattach_scene_gizmo;
pub(crate) mod skybox_scene_gizmo;
pub(crate) mod particle_scene_gizmo;
mod prefab_ops;
pub(crate) mod camera_scene_gizmo;
pub(super) mod terrain_ops;
pub(crate) mod terrain_mesh_build;
pub(crate) mod terrain_layer_albedo;
/// 地形プロップ散布（草・木）のエンジン統合層（散布データ ⇄ ECS/GPU/IPC の橋渡し）。
pub(super) mod terrain_scatter_ops;

// ── 外部クレート・標準ライブラリ ────────────────────────────
use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use winit::event_loop::{ControlFlow, EventLoop};
use winit::window::Window;

// ── エンジン内部モジュール ───────────────────────────────────
use crate::engine::core::clock::Clock;
use crate::engine::core::input::Input;
use crate::engine::core::renderer::Renderer;
use crate::engine::core::app_base::ipc::{IpcClient, ToolMode, GizmoSpace};
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
    /// 深度テクスチャのビュー（All aspect: レンダーアタッチメント用）
    depth_view:    wgpu::TextureView,
    /// 深度テクスチャの DepthOnly aspect ビュー。
    ///
    /// デファードのライティングパス（案A: ミニデファード）が深度を `texture_depth_2d`
    /// としてサンプルするために使う。Depth24PlusStencil8 はステンシル面を持つため、
    /// サンプルには DepthOnly aspect ビューが必須（メイン深度テクスチャの
    /// `depth_only_view` と同じ役割・同じ理由）。
    depth_sample_view: wgpu::TextureView,
    /// プレビュー専用 G-Buffer 4 枚（案A: ミニデファード）。
    ///
    /// カメラプレビューを「本番と同じ Deferred（G-Buffer → ライティング）」で描くための
    /// 小さな G-Buffer 一式。地形のレイヤブレンド（terrain_gbuffer_write.wgsl）と草
    /// （grass_gbuffer.wgsl）は G-Buffer MRT へ焼くパイプラインでしか描けないため、
    /// フォワード小窓ではなくここへ焼いてからフルスクリーン・ライティングで復元する。
    /// テクスチャ本体は所有し続ける必要があるため（ビューは借用）フィールドで保持する。
    /// フォーマット・命名はメイン G-Buffer（renderer/gbuffer.rs の GBUFFER*_FORMAT）と一致。
    gbuffer_textures: [wgpu::Texture; 4],
    /// G-Buffer 4 枚のビュー（レンダーターゲット兼サンプリング用）。
    gbuffer_views:    [wgpu::TextureView; 4],
    /// プレビュー専用の速度 RT（G-Buffer の RT4）。**誰も読まない捨て先**。
    ///
    /// プレビューは TAA もモーションブラーも掛けないので速度は不要だが、
    /// G-Buffer パイプラインを本番と共有している以上カラーターゲット 5 枚ぶんの
    /// アタッチメントが要る（詳細は `begin_gbuffer_pass_to_depth` のコメント）。
    /// 加えてプレビューカメラの uniform は `prev_view_proj = view_proj` で更新するため、
    /// ここへ書かれる値はカメラ由来ぶんが常に 0（＝実質無効化されている）。
    velocity_texture: wgpu::Texture,
    /// 速度 RT のビュー（レンダーターゲット用）。
    velocity_view:    wgpu::TextureView,
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
        // オフスクリーンカラーテクスチャ（HDR: Rgba16Float）。
        // Phase R3 でメッシュシェーダから Reinhard を撤去したため、プレビューも HDR で
        // 描画し、ブリット時（camera_preview_blit.wgsl）にトーンマップして表示する。
        // メインシーンのパイプラインは HDR_FORMAT でビルドされるため、プレビューの
        // レンダーターゲットも同フォーマットに揃える必要がある。
        let color_texture = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Camera Preview Color"),
            size:            wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          crate::engine::core::renderer::HDR_FORMAT,
            usage:           wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats:    &[],
        });
        let color_view = color_texture.create_view(&wgpu::TextureViewDescriptor::default());

        // オフスクリーン深度テクスチャ。
        //   案A（ミニデファード）でデファードのライティングパスが深度をテクスチャとして
        //   サンプルするため、RENDER_ATTACHMENT に加えて TEXTURE_BINDING を付与する
        //   （メイン深度テクスチャ DepthTexture と同じ用途構成）。
        let depth_texture = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Camera Preview Depth"),
            size:            wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Depth24PlusStencil8,
            usage:           wgpu::TextureUsages::RENDER_ATTACHMENT
                           | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats:    &[],
        });
        // All aspect: レンダーパスの depth_stencil_attachment 用（深度＋ステンシル）。
        let depth_view = depth_texture.create_view(&wgpu::TextureViewDescriptor::default());
        // DepthOnly aspect: デファードライティングが texture_depth_2d としてサンプルする用。
        let depth_sample_view = depth_texture.create_view(&wgpu::TextureViewDescriptor {
            aspect: wgpu::TextureAspect::DepthOnly,
            ..Default::default()
        });

        // プレビュー専用 G-Buffer 4 枚（案A: ミニデファード）。
        //   フォーマットはメイン G-Buffer（renderer/gbuffer.rs の GBUFFER*_FORMAT）と一致必須。
        //   RENDER_ATTACHMENT（G-Buffer パスの MRT 出力先）＋ TEXTURE_BINDING（ライティング
        //   パスが textureLoad で読む）を付与する。プレビュー解像度は小さい（高さ固定・
        //   幅アスペクト比）ため、4 枚合わせても VRAM は数 MB に収まる。
        use crate::engine::core::renderer::gbuffer::{
            GBUFFER0_FORMAT, GBUFFER1_FORMAT, GBUFFER2_FORMAT, GBUFFER3_FORMAT,
        };
        let gbuffer_formats = [GBUFFER0_FORMAT, GBUFFER1_FORMAT, GBUFFER2_FORMAT, GBUFFER3_FORMAT];
        // テクスチャ本体（所有し続ける）とビュー（借用）を同順で 4 枚ぶん生成する。
        let g_tex_view: [(wgpu::Texture, wgpu::TextureView); 4] = std::array::from_fn(|i| {
            let tex = device.create_texture(&wgpu::TextureDescriptor {
                label:           Some("Camera Preview GBuffer"),
                size:            wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
                mip_level_count: 1,
                sample_count:    1,
                dimension:       wgpu::TextureDimension::D2,
                format:          gbuffer_formats[i],
                usage:           wgpu::TextureUsages::RENDER_ATTACHMENT
                               | wgpu::TextureUsages::TEXTURE_BINDING,
                view_formats:    &[],
            });
            let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
            (tex, view)
        });
        let [(gt0, gv0), (gt1, gv1), (gt2, gv2), (gt3, gv3)] = g_tex_view;
        let gbuffer_textures = [gt0, gt1, gt2, gt3];
        let gbuffer_views    = [gv0, gv1, gv2, gv3];

        // プレビュー専用の速度 RT（G-Buffer の RT4 = MRT 5 枚目）。捨て先なので
        // TEXTURE_BINDING は付けない（誰もサンプルしない＝ドライバに意図を明示する）。
        let velocity_texture = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Camera Preview GBuffer Velocity (discarded)"),
            size:            wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          crate::engine::core::renderer::gbuffer::GBUFFER_VELOCITY_FORMAT,
            usage:           wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats:    &[],
        });
        let velocity_view = velocity_texture.create_view(&wgpu::TextureViewDescriptor::default());

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
            depth_texture, depth_view, depth_sample_view,
            gbuffer_textures, gbuffer_views,
            velocity_texture, velocity_view,
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
    /// 静的地形チャンクの毎フレーム update スキップ（Fix B）用スナップショット。
    /// `(統合インスタンス行列, 各インスタンスの絶対 ID)` を前回 `batch.update()` 時の値で保持する。
    /// 地形チャンクキー（`TERRAIN_SOURCE_SCHEME`）のときだけ設定・比較する。次フレームの
    /// merge 結果がこれと完全一致すれば mark_dirty()+update()+id バッファ書込をまるごと省ける
    /// （描画結果・ピッキング ID ともに不変）。
    /// 行列だけでなく abs_ids も比較するのは、行列不変でもアクター追加/削除で id_base が
    /// ずれると lod_id_buffers（ピッキング）が陳腐化するのを防ぐため。
    /// 非地形（通常モデル・散布・アニメ）は None のままで、従来どおり毎フレーム更新する。
    uploaded_sig: Option<(Vec<[[f32; 4]; 4]>, Vec<u32>)>,
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

// ============================================================
//  PlaySnapshot — 埋め込みインプレース Play のアクター状態退避
// ============================================================

/// 埋め込み Play（ENTER_PLAY）開始時に取るシーンのトップレベルアクター状態のスナップショット。
///
/// 【設計】高速化の本体は「地形・散布・GPU リソースを作り直さない」こと。そのため
/// スナップショットは **シーン世界線（world_line == 0）の非地形アクターのみ** を対象とし、
/// 地形ルート（`TERRAIN_ROOT_NAME`）とアクター編集タブ（world_line > 0）のアクターは
/// 「現物を保持（Keep）」して一切触らない。Play 中に地形は変化せず（スクリプトに地形編集
/// API は無い）、編集タブはシミュレートされないため、保持で状態は保たれる。
///
/// トップレベルアクターの **並び順を保持** することで、復元後も DFS ID の対応
/// （物理・スクリプトのイベント配信が依存）を Play 前と一致させる。
pub struct PlaySnapshot {
    /// Play 前のトップレベルアクター列（順序保持）。各要素が復元方法を持つ。
    entries: Vec<PlaySnapshotEntry>,
}

/// トップレベルアクター 1 体分のスナップショット項目。
enum PlaySnapshotEntry {
    /// スナップショット対象（world_line == 0・非地形）。ActorData から再構築する。
    Restore(ActorData),
    /// 保持対象（地形ルート・world_line > 0）。ルート entity をキーに現物を退避して戻す。
    Keep(crate::engine::ecs::Entity),
}

/// Edit モードのビューポート表示モード（エディタの「3Dシーン / 2Dシーン」タブに対応）。
///
/// Edit モードかつシーン世界線（world_line = 0）でのみ意味を持つ。
/// Play モードやアクター編集タブ（world_line > 0）には影響しない。
/// IPC コマンド `EDIT_VIEW:3d` / `EDIT_VIEW:2d` で切り替える。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum EditViewMode {
    /// 3D シーンビュー（デフォルト）。
    /// 3D オブジェクトのみ表示し、スクリーンスペースキャンバス（Actor2D）は
    /// 描画・ピッキング・ギズモをすべて非表示にする。
    /// 3D ワールドキャンバス（Actor3D + CanvasComponent）は従来どおり表示する。
    View3D,
    /// 2D シーンビュー。
    /// 3D シーン（モデル・3D グリッド・3D ギズモ等）を非表示にし、
    /// 全スクリーンスペースキャンバスを Play 時と同じ WYSIWYG レイアウトで重ね表示する。
    /// カメラ操作は 2D パン・ズーム（canvas_cameras[0]）に切り替わる。
    View2D,
}

/// キャンバス編集タブ 1 セッション分の状態。
///
/// EDIT_CANVAS_BEGIN でシーン世界線（0）から専用世界線へ移動したアクターの
/// 出自情報を保持し、EDIT_CANVAS_END で元の位置・状態へ正確に復元する。
pub struct CanvasEditSession {
    /// 復帰先の親アクターエンティティ（None = シーン直下のトップレベル）。
    /// エンティティは世代付きで安定なため、セッション中に親が移動しても追跡できる。
    parent_entity: Option<crate::engine::ecs::Entity>,
    /// 復帰時の挿入位置（トップレベルなら scene.actors の index、
    /// 子なら親の children 内 index）。範囲外になった場合は末尾へクランプする。
    child_index: usize,
    /// 対象が 3D ワールドキャンバス（Actor3D + CanvasComponent）だった場合 true。
    /// BEGIN 時に一時的に Actor2D 化 + CanvasTransform 付与しており、
    /// END 時に Actor3D へ戻して CanvasTransform を除去する必要がある。
    was_3d_root: bool,
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
    /// スクリプト Audio API 用のオーディオマネージャ（初回コマンド時に遅延初期化）
    audio:          Option<crate::engine::core::audio::AudioManager>,
    /// シーンレジストリ: シーンマネージャ登録名 → assets:// パス
    /// （project_settings.json の "scenes" 配列から起動時に読み込む）。
    /// スクリプトの SEED.Scene.Load / Transition が名前解決に使う。
    scene_registry: HashMap<String, String>,
    /// スクリプトの Scene.Load で事前読み込みされたシーン
    /// （(解決済みパス, シーン, デバッグカメラ)。Transition 時に消費される）。
    preloaded_scene: Option<(String, Scene, Option<DebugCameraData>)>,
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
    /// AI 実行中にレンダリングを停止して GPU リソースを LLM に解放するフラグ。
    /// PAUSE_RENDER / RESUME_RENDER IPC コマンドで切り替える。
    render_paused: bool,
    /// ウィンドウがフォーカスされているか（winit の WindowEvent::Focused で更新）。
    /// フォーカスが無い間はフレームレートを抑え、遮蔽時の present 即時リターンによる
    /// 暴走ループ（毎秒数千フレーム）を防ぐために使う。
    window_focused: bool,
    /// 最後にフレーム処理（handle_redraw_requested）へ入った時刻。
    /// 埋め込みウィンドウが他タブの裏へ隠れると OS が WM_PAINT を配送しなくなり
    /// RedrawRequested が途絶える（＝フレームループ停止）。その間も IPC を処理するため、
    /// about_to_wait 側がこの時刻からの経過でフレーム途絶を判定する。
    last_frame_at: std::time::Instant,
    /// 最後に about_to_wait 側から IPC をポンプした時刻。
    /// ControlFlow::Poll で about_to_wait は毎秒数十万回呼ばれるため、
    /// 時間ゲートで実際のポンプ頻度を抑えるのに使う。
    last_ipc_pump_at: std::time::Instant,
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
    /// 2D ピックの巡回選択状態（同一地点の連続クリックで次候補へ回すため）。
    /// (直前クリックのスクリーン座標, 優先度順の候補 DFS リスト, 現在選択インデックス)。
    pick_2d_cycle:      Option<([f32; 2], Vec<usize>, usize)>,
    /// 直前フレームのカーソル座標（ビューポートローカル）。
    last_cursor_pos:    Option<(f32, f32)>,
    /// ギズモ描画用の単位行列モデルバッファ。
    line_model_buf:     Option<(wgpu::Buffer, wgpu::BindGroup)>,
    /// 現在のエディタツールモード。
    tool_mode:          ToolMode,
    /// ギズモ座標系モード（World / Local）。デフォルトは World（従来の挙動）。
    gizmo_space:        GizmoSpace,
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
    /// レンダリング機能マトリクス（影/GI/反射/AO/半透明のモード集合）。
    /// プロジェクト設定・IPC（RT_SHADOWS / SET_POST_FX の features）で更新する。
    /// 実行時分岐は resolve() 済みの ResolvedFeatures を参照する（frame_renderer）。
    pub(crate) render_features: crate::engine::core::renderer::RenderFeatures,
    /// [SEED FEATURES] ログの重複抑制キャッシュ（前回ログした (features, rt_supported)）。
    features_log_state: Option<(crate::engine::core::renderer::RenderFeatures, bool)>,
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
    /// 直近に記録したインスペクタのフィールド編集（連続編集を 1 コマンドへまとめるために保持）。
    /// 詳細は field_edit.rs のヘッダを参照。
    field_edit_session: Option<field_edit::FieldEditSession>,
    /// アクタ散布（kind=Actor プロップ）用のプレハブ ActorData キャッシュ。
    /// ブラシ散布は 1 ストロークで何十回も飛んでくるため、同じ .actor ファイルの
    /// 再読込・再パースを避ける。プレハブ内容が変わりうる操作（ルール再散布の開始・
    /// プレハブ再展開系）でクリアする。
    pub(super) scatter_prefab_cache: std::collections::HashMap<
        String, crate::engine::structs::objects::actor::ActorData>,

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
    /// キャンバス編集タブのアクティブセッション（世界線番号 → セッション情報）。
    /// EDIT_CANVAS_BEGIN でシーンから移動したアクターの出自を記録し、
    /// EDIT_CANVAS_END で元の位置へ戻すために使用する。
    canvas_edit_sessions: HashMap<u32, CanvasEditSession>,
    /// キャンバスをスクリーンスペースオーバーレイで表示するフラグ。
    /// false（デフォルト）= ワールドスペース、true = スクリーンスペースオーバーレイ。
    /// エディタのビューポートオプションから切り替え可能。実行時は常に true。
    /// EDIT_VIEW コマンド受信時にビューモードと同期される
    /// （View2D = true / View3D = false）。
    canvas_screen_space_overlay: bool,
    /// Edit モードのビューポート表示モード（3Dシーン / 2Dシーンタブ）。
    /// デフォルトは View3D。IPC の `EDIT_VIEW:3d` / `EDIT_VIEW:2d` で切り替える。
    /// Edit モードかつシーン世界線（world_line = 0）でのみ参照される。
    edit_view_mode: EditViewMode,

    // ── HDR ポストプロセス（Phase R3）────────────────────────────
    /// 名前付きレンダーターゲットプール（シーン HDR オフスクリーン等の確保・再利用）。
    /// 毎フレーム ensure でサーフェスサイズに追従する。静的リソース（トーンマップ等の
    /// パイプライン）は DrawContext.post が持つ。
    rt_pool: crate::engine::core::renderer::RtPool,
    /// すりガラス用の屈折背景ミップチェーン（ガラス表現）。translucency=Rt かつ deferred かつ
    /// 半透明ありのフレームで、不透明シーン HDR のミップ（下位ほど強くぼかし）を作る。
    /// 半透明フラグメントが roughness からミップレベルを選んで「すりガラス」を表現する。
    refract_pyramid: crate::engine::core::renderer::RefractPyramid,
    /// 水面描画パス（Phase W1）のリソース一式。`engine::water` が解決した水ボリュームを
    /// 1 ドローで描き、屈折の背景グラブ・波法線・吸収・岸フォーム・フレネルを合成する。
    ///
    /// `Option` なのは、パイプライン構築に `wgpu::Device` が要る一方 `App::new` は
    /// デバイス確立前に呼ばれるため（`hiz` と同じ遅延構築方式）。
    /// 水ボリュームが存在するフレームで初めて構築され、以後使い回される
    /// （水を置かないプロジェクトではパイプラインすら作られない＝起動コスト 0）。
    water_renderer: Option<crate::engine::core::renderer::WaterRenderer>,
    /// 岸波のショアフィールド（Phase W1.5）の CPU キャッシュ。
    ///
    /// 水域ごとに「水深・符号付き岸距離・岸方向」の俯瞰 2D を焼いて保持する。
    /// GPU リソースを持たない純 CPU データなので `Option` にせず常設する
    /// （岸波を使う水域が無ければ中身は空のまま＝メモリもコストも 0）。
    /// ベイクは地形編集・水パラメータ変更・Ocean のカメラ移動を検知したときだけ、
    /// 数百 ms のデバウンスを挟んで走る（毎フレームは焼かない）。
    water_shore_fields: crate::engine::water::ShoreFieldSet,
    /// 水位グラフ（Phase W2.5）の時間発展を担う状態。
    ///
    /// 保持するのは固定ステップのアキュムレータと直近フレームの流量イベントだけで、
    /// 水位そのものは `WaterVolumeComponent.sim_level_y`（揮発フィールド）にある。
    /// **Play かつ非ポーズのときだけ進み、Play を抜けると設定水位へ戻す**
    /// （水位は演出ではなくゲーム状態なので、波と違い Edit 中は動かさない）。
    /// GPU リソースを持たないので `Option` にせず常設する（対象水域が 0 ならコスト 0）。
    water_level_sim: crate::engine::water::WaterLevelSim,
    /// 地形の**密度**が編集されるたびに進む単調カウンタ。
    ///
    /// ショアフィールドの再ベイク判定に使う。`TerrainState` ではなく `App` に置くのは、
    /// シーン読み込み・地形作り直しで `TerrainState` が丸ごと差し替わる
    /// （＝カウンタが 0 に戻り「変化していない」と誤判定する）ため。
    /// LOD 遷移の再メッシュでは進めない（密度は変わっていないので焼き直す理由が無い）。
    terrain_edit_version: u64,
    /// 瞬発インタラクションフィールド（Phase I1）の GPU リソース一式。
    ///
    /// 動く `InteractionSource` の速度をワールド俯瞰テクスチャへ焼き、草・（将来の）水面が
    /// 頂点/フラグメント段で読む。`Option` なのは構築に `wgpu::Device` が要るため
    /// （`water_renderer` と同じ遅延構築方式）。
    /// **草を描くフレーム、またはソースが 1 個でもあるフレーム**で初めて構築される
    /// （草もソースも無いプロジェクトでは 4MB のテクスチャすら確保されない）。
    interaction_field: Option<crate::engine::core::renderer::InteractionFieldRenderer>,
    /// ビューポートで選択中のコントロールポイント（アクタ＋スロット＋点添字）。
    ///
    /// `Some` の間だけ**移動ギズモの対象がアクタ Transform からその点へ切り替わる**。
    /// アクタ選択の変更・空クリック・シーン切替で解除する。
    pub(crate) selected_control_point: Option<control_point_ops::SelectedControlPoint>,
    /// インタラクションソースの速度算出（前フレーム位置の保持）。
    ///
    /// GPU リソースを持たないので `Option` にせず常設する。シーン切替時は
    /// `clear()` を呼んで位置履歴を捨てること（不連続な巨大速度の防止）。
    interaction_velocity: crate::engine::interaction::InteractionSourceVelocityTracker,
    /// AO（SSAO / RT-AO, Phase D4）の半解像度テクスチャ群（ao_raw / ao_a / ao_b）。
    /// STORAGE_BINDING を要するため RtPool には載せられず本フィールドが専有する。
    /// 毎フレーム ensure で半解像度サイズに追従する（AO 生成パイプラインは DrawContext.pipelines.ao）。
    ao_targets: crate::engine::core::renderer::AoTargets,
    /// SSGI（スクリーンスペース GI, Phase SSGI）の半解像度テクスチャ群（ssgi_raw / ssgi_a / ssgi_b）。
    /// AoTargets と同型だが .rgb カラー・**フレーム跨ぎ永続**（1 フレーム遅延方式で ssgi_b が前フレーム
    /// 結果を保持する）。毎フレーム ensure で半解像度サイズに追従する（生成は DrawContext.pipelines.ssgi）。
    ssgi_targets: crate::engine::core::renderer::SsgiTargets,
    /// 水中コースティクス（Phase W5.3）のフル解像度 1ch テクスチャ。
    /// deferred 有効かつ水域があるフレームだけ ensure・生成する（それ以外は 0 コスト）。
    /// **フレーム跨ぎの履歴は持たない**（毎フレーム 0 クリアして焼き直す）。
    caustics_targets: crate::engine::core::renderer::CausticsTargets,
    /// 水面反射の粗さブラー（Phase W5.2）用フル解像度 ping-pong テクスチャ 2 枚。
    /// **粗さ > 0 の水域があるフレームだけ** ensure する（粗さ 0＝鏡面のシーンでは 0 コスト）。
    /// STORAGE_BINDING を要するため RtPool には載せられず本フィールドが専有する。
    water_reflection_blur_targets: crate::engine::core::renderer::WaterReflectionBlurTargets,
    /// RT ソフト影マスク（Phase RT-Shadow-Denoise）の半解像度 4 レイヤテクスチャ群（mask_raw/a/b）。
    /// STORAGE_BINDING を要するため RtPool には載せられず本フィールドが専有する。deferred+RT 影+ソフト影
    /// ありのフレームのみ ensure する（生成は DrawContext.pipelines.shadow_mask）。
    shadow_mask_targets: crate::engine::core::renderer::ShadowMaskTargets,
    /// SSGI が「収束済み」か（ssgi_b に有効な前フレーム結果があるか）。false のフレームは
    /// GiParams を enabled=false でフラットに倒す（初回・リサイズ直後・SSGI 有効化直後の 1 フレーム）。
    /// SsgiTargets::ensure の再確保通知と「前フレームも SSGI が active だったか」で更新する。
    ssgi_warmed: bool,
    /// **前フレームの** ViewProjection 行列（速度バッファ＝モーションベクタ用）。
    ///
    /// `None` は「前フレームが存在しない／連続していない」ことを表し、そのフレームは
    /// `prev_view_proj = view_proj` を積む（＝静的ジオメトリの速度が厳密に 0 になる）。
    ///
    /// ## `None` へ落とす（＝速度をリセットする）条件（要件: 速度を爆発させない）
    /// 1. 初回フレーム（初期値が `None`）
    /// 2. Play⇄Edit の切替・ポーズ状態の変化（カメラがデバッグカメラ⇄ゲームカメラで飛ぶ）
    /// 3. シーンロード／ワールドライン切替（`request_velocity_reset` 経由）
    /// 4. カメラのテレポート（同上。スクリプト等が明示的に要求する）
    /// 5. レンダーターゲットのリサイズ（速度 RT が作り直され履歴が無効になる）
    ///
    /// 2 と 5 は本フィールドと対の `velocity_prev_key` による自動検出、
    /// 3 と 4 は `velocity_reset_requested` フラグで扱う。
    velocity_prev_view_proj: Option<crate::engine::structs::tensor::Mat4x4<f32>>,
    /// 前フレームの「速度の連続性キー」
    /// = (Play か, ポーズ中か, RT 幅, RT 高さ, ワールドライン)。
    /// これが変化したフレームは前フレームとの連続性が無いとみなして速度をリセットする。
    velocity_prev_key: Option<(bool, bool, u32, u32, u32)>,
    /// 次フレームで速度をリセットする明示要求（シーンロード・カメラテレポート）。
    /// 立っているフレームで `velocity_prev_view_proj` を `None` へ落として消費する。
    velocity_reset_requested: bool,

    /// GPU パーティクルシステム（Phase RP）。エミッタごとの GPU バッファ・CPU 状態を保持する。
    /// rt_pool と同じく eager 構築（デバイス不要。GPU パイプラインは DrawContext.pipelines が持つ）。
    /// 毎フレーム collect_and_consume（CPU）→ sync_gpu/dispatch/draw（GPU）で更新・描画する。
    particle_system: crate::engine::core::renderer::ParticleSystem,
    /// スカイボックス（天球）システム（Phase R9）。Skybox スロットごとの GPU 状態
    /// （uniform バッファ・BindGroup）とテクスチャキャッシュを保持する。particle_system と
    /// 同じく eager 構築（パイプラインは DrawContext.pipelines.skybox が持つ）。
    /// 毎フレーム collect（CPU）→ sync_gpu/draw（GPU）で更新・描画する。
    skybox_system: crate::engine::core::renderer::SkyboxSystem,
    /// Hi-Z オクルージョンカリングシステム（地形チャンク・**opt-in**）。
    /// `SEED_OCCLUSION_CULL=1` のフレームで初回レイジー構築し、G-Buffer 深度から Hi-Z を作り、
    /// 各地形チャンク AABB を投影・比較して「完全遮蔽」のチャンクを 1 フレーム遅延で描画スキップする。
    /// 既定（未設定）では None のまま一切構築・実行しない（オーバーヘッド 0）。
    hiz: Option<crate::engine::core::renderer::hiz::HiZSystem>,
    /// 直近フレームで Hi-Z ディスパッチした地形チャンクの key（path）列。
    /// 次フレームの `try_read_results` が返す可視性 `Vec<u32>` と同順で対応させ、
    /// 遮蔽チャンク集合（描画スキップ）へ変換するためのインデックス→key マップ。
    hiz_prev_keys: Vec<String>,
    /// このフレームの終わり（frame.finish 後）に Hi-Z staging のマップ予約が必要か。
    hiz_need_map: bool,
    /// ビネットポストパスの有効フラグ（デフォルト OFF）。
    /// project_settings.json の `post_vignette`（bool, 既定 false）を起動時に読み込む。
    /// 有効時はトーンマップ前段にビネットを挿す（ポストパスチェーンの実証）。
    post_vignette_enabled: bool,
    /// ブルーム／FXAA のポストエフェクト設定（Phase R4, 既定すべて OFF）。
    /// project_settings.json（起動時）＋ IPC `SET_POST_FX:{json}`（実行時）で更新する。
    post_fx: crate::engine::core::renderer::PostFxSettings,
    /// エディタのシーンビュー（デバッグカメラ）表示モード（既定 Lit=フル PBR）。
    /// IPC `SET_POST_FX:{json}` の `view_mode` で切り替える。Edit モードのメインカメラ
    /// のパスにのみ効き、プレビュー小窓・Play の見た目には影響しない（frame_renderer 参照）。
    /// セッション内のみ有効（永続化しない）。
    scene_view_mode: crate::engine::core::renderer::SceneViewMode,
    /// 環境光（アンビエント）の色（リニア RGB, Phase R1.5, 既定 白）。
    /// 毎フレーム LightBuffer::update 経由で LightMeta.ambient_color へアップロードする。
    ambient_color: [f32; 3],
    /// 環境光（アンビエント）の強度（Phase R1.5, 既定 0.05）。0 で完全な暗闇。
    /// project_settings.json（起動時, `ambient_intensity`）＋ IPC `SET_AMBIENT`（実行時）で更新する。
    ambient_intensity: f32,

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

    // ── ライトギズモアイコン ─────────────────────────────────────
    /// ライト用アイコン（暫定的に camera.glb を流用）で全ライトアクター位置に
    /// アイコンを描画するリソース。CameraGizmoResources と同一構造を流用する。
    /// 3D 編集モードの初回フレームで遅延初期化される。
    light_gizmo: Option<CameraGizmoResources>,

    // ── パーティクルエミッタギズモアイコン ────────────────────────
    /// パーティクルエミッタ用アイコン（暫定的に camera.glb を流用）で
    /// 全エミッタアクター位置にアイコンを描画するリソース。
    /// 3D 編集モードの初回フレームで遅延初期化される。
    particle_gizmo: Option<CameraGizmoResources>,

    // ── 統合モデルバッチキャッシュ ──────────────────────────────────
    /// モデルパス → 統合バッチのキャッシュ。
    /// 同一モデルを参照する全アクターの行列を 1 つの InstancedModelBatch に統合し、
    /// draw call 数を「アクター数 × プリミティブ数」→「ユニークモデル数 × プリミティブ数」
    /// に削減することで RenderPass::drop() のオーバーヘッドを大幅に低減する。
    shared_model_batches: HashMap<String, SharedModelData>,

    /// 前フレームで確定したメインカメラのワールド位置。
    /// チャンク単位 地形 LOD（`tick_terrain_lod`）が、描画の借用と衝突しないフレーム先頭で
    /// カメラ距離を評価するために使う（1 フレーム遅れは体感できない）。フレーム後半の
    /// `saved_camera_pos` 確定時に更新する。
    last_camera_pos: [f32; 3],

    /// 統合バッチキー（batch_key = モデルパス＋マテリアルオーバーライド署名）ごとの
    /// 「このフレームの描画対象に不在だった連続フレーム数」。
    /// マテリアルのインライン編集（スライダードラッグ等）は署名が変わるたびに新しい
    /// batch_key を生む。以前は `shared_model_batches` にも RT の BLAS キャッシュにも
    /// 削除処理が無く、編集のたびにユニークキーの GPU リソース（インスタンスバッファ・
    /// 巨大 BLAS・全テクスチャ込みの GpuModel）が無制限に蓄積して数秒で VRAM が枯渇していた。
    /// このマップで「N フレーム連続で不在」を検出し、遅延して安全に解放する（誤解放防止）。
    /// alive（今フレーム存在）になったキーはエントリを除去してカウンタをリセットする。
    batch_absent_frames: HashMap<String, u32>,

    // ── ドラッグ&ドロップ ───────────────────────────────────────
    /// DROP_ACTOR コマンドを受け取ったときに設定する。
    /// 次フレームの ID パス後にワールド座標を読み出してアクターを配置する。
    /// タプル: (actor_path, screen_x, screen_y)
    pending_drop: Option<(String, u32, u32)>,
    /// 水面シェーディングアセットのパラメータ宣言（W8.2）が変わったので、
    /// 選択中アクタの ACTOR_COMPONENTS を送り直す必要があるか。
    ///
    /// 検出は描画準備の最中（`&mut self.renderer` を借りている最中）に起きるため、
    /// その場では `&self` を要求する送信メソッドを呼べない。フラグだけ立てておき、
    /// GPU サブミット後に処理する（1 フレーム遅れるが、ホットリロードの体感には出ない）。
    pending_water_param_decls_resend: bool,
    /// L3 シェーディングアセットのパラメータ宣言が変わったので、
    /// 選択中アクタの `ACTOR_COMPONENTS` と `SCENE_SHADING_PARAMS` を送り直す必要があるか。
    /// 立てる理由・タイミングは `pending_water_param_decls_resend` と同じ。
    pending_shading_param_decls_resend: bool,
    /// DRAG_HOVER コマンドを受け取ったときに設定する。
    /// 次フレームの ID パスでワールド座標を解決してプレビュー球体位置を更新する。
    /// タプル: (viewport_x, viewport_y)
    pending_drop_hover: Option<(u32, u32)>,
    /// ドラッグ中の配置プレビュー球体を描画するワールド座標。
    /// DragHoverEnd または DROP_ACTOR でクリアされる。
    drop_preview_pos: Option<[f32; 3]>,
    /// 2D シーンビューでドラッグホバー中のルートキャンバスエンティティ。
    /// DRAG_HOVER 受信時にヒット判定で更新し、キャンバス枠線ハイライトに使用する。
    /// DragHoverEnd・DROP_ACTOR・ドロップ確定後にクリアされる。
    drag_hover_canvas_entity: Option<crate::engine::ecs::Entity>,
    /// コンテキストメニューを開いた時点のビューポートスクリーン座標。
    /// ADD_ACTOR / ADD_ACTOR_2D 受信時に pending_add_actor へ移送される。
    context_menu_screen_pos: Option<(u32, u32)>,
    /// コンテキストメニュー経由のアクター追加を次フレームで処理するためのキュー。
    /// pending_drop と同様に IDバッファ座標読み取り後にスポーン位置を確定する。
    /// タプル: (is_2d, world_line, parent_dfs_id, screen_x, screen_y)
    pending_add_actor: Option<(bool, u32, Option<u32>, u32, u32)>,
    /// ADD_CONTROL_POINT_AT_SCREEN コマンドを受け取ったときに設定する。
    /// 次フレームの ID パス後に着弾点のワールド座標を解決し、
    /// アクタ相対へ変換してから制御点を末尾へ追加する。
    /// タプル: (actor_dfs_id, slot_idx, screen_x, screen_y)
    ///
    /// pending_drop と同様に「IPC 受信時点では解決できない」ためのキューである
    /// （ID バッファの読み戻しは GPU サブミット後のフレーム内でしか行えない）。
    /// 1 フレームに 1 回しか読み戻せないので、通常ドロップが読み戻しを使ったフレームでは
    /// 次フレームへ再キューされる。
    pending_control_point_drop: Option<(u32, u32, u32, u32)>,
    /// CONTROL_POINT_DRAG_HOVER コマンドで設定する「配置予定位置の問い合わせ座標」。
    /// タプル: (screen_x, screen_y)。
    ///
    /// ドロップ用キュー（`pending_control_point_drop`）と違い、読み戻しを他用途に
    /// 取られたフレームでは**再キューせず捨てる**。ホバーは 30Hz で次が飛んでくるので
    /// 取りこぼしても実害が無く、再キューすると古い座標のマーカーが残るためである。
    pending_control_point_hover: Option<(u32, u32)>,
    /// ドラッグ中の「配置予定マーカー」を描くワールド座標（解決済み）。
    /// None は「今のカーソル位置には置けない（空をドラッグしている）」を意味し、
    /// マーカーを描かないことでユーザーに伝える。
    /// CONTROL_POINT_DRAG_END で必ず None に戻す。
    control_point_drop_preview: Option<[f32; 3]>,

    // ── プラグインシステム ─────────────────────────────────────────
    /// ロード済みプラグインのレジストリ。
    /// 起動時に handle_resumed で初期化される。
    pub(super) plugin_registry: crate::engine::plugin::registry::PluginRegistry,

    // ── 物理システム ──────────────────────────────────────────────
    /// 固定ステップ物理スレッド。Play モード開始時に起動、停止時に Drop する。
    /// 編集時の物理シミュレーションが有効な場合は Edit モードでも起動する。
    pub(super) physics_thread: Option<crate::engine::physics::PhysicsThread>,

    /// メインスレッド常駐のキャラクター衝突ミラー（`CharacterWorld`）。
    /// 物理スレッドと同一形状のコライダー集合を保持し、キャラクターの押し戻しを
    /// KCC でその場（同一スレッド・同一フレーム）解決する。物理スレッド起動中のみ Some。
    /// （なぜメインスレッド解決なのか＝物理スレッド飢餓とフィードバックループの排除は
    ///   char_world.rs 冒頭の設計解説を参照）
    pub(super) character_world: Option<crate::engine::physics::CharacterWorld>,

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

    /// Play 中のシェーディングアセット（.wgsl）ホットリロード可否フラグ。
    /// true のとき Play 実行中でも `.wgsl` の mtime ポーリング＋再コンパイルを許可する。
    /// エディタ設定 `play_shader_hot_reload`（既定 ON）から `SET_PLAY_SHADER_HOT_RELOAD`
    /// で同期される。ランタイム単体起動（エディタ非接続）でも既定 ON で動く。
    /// Edit モードのホットリロードはこのフラグとは無関係に常に有効。
    pub(super) play_shader_hot_reload: bool,

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

    /// コライダーのみ編集モード（edit_physics_2d_with_rigidbody=false）でのドラッグ中の
    /// 「衝突なしの最終有効位置」。3D 版 `drag_collider_last_valid_pos` と同じ役割で、
    /// 同期オーバーラップ判定（CheckKinematicOverlap2d）でめり込みを検出した際に
    /// この位置へ CanvasTransform を押し戻すことでトンネリング（すり抜け）を防ぐ。
    /// (position [x,y]（メートル）, rotation（ラジアン）) の形式。
    pub(super) drag_collider_last_valid_pos_2d: Option<([f32; 2], f32)>,

    /// 直前フレームで 2D 物理に使用したビューポートサイズ。
    /// ウィンドウリサイズ検出に使用し、変化時に static 物理ボディを再登録する。
    pub(super) last_physics_2d_viewport: Option<[f32; 2]>,

    // ─── タブごと物理状態保持（続きから再開）────────────────────────────────────
    // ビュータブ（3Dシーン/2Dシーン）・アクター編集タブ（world_line）ごとに、
    // 物理タイムライン状態と Dynamic ボディの速度を退避し、タブ復帰時に
    // 位置（ECS で保持）＋速度（下記キャッシュ）で「続き」から再開できるようにする。

    /// タブ（TabKey）→ 退避した物理状態。タブ離脱時に挿入し、復帰時に取り出す。
    pub(super) tab_physics: std::collections::HashMap<tab_physics::TabKey, tab_physics::TabPhysicsState>,

    /// 直近フレームの 3D Dynamic ボディ速度キャッシュ（ECS Entity → (linvel, angvel)）。
    /// update_physics が結果ストリームから毎フレーム更新し、タブ離脱時に退避する。
    pub(super) current_vel_cache_3d: std::collections::HashMap<crate::engine::ecs::Entity, ([f32; 3], [f32; 3])>,

    /// 直近フレームの 2D Dynamic ボディ速度キャッシュ（ECS Entity → (linvel, angvel スカラー)）。
    pub(super) current_vel_cache_2d: std::collections::HashMap<crate::engine::ecs::Entity, ([f32; 2], f32)>,

    /// タブ復帰時に「次の start_physics で初速として積む速度」（一回性）。
    /// Some のときだけ collect_physics_objects が該当 Entity の初速を上書きする。
    /// start 直後に None へ戻すことで、他の多数の start_physics 呼び出しは初速なしを保つ。
    pub(super) pending_restore_vel_3d: Option<std::collections::HashMap<crate::engine::ecs::Entity, ([f32; 3], [f32; 3])>>,

    /// タブ復帰時に「次の start_physics_2d で初速として積む速度」（一回性、2D 版）。
    pub(super) pending_restore_vel_2d: Option<std::collections::HashMap<crate::engine::ecs::Entity, ([f32; 2], f32)>>,

    /// プロジェクト設定（project_settings.json）のウィンドウ解像度キャッシュ (幅, 高さ)。
    /// handle_resumed で一度だけ読み込み、以降は再読込しない。
    /// 用途:
    ///   - カメラ新規追加時の既定アスペクト比（target_width / target_height）
    ///   - ビューポート・ルートキャンバスの自動解像度計算（effective_root_canvas_size）
    pub(super) project_resolution: (u32, u32),

    // ── アニメーション Edit プレビュー ───────────────────────────────
    /// Edit モードのアニメーションプレビュー（ANIM_PREVIEW）用クリップキャッシュ。
    /// キー = .anim アセットパス。ANIM_RELOAD 受信時にエントリを破棄し、
    /// 次回プレビューで再ロードさせる（エディタでの .anim 保存直後に送られる）。
    pub(super) anim_preview_cache: HashMap<String, Arc<crate::engine::animation::AnimationClip>>,

    /// Edit モードのアニメーションプレビュー適用前の元値退避。
    /// キー = プレビュー対象アクターの DFS id。値 = そのアクターのクリップが持つ
    /// 各トラックの (相対アクターパス, 束縛, プレビュー適用前の値)。
    /// ANIM_PREVIEW_STOP 受信時にここから取り出して書き戻し、プレビュー中に
    /// 触れたプロパティを元の状態へ完全復元する。
    pub(super) anim_preview_saved: HashMap<u32, Vec<(String, crate::engine::animation::PropBinding, crate::engine::animation::AnimValue)>>,

    // ── ジョイントアタッチ（ソケット）───────────────────────────────
    /// JointAttachComponent のジョイント名解決失敗を「1 回だけ」警告するための既出集合。
    /// キー = (JointAttach スロットの entity, 解決できなかった joint_name)。毎フレーム走査で
    /// 同じ警告を繰り返さないために使う（jointattach_ops::update_joint_attachments が更新）。
    pub(super) joint_attach_warned: std::collections::HashSet<(crate::engine::ecs::Entity, String)>,

    // ── ボクセル地形（Terrain Editor ランタイム）────────────────────
    /// 地形の実行時状態（設定・全チャンクの密度・チャンク→メッシュスロット対応・
    /// 編集ダーティ集合）。シーン非依存の terrain ライブラリと ECS/GPU を橋渡しする。
    /// 詳細は terrain_ops::TerrainState を参照。
    pub(super) terrain: terrain_ops::TerrainState,

    // ── 埋め込みインプレース Play（フェーズ2）─────────────────────
    /// ENTER_PLAY 開始時に取るアクター状態のスナップショット（メモリ内・serde 不要）。
    /// EXIT_PLAY でここから非地形アクターを再構築して編集状態へ復帰する。
    /// None = 埋め込み Play 中でない（通常の Edit / ウィンドウ Play）。
    pub(super) play_snapshot: Option<PlaySnapshot>,
}

/// プロジェクト設定が読めない場合のウィンドウ解像度既定値（Full HD）。
/// エディタ側 ProjectSettingsData の既定値と一致させること。
pub(super) const DEFAULT_PROJECT_RESOLUTION: (u32, u32) = (1920, 1080);

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
                Ok(host) => {
                    // コンポーネントアクセス用の関数ポインタ表を C# へ登録する
                    // （これ以降 transform.Position などのスクリプトアクセスが有効になる）
                    host.install_host_api();
                    Some(host)
                }
                Err(_)   => None,
            }
        } else {
            None
        };

        // アセットルート配下のユーザースクリプト (.cs) を CLR 側でコンパイルする。
        // シーンロード時の ScriptComponent 生成（型解決）より前に行う必要がある。
        // コンパイルエラーは C# 側が stderr に出力し、エディタの Output パネルに表示される。
        if let (Some(host), Some(root)) = (&scripting_host, &args.assets_root) {
            let count = host.compile_scripts(root);
            if count >= 0 {
                eprintln!("[SEED] user scripts compiled: {count} type(s)");
            } else {
                eprintln!("[SEED] user script compilation failed (see errors above)");
            }
        }

        Self {
            window:         None,
            renderer:       None,
            input:          Input::new(),
            audio:          None,
            scene_registry: HashMap::new(),
            preloaded_scene: None,
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
            paused:        false,
            render_paused: false,
            window_focused: true,
            // フレーム途絶判定・IPC ポンプの時間ゲートの起点（初回は「今」から数える）。
            last_frame_at:    std::time::Instant::now(),
            last_ipc_pump_at: std::time::Instant::now(),
            assets_root:      args.assets_root,
            editor_resources: args.editor_resources,
            scene_path:       args.scene_path,
            cam_grab_screen_pos: None,
            mmb_grab_screen_pos: None,
            play_clamp: false,
            id_buffer:          None,
            selected_instances: Vec::new(),
            pending_pick:       None,
            pick_2d_cycle:      None,
            last_cursor_pos:    None,
            line_model_buf:     None,
            tool_mode:          ToolMode::Select,
            gizmo_space:        GizmoSpace::World,
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
            render_features:    crate::engine::core::renderer::RenderFeatures::default(),
            features_log_state: None,
            show_axis_gizmo: true,
            axis_gizmo_hovered: None,
            camera_snap_anim:   None,
            actor_virtual_selected_idx:      None,
            selected_actor_dfs_ids:          Vec::new(),
            actor_virtual_selected_slot_idx: 0,
            inspector_transform_drag:     None,
            field_edit_session:           None,
            scatter_prefab_cache:         std::collections::HashMap::new(),
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
            // Play 中シェーダホットリロードは既定 ON（エディタ設定の既定値と一致させる）。
            // エディタ接続時は SyncViewportSettings から SET_PLAY_SHADER_HOT_RELOAD が届いて
            // ユーザー設定で上書きされる。
            play_shader_hot_reload:       true,
            active_collision_dfs_ids:     std::collections::HashSet::new(),
            dragging_physics_entity_id:   None,
            drag_collider_last_valid_pos: None,
            active_world_line: 0,
            saved_cameras: HashMap::new(),
            canvas_world_lines:    HashSet::new(),
            actor_edit_canvas_wls: HashSet::new(),
            canvas_cameras:        HashMap::new(),
            canvas_edit_sessions:  HashMap::new(),
            canvas_screen_space_overlay: false,
            edit_view_mode:              EditViewMode::View3D,
            rt_pool:                     crate::engine::core::renderer::RtPool::new(),
            refract_pyramid:             crate::engine::core::renderer::RefractPyramid::new(),
            // 水面レンダラは device 確立後（初めて水ボリュームを描くフレーム）に遅延構築する。
            water_renderer:              None,
            // ショアフィールドは CPU データのみ。空のキャッシュで開始する（Phase W1.5）。
            water_shore_fields:          crate::engine::water::ShoreFieldSet::new(),
            // 水位グラフ（Phase W2.5）。Play を開始するまで何もしない。
            water_level_sim:             crate::engine::water::WaterLevelSim::default(),
            terrain_edit_version:        0,
            // インタラクションフィールドは device 確立後（草かソースが現れるフレーム）に遅延構築する。
            interaction_field:           None,
            interaction_velocity:        crate::engine::interaction::InteractionSourceVelocityTracker::new(),
            // コントロールポイントは未選択で開始する（ギズモは通常どおりアクタを対象にする）。
            selected_control_point:      None,
            ao_targets:                  crate::engine::core::renderer::AoTargets::new(),
            ssgi_targets:                crate::engine::core::renderer::SsgiTargets::new(),
            caustics_targets:            crate::engine::core::renderer::CausticsTargets::new(),
            water_reflection_blur_targets:
                crate::engine::core::renderer::WaterReflectionBlurTargets::new(),
            shadow_mask_targets:         crate::engine::core::renderer::ShadowMaskTargets::new(),
            ssgi_warmed:                 false,
            particle_system:             crate::engine::core::renderer::ParticleSystem::new(),
            skybox_system:               crate::engine::core::renderer::SkyboxSystem::new(),
            hiz:                         None,
            hiz_prev_keys:               Vec::new(),
            hiz_need_map:                false,
            post_vignette_enabled:       false,
            post_fx:                     crate::engine::core::renderer::PostFxSettings::default(),
            scene_view_mode:             crate::engine::core::renderer::SceneViewMode::default(),
            ambient_color:               crate::engine::core::renderer::DEFAULT_AMBIENT_COLOR,
            ambient_intensity:           crate::engine::core::renderer::DEFAULT_AMBIENT_INTENSITY,
            camera_preview:              None,
            camera_preview_target_size:  None,
            camera_gizmo:                None,
            light_gizmo:                 None,
            particle_gizmo:              None,
            shared_model_batches:    HashMap::new(),
            last_camera_pos:         [0.0; 3],
            // 速度バッファ: 起動直後は前フレームが無い ⇒ 初回は prev=curr（速度 0）。
            velocity_prev_view_proj:  None,
            velocity_prev_key:        None,
            velocity_reset_requested: false,
            batch_absent_frames:     HashMap::new(),
            pending_drop:            None,
            pending_water_param_decls_resend: false,
            pending_shading_param_decls_resend: false,
            pending_drop_hover: None,
            drop_preview_pos:   None,
            drag_hover_canvas_entity: None,
            context_menu_screen_pos: None,
            pending_add_actor:  None,
            pending_control_point_drop: None,
            pending_control_point_hover: None,
            control_point_drop_preview: None,
            plugin_registry:  crate::engine::plugin::registry::PluginRegistry::empty(),
            physics_thread:   None,
            character_world:  None,
            physics_thread_2d:           None,
            canvas_3d_physics:           std::collections::HashMap::new(),
            edit_physics_2d_enabled:     false,
            edit_physics_2d_with_rigidbody: false,
            active_collision_2d_dfs_ids: std::collections::HashSet::new(),
            dragging_physics_2d_entity_id: None,
            drag_collider_last_valid_pos_2d: None,
            last_physics_2d_viewport: None,
            // タブごと物理状態保持（続きから再開）
            tab_physics:            std::collections::HashMap::new(),
            current_vel_cache_3d:   std::collections::HashMap::new(),
            current_vel_cache_2d:   std::collections::HashMap::new(),
            pending_restore_vel_3d: None,
            pending_restore_vel_2d: None,
            // handle_resumed で project_settings.json から上書きされる
            project_resolution: DEFAULT_PROJECT_RESOLUTION,
            anim_preview_cache: HashMap::new(),
            anim_preview_saved: HashMap::new(),
            joint_attach_warned: std::collections::HashSet::new(),
            terrain:             terrain_ops::TerrainState::default(),
            play_snapshot:       None,
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

    // ── Edit ビューモード判定ヘルパー ─────────────────────────────

    /// 現在のビューポートが「2D シーンビュー（EditViewMode::View2D）」かどうかを返す。
    ///
    /// 条件:
    ///   - Edit モードである（Play・Play ポーズ中は常に false = 従来動作）
    ///   - シーン世界線（world_line = 0）を表示中である
    ///     （アクター編集タブ・キャンバス編集タブには影響しない）
    ///
    /// true のとき: 3D シーン描画を抑制し、スクリーンスペースキャンバスを
    /// WYSIWYG レイアウトで表示、カメラは 2D パン・ズームに切り替わる。
    /// 次フレームの速度バッファ（モーションベクタ）をリセットする（＝速度を全面 0 にする）。
    ///
    /// ## いつ呼ぶか
    /// 「前フレームの画面と今フレームの画面が連続していない」操作の直後:
    /// - シーンのロード／差し替え（`self.scene = Some(..)` を伴う遷移）
    /// - カメラのテレポート（位置・向きの瞬間移動）
    ///
    /// Play⇄Edit 切替・ポーズ切替・レンダーターゲットのリサイズ・ワールドライン切替は
    /// `velocity_prev_key` の変化として **自動検出** されるため、呼ぶ必要はない。
    ///
    /// ## 呼ばないとどうなるか
    /// カメラや物体が 1 フレームで画面の端から端まで飛んだことになり、速度バッファに
    /// 巨大な値が入る。消費者（将来の TAA・モーションブラー）がそれを信じると、
    /// 画面全体が流れる／履歴が総崩れになる。生成側で潰すのが正しい層である。
    pub(super) fn request_velocity_reset(&mut self) {
        self.velocity_reset_requested = true;
    }

    pub(super) fn edit_view_is_2d(&self) -> bool {
        self.mode == RuntimeMode::Edit
            && self.edit_view_mode == EditViewMode::View2D
            && self.active_world_line == 0
    }

    /// 現在アクティブなタブが「2D 系のアクター編集タブ／キャンバス編集タブ」か
    /// どうかを返す（wl>0 かつ actor_edit_canvas_wls に登録済み）。
    ///
    /// これらのタブは常にスクリーンスペース 2D レイアウトで描画され（frame_renderer の
    /// use_screen_space 参照）、ルートアクターが 1 体だけという単一ルート構造を持つ。
    /// ビューポート（wl==0 の Edit View2D = edit_view_is_2d）とは別条件だが、
    /// D&D ドロップ配置ではどちらも 2D 経路（handle_drop_actor_2d）へ流す。
    pub(super) fn is_2d_edit_tab(&self) -> bool {
        self.active_world_line != 0
            && self.actor_edit_canvas_wls.contains(&self.active_world_line)
    }

    /// 現在のビューポートが「3D シーンビューでスクリーンスペースキャンバスを
    /// 非表示にする状態」かどうかを返す。
    ///
    /// 条件は edit_view_is_2d と対になる（Edit モード + シーン世界線 + View3D）。
    /// true のとき: Actor2D（スクリーンスペースキャンバス）の描画・ピッキング・
    /// ギズモをすべて無効化する。3D ワールドキャンバスは影響を受けない。
    pub(super) fn edit_view_hides_ss_canvas(&self) -> bool {
        self.mode == RuntimeMode::Edit
            && self.edit_view_mode == EditViewMode::View3D
            && self.active_world_line == 0
    }

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
mod script_ops;
/// シェーディングアセット WGSL のインメモリ検証（エディタの未保存バッファ → 診断）。
mod shading_validate_ops;
/// L3 シェーディングアセットのパラメータ編集（カメラ添付・シーン添付）。
mod shading_param_ops;

// actor_utils / platform_utils の関数を親名前空間に再エクスポートする。
// サブモジュール（render.rs 等）は既存の `use super::fn_name` のまま使用可能。
use actor_utils::{
    collect_actor_nodes, build_hierarchy_json,
    find_actor_by_dfs,
    canvas_anchor_offset_for_dfs, collect_canvas_actors_in_rect,
    collect_transform_only_in_rect,
    collect_mcs_in_world_line,
    remove_actor_by_dfs, actor_subtree_size, find_parent_canvas_info,
    find_actor_root_info,
    collect_entities_for_wl, despawn_actor_recursive,
    insert_actors_after_dfs, extract_actor_by_dfs, extract_actor_by_entity,
    selection_centroid, world_to_screen,
    collect_child_actor_drag_starts, collect_child_actor_old_states,
    apply_delta_to_actor_children, apply_delta_to_actor_subtree,
    find_parent_actor_of_dfs, get_3d_canvas_world_mat,
    extract_actor_by_dfs_with_origin, find_actor_by_entity_mut,
};
// undo.rs（app_base 直下の ActorActiveCommand）からも DFS 探索を共有するため再エクスポートする。
pub(crate) use actor_utils::find_actor_by_dfs_mut;
use platform_utils::{
    camera_grab_start, camera_grab_end, apply_window_clamp, release_window_clamp,
    warp_cursor_to_local, mmb_grab_start, mmb_grab_end,
};

