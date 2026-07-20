// ============================================================
//  terrain/tvox.rs — チャンク密度のバイナリ永続化（バージョン付き）
//
//  【責務】
//    1 チャンクの密度グリッドを、バージョン付きバイナリ形式へ
//    直列化 / 復元する。ファイル IO は行わない（純粋な bytes in/out）。
//    ファイル読み書きはエンジン層の責務。
//
//  【オンディスク仕様（TVOX v1・リトルエンディアン）】
//    オフセット  型            内容
//    0           u8[4]         マジック "TVOX"
//    4           u32           バージョン（現在 1）
//    8           i32           チャンク座標 x
//    12          i32           チャンク座標 y
//    16          i32           チャンク座標 z
//    20          u32           samples_per_axis（1 軸のサンプル数, 例 33）
//    24          f32           voxel_size（メートル）
//    28          f32 * N       密度サンプル（N = samples_per_axis³, row-major）
//
//    全数値はリトルエンディアン。密度は f32 ビット表現をそのまま格納する。
// ============================================================

use super::chunk_coord::ChunkCoord;
use super::chunk_data::TerrainChunkData;
use super::settings::TerrainSettings;

/// TVOX フォーマットのマジックバイト。
pub const TVOX_MAGIC: [u8; 4] = *b"TVOX";
/// TVOX フォーマットの現行バージョン。
pub const TVOX_VERSION: u32 = 1;

/// ヘッダ長（マジック4 + version4 + coord12 + samples4 + voxel4 = 28 バイト）。
const HEADER_LEN: usize = 4 + 4 + 4 * 3 + 4 + 4;

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

/// チャンク密度を TVOX バイト列へ直列化する。
pub fn write_chunk(
    chunk: &TerrainChunkData,
    coord: ChunkCoord,
    settings: &TerrainSettings,
) -> Vec<u8> {
    let samples = chunk.samples_per_axis() as u32;
    let density = chunk.raw_density();

    // ─── ヘッダ + 密度ぶんを確保 ───
    let mut out = Vec::with_capacity(HEADER_LEN + density.len() * 4);

    // ─── ヘッダ書き込み（すべてリトルエンディアン） ───
    out.extend_from_slice(&TVOX_MAGIC);
    out.extend_from_slice(&TVOX_VERSION.to_le_bytes());
    out.extend_from_slice(&coord.x.to_le_bytes());
    out.extend_from_slice(&coord.y.to_le_bytes());
    out.extend_from_slice(&coord.z.to_le_bytes());
    out.extend_from_slice(&samples.to_le_bytes());
    out.extend_from_slice(&settings.voxel_size.to_le_bytes());

    // ─── 密度サンプルを f32 LE で書き込む ───
    for &d in density {
        out.extend_from_slice(&d.to_le_bytes());
    }

    out
}

/// TVOX バイト列からチャンク密度を復元する。
///
/// 復元されるチャンクの voxel_size / チャンク数は呼び出し側の
/// TerrainSettings と組み合わせて使う想定（このヘッダは samples と
/// voxel_size のみ保持し、それらの整合を検証する）。
pub fn read_chunk(bytes: &[u8]) -> Result<(TerrainChunkData, ChunkCoord), TvoxError> {
    // ─── 最低限ヘッダ長があるか ───
    if bytes.len() < HEADER_LEN {
        return Err(TvoxError::Truncated);
    }

    // ─── マジック検証 ───
    if bytes[0..4] != TVOX_MAGIC {
        return Err(TvoxError::BadMagic);
    }

    // ─── バージョン検証 ───
    let version = read_u32_le(bytes, 4);
    if version != TVOX_VERSION {
        return Err(TvoxError::BadVersion);
    }

    // ─── ヘッダ本体の読み取り ───
    let x = read_i32_le(bytes, 8);
    let y = read_i32_le(bytes, 12);
    let z = read_i32_le(bytes, 16);
    let samples = read_u32_le(bytes, 20) as usize;
    let voxel_size = read_f32_le(bytes, 24);

    // ─── 想定される密度サンプル数と実バイト数の整合を検証 ───
    let expected = samples
        .checked_mul(samples)
        .and_then(|v| v.checked_mul(samples))
        .ok_or(TvoxError::DimMismatch)?;
    let body = &bytes[HEADER_LEN..];
    if body.len() % 4 != 0 {
        return Err(TvoxError::Truncated);
    }
    let actual = body.len() / 4;
    if actual != expected {
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
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
    for iz in 0..samples {
        for iy in 0..samples {
            for ix in 0..samples {
                // row-major: index = x + y*S + z*S*S
                let flat = ix + iy * samples + iz * samples * samples;
                let d = read_f32_le(body, flat * 4);
                chunk.set_sample(ix, iy, iz, d);
            }
        }
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
