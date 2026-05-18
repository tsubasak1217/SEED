// ============================================================
//  pak.rs — アセットパックファイルリーダー
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
// ============================================================

use std::collections::HashMap;
use std::fs::File;
use std::io::{self, Read, Seek, SeekFrom};
use std::path::Path;

/// PAK ファイルのマジックナンバー。
const MAGIC: [u8; 4] = *b"SEED";
/// 対応バージョン番号。
const VERSION: u32 = 1;

// ============================================================
//  PakReader
// ============================================================

/// assets.pak を読み込み、エントリをメモリ上にインデックスするリーダー。
///
/// `read()` でエントリの相対パスを指定すると、対応するバイト列を返す。
/// 内部では File ハンドルを保持し、Seek しながら読み出す。
pub struct PakReader {
    /// オープンしたパックファイル。
    file: File,
    /// 相対パス → (ファイル先頭からのオフセット, バイトサイズ) のマップ。
    entries: HashMap<String, (u64, u64)>,
}

impl PakReader {
    /// 指定パスの PAK ファイルを開いてインデックスを構築する。
    ///
    /// マジック / バージョンの不一致やパースエラーは `io::Error` として返す。
    pub fn open(path: &Path) -> io::Result<Self> {
        let mut file = File::open(path)?;

        // ── ヘッダー読み込み ────────────────────────────────────
        let mut magic = [0u8; 4];
        file.read_exact(&mut magic)?;
        if magic != MAGIC {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Invalid PAK magic (expected 'SEED')",
            ));
        }

        let version = read_u32(&mut file)?;
        if version != VERSION {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("Unsupported PAK version: {version}"),
            ));
        }

        let entry_count = read_u32(&mut file)? as usize;

        // ── エントリテーブル読み込み ─────────────────────────────
        let mut entries = HashMap::with_capacity(entry_count);
        for _ in 0..entry_count {
            let path_len = read_u32(&mut file)? as usize;
            let mut path_bytes = vec![0u8; path_len];
            file.read_exact(&mut path_bytes)?;
            let entry_path = String::from_utf8(path_bytes).map_err(|e| {
                io::Error::new(io::ErrorKind::InvalidData, e)
            })?;
            let offset = read_u64(&mut file)?;
            let size   = read_u64(&mut file)?;
            entries.insert(entry_path, (offset, size));
        }

        Ok(Self { file, entries })
    }

    /// 相対パス（例: `"scenes/main.scene"`）に対応するバイト列を返す。
    ///
    /// エントリが存在しない場合は `None` を返す。
    /// 内部で Seek するため `&mut self` が必要。
    pub fn read(&mut self, relative_path: &str) -> Option<Vec<u8>> {
        // Windows バックスラッシュを正規化（念のため）
        let key = relative_path.replace('\\', "/");
        let (offset, size) = *self.entries.get(key.as_str())?;

        self.file.seek(SeekFrom::Start(offset)).ok()?;
        let mut buf = vec![0u8; size as usize];
        self.file.read_exact(&mut buf).ok()?;
        Some(buf)
    }

    /// PAK に含まれる全エントリの相対パスを返す（デバッグ用）。
    pub fn entry_paths(&self) -> impl Iterator<Item = &str> {
        self.entries.keys().map(|s| s.as_str())
    }
}

// ============================================================
//  ユーティリティ
// ============================================================

/// ファイルから u32 をリトルエンディアンで読む。
fn read_u32(file: &mut File) -> io::Result<u32> {
    let mut buf = [0u8; 4];
    file.read_exact(&mut buf)?;
    Ok(u32::from_le_bytes(buf))
}

/// ファイルから u64 をリトルエンディアンで読む。
fn read_u64(file: &mut File) -> io::Result<u64> {
    let mut buf = [0u8; 8];
    file.read_exact(&mut buf)?;
    Ok(u64::from_le_bytes(buf))
}
