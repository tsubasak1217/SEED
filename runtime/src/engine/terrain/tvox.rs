// ============================================================
//  terrain/tvox.rs — チャンク密度＋スプラットのバイナリ永続化（バージョン付き）
//
//  【責務】
//    1 チャンクの密度グリッドと手ペイントスプラットを、バージョン付きバイナリ形式へ
//    直列化 / 復元する。ファイル IO は行わない（純粋な bytes in/out）。
//    ファイル読み書きはエンジン層の責務。
//
//  【オンディスク仕様（TVOX v3・リトルエンディアン）】
//    オフセット  型            内容
//    0           u8[4]         マジック "TVOX"
//    4           u32           バージョン（現在 3）
//    8           i32           チャンク座標 x
//    12          i32           チャンク座標 y
//    16          i32           チャンク座標 z
//    20          u32           samples_per_axis（1 軸のサンプル数, 例 33）
//    24          f32           voxel_size（メートル）
//    28          u32           slot_count（1 サンプルのブレンドスロット数。v3 では 4）
//    32          f32 * N       密度サンプル（N = samples_per_axis³, row-major）
//    …           u8  * N*S     スロットのレイヤ番号（S = slot_count）
//    …           u8  * N*S     スロットの重み（u8 量子化）
//    …           u8  * N       ペイント量（0=未ペイント〜255=完全に手描き優先）
//
//    全数値はリトルエンディアン。密度は f32 ビット表現をそのまま格納する。
//
//  【v2 との後方互換】
//    v2 は「レイヤ番号なし・レイヤ 0..L-1 の重みを密に並べる」形式だった。
//    v2 を読む場合はスロットのレイヤ番号を [0,1,2,3]（の先頭 min(L, 4) 個）と
//    みなして重みだけを取り込む。T2b 以前のセーブデータがそのまま開ける。
//
//  【v1 との後方互換】
//    v1 ヘッダは 28 バイト（layer_count 無し）で、本体は密度のみ。
//    v1 を読んだ場合はスプラットを「全サンプル未ペイント（paint_amount=0）」として
//    復元する。未ペイント＝斜度／高度ルールによる自動下地が全面に適用されるため、
//    旧セーブデータでもレイヤブレンドされた地形として正しく表示される
//    （重みが欠落して真っ黒になる、といった破綻は起きない）。
//
//  【スロット数が変わったとき】
//    ファイルの slot_count が現在の TERRAIN_BLEND_SLOTS と異なる場合は、
//    共通する先頭 min(S_file, S_now) スロットぶんだけを読み、残りは 0 で埋める。
// ============================================================

use super::chunk_coord::ChunkCoord;
use super::chunk_data::TerrainChunkData;
use super::layers::TERRAIN_BLEND_SLOTS;
use super::settings::TerrainSettings;

/// TVOX フォーマットのマジックバイト。
pub const TVOX_MAGIC: [u8; 4] = *b"TVOX";
/// TVOX フォーマットの現行バージョン（v3 = レイヤ番号付きスロット）。書き出しは常にこれ。
pub const TVOX_VERSION: u32 = 3;
/// レイヤ番号なしスプラットの旧バージョン。読み込み時のみ受け付ける。
pub const TVOX_VERSION_V2: u32 = 2;
/// スプラット非対応の旧バージョン（密度のみ）。読み込み時のみ受け付ける。
pub const TVOX_VERSION_V1: u32 = 1;

/// v1 ヘッダ長（マジック4 + version4 + coord12 + samples4 + voxel4 = 28 バイト）。
const HEADER_LEN_V1: usize = 4 + 4 + 4 * 3 + 4 + 4;
/// v2 ヘッダ長（v1 + layer_count4 = 32 バイト）。
const HEADER_LEN_V2: usize = HEADER_LEN_V1 + 4;
/// v3 ヘッダ長（v2 と同じレイアウト。28 バイト目が slot_count に変わるだけ）。
const HEADER_LEN_V3: usize = HEADER_LEN_V2;

/// TVOX の読み込みエラー。
#[derive(Debug, PartialEq, Eq)]
pub enum TvoxError {
    /// マジックが "TVOX" でない。
    BadMagic,
    /// 未対応のバージョン。
    BadVersion,
    /// バイト列が途中で切れている。
    Truncated,
    /// 密度サンプル数がヘッダの次元と一致しない。
    DimMismatch,
}

/// TVOX ヘッダから読み取れる「そのチャンクが作られたときの構成」。
///
/// シーンロード時に、保存された地形が現在の `TerrainSettings` と同じ分割数・
/// ボクセルサイズで作られているかを突き合わせるために使う。
/// 本体（密度・スプラット）を読まずに済むため、複数チャンクの整合検査が安価に回せる。
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct TvoxHeader {
    /// ファイルに記録されたチャンク格子座標。
    pub coord: ChunkCoord,
    /// 1 軸あたりのサンプル数（= chunk_cells + 1）。
    pub samples_per_axis: u32,
    /// 1 ボクセル辺のサイズ（メートル）。
    pub voxel_size: f32,
}

impl TvoxHeader {
    /// ヘッダから逆算したチャンク 1 軸のセル数（= samples_per_axis - 1）。
    pub fn chunk_cells(&self) -> u32 {
        self.samples_per_axis.saturating_sub(1)
    }
}

/// TVOX バイト列の**ヘッダのみ**を読む（本体は検証しない）。
///
/// マジックとバージョンだけ検証し、座標・サンプル数・ボクセルサイズを返す。
/// v1 / v2 / v3 いずれも先頭 28 バイトのレイアウトが共通なので同じ経路で読める。
pub fn read_header(bytes: &[u8]) -> Result<TvoxHeader, TvoxError> {
    if bytes.len() < HEADER_LEN_V1 {
        return Err(TvoxError::Truncated);
    }
    if bytes[0..4] != TVOX_MAGIC {
        return Err(TvoxError::BadMagic);
    }
    let version = read_u32_le(bytes, 4);
    if version != TVOX_VERSION && version != TVOX_VERSION_V2 && version != TVOX_VERSION_V1 {
        return Err(TvoxError::BadVersion);
    }
    Ok(TvoxHeader {
        coord: ChunkCoord::new(
            read_i32_le(bytes, 8),
            read_i32_le(bytes, 12),
            read_i32_le(bytes, 16),
        ),
        samples_per_axis: read_u32_le(bytes, 20),
        voxel_size: read_f32_le(bytes, 24),
    })
}

/// チャンク（密度＋スプラット）を TVOX v3 バイト列へ直列化する。
pub fn write_chunk(
    chunk: &TerrainChunkData,
    coord: ChunkCoord,
    settings: &TerrainSettings,
) -> Vec<u8> {
    let samples = chunk.samples_per_axis() as u32;
    let density = chunk.raw_density();
    let paint_index = chunk.raw_paint_index();
    let paint_weight = chunk.raw_paint_weight();
    let paint_amount = chunk.raw_paint_amount();

    // ─── ヘッダ + 密度 + スプラット（番号・重み・量）ぶんを確保 ───
    let body_len = density.len() * 4
        + paint_index.len() * TERRAIN_BLEND_SLOTS
        + paint_weight.len() * TERRAIN_BLEND_SLOTS
        + paint_amount.len();
    let mut out = Vec::with_capacity(HEADER_LEN_V3 + body_len);

    // ─── ヘッダ書き込み（すべてリトルエンディアン） ───
    out.extend_from_slice(&TVOX_MAGIC);
    out.extend_from_slice(&TVOX_VERSION.to_le_bytes());
    out.extend_from_slice(&coord.x.to_le_bytes());
    out.extend_from_slice(&coord.y.to_le_bytes());
    out.extend_from_slice(&coord.z.to_le_bytes());
    out.extend_from_slice(&samples.to_le_bytes());
    out.extend_from_slice(&settings.voxel_size.to_le_bytes());
    out.extend_from_slice(&(TERRAIN_BLEND_SLOTS as u32).to_le_bytes());

    // ─── 密度サンプルを f32 LE で書き込む ───
    for &d in density {
        out.extend_from_slice(&d.to_le_bytes());
    }

    // ─── スロットのレイヤ番号（サンプルごとに S バイト）───
    for idx in paint_index {
        out.extend_from_slice(idx);
    }

    // ─── スロットの重み（サンプルごとに S バイト）───
    for w in paint_weight {
        out.extend_from_slice(w);
    }

    // ─── ペイント量（サンプルごとに 1 バイト）───
    out.extend_from_slice(paint_amount);

    out
}

/// TVOX バイト列からチャンクを復元する（v1 / v2 / v3 のいずれも受け付ける）。
///
/// 復元されるチャンクの voxel_size / チャンク数は呼び出し側の
/// TerrainSettings と組み合わせて使う想定（このヘッダは samples と
/// voxel_size のみ保持し、それらの整合を検証する）。
pub fn read_chunk(bytes: &[u8]) -> Result<(TerrainChunkData, ChunkCoord), TvoxError> {
    // ─── 最低限 v1 ヘッダ長があるか ───
    if bytes.len() < HEADER_LEN_V1 {
        return Err(TvoxError::Truncated);
    }

    // ─── マジック検証 ───
    if bytes[0..4] != TVOX_MAGIC {
        return Err(TvoxError::BadMagic);
    }

    // ─── バージョン検証（v1 / v2 / v3 のみ受け付ける）───
    let version = read_u32_le(bytes, 4);
    if version != TVOX_VERSION && version != TVOX_VERSION_V2 && version != TVOX_VERSION_V1 {
        return Err(TvoxError::BadVersion);
    }

    // ─── ヘッダ本体の読み取り ───
    let x = read_i32_le(bytes, 8);
    let y = read_i32_le(bytes, 12);
    let z = read_i32_le(bytes, 16);
    let samples = read_u32_le(bytes, 20) as usize;
    let voxel_size = read_f32_le(bytes, 24);

    // ─── v2/v3 のみ 28 バイト目のカウントを読む（v1 はスプラット無し = 0 扱い）───
    //   v2 では「レイヤ数 L」、v3 では「スロット数 S」を意味する。どちらも
    //   「1 サンプルあたりの重みバイト数」であることは共通。
    let (header_len, file_slots) = if version == TVOX_VERSION || version == TVOX_VERSION_V2 {
        if bytes.len() < HEADER_LEN_V2 {
            return Err(TvoxError::Truncated);
        }
        (HEADER_LEN_V2, read_u32_le(bytes, 28) as usize)
    } else {
        (HEADER_LEN_V1, 0usize)
    };

    // ─── 想定されるサンプル総数を求める ───
    let total = samples
        .checked_mul(samples)
        .and_then(|v| v.checked_mul(samples))
        .ok_or(TvoxError::DimMismatch)?;
    let body = &bytes[header_len..];

    // ─── 本体長の整合を検証する ───
    //   v1: density のみ
    //   v2: density + weight(S bytes/sample) + amount(1 byte/sample)
    //   v3: density + index(S) + weight(S) + amount(1) bytes/sample
    let density_bytes = total.checked_mul(4).ok_or(TvoxError::DimMismatch)?;
    let per_sample_splat = match version {
        TVOX_VERSION => file_slots * 2 + 1,
        TVOX_VERSION_V2 => file_slots + 1,
        _ => 0,
    };
    let splat_bytes = total
        .checked_mul(per_sample_splat)
        .ok_or(TvoxError::DimMismatch)?;
    let expected_bytes = density_bytes
        .checked_add(splat_bytes)
        .ok_or(TvoxError::DimMismatch)?;
    if body.len() != expected_bytes {
        // 密度部分すら足りないなら Truncated、長さが噛み合わないなら DimMismatch。
        if body.len() < density_bytes {
            return Err(TvoxError::Truncated);
        }
        return Err(TvoxError::DimMismatch);
    }

    // ─── ヘッダの voxel_size / samples から復元用の設定を構成 ───
    //   samples = chunk_cells + 1 より chunk_cells を逆算する。
    let settings = TerrainSettings {
        voxel_size,
        chunk_cells: (samples.saturating_sub(1)) as u32,
        ..TerrainSettings::default()
    };

    // ─── 密度サンプルを読み込んでチャンクへ書き込む ───
    //   new_filled はスプラットを「全サンプル未ペイント」で初期化するため、
    //   v1 ファイルではこの初期値がそのまま残る（＝全面ルール自動生成）。
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
    for flat in 0..total {
        let d = read_f32_le(body, flat * 4);
        // row-major: flat = x + y*S + z*S*S を逆算する。
        let ix = flat % samples;
        let iy = (flat / samples) % samples;
        let iz = flat / (samples * samples);
        chunk.set_sample(ix, iy, iz, d);
    }

    // ─── スプラット（v2 / v3）を読み込む ───
    if file_slots > 0 {
        // スロット数の増減に耐えるよう、共通する先頭 min(S_file, S_now) 個だけを取り込む。
        let common = file_slots.min(TERRAIN_BLEND_SLOTS);
        let mut index = vec![[0u8; TERRAIN_BLEND_SLOTS]; total];
        let mut weight = vec![[0u8; TERRAIN_BLEND_SLOTS]; total];

        // v3 は index → weight の順で並ぶ。v2 は index ブロックが存在しない。
        let (index_base, weight_base) = if version == TVOX_VERSION {
            (Some(density_bytes), density_bytes + total * file_slots)
        } else {
            (None, density_bytes)
        };
        let amount_base = weight_base + total * file_slots;

        for flat in 0..total {
            match index_base {
                // v3: 保存されたレイヤ番号をそのまま読む。
                Some(base) => {
                    let src = base + flat * file_slots;
                    index[flat][..common].copy_from_slice(&body[src..src + common]);
                }
                // v2 後方互換: 重みはレイヤ 0..L-1 の密ベクトルだったので、
                // スロット k のレイヤ番号は k とみなす（index=[0,1,2,3]）。
                None => {
                    for k in 0..common {
                        index[flat][k] = k as u8;
                    }
                }
            }
            let src = weight_base + flat * file_slots;
            weight[flat][..common].copy_from_slice(&body[src..src + common]);
        }

        chunk.set_raw_paint_index(index);
        chunk.set_raw_paint_weight(weight);
        chunk.set_raw_paint_amount(body[amount_base..amount_base + total].to_vec());
    }

    Ok((chunk, ChunkCoord::new(x, y, z)))
}

// ─── リトルエンディアン読み取りヘルパ（呼び出し前に境界検証済み前提） ─────────

#[inline]
fn read_u32_le(b: &[u8], off: usize) -> u32 {
    u32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}

#[inline]
fn read_i32_le(b: &[u8], off: usize) -> i32 {
    i32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}

#[inline]
fn read_f32_le(b: &[u8], off: usize) -> f32 {
    f32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}

/// テスト・移行ツール用に v2（レイヤ番号なしスプラット）形式で直列化する。
///
/// 本番の保存経路では使わない（保存は常に v3）。v2→v3 の後方互換テストが
/// 「本物の v2 バイト列」を作るために必要なので、書き手をここに残しておく。
///
/// `dense_weights[flat][layer]` はレイヤ番号を添字とする u8 量子化重み
/// （v2 当時の並び）。レイヤ数は TERRAIN_BLEND_SLOTS 固定で書く。
pub fn write_chunk_v2(
    chunk: &TerrainChunkData,
    coord: ChunkCoord,
    settings: &TerrainSettings,
    dense_weights: &[[u8; TERRAIN_BLEND_SLOTS]],
) -> Vec<u8> {
    let samples = chunk.samples_per_axis() as u32;
    let density = chunk.raw_density();
    let paint_amount = chunk.raw_paint_amount();
    debug_assert_eq!(
        dense_weights.len(),
        density.len(),
        "v2 重み配列の長さ不一致"
    );

    let mut out = Vec::with_capacity(HEADER_LEN_V2 + density.len() * 4);
    out.extend_from_slice(&TVOX_MAGIC);
    out.extend_from_slice(&TVOX_VERSION_V2.to_le_bytes());
    out.extend_from_slice(&coord.x.to_le_bytes());
    out.extend_from_slice(&coord.y.to_le_bytes());
    out.extend_from_slice(&coord.z.to_le_bytes());
    out.extend_from_slice(&samples.to_le_bytes());
    out.extend_from_slice(&settings.voxel_size.to_le_bytes());
    out.extend_from_slice(&(TERRAIN_BLEND_SLOTS as u32).to_le_bytes());
    for &d in density {
        out.extend_from_slice(&d.to_le_bytes());
    }
    for w in dense_weights {
        out.extend_from_slice(w);
    }
    out.extend_from_slice(paint_amount);
    out
}

/// テスト・移行ツール用に v1（密度のみ）形式で直列化する。
///
/// 本番の保存経路では使わない（保存は常に v3）。tvox v1 の後方互換テストが
/// 「本物の v1 バイト列」を作るために必要なので、書き手をここに残しておく。
pub fn write_chunk_v1(
    chunk: &TerrainChunkData,
    coord: ChunkCoord,
    settings: &TerrainSettings,
) -> Vec<u8> {
    let samples = chunk.samples_per_axis() as u32;
    let density = chunk.raw_density();
    let mut out = Vec::with_capacity(HEADER_LEN_V1 + density.len() * 4);
    out.extend_from_slice(&TVOX_MAGIC);
    out.extend_from_slice(&TVOX_VERSION_V1.to_le_bytes());
    out.extend_from_slice(&coord.x.to_le_bytes());
    out.extend_from_slice(&coord.y.to_le_bytes());
    out.extend_from_slice(&coord.z.to_le_bytes());
    out.extend_from_slice(&samples.to_le_bytes());
    out.extend_from_slice(&settings.voxel_size.to_le_bytes());
    for &d in density {
        out.extend_from_slice(&d.to_le_bytes());
    }
    out
}
