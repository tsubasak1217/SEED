# Terrain（地形エディタ）設計メモ — T1 ランタイム基盤 ＋ T2/T2b レイヤブレンド ＋ T3 第1段 散布

本書は SEED の地形（terrain）機能の設計正典である。
**T1: ランタイム基盤**（ボクセル SDF ＋ marching cubes による洞窟対応の破壊可能地形）と、
**T2: 地形マテリアルのレイヤブレンド**（スプラット × triplanar・斜度/高度ルールによる自動下地・
ペイントブラシによる手修正）と、
**T2b: レイヤ拡張・テクスチャ・タイリング解消**（レイヤ定義を最大 16 層へ拡張しつつ
チャンク単位パレットで同時ブレンド 4 層の頂点フォーマットを維持・レイヤテクスチャの
2D 配列対応・3 種のタイリング解消モード）と、
**T3 第1段: 地形プロップ散布**（斜度/高度/レイヤ重みによる草・木の自動散布＋ブラシ手描き・
手続き生成 GPU インスタンシング草・地形編集後の再接地）をカバーする（§15）。

> スコープ外（未実装）: `kind=model` プロップの実描画（データは生成・保存済み。§15.9）、
> 散布物との接触インタラクション、LOD／ストリーミング。末尾「拡張余地」参照。

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
- **ワインディング**: エンジン規約（左手系＋`FrontFace::Ccw`／glTF ローダの Z 反転により
  「代数的外積 `cross(b-a,c-a)` は外向き法線と**逆向き**」が表面の条件）に合わせて出力。
  必要なら 2 頂点を入れ替えて反転する。テスト `sphere_winding_matches_engine_front_face_convention` が固定。
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
- **背面カリング（T1 の両面回避策は撤去済み）**: エンジンは `FrontFace::Ccw`＋マテリアル別カリング（既定 Back）。
  T1 では marching cubes の巻き順が規約と逆だったため `cull_face=None`（両面）で回避していたが、
  これは地表フラグメントを全面「裏面」判定にし、`terrain_gbuffer_write.wgsl` の `front_facing` 反転
  （`facing_sign = -1`）で**法線を丸ごと反転**させていた。結果、ライト方向に対して陰影が逆転し
  （上向きライトで地形が明るくなる）、シャドウの法線オフセットバイアスも逆に効いて斑状のアクネが出ていた。
  → `push_triangle` の巻き順をエンジン規約に合わせ、地形マテリアルは `cull_face=Back` / `double_sided=false` に戻した。
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
| `TERRAIN_INIT` | なし（旧形式・現在の設定で初期化） | `TERRAIN_INIT_OK` |
| `TERRAIN_INIT:{chunks_x},{chunks_z},{chunk_cells},{voxel_size}` | u32×3 + f32。チャンク構成を反映してから初期化（§9.6） | `TERRAIN_INIT_OK` |
| `TERRAIN_ADD_CHUNKS:{min_x},{min_z},{max_x},{max_z}` | i32×4（チャンク座標範囲・**両端含む**）。既存チャンクは温存（§9.7） | `TERRAIN_ADD_CHUNKS_OK:{追加数},{再メッシュした隣接数}`／`TERRAIN_ADD_CHUNKS_ERROR:{msg}` |
| `TERRAIN_BRUSH:{op},{screen_x},{screen_y},{radius},{strength}` | `op`:u32（0=Add,1=Subtract,2=Smooth,3=Flatten）／他 f32。`screen_x/y` はビューポート左上原点のピクセル座標 | ヒット時 `TERRAIN_BRUSH_OK:{hx},{hy},{hz}`（ワールドヒット点）／非ヒット `TERRAIN_BRUSH_MISS` |
| `TERRAIN_PAINT:{layer},{screen_x},{screen_y},{radius},{strength}` | `layer`:u32（塗る対象レイヤ番号・0 起点／`layers.json` の並び順）／他 f32 | ヒット時 `TERRAIN_PAINT_OK:{layer},{hx},{hy},{hz}`／非ヒット `TERRAIN_PAINT_MISS` |
| `TERRAIN_SAVE` | なし | `TERRAIN_SAVE_OK:{count}`（保存チャンク数）／`TERRAIN_SAVE_ERROR:{msg}` |
| `TERRAIN_BRUSH_PREVIEW:{screen_x},{screen_y},{radius},{strength}` | 全 f32。ホバー中（非押下）に送る。`strength` はプレビュー球の色（強度連動）にのみ使う | 応答なし（高頻度・ホバー用）。レイマーチのヒット点にブラシ半径のワイヤスフィアを描く |
| `TERRAIN_BRUSH_PREVIEW_OFF` | なし | 応答なし。プレビュー（ワイヤスフィア）を非表示にする |
| `TERRAIN_UNDO` | なし | 応答なし。地形専用 undo スタックを 1 ストローク分戻す（下記 §9.1） |
| `TERRAIN_REDO` | なし | 応答なし。地形専用 undo を 1 ストローク分やり直す |
| `TERRAIN_STROKE_END` | なし | 応答なし。進行中ストロークを 1 undo エントリとして確定する（左ボタン解放時に送る） |
| `TERRAIN_HEIGHTMAP:{path},{height_scale}` | 旧形式。`path`=画像の実ファイル絶対パス（png/jpg）、`height_scale`:f32（最大高さ m）。`path` にカンマが含まれても壊れないよう **最後のカンマで path / height_scale を分割** する | `TERRAIN_HEIGHTMAP_OK:{ms}`（処理ミリ秒）／`TERRAIN_HEIGHTMAP_ERROR:{msg}` |
| `TERRAIN_HEIGHTMAP:{chunks_x},{chunks_z},{chunk_cells},{voxel_size},{height_scale},{path}` | 新形式。**path を末尾に置く**ことで前 5 個の数値フィールドを固定個数（`splitn(6, ',')`）で切り出せる（§9.6） | 同上 |
| `TERRAIN_RELOAD_LAYERS` | なし | `TERRAIN_RELOAD_LAYERS_OK:{count}`（再メッシュしたチャンク数）。`layers.json` を読み直し、レイヤテクスチャ配列と全チャンクを作り直す（下記 §9.5） |

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

### 9.6 チャンク構成の設定（TERRAIN_INIT / TERRAIN_HEIGHTMAP のパラメータ）

**背景**：`voxel_size`(0.5m) / `chunk_cells`(32) / 初期平地の枚数(4×4) はすべて `settings.rs` の
定数固定で、UI から変えられなかった。これを IPC 引数で渡せるようにした。

**ワイヤ形式**（下位互換のため旧形式もそのまま受け付ける）:

```
TERRAIN_INIT                                                          ← 旧形式（現在の設定で初期化）
TERRAIN_INIT:{chunks_x},{chunks_z},{chunk_cells},{voxel_size}         ← 新形式
TERRAIN_HEIGHTMAP:{path},{height_scale}                               ← 旧形式
TERRAIN_HEIGHTMAP:{chunks_x},{chunks_z},{chunk_cells},{voxel_size},{height_scale},{path}
```

- **ハイトマップの引数順が新旧で逆になっている理由**：`path` は Windows パスでカンマを含みうるため、
  可変長フィールドは端に置くしかない。旧形式は「path が先頭・右端のカンマで分割」、新形式は
  「**path が末尾**・`splitn(6, ',')` で前 5 個を固定個数で切り出す」。判別はパーサが
  「6 フィールドあり、前 5 個がすべて数値として読める」かで行い、読めなければ旧形式へフォールバックする
  （旧形式の path は先頭フィールドが `C:\…` のように数値にならないため衝突しない）。
- **値域の検証は 1 箇所に集約**：パース層は「文字列 → 型」までしかやらない。上下限のクランプは
  `TerrainSettings::apply_chunk_config()` が一手に担う（`settings.rs`）。

| 項目 | 下限 | 上限 | 既定 |
|---|---|---|---|
| `chunks_x` / `chunks_z` | 1 | 32 | 4 |
| `chunk_cells` | 4 | 64 | 32 |
| `voxel_size` (m) | 0.05 | 8.0 | 0.5 |
| チャンク総数（`TERRAIN_ADD_CHUNKS` の安全弁） | — | 4096 | — |

- `chunk_cells` の上限が 64 なのは、サンプル数が `(cells+1)³` で効くため。
  cells=64 → 65³ ≒ 275k サンプル ⇒ 約 3.6 MB/チャンク。cells=128 では約 28 MB/チャンクとなり実用外。
- `density_clamp` は「1 チャンク分の広がり」の派生値なので `apply_chunk_config` 内で再計算する。
  これを怠ると旧構成の 16.0 が残り、大きなチャンクでブラシ編集の密度が頭打ちになる。
- `build_terrain_with` は状態リセット時に `TerrainState::default()` を代入するが、**settings だけは
  退避して復元する**。さもないと IPC で渡された構成が即座に既定値へ潰される。

#### chunk_cells / voxel_size 変更の安全策（採った設計と理由）

分割数・ボクセルサイズを変えると 1 チャンクのサンプル数と実寸が変わり、既存の密度配列とも
保存済み `.tvox` とも**ビット互換でなくなる**。採った設計は次の 2 段構え。

1. **構成変更は「地形を丸ごと作り直す経路」からのみ許す**。
   `apply_chunk_config` を呼ぶのは `TERRAIN_INIT` と `TERRAIN_HEIGHTMAP` だけで、どちらも既存地形を
   破棄して敷き直す。編集中の地形へ後から分割数だけを差し込む API は**作らない**
   （`TERRAIN_ADD_CHUNKS` は構成を一切変更しない）。再サンプリングによる移行は採らなかった
   ── 密度の再サンプルは表面を必ず鈍らせ、手ペイントのスプラットは補間の意味が定義できないため、
   「静かに劣化する」より「作り直しを明示させる」方が壊れ方が分かりやすい。
2. **読み込み時は `.tvox` ヘッダを正とする**。`tvox::read_header()`（本体を読まずヘッダ 28 バイトだけ
   読む軽量版）で `samples_per_axis` / `voxel_size` を取り、
   `rebuild_terrain_after_load` は **最初に読めた 1 枚の構成を地形全体の構成として settings へ採用**する。
   2 枚目以降でヘッダが食い違うチャンク（分割数変更後に古い `.tvox` が残っている状態）は
   警告ログを出して**読み飛ばす**（読み込むと配列長が食い違い、描画・編集が破綻するため）。

エディタ側は設定ウィンドウ「地形」タブに常時警告を出し、変更後は初期化し直すよう促す（§14.5）。

### 9.7 チャンク追加（TERRAIN_ADD_CHUNKS）

編集中の地形を保ったまま、指定したチャンク座標範囲へチャンクを増やす。

```
TERRAIN_ADD_CHUNKS:{min_x},{min_z},{max_x},{max_z}   ← i32×4・両端含む・反転指定も正規化される
→ TERRAIN_ADD_CHUNKS_OK:{追加数},{再メッシュした隣接数}
→ TERRAIN_ADD_CHUNKS_ERROR:{msg}   （"terrain not initialized" / "chunk limit exceeded …" 等）
```

- 縦方向（Y）の段数は現在の設定（`ground_chunk_y_min..=ground_chunk_y_max`）に従う。
- **既存チャンクの温存**：追加対象の列挙は純粋関数 `collect_new_chunk_coords()` に切り出してあり、
  既に存在する座標を結果に含めない。よって「上書きしようがない」ことが戻り値だけで保証される
  （ユニットテスト `collect_new_chunks_excludes_existing`）。範囲がすべて既存なら `OK:0,0` を返す。
- 地形が未初期化のときはエラー（構成もツリーも未確定な状態での「追加」は意味が定まらないため、
  先に `TERRAIN_INIT` させる）。

#### 継ぎ目の処理（境界が不連続にならない仕組み）

グローバルサンプル座標の規約上、隣り合うチャンクは接する面のサンプルを**重複所有**する（§5）。
ブラシ編集は `write_global_impl` が全所有チャンクへ同じ値を書くのでこの重複は常に一致しているが、
新しく作ったチャンクは平地の初期値を持つため、隣が編集済みだと**同じ座標のサンプルが 2 つの
異なる値を持つ**ことになり、MC が両側で別々の等値面を出して継ぎ目に穴・段差が生じる。

対処は 3 段階：

1. 新規チャンクを `from_ground_plane` で作る。
2. `sync_new_chunk_boundary()` で、**既存側の値を正として**新規チャンクの境界サンプル
   （ローカル添字が 0 または `cells` のもの＝6 面）へ引き写す。密度だけでなく
   **手ペイントスロット（レイヤ番号＋重み）とペイント量も**引き写す（色の継ぎ目も出さないため）。
   走査は境界面に限定し、内部サンプルは触らない（既存の編集内容が新しい地面へ波及しないため）。
   この関数は **新規チャンクを `chunks` へ insert する前に**呼ぶ必要がある
   （insert 済みだと新規チャンク自身が主所有者として見つかり、自分の初期値で自分を上書きするだけになる）。
3. 新規チャンクの **26 近傍にある既存チャンクを再メッシュ化**する。既存チャンクのサンプル自体は
   変わらないが、メッシュ生成時に読む「外側 1 サンプル」が地形外（＝AIR 相当の `density_clamp`）から
   実際の密度へ変わるため、境界の三角形と法線が変化する。これを怠ると継ぎ目に隙間・陰影の段差が残る。

回帰テストは `terrain_ops.rs` の `mod tests` に置いた（`new_chunk_boundary_matches_existing_neighbor` /
`new_chunk_keeps_ground_plane_where_no_neighbor` / `isolated_new_chunk_is_untouched`）。
App も wgpu も要らない純粋関数へ切り出してあるため GPU 無しで検証できる。

- 追加チャンクは `dirty` に入るので、`TERRAIN_SAVE` で `.tvox` が書き出される。
- undo スタックは触らない（既存チャンクを座標で参照しているだけで、追加によって無効化されないため）。

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

### 9.5 レイヤ定義の再読込（TERRAIN_RELOAD_LAYERS）

エディタの**地形設定ウィンドウ**（§14）が `layers.json` を保存した直後に送る。ランタイム側の処理は
`terrain_ops.rs::handle_terrain_reload_layers`:

1. `ensure_terrain_layers()` で `layers.json`（または `SEED_TERRAIN_LAYERS` が指す差し替えファイル）を読み直し、
   レイヤテクスチャ配列（base_color / normal / roughness の 3 本）を作り直す。
2. 既存の全チャンクを `remesh_chunks()` で再メッシュ化する。ルール（斜度・高度ウィンドウ）が変わると
   **密度＝形状が同じでも頂点のレイヤ重みが変わる**ため、頂点バッファの作り直しが必要になる。
3. `TERRAIN_RELOAD_LAYERS_OK:{count}` を返す。地形未生成（チャンク 0 個）なら定義の読み直しだけ行う。

**VRAM 2 倍スパイク回避**: レイヤテクスチャ配列は全レイヤぶんの画像を 3 本抱えるため、旧リソースを保持したまま
新しい配列を確保すると瞬間的に 2 倍の VRAM を要求する。`ensure_terrain_layers()` は
`remesh_chunks` / `slot_ops` と同じ **「旧 drop → `device.poll(Wait)` で解放確定 → 新規確保」** の順序に従う
（初回呼び出しでは旧リソースが無いため実質ノーオペ）。

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
- **T3 第1段（実装済み・§15）**: 斜度/高度/レイヤ重みルールによる草・木の自動散布＋ブラシ手描き、
  `.tscatter` 永続化、草のプロシージャル GPU インスタンシング描画、地形編集後の再接地。
- **T3 第2段（未実装・§15.9）**: `kind=model` プロップ（木など）の実描画。散布物との接触インタラクション
  （プレイヤーで草をなぎ倒す等）。
- **T3 LOD/ストリーミング**: 遠距離チャンクの粗メッシュ（セル間引き）・視錐台外チャンクのアンロード。
  再メッシュは既に「影響チャンクのみ差し替え」なので、距離別メッシュキャッシュを足す方向で拡張できる。
- **編集の連続化**: エディタ側でドラッグ中に毎フレーム `TERRAIN_BRUSH` を送る（`dt` はフレーム時間）。undo/redo は
  編集前後のチャンク密度スナップショット（または tvox）を既存 Undo 機構に載せる。

---

## 14. 地形設定ウィンドウ（エディタ）

terrain ツールバー右端の **「⚙ 設定」** ボタンで開く**独立ウィンドウ**（`editor/src/Terrain/TerrainSettingsWindow.xaml`）。
ドッキングパネルにしていないのは、レイヤ編集が「一覧＋多数のパラメータ」で横幅を要求する一方、
シーンビューを見ながら常時開いておく種類の UI ではないため（ドッキングするとビューポートを圧迫し続ける）。

- **非モーダル**。開いたままシーンビューでブラシ操作を続けられる。
- **多重起動しない**。既に開いていれば新規生成せず前面化する（`MainWindow._terrainSettingsWindow` で保持）。
- **閉じても terrain モードは維持**される（モード状態とウィンドウの寿命は独立）。

### 14.1 タブ構成（将来拡張の器）

| タブ | 内容 |
|---|---|
| 地形 | チャンク構成（枚数・分割数・ボクセルサイズ）＋チャンク追加（実装済み・§14.5） |
| レイヤ | `assets/terrain/layers.json` の編集（実装済み） |
| ブラシ | ブラシテクスチャ（スタンプ画像）・プリセット等を置く予定の空タブ |

タブ構成にしてあるのは、今後の地形設定（ブラシテクスチャ・T3 散布設定など）を
**タブを 1 枚足すだけ**で受けられるようにするため。ウィンドウ構造そのものを組み替えずに済む。

### 14.2 レイヤ編集 UI

- **レイヤ一覧**（左）: 並び順がそのままレイヤ番号（0, 1, 2 …）。`▲`/`▼` で並べ替え、`追加`/`複製`/`削除`。
  レイヤ数の上限は `TerrainLayerDefaults.MaxLayers`（= Rust の `TERRAIN_MAX_LAYERS`）。最低 1 枚は残す。
  削除は以降のレイヤ番号を繰り上げ、既存の手ペイント割り当てがずれるため確認ダイアログを出す。
- **プロパティ**（右）: 名前／ベースカラー（カラーピッカー）／roughness／metallic／UV スケール／
  テクスチャ 3 種（base_color・normal・roughness）／detile モードと強度／自動ペイント条件
  （斜度 min・max・フェード、高度 min・max・フェード、優先度）。
  斜度ウィンドウは 0〜90 度の帯として簡易プレビューされる（ランタイム `layers.rs::window()` と同じ台形＋smoothstep を再現。
  **描画の正典はランタイム側**で、こちらは入力補助）。
- **テクスチャ指定**: 参照ボタンとドラッグ＆ドロップの双方に対応（インスペクタ共通の `FileRefBuilder` を再利用）。
  選んだ絶対パスは **assets ルート基準の相対パス**（区切りは `/`）へ変換して保存する。右クリックで指定解除。

### 14.3 layers.json の保存 — 未知フィールドを壊さない差分更新

`layers.json` は**ランタイムが正典**であり、エディタが知らないフィールドが今後増えうる
（現ファイル冒頭の `_comment`、将来のフィールドなど）。C# の型へ deserialize → serialize すると
型に無いフィールドが黙って消えるため、`TerrainLayersDocument`（`editor/src/Terrain/TerrainLayersDocument.cs`）は
**元 JSON を `JsonNode` ツリーとして保持し、編集したキーだけを上書きする差分更新**を行う。

- ルート直下の未知キー（`_comment` 等）はそのまま残る。
- 各レイヤも元の `JsonObject` を保持するため、レイヤ内の未知キーも失われない。
- **既定値と同じ値は書き出さない**（Rust 側は全フィールドが `#[serde(default)]` なので省略＝既定値）。
  ただし「元ファイルに存在したキー」は値が既定値でも書き続ける（保存性優先）。
  既定値テーブルは `TerrainLayerDefaults.cs` にあり、**`layers.rs` の `DEFAULT_*` 定数と一致させる義務がある**
  （片方だけ変えると、エディタが「既定値だから省略」と判断したフィールドをランタイムが別の値で解釈する）。

### 14.4 即時反映とレイヤ選択コンボの動的化

- 「保存して適用」で `layers.json` を書き、`TERRAIN_RELOAD_LAYERS`（§9.5）を送る。ウィンドウは開いたままなので
  続けて調整できる。
- ツールバーの**レイヤ選択コンボは `layers.json` から動的生成**する（`MainWindow.RefreshTerrainLayerCombo`）。
  レイヤ総数は自由なので XAML に固定項目は持たない。設定ウィンドウでの保存直後にも呼ばれ、増減・並べ替えが即反映される。
  選択中のレイヤ番号は可能な限り維持し、範囲外になった場合は先頭へ戻す。

### 14.5 地形タブ — チャンク構成とチャンク追加

**永続化**：`assets/terrain/chunk_config.json`（`editor/src/Terrain/TerrainChunkConfigDocument.cs`）。
`layers.json` と違いランタイムは**このファイルを読まない**。値はあくまで IPC 引数として渡される
（`TERRAIN_INIT:` / `TERRAIN_HEIGHTMAP:` の新形式。§9.6）。ランタイム側の正典は
実行時の `TerrainSettings` と、保存済み地形については `.tvox` ヘッダである。

| セクション | 項目 |
|---|---|
| チャンク構成 | チャンク数 X／チャンク数 Z／チャンク分割数（`chunk_cells`）／ボクセルサイズ (m) |
| 計算値（読み取り専用） | チャンク 1 辺の実寸（= `voxel_size × chunk_cells`）／地形全体のフットプリント／総チャンク数 |
| チャンクを追加 | `min_x` / `min_z` / `max_x` / `max_z` ＋「追加」ボタン（`TERRAIN_ADD_CHUNKS` を送る） |

- 上部に**常時警告**を表示する:
  > 「チャンク分割数・ボクセルサイズの変更は、『地形を初期化』または『ハイトマップ読込』で地形を
  > 作り直したときにのみ反映されます。既存の地形（保存済み .tvox ファイル）とはボクセル配置が
  > 非互換になるため、変更後は必ず地形を初期化し直してください。」
- 「保存して適用」は `chunk_config.json` を保存するだけで、**`TERRAIN_INIT` は送らない**
  （破壊的操作なのでツールバーの「地形を初期化」ボタン経由に限定する。そちらは確認ダイアログに
  これから作られる構成を明記する）。
- ツールバーの「地形を初期化」「ハイトマップ読込」は `chunk_config.json` を読んで**新形式**で送る。
  ハイトマップは path が末尾になったため、パス中のカンマ問題が構造的に解消した。

---

## 15. Terrain T3（散布 / Scatter）第1段

草・木などの「地形の上に載る小物」を、斜度/高度/レイヤ重みのルールで自動散布し、
ブラシで手描き修正できるようにする機能。**草は手続き生成 GPU インスタンシングで実際に描画される
（G-Buffer 統合済み）が、`kind=model` のプロップ（木など）は現時点ではデータの生成・保存までで、
実描画は第2段に持ち越し**（§15.9）。

### 15.1 モジュール構成

密度・レイヤブレンドと同じく「純粋データ層／エンジン統合層／レンダリング層」の 3 段構成。

| ファイル | 責務 |
|---|---|
| `runtime/src/engine/terrain/scatter/props.rs` | プロップ定義（`TerrainProp`/`GrassParams`/`WindParams`/`ScatterParams`/`ScatterRule`）と斜度/高度/レイヤ条件の確率評価。ECS・GPU・ファイル IO 非依存 |
| `runtime/src/engine/terrain/scatter/tscatter.rs` | 散布インスタンス列のバージョン付きバイナリ直列化（純 bytes、ファイル IO はしない） |
| `runtime/src/engine/terrain/scatter/generate.rs` | 決定的なルール自動散布・ブラシ散布・地形編集後の再接地アルゴリズム（`ScatterField` トレイト越しに密度・レイヤ重みへアクセス） |
| `runtime/src/engine/terrain/scatter/tests_scatter.rs` | 散布レイヤ専用のユニットテスト |
| `runtime/src/engine/core/app_base/app/terrain_scatter_ops.rs` | エンジン統合層。`TerrainScatterField`（`ScatterField` の実装）・IPC ハンドラ・`.tscatter` 保存/読込・再接地の呼び出し・草 GPU バッファ再構築 |
| `runtime/src/engine/core/renderer/grass_gbuffer.rs` + `shaders/grass_gbuffer.wgsl` | 草のプロシージャル GPU インスタンシング描画パイプライン |

密度・レイヤブレンドと責務を分けた理由は `terrain_ops.rs` が既に密度編集・ペイント・メッシュ化で
3000 行を超え飽和していたため（単一責任原則）。散布は「地形の上に載る別レイヤ」であり、
密度グリッドとは更新頻度も永続化ファイルも独立している。

### 15.2 `.tscatter` フォーマット（v1・リトルエンディアン）

1 チャンク分の散布インスタンス列を、`.tvox` と同じ流儀のバージョン付きバイナリで永続化する。
`.tvox`（密度・約 144KB/チャンク）と別ファイルにしたのは、散布はブラシで頻繁に描き替えるが密度は
変わらない・逆に地形を彫っても散布は再接地するだけで済む、というように更新頻度が独立しているため
（同一ファイルにすると毎回 MB 級の密度グリッドを書き戻すことになる）。

**ヘッダ（24 バイト）**:

| オフセット | 型 | 内容 |
|---|---|---|
| 0 | u8[4] | マジック `"TSCT"` |
| 4 | u32 | バージョン（現在 `1`） |
| 8 | i32 | チャンク座標 x |
| 12 | i32 | チャンク座標 y |
| 16 | i32 | チャンク座標 z |
| 20 | u32 | instance_count |

**インスタンス 1 件（40 バイト）**:

| オフセット | 型 | 内容 |
|---|---|---|
| +0 | f32×3 | pos（接地点のワールド座標） |
| +12 | f32×3 | normal（接地面の外向き単位法線） |
| +24 | f32 | yaw（Y 軸まわり回転・ラジアン） |
| +28 | f32 | scale（スケール倍率。1.0 = プロップ定義そのまま） |
| +32 | u32 | prop_id（props.json 内の **添字**。ID 文字列ではない） |
| +36 | u32 | seed（インスタンス固有の乱数シード。風の位相・色ゆらぎに使う） |

`read_chunk` は本体長が `instance_count × 40` バイトちょうどでなければ `CountMismatch` を返す
（余分な末尾バイトも許さない＝壊れたファイルを黙って読まない）。

**パス規約**: `assets://terrain/<scene>/chunk_X_Y_Z.tscatter`（`.tvox` の隣に置く）。
ロード時は `TerrainChunkComponent::tvox_path` の拡張子を `.tscatter` へ差し替えて導出する
（`tscatter_path_from_tvox`。シーン名からパスを組み立て直すと規則が 2 か所に分かれて壊れやすいため）。
**ファイルが無いのはエラーではない**（散布機能より前に保存されたシーンには `.tscatter` が存在しない。
欠落を空配列として扱うことで旧シーンもそのまま開ける）。
**保存時、インスタンス 0 本のチャンクは既存ファイルを削除する**（0 本 = ファイルを消す、を保存の
不変条件とする。残すと次回ロード時に「消したはずの草が復活する」——もっとも分かりにくい部類の
バグになる）。

`prop_id` が props.json の添字であることの帰結として、props.json のプロップを並び替えると既存の
散布データの指し先がずれる。これは ID 表を併記する／並び替えを禁止するといった対策をエンジン層が
面倒を見る前提の設計判断であり、`.tscatter` 自体は添字をそのまま運ぶだけである（現時点では対策は未実装）。

### 15.3 プロップ定義（データドリブン）— `assets/terrain/props.json`

散布するプロップの種類・見た目・散布ルールはすべてアセットで定義する（コード変更不要）。
読み込み元は環境変数 **`SEED_TERRAIN_PROPS`** > `assets://terrain/props.json` の順で解決する
（`layers.json` の `ensure_terrain_layers` と完全に同じ流儀）。読み込み失敗（無し／壊れている）時は
草 1 種＋木 1 種の既定セットへフォールバックする。定義数の上限は **`TERRAIN_MAX_PROPS = 64`**
（超過分は先頭 64 種へ切り詰め・エラーにはしない＝データ差し替えの試行錯誤を止めないため）。
省略フィールドは serde の `#[serde(default)]` で既定値が入る（`layers.json` と同じ後方互換方針）。

```jsonc
{
  "props": [
    {
      "id": "grass_field",        // 一意なプロップ ID（散布データ・スクリプトから参照する安定キー）
      "name": "Grass Field",      // エディタ一覧の表示名
      "kind": "grass",            // "grass"（手続き生成の草） / "model"（外部モデルアセット）
      "model_path": null,         // kind=model のときのみ意味を持つ。アセット相対パス
      "grass": { /* kind=grass のときのみ意味を持つ。表参照 */ },
      "wind":  { /* 風による揺れ。表参照 */ },
      "scatter": { /* 散布密度・姿勢のばらつき。表参照 */ },
      "rule": { /* 斜度/高度/レイヤ重みによる自動散布条件。表参照 */ }
    }
  ]
}
```

`kind` に関わらず全フィールドが常に存在する（`TerrainProp` は kind を切り替えても未使用の
パラメータが消えない設計。エディタで grass ⇔ model を行き来しても以前の設定が復帰する）。

#### `grass`（`GrassParams`・手続き生成される草 1 本の形状）

| フィールド | 既定値 | 内容 |
|---|---|---|
| `width` | `0.055` m | 根元の葉幅。先端に向かって 0 へ収束する前提 |
| `height` | `0.38` m | 葉の高さ |
| `height_variance` | `0.35` | 高さのランダム変動率（0..1）。個体ごとに height を ±この割合で振る |
| `segments` | `4` | 縦分割数（曲げの滑らかさ）。`clamped_segments()` 経由で `1..=GRASS_MAX_SEGMENTS`（=8）へクランプされる |
| `cross_planes` | `true` | true=十字 2 枚、false=1 枚板 |
| `bend` | `0.25` | 静止時の自然な垂れ（0=直立）。風とは別の常時掛かる曲げ量 |
| `color_bottom` | `[0.08, 0.20, 0.05]` | 根元色（リニア RGB） |
| `color_top` | `[0.34, 0.58, 0.16]` | 先端色（リニア RGB）。根元色との縦グラデーションになる |
| `roughness` | `0.85` | ラフネス |
| `tip_alpha_cutoff` | `0.0` | 先端のアルファカットアウト閾値（0=無効） |
| `normal_up_blend` | `0.7` | 陰影用法線を地表法線（株の up）へ寄せる割合（0..1）。0 = 真の幾何法線、1 = 完全に地表法線。板ポリの葉を地表法線側へ寄せて陰影を整える（normal flattening）。理由と実装位置は §15.5 参照 |

#### `wind`（`WindParams`・風による揺れ）

「基本振動（速く細かい）」＋「突風（遅く大きい）」の 2 成分で構成する（単一の正弦波だけだと
草原全体が同位相で揺れて機械的に見えるため）。

| フィールド | 既定値 | 内容 |
|---|---|---|
| `strength` | `0.16` | 横揺れ幅（草の高さに対する比率）。0 で風の影響なし |
| `speed` | `1.4` | 基本振動の時間速度 |
| `frequency` | `0.35` | 基本振動の空間周波数（ワールド 1m あたりの位相進み） |
| `gust_strength` | `0.35` | 突風成分の強さ（strength に対する追加分の比率） |
| `gust_speed` | `0.25` | 突風成分の進行速度 |

#### `scatter`（`ScatterParams`・散布の姿勢・密度）

| フィールド | 既定値 | 内容 |
|---|---|---|
| `density` | `4.0` /m² | 1 m² あたりの候補点数。**候補であって確定本数ではない**（`rule` の確率判定を通ったものだけがインスタンス化される） |
| `scale_min` | `0.85` | スケール下限（1.0=定義そのままのサイズ） |
| `scale_max` | `1.25` | スケール上限 |
| `align_to_normal` | `true` | 地表法線に沿わせるか（false なら常に真上を向く）。草は true（斜面で寝る）、木は false（斜面でも垂直に立つ）が自然 |
| `tilt_max_deg` | `8.0`° | ランダム傾き上限。0 だと全個体が同じ姿勢で人工的に見える |
| `random_yaw` | `true` | Y 軸まわりのランダム回転を掛けるか |

#### `rule`（`ScatterRule`・斜度/高度/レイヤ重みによる自動散布条件）

斜度ウィンドウ（度）×高度ウィンドウ（ワールド Y メートル）×全レイヤ条件の成立度、の積が
「その地点に生える確率」になる。ウィンドウの縁は `*_fade` 幅で smoothstep 補間される
（`layers.rs` の `window()`/`smoothstep()` を**そのまま再利用**——レイヤの塗り分け境界と草の
生え際が同じ式で決まるため、片方だけ直してずれる事故を構造的に防ぐ）。

| フィールド | 既定値 | 内容 |
|---|---|---|
| `slope_min_deg` | `0.0`° | 斜度ウィンドウ下限（0=水平面） |
| `slope_max_deg` | `90.0`° | 斜度ウィンドウ上限（90=垂直面） |
| `slope_fade_deg` | `8.0`° | 斜度ウィンドウ両端のぼかし幅 |
| `height_min` | `-1.0e6` m | 高度ウィンドウ下限（実質「制限なし」） |
| `height_max` | `1.0e6` m | 高度ウィンドウ上限（実質「制限なし」） |
| `height_fade` | `2.0` m | 高度ウィンドウ両端のぼかし幅 |
| `layer_conditions` | `[]` | `LayerCondition{ layer: String, min_weight: f32 }` の配列。空なら無条件。複数指定は**すべて**満たす必要がある（AND・成立度は積で合成） |
| `threshold` | `0.02` | 最終確率がこの値未満なら生やさない（裾の切り落とし。0 だと確率 0.001 のような「ごく稀に 1 本だけ生える」個体が広範囲に散らばりレイヤ境界がぼやける） |

`LayerCondition.min_weight` の既定値は `0.5`。レイヤ名は**番号ではなく名前**で指定する
（`layers.json` の並び替えで散布定義が壊れないようにするため）。未知のレイヤ名は重み 0 として扱われ
条件は不成立になる（タイポで草が全く生えない、という分かりやすい失敗にするための設計判断——逆に
1.0 を返すと「条件を書いたのに全面に生える」ほうが発見しにくい）。条件境界も `LAYER_CONDITION_FADE
= 0.15`（重み単位）で smoothstep フェードする（境界で草が直線状に切れないように）。

出荷版 `assets/terrain/props.json` は `grass_field`（芝地レイヤに乗る密な草）・`grass_dry`
（dirt レイヤに乗る疎らな枯草）・`tree_pine`（`kind=model`・`model_path=null`。樹木 glb 未整備のため
プレースホルダ）の 3 種。テスト `shipped_props_json_parses` / `shipped_props_reference_existing_layers`
（`terrain_scatter_ops.rs`）が構文とレイヤ名参照の妥当性を CI で固定する。

### 15.4 レンダリング — プロシージャル草の GPU インスタンシング

草は**頂点バッファもインデックスバッファも持たない**。1 株ぶんの固定頂点数
（`GRASS_MAX_VERTS_PER_BLADE = GRASS_MAX_SEGMENTS(8) × GRASS_VERTS_PER_SEGMENT(6) × GRASS_MAX_PLANES(2)
= 96`）を `@builtin(vertex_index)` と `@builtin(instance_index)` だけから手続き的に生成し、
`rp.draw(0..96, 0..count)` の 1 コールで描く。実際の分割数が最大未満のインスタンスでは、
余った頂点をシェーダ側で面積 0 の縮退三角形に潰す（`grass_degenerate_vertex`）。

- **描画先は既存の deferred G-Buffer**（`GrassGBufferPipeline`。`gbuffer_color_targets()` を通常の
  地形/モデル描画と完全共有）。新しいライティングコードは一切書かない。合成済みの
  albedo/normal/metallic/roughness を通常レイアウトへ出すため、**シャドウ・GI・AO・RT 反射は
  既存パスがそのまま効く**。
- **インスタンスデータ**: `GrassInstance`（storage buffer, 48 バイト/株: pos+yaw / normal+scale の
  vec4 パッキング + seed + pad）と `GrassUniform`（uniform, 80 バイト: プロップ 1 種ぶんの見た目・
  風パラメータ）の 2 本を group1 で束ねる。プロップ種別ごとに別バッファ・別 uniform を持つため、
  GPU 側では `prop_id` を運ばない（バッファに入った時点でどのプロップかは確定している）。
- **風による曲げ**: タンジェント（葉の縦方向ベクトル）を回転させることで曲げる（＝弧長保存。
  頂点を単純にシアーする方式と違い、曲げても葉の長さが伸び縮みしない）。根元（`height_t=0`）で
  曲げ角 0、`h^2` の片持ち梁（cantilever）荷重分布で先端に向かうほど曲がりが強くなる。
  基本振動＋突風の 2 成分を合成し、位相は `seed` からインスタンスごとにずらす（草原全体が
  同位相で揺れる機械的な見た目を避ける）。
- **背面**: `cull_mode: None`（板ポリなので両面描画）。裏面フラグメントでは頂点で作った面法線が
  視線と逆向きになるため、`fs_grass` が `front_facing` を見て法線を反転する
  （視線ベクトルとの内積ではなく `front_facing` を使うのは、ラスタライザの巻き方向判定と厳密に
  一致させ、シルエット際でのちらつきを防ぐため）。

### 15.5 法線の地表寄せ（normal flattening）

**問題**: 直立した葉の真の幾何法線は `cross(side, tangent)` であり、これは葉の面に垂直——つまり
**水平**に近いベクトルになる（葉は縦に長い板ポリで、法線は板の面外方向を指すため）。高い指向性の
太陽光に対しては `N·L ≈ 0` となり、ほとんどの草がほぼ真っ黒に描画される。ランダムな yaw によって
たまたま法線が太陽側を向いた株だけが明るく光り、**まだら（2 値的な斑点状）** な見た目になる。

**対策**: 陰影計算に使う法線を、株の接地面法線（`inst.normal`＝地表の上方向）側へ `normal_up_blend`
の割合だけ寄せる（0=真の幾何法線、1=完全に地表法線）。地表法線は常に太陽に対してより素直な角度を
持つため、まだらが解消され自然な陰影になる。これはリアルタイム草描画の標準的な対策
（normal flattening / spherical grass normals）である。

**適用位置（重要）**: このブレンドは `fs_grass` の中で **`front_facing` による反転処理の"後"に**
適用する（§15.4 の背面反転と同じ関数の中）。反転より先にブレンドすると、裏面フラグメントで
まず地表寄せした法線を反転してしまい、意図した「地表側へ寄せる」効果が裏面では逆向きに働いて
打ち消される。反転してから寄せることで、表裏どちらのフラグメントでも一貫して地表法線側へ寄る。

**計測で分かったこと（過信しないこと）**: 同一構図の全景（約 48,000 株）で `normal_up_blend`
を 0.0 と 0.7 で撮り比べたところ、草ピクセルの平均輝度は 111 → 117、ほぼ黒（輝度 30 未満）の
草ピクセル比率は 7.6% → 4.5% へ改善した。方向としては確かに効いているが、**「黒い草」が
すべてこの法線問題で説明できるわけではない**。近接構図では、そもそも `color_bottom` が
`[0.05, 0.18, 0.03]` とほぼ黒に近く、葉の下半分は**アルベドの時点で暗い**（`height_t` が小さい
領域は照明に関係なく暗く出る）。近接スクリーンショットで葉が黒く見える場合、法線を疑う前に
まず `color_bottom` と構図（葉のどの高さが映っているか）を確認すること。

### 15.6 IPC

| コマンド（送信） | 引数 | 応答（受信） |
|---|---|---|
| `TERRAIN_SCATTER_RULES:{prop_id},{seed}` | `prop_id`:文字列（空文字＝全プロップ対象）。`seed`:u64 | `TERRAIN_SCATTER_OK:{n}`（総インスタンス数）／`TERRAIN_SCATTER_ERROR:{msg}` |
| `TERRAIN_SCATTER_BRUSH:{prop_id},{sx},{sy},{radius},{density},{erase}` | `prop_id`:文字列。`sx,sy`:f32（画面座標）。`radius,density`:f32。`erase`:0/1 | `TERRAIN_SCATTER_BRUSH_OK:{hx},{hy},{hz}`（ワールドヒット点）／`TERRAIN_SCATTER_BRUSH_MISS` |

- `TERRAIN_SCATTER_RULES`: 全チャンクを対象プロップについてルールで散布し直す。**対象プロップの
  既存インスタンスだけを全チャンクから取り除いてから生成し直す**（他プロップとブラシ散布ぶんは
  温存）。ブラシで描いた草も対象プロップなら消える点は仕様——「ルールで敷き直す」は自動生成の
  結果で置き換えることであり、手描きだけを見分けて残す情報をインスタンスは持っていない。
  チャンクごとの生成は `rayon` で並列化されるが、**チャンク座標を昇順ソートしてから並列マップ**
  するため、実行順・スレッド数に関わらず書き戻し順は決定的である。
- `TERRAIN_SCATTER_BRUSH`: 密度ブラシ（`TERRAIN_BRUSH`）と同じ `terrain_raymarch_hit` で着弾点を
  求め、そこへ球状に散布／消去する。プロップは容易にチャンク境界を跨ぐため、一旦影響チャンクの
  インスタンスをフラットな作業配列へ集めて処理し、結果を位置に基づいて所有チャンクへ仕分け直す。
- **決定性**: 同じ `(seed, 地形, props.json)` なら必ずビット単位で同じ結果になる。
  `scatter_chunk_by_rules` はチャンク座標とシードだけから乱数列を導出する（セルごとに独立
  ストリーム）ため、チャンク生成が並列化されていても実行順・スレッド数に依存しない
  （§「決定性」の設計はチャンク間で相互に独立な乱数ストリームを前提にしており、並列化はこの
  独立性が成り立つ範囲でのみ安全に行っている）。

### 15.7 地形編集後の再接地（restick）

密度ブラシ（Add/Subtract/Smooth/Flatten）・undo・redo で地形の形状が変わると、その上に載っている
散布インスタンスは古い高さのまま宙に浮く／埋まる。`restick_scatter_for_chunks` が、密度編集で
触れたチャンクの散布インスタンスを新しい地表へ再接地する:

- 各インスタンスの XZ 位置を保ったまま、元の Y から **上下 `RESTICK_Y_SEARCH_VOXELS`（=4）ボクセル
  ×`voxel_size`**（既定 0.5m×4=2m）の範囲で地表（AIR→SOLID 境界）を探索し直す。
- **見つかれば**その高さへ移動（盛土で 1 チャンクぶん上へ押し上げられた場合は所有チャンクも
  再計算して移し替える）。
- **見つからなければ**（崖が崩れて地面ごと消えた等）そのインスタンスは削除する
  （空中に取り残さない）。
- 探索窓を「無限」ではなく 4 ボクセルに絞っているのは、ブラシ 1 ストロークが地面を動かす量は
  ボクセル数個ぶんに収まるため——これを大きくすると、崖の下に落ちた草が遠くの地面へ吸着して
  不自然になる。

**ペイント編集ではこの再接地を呼ばない**。理由は、ペイント（`handle_terrain_paint_world`）は
レイヤ重み（頂点カラー）だけを変え、密度グリッド＝形状を一切変えないため。頂点が動かない以上、
草が宙に浮くことも埋まることも構造的に起こり得ない。ペイントは 1 ストロークで何十回もコマンドが
飛んでくるため、そこで全インスタンスの柱探索（1 本あたり数十回の密度サンプル）を走らせると
目に見えて重くなる——形状が変わらないと分かっている経路でコストを払わない、という判断である。
undo/redo は密度スナップショットを戻すため、再接地も併せて掛ける（でなければ「undo したら草だけ
空中に取り残される」という壊れた見た目になる）。

### 15.8 エディタ UI

**地形ツールバー**（terrain モード時）に「散布」ツールを追加（`TerrainOpScatter` = 擬似 op 値
101。既存の Add/Subtract/Smooth/Flatten/Paint とは別コマンド `TERRAIN_SCATTER_BRUSH` を送る）。

| UI | 機能 |
|---|---|
| プロップ選択コンボ | 散布ツール選択時のみ表示。`props.json` の並びに対応 |
| 密度スライダー | `TERRAIN_SCATTER_BRUSH` の `density` |
| 半径スライダー | 他ブラシと共有の半径スライダーを流用 |
| 消去チェックボックス | オンで対象半径内のプロップ（プロップ種別を問わず）を消去 |

強度スライダーは散布ツールでは意味を持たないため非表示にし、代わりに密度スライダーを表示する
（Shift+ホイールも同様に密度へ効くよう配線し直してある。既存ブラシのホイール操作規約を踏襲）。

**地形設定ウィンドウ「散布」タブ**（`TerrainSettingsWindow.Scatter.cs`。レイヤタブと同じ
`TerrainSettingsWindow` の partial として実装。行生成ヘルパを共有する）:

- **プロップ一覧**（左）: `props.json` の並び。追加・複製・削除。
- **プロパティ**（右）: `kind`（草/モデル）切り替えで意味のあるパラメータだけを条件付き表示する
  （「無効化」ではなく「非表示」——プロジェクトのインスペクタ方針。値自体は常に保持されるため
  kind を戻せば以前の設定が復帰する。Rust 側 `TerrainProp` が全フィールドを常に持つ設計と対）。
- **ルール編集**: 斜度・高度ウィンドウ・レイヤ条件・閾値。
- **「ルールで再散布」+ シード欄**: `TERRAIN_SCATTER_RULES:{prop_id または "all"},{seed}` を送る。
  ウィンドウ起動時にシードへ乱数値を自動で振っておく（0 固定のまま「ランダム」を押し忘れて
  毎回同じ配置になる事故を防ぐため）。

### 15.9 第2段への持ち越し・既知の限界

- **`kind=model` プロップは描画されない**。散布インスタンスそのものは生成・保存される（`.tscatter`
  に入っている）ため、データは失われない。描かれない理由は既存のモデル描画経路が ECS 前提で
  組まれているため:
  - `frame_renderer.rs` の `gpu_model_by_path` は、毎フレーム ECS の `ModelComponent` アクター群
    （`all_mcs`）から**専ら**組み立て直される。
  - 散布バッチには対応する ECS アクターが存在しないため、main/shadow/ID/RT の**すべての描画パス**
    でこの表からの参照が None になり静かに描画スキップされ、さらに 60 フレーム後には stale として
    prune される。
  - 対処には、散布バッチ専用の独立した `GpuModel` 所有・管理と、各描画パスへの個別登録が要る
    （ECS アクターに紐付けずに描くための新しい経路）。単純に N 体のアクターを spawn する案は
    シーンファイルの肥大・`.tscatter` との情報二重化を招くため採らない。
- **接触インタラクション**（プレイヤーが通ると草がなびく／なぎ倒れる等）は未実装。第2段のスコープ。
- **prop_id の並び替え耐性が無い**: `.tscatter` は prop_id を添字で保持するため、props.json の
  並び替えで既存散布データの指し先がずれる（§15.2）。

### 15.10 パフォーマンス実測

46,679 本の草を描画した実測（測定条件: Mailbox プレゼントモード・GPU バックプレッシャー無し）:

| 項目 | 実測値 |
|---|---|
| 草 GPU インスタンスバッファの初回構築 | 10.2 ms（一度きり。以降は `grass_gpu_dirty` が立った散布操作時のみ） |
| 定常状態の草描画コマンド記録 | 0.001 ms/フレーム |
| CPU フレーム合計 | 約 2.0 ms |

**注意**: Mailbox プレゼントかつバックプレッシャー無しの条件で計測しているため、これは
**CPU コストの実測値**であり GPU 側のコストについての測定ではない。GPU が飽和する構成
（VSync・低スペック GPU・高解像度シャドウ等）では別途フレームタイムの計測が要る。上記数値は
CPU コストの上限（アッパーバウンド）として読むこと。

### 15.11 ルール散布のボトルネックと高速化（`fast_density_at`）

**症状**: `TERRAIN_SCATTER_RULES`（全チャンクをルールで敷き直す）が CPU 100% 張り付きで
数十秒かかり、その間エディタが固まる。**エディタは既定で debug ビルドの `SEED.exe` を起動する**
（`MainWindow.xaml.cs` の `ResolveRuntimePath`）ため、体感するのは debug の遅い方の数値である。

**計測（`terrain_scatter_ops::tests::bench_scatter_rules_realistic`。48 チャンク・出荷 props.json の
密度・地表を全チャンクが含む起伏地形）**:

| ビルド | 直列 生成 | rayon 並列 生成 | 生成インスタンス数 |
|---|---|---|---|
| debug 修正前 | 37,173 ms | 8,713 ms | 8,048 |
| debug 修正後 | 9,397 ms | 2,395 ms | 8,048（修正前とビット一致） |
| release 修正前 | 2,689 ms | 654 ms | 8,048 |
| release 修正後 | 812 ms | 168 ms | 8,048（同上） |

（並列は当該計測機の実効 ~4 コア相当。生成インスタンス数が修正前後で厳密に一致するのが
`fast_density_at` のビット等価性の裏付け。）

**真のボトルネック**: 生成が支配項（GPU バッファ構築は 8k 本で 10ms 程度・`.tscatter` 保存は
微小）。生成コストの正体は「候補柱の表面マーチ」だった。1 チャンクあたり密度に比例した数千の
候補（grass_field 密度 12 → 56×56=3136 点/チャンク）を上から 0.25m 刻みでマーチし、各ステップで
密度をトライリニア補間する。汎用の `sample_density_world` は **8 コーナーそれぞれに `find_owner` を
呼び、`find_owner` は `ChunkCoord` を SipHash の `HashMap` で引く**——つまり 1 密度サンプル =
最悪 8 回の SipHash 探索。散布全体では数千万回に達し、これが CPU 100% 張り付きの実測上の主因。
なお 8,048 本しか生えないのに 20 万候補を評価するのはグリッド＋ルール確率棄却の設計そのもの
であり、レイヤ条件（`grass` min_weight 0.3 等）が多くを棄却する（＝候補数は目標描画密度で決まり
削れない）。

**対策（`TerrainScatterField::fast_density_at`）**: トライリニアの 8 コーナーは連続する 2×2×2
サンプルなので、点がチャンク内部（各軸ローカル添字が `[0, cells-2]` の帯）にあれば **8 コーナーが
単一チャンクに収まる**。所有チャンクを 1 回だけ引き、ローカル配列から 8 値を直接読む（SipHash 8→1）。
チャンク遠端境界・地形外に掛かる点だけ汎用パスへ退避する。退避条件は「局所読みが `find_owner` の
primary 解決と一致する範囲」に限定してあるため、**結果は `sample_density_world` とビット単位で
一致する**（散布の決定性という最重要不変条件を一切損なわない）。ユニットテスト
`fast_density_matches_general_bit_exact` が境界・地形外・内部を総当たりで固定している。
効果は debug 約 3.7×・release 約 4×。

**残る硬直について（第2段の候補）**: 高速化後も debug 並列で ~2.4s は全コアを使い切って走るため、
その間エディタは固まる。恒久解は「生成を別スレッドへ出す／複数フレームへ分割する」だが、
`ScatterField` が `terrain` を不変借用する制約上、チャンクのオーナーシップ設計（スナップショット
＋チャンネルで結果を回収）が要り第1段のスコープを超える。`SEED_PERF_TERRAIN=1` で
`[PERF terrain] scatter rules: gen=… writeback=…` の内訳ログが出る。

### 15.12 草のサブピクセル・アンチエイリアス（スクリーン空間最小幅）

**症状**: 遠景の草がギザギザ・点滅し、まばらに見える。**原因はコードのバグではなく**、deferred
G-Buffer に MSAA も alpha-to-coverage も無い（草は不透明パスに乗る）ことによる幾何アンチエイリアス
の不在である。既定 幅 0.04m の細い葉は遠距離で 1 画素未満へ縮み、フレームごとに当たり画素が
入れ替わって点滅・まばら・ジャギとして見える。alpha-to-coverage は MSAA 前提で deferred では
使えず、アルファフェードも不透明 G-Buffer では背景と混ざらないため、いずれもこの構成では無効。

**対策（`grass_gbuffer.wgsl`・頂点段）**: 葉の半幅を **スクリーン空間で最低
`GRASS_MIN_SCREEN_HALF_PX`（0.75px）確保**するよう頂点段で太らせる（`grass_min_screen_half_width`）。
`center` と幅端点を `view_proj` で射影し、`u_camera.resolution` を掛けて画素幅を測り、目標画素との
比を拡大倍率（1..`GRASS_MAX_WIDTH_WIDEN`=8 でクランプ）とする。**近距離では既に十分太いので倍率 1
（無変化）、遠距離のみ太らせて葉を常に 1 画素以上の安定したリボンにする**。left/right・top/bottom
頂点は同じ `center`・`half_width` から同じ倍率を得るため対称に太り、四角形に亀裂は入らない。
幾何法線・穂先カットアウト・風の計算には一切影響しない（幅だけを変える）。GPU uniform の
レイアウト（80 バイト）も不変で、新フィールドは追加していない。

**併せて props.json のデフォルト見直し**: 葉幅を fuller に（grass_field 0.04→0.05・grass_dry
0.035→0.045）。幅はレンダー専用パラメータで散布コスト（§15.11）には一切影響しない。散布密度は
Problem 1 を悪化させないため据え置いた。

**Before/After（同一構図・全景・debug スモーク `SEED_TERRAIN_SMOKE`）**: 修正前は遠景の草が
まばらな黒い点の散乱に見えるが、修正後は草が均一なマットとして埋まり、サブピクセルの点滅・
ジャギが目に見えて減る。近接構図は葉が近くて既に十分太いためほぼ同一（＝意図どおり遠景のみ改善）。

## 16. 物理コリジョン（Static 三角形メッシュコライダー）

地形に **物理コリジョン**を持たせ、落下する Dynamic 物理オブジェクトが地形の上に乗る／
斜面を転がる／掘った穴へ落ちるようにする。MVP は**地形コリジョンのみ**（キャラクター
コントローラーは対象外）。実装は `physics/thread.rs`（Rapier3D）と
`core/app_base/app/terrain_ops.rs`・`physics_ops.rs` にまたがる。

### 16.1 形状：共有頂点＋インデックスのトライメッシュ

洞窟・オーバーハングを表現できるよう **heightfield ではなく三角形メッシュ**を使う。
`ColliderShape` に新バリアント `TriangleMeshIndexed { vertices, indices }` を追加した
（`physics/types.rs`）。既存の `TriangleMesh`（三角形ごとに頂点を複製した展開版）と違い、
地形メッシュ（`TerrainMesh` = 共有頂点 `positions` ＋ 共有インデックス `indices`）をそのまま
Rapier の `ColliderBuilder::trimesh(vertices, indices)` へ渡せる（`build_collider_shape`）。
頂点を複製しないため、地形の大規模メッシュ（cells=64 で 1.7 万頂点級）でもメモリ効率が良い。

コライダーのメッシュは描画メッシュと**同じ** `terrain::generate`＋隣接サンプラ
（`build_chunk_collider_shape`）で作るので、コリジョン形状は見た目と頂点単位で一致する
（原理的にめり込み・浮きが出ない）。三角形 0 個（全 AIR／全 SOLID）の空チャンクは
`None` を返してコライダーを作らない。

### 16.2 座標系：チャンクローカル → ワールド

`TerrainMesh.positions` はチャンクローカル座標（原点＝チャンク最小コーナー）。地形メッシュ
アクターは「チャンク原点への平行移動のみ」（回転・スケール無し。`spawn_chunk_actor` の
Transform）なので、コライダーも **回転単位・スケール 1・オフセット 0**、位置＝チャンクの
ワールド原点（`ChunkCoord::world_origin`）で登録する（`terrain_collider_object`）。
`rigidbody = None` により RigidBody 無しの **Static コライダー**（ワールド固定）になる。

### 16.3 登録・管理

地形コライダーは ECS の `ColliderComponent` を持たず **terrain 側で内部管理**する。
`TerrainState` にチャンク → 物理 `entity_id` のマップ（`chunk_collider_ids`）と単調採番
カウンタ（`next_terrain_collider_id`）を持つ。地形 `entity_id` はアクターの DFS `entity_id`
空間（1 始まり・アクター数ぶん）と衝突しないよう **高位ベース `2^48` から採番**する
（Static なので物理結果の `transform_updates` には現れず、DFS 逆引きにも掛からない）。

- **Play 開始時**（`start_physics` 末尾）に `register_all_terrain_colliders` が全チャンクぶんを
  `AddObject` で登録する。空チャンクはスキップ。

### 16.4 変形時のリアルタイム再構成

地形は Static で固定し、**変形（掘削・盛土）でメッシュが変わったときだけ**コライダーを作り直す。
チャンク再メッシュ（`remesh_chunks`＝密度を変えたブラシ由来）の末尾で、**物理稼働中のみ**
（`physics_thread.is_some()`）`sync_terrain_chunk_collider` を呼ぶ：既存コライダーを
`RemoveObject` してから、新メッシュが空でなければ同じ `entity_id` で `AddObject` する。
掘り切って空になったチャンクは削除のみ。**ペイントは形状不変**なので高速パス
（`apply_terrain_paint_colors`）に乗り、この経路には来ない（コライダーは作り直さない）。

物理スレッドはコマンドを 1 ドレインで `Remove→Add` の順に処理し、`QueryPipeline` は毎ステップ
更新されるため、実行中の Static コライダー差し替えに追加のコマンドは不要だった。

### 16.5 検証

- **ユニット／統合テスト**（`cargo test`）:
  - `build_chunk_collider_shape`：空チャンク→`None`／等値面あり→有効な共有頂点＋インデックス
    （全インデックスが頂点範囲内）／頂点がチャンクローカル（ワールド座標が混入しない）。
  - `terrain_collider_object`：回転なし・スケール 1・RigidBody 無し（Static）。
  - `dynamic_ball_rests_on_indexed_trimesh_floor`：`TriangleMeshIndexed` で作った床の上に
    落下する Dynamic 球が `y≈0.5` で静止（すり抜けない）ことを固定 dt で決定論的に検証。
- **実機スモーク**（`SEED_TERRAIN_PHYS_SMOKE=1`＋`--mode=play`、自己ゲート）:
  `run_terrain_physics_smoke` がフラット地形＋中央の小山の上空へ Dynamic 球を 5 個落とし、
  `tick_terrain_physics_smoke` が各球の Y を毎フレーム記録する。実測では
  `y≈3.9〜5.9 → 0.19〜0.71` へ下がって静定し（`Y<0`＝すり抜けは皆無）、
  `all_balls_rested_on_terrain = true` を出力した。球には描画メッシュを付けないため画面には
  出ず、検証は数値ログで行う。
