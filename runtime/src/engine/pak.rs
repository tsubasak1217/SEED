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
            let entry_path = String::from_utf8(path_bytes)
                .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
            let offset = read_u64(&mut file)?;
            let size = read_u64(&mut file)?;
            entries.insert(normalize_key(&entry_path), (offset, size));
        }

        Ok(Self { file, entries })
    }

    /// 相対パス（例: `"scenes/main.scene"`）に対応するバイト列を返す。
    ///
    /// エントリが存在しない場合は `None` を返す。
    /// 内部で Seek するため `&mut self` が必要。
    pub fn read(&mut self, relative_path: &str) -> Option<Vec<u8>> {
        // 区切りと大文字小文字を正規化して検索する（normalize_key のコメントを参照）
        let key = normalize_key(relative_path);
        let (offset, size) = *self.entries.get(key.as_str())?;

        self.file.seek(SeekFrom::Start(offset)).ok()?;
        let mut buf = vec![0u8; size as usize];
        self.file.read_exact(&mut buf).ok()?;
        Some(buf)
    }

    /// PAK に含まれる全エントリの相対パスを返す（デバッグ用。検索キーと同じく小文字化済み）。
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

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    /// テスト用の PAK をヘッダ仕様どおりに組み立てる（エディタの PakWriter と同じレイアウト）。
    fn write_pak(path: &Path, entries: &[(&str, &[u8])]) {
        let mut table = Vec::new();
        let mut data = Vec::new();
        // データ部の先頭オフセット = ヘッダ 12 バイト + テーブル長。テーブル長は先に計算する。
        let table_len: usize = entries.iter().map(|(p, _)| 4 + p.len() + 8 + 8).sum();
        let mut offset = (12 + table_len) as u64;
        for (p, bytes) in entries {
            table.extend_from_slice(&(p.len() as u32).to_le_bytes());
            table.extend_from_slice(p.as_bytes());
            table.extend_from_slice(&offset.to_le_bytes());
            table.extend_from_slice(&(bytes.len() as u64).to_le_bytes());
            data.extend_from_slice(bytes);
            offset += bytes.len() as u64;
        }
        let mut f = File::create(path).unwrap();
        f.write_all(&MAGIC).unwrap();
        f.write_all(&VERSION.to_le_bytes()).unwrap();
        f.write_all(&(entries.len() as u32).to_le_bytes()).unwrap();
        f.write_all(&table).unwrap();
        f.write_all(&data).unwrap();
    }

    /// エントリ名と参照の大文字小文字・区切りが違っても同じエントリに解決すること。
    #[test]
    fn read_is_case_and_separator_insensitive() {
        let dir = std::env::temp_dir().join(format!("seed_pak_test_{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let pak = dir.join("assets.pak");
        write_pak(&pak, &[("mainGame/models/Yasi.glb", b"glb-bytes"), ("a/b.txt", b"x")]);

        let mut reader = PakReader::open(&pak).unwrap();
        assert_eq!(reader.read("mainGame/models/yasi.glb").as_deref(), Some(&b"glb-bytes"[..]));
        assert_eq!(reader.read("MAINGAME\\MODELS\\YASI.GLB").as_deref(), Some(&b"glb-bytes"[..]));
        assert_eq!(reader.read("a/b.txt").as_deref(), Some(&b"x"[..]));
        assert!(reader.read("a/missing.txt").is_none());

        drop(reader);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
