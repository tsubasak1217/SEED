# Terrain（地形エディタ）設計メモ — T1 ランタイム基盤 ＋ T2/T2b レイヤブレンド

本書は SEED の地形（terrain）機能の設計正典である。
**T1: ランタイム基盤**（ボクセル SDF ＋ marching cubes による洞窟対応の破壊可能地形）と、
**T2: 地形マテリアルのレイヤブレンド**（スプラット × triplanar・斜度/高度ルールによる自動下地・
ペイントブラシによる手修正）と、
**T2b: レイヤ拡張・テクスチャ・タイリング解消**（レイヤ定義を最大 16 層へ拡張しつつ
チャンク単位パレットで同時ブレンド 4 層の頂点フォーマットを維持・レイヤテクスチャの
2D 配列対応・3 種のタイリング解消モード）をカバーする。

> スコープ外（未実装）: 木/草散布、LOD／ストリーミング。これらは T3 で追加する（末尾「拡張余地」参照）。

---

## 1. 全体像

- **表現**: ボクセル SDF（符号付き密度場）。ハイトマップではないため洞窟・オーバーハングを表現できる。
- **メッシュ化**: marching cubes（CPU）。頂点法線は密度場の勾配。
- **単位**: ボクセル 0.5 m、チャンク 16 m 角（= 32 セル／軸、サンプル 33/軸）。すべて `TerrainSettings` で設定化。
- **密度規約**: `density < iso_level` ⇒ SOLID（内部）、`density > iso_level` ⇒ AIR（外部）、`== iso_level` が表面。
  平坦地面は `density(p) = p.y`（y=0 が地表、下が SOLID）。
- **ヒエラルキー**: シーンルート直下に **フォルダノード** `terrain`（`is_folder`・Transform 非保持）
  → その中にチャンク毎のフォルダノード `chunk_X_Y_Z` → さらに中に地形メッシュアクター
  （`ModelComponent` + `Transform` を持つ通常アクター）。フォルダは子のワールド変換に影響しない
  整理専用ノード（§11「ヒエラルキーの器」参照）。将来ここに木・草アクターを足す。

---

## 2. モジュール構成

### 純粋ライブラリ（エンジン非依存） — `runtime/src/engine/terrain/`

| ファイル | 責務 |
|---|---|
| `settings.rs` | `TerrainSettings`（全調整定数・serde・Default） |
| `chunk_coord.rs` | `ChunkCoord{x,y,z:i32}` と `world_origin()` |
| `chunk_data.rs` | `TerrainChunkData`（f32 密度グリッド 33³、read/write/`from_ground_plane`） |
| `marching_cubes.rs` | MC 本体＋正典テーブル（`EDGE_TABLE`/`TRI_TABLE`）、辺溶接キャッシュ、勾配法線、`TerrainMesh` |
| `brush.rs` | `SampleField` トレイト・`SphereBrush`・`BrushOp`・`apply()`（チャンク非依存の球ブラシ） |
| `tvox.rs` | バージョン付きバイナリ直列化（純 bytes、ファイル IO はしない） |
| `tests.rs` | 4 テスト（水密性・法線向き・境界同期・tvox 往復） |

このライブラリは ECS・レンダラを一切知らない（単一責任・ユニットテスト可能）。

### エンジン統合層 — `runtime/src/engine/`

| ファイル | 責務 |
|---|---|
| `components/terrain_component.rs` | `TerrainChunkComponent`(+`Data`)。チャンク座標＋tvox パスを保持する内部管理コンポーネント |
| `core/app_base/app/terrain_ops.rs` | `TerrainState`・`FieldView`(`SampleField` 実装)・各 `handle_terrain_*` ハンドラ |
| `core/app_base/app/terrain_mesh_build.rs` | `TerrainMesh` → エンジン `Model` 変換 |

`TerrainChunkComponent` は「ユーザーが手で付ける」コンポーネントではない（インスペクタのコンポーネント追加リストには出さない）。
シーンをまたいで各チャンクの tvox を再ロードするための **内部メタデータ** として ECS に載せている。

---

## 3. データ構造

### TerrainSettings（`settings.rs`）

| フィールド | 既定値 | 意味 |
|---|---|---|
| `voxel_size` | `0.5` | 1 ボクセルの一辺（m） |
| `chunk_cells` | `32` | チャンク 1 軸あたりのセル数。サンプル数＝`chunk_cells+1`（=33） |
| `iso_level` | `0.0` | marching cubes の表面しきい値 |
| `density_clamp` | `voxel_size*chunk_cells`（=16.0） | ブラシ編集で密度を `[-clamp, +clamp]` に収め勾配を有界化 |
| `ground_chunks_x` / `ground_chunks_z` | `4` / `4` | 初期平地のチャンク数（水平） |
| `ground_chunk_y_min` / `ground_chunk_y_max` | `-1` / `1` | 掘り下げ・盛り上げ余地のための垂直チャンク範囲 |

メソッド: `samples_per_axis()->usize`（=33）、`chunk_extent()->f32`（=16.0）。

### TerrainChunkData（`chunk_data.rs`）

- **表現**: `Vec<f32>` 長さ `33³ = 35,937`、row-major（`index = x + y*S + z*S*S`, `S=33`）。
- **再生成コスト（実測）**: 1 チャンク（33³ サンプル）の marching cubes 再メッシュ ≈ **1.6 ms**（release, 200 回平均,
  3,656 三角形の球チャンク。計測: `cargo test --release mc_regen_timing -- --ignored --nocapture`）。GPU アップロードは別途。
  ブラシ 1 回で影響チャンク（継ぎ目両側込み）を再生成しても数 ms 台に収まる。
- **メモリ/品質トレードオフ（採用理由）**: T1 は **f32** を採用。1 チャンク約 **143 KB**（35,937 × 4B）。
  4×4×3（=48）チャンクの初期平地で約 6.9 MB。編集時の精度と勾配法線の品質を優先した。
  - i8 量子化なら約 1/4（35 KB/チャンク）に削減できるが、量子化ノイズが勾配法線に乗るため T2 の最適化候補とする。
    ディスク（tvox）側も現状 f32 で往復ビット一致を保証している。i8 化する場合はメモリ・ディスク両方で行う。

---

## 4. Marching Cubes（`marching_cubes.rs`）

- 正典 MC テーブル（256 要素 `EDGE_TABLE` / 256×16 `TRI_TABLE`）を使用。各セル 8 コーナーの密度から表面三角形を生成。
- **頂点位置**: 辺上を密度線形補間（iso_level 交差点）。チャンクローカル座標（原点＝チャンク最小角、単位 m）。
- **法線**: 密度場の中心差分勾配を正規化。勾配は密度増加方向＝ SOLID→AIR ＝ **外向き** 表面法線。法線配列が唯一の真実。
- **ワインディング**: 幾何法線（右手系 `cross(b-a,c-a)`）が外向き解析法線と揃うよう三角形を必要に応じ反転して出力。
- **水密性**: 共有辺を「サンプル座標の正規化ペア(小,大)」でキー化し補間方向も固定。隣接セルは共有辺にビット一致の
  1 頂点を生成 → インデックス溶接済みで水密（テスト `sphere_mesh_is_watertight` が全無向辺＝正確に 2 三角形共有を検証）。
- API: `generate_standalone(&chunk,&settings)`（境界勾配を片側差分でクランプ、ユニットテスト用）／
  `generate(&chunk,&settings, neighbor_sampler)`（境界サンプルの勾配を隣接チャンクから読む、統合層で使用）。

---

## 5. チャンク境界の継ぎ目対策（重要設計）

**グローバルサンプル座標 ＋ 重複サンプル同期** で継ぎ目をゼロにする。

- チャンクは 33 サンプル/軸を持ち、**境界サンプル（グローバル index が 32 の倍数）は隣接チャンクと共有** される。
  例: チャンク(0,0,0) の x サンプル 32 ＝ チャンク(1,0,0) の x サンプル 0（同一ワールド座標）。
- 統合層の `FieldView`（`terrain_ops.rs`）が `brush::SampleField` を実装し、グローバル↔チャンクローカル変換を担う:
  - `owners(gx,gy,gz)`: あるグローバルサンプルを保持する **全チャンク** を返す（32 境界上では最大 8 チャンク）。
  - `write_global`: 値を **所有する全チャンク** の対応ローカルサンプルへ書く → 重複サンプルが常にビット一致。
  - `read_global`: 主所有チャンク（無ければ境界隣接チャンク）から読む。地形外は `density_clamp`（AIR）を返す。
- ブラシ編集・スムーズはすべて `read_global`/`write_global` 経由。ブラシはワールド座標の純関数として作用するため、
  継ぎ目の両側で同一入力・同一ステンシルとなり **設計上ビット一致** → メッシュ境界が水密に連続する。
- `brush::apply()` は編集サンプルを所有する ChunkCoord 集合（継ぎ目の両側を含む）を返す。統合層はその全チャンクを再メッシュする。
  再メッシュは `generate(neighbor_sampler=グローバル読み)` を使い、境界サンプルの勾配法線も両側で一致させる。

テスト `boundary_samples_stay_synced` が、継ぎ目をまたぐ Add ブラシ後に共有面サンプルがビット一致し、
両チャンクの共有面頂点が一致することを検証している。

---

## 6. ブラシ演算（`brush.rs`）

`BrushOp{Add=0, Subtract=1, Smooth=2, Flatten=3}`、`SphereBrush{center,radius,strength}`、`apply(field,brush,op,dt)`。
球内の各サンプルに `falloff`（中心 1 →半径 0 の smoothstep）を掛けて作用し、`[-density_clamp,+density_clamp]` にクランプ。

| op | 効果 |
|---|---|
| Add | `density -= strength*falloff*dt`（より SOLID＝盛る） |
| Subtract | `density += strength*falloff*dt`（より AIR＝掘る＝洞窟） |
| Flatten | 密度をブラシ中心を通る水平面（`sample_world_y - center_y`）へ向けて lerp |
| Smooth | 密度を 6 近傍平均（`read_global` 経由）へ向けて lerp。境界越しに読むため継ぎ目も水密 |

`dt` はクリック 1 回あたりの離散ステップ（既定 `BRUSH_DT=1.0`）。連続ドラッグはエディタ側で毎フレーム呼ぶ想定。

---

## 7. レンダリング統合

各チャンクの地形メッシュアクターは通常の `ModelComponent` として不透明パスに載る。

- `TerrainMesh` → エンジン `Model`（`terrain_mesh_build.rs`）: 1 ノード/1 メッシュ/1 プリミティブ。`Vertex` に position・normal を
  詰め、tangent=`[1,0,0,1]`・uv=`[0,0]`・color=`[1,1,1,1]`、`skin_vertices` は空（スキン付きは BLAS/RT 除外のため）、
  マテリアル 1 枚（T2 で triplanar/レイヤブレンドへ置換）。
- **両面描画（T1）**: エンジンは `FrontFace::Ccw`＋マテリアル別カリング（既定 Back）。marching cubes のワインディングが
  この規約と一致せず片面カリングだと地表が裏面判定で消えるため、地形マテリアルは `cull_face=None`（両面）にしている。
  陰影は頂点法線（密度勾配＝外向き、テスト検証済み）で行われるため両面でも正しい。T2 で片面ワインディングへ正す余地あり。
- **空メッシュチャンク**: 全 AIR / 全 SOLID のチャンクは表面三角形が 0。GPU アップロードすると「サイズ 0 バッファ」を
  `buffer.slice()`（RT BLAS・ドロー）でパニックさせるため、空メッシュは `gpu_model=None`（非描画）にして GPU リソースを作らない。
  MC スロット自体は保持し、掘削で表面が出たら再メッシュ時にアップロードする。
- `ModelComponent`: `source_path = "terrain://<scene>/chunk_X_Y_Z"`（非空＝描画・RT キャスタ対象になる）、`cast_shadows = true`、
  `instance_mats = [translate(world_origin)]`、メッシュ位置はチャンクローカル。アクターの `Transform.position = world_origin`。
- **RT（TLAS/影・映り込み）**: 不透明かつ `cast_shadows` かつ非空 source_path なので、既存の不透明モデルと同様に自動で
  TLAS に登録され RT 影・反射に映る（RT 対応時に `BLAS_INPUT` usage が付く）。
- **VRAM スパイク防止（編集チャンク再アップロード時に必須）**: `slot_ops::handle_set_material_override` と同手順で
  `gpu_model=None` → `device.poll(PollType::Wait)`（解放確定）→ 新 `gpu_model`/`instanced_batch` を生成 →
  `mark_batch_dirty()`。旧 drop 前に新規確保すると一時 2× VRAM 需要 → OOM になるため、この順序を厳守。

### terrain:// スキームガード（シーンロード対策）

地形メッシュアクターはセーブ時に `ModelComponent`(source_path=`terrain://…`) も保存される。ロード時 `build_actor` の
ModelComponent 分岐がこのパスをそのまま `load_model` すると（実ファイルが無いため）シーンロード全体が失敗する。
そのため **`TERRAIN_SOURCE_SCHEME = "terrain://"` で始まる source_path は `load_model` をスキップ**（model/gpu を None のまま
source_path のみ保持）し、後述の再構築パスで tvox から埋める。空パスガードと同じ場所に実装。

---

## 8. シーン永続化

- 地形メッシュアクターは `TerrainChunkComponent{chunk_x/y/z, tvox_path}` を持ち、`.scene`（pretty JSON）に
  `{"type":"TerrainChunkComponent","data":{...}}` として保存される（他コンポーネント同様、`ComponentData` の adjacently-tagged enum 経由）。
- ボクセル密度そのものはシーン JSON には入れず、チャンク毎に **tvox バイナリ** へ別口保存する（`TERRAIN_SAVE`）。
  シーンには「terrain ルート＋チャンクアクター（tvox パス参照）」のみが残る。
- **ロード時再構築** `rebuild_terrain_after_load()`（`load_play_scene` と `LoadScene` ハンドラの末尾で呼ぶ）:
  全アクターを走査し `TerrainChunkComponent` を持つものの tvox を `asset_fs::read_bytes` で読み、`tvox::read_chunk` で復元、
  `TerrainState.chunks` へ格納。全チャンク読み込み後に各チャンクをメッシュ化（境界が正しく揃う）し、None のままの
  ModelComponent を埋める。tvox 欠損はログ＋スキップ（ロードは中断しない）。
- **既知の限界（T1）**: ロード後の `TerrainState.settings` は既定（voxel_size=0.5 / 32 セル）にリセットされる。
  `tvox::read_chunk` の戻り値が `TerrainSettings` を返さないため。既定構成では問題ないが、非既定 voxel_size プロジェクトを
  完全復元するには tvox ヘッダの voxel_size/samples を `TerrainState` へ反映する小改修が要る（T2 で対応）。

### tvox フォーマット（v3・リトルエンディアン）— T2b でレイヤ番号付きに拡張

| オフセット | 型 | 内容 |
|---|---|---|
| 0 | u8[4] | マジック `"TVOX"` |
| 4 | u32 | バージョン（現在 `3`） |
| 8 | i32 | チャンク座標 x |
| 12 | i32 | チャンク座標 y |
| 16 | i32 | チャンク座標 z |
| 20 | u32 | samples_per_axis（例 33） |
| 24 | f32 | voxel_size（m） |
| 28 | u32 | **slot_count**（1 サンプルのブレンドスロット数。v3 では `TERRAIN_BLEND_SLOTS`=4） |
| 32 | f32 × N | 密度サンプル（N = samples_per_axis³, row-major） |
| … | u8 × N×S | **スロットのレイヤ番号**（S = slot_count・サンプルごとに S バイト。v3 で追加） |
| … | u8 × N×S | **スロットの手ペイント重み**（u8 量子化。paint_index と添字対応） |
| … | u8 × N | **ペイント量**（0=未ペイント〜255=完全に手描き優先） |

ヘッダ v3 = 32 バイト（v2 と同じヘッダ長。28 バイト目の意味が layer_count→slot_count に変わるだけ。
v1 = 28 バイト）。`read_chunk` は magic/version を検証し、
`TvoxError{BadMagic, BadVersion, Truncated, DimMismatch}` を返す。

**v2 後方互換（T2b で追加）**: v2 は「レイヤ番号なし・レイヤ 0..L-1 の重みを密に並べる」形式だった
（T2b 以前のフォーマット）。v2 を読む場合はスロットのレイヤ番号を `[0,1,2,3]`（の先頭 `min(L,4)` 個）と
みなして重みだけを取り込む。T2b 以前のセーブデータもそのまま開ける。

**v1 後方互換**: v1（密度のみ・レイヤ情報無し）も `read_chunk` が受け付ける。
その場合スプラットは「全サンプル未ペイント」で復元される。未ペイント＝斜度/高度ルールによる
自動下地が全面に適用されるため、旧セーブデータも正しくレイヤブレンドされた地形として表示される
（重みが欠落して黒落ちする、といった破綻は起きない）。
保存は常に v3 で行う（v1/v2 の書き手 `write_chunk_v1`/`write_chunk_v2` はテスト・移行用のみ）。

**スロット数が変わったとき**: ファイルの `slot_count`（v2 では `layer_count`）が現在の
`TERRAIN_BLEND_SLOTS` と異なる場合、共通する先頭 `min(S_file, S_now)` 個だけを読み、
残りは 0 で埋める（定義の増減でロード不能にしない）。
保存先: `<assets_root>/terrain/<scene>/chunk_X_Y_Z.tvox`（`std::fs::create_dir_all`＋`std::fs::write`。読みは `asset_fs::read_bytes` で PAK 対応）。

---

## 9. IPC 仕様（エディタ実装フェーズへの引き継ぎ）

SEED の IPC は **行指向のプレーンテキスト**（名前付きパイプ、`\n` 区切り、`COMMAND:arg1,arg2,…`、SCREAMING_SNAKE_CASE）。
serde/JSON ではない。地形コマンドは以下の 3 つ。エディタ側はこの文字列を送るだけでよい。

| コマンド（送信） | 引数 | 応答（受信） |
|---|---|---|
| `TERRAIN_INIT` | なし | `TERRAIN_INIT_OK` |
| `TERRAIN_BRUSH:{op},{screen_x},{screen_y},{radius},{strength}` | `op`:u32（0=Add,1=Subtract,2=Smooth,3=Flatten）／他 f32。`screen_x/y` はビューポート左上原点のピクセル座標 | ヒット時 `TERRAIN_BRUSH_OK:{hx},{hy},{hz}`（ワールドヒット点）／非ヒット `TERRAIN_BRUSH_MISS` |
| `TERRAIN_PAINT:{layer},{screen_x},{screen_y},{radius},{strength}` | `layer`:u32（塗る対象レイヤ番号・0 起点／`layers.json` の並び順）／他 f32 | ヒット時 `TERRAIN_PAINT_OK:{layer},{hx},{hy},{hz}`／非ヒット `TERRAIN_PAINT_MISS` |
| `TERRAIN_SAVE` | なし | `TERRAIN_SAVE_OK:{count}`（保存チャンク数）／`TERRAIN_SAVE_ERROR:{msg}` |
| `TERRAIN_BRUSH_PREVIEW:{screen_x},{screen_y},{radius},{strength}` | 全 f32。ホバー中（非押下）に送る。`strength` はプレビュー球の色（強度連動）にのみ使う | 応答なし（高頻度・ホバー用）。レイマーチのヒット点にブラシ半径のワイヤスフィアを描く |
| `TERRAIN_BRUSH_PREVIEW_OFF` | なし | 応答なし。プレビュー（ワイヤスフィア）を非表示にする |
| `TERRAIN_UNDO` | なし | 応答なし。地形専用 undo スタックを 1 ストローク分戻す（下記 §9.1） |
| `TERRAIN_REDO` | なし | 応答なし。地形専用 undo を 1 ストローク分やり直す |
| `TERRAIN_STROKE_END` | なし | 応答なし。進行中ストロークを 1 undo エントリとして確定する（左ボタン解放時に送る） |
| `TERRAIN_HEIGHTMAP:{path},{height_scale}` | `path`=画像の実ファイル絶対パス（png/jpg）、`height_scale`:f32（最大高さ m）。`path` にカンマが含まれても壊れないよう **最後のカンマで path / height_scale を分割** する | `TERRAIN_HEIGHTMAP_OK:{ms}`（処理ミリ秒）／`TERRAIN_HEIGHTMAP_ERROR:{msg}` |

- `TERRAIN_INIT`: terrain ルート＋初期平地（`ground_chunks_x × ground_chunks_z × [y_min..=y_max]` チャンク、y=0 に地面）を生成。
- `TERRAIN_BRUSH`: カーソル位置からカメラレイを作り、グローバル密度場を SDF レイマーチして最初の AIR→SOLID 交差点を求め、
  そこへ球ブラシを適用。ヒットが無ければ無視（MISS）。適用後、影響チャンクを VRAM 安全手順で再アップロードし dirty 化。
- `TERRAIN_PAINT`: `TERRAIN_BRUSH` と同じレイマーチで着弾点を求め、そこへ球ブラシで**レイヤ重みだけ**を塗る
  （密度＝形状は一切変えない）。undo は密度ブラシと同じストローク単位（`TERRAIN_STROKE_END` で確定）に載る。
  ペイントは形状を変えないが頂点属性（頂点カラー＝レイヤ重み）が変わるため、影響チャンクは再メッシュされる。
- `TERRAIN_SAVE`: 全チャンクの tvox を書き出す（シーン保存とは別口。シーン保存は既存 `SAVE_SCENE`）。

### 9.1 地形 undo/redo（1 ストローク = 1 エントリ）

- **専用スタック**：既存の `undo.rs`（`Command`/`UndoHistory`）は `Scene` を対象にするが、地形密度は
  `App.terrain`（`TerrainState`・Scene 外の `HashMap<ChunkCoord, TerrainChunkData>`）にあるため統合せず、
  `TerrainState` に **地形専用スタック** を新設した（`undo_stack` / `redo_stack: Vec<TerrainEdit>`、
  `TerrainEdit{ before, after: HashMap<ChunkCoord, Vec<f32>> }`。触ったチャンクの密度のみ保持）。上限
  `TERRAIN_UNDO_MAX = 32` エントリ（超過は古いものから捨ててメモリ有界化。1 チャンク 33³×4B≈144 KB）。
- **粒度**：LBUTTONDOWN→UP の 1 ストローク＝1 エントリ。ストローク開始は明示 IPC を作らず
  「`stroke_active` でない状態で最初の `TERRAIN_BRUSH` が来たとき」に暗黙開始。`handle_terrain_brush_world` は
  ブラシ適用**前**に `brush::chunks_in_brush_aabb`（純関数・AABB→所有チャンク）で触りうるチャンクを求め、
  未登録のものだけ現在密度を `stroke_before` へスナップショットする（初回タッチ時コピー）。
- **確定**：`TERRAIN_STROKE_END` で `stroke_before`（before）と現在密度（after）から `TerrainEdit` を作り
  `undo_stack` へ push、`redo_stack` をクリア。`TERRAIN_UNDO`/`TERRAIN_REDO` は該当チャンクの密度を
  `TerrainChunkData::set_raw_density` で書き戻し、`remesh_chunks`（ブラシ編集と共通の VRAM 安全再メッシュ手順）で
  再メッシュする。
- **整合**：`TERRAIN_INIT` と `TERRAIN_HEIGHTMAP` は密度場を丸ごと差し替えるため、undo/redo スタックを
  クリアする（旧エントリを適用すると座標が食い違って壊れるため。コード内コメント明記）。
- エディタは terrain モード中の Ctrl+Z / Ctrl+Y を（通常の `UNDO`/`REDO` ではなく）`TERRAIN_UNDO`/`TERRAIN_REDO`
  として送り、左ボタン解放とモード離脱で `TERRAIN_STROKE_END` を送る。

### 9.2 ハイトマップ読込（TERRAIN_HEIGHTMAP）

- ランタイムは `image` クレートで png/jpg を読み、グレースケール（`to_luma8`）→ luma01 正規化 →
  `HeightmapField`（`terrain/heightmap.rs`・`image` 非依存の純粋構造体。バイリニア補間）を組む。
- 画像を初期平地フットプリント（world x∈[0, `ground_chunks_x`×`chunk_extent`], z∈[0, `ground_chunks_z`×`chunk_extent`]）へ
  張り、各サンプルの world(x,z)→uv→バイリニアでグレー値 g01→高さ `h = g01 × height_scale`。密度 = `worldY − h`
  （規約どおり `worldY < h` が SOLID、`> h` が AIR、`= h` が表面。洞窟の無い純地形）。
- 地形ツリー構築（root/フォルダ/mesh・MC/TerrainChunk スロット・GPU アップロード）は `TERRAIN_INIT` と共通化した
  `build_terrain_with<F>(fill)` を通す（INIT は `from_ground_plane`、ハイトマップは `HeightmapField` サンプラを渡す）。
  既存地形があれば INIT と同じ冪等経路で置き換え、undo スタックはクリア、全チャンクを再メッシュ。処理時間を
  `TERRAIN_HEIGHTMAP_OK:{ms}` で返す（実測: 64×64 グラデーション PNG・48 チャンクで約 44〜47 ms）。
- エディタは地形ツールバーの「ハイトマップ読込」ボタン→`OpenFileDialog`（png/jpg）→高さスケール入力
  （`TxtTerrainHeightScale`・既定 10 m）→ `TERRAIN_HEIGHTMAP:{path},{scale}` を送る。

### 9.3 プレビュー球の追従と強度連動カラー

- **追従（不具合修正）**：ストローク中はエディタが `TERRAIN_BRUSH_PREVIEW` を送らない（移動握り潰し回避のため）。
  そのため押しながらだと球が置き去りになっていた。ランタイムの `handle_terrain_brush` が **ブラシ着弾点で
  `terrain.brush_preview` も同時更新** するよう修正（追加レイ不要）。これでドラッグ中も球が着弾点に追従する。
- **強度連動カラー**：`brush_preview` を `Option<([f32;3], 半径, 強度)>` に拡張し、`frame_renderer` が
  `TERRAIN_PREVIEW_COLOR_LOW`（低強度＝薄い水色）→`TERRAIN_PREVIEW_COLOR_HIGH`（高強度＝濃いオレンジ）を
  強度 0..1 で線形補間する。ホバー時は `TERRAIN_BRUSH_PREVIEW` の 4 番目 `strength` を反映するため、
  Shift+ホイールで強度を変えると球の色が即変わる。

### 9.4 ブラシ半径・強度のホイール操作（エディタ）

- terrain モード中・ビューポート上の `WM_MOUSEWHEEL` を `WH_MOUSE_LL` フックで捕捉する。**Ctrl+ホイール**で半径
  （乗算式・1 ノッチ ±10%＝`TerrainRadiusWheelFactor=0.10`）、**Shift+ホイール**で強度（加算式・1 ノッチ ±0.05＝
  `TerrainStrengthWheelStep`）をスライダー範囲内で増減し、イベントを飲み込んでランタイムのカメラズームへ流さない。
  修飾キー無しのホイールは素通し（従来のズーム）。変更後は即プレビューを再送し、球の大きさ・色が即反映される。

### レイマーチ詳細（TERRAIN_BRUSH の内部）

`gizmo_interact` の `editor_3d_ray`（透視/正射対応）でスクリーン→ワールドレイを生成し、
密度場を三線形補間サンプルしながら `voxel_size*0.5` 刻みで前進、最初の `density>=iso → <iso`（AIR→SOLID）交差を検出、
8 回二分法で精緻化してヒット点を得る。既存の GPU ID ピッキングや物理レイキャストは使わない（地形は解析 SDF で直接当てる）。

---

## 10. デバッグスモークフック（恒久・環境変数ゲート）

`handle_resumed` 末尾（`load_play_scene` 後）に、環境変数 `SEED_TERRAIN_SMOKE=1` のときだけ動く恒久フックを置いた。
起動直後に `TERRAIN_INIT` → デバッグカメラを地形フットプリント俯瞰位置へ（設定から算出、マジックナンバー無し）→
`Add`（盛り上がり）と `Subtract`（掘り＝穴/洞窟）を数回自動適用する。通常の play/edit では動かない。
IPC を叩けない環境（エディタ非接続）で地形生成・編集を実機確認するための手段。

### レイヤ定義の差し替えフック（環境変数 `SEED_TERRAIN_LAYERS`）

`ensure_terrain_layers()`（`terrain_ops.rs`）はレイヤ定義を読む際、環境変数
**`SEED_TERRAIN_LAYERS`** に絶対パスが設定されていれば `assets://terrain/layers.json` の代わりに
そのファイルを読む（空文字は無視）。プロジェクトのアセットを一切書き換えずに、
別のレイヤ構成（テクスチャ付き・16 層フル定義・detile モード違い等）を実機で試すための
恒常フック。未設定時の挙動は従来どおり（`assets://terrain/layers.json` → 読めなければ既定 4 層）。

---

## 11. エディタ操作仕様（T1 後半・WPF エディタ UI）

ランタイムの IPC（§9）をエディタの UI へ接続した層。実装は `editor/src/MainWindow.Terrain.cs`
（ロジック）と `editor/src/MainWindow.xaml`（モードコンボ＋地形ツールバー）、
`editor/src/Runtime/RuntimeManager.cs`（応答受信）に分かれる。

### モード切替（common / terrain）

- シーンパネル左上のコンボ **`CmbSceneMode`**（`common` / `terrain`、既定 common）。Blender のモード切替のイメージ。
- `terrain` を選ぶとタブバー下段に **地形ツールバー**（`TerrainToolbar`）が現れ、シーンビュー上の左ドラッグが
  地形ブラシになる。`common` では従来どおりの選択・ギズモ編集。

### 地形ツールバー（terrain モード時のみ表示）

| UI | 機能 |
|---|---|
| ツール選択（盛る/掘る/均す/平坦化/ペイント） | トグル（`RadioButton` 群）。選択状態をアクセント色で表示。`op` = 0/1/2/3 に対応。**ペイント**だけは擬似 op（100）で、`TERRAIN_BRUSH` ではなく `TERRAIN_PAINT` を送る |
| レイヤ選択コンボ（`CmbTerrainLayer`） | **ペイントツール選択時のみ表示**（`TerrainLayerPanel`）。項目は `assets/terrain/layers.json` の並びに対応し、`SelectedIndex` がそのまま `TERRAIN_PAINT` の `layer` になる |
| ブラシ半径スライダー | 0.5〜8 m（`TERRAIN_BRUSH` の radius）|
| ブラシ強度スライダー | 0〜1（`TERRAIN_BRUSH` の strength）|
| 「地形を初期化」ボタン | `TERRAIN_INIT` を送る。再初期化時は確認ダイアログを出す（既存地形は作り直される）|
| 「地形を保存」ボタン | `TERRAIN_SAVE` を送る。結果（保存チャンク数/エラー）をステータス表示 |

### ブラシ入力（マウス）

- ビューポート（ランタイム HWND）は WPF の入力ルートを通らないため、**低レベルマウスフック（`WH_MOUSE_LL`）** で
  左ドラッグを捕捉する（キーボードフックと同じ UI スレッドに常設。terrain モードかつ Edit 状態のときだけ作用）。
- 左ボタン押下でストローク開始、移動中は **スロットル（`TerrainBrushThrottleMs` = 40 ms）** で
  `TERRAIN_BRUSH:{op},{lx},{ly},{radius},{strength}` を送る（`lx,ly` はビューポート左上原点の物理ピクセル。
  `GetCursorPos - GetWindowRect(ContainerHwnd).TopLeft`。既存の `DROP_ACTOR` と同じ座標変換）。
- terrain モード中は **ビューポート上の左ボタン押下/移動/解放をフックで飲み込む**（`return 1`）ため、
  ランタイムの選択・ギズモへは届かない（＝選択/ギズモ無効）。右ドラッグ（カメラ回転）・WASD 等は
  フックが一切触れないため従来どおり効く。ストローク中でない移動はランタイムへ素通しする。

### ドラッグ追従（押しながら塗り続ける）

- 左ボタン押下でストローク開始（`_terrainStroking=true`）→ 押下中の `WM_MOUSEMOVE` を
  スロットル（40ms）で `TERRAIN_BRUSH` として送り続ける → 左ボタン解放で終了。ボタン押下状態を
  `_terrainStroking` で追跡するため、押しながらカーソルを動かすと連続適用され線状の畝ができる。
- ランタイムは 1 ブラシごとに影響チャンクのみを再メッシュ化する（`handle_terrain_brush_world`）。
  1 チャンク再メッシュ ≈ 1.6ms（§3）で、ストローク中の 40ms 間隔・数チャンク差し替えに追従する。
  スモーク（`SEED_TERRAIN_SMOKE=1`）は連続ストローク（`SMOKE_STROKE_STEPS` 点への Add 連打）で
  この追従を実機確認する（線状の盛り上がりが出る）。

### ブラシ範囲プレビュー（ワイヤスフィア）

- **ねらい**: 押していないホバー中も、ブラシがどこにどの大きさで当たるかを可視化する。
- **エディタ**: terrain モードの非ストローク `WM_MOUSEMOVE` で、ビューポート上なら 40ms スロットルで
  `TERRAIN_BRUSH_PREVIEW:{lx},{ly},{radius}` を送る（移動は飲み込まず素通し＝カメラ操作を妨げない）。
  ビューポート外へ出た瞬間・terrain モード離脱時に `TERRAIN_BRUSH_PREVIEW_OFF` を 1 度送る
  （`_terrainPreviewActive` で重複送信を抑止）。
- **ランタイム**: `handle_terrain_brush_preview` が `terrain_raymarch_hit`（ブラシ着弾と共通の SDF
  レイマーチ）で地形ヒット点を求め、`TerrainState.brush_preview = Some((中心, 半径))` を設定する
  （ヒット無し＝空を指すフレームは `None`＝非表示）。
- **描画**: `frame_renderer` が既存のデバッグ線描画基盤（`LineBatch` / `draw_line_batch`・グリッドや
  コライダーワイヤと同じ経路）を流用し、`LineBatch::add_wire_sphere_latlong`（緯線・経線グリッドの
  ワイヤスフィア）を組んで半透明シアンで描く。**`in_editor` のみ描画・Play では出さない**。色・分割数は
  `TERRAIN_PREVIEW_*` 定数で集約（マジックナンバー無し）。

### ヒエラルキー整合（設計の要）

- **ランタイムがシーンの正、エディタは指示役**。`handle_terrain_init` が生成した地形アクター
  （`terrain` ルート → `chunk_X_Y_Z` → `mesh`）は、同ハンドラ末尾の `send_hierarchy()` により
  `HIERARCHY` としてエディタへ届き、ヒエラルキーパネルへ自動反映される（追加の同期実装は不要）。
- **シーン保存**はエディタが `SAVE_SCENE:{path}` を送り、ランタイムが自分の `scene`
  （terrain アクター＋`TerrainChunkComponent` を含む）を `scene.save` でシリアライズする。エディタ側で
  独自にシーンを直列化する経路は無いため、**地形アクターは保存で消えない**。密度データ本体は
  `TERRAIN_SAVE` で別口の .tvox に保存される（§8）。
- **二重初期化対策**: `handle_terrain_init` は冪等化してあり、実行時に既存の `terrain` ルートと
  そのサブツリーのエンティティを despawn してから作り直す（`TERRAIN_INIT` を再実行しても
  ヒエラルキーに重複ルートが残らない）。エディタ側も再初期化前に確認ダイアログを出す。
- **`TerrainChunkComponent` はインスペクタのコンポーネント追加リストに出さない**（内部管理メタデータ）。
  エディタの追加リスト（`ComponentSelectorWindow` の静的 `Categories`）に項目が無いため既定で出ない。

---

### ヒエラルキーの器：フォルダノード機構（実装済み）

**方式**: 汎用の **フォルダノード**（`Actor.is_folder` フラグ）を新設し、地形ルートと
チャンクの器をこれで構築する。ヒエラルキーは `terrain`（フォルダ）→ `chunk_X_Y_Z`（フォルダ）
→ `mesh`（通常アクター＝ `ModelComponent` + `Transform` 保持）となる。各チャンクメッシュは
従来どおり **固有ジオメトリを持つ独立した ModelComponent**（1 アクター 1 メッシュ）である。

**フォルダノードの性質**:
- **Transform / CanvasTransform を一切持たない**整理専用の透過ノード。子のワールド変換に
  影響しない（描画・物理・スクリプトの走査からは「コンポーネント無しアクター」として素通し
  される。既存の 2D アクターが Transform を持たないのと同じく、走査系は `world.get::<T>()` の
  `Option` を安全に処理するためパニックしない）。
- 保存/ロードで永続化。`ActorData.is_folder`（`#[serde(default, skip_serializing_if=false)]`）で
  往復し、**既存シーンは省略＝ false（通常アクター）としてバイト互換に読める**。
- `build_actor` は `is_folder` のとき Transform/CanvasTransform を挿入しない分岐を持つ
  （ロード・プレハブ再展開・クリップボード貼り付けすべてがこの経路を通る）。

**ヒエラルキー送信 / エディタ表示**:
- `collect_actor_nodes` → `build_hierarchy_json` が `is_folder` を HIERARCHY JSON へ出力する。
  フォルダは同時に `is_group=true` としても送り、エディタ既存のグループ系ロジック（選択種別
  スキップ・ソート・エクスポート除外）と整合させる。
- エディタ（`HierarchyPanel`）はフォルダを **専用アイコン（`▤`・落ち着いた黄土色）** で
  グループ（黄 `▶`）とも通常アクター（`◆`）とも視覚区別する。
- `send_actor_components` はフォルダのとき `transform`/`canvas_transform` を送らず `is_folder` を
  付す。`InspectorPanel` はフォルダ選択時に **Transform セクションを出さず**「フォルダ（整理用
  ノード・Transform なし）」表示にする（名前変更・アクティブ切替は可能）。

**移行（既存シーンのフォルダ化）**: `rebuild_terrain_after_load`（ロード末尾）に移行ステップを
置いた。`name==terrain` のトップレベルアクターと、その直下のコンポーネント無しの器アクター
（`chunk_X_Y_Z`）を検出して `is_folder=true` へ作り直し、Transform を取り除く。メッシュアクター
（Model/TerrainChunk スロット持ち）はそのまま。既にフォルダ化済み（新規保存）のシーンでは
何もしない冪等処理。`handle_terrain_init` の冪等除去（`TERRAIN_ROOT_NAME` サブツリー despawn）は
そのまま流用でき、ルート/チャンクの器を `Actor::new_folder` で作り直す。

**チャンクメッシュ**はフォルダ直下にフラット配置し、`mesh_tf.position=world_origin`・
`instance_mats` はワールド空間のまま（親子 Transform 合成に依存しない現構造をそのまま活かす）。

**既知の制限**: スクリプト `Scene.Destroy()` はエンティティの生存確認に Transform/CanvasTransform
の有無を使うため、両方を持たないフォルダは破棄要求が拒否される（安全側に倒れるだけでパニック
はしない）。地形フォルダは `terrain_ops` が直接 despawn し、エディタ削除は DFS id 経由の
`REMOVE_ACTOR`/`DELETE_RECURSIVE` で行うため実害はない。スクリプトからフォルダを破棄させたい
場合は `ffi_destroy` の生存確認を is_folder 対応へ拡張する余地がある。

## 12. T2: 地形マテリアルのレイヤブレンド

AAA 定番の **スプラット（レイヤ重み）× triplanar** 方式。
「斜度/高度ルールによる自動下地生成」と「ブラシによる手ペイント修正」の**両方**に対応する。

> 本節は T2 時点の基本設計（1 頂点が同時に持てるレイヤは 4 種まで＝レイヤ定義自体の上限も 4）を
> 記す。レイヤ定義数を 16 まで拡張し、頂点フォーマットは変えずに済ませる T2b の設計は §12.7 以降を参照。

### 12.1 レイヤ定義（データドリブン）— `assets/terrain/layers.json`

レイヤ構成はアセットで定義する。値を書き換えるだけで塗り分けが変わる（コード変更不要）。
サンプルは §12.7 のテクスチャ・detile 付き例を参照（本節の最小例は base_color のみの単色レイヤ）。

```jsonc
{
  "layers": [
    {
      "name": "grass",                 // エディタのレイヤ選択 UI に出る表示名
      "base_color": [0.16, 0.38, 0.12],// リニア RGB 係数（テクスチャがあれば乗算）
      "roughness": 0.95,
      "metallic": 0.0,
      "uv_scale": 0.25,                // triplanar UV スケール（world 1m あたりの UV 進み量）
      "base_color_texture": null,      // アセット相対パス。null なら base_color の単色レイヤ
      "rule": {                        // 斜度/高度による自動下地生成ルール
        "slope_min_deg": 0.0,          // 斜度ウィンドウ（0=水平, 90=垂直）
        "slope_max_deg": 22.0,
        "slope_fade_deg": 10.0,        // ウィンドウ両端の smoothstep ぼかし幅
        "height_min": -1000000.0,      // 高度ウィンドウ（ワールド Y・メートル）
        "height_max":  1000000.0,
        "height_fade": 2.0,
        "priority": 1.0                // 同条件で複数層が立つときの配分比
      }
    }
    // … 最大 16 層（TERRAIN_MAX_LAYERS。T2 時点は 4 層＝TERRAIN_BLEND_SLOTS が上限だった）。
    // 超過分は先頭 16 層へ切り詰め
  ]
}
```

- 正典実装: `runtime/src/engine/terrain/layers.rs`（純粋データ層。IO/GPU 非依存）。
- ファイルが無い／壊れている場合は**既定セット（単色 4 層: 草・土・岩・砂）へフォールバック**する。
  アセット未整備でもブレンドが目視でき、データドリブンな試作を止めない。
- レイヤ定義数の上限は `TERRAIN_MAX_LAYERS = 16`（layers.rs）。GPU 側のレイヤテクスチャ配列の
  枚数上限と一致させてある。**同時にブレンドできる数（4）とは別の定数**であることに注意
  （§12.7「レイヤ数の拡張とチャンク単位パレット」参照）。

### 12.2 スプラット（レイヤ重み）の保持方法とメモリ

密度と同じ 33³ グリッド上に、**手ペイント分だけ**を保持する（`chunk_data.rs`）。
T2b で「どのレイヤ番号に塗ったか」を持ち回る必要が生じたため、重みだけでなくスロットごとの
**レイヤ番号**も保持するようになった（旧: 重みのみ 4 成分 → 新: レイヤ番号 4 ＋重み 4）:

| 配列 | 型 | サイズ/チャンク |
|---|---|---|
| `density` | f32 × 1 | 143.7 KB |
| `paint_index`（スロットのレイヤ番号・T2b で追加） | u8 × 4 | 143.7 KB |
| `paint_weight`（スロットの手ペイント重み） | u8 × 4 | 143.7 KB |
| `paint_amount`（ペイント量 0..1） | u8 × 1 | 35.9 KB |
| **合計** | | **467.0 KB/チャンク** |

既定の地面 4×4×3 = 48 チャンクで約 **22.4 MB**。
u8 量子化を採ったのは、f32 のままだと `paint_weight` だけで 575 KB/チャンク（density の 4 倍）に
膨らむため。重みは 0..1 の被覆率であり 8bit で視覚的に十分。レイヤ番号も u8 で足りる
（`TERRAIN_MAX_LAYERS = 16 << 255`）。

**斜度/高度ルールによる自動下地はグリッドに保存しない。** メッシュ生成時に法線と高度から毎回計算する。
理由は、ルールが `layers.json` で後から差し替えられるべきで、グリッドへ焼き込むと差し替えが効かなくなるため。

### 12.3 ルール自動生成と手ペイントの共存

最終的なレイヤ重みは 1 本の式で決まる（T2b では任意レイヤ数版 `layers::blend_rule_and_paint_all`）:

```
result = normalize( lerp(rule_weights, paint_slots を展開した重み, paint_amount) )
```

| `paint_amount` | 挙動 |
|---|---|
| 0（未ペイント） | 完全にルール任せ。地形を掘って斜面ができれば**自動で岩になる**（自動下地が生き続ける） |
| 1（完全ペイント） | ルールを無視して手描き優先。塗った箇所は**その後に地形を変形してもルールに塗り戻されない** |
| 中間 | ブラシ縁のフェード。ペイント領域が地形へ自然に溶ける |

ペイント済みを真偽フラグではなく**連続値**（`paint_amount`）にしたのが要点で、
これによりブラシ縁の段差が出ず、「上書きしない」という要件も同時に満たす。

`rule_weights_all(normal_y, world_y)` は
斜度 = `acos(|n.y|)`（度）と高度 = ワールド Y に対する台形ウィンドウの積 × `priority` を、
**レイヤ定義数ぶんの密ベクトルとして**総和 1 へ正規化したもの（T2 時点は固定 4 要素だったが、
T2b で任意のレイヤ定義数 `layers.len()` に対応した）。全ルールが 0 になる縮退時はレイヤ 0 に
1.0 を寄せる（黒落ち防止）。

### 12.4 GPU への運び方（頂点カラー転用）

レイヤ重み 4 成分は **`Vertex.color` の RGBA** に載せて渡す（`terrain_mesh_build.rs`）。

専用の頂点属性スロットを増やす案もあったが、`Vertex`/`mesh_vertex` レイアウトは
forward / shadow / depth / id / outline / RT を含む**全パイプラインが共有**しており、
1 バイト増やすだけで十数本のパイプラインへ波及する。地形メッシュでは頂点カラーが未使用（常に白）だったため、
これを転用するのが最小の差分で、既存の頂点アップロード経路をそのまま使える。
→ 同時ブレンド可能なレイヤ数が 4 に固定される、というトレードオフ。

**T2 時点はこれが「レイヤ定義数そのものの上限」でもあった**（4 層固定）。T2b では
「1 頂点が同時にブレンドできる数（4・`TERRAIN_BLEND_SLOTS`）」と「レイヤ定義の総数（最大 16・
`TERRAIN_MAX_LAYERS`）」を分離し、後者だけを拡張した。頂点カラーの意味は変わらず「重み」のままで、
「その重みがどのレイヤ番号を指すか」をチャンク単位の uniform（パレット）で運ぶようにしたのが
T2b の核心である（詳細は §12.7）。

マーチングキューブスは辺の両端サンプルから `paint`（T2b では `paint_index`/`paint_weight`）/
`paint_amount` を位置と同じ係数 `t` で線形補間し（`marching_cubes.rs`）、
`terrain_mesh_to_model` がそこへルール重みを合成して頂点カラーへ焼く。

### 12.5 シェーディング（既存 deferred G-Buffer への統合）

新しいライティングは**一切書かない**。G-Buffer 書き込み段だけを差し替える。

- シェーダ: `runtime/src/engine/core/renderer/shaders/terrain_gbuffer_write.wgsl`（entry `fs_terrain_gbuffer`）
- パイプライン: `runtime/src/engine/core/renderer/terrain_gbuffer.rs`（`TerrainGBufferPipelines`）
- 連結順: `["shader_common.wgsl", "shader_static_vertex.wgsl", "terrain_gbuffer_write.wgsl"]`
  （`surface.wgsl` / `surface_gather.wgsl` は連結しない。Surface を経由せず直接 MRT を作る）
- バインドグループ: `group0=camera / 1=model / 2=material`（`MeshPipeline` から借用）＋
  **`group3` = 地形レイヤ定義**。**T2b でレイアウトが変わり**、レイヤテクスチャは個別バインディングではなく
  2D 配列テクスチャ 3 本になった（正確なレイアウトは §12.8 の表を参照）。
  非スキンの地形では group3 が空いているため、ここに差すのが最小差分。
- MRT カラーターゲットは通常の G-Buffer と**完全に同一**（`gbuffer_color_targets()` を共有）。

フラグメントは
①頂点カラー（レイヤ重み）を再正規化 → ②法線から triplanar ブレンド重み `pow(|n|, sharpness)` を求める →
③各レイヤをワールド座標由来 UV で 3 平面サンプル → ④重みで線形合成した
`albedo / normal / metallic / roughness` を G-Buffer へ書く。
合成済みの値を通常レイアウトへ出すため、**ライティング・シャドウ・SSAO・RT 反射・SSGI は既存パスがそのまま効く**。

パイプライン選択は `Material::terrain_layers`（bool）が唯一のスイッチで、
`gbuffer.rs::draw_gbuffer_indirect` が `GpuModel::primitive_terrain_layers()` を見て振り分ける。
このフラグを立てるのは `terrain_mesh_build.rs` だけ。
レイヤ定義の BindGroup が未用意（地形が無いシーン・GPU 未初期化）のときは切り替えず、通常マテリアル描画へ倒す。

### 12.6 ペイントブラシ

- 純粋アルゴリズム: `runtime/src/engine/terrain/paint.rs`（`PaintField` トレイト＋`apply_paint`）。
  密度ブラシ（`brush.rs`）と同じ減衰カーブ（`falloff`）・同じ境界重複同期規約を使う。
- 1 サンプルあたり対象レイヤのスロット重みへ `+= delta` → 正規化（他スロットは相対的に減衰）、
  `paint_amount += delta` を clamp。**T2b の変更**: 対象レイヤが既存の 4 スロットのどれにも
  無い場合は、そのサンプルで**最小重みスロットを対象レイヤへ置換**する（`add_layer_weight`。
  最も影響の薄い層を捨てる規則。詳細は §12.7 のペイント保存形式変更を参照）。
- エンジン統合: `terrain_ops.rs::handle_terrain_paint`（レイキャスト経路）と `handle_terrain_paint_world`（共通経路）。
- undo: 密度ブラシと同じ `stroke_before` を共有する。1 エントリはチャンクスナップショット
  （`density`/`paint_index`/`paint_weight`/`paint_amount`）を丸ごと控えるため、密度編集とペイントが
  混在したストロークも 1 回の undo で完全に戻る。

---

## 12.7 T2b: レイヤ数の拡張とチャンク単位パレット

### なぜ頂点属性を増やさなかったか

T2 の「拡張余地」では『レイヤ数を 4 を超えて増やすには専用の頂点属性が要る』としていたが、
これは採用しなかった。理由は 3 つ:

1. **影響範囲**: `mesh_vertex` レイアウト（72B）は forward / shadow / depth / id / outline / RT を
   含む **22 本のパイプラインが共有**しており、属性を 1 つ増やすだけでも全パイプラインへ波及する。
2. **flat 補間の継ぎ目問題**: レイヤ番号は（重みと違って）補間されては困る値なので
   `@interpolate(flat)` にする必要がある。しかし三角形の 3 頂点でパレット（どのレイヤ番号を
   運ぶか）が食い違うと、どの頂点が「代表」になるかが provoking vertex 依存になり、
   ハードな継ぎ目が三角形単位で出てしまう。
3. **業界標準との整合**: Unreal Engine の Landscape も component（チャンク相当）単位の
   weightmap でレイヤを割り当てており、チャンク単位パレットは特殊な妥協ではなく王道の解法。

代わりに採った設計は「**頂点カラーは重みのみを運び、レイヤ番号はチャンクごとの
パレット `[u32; 4]` として uniform で渡す**」というもの。頂点フォーマットを一切変えずに、
レイヤ定義数を 4 → 16（`TERRAIN_MAX_LAYERS`）へ拡張できる。

### 定数の分離

- `TERRAIN_BLEND_SLOTS = 4`（旧 `TERRAIN_LAYER_COUNT` からリネーム）: 1 頂点／1 チャンクが
  同時にブレンドできるレイヤ数。頂点カラー RGBA の成分数のまま変わらない。
- `TERRAIN_MAX_LAYERS = 16`（新設）: `layers.json` に定義できるレイヤ数の上限。
  GPU 側のレイヤテクスチャ配列の枚数上限と一致。

どちらも `runtime/src/engine/terrain/layers.rs` の定数で、WGSL 側
`terrain_gbuffer_write.wgsl` の同名 `const` と一致必須（`terrain_gbuffer.rs` のテストが
文字列一致で検証する）。

### パレットの決め方（`terrain_mesh_build.rs` の 2 パス）

チャンクのメッシュ化時に、`TerrainMesh` → `Model` 変換（`terrain_mesh_to_model`）が
以下の 2 パスでチャンク固有のパレットを決める:

1. **パス 1**: 全頂点についてルール重み＋手ペイントを合成した密重みベクトル
   （長さ = `layers.len()`）を求めつつ、チャンク全体の合計 `chunk_total[layer]` を累積する。
2. **パレット決定**: `select_top_slots(chunk_total)` で合計上位 4 層を選び、
   `BlendSlots{ index: [u32;4], weight: [f32;4] }` を得る。この `index` がそのチャンクの
   **パレット**になる。
3. **パス 2**: 各頂点の密重みを、決まったパレットの 4 成分へ射影し直し、総和 1 へ再正規化して
   `Vertex.color` に焼く。

戻り値はパレット `[u32; TERRAIN_BLEND_SLOTS]` も含む（呼び出し側が GPU の uniform／
バインドグループ選択に使う）。

### 設計上の限界（必ず把握しておくこと）

**1 チャンク（既定 16m 角）内で同時に使えるレイヤは 4 種まで。** パレットはチャンク全体の
重み合計で決まるため、チャンク内の一部分にしか出ない 5 番目以降の層は、そのチャンクでは
パレットから落ちる（残り 4 層へ再正規化されて描かれるので、意図しない層で塗られたように見える）。
チャンクをまたげば別パレットになるので、レイヤの局所性が高いほど問題になりにくい
（局所的にしか出ない層が 1 チャンク内で 5 種以上重なる構成は避けること）。

---

## 12.8 T2b: レイヤテクスチャ対応

`layers.json` の `base_color_texture` / `normal_texture` / `roughness_texture` を実際に
読み込み、triplanar でサンプルする（T2 では base_color の単色のみだった）。

### texture_2d_array を選んだ理由（bindless を採らなかった理由）

素直な代替は bindless（`binding_array<texture_2d<f32>>`）だが、**wgpu では
`binding_array` と `uniform` を同一バインドグループへ置けない**（実機の BGL 構築でパニックする
既往あり）。地形の `group3` にはレイヤパラメータの `uniform` が同居必須で、かつ `group0`〜`group3`
がすべて埋まっているため uniform を別グループへ逃がす余地が無い。
`texture_2d_array` なら uniform と同居でき、追加の GPU 機能フラグ（`TEXTURE_BINDING_ARRAY`）も
不要で全環境で動く。代償は「配列内の全レイヤが同一解像度・同一フォーマットを強制される」ことで、
これは共通解像度へのリサイズで吸収する（`terrain_layer_textures.rs`）。

- 共通解像度: `TERRAIN_LAYER_TEXTURE_SIZE = 512`（2 のべき乗。ミップ段数計算の前提）。
- ミップ連鎖は **CPU 生成**（`image::imageops::resize` を段ごとに適用）。GPU ミップ生成
  （ブリット連鎖）は専用パイプラインを要するため、レイヤ枚数が高々 16・構築が起動時 1 回
  であることを踏まえ CPU 側の方が総コストが小さい。
- 同一パス文字列は `HashMap<String, RgbaImage>` でデコード結果をキャッシュする
  （同じ PNG を複数レイヤ・複数マップが参照しても 1 回しかデコードしない）。
- **配列の層数は実際の定義レイヤ数に合わせる**（`set.layers.len().clamp(1, TERRAIN_MAX_LAYERS)`）。
  常に 16 層ぶん確保すると、4 層構成でも 512² × RGBA × 16 層 × 3 マップ ≒ **64MB** を占有してしまう
  ため（4 層構成なら約 16MB で済む）。
- sRGB: base_color のみ `Rgba8UnormSrgb`（サンプル時に自動でリニア展開）。normal/roughness は
  数値データなのでリニア `Rgba8Unorm`。
- **テクスチャ未指定レイヤは単色（base_color のみ）へフォールバック** — T2 と同じ見た目になる
  （`base_color.a` が「テクスチャ有無」フラグを兼ねる。§12.9 の表を参照）。
- 法線マップは triplanar の **whiteout ブレンド**（Ben Golus 方式）でワールド空間の法線を合成する
  （3 平面の接空間タンジェント法線を頂点法線の対応成分へ加算する近似。地形は UV 展開を持たず
  正しい接空間が存在しないため、この近似が事実上の標準解）。
- ラフネスはテクスチャの R チャンネル × スカラ係数（`roughness`）。

---

## 12.9 group3 のバインドレイアウト（T2b）

| binding | 種別 | 内容 |
|---|---|---|
| 0 | uniform | `TerrainLayerUniform`（800B: `TerrainLayerParams`×16 + `palette: vec4<u32>` + `params: vec4<f32>`） |
| 1 | sampler | Repeat（u/v/w すべて）・トライリニア（ミップ線形。CPU 生成ミップに対応） |
| 2 | `texture_2d_array<f32>` | base_color（`Rgba8UnormSrgb`） |
| 3 | `texture_2d_array<f32>` | normal（`Rgba8Unorm`） |
| 4 | `texture_2d_array<f32>` | roughness（`Rgba8Unorm`） |

`TerrainLayerParams`（48B・レイヤ 1 枚ぶん）:

| フィールド | 内容 |
|---|---|
| `base_color: vec4<f32>` | rgb = ベースカラー係数（リニア）、a = ベースカラーテクスチャ有無（0/1） |
| `surface: vec4<f32>` | x=metallic, y=roughness, z=triplanar UV スケール, w=detile モードコード |
| `extra: vec4<f32>` | x=法線テクスチャ有無, y=ラフネステクスチャ有無, z=detile 強度, w=予約 |

`palette: [u32; 4]` は「このチャンクが使うレイヤ番号 4 つ」＝頂点カラー成分 → レイヤ番号の対応表
（§12.7 参照）。**パレットごとにバインドグループを `HashMap<[u32;4], wgpu::BindGroup>` でキャッシュ**
する（`TerrainLayerResources::bind_groups`。テクスチャ配列・サンプラ・レイヤパラメータは全チャンクで
共有し、パレット違いのぶんだけ小さな uniform バッファ＋BindGroup を追加で持つ）。未登録パレットは
既定パレット `IDENTITY_PALETTE = [0,1,2,3]` へフォールバックし、描画をパニックさせない。

---

## 12.10 T2b: タイリング解消（detile）

`layers.json` の per-layer `"detile"` フィールドで、レイヤごとに 3 モードから選べる
（既定 `"none"` は従来どおりの単純タイリングで、後方互換のため既定値）。

| モード | 内容 |
|---|---|
| `"none"`（既定） | 単純タイリング。従来（T2）どおりの見た目。 |
| `"stochastic"` | 六角格子の確率的タイリング（Heitz-Neyret 風）。UV を六角（三角）格子へ写し、最近傍 3 セル＋重心座標を求め、セルごとにハッシュ関数でずらした UV で 3 タップサンプルする。重心を `pow(w, 7)`（`DETILE_BLEND_SHARPNESS`）で鋭くしてから再正規化し、3 タップが均等に混ざるぼやけ領域を狭める。`detile_strength` で `none`（無変換）とのブレンド量を制御する。 |
| `"macro"` | 第 2 スケール（元スケール比 `0.13`）のテクスチャ重ね＋大スケール value ノイズによる明度変調。安価（3 タップに収まる）。**タイル格子そのものは消さない**——大局的な単調さを減らす（明るさのムラを付ける）用途に限る。 |

**stochastic モードの既知の限界（必ず把握しておくこと）**: Heitz-Neyret 本来の手法にある
「ヒストグラム保存（inverse CDF によるコントラスト補正）」は実装していない。3 タップを単純な
線形ブレンドしているだけなので、タイル重なり領域でコントラスト（分散）が理論値より低下し、
わずかに眠い（ぼやけた）絵になる。ヒストグラム保存には前処理でガウス化した変換テクスチャと
逆変換 LUT の生成が要り、アセットパイプラインの追加が必要なため T2b の範囲外とした。

### WGSL の一様制御フロー制約への対応（`textureSampleGrad` への統一）

全テクスチャサンプルを `textureSampleGrad`（明示 grad 版）に統一している。
`dpdx(world_pos)` / `dpdy(world_pos)` はフラグメント関数の先頭（一様制御フロー内）で
**1 回だけ**取り、UV スケール倍して各サンプル呼び出しへ配る。理由は 2 つ:

1. WGSL の暗黙 derivative（`textureSample`）は**一様制御フロー内でしか呼べない**。明示
   `textureSampleGrad` なら非一様分岐の中でも合法にサンプルでき、「重み 0 のスロットを
   丸ごとスキップする」「寄与が極小の triplanar 面をスキップする」といった最適化が可能になる
   （最悪 4 スロット×3 平面×3 タップ×3 マップ＝108 タップを、実用域まで削減できる）。
2. 確率的タイリングはタイルごとに UV を不連続にずらす。暗黙 derivative のままだと、
   ずらし目でミップ段差＝シームが必ず出る。元 UV（ずらす前）の grad を使い回すことで、
   タイル境界のシームを消している。

---

## 12.11 layers.json 書式の後方互換（T2b）

- T2b で追加したフィールド（`normal_texture` / `roughness_texture` / `detile` /
  `detile_strength` 等）はすべて `#[serde(default)]`。**T2 時点の 4 層 layers.json
  （テクスチャ・detile 未指定）はそのまま読める**。`detile` 未指定は `"none"` になる。
- 17 層以上を書くと `TERRAIN_MAX_LAYERS`（16）へ切り詰められる（エラーにはしない＝
  データ差し替えでの試行錯誤を止めないため）。

テクスチャ・detile 指定込みのサンプル:

```jsonc
{
  "layers": [
    {
      "name": "grass",
      "base_color": [0.16, 0.38, 0.12],
      "roughness": 0.9,
      "uv_scale": 0.25,
      "base_color_texture": "terrain/textures/grass_basecolor.png",
      "normal_texture": "terrain/textures/grass_normal.png",
      "roughness_texture": "terrain/textures/grass_roughness.png",
      "detile": "stochastic",
      "detile_strength": 1.0,
      "rule": { "slope_min_deg": 0.0, "slope_max_deg": 22.0, "slope_fade_deg": 10.0, "priority": 1.0 }
    },
    {
      "name": "rock",
      "base_color": [0.4, 0.4, 0.42],
      "roughness": 0.7,
      "base_color_texture": "terrain/textures/rock_basecolor.png",
      "detile": "macro",
      "detile_strength": 0.6,
      "rule": { "slope_min_deg": 38.0, "slope_max_deg": 90.0, "slope_fade_deg": 10.0, "priority": 1.2 }
    }
  ]
}
```

---

## 12.12 ペイント保存形式の変更（T2b）

- ペイントは T2 では重みのみ `[u8; 4]` だったが、T2b では**レイヤ番号＋重み**
  （`paint_index: [u8; 4]` ＋ `paint_weight: [u8; 4]`）へ変更した（§12.2 参照）。
- `apply_paint`（`paint.rs`）は対象レイヤが既存の 4 スロットのどれにも無い場合、
  **最小重みスロットを対象レイヤへ置換**する（`add_layer_weight`。そのサンプルで最も
  影響の薄い層を捨てる規則。未使用スロット＝重み 0 は必然的に最小になるため、
  「空きスロットへ入れる」と「既存最小を置換する」を同じ 1 本の規則で扱える）。
- 永続化フォーマットは **tvox v3** になった（§8 参照。書き出しは常に v3、v2/v1 は読み込みのみ
  後方互換）。

---

## 13. 拡張余地（T3 以降）

- **レイヤ数の拡張（>4 = 頂点属性の追加）**: T2b で「頂点フォーマットを変えずにレイヤ定義数を
  16 まで拡張する」ことは解決済み（§12.7）。頂点属性そのものを増やす方向はもはや不要。
- **確率的タイリングのヒストグラム保存**: `"stochastic"` detile はヒストグラム保存
  （inverse CDF）未実装で、重なり領域のコントラストがやや落ちる（§12.10 参照）。前処理
  テクスチャ変換＋LUT のアセットパイプラインが要る。
- **チャンク内 5 層以上の同時ブレンド**: パレットはチャンク全体の重み合計で決まるため、
  1 チャンク内に局所的にしか出ない 5 番目以降の層は落ちる（§12.7「設計上の限界」）。
  必要ならチャンクサイズを小さくする、あるいはパレット選択をチャンク内サブ領域単位に
  分割する等の再設計が要る。
- **レイヤテクスチャの共通解像度リサイズの是非**: 全レイヤを 512² へ強制リサイズしている
  （§12.8）。高解像度が必要なレイヤと低解像度で十分なレイヤが混在する場合、解像度を
  レイヤごとに選べるようにするには texture_2d_array をやめる（＝bindless 化。wgpu の
  binding_array と uniform の非共存制約への対処が要る）か、マップ種別ごとに複数の配列
  テクスチャへ分割する設計変更が要る。
- **VRAM**: レイヤテクスチャは 16 層フル定義で約 64MB（512² × RGBA × 16 層 × 3 マップ、
  ミップ込みで実際はやや増える）。プロジェクトのレイヤ数が少なければ比例して小さい
  （§12.8）。
- **tvox 拡張**: voxel_size/samples をロード後 `TerrainState` へ反映（§8 の限界解消）。密度の i8 量子化（ディスク 1/4）。
- **T3 散布物**: チャンクフォルダ内に木・草アクターを追加（ヒエラルキーは既に対応）。地表サンプルから配置点を算出。
- **T3 LOD/ストリーミング**: 遠距離チャンクの粗メッシュ（セル間引き）・視錐台外チャンクのアンロード。
  再メッシュは既に「影響チャンクのみ差し替え」なので、距離別メッシュキャッシュを足す方向で拡張できる。
- **編集の連続化**: エディタ側でドラッグ中に毎フレーム `TERRAIN_BRUSH` を送る（`dt` はフレーム時間）。undo/redo は
  編集前後のチャンク密度スナップショット（または tvox）を既存 Undo 機構に載せる。
