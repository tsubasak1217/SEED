# SEED レンダリングフロー（現行実装）

本ドキュメントは **実コードから確認した現行の 1 フレームのパス構成** を図解する。
フェーズ名・用語は `docs/rendering_roadmap.md` に準拠する（正典はロードマップ側）。

- 主な実装: `runtime/src/engine/core/app_base/app/frame_renderer.rs` の `App::handle_redraw_requested`
- パス開始ヘルパ: `runtime/src/engine/core/renderer/mod.rs` の `begin_*_pass*` 系
- 記載した行番号は調査時点（branch `fix/edit-physics`）のもの。

> 表記ルール: コードで確認できた事実のみを書く。確認できなかった箇所は明示的に **未確認** と書く。

---

## 1. 1 フレームの全体パス構成

以下は `handle_redraw_requested` が **コマンドエンコーダへ記録する順序** である。
（＝GPU 実行順。CPU 側の収集処理は図では省略）

```mermaid
flowchart TD
    A0["フレーム先頭 CPU<br/>入力ポンプ / スクリプトフレーム進行<br/>地形GPU退役 / 地形LODティック"] --> A1
    A1["renderer.begin_frame<br/>get_current_texture"] --> A2
    A2["shadow.prepare_frame<br/>CSM/スポット行列とshadow_index確定"] --> C1

    subgraph COMPUTE["コンピュート段"]
      C1["Skin Compute Pass"] --> C2["Particle Sim Pass<br/>エミッタありのみ"]
      C2 --> C3["Meshlet Cull Pass<br/>対象>0のみ"]
    end

    C3 --> P0

    subgraph PREVIEW["カメラプレビュー描画（Edit・カメラ選択時のみ）"]
      P0["プレビューCSM深度<br/>shadow_preview.record"] --> P1["プレビュー G-Buffer パス"]
      P1 --> P2["プレビュー Deferred ライティング"]
      P2 --> P3["プレビュー フォワード Load パス<br/>skybox / 半透明 / 3Dスプライト"]
    end

    P3 --> S1["シャドウ深度パス<br/>CSM 3枚 + スポット最大4灯"]
    S1 --> S2["RT 加速構造ビルド<br/>BLAS/TLAS・needs_tlas時のみ"]
    S2 --> S3["DDGI プローブ更新 Compute<br/>gi=rt のみ"]
    S3 --> S4["Cluster Build Compute<br/>16x9x24・透視カメラのみ"]

    S4 --> G1

    subgraph GBUF["G-Buffer 段（deferred_active のみ）"]
      G1["G-Buffer パス<br/>不透明のみ MRT x4 + 深度"] --> G2["Hi-Z ピラミッド + 遮蔽ディスパッチ<br/>opt-in・結果は次フレーム"]
    end

    G2 --> L1

    subgraph LIGHT["ライティング段（deferred_active のみ）"]
      L1["AO 生成 + いもす法ブラー<br/>ao != off"] --> L2["シャドウマスク生成 + バイラテラル<br/>RT影 + ソフト影灯あり"]
      L2 --> L3["Deferred ライティングパス<br/>フルスクリーン → scene_hdr"]
      L3 --> L4["SSGI 生成 + ブラー<br/>gi=ssgi・結果は次フレーム使用"]
    end

    L4 --> R1["反射パス + Additive 合成<br/>reflection != off"]
    R1 --> R2["屈折背景ピラミッド生成<br/>refract_active"]

    R2 --> M1

    subgraph MAIN["メインパス（フォワード / 半透明）"]
      M1["begin_scene_pass_load_to（deferred）<br/>または begin_scene_pass_to（forward）"] --> M2["レターボックス帯塗り<br/>Play・Bar系のみ"]
      M2 --> M3["スカイボックス"]
      M3 --> M4["背景ゾーン2Dスプライト"]
      M4 --> M5["不透明フォワード描画<br/>deferred無効時のみ"]
      M5 --> M6["半透明 距離ソート描画<br/>tp_sorted"]
      M6 --> M7["sequential grab 逐次パス<br/>有効時のみパス分割"]
      M7 --> M8["グリッド / アウトライン / スプライト"]
    end

    M8 --> W1

    subgraph WB["WBOIT（tp_wboit のみ）"]
      W1["accum + reveal 蓄積パス"] --> W2["WBOIT 合成 2パス<br/>背景濾過 → 自色加算"]
    end

    W2 --> O1["エディタオーバーレイパス<br/>ギズモ/軸/ワイヤ/アイコン"]
    O1 --> O2["GPU パーティクル描画パス<br/>hdr_view へ Load"]
    O2 --> O3["ブルーム<br/>bloom_enabled"]
    O3 --> O4["トーンマップ → LDR中間<br/>ビネット有効時は前段に挿入"]
    O4 --> O5["キャンバスオーバーレイパス → LDR<br/>scene_canvas_ss のみ"]
    O5 --> O6["present_to_swapchain<br/>FXAA or コピー"]
    O6 --> O7["カメラプレビュー ブリット<br/>右下小窓"]
    O7 --> O8["ID パス<br/>Edit / Pause のみ + ピック読み戻し予約"]
    O8 --> O9["frame.finish<br/>encoder.finish + submit + present"]
```

**図示したパス数（記録される GPU パス）**: コンピュート 3 + カメラプレビュー 4 + シャドウ/RT/DDGI/クラスタ 4 +
G-Buffer/Hi-Z 2 + ライティング段 4（＋各ブラー）+ 反射 2 + 屈折 1 + メイン 1（＋逐次分割）+
WBOIT 3 + オーバーレイ 1 + パーティクル 1 + ブルーム/トーンマップ/キャンバス/present 4 + プレビューブリット 1 + ID 1
= **最大 32 パス前後**（機能フラグとモードで大きく増減する）。

### 重要な順序上の注意（コードで確認）

- **カメラプレビューはメインカメラより「先」に記録される**（`frame_renderer.rs:2147` 付近 vs メイン G-Buffer `:4221`）。
  そのためプレビューは専用の CSM (`draw_ctx.shadow_preview`) と `LightingPass::CameraPreview` の
  BindGroup を使う必要がある（メイン CSM はこの時点で未描画）。ソース走査ユニットテスト
  `camera_preview_pass_uses_preview_lighting_resources`（`frame_renderer.rs:6658`）がこの規約を検査している。
- **WBOIT 合成はエディタオーバーレイより「前」**（`frame_renderer.rs:5062-5073` のコメント）。
  合成は no_depth フルスクリーンクアッドのため、後に置くとギズモを上書きしてしまう。
- **プレビューのブリットは present の「後」**（`:5693`）＝スワップチェーンへ直接貼る。

---

## 2. 各パスの詳細

### 2.1 フレーム先頭処理（CPU）

| 項目 | 内容 |
|---|---|
| 目的 | 入力ポンプ、スクリプトフレーム進行、地形 GPU リソースの遅延退役、地形 LOD ティック |
| 入出力 | GPU パスなし |
| シェーダ | なし |
| 実装 | `frame_renderer.rs:272-303`（`update_gamepad` / `advance_script_frame` / `process_terrain_gpu_retire` / `tick_terrain_lod`） |
| Edit / Play | 差なし。ただし `process_terrain_gpu_retire` は **必ず `begin_frame` より前** に呼ぶ規約（snatch lock 再帰パニック回避、`:274-276` のコメント） |

### 2.2 begin_frame

| 項目 | 内容 |
|---|---|
| 目的 | スワップチェーンテクスチャ取得とエンコーダ生成 |
| 入出力 | 出力: `RenderFrame`（color_view / depth_view / encoder） |
| シェーダ | なし |
| 実装 | `renderer/mod.rs:614` `begin_frame`、呼び出しは `frame_renderer.rs:1133` |
| Edit / Play | 差なし。GPU バックプレッシャーはここに現れる（`perf_begin_frame_ms`） |

### 2.3 Skin Compute / Particle Sim / Meshlet Cull

| 項目 | 内容 |
|---|---|
| 目的 | スキニング（joint 適用）、GPU パーティクルのシミュレーション、LOD0 不透明メッシュレットの可視カリング |
| 入出力 | 頂点/インスタンス/間接コマンドバッファ（ストレージ） |
| シェーダ | `skin_compute.wgsl` / `particle_sim.wgsl` / `meshlet_cull.wgsl` |
| 実装 | `frame_renderer.rs:1445`（Skin, 全バッチで 1 パス共有）/ `:1478`（Particle）/ `:1646`（Meshlet Cull） |
| Edit / Play | 差なし。パーティクルはエミッタ 0 でパスを開かない。メッシュレットカリングは `MULTI_DRAW_INDIRECT_COUNT` 対応 GPU のみ稼働（非対応は `draw_indexed` へ自動フォールバック） |

### 2.4 カメラプレビュー（Edit 専用）

| 項目 | 内容 |
|---|---|
| 目的 | 選択中のカメラアクターの映像を小窓に出す。**本番と同じ Deferred 経路**で描く（旧フォワード小窓は撤去済み） |
| 入出力 | 出力: `preview.gbuffer_views[0..3]` + `preview.depth_view` → `preview.color_view` |
| シェーダ | `gbuffer_write.wgsl` / `terrain_gbuffer_write.wgsl` / `grass_gbuffer.wgsl` → `deferred_lighting.wgsl`（rt_off 版）→ `skybox.wgsl` / `shader_transparent.wgsl` / `sprite.wgsl` |
| 実装 | プレビュー CSM `:2072`、G-Buffer `:2147`（`begin_gbuffer_pass_to_depth`）、ライティング `:2216`（`begin_deferred_lighting_pass_to`）、フォワード Load `:2236`（`begin_offscreen_load_pass`）、ブリット `:5693`（`begin_blit_pass`） |
| Edit / Play | **Edit のみ**（`is_3d_scene = in_editor && !use_ortho_2d_camera`、`in_editor = mode==Edit \|\| paused`、`:458`）。かつ `selected_cam_data` が Some のとき（`:1805`）。Play では描かれない |
| 制限 | AO / SSGI / RT 影 / シャドウマスクは無効（ダミー供給）。クラスタは `ClusterParams.enabled=0`（全ライト線形走査）。`view_mode` は Lit 固定。メッシュレットカリングは使わない（間接コマンドがメインカメラ基準のため） |

### 2.5 シャドウ深度パス

| 項目 | 内容 |
|---|---|
| 目的 | 方向光 CSM 3 カスケード＋スポット最大 4 灯の深度描画 |
| 入出力 | 出力: `dir_tex`（`Depth32Float` の 3 レイヤ配列, 2048x2048）、スポット 1024x1024 |
| シェーダ | `depth_prepass.wgsl`（深度のみ）。読み取り側は `shadow.wgsl`（group4 binding2..5） |
| 実装 | 行列準備 `frame_renderer.rs:1157`（`shadow.prepare_frame`）、深度記録 `:3942`（`shadow.record`）。定数は `renderer/shadow.rs:43-54`（`CSM_CASCADE_COUNT=3` / `SHADOW_MAP_SIZE=2048` / `SPOT_SHADOW_SIZE=1024` / `MAX_SHADOW_SPOTS=4` / `CSM_SPLIT_LAMBDA=0.5`） |
| Edit / Play | 差なし（キャスターが 0 なら 0 コストでスキップ）。ただし CSM はカメラ固有のため Edit ではデバッグカメラ基準。散布モデル（`kind=Model` プロップ）もキャスターに含む |

### 2.6 RT 加速構造ビルド（BLAS / TLAS）

| 項目 | 内容 |
|---|---|
| 目的 | RT 影 / RT-GI / RT 反射 / RT 屈折 が使う TLAS の構築 |
| 入出力 | 出力: TLAS・BLAS キャッシュ・アルベドバッファ（DDGI / RT 反射と共有） |
| シェーダ | なし（`build_acceleration_structures`） |
| 実装 | `frame_renderer.rs:3965`（ゲートは `draw_ctx.rt_shadow.is_some() && resolved_features.needs_tlas()`）、実体は `renderer/rt_shadow.rs:333` `prepare_and_build` |
| Edit / Play | 差なし。散布モデルは `"scatter://{model_path}"` キーで BLAS 共有。`MAX_RT_INSTANCES=4096` 超は警告付きクランプ |

### 2.7 DDGI プローブ更新 Compute

| 項目 | 内容 |
|---|---|
| 目的 | プローブ格子の八面体アトラス（放射輝度＋可視性）をローテーション更新 |
| 入出力 | 出力: `GI_ATLAS_FORMAT = Rgba16Float` の放射輝度アトラス(8x8) と可視性アトラス(16x16) |
| シェーダ | `ddgi_common.wgsl` + `ddgi_probe_update.wgsl`（`EXPERIMENTAL_RAY_QUERY` 必須） |
| 実装 | `frame_renderer.rs:4094-4097`（`gi_on = draw_ctx.gi.is_attached() && resolved_features.rt_gi()`）、`renderer/ddgi/mod.rs:76,91-99` |
| Edit / Play | 差なし。`GiParams` は毎フレーム書き込む（`gi_on=false` のときは `enabled=0` を書きフラットアンビエントへ戻す）。SSGI の場合は compute 不要 |

### 2.8 Cluster Build Compute（クラスタードライティング）

| 項目 | 内容 |
|---|---|
| 目的 | メインカメラ視錐台を 3D フロクセルへ分割し、各クラスタへ影響する局所ライト（point/spot/rect）のインデックスを集める |
| 入出力 | 出力: `grid_buffer`（`array<ClusterCell>` = offset/count）、`indices_buffer`（`array<u32>`）、`cursor_buffer`（atomic。毎フレーム 0 クリア） |
| シェーダ | `cluster_build.wgsl`（共有定義 `cluster_common.wgsl`）。消費側は `lighting_eval.wgsl` |
| 実装 | `frame_renderer.rs:4142-4176`、定数は `renderer/clustered.rs:42-70` |
| 構成 | `CLUSTER_TILES_X=16` / `CLUSTER_TILES_Y=9` / `CLUSTER_SLICES_Z=24`（指数分割）= `CLUSTER_COUNT=3456`、`MAX_LIGHTS_PER_CLUSTER=256`、ワークグループ `4x4x4` |
| Edit / Play | タイル分割の正規化に使う矩形が違う。Play（非 Pause）は `game_viewport`、それ以外は `(0,0,win_w,win_h)`（`:4148-4152`）。正射／2D オルソカメラでは `saved_shadow_cam` が None になり `cluster_on=false`（全ライト線形走査へフォールバック） |

### 2.9 G-Buffer パス

| 項目 | 内容 |
|---|---|
| 目的 | **不透明 Lit ジオメトリのみ** を 4 枚の MRT ＋深度へ焼く |
| 入出力 | 出力: `gbuffer0..3` + 共有深度 |
| シェーダ | `gbuffer_write.wgsl`（メッシュ/スキン）、`terrain_gbuffer_write.wgsl`（地形 triplanar レイヤブレンド）、`grass_gbuffer.wgsl`（プロシージャル草） |
| 実装 | `frame_renderer.rs:4221`（`begin_gbuffer_pass_to`）、パイプラインは `renderer/gbuffer.rs`、描画は `gbuffer::draw_gbuffer_indirect` |
| ゲート | `deferred_active = post_fx.deferred && !edit_view_2d && !scene_wireframe && scene_is_lit`（`:3681`） |
| Edit / Play | Play では `game_viewport` の viewport/scissor を適用（帯外にジオメトリを焼かない、`:4225-4230`）。Edit のワイヤーフレーム表示・2D シーンビューは `deferred_active=false` になりフォワードへ落ちる。`scene_is_lit` は Play では常に true、Edit ではシーンビュー表示モードに従う |
| カリング | 視錐台外・遠方の地形チャンク（`terrain_culled`）はスキップ |

#### G-Buffer レイアウト

| RT | 定数 | フォーマット | 格納内容 |
|---|---|---|---|
| g0 | `GBUFFER0` | `Rgba8Unorm` | `rgb` = albedo（リニア）、`a` = occlusion |
| g1 | `GBUFFER1` | `Rgba16Float` | `xyz` = ワールド法線（シェーディング法線）、`w` = authored 法線フラグ（0＝深度復元 Ng を使う、1＝草・地形など信頼できる法線を使う） |
| g2 | `GBUFFER2` | `Rgba8Unorm` | `r` = metallic、`g` = roughness、`b` = diffuse_transmission、`a` = **user_data**（マテリアルの汎用ユーザーデータ 0..1・8bit） |
| g3 | `GBUFFER3` | `Rgba16Float` | `rgb` = emissive（HDR）、`w` = **surface_id**（下位 4bit = セマンティックタグ / 続く 2bit = シェーディングモデル ID） |
| depth | `DEPTH_FORMAT` | `Depth24PlusStencil8` | `depth_write_enabled: true` / `CompareFunction::Less`。ライティング側は `texture_depth_2d` を `textureLoad` のみで読み、`inv_view_proj` でワールド座標を復元 |

- フォーマット定義: `renderer/gbuffer.rs:38-44`、深度は `renderer/mod.rs:132`、パイプラインの depth_stencil は `gbuffer.rs:215-224`
- 書き込み側の権威: `shaders/gbuffer_write.wgsl`（`GBufferOut` / `fs_gbuffer`）
- 読み出し側: `shaders/deferred_lighting.wgsl`（`GBUFFER_NORMAL_AUTHORED_THRESHOLD` で g1.w を分岐、g2.a / g3.a を `Surface` へ復元）

#### 情報系チャンネル（g2.a / g3.a）

「表面の物性」ではなく「この点は何なのか」を運ぶチャンネル。ライティングは一切参照せず、
合成（第3層）が読むための素材として第1層が申告する。

| 値 | 宣言場所 | 粒度 | 格納先 | 精度 |
|---|---|---|---|---|
| `render_tag`（セマンティックタグ） | `ModelComponent::render_tag`（`.scene` に保存） | **アクタ単位** | g3.a の下位 4bit | 16 種（0 = タグ無し） |
| `shading_model`（シェーディングモデル ID） | `Material::shading_model`（`.smdl`/`.mat` に保存） | マテリアル単位 | g3.a の続く 2bit | 4 種（0 = DefaultPBR。現状これのみ実装） |
| `user_data`（汎用ユーザーデータ） | `Material::user_data`（同上） | マテリアル単位 | g2.a | 8bit＝1/255 刻み（0..1） |

- ビット規約の単一の真実source: `renderer/surface_id.rs`（Rust）と `shaders/surface.wgsl` の
  `pack_surface_id` / `unpack_surface_id`（WGSL）。両者の一致は `surface_id.rs` のユニットテストが固定する。
- `g3.a` は `Rgba16Float`。half float は整数 2048 まで誤差ゼロで表現できるため、パック値（最大 63）は
  **完全に無損失**で往復する。使用は 6bit のみで、無損失域の残り 5bit が将来用に空いている。
- **新規 MRT を足していない**。棚卸しの結果 g2.a / g3.a が唯一の完全な空きチャンネルであり、
  そこへ詰めたので G-Buffer の帯域増加はゼロ・既存チャンネルの精度低下もゼロ。
- `render_tag` の配管は `ModelUniform.normal_matrix` の **4 列目**（法線変換は常に `w=0` の
  ベクトルに掛かるため数学的に寄与しない 16 byte）を「インスタンス拡張スロット」として転用する。
  `ModelUniform` は 128 byte のまま＝全インスタンス×全ノードの毎フレーム転送量は 1 byte も増えていない。
- 地形（`terrain_gbuffer_write.wgsl`）・草（`grass_gbuffer.wgsl`）は両チャンネルに 0 を書く
  （＝タグ無し・DefaultPBR・user_data 0。既定値と一致するので合成側の分岐は不要）。

### 2.10 Hi-Z オクルージョン（opt-in）

| 項目 | 内容 |
|---|---|
| 目的 | 地形チャンクの world AABB を Hi-Z 深度へ投影・比較して完全遮蔽チャンクを求める |
| 入出力 | 入力: G-Buffer 深度。出力: 可視性バッファ → staging（**次フレーム** の `try_read_results` が受け取る 1 フレーム遅延） |
| シェーダ | `hiz_copy_depth.wgsl` / `hiz_gen_mip.wgsl` / `hiz_occlusion.wgsl` |
| 実装 | `frame_renderer.rs:4360-4392`（`build_pyramid` / `dispatch_occlusion` / `schedule_readback`）、submit 後のマップ予約は `:6192` `map_after_submit` |
| ゲート | 環境変数 `SEED_OCCLUSION_CULL=1`（`terrain_scatter_ops::HIZ_OCCLUSION_ENABLED`、`terrain_scatter_ops.rs:281`）のときのみ、かつ deferred のみ、かつ `readback_idle()` のフレームのみ |
| Edit / Play | 差なし。結果が無いチャンクは「不明なら描く」保守側 |

### 2.11 AO 生成パス

| 項目 | 内容 |
|---|---|
| 目的 | 半解像度 AO を焼き、いもす法で均す。ライティングは occlusion に乗算（アンビエント/DDGI/バウンスのみに効く） |
| 入出力 | 入力: G-Buffer + 深度（+ RT-AO なら TLAS）。出力: `ao_raw` → いもす法 → `ao_b`（`AO_FORMAT = IMOS_BLUR_FORMAT`。`.r` のみ使用） |
| シェーダ | `ao_common.wgsl` + `ao_ssao.wgsl`（SSAO）または `ao_rt.wgsl`（RT-AO）、ブラーは `imos_blur.wgsl` |
| 実装 | `frame_renderer.rs:4422`（`begin_ao_pass_to`）、`:4436`（`ao_p.blur`）、`renderer/ao.rs:44,98-100,157,165` |
| ゲート | `ao_effective != Off`。`ao_effective` は `deferred_active` のときのみ `resolved_features.ao`（`:3693-3698`）。RT-AO は `ao==Rt && ao_p.rt.is_some()` のときのみ、それ以外は SSAO へ安全側フォールバック |
| Edit / Play | 差なし（半解像度・UV ベースのため viewport 適用なし） |

### 2.12 シャドウマスク生成パス

| 項目 | 内容 |
|---|---|
| 目的 | RT ソフト影のディザ状ノイズ対策。選定した最大 4 灯について `rt_shadow_factor` を半解像度で評価しマスク化・デノイズする |
| 入出力 | 出力: 半解像度 `texture_2d_array`（`Rgba16Float`、レイヤ＝スロット）。`.rgb` = 透過率、`.a` = half-res ビュー空間深度（バイラテラルのガイド） |
| シェーダ | `shadow_mask.wgsl`（内部で `rt_shadow_on.wgsl` を連結）、デノイズは `shadow_mask_bilateral.wgsl` の `blur_cs` コンピュート |
| 実装 | `frame_renderer.rs:4475`（`begin_shadow_mask_pass_to`）、`:4498`（`smp.blur`）、`renderer/shadow_mask.rs:1-63`、`renderer/shadow_mask_bilateral.rs:63,112` |
| ゲート | `shadow_mask_active = deferred_active && rt_on && !shadow_mask_selection.is_empty() && pipelines.shadow_mask.is_some()`（`:3796`）。上限（`RT_SHADOW_MASK_LIGHTS=4`）を超える灯は `lighting_eval.wgsl` からインラインで `rt_shadow_factor` を評価する |
| Edit / Play | 差なし |

### 2.13 Deferred ライティングパス

| 項目 | 内容 |
|---|---|
| 目的 | G-Buffer からフルスクリーンで PBR ライティングを復元し HDR シーンへ書く |
| 入出力 | 入力: g0..g3 / depth / AO(`ao_b`) / SSGI(前フレーム `ssgi_b`) / シャドウマスク(`mask_b`) / ライト・CSM・クラスタ・DDGI。出力: `RT_SCENE_HDR`（`Rgba16Float`） |
| シェーダ | `deferred_lighting.wgsl`（`fs_deferred`）。連結順は `cluster_common.wgsl` + `pbr_common.wgsl` + `ddgi_common.wgsl` + `light_common.wgsl` + `shadow.wgsl` + `rt_shadow_off.wgsl` または `rt_shadow_on.wgsl`(+`rt_shadow_tint_avg.wgsl` / バインドレス版は `bindless_common.wgsl`+`rt_shadow_tint_bindless.wgsl`) + `surface.wgsl` + `lighting_eval.wgsl` + `deferred_lighting.wgsl` |
| 実装 | `frame_renderer.rs:4538`（`begin_deferred_lighting_pass_to`）、パイプラインは `renderer/deferred.rs`（`:310-321` / `:336-348` / `:146-150` に連結のユニットテストあり） |
| Edit / Play | Play では `game_viewport` の viewport/scissor を適用（`:4539-4543`）。`view_mode`（シーンビュー表示モード）は Edit のみ有効で Play は Lit 固定（`:970-980` 付近の `scene_view_mode_code`） |

#### group1（G-Buffer 入力）バインディング — `deferred.rs:251-297`

| binding | 内容 |
|---|---|
| 0-3 | g0 / g1 / g2 / g3 |
| 4 | depth（DepthOnly aspect） |
| 5 | G-Buffer サンプラー |
| 6,7 | AO ビュー（AO=Off なら白 1x1）、linear サンプラー |
| 8,9 | SSGI ビュー（未収束ならダミー）、linear サンプラー |
| 10,11 | シャドウマスク配列（非対象ならダミー白）、linear サンプラー |

group0 = カメラ、group4 = ライト複合（`light_common.wgsl` 由来。RT バリアントは TLAS を binding6、平均アルベドを binding14 に含む）、group3 = バインドレス色付き影（対応 GPU のみ）。
反射は **別のフルスクリーンパイプライン** であり group1 には含まれない（加算合成で後から重ねる）。

### 2.14 SSGI 生成パス

| 項目 | 内容 |
|---|---|
| 目的 | スクリーンスペース間接照明の生成。**結果は次フレームのライティングが読む（1 フレーム遅延）** |
| 入出力 | 入力: G-Buffer + `scene_hdr`（今フレームの不透明 HDR）。出力: `ssgi_raw` → いもす法 → `ssgi_b`（`SSGI_FORMAT = IMOS_BLUR_FORMAT`。`.rgb`） |
| シェーダ | `ssgi_common.wgsl` + `ssgi_gen.wgsl`、ブラーは `imos_blur.wgsl` |
| 実装 | `frame_renderer.rs:4628`（`begin_ssgi_pass_to`）、`:4636`（`sp.blur`）、`renderer/ssgi.rs:19,27,110` |
| ゲート | `ssgi_active = deferred_active && resolved_features.gi == Ssgi`（`:3701`）。読める条件は `ssgi_readable = ssgi_active && self.ssgi_warmed && !ssgi_reallocated`（`:3822`）。未収束フレームは `GiParams.enabled=0` でフラットへ倒れる |
| Edit / Play | 差なし |

### 2.15 反射パス + 合成

| 項目 | 内容 |
|---|---|
| 目的 | G-Buffer + `scene_hdr` から反射色を作り、Additive で `scene_hdr` へ加算 |
| 入出力 | 出力: `RT_REFLECTION`（`REFLECTION_FORMAT = Rgba16Float`）→ 合成で `scene_hdr` |
| シェーダ | `reflection_common.wgsl` + `ddgi_common.wgsl` + `reflection_ssr.wgsl`（SSR）または `reflection_rt.wgsl`（RT。バインドレス有無で `reflection_rt_hit_on.wgsl` / `reflection_rt_hit_off.wgsl`）、合成は `reflection_composite.wgsl`（ブレンド `One + One`） |
| 実装 | `frame_renderer.rs:4707`（`begin_reflection_pass_to`）、`:4729`（`begin_reflection_composite_pass_to`）、`renderer/reflection.rs:34,68-70,195-236` |
| ゲート | `reflection_effective`：`deferred_active` のときのみ `resolved_features.reflection`、フォワード時は常に Off（`:3685-3691`） |
| Edit / Play | Play では反射パス・合成パスの両方に `game_viewport` の viewport/scissor を適用（`:4708-4712` / `:4730-4734`） |
| 未確認 | reflection 側に明示的なブラー処理があるかは確認できなかった |

### 2.16 屈折背景ピラミッド

| 項目 | 内容 |
|---|---|
| 目的 | すりガラス表現用。不透明ライティング完成後の `scene_hdr` を mip0 へコピーし、以降のミップをダウンサンプル→いもす法ブラーで作る |
| 入出力 | 入力: `RT_SCENE_HDR` テクスチャ。出力: 屈折背景ミップチェーン（`refract_pyramid.full_view()`） |
| シェーダ | `refract_common.wgsl` / `refract_rt.wgsl` / `refract_ss.wgsl`（半透明フラグメント側でサンプル）、ブラーは `imos_blur.wgsl` |
| 実装 | `frame_renderer.rs:4751`（`refract_pyramid.record`）。同時に `LightMeta.translucency_rt`（offset 12）へ屈折ビット（bit1）を追記。WBOIT のときは bit2 も追記して界面 tint の二重計上を防ぐ |
| ゲート | `refract_active = translucency_rt_on && deferred_active && has_tp`（`:3840`） |
| 既知の制限 | 背景に skybox は含まれない（skybox はこの後のメインパスで描かれるため） |

### 2.17 メインパス（フォワード / 半透明）

| 項目 | 内容 |
|---|---|
| 目的 | deferred の結果の上に、スカイボックス・半透明・スプライト・（フォワード時は）不透明を重ねる |
| 入出力 | 出力: `RT_SCENE_HDR` + 共有深度・ステンシル |
| シェーダ | `skybox.wgsl` / `shader_static_vertex.wgsl`+`shader_skinned_vertex.wgsl`+`shader_fragment.wgsl`（不透明フォワード）/ `shader_transparent.wgsl`（半透明）/ `sprite.wgsl` / `sprite_outline.wgsl` / `gizmo_line.wgsl` / `bar_fill.wgsl` / `unlit.wgsl` |
| 実装 | `frame_renderer.rs:4770-4775`：`deferred_active` なら `begin_scene_pass_load_to(hdr_view)`（Load 再開）、そうでなければ `begin_scene_pass_to(hdr_view, clear_color)`（Clear） |
| パス内の順序 | 帯塗り（`:4780-4812`）→ スカイボックス（`:4828`）→ 背景ゾーン 2D スプライト（`:4843`）→ 不透明フォワード（`:4862`、deferred 無効時のみ）→ 半透明距離ソート（`:4886`）→ グリッド（`:5000`）→ 3D スプライトアウトライン（`:5013`）→ 3D スプライト（`:5028`）→ 2D スプライト（`:5041`、非 SS のみ） |
| Edit / Play | Play（非 Pause）かつ `play_viewport_ok` のとき `game_viewport` の viewport/scissor を適用（`:4817-4821`）。LetterBox / PillarBox のときは viewport 設定「前」に帯エリアを `BarFillPipeline` で塗る。Edit のクリアカラーはダークグレー／2D は紺色、Play はゲームカメラのクリアカラー |

### 2.18 WBOIT

| 項目 | 内容 |
|---|---|
| 目的 | 順序独立半透明。accum / reveal へ蓄積してフルスクリーン合成 |
| 入出力 | 出力: `wboit_accum`（`Rgba16Float`、ブレンド `One+One`、Clear `(0,0,0,0)`）、`wboit_reveal`（`Rgba16Float`、ブレンド `Dst*Src`、Clear `(1,1,1,1)`）。合成先は `scene_hdr`（Load） |
| シェーダ | 蓄積 `shader_wboit.wgsl`（`fs_wboit`）、合成 `post_wboit_composite.wgsl`（2 パス: `wboit_composite_bg` で `scene *= ΠT`、`wboit_composite_self` で `final += avg*coverage`） |
| 実装 | `frame_renderer.rs:5079`（`begin_wboit_pass_to`、`renderer/mod.rs:954`）、合成 `:5133` 付近（`transparency::composite_wboit`、`renderer/transparency.rs:410-460`）。RT フォーマット定数は `transparency.rs:64-75`、ブレンドは `:216-254` |
| 深度 | `depth_write_enabled: false` / `LessEqual`。パス自体は深度を `LoadOp::Load`（不透明深度でテストのみ） |
| Edit / Play | Play では **蓄積パスにのみ** `game_viewport` を適用（`:5092-5096`）。合成パスには適用しない（accum/reveal を 1:1 テクセルでサンプルするため。二重スケール回避、`:5121-5130` のコメント） |

### 2.19 エディタオーバーレイパス

| 項目 | 内容 |
|---|---|
| 目的 | ギズモ / 軸 / 各種ワイヤ / アイコン / 選択アウトラインを描く |
| 入出力 | カラー = `hdr_view` を Load、深度 = 共有深度を Load（テストのみ）、ステンシル = Clear(0)（選択アウトラインのマスク用） |
| シェーダ | `gizmo_line.wgsl` / `axis_gizmo.wgsl` / `outline.wgsl` / `icon_overlay.wgsl` / `text.wgsl` |
| 実装 | `frame_renderer.rs:5147`（`begin_overlay_pass_to`、`renderer/mod.rs:913`） |
| Edit / Play | 実質 Edit 向けの内容（各要素が Edit 選択状態に依存）。パス自体はモード分岐なしで開かれる |

### 2.20 GPU パーティクル描画

| 項目 | 内容 |
|---|---|
| 目的 | シミュレーション済みパーティクルをトーンマップ前の HDR へ加算/アルファ合成 |
| 入出力 | カラー = `hdr_view`（Load）、深度 = 共有深度（Load、テストのみ・書込なし） |
| シェーダ | `particle_draw.wgsl` |
| 実装 | `frame_renderer.rs:5536`（`begin_particle_pass_to`、`renderer/mod.rs:1006`） |
| Edit / Play | Play では `game_viewport` を実サーフェスへ再クランプして適用（`:5528-5540`）。Edit は常にターゲット全面 |
| 既知の TODO | Alpha ブレンドのエミッタ単位粗ソートは未実装（現状は登録順） |

### 2.21 ブルーム → トーンマップ → キャンバスオーバーレイ → present

| 項目 | 内容 |
|---|---|
| 目的 | 高輝度抽出＋加算、HDR→LDR 変換、UI 合成、スワップチェーンへの書き出し |
| 入出力 | ブルーム: `scene_hdr` を読み書き。トーンマップ: `scene_hdr`（＋ビネット時は `RT_POST_INTER`）→ `RT_LDR`。キャンバス: `RT_LDR` へ直描き。present: `RT_LDR` → スワップチェーン |
| シェーダ | `post_bloom_prefilter.wgsl` / `post_bloom_down.wgsl` / `post_bloom_up.wgsl` / `post_vignette.wgsl` / `post_tonemap.wgsl`（+`tonemap_ops.wgsl`）/ `post_fxaa.wgsl` |
| 実装 | ブルーム `frame_renderer.rs:5559`（`post.run_bloom`）、トーンマップ `:5581`（`frame.tonemap_to_ldr`）、キャンバスオーバーレイ `:5595`（`begin_canvas_overlay_pass_to`）、present `:5683`（`present_to_swapchain`、`renderer/mod.rs:1383`） |
| ゲート | `bloom_on = post_fx.bloom_enabled`、`fxaa_on = post_fx.fxaa_enabled`、`vignette_on = post_vignette_enabled`（`:3704-3707`）。ビネット有効時のチェーンは `hdr → vignette → tonemap`。ビネット強度は現状ハードコード定数 `VIGNETTE_INTENSITY = 0.4`（`:5570`。将来プロジェクト設定へデータ駆動化予定とコメント） |
| Edit / Play | キャンバスオーバーレイは `scene_canvas_ss = ss_layout && !edit_view_2d`（`:495`）のときのみ。UI をトーンマップ後の LDR へ描くことで UI が暗化しない |

### 2.22 ID パス（ピッキング）

| 項目 | 内容 |
|---|---|
| 目的 | アクター / キャンバス / コライダー / 各種ギズモの ID をオフスクリーンへ描き、1 ピクセル読み戻して選択・ドロップ位置を決める |
| 入出力 | 出力: `id_buffer`（読み戻し用 staging へ 1px コピー） |
| シェーダ | `id_pass.wgsl` / `canvas_id.wgsl` |
| 実装 | `frame_renderer.rs:5990`（`begin_id_pass`、`renderer/mod.rs:1553`）、読み戻し予約 `:6180` 付近（`frame.schedule_id_copy`） |
| Edit / Play | **`in_editor`（= `mode==Edit \|\| paused`）のときのみ**（`:5701`）。Play 実行中は一切走らない。読み戻し優先度は drop > add_actor > pick |

---

## 3. ライティング段の切り替え（機能マトリクス）

`SET_POST_FX` IPC の `features` オブジェクトが `RenderFeatures` へデシリアライズされ、
`RenderFeatures::resolve(rt_supported)` が GPU 対応状況で降格した `ResolvedFeatures` を作る。
以降のゲートはすべて `resolved_features` を見る（生の `render_features` は見ない）。

- パース: `runtime/src/engine/core/app_base/ipc.rs:1213-1215`
- 定義と降格: `runtime/src/engine/core/renderer/render_features.rs:128-145`（フィールド）、`:158-204`（`resolve`）
- serde は `rename_all = "lowercase"`。欠落キーは default で埋まる（旧エディタ互換）

| features キー | 値 | 既定 | 降格 | 効くパス |
|---|---|---|---|---|
| `shadow` | `rt` / `shadowmap` | `shadowmap` | RT 非対応 → `shadowmap`（`:161-164`） | `rt_shadow` インライン評価、シャドウマスクパス（`rt_on` 経由） |
| `gi` | `rt` / `ssgi` / `flat` | `flat` | RT 非対応 → `ssgi`（`:169-174`） | `rt` → DDGI プローブ更新 compute、`ssgi` → SSGI 生成パス、`flat` → フラットアンビエント |
| `reflection` | `rt` / `ssr` / `off` | `off` | RT 非対応 → `ssr`（`:177-182`） | 反射パス（`reflection_rt.wgsl` / `reflection_ssr.wgsl`）。**deferred 有効時のみ** |
| `ao` | `rt` / `ssao` / `off` | `off` | RT 非対応 → `ssao`（`:188-193`） | AO 生成パス（`ao_rt.wgsl` / `ao_ssao.wgsl`）。**deferred 有効時のみ** |
| `translucency` | `rt` / `raster` | `raster` | RT 非対応 → `raster`（`:199-202`） | 色付き影（`shadow=rt` 併用時のみ）＋屈折背景ピラミッド。**deferred 有効時のみ** |

- **TLAS 構築ゲートの単一集約点**: `ResolvedFeatures::needs_tlas()`（`render_features.rs:279-285`）。
  いずれかが `Rt` に解決されれば true。呼び出しは `frame_renderer.rs:3965`。
- **deferred ゲートはフレーム側で行う**（`resolve` は RT 降格のみを担当）:
  `reflection_effective` / `ao_effective` / `ssgi_active` は `deferred_active` が false なら Off へ倒れる。
- `features` 以外にも `SET_POST_FX` は `bloom` / `fxaa` / `bloom_intensity` / `transparency` /
  `deferred` / `refract_sequential_grab` / `view_mode` / `gi_intensity` / `reflection_intensity` /
  `ao_intensity` / 旧キー `gi_enabled` を受ける（`ipc.rs:1163-1216`）。

---

## 4. 半透明の分岐

### 4.1 距離ソート方式 と WBOIT 方式

```rust
// frame_renderer.rs:3663-3666
let tp_sorted = has_tp && transparency_mode == TransparencyMode::DistanceSort;
let tp_wboit  = has_tp && transparency_mode == TransparencyMode::Wboit;
```

- `has_tp = transparency::has_transparent(&transparent_models)`（`:3660`）
  = 可視インスタンスを持つ `AlphaMode::Blend` プリミティブが 1 件以上あるか（`transparency.rs:635-648`）。
- `transparency_mode = self.post_fx.transparency`（プロジェクト設定）。
  `TransparencyMode::from_str`（`transparency.rs:46-51`）は `"wboit"` のみ `Wboit`、それ以外（`"sort"` 含む）は `DistanceSort`。既定も `DistanceSort`。
- 2D シーンビュー（`edit_view_2d`）は `transparent_models` が空になり半透明処理は走らない。

### 4.2 距離ソートの実装

- ソートキーは `TransparentItem.dist_sq`（カメラ位置からの距離二乗、`transparency.rs:103`）。
- 収集は `gather_items`（`:651-693`）が `instance_centroid` とカメラ位置から算出。
- ソート本体（`draw_sorted`, `:757-776`）:
  ```rust
  let mut items = gather_items(models, camera_pos);
  items.sort_by(|a, b| b.dist_sq.partial_cmp(&a.dist_sq).unwrap_or(Ordering::Equal));
  ```
  背面（遠い）→ 前面（近い）の降順。`draw_sorted_rt`（`:800-817`）と `plan_sorted_sequential`（`:864-880`）も同一規約。
- 描画は 1 アイテムずつ `draw_one`（`:701-753`）、`first_instance = compact_idx` でインスタンス単位に発行。

### 4.3 RT 屈折パイプラインの選択

距離ソート / WBOIT のそれぞれで、フレームごとに独立して判定する（`:4891-4919` / `:5098-5119`）:

```rust
if let (Some(rt_bg), Some(rt_tp)) = (transparent_rt_bg_main.as_ref(), pipelines.transparent.rt.as_ref()) {
    // draw_sorted_rt / draw_wboit_rt（refract_rt.wgsl 経路）
} else {
    // draw_sorted / draw_wboit（refract_ss.wgsl 経路へ完全フォールバック）
}
```

RT 半透明パイプラインの構築条件は `transparency.rs:378`:
`rt_shadow::rt_shadows_supported() && bindless::bindless_supported()`。

### 4.4 sequential grab（距離ソート専用）

- `refract_sequential_active = tp_sorted && refract_active && post_fx.refract_sequential_grab`（`:3854`）。
- 有効時はメインパス内での通常半透明描画をスキップし（`:4886-4894`）、`:4926-4993` で
  「屈折関与アイテムの手前で `dirty` ならパスを閉じて `refract_pyramid.record` で再グラブ → パス再開」を繰り返す
  （ガラス越しのガラス）。完了後 `begin_scene_pass_load_to` でメインパスを Load 再開してオーバーレイへ続く。
- WBOIT は順序独立で「先に描いたガラス」の概念が無いため、設定が ON でもこのフラグは常に false。

### 4.5 Play スケーリング（game_viewport）の適用

- 定義: `compute_game_viewport`（`app/canvas_collect.rs:1236-1290`）が `ScalingMode`
  （VertMinus / HorPlus / LetterBox / PillarBox / LetterPillarBox / FullScale）ごとに
  `(vp_x, vp_y, vp_w, vp_h, proj_aspect, fov_y_rad)` を返す。クランプは `clamp_viewport_to_target`（`:1313-1332`）。
- 適用判定: `frame_renderer.rs:678-725` で Play かつ非 Pause のとき `is_main=true` の `CameraComponent` を探し、
  その `scaling_mode` から `game_viewport` を再計算する（見つからなければデバッグカメラへフォールバック、`:726-739`）。
- **RT サイズは変わらない**。`scene_hdr` / accum / reveal / G-Buffer はすべて実サーフェス全面（`frame.surface_size()`）で確保され、
  `game_viewport` は「その全面 RT 内のどの矩形へ描くか」を `set_viewport` / `set_scissor_rect` で制御するだけ（ブリットはしない）。
- リサイズ直後の 1 フレーム対策として、実サーフェスへ **一度だけ** クランプする集約点がある（`:4101-4117`、`play_viewport_ok`）。
  これで G-Buffer / ライティング / 反射 / メイン / 逐次屈折の全 `set_viewport` 箇所が一括で安全化される。
- 適用箇所: G-Buffer（`:4225`）、Deferred ライティング（`:4539`）、反射・反射合成（`:4708` / `:4730`）、
  メインパス（`:4817`）、sequential grab の各再開パス（`:4954` / `:4970` / `:4988`）、
  WBOIT 蓄積（`:5092`、合成には非適用）、パーティクル（`:5537`）。
- Edit との差: `game_viewport` の初期値は `(0, 0, win_w, win_h)`（`:636`）で、Edit では書き換えられず、
  かつ全ての適用箇所が `self.mode == RuntimeMode::Play` で弾かれるため常に全画面。

---

## 5. レンダリングの3層モデル（設計判断の基準）

本エンジンのレンダリングは以下の3層に分けて考える。新機能を足すときは
「どの層に属するか」「層の間の契約を壊さないか」をまず判定する。

```
第1層  G-Buffer         枠=エンジン / 中身=ユーザー（マテリアル）
        色・法線・粗さ・金属度・自己発光（＋深度は自動）
        ＋情報系: セマンティックタグ / シェーディングモデル ID / ユーザーデータ
              ↓
第2層  中間バッファ生成   製法=エンジン（SS系/RT系を選択式、off なら作らない）
        影マスク / GI / 反射 / AO / ID テクスチャ  ← 作られる物のリストは固定（第3層の入力契約）
              ↓
第3層  画面の合成        標準合成=エンジン / 介入点= shade()・ポストプロセス
```

**第1層（マテリアル段）** が担うのは **表面の自己申告** だけである。
`gbuffer_write.wgsl` / `terrain_gbuffer_write.wgsl` / `grass_gbuffer.wgsl` が書くのは
albedo・法線・metallic・roughness・diffuse_transmission・emissive・occlusion という
「この点の表面はどういう物性か」の記述であり、光源・影・遮蔽・間接光・反射の計算は一切含まない
（4 枚の MRT にライティング結果を焼く箇所はコード上に存在しない）。
スロットの定義と保証がエンジンの仕事、各ピクセルに何を書くかがユーザー（マテリアル）の自由。

第1層はさらに **「この点は何なのか」の自己申告**（情報系チャンネル）も運ぶ:
セマンティックタグ（アクタ単位・「敵」「インタラクト可能」等）、シェーディングモデル ID
（マテリアル単位・将来のトゥーン分岐用）、ユーザーデータ（マテリアル単位の自由 0..1 回線）。
いずれも物性ではなく**意味**であり、ライティングは参照しない。合成（第3層）が
「敵だけ縁取る」「濡れているところだけ暗くする」といった判断に使うための素材である。
格納先は G-Buffer の空きチャンネル（g2.a / g3.a）で、MRT は 4 枚のまま増えていない。

**第2層（中間バッファ生成）** は、第1層とライトデータから
影マスク / GI / 反射 / AO / **ID テクスチャ** という中間生成物を作る。製法（SS 系か RT 系か）は
機能マトリクス（`SET_POST_FX` features）で選択式・off なら生成しないが、
**製法が変わっても「作られる物のリスト」は増えない**。この固定リストが第3層の
安定した入力契約になっており、将来 SS 系↔RT 系を入れ替えても上下の層は無傷で残る。

**ID テクスチャ**（`Rgba32Float`・ライティング段と同解像度・`methods/drawer/id_pass.rs`）は
元来エディタのピッキング専用だったものを、合成が読める中間バッファへ昇格させたもの。
`rgb` = ワールド座標、`a` = `bitcast<f32>(actor_instance_id + 1)`（0 = 背景）で、規約は
ピッキングと完全に共通（変更していない）。`TEXTURE_BINDING` を付与済みで、
`IdBuffer::bind_group_layout()` / `create_bind_group()` からシェーダへ差せる。

生成タイミングと使い分けは以下:

| 状況 | ID パスを描くか | 理由 |
|---|---|---|
| Edit / Pause | 毎フレーム描く（従来どおり） | ピッキング・D&D のワールド座標取得が依存している |
| Play | **既定でスキップ**。`SEED_ID_PASS_IN_PLAY` を設定したときのみ毎フレーム描く | 全不透明ジオメトリをフル解像度でもう一度描く専用パス（16 byte/px）で、深度プリパス 1 本ぶんのコストがかかる |

> **使い分けの指針**: 「敵だけ」「インタラクト可能だけ」のような**種別**で足りるなら
> 第1層のセマンティックタグ（追加パスも追加帯域もゼロ）を使う。ID テクスチャは
> 「この 1 体だけ」という**個体の厳密な同定**が要るときにだけ使う。
> コストは `SEED_PERF_LOG=1` の `[PERF]` 行の `id=` フィールドで実測できる。

**第3層（合成）** はエンジンの標準 Deferred ライティング
（`deferred_lighting.wgsl` + `lighting_eval.wgsl` + クラスタ走査）が
**不変の光の物理** を1箇所で計算する。ユーザーの介入点は
シェーディングモデル（`shade()` = 1ライト分の光応答式）とポストプロセスに限定する。

この分界のおかげで、新しいマテリアル表現＝第1層の G-Buffer 出力だけ、
新しい光の機能（RT 影・DDGI 等）＝第2層の製法追加だけ、を考えればよい。

### 将来構想: 第3層のアセット化（未実装）

合成シェーダを固定バインディング契約の WGSL アセットとして差し替え可能にする構想がある:

- 段階 L3-a: `shade()`（1ライト分の応答式）のみをアセット化（契約が最小）
  - 前提となる素材は整備済み（第1層の情報系チャンネル＋第2層の ID テクスチャ）。
  - **未解決**: ID パスは現在フレーム最終盤（present コピーの後・`finish()` の直前）で描かれるため、
    同一フレームの合成からは読めない。L3 で ID を合成入力として使うなら、ID パスを
    G-Buffer 段の直後（深度が確定した位置）へ移す必要がある。移動はピック結果の
    描画順に影響し得るため、L3-a の着手時に単独の変更として行うこと。
- 段階 L3-b: 合成パス全体（G-Buffer＋第2層バッファ＋ライトデータ → HDR 1枚）をアセット化
- 成立条件: エンジン提供の WGSL 標準ライブラリ（クラスタ内ライト走査・影/GI サンプル関数）、
  コンパイル失敗時の組み込み標準へのフォールバック、バインディング契約のバージョン管理
- 標準レンダラー自身を同梱アセットとして出荷し、ユーザーは複製・改造から始める

パス構成そのものは差し替え対象にしない（RenderGraph 化・レンダラー差し替え基盤は
本エンジンの規模では負債と判断し採用しない）。
