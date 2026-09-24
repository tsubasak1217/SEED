// ============================================================
//  pak/source.rs — PAK を読む「読み口」の抽象（Read + Seek + Send）
//
//  【役割】
//  PakReader が必要とする能力だけを 1 つのトレイトにまとめる。
//  PAK の形式（ヘッダー・エントリ表・データ部）は読み口に依存しないので、
//  「どこから読むか」を差し替えられるようにしておく。
//
//  【実装しているもの】
//    std::fs::File             … デスクトップの配布物（実行ファイルの隣の assets.pak）
//    std::io::Cursor<Vec<u8>>  … メモリ上の PAK（単体テスト）
//    Android の APK 内アセット  … runtime/android/native の ApkAsset（ndk の Asset を包んだもの）
//
//  【なぜ Send が要るのか】
//  PakReader は asset_fs のグローバル（`OnceLock<Option<Mutex<PakReader>>>`）に置かれ、
//  メインスレッド・モデルの非同期ロードのワーカー・rayon・音声スレッドから読まれる。
//  `Mutex<T>` を複数スレッドで共有するには `T: Send` が必要なので、読み口にも Send を課す。
//  同時アクセスは Mutex が排除するため、Sync（共有参照の同時使用）は求めない。
// ============================================================

use std::io::{Read, Seek};

/// PAK の読み口（任意位置へ Seek してバイト列を読めるもの）。
///
/// `Read + Seek + Send` を満たす型には自動で実装される（ブランケット実装）。
/// 利用側は `Box<dyn PakSource>` として型を消して持つ。
pub trait PakSource: Read + Seek + Send {}

impl<T: Read + Seek + Send> PakSource for T {}
