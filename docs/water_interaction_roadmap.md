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
  - `flow_at()` は常にゼロ（W4 の川スプラインで実装）。`WaterVolumeKind::Spline` は enum のみで未実装。
  - 水位は静的（W2.5 の水位グラフで時変化に一般化）。
- **W2: 水中表現** — カメラ水中判定→フルスクリーンポスト（青緑フォグ＋ゆらぎ）
- **W2.5: 水位グラフ** — ボリューム＋リンク＋連通計算（§1.2）。W1の水位を時変化に一般化
- **W3: 遊泳** — KCC に遊泳ステート（浮力込み沈降・全方向移動・水面スナップ）、
  リジッドボディに浮力＋抵抗
- **W4: 川** — スプラインリボンメッシュ＋フローマップ。流速問い合わせで「流される」

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
    **`.xy` = ワールド XZ 速度場（I1）／`.z` = 波エネルギー予約（I2）／`.w` = 予約**。
    I2 は更新シェーダのスタンプ節に `.z` を足すだけでよく、フォーマット・バインド・
    パス構成は変えなくてよい。
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

  - 場は **XZ 速度のみ**。波エネルギー（`.z`）は誰も書かない（I2）。
  - 消費側は草だけ。水面の波紋は I2、雪泥の轍（永続変形）は I3。
  - 窓（64m）の外に出た草は影響を受けない。広域の轍表現は I3 の永続変形の担当。
  - 影響半径は円のみ（向きを持たない）。車両のような細長い接地形状は未対応。
- **I2: 水の波紋** — W1 完了後、瞬発場の波エネルギーを水面の法線摂動として消費
- **I3: 雪・泥の轍** — 永続変形テクスチャ＋地形シェーダの変位・レイヤ露出（最重量）

推奨順: W0 → W1 → I1 → W2 → W2.5 → I2 → W3 → I3 → W4（W/I は依存が薄いので入れ替え可。
I2 のみ W1 に依存）。
