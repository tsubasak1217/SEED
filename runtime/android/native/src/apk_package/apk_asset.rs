// ============================================================
//  apk_package/apk_asset.rs — APK 内の 1 ファイルの読み口（ndk の Asset を包んだもの）
//
//  【なぜ包むのか】
//  ndk 0.9 の `Asset`（AAsset*）は Read + Seek を実装するが Send ではない
//  （生ポインタを持つので自動では付かず、ndk 側も付けていない）。一方エンジンの asset_fs は
//  PAK の読み口をグローバルの Mutex に置き、メインスレッド・モデルの非同期ロードのワーカー・rayon・
//  音声スレッドから使うため、読み口に Send を求める（engine::pak::PakSource）。
//  そこで Send だけを足した薄い包みを作る（Sync は足さない）。
//
//  【Send にしてよい理由】（下の unsafe impl の SAFETY）
//  - NDK の資料が禁じているのは「AAsset を複数スレッドで共有して同時に使うこと」
//    （"AAsset objects are NOT thread-safe, and should not be shared across threads"）。
//    AAsset は APK のファイル記述子／マップ領域と読み取り位置を持つだけの C++ オブジェクト
//    （AOSP frameworks/base/native/android/asset_manager.cpp の AAsset → android::Asset）で、
//    スレッドローカルな状態や「作ったスレッドでしか使えない」制約は持たない。
//  - この型は `&mut self` 経由でしか読まず、Sync を実装しないので共有参照からは使えない。
//    asset_fs は Mutex で包んで持つため、同時に 2 スレッドから触られることはない。
//  - 元の AAssetManager は android-activity がアプリ全体の AssetManager へのグローバル参照を
//    リークして保持しており、プロセスの終わりまで有効（AndroidApp::asset_manager の実装）。
//    したがってどのスレッドで閉じても（Drop → AAsset_close）よい。
//
//  性能上の注意: 読み口 1 本を Mutex で直列化して使う（デスクトップのファイルと同じ）。
//  並列に読みたくなったら、Asset::open_file_descriptor（非圧縮で格納した pak なら使える）で
//  得た fd に対する pread へ置き換えれば Mutex が要らなくなる（docs/backlog.md）。
// ============================================================

use std::io::{self, Read, Seek, SeekFrom};

use winit::platform::android::activity::ndk::asset::Asset;

/// APK 内の 1 ファイルの読み口（ndk の Asset に Send を足した包み）。
pub struct ApkAsset(Asset);

// SAFETY: 上の【Send にしてよい理由】のとおり。AAsset はスレッドに結び付いた状態を持たず、
// この型は &mut self でしか使えない（Sync を実装しない）ため、所有権ごと別スレッドへ渡しても
// 同時アクセスは起きない。
unsafe impl Send for ApkAsset {}

impl ApkAsset {
    /// 開いた Asset を包む。
    pub fn new(asset: Asset) -> Self {
        Self(asset)
    }
}

impl Read for ApkAsset {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        self.0.read(buf)
    }
}

impl Seek for ApkAsset {
    fn seek(&mut self, pos: SeekFrom) -> io::Result<u64> {
        self.0.seek(pos)
    }
}
