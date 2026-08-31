// ============================================================
//  terrain/dir_ref.rs — 「地形フォルダ」参照の正規化・解決（純関数のみ）
//
//  【責務】
//    地形一式（密度 .tvox / 散布 .tscatter / カバー .tcover）は 1 つのフォルダに
//    まとまって保存される。そのフォルダ（＝地形フォルダ）を指す文字列参照を
//    「アセットルート相対・スラッシュ区切り・末尾スラッシュ無し」の正準形へ
//    正規化し、そこから各ファイルの仮想パスを組み立てる。
//
//  【なぜ独立ファイルか】
//    パス解決は App / ECS / GPU に一切依存しない純粋な文字列演算であり、
//    ここだけを単体テストできるようにしておくと「アセットルート外への保存拒否」
//    という安全要件を実行環境なしで検証できる。terrain モジュールの他ファイルと
//    同じく「エンジン非依存の純粋層」に置く。
//
//  【参照の持ち主】
//    `.scene` の `terrain_dir` キー（`Scene::terrain_dir`）。
//    キーが無い旧シーンは `None` として読まれ、`resolve_or_default` が
//    従来の既定パス `terrain/<シーン名>` を返す（後方互換）。
// ============================================================

use crate::engine::terrain::chunk_coord::ChunkCoord;

// ─── 定数（マジックナンバー・マジック文字列の集約） ─────────────────────

/// 既定の地形フォルダの親ディレクトリ名（アセットルート直下）。
///
/// 参照を持たない旧シーンの既定パスは `terrain/<シーン名>` であり、
/// この定数がその `terrain` の部分にあたる。
pub const DEFAULT_TERRAIN_ROOT_DIR: &str = "terrain";

/// 地形フォルダ内のチャンクファイルの基本名接頭辞（`chunk_X_Y_Z`）。
const CHUNK_FILE_PREFIX: &str = "chunk";

/// 密度データの拡張子（ドット込み）。
pub const TVOX_EXT: &str = ".tvox";
/// 散布データの拡張子（ドット込み）。
pub const TSCATTER_EXT: &str = ".tscatter";
/// カバー場データの拡張子（ドット込み）。
pub const TCOVER_EXT: &str = ".tcover";

/// パス区切りの正準形（`assets://` 仮想パスの規約に合わせる）。
const PATH_SEP: char = '/';

/// Windows のパス区切り（正規化時に `PATH_SEP` へ潰す）。
const WIN_PATH_SEP: char = '\\';

/// 親ディレクトリを指すパス要素（アセットルート脱出の検出に使う）。
const PARENT_COMPONENT: &str = "..";

/// カレントディレクトリを指すパス要素（無害なので正規化時に取り除く）。
const CURRENT_COMPONENT: &str = ".";

/// Windows のドライブレター区切り（`C:` の `:`）。絶対パス判定に使う。
const DRIVE_COLON: char = ':';

// ─── エラー型 ───────────────────────────────────────────────────────────

/// 地形フォルダ参照の正規化に失敗した理由。
///
/// エディタへそのまま文字列で返せるよう `reason()` で日本語メッセージを持つ
/// （IPC の `TERRAIN_SAVE_AS_ERROR:` の引数になる）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TerrainDirError {
    /// 空文字・スラッシュだけなど、フォルダ名として意味を成さない。
    Empty,
    /// 絶対パス（`C:\...` / `/...` / UNC）であり、アセットルート内を指していない。
    Absolute,
    /// `..` を含み、アセットルートの外へ出る可能性がある。
    Escapes,
}

impl TerrainDirError {
    /// エディタ表示用の理由文字列。
    pub fn reason(self) -> &'static str {
        match self {
            Self::Empty => "地形フォルダ名が空です",
            Self::Absolute => "アセットルート外（絶対パス）は保存先にできません",
            Self::Escapes => "アセットルート外（.. を含むパス）は保存先にできません",
        }
    }
}

// ─── 正規化 ─────────────────────────────────────────────────────────────

/// 任意の入力文字列を「アセットルート相対の地形フォルダ参照」正準形へ正規化する。
///
/// 受け付ける入力:
///   - `terrain/Scene1`                  … アセットルート相対（正準形そのもの）
///   - `assets://terrain/Scene1`         … 仮想パス（スキームを剥がす）
///   - `terrain\Scene1\`                 … Windows 区切り・末尾スラッシュ付き
///
/// 拒否する入力（アセットルート外への保存を禁じるため）:
///   - `C:\proj\assets\terrain\Scene1`   … 絶対パス（`TerrainDirError::Absolute`）
///   - `/terrain/Scene1`                 … ルート起点（同上）
///   - `terrain/../../secret`            … `..` を含む（`TerrainDirError::Escapes`）
///   - `""` / `"/"` / `"assets://"`      … 空（`TerrainDirError::Empty`）
///
/// 【注意】エディタは絶対パスを渡さない設計（下の `to_relative_under_root` で
/// アセットルート相対へ落としてから送る）だが、手書きの `.scene` から
/// 絶対パスが混入する可能性があるため、ここでも必ず拒否する。
pub fn normalize(raw: &str) -> Result<String, TerrainDirError> {
    // ── 仮想パススキームを剥がす ──
    let body = raw
        .strip_prefix(crate::engine::asset_fs::ASSETS_SCHEME)
        .unwrap_or(raw);

    // ── 区切りを '/' へ統一する ──
    let unified = body.replace(WIN_PATH_SEP, PATH_SEP.to_string().as_str());
    let trimmed = unified.trim();

    // ── 絶対パスの検出 ──
    //   ・先頭が '/' … ルート起点 or UNC（'//server/share'）
    //   ・2 文字目が ':' … Windows ドライブレター（'C:/...'）
    if trimmed.starts_with(PATH_SEP) {
        return Err(TerrainDirError::Absolute);
    }
    if trimmed.chars().nth(1) == Some(DRIVE_COLON) {
        return Err(TerrainDirError::Absolute);
    }

    // ── 要素へ分解して掃除する ──
    let mut parts: Vec<&str> = Vec::new();
    for part in trimmed.split(PATH_SEP) {
        let p = part.trim();
        // 空要素（連続スラッシュ）と '.' は無害なので落とす。
        if p.is_empty() || p == CURRENT_COMPONENT {
            continue;
        }
        // '..' は正準化で吸収せず**必ず拒否**する。
        //   吸収してしまうと `a/../../b` のような入力が「たまたまルート内に
        //   収まったかどうか」で通ったり弾かれたりし、拒否条件が読めなくなる。
        if p == PARENT_COMPONENT {
            return Err(TerrainDirError::Escapes);
        }
        parts.push(p);
    }

    if parts.is_empty() {
        return Err(TerrainDirError::Empty);
    }
    Ok(parts.join(PATH_SEP.to_string().as_str()))
}

/// シーン名から既定の地形フォルダ参照（`terrain/<シーン名>`）を作る。
///
/// 地形フォルダ参照を持たない旧シーンの互換パスであり、
/// 本機能導入前に `handle_terrain_save` が固定で書いていた場所と一致する。
pub fn default_for_scene(scene_name: &str) -> String {
    // シーン名にパス区切りや空白が混じっていても正準形へ落ちるよう normalize を通す。
    // 失敗（シーン名が空など）したときはルート直下の `terrain` を使う。
    normalize(&format!("{DEFAULT_TERRAIN_ROOT_DIR}{PATH_SEP}{scene_name}"))
        .unwrap_or_else(|_| DEFAULT_TERRAIN_ROOT_DIR.to_string())
}

/// シーンが持つ地形フォルダ参照を解決する。参照が無い／壊れているときは既定パスへ落とす。
///
/// - `Some(有効な参照)` … 正規化した値
/// - `Some(壊れた参照)` … 既定パス（読み込みを失敗させない。壊れた参照で
///                        アセットルート外を読みに行かないための安全側の挙動）
/// - `None`             … 既定パス（旧シーン互換）
pub fn resolve_or_default(scene_terrain_dir: Option<&str>, scene_name: &str) -> String {
    match scene_terrain_dir {
        Some(raw) => normalize(raw).unwrap_or_else(|_| default_for_scene(scene_name)),
        None => default_for_scene(scene_name),
    }
}

// ─── パス組み立て ───────────────────────────────────────────────────────

/// チャンク 1 個のファイル基本名（`chunk_X_Y_Z`。拡張子なし）を返す。
pub fn chunk_stem(coord: ChunkCoord) -> String {
    format!("{CHUNK_FILE_PREFIX}_{}_{}_{}", coord.x, coord.y, coord.z)
}

/// 地形フォルダ参照＋チャンク座標から `.tvox` の仮想パスを組み立てる。
///
/// 例: `("terrain/Scene1", (0,0,0))` → `assets://terrain/Scene1/chunk_0_0_0.tvox`
pub fn tvox_virtual_path(dir: &str, coord: ChunkCoord) -> String {
    format!(
        "{}{dir}{PATH_SEP}{}{TVOX_EXT}",
        crate::engine::asset_fs::ASSETS_SCHEME,
        chunk_stem(coord)
    )
}

/// `.tvox` パスの拡張子を差し替えて、同じチャンクの兄弟ファイル
/// （`.tscatter` / `.tcover`）のパスを導く。
///
/// 実行時の読込は `TerrainChunkComponent::tvox_path` しか手掛かりを持たないため、
/// 散布・カバーのパスは必ずこの規則で導かれる。地形フォルダをどこへ移しても
/// 「.tvox の隣に一式が揃う」という不変条件がこの 1 関数に閉じている。
pub fn sibling_path(tvox_path: &str, ext: &str) -> String {
    match tvox_path.strip_suffix(TVOX_EXT) {
        Some(stem) => format!("{stem}{ext}"),
        // 拡張子が .tvox でない異常入力は、末尾に足すだけに留める
        // （既存の `tscatter_path_from_tvox` / `tcover_path_from_tvox` と同じ挙動）。
        None => format!("{tvox_path}{ext}"),
    }
}

/// 地形フォルダの仮想パス（`assets://<dir>`）を返す。表示・ログ用。
pub fn dir_virtual_path(dir: &str) -> String {
    format!("{}{dir}", crate::engine::asset_fs::ASSETS_SCHEME)
}

/// `.tvox` の仮想パスから、それが入っているフォルダ参照を逆算する。
///
/// 【用途】`terrain_dir` キーを持たない旧 `.scene` の互換。
/// チャンクコンポーネントの `tvox_path` は昔から保存されているので、
/// そこからフォルダを復元すれば「既定パス以外の場所に置かれた旧地形」も
/// そのまま読み書きできる（既定パスへ決め打ちすると保存先がずれる）。
///
/// 逆算できないとき（`/` を含まない・正規化に失敗）は `None`。
pub fn dir_from_tvox_path(tvox_path: &str) -> Option<String> {
    let body = tvox_path
        .strip_prefix(crate::engine::asset_fs::ASSETS_SCHEME)
        .unwrap_or(tvox_path);
    let unified = body.replace(WIN_PATH_SEP, PATH_SEP.to_string().as_str());
    let idx = unified.rfind(PATH_SEP)?;
    normalize(&unified[..idx]).ok()
}

/// アセットルート配下の絶対パスを、アセットルート相対の地形フォルダ参照へ落とす。
///
/// エディタのフォルダ選択（絶対パスが返る）をランタイムへ送る前段で使うことを
/// 想定した純関数。ルート外・ルートそのものは `Err` を返す。
///
/// 比較は「区切りを `/` へ統一し、Windows の大文字小文字非依存に合わせて
/// 小文字化した接頭辞一致」で行う。
pub fn to_relative_under_root(root: &str, abs: &str) -> Result<String, TerrainDirError> {
    let unify = |s: &str| s.replace(WIN_PATH_SEP, PATH_SEP.to_string().as_str());
    let root_u = unify(root);
    let abs_u = unify(abs);
    let root_trim = root_u.trim_end_matches(PATH_SEP);
    let abs_trim = abs_u.trim_end_matches(PATH_SEP);

    let root_key = root_trim.to_ascii_lowercase();
    let abs_key = abs_trim.to_ascii_lowercase();

    // ルートそのもの＝地形フォルダ名が無い（アセットルート直下へ地形一式を
    // ばら撒くと他アセットと混ざるので、フォルダ指定を必須にする）。
    if abs_key == root_key {
        return Err(TerrainDirError::Empty);
    }
    let rest = abs_key
        .strip_prefix(&root_key)
        .filter(|r| r.starts_with(PATH_SEP))
        .ok_or(TerrainDirError::Absolute)?;
    // 大文字小文字は元の文字列側から取り直す（表示・ファイル名の見た目を保つ）。
    //   `to_ascii_lowercase` はバイト長を変えないので、末尾から同じ長さを切り出せば
    //   元のケースのまま同じ範囲になる（非 ASCII のフォルダ名でも安全）。
    let rest_original = &abs_trim[abs_trim.len() - rest.len()..];
    // 先頭の区切りを落としてから正規化する（残すと「絶対パス」と判定されてしまう）。
    normalize(rest_original.trim_start_matches(PATH_SEP))
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 正準形はそのまま通る。
    #[test]
    fn normalize_passes_canonical_form() {
        assert_eq!(normalize("terrain/Scene1").unwrap(), "terrain/Scene1");
    }

    /// 仮想パススキーム・Windows 区切り・余分なスラッシュは正準形へ落ちる。
    #[test]
    fn normalize_cleans_scheme_separators_and_slashes() {
        assert_eq!(normalize("assets://terrain/Scene1").unwrap(), "terrain/Scene1");
        assert_eq!(normalize("terrain\\Scene1\\").unwrap(), "terrain/Scene1");
        assert_eq!(normalize("terrain//./Scene1/").unwrap(), "terrain/Scene1");
        assert_eq!(normalize("levels/forest/terrain").unwrap(), "levels/forest/terrain");
    }

    /// アセットルート外（絶対パス）は拒否する。
    #[test]
    fn normalize_rejects_absolute_paths() {
        assert_eq!(normalize("C:\\proj\\assets\\terrain"), Err(TerrainDirError::Absolute));
        assert_eq!(normalize("/terrain/Scene1"), Err(TerrainDirError::Absolute));
        assert_eq!(normalize("//server/share/terrain"), Err(TerrainDirError::Absolute));
    }

    /// アセットルート外（`..`）は拒否する。
    #[test]
    fn normalize_rejects_parent_escape() {
        assert_eq!(normalize("../secret"), Err(TerrainDirError::Escapes));
        assert_eq!(normalize("terrain/../../secret"), Err(TerrainDirError::Escapes));
        // ルート内へ戻る `..` も一律で拒否する（判定を単純に保つ）。
        assert_eq!(normalize("terrain/sub/../Scene1"), Err(TerrainDirError::Escapes));
    }

    /// 空・スラッシュだけは拒否する。
    #[test]
    fn normalize_rejects_empty() {
        assert_eq!(normalize(""), Err(TerrainDirError::Empty));
        assert_eq!(normalize("   "), Err(TerrainDirError::Empty));
        assert_eq!(normalize("assets://"), Err(TerrainDirError::Empty));
        // スラッシュだけの入力は「ルート起点の絶対パス」として弾かれる
        // （空扱いより先に絶対パス判定が走る。どちらにせよ保存先にはならない）。
        assert_eq!(normalize("///"), Err(TerrainDirError::Absolute));
    }

    /// 参照が無い旧シーンは従来の既定パス（`terrain/<シーン名>`）へ解決される。
    #[test]
    fn resolve_falls_back_to_legacy_default() {
        assert_eq!(resolve_or_default(None, "Scene1"), "terrain/Scene1");
    }

    /// 参照があればそちらが勝つ。
    #[test]
    fn resolve_prefers_scene_reference() {
        assert_eq!(
            resolve_or_default(Some("levels/forest/ground"), "Scene1"),
            "levels/forest/ground"
        );
        // 仮想パス表記でも同じ場所へ解決される。
        assert_eq!(
            resolve_or_default(Some("assets://levels/forest/ground"), "Scene1"),
            "levels/forest/ground"
        );
    }

    /// 壊れた参照（ルート外）は既定パスへフォールバックする（読み込みを止めない）。
    #[test]
    fn resolve_falls_back_when_reference_is_outside_root() {
        assert_eq!(resolve_or_default(Some("../secret"), "Scene1"), "terrain/Scene1");
        assert_eq!(resolve_or_default(Some(""), "Scene1"), "terrain/Scene1");
    }

    /// 仮想パスの組み立ては従来の固定パスと同じ形になる（後方互換の要）。
    #[test]
    fn tvox_virtual_path_matches_legacy_layout() {
        assert_eq!(
            tvox_virtual_path("terrain/Scene1", ChunkCoord::new(1, -2, 3)),
            "assets://terrain/Scene1/chunk_1_-2_3.tvox"
        );
    }

    /// `.tvox` パスからフォルダ参照を逆算できる（旧シーン互換の要）。
    #[test]
    fn dir_from_tvox_path_recovers_folder() {
        assert_eq!(
            dir_from_tvox_path("assets://terrain/Scene1/chunk_0_0_0.tvox").unwrap(),
            "terrain/Scene1"
        );
        assert_eq!(
            dir_from_tvox_path("levels/forest/ground/chunk_0_0_0.tvox").unwrap(),
            "levels/forest/ground"
        );
        // フォルダが無い（ファイル名だけ）／ルート外は逆算しない。
        assert_eq!(dir_from_tvox_path("chunk_0_0_0.tvox"), None);
        assert_eq!(dir_from_tvox_path("C:/x/chunk_0_0_0.tvox"), None);
    }

    /// アセットルート配下の絶対パスは相対参照へ落ちる。
    #[test]
    fn to_relative_accepts_paths_under_root() {
        assert_eq!(
            to_relative_under_root("C:\\proj\\assets", "C:\\proj\\assets\\levels\\forest").unwrap(),
            "levels/forest"
        );
        // ドライブレターの大文字小文字が食い違っても受け入れる。
        assert_eq!(
            to_relative_under_root("c:/proj/assets", "C:/proj/assets/terrain/Scene1").unwrap(),
            "terrain/Scene1"
        );
    }

    /// アセットルート外・ルートそのものは拒否する。
    #[test]
    fn to_relative_rejects_outside_and_root_itself() {
        assert_eq!(
            to_relative_under_root("C:/proj/assets", "C:/proj/other/terrain"),
            Err(TerrainDirError::Absolute)
        );
        assert_eq!(
            to_relative_under_root("C:/proj/assets", "C:/proj/assets"),
            Err(TerrainDirError::Empty)
        );
        // 接頭辞が「たまたま一致するだけ」の兄弟ディレクトリは拒否する。
        assert_eq!(
            to_relative_under_root("C:/proj/assets", "C:/proj/assets_backup/terrain"),
            Err(TerrainDirError::Absolute)
        );
    }
}
