// ============================================================
// grass_gbuffer.wgsl — プロシージャル草の GPU インスタンシング G-Buffer 書き込み
//
// ## 役割（単一責任）
// 「1 インスタンス = 草 1 株」を、**頂点バッファを一切使わず**
// `@builtin(vertex_index)` と `@builtin(instance_index)` だけから頂点シェーダで
// 組み立て、風で揺らして G-Buffer（MRT 4 枚）へ焼く。
// ライティング・影・SSAO・反射は G-Buffer を読む既存パスがそのまま担当するため、
// 草専用のライティングコードは一切書かない（deferred 経路に完全に乗る）。
//
// ## 自己完結（連結しない）
// 本ファイルは **単体で** シェーダモジュールになる（shader_common.wgsl を連結しない）。
// 理由: 草は mesh_vertex 頂点フォーマットも model/material バインドグループも使わず、
// shader_common.wgsl を連結すると使わない group1/group2 のバインディングが
// リフレクションに載ってしまうため。
// 先例: deferred_lighting.wgsl も同じ理由で CameraUniform を自前宣言している。
// したがって `CameraUniform` は Rust 側 `uniforms::CameraUniform`（224 バイト）と
// **バイト単位で一致必須**（Rust / shader_common.wgsl / deferred_lighting.wgsl /
// 本ファイルの 4 箇所を同時に直すこと）。
//
// ## バインディング規約
//   group 0 binding 0: uniform CameraUniform            （MeshPipeline の camera BGL を借用）
//   group 1 binding 0: storage(read) array<GrassInstance>（1 要素 = 草 1 株）
//   group 1 binding 1: uniform GrassUniform             （見た目・風のパラメータ）
//   group 2 binding 0: texture_2d<f32>                  （瞬発インタラクションフィールド）
//   group 2 binding 1: sampler                          （同上・線形/ClampToEdge）
//   group 2 binding 2: uniform InteractionFieldUniform  （場の窓と曲げ係数）
//
// ## インタラクションフィールドの消費（Phase I1）
// group2 の速度場（ワールド俯瞰・カメラ追従の小窓）を **頂点段で** サンプルし、
// 草を速度方向へ曲げる（＝踏み分け・薙ぎ倒し）。場の窓の外では影響ゼロ。
// 場を書くのは `InteractionSource` コンポーネントだけで、本シェーダは
// **読むだけ**（書き手と読み手の分離＝ロードマップ §1.3 の設計）。
// 詳細は `renderer/interaction/mod.rs` と `shaders/interaction_field.wgsl`。
//
// ## G-Buffer レイアウト（gbuffer_write.wgsl と同一。正典はそちら）
//   RT0 Rgba8Unorm  : albedo.rgb + occlusion.a
//   RT1 Rgba16Float : world normal.xyz + 0
//   RT2 Rgba8Unorm  : metallic.r + roughness.g + diffuse_transmission.b + 予約 a
//   RT3 Rgba16Float : emissive.rgb(HDR) + 0
//
// ## 描画コールの形（重要）
//   rp.draw(0..GRASS_MAX_VERTS_PER_BLADE, 0..instance_count)
// 頂点数は **常に最大値で固定**（インスタンスごとに draw を分けない）。
// 実際の分割数 `segments` / 平面数 `cross_planes` がそれより少ないインスタンスでは
// 余った頂点を「縮退（degenerate）」させる。縮退の方法は vs_grass のコメント参照。
// ============================================================

// ─── Rust 側と一致必須の定数（grass_gbuffer.rs のテストが文字列一致で検証する）───

/// 1 枚の草平面を縦に分割する最大セグメント数。
/// Rust `grass_gbuffer.rs::GRASS_MAX_SEGMENTS` と一致必須。
/// また `runtime/src/engine/terrain/scatter/props.rs` の `GRASS_MAX_SEGMENTS`
/// （別担当が作成予定。本ファイル作成時点では未存在）とも一致させること。
const GRASS_MAX_SEGMENTS: u32 = 8u;

/// 1 セグメント（クワッド）= 三角形 2 枚 = 6 頂点。
const GRASS_VERTS_PER_SEGMENT: u32 = 6u;

/// 1 株が持てる平面の最大枚数（十字配置 = 2 枚）。
const GRASS_MAX_PLANES: u32 = 2u;

/// 1 株ぶんの固定頂点数（＝ draw() に渡す頂点数）。
const GRASS_MAX_VERTS_PER_BLADE: u32 = GRASS_MAX_SEGMENTS * GRASS_VERTS_PER_SEGMENT * GRASS_MAX_PLANES;

/// 1 平面ぶんの固定頂点数（vertex_index の分解に使う）。
const GRASS_VERTS_PER_PLANE: u32 = GRASS_MAX_SEGMENTS * GRASS_VERTS_PER_SEGMENT;

// ─── 見た目の調整定数（マジックナンバー禁止）─────────────────────────────

/// 穂先の幅が根元の幅に対して占める比率（テーパー）。
/// 0 にすると穂先が完全な点になりアンチエイリアスが暴れるため、わずかに残す。
const GRASS_TIP_WIDTH_RATIO: f32 = 0.15;

/// 根元のアンビエントオクルージョン（RT0.a）。草の根本は周囲に遮蔽されて暗いという
/// 近似。穂先へ向かって 1.0（無遮蔽）へ線形補間する。
const GRASS_ROOT_OCCLUSION: f32 = 0.35;

/// インスタンスごとの色相（明度）ゆらぎ幅。seed のハッシュを ±この幅で乗算する。
/// 0 だと全株が同色になり CG くさくなるため、わずかにばらす。
const GRASS_TINT_VARIATION: f32 = 0.18;

/// 穂先のアルファカットアウトが効き始める高さ（0=根元, 1=穂先）。
/// これより下では tip_mask が必ず 1.0 になり、絶対に discard されない。
const GRASS_TIP_FADE_START: f32 = 0.55;

/// 2 枚目の十字平面を 1 枚目から回す角度（ラジアン、90 度）。
const GRASS_CROSS_PLANE_YAW_OFFSET: f32 = 1.5707963;

/// G-Buffer RT1.w へ書く「authored 法線」フラグ値（1=有効）。
/// deferred_lighting.wgsl の GBUFFER_NORMAL_AUTHORED_THRESHOLD と対で意味を持つ。
const GRASS_NORMAL_AUTHORED_FLAG: f32 = 1.0;

// ─── サブピクセル・アンチエイリアス（葉のスクリーン空間最小幅）────────────────
//
// deferred G-Buffer には MSAA も alpha-to-coverage も無い（草は不透明パスに乗る）。
// そのため遠方の細い葉（既定 幅 0.04m）は 1 画素未満へ縮み、フレームごとに
// 当たり画素が入れ替わって「ギザギザ・点滅・まばら」に見える（実測された症状）。
// 対策として、葉の半幅を **スクリーン空間で最低 GRASS_MIN_SCREEN_HALF_PX 画素**
// 確保するよう頂点段で太らせる。近距離では既に十分太いので倍率 1（無変化）、
// 遠距離のみ太らせて葉を常に 1 画素以上の安定したリボンにする。幾何法線・穂先
// カットアウト・風の計算には一切影響しない（幅だけを変える）。

/// 確保するスクリーン空間の最小「半幅」（画素）。full width では約 2 倍。
/// 大きすぎると近景の細い葉まで太って束に見えるため、1 画素弱に抑える。
const GRASS_MIN_SCREEN_HALF_PX: f32 = 0.75;
/// 最小画素確保のための葉幅拡大の上限倍率。
/// 無制限だと真横・遠方の退化ケースで葉が異常に太る（板の過剰な重なり）ため飽和させる。
const GRASS_MAX_WIDTH_WIDEN: f32 = 8.0;
/// クリップ座標 w がこの値以下なら「カメラ背面」とみなし拡大しない（0 除算回避）。
const GRASS_CLIP_W_EPSILON: f32 = 1.0e-4;
/// スクリーン投影幅がこの値（画素）以下なら退化とみなし拡大しない。
const GRASS_SCREEN_PX_EPSILON: f32 = 1.0e-4;

// ─── 風・曲げの調整定数 ───────────────────────────────────────────────

/// 突風（gust）の位相を基本揺れの位相からずらす倍率。
/// 1.0 にすると両者が同期して「1 つの正弦波」に見えてしまうため、無理数寄りの値を使う。
const GUST_PHASE_SCALE: f32 = 0.37;

/// seed ハッシュを位相へ写す係数（2π。0〜1 の乱数を全周へ広げる）。
const GRASS_PHASE_TAU: f32 = 6.2831853;

/// 曲げ角の上限（ラジアン）。これを超えると草が地面へめり込む・裏返るため飽和させる。
/// 約 100 度。真横（90 度）を少し超える程度までは許す。
const GRASS_MAX_BEND_ANGLE: f32 = 1.7453293;

// ─── 数値安定化の閾値 ─────────────────────────────────────────────────

/// ベクトルをこの長さ以下なら「ゼロベクトル」とみなす（正規化で NaN を出さないため）。
const GRASS_VEC_EPSILON: f32 = 1.0e-4;

/// 外積の結果がこの長さ以下なら「2 ベクトルが平行」とみなし、代替軸へフォールバックする。
const GRASS_CROSS_EPSILON: f32 = 1.0e-3;

/// ワールド上方向（法線が取れないときのフォールバック）。
const GRASS_WORLD_UP: vec3<f32> = vec3<f32>(0.0, 1.0, 0.0);

/// up と forward が平行だったときの代替 forward 軸その 1。
const GRASS_FALLBACK_AXIS_A: vec3<f32> = vec3<f32>(1.0, 0.0, 0.0);
/// 代替軸その 1 まで平行だったときの最終フォールバック軸（A と直交）。
const GRASS_FALLBACK_AXIS_B: vec3<f32> = vec3<f32>(0.0, 0.0, 1.0);

/// 整数ハッシュの混合定数（PCG 系 xorshift-multiply。GPU ハッシュの定番値）。
const GRASS_HASH_MUL_A: u32 = 0x7feb352du;
const GRASS_HASH_MUL_B: u32 = 0x846ca68bu;
/// u32 → [0,1) 正規化の除数（2^32 - 1）。
const GRASS_HASH_INV_MAX: f32 = 4294967295.0;

// ============================================================
//  Group 0: カメラ（Rust uniforms::CameraUniform・224 バイトと一致必須）
// ============================================================

/// ビュー×プロジェクション行列一式。
/// 草が実際に使うのは view_proj と prev_view_proj（速度バッファ用）のみだが、
/// **レイアウト一致のため全フィールドを宣言する**
/// （借用している MeshPipeline の camera BGL は 288 バイトのバッファを差してくる）。
struct CameraUniform {
    view_proj:      mat4x4<f32>,   // offset   0
    view:           mat4x4<f32>,   // offset  64
    position:       vec3<f32>,     // offset 128
    _pad:           f32,           // offset 140
    resolution:     vec2<f32>,     // offset 144
    _pad2:          vec2<f32>,     // offset 152
    inv_view_proj:  mat4x4<f32>,   // offset 160
    // 前フレームの ViewProjection（速度バッファ用）。草は静的扱いなので
    // 「今フレームのワールド座標を前フレームのカメラで再投影する」だけでよく、
    // 前フレームのインスタンス行列（velocity_common.wgsl の group 4）は要らない。
    prev_view_proj: mat4x4<f32>,   // offset 224（計 288）
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ============================================================
//  Group 1: 草インスタンス＋パラメータ
// ============================================================

/// 草 1 株ぶんの散布データ。
/// Rust `GrassInstanceGpu` と一致必須（std430 / 48 バイト）。
struct GrassInstance {
    /// 株の根元のワールド座標。
    pos:    vec3<f32>,
    /// 平面の向き（Y 軸まわり、ラジアン）。
    yaw:    f32,
    /// 生えている面の法線（ワールド）。これが草の「上方向」になる。
    normal: vec3<f32>,
    /// 株ごとの大きさ倍率（高さ・幅の双方に掛かる）。
    scale:  f32,
    /// 株ごとの疑似乱数の種。風の位相・色ゆらぎに使う。
    seed:   u32,
    _pad0:  u32,
    _pad1:  u32,
    _pad2:  u32,
}
@group(1) @binding(0) var<storage, read> u_instances: array<GrassInstance>;

/// 草の種別ごとの見た目・風パラメータ。
/// Rust `GrassUniformGpu` と一致必須（80 バイト）。
struct GrassUniform {
    /// 根元の色（リニア）。
    color_bottom:  vec3<f32>,
    /// 根元の葉幅（ワールド単位）。
    width:         f32,
    /// 穂先の色（リニア）。
    color_top:     vec3<f32>,
    /// 葉の長さ（ワールド単位。instance.scale が掛かる）。
    height:        f32,

    // ── 風 ──
    /// 基本揺れの振幅（曲げ角のラジアン相当）。
    wind_strength:  f32,
    /// 基本揺れの時間周波数。
    wind_speed:     f32,
    /// 位置による位相差の空間周波数（大きいほど細かい波が走る）。
    wind_frequency: f32,
    /// 突風の振幅。
    gust_strength:  f32,
    /// 突風の時間周波数（wind_speed より低くすること）。
    gust_speed:     f32,
    /// 経過時間（秒）。update_time が毎フレームここだけ書き換える。
    time:           f32,
    /// 風とは無関係な静的な垂れ（曲げ角のラジアン相当）。
    bend:           f32,
    /// G-Buffer へ書く roughness。
    roughness:      f32,

    /// このインスタンス群が実際に使う縦分割数（1..=GRASS_MAX_SEGMENTS）。
    segments:         u32,
    /// 実際に使う平面枚数（1 または 2）。
    cross_planes:     u32,
    /// 穂先アルファカットアウトの閾値。0 以下でカットアウト無効。
    tip_alpha_cutoff: f32,
    /// 陰影用法線を地表法線（株の up）へ寄せる割合（0..1）。
    /// 0 = 真の幾何法線をそのまま使う、1 = 完全に地表法線へ置き換える。
    /// 詳細と存在理由は fs_grass の「法線の地表寄せ」節を参照。
    /// Rust `GrassUniformGpu::normal_up_blend` と一致必須
    /// （旧 `_pad` を潰したフィールドであり、構造体サイズは 80 バイトのまま）。
    normal_up_blend:  f32,
}
@group(1) @binding(1) var<uniform> u_grass: GrassUniform;

// ============================================================
//  Group 2: 瞬発インタラクションフィールド（Phase I1・読むだけ）
// ============================================================

/// 場の窓と消費パラメータ。
/// Rust `InteractionFieldUniformGpu` および `interaction_field.wgsl` の
/// 同名構造体と **フィールド順まで一致必須**（3 箇所を同時に直すこと。
/// テスト `interaction_uniform_fields_match_grass_shader` が照合する）。
struct InteractionFieldUniform {
    /// 今フレームの窓のワールド XZ 最小（テクセル単位にスナップ済み）。
    origin_xz:      vec2<f32>,
    /// 前フレームの窓のワールド XZ 最小（更新パスのみ使用）。
    prev_origin_xz: vec2<f32>,
    /// 1 テクセルのワールドサイズ（m。更新パスのみ使用）。
    texel_size:     f32,
    /// 窓の一辺の逆数（1/m）。ワールド XZ → [0,1] UV 変換に使う。
    inv_extent:     f32,
    /// 減衰係数（更新パスのみ使用）。
    decay:          f32,
    /// 場の一辺の解像度（更新パスのみ使用）。
    resolution:     u32,
    /// 有効なソース数（更新パスのみ使用）。
    source_count:   u32,
    /// 速度 1 m/s あたりの曲げ角（rad·s/m）。
    bend_per_speed: f32,
    /// インタラクションによる曲げ角の上限（rad）。
    max_bend:       f32,
    /// パディング（未使用）。
    _pad:           f32,
}
@group(2) @binding(0) var  u_interaction_tex:     texture_2d<f32>;
@group(2) @binding(1) var  u_interaction_sampler: sampler;
@group(2) @binding(2) var<uniform> u_interaction: InteractionFieldUniform;

// ============================================================
//  ユーティリティ
// ============================================================

/// 株の根元のワールド XZ で瞬発場をサンプルし、ワールド XZ 速度（m/s）を返す。
///
/// **窓の外は必ずゼロ**（＝場の影響なし）。ClampToEdge サンプラーのままだと
/// 窓の縁の値が外側へ無限に引き伸ばされ、遠方の草まで倒れてしまうため、
/// UV 範囲を明示的に判定して弾く。
///
/// 頂点段からのサンプルなので `textureSampleLevel`（明示 LOD）を使う
/// （頂点シェーダでは暗黙微分が使えず `textureSample` は呼べない）。
fn grass_interaction_velocity(world_xz: vec2<f32>) -> vec2<f32> {
    let uv = (world_xz - u_interaction.origin_xz) * u_interaction.inv_extent;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
        return vec2<f32>(0.0);
    }
    // .xy = XZ 速度ベクトル場。.zw は I2（波エネルギー）用の予約チャンネルで、草は見ない。
    return textureSampleLevel(u_interaction_tex, u_interaction_sampler, uv, 0.0).xy;
}

/// u32 の種を [0,1) の f32 へ写す整数ハッシュ（xorshift-multiply）。
/// sin ベースのハッシュと違い、大きな seed でも精度が落ちない。
fn grass_hash_f32(seed: u32) -> f32 {
    var h: u32 = seed;
    h = h ^ (h >> 16u);
    h = h * GRASS_HASH_MUL_A;
    h = h ^ (h >> 15u);
    h = h * GRASS_HASH_MUL_B;
    h = h ^ (h >> 16u);
    return f32(h) / GRASS_HASH_INV_MAX;
}

/// ゼロ長でも NaN を返さない正規化（長さが極小なら fallback を返す）。
fn grass_safe_normalize(v: vec3<f32>, fallback: vec3<f32>) -> vec3<f32> {
    let len = length(v);
    if (len > GRASS_VEC_EPSILON) {
        return v / len;
    }
    return fallback;
}

/// 「上方向 up」に直交する葉の横方向（side）を作る。
///
/// side = normalize(cross(up, forward)) が基本だが、up と forward が平行だと
/// 外積がゼロベクトルになり normalize が NaN を生む（＝画面全体が消える実害のある罠）。
/// 平行だった場合は互いに直交する固定軸 A → B の順にフォールバックする。
fn grass_side_axis(up: vec3<f32>, forward: vec3<f32>) -> vec3<f32> {
    let c0 = cross(up, forward);
    if (length(c0) > GRASS_CROSS_EPSILON) {
        return normalize(c0);
    }
    // forward が up と平行 → 固定軸 A で作り直す。
    let c1 = cross(up, GRASS_FALLBACK_AXIS_A);
    if (length(c1) > GRASS_CROSS_EPSILON) {
        return normalize(c1);
    }
    // up が A とも平行（up ≒ ±X）→ A と直交する B を使えば必ず非退化。
    return normalize(cross(up, GRASS_FALLBACK_AXIS_B));
}

/// 高さパラメータ h（0=根元, 1=穂先）における曲げ角を返す。
///
/// 重み付けは `h*h`。片持ち梁（cantilever）のたわみが根元からの距離の二乗に比例する
/// という近似で、**根元では必ず 0**（＝地面から葉が浮かない）ことが保証される。
fn grass_bend_angle(theta_max: f32, h: f32) -> f32 {
    return theta_max * h * h;
}

/// 葉の中心 `center` と幅方向の端点 `edge`（= center + side*half_width）との
/// スクリーン空間距離（画素）を返す。どちらかがカメラ背面なら負値。
///
/// NDC は [-1,1] なので、画素へは各軸 `* 0.5 * resolution` で写す。
fn grass_screen_half_px(center: vec3<f32>, edge: vec3<f32>) -> f32 {
    let c = u_camera.view_proj * vec4<f32>(center, 1.0);
    let e = u_camera.view_proj * vec4<f32>(edge, 1.0);
    if (c.w <= GRASS_CLIP_W_EPSILON || e.w <= GRASS_CLIP_W_EPSILON) {
        return -1.0;
    }
    let ndc_c = c.xy / c.w;
    let ndc_e = e.xy / e.w;
    let d = (ndc_e - ndc_c) * u_camera.resolution * 0.5;
    return length(d);
}

/// 葉の半幅を、スクリーン空間で最低 `GRASS_MIN_SCREEN_HALF_PX` 画素になるよう拡大する。
///
/// 近距離（既に十分太い）では倍率 1（無変化）、遠距離のみ太らせる。上限あり。
/// カメラ背面・退化ケースでは元の半幅をそのまま返す（触らない）。
fn grass_min_screen_half_width(center: vec3<f32>, side: vec3<f32>, half_width: f32) -> f32 {
    let edge = center + side * half_width;
    let px = grass_screen_half_px(center, edge);
    if (px <= GRASS_SCREEN_PX_EPSILON) {
        return half_width;
    }
    // px は現在の半幅の画素数。目標画素との比が拡大倍率（1..MAX でクランプ）。
    let widen = clamp(GRASS_MIN_SCREEN_HALF_PX / px, 1.0, GRASS_MAX_WIDTH_WIDEN);
    return half_width * widen;
}

// ============================================================
//  頂点シェーダ
// ============================================================

/// 頂点シェーダ → フラグメントシェーダの受け渡し。
struct GrassVsOut {
    @builtin(position) clip_position: vec4<f32>,
    /// ワールド座標（今は未使用だが G-Buffer 系の慣例に合わせて渡す）。
    @location(0) world_position: vec3<f32>,
    /// 曲げ後の葉面のワールド法線（正規化済み・表側基準）。
    @location(1) world_normal:   vec3<f32>,
    /// 根元 0 → 穂先 1 の高さパラメータ（色グラデーション・AO に使う）。
    @location(2) height_t:       f32,
    /// 葉幅方向のパラメータ（-1=左端, +1=右端）。穂先カットアウトに使う。
    @location(3) side_t:         f32,
    /// 株ごとの明度ゆらぎ係数（1.0 中心）。
    @location(4) tint:           f32,
    /// 今フレームのクリップ座標（速度バッファ用。透視除算はフラグメント段で行う）。
    @location(6) curr_clip:      vec4<f32>,
    /// 前フレームのクリップ座標（速度バッファ用）。
    ///
    /// 草は「静的ジオメトリ扱い」なので、**今フレームのワールド座標**を
    /// 前フレームのカメラ行列で再投影しただけの値を入れる。風の揺れによる
    /// 頂点移動ぶんは意図的に速度へ含めない（風は毎フレーム位相が変わる
    /// 高周波の変形で、これを速度に含めると TAA の履歴が常に破棄されて
    /// 逆にちらつく。地形・草を静的扱いにする方針は rendering_flow.md 参照）。
    @location(7) prev_clip:      vec4<f32>,
    /// この株が生えている地表の法線（＝株のローカル up・正規化済み）。
    ///
    /// インスタンス単位の定数であり葉の頂点ごとには変化しないが、
    /// 「法線の地表寄せ」をフラグメント段で行うために補間経由で渡す。
    /// 頂点段で寄せてしまうと、フラグメント段の front_facing 反転が
    /// 寄せ済み法線ごと地平線の下へ倒してしまうため（fs_grass の説明を参照）。
    @location(5) ground_normal:  vec3<f32>,
}

/// 縮退頂点（このインスタンスでは使われない余りの頂点）の出力を作る。
///
/// ## 縮退のしかた（設計判断）
/// draw() の頂点数は常に GRASS_MAX_VERTS_PER_BLADE で固定なので、
/// `segments` / `cross_planes` が最大未満のインスタンスでは余りの頂点が出る。
/// これらは **すべて同一のクリップ座標へ潰す**。vertex_index → segment の分解は
/// 6 頂点単位（= 三角形 2 枚がまるごと同じ segment に属する）なので、
/// 不使用セグメントの 3 頂点は必ず同時に潰れ、**面積 0 の三角形**になる。
/// 面積 0 の三角形はラスタライザが 1 フラグメントも生成しない（仕様上の保証）。
///
/// w=0（無限遠）や NaN を使わないのはなぜか: w=0 はクリップ空間で未定義寄りの扱いになり
/// ドライバ差が出る。NaN はさらに悪く、実装によってはラスタライズされる。
/// 「同一座標へ潰して面積 0 にする」のが最も移植性の高い縮退手段である。
fn grass_degenerate_vertex() -> GrassVsOut {
    var out: GrassVsOut;
    // NDC 原点（有効なクリップ座標）。3 頂点が完全に一致するため面積 0。
    out.clip_position  = vec4<f32>(0.0, 0.0, 0.0, 1.0);
    out.world_position = vec3<f32>(0.0);
    out.world_normal   = GRASS_WORLD_UP;
    out.height_t       = 0.0;
    out.side_t         = 0.0;
    out.tint           = 1.0;
    out.ground_normal  = GRASS_WORLD_UP;
    // 縮退頂点はラスタライズされない（面積 0）が、構造体の全フィールドを
    // 埋めておく（未初期化を残さない＝WGSL の規約と読み手の安全のため）。
    out.curr_clip      = out.clip_position;
    out.prev_clip      = out.clip_position;
    return out;
}

/// 草の葉を頂点シェーダだけで組み立てる（頂点バッファなし）。
@vertex
fn vs_grass(
    @builtin(vertex_index)   vertex_index:   u32,
    @builtin(instance_index) instance_index: u32,
) -> GrassVsOut {
    // ── 1. vertex_index を「平面 / セグメント / コーナー」へ分解する ──
    //   [ plane ][ segment ][ corner ]
    //     0..1     0..7       0..5
    let plane   = vertex_index / GRASS_VERTS_PER_PLANE;
    let in_plane = vertex_index % GRASS_VERTS_PER_PLANE;
    let segment = in_plane / GRASS_VERTS_PER_SEGMENT;
    let corner  = in_plane % GRASS_VERTS_PER_SEGMENT;

    // ── 2. パラメータを有効範囲へ丸める（不正データで無限ループ／0 除算しないため）──
    let seg_count   = clamp(u_grass.segments,     1u, GRASS_MAX_SEGMENTS);
    let plane_count = clamp(u_grass.cross_planes, 1u, GRASS_MAX_PLANES);

    // ── 3. 使わない頂点は縮退させる ──
    if (plane >= plane_count || segment >= seg_count) {
        return grass_degenerate_vertex();
    }

    let inst = u_instances[instance_index];

    // ── 4. 葉のローカルフレーム（up / forward / side）を作る ──
    //   up は生えている面の法線。法線が壊れている（長さ 0）ならワールド +Y。
    let up = grass_safe_normalize(inst.normal, GRASS_WORLD_UP);

    //   forward は yaw から作る水平ベクトル。十字の 2 枚目は 90 度回す。
    let yaw = inst.yaw + f32(plane) * GRASS_CROSS_PLANE_YAW_OFFSET;
    let forward_raw = vec3<f32>(sin(yaw), 0.0, cos(yaw));

    //   side は up と forward の外積（平行時のフォールバックあり）。
    let side = grass_side_axis(up, forward_raw);
    //   forward を up・side に厳密直交させ直す（傾いた斜面でも直交フレームを保つ）。
    //   side ⊥ up かつ |side|=|up|=1 なので cross の結果は必ず単位長で非退化。
    let forward = cross(side, up);

    // ── 5. 風の位相と曲げ角の総量を求める ──
    //   位相 = 株ごとの乱数（全周へ広げる）＋ 位置による空間位相差。
    //   空間位相差があることで、隣り合う株が波として同期しつつも位置でずれる。
    let phase = grass_hash_f32(inst.seed) * GRASS_PHASE_TAU
              + dot(inst.pos.xz, vec2<f32>(u_grass.wind_frequency, u_grass.wind_frequency));

    //   基本揺れ（高周波・小振幅）。
    let sway = sin(u_grass.time * u_grass.wind_speed + phase) * u_grass.wind_strength;
    //   突風（低周波・大振幅）。位相をずらして基本揺れと同期しないようにする。
    let gust = sin(u_grass.time * u_grass.gust_speed + phase * GUST_PHASE_SCALE)
             * u_grass.gust_strength;

    //   静的な垂れ（bend）を足し、飽和させる（草が裏返る／地面へ潜るのを防ぐ）。
    //   ここまでが**風だけ**の曲げ角（符号あり。+ が forward 方向）。
    let theta_wind = clamp(sway + gust + u_grass.bend,
                           -GRASS_MAX_BEND_ANGLE, GRASS_MAX_BEND_ANGLE);

    // ── 5b. インタラクションフィールドとの合成（Phase I1）──────────────────
    //
    //   ## 何をするか
    //   瞬発場から「この株の足元を通った物体の速度」を読み、その向きへ草を倒す。
    //   曲げ角は速度の大きさに比例（`bend_per_speed`）し、`max_bend` で飽和する。
    //
    //   ## 風との合成方法（設計判断）
    //   風は「forward 方向へ theta_wind だけ曲げる」、インタラクションは
    //   「push 方向へ theta_interact だけ曲げる」。方向が違うのでスカラー加算はできない。
    //   そこで **曲げを「向き × 角度」のベクトルとして足す**:
    //       bend_vec  = forward * theta_wind + push * theta_interact
    //       曲げ方向  = normalize(bend_vec) / 曲げ角 = length(bend_vec)
    //   これは風の挙動の厳密な一般化である（インタラクション 0 のとき
    //   bend_dir = ±forward・theta = |theta_wind| となり、cos が偶関数・sin が奇関数
    //   であることから従来の tangent 式と完全に一致する＝**風を壊さない**）。
    //
    //   ## 葉の面（side）は回さない
    //   `side`（葉幅の方向）は yaw 由来のまま据え置く。曲げ方向だけを変えるので、
    //   物体が通過した瞬間に葉が回転してパチンと切り替わることがない。
    let interact_vel   = grass_interaction_velocity(inst.pos.xz);
    let interact_speed = length(interact_vel);
    var bend_vec = forward * theta_wind;
    if (interact_speed > GRASS_VEC_EPSILON) {
        //   ワールド XZ の押し方向を、株の up に直交する成分へ落とす。
        //   斜面では XZ 方向がそのままだと地面へめり込む向きを含むため。
        let push_world = vec3<f32>(interact_vel.x, 0.0, interact_vel.y) / interact_speed;
        let push       = grass_safe_normalize(push_world - up * dot(push_world, up), forward);
        let theta_interact = min(interact_speed * u_interaction.bend_per_speed,
                                 u_interaction.max_bend);
        bend_vec = bend_vec + push * theta_interact;
    }
    //   合成後の曲げ方向と曲げ角（角度は必ず 0 以上。向きが符号を担う）。
    let bend_dir  = grass_safe_normalize(bend_vec, forward);
    let theta_max = min(length(bend_vec), GRASS_MAX_BEND_ANGLE);

    // ── 6. 葉の芯線を「弧長保存」で積分する ──
    //   ## なぜ単純な X オフセットではないのか
    //   `pos.x += bend * h*h` のように先端を横へずらすと、葉の全長が
    //   sqrt(h^2 + offset^2) 倍に **伸びてしまう**（強風で草が伸びる不自然な絵になる）。
    //   そこで「オフセットを足す」のではなく「接線を回す」。
    //   接線 T(s) = up*cos(θ(s)) + forward*sin(θ(s)) は常に単位長なので、
    //   芯線を T(s) の積分で作れば全長は厳密に blade_len のままになる。
    //   θ(s) = theta_max * s^2 の積分は閉形式（Fresnel 積分）になり高価なので、
    //   セグメント境界ごとに中点則で数値積分する（分割数は高々 GRASS_MAX_SEGMENTS=8）。
    //   これは離散的には**厳密に**弧長保存である（各ステップの寄与長が必ず ds のため）。
    let blade_len = u_grass.height * inst.scale;
    let ds        = blade_len / f32(seg_count);

    //   このコーナーが属する行（0=セグメント下端, 1=上端）。
    //   クワッド 6 頂点の並び:
    //     corner 0=(左,下) 1=(右,下) 2=(右,上) / 3=(左,下) 4=(右,上) 5=(左,上)
    //   → 反時計回り（Ccw）を保つ。ただし cull は None なので裏表は問わない。
    let is_top   = (corner == 2u) || (corner == 4u) || (corner == 5u);
    let is_right = (corner == 1u) || (corner == 2u) || (corner == 4u);
    let row      = segment + select(0u, 1u, is_top);

    //   芯線の積分（row ステップぶん）。
    var center = inst.pos;
    for (var i: u32 = 0u; i < row; i = i + 1u) {
        // 中点則: セグメント i の中央での接線を、その区間の代表方向とする。
        let s_mid = (f32(i) + 0.5) / f32(seg_count);
        let th    = grass_bend_angle(theta_max, s_mid);
        //   曲げ方向は風＋インタラクションの合成方向（bend_dir）。
        let tangent_mid = up * cos(th) + bend_dir * sin(th);
        center = center + tangent_mid * ds;
    }

    //   この行の高さパラメータと接線（法線の再計算に使う）。
    let height_t = f32(row) / f32(seg_count);
    let theta_r  = grass_bend_angle(theta_max, height_t);
    let tangent  = up * cos(theta_r) + bend_dir * sin(theta_r);

    // ── 7. 幅方向へ広げる（根元 → 穂先でテーパー）──
    //   幅にも scale を掛ける。高さだけ伸ばすと株ごとに葉の縦横比が変わって
    //   引き伸ばされた見た目になるため、相似変形にする。
    let width_root = u_grass.width * inst.scale;
    let half_width = mix(width_root, width_root * GRASS_TIP_WIDTH_RATIO, height_t) * 0.5;
    let side_t     = select(-1.0, 1.0, is_right);
    //   遠距離のサブピクセル落ち（ジャギ・点滅・まばら）対策で、葉の半幅を
    //   スクリーン空間で最低 1 画素弱まで確保する（近距離は無変化）。
    //   left/right・top/bottom 頂点は同じ center・half_width から同じ倍率を得るので
    //   対称に太り、四角形に亀裂は入らない。
    let half_width_eff = grass_min_screen_half_width(center, side, half_width);
    let world_pos  = center + side * (side_t * half_width_eff);

    // ── 8. 曲げ後の法線を作り直す ──
    //   葉面は「side（幅方向・曲げても不変）」と「tangent（曲げ後の芯線方向）」が張る平面。
    //   したがって面法線は cross(side, tangent)。
    //   風だけのときは tangent が up-forward 平面内にあり side はその平面に直交するため
    //   外積は単位長だったが、**インタラクションの曲げ方向は side と直交とは限らない**
    //   （任意のワールド方向へ倒れる）。外積が単位長でなくなるので正規化する。
    //   曲げ方向が side と平行な退化ケース（真横へ倒れる）では外積がゼロ長になるため、
    //   up へフォールバックする（黒く落ちるより上向きの方が破綻が小さい）。
    let world_normal = grass_safe_normalize(cross(side, tangent), up);

    var out: GrassVsOut;
    let world_pos4     = vec4<f32>(world_pos, 1.0);
    out.clip_position  = u_camera.view_proj * world_pos4;
    // 速度バッファ用のクリップ座標 2 本（草は静的扱い＝カメラ由来の速度のみ）。
    out.curr_clip      = out.clip_position;
    out.prev_clip      = u_camera.prev_view_proj * world_pos4;
    out.world_position = world_pos;
    out.world_normal   = world_normal;
    out.height_t       = height_t;
    out.side_t         = side_t;
    //   地表法線（＝株の up）。フラグメント段の「法線の地表寄せ」で使う。
    //   up は既に grass_safe_normalize 済みなので単位長が保証されている。
    out.ground_normal  = up;
    //   株ごとの明度ゆらぎ（1.0 中心 ± GRASS_TINT_VARIATION）。
    //   位相用ハッシュとは別の種（seed を反転）にして、位相と明度の相関を切る。
    out.tint = 1.0 + (grass_hash_f32(~inst.seed) - 0.5) * 2.0 * GRASS_TINT_VARIATION;
    return out;
}

// ============================================================
//  フラグメントシェーダ（MRT 5 枚）
// ============================================================

/// 草の G-Buffer 出力（gbuffer_write.wgsl の GBufferOut と同一レイアウト）。
struct GrassGBufferOut {
    @location(0) albedo_occ: vec4<f32>,
    @location(1) normal:     vec4<f32>,
    @location(2) mr:         vec4<f32>,
    @location(3) emissive:   vec4<f32>,
    /// RT4: スクリーンスペース速度（前フレーム→今フレームの UV 移動量）。
    @location(4) velocity:   vec2<f32>,
}

@fragment
fn fs_grass(
    in: GrassVsOut,
    /// 表裏判定。草平面は cull None で描くため、裏面も必ず来る。
    @builtin(front_facing) front_facing: bool,
) -> GrassGBufferOut {
    // ── 1. 穂先のアルファカットアウト ──
    //   幾何の幅テーパーだけでは穂先が「平らに切り落とされた」形になる。
    //   穂先付近だけ葉幅の外側を discard して、先端を尖らせる。
    //   tip_fade: GRASS_TIP_FADE_START 未満では 0 → tip_mask が必ず 1.0 になり
    //   根元側は絶対に discard されない（葉に穴が空くのを構造的に防ぐ）。
    let tip_fade = smoothstep(GRASS_TIP_FADE_START, 1.0, in.height_t);
    let tip_mask = 1.0 - tip_fade * abs(in.side_t);
    if (u_grass.tip_alpha_cutoff > 0.0 && tip_mask < u_grass.tip_alpha_cutoff) {
        discard;
    }

    // ── 2. アルベド（根元 → 穂先のグラデーション × 株ごとの明度ゆらぎ）──
    let albedo = mix(u_grass.color_bottom, u_grass.color_top, in.height_t) * in.tint;

    // ── 3. オクルージョン近似 ──
    //   草むらの根元は周囲の葉に囲まれて暗い。高さで線形補間するだけの安価な近似
    //   （本物の AO は後段の SSAO / RT-AO パスがさらに乗せる）。
    let occlusion = mix(GRASS_ROOT_OCCLUSION, 1.0, in.height_t);

    // ── 4. 両面法線 ──
    //   cull None なので裏面フラグメントが必ず来る。裏面では頂点で作った面法線が
    //   視線と逆向き（＝カメラから見て裏返し）になり、そのまま G-Buffer へ焼くと
    //   ライティングパスが「光の当たらない面」と誤判定して真っ黒になる。
    //   front_facing を見て反転し、常にカメラ側を向いた法線を書き込む。
    //   （視線ベクトルとの内積ではなく front_facing を使う理由: ラスタライザの
    //     巻き方向判定と厳密に一致するため、シルエット際でのちらつきが出ない。）
    let n = grass_safe_normalize(in.world_normal, GRASS_WORLD_UP);
    let facing_n = select(-n, n, front_facing);

    // ── 5. 法線の地表寄せ（normal flattening）──
    //
    //   ## なぜ必要か（これが無いと草が真っ黒になる）
    //   葉は板ポリなので、その**真の幾何法線**は `cross(side, tangent)` である。
    //   直立した葉では tangent ≒ up（+Y）で side は水平なので、幾何法線は
    //   **水平**を向く。太陽が高い位置にある平行光に対しては N·L ≒ 0 となり、
    //   葉はまったく照らされず黒く落ちる。ランダム yaw によってたまたま法線が
    //   太陽の方位を向いた株だけが明るくなるため、同じ深度・同じ姿勢の葉が
    //   「明るい緑」と「ほぼ黒」の 2 値にまだらに割れる（実測された症状）。
    //
    //   ## 対策
    //   陰影用の法線を、その株が生えている地表法線（＝ up）側へ寄せる。
    //   薄い葉を「その場所の地面の向きを持つ塊」として扱う近似であり、
    //   リアルタイム草の定番手法（normal flattening / spherical grass normals）。
    //   幾何は一切変えず、G-Buffer へ書く法線だけを曲げる。
    //   寄せ量は props.json の `grass.normal_up_blend` で完全にデータ駆動
    //   （0 = 従来どおり真の幾何法線、1 = 完全に地表法線）。
    //
    //   ## 適用順序が front_facing 反転の「後」である理由（重要）
    //   先に寄せてから反転すると、裏面フラグメントでは寄せ済み法線ごと符号が
    //   反転し、地表法線へ寄せたはずの成分が**地平線の下**を向いてしまう。
    //   結果として裏面だけが再び黒くなり、症状が半分残る。
    //   逆に「反転 → 寄せ」の順なら、常にカメラ側を向いた法線を起点に
    //   上方向へ寄せるので、表裏どちらのフラグメントでも上向き成分が残る。
    let ground_up = grass_safe_normalize(in.ground_normal, GRASS_WORLD_UP);
    let blend     = clamp(u_grass.normal_up_blend, 0.0, 1.0);
    let blended   = mix(facing_n, ground_up, blend);
    //   facing_n と ground_up がほぼ逆向き（＝真下から見上げた裏面など）だと
    //   mix の結果がゼロ長になり normalize が NaN を出す。その場合は
    //   地表法線そのものへ落とす（黒く落ちるより上向きの方が破綻が小さい）。
    let out_n = grass_safe_normalize(blended, ground_up);

    var o: GrassGBufferOut;
    o.albedo_occ = vec4<f32>(albedo, occlusion);
    // RT1.w=1: authored 法線フラグ。草は法線を地表向き（up）へ意図的に平坦化しているため、
    // deferred のライティングが深度から復元する幾何法線（刃の水平向き・深度不連続で暴れる）で
    // なく、この out_n を geo_gate の幾何法線として使わせる（草の影がドット状ノイズに壊れる不具合の
    // 根治。詳細は deferred_lighting.wgsl の GBUFFER_NORMAL_AUTHORED_THRESHOLD を参照）。
    o.normal     = vec4<f32>(out_n, GRASS_NORMAL_AUTHORED_FLAG);
    // metallic は常に 0（草は誘電体）。roughness は uniform 指定。
    // b = diffuse_transmission は 0（葉の逆光透けは将来拡張）。
    // a = user_data（汎用ユーザーデータ）は 0（草はマテリアル uniform を持たない）。
    o.mr         = vec4<f32>(0.0, clamp(u_grass.roughness, 0.0, 1.0), 0.0, 0.0);
    // .a = surface_id（セマンティックタグ | シェーディングモデル ID）。
    // 草はアクタではなくタグを持たず、シェーディングも DefaultPBR なのでパック値 0 が正しい。
    o.emissive   = vec4<f32>(0.0, 0.0, 0.0, 0.0);
    // スクリーンスペース速度（velocity_math.wgsl の定義に従う）。
    o.velocity   = compute_velocity_uv(in.curr_clip, in.prev_clip);
    return o;
}
