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
  （1024×4レイヤ, `SPOT_SHADOW_SIZE=1024`/`MAX_SHADOW_SPOTS=4`）。group5=深度配列×2＋比較サンプラー
  （LessEqual）＋`ShadowMatricesUbo`（cascade_vp×3＋spot_vp×4＋分割距離＋params）。
- シャドウパス: 死蔵の depth_prepass.wgsl を流用（`ShadowDepthPipelines`, `shadow_depth_*.toml`）。
  skin compute 後・メインパス直前に各カスケード/スポットレイヤへ深度専用描画。
  シャドウ用 view-proj はレイヤごとに専用 `CameraBuffer`（group0）へアップロード。
- CSM: practical split（`CSM_SPLIT_LAMBDA=0.5`）＋バウンディング球タイト正射＋テクセルスナップ。
- シェーディング: `shadow.wgsl`（group5）で方向光=カスケード選択→PCF3x3、スポット=PCF3x3。
  slope-scaled 深度バイアス（`shadow_depth_*.toml` の `depth_bias_*`）＋シェーダ定数バイアス併用。
  影付きは「最初の cast_shadows=true な方向光1灯」＋スポット最大4。`GpuLight.shadow_index` で結線。
- cast_shadows: `ModelComponent.cast_shadows`（既定true, インスペクタ「影を落とす」チェック）。
  粒度は共有バッチ（source_path）単位（インスタンス単位除外は未対応）。
- TODO（R2残）: カスケード別カリング・境界スムーズブレンド・receive_shadows・point/rect影・
  Play正射/2Dビュー時のCSM・カスケード可視化デバッグ表示。

### Phase R3: HDR＋ポストプロセス土台 【状況: 未着手】
- オフスクリーンHDRターゲット（Rgba16Float）へシーン描画→フルスクリーントーンマップパスで
  スワップチェーンへ（各メッシュシェーダ内のReinhardを撤去し一元化）。
- **RTプール＋ポストパス抽象**: 名前付きレンダーターゲットの確保/再利用、入出力テクスチャ＋
  任意のマスクテクスチャを取るポストパス定義（TOMLパイプラインの流儀に合わせる）。
  「テクスチャ単位・マスクのかけやすさ」はこの抽象で担保（例: パスの入力に mask を宣言可能）。
- 受入: 見た目が現状と同等（トーンマップ位置が変わるのみ）＋ポストパスを1つ挿せるサンプル（例: ビネット）。

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

### Phase R8: インラインRT影（品質オプション） 【状況: 未着手】
- EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE / EXPERIMENTAL_RAY_QUERY を要求する
  「RT対応デバイス」初期化経路を追加（非対応GPUは自動でシャドウマップへフォールバック）。
- BLAS（メッシュごと）/TLAS（フレームごと更新）の構築・更新管理が工数の本体。
- 影解決: ライティング時に rayQuery で遮蔽判定（シャドウマップの代替）。rect/pointの
  ソフトシャドウはRT側が得意（面光源サンプリング）。
- 実験的APIのため、wgpu更新で追従コストが発生しうる点を認識しておく。

### 継続タスク（全フェーズ共通）
- frame_renderer.rs の該当パスを触るたびにモジュール分割（passes/ サブフォルダへ）。
- 各フェーズでデバッグ表示を拡充（R1: ライトギズモ、R2: カスケード可視化、R5: OITバッファ可視化等）。
- Hi-Zオクルージョンの接続は性能課題が顕在化した時点で独立タスクとして実施（実装済み・接続のみ）。

## 実装順の根拠
R1→R2 は依存関係（影はライトの上に）。R3→R4/R5 も依存（ポスト土台の上にブルーム/WBOIT合成）。
R6/R7 は独立しており、R2とR3の間など任意の位置に差し込み可能（疲労分散・検証待ちの間に実施推奨）。
R8 は影アーキテクチャ確定後かつ実験的API理解が必要なため最後。
