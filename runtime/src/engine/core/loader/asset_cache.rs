// ============================================================
//  asset_cache.rs — モデル/テクスチャの派生データキャッシュ
//
//  【役割】
//  glTF/OBJ のパース結果（Model）と、そこに埋め込まれたテクスチャを
//  「即ロード可能な形式」（テクスチャは BC 圧縮 + ミップ生成済み）で
//  ディスクにキャッシュし、2 回目以降のロードを大幅に高速化する。
//
//  ・初回ロード:   パース → 画像デコード → ミップ生成 → BC 圧縮 → キャッシュ書き出し
//  ・2 回目以降:   キャッシュ（bincode）を読むだけ。デコード・パース・LOD 生成を全てスキップ。
//
//  【キャッシュ配置】
//  `{assets_root}/../cache/`（assets 外。プロジェクト設定・PAK には含めない）
//
//  【検証】
//  ヘッダに元ファイルの mtime + サイズ + フォーマットバージョン + BC 使用フラグを格納。
//  いずれか不一致ならキャッシュ無効とみなし元ファイルから再生成する。
//  破損キャッシュも読み取り失敗として扱い、必ず元ファイル経路へフォールバックする
//  （キャッシュ関連の失敗はすべて警告ログ止まりで、クラッシュしない）。
// ============================================================

use std::hash::{Hash, Hasher};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::SystemTime;

use rayon::prelude::*;

use super::model::{
    CachedTexFormat, Material, Model, TextureData, TextureSource, TextureUsage,
};

// ============================================================
//  定数・グローバル状態
// ============================================================

/// キャッシュフォーマットのバージョン。
///
/// インポータのロジック（頂点レイアウト・LOD 生成・座標変換・ミップ生成など）や
/// キャッシュのバイナリ表現を変更したら必ずインクリメントすること。
/// これによりバージョン不一致の古いキャッシュは自動的に無視され再生成される。
pub const CACHE_FORMAT_VERSION: u32 = 1;

/// モデルキャッシュファイルのマジック（8 バイト）。
const MODEL_MAGIC: &[u8; 8] = b"SEEDMDL\0";

/// キャッシュ用サブディレクトリ名（`{assets_root}/../cache`）。
const CACHE_DIR_NAME: &str = "cache";

/// GPU が BC 圧縮（TEXTURE_COMPRESSION_BC）に対応しているか。
/// レンダラ初期化時に `set_bc_supported` で設定する。デフォルトは false（=非圧縮 RGBA）。
static BC_SUPPORTED: AtomicBool = AtomicBool::new(false);

/// GPU の BC 圧縮対応可否を設定する（レンダラのデバイス生成時に一度呼ぶ）。
pub fn set_bc_supported(v: bool) {
    BC_SUPPORTED.store(v, Ordering::Relaxed);
}

/// GPU が BC 圧縮に対応しているか。
pub fn bc_supported() -> bool {
    BC_SUPPORTED.load(Ordering::Relaxed)
}

// ============================================================
//  キャッシュパス解決
// ============================================================

/// キャッシュディレクトリ `{assets_root}/../cache` を返す（未初期化なら None）。
fn cache_dir() -> Option<PathBuf> {
    let root = crate::engine::asset_fs::root()?;
    let parent = root.parent()?;
    Some(parent.join(CACHE_DIR_NAME))
}

/// 元アセットの解決済み絶対パスから、モデルキャッシュファイルのパスを求める。
///
/// ファイル名はパス全体のハッシュ + 元ファイル名 stem で構成し、
/// 別ディレクトリの同名ファイル同士が衝突しないようにする。
fn model_cache_path(resolved_src: &Path) -> Option<PathBuf> {
    let dir = cache_dir()?;
    // std の DefaultHasher は固定キーの SipHash のため実行間で決定的。
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    resolved_src.to_string_lossy().hash(&mut hasher);
    let hash = hasher.finish();
    let stem = resolved_src.file_stem().and_then(|s| s.to_str()).unwrap_or("model");
    Some(dir.join(format!("{hash:016x}_{stem}.smdl")))
}

/// 元ファイルの (mtime 秒, mtime ナノ秒, サイズ) を取得する。
/// 取得できない場合（PAK 内のみ・権限など）は None を返し、キャッシュを無効化する。
fn source_stamp(resolved_src: &Path) -> Option<(u64, u32, u64)> {
    let meta = std::fs::metadata(resolved_src).ok()?;
    let size = meta.len();
    let mtime = meta.modified().ok()?;
    let dur = mtime.duration_since(SystemTime::UNIX_EPOCH).ok()?;
    Some((dur.as_secs(), dur.subsec_nanos(), size))
}

// ============================================================
//  ヘッダの読み書き（固定長・手動シリアライズ）
// ============================================================

/// ヘッダ長 = magic(8) + version(4) + flags(4) + mtime_secs(8) + mtime_nanos(4) + size(8) = 36 バイト。
const HEADER_LEN: usize = 8 + 4 + 4 + 8 + 4 + 8;

/// flags のビット 0: このキャッシュが BC 圧縮テクスチャを含むか。
const FLAG_BC_USED: u32 = 1 << 0;

/// ヘッダをバイト列に書き出す。
fn write_header(buf: &mut Vec<u8>, stamp: (u64, u32, u64), bc_used: bool) {
    buf.extend_from_slice(MODEL_MAGIC);
    buf.extend_from_slice(&CACHE_FORMAT_VERSION.to_le_bytes());
    let flags = if bc_used { FLAG_BC_USED } else { 0 };
    buf.extend_from_slice(&flags.to_le_bytes());
    buf.extend_from_slice(&stamp.0.to_le_bytes()); // mtime secs
    buf.extend_from_slice(&stamp.1.to_le_bytes()); // mtime nanos
    buf.extend_from_slice(&stamp.2.to_le_bytes()); // size
}

/// ヘッダを検証する。マジック・バージョン・mtime・サイズ・BC フラグが
/// すべて期待値と一致した場合のみ Some(本体開始オフセット) を返す。
fn validate_header(data: &[u8], expect_stamp: (u64, u32, u64)) -> Option<usize> {
    if data.len() < HEADER_LEN { return None; }
    if &data[0..8] != MODEL_MAGIC { return None; }

    let version = u32::from_le_bytes(data[8..12].try_into().ok()?);
    if version != CACHE_FORMAT_VERSION { return None; }

    let flags = u32::from_le_bytes(data[12..16].try_into().ok()?);
    let bc_used = flags & FLAG_BC_USED != 0;
    // GPU の BC 対応状態が変わった場合はフォーマットが噛み合わないため無効化する。
    if bc_used != bc_supported() { return None; }

    let mtime_secs  = u64::from_le_bytes(data[16..24].try_into().ok()?);
    let mtime_nanos = u32::from_le_bytes(data[24..28].try_into().ok()?);
    let size        = u64::from_le_bytes(data[28..36].try_into().ok()?);
    if (mtime_secs, mtime_nanos, size) != expect_stamp { return None; }

    Some(HEADER_LEN)
}

// ============================================================
//  モデルキャッシュ: 読み込み
// ============================================================

/// 有効なモデルキャッシュがあれば読み込んで返す。
///
/// 元ファイルパス（`assets://` 仮想パスまたは絶対パス）を受け取り、
/// キャッシュが存在し検証を通ればデシリアライズした Model を返す。
/// キャッシュなし・古い・破損の場合は None（呼び出し元は元ファイルからロードする）。
pub fn try_load_model(src: &Path) -> Option<Model> {
    let resolved = resolve_src(src);
    let stamp = source_stamp(&resolved)?;
    let cache_path = model_cache_path(&resolved)?;

    let data = std::fs::read(&cache_path).ok()?;
    let body_start = validate_header(&data, stamp)?;

    // bincode デシリアライズ。破損していても panic せず None を返す。
    match bincode::deserialize::<Model>(&data[body_start..]) {
        Ok(model) => Some(model),
        Err(e) => {
            eprintln!("[SEED cache] モデルキャッシュのデシリアライズに失敗（再生成します）: {cache_path:?} err={e}");
            None
        }
    }
}

// ============================================================
//  モデルキャッシュ: 書き出し
// ============================================================

/// Model をキャッシュに書き出す（ベストエフォート。失敗は警告のみ）。
///
/// 呼び出し前に `process_model_textures` でテクスチャを Ready 形式に変換しておくこと。
pub fn store_model(src: &Path, model: &Model) {
    let resolved = resolve_src(src);
    let Some(stamp) = source_stamp(&resolved) else { return; };
    let Some(cache_path) = model_cache_path(&resolved) else { return; };
    let Some(dir) = cache_dir() else { return; };

    if let Err(e) = std::fs::create_dir_all(&dir) {
        eprintln!("[SEED cache] キャッシュディレクトリ作成失敗（キャッシュ無効）: {dir:?} err={e}");
        return;
    }

    // このモデルが BC テクスチャを含むか（= 現在の GPU 能力）をフラグに記録する。
    let bc_used = bc_supported();

    let mut buf = Vec::new();
    write_header(&mut buf, stamp, bc_used);
    match bincode::serialize(model) {
        Ok(blob) => buf.extend_from_slice(&blob),
        Err(e) => {
            eprintln!("[SEED cache] モデルの直列化に失敗（キャッシュ書き込み中止）: err={e}");
            return;
        }
    }

    // 一時ファイルに書いてからリネームし、途中失敗による破損キャッシュを避ける。
    let tmp_path = cache_path.with_extension("smdl.tmp");
    if let Err(e) = std::fs::write(&tmp_path, &buf) {
        eprintln!("[SEED cache] キャッシュ書き込み失敗: {tmp_path:?} err={e}");
        return;
    }
    if let Err(e) = std::fs::rename(&tmp_path, &cache_path) {
        eprintln!("[SEED cache] キャッシュのリネーム失敗: {cache_path:?} err={e}");
        let _ = std::fs::remove_file(&tmp_path);
    }
}

/// `assets://` 仮想パスなら実ファイルパスに解決する。絶対パスはそのまま。
fn resolve_src(src: &Path) -> PathBuf {
    let s = src.to_string_lossy();
    if crate::engine::asset_fs::is_virtual(&s) {
        crate::engine::asset_fs::resolve(&s)
    } else {
        src.to_path_buf()
    }
}

// ============================================================
//  テクスチャの用途分類
// ============================================================

/// 各テクスチャ（`model.textures`）の用途を、マテリアル参照から分類する。
///
/// 1 枚が複数スロットで参照される場合の優先順位は
/// NormalMap > ColorSrgb > LinearData（法線を誤って sRGB 化しないことを最優先）。
/// どのマテリアルからも参照されないテクスチャは `linear` フラグで振り分ける。
fn classify_textures(model: &Model) -> Vec<TextureUsage> {
    let n = model.textures.len();
    // None = 未参照。Some(usage) = 確定済み。
    let mut usage: Vec<Option<TextureUsage>> = vec![None; n];

    // 用途を格上げ方向にのみ更新するヘルパー（NormalMap が最優先）。
    fn assign(slot: &mut Option<TextureUsage>, u: TextureUsage) {
        let rank = |x: TextureUsage| match x {
            TextureUsage::NormalMap  => 2,
            TextureUsage::ColorSrgb  => 1,
            TextureUsage::LinearData => 0,
        };
        match slot {
            Some(cur) if rank(*cur) >= rank(u) => {}
            _ => *slot = Some(u),
        }
    }

    let mark = |usage: &mut Vec<Option<TextureUsage>>, idx: usize, u: TextureUsage| {
        if idx < usage.len() { assign(&mut usage[idx], u); }
    };

    for mat in &model.materials {
        classify_one_material(mat, &mut |idx, u| mark(&mut usage, idx, u));
    }

    // 未参照テクスチャは linear フラグから推定
    (0..n).map(|i| {
        usage[i].unwrap_or_else(|| {
            if model.textures[i].linear { TextureUsage::LinearData } else { TextureUsage::ColorSrgb }
        })
    }).collect()
}

/// 1 マテリアルの各テクスチャスロットを走査し、用途を通知する。
fn classify_one_material(mat: &Material, mark: &mut impl FnMut(usize, TextureUsage)) {
    if let Some(t) = &mat.base_color_texture         { mark(t.texture_index, TextureUsage::ColorSrgb); }
    if let Some(t) = &mat.emissive_texture           { mark(t.texture_index, TextureUsage::ColorSrgb); }
    if let Some(t) = &mat.normal_texture             { mark(t.texture_index, TextureUsage::NormalMap); }
    if let Some(t) = &mat.metallic_roughness_texture { mark(t.texture_index, TextureUsage::LinearData); }
    if let Some(t) = &mat.occlusion_texture          { mark(t.texture_index, TextureUsage::LinearData); }
}

// ============================================================
//  テクスチャ処理: Embedded → Ready（ミップ生成 + BC 圧縮）
// ============================================================

/// モデル内の埋め込みテクスチャ（`TextureSource::Embedded`）を、
/// GPU 即アップロード可能な `TextureSource::Ready`（ミップ + BC 圧縮 or RGBA）へ変換する。
///
/// これによりキャッシュには圧縮済み・ミップ済みデータが保存され、
/// 2 回目以降は画像デコードもミップ生成も BC 圧縮も不要になる。
/// `FilePath` テクスチャ（OBJ 外部）はそのまま残し、ロード時に実ファイルから読む。
///
/// 戻り値: 圧縮に費やした概算バイト数（元 RGBA 合計。ログ用）。
pub fn process_model_textures(model: &mut Model) -> usize {
    let usages = classify_textures(model);
    let bc = bc_supported();
    let mut total_src_bytes = 0usize;

    // テクスチャ間は独立なので rayon で並列処理する（BC 圧縮が支配的なため効果大）。
    let converted: Vec<Option<(TextureSource, usize)>> = model.textures
        .par_iter()
        .zip(usages.par_iter())
        .map(|(td, &usage)| {
            if let TextureSource::Embedded { width, height, pixels } = &td.source {
                let ready = build_ready_texture(*width, *height, pixels, usage, bc);
                let src_bytes = pixels.len();
                Some((ready, src_bytes))
            } else {
                None
            }
        })
        .collect();

    for (td, conv) in model.textures.iter_mut().zip(converted.into_iter()) {
        if let Some((ready, src_bytes)) = conv {
            td.source = ready;
            total_src_bytes += src_bytes;
        }
    }
    total_src_bytes
}

/// RGBA8 ピクセルからミップチェーンを生成し、用途に応じて BC 圧縮した `Ready` を作る。
fn build_ready_texture(
    width:  u32,
    height: u32,
    pixels: &[u8],
    usage:  TextureUsage,
    bc:     bool,
) -> TextureSource {
    // ── ミップチェーン（RGBA8）を生成 ────────────────────────────
    let mip_chain = generate_mip_chain(width, height, pixels);

    // BC ブロックは 4×4 テクセル単位。ベース解像度が 4 の倍数でない
    // テクスチャは一部バックエンドで圧縮テクスチャ生成が弾かれる可能性があるため、
    // BC 圧縮を諦めて非圧縮 RGBA ミップとしてキャッシュする（デコードスキップの恩恵は維持）。
    let bc_ok = bc && width % 4 == 0 && height % 4 == 0 && width >= 4 && height >= 4;

    // ── BC 非対応 or 非ブロック整列: 非圧縮 RGBA8 ミップとして保存 ──
    if !bc_ok {
        let format = match usage {
            TextureUsage::ColorSrgb => CachedTexFormat::Rgba8UnormSrgb,
            _                       => CachedTexFormat::Rgba8Unorm,
        };
        let mips = mip_chain.into_iter().map(|m| m.pixels).collect();
        return TextureSource::Ready { format, width, height, mips };
    }

    // ── BC 対応: 用途別フォーマットで各ミップを圧縮 ──────────────
    let format = match usage {
        TextureUsage::ColorSrgb  => CachedTexFormat::Bc3RgbaUnormSrgb,
        TextureUsage::NormalMap  => CachedTexFormat::Bc5RgUnorm,
        TextureUsage::LinearData => CachedTexFormat::Bc3RgbaUnorm,
    };

    let mips: Vec<Vec<u8>> = mip_chain.iter()
        .map(|m| compress_mip(m.width, m.height, &m.pixels, format))
        .collect();

    TextureSource::Ready { format, width, height, mips }
}

// ============================================================
//  ミップ生成（box filter）
// ============================================================

/// 1 ミップレベルの RGBA8 データ。
struct MipLevel {
    width:  u32,
    height: u32,
    pixels: Vec<u8>, // 長さ = width*height*4
}

/// RGBA8 画像から 1×1 までのミップチェーンを box filter で生成する。
/// `mip[0]` が元解像度。
fn generate_mip_chain(width: u32, height: u32, pixels: &[u8]) -> Vec<MipLevel> {
    let mut chain = Vec::new();
    // 入力ピクセル長が不足している場合は安全側に倒して 1 レベルのみ返す。
    if (width as usize) * (height as usize) * 4 != pixels.len() || width == 0 || height == 0 {
        chain.push(MipLevel { width: width.max(1), height: height.max(1), pixels: pixels.to_vec() });
        return chain;
    }

    chain.push(MipLevel { width, height, pixels: pixels.to_vec() });

    let (mut w, mut h) = (width, height);
    while w > 1 || h > 1 {
        let prev = chain.last().unwrap();
        let nw = (w / 2).max(1);
        let nh = (h / 2).max(1);
        let down = downsample_box(prev.width, prev.height, &prev.pixels, nw, nh);
        chain.push(MipLevel { width: nw, height: nh, pixels: down });
        w = nw;
        h = nh;
    }
    chain
}

/// 2×2 平均（box filter）で RGBA8 を縮小する。奇数辺はクランプで対応。
fn downsample_box(sw: u32, sh: u32, src: &[u8], dw: u32, dh: u32) -> Vec<u8> {
    let mut out = vec![0u8; (dw as usize) * (dh as usize) * 4];
    let sample = |x: u32, y: u32, c: usize| -> u32 {
        let xi = x.min(sw - 1) as usize;
        let yi = y.min(sh - 1) as usize;
        src[(yi * sw as usize + xi) * 4 + c] as u32
    };
    for dy in 0..dh {
        for dx in 0..dw {
            let sx = dx * 2;
            let sy = dy * 2;
            for c in 0..4 {
                let sum = sample(sx, sy, c) + sample(sx + 1, sy, c)
                        + sample(sx, sy + 1, c) + sample(sx + 1, sy + 1, c);
                out[((dy * dw + dx) as usize) * 4 + c] = ((sum + 2) / 4) as u8;
            }
        }
    }
    out
}

// ============================================================
//  BC 圧縮（texpresso — 純 Rust）
// ============================================================

/// `CachedTexFormat` を texpresso の `Format` に対応付ける。
///
/// texpresso は RGBA8 入力を受け取り、BC4 は R チャンネル、BC5 は RG チャンネルを
/// 内部で使用する。非圧縮フォーマットには対応しないため None を返す。
fn texpresso_format(format: CachedTexFormat) -> Option<texpresso::Format> {
    use texpresso::Format as F;
    match format {
        CachedTexFormat::Bc3RgbaUnormSrgb | CachedTexFormat::Bc3RgbaUnorm => Some(F::Bc3),
        CachedTexFormat::Bc1RgbaUnorm                                     => Some(F::Bc1),
        CachedTexFormat::Bc5RgUnorm                                       => Some(F::Bc5),
        CachedTexFormat::Bc4RUnorm                                        => Some(F::Bc4),
        // BC6H は texpresso 非対応（将来 HDR パイプライン導入時に別経路で対応）。
        CachedTexFormat::Bc6hRgbUfloat                                    => None,
        CachedTexFormat::Rgba8Unorm | CachedTexFormat::Rgba8UnormSrgb     => None,
    }
}

/// 1 ミップ（RGBA8）を指定フォーマットで BC 圧縮し、ブロック列を返す。
///
/// texpresso は幅・高さが 4 の倍数でなくても内部でエッジブロックを処理するため、
/// パディングは不要。出力ブロック数 `ceil(w/4)*ceil(h/4)` は wgpu の期待と一致する。
fn compress_mip(width: u32, height: u32, rgba: &[u8], format: CachedTexFormat) -> Vec<u8> {
    let Some(tp) = texpresso_format(format) else {
        // 到達しない想定（build_ready_texture が BC 対応フォーマットのみ渡す）。
        // 安全側で元 RGBA をそのまま返す。
        return rgba.to_vec();
    };
    let (w, h) = (width as usize, height as usize);
    let size = tp.compressed_size(w, h);
    let mut out = vec![0u8; size];
    tp.compress(rgba, w, h, texpresso::Params::default(), &mut out);
    out
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// ミップチェーンのレベル数と各レベルのピクセルバイト長を検証する。
    #[test]
    fn mip_chain_dims_and_len() {
        // 8×8 → 8,4,2,1 の 4 レベル
        let pixels = vec![200u8; 8 * 8 * 4];
        let chain = generate_mip_chain(8, 8, &pixels);
        assert_eq!(chain.len(), 4);
        let dims: Vec<(u32, u32)> = chain.iter().map(|m| (m.width, m.height)).collect();
        assert_eq!(dims, vec![(8, 8), (4, 4), (2, 2), (1, 1)]);
        for m in &chain {
            assert_eq!(m.pixels.len(), (m.width * m.height * 4) as usize);
        }
    }

    /// 単色画像の box filter 縮小は同じ色を保つ。
    #[test]
    fn downsample_preserves_solid_color() {
        let src = vec![[10u8, 20, 30, 40]; 2 * 2].concat();
        let out = downsample_box(2, 2, &src, 1, 1);
        assert_eq!(out, vec![10, 20, 30, 40]);
    }

    /// BC 圧縮の出力サイズが wgpu 期待のブロック数と一致する（4 の倍数・非倍数の両方）。
    #[test]
    fn compressed_size_matches_block_layout() {
        // Bc3: 16B/ブロック
        let px = vec![128u8; 8 * 8 * 4];
        let out = compress_mip(8, 8, &px, CachedTexFormat::Bc3RgbaUnormSrgb);
        assert_eq!(out.len(), (8 / 4) * (8 / 4) * 16);
        // 非 4 倍数（6×6 → ceil=2 ブロック）でも破綻しない
        let px6 = vec![128u8; 6 * 6 * 4];
        let out6 = compress_mip(6, 6, &px6, CachedTexFormat::Bc3RgbaUnorm);
        assert_eq!(out6.len(), 2 * 2 * 16);
        // Bc5: 16B/ブロック, Bc1: 8B/ブロック
        let out5 = compress_mip(4, 4, &vec![128u8; 4 * 4 * 4], CachedTexFormat::Bc5RgUnorm);
        assert_eq!(out5.len(), 16);
        let out1 = compress_mip(4, 4, &vec![128u8; 4 * 4 * 4], CachedTexFormat::Bc1RgbaUnorm);
        assert_eq!(out1.len(), 8);
    }

    /// ヘッダの書き込み → 検証のラウンドトリップ。
    /// 一致で Some(HEADER_LEN)、mtime/サイズ/バージョン不一致で None を返す。
    #[test]
    fn header_roundtrip_and_mismatch() {
        let stamp = (1234u64, 5678u32, 9999u64);
        // bc_used = bc_supported()（デフォルト false）に合わせる
        let bc = bc_supported();

        let mut buf = Vec::new();
        write_header(&mut buf, stamp, bc);
        assert_eq!(buf.len(), HEADER_LEN);
        assert_eq!(validate_header(&buf, stamp), Some(HEADER_LEN));

        // mtime 不一致 → None
        assert_eq!(validate_header(&buf, (1234, 5679, 9999)), None);
        // サイズ不一致 → None
        assert_eq!(validate_header(&buf, (1234, 5678, 10000)), None);

        // バージョンバイトを壊す → None
        let mut bad = buf.clone();
        bad[8] = bad[8].wrapping_add(1);
        assert_eq!(validate_header(&bad, stamp), None);

        // マジックを壊す → None
        let mut bad_magic = buf.clone();
        bad_magic[0] = 0;
        assert_eq!(validate_header(&bad_magic, stamp), None);

        // 長さ不足 → None
        assert_eq!(validate_header(&buf[..HEADER_LEN - 1], stamp), None);
    }

    /// テクスチャ用途分類: 法線が最優先で確定される。
    #[test]
    fn classify_prefers_normal_map() {
        use super::super::model::*;
        let tex = |linear: bool| TextureData {
            name: None,
            source: TextureSource::Embedded { width: 1, height: 1, pixels: vec![0; 4] },
            sampler: SamplerData::default(),
            linear,
        };
        let mut mat = Material::default();
        // テクスチャ 0 を base_color(sRGB) と normal(NormalMap) の両方で参照
        mat.base_color_texture = Some(TextureInfo { texture_index: 0, tex_coord_set: 0 });
        mat.normal_texture = Some(NormalTextureInfo { texture_index: 0, tex_coord_set: 0, scale: 1.0 });
        let model = Model {
            name: String::new(), nodes: vec![], root_nodes: vec![], meshes: vec![],
            materials: vec![mat], textures: vec![tex(true)], animations: vec![], skins: vec![],
        };
        let usages = classify_textures(&model);
        assert_eq!(usages[0], TextureUsage::NormalMap);
    }
}
