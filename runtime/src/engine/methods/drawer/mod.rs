mod uniforms;
pub(crate) mod gpu_resources;
mod pipeline;
mod model_drawer;
mod primitive_drawer;
mod hiz;

pub use uniforms::{CameraUniform, ModelUniform, MaterialUniform, JointUniform, ColorVertex,
                   GpuCullData, FrustumUniform};
pub use gpu_resources::{GpuTexture, GpuMaterial, GpuPrimitive, GpuMesh, GpuModel,
                        InstancedModelBatch, NodePrimDraw, GpuLineBatch, DefaultTextures,
                        CameraBuffer, extract_frustum_planes, test_aabb_frustum};
pub use pipeline::{MeshPipeline, SkinnedMeshPipeline, UnlitPipeline, CullPipeline, DrawPipelines};
pub use model_drawer::draw_model_indirect;
pub use primitive_drawer::{LineBatch, draw_line_batch};

use std::sync::Arc;
use crate::engine::core::loader::model::Model;

// ============================================================
//  DrawContext — 描画に必要なリソースを一括管理する高レベル型
// ============================================================

/// GPU 描画コンテキスト。
///
/// `Renderer` から生成し、モデルのアップロードや描画関数呼び出しに使う。
///
/// # 使用例
/// ```rust
/// let ctx = DrawContext::new(renderer.device(), renderer.queue(),
///                            renderer.surface_format(), renderer.depth_format());
/// let gpu_model   = ctx.upload_model(&model);
/// let transforms  = ctx.create_model_transforms(&model);
/// let camera_buf  = ctx.create_camera_buffer();
/// ```
pub struct DrawContext {
    pub device:    Arc<wgpu::Device>,
    pub queue:     Arc<wgpu::Queue>,
    pub pipelines: DrawPipelines,
    pub defaults:  DefaultTextures,
}

impl DrawContext {
    /// 描画コンテキストを初期化する。
    pub fn new(
        device:         Arc<wgpu::Device>,
        queue:          Arc<wgpu::Queue>,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
    ) -> Self {
        let pipelines = DrawPipelines::new(&device, surface_format, depth_format);
        let defaults  = DefaultTextures::new(&device, &queue);
        Self { device, queue, pipelines, defaults }
    }

    /// モデルを GPU にアップロードして `GpuModel` を返す。
    pub fn upload_model(&self, model: &Model) -> GpuModel {
        GpuModel::upload(
            &self.device,
            &self.queue,
            model,
            &self.pipelines.mesh.material_bgl,
            &self.pipelines.skinned_mesh.joint_bgl,
            &self.defaults,
        )
    }

    /// N インスタンス分のモデル変換ストレージバッファと GPU カリング用バッファを生成する。
    pub fn create_instanced_batch(&self, model: &Model, num_instances: u32) -> InstancedModelBatch {
        InstancedModelBatch::new(
            &self.device,
            model,
            &self.pipelines.mesh.model_bgl,
            &self.pipelines.cull.bgl,
            num_instances,
        )
    }

    /// カメラユニフォームバッファを生成する。
    pub fn create_camera_buffer(&self) -> CameraBuffer {
        CameraBuffer::new(&self.device, &self.pipelines.mesh.camera_bgl)
    }

    /// アンライトパイプライン用の単位行列モデルバッファを生成する。
    ///
    /// グリッド等のワールド空間ラインバッチ描画に使用する。
    /// 返値の `wgpu::Buffer` はバインドグループが参照するため、
    /// ドロップせずに呼び出し元で保持すること。
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
