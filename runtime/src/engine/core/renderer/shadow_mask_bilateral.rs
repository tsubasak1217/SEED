// ============================================================
//  shadow_mask_bilateral.rs — 影マスク専用 separable バイラテラルブラー基盤
//
//  ## 役割（単一責任）
//  「影マスク（半解像度・色付き透過率）を、深度エッジを保持したまま separable
//  バイラテラルブラーで均す」コンピュートパイプライン一式と、H→V の 2 パス実行
//  ヘルパー（record_layer）だけを持つ。対象テクスチャの確保は呼び出し側
//  （shadow_mask.rs / ShadowMaskTargets）が行う。
//
//  ## なぜ imos_blur とは別物なのか
//  いもす法（累積和）は半径非依存 O(1)/画素だが、走査中に窓幅の総和を保持する構造上、
//  途中で深度重みを変えられず**バイラテラル化できない**（原理的非両立）。そのため
//  深度エッジを跨いで影値を混ぜ、カーテンのフチにハローを生む。影マスクはエッジ保持が
//  必須なので固定タップの separable バイラテラルに切り替える（imos_blur は AO/SSGI/
//  すりガラス等の低周波用途で継続使用）。
//
//  ## ストレージフォーマット
//  マスクは Rgba16Float（imos と同じ storage 可・filterable）。.rgb=透過率、.a=深度ガイド
//  （shadow_mask.wgsl が全レイヤの .a に同梱した half-res ビュー空間 z）。textureLoad で読むだけ。
//
//  ## 深度ガイドの供給（別テクスチャなし）
//  深度ガイドはマスク自身の .a（shadow_mask.wgsl が全レイヤの .a に同梱したビュー空間 z）を読む。
//  別 R32Float アタッチメントは 4×Rgba16Float=32B に 4B を足して
//  max_color_attachment_bytes_per_sample=32 を超過するため採らない。出力 .a は中心タップの
//  深度を素通しし、深度自体はブラーしない（H→V の両パスで .a=view_z を維持）。
//
//  ## 結果テクスチャの不変条件
//  record_layer は H（src→tmp）→ V（tmp→dst）の 2 パス。呼び出し側は最終結果を dst 側
//  （mask_b レイヤ）で受け取る。
// ============================================================

use super::imos_blur::IMOS_BLUR_FORMAT;

/// バイラテラルブラーパラメータ UBO（WGSL shadow_mask_bilateral.wgsl の BilateralParams と
/// #[repr(C)] 一致・32B）。σ・深度許容は WGSL 側の名前付き定数（データドリブンな重み定義を
/// シェーダに集約）で、CPU からは半径・走査方向・寸法と、テンポラル EMA の混合率／有効フラグを渡す。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ShadowMaskBilateralParams {
    /// ブラー半径（画素。>=0）。
    pub radius: i32,
    /// 走査方向（1=水平 / 0=垂直）。
    pub horizontal: u32,
    /// 対象テクスチャ幅（画素, half-res）。
    pub width: i32,
    /// 対象テクスチャ高さ（画素, half-res）。
    pub height: i32,
    /// テンポラル EMA の混合率 α（out = mix(history, current, α)）。1.0 = 履歴を使わない。
    pub ema_alpha: f32,
    /// EMA を行うか（1=行う）。H パスは常に 0、V パス（最終出力）だけ CPU の判断で 1 になる。
    pub ema_enable: u32,
    pub _pad0: u32,
    pub _pad1: u32,
}

/// 「履歴を使わない」= 現在値をそのまま採用する α（out = mix(h, cur, 1.0) = cur）。
/// カメラが動いたフレーム・履歴未確立フレームで使う中立値。
pub const EMA_ALPHA_DISABLED: f32 = 1.0;

/// EMA の混合率 α と有効フラグを、履歴が使えるか（`use_history`）から決める純関数。
///
/// - `use_history=false`（カメラが動いた／履歴未確立／リサイズ直後）→ (1.0, 0)＝従来と同一出力。
/// - `use_history=true` → (`alpha`, 1)。α は呼び出し側（shadow_mask.rs の SHADOW_MASK_EMA_ALPHA）。
///
/// α は安全のため (0, 1] にクランプする（0 以下だと履歴が永久に更新されず固着する）。
pub fn ema_params(use_history: bool, alpha: f32) -> (f32, u32) {
    if use_history {
        (alpha.clamp(f32::MIN_POSITIVE, 1.0), 1)
    } else {
        (EMA_ALPHA_DISABLED, 0)
    }
}

/// 影マスク専用 separable バイラテラルブラーのコンピュートパイプライン一式。
pub struct ShadowMaskBilateral {
    /// blur_cs コンピュートパイプライン。
    pipeline: wgpu::ComputePipeline,
    /// group0 レイアウト（0=params / 1=src マスク（.a=深度ガイド）/ 2=dst storage）。
    bgl: wgpu::BindGroupLayout,
}

impl ShadowMaskBilateral {
    /// バイラテラルブラーのコンピュートパイプラインを構築する。
    pub fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Shadow Mask Bilateral Shader"),
            source: wgpu::ShaderSource::Wgsl(
                include_str!("shaders/shadow_mask_bilateral.wgsl").into(),
            ),
        });
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Shadow Mask Bilateral BGL"),
            entries: &[
                // binding 0: params UBO
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
                // binding 1: src マスクテクスチャ（textureLoad。.rgb=透過率, .a=深度ガイド。Rgba16Float は filterable）
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                // binding 2: 出力 storage テクスチャ（write, IMOS_BLUR_FORMAT=Rgba16Float）
                wgpu::BindGroupLayoutEntry {
                    binding: 2,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::StorageTexture {
                        access: wgpu::StorageTextureAccess::WriteOnly,
                        format: IMOS_BLUR_FORMAT,
                        view_dimension: wgpu::TextureViewDimension::D2,
                    },
                    count: None,
                },
                // binding 3: 前フレームの最終マスク（テンポラル EMA の履歴。textureLoad で読むだけ）。
                // ema_enable=0 の H パスでは src と同じビューを bind してダミーにする（読み取り 2 重は合法）。
                wgpu::BindGroupLayoutEntry {
                    binding: 3,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
            ],
        });
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Shadow Mask Bilateral Layout"),
            bind_group_layouts: &[&bgl],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label: Some("Shadow Mask Bilateral Pipeline"),
            layout: Some(&layout),
            module: &shader,
            entry_point: Some("blur_cs"),
            compilation_options: Default::default(),
            cache,
        });
        Self { pipeline, bgl }
    }

    /// 1 レイヤぶんを H（src→tmp）→ V（tmp→dst）の separable 2 パスでバイラテラルブラーする。
    ///
    /// - `src`   : 入力（ブラー前）マスクレイヤ（TEXTURE_BINDING, Rgba16Float。.a=view_z ガイド）。
    /// - `tmp`   : 中間（H パス出力／V パス入力。STORAGE_BINDING|TEXTURE_BINDING, Rgba16Float）。
    /// - `dst`   : 最終（V パス出力。STORAGE_BINDING|TEXTURE_BINDING, Rgba16Float）。結果はここ。
    /// - `hist`  : 前フレームの最終マスク（同レイヤ。dst とは**別テクスチャ**であること＝ping-pong）。
    /// - `width`/`height` : half-res 画素サイズ。
    /// - `radius`: ブラー半径（画素。>=0）。
    /// - `ema_alpha`/`ema_enable` : テンポラル EMA の混合率と有効フラグ（`ema_params` が決める）。
    ///
    /// H と V で src≠dst（別テクスチャ）＝storage 読み書き競合なし。深度ガイドは src の .a を読む
    /// （H パスが中心タップの .a を素通しするため、V パスの入力 tmp にも view_z が残っている）。
    /// EMA は最終出力を作る V パスにだけ融合する（H パスは ema_enable=0・hist に src を渡すダミー）。
    #[allow(clippy::too_many_arguments)]
    pub fn record_layer(
        &self,
        device: &wgpu::Device,
        encoder: &mut wgpu::CommandEncoder,
        src: &wgpu::TextureView,
        tmp: &wgpu::TextureView,
        dst: &wgpu::TextureView,
        hist: &wgpu::TextureView,
        width: i32,
        height: i32,
        radius: i32,
        ema_alpha: f32,
        ema_enable: u32,
    ) {
        // 水平パス: src → tmp（各行を独立にバイラテラル）。EMA なし（hist は src を渡すダミー）。
        self.subpass(
            device, encoder, src, tmp, src, 1, width, height, radius, EMA_ALPHA_DISABLED, 0,
        );
        // 垂直パス: tmp → dst（各列を独立にバイラテラル）＋ EMA。結果は dst に残る。
        self.subpass(
            device, encoder, tmp, dst, hist, 0, width, height, radius, ema_alpha, ema_enable,
        );
    }

    /// 1 サブパス（1 方向の走査線バイラテラル）を記録する。
    /// 毎サブパスで transient な params UBO と BindGroup を作る（imos_blur.rs の subpass と同流儀）。
    #[allow(clippy::too_many_arguments)]
    fn subpass(
        &self,
        device: &wgpu::Device,
        encoder: &mut wgpu::CommandEncoder,
        src: &wgpu::TextureView,
        dst: &wgpu::TextureView,
        hist: &wgpu::TextureView,
        horizontal: u32,
        width: i32,
        height: i32,
        radius: i32,
        ema_alpha: f32,
        ema_enable: u32,
    ) {
        use wgpu::util::DeviceExt;
        let params = ShadowMaskBilateralParams {
            radius,
            horizontal,
            width,
            height,
            ema_alpha,
            ema_enable,
            _pad0: 0,
            _pad1: 0,
        };
        let ubo = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Shadow Mask Bilateral Params"),
            contents: bytemuck::bytes_of(&params),
            usage: wgpu::BufferUsages::UNIFORM,
        });
        let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Shadow Mask Bilateral BG"),
            layout: &self.bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: ubo.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(src),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::TextureView(dst),
                },
                wgpu::BindGroupEntry {
                    binding: 3,
                    resource: wgpu::BindingResource::TextureView(hist),
                },
            ],
        });
        let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label: Some("Shadow Mask Bilateral Pass"),
            timestamp_writes: None,
        });
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &bg, &[]);
        // 走査線本数（水平=行数=高さ / 垂直=列数=幅）を 64 スレッド/ワークグループで割る。
        let count = if horizontal == 1 { height } else { width };
        let groups = ((count.max(0) as u32) + 63) / 64;
        pass.dispatch_workgroups(groups.max(1), 1, 1);
    }
}

// ============================================================
//  ユニットテスト（バイラテラル重み関数の境界＋WGSL naga 検証）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    // WGSL shadow_mask_bilateral.wgsl の名前付き定数と一致（重み関数の CPU 参照実装のため）。
    const BILATERAL_SIGMA_FRAC: f32 = 0.5;
    const BILATERAL_DEPTH_TOL_FRAC: f32 = 0.01;
    const BILATERAL_DEPTH_TOL_MIN: f32 = 0.05;

    /// WGSL blur_cs のタップ重みと同式の CPU 参照。
    ///   ws = exp(-k^2 / 2σ^2), wd = exp(-|dz - d_center| / tol), w = ws * wd。
    /// σ = radius * BILATERAL_SIGMA_FRAC、tol は中心深度に対する相対値（下限つき）。
    fn bilateral_weight(k: i32, radius: i32, d_center: f32, d_sample: f32) -> f32 {
        let sigma = (radius as f32 * BILATERAL_SIGMA_FRAC).max(1e-4);
        let ws = (-(k as f32) * (k as f32) / (2.0 * sigma * sigma)).exp();
        let tol = BILATERAL_DEPTH_TOL_FRAC * d_center.abs().max(BILATERAL_DEPTH_TOL_MIN);
        let wd = (-(d_sample - d_center).abs() / tol).exp();
        ws * wd
    }

    /// 中心タップ（k=0・同深度）は常に重み 1（空間・深度とも exp(0)）。ゼロ除算不能の根拠。
    #[test]
    fn center_tap_weight_is_one() {
        for &r in &[1, 3, 5] {
            for &d in &[0.01f32, 1.0, 100.0, -50.0] {
                let w = bilateral_weight(0, r, d, d);
                assert!(
                    (w - 1.0).abs() < 1e-6,
                    "中心タップ重みは 1 のはず: r={r} d={d} w={w}"
                );
            }
        }
    }

    /// 深度が同一なら重みは純粋な空間ガウスに一致する（深度項 wd=1）。
    #[test]
    fn same_depth_reduces_to_gaussian() {
        let r = 3;
        let d = 5.0;
        let sigma = r as f32 * BILATERAL_SIGMA_FRAC;
        for k in -r..=r {
            let w = bilateral_weight(k, r, d, d);
            let gauss = (-(k as f32) * (k as f32) / (2.0 * sigma * sigma)).exp();
            assert!(
                (w - gauss).abs() < 1e-6,
                "同深度ではガウスに一致: k={k} w={w} gauss={gauss}"
            );
        }
    }

    /// 深度が大きく乖離した非中心タップは重みがほぼ 0（中心のみ生き残る＝エッジ保持）。
    #[test]
    fn distant_depth_collapses_to_center() {
        let r = 3;
        let d_center = 10.0;
        // 中心から相対 100 倍の深度差（tol の遥か外）。
        let d_far = d_center + 50.0;
        for k in 1..=r {
            let w = bilateral_weight(k, r, d_center, d_far);
            assert!(w < 1e-6, "深度乖離タップは棄却されるはず: k={k} w={w}");
        }
        // 一方、中心タップ（同深度）は 1 のまま生き残る。
        assert!((bilateral_weight(0, r, d_center, d_center) - 1.0).abs() < 1e-6);
    }

    /// ShadowMaskBilateralParams は 32 バイト（WGSL BilateralParams と一致）。
    #[test]
    fn bilateral_params_is_32_bytes() {
        assert_eq!(std::mem::size_of::<ShadowMaskBilateralParams>(), 32);
    }

    // ── テンポラル EMA ───────────────────────────────────────

    /// WGSL の EMA 混合と同式の CPU 参照（out = mix(history, current, α)、深度不一致なら α=1）。
    fn ema_mix(history: f32, current: f32, d_hist: f32, d_cur: f32, alpha: f32) -> f32 {
        // WGSL 側 EMA_DEPTH_TOL_FRAC / EMA_DEPTH_TOL_MIN と一致。
        const EMA_DEPTH_TOL_FRAC: f32 = 0.01;
        const EMA_DEPTH_TOL_MIN: f32 = 0.05;
        let tol = EMA_DEPTH_TOL_FRAC * d_cur.abs().max(EMA_DEPTH_TOL_MIN);
        let a = if (d_hist - d_cur).abs() <= tol {
            alpha
        } else {
            1.0
        };
        history + (current - history) * a
    }

    /// α=1（履歴無効）は現在値をそのまま返す＝EMA 導入前と完全に同一出力。
    #[test]
    fn ema_alpha_one_is_passthrough() {
        assert_eq!(ema_mix(0.0, 0.7, 5.0, 5.0, EMA_ALPHA_DISABLED), 0.7);
        assert_eq!(ema_params(false, 0.25), (EMA_ALPHA_DISABLED, 0));
    }

    /// 深度が一致していれば履歴と現在が α で混ざる（蓄積が効く条件）。
    #[test]
    fn ema_blends_when_depth_matches() {
        // 同深度・α=0.25 → 0.0 と 1.0 の混合は 0.25。
        let v = ema_mix(0.0, 1.0, 10.0, 10.0, 0.25);
        assert!((v - 0.25).abs() < 1e-6, "混合値が想定外: {v}");
        // 許容内（10.0 に対し 1% = 0.1）の微小な深度差なら混合される。
        let v2 = ema_mix(0.0, 1.0, 10.05, 10.0, 0.25);
        assert!(
            (v2 - 0.25).abs() < 1e-6,
            "許容内の深度差は混合されるはず: {v2}"
        );
    }

    /// 深度が許容を超えて食い違えば履歴を棄却し、現在値をそのまま採用する（ゴースト防止）。
    #[test]
    fn ema_rejects_history_on_depth_mismatch() {
        // 10.0 に対し 1% = 0.1 を超える差（シルエットを跨いだ画素）。
        let v = ema_mix(0.0, 1.0, 10.5, 10.0, 0.25);
        assert_eq!(v, 1.0, "深度不一致では現在値をそのまま採用するはず");
        // 履歴テクスチャのゼロクリア状態（.a=0）も必ず棄却される（初回フレームの自己修復）。
        let v0 = ema_mix(0.0, 1.0, 0.0, 10.0, 0.25);
        assert_eq!(v0, 1.0, "ゼロクリア履歴は棄却されるはず");
    }

    /// ema_params は履歴有効時に α をクランプしつつフラグを立てる。
    #[test]
    fn ema_params_clamps_alpha() {
        let (a, e) = ema_params(true, 0.25);
        assert_eq!((a, e), (0.25, 1));
        // 1.0 超は 1.0 に、0 以下は正の最小値にクランプ（履歴固着の防止）。
        assert_eq!(ema_params(true, 2.0).0, 1.0);
        assert!(ema_params(true, 0.0).0 > 0.0);
    }

    /// **GPU 実機**でバイラテラル＋EMA のコンピュートパイプラインが生成でき、H→V の 2 パスが
    /// 記録・実行できること（`--ignored` で実行する検証用）。
    ///
    /// naga の静的検証は WGSL 単体の妥当性しか見ない。ここで潰したい失敗要因は実機でしか出ない:
    ///   ① BGL と WGSL の binding 不一致（EMA で binding3=hist を増やしたため）
    ///   ② params UBO のサイズ／レイアウト不一致（16B→32B へ拡張したため）
    ///   ③ 同一パスで src と hist に**同じビュー**を bind する H パス（読み取り 2 重）が合法であること
    ///   ④ V パスで dst（storage write）と hist（sampled）が別テクスチャ＝ping-pong 成立
    ///
    /// 実行: `cargo test shadow_mask_bilateral::tests::bilateral_pipeline_builds_on_gpu -- --ignored --nocapture`
    #[test]
    #[ignore = "実 GPU が必要。--ignored で実行する"]
    fn bilateral_pipeline_builds_on_gpu() {
        // 他の GPU テストとグローバル設定を奪い合わないよう直列化する（本テストは
        // グローバルを書き換えないが、GPU の同時掴みを避ける流儀に合わせる）。
        let _guard = super::super::GPU_TEST_LOCK
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
        let Ok(adapter) =
            pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions::default()))
        else {
            eprintln!("[shadow_mask_bilateral] GPU アダプタが見つからないため検証をスキップ");
            return;
        };
        let (device, queue) = pollster::block_on(
            adapter.request_device(&wgpu::DeviceDescriptor::default()),
        )
        .expect("デバイス生成に失敗");

        // ① パイプライン生成（BGL と WGSL の binding/レイアウト不一致はここで落ちる）。
        let bilateral = ShadowMaskBilateral::new(&device, None);

        // 半解像度相当の小さなテクスチャ 4 枚（src / tmp / dst / hist）。1 レイヤぶんの 2D。
        const W: u32 = 8;
        const H: u32 = 8;
        let make = |label: &str, usage: wgpu::TextureUsages| -> wgpu::TextureView {
            device
                .create_texture(&wgpu::TextureDescriptor {
                    label: Some(label),
                    size: wgpu::Extent3d {
                        width: W,
                        height: H,
                        depth_or_array_layers: 1,
                    },
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    format: IMOS_BLUR_FORMAT,
                    usage,
                    view_formats: &[],
                })
                .create_view(&wgpu::TextureViewDescriptor::default())
        };
        let storage = wgpu::TextureUsages::STORAGE_BINDING | wgpu::TextureUsages::TEXTURE_BINDING;
        let src = make("bilateral_test_src", wgpu::TextureUsages::TEXTURE_BINDING);
        let tmp = make("bilateral_test_tmp", storage);
        let dst = make("bilateral_test_dst", storage);
        let hist = make("bilateral_test_hist", storage);

        // ② EMA 有効（V パスで hist を読む）の 1 レイヤぶんを記録して実行する。
        let (alpha, enable) = ema_params(true, 0.25);
        let mut encoder =
            device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
        bilateral.record_layer(
            &device,
            &mut encoder,
            &src,
            &tmp,
            &dst,
            &hist,
            W as i32,
            H as i32,
            3,
            alpha,
            enable,
        );
        queue.submit(Some(encoder.finish()));
        // 実行完了まで待つ（検証エラーがあればここまでに uncaptured error として上がる）。
        let _ = device.poll(wgpu::PollType::Wait);

        // ③ EMA 無効（履歴なし＝カメラが動いたフレーム相当）も同じ経路で通ること。
        let (alpha0, enable0) = ema_params(false, 0.25);
        let mut encoder2 =
            device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
        bilateral.record_layer(
            &device,
            &mut encoder2,
            &src,
            &tmp,
            &dst,
            &hist,
            W as i32,
            H as i32,
            3,
            alpha0,
            enable0,
        );
        queue.submit(Some(encoder2.finish()));
        let _ = device.poll(wgpu::PollType::Wait);
        println!("[shadow_mask_bilateral] GPU パイプライン生成＋H/V 2 パス実行に成功");
    }

    /// WGSL の EMA 定数・分岐が CPU 参照と一致すること（値ズレ／削除の回帰ガード）。
    #[test]
    fn wgsl_ema_constants_match_cpu_reference() {
        let src = include_str!("shaders/shadow_mask_bilateral.wgsl");
        assert!(
            src.contains("const EMA_DEPTH_TOL_FRAC: f32 = 0.01;"),
            "EMA_DEPTH_TOL_FRAC が CPU 参照(0.01)と一致しません"
        );
        assert!(
            src.contains("const EMA_DEPTH_TOL_MIN: f32 = 0.05;"),
            "EMA_DEPTH_TOL_MIN が CPU 参照(0.05)と一致しません"
        );
        // hist binding と EMA 分岐が生きていること（誤って削られたら検出する）。
        assert!(src.contains("@group(0) @binding(3) var hist: texture_2d<f32>;"));
        assert!(src.contains("if (P.ema_enable == 1u) {"));
    }

    /// shadow_mask_bilateral.wgsl を naga で parse + validate する（RAY_QUERY 不要）。
    #[test]
    fn bilateral_wgsl_parses_and_validates() {
        let src = include_str!("shaders/shadow_mask_bilateral.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[shadow_mask_bilateral] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[shadow_mask_bilateral] WGSL validate 失敗: {e:?}"));
    }

    /// WGSL 側の名前付き定数が CPU 参照と一致すること（値ズレ防止の回帰ガード）。
    #[test]
    fn wgsl_constants_match_cpu_reference() {
        let src = include_str!("shaders/shadow_mask_bilateral.wgsl");
        let parse_f32 = |name: &str| -> f32 {
            let decl = src
                .lines()
                .map(str::trim)
                .find(|l| l.starts_with(&format!("const {name}")))
                .unwrap_or_else(|| panic!("WGSL に const {name} が見つかりません"));
            decl.split('=')
                .nth(1)
                .unwrap()
                .trim()
                .trim_end_matches(';')
                .trim()
                .parse::<f32>()
                .unwrap_or_else(|_| panic!("const {name} が f32 として解釈できません"))
        };
        assert_eq!(parse_f32("BILATERAL_SIGMA_FRAC"), BILATERAL_SIGMA_FRAC);
        assert_eq!(
            parse_f32("BILATERAL_DEPTH_TOL_FRAC"),
            BILATERAL_DEPTH_TOL_FRAC
        );
        assert_eq!(
            parse_f32("BILATERAL_DEPTH_TOL_MIN"),
            BILATERAL_DEPTH_TOL_MIN
        );
    }
}
