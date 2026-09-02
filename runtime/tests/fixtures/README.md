# テストフィクスチャ

ここに置くファイルは **テスト専用のコピー（スナップショット）** である。

## なぜコピーを置くのか

`runtime/assets/` はユーザーがエディタ上でファイルを自由に移動・改名・削除できる
**作業領域** である。テストがそこを `include_str!` していると、ユーザーがアセットを
1 つ動かしただけでクレートがコンパイル不能になる（実際に発生した）。
そのため「出荷サンプルが壊れていないこと」を確かめるテストは、`runtime/assets/` を
直接読まず、このディレクトリのコピーを読む。

## 担保が変わる点（重要）

コピーである以上、これらのテストが担保するのは
**「スナップショット時点の出荷サンプルが壊れていないこと」** であって、
「今この瞬間の `runtime/assets/` の中身が壊れていないこと」ではない。
アセット側の正本を編集しても、コピーを更新しない限りテストは古い内容を見続ける。

**正本を更新したら、このディレクトリのコピーも同じ内容へ差し替えること（バイト一致が前提）。**

## 対応表（コピー元＝正本）

| フィクスチャ | 正本 | 使用テスト |
| --- | --- | --- |
| `shaders/magma.wgsl` | `runtime/assets/templates/shaders/magma.wgsl` | `renderer::water::shading_asset` |
| `shaders/poison.wgsl` | `runtime/assets/templates/shaders/poison.wgsl` | 同上 |
| `shaders/pop_ocean.wgsl` | `runtime/assets/mainGame/shaders/pop_ocean.wgsl` | 同上 |
| `terrain/cover_materials.json` | `runtime/assets/templates/terrain/cover_materials.json` | `terrain::cover::tests_cover` |
| `terrain/props.json` | `runtime/assets/templates/terrain/props.json` | `app::terrain_scatter_ops` |
| `terrain/layers.json` | `runtime/assets/templates/terrain/layers.json` | 同上 |

`*.sprite_mesh` はアセット由来ではなくテストのために手で作った入力なので、正本は無い。
