// ============================================================
//  refract_pyramid.rs — すりガラス用の屈折背景ミップチェーン（ガラス表現）
//
//  ## 役割（単一責任）
//  半透明フォワードパスがサンプルする「不透明シーン HDR のコピー（屈折の背景）」を、
//  ミップチェーン付きテクスチャとして保持し、下位ミップほど強くぼかす。
//  roughness からミップレベルを選んでサンプルすることで「すりガラス」を表現する
//  （refract_common.wgsl の refract_background が textureSampleLevel でミップを選ぶ）。
//
//  ## ミップ生成（ダウンサンプル → いもす法ブラーの反復）
//  mip0 = 不透明シーン HDR のコピー（シャープ）。
//  mip m (1..N) = mip(m-1) を 1/2 にダウンサンプル（ブルームと同じ 13-tap）してから
//                 いもす法ボックスブラー（imos_blur.rs）でぼかしたもの。
//  各ミップは半解像度ずつ小さくなり、かつブラーが累積するため下位ほど強くぼける。
//
//  ## テクスチャ所有（RtPool を使わない理由）
//  いもすブラーの ping-pong は STORAGE_BINDING を要し、RtPool は
//  RENDER_ATTACHMENT|TEXTURE_BINDING 固定のため使えない（ao.rs と同じ理由）。
//  よって本構造体がミップチェーン本体とスクラッチを専有し、サイズ追従（ensure）も自前で行う。
//
//  ## スクラッチの構成（ao.rs の raw/a/b を踏襲＝実績のある usage 分離）
//  各ブラー対象ミップ m につき 3 枚:
//    down[m] : ダウンサンプル出力（RENDER_ATTACHMENT|TEXTURE_BINDING）
//    a[m]    : いもす ping-pong その1（STORAGE_BINDING|TEXTURE_BINDING）
//    b[m]    : いもす ping-pong その2（STORAGE|TEXTURE|COPY_SRC）。結果は必ず b に残る。
//  b[m] を copy_texture_to_texture でミップチェーンの mip m へ書き戻す。
// ============================================================

use super::HDR_FORMAT;
use super::imos_blur::{IMOS_BLUR_FORMAT, ImosBlur};
use super::post::{PostPipeline, run_post_stage};

/// 屈折背景ミップチェーンのミップ数（mip0 含む）。
/// refract_common.wgsl の REFRACT_MAX_MIP（= REFRACT_MIP_COUNT - 1）と一致させること。
/// 5 = mip0（シャープ）＋ ぼかしミップ 4 段。roughness 1.0 で最深ミップ（最大ぼかし）へ届く。
pub const REFRACT_MIP_COUNT: u32 = 5;

/// 各ミップのいもす法ボックスブラー半径（そのミップの画素単位）。
/// 半径非依存 O(n) のため小さめでも累積で十分ぼける（ダウンサンプルと合わせて広がる）。
const REFRACT_BLUR_RADIUS: i32 = 2;
/// いもす法の反復回数（H→V を 1 反復。3 回でガウシアン近似だが、ミップ累積があるため 2 で足りる）。
const REFRACT_BLUR_ITERATIONS: u32 = 2;

/// ミップチェーンのフォーマット。HDR（scene_hdr）のコピー元と揃える。
/// いもすの storage フォーマット（Rgba16Float）とも一致必須（imos が storage 書き込みするため）。
const PYRAMID_FORMAT: wgpu::TextureFormat = HDR_FORMAT;

/// 1 ミップぶんのブラー用スクラッチ 3 枚（ao.rs の raw/a/b を踏襲）。
struct MipScratch {
    /// ダウンサンプル出力（RENDER_ATTACHMENT|TEXTURE_BINDING）。いもす src。
    down: wgpu::TextureView,
    /// いもす ping-pong その1（STORAGE|TEXTURE）。
    a: wgpu::TextureView,
    /// いもす ping-pong その2（STORAGE|TEXTURE|COPY_SRC）。結果はここに残り、mip へコピーする。
    b_tex: wgpu::Texture,
    b: wgpu::TextureView,
    /// このミップの画素サイズ。
    w: u32,
    h: u32,
}

/// 生成パイプライン一式（ダウンサンプル＋いもす）。初回 ensure で遅延構築する
/// （App::new が device 無しでフィールドを初期化できるよう、AoTargets と同じく遅延確保にする）。
struct PyramidPipes {
    /// ダウンサンプル（13-tap, ブルームと共用の post_bloom_down）パイプライン。
    down_pipeline: PostPipeline,
    /// ダウンサンプル入力のサンプラー（線形・ClampToEdge）。
    down_sampler: wgpu::Sampler,
    /// run_post_stage のマスク引数用ダミー（1x1。down は 2 グループでマスク不使用だが引数に必要）。
    white_view: wgpu::TextureView,
    /// いもす法ボックスブラー。
    imos: ImosBlur,
}

impl PyramidPipes {
    fn new(device: &wgpu::Device) -> Self {
        // ダウンサンプルはブルームの 13-tap を共用（出力 HDR）。自己完結のリゾルバを渡す。
        // パイプラインキャッシュは遅延構築のため None（一度きりの構築コストのみ）。
        let down_pipeline = PostPipeline::from_toml(
            device,
            include_str!("pipelines/post_bloom_down.toml"),
            PYRAMID_FORMAT,
            None,
            |name: &str| -> &'static str {
                match name {
                    "fullscreen.wgsl" => include_str!("shaders/fullscreen.wgsl"),
                    "post_bloom_down.wgsl" => include_str!("shaders/post_bloom_down.wgsl"),
                    other => panic!("refract_pyramid: unknown shader source: {other}"),
                }
            },
        );
        let down_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Refract Pyramid Downsample Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });
        // マスク引数用の白 1x1（down は group2 を持たないため実際にはバインドされない）。
        let white_tex = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Refract Pyramid White 1x1"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: PYRAMID_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let white_view = white_tex.create_view(&wgpu::TextureViewDescriptor::default());
        Self {
            down_pipeline,
            down_sampler,
            white_view,
            imos: ImosBlur::new(device, None),
        }
    }
}

/// すりガラス用の屈折背景ミップチェーンと生成パイプライン一式。
pub struct RefractPyramid {
    /// ミップチェーン本体（TEXTURE_BINDING|COPY_DST）。binding15 のサンプル対象。
    tex: Option<wgpu::Texture>,
    /// 全ミップを含むサンプル用ビュー（binding15 に差す）。
    full_view: Option<wgpu::TextureView>,
    /// ミップ m-1 をダウンサンプル入力として読むための単一ミップビュー（[0]=mip0, [1]=mip1, ...）。
    mip_input_views: Vec<wgpu::TextureView>,
    /// ブラー対象ミップ（1..N）ごとのスクラッチ 3 枚（添字 = mip-1）。
    scratch: Vec<MipScratch>,
    width: u32,
    height: u32,
    /// 生成パイプライン（初回 ensure で遅延構築）。
    pipes: Option<PyramidPipes>,
}

impl Default for RefractPyramid {
    fn default() -> Self {
        Self::new()
    }
}

impl RefractPyramid {
    /// 空の状態で生成する（device 不要。パイプライン・テクスチャは ensure で遅延確保）。
    pub fn new() -> Self {
        Self {
            tex: None,
            full_view: None,
            mip_input_views: Vec::new(),
            scratch: Vec::new(),
            width: 0,
            height: 0,
            pipes: None,
        }
    }

    /// サーフェスサイズ (w,h) に追従してミップチェーンとスクラッチを確保する。
    /// 初回はパイプラインも構築する。既存が同サイズなら何もしない（フレーム跨ぎで再利用）。
    pub fn ensure(&mut self, device: &wgpu::Device, w: u32, h: u32) {
        if self.pipes.is_none() {
            self.pipes = Some(PyramidPipes::new(device));
        }
        let w = w.max(1);
        let h = h.max(1);
        if self.tex.is_some() && self.width == w && self.height == h {
            return;
        }

        // ── ミップチェーン本体（mip0 は copy_dst、以降も copy_dst で書き戻す）──
        let mip_count = REFRACT_MIP_COUNT.min(max_mips(w, h));
        let tex = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Refract Pyramid"),
            size: wgpu::Extent3d {
                width: w,
                height: h,
                depth_or_array_layers: 1,
            },
            mip_level_count: mip_count,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: PYRAMID_FORMAT,
            // サンプル（binding15）＋ mip0/各ミップへの copy 書き込み。
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        // 全ミップを含むサンプルビュー（binding15）。
        let full_view = tex.create_view(&wgpu::TextureViewDescriptor {
            label: Some("Refract Pyramid Full View"),
            ..Default::default()
        });
        // ミップ m を入力として読むための単一ミップビュー。
        let mip_input_views: Vec<wgpu::TextureView> = (0..mip_count)
            .map(|m| {
                tex.create_view(&wgpu::TextureViewDescriptor {
                    label: Some("Refract Pyramid Mip Input"),
                    base_mip_level: m,
                    mip_level_count: Some(1),
                    ..Default::default()
                })
            })
            .collect();

        // ── ブラー対象ミップ（1..mip_count）ごとのスクラッチ 3 枚 ──
        let storage = wgpu::TextureUsages::STORAGE_BINDING | wgpu::TextureUsages::TEXTURE_BINDING;
        let mut scratch: Vec<MipScratch> = Vec::new();
        for m in 1..mip_count {
            let mw = (w >> m).max(1);
            let mh = (h >> m).max(1);
            let down = make_tex(
                device,
                "refract_down",
                mw,
                mh,
                wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
            )
            .1;
            let a = make_tex(device, "refract_a", mw, mh, storage).1;
            let (b_tex, b) = make_tex(
                device,
                "refract_b",
                mw,
                mh,
                storage | wgpu::TextureUsages::COPY_SRC,
            );
            scratch.push(MipScratch {
                down,
                a,
                b_tex,
                b,
                w: mw,
                h: mh,
            });
        }

        self.tex = Some(tex);
        self.full_view = Some(full_view);
        self.mip_input_views = mip_input_views;
        self.scratch = scratch;
        self.width = w;
        self.height = h;
    }

    /// binding15 に差すサンプルビュー（全ミップ）。ensure 済みであること。
    pub fn full_view(&self) -> &wgpu::TextureView {
        self.full_view
            .as_ref()
            .expect("RefractPyramid: full_view 未確保（ensure を先に呼ぶこと）")
    }

    /// ミップチェーンを生成する（不透明ライティング完成後・半透明パス前に呼ぶ）。
    /// mip0 に scene_hdr をコピーし、以降のミップをダウンサンプル→いもすブラーで作る。
    pub fn record(
        &self,
        device: &wgpu::Device,
        encoder: &mut wgpu::CommandEncoder,
        scene_hdr_tex: &wgpu::Texture,
    ) {
        let Some(tex) = self.tex.as_ref() else {
            return;
        };
        let Some(pipes) = self.pipes.as_ref() else {
            return;
        };

        // mip0 = scene_hdr のコピー（シャープな屈折背景。従来の refract_bg と同一内容）。
        encoder.copy_texture_to_texture(
            wgpu::ImageCopyTexture {
                texture: scene_hdr_tex,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::ImageCopyTexture {
                texture: tex,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::Extent3d {
                width: self.width,
                height: self.height,
                depth_or_array_layers: 1,
            },
        );

        // 各ブラーミップ: ダウンサンプル → いもすブラー → mip へ書き戻し。
        for (idx, s) in self.scratch.iter().enumerate() {
            let m = idx + 1; // 生成先ミップ（1 起点）
            let in_view = &self.mip_input_views[m - 1]; // 入力＝1 段上のミップ（既に確定済み）
            let in_w = (self.width >> (m - 1)).max(1);
            let in_h = (self.height >> (m - 1)).max(1);

            // ① ダウンサンプル（13-tap）: mip(m-1) → down[m]。params は入力の 1 テクセルサイズ。
            let texel = [1.0f32 / in_w as f32, 1.0f32 / in_h as f32, 0.0f32, 0.0f32];
            run_post_stage(
                device,
                encoder,
                &pipes.down_pipeline,
                in_view,
                None,
                &pipes.white_view,
                &pipes.down_sampler,
                bytemuck::bytes_of(&texel),
                &s.down,
                "Refract Pyramid Downsample",
            );

            // ② いもす法ブラー: down[m] → (a,b の ping-pong)。結果は必ず b に残る。
            pipes.imos.record(
                device,
                encoder,
                &s.down,
                &s.a,
                &s.b,
                s.w as i32,
                s.h as i32,
                REFRACT_BLUR_RADIUS,
                REFRACT_BLUR_ITERATIONS,
            );

            // ③ b[m]（ぼかし結果）→ ミップチェーンの mip m へ書き戻す。
            encoder.copy_texture_to_texture(
                wgpu::ImageCopyTexture {
                    texture: &s.b_tex,
                    mip_level: 0,
                    origin: wgpu::Origin3d::ZERO,
                    aspect: wgpu::TextureAspect::All,
                },
                wgpu::ImageCopyTexture {
                    texture: tex,
                    mip_level: m as u32,
                    origin: wgpu::Origin3d::ZERO,
                    aspect: wgpu::TextureAspect::All,
                },
                wgpu::Extent3d {
                    width: s.w,
                    height: s.h,
                    depth_or_array_layers: 1,
                },
            );
        }
    }
}

/// (w,h) から作れる最大ミップ数（最小辺が 1 になるまで）。
fn max_mips(w: u32, h: u32) -> u32 {
    let mut n = 1u32;
    let mut d = w.min(h);
    while d > 1 {
        d >>= 1;
        n += 1;
    }
    n
}

/// スクラッチテクスチャ 1 枚を作る（ao.rs の make_tex 相当）。
fn make_tex(
    device: &wgpu::Device,
    label: &str,
    w: u32,
    h: u32,
    usage: wgpu::TextureUsages,
) -> (wgpu::Texture, wgpu::TextureView) {
    let tex = device.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d {
            width: w.max(1),
            height: h.max(1),
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: IMOS_BLUR_FORMAT,
        usage,
        view_formats: &[],
    });
    let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
    (tex, view)
}

// ============================================================
//  テスト（定数整合）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// Rust の REFRACT_MIP_COUNT と WGSL refract_common.wgsl の REFRACT_MAX_MIP の整合。
    /// WGSL 側は `const REFRACT_MAX_MIP: f32 = 4.0;`（= REFRACT_MIP_COUNT - 1）で宣言している。
    /// ズレると roughness→ミップのマッピングがチェーン深度と食い違う（範囲外は textureSampleLevel が
    /// クランプするため破綻はしないが、意図した最大ぼかしに届かない）。
    #[test]
    fn wgsl_max_mip_matches_rust_mip_count() {
        let src = include_str!("shaders/refract_common.wgsl");
        // "const REFRACT_MAX_MIP: f32 = 4.0;" の数値を抜き出して (MIP_COUNT-1) と照合する。
        let needle = "REFRACT_MAX_MIP: f32 =";
        let pos = src
            .find(needle)
            .expect("REFRACT_MAX_MIP 宣言が見つからない");
        let tail = &src[pos + needle.len()..];
        let num: String = tail
            .trim_start()
            .chars()
            .take_while(|c| c.is_ascii_digit() || *c == '.')
            .collect();
        let wgsl_max_mip: f32 = num.parse().expect("REFRACT_MAX_MIP の数値パースに失敗");
        assert_eq!(
            wgsl_max_mip as u32,
            REFRACT_MIP_COUNT - 1,
            "WGSL REFRACT_MAX_MIP は Rust REFRACT_MIP_COUNT-1 と一致すること",
        );
    }

    /// max_mips が最小辺基準で正しい段数を返す。
    #[test]
    fn max_mips_counts_to_one_pixel() {
        assert_eq!(max_mips(1, 1), 1);
        assert_eq!(max_mips(2, 2), 2);
        assert_eq!(max_mips(16, 16), 5);
        assert_eq!(max_mips(1920, 1080), 11); // 最小辺 1080 → 1024..1 で 11 段
    }
}
