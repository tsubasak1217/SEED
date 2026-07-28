// ============================================================
//  water_surface.wgsl — 水面描画パス（Phase W1）
//
//  ## 役割（単一責任）
//  `ResolvedWaterVolume` から作られた水面クアッド（Ocean / Region）を 1 ドローで描き、
//  「シーン深度から求めた水の厚み」による吸収・岸フォーム・フレネル・スクリーンスペース屈折を
//  **このシェーダ内で最終合成**して HDR へ出力する。
//
//  ## 頂点バッファを持たない理由
//  水面のポリゴンは 1 インスタンス = 四角形 1 枚で、その 4 隅はパラメータから完全に決まる
//  （Ocean = カメラ追従の巨大クアッド／Region = AABB の上面／**川 = リボンの 1 分割**）。
//  よって頂点バッファは一切使わず、`@builtin(vertex_index)`(0..6) で角を生成し、
//  `@builtin(instance_index)` でストレージバッファのパラメータ配列を引く。
//  全水面が `draw(0..6, 0..N)` の 1 ドローで済む。
//
//  ## 川（Phase W4）
//  `center.w`（インスタンス種別）が川なら、頂点は矩形ではなく
//  「折れ線の隣り合う 2 ノード ± 断面法線 × 半幅」で作る（`water_river_vertex`）。
//  各ノードの Y をそのまま使うので、下る川は傾いた面になる。
//  さらに解析波のサンプル位置を上流へずらして**流れ**を作る（2 位相ブレンド。
//  `water_flow_*` を参照）。色・吸収・泡・屈折の経路は Ocean / Region と完全に共通である。
//
//  ## 深度の扱い（アタッチメント無し＋手動深度テスト）
//  本パスは **深度アタッチメントを一切持たない**（TOML の `no_depth = true`）。
//  理由: 共有深度テクスチャは他パスで「書き込み可能」としてアタッチされており、
//  同一パス内でアタッチメントとサンプルテクスチャに同時バインドすると
//  エイリアシング（read/write 競合）になる。アタッチメントを外し、DepthOnly ビューを
//  **サンプルテクスチャとして** group1 に受け取って `textureLoad` で読み、
//  「シーン深度 < 水面フラグメントの深度なら遮蔽 → discard」という手動深度テストを行う。
//  水面は元々深度を書かない（`depth_write = false` 相当）ため、挙動は通常の Z テストと等価。
//  同じ 1 回の深度サンプルから **水の厚み** も復元でき、吸収と岸フォームに使い回せる。
//
//  ## 屈折の背景
//  本パス直前に「シーン HDR をそのままコピーした 1 ミップのグラブテクスチャ」を作り、
//  それを `t_scene` として読む（refract_pyramid のようなブラーミップ鎖は水面には過剰なので作らない）。
//  グラブはメインパス・WBOIT 合成の後に取るため、スカイボックスも既存半透明も背景に含まれる。
//
//  ## 波
//  頂点変位は行わず（W1）、フラグメントで **解析的サイン波の微分から法線のみ**を合成する。
//  法線マップ画像などのテクスチャアセットには一切依存しない。
//
//  ## 波紋・航跡（Phase I2）
//  インタラクションフィールド（`interaction_field.wgsl` が更新するカメラ追従の俯瞰テクスチャ）の
//  `.z`（波の高さ）を group2 でサンプルし、
//    ・その **勾配**を解析波の勾配へ足して法線を摂動させる（＝波紋の輪・航跡の筋）
//    ・**高さの絶対値**がしきい値を超えた所へ既存の岸フォームと同じ `foam_color` を乗せる
//      （＝航跡の白い泡）
//  場は書き手（InteractionSource）と完全に分離されており、本シェーダは **読むだけ**。
//  場の窓（64m）の外は影響ゼロ。
//
//  ## 岸波（Phase W1.5）
//  水域ごとに CPU で焼いた **ショアフィールド**（俯瞰 2D。水深／符号付き岸距離／岸方向）を
//  group1 の配列テクスチャからサンプルし、
//    ・うねり帯 … 位相 =(岸距離/波長 + 時間/周期) の周期波。浅くなるほど成長し沖では 0
//    ・砕け泡   … 振幅/水深がしきい値を超えた所へ既存 foam_color を乗せる
//    ・打ち上げ … 岸線付近の薄い泡帯が周期で前後する
//  を作る。流体シミュレーションは一切していない。ランタイムのコストは
//  **テクスチャ 1 サンプル＋数式**だけで、フィールドの焼き直しは地形編集時のみ。
//
//  ## 合成された高さ場（W5.1 の合流点）
//  解析波・波紋・岸波の 3 ソースは `water_surface_height` /
//  `water_surface_gradient` に集約してある。W5.1（頂点変位の大波）は
//  頂点シェーダから前者を呼ぶだけでフラグメントと同じ高さ場を共有できる。
//
//  ## バインディング規約
//    group 0: カメラ（CameraUniform, binding 0）
//    group 1: binding0 = 水パラメータ配列（storage, read）
//             binding1 = シーンカラーのグラブ（屈折の背景）
//             binding2 = そのサンプラー（線形・ClampToEdge）
//             binding3 = シーン深度（DepthOnly ビュー。textureLoad のみ）
//             binding4 = ショアフィールド配列（W1.5。Rgba16Float・水域ごとに 1 レイヤ）
//             binding5 = そのサンプラー（線形・ClampToEdge）
//    group 2: binding0 = インタラクションフィールド（波紋。Rgba16Float）
//             binding1 = そのサンプラー（線形・ClampToEdge）
//             binding2 = 場のパラメータ UBO（窓原点・窓幅の逆数・テクセルサイズ）
// ============================================================

// ─── Group 0: カメラ ─────────────────────────────────────────
//
// `shader_common.wgsl` は連結しない（マテリアル group2 等を要求してしまうため）。
// deferred_lighting.wgsl と同じ方針で、Rust 側 `uniforms::CameraUniform`（304 bytes）と
// 1:1 対応する同一レイアウトの構造体をここで自前宣言する。
// フィールドを 1 つでも増減すると全パスのカメラ BindGroup と食い違うため、
// Rust 側 / shader_common.wgsl / deferred_lighting.wgsl と併せて同期すること。
struct CameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    /// ゲーム内累計時間（秒）。波の位相スクロールに使う（Play 非ポーズ時のみ進む）。
    time:           f32,
    /// レンダーターゲット全面の解像度（ピクセル）。グラブテクスチャのアドレッシングに使う。
    resolution:     vec2<f32>,
    _pad2:          vec2<f32>,
    /// 逆 ViewProjection（シーン深度 → ワールド座標の復元用）。
    inv_view_proj:  mat4x4<f32>,
    /// 速度バッファ用の前フレーム ViewProjection（本パスでは未使用。オフセット合わせ）。
    prev_view_proj: mat4x4<f32>,
    /// フレームバッファ基準のビューポート矩形 (x, y, w, h)。
    /// NDC はこの矩形へ写像されるため、深度 → ワールド復元はこれで正規化する
    /// （Play のレターボックス時に RT 全面で正規化すると復元座標が横滑りする）。
    viewport:       vec4<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: 水パラメータ＋背景＋深度 ────────────────────────

/// 水ボリューム 1 個ぶんの GPU パラメータ。
/// Rust 側 `renderer::water::params::WaterParams` と **フィールド順・オフセットを厳密一致**させること
/// （vec4 だけで構成し、std430 のアラインメント問題を構造的に起こさないようにしてある）。
struct WaterParams {
    /// xyz = 水面クアッド中心のワールド座標（y = 水面 Y）／w = 未使用
    center:           vec4<f32>,
    /// x,z = クアッドの片側半径（m）／y,w = 未使用
    half_extent:      vec4<f32>,
    /// rgb = 浅場の色／a = 深色へ収束するまでの水中距離（m）
    shallow_color:    vec4<f32>,
    /// rgb = 深場の色／a = 深場での最大不透明度（0..1）
    deep_color:       vec4<f32>,
    /// rgb = 岸フォームの色／a = フォームが出る水深（m）
    foam_color:       vec4<f32>,
    /// rgb = 簡易反射色／a = フォームの強度（0..1）
    reflection_color: vec4<f32>,
    /// x = 波の振幅／y = 空間周波数（1/m）／z = スクロール速度／w = 屈折 UV の最大歪み（画面比）
    wave:             vec4<f32>,
    /// x = フレネル指数／y = フレネル寄与率／
    /// z = 波紋の法線摂動スケール（I2）／w = 波紋フォームの波高しきい値（I2）
    fresnel:          vec4<f32>,
    /// x = ピッキング用 raw アクタ ID（本シェーダでは未使用。ID パス `water_id.wgsl` が読む）。
    /// 配列ストライドを Rust 側 `WaterParams` と一致させるため宣言だけしておく。
    actor_id:         vec4<u32>,
    /// 岸波（Phase W1.5）。
    /// x = 強さ（0 で完全無効）／y = うねりの波長（m）／z = うねりの周期（秒）／w = 泡量（0..1）
    shore:            vec4<f32>,
    /// 岸波のショアフィールド窓（Phase W1.5）。
    /// x,y = 窓のワールド XZ 最小／z = 窓一辺の逆数（1/m）／
    /// **w = 配列テクスチャのレイヤ番号。負値 = この水域にショアフィールドが無い**
    shore_field:      vec4<f32>,
    /// 川リボン 1 分割の上流ノード（Phase W4）。xyz = ワールド座標／w = リボンの半幅（m）
    river_p0:         vec4<f32>,
    /// 川リボン 1 分割の下流ノード（Phase W4）。xyz = ワールド座標／w = 流速（m/s）
    river_p1:         vec4<f32>,
    /// 川リボンの断面法線（Phase W4）。x,y = 上流ノード／z,w = 下流ノード（XZ・マイター込み）
    river_normal:     vec4<f32>,
}

@group(1) @binding(0) var<storage, read> u_water: array<WaterParams>;
@group(1) @binding(1) var t_scene: texture_2d<f32>;
@group(1) @binding(2) var s_scene: sampler;
@group(1) @binding(3) var t_depth: texture_depth_2d;
/// ショアフィールド（Phase W1.5）。水域ごとに 1 レイヤ。
/// チャネル: x = 水深(m。負は陸) / y = 符号付き岸距離(m。正は沖) / zw = 岸方向(単位 XZ)
@group(1) @binding(4) var t_shore: texture_2d_array<f32>;
@group(1) @binding(5) var s_shore: sampler;

// ─── Group 2: インタラクションフィールド（波紋・航跡。Phase I2）────

/// 場の更新／消費で共有するパラメータ UBO。
///
/// Rust `InteractionFieldUniformGpu` および `interaction_field.wgsl` /
/// `grass_gbuffer.wgsl` の同名構造体と **フィールド順まで一致必須**
/// （4 箇所を同時に直すこと。テスト `interaction_uniform_fields_match_grass_shader` が照合する）。
struct InteractionFieldUniform {
    /// 今フレームの窓のワールド XZ 最小（テクセル単位にスナップ済み）。
    origin_xz:      vec2<f32>,
    /// 前フレームの窓のワールド XZ 最小（更新パスのみ使用）。
    prev_origin_xz: vec2<f32>,
    /// 1 テクセルのワールドサイズ（m）。**波紋の勾配を取る幅として使う。**
    texel_size:     f32,
    /// 窓の一辺の逆数（1/m）。ワールド XZ → [0,1] UV 変換に使う。
    inv_extent:     f32,
    /// 減衰係数（更新パスのみ使用）。
    decay:          f32,
    /// 場の一辺の解像度（更新パスのみ使用）。
    resolution:     u32,
    /// 有効なソース数（更新パスのみ使用）。
    source_count:   u32,
    /// 速度 1 m/s あたりの草の曲げ角（草のみ使用）。
    bend_per_speed: f32,
    /// 草の曲げ角の上限（草のみ使用）。
    max_bend:       f32,
    /// 波の伝播係数（更新パスのみ使用）。
    wave_k:         f32,
    /// 1 サブステップぶんの波の減衰係数（更新パスのみ使用）。
    wave_damp:      f32,
    /// パディング（未使用）。
    _pad0:          f32,
    _pad1:          f32,
    _pad2:          f32,
}
@group(2) @binding(0) var  t_interaction:     texture_2d<f32>;
@group(2) @binding(1) var  s_interaction:     sampler;
@group(2) @binding(2) var<uniform> u_interaction: InteractionFieldUniform;

// ─── 定数（マジックナンバー禁止のため全て命名する）───────────

/// クアッド 1 枚あたりの頂点数（三角形 2 枚 = 6 頂点）。
const WATER_QUAD_VERTEX_COUNT: u32 = 6u;

/// 重ね合わせるサイン波の層数。
const WAVE_LAYER_COUNT: u32 = 4u;

/// 「シーン深度がここ以上なら遠クリップ（＝空／何も無い）」とみなす閾値。
/// 深度は `Clear(1.0)` 起点・比較 LessEqual の通常 Z なので、1.0 近傍が空にあたる。
const WATER_SKY_DEPTH_THRESHOLD: f32 = 0.999999;

/// 空に対する水の厚み（m）。実際には背景が無限遠なので「十分深い」として扱う。
const WATER_SKY_THICKNESS: f32 = 1.0e4;

/// ゼロ除算回避の下限値（吸収距離・フォーム幅など、ユーザ入力が 0 になり得るもの）。
const WATER_EPSILON: f32 = 1.0e-4;

/// 波紋の勾配を水面法線へ足すときの基準倍率（無次元）。
///
/// 場の波高（m 相当）の勾配は「1 テクセル(0.125m)あたりの高さ差 / 距離」であり、
/// そのまま足すと弱すぎる。ユーザ調整値 `ripple_strength`(=1 が標準) に掛かる係数として、
/// 「走ったときの航跡が解析波と同程度に見える」倍率を定数化する。
const WATER_RIPPLE_NORMAL_SCALE: f32 = 6.0;

/// 波紋フォームが「しきい値超過ぶん」で完全に白くなるまでの幅（しきい値に対する倍率）。
///
/// しきい値ちょうどで泡が突然出るとバンド状の縁が見えるため、
/// しきい値の 1 倍ぶんかけて滑らかに立ち上げる。
const WATER_RIPPLE_FOAM_RAMP: f32 = 1.0;

/// 波紋フォームの最大濃度（0..1）。既存の岸フォーム（foam_intensity）と重ねても
/// 白飛びしないよう、単体では 1.0 まで行かせない。
const WATER_RIPPLE_FOAM_MAX: f32 = 0.85;

/// 屈折 UV の有効範囲（画面外を舐めないようにクランプする）。
const WATER_UV_MIN: f32 = 0.0;
const WATER_UV_MAX: f32 = 1.0;

/// 波の層ごとの周波数倍率（互いに無理数比に近い値にしてタイル感を消す）。
const WAVE_FREQ_MUL_0: f32 = 1.0;
const WAVE_FREQ_MUL_1: f32 = 2.13;
const WAVE_FREQ_MUL_2: f32 = 3.71;
const WAVE_FREQ_MUL_3: f32 = 5.29;

/// 波の層ごとの振幅倍率（高周波ほど小さく＝1/f 的なスペクトル）。
const WAVE_AMP_MUL_0: f32 = 1.0;
const WAVE_AMP_MUL_1: f32 = 0.5;
const WAVE_AMP_MUL_2: f32 = 0.25;
const WAVE_AMP_MUL_3: f32 = 0.125;

/// 波の層ごとのスクロール速度倍率。
const WAVE_SPEED_MUL_0: f32 = 1.0;
const WAVE_SPEED_MUL_1: f32 = 1.37;
const WAVE_SPEED_MUL_2: f32 = 0.83;
const WAVE_SPEED_MUL_3: f32 = 1.71;

// ─── 岸波の定数（Phase W1.5。すべてエンジン側の固定値）───────
//
// ユーザが触るのは WaterVolumeComponent の 4 パラメータ（強さ・波長・周期・泡量）だけで、
// 波形の性質を決める以下の比率はエンジンが持つ。

/// 円周（2π）。位相計算で使う。
const WATER_TAU: f32 = 6.28318530718;

/// うねりの振幅を波長から決める比（無次元）。振幅 = 波長 × この値。
///
/// 実際の浜のうねりの波形勾配（波高/波長 ≒ 1/30〜1/15）に合わせてある。
/// 波高を独立パラメータにしないのは、波長と切り離すと簡単に非物理な
/// 「短い波長で巨大な波高」になり、法線が破綻するため。
const SHORE_AMPLITUDE_TO_WAVELENGTH: f32 = 0.03;

/// 深水とみなす水深の波長比（水深 > 波長 × この値 なら沖）。
/// 線形波理論の深水条件 h > L/2 をそのまま採る。
const SHORE_DEEPWATER_DEPTH_RATIO: f32 = 0.5;

/// 浅水変形（shoaling）の利得上限（無次元）。
/// Green の法則は h→0 で発散するので、目視で自然な範囲で頭打ちにする。
const SHORE_MAX_SHOAL_GAIN: f32 = 2.5;

/// 浅水変形の計算に使う水深の下限（m）。0 除算と発散の防止。
const SHORE_MIN_DEPTH_M: f32 = 0.15;

/// Green の法則の指数（振幅 ∝ 水深^(−1/4)）。
const SHORE_SHOAL_EXPONENT: f32 = 0.25;

/// 陸側で岸波を消しきるフェード幅（m）。水深 0 から −この値 の間で振幅 0 になる。
const SHORE_LAND_FADE_M: f32 = 0.5;

/// 砕波とみなす「振幅 / 水深」比。
/// 実海岸の砕波指標（波高/水深 ≒ 0.78、振幅換算で約 0.39）よりやや高めに取り、
/// 「ほんとうに浅い所だけが白くなる」ようにしてある。
const SHORE_BREAK_RATIO: f32 = 0.55;

/// 砕波泡がしきい値超過ぶんで最大濃度に達するまでの幅（しきい値に対する倍率）。
const SHORE_BREAK_RAMP: f32 = 0.6;

/// 打ち上げ（swash）の泡帯が岸線から前後する振れ幅（波長比）。
const SHORE_SWASH_TRAVEL_RATIO: f32 = 0.35;

/// 打ち上げの泡帯の厚み（波長比）。
const SHORE_SWASH_BAND_RATIO: f32 = 0.12;

/// 打ち上げの位相遅れ（周期比）。
/// うねりが着岸してから泡が伸び上がるまでのずれ。0 だと「波の峰と泡が同時」で機械的に見える。
const SHORE_SWASH_PHASE_LAG: f32 = 0.25;

/// 岸波フォームの最大濃度（0..1）。既存の岸フォーム・航跡フォームと重ねても白飛びしない上限。
const SHORE_FOAM_MAX: f32 = 0.9;

/// 岸方向が「岸情報なし」（長さ 0）かを判定するしきい値（二乗長）。
const SHORE_DIR_EPSILON: f32 = 1.0e-6;

/// ショアフィールドが無い／窓外のときに返す水深・岸距離（m）。
/// 十分深い＝岸波の振幅が 0 になる値であればよい。
const SHORE_NO_FIELD_DEPTH: f32 = 1.0e4;

// ─── 川（Phase W4）の定数 ────────────────────────────────────

/// インスタンス種別のしきい値。`center.w` がこれ以上なら川リボンの 1 分割。
/// Rust 側 `WATER_INSTANCE_QUAD`(0) / `WATER_INSTANCE_RIVER`(1) の中間値。
const WATER_INSTANCE_RIVER_MIN: f32 = 0.5;

/// 流れの 2 位相ブレンドの 1 位相の長さ（秒）。
///
/// 川面の模様は「流れに乗って下流へ運ばれる」＝サンプル位置を上流側へずらすことで作るが、
/// ずらし量を無限に増やし続けると模様が永久に同じ形で平行移動するだけになり、
/// 「水面が生まれ変わりながら流れる」感じが出ない。そこで **標準的なフローマップ手法**に従い、
/// 半周期ずれた 2 つの位相を作って三角波でクロスフェードする。
/// この値が短いほど模様の入れ替わりが速く（＝せわしなく）、長いほど平行移動に近づく。
/// 4 秒は「流速 1.5m/s で 6m ぶん流れてから入れ替わる」程度で、川幅 4m に対して自然に見える。
const WATER_FLOW_PHASE_PERIOD: f32 = 4.0;

/// 2 位相ブレンドの位相差（周期比）。半周期ずらすのが標準。
const WATER_FLOW_PHASE_OFFSET: f32 = 0.5;

/// 波の層ごとの進行方向（XZ 平面。単位ベクトル）。
const WAVE_DIR_0: vec2<f32> = vec2<f32>( 1.0,  0.0);
const WAVE_DIR_1: vec2<f32> = vec2<f32>( 0.0,  1.0);
const WAVE_DIR_2: vec2<f32> = vec2<f32>( 0.70710678, 0.70710678);
const WAVE_DIR_3: vec2<f32> = vec2<f32>(-0.70710678, 0.70710678);

// ─── ヘルパー ────────────────────────────────────────────────

/// クアッドの角オフセット（-1..1 の XZ）を頂点インデックスから生成する。
/// 0,1,2 / 3,4,5 の 2 三角形。カリングは None なので巻き方向は問わない。
fn water_quad_corner(vi: u32) -> vec2<f32> {
    // 三角形1: 左下 → 右下 → 右上 ／ 三角形2: 左下 → 右上 → 左上
    if (vi == 0u) { return vec2<f32>(-1.0, -1.0); }
    if (vi == 1u) { return vec2<f32>( 1.0, -1.0); }
    if (vi == 2u) { return vec2<f32>( 1.0,  1.0); }
    if (vi == 3u) { return vec2<f32>(-1.0, -1.0); }
    if (vi == 4u) { return vec2<f32>( 1.0,  1.0); }
    return vec2<f32>(-1.0, 1.0);
}

/// 波レイヤ i の進行方向。
fn wave_dir(i: u32) -> vec2<f32> {
    if (i == 0u) { return WAVE_DIR_0; }
    if (i == 1u) { return WAVE_DIR_1; }
    if (i == 2u) { return WAVE_DIR_2; }
    return WAVE_DIR_3;
}

/// 波レイヤ i の周波数倍率。
fn wave_freq_mul(i: u32) -> f32 {
    if (i == 0u) { return WAVE_FREQ_MUL_0; }
    if (i == 1u) { return WAVE_FREQ_MUL_1; }
    if (i == 2u) { return WAVE_FREQ_MUL_2; }
    return WAVE_FREQ_MUL_3;
}

/// 波レイヤ i の振幅倍率。
fn wave_amp_mul(i: u32) -> f32 {
    if (i == 0u) { return WAVE_AMP_MUL_0; }
    if (i == 1u) { return WAVE_AMP_MUL_1; }
    if (i == 2u) { return WAVE_AMP_MUL_2; }
    return WAVE_AMP_MUL_3;
}

/// 波レイヤ i の速度倍率。
fn wave_speed_mul(i: u32) -> f32 {
    if (i == 0u) { return WAVE_SPEED_MUL_0; }
    if (i == 1u) { return WAVE_SPEED_MUL_1; }
    if (i == 2u) { return WAVE_SPEED_MUL_2; }
    return WAVE_SPEED_MUL_3;
}

/// 解析的サイン波の重ね合わせによる水面高さ場の **勾配** (∂h/∂x, ∂h/∂z) を求める。
///
/// 高さ場 h(p) = Σ_k A_k * sin(dot(d_k, p) * f_k + t * s_k) の解析微分（cos）。
/// 法線ではなく勾配を返すのは、**波紋（I2）の勾配と足し合わせてから 1 回だけ
/// 正規化する**ため（法線を 2 つ作って混ぜるより、勾配の加算の方が物理的に正しい：
/// 高さ場の重ね合わせ = 勾配の重ね合わせ）。
fn water_wave_gradient(p: vec2<f32>, amplitude: f32, scale: f32, speed: f32, t: f32) -> vec2<f32> {
    var grad = vec2<f32>(0.0, 0.0);
    for (var i: u32 = 0u; i < WAVE_LAYER_COUNT; i = i + 1u) {
        let dir   = wave_dir(i);
        let freq  = scale * wave_freq_mul(i);
        let amp   = amplitude * wave_amp_mul(i);
        let phase = dot(dir, p) * freq + t * speed * wave_speed_mul(i);
        // d/dp [ A sin(dot(d,p) f + ...) ] = A f cos(...) * d
        grad = grad + dir * (amp * freq * cos(phase));
    }
    return grad;
}

/// 解析的サイン波の重ね合わせによる水面高さ場の **高さ**（m）。
///
/// `water_wave_gradient` と同じ層構成の原関数（sin）。W5.1（頂点変位）が
/// 頂点段で高さを必要とするため、勾配と対で持たせてある。
/// フラグメントの法線計算では勾配のみを使うので、こちらは呼ばれない場合もある。
fn water_wave_height(p: vec2<f32>, amplitude: f32, scale: f32, speed: f32, t: f32) -> f32 {
    var h = 0.0;
    for (var i: u32 = 0u; i < WAVE_LAYER_COUNT; i = i + 1u) {
        let dir   = wave_dir(i);
        let freq  = scale * wave_freq_mul(i);
        let amp   = amplitude * wave_amp_mul(i);
        let phase = dot(dir, p) * freq + t * speed * wave_speed_mul(i);
        h = h + amp * sin(phase);
    }
    return h;
}

/// 高さ場の勾配から水面法線（ワールド空間・上向き基準）を作る。
/// N = normalize(-∂h/∂x, 1, -∂h/∂z)。頂点変位は行わないので「法線だけの波」。
fn water_normal_from_gradient(grad: vec2<f32>) -> vec3<f32> {
    return normalize(vec3<f32>(-grad.x, 1.0, -grad.y));
}

/// インタラクションフィールドの波の高さ（`.z`）を、ワールド XZ でサンプルする。
///
/// **窓の外は必ず 0**（＝波紋の影響なし）。ClampToEdge サンプラーのままだと窓の縁の値が
/// 外側へ無限に引き伸ばされ、水域全体が縁の波紋で染まる（草側と同じ扱い）。
fn water_ripple_height(world_xz: vec2<f32>) -> f32 {
    let uv = (world_xz - u_interaction.origin_xz) * u_interaction.inv_extent;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
        return 0.0;
    }
    return textureSampleLevel(t_interaction, s_interaction, uv, 0.0).z;
}

/// 波紋の高さ場の勾配 (∂h/∂x, ∂h/∂z) を中央差分で求める（ワールド m あたり）。
///
/// 場はテクセル 0.125m の離散データなので解析微分できない。1 テクセル離れた
/// 2 点の差を 2 テクセル幅で割る中央差分が、ノイズに強く実装も最小。
/// 4 タップぶんのサンプルコストは、水面フラグメントに対して十分安い。
fn water_ripple_gradient(world_xz: vec2<f32>) -> vec2<f32> {
    let d = max(u_interaction.texel_size, WATER_EPSILON);
    let hx1 = water_ripple_height(world_xz + vec2<f32>(d, 0.0));
    let hx0 = water_ripple_height(world_xz - vec2<f32>(d, 0.0));
    let hz1 = water_ripple_height(world_xz + vec2<f32>(0.0, d));
    let hz0 = water_ripple_height(world_xz - vec2<f32>(0.0, d));
    return vec2<f32>(hx1 - hx0, hz1 - hz0) / (2.0 * d);
}

// ─── 岸波（ショアフィールド。Phase W1.5）───────────────────────
//
// 岸波は流体シミュレーションではなく、**「岸までの距離・岸の方向・水深」の場**
// （ショアフィールド。CPU で焼いて `t_shore` に置いてある）から作る
// プロシージャル波帯である。1 サンプル＋数式だけで、
//   ・うねり帯 … 岸へ向かって進む周期波。浅くなるほど成長し、沖では消える
//   ・砕け泡   … 振幅/水深がしきい値を超えた所を白くする
//   ・打ち上げ … 岸線付近の薄い泡帯が周期で前後する
// を出す。

/// ショアフィールドの 1 サンプル。
struct ShoreSample {
    /// 水深（m）。**負は陸**。
    depth:    f32,
    /// 符号付き岸距離（m）。**正 = 沖（水側）／負 = 陸側**。
    distance: f32,
    /// 岸方向（単位 XZ。そのテクセルから最寄りの岸を指す）。
    /// **長さ 0 は「岸情報なし」**（窓外・窓内に岸が無い）を意味する。
    dir:      vec2<f32>,
}

/// ショアフィールドをワールド XZ でサンプルする。
///
/// レイヤ番号が負（＝この水域にフィールドが無い）か、窓の外なら
/// 「十分深い沖・岸情報なし」を返す。**テクスチャサンプルも行わない**ので、
/// 岸波を使わない水域のコストは W1/I2 と完全に同じになる。
fn water_shore_sample(p: WaterParams, world_xz: vec2<f32>) -> ShoreSample {
    var s: ShoreSample;
    s.depth    = SHORE_NO_FIELD_DEPTH;
    s.distance = SHORE_NO_FIELD_DEPTH;
    s.dir      = vec2<f32>(0.0, 0.0);
    if (p.shore_field.w < 0.0) {
        return s;
    }
    let uv = (world_xz - p.shore_field.xy) * p.shore_field.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
        return s;
    }
    let t = textureSampleLevel(t_shore, s_shore, uv, i32(p.shore_field.w), 0.0);
    s.depth    = t.x;
    s.distance = t.y;
    s.dir      = t.zw;
    return s;
}

/// うねりの振幅（m）。浅水変形（shoaling）で岸に近づくほど成長し、沖と陸で 0 になる。
///
/// 包絡線は 3 つの積:
///   ・`offshore` … 深水（h > L/2）で 0 へ落とす。外洋には岸波を出さない
///   ・`shoal`    … Green の法則 A ∝ h^(−1/4)。浅くなるほど波が立ち上がる
///   ・`land`     … 水深が負（陸）の側で 0 にする
fn water_shore_amplitude(p: WaterParams, s: ShoreSample) -> f32 {
    // 岸情報が無い（窓外・窓内に岸が無い）なら波は立てない。
    if (dot(s.dir, s.dir) < SHORE_DIR_EPSILON || p.shore.x <= 0.0) {
        return 0.0;
    }
    let wavelength = p.shore.y;
    let base       = wavelength * SHORE_AMPLITUDE_TO_WAVELENGTH;
    let deep       = wavelength * SHORE_DEEPWATER_DEPTH_RATIO;
    let offshore   = 1.0 - smoothstep(0.0, deep, s.depth);
    let shoal      = clamp(
        pow(deep / max(s.depth, SHORE_MIN_DEPTH_M), SHORE_SHOAL_EXPONENT),
        1.0, SHORE_MAX_SHOAL_GAIN,
    );
    let land = smoothstep(-SHORE_LAND_FADE_M, 0.0, s.depth);
    return p.shore.x * base * offshore * shoal * land;
}

/// うねりの位相（ラジアン）。
///
/// 位相 = 2π ( 岸距離 / 波長 + 時間 / 周期 )
///
/// **時間項の符号**: 岸距離は「沖が正」なので、位相が一定の点（＝波の峰）が
/// 岸へ進むには距離が時間とともに **減る** 必要がある。すなわち
/// `距離/波長 + 時間/周期 = 一定` ⇒ `距離 = −波長×時間/周期 + 一定` となる
/// **同符号**が正しい（差にすると波が沖へ逃げていく）。
fn water_shore_phase(p: WaterParams, s: ShoreSample, t: f32) -> f32 {
    return WATER_TAU * (s.distance / p.shore.y + t / p.shore.z);
}

/// 岸波の高さ（m）。**W5.1（頂点変位）はこの関数をそのまま頂点段で呼べる。**
fn water_shore_height(p: WaterParams, world_xz: vec2<f32>, t: f32) -> f32 {
    let s = water_shore_sample(p, world_xz);
    return water_shore_amplitude(p, s) * sin(water_shore_phase(p, s, t));
}

/// 岸波の高さ場の勾配 (∂h/∂x, ∂h/∂z)。
///
/// 岸距離は距離関数なので `∇(岸距離)` は長さ 1・**岸から遠ざかる向き**、
/// すなわち `−dir` である。したがって搬送波の微分は
/// `A·cos(位相) · 2π/波長 · (−dir)` になる。
/// 振幅（包絡線）の空間変化は波長スケールに比べてはるかに緩やかなので、
/// その微分は無視する（＝包絡線を局所定数とみなす標準的な近似）。
fn water_shore_gradient(p: WaterParams, world_xz: vec2<f32>, t: f32) -> vec2<f32> {
    let s   = water_shore_sample(p, world_xz);
    let amp = water_shore_amplitude(p, s);
    if (amp <= 0.0) {
        return vec2<f32>(0.0, 0.0);
    }
    let k = WATER_TAU / p.shore.y;
    return (-s.dir) * (amp * k * cos(water_shore_phase(p, s, t)));
}

/// 岸波の泡（0..1）。砕け泡と打ち上げ（swash）の強い方を採る。
fn water_shore_foam(p: WaterParams, world_xz: vec2<f32>, t: f32) -> f32 {
    let s = water_shore_sample(p, world_xz);
    if (dot(s.dir, s.dir) < SHORE_DIR_EPSILON || p.shore.x <= 0.0) {
        return 0.0;
    }

    // ① 砕け泡: 振幅/水深 が砕波比を超えた所が白くなる。
    //    波の峰（sin > 0）にだけ乗せることで、泡が帯として岸へ流れて見える。
    let amp      = water_shore_amplitude(p, s);
    let phase    = water_shore_phase(p, s, t);
    let ratio    = amp / max(s.depth, SHORE_MIN_DEPTH_M);
    let breaking = smoothstep(
        SHORE_BREAK_RATIO, SHORE_BREAK_RATIO * (1.0 + SHORE_BREAK_RAMP), ratio);
    let break_foam = breaking * clamp(sin(phase), 0.0, 1.0);

    // ② 打ち上げ（swash）: 岸線（岸距離 0）付近の薄い泡帯が、周期で前後する。
    //    うねりの峰から少し遅れて伸び上がるので位相遅れを入れる。
    let travel = p.shore.y * SHORE_SWASH_TRAVEL_RATIO
               * sin(WATER_TAU * (t / p.shore.z + SHORE_SWASH_PHASE_LAG));
    let band   = p.shore.y * SHORE_SWASH_BAND_RATIO;
    let swash  = 1.0 - smoothstep(0.0, band, abs(s.distance - travel));

    return clamp(max(break_foam, swash) * p.shore.w * p.shore.x, 0.0, 1.0) * SHORE_FOAM_MAX;
}

// ─── 合成された水面の高さ場（**W5.1 の合流点**）─────────────────
//
// 水面の高さは「解析サイン波（W1）＋ 波紋・航跡（I2）＋ 岸波（W1.5）」の
// **単純な重ね合わせ**である。高さ場の重ね合わせは勾配の重ね合わせと同値なので、
// 法線は「勾配を全部足してから 1 回だけ正規化」で正しく求まる。
//
// **W5.1（頂点変位の大波）へ**: 頂点シェーダから `water_surface_height` を
// そのまま呼べば、フラグメントの法線とまったく同じ高さ場で頂点を動かせる
// （＝シルエットと陰影がズレない）。引数は WaterParams・ワールド XZ・時間だけで、
// フラグメント固有の入力（深度・画面 UV）に一切依存しないよう分離してある。
// 頂点段では group2（波紋の場）も group1（ショアフィールド）も
// VERTEX_FRAGMENT 可視でバインドされているため、追加のバインド変更も要らない。

// ─── 流れ（Phase W4）──────────────────────────────────────────
//
// 川インスタンスでは、解析波（W1）のサンプル位置を **上流側へずらす**ことで
// 「水面模様が下流へ流れる」を作る。ずらし量は `流速 × 時間` だが、そのまま
// 単調に増やすと模様が永久に平行移動するだけなので、半周期ずれた 2 位相を
// 三角波でクロスフェードする（フローマップの標準手法。継ぎ目は原理的に出ない：
// 各位相の重みは、そのずらし量が 0 に戻る瞬間にちょうど 1 になる）。
//
// **重要**: ブレンド重みは時間だけの関数で空間に依存しないため、
// 「高さをブレンドしたもの」の空間微分は「勾配をブレンドしたもの」と厳密に一致する。
// つまり法線（勾配）と W5.1 の頂点変位（高さ）は食い違わない。

/// このインスタンスは川リボンの 1 分割か。
fn water_is_river(p: WaterParams) -> bool {
    return p.center.w >= WATER_INSTANCE_RIVER_MIN;
}

/// 川の流れの向き（XZ 単位ベクトル。上流 → 下流）。分割が縮退していればゼロ。
fn water_flow_dir(p: WaterParams) -> vec2<f32> {
    let d = vec2<f32>(p.river_p1.x - p.river_p0.x, p.river_p1.z - p.river_p0.z);
    let len = length(d);
    if (len < WATER_EPSILON) {
        return vec2<f32>(0.0, 0.0);
    }
    return d / len;
}

/// 2 位相ブレンドの (位相0のずらし量, 位相1のずらし量, 位相0の重み)。
///
/// 戻り値の xy = 位相 0 のワールド XZ オフセット、zw は使わず、
/// 重みは別関数で返す（WGSL に多値返却が無いため、必要な 3 つを vec4 に詰める）。
/// x,y = 位相 0 のオフセット／z,w = 位相 1 のオフセット。
fn water_flow_offsets(p: WaterParams, t: f32) -> vec4<f32> {
    let dir   = water_flow_dir(p);
    let speed = p.river_p1.w;
    let phase = t / WATER_FLOW_PHASE_PERIOD;
    let f0 = fract(phase);
    let f1 = fract(phase + WATER_FLOW_PHASE_OFFSET);
    let travel = speed * WATER_FLOW_PHASE_PERIOD;
    let o0 = dir * (travel * f0);
    let o1 = dir * (travel * f1);
    return vec4<f32>(o0.x, o0.y, o1.x, o1.y);
}

/// クロスフェードの三角波（0..1）。
///
/// **重みの割り当てが本手法の要**である。ずらし量 `fract()` は一周ごとに
/// 1 → 0 へ跳ぶので、**跳ぶ瞬間にその位相の重みが 0 でなければ継ぎ目が見える**。
///   ・位相 0（`fract(phase)`）が跳ぶのは phase が整数のとき。そこで本関数は 1 を返す
///     → よって位相 0 には **`1 - weight`** を掛ける（跳ぶ瞬間に 0 になる）
///   ・位相 1（`fract(phase + 0.5)`）が跳ぶのは `fract(phase) = 0.5` のとき。
///     そこで本関数は 0 を返す → よって位相 1 には **`weight`** を掛ける
/// 逆に割り当てると、周期ごとに 2 回パターンが飛ぶ（実際にやると目に見える）。
fn water_flow_weight(t: f32) -> f32 {
    return abs(1.0 - 2.0 * fract(t / WATER_FLOW_PHASE_PERIOD));
}

/// 解析波の高さ（川なら流れに乗せた 2 位相ブレンド、それ以外は素の解析波）。
fn water_flow_wave_height(p: WaterParams, world_xz: vec2<f32>, t: f32) -> f32 {
    if (!water_is_river(p)) {
        return water_wave_height(world_xz, p.wave.x, p.wave.y, p.wave.z, t);
    }
    let off = water_flow_offsets(p, t);
    let w   = water_flow_weight(t);
    // サンプル位置を **上流側へ**ずらす（＝模様が下流へ動いて見える）。
    let h0 = water_wave_height(world_xz - off.xy, p.wave.x, p.wave.y, p.wave.z, t);
    let h1 = water_wave_height(world_xz - off.zw, p.wave.x, p.wave.y, p.wave.z, t);
    // mix(a, b, w) = a*(1-w) + b*w → 位相 0 に (1-w)、位相 1 に w。
    // この対応でないと、ずらし量が巻き戻る瞬間にパターンが飛ぶ（water_flow_weight 参照）。
    return mix(h0, h1, w);
}

/// 解析波の勾配（上と同じ 2 位相ブレンド。平行移動の微分は微分の平行移動）。
fn water_flow_wave_gradient(p: WaterParams, world_xz: vec2<f32>, t: f32) -> vec2<f32> {
    if (!water_is_river(p)) {
        return water_wave_gradient(world_xz, p.wave.x, p.wave.y, p.wave.z, t);
    }
    let off = water_flow_offsets(p, t);
    let w   = water_flow_weight(t);
    let g0 = water_wave_gradient(world_xz - off.xy, p.wave.x, p.wave.y, p.wave.z, t);
    let g1 = water_wave_gradient(world_xz - off.zw, p.wave.x, p.wave.y, p.wave.z, t);
    // 高さ側とまったく同じ重み配分にすること（食い違うと法線と変位がずれる）。
    return mix(g0, g1, w);
}

/// 波紋（I2）の高さ・勾配へ掛かるユーザ調整スケール。
/// 高さと勾配の両方に同じ係数を掛けないと、法線と（W5.1 の）変位が食い違う。
fn water_ripple_scale(p: WaterParams) -> f32 {
    return p.fresnel.z * WATER_RIPPLE_NORMAL_SCALE;
}

/// 合成された水面高さ（m）。3 ソースの高さ場の和。
/// 解析波は川では流れに乗る（W4）。波紋・岸波はワールド固定のまま
/// （波紋の移流は W4 では行わない。理由は本ファイル冒頭の「流れ」節を参照）。
fn water_surface_height(p: WaterParams, world_xz: vec2<f32>, t: f32) -> f32 {
    return water_flow_wave_height(p, world_xz, t)
         + water_ripple_height(world_xz) * water_ripple_scale(p)
         + water_shore_height(p, world_xz, t);
}

/// 合成された水面高さ場の勾配 (∂h/∂x, ∂h/∂z)。`water_surface_height` の微分。
fn water_surface_gradient(p: WaterParams, world_xz: vec2<f32>, t: f32) -> vec2<f32> {
    return water_flow_wave_gradient(p, world_xz, t)
         + water_ripple_gradient(world_xz) * water_ripple_scale(p)
         + water_shore_gradient(p, world_xz, t);
}

/// フラグメント座標（フレームバッファ画素）＋深度からワールド座標を復元する。
///
/// NDC が写像されるのは **ビューポート矩形だけ**なので、RT 全面ではなく
/// `u_camera.viewport` で正規化する（Play のレターボックス時のズレ防止。
/// deferred_lighting.wgsl と同一の規約）。
fn water_world_from_depth(frag_xy: vec2<f32>, depth: f32) -> vec3<f32> {
    let vp = u_camera.viewport;
    let uv = (frag_xy - vp.xy) / max(vp.zw, vec2<f32>(WATER_EPSILON, WATER_EPSILON));
    // wgpu の NDC は x:[-1,1], y:[-1,1]（上が +1）, z:[0,1]。
    let ndc = vec3<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth);
    let p   = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return p.xyz / p.w;
}

/// 指定のフレームバッファ画素のシーン深度を読む（範囲外はクランプ）。
fn water_load_depth(px: vec2<f32>) -> f32 {
    let dim = vec2<i32>(textureDimensions(t_depth, 0));
    let c   = clamp(vec2<i32>(px), vec2<i32>(0, 0), dim - vec2<i32>(1, 1));
    return textureLoad(t_depth, c, 0);
}

// ─── 頂点シェーダ ────────────────────────────────────────────

/// 頂点シェーダ出力。`idx` はフラグメントでパラメータ配列を引くためのインスタンス番号。
struct WaterVsOut {
    @builtin(position) clip:      vec4<f32>,
    @location(0)       world_pos: vec3<f32>,
    /// インスタンス番号（補間するとフラグメント間で混ざるため flat）
    @location(1) @interpolate(flat) idx: u32,
}

/// 川リボンの 1 分割ぶんの四角形を作る（Phase W4）。
///
/// 角オフセット `corner` の **y が上流/下流の選択**（−1 = 上流ノード p0／+1 = 下流ノード p1）、
/// **x が左右**（±1）を意味する。各ノードのワールド Y をそのまま使うので、
/// 下る川ではリボンが傾いた面になる。
/// 法線はマイター補正込みなので、掛けるのは半幅だけでよい。
/// **`water_id.wgsl` の同名関数と完全に同一の形状**にすること
/// （見た目とピック形状が食い違うため）。
fn water_river_vertex(p: WaterParams, corner: vec2<f32>) -> vec3<f32> {
    let downstream = corner.y > 0.0;
    let base = select(p.river_p0.xyz, p.river_p1.xyz, downstream);
    let nrm  = select(p.river_normal.xy, p.river_normal.zw, downstream);
    let hw   = p.river_p0.w;
    return vec3<f32>(
        base.x + nrm.x * corner.x * hw,
        base.y,
        base.z + nrm.y * corner.x * hw,
    );
}

/// 頂点バッファ無しで水面のポリゴンを生成する。
/// インスタンス種別（`center.w`）に応じて
/// 「軸平行クアッド（Ocean / Region）」か「川リボンの 1 分割（W4）」を作る。
@vertex
fn vs_water(
    @builtin(vertex_index)   vi: u32,
    @builtin(instance_index) ii: u32,
) -> WaterVsOut {
    let p      = u_water[ii];
    let corner = water_quad_corner(vi % WATER_QUAD_VERTEX_COUNT);
    var world  = vec3<f32>(
        p.center.x + corner.x * p.half_extent.x,
        p.center.y,
        p.center.z + corner.y * p.half_extent.z,
    );
    if (water_is_river(p)) {
        world = water_river_vertex(p, corner);
    }

    var out: WaterVsOut;
    out.clip      = u_camera.view_proj * vec4<f32>(world, 1.0);
    out.world_pos = world;
    out.idx       = ii;
    return out;
}

// ─── フラグメントシェーダ ────────────────────────────────────

/// 水面 1 フラグメントを合成する。処理順は
/// 波法線 → 手動深度テスト → 厚み復元 → 吸収 → 屈折 → 岸フォーム → フレネル → 合成。
@fragment
fn fs_water(in: WaterVsOut) -> @location(0) vec4<f32> {
    let p = u_water[in.idx];

    // ① 波法線（ワールド空間、上向き基準）
    //    解析波（W1）・波紋（I2）・岸波（W1.5）の **勾配を合算してから 1 回だけ正規化**する。
    //    高さ場の重ね合わせ = 勾配の重ね合わせ、という関係に乗った合成であり、
    //    法線を 3 つ作って混ぜるより物理的に正しい。
    //    波紋強度 0／岸波強度 0 の水では該当項が完全に消え、W1 と 1 ビットも変わらない。
    let ripple_h = water_ripple_height(in.world_pos.xz);
    let grad     = water_surface_gradient(p, in.world_pos.xz, u_camera.time);
    let n        = water_normal_from_gradient(grad);

    // ② 手動深度テスト（深度アタッチメントを持たないため自前で行う）
    //    シーン深度が水面フラグメントより手前なら、水は不透明物に隠れている。
    let scene_depth = water_load_depth(in.clip.xy);
    if (scene_depth < in.clip.z) {
        discard;
    }

    // ③ 水の厚み（水面点 → 水底/背景点までの距離）
    var thickness: f32;
    if (scene_depth >= WATER_SKY_DEPTH_THRESHOLD) {
        // 背景が空（無限遠）＝ 水底が無い。最大厚みとして扱い、深場の色へ収束させる。
        thickness = WATER_SKY_THICKNESS;
    } else {
        let bottom = water_world_from_depth(in.clip.xy, scene_depth);
        thickness  = distance(bottom, in.world_pos);
    }

    // ④ 吸収（Beer-Lambert 近似）: 厚いほど deep_color へ寄る。
    let absorb_dist = max(p.shallow_color.a, WATER_EPSILON);
    let absorb      = exp(-thickness / absorb_dist);
    let tint        = mix(p.deep_color.rgb, p.shallow_color.rgb, absorb);

    // ⑤ 屈折（スクリーンスペース）: 波法線の XZ で背景 UV をずらしてグラブを読む。
    let base_uv = in.clip.xy / max(u_camera.resolution, vec2<f32>(WATER_EPSILON, WATER_EPSILON));
    let warp_uv = clamp(
        base_uv + n.xz * p.wave.w,
        vec2<f32>(WATER_UV_MIN, WATER_UV_MIN),
        vec2<f32>(WATER_UV_MAX, WATER_UV_MAX),
    );
    // 歪んだ先のシーン深度が水面より **手前** なら、そこは水より前にある物体（岸・手前のオブジェクト）で、
    // その色を水中の背景として拾うと岸際で背景が滲む典型バグになる。その場合は歪み無し UV へ戻す。
    let warp_depth = water_load_depth(warp_uv * u_camera.resolution);
    var refract_uv = warp_uv;
    if (warp_depth < in.clip.z) {
        refract_uv = base_uv;
    }
    let background = textureSampleLevel(t_scene, s_scene, refract_uv, 0.0).rgb;

    // ⑥ 最終合成: 深いほど背景を隠す（surface_opacity が最大被覆率）。
    let opacity = clamp(p.deep_color.a, 0.0, 1.0) * (1.0 - absorb);
    var color   = mix(background, tint, opacity);

    // ⑦ 岸フォーム: 水深が foam_width 未満の帯に滑らかに乗せる。
    let foam_width = max(p.foam_color.a, WATER_EPSILON);
    let foam       = (1.0 - smoothstep(0.0, foam_width, thickness)) * p.reflection_color.a;
    color          = color + p.foam_color.rgb * foam;

    // ⑦' 航跡フォーム（Phase I2）: 波紋の高さがしきい値を超えた所へ白い泡を乗せる。
    //     岸フォームと **同じ foam_color** を流用する（水ごとの泡の色は 1 つ、という設計）。
    //     走る・飛び込むと出て、歩く程度では出ないしきい値が既定。
    let ripple_threshold = max(p.fresnel.w, WATER_EPSILON);
    let ripple_foam = smoothstep(
        ripple_threshold,
        ripple_threshold * (1.0 + WATER_RIPPLE_FOAM_RAMP),
        abs(ripple_h),
    ) * WATER_RIPPLE_FOAM_MAX * clamp(p.fresnel.z, 0.0, 1.0);
    color = color + p.foam_color.rgb * ripple_foam;

    // ⑦'' 岸波の泡（Phase W1.5）: 砕け波の白帯と打ち上げ（swash）の泡。
    //      ここも **同じ foam_color** を流用する（水ごとの泡の色は 1 つ、という設計）。
    //      岸波強度 0・ショアフィールド無しの水では 0 が返り、W1/I2 と同一出力になる。
    let shore_foam = water_shore_foam(p, in.world_pos.xz, u_camera.time);
    color = color + p.foam_color.rgb * shore_foam;

    // ⑧ フレネル: 浅い角度ほど反射色へ寄せる。
    let view_dir = normalize(u_camera.position - in.world_pos);
    let fresnel  = clamp(
        p.fresnel.y * pow(1.0 - clamp(dot(n, view_dir), 0.0, 1.0), max(p.fresnel.x, WATER_EPSILON)),
        0.0, 1.0,
    );
    color = mix(color, p.reflection_color.rgb, fresnel);

    // 背景を自前で合成済みなので、そのまま不透明（alpha=1）で書き出す（ブレンドは Replace）。
    return vec4<f32>(color, 1.0);
}
