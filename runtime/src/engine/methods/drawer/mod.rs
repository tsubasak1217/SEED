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
    GpuTexture, GpuMaterial, GpuPrimitive, GpuMesh, GpuModel, InlineUpdateResult,
    InstancedModelBatch, NodePrimDraw, GpuLineBatch, DefaultTextures,
    GpuGizmoBatch, CameraBuffer, extract_frustum_planes, test_aabb_frustum, NUM_LODS,
    // パイプライン型
    MeshPipeline, SkinnedMeshPipeline, UnlitPipeline, CullPipeline, DrawPipelines,
    SkinComputePipeline, IdPassPipeline, OutlinePipeline, DepthPrepassPipelines,
    SpritePipeline, SpriteOutlinePipeline, CanvasIdPipeline, CanvasIdUniform,
    CameraPreviewBlitPipeline,
    // ライト（LightingPass = group 4 の BG を「どのカメラのパスか」で選ぶ型）
    GpuLight, LightBuffer, LightingPass, MAX_LIGHTS,
    // Clustered Lighting（Phase C1）
    ClusterResources, partition_directional_first,
    // シャドウ（Phase R2）
    ShadowResources, ShadowPlan, ShadowDepthPipelines,
    // インラインレイトレ影（Phase R8）
    RtShadowResources,
    // HDR ポストプロセス土台（Phase R3）
    PostContext,
    // テクスチャ単位ポストプロセス（.postfx）
    PostfxContext, SpritePostfxCache,
};
// DDGI（Phase RT-GI）
use crate::engine::core::renderer::ddgi::GiResources;

// 描画関数
pub use model_drawer::draw_model_indirect;
pub use id_pass::{IdBuffer, draw_id_pass, draw_canvas_id_items, draw_collider_pick_items, prepare_canvas_id_bg};
pub use outline::{draw_outline, draw_stencil_mask, draw_outline_multi, draw_stencil_mask_multi};
pub use primitive_drawer::{LineBatch, GizmoBatch, draw_line_batch, draw_gizmo_batch, draw_thick_line_batch};
pub use sprite_drawer::{
    GpuSpriteTexture, SpriteVertex, load_sprite_texture,
};
// Phase R6: スプライトの汎用インスタンシングバッチャ（旧 prepare_sprites/draw_sprites を置換）
pub use crate::engine::core::renderer::batch2d::{
    SpriteBatcher, SpriteInstance, SpriteBatch, SpriteBatchList,
    SPRITE_INSTANCE_SIZE, draw_sprite_batches, draw_sprite_outline_batches,
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
    /// 「どのカメラのパスか」で選ぶ 2 本（`bind_group(LightingPass::…)`）を持つ。
    pub light_buffer:     LightBuffer,
    /// Clustered Lighting のクラスタ資源一式（Phase C1）。
    /// 毎フレーム `update()` でメインカメラのパラメータを書き、compute で
    /// クラスタごとの影響ライトリストを構築する。group 4 の binding 7〜9 を供給する。
    ///
    /// クラスタは**カメラごとに固有**なので、カメラプレビューのパスでは
    /// `light_buffer.bind_group(LightingPass::CameraPreview)`（enabled=0）を bind して
    /// 従来の全ライト線形走査へフォールバックさせること。
    pub clusters:         ClusterResources,
    /// **メインカメラ用**シャドウ GPU リソース一式（Phase R2）。
    /// 毎フレーム prepare_frame でシャドウ行列を更新し、record で深度パスを記録する。
    /// サンプリング用資源は `light_buffer.bind_group(LightingPass::MainCamera)`
    /// （group 4 複合 BG の binding 2〜5）経由でメインパスの全メッシュ描画に共用される。
    pub shadow:           ShadowResources,
    /// **カメラプレビュー用**シャドウ GPU リソース一式。
    ///
    /// CSM のカスケードは**カメラ視錐台にフィットさせる＝カメラ固有**である。
    /// エディタのカメラプレビューはゲームカメラの映像なので、メインカメラ
    /// （Edit ではデバッグカメラ）基準の CSM を使うと、プレビューの影が
    /// デバッグカメラの向きで変わってしまう（＝プレビューがゲームカメラの映像でなくなる）。
    /// そのためプレビュー専用の実体を持ち、プレビューカメラ基準でカスケードを組み、
    /// プレビューパスの直前に深度を描く（frame_renderer.rs）。
    /// サンプリングは `light_buffer.bind_group(LightingPass::CameraPreview)` 経由。
    pub shadow_preview:   ShadowResources,
    /// RT 影用リソース一式（Phase R8）。RT 対応 GPU でのみ Some。
    /// 毎フレーム（RT 影オン時）prepare_and_build で BLAS/TLAS を更新し、
    /// bind_group（group 4 に TLAS を加えた複合 BG）を RT パイプライン描画で使う。
    /// 非対応時は None で、従来のシャドウマップ経路が完全に無変更で動作する。
    ///
    /// DrawContext は `&self` で共有参照されるため、フレーム内で BLAS/TLAS を再構築する
    /// （`&mut` が要る）には内部可変性が必要。model_cache と同じく RefCell で包む。
    pub rt_shadow:        Option<RefCell<RtShadowResources>>,
    /// バインドレス基盤（フェーズ B1）。対応 GPU でのみ Some。
    /// テクスチャ配列レジストリ・UV/index メガバッファ・インスタンステーブルを保持する。
    /// B1 では確保のみ（消費側 B2/B3 は後続）。登録／解放でダーティ化する BindGroup キャッシュを
    /// 内包するため `&mut` が要り、DrawContext は `&self` 共有のため RefCell で包む。
    /// B1 時点では確保のみで読み出し側が無いため未使用警告が出るが、B2 が消費する（意図的保持）。
    #[allow(dead_code)]
    pub bindless:         Option<RefCell<crate::engine::core::renderer::BindlessResources>>,
    /// DDGI リソース一式（Phase RT-GI）。アトラス・GiParams・更新 compute BindGroup を持つ。
    /// RT 非対応 GPU でもリソースは生成する（compute は attach されず GI は無効）。
    pub gi:               GiResources,
    /// HDR ポストプロセスの静的リソース一式（Phase R3）。
    /// トーンマップ／ビネットのパイプライン・共有サンプラー・既定マスクを保持する。
    /// 動的なレンダーターゲット（HDR オフスクリーン等）は App 側の RtPool が持つ。
    pub post:             PostContext,
    /// テクスチャ単位ポストプロセス（.postfx）の静的リソース一式。
    /// スプライトのポストエフェクト焼き込みで使うパイプライン・サンプラー・既定マスク。
    pub postfx:           PostfxContext,
    /// スプライトのポストエフェクト焼き込みキャッシュ（(texture_path, postfx_path) → 焼き込みテクスチャ）。
    /// `&self` 共有下でフレーム内から焼き込み・参照するため内部可変（RefCell）を内包する。
    pub sprite_postfx_cache: SpritePostfxCache,
    /// パス → 解析済み CPU モデルのキャッシュ。
    /// 同じパスのモデルを繰り返し build_actor/rebuild するときにディスク読み込みとパースを省く。
    pub model_cache:      RefCell<HashMap<String, Arc<Model>>>,
    /// パス → GPU スプライトテクスチャキャッシュ。
    /// Some(arc) = ロード成功、None = ロード失敗済み（毎フレームのリトライ・ログ爆発を防ぐ）。
    pub sprite_tex_cache: RefCell<HashMap<String, Option<Arc<GpuSpriteTexture>>>>,
    /// スプライト汎用インスタンシングバッチャ（Phase R6）。
    /// 永続インスタンスバッファを毎フレーム write_buffer し、同一テクスチャの連続
    /// スプライトを 1 ドローコールへ束ねる。DrawContext は `&self` 共有のため
    /// フレーム内で scratch/バッファを可変にできるよう RefCell で包む。
    pub sprites:          RefCell<SpriteBatcher>,
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
        // テクスチャ単位ポスト（.postfx）の静的リソース（作業フォーマットは常に Rgba16Float）。
        let postfx    = PostfxContext::new(&device, &queue, cache);
        let defaults  = DefaultTex::new(&device, &queue);
        // シャドウリソース（深度配列・比較サンプラー・シャドウ行列 UBO）を先に生成し、
        // ライトバッファが group 4 の複合 BindGroup（ライト binding 0/1 ＋
        // シャドウ binding 2〜5）を構築する際に参照させる。
        // max_bind_groups=5（group 0〜4）のデバイスがあるため group 5 は使わない。
        // レイアウトは mesh パイプライン由来（skinned とレイアウト互換のため共用）。
        let shadow       = ShadowResources::new(&device, &pipelines.mesh.camera_bgl);
        // カメラプレビュー用のシャドウ実体（同一構成・別テクスチャ）。
        // CSM はカメラ視錐台にフィットさせる＝カメラ固有のため、プレビュー
        // （ゲームカメラの映像）はメインカメラの CSM を流用できない。
        let shadow_preview = ShadowResources::new(&device, &pipelines.mesh.camera_bgl);
        // クラスタ資源（Phase C1）: group 4 の binding 7〜9 を供給する。
        // 生成順は「クラスタバッファ → LightBuffer（クラスタを参照する複合 BG を作る）
        // → クラスタの compute BindGroup（ライトバッファを参照する）」の 2 段構え。
        // 相互参照になるため attach_lights を後から呼ぶ。
        let mut clusters = ClusterResources::new(&device);
        // DDGI リソース（Phase RT-GI）。group 4 binding 10〜13 を供給するため LightBuffer より先に作る。
        // 次元は固定・原点/間隔はシーンフィット（frame_renderer が gi.fit を呼ぶ）。
        let mut gi = GiResources::new(
            &device, crate::engine::core::renderer::rt_shadow::rt_shadows_supported(),
        );
        let light_buffer = LightBuffer::new(
            &device, &pipelines.mesh.lights_bgl, &shadow, &shadow_preview, &clusters, &gi,
        );
        clusters.attach_lights(
            &device, &pipelines.cluster_build.bgl, light_buffer.lights_buffer(),
        );
        // RT 影リソースは RT パイプラインが生成できた場合（＝ RT 対応 GPU）のみ生成する。
        // group 4 に TLAS を加えた複合 BindGroup を、RT パイプラインの group 4 レイアウトで作る。
        let rt_shadow = pipelines.rt.as_ref().map(|rtp| {
            RefCell::new(RtShadowResources::new(
                &device, &rtp.lights_bgl, &shadow, &light_buffer, &clusters, &gi,
            ))
        });
        // GI 更新 compute の BindGroup を接続する（RT 対応 GPU のみ。TLAS/平均アルベドを共有）。
        if let (Some(rt_cell), Some(gip)) = (rt_shadow.as_ref(), pipelines.gi_update.as_ref()) {
            let rt = rt_cell.borrow();
            gi.attach(
                &device, &gip.bgl,
                light_buffer.lights_buffer(),
                light_buffer.meta_main_buffer(),
                rt.tlas(),
                rt.albedo_buffer(),
            );
        }
        // バインドレス基盤（フェーズ B1）。対応 GPU でのみ確保する。
        // 確定容量はデバイス初期化時に global へ設定済み（mod.rs）。非対応 GPU では None で、
        // 一切の GPU 資源を確保しない（＝従来経路と完全に同一）。
        let bindless = if crate::engine::core::renderer::bindless_supported() {
            let cap = crate::engine::core::renderer::bindless::bindless_capacity();
            Some(RefCell::new(
                crate::engine::core::renderer::BindlessResources::new(&device, &queue, cap),
            ))
        } else {
            None
        };
        // スプライトバッチャ（Phase R6）: 永続インスタンスバッファを初期容量で確保する。
        let sprites = RefCell::new(SpriteBatcher::new(&device));
        Self {
            device,
            queue,
            pipelines,
            defaults,
            light_buffer,
            clusters,
            shadow,
            shadow_preview,
            rt_shadow,
            bindless,
            gi,
            post,
            postfx,
            sprite_postfx_cache: SpritePostfxCache::new(),
            model_cache:      RefCell::new(HashMap::new()),
            sprite_tex_cache: RefCell::new(HashMap::new()),
            sprites,
        }
    }

    /// このフレームで RT 影を実際に使うか（RT 対応 かつ 設定オン）。
    /// フラグメントの実行時分岐（LightMeta.rt_shadows）とパイプライン選択の両方に使う。
    pub fn rt_active(&self, rt_setting: bool) -> bool {
        self.rt_shadow.is_some() && rt_setting
    }

    /// GPU モデルを構築する（バインドレス登録なし）。`upload_model` /
    /// `upload_model_with_overrides` が実効マテリアル確定後に `finalize_bindless` を呼ぶため、
    /// 生の構築だけをここに分離する（オーバーライドで texture/factor が変わっても登録が
    /// 一度で済むようにするため＝二重登録を避ける）。
    fn upload_model_raw(&self, model: &Model) -> GpuModelInner {
        GpuModelInner::upload(
            &self.device,
            &self.queue,
            model,
            &self.pipelines.mesh.material_bgl,
            &self.pipelines.skinned_mesh.joint_bgl,
            &self.defaults,
        )
    }

    /// バインドレス（B2）対応 GPU でのみ、実効マテリアル確定後の GpuModel を
    /// テクスチャ配列・UV/index メガバッファへ登録する（非対応 GPU では何もしない）。
    fn finalize_bindless(&self, gpu: &mut GpuModelInner, model: &Model) {
        if let Some(cell) = &self.bindless {
            let mut b = cell.borrow_mut();
            gpu.register_bindless(model, &self.queue, &mut b);
        }
    }

    pub fn upload_model(&self, model: &Model) -> GpuModelInner {
        let mut gpu = self.upload_model_raw(model);
        self.finalize_bindless(&mut gpu, model);
        gpu
    }

    /// モデルをアップロードし、マテリアルオーバーライド（Phase R7）を GPU マテリアルへ
    /// 焼き込んだ `GpuModel` を返す。
    ///
    /// `overrides` が空スライスなら `apply_overrides` のループが 0 回実行されるだけで
    /// `upload_model` と完全に同一の結果になる（オーバーライド無しモデルの描画経路・
    /// 性能は一切変わらない）。
    pub fn upload_model_with_overrides(
        &self,
        model:     &Model,
        overrides: &[crate::engine::components::MaterialOverride],
    ) -> GpuModelInner {
        let mut gpu_model = self.upload_model_raw(model);
        gpu_model.apply_overrides(
            &self.device,
            &self.queue,
            model,
            overrides,
            &self.pipelines.mesh.material_bgl,
            &self.defaults,
        );
        // オーバーライドで texture/factor が確定した後に一度だけバインドレス登録する。
        self.finalize_bindless(&mut gpu_model, model);
        gpu_model
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
