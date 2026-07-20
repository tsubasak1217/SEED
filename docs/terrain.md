# Terrain（地形エディタ）設計メモ — T1 ランタイム基盤

本書は SEED の地形（terrain）機能 **T1: ランタイム基盤** の設計正典である。
ボクセル SDF ＋ marching cubes による洞窟対応の破壊可能地形をランタイム側に実装した段階の記録であり、
データ構造・チャンク境界同期・tvox フォーマット・IPC 仕様・T2/T3 の拡張余地をまとめる。

> スコープ外（T1 では未実装）: エディタ UI（モード/ツールバー/マウス入力）、マテリアルレイヤブレンド、
> triplanar、木/草散布、LOD。これらは T2/T3 で追加する（末尾「拡張余地」参照）。

---

## 1. 全体像

- **表現**: ボクセル SDF（符号付き密度場）。ハイトマップではないため洞窟・オーバーハングを表現できる。
- **メッシュ化**: marching cubes（CPU）。頂点法線は密度場の勾配。
- **単位**: ボクセル 0.5 m、チャンク 16 m 角（= 32 セル／軸、サンプル 33/軸）。すべて `TerrainSettings` で設定化。
- **密度規約**: `density < iso_level` ⇒ SOLID（内部）、`density > iso_level` ⇒ AIR（外部）、`== iso_level` が表面。
  平坦地面は `density(p) = p.y`（y=0 が地表、下が SOLID）。
- **ヒエラルキー**: シーンルート直下に空アクター `terrain` → その中にチャンク毎の空アクター `chunk_X_Y_Z`
  → さらに中に地形メッシュアクター（`ModelComponent` を持つ）。将来ここに木・草アクターを足す。

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

### tvox フォーマット（v1・リトルエンディアン）

| オフセット | 型 | 内容 |
|---|---|---|
| 0 | u8[4] | マジック `"TVOX"` |
| 4 | u32 | バージョン（現在 `1`） |
| 8 | i32 | チャンク座標 x |
| 12 | i32 | チャンク座標 y |
| 16 | i32 | チャンク座標 z |
| 20 | u32 | samples_per_axis（例 33） |
| 24 | f32 | voxel_size（m） |
| 28 | f32 × N | 密度サンプル（N = samples_per_axis³, row-major） |

ヘッダ 28 バイト。`read_chunk` は magic/version を検証し、`TvoxError{BadMagic, BadVersion, Truncated, DimMismatch}` を返す。
保存先: `<assets_root>/terrain/<scene>/chunk_X_Y_Z.tvox`（`std::fs::create_dir_all`＋`std::fs::write`。読みは `asset_fs::read_bytes` で PAK 対応）。

---

## 9. IPC 仕様（エディタ実装フェーズへの引き継ぎ）

SEED の IPC は **行指向のプレーンテキスト**（名前付きパイプ、`\n` 区切り、`COMMAND:arg1,arg2,…`、SCREAMING_SNAKE_CASE）。
serde/JSON ではない。地形コマンドは以下の 3 つ。エディタ側はこの文字列を送るだけでよい。

| コマンド（送信） | 引数 | 応答（受信） |
|---|---|---|
| `TERRAIN_INIT` | なし | `TERRAIN_INIT_OK` |
| `TERRAIN_BRUSH:{op},{screen_x},{screen_y},{radius},{strength}` | `op`:u32（0=Add,1=Subtract,2=Smooth,3=Flatten）／他 f32。`screen_x/y` はビューポート左上原点のピクセル座標 | ヒット時 `TERRAIN_BRUSH_OK:{hx},{hy},{hz}`（ワールドヒット点）／非ヒット `TERRAIN_BRUSH_MISS` |
| `TERRAIN_SAVE` | なし | `TERRAIN_SAVE_OK:{count}`（保存チャンク数）／`TERRAIN_SAVE_ERROR:{msg}` |
| `TERRAIN_BRUSH_PREVIEW:{screen_x},{screen_y},{radius}` | 全 f32。ホバー中（非押下）に送る | 応答なし（高頻度・ホバー用）。レイマーチのヒット点にブラシ半径のワイヤスフィアを描く |
| `TERRAIN_BRUSH_PREVIEW_OFF` | なし | 応答なし。プレビュー（ワイヤスフィア）を非表示にする |

- `TERRAIN_INIT`: terrain ルート＋初期平地（`ground_chunks_x × ground_chunks_z × [y_min..=y_max]` チャンク、y=0 に地面）を生成。
- `TERRAIN_BRUSH`: カーソル位置からカメラレイを作り、グローバル密度場を SDF レイマーチして最初の AIR→SOLID 交差点を求め、
  そこへ球ブラシを適用。ヒットが無ければ無視（MISS）。適用後、影響チャンクを VRAM 安全手順で再アップロードし dirty 化。
- `TERRAIN_SAVE`: 全チャンクの tvox を書き出す（シーン保存とは別口。シーン保存は既存 `SAVE_SCENE`）。

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
| ツール選択（盛る/掘る/均す/平坦化） | トグル（`RadioButton` 群）。選択状態をアクセント色で表示。`op` = 0/1/2/3 に対応 |
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

### ヒエラルキーの器：親子付け vs グループフォルダ（調査結果・未着手）

**現状**: `handle_terrain_init` は地形を **アクターの親子付け**（`terrain` ルート →
`chunk_X_Y_Z` フォルダアクター → `mesh` アクター）で構築する。各チャンクメッシュは
**それぞれ固有ジオメトリを持つ独立した ModelComponent**（1 アクター 1 メッシュ）である。

**「グループフォルダ」機構の実体**（`CREATE_GROUP` / `GroupMeta` / エディタの `is_group`）:
グループフォルダは `ModelComponent.group_meta`（`GroupMeta{id,name,parent}`）として保持され、
**単一 ModelComponent 内の複数インスタンス**（`instance_meta[].parent` がグループ id を指す）を
整理する仕組みである。適用先は「1 MC＋多数インスタンス」パラダイム（アクター編集タブ等の
world_line）で、`CreateGroup` ハンドラも `find_component_in_world_line_mut::<ModelComponent>` で
アクティブ world_line の代表 MC に対して働く。シーン（WL0）の `do_send_hierarchy` は
アクターツリーを走査し `is_group=false` 固定で送るため、**独立したシーンアクターをグループ
フォルダに束ねる経路は存在しない**。

**結論（停止・別判断）**: 地形チャンクは各々別メッシュの独立アクターであり、単一 MC の
インスタンス群ではないため、**既存のグループフォルダ機構では器を置き換えられない**。
アクター単位のフォルダ（`Transform` 親子を持たない整理専用ノード）は現状の `ActorKind`
（Actor3D/Actor2D のみ）にも存在せず、これを新設するのは「フォルダ機構の新設」＝別判断のため
本作業では**未着手**とする。将来対応する場合の設計候補:
- `ActorKind::Folder`（またはアクターの `is_folder` フラグ）を追加し、`do_send_hierarchy` の
  `is_group` をこのフラグから供給。フォルダは Transform を持たず、ドラッグ伝播
  （`apply_delta_to_actor_subtree`）の対象外にする。
- 移行: `handle_terrain_init` の冪等除去（`TERRAIN_ROOT_NAME` サブツリー despawn）は流用でき、
  ルート/チャンクの器をフォルダ種別で作り直す。既存 `.scene`（アクター親子版）はロード時に
  `terrain` ルートを検出したらフォルダ構造へ作り直す（または再初期化を促す）。
- チャンクメッシュはフォルダ直下にフラット配置し、`mesh_tf.position=world_origin`・
  `instance_mats` はワールド空間のまま（親子 Transform 合成に依存しない現構造をそのまま活かせる）。

## 12. 拡張余地（T2 / T3）

- **T2 マテリアル**: triplanar ＋標高/傾斜ベースのレイヤブレンド（草/岩/砂）。現在の単一デフォルト Material を置換。
  `terrain_mesh_build.rs` の Material 生成部と `Vertex.color`/uv の使い方を差し替えるだけで載る設計。
- **T2 tvox 拡張**: voxel_size/samples をロード後 `TerrainState` へ反映（§8 の限界解消）。i8 量子化オプション（メモリ/ディスク 1/4）。
  マテリアル ID/レイヤ重みをサンプル毎に持たせる場合はフォーマット版数を上げて後方互換ロードを追加。
- **T2/T3 散布物**: チャンクフォルダ内に木・草アクターを追加（ヒエラルキーは既に対応）。地表サンプルから配置点を算出。
- **T3 LOD/ストリーミング**: 遠距離チャンクの粗メッシュ（セル間引き）・視錐台外チャンクのアンロード。
  再メッシュは既に「影響チャンクのみ差し替え」なので、距離別メッシュキャッシュを足す方向で拡張できる。
- **編集の連続化**: エディタ側でドラッグ中に毎フレーム `TERRAIN_BRUSH` を送る（`dt` はフレーム時間）。undo/redo は
  編集前後のチャンク密度スナップショット（または tvox）を既存 Undo 機構に載せる。
