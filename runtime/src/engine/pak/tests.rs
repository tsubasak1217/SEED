// ============================================================
//  pak/tests.rs — PakReader の単体テスト
//
//  【確かめること】
//  - 大文字小文字・区切りの違いを吸収して同じエントリに解決する（従来の挙動）
//  - 読み口を差し替えても結果が変わらない（ファイル経路とメモリ上の Cursor 経路の一致）
//  - 壊れた PAK（マジック・版・途中で切れた表）をエラーとして返す
//  - エントリ表の先読みバッファの境界をまたいでも正しく読める
//  - PakReader を asset_fs のグローバル（Mutex）に置ける＝ Send である
// ============================================================

use super::*;
use std::io::{Cursor, Write};

/// エントリ 1 件ぶんの固定長部分（path_len 4 + offset 8 + size 8）のバイト数。
const ENTRY_FIXED_BYTES: usize = 4 + 8 + 8;
/// ヘッダー（magic 4 + version 4 + entry_count 4）のバイト数。
const HEADER_BYTES: usize = 12;

/// テスト用の PAK をヘッダ仕様どおりにメモリ上へ組み立てる（エディタの PakWriter と同じレイアウト）。
pub(crate) fn build_pak_bytes(entries: &[(&str, &[u8])]) -> Vec<u8> {
    let mut table = Vec::new();
    let mut data = Vec::new();
    // データ部の先頭オフセット = ヘッダー + テーブル長。テーブル長は先に計算する。
    let table_len: usize = entries.iter().map(|(p, _)| ENTRY_FIXED_BYTES + p.len()).sum();
    let mut offset = (HEADER_BYTES + table_len) as u64;
    for (p, bytes) in entries {
        table.extend_from_slice(&(p.len() as u32).to_le_bytes());
        table.extend_from_slice(p.as_bytes());
        table.extend_from_slice(&offset.to_le_bytes());
        table.extend_from_slice(&(bytes.len() as u64).to_le_bytes());
        data.extend_from_slice(bytes);
        offset += bytes.len() as u64;
    }
    let mut out = Vec::with_capacity(HEADER_BYTES + table.len() + data.len());
    out.extend_from_slice(&MAGIC);
    out.extend_from_slice(&VERSION.to_le_bytes());
    out.extend_from_slice(&(entries.len() as u32).to_le_bytes());
    out.extend_from_slice(&table);
    out.extend_from_slice(&data);
    out
}

/// テスト用の PAK をファイルとして書き出す。
fn write_pak(path: &Path, entries: &[(&str, &[u8])]) {
    let mut f = File::create(path).unwrap();
    f.write_all(&build_pak_bytes(entries)).unwrap();
}

/// テストごとに衝突しない一時フォルダを作る（並列実行されるテスト同士で名前がぶつからないようにする）。
fn temp_dir(tag: &str) -> std::path::PathBuf {
    let dir = std::env::temp_dir().join(format!("seed_pak_test_{tag}_{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

/// 見本のテキストエントリの中身。
const SAMPLE_SCENE: &[u8] = b"{\"name\":\"Main\"}";
/// 見本のバイナリエントリの中身（NUL や 0xFF を含む）。
const SAMPLE_BINARY: &[u8] = b"glTF\x02\x00\x00\x00binary\x00\xff";
/// 見本の空エントリの中身。
const SAMPLE_EMPTY: &[u8] = b"";

/// 3 件入りの見本（テキスト・バイナリ・空ファイル）。
fn sample_entries() -> Vec<(&'static str, &'static [u8])> {
    vec![
        ("scenes/Main.scene", SAMPLE_SCENE),
        ("models/BrainStem.glb", SAMPLE_BINARY),
        ("empty/zero.txt", SAMPLE_EMPTY),
    ]
}

/// エントリ名と参照の大文字小文字・区切りが違っても同じエントリに解決すること。
#[test]
fn read_is_case_and_separator_insensitive() {
    let dir = temp_dir("case");
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

/// メモリ上の Cursor（ファイル以外の読み口）から開いて読めること。
#[test]
fn opens_from_in_memory_cursor() {
    let bytes = build_pak_bytes(&sample_entries());
    let mut reader = PakReader::from_source(Cursor::new(bytes)).unwrap();

    assert_eq!(reader.entry_count(), 3);
    assert_eq!(reader.read("scenes/main.scene").as_deref(), Some(SAMPLE_SCENE));
    assert_eq!(reader.read("models/BrainStem.glb").as_deref(), Some(SAMPLE_BINARY));
    // 0 バイトのエントリも「有る（空）」として返る（無いの None と区別する）。
    assert_eq!(reader.read("empty/zero.txt").as_deref(), Some(SAMPLE_EMPTY));
    assert!(reader.read("scenes/missing.scene").is_none());
}

/// 同じ PAK を「ファイル」と「メモリ」から開いたとき、エントリ一覧も中身も一致すること。
/// 読み口の抽象化で従来のファイル経路の結果が変わっていないことの確認。
#[test]
fn file_and_cursor_sources_give_identical_results() {
    let entries = sample_entries();
    let dir = temp_dir("same");
    let pak = dir.join("assets.pak");
    write_pak(&pak, &entries);

    let mut from_file = PakReader::open(&pak).unwrap();
    let mut from_memory = PakReader::from_source(Cursor::new(std::fs::read(&pak).unwrap())).unwrap();

    let mut file_paths: Vec<String> = from_file.entry_paths().map(str::to_string).collect();
    let mut memory_paths: Vec<String> = from_memory.entry_paths().map(str::to_string).collect();
    file_paths.sort();
    memory_paths.sort();
    assert_eq!(file_paths, memory_paths);
    assert_eq!(file_paths.len(), entries.len());

    for path in &file_paths {
        let a = from_file.read(path);
        assert!(a.is_some(), "ファイル経路で読めない: {path}");
        assert_eq!(a, from_memory.read(path), "中身が食い違う: {path}");
    }

    drop(from_file);
    let _ = std::fs::remove_dir_all(&dir);
}

/// 型を消した読み口（Box<dyn PakSource>）から開けること（プラットフォーム側が実体を決める経路）。
#[test]
fn opens_from_boxed_source() {
    let source: Box<dyn PakSource> = Box::new(Cursor::new(build_pak_bytes(&sample_entries())));
    let mut reader = PakReader::from_boxed(source).unwrap();
    assert_eq!(reader.entry_count(), 3);
    assert_eq!(reader.read("scenes/Main.scene").as_deref(), Some(SAMPLE_SCENE));
}

/// 読み口の現在位置が先頭でなくても、先頭から解釈すること。
#[test]
fn source_position_does_not_matter() {
    let mut cursor = Cursor::new(build_pak_bytes(&sample_entries()));
    cursor.set_position(7);
    let mut reader = PakReader::from_source(cursor).unwrap();
    assert_eq!(reader.read("scenes/Main.scene").as_deref(), Some(SAMPLE_SCENE));
}

/// エントリをどの順に読んでも（後ろ → 前 → 真ん中 → 前）正しい中身が返ること（毎回 Seek し直す）。
#[test]
fn reads_entries_in_any_order() {
    let mut reader = PakReader::from_source(Cursor::new(build_pak_bytes(&sample_entries()))).unwrap();
    assert_eq!(reader.read("empty/zero.txt").as_deref(), Some(SAMPLE_EMPTY));
    assert_eq!(reader.read("scenes/Main.scene").as_deref(), Some(SAMPLE_SCENE));
    assert_eq!(reader.read("models/BrainStem.glb").as_deref(), Some(SAMPLE_BINARY));
    assert_eq!(reader.read("scenes/Main.scene").as_deref(), Some(SAMPLE_SCENE));
}

/// エントリ表が先読みバッファ（TABLE_READ_BUFFER_BYTES）より大きくても正しく読めること。
#[test]
fn large_entry_table_crosses_buffer_boundary() {
    // 1 件あたり固定部 20 バイト + パス約 30 バイト。バッファの 2 倍以上になる件数にする。
    const ENTRY_COUNT: usize = 5000;
    // 全件は検証せず、この間隔で間引いて確かめる（件数が多くテストが遅くなるため）。
    const CHECK_STRIDE: usize = 499;
    // フォルダ名を散らす剰余（同じフォルダに全件が並ばないようにするだけの値）。
    const FOLDER_SPREAD: usize = 97;

    let names: Vec<String> = (0..ENTRY_COUNT)
        .map(|i| format!("folder_{:04}/file_{i:05}.bin", i % FOLDER_SPREAD))
        .collect();
    let payloads: Vec<Vec<u8>> = (0..ENTRY_COUNT).map(|i| (i as u32).to_le_bytes().to_vec()).collect();
    let entries: Vec<(&str, &[u8])> =
        names.iter().zip(&payloads).map(|(n, p)| (n.as_str(), p.as_slice())).collect();
    let bytes = build_pak_bytes(&entries);
    assert!(
        bytes.len() > 2 * TABLE_READ_BUFFER_BYTES,
        "テストの前提（表がバッファより大きい）が崩れている"
    );

    let mut reader = PakReader::from_source(Cursor::new(bytes)).unwrap();
    assert_eq!(reader.entry_count(), ENTRY_COUNT);
    for (i, name) in names.iter().enumerate().step_by(CHECK_STRIDE) {
        assert_eq!(reader.read(name), Some((i as u32).to_le_bytes().to_vec()), "{name}");
    }
    // 最後の 1 件（表の末尾）も読める。
    let last = ENTRY_COUNT - 1;
    assert_eq!(reader.read(&names[last]), Some((last as u32).to_le_bytes().to_vec()));
}

/// マジックが違うものは InvalidData として拒否すること。
#[test]
fn rejects_invalid_magic() {
    let mut bytes = build_pak_bytes(&sample_entries());
    bytes[0] = b'X';
    let err = PakReader::from_source(Cursor::new(bytes)).err().expect("拒否されるべき");
    assert_eq!(err.kind(), io::ErrorKind::InvalidData);
}

/// 未対応の版は InvalidData として拒否すること。
#[test]
fn rejects_unsupported_version() {
    // 版フィールドの位置（magic 4 バイトの直後の 4 バイト）。
    const VERSION_FIELD: std::ops::Range<usize> = 4..8;
    let mut bytes = build_pak_bytes(&sample_entries());
    bytes[VERSION_FIELD].copy_from_slice(&(VERSION + 1).to_le_bytes());
    let err = PakReader::from_source(Cursor::new(bytes)).err().expect("拒否されるべき");
    assert_eq!(err.kind(), io::ErrorKind::InvalidData);
}

/// エントリ表の途中で切れているものはエラーになること（途中まで読めた表で動き出さない）。
#[test]
fn rejects_truncated_entry_table() {
    let bytes = build_pak_bytes(&sample_entries());
    // ヘッダー + 1 件目の固定部の途中までで切る。
    let truncated = bytes[..HEADER_BYTES + ENTRY_FIXED_BYTES / 2].to_vec();
    let err = PakReader::from_source(Cursor::new(truncated)).err().expect("拒否されるべき");
    assert_eq!(err.kind(), io::ErrorKind::UnexpectedEof);
}

/// PakReader は Send であること（asset_fs のグローバルな Mutex に置き、複数スレッドから読むため）。
#[test]
fn pak_reader_is_send() {
    fn assert_send<T: Send>() {}
    assert_send::<PakReader>();
}
