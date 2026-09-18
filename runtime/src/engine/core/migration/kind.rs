// ============================================================
//  kind.rs — アセット形式の種類と、形式ごとの「現行版・版の欄・対象ファイル・書き手」の表
//
//  【役割】
//  マイグレーション機構で唯一の「表」。形式を 1 つ足す／版を 1 つ上げるときに
//  書き換えるのはこのファイルの表と registry.rs の登録行だけになるようにしてある
//  （docs/asset_migration.md 4 章のチェックリスト）。
//
//  【表に入れるもの】
//  - 現行版（このエンジンが書き出す版）
//  - 版を格納する JSON の欄名（形式ごとに異なる。`VersionKey` を参照）
//  - その形式に属するファイル拡張子（一括アップグレードの列挙に使う）
//  - 拡張子で区別できない形式のための「アセットルート相対パス」
//    （`layers.json` などは拡張子が `.json` で共有されるため、置き場所で見分ける）
//  - 版を刻む書き手（ランタイム＝Rust / エディタ＝C#）。C# が書く形式は
//    ランタイムからは**読むだけ**で、刻印はエディタ側の責務になる
//
//  版の欄が無いファイルは 1 版とみなす（docs/asset_migration.md 1 章）。
// ============================================================

use std::fmt;
use std::path::Path;

// ── 定数 ─────────────────────────────────────────────────────────

/// 版の欄が無いファイルを何版とみなすか。
pub const IMPLICIT_FIRST_VERSION: u32 = 1;

/// 版の欄名（`format_version`）。新しい形式はこちらを使う。
///
/// 外部（`upgrade` など）が「版の欄名」を必要とする場合は、必ず
/// `FormatKind::version_key()` を経由すること（形式ごとに欄名が違う）。
pub const JSON_VERSION_KEY: &str = "format_version";

/// 版の欄名（`version`）。版の仕組みを導入する前から独自に版を持っていた形式が使う。
pub const LEGACY_VERSION_KEY: &str = "version";

// ── VersionKey ───────────────────────────────────────────────────

/// 版を格納する JSON のトップレベル欄名。
///
/// 【なぜ文字列ではなく列挙なのか】
/// 版の**読み取り**（先読み用の `#[derive(Deserialize)]`）と**刻印**
/// （`#[serde(flatten)]` のラッパー）は、serde の derive の制約で欄名に
/// リテラルしか書けない。列挙にしておけば「表に書いた欄名」と
/// 「コード上の綴り」の対応が網羅 match で保証され、片方だけ増やす事故が起きない。
#[derive(Clone, Copy, PartialEq, Eq, Debug, Hash)]
pub enum VersionKey {
    /// `"format_version"`。この仕組みで新しく版を持たせる形式。
    FormatVersion,
    /// `"version"`。`.inputmap` / `.sprite_mesh` が以前から使っている欄名。
    /// 既存ファイルを 1 バイトも書き換えずに仕組みへ載せるため、綴りを尊重する。
    Version,
}

impl VersionKey {
    /// JSON 上の欄名。
    pub fn as_str(self) -> &'static str {
        match self {
            VersionKey::FormatVersion => JSON_VERSION_KEY,
            VersionKey::Version => LEGACY_VERSION_KEY,
        }
    }
}

// ── FormatWriter ─────────────────────────────────────────────────

/// そのアセットを「保存するとき版を刻む」のは誰か。
///
/// 変換（読み込み時の持ち上げ）は形式によらずランタイムに一本化されているが、
/// **保存**はランタイムが行う形式とエディタ（C#）が行う形式がある。
/// C# が書く形式は、エディタ側が保存時に現行版を刻むまで「欄なし＝v1」のまま
/// 出回る。ランタイムは欄なしを 1 版として読むので互換は保たれる。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum FormatWriter {
    /// ランタイム（Rust）が保存する。刻印もランタイムが行う。
    Runtime,
    /// エディタ（C#）が保存する。刻印はエディタ側の実装が必要
    /// （docs/asset_migration.md「エディタ側の実装メモ」）。
    Editor,
}

impl FormatWriter {
    /// レポート・ドキュメント用の表記。
    pub fn as_str(self) -> &'static str {
        match self {
            FormatWriter::Runtime => "runtime",
            FormatWriter::Editor => "editor",
        }
    }
}

// ── FormatSpec ───────────────────────────────────────────────────

/// 1 つのアセット形式の仕様（表の 1 行）。
pub struct FormatSpec {
    /// レポート・エラーメッセージ・`--migrate-json` の引数に使う形式名
    /// （小文字・英数字とアンダースコアのみ）。
    pub label: &'static str,
    /// 版を格納する JSON の欄名。
    pub version_key: VersionKey,
    /// このエンジンが読み書きできる最新の版。保存時はこの版を刻む。
    pub current_version: u32,
    /// この形式に属するファイル拡張子（ドット無し・小文字）。
    ///
    /// 拡張子が `.json` で他形式と衝突する形式はここを空にし、
    /// `asset_relative_paths` で見分ける。
    pub extensions: &'static [&'static str],
    /// 拡張子で区別できない形式の、**アセットルート相対パス**（スラッシュ区切り・小文字）。
    ///
    /// 例: `terrain/layers.json`。ファイル名だけ（`layers.json`）で照合すると
    /// 無関係な同名ファイルを巻き込むため、置き場所まで含めて一致させる。
    pub asset_relative_paths: &'static [&'static str],
    /// 版を刻む書き手。
    pub writer: FormatWriter,
}

/// `.scene`（シーン）の仕様。
///
/// - v1: 欄なしの従来形式
/// - v2: 旧 enum 表記（`blend: "alpha"/"additive"`・`shape: "point"`・
///       `gravity_mode: "screen_down"`）を現行表記へ正規化
const SCENE_SPEC: FormatSpec = FormatSpec {
    label: "scene",
    version_key: VersionKey::FormatVersion,
    current_version: 2,
    extensions: &["scene"],
    asset_relative_paths: &[],
    writer: FormatWriter::Runtime,
};

/// `.actor` / `.actor2d`（アクタ＝プレハブ）の仕様。版の内容は `.scene` と同じ。
///
/// `.actor2d` は 2D アクタのプレハブで、中身は `.actor` と同じ `ActorData` JSON。
const ACTOR_SPEC: FormatSpec = FormatSpec {
    label: "actor",
    version_key: VersionKey::FormatVersion,
    current_version: 2,
    extensions: &["actor", "actor2d"],
    asset_relative_paths: &[],
    writer: FormatWriter::Runtime,
};

/// `.anim`（アニメーションクリップ）の仕様。
///
/// - v1: 欄なしの従来形式（実変換はまだ無い）
///
/// 書き手はエディタ（C#）。ランタイムは読むだけなので、刻印はエディタ側で行う。
const ANIM_SPEC: FormatSpec = FormatSpec {
    label: "anim",
    version_key: VersionKey::FormatVersion,
    current_version: 1,
    extensions: &["anim"],
    asset_relative_paths: &[],
    writer: FormatWriter::Editor,
};

/// `.mat`（マテリアルアセット）の仕様。
///
/// - v1: 欄なしの従来形式（実変換はまだ無い）
const MATERIAL_SPEC: FormatSpec = FormatSpec {
    label: "material",
    version_key: VersionKey::FormatVersion,
    current_version: 1,
    extensions: &["mat"],
    asset_relative_paths: &[],
    writer: FormatWriter::Editor,
};

/// `.postfx`（ポストエフェクトチェーン）の仕様。
///
/// - v1: 欄なしの従来形式（実変換はまだ無い）
const POSTFX_SPEC: FormatSpec = FormatSpec {
    label: "postfx",
    version_key: VersionKey::FormatVersion,
    current_version: 1,
    extensions: &["postfx"],
    asset_relative_paths: &[],
    writer: FormatWriter::Editor,
};

/// `.inputmap`（入力アクションマップ）の仕様。
///
/// この形式は仕組みの導入前から `version` 欄で版を持っていた。
/// **既存の版番号をそのまま尊重する**（欄名も `version` のまま）。
///
/// - v1: `bindings` にすべてのバインドが入る（`WASD` 合成軸を含む）
/// - v2: Axis1D は `positive` / `negative`、Axis2D は `x` / `y` の正負グループへ分離
const INPUTMAP_SPEC: FormatSpec = FormatSpec {
    label: "inputmap",
    version_key: VersionKey::Version,
    current_version: 2,
    extensions: &["inputmap"],
    asset_relative_paths: &[],
    writer: FormatWriter::Editor,
};

/// `.sprite_mesh`（2D スプライトメッシュ）の仕様。
///
/// この形式も導入前から `version` 欄（= 1）を持っている。実変換はまだ無い。
const SPRITE_MESH_SPEC: FormatSpec = FormatSpec {
    label: "sprite_mesh",
    version_key: VersionKey::Version,
    current_version: 1,
    extensions: &["sprite_mesh"],
    asset_relative_paths: &[],
    writer: FormatWriter::Editor,
};

/// 地形レイヤ定義（`assets/terrain/layers.json`）の仕様。実変換はまだ無い。
const TERRAIN_LAYERS_SPEC: FormatSpec = FormatSpec {
    label: "terrain_layers",
    version_key: VersionKey::FormatVersion,
    current_version: 1,
    extensions: &[],
    asset_relative_paths: &["terrain/layers.json"],
    writer: FormatWriter::Editor,
};

/// 地形散布プロップ定義（`assets/terrain/props.json`）の仕様。実変換はまだ無い。
const TERRAIN_PROPS_SPEC: FormatSpec = FormatSpec {
    label: "terrain_props",
    version_key: VersionKey::FormatVersion,
    current_version: 1,
    extensions: &[],
    asset_relative_paths: &["terrain/props.json"],
    writer: FormatWriter::Editor,
};

/// 地形カバー材質定義（`assets/terrain/cover_materials.json`）の仕様。実変換はまだ無い。
const TERRAIN_COVER_MATERIALS_SPEC: FormatSpec = FormatSpec {
    label: "terrain_cover_materials",
    version_key: VersionKey::FormatVersion,
    current_version: 1,
    extensions: &[],
    asset_relative_paths: &["terrain/cover_materials.json"],
    writer: FormatWriter::Editor,
};

/// プロジェクト設定（`assets/project_settings.json`）の仕様。実変換はまだ無い。
///
/// ランタイムからは `core::app_base::project_settings` の共通ローダ 1 本だけが読む。
const PROJECT_SETTINGS_SPEC: FormatSpec = FormatSpec {
    label: "project_settings",
    version_key: VersionKey::FormatVersion,
    current_version: 1,
    extensions: &[],
    asset_relative_paths: &["project_settings.json"],
    writer: FormatWriter::Editor,
};

// ── FormatKind ───────────────────────────────────────────────────

/// マイグレーションの対象となるアセット形式。
///
/// 形式を足すときは variant → `spec()` の腕 → `ALL` の 3 か所を更新する
/// （`spec()` は網羅 match なので腕の書き忘れはビルドが落として教えてくれる）。
#[derive(Clone, Copy, PartialEq, Eq, Debug, Hash)]
pub enum FormatKind {
    /// シーン（`.scene`）
    Scene,
    /// アクタ＝プレハブ（`.actor` / `.actor2d`）
    Actor,
    /// アニメーションクリップ（`.anim`）
    Anim,
    /// マテリアルアセット（`.mat`）
    Material,
    /// ポストエフェクトチェーン（`.postfx`）
    Postfx,
    /// 入力アクションマップ（`.inputmap`）
    InputMap,
    /// 2D スプライトメッシュ（`.sprite_mesh`）
    SpriteMesh,
    /// 地形レイヤ定義（`terrain/layers.json`）
    TerrainLayers,
    /// 地形散布プロップ定義（`terrain/props.json`）
    TerrainProps,
    /// 地形カバー材質定義（`terrain/cover_materials.json`）
    TerrainCoverMaterials,
    /// プロジェクト設定（`project_settings.json`）
    ProjectSettings,
}

impl FormatKind {
    /// 全形式の一覧（網羅テスト・一括アップグレードの列挙・CLI のヘルプが使う）。
    pub const ALL: &'static [FormatKind] = &[
        FormatKind::Scene,
        FormatKind::Actor,
        FormatKind::Anim,
        FormatKind::Material,
        FormatKind::Postfx,
        FormatKind::InputMap,
        FormatKind::SpriteMesh,
        FormatKind::TerrainLayers,
        FormatKind::TerrainProps,
        FormatKind::TerrainCoverMaterials,
        FormatKind::ProjectSettings,
    ];

    /// この形式の仕様（表の 1 行）を返す。
    pub fn spec(self) -> &'static FormatSpec {
        match self {
            FormatKind::Scene => &SCENE_SPEC,
            FormatKind::Actor => &ACTOR_SPEC,
            FormatKind::Anim => &ANIM_SPEC,
            FormatKind::Material => &MATERIAL_SPEC,
            FormatKind::Postfx => &POSTFX_SPEC,
            FormatKind::InputMap => &INPUTMAP_SPEC,
            FormatKind::SpriteMesh => &SPRITE_MESH_SPEC,
            FormatKind::TerrainLayers => &TERRAIN_LAYERS_SPEC,
            FormatKind::TerrainProps => &TERRAIN_PROPS_SPEC,
            FormatKind::TerrainCoverMaterials => &TERRAIN_COVER_MATERIALS_SPEC,
            FormatKind::ProjectSettings => &PROJECT_SETTINGS_SPEC,
        }
    }

    /// レポート・エラーメッセージ用の形式名。
    pub fn label(self) -> &'static str {
        self.spec().label
    }

    /// 版を格納する JSON の欄名（文字列）。
    pub fn version_key(self) -> &'static str {
        self.spec().version_key.as_str()
    }

    /// 版を格納する JSON の欄名（列挙。先読み・刻印の分岐に使う）。
    pub fn version_key_kind(self) -> VersionKey {
        self.spec().version_key
    }

    /// このエンジンが書き出す版（＝連鎖の終着点）。
    pub fn current_version(self) -> u32 {
        self.spec().current_version
    }

    /// この形式に属するファイル拡張子（ドット無し・小文字）。
    pub fn extensions(self) -> &'static [&'static str] {
        self.spec().extensions
    }

    /// この形式を置き場所で見分けるためのアセットルート相対パス。
    pub fn asset_relative_paths(self) -> &'static [&'static str] {
        self.spec().asset_relative_paths
    }

    /// 版を刻む書き手。
    pub fn writer(self) -> FormatWriter {
        self.spec().writer
    }

    /// ファイルパスの拡張子から形式を判定する。対象外なら `None`。
    ///
    /// 拡張子の大小は無視する（Windows のファイルシステムは大小を区別しないため、
    /// `Foo.Scene` のような綴りが混ざりうる）。
    ///
    /// **拡張子が `.json` の形式（地形定義・プロジェクト設定）はここでは判定できない**。
    /// それらは `from_asset_relative_path` を使うこと。
    pub fn from_path(path: &Path) -> Option<FormatKind> {
        let ext = path.extension()?.to_string_lossy().to_ascii_lowercase();
        FormatKind::ALL
            .iter()
            .copied()
            .find(|k| k.extensions().contains(&ext.as_str()))
    }

    /// アセットルート相対パス（スラッシュ区切り）から形式を判定する。対象外なら `None`。
    ///
    /// 拡張子で見分けられる形式もここで拾えるよう、相対パス表 →拡張子 の順に見る。
    pub fn from_asset_relative_path(relative: &str) -> Option<FormatKind> {
        let normalized = relative.replace('\\', "/").to_ascii_lowercase();
        if let Some(kind) = FormatKind::ALL
            .iter()
            .copied()
            .find(|k| k.asset_relative_paths().contains(&normalized.as_str()))
        {
            return Some(kind);
        }
        FormatKind::from_path(Path::new(&normalized))
    }

    /// `--migrate-json <kind>` の引数（＝ `label`）から形式を引く。
    pub fn from_label(label: &str) -> Option<FormatKind> {
        FormatKind::ALL
            .iter()
            .copied()
            .find(|k| k.label() == label)
    }

    /// 対応する形式名をカンマ区切りで並べる（CLI の使い方メッセージ用）。
    pub fn all_labels() -> String {
        FormatKind::ALL
            .iter()
            .map(|k| k.label())
            .collect::<Vec<_>>()
            .join(", ")
    }
}

impl fmt::Display for FormatKind {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.label())
    }
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 表の不変条件: 現行版は必ず「欄なし＝1 版」以上であること。
    /// （現行版 0 や負の版は連鎖の前提を壊す）
    #[test]
    fn current_versions_are_at_least_the_implicit_first_version() {
        for kind in FormatKind::ALL {
            assert!(
                kind.current_version() >= IMPLICIT_FIRST_VERSION,
                "{kind} の現行版が {} で、欄なしの既定 {IMPLICIT_FIRST_VERSION} を下回っている",
                kind.current_version()
            );
        }
    }

    /// 表の不変条件: 拡張子は形式間で重複しないこと（`from_path` の判定が一意になる）。
    #[test]
    fn extensions_are_unique_across_formats() {
        let mut seen: Vec<&str> = Vec::new();
        for kind in FormatKind::ALL {
            for ext in kind.extensions() {
                assert!(
                    !seen.contains(ext),
                    "拡張子 {ext} が複数の形式に登録されている"
                );
                assert_eq!(
                    *ext,
                    ext.to_ascii_lowercase(),
                    "拡張子 {ext} は小文字で登録すること（from_path が小文字で比較する）"
                );
                seen.push(ext);
            }
        }
    }

    /// 表の不変条件: 相対パスも形式間で重複しないこと。
    ///
    /// あわせて「拡張子 `.json` の形式は拡張子表を持たない」ことを固定する。
    /// `.json` を拡張子表に入れると、地形定義とプロジェクト設定が区別できなくなる。
    #[test]
    fn asset_relative_paths_are_unique_and_json_formats_have_no_extension() {
        let mut seen: Vec<&str> = Vec::new();
        for kind in FormatKind::ALL {
            for path in kind.asset_relative_paths() {
                assert!(
                    !seen.contains(path),
                    "相対パス {path} が複数の形式に登録されている"
                );
                assert_eq!(
                    *path,
                    path.to_ascii_lowercase(),
                    "相対パス {path} は小文字で登録すること"
                );
                assert!(
                    !path.starts_with('/') && !path.contains('\\'),
                    "相対パス {path} はスラッシュ区切りの相対表記で書くこと"
                );
                seen.push(path);
                // 相対パスで見分ける形式は拡張子表を持たない（両方あると判定が二重になる）。
                assert!(
                    kind.extensions().is_empty(),
                    "{kind} は相対パスと拡張子の両方で登録されている"
                );
            }
        }
        // `.json` が拡張子として登録されていないこと
        for kind in FormatKind::ALL {
            assert!(
                !kind.extensions().contains(&"json"),
                "{kind} が拡張子 json を持っている（地形定義と衝突する）"
            );
        }
    }

    /// 表の不変条件: 形式名は重複せず、CLI の引数として扱える綴りであること。
    #[test]
    fn labels_are_unique_and_cli_safe() {
        let mut seen: Vec<&str> = Vec::new();
        for kind in FormatKind::ALL {
            let label = kind.label();
            assert!(!seen.contains(&label), "形式名 {label} が重複している");
            assert!(
                label
                    .chars()
                    .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '_'),
                "形式名 {label} に CLI で扱いにくい文字が含まれている"
            );
            seen.push(label);
        }
    }

    /// 拡張子からの判定（大小無視・対象外は None）。
    #[test]
    fn from_path_matches_extension_ignoring_case() {
        assert_eq!(
            FormatKind::from_path(Path::new("a/b/Main.scene")),
            Some(FormatKind::Scene)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("a/b/Main.SCENE")),
            Some(FormatKind::Scene)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("Fish.actor")),
            Some(FormatKind::Actor)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("Hud.actor2d")),
            Some(FormatKind::Actor)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("Swim.anim")),
            Some(FormatKind::Anim)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("Water.mat")),
            Some(FormatKind::Material)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("Blur.postfx")),
            Some(FormatKind::Postfx)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("Game.inputmap")),
            Some(FormatKind::InputMap)
        );
        assert_eq!(
            FormatKind::from_path(Path::new("Body.sprite_mesh")),
            Some(FormatKind::SpriteMesh)
        );
        assert_eq!(FormatKind::from_path(Path::new("note.txt")), None);
        assert_eq!(FormatKind::from_path(Path::new("noext")), None);
        // `.json` は拡張子だけでは判定しない（置き場所で見分ける形式のため）
        assert_eq!(FormatKind::from_path(Path::new("terrain/layers.json")), None);
    }

    /// 相対パスからの判定（置き場所で見分ける形式＋拡張子で見分ける形式の両方）。
    #[test]
    fn from_asset_relative_path_matches_location_then_extension() {
        assert_eq!(
            FormatKind::from_asset_relative_path("terrain/layers.json"),
            Some(FormatKind::TerrainLayers)
        );
        assert_eq!(
            FormatKind::from_asset_relative_path("terrain\\props.json"),
            Some(FormatKind::TerrainProps),
            "Windows 区切りでも判定できること"
        );
        assert_eq!(
            FormatKind::from_asset_relative_path("terrain/cover_materials.json"),
            Some(FormatKind::TerrainCoverMaterials)
        );
        assert_eq!(
            FormatKind::from_asset_relative_path("project_settings.json"),
            Some(FormatKind::ProjectSettings)
        );
        // 置き場所が違う同名ファイルは対象外（無関係な JSON を巻き込まない）
        assert_eq!(FormatKind::from_asset_relative_path("ui/layers.json"), None);
        // 拡張子で見分ける形式もここで拾える
        assert_eq!(
            FormatKind::from_asset_relative_path("scenes/Main.scene"),
            Some(FormatKind::Scene)
        );
    }

    /// 版の欄名は形式ごとに表のとおりであること（既存の版番号を尊重する形式の固定）。
    #[test]
    fn version_keys_follow_the_table() {
        assert_eq!(FormatKind::Scene.version_key(), JSON_VERSION_KEY);
        assert_eq!(FormatKind::Anim.version_key(), JSON_VERSION_KEY);
        assert_eq!(FormatKind::InputMap.version_key(), LEGACY_VERSION_KEY);
        assert_eq!(FormatKind::SpriteMesh.version_key(), LEGACY_VERSION_KEY);
        // 既存の版番号をそのまま引き継いでいること
        assert_eq!(FormatKind::InputMap.current_version(), 2);
        assert_eq!(FormatKind::SpriteMesh.current_version(), 1);
    }

    /// 形式名からの逆引き（`--migrate-json <kind>` の引数解釈）。
    #[test]
    fn from_label_round_trips_every_kind() {
        for kind in FormatKind::ALL.iter().copied() {
            assert_eq!(FormatKind::from_label(kind.label()), Some(kind));
        }
        assert_eq!(FormatKind::from_label("unknown_format"), None);
        assert_eq!(FormatKind::from_label(""), None);
        // ヘルプ用の一覧に全形式が載ること
        let labels = FormatKind::all_labels();
        for kind in FormatKind::ALL {
            assert!(labels.contains(kind.label()), "{kind} が一覧に無い");
        }
    }
}
