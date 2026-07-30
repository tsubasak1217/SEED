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
//  ## 水中から見上げた水面（Phase W7）
//  カメラが水面より下にあるフラグメントは「水面の裏側」を見ている。パイプラインは
//  `cull_mode = None` なので裏面もラスタライズされており、修正が要るのは合成側だけである。
//    ・**厚み 0（素通し）**にする … 水面の向こう側は空気であって水ではないので、シーン深度
//      から求めた距離を厚みとして使ってはいけない（最大厚み扱いになり深場の色で
//      塗り潰され、外の景色が見えなくなる）。視線が通った水の減衰は水中ポスト（W2）の担当。
//    ・**法線を反転**して視線側へ向け、フレネルをそのまま評価する（水中では浅い角度ほど
//      水面が鏡になる＝全反射の粗い代用になる）。
//    ・**泡を出さない** … 泡は水面の上に浮くものであり、かつ厚み 0 では岸フォームが
//      「水深 0」と誤判定されて全面が白くなるため。
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
//  波は 6 層の重ね合わせで、周波数比を無理数・方向を非対称に取り、さらに
//  **低周波の空間包絡**で振幅を緩やかに変調してタイル感（繰り返しの視認）を消してある（W6.1）。
//  一式は `wave_direction_deg`（`wave_axis`）でまとめて回転できる（W6.3）。
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
    /// 解析波の**全体回転**（Phase W6.3）。
    /// x = cos(方位角)／y = sin(方位角)／z,w = 予約（0）。
    /// 方位角は `WaterVolumeComponent::wave_direction_deg`（ワールド XZ 平面。**0 = +Z へ進む**、
    /// 正の角度で +X 側へ回る＝上から見て時計回り）を CPU 側でラジアン化し、
    /// 毎ピクセルの sin/cos を避けるため cos/sin を焼き込んだもの。
    wave_axis:        vec4<f32>,
    /// 川リボン 1 分割の**関節タンジェント**（Phase W6.2）。
    /// x,y = 上流ノード／z,w = 下流ノードの進行方向（XZ 単位ベクトル）。
    ///
    /// **区間の弦（p1 - p0）ではなくノードごとの平滑化タンジェント**であることが要点。
    /// 隣り合う 2 インスタンスは共有する関節でまったく同じ値を持つため、
    /// これを頂点間で補間して流れの向きに使うと、区間境界で流れが跳ばない
    /// （＝リボンの継ぎ目が見えなくなる。詳細は「流れ」節を参照）。
    river_tangent:    vec4<f32>,
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

/// 重ね合わせるサイン波の層数（Phase W6.1 で 4 → 6）。
///
/// 層が少ないほど「同じ形の繰り返し」が目視で分かりやすい。6 層は
/// 「タイル感が消える最小の層数」として選んだ値で、コスト増は 2 層ぶん
/// （＝1 ピクセルあたり sin/cos 各 2 回）に収まる。
const WAVE_LAYER_COUNT: u32 = 6u;

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

// ─── 解析波の層テーブル（Phase W6.1）────────────────────────────
//
// ## なぜ 6 層＋非対称な方向・無理数比の周波数なのか
// サイン波の重ね合わせは、各層の**空間周期の最小公倍数**で必ず繰り返す。
// 周波数比が有理数（2.0 や 1.5 のような小さい整数比）だと最小公倍数が短くなり、
// 数十 m スケールで「同じ形」が目に見えて反復する。周波数比を無理数
// （黄金比 φ、1+√2、3+√3 …）に取ると厳密な繰り返し周期が存在しなくなり、
// さらに方向を等間隔・直交から外す（90°/180° の対称を避ける）ことで
// 格子状の模様が出なくなる。
//
// ## 見た目の強さを変えないための正規化
// タイル感対策で層を増やしても**体感の波の強さ（＝法線の傾き）は据え置く**。
// 法線を決めるのは高さではなく勾配で、層ごとの寄与は A_k·f_k である。
// ここで揃えるべきは単純和ではなく **二乗和（RMS）** である。位相が互いに独立なので
// 実際に目に入る傾きは √(Σ(A·f)²/2) に比例し、単純和を保ったまま層を増やすと
// 二乗和が減って波が平たくなってしまう（実際 4→6 層で RMS は約 2 割低下する）。
// 旧 4 層の Σ(A·f)² は 1² + (0.5·2.13)² + (0.25·3.71)² + (0.125·5.29)² ≒ 3.43。
// 新 6 層の振幅倍率は **この二乗和に一致するよう正規化**してある（実測 3.42）。
// したがって `wave_amplitude` の既定値は変更不要である
//（振幅そのものの和は 1.875 → 2.145 とやや増えるが、これは高さ側の話であり、
//  W5.1 の頂点変位を入れる際に必要なら amplitude 側で調整すればよい）。

/// 波の層ごとの進行方向（XZ 平面。単位ベクトル）。
///
/// **方位角の規約**: 0° = +Z へ進む／正の角度で +X 側へ回る（上から見て時計回り）。
/// ベクトルは `(sin(方位角), cos(方位角))`。
/// 方位角は 0/41/97/158/226/289° と**不等間隔**に取ってあり、
/// 直交（90°）・逆向き（180°）の対称ペアを持たない。
/// この一式は `wave_axis`（`wave_direction_deg`）で**剛体回転**する（相対分散は不変）。
const WAVE_DIR_0: vec2<f32> = vec2<f32>( 0.00000,  1.00000); //   0°
const WAVE_DIR_1: vec2<f32> = vec2<f32>( 0.65606,  0.75471); //  41°
const WAVE_DIR_2: vec2<f32> = vec2<f32>( 0.99255, -0.12187); //  97°
const WAVE_DIR_3: vec2<f32> = vec2<f32>( 0.37461, -0.92718); // 158°
const WAVE_DIR_4: vec2<f32> = vec2<f32>(-0.71934, -0.69466); // 226°
const WAVE_DIR_5: vec2<f32> = vec2<f32>(-0.94552,  0.32557); // 289°

/// 波の層ごとの周波数倍率（**互いに無理数比**。有理数比だと繰り返し周期が生まれる）。
/// 値は 1／φ／1+√2／(この 2 つの中間の無理数)／3+√3／φ⁴。
const WAVE_FREQ_MUL_0: f32 = 1.0;
const WAVE_FREQ_MUL_1: f32 = 1.618034;
const WAVE_FREQ_MUL_2: f32 = 2.414214;
const WAVE_FREQ_MUL_3: f32 = 3.302776;
const WAVE_FREQ_MUL_4: f32 = 4.732051;
const WAVE_FREQ_MUL_5: f32 = 6.854102;

/// 波の層ごとの振幅倍率（高周波ほど小さい ≒ f^-1.15 のスペクトル）。
/// 上記のとおり Σ(振幅×周波数)² が旧 4 層と一致するよう正規化してある。
const WAVE_AMP_MUL_0: f32 = 0.8670;
const WAVE_AMP_MUL_1: f32 = 0.5008;
const WAVE_AMP_MUL_2: f32 = 0.3167;
const WAVE_AMP_MUL_3: f32 = 0.2182;
const WAVE_AMP_MUL_4: f32 = 0.1462;
const WAVE_AMP_MUL_5: f32 = 0.0958;

/// 波の層ごとのスクロール速度倍率（時間方向にも周期が揃わないよう非整数比）。
const WAVE_SPEED_MUL_0: f32 = 1.0;
const WAVE_SPEED_MUL_1: f32 = 1.31;
const WAVE_SPEED_MUL_2: f32 = 0.79;
const WAVE_SPEED_MUL_3: f32 = 1.63;
const WAVE_SPEED_MUL_4: f32 = 0.58;
const WAVE_SPEED_MUL_5: f32 = 1.13;

/// 波の層ごとの初期位相（ラジアン）。
///
/// 全層の位相が原点で揃っていると、原点付近に「全部の波の峰が重なる」目立つ点が生まれ、
/// そこが模様の基準点として認識されてしまう。互いに素な適当な値でずらして散らす。
const WAVE_PHASE_0: f32 = 0.0;
const WAVE_PHASE_1: f32 = 1.7;
const WAVE_PHASE_2: f32 = 3.4;
const WAVE_PHASE_3: f32 = 5.1;
const WAVE_PHASE_4: f32 = 2.2;
const WAVE_PHASE_5: f32 = 4.9;

// ─── 低周波の空間包絡（Phase W6.1）──────────────────────────────
//
// 層を増やして周波数比を無理数にしても、**局所的な波の「密度」は一様**なので、
// 遠景では均質なテクスチャに見えて反復感が残る。実際の水面には
// 「風の当たる帯」「凪いだ帯」といった数百 m スケールのむらがある。
// そこで波長の数倍〜十数倍という**非常に低い周波数のサイン 2 本の積**で
// 振幅を緩やかに変調し、周期性の手がかりを潰す。
//
// コストは dot 2・sin 2・積和 2 の合計 6 命令程度。
// 包絡の空間変化は波長スケールに比べて桁違いに緩やかなので、
// **勾配を取るときは包絡を局所定数とみなす**（岸波の包絡線と同じ標準的な近似）。
// 高さ・勾配の双方に同じ係数を掛けるので、W5.1 の頂点変位とも食い違わない。

/// 包絡の変調深さ（0 = 変調なし）。振幅は 1±この値の範囲で揺れる。
/// 平均は 1 のまま（sin の積の期待値は 0）なので、**体感の波の強さは変わらない**。
const WAVE_ENVELOPE_DEPTH: f32 = 0.35;

/// 包絡サイン A/B の空間周波数（基本波数 `wave_scale` に対する比）。
/// 既定 `wave_scale`=0.12 なら波長 ≒ 248m / 412m にあたり、基本波（≒52m）の 5〜8 倍。
const WAVE_ENVELOPE_FREQ_A: f32 = 0.211;
const WAVE_ENVELOPE_FREQ_B: f32 = 0.127;

/// 包絡サイン A/B の向き（XZ 単位ベクトル。37° と 121°。直交させない）。
const WAVE_ENVELOPE_DIR_A: vec2<f32> = vec2<f32>( 0.60182,  0.79864); //  37°
const WAVE_ENVELOPE_DIR_B: vec2<f32> = vec2<f32>( 0.85717, -0.51504); // 121°

/// 包絡サイン A/B のスクロール速度倍率（`wave_speed` に対する比）。
/// 0 だと「むらの模様」がワールドに貼り付いて止まって見えるため、波よりずっと遅く流す。
const WAVE_ENVELOPE_SPEED_A: f32 = 0.07;
const WAVE_ENVELOPE_SPEED_B: f32 = 0.11;

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

/// 解析波 1 層ぶんのパラメータ（テーブルの 1 行）。
///
/// 4 本の if 連鎖（方向・周波数・振幅・速度）を 1 本にまとめるための束ね方。
/// 層を増減するときに直す場所が 1 箇所で済む。
struct WaveLayer {
    /// 進行方向（XZ 単位ベクトル。**回転前のローカル基準**）
    dir:       vec2<f32>,
    /// 基本波数に対する周波数倍率
    freq_mul:  f32,
    /// 基本振幅に対する振幅倍率
    amp_mul:   f32,
    /// 基本速度に対するスクロール速度倍率
    speed_mul: f32,
    /// 初期位相（ラジアン）
    phase:     f32,
}

/// 波レイヤ i のパラメータ一式。
fn wave_layer(i: u32) -> WaveLayer {
    if (i == 0u) { return WaveLayer(WAVE_DIR_0, WAVE_FREQ_MUL_0, WAVE_AMP_MUL_0, WAVE_SPEED_MUL_0, WAVE_PHASE_0); }
    if (i == 1u) { return WaveLayer(WAVE_DIR_1, WAVE_FREQ_MUL_1, WAVE_AMP_MUL_1, WAVE_SPEED_MUL_1, WAVE_PHASE_1); }
    if (i == 2u) { return WaveLayer(WAVE_DIR_2, WAVE_FREQ_MUL_2, WAVE_AMP_MUL_2, WAVE_SPEED_MUL_2, WAVE_PHASE_2); }
    if (i == 3u) { return WaveLayer(WAVE_DIR_3, WAVE_FREQ_MUL_3, WAVE_AMP_MUL_3, WAVE_SPEED_MUL_3, WAVE_PHASE_3); }
    if (i == 4u) { return WaveLayer(WAVE_DIR_4, WAVE_FREQ_MUL_4, WAVE_AMP_MUL_4, WAVE_SPEED_MUL_4, WAVE_PHASE_4); }
    return WaveLayer(WAVE_DIR_5, WAVE_FREQ_MUL_5, WAVE_AMP_MUL_5, WAVE_SPEED_MUL_5, WAVE_PHASE_5);
}

/// 波の方位角ぶんだけ**サンプル位置を逆回転**する（Phase W6.3）。
///
/// 層ごとに方向ベクトルを回すと 6 回の回転が要るが、
/// `dot(R d, p) = dot(d, R⁻¹ p)` なので **点を 1 回だけ逆回転すれば全層が回る**。
/// `axis` は `WaterParams::wave_axis`（x = cosθ, y = sinθ）。
fn water_wave_rotate_point(p: vec2<f32>, axis: vec2<f32>) -> vec2<f32> {
    // R(−θ) p = ( x cosθ − z sinθ,  x sinθ + z cosθ )
    return vec2<f32>(p.x * axis.x - p.y * axis.y, p.x * axis.y + p.y * axis.x);
}

/// 逆回転した座標系で求めた勾配を、ワールド XZ の勾配へ戻す（Phase W6.3）。
///
/// 合成関数の微分則より ∇_p f(R⁻¹p) = (R⁻¹)ᵀ ∇f = R(θ) ∇f。
fn water_wave_rotate_gradient(g: vec2<f32>, axis: vec2<f32>) -> vec2<f32> {
    // R(θ) g = ( gx cosθ + gz sinθ,  −gx sinθ + gz cosθ )
    return vec2<f32>(g.x * axis.x + g.y * axis.y, -g.x * axis.y + g.y * axis.x);
}

/// 低周波の空間包絡（Phase W6.1）。平均 1・振れ幅 ±`WAVE_ENVELOPE_DEPTH` の緩やかな振幅変調。
///
/// 引数 `p` は**逆回転済みの座標**（波と一緒に包絡も回るようにするため）。
/// 高さ・勾配の双方へ同じ値を掛けること（片方だけだと法線と変位が食い違う）。
fn water_wave_envelope(p: vec2<f32>, scale: f32, speed: f32, t: f32) -> f32 {
    let a = sin(dot(WAVE_ENVELOPE_DIR_A, p) * scale * WAVE_ENVELOPE_FREQ_A
              + t * speed * WAVE_ENVELOPE_SPEED_A);
    let b = sin(dot(WAVE_ENVELOPE_DIR_B, p) * scale * WAVE_ENVELOPE_FREQ_B
              + t * speed * WAVE_ENVELOPE_SPEED_B);
    return 1.0 + WAVE_ENVELOPE_DEPTH * a * b;
}

/// 解析的サイン波の重ね合わせによる水面高さ場の **勾配** (∂h/∂x, ∂h/∂z) を求める。
///
/// 高さ場 h(p) = E(p) · Σ_k A_k · sin(dot(d_k, R⁻¹p) · f_k + t · s_k + φ_k) の解析微分（cos）。
/// `E` は低周波の空間包絡で、その微分は無視する（局所定数近似。定数節を参照）。
/// 法線ではなく勾配を返すのは、**波紋（I2）の勾配と足し合わせてから 1 回だけ
/// 正規化する**ため（法線を 2 つ作って混ぜるより、勾配の加算の方が物理的に正しい：
/// 高さ場の重ね合わせ = 勾配の重ね合わせ）。
fn water_wave_gradient(
    p: vec2<f32>, amplitude: f32, scale: f32, speed: f32, t: f32, axis: vec2<f32>,
) -> vec2<f32> {
    let q = water_wave_rotate_point(p, axis);
    var grad = vec2<f32>(0.0, 0.0);
    for (var i: u32 = 0u; i < WAVE_LAYER_COUNT; i = i + 1u) {
        let L     = wave_layer(i);
        let freq  = scale * L.freq_mul;
        let amp   = amplitude * L.amp_mul;
        let phase = dot(L.dir, q) * freq + t * speed * L.speed_mul + L.phase;
        // d/dp [ A sin(dot(d,p) f + ...) ] = A f cos(...) * d
        grad = grad + L.dir * (amp * freq * cos(phase));
    }
    grad = grad * water_wave_envelope(q, scale, speed, t);
    return water_wave_rotate_gradient(grad, axis);
}

/// 解析的サイン波の重ね合わせによる水面高さ場の **高さ**（m）。
///
/// `water_wave_gradient` と同じ層構成・同じ包絡の原関数（sin）。W5.1（頂点変位）が
/// 頂点段で高さを必要とするため、勾配と対で持たせてある。
/// フラグメントの法線計算では勾配のみを使うので、こちらは呼ばれない場合もある。
fn water_wave_height(
    p: vec2<f32>, amplitude: f32, scale: f32, speed: f32, t: f32, axis: vec2<f32>,
) -> f32 {
    let q = water_wave_rotate_point(p, axis);
    var h = 0.0;
    for (var i: u32 = 0u; i < WAVE_LAYER_COUNT; i = i + 1u) {
        let L     = wave_layer(i);
        let freq  = scale * L.freq_mul;
        let amp   = amplitude * L.amp_mul;
        let phase = dot(L.dir, q) * freq + t * speed * L.speed_mul + L.phase;
        h = h + amp * sin(phase);
    }
    return h * water_wave_envelope(q, scale, speed, t);
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
// **区間境界の継ぎ目について（W6.2）**: ずらしの向きは「区間の弦」ではなく
// **関節ごとの平滑化タンジェント**（`river_tangent`）を頂点間で補間したものを使う。
// 弦は区間ごとに折れるため、境界で流れの向きが跳んで水面模様が不連続になり、
// マイターで重なる角の内側と併せて「ポリゴンの継ぎ目」として見えていた。
// 関節タンジェントは隣接インスタンスが共有する関節で必ず一致するので、境界で連続になる。
// これによりフラグメント段が読む値は **すべて水域単位か、関節で連続な補間値だけ**になり、
// 区間境界に継ぎ目が出る余地が構造的に無くなる。
//
// **重要**: ブレンド重みは時間だけの関数で空間に依存しないため、
// 「高さをブレンドしたもの」の空間微分は「勾配をブレンドしたもの」と厳密に一致する。
// つまり法線（勾配）と W5.1 の頂点変位（高さ）は食い違わない。

/// このインスタンスは川リボンの 1 分割か。
fn water_is_river(p: WaterParams) -> bool {
    return p.center.w >= WATER_INSTANCE_RIVER_MIN;
}

/// 解析波の回転軸（x = cosθ, y = sinθ）。ゼロなら無回転（1,0）として扱う。
///
/// 旧シーン・未設定のパラメータでゼロが入っていても波が消えないための保険。
fn water_wave_axis(p: WaterParams) -> vec2<f32> {
    if (dot(p.wave_axis.xy, p.wave_axis.xy) < WATER_EPSILON) {
        return vec2<f32>(1.0, 0.0);
    }
    return p.wave_axis.xy;
}

/// 川の流れの向き（XZ 単位ベクトル。上流 → 下流）。
///
/// **区間の弦ではなく、頂点から補間されてきた関節タンジェント `hint` を使う**（Phase W6.2）。
/// 弦（p1 − p0）は区間ごとに折れるため、区間境界で流れの向きが跳び、
/// そこだけ水面模様が不連続になる（＝ポリゴンの継ぎ目が見える）。
/// 関節タンジェントは隣接インスタンスで共有されるので、境界で必ず一致する。
/// 補間の結果として長さが 1 を割るため、ここで正規化する。
/// `hint` が縮退している（＝関節タンジェントが得られない）ときだけ弦へフォールバックする。
fn water_flow_dir(p: WaterParams, hint: vec2<f32>) -> vec2<f32> {
    if (dot(hint, hint) > WATER_EPSILON) {
        return normalize(hint);
    }
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
fn water_flow_offsets(p: WaterParams, flow_dir: vec2<f32>, t: f32) -> vec4<f32> {
    let dir   = flow_dir;
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
///
/// `flow_dir` は頂点から補間されてきた関節タンジェント（川以外では未使用）。
fn water_flow_wave_height(
    p: WaterParams, world_xz: vec2<f32>, flow_dir: vec2<f32>, t: f32,
) -> f32 {
    let axis = water_wave_axis(p);
    if (!water_is_river(p)) {
        return water_wave_height(world_xz, p.wave.x, p.wave.y, p.wave.z, t, axis);
    }
    let dir = water_flow_dir(p, flow_dir);
    let off = water_flow_offsets(p, dir, t);
    let w   = water_flow_weight(t);
    // サンプル位置を **上流側へ**ずらす（＝模様が下流へ動いて見える）。
    let h0 = water_wave_height(world_xz - off.xy, p.wave.x, p.wave.y, p.wave.z, t, axis);
    let h1 = water_wave_height(world_xz - off.zw, p.wave.x, p.wave.y, p.wave.z, t, axis);
    // mix(a, b, w) = a*(1-w) + b*w → 位相 0 に (1-w)、位相 1 に w。
    // この対応でないと、ずらし量が巻き戻る瞬間にパターンが飛ぶ（water_flow_weight 参照）。
    return mix(h0, h1, w);
}

/// 解析波の勾配（上と同じ 2 位相ブレンド。平行移動の微分は微分の平行移動）。
fn water_flow_wave_gradient(
    p: WaterParams, world_xz: vec2<f32>, flow_dir: vec2<f32>, t: f32,
) -> vec2<f32> {
    let axis = water_wave_axis(p);
    if (!water_is_river(p)) {
        return water_wave_gradient(world_xz, p.wave.x, p.wave.y, p.wave.z, t, axis);
    }
    let dir = water_flow_dir(p, flow_dir);
    let off = water_flow_offsets(p, dir, t);
    let w   = water_flow_weight(t);
    let g0 = water_wave_gradient(world_xz - off.xy, p.wave.x, p.wave.y, p.wave.z, t, axis);
    let g1 = water_wave_gradient(world_xz - off.zw, p.wave.x, p.wave.y, p.wave.z, t, axis);
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
fn water_surface_height(
    p: WaterParams, world_xz: vec2<f32>, flow_dir: vec2<f32>, t: f32,
) -> f32 {
    return water_flow_wave_height(p, world_xz, flow_dir, t)
         + water_ripple_height(world_xz) * water_ripple_scale(p)
         + water_shore_height(p, world_xz, t);
}

/// 合成された水面高さ場の勾配 (∂h/∂x, ∂h/∂z)。`water_surface_height` の微分。
fn water_surface_gradient(
    p: WaterParams, world_xz: vec2<f32>, flow_dir: vec2<f32>, t: f32,
) -> vec2<f32> {
    return water_flow_wave_gradient(p, world_xz, flow_dir, t)
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
    /// 川の流れの向き（XZ。**関節タンジェントを頂点間で補間したもの**。Phase W6.2）。
    ///
    /// **あえて flat にしない**のが要点である。上流端の 2 頂点には上流ノードの、
    /// 下流端の 2 頂点には下流ノードのタンジェントを入れてあるので、
    /// 隣り合うリボン区間は共有する関節でまったく同じ値を持ち、区間境界で連続になる。
    /// 川以外のインスタンスではゼロ（未使用）。
    @location(2) flow_dir: vec2<f32>,
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
    // 川の流れの向きは「この頂点が属する関節」のタンジェント（Phase W6.2）。
    // corner.y < 0 = 上流端 / > 0 = 下流端。川以外はゼロ（フラグメントで未使用）。
    var flow = vec2<f32>(0.0, 0.0);
    if (water_is_river(p)) {
        world = water_river_vertex(p, corner);
        flow  = select(p.river_tangent.xy, p.river_tangent.zw, corner.y > 0.0);
    }

    var out: WaterVsOut;
    out.clip      = u_camera.view_proj * vec4<f32>(world, 1.0);
    out.world_pos = world;
    out.idx       = ii;
    out.flow_dir  = flow;
    return out;
}

// ─── フラグメントシェーダ ────────────────────────────────────

/// 水面 1 フラグメントを合成する。処理順は
/// 波法線 → 水中判定 → 手動深度テスト → 厚み復元 → 吸収 → 屈折 → 岸フォーム → フレネル → 合成。
@fragment
fn fs_water(in: WaterVsOut) -> @location(0) vec4<f32> {
    let p = u_water[in.idx];

    // ① 波法線（ワールド空間、上向き基準）
    //    解析波（W1）・波紋（I2）・岸波（W1.5）の **勾配を合算してから 1 回だけ正規化**する。
    //    高さ場の重ね合わせ = 勾配の重ね合わせ、という関係に乗った合成であり、
    //    法線を 3 つ作って混ぜるより物理的に正しい。
    //    波紋強度 0／岸波強度 0 の水では該当項が完全に消え、W1 と 1 ビットも変わらない。
    let ripple_h = water_ripple_height(in.world_pos.xz);
    let grad     = water_surface_gradient(p, in.world_pos.xz, in.flow_dir, u_camera.time);
    let n_up     = water_normal_from_gradient(grad);

    // ①' 水中判定（Phase W7）
    //     カメラがこのフラグメントの水面高さより **下** にあるなら、見えているのは
    //     水面の裏側（＝水中から見上げている）。パイプラインは `cull_mode = None` なので
    //     裏面もラスタライズされており、必要なのは「合成の向き」を反転することだけである。
    //     判定に `in.world_pos.y` を使うのは、それが実際にラスタライズされた面の高さそのもので
    //     あり（W1 は頂点変位しないので幾何面 = 水面平面）、傾いた川の面でも正しく効くため。
    let underwater = u_camera.position.y < in.world_pos.y;
    //     視線側を向く法線。水中では上向き法線を反転して使う（フレネル・屈折の歪み方向とも
    //     整合させるため、以降は一貫してこの `n` だけを使う）。
    let n = select(n_up, -n_up, underwater);

    // ② 手動深度テスト（深度アタッチメントを持たないため自前で行う）
    //    シーン深度が水面フラグメントより手前なら、水は不透明物に隠れている。
    let scene_depth = water_load_depth(in.clip.xy);
    if (scene_depth < in.clip.z) {
        discard;
    }

    // ③ 水の厚み（水面点 → 水底/背景点までの距離）
    var thickness: f32;
    if (underwater) {
        // 水中から見上げている場合、水面の「向こう側」は水ではなく **外の空気／空** であり、
        // シーン深度から求まる距離は水の厚みではない（そのまま使うと最大厚み扱いになり、
        // 深場の色で塗り潰されて外の景色が一切見えなくなる＝本修正前の不具合）。
        // 視線が通った水の量は「カメラ → 水面点」の距離だが、その減衰は水中ポスト（W2）の
        // 担当であり本パスでは扱わない。よって厚み 0 として **素通し**にする
        //（absorb=1 → opacity=0 → 背景グラブ＝水上の景色がそのまま出る）。
        thickness = 0.0;
    } else if (scene_depth >= WATER_SKY_DEPTH_THRESHOLD) {
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

    // ⑦ 泡の全体ゲート（Phase W7）
    //    泡（岸フォーム・航跡・砕け波）はいずれも **水面の上側に浮くもの**なので、
    //    水中から見上げているときは載せない。特に岸フォームは thickness=0（素通し）だと
    //    「水深 0 = 岸際」と誤判定されて画面全面が白く塗られてしまうため、この
    //    ゲートは水中視点における必須の抑制である。
    let foam_gate = select(1.0, 0.0, underwater);

    // ⑦-a 岸フォーム: 水深が foam_width 未満の帯に滑らかに乗せる。
    let foam_width = max(p.foam_color.a, WATER_EPSILON);
    let foam       = (1.0 - smoothstep(0.0, foam_width, thickness))
                   * p.reflection_color.a * foam_gate;
    color          = color + p.foam_color.rgb * foam;

    // ⑦' 航跡フォーム（Phase I2）: 波紋の高さがしきい値を超えた所へ白い泡を乗せる。
    //     岸フォームと **同じ foam_color** を流用する（水ごとの泡の色は 1 つ、という設計）。
    //     走る・飛び込むと出て、歩く程度では出ないしきい値が既定。
    let ripple_threshold = max(p.fresnel.w, WATER_EPSILON);
    let ripple_foam = smoothstep(
        ripple_threshold,
        ripple_threshold * (1.0 + WATER_RIPPLE_FOAM_RAMP),
        abs(ripple_h),
    ) * WATER_RIPPLE_FOAM_MAX * clamp(p.fresnel.z, 0.0, 1.0) * foam_gate;
    color = color + p.foam_color.rgb * ripple_foam;

    // ⑦'' 岸波の泡（Phase W1.5）: 砕け波の白帯と打ち上げ（swash）の泡。
    //      ここも **同じ foam_color** を流用する（水ごとの泡の色は 1 つ、という設計）。
    //      岸波強度 0・ショアフィールド無しの水では 0 が返り、W1/I2 と同一出力になる。
    let shore_foam = water_shore_foam(p, in.world_pos.xz, u_camera.time) * foam_gate;
    color = color + p.foam_color.rgb * shore_foam;

    // ⑧ フレネル: 浅い角度ほど反射色へ寄せる。
    //    `n` は視線側を向く法線（水中では下向き）なので、この 1 本の式が
    //    水上＝空の反射、水中＝水面の内側での反射（全反射の粗い代用）の両方を兼ねる。
    //    水中で浅い角度になるほど reflection_color へ寄るため、真上付近は外が見え、
    //    水平方向へ向くほど水面が鏡のように見える、という向きの正しい応答になる。
    let view_dir = normalize(u_camera.position - in.world_pos);
    let fresnel  = clamp(
        p.fresnel.y * pow(1.0 - clamp(dot(n, view_dir), 0.0, 1.0), max(p.fresnel.x, WATER_EPSILON)),
        0.0, 1.0,
    );
    color = mix(color, p.reflection_color.rgb, fresnel);

    // 背景を自前で合成済みなので、そのまま不透明（alpha=1）で書き出す（ブレンドは Replace）。
    return vec4<f32>(color, 1.0);
}
