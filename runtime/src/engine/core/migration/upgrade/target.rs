// ============================================================
//  upgrade/target.rs — 一括アップグレードの対象を決める
//
//  【責務】
//  (1) コマンドラインで渡された「プロジェクト」からアセットルートを決める
//  (2) アセットルート配下から、版を持つ形式のファイルを列挙する
//
//  どちらもファイルを**読むだけ**で、書き込みは行わない（`upgrade/mod.rs` の責務）。
// ============================================================

use std::path::{Path, PathBuf};

use crate::engine::core::migration::kind::FormatKind;

// ── 定数 ─────────────────────────────────────────────────────────

/// プロジェクトフォルダ直下のアセットフォルダ名。
const ASSETS_DIR_NAME: &str = "assets";

/// プロジェクトファイルの拡張子（ドット無し）。
const SEEDPROJ_EXT: &str = "seedproj";

/// 列挙から除外するフォルダの先頭文字。
///
/// `.backup`（safe_write の世代バックアップ）・`.git`・`.claude` などを一括で外す。
/// バックアップを変換対象にすると「旧版へ戻す」手段が失われるため、これは必須。
const HIDDEN_DIR_PREFIX: char = '.';

// ── アセットルートの決定 ─────────────────────────────────────────

/// コマンドラインで渡されたパスからアセットルートを決める。
///
/// 受け付ける形:
/// - `<プロジェクト>/Game.seedproj`  … そのファイルのあるフォルダの `assets/`
/// - `<プロジェクト>`（フォルダ）     … 直下に `assets/` があればそれ
/// - `<プロジェクト>/assets`（フォルダ）… それ自身をアセットルートとみなす
///
/// 見つからない場合は利用者向けの日本語メッセージを返す。
pub fn resolve_assets_root(input: &Path) -> Result<PathBuf, String> {
    if input.is_file() {
        let is_project_file = input
            .extension()
            .map(|e| e.to_string_lossy().to_ascii_lowercase() == SEEDPROJ_EXT)
            .unwrap_or(false);
        if !is_project_file {
            return Err(format!(
                "プロジェクトファイルではありません（.{SEEDPROJ_EXT} かフォルダを指定してください）: {}",
                input.display()
            ));
        }
        let dir = input.parent().ok_or_else(|| {
            format!("プロジェクトファイルの親フォルダが取れません: {}", input.display())
        })?;
        return assets_dir_of(dir);
    }

    if input.is_dir() {
        // 直下に assets/ があればプロジェクトフォルダ、無ければそれ自身をアセットルートとみなす。
        let nested = input.join(ASSETS_DIR_NAME);
        if nested.is_dir() {
            return Ok(nested);
        }
        return Ok(input.to_path_buf());
    }

    Err(format!("指定されたパスが見つかりません: {}", input.display()))
}

/// プロジェクトフォルダ直下の `assets/` を返す（無ければエラー）。
fn assets_dir_of(project_dir: &Path) -> Result<PathBuf, String> {
    let assets = project_dir.join(ASSETS_DIR_NAME);
    if assets.is_dir() {
        Ok(assets)
    } else {
        Err(format!(
            "プロジェクト直下に {ASSETS_DIR_NAME}/ がありません: {}",
            project_dir.display()
        ))
    }
}

// ── 対象ファイルの列挙 ───────────────────────────────────────────

/// 一括アップグレードの対象ファイル 1 件。
pub struct UpgradeTarget {
    /// 対象ファイルの実パス。
    pub path: PathBuf,
    /// 拡張子から判定した形式。
    pub kind: FormatKind,
}

/// アセットルート配下から、版を持つ形式のファイルを再帰的に列挙する。
///
/// - ドットで始まるフォルダ（`.backup` など）は中へ入らない
/// - 結果はパスの昇順（レポートの並びを実行ごとに安定させるため）
/// - 読めないフォルダは黙って飛ばす（1 つの権限エラーで全体を止めない）
///
/// 形式の判定は**アセットルート相対パス**で行う。拡張子が `.json` の形式
/// （`terrain/layers.json` / `project_settings.json` など）は置き場所でしか
/// 見分けられないため、ファイル名だけでは判定しない。
pub fn collect_targets(assets_root: &Path) -> Vec<UpgradeTarget> {
    let mut found = Vec::new();
    collect_recursive(assets_root, assets_root, &mut found);
    found.sort_by(|a, b| a.path.cmp(&b.path));
    found
}

/// `collect_targets` の再帰本体。
///
/// * `assets_root` … 形式判定に使う相対パスの基準（再帰しても変わらない）
/// * `dir`         … 今見ているフォルダ
fn collect_recursive(assets_root: &Path, dir: &Path, out: &mut Vec<UpgradeTarget>) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let Ok(file_type) = entry.file_type() else {
            continue;
        };
        if file_type.is_dir() {
            if is_hidden_dir(&path) {
                continue;
            }
            collect_recursive(assets_root, &path, out);
        } else if file_type.is_file() {
            if let Some(kind) = kind_of(assets_root, &path) {
                out.push(UpgradeTarget { path, kind });
            }
        }
    }
}

/// アセットルート相対パスからファイルの形式を判定する。対象外なら `None`。
pub fn kind_of(assets_root: &Path, path: &Path) -> Option<FormatKind> {
    let relative = path.strip_prefix(assets_root).ok()?;
    FormatKind::from_asset_relative_path(&relative.to_string_lossy())
}

/// フォルダ名がドットで始まるか（列挙から外す対象か）。
fn is_hidden_dir(path: &Path) -> bool {
    path.file_name()
        .map(|n| n.to_string_lossy().starts_with(HIDDEN_DIR_PREFIX))
        .unwrap_or(false)
}

// ── レポート用のパス表記 ─────────────────────────────────────────

/// レポートに出すパスを作る（アセットルートの親から見た相対・スラッシュ区切り）。
///
/// `<プロジェクト>/assets/scenes/Main.scene` なら `assets/scenes/Main.scene` になる。
/// 相対化できない場合は実パスをそのまま返す。
pub fn display_path(assets_root: &Path, path: &Path) -> String {
    let base = assets_root.parent().unwrap_or(assets_root);
    let rel = path.strip_prefix(base).unwrap_or(path);
    rel.to_string_lossy().replace('\\', "/")
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    /// テスト用の一時ディレクトリを作る（外部クレートに依存しない）。
    fn temp_dir(tag: &str) -> PathBuf {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("seed_migration_target_{tag}_{nanos}"));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    /// プロジェクトフォルダ・`.seedproj`・assets ルートのどれを渡しても同じ場所に着くこと。
    #[test]
    fn resolves_assets_root_from_every_accepted_form() {
        let root = temp_dir("resolve");
        let project = root.join("MyGame");
        let assets = project.join(ASSETS_DIR_NAME);
        fs::create_dir_all(&assets).unwrap();
        let proj_file = project.join("MyGame.seedproj");
        fs::write(&proj_file, "{}").unwrap();

        assert_eq!(resolve_assets_root(&project).unwrap(), assets);
        assert_eq!(resolve_assets_root(&proj_file).unwrap(), assets);
        assert_eq!(resolve_assets_root(&assets).unwrap(), assets);

        fs::remove_dir_all(&root).ok();
    }

    /// 存在しないパス・プロジェクトでないファイルはエラーになること。
    #[test]
    fn rejects_unknown_paths() {
        let root = temp_dir("reject");
        assert!(resolve_assets_root(&root.join("nope")).is_err());

        let txt = root.join("readme.txt");
        fs::write(&txt, "x").unwrap();
        assert!(resolve_assets_root(&txt).is_err());

        fs::remove_dir_all(&root).ok();
    }

    /// 対象拡張子だけを拾い、ドットで始まるフォルダには入らないこと。
    #[test]
    fn collects_only_versioned_formats_and_skips_hidden_dirs() {
        let assets = temp_dir("collect").join(ASSETS_DIR_NAME);
        fs::create_dir_all(assets.join("scenes")).unwrap();
        fs::create_dir_all(assets.join(".backup").join("scenes")).unwrap();
        fs::create_dir_all(assets.join("prefabs")).unwrap();

        fs::write(assets.join("scenes/Main.scene"), "{}").unwrap();
        fs::write(assets.join("prefabs/Fish.actor"), "{}").unwrap();
        fs::write(assets.join("prefabs/Hud.actor2d"), "{}").unwrap();
        fs::write(assets.join("prefabs/note.txt"), "x").unwrap();
        // バックアップ配下は対象外
        fs::write(assets.join(".backup/scenes/Main.20260101-000000.scene"), "{}").unwrap();

        let targets = collect_targets(&assets);
        let names: Vec<String> = targets
            .iter()
            .map(|t| display_path(&assets, &t.path))
            .collect();
        assert_eq!(
            names,
            vec![
                "assets/prefabs/Fish.actor",
                "assets/prefabs/Hud.actor2d",
                "assets/scenes/Main.scene",
            ],
            "列挙結果が想定と違う: {names:?}"
        );
        assert_eq!(targets[0].kind, FormatKind::Actor);
        assert_eq!(targets[2].kind, FormatKind::Scene);

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// 拡張子で見分けられない形式は**置き場所**で拾い、同名の無関係ファイルは拾わないこと。
    #[test]
    fn collects_json_formats_by_location_only() {
        let assets = temp_dir("collect_json").join(ASSETS_DIR_NAME);
        fs::create_dir_all(assets.join("terrain")).unwrap();
        fs::create_dir_all(assets.join("ui")).unwrap();

        fs::write(assets.join("project_settings.json"), "{}").unwrap();
        fs::write(assets.join("terrain/layers.json"), "{}").unwrap();
        fs::write(assets.join("terrain/props.json"), "{}").unwrap();
        fs::write(assets.join("terrain/cover_materials.json"), "{}").unwrap();
        // 置き場所が違う同名ファイル・地形フォルダの別 JSON は対象外
        fs::write(assets.join("ui/layers.json"), "{}").unwrap();
        fs::write(assets.join("terrain/terrain_meta.json"), "{}").unwrap();

        let targets = collect_targets(&assets);
        let found: Vec<(String, FormatKind)> = targets
            .iter()
            .map(|t| (display_path(&assets, &t.path), t.kind))
            .collect();
        assert_eq!(
            found,
            vec![
                (
                    "assets/project_settings.json".to_string(),
                    FormatKind::ProjectSettings
                ),
                (
                    "assets/terrain/cover_materials.json".to_string(),
                    FormatKind::TerrainCoverMaterials
                ),
                ("assets/terrain/layers.json".to_string(), FormatKind::TerrainLayers),
                ("assets/terrain/props.json".to_string(), FormatKind::TerrainProps),
            ],
            "列挙結果が想定と違う: {found:?}"
        );

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }

    /// 新しく対象へ入れた拡張子（`.anim` / `.mat` / `.postfx` / `.inputmap` /
    /// `.sprite_mesh`）が拾われること。
    #[test]
    fn collects_newly_supported_extensions() {
        let assets = temp_dir("collect_ext").join(ASSETS_DIR_NAME);
        fs::create_dir_all(&assets).unwrap();
        for name in [
            "Swim.anim",
            "Water.mat",
            "Blur.postfx",
            "Game.inputmap",
            "Body.sprite_mesh",
        ] {
            fs::write(assets.join(name), "{}").unwrap();
        }

        let kinds: Vec<FormatKind> = collect_targets(&assets).iter().map(|t| t.kind).collect();
        for expected in [
            FormatKind::Anim,
            FormatKind::Material,
            FormatKind::Postfx,
            FormatKind::InputMap,
            FormatKind::SpriteMesh,
        ] {
            assert!(kinds.contains(&expected), "{expected} が拾われていない: {kinds:?}");
        }

        fs::remove_dir_all(assets.parent().unwrap()).ok();
    }
}
