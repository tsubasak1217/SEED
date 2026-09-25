// ============================================================
//  hot_reload/asset_key.rs — キャッシュのキーが差し替え対象のアセットを指すかの照合（純粋な処理）
//
//  【なぜ要るか】
//  エンジンの各キャッシュ（モデル・スプライトの画像・音声 等）は、読み込んだときのパス文字列をキーにしている。
//  その表記は出どころで揺れる:
//    assets://models/Fish.glb        … シーン・プレハブに保存された仮想パス（Android・配布物はこれだけ）
//    C:\proj\assets\models\Fish.glb  … PC のエディタが渡した絶対パス（区切りも \ と / が混ざる）
//    models/Fish.glb                 … アセットルートからの相対パス（データ側の表記）
//  差し替えの命令は「アセットルートからの相対パス」で届くので、キーを同じ形へ直してから比べる。
//  比べるときは区切り（\ と /）と大文字小文字を問わない（pak の引き方・Windows のファイルシステムと同じ）。
//  統合バッチのキーは「モデルのパス＋区切り（SOH）＋マテリアルの署名」なので、区切りより前だけを見る。
// ============================================================

use std::path::Path;

use crate::engine::asset_fs::ASSETS_SCHEME;

/// 統合バッチのキーでモデルのパスと署名を分ける区切り（components/model_component.rs の BATCH_KEY_SEPARATOR と同じ）。
const BATCH_KEY_SEPARATOR: char = '\u{1}';

/// 照合用の区切り。
const PATH_SEPARATOR: char = '/';

/// Windows の区切り。
const BACKSLASH: char = '\\';

/// Windows のドライブ名の区切り（`C:`）。
const DRIVE_SEPARATOR: char = ':';

/// キャッシュのキーが、差し替え対象のアセット（アセットルートからの相対パス）を指すか【純関数】。
///
/// # 引数
/// * `key`         - キャッシュのキー（仮想パス・絶対パス・相対パス。統合バッチのキーでもよい）
/// * `relative`    - 差し替え対象（アセットルートからの相対パス。区切り /）
/// * `assets_root` - アセットルート（絶対パスのキーを相対パスへ直すのに使う。分からなければ None）
pub fn key_refers_to(key: &str, relative: &str, assets_root: Option<&Path>) -> bool {
    let path_part = key.split(BATCH_KEY_SEPARATOR).next().unwrap_or(key);
    match key_to_relative(path_part, assets_root) {
        Some(key_relative) => comparable(&key_relative) == comparable(relative),
        None => false,
    }
}

/// キーをアセットルートからの相対パスへ直す（アセットルートの外を指す絶対パスなら None）【純関数】。
fn key_to_relative(key: &str, assets_root: Option<&Path>) -> Option<String> {
    if let Some(relative) = key.strip_prefix(ASSETS_SCHEME) {
        return Some(relative.to_string());
    }
    let unified = unify(key);
    if Path::new(key).is_absolute() || unified.starts_with(PATH_SEPARATOR) || has_drive_prefix(&unified) {
        // 絶対パス: アセットルートの下なら、その下の部分（大文字小文字を問わずに前を比べる）
        let root = unify(&assets_root?.to_string_lossy());
        let root_prefix = format!("{}{PATH_SEPARATOR}", root.trim_end_matches(PATH_SEPARATOR));
        let head = unified.get(..root_prefix.len())?;
        return head
            .eq_ignore_ascii_case(&root_prefix)
            .then(|| unified[root_prefix.len()..].to_string());
    }
    Some(unified)
}

/// Windows のドライブ名で始まるか（`C:/…`。Android でビルドしたときも Windows の絶対パスを絶対パスとして扱うため）。
fn has_drive_prefix(unified: &str) -> bool {
    let mut chars = unified.chars();
    matches!((chars.next(), chars.next()), (Some(letter), Some(DRIVE_SEPARATOR)) if letter.is_ascii_alphabetic())
}

/// 区切りを / に揃える。
fn unify(path: &str) -> String {
    path.replace(BACKSLASH, &PATH_SEPARATOR.to_string())
}

/// 照合用の形（区切り /・先頭の ./ と / を落とす・小文字）。
fn comparable(path: &str) -> String {
    unify(path)
        .trim_start_matches("./")
        .trim_start_matches(PATH_SEPARATOR)
        .to_lowercase()
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 仮想パスのキーは区切り・大文字小文字を問わずに照合する。
    #[test]
    fn virtual_keys_match_relative_paths() {
        assert!(key_refers_to("assets://models/Fish.glb", "models/Fish.glb", None));
        assert!(key_refers_to("assets://Models\\FISH.glb", "models/fish.glb", None));
        assert!(!key_refers_to("assets://models/Fish2.glb", "models/Fish.glb", None));
        assert!(!key_refers_to("assets://other/models/Fish.glb", "models/Fish.glb", None));
    }

    /// アセットルートの下の絶対パスのキーは、その下の部分で照合する（PC のエディタが渡す表記）。
    #[test]
    fn absolute_keys_under_assets_root_match() {
        let root = Path::new(r"C:\proj\assets");
        assert!(key_refers_to(r"C:\proj\assets\models\Fish.glb", "models/Fish.glb", Some(root)));
        assert!(key_refers_to("c:/PROJ/assets/models/fish.glb", "models/Fish.glb", Some(root)));
        // アセットルートの外・アセットルートが分からないときは一致しない
        assert!(!key_refers_to(r"C:\other\models\Fish.glb", "models/Fish.glb", Some(root)));
        assert!(!key_refers_to(r"C:\proj\assets\models\Fish.glb", "models/Fish.glb", None));
        // 名前の途中で切れる別のフォルダ（assets2）を取り違えない
        assert!(!key_refers_to(r"C:\proj\assets2\models\Fish.glb", "models/Fish.glb", Some(root)));
    }

    /// 相対パスのキー（データ側の表記）もそのまま照合する。
    #[test]
    fn relative_keys_match() {
        assert!(key_refers_to("ui/button.png", "ui/button.png", None));
        assert!(key_refers_to("./ui\\Button.png", "ui/button.png", None));
    }

    /// 統合バッチのキー（パス＋SOH＋署名）はパスの部分だけで照合する。
    #[test]
    fn batch_keys_compare_path_part_only() {
        let key = format!("assets://models/Fish.glb{BATCH_KEY_SEPARATOR}mat:abc");
        assert!(key_refers_to(&key, "models/Fish.glb", None));
        assert!(!key_refers_to(&key, "models/Fish.glb.bak", None));
    }
}
