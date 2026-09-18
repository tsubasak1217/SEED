# テストフィクスチャ

ここに置くファイルは **テスト専用のコピー（スナップショット）** である。

## なぜコピーを置くのか

プロジェクトの `assets/`（例: `<project>/assets/`）はユーザーがエディタ上でファイルを自由に移動・改名・削除できる
**作業領域** である。テストがそこを `include_str!` していると、ユーザーがアセットを
1 つ動かしただけでクレートがコンパイル不能になる（実際に発生した）。
そのため「出荷サンプルが壊れていないこと」を確かめるテストは、プロジェクトの `assets/` を
直接読まず、このディレクトリのコピーを読む。

## 担保が変わる点（重要）

コピーである以上、これらのテストが担保するのは
**「スナップショット時点の出荷サンプルが壊れていないこと」** であって、
「今この瞬間のプロジェクト `assets/` の中身が壊れていないこと」ではない。
アセット側の正本を編集しても、コピーを更新しない限りテストは古い内容を見続ける。

**正本を更新したら、このディレクトリのコピーも同じ内容へ差し替えること（バイト一致が前提）。**

## 対応表（コピー元＝正本）

| フィクスチャ | 正本 | 使用テスト |
| --- | --- | --- |
| `shaders/magma.wgsl` | `templates/shaders/magma.wgsl` | `renderer::water::shading_asset` |
| `shaders/poison.wgsl` | `templates/shaders/poison.wgsl` | 同上 |
| `shaders/pop_ocean.wgsl` | `<project>/assets/mainGame/shaders/pop_ocean.wgsl` | 同上 |
| `terrain/cover_materials.json` | `templates/terrain/cover_materials.json` | `terrain::cover::tests_cover` |
| `terrain/props.json` | `templates/terrain/props.json` | `app::terrain_scatter_ops` |
| `terrain/layers.json` | `templates/terrain/layers.json` | 同上 |
| `migration/scene/v1.scene` | 正本なし（手で作った変換前の見本） | `core::migration::golden` |
| `migration/scene/v2.scene` | 同上（`v1.scene` を変換した期待結果） | 同上 |
| `migration/actor/v1.actor` | 正本なし（手で作った変換前の見本） | 同上 |
| `migration/actor/v2.actor` | 同上（`v1.actor` を変換した期待結果） | 同上 |
| `migration/inputmap/v1.inputmap` | 正本なし（手で作った変換前の見本） | 同上 |
| `migration/inputmap/v2.inputmap` | 同上（`v1.inputmap` を変換した期待結果） | 同上 |

`*.sprite_mesh` はアセット由来ではなくテストのために手で作った入力なので、正本は無い。

## `migration/` の見本について

アセット形式のマイグレーション（docs/asset_migration.md）のゴールデンテスト用。
**`vN` が変換前、`vM` が期待結果**で、対になっている。プロジェクトのアセットのコピーではなく、
変換したい旧表記を最小限に詰めた手作りの入力である（実データに旧表記が無くてもテストが効くように）。

照合は `serde_json::Value` としての一致（JSON の**意味**）で行い、整形や欄の並びは見ない。
版を上げたときは、**期待結果の見本を新しい版へ差し替える**こと
（差し替え忘れは `golden::fixtures_declare_the_expected_versions` が検出する）。

### 見本が要るのは「実変換がある形式」だけ

`.anim` / `.mat` / `.postfx` / 地形 JSON / `project_settings.json` / `.sprite_mesh` は
現行版 1 で変換段を持たない（版の欄を読む・未来版を拒否する・保存で刻む、だけ）。
変換前後の見本は無くてよく、代わりに
`golden::version_only_formats_migrate_without_any_step` が
「段が 1 つも無くても版が刻まれること」を全形式について確かめている。
それらに最初の変換段を足すときに、この表へ 1 行と見本 2 本を足すこと。

### 版の欄名は形式ごとに違う

`migration/inputmap/*` の見本だけ版の欄が `version`（`format_version` ではない）。
`.inputmap` と `.sprite_mesh` は仕組みの導入前から `version` で版を持っており、
既存ファイルを 1 バイトも書き換えずに載せるため綴りをそのまま尊重している
（正典は `runtime/src/engine/core/migration/kind.rs` の表）。
