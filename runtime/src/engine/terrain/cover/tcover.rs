// ============================================================
//  terrain/cover/tcover.rs — カバー場のバイナリ永続化（バージョン付き）
//
//  【責務】
//    1 チャンク分のカバー場（素材添字 ＋ 量）を、バージョン付きバイナリ形式へ
//    直列化 / 復元する。ファイル IO は行わない（純粋な bytes in/out）。
//    ファイル読み書きはエンジン層の責務（tvox.rs / tscatter.rs と同じ役割分担）。
//
//  【なぜ .tvox / .tscatter と別ファイルにするのか】
//    更新頻度が独立しているため。雪は降り続けるあいだ毎秒書き換わるが、
//    密度グリッド（143 KB/チャンク）も散布インスタンスも変わらない。
//    同一ファイルにすると雪が降るたびに MB 級を書き戻すことになる。
//    カバー場だけを分離すれば 1 チャンク 2 KB 強で済む。
//
//  【オンディスク仕様（TCOVER v1・リトルエンディアン）】
//    オフセット  型            内容
//    0           u8[4]         マジック "TCOV"
//    4           u32           バージョン（現在 1）
//    8           i32           チャンク座標 x
//    12          i32           チャンク座標 y
//    16          i32           チャンク座標 z
//    20          u32           resolution（1 軸のテクセル数）
//    24          u8[R*R]       素材添字（row-major: ix + iz*R）
//    24+R*R      u8[R*R]       量（0..255 が 0.0..1.0）
//
//    resolution をヘッダに書くのは、将来 COVER_FIELD_RESOLUTION を変えたときに
//    「壊れたファイル」と「解像度が違うだけのファイル」を区別するため。
//    現状は不一致を `ResolutionMismatch` として弾く（リサンプルはしない）。
// ============================================================

use super::super::chunk_coord::ChunkCoord;
use super::field::{CoverField, COVER_FIELD_RESOLUTION, COVER_FIELD_TEXELS};

/// TCOVER フォーマットのマジックバイト。
pub const TCOVER_MAGIC: [u8; 4] = *b"TCOV";
/// TCOVER フォーマットの現行バージョン。書き出しは常にこれ。
pub const TCOVER_VERSION: u32 = 1;

// ─── ヘッダのレイアウト（マジックナンバー禁止のため全て定数化）─────────────

/// マジックバイトのバイト数。
const MAGIC_LEN: usize = 4;
/// バージョン欄のオフセット。
const HEADER_OFF_VERSION: usize = MAGIC_LEN;
/// チャンク座標 x のオフセット。
const HEADER_OFF_COORD_X: usize = HEADER_OFF_VERSION + 4;
/// チャンク座標 y のオフセット。
const HEADER_OFF_COORD_Y: usize = HEADER_OFF_COORD_X + 4;
/// チャンク座標 z のオフセット。
const HEADER_OFF_COORD_Z: usize = HEADER_OFF_COORD_Y + 4;
/// 解像度欄のオフセット。
const HEADER_OFF_RESOLUTION: usize = HEADER_OFF_COORD_Z + 4;
/// ヘッダ長（マジック4 + version4 + coord12 + resolution4 = 24 バイト）。
const HEADER_LEN: usize = HEADER_OFF_RESOLUTION + 4;

// ============================================================
//  型
// ============================================================

/// TCOVER ヘッダから読み取れる情報。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TcoverHeader {
    /// ファイルに記録されたチャンク格子座標。
    pub coord: ChunkCoord,
    /// ファイルに記録された 1 軸あたりのテクセル数。
    pub resolution: u32,
}

/// TCOVER の読み込みエラー。
#[derive(Debug, PartialEq, Eq)]
pub enum TcoverError {
    /// マジックが "TCOV" でない。
    BadMagic,
    /// 未対応のバージョン。
    BadVersion,
    /// バイト列がヘッダ長に満たない。
    Truncated,
    /// 解像度が現行の `COVER_FIELD_RESOLUTION` と一致しない。
    ResolutionMismatch,
    /// 本体長がヘッダの解像度から決まる長さと一致しない。
    SizeMismatch,
}

// ============================================================
//  書き出し / 読み込み
// ============================================================

/// カバー場を TCOVER v1 バイト列へ直列化する。
pub fn write_chunk(field: &CoverField, coord: ChunkCoord) -> Vec<u8> {
    let mut out = Vec::with_capacity(HEADER_LEN + COVER_FIELD_TEXELS * 2);

    // ─── ヘッダ書き込み（すべてリトルエンディアン）───
    out.extend_from_slice(&TCOVER_MAGIC);
    out.extend_from_slice(&TCOVER_VERSION.to_le_bytes());
    out.extend_from_slice(&coord.x.to_le_bytes());
    out.extend_from_slice(&coord.y.to_le_bytes());
    out.extend_from_slice(&coord.z.to_le_bytes());
    out.extend_from_slice(&(COVER_FIELD_RESOLUTION as u32).to_le_bytes());

    // ─── 本体（素材 → 量の順。u8 配列なのでエンディアンは無関係）───
    out.extend_from_slice(field.raw_material());
    out.extend_from_slice(field.raw_amount());

    out
}

/// TCOVER バイト列の **ヘッダのみ** を読む（本体長は検証しない）。
pub fn read_header(bytes: &[u8]) -> Result<TcoverHeader, TcoverError> {
    if bytes.len() < HEADER_LEN {
        return Err(TcoverError::Truncated);
    }
    if bytes[0..MAGIC_LEN] != TCOVER_MAGIC {
        return Err(TcoverError::BadMagic);
    }
    let version = read_u32_le(bytes, HEADER_OFF_VERSION);
    if version != TCOVER_VERSION {
        return Err(TcoverError::BadVersion);
    }
    Ok(TcoverHeader {
        coord: ChunkCoord::new(
            read_i32_le(bytes, HEADER_OFF_COORD_X),
            read_i32_le(bytes, HEADER_OFF_COORD_Y),
            read_i32_le(bytes, HEADER_OFF_COORD_Z),
        ),
        resolution: read_u32_le(bytes, HEADER_OFF_RESOLUTION),
    })
}

/// TCOVER バイト列からカバー場を復元する。
///
/// 本体長がヘッダの解像度と厳密に一致しない場合は `SizeMismatch`
/// （余分な末尾バイトも許さない＝壊れたファイルを黙って読まない）。
pub fn read_chunk(bytes: &[u8]) -> Result<(CoverField, ChunkCoord), TcoverError> {
    // ─── ヘッダ検証（マジック・バージョン・最低長）───
    let header = read_header(bytes)?;
    if header.resolution as usize != COVER_FIELD_RESOLUTION {
        return Err(TcoverError::ResolutionMismatch);
    }

    // ─── 本体長の整合を検証する（素材 R*R ＋ 量 R*R ちょうど）───
    let body = &bytes[HEADER_LEN..];
    if body.len() != COVER_FIELD_TEXELS * 2 {
        return Err(TcoverError::SizeMismatch);
    }

    let material = body[..COVER_FIELD_TEXELS].to_vec();
    let amount = body[COVER_FIELD_TEXELS..].to_vec();
    // `from_raw` は長さ検証込み。上で長さを見ているので実質必ず成功する。
    let field = CoverField::from_raw(material, amount).ok_or(TcoverError::SizeMismatch)?;
    Ok((field, header.coord))
}

// ─── リトルエンディアン読み取りヘルパ（呼び出し前に境界検証済み前提）─────────

#[inline]
fn read_u32_le(b: &[u8], off: usize) -> u32 {
    u32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}

#[inline]
fn read_i32_le(b: &[u8], off: usize) -> i32 {
    i32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}
