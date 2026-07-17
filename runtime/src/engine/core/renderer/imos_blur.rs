// ============================================================
//  imos_blur.rs — いもす法（累積和）単一チャンネル分離ボックスブラー基盤（再利用モジュール）
//
//  ## 役割（単一責任）
//  「単一チャンネルの低周波信号（AO 等）を、半径非依存 O(n) のいもす法ボックスブラーで
//  ぼかす」コンピュートパイプライン一式と、ping-pong 実行ヘルパー（record）だけを持つ。
//  対象テクスチャの確保・ブラー反復回数は呼び出し側（ao.rs 等）が決める。本モジュールは
//  iterations 引数を受け取って H→V を反復するだけで、AO 固有の知識を持たない。
//
//  ## アルゴリズム
//  WGSL 本体（shaders/imos_blur.wgsl）は postfx_blur.wgsl と同一のランニング和。
//  1 サブパス＝1 方向（水平 or 垂直）の走査線ランニング和。CPU 側が方向を切り替えて
//  iterations 回（H,V を 1 反復として）反復し、ボックス 3 回相当のガウシアン近似を作る。
//
//  ## ストレージフォーマット（R16Float 不可 → Rgba16Float 単一チャンネル運用）
//  単一チャンネル R16Float は core WebGPU の storage テクスチャフォーマットに含まれない
//  （wgpu-types::TextureFormat::guaranteed_format_features で R16Float は attachment のみ、
//  STORAGE_BINDING 不可。有効化には TEXTURE_ADAPTER_SPECIFIC_FORMAT_FEATURES が要る）。
//  そのため storage は Rgba16Float（storage 可・かつ filterable なので後段のバイリニア
//  アップサンプルも可能）とし、.r のみを意味あるチャンネルとして使う。
//  postfx_blur.wgsl（rgba16float storage）と同一フォーマットで、実績のある構成。
//
//  ## 結果テクスチャの不変条件（重要）
//  record は各 iteration を H→V の 2 サブパスで実行する。総サブパス数 = 2*iterations（常に偶数）。
//  サブパス列は in≠out（t0/t1 の parity 交互）で storage 読み書き競合が起きない。
//  最終サブパス（k = 2*iterations-1, 奇数）の出力は必ず t1 になるため、
//  ブラー結果は必ず t1 に残る（呼び出し側は t1 を最終 AO として使う）。
// ============================================================

/// いもすブラーの作業/出力ストレージフォーマット。
///
/// 単一チャンネル R16Float は core WebGPU の storage 非対応のため Rgba16Float を使い、
/// .r のみを有効チャンネルとして扱う（上のモジュールコメント参照）。
/// TODO(SSGI): カラーブラー（SSGI 等）へ転用する場合はフォーマットはこのまま、
/// WGSL の load/store を .r → .rgb へ広げるだけでよい（フォーマット変更不要）。
pub const IMOS_BLUR_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

/// いもすブラーパラメータ UBO（WGSL imos_blur.wgsl の ImosBlurParams と #[repr(C)] 一致・16B）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ImosBlurParams {
    /// ボックス半径（画素。>=0）。
    pub radius:     i32,
    /// 走査方向（1=水平 / 0=垂直）。
    pub horizontal: u32,
    /// 対象テクスチャ幅（画素）。
    pub width:      i32,
    /// 対象テクスチャ高さ（画素）。
    pub height:     i32,
}

/// いもす法ボックスブラーのコンピュートパイプライン一式（再利用可能）。
pub struct ImosBlur {
    /// blur_cs コンピュートパイプライン。
    pipeline: wgpu::ComputePipeline,
    /// group0 レイアウト（0=params UBO / 1=入力 texture / 2=出力 storage=IMOS_BLUR_FORMAT）。
    bgl:      wgpu::BindGroupLayout,
}

impl ImosBlur {
    /// いもすブラーのコンピュートパイプラインを構築する（postfx の BlurPipeline::new が手本）。
    pub fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Imos Blur Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/imos_blur.wgsl").into()),
        });
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Imos Blur BGL"),
            entries: &[
                // binding 0: params UBO
                wgpu::BindGroupLayoutEntry {
                    binding:    0,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty:                 wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size:   None,
                    },
                    count: None,
                },
                // binding 1: 入力テクスチャ（textureLoad。filterable=true＝Rgba16Float は filterable）
                wgpu::BindGroupLayoutEntry {
                    binding:    1,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Texture {
                        sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled:   false,
                    },
                    count: None,
                },
                // binding 2: 出力 storage テクスチャ（write, IMOS_BLUR_FORMAT=Rgba16Float）
                wgpu::BindGroupLayoutEntry {
                    binding:    2,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::StorageTexture {
                        access:         wgpu::StorageTextureAccess::WriteOnly,
                        format:         IMOS_BLUR_FORMAT,
                        view_dimension: wgpu::TextureViewDimension::D2,
                    },
                    count: None,
                },
            ],
        });
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Imos Blur Layout"),
            bind_group_layouts:   &[&bgl],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("Imos Blur Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("blur_cs"),
            compilation_options: Default::default(),
            cache,
        });
        Self { pipeline, bgl }
    }

    /// いもすブラーを src → (t0/t1 ping-pong) で iterations 回反復し、結果を t1 へ残す。
    ///
    /// - `src`  : 入力（ブラー前）テクスチャビュー（TEXTURE_BINDING）。
    /// - `t0`   : ping-pong その 1（STORAGE_BINDING|TEXTURE_BINDING, IMOS_BLUR_FORMAT）。
    /// - `t1`   : ping-pong その 2（同上）。結果は必ずここに残る（不変条件）。
    /// - `width`/`height` : 対象（＝各テクスチャ）の画素サイズ。
    /// - `radius`     : ボックス半径（画素。>=0）。
    /// - `iterations` : H→V を 1 とした反復回数（>=1。0 は 1 に丸める）。
    ///
    /// サブパス列（k=0..2*iterations）: horizontal = (k 偶数)、out(k) = (k 偶数)?t0:t1、
    /// in(k) = (k==0)?src:out(k-1)。総サブパス数は常に偶数のため最終出力は必ず t1。
    /// 連続サブパスは in≠out（parity 交互）＝storage 読み書き競合なし。
    #[allow(clippy::too_many_arguments)]
    pub fn record(
        &self,
        device:     &wgpu::Device,
        encoder:    &mut wgpu::CommandEncoder,
        src:        &wgpu::TextureView,
        t0:         &wgpu::TextureView,
        t1:         &wgpu::TextureView,
        width:      i32,
        height:     i32,
        radius:     i32,
        iterations: u32,
    ) {
        let iters = iterations.max(1);       // 0 は無意味。最低 1 反復（=H,V の 2 サブパス）。
        let total = 2 * iters;               // 常に偶数 → 最終出力は t1（不変条件）。
        for k in 0..total {
            let even       = k % 2 == 0;
            let horizontal = if even { 1u32 } else { 0u32 };
            let out: &wgpu::TextureView = if even { t0 } else { t1 };
            // 入力: 先頭は src、以降は 1 つ前のサブパスの出力（parity で交互）。
            let inp: &wgpu::TextureView = if k == 0 {
                src
            } else if (k - 1) % 2 == 0 {
                t0
            } else {
                t1
            };
            self.subpass(device, encoder, inp, out, horizontal, width, height, radius);
        }
    }

    /// 1 サブパス（1 方向の走査線ランニング和）を記録する（postfx bake.rs の blur_step が手本）。
    /// 毎サブパスで transient な params UBO と BindGroup を作る（write_buffer 多重書き回避）。
    #[allow(clippy::too_many_arguments)]
    fn subpass(
        &self,
        device:     &wgpu::Device,
        encoder:    &mut wgpu::CommandEncoder,
        src:        &wgpu::TextureView,
        dst:        &wgpu::TextureView,
        horizontal: u32,
        width:      i32,
        height:     i32,
        radius:     i32,
    ) {
        use wgpu::util::DeviceExt;
        let params = ImosBlurParams { radius, horizontal, width, height };
        let ubo = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Imos Blur Params"),
            contents: bytemuck::bytes_of(&params),
            usage:    wgpu::BufferUsages::UNIFORM,
        });
        let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Imos Blur BG"),
            layout:  &self.bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: ubo.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(src) },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(dst) },
            ],
        });
        let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label:            Some("Imos Blur Pass"),
            timestamp_writes: None,
        });
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &bg, &[]);
        // 走査線本数（水平=行数=高さ / 垂直=列数=幅）を 64 スレッド/ワークグループで割る。
        let count  = if horizontal == 1 { height } else { width };
        let groups = ((count.max(0) as u32) + 63) / 64;
        pass.dispatch_workgroups(groups.max(1), 1, 1);
    }
}

// ============================================================
//  ユニットテスト（CPU 参照一致＋WGSL naga 検証）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// WGSL と同式のいもす法ランニング和ボックス平均（CPU 参照）。
    /// WGSL imos_blur.wgsl の blur_cs と同一:
    ///   load はクランプ読み、norm=1/(2r+1)、初期窓 [-r,r] を合算し、
    ///   1 画素進むごとに sum += load(i+r+1) - load(i-r)。
    fn imos_running_box_1d(input: &[f32], r: i32) -> Vec<f32> {
        let len = input.len() as i32;
        let load = |pos: i32| input[pos.clamp(0, len - 1) as usize];
        let norm = 1.0f32 / (2 * r + 1) as f32;
        let mut sum = 0.0f32;
        for k in -r..=r {
            sum += load(k);
        }
        let mut out = vec![0.0f32; len as usize];
        for i in 0..len {
            out[i as usize] = sum * norm;
            sum += load(i + r + 1) - load(i - r);
        }
        out
    }

    /// 素朴なボックス平均（各画素で窓 [i-r, i+r] を毎回合算。端はクランプ）。
    fn naive_box_1d(input: &[f32], r: i32) -> Vec<f32> {
        let len = input.len() as i32;
        let load = |pos: i32| input[pos.clamp(0, len - 1) as usize];
        (0..len)
            .map(|i| {
                let mut s = 0.0f32;
                for k in (i - r)..=(i + r) {
                    s += load(k);
                }
                s / (2 * r + 1) as f32
            })
            .collect()
    }

    /// いもす法ランニング和が素朴ボックス平均と一致する（複数半径・1D と 2D 水平パス）。
    #[test]
    fn imos_running_sum_matches_naive_box_average() {
        // 1D（長さ 8）を r=0,1,2 で検証。整数入力なので合算に丸めが入らず厳密一致する。
        let v1d: [f32; 8] = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0];
        for r in 0..=2 {
            let a = imos_running_box_1d(&v1d, r);
            let b = naive_box_1d(&v1d, r);
            for i in 0..a.len() {
                assert!((a[i] - b[i]).abs() < 1e-4,
                    "1D r={r} i={i}: imos={} naive={}", a[i], b[i]);
            }
        }

        // 2D（4x4）の水平パス（各行を独立にボックス平均）を r=0,1,2 で検証。
        // WGSL の水平パス（horizontal=1）は 1 走査線＝1 行。行ごとに 1D ブラーと同一。
        let m: [[f32; 4]; 4] = [
            [1.0, 2.0, 3.0, 4.0],
            [5.0, 6.0, 7.0, 8.0],
            [9.0, 10.0, 11.0, 12.0],
            [13.0, 14.0, 15.0, 16.0],
        ];
        for r in 0..=2 {
            for row in &m {
                let a = imos_running_box_1d(row, r);
                let b = naive_box_1d(row, r);
                for i in 0..4 {
                    assert!((a[i] - b[i]).abs() < 1e-4,
                        "2D r={r} i={i}: imos={} naive={}", a[i], b[i]);
                }
            }
        }
    }

    /// ImosBlurParams は 16 バイト（WGSL imos_blur.wgsl の ImosBlurParams と一致）。
    #[test]
    fn imos_blur_params_is_16_bytes() {
        assert_eq!(std::mem::size_of::<ImosBlurParams>(), 16);
    }

    /// imos_blur.wgsl を naga で parse + validate する（RAY_QUERY 不要）。
    #[test]
    fn imos_blur_wgsl_parses_and_validates() {
        let src = include_str!("shaders/imos_blur.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[imos_blur] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(), naga::valid::Capabilities::empty());
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[imos_blur] WGSL validate 失敗: {e:?}"));
    }
}
