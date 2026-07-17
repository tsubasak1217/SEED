// ============================================================
// rt_shadow_on.wgsl  —  インラインレイトレ影 本体（RT 対応パイプライン用）
//
// RT 対応 GPU のパイプライン（mesh_rt.toml / skinned_mesh_rt.toml）に連結される。
// group 4 binding 6 に TLAS（acceleration_structure）を宣言し、フラグメントの
// ライトループから表面→ライト方向の遮蔽レイ（rayQuery）を飛ばす。
//
// 【バインドグループ】group 4（ライト binding0/1 ＋ シャドウ binding2〜5 と同居）
//   binding 6: acceleration_structure（TLAS。Rust: rt_shadow.rs / lighting.rs）
// max_bind_groups=5（group 0〜4）環境に適合（新グループを増やさず binding 追加のみ）。
//
// 関数シグネチャは rt_shadow_off.wgsl と一致させること。
//
// ============================================================
// ## 3 本の法線の役割分担（最重要・ここを崩すと影が破綻する）
//
//   N  (Surface.normal)        : 法線マップ適用後のシェーディング法線。**BRDF 専用**。
//                                影の計算では一切使わない（本ファイルへは渡ってこない）。
//   Nv (Surface.vertex_normal) : 補間頂点法線（法線マップ前）。三角形をまたいで**滑らか**。
//                                → 影の「減衰カーブ」を決める判定に使う:
//                                   ・シャドウターミネータのランプ（smoothstep）
//                                   ・レイ原点の固定量法線オフセット（押し出し方向）
//                                   ・円錐サンプルの地平線判定（dot(nv,dir)）
//   Ng (Surface.geo_normal)    : 画面微分 cross(dpdx,dpdy) 由来の**フラットな面法線**。
//                                三角形内で一定＝面境界で不連続。
//                                → 「レイ原点を自分の三角形の平面から確実に浮かせる」目的**だけ**:
//                                   ・原点の最小クリアランス（平面から浮かせる）
//                                Ng を**レイの向き**や**遮蔽判定**に使ってはならない（後述）。
//
// なぜ分けるか（退行の原因）: 以前は減衰判定（`dot(Ng,L) <= 0 → return 0.0` の硬い
// カットオフ／地平線カリング）にも Ng を使っていた。Ng は三角形ごとに一定なので、
// 滑らかな曲面（カーテンの折り目など）では隣り合う三角形で判定が 0/1 に飛び、
// **面境界に沿った硬い黒帯（ファセット状の影）**になった。判定を Nv に移すと、
// 判定値が面をまたいで連続に変化するため黒帯が消える。
//
// ## 地平線より下を向くサンプルの扱い（光漏れの再発防止・最重要）
// 円錐サンプル（およびターミネータ帯の中心方向）は、面の地平線より下＝面の裏側を
// 向くことがある。これを **Ng 方向へ「起こす（リフトする）」実装は光漏れを生む**:
//   厚みのない一枚布（カーテン）にライトが裏側からあたるとき、こちら側の面から見た
//   ライト方向は必ず面の裏側にある。リフトはそのレイを手前側（Ng 側）へ折り返すため、
//   レイは布を貫通せずに空へ逃げ、「遮蔽なし＝照らされている」と誤判定される。
//   折り目で Nv がわずかに光側を向くと、ターミネータのランプが 0 にならないため、
//   その誤判定が**点描状の光漏れ**として現れる（この症状の実際の原因だった）。
// 現実装は起こさない。**地平線より下を向くサンプルは遮蔽（0）として数える**:
//   - 幾何的に面の裏側から光は来ない。0 と数えるのは近似ではなく正しい可視性である。
//   - 分母は常に samples 本のままなので、「カリングで分母が暴れる＝ディザノイズ」も出ない。
//   - 判定に使う法線は**滑らかな Nv**（Ng だと三角形ごとに判定が飛び、黒帯が再発する）。
//
// ## ソフトシャドウ
// 面光源の見込み半径（cone_radius）に応じて円錐内へ複数レイを分散する。
//   cone_radius=0 のとき従来どおりハード 1 本へ分岐（コスト増ゼロ）。
//   cone_radius>0 のときは本数を適応化（RT_SHADOW_SAMPLES_MIN..MAX）する。
//   ライト種別ごとの cone_radius の求め方と上限クランプは lighting_eval.wgsl 側
//   （RT_SHADOW_MAX_CONE_RADIUS）。
//
// ## ノイズについて（ディザ状のまだら）
// サンプル方向はピクセルごとに IGN で回転させるため、遮蔽率の量子化がそのまま
// 空間ノイズになる。以下でノイズを抑える:
//   - サンプル数を cone_radius に応じて適応化（4〜16 本）
//   - cone_radius に上限（lighting_eval.wgsl）を設けて円錐の発散を止める
//   - **平均の母数（分母）をピクセル間で変えない**（常に samples 本）
// 旧実装は「地平線より下のサンプルを母数から除外」していたが、除外本数はピクセルごとの
// 回転に依存して変わるため、分母自体がピクセル間でばらつき、それ自体がノイズ源だった。
// 現実装は除外しない。地平線より下のサンプルは **0（遮蔽）として母数に数える**ので、
// 分母は常に samples で一定であり、かつ光漏れも起きない（上記参照）。
// ============================================================

/// group 4 binding 6: RT 影用 TLAS。
@group(4) @binding(6) var rt_accel: acceleration_structure;

/// group 4 binding 14: TLAS インスタンス順の平均アルベド（.rgb）＋パック済み α/transmission（.a）。
/// 色付き影（半透明レイヤー越しの光を染める）で、半透明ヒットの custom_data を添字に引く。
/// GI compute（binding4）と RT リフレクション（別 group）が読むのと同一バッファ（rt_shadow.rs 所有）。
/// - `.rgb`: 生の平均アルベド。**GI/反射が「生アルベド」として読む共有値。意味を変えてはならない**。
/// - `.a`  : 色付き影専用。α（base_color_factor.a）と transmission を各 8bit 固定小数で相乗り
///           （rt_shadow.rs::pack_shadow_alpha_transmission と対。GI/反射は .a を読まないため不干渉）。
@group(4) @binding(14) var<storage, read> rt_shadow_albedo: array<vec4<f32>>;

// ─── 定数 ────────────────────────────────────────────────────

/// レイ最小距離。自己交差を避けるための下限（法線オフセットと併用）。
const RT_SHADOW_TMIN: f32 = 0.001;

/// 自己交差防止の原点オフセット量（ワールド単位）。押し出しは**補間法線 Nv 方向**の固定量。
/// Nv は面をまたいで滑らかなので、オフセット量も滑らかに変化する（＝バイアス由来の
/// 不連続＝黒帯が出ない）。Nv は必ず Ng と同じ半球にある（surface_gather.wgsl が
/// faceforward 済み）ため、この押し出しは常に三角形の平面から「上」へ向かう。
///
/// 【スロープスケールを廃止した理由（光漏れの再発防止・最重要）】
/// 以前はオフセットを NORMAL_BIAS / max(dot(Nv,L), 下限 0.1) と除算でスケールし、グレージング
/// 入射（面すれすれの光）ほど押し出し量を増やしていた。しかし下限 0.1 では最大 0.02/0.1 =
/// 0.2 ワールド単位（Sponza はメートル単位なので 20cm）まで膨らむ。カーテンの折り目どうしの
/// 間隔は数 cm しかないため、レイ原点が**隣の布ヒダを突き抜けた位置**へ飛び、本来遮蔽する
/// はずの布がレイの後方に落ちて当たらなくなる ⇒ 幾何的に光を向いた折り目のフチだけが光る
/// リム状の光漏れになった。よってスロープスケールは廃止し、オフセットは固定 2 項
/// （NORMAL_BIAS + GEO_CLEARANCE）だけにする。総オフセットは最大でも 0.02 + 0.005 =
/// 0.025 ワールド単位（2.5cm）に静的に収まり、薄い布のヒダ間隔を越えない。
///
/// 【グレージング角の自己交差アクネを廃止後も許容できる根拠（現在の防御構成）】
///   (a) 幾何ゲート（lighting_eval.wgsl の geo_gate）が幾何的に裏を向いた面を落とすため、
///       グレージングで自己交差しうる面の大半は影の計算に入る前に寄与が消える。
///   (b) 下のシャドウターミネータランプ（dot(nv,l) < BAND の帯を smoothstep で減光）が、
///       浅い入射角＝アクネの出やすい帯の寄与をそもそも大きく落とす。
/// この 2 段でグレージング時のアクネは実用上問題にならない範囲へ抑えられている。
const RT_SHADOW_NORMAL_BIAS: f32 = 0.02;

/// 幾何法線 Ng 方向の**最小クリアランス**（ワールド単位）。
/// Nv 方向の押し出しだけでは、Nv が三角形の平面から大きく傾いている（＝曲率の高い箇所）とき
/// 平面からの実効クリアランスが dot(Nv,Ng) 倍に落ちる。フラットな Ng 方向へ固定量を足すことで
/// 「その三角形の平面から必ずこの距離だけ浮いている」ことを保証する（自己交差＝真っ黒の防止）。
/// 値 0.005 の根拠: RT_SHADOW_TMIN(0.001) の 5 倍。tmin による打ち切りに埋もれず、かつ
/// RT_SHADOW_NORMAL_BIAS(0.02) の 1/4 なので接触影（接地点の影）を痩せさせない。
const RT_SHADOW_GEO_CLEARANCE: f32 = 0.005;

/// 影のインスタンスカリングマスク。TLAS 側インスタンスマスクとの AND が 0 の
/// インスタンスは素通りする（＝影を落とさない）。
/// 不透明ビットのみを立て、Blend / Mask マテリアルを影のオクルーダから除外する。
/// Rust 側 `rt_shadow.rs::RT_MASK_OPAQUE` と値を一致させること
/// （rt_shadow.rs のユニットテスト `wgsl_cull_mask_matches_rust_mask` が両者の一致を検証する）。
const RT_SHADOW_CULL_MASK: u32 = 0x01u;

/// 半透明レイヤー（Blend/Mask）のインスタンスマスク（色付き影の第 2 クエリ専用）。
/// Rust 側 `rt_shadow.rs::RT_MASK_NON_OPAQUE`（0x02）と一致させること。
/// 不透明の遮蔽（0x01）で影が確定していないピクセルだけ、このマスクで半透明レイヤーを
/// 追加トレースし、ガラスの透過色を影に乗せる。
const RT_TRANSLUCENT_CULL_MASK: u32 = 0x02u;

/// 色付き影の透過色を累積する最大ヒット数（tmin を先へ進めながら再トレースする回数上限）。
/// inline ray query は最近ヒットしか返さないため、重なった半透明レイヤーを N 枚まで貫く。
/// 4 枚＝ガラスが数枚重なっても色が乗る。これ以上は稀かつコスト線形増のため打ち切る。
const RT_TRANSLUCENT_MAX_HITS: u32 = 4u;

/// 再トレース時に tmin を「直前ヒットの t＋この量」へ進める微小オフセット（同一面の再ヒット防止）。
/// RT_SHADOW_TMIN と同オーダー。ヒット面の厚みより十分小さく、次のレイヤーは取りこぼさない。
const RT_TRANSLUCENT_T_STEP: f32 = 0.002;

/// 色付き影のパック定数（Rust の rt_shadow.rs::pack_shadow_alpha_transmission と対）。
/// rt_shadow_albedo[.a] へ α（base_color_factor.a）と transmission を各 8bit 固定小数で相乗りさせる。
/// .rgb は GI/反射が読む生アルベドなので触れず、GI が読まない .a にだけ 2 値を詰めている。
///   パック: a_q=round(α*QUANT), t_q=round(tr*QUANT), .a = a_q*RADIX + t_q（最大 65535＝f32 整数域内）
///   デコード（下記 rt_trace_translucent_tint）: a_q=floor(.a/RADIX), t_q=.a - a_q*RADIX
/// 値は Rust 側定数と一致させること（rt_shadow.rs のユニットテスト shadow_pack_roundtrip_and_transmittance が担保）。
/// 1 チャンネルの量子化段数（8bit）。Rust の rt_shadow.rs::SHADOW_PACK_QUANT と一致させること。
const SHADOW_PACK_QUANT: f32 = 255.0;
/// α を上位バイトへ寄せる基数（t_q と衝突しない）。Rust の SHADOW_PACK_RADIX と一致させること。
const SHADOW_PACK_RADIX: f32 = 256.0;

/// ソフトシャドウの最小サンプル数（cone_radius > 0 のときに必ず飛ばす遮蔽レイの本数）。
/// 4 本＝遮蔽率が 5 段階に量子化される。細いペナンブラ（cone_radius が小さい）ならこの
/// 粗さは影の境界の数ピクセルに閉じるため知覚されない。
const RT_SHADOW_SAMPLES_MIN: u32 = 4u;

/// ソフトシャドウの最大サンプル数（適応サンプルの上限）。
/// ループ回数を必ずこの定数で縛ること（cone_radius がどれだけ大きくても 1 ライトあたりの
/// レイ本数がこれを超えない＝最悪コストが静的に決まる）。
/// 16 本＝遮蔽率が 17 段階になり、IGN 回転と合わせればディザが視認しづらい程度に細かい。
/// これ以上増やしてもノイズの低減は √N でしか効かず、コストは線形に増える。
const RT_SHADOW_SAMPLES_MAX: u32 = 16u;

/// 適応サンプル数の傾き: 「サンプル 1 本が受け持つ cone_radius の幅」。
/// samples = clamp(ceil(cone_radius / この値), MIN, MAX) とすることで、ペナンブラが
/// 広い（＝1 サンプルあたりの担当立体角が大きい＝量子化が目立つ）ほど本数を増やす。
///
/// 値 0.03125 の根拠: lighting_eval.wgsl の RT_SHADOW_MAX_CONE_RADIUS(=0.5) を
/// RT_SHADOW_SAMPLES_MAX(=16) で割った値。すなわち「クランプ上限まで広がった円錐で
/// ちょうど最大サンプル数に到達する」ように傾きを決めている。3 定数の整合は Rust 側の
/// ユニットテスト（rt_shadow.rs::wgsl_soft_shadow_constants_are_consistent）が担保する。
const RT_SHADOW_CONE_RADIUS_PER_SAMPLE: f32 = 0.03125;

/// シャドウターミネータのランプ幅（**Nv 基準**の dot(Nv,L) の帯幅）。
/// dot(Nv,L) が 0（幾何的に光に背を向ける）→ この値（十分に光を受ける）にかけて
/// 遮蔽率を smoothstep で 0 → 1 に上げる。硬いカットオフ（<=0 で即 0.0）の置き換え。
///
/// なぜ 0 に落とす必要があるのか（＝ランプの終端を 0 にする根拠）:
///   法線マップで N が持ち上がると、幾何的には光に背を向けている面でも BRDF の
///   ndl = dot(N,L) が正になり、**壁の裏側から光が漏れる**。これを止められるのは影の係数だけ
///   （ndl は N 由来なので止まらない）。ゆえに遮蔽率は 0 まで落とす。ただし**滑らかに**落とす。
///
/// 値 0.15 の根拠: cos = 0.15 → 光の入射角 ≈ 81.4°。この帯の中では BRDF の ndl も 0.15 以下、
/// すなわち最大照度の 15% 未満しか光を受けない。そこを滑らかに減光しても「面が急に暗くなる」
/// 帯としては知覚されない。逆にこれ以上広げると、正当に照らされている領域まで影の係数で
/// 減光してしまい、全体が暗く沈む。
///
/// ## サンプルの地平線判定との二重適用にならない根拠
/// この帯は **円錐の中心方向 L** の関数（dot(Nv,L)）、地平線判定は **各サンプル方向** の関数。
/// 役割が違い、効く領域もほぼ重ならない:
///   - ハード影（cone_radius = 0）: サンプルは L 一本のみで dot(Nv,L) > 0 が保証済み
///     （関数冒頭の早期 return）。地平線判定は一度も発火しない ⇒ ランプ単独。
///   - dot(Nv,L) >= BAND（＝ランプ = 1.0、正当に照らされる領域）: ランプは減光しない。
///     ここで円錐の一部が地平線より下に落ちるのは、**光源の円盤の一部が本当に地平線の下に
///     沈んでいる**からで、その分の減光は物理的に正しい可視性 ⇒ 過剰な暗化ではない。
///   - 両方が 1 未満になるのは dot(Nv,L) ∈ (0, 0.15) の帯だけ。この帯は BRDF の ndl 自体が
///     0.15 未満（最大照度の 15% 未満）で、そもそもほぼ黒い領域である。ここでランプが
///     必要なのは「法線マップで N が持ち上がり ndl が正になる」ケースを潰すため（上記）で、
///     地平線判定だけでは 0 に落ちきらない（dot(Nv,L) → +0 でも円錐の半分は地平線の上に残る）。
///     ゆえに両者は重複ではなく、それぞれ別の抜け道を塞いでいる。
const RT_SHADOW_TERMINATOR_BAND_COS: f32 = 0.15;

/// 円周率（本ファイルで自己完結させる）。
const RT_SHADOW_PI: f32 = 3.14159265359;
/// 黄金角（ラジアン）。Vogel ディスク分布のサンプル間角度。
const RT_SHADOW_GOLDEN_ANGLE: f32 = 2.39996323;

// ─── 遮蔽判定 ────────────────────────────────────────────────

/// RT 影が有効か。RT パイプラインでは LightMeta.rt_shadows で実行時分岐する。
fn rt_shadow_enabled() -> bool {
    return u_light_meta.rt_shadows != 0u;
}

/// 単一の遮蔽レイを飛ばして遮蔽率を返す（1.0=非遮蔽 / 0.0=遮蔽）。
/// 不透明ジオメトリのみを対象とし、最初のヒットで打ち切る。
fn rt_trace_occlusion(o: vec3<f32>, dir: vec3<f32>, tmax: f32) -> f32 {
    var desc: RayDesc;
    // 最初のヒットで打ち切り（影は「何かに当たったか」だけが必要）。
    desc.flags     = RAY_FLAG_TERMINATE_ON_FIRST_HIT;
    desc.cull_mask = RT_SHADOW_CULL_MASK; // 不透明インスタンスのみを対象（Blend/Mask は素通り）
    desc.tmin      = RT_SHADOW_TMIN;
    desc.tmax      = max(tmax, RT_SHADOW_TMIN);
    desc.origin    = o;
    desc.dir       = dir;

    var rq: ray_query;
    rayQueryInitialize(&rq, rt_accel, desc);
    rayQueryProceed(&rq);
    let hit = rayQueryGetCommittedIntersection(&rq);

    // ヒットあり（TRIANGLE 等）＝遮蔽。ヒットなし（NONE）＝照射。
    if hit.kind != RAY_QUERY_INTERSECTION_NONE {
        return 0.0;
    }
    return 1.0;
}

/// 半透明レイヤー（cull_mask 0x02）越しの光を染める透過率（RGB）を返す（色付き影）。
/// 不透明で遮蔽されていない方向へ、半透明インスタンスだけをトレースし、ヒットするたびに
/// そのプリミティブの平均アルベド・被覆 α・透過率 tr で光をフィルタする（T = (1-α)+α·tr·albedo）。
/// inline ray query は最近ヒットしか返さないため、tmin をヒットの先へ進めながら最大
/// RT_TRANSLUCENT_MAX_HITS 回再トレースして重なったガラスを貫く。ヒットが無ければ vec3(1)（＝色を付けない）。
/// - `o`   : レイ原点（自己交差防止のオフセット適用済み）。
/// - `dir` : 光源方向（rt_trace_occlusion と同じ向き）。
/// - `tmax`: レイ最大距離（光源までの距離 or directional の大定数）。
fn rt_trace_translucent_tint(o: vec3<f32>, dir: vec3<f32>, tmax: f32) -> vec3<f32> {
    var tint = vec3<f32>(1.0, 1.0, 1.0);
    var tmin = RT_SHADOW_TMIN;

    // ループ上限は定数で静的に固定（1 ピクセルあたりのレイ本数が上限を超えない）。
    for (var i: u32 = 0u; i < RT_TRANSLUCENT_MAX_HITS; i = i + 1u) {
        var desc: RayDesc;
        // 最近ヒットを取り、次の反復で tmin をその先へ進める（TERMINATE は付けない）。
        desc.flags     = RAY_FLAG_NONE;
        desc.cull_mask = RT_TRANSLUCENT_CULL_MASK; // 半透明インスタンスのみ
        desc.tmin      = tmin;
        desc.tmax      = max(tmax, tmin);
        desc.origin    = o;
        desc.dir       = dir;

        var rq: ray_query;
        rayQueryInitialize(&rq, rt_accel, desc);
        rayQueryProceed(&rq);
        let hit = rayQueryGetCommittedIntersection(&rq);

        // これ以上半透明レイヤーは無い＝現在の透過色で確定。
        if hit.kind == RAY_QUERY_INTERSECTION_NONE {
            break;
        }

        // 平均アルベド（.rgb）＋パック済み α/transmission（.a）を custom_data で引く。
        // .a は rt_shadow.rs::pack_shadow_alpha_transmission が
        //   a_q = round(α * SHADOW_PACK_QUANT), t_q = round(tr * SHADOW_PACK_QUANT)
        //   .a  = a_q * SHADOW_PACK_RADIX + t_q
        // と詰めた固定小数（f32 の整数完全表現域内）。ここでその逆でデコードする。
        // 範囲外インデックスは何もフィルタしない（tint 不変＝白のまま＝色を付けない）。
        let ai = hit.instance_custom_data;
        if ai < arrayLength(&rt_shadow_albedo) {
            let entry  = rt_shadow_albedo[ai];
            let packed = entry.a;
            let a_q    = floor(packed / SHADOW_PACK_RADIX);
            let t_q    = packed - a_q * SHADOW_PACK_RADIX;
            let alpha  = clamp(a_q / SHADOW_PACK_QUANT, 0.0, 1.0); // 被覆（base_color_factor.a）
            let tr     = clamp(t_q / SHADOW_PACK_QUANT, 0.0, 1.0); // 透過率（KHR_materials_transmission）
            // 1 枚の Blend 面を通る光の RGB 透過率:
            //   T = (1-α) + α·transmission·albedo.rgb
            //   ・α=1, tr=1 → T = albedo   （色の付いた影＝色ガラスは baseColor で透過光を濾過する）
            //   ・α=1, tr=0 → T = 0        （透過しない被覆面は光を通さない＝暗い影）
            //   ・α=0        → T = 1        （影なし）
            // 非透過成分 α·(1-tr) は光を通さない（＝0）ため式に現れない。透過光だけがアルベドで色付く。
            let layer_t = vec3<f32>(1.0 - alpha) + alpha * tr * entry.rgb;
            tint = tint * layer_t;
        }

        // 次のレイヤーへ: tmin を直前ヒットの手前まで進める（同一面の再ヒットを避ける）。
        tmin = hit.t + RT_TRANSLUCENT_T_STEP;
        if tmin >= tmax {
            break;
        }
    }

    return tint;
}

/// Interleaved Gradient Noise（Jimenez）。フラグメント座標から [0,1) の擬似乱数を返す。
/// 時間項を含まないため TAA 非前提でも時間的ちらつきが出ない（空間的にのみ変化する）。
fn rt_shadow_ign(p: vec2<f32>) -> f32 {
    return fract(52.9829189 * fract(dot(p, vec2<f32>(0.06711056, 0.00583715))));
}

/// ペナンブラの広さ（cone_radius）に応じた適応サンプル数を返す。
/// - cone_radius が小さい（＝影の境界が細い）ほど少なく、広いほど多く。
/// - 戻り値は必ず [RT_SHADOW_SAMPLES_MIN, RT_SHADOW_SAMPLES_MAX] に収まる。
/// 呼び出し側は cone_radius > 0 のときだけ使う（0 はハード 1 本経路）。
fn rt_shadow_sample_count(cone_radius: f32) -> u32 {
    // 1 本が受け持つ円錐幅を RT_SHADOW_CONE_RADIUS_PER_SAMPLE に保つ本数を求め、上下限で締める。
    let raw = ceil(cone_radius / RT_SHADOW_CONE_RADIUS_PER_SAMPLE);
    let n   = clamp(raw, f32(RT_SHADOW_SAMPLES_MIN), f32(RT_SHADOW_SAMPLES_MAX));
    return u32(n);
}

/// ベクトル v に直交する任意の単位ベクトルを 1 本作る（円錐サンプル基底の第 1 軸）。
fn rt_shadow_perp(v: vec3<f32>) -> vec3<f32> {
    // v と平行になりにくい軸を選んで外積を取る。
    let a = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(v.x) < 0.9);
    return normalize(cross(a, v));
}

/// 表面からライト方向 l へ遮蔽レイを飛ばし、遮蔽率を返す。
/// - 戻り値: 1.0=完全照射 / 0.0=完全遮蔽 / 中間=ペナンブラ。
/// - `origin`     : 表面のワールド座標。
/// - `ng`         : **幾何法線**（フラットな面法線）。**レイ原点の最小クリアランス専用**。
///                  レイの向き・遮蔽判定には一切使わない（使うと光漏れ／黒帯が出る）。
/// - `nv`         : **補間頂点法線**（法線マップ前・滑らか）。減衰判定に使う
///                  （ターミネータランプ／レイ原点の法線オフセット方向／サンプルの地平線判定）。
/// - `l`          : 面から光源への方向（正規化済み）。
/// - `tmax`       : レイの最大距離。directional は大定数、局所光はライトまでの距離。
/// - `cone_radius`: 面光源の見込み半径（tan 相当）。0 でハード 1 本、>0 で円錐内 N サンプル平均。
/// - `frag_xy`    : フラグメント座標（サンプル回転のノイズ源。時間項なし）。
///
/// ## 手順
/// 1. ターミネータ係数 = smoothstep(0, BAND, dot(nv, l))。0 なら即 0.0（レイも飛ばさない）。
///    硬いカットオフ（dot <= 0 で即 0）をやめ、滑らかな帯で 0 へ落とす。判定は**滑らかな nv**
///    なので、三角形の面境界に沿った黒帯（ファセット）が出ない。
/// 2. レイ原点 = origin + nv * (固定法線バイアス) + ng * (最小クリアランス)。両者とも固定量で、
///    総オフセットは最大でも NORMAL_BIAS + GEO_CLEARANCE = 0.025 ワールド単位に収まる
///    （スロープスケールは廃止。グレージングでオフセットが 20cm まで膨らんで薄い布のヒダ間隔を
///    越え、レイ原点が隣のヒダを突き抜けて遮蔽が外れる＝リム状の光漏れを防ぐため）。
///    前者が自己交差の主防御、後者は「その三角形の平面から必ず浮いている」ことを保証する。
/// 3. 円錐内へ Vogel ディスク分布でレイを分散する。地平線（dot(nv,dir) <= 0）より下を向く
///    サンプルは**レイを飛ばさず 0（遮蔽）として加算**する。方向を起こしてはならない
///    （＝一枚布の裏側へ抜けるべきレイが手前へ逃げ、光漏れになる。ファイル冒頭参照）。
///    サンプルは 1 本も捨てない（分母は常に samples ＝ ディザノイズが出ない）。
/// 4. 平均 × ターミネータ係数（スカラー可視性）に、半透明レイヤーの透過色（RGB）を掛けて返す。
///    透過色は u_light_meta.translucency_rt==1（RT-Translucency 有効）かつ不透明で遮蔽されて
///    いないときだけ累積する。無効時は vec3(1)（色を付けない＝従来の白い影）。
///
/// 戻り値は vec3<f32>（RGB 透過率）。呼び出し側 lighting_eval.wgsl は `radiance *= factor` を
/// 成分ごとの積として評価する（ガラス越しの光が色を帯びる）。
fn rt_shadow_factor(
    origin:      vec3<f32>,
    ng:          vec3<f32>,
    nv:          vec3<f32>,
    l:           vec3<f32>,
    tmax:        f32,
    cone_radius: f32,
    frag_xy:     vec2<f32>,
) -> vec3<f32> {
    // ── 1. シャドウターミネータ（滑らかなランプ）────────────────
    // 幾何的な入射角は**補間法線**で測る。フラットな面法線で測ると面境界で不連続になる。
    let ndl_v = dot(nv, l);
    // 完全に光へ背を向けている（ランプの値も 0）。レイを飛ばす意味がないので即返す。
    if ndl_v <= 0.0 {
        return vec3<f32>(0.0, 0.0, 0.0);
    }
    // 0 → BAND で 0 → 1。dot が 0 に近づくほど連続的に遮蔽へ寄せる。
    let terminator = smoothstep(0.0, RT_SHADOW_TERMINATOR_BAND_COS, ndl_v);

    // ── 2. レイ原点（自己交差防止）──────────────────────────────
    // (a) 補間法線方向の固定オフセット（滑らか）。スロープスケール（除算）は廃止した:
    //     グレージングで最大 0.2 ワールド単位（20cm）まで膨らみ、薄い布のヒダ間隔（数 cm）を
    //     越えてレイ原点が隣のヒダを突き抜け、遮蔽が外れてリム状の光漏れになったため。
    // (b) 幾何法線方向の最小クリアランス（三角形の平面から必ず浮かせる）。
    // nv と ng は同じ半球（faceforward 済み）なので、両項とも必ず平面から上向きに効く。
    // 総オフセットは最大でも NORMAL_BIAS + GEO_CLEARANCE = 0.025 ワールド単位（2.5cm）に静的に収まる。
    let o = origin
          + nv * RT_SHADOW_NORMAL_BIAS
          + ng * RT_SHADOW_GEO_CLEARANCE;

    // ── 3. 不透明レイヤーのスカラー可視性（0..1）を求める ─────────
    var vis: f32;
    if cone_radius <= 0.0 {
        // ハードシャドウ経路（面積ゼロ）: 1 本のみで高速（コスト増ゼロを維持）。
        // 方向は l そのまま。ng でリフトしてはならない（薄い面の裏側へ抜けるべきレイが
        // 手前へ折り返され、光漏れになる）。dot(nv,l) > 0 は上の早期 return で保証済み。
        vis = rt_trace_occlusion(o, l, tmax);
    } else {
        // ソフトシャドウ経路: ペナンブラの広さに応じたサンプル本数（MIN..MAX に必ず収まる）。
        let samples = rt_shadow_sample_count(cone_radius);

        // 円錐サンプル用の直交基底（l に垂直な 2 軸）。
        let t = rt_shadow_perp(l);
        let b = cross(l, t);
        // フラグメントごとの回転（バンディング回避）。時間項を含まないため静止画で安定。
        let rot = rt_shadow_ign(frag_xy) * 2.0 * RT_SHADOW_PI;

        var sum = 0.0;

        // ループ上限は必ず定数 RT_SHADOW_SAMPLES_MAX で縛る（samples は clamp 済みだが、
        // 万一の異常値でも 1 ピクセルあたりのレイ本数が静的上限を超えないことを保証する）。
        for (var i: u32 = 0u; i < RT_SHADOW_SAMPLES_MAX; i = i + 1u) {
            if i >= samples {
                break;
            }
            // Vogel ディスク: 半径 √((i+0.5)/N)、角度 i*黄金角 + 回転。
            let fi    = f32(i) + 0.5;
            let r     = sqrt(fi / f32(samples));
            let theta = f32(i) * RT_SHADOW_GOLDEN_ANGLE + rot;
            let disk  = vec2<f32>(cos(theta), sin(theta)) * r * cone_radius;
            // l を円錐内へずらして正規化（見込み半径 cone_radius のディスクを張る）。
            let dir   = normalize(l + t * disk.x + b * disk.y);

            // 地平線判定（**滑らかな nv 基準**。ng だと三角形ごとに飛んでファセット状の黒帯になる）。
            // 面の裏側を向くサンプルは幾何的に光を受け取れない。0（遮蔽）として**加算せず**、
            // レイも飛ばさない（トレース 1 本ぶん速い）。捨てない＝分母は samples のまま一定。
            if dot(nv, dir) <= 0.0 {
                continue;
            }
            sum += rt_trace_occlusion(o, dir, tmax);
        }

        // 分母は常に samples（地平線より下のサンプルも 0 として母数に含む）。
        // ピクセルごとの IGN 回転で分母が変動しない＝ディザ状のまだらノイズが出ない。
        vis = sum / f32(samples);
    }

    // ── 4. 色付き影（半透明レイヤーの透過色）─────────────────────
    // RT-Translucency 有効（translucency_rt==1）かつ不透明で完全遮蔽されていない（vis>0）ときだけ、
    // 中心方向 L に沿って半透明レイヤーの透過色を 1 本累積する（コスト有界）。無効時は白のまま。
    // シャドウマップ（二値）では色を持てないため、色付き影は影=rt のときだけ効く。
    var tint = vec3<f32>(1.0, 1.0, 1.0);
    if (u_light_meta.translucency_rt & TRANSLUCENCY_RT_COLORED_SHADOW) != 0u && vis > 0.0 {
        tint = rt_trace_translucent_tint(o, l, tmax);
    }

    // スカラー可視性 × ターミネータ係数（幾何的に光へ背を向ける側へ滑らかに 0）× 透過色。
    return vec3<f32>(terminator * vis) * tint;
}
