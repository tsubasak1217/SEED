// ============================================================
//  asset_fs.rs — アセットファイルシステム抽象層
//
//  【役割】
//  エディタモードとパッケージモードで統一されたアセット読み込み API を提供する。
//
//  ・エディタモード:   絶対パスをそのままファイルシステムから読む
//  ・パッケージモード: assets:// 仮想パスを PAK ファイルから読む
//                       PAK に存在しない場合はファイルシステムへフォールバック
//
//  【仮想パス形式】
//  "assets://textures/player.png"
//   ↑ スキーム       ↑ アセットルートからの相対パス
//
//  【初期化】
//  `init(assets_root, pak_path)` をアプリ起動時に一度だけ呼ぶ。
//  - assets_root: アセットフォルダの絶対パス
//  - pak_path:    assets.pak の絶対パス（存在する場合のみ Some）
// ============================================================

use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};

use super::pak::PakReader;

// ============================================================
//  グローバル状態
// ============================================================

/// アセットルートディレクトリの絶対パス。
static ASSETS_ROOT: OnceLock<PathBuf> = OnceLock::new();

/// PAK リーダー（存在する場合のみ Some）。
/// Mutex で包んで Seek による &mut 要件に対応する。
static PAK: OnceLock<Option<Mutex<PakReader>>> = OnceLock::new();

/// 仮想パスのスキーム文字列。
pub const ASSETS_SCHEME: &str = "assets://";

// ============================================================
//  初期化
// ============================================================

/// アセット読み込み層を初期化する。アプリ起動時に一度だけ呼ぶこと。
///
/// - `assets_root`: アセットフォルダの絶対パス
/// - `pak_path`:    assets.pak のパス（存在しない場合は None を渡す）
pub fn init(assets_root: PathBuf, pak_path: Option<&Path>) {
    let _ = ASSETS_ROOT.set(assets_root);

    let pak = pak_path.and_then(|p| {
        if p.exists() {
            match PakReader::open(p) {
                Ok(reader) => Some(Mutex::new(reader)),
                Err(_)     => None,
            }
        } else {
            None
        }
    });

    let _ = PAK.set(pak);
}

// ============================================================
//  パス解決
// ============================================================

/// 初期化済みアセットルートを返す。未初期化の場合は None。
pub fn root() -> Option<&'static PathBuf> {
    ASSETS_ROOT.get()
}

/// 仮想パスかどうかを判定する。
pub fn is_virtual(path: &str) -> bool {
    path.starts_with(ASSETS_SCHEME)
}

/// 仮想パス / 絶対パスを実際の `PathBuf` に変換する。
///
/// 仮想パス `"assets://textures/player.png"` の場合は
/// `{assets_root}/textures/player.png` に変換する。
/// 絶対パスの場合はそのまま返す。
pub fn resolve(path: &str) -> PathBuf {
    if let Some(rel) = path.strip_prefix(ASSETS_SCHEME) {
        if let Some(root) = ASSETS_ROOT.get() {
            // '/' → OS のパス区切りに変換
            let rel_os = rel.replace('/', std::path::MAIN_SEPARATOR_STR);
            return root.join(rel_os);
        }
    }
    PathBuf::from(path)
}

/// 絶対パスを仮想パスに変換する。
///
/// 絶対パスがアセットルート配下でない場合は元の絶対パスを文字列で返す。
pub fn to_virtual(absolute: &str) -> String {
    if let Some(root) = ASSETS_ROOT.get() {
        // 比較のため '\\' → '/' 正規化
        let abs_norm  = absolute.replace('\\', "/");
        let root_norm = root.to_string_lossy().replace('\\', "/");
        let root_prefix = format!("{root_norm}/");
        if abs_norm.starts_with(&root_prefix) {
            let rel = &abs_norm[root_prefix.len()..];
            return format!("{ASSETS_SCHEME}{rel}");
        }
    }
    absolute.to_string()
}

// ============================================================
//  読み込み API
// ============================================================

/// バイト列としてアセットを読み込む。
///
/// 1. PAK に存在すれば PAK から読む
/// 2. 存在しなければファイルシステムから読む（エディタモード兼フォールバック）
pub fn read_bytes(path: &str) -> std::io::Result<Vec<u8>> {
    // 仮想パスの場合は相対パスを取り出す
    if let Some(rel) = path.strip_prefix(ASSETS_SCHEME) {
        // PAK から読む
        if let Some(Some(pak_mutex)) = PAK.get() {
            if let Ok(mut pak) = pak_mutex.lock() {
                if let Some(data) = pak.read(rel) {
                    return Ok(data);
                }
            }
        }
        // ファイルシステムへフォールバック
        let file_path = resolve(path);
        return std::fs::read(file_path);
    }

    // 絶対パスの場合はそのまま読む（エディタモード・後方互換）
    std::fs::read(path)
}

/// テキスト（UTF-8）としてアセットを読み込む。
///
/// BOM（U+FEFF）が付いている場合は除去する。
pub fn read_string(path: &str) -> std::io::Result<String> {
    let bytes = read_bytes(path)?;
    let s = String::from_utf8(bytes)
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
    // UTF-8 BOM 除去
    Ok(s.strip_prefix('\u{FEFF}').unwrap_or(&s).to_string())
}

/// 画像バイトを読み込み、`image::RgbaImage` として返す。
///
/// 読み込み失敗時は 1×1 マゼンタ画像（エラー表示用）を返す。
pub fn read_image(path: &str) -> image::RgbaImage {
    let bytes = match read_bytes(path) {
        Ok(b) => b,
        Err(e) => {
            eprintln!("[SEED asset_fs] read_image failed: path={path:?} err={e}");
            return magenta_fallback();
        }
    };

    match image::load_from_memory(&bytes) {
        Ok(img) => img.to_rgba8(),
        Err(e) => {
            eprintln!("[SEED asset_fs] decode failed: path={path:?} err={e}");
            magenta_fallback()
        }
    }
}

// ============================================================
//  ヘルパー
// ============================================================

/// エラー時のフォールバック画像（1×1 マゼンタ）。
fn magenta_fallback() -> image::RgbaImage {
    let mut img = image::RgbaImage::new(1, 1);
    img.put_pixel(0, 0, image::Rgba([255, 0, 255, 255]));
    img
}
