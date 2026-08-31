// ============================================================
// shader_common.wgsl  —  PBR シェーダ共通定義
// ============================================================

// ─── Group 0: カメラ ──────────────────────────────────────────

struct CameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    /// ゲーム内累計時間（秒）。旧 `_pad`（vec3 のアライン規則で必ず生じる 4 byte の
    /// 死に領域）を W0 で転用したもの。`ShadingSurface.time` の供給元であり、
    /// フォワード経路の lighting_eval.wgsl がここから読んで契約へ写す。
    /// Play 非ポーズ時のみ進む（草の揺れ・スクリプトの SEED.Time.ElapsedTime と同一値）。
    time:           f32,
    // 以下、Rust 側 uniforms::CameraUniform と完全一致させるための末尾フィールド。
    // mesh/skinned のフラグメントは resolution/inv_view_proj を参照しないため未使用のまま
    // でよいが、宣言を欠くと（G-Buffer デファードパスが独自に持つ同名構造体との）
    // オフセット規約がズレて見えるため、常に Rust と 1:1 で保つ（実害はないが保守事故防止）。
    resolution:     vec2<f32>,
    _pad2:          vec2<f32>,
    // 逆 ViewProjection 行列（Phase D3: G-Buffer デファード化の準備で追加）。
    // 本ファイルを連結する mesh/skinned 系フラグメントは使わない
    // （ライティングパスは deferred_lighting.wgsl が shader_common を連結せず
    //   自前の CameraUniform を宣言して使う。ここでは Rust 構造体との
    //   バイトレイアウト一致だけを保つために宣言している）。
    inv_view_proj:  mat4x4<f32>,
    // **前フレームの** ViewProjection 行列（速度バッファ＝モーションベクタ用）。
    // G-Buffer 用の頂点シェーダ（gbuffer_static_vertex / gbuffer_skinned_vertex）だけが
    // 読む。フォワード系は宣言だけ持ち参照しない（Rust CameraUniform と 1:1 を保つため）。
    // 初回フレーム／Play⇄Edit 切替／シーンロード直後は Rust 側が prev=curr を入れるため、
    // ここが「前フレームでない値」になることは無い（速度爆発の防止は CPU 側で完結する）。
    prev_view_proj: mat4x4<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: モデル変換（インスタンス配列）─────────────────
//
// storage buffer に全インスタンス分の行列を格納し、
// 頂点シェーダが @builtin(instance_index) でインデックスする。
// インスタンス数 1 の通常描画でも同じ構造を使う。

struct ModelUniform {
    model:         mat4x4<f32>,
    // 法線変換行列。**4 列目は法線変換に寄与しない「インスタンス拡張スロット」**。
    // 法線変換は常に `normal_matrix * vec4(n, 0.0)` の形で行われるため、
    // col3 には常に 0.0 が掛かる＝数学的に死んでいる 16 byte である。
    // ここへ per-instance の付随データ（現在は render_tag）を載せることで、
    // ModelUniform のサイズ（128B・全インスタンス×全ノードぶん毎フレーム転送）を
    // 増やさずに情報を追加している。Rust 側 uniforms::INSTANCE_EXTRAS_COLUMN と一致必須。
    normal_matrix: mat4x4<f32>,
}

/// インスタンス拡張スロットの列添字（Rust uniforms::INSTANCE_EXTRAS_COLUMN と一致必須）。
const INSTANCE_EXTRAS_COLUMN: i32 = 3;
/// 拡張スロット内で render_tag（セマンティックタグ）が入る行
/// （Rust uniforms::INSTANCE_EXTRAS_ROW_RENDER_TAG と一致必須）。
const INSTANCE_EXTRAS_ROW_RENDER_TAG: i32 = 0;

/// インスタンス拡張スロットから render_tag を取り出す（0..15 の整数を float で保持）。
/// 頂点シェーダだけが呼び、結果を VertexOutput.render_tag へ flat 補間で流す。
fn instance_render_tag(m: ModelUniform) -> f32 {
    return m.normal_matrix[INSTANCE_EXTRAS_COLUMN][INSTANCE_EXTRAS_ROW_RENDER_TAG];
}
/// ノードごとのワールド行列配列（全インスタンス分）。
/// @builtin(instance_index) は DrawIndexedIndirect.first_instance を通じて
/// GPU カリングが書き込んだ実インスタンス番号を直接受け取る。
@group(1) @binding(0) var<storage, read> u_instances: array<ModelUniform>;

// ─── Group 2: マテリアル ──────────────────────────────────────

struct MaterialUniform {
    base_color_factor:  vec4<f32>,
    metallic_factor:    f32,
    roughness_factor:   f32,
    alpha_cutoff:       f32,
    has_base_color_tex: u32,
    emissive_factor:    vec3<f32>,
    has_normal_tex:     u32,
    has_mr_tex:         u32,
    has_occlusion_tex:  u32,
    has_emissive_tex:   u32,
    // 屈折率（IOR, Phase RT-Translucency）。旧 _pad（offset 60）を転用。1.0=屈折なし。
    ior:                f32,
    // 透過率（transmission, 0..1。ガラス表現）。offset 64。0.0=従来動作（後方互換）。
    transmission:       f32,
    // MR テクスチャ無視トグル（旧 _pad0, offset 68 を転用）。0=無視しない（従来の乗算）、
    // 1=無視（metallic/roughness factor をそのまま実効値にする）。MR 採取分岐で参照。
    mr_tex_ignore:      u32,
    // 拡散透過（diffuse_transmission, 0..1。葉・布・紙の逆光透け）。旧 _pad1（offset 72）を転用。
    // 0.0=従来動作。gather_surface が Surface.diffuse_transmission へ渡し lighting_eval の逆光項が使う。
    diffuse_transmission: f32,
    // 頂点カラー無視トグル（旧 _pad2, offset 76 を転用）。0=乗算（従来）、1=乗算スキップ。
    // カメラプレビューの地形簡易描画専用。gather_surface のベースカラー採取が参照する。
    // Rust uniforms::MaterialUniform（offset 76, u32）と 1:1。
    ignore_vertex_color: u32,
    // 汎用ユーザーデータ回線（offset 80, 0..1）。エンジンは意味を定めない自由回線で、
    // 濡れ・ダメージ・経年劣化などをマテリアルが好きに載せる。G-Buffer RT2.a へ焼かれる。
    // 既定 0.0＝旧データと一致（後方互換）。
    user_data:          f32,
    // シェーディングモデル ID（offset 84）。0 = DefaultPBR（現状これのみ）。
    // render_tag と一緒に G-Buffer RT3.a へビットパックされる（pack_surface_id 参照）。
    shading_model:      u32,
    // 予約（offset 88 / 92）。構造体サイズを 96（16 の倍数）に保つためのパディング。
    // 次にマテリアルへ値を足すときはここを転用してサイズを維持すること。
    _pad3:              u32,
    _pad4:              u32,
}
@group(2) @binding(0)  var<uniform> u_material:          MaterialUniform;
@group(2) @binding(1)  var          t_base_color:         texture_2d<f32>;
@group(2) @binding(2)  var          s_base_color:         sampler;
@group(2) @binding(3)  var          t_normal:             texture_2d<f32>;
@group(2) @binding(4)  var          s_normal:             sampler;
@group(2) @binding(5)  var          t_metallic_roughness: texture_2d<f32>;
@group(2) @binding(6)  var          s_metallic_roughness: sampler;
@group(2) @binding(7)  var          t_occlusion:          texture_2d<f32>;
@group(2) @binding(8)  var          s_occlusion:          sampler;
@group(2) @binding(9)  var          t_emissive:           texture_2d<f32>;
@group(2) @binding(10) var          s_emissive:           sampler;

// ─── Group 4: ライト／クラスタ参照は light_common.wgsl へ移設 ─────────
//
// GpuLight / LightMeta 構造体、group 4 binding 0/1（ライト）、
// binding 7〜9（クラスタ参照）は light_common.wgsl（本ファイルより後方に連結）へ
// 移設した（Phase D3: G-Buffer デファード化 Phase A）。
//
// 理由: デファードのライティングパス（deferred_lighting.wgsl）は group 4 のライト／
// クラスタ参照だけを必要とし、本ファイルが持つ group 2（マテリアル 11 binding）は
// 不要である。両方を同じファイルに同居させると、ライティングパスが本ファイルを
// 連結した時点で使わない group 2 のバインディングまでリフレクションに載ってしまう
// （pipeline_config::reflect_bgls は module.global_variables を無条件に走査するため）。
// そのため mesh / skinned / transparent 系の TOML は本ファイルの直後に
// light_common.wgsl を連結すること（cluster_common → shader_common → light_common の順）。


// ─── 頂点シェーダ出力 / フラグメントシェーダ入力 ─────────────

struct VertexOutput {
    @builtin(position) clip_pos:     vec4<f32>,
    @location(0)       world_pos:    vec3<f32>,
    @location(1)       world_normal: vec3<f32>,
    @location(2)       world_tan:    vec3<f32>,
    @location(3)       world_bitan:  vec3<f32>,
    @location(4)       uv0:          vec2<f32>,
    @location(5)       uv1:          vec2<f32>,
    @location(6)       color:        vec4<f32>,
    /// アクタ単位のセマンティックタグ（0..15 の整数を float で保持。0 = タグ無し）。
    /// インスタンス拡張スロット（ModelUniform.normal_matrix の 4 列目）由来で、
    /// インスタンスごとに一定なので `flat` 補間（頂点間で値を混ぜてはならない）。
    /// G-Buffer 書き込みでのみ使う（フォワード系フラグメントは参照しない）。
    @location(7) @interpolate(flat) render_tag: f32,

    // ─── 速度バッファ（モーションベクタ）用のクリップ座標 2 本 ───────────────
    //
    // 【なぜクリップ座標のまま渡すのか】
    // スクリーン位置は「クリップ座標を w で割った NDC」であり、透視除算は **線形補間の
    // 後に** 行わないと正しくならない（頂点段で割ってから補間すると遠近が歪む）。
    // したがって頂点段では割らずにクリップ座標のまま渡し、フラグメント段で割る。
    //
    // 【なぜ curr も渡すのか（@builtin(position) では足りない理由）】
    // フラグメントの `@builtin(position)` は**フレームバッファ画素座標**であり、
    // `set_viewport`（Play のレターボックス帯）を適用すると NDC への逆変換に
    // ビューポート矩形が要る。両方をクリップ座標で持てば差分は常に
    // 「ビューポート正規化 UV の移動量」になり、ビューポート矩形に一切依存しない。
    //
    // 【コスト】フォワード系パイプラインもこの構造体を共有するため、速度を使わない
    // 描画でも 8 float ぶんの補間が乗る（フォワードの頂点シェーダは prev=curr を入れる）。
    // ビューポート矩形を CameraUniform へ足して 4 float に減らす案もあるが、
    // 「全カメラ更新箇所で矩形を正しく入れる」という静かに壊れやすい依存を
    // 増やす方が高くつくと判断し、自己完結するこちらを採った。
    /// 今フレームのクリップ座標（`view_proj * world_pos`）。
    @location(8) curr_clip: vec4<f32>,
    /// 前フレームのクリップ座標（`prev_view_proj * prev_world_pos`）。
    /// 速度を書かないパイプライン（フォワード）では `curr_clip` と同値（＝速度 0）。
    @location(9) prev_clip: vec4<f32>,

    /// 法線シャープネス（0=スムーズ法線をそのまま使う〜1=面法線へ完全に寄せる）。
    ///
    /// 頂点属性 `tangent.w` から `decode_vertex_sharpness` で復号した値。
    /// **地形（terrain_gbuffer_write.wgsl）だけが参照する**。通常メッシュの
    /// `tangent.w` は ±1 のハンドネスなので復号すると必ず 0 になり、
    /// この機能の導入前とビット単位で同じ絵になる。
    @location(10) sharpness: f32,
}

/// 法線シャープネスを載せている接線 w のバイアス。
/// Rust 側 `TANGENT_W_SHARPNESS_BIAS`（terrain_mesh_build.rs）と一致必須。
///
/// 生値 0..1 ではなく 1..2 に載せる理由は Rust 側のコメントを参照
/// （w=0 だと従法線 `normalize(cross(N,T) * w)` が NaN になるため）。
const VERTEX_SHARPNESS_TANGENT_BIAS: f32 = 1.0;

/// 接線 w から法線シャープネス（0..=1）を復号する。
///
/// 通常メッシュの w（+1 / -1 のハンドネス）は clamp によって必ず 0 へ落ちる
/// （+1 → 0、-1 → -2 を 0 へクランプ）。よって地形以外は常にスムーズ法線。
fn decode_vertex_sharpness(tangent_w: f32) -> f32 {
    return clamp(tangent_w - VERTEX_SHARPNESS_TANGENT_BIAS, 0.0, 1.0);
}

// ─── PBR ヘルパー関数は pbr_common.wgsl へ移設 ───────────────
//
// distribution_ggx / geometry_smith / fresnel_schlick / PI は pbr_common.wgsl
// （バインディングを持たない共有定義）へ移設した（Phase D3: G-Buffer デファード化 Phase A）。
// 理由は光源系構造体の分離（本ファイル冒頭の Group 4 のコメント参照）と同じ:
// デファードのライティングパスが shader_common.wgsl を連結せずに evaluate_lighting を
// 呼べるようにするため。本ファイルを連結する全 TOML は pbr_common.wgsl も連結すること
// （例: ["cluster_common.wgsl", "pbr_common.wgsl", "shader_common.wgsl", "light_common.wgsl", ...]）。
