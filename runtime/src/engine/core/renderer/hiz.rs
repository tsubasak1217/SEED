// ============================================================
//  hiz.rs — Hi-Z オクルージョンカリングシステム
//
//  ## フレームごとの流れ（frame_renderer で配線済み・SEED_OCCLUSION_CULL=1 で有効）
//  0. try_read_results(device) → Option<Vec<u32>>  — 前フレーム結果の取得（1 フレーム遅延）
//  1. set_instances(device, queue, &aabbs)  — 対象 AABB を GPU へ設定
//  2. build_pyramid(encoder, depth_view, device)  — G-Buffer 深度 → Hi-Z ミップ生成
//     （深度は Depth24PlusStencil8 の DepthOnly ビュー。専用の深度プリパスは使わない）
//  3. dispatch_occlusion(encoder, camera_buf, device) — インスタンス単位オクルージョンテスト
//  4. schedule_readback(encoder)  — 結果を result_buf → staging へコピー
//  5. (GPU submit) → map_after_submit()  — staging のマップ予約
//  6. 次フレームの 0. で受領する
// ============================================================

use wgpu::util::DeviceExt;

use super::uniforms::GpuCullData;

/// GPU→CPU 可視性読み戻しのマップ完了通知（`map_after_submit` → `try_read_results`）。
type MapResult = Result<(), wgpu::BufferAsyncError>;

// ============================================================
//  ScreenInfo ユニフォーム（オクルージョンシェーダー用）
// ============================================================

/// オクルージョンテストシェーダーへ渡すスクリーン情報 uniform（binding 4）。
///
/// Hi-Z テクスチャの解像度とミップ数を GPU 側へ伝える。`resize` でサイズ変更時に更新される。
#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct ScreenInfo {
    width:      f32,
    height:     f32,
    mip_levels: u32,
    _pad:       u32,
}

// ============================================================
//  HiZSystem
// ============================================================

/// Hi-Z ピラミッド生成 + インスタンス単位オクルージョンテストシステム。
///
/// `build_pyramid` → `dispatch_occlusion` → `schedule_readback` の順で呼び出し、
/// GPU submit 後に `try_read_results` で前フレームの結果を取得する（1フレーム遅延）。
pub struct HiZSystem {
    // ── Hi-Z テクスチャ（R32Float, ミップチェーン付き）─────────
    pub hiz_texture:   wgpu::Texture,
    /// ミップレベル 0 からのフルビュー（オクルージョンシェーダーがサンプル）
    pub hiz_full_view: wgpu::TextureView,
    /// ミップレベルごとのストレージビュー（生成シェーダーが書き込み）
    hiz_mip_views:     Vec<wgpu::TextureView>,
    pub hiz_width:     u32,
    pub hiz_height:    u32,
    pub mip_levels:    u32,

    // ── 深度コピーパイプライン（D32Float → R32Float mip0）─────
    copy_pipeline:     wgpu::ComputePipeline,
    copy_bgl:          wgpu::BindGroupLayout,

    // ── ミップ生成パイプライン（mip[n-1] → mip[n]）────────────
    gen_pipeline:      wgpu::ComputePipeline,
    gen_bgl:           wgpu::BindGroupLayout,

    // ── オクルージョンテストパイプライン ─────────────────────
    occ_pipeline:      wgpu::ComputePipeline,
    occ_bgl:           wgpu::BindGroupLayout,

    // ── スクリーン情報ユニフォーム ────────────────────────────
    screen_buf:        wgpu::Buffer,

    // ── インスタンス AABB バッファ（world space AABB, CPU→GPU 書き込み）─────
    /// オクルージョンシェーダー（binding 0）が読む `GpuCullData` 配列。
    /// `set_instances` で毎フレーム上書きする。capacity 個ぶん確保する。
    aabb_buf:      wgpu::Buffer,   // STORAGE(read) | COPY_DST

    // ── 可視性結果バッファ ────────────────────────────────────
    /// オクルージョンシェーダー（binding 3）が書く可視性（0=遮蔽 / 1=可視）。
    result_buf:    wgpu::Buffer,   // STORAGE | COPY_SRC (GPU write)
    /// 可視性を CPU へ読み戻すためのステージングバッファ（1 フレーム遅延読み取り）。
    staging_buf:   wgpu::Buffer,   // MAP_READ | COPY_DST

    /// aabb_buf / result_buf / staging_buf が確保しているインスタンス上限（要素数）。
    /// 実インスタンス数がこれを超えたら 3 バッファをまとめて作り直す。
    capacity:      u32,
    /// このフレームで実際にオクルージョンテストするインスタンス数（≤ capacity）。
    num_instances: u32,

    // ── 読み戻し状態（1 フレーム遅延の GPU→CPU 可視性フィードバック）─────────
    /// `map_after_submit` で予約したマップ完了通知の受信端。`try_read_results` が
    /// 消費する。None＝読み戻しアイドル（次のディスパッチを予約可能）。
    prev_pending:  Option<std::sync::mpsc::Receiver<MapResult>>,
    /// prev_pending が対応するインスタンス数（staging_buf の有効バイト数の算出に使う）。
    pending_len:   u32,
}

impl HiZSystem {
    pub fn new(
        device:        &wgpu::Device,
        width:         u32,
        height:        u32,
        num_instances: u32,
    ) -> Self {
        let mip_levels = mip_count(width, height);

        // ── Hi-Z テクスチャ ───────────────────────────────────
        let (hiz_texture, hiz_full_view, hiz_mip_views) =
            create_hiz_texture(device, width, height, mip_levels);

        // ── 深度コピーパイプライン ────────────────────────────
        let (copy_pipeline, copy_bgl) = create_copy_pipeline(device);

        // ── ミップ生成パイプライン ────────────────────────────
        let (gen_pipeline, gen_bgl) = create_gen_pipeline(device);

        // ── オクルージョンテストパイプライン ─────────────────
        let (occ_pipeline, occ_bgl) = create_occ_pipeline(device);

        // ── スクリーン情報バッファ ────────────────────────────
        let screen_info = ScreenInfo {
            width:      width as f32,
            height:     height as f32,
            mip_levels,
            _pad:       0,
        };
        let screen_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("HiZ ScreenInfo Buffer"),
            contents: bytemuck::bytes_of(&screen_info),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        // ── インスタンス AABB／結果／ステージングバッファ ─────────
        let capacity = num_instances.max(1);
        let (aabb_buf, result_buf, staging_buf) = create_instance_buffers(device, capacity);

        Self {
            hiz_texture,
            hiz_full_view,
            hiz_mip_views,
            hiz_width:  width,
            hiz_height: height,
            mip_levels,
            copy_pipeline,
            copy_bgl,
            gen_pipeline,
            gen_bgl,
            occ_pipeline,
            occ_bgl,
            screen_buf,
            aabb_buf,
            result_buf,
            staging_buf,
            capacity,
            num_instances: 0,
            prev_pending:  None,
            pending_len:   0,
        }
    }

    /// 描画解像度に Hi-Z テクスチャを追従させる（サイズ変化時のみ再生成）。
    ///
    /// 毎フレーム G-Buffer 深度から Hi-Z を作る前に呼ぶ。リサイズ処理を専用イベントに
    /// 頼らず「実際に使う深度ビューのサイズ」へ合わせることで、サーフェス・深度・Hi-Z の
    /// 三者不一致（Vulkan の extent クランプ等）を確実に防ぐ。
    pub fn ensure_size(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, width: u32, height: u32) {
        if width == 0 || height == 0 { return; }
        if self.hiz_width == width && self.hiz_height == height { return; }
        self.resize(device, queue, width, height);
    }

    /// このフレームのオクルージョンテスト対象（world space AABB 配列）を GPU へ設定する。
    ///
    /// 容量不足なら 3 バッファ（aabb/result/staging）をまとめて作り直す。作り直しは
    /// 進行中の読み戻し（prev_pending）を無効化するため破棄する（保守側＝結果不明として扱う）。
    pub fn set_instances(
        &mut self,
        device: &wgpu::Device,
        queue:  &wgpu::Queue,
        aabbs:  &[GpuCullData],
    ) {
        let n = aabbs.len() as u32;
        if n > self.capacity {
            // 余裕を持たせて再確保（チャンク数の小刻みな増減で毎回作り直さない）。
            let new_cap = (n + n / 2).max(n).max(1);
            let (a, r, s) = create_instance_buffers(device, new_cap);
            self.aabb_buf    = a;
            self.result_buf  = r;
            self.staging_buf = s;
            self.capacity    = new_cap;
            // 旧バッファに紐づく読み戻しは破棄（staging を作り直したため）。
            self.prev_pending = None;
            self.pending_len  = 0;
        }
        if n > 0 {
            queue.write_buffer(&self.aabb_buf, 0, bytemuck::cast_slice(aabbs));
        }
        self.num_instances = n;
    }

    /// 読み戻しがアイドル（新しいディスパッチ＋読み戻しを予約可能）かどうか。
    ///
    /// 前フレームの読み戻しがまだ `try_read_results` で消費されていない間は false を返し、
    /// 呼び出し側は新規ディスパッチ／`schedule_readback`／`map_after_submit` を見送る
    /// （マップ中の staging へ COPY / 二重 map するのを防ぐ）。
    pub fn readback_idle(&self) -> bool { self.prev_pending.is_none() }

    /// ウィンドウリサイズ時に Hi-Z テクスチャを再生成する。
    pub fn resize(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, width: u32, height: u32) {
        self.hiz_width  = width;
        self.hiz_height = height;
        self.mip_levels = mip_count(width, height);

        let (tex, full_view, mip_views) = create_hiz_texture(device, width, height, self.mip_levels);
        self.hiz_texture  = tex;
        self.hiz_full_view = full_view;
        self.hiz_mip_views = mip_views;

        // スクリーン情報を更新
        let info = ScreenInfo {
            width:      width as f32,
            height:     height as f32,
            mip_levels: self.mip_levels,
            _pad:       0,
        };
        queue.write_buffer(&self.screen_buf, 0, bytemuck::bytes_of(&info));
    }

    // ── ① Hi-Z ピラミッド生成 ─────────────────────────────────

    /// 深度バッファから Hi-Z ミップピラミッドを生成する。
    ///
    /// 深度プリパスの後、メインレンダーパスの前に呼び出すこと。
    pub fn build_pyramid(
        &self,
        encoder:    &mut wgpu::CommandEncoder,
        depth_view: &wgpu::TextureView,
        device:     &wgpu::Device,
    ) {
        let mut cpass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label:            Some("HiZ Build"),
            timestamp_writes: None,
        });

        // パス 1: depth → mip 0
        {
            let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:  Some("HiZ Copy BG"),
                layout: &self.copy_bgl,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(depth_view) },
                    wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(&self.hiz_mip_views[0]) },
                ],
            });
            cpass.set_pipeline(&self.copy_pipeline);
            cpass.set_bind_group(0, &bg, &[]);
            let (gx, gy) = dispatch_size(self.hiz_width, self.hiz_height, 8);
            cpass.dispatch_workgroups(gx, gy, 1);
        }

        // パス 2〜N: mip[n-1] → mip[n]
        for mip in 1..self.mip_levels as usize {
            let src_w = (self.hiz_width  >> (mip - 1)).max(1);
            let src_h = (self.hiz_height >> (mip - 1)).max(1);
            let dst_w = (self.hiz_width  >> mip).max(1);
            let dst_h = (self.hiz_height >> mip).max(1);

            // mip[n-1] のサンプリング用ビューを一時生成
            let src_view = self.hiz_texture.create_view(&wgpu::TextureViewDescriptor {
                base_mip_level:   (mip - 1) as u32,
                mip_level_count:  Some(1),
                dimension:        Some(wgpu::TextureViewDimension::D2),
                ..Default::default()
            });

            let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:  Some("HiZ Gen BG"),
                layout: &self.gen_bgl,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(&src_view) },
                    wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(&self.hiz_mip_views[mip]) },
                ],
            });
            cpass.set_pipeline(&self.gen_pipeline);
            cpass.set_bind_group(0, &bg, &[]);
            let (gx, gy) = dispatch_size(dst_w, dst_h, 8);
            cpass.dispatch_workgroups(gx, gy, 1);

            let _ = (src_w, src_h); // suppress unused warnings
        }
    }

    // ── ② オクルージョンテスト ───────────────────────────────

    /// `set_instances` で設定した全インスタンスにオクルージョンテストを GPU で実行する。
    ///
    /// `camera_buf` は `CameraUniform` を持つ Uniform バッファ（先頭 144B のみ読む）。
    /// 結果は `result_buf`（可視性 0/1）に書き込む。`schedule_readback` で staging へ写す。
    pub fn dispatch_occlusion(
        &self,
        encoder:    &mut wgpu::CommandEncoder,
        camera_buf: &wgpu::Buffer,
        device:     &wgpu::Device,
    ) {
        if self.num_instances == 0 { return; }

        let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("HiZ Occlusion BG"),
            layout: &self.occ_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: self.aabb_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: camera_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(&self.hiz_full_view) },
                wgpu::BindGroupEntry { binding: 3, resource: self.result_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 4, resource: self.screen_buf.as_entire_binding() },
            ],
        });

        let mut cpass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label:            Some("HiZ Occlusion"),
            timestamp_writes: None,
        });
        cpass.set_pipeline(&self.occ_pipeline);
        cpass.set_bind_group(0, &bg, &[]);
        let groups = self.num_instances.div_ceil(64);
        cpass.dispatch_workgroups(groups, 1, 1);
    }

    // ── ③ 可視性の読み戻し（1 フレーム遅延）───────────────────────

    /// 可視性結果（`result_buf`）をステージングバッファへコピーする。
    ///
    /// `dispatch_occlusion` と同じエンコーダに積む。submit 後に `map_after_submit` を呼ぶ。
    pub fn schedule_readback(&self, encoder: &mut wgpu::CommandEncoder) {
        if self.num_instances == 0 { return; }
        let bytes = self.num_instances as u64 * 4;
        encoder.copy_buffer_to_buffer(&self.result_buf, 0, &self.staging_buf, 0, bytes);
    }

    /// `schedule_readback` を積んだエンコーダを **submit した後** に呼び、staging バッファの
    /// マップを予約する。完了は次フレームの `try_read_results` で受け取る（1 フレーム遅延）。
    ///
    /// 呼び出し前に `readback_idle()` が true であること（前回のマップを消費済みであること）。
    pub fn map_after_submit(&mut self) {
        if self.num_instances == 0 || self.prev_pending.is_some() { return; }
        let bytes = self.num_instances as u64 * 4;
        let (tx, rx) = std::sync::mpsc::channel::<MapResult>();
        self.staging_buf
            .slice(0..bytes)
            .map_async(wgpu::MapMode::Read, move |r| { let _ = tx.send(r); });
        self.prev_pending = Some(rx);
        self.pending_len  = self.num_instances;
    }

    /// 前フレームで予約したマップが完了していれば、可視性（0=遮蔽 / 1=可視）を返す。
    ///
    /// 非ブロッキング（`PollType::Poll`）で判定し、未完了なら `None`（予約は保持し次フレーム再試行）。
    /// 呼び出し側は「結果が無い＝不明＝描く」保守側で扱うこと（遮蔽確定のインスタンスだけ落とす）。
    /// 返る `Vec<u32>` の要素順は、そのディスパッチ時に `set_instances` へ渡した AABB 配列の順。
    pub fn try_read_results(&mut self, device: &wgpu::Device) -> Option<Vec<u32>> {
        // 予約が無ければ何もしない。
        self.prev_pending.as_ref()?;
        // マップコールバックを進める（非ブロッキング）。
        let _ = device.poll(wgpu::PollType::Poll);
        match self.prev_pending.as_ref().unwrap().try_recv() {
            Ok(Ok(())) => {
                let bytes = self.pending_len as u64 * 4;
                let slice = self.staging_buf.slice(0..bytes);
                let data  = slice.get_mapped_range();
                let out: Vec<u32> = bytemuck::cast_slice::<u8, u32>(&data).to_vec();
                drop(data);
                self.staging_buf.unmap();
                self.prev_pending = None;
                Some(out)
            }
            // マップ失敗（デバイス喪失等）: 予約を捨てて保守側（結果なし）へ倒す。
            Ok(Err(_)) => { self.prev_pending = None; None }
            // 未完了: 予約を保持し次フレーム再試行（保守側＝このフレームは結果なし）。
            Err(std::sync::mpsc::TryRecvError::Empty) => None,
            // 送信端消失（通常起きない）: 予約を捨てる。
            Err(std::sync::mpsc::TryRecvError::Disconnected) => { self.prev_pending = None; None }
        }
    }

}

// ============================================================
//  内部ヘルパー
// ============================================================

fn mip_count(width: u32, height: u32) -> u32 {
    let max_dim = width.max(height);
    (u32::BITS - max_dim.leading_zeros()).max(1)
}

fn dispatch_size(width: u32, height: u32, tile: u32) -> (u32, u32) {
    ((width + tile - 1) / tile, (height + tile - 1) / tile)
}

fn create_hiz_texture(
    device:     &wgpu::Device,
    width:      u32,
    height:     u32,
    mip_levels: u32,
) -> (wgpu::Texture, wgpu::TextureView, Vec<wgpu::TextureView>) {
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label:           Some("Hi-Z Texture"),
        size:            wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
        mip_level_count: mip_levels,
        sample_count:    1,
        dimension:       wgpu::TextureDimension::D2,
        format:          wgpu::TextureFormat::R32Float,
        usage:           wgpu::TextureUsages::STORAGE_BINDING
                       | wgpu::TextureUsages::TEXTURE_BINDING,
        view_formats:    &[],
    });

    // 全ミップをサンプリングするフルビュー
    let full_view = texture.create_view(&wgpu::TextureViewDescriptor {
        dimension:       Some(wgpu::TextureViewDimension::D2),
        mip_level_count: Some(mip_levels),
        ..Default::default()
    });

    // ミップごとのストレージビュー（書き込み専用）
    let mip_views: Vec<wgpu::TextureView> = (0..mip_levels)
        .map(|mip| {
            texture.create_view(&wgpu::TextureViewDescriptor {
                base_mip_level:  mip,
                mip_level_count: Some(1),
                dimension:       Some(wgpu::TextureViewDimension::D2),
                ..Default::default()
            })
        })
        .collect();

    (texture, full_view, mip_views)
}

/// インスタンス AABB / 可視性結果 / 読み戻しステージングの 3 バッファをまとめて確保する。
///
/// - aabb:    `GpuCullData`（32B）× capacity。オクルージョンシェーダー binding 0（read）。
/// - result:  u32（4B）× capacity。シェーダー binding 3（write）＋ COPY_SRC。
/// - staging: u32（4B）× capacity。MAP_READ｜COPY_DST（CPU 読み戻し）。
fn create_instance_buffers(
    device:   &wgpu::Device,
    capacity: u32,
) -> (wgpu::Buffer, wgpu::Buffer, wgpu::Buffer) {
    let cap = capacity.max(1) as u64;
    let aabb_size   = (cap * std::mem::size_of::<GpuCullData>() as u64).max(32);
    let result_size = (cap * 4).max(16);

    let aabb_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label:              Some("HiZ AABB Buffer"),
        size:               aabb_size,
        usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });
    let result_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label:              Some("HiZ Result Buffer"),
        size:               result_size,
        usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
        mapped_at_creation: false,
    });
    let staging_buf = device.create_buffer(&wgpu::BufferDescriptor {
        label:              Some("HiZ Readback Staging"),
        size:               result_size,
        usage:              wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });
    (aabb_buf, result_buf, staging_buf)
}

fn create_copy_pipeline(device: &wgpu::Device) -> (wgpu::ComputePipeline, wgpu::BindGroupLayout) {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some("HiZ Copy Shader"),
        source: wgpu::ShaderSource::Wgsl(include_str!("shaders/hiz_copy_depth.wgsl").into()),
    });

    let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label:   Some("HiZ Copy BGL"),
        entries: &[
            // binding 0: depth texture (texture_depth_2d)
            wgpu::BindGroupLayoutEntry {
                binding:    0,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Texture {
                    sample_type:    wgpu::TextureSampleType::Depth,
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            },
            // binding 1: dst R32Float storage (write)
            wgpu::BindGroupLayoutEntry {
                binding:    1,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::StorageTexture {
                    access:         wgpu::StorageTextureAccess::WriteOnly,
                    format:         wgpu::TextureFormat::R32Float,
                    view_dimension: wgpu::TextureViewDimension::D2,
                },
                count: None,
            },
        ],
    });

    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some("HiZ Copy Layout"),
        bind_group_layouts:   &[&bgl],
        push_constant_ranges: &[],
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label:               Some("HiZ Copy Pipeline"),
        layout:              Some(&layout),
        module:              &shader,
        entry_point:         Some("cs_main"),
        compilation_options: Default::default(),
        cache:               None,
    });

    (pipeline, bgl)
}

fn create_gen_pipeline(device: &wgpu::Device) -> (wgpu::ComputePipeline, wgpu::BindGroupLayout) {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some("HiZ Gen Shader"),
        source: wgpu::ShaderSource::Wgsl(include_str!("shaders/hiz_gen_mip.wgsl").into()),
    });

    let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label:   Some("HiZ Gen BGL"),
        entries: &[
            // binding 0: src R32Float sampled
            wgpu::BindGroupLayoutEntry {
                binding:    0,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Texture {
                    sample_type:    wgpu::TextureSampleType::Float { filterable: false },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            },
            // binding 1: dst R32Float storage (write)
            wgpu::BindGroupLayoutEntry {
                binding:    1,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::StorageTexture {
                    access:         wgpu::StorageTextureAccess::WriteOnly,
                    format:         wgpu::TextureFormat::R32Float,
                    view_dimension: wgpu::TextureViewDimension::D2,
                },
                count: None,
            },
        ],
    });

    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some("HiZ Gen Layout"),
        bind_group_layouts:   &[&bgl],
        push_constant_ranges: &[],
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label:               Some("HiZ Gen Pipeline"),
        layout:              Some(&layout),
        module:              &shader,
        entry_point:         Some("cs_main"),
        compilation_options: Default::default(),
        cache:               None,
    });

    (pipeline, bgl)
}

fn create_occ_pipeline(device: &wgpu::Device) -> (wgpu::ComputePipeline, wgpu::BindGroupLayout) {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some("HiZ Occlusion Shader"),
        source: wgpu::ShaderSource::Wgsl(include_str!("shaders/hiz_occlusion.wgsl").into()),
    });

    let make_storage_ro = |binding: u32| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty:                 wgpu::BufferBindingType::Storage { read_only: true },
            has_dynamic_offset: false,
            min_binding_size:   None,
        },
        count: None,
    };
    let make_storage_rw = |binding: u32| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty:                 wgpu::BufferBindingType::Storage { read_only: false },
            has_dynamic_offset: false,
            min_binding_size:   None,
        },
        count: None,
    };
    let make_uniform = |binding: u32| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty:                 wgpu::BufferBindingType::Uniform,
            has_dynamic_offset: false,
            min_binding_size:   None,
        },
        count: None,
    };

    let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label:   Some("HiZ Occlusion BGL"),
        entries: &[
            make_storage_ro(0), // aabbs
            make_uniform(1),    // u_camera
            // binding 2: Hi-Z texture (sampled, R32Float non-filterable)
            wgpu::BindGroupLayoutEntry {
                binding:    2,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Texture {
                    sample_type:    wgpu::TextureSampleType::Float { filterable: false },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            },
            make_storage_rw(3), // visibility output
            make_uniform(4),    // u_screen
        ],
    });

    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some("HiZ Occlusion Layout"),
        bind_group_layouts:   &[&bgl],
        push_constant_ranges: &[],
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label:               Some("HiZ Occlusion Pipeline"),
        layout:              Some(&layout),
        module:              &shader,
        entry_point:         Some("cs_main"),
        compilation_options: Default::default(),
        cache:               None,
    });

    (pipeline, bgl)
}

// ============================================================
//  CPU ミラー（オクルージョン判定ロジックの単体テスト用）
//
//  実運用の判定は hiz_occlusion.wgsl（GPU）が担う。ここでは同じ数式を CPU で
//  再現し、「保守側（AABB が少しでも見える／カメラ背後／画面外なら必ず可視）」と
//  「完全遮蔽（AABB 最近接 z がオクルーダ最大深度より奥）だけ落とす」ことを固定する。
//  cargo test でシェーダーの意図（誤棄却ゼロ）を回帰検証するのが目的。
// ============================================================

#[cfg(test)]
mod tests {
    /// 行優先 4x4 行列 × 点(w=1) → クリップ座標。テスト内の vp も行優先で与える。
    fn mul_vp(vp: &[[f32; 4]; 4], p: [f32; 3]) -> [f32; 4] {
        let e = [p[0], p[1], p[2], 1.0];
        let mut o = [0.0f32; 4];
        for i in 0..4 {
            o[i] = vp[i][0] * e[0] + vp[i][1] * e[1] + vp[i][2] * e[2] + vp[i][3] * e[3];
        }
        o
    }

    /// AABB 8 頂点の投影結果（hiz_occlusion.wgsl の前半に対応）。
    struct Proj {
        min_xy:     [f32; 2],
        max_xy:     [f32; 2],
        min_z:      f32,
        any_behind: bool,
    }

    /// AABB を NDC へ投影する（シェーダーと同じ 8 頂点ループ）。
    fn project_aabb(vp: &[[f32; 4]; 4], min: [f32; 3], max: [f32; 3]) -> Proj {
        let mut min_xy = [1e9f32, 1e9];
        let mut max_xy = [-1e9f32, -1e9];
        let mut min_z = 1e9f32;
        let mut any_behind = false;
        for bx in 0..2 { for by in 0..2 { for bz in 0..2 {
            let p = [
                if bx == 1 { max[0] } else { min[0] },
                if by == 1 { max[1] } else { min[1] },
                if bz == 1 { max[2] } else { min[2] },
            ];
            let c = mul_vp(vp, p);
            if c[3] <= 0.0 {
                any_behind = true;
            } else {
                let ndc = [c[0] / c[3], c[1] / c[3], c[2] / c[3]];
                min_xy[0] = min_xy[0].min(ndc[0]); min_xy[1] = min_xy[1].min(ndc[1]);
                max_xy[0] = max_xy[0].max(ndc[0]); max_xy[1] = max_xy[1].max(ndc[1]);
                min_z = min_z.min(ndc[2]);
            }
        }}}
        Proj { min_xy, max_xy, min_z, any_behind }
    }

    /// オクルージョン判定（true=可視）。シェーダー後半の保守側規則を再現する。
    /// `hiz_max` は AABB のスクリーン矩形における Hi-Z 最大深度（オクルーダ最奥）。
    fn visible(proj: &Proj, hiz_max: f32) -> bool {
        // カメラ背後の頂点を含む → 保守的に可視。
        if proj.any_behind { return true; }
        // NDC 矩形が完全に画面外 → 保守的に可視（CPU フラスタムカリングが主担当）。
        if proj.max_xy[0] < -1.0 || proj.min_xy[0] > 1.0
        || proj.max_xy[1] < -1.0 || proj.min_xy[1] > 1.0 {
            return true;
        }
        // AABB 最近接 z がオクルーダ最大深度より奥 → 完全遮蔽（不可視）。それ以外は可視。
        proj.min_z <= hiz_max
    }

    /// 恒等行列（clip = (x,y,z,1)）: ndc=world, w=1。深度規約 0=near,1=far を world z で表す。
    const IDENTITY: [[f32; 4]; 4] = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ];

    #[test]
    fn front_of_all_occluders_is_visible() {
        // 画面中央・近深度(0.3〜0.4)の AABB。オクルーダは遠景(hiz_max=1.0)。→ 可視。
        let p = project_aabb(&IDENTITY, [-0.2, -0.2, 0.3], [0.2, 0.2, 0.4]);
        assert!(visible(&p, 1.0), "手前の AABB は必ず描く");
    }

    #[test]
    fn fully_behind_occluder_is_culled() {
        // AABB 最近接 z=0.8。オクルーダ最大深度 0.5（手前の山）。0.8>0.5 → 完全遮蔽。
        let p = project_aabb(&IDENTITY, [-0.2, -0.2, 0.8], [0.2, 0.2, 0.9]);
        assert!(!visible(&p, 0.5), "山の裏（完全遮蔽）は落とす");
    }

    #[test]
    fn touching_occluder_is_conservatively_visible() {
        // 最近接 z=0.4、オクルーダ 0.5。0.4<=0.5 → 一部でも手前なら描く（誤棄却防止）。
        let p = project_aabb(&IDENTITY, [-0.2, -0.2, 0.4], [0.2, 0.2, 0.6]);
        assert!(visible(&p, 0.5), "一部でも手前なら描く（保守側）");
    }

    #[test]
    fn revealed_region_becomes_visible() {
        // 遮蔽が解けた領域は背景深度=遠(1.0)。z=0.6 の AABB は 0.6<=1.0 → 即可視（消え残り無し）。
        let p = project_aabb(&IDENTITY, [-0.2, -0.2, 0.6], [0.2, 0.2, 0.7]);
        assert!(visible(&p, 1.0), "遮蔽が解けたら即描く（1 フレーム遅延の自己修復）");
    }

    #[test]
    fn behind_camera_is_visible() {
        // w=z となる vp。z<=0 の頂点を含む AABB → any_behind → 保守的に可視。
        let vp = [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 1.0, 0.0], // w = z
        ];
        let p = project_aabb(&vp, [-0.2, -0.2, -0.1], [0.2, 0.2, 0.5]);
        assert!(p.any_behind);
        assert!(visible(&p, 0.0), "カメラ背後を含む AABB は落とさない");
    }

    #[test]
    fn offscreen_is_visible() {
        // NDC で画面右外(x>1)。完全に画面外 → 保守的に可視（フラスタムカリングに委ねる）。
        let p = project_aabb(&IDENTITY, [2.0, -0.2, 0.3], [3.0, 0.2, 0.4]);
        assert!(!p.any_behind);
        assert!(visible(&p, 0.5), "画面外 AABB はオクルージョンでは落とさない");
    }

    #[test]
    fn deep_but_partially_in_front_is_visible() {
        // 奥行きの深い AABB（z 0.2〜0.95）。最近接 0.2 < hiz_max 0.6 → 可視。
        let p = project_aabb(&IDENTITY, [-0.2, -0.2, 0.2], [0.2, 0.2, 0.95]);
        assert!(visible(&p, 0.6), "最近接が手前なら奥行きが深くても描く");
    }
}
