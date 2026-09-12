// ============================================================
//  cache_key.rs — モデルサムネイルのキャッシュキーとキャッシュパス
// ------------------------------------------------------------
//  役割:
//    「このモデルファイルの、このサイズのサムネイル PNG はどこに置かれるか」を
//    決める純粋ロジック。GPU にも ECS にも触れないので、そのまま単体テストできる。
//
//  なぜエディタ（C#）と同じ規則でなければならないか:
//    サムネイルの表示手順は「エディタがキャッシュ PNG を探す → 無ければランタイムへ
//    生成を頼む → ランタイムが PNG を書く → エディタがそれを表示する」である。
//    探す側（C#）と書く側（Rust）がキーの計算を 1 文字でも違えると、
//    エディタは永遠にキャッシュを見つけられず、毎回生成要求を投げ続ける。
//    そこで「同じ入力から同じハッシュが出る」ことを両言語の単体テストで固定する
//    （Rust: 本ファイル末尾 / C#: editor/tests/ProjectPanelLogicTests）。
//
//  なぜ FNV-1a なのか:
//    std の DefaultHasher（SipHash）は Rust 実装に固有で、C# で再現できない。
//    FNV-1a 64bit は仕様が数行で、どの言語でもビット単位に同じ値を出せる。
//    ここで欲しいのは暗号強度ではなく「両言語で一致すること」なので FNV-1a で足りる。
// ============================================================

use std::path::{Path, PathBuf};

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// FNV-1a 64bit のオフセット基底値（仕様値）。
const FNV_OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;

/// FNV-1a 64bit の素数（仕様値）。
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

/// キャッシュディレクトリ（`cache/`）の下に作るサムネイル置き場の名前。
pub const THUMBNAIL_CACHE_SUBDIR: &str = "thumbnails";

/// 生成する PNG の拡張子（ドット込み）。
pub const THUMBNAIL_EXTENSION: &str = ".png";

/// アセット仮想パスの接頭辞。キー計算前に取り除く。
const ASSETS_SCHEME: &str = "assets://";

/// キー文字列の各項目を区切る文字。パスに現れない文字を選ぶ。
const KEY_FIELD_SEPARATOR: char = '|';

// ============================================================
//  ThumbnailCacheKey — キャッシュキーの材料
// ============================================================

/// サムネイル 1 枚を一意に決める材料。
///
/// 「パスが同じでもファイルが更新されたら別物」「同じファイルでも要求サイズが違えば別物」
/// という 2 つの要件を、4 つの材料で満たす。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ThumbnailCacheKey {
    /// アセットルートからの相対パス（`assets://` 付きでも可。正規化はキー計算時に行う）。
    pub asset_path: String,
    /// 元ファイルの最終更新時刻（Unix エポックからの**秒**）。
    ///
    /// ミリ秒・ナノ秒ではなく秒なのは、.NET（100ns 刻みの FILETIME 由来）と
    /// Rust（`SystemTime`）で端数の丸めが一致する保証がないため。
    /// 秒までなら両者とも同じ整数になる。
    /// 「同じ 1 秒の中で、サイズを変えずに内容だけ差し替える」編集は
    /// キャッシュが古いままになるが、現実には起こらないうえ、
    /// 起きてもファイルを触り直せば解消するので許容する。
    pub modified_unix_seconds: i64,
    /// 元ファイルのバイト数。更新時刻が同じでも中身が変わったことを捕まえる第 2 の材料。
    pub file_size_bytes: u64,
    /// 出力するサムネイル 1 辺のピクセル数。
    ///
    /// これをキーに含めないと、128px で作った PNG を 256px の要求が拾ってしまう。
    pub size_px: u32,
}

impl ThumbnailCacheKey {
    /// キャッシュキーの素になる正規化済み文字列を組み立てる。
    ///
    /// この文字列の書式がそのままエディタ（C#）との契約になるので、
    /// 変更するときは必ず両側のテストを同時に直すこと。
    pub fn to_key_string(&self) -> String {
        format!(
            "{}{sep}{}{sep}{}{sep}{}",
            normalize_asset_path(&self.asset_path),
            self.modified_unix_seconds,
            self.file_size_bytes,
            self.size_px,
            sep = KEY_FIELD_SEPARATOR,
        )
    }

    /// キャッシュファイル名（拡張子込み）を求める。
    pub fn to_file_name(&self) -> String {
        format!(
            "{:016x}{}",
            fnv1a_64(self.to_key_string().as_bytes()),
            THUMBNAIL_EXTENSION
        )
    }
}

// ============================================================
//  正規化とハッシュ
// ============================================================

/// アセットパスを、両言語で完全に同じ結果になる形へ正規化する。
///
/// 1. `assets://` 接頭辞を取り除く（エディタは付けて送り、ランタイムは解決済みを持つ）。
/// 2. 区切りを `/` に統一する（Windows の `\` とアセット表記の `/` を同一視する）。
/// 3. 先頭の余分な `/` を落とす。
/// 4. **ASCII 範囲だけ**小文字化する。
///
/// # なぜ ASCII だけの小文字化なのか
/// Windows のファイル名は大小文字を区別しないので小文字化そのものは要る。
/// しかし Unicode 全体の小文字化は言語ごとに結果が違う
/// （例: `İ` U+0130 は Rust の `to_lowercase` で 2 文字、.NET の `ToLowerInvariant` で 1 文字）。
/// 日本語などの非 ASCII 文字は大小の区別を持たず小文字化しても変わらないため、
/// ASCII に限定すれば「両言語で必ず一致」と「実用上の大小無視」を同時に満たせる。
pub fn normalize_asset_path(path: &str) -> String {
    let without_scheme = path.strip_prefix(ASSETS_SCHEME).unwrap_or(path);
    without_scheme
        .replace('\\', "/")
        .trim_start_matches('/')
        .chars()
        .map(|c| c.to_ascii_lowercase())
        .collect()
}

/// FNV-1a 64bit ハッシュ。エディタ（C#）と 1 ビットも違わないことが要件。
pub fn fnv1a_64(bytes: &[u8]) -> u64 {
    let mut hash = FNV_OFFSET_BASIS;
    for &byte in bytes {
        hash ^= byte as u64;
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    hash
}

// ============================================================
//  キャッシュパスの組み立て
// ============================================================

/// キャッシュディレクトリ（`<プロジェクト>/cache`）から、この鍵の PNG のパスを求める。
///
/// 実際のディレクトリ作成は呼び出し側（書き出す直前）が行う。
/// ここはあくまで「どこに置くか」を決めるだけの純関数にしておく。
pub fn thumbnail_path_in(cache_dir: &Path, key: &ThumbnailCacheKey) -> PathBuf {
    cache_dir.join(THUMBNAIL_CACHE_SUBDIR).join(key.to_file_name())
}

/// ファイルのメタデータからキャッシュキーを作る。
///
/// メタデータが読めない（ファイルが無い・PAK の中だけにある等）ときは `None`。
/// 呼び出し側はそれを「サムネイルを作れない」として扱う。
pub fn key_from_file(asset_path: &str, resolved: &Path, size_px: u32) -> Option<ThumbnailCacheKey> {
    let meta = std::fs::metadata(resolved).ok()?;
    let modified = meta.modified().ok()?;
    // エポック以前（時計が狂っている環境）でも破綻しないよう、符号付きで扱う。
    let modified_unix_seconds = match modified.duration_since(std::time::UNIX_EPOCH) {
        Ok(after) => after.as_secs() as i64,
        Err(before) => -(before.duration().as_secs() as i64),
    };
    Some(ThumbnailCacheKey {
        asset_path: asset_path.to_string(),
        modified_unix_seconds,
        file_size_bytes: meta.len(),
        size_px,
    })
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// C# 側テストと共有する固定入力。
    /// **この値を変えるときは editor/tests/ProjectPanelLogicTests も同時に直すこと。**
    fn fixed_key() -> ThumbnailCacheKey {
        ThumbnailCacheKey {
            asset_path: "assets://mainGame/models/Yasi.glb".to_string(),
            modified_unix_seconds: 1_700_000_000,
            file_size_bytes: 123_456,
            size_px: 128,
        }
    }

    /// FNV-1a が仕様どおりの値を出すこと（他言語実装との照合の土台）。
    #[test]
    fn fnv1a_matches_reference_vectors() {
        // FNV 公式のテストベクタ
        assert_eq!(fnv1a_64(b""), FNV_OFFSET_BASIS);
        assert_eq!(fnv1a_64(b"a"), 0xaf63_dc4c_8601_ec8c);
        assert_eq!(fnv1a_64(b"foobar"), 0x8594_4171_f739_67e8);
    }

    /// 正規化が「スキーム除去・区切り統一・先頭スラッシュ除去・ASCII 小文字化」を行うこと。
    #[test]
    fn normalization_unifies_separators_and_case() {
        assert_eq!(
            normalize_asset_path(r"assets://mainGame\Models\Yasi.GLB"),
            "maingame/models/yasi.glb"
        );
        assert_eq!(normalize_asset_path("/Foo/Bar.obj"), "foo/bar.obj");
        // 非 ASCII はそのまま（大小の区別が無いので変換不要）
        assert_eq!(normalize_asset_path("assets://モデル/魚.glb"), "モデル/魚.glb");
    }

    /// 表記ゆれのあるパスが同じキャッシュファイルへ落ちること。
    #[test]
    fn path_spelling_variants_share_one_cache_file() {
        let canonical = fixed_key().to_file_name();
        let mut variant = fixed_key();
        variant.asset_path = r"mainGame\models\YASI.glb".to_string();
        assert_eq!(variant.to_file_name(), canonical);
    }

    /// 材料が 1 つでも違えばファイル名が変わること（キャッシュの取り違えを防ぐ）。
    #[test]
    fn every_ingredient_changes_the_file_name() {
        let base = fixed_key().to_file_name();

        let mut newer = fixed_key();
        newer.modified_unix_seconds += 1;
        assert_ne!(newer.to_file_name(), base, "更新時刻が効いていない");

        let mut resized = fixed_key();
        resized.file_size_bytes += 1;
        assert_ne!(resized.to_file_name(), base, "ファイルサイズが効いていない");

        let mut bigger = fixed_key();
        bigger.size_px = 256;
        assert_ne!(bigger.to_file_name(), base, "要求ピクセル数が効いていない");

        let mut other = fixed_key();
        other.asset_path = "mainGame/models/hut.glb".to_string();
        assert_ne!(other.to_file_name(), base, "パスが効いていない");
    }

    /// キー文字列とファイル名が、エディタ（C#）と共有する固定値であること。
    ///
    /// ここが変わるとエディタが作った／探すパスと食い違う。
    /// **C# 側の同名テストと必ず同じ値を書くこと。**
    #[test]
    fn fixed_input_produces_the_agreed_file_name() {
        let key = fixed_key();
        assert_eq!(key.to_key_string(), "maingame/models/yasi.glb|1700000000|123456|128");
        // 期待値はこのテストが初めて通ったときの実測値を固定したもの。
        assert_eq!(key.to_file_name(), FIXED_FILE_NAME);
    }

    /// 固定入力に対する期待ファイル名（C# 側テストと共有する契約値）。
    const FIXED_FILE_NAME: &str = "63cf730ec7b8e0eb.png";

    /// キャッシュパスが `<cache>/thumbnails/<ハッシュ>.png` になること。
    #[test]
    fn cache_path_is_under_the_thumbnails_subdirectory() {
        let path = thumbnail_path_in(Path::new("C:/proj/cache"), &fixed_key());
        assert_eq!(
            path,
            Path::new("C:/proj/cache")
                .join(THUMBNAIL_CACHE_SUBDIR)
                .join(FIXED_FILE_NAME)
        );
    }
}
