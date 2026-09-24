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
//  【仮想パスを読む順】（read_bytes / read_virtual_layers）
//    1. PAK（パッケージ実行のとき）
//    2. 配布物の PAK 外のアセット（配布物の読み口 PackageSource を渡されたときだけ。
//       Android の APK 内 assets/seed/assets/<相対パス>。engine::package_source 参照）
//    3. ファイルシステムの <アセットルート>/<相対パス>（エディタモード兼フォールバック）
//  デスクトップの配布物は 2 を持たない（PAK 外のアセット＝実行ファイルの隣の assets/ が
//  そのまま 3 のアセットルートなので、従来どおり 1 → 3 の順になる）。
//
//  【初期化】
//  アプリ起動時に一度だけ、次のどちらかを呼ぶ（App::init_asset_fs）。
//  - `init(assets_root, pak_path)` … assets.pak をファイルパスで開く（開けなければ黙って PAK 無し）
//  - `init_with(assets_root, pak, package)` … 開いた PAK と配布物の読み口を直接渡す
//    （Android の APK 内 pak。デスクトップの起動も開けなかった理由をログに残すためこちらを使う）
// ============================================================

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock};

use super::package_source::{self, PackageSource};
use super::pak::PakReader;

// ============================================================
//  グローバル状態
// ============================================================

/// アセットルートディレクトリの絶対パス。
static ASSETS_ROOT: OnceLock<PathBuf> = OnceLock::new();

/// PAK リーダー（存在する場合のみ Some）。
/// Mutex で包んで Seek による &mut 要件に対応する。
///
/// 【スレッド安全性】メインスレッド・モデルの非同期ロードのワーカー・rayon・音声スレッドから読まれる。
/// 読み口は 1 本なので読み出しは Mutex で直列化する（デスクトップのファイル・Android の APK 内アセットとも同じ）。
static PAK: OnceLock<Option<Mutex<PakReader>>> = OnceLock::new();

/// 配布物の読み口（Android の APK など、ファイルシステムの外にある配布物のときだけ Some）。
///
/// PAK に無いアセットを配布物の `assets/<相対パス>` から読むのに使う。
/// `PackageSource` は `Send + Sync`（同時に呼ばれてよい）なので Mutex では包まない。
static PACKAGE: OnceLock<Option<Arc<dyn PackageSource>>> = OnceLock::new();

/// 仮想パスのスキーム文字列。
pub const ASSETS_SCHEME: &str = "assets://";

// ============================================================
//  初期化
// ============================================================

/// アセット読み込み層を初期化する。アプリ起動時に一度だけ呼ぶこと。
///
/// - `assets_root`: アセットフォルダの絶対パス
/// - `pak_path`:    assets.pak のパス（存在しない場合は None を渡す）
///
/// PAK を開けなかった場合は黙って PAK 無し（ファイルシステムのみ）で初期化する。
/// 開けなかった理由をログに残したい呼び出し側は、自分で `PakReader::open` して `init_with` を使う。
pub fn init(assets_root: PathBuf, pak_path: Option<&Path>) {
    let pak = pak_path
        .filter(|p| p.exists())
        .and_then(|p| PakReader::open(p).ok());
    init_with(assets_root, pak, None);
}

/// 開いた PAK と配布物の読み口を直接渡して初期化する。アプリ起動時に一度だけ呼ぶこと。
///
/// - `assets_root`: アセットフォルダの絶対パス（ファイルシステムへのフォールバック先）
/// - `pak`:         開いた assets.pak（無ければ None ＝パッケージ実行ではない）
/// - `package`:     配布物の読み口（PAK 外のアセットを配布物から読む場合だけ Some。Android の APK）
///
/// 2 回目以降の呼び出しは無視される（`OnceLock`。最初の 1 回だけが効く）。
pub fn init_with(
    assets_root: PathBuf,
    pak: Option<PakReader>,
    package: Option<Arc<dyn PackageSource>>,
) {
    let _ = ASSETS_ROOT.set(assets_root);
    let _ = PAK.set(pak.map(Mutex::new));
    let _ = PACKAGE.set(package);
}

// ============================================================
//  パス解決
// ============================================================

/// 初期化済みアセットルートを返す。未初期化の場合は None。
pub fn root() -> Option<&'static PathBuf> {
    ASSETS_ROOT.get()
}

/// パッケージ実行（assets.pak を読み込んで動作している）かを返す。
///
/// PAK が存在する = エディタから起動されたリポジトリ内実行ではなく、
/// 配布された実行ファイルとして動いている、という判定に使う。
/// セーブデータの保存先切り替え（`core::save::path`）がこれを見る。
pub fn is_packaged() -> bool {
    matches!(PAK.get(), Some(Some(_)))
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
/// 仮想パスは次の順に探す（詳細は `read_virtual_layers`）。
/// 1. PAK に存在すれば PAK から読む
/// 2. 配布物の読み口があれば、配布物の PAK 外（`assets/<相対パス>`）から読む（Android の APK）
/// 3. どちらにも無ければファイルシステムから読む（エディタモード兼フォールバック）
pub fn read_bytes(path: &str) -> std::io::Result<Vec<u8>> {
    // 仮想パスの場合は相対パスを取り出す
    if let Some(rel) = path.strip_prefix(ASSETS_SCHEME) {
        let pak = PAK.get().and_then(Option::as_ref);
        let package = PACKAGE.get().and_then(Option::as_ref).map(|p| p.as_ref());
        return read_virtual_layers(rel, pak, package, &resolve(path));
    }

    // 絶対パスの場合はそのまま読む（エディタモード・後方互換）
    std::fs::read(path)
}

/// 仮想パスの相対部分を、PAK → 配布物の PAK 外 → ファイルシステムの順で読む【層を引数で受ける】。
///
/// グローバル状態に触らないので、層の組み合わせ（デスクトップの配布物＝PAK とファイルシステム、
/// Android の配布物＝PAK と APK とファイルシステム、エディタ＝ファイルシステムのみ）を単体テストで検証できる。
/// `read_bytes` はグローバルの層を渡すだけ。
///
/// # 引数
/// * `rel`     - `assets://` を外した相対パス（区切り・大文字小文字は PAK 側が吸収する）
/// * `pak`     - PAK（パッケージ実行でなければ None）
/// * `package` - 配布物の読み口（APK など。デスクトップ・エディタは None）
/// * `fs_path` - ファイルシステム上の実パス（`resolve` の結果）
fn read_virtual_layers(
    rel: &str,
    pak: Option<&Mutex<PakReader>>,
    package: Option<&dyn PackageSource>,
    fs_path: &Path,
) -> std::io::Result<Vec<u8>> {
    // ── 1. PAK ──
    if let Some(pak_mutex) = pak {
        if let Ok(mut pak) = pak_mutex.lock() {
            if let Some(data) = pak.read(rel) {
                return Ok(data);
            }
        }
    }
    // ── 2. 配布物の PAK 外（Android: APK の assets/seed/assets/<相対パス>）──
    //   無い・読めないときは黙って次へ（最終的なエラーはファイルシステムの結果で返す）。
    if let Some(package) = package {
        if let Ok(data) = package.read_all(&package_source::loose_asset_path(rel)) {
            return Ok(data);
        }
    }
    // ── 3. ファイルシステムへフォールバック ──
    std::fs::read(fs_path)
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
//  列挙 API（プリフェッチ用）
// ============================================================

/// 指定拡張子のアセットを列挙し、`assets://` 仮想パスの配列で返す。
///
/// モデルの先読み（`loader::async_loader` のプリフェッチ）が
/// 「このプロジェクトにどんな `.actor` があるか」を知るために使う。
///
/// - `ext`   : 拡張子（先頭のドット無し。大文字小文字は無視）
/// - `dirs`  : アセットルート相対の絞り込みディレクトリ。空ならルート全体。
/// - `limit` : 返す最大件数（巨大プロジェクトで走査が終わらないことを防ぐ）
///
/// パッケージ実行（PAK）では PAK のエントリ表から、それ以外では実ディレクトリの
/// 再帰走査から集める。**呼び出し側はワーカースレッドから呼ぶこと**
/// （ディレクトリ走査は USB 等の遅いドライブで数百ミリ秒かかる）。
pub fn list_by_extension(ext: &str, dirs: &[String], limit: usize) -> Vec<String> {
    let ext_lower = ext.to_ascii_lowercase();
    let mut out: Vec<String> = Vec::new();

    // 絞り込みディレクトリ（`/` 区切り・末尾スラッシュ無し）へ正規化する。
    let prefixes: Vec<String> = dirs
        .iter()
        .map(|d| {
            d.replace('\\', "/")
                .trim_start_matches("./")
                .trim_start_matches('/')
                .trim_end_matches('/')
                .to_string()
        })
        .filter(|d| !d.is_empty())
        .collect();
    // 相対パスが絞り込み対象に含まれるか（絞り込み無しなら常に真）。
    let matches_prefix = |rel: &str| -> bool {
        if prefixes.is_empty() {
            return true;
        }
        prefixes
            .iter()
            .any(|p| rel.starts_with(&format!("{p}/")) || rel == p)
    };
    // 拡張子が一致するか。
    let matches_ext = |rel: &str| -> bool {
        Path::new(rel)
            .extension()
            .and_then(|e| e.to_str())
            .map(|e| e.eq_ignore_ascii_case(&ext_lower))
            .unwrap_or(false)
    };

    // ── ① PAK（パッケージ実行）─────────────────────────────
    if let Some(Some(pak_mutex)) = PAK.get() {
        if let Ok(pak) = pak_mutex.lock() {
            for rel in pak.entry_paths() {
                if out.len() >= limit {
                    break;
                }
                let rel_norm = rel.replace('\\', "/");
                if matches_ext(&rel_norm) && matches_prefix(&rel_norm) {
                    out.push(format!("{ASSETS_SCHEME}{rel_norm}"));
                }
            }
        }
        // PAK 実行でも assets/ フォルダが併存することがある（フォールバック読み）。
        // ただし二重登録を避けるため、PAK から 1 件でも取れたらそこで確定させる。
        if !out.is_empty() {
            return out;
        }
    }

    // ── ② 実ディレクトリ走査（エディタ / 開発実行）───────────
    let Some(root) = ASSETS_ROOT.get() else {
        return out;
    };
    // 走査の起点。絞り込みがあればその分だけ、無ければルート全体。
    let roots: Vec<PathBuf> = if prefixes.is_empty() {
        vec![root.clone()]
    } else {
        prefixes.iter().map(|p| root.join(p)).collect()
    };

    // 明示的なスタックで再帰せずに走査する（深いツリーでもスタックを食わない）。
    let mut stack: Vec<PathBuf> = roots;
    while let Some(dir) = stack.pop() {
        if out.len() >= limit {
            break;
        }
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            if out.len() >= limit {
                break;
            }
            let path = entry.path();
            match entry.file_type() {
                Ok(ft) if ft.is_dir() => stack.push(path),
                Ok(ft) if ft.is_file() => {
                    if matches_ext(&path.to_string_lossy()) {
                        out.push(to_virtual(&path.to_string_lossy()));
                    }
                }
                _ => {}
            }
        }
    }
    out
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

    // ── 読む順（read_virtual_layers）─────────────────────────────

    use crate::engine::package_source::MemoryPackage;
    use crate::engine::pak::tests::build_pak_bytes;

    /// メモリ上の PAK を Mutex で包んで返す（asset_fs のグローバルと同じ持ち方）。
    fn memory_pak(entries: &[(&str, &[u8])]) -> Mutex<PakReader> {
        Mutex::new(PakReader::from_source(std::io::Cursor::new(build_pak_bytes(entries))).unwrap())
    }

    /// テストごとの一時フォルダ（ファイルシステム層の実ファイルを置く）。
    fn layer_temp_dir(tag: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("seed_asset_fs_layers_{tag}_{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    /// 3 層すべてに同じアセットがあるときは PAK が勝つこと。
    #[test]
    fn layers_prefer_pak() {
        let dir = layer_temp_dir("pak_first");
        let fs_file = dir.join("a.txt");
        std::fs::write(&fs_file, b"fs").unwrap();
        let pak = memory_pak(&[("a.txt", b"pak")]);
        let package = MemoryPackage::new(&[("assets/a.txt", b"apk")]);

        let data = read_virtual_layers("a.txt", Some(&pak), Some(&package), &fs_file).unwrap();
        assert_eq!(data, b"pak");
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// PAK に無いアセットは配布物の PAK 外（assets/<相対パス>）から読むこと（Android の APK）。
    /// 区切りが \ でも配布物側は / で引く。
    #[test]
    fn layers_fall_back_to_package_loose_asset() {
        let dir = layer_temp_dir("package");
        let pak = memory_pak(&[("other.txt", b"pak")]);
        let package = MemoryPackage::new(&[("assets/dir/b.txt", b"apk")]);

        let data =
            read_virtual_layers("dir\\b.txt", Some(&pak), Some(&package), &dir.join("missing.txt")).unwrap();
        assert_eq!(data, b"apk");
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// PAK にも配布物にも無ければファイルシステムから読むこと。
    #[test]
    fn layers_fall_back_to_filesystem() {
        let dir = layer_temp_dir("fs");
        let fs_file = dir.join("c.txt");
        std::fs::write(&fs_file, b"fs").unwrap();
        let pak = memory_pak(&[("other.txt", b"pak")]);
        let package = MemoryPackage::new(&[]);

        let data = read_virtual_layers("c.txt", Some(&pak), Some(&package), &fs_file).unwrap();
        assert_eq!(data, b"fs");
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// デスクトップの配布物（配布物の読み口なし）は従来どおり PAK → ファイルシステムの順であること。
    #[test]
    fn desktop_layers_are_pak_then_filesystem() {
        let dir = layer_temp_dir("desktop");
        let fs_file = dir.join("d.txt");
        std::fs::write(&fs_file, b"fs").unwrap();
        let pak = memory_pak(&[("Models/E.glb", b"glb")]);

        // PAK の検索は大文字小文字・区切りを問わない（従来の挙動）。
        let from_pak = read_virtual_layers("models\\e.glb", Some(&pak), None, &dir.join("x")).unwrap();
        assert_eq!(from_pak, b"glb");
        let from_fs = read_virtual_layers("d.txt", Some(&pak), None, &fs_file).unwrap();
        assert_eq!(from_fs, b"fs");
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// どの層にも無ければファイルシステムのエラー（NotFound）を返すこと。
    #[test]
    fn layers_report_not_found() {
        let dir = layer_temp_dir("none");
        let pak = memory_pak(&[]);
        let package = MemoryPackage::new(&[]);
        let err = read_virtual_layers("none.txt", Some(&pak), Some(&package), &dir.join("none.txt"))
            .err()
            .expect("どこにも無いのでエラー");
        assert_eq!(err.kind(), std::io::ErrorKind::NotFound);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
