// ============================================================
// rt_shadow_on.wgsl  —  インラインレイトレ影 本体（RT 対応パイプライン用）
//
// RT 対応 GPU のパイプライン（mesh_rt.toml / skinned_mesh_rt.toml）に連結される。
// group 4 binding 6 に TLAS（acceleration_structure）を宣言し、フラグメントの
// ライトループから表面→ライト方向の遮蔽レイ（rayQuery, 1 本＝ハードシャドウ）を飛ばす。
//
// 【バインドグループ】group 4（ライト binding0/1 ＋ シャドウ binding2〜5 と同居）
//   binding 6: acceleration_structure（TLAS。Rust: rt_shadow.rs / lighting.rs）
// max_bind_groups=5（group 0〜4）環境に適合（新グループを増やさず binding 追加のみ）。
//
// 関数シグネチャは rt_shadow_off.wgsl と一致させること。
//
// ソフトシャドウ: 面光源の見込み半径（cone_radius）に応じて円錐内へ複数レイを分散する。
//   cone_radius=0 のとき従来どおりハード 1 本へ分岐（コスト増ゼロ）。
//   cone_radius>0 のときは本数を適応化（RT_SHADOW_SAMPLES_MIN..MAX）し、面の幾何的地平線
//   より下を向いたサンプルは平均の母数から除外する。
//   ライト種別ごとの cone_radius の求め方と上限クランプは lighting_eval.wgsl 側で行う
//   （RT_SHADOW_MAX_CONE_RADIUS）。
// ============================================================

/// group 4 binding 6: RT 影用 TLAS。
@group(4) @binding(6) var rt_accel: acceleration_structure;

// ─── 定数 ────────────────────────────────────────────────────

/// レイ最小距離。自己交差を避けるための下限（法線オフセットと併用）。
const RT_SHADOW_TMIN: f32 = 0.001;
/// 自己交差防止の原点オフセット量（幾何法線方向 ε, ワールド単位）。
/// 表面から少し浮かせてレイを飛ばす。押し出しには必ず「幾何法線 Ng」を使うこと
/// （法線マップ適用後のシェーディング法線 N は面から傾いているため、押し出しても
/// 面からのクリアランスが N・Ng 分しか稼げず自己交差＝真っ黒になる）。
const RT_SHADOW_NORMAL_BIAS: f32 = 0.02;
/// スロープスケールバイアスの下限 cos。オフセットを NORMAL_BIAS / max(dot(Ng,L), これ) に
/// することで、グレージング入射（面すれすれの光）ほど押し出し量を増やす。
/// - 根拠: レイが面と成す角が浅いほど、同じ法線オフセットで稼げる「面からのレイの離れ」は
///   sin ≈ dot(Ng,L) に比例して小さくなる。除算で打ち消すと離れ量が角度に依らず一定になる。
/// - 0.1 は clamp 上限 = 1/0.1 = 10 倍（最大オフセット 0.02*10 = 0.2 ワールド単位）を意味する。
///   これ以上小さくすると薄い壁を貫通して光漏れするリスクが上がる。dot(Ng,L) がこの値を
///   下回る領域は ndl≈0 でライト寄与自体がほぼ 0 のため、多少過剰なオフセットでも見た目に出ない。
const RT_SHADOW_SLOPE_MIN_COS: f32 = 0.1;
/// 影レイのインスタンスカリングマスク。TLAS 側インスタンスマスクとの AND が 0 の
/// インスタンスは素通りする（＝影を落とさない）。
/// 不透明ビットのみを立て、Blend / Mask マテリアルを影のオクルーダから除外する。
/// Rust 側 `rt_shadow.rs::RT_MASK_OPAQUE` と値を一致させること
/// （rt_shadow.rs のユニットテスト `wgsl_cull_mask_matches_rust_mask` が両者の一致を検証する）。
/// 将来 Mask（葉・鎖など）を影に含めるなら RT_MASK_NON_OPAQUE のビットもここに OR する
/// （ただし正しいアルファテストには bindless が必要。rt_shadow.rs のコメント参照）。
const RT_SHADOW_CULL_MASK: u32 = 0x01u;
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
/// 有効サンプルとみなす dot(Ng, dir) の下限（幾何的地平線のマージン）。
/// 円錐が広がると一部のサンプル方向が面の地平線より下（dot <= 0）を向く。そこから来る光は
/// そもそもこの面を照らさないため、遮蔽 0 として平均に混ぜてはならない（不当に暗くなる）。
/// 平均の母数から除外する。
/// 0.01（≒ 89.4°）とわずかに正のマージンを取るのは、地平線ちょうどをかすめるレイが
/// 自身のジオメトリと交差して偽の遮蔽を返しやすいため（原点の法線オフセットは円錐中心
/// 方向の dot(Ng,L) で決めており、地平線すれすれの方向には足りない）。
const RT_SHADOW_HORIZON_MIN_COS: f32 = 0.01;
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

/// 表面（origin, 幾何法線 ng）からライト方向 l へ遮蔽レイを飛ばし、遮蔽率を返す。
/// - 戻り値: 1.0=完全照射 / 0.0=完全遮蔽 / 中間=ペナンブラ。
/// - `ng`         : **幾何法線**（法線マップ適用前・三角形の面法線）。レイ原点の押し出しと
///                  裏面判定にのみ使う。シェーディングには使わない（呼び出し側が N を保持）。
/// - `tmax`       : レイの最大距離。directional は大定数、局所光はライトまでの距離。
/// - `cone_radius`: 面光源の見込み半径（tan 相当）。0 でハード 1 本、>0 で円錐内 N サンプル平均。
/// - `frag_xy`    : フラグメント座標（サンプル回転のノイズ源。時間項なし）。
///
/// ソフトシャドウは l を中心とする円錐内へ Vogel ディスク分布でレイを分散し、フラグメント
/// 座標由来の回転を掛けて平均する。遮蔽物から遠い影ほど l の分散角が実効的に大きくなり
/// （cone_radius は directional では距離非依存、局所光では radius/距離）、物理的に正しく
/// 「遠いほどボケる」挙動になる。
///
/// サンプル本数は cone_radius に応じて適応的に増やす（rt_shadow_sample_count 参照）。
/// 広い円錐を固定 4 本で積分すると遮蔽率が 5 段階に量子化され、ピクセルごとの IGN 回転と
/// 相まってディザ状のまだらノイズになるため（＝ライト近傍の面が点描状に崩れる不具合の主因）。
///
/// さらに、円錐が広がって面の幾何的地平線より下（dot(ng, dir) <= 0）を向いたサンプルは
/// 平均から**除外**する。その方向から来る光はそもそもこの面を照らさないため、遮蔽 0 として
/// 足し込むと不当に暗くなる（＆自己ジオメトリに当たって偽の遮蔽を返す）。
fn rt_shadow_factor(
    origin:      vec3<f32>,
    ng:          vec3<f32>,
    l:           vec3<f32>,
    tmax:        f32,
    cone_radius: f32,
    frag_xy:     vec2<f32>,
) -> f32 {
    let ndl = dot(ng, l);

    // 幾何的にライトの裏側を向いている面はレイを飛ばすまでもなく遮蔽（自己遮蔽）扱い。
    // 法線マップで N が傾き ndl_shading > 0 になっていても、面自体が裏なら光は当たらない。
    // ここで打ち切ることでレイ本数も減る（性能）。
    if ndl <= 0.0 {
        return 0.0;
    }

    // 自己交差防止: 原点を幾何法線方向へ押し出す。
    // スロープスケール: グレージング（ndl 小）ほど押し出し量を増やし、レイが面から離れる
    // 実距離を角度に依らず一定に保つ。max(..., RT_SHADOW_SLOPE_MIN_COS) で発散を防ぐ。
    let o = origin + ng * (RT_SHADOW_NORMAL_BIAS / max(ndl, RT_SHADOW_SLOPE_MIN_COS));

    // ハードシャドウ経路（面積ゼロ）: 従来どおり 1 本のみで高速（コスト増ゼロを維持）。
    if cone_radius <= 0.0 {
        return rt_trace_occlusion(o, l, tmax);
    }

    // ペナンブラの広さに応じたサンプル本数（MIN..MAX に必ず収まる）。
    let samples = rt_shadow_sample_count(cone_radius);

    // 円錐サンプル用の直交基底（l に垂直な 2 軸）。
    let t = rt_shadow_perp(l);
    let b = cross(l, t);
    // フラグメントごとの回転（バンディング回避）。時間項を含まないため静止画で安定。
    let rot = rt_shadow_ign(frag_xy) * 2.0 * RT_SHADOW_PI;

    var sum   = 0.0;   // 有効サンプルの遮蔽率の総和
    var valid = 0u;    // 地平線より上を向いた（＝光を受けうる）サンプル数

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

        // 地平線カリング: 面の裏側へ潜ったサンプルは母数からも除外する（0 を足さない）。
        if dot(ng, dir) <= RT_SHADOW_HORIZON_MIN_COS {
            continue;
        }
        sum   += rt_trace_occlusion(o, dir, tmax);
        valid += 1u;
    }

    // 有効サンプルが 1 本も無い＝この面から見て光源はほぼ地平線下にある。
    // 幾何的に光を受けないので完全遮蔽（0.0）を返す。
    // （中心方向は ndl > 0 が保証されているため、ここに来るのは ndl がごく小さく、かつ円錐が
    //   極端に広い場合のみ。その領域は shade_light の ndl 係数でどのみち寄与がほぼ 0 になる。）
    if valid == 0u {
        return 0.0;
    }
    return sum / f32(valid);
}
