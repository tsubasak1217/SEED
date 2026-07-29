# 水システム・インタラクションフィールド ロードマップ（正典）

2026-07-28 のユーザー合意に基づく設計方針と実装段階。方式の再検討は明示的な要求があったときのみ行う。
3層モデル（`rendering_flow.md` §5）との関係も本書で定義する。

---

## 1. 設計方針（合意事項）

### 1.1 WaterVolume — 水は1つのデータ概念に統一する

海・池・川は別々の仕組みではなく、同じ `WaterVolume` コンポーネントのパラメータ違いとして表現する。

| 種類 | 領域 | 水面 | 流れ |
|---|---|---|---|
| 海 | 無限（ワールド全域） | 一定高さ（海面レベル） | なし〜波のみ |
| 池・湖 | AABB / 多角柱 | 一定高さ | なし |
| 川 | スプライン＋幅 | スプラインに沿って下る | スプライン接線方向の流速 |

`WaterVolume` が提供する**問い合わせは3点のみ**（この契約の上に描画・物理・遊泳・音のすべてを建てる）:

1. この点は水中か
2. この点の水面高さは
3. この点の流速は

この3点を最初から正式 API にしておくこと。描画都合の暫定実装にすると遊泳・水中敵AI・酸素ゲージ等で作り直しになる。

> **実装済み（W1）**: 正式 API は `runtime/src/engine/water/query.rs` の `WaterQuery`
> （`is_underwater` / `surface_height_at` / `flow_at`）。描画はこの API とは独立に
> `ResolvedWaterVolume`（`water/resolved.rs`）だけを見る。遊泳・浮力・水中ポストは
> **`WaterQuery` のみ**を呼ぶこと。

### 1.2 浸水（建物への流れ込み）— 連通ボリューム方式（水位グラフ）

```
部屋A(水位2.0m) ──扉(開口0〜2m)── 部屋B(水位0.3m) ──階段穴── 地下室(水位1.8m)
```

- **ボリューム** = レベルデザイナが配置する領域＋現在水位（スカラー1個）
- **リンク** = ボリューム間の開口（扉・窓・穴）。位置と断面積を持つ
- **計算** = 毎ステップ「水位差 × 開口面積 × 係数」で水を移動するだけの水位グラフ。
  グリッド・粒子は不要で、数十ボリュームでも実質タダ。「扉を開けると流れ込み、
  釣り合うと止まる」「階段から地下へ先に落ちる」が自然に出る
- **ゲームプレイ制御**: バルブを閉める＝開口を0に、壁を壊す＝リンク追加。
  レベルスクリプトがそのまま水の挙動になる（データドリブン）
- **演出接続**: 流量の大きいリンク位置に、流量比例でパーティクル・波源
  （インタラクションフィールド）・音を発生させる

**不採用と決めた方式**（再検討はユーザー要求時のみ）:

- 3D流体（FLIP/SPH/ボクセル）: コスト対効果で不採用
- 部屋の自動検出: 研究レベルの問題。ボリュームは人が置く
- ハイトフィールド流体（浅水方程式2Dグリッド）: 屋外の川・氾濫用の**将来オプション**として温存。
  室内には常に水位グラフが優る

### 1.3 インタラクションフィールド — 動きへの反応を1系統に統合

草の揺れ・水の波紋・雪や泥の轍を、**1種類の書き手と共有の場**で統合駆動する。

```
[書き手] InteractionSource コンポーネント（ECS・データドリブン）
   動く物に付ける。半径 / 強さ /（必要なら種類）だけ宣言
        ↓ 毎フレーム、位置と速度を場へ焼く（GPU）
[場]  ワールド空間の俯瞰テクスチャ
   ・瞬発場: カメラ追従の小窓（例 64m四方=512px）。速度ベクトル場＋波エネルギー。数秒で減衰
   ・永続変形: 地形チャンクに紐づく蓄積テクスチャ（轍用。歩き去っても残る）
        ↓ 読むだけ
[読み手] 各シェーダが自分の解釈で消費
   草   : 速度場 → 頂点を曲げる（なびき・踏み分け）
   水   : 波エネルギー → 法線摂動（波紋・航跡）
   雪泥 : 永続変形 → 変位＋レイヤ露出（轍）
```

- 読み手を増やしても書き手は無変更。キャラに InteractionSource を1個付ければ全表現が反応する
- **寿命の違いで場を2種類に分ける**のが設計の要（瞬発場＝追従小窓 / 永続変形＝チャンク紐づけ）
- 永続変形をシーン保存に含めるかは I3 実装時の設計判断として保留

### 1.4 3層モデルでの位置づけ

水面・インタラクションフィールドは第2層（画面空間の中間バッファ）ではなく、
**地形・草と同格の「ワールド空間のシーン素材」**。消費は主に第1層（頂点変位・
G-Buffer 書き込み時のレイヤ選択）で行われる。

水面の描画自体はフォワード経路（半透明）の**専用実装**であり、L3 シェーディングアセットでは
表現しない（頂点変位・マルチパス・半透明・動的テクスチャのすべてが L3 契約の対象外）。

---

## 2. 実装段階

### 水系（W）

- **W0: L3 契約に時間を追加**（小）— `ShadingSurface` 等へ `time` を後方互換追加。
  水とは独立に「アニメーションする光応答」がアセットで書けるようになる
- **W1: WaterVolume＋平面水面描画** — ✅ **実装済み（2026-07-28）**
  コンポーネント＋問い合わせ3点API＋水面クアッド1ドロー（プロシージャル波・深度差の吸収色と
  岸フォーム・フレネル・専用グラブ屈折）。Deferred 無改造。

  **実装参照**

  | 役割 | 場所 |
  |---|---|
  | コンポーネント | `runtime/src/engine/components/water_volume_component.rs`（`WaterVolumeKind::{Ocean,Region,Spline}` / `WaterVolumeComponentData`） |
  | 問い合わせ3点API（正典） | `runtime/src/engine/water/query.rs` の `WaterQuery::{is_underwater, surface_height_at, flow_at}` |
  | ワールド解決の中間表現 | `runtime/src/engine/water/resolved.rs` の `ResolvedWaterVolume` / `WaterVisualParams` |
  | シーン走査 | `runtime/src/engine/water/collect.rs` の `collect_water_volumes()` |
  | IPC フィールド編集 | `runtime/src/engine/core/app_base/app/water_ops.rs`（`SET_WATER_FIELD:`） |
  | 描画 | `runtime/src/engine/core/renderer/water/`（`WaterRenderer`）＋ `shaders/water_surface.wgsl` ＋ `pipelines/water_surface.toml` |
  | パス挿入 | `app/frame_renderer.rs`（メインパス drop → WBOIT 合成 → **水面パス** → オーバーレイ）。`RenderFrame::begin_water_pass_to` |
  | エディタ UI | `editor/src/Panels/InspectorPanel.xaml.cs` の `BuildWaterVolumeSlotContent` / `ComponentSelectorWindow.xaml.cs`「環境」カテゴリ |

  **W1 で確定した設計判断**

  - 水面高さ: **Ocean = ワールドY絶対値 / Region = アクタ原点からの相対Y**（海面レベルはワールド定数、
    池はアクタを動かすと追従するのが直感的なため）。Region の領域はアクタ位置中心の軸平行 AABB
    （**アクタ回転は W1 では無視**）。
  - メッシュ: 頂点バッファを持たず、頂点シェーダが `vertex_index`(0..6) と `instance_index` から
    クアッドを生成。**全水ボリュームを `draw(0..6, 0..N)` の 1 ドローで描く**（上限 `WATER_MAX_VOLUMES=64`）。
    Ocean はカメラ XZ 追従で `ocean_extent`（既定 2000m）半径。頂点変位は行わず法線のみの波。
  - 波は**サイン波4層の解析微分による法線合成**。法線マップ等の**テクスチャアセットに一切依存しない**。
  - 深度: 半透明パスの group4 に深度が無く、共有深度は書き込み可能状態でアタッチされていて同時サンプル
    できないため、**水面パスは深度アタッチメントを持たず**（`no_depth`）、DepthOnly ビューを
    サンプルバインドして `textureLoad` →「シーン深度 < 水面深度なら `discard`」の**手動深度テスト**を行う。
    同じサンプルから水の厚みも復元し、吸収色・岸フォームに使い回す。
  - 屈折: `RefractPyramid` のブラーミップ鎖は水面には過剰なので流用せず、「シーンHDRをコピーして読む」
    方式だけを流用して**専用の1ミップグラブ**を新設（水ボリュームが0個のフレームはコピーもパスも行わない）。
    メインパス・WBOIT 合成の後にグラブするため、背景にスカイボックスと既存半透明が含まれる。
  - WBOIT との両立: 水は **WBOIT の対象外**とし、常に自前パスでソート描画。距離ソート／WBOIT
    どちらのモードでも経路が同一になる。
  - Play / Edit 両方で描画（Play のレターボックスは他パスと同一条件で `set_viewport`/`set_scissor_rect`）。
    **カメラプレビューには描かない**（W1 スコープ外）。
  - `[PERF]` に `water=`（グラブ＋水面パスの CPU 時間）を追加。

  **W1 フォローアップ（同フェーズ内で追加）**

  - **水面のピッキング**: 同じクアッドを ID パスにも描く専用パイプライン
    （`shaders/water_id.wgsl` ＋ `pipelines/water_id.toml`、`WaterRenderer::draw_id`）。
    ID 値は他のアクタピックと同一規約 `canvas_id_offset + アクタDFS + 1` で、
    デコード側のキャンバス選択分岐（DFS → アクタ）にそのまま乗る（デコード側の変更ゼロ）。
    DFS 連番は `collect_water_volumes` が採番し、非アクティブなアクタも数える
    （`collect_mcs_in_world_line` と完全一致させるため）。
    深度整合は ID パスの深度アタッチメント（メインパスのシーン深度を Load）＋
    `depth_compare = LessEqual` / `depth_write = false` に任せるので、
    水面より手前の物体をクリックすればそちらが選ばれる。描画順はモデルの後・ギズモの前。
    選択のアウトライン表示は水面には出ない（アウトラインはメッシュのステンシル経路のため。
    選択自体とインスペクタ表示は成立する）。
  - **Edit 中も波が動く**: `Clock::ambient_time`（モード・ポーズに関係なく進む壁時計、内部 f64）を追加し、
    `CameraUniform.time` へ **Play 非ポーズ = `anim_time` / Edit・ポーズ = `ambient_time`** を配る
    （メインカメラ・2D オルソオーバーレイ・カメラプレビューの 3 箇所とも同一）。
    L3 シェーディングアセットの `ShadingSurface.time` も同じ値なので Edit 中に動く（`docs/shading_asset.md`）。
    切替時に位相は跳ぶ（波・時間応答は位相の連続性を要さないため許容）。
    草・風（`GrassUniform.time`）は従来どおり `anim_time` 駆動＝ Edit では静止のまま。

  **W1 の既知の制限**（後続フェーズで解消）

  - 水面パスは既存の半透明の**後**に描くため、水より手前にある半透明オブジェクトが水に上書きされる。
  - `flow_at()` は常にゼロ、`WaterVolumeKind::Spline` は enum のみ（**どちらも W4 で実装済み**）。
  - 水位は静的（W2.5 の水位グラフで時変化に一般化）。
- **W1.5: 岸波（ショアフィールド）** — ✅ **実装済み（2026-07-29）**
  水域ごとに「水深・岸までの距離・岸の方向」の俯瞰 2D（＝ショアフィールド）を CPU で焼き、
  水面シェーダがその 1 サンプルから「岸へ寄せるうねり・砕け泡・打ち上げ」を作る。
  **流体シミュレーションは一切行わない**（プロシージャルな波帯である）。
  I2 の勾配合成・泡システムに 3 つ目のソースとして合流する。

  **実装参照**

  | 役割 | 場所 |
  |---|---|
  | 場のベイク（正典） | `runtime/src/engine/water/shore.rs` の `ShoreFieldSet::update()` / `bake_shore_field()` |
  | 地形の存在範囲（走査の早期棄却） | 同上 `ShoreTerrainBounds` ／ 生成は `app/frame_renderer.rs` の `terrain_world_bounds()` |
  | 地形高さの取得 | `terrain::scatter::generate::surface_hit_down()`（**散布プロップの接地判定と同一関数**）＋ `app/terrain_scatter_ops.rs` の `TerrainScatterField` |
  | 再ベイクのトリガ | `App::terrain_edit_version`（`app/mod.rs`）。加算は `app/terrain_ops.rs` の 密度ブラシ／チャンク追加／undo／redo／`build_terrain_with`／`rebuild_terrain_after_load` |
  | パラメータ | `components/water_volume_component.rs` の `shore_wave_strength` / `shore_wave_length` / `shore_wave_period` / `shore_wave_foam` |
  | GPU パラメータ | `renderer/water/params.rs` の `WaterParams.shore` / `.shore_field`（vec4 を 2 本追加＝計 11 本）＋ `SHORE_LAYER_NONE` |
  | GPU リソース（配列テクスチャ・アップロード） | `renderer/water/mod.rs` の `WaterRenderer::sync_shore_fields()` ／ `SHORE_FIELD_FORMAT` |
  | 消費（水面） | `shaders/water_surface.wgsl` の `water_shore_sample` / `_amplitude` / `_phase` / `_height` / `_gradient` / `_foam` と `fs_water` 節 ①・⑦'' |
  | **合成高さ場（W5.1 の合流点）** | 同上 `water_surface_height()` / `water_surface_gradient()` |
  | 更新の呼び出し | `app/frame_renderer.rs`（水ボリューム収集の直後・I1 のコンピュート記録より前） |
  | IPC フィールド編集 | `app/water_ops.rs`（`shore_wave_*` の 4 キー）／ 送信は `app/component_ops.rs` |
  | エディタ UI | `editor/src/Panels/InspectorPanel.xaml.cs` の `BuildWaterVolumeSlotContent`「岸波」セクション |

  **W1.5 で確定した設計判断**

  - **場のチャネル設計はビット詰めしない**。1 テクセル ＝
    `x` 水深(m。水面Y−地形Y。**負は陸**) / `y` 符号付き岸距離(m。**正が沖**) /
    `zw` 岸方向(単位 XZ。そのテクセルから最寄りの岸を指す) の 4 成分が
    そのまま 4 チャネルに収まる。フォーマットはインタラクションフィールドと同じ
    **Rgba16Float**（理由も同じで、**フィルタ可能な浮動小数が core WebGPU ではこれだけ**。
    `Rgba32Float` は `float32-filterable` を要求する）。f16 の刻みは 256m 付近で 0.25m だが、
    岸波の振幅は沖で 0 へ落ちるので精度が要る岸近傍（数 m）では 1mm 未満になる。
  - **`zw = (0,0)` は「岸情報なし」の予約値**。窓外・窓内に岸が 1 つも無い場合にこれになり、
    シェーダは振幅 0 にする。これが無いと、岸の無い外洋で距離場の飽和値から波帯が湧く。
  - **窓は水域ごと**。Region = AABB＋マージン 16m を覆う正方の固定窓（上限 512m）。
    Ocean = **カメラ追従の 512m 窓**で、窓中心からカメラが窓一辺の 1/4 動いたときだけ置き直す
    （毎フレーム追従させるとベイクが止まらない）。窓原点は I1 と同じくテクセル境界へスナップし、
    置き直しの瞬間に位相が跳ねないようにしている。解像度は 256²（外洋窓で 2m/テクセル）。
    レイヤは配列テクスチャの 1 枚で、上限 `SHORE_FIELD_MAX_LAYERS = 8`（512KB × 8 ＝ 4MB）。
    **岸波を使う水域が 0 個のフレームはテクスチャを確保すらしない**。
  - **地形高さは CPU のカラム走査**。地形の真実源は ECS ではなくボクセル SDF
    （`TerrainState.chunks`）なので、テクセル中心の XZ について
    「水面＋8m から水面−32m まで降り、最初に現れる AIR→SOLID 遷移」を採る。
    使う関数は散布プロップの接地判定と**同一の `surface_hit_down`**である
    （別実装にすると「草は生えているのに岸波が陸へ乗る」ずれが出る）。
    物理レイキャストは使わない（Play 中しかコライダーが無く、コストも桁で高い）。
  - **洞窟の割り切り**: 上から降りて最初に当たった面をその XZ の地表とする。
    したがって水面より上に天井がある洞窟内は「天井が地表」と解釈される。
    岸波は水際の見た目のための場であり、洞窟内の水面に岸波を出す要求が無いため
    この割り切りで十分と判断した（散布の接地判定と同じ規約でもある）。
  - **岸距離は 8SSEDT（Danielsson のベクタ距離変換・CPU 2 パス）**。256² なら GPU 化する
    理由が無い。この方式を選んだ決め手は「最寄りシードへの**オフセットベクタ**を持ち回るので、
    **距離と岸方向が同時に得られる**」ことで、岸方向のための勾配計算が不要になる。
    シードは水／陸の隣接ペアの間で水深を線形補間した**サブテクセル位置**に置く
    （テクセル刻みに量子化すると外洋窓では 2m 刻みになり、位相が縞状に段付く）。
    - ⚠️ **実装時に踏んだ罠**: 伝播で隣から借りる差分は「**隣の座標 − 自分の座標**」でなければ
      ならない。符号を逆にしても距離の大きさは対称なので正しく見えるが、
      **岸方向だけが 180° 反転し、波が岸から沖へ逃げる**。
      `shore_direction_points_toward_shore` テストがこれを固定している。
  - **再ベイクは「地形編集バージョン＋水パラメータ＋（Ocean は）カメラ位置」の署名比較＋
    300ms デバウンス**。毎フレームは焼かない。地形ブラシのドラッグ中は署名が毎フレーム
    変わり続けるので、実際に焼かれるのは**手を止めた後の 1 回だけ**になる。
    バージョンカウンタは `TerrainState` ではなく **`App` に置く**（シーンロードや地形の
    作り直しで `TerrainState` が丸ごと差し替わり、カウンタが 0 に戻って
    「変化していない」と誤判定するため）。LOD 遷移の再メッシュでは進めない
    （密度は変わっていないので焼き直す理由が無い）。
  - **走査の早期棄却が性能の要**。地形チャンクの実在 AABB（`ShoreTerrainBounds`）を渡し、
    ① 地形の外側の XZ は 1 回も密度サンプルしない ② 走査 Y 範囲を地形の Y 帯との共通部分へ狭める
    ③ 地形が 0 チャンクなら**そもそも焼かない**、の 3 段で切る。
    外洋の窓に小島がひとつ、という典型ケースでは走査カラムが数 % まで落ちる。
    最悪ケース（512m 窓が全面地形）の実測は **密度サンプル約 311 万回／カラムあたり平均 47.5 回**。
    rayon で行並列に焼く。`columns_outside_terrain_bounds_cost_no_samples` テストが
    早期棄却の生存を固定している。
  - **合成式**（`fs_water` 節 ①・⑦''）。うねりの位相は

    ```
    位相 = 2π ( 岸距離 / 波長 + 時間 / 周期 )
    ```

    **時間項は距離項と同符号**である。岸距離は「沖が正」なので、位相一定の点（波の峰）が
    岸へ進むには距離が時間とともに**減る**必要があり、差にすると波が沖へ逃げる。
    振幅は 3 つの包絡線の積:
    `波長×0.03`（波形勾配 1/33 相当の基準振幅）×
    `1 − smoothstep(0, 波長/2, 水深)`（深水 h>L/2 で 0 へ）×
    `clamp((波長/2 ÷ 水深)^(1/4), 1, 2.5)`（Green の法則の浅水増幅・上限付き）×
    `smoothstep(−0.5m, 0, 水深)`（陸側で消す）。
    勾配は搬送波の解析微分 `A·cos(位相)·2π/波長·(−岸方向)` で、包絡線の微分は無視する
    （包絡線は波長スケールに対して十分緩やかという標準的な近似）。
    泡は「砕け泡 `smoothstep(0.55, 0.88, 振幅/水深) × clamp(sin(位相),0,1)`」と
    「打ち上げ `1 − smoothstep(0, 0.12λ, |岸距離 − 0.35λ·sin(2π(t/T + 0.25))|)`」の
    **大きい方**を採り、`岸波の泡量 × 強さ` を掛けて既存の `foam_color` に乗せる。
  - **W5.1 の合流点を先に用意した**。解析波（W1）・波紋（I2）・岸波（W1.5）の高さと勾配は
    `water_surface_height()` / `water_surface_gradient()` に集約してある。
    引数は `WaterParams`・ワールド XZ・時間だけで、フラグメント固有の入力
    （深度・画面 UV）に一切依存しない。**W5.1（頂点変位）は頂点シェーダから
    `water_surface_height` を呼ぶだけ**でフラグメントの法線と同じ高さ場を共有できる
    （シルエットと陰影がズレない）。group1／group2 はどちらも VERTEX_FRAGMENT 可視なので
    バインドの変更も要らない。`water_shader_exposes_combined_height_field` テストが
    この契約（シグネチャと `fs_water` が合成関数経由であること）を固定している。
  - **ランタイムコストはテクスチャ 1 サンプル＋数式のみ**。追加のパスもディスパッチも無く、
    水面パスは 1 ドローのまま。岸波を切っている（`strength = 0`）／フィールドが焼かれていない
    水域はパラメータのレイヤ番号が負になり、シェーダは**テクスチャサンプルすら行わない**
    （＝ W1/I2 と完全に同じコスト・同じ出力）。

  **パラメータ表**（`WaterVolumeComponent`。細部はエンジン定数）

  | フィールド | 既定 | 意味 |
  |---|---|---|
  | `shore_wave_strength` | 1.0 | 岸波の強さ。**0 で完全無効（W1/I2 と同一出力・ベイクも走らない）** |
  | `shore_wave_length` | 12.0 | うねりの波長（m）。振幅は波長から決まる（波長 × 0.03） |
  | `shore_wave_period` | 4.0 | うねりの周期（秒）。12m / 4s ＝ 位相速度 3m/s |
  | `shore_wave_foam` | 0.8 | 砕け波・打ち上げの泡量（0..1） |

  **W1.5 の既知の制限**（後続フェーズで解消）

  - 岸波も**法線と泡だけ**（頂点変位は無い）。うねりが実際に盛り上がるのは W5.1。
  - 場は静的なベイク。潮位変化・可動の堰などで水面 Y が動くと、その都度 300ms 遅れて焼き直る
    （W2.5 の水位グラフで水位が連続的に変わるようになったら、ベイク頻度の再検討が要る）。
  - Ocean の窓は 512m。それより遠い岸は岸波を持たない（近づけば焼き直されて現れる）。
  - 洞窟内の水面は「天井が地表」と解釈される（上記の割り切り）。
  - `WaterQuery` は岸波を含まない静的な水面高さを返す（I2 の波紋と同じ扱い）。
- **W2: 水中表現** — カメラ水中判定→フルスクリーンポスト（青緑フォグ＋ゆらぎ）
- **W2.5: 水位グラフ** — ボリューム＋リンク＋連通計算（§1.2）。W1の水位を時変化に一般化
- **W3: 遊泳** — KCC に遊泳ステート（浮力込み沈降・全方向移動・水面スナップ）、
  リジッドボディに浮力＋抵抗
- **W4: 川（スプライン水面と流れ）** — ✅ **実装済み（2026-07-29）**
  制御点列（アクタ相対）を Catmull-Rom で補間した曲線に沿って、幅一定のリボン水面を張る。
  水面 Y は制御点 Y の補間なので**川は下る**。流速は `WaterQuery::flow_at` が返し、
  同じ値が水面模様を下流へ流す（見た目と挙動が同じ数字を見る）。

  **実装参照**

  | 役割 | 場所 |
  |---|---|
  | スプライン幾何（正典） | `runtime/src/engine/water/spline.rs` の `RiverPath::{build, nearest}` / `catmull_rom` |
  | コンポーネント | `components/water_volume_component.rs` の `spline_points` / `river_width` / `flow_speed` / `river_depth` / `river_segment_length` / `control_point_ref` |
  | ワールド解決 | `water/resolved.rs` の `ResolvedWaterVolume::river`（**kind = Spline のときだけ Some**）。点列の出どころ切替は `from_component_with_path` |
  | 収集 | `water/collect.rs`（制御点 2 点未満の Spline はスキップ／`control_point_ref` の名前解決もここ: `find_actor_by_name` / `resolve_control_polyline`） |
  | 汎用パス基盤（点列の正典） | `runtime/src/engine/path/eval.rs` の `PathEval` — 詳細は `docs/control_points.md` |
  | 問い合わせ | `water/query.rs` の `flow_at` / `volume_contains` / `volume_surface_at_xz` の Spline 分岐 |
  | GPU パラメータ | `renderer/water/params.rs` の `WaterParams::from_river_segment`（vec4 を 3 本追加＝計 14 本）＋ `WATER_INSTANCE_QUAD/RIVER` / `WATER_MAX_INSTANCES` |
  | インスタンス生成 | `renderer/water/mod.rs` の `WaterRenderer::prepare`（川は 1 分割 = 1 インスタンス） |
  | 描画（リボン頂点・流れ） | `shaders/water_surface.wgsl` の `water_river_vertex` / `water_flow_offsets` / `water_flow_wave_{height,gradient}` |
  | ピッキング | `shaders/water_id.wgsl` の `water_id_river_vertex`（水面 ID 経路をそのまま拡張） |
  | IPC フィールド編集・地形スナップ | `app/water_ops.rs`（`river_width` / `flow_speed` / `river_depth` / `spline_points` / `spline_snap_terrain` / `river_segment_length` / `control_point_ref`）＋ `App::snap_water_spline_to_terrain` |
  | エディタ UI | `editor/src/Panels/InspectorPanel.xaml.cs` の `BuildWaterVolumeSlotContent`「川（スプライン）」セクション |

  **W4 で確定した設計判断**

  - **補間は Catmull-Rom**（uniform, τ=0.5）。制御点を必ず通り、ベジェのような
    追加ハンドルが要らないため「点を置くだけで滑らかな川になる」エディタ体験に最も素直。
    端点は 1 つ外側の点を折り返して複製する標準処理。
  - **分割は曲線長からの一定密度**（既定 `RIVER_SAMPLE_STEP_M = 2m` ごとに 1 分割、
    上限 `RIVER_MAX_SEGMENTS = 256`）。上限超過時は区間ごとに比例配分で縮める
    （各区間は最低 1 分割）。制御点も `RIVER_MAX_CONTROL_POINTS = 64` で上限を切り、
    「1 区間 = 最低 1 分割」と分割上限が矛盾しないようにしてある。
    **W4.1 で刻み幅をボリュームごとに設定可能にした**（`river_segment_length`。既定 2.0m ＝
    従来の固定値と同一なので旧シーンの形は 1 頂点も変わらない。下限
    `RIVER_SEGMENT_LENGTH_MIN = 0.25m`）。**総分割数の上限は据え置き**なので、
    長い川では設定を細かくしても上限で頭打ちになり自動的に粗くなる。
  - **描画は既存のインスタンス描画へ合流させた**。頂点バッファは持たないまま、
    `WaterParams` に「インスタンス種別」（`center.w`）を持たせ、頂点シェーダが
    `center ± half_extent` の矩形か、`river_p0/p1 ± 法線 × 半幅` のリボン 1 コマかを選ぶ。
    **クアッド前提の 0..6 生成をそのまま使える**ので、パス・パイプライン・
    BindGroup はいっさい増えていない（1 ドローのまま）。曲がり角では隣接区間の
    法線を平均した**マイター法線**（`1/cos(θ/2)`・上限 2 倍）でリボンが痩せないようにする。
  - **流れは 2 位相ブレンド**。解析波のサンプル位置を `流速 × 時間` だけ上流へずらすと
    模様が下流へ動くが、単調にずらし続けると永久に平行移動するだけになる。
    半周期ずれた 2 位相を三角波でクロスフェードする（フローマップの標準手法）。
    **ブレンド重みは時間のみの関数で空間に依存しない**ので、高さをブレンドした場の
    空間微分は勾配をブレンドしたものと厳密に一致する ＝ W5.1 の頂点変位と法線が食い違わない。
  - **問い合わせと描画は同じ折れ線を見る**。`RiverPath` の折れ線がリボンの頂点でもあり、
    最近傍判定の対象でもある（「水に見えるのに流されない」ズレを構造的に防ぐ）。
    ただし判定は「折れ線までの XZ 距離 ≤ 半幅」なので、曲がり角の外側だけ
    マイターで張った描画リボンよりわずかに内側になる（安全側の誤差として許容）。
  - **岸波（W1.5）は川では焼かない**。岸波は「岸へ寄せるうねり」であり、
    川の窓は AABB では表せない（細長い折れ線）ため、Region 用の正方窓を当てても
    無関係な広域を焼くだけになる。`ShoreFieldSet::update` が Spline を除外する。
  - **川の深さは専用フィールド `river_depth`**（既定 2m）。Region の AABB 縦半径を
    流用すると、Spline ではインスペクタに出ない値が水中判定を決める“隠れた結合”になる。
  - **地形スナップ**（`spline_snap_terrain`）は `terrain::scatter::generate::surface_hit_down`
    ＝散布プロップの接地判定・岸波のカラム走査と**同一関数**を使う。
    地形に当たらなかった制御点は元の Y を保つ（0 に落とすと川が地面を突き抜ける）。

  **W4 のパラメータ**（インスペクタ「川（スプライン）」）

  | フィールド | 既定 | 意味 |
  |---|---|---|
  | `spline_points` | 空 | 制御点列（**アクタ相対**）。2 点未満は描画・判定とも無効。**`control_point_ref` が設定されているときは完全に無視される**（`docs/control_points.md`） |
  | `river_width` | 4.0 | 川幅（m。全幅。リボンは一定幅） |
  | `flow_speed` | 1.5 | 流速（m/s）。`flow_at` の速さ＝模様が流れる速さ。負値で逆流 |
  | `river_depth` | 2.0 | 川の深さ（m）。水面からこの深さまでが水中判定 |
  | `river_segment_length` | 2.0 | 折れ線 1 分割ぶんの目標長（m。W4.1）。下限 0.25。総分割数の上限 256 は据え置き |
  | `control_point_ref` | 空 | 制御点を借りる**参照先アクタ名**（W4.1）。空 = `spline_points` を使う |

  **W4.1: ControlPointComponent との統合（明示参照方式）**（2026-07-29 実装・同日改訂）

  川の形（点列）は、W4 固有の `spline_points` ではなく**汎用のコントロールポイント基盤**へ
  移せる。詳細と使い方は **`docs/control_points.md` が正典**。ここでは水側の接続点だけ記す。

  - 使う／使わないは `WaterVolumeComponent.control_point_ref`（**参照先アクタ名**・既定は空）で
    **明示的に指定する**。参照が解決でき、そのアクタの 0 番目の有効な `ControlPointComponent` の
    折れ線が 2 点以上あるとき、**`spline_points` を完全に無視して**そちらから `RiverPath` を組む。
    それ以外（参照が空／アクタが見つからない／スロット無し・無効／点が足りない）は
    従来の `spline_points` へフォールバックする。
  - **初版の「同一アクタなら自動優先」は廃止した。**
    どちらが効いているのか UI から判別できず、コンポーネントを足しただけで川の形が変わるため
    （実際に混乱を生んだ）。既存シーンで自動優先に頼っていた川は参照の設定し直しが要る。
  - **参照先は別アクタでもよい。** そのとき点列のワールド解決には**参照先アクタの Transform**
    （位置＋回転＋スケール）を使う ＝ 制御点を持つアクタを動かすと川が動き、
    水アクタを動かしても川は動かない。
  - `spline_points` は**削除していない**。参照を外せばいつでも従来経路へ戻る。
    インスペクタの川セクションには参照ボックス（Hierarchy から D&D・✕ で解除・
    ダブルクリックで Hierarchy へジャンプ）と、
    「このアクタに制御点を作って参照に設定」ボタンを置いた
    （ControlPointComponent の追加 → `spline_points` の変換投入 → 参照の結線を 1 回で行う）。
    参照が設定されているあいだ `spline_points` の編集 UI は丸ごと隠れる。
  - **形だけが移り、川幅・流速・川の深さ・分割長・見た目・`surface_height` は WaterVolume 側に残る。**
    `surface_height` は参照経路でも Y に上乗せされるので、点を触らずに川全体の水位を上下できる。
  - **座標系の落とし穴**: `PathEval::sample_polyline` が返す折れ線は**すでに完全なワールド座標**
    （アクタの位置だけでなく回転・スケールも適用済み）。`spline_points` 経路のようにアクタ位置を
    足すと二重加算になる。`ResolvedWaterVolume::from_component_with_path` が両経路の差を吸収する
    ＝ **W4 の「アクタ位置しか見ない」制限は参照経路では解消している**。
  - 実装: `water/collect.rs::{find_actor_by_name, resolve_control_polyline, collect_in_actor}`
    （参照名の解決）／ `water/resolved.rs::from_component_with_path`（折れ線の適用。従来の
    `from_component` は `control_polyline = None` を渡す薄いラッパ）。

  **W4 の既知の制限**（後続フェーズで解消）

  - ~~**ビューポートの 3D ギズモによる制御点編集は無い**~~ → **上記 ControlPoint 統合で解消。**
    点はワイヤキューブとして描かれ、クリックで選択して移動ギズモで動かせる（Undo 対応）。
    さらに「制御点を追加」ボタンをビューポートへ D&D すると、メッシュ・地形・水面への
    レイキャスト着弾点に点が置かれる（＝数ドロップで地形に沿った川が引ける）。
  - **波紋（I2）の移流をしていない**。川の中で立った波紋はその場で減衰し、下流へは
    流されない。インタラクションフィールドは水域を知らない単一のカメラ追従窓であり、
    移流項を足すには「どのテクセルがどの川に属し、どちらへ流れるか」を場へ焼く必要がある
    （＝ショアフィールド相当の新しい場が要る）。W4 では見送った。
  - 川幅は一定（区間ごとの可変幅・河口の広がりは未対応）。
  - 川面には岸波が出ない（上記の設計判断）。岸フォームは水深由来なので川岸にも出る。
  - 水面はリボンの内側だけで、隣接する川同士の合流は「重ねて置く」以上のことはしない
    （合流点で 2 枚の水面が交差する）。
- **W5: 水の最終形（北極星: Horizon Forbidden West 級）** — 未着手。
  W1〜W4・I2 で「平面の水面・波紋・浸水・川」までは揃うが、目標画質には**まだ足りない**。
  最終的に目指す水は次の 5 点を満たすものであり、W5 はそこへ至る残項目の受け皿である。

  | 残項目 | 内容 | 依存 |
  |---|---|---|
  | **W5.1 頂点変位の大波** | 水面を実際に上下させる（Gerstner 波＋LOD 付きグリッド／テッセレーション）。現状は法線のみで、シルエットが常に平面。**合流点は W1.5 で用意済み**: `water_surface.wgsl` の `water_surface_height()`（解析波＋波紋＋岸波の合成高さ）を頂点シェーダから呼べば、フラグメントの法線とまったく同じ高さ場で頂点を動かせる（引数はワールド XZ と時間だけ・バインド変更も不要）。岸との交差（波が岸へ乗り上げる）と浮力（W3）が波に同期する | W1 / I2 / **W1.5** |
  | **W5.2 反射の本命化** | 現状は「浅い角度で単色へ寄せる」簡易フレネル。SSR（画面空間反射）を第一候補、RT 対応 GPU では RT 反射へ切り替える。**Deferred+Clustered 化（`rendering_roadmap.md` フェーズ D）と合流させる**のが前提で、単独で作ると二重実装になる | D フェーズ / R8 |
  | **W5.3 コースティクス** | 水面の高さ場から屈折集光パターンを生成し、水中の地形・オブジェクトへ投影する。高さ場は I2/W5.1 のものをそのまま使える（新しいシミュレーションは不要） | W5.1 |
  | **W5.4 水中ゴッドレイ** | 水面を通した光芒（ボリュメトリックライト）。水中ポスト（W2）とボリュメトリック基盤の合流点 | W2 |
  | **W5.5 泡・飛沫のパーティクル連携** | 航跡の泡（I2 はシェーダ内の塗りのみ）を GPU パーティクルへ昇格させ、飛沫・水しぶきを出す。発生源は I2 が既に持っている波エネルギー場でよい | I2 / パーティクル |

  **順序の考え方**: W5.2 は D フェーズ（Deferred+Clustered）に従属するので**単独で先行させない**。
  W5.1 → W5.3 → W5.5 → W5.4 の順に、既に持っている高さ場を使い回せるものから積む。

### インタラクション系（I）

- **I1: InteractionSource＋瞬発場＋草の揺れ** — ✅ **実装済み（2026-07-28）**
  書き手コンポーネント＋カメラ追従の瞬発場（コンピュート 1 パス）＋草の頂点段での消費。

  **実装参照**

  | 役割 | 場所 |
  |---|---|
  | コンポーネント | `runtime/src/engine/components/interaction_source_component.rs`（`InteractionSourceComponentData`: 半径 / 強さ / 有効） |
  | シーン走査 | `runtime/src/engine/interaction/collect.rs` の `collect_interaction_sources()` |
  | ワールド解決の中間表現 | `runtime/src/engine/interaction/resolved.rs` の `ResolvedInteractionSource` / `source_key()` |
  | 速度算出（前フレーム位置の保持） | `runtime/src/engine/interaction/velocity.rs` の `InteractionSourceVelocityTracker` |
  | 場（GPU リソース＋更新） | `runtime/src/engine/core/renderer/interaction/mod.rs` の `InteractionFieldRenderer` ＋ `shaders/interaction_field.wgsl` |
  | 消費（草） | `shaders/grass_gbuffer.wgsl` の `grass_interaction_velocity()` と vs_grass 節 5b（group2） |
  | パス挿入 | `app/frame_renderer.rs`（水ボリューム収集の直後・**カメラプレビューとメインパスの両方より前**にコンピュートを記録） |
  | IPC フィールド編集 | `runtime/src/engine/core/app_base/app/interaction_ops.rs`（`SET_INTERACTION_FIELD:`） |
  | エディタ UI | `editor/src/Panels/InspectorPanel.xaml.cs` の `BuildInteractionSourceSlotContent` / `ComponentSelectorWindow.xaml.cs`「環境」カテゴリ |

  **I1 で確定した設計判断**

  - **場の形**: 一辺 64m（`INTERACTION_FIELD_EXTENT_M`）× 512px（`INTERACTION_FIELD_RESOLUTION`）
    ＝ 0.125m/テクセル。フォーマットは **Rgba16Float**（`rg16float` は core WebGPU の
    storage フォーマットに無いため。imos_blur の R16Float と同じ事情）。
    **`.xy` = ワールド XZ 速度場（I1）／`.z` = 波の高さ h_t（I2）／`.w` = 1 フレーム前の高さ h_(t−1)（I2）**。
    実際に I2 はフォーマット・バインド・パス構成を一切変えずに実装できた
    （ping-pong の読み側に 2 世代が揃うため、伝播用の別バッファも不要だった）。
  - **更新はコンピュート 1 パス**。「減衰パス → スタンプパス」に分けるとスタンプが
    read-modify-write を要求し、rgba16float の `read_write` storage が core で使えないため
    どのみち ping-pong が要る。ならば 1 パスで「前フレーム読み → 再マップ → 減衰 →
    全ソース合成 → 書き」までやり切る方が単純かつ速い（バリア・クリア不要）。
    ソース上限 `INTERACTION_MAX_SOURCES=64`。
  - **カメラ追従はテクセル単位スナップ**（`snap_window_origin`）。窓原点を
    `floor(v/texel)*texel` へ丸めるので前フレームとの差は必ず整数テクセルになり、
    再マップは整数 `textureLoad` で済む（バイリニアのにじみ＝カメラ移動でのちらつきが
    構造的に起きない）。窓外の再マップ読みは 0＝新しく入ってきた帯に場は無い。
  - **スタンプは加算ではなく重み付き上書き**（`mix(場, 速度, w)`, `w = falloff² × strength`）。
    加算だと同じ場所で動き続けるソースの寄与が毎フレーム積み上がり `1/(1-decay)` 倍
    （60fps で数十倍）に発散する。mix なら場はそのソースの速度へ収束する。
  - **減衰**は指数（τ=1s、`INTERACTION_FIELD_DECAY_TAU_SECS`）で「通り過ぎて約 3 秒で復元」。
    ソースが 0 個の状態が 5τ 続いたら最後に 1 回「減衰 0」で書き潰し、
    以降ディスパッチしない（**ソースを置かないシーンの GPU コストは 0**）。
    場テクスチャ自体も「草バッファかソースが存在するフレーム」まで確保しない。
  - **速度はコンポーネントが宣言しない**。`Transform` のフレーム間差分から
    `InteractionSourceVelocityTracker` が算出する（駆動元が物理／スクリプト／アニメ／
    手動ドラッグのいずれでも等しく機能する）。初出フレームは速度 0、テレポートは
    `INTERACTION_MAX_SPEED=20m/s` で飽和。シーン切替・Play 開始/終了では履歴を `clear()`。
  - **草の曲げは風との「ベクトル合成」**。風 `forward × θ_wind` とインタラクション
    `push × θ_interact` をベクトルとして足し、合成方向へ弧長保存の曲げを行う。
    インタラクション 0 のとき従来式と厳密に一致する（cos が偶関数・sin が奇関数）ため
    **既存の風を 1 ビットも壊さない**。葉面の向き（`side`）は yaw のまま据え置き、
    通過の瞬間に葉が回転してパチンと切り替わるのを避ける。曲げ方向が `side` と
    平行になり得るので、法線 `cross(side, tangent)` は正規化する（従来は単位長保証だった）。
  - **Edit でも動く**。減衰も速度算出も `ctx.delta_time`（壁時計）で駆動するため、
    Edit 中にアクタをドラッグしても草が反応し、離せば数秒で戻る。
    草の風（`GrassUniform.time = anim_time`）とは独立した時間源である。
  - **カメラプレビューにも本物の場をそのままバインドする**（ダミー 0 テクスチャを持たない）。
    場はメインカメラ追従の窓なので、プレビューが窓の外を映していれば
    サンプル範囲外＝0 で自然に無反応になり、同じ場所を映していれば主画面と一致する。
  - 草パイプラインの **group2** に場を追加。BGL は
    `renderer::interaction::create_field_sample_bind_group_layout()` を草側・場側の
    双方が呼び、wgpu の BindGroupLayout 構造的等価性でバインドする（カメラ BGL 借用と同じ慣例）。
  - `[PERF]` に `interact=`（収集＋速度算出＋コンピュート記録の CPU 時間）を追加。

  **I1 の既知の制限**（後続フェーズで解消）

  - ~~場は **XZ 速度のみ**。波エネルギー（`.z`）は誰も書かない~~ → **I2 で解消**
    （`.z` = 波の高さ / `.w` = 1 フレーム前の高さ として波動方程式を同居実装）。
  - ~~消費側は草だけ~~ → **I2 で水面が加わった**。雪泥の轍（永続変形）は I3。
  - 窓（64m）の外に出た草は影響を受けない。広域の轍表現は I3 の永続変形の担当。
  - 影響半径は円のみ（向きを持たない）。車両のような細長い接地形状は未対応。
- **I2: 水の波紋・航跡** — ✅ **実装済み（2026-07-28）**
  瞬発場に「波の高さ場」を同居させ、波動方程式で伝播させて水面が法線摂動＋泡として消費する。

  **実装参照**

  | 役割 | 場所 |
  |---|---|
  | 注入量の決定（水面近接判定） | `runtime/src/engine/interaction/water_wave.rs` の `apply_water_wave_injection()` |
  | 垂直速度の算出 | `runtime/src/engine/interaction/velocity.rs` の `MovingInteractionSource::velocity_y` |
  | 波の伝播（場の更新） | `runtime/src/engine/core/renderer/shaders/interaction_field.wgsl`（節 ④）＋ `renderer/interaction/mod.rs` の波係数（`INTERACTION_WAVE_*`） |
  | 消費（水面） | `shaders/water_surface.wgsl` の `water_ripple_height()` / `water_ripple_gradient()` と `fs_water` 節 ①・⑦' |
  | 水面側の group2 バインド | `renderer/water/mod.rs`（リフレクション由来 BGL ＋ 毎フレーム BindGroup 生成＋フォールバック） |
  | 調整パラメータ | `components/water_volume_component.rs` の `ripple_strength` / `ripple_foam_threshold`（`WaterParams.fresnel.zw` へ同居） |
  | パス挿入 | 変更なし（**既存の 512² コンピュート 1 本のまま**。水面パスも 1 ドローのまま） |
  | IPC フィールド編集 | `app/water_ops.rs`（`ripple_strength` / `ripple_foam_threshold`） |
  | エディタ UI | `editor/src/Panels/InspectorPanel.xaml.cs` の `BuildWaterVolumeSlotContent`「波紋・航跡」セクション |

  **I2 で確定した設計判断**

  - **伝播バッファは増やさない（チャンネル同居）**。波動方程式の陽解法
    `h_(t+1) = damp·( 2h_t − h_(t−1) + k·∇²h_t )` は
    **h の 2 世代**を要するが、既存の場は ping-pong なので読み側の
    `.z`(h_t) / `.w`(h_(t−1)) で 2 世代が揃う。書き側へ `.z = h_(t+1)` / `.w = h_t` と
    詰め直すだけで世代が進み、ラプラシアンの 4 近傍も同じ読み側から取れる。
    **追加テクスチャ 0・追加ディスパッチ 0**で速度場 `.xy` と完全に同居する
    （再マップは整数 `textureLoad` のまま。近傍サンプルにも同じ整数シフトを適用する）。
  - **時間刻みは固定タイムステップ＋サブステップ**（当初の「可変 dt を dt 比で正規化」方式は
    発散したため撤回した）。dt 比補正 `inertia` を入れた式のモード解析では特性方程式が
    `g² − damp·(1 + inertia − kλ)·g + damp·inertia = 0` となり、2 根の積が `damp·inertia`。
    つまり `inertia > 1/damp` のフレームでは必ず `|g| > 1` の根が生じ、フレーム時間が
    揺れる（エディタでは常態）たびにエネルギーが注入されて**必ず発散する**。
    現行方式は実経過時間をアキュムレータへ積み、`INTERACTION_WAVE_FIXED_DT_SECS`（1/60 秒）
    単位で 1 フレーム最大 `INTERACTION_WAVE_MAX_SUBSTEPS`（4）回のサブステップとして消化する。
    これにより `k = (c·dt_fixed/dx)² = 0.25`（安定限界 1/2 の半分）と
    `damp = exp(-dt_fixed/τ)` が**コンパイル時定数**になり、フレームレートに一切依存しない。
    上限を超えたぶんの時間は捨てる（波が実時間より遅く進むだけの安全側）。
    パスの構成は「1 回目 = 再マップ＋速度場減衰＋波＋スタンプ（`cs_interaction_field`）／
    2 回目以降 = 波のみ（`cs_interaction_wave_substep`）」。再マップとスタンプは
    1 フレームに 1 回だけが正しく、速度場は 1 次減衰なので実 dt ぶんをまとめて 1 回掛ける。
  - **スタンプは加算でも設定でもなく「押し下げへの mix」**。速度場と同じ理由
    （加算は発散する）に加え、「動く物体が水面に作るくぼみ」という境界条件そのものになる。
    くぼみが移動すると跡から波が放射され、**輪と航跡が自動的に出る**。値は常に有界。
  - **水面近接の判定は CPU 側**。場は俯瞰 2D で高さを持たないため、「水面を歩く人」と
    「上空を飛ぶ鳥」を場の上では区別できない。`WaterQuery::surface_height_at`（正式 API）で
    XZ が水域内かを見て、`|y − 水面Y| ≤ ソース半径` の帯に入るソースだけが注入する。
  - **注入の強さ**: 水平速度に比例（歩く＝さざ波／走る＝強い航跡）＋ 垂直速度は 3 倍
    （飛び込み）。しきい値未満の微動は 0（**静止時は注入しない**）。振幅は上限で飽和。
  - **水面の消費は「勾配の重ね合わせ」**。解析サイン波の法線と波紋の法線を別々に作って
    混ぜるのではなく、**高さ場の勾配同士を足してから 1 回だけ正規化**する
    （高さ場の重ね合わせ＝勾配の重ね合わせ、が物理的に正しい）。波紋の勾配は場の
    テクセル幅（0.125m）の中央差分 4 タップ。窓外は 0＝影響ゼロ（草と同じ扱い）。
  - **航跡の泡は既存の岸フォームを流用**。`|h|` がしきい値を超えた所へ同じ `foam_color` を
    乗せる（水ごとの泡の色は 1 つ、という設計）。既定しきい値は「歩行では出ず、
    走り・飛び込みで出る」値。
  - **水面側の group2 は共有レイアウトを使わず、リフレクション由来 BGL で自前 BindGroup**。
    水面パイプラインは TOML＋WGSL リフレクションで組まれ、リフレクションは uniform を
    VERTEX_FRAGMENT 可視にするため、草が使う共有レイアウト（`create_field_sample_bind_group_layout`）と
    構造的に一致しない。場は毎フレーム ping-pong するので BindGroup はどのみち毎フレーム
    作り直しであり、リフレクション BGL をそのまま使うのが最短かつ二重管理が起きない。
    場が未構築のフレーム用に 1×1 ゼロテクスチャ＋ゼロ UBO のフォールバックを常備する
    （`inv_extent = 0` ＝ 常に窓外 ＝ W1 と同一の見た目）。
  - **場の休止判定は「波の τ」基準へ変更**。速度場の τ（1s）で切ると、まだ見えている
    波紋の途中でディスパッチが止まり波が凍りつく。`INTERACTION_FIELD_SETTLE_SECS` を
    波の τ（1.5s）の 5 倍に取り直した。消去フレーム（`decay = 0`）は速度も波も
    全チャンネルを 0 で書き潰す。
  - **Edit でも動く**（I1 と同じ壁時計駆動）。ギズモでアクタを水面上でドラッグすると
    波紋が出て、離すと数秒で消える。
  - **性能**: 追加のパスもディスパッチも無し（既存 512² コンピュート 1 本の中で完結）。
    テクセルあたり 4 タップの近傍読みが増えるだけ。`[PERF]` は `interact=` に含まれる。

  **I2 の既知の制限**（後続フェーズで解消）

  - 波は**法線と泡だけ**（頂点変位は無い）。大波・うねりは W5 の頂点変位で扱う。
  - 場の窓（64m）の外に出ると波紋は消える。広域の航跡は瞬発場の担当外。
  - 影響形状は円のみ（船体のような細長い接地形状は未対応。I1 と同じ制限）。
  - 水面の高さ場は描画専用。`WaterQuery` は波紋を含まない静的な水面高さを返す
    （浮力・遊泳が波で揺れるのは W3 以降の判断）。
- **I3: 雪・泥の轍** — 永続変形テクスチャ＋地形シェーダの変位・レイヤ露出（最重量）

推奨順: W0 → W1 → I1 → **I2（済）** → **W1.5（済）** → **W4（済）** → W2 → W2.5 → W3 → I3 → W5
（W/I は依存が薄いので入れ替え可。I2 のみ W1 に依存。W5 は上記のとおり D フェーズと
W1〜W4 が揃ってからの最終仕上げであり、**最後に置く**）。

> **北極星**: 本ロードマップの水系の最終目標は
> **Horizon Forbidden West 級の水表現**（うねる大波・正しい反射・コースティクス・
> 水中の光芒・飛沫）である。W1〜W4 はその土台であり、到達点そのものではない。
> 個別フェーズの設計判断で迷ったときは「W5 まで作り直さずに済むか」で選ぶこと。
