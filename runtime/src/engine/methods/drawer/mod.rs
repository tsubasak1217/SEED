// methods/drawer/mod.rs — アプリケーション向け描画 API
//
// GPU リソース管理・パイプライン定義は core::renderer に集約されている。
// ここでは draw_* 関数群と DrawContext（高レベル API）のみを公開する。

mod model_drawer;
mod id_pass;
mod outline;
mod primitive_drawer;
mod sprite_drawer;

// drawing files が use super::gpu_resources::... 等で参照できるようモジュール別名を作成
pub(crate) use crate::engine::core::renderer::gpu_resources;
pub(crate) use crate::engine::core::renderer::pipeline;
pub(crate) use crate::engine::core::renderer::uniforms;

// core::renderer の公開型をそのまま再エクスポート
pub use crate::engine::core::renderer::{
    // ユニフォーム型
    CameraUniform, ModelUniform, MaterialUniform, JointUniform, ColorVertex, GizmoVertex,
    GpuCullData, FrustumUniform,
    // GPU リソース型
    GpuTexture, GpuMaterial, GpuPrimitive, GpuMesh, GpuModel,
    InstancedModelBatch, NodePrimDraw, GpuLineBatch, DefaultTextures,
    GpuGizmoBatch, CameraBuffer, extract_frustum_planes, test_aabb_frustum, NUM_LODS,
    // パイプライン型
    MeshPipeline, SkinnedMeshPipeline, UnlitPipeline, CullPipeline, DrawPipelines,
    SkinComputePipeline, IdPassPipeline, OutlinePipeline, DepthPrepassPipelines,
    SpritePipeline, SpriteOutlinePipeline, CanvasIdPipeline, CanvasIdUniform,
    CameraPreviewBlitPipeline,
    // ライト
    GpuLight, LightBuffer, MAX_LIGHTS,
    // シャドウ（Phase R2）
    ShadowResources, ShadowPlan, ShadowDepthPipelines,
    // インラインレイトレ影（Phase R8）
    RtShadowResources,
    // HDR ポストプロセス土台（Phase R3）
    PostContext,
};

// 描画関数
pub use model_drawer::draw_model_indirect;
pub use id_pass::{IdBuffer, draw_id_pass, draw_canvas_id_items, draw_collider_pick_items, prepare_canvas_id_bg};
pub use outline::{draw_outline, draw_stencil_mask, draw_outline_multi, draw_stencil_mask_multi};
pub use primitive_drawer::{LineBatch, GizmoBatch, draw_line_batch, draw_gizmo_batch, draw_thick_line_batch};
pub use sprite_drawer::{
    GpuSpriteTexture, SpriteUniform, SpriteVertex,
    load_sprite_texture, prepare_sprites, prepare_sprites_from_mats,
    draw_sprites, draw_sprite_outline, SpritePrepared,
};

// ============================================================
//  DrawContext — アプリケーション向け高レベル API
// ============================================================

use std::sync::Arc;
use std::collections::HashMap;
use std::cell::RefCell;
use crate::engine::core::loader::model::Model;
use crate::engine::core::renderer::gpu_resources::{
    GpuModel as GpuModelInner, InstancedModelBatch as BatchInner,
    DefaultTextures as DefaultTex, CameraBuffer as CamBuf,
};
/// GPU 描画コンテキスト。
///
/// `Renderer` から生成し、モデルのアップロードや描画関数呼び出しに使う。
pub struct DrawContext {
    pub device:           Arc<wgpu::Device>,
    pub queue:            Arc<wgpu::Queue>,
    pub pipelines:        DrawPipelines,
    pub defaults:         DefaultTex,
    /// ライト用 GPU バッファ一式（毎フレーム `update()` で有効ライトを書き込む）。
    /// group 4 の bind group を全メッシュ描画で共用する。
    pub light_buffer:     LightBuffer,
    /// シャドウ用 GPU リソース一式（Phase R2）。
    /// 毎フレーム prepare_frame でシャドウ行列を更新し、record で深度パスを記録する。
    /// サンプリング用資源は light_buffer.bind_group（group 4 複合 BG の binding 2〜5）
    /// 経由で全メッシュ描画に共用される。
    pub shadow:           ShadowResources,
    /// RT 影用リソース一式（Phase R8）。RT 対応 GPU でのみ Some。
    /// 毎フレーム（RT 影オン時）prepare_and_build で BLAS/TLAS を更新し、
    /// bind_group（group 4 に TLAS を加えた複合 BG）を RT パイプライン描画で使う。
    /// 非対応時は None で、従来のシャドウマップ経路が完全に無変更で動作する。
    ///
    /// DrawContext は `&self` で共有参照されるため、フレーム内で BLAS/TLAS を再構築する
    /// （`&mut` が要る）には内部可変性が必要。model_cache と同じく RefCell で包む。
    pub rt_shadow:        Option<RefCell<RtShadowResources>>,
    /// HDR ポストプロセスの静的リソース一式（Phase R3）。
    /// トーンマップ／ビネットのパイプライン・共有サンプラー・既定マスクを保持する。
    /// 動的なレンダーターゲット（HDR オフスクリーン等）は App 側の RtPool が持つ。
    pub post:             PostContext,
    /// パス → 解析済み CPU モデルのキャッシュ。
    /// 同じパスのモデルを繰り返し build_actor/rebuild するときにディスク読み込みとパースを省く。
    pub model_cache:      RefCell<HashMap<String, Arc<Model>>>,
    /// パス → GPU スプライトテクスチャキャッシュ。
    /// Some(arc) = ロード成功、None = ロード失敗済み（毎フレームのリトライ・ログ爆発を防ぐ）。
    pub sprite_tex_cache: RefCell<HashMap<String, Option<Arc<GpuSpriteTexture>>>>,
}

impl DrawContext {
    pub fn new(
        device:         Arc<wgpu::Device>,
        queue:          Arc<wgpu::Queue>,
        scene_format:   wgpu::TextureFormat,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
        cache:          Option<&wgpu::PipelineCache>,
    ) -> Self {
        // シーン描画パイプラインは HDR（scene_format）でビルドし、トーンマップ後に
        // スワップチェーンへ直接描くパスのみ surface_format を使う（Phase R3）。
        let pipelines = DrawPipelines::new(&device, &queue, scene_format, surface_format, depth_format, cache);
        // HDR ポストプロセスの静的リソース（トーンマップ／ビネットのパイプライン等）。
        let post      = PostContext::new(&device, &queue, scene_format, surface_format, cache);
        let defaults  = DefaultTex::new(&device, &queue);
        // シャドウリソース（深度配列・比較サンプラー・シャドウ行列 UBO）を先に生成し、
        // ライトバッファが group 4 の複合 BindGroup（ライト binding 0/1 ＋
        // シャドウ binding 2〜5）を構築する際に参照させる。
        // max_bind_groups=5（group 0〜4）のデバイスがあるため group 5 は使わない。
        // レイアウトは mesh パイプライン由来（skinned とレイアウト互換のため共用）。
        let shadow       = ShadowResources::new(&device, &pipelines.mesh.camera_bgl);
        let light_buffer = LightBuffer::new(&device, &pipelines.mesh.lights_bgl, &shadow);
        // RT 影リソースは RT パイプラインが生成できた場合（＝ RT 対応 GPU）のみ生成する。
        // group 4 に TLAS を加えた複合 BindGroup を、RT パイプラインの group 4 レイアウトで作る。
        let rt_shadow = pipelines.rt.as_ref().map(|rtp| {
            RefCell::new(RtShadowResources::new(&device, &rtp.lights_bgl, &shadow, &light_buffer))
        });
        Self {
            device,
            queue,
            pipelines,
            defaults,
            light_buffer,
            shadow,
            rt_shadow,
            post,
            model_cache:      RefCell::new(HashMap::new()),
            sprite_tex_cache: RefCell::new(HashMap::new()),
        }
    }

    /// このフレームで RT 影を実際に使うか（RT 対応 かつ 設定オン）。
    /// フラグメントの実行時分岐（LightMeta.rt_shadows）とパイプライン選択の両方に使う。
    pub fn rt_active(&self, rt_setting: bool) -> bool {
        self.rt_shadow.is_some() && rt_setting
    }

    pub fn upload_model(&self, model: &Model) -> GpuModelInner {
        GpuModelInner::upload(
            &self.device,
            &self.queue,
            model,
            &self.pipelines.mesh.material_bgl,
            &self.pipelines.skinned_mesh.joint_bgl,
            &self.defaults,
        )
    }

    pub fn create_instanced_batch(&self, model: &Model, num_instances: u32) -> BatchInner {
        BatchInner::new(
            &self.device,
            model,
            &self.pipelines.mesh.model_bgl,
            &self.pipelines.skin_compute,
            &self.pipelines.skinned_mesh.joint_bgl,
            &self.pipelines.id_pass.id_data_bgl,
            num_instances,
        )
    }

    pub fn create_camera_buffer(&self) -> CamBuf {
        CamBuf::new(&self.device, &self.pipelines.mesh.camera_bgl)
    }

    /// ID パス用のベースオフセット bind group を生成する。
    /// 複数モデルを ID パスで描画する際、モデルごとに異なる base 値を渡す。
    pub fn create_id_base_bg(&self, base: u32) -> (wgpu::Buffer, wgpu::BindGroup) {
        use wgpu::util::DeviceExt;
        let buf = self.device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("ID Base Buffer"),
            contents: bytemuck::bytes_of(&base),
            usage:    wgpu::BufferUsages::UNIFORM,
        });
        let bg = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("ID Base BG"),
            layout:  &self.pipelines.id_pass.id_base_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: buf.as_entire_binding(),
            }],
        });
        (buf, bg)
    }

    pub fn create_identity_model_bg_for_unlit(&self) -> (wgpu::Buffer, wgpu::BindGroup) {
        use wgpu::util::DeviceExt;
        let uniform = uniforms::ModelUniform::identity();
        let buf = self.device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Identity Model Buffer"),
            contents: bytemuck::bytes_of(&uniform),
            usage:    wgpu::BufferUsages::UNIFORM,
        });
        let bg = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("Identity Model BG"),
            layout: &self.pipelines.unlit_line.model_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: buf.as_entire_binding(),
            }],
        });
        (buf, bg)
    }
}
