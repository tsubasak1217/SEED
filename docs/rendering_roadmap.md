# レンダリング改善ロードマップ

2026-07 策定。本書がレンダリング改修の正典。フェーズ完了時に「状況」を更新すること。

## 前提（調査で確定した現状）

- 3Dメッシュ基盤は高水準: インスタンシング＋距離LOD(4段)＋GPUフラスタムカリング＋Indirect Draw、
  glTFフルセットPBR（Cook-Torrance GGX）、TOMLデータ駆動パイプライン（renderer/pipelines/*.toml）。
- 欠落層: ライト（方向光1灯がシェーダ内ハードコード）・シャドウ・ポストプロセス・環境表現・2Dバッチング。
- 死蔵資産: Hi-Zオクルージョン一式（hiz.rs＋WGSL3本、未接続）・深度プリパス（未接続）。
- HDR中間バッファなし（sRGBスワップチェーン直描き）。トーンマッピングは各メッシュシェーダ内のReinhard。
- frame_renderer.rs は約6400行の単一巨大関数（構造的負債。各フェーズのついでに触るパスをモジュール分割する）。
- wgpu 25.0.2。**インラインレイトレ（ray query）は DX12/Vulkan 両対応**（EXPERIMENTAL_RAY_QUERY、
  wgpu-hal 25.0.2 dx12/adapter.rs:473 で確認済み）。フルRTパイプラインはwgpu未実装（不要、インラインで足りる）。

## 確定済み仕様判断（ユーザー合意）

| 項目 | 決定 |
|------|------|
| ライト種 | directional / point / spot / **rect（エリアライト）** の4種 |
| 影（第1弾） | シャドウマップ: directional（CSM）＋ spot。point影・rect影は後続 |
| RT影 | 品質オプション扱い。シャドウマップを全環境の既定とし、対応GPUでインラインRT影へ切替可能に |
| OIT | Weighted Blended (WBOIT)。距離ソート方式と**切替可能**にする |
| ポスト基盤 | 画面全体だけでなく**テクスチャ単位・マスク**を扱いやすい合成土台（RTプール＋パス抽象）。まず土台のみでOK |
| バッチング | スプライトに限らず**同一形状の一括描画**の汎用機構 |
| マテリアル | **.matアセット＋オーバーライド**。マルチメッシュ/マルチマテリアルをスロット一覧で簡潔に編集 |

## フェーズ計画（実装しやすい依存順）

### Phase R1: ライトECS＋ライトバッファ 【状況: 完了（master ce551ee、rect=最近接点近似・LTCはR1.5 TODO）】

#### R1.5 アンビエント制御（2026-07 追加, 実機検証待ち）
- 環境光をハードコード定数 `vec3(0.05)*albedo*ao` から制御可能化。`LightMeta`（lighting.rs / shader_common.wgsl）に
  `ambient_color: vec3` ＋ `ambient_intensity: f32` を追加（16→32 バイト。offset: color=16, intensity=28。
  layout_tests で固定検証）。shade_pbr のアンビエント項を `ambient_color * ambient_intensity * albedo * ao` に置換。
  既定は白×0.05（従来と同一の見た目）。`ambient_intensity=0` で完全な暗闇。
- 経路: App.ambient_color/ambient_intensity → 毎フレーム `LightBuffer::update` で LightMeta へ。IPC は独立
  `SET_AMBIENT:{r},{g},{b},{intensity}`（PostFx はポスト用のため別 IPC が自然と判断）。起動時
  `load_graphics_settings` が project_settings.json の `ambient_color`(配列)/`ambient_intensity` を unwrap_or 既定で読む
  （コミット禁止）。エディタはビューポート設定ポップアップに「環境光」Expander（カラースウォッチ＋強度スライダ 0〜1）を追加。
  エディタは起動時に自動送信せず、ランタイム側の起動時読込値を尊重する（UI 変更時のみ送信）。
- 残 TODO: rect の LTC（本項とは独立）。IBL（環境マップ irradiance）は将来。

- LightComponent（ComponentKind::Light）: kind(directional/point/spot/rect), color, intensity,
  range, スポットの内外角, rectの幅高, cast_shadows フラグ。向き/位置はActorTransformから。
- シェーダ: ハードコード方向光を廃止し、ライト配列（storage buffer、上限は定数 MAX_LIGHTS）＋
  ライト数 uniform に置換。フォワードのper-fragmentループ（クラスタリングは将来課題として明記のみ）。
- rectライトのシェーディングは **LTC（Linearly Transformed Cosines）**＋組込LUTテクスチャ。
  R1で難航する場合はrectのデータモデルだけ先行し、LTCはR1.5として分離可。
- インスペクタ: ライト編集UI（種別・色・強度・角度等）。エディタ表示: ライトギズモ（アイコン＋範囲ワイヤ）。
- 受入: シーンに複数ライトを置き、色・向き・減衰が正しく効く。ライト0灯でも破綻しない（アンビエントのみ）。

### Phase R2: シャドウマップ 【状況: 実装済み（実機検証待ち）】
- directional: CSM（カスケード数は定数、まず3）。spot: 単一深度マップ。
- シャドウアトラス or ライトごとの深度テクスチャ配列。PCFフィルタ。
- cast_shadows/receive_shadows の制御（ライト側＋ModelComponent側）。
- 深度プリパス（死蔵資産）の接続をこのフェーズで検討（シャドウパスと同系の作業のため）。
- 受入: 方向光CSMで接地影、スポットで円錐影。カメラ移動でカスケード境界が破綻しない。

#### 実装メモ（2026-07, 実機検証待ち）
- リソース: `renderer/shadow.rs`（`ShadowResources`）。CSM=Depth32Float Texture2DArray（2048×3レイヤ,
  `CSM_CASCADE_COUNT=3`/`SHADOW_MAP_SIZE=2048`）、スポット=Depth32Float Texture2DArray
  （1024×4レイヤ, `SPOT_SHADOW_SIZE=1024`/`MAX_SHADOW_SPOTS=4`）。
- バインディング: **group4 にライトと同居**（binding 0=lights, 1=meta, 2=CSM深度配列,
  3=スポット深度配列, 4=比較サンプラー(LessEqual), 5=`ShadowMatricesUbo`）。
  max_bind_groups=5（group0〜4）のデバイスが実在するため group5 は新設しない
  （当初の group5 実装は実機で起動不能→group4 統合で修正済み）。複合BGは `LightBuffer::new` が
  起動時1回生成。pipeline_config.rs に「グループ数≦デバイスリミット」の起動時アサートを追加（再発防止）。
- シャドウパス: 死蔵の depth_prepass.wgsl を流用（`ShadowDepthPipelines`, `shadow_depth_*.toml`）。
  skin compute 後・メインパス直前に各カスケード/スポットレイヤへ深度専用描画。
  シャドウ用 view-proj はレイヤごとに専用 `CameraBuffer`（group0）へアップロード。
- CSM: practical split（`CSM_SPLIT_LAMBDA=0.5`）＋バウンディング球タイト正射＋テクセルスナップ。
- シェーディング: `shadow.wgsl`（group4 binding2〜5）で方向光=カスケード選択→PCF3x3、スポット=PCF3x3。
  slope-scaled 深度バイアス（`shadow_depth_*.toml` の `depth_bias_*`）＋シェーダ定数バイアス併用。
  影付きは「最初の cast_shadows=true な方向光1灯」＋スポット最大4。`GpuLight.shadow_index` で結線。
- cast_shadows: `ModelComponent.cast_shadows`（既定true, インスペクタ「影を落とす」チェック）。
  粒度は共有バッチ（source_path）単位（インスタンス単位除外は未対応）。
- TODO（R2残）: カスケード別カリング・境界スムーズブレンド・receive_shadows・point/rect影・
  Play正射/2Dビュー時のCSM・カスケード可視化デバッグ表示。

### Phase R3: HDR＋ポストプロセス土台 【状況: 実装済み（実機検証待ち）】
- オフスクリーンHDRターゲット（Rgba16Float）へシーン描画→フルスクリーントーンマップパスで
  スワップチェーンへ（各メッシュシェーダ内のReinhardを撤去し一元化）。
- **RTプール＋ポストパス抽象**: 名前付きレンダーターゲットの確保/再利用、入出力テクスチャ＋
  任意のマスクテクスチャを取るポストパス定義（TOMLパイプラインの流儀に合わせる）。
  「テクスチャ単位・マスクのかけやすさ」はこの抽象で担保（例: パスの入力に mask を宣言可能）。
- 受入: 見た目が現状と同等（トーンマップ位置が変わるのみ）＋ポストパスを1つ挿せるサンプル（例: ビネット）。

#### 実装メモ（2026-07, 実機検証待ち）
- 新設: `renderer/post/`（`rt_pool.rs`=RtPool, `post_pass.rs`=PostPipeline+run_post_stage,
  `mod.rs`=PostContext+Tonemap/Vignette params）。定数 `HDR_FORMAT=Rgba16Float` を
  `renderer/mod.rs` に追加。
- パス順（メインエンコーダ）: [カメラプレビューofスクリーン(HDR)] → シャドウ/RT加速構造 →
  **メインパス(begin_scene_pass_to, HDR)** → **キャンバスオーバーレイ(begin_canvas_overlay_pass_to, HDR)**
  → **トーンマップ(post_tonemap, HDR→スワップチェーン)** → カメラプレビューブリット(スワップチェーン,
  ブリットシェーダ内でHDRプレビューをトーンマップ) → IDパス(オフスクリーンRgba32Float, 従来通り)。
  シャドウ(R2)/RT影(R8)/IDピックは一切変更せず（HDR非経由）。
- トーンマップ一元化: `shader_fragment.wgsl` 末尾の輝度Reinhard（撤去前 214-224 行）を撤去し
  `return vec4(hdr_color, a)` に変更。演算子は `tonemap_ops.wgsl`（純関数, `tonemap_apply`）へ
  分離し、`post_tonemap.wgsl`（フルスクリーンパス）とカメラプレビューブリットが共用。演算子は
  uniform（`TonemapParams.op`）で切替可能な構造（現状 Reinhard のみ、ACES 等は R4+）。
- パイプラインのフォーマット分岐: `DrawPipelines::new`/`DrawContext::new` に `scene_format`(HDR) と
  `surface_format`(スワップチェーン) を分けて渡す。シーン描画（mesh/skinned/rt/unlit/gizmo/
  sprite/outline/bar_fill/軸ギズモ/アイコン）は HDR、トーンマップ後直描き（カメラプレビューブリット）
  のみ surface。IDパス(Rgba32Float)/深度・シャドウパス(カラーなし)はパイプライン内部固定のため不変。
  軸ギズモ/アイコンは `app_init.rs` で HDR_FORMAT を渡す。
- RTプール: `RtPool`（App フィールド, 毎フレーム ensure でサーフェスサイズ追従）。名前 `scene_hdr`
  （シーンHDR）/`post_inter`（ビネット等の中間HDR, トーンマップ前段出力）。R4 ブルームの
  ダウンサンプルチェーンも同プールから名前で確保する前提。
- ポストパス抽象: `run_post_stage`＝group0=params UBO / group1=入力tex+sampler / group2=マスクtex+sampler
  （マスク未指定時は白1x1を既定バインド→常時バインド可能）。パイプラインは既存 TOML 機構
  （`post_*.toml`＋WGSL, sampler/テクスチャはリフレクション任せ）。チェーンは `PostContext::run` が
  「ビネット(任意)→トーンマップ」を最小実装（前段出力→次段入力）。
- ビネットサンプル: `post_vignette.toml`/`post_vignette.wgsl`。既定 OFF。project_settings.json の
  `post_vignette`(bool) を `load_graphics_settings` で `App.post_vignette_enabled` へ（読み側 unwrap_or(false)。
  設定ファイルはコミットしない）。ON 時はトーンマップ前段に HDR 中間経由で挿入。
- naga parse+validate: 新規ポスト（`post/mod.rs` の `post_shaders_parse_and_validate`）＋
  既存 `rt_shadow.rs` の 4 バリアント test が撤去後の mesh/skinned フラグメントを再検証（両者 pass）。
  グループ数は全パイプライン ≤5 維持（ポストは group0〜2）。
- 既知の見た目差分（要実機確認）: 2D オーバーレイ（スプライト/ギズモ/線/帯）と背景クリア色も
  HDR 経由で一括トーンマップされるため、従来 sRGB 直描きで Reinhard 非適用だった LDR 要素が
  わずかに暗くなる（白系ほど顕著。luma に応じ 1.0→0.83 前後）。3D メッシュ（本フェーズの主眼）と
  カメラプレビューは Reinhard 位置が移動するのみで一致。厳密な UI 非トーンマップ化は「トーンマップ後に
  オーバーレイを直描き」する構成が正道で、R4 の合成整理時に検討（本フェーズは最小構成で一元化を優先）。
  → **R4 で解消**（下記）。
- TODO(R3残): ~~上記オーバーレイのトーンマップ回避（ポスト後合成）~~【R4で解消】・露出制御(exposure UI)・
  演算子切替 UI・ビネット強度のデータ駆動化。
  - **R4での解消内容**: シーンキャンバスオーバーレイパス（`begin_canvas_overlay_pass_to`, 深度クリアで
    前面化する既存挙動は維持）の描画先を HDR から **トーンマップ後の LDR 中間（`RT_LDR`）** へ移動。
    描画順を シーンHDR→ブルーム→トーンマップ(HDR→LDR)→2Dオーバーレイ(LDR)→FXAA/プレゼント(→スワップ
    チェーン) に再構成した。オーバーレイはトーンマップを通らなくなり暗化が解消。`RT_LDR` は物理
    フォーマットを Rgba16Float（HDRと同一）にしたため、オーバーレイ用パイプライン（sprite/line/gizmo/
    軸ギズモ, HDRフォーマットで構築済み）を一切変更せずそのまま描ける。sprite.wgsl 等に Reinhard は無く
    影響なし。カメラプレビューブリットは従来どおり最終段の後にスワップチェーンへ重ねる（整合確認済み）。
    ※メインパス冒頭の背景ゾーンスプライト（3Dワールドより奥）は3Dと深度整合するためHDR側のまま。

### Phase R4: ブルーム＋FXAA 【状況: 実装済み（実機検証待ち）】
- R3の土台上に。ブルーム（しきい値→ダウンサンプルチェーン→合成）、FXAA（最終段）。
- カメラ/プロジェクト設定でON/OFF・強度をデータドリブンに。

#### 実装メモ（2026-07, 実機検証待ち）
- 新設: `renderer/post/bloom.rs`（`BloomPipelines`=プレフィルタ/ダウン/アップの3パイプライン、
  `BloomParams`、`mip_plan`/`ensure_targets`/`record`）。定数 `MAX_BLOOM_MIPS=6`。
  WGSL: `post_bloom_prefilter.wgsl`（ソフトニーしきい値抽出）・`post_bloom_down.wgsl`（13-tap
  ダウンサンプル, CoD:AW方式）・`post_bloom_up.wgsl`（3x3テント＋加算合成）。TOML同名3本。
- ブルーム構成: シーンHDR→プレフィルタ(半解像度=`bloom_0`)→ダウンサンプルチェーン(各1/2, 段数は
  解像度から算出・下限8px/上限6段)→アップサンプル加算(小mip→大mipへテント拡大, `blend=Additive`
  ＋`LoadOp::Load`)→合成(`bloom_0`×intensityをシーンHDRへ加算)。中間RTは全てRtPoolから
  名前(`bloom_0`..`bloom_5`)で確保。合成式: `scene += tent(bloom_0) * intensity`。
- FXAA: `post_fxaa.wgsl`（Timothy Lottes簡易版=FXAA 3.x相当）。トーンマップ後LDRの最終段
  （スワップチェーン直前）に適用。`enabled=0`時は中央1タップのコピー（＝プレゼント兼用）で安価。
  輝度はリニアRGBから算出（厳密にはsRGB空間が理想だが標準実装として許容）。
- パイプライン: `pipeline_config.rs` に `blend="Additive"`（One/One加算）を追加。
  `post_pass.rs` に `run_post_stage_load`（`LoadOp::Load`で加算合成用）を追加。
- 設定: `PostFxSettings`（`renderer/post/mod.rs`）に bloom(enabled/threshold/knee/intensity)＋
  fxaa(enabled) を集約。App.post_fx フィールド。project_settings.json（`bloom`/`bloom_threshold`/
  `bloom_knee`/`bloom_intensity`/`fxaa`, 読み側unwrap_orでデフォルト, **コミット禁止**）を起動時
  `load_graphics_settings` で読み、IPC `SET_POST_FX:{json}`（`SetPostFx`）で実行中変更。
  エディタは MainWindow.xaml ビューポート設定ポップアップに「ポストプロセス」Expander
  （ChkBloom/SldBloomIntensity/ChkFxaa）を追加し `SET_POST_FX` を送る。デフォルト bloom=OFF/fxaa=OFF。
- naga parse+validate: `post/mod.rs` の `post_shaders_parse_and_validate` に新規4本を追加（全6本pass）。
- 両OFF時のコスト: ブルームはRT確保・パスとも完全スキップ（コスト増ゼロ）。FXAA無効時は最終段が
  中央1タップのコピーになる。R3比の増分は「トーンマップ→LDR中間→最終コピー」の追加フルスクリーン
  コピー1回＋LDR中間RT(Rgba16Float, 全解像度)1枚のみ（下記R3課題解消と引き換え。GPU負荷は微小）。

### Phase R5: 透明描画の整備 【状況: 実装済み（実機検証待ち）】
- 不透明/透明の描画分離（マテリアルのAlphaMode: Opaque/Mask/Blend で分類）。
- Maskモードのdiscard復活（shader_fragment.wgslのコメントアウト解除＋alpha_cutoff結線）。
- Blendは2方式を切替可能に: (a)距離ソート（後方→前方）、(b)WBOIT（accum/revealage 2RT＋合成パス。R3の土台使用）。
  切替はカメラ or プロジェクト設定。
- 受入: 半透明同士の交差でWBOITが破綻なく、ソート方式では従来型の見た目になる。

#### 実装メモ（2026-07, 実機検証待ち）
- 分類: **プリミティブ（サブメッシュ）単位**。`GpuMaterial` に `alpha_mode` を保持し、
  `GpuModel::primitive_alpha_mode(material_idx)` を唯一の判定源とする。Opaque/Mask は
  従来のメイン HDR パス、Blend は透明パスへ分離（`draw_model_indirect` が Blend をスキップ）。
- Mask discard 復活: `shader_fragment.wgsl` のコメントアウト解除。`alpha_cutoff` は
  `GpuMaterial::upload` が Mask のときのみ正値（他は 0.0）を UBO へ入れるため、
  **1 パイプライン共用**で Opaque/Blend は無影響。ライティング本体は `shade_pbr()` へ
  切り出し、fs_main（不透明/Mask）と fs_wboit（WBOIT）が共有する。
- 新設: `renderer/transparency.rs`（`TransparencyMode`{DistanceSort,Wboit}・
  `TransparentPipelines`・gather/draw_sorted/draw_wboit/has_transparent/composite_wboit）。
  WGSL: `shader_wboit.wgsl`（McGuire/Bavoil 深度依存重み, WBOIT_* 定数）・
  `post_wboit_composite.wgsl`。TOML: `transparent_mesh/skinned.toml`
  （blend=AlphaBlending, depth_write=false, LessEqual）・`post_wboit_composite.toml`。
- 距離ソート方式（既定）: 粒度は**プリミティブ×インスタンス**。Indirect 構造は使わず、
  インスタンス重心のカメラ距離二乗で背面→前面ソートし
  `draw_indexed(0..n, 0, compact_idx..compact_idx+1)`（非ゼロ first_instance、outline と
  同手法）で 1 件ずつ直接描画（透明は少数前提でドローコール増を許容する設計判断）。
  メインパス内・不透明直後に描画。BGL 構造同一性により既存の
  camera/model/material/joint/lights BindGroup をそのまま流用。
- WBOIT 方式: accum=Rgba16Float（One/One 加算, クリア0）＋ reveal=R16Float
  （(Zero, OneMinusSrc)＝dst*=(1-a), クリア1）の 2RT（RtPool `wboit_accum`/`wboit_reveal`）。
  デュアル MRT パイプラインは TOML ビルダー非対応のため手動構築（ソート用ビルドの
  リフレクション BGL を再利用、グループ数 5 維持）。深度はメインパス深度を Load・
  書込なしで不透明に隠される。合成はフルスクリーンパスで
  `vec4(accum.rgb/max(accum.a,ε), 1-reveal)` を **ブルーム前＝トーンマップ前**の
  シーン HDR へ AlphaBlending（LoadOp::Load）合成。reveal≈1 のピクセルは discard。
- 切替: `PostFxSettings.transparency`（既定 DistanceSort）。IPC は `SET_POST_FX` に
  `"transparency":"sort"|"wboit"` を追加（欠落時 sort）。project_settings.json の
  `transparency` を起動時 `load_graphics_settings` で読む（読み側 unwrap_or、コミット禁止）。
  エディタはビューポート設定ポップアップに「透明描画」Expander＋ドロップダウン
  （CmbTransparency: 距離ソート/WBOIT）を追加。実行時切替（両パイプライン起動時構築済み）。
- 透明なしシーンのコスト: `has_transparent`（可視 Blend プリミティブの有無を安価走査）が
  false のとき gather・透明パス・WBOIT RT 確保をすべてスキップ＝**追加コストほぼゼロ**
  （残るのは (GpuModel,Batch) ペア収集の O(モデル数) Vec と走査のみ）。全 Opaque シーンの
  見た目は完全不変（Blend が存在しないため不透明パスの skip 分岐が発火しない）。
- カメラプレビュー: プレビューパスにも透明ソート描画を追加（プレビューカメラ位置基準）。
  プレビューはグローバル設定にかかわらず**常に距離ソート**（WBOIT はプレビュー毎の
  RT/合成が必要でコスト不相応。R8 のプレビュー簡易経路と同方針）。
- naga parse+validate: `transparency.rs` の #[test] で WBOIT mesh/skinned・合成の 3 連結を
  検証（post/rt_shadow の既存テストが shade_pbr 分割後の fs_main も再検証、全 pass）。
- スコープ外（TODO）: スプライト 2D の透明（既存レイヤーソートのまま）・パーティクル・
  透明の RT 影/シャドウキャスト（透明は非 RT ライト BG＝シャドウマップ受光のみ。
  BLAS/シャドウキャスターからの Blend 除外は未実施——cast_shadows=false で回避可能）・
  カメラプレビューの WBOIT・実機での視覚検証（交差半透明の WBOIT 破綻なし確認）。

### Phase R6: 汎用バッチング（同一形状一括描画） 【状況: 実装済み（実機検証待ち）】
- スプライト: 1スプライト=1ドローコール＋毎フレームbuffer/BindGroup生成を撤廃。
  インスタンシング（クアッド1枚×インスタンスバッファ）＋テクスチャは配列 or アトラスで統合。
- 汎用化: 「同一メッシュ形状＋同一パイプライン」を自動でインスタンス束ねる軽量バッチャを
  プリミティブ描画（ライン/ギズモ以外の形状描画）にも適用できる形で設計。
  ※3Dモデルは既存 InstancedModelBatch が担うため対象外（重複実装しない）。
- 受入: スプライト1000枚でドローコールが数個に収まり、フレーム時間が現状比で大幅短縮。

#### 実装メモ（2026-07, 実機検証待ち）
- 新設: `renderer/batch2d.rs`（`SpriteBatcher`／`InstanceStream`／`SpriteInstance`(80B=model 4列+color)／
  `SpriteBatchList`＋`draw_sprite_batches`／`draw_sprite_outline_batches`）。定数
  `INITIAL_INSTANCE_CAPACITY=256`・`INSTANCE_GROWTH_FACTOR=2`・`SPRITE_INSTANCE_SIZE=80`。
- インスタンスデータ: クアッド1枚（`SpritePipeline.unit_quad_vbuf`, 6頂点, per-vertex slot0）を全スプライトで
  共有し、model行列（列優先 mat4x4=4×vec4, location2〜5）＋color（vec4, location6）を per-instance slot1
  （`step_mode=Instance`, `pipeline_config.rs` の `"sprite_instance"` レイアウト, stride80）で供給する。
  sprite.wgsl / sprite_outline.wgsl をインスタンス属性対応に改修（旧uniform方式=group1 SpriteUniformは撤去、
  テクスチャがgroup1へ繰り上がり）。sprite.toml=2グループ・sprite_outline.toml=1グループ（camera のみ）。
- 永続化＋書込: インスタンスバッファは永続化し毎フレーム `write_buffer`（容量不足時のみ倍々成長で再確保）。
  BindGroupの毎フレーム生成を撤廃（テクスチャBindGroupは従来どおり `sprite_tex_cache` でテクスチャ単位に永続キャッシュ）。
  `SpriteBatcher` は `DrawContext.sprites: RefCell<..>` が保持。バッファは `Arc<wgpu::Buffer>` で、記録前に
  ローカルへ clone して `'rp` に渡す（RefCell の Ref を跨がない）。
- バッチ区切り（ソート順維持）: 描画順は現行のレイヤーソート済み順を一切変更せず、`push` が
  「連続する同一テクスチャ（`Arc::ptr_eq`）」のみ1バッチへ融合し、テクスチャが切り替わる境界で区切る。
  各バッチは `inst_buf.slice(base..base+count)` ＋ `draw(0..6, 0..count)` の1ドロー。→ 見た目不変。
- 2チャンネル: `main`（メイン／キャンバスオーバーレイパスの2D背景/前面/3Dキャンバス/選択アウトライン）と
  `preview`（カメラプレビューパス）を分離。プレビューパスはメインのスプライト収集より前に記録されるため、
  単一バッファ共有だとメイン収集時の再確保が記録済みプレビューコマンドの参照を無効化する。分離で回避。
- テクスチャ配列/アトラスによる異テクスチャ統合は TODO（現行のテクスチャ管理を大改造しないため）。9-slice も TODO。
- テキスト描画（font/）・アイコンオーバーレイ（icon_overlay.rs）は同種の毎フレーム `create_buffer_init` を
  持つが、動的グリフ/アイコンジオメトリで頂点フォーマットも別（インスタンス化ではなく単一頂点バッチ）のため
  本バッチャに乗らず、工数大＝TODO（別タスク）。ライン/ギズモは既存の `LineBatch`/`GizmoBatch` が担当（対象外）。
- 計測: `[PERF]` 行に `sprites=<枚数>枚/<draws>draws` を追加（main チャンネルの総インスタンス数と
  バッチ数=ドローコール数）。理論削減: 同一テクスチャの連続N枚 → 1ドローコール（旧: N枚=Nドロー＋N個の
  uniform buffer/BindGroup生成）。スプライト0枚時は begin/upload が早期returnしRT確保も無く追加コストなし。
- naga parse+validate: `batch2d.rs` の #[test] で sprite.wgsl / sprite_outline.wgsl を parse+validate（pass）。
  `cargo build` 0エラー。旧経路（`sprite_drawer` の SpriteUniform／SpritePrepared／prepare_sprites(_from_mats)／
  draw_sprites／draw_sprite_outline）は削除（sprite_drawer はテクスチャロードのみに縮小）。
- 実機テスト観点: 2D/3Dキャンバス両方・SS合成/非SS・アクター編集タブ・カメラプレビュー・選択アウトラインで
  レイヤー順/ブレンド/UV/色が従来と一致すること。多数スプライトで `[PERF]` の draws が枚数より大幅に少ないこと。

### Phase R7: .matマテリアル＋マルチマテリアル編集 【状況: 実装済み（実機検証待ち）】
- .mat（JSON）: base_color/metallic/roughness/emissive/テクスチャパス群/alpha_mode/cutoff。
- ModelComponent: マテリアルスロット一覧（サブメッシュ→マテリアルの対応を表示）、
  スロットごとに「glTF埋込（既定）/.mat割当/インライン上書き」を選択可能に。
- インスペクタ: スロット一覧＋.matのD&D割当＋主要値のインライン編集。ProjectPanelで.mat新規作成。
- 受入: マルチメッシュ/マルチマテリアルのglTFで、特定スロットだけ色や粗さを差し替えられる。

#### 実装メモ（2026-07, 実機検証待ち）
- **方式: (a) の最軽量形＝「各 MC の GpuModel へオーバーライドを焼き込み＋マージキー分離」**。
  各 ModelComponent は自前の `gpu_model`（Arc 共有でなく MC 単位所有。CPU の Arc<Model> のみ
  model_cache 共有）を持ち、描画/透明/シャドウ/RT の全経路はマテリアルと alpha_mode を
  `GpuModel.materials` / `primitive_alpha_mode()` **からのみ**読む。よってオーバーライドを
  ビルド時に GpuModel へ焼き込むだけで **draw_model_indirect / transparency.rs / shadow.rs /
  rt_shadow.rs を一切変更せず**全経路へ反映される。per-アクタ整合は「マージキー＝
  `source_path + オーバーライド署名`」で担保（`ModelComponent::batch_key()`）。オーバーライド
  無しは署名空＝`batch_key == source_path` でビット一致し、旧シーン互換・描画経路・性能とも不変。
- 新設 `renderer/material_asset.rs`（`MaterialAsset`/`MatTextures`＝.mat JSON, 全 serde default,
  `OnceLock<Mutex<HashMap>>` キャッシュ＋`load`/`reload`/`clear_cache`, `parse_alpha_mode`）。
  .mat テクスチャは `TextureSource::FilePath` 経由でアップロード（`asset_fs::read_image` が
  assets:// 仮想パス／PAK を解決）。
- 新設 `components/material_override.rs`（`MaterialOverride{slot,kind}`, `MaterialOverrideKind`＝
  `MatAsset{path}`/`Inline{base_color/metallic/roughness/emissive/alpha_mode/alpha_cutoff の Option 群}`,
  `#[serde(tag="kind")]`, `overrides_signature()`＝空 Vec は空文字列）。
- `ModelComponent`＋`ModelComponentData` に `material_overrides: Vec<MaterialOverride>`
  （`#[serde(default)]`）を追加。`GpuModel::apply_overrides`（gpu_resources.rs）が Inline＝埋込
  Material を clone して factor/alpha を上書き（テクスチャ参照は維持）、MatAsset＝.mat の
  factor/alpha＋テクスチャを適用（新規テクスチャは self.textures へ push して GpuModel が所有）。
  `drawer::upload_model_with_overrides` が upload_model→apply_overrides を実行。gpu_model 構築の
  全箇所（scene ロード/slot_ops/component_ops/複製）を overrides 対応化。SET_MODEL_PATH で
  overrides をクリア（スロット意味が変わるため）。
- IPC: `SET_MATERIAL_OVERRIDE:{actor},{slot_idx},{mat_slot},{json}`（json は
  `{"kind":"embedded"}`＝埋込に戻す／`{"kind":"mat_asset","path":..}`／`{"kind":"inline",..}`）。
  受信で該当 MC の `material_overrides` を更新し gpu_model を再構築、ACTOR_COMPONENTS 再送。
  ACTOR_COMPONENTS の Model スロットに `materials`（各 slot の name/mode(embedded|mat|inline)/
  現在実効値/path）を追加（R8 の animations 追加と同流儀）。
- エディタ: InspectorPanel の Model セクションにマテリアルスロット一覧（.mat 割当 D&D＋
  カラーピッカー/スライダのインライン編集/埋込に戻す）。ProjectPanel で .mat 新規作成＆
  .mat ダブルクリックで外部エディタ起動。
- スコープ外（TODO）: テクスチャのインライン差替 UI（.mat 経由では可）・.mat ライブファイル
  ウォッチ・シェーダバリアント・material_index が None のプリミティブへの割当・RT 影 BLAS の
  override 群での重複（幾何は同一のため正しく描けるが batch_key ごとに BLAS が重複＝微小メモリ増）。

### Phase R8: インラインRT影（品質オプション） 【状況: 実装済み（実機検証待ち, v1ハードシャドウ）】
- EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE / EXPERIMENTAL_RAY_QUERY を要求する
  「RT対応デバイス」初期化経路を追加（非対応GPUは自動でシャドウマップへフォールバック）。
- BLAS（メッシュごと）/TLAS（フレームごと更新）の構築・更新管理が工数の本体。
- 影解決: ライティング時に rayQuery で遮蔽判定（シャドウマップの代替）。rect/pointの
  ソフトシャドウはRT側が得意（面光源サンプリング）。
- 実験的APIのため、wgpu更新で追従コストが発生しうる点を認識しておく。

#### 実装メモ（2026-07, 実機検証待ち）
- リソース: `renderer/rt_shadow.rs`（`RtShadowResources`）。BLASキャッシュ（source_path+
  mesh+prim 粒度, 非スキンのみ, 初回のみ構築）＋TLAS（`MAX_RT_INSTANCES=4096`, 毎フレーム
  cast_shadows=true の全インスタンス＝カメラカリング前から再構築）。RT対応フラグは
  グローバル `rt_shadow::set/rt_shadows_supported`（`renderer/mod.rs` で確定, 起動ログ
  `[SEED RT]`）。BLAS入力は既存頂点/インデックスバッファに `BLAS_INPUT` 用途を足す
  （対応GPUのみ, 位置は Vertex 先頭 offset0 の Float32x3・ストライド72）。
- 能力ベースの静的パイプライン選択＋実行時フラグ設計: RT対応時は常に RT バリアント
  パイプライン（`mesh_rt.toml`/`skinned_mesh_rt.toml`, group4 binding6 に
  `acceleration_structure` を追加。グループ数は5維持で R2 の起動時アサートに適合）を使い、
  設定 `rt_shadows` の オン/オフは `LightMeta.rt_shadows` フラグでフラグメントが実行時分岐
  （RT ↔ シャドウマップ）。→ 設定変更でパイプライン差し替え不要（features は起動時固定要求）。
- シェーダ: `shader_fragment.wgsl` のライトループが `rt_shadow_enabled()`/`rt_shadow_factor()`
  を呼ぶ。実体は連結される `rt_shadow_on.wgsl`（accel宣言＋rayQuery, 有効時は全ライト種で
  遮蔽レイ1本＝ハードシャドウ。tmax は directional=大定数/局所光=ライト距離、自己交差防止に
  法線オフセット＋tmin）と `rt_shadow_off.wgsl`（スタブ, 常にシャドウマップ経路）が供給。
  非対応GPUは `rt_shadow_off.wgsl` 側のみをロードし従来シェーダが完全に無変更で動作。
  naga parse+validate（RAY_QUERYケイパビリティ）を `rt_shadow.rs` の #[test] で全4バリアント検証。
- 反映経路: プロジェクト設定 `rt_shadows`(bool, 既定false)→起動時 `load_graphics_settings`
  で `App.rt_shadows` へ。エディタのチェックボックス→IPC `RT_SHADOWS:0/1`→`SetRtShadows`
  で実行中切替（`App.rt_shadows`）。TLAS再構築/RT用BindGroup bind は rt_on 時のみ＝RT無効時
  コスト増ゼロ。非対応GPUでONにしても `pipelines.rt=None` のためシャドウマップ継続。
- ソフトシャドウ（2026-07 追加, 実機検証待ち）: 面光源サンプリングで「遮蔽物から遠い影ほどボケる」
  物理挙動を実装。`LightComponent.soft_radius`（serde default 0.25。directional=角径(度)/局所光=ワールド半径。
  0 でハード。Inspector に「ソフト影半径」行、IPC `SET_LIGHT_FIELD ...,soft_radius`）を追加し、
  `GpuLight.soft_radius`（旧 _pad1 offset92 を再利用＝96B 不変）へ搬送。directional は collect_gpu_lights で
  度→tan 変換、局所光は raw 半径。`rt_shadow_on.wgsl` は cone_radius（directional=soft_radius, 局所光=
  soft_radius/距離）から l 中心の円錐内へレイを Vogel ディスク分布＋フラグメント座標
  由来の IGN 回転（時間項なし＝TAA非前提でちらつき無し）で分散し平均。cone_radius=0 は 1 本ハードへ分岐で高速維持。
  シグネチャは `rt_shadow_off.wgsl` スタブと一致。naga RAY_QUERY テストで全バリアント再検証済み。
- ソフト影のディザノイズ修正（2026-07, 実機検証待ち）: ライト近傍の面が点描状のまだらになり暗く潰れる不具合。
  原因は 局所光の cone_radius = soft_radius/距離 に上限が無く、近づくほど円錐が発散 →(a) 固定4本では遮蔽率が
  5段階に量子化されて IGN 回転と相まってディザ化 (b) 面の幾何的地平線より下を向いたサンプルが自己ジオメトリに
  当たり偽遮蔽。対処: ①`lighting_eval.wgsl` の `RT_SHADOW_MAX_CONE_RADIUS=0.5`(tan半角≈26.6°)でクランプ
  ②地平線より下（dot(Ng,dir) <= `RT_SHADOW_HORIZON_MIN_COS`=0.01）のサンプルを平均の**母数から除外**（有効0本なら完全遮蔽）
  ③サンプル数を cone_radius に応じ適応化（`RT_SHADOW_SAMPLES_MIN=4` 〜 `RT_SHADOW_SAMPLES_MAX=16`、
  傾き `RT_SHADOW_CONE_RADIUS_PER_SAMPLE=0.03125`=上限cone/最大本数）。ループ上限は定数で静的に固定。
  負荷: soft_radius>0 のライト 1 灯あたり最悪 16 本／ピクセル（cone_radius=0 なら従来どおり 1 本＝増加ゼロ）。
  定数整合は `rt_shadow.rs::wgsl_soft_shadow_constants_are_consistent` が担保。
  残課題: 16本でも遮蔽率は17段階の量子化が残るため、空間デノイズ（いもす法ベースの可変半径ブラー）or
  時間的蓄積 or ブルーノイズ（IGN からの差し替え）の追加を検討。
- TODO（R8残）: スキンメッシュのRT影（スキン済み頂点からのBLAS毎フレーム再構築）・カメラプレビュー/
  ギズモモデルのRT影（現状は従来パイプライン固定で影を受けない）・ソフト影サンプル数の SET_POST_FX 可変化・実機での視覚検証。

### ブラー系実装の方針（ユーザー指定・必読）
今後ガウシアンフィルタ相当の処理（ブラー・被写界深度・大半径ブルーム等）を実装する際は、
**いもす法（累積和）の応用**（https://imoz.jp/algorithms/imos_method.html）を優先検討すること。
累積和→ボックスフィルタは半径非依存の O(n) で、ボックスフィルタ3回反復でガウシアンを高精度に
近似できる（分離可能: 水平→垂直）。GPU実装は行/列prefix sum（compute）＋等幅区間和。
大カーネルほどタップ数固定の従来法より高速。

**基盤の実体（Phase D4 で作成・再利用可）**: `renderer/imos_blur.rs` ＋ `shaders/imos_blur.wgsl`。
単一チャンネル（`.r`）分離ボックスブラーの compute パイプライン＋ping-pong 実行ヘルパー（`ImosBlur::record`、
結果は必ず `t1` に残る不変条件つき）。アルゴリズムは `postfx_blur.wgsl` と同一式。ストレージは
`Rgba16Float`（単一チャンネル R16Float は core WebGPU の storage 非対応のため。`.r` のみ使用）。
**AO（SSAO / RT-AO）で初適用**。**SSGI（カラーブラー）へ転用済み**：`imos_blur.wgsl` の load/store を `.r`→`.rgb` に広げた（ランニング和はチャンネル独立のため AO の `.r` 運用は不変）
（フォーマット変更不要）。CPU 参照一致・WGSL naga 検証のユニットテストつき。

**例外（エッジ保持が必須な用途はバイラテラル）**: 影マスク（`shadow_mask`）だけは `imos_blur` を使わず、
専用の separable バイラテラルブラー（`shadow_mask_bilateral.rs`＋`shaders/shadow_mask_bilateral.wgsl`）を使う。
いもす法（累積和）は走査中に窓和を保持する構造上バイラテラル化できず、深度エッジを跨いで影値を混ぜて
ハロー（フチの薄い帯）を生むため。**いもす法は AO/SSGI/すりガラス等の低周波用途（エッジ滲みが問題化しない）**、
**バイラテラルは影マスクのようにエッジ保持が必須な用途**、と使い分ける。

### Phase RP: GPUパーティクル 【状況: 拡張実装済み（実機検証待ち）】
GPU 上でパーティクルをシミュレート（compute）して形状メッシュ×インスタンスで描画する。
ECS の `ParticleEmitterComponent`（データのみ）を入力に、エミッタごとの GPU バッファを
確保してシミュレーション→HDR（トーンマップ前）へ加算/アルファ合成する。

#### 拡張実装（2026-07 第2ウェーブ, 実機検証待ち）
- **形状（shape）**: Point（ビルボード・既定）/ Sphere（icosphere 12頂点）/ Box / Plane（両面クアッド）/
  Model{path}（既存ローダの先頭プリミティブ位置のみ流用。`MAX_PARTICLE_MODEL_VERTS`=2048 超・ロード失敗は
  警告して Point フォールバック）。組込みメッシュは `renderer/particle_shapes.rs`（`ShapeMeshCache`、
  全エミッタ共有・Model はパスごと遅延キャッシュ）。描画は頂点バッファ（位置 vec3 のみ）＋
  `draw_indexed`×max インスタンス。billboard は面内回転、mesh は seed 由来ランダム軸の軸角回転（Rodrigues）。
- **出現範囲（spawn_volume）**: Point / Box{half_extents} / Sphere{radius}。compute 内で体積内一様サンプル
  （Box=各軸一様、Sphere=方向球面一様×半径 u^(1/3)）。
- **emit 制御**: `emit_mode`（Loop/Once/Count{total}・旧 loop_emit を置換）＋ `initial_delay` ＋
  `prewarm_time`（起動フレームに固定 1/60 の K ステップ一括 compute。`MAX_PREWARM_STEPS`=600 でクランプ。
  ステップごとの一時 uniform＋BG を作って順次 dispatch する＝write_buffer 多重書きは不可のため）＋
  `emit_interval`×`particles_per_emit`（旧 emit_rate を置換。互換変換 interval=1/rate, per_emit=1）＋
  `direction_randomness`（0..1。half_angle_deg = randomness×180。旧 spread_angle_deg/180 から互換変換）。
- **Vector4 カーブ**: `ParamCurve{channels:Vec<CurveChannel{keys:[{t,v}]}}`（線形補間・serde が C# カーブ
  エディタとの共有契約）。speed_curve(x)/rot_speed_curve(x)/color_curve(HSVA)/scale_curve(xyz)/
  random_color_curves(HSVA リスト。非空なら粒子ごとハッシュで 1 本選択)。GPU 化は各カーブを
  `CURVE_LUT_SAMPLES`=64 行の vec4 に CPU で焼き、1 本の storage buffer に連結
  （**行レイアウト: [speed | rot_speed | color | scale | random_color_0..N-1]**、オフセットはシェーダが
  lut_samples から計算）。HSVA のまま格納しシェーダで hsv→rgb（色相の正しい補間）。再焼きは
  コンポーネントの `curve_generation`（SET_PARTICLE_CURVE で bump）変化時のみ。
- **ランダム範囲**: rot_speed_range（度/秒→GPU では rad/s）・size_range（全体倍率）・initial_speed・
  lifetime。粒子ごと seed ハッシュ抽選（決定的）。
- **速度モデル**: 射出速度 = emit_dir×base_speed×speed_curve(t)（毎フレーム再評価）＋ 蓄積速度 vel
  （重力/drag のみ積分）。pos += (射出＋蓄積)×dt。
- **IPC**: スカラー/enum は `SET_PARTICLE_FIELD`（新キー: shape/shape_model_path/spawn_volume/spawn_box_x..z/
  spawn_sphere_radius/emit_mode/emit_count_total/initial_delay/prewarm_time/emit_interval/particles_per_emit/
  direction_randomness/rot_speed_min/max/size_min/max 等）。カーブは新 IPC
  `SET_PARTICLE_CURVE:{actor},{slot},{curve_id},{json}`（curve_id ∈ speed|rot_speed|color|scale|random_colors、
  json は ParamCurve の serde 形。random_colors は配列）→ 差し替え→LUT 再焼き→ACTOR_COMPONENTS 再送。
  ACTOR_COMPONENTS には全スカラー＋カーブ JSON を含める。
- **後方互換**: 旧 .scene（emit_rate/burst/spread_angle_deg/start_size/end_size_scale/start_color/end_color/
  loop_emit）は `ParticleEmitterComponentRaw` 経由で新スキーマへ変換して読める（テスト `legacy_scene_converts`）。
  旧 start/end_color は RGB→HSVA の 2 キーカーブへ、start_size は size_range へ、end_size_scale は
  scale_curve(1→es) へ。スクリプト API（EmitRate/SpreadAngle/LoopEmit）は host_api の互換レイヤで名前を維持。
- **C# インスペクタ**: 新スカラー/enum の UI は実装済み。カーブ編集 UI（カーブエディタ）は次ウェーブ
  （現状プレースホルダ表示）。

#### 改善ウェーブ（2026-07 第3ウェーブ, 実機検証待ち）— 12 件
- **形状 Point→Pixel 改名＋軽量化**: 旧 Point（カメラ向きビルボード）を廃し、`Pixel`＝**PointList トポロジの
  1 頂点/インスタンス＝1 ピクセル描画**（ポリゴン非生成の最軽量）へ。CS 直書き（HDR ストレージ＋手動深度）案は
  6 ブレンドのアルファ正しさ・深度統合を壊すため不採用、**PointList 案を採用**（既存 render pass・深度・全ブレンドに
  そのまま乗る）。既定形状は Pixel。旧 "point" は serde alias で Pixel。Model ロード失敗/頂点超過は **Pixel
  フォールバック**（billboard モードは撤廃）。**注意（挙動変更）**: カメラ向き textured billboard は無くなった
  （テクスチャは Plane/Sphere 等メッシュ形状で使う）。
- **カーブキー補間タイプ**: `CurveKey.interp`（Linear 既定 / Smooth=Catmull-Rom / Step。serde default）。LUT 焼きは
  `CurveChannel::eval` がキー単位で補間するため 3 種とも自動で焼き込まれる（GPU 側変更不要）。C# はキー選択時に切替 UI＋
  プレビュー反映。
- **初期回転**: `initial_rotation_range:[f32;2]`（度, 既定 [0,0]）。sim シェーダがスポーン時に `rot_angle` を抽選
  （billboard=面内・mesh=軸角の初期角）。GPU 度→rad は CPU 変換。
- **色カーブの一本化**: `color_curve`＋`random_color_curves` → `color_curves:Vec<ParamCurve>`（HSVA・**最低 1 本**）。
  粒子ごとに seed で 1 本選択。C# は最後の 1 本を削除不可、Rust は空なら白フェード補完。IPC は curve_id=`colors`
  （リスト全体 JSON。旧 `color`/`random_colors` も互換受理）。
- **テクスチャリスト**: `texture_path`→`texture_paths:Vec<String>`（最大 `MAX_PARTICLE_TEXTURES`=8）。GPU は
  **texture_2d_array** に載せ、サイズ不一致は**先頭サイズへ CPU リサイズ**（Triangle）、粒子ごとに seed でレイヤ選択。
  空は既定白（1 レイヤ配列）。IPC は SET_PARTICLE_FIELD key=`texture_paths`（JSON 配列）。
- **ブレンド拡充（6 種）**: None(不透明)/Normal(over)/Add/Sub(reverse-subtract)/Mul(Dst×Src)/Screen(One+OneMinusSrcColor)。
  旧 Additive/Alpha は alias。パイプラインは **ブレンド 6 × トポロジ 2（mesh=TriangleList / pixel=PointList）＝12 本**を
  `ParticleBlend::to_code()` 索引で構築。ブレンドステート表:
  | code | 名称   | color/alpha src | dst              | op              |
  |------|--------|-----------------|------------------|-----------------|
  | 0    | None   | One             | Zero             | Add             |
  | 1    | Normal | One             | OneMinusSrcAlpha | Add             |
  | 2    | Add    | One             | One              | Add             |
  | 3    | Sub    | One             | One              | ReverseSubtract |
  | 4    | Mul    | Dst             | Zero             | Add             |
  | 5    | Screen | One             | OneMinusSrc      | Add             |
- **LUT 行レイアウト変更**: `[speed | rot_speed | scale | color_0..M-1]`（旧 `[speed|rot_speed|color|scale|
  random_color_0..]`）。固定 3 本＋色カーブ M 本。scale=行 2S、color_j=行 (3+j)S。
- **出現範囲ギズモ**: `particle_scene_gizmo.rs` に選択時の spawn_volume デバッグ描画を追加（Point=小十字 / Box=ワイヤ箱 /
  Sphere=軸別 3 円。放出円錐と併描画、light ギズモの流儀）。
- **Inspector 条件付き表示の徹底**: 形状 Model 以外→モデル参照非表示、spawn_volume 種別で寸法のみ、emit_mode=Count
  以外→total 非表示、Pixel→サイズ/スケール/回転系非表示（Visibility.Collapsed）。横並びレイアウト・ドラッグ値の
  フィールド別 clamp・カーブエディタ操作での Expander 誤開閉修正（e.Handled）も対応。
- **ライト/エミッタのアイコン表示＋IDピック**: camera ギズモ方式（GLB＋InstancedModelBatch＋draw_id_pass＋ID空間
  割り当て）を複製し、ワールド位置にアイコン表示＋クリックで該当アクター選択（`id_pass` に載せる）。
- **GpuEmitterParams は 208B**（192B から拡張）: 末尾に `color_count / initial_rot_min / initial_rot_max /
  tex_layer_count` を追加（`random_color_count` は `color_count` へ改名）。`layout_tests` を 208B・新オフセットへ更新。

#### 実装メモ（2026-07 第1ウェーブ、以下は拡張後の値に更新済み）
- ソース: `renderer/particle_system.rs`（CPU/GPU 状態・収集・LUT 焼き・描画）、`renderer/particle_shapes.rs`
  （組込み形状メッシュ＋Model キャッシュ）、`renderer/shaders/particle_sim.wgsl`
  （compute）、`renderer/shaders/particle_draw.wgsl`（描画）、`renderer/pipeline.rs`
  （`ParticleComputePipeline` / `ParticlePipelines`, DrawPipelines へ登録）、
  `app_base/app/particle_scene_gizmo.rs`（選択時の放出円錐ワイヤ）。
- バッファレイアウト: `GpuParticle`（std430 storage, **stride 64**, `PARTICLE_STRIDE`）=
  pos(vec3)+age / vel(vec3)+lifetime / emit_dir(vec3)+base_speed / seed+rot_angle+pad。
  `GpuEmitterParams`（uniform, **192B**）= world_mat(mat4, 列優先=転置) / dt / emit_count / ring_start /
  max / frame_nonce / drag / spread_rad / shape_mode / direction_local(vec3)+speed_min / speed_max /
  lifetime_min/max / rot_speed_min / gravity(vec3)+rot_speed_max / spawn_box(vec3)+spawn_sphere_radius /
  size_min/max / spawn_volume / sim_space / use_texture / lut_samples / random_color_count。
  vec3 の直後にスカラーを詰めて std140 に一致（`layout_tests` でサイズ・オフセットを固定）。
  生成時ゼロ初期化＝全 dead（age>=lifetime か lifetime<=0）。
- スポーン方式（リングカーソル・atomic なし）: CPU が `spawn_cursor`(=ring_start) と `emit_count` を
  uniform で渡し、compute のスレッド i がリング区間 [ring_start, ring_start+emit_count)（mod max）に
  入っていれば無条件で再スポーン（過剰放出時は生存粒子を上書き＝標準リング挙動）。乱数は wanghash/PCG 系
  ハッシュ（seed=hash(i ^ hash(frame_nonce))）で寿命/初速/円錐方向を決定的に生成し、seed を保存して頂点
  シェーダがサイズ乱数を再現する。円錐は spread 半頂角内の一様サンプリング（cosθ を [cos(spread),1] で一様）。
- 空間シム: World=スポーン位置は行列の平行移動・方向は行列で回した円錐（放出後ワールド固定）。
  Local=原点発生・ローカルでシムし描画時に行列変換（エミッタ追従）。
- パス位置: メインパス drop 後・WBOIT 合成後・**ブルーム前**の HDR（トーンマップ前）へ描画
  （`frame_renderer.rs` の WBOIT 合成直後）。compute dispatch は skin compute と同時期の専用 compute pass。
  CPU の放出決定・`pending_burst` 消費（スクリプト Burst 要求）は World への &mut が要るため描画ブロック前
  （`collect_and_consume`）で実施。ヘルパ `RenderFrame::begin_particle_pass_to`（color=hdr Load・
  深度 Load でテストのみ・書込なし）を使う。
- group 構成（≦5）: group0=camera（既存 CameraBuffer BG を流用＝同一 camera_bgl）、group1=particles(storage
  read)+params(uniform)+lut(storage read)、group2=texture+sampler（未指定は既定白 1x1＋シェーダの
  プロシージャル円。メッシュ形状は UV 中心固定＝フルアルファ）。compute の group0 も同様に particles+params+lut。
  描画は premultiplied alpha を出力し Additive=One/One・Alpha=One/OneMinusSrcA。深度=LessEqual・書込 OFF。
- Edit 常時プレビュー: dt は Play=可変（ctx.delta_time）/ Edit=固定 1/60（time_running 非依存・物理の先例に倣う）。
  playing=false は放出のみ停止し、既存粒子は自然消滅するまで更新を回す（常時 dispatch）。
- 既定値: max_particles=1024（上限 `MAX_PARTICLES_PER_EMITTER`=65536）、emit_interval=0.05×per_emit=1、
  lifetime[1,2]、initial_speed[1,3]、direction_randomness=0、direction_local=+Y、gravity=[0,-9.8,0]、
  size_range[1,1]、color_curve=白 A:1→0、scale_curve=1→0（コンポーネント側 default）。
- 追加コストゼロ: エミッタ 0 個のフレームは collect で frame が空になり、sync_gpu/dispatch/draw・パス生成・
  バッファ確保がすべて即 return（早期リターンで担保）。
- 検証: `particle_system.rs` の `layout_tests`（GpuParticle/GpuEmitterParams のサイズ・オフセット固定）と
  `shader_tests`（particle_sim/particle_draw の naga parse+validate）。※本 crate は build.rs が
  `/EXPORT:NvOptimusEnablement`（main.rs 定義）を全ターゲットへ付けるため lib テストの**リンク**が通らない
  既知の制約がある（本実装外）。レイアウトは standalone rustc、WGSL は naga 25.0.1 で個別検証済み。
- TODO: Alpha ブレンドのエミッタ単位粗ソート（現状は登録順）・indirect draw count（生存数に応じた
  可変インスタンス数で無駄頂点削減）・ソフトパーティクル（深度フェード）・スキン/2D 対応・全粒子 dead 検出で
  dispatch 打ち切り・エミッタの親ヒエラルキー追従（現状は Actor 自身の Transform のみ、ライトギズモと同慣例）。

### Phase R9: スカイボックス（天球） 【状況: 実装済み（実機検証待ち）】
equirectangular（正距円筒）画像 1 枚を天球として描画する。ECS の `SkyboxComponent`
（データのみ）を入力に、内向き UV 球メッシュへ方向ベースでテクスチャをサンプルする。

- **コンポーネント**: `SkyboxComponent`（`ComponentKind::Skybox`, 3D アクター用スロット）。
  `texture_path`(assets://・equirect 1 枚) / `mode`(CameraLocked 既定 / WorldAnchored) /
  `intensity`(既定 1) / `tint`([f32;3] 既定 白)。全 serde default で旧シーン互換。
- **配置モード**:
  - CameraLocked: カメラ位置中心・無限遠。頂点 VS で球をカメラ位置へ平行移動し `clip.z=clip.w`
    で NDC 深度を far(1.0) に固定、depth 書込 OFF・LessEqual。標準スカイボックス。
  - WorldAnchored: アクター Transform（位置・回転・スケール）で配置される内向き球。通常深度
    （depth 書込 ON）で実体化し、接近／内外移動が可能。
  - 複数時: CameraLocked は最初の 1 つのみ有効（以降は警告して無視, `skybox_system` の collect）。
    WorldAnchored は複数可。テクスチャ未設定（空）は描画しない。
- **描画統合**: 新設 `renderer/skybox.rs`（`SkyboxSystem`=collect/sync_gpu/draw＋`SkyboxPipelines`＋
  内向き UV 球生成 `generate_uv_sphere` 24×48）＋`shaders/skybox.wgsl`＋`pipelines/skybox.toml`。
  パイプラインは TOML から `RenderPipelineBuilder` で構築し、depth_write だけ異なる 2 バリアント
  （builder に `with_depth_write` 上書きを追加）を同一 TOML から生成。頂点は位置のみの新スロット
  `"pos3"`（pipeline_config.rs）。group0=camera（共有 CameraBuffer BG を流用・reflection の BGL 重複
  排除で互換）, group1=skybox uniform+tex+sampler（≦5 グループ）。描画位置は **HDR メインパスの最初**
  （`begin_scene_pass_to` 直後・Play ビューポート/シザー適用後・不透明より先）。unlit（intensity×tint×
  テクスチャ）で HDR へ出すため intensity>1 は Bloom と連動。
- **equirect サンプリング**: フラグメントで方向 `d=normalize(local_pos)` から
  `u=atan2(d.z,d.x)/2π+0.5`, `v=acos(clamp(d.y,-1,1))/π`。`textureSampleLevel`(level 0) で
  継ぎ目 derivative を回避。サンプラーは U=Repeat / V=ClampToEdge。テクスチャは `asset_fs::read_image`
  （8bit sRGB）で `SkyboxSystem` がパス単位キャッシュ。
- **エディタ**: ComponentSelector「レンダリング」カテゴリに追加。Inspector にテクスチャ参照(D&D)/
  mode ドロップダウン/intensity/tint。`SET_SKYBOX_FIELD:{actor},{slot},{key},{value}`
  （key=texture_path/mode/intensity/tint, tint="r,g,b"）で SET_LIGHT_FIELD 流儀。WorldAnchored 選択時は
  配置ワイヤ球ギズモ（`skybox_scene_gizmo.rs`, light_scene_gizmo 流儀）。
- **検証**: `skybox.rs` の layout_tests（SkyboxUniform=96B・UV 球本数）＋shader_tests（naga parse+validate）。
  cargo build/test・dotnet build いずれも 0 エラー。
- **TODO**: キューブマップ 6 枚対応・真の HDR(.hdr float)ロード（現状 8bit×intensity）・
  CameraLocked のアクター回転による天球オリエンテーション（現状は無回転）・IBL 環境照明への流用。

### Phase R10: .postfx テクスチャ単位ポストプロセス 【状況: 実装済み（実機検証待ち, v1）】
Phase R3 のポスト土台（RtPool / post_pass 抽象 / マスク）を応用し、個々のテクスチャ
（まずスプライト）へエフェクトチェーン（.postfx アセット）を焼き込む。「画面全体」ではなく
「テクスチャ単位・マスク」を扱う土台の実利用例。

#### .postfx アセット（JSON, material_asset.rs 流儀）
- 新設 `renderer/postfx/`（`asset.rs`=.postfx スキーマ＋ロード＋`OnceLock<Mutex<HashMap>>` キャッシュ
  ＋`load`/`reload`/`clear_cache`／`bake.rs`=焼き込み＋キャッシュ／`mod.rs`=`PostfxContext`＋params）。
- スキーマ: `{"every_frame":bool, "effects":[{"type":..,..}]}`。全フィールド serde default 相当
  （effects は `Vec<serde_json::Value>` で受け、`type` を手動ディスパッチ＝**未知 type は警告スキップ**）。
  v1 エフェクト:
  - **blur**（`radius`）: **いもす法（走査線ランニング和）ボックス 3 回近似ガウシアン**。
    `postfx_blur.wgsl`（compute, workgroup 64）が 1 起動＝1 走査線を担当し、幅 (2r+1) の窓和を
    「先頭を足し末尾を引く」ランニング和で更新（半径非依存 O(1)/画素）。分離可能性で水平（行走査）
    →垂直（列走査）に分解し、H/V を 3 往復＝6 サブパス（temp 2 枚をピンポン）で box×3=ガウス近似。
  - **vignette**（`strength`, `mask`）: 既存 `post_vignette.wgsl`／`VignetteParams` を作業フォーマットで
    再構築して流用（strength→intensity, 形状は既定）。`mask` はテクスチャパス（group2, 未指定=白全面）。
  - **tint**（`color`）: `postfx_tint.wgsl`（乗算色）。color=白で恒等コピー＝チェーン先頭の ingest 取り込みにも流用。
- チェーン実行（`bake.rs::bake`）: ベース sRGB テクスチャ→ingest(tint 白)でリニア HDR(Rgba16Float)作業
  バッファへ→各エフェクトを作業 2 枚のピンポンで適用→最終枚を `GpuSpriteTexture` に包む。全工程を
  **リニア HDR（Rgba16Float）作業空間**で統一（sRGB ベースをサンプルした自動デコード値と、焼き上げ
  Rgba16Float をサンプルした値が一致＝スプライト描画は元テクスチャと同一挙動。blur の rgba16float
  storage とも整合）。作業テクスチャは RENDER_ATTACHMENT|TEXTURE_BINDING|STORAGE_BINDING の 3 用途兼用。

#### スプライト統合（描画コード無変更＝テクスチャキャッシュ層で差し替え）
- `SpriteComponent`＋`SpriteComponentData` に `postfx_path: String`（空=無効, `#[serde(default)]`,
  ComponentKind 変更なし・フィールド追加のみ・旧 .scene 互換）を追加。
- `collect_sprite_items`（canvas_collect.rs）でベーステクスチャ解決の直後、postfx_path 非空なら
  `postfx::resolve_baked` で焼き込み済みテクスチャへ差し替える（batch2d 描画経路は一切無変更）。
- **キャッシュ**（`SpritePostfxCache`, DrawContext 保持）: キー=(texture_path, postfx_path)、値に .postfx の
  **mtime**（`asset_fs::mtime` 追加）を保持。元テクスチャ・.postfx 不変なら 1 回焼いて使い回す。.postfx が
  mtime 変化したらアセット reload して焼き直し。`every_frame=true` は毎回焼き直す。焼き込みは
  **フレームのメインエンコーダと独立した専用エンコーダを生成即 submit**するため、collect から `&DrawContext`
  だけで完結し frame_renderer への割り込み不要（＝R3 土台の上に非侵襲で載る）。
- IPC: `SET_SPRITE_POSTFX:{actor},{slot},{path}`（SET_SPRITE_PATH と対称。空でクリア）→
  `handle_set_sprite_postfx` が SpriteComponent 更新＋該当キャッシュ invalidate＋ACTOR_COMPONENTS 再送。
  ACTOR_COMPONENTS の SpriteComponent に `postfx_path` を追加。
- エディタ: Inspector のスプライトに「ポストエフェクト」参照欄（D&D/ダイアログ, .postfx）。ProjectPanel
  右クリック「新規ポストエフェクト」で雛形 .postfx 生成。
- **検証**: `postfx/mod.rs` の `postfx_shaders_parse_and_validate`（tint/vignette/blur の naga parse+validate,
  RAY_QUERY 不要 = Capabilities::empty）pass。cargo build 0 エラー。
- **実機観点**: 焼き込みは初回のみ（キャッシュ）＝描画コスト増は微小。blur は大半径でも走査線ランニング和で
  高速。postfx 無しスプライトは差し替え分岐が発火せず完全に従来経路（コスト増ゼロ）。
- **スコープ外（TODO）**:
  - **.mat のテクスチャへの .postfx 適用**（次段。material_asset の各テクスチャ参照へ postfx 焼き込みを掛ける）。
  - **③ カメラ RTT（Render To Texture）**: カメラの描画結果をオフスクリーンテクスチャへ焼き、
    スプライト/マテリアルのテクスチャとして参照可能にする（本 postfx チェーンの入力源に流用できる想定）。
  - **④ カメラ紐づけの画面全体ポスト**: カメラ（or プロジェクト設定）に .postfx を紐づけ、画面全体へ
    チェーン適用する経路（R3/R4 の全画面ポスト段に .postfx チェーンを差し込む形が自然）。
  - every_frame の焼き込み先 RT 再利用（現状は毎フレーム新規テクスチャ確保＝GPU メモリ churn。opt-in 前提）・
    追加エフェクト種（色収差・グロー・ディゾルブ等）・エフェクト単位マスクの全種対応（現状 vignette のみ）。

### 継続タスク（全フェーズ共通）
- frame_renderer.rs の該当パスを触るたびにモジュール分割（passes/ サブフォルダへ）。
- 各フェーズでデバッグ表示を拡充（R1: ライトギズモ、R2: カスケード可視化、R5: OITバッファ可視化等）。
- Hi-Zオクルージョンの接続は性能課題が顕在化した時点で独立タスクとして実施（実装済み・接続のみ）。

## 実装順の根拠
R1→R2 は依存関係（影はライトの上に）。R3→R4/R5 も依存（ポスト土台の上にブルーム/WBOIT合成）。
R6/R7 は独立しており、R2とR3の間など任意の位置に差し込み可能（疲労分散・検証待ちの間に実施推奨）。
R8 は影アーキテクチャ確定後かつ実験的API理解が必要なため最後。

## 付録: JointAttachComponent（ソケット機構）

レンダリング直系のフェーズではないが、モデルアニメ（スキニング）評価結果を利用して
アクターをボーンへ追従させる機能のため、ここに設計を記録する。

- **目的**: 剣を手のボーンへ持たせる・エフェクトを頭に固定する等の「ソケット」。
- **コンポーネント**: `ComponentKind::JointAttach`（スロット）。フィールド `joint_name`（空=無効）/
  `offset_pos` / `offset_rot_deg`（YXZ度）/ `offset_scale`（既定[1,1,1]）。全 `#[serde(default)]` で旧シーン互換。
- **ターゲットモデル**: 本コンポーネントを持つアクターから**親（祖先）を上方向へ辿り、最初に Model
  スロットを持つアクター**。`jointattach_ops::collect_attach_jobs` が祖先スタックで解決する。
- **ジョイント解決（CPU）**: `renderer/animator.rs::compute_node_world_matrices(model, anim_idx, time)`
  が `sample_joint_matrices` の①〜③（TRS補間→ローカル行列→シーングラフ走査）を切り出した純関数。
  ノードのワールド行列（モデル空間）を返す（インバースバインドは掛けない＝ノード姿勢そのもの）。
  `anim_idx` 無効（`usize::MAX`）でバインドポーズ＝静止 t0 相当。
- **時刻源**: `ModelComponent.anim_drive`（Play 中の Animator 権威時刻）。無ければ静止（バインドポーズ）。
- **キャッシュ**: `jointattach_ops::update_joint_attachments` が**モデルごと・フレームごとに1回**だけ
  ノードワールド行列を計算し、同一モデルへの複数アタッチで共有する（キー=Model スロット entity）。
- **書込**: `モデルアクタのワールド行列 × ジョイントワールド行列(モデル空間) × オフセット行列` を
  自アクターの `Transform` と Model `instance_mats[0]` へ書き込む（registry の instance_mats 同期と同方針。
  行列を直接書き込みシアーの丸めを避ける）。呼び出しは `frame_renderer` のアニメ評価後・描画収集前、
  **Edit / Play 両モード毎フレーム**（パーティクル常時プレビューと同様）。
- **エラー**: `joint_name` 不一致は (スロット, 名前) 単位で**1回だけ**警告し追従無効。
- **エディタ**: ACTOR_COMPONENTS の Model 送信に `joints`（skin ジョイント名優先・無ければノード名）を追加。
  Inspector にジョイントドロップダウン＋オフセット3行、`SET_JOINTATTACH_FIELD` IPC。選択時ギズモは
  ソケット位置に RGB 軸十字（`jointattach_scene_gizmo`, light_scene_gizmo 流儀）。
- **検証**: `animator::tests::node_world_matrices_compose_hierarchy_bind_pose`（親子ノードのワールド行列
  階層合成）pass。cargo build / test・dotnet build 0 エラー。

### Phase RM: GPU メッシュレットカリング（第1弾） 【状況: 実装済み（実機検証待ち, LOD0 不透明のみ）】

不透明メッシュの LOD0 描画を「メッシュレット単位の GPU カリング＋間接描画」に置換する第1弾。
**最重要要件は既存描画との見た目一致（トグル OFF ＝完全に従来経路）**。スキン/透明/Mask/
シャドウ/RT影/ID ピック/アウトラインは一切変更しない。LOD1〜3 も従来経路のまま（遠距離は安全側）。

#### メッシュレット焼き込み（ロード時 → .smdl v3）
- 定数: `MESHLET_MAX_VERTS=64` / `MESHLET_MAX_TRIS=124`（4 の倍数, meshopt 制約）/ `MESHLET_CONE_WEIGHT=0.5`。
- `gltf_loader::build_meshlets_for_primitive`（OBJ も共用）が **非スキンプリミティブの LOD0** を
  `meshopt::build_meshlets` で分割し、`compute_meshlet_bounds` で境界球＋法線コーンを計算。
  スキン・三角形なし・生成失敗は空（＝従来経路）。
- `Primitive` に `meshlets: Vec<MeshletDesc>`（記述子＝境界球/コーン/オフセット, Pod 48B）＋
  `meshlet_vertices: Vec<u32>`／`meshlet_triangles: Vec<u8>`（連結配列）を追加。
- キャッシュ `CACHE_FORMAT_VERSION` を **2→4** に更新（旧キャッシュは自動再生成）。v4 では記述子・連結配列
  とも `asset_cache::visit_blob_slots` の生ブロブ領域（bytemuck ゼロコピー）へ格納し、bincode メタには
  メッシュレット関連の大配列を一切残さない（Sponza 級=数万記述子の debug serde コスト回避）。
  ラウンドトリップ test 更新済み。
- **生成コスト実測（Intel New Sponza Main, 3.75M tris / 405 prims / 42k meshlets, debug）**:
  メッシュレット分割＋境界計算＝**約 1.9 秒**（`[profile.dev.package.meshopt] opt-level=2` 追加後。
  LOD simplify も同時に高速化）。キャッシュ増分は約 21 MiB。初回生成全体（parse 89s + tex 27s +
  encode 12s ≒ 2 分強）の支配項は既存のテクスチャデコード/圧縮/書出であり、メッシュレットではない。
  切り分け用に `[SEED cache] 初回ロード` 行へ内訳 `(内 lod=..ms meshlet=..ms)` を追加（`loader::gen_timing`）。

#### カリングパス構成（compute）
- 新規 `shaders/meshlet_cull.wgsl`＋`pipeline::MeshletCullPipeline`（compute, 単一 BindGroup）。
  group0: 0=instances(ModelUniform 配列, RO) / 1=meshlets(`GpuMeshlet` 配列, RO) /
  2=draw_cmds(DrawIndexedIndirect 配列, RW) / 3=draw_count(atomic<u32>, RW) / 4=params(uniform)。
- スレッド = 可視 LOD0 インスタンス × メッシュレット。各スレッドが境界球をインスタンス行列で
  ワールド空間へ変換（中心=model 行列, 半径=基底ベクトル長 max で保守的過大評価, コーン軸=normal_matrix）し、
  ①視錐台 6 平面（`extract_frustum_planes` と同一・未正規化平面を都度正規化, 球マージン `SPHERE_MARGIN`）
  ②法線コーン背面棄却（meshopt 球ベース式・`cone_cutoff<0.999` のみ・`CONE_MARGIN` で保守側）
  を通過したものだけ `atomicAdd(draw_count)` で先頭からコンパクトに DrawIndexedIndirect を書き出す。
- `GpuPrimitive` は upload 時に**展開済みメッシュレットインデックス**（各三角形コーナー→元頂点
  インデックスへ解決し連結, INDEX バッファ）＋**記述子 storage バッファ**（`GpuMeshlet`, stride48,
  offset 0/4/16/28/32/44 を layout_test で固定）を構築。

#### 間接描画統合
- `InstancedModelBatch` が `node_prim_list` と同順の per-prim スロット
  （cmd/count/params バッファ・毎フレーム再構築 BindGroup・ディスパッチ数）を保持。
  `prepare_meshlet_cull`（compute パス前: params 更新・count 0 リセット・BG 構築・Blend/非対象スキップ）→
  `record_meshlet_cull`（専用 compute パスで dispatch）。
- `draw_model_indirect` に `meshlet_cull: bool` 引数を追加。**LOD0 かつ 非スキン かつ アクティブスロット**の
  ときのみ、展開インデックスを張り `multi_draw_indexed_indirect_count(cmd, 0, count, 0, capacity)` で描画。
  それ以外（LOD1〜3・スキン・Blend・メッシュレット無し・非対応）は従来 `draw_indexed` へ自動フォールバック。
  `first_instance`（各コマンド）＝可視インスタンス番号で、メッシュ VS の group1 インスタンス行列を index。
  既存 mesh パイプライン・全 BindGroup をそのまま流用（`INDIRECT_FIRST_INSTANCE` は既存要求）。
- 呼び出し: メインパス不透明 LOD0 のみ `meshlet_active` を渡す。カメラプレビュー・ギズモは `false`（従来経路）。

#### トグルとフォールバック
- 実行時トグル `PostFxSettings.meshlet_cull`（既定 **true**）。`SET_POST_FX` JSON に `"meshlet_cull":bool`
  （欠落時 true）を追加、`load_graphics_settings` が `project_settings.json` の `meshlet_cull`(既定 true) を読む
  （**コミット禁止**）。
- `meshlet_active = 設定 && MULTI_DRAW_INDIRECT_COUNT 対応`。**非対応 GPU は本値に関わらず完全に従来経路**
  （`gpu_resources::set/meshlet_cull_supported`, `mod.rs` で対応判定＋条件付き feature 要求＋起動ログ `[SEED MESHLET]`）。
- **OFF＝完全に従来経路**（compute 前処理・dispatch・間接描画をすべてスキップ）＝ A/B パリティ検証用。

#### パリティ担保の設計
- OFF は draw コードの分岐が一切発火せずビット単位で従来と同一。ON でもカリングは**保守側**（境界球マージン・
  コーン cutoff マージン・未正規化平面の安全処理・スキップ時フォールバック）に倒し、
  「本来見えるメッシュレットを誤って捨てない」ことで見た目一致を担保。展開インデックスは元の巻き順を保持し
  back-face カリング挙動も不変。

#### ビルド・テスト
- `cargo build` / `cargo test --bin SEED` 0 エラー（35 passed）。追加 test: `meshlet_tests`（分割の三角形
  網羅・境界球が全構成頂点を内包・コーン軸単位性・スキン/縮退で空）／`meshlet_gpu_tests`（`GpuMeshlet` 48B
  レイアウト固定・`meshlet_cull.wgsl` の naga parse+validate）／cache v3 ラウンドトリップ。

#### 実機観点・残 TODO（第2弾以降）
- **[PERF]** に `meshlet=<考慮数>考慮`（このフレームに評価したメッシュレット×インスタンス総数）。
  **生存数の表示は GPU→CPU リードバックが必要なため未実装（TODO）**。現状は「総数（考慮数）」のみ。
- スキンメッシュのメッシュレットカリング（毎フレーム境界再計算）／LOD1〜3 への拡張／間接コマンドの
  真のコンパクション統計リードバック／per-prim ではなく全プリミティブ統合ディスパッチ／Hi-Z オクルージョン併用。
- **実機での視覚 A/B 検証（トグル ON/OFF で見た目一致）が受入の最終条件。GPU 実行環境が無いため本実装は未検証。**

---

# フェーズ D: Deferred + Clustered Lighting 化（2026-07-13 開始）

## 目標構成（ユーザー合意・確定）

**不透明 = Deferred（G-Buffer）+ Clustered Lighting / 半透明 = Forward（WBOIT）**

ハイエンド（AAA 級）を目標とし、**デカール・SSAO・SSR を確実に実装する**という要件が確定したため、
G-Buffer を土台として据える。G-Buffer はこれらスクリーンスペース効果の前提そのものなので、
後から足すより最初から持つほうが安い。

Forward+ ではなく Deferred を選ぶ根拠: 多灯対応だけなら Forward+ で足りるが、
デカール／SSAO／SSR を入れるなら G-Buffer が要る。逆に Deferred のデメリット
（MSAA と相性が悪い）は、本エンジンが元々 MSAA を使っていない（FXAA）ため損失ゼロ。

## 二重メンテ問題への構造的対策（最重要）

ハイブリッド構成である以上、「G-Buffer ライティングパス」と「フォワード半透明パス」の
2 つがライトを評価する。ここで実装が二重化すると、BRDF・ライト種別・影の方式を変えるたびに
2 箇所を同期する羽目になる。

→ **`evaluate_lighting(Surface) -> vec3` を唯一のライト評価実装とし、両パスから呼ぶ。**
   `Surface` は VertexOutput に依存しないので、G-Buffer から復元した Surface でもそのまま使える。
   Clustered 化も `lighting_eval.wgsl` のループ冒頭だけで完結する。

## フェーズ

- **D1 shade_pbr の分割: 完了**（master abe1d82, 2026-07-13）
  surface.wgsl（Surface 定義のみ）/ surface_gather.wgsl（採取・group2 依存）/
  lighting_eval.wgsl（evaluate_lighting・group0/4 のみ依存）/ shader_fragment.wgsl（薄いラッパ）。
  見た目不変（式のオペランド順・演算子とも無変更であることを行単位差分で確認）。
  **Surface 定義だけを単独ファイルに切るのが要点**: pipeline_config::reflect_bgls は
  global_variables を使用有無に関わらず走査して BGL を作るため、将来のライティングパスが
  Surface 定義欲しさに surface_gather.wgsl を連結すると、使わない group2（テクスチャ 11 binding）を
  要求する壊れたレイアウトになる。

- **D2 Clustered Lighting: 完了・実機検証待ち**（master 524b315, 2026-07-13）
  16×9×24=3456 フロクセル、Z は指数分割。MAX_LIGHTS 64 → 1024。
  Directional はクラスタに入れず別枠（配列先頭へ安定分割し常時評価）。
  ライト境界は全て保守側（Spot は円錐を厳密に包含する球、クラスタ体積は錐台セルを包む AABB、
  接触も交差、半径に 1.05 マージン）。**ライトが誤って落ちると暗くなり原因追跡が困難なため。**
  group4 に binding 7=グリッド / 8=ライトインデックス / 9=ClusterParams を追加
  （新規 group を増やすと max_bind_groups=5 で起動失敗する既知の地雷を回避）。
  **複数カメラ問題**: クラスタはカメラ固有なので、メインカメラ基準のクラスタをカメラプレビューで
  使うとライティングが壊れる。ClusterParams.enabled で切替え、プレビュー・非透視カメラ・
  near/far/fov 不正・ライト0灯は enabled=0（従来の全ライト線形走査）へフォールバックする。
  最悪でも「速くならない」だけで暗くはならない設計。

- **D3 G-Buffer + Deferred 不透明パス: 実装済み・実機検証待ち**（Phase A: gbuffer.rs/deferred.rs 基盤、
  Phase B: フレームループ接続・IPC 切替・エディタ UI・本ドキュメント）
  fs_gbuffer が gather_surface の結果を MRT へ焼き、フルスクリーンのライティングパスが
  G-Buffer から Surface を復元して evaluate_lighting をそのまま呼ぶ。半透明は WBOIT／距離ソートの
  フォワードのまま（G-Buffer 深度を Load してテストする）。
  RT 影はデファードのほうが安くなる（可視ピクセルのみレイを飛ばす）。
  メッシュレットカリング（compute + multi_draw_indirect_count）は G-Buffer パスでもそのまま使える。

  **G-Buffer レイアウト**（gbuffer.rs / gbuffer_write.wgsl が正典）:
  | RT | フォーマット | 内容 |
  |----|------------|------|
  | RT0 gbuffer0 | Rgba8Unorm | albedo.rgb + occlusion.a |
  | RT1 gbuffer1 | Rgba16Float | world normal.xyz + 0 |
  | RT2 gbuffer2 | Rgba8Unorm | metallic.r + roughness.g + 予約(b,a) |
  | RT3 gbuffer3 | Rgba16Float | emissive.rgb(HDR) + 0 |

  **Surface 復元の焼く・復元・代用対応表**（surface_gather.wgsl で採取する Surface フィールドと
  G-Buffer 4 枚＋深度の対応。フルスクリーン・ライティングパスは deferred_lighting.wgsl が
  この対応でテクスチャから Surface を再構築してから evaluate_lighting を呼ぶ）:
  | Surface フィールド | 由来 |
  |---------------------|------|
  | albedo | gbuffer0.rgb |
  | occlusion | gbuffer0.a |
  | normal（world） | gbuffer1.xyz |
  | metallic | gbuffer2.r |
  | roughness | gbuffer2.g |
  | emissive | gbuffer3.rgb |
  | world position | depth（DepthOnly aspect）+ inv_view_proj から再構築（G-Buffer に位置は焼かない） |
  | ライト方向・視線方向等 | ライティングパスの CameraUniform（自前宣言、shader_common.wgsl と同一
    レイアウト）とスクリーン UV から算出 |

  **パス順序**（deferred_active＝true のフレーム、frame_renderer.rs のメインシーンパス直前に挿入）:
  1. G-Buffer パス（`begin_gbuffer_pass_to`）: 不透明ジオメトリのみを 4 枚の MRT + 深度へ焼く
     （深度は Clear(1.0) で新規に確保し直す＝このパスが「深度を最初に書く」パスになる）。
  2. G-Buffer BindGroup 生成（`create_gbuffer_bind_group`）＋ フルスクリーン・ライティングパス
     （`begin_deferred_lighting_pass_to`）: HDR シーンへ Clear(clear_color) してから 3 頂点の
     フルスクリーン三角形でライティングを復元する（深度 >= 1.0＝背景は shader 側で discard、
     クリア色がそのまま残るためメインパス clear と同じ見た目になる）。
  3. メインシーンパスを **Load** で再開（`begin_scene_pass_load_to`）: G-Buffer パス・
     ライティングパスが書いた HDR／深度／ステンシルを一切クリアせず保持し、
     スカイボックス・半透明（距離ソート／WBOIT）・ギズモ・2D オーバーレイ等の
     フォワード要素だけを重ねて描く（不透明の draw_model_indirect 呼び出しはスキップする）。
     スカイボックスは深度 LessEqual テストにより背景（depth=1.0）のみに正しく出る。

  **deferred=false（フォールバック）**: `PostFxSettings.deferred` が false のときは
  上記 1〜3 を一切スキップし、従来どおり `begin_scene_pass_to`（Clear）から不透明を
  `draw_model_indirect` で直接 HDR へ描く完全フォワード経路になる（コード的に無改変）。
  デファードはメインカメラの不透明・Lit のみが対象で、Unlit／ワイヤーフレーム表示・
  2D シーンビューは `deferred_active` 判定により常にフォワードへフォールバックする
  （`frame_renderer.rs` の `deferred_active` 算出コメント参照）。

  **light_common.wgsl・pbr_common.wgsl の抽出**: 旧 shader_common.wgsl はマテリアル
  （group2）とライト（group4）を同居させていたため、「ライトだけ使いたい」デファードの
  ライティングパスも shader_common.wgsl を連結せざるを得ず、不要な group2 バインディングが
  リフレクションに載って破綻していた。そこでバインディングを持たない PBR ヘルパー群を
  pbr_common.wgsl、ライト構造体・binding 宣言を light_common.wgsl として shader_common.wgsl
  から分離し、deferred_lighting.wgsl は shader_common.wgsl を一切連結せず pbr_common.wgsl /
  light_common.wgsl だけを連結する（フォワード系は従来どおり
  cluster_common→pbr_common→shader_common→light_common の順で連結し、shader_common.wgsl
  経由で両方を利用する）。gbuffer_write.wgsl 側は逆にライティングを一切必要としないため
  light_common.wgsl / pbr_common.wgsl のどちらも連結しない（マテリアル採取のみで完結し、
  使わない group4 バインディングをリフレクションに載せないため）。

  **IPC / 設定**: `SET_POST_FX` JSON に `"deferred":bool` を追加（欠落時 true）。
  `project_settings.json` の `deferred` キー（起動時読込, 欠落時 true）。
  エディタはビューポート設定ポップアップ「ポストプロセス」内チェックボックス
  `ChkDeferred`（既定チェック）から切り替える。

- **D4 SSAO / RT-AO: 実装済み（Phase D4）**。Deferred 有効時のみ動く独立フルスクリーン AO パス。
  G-Buffer の深度＋ワールド法線から半解像度 `ao_raw`（`Rgba16Float`）へ AO を焼き、いもす法ブラー
  （`renderer/imos_blur.rs`）で `ao_b` へ均し、deferred ライティングの `occlusion` へバイリニアで乗算する
  （アンビエント/DDGI/疑似バウンスにのみ効き、直接光は暗くしない）。SSAO＝半球カーネル法（RAY_QUERY 不要・
  常時）、RT-AO＝コサイン半球の短レイ（RT 対応 GPU のみ、非対応は SSAO へ降格）。強度は `ao_intensity` ノブ。
  実装: `renderer/ao.rs`（`AoPipelines`/`AoTargets`）＋ `shaders/ao_common.wgsl` / `ao_ssao.wgsl` / `ao_rt.wgsl`。
- **D5 Deferred Decal: 未着手**
- **D6 SSR / RT 反射: 実装済み（Phase D6）**。Deferred 有効時のみ動く独立フルスクリーンパス。
  不透明 Deferred ライティング完成後に G-Buffer＋scene_hdr を入力に専用 RT（RT_REFLECTION,
  Rgba16Float, Clear=0）へ反射色を描き、Additive(One/One)+LoadOp::Load で scene_hdr へ加算合成する。
  scene_hdr は入力（読み）・RT_REFLECTION は出力（書き）で別テクスチャのため読み書き競合が起きない。
  SSR は RAY_QUERY 不要のビュー空間線形マーチ＋二分リファイン、RT は TLAS への closest-hit 1 本＋
  ヒット点近似シェーディング（`ddgi_probe_update.wgsl` の直接光/バウンスを移植・★同期必須★）。
  粗面は roughness 0.30→0.55 でフェード、SSR はヒット UV に画面端フェード。強度は
  `PostFxSettings.reflection_intensity`（既定 1.0, SET_POST_FX の `reflection_intensity`）。
  BindGroup: SSR=group0..3（4）/ RT=group0..4（5＝max_bind_groups 上限）/ composite=group0（1）。
  実装: `renderer/reflection.rs`, `shaders/reflection_common|ssr|rt|composite.wgsl`, `frame_renderer.rs` 配線。
- **SSGI（スクリーンスペース GI）: 実装済み（Phase SSGI）**。GI の第 3 モード（`GiMode::Ssgi`）。
  Deferred 有効時のみ動く独立フルスクリーン AO の**カラー版**パス。G-Buffer の深度＋ワールド法線から
  コサイン半球方向へ 3 本（`SSGI_NUM_DIRS`）のスクリーンスペースレイを 16 ステップ×最大 5m でマーチし、
  ヒットしたら scene_hdr（不透明ライティング済み）の色を拾ってコサイン平均＝1 バウンス間接光。
  ミスはフラットアンビエント色で埋める（黒にしない）。半解像度 `ssgi_raw`（`Rgba16Float`）へ焼き、
  ピクセルごとの IGN 回転＋いもす法カラーブラー 3 反復（`renderer/imos_blur.rs` の `.rgb` 対応）で
  デノイズする。時間的蓄積はしない（モーションベクタが無い。TODO(SSGI-Temporal)）。
  **1 フレーム遅延方式**: `evaluate_gi_ambient`（`lighting_eval.wgsl`）は同じライティングパス内にあり
  今フレームの HDR をすぐ使えないため、G-Buffer → デファードライティング（**前フレーム** の `ssgi_b` を
  `t_ssgi` で読む）→ SSGI 生成パス（**今フレーム** の HDR → 次フレーム用 `ssgi_b`）の順で走らせる。
  初回/リサイズ/有効化直後の 1 フレームだけ `GiParams.enabled=0` でフラットに倒す（`SsgiTargets::ensure`
  の再確保通知で検知）＝ゼロクリアがフラットアンビエントと等価になる。強度は `gi_intensity`（DDGI と共通）。
  **バインディング配置**: `t_ssgi`/`s_ssgi` は deferred ライティング専用の group1（binding 8/9）に置き、
  共有 `evaluate_gi_ambient` には持ち込まない。deferred の fragment だけが採取して `Surface.screen_gi`
  （`.rgb`＝間接光, `.a`＝有効フラグ）へ渡す。**半透明フォワード**（WBOIT/距離ソート）は `Surface` を
  `var s: Surface;` でゼロ初期化する＝`screen_gi.a=0` となり、`evaluate_gi_ambient` は SSGI モードでも
  フラットアンビエントへフォールバックする（スクリーン入力は不透明前提のため半透明に効かせない）。
  `GiParams.gi_mode`（旧 `_pad0` を転用・サイズ 80B 不変）が `flat/ddgi/ssgi` を切り替える。
  実装: `renderer/ssgi.rs`（`SsgiPipelines`/`SsgiTargets`/`SsgiParams`）＋ `shaders/ssgi_common.wgsl` /
  `ssgi_gen.wgsl`、`deferred_lighting.wgsl`（group1 binding8/9）、`lighting_eval.wgsl`（SSGI 分岐）、
  `surface.wgsl`（`screen_gi`）、`frame_renderer.rs` 配線。BindGroup: 生成 group0..2（3, 上限内）。
- **RT ソフト影のデノイズ（半解像度マスク＋バイラテラル）: 実装済み（Phase RT-Shadow-Denoise）**。
  RT ソフト影の半影が「砂を撒いたようなガサガサのディザ」に見える症状（ピクセルごとの IGN 回転×確率的
  サンプリングによる遮蔽率の量子化ノイズ）を根治する。**deferred 有効かつ影方式が Rt かつ soft_radius>0 の
  ライトがあるフレーム**でのみ動く独立フルスクリーンパス（AO/SSGI と同じ half-res 基盤）。
  CPU（`shadow_mask.rs::assign_shadow_mask_slots`）が soft_radius>0 のライトを intensity 降順で最大
  `RT_SHADOW_MASK_LIGHTS=4` 灯選び、各 `GpuLight.shadow_mask_slot`（offset 100・旧 `_pad_bounce0` を転用・
  112B 不変）へスロット番号を書く（溢れた分と 5 灯目以降・ハード影は従来のインライン経路＝1 度だけ警告ログ）。
  マスク生成パスは G-Buffer＋TLAS から半解像度 `mask_raw`（**texture_2d_array・4 レイヤ・Rgba16Float**）へ
  選定ライトごとに既存の `rt_shadow_factor`（無改変・色付き影込みの vec3 透過率）を評価して MRT 4 レイヤ出力する。
  **各レイヤの `.rgb`=透過率／`.a`=half-res ビュー空間深度**（バイラテラルブラーの深度ガイドを同梱）。
  別 R32Float アタッチメント方式は 4×Rgba16Float=32B に 4B を足して `max_color_attachment_bytes_per_sample=32` を
  超過して `create_render_pipeline` がパニックするため採らず、未使用だった `.a` へ深度を載せる（f16 精度＝相対
  ~0.05% で、深度許容幅 相対 5% に対し十分。全レイヤ同じ深度＝画素固有・ライト非依存）。
  **デノイズは影マスク専用の separable バイラテラルブラー**（`shadow_mask_bilateral.rs`＋
  `shaders/shadow_mask_bilateral.wgsl`。半径 3px・ガウス空間重み×深度類似度重み `exp(-|dz-dc|/tol)`）を
  **レイヤごとに H→V の 2 パス**掛けて `mask_b` へデノイズする（深度ガイドは各レイヤ自身の `.a` を読む。深度自体は
  ブラーせず出力 `.a` は中心タップの深度を素通し＝次パス・下流の深度基準を維持）。
  **なぜバイラテラルか**: いもす法（累積和）は走査中に窓和を保持する構造上バイラテラル化できず、深度エッジを
  跨いで影値を混ぜてカーテンのフチにハロー（薄い帯）を生む。エッジ保持が必須な影マスクだけ固定タップの
  バイラテラルに切替（AO/SSGI/すりガラスは低周波用途でこの滲みが問題化しないため `imos_blur` を継続使用）。
  deferred ライティングは `mask_b` を group1 binding10（D2Array・`.rgb`）で**深度考慮の joint bilateral アップサンプル**
  （4 テクセル×バイリニア重み×深度類似度。深度はフル解像度 `t_depth` から生成時と同一写像で復元＝追加 binding
  なし。上流バイラテラルと合わせ上流・下流の両方でエッジを保つ）して `Surface.shadow_mask[slot]`／
  `shadow_mask_valid=1` を載せ、ライトループがマスク対象ライトでレイを飛ばさずこの値を遮蔽率にする。
  **forward/WBOIT**（`Surface` ゼロ初期化＝`shadow_mask_valid=0`）とマスク非対象ライト（`slot<0`）は従来どおり
  インライン `rt_shadow_factor`。半解像度化でレイ本数は従来比 ~1/4。ハード影（`cone_radius=0`）は完全に不変。
  `L`／光源距離／`cone_radius` はインライン経路とマスク経路で共有関数 `light_shadow_geometry`
  （`light_common.wgsl`。`RT_DIR_TMAX`／`RT_SHADOW_MAX_CONE_RADIUS` も `lighting_eval.wgsl` から移設）で算出し
  両経路の影が一致する。
  BindGroup: group0=camera/1=G-Buffer/2=`ShadowMaskParams`/3=gap/4=ライト+TLAS（RT 複合 BG 借用・上限 5 内）。
  マスク生成 MRT は 4 レイヤ（Rgba16Float・blend None・`.a` に深度同梱）。バイラテラルブラー BindGroup:
  group0（0=params/1=src マスク（`.a`=深度ガイド）/2=dst storage）。実装: `renderer/shadow_mask.rs`
  （`ShadowMaskPipelines`/`ShadowMaskTargets`＝mask_raw/a/b/`ShadowMaskParams`＋選定）＋
  `renderer/shadow_mask_bilateral.rs`（`ShadowMaskBilateral`）＋`shaders/shadow_mask.wgsl`（`.a` に深度同梱）／
  `shaders/shadow_mask_bilateral.wgsl`、`light_common.wgsl`（`shadow_mask_slot`／共有ジオメトリ）、`surface.wgsl`
  （`shadow_mask`／`shadow_mask_valid`）、`deferred_lighting.wgsl`（group1 binding10/11＋joint bilateral アップサンプル）、
  `lighting_eval.wgsl`（マスク or インライン分岐）、`deferred.rs`（ダミー 4 レイヤ配列＋Filtering サンプラー）、
  `frame_renderer.rs` 配線。

## D3 実機テスト観点（GPU 実機確認が必要。開発環境では cargo build/test までしか検証できない）

- **見た目パリティ**: 同一シーンで `deferred` ON/OFF を切り替え、不透明の見た目（アルベド・法線・
  金属度・粗さ・エミッシブ・影）が一致すること。
- **Mask discard**: AlphaMode::Mask のマテリアルが G-Buffer パスでも正しく discard されること。
- **両面（cull None）**: CullFace::None のマテリアルが G-Buffer パスでも両面描画されること。
- **メッシュレットカリング**: `meshlet_cull` ON 時、G-Buffer パスの LOD0・非スキンでも
  multi_draw_indexed_indirect_count 経路が正しく機能すること（deferred/meshlet_cull 両方 ON の組合せ）。
- **RT 影**（RT 対応 GPU）: デファードのライティングパスで `deferred_lighting_rt` バリアントが選択され、
  RT 影が正しく落ちること。RT 非対応 GPU では rt_off バリアント＋非 RT ライト BG に安全側フォールバック
  すること。
- **半透明の重なり**: 距離ソート／WBOIT いずれも、デファードの不透明（G-Buffer 深度）と正しく前後関係
  が取れること（メインパスが Load で深度を保持しているため）。
- **スカイボックス背景**: CameraLocked/WorldAnchored いずれも背景として正しく見えること
  （深度 LessEqual テストにより G-Buffer ジオメトリの手前に出ないこと）。
- **カメラプレビュー不変**: カメラプレビュー小窓は本 Phase B の変更対象外（メインカメラのみ
  デファード化）であり、見た目が変わらないこと。
- **Play letterbox の帯**: LetterBox/PillarBox 時、G-Buffer パス・ライティングパスにも
  viewport/scissor が正しく適用され、帯エリアにジオメトリが漏れないこと。
- **ワイヤ・unlit 時フォワードフォールバック**: エディタのシーンビュー表示モードを
  Unlit／Wireframe に切り替えると `deferred_active` が false になり、フォワード経路（従来どおり）
  で描画されること。
- **環境光・エミッシブ**: アンビエントライトとエミッシブがデファードのライティングパスでも
  フォワードと同じ強度で反映されること。

## 影について（今回スコープ外・ユーザー判断）

現状のシャドウマップは Directional 1 灯（CSM）＋ Spot 最大 4 灯が上限。多灯すべてに影を落としたい
場合はインラインRT影（全ライト種対応済み）を使う運用になる。シャドウアトラス導入は将来課題。

## 事実訂正（よくある誤解）

- SEED は **DirectX12 直叩きではなく wgpu**（DX12/Vulkan バックエンド）。
- **メッシュシェーダーは使っていない**。メッシュレット分割はしているが、実体は compute による
  カリング + `multi_draw_indexed_indirect_count`（wgpu の EXPERIMENTAL_MESH_SHADER は未成熟）。
- **MSAA は未使用**（AA は FXAA）。よって「Deferred は MSAA と相性が悪い」は本エンジンでは損失ゼロ。

---

## フェーズ RT-GI（DDGI — プローブ格子方式のリアルタイム レイトレース GI）

間接光（1 バウンス以上）を、画面解像度から独立したプローブ格子のレイトレで動的に計算する。
`lighting_eval.wgsl` のアンビエント項を「プローブ補間による間接放射照度」で置き換える（deferred /
forward 両対応が自動で付く）。RT 対応 GPU（`EXPERIMENTAL_RAY_QUERY`）でのみ有効。非対応 GPU では
従来のフラットアンビエントへ完全フォールバックする。

### 構成（新規/変更ファイル）
- `renderer/ddgi/`（新規モジュール）
  - `octahedral.rs` … 八面体 dir↔uv（WGSL と往復一致をテスト）
  - `grid.rs` … プローブ格子の幾何・AABB フィット・番号/座標/ワールド変換
  - `params.rs` … `GiParams`（GPU uniform, 80B スカラー詰め）と naga サイズ照合
  - `resources.rs` … `GiResources`（アトラス2枚＋履歴2枚・GiParams バッファ・更新 compute BG・ディスパッチ）
- `shaders/ddgi_common.wgsl`（新規・バインディングなし共有定義）… `GiParams`／八面体／格子・アトラス索引／
  `ddgi_sample_irradiance`（トライリニア＋チェビシェフ可視性＋法線余弦重み）。fragment と compute が共有。
- `shaders/ddgi_probe_update.wgsl`（新規・compute）… 1 ワークグループ=1 プローブ。rayQuery でレイを飛ばし、
  ヒット点をシェーディングして八面体タイル（放射輝度8×8／可視性16×16）へ積分・時間蓄積・ガター複製。
- `light_common.wgsl` … group4 に GI バインディング 10〜13 を追加（`GiParams` uniform ＋ アトラス2枚 ＋ サンプラ）。
- `lighting_eval.wgsl` … アンビエント項を `evaluate_gi_ambient`（GI 有効時はプローブ補間、無効時は従来値）へ。
- `pipeline.rs` … `GiUpdatePipeline`（compute, RT 対応 GPU のみ）。
- `rt_shadow.rs` … 影用 TLAS を GI と共有。TLAS パッキングに相乗りして「インスタンス順の平均アルベド storage」を詰める。
- `gpu_resources.rs` / `loader/*` … `Material.avg_albedo`（プリミティブ平均アルベド）を追加。`CACHE_FORMAT_VERSION` 8→9。

### ヒット点シェーディングの近似（bindless 回避）
inline RT ではヒット三角形のマテリアルテクスチャ・頂点属性を引けない。以下で回避する:
- **プリミティブ平均アルベド**: ローダでベースカラーテクスチャのアルファ加重平均（リニア）×`base_color_factor`
  を焼き、`asset_cache`（v9）に格納。TLAS `custom_data`（インスタンス番号）で storage を引く。
- **ヒット法線 = −レイ方向**（頂点フェッチ不可のため）。表面はおおむねレイ原点側を向くという近似。
- ヒット放射輝度 = albedo/π ×（各ライトの直接光: 距離減衰＋ndl(近似法線)＋**主要光1灯へのシャドウレイ1本**）
  ＋ **前フレームのプローブ照度をヒット点でサンプル**（多重バウンス、`recursive_weight` 係数）。

### 近似の限界（実機で意識する点）
- **平均アルベド**: プリミティブ内でテクスチャの色ムラ（模様）を平均で潰すため、色付きバウンスは大まかになる。
- **擬似法線（−レイ方向）**: 凹面・薄板で法線が実際とズレ、直接光の ndl とバウンス方向が不正確になり得る。
- **GI ジオメトリ = 影キャスター**: TLAS を影と共有するため、`cast_shadows=false` の静的メッシュは GI に寄与しない。
- **画面外を拾える**（SSGI との本質的差）: プローブは全方位レイなので、画面外/オフスクリーンの光源・遮蔽も反映する。
- **プローブが壁内**: 裏面検出をしていない（擬似法線のため）。壁に埋まったプローブは暗くなり得る（チェビシェフで緩和）。
- **可視性フォーマット**: 仕様の Rg16Float ではなく **Rgba16Float**（コアの storage 対応フォーマット制約。`.rg` のみ使用）。
- **格子フィット**: 全静的バッチのワールド AABB 合併へ毎フレーム簡易フィット（`world_aabbs` キャッシュ由来で 1 フレーム遅延あり）。
  次元は 16×8×16 固定（UI では変えない）。

### ノブ（`GiSettings`、SET_POST_FX に相乗り。UI は enabled＋intensity のみ、詳細は IPC で受ける）
- `gi_enabled`（既定 true。RT 非対応で強制 false） / `gi_intensity`（既定 1.0）
- `gi_probes_per_frame`（既定 256／2048 プローブ中） / `gi_rays_per_probe`（既定 64、上限 64）
- `gi_hysteresis`（既定 0.97、時間的蓄積） / `gi_recursive_weight`（既定 0.5、多重バウンス）

### GPU リソース概算（既定 16×8×16=2048 プローブ）
- 放射輝度アトラス 2560×80、可視性アトラス 4608×144（各 Rgba16Float、履歴コピー各 1 枚）… 合計 ≈ 14 MB VRAM。
- 平均アルベド storage: 4096 インスタンス × 16B = 64 KB。
- 1 フレームのレイ本数（既定）: 256 プローブ × 64 レイ ≈ 16K 本 ＋ 主要光シャドウレイ最大 16K 本 ≈ 32K 本／フレーム（解像度非依存）。

### バインディング/ストレージ本数（上限に対する余裕）
- group 数は 0〜4 のまま（`max_bind_groups=5` を厳守。新グループ無し）。group4 は RT バリアントで binding 0〜13（14 本）。
- フラグメント stage の storage buffer: group4 で 0/7/8 の **3 本**（GI 追加ぶんは uniform＋texture＋sampler で storage 増なし）。
  `max_storage_buffers_per_shader_stage=12` に対し十分な余裕。
- GI 更新 compute の storage buffer: lights(1)＋albedo(4) の **2 本** ＋ storage テクスチャ 2 枚（書込）。いずれも上限内。

### 実機確認観点（監督者のスモークテスト・ユーザーの目視）
- 起動ログ `[SEED GI] DDGI: 有効（プローブ 2048 個 / 更新 256個×64レイ/フレーム）`（非対応時はフォールバックログ）。
- 起動時にビュー設定の「RT-GI」チェック ON で、影の中・軒下・室内が真っ黒でなく回り込み光で持ち上がること（数フレームで収束）。
- 強度スライダーで間接光が滑らかに増減すること。OFF で従来のフラットアンビエントに戻ること。
- カメラプレビュー小窓・Unlit/Wireframe 表示に GI が漏れ出ないこと（GiParams.enabled=0 経路）。
- 20〜30fps を維持できること（RTX 3060 Laptop 35W）。重ければ `gi_probes_per_frame` / `gi_rays_per_probe` を下げる。
- 時間的ちらつき（プローブ更新ローテーションのバンディング）が許容範囲か。強い場合は `gi_hysteresis` を上げる。

### 検証（実装時点）
- `cargo test`: 81 passed（+9: 八面体往復・格子往復・GiParams naga サイズ・プローブ更新 compute の naga parse+validate 等）。
- 全フラグメントバリアント（mesh/skinned × RT on/off・deferred on/off・WBOIT）の WGSL 連結が naga validate を通過。
- `cargo build` / `cd editor && dotnet build` ともに 0 エラー。
- **未検証（実機 GPU 無し）**: 実際のレンダリング結果・fps・バインドグループの実行時整合はスモークテストで確認すること。

---

## フェーズ RT-Translucency（高品質半透明＝色付き影＋屈折） 【状況: 実装済み（実機検証待ち）】

`TranslucencyMode::Rt` を実体化する。**Rt = 「色付き影[RT シャドウレイ]＋屈折[スクリーンスペース]」の
パッケージ**（屈折はスクリーンスペースだが、色付き影が RT 前提のため RT 対応 GPU をまとめて要求する）。
RT 非対応 GPU では `raster` へ降格する（`render_features.rs::resolve`）。ゲートは `LightMeta.translucency_rt`
（旧 `_pad`, offset 12）に**ビットマスク**で載せる: bit0=色付き影 / bit1=屈折可能。

### A. 色付き影（ガラス越しの光が染まる）
- RT シャドウレイ（`rt_shadow_on.wgsl`）は従来 cull_mask 0x01（不透明のみ）。色付き影では、不透明で
  遮蔽されていないとき**第 2 のクエリ（cull_mask 0x02＝`RT_MASK_NON_OPAQUE`。半透明レイヤーは TLAS 登録済み）**を
  発射し、透過色を累積する。inline ray query は最近ヒットしか返さないため、**tmin をヒットの先へ進めながら
  最大 `RT_TRANSLUCENT_MAX_HITS`(=4) 回再トレース**して重なったガラスを貫く。1 枚の Blend 面を通る光の
  RGB 透過率は **`T = (1-α) + α·transmission·avg_albedo.rgb`**（`tint *= T`）。透過光は baseColor で濾過される
  という KHR_materials_transmission の物理に合わせ、**transmission=1 でも影は消えず avg_albedo 色に染まる**
  （ステンドグラスの床に色付き光）。α=被覆（`base_color_factor.a`）, tr=透過率。
- 平均アルベドは DDGI 用の per-instance storage（`rt_shadow.rs` 所有・16B/インスタンス・TLAS `custom_data` で引く）を
  **シャドウレイでも読めるように group4 に binding14 を追加**（`rt_shadow_on.wgsl` 宣言＋`lighting.rs::create_rt_bind_group`
  で bind）＝**キャッシュ昇格不要**。α と transmission は GI/反射が読まない `.a` に **各 8bit 固定小数でビットパック**
  して相乗りさせる（`.a = round(α*255)*256 + round(tr*255)`。`.rgb` は生アルベドのまま＝GI/反射に不干渉）。
  パック/デコードは Rust `rt_shadow.rs::pack_shadow_alpha_transmission` と WGSL `rt_trace_translucent_tint` の対で、
  往復＋真理値表テスト（`shadow_pack_roundtrip_and_transmittance`）と WGSL 定数一致で担保する。
- `rt_shadow_factor` の戻り値を **f32 → vec3<f32>（RGB 透過率）**へ変更。`rt_shadow_off.wgsl` スタブも一致
  （`vec3(1)`）。`lighting_eval.wgsl` は `radiance = radiance * factor`（vec3×vec3）で変更不要。deferred/forward の
  全 RT バリアントに波及（naga 全バリアント検証で担保）。スカラー可視性（不透明の遮蔽平均）× ターミネータ ×
  透過色（中心方向 L に 1 本、コスト有界）で合成。
- **注意**: 色付き影は shadow=rt のときだけ効く（シャドウマップは二値で色を持てない）。translucency=rt かつ
  shadow=shadowmap では色付き影は不発（屈折のみ）。`[SEED FEATURES]` に注記。

### B. 屈折（スクリーンスペース）
- RT 屈折レイは採用しない（ヒット先が平均アルベドのベタ塗りになるため）。**不透明シーン HDR のコピーを
  IOR ベースのオフセットでサンプルする Screen-Space Refraction** をフォワード半透明（距離ソート／WBOIT 両方）に入れる。
- 反射パスの read/write 分離を流用: scene_hdr を `refract_bg`（RtPool・別 RT）へ `copy_texture_to_texture` してから
  半透明フラグメントが読む（同一 scene_hdr の読み書き競合回避）。RtPool の usage に COPY_SRC|COPY_DST を追加。
  コピーは**不透明ライティング＋反射完成後・メインパス再開前**に置く（deferred 前提。skybox はメインパス描画のため
  背景に含まれない＝既知の制限）。
- Material に `ior: f32`（既定 1.0）を追加。`MaterialUniform`（group2）の旧 `_pad`(offset60) を転用（64B 不変）。
  loader（glTF の `Material::ior()` は現行 gltf クレートに無いため既定 1.0。`.mat`／インライン上書き／Inspector で設定）・
  `.mat`・`MaterialOverride::Inline`・ACTOR_COMPONENTS・Inspector（**AlphaMode=Blend のときだけ表示**）へ配線。
  Material がキャッシュに焼かれるため **`CACHE_FORMAT_VERSION` 9→10**。
- **合成式**（gate off ＝屈折ビット未設定時は従来の見た目に一致するよう設計）:
  - 距離ソート: 専用 fragment `fs_transparent_sorted` が **premultiplied over**（新 blend `PremultipliedAlpha`）で出力。
    屈折オフ→`(lit*a, a)`（straight AlphaBlending と数学的に等価＝Raster パリティ）。屈折オン→
    `out.rgb = lit*a + bg*tint*(1-a)`, `out.a = 1`（背景をフラグメントで自前合成し置換）。`tint = base_color.rgb`（色付きガラス）。
  - WBOIT: 屈折オフ→従来（`accum=(lit*a, a)*w`, `reveal=a`）。屈折オン→`premult = lit*a + bg*tint*(1-a)`,
    実効被覆 `a_eff=1`（`accum=(premult, 1)*w`, `reveal=1`）→ 合成で背景を確定表示。
  - 背景 UV は法線のビュー空間傾き × `strength=1-1/ior` × 上限比率でオフセット。画面外は素の UV へフェード。
- **バインドグループ**（`max_bind_groups=5` 厳守）: 透明パイプラインの group4 を「lights（0〜13）＋屈折背景
  （15=tex/16=sampler）」の superset にした（`refract_common.wgsl` を透明シェーダにのみ連結）。frame_renderer が
  `LightBuffer::create_transparent_bind_group` で毎フレーム透明用 group4 BG を生成（メイン＝実背景 or ダミー、
  プレビュー＝ダミー・屈折ビット 0）。屈折オフのフレームは**ダミー 1x1 を bind**するため透明描画は常に成立
  （Raster 既定でも壊れない）。

### C. resolve / needs_tlas / UI
- resolve: `Rt → (rt非対応) → Raster`（色付き影が RT 前提）。`needs_tlas()` は translucency==Rt で true（一般化済み）。
- UI: 半透明コンボの「（未実装）」を撤去。IOR は Inspector のマテリアル編集（Blend のみ表示）。

### 検証（実装時点）
- `cargo test --bin SEED`: 107 passed（+2: `render_features` の translucency resolve 降格・`lighting` の WGSL LightMeta
  naga サイズ照合。既存の cull mask テストへ 0x02=`RT_MASK_NON_OPAQUE` の照合、log_line テストを実装済みへ更新）。
- 全フラグメントバリアント（mesh/skinned × RT on/off・deferred on/off・WBOIT・距離ソート）＋屈折 refract_common・
  色付き影 binding14 の WGSL 連結が naga validate を通過。
- `cargo build` / `cd editor && dotnet build` ともに 0 エラー。
- **未検証（実機 GPU 無し）**: 実レンダリング結果（色付き影の染まり・屈折の歪み）・バインドグループの実行時整合・
  fps はスモークテストで確認すること。**監督者スモーク**: translucency=rt で従来シーン（Raster 相当・非 Blend）が
  壊れないこと（ダミー背景 bind ＋ premultiplied パリティで担保）。

---

## フェーズ ガラス表現（透過率の分離 ＋ すりガラス） 【状況: 実装済み（実機検証待ち）】

RT-Translucency の屈折を土台に、「本物のガラス」を作れる 2 機能を追加した。
`Material` に `transmission: f32`（0..1, 既定 0.0）を追加し、`MaterialUniform` を 64→80 バイトへ拡張
（offset 64。`layout_tests::material_uniform_layout_is_80_bytes` で固定）。`CACHE_FORMAT_VERSION` 10→11。

### A. transmission（透過率）— アルファとの分離
- 従来は Blend のアルファが「カバレッジ（被覆）」と「透過（透け具合）」を兼ねていたため、
  「ハイライトは強いのに向こうがよく透ける」ガラスが作れなかった。transmission でこれを分離する。
- 配線は ior と完全に同一経路: `model.rs`(serde default 0.0) / `.mat`(material_asset) /
  `MaterialOverride::Inline` / Inspector（**Blend 選択時のみ表示**） / `MaterialUniform` /
  ACTOR_COMPONENTS / **in-place 更新経路（bake_inline_material / build_material_uniform）**。
  ドラッグ編集も write_buffer 1 回で即反映（uniform 経由）。
- **glTF**: gltf クレート 1.4.1 の `KHR_materials_transmission` 機能を `runtime/Cargo.toml` の features に
  追加し、`Material::transmission().map(|t| t.transmission_factor())` で読む（拡張が無ければ 0.0）。
  ※ ior は依然 gltf 側では読まない（`KHR_materials_ior` feature 未追加。既存踏襲）。
- **合成式**（`refract_common.wgsl::glass_composite`。距離ソート `fs_transparent_sorted` と WBOIT `fs_wboit` が共有）:
  - 屈折オフ（背景コピー無し）: `premult=c*a`, `a_eff=a*(1-transmission)`。透過率で被覆を下げて背後を見せる
    （固定関数ブレンドでは dst を色付けできないため、色付き透過は屈折経路のみ）。
  - 屈折オン: 従来式 `rgb0 = c*a + bg*tint*(1-a)`（transmission=0 の端点）と、フレネル配分した
    透過端点 `rgb1 = c + bg*tint*(1-F)`（表面ハイライト c を被覆として残し、背景をガラス色で色付けして
    (1-F) 分だけ透過）を `mix(rgb0, rgb1, transmission)` で補間する。
  - **後方互換（transmission=0 でビット一致）**: transmission==0 のとき early-return で従来式そのものを返す
    （屈折オフ→`(c*a,a)`、屈折オン→`(rgb0,1)`）。`a*(1-0)=a`・`c*a` 不変・`mix(x,y,0)=x` の恒等性で
    ビット一致を担保（WGSL naga 検証済み。従来の RT-Translucency パリティを保存）。
- **RT 色付き影への反映**: 影の透過率 = **`T = (1-α) + α·transmission·avg_albedo.rgb`**（`rt_shadow_on.wgsl`）。
  透過光は baseColor で濾過されるという物理（KHR_materials_transmission）に合わせ、`transmission=1` でも
  影は消えず avg_albedo 色に染まる。**挙動変更**: 旧モデル（`.a = α·(1-tr)`, `tint *= mix(白, .rgb, .a)`）では
  `transmission=1` で影が完全に消え、`α=1,tr=0` はアルベド色の影だった。新モデルは `α=1,tr=1 → 影=アルベド色`、
  **`α=1,tr=0 → 影=0（暗い影。透過しない被覆面は光を通さない。意図的な挙動変更）`**、`α=0 → 影なし`。
  α（`base_color_factor.a`）と transmission は GI/反射が読まない avg_albedo の `.a` に各 8bit 固定小数でパック
  （`rt_shadow.rs::pack_shadow_alpha_transmission`）。`.rgb` は生アルベドのままで GI compute（`ddgi_probe_update.wgsl`）
  ・RT リフレクション（`reflection_rt.wgsl`）は `.rgb` のみ参照するため両者は不変。`GpuModel.transmissions` を
  materials 同順で保持し `primitive_transmission()` で引く（in-place 編集は次の TLAS 再構築で反映）。

### B. すりガラス（roughness 連動の屈折ぼかし）
- 屈折背景をミップチェーン化し、下位ミップほど強くぼかす。屈折サンプル時に roughness からミップレベルを選ぶ
  （`refract_common.wgsl::refract_background` の `textureSampleLevel(t, s, uv, mip)`。roughness 0=mip0=シャープ、
  roughness 高=深いミップ=すりガラス）。サンプラーは mipmap_filter=Linear（トライリニア＝ミップ境界の滑らか化）。
- **実体**: `renderer/refract_pyramid.rs`（`RefractPyramid`）。旧 RtPool 単一 RT（`refract_bg`）を撤去し置換。
  mip0 = 不透明シーン HDR のコピー。mip m (1..N) = mip(m-1) を 13-tap（ブルーム共用 `post_bloom_down`）で
  1/2 ダウンサンプル → **いもす法ボックスブラー**（`imos_blur.rs`。ao.rs の raw/a/b と同 usage 分離で
  ping-pong）→ 結果を copy_texture_to_texture でミップへ書き戻す。ダウンサンプル＋ブラー累積で下位ほど強くぼける。
- **名前付き定数**: `REFRACT_MIP_COUNT=5`（refract_pyramid.rs）／WGSL `REFRACT_MAX_MIP=4.0`
  （= MIP_COUNT-1。`wgsl_max_mip_matches_rust_mip_count` テストで整合固定）。ブラー半径 `REFRACT_BLUR_RADIUS=2`・
  反復 `REFRACT_BLUR_ITERATIONS=2`。roughness→ミップは線形 `mip = roughness * REFRACT_MAX_MIP`（GGX の視覚ぼけ量が
  roughness に概ね比例するため）。
- **VRAM 増分（概算, Rgba16Float=8B/px, 1920×1080）**: ミップチェーン ≈1.33·WH·8B≈22MB、スクラッチ
  3 枚×Σ(WH/4^m, m=1..4)≈3·0.33·WH·8B≈16.5MB。合計 ≈38.5MB。旧 refract_bg 単一（≈16.6MB）比で **+約22MB**
  （解像度に比例。translucency=rt かつ deferred かつ半透明ありのフレームのみ確保＝それ以外は 0）。
- ゲートは既存の屈折と同一（translucency=Rt かつ deferred 有効のフレームのみ。`refract_active`）。

### 検証（実装時点）
- `cargo build` 0 エラー / `cargo test --bin SEED` **129 passed**（+3: material_uniform 80B レイアウト・
  すりガラス mip 整合・max_mips）。`cd editor && dotnet build` 0 エラー。transmission=0 のビット一致は
  glass_composite の early-return ＋ WGSL naga 検証で担保。
- **未検証（実機 GPU 無し）**: すりガラスのミップ生成（ダウンサンプル→いもす→書き戻しの実描画）・
  roughness 連動ぼけ・透過率の見た目・色付き影の明るさ変化・VRAM 実測は実機スモークで確認すること。

### C. 本物の RT 屈折（TLAS 屈折レイ・スクリーンスペースはフォールバックへ降格） 【状況: 実装済み（実機検証待ち）】
スクリーンスペース屈折（IOR オフセットで不透明背景コピーをサンプル）の原理的限界
（(a) ガラス越しのガラスが映らない (b) 自己屈折＝厚みの屈折がない (c) 視差が不正確）を、
**TLAS への本物の屈折レイ**で解消する（ユーザー決定・正攻法）。SS 版は非対応 GPU／`translucency≠Rt` の
**フォールバックとして温存**する（完全同一経路）。

- **背景取得の 2 方式を差し替え可能に分離**（`rt_shadow_tint_avg` / `_bindless` と同じ排他分離方式）:
  `refract_common.wgsl` が方式非依存の共有部（背景バインディング 15/16・すりガラスのミップ選択・
  `glass_composite`）を持ち、`glass_composite` は背景取得 `refract_sample_bg(surf, frag_xy, ior)` を呼ぶだけ。
  実装は連結される **どちらか 1 本**が供給する:
  - `refract_ss.wgsl`: スクリーンスペース屈折（従来の `refract_background` を移設。フォールバック）。
  - `refract_rt.wgsl`: TLAS 屈折レイ（本物）。group4 に **TLAS(binding6)＋平均アルベド(binding14)** を追加宣言する。
- **RT トレースアルゴリズム**（`refract_rt.wgsl::refract_sample_bg`。**最大 4 界面 + 1 不透明 = 5 レイ/px** で有界）:
  1. **入射屈折**: シェーディング表面で `refract(-V, N, 1/ior)`（**表面 N は本物**＝一次屈折は正確）。
     全反射（refract が 0 ベクトル）なら鏡面反射方向へ切り替え。
  2. **界面トレースループ**（半透明マスク `0x02` のみ・最近ヒット・最大 `REFRACT_MAX_INTERFACES=4`）:
     入射面（`front_face`）でだけ界面色 `T=(1-α)+α·tr·albedo`（色付き影と同一の透過モデル・平均アルベド storage を
     `custom_data` で引く）を `tint` へ乗算。裏面（出射面）は掛けない（媒質 1 個につき 1 回＝二重計上防止）。
  3. **最終背景**: 現在の方向で不透明（`0x01`）を最近トレース → ヒット点を `view_proj` で画面へ射影 →
     **画面内なら `refract_bg`（不透明のみのコピー）をその UV で roughness 連動サンプル**（すりガラス維持）。
     画面外 or 不透明ミスは **DDGI プローブ照度（GI 有効時）or フラットアンビエント**（`evaluate_gi_ambient`・
     反射 RT の fallback と同じ分岐）。深度一致チェックは不要（`refract_bg` は不透明のみのコピーで遮蔽関係が単純）。
  4. 背景の光は貫いた界面群の `tint` でフィルタ（ガラス越しのガラス・厚み色）。シェーディング面自身の色は
     `glass_composite` の `base_color_factor` で別途乗る（二重計上しない）。
- **パイプライン**: RT 対応 GPU（`rt_shadows_supported()`）でのみ RT 透明バリアント一式（`transparent_mesh_rt` /
  `transparent_skinned_rt` ＋ WBOIT RT）を生成し `TransparentPipelines.rt: Option<..>` に保持
  （`RtMeshPipelines` と同流儀）。距離ソート／WBOIT 両対応（`glass_composite` 共有）。group4 は
  「ライト＋シャドウ＋クラスタ＋DDGI＋TLAS＋アルベド＋屈折背景」の superset（`create_transparent_rt_bind_group`）。
  影経路は従来どおり `rt_shadow_off`（シャドウマップのみ）＝**今回のスコープは屈折のみ**（透明パスの受光影は不変）。
- **TLAS ライフタイム**: `needs_tlas()` は既に `translucency==Rt` を含むため、メインパス前に TLAS 構築済み保証。
- **ゲート／コスト**: `translucency=Rt` かつ RT 対応かつ屈折背景アクティブ（`refract_active`）のときだけ RT 版で描く。
  非対応 GPU・`translucency≠Rt` は `rt=None` で SS 版へ完全フォールバック（追加コストゼロ）。屈折レイは
  `material_refracts` の半透明ピクセルのみ。

#### 界面ごとの本物の再屈折（バインドレス法線メガバッファ・2026-07 実装）
- **後続界面も実屈折する**: inline ray query は committed intersection の `primitive_index`＋`barycentrics`＋
  `instance_custom_data` を返す。これを使い、group4 の追加バインディング（instance_table 17／index メガ
  バッファ 18／**法線メガバッファ** 19＝すべて `bindless.rs` 所有の storage）から**ヒット三角形の補間法線を
  オブジェクト空間で復元**し、`hit.world_to_object` の線形部の**転置**でワールド法線化する（逆転置＝非一様
  スケール対応）。front_face（空気→ガラス, eta=1/ior）／back_face（ガラス→空気, eta=ior）で `refract()` を
  界面ごとに適用する。**ガラス越しのガラスが正しく屈折して重なる**（多重界面の再屈折を実体化）。
  - 法線メガバッファは UV/index と同じメガアロケータ方式。**八面体エンコード u32（4B/頂点）**で省メモリ化
    （容量 32MB＝UV 64MB の半分＝同じ頂点数）。エンコード/デコードは Rust/WGSL 両方に実装し往復一致をテスト。
  - `BindlessInstanceRecord` は 64B のまま（旧 `_pad[3]` の 1 枠を `normal_offset` に転用）。ジオメトリ（法線）
    登録済みは `BINDLESS_FLAG_GEOM`（テクスチャ有無に非依存）で示し、屈折シェーダはこのビットで法線復元可否を判定。
  - **一次屈折の非二重化**: 原点を法線側へ押し出す都合で最初に踏む「自分の表面（i==0 かつ front_face）」は
    shading 面 N で処理済みの入射屈折なので**再屈折しない**（`do_refract = (i>0) || !front_face`）。
  - **配線はバインドレス対応 GPU 限定**: 3 バッファは storage（uniform 同居可）だが実体は `BindlessResources`
    が確保するため、RT 屈折パイプラインは **RAY_QUERY ＋ バインドレス両対応**時のみ構築（非対応は SS 屈折へ
    フォールバック。実 GPU では RT 対応＝ほぼ常にバインドレス対応のため実害は理論上のみ）。
- **限界（正直に記す）**:
  - **界面の ior は自身の ior で近似**: インスタンス個別 ior はレコードに無いため、シェーディング面の
    `material.ior` を全界面に流用する。レコードに ior を積めば界面ごとに正確化できる拡張余地。
  - **全反射（TIR）は直進**: `refract` が 0 ベクトルを返す界面では方向を更新せず直進する（レイ反転や同一面
    ループ回避。入射面の一次屈折は shading 面 N で別処理済み）。
  - **Mask のアルファ抜きは未対応**: テクスチャ実サンプルには `binding_array`（group3 の色付き影方式）が要るが、
    透明 group4 は uniform を含み `binding_array` と同居不可・グループ上限 5 も満杯のため、界面のアルファ抜きは
    現状の平均色ベール維持（`refract_layer_tint`・front_face のみ）。
  - **背景は不透明のみのコピー**: レイが最終的に当たる不透明面は「画面内に写っていれば」正しく引ける。
    画面外／不透明ミスは DDGI（無効ならアンビエント）で近似する。

### 検証（本物の RT 屈折・実装時点）
- `cargo build` 0 エラー / `cargo test` 0 失敗。naga 全バリアント通過（SS 3＝`Capabilities::empty`／
  **RT 3＝`Capabilities::RAY_QUERY`**＝`sorted_mesh_rt`・`wboit_mesh_rt`・`wboit_skinned_rt`）＋屈折 RT 定数の
  Rust/WGSL 一致（マスク 0x01/0x02・パック 255/256・界面上限 4）。
- **要実機確認**: ガラス越しのガラスの再屈折・厚みのある屈折・すりガラス維持・非対応 GPU の SS 縮退。
  八面体法線の往復一致は Rust 単体テスト（`bindless::tests::oct_roundtrip_*`）で担保済み。

### 将来課題（ユーザーと合意済みの積み残し）
- **界面 ior の正確化**: `BindlessInstanceRecord` に ior を足し、`custom_data` から界面ごとの実 ior を引いて
  再屈折する（現状は shading 面の `material.ior` を全界面に流用）。※界面法線の復元は実装済み（上記）。
- **界面のアルファ抜き（Mask）**: 透明 group4 に `binding_array` を持ち込めれば界面のテクスチャ α で抜ける
  （現状はグループ制約で不可＝平均色ベール維持）。group4 の uniform 分離 or グループ再編が要る。
- **拡散透過（diffuse transmission）〔実装済み〕**: 葉・布・紙の逆光透け（`KHR_materials_diffuse_transmission`
  相当の簡易版）。`Material.diffuse_transmission`（0..1・既定 0）を追加し、`lighting_eval.wgsl` の
  evaluate_lighting のライトループ内で、面がライトに背を向けている側の逆光を
  `radiance × saturate(-dot(N,L)) × diffuse_transmission × albedo / PI` として加算する（透過色は base_color を
  流用）。**幾何ゲート（geo_gate）も影も掛けない**（裏からの光が本体であり geo_gate は目的が真逆／薄物の自己遮蔽で
  逆光透けが潰れるのを避けるため。手前の別遮蔽物の影が透けに落ちない限界は許容）。スポット円錐・距離減衰は radiance に
  含まれるため効く。forward / deferred 両対応（deferred は G-Buffer RT2.b に格納）。配線は ior/transmission と同経路
  （model.rs・.mat・Inline・MaterialUniform offset 72＝旧 _pad1・CACHE_FORMAT_VERSION 13→14）。Inspector は
  「拡散透過」スライダーを **AlphaMode に関わらず常時表示**（葉=Mask・布=Opaque でも使うため。ガラス系 ior/透過率が
  Blend 限定なのとは意図的に異なる）。glTF 拡張は gltf 1.4.1 クレートに未対応のため glTF ロードでは既定 0（.mat/Inline で設定）。
- **スクリーンスペース SSS（肌）**: サブサーフェススキャタリングのスクリーンスペース近似。
  本フェーズで作った**いもす法ブラー基盤（可変半径・分離ボックス）がそのまま適用可能**（拡散プロファイル
  のぼかしに転用）。透過率／すりガラスのミップ生成と同じパイプライン部品を再利用する想定。

---

## レンダリング機能マトリクス（機能 × モード）

2026-07 追加。RT-Shadow / RT-GI / RT-Reflection / RT-AO / RT-Translucency を**機能ごとに独立して
モード選択**できるフレームワーク。各機能は「レイトレ実装」と「代替（スクリーンスペース系／ラスタ／なし）」を
モードとして持つ。RT 非対応 GPU や未実装モードは**一箇所**（`RenderFeatures::resolve`）で代替へ自動降格する。

正典コード: `runtime/src/engine/core/renderer/render_features.rs`。

### モード一覧と実装状況

| 機能 | enum | モード（文字列） | 既定 | 実装状況 |
|------|------|------------------|------|----------|
| 影 | `ShadowMode` | `rt` / `shadowmap` | `shadowmap` | 両方実装済み（LightMeta.rt_shadows で実行時分岐） |
| GI | `GiMode` | `rt` / `ssgi` / `off`(=`flat`) | `flat`※ | **3 方式すべて実装済み**（`rt`＝DDGI / `ssgi`＝スクリーンスペース GI / `flat`＝フラットアンビエント）。`rt`＝プローブ格子レイトレ（RT 対応 GPU）、`ssgi`＝1 フレーム遅延の半解像度 SS-GI（RT 不要・deferred 有効時のみ）。RT 非対応で `rt` 要求時は `ssgi` へ降格。強度は `gi_intensity`（DDGI と共通） |
| 反射 | `ReflectionMode` | `rt` / `ssr` / `off` | `off` | **実装済み（SSR / RT, Phase D6）**。`rt`＝RT 反射（RT 対応 GPU）、`ssr`＝スクリーンスペース反射。RT 非対応で `rt` 要求時は `ssr` へ降格。deferred 有効時のみ動作 |
| AO | `AoMode` | `rt` / `ssao` / `off` | `off` | **実装済み（SSAO / RT-AO, Phase D4）**。`rt`＝RT-AO（RT 対応 GPU）、`ssao`＝SSAO。RT 非対応で `rt` 要求時は `ssao` へ降格。deferred 有効時のみ動作。強度は `ao_intensity` |
| 半透明 | `TranslucencyMode` | `rt` / `raster` | `raster` | **実装済み（Phase RT-Translucency）**。`raster`＝従来 WBOIT/距離ソート。`rt`＝高品質半透明パッケージ＝**色付き影[RT シャドウレイ]＋屈折[スクリーンスペース]**。RT 非対応で `rt` 要求時は `raster` へ降格（色付き影が RT 前提のため）。屈折は deferred 有効時のみ動作（背景コピーの都合） |

※ `GiMode` の型既定は `flat` だが、**旧 `GiSettings.enabled` の既定は true** だったため、
プロジェクト設定に `gi_enabled` キーが無い場合は移行時に `rt`（GI 有効）へ写像し、現状の見た目を維持する
（`app_init.rs::load_graphics_settings`）。エディタの GI コンボは「なし（環境光）」`flat`／「SSGI」`ssgi`／「レイトレ（DDGI）」`rt` の 3 択で既定 `rt`（DDGI）。

### 降格・未実装判定の集約点

- `RenderFeatures::resolve(rt_supported) -> ResolvedFeatures` が**唯一の判定入口**。
  - RT 非対応 GPU（`rt_shadow::rt_shadows_supported()==false`）では `shadow=rt→shadowmap` / `gi=rt→ssgi`（SSGI は RT 不要で DDGI の次善。`flat` ではなく間接光を残す）。
  - GI: `rt`＝RT 対応時のみ通す／RT 非対応時は `ssgi` へ降格、`ssgi`＝常に `ssgi`、`flat`＝`flat`（3 方式実装済み）。
    ただし `ssgi` は deferred 有効時のみ動作（deferred ゲートは `frame_renderer` 側 `ssgi_active` で判定。無効時はフラットへ）。
  - 反射: `rt`＝RT 対応時のみ通す／RT 非対応時は `ssr` へ降格、`ssr`＝常に `ssr`、`off`＝`off`（Phase D6 実装済み）。
  - AO: `rt`＝RT 対応時のみ通す／RT 非対応時は `ssao` へ降格、`ssao`＝常に `ssao`、`off`＝`off`（Phase D4 実装済み）。
    ただし `ssao`/`rt` は deferred 有効時のみ動作（deferred ゲートは `frame_renderer` 側 `ao_effective` で判定）。
  - 半透明: `rt`＝RT 対応時のみ通す／RT 非対応時は `raster` へ降格（色付き影が RT 前提のため。屈折だけ欲しいケースの分離は将来課題）、`raster`＝常に `raster`（Phase RT-Translucency 実装済み）。
- 実行時分岐（`frame_renderer.rs` 等）は必ず `ResolvedFeatures` を参照し、生の `RenderFeatures` を直接見ない。
- 起動時・切替時に実効モードを `[SEED FEATURES] shadow=… gi=… reflection=rt … translucency=rt …` の 1 行でログ
  （反射は実装済みのため `(未実装)` は付かない。RT 非対応で SSR 降格時のみ `reflection=ssr(rt非対応→ssr)`。
  RT 非対応で GI が SSGI 降格時は `gi=ssgi(rt非対応→ssgi)`。反射要求ありで deferred 無効なら
  `反射:deferred無効のため停止`、実効 GI が `ssgi` で deferred 無効なら `GI(SSGI):deferred無効のため停止` を追記）
  - 半透明も実装済みのため `(未実装)` は付かない。RT 非対応で降格時のみ `translucency=raster(rt非対応→raster)`。
    Rt が通っても影が RT でない（shadow≠rt）ときは `translucency=rt(影=rt時のみ色付き影/屈折のみ)` と注記する
    （シャドウマップは二値で色を持てないため色付き影は不発。屈折はスクリーンスペースなので影の方式に依らず有効）。

### D6 反射 実機テスト観点（GPU 実機確認が必要。開発環境では cargo build/test まで）
- SSR/RT を切り替えると鏡面（低 roughness）の床・金属に反射像が出ること。
- roughness を上げると 0.30→0.55 で反射がフェードして消えること。
- `deferred` を OFF にすると反射が完全に消えること（反射は G-Buffer 有効時のみ動く独立パス）。
- RT 非対応 GPU で `rt` を選ぶと自動で SSR が動くこと（`reflection=ssr(rt非対応→ssr)` ログ）。
- `reflection_intensity` を変えると反射の強さがスケールすること。
- SSR は画面外・裏面ヒットでミスし、GI 有効時は粗い環境反射、無効時は反射なし（黒＝加算無害）になること。
  （`App::log_render_features_if_changed`、変化時のみ・重複抑制）。

### TLAS 構築ゲートの一般化

`ResolvedFeatures::needs_tlas()` は「解決後モードのいずれかが `rt` か」を返す。`frame_renderer.rs` の
TLAS 構築は `draw_ctx.rt_shadow.is_some() && resolved.needs_tlas()` の 1 判定に集約されており、
将来 Reflection/AO/Translucency の `rt` が resolve を通るようになれば、**ゲート側を触らずに** TLAS が構築される。

### IPC / 旧キー互換

- 新キー: `SET_POST_FX:{…,"features":{"shadow":"…","gi":"…","reflection":"…","ao":"…","translucency":"…"}}`。
  欠落キーは serde default（各 enum の Default）で埋まる。
- 旧キー互換:
  - `RT_SHADOWS:1/0`（IPC コマンド）→ `render_features.shadow` を `rt`/`shadowmap` に写像。
  - `SET_POST_FX` の旧 `gi_enabled`（bool）→ `features` が無いときだけ `render_features.gi` を `rt`/`flat` に写像。
  - プロジェクト設定 `project_settings.json` の `rt_shadows` / `gi_enabled` は読み側で `RenderFeatures` へ変換（ファイルは不変）。
- `GiSettings` は**数値パラメータ専用**（強度／プローブ数／レイ数／ヒステリシス／再帰重み）に縮小。有効/無効は `GiMode` へ移行。

### エディタ UI

ビューポート設定（ギア）ポップアップの「レンダリング機能」セクションに 5 コンボ（影/GI/反射/AO/半透明）。
`OnRenderFeatureChanged` → `SendPostFx()` が `features` を組んで送る。「（未実装）」項目は選択可（選ぶと従来動作＋ログ）。
実装されたらラベルの「（未実装）」を外すだけでよい。

### 今後の拡張手順（新モード追加 / 未実装モードの実体化で触る箇所）

**A. 新しいモードを enum に追加する（例: `GiMode::Ssgi`）**
1. `render_features.rs`: 対象 enum にバリアント追加（serde 文字列は小文字）＋ `mode_str_*` の match 追加。
2. `render_features.rs::resolve`: そのモードをどう解決するか（対応可否・降格先）を該当腕に記述。
3. `render_features.rs::ResolvedFeatures::needs_tlas`: RT を要するモードなら OR 条件に追加。
4. エディタ `MainWindow.xaml`: 対象コンボに `<ComboBoxItem>` を追加（Tag＝小文字文字列）。
5. ユニットテスト（serde 往復・resolve 降格）を追加。

**B. 未実装モードの実体を実装する（例: SSR / RT-Reflection）**
1. `render_features.rs::resolve`: 該当機能の腕を「フォールバック固定」から「`rt_supported` 条件で通す／Ssr は常に通す」へ変更。
2. `frame_renderer.rs`: `resolved.reflection` 等を参照する描画パス（SSR パス／RT レイ発射）を追加。
   TLAS が要るモードなら `needs_tlas()` が自動で true になるため、TLAS 構築ゲートは触らなくてよい。
3. 必要な GPU リソース（G-Buffer 追加ターゲット・パイプライン・BindGroup）を用意。
4. エディタ: 対象コンボのラベルから「（未実装）」を外す。
5. `[SEED FEATURES]` の未実装注記（`log_line`）は resolve が実効モードを返すようになれば自動で消える。

### 検証（実装時点）

- `cargo test`: 87 passed（+6: `render_features` の serde 往復・旧キー欠落既定・resolve 降格・needs_tlas・log_line 注記）。
- `cargo build` / `cd editor && dotnet build` ともに 0 エラー。
- **未検証（実機 GPU 無し）**: `[SEED FEATURES]` ログの実値・従来動作の維持はスモークテストで確認すること。

---

## フェーズ B: バインドレス基盤（RT ヒットシェーディングの土台）【状況: B1・B2・B3 実装済み（実機検証待ち）】

### 背景・目的

inline RT のヒットシェーディング（色付き影・DDGI・RT 反射）は現在「プリミティブ平均色」の
近似に留まっている。根本原因は「ヒットした三角形のテクスチャ・頂点属性（UV）を GPU 側で引けない」
こと。マテリアルごとに BindGroup を差し替える従来モデルでは、レイがどのプリミティブに当たったかを
実行時に解決してそのテクスチャ／UV を引く手段が無い。これを解消する土台が**バインドレス**である。

方針は 3 段階（各段で見た目を壊さない・非対応 GPU は従来経路を維持）:

- **B1（本フェーズ・実装済み）**: 基盤の確保・API・充填のみ。**消費側は無し＝見た目は一切変わらない**。
- **B2（実装済み）**: 消費側の配線＋ライブ充填。RT 反射パス（reflection_rt）の専用グループ
  （group3）へ「インスタンステーブル / UV / index / テクスチャ配列 / サンプラー」を追加し、
  ヒット先のベースカラーを **UV でテクスチャサンプルした本物の色**にする（詳細は下記「B2 の実装内容」）。
  raster 連結（group4）への追加は B3（色付き影）に委譲。
- **B3（実装済み）**: 色付き影の tint をヒット点テクスチャの実サンプルへ、Mask（アルファテスト）
  オクルーダの影付け（葉の形の影）、色付き影バインドレス配線（deferred / shadow_mask）。詳細は下記
  「B3 の実装内容」。normal/MR 属性の拡張は将来課題（B3 では albedo のみ）。

### 非対応 GPU フォールバック方針

バインドレスは以下を要求フィーチャー／リミットとする。**いずれか欠ける GPU では基盤を一切確保せず**、
既存の平均色経路が現状どおり動く（B2/B3 でも消費側は「対象外＝ダミー」で縮退する）。

- `Features::TEXTURE_BINDING_ARRAY`（`binding_array<texture_2d>` 宣言）
- `Features::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING`
  （ヒットごとに非一様なインデックスで配列を引く）
- `Limits::max_binding_array_elements_per_shader_stage > 0`（wgpu 25 既定 0＝配列不可）
- `Features::PARTIALLY_BOUND_BINDING_ARRAY` は**任意**（対応時のみ要求）。B1 は全スロットを
  ダミー白で埋めて BindGroup を構築するため、部分バインド非対応でも動く。

対応時は `max_sampled_textures_per_shader_stage` / `max_binding_array_elements_per_shader_stage` を
テクスチャ配列容量（`BINDLESS_MAX_TEXTURES=4096` をアダプタ上限でクランプした値）まで
**アダプタ上限の範囲で**引き上げる。RTX 3060（Vulkan/DX12）では上限が十分大きく 4096 が確保される。
起動ログ `[SEED BINDLESS] 対応/非対応` に確定容量・部分バインド可否を出す。

### B1 の実装内容（renderer/bindless.rs）

- **テクスチャレジストリ**: ベースカラー（albedo）テクスチャに安定インデックスを割り当てる。
  index 0 は 1x1 白ダミー（未登録・解放後の安全弁）。解放スロットはフリーリストで再利用。
  `binding_array<texture_2d<f32>>`（binding0）＋共有サンプラー（binding1・リニア／リピート）を
  1 個の BindGroup にまとめる。まず albedo のみ対象（normal/MR は B3 で別配列を増設できる設計）。
- **メガバッファ方式**（buffer の binding_array は使わない＝GPU 互換・単純さ優先）:
  - `bindless_uv_buffer`（既定 64MB）: 全プリミティブの UV（vec2）を連結。
  - `bindless_index_buffer`（既定 32MB）: 全プリミティブの三角形インデックス（u32）を連結。
  - サブアロケータ `MegaAllocator`（バンプ＋フリーリスト・隣接コアレス）。あふれたら警告ログ＋
    そのプリミティブはバインドレス対象外（ダミー扱い）。断片化でコンパクションは将来課題。
- **インスタンステーブル**（`BindlessInstanceRecord`, 64B, 16B 整列, TLAS custom_data で引く）:
  `avg_albedo`（offset 0・既存 16B と同レイアウト）／`base_color_factor`／`albedo_tex_index`／
  `uv_offset`／`index_offset`／`flags`。WGSL ミラー `shaders/bindless_common.wgsl` と
  Rust `bindless::tests` でレイアウトを照合する。

### 既存の平均アルベド storage との互換（重要）

既存の平均アルベド storage（16B/inst, `array<vec4<f32>>`）は rt_shadow_on.wgsl(group4 b14) /
ddgi_probe_update.wgsl(group0 b4) / reflection_rt.wgsl(group3 b3) の 3 経路が `.rgb`/`.a` を読む。
このストライドを変えると 3 シェーダが無言で壊れるため、**B1 では既存バッファを一切変更せず**、
`BindlessInstanceRecord` を**別個の新バッファ**として確保する（＝ゼロリグレッション・シェーダ変更なし）。
`BindlessInstanceRecord` の先頭 16B は既存と同じ `avg_albedo vec4` にしてあり、B2/B3 で両バッファを
統合する際に先頭 16B をそのまま流用できる。

### B2 が消費する API（renderer::BindlessResources）

- `texture_bind_group_layout()` / `ensure_texture_bind_group()`（テクスチャ配列＋サンプラーの BGL/BG）
- `uv_buffer()` / `index_buffer()` / `instance_table_buffer()`（storage バインド用）
- `register_albedo_texture(view) -> u32` / `free_albedo_texture(idx)`（登録・解放）
- `append_uv(queue, &[[f32;2]]) -> Option<u32>` / `append_indices(queue, &[u32]) -> Option<u32>` /
  `free_uv` / `free_indices`（メガバッファ追記・解放）
- `upload_instance_records(queue, &[BindlessInstanceRecord])`（テーブル一括アップロード）

### バインディング配置（B2 で行う・B1 では触らない）

group4（raster 連結）へ `binding17=テクスチャ配列, 18=サンプラー, 19=UV, 20=index,
21=インスタンステーブル` を予約追加するのは **B2**。B1 は既存パイプラインのバインディングを
一切変えない（`max_bind_groups=5` は不変）。

### 検証（B1 実装時点）

- `cargo test`: 159 passed（+11: レコードのレイアウト照合／WGSL ミラー定数一致・parse／
  アロケータのバンプ・整列パディング・解放再利用・コアレス・あふれ縮退・0 サイズ／
  レジストリの採番・再利用・ダミー0番／インスタンス容量一致／dummy レコード）。
- `cargo build`: 0 エラー。既存の naga シェーダバリアント検証も通過（B1 はシェーダ未連結）。
- **未検証（実機 GPU 無し）**: 起動ログ `[SEED BINDLESS]` の実値・確定容量、非対応 GPU での
  従来経路維持はスモークテストで確認すること（B1 の観測差分は起動ログのみの想定）。

### B1 の割り切り（B2 へ引き継ぐ TODO）

- **ライブ充填の配線は B2 に委譲**。B1 はレジストリ登録／メガバッファ追記／テーブル充填の各 API を
  完成・単体テストし、`BindlessResources` を対応 GPU で確保するところまで。実際にモデルアップロード
  （gpu_resources）・stale prune・TLAS 詰め直し（rt_shadow）へ配線してデータを流し込むのは、
  **消費側バインディングと同時に B2 で行う**。理由: B1 では読み手が無く配線の正しさをスモークで
  検証できないうえ、ホットパス（upload/prune/rt_shadow）へ触れるのはリグレッション面が大きいため、
  「消費と同時に配線して実データで検証する」方が安全（ゼロリグレッション優先）。

### B2 の実装内容（ライブ配線＋RT 反射ヒットのテクスチャサンプル化）

RT 反射のヒット色を「プリミティブ平均色のベタ塗り」から「**ヒット点の UV でベースカラー
テクスチャをサンプルした本物の色 × base_color_factor**」へ差し替えた。B1 で先送りした
登録・充填・解放のライブ配線も本フェーズで実施。**非対応 GPU・縮退時は従来の平均色を維持**。

#### ライフサイクル（登録・解放）— RAII 遅延解放キューを採用
- **設計判断: 明示解放でなく RAII（Drop）**。GpuModel は多数の地点で drop される（MC の
  `Option<GpuModel>` 置換・stale バッチ prune・スロット再割当・シーン再読込・複製・ギズモ）。
  明示解放は全 drop 地点の網羅が必要でリーク源になり、二重解放も招く。そこで登録の対価として
  GpuModel に `BindlessModelAlloc`（RAII ハンドル, `renderer/bindless.rs`）を持たせ、その **Drop で
  解放要求を共有キュー（`Arc<Mutex<Vec<BindlessFree>>>`）へ積む**。GpuModel は Clone 不可＝Drop は
  ちょうど 1 回のため、どこで drop されても **必ず 1 回だけ**解放要求が積まれる（リーク・二重解放が
  構造的に起こらない）。実返却は `BindlessResources::process_pending_frees` が次のモデル登録直前に
  まとめて行う（解放スロット/領域を再利用）。**テスト**: `register_then_drop_drain_balances_no_leak`
  （登録数＝解放数の整合）・`model_alloc_drop_enqueues_exactly_once`（二重解放なし）・
  `model_alloc_empty_or_dummy_drop_is_noop`。
- **登録タイミング**: `DrawContext::upload_model` / `upload_model_with_overrides` が実効マテリアル・
  テクスチャ確定後（apply_overrides の後）に `GpuModel::register_bindless` を 1 回だけ呼ぶ
  （生の構築 `upload_model_raw` と分離し二重登録を回避）。テクスチャ登録は `GpuModel.textures[base_color
  _texture.texture_index].view`、UV/index は CPU `Model` の `prim.vertices[].uv0` / `prim.indices` から
  読む（GpuPrimitive は CPU 配列を保持しないため）。スキンメッシュは RT の BLAS 対象外なので登録スキップ。
- **あふれ縮退**: UV/index メガバッファ満杯時は `register_primitive_geometry` が片側確保分を即解放し
  `eligible=false`（バインドレス対象外＝平均色へ縮退）。テクスチャ満杯はダミー 0 番。
- **保持するインデックス**: `GpuMaterial` に `base_color_factor` / `base_color_tex_index` /
  `bindless_albedo_tex_index`、`GpuPrimitive` に `bindless_uv_offset/uv_count/index_offset/index_count/
  eligible`。in-place マテリアル編集（`update_material_inline`）は `base_color_factor` フィールドも追従。

#### インスタンステーブル充填（rt_shadow）
- `rt_shadow.rs::prepare_and_build` に `bindless: Option<&BindlessResources>` を追加。TLAS 詰め直しの
  **同一ループ・同順（custom_data=inst_count 順）**で `BindlessInstanceRecord` を組んで
  `upload_instance_records`（albedo_buffer と同じ更新周期＝再構築フレームのみ）。既存の平均アルベド
  storage（16B）は併存（DDGI/色付き影/reflection のフォールバックが読むため不変＝ゼロリグレッション）。
- `flags=ELIGIBLE` は「UV/index 登録済み **かつ** albedo テクスチャ登録済み（tex≠0）」のときのみ。
  `avg_albedo`（先頭 16B）は既存 storage と同一（`.a`=色付き影のパック値）でフォールバックに使える。
- **静止スキップ シグネチャ**に新フィールド（`base_color_factor` / `albedo_tex_index` / `uv_offset` /
  `index_offset` / `eligible`）を追加。マテリアルの in-place 編集で instance_table が古いまま固定される
  退行を防ぐ（bindless 有効時のみハッシュに混ぜる）。

#### RT 反射ヒットシェーディング（reflection_rt.wgsl）
- `fs_rt` のヒット色を `rt_hit_base_color(instance_custom_data, primitive_index, barycentrics)` へ委譲。
  naga の `RayIntersection` フィールド名は `instance_custom_data` / `primitive_index` / `barycentrics`
  （vec2, offset28）を naga-25.0.1 `front/type_gen.rs` で裏取り済み。
- **UV 補間**: `bl_index[index_offset + primitive_index*3 + i]` で三角形の頂点番号 →
  `bl_uv[uv_offset + 頂点番号]` で UV3 点 → 重心座標 `(1-x-y, x, y)` で補間 →
  `textureSampleLevel(bl_tex[albedo_tex_index], 共有サンプラー, uv, 0.0) × base_color_factor.rgb`
  （ミップ 0 固定。レイ微分は将来課題）。直接光近似＋DDGI バウンスの構成は従来どおり（アルベドのみ本物化）。
- **フォールバック分岐は 2 段構え**:
  1. **パイプライン/BG レベル（必須）**: 非対応 GPU で `binding_array` を宣言すると BGL 生成が失敗する
     ため、`reflection.rs` が `use_bindless = rt_shadows_supported() && bindless_supported()` で group3
     レイアウトとヒットシェーダ連結（`reflection_rt_hit_on.wgsl` ↔ `reflection_rt_hit_off.wgsl`）を切替。
     非対応 GPU は配列を一切宣言しない従来シェーダのみをロード。
  2. **シェーダ内分岐（縮退）**: 対応 GPU でも `flags` 非 ELIGIBLE・`tex_index=0` のときは平均アルベド
     （`rt_albedo[ai].rgb`）へ縮退。

#### バインディング配置（reflection_rt 専用パイプライン, group ≦5）
- group0=camera / group1=G-Buffer / group2=input / **group3=RT データ（拡張）** / group4=GI（＝5 groups）。
- group3 拡張: `0=lights(storage) 1=meta(storage) 2=tlas(accel) 3=avg_albedo(storage) 4=instance_table(storage)
  5=UV(storage) 6=index(storage) 7=テクスチャ配列(binding_array, count=容量) 8=共有サンプラー`。
- **meta は storage（uniform でない）**: WebGPU 制約「binding_array と uniform buffer は同一 bind group に
  同居不可」のため、meta（LightMetaR, 32B）を read-only storage で読む（レイアウトは std140/std430 一致＝
  値不変。LightBuffer の meta バッファへ STORAGE 用途を追加。他パスの uniform バインドは用途スーパーセットで不変）。
  group3 に uniform は 1 つも無い構成（binding_array と両立）。
- **本数**: フラグメント段の storage buffer=6（lights/meta/albedo/instance_table/UV/index, 上限 12 以内）、
  binding_array 要素=容量（=
  `max_binding_array_elements_per_shader_stage`, ちょうど上限一致で pass）。**テクスチャ配列は
  `max_sampled_textures_per_shader_stage` に計上されない**（wgpu-core 25 `binding_model.rs:345` で
  binding_array 要素は `binding_array_elements` のみに加算）ため G-Buffer/GI テクスチャと競合しない
  → mod.rs のリミット計算は変更不要。

#### 検証
- `cargo build` 0 エラー / `cargo test` **164 passed**（B1 の 160 から +4: RAII 遅延解放の整合 3 本＋
  reflection_rt の bindless-on WGSL parse+validate。binding_array が RAY_QUERY と共存して naga validate を
  `SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING` 付きで通ることを固定）。
- **未検証（実機 GPU 無し・スモークで確認）**: RT 反射に模様（テクスチャ）が映ること・非対応/縮退で
  従来のベタ塗りに戻ること・非一様インデックスの実機動作・VRAM 増（UV 64MB+index 32MB+テーブル
  4096×64B の常設）・性能（ヒットごとの storage/テクスチャフェッチ増分）。

### B3 の実装内容（色付き影のテクスチャ実サンプル＋Mask アルファ抜き影）

RT 色付き影の透過色を「プリミティブ平均アルベド」から「**ヒット点 UV でベースカラーテクスチャを
実サンプルした色 × base_color_factor**」へ差し替え（ステンドグラスの模様がそのまま床の影に落ちる）、
さらに Mask マテリアル（アルファテスト）を**テクスチャ α のアルファテストで影に落とす**（葉の形の影）。
**非対応 GPU・縮退時は従来の平均アルベド経路を維持**（ゼロリグレッション）。

#### バインディング設計（uniform × binding_array 同居問題の解法）
- **問題**: 色付き影は raster 連結（deferred_lighting / shadow_mask）の group4 で動くが、**WebGPU 制約
  「binding_array と uniform buffer は同一 bind group に同居不可」**（B2 で実機クラッシュ済み）により、
  uniform を複数持つ group4（LightMeta b1 / ShadowMatrices b5 / ClusterParams b9 / GiParams b10）に
  binding_array を置けない。
- **解法（採用）**: **uniform を一切含まない新しい group3 にバインドレス資源を隔離する**。deferred /
  shadow_mask は group3 が空き（gap）であり、そこへ `0=instance_table / 1=UV / 2=index /
  3=テクスチャ配列(binding_array) / 4=サンプラー`（`bindless::colored_shadow_bgl`）を置く。group4 の
  uniform 群には binding_array を置かないため制約を構成上満たす（storage 化などの広い改修は不要）。
  → uniform 群の storage 化（候補 a）は影響範囲が大きいため不採用。別グループ隔離（候補 b）を採った。
- **対応パス**: **deferred_lighting_rt と shadow_mask の両方**をバインドレス影対応にした（両者とも
  group3 が空きで、共通の group3 BGL を借用する）。deferred が既定経路・shadow_mask が半解像度の
  事前計算マスクのため、不透明サーフェスへ落ちる影（＝視認できる色付き影・葉形影）は全ライト種で
  カバーされる。**forward（透明パスの影＝mesh_rt/skinned）は group が全て埋まっており空きが無いため
  平均アルベドのまま**（段階的対応・実用上十分）。非対応 GPU は従来の平均アルベド経路。

#### シェーダ分割（tint バリアント）
- `rt_shadow_on.wgsl`（コア）から `rt_trace_translucent_tint` を分離し、2 本の tint バリアントへ:
  - `rt_shadow_tint_avg.wgsl`      : 平均アルベド storage（binding14）で染める従来経路。forward・
    非バインドレスの deferred/shadow_mask が連結。
  - `rt_shadow_tint_bindless.wgsl` : group3 のバインドレス資源でヒット点 UV のベースカラーを実サンプル
    （B2 の hit_on と同じ補間手順）し、透過モデル `T=(1-α)+α·tr·texel.rgb`（α=texel.a×factor.a、
    tr は avg_albedo.a のパック値から復号）。Mask 面は `texel.a×factor.a >= alpha_cutoff` なら
    **完全遮蔽（tint=0 で即終了）**、未満なら**素通り（tmin を進める）**。deferred/shadow_mask の
    バインドレス影バリアントが連結。
  - コアの `rt_shadow_factor` は前方参照で `rt_trace_translucent_tint` を呼ぶ（連結時に tint を 1 本並べる）。

#### インスタンステーブル拡張（BindlessInstanceRecord）
- `alpha_cutoff: f32`（offset 48, Mask 用）と `BINDLESS_FLAG_MASK`（flags のビット 1）を追加（サイズ 64B
  不変・既存フィールドのオフセット不変）。Rust `bindless.rs` ↔ WGSL `bindless_common.wgsl` の
  レイアウト照合テストを更新。rt_shadow.rs が TLAS 詰め直し（custom_data 順）で Mask フラグ＋cutoff を記録。
- `GpuMaterial.alpha_cutoff` を追加（`Material` から複製）。`update_material_inline` が in-place で追従
  （Mask cutoff の実行時編集が影に反映される）。**静止スキップ シグネチャに Mask 判別（Blend/Mask は
  同一 0x02 に潰れるため別途）＋alpha_cutoff を追加**（in-place 編集の TLAS 再構築を確実に発火）。

#### バインドレス影 group3 BindGroup（frame_renderer）
- deferred 有効フレームで RT 対応 かつ バインドレス対応のとき、`create_colored_shadow_bind_group` で
  group3 BG を 1 回組み、shadow_mask 生成パス／deferred ライティングパスの両方で bind（テクスチャ配列は
  毎フレーム全スロットを「登録済み=実 view／空き=ダミー白」で並べる。reflection_rt と同流儀）。
  非対応・縮退時は従来の空 gap（empty_bg3）。**色付き影の実行時トグル `translucency_rt` が OFF なら
  第 2 クエリ自体をスキップ**するため、Mask 影・色付き影とも OFF 時は従来コスト（追加レイ 0）。

#### 検証
- `cargo build` 0 エラー / `cargo test` **169 passed**（B2 の 164 から +5: レコードのレイアウト照合
  更新（alpha_cutoff/MASK フラグ）＋deferred/shadow_mask のバインドレス連結 naga parse+validate
  （binding_array×RAY_QUERY×非一様インデックス）2 本＋ tint 分割の整合。既存の cull_mask/pack
  往復・ソフト影定数整合も維持）。naga 全バリアント（forward avg / deferred avg / deferred bindless /
  shadow_mask avg / shadow_mask bindless）が pass し、**binding_array×uniform の同居が無いことを構成上保証**。
- **未検証（実機 GPU 無し・スモークで確認）**: ステンドグラスの模様が床の影に落ちること・葉っぱの形の
  影が落ちること・非対応 GPU で従来の平均アルベド影に縮退すること・性能（Mask/Blend の第 2 クエリで
  ヒットごとテクスチャフェッチ増）・group3 追加による fragment storage buffer 本数（≈7）が実機リミット内。
