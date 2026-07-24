// ============================================================
//  bloom.rs — ブルーム（しきい値→ダウンサンプル→アップサンプル加算合成）
//
//  Phase R4。R3 のポスト土台（RtPool＋PostPipeline＋run_post_stage）に載せる。
//
//  構成（デュアルフィルタ系）:
//    1. プレフィルタ : シーン HDR → bloom[0]（半解像度）。ソフトニーしきい値抽出。
//    2. ダウンサンプル: bloom[i-1] → bloom[i]（各 1/2, 13-tap）。段数は解像度から算出。
//    3. アップサンプル: bloom[i+1] を 3x3 テントで拡大し bloom[i] へ加算合成。
//    4. 合成         : bloom[0] を intensity 倍してシーン HDR へ加算合成。
//
//  中間 RT はすべて RtPool から名前（"bloom_0".."bloom_N"）で確保する。
//  加算合成は blend="Additive"（post_bloom_up.toml）＋ LoadOp::Load で行う。
// ============================================================

use super::post_pass::{PostPipeline, run_post_stage, run_post_stage_load};
use super::rt_pool::RtPool;

// ─── 定数（マジックナンバー回避）──────────────────────────────
/// ブルーム mip の最大段数（解像度が高くてもこれで頭打ちにする）。
pub const MAX_BLOOM_MIPS: usize = 6;
/// mip を刻む下限サイズ（この px 未満になる手前で段数を止める）。
const MIN_BLOOM_MIP_SIZE: u32 = 8;
/// bloom mip の RtPool 名（段数ぶん静的に用意。MAX_BLOOM_MIPS と一致させること）。
const BLOOM_RT_NAMES: [&str; MAX_BLOOM_MIPS] = [
    "bloom_0", "bloom_1", "bloom_2", "bloom_3", "bloom_4", "bloom_5",
];

// ─── パラメータ UBO（WGSL 側 struct と #[repr(C)] 一致）──────────

/// プレフィルタパラメータ（post_bloom_prefilter.wgsl BloomPrefilterParams と一致）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
struct BloomPrefilterParams {
    threshold: f32,
    knee: f32,
    _pad0: f32,
    _pad1: f32,
}

/// ダウンサンプルパラメータ（post_bloom_down.wgsl BloomDownParams と一致）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
struct BloomDownParams {
    /// 入力テクスチャの 1 テクセルサイズ（1/幅, 1/高さ）。
    texel: [f32; 2],
    _pad0: f32,
    _pad1: f32,
}

/// アップサンプルパラメータ（post_bloom_up.wgsl BloomUpParams と一致）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
struct BloomUpParams {
    /// 入力テクスチャの 1 テクセルサイズ（1/幅, 1/高さ）。
    texel: [f32; 2],
    /// 加算合成スケール（中間アップ=1.0, 最終合成=intensity）。
    scale: f32,
    _pad: f32,
}

/// ブルーム 1 フレームぶんの設定（PostFxSettings から抜き出したもの）。
#[derive(Copy, Clone, Debug)]
pub struct BloomParams {
    /// 抽出しきい値。
    pub threshold: f32,
    /// ソフトニー幅係数（0..1）。
    pub knee: f32,
    /// 合成強度。
    pub intensity: f32,
}

// ============================================================
//  BloomPipelines — ブルームの静的パイプライン一式
// ============================================================

/// ブルームの 3 パイプライン（プレフィルタ・ダウン・アップ）。すべて HDR 出力。
pub struct BloomPipelines {
    prefilter: PostPipeline,
    down: PostPipeline,
    up: PostPipeline,
}

impl BloomPipelines {
    /// ブルームパイプラインを構築する。出力はすべて `hdr_format`（Rgba16Float）。
    pub fn new<F: Fn(&str) -> &'static str>(
        device: &wgpu::Device,
        hdr_format: wgpu::TextureFormat,
        cache: Option<&wgpu::PipelineCache>,
        resolve: F,
    ) -> Self {
        let prefilter = PostPipeline::from_toml(
            device,
            include_str!("../pipelines/post_bloom_prefilter.toml"),
            hdr_format,
            cache,
            &resolve,
        );
        let down = PostPipeline::from_toml(
            device,
            include_str!("../pipelines/post_bloom_down.toml"),
            hdr_format,
            cache,
            &resolve,
        );
        let up = PostPipeline::from_toml(
            device,
            include_str!("../pipelines/post_bloom_up.toml"),
            hdr_format,
            cache,
            &resolve,
        );
        Self {
            prefilter,
            down,
            up,
        }
    }

    /// ブルーム mip のサイズ計画を返す（level0 = 半解像度から 1/2 ずつ）。
    ///
    /// `MIN_BLOOM_MIP_SIZE` 未満になる手前・`MAX_BLOOM_MIPS` 段で打ち切る。
    /// 少なくとも 1 段は返す（極小解像度でも破綻しないよう 1 にクランプ）。
    pub fn mip_plan(surf_w: u32, surf_h: u32) -> Vec<(u32, u32)> {
        let mut mips = Vec::new();
        let mut w = (surf_w / 2).max(1);
        let mut h = (surf_h / 2).max(1);
        for _ in 0..MAX_BLOOM_MIPS {
            mips.push((w, h));
            let nw = w / 2;
            let nh = h / 2;
            if nw < MIN_BLOOM_MIP_SIZE || nh < MIN_BLOOM_MIP_SIZE {
                break;
            }
            w = nw;
            h = nh;
        }
        mips
    }

    /// ブルーム用中間 RT を RtPool に確保する（`record` の前に呼ぶこと）。
    ///
    /// 返り値は各 mip の (RtPool 名, 幅, 高さ)。`record` へそのまま渡す。
    pub fn ensure_targets(
        rt_pool: &mut RtPool,
        device: &wgpu::Device,
        hdr_format: wgpu::TextureFormat,
        surf_w: u32,
        surf_h: u32,
    ) -> Vec<(&'static str, u32, u32)> {
        let plan = Self::mip_plan(surf_w, surf_h);
        let mut targets = Vec::with_capacity(plan.len());
        for (i, (w, h)) in plan.into_iter().enumerate() {
            let name = BLOOM_RT_NAMES[i];
            rt_pool.ensure(device, name, w, h, hdr_format);
            targets.push((name, w, h));
        }
        targets
    }

    /// ブルーム一式を記録し、結果を `scene_hdr` へ加算合成する。
    ///
    /// - `targets`  : `ensure_targets` の返り値（確保済み mip 名＋サイズ）。
    /// - `rt_pool`  : `targets` の RT を確保済みのプール（ビュー取得に使う）。
    /// - `scene_hdr`: シーン HDR ビュー（プレフィルタ入力かつ最終合成先）。
    /// - `sampler` / `white`: run_post_stage 共通（リニアサンプラー／既定マスク）。
    #[allow(clippy::too_many_arguments)]
    pub fn record(
        &self,
        device: &wgpu::Device,
        encoder: &mut wgpu::CommandEncoder,
        rt_pool: &RtPool,
        targets: &[(&'static str, u32, u32)],
        scene_hdr: &wgpu::TextureView,
        sampler: &wgpu::Sampler,
        white: &wgpu::TextureView,
        params: BloomParams,
    ) {
        // 段数 0（あり得ないが保険）は何もしない。
        let n = targets.len();
        if n == 0 {
            return;
        }

        // ── 1. プレフィルタ: scene_hdr → bloom[0]（Clear）─────────
        let pf = BloomPrefilterParams {
            threshold: params.threshold,
            knee: params.knee,
            _pad0: 0.0,
            _pad1: 0.0,
        };
        run_post_stage(
            device,
            encoder,
            &self.prefilter,
            scene_hdr,
            None,
            white,
            sampler,
            bytemuck::bytes_of(&pf),
            rt_pool.view(targets[0].0),
            "Bloom Prefilter",
        );

        // ── 2. ダウンサンプル: bloom[i-1] → bloom[i]（Clear）──────
        for i in 1..n {
            let (src_name, src_w, src_h) = targets[i - 1];
            let dn = BloomDownParams {
                texel: [1.0 / src_w as f32, 1.0 / src_h as f32],
                _pad0: 0.0,
                _pad1: 0.0,
            };
            run_post_stage(
                device,
                encoder,
                &self.down,
                rt_pool.view(src_name),
                None,
                white,
                sampler,
                bytemuck::bytes_of(&dn),
                rt_pool.view(targets[i].0),
                "Bloom Downsample",
            );
        }

        // ── 3. アップサンプル: bloom[i+1] を bloom[i] へ加算（Load+Additive）
        //    小さい mip から大きい mip へテント拡大して積み上げる。
        for i in (0..n - 1).rev() {
            let (src_name, src_w, src_h) = targets[i + 1];
            let up = BloomUpParams {
                texel: [1.0 / src_w as f32, 1.0 / src_h as f32],
                scale: 1.0,
                _pad: 0.0,
            };
            run_post_stage_load(
                device,
                encoder,
                &self.up,
                rt_pool.view(src_name),
                None,
                white,
                sampler,
                bytemuck::bytes_of(&up),
                rt_pool.view(targets[i].0),
                "Bloom Upsample",
                true,
            );
        }

        // ── 4. 合成: bloom[0] を intensity 倍して scene_hdr へ加算（Load+Additive）
        let (b0_name, b0_w, b0_h) = targets[0];
        // scene_hdr のフル解像度基準ではなく bloom[0] のテクセルでテント拡大する。
        let comp = BloomUpParams {
            texel: [1.0 / b0_w as f32, 1.0 / b0_h as f32],
            scale: params.intensity,
            _pad: 0.0,
        };
        run_post_stage_load(
            device,
            encoder,
            &self.up,
            rt_pool.view(b0_name),
            None,
            white,
            sampler,
            bytemuck::bytes_of(&comp),
            scene_hdr,
            "Bloom Composite",
            true,
        );
    }
}
