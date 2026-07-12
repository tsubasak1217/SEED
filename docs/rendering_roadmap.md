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
  soft_radius/距離）から l 中心の円錐内へ `RT_SHADOW_SAMPLES=4` 本を Vogel ディスク分布＋フラグメント座標
  由来の IGN 回転（時間項なし＝TAA非前提でちらつき無し）で分散し平均。cone_radius=0 は 1 本ハードへ分岐で高速維持。
  負荷: soft_radius>0 のライトで RT 影レイが 4 倍（品質オプション。サンプル数は定数、可変化は将来）。
  シグネチャは `rt_shadow_off.wgsl` スタブと一致。naga RAY_QUERY テストで全4バリアント再検証済み。
- TODO（R8残）: スキンメッシュのRT影（スキン済み頂点からのBLAS毎フレーム再構築）・カメラプレビュー/
  ギズモモデルのRT影（現状は従来パイプライン固定で影を受けない）・ソフト影サンプル数の SET_POST_FX 可変化・実機での視覚検証。

### ブラー系実装の方針（ユーザー指定・必読）
今後ガウシアンフィルタ相当の処理（ブラー・被写界深度・大半径ブルーム等）を実装する際は、
**いもす法（累積和）の応用**（https://imoz.jp/algorithms/imos_method.html）を優先検討すること。
累積和→ボックスフィルタは半径非依存の O(n) で、ボックスフィルタ3回反復でガウシアンを高精度に
近似できる（分離可能: 水平→垂直）。GPU実装は行/列prefix sum（compute）＋等幅区間和。
大カーネルほどタップ数固定の従来法より高速。

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

### 継続タスク（全フェーズ共通）
- frame_renderer.rs の該当パスを触るたびにモジュール分割（passes/ サブフォルダへ）。
- 各フェーズでデバッグ表示を拡充（R1: ライトギズモ、R2: カスケード可視化、R5: OITバッファ可視化等）。
- Hi-Zオクルージョンの接続は性能課題が顕在化した時点で独立タスクとして実施（実装済み・接続のみ）。

## 実装順の根拠
R1→R2 は依存関係（影はライトの上に）。R3→R4/R5 も依存（ポスト土台の上にブルーム/WBOIT合成）。
R6/R7 は独立しており、R2とR3の間など任意の位置に差し込み可能（疲労分散・検証待ちの間に実施推奨）。
R8 は影アーキテクチャ確定後かつ実験的API理解が必要なため最後。
