use std::path::Path;
use wgpu::util::DeviceExt;
use rayon::prelude::*;
use crate::engine::core::loader::model::{
    Model, ModelNode, Primitive, Vertex, TextureData, TextureSource, SamplerData,
    FilterMode, WrapMode, Material, AlphaMode, CullFace, CachedTexFormat, TextureInfo,
    NormalTextureInfo, OcclusionTextureInfo,
};
use super::uniforms::{CameraUniform, ModelUniform, MaterialUniform, JointUniform, ColorVertex,
                      GpuCullData, GizmoVertex};
use super::skin_system::SkinComputeSystem;
use super::pipeline::{SkinComputePipeline, MeshletCullPipeline};
use super::material_asset;
use crate::engine::components::material_override::{MaterialOverride, MaterialOverrideKind};

// ============================================================
//  GPU メッシュレットカリング対応フラグ（第1弾）
// ============================================================

/// GPU が MULTI_DRAW_INDIRECT_COUNT に対応しているか。
/// レンダラ初期化時に `set_meshlet_cull_supported` で設定する。デフォルト false。
/// false のデバイスではメッシュレットカリング経路は完全に無効化され、
/// 従来の CPU カリング＋`draw_indexed` 経路がそのまま使われる（フォールバック）。
static MESHLET_CULL_SUPPORTED: std::sync::atomic::AtomicBool =
    std::sync::atomic::AtomicBool::new(false);

/// GPU の MULTI_DRAW_INDIRECT_COUNT 対応可否を設定する（デバイス生成時に一度呼ぶ）。
pub fn set_meshlet_cull_supported(v: bool) {
    MESHLET_CULL_SUPPORTED.store(v, std::sync::atomic::Ordering::Relaxed);
}

/// GPU が MULTI_DRAW_INDIRECT_COUNT に対応しているか（メッシュレットカリング可否）。
pub fn meshlet_cull_supported() -> bool {
    MESHLET_CULL_SUPPORTED.load(std::sync::atomic::Ordering::Relaxed)
}

// ============================================================
//  マテリアルオーバーライド適用（Phase R7）で使うマジックナンバー排除用定数
// ============================================================

/// オーバーライドで新規追加するテクスチャの UV セット番号（既定は UV0 のみサポート）。
const OVERRIDE_TEX_COORD_SET: u32 = 0;
/// オーバーライド法線マップの強度スケール既定値（フル強度）。
const OVERRIDE_NORMAL_SCALE: f32 = 1.0;
/// オーバーライド AO テクスチャの強度既定値（フル適用）。
const OVERRIDE_OCCLUSION_STRENGTH: f32 = 1.0;

// ============================================================
//  デフォルトテクスチャ（テクスチャなし時のフォールバック）
// ============================================================

/// マテリアルにテクスチャが指定されていない場合のデフォルト値。
pub struct DefaultTextures {
    /// 1×1 白（base_color / occlusion のデフォルト）
    pub white:      GpuTexture,
    /// 1×1 フラット法線 (0.5, 0.5, 1.0) — normal map デフォルト
    pub flat_normal: GpuTexture,
    /// 1×1 黒（emissive のデフォルト）
    pub black:      GpuTexture,
}

impl DefaultTextures {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        let sd = SamplerData::default();
        Self {
            white:      upload_rgba8(device, queue, 1, 1, &[255, 255, 255, 255], false, &sd, Some("Default White")),
            flat_normal: upload_rgba8(device, queue, 1, 1, &[128, 128, 255, 255], false, &sd, Some("Default Normal")),
            black:      upload_rgba8(device, queue, 1, 1, &[0,   0,   0,   255], false, &sd, Some("Default Black")),
        }
    }
}

// ============================================================
//  GpuTexture
// ============================================================

pub struct GpuTexture {
    #[allow(dead_code)]
    pub texture: wgpu::Texture,
    pub view:    wgpu::TextureView,
    pub sampler: wgpu::Sampler,
}

/// RGBA8 ピクセルデータから GPU テクスチャを生成する。
///
/// - `linear`: `true` なら `Rgba8Unorm`（法線・MR・AO）、`false` なら `Rgba8UnormSrgb`（ベースカラー・エミッシブ）
pub fn upload_rgba8(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    width:   u32,
    height:  u32,
    pixels:  &[u8],
    linear:  bool,
    sampler: &SamplerData,
    label:   Option<&str>,
) -> GpuTexture {
    let format = if linear {
        wgpu::TextureFormat::Rgba8Unorm
    } else {
        wgpu::TextureFormat::Rgba8UnormSrgb
    };

    let texture = device.create_texture_with_data(
        queue,
        &wgpu::TextureDescriptor {
            label,
            size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        },
        wgpu::util::TextureDataOrder::LayerMajor,
        pixels,
    );

    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());

    let (mag, min, mipmap) = conv_filter(sampler.min_filter, sampler.mag_filter);
    let gpu_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        label:            None,
        address_mode_u:   conv_wrap(sampler.wrap_u),
        address_mode_v:   conv_wrap(sampler.wrap_v),
        address_mode_w:   wgpu::AddressMode::Repeat,
        mag_filter:       mag,
        min_filter:       min,
        mipmap_filter:    mipmap,
        ..Default::default()
    });

    GpuTexture { texture, view, sampler: gpu_sampler }
}

/// `TextureData` から GPU テクスチャを生成する。
///
/// `TextureSource::FilePath` の場合は `image` クレートでロードする。
pub fn upload_texture_data(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    tex:     &TextureData,
    linear:  bool,
) -> GpuTexture {
    match &tex.source {
        TextureSource::Embedded { width, height, pixels } => {
            upload_rgba8(device, queue, *width, *height, pixels, linear, &tex.sampler,
                         tex.name.as_deref())
        }
        TextureSource::FilePath(path) => {
            load_from_file(device, queue, path, linear, &tex.sampler)
        }
        // 派生キャッシュ由来の即アップロード形式（BC 圧縮 or RGBA ミップ）
        TextureSource::Ready { format, width, height, mips } => {
            upload_ready(device, queue, *format, *width, *height, mips, &tex.sampler,
                         tex.name.as_deref())
        }
        // 遅延デコード画像がキャッシュ処理を経ずに残った場合のフォールバック。
        // （通常は process_model_textures が Ready 化するため到達しない）
        TextureSource::EncodedBytes { bytes } => {
            match image::load_from_memory(bytes) {
                Ok(img) => {
                    let rgba = img.to_rgba8();
                    let (w, h) = rgba.dimensions();
                    upload_rgba8(device, queue, w, h, &rgba, linear, &tex.sampler,
                                 tex.name.as_deref())
                }
                Err(e) => {
                    eprintln!("[SEED cache] 埋め込み画像のデコード失敗（マゼンタで代替）: err={e}");
                    // 1×1 マゼンタ（エラー可視化用）
                    upload_rgba8(device, queue, 1, 1, &[255, 0, 255, 255], linear,
                                 &tex.sampler, tex.name.as_deref())
                }
            }
        }
    }
}

/// `CachedTexFormat` を wgpu の `TextureFormat` に変換する。
fn conv_cached_format(f: CachedTexFormat) -> wgpu::TextureFormat {
    use wgpu::TextureFormat as W;
    match f {
        CachedTexFormat::Rgba8Unorm       => W::Rgba8Unorm,
        CachedTexFormat::Rgba8UnormSrgb   => W::Rgba8UnormSrgb,
        CachedTexFormat::Bc3RgbaUnormSrgb => W::Bc3RgbaUnormSrgb,
        CachedTexFormat::Bc3RgbaUnorm     => W::Bc3RgbaUnorm,
        CachedTexFormat::Bc1RgbaUnorm     => W::Bc1RgbaUnorm,
        CachedTexFormat::Bc5RgUnorm       => W::Bc5RgUnorm,
        CachedTexFormat::Bc4RUnorm        => W::Bc4RUnorm,
        CachedTexFormat::Bc6hRgbUfloat    => W::Bc6hRgbUfloat,
    }
}

/// 派生キャッシュの `Ready`（ミップ付き BC/RGBA データ）を GPU テクスチャにアップロードする。
///
/// 各ミップを `queue.write_texture` で個別に書き込む。BC フォーマットの
/// `bytes_per_row` はブロック行あたりのバイト数 `ceil(w/4) * block_bytes` を使う。
/// `queue.write_texture` は 256B アライメント制約がない（バッファコピーのみの制約）ため、
/// ブロック行そのままのピッチで書き込める。
fn upload_ready(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    format:  CachedTexFormat,
    width:   u32,
    height:  u32,
    mips:    &[Vec<u8>],
    sampler: &SamplerData,
    label:   Option<&str>,
) -> GpuTexture {
    let wgpu_format = conv_cached_format(format);
    let mip_level_count = mips.len().max(1) as u32;
    let block_bytes = format.block_bytes();
    let is_bc = format.is_block_compressed();

    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label,
        size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
        mip_level_count,
        sample_count: 1,
        dimension:    wgpu::TextureDimension::D2,
        format:       wgpu_format,
        usage:        wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });

    // 各ミップを書き込む
    for (level, data) in mips.iter().enumerate() {
        // 論理サイズ（ミップチェーンの数学的サイズ）
        let mw = (width  >> level).max(1);
        let mh = (height >> level).max(1);

        // bytes_per_row / rows / コピーサイズ:
        // BC はブロック単位のため、コピー幅・高さを物理サイズ
        // `ceil(w/4)*4 × ceil(h/4)*4` で指定する。wgpu の検証は
        // 「コピー幅がブロック幅(4)の倍数」を要求するため、末端ミップ
        // （2×2, 1×1）や非 4 倍数ミップ（例: 20→10→5）を論理サイズで
        // コピーすると Validation Error になる。圧縮ミップに対しては
        // 論理サイズを超える物理サイズのコピーが許容されている。
        // データは asset_cache 側でも物理サイズで圧縮済みのため長さが一致する。
        let (bytes_per_row, rows, copy_w, copy_h) = if is_bc {
            let blocks_x = mw.div_ceil(4);
            let blocks_y = mh.div_ceil(4);
            (blocks_x * block_bytes, blocks_y, blocks_x * 4, blocks_y * 4)
        } else {
            (mw * block_bytes, mh, mw, mh)
        };

        // データ長が不足している破損ケースはスキップ（クラッシュ回避）。
        if (bytes_per_row as usize) * (rows as usize) > data.len() {
            eprintln!("[SEED cache] Ready テクスチャのミップ {level} のデータ長が不足。スキップします。");
            continue;
        }

        queue.write_texture(
            wgpu::ImageCopyTexture {
                texture:   &texture,
                mip_level: level as u32,
                origin:    wgpu::Origin3d::ZERO,
                aspect:    wgpu::TextureAspect::All,
            },
            data,
            wgpu::ImageDataLayout {
                offset:         0,
                bytes_per_row:  Some(bytes_per_row),
                rows_per_image: Some(rows),
            },
            wgpu::Extent3d { width: copy_w, height: copy_h, depth_or_array_layers: 1 },
        );
    }

    let view = texture.create_view(&wgpu::TextureViewDescriptor::default());

    let (mag, min, mipmap) = conv_filter(sampler.min_filter, sampler.mag_filter);
    let gpu_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        label:          None,
        address_mode_u: conv_wrap(sampler.wrap_u),
        address_mode_v: conv_wrap(sampler.wrap_v),
        address_mode_w: wgpu::AddressMode::Repeat,
        mag_filter:     mag,
        min_filter:     min,
        mipmap_filter:  mipmap,
        // ミップを持つのでフィルタ有効化。lod 範囲はデフォルト（全ミップ使用）。
        ..Default::default()
    });

    GpuTexture { texture, view, sampler: gpu_sampler }
}

fn load_from_file(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    path:    &Path,
    linear:  bool,
    sampler: &SamplerData,
) -> GpuTexture {
    // asset_fs 経由で読む（仮想パス assets:// と PAK に対応）
    let path_str = path.to_str().unwrap_or("");
    let img = crate::engine::asset_fs::read_image(path_str);
    upload_rgba8(device, queue, img.width(), img.height(), &img, linear, sampler,
                 path.to_str())
}

// ─── フォーマット変換ヘルパー ─────────────────────────────────

fn conv_wrap(w: WrapMode) -> wgpu::AddressMode {
    match w {
        WrapMode::Repeat         => wgpu::AddressMode::Repeat,
        WrapMode::MirroredRepeat => wgpu::AddressMode::MirrorRepeat,
        WrapMode::ClampToEdge    => wgpu::AddressMode::ClampToEdge,
    }
}

fn conv_filter(
    min: FilterMode,
    mag: FilterMode,
) -> (wgpu::FilterMode, wgpu::FilterMode, wgpu::FilterMode) {
    let mag_f = match mag {
        FilterMode::Nearest | FilterMode::NearestMipmapNearest | FilterMode::NearestMipmapLinear
            => wgpu::FilterMode::Nearest,
        _   => wgpu::FilterMode::Linear,
    };
    let (min_f, mip_f) = match min {
        FilterMode::Nearest | FilterMode::NearestMipmapNearest
            => (wgpu::FilterMode::Nearest, wgpu::FilterMode::Nearest),
        FilterMode::NearestMipmapLinear
            => (wgpu::FilterMode::Nearest, wgpu::FilterMode::Linear),
        FilterMode::LinearMipmapNearest
            => (wgpu::FilterMode::Linear, wgpu::FilterMode::Nearest),
        _   => (wgpu::FilterMode::Linear, wgpu::FilterMode::Linear),
    };
    (mag_f, min_f, mip_f)
}

// ============================================================
//  GpuMaterial
// ============================================================

pub struct GpuMaterial {
    pub bind_group:     wgpu::BindGroup,
    /// アルファ合成モード（Opaque / Mask / Blend）。
    /// 透明描画の分類（Phase R5）の唯一の真実source。Opaque・Mask は不透明パス、
    /// Blend は透明パスへ振り分ける。`Material::alpha_mode` を upload 時に複製する。
    pub alpha_mode:     AlphaMode,
    /// アルファテスト（Mask）のカットオフ閾値（B3）。`Material::alpha_cutoff` を upload 時に複製する
    /// （生値。Mask 以外では使わない）。色付き影のバインドレス Mask アルファ抜きで、ヒット点テクスチャ
    /// α をこの閾値と比較して葉の形の影を落とす。rt_shadow.rs が BINDLESS_FLAG_MASK と併せて記録する。
    pub alpha_cutoff:   f32,
    /// カリング面（Back / Front / None）。
    /// 描画側はこの値でパイプラインバリアント（cull_mode 3 種）を選ぶ。
    /// メッシュレットカリングの法線コーン背面棄却も Back のときのみ有効化する。
    /// `Material::cull_face` を upload 時に複製する（オーバーライド適用後の実効値）。
    pub cull_face:      CullFace,
    /// 実効ベースカラー係数（override 適用後）。バインドレス（B2）の RT 反射ヒット
    /// シェーディングで、サンプルした albedo テクスチャ値へ乗算するために保持する。
    /// avg_albedo（テクスチャ平均×factor）と違い、テクスチャ生値へ掛ける「係数そのもの」。
    pub base_color_factor: [f32; 4],
    /// ベースカラーテクスチャの `GpuModel.textures` インデックス（テクスチャ無しは None）。
    /// バインドレス登録（B2）で、このマテリアルの albedo テクスチャ view を引くために保持する。
    pub base_color_tex_index: Option<usize>,
    /// バインドレス テクスチャ配列に登録した albedo テクスチャの安定インデックス
    /// （未登録・非対応 GPU は `BINDLESS_DUMMY_TEX_INDEX`=0）。B2 の RT 反射が引く。
    /// `GpuModel::register_bindless` が対応 GPU でのみ非 0 を書き込む。
    pub bindless_albedo_tex_index: u32,
    #[allow(dead_code)]
    uniform_buffer:     wgpu::Buffer,
    // テクスチャのライフタイムを保持する
    #[allow(dead_code)]
    textures:           Vec<GpuTexture>,
}

/// 拡散透過（diffuse_transmission）機能の一時無効化フラグ。
///
/// true の間、`build_material_uniform` が GPU へ渡す直前に diffuse_transmission を 0.0 に
/// 強制する。Material 構造体・serde・.mat・インライン上書き・Inspector UI・シェーダ側の
/// 実装は一切変更しない（入口を塞ぐだけ）。将来再有効化する場合は false に戻す。
const DIFFUSE_TRANSMISSION_DISABLED: bool = true;

/// `Material` から GPU 用 `MaterialUniform`（std140 レイアウト）を組み立てる純関数。
///
/// 【単一の真実source】マテリアル uniform のビット表現をここ 1 か所に集約する。
/// フルアップロード経路（`GpuMaterial::upload`）とインライン in-place 更新経路
/// （`GpuModel::update_material_inline`）の両方がこれを使うことで、同じ `Material` からは
/// 必ず 1 ビットも違わない同一 uniform が生成されることを保証する（値解釈のズレ防止）。
pub(crate) fn build_material_uniform(mat: &Material) -> MaterialUniform {
    // Mask のときだけカットオフを有効にする（それ以外は 0.0 = 破棄なし）。
    let alpha_cutoff = match mat.alpha_mode {
        AlphaMode::Mask => mat.alpha_cutoff,
        _               => 0.0,
    };
    MaterialUniform {
        base_color_factor:  mat.base_color_factor,
        metallic_factor:    mat.metallic_factor,
        roughness_factor:   mat.roughness_factor,
        alpha_cutoff,
        has_base_color_tex: mat.base_color_texture.is_some() as u32,
        emissive_factor:    mat.emissive_factor,
        has_normal_tex:     mat.normal_texture.is_some() as u32,
        has_mr_tex:         mat.metallic_roughness_texture.is_some() as u32,
        has_occlusion_tex:  mat.occlusion_texture.is_some() as u32,
        has_emissive_tex:   mat.emissive_texture.is_some() as u32,
        ior:                mat.ior,
        transmission:       mat.transmission,
        // MR テクスチャ無視トグル（旧 _pad0 を転用, offset 68）。1=無視（factor 直値）。
        // シェーダ側 surface_gather.wgsl の MR 採取 1 箇所が参照する（forward/G-Buffer 共通）。
        mr_tex_ignore:      mat.mr_tex_ignore as u32,
        // 拡散透過（葉・布・紙の逆光透け, offset 72）。lighting_eval.wgsl の逆光項が Surface 経由で読む。
        //
        // 【一時無効化】パラメータ数の割に制御が難しく、狙った見た目にならないため 2026-07-20 時点で
        // 無効化中。Material / .mat / インライン上書き・serde・エディタ UI・シェーダは温存し、
        // GPU へ渡す直前のこの 1 か所で強制的に 0.0 にする（= 既存シーンに保存済みの値も無視される）。
        // 再有効化するには DIFFUSE_TRANSMISSION_DISABLED を false にするだけでよい。
        diffuse_transmission: if DIFFUSE_TRANSMISSION_DISABLED { 0.0 } else { mat.diffuse_transmission },
        _pad2:              0.0,
    }
}

/// インラインオーバーライドを埋込マテリアルへ焼き込んで「実効マテリアル」を作る純関数。
///
/// 【単一の真実source】インライン各フィールドの解釈（parse_alpha_mode / parse_cull_face 含む）を
/// ここ 1 か所に集約する。`apply_overrides`（フル再アップロード経路）と
/// `GpuModel::update_material_inline`（in-place 更新経路）が同じ関数を通るため、
/// 同じインライン値からは必ず同一の実効マテリアルが得られる（両経路の結果が一致する保証）。
///
/// `kind` が `Inline` 以外なら `None`（呼び出し側で `MatAsset` 等を別処理する）。
/// `None` のフィールドは `base`（埋込値）をそのまま維持する。
pub(crate) fn bake_inline_material(base: &Material, kind: &MaterialOverrideKind) -> Option<Material> {
    let MaterialOverrideKind::Inline {
        base_color, metallic, roughness, emissive, alpha_mode, alpha_cutoff, ior, transmission, diffuse_transmission, mr_tex_ignore, cull_face,
    } = kind else {
        return None;
    };
    let mut eff: Material = base.clone();
    if let Some(v) = base_color   { eff.base_color_factor = *v; }
    if let Some(v) = metallic     { eff.metallic_factor   = *v; }
    if let Some(v) = roughness    { eff.roughness_factor  = *v; }
    if let Some(v) = emissive     { eff.emissive_factor   = *v; }
    if let Some(v) = alpha_mode   { eff.alpha_mode        = material_asset::parse_alpha_mode(v); }
    if let Some(v) = alpha_cutoff { eff.alpha_cutoff      = *v; }
    // 屈折率の上書き（None なら埋込値を維持）。RT-Translucency の屈折で使う。
    if let Some(v) = ior          { eff.ior               = *v; }
    // 透過率の上書き（None なら埋込値を維持）。ガラス表現の透け具合。
    if let Some(v) = transmission { eff.transmission      = *v; }
    // 拡散透過の上書き（None なら埋込値を維持）。葉・布・紙の逆光透け（ガラスの transmission とは別物）。
    if let Some(v) = diffuse_transmission { eff.diffuse_transmission = *v; }
    // MR テクスチャ無視トグルの上書き（None なら埋込値を維持）。true で MR テクスチャを無視。
    if let Some(v) = mr_tex_ignore { eff.mr_tex_ignore    = *v; }
    // カリング面の上書き（None なら埋込値＝glTF double_sided 由来を維持）。
    if let Some(v) = cull_face    { eff.cull_face         = material_asset::parse_cull_face(v); }
    // テクスチャ参照は維持（texture_index は元モデルのテクスチャ列を指したまま）。
    Some(eff)
}

impl GpuMaterial {
    pub fn upload(
        device:   &wgpu::Device,
        queue:    &wgpu::Queue,
        mat:      &Material,
        all_textures: &[TextureData],   // モデル全体のテクスチャスロット
        gpu_textures: &[GpuTexture],    // 上記に対応する GPU テクスチャ
        bgl:      &wgpu::BindGroupLayout,
        defaults: &DefaultTextures,
    ) -> Self {
        // ── ユニフォームバッファ ─────────────────────────────
        let uniform = build_material_uniform(mat);

        // COPY_DST を付与して、インライン値編集時に uniform を in-place で
        // `queue.write_buffer` 更新できるようにする（GpuModel 全体の再生成を回避＝OOM 対策）。
        let uniform_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Material Uniform"),
            contents: bytemuck::bytes_of(&uniform),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        // ── テクスチャ参照のヘルパー ─────────────────────────
        let tex_view = |idx: usize| &gpu_textures[idx].view;
        let tex_sampler = |idx: usize| &gpu_textures[idx].sampler;

        let base_color_idx   = mat.base_color_texture.as_ref().map(|t| t.texture_index);
        let normal_idx       = mat.normal_texture.as_ref().map(|t| t.texture_index);
        let mr_idx           = mat.metallic_roughness_texture.as_ref().map(|t| t.texture_index);
        let occlusion_idx    = mat.occlusion_texture.as_ref().map(|t| t.texture_index);
        let emissive_idx     = mat.emissive_texture.as_ref().map(|t| t.texture_index);

        let bc_view    = base_color_idx.map(tex_view).unwrap_or(&defaults.white.view);
        let bc_sampler = base_color_idx.map(tex_sampler).unwrap_or(&defaults.white.sampler);
        let nm_view    = normal_idx.map(tex_view).unwrap_or(&defaults.flat_normal.view);
        let nm_sampler = normal_idx.map(tex_sampler).unwrap_or(&defaults.flat_normal.sampler);
        let mr_view    = mr_idx.map(tex_view).unwrap_or(&defaults.white.view);
        let mr_sampler = mr_idx.map(tex_sampler).unwrap_or(&defaults.white.sampler);
        let ao_view    = occlusion_idx.map(tex_view).unwrap_or(&defaults.white.view);
        let ao_sampler = occlusion_idx.map(tex_sampler).unwrap_or(&defaults.white.sampler);
        let em_view    = emissive_idx.map(tex_view).unwrap_or(&defaults.black.view);
        let em_sampler = emissive_idx.map(tex_sampler).unwrap_or(&defaults.black.sampler);

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("Material BG"),
            layout: bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0,  resource: uniform_buffer.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1,  resource: wgpu::BindingResource::TextureView(bc_view) },
                wgpu::BindGroupEntry { binding: 2,  resource: wgpu::BindingResource::Sampler(bc_sampler) },
                wgpu::BindGroupEntry { binding: 3,  resource: wgpu::BindingResource::TextureView(nm_view) },
                wgpu::BindGroupEntry { binding: 4,  resource: wgpu::BindingResource::Sampler(nm_sampler) },
                wgpu::BindGroupEntry { binding: 5,  resource: wgpu::BindingResource::TextureView(mr_view) },
                wgpu::BindGroupEntry { binding: 6,  resource: wgpu::BindingResource::Sampler(mr_sampler) },
                wgpu::BindGroupEntry { binding: 7,  resource: wgpu::BindingResource::TextureView(ao_view) },
                wgpu::BindGroupEntry { binding: 8,  resource: wgpu::BindingResource::Sampler(ao_sampler) },
                wgpu::BindGroupEntry { binding: 9,  resource: wgpu::BindingResource::TextureView(em_view) },
                wgpu::BindGroupEntry { binding: 10, resource: wgpu::BindingResource::Sampler(em_sampler) },
            ],
        });

        Self {
            bind_group,
            alpha_mode: mat.alpha_mode,
            alpha_cutoff: mat.alpha_cutoff,
            cull_face:  mat.cull_face,
            base_color_factor:      mat.base_color_factor,
            base_color_tex_index:   base_color_idx,
            bindless_albedo_tex_index: crate::engine::core::renderer::BINDLESS_DUMMY_TEX_INDEX,
            uniform_buffer,
            textures: Vec::new(),
        }
    }
}

// ============================================================
//  GpuPrimitive / GpuMesh / GpuModel
// ============================================================

/// GPU メッシュレット記述子（std430 storage, stride 48）。
///
/// `index_offset` / `index_count` は `meshlet_index_buffer`（展開済みインデックス）への
/// オフセット・長さで、カリング compute が生存メッシュレットの `DrawIndexedIndirect` へ
/// そのまま書き込む。境界球・法線コーンはモデルローカル空間で、compute がインスタンス
/// 行列でワールド空間へ変換して視錐台/背面テストに使う。
///
/// レイアウトは `shaders/meshlet_cull.wgsl` の `Meshlet` 構造体と一致させること。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GpuMeshlet {
    pub index_offset: u32,   // offset 0
    pub index_count:  u32,   // offset 4
    pub _pad0:        u32,   // offset 8
    pub _pad1:        u32,   // offset 12
    pub center:       [f32; 3], // offset 16
    pub radius:       f32,   // offset 28
    pub cone_axis:    [f32; 3], // offset 32
    pub cone_cutoff:  f32,   // offset 44
}                            // size 48

pub struct GpuPrimitive {
    pub vertex_buffer:        wgpu::Buffer,
    /// 頂点数（RT 影 Phase R8: BLAS サイズ記述子で使用）。
    pub vertex_count:         u32,
    pub skin_vertex_buffer:   Option<wgpu::Buffer>,
    /// アウトライン描画用スムーズ法線（位置が同じ頂点の法線を平均化したもの）
    pub smooth_normal_buffer: wgpu::Buffer,
    /// LOD0 インデックスバッファ（フル解像度）
    pub index_buffer:         wgpu::Buffer,
    pub index_count:          u32,
    /// LOD1, LOD2, LOD3 インデックスバッファ（lod_index_buffers[0] = LOD1）
    pub lod_index_buffers:    Vec<wgpu::Buffer>,
    pub lod_index_counts:     Vec<u32>,
    pub material_index:       Option<usize>,

    // ── メッシュレット（GPU カリング第1弾, LOD0 のみ）─────────────
    /// 展開済みメッシュレットインデックスバッファ（Uint32）。各メッシュレットの三角形を
    /// 元頂点インデックスに解決して連結したもの。間接描画時の INDEX バッファとして使う。
    /// メッシュレット未生成のプリミティブでは None。
    pub meshlet_index_buffer: Option<wgpu::Buffer>,
    /// メッシュレット記述子バッファ（storage, `GpuMeshlet` 配列）。カリング compute が読む。
    pub meshlet_desc_buffer:  Option<wgpu::Buffer>,
    /// メッシュレット数。0 = メッシュレット未生成。
    pub meshlet_count:        u32,

    // ── バインドレス（B2）: メガバッファ登録結果 ────────────────
    /// UV メガバッファ内の先頭要素オフセット（vec2 単位）。非対応/未登録は 0。
    pub bindless_uv_offset:    u32,
    /// 登録した UV 要素数（= 頂点数）。RAII 解放と整合検証用。
    pub bindless_uv_count:     u32,
    /// インデックス メガバッファ内の先頭要素オフセット（u32 単位）。非対応/未登録は 0。
    pub bindless_index_offset: u32,
    /// 登録したインデックス要素数（= index_count）。
    pub bindless_index_count:  u32,
    /// 法線メガバッファ内の先頭要素オフセット（八面体 u32 単位・頂点順）。非対応/未登録は 0。
    /// RT 屈折の界面法線復元に使う（UV/index と同じ頂点番号で引く）。
    pub bindless_normal_offset: u32,
    /// バインドレス対象か（UV・インデックス・法線の 3 者すべてをメガバッファへ登録できた）。
    /// false のとき B2 のヒットシェーダは従来の平均色へ縮退する。
    pub bindless_eligible:     bool,
}

impl GpuPrimitive {
    /// `lod` 番号に対応するインデックスバッファとインデックス数を返す。
    /// 対応する LOD データが存在しない場合は利用可能な最高 LOD（最も簡略化済み）を使用。
    pub fn get_lod_index_buffer(&self, lod: usize) -> (&wgpu::Buffer, u32) {
        if lod == 0 || self.lod_index_buffers.is_empty() {
            return (&self.index_buffer, self.index_count);
        }
        let idx = (lod - 1).min(self.lod_index_buffers.len() - 1);
        (&self.lod_index_buffers[idx], self.lod_index_counts[idx])
    }

    fn upload(device: &wgpu::Device, prim: &Primitive) -> Self {
        use crate::engine::core::loader::model::{Vertex, SkinVertex};

        // RT 影（Phase R8）対応 GPU では、頂点/インデックスバッファに BLAS_INPUT 用途を足す。
        // BLAS は各頂点先頭（offset 0）の位置 Float32x3 を Vertex ストライドで直接読むため、
        // 位置のみの別バッファは作らず既存バッファへ用途を足す（キャッシュ v2 ロード経路でも同一）。
        // 非対応 GPU では用途を足さない（従来と完全に同一のバッファ）。
        let rt = super::rt_shadow::rt_shadows_supported();
        let vertex_usage = if rt {
            wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::BLAS_INPUT
        } else {
            wgpu::BufferUsages::VERTEX
        };
        let index_usage = if rt {
            wgpu::BufferUsages::INDEX | wgpu::BufferUsages::BLAS_INPUT
        } else {
            wgpu::BufferUsages::INDEX
        };

        let vertex_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Vertex Buffer"),
            contents: bytemuck::cast_slice::<Vertex, u8>(&prim.vertices),
            usage:    vertex_usage,
        });

        let smooth_normals = compute_smooth_normals(&prim.vertices, &prim.indices);
        let smooth_normal_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Smooth Normal Buffer"),
            contents: bytemuck::cast_slice::<[f32; 3], u8>(&smooth_normals),
            usage:    wgpu::BufferUsages::VERTEX,
        });

        let skin_vertex_buffer = if prim.skin_vertices.is_empty() {
            None
        } else {
            Some(device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label:    Some("Skin Vertex Buffer"),
                contents: bytemuck::cast_slice::<SkinVertex, u8>(&prim.skin_vertices),
                usage:    wgpu::BufferUsages::VERTEX,
            }))
        };

        // LOD0（フル解像度）インデックスバッファ。BLAS 構築（rt_shadow.rs）はこのバッファを
        // 参照するため、RT 対応時は BLAS_INPUT 用途（index_usage）が必須。
        let index_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Index Buffer"),
            contents: bytemuck::cast_slice(&prim.indices),
            usage:    index_usage,
        });

        // LOD インデックスバッファをアップロード
        // （BLAS は LOD0 の index_buffer のみを使うため、LOD1〜3 に BLAS_INPUT は不要）
        let lod_index_buffers: Vec<wgpu::Buffer> = prim.lod_indices.iter()
            .map(|lod_idx| device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label:    Some("LOD Index Buffer"),
                contents: bytemuck::cast_slice(lod_idx),
                usage:    wgpu::BufferUsages::INDEX,
            }))
            .collect();
        let lod_index_counts: Vec<u32> = prim.lod_indices.iter()
            .map(|lod_idx| lod_idx.len() as u32)
            .collect();

        // ── メッシュレット GPU バッファ（LOD0, GPU カリング第1弾）──────────
        // 展開済みインデックス（元頂点インデックス）と記述子（境界球・コーン）を構築。
        // メッシュレット未生成（スキン/微小/失敗）のプリミティブは None のまま。
        let (meshlet_index_buffer, meshlet_desc_buffer, meshlet_count) =
            Self::build_meshlet_buffers(device, prim);

        Self {
            vertex_buffer,
            vertex_count:   prim.vertices.len() as u32,
            skin_vertex_buffer,
            smooth_normal_buffer,
            index_buffer,
            index_count:    prim.indices.len() as u32,
            lod_index_buffers,
            lod_index_counts,
            material_index: prim.material_index,
            meshlet_index_buffer,
            meshlet_desc_buffer,
            meshlet_count,
            // バインドレス登録は GpuModel::register_bindless が後段で埋める（既定は非対象）。
            bindless_uv_offset:    0,
            bindless_uv_count:     0,
            bindless_index_offset: 0,
            bindless_index_count:  0,
            bindless_normal_offset: 0,
            bindless_eligible:     false,
        }
    }

    /// LOD0 メッシュレットの GPU バッファ（展開インデックス + 記述子）を構築する。
    ///
    /// - 展開インデックス: 各メッシュレットの三角形（ローカル頂点番号）を
    ///   `meshlet_vertices` を介して元頂点インデックスへ解決し連結する。間接描画の
    ///   INDEX バッファとして使い、記述子の `index_offset`/`index_count` が各メッシュレットの
    ///   範囲を指す。
    /// - 記述子: `GpuMeshlet`（境界球・法線コーン + 展開インデックス範囲）。
    ///
    /// メッシュレット未生成なら `(None, None, 0)` を返す（従来経路で描画される）。
    fn build_meshlet_buffers(
        device: &wgpu::Device,
        prim:   &Primitive,
    ) -> (Option<wgpu::Buffer>, Option<wgpu::Buffer>, u32) {
        if !prim.has_meshlets() {
            return (None, None, 0);
        }

        let mut expanded: Vec<u32> = Vec::new();
        let mut gpu_meshlets: Vec<GpuMeshlet> = Vec::with_capacity(prim.meshlets.len());

        for m in &prim.meshlets {
            let index_offset = expanded.len() as u32;
            let vbase = m.vertex_offset as usize;
            let tbase = m.triangle_offset as usize;
            let corner_count = (m.triangle_count * 3) as usize;
            // 各三角形コーナー: ローカル頂点番号 → メッシュレット頂点表 → 元頂点インデックス。
            for c in 0..corner_count {
                let local = prim.meshlet_triangles[tbase + c] as usize;
                let orig  = prim.meshlet_vertices[vbase + local];
                expanded.push(orig);
            }
            gpu_meshlets.push(GpuMeshlet {
                index_offset,
                index_count: m.triangle_count * 3,
                _pad0: 0,
                _pad1: 0,
                center:      m.center,
                radius:      m.radius,
                cone_axis:   m.cone_axis,
                cone_cutoff: m.cone_cutoff,
            });
        }

        // 展開インデックスが空（理論上到達しない）の場合は無効化。
        if expanded.is_empty() || gpu_meshlets.is_empty() {
            return (None, None, 0);
        }

        let index_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Meshlet Index Buffer"),
            contents: bytemuck::cast_slice(&expanded),
            usage:    wgpu::BufferUsages::INDEX,
        });
        let desc_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Meshlet Desc Buffer"),
            contents: bytemuck::cast_slice(&gpu_meshlets),
            usage:    wgpu::BufferUsages::STORAGE,
        });

        (Some(index_buf), Some(desc_buf), gpu_meshlets.len() as u32)
    }
}

/// 隣接トライアングルの面法線を加重平均してスムーズ法線を計算する。
///
/// ハードエッジ部分（頂点が複製されている）では複製ごとに独立した結果になるが、
/// それでも同一プリミティブ内の連続面の法線は滑らかになりアウトラインの破綻を軽減する。
fn compute_smooth_normals(
    vertices: &[crate::engine::core::loader::model::Vertex],
    indices:  &[u32],
) -> Vec<[f32; 3]> {
    let n = vertices.len();
    let mut accum = vec![[0.0f32; 3]; n];

    for tri in indices.chunks(3) {
        if tri.len() < 3 { continue; }
        let (i0, i1, i2) = (tri[0] as usize, tri[1] as usize, tri[2] as usize);
        if i0 >= n || i1 >= n || i2 >= n { continue; }

        let p0 = vertices[i0].position;
        let p1 = vertices[i1].position;
        let p2 = vertices[i2].position;

        let e1 = [p1[0]-p0[0], p1[1]-p0[1], p1[2]-p0[2]];
        let e2 = [p2[0]-p0[0], p2[1]-p0[1], p2[2]-p0[2]];
        // 面積比例の面法線（正規化しないことで大きい面を重視）
        // エンジンは左手座標系（Z 反転済みの頂点座標）を使用するため、
        // 標準クロス積 e1×e2 は LH でのアウトライン向き（外向き）と逆になる。
        // → e2×e1 = -(e1×e2) を使って正しい外向き法線を得る。
        let fn_ = [
            e2[1]*e1[2] - e2[2]*e1[1],
            e2[2]*e1[0] - e2[0]*e1[2],
            e2[0]*e1[1] - e2[1]*e1[0],
        ];

        for &i in &[i0, i1, i2] {
            accum[i][0] += fn_[0];
            accum[i][1] += fn_[1];
            accum[i][2] += fn_[2];
        }
    }

    accum.into_iter().enumerate().map(|(i, mut s)| {
        let len = (s[0]*s[0] + s[1]*s[1] + s[2]*s[2]).sqrt();
        if len > 1e-6 {
            s[0] /= len; s[1] /= len; s[2] /= len;
            s
        } else {
            // インデックス未使用の孤立頂点: 元の法線をフォールバックとして使用
            vertices[i].normal
        }
    }).collect()
}

pub struct GpuMesh {
    pub primitives: Vec<GpuPrimitive>,
}

/// モデル全体の GPU リソース。
/// `GpuModel::update_material_inline` の結果（in-place 更新の可否）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InlineUpdateResult {
    /// in-place 更新に成功。バッチ・BLAS の再構築は不要。
    Updated,
    /// alpha_mode が変わるため in-place 不可。呼び出し側でフル再構築すること。
    NeedsRebuild,
    /// slot 範囲外 or Inline 以外。呼び出し側でフォールバックすること。
    NotApplicable,
}

pub struct GpuModel {
    pub meshes:           Vec<GpuMesh>,
    pub materials:        Vec<GpuMaterial>,
    /// マテリアル未指定プリミティブ用のデフォルトマテリアル bind group
    pub default_material: GpuMaterial,
    /// スキンなしノード用の単位行列ジョイント bind group
    pub identity_joints_bg: wgpu::BindGroup,
    /// マテリアルごとのプリミティブ平均アルベド（Phase RT-GI, リニア RGBA）。
    /// materials と同順・同数。DDGI の TLAS パッキング（rt_shadow.rs）が material_index で引く。
    pub avg_albedos: Vec<[f32; 4]>,
    /// material_index が無い/範囲外のプリミティブ用の平均アルベド（既定マテリアル由来）。
    pub default_avg_albedo: [f32; 4],
    /// マテリアルごとの透過率（ガラス表現, 0..1）。materials と同順・同数。
    /// RT 色付き影の「影の透過量」計算（rt_shadow.rs）が material_index で引く。
    pub transmissions: Vec<f32>,
    /// material_index が無い/範囲外のプリミティブ用の透過率（既定マテリアル由来＝0.0）。
    pub default_transmission: f32,
    /// マテリアルごとの屈折率（ガラス表現, 通常 1.0..2.0）。materials と同順・同数。
    /// sequential grab（屈折の逐次グラブ）が「このプリミティブが屈折に関与するか
    /// （ior>1 または transmission>0）」を距離ソート描画中に判定するために引く。
    /// transmissions と完全に同じ更新経路（コンストラクタ＋override 3 箇所）で常に同期させる。
    pub iors: Vec<f32>,
    /// material_index が無い/範囲外のプリミティブ用の屈折率（既定マテリアル由来＝1.0）。
    pub default_ior: f32,
    // テクスチャ所有権
    #[allow(dead_code)]
    textures: Vec<GpuTexture>,
    /// バインドレス（B2）割り当ての RAII ハンドル。対応 GPU で `register_bindless` 済みのとき Some。
    /// **この GpuModel が drop されると Drop が発火し、登録した texture 配列スロット・UV/index
    /// メガバッファ領域の解放要求が自動でキューへ積まれる**（どの drop 地点でも 1 回だけ＝
    /// リーク・二重解放なし）。非対応 GPU では常に None（従来経路と完全に同一）。
    bindless_alloc: Option<crate::engine::core::renderer::BindlessModelAlloc>,
}

/// 実効マテリアルからプリミティブ平均アルベド（リニア RGBA）を導く（Phase RT-GI / RT-Translucency）。
///
/// - `.rgb`（GI のバウンス色・色付き影の色相）: **テクスチャ平均 × 現在の（override 適用後の）
///   `base_color_factor.rgb`**。テクスチャ無しなら `base_color_tex_avg` が白なので base_color_factor そのもの。
/// - `.a`（色付き影の「影の不透明度」）: **常に override 適用後の `base_color_factor.a` を使う**。
///
/// 【なぜ「テクスチャ平均 × 現 factor」で再計算するのか（色付き影の退行・check C / 濃い黒の影）】
/// 旧実装は baked `avg_albedo`（＝テクスチャ平均 × **ロード時** factor）の rgb を、非白なら丸ごと採用し、
/// 白（＝[1,1,1]）のときだけ base_color_factor を使っていた。これは 2 つの退行を招いていた:
///   1. **.a の固定**: baked `.a` はロード時アルファのままで、実行時の Blend 化・アルファ変更を反映しない。
///   2. **色相の固定（本修正の主眼）**: baked `.rgb` はロード時 factor を折り込んでいるため、インライン編集で
///      base_color を（例）シアンへ変えても、baked が非白なら旧色が採用され新色が影に出ない。とりわけ
///      **黒ベースの駒**（テクスチャ無し・ロード時 factor=[0,0,0]）を「シアンのガラス」に編集した場合、
///      baked=[0,0,0]（非白）が採用され、透過モデル T=(1-α)+α·tr·albedo の albedo が黒になって
///      **影が濃い黒**になる（表面は base_color_factor=シアンで正しくシアンに見えるのに影だけ黒）。
/// 実効アルベドは「テクスチャの色味（factor 抜き平均）× 現在の factor」が唯一の正しい定義なので、
/// factor を掛ける前の `base_color_tex_avg`（compute_material_avg_albedo が焼く）に現 factor を掛け直す。
/// これで表面色（base_color_texture × base_color_factor）と影の色相が一致する。
/// （透過率 transmission の折り込みは呼び出し側 rt_shadow.rs が α/tr パックで行う）。
fn eff_avg_albedo(eff: &Material) -> [f32; 4] {
    // テクスチャ平均（factor 抜き）× 現在の base_color_factor.rgb ＝ 実効アルベドの色味。
    // テクスチャ無しマテリアルは base_color_tex_avg=[1,1,1] なので base_color_factor そのものになる。
    let t = eff.base_color_tex_avg;
    let f = eff.base_color_factor;
    [t[0] * f[0], t[1] * f[1], t[2] * f[2], f[3]]
}

impl GpuModel {
    /// プリミティブのマテリアルインデックスからアルファ合成モードを返す（Phase R5）。
    ///
    /// `material_idx` が None・範囲外のときは default_material のモード（既定 Opaque）を返す。
    /// 透明描画の分類（不透明パス / 透明パス）で唯一の判定に用いる。
    pub fn primitive_alpha_mode(&self, material_idx: Option<usize>) -> AlphaMode {
        material_idx
            .and_then(|mi| self.materials.get(mi))
            .map(|m| m.alpha_mode)
            .unwrap_or(self.default_material.alpha_mode)
    }

    /// プリミティブのマテリアルインデックスからカリング面を返す。
    ///
    /// `material_idx` が None・範囲外のときは default_material の値（既定 Back）を返す。
    /// パイプラインバリアント選択（model_drawer / transparency）と、メッシュレットカリングの
    /// 法線コーン棄却の有効/無効判定の、唯一の判定入口。
    pub fn primitive_cull_face(&self, material_idx: Option<usize>) -> CullFace {
        material_idx
            .and_then(|mi| self.materials.get(mi))
            .map(|m| m.cull_face)
            .unwrap_or(self.default_material.cull_face)
    }

    /// プリミティブのマテリアルインデックスからプリミティブ平均アルベドを返す（Phase RT-GI）。
    /// `material_idx` が None・範囲外のときは default_avg_albedo を返す。
    /// DDGI のヒット点近似シェーディング（rt_shadow.rs の TLAS パッキング）で唯一の引き当て入口。
    pub fn primitive_avg_albedo(&self, material_idx: Option<usize>) -> [f32; 4] {
        material_idx
            .and_then(|mi| self.avg_albedos.get(mi).copied())
            .unwrap_or(self.default_avg_albedo)
    }

    /// プリミティブのマテリアルインデックスから透過率（ガラス表現, 0..1）を返す。
    /// `material_idx` が None・範囲外のときは default_transmission（0.0）を返す。
    /// RT 色付き影の「影の透過量」計算（rt_shadow.rs）で唯一の引き当て入口。
    pub fn primitive_transmission(&self, material_idx: Option<usize>) -> f32 {
        material_idx
            .and_then(|mi| self.transmissions.get(mi).copied())
            .unwrap_or(self.default_transmission)
    }

    /// プリミティブのマテリアルインデックスから屈折率（ガラス表現, 通常 1.0..2.0）を返す。
    /// `material_idx` が None・範囲外のときは default_ior（1.0）を返す。
    /// sequential grab（屈折の逐次グラブ）が「このプリミティブが屈折に関与するか」を
    /// 距離ソート描画中に判定する唯一の引き当て入口（transmission と組で refract 述語を成す）。
    pub fn primitive_ior(&self, material_idx: Option<usize>) -> f32 {
        material_idx
            .and_then(|mi| self.iors.get(mi).copied())
            .unwrap_or(self.default_ior)
    }

    /// プリミティブのマテリアルインデックスから実効ベースカラー係数を返す（バインドレス B2）。
    /// `material_idx` が None・範囲外のときは default_material の値（白）を返す。
    pub fn primitive_base_color_factor(&self, material_idx: Option<usize>) -> [f32; 4] {
        material_idx
            .and_then(|mi| self.materials.get(mi))
            .map(|m| m.base_color_factor)
            .unwrap_or(self.default_material.base_color_factor)
    }

    /// プリミティブのマテリアルインデックスからアルファカットオフ（Mask のアルファテスト閾値）を返す（B3）。
    /// `material_idx` が None・範囲外のときは default_material の値を返す。生値を返す（Mask 判定は
    /// alpha_mode 側で行い、色付き影の記録では BINDLESS_FLAG_MASK が立つ Mask のときだけ使う）。
    pub fn primitive_alpha_cutoff(&self, material_idx: Option<usize>) -> f32 {
        material_idx
            .and_then(|mi| self.materials.get(mi))
            .map(|m| m.alpha_cutoff)
            .unwrap_or_else(|| self.default_material.alpha_cutoff)
    }

    /// プリミティブのマテリアルインデックスからバインドレス albedo テクスチャ index を返す（B2）。
    /// `material_idx` が None・範囲外のときは default_material の値（既定ダミー 0）を返す。
    pub fn primitive_bindless_albedo_tex_index(&self, material_idx: Option<usize>) -> u32 {
        material_idx
            .and_then(|mi| self.materials.get(mi))
            .map(|m| m.bindless_albedo_tex_index)
            .unwrap_or(self.default_material.bindless_albedo_tex_index)
    }

    /// バインドレス（B2）: このモデルの albedo テクスチャと全プリミティブの UV/インデックスを
    /// テクスチャ配列・メガバッファへ登録し、RT 反射のヒットシェーディングで引けるようにする。
    ///
    /// - 呼ぶのは対応 GPU（`bindless` が Some）で **upload / apply_overrides の完了後**（実効
    ///   マテリアル・テクスチャが確定した後）に一度だけ。`DrawContext::upload_model[_with_overrides]` が呼ぶ。
    /// - `model`: UV（頂点順）とインデックス（三角形リスト）の CPU ソース。GpuPrimitive は
    ///   これらを保持しないため CPU モデルから読む（メッシュ/プリミティブは GpuModel::upload と 1:1）。
    /// - 解放は本モデルが drop されると RAII（`bindless_alloc` の Drop）で自動的にキューへ積まれる。
    ///   再登録前に `process_pending_frees` を呼んで解放済みスロット/領域を再利用する。
    pub fn register_bindless(
        &mut self,
        model:    &Model,
        queue:    &wgpu::Queue,
        bindless: &mut crate::engine::core::renderer::BindlessResources,
    ) {
        use crate::engine::core::renderer::BINDLESS_DUMMY_TEX_INDEX;
        // drop 済みモデルの解放要求を先に回収し、スロット/領域を再利用可能にする。
        bindless.process_pending_frees();
        let mut alloc = bindless.new_model_alloc();

        // ── マテリアル: ベースカラーテクスチャを配列へ登録 ──────────
        for mi in 0..self.materials.len() {
            let idx = match self.materials[mi].base_color_tex_index {
                Some(ti) if ti < self.textures.len() => {
                    bindless.register_albedo_texture_tracked(&self.textures[ti].view, &mut alloc)
                }
                // テクスチャ無し → ダミー 0。ヒット時は平均色へ縮退する（B2 シェーダのフォールバック）。
                _ => BINDLESS_DUMMY_TEX_INDEX,
            };
            self.materials[mi].bindless_albedo_tex_index = idx;
        }

        // ── プリミティブ: UV とインデックスをメガバッファへ登録 ──────
        // model.meshes と self.meshes は同順・同数（GpuModel::upload が 1:1 で構築）。
        for (mesh_i, mesh) in model.meshes.iter().enumerate() {
            let Some(gpu_mesh) = self.meshes.get_mut(mesh_i) else { break; };
            for (prim_i, prim) in mesh.primitives.iter().enumerate() {
                let Some(gp) = gpu_mesh.primitives.get_mut(prim_i) else { break; };
                // スキンメッシュは RT の BLAS 対象外（rt_shadow.rs が skin_vertex_buffer 有りを
                // スキップ）＝インスタンステーブルに載らないため、メガバッファへ登録しない
                // （UV/index 予算の浪費回避）。bindless_eligible は既定 false のまま。
                if !prim.skin_vertices.is_empty() { continue; }
                // UV0 と法線を頂点順に抽出（メガバッファは頂点番号で引くため頂点順が必須）。
                // 法線は八面体エンコード u32（4B/頂点）へ畳んで省メモリ化する（RT 屈折の界面法線復元用）。
                let uvs: Vec<[f32; 2]> = prim.vertices.iter().map(|v| v.uv0).collect();
                let normals: Vec<u32> = prim.vertices.iter()
                    .map(|v| crate::engine::core::renderer::bindless::oct_encode_normal(v.normal))
                    .collect();
                let (uv_off, idx_off, nrm_off, elig) =
                    bindless.register_primitive_geometry(queue, &uvs, &prim.indices, &normals, &mut alloc);
                gp.bindless_uv_offset     = uv_off;
                gp.bindless_uv_count      = if elig { uvs.len() as u32 } else { 0 };
                gp.bindless_index_offset  = idx_off;
                gp.bindless_index_count   = if elig { prim.indices.len() as u32 } else { 0 };
                gp.bindless_normal_offset = nrm_off;
                gp.bindless_eligible      = elig;
            }
        }

        self.bindless_alloc = Some(alloc);
    }

    pub fn upload(
        device:       &wgpu::Device,
        queue:        &wgpu::Queue,
        model:        &Model,
        material_bgl: &wgpu::BindGroupLayout,
        joint_bgl:    &wgpu::BindGroupLayout,
        defaults:     &DefaultTextures,
    ) -> Self {
        // ── テクスチャ ─────────────────────────────────────────
        let gpu_textures: Vec<GpuTexture> = model.textures.iter().enumerate().map(|(i, td)| {
            // TextureData.linear フラグで sRGB / 線形を切り替える。
            // false → Rgba8UnormSrgb（ベースカラー・エミッシブ）
            // true  → Rgba8Unorm   （法線・MR・AO など線形データ）
            upload_texture_data(device, queue, td, td.linear)
        }).collect();

        // ── マテリアル ─────────────────────────────────────────
        let materials: Vec<GpuMaterial> = model.materials.iter().map(|mat| {
            GpuMaterial::upload(device, queue, mat, &model.textures, &gpu_textures,
                                material_bgl, defaults)
        }).collect();

        // デフォルトマテリアル（白・完全不透明）
        let default_mat_data = Material::default();
        let default_material = GpuMaterial::upload(
            device, queue, &default_mat_data, &[], &[], material_bgl, defaults);

        // プリミティブ平均アルベド（Phase RT-GI）。materials と同順で焼く（DDGI の TLAS 引き当て用）。
        let avg_albedos: Vec<[f32; 4]> = model.materials.iter().map(|m| m.avg_albedo).collect();
        let default_avg_albedo = default_mat_data.avg_albedo;
        // 透過率（ガラス表現）。materials と同順で焼く（RT 色付き影の引き当て用）。
        let transmissions: Vec<f32> = model.materials.iter().map(|m| m.transmission).collect();
        let default_transmission = default_mat_data.transmission;
        // 屈折率（ガラス表現）。materials と同順で焼く（sequential grab の屈折関与判定用）。
        let iors: Vec<f32> = model.materials.iter().map(|m| m.ior).collect();
        let default_ior = default_mat_data.ior;

        // ── メッシュ ───────────────────────────────────────────
        let meshes: Vec<GpuMesh> = model.meshes.iter().map(|mesh| {
            GpuMesh {
                primitives: mesh.primitives.iter()
                    .map(|p| GpuPrimitive::upload(device, p))
                    .collect(),
            }
        }).collect();

        // ── 単位行列ジョイント bind group ─────────────────────
        let identity_joints = JointUniform::identity();
        let joint_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Identity Joints"),
            contents: bytemuck::bytes_of(&identity_joints),
            usage:    wgpu::BufferUsages::STORAGE,
        });
        let identity_joints_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("Identity Joints BG"),
            layout: joint_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: joint_buf.as_entire_binding(),
            }],
        });

        Self { meshes, materials, default_material, identity_joints_bg, avg_albedos, default_avg_albedo, transmissions, default_transmission, iors, default_ior, textures: gpu_textures, bindless_alloc: None }
    }

    /// マテリアルオーバーライドを GPU マテリアルへ焼き込む（Phase R7）。
    ///
    /// 【設計の核心】描画コード（draw_model_indirect / transparency.rs / shadow.rs /
    /// rt_shadow.rs）は `self.materials` と `primitive_alpha_mode()` からのみ
    /// マテリアル・アルファモードを読む。よってここで `self.materials[slot]` を
    /// 差し替えるだけで、描画コードを一切変更せずオーバーライドが全経路に反映される。
    ///
    /// - `slot` が `self.materials` の範囲外のオーバーライドは無視する（安全側）。
    /// - `default_material`（プリミティブに material_index が無い場合のフォールバック）は
    ///   対象外（オーバーライドは常に slot 指定＝materials 配列前提のため）。
    /// - `overrides` が空ならループが 0 回実行されるだけで、以前の挙動と完全に同一。
    pub fn apply_overrides(
        &mut self,
        device:       &wgpu::Device,
        queue:        &wgpu::Queue,
        model:        &Model,
        overrides:    &[MaterialOverride],
        material_bgl: &wgpu::BindGroupLayout,
        defaults:     &DefaultTextures,
    ) {
        // MatAsset オーバーライドが追加するローカルテクスチャの texture_index 起点。
        // Inline オーバーライドは埋込テクスチャのインデックス（0..base_tex_count）をそのまま
        // 使い回すため、self.textures の「元モデル分」の範囲だけを渡して意図しない
        // 追加テクスチャの混入を避ける（複数オーバーライドを連続適用する場合の安全策）。
        let base_tex_count = model.textures.len();

        for ovr in overrides {
            // slot が範囲外のオーバーライドは無視する（安全側フォールバック）。
            if ovr.slot >= self.materials.len() { continue; }

            match &ovr.kind {
                // ── インライン差し替え: 埋込マテリアルを複製し、指定フィールドのみ上書き ──
                MaterialOverrideKind::Inline { .. } => {
                    let Some(base) = model.materials.get(ovr.slot) else { continue };
                    // 焼き込みは共有関数に委譲（in-place 更新経路と 1 ビットもズレない）。
                    let Some(eff) = bake_inline_material(base, &ovr.kind) else { continue };

                    let tex_slice = &self.textures[..base_tex_count.min(self.textures.len())];
                    let gpu_mat = GpuMaterial::upload(
                        device, queue, &eff, &model.textures, tex_slice, material_bgl, defaults,
                    );
                    self.materials[ovr.slot] = gpu_mat;
                    // 平均アルベド（Phase RT-GI）も追従させる。baked 値（テクスチャ平均×元factor）を
                    // 優先し、無い（既定白）ときは base_color_factor を折り込む（近似）。
                    self.avg_albedos[ovr.slot] = eff_avg_albedo(&eff);
                    // 透過率（ガラス表現）も追従させる（RT 色付き影の引き当て用）。
                    self.transmissions[ovr.slot] = eff.transmission;
                    // 屈折率も追従させる（sequential grab の屈折関与判定用。transmissions と同期）。
                    self.iors[ovr.slot] = eff.ior;
                }

                // ── .mat アセット差し替え: アセットの factor/alpha + テクスチャを丸ごと適用 ──
                MaterialOverrideKind::MatAsset { path } => {
                    if path.is_empty() { continue; }
                    let Some(asset) = material_asset::load(path) else {
                        eprintln!("[SEED gpu_resources] MaterialOverride: .mat ロード失敗（path={path}）。オーバーライドをスキップします。");
                        continue;
                    };

                    let mut eff = Material {
                        name:              asset.name.clone(),
                        base_color_factor: asset.base_color,
                        metallic_factor:   asset.metallic,
                        roughness_factor:  asset.roughness,
                        emissive_factor:   asset.emissive,
                        alpha_mode:        material_asset::parse_alpha_mode(&asset.alpha_mode),
                        alpha_cutoff:      asset.alpha_cutoff,
                        // .mat が持つ屈折率をそのまま適用する（キー欠落時は serde 既定の 1.0）。
                        ior:               asset.ior,
                        // .mat が持つ透過率をそのまま適用する（キー欠落時は serde 既定の 0.0＝後方互換）。
                        transmission:      asset.transmission,
                        // .mat が持つ拡散透過をそのまま適用する（キー欠落時は serde 既定の 0.0＝後方互換）。
                        diffuse_transmission: asset.diffuse_transmission,
                        // .mat が持つ MR テクスチャ無視トグルを適用する（キー欠落時は serde 既定の false）。
                        mr_tex_ignore:     asset.mr_tex_ignore,
                        // .mat が持つカリング面をそのまま適用する（キー欠落時は serde 既定の "back"）。
                        cull_face:         material_asset::parse_cull_face(&asset.cull_face),
                        ..Material::default()
                    };

                    // .mat が指すテクスチャをこの呼び出し専用にアップロードし、self.textures へ
                    // 追加する（GpuModel が所有権を保持し続ける必要があるため：GpuMaterial は
                    // テクスチャを所有せず view/sampler の参照のみを bind group に焼き込む）。
                    // (テクスチャパス, linear フラグ, 差し込み先) の一覧。
                    // linear: albedo/emissive は sRGB(false)、normal/mr/occlusion は線形(true)。
                    let tex_path = &asset.textures;
                    if !tex_path.albedo.is_empty() {
                        let idx = Self::upload_override_texture(device, queue, &tex_path.albedo, false, &mut self.textures);
                        eff.base_color_texture = Some(TextureInfo { texture_index: idx, tex_coord_set: OVERRIDE_TEX_COORD_SET });
                    }
                    if !tex_path.normal.is_empty() {
                        let idx = Self::upload_override_texture(device, queue, &tex_path.normal, true, &mut self.textures);
                        eff.normal_texture = Some(NormalTextureInfo {
                            texture_index: idx, tex_coord_set: OVERRIDE_TEX_COORD_SET, scale: OVERRIDE_NORMAL_SCALE,
                        });
                    }
                    if !tex_path.metallic_roughness.is_empty() {
                        let idx = Self::upload_override_texture(device, queue, &tex_path.metallic_roughness, true, &mut self.textures);
                        eff.metallic_roughness_texture = Some(TextureInfo { texture_index: idx, tex_coord_set: OVERRIDE_TEX_COORD_SET });
                    }
                    if !tex_path.occlusion.is_empty() {
                        let idx = Self::upload_override_texture(device, queue, &tex_path.occlusion, true, &mut self.textures);
                        eff.occlusion_texture = Some(OcclusionTextureInfo {
                            texture_index: idx, tex_coord_set: OVERRIDE_TEX_COORD_SET, strength: OVERRIDE_OCCLUSION_STRENGTH,
                        });
                    }
                    if !tex_path.emissive.is_empty() {
                        let idx = Self::upload_override_texture(device, queue, &tex_path.emissive, false, &mut self.textures);
                        eff.emissive_texture = Some(TextureInfo { texture_index: idx, tex_coord_set: OVERRIDE_TEX_COORD_SET });
                    }

                    // texture_index は self.textures（元モデル分 + 今回追加分）を直接指すため、
                    // gpu_textures には self.textures 全体を渡す。
                    let gpu_mat = GpuMaterial::upload(
                        device, queue, &eff, &model.textures, &self.textures, material_bgl, defaults,
                    );
                    self.materials[ovr.slot] = gpu_mat;
                    // 平均アルベド（Phase RT-GI）。.mat の実効マテリアルには baked 平均が無いため
                    // base_color_factor を折り込む（テクスチャ色味は反映されない近似。docs 参照）。
                    self.avg_albedos[ovr.slot] = eff_avg_albedo(&eff);
                    // 透過率（ガラス表現）も追従させる（RT 色付き影の引き当て用）。
                    self.transmissions[ovr.slot] = eff.transmission;
                    // 屈折率も追従させる（sequential grab の屈折関与判定用。transmissions と同期）。
                    self.iors[ovr.slot] = eff.ior;
                }
            }
        }
    }

    /// 1 スロットのインラインオーバーライド値を **GpuModel を作り直さずに** in-place 反映する。
    ///
    /// 【なぜ必要か】インライン値のスライダー編集は毎イベント届く。従来はそのたびに
    /// `upload_model_with_overrides` で GpuModel（全テクスチャ＋全メッシュ）を再アップロードし、
    /// wgpu の遅延破棄と相まって瞬間 VRAM 2 倍需要 → OOM を起こしていた。インラインは
    /// テクスチャを差し替えないため、対象マテリアルの uniform（＋ cull_face）だけを書き換えれば
    /// 描画結果は同一になる。ここでは `write_buffer` 1 回（＋ CPU フィールド更新）で完結する。
    ///
    /// 戻り値で「再構築が必要か」を呼び出し側へ返す:
    /// - `Updated`       … in-place 更新成功。バッチ・BLAS 再構築は不要。
    /// - `NeedsRebuild`  … alpha_mode（不透明/半透明の描画パス分類）が変わるため in-place 不可。
    ///                     呼び出し側でフル再構築すること（描画パス・TLAS マスクが追従しないため）。
    /// - `NotApplicable` … slot 範囲外 or kind が Inline でない（呼び出し側でフォールバック）。
    pub fn update_material_inline(
        &mut self,
        queue: &wgpu::Queue,
        model: &Model,
        slot:  usize,
        kind:  &MaterialOverrideKind,
    ) -> InlineUpdateResult {
        if slot >= self.materials.len() { return InlineUpdateResult::NotApplicable; }
        let Some(base) = model.materials.get(slot) else { return InlineUpdateResult::NotApplicable; };
        // フル経路と同一の共有関数で実効マテリアルを作る（値解釈が 1 ビットもズレない）。
        let Some(eff) = bake_inline_material(base, kind) else { return InlineUpdateResult::NotApplicable; };

        // alpha_mode（Opaque/Mask/Blend）が変わると不透明パス⇔半透明パスの振り分け自体が
        // 変わる。in-place では統合バッチの分類・RT の TLAS インスタンスマスクが追従しないため、
        // これだけは呼び出し側のフル再構築に委ねる（安全側）。
        if eff.alpha_mode != self.materials[slot].alpha_mode {
            return InlineUpdateResult::NeedsRebuild;
        }

        // ① uniform を in-place 更新（write_buffer 1 回。フル経路と同一の build_material_uniform）。
        let uniform = build_material_uniform(&eff);
        queue.write_buffer(
            &self.materials[slot].uniform_buffer,
            0,
            bytemuck::bytes_of(&uniform),
        );
        // ② cull_face はパイプラインバリアント選択に効く（uniform 外）。draw 時に
        //    primitive_cull_face() が GpuMaterial.cull_face を読むため、フィールド更新で次フレームに反映。
        self.materials[slot].cull_face = eff.cull_face;
        // ②' バインドレス（B2）: RT 反射ヒットの base_color_factor も追従させる（テクスチャは
        //     不変＝bindless_albedo_tex_index はそのまま）。次フレームの TLAS 再構築で
        //     instance_table へ反映される（静止スキップ シグネチャに base_color_factor を含む）。
        self.materials[slot].base_color_factor = eff.base_color_factor;
        // ②'' バインドレス色付き影（B3）: Mask アルファ抜きのカットオフも追従させる。alpha_mode が
        //      Mask のまま cutoff だけ変えた in-place 編集で GpuMaterial.alpha_cutoff が古いままだと、
        //      静止スキップ シグネチャ（primitive_alpha_cutoff を含む）が不変になり TLAS/instance_table が
        //      再構築されず、葉の形の影のカットオフ編集が反映されない。ここで追従させて次フレームの
        //      TLAS 再構築を確実に発火させる（テクスチャは不変＝tex index はそのまま）。
        self.materials[slot].alpha_cutoff = eff.alpha_cutoff;
        // ③ 平均アルベド（Phase RT-GI / RT 色付き影）も追従させる（フル経路と同一の eff_avg_albedo）。
        self.avg_albedos[slot] = eff_avg_albedo(&eff);
        // ④ 透過率（ガラス表現）も追従させる。
        //    RT 色付き影の平均アルベド storage は TLAS 再構築フレームでしか再アップロードされないが、
        //    この avg_albedos/transmissions の更新は rt_shadow.rs の静止スキップ シグネチャに
        //    含まれる（平均アルベド・透過率をハッシュ）ため、in-place 編集の次フレームに TLAS が
        //    確実に再構築され、色付き影へ反映される（アルファ・色・透過率のスライダー編集が即時に効く）。
        self.transmissions[slot] = eff.transmission;
        // ⑤ 屈折率も追従させる（sequential grab の屈折関与判定用。transmissions と同期）。
        self.iors[slot] = eff.ior;

        InlineUpdateResult::Updated
    }

    /// オーバーライド用テクスチャ 1 枚をアップロードし `textures` 末尾へ追加、その
    /// インデックスを返すヘルパー（`apply_overrides` の MatAsset 分岐から使用）。
    ///
    /// `path` は `.mat` に記述された任意パス（assets:// 仮想パス / 絶対パスいずれも可）。
    /// `TextureSource::FilePath` 経由でアップロードすることで `asset_fs`（PAK 対応）を通す。
    fn upload_override_texture(
        device:  &wgpu::Device,
        queue:   &wgpu::Queue,
        path:    &str,
        linear:  bool,
        textures: &mut Vec<GpuTexture>,
    ) -> usize {
        let tex_data = TextureData {
            name:    Some(path.to_string()),
            source:  TextureSource::FilePath(std::path::PathBuf::from(path)),
            sampler: SamplerData::default(),
            linear,
        };
        let gpu_tex = upload_texture_data(device, queue, &tex_data, linear);
        textures.push(gpu_tex);
        textures.len() - 1
    }
}

// ============================================================
//  視錐台カリング
// ============================================================

/// ビュープロジェクション行列（行優先）から視錐台の 6 平面を抽出する。
///
/// 平面方程式: `dot(plane.xyz, P) + plane.w ≥ 0` が「内側」。
/// DX / wgpu の深度レンジ [0, 1] に対応する Near/Far 平面を使用。
pub fn extract_frustum_planes(vp: &[[f32; 4]; 4]) -> [[f32; 4]; 6] {
    // vp[row][col] 行優先: clip = vp * world_pos
    [
        // Left:   vp[3] + vp[0]
        add_rows(vp[3], vp[0]),
        // Right:  vp[3] - vp[0]
        sub_rows(vp[3], vp[0]),
        // Bottom: vp[3] + vp[1]
        add_rows(vp[3], vp[1]),
        // Top:    vp[3] - vp[1]
        sub_rows(vp[3], vp[1]),
        // Near:   vp[2]         （depth [0,1]）
        vp[2],
        // Far:    vp[3] - vp[2]
        sub_rows(vp[3], vp[2]),
    ]
}

#[inline]
fn add_rows(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    [a[0]+b[0], a[1]+b[1], a[2]+b[2], a[3]+b[3]]
}
#[inline]
fn sub_rows(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    [a[0]-b[0], a[1]-b[1], a[2]-b[2], a[3]-b[3]]
}

/// モデルローカル空間の AABB をルート変換行列でワールド空間に変換する。
///
/// 8 頂点すべてを変換し、新しい AABB を求める（精密かつシンプル）。
#[inline]
fn transform_aabb(
    local_min: [f32; 3],
    local_max: [f32; 3],
    mat:       &[[f32; 4]; 4],
) -> ([f32; 3], [f32; 3]) {
    let mut wmin = [f32::MAX; 3];
    let mut wmax = [f32::MIN; 3];
    for &bx in &[local_min[0], local_max[0]] {
        for &by in &[local_min[1], local_max[1]] {
            for &bz in &[local_min[2], local_max[2]] {
                for i in 0..3 {
                    let v = mat[i][0]*bx + mat[i][1]*by + mat[i][2]*bz + mat[i][3];
                    wmin[i] = wmin[i].min(v);
                    wmax[i] = wmax[i].max(v);
                }
            }
        }
    }
    (wmin, wmax)
}

/// モデルの全頂点から AABB（ローカル空間）を計算する。
fn compute_model_aabb(model: &Model) -> ([f32; 3], [f32; 3]) {
    let mut min = [f32::MAX; 3];
    let mut max = [f32::MIN; 3];
    let mut found = false;
    for mesh in &model.meshes {
        for prim in &mesh.primitives {
            for v in &prim.vertices {
                found = true;
                for i in 0..3 {
                    min[i] = min[i].min(v.position[i]);
                    max[i] = max[i].max(v.position[i]);
                }
            }
        }
    }
    if found { (min, max) } else { ([-1.0; 3], [1.0; 3]) }
}

// ============================================================
//  InstancedModelBatch — CPU フラスタムカリング + LOD + Indirect Draw
// ============================================================

/// LOD レベル数（0 = フル解像度、1〜3 = 簡略化済み）。
pub const NUM_LODS: usize = 4;

/// LOD 切り替え距離の二乗値。`dist_sq < LOD_DIST_SQ[i]` なら LOD i を使用。
/// [LOD0→LOD1, LOD1→LOD2, LOD2→LOD3] の境界距離の二乗。
const LOD_DIST_SQ: [f32; 3] = [
    10.0 * 10.0,   // 10 ユニット以内: LOD0（フル）
    30.0 * 30.0,   // 30 ユニット以内: LOD1（50%）
    60.0 * 60.0,   // 60 ユニット以内: LOD2（25%）
                   // 60 ユニット以遠: LOD3（10%）
];

/// レンダーパスで `draw_indexed` を呼ぶ際に必要な
/// (ノード, プリミティブ) ペア情報。
///
/// `node_prim_list` は `(is_skinned, material_idx)` で昇順ソート済み。
/// これによりパイプライン・マテリアル切り替え回数を最小化できる。
pub struct NodePrimDraw {
    /// `lod_node_data[lod][node_idx]` の bind group を参照するためのインデックス。
    pub node_idx:     usize,
    /// `gpu_model.meshes[mesh_idx]` の参照に使用。
    pub mesh_idx:     usize,
    /// `gpu_mesh.primitives[prim_idx]` の参照に使用。
    pub prim_idx:     usize,
    /// スキンメッシュパイプラインを使うかどうか（ソートキー①）。
    pub is_skinned:   bool,
    /// マテリアルインデックス（ソートキー②）。`None` は デフォルトマテリアル。
    pub material_idx: Option<usize>,
}

/// N インスタンス分のモデル変換・CPU フラスタムカリング・距離 LOD。
///
/// ## フレームごとの流れ
/// 1. dirty 時のみ: rayon でワールド行列と AABB を全インスタンス分計算して CPU キャッシュに保存
/// 2. 毎フレーム: 視錐台テスト → 可視インスタンスをカメラ距離で LOD バケットに振り分け →
///    各 LOD のコンパクト行列をノードバッファへアップロードし `lod_visible_counts` を更新。
/// 3. render pass: LOD ごとに `draw_indexed(0..lod_index_count, 0, 0..lod_visible_count)` で描画
///
/// ## Dirty Flag
/// インスタンス変換が変化した場合のみワールド行列・AABB を再計算する。
/// 視錐台カリング・LOD 選択は毎フレーム実行する（カメラが動くため）。
pub struct InstancedModelBatch {
    /// lod_node_data[lod][node_idx] = (storage_buffer, bind_group)。
    /// メッシュを持たないノードは None。
    pub lod_node_data:      Vec<Vec<Option<(wgpu::Buffer, wgpu::BindGroup)>>>,
    pub num_instances:      u32,
    /// 各 LOD の直前フレーム可視インスタンス数（draw_indexed の instance_count に使用）
    pub lod_visible_counts: [u32; NUM_LODS],

    // ── 事前計算データ ───────────────────────────────────────
    mesh_node_indices: Vec<usize>,
    node_pos_map:      Vec<Option<usize>>,

    // ── CPU キャッシュ（dirty 時のみ再計算）─────────────────
    /// ワールド行列キャッシュ: flat[inst_idx * n_mesh_nodes + pos]
    world_mats_cache: Vec<ModelUniform>,
    /// ワールド空間 AABB キャッシュ: [inst_idx]
    world_aabbs:      Vec<GpuCullData>,
    /// メッシュノード数（キャッシュアクセスに使用）
    n_mesh_nodes:     usize,

    /// レンダーパスで参照する (node, prim) フラットリスト
    pub node_prim_list: Vec<NodePrimDraw>,
    /// node_prim_list の長さ
    pub n_prims:        u32,

    // ── ローカル AABB ────────────────────────────────────────
    model_aabb_min: [f32; 3],
    model_aabb_max: [f32; 3],

    // ── GPU スキニング ───────────────────────────────────────
    /// スキンとアニメーションを持つ場合のみ Some
    pub skin: Option<SkinComputeSystem>,

    // ── ID パス用コンパクトインスタンス ID バッファ ───────────
    /// lod_id_buffers[lod]: そのフレームの可視インスタンス元インデックスの配列（u32）
    pub lod_id_buffers: Vec<wgpu::Buffer>,
    /// lod_id_bgs[lod]: id_pass パイプラインの group 2 用 BindGroup
    pub lod_id_bgs:     Vec<wgpu::BindGroup>,

    // ── アウトライン / ピック用 CPU コンパクトインスタンスリスト ──
    /// lod_compact_insts[lod]: compact_idx → original_inst_idx マッピング（CPU 側）
    pub lod_compact_insts: Vec<Vec<usize>>,

    // ── Dirty Flag ───────────────────────────────────────────
    dirty: bool,

    /// インスタンスごとの Animator 駆動権威時刻（元インスタンスインデックス順）。
    /// `Some(t)` のインスタンスは `t` を再生時刻として使う（`ModelComponent::anim_drive` 由来）。
    /// `None` または要素なしのインスタンスは静止（先頭フレームで凍結）。
    /// 毎フレーム `set_anim_time_overrides` で更新する。
    pub anim_time_overrides: Vec<Option<f32>>,

    // ── GPU メッシュレットカリング（第1弾）─────────────────────
    /// `node_prim_list`（ソート後）と同順・同長。メッシュレットを持つ非スキンプリミティブに
    /// 対してのみ Some。LOD0 の間接描画用リソース（コマンド/カウント/パラメータバッファ）と
    /// 毎フレーム再構築する BindGroup・ディスパッチ数を保持する。
    meshlet_cull: Vec<Option<MeshletCullSlot>>,
}

/// 1 プリミティブ（LOD0）のメッシュレットカリング GPU リソース。
///
/// バッファ（cmd/count/params）は容量固定で永続化し、BindGroup とディスパッチ数は
/// `prepare_meshlet_cull` が毎フレーム再構築する（GpuModel 差し替え耐性のため）。
struct MeshletCullSlot {
    /// インスタンス行列バッファ参照用のノードインデックス（`lod_node_data[0][node_idx]`）。
    node_idx:      usize,
    /// このプリミティブのメッシュレット数。
    meshlet_count: u32,
    /// draw_cmds の最大コマンド数（= meshlet_count × num_instances(最大)）。
    capacity:      u32,
    /// DrawIndexedIndirect コマンドバッファ（compute が生存分を先頭から詰める）。
    cmd_buf:       wgpu::Buffer,
    /// 生存コマンド数（atomic）。indirect count バッファ。毎フレーム 0 リセット。
    count_buf:     wgpu::Buffer,
    /// カリング compute のパラメータ UBO（視錐台・カメラ・カウント）。
    params_buf:    wgpu::Buffer,
    /// このフレームの compute BindGroup（prepare で構築）。None = 今フレーム非アクティブ。
    bind_group:    Option<wgpu::BindGroup>,
    /// このフレームのディスパッチワークグループ数（prepare で算出）。
    workgroups:    u32,
}

/// メッシュレットカリング compute のパラメータ UBO（meshlet_cull.wgsl CullParams と一致, 128B）。
#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct MeshletCullParams {
    planes:            [[f32; 4]; 6], // 96
    camera_pos:        [f32; 4],      // 16
    instance_count:    u32,           // 112
    meshlet_count:     u32,           // 116
    /// 法線コーン背面棄却を行うか（1 = 行う / 0 = 行わない）。
    /// 背面カリング（CullFace::Back）が有効なマテリアルでのみ 1。Front / None のマテリアルは
    /// 背面も描画されるため、コーン棄却すると「見えているメッシュレット」が消えてしまう。
    /// 視錐台テストはカリング面に関係なく常に有効（WGSL 側もこのフラグを見ない）。
    cone_cull_enabled: u32,           // 120
    _pad0:             u32,           // 124
}                                     // 128

/// `cone_cull_enabled` の有効値（コーン棄却を行う）。WGSL の CONE_CULL_ENABLED と一致させること。
const CONE_CULL_ON:  u32 = 1;
/// `cone_cull_enabled` の無効値（コーン棄却を行わない）。
const CONE_CULL_OFF: u32 = 0;

/// DrawIndexedIndirect のバイトサイズ（u32 × 5）。
const DRAW_INDEXED_INDIRECT_SIZE: u64 = 20;

impl InstancedModelBatch {
    pub fn new(
        device:        &wgpu::Device,
        model:         &Model,
        model_bgl:     &wgpu::BindGroupLayout,
        skin_pipeline: &SkinComputePipeline,
        joint_bgl:     &wgpu::BindGroupLayout,
        id_data_bgl:   &wgpu::BindGroupLayout,
        num_instances: u32,
    ) -> Self {
        let n = num_instances.max(1) as usize;

        // ── メッシュノードの順序テーブル ────────────────────────
        let mesh_node_indices: Vec<usize> = model.nodes.iter().enumerate()
            .filter_map(|(i, nd)| nd.mesh_index.map(|_| i))
            .collect();
        let mut node_pos_map = vec![None::<usize>; model.nodes.len()];
        for (pos, &node_idx) in mesh_node_indices.iter().enumerate() {
            node_pos_map[node_idx] = Some(pos);
        }

        // ── (node, prim) フラットリスト ──────────────────────────
        let mut node_prim_list = Vec::new();
        let mut prim_slot = 0u32;

        for &node_idx in &mesh_node_indices {
            let mesh_idx = model.nodes[node_idx].mesh_index.unwrap();
            let mesh     = &model.meshes[mesh_idx];
            for (prim_idx, prim) in mesh.primitives.iter().enumerate() {
                node_prim_list.push(NodePrimDraw {
                    node_idx,
                    mesh_idx,
                    prim_idx,
                    is_skinned:   prim.is_skinned(),
                    material_idx: prim.material_index,
                });
                prim_slot += 1;
            }
        }
        let n_prims = prim_slot;

        // ── ソート: (is_skinned, cull_face, material_idx) 昇順 ────────────
        // パイプライン切り替え（スキン有無 × カリング面の 2 軸）が連続描画で最小になるよう、
        // パイプラインを決める要素を優先キーにする。カリング面はマテリアル由来のため、
        // material_idx でソートしても Back/None が交互に並び得る（＝毎プリミティブで
        // set_pipeline が走る）。カリング面を先にキーへ入れることで同一 cull_mode が連続し、
        // model_drawer は set_pipeline を省略できる。
        // マテリアル未指定（material_idx = None）のプリミティブは default_material（Back）扱い。
        // ※ ここで見るのは埋込マテリアルの値。MaterialOverride でカリング面を変えた場合は
        //   並びが最適でなくなる（set_pipeline 回数が増える）だけで、描画結果は変わらない
        //   （描画側は GpuModel の実効マテリアルからカリング面を読むため）。
        let cull_key = |material_idx: Option<usize>| -> u8 {
            material_idx
                .and_then(|mi| model.materials.get(mi))
                .map(|m| m.cull_face)
                .unwrap_or_default()
                .index() as u8
        };
        node_prim_list.sort_by_key(|d| {
            (d.is_skinned as u8, cull_key(d.material_idx), d.material_idx.unwrap_or(usize::MAX))
        });

        // ── LOD ごとのノードインスタンスバッファ + bind group ────
        let stride = std::mem::size_of::<ModelUniform>() as u64;
        let inst_buf_size = (stride * n as u64).max(16);

        let lod_node_data: Vec<Vec<Option<(wgpu::Buffer, wgpu::BindGroup)>>> = (0..NUM_LODS)
            .map(|lod| {
                model.nodes.iter().enumerate().map(|(i, node)| {
                    if node.mesh_index.is_none() { return None; }
                    let buf = device.create_buffer(&wgpu::BufferDescriptor {
                        label:              Some(&format!("Node[{}] LOD[{}] Instance Buffer", i, lod)),
                        size:               inst_buf_size,
                        usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                        mapped_at_creation: false,
                    });
                    let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                        label:   None,
                        layout:  model_bgl,
                        entries: &[wgpu::BindGroupEntry {
                            binding:  0,
                            resource: buf.as_entire_binding(),
                        }],
                    });
                    Some((buf, bg))
                }).collect()
            })
            .collect();

        // ── モデル AABB（ローカル空間）──────────────────────────
        let (model_aabb_min, model_aabb_max) = compute_model_aabb(model);

        // ── GPU スキニングシステム ────────────────────────────
        let skin = SkinComputeSystem::new(device, model, num_instances, skin_pipeline, joint_bgl);

        // ── ID パス用インスタンス ID バッファ（per-LOD）────────
        let id_buf_size = (4 * n as u64).max(16);
        let id_data: Vec<(wgpu::Buffer, wgpu::BindGroup)> = (0..NUM_LODS)
            .map(|lod| {
                let buf = device.create_buffer(&wgpu::BufferDescriptor {
                    label:              Some(&format!("Instance ID Buffer LOD[{}]", lod)),
                    size:               id_buf_size,
                    usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                });
                let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label:  None,
                    layout: id_data_bgl,
                    entries: &[wgpu::BindGroupEntry {
                        binding:  0,
                        resource: buf.as_entire_binding(),
                    }],
                });
                (buf, bg)
            })
            .collect();
        let (lod_id_buffers, lod_id_bgs): (Vec<_>, Vec<_>) = id_data.into_iter().unzip();

        // ── メッシュレットカリング スロット（node_prim_list と同順・同長）──────
        // メッシュレットを持つ非スキンプリミティブにのみ Some。容量は
        // meshlet_count × num_instances(最大) 分のコマンドを確保する。
        let meshlet_cull: Vec<Option<MeshletCullSlot>> = node_prim_list.iter().map(|draw| {
            if draw.is_skinned { return None; }
            let prim = &model.meshes[draw.mesh_idx].primitives[draw.prim_idx];
            let ml_count = prim.meshlets.len() as u32;
            if ml_count == 0 { return None; }

            let capacity = ml_count.saturating_mul(n as u32).max(1);
            let cmd_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label:              Some("Meshlet Cull Cmd Buffer"),
                size:               capacity as u64 * DRAW_INDEXED_INDIRECT_SIZE,
                usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::INDIRECT,
                mapped_at_creation: false,
            });
            let count_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label:              Some("Meshlet Cull Count Buffer"),
                size:               4,
                usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::INDIRECT
                                  | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            let params_buf = device.create_buffer(&wgpu::BufferDescriptor {
                label:              Some("Meshlet Cull Params Buffer"),
                size:               std::mem::size_of::<MeshletCullParams>() as u64,
                usage:              wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            Some(MeshletCullSlot {
                node_idx: draw.node_idx,
                meshlet_count: ml_count,
                capacity,
                cmd_buf,
                count_buf,
                params_buf,
                bind_group: None,
                workgroups: 0,
            })
        }).collect();

        let n_mesh_nodes = mesh_node_indices.len();
        Self {
            meshlet_cull,
            lod_node_data,
            num_instances,
            lod_visible_counts: [0; NUM_LODS],
            mesh_node_indices,
            node_pos_map,
            world_mats_cache: Vec::new(),
            world_aabbs:      Vec::new(),
            n_mesh_nodes,
            node_prim_list,
            n_prims,
            model_aabb_min,
            model_aabb_max,
            skin,
            lod_id_buffers,
            lod_id_bgs,
            lod_compact_insts: vec![Vec::new(); NUM_LODS],
            dirty: true,
            anim_time_overrides: Vec::new(),
        }
    }

    /// 変換が変化したことを通知する。
    /// 次の `update()` でワールド行列・AABB が再計算される。
    pub fn mark_dirty(&mut self) { self.dirty = true; }

    /// Animator 駆動の権威時刻配列を同期する（元インスタンスインデックス順）。
    /// `update()` の前に毎フレーム呼び出す。空配列を渡すと全インスタンスが静止（先頭フレーム凍結）になる。
    pub fn set_anim_time_overrides(&mut self, overrides: &[Option<f32>]) {
        self.anim_time_overrides.clear();
        self.anim_time_overrides.extend_from_slice(overrides);
    }

    // ── GPU メッシュレットカリング（第1弾）─────────────────────

    /// このバッチにメッシュレットカリング対象プリミティブが 1 つでもあるか。
    /// 追加コストゼロ判定（対象ゼロなら compute パス生成をスキップできる）。
    pub fn has_meshlet_slots(&self) -> bool {
        self.meshlet_cull.iter().any(|s| s.is_some())
    }

    /// 毎フレームの前処理（compute パス開始前に呼ぶ）。
    ///
    /// LOD0 の可視インスタンス × メッシュレットに対し、パラメータ UBO 更新・カウント 0 リセット・
    /// compute BindGroup 構築・ディスパッチ数算出を行う。`update()` の後（`lod_visible_counts`
    /// 確定後）に呼ぶこと。Blend プリミティブ・メッシュレット非対応プリミティブはスキップする。
    ///
    /// 戻り値: このフレームに考慮したメッシュレット×インスタンス総数（PERF 表示用）。
    pub fn prepare_meshlet_cull(
        &mut self,
        queue:      &wgpu::Queue,
        device:     &wgpu::Device,
        gpu_model:  &GpuModel,
        cull_bgl:   &wgpu::BindGroupLayout,
        planes:     &[[f32; 4]; 6],
        camera_pos: [f32; 3],
    ) -> u32 {
        let visible = self.lod_visible_counts[0];
        let mut considered = 0u32;

        for (i, slot_opt) in self.meshlet_cull.iter_mut().enumerate() {
            let Some(slot) = slot_opt.as_mut() else { continue };
            // 毎フレームリセット（前フレームの活性状態を持ち越さない）。
            slot.bind_group = None;
            slot.workgroups = 0;
            if visible == 0 { continue; }

            let draw = &self.node_prim_list[i];
            // Blend は透明パスで描くため compute も走らせない（従来経路と一致）。
            if gpu_model.primitive_alpha_mode(draw.material_idx) == AlphaMode::Blend { continue; }

            // GPU プリミティブのメッシュレット記述子バッファ。
            let Some(gpu_prim) = gpu_model.meshes.get(draw.mesh_idx)
                .and_then(|m| m.primitives.get(draw.prim_idx)) else { continue };
            let Some(desc_buf) = gpu_prim.meshlet_desc_buffer.as_ref() else { continue };
            // このノードの LOD0 可視インスタンス行列バッファ。
            let Some((inst_buf, _)) = self.lod_node_data[0].get(slot.node_idx)
                .and_then(|o| o.as_ref()) else { continue };

            // 法線コーン背面棄却は「背面カリングが有効」であることが前提の最適化。
            // CullFace::None / Front のマテリアルは背面（あるいは前面）も描画されるため、
            // コーン棄却を効かせると本来見えているメッシュレットが消える。Back のときだけ有効化する。
            let cone_cull_enabled = if gpu_model.primitive_cull_face(draw.material_idx) == CullFace::Back {
                CONE_CULL_ON
            } else {
                CONE_CULL_OFF
            };

            // パラメータ UBO 更新＋カウント 0 リセット。
            let params = MeshletCullParams {
                planes:         *planes,
                camera_pos:     [camera_pos[0], camera_pos[1], camera_pos[2], 0.0],
                instance_count: visible,
                meshlet_count:  slot.meshlet_count,
                cone_cull_enabled,
                _pad0: 0,
            };
            queue.write_buffer(&slot.params_buf, 0, bytemuck::bytes_of(&params));
            queue.write_buffer(&slot.count_buf, 0, bytemuck::bytes_of(&0u32));

            // compute BindGroup を毎フレーム再構築（GpuModel 差し替え耐性）。
            let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:   Some("Meshlet Cull BG"),
                layout:  cull_bgl,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: inst_buf.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 1, resource: desc_buf.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 2, resource: slot.cmd_buf.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 3, resource: slot.count_buf.as_entire_binding() },
                    wgpu::BindGroupEntry { binding: 4, resource: slot.params_buf.as_entire_binding() },
                ],
            });
            slot.bind_group = Some(bg);

            let total = visible.saturating_mul(slot.meshlet_count);
            slot.workgroups = total.div_ceil(64);
            considered = considered.saturating_add(total);
        }
        considered
    }

    /// compute パス内で全アクティブスロットのカリングディスパッチを記録する。
    pub fn record_meshlet_cull<'p>(
        &'p self,
        pass:     &mut wgpu::ComputePass<'p>,
        pipeline: &'p MeshletCullPipeline,
    ) {
        let mut pipeline_set = false;
        for slot_opt in &self.meshlet_cull {
            let Some(slot) = slot_opt.as_ref() else { continue };
            let Some(bg) = slot.bind_group.as_ref() else { continue };
            if slot.workgroups == 0 { continue; }
            if !pipeline_set {
                pass.set_pipeline(&pipeline.pipeline);
                pipeline_set = true;
            }
            pass.set_bind_group(0, bg, &[]);
            pass.dispatch_workgroups(slot.workgroups, 1, 1);
        }
    }

    /// `node_prim_list[idx]` に対応するアクティブなメッシュレット間接描画リソースを返す。
    /// このフレームに `prepare_meshlet_cull` でアクティブ化されたスロットのみ Some。
    /// 戻り値: `(コマンドバッファ, カウントバッファ, 最大コマンド数)`。
    /// None のとき呼び出し元は従来の `draw_indexed` へフォールバックする。
    pub fn meshlet_draw(&self, idx: usize) -> Option<(&wgpu::Buffer, &wgpu::Buffer, u32)> {
        let slot = self.meshlet_cull.get(idx)?.as_ref()?;
        if slot.bind_group.is_none() || slot.workgroups == 0 { return None; }
        Some((&slot.cmd_buf, &slot.count_buf, slot.capacity))
    }

    /// 毎フレーム呼び出す更新関数。
    ///
    /// 1. `dirty` な場合のみ: rayon でワールド行列と AABB を全インスタンス分計算して
    ///    CPU キャッシュ（`world_mats_cache`・`world_aabbs`）に保存。
    /// 2. 毎フレーム: カメラ距離で LOD バケットに振り分け →
    ///    各 LOD の可視行列をノードバッファへアップロードし `lod_visible_counts` を更新。
    ///
    /// NOTE: オブジェクト単位の視錐台カリングは廃止した（画面端ポッピング・チャンク消えの
    ///       誤棄却を根絶するため）。可視範囲の間引きはメッシュレットカリング側のみが担う。
    ///       全インスタンスを常に「可視」として LOD 振り分け・アップロードする。
    pub fn update(
        &mut self,
        queue:           &wgpu::Queue,
        model:           &Model,
        root_transforms: &[[[f32; 4]; 4]],
        camera_pos:      [f32; 3],
    ) {
        let n_instances  = root_transforms.len();
        let n_mesh_nodes = self.n_mesh_nodes;
        if n_mesh_nodes == 0 { return; }
        if n_instances == 0 {
            self.dirty = false;
            self.world_mats_cache.clear();
            self.world_aabbs.clear();
            for lod in 0..NUM_LODS {
                self.lod_visible_counts[lod] = 0;
                self.lod_compact_insts[lod].clear();
            }
            return;
        }

        // ── ① ワールド行列と AABB をキャッシュ（dirty 時のみ）──────
        if self.dirty {
            self.dirty = false;

            let mut flat = vec![ModelUniform::identity(); n_instances * n_mesh_nodes];
            let node_pos_map = &self.node_pos_map;
            let root_nodes   = &model.root_nodes;

            flat.par_chunks_mut(n_mesh_nodes)
                .zip(root_transforms.par_iter())
                .for_each(|(chunk, root_t)| {
                    for &root_node in root_nodes {
                        fill_chunk(model, root_node, root_t, node_pos_map, chunk);
                    }
                });
            self.world_mats_cache = flat;

            let aabb_min = self.model_aabb_min;
            let aabb_max = self.model_aabb_max;
            self.world_aabbs = root_transforms
                .par_iter()
                .map(|mat| {
                    let (wmin, wmax) = transform_aabb(aabb_min, aabb_max, mat);
                    GpuCullData { aabb_min: wmin, _pad0: 0.0, aabb_max: wmax, _pad1: 0.0 }
                })
                .collect();
        }

        // ── ② 距離 LOD → per-LOD 可視インスタンスリスト ─
        // オブジェクト単位の視錐台カリングは行わない（全インスタンスを可視扱い）。
        // カメラからの距離のみで LOD バケットへ振り分ける。
        let mut lod_visible_insts: Vec<Vec<usize>> = vec![Vec::new(); NUM_LODS];
        let mut compact: Vec<Vec<Vec<ModelUniform>>> = (0..NUM_LODS)
            .map(|_| (0..n_mesh_nodes).map(|_| Vec::with_capacity(n_instances / NUM_LODS + 1)).collect())
            .collect();

        for (inst_idx, aabb) in self.world_aabbs.iter().enumerate() {
            let cx = (aabb.aabb_min[0] + aabb.aabb_max[0]) * 0.5;
            let cy = (aabb.aabb_min[1] + aabb.aabb_max[1]) * 0.5;
            let cz = (aabb.aabb_min[2] + aabb.aabb_max[2]) * 0.5;
            let dx = cx - camera_pos[0];
            let dy = cy - camera_pos[1];
            let dz = cz - camera_pos[2];
            let dist_sq = dx*dx + dy*dy + dz*dz;

            let lod = if dist_sq < LOD_DIST_SQ[0] { 0 }
                      else if dist_sq < LOD_DIST_SQ[1] { 1 }
                      else if dist_sq < LOD_DIST_SQ[2] { 2 }
                      else { 3 };

            lod_visible_insts[lod].push(inst_idx);
            for pos in 0..n_mesh_nodes {
                compact[lod][pos].push(self.world_mats_cache[inst_idx * n_mesh_nodes + pos]);
            }
        }

        // ── ③ 各 LOD: モデル行列・インスタンス ID をアップロード ──────
        // CPU 側コンパクトリストを保存（アウトライン描画で compact_idx を逆引きするため）
        for lod in 0..NUM_LODS {
            self.lod_compact_insts[lod] = std::mem::take(&mut lod_visible_insts[lod]);
        }

        for lod in 0..NUM_LODS {
            let visible = self.lod_compact_insts[lod].len() as u32;
            self.lod_visible_counts[lod] = visible;

            if visible > 0 {
                for (pos, &node_idx) in self.mesh_node_indices.iter().enumerate() {
                    if let Some((buf, _)) = &self.lod_node_data[lod][node_idx] {
                        queue.write_buffer(buf, 0, bytemuck::cast_slice(&compact[lod][pos]));
                    }
                }

                // ID パス用: 元インスタンスインデックスをアップロード
                let ids: Vec<u32> = self.lod_compact_insts[lod].iter().map(|&i| i as u32).collect();
                queue.write_buffer(&self.lod_id_buffers[lod], 0, bytemuck::cast_slice(&ids));
            }

            // スキンシステムへの anim_times 転送（GPU スキニング計算の入力）
            // anim_time_overrides が指定されたインスタンスは Animator 駆動の権威時刻、
            // それ以外は静止（先頭フレーム凍結）となる。
            if let Some(skin) = &self.skin {
                skin.upload_lod_times(
                    queue, lod, &self.lod_compact_insts[lod],
                    &self.anim_time_overrides,
                );
            }
        }
    }

    /// 指定インスタンスが現在フレームで可視な場合、その LOD と compact index を返す。
    /// カリングで非表示の場合は None。
    pub fn find_compact_index(&self, inst_idx: u32) -> Option<(usize, u32)> {
        for lod in 0..NUM_LODS {
            if let Some(pos) = self.lod_compact_insts[lod].iter().position(|&i| i == inst_idx as usize) {
                return Some((lod, pos as u32));
            }
        }
        None
    }

    /// 指定インスタンスのワールド空間 AABB 中心（重心近似）を返す（Phase R5）。
    ///
    /// 透明描画の背面→前面ソートで、インスタンスのカメラ距離を求めるのに使う。
    /// `world_aabbs` は dirty 時に再計算されるキャッシュで、`update()` 実行後に有効。
    /// `inst_idx` が範囲外（未計算）の場合は None。
    pub fn instance_centroid(&self, inst_idx: usize) -> Option<[f32; 3]> {
        self.world_aabbs.get(inst_idx).map(|a| [
            (a.aabb_min[0] + a.aabb_max[0]) * 0.5,
            (a.aabb_min[1] + a.aabb_max[1]) * 0.5,
            (a.aabb_min[2] + a.aabb_max[2]) * 0.5,
        ])
    }

    /// GPU スキニング コンピュートシェーダを全 LOD 分 ComputePass に積む。
    ///
    /// 呼び出し元が 1 つの ComputePass を複数アクターで共有することで
    /// begin/end pass オーバーヘッドを排除する。
    /// レンダーパスより前の ComputePass 内で呼び出すこと。
    pub fn dispatch_skin(
        &self,
        pass:     &mut wgpu::ComputePass<'_>,
        pipeline: &SkinComputePipeline,
    ) {
        if let Some(skin) = &self.skin {
            for lod in 0..NUM_LODS {
                skin.dispatch_lod(pass, pipeline, lod, self.lod_visible_counts[lod]);
            }
        }
    }

    /// LOD の頂点シェーダ用ジョイント BG（group 3）を返す。
    /// スキンなしの場合は None。
    pub fn joint_vs_bg(&self, lod: usize) -> Option<&wgpu::BindGroup> {
        self.skin.as_ref().map(|s| &s.lod_joint_vs_bgs[lod])
    }

    /// RT 影 TLAS 構築用: 全インスタンス × 全メッシュノードプリミティブ（非スキン）を
    /// `(mesh_idx, prim_idx, material_idx, 3x4 行優先ワールド変換)` でコールバックに
    /// 列挙する（Phase R8）。
    ///
    /// - カメラカリング前の全インスタンス（`num_instances`）を対象とする
    ///   （RT 影は画面外キャスターも影を落とせるのが利点）。
    /// - 各ノードのワールド行列は `world_mats_cache`（dirty 時に再計算する CPU キャッシュ）
    ///   から取得する。キャッシュは列優先（GPU 用に転置済み）で保持されているため、
    ///   TLAS が要求する 3x4 行優先アフィン変換 `[f32; 12]` へ変換して渡す。
    /// - スキンプリミティブは変形後頂点で BLAS を作らない v1 制約のため除外する。
    /// - `material_idx`（`None` = デフォルトマテリアル）をそのまま渡すのは、
    ///   「どのマテリアルが影を落とすか」の判断を呼び出し側（rt_shadow.rs）へ委ねるため。
    ///   バッチは幾何とマテリアル参照の列挙だけを担い、alpha_mode の解釈は行わない
    ///   （`GpuModel::primitive_alpha_mode()` が解釈の単一の場所）。
    /// このバッチの全インスタンスのワールド AABB を包む境界（DDGI 格子フィット用, Phase RT-GI）。
    /// world_aabbs（描画時のカリングで計算・キャッシュ）が空なら None。
    pub fn world_bounds(&self) -> Option<([f32; 3], [f32; 3])> {
        if self.world_aabbs.is_empty() { return None; }
        let mut mn = [f32::INFINITY; 3];
        let mut mx = [f32::NEG_INFINITY; 3];
        for a in &self.world_aabbs {
            for i in 0..3 {
                mn[i] = mn[i].min(a.aabb_min[i]);
                mx[i] = mx[i].max(a.aabb_max[i]);
            }
        }
        Some((mn, mx))
    }

    pub fn rt_enumerate<F: FnMut(usize, usize, Option<usize>, [f32; 12])>(&self, mut f: F) {
        // ワールド行列キャッシュが未生成（update 未実行）なら何もしない。
        if self.world_mats_cache.is_empty() || self.n_mesh_nodes == 0 { return; }
        let n_inst = self.num_instances as usize;
        for inst in 0..n_inst {
            for draw in &self.node_prim_list {
                if draw.is_skinned { continue; }
                let Some(pos) = self.node_pos_map[draw.node_idx] else { continue };
                let cache_idx = inst * self.n_mesh_nodes + pos;
                let Some(mu) = self.world_mats_cache.get(cache_idx) else { continue };
                f(
                    draw.mesh_idx,
                    draw.prim_idx,
                    draw.material_idx,
                    model_uniform_to_tlas_transform(&mu.model),
                );
            }
        }
    }
}

/// 列優先で格納されたモデル行列（`ModelUniform.model`, `model[col][row]`）を
/// TLAS が要求する 3x4 行優先アフィン変換 `[f32; 12]`（row-major, `t[row*4+col]`）へ変換する。
///
/// `ModelUniform.model` は `transpose(world)` を保持するため `model[col][row] = world[row][col]`。
/// TLAS 変換の要素は `t[row*4+col] = world[row][col] = model[col][row]`。
fn model_uniform_to_tlas_transform(model: &[[f32; 4]; 4]) -> [f32; 12] {
    let mut t = [0.0f32; 12];
    for row in 0..3 {
        for col in 0..4 {
            t[row * 4 + col] = model[col][row];
        }
    }
    t
}

/// インスタンス 1 件分のシーングラフをトラバースし、
/// メッシュノードのワールド行列をチャンクの対応列に書き込む。
fn fill_chunk(
    model:        &Model,
    node_idx:     usize,
    parent_mat:   &[[f32; 4]; 4],
    node_pos_map: &[Option<usize>],
    chunk:        &mut [ModelUniform],
) {
    let node  = &model.nodes[node_idx];
    let world = mat4_mul(parent_mat, &node.local_matrix);

    if node.mesh_index.is_some() {
        if let Some(pos) = node_pos_map[node_idx] {
            let world_t = transpose4x4(&world);
            chunk[pos]  = ModelUniform::from_matrix(world_t);
        }
    }

    for &child in &node.children {
        fill_chunk(model, child, &world, node_pos_map, chunk);
    }
}

/// 4×4 行列の転置（行優先 → 列優先変換に使用）
fn transpose4x4(m: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for i in 0..4 {
        for j in 0..4 {
            out[i][j] = m[j][i];
        }
    }
    out
}

/// 行優先 4×4 行列乗算
pub fn mat4_mul(a: &[[f32; 4]; 4], b: &[[f32; 4]; 4]) -> [[f32; 4]; 4] {
    let mut out = [[0.0f32; 4]; 4];
    for i in 0..4 {
        for j in 0..4 {
            for k in 0..4 {
                out[i][j] += a[i][k] * b[k][j];
            }
        }
    }
    out
}

// ============================================================
//  CameraBuffer
// ============================================================

/// カメラユニフォームバッファ + bind group。
pub struct CameraBuffer {
    pub buffer:     wgpu::Buffer,
    pub bind_group: wgpu::BindGroup,
}

impl CameraBuffer {
    pub fn new(device: &wgpu::Device, camera_bgl: &wgpu::BindGroupLayout) -> Self {
        let buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("Camera Uniform Buffer"),
            size:               std::mem::size_of::<CameraUniform>() as u64,
            usage:              wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("Camera BG"),
            layout: camera_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: buffer.as_entire_binding(),
            }],
        });
        Self { buffer, bind_group }
    }

    /// カメラパラメータを GPU にアップロードする。
    pub fn update(&self, queue: &wgpu::Queue, uniform: &CameraUniform) {
        queue.write_buffer(&self.buffer, 0, bytemuck::bytes_of(uniform));
    }
}

// ============================================================
//  GpuLineBatch — ラインバッチの GPU バッファ
// ============================================================

/// `LineBatch::build()` で生成した描画可能なライン頂点バッファ。
pub struct GpuLineBatch {
    pub vertex_buffer: wgpu::Buffer,
    pub vertex_count:  u32,
}

impl GpuLineBatch {
    pub fn new(device: &wgpu::Device, vertices: &[ColorVertex]) -> Self {
        let vertex_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Line Batch Vertex Buffer"),
            contents: bytemuck::cast_slice(vertices),
            usage:    wgpu::BufferUsages::VERTEX,
        });
        Self { vertex_buffer, vertex_count: vertices.len() as u32 }
    }
}

// ============================================================
//  GpuGizmoBatch — ギズモ描画用 GPU バッファ
// ============================================================

/// `GizmoBatch::build()` で生成した描画可能なギズモバッファ。
///
/// - `line_buffer`: 太線クワッド（GizmoVertex 形式）
/// - `tri_buffer` : ソリッド三角形（ColorVertex 形式）
pub struct GpuGizmoBatch {
    pub line_buffer: Option<wgpu::Buffer>,
    pub line_count:  u32,
    pub tri_buffer:  Option<wgpu::Buffer>,
    pub tri_count:   u32,
}

impl GpuGizmoBatch {
    pub fn new(device: &wgpu::Device, lines: &[GizmoVertex], tris: &[ColorVertex]) -> Self {
        let line_buffer = if lines.is_empty() { None } else {
            Some(device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label:    Some("Gizmo Line Buffer"),
                contents: bytemuck::cast_slice(lines),
                usage:    wgpu::BufferUsages::VERTEX,
            }))
        };
        let tri_buffer = if tris.is_empty() { None } else {
            Some(device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label:    Some("Gizmo Tri Buffer"),
                contents: bytemuck::cast_slice(tris),
                usage:    wgpu::BufferUsages::VERTEX,
            }))
        };
        Self {
            line_buffer,
            line_count: lines.len() as u32,
            tri_buffer,
            tri_count:  tris.len() as u32,
        }
    }
}

// ============================================================
//  テスト: メッシュレット GPU レイアウト + WGSL 検証
// ============================================================

#[cfg(test)]
mod meshlet_gpu_tests {
    use super::GpuMeshlet;

    /// GpuMeshlet は std430 想定の 48 バイト・各フィールドオフセットを固定する。
    /// meshlet_cull.wgsl の Meshlet 構造体とバイト一致していること。
    #[test]
    fn gpu_meshlet_layout_is_48_bytes() {
        assert_eq!(std::mem::size_of::<GpuMeshlet>(), 48);
        let base = std::mem::MaybeUninit::<GpuMeshlet>::uninit();
        let p = base.as_ptr();
        unsafe {
            let b = p as usize;
            assert_eq!((std::ptr::addr_of!((*p).index_offset) as usize) - b, 0);
            assert_eq!((std::ptr::addr_of!((*p).index_count)  as usize) - b, 4);
            assert_eq!((std::ptr::addr_of!((*p).center)       as usize) - b, 16);
            assert_eq!((std::ptr::addr_of!((*p).radius)       as usize) - b, 28);
            assert_eq!((std::ptr::addr_of!((*p).cone_axis)    as usize) - b, 32);
            assert_eq!((std::ptr::addr_of!((*p).cone_cutoff)  as usize) - b, 44);
        }
    }

    /// meshlet_cull.wgsl を naga で parse + validate する（自己完結）。
    #[test]
    fn meshlet_cull_shader_parses_and_validates() {
        let src = include_str!("shaders/meshlet_cull.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("meshlet_cull WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("meshlet_cull WGSL validate 失敗: {e:?}"));
    }
}

// ============================================================
//  テスト（インライン焼き込みの共有化）
// ============================================================

#[cfg(test)]
mod inline_bake_tests {
    use super::{bake_inline_material, build_material_uniform};
    use crate::engine::core::loader::model::{Material, AlphaMode, CullFace};
    use crate::engine::components::material_override::MaterialOverrideKind;

    /// 全フィールドを指定したインライン kind を作るヘルパー。
    #[allow(clippy::too_many_arguments)]
    fn inline(
        base_color: Option<[f32; 4]>, metallic: Option<f32>, roughness: Option<f32>,
        emissive: Option<[f32; 3]>, alpha_mode: Option<&str>, alpha_cutoff: Option<f32>,
        ior: Option<f32>, transmission: Option<f32>, diffuse_transmission: Option<f32>,
        mr_tex_ignore: Option<bool>, cull_face: Option<&str>,
    ) -> MaterialOverrideKind {
        MaterialOverrideKind::Inline {
            base_color, metallic, roughness, emissive,
            alpha_mode: alpha_mode.map(|s| s.to_string()),
            alpha_cutoff, ior, transmission, diffuse_transmission, mr_tex_ignore,
            cull_face: cull_face.map(|s| s.to_string()),
        }
    }

    /// bake_inline_material が各フィールドを正しく上書きする（None は埋込維持）。
    #[test]
    fn bake_overrides_fields_correctly() {
        let mut base = Material::default();
        base.base_color_factor = [1.0, 1.0, 1.0, 1.0];
        base.metallic_factor   = 0.0;
        base.cull_face         = CullFace::Back;

        let kind = inline(
            Some([0.2, 0.4, 0.6, 0.8]), Some(0.3), Some(0.7),
            Some([1.0, 0.0, 0.0]), None, None, Some(1.5), Some(0.9), Some(0.6), Some(true), Some("none"),
        );
        let eff = bake_inline_material(&base, &kind).expect("Inline は Some を返す");
        assert_eq!(eff.base_color_factor, [0.2, 0.4, 0.6, 0.8]);
        assert_eq!(eff.metallic_factor, 0.3);
        assert_eq!(eff.roughness_factor, 0.7);
        assert_eq!(eff.emissive_factor, [1.0, 0.0, 0.0]);
        assert_eq!(eff.ior, 1.5);
        assert_eq!(eff.transmission, 0.9);
        assert_eq!(eff.diffuse_transmission, 0.6, "diffuse_transmission=Some(0.6) が反映されること");
        assert!(eff.mr_tex_ignore, "mr_tex_ignore=Some(true) が反映されること");
        assert_eq!(eff.cull_face, CullFace::None);
        // alpha_mode は None なので埋込（既定）を維持する。
        assert_eq!(eff.alpha_mode, base.alpha_mode);
    }

    /// in-place 経路とフル経路は同じ `MaterialUniform` を生む。
    ///
    /// 両経路とも `bake_inline_material` → `build_material_uniform` を通るため、同じ base と
    /// 同じ kind からは 1 ビットも違わない uniform が得られることを、独立に 2 回計算して確認する
    /// （update_material_inline とフル再アップロードで値解釈がズレないことの回帰テスト）。
    #[test]
    fn in_place_and_full_paths_produce_identical_uniform() {
        let base = Material::default();
        let kind = inline(
            Some([0.1, 0.2, 0.3, 1.0]), Some(0.9), Some(0.1),
            Some([0.0, 0.5, 0.0]), Some("mask"), Some(0.5), Some(1.33), Some(0.6), Some(0.4), Some(true), Some("front"),
        );
        // フル経路の uniform（GpuMaterial::upload 相当）。
        let eff_full = bake_inline_material(&base, &kind).unwrap();
        let uni_full = build_material_uniform(&eff_full);
        // in-place 経路の uniform（update_material_inline 相当）。
        let eff_inplace = bake_inline_material(&base, &kind).unwrap();
        let uni_inplace = build_material_uniform(&eff_inplace);
        assert_eq!(
            bytemuck::bytes_of(&uni_full),
            bytemuck::bytes_of(&uni_inplace),
            "in-place とフル経路の MaterialUniform はビット一致すること",
        );
    }

    /// alpha_cutoff は Mask のときだけ uniform に反映される（それ以外は 0.0）。
    #[test]
    fn alpha_cutoff_only_applies_in_mask_mode() {
        let base = Material::default();
        // Opaque（alpha_mode 未指定）＋ cutoff 指定 → uniform では 0.0。
        let opaque = inline(None, None, None, None, None, Some(0.5), None, None, None, None, None);
        let eff_o = bake_inline_material(&base, &opaque).unwrap();
        assert_eq!(build_material_uniform(&eff_o).alpha_cutoff, 0.0);
        // Mask ＋ cutoff 指定 → uniform に反映。
        let mask = inline(None, None, None, None, Some("mask"), Some(0.5), None, None, None, None, None);
        let eff_m = bake_inline_material(&base, &mask).unwrap();
        assert_eq!(eff_m.alpha_mode, AlphaMode::Mask);
        assert_eq!(build_material_uniform(&eff_m).alpha_cutoff, 0.5);
    }

    /// MatAsset kind には bake_inline_material は適用されない（None を返す）。
    #[test]
    fn mat_asset_kind_returns_none() {
        let base = Material::default();
        let kind = MaterialOverrideKind::MatAsset { path: "assets://x.mat".to_string() };
        assert!(bake_inline_material(&base, &kind).is_none());
    }

    /// mr_tex_ignore=true の uniform は MR フラグが 1 で factor が保存される（実効値＝factor）。
    ///
    /// 実際の「MR テクスチャを無視して factor を実効値にする」乗算スキップはシェーダ
    /// （surface_gather.wgsl）で行われるため、CPU 側の焼き込みが検証できるのは
    /// 「uniform に mr_tex_ignore=1 が載り、metallic/roughness factor が素通りする」ことである。
    #[test]
    fn mr_tex_ignore_true_sets_flag_and_preserves_factors() {
        let base = Material::default();
        // metallic=0.2 / roughness=0.9 を factor に、mr_tex_ignore=true を指定。
        let kind = inline(None, Some(0.2), Some(0.9), None, None, None, None, None, None, Some(true), None);
        let eff = bake_inline_material(&base, &kind).unwrap();
        let uni = build_material_uniform(&eff);
        assert_eq!(uni.mr_tex_ignore, 1, "mr_tex_ignore=true は uniform で 1");
        // シェーダはこの factor をテクスチャ乗算せずそのまま実効値にする。
        assert_eq!(uni.metallic_factor, 0.2, "metallic factor は素通り（実効値＝factor）");
        assert_eq!(uni.roughness_factor, 0.9, "roughness factor は素通り（実効値＝factor）");
    }

    /// mr_tex_ignore=false（既定）の uniform は、この機能を追加する前とビット一致する。
    ///
    /// 旧レイアウトでは当該 4 バイト（offset 68）は `_pad0: f32 = 0.0`（＝全ビット 0）だった。
    /// トグルを u32 として転用しても false→0u は全ビット 0 なので、既定マテリアルの uniform は
    /// 追加前と 1 ビットも変わらない（後方互換＝焼き込みビット一致）。
    #[test]
    fn mr_tex_ignore_false_is_bit_compatible_with_old_layout() {
        // 既定（mr_tex_ignore=false）のマテリアルから uniform を作る。
        let eff = Material::default();
        let uni = build_material_uniform(&eff);
        assert_eq!(uni.mr_tex_ignore, 0, "既定は 0（無視しない＝従来の乗算）");
        // offset 68 の 4 バイトが全ゼロ＝旧 _pad0(f32 0.0) とビット一致であることを直接確認。
        let bytes = bytemuck::bytes_of(&uni);
        assert_eq!(&bytes[68..72], &[0u8; 4], "offset 68 の 4 バイトは旧 _pad0(0.0) とビット一致");
    }

    /// 実効アルベド（eff_avg_albedo）は「テクスチャ平均 × 現在の base_color_factor」で、
    /// `.rgb`（色相）も `.a`（影の不透明度）も override 適用後の値に追従する
    /// （Phase RT-Translucency, check C ＋ 濃い黒の影の回帰ガード）。
    ///
    /// 旧実装は baked `avg_albedo`（ロード時 factor 折込済み）の rgb を非白なら丸ごと返していたため:
    ///   - `.a` がロード時アルファに固定（実行時の Blend 化・アルファ変更が影に出ない）、
    ///   - `.rgb` の色相がロード時 factor に固定（インライン編集の新色が影に出ない）
    /// という 2 退行があった。特に黒ベースの駒（factor=[0,0,0]）をシアンへ編集すると影だけ濃い黒になった。
    #[test]
    fn eff_avg_albedo_tracks_current_base_color_factor() {
        use super::eff_avg_albedo;

        // ── テクスチャ付き想定: tex 平均は非白。現 factor（アルファ 0.3・色 白）を掛ける ──
        let mut mat = Material::default();
        mat.base_color_tex_avg = [0.2, 0.1, 0.05];      // テクスチャの色味（factor 抜き）
        mat.base_color_factor  = [1.0, 1.0, 1.0, 0.3];  // 実行時に Blend 化＋アルファを 0.3 へ
        let out = eff_avg_albedo(&mat);
        assert_eq!([out[0], out[1], out[2]], [0.2, 0.1, 0.05], ".rgb = tex 平均 × factor.rgb（白 factor なので tex 平均）");
        assert_eq!(out[3], 0.3, ".a は現在の base_color_factor.a（baked のロード時アルファではない）");

        // ── テクスチャ無し想定: tex 平均は白なので base_color_factor そのもの ──
        let mut untex = Material::default();
        untex.base_color_tex_avg = [1.0, 1.0, 1.0];     // テクスチャ無し
        untex.base_color_factor  = [0.1, 0.2, 0.3, 0.5];
        let out2 = eff_avg_albedo(&untex);
        assert_eq!(out2, [0.1, 0.2, 0.3, 0.5], "テクスチャ無しは rgb/a とも base_color_factor");

        // ── 症状 1 の回帰ガード: 黒ベースの駒（元 factor=[0,0,0]）をシアンのガラスへ編集 ──
        // インライン override 後の実効マテリアル eff は base_color_factor=シアンを持つ。テクスチャは
        // 無い（tex 平均=白）ので実効アルベドはシアンでなければならない。旧実装は baked=[0,0,0] を
        // 非白として採用し影 tint を黒にしていた（表面はシアンなのに影だけ濃い黒＝症状 1）。
        let mut cyan_glass = Material::default();
        cyan_glass.base_color_tex_avg = [1.0, 1.0, 1.0]; // テクスチャ無し（白）
        cyan_glass.base_color_factor  = [0.0, 1.0, 1.0, 1.0]; // シアン・α=1
        let out3 = eff_avg_albedo(&cyan_glass);
        assert_eq!(out3, [0.0, 1.0, 1.0, 1.0], "シアンのガラスの実効アルベド＝シアン（影 tint がシアンになる根拠）");
        // 透過モデル T=(1-α)+α·tr·albedo に α=1,tr=1 を入れると T=albedo=シアン（濃い黒でない）ことを確認。
        let (a, tr) = (out3[3], 1.0f32);
        let t = [
            (1.0 - a) + a * tr * out3[0],
            (1.0 - a) + a * tr * out3[1],
            (1.0 - a) + a * tr * out3[2],
        ];
        assert_eq!(t, [0.0, 1.0, 1.0], "α=1,tr=1 の透過率 T はシアン（R を遮り G/B を通す色影。黒ではない）");
    }
}
