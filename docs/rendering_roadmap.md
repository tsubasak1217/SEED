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
- TODO(R3残): 上記オーバーレイのトーンマップ回避（ポスト後合成）・露出制御(exposure UI)・
  演算子切替 UI・ビネット強度のデータ駆動化。

### Phase R4: ブルーム＋FXAA 【状況: 未着手】
- R3の土台上に。ブルーム（しきい値→ダウンサンプルチェーン→合成）、FXAA（最終段）。
- カメラ/プロジェクト設定でON/OFF・強度をデータドリブンに。

### Phase R5: 透明描画の整備 【状況: 未着手】
- 不透明/透明の描画分離（マテリアルのAlphaMode: Opaque/Mask/Blend で分類）。
- Maskモードのdiscard復活（shader_fragment.wgslのコメントアウト解除＋alpha_cutoff結線）。
- Blendは2方式を切替可能に: (a)距離ソート（後方→前方）、(b)WBOIT（accum/revealage 2RT＋合成パス。R3の土台使用）。
  切替はカメラ or プロジェクト設定。
- 受入: 半透明同士の交差でWBOITが破綻なく、ソート方式では従来型の見た目になる。

### Phase R6: 汎用バッチング（同一形状一括描画） 【状況: 未着手】
- スプライト: 1スプライト=1ドローコール＋毎フレームbuffer/BindGroup生成を撤廃。
  インスタンシング（クアッド1枚×インスタンスバッファ）＋テクスチャは配列 or アトラスで統合。
- 汎用化: 「同一メッシュ形状＋同一パイプライン」を自動でインスタンス束ねる軽量バッチャを
  プリミティブ描画（ライン/ギズモ以外の形状描画）にも適用できる形で設計。
  ※3Dモデルは既存 InstancedModelBatch が担うため対象外（重複実装しない）。
- 受入: スプライト1000枚でドローコールが数個に収まり、フレーム時間が現状比で大幅短縮。

### Phase R7: .matマテリアル＋マルチマテリアル編集 【状況: 未着手】
- .mat（JSON）: base_color/metallic/roughness/emissive/テクスチャパス群/alpha_mode/cutoff。
- ModelComponent: マテリアルスロット一覧（サブメッシュ→マテリアルの対応を表示）、
  スロットごとに「glTF埋込（既定）/.mat割当/インライン上書き」を選択可能に。
- インスペクタ: スロット一覧＋.matのD&D割当＋主要値のインライン編集。ProjectPanelで.mat新規作成。
- 受入: マルチメッシュ/マルチマテリアルのglTFで、特定スロットだけ色や粗さを差し替えられる。

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
- TODO（R8残）: rect/point のソフトシャドウ（面光源の複数サンプル。v1はハード1本）・
  スキンメッシュのRT影（スキン済み頂点からのBLAS毎フレーム再構築）・カメラプレビュー/
  ギズモモデルのRT影（現状は従来パイプライン固定で影を受けない）・実機での視覚検証。

### 継続タスク（全フェーズ共通）
- frame_renderer.rs の該当パスを触るたびにモジュール分割（passes/ サブフォルダへ）。
- 各フェーズでデバッグ表示を拡充（R1: ライトギズモ、R2: カスケード可視化、R5: OITバッファ可視化等）。
- Hi-Zオクルージョンの接続は性能課題が顕在化した時点で独立タスクとして実施（実装済み・接続のみ）。

## 実装順の根拠
R1→R2 は依存関係（影はライトの上に）。R3→R4/R5 も依存（ポスト土台の上にブルーム/WBOIT合成）。
R6/R7 は独立しており、R2とR3の間など任意の位置に差し込み可能（疲労分散・検証待ちの間に実施推奨）。
R8 は影アーキテクチャ確定後かつ実験的API理解が必要なため最後。
