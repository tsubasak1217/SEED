// ============================================================
//  pak/mod.rs — アセットパックファイルリーダー（形式の正典）
//
//  【バイナリ形式】
//  [Header - 12 bytes]
//    magic:       [u8; 4] = b"SEED"
//    version:     u32 LE  = 1
//    entry_count: u32 LE
//
//  [Entry Table - entry_count * variable bytes]
//    path_len: u32 LE
//    path:     [u8; path_len]  (UTF-8, '/' 区切りの相対パス)
//    offset:   u64 LE  (ファイル先頭からのバイト位置)
//    size:     u64 LE
//
//  [Data Section]
//    各ファイルの生バイトを連結
//
//  書き手はエディタの editor/src/Packaging/Pak/PakWriter.cs。形式は変更しない。
//
//  【読み口】
//  PakReader はファイルそのものではなく「読み口」（source.rs の PakSource = Read + Seek + Send）
//  から読む。デスクトップの配布物は実行ファイルの隣のファイル（`open`）、Android は APK 内の
//  アセット、単体テストはメモリ上の Cursor（`from_source`）。形式の解釈はどの読み口でも同じコードを通る。
//
//  【構成】
//    mod.rs    … PakReader（ヘッダー・エントリ表の解釈と読み出し）
//    source.rs … PakSource（読み口の抽象）
//    tests.rs  … 単体テスト
// ============================================================

mod source;
// テスト用の PAK 組み立て（build_pak_bytes）を asset_fs・package_source のテストでも使うため crate 内へ公開する。
#[cfg(test)]
pub(crate) mod tests;

pub use source::PakSource;

use std::collections::HashMap;
use std::fs::File;
use std::io::{self, BufReader, Read, Seek, SeekFrom};
use std::path::Path;

/// PAK ファイルのマジックナンバー。
const MAGIC: [u8; 4] = *b"SEED";
/// 対応バージョン番号。
const VERSION: u32 = 1;

/// エントリ表を読むときの先読みバッファの大きさ（バイト）。
///
/// エントリ表は「4 バイト・パス・8 バイト・8 バイト」の小さな読み取りの繰り返しなので、
/// 読み口へ直接発行すると 1 件あたり 4 回の read になる（APK 内アセットでは 1 回ごとに NDK の
/// `AAsset_read` 呼び出し）。まとめて先読みして回数を減らす。数千件の表でも数百 KB 程度なので、
/// この大きさで数回の read に収まる。
const TABLE_READ_BUFFER_BYTES: usize = 64 * 1024;

/// エントリ検索用にパスを正規化する（区切りを `/` に統一し、大文字小文字を無視する）。
///
/// 開発時はアセットを Windows のファイルシステムから読むため、参照文字列の大文字小文字が
/// 実ファイル名と違っていても動く。PAK だけが大文字小文字を区別すると「エディタでは動くのに
/// パッケージ版だけシーンが読めない」不具合になる（実際に `Yasi.glb` と `yasi.glb` で起きた）ので、
/// PAK もファイルシステムと同じ意味論にそろえる。書き込み側（エディタ）は 1 ファイル 1 エントリを
/// 保証するため、小文字化で衝突することはない。
fn normalize_key(path: &str) -> String {
    path.replace('\\', "/").to_lowercase()
}

// ============================================================
//  PakReader
// ============================================================

/// assets.pak を読み込み、エントリをメモリ上にインデックスするリーダー。
///
/// `read()` でエントリの相対パスを指定すると、対応するバイト列を返す。
/// 内部では読み口（`PakSource`）を保持し、Seek しながら読み出す。
/// 読み口が 1 本なので `read` は `&mut self`（共有するときは呼び出し側が Mutex で包む。asset_fs 参照）。
pub struct PakReader {
    /// PAK 本体の読み口（ファイル・APK 内アセット・メモリ）。
    source: Box<dyn PakSource>,
    /// 正規化済みの相対パス → (ファイル先頭からのオフセット, バイトサイズ) のマップ。
    entries: HashMap<String, (u64, u64)>,
}

impl PakReader {
    /// 指定パスの PAK ファイルを開いてインデックスを構築する（デスクトップの配布物）。
    ///
    /// マジック / バージョンの不一致やパースエラーは `io::Error` として返す。
    pub fn open(path: &Path) -> io::Result<Self> {
        Self::from_source(File::open(path)?)
    }

    /// 任意の読み口（`Read + Seek + Send`）から PAK を開いてインデックスを構築する。
    ///
    /// # 引数
    /// * `source` - PAK 全体を読める読み口（例: `std::io::Cursor<Vec<u8>>`、APK 内アセット）
    pub fn from_source<S: PakSource + 'static>(source: S) -> io::Result<Self> {
        Self::from_boxed(Box::new(source))
    }

    /// 型を消した読み口から PAK を開いてインデックスを構築する。
    ///
    /// 読み口の実体をプラットフォーム側が決める経路（`PackageSource::open` の戻り値）用。
    /// 読み口の現在位置は問わない（先頭へ Seek してからヘッダーを読む）。
    pub fn from_boxed(mut source: Box<dyn PakSource>) -> io::Result<Self> {
        source.seek(SeekFrom::Start(0))?;
        // エントリ表は先読みバッファ越しに読む（TABLE_READ_BUFFER_BYTES のコメント参照）。
        // バッファが読み口を先へ進めても、データ部の読み出しは毎回オフセットへ Seek するので影響しない。
        let entries = {
            let mut table = BufReader::with_capacity(TABLE_READ_BUFFER_BYTES, &mut source);
            read_index(&mut table)?
        };
        Ok(Self { source, entries })
    }

    /// 相対パス（例: `"scenes/main.scene"`）に対応するバイト列を返す。
    ///
    /// エントリが存在しない場合は `None` を返す。
    /// 内部で Seek するため `&mut self` が必要。
    pub fn read(&mut self, relative_path: &str) -> Option<Vec<u8>> {
        // 区切りと大文字小文字を正規化して検索する（normalize_key のコメントを参照）
        let key = normalize_key(relative_path);
        let (offset, size) = *self.entries.get(key.as_str())?;

        self.source.seek(SeekFrom::Start(offset)).ok()?;
        let mut buf = vec![0u8; size as usize];
        self.source.read_exact(&mut buf).ok()?;
        Some(buf)
    }

    /// 相対パス（例: `"scenes/main.scene"`）のエントリがあるか（`read` と同じく区切り・大文字小文字を問わない）。
    ///
    /// 中身は読まない（エントリ表を引くだけ）。Android の起動オプションで指定されたシーンが pak にあるかの確かめに使う。
    pub fn contains(&self, relative_path: &str) -> bool {
        self.entries.contains_key(normalize_key(relative_path).as_str())
    }

    /// PAK に含まれる全エントリの相対パスを返す（検索キーと同じく小文字化済み）。
    pub fn entry_paths(&self) -> impl Iterator<Item = &str> {
        self.entries.keys().map(|s| s.as_str())
    }

    /// PAK に含まれるエントリ数を返す（起動ログ用）。
    pub fn entry_count(&self) -> usize {
        self.entries.len()
    }
}

// ============================================================
//  形式の解釈
// ============================================================

/// ヘッダーとエントリ表を読み、正規化済みキー → (オフセット, サイズ) の表を返す。
///
/// # 引数
/// * `reader` - PAK の先頭に位置している読み手
fn read_index<R: Read>(reader: &mut R) -> io::Result<HashMap<String, (u64, u64)>> {
    // ── ヘッダー読み込み ────────────────────────────────────
    let mut magic = [0u8; 4];
    reader.read_exact(&mut magic)?;
    if magic != MAGIC {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Invalid PAK magic (expected 'SEED')",
        ));
    }

    let version = read_u32(reader)?;
    if version != VERSION {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("Unsupported PAK version: {version}"),
        ));
    }

    let entry_count = read_u32(reader)? as usize;

    // ── エントリテーブル読み込み ─────────────────────────────
    let mut entries = HashMap::with_capacity(entry_count);
    for _ in 0..entry_count {
        let path_len = read_u32(reader)? as usize;
        let mut path_bytes = vec![0u8; path_len];
        reader.read_exact(&mut path_bytes)?;
        let entry_path = String::from_utf8(path_bytes)
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
        let offset = read_u64(reader)?;
        let size = read_u64(reader)?;
        entries.insert(normalize_key(&entry_path), (offset, size));
    }
    Ok(entries)
}

// ============================================================
//  ユーティリティ
// ============================================================

/// 読み手から u32 をリトルエンディアンで読む。
fn read_u32<R: Read>(reader: &mut R) -> io::Result<u32> {
    let mut buf = [0u8; 4];
    reader.read_exact(&mut buf)?;
    Ok(u32::from_le_bytes(buf))
}

/// 読み手から u64 をリトルエンディアンで読む。
fn read_u64<R: Read>(reader: &mut R) -> io::Result<u64> {
    let mut buf = [0u8; 8];
    reader.read_exact(&mut buf)?;
    Ok(u64::from_le_bytes(buf))
}
