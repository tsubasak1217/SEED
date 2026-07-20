// ============================================================
//  terrain/scatter/tscatter.rs — 散布インスタンスのバイナリ永続化（バージョン付き）
//
//  【責務】
//    1 チャンク分の散布インスタンス列を、バージョン付きバイナリ形式へ
//    直列化 / 復元する。ファイル IO は行わない（純粋な bytes in/out）。
//    ファイル読み書きはエンジン層の責務（tvox.rs とまったく同じ役割分担）。
//
//  【なぜ .tvox と別ファイルにするのか】
//    散布はブラシで頻繁に描き替えるが密度グリッドは変わらない、逆に地形を
//    彫っても散布は再接地するだけで済む、というように更新頻度が独立している。
//    同一ファイルにすると毎回 MB 級の密度グリッドを書き戻すことになるため、
//    散布だけを分離した軽量ファイルとして持つ。
//
//  【オンディスク仕様（TSCATTER v1・リトルエンディアン）】
//    オフセット  型            内容
//    0           u8[4]         マジック "TSCT"
//    4           u32           バージョン（現在 1）
//    8           i32           チャンク座標 x
//    12          i32           チャンク座標 y
//    16          i32           チャンク座標 z
//    20          u32           instance_count
//    24          ...           インスタンス配列（1 件 40 バイト）
//
//    インスタンス 1 件（40 バイト）のレイアウト:
//      +0   f32*3   pos     ワールド座標
//      +12  f32*3   normal  接地面法線
//      +24  f32     yaw     Y 軸まわり回転（ラジアン）
//      +28  f32     scale   スケール倍率
//      +32  u32     prop_id props.json の prop 添字
//      +36  u32     seed    インスタンス固有の乱数シード
//
//    全数値はリトルエンディアン。f32 はビット表現をそのまま格納するため、
//    書き出し → 読み戻しでビット単位に一致する（丸め誤差は入らない）。
//
//  【prop_id の扱い】
//    prop_id は props.json 内の **添字** であり ID 文字列ではない。
//    props.json のプロップを並び替えると既存の散布データの指し先がずれる。
//    これはエンジン層が「保存時に ID 表を併記する / 並び替えを禁止する」等で
//    面倒を見る前提の設計判断であり、本レイヤは添字をそのまま運ぶ。
// ============================================================

use super::super::chunk_coord::ChunkCoord;

/// TSCATTER フォーマットのマジックバイト。
pub const TSCATTER_MAGIC: [u8; 4] = *b"TSCT";
/// TSCATTER フォーマットの現行バージョン。書き出しは常にこれ。
pub const TSCATTER_VERSION: u32 = 1;

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
/// インスタンス数欄のオフセット。
const HEADER_OFF_COUNT: usize = HEADER_OFF_COORD_Z + 4;
/// ヘッダ長（マジック4 + version4 + coord12 + count4 = 24 バイト）。
const HEADER_LEN: usize = HEADER_OFF_COUNT + 4;

// ─── インスタンス 1 件のレイアウト ─────────────────────────────────────────

/// インスタンス 1 件のバイト数。
const INSTANCE_LEN: usize = 40;
/// pos（f32*3）のインスタンス内オフセット。
const INSTANCE_OFF_POS: usize = 0;
/// normal（f32*3）のインスタンス内オフセット。
const INSTANCE_OFF_NORMAL: usize = INSTANCE_OFF_POS + 4 * 3;
/// yaw（f32）のインスタンス内オフセット。
const INSTANCE_OFF_YAW: usize = INSTANCE_OFF_NORMAL + 4 * 3;
/// scale（f32）のインスタンス内オフセット。
const INSTANCE_OFF_SCALE: usize = INSTANCE_OFF_YAW + 4;
/// prop_id（u32）のインスタンス内オフセット。
const INSTANCE_OFF_PROP_ID: usize = INSTANCE_OFF_SCALE + 4;
/// seed（u32）のインスタンス内オフセット。
const INSTANCE_OFF_SEED: usize = INSTANCE_OFF_PROP_ID + 4;

// コンパイル時にオフセット表とインスタンス長の整合を保証する
// （フィールドを足したときに INSTANCE_LEN の更新漏れを検出する）。
const _: () = assert!(INSTANCE_OFF_SEED + 4 == INSTANCE_LEN);

// ============================================================
//  ScatterInstance — 散布インスタンス 1 件
// ============================================================

/// 散布インスタンス 1 件（草 1 本・木 1 本）。
///
/// GPU インスタンシングへそのまま流せる最小限の姿勢情報のみを持つ。
/// 見た目（色・サイズ・風）は `prop_id` が指すプロップ定義側にあり、
/// 個体差は `seed` から手続き的に導出する（インスタンスを太らせない）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ScatterInstance {
    /// 接地点のワールド座標（メートル）。
    pub pos: [f32; 3],
    /// 接地面の外向き単位法線（密度勾配方向＝上向き）。
    pub normal: [f32; 3],
    /// Y 軸まわりの回転（ラジアン）。
    pub yaw: f32,
    /// スケール倍率（1.0 = プロップ定義そのままのサイズ）。
    pub scale: f32,
    /// props.json のプロップ添字。
    pub prop_id: u32,
    /// インスタンス固有の乱数シード（風の位相・色ゆらぎ等に使う）。
    pub seed: u32,
}

/// TSCATTER ヘッダから読み取れる情報。
///
/// 本体（インスタンス配列）を読まずに件数だけ知りたい場面
/// （LOD 判定・チャンク一覧の統計表示）のために分離してある。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TscatterHeader {
    /// ファイルに記録されたチャンク格子座標。
    pub coord: ChunkCoord,
    /// 格納されているインスタンス件数。
    pub instance_count: u32,
}

/// TSCATTER の読み込みエラー。
#[derive(Debug, PartialEq, Eq)]
pub enum TscatterError {
    /// マジックが "TSCT" でない。
    BadMagic,
    /// 未対応のバージョン。
    BadVersion,
    /// バイト列がヘッダ長に満たない。
    Truncated,
    /// 本体長がヘッダの instance_count と一致しない。
    CountMismatch,
}

/// 散布インスタンス列を TSCATTER v1 バイト列へ直列化する。
pub fn write_chunk(instances: &[ScatterInstance], coord: ChunkCoord) -> Vec<u8> {
    let mut out = Vec::with_capacity(HEADER_LEN + instances.len() * INSTANCE_LEN);

    // ─── ヘッダ書き込み（すべてリトルエンディアン）───
    out.extend_from_slice(&TSCATTER_MAGIC);
    out.extend_from_slice(&TSCATTER_VERSION.to_le_bytes());
    out.extend_from_slice(&coord.x.to_le_bytes());
    out.extend_from_slice(&coord.y.to_le_bytes());
    out.extend_from_slice(&coord.z.to_le_bytes());
    out.extend_from_slice(&(instances.len() as u32).to_le_bytes());

    // ─── 本体（1 件 40 バイト・オフセット表どおりの順で並べる）───
    for inst in instances {
        for v in inst.pos {
            out.extend_from_slice(&v.to_le_bytes());
        }
        for v in inst.normal {
            out.extend_from_slice(&v.to_le_bytes());
        }
        out.extend_from_slice(&inst.yaw.to_le_bytes());
        out.extend_from_slice(&inst.scale.to_le_bytes());
        out.extend_from_slice(&inst.prop_id.to_le_bytes());
        out.extend_from_slice(&inst.seed.to_le_bytes());
    }

    out
}

/// TSCATTER バイト列の **ヘッダのみ** を読む（本体長は検証しない）。
///
/// マジックとバージョンだけ検証し、座標とインスタンス件数を返す。
pub fn read_header(bytes: &[u8]) -> Result<TscatterHeader, TscatterError> {
    if bytes.len() < HEADER_LEN {
        return Err(TscatterError::Truncated);
    }
    if bytes[0..MAGIC_LEN] != TSCATTER_MAGIC {
        return Err(TscatterError::BadMagic);
    }
    let version = read_u32_le(bytes, HEADER_OFF_VERSION);
    if version != TSCATTER_VERSION {
        return Err(TscatterError::BadVersion);
    }
    Ok(TscatterHeader {
        coord: ChunkCoord::new(
            read_i32_le(bytes, HEADER_OFF_COORD_X),
            read_i32_le(bytes, HEADER_OFF_COORD_Y),
            read_i32_le(bytes, HEADER_OFF_COORD_Z),
        ),
        instance_count: read_u32_le(bytes, HEADER_OFF_COUNT),
    })
}

/// TSCATTER バイト列から散布インスタンス列を復元する。
///
/// 本体長がヘッダの件数と厳密に一致しない場合は `CountMismatch`
/// （余分な末尾バイトも許さない＝壊れたファイルを黙って読まない）。
pub fn read_chunk(bytes: &[u8]) -> Result<(Vec<ScatterInstance>, ChunkCoord), TscatterError> {
    // ─── ヘッダ検証（マジック・バージョン・最低長）───
    let header = read_header(bytes)?;
    let count = header.instance_count as usize;

    // ─── 本体長の整合を検証する ───
    //   件数 × 40 バイト ちょうどでなければならない。
    let body = &bytes[HEADER_LEN..];
    let expected = count
        .checked_mul(INSTANCE_LEN)
        .ok_or(TscatterError::CountMismatch)?;
    if body.len() != expected {
        return Err(TscatterError::CountMismatch);
    }

    // ─── インスタンスを 1 件ずつ読み出す ───
    let mut out = Vec::with_capacity(count);
    for i in 0..count {
        let base = i * INSTANCE_LEN;
        out.push(ScatterInstance {
            pos: [
                read_f32_le(body, base + INSTANCE_OFF_POS),
                read_f32_le(body, base + INSTANCE_OFF_POS + 4),
                read_f32_le(body, base + INSTANCE_OFF_POS + 8),
            ],
            normal: [
                read_f32_le(body, base + INSTANCE_OFF_NORMAL),
                read_f32_le(body, base + INSTANCE_OFF_NORMAL + 4),
                read_f32_le(body, base + INSTANCE_OFF_NORMAL + 8),
            ],
            yaw: read_f32_le(body, base + INSTANCE_OFF_YAW),
            scale: read_f32_le(body, base + INSTANCE_OFF_SCALE),
            prop_id: read_u32_le(body, base + INSTANCE_OFF_PROP_ID),
            seed: read_u32_le(body, base + INSTANCE_OFF_SEED),
        });
    }

    Ok((out, header.coord))
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

#[inline]
fn read_f32_le(b: &[u8], off: usize) -> f32 {
    f32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]])
}
