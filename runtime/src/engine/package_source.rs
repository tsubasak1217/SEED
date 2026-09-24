// ============================================================
//  package_source.rs — 配布物（パッケージ）のファイルを読む「読み口」
//
//  【役割】
//  配布物のルートにあるファイルを「ルートからの相対パス」で開くための抽象。
//  配布物の相対構成（core::package_layout が正典）は全プラットフォームで同じで、
//    <配布物のルート>/
//      assets.pak            … アセット（PAK）
//      assets/<相対パス>      … PAK に入れずに置いたアセット（PAK に無いときのフォールバック先）
//      bin/ ...              … スクリプト等の副次ファイル（デスクトップのみ。Android は段階B）
//  ルートの実体だけがプラットフォームで違う:
//    デスクトップ … 実行ファイルのフォルダ（ファイルシステム）
//    Android      … APK の assets/seed/（AAssetManager 経由でしか読めない）
//
//  【誰が実装するか】
//  - Android: runtime/android/native の ApkPackageSource。エンジン本体は ndk に依存しない。
//  - デスクトップ: 実装しない。配布物のルートはファイルシステムそのものなので、従来どおり
//    asset_fs::init（PAK のパス指定）と std::fs で読む（デスクトップの挙動を一切変えないため）。
//
//  【asset_fs からの使われ方】
//  起動時に LaunchArgs.package_source で受け取り、`open_pak` で assets.pak を開いて PAK モードにする。
//  PAK に無いアセットは `loose_asset_path` の場所（<ルート>/assets/<相対パス>）から読み、
//  それも無ければファイルシステム（アセットルート）へ落ちる。詳細は docs/android.md §13。
// ============================================================

use std::io::{self, Read};

use crate::engine::core::package_layout;
use crate::engine::pak::{PakReader, PakSource};

/// 配布物のルートにあるファイルを相対パスで開く読み口。
///
/// asset_fs のグローバルに置かれ、アセットを読むすべてのスレッドから同時に呼ばれ得るため
/// `Send + Sync` を課す（Android の実装が持つ AAssetManager はスレッド安全）。
pub trait PackageSource: Send + Sync {
    /// 配布物のルートからの相対パス（`/` 区切り）でファイルを開く。
    ///
    /// 無いときは `io::ErrorKind::NotFound` を返す。
    /// 返す読み口は呼び出しごとに独立している（同じファイルを複数回開いてよい）。
    fn open(&self, relative: &str) -> io::Result<Box<dyn PakSource>>;

    /// 相対パスが指す場所を、人が読める形で返す（ログ用。例 `apk:seed/assets.pak`）。
    fn describe(&self, relative: &str) -> String;

    /// ファイル全体をバイト列として読む。
    ///
    /// 既定の実装は `open` して最後まで読むだけ。
    fn read_all(&self, relative: &str) -> io::Result<Vec<u8>> {
        let mut source = self.open(relative)?;
        let mut bytes = Vec::new();
        source.read_to_end(&mut bytes)?;
        Ok(bytes)
    }
}

/// 配布物の assets.pak を開いて PakReader にする。
///
/// # 引数
/// * `package` - 配布物の読み口
///
/// # 戻り値
/// PAK が無ければ `NotFound`、形式が壊れていれば `InvalidData` 等のエラー。
pub fn open_pak(package: &dyn PackageSource) -> io::Result<PakReader> {
    PakReader::from_boxed(package.open(package_layout::PAK_FILE_NAME)?)
}

/// 仮想パスの相対部分（`assets://` の後ろ）から、PAK の外に置いたアセットの
/// 配布物内の相対パスを作る【純関数】。
///
/// 例: `models\a.glb` → `assets/models/a.glb`
///
/// 区切りは `/` に統一する（書き換え後のシーンには `assets://a\b.glb` のような区切りが混ざることがあり、
/// APK 内のパスは `/` しか受け付けないため）。大文字小文字はそのまま（APK 内の名前は大文字小文字を区別する。
/// PAK だけが区別しない）。
pub fn loose_asset_path(asset_relative: &str) -> String {
    let rel = asset_relative.replace('\\', "/");
    let rel = rel.trim_start_matches("./").trim_start_matches('/');
    format!("{}/{rel}", package_layout::LOOSE_ASSETS_DIR_NAME)
}

// ============================================================
//  テスト支援（他モジュールのテストからも使う）
// ============================================================

/// メモリ上のファイル表で配布物を模した読み口（単体テスト専用）。
#[cfg(test)]
pub(crate) struct MemoryPackage {
    /// 配布物のルートからの相対パス → 中身。
    pub files: std::collections::HashMap<String, Vec<u8>>,
}

#[cfg(test)]
impl MemoryPackage {
    /// (相対パス, 中身) の組から作る。
    pub fn new(files: &[(&str, &[u8])]) -> Self {
        Self {
            files: files.iter().map(|(p, b)| (p.to_string(), b.to_vec())).collect(),
        }
    }
}

#[cfg(test)]
impl PackageSource for MemoryPackage {
    fn open(&self, relative: &str) -> io::Result<Box<dyn PakSource>> {
        match self.files.get(relative) {
            Some(bytes) => Ok(Box::new(io::Cursor::new(bytes.clone()))),
            None => Err(io::Error::new(io::ErrorKind::NotFound, relative.to_string())),
        }
    }

    fn describe(&self, relative: &str) -> String {
        format!("memory:{relative}")
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::pak::tests::build_pak_bytes;

    /// 配布物のルートの assets.pak を開けること（Android の APK 内 pak と同じ経路）。
    #[test]
    fn open_pak_reads_package_root_pak() {
        let pak = build_pak_bytes(&[("scenes/Main.scene", b"scene")]);
        let package = MemoryPackage::new(&[(package_layout::PAK_FILE_NAME, &pak)]);

        let mut reader = open_pak(&package).expect("PAK を開けるべき");
        assert_eq!(reader.entry_count(), 1);
        assert_eq!(reader.read("scenes/main.scene").as_deref(), Some(&b"scene"[..]));
    }

    /// 配布物に PAK が無ければ NotFound（呼び出し側はこれで「パッケージ実行ではない」と判断できる）。
    #[test]
    fn open_pak_without_pak_is_not_found() {
        let package = MemoryPackage::new(&[("assets/project_settings.json", b"{}")]);
        let err = open_pak(&package).err().expect("PAK が無いのでエラー");
        assert_eq!(err.kind(), io::ErrorKind::NotFound);
    }

    /// PAK 外のアセットの場所は <ルート>/assets/<相対パス>（区切りは / に統一）。
    #[test]
    fn loose_asset_path_uses_package_layout() {
        assert_eq!(loose_asset_path("project_settings.json"), "assets/project_settings.json");
        assert_eq!(loose_asset_path("models\\sub\\A.glb"), "assets/models/sub/A.glb");
        assert_eq!(loose_asset_path("/scenes/Main.scene"), "assets/scenes/Main.scene");
        assert_eq!(loose_asset_path("./scenes/Main.scene"), "assets/scenes/Main.scene");
    }

    /// 既定の read_all は open した中身を最後まで読むこと。
    #[test]
    fn read_all_reads_whole_file() {
        let package = MemoryPackage::new(&[("bin/a.dll", b"0123456789")]);
        assert_eq!(package.read_all("bin/a.dll").unwrap(), b"0123456789");
        assert_eq!(
            package.read_all("bin/missing.dll").err().map(|e| e.kind()),
            Some(io::ErrorKind::NotFound)
        );
    }
}
