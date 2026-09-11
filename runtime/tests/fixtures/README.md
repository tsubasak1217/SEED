# テストフィクスチャ

ここに置くファイルは **テスト専用のコピー（スナップショット）** である。

## なぜコピーを置くのか

プロジェクトの `assets/`（例: `projects/WarashibeFishing/assets/`）はユーザーがエディタ上でファイルを自由に移動・改名・削除できる
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
| `shaders/pop_ocean.wgsl` | `projects/WarashibeFishing/assets/mainGame/shaders/pop_ocean.wgsl` | 同上 |
| `terrain/cover_materials.json` | `templates/terrain/cover_materials.json` | `terrain::cover::tests_cover` |
| `terrain/props.json` | `templates/terrain/props.json` | `app::terrain_scatter_ops` |
| `terrain/layers.json` | `templates/terrain/layers.json` | 同上 |

`*.sprite_mesh` はアセット由来ではなくテストのために手で作った入力なので、正本は無い。
