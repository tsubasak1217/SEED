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
use std::time::{Instant, SystemTime};

use rayon::prelude::*;

use super::model::{
    CachedTexFormat, Material, MeshletDesc, Model, SkinVertex, TextureSource, TextureUsage, Vertex,
};

// ============================================================
//  定数・グローバル状態
// ============================================================

/// キャッシュフォーマットのバージョン。
///
/// インポータのロジック（頂点レイアウト・LOD 生成・座標変換・ミップ生成など）や
/// キャッシュのバイナリ表現を変更したら必ずインクリメントすること。
/// これによりバージョン不一致の古いキャッシュは自動的に無視され再生成される。
///
/// v2: ミップを物理サイズ（4 の倍数）で圧縮するよう変更。
///     巨大バイト列（頂点・インデックス・ミップ）を bincode から分離し
///     生ブロブ領域として格納する形式に変更（デシリアライズの大幅高速化）。
/// v3: LOD0 メッシュレット（GPU カリング第1弾）を追加。Primitive に meshlet 記述子
///     （bincode メタ）＋ meshlet_vertices(u32 blob) ＋ meshlet_triangles(u8 blob) を格納。
///     バージョン更新により旧 v2 キャッシュは自動再生成される。
/// v4: メッシュレット記述子（`MeshletDesc`, Pod 48B）も bincode メタから生ブロブ領域へ移動。
///     Sponza 級（数万記述子）では debug ビルドの serde 要素単位シリアライズが
///     opt-level 0 のモノモーフ化コードで実行され遅い（キャッシュヒット 115 秒問題と同根）ため。
///     作りかけ/遅い形式の v3 キャッシュもバージョン不一致で自動再生成される。
/// v5: テクスチャ処理のストリーミング化（ピークメモリ削減）。
///     glTF 画像を遅延デコード（`TextureSource::EncodedBytes` 追加）にし、
///     1 枚ずつ「デコード → ミップ → BC 圧縮 → 即 drop」の有界並列で処理。
///     外部ファイル参照（FilePath）テクスチャもキャッシュに含まれるようになった。
///     ファイル書き出しも全体バッファを作らず逐次 write に変更。
/// v6: BC1/BC3 圧縮を ClusterFit（texpresso デフォルト。4K 1 枚 8〜11 分）から
///     RangeFit（高速・品質低-中）へ変更。v5 の低速形式で焼かれた途中キャッシュを
///     バージョン不一致で自動再生成させるためインクリメント。
pub const CACHE_FORMAT_VERSION: u32 = 6;

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
//  ブロブスロット走査 — 巨大バイト列を bincode から分離するための機構
//
//  【背景】
//  bincode(serde derive) は `Vec<Vertex>` や `Vec<u8>` を要素単位で
//  シリアライズ/デシリアライズするため、debug ビルドでは Sponza 級の
//  数百 MB のデータに対して数十秒〜100 秒超かかる（実測 115 秒）。
//
//  【対策】
//  Model 内の「巨大な Pod 配列」（頂点・スキン頂点・インデックス・LOD
//  インデックス・テクスチャミップ/ピクセル）を bincode の対象から外し、
//  キャッシュファイル内の生ブロブ領域に直接格納する。
//  bincode は小さな構造メタデータ（名前・行列・マテリアル定数・アニメ等）
//  のみを扱うため、ヒット時は「File::read + memcpy」とほぼ等速になる。
//
//  【走査順序の決定性】
//  extract（書き出し）と inject（読み込み）は `visit_blob_slots` の
//  同一トラバーサルを共有するため、ブロブの順序は常に一致する。
// ============================================================

/// Model 内の 1 ブロブスロット（巨大配列フィールドへの可変参照）。
enum BlobSlot<'a> {
    /// テクスチャのバイト列（Ready のミップ 1 枚 / Embedded のピクセル）
    Bytes(&'a mut Vec<u8>),
    /// プリミティブの頂点配列
    Verts(&'a mut Vec<Vertex>),
    /// スキニング頂点配列
    Skins(&'a mut Vec<SkinVertex>),
    /// インデックス配列（LOD0 / LOD1..3）
    U32s(&'a mut Vec<u32>),
    /// メッシュレット記述子配列（v4: Pod 48B、生ブロブとしてゼロコピー格納）
    Meshlets(&'a mut Vec<MeshletDesc>),
}

/// Model 内の全ブロブスロットを決定的な順序で訪問する。
///
/// 順序: 全テクスチャ（Ready の各ミップ / Embedded のピクセル）
///     → 全メッシュ × 全プリミティブ（vertices, skin_vertices, indices, 各 lod_indices）。
/// `FilePath` テクスチャはピクセルを持たないためスロットなし。
fn visit_blob_slots(model: &mut Model, f: &mut impl FnMut(BlobSlot<'_>)) {
    for td in &mut model.textures {
        match &mut td.source {
            TextureSource::Ready { mips, .. } => {
                for mip in mips.iter_mut() {
                    f(BlobSlot::Bytes(mip));
                }
            }
            TextureSource::Embedded { pixels, .. } => f(BlobSlot::Bytes(pixels)),
            // 未デコード画像（通常はテクスチャ処理で Ready 化されるが、
            // デコード失敗時などに残った場合もブロブとして格納する）
            TextureSource::EncodedBytes { bytes } => f(BlobSlot::Bytes(bytes)),
            TextureSource::FilePath(_) => {}
        }
    }
    for mesh in &mut model.meshes {
        for prim in &mut mesh.primitives {
            f(BlobSlot::Verts(&mut prim.vertices));
            f(BlobSlot::Skins(&mut prim.skin_vertices));
            f(BlobSlot::U32s(&mut prim.indices));
            for lod in prim.lod_indices.iter_mut() {
                f(BlobSlot::U32s(lod));
            }
            // メッシュレット（LOD0, v3/v4）。連結配列＋記述子（Pod）を全てブロブ化し、
            // bincode メタにはメッシュレット関連の大配列を一切残さない。
            f(BlobSlot::U32s(&mut prim.meshlet_vertices));
            f(BlobSlot::Bytes(&mut prim.meshlet_triangles));
            f(BlobSlot::Meshlets(&mut prim.meshlets));
        }
    }
}

/// 取り出したブロブの所有権付き表現（書き出し後に元へ戻すために保持する）。
enum OwnedBlob {
    Bytes(Vec<u8>),
    Verts(Vec<Vertex>),
    Skins(Vec<SkinVertex>),
    U32s(Vec<u32>),
    Meshlets(Vec<MeshletDesc>),
}

impl OwnedBlob {
    /// バイト列として参照する（Pod 型は bytemuck でゼロコピー変換）。
    fn as_bytes(&self) -> &[u8] {
        match self {
            OwnedBlob::Bytes(v) => v,
            OwnedBlob::Verts(v) => bytemuck::cast_slice(v),
            OwnedBlob::Skins(v) => bytemuck::cast_slice(v),
            OwnedBlob::U32s(v)  => bytemuck::cast_slice(v),
            OwnedBlob::Meshlets(v) => bytemuck::cast_slice(v),
        }
    }
}

/// Model からブロブを全て取り出し（各スロットは空 Vec になる）、取り出しリストを返す。
fn extract_blobs(model: &mut Model) -> Vec<OwnedBlob> {
    let mut blobs = Vec::new();
    visit_blob_slots(model, &mut |slot| {
        let blob = match slot {
            BlobSlot::Bytes(v) => OwnedBlob::Bytes(std::mem::take(v)),
            BlobSlot::Verts(v) => OwnedBlob::Verts(std::mem::take(v)),
            BlobSlot::Skins(v) => OwnedBlob::Skins(std::mem::take(v)),
            BlobSlot::U32s(v)  => OwnedBlob::U32s(std::mem::take(v)),
            BlobSlot::Meshlets(v) => OwnedBlob::Meshlets(std::mem::take(v)),
        };
        blobs.push(blob);
    });
    blobs
}

/// `extract_blobs` で取り出したブロブを元のスロットへ戻す（書き出し後の復元用）。
fn restore_blobs(model: &mut Model, blobs: Vec<OwnedBlob>) {
    let mut it = blobs.into_iter();
    visit_blob_slots(model, &mut |slot| {
        // extract と同一トラバーサルのためスロット数・順序は必ず一致する。
        let Some(blob) = it.next() else { return; };
        match (slot, blob) {
            (BlobSlot::Bytes(v), OwnedBlob::Bytes(b)) => *v = b,
            (BlobSlot::Verts(v), OwnedBlob::Verts(b)) => *v = b,
            (BlobSlot::Skins(v), OwnedBlob::Skins(b)) => *v = b,
            (BlobSlot::U32s(v),  OwnedBlob::U32s(b))  => *v = b,
            (BlobSlot::Meshlets(v), OwnedBlob::Meshlets(b)) => *v = b,
            // 型不一致は起こらない想定（同一トラバーサル）。安全側で無視。
            _ => {}
        }
    });
}

/// 生バイト列スライスの列をブロブスロットへ注入する（読み込み用）。
///
/// Pod 型は `pod_collect_to_vec` で復元する（アライメント不問・memcpy 1 回）。
/// スロット数とスライス数が食い違った場合は false（破損キャッシュ）。
fn inject_blob_bytes(model: &mut Model, slices: &[&[u8]]) -> bool {
    let mut i = 0usize;
    let mut ok = true;
    visit_blob_slots(model, &mut |slot| {
        let Some(bytes) = slices.get(i) else { ok = false; return; };
        i += 1;
        match slot {
            BlobSlot::Bytes(v) => *v = bytes.to_vec(),
            BlobSlot::Verts(v) => *v = bytemuck::pod_collect_to_vec(bytes),
            BlobSlot::Skins(v) => *v = bytemuck::pod_collect_to_vec(bytes),
            BlobSlot::U32s(v)  => *v = bytemuck::pod_collect_to_vec(bytes),
            BlobSlot::Meshlets(v) => *v = bytemuck::pod_collect_to_vec(bytes),
        }
    });
    ok && i == slices.len()
}

// ============================================================
//  キャッシュ本体のエンコード / デコード（v2 コンテナ）
//
//  レイアウト（ヘッダ 36B の直後から）:
//    u64 meta_len
//    meta                     … bincode(ブロブ除去済み Model)。小さい。
//    u32 blob_count
//    blob_count × u64 len     … 各ブロブのバイト長テーブル
//    blobs                    … 生バイト列の連結
// ============================================================

/// キャッシュコンテナ全体（ヘッダ + メタ + ブロブテーブル + ブロブ）を
/// 任意のライターへ逐次書き出す。
///
/// 【ストリーミング書き出し】
/// 旧実装はファイル全体（Sponza 級で GB 単位）を 1 本の `Vec<u8>` に構築してから
/// 書いていたため、Model 本体と合わせてピークメモリが約 2 倍になっていた。
/// 本実装はブロブを 1 本ずつ `write_all` するため、追加メモリはメタデータ分のみ。
///
/// ブロブ取り出しのため `&mut Model` を取るが、成否に関わらず必ず復元する。
fn write_model_cache<W: std::io::Write>(
    w:       &mut W,
    model:   &mut Model,
    stamp:   (u64, u32, u64),
    bc_used: bool,
) -> std::io::Result<()> {
    // ── ブロブを取り出してメタデータを小さくする ──────────────
    let blobs = extract_blobs(model);
    let meta = match bincode::serialize(model) {
        Ok(m) => m,
        Err(e) => {
            // メタ直列化失敗でも呼び出し元の Model は壊さない
            restore_blobs(model, blobs);
            return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, e));
        }
    };

    // ── 逐次書き出し（途中失敗でも必ずブロブを復元する）──────────
    let result = (|| -> std::io::Result<()> {
        let mut header = Vec::with_capacity(HEADER_LEN);
        write_header(&mut header, stamp, bc_used);
        w.write_all(&header)?;
        w.write_all(&(meta.len() as u64).to_le_bytes())?;
        w.write_all(&meta)?;
        w.write_all(&(blobs.len() as u32).to_le_bytes())?;
        for b in &blobs {
            w.write_all(&(b.as_bytes().len() as u64).to_le_bytes())?;
        }
        for b in &blobs {
            w.write_all(b.as_bytes())?;
        }
        Ok(())
    })();

    restore_blobs(model, blobs);
    result
}

/// Model をキャッシュファイルのバイト列（ヘッダ込み）へエンコードする。
///
/// 実運用の `store_model` はファイルへ直接ストリーミング書き出しするため
/// この関数を使わない。フォーマットのラウンドトリップ検証（ユニットテスト）用。
#[cfg(test)]
fn encode_model_cache(model: &mut Model, stamp: (u64, u32, u64), bc_used: bool) -> Option<Vec<u8>> {
    let mut buf = Vec::new();
    match write_model_cache(&mut buf, model, stamp, bc_used) {
        Ok(())  => Some(buf),
        Err(e)  => {
            eprintln!("[SEED cache] モデルのエンコードに失敗: err={e}");
            None
        }
    }
}

/// キャッシュファイルのバイト列から Model をデコードする。
///
/// ヘッダ検証（mtime/サイズ/バージョン/BC フラグ）を通過し、
/// メタデータ・ブロブテーブルとも整合した場合のみ Some を返す。
/// 破損・不整合はすべて None（呼び出し元が元ファイルから再生成）。
fn decode_model_cache(data: &[u8], expect_stamp: (u64, u32, u64)) -> Option<Model> {
    let mut pos = validate_header(data, expect_stamp)?;

    // ── メタデータ（bincode。小さいので debug ビルドでも高速）──────
    let meta_len = read_u64(data, &mut pos)? as usize;
    let meta = data.get(pos..pos.checked_add(meta_len)?)?;
    pos += meta_len;
    let mut model: Model = match bincode::deserialize(meta) {
        Ok(m) => m,
        Err(e) => {
            eprintln!("[SEED cache] メタデータのデシリアライズに失敗（再生成します）: err={e}");
            return None;
        }
    };

    // ── ブロブテーブル ─────────────────────────────────────────
    let blob_count = read_u32(data, &mut pos)? as usize;
    let mut lens = Vec::with_capacity(blob_count);
    for _ in 0..blob_count {
        lens.push(read_u64(data, &mut pos)? as usize);
    }
    let mut slices: Vec<&[u8]> = Vec::with_capacity(blob_count);
    for len in lens {
        let s = data.get(pos..pos.checked_add(len)?)?;
        slices.push(s);
        pos += len;
    }

    // ── ブロブを Model のスロットへ注入 ─────────────────────────
    if !inject_blob_bytes(&mut model, &slices) {
        eprintln!("[SEED cache] ブロブ数がメタデータと不一致（破損キャッシュ。再生成します）");
        return None;
    }
    Some(model)
}

/// リトルエンディアン u64 を読み進める（境界チェック付き）。
fn read_u64(data: &[u8], pos: &mut usize) -> Option<u64> {
    let bytes = data.get(*pos..*pos + 8)?;
    *pos += 8;
    Some(u64::from_le_bytes(bytes.try_into().ok()?))
}

/// リトルエンディアン u32 を読み進める（境界チェック付き）。
fn read_u32(data: &[u8], pos: &mut usize) -> Option<u32> {
    let bytes = data.get(*pos..*pos + 4)?;
    *pos += 4;
    Some(u32::from_le_bytes(bytes.try_into().ok()?))
}

// ============================================================
//  モデルキャッシュ: 読み込み
// ============================================================

/// 有効なモデルキャッシュがあれば読み込んで返す。
///
/// 元ファイルパス（`assets://` 仮想パスまたは絶対パス）を受け取り、
/// キャッシュが存在し検証を通ればデコードした Model を返す。
/// キャッシュなし・古い・破損の場合は None（呼び出し元は元ファイルからロードする）。
pub fn try_load_model(src: &Path) -> Option<Model> {
    let resolved = resolve_src(src);
    let stamp = source_stamp(&resolved)?;
    let cache_path = model_cache_path(&resolved)?;

    let t_read = Instant::now();
    let data = std::fs::read(&cache_path).ok()?;
    let read_ms = t_read.elapsed().as_secs_f64() * 1000.0;

    let t_decode = Instant::now();
    let model = decode_model_cache(&data, stamp)?;
    let decode_ms = t_decode.elapsed().as_secs_f64() * 1000.0;

    eprintln!(
        "[SEED cache]   内訳: read {:.1}ms ({} MiB) + decode {:.1}ms",
        read_ms, data.len() / (1024 * 1024), decode_ms,
    );
    Some(model)
}

// ============================================================
//  モデルキャッシュ: 書き出し
// ============================================================

/// Model をキャッシュに書き出す（ベストエフォート。失敗は警告のみ）。
///
/// 呼び出し前に `process_model_textures` でテクスチャを Ready 形式に変換しておくこと。
/// ブロブ分離のため `&mut` を取るが、書き出し後に内容は元通り復元される。
pub fn store_model(src: &Path, model: &mut Model) {
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

    // 一時ファイルへストリーミング書き出ししてからリネームし、
    // 途中失敗による破損キャッシュを避ける。
    // 全体を Vec に構築しない（Sponza 級でファイルサイズ相当のピークメモリ増を防ぐ）。
    let tmp_path = cache_path.with_extension("smdl.tmp");
    let write_result = std::fs::File::create(&tmp_path).and_then(|f| {
        let mut w = std::io::BufWriter::new(f);
        write_model_cache(&mut w, model, stamp, bc_used)?;
        use std::io::Write as _;
        w.flush()
    });
    if let Err(e) = write_result {
        eprintln!("[SEED cache] キャッシュ書き込み失敗: {tmp_path:?} err={e}");
        let _ = std::fs::remove_file(&tmp_path);
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

/// テクスチャ処理の同時実行数（有界並列）。
///
/// 4K テクスチャ 1 枚あたりの一時メモリは RGBA 展開 64MiB + ミップチェーン ~21MiB
/// + パディング/BC 出力 ~40MiB ≒ 最大 ~130MiB。全テクスチャ一斉（旧実装）だと
/// Sponza 級で数 GB に達しエディタ同居環境でスワップを誘発するため、
/// 同時枚数を定数で制限してピークを「この枚数 × 1 枚分」に抑える。
const TEXTURE_PARALLELISM: usize = 3;

/// モデル内のテクスチャを、GPU 即アップロード可能な
/// `TextureSource::Ready`（ミップ + BC 圧縮 or RGBA）へ変換する。
///
/// 【ストリーミング処理（ピークメモリ削減）】
/// 旧実装は「全テクスチャを RGBA 展開したまま rayon で一斉圧縮」だったため、
/// Sponza 級（RGBA 合計 ~4.6GB）でピークメモリが数 GB 積み上がっていた。
/// 本実装は `TEXTURE_PARALLELISM` 枚ずつのチャンク単位で
/// 「デコード → ミップ → BC 圧縮 → Ready 置換（入力バイトは即 drop）」を行い、
/// チャンク間は逐次実行することで RGBA 展開の同時保持数を定数に制限する。
///
/// 対応する供給元:
/// - `EncodedBytes`（glTF 遅延デコード: GLB 埋め込み/data URI）→ ここでデコード
/// - `FilePath`（glTF 外部画像 / OBJ）→ ここで読み込み + デコード（失敗時は FilePath のまま
///   残し、GPU アップロード時の従来フォールバックに任せる）
/// - `Embedded`（デコード済み RGBA）→ ミップ + 圧縮のみ
/// - `Ready` → 処理済みのためスキップ
///
/// 戻り値: 処理した元 RGBA の合計バイト数（ログ用）。
pub fn process_model_textures(model: &mut Model) -> usize {
    use std::sync::atomic::AtomicUsize;

    let usages = classify_textures(model);
    let bc = bc_supported();
    let total = model.textures.len();
    if total == 0 { return 0; }

    // 進捗カウンタ（並列クロージャ間で共有。ユーザーがハングと進行を判別できるように
    // 1 枚ごとに n/N を出力する）
    let done          = AtomicUsize::new(0);
    let src_bytes_sum = AtomicUsize::new(0);

    // ── 有界並列ストリーミング ─────────────────────────────────
    // チャンク内は rayon 並列、チャンク間は逐次。
    // par_iter_mut により各テクスチャの source を並列に直接置換できる。
    for (chunk_i, chunk) in model.textures.chunks_mut(TEXTURE_PARALLELISM).enumerate() {
        let chunk_base = chunk_i * TEXTURE_PARALLELISM;
        chunk.par_iter_mut().enumerate().for_each(|(k, td)| {
            let usage = usages[chunk_base + k];
            let t0 = Instant::now();

            // 供給元を取り出す（プレースホルダと交換）。デコード失敗時は元に戻す。
            let taken = std::mem::replace(
                &mut td.source,
                TextureSource::Embedded { width: 0, height: 0, pixels: Vec::new() },
            );
            match decode_source_rgba(taken) {
                // デコード成功: ミップ + BC 圧縮して Ready に置換。
                // rgba / 元のエンコード済みバイトはこのスコープで drop される。
                Ok((w, h, rgba)) => {
                    src_bytes_sum.fetch_add(rgba.len(), Ordering::Relaxed);
                    td.source = build_ready_texture(w, h, &rgba, usage, bc);
                    // 変換後フォーマット名（遅いフォーマットの特定用にログへ含める）
                    let fmt_name = match &td.source {
                        TextureSource::Ready { format, .. } => format!("{format:?}"),
                        _ => "?".to_string(),
                    };
                    let n = done.fetch_add(1, Ordering::Relaxed) + 1;
                    eprintln!(
                        "[SEED cache]   tex {n}/{total}: {}x{} {} 変換 {:.0}ms",
                        w, h, fmt_name, t0.elapsed().as_secs_f64() * 1000.0,
                    );
                }
                // デコード不能 or 処理済み: 元の供給元へ戻す
                //（FilePath はアップロード時の従来フォールバックが効く）
                Err(original) => {
                    let skipped = matches!(original, TextureSource::Ready { .. });
                    td.source = original;
                    let n = done.fetch_add(1, Ordering::Relaxed) + 1;
                    if !skipped {
                        eprintln!("[SEED cache]   tex {n}/{total}: デコード不可（未変換のままフォールバック）");
                    }
                }
            }
        });
    }
    src_bytes_sum.load(Ordering::Relaxed)
}

/// テクスチャ供給元をデコードして (幅, 高さ, RGBA8) を返す。
///
/// 所有権を受け取り、デコードできない場合は元の供給元をそのまま Err で返す
/// （呼び出し元が復元してフォールバック経路に委ねる）。
fn decode_source_rgba(source: TextureSource) -> Result<(u32, u32, Vec<u8>), TextureSource> {
    match source {
        // デコード済み RGBA（コピーなしで所有権ごと使う）
        TextureSource::Embedded { width, height, pixels } => Ok((width, height, pixels)),
        // エンコード済みバイト（PNG/JPG 等）をデコード
        TextureSource::EncodedBytes { bytes } => {
            match image::load_from_memory(&bytes) {
                Ok(img) => {
                    let rgba = img.to_rgba8();
                    let (w, h) = rgba.dimensions();
                    Ok((w, h, rgba.into_raw()))
                }
                Err(e) => {
                    eprintln!("[SEED cache] 埋め込み画像のデコード失敗: err={e}");
                    Err(TextureSource::EncodedBytes { bytes })
                }
            }
        }
        // 外部ファイル（glTF 外部画像 / OBJ）。asset_fs 経由で PAK にも対応。
        TextureSource::FilePath(path) => {
            let path_str = path.to_string_lossy().to_string();
            match crate::engine::asset_fs::read_bytes(&path_str)
                .ok()
                .and_then(|bytes| image::load_from_memory(&bytes).ok())
            {
                Some(img) => {
                    let rgba = img.to_rgba8();
                    let (w, h) = rgba.dimensions();
                    Ok((w, h, rgba.into_raw()))
                }
                None => Err(TextureSource::FilePath(path)),
            }
        }
        // 処理済み
        ready @ TextureSource::Ready { .. } => Err(ready),
    }
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

/// 4 の倍数に切り上げる（BC ブロックは 4×4 テクセル単位）。
#[inline]
fn align4(v: u32) -> u32 { (v + 3) & !3 }

/// 1 ミップ（RGBA8）を指定フォーマットで BC 圧縮し、ブロック列を返す。
///
/// 【物理サイズでの圧縮】
/// wgpu の `write_texture` は BC フォーマットに対して「コピー幅がブロック幅(4)の
/// 倍数」を要求する。ミップチェーンの末端（2×2, 1×1）や、ベースが 4 の倍数でも
/// 途中のミップが非 4 倍数になるケース（例: 20→10→5）は論理サイズのままだと違反する。
/// そこで各ミップを物理サイズ `ceil(w/4)*4 × ceil(h/4)*4` へエッジ複製で
/// パディングしてから圧縮し、アップロード時も物理サイズでコピーする
/// （wgpu は圧縮ミップに対して論理サイズを超える物理サイズコピーを許容する）。
///
/// 出力ブロック数は `ceil(w/4) * ceil(h/4)` で wgpu が期待するデータ量と一致する。
fn compress_mip(width: u32, height: u32, rgba: &[u8], format: CachedTexFormat) -> Vec<u8> {
    let Some(tp) = texpresso_format(format) else {
        // 到達しない想定（build_ready_texture が BC 対応フォーマットのみ渡す）。
        // 安全側で元 RGBA をそのまま返す。
        return rgba.to_vec();
    };

    // 物理サイズ（4 の倍数へ切り上げ）にエッジ複製でパディング。
    // 端ピクセルの複製により、パディング領域がブロック圧縮の品質に悪影響を与えない。
    let pw = align4(width);
    let ph = align4(height);
    let padded;
    let src: &[u8] = if pw == width && ph == height {
        rgba
    } else {
        padded = pad_rgba_edge(width, height, rgba, pw, ph);
        &padded
    };

    // 【アルゴリズム選択 — RangeFit 必須】
    // texpresso の Params::default() は Algorithm::ClusterFit（高品質・低速）で、
    // BC1/BC3 のカラーブロックにのみ適用される。実機計測では 4K テクスチャの
    // BC3 圧縮（アルベド/MR）が 1 枚 8〜11 分に達した一方、BC5（法線）は
    // ClusterFit を通らない min/max コードブック経路のため同サイズ 3 秒だった
    // （約 170 倍差）。RangeFit（主成分軸への端点フィット）はブロックあたり
    // 定数コストの高速アルゴリズムで、品質低下はゲームテクスチャ用途では許容範囲。
    let params = texpresso::Params {
        algorithm: texpresso::Algorithm::RangeFit,
        ..texpresso::Params::default()
    };

    let (w, h) = (pw as usize, ph as usize);
    let size = tp.compressed_size(w, h);
    let mut out = vec![0u8; size];
    tp.compress(src, w, h, params, &mut out);
    out
}

/// RGBA8 バッファを (pw × ph) にエッジ複製（clamp）でパディングする。
fn pad_rgba_edge(w: u32, h: u32, rgba: &[u8], pw: u32, ph: u32) -> Vec<u8> {
    let mut out = vec![0u8; (pw as usize) * (ph as usize) * 4];
    for y in 0..ph {
        let sy = (y.min(h - 1)) as usize;
        for x in 0..pw {
            let sx = (x.min(w - 1)) as usize;
            let src_i = (sy * w as usize + sx) * 4;
            let dst_i = ((y * pw + x) as usize) * 4;
            out[dst_i..dst_i + 4].copy_from_slice(&rgba[src_i..src_i + 4]);
        }
    }
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
    ///
    /// v2 では各ミップを物理サイズ（4 の倍数へ切上げ）で圧縮するため、
    /// 非 4 倍数ミップ（10×10 等）や末端ミップ（2×2, 1×1）でも
    /// 出力長 = `ceil(w/4) * ceil(h/4) * block_bytes` が保証される。
    #[test]
    fn compressed_size_matches_block_layout() {
        // Bc3: 16B/ブロック
        let px = vec![128u8; 8 * 8 * 4];
        let out = compress_mip(8, 8, &px, CachedTexFormat::Bc3RgbaUnormSrgb);
        assert_eq!(out.len(), (8 / 4) * (8 / 4) * 16);

        // 非 4 倍数ミップ（10×10 → 物理 12×12 = 3×3 ブロック）。
        // 例: ベース 20 のミップチェーン 20→10→5→2→1 で発生するケース。
        let px10 = vec![128u8; 10 * 10 * 4];
        let out10 = compress_mip(10, 10, &px10, CachedTexFormat::Bc3RgbaUnorm);
        assert_eq!(out10.len(), 3 * 3 * 16);

        // 5×5 → 物理 8×8 = 2×2 ブロック
        let px5 = vec![128u8; 5 * 5 * 4];
        let out5x5 = compress_mip(5, 5, &px5, CachedTexFormat::Bc3RgbaUnorm);
        assert_eq!(out5x5.len(), 2 * 2 * 16);

        // 末端ミップ 2×2 / 1×1 → いずれも物理 4×4 = 1 ブロック
        let px2 = vec![128u8; 2 * 2 * 4];
        let out2 = compress_mip(2, 2, &px2, CachedTexFormat::Bc3RgbaUnormSrgb);
        assert_eq!(out2.len(), 16);
        let px1 = vec![128u8; 1 * 1 * 4];
        let out1x1 = compress_mip(1, 1, &px1, CachedTexFormat::Bc3RgbaUnormSrgb);
        assert_eq!(out1x1.len(), 16);

        // Bc5: 16B/ブロック, Bc1: 8B/ブロック
        let out5 = compress_mip(4, 4, &vec![128u8; 4 * 4 * 4], CachedTexFormat::Bc5RgUnorm);
        assert_eq!(out5.len(), 16);
        let out1 = compress_mip(4, 4, &vec![128u8; 4 * 4 * 4], CachedTexFormat::Bc1RgbaUnorm);
        assert_eq!(out1.len(), 8);
        // Bc5 の末端ミップ（法線マップの 1×1）も 1 ブロック
        let out5m = compress_mip(1, 1, &vec![128u8; 4], CachedTexFormat::Bc5RgUnorm);
        assert_eq!(out5m.len(), 16);
    }

    /// エッジ複製パディング: 端ピクセルが物理領域へ複製される。
    #[test]
    fn pad_rgba_edge_replicates_border() {
        // 1×1 の赤ピクセル → 4×4 全面が赤になる
        let src = vec![255u8, 0, 0, 255];
        let out = pad_rgba_edge(1, 1, &src, 4, 4);
        assert_eq!(out.len(), 4 * 4 * 4);
        for px in out.chunks_exact(4) {
            assert_eq!(px, &[255, 0, 0, 255]);
        }

        // 2×1（左=赤, 右=緑）→ 4×4 で x=0,1 が赤 / x=2,3 が緑（右端の複製）
        let src2 = vec![255u8, 0, 0, 255, 0, 255, 0, 255];
        let out2 = pad_rgba_edge(2, 1, &src2, 4, 4);
        for y in 0..4usize {
            for x in 0..4usize {
                let i = (y * 4 + x) * 4;
                let expect: [u8; 4] = if x < 1 { [255, 0, 0, 255] } else if x == 1 { [0, 255, 0, 255] } else { [0, 255, 0, 255] };
                assert_eq!(&out2[i..i + 4], &expect, "at ({x},{y})");
            }
        }
    }

    /// v2 コンテナ（メタ + 生ブロブ）のエンコード → デコードのラウンドトリップ。
    /// 頂点・インデックス・LOD・ミップの全ブロブが復元されることを検証する。
    #[test]
    fn model_cache_roundtrip() {
        use super::super::model::*;

        // ── テスト用 Model 構築 ─────────────────────────────
        let vertices = vec![
            Vertex { position: [1.0, 2.0, 3.0], ..Default::default() },
            Vertex { position: [4.0, 5.0, 6.0], ..Default::default() },
            Vertex { position: [7.0, 8.0, 9.0], ..Default::default() },
        ];
        let indices = vec![0u32, 1, 2];
        let lod_indices = vec![vec![0u32, 1, 2], vec![2u32, 1, 0]];
        let mips = vec![vec![1u8; 64], vec![2u8; 16], vec![3u8; 16]];
        // メッシュレット（v3）: 記述子（メタ）＋連結配列（ブロブ）のラウンドトリップを検証。
        let meshlets = vec![MeshletDesc {
            vertex_offset: 0, triangle_offset: 0, vertex_count: 3, triangle_count: 1,
            center: [1.0, 2.0, 3.0], radius: 4.0,
            cone_axis: [0.0, 0.0, 1.0], cone_cutoff: 0.5,
        }];
        let meshlet_vertices = vec![0u32, 1, 2];
        let meshlet_triangles = vec![0u8, 1, 2];

        let mut model = Model {
            name: "roundtrip".to_string(),
            nodes: vec![],
            root_nodes: vec![],
            meshes: vec![Mesh {
                name: "m".to_string(),
                primitives: vec![Primitive {
                    vertices: vertices.clone(),
                    skin_vertices: vec![],
                    indices: indices.clone(),
                    material_index: Some(0),
                    lod_indices: lod_indices.clone(),
                    meshlets: meshlets.clone(),
                    meshlet_vertices: meshlet_vertices.clone(),
                    meshlet_triangles: meshlet_triangles.clone(),
                }],
            }],
            materials: vec![Material::default()],
            textures: vec![
                TextureData {
                    name: Some("t".to_string()),
                    source: TextureSource::Ready {
                        format: CachedTexFormat::Bc3RgbaUnormSrgb,
                        width: 8,
                        height: 8,
                        mips: mips.clone(),
                    },
                    sampler: SamplerData::default(),
                    linear: false,
                },
                // 未デコード画像（v5: デコード失敗時などに残るケース）もブロブとして往復する
                TextureData {
                    name: Some("enc".to_string()),
                    source: TextureSource::EncodedBytes { bytes: vec![9u8, 8, 7, 6, 5] },
                    sampler: SamplerData::default(),
                    linear: true,
                },
            ],
            animations: vec![],
            skins: vec![],
        };

        // ── エンコード（&mut だが復元されること）────────────
        let stamp = (11u64, 22u32, 33u64);
        let bc = bc_supported();
        let buf = encode_model_cache(&mut model, stamp, bc).expect("encode failed");

        // encode 後も元 Model は破壊されていない
        assert_eq!(model.meshes[0].primitives[0].vertices.len(), 3);
        assert_eq!(model.meshes[0].primitives[0].indices, indices);
        match &model.textures[0].source {
            TextureSource::Ready { mips: m, .. } => assert_eq!(m, &mips),
            _ => panic!("texture source mutated"),
        }

        // ── デコード ─────────────────────────────────────────
        let decoded = decode_model_cache(&buf, stamp).expect("decode failed");
        assert_eq!(decoded.name, "roundtrip");
        let prim = &decoded.meshes[0].primitives[0];
        assert_eq!(prim.vertices.len(), 3);
        assert_eq!(prim.vertices[1].position, [4.0, 5.0, 6.0]);
        assert_eq!(prim.indices, indices);
        assert_eq!(prim.lod_indices, lod_indices);
        // メッシュレット: 記述子（メタ）と連結配列（ブロブ）が復元される。
        assert_eq!(prim.meshlets.len(), 1);
        assert_eq!(prim.meshlets[0].vertex_count, 3);
        assert_eq!(prim.meshlets[0].center, [1.0, 2.0, 3.0]);
        assert_eq!(prim.meshlets[0].cone_cutoff, 0.5);
        assert_eq!(prim.meshlet_vertices, meshlet_vertices);
        assert_eq!(prim.meshlet_triangles, meshlet_triangles);
        match &decoded.textures[0].source {
            TextureSource::Ready { format, width, height, mips: m } => {
                assert_eq!(*format, CachedTexFormat::Bc3RgbaUnormSrgb);
                assert_eq!((*width, *height), (8, 8));
                assert_eq!(m, &mips);
            }
            _ => panic!("wrong texture source"),
        }
        match &decoded.textures[1].source {
            TextureSource::EncodedBytes { bytes } => assert_eq!(bytes, &vec![9u8, 8, 7, 6, 5]),
            _ => panic!("wrong encoded texture source"),
        }

        // ── stamp 不一致 → None ─────────────────────────────
        assert!(decode_model_cache(&buf, (11, 22, 34)).is_none());

        // ── 末尾切り詰め（破損）→ None（panic しない）────────
        assert!(decode_model_cache(&buf[..buf.len() - 10], stamp).is_none());
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

    /// ストリーミングテクスチャ処理: Embedded / EncodedBytes(PNG) の両供給元が
    /// Ready（ミップ付き）へ変換され、破損バイトはフォールバックで元のまま残る。
    #[test]
    fn process_textures_streaming_converts_sources() {
        use super::super::model::*;
        use std::io::Cursor;

        // 8×8 の PNG をメモリ上でエンコード（EncodedBytes 供給元のテスト用）
        let img = image::RgbaImage::from_pixel(8, 8, image::Rgba([10, 20, 30, 255]));
        let mut png = Vec::new();
        image::DynamicImage::ImageRgba8(img)
            .write_to(&mut Cursor::new(&mut png), image::ImageFormat::Png)
            .expect("png encode failed");

        let mk = |source| TextureData {
            name: None, source, sampler: SamplerData::default(), linear: false,
        };
        let mut model = Model {
            name: String::new(), nodes: vec![], root_nodes: vec![], meshes: vec![],
            materials: vec![],
            textures: vec![
                mk(TextureSource::Embedded { width: 4, height: 4, pixels: vec![128; 4 * 4 * 4] }),
                mk(TextureSource::EncodedBytes { bytes: png }),
                // 画像として不正なバイト列（デコード失敗 → フォールバックで残る）
                mk(TextureSource::EncodedBytes { bytes: vec![0, 1, 2] }),
            ],
            animations: vec![], skins: vec![],
        };

        let processed = process_model_textures(&mut model);
        // Embedded(4×4) + PNG(8×8) の RGBA 合計が集計される
        assert!(processed >= 4 * 4 * 4 + 8 * 8 * 4);

        // Embedded → Ready
        assert!(matches!(model.textures[0].source, TextureSource::Ready { .. }));
        // PNG → Ready（8,4,2,1 の 4 ミップ）
        match &model.textures[1].source {
            TextureSource::Ready { width, height, mips, .. } => {
                assert_eq!((*width, *height), (8, 8));
                assert_eq!(mips.len(), 4);
            }
            _ => panic!("PNG texture not converted"),
        }
        // 破損バイトは EncodedBytes のまま（クラッシュせずフォールバック）
        assert!(matches!(model.textures[2].source, TextureSource::EncodedBytes { .. }));
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



