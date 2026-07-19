// ============================================================
// light_common.wgsl  —  ライト（GpuLight/LightMeta）＋クラスタ参照の共有定義
//
// ## 役割（単一責任）
// フラグメントのライティングに必要な「ライト構造体・メタ情報・binding 宣言」だけを持つ。
// 旧 shader_common.wgsl から移設した（Phase D3: G-Buffer Deferred 化 Phase A）。
//
// なぜ分離するか:
//   デファードのライティングパス（deferred_lighting.wgsl）は、G-Buffer から復元した
//   Surface に対して evaluate_lighting を呼ぶだけであり、マテリアルのテクスチャ群
//   （shader_common.wgsl の group 2: 11 binding）を一切必要としない。
//   しかし旧 shader_common.wgsl は group 2（マテリアル）と group 4（ライト）を同じ
//   ファイルに同居させていたため、「group 4 だけ使いたい」ライティングパスも
//   shader_common.wgsl を連結せざるを得ず、結果として使わない group 2 のバインディングが
//   リフレクション（pipeline_config::reflect_bgls は module.global_variables を無条件に
//   走査する）に載ってしまい、破綻したパイプラインレイアウトになる。
//   surface.wgsl / surface_gather.wgsl の分離（既存の思想）と同じ理由で、
//   「ライト（本ファイル）」と「マテリアル（shader_common.wgsl）」を分離する。
//
// ## 連結順序（重要）
// 本ファイルは ClusterCell / ClusterParams（cluster_common.wgsl で定義）を参照するため、
// 必ず cluster_common.wgsl の**後**に連結すること。
//   例: ["cluster_common.wgsl", "shader_common.wgsl", "light_common.wgsl", "shadow.wgsl", ...]
//   （フォワード系 TOML の連結順。deferred はさらに shader_common.wgsl を省く）
//
// ## Rust 側との対応
// GpuLight / LightMeta のレイアウトは Rust 側 lighting.rs の repr(C) 構造体と
// 厳密に一致させること（vec3 は 16 バイト境界）。
// ============================================================

// ─── Group 4: ライト（binding 0/1）───────────────────────────
//
// storage buffer に全ライト（最大 MAX_LIGHTS）を格納し、
// uniform（u_light_meta.count）で有効ライト数を渡す。
// フラグメントシェーダのライトループがこの配列を count 件だけ走査する。
//
// binding 2〜5 は Phase R2 のシャドウ資源（shadow.wgsl で宣言）。
// デバイスの max_bind_groups=5（group 0〜4）環境があるため group 5 は新設せず、
// シャドウを本グループへ同居させている（Rust 側の複合 BindGroup は lighting.rs）。
//
// GpuLight のレイアウトは Rust 側 lighting.rs の repr(C) 構造体と
// 厳密に一致させること（vec3 は 16 バイト境界、合計 112 バイト）。

// ライト種別コード（LIGHT_KIND_*）は cluster_common.wgsl へ移設済み。
// クラスタ構築 compute（cluster_build.wgsl）も種別ごとのバウンディング体積を求めるのに
// 必要だが、compute は本ファイルを連結できない（group 4 のバインディングが混入する）ため、
// 「バインディングを持たない共有定義ファイル」である cluster_common.wgsl が唯一の定義元になる。
// 本ファイルを使う全パイプライン（mesh / skinned / transparent / deferred 系）の TOML は
// cluster_common.wgsl を本ファイルより先に連結すること。

struct GpuLight {
    color:            vec3<f32>,   // 0
    intensity:        f32,         // 12
    position:         vec3<f32>,   // 16
    range:            f32,         // 28
    direction:        vec3<f32>,   // 32  光が進む向き（L = -direction）
    kind:             u32,         // 44
    inner_cos:        f32,         // 48
    outer_cos:        f32,         // 52
    rect_half_width:  f32,         // 56
    rect_half_height: f32,         // 60
    rect_right:       vec3<f32>,   // 64
    shadow_index:     f32,         // 76  影スロット（-1=影なし / dir:0=CSM有効 / spot:0..3=レイヤ）
    rect_up:          vec3<f32>,   // 80
    soft_radius:      f32,         // 92  ソフト影の見込み半径（Phase R8 ソフトシャドウ）
                                   //     directional: tan(角径) の無次元スロープ（Rust 側で度→tan 変換済み）
                                   //     point/spot/rect: 光源のワールド半径（シェーダで radius/距離＝見込み角に換算）
                                   //     0 = ハードシャドウ（従来の遮蔽レイ 1 本）
    bounce_intensity: f32,         // 96  疑似バウンス（間接光近似）の強度。0 = 無効。
                                   //     lighting_eval.wgsl が無方向・影非適用・幾何ゲート非適用の
                                   //     淡い光を距離減衰つきで撒く項の係数。directional は常に 0。
    // ソフト影マスクのレイヤ番号（Phase RT-Shadow-Denoise）。offset 100（旧 _pad_bounce0 を転用）。
    //   -1.0                           = マスク非対象（従来どおりインライン rt_shadow_factor を評価）。
    //   0.0 .. RT_SHADOW_MASK_LIGHTS-1 = 事前計算した半解像度シャドウマスク（デノイズ済み）のレイヤ番号。
    // CPU（shadow_mask.rs の選定＝soft_radius>0 の上位 intensity）が毎フレーム書き込み、deferred
    // ライティングパスが該当レイヤをサンプルして遮蔽率にする（forward はマスク無効＝インライン）。
    shadow_mask_slot: f32,         // 100
    // 104 → stride 112（16 の倍数）へ揃えるパディング（未使用）。
    // 【重要】vec3<f32> にしてはいけない。WGSL の vec3 は align 16 のため offset 112 へ
    // 押し出され stride が 128 になり、Rust GpuLight(112B) と食い違って
    // 配列 2 要素目以降のライトが全て化ける（平行光は先頭に分割されるため
    // 「point/spot/rect だけ消える」という症状になる）。スカラー 2 本で詰めること。
    _pad_bounce1:     f32,
    _pad_bounce2:     f32,
}

// count = 有効ライト数、rt_shadows = インラインレイトレ影フラグ（Phase R8, 0/1）。
// ambient_* は制御可能な環境光（Phase R1.5）。先頭スカラー 4 つで 16 バイト、その後に
// vec3 の ambient_color（16 バイト境界）＋ ambient_intensity で計 32 バイト。
// Rust 側 lighting.rs の LightMeta（repr(C), 32 バイト）と厳密に一致させること。
struct LightMeta {
    count:             u32,
    rt_shadows:        u32,
    // エディタのシーンビュー表示モード（0=Lit / 1=Unlit / 2=Wireframe。SceneViewMode::to_code）。
    // 0 以外のとき evaluate_lighting はライティングを行わずアルベド＋エミッシブを返す。
    // メインカメラ用 LightMeta のみ非 0 になり得る（プレビュー用は常に 0＝Lit）。
    view_mode:         u32,         //  8
    // RT-Translucency（色付き影＋屈折）の有効フラグ（Phase RT-Translucency, 0/1）。旧 _pad2 を転用。
    // rt_shadow_on.wgsl の色付き影・refract_common.wgsl の屈折を実行時ゲートする。
    translucency_rt:   u32,         // 12
    ambient_color:     vec3<f32>,   // 16  環境光の色（リニア RGB）
    ambient_intensity: f32,         // 28  環境光の強度（0 で完全な暗闇）
}

// LightMeta.translucency_rt のビットマスク（Phase RT-Translucency）。
//   bit0: 色付き影を有効化（translucency==Rt。RT 影シェーダが半透明レイヤーの透過色を影に乗せる）。
//   bit1: 屈折の背景（refract_bg）が有効＝このフレームで屈折可能（deferred 有効＋コピー済み）。
// 2 機能で解決タイミングが違う（色付き影は forward-RT でも効くが、屈折は deferred の背景コピーが要る）
// ため 1 つの u32 に別ビットで載せる。
const TRANSLUCENCY_RT_COLORED_SHADOW: u32 = 1u;
const TRANSLUCENCY_RT_REFRACTION:     u32 = 2u;
//   bit2: 実効の半透明方式が WBOIT（順序独立）。WBOIT は半透明相互の遮蔽が無いため、
//         RT 屈折の界面 tint（奥の半透明レイヤーの透過色）と奥レイヤー自身の WBOIT 描画とで
//         同じ色が二重計上される。このビットが立つとき refract_rt.wgsl は界面 tint 累積をスキップし、
//         奥レイヤーの色を WBOIT 平均で本人が 1 回だけ寄与させる（距離ソート時は本ビット 0＝従来どおり）。
const TRANSLUCENCY_RT_WBOIT:          u32 = 4u;

@group(4) @binding(0) var<storage, read> u_lights:     array<GpuLight>;
@group(4) @binding(1) var<uniform>       u_light_meta: LightMeta;

// ─── Group 4: Clustered Lighting（binding 7〜9, Phase C1）─────
//
// 視錐台を X×Y タイル × Z 深度スライスへ分割したクラスタごとに、影響する局所ライト
// （point/spot/rect）のインデックスを compute（cluster_build.wgsl）が集めておく。
// フラグメント（lighting_eval.wgsl）は「自分のクラスタのリスト ＋ 全平行光」だけを走査する。
//
// 構造体・定数・索引関数は cluster_common.wgsl（バインディングを持たない共有定義）にある。
// バッファ実体と BindGroup は Rust 側 renderer/clustered.rs / lighting.rs が持つ。
//
// binding 6 は RT 影の TLAS（rt_shadow_on.wgsl）。番号の重複を避けて 7 から始める。
//
// 【カメラごとに固有】u_cluster_params は**パスごとに差し替わる**。メインカメラのパスは
// enabled=1 のパラメータ、カメラプレビューのパスは enabled=0 のパラメータを bind する
// （後者ではクラスタを一切参照せず、従来どおり全ライトを線形走査する）。

/// クラスタごとの (offset, count)。CLUSTER_COUNT 要素。
@group(4) @binding(7) var<storage, read> u_cluster_grid:   array<ClusterCell>;
/// グローバルライトインデックスリスト（クラスタの offset..offset+count が自分のライト）。
@group(4) @binding(8) var<storage, read> u_cluster_lights: array<u32>;
/// クラスタパラメータ（カメラ／ビューポート／有効フラグ）。
@group(4) @binding(9) var<uniform>       u_cluster_params: ClusterParams;

// --- Group 4: DDGI probe atlases (bindings 10-13, Phase RT-GI) ---
// GiParams struct comes from ddgi_common.wgsl (concatenated before this file).
// Two octahedral atlases (radiance 8x8 / visibility 16x16, both Rgba16Float) + a
// linear-clamp sampler. Rust side supplies these in lighting.rs make_bg /
// create_rt_bind_group. On non-RT GPUs / preview passes u_gi_params.enabled==0 and
// evaluate_lighting falls back to flat ambient (the atlases are still bound, unused).
@group(4) @binding(10) var<uniform> u_gi_params:     GiParams;
@group(4) @binding(11) var          t_gi_irradiance: texture_2d<f32>;
@group(4) @binding(12) var          t_gi_visibility: texture_2d<f32>;
@group(4) @binding(13) var          s_gi:            sampler;

// ============================================================
// RT 影の共有ジオメトリ（インライン経路＝lighting_eval.wgsl とマスク経路＝shadow_mask.wgsl の
// 唯一の実装）。バインディングを持たない純関数なので、ライト BG を持たない compute 等以外の
// すべての連結から安全に使える（本ファイルは既に GpuLight を定義しているため置き場所として適切）。
// ============================================================

/// 平行光の RT 影レイの最大距離（実質無限。ライトまでの距離が定義できないため大定数）。
/// 旧 lighting_eval.wgsl のローカル定数を共有へ移設した（マスク生成でも同値が要るため）。
const RT_DIR_TMAX: f32 = 10000.0;

/// RT ソフト影の「見込み半径（cone_radius）」の上限（＝tan(見込み半角)）。
///
/// 局所ライトでは cone_radius = soft_radius / light_dist と距離に反比例して発散するため、
/// 上限を設けないと円錐が半球全体へ広がり、少ないサンプル数では量子化ノイズ（ディザ）になる。
/// 値 0.5 → 見込み半角 ≈ 26.6°（直径 53°）。太陽や一般的な室内光源を大きく上回る実用十分な広さ。
/// 平行光（soft_radius=tan(角径)）はこの上限に達しないため主光源のボケ幅には影響しない。
///
/// ## この定数の整合はテストで担保される
/// rt_shadow.rs の `wgsl_soft_shadow_constants_are_consistent` が、本値と rt_shadow_on.wgsl の
/// RT_SHADOW_SAMPLES_MAX / RT_SHADOW_CONE_RADIUS_PER_SAMPLE の噛み合わせ（上限まで広がった円錐で
/// ちょうど最大サンプル数に到達する）を検証する。旧 lighting_eval.wgsl から共有へ移設した。
const RT_SHADOW_MAX_CONE_RADIUS: f32 = 0.5;

/// RT 影のためのライトごとの共有ジオメトリ（放射輝度は含まない。影の L/距離/見込み半径のみ）。
struct LightShadowGeo {
    /// 面から光源への方向（正規化済み）。
    L:           vec3<f32>,
    /// 光源までの距離（レイ tmax。directional は RT_DIR_TMAX）。
    dist:        f32,
    /// ソフト影の見込み半径（RT_SHADOW_MAX_CONE_RADIUS でクランプ済み。0=ハード 1 本）。
    cone_radius: f32,
}

/// ライトと着目点から、RT 影に必要な L・光源距離・見込み半径を求める（種別ごとに分岐）。
///
/// **この関数が L・距離・cone_radius の唯一の実装である**。インライン影（lighting_eval.wgsl）と
/// 事前計算マスク（shadow_mask.wgsl）が同じ値を使うことを保証し、両経路で影がズレないようにする。
/// 放射輝度（減衰・スポット円錐・rect 前面判定）は呼び出し側が geo.L / geo.dist から別途求める。
fn light_shadow_geometry(light: GpuLight, world_pos: vec3<f32>) -> LightShadowGeo {
    var g: LightShadowGeo;
    g.L    = vec3<f32>(0.0, 0.0, 1.0);
    g.dist = RT_DIR_TMAX;

    if light.kind == LIGHT_KIND_DIRECTIONAL {
        // 平行光: L = -照射方向。距離は無限（大定数）。
        g.L = normalize(-light.direction);
    } else if light.kind == LIGHT_KIND_RECT {
        // 矩形エリアライト: 矩形上の最近接点を点光源とみなす（lighting_eval.wgsl の R1 近似と同一）。
        let d       = world_pos - light.position;
        let px      = clamp(dot(d, light.rect_right), -light.rect_half_width,  light.rect_half_width);
        let py      = clamp(dot(d, light.rect_up),    -light.rect_half_height, light.rect_half_height);
        let closest = light.position + light.rect_right * px + light.rect_up * py;
        let to_l    = closest - world_pos;
        g.dist      = length(to_l);
        g.L         = to_l / max(g.dist, 1e-4);
    } else {
        // point / spot: 位置から着目点への方向。
        let to_l = light.position - world_pos;
        g.dist   = length(to_l);
        g.L      = to_l / max(g.dist, 1e-4);
    }

    // 見込み半径（cone_radius）。directional は soft_radius（tan(角径)・距離非依存）、
    // 局所は soft_radius/距離。0 のときはハード 1 本（cone_radius=0）のまま。上限で発散を止める。
    var cone: f32 = 0.0;
    if light.soft_radius > 0.0 {
        if light.kind == LIGHT_KIND_DIRECTIONAL {
            cone = light.soft_radius;
        } else {
            cone = light.soft_radius / max(g.dist, 1e-4);
        }
        cone = min(cone, RT_SHADOW_MAX_CONE_RADIUS);
    }
    g.cone_radius = cone;
    return g;
}
