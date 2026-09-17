---
paths:
  - "runtime/src/engine/components/**"
  - "runtime/src/engine/structs/objects/actor/**"
  - "runtime/src/engine/core/app_base/scene.rs"
  - "runtime/src/engine/core/app_base/actor_file.rs"
  - "runtime/src/engine/core/app_base/scene_settings.rs"
  - "runtime/src/engine/core/migration/**"
  - "runtime/src/engine/animation/**"
  - "runtime/src/engine/terrain/**"
---

# アセットのシリアライズ形式を触るときのルール

保存されたアセット（`.scene` / `.actor` / `.anim` / `.mat` / 地形 JSON …）は**ユーザーの作ったデータ**であり、
壊すと復元できない。形式に手を入れるときは以下を厳守する。正典は **docs/asset_migration.md**。

## 1. 変更の種類で対応が決まる

| 変更 | 版を上げるか | やること |
|---|---|---|
| **項目の追加** | 上げない | 追加フィールドに必ず `#[serde(default)]`（非ゼロ既定値は `#[serde(default = "fn")]`）。これだけで旧ファイルが読める |
| **キー名の変更** | **上げる** | 変換段を書く。`#[serde(rename)]` / `alias` だけで済ませない |
| **構造の組み替え** | **上げる** | 変換段を書く（入れ子の付け替え・配列化など） |
| **意味の変更**（単位・座標系・既定値の意味） | **上げる** | 変換段を書く。値の読み替えは変換でしか表現できない |
| **enum の値の綴り変更** | **上げる** | 変換段で正規化する。`alias` は移行期間の受け皿として残す |

`#[serde(default)]` の付け忘れは、そのフィールドを持たない**旧ファイルの読み込みが丸ごと失敗する**。
新しいフィールドを足したら必ず確認すること。

## 2. 版を上げるときの手順（3 ファイルで収まる）

1. `runtime/src/engine/core/migration/kind.rs` の該当形式の `current_version` を +1。
2. `runtime/src/engine/core/migration/steps/<形式>/vN_to_vM.rs` を新規作成し、
   `registry.rs` の `STEPS` に 1 行足す。
   変換段は **`&mut serde_json::Value -> Result<(), String>` の純関数**で、ファイル・時刻・乱数に触らない。
   **冪等**であること。版の欄には触らない（進めるのは `runner` の責務）。
   アクタ木の走査は `json_walk` のヘルパを使う（`.scene` と `.actor` で共用）。
3. `runtime/tests/fixtures/migration/<形式>/` に変換前後の見本を置き、
   `migration/golden.rs` の `CASES` と `runtime/tests/fixtures/README.md` の対応表に 1 行ずつ足す。

`registry.rs` の網羅性テストが「1 から現行版まで抜けなく並んでいること」を固定しているので、
現行版だけ上げて段を忘れると `cargo test` が落ちる。

## 3. 絶対に守ること

- **未来の版は拒否する**。現行版より新しいファイルは読み込まない（`MigrationError::FutureVersion`）。
  前進のみ・1 段ずつ、という前提を守るため。黙って読めるところだけ読む、は禁止。
- **読み込み時はメモリ上でだけ変換する**。開いただけでファイルを書き換えない
  （全員の作業コピーが勝手に変わって VCS で衝突する）。書き換えてよいのは
  「普通に保存したとき」と「一括アップグレード（`SEED.exe --upgrade-project`）」だけ。
- **変換は Rust に一本化する**。C# 側で同じ変換を書かない（`.inputmap` の二重実装が既に負債になっている）。
- **読み込みの入口を増やさない**。`.scene` は `Scene::from_json`、`.actor` は
  `core::app_base::actor_file` が唯一の経路。新しい経路を作るとマイグレーションが素通りする。
- **プレハブのハッシュは生テキストから取る**（`prefab_ops::prefab_content_hash`）。
  「生テキストでハッシュ → その後に変換」の順序を崩さない。

## 4. C# が直接読んでいるキーに触るときは同じコミットで直す

Rust の変換を通らない読み手が居る。対象は **docs/asset_migration.md 2 章**の一覧が正典
（`SceneSettingsData.cs` が `.scene` の `settings` / `shading_asset`、
`FishCatalogGenerator.cs` が `.actor` のコンポーネント構造、`AssetCollector.cs` が参照の正規表現走査）。
これらが見ているキー名・構造を変えるなら、同じコミットで C# 側も直す。

## 5. 版を持たない形式を触るとき

`.anim` / `.mat` / `.postfx` / `layers.json` / `props.json` などはまだ版の欄を持たない（M2 で追加予定）。
それらのキー名・構造を変えるなら、**先に版の仕組みへ載せてから**変えること。
`kind.rs` に形式を足し、読み込みの入口に `migration::load_json` を通し、保存で
`migration::to_stamped_pretty_json` を使う。
