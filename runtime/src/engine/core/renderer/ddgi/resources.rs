// ============================================================
// ddgi/resources.rs - GiResources: DDGI GPU resource manager
//
// Owns the probe atlases (radiance + visibility, each with a history copy for
// temporal blend), the GiParams uniform buffers (live + disabled), the linear
// sampler, and the probe-update compute bind group. Mirrors the two-phase
// creation used by ClusterResources (buffers first, compute BG attached later
// once the light buffer + TLAS exist).
//
// Ping-pong avoidance: instead of swapping which atlas the fragment samples
// (which would force rebuilding the group-4 bind groups every frame), we keep a
// single stable atlas that the fragment always samples, plus a history texture.
// Each frame: copy atlas -> history, then the compute reads history (for the
// hysteresis blend + recursive bounce) and writes the atlas in place for the
// rotated subset of probes. Probes not updated this frame keep their value.
//
// Atlas format note: both atlases are Rgba16Float (see ddgi/mod.rs) because the
// compute writes them as storage textures and rg16float is not a core storage
// format. Visibility uses .rg (mean depth, mean depth^2); radiance uses .rgb.
// ============================================================

use std::cell::Cell;

use wgpu::util::DeviceExt;

use super::{
    params::GiParams, grid::GiGrid, GI_ATLAS_FORMAT, GI_DEFAULT_DIMS, GI_PROBE_WG_THREADS,
};
use crate::engine::core::renderer::post::GiSettings;

/// DDGI の GPU リソース一式（RT 対応 GPU でのみ compute が機能する）。
pub struct GiResources {
    /// RT 対応 GPU か（EXPERIMENTAL_RAY_QUERY）。false なら compute は attach されず GI は無効。
    supported: bool,
    /// プローブ格子（次元は固定=GI_DEFAULT_DIMS、原点/間隔はシーンにフィット）。内部可変（Cell）。
    grid: Cell<GiGrid>,

    /// ライブ GiParams（uniform, group4 binding10 のメインカメラ用 / compute group0 binding0）。
    params_buffer: wgpu::Buffer,
    /// enabled=0 固定の GiParams（カメラプレビュー / GI 無効時に bind する）。
    params_disabled_buffer: wgpu::Buffer,

    /// 放射輝度アトラス（fragment がサンプル・compute が storage 書き込み）。
    irradiance_view: wgpu::TextureView,
    irradiance_tex: wgpu::Texture,
    /// 放射輝度の履歴（毎フレーム atlas からコピー。compute が読む）。
    irradiance_hist_view: wgpu::TextureView,
    irradiance_hist_tex: wgpu::Texture,
    /// 可視性アトラス。
    visibility_view: wgpu::TextureView,
    visibility_tex: wgpu::Texture,
    /// 可視性の履歴。
    visibility_hist_view: wgpu::TextureView,
    visibility_hist_tex: wgpu::Texture,

    /// リニア clamp サンプラー（アトラスサンプリング用）。
    sampler: wgpu::Sampler,

    /// プローブ更新 compute の group0 BindGroup（attach 後に Some）。
    compute_bg: Option<wgpu::BindGroup>,

    /// フレーム番号（レイ回転ノイズ用。毎フレーム +1）。
    frame_index: Cell<u32>,
    /// 更新ローテーションの先頭プローブ番号カーソル。
    probe_cursor: Cell<u32>,
    /// アトラス/履歴をゼロクリア済みか（初回 record で 1 回クリアする）。
    cleared: Cell<bool>,
}

impl GiResources {
    /// アトラス2枚（＋履歴2枚）・GiParams バッファ・サンプラーを生成する。
    /// compute BindGroup は `attach`（ライトバッファ・TLAS・平均アルベドが揃った後）で作る。
    pub fn new(device: &wgpu::Device, supported: bool) -> Self {
        // 次元は固定（UI では変えない）。原点/間隔は fit で後から設定する。
        let grid = GiGrid { dims: GI_DEFAULT_DIMS, origin: [0.0; 3], spacing: [1.0; 3] };

        let (iw, ih) = grid.irradiance_atlas_size();
        let (vw, vh) = grid.visibility_atlas_size();

        // アトラス（サンプル＋storage 書込＋コピー元/先）。
        let atlas_usage = wgpu::TextureUsages::STORAGE_BINDING
            | wgpu::TextureUsages::TEXTURE_BINDING
            | wgpu::TextureUsages::COPY_SRC
            | wgpu::TextureUsages::COPY_DST;
        // 履歴（サンプル＝texture_2d 読み＋コピー先）。
        let hist_usage = wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST;

        let mk = |label: &str, w: u32, h: u32, usage: wgpu::TextureUsages| {
            let tex = device.create_texture(&wgpu::TextureDescriptor {
                label: Some(label),
                size: wgpu::Extent3d { width: w.max(1), height: h.max(1), depth_or_array_layers: 1 },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: GI_ATLAS_FORMAT,
                usage,
                view_formats: &[],
            });
            let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
            (tex, view)
        };
        let (irradiance_tex, irradiance_view) = mk("GI Irradiance Atlas", iw, ih, atlas_usage);
        let (irradiance_hist_tex, irradiance_hist_view) = mk("GI Irradiance Hist", iw, ih, hist_usage);
        let (visibility_tex, visibility_view) = mk("GI Visibility Atlas", vw, vh, atlas_usage);
        let (visibility_hist_tex, visibility_hist_view) = mk("GI Visibility Hist", vw, vh, hist_usage);

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("GI Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        let disabled = GiParams::disabled(&grid);
        let params_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("GI Params Uniform (live)"),
            contents: bytemuck::bytes_of(&disabled),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let params_disabled_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("GI Params Uniform (disabled)"),
            contents: bytemuck::bytes_of(&disabled),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        Self {
            supported,
            grid: Cell::new(grid),
            params_buffer,
            params_disabled_buffer,
            irradiance_view, irradiance_tex,
            irradiance_hist_view, irradiance_hist_tex,
            visibility_view, visibility_tex,
            visibility_hist_view, visibility_hist_tex,
            sampler,
            compute_bg: None,
            frame_index: Cell::new(0),
            probe_cursor: Cell::new(0),
            cleared: Cell::new(false),
        }
    }

    // --- accessors used by lighting.rs group-4 bind groups (bindings 10-13) ---
    pub fn irradiance_view(&self) -> &wgpu::TextureView { &self.irradiance_view }
    pub fn visibility_view(&self) -> &wgpu::TextureView { &self.visibility_view }
    pub fn sampler(&self) -> &wgpu::Sampler { &self.sampler }
    pub fn params_buffer(&self) -> &wgpu::Buffer { &self.params_buffer }
    pub fn params_disabled_buffer(&self) -> &wgpu::Buffer { &self.params_disabled_buffer }
    pub fn grid(&self) -> GiGrid { self.grid.get() }
    pub fn is_attached(&self) -> bool { self.compute_bg.is_some() }

    /// シーン AABB へプローブ格子をフィットさせる（次元は固定。原点/間隔のみ更新）。
    /// アトラス寸法は次元固定のため変わらず、テクスチャ再生成は不要。
    /// シーンロード/モデル変化時に呼ぶこと（毎フレームではない）。
    pub fn fit(&self, aabb_min: [f32; 3], aabb_max: [f32; 3]) {
        // 次元は固定のためアトラス寸法は不変。原点/間隔のみ更新（内部可変）。
        self.grid.set(GiGrid::fit_from_aabb(aabb_min, aabb_max, GI_DEFAULT_DIMS));
    }

    /// プローブ更新 compute の group0 BindGroup を構築する（RT 対応かつ TLAS がある場合のみ）。
    /// ライトバッファ・LightMeta・TLAS・平均アルベド storage が揃った後に呼ぶ。
    ///
    /// - `bgl`:        GiUpdatePipeline の BGL（pipeline.rs）。
    /// - `lights`:     ライト storage（LightBuffer と共有）。
    /// - `light_meta`: LightMeta uniform（メインカメラ用）。
    /// - `tlas`:       RT 影と共有する TLAS。
    /// - `albedo`:     TLAS インスタンス順の平均アルベド storage（rt_shadow.rs 所有）。
    #[allow(clippy::too_many_arguments)]
    pub fn attach(
        &mut self,
        device: &wgpu::Device,
        bgl: &wgpu::BindGroupLayout,
        lights: &wgpu::Buffer,
        light_meta: &wgpu::Buffer,
        tlas: &wgpu::Tlas,
        albedo: &wgpu::Buffer,
    ) {
        if !self.supported { return; }
        self.compute_bg = Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("GI Update BG (group 0)"),
            layout: bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: self.params_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: lights.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: light_meta.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::AccelerationStructure(tlas) },
                wgpu::BindGroupEntry { binding: 4, resource: albedo.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 5, resource: wgpu::BindingResource::TextureView(&self.irradiance_hist_view) },
                wgpu::BindGroupEntry { binding: 6, resource: wgpu::BindingResource::TextureView(&self.visibility_hist_view) },
                wgpu::BindGroupEntry { binding: 7, resource: wgpu::BindingResource::Sampler(&self.sampler) },
                wgpu::BindGroupEntry { binding: 8, resource: wgpu::BindingResource::TextureView(&self.irradiance_view) },
                wgpu::BindGroupEntry { binding: 9, resource: wgpu::BindingResource::TextureView(&self.visibility_view) },
            ],
        }));
    }

    /// このフレームのライブ GiParams を書き込み、ローテーションカーソル/フレーム番号を進める。
    /// 戻り値は「このフレームで更新するプローブ数」（dispatch のワークグループ数）。
    ///
    /// `active` は「GI をこのフレーム実際に走らせるか」（RT 対応 && 設定 enabled && attach 済み）。
    /// 非 active でも params は enabled=0 で書き、描画側はフラットアンビエントへフォールバックする。
    /// アトラス4枚（現行＋履歴）をゼロで初期化する（初回のみ）。
    ///
    /// 【なぜ encoder.clear_texture ではないか】clear_texture は wgpu の
    /// Features::CLEAR_TEXTURE を要求し、本エンジンは要求していないため
    /// 実行時に検証パニックする（実機スモークテストで発覚）。
    /// Queue::write_texture はコア機能でフィーチャー不要のため、ゼロ埋めデータの
    /// アップロードで初期化する。1 回きり（数 MB）なのでコストは無視できる。
    fn ensure_zero_init(&self, queue: &wgpu::Queue) {
        if self.cleared.get() {
            return;
        }
        /// Rgba16Float の 1 ピクセルあたりバイト数（4ch × f16 2バイト）。
        const BYTES_PER_PIXEL: u32 = 8;
        let mut zero_fill = |tex: &wgpu::Texture| {
            let size = tex.size();
            let bytes = vec![0u8; (size.width * size.height * BYTES_PER_PIXEL) as usize];
            queue.write_texture(
                tex.as_image_copy(),
                &bytes,
                wgpu::TexelCopyBufferLayout {
                    offset:         0,
                    bytes_per_row:  Some(size.width * BYTES_PER_PIXEL),
                    rows_per_image: Some(size.height),
                },
                size,
            );
        };
        zero_fill(&self.irradiance_tex);
        zero_fill(&self.irradiance_hist_tex);
        zero_fill(&self.visibility_tex);
        zero_fill(&self.visibility_hist_tex);
        self.cleared.set(true);
    }

    pub fn update_params(&self, queue: &wgpu::Queue, gi: &GiSettings, active: bool) -> u32 {
        // 初回のみアトラスをゼロ初期化（未初期化 storage テクスチャの読みを避ける）。
        self.ensure_zero_init(queue);
        let grid = self.grid.get();
        let probe_count = grid.probe_count().max(1);
        let base = self.probe_cursor.get() % probe_count;
        let frame = self.frame_index.get();
        let ppf = gi.probes_per_frame.clamp(1, probe_count);

        let live = GiParams::new(
            &grid, active, gi.rays_per_probe, base, frame,
            gi.intensity, gi.hysteresis, gi.recursive_weight,
        );
        queue.write_buffer(&self.params_buffer, 0, bytemuck::bytes_of(&live));
        // disabled 側も格子だけ最新にしておく（fit で原点/間隔が変わり得るため）。
        let disabled = GiParams::disabled(&grid);
        queue.write_buffer(&self.params_disabled_buffer, 0, bytemuck::bytes_of(&disabled));

        // ローテーションを進める（次フレームは続きのプローブ群を更新）。
        self.probe_cursor.set((base + ppf) % probe_count);
        self.frame_index.set(frame.wrapping_add(1));
        ppf
    }

    /// プローブ更新 compute を command encoder へ記録する（active かつ attach 済みのときのみ）。
    ///   1. 初回はアトラス/履歴をゼロクリアする。
    ///   2. atlas -> history をコピー（compute のヒステリシス読み用）。
    ///   3. compute で更新対象プローブ（ppf 個）を再計算し atlas を上書きする。
    pub fn record(
        &self,
        encoder: &mut wgpu::CommandEncoder,
        pipeline: &wgpu::ComputePipeline,
        probes_this_frame: u32,
    ) {
        let Some(bg) = self.compute_bg.as_ref() else { return; };
        if probes_this_frame == 0 { return; }

        // 1. 初回クリアは update_params 内の ensure_zero_init（write_texture 方式）で
        //    実施済み（clear_texture は CLEAR_TEXTURE フィーチャーが必要なため使わない）。

        // 2. atlas -> history コピー（全面）。
        let copy = |enc: &mut wgpu::CommandEncoder, src: &wgpu::Texture, dst: &wgpu::Texture| {
            let size = src.size();
            enc.copy_texture_to_texture(src.as_image_copy(), dst.as_image_copy(), size);
        };
        copy(encoder, &self.irradiance_tex, &self.irradiance_hist_tex);
        copy(encoder, &self.visibility_tex, &self.visibility_hist_tex);

        // 3. compute ディスパッチ（1 ワークグループ = 1 プローブ、64 スレッド）。
        let _ = GI_PROBE_WG_THREADS; // ワークグループサイズは WGSL 側の @workgroup_size(64) と一致
        let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label: Some("GI Probe Update Pass"),
            timestamp_writes: None,
        });
        pass.set_pipeline(pipeline);
        pass.set_bind_group(0, bg, &[]);
        pass.dispatch_workgroups(probes_this_frame, 1, 1);
    }
}
