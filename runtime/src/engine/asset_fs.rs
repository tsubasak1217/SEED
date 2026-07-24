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
                Err(_) => None,
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

/// 任意のパス文字列を「読み込み API がアセットルート基準で解決できる形」へ正規化する。
///
/// アセット参照の文字列は出所によって 3 系統ある:
///   1. `assets://terrain/rock.png` … 仮想パス（PAK 対応。そのまま通す）
///   2. `C:\proj\assets\rock.png`   … 絶対パス（エディタ外のファイル。そのまま通す）
///   3. `terrain/rock.png`          … **アセットルート相対**（layers.json などデータ側の標準表記）
///
/// 3 は `read_bytes` がそのまま `std::fs::read` へ渡すとカレントディレクトリ基準になり、
/// 実行ディレクトリ次第で読めたり読めなかったりする。ここで `assets://` を補って
/// アセットルート基準へ固定する。1・2 は一切変更しないので既存の呼び出しは無影響。
///
/// 区切り文字は `assets://` の規約に合わせて `/` へ統一する。
pub fn normalize_asset_path(path: &str) -> String {
    // ── 1. 仮想パスはそのまま ──
    if is_virtual(path) {
        return path.to_string();
    }
    // ── 2. 絶対パスはそのまま ──
    //   Windows では `C:\...` / `C:/...` / `\\server\share` が絶対と判定される。
    //   先頭が `/` だけのパスも Path::is_absolute では非絶対（Windows）だが、
    //   その形はアセット相対の書き間違いとみなしてルート基準へ寄せた方が実害が無い。
    if Path::new(path).is_absolute() {
        return path.to_string();
    }
    // ── 3. 相対パスはアセットルート基準の仮想パスへ ──
    //   先頭の `./` や `/` は仮想パスとして無意味なので落としておく。
    let rel = path.replace('\\', "/");
    let rel = rel.trim_start_matches("./").trim_start_matches('/');
    format!("{ASSETS_SCHEME}{rel}")
}

/// 絶対パスを仮想パスに変換する。
///
/// 絶対パスがアセットルート配下でない場合は元の絶対パスを文字列で返す。
pub fn to_virtual(absolute: &str) -> String {
    if let Some(root) = ASSETS_ROOT.get() {
        // 比較のため '\\' → '/' 正規化
        let abs_norm = absolute.replace('\\', "/");
        let root_norm = root.to_string_lossy().replace('\\', "/");
        let root_prefix = format!("{root_norm}/");
        if abs_norm.starts_with(&root_prefix) {
            let rel = &abs_norm[root_prefix.len()..];
            return format!("{ASSETS_SCHEME}{rel}");
        }
    }
    absolute.to_string()
}

/// アセットファイルの最終更新時刻（UNIX 秒）を返す。
///
/// キャッシュ無効化キー用途（.postfx のライブ編集反映など）。
/// - ファイルシステム上に実体があればその mtime を返す。
/// - PAK 内アセット・解決不能・メタデータ取得失敗時は 0 を返す
///   （0 は「不変」を意味し、パッケージモードでは一度焼けば再焼きしない）。
pub fn mtime(path: &str) -> u64 {
    let resolved = resolve(path);
    std::fs::metadata(&resolved)
        .and_then(|m| m.modified())
        .ok()
        .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
        .map(|d| d.as_secs())
        .unwrap_or(0)
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

/// 画像バイトを読み込み、`image::RgbaImage` として返す（フォールバック・ログ無し）。
///
/// `read_image` と違い、失敗を呼び出し側へ返す。用途ごとに安全な既定値が異なる
/// （法線マップにマゼンタを敷くと法線が壊れる、など）ケースで使う。
/// パスは `normalize_asset_path` で正規化してから読むため、アセットルート相対でも読める。
pub fn read_image_result(path: &str) -> std::io::Result<image::RgbaImage> {
    let normalized = normalize_asset_path(path);
    let bytes = read_bytes(&normalized)?;
    image::load_from_memory(&bytes)
        .map(|img| img.to_rgba8())
        .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))
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

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 仮想パスは一切書き換えられないこと（既存挙動の保護）。
    #[test]
    fn normalize_keeps_virtual_path_as_is() {
        assert_eq!(
            normalize_asset_path("assets://terrain/textures/rock_normal.jpeg"),
            "assets://terrain/textures/rock_normal.jpeg"
        );
        // スキーム付きなら区切りも触らない。
        assert_eq!(
            normalize_asset_path("assets://a\\b.png"),
            "assets://a\\b.png"
        );
    }

    /// 絶対パスは一切書き換えられないこと（エディタモード・後方互換の保護）。
    #[test]
    fn normalize_keeps_absolute_path_as_is() {
        let win = r"C:\proj\assets\terrain\rock.png";
        assert_eq!(normalize_asset_path(win), win);
        // ドライブ + スラッシュ区切りも絶対として扱われる。
        let win_fwd = "C:/proj/assets/terrain/rock.png";
        assert_eq!(normalize_asset_path(win_fwd), win_fwd);
        // UNC パス。
        let unc = r"\\server\share\rock.png";
        assert_eq!(normalize_asset_path(unc), unc);
    }

    /// 相対パスはアセットルート基準（assets:// 付き）へ寄せられること。
    /// これが本修正の主眼（layers.json のテクスチャパスが cwd 基準で解決されていた）。
    #[test]
    fn normalize_promotes_relative_path_to_virtual() {
        assert_eq!(
            normalize_asset_path("terrain/textures/rock_normal.jpeg"),
            "assets://terrain/textures/rock_normal.jpeg"
        );
        // 区切りは '/' に統一される。
        assert_eq!(
            normalize_asset_path(r"terrain\textures\rock_normal.jpeg"),
            "assets://terrain/textures/rock_normal.jpeg"
        );
        // 先頭の "./" と "/" は落とす。
        assert_eq!(normalize_asset_path("./tex/a.png"), "assets://tex/a.png");
        assert_eq!(normalize_asset_path("/tex/a.png"), "assets://tex/a.png");
    }

    /// 存在しないパスは Err を返すこと（フォールバック画像で握り潰さない）。
    #[test]
    fn read_image_result_reports_error() {
        let r = read_image_result("no/such/texture_for_test.png");
        assert!(r.is_err(), "存在しないパスは Err になるべき");
    }
}
