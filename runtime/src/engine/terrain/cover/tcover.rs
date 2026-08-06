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
//  【オンディスク仕様（TCOVER v2・リトルエンディアン）】
//    オフセット      型            内容
//    0               u8[4]         マジック "TCOV"
//    4               u32           バージョン（現在 2）
//    8               i32           チャンク座標 x
//    12              i32           チャンク座標 y
//    16              i32           チャンク座標 z
//    20              u32           resolution（1 軸のテクセル数）
//    24              u8[R*R]       素材添字（row-major: ix + iz*R）
//    24+R*R          u8[R*R]       量（0..255 が 0.0..1.0）
//    24+2*R*R        u8[R*R]       踏み固め深さ（0..255 が 0.0..1.0。v2 で追加）
//    24+3*R*R        f32[R*R]      面の基準 Y（ワールド座標・メートル。v2 で追加）
//
//    resolution をヘッダに書くのは、将来 COVER_FIELD_RESOLUTION を変えたときに
//    「壊れたファイル」と「解像度が違うだけのファイル」を区別するため。
//    現状は不一致を `ResolutionMismatch` として弾く（リサンプルはしない）。
//
//  【v1 との互換（読み込みのみ）】
//    v1（素材＋量だけ）のファイルもそのまま読める。
//      ・踏み固め … 全 0（轍が無い状態）として移行する
//      ・基準 Y   … 「未知」として移行し、ロード後に地表情報から再計算する
//                   （エンジン層 `sync_cover_base_y` → `CoverField::refresh_base_y`）
//    書き出しは常に v2。したがって v1 のシーンを 1 度保存すると v2 へ上がる。
// ============================================================

use super::super::chunk_coord::ChunkCoord;
use super::field::{
    CoverField, COVER_BASE_Y_ABSENT, COVER_FIELD_RESOLUTION, COVER_FIELD_TEXELS,
};

/// TCOVER フォーマットのマジックバイト。
pub const TCOVER_MAGIC: [u8; 4] = *b"TCOV";
/// TCOVER フォーマットの現行バージョン。書き出しは常にこれ。
pub const TCOVER_VERSION: u32 = 2;

/// 読み込みだけ対応する旧バージョン（素材＋量のみ）。
pub const TCOVER_VERSION_LEGACY: u32 = 1;

/// 基準 Y 1 個ぶんのバイト数（f32・リトルエンディアン）。
const BASE_Y_BYTES: usize = 4;

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
    /// ファイルに記録されたフォーマットバージョン（1 または 2）。
    pub version: u32,
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

/// カバー場を TCOVER v2 バイト列へ直列化する。
pub fn write_chunk(field: &CoverField, coord: ChunkCoord) -> Vec<u8> {
    let mut out = Vec::with_capacity(
        HEADER_LEN + COVER_FIELD_TEXELS * 3 + COVER_FIELD_TEXELS * BASE_Y_BYTES,
    );

    // ─── ヘッダ書き込み（すべてリトルエンディアン）───
    out.extend_from_slice(&TCOVER_MAGIC);
    out.extend_from_slice(&TCOVER_VERSION.to_le_bytes());
    out.extend_from_slice(&coord.x.to_le_bytes());
    out.extend_from_slice(&coord.y.to_le_bytes());
    out.extend_from_slice(&coord.z.to_le_bytes());
    out.extend_from_slice(&(COVER_FIELD_RESOLUTION as u32).to_le_bytes());

    // ─── 本体（素材 → 量 → 踏み固めの順。u8 配列なのでエンディアンは無関係）───
    out.extend_from_slice(field.raw_material());
    out.extend_from_slice(field.raw_amount());
    out.extend_from_slice(field.raw_trample());

    // ─── 基準 Y（f32・リトルエンディアン）───
    //   面が無いテクセルは -∞（`COVER_BASE_Y_ABSENT`）がそのまま入る。
    //   f32 のビットパターンをそのまま書くので、読み戻しは厳密に元の値になる。
    for y in field.raw_base_y() {
        out.extend_from_slice(&y.to_le_bytes());
    }

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
    if version != TCOVER_VERSION && version != TCOVER_VERSION_LEGACY {
        return Err(TcoverError::BadVersion);
    }
    Ok(TcoverHeader {
        coord: ChunkCoord::new(
            read_i32_le(bytes, HEADER_OFF_COORD_X),
            read_i32_le(bytes, HEADER_OFF_COORD_Y),
            read_i32_le(bytes, HEADER_OFF_COORD_Z),
        ),
        resolution: read_u32_le(bytes, HEADER_OFF_RESOLUTION),
        version,
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

    // ─── 本体長の整合を検証する（バージョンごとに期待長が違う）───
    let body = &bytes[HEADER_LEN..];
    let expected = if header.version == TCOVER_VERSION_LEGACY {
        // v1: 素材 ＋ 量。
        COVER_FIELD_TEXELS * 2
    } else {
        // v2: 素材 ＋ 量 ＋ 踏み固め ＋ 基準 Y(f32)。
        COVER_FIELD_TEXELS * 3 + COVER_FIELD_TEXELS * BASE_Y_BYTES
    };
    if body.len() != expected {
        return Err(TcoverError::SizeMismatch);
    }

    let material = body[..COVER_FIELD_TEXELS].to_vec();
    let amount = body[COVER_FIELD_TEXELS..COVER_FIELD_TEXELS * 2].to_vec();

    // ─── 踏み固め・基準 Y（v1 は既定値へ移行する）───
    let (trample, base_y) = if header.version == TCOVER_VERSION_LEGACY {
        // 轍は無かったものとし、基準 Y は「未知」にする。
        // 未知の基準 Y はロード後に地表情報から再計算される。
        (
            vec![0u8; COVER_FIELD_TEXELS],
            vec![COVER_BASE_Y_ABSENT; COVER_FIELD_TEXELS],
        )
    } else {
        let trample = body[COVER_FIELD_TEXELS * 2..COVER_FIELD_TEXELS * 3].to_vec();
        let base_bytes = &body[COVER_FIELD_TEXELS * 3..];
        let mut base_y = Vec::with_capacity(COVER_FIELD_TEXELS);
        for i in 0..COVER_FIELD_TEXELS {
            let o = i * BASE_Y_BYTES;
            base_y.push(f32::from_le_bytes([
                base_bytes[o],
                base_bytes[o + 1],
                base_bytes[o + 2],
                base_bytes[o + 3],
            ]));
        }
        (trample, base_y)
    };

    // `from_raw` は長さ検証込み。上で長さを見ているので実質必ず成功する。
    let field =
        CoverField::from_raw(material, amount, trample, base_y).ok_or(TcoverError::SizeMismatch)?;
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
