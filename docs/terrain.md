# Terrain（地形エディタ）設計メモ — T1 ランタイム基盤 ＋ T2/T2b レイヤブレンド ＋ T3 第1段 散布

本書は SEED の地形（terrain）機能の設計正典である。
**T1: ランタイム基盤**（ボクセル SDF ＋ marching cubes による洞窟対応の破壊可能地形）と、
**T2: 地形マテリアルのレイヤブレンド**（スプラット × triplanar・斜度/高度ルールによる自動下地・
ペイントブラシによる手修正）と、
**T2b: レイヤ拡張・テクスチャ・タイリング解消**（レイヤ定義を最大 16 層へ拡張しつつ
チャンク単位パレットで同時ブレンド 4 層の頂点フォーマットを維持・レイヤテクスチャの
2D 配列対応・3 種のタイリング解消モード）と、
**T3 第1段: 地形プロップ散布**（斜度/高度/レイヤ重みによる草・木の自動散布＋ブラシ手描き・
手続き生成 GPU インスタンシング草・地形編集後の再接地）と、
**T3 第2段: `kind=model` プロップの実描画**（散布した実モデルアセットを ECS 非依存の
専用インスタンシング経路で deferred G-Buffer へ描画＋ラスタ影に載せる）をカバーする（§15）。

> スコープ外（未実装）: 散布物との接触インタラクション、インポスター（hemi-octahedral ビルボード）／
> ストリーミング、木の風揺れ。
> **散布モデルの RT 影（TLAS 登録）は §15.9f で実装済み**（RT 反射・RT-GI への寄与は同経路で自動的に効く）。
> **植生 LOD 第1段（距離 LOD メッシュ切替＋遠景密度減衰）は §15.9d で実装済み**。末尾「拡張余地」参照。

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

### 7.1 チャンク単位 地形 LOD（遠いチャンクを低ポリで描く）

俯瞰など全チャンクが視界に入る構図では、フラスタム＋距離カリング（§申し送り元）でも
1 枚も落とせず、256+ チャンクの全三角形（実測 16×16×3=768 チャンクで **約 55 万三角形**）を
毎フレーム描くため重い。そこで **遠いチャンクを低解像度の marching cubes メッシュで描く** LOD を入れた。

- **低解像度メッシュ生成**（`terrain/lod.rs`・エンジン非依存）: 密度グリッド（33³）を stride で
  間引いた「粗いチャンク」を組み、`voxel_size` を stride 倍・`chunk_cells` を 1/stride にした設定で
  **既存の `generate_standalone` をそのまま**回す。チャンクの実寸（extent）は保たれ、出力頂点は
  フル解像度と同じローカル空間 `[0, extent]` に載る。段は `TERRAIN_LOD_STRIDES = [1, 2, 4]`
  （LOD0=フル・LOD1≒頂点1/4・LOD2≒頂点1/16）。**marching_cubes.rs には一切手を入れておらず、
  フル解像度出力・水密性・全テストは 1 ビットも変わらない**（LOD0 は従来経路をそのまま使う）。
- **境界の継ぎ目（クラック）対策 = スカート**: 隣接チャンクが別 LOD だと境界でテッセレーションが
  食い違い隙間が出る。第 1 段は **スカート**（チャンクの 4 垂直側面 x=0/x=extent/z=0/z=extent 上の
  表面辺から真下へ深さ `voxel*stride*2` 伸ばす両巻きの幕）で隙間を隠す。スカートは **LOD>=1 の
  間引きメッシュにだけ**付ける（LOD0 は境界サンプル共有で隣接 LOD0 と水密・ペイント高速パスの
  由来辺キャッシュと食い違わせないため）。LOD0↔LOD1 の継ぎ目は LOD1 側のカーテンが受け持つ。
  実機（俯瞰・全 LOD2）で **露骨な穴・見通せるクラックは出ない**ことを確認済み
  （※ LOD2 チャンク境界に法線の不連続＝薄い陰影の帯が残る。standalone の境界勾配クランプに由来。
  平坦地では目立たない。近傍サンプラ対応は第 2 段の申し送り）。
- **LOD 選択**（`terrain_ops.rs::tick_terrain_lod`）: フレーム先頭で **前フレームのメインカメラ位置**
  （`App::last_camera_pos`）とチャンク AABB の最近点距離から目標 LOD を決める。しきい値は
  名前付き定数＋env 上書き（`SEED_TERRAIN_LOD1_DIST` 既定 60m / `SEED_TERRAIN_LOD2_DIST` 既定 140m。
  将来 `TerrainSettings` へ）。境界のばたつきを防ぐヒステリシス（±12%）付き。`SEED_TERRAIN_LOD_DISABLED=1`
  で全 LOD0（before 計測用）。
- **保持と切替（動的キャッシュ）**: チャンクごとに保持する GpuModel は **常に 1 つ**（現在 LOD ぶんだけ）。
  目標 LOD が変わったチャンクを `pending_remesh` へ積み、既存の VRAM 安全な `remesh_chunks`
  （gpu_model と instanced_batch を同時に作り直し・統合バッチ／BLAS キャッシュも破棄）で差し替える。
  1 フレームの切替は近い順に **最大 8 チャンク**へ小分けしてスパイクを防ぐ。全 LOD を同時保持しないため
  **メモリ増はゼロ**（むしろ遠方が低ポリになるぶん頂点 VRAM は減る）。CPU メッシュは切替時に 1 回だけ生成。
- **描画**: 既存のチャンク描画（`shared_model_batches` × `gpu_model_by_path`）をそのまま使う。
  path（`terrain://…`）ごとの GpuModel が LOD ぶんに差し替わるだけで、フラスタム／距離カリングとも両立
  （視界外はスキップ・視界内は距離で LOD）。
- **物理コライダーは常に LOD0（フル解像度）**: 当たり判定は精度優先。表示 LOD を落としたチャンク
  （`chunk_lod>0`）では描画メッシュを流用せず `build_chunk_collider_shape`（密度からフル解像度 MC）で
  作り直す（`register_all_terrain_colliders` / `sync_terrain_chunk_collider`）。表示だけ粗く・衝突はフル。
- **BLAS（RT 影・反射）は表示 LOD に追従**: BLAS は表示 GpuModel から作られるため、遠方は低ポリの
  加速構造になる（遠方の RT 影は精度が落ちるが描画三角形削減を優先。第 2 段で要判断）。

**実機計測（`SEED_TERRAIN_SMOKE=1 SEED_SMOKE_CHUNKS=16 SEED_SMOKE_NO_SCATTER=1`・768 チャンク）**:

| 構図 | LOD | 総三角形（`[PERF terrain] lod: total_tris`） | main_pass |
|---|---|---|---|
| 俯瞰 | OFF | 549,428（全 768 = LOD0） | 約 3.0–3.8ms |
| 俯瞰 | ON | **71,084（全 768 = LOD2）＝ −87.1%** | 約 2.5ms |
| 一人称 | ON | 312,772（LOD0=228 / LOD1=528 / LOD2=12）＝ −43%・近景フル解像度維持 | — |

俯瞰で遠方まで含め全チャンクが LOD2 へ落ち、三角形が 8 割以上減る。一人称では近景がフル解像度の
まま遠方だけ低ポリ化する（LOD0/1/2 の階調が出る）。どちらも穴・破綻なし。

**第 2 段の申し送り**: (1) LOD 境界の法線不連続（standalone の境界クランプ → 近傍サンプラ対応で
帯を消す）、(2) 上下 Y 面／洞窟の継ぎ目スカート（現状は 4 垂直側面のみ）、(3) 遠方 BLAS 精度、
(4) しきい値の `TerrainSettings` 化。

### 7.2 静的地形チャンクの毎フレーム CPU 削減（CPU バウンド対策）

三角形を LOD で −87% 減らしても実機 fps が変わらない＝**CPU バウンド**だった。16×16×3層=768
チャンクの俯瞰（`[PERF]` 内訳）で支配していたのは描画三角形でも 276 ドローコール記録でもなく、
**静的な地形チャンクに毎フレーム走る 2 つの無駄なバッチ更新**だった。

- **Fix A — per-MC `instanced_batch` の毎フレーム更新を撤去**（`[PERF]` の `batch` バケット）。
  Phase R7 のバッチ統合以降、実描画はすべて `shared_model_batches`（batch_key ごとの統合バッチ）を
  通る。`ModelComponent::instanced_batch` はどの描画・アウトライン・ピッキング経路からも参照されず
  （唯一の描画アクセサ `rendering_refs()` は呼び出し 0 件）、その `update()`（距離 LOD 振り分け＋
  GPU 書込）は**完全な死荷重**だった。呼び出し元（`update_all_mc_batches_for_wl`）ごと削除。
  実測 `batch` ≈ 4.1ms/フレーム → **0ms**。
- **Fix B — 統合バッチ更新を静的地形チャンクでスキップ**（新設 `[PERF]` `merge` バケット）。
  `frame_renderer` の統合バッチ更新ループは毎フレーム全 batch_key に `mark_dirty()+update()`
  （rayon 行列再計算・全ノードバッファ書込・id バッファ書込）を掛けていた。地形チャンクは静的
  （1 インスタンス＝単位行列）で、変形/掘削/LOD 切替のときだけ mats/ジオメトリが変わる。
  そこで `SharedModelData` に前回アップロードした `(mats, abs_ids)` を保持し、地形キー
  （`TERRAIN_SOURCE_SCHEME`）かつ前フレームと一致するなら更新を丸ごとスキップする。
  - **正しさの根拠**: 地形ジオメトリを変える全経路（`remesh_chunks`＝掘削/LOD 切替、`handle_terrain_init`
    ＝初期化/ハイトマップ、シーンロード）は `invalidate_geometry_caches` で当該 `shared_model_batches`
    エントリを削除する。削除＝次フレーム reinit＝`uploaded_sig=None`＝フル更新となり、古いメッシュが
    残ることはない。高速ペイントは頂点色を `gpu_model`（描画は `gpu_model_by_path` から引く）へ直書き
    するだけで統合バッチ更新を必要としない（元々 `mark_batch_dirty` も呼んでいない）。行列だけでなく
    `abs_ids` も比較するのは、mats 不変でもアクター追加/削除で id_base がずれるとピッキング ID が
    陳腐化するのを防ぐため。通常モデル/散布/アニメは非地形キー or mats 変化のため従来どおり毎フレーム更新。

**実機計測（`SEED_TERRAIN_SMOKE=1 SEED_SMOKE_CHUNKS=16`・768 チャンク俯瞰・276 チャンク描画・
物理稼働・`[PERF f=120..600]` 平均）**:

| バケット | before | after | 備考 |
|---|---|---|---|
| **total** | **26.7ms（≈37fps）** | **13.0ms（≈77fps）** | **−13.7ms / −51%** |
| batch（per-MC 更新） | 4.1ms | **0ms** | Fix A（死荷重撤去） |
| merge（統合バッチ更新） | （before は other に内包） | 1.08ms | Fix B 後は merge_map 構築のみ |
| other | 14.4ms | 6.24ms | path-2 update をスキップした差が主 |
| main_pass | 6.0ms | 4.36ms | GPU 書込減による副次的な軽減 |
| tlas / physics | 1.25 / 1.0ms | 1.02 / 0.9ms | ほぼ不変（対象外） |

`draw=0.000ms`（Deferred のため不透明は G-Buffer パスで記録）であり、**ドローコール記録は支配項では
なかった**。支配項は「静的地形への毎フレーム冗長バッチ更新」で、Fix A+B で frame time が約半減した。

**次段の申し送り（さらに削るなら）**: (1) `merge_map` 自体の毎フレーム再構築（768 エントリの Arc clone＋
push、after では merge≈1.08ms のほぼ全部）を地形はキャッシュ再利用にする、(2) 276 個別ドローの
統合ドロー化（同一マテリアル・LOD のチャンクメッシュを 1 頂点/インデックスバッファへマージ or
indirect draw）、(3) メッシュレット化／地形ストリーミング。

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
| `TERRAIN_BRUSH_MASK:{path}` | `path`=グレースケール画像のパス（`assets://` 仮想パス・絶対パスのどちらも可）。**空文字で解除**。コロン以降はすべて path（カンマ・空白を含んでよい） | `TERRAIN_BRUSH_MASK_OK:{path}`／読み込み失敗時 `TERRAIN_BRUSH_MASK_ERROR:{path}`（下記 §9.9） |
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
- `TERRAIN_BRUSH_MASK`: ブラシの **形状マスク**（ブラシテクスチャ）を設定・解除する状態設定コマンド。
  半径・強度と同じ「ツールの現在設定」であり、ブラシ 1 発ごとの IPC には載せない（§9.9）。
- `TERRAIN_SAVE`: 全チャンクの tvox（＋ .tscatter / .tcover）を書き出す。
- **シーン保存（`SAVE_SCENE`）も地形をフラッシュする**。こちらは `App::flush_dirty_terrain` が
  **ダーティなチャンクだけ**を書く（`TERRAIN_SAVE` は無条件に全チャンク）。
  地形の実体は .scene の外にあるため、これが無いと「Ctrl+S したのに掘った地形が消える」ことになる。
  ダーティが空なら 1 バイトも触らない（保存が地形の規模で遅くならない）。
  成功時に IPC は送らない（`SAVE_OK` の二重通知を避けるため）。失敗時のみ `TERRAIN_SAVE_ERROR:{msg}`。

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

### 9.5 ストローク中の付随処理と RT 加速構造（BLAS）の追従

ドラッグ中（`stroke_active`）は、描画メッシュ（`remesh_chunks`）だけを毎フレーム更新し、
重い付随処理は `stroke_deferred_chunks` へ溜めて確定時（マウスアップ or 無操作タイムアウト
`STROKE_IDLE_FLUSH_MS`）に一括処理する（`finalize_stroke_deferred`）。

| 付随処理 | ストローク中 | 理由 |
|---|---|---|
| 物理コライダー再構築 | **遅延** | 同期 QBVH 構築がフレーム時間を支配する |
| 散布の再接地 | **遅延** | 全インスタンスの柱探索が重い |
| 統合バッチ無効化 | 毎フレーム | 描画に必須 |
| **RT BLAS 再構築** | **毎フレーム（予算つき）** | 遅延すると**掘った跡が真っ黒になる**（下記） |

**RT BLAS だけは遅延できない。** BLAS キャッシュ（`rt_shadow.rs`。キー = `source_path`
＝チャンクの `batch_key`）は「一度作ったら作り直さない」ため、掘削・カバー消去でラスタの地表が
下がっても RT が辿る地形は古い（高い）ままになる。すると RT 影のレイ原点が古い形状の**内側**へ
沈み、レイ原点バイアス（数センチ）では抜け出せず全面遮蔽＝**真っ黒**になる。

そこで頂点が動いたチャンクを `TerrainState::rt_blas_prune_pending` へ積み、
`terrain_ops.rs::flush_rt_blas_prune` が**毎フレーム**（`frame_renderer` の
カバー焼き直し直後・描画ブロックより前）消化する。積む側は 2 経路:

- `remesh_chunks(defer_side_effects=true)` … 密度ブラシ（掘る/盛る）のストローク中再メッシュ
- `apply_pending_cover` … カバー場の頂点焼き直し（詳細は **docs/cover_field.md §6-2**）

消化は 1 フレーム `rt_shadow::MAX_BLAS_BUILDS_PER_FRAME`（= 8）チャンクまでの予算つきで、
これは BLAS の 1 フレーム再構築上限と同じ値である（多く捨てても再構築が追いつかない）。
捨ててからまだ作り直されていないチャンクは TLAS 詰め直しで素通りされる
（`blas_cache.get()==None`）ため、**古い形で誤遮蔽するのではなく数フレーム影を落とさない**
という安全側へ縮退する。

**計測**：`SEED_PERF_LOG` を設定すると
`[PERF terrain] rt_blas_prune chunks=… remain=… take=…ms` が出る（`chunks` = そのフレームで
捨てた数、`remain` = 予算を超えて次フレームへ繰り越した数）。BLAS 構築そのものの件数は
`[SEED RT] BLAS 構築: terrain://…` 行と `BLAS 分割ビルド: … 後続フレームへ繰り越し` 行で追える。

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

### 9.9 ブラシ形状マスク（ブラシテクスチャ・TERRAIN_BRUSH_MASK）

グレースケール画像をブラシの**形状**として使う機能。純粋層は `terrain/brush_mask.rs`、
状態とキャッシュ解決は `app/terrain_brush_mask_ops.rs`。

| 項目 | 値 |
|---|---|
| 対象ブラシ | **レイヤペイント（`TERRAIN_PAINT`）** と **カバー（`TERRAIN_COVER_BRUSH`。塗り／消去の両方）** |
| 対象外 | 密度ブラシ（`TERRAIN_BRUSH`）・散布ブラシ（`TERRAIN_SCATTER_BRUSH`） |
| 画像の読み方 | **R チャンネル**のみ（`CoverMask::sample`・最近傍）。白(255)=フル強度 / 黒(0)=0 |
| 貼り付け範囲 | ブラシ球の **XZ バウンディング正方形**（中心 = 着弾点、一辺 = 2 × 半径）。**回転しない** |
| UV 規約 | `u = (world_x - center_x) / (2r) + 0.5` / `v = (world_z - center_z) / (2r) + 0.5` |
| V の向き | 画像の上端（v=0）が **-Z** 側、左端（u=0）が **-X** 側 |
| 未指定のとき | 従来どおりの円形フォールオフ（smoothstep）。**挙動はビット単位で従来と同一** |
| 読み込み失敗のとき | **円形フォールオフへ縮退**（効果 0 にはしない）。`TERRAIN_BRUSH_MASK_ERROR` を返す |
| 設定の寿命 | 半径・強度と同じ「ツールの現在設定」。シーンには保存されない。地形の作り直し（`TERRAIN_INIT` / シーンロード）を跨いでも**パスは保持**される |
| Undo | 対象外（道具の設定であってシーンの値ではない） |

**V の向きを +Z = V 増加にした理由**: カバーエミッタのマスク範囲（`CoverEmitRange::TextureMask`）が
まったく同じ式を使っており、「真上から見下ろした絵をそのまま地面へ貼る」向きになっている。
同じ画像をエミッタとブラシで使ったときに上下が食い違わないよう揃えた。
（轍スタンプの `CoverStampShape::Texture` は `v = 0.5 - forward/size` と逆向きだが、あちらは
**進行方向を画像の上端に合わせて回転する**スタンプであり、「前が上」という別の規約に従っている。）

**マスク指定時のサンプル棄却範囲**: レイヤペイントは通常「球の中（3D 距離 ≤ 半径）」だけを塗るが、
マスクありでは正方形の四隅（球の外）も塗れなければ矩形の絵が角から欠ける。そのため
**XZ は正方形・Y だけ半径で切る**。カバーブラシは元から XZ 平面の話なので変更なし。

**マスクを掛け算しない理由**: マスクは「形そのもの」を与えるもの。円形フォールオフと乗算すると
四隅が必ず削られ、白＝フル強度という約束が守れなくなるため、マスクがあれば**置き換える**。

**サンプリングコスト**: 画像は CPU 側で 1 回だけデコードして `TerrainState::mask_cache`
（`HashMap<パス, CoverMask>`。カバーエミッタ・轍スタンプと共用）へ載せる。
ストローク中の追加コストは 1 テクセルあたり「UV 2 回の乗除算 ＋ `Vec<u8>` の添字 1 回」だけで、
置き換えられる `falloff()`（sqrt ＋ smoothstep）と同程度。デコードはブラシ適用の入口
（`ensure_terrain_brush_mask`）で遅延実行され、2 回目以降は `HashMap` 参照 1 回で戻る。

**UI**: 地形設定ウィンドウ（§14）の「ブラシ」タブに、半径・強度と同じ**ツール共通の設定**として
1 行だけ置く（§14.6）。

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
  別口の .tvox に保存される（§8）。`SAVE_SCENE` はその .tvox / .tscatter / .tcover のうち
  **ダーティなものを先にフラッシュしてから** .scene を書く（`TERRAIN_SAVE` は全チャンクを書く別経路）。
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
- **T3 第2段（実装済み・§15.9）**: `kind=model` プロップ（木・岩など）の実描画。ECS 非依存の専用
  インスタンシング経路で deferred G-Buffer へ描画＋ラスタ影に載せる。TLAS/RT 登録・接触
  インタラクション・木の風揺れは持ち越し。
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
| ブラシ | ブラシテクスチャ（形状マスク）＝ツール共通のブラシ設定（実装済み・§14.6） |

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

### 14.6 ブラシタブ — ブラシテクスチャ（形状マスク）

`TerrainSettingsWindow.Brush.cs`。行の作りはインスペクタ共通の `FileRefBuilder`
（ラベル / パス表示 / ドラッグ＆ドロップ / 参照ボタン / **× ボタンで解除**）。

| 項目 | 内容 |
|---|---|
| テクスチャ | グレースケール画像（png/jpg/jpeg/tga/bmp）。選んだ絶対パスは assets ルート基準へ相対化して保持する |

**「共通のブラシ設定」として 1 行だけ置く理由**: 半径・強度は既にツール横断で共有されている
（ツールを切り替えてもスライダーの値は据え置き）。同じ「ブラシの当たり方」を決めるパラメータの
うちテクスチャだけをツールごとに分けると、①レイヤペイントで形を決めた直後にカバーへ切り替えると
形が消える、②どのツールのテクスチャを編集しているのか UI から読み取れない、という一貫性の無さが出る。
よってレイヤペイント／カバーで**同じ 1 つの値**を共有する（効かないツールを選んでいるときの扱いは
タブが別ウィンドウにあるため、地形ツールバーのような条件付き表示は行わず、説明文で明示する）。

**値の持ち主**: 実体はランタイムの `TerrainState::brush_mask_path`。エディタ側では
`MainWindow._terrainBrushMaskPath` が控える（設定ウィンドウは開閉のたびに作り直されるため、
ウィンドウに持たせると選択が消える）。変更時に `TERRAIN_BRUSH_MASK:{path}` を 1 回だけ送る。

---

## 15. Terrain T3（散布 / Scatter）第1段

草・木などの「地形の上に載る小物」を、斜度/高度/レイヤ重みのルールで自動散布し、
ブラシで手描き修正できるようにする機能。**草（`kind=grass`）は手続き生成 GPU インスタンシングで、
モデル（`kind=model`・木/岩など）は実アセットを ECS 非依存の専用インスタンシング経路で、
いずれも deferred G-Buffer に描画される**（第2段で model 描画を実装。§15.9）。
第 3 の種別 **アクタ（`kind=actor`）** は GPU インスタンスではなくプレハブから
シーンの実アクタを生成する（コライダー・スクリプトが動く。§15.13）。

### 15.1 モジュール構成

密度・レイヤブレンドと同じく「純粋データ層／エンジン統合層／レンダリング層」の 3 段構成。

| ファイル | 責務 |
|---|---|
| `runtime/src/engine/terrain/scatter/props.rs` | プロップ定義（`TerrainProp`/`GrassParams`/`WindParams`/`ScatterParams`/`ScatterRule`）と斜度/高度/レイヤ条件の確率評価。ECS・GPU・ファイル IO 非依存 |
| `runtime/src/engine/terrain/scatter/tscatter.rs` | 散布インスタンス列のバージョン付きバイナリ直列化（純 bytes、ファイル IO はしない） |
| `runtime/src/engine/terrain/scatter/generate.rs` | 決定的なルール自動散布・ブラシ散布・地形編集後の再接地アルゴリズム（`ScatterField` トレイト越しに密度・レイヤ重みへアクセス） |
| `runtime/src/engine/terrain/scatter/tests_scatter.rs` | 散布レイヤ専用のユニットテスト |
| `runtime/src/engine/core/app_base/app/terrain_scatter_ops.rs` | エンジン統合層。`TerrainScatterField`（`ScatterField` の実装）・IPC ハンドラ・`.tscatter` 保存/読込・再接地の呼び出し・草 GPU バッファ再構築 |
| `runtime/src/engine/core/app_base/app/terrain_scatter_actor_ops.rs` | アクタ散布（`kind=actor`）の統合層。散布インスタンスの横取り・プレハブからのアクタ生成・消去ブラシ対応（§15.11） |
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

### 15.9 `kind=model` プロップの描画（第2段・実装済み）

草（`kind=grass`）が手続き生成されるのに対し、`kind=model` のプロップは `model_path` の実アセット
（glTF/obj）をロードして、通常の deferred メッシュ G-Buffer パイプラインでインスタンス描画する。
ECS アクターには一切紐付けない**独立したインスタンシング経路**を新設した（`all_mcs`/`ModelComponent`
を経由しない）。単純に N 体のアクターを spawn する案は、シーンファイルの肥大・`.tscatter` との情報
二重化を招くため採っていない。

**リソース所有（`terrain_scatter_ops.rs::ScatterModelResource`）**
- `TerrainState.scatter_models: HashMap<usize /*prop 添字*/, ScatterModelResource>` が
  `{ model_path, cpu_model(Arc<Model>), gpu_model(GpuModel), batch(InstancedModelBatch), capacity }`
  を所有する（草の `grass_buffers` と対を成す）。GpuModel は ECS 由来ではないため
  `frame_renderer.rs` の 60 フレーム stale prune の対象外＝散布が変わるまで保持される。
- `scatter_model_failed: HashMap<usize, String>` はロード失敗プロップを記録し、毎フレーム同じ
  壊れたパスを読み直して警告を撒くのを防ぐ（`model_path` が変われば自動で再試行）。

**モデルのロードとキャッシュ（`rebuild_scatter_models_gpu`）**
- `model_path`（assets 相対/仮想/絶対）→ `asset_fs::normalize_asset_path` → `resolve` で実パス化し
  `loader::load_model` で読む（ECS モデル・ギズモと同じ解決規約）。GPU 化は `DrawContext::upload_model`。
- **プロップごとに 1 回だけロード**（散布インスタンス数ぶんロードしない）。`model_path` 単位でキャッシュし、
  props リロード（`ensure_terrain_props`）で `model_path` が変わったときだけ読み直す。ロード失敗は
  1 回だけ警告してそのプロップをスキップ（他プロップは描く）。

**インスタンス行列と dirty 連動**
- `ScatterInstance{pos, normal(tilt適用済みの up), yaw, scale}` から
  `ワールド = T(pos)·R(up=normal, yaw)·S(scale)` の 4x4 を CPU で組む
  （`scatter_instance_to_model_matrix`。`Transform::to_mat4` と同一の row-major・右手系規約。
  ユニットテストが直交正規性・右手系・up 整合・yaw 回転を固定）。
- 再構築トリガは**草と同じ `grass_gpu_dirty`**（散布データは草と共有の集合であり、フラグを分けると
  散布操作 5 か所すべてで二重管理になるため 1 本に集約）。`rebuild_scatter_models_gpu` は
  フラグを寝かせず、同フレーム後段の `rebuild_grass_gpu` がクリアする。ゆえに frame_renderer は
  **model 再構築 → 草再構築**の順で呼ぶ（逆順だと model 側が毎回スキップされる）。
- GPU アップロードは統合バッチ（`shared_model_batches`）と同規約: 容量が足りていれば
  `batch.update`（内部 `write_buffer`）で行列だけ差し替え、容量不足時のみ作り直す
  （`.max(SCATTER_MODEL_MIN_CAPACITY)`）。Edit/Play どちらでも反映される（草と同じ dirty 経路）。

**描画経路（`frame_renderer.rs`）**
- G-Buffer パス: 草の直後に `self.terrain.scatter_models.values()` を走査し、既存の
  `gbuffer::draw_gbuffer_indirect(gpass, &gpu_model, &batch, camera_bg, gbuffer_pipelines,
  meshlet=scatter_meshlet_draw, terrain_layers=None)` へ渡す。通常マテリアルで描くので deferred
  ライティング・SSAO・DDGI 受光が通常メッシュと同じく効く。
- **半透明パス（重要・葉の描画）**: `draw_gbuffer_indirect` は `alphaMode=Blend` プリミティブを
  スキップする（不透明 G-Buffer には焼けない）。木の葉・小枝（例: searsia の `*_leaves` /
  `*_twigs` マテリアルは `doubleSided=true` の **BLEND**）はこの経路では描かれない。したがって
  散布モデルを**透明モデル集合（`transparent_models`）へも加える**必要がある。これを怠ると散布木は
  幹（Opaque）だけが描かれ**葉が丸ごと消える**（症状: 「幹だけの枯れ木」）。`frame_renderer` は
  `transparent_models` をアクター（`shared_model_batches`）に続けて `scatter_models` の
  `(gpu_model, batch)` で拡張する。バッチはアクターと同じ `InstancedModelBatch` なので、透明描画
  （距離ソート／WBOIT）は `lod_visible_counts`・インスタンス行列をそのまま扱える。
  - なお葉は BLEND ＝ `cone_cull_enabled=OFF`（両面）なので、メッシュレットのコーン背面棄却とは
    無関係（`SEED_SCATTER_MESHLET` の ON/OFF で葉の有無は変わらない）。葉が出るかは純粋に
    「散布モデルが透明パスに載っているか」で決まる。
- **メッシュレットカリング（近景高ポリ木の描画コスト対策・実装済み）**: 散布モデルのバッチは
  `create_instanced_batch`（`enable_meshlet_cull=true`）で作られ、`frame_renderer` の
  メッシュレットカリング前処理ループにアクターと並んで載る（`prepare_meshlet_cull` →
  同一 compute パスで `record_meshlet_cull`）。G-Buffer 描画は `meshlet_active` を渡し、
  可視 LOD0 メッシュレットだけを間接コマンドで描く。これで近景の高ポリ散布モデル（例: searsia＝
  377k 三角形/本）がアクター（`shared_model_batches`）と同等に軽くなる。
  - **上限フォールバック**: メッシュレットカリング用コマンドバッファは
    `メッシュレット数 × インスタンス数 × 20B`。これが `min(max_buffer_size,
    max_storage_buffer_binding_size)`（＝既定 128MiB。**ストレージバインディング上限**が効く）を
    超える prim は `InstancedModelBatch::new` がスロットを確保せず、通常の `draw_indexed` へ自動
    フォールバックする（大量散布でもパニックしない）。searsia の重い prim（1367 メッシュレット）は
    総インスタンス **約 6,700 本**を超えると 128MiB を超えてフォールバックする（＝十数〜数千本の
    近景では効き、数千〜万本規模の遠景大量散布では自動でフォールバック＝チャンクカリング／距離 LOD が担当）。
  - env トグル `SEED_SCATTER_MESHLET=0` で無効化できる（既定 ON。before/after 計測を同一バイナリで
    取るための切替。描画結果は不変＝可視部分だけを描くカリング）。
- 影: `shadow_casters` に `(&gpu_model, &batch)` を push するだけでラスタのシャドウマップに乗る
  （`ShadowResources::record` は G-Buffer と同じ InstancedModelBatch の storage-instance を再利用する）。
  - **【重要な残課題】シャドウパスはメッシュレットカリングを使わない**。`shadow.rs` の `draw_caster` は
    全 LOD × 3 カスケード（`CSM_CASCADE_COUNT`）を `draw_indexed` で全三角形描画する（アクターも散布も同じ）。
    近景の高ポリ木では **シャドウ（実質 3 回描画）が G-Buffer（1 回）の約 3 倍**の三角形コストを占め、
    G-Buffer 側だけをメッシュレットカリングしても総 GPU 時間の削減は限定的（実測で bf 約 15〜25% 減）。
    木のコストを本質的に下げるには、シャドウパスのカスケード別メッシュレットカリング、または散布モデル
    シャドウの LOD バイアス（影の輪郭は低ポリで十分）が次段の本命。

**スコープ外（この段では未実装）**
- **TLAS/RT 登録**: 散布モデルは RT 影・RT 反射・RT-GI への**寄与**はしない（`rt_casters` に加えていない）。
  deferred ライティング上の受光・ラスタ影には乗る。RT 反射に木が映るのは将来。
- LOD/インポスター・木の風揺れ・接触インタラクション（プレイヤーが通ると草がなびく等）。
- **スキン付きモデル**を散布した場合はバインドポーズで描かれる（スキン compute を回さない）。

### 15.9b 第2段以降への持ち越し・既知の限界

- **接触インタラクション**（プレイヤーが通ると草がなびく／なぎ倒れる等）は未実装。
- **prop_id の並び替え耐性が無い**: `.tscatter` は prop_id を添字で保持するため、props.json の
  並び替えで既存散布データの指し先がずれる（§15.2）。

### 15.9c 散布のチャンク単位カリング（描画最適化・実装済み）

大量散布（実モデルの木を数千〜万インスタンス＋草 10 万本級）を **LOD 無し・視界外カリング無し**で
毎フレーム全ポリゴン描画すると、画面外・遠方のチャンクまで描き続けて 1fps 級に落ちる。
以前オブジェクト単位フラスタムカリングを撤去した経緯もあり、散布インスタンスは画面外・遠方も
毎フレーム全部描かれていた。これを **チャンク単位の距離＋フラスタムカリング（CPU・毎フレーム）** で
可視ぶんだけに絞る。

**判定単位＝チャンク AABB**
- 各地形チャンクは 16m 角（`chunk_extent`）。散布インスタンスは所有チャンク（`owning_chunk_coord`・
  XYZ 全軸）に属するので、チャンク格子座標から**ワールド AABB を厳密に**求められる
  （`chunk_world_aabb`）。
- AABB は **マージンを水平は小さく・上方向だけ樹高ぶん大きく**ふくらませる
  （草: 水平 `GRASS_MARGIN_HORIZ=1m`／上 `GRASS_MARGIN_UP=2m`、モデル: 水平
  `SCATTER_MODEL_MARGIN_HORIZ=4m`／上 `SCATTER_MODEL_MARGIN_UP=16m`）。**水平マージンを大きく
  取ると AABB がチャンク寸法の数倍に膨れ、カメラ背後や画面外のチャンクまで判定を通過して
  カリングがまったく効かなくなる**（実測で 24m 一律マージンだと可視=総数で全滅した）。木は
  「縦に高いが横幅は狭い」ので上だけ樹高分を確保し、水平は樹冠のはみ出し程度に留める。

**フラスタム平面の入手**（撤去した `test_aabb_frustum`／`compute_frustum_planes` は復活させない）
- メインカメラの `view_proj` から `gpu_resources::extract_frustum_planes()` で 6 平面を得る
  （frame_renderer が毎フレーム `saved_frustum_planes` として既に算出済み・メッシュレットカリングと共有）。
- AABB 判定は `gpu_resources::aabb_outside_frustum()`（p-vertex 法・偽陽性ゼロ＝見える物を消さない）と
  `aabb_distance_sq()`（最近点距離²）。距離閾値は `GRASS_CULL_DISTANCE`（既定 90m）/
  `SCATTER_MODEL_CULL_DISTANCE`（既定 220m）。`SEED_GRASS_CULL_DIST` / `SEED_MODEL_CULL_DIST` で
  上書き可（将来 props.json の per-prop フィールドへ移す前提の名前付き定数）。

**草（`draw_grass_culled`）** — バッファは詰め直さない
- `rebuild_grass_gpu` はプロップごとのインスタンス配列を**チャンク座標順**に詰め、各チャンクの
  連続区間（`GrassChunkSpan{aabb, first, count}`）を `GrassInstanceBuffer` に併記する。
- 描画時に各 span を視錐台＋距離テストし、**可視な連続区間だけ** `draw(0..96, first..first+count)` で
  発行する（隣接可視 span は 1 draw にまとめる）。バッファ本体は無変更＝毎フレームのアップロード無し。

**散布モデル（`cull_and_update_scatter_models`）** — 可視ぶんだけをバッチへ
- `rebuild_scatter_models_gpu`（散布が変わったときだけ）はプロップごとに**チャンク単位の span**
  （AABB＋事前計算ワールド行列）を `ScatterModelResource.chunk_spans` に構築する。バッチへの
  行列アップロードはここでは行わない。
- 毎フレーム `cull_and_update_scatter_models` が span を視錐台＋距離でふるい、可視チャンクの行列だけを
  `InstancedModelBatch::update` へ流す。バッチには可視インスタンスだけが載り、**G-Buffer パスも
  ラスタ影パスも可視ぶんだけ**を描く（両パスが同じバッチを共有するため）。容量は全数×2 で確保済み
  なので毎フレームの再確保（＝snatch lock 再帰の危険）は起きない。
- **影の割り切り**: バッチ共有ゆえ視錐台外チャンクはシャドウキャスタからも外れる。画面すぐ外の木の影が
  画面端で欠けうるが、AABB の水平マージンで縁を広げて緩和。シャドウ専用の広いカリングは将来。

**計測用フック**: `SEED_SCATTER_NOCULL=1` でカリングを無効化（カリング前挙動）。`SEED_PERF_TERRAIN` 有効時に
`[PERF terrain] scatter model cull: visible=N/M` を間引き出力（可視/総数）。

**スコープ外（この段では扱わない）**: per-instance GPU カリング（indirect draw）、インポスター
（hemi-octahedral ビルボード）。**距離 LOD メッシュ切替と遠景の密度低減は次段 §15.9d で実装済み**。

**実測（RTX 3060 Laptop・Vulkan・release・`SEED_TERRAIN_SMOKE`）**
- カリングが可視集合を正しく削減することを確認: クローズアップ視点で木 **1,789 → 1,092 本（−39%）**・
  草 **60,595 → 27,376 本（−55%）**、オーバービュー＋距離 55m で木 **3,914 → 2,916 本（−25%）**
  （`[PERF terrain] scatter model cull` ログ）。マージン修正前（水平 24m）は 1,789/1,789＝全数通過で
  カリングが効いておらず、この実測で不具合を発見・修正した。
- 高密度（木 6,119 本）では**カリング無しの全描画は GPU が device-lost（TDR）に陥りクラッシュ**
  （wgpu snatch lock 再帰パニック）。カリング有効時は可視ぶんに絞られて描画が成立する。
- **定常フレームタイムの before/after 数値は本自動計測環境では確定できなかった**: バックグラウンド
  ウィンドウのスワップチェーン present が重負荷フェーズで断続的にタイムアウト（`Render error: Timeout`）し、
  `bf`/`total` 指標がカリング有無どちらでも非決定的に汚染されるため（草のみの軽量フレームでは
  タイムアウト無しで安定計測できたことから、present 停止は描画負荷起因の環境要因と切り分け済み）。
  可視集合の削減（上記・決定的計測）と高密度でのクラッシュ回避が、描画コスト削減の直接証拠である。

### 15.9d 植生 LOD 第1段（距離 LOD メッシュ切替＋遠景の密度減衰・実装済み）

チャンク単位カリング（§15.9c）を通った**可視チャンク内**で、さらに距離に応じて (1) 描くメッシュを
簡略化し、(2) 描くインスタンスを間引く。俯瞰（全チャンクが視錐台内でフラスタムカリングが効かない構図）で
特に効く。最終形は 3 段 LOD（近=フルメッシュ／中=低ポリ／遠=hemi-octahedral ビルボード）だが、
本段はその第1段＝**距離 LOD メッシュ（フル/低ポリ）＋遠景密度減衰**まで（ビルボードは次段）。

**(1) 距離 LOD メッシュ切替 — 既存の glTF LOD をそのまま流用（新規実装なし）**
- glTF ローダは読み込み時に `Primitive.lod_indices`（LOD1≈50%／LOD2≈25%／LOD3≈10% の簡略化済み
  インデックスバッファ・頂点は LOD0 と共有）を生成し、`GpuModel`（`GpuPrimitive.lod_index_buffers`）へ
  アップロード済み。
- 散布モデルが使う `InstancedModelBatch` は**元から距離 LOD を実装**しており（`LOD_DIST_SQ`＝
  カメラ距離 10m/30m/60m 境界・`NUM_LODS=4`）、毎フレーム可視インスタンスをカメラ距離で LOD バケットへ
  振り分け、各 LOD の簡略インデックスで `draw_indexed` する。`cull_and_update_scatter_models` が
  可視行列を `batch.update(.., camera_pos)` へ流した時点で LOD 選択が働く。
- **したがって「フル/低ポリのメッシュ切替」は既存機構の流用で達成済み**であり、本段の新規コードは
  下記 (2) の密度減衰である。草は手続き生成（頂点バッファを持たない）なのでメッシュ LOD の概念が無く、
  代わりに密度減衰と WGSL 内のサブピクセル最小幅（§15.12）で遠景コストを抑える。

**(2) 遠景の密度減衰 — 距離帯で「先頭 kept 本だけ描く」**
- 距離帯（チャンク AABB 最近点距離）で描画本数を段階的に減らす: **近＝全数／中＝1/2／遠＝1/4**
  （`gpu_resources::density_kept_count`・除数 `DENSITY_DECAY_MID/FAR_DIVISOR`）。散布データ自体は変えず
  **描画時に間引く**。帯境界は名前付き定数＋環境変数上書き（将来 props.json の per-prop 化前提）:
  草 `GRASS_DECAY_NEAR`=30m/`GRASS_DECAY_MID`=55m（`SEED_GRASS_DECAY_NEAR/MID`）、
  モデル `SCATTER_MODEL_DECAY_NEAR`=70m/`_MID`=130m（`SEED_MODEL_DECAY_NEAR/MID`）。いずれもカリング距離の内側。
- **決定性・ちらつかなさ（最重要不変条件）**: 間引きは「先頭 kept 本を描く」プレフィクス方式で、
  `1/4 ⊂ 1/2 ⊂ 全数` と入れ子になる。カメラが近づくと描画個体は**増える方向にのみ**変化し、
  既存個体が別個体へすり替わらない（帯境界でのポップ／ちらつきが出ない）。`density_kept_count` は
  `(total, dist_sq)` だけの純関数でフレーム状態に非依存。
- **均一に薄くする（穴を空けない）**: プレフィクスが空間的に均一なサブセットになるよう、インスタンス列を
  チャンク内で **`scatter_thin_key(seed)`（splitmix32 撹拌）のハッシュ順**に並べておく（草は
  `rebuild_grass_gpu` のパック時、モデルは `gather_scatter_model_chunks` の span 構築時。どちらも散布が
  変わったときだけ走る安価な処理）。生成順のままだと先頭プレフィクスが空間的に偏り遠景に穴が空く。
- **草（`draw_grass_culled`）**: 可視 span ごとに最近点距離で kept を求め `[first, first+kept)` を描く。
  間引かれた span は次 span 先頭と連続しないため run が自然に途切れ、そのプレフィクスだけが描かれる
  （全密度で連続する可視 span は従来どおり 1 draw にまとめる）。戻り値＝実描画本数を計測へ返す。
- **モデル（`cull_and_update_scatter_models`）**: 可視チャンクごとに `span.mats[..kept]` だけを可視行列列へ
  連結して `batch.update` へ流す。少ない可視数がそのまま LOD 振り分け・描画コストへ効く。

**計測用フック**: `SEED_SCATTER_NOCULL=1` で減衰も無効（＝カリング前の全描画）。`SEED_PERF_TERRAIN` 有効時に
`[PERF terrain] grass draw: drawn=N/M`・`scatter model cull: visible=N/M` を 60 フレームに 1 回出力。

**実測（自動スモーク・`SEED_TERRAIN_SMOKE`＋一時 props で木=A.gltf・草+木を高密度散布・48 チャンク俯瞰）**
- 減衰 OFF（`SEED_SCATTER_NOCULL=1`＝旧挙動）: 木 **6,146/6,146**（全描画）。
- 減衰 ON（帯 near=8m/mid=20m へ調整し小フットプリントでも遠景帯へ入るようにした計測）:
  木 **6,146 → 1,726 本（−72%）**・草 **79,633 → 19,900 本（−75%）**。**全フレームで同一値**
  （42〜48 連続ログが完全一致）＝決定的でちらつかないことを確認。
- 近景相当（既定帯・64m フットプリントは全域が near 帯）: 木 **6,146/6,146＝全密度維持**（近景が痩せない）。
- パニック／device-lost／snatch 再帰は全実行で発生せず。**定常 fps の純数値**はこの自動環境
  （エディタ非接続＝FPS の IPC 送出先が無い／バックグラウンド present 未確定）では確定できないため、
  §15.9c と同様に**決定的な描画本数削減**（上記）を描画コスト削減の直接証拠とする。草の描画本数ログは
  レンダーパス記録中（`gpass`）に集計しており、フレームが実際に記録・submit されている証拠でもある。

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

**散布モデル（`kind=model`）実測（`SEED_TERRAIN_SMOKE`・debug ビルド・4×4 チャンク≈64m²・
`models/A.gltf`（72 頂点）を density 0.4/scale 3〜6 で散布）**:

| 項目 | 実測値 |
|---|---|
| モデルアセットのロード（`load_model`＋GPU 化）| 約 11 ms（プロップごとに一度きり。`model_path` 変更まで再ロードしない） |
| 散布モデル GPU 再構築（`rebuild_scatter_models_gpu`）初回 | 18.19 ms（1,848 インスタンス・行列生成＋アップロード） |
| 同・2 回目以降（容量内・行列差し替えのみ） | 2.48 ms（1,789 可視インスタンス） |

同一シーンで草＋モデル合わせて 49,651 インスタンスを散布・描画しても破綻せず、
`[PERF terrain]` ログにロード失敗は出なかった（`SEED_PERF_TERRAIN=1` で内訳ログが出る）。
モデル 1 体あたりの頂点数が大きいほど GPU 側は重くなるため、実運用では低ポリ木＋控えめな
density を推奨する（数千インスタンス × 数百〜数千頂点が現実的な上限の目安。LOD/インポスターは
持ち越し）。

### 15.9e 地形チャンク**メッシュ本体**のチャンク単位カリング（描画最適化・実装済み）

§15.9c/§15.9d は「地形の上に載る散布物（草・木）」の描画最適化だった。本節は**地形メッシュ本体**（各
チャンクの marching cubes メッシュ＝`terrain://` の独立 `ModelComponent` バッチ）の描画カリングである。

**症状**: 草木を一切散布せず、16×16 チャンク（水平 256＋高さ層）・分割数 32 の地形を**置いただけ**で
30fps を切る。原因は「地形チャンクは 1 チャンク＝独立 `ModelComponent` バッチであり、視界外・背後の
チャンクまで毎フレーム全ポリゴン描画していた」こと。

- **オブジェクト単位フラスタムカリングは 00dbe29 で撤去済み**（通常メッシュの画面端ポップ・誤棄却を嫌ったため）。
- **メッシュレットカリングは地形に効いていない**。地形メッシュの `GpuModel` はメッシュレット記述子
  （`meshlet_desc_buffer`）を持たず、`prepare_meshlet_cull` は地形バッチをスキップする（実測でも
  `[PERF f=] meshlet=0考慮`）。さらに `MULTI_DRAW_INDIRECT_COUNT` 非対応 GPU ではメッシュレットカリング
  自体が無効。→ **どちらの経路でも 256+ チャンクの全三角形が毎フレーム描かれていた**。

**対策＝チャンク単位フラスタム＋距離カリング（CPU・毎フレーム）**。散布（§15.9c）と同じ仕組みを
メッシュ本体へ流用する。frame_renderer が統合バッチ更新の直後に、`terrain://` で始まるバッチだけを対象に
視錐台外・遠方のチャンク path 集合（`terrain_culled`）を作り、**G-Buffer 描画ループ・フォワード不透明
描画ループ・メッシュレットカリング前処理**からスキップする。

**判定 AABB ＝ バッチのワールドメッシュ AABB（`InstancedModelBatch::world_bounds`）**
- 各チャンクバッチの `update()` が全インスタンス（地形は 1 チャンク＝1 インスタンス）のワールド AABB を
  `world_aabbs` にキャッシュ済み。これは頂点ローカル AABB をインスタンス行列で変換した**実ジオメトリを
  厳密に包む箱**であり、チャンク格子から求める `chunk_world_aabb` より**さらにタイト**。マージンは一切不要
  （箱が視錐台外なら描画物は 1 頂点も視錐台に入らないことが p-vertex 法で保証される）。空メッシュチャンク
  （全 AIR/全 SOLID）は `gpu_model=None` で統合バッチに載らないため、そもそも対象外（描かれない）。

**判定関数**: `gpu_resources::chunk_culled_by_camera(planes, camera_pos, min, max, cull_dist_sq)`
＝ `aabb_outside_frustum()`（p-vertex 法・**偽陽性ゼロ**）‖ `aabb_distance_sq() > cull_dist_sq`。平面は
メインカメラ `view_proj` から既算出の `saved_frustum_planes`（メッシュレット・草カリングと共有）。距離閾値は
`TERRAIN_CHUNK_CULL_DISTANCE`（既定 **4000m**＝地形は草木より遠くまで見えるべきなので緩め。16×16≈256m 角
では距離では 1 枚も落ちずフラスタムのみが効く。`SEED_TERRAIN_CULL_DIST` で上書き可）。

**メッシュレットカリング compute との協調**: 視錐台外の地形チャンクは前処理をスキップしつつ
`InstancedModelBatch::reset_meshlet_cull()` でスロットを非アクティブへ戻す（前フレームの `bind_group`/
`workgroups>0` が残って `record_meshlet_cull` が無駄 dispatch するのを防ぐ）。

**影パス（シャドウマップ）は現状維持**。影キャスタの棄却は**ライト視点の視錐台**で行うべきで、メインカメラ
視錐台で棄却すると「カメラ視界外でも視界内へ影を落とすチャンク」が消えて影が欠ける。今回はメインカメラの
G-Buffer/不透明パスのカリングを優先し、影パスは触らない（ライト視錐台での地形影カリングは次段の申し送り）。

**適用範囲＝地形バッチのみ**。`terrain://` プレフィクスで判定するため、通常アクター・散布の描画挙動は一切
変わらない（撤去された旧オブジェクトカリングの誤棄却問題は再発しない）。地形バッチはマテリアル
オーバーライドを持たないため `batch_key == source_path == "terrain://<scene>/chunk_X_Y_Z"`。

**計測用フック**: `SEED_TERRAIN_NOCULL=1` でカリング無効（＝全チャンク描画＝導入前挙動）。
`SEED_PERF_TERRAIN` 有効時に `[PERF terrain] chunk draw: drawn=N/M` を 60 フレームに 1 回出力。
スモークは `SEED_SMOKE_CHUNKS=16`（16×16 へ拡大）・`SEED_SMOKE_NO_SCATTER=1`（散布なし＝地形のみ）・
`SEED_SMOKE_FPV=1`（一人称視点）を追加してこの計測を実機で回せる。

**実測（`SEED_TERRAIN_SMOKE`・debug ビルド・16×16・地形のみ・分割数 32）**
- **俯瞰**（全チャンクが視錐台内の構図）: カリング ON でも `drawn=276/276`（空メッシュを除く実描画チャンクが
  276。視界内は 1 枚も落とさない＝正しい）。
- **一人称**（`SEED_SMOKE_FPV=1`）: `drawn=73/276`（**−73.6%**）。NOCULL は `drawn=276/276`。
  CPU 側 main_pass 記録は約 3.8ms → 2.9ms（−約 25%）。GPU 側のラスタライズ削減（描画チャンク 276→73）は
  CPU タイマには表れないが、視界外 203 チャンク分の全三角形描画が消える直接効果である。
- **視界内が欠けないことの証拠**: カリング ON と NOCULL の提示フレーム PNG（`SEED_SCREENSHOT_*`）が
  **全フレームでバイト単位一致（同一 MD5）**。＝カリングは可視ピクセルを 1 つも変えない（偽陽性ゼロ）。
- fps 純数値はこの自動環境（エディタ非接続・バックグラウンド present）では確定できないため、§15.9c/d と同様に
  **決定的な描画チャンク数削減＋main_pass 記録削減＋出力バイト一致**を描画コスト削減の直接証拠とする。

**次段の申し送り（今回スコープ外）**: (1) 地形 LOD（遠いチャンクを低ポリ MC・またはメッシュレット記述子の
付与で地形にも GPU メッシュレットカリングを効かせる）。(2) 影パスのライト視錐台による地形影カリング。
(3) そもそも 256 チャンクは多いのでストリーミング／ページング。まずは本節のカリングで「見えない地形を
描かない」ことで大幅改善する。

### 15.9f 散布モデルの RT 影（TLAS 登録・実装済み）

**症状**: レイトレ影（`shadow=rt`）構成で、散布した `kind=model` プロップ（木）の影が地面に落ちなかった。
原因は「散布モデルが RT の TLAS に未登録」だったこと。ラスタのシャドウマップには載っていた
（§15.9 の `shadow_casters` に追加済み）が、RT 影は TLAS のみを引くため、影のレイが木を素通りしていた。

**修正（`frame_renderer.rs` の RT キャスター収集）**: RT の TLAS 構築（`rt.prepare_and_build`）へ渡す
`rt_casters` に、ECS アクター（`shared_model_batches`）だけでなく散布モデル（`terrain.scatter_models`）も
加える。散布モデルはワールド行列を供給する `InstancedModelBatch` を持つため、通常メッシュと同じ
`(source_path, &GpuModel, &InstancedModelBatch)` の 3 つ組で登録できる。

- **BLAS の共有とキャッシュ**: キャスターキーは `"scatter://{model_path}"`。同一モデルの複数プロップ・
  数百〜数千インスタンスは**同じキー＝同じ BLAS を共有**する（`rt_shadow.rs` の `BlasKey` キャッシュ）。
  高ポリ木でも BLAS はプリミティブ数ぶんだけ（1 種 = 数個〜数十個）で済む。ECS アクターの `batch_key` とは
  名前空間を分け、frame_renderer の stale prune（`prune_source_paths`）が散布 BLAS を巻き添え解放しない。
- **BLAS 分割ビルドに乗る**: 新規 BLAS は既存の `MAX_BLAS_BUILDS_PER_FRAME=8` 分割に従う（木の初回登録・
  地形再構築でキャッシュが一斉に空になっても、1 submit の GPU 占有を TDR しきい値未満に保つ＝
  デバイスロスト／snatch 再帰パニックの回避。実機ログ `BLAS 分割ビルド: … 後続フレームへ繰り越し` で確認）。
- **登録数の抑制（可視・近傍に絞る）**: 散布モデルの `batch` は毎フレーム `cull_and_update_scatter_models` で
  「可視チャンクのインスタンスだけ」に更新済み。`rt_enumerate` は `num_instances`（＝可視数）のみ列挙するため、
  既存の視錐台＋距離カリング済み可視集合をそのまま TLAS 登録へ流用して数を抑える。総数が
  `MAX_RT_INSTANCES=4096` を超えた分は `prepare_and_build` 側で警告付きクランプ（超過キャスターは影を落とさない）。
- **葉のアルファ（申し送り）**: 葉が `AlphaMode::Blend/Mask` の場合、影レイのマスクは `0x02`（`RT_MASK_NON_OPAQUE`）
  となり基本の不透明影レイからは除外される（幹など不透明部のシルエット影は落ちる）。Mask（アルファテスト）は
  バインドレス対応 GPU では色付き影の第 2 クエリ（`BINDLESS_FLAG_MASK`）で葉の形にアルファ抜きされ得る
  （メガバッファ登録済みが条件）。Blend の葉のアルファ抜き影は未対応（幹シルエットで代替。§R8 TODO）。
- **編集連動**: 散布が変わると `grass_gpu_dirty`→`rebuild_scatter_models_gpu` でバッチが作り直され、TLAS は
  内容シグネチャ変化で自動再構築される（`prepare_and_build` の静止スキップ判定が変換・追加削除を検知）。

**実機検証（`SEED_TERRAIN_SMOKE`＋一時 props で木=`assets://models/A.gltf`・4×4 チャンク俯瞰・RTX 3060・
Vulkan・debug・`shadow=rt`）**:
- `[SEED FEATURES] shadow=rt`、`[SEED RT] インラインレイトレ: 対応`。
- `[SEED RT] BLAS 構築: scatter://assets://models/A.gltf mesh#0 prim#0`（散布モデルの BLAS が 1 個だけ構築＝
  239 インスタンスで共有）。BLAS 分割ビルドが 8 件/フレームで消化されるログを確認。
- TLAS インスタンス数 `tlas=…ms(skip/263inst)` ＝ **地形チャンク 24＋散布木 239＝263**（`skip/0inst` でなく
  散布木ぶんが確かに登録されている）。可視カリング `scatter model cull: visible=239/239`。
- スクリーンショット（`scatter_rt_shadow_proof.png`）で、緑の地面に散布モデルの影が落ちることを目視確認。
- **パニック／device-lost／snatch 再帰／上限超過警告は発生せず**（1,787 インスタンスの高密度散布でも同様）。

**残課題（申し送り）**: (1) Blend の葉のアルファ抜き影（現状は不透明シルエット）。(2) 登録上限
`MAX_RT_INSTANCES=4096` を超える大量散布では遠景側の一部が影を落とさない（可視カリングで実用上は回避されるが、
超広域で多数の木種を近接配置すると到達し得る＝クランプ発火）。(3) スキンメッシュは従来どおり RT 対象外。

### 15.10 「視界外で 20ms」調査 — 毎フレーム走る地形 CPU 処理は既にボトルネックではない（計測結論）

**背景**: 「16×16（768 チャンク）・分割 32 の地形を置き、カメラを地形から外した（描画ほぼゼロの）ときでも
50fps 程度（≈20ms）」という症状の切り分け。容疑は (1) `merge_map` の毎フレーム再構築、(2) `tick_terrain_lod`
の全 768 チャンク距離計算、(3) チャンク単位カリング判定、の 3 つだった。**実機 [PERF] で支配項を確定した
結果、これら 3 つはいずれも支配項ではない**（合計でも 1ms 未満）。

**計測手順（再現可能）**: `SEED_TERRAIN_SMOKE=1 SEED_SMOKE_CHUNKS=16 SEED_SMOKE_NO_SCATTER=1 SEED_PERF_LOG=1`
に加え、**視界外構図を厳密に再現する `SEED_SMOKE_LOOKAWAY=1`**（フットプリント外の -Z 側に立ち地形と反対を
向く。全チャンクが視錐台背後→ `drawn=0/276`）を追加した。**重要**: Play/スタンドアロンは**ゲームカメラ**で
描画されデバッグカメラの向きが効かない（`frame_renderer` のカメラ選択が Play=ゲームカメラ / Edit=デバッグ
カメラ）。視界外構図を実際に描くには **`--mode=edit`** で起動すること（そうしないと `drawn` はカメラ向きに
反応しない）。

**実測（release・16×16・分割 32・地形のみ・Edit・視界外 `drawn=0/276`）**
- `merge`（`merge_map` 再構築＋全統合バッチ update 記録）= **0.2〜0.5ms**。§Fix B（296ac8d の静的地形 update
  スキップ）で既に解消済み。視界外・視界内で差はほぼ無い。
- `tick_terrain_lod` の**実処理 = 約 0.05ms**（LOD 判定ループ 0.04ms＋remesh 0ms＝カメラ静止で `n_changes=0`）。
  この関数は `perf_t_total` 開始**前**に走るため [PERF total] に含まれない“死角”だが、内部を分割計測すると
  実 CPU は 0.05ms しかない。関数呼び出しの外側で稀に 4〜9ms の壁時計が観測されるのは、**フレーム境界の
  スケジューラ/present 待ち**がそこに吸収されて見えるだけで、LOD 計算の CPU コストではない（バックグラウンド・
  非フォーカス時は `pace_frame_if_unfocused` が 30fps へ制限するため特に出やすい）。
- カリング判定ループ（276 バッチのフラスタム＋距離判定）は安価。視界外で正しく `drawn=0/276` になる。
- 視界外（`drawn=0`）と視界内（`drawn=58/276`）で **[PERF total] はほぼ同じ**（〜7〜11ms）。＝残りの毎フレーム
  コストは**地形チャンク描画数に依存しない**（フルスクリーンの deferred＋RT 影＋RT GI と present バックプレッ
  シャ＝`bf`。散布ありでは `bf` が 3〜6ms へ増える）。

**結論**: 視界外の毎フレームコストの支配項は**フルスクリーン GPU パイプライン（deferred/RT 影/RT GI）と
present/スケジューラ待ち**であり、**視界に依存しない性質**を持つ。地形チャンクの CPU 処理（merge_map・LOD・
カリング）は 296ac8d 時点で既に 1ms 未満まで最適化済みで、これ以上のキャッシュ化（merge_map/LOD/AABB の
変化時のみ再計算）を入れても数十〜数百µs しか縮まず、frame 全体（数〜十数 ms）には現れない。よって
**本調査ではキャッシュ化は実装しない**（非ボトルネックへの最適化を避ける）。

#### 草インスタンスバッファの単一バインド上限クランプ（400万本 panic の解消）

**症状（実機・確定）**: 散布あり 16×16（768 チャンク）で高密度に草を撒くと草インスタンスが
約 400 万本（実測 4,171,738 本）に達し、`grass_instance_bg`（草インスタンスバッファのバインド）が
単一 storage バインド上限 `max_storage_buffer_binding_size`（既定 128MB=134217728）を超えて
wgpu 検証エラーで panic する。原因は、草バッファがプロップ種別ごとに 1 本の storage 配列で、
バインドグループへ `as_entire_binding()`（＝バッファ全域）で渡すため、**バインド範囲＝バッファ全サイズ**
であり、これが 400万本 × 48B ≒ 192MB で上限を突破していたこと（さらに `GRASS_CAPACITY_GROWTH_FACTOR`
の 2 倍確保が容量を押し上げる）。前回のメッシュレット cmd バッファ・skin 行列バッファと同種の
「インスタンス数比例バッファが上限超過」問題。

**修正（クランプ方式・確実に panic を防ぐ）**: 総本数を「単一バインドに収まる最大本数」
`max = max_storage_buffer_binding_size / stride`（128MB / 48B = 2,796,202 本）で頭打ちにする。
- `renderer/grass_gbuffer.rs`: `grass_max_instances_for_limit`／`max_grass_instances`／
  `clamp_instances_and_spans` を新設。`GrassInstanceBuffer::new`／`update` は device 上限から
  求めた max で**バッファ容量そのものを頭打ち**にする（容量再確保の 2 倍化も `.clamp(_, max)`）ので、
  確保するバッファが 128MB を超えることは**構造的に**起こらない（どの経路から作られても panic しない防御）。
- `terrain_scatter_ops.rs::rebuild_grass_gpu`: バッファへ詰める前に、プロップごとに本数と span を
  `clamp_instances_and_spans` で max 以内へ切り詰める（span 側も「max を跨ぐ span は count を詰め、
  以降は捨てる」で整合させる。ずれると `draw_grass_culled` が範囲外を描く）。切り捨てはチャンク座標
  ソートの**末尾**（＝最も座標の大きいチャンク群）から起きる。切り詰め発生時は警告ログを 1 行出す。

**実機検証（`SEED_TERRAIN_SMOKE=1 SEED_SMOKE_CHUNKS=16` ＋高密度 props（density=120）・RTX 3060・debug）**:
- 散布 4,171,738 本 → 草プロップ #0 を 2,796,202 本へクランプ（1,375,500 本を除外）。**panic せず**
  `grass gpu rebuild: 2796202 instances` で確定。俯瞰／FPV とも複数フレーム present 継続（screenshot 取得成功）。
- FPV（`SEED_SMOKE_FPV=1`・草原の中に立つ構図）で `grass draw: drawn=257601/2796202`。クランプ後の
  span でカリング描画が正しく成立し、GPU 検証エラーは出ない。近景の草原が正常に描画される。
- 通常規模（本数が max 未満）ではクランプは無発火（`clamp_instances_and_spans` が即 return）＝退行なし。

**残る同種リスク（申し送り）**: 散布モデル（`kind=Model`）の skin 行列バッファ／統合バッチ
（`InstancedModelBatch`）も理屈上はインスタンス数比例で `max_storage_buffer_binding_size` を超えうる。
本修正は草バッファのみ。木を超高密度に撒く運用が出たら同様のクランプ／分割を入れること。
草をクランプではなく**全数描く**必要が出た場合は、バッファ分割（複数バインドで 1 バインド 128MB 未満）
または可視ぶんだけ確保（カメラ依存の毎フレーム再構築）が次の選択肢（本修正は最小コストのクランプを採用）。

**次に効く方向（申し送り・いずれも本調査スコープ外の GPU/散布側）**:
(1) フルスクリーン deferred/RT 影/RT GI のコスト自体（解像度スケール・RT の間引き・GI の更新頻度低減）。
(2) 散布/草の視界外時の GPU 処理（カリングで `drawn=0` でも buffer 保持・cull compute が走るなら間引く）。
(3) present バックプレッシャ（`bf`）＝GPU 律速の指標。CPU 側の地形処理をこれ以上削っても `bf` は縮まない。

`SEED_SMOKE_LOOKAWAY=1`（`--mode=edit` 併用）は視界外構図の再計測用フックとして常設した。

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

### 15.13 アクタ散布（`kind=actor`・プレハブから実アクタを生成）

散布プロップの第 3 の種別。散布点に GPU インスタンスではなく**シーンの実アクタ**
（コライダー付きモデル・アイテム・スクリプト持ちオブジェクト等）をプレハブ（.actor）から生成する。
実装は `terrain_scatter_actor_ops.rs`（統合層）。

**設計の要点（第 1 段の割り切り）**:

- **散布点は `.tscatter` に保存しない**。ルール散布・ブラシ散布の生成直後に
  `kind=actor` のインスタンスを横取り（`partition_actor_instances`）してアクタ生成へ回し、
  生成されたアクタが `.scene` に永続化される（二重生成を構造的に防ぐ）。
- 生成アクタは Hierarchy の専用グループフォルダ **「散布アクタ」** 配下に入る。
  フォルダ・生成アクタは `ActorData.scatter_prop_id`（フォルダは `__scatter_group__`、
  アクタは生成元プロップ ID）で識別する。手動配置のアクタは常に None。
- 生成アクタは `prefab_source` を持つ**通常のプレハブインスタンス**になるため、
  プレハブ本体を編集して「全プレハブ更新」を掛ければ散布済みアクタにも反映される。
- **ルール散布 = 敷き直し**（そのプロップの既存生成アクタを全消しして再生成。Undo 可能）。
  **ブラシ散布 = 追加**、**消去ブラシ = 半径内の全散布アクタを削除**
  （草・モデルの意味論と同じ。ブラシ経路は高頻度のため Undo 記録しない）。
- 配置は prefab_ops と同じ **delta = M_new · M_file⁻¹ をサブツリーへ適用**する方式
  （プレハブファイルはルート基準・ワールド空間行列で保存されているため）。
  yaw は Transform の Y 回転（度）へ加算、scale はプレハブ自身のスケールへ乗算。
- 上限 `SCATTER_ACTOR_MAX_PER_PROP = 512` 件/プロップ。超過は生成せず件数を警告ログへ
  （黙って切らない）。アクタは ECS エンティティ・物理・ヒエラルキー行を伴い、さらに現状は
  アクタごとにモデルの GPU バッファが複製される（`GpuModel` の共有キャッシュが無い）ため
  VRAM を考慮して控えめに抑えている。共有キャッシュ導入後に引き上げを検討する。
- プレハブの ActorData は `App.scatter_prefab_cache` にキャッシュされる（ブラシ 1 ストロークでの
  再読込防止）。ルール再散布の開始とプレハブ再展開系の操作でクリアされる。
- **地形編集後の再接地（restick）は対象外**。地形を変えたら再散布で追従する。

エディタ側は種別コンボに「アクタ（プレハブ）」を追加し、`prefab_path`（.actor）を
FileRefBuilder で参照設定する（`TerrainPropsDocument.PrefabPath` / props.json の `"prefab_path"`）。

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
