// ============================================================
//  actor_file.rs — `.actor` / `.actor2d` ファイルの読み書き（唯一の経路）
//
//  【なぜ 1 本にまとめるのか】
//  以前は `.actor` の読み込みが 3 か所（`scene.rs` の `load_actor` /
//  `load_actor_into`、`app/prefab_ops.rs` の `load_actor_data_with_hash`）に
//  重複していた。形式のマイグレーションのように「読み込みの入口で必ず通す処理」を
//  足すたびに 3 か所へ書き写すことになり、必ずどれかが漏れる。
//  読み書きの入口をこのファイルへ集約し、各呼び出し側はここを呼ぶだけにする。
//
//  【守っている順序（プレハブのハッシュ）】
//  `Actor::prefab_hash` は**ファイルの生テキスト**の FNV ハッシュである
//  （`app/prefab_ops.rs::prefab_content_hash`）。したがって
//  **「生テキストを読む → ハッシュを取る → そのあと変換する」**の順を必ず守る。
//  変換後の値からハッシュを取ると、ディスク上のファイルと値が食い違う。
//  そのため `load_with_raw` は生テキストも一緒に返す。
//
//  【保存】
//  `.actor` はプレハブの本体であり、壊すと全インスタンスへ波及する。
//  書き込みは常に `safe_write`（旧版を `.backup/` へ退避 → `.tmp` → rename）を通し、
//  先頭に現行の `format_version` を刻む。
//  版の欄を `ActorData` 構造体に足していないのは、`ActorData` が `.scene` の中へ
//  入れ子で使われるため（シーン内の各アクタに版が付いてしまう）。
// ============================================================

use std::path::Path;

use crate::engine::core::migration::{self, FormatKind, MigrationError};
use crate::engine::structs::objects::actor::ActorData;

// ── エラー型 ─────────────────────────────────────────────────────

/// `.actor` / `.actor2d` の読み書きで起こりうる失敗。
#[derive(Debug)]
pub enum ActorFileError {
    /// ファイルを読めない・書けない。
    Io(std::io::Error),
    /// JSON として読めない、または `ActorData` に当てはまらない。
    Json(serde_json::Error),
    /// 版の判定・変換に失敗した（未来版の拒否を含む）。
    Migration(MigrationError),
}

impl std::fmt::Display for ActorFileError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ActorFileError::Io(e) => write!(f, "読み書きに失敗しました: {e}"),
            ActorFileError::Json(e) => write!(f, "JSON として読めませんでした: {e}"),
            ActorFileError::Migration(e) => write!(f, "{e}"),
        }
    }
}

impl std::error::Error for ActorFileError {}

impl From<std::io::Error> for ActorFileError {
    fn from(e: std::io::Error) -> Self {
        Self::Io(e)
    }
}
impl From<serde_json::Error> for ActorFileError {
    fn from(e: serde_json::Error) -> Self {
        Self::Json(e)
    }
}
impl From<MigrationError> for ActorFileError {
    fn from(e: MigrationError) -> Self {
        Self::Migration(e)
    }
}

// ── 読み込み ─────────────────────────────────────────────────────

/// `.actor` / `.actor2d` を読み、必要なら現行版へ変換して `ActorData` を返す。
///
/// * `src` … `assets://` 仮想パスまたは実パス（`asset_fs::read_string` の規約）
///
/// 変換はメモリ上だけで行い、ファイルは書き換えない。
pub fn load(src: &str) -> Result<ActorData, ActorFileError> {
    let raw = crate::engine::asset_fs::read_string(src)?;
    Ok(parse(&raw)?)
}

/// `.actor` / `.actor2d` を読み、`ActorData` と**生テキスト**を返す。
///
/// プレハブの版（`prefab_hash`）は生テキストから取るため、変換前の内容が要る。
/// 読み込みとハッシュ算出をファイル 1 回読みで済ませる用途に使う。
pub fn load_with_raw(src: &str) -> Result<(ActorData, String), ActorFileError> {
    let raw = crate::engine::asset_fs::read_string(src)?;
    let data = parse(&raw)?;
    Ok((data, raw))
}

/// `.actor` の JSON テキストから `ActorData` を作る（ファイル読み込みを伴わない）。
///
/// BOM の除去・版の判定・変換はすべて `migration::load_json` が行う。
pub fn parse(raw: &str) -> Result<ActorData, MigrationError> {
    migration::load_json(FormatKind::Actor, raw)
}

// ── 保存 ─────────────────────────────────────────────────────────

/// `ActorData` を `.actor` のテキスト（現行版の刻印付き）にする。
pub fn to_json(data: &ActorData) -> Result<String, serde_json::Error> {
    migration::to_stamped_pretty_json(FormatKind::Actor, data)
}

/// 変換済みの `.actor` の `Value` を、**エンジンが保存するのと同じ並び**のテキストにする。
///
/// 一括アップグレード（`migration::upgrade`）から呼ぶ。`Value` をそのまま
/// `to_string_pretty` すると `serde_json::Map` の並び（アルファベット順）になり、
/// 中身が 1 つも変わらないファイルでも全行が差分になってしまう。
/// `ActorData` を経由すれば欄の並びは宣言順のまま（＝普通に保存したときと同じ）になる。
///
/// 副作用として「`ActorData` として読めないファイルは書き換えない」という安全弁にもなる。
pub fn text_from_value(value: serde_json::Value) -> Result<String, ActorFileError> {
    let data: ActorData = serde_json::from_value(value)?;
    Ok(to_json(&data)?)
}

/// `ActorData` を `.actor` / `.actor2d` ファイルへ保存する。
///
/// 親フォルダが無ければ作り、`safe_write`（旧版を `.backup/` へ退避 → `.tmp` → rename）で書く。
/// 戻り値はバックアップに失敗したときの警告（保存自体は成功している）。
pub fn save(path: &Path, data: &ActorData) -> Result<Option<String>, ActorFileError> {
    let json = to_json(data)?;
    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            std::fs::create_dir_all(parent)?;
        }
    }
    Ok(super::safe_write::write_atomic_with_backup(path, &json)?)
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::migration::kind::JSON_VERSION_KEY;

    /// 旧表記を含む版なし `.actor`（v1 相当）。
    const LEGACY: &str = r#"{
        "name": "Fish",
        "components": [
            { "name": "FX", "component": { "type": "ParticleEmitterComponent",
              "data": { "blend": "additive", "shape": "point" } } }
        ],
        "children": []
    }"#;

    /// 版なしのテキストが変換されて読めること（旧 enum 表記が現行値になる）。
    #[test]
    fn parses_legacy_text_through_migration() {
        let data = parse(LEGACY).expect("旧形式が読めること");
        assert_eq!(data.name, "Fish");
        // 変換後の値が ParticleBlend::Add / ParticleShape::Pixel になっていること。
        // 直接の比較を避け、再直列化した JSON で確かめる（コンポーネント型に依存しない）。
        let json = serde_json::to_string(&data).unwrap();
        assert!(json.contains("\"blend\":\"add\""), "{json}");
        assert!(json.contains("\"shape\":\"pixel\""), "{json}");
    }

    /// 保存したテキストはトップレベルにだけ版を持ち、子アクタには付かないこと。
    #[test]
    fn stamps_version_only_at_the_top_level() {
        let mut data = parse(LEGACY).unwrap();
        // 子アクタを 1 つ足して、入れ子へ版が付かないことを確かめる
        let child = parse(LEGACY).unwrap();
        data.children.push(child);

        let json = to_json(&data).unwrap();
        assert_eq!(
            json.matches(JSON_VERSION_KEY).count(),
            1,
            "版の欄はトップレベルの 1 か所だけであること:\n{json}"
        );
        // 先頭に来ていること
        assert!(
            json.lines().nth(1).unwrap_or_default().contains(JSON_VERSION_KEY),
            "{json}"
        );

        // 書いたものを読み戻せること
        let back = parse(&json).expect("保存したテキストが読み戻せること");
        assert_eq!(back.name, "Fish");
        assert_eq!(back.children.len(), 1);
    }

    /// 未来の版は読み込みを拒否すること。
    #[test]
    fn refuses_future_version() {
        let future = FormatKind::Actor.current_version() + 1;
        let text = format!(
            r#"{{"{JSON_VERSION_KEY}":{future},"name":"X","components":[],"children":[]}}"#
        );
        // `ActorData` は Debug を実装していないので `unwrap_err` は使えない。
        let Err(err) = parse(&text) else {
            panic!("未来版が拒否されていない");
        };
        assert!(matches!(err, MigrationError::FutureVersion { .. }), "{err}");
    }
}
