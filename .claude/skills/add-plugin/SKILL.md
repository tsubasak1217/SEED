---
name: add-plugin
description: 新しいプラグインDLLの作成、プラグインへの機能追加・フィールド拡張を求められたときに使用。plugin_api クレートを使い、sample_plugin をコピー元に差分作成する手順。
---

# プラグインDLLの新規作成（add-plugin）

SEED のプラグインは `plugin_api`（crate: `seed-plugin-api`）の `Plugin` trait を実装した
Rust cdylib。`plugins/sample_plugin/` を丸ごとコピーして名前を書き換えるのが最速。

## 全体像

- `plugin_api/` — trait 定義クレート（プラグイン作者はここにのみ依存）
- `plugins/<crate名>/` — 各プラグインのソース（Cargo crate）
- ビルド後、`build.rs` が DLL と `plugin.json` を **`runtime/plugins/<Name>/`** に自動コピー
  （このリポジトリ自身が1つの SEED プロジェクトであり、`runtime/assets` が assets_root、
  その隣の `runtime/plugins/` をランタイムが走査するため）
- `editor/plugins/<Name>/` は**エディタに同梱される別プロジェクト用のプラグインライブラリ**。
  プロジェクト設定ウィンドウの「プラグイン管理」→ライブラリ一覧はここを読み、
  他プロジェクトへの「インポート」時に `assets/../plugins/` へコピーされる。
  このリポジトリの開発用途では通常不要（runtime/plugins に直接デプロイされるため）だが、
  他プロジェクトでも配布したい場合は plugin.json と DLL をここにも配置する。

## 手順

### 1. ディレクトリをコピーして書き換える

```
plugins/sample_plugin/  →  plugins/<new_crate_name>/
  build.rs      … dll_name と "SamplePlugin"（デプロイ先フォルダ名）を書き換え
  Cargo.toml    … [package] name, [lib] name を書き換え
  plugin.json   … name/description/entry_dll を書き換え
  src/lib.rs    … struct名・name()・field_defs() を書き換え
```

`build.rs` の書き換え箇所（DLL名とデプロイ先フォルダ名のみ）:

```rust
let dll_name = if cfg!(target_os = "windows") {
    "new_plugin.dll"          // ← crate名に合わせる
} ...
let plugin_dir = manifest_dir.ancestors().nth(2)
    .map(|p| p.join("runtime").join("plugins").join("NewPlugin")) // ← plugin.json の name と一致させる
```

`Cargo.toml`:

```toml
[package]
name    = "new_plugin"
version = "0.1.0"
edition = "2024"

[lib]
name       = "new_plugin"
crate-type = ["cdylib"]

[dependencies]
seed-plugin-api = { path = "../../plugin_api" }
```

`plugin.json`（実スキーマ = `PluginManifest` in `runtime/src/engine/plugin/manifest.rs`）:

```json
{
  "name": "NewPlugin",
  "version": "0.1.0",
  "description": "説明文",
  "author": "作者名",
  "entry_dll": "new_plugin.dll"
}
```
`entry_dll` を省略すると `"{name}.dll"` が使われる（`PluginManifest::dll_path`）。
`name` は `Plugin::name()` の返り値、および `plugin.json` のファイル配置フォルダ名と一致させること
（ロード時に不一致なら警告ログのみで動作は継続するが、必ず揃える）。

### 2. ワークスペースへ登録する（重要・忘れやすい）

ルート `Cargo.toml` の `[workspace].members` は**明示列挙**であり glob ではない。
新規クレートを追加し忘れるとビルド対象に入らない。

```toml
[workspace]
members  = [
    "runtime",
    "plugin_api",
    "plugins/sample_plugin",
    "plugins/new_plugin",   # ← 追記必須
]
```

### 3. Plugin trait を実装する（src/lib.rs）

`seed_plugin_api::{Plugin, PluginFieldDef, PluginFieldKind}` を使う。
`PluginFieldKind` の実バリアント（`plugin_api/src/lib.rs`）:

| バリアント | パラメータ | 用途 |
|---|---|---|
| `Float { min, max, step }` | f32 | スライダー |
| `Int { min, max }` | i32 | 整数スライダー |
| `String { max_len }` | usize | テキスト入力 |
| `Bool` | なし | チェックボックス |
| `Color` | なし | RGBAピッカー（値は `"r,g,b,a"` 0.0〜1.0 文字列） |
| `FilePath { filter }` | String（例 `".png;.jpg"`） | ファイル選択ダイアログ |
| `Enum { options }` | `Vec<String>` | ドロップダウン（値は選択肢インデックスの文字列） |

`field_defs()` で各フィールドを `PluginFieldDef { key, label, kind, default_value, tooltip }` として列挙する。
`default_value` は必ず `kind` に対応する文字列表現にする（Enum ならインデックス、Colorなら `"1.0,1.0,1.0,1.0"`）。

```rust
impl Plugin for NewPlugin {
    fn name(&self) -> &str { "NewPlugin" }
    fn version(&self) -> &str { "0.1.0" }
    fn description(&self) -> &str { "説明" }
    fn field_defs(&self) -> Vec<PluginFieldDef> {
        vec![
            PluginFieldDef {
                key: "power".to_string(),
                label: "威力".to_string(),
                kind: PluginFieldKind::Float { min: 0.0, max: 100.0, step: 1.0 },
                default_value: "10.0".to_string(),
                tooltip: "".to_string(),
            },
        ]
    }
    fn on_field_changed(&self, key: &str, old_value: &str, new_value: &str) {
        // 値変更時のバリデーション・副作用処理（任意）
    }
}
```

DLL エントリポイントも `SamplePlugin` → 自作 struct 名に差し替えてコピーする
（`seed_create_plugin()` 関数名・シグネチャ自体は変更しない。ABI互換のため
 runtime と同一 Rust ツールチェーンでビルドすること）。

### 4. ビルドしてデプロイを確認する

```
cargo build -p new_plugin
```

成功すると `build.rs` が自動的に以下へコピーする:
- `runtime/plugins/NewPlugin/new_plugin.dll`
- `runtime/plugins/NewPlugin/plugin.json`

手動配置は不要。存在確認のみ行う。

### 5. project_settings.json に有効化エントリを追加する

`runtime/assets/project_settings.json` の `"plugins"` 配列（`PluginEntry`）に追記
（未記載でもデフォルトで有効扱いだが、明示登録を推奨）:

```json
{ "name": "NewPlugin", "enabled": true }
```

### 6. エディタで確認する

1. エディタ起動 → シーンを開く → アクターを選択
2. コンポーネント追加メニュー（`ComponentSelectorWindow`、「プラグイン」カテゴリ）に
   `NewPlugin` が一覧表示されることを確認
   （内部的には runtime が `GET_PLUGIN_LIST` IPC 応答で `PLUGIN_LIST:[...]` を返し、
   `ComponentSelectorWindow.BuildPluginEntries()` が `Plugin:{name}` 形式のエントリとして描画）
3. 追加すると `PluginComponent` として登録され、インスペクタに `field_defs()` の内容が
   `InspectorPanel.BuildPluginSlotContent` によりフィールド種別ごとのUIとして自動生成される
4. 値を変更すると `SET_PLUGIN_FIELD:{actor},{slot},{key},{value}` が送られ、
   `on_field_changed` が呼ばれることをログ（`eprintln!`）で確認する

## よくある失敗

- **workspace members への追記漏れ**: `cargo build -p new_plugin` が
  「package not found」で失敗する。手順2を確認。
- **DLL名の不一致**: `build.rs` の `dll_name` / `Cargo.toml` の `[lib].name` /
  `plugin.json` の `entry_dll` の3箇所は同じ文字列（拡張子の有無以外）で揃える必要がある。
  ずれると `PluginManifest::dll_path()` が存在しないパスを指し、ロード時に
  「ロード失敗」ログが出る。
- **plugin.json の name とフォルダ名の不一致**: `build.rs` のデプロイ先フォルダ名
  （`runtime/plugins/<フォルダ名>/`）と `plugin.json` の `name` は一致させる。
  ずれても走査自体は失敗しないが、混乱の元なので統一する。
- **`Plugin::name()` と `plugin.json` の `name` の不一致**: ロードはされるが
  `[PluginRegistry] 警告` がログに出る。実害は薄いが直しておく。
- **フィールド型と default_value のミスマッチ**: 例えば `Enum` で `default_value` に
  ラベル文字列を入れてしまう（正しくはインデックスの文字列 `"0"`）。
  エディタ側は素通しでテキスト保存するため、パース側（自作プラグインのロジック）で
  ズレに気づきにくい。型ごとの文字列表現を必ず守る。
- **project_settings.json に `"enabled": false` で登録したまま忘れる**:
  この場合ロード自体がスキップされ、コンポーネント一覧に出てこない。
