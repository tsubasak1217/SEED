// ============================================================
//  postfx/bake.rs — テクスチャ単位ポストの焼き込み＋キャッシュ
//
//  ベーススプライトテクスチャ（sRGB）に .postfx のエフェクトチェーンを適用し、
//  専用の出力テクスチャ（リニア HDR = Rgba16Float）へ焼き込む。焼き上げた
//  テクスチャはスプライト用 tex_bgl でサンプル可能な `GpuSpriteTexture` として返し、
//  既存のスプライト描画経路（batch2d）はそのまま流用する（描画コード無変更）。
//
//  【キャッシュ】
//  キー = (texture_path, postfx_path)。値に .postfx の mtime を持ち、元テクスチャ・
//  .postfx が不変なら 1 回だけ焼いて使い回す。.postfx が更新（mtime 変化）されたら
//  アセットを reload して焼き直す。`every_frame=true` の .postfx は毎回焼き直す。
//
//  【焼き込み専用エンコーダ】
//  焼き込みはフレームのメインエンコーダとは独立した専用エンコーダを生成して即 submit
//  する（キャッシュ前提で頻度が低いため）。これによりスプライト収集（collect）から
//  `&DrawContext` だけで焼き込みを完結でき、frame_renderer への割り込みが不要になる。
// ============================================================

use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::Arc;

use wgpu::util::DeviceExt;

use crate::engine::core::renderer::post::{VignetteParams, run_post_stage};
use crate::engine::methods::drawer::{DrawContext, GpuSpriteTexture, load_sprite_texture};

use super::asset::{PostfxAsset, PostfxEffect};
use super::{BlurParams, PostfxContext, TintParams, WORK_FORMAT};

// ============================================================
//  キャッシュ
// ============================================================

/// 焼き込み済みテクスチャ 1 件のキャッシュエントリ。
struct CacheEntry {
    /// 焼き上げたテクスチャ（None = エフェクト空 or 焼き込み不能 → 呼び出し側は元を使う）。
    tex: Option<Arc<GpuSpriteTexture>>,
    /// 焼いた時点の .postfx ファイル mtime（変化で焼き直し）。
    mtime: u64,
}

/// スプライトのポストエフェクト焼き込みキャッシュ（DrawContext が 1 個保持）。
///
/// キーは (texture_path, postfx_path)。`&DrawContext` 共有下でフレーム内から
/// 焼き込み・参照できるよう内部可変（RefCell）で包む。
pub struct SpritePostfxCache {
    map: RefCell<HashMap<(String, String), CacheEntry>>,
}

impl Default for SpritePostfxCache {
    fn default() -> Self {
        Self::new()
    }
}

impl SpritePostfxCache {
    /// 空のキャッシュを生成する。
    pub fn new() -> Self {
        Self {
            map: RefCell::new(HashMap::new()),
        }
    }

    /// 指定 .postfx パスに紐づく全エントリを破棄する（IPC でのパス変更・編集反映用）。
    pub fn invalidate_postfx(&self, postfx_path: &str) {
        self.map
            .borrow_mut()
            .retain(|(_, pfx), _| pfx != postfx_path);
    }

    /// キャッシュ全体を破棄する。
    #[allow(dead_code)]
    pub fn clear(&self) {
        self.map.borrow_mut().clear();
    }
}

// ============================================================
//  解決（キャッシュ参照 → 必要なら焼き込み）
// ============================================================

/// スプライトの (元テクスチャ, .postfx) からポスト焼き込み済みテクスチャを解決する。
///
/// - 返り値 `Some(tex)`: 焼き込み済みテクスチャ（これを描画に使う）。
/// - 返り値 `None`: エフェクト空・.postfx ロード失敗・焼き込み不能。呼び出し側は
///   元テクスチャをそのまま使う。
pub fn resolve_baked(
    draw_ctx: &DrawContext,
    base: &GpuSpriteTexture,
    texture_path: &str,
    postfx_path: &str,
) -> Option<Arc<GpuSpriteTexture>> {
    let key = (texture_path.to_string(), postfx_path.to_string());
    let mtime = crate::engine::asset_fs::mtime(postfx_path);

    // ── キャッシュ参照 ────────────────────────────────────
    // 既存エントリがあり、.postfx が unchanged（mtime 一致）かつ every_frame でなければ再利用。
    let cached_mtime = draw_ctx
        .sprite_postfx_cache
        .map
        .borrow()
        .get(&key)
        .map(|e| e.mtime);
    // .postfx（every_frame 判定に必要）をロードする（キャッシュ済みなら安価）。
    let asset = if cached_mtime.map(|cm| cm != mtime).unwrap_or(false) {
        // mtime が変わっていればファイル編集を反映するため reload する。
        super::asset::reload(postfx_path)
    } else {
        super::asset::load(postfx_path)
    }?;

    if let Some(cm) = cached_mtime {
        if cm == mtime && !asset.every_frame {
            return draw_ctx
                .sprite_postfx_cache
                .map
                .borrow()
                .get(&key)
                .and_then(|e| e.tex.clone());
        }
    }

    // ── 焼き込み ──────────────────────────────────────────
    let baked = bake(draw_ctx, base, &asset);
    draw_ctx.sprite_postfx_cache.map.borrow_mut().insert(
        key,
        CacheEntry {
            tex: baked.clone(),
            mtime,
        },
    );
    baked
}

// ============================================================
//  焼き込み本体
// ============================================================

/// 作業テクスチャ（リニア HDR, 描画/サンプル/storage の 3 用途）を確保する。
fn make_work(device: &wgpu::Device, w: u32, h: u32) -> (wgpu::Texture, wgpu::TextureView) {
    let tex = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("Postfx Work"),
        size: wgpu::Extent3d {
            width: w,
            height: h,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: WORK_FORMAT,
        // フラグメントパス出力（RENDER_ATTACHMENT）・次段入力（TEXTURE_BINDING）・
        // blur compute 出力（STORAGE_BINDING）の 3 用途を兼ねる。
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT
            | wgpu::TextureUsages::TEXTURE_BINDING
            | wgpu::TextureUsages::STORAGE_BINDING,
        view_formats: &[],
    });
    let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
    (tex, view)
}

/// エフェクトチェーンを焼き込み、出力を `GpuSpriteTexture` として返す。
///
/// エフェクトが空／サイズ 0 のときは `None`（呼び出し側は元テクスチャを使う）。
fn bake(
    draw_ctx: &DrawContext,
    base: &GpuSpriteTexture,
    asset: &PostfxAsset,
) -> Option<Arc<GpuSpriteTexture>> {
    if asset.effects.is_empty() {
        return None;
    }
    let (w, h) = (base.width, base.height);
    if w == 0 || h == 0 {
        return None;
    }

    let device = &draw_ctx.device;
    let queue = &draw_ctx.queue;
    let pfx: &PostfxContext = &draw_ctx.postfx;

    // チェーン用ピンポン 2 枚（Vec で保持し、最後に swap_remove で最終枚を取り出す）。
    let mut works: Vec<(wgpu::Texture, wgpu::TextureView)> =
        vec![make_work(device, w, h), make_work(device, w, h)];
    // blur がある場合のみ内部ピンポン用 temp を 2 枚確保する。
    let has_blur = asset
        .effects
        .iter()
        .any(|e| matches!(e, PostfxEffect::Blur { .. }));
    let temps: Vec<(wgpu::Texture, wgpu::TextureView)> = if has_blur {
        vec![make_work(device, w, h), make_work(device, w, h)]
    } else {
        Vec::new()
    };

    let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
        label: Some("Postfx Bake Encoder"),
    });

    // ── ingest: ベース sRGB テクスチャ → works[0]（リニア HDR）─────────
    // tint(白=恒等) で取り込む。以降のチェーンは全てリニア HDR で処理する。
    let identity = TintParams {
        color: [1.0, 1.0, 1.0, 1.0],
    };
    run_post_stage(
        device,
        &mut encoder,
        &pfx.tint,
        &base.view,
        None,
        &pfx.white_view,
        &pfx.sampler,
        bytemuck::bytes_of(&identity),
        &works[0].1,
        "Postfx Ingest",
    );
    let mut cur = 0usize;

    // ── エフェクトチェーン（前段出力 → 次段入力）─────────────────────
    for eff in &asset.effects {
        let dst = 1 - cur;
        match eff {
            PostfxEffect::Tint { color } => {
                let p = TintParams { color: *color };
                run_post_stage(
                    device,
                    &mut encoder,
                    &pfx.tint,
                    &works[cur].1,
                    None,
                    &pfx.white_view,
                    &pfx.sampler,
                    bytemuck::bytes_of(&p),
                    &works[dst].1,
                    "Postfx Tint",
                );
            }
            PostfxEffect::Vignette { strength, mask } => {
                // マスク指定時のみ都度ロード（.postfx はキャッシュ焼きのため頻度は低い）。
                let mask_tex = if mask.is_empty() {
                    None
                } else {
                    load_sprite_texture(
                        device,
                        queue,
                        mask,
                        &draw_ctx.pipelines.sprite.tex_bgl,
                        &draw_ctx.pipelines.sprite.sampler,
                    )
                };
                let mask_view = mask_tex.as_ref().map(|t| &t.view);
                // 既存 VignetteParams を流用（strength を intensity へ、形状は既定値）。
                let vp = VignetteParams {
                    intensity: *strength,
                    radius: 0.5,
                    softness: 0.5,
                    _pad: 0.0,
                };
                run_post_stage(
                    device,
                    &mut encoder,
                    &pfx.vignette,
                    &works[cur].1,
                    mask_view,
                    &pfx.white_view,
                    &pfx.sampler,
                    bytemuck::bytes_of(&vp),
                    &works[dst].1,
                    "Postfx Vignette",
                );
            }
            PostfxEffect::Blur { radius } => {
                run_blur(
                    device,
                    &mut encoder,
                    pfx,
                    &works[cur].1,
                    &works[dst].1,
                    &temps[0].1,
                    &temps[1].1,
                    w as i32,
                    h as i32,
                    radius.round() as i32,
                );
            }
        }
        cur = dst;
    }

    // ── 焼き込み実行（専用エンコーダを即 submit）──────────────────────
    queue.submit(std::iter::once(encoder.finish()));

    // 最終枚を取り出して GpuSpriteTexture へ包む（残りの作業/temp は drop）。
    let (final_tex, _final_view) = works.swap_remove(cur);
    Some(GpuSpriteTexture::from_texture(
        device,
        final_tex,
        &draw_ctx.pipelines.sprite.tex_bgl,
        &draw_ctx.pipelines.sprite.sampler,
        w,
        h,
    ))
}

// ============================================================
//  blur（いもす法ボックス 3 回近似ガウシアン）
// ============================================================

/// ボックスフィルタ 3 回反復（＝ガウシアン近似）を分離実行する。
///
/// 6 サブパスで temp(t0/t1) をピンポンし、最終結果を `dst` へ書き出す:
///   src→t0(H), t0→t1(V), t1→t0(H), t0→t1(V), t1→t0(H), t0→dst(V)
#[allow(clippy::too_many_arguments)]
fn run_blur(
    device: &wgpu::Device,
    encoder: &mut wgpu::CommandEncoder,
    pfx: &PostfxContext,
    src: &wgpu::TextureView,
    dst: &wgpu::TextureView,
    t0: &wgpu::TextureView,
    t1: &wgpu::TextureView,
    w: i32,
    h: i32,
    radius: i32,
) {
    // (入力, 出力, 水平フラグ) の 6 サブパス。
    let steps: [(&wgpu::TextureView, &wgpu::TextureView, u32); 6] = [
        (src, t0, 1),
        (t0, t1, 0),
        (t1, t0, 1),
        (t0, t1, 0),
        (t1, t0, 1),
        (t0, dst, 0),
    ];
    for (s, d, horizontal) in steps {
        blur_step(device, encoder, pfx, s, d, horizontal, w, h, radius);
    }
}

/// blur の 1 サブパス（1 方向の走査線ランニング和）を記録する。
#[allow(clippy::too_many_arguments)]
fn blur_step(
    device: &wgpu::Device,
    encoder: &mut wgpu::CommandEncoder,
    pfx: &PostfxContext,
    src: &wgpu::TextureView,
    dst: &wgpu::TextureView,
    horizontal: u32,
    w: i32,
    h: i32,
    radius: i32,
) {
    let params = BlurParams {
        radius,
        horizontal,
        width: w,
        height: h,
    };
    let ubo = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some("Postfx Blur Params"),
        contents: bytemuck::bytes_of(&params),
        usage: wgpu::BufferUsages::UNIFORM,
    });
    let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("Postfx Blur BG"),
        layout: &pfx.blur.bgl,
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
        ],
    });
    let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
        label: Some("Postfx Blur Pass"),
        timestamp_writes: None,
    });
    pass.set_pipeline(&pfx.blur.pipeline);
    pass.set_bind_group(0, &bg, &[]);
    // 走査線本数（水平=行数=高さ / 垂直=列数=幅）を 64 スレッド/ワークグループで割る。
    let count = if horizontal == 1 { h } else { w };
    let groups = ((count.max(0) as u32) + 63) / 64;
    pass.dispatch_workgroups(groups.max(1), 1, 1);
}
