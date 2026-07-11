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
//   ライト種別ごとの cone_radius の求め方は shader_fragment.wgsl 側で行う。
// ============================================================

/// group 4 binding 6: RT 影用 TLAS。
@group(4) @binding(6) var rt_accel: acceleration_structure;

// ─── 定数 ────────────────────────────────────────────────────

/// レイ最小距離。自己交差を避けるための下限（法線オフセットと併用）。
const RT_SHADOW_TMIN: f32 = 0.001;
/// 自己交差防止の原点オフセット量（法線方向 ε）。表面から少し浮かせてレイを飛ばす。
const RT_SHADOW_NORMAL_BIAS: f32 = 0.02;
/// ソフトシャドウのサンプル数（円錐内へ分散する遮蔽レイの本数）。
/// 品質と負荷のトレードオフ。4 で概ね自然なペナンブラ。増やすほど滑らかだが線形にコスト増。
const RT_SHADOW_SAMPLES: u32 = 4u;
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
    desc.cull_mask = 0xFFu;               // 全インスタンスを対象（TLAS 側 mask=0xFF）
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

/// ベクトル v に直交する任意の単位ベクトルを 1 本作る（円錐サンプル基底の第 1 軸）。
fn rt_shadow_perp(v: vec3<f32>) -> vec3<f32> {
    // v と平行になりにくい軸を選んで外積を取る。
    let a = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(v.x) < 0.9);
    return normalize(cross(a, v));
}

/// 表面（origin, 法線 n）からライト方向 l へ遮蔽レイを飛ばし、遮蔽率を返す。
/// - 戻り値: 1.0=完全照射 / 0.0=完全遮蔽 / 中間=ペナンブラ。
/// - `tmax`       : レイの最大距離。directional は大定数、局所光はライトまでの距離。
/// - `cone_radius`: 面光源の見込み半径（tan 相当）。0 でハード 1 本、>0 で円錐内 N サンプル平均。
/// - `frag_xy`    : フラグメント座標（サンプル回転のノイズ源。時間項なし）。
///
/// ソフトシャドウは l を中心とする円錐内へ RT_SHADOW_SAMPLES 本を Vogel ディスク分布で
/// 分散し、フラグメント座標由来の回転を掛けて平均する。遮蔽物から遠い影ほど l の
/// 分散角が実効的に大きくなり（cone_radius は directional では距離非依存、局所光では
/// radius/距離）、物理的に正しく「遠いほどボケる」挙動になる。
fn rt_shadow_factor(
    origin:      vec3<f32>,
    n:           vec3<f32>,
    l:           vec3<f32>,
    tmax:        f32,
    cone_radius: f32,
    frag_xy:     vec2<f32>,
) -> f32 {
    // 自己交差防止: 原点を法線方向へ少し押し出す。
    let o = origin + n * RT_SHADOW_NORMAL_BIAS;

    // ハードシャドウ経路（面積ゼロ）: 従来どおり 1 本のみで高速。
    if cone_radius <= 0.0 {
        return rt_trace_occlusion(o, l, tmax);
    }

    // 円錐サンプル用の直交基底（l に垂直な 2 軸）。
    let t = rt_shadow_perp(l);
    let b = cross(l, t);
    // フラグメントごとの回転（バンディング回避）。時間項を含まないため静止画で安定。
    let rot = rt_shadow_ign(frag_xy) * 2.0 * RT_SHADOW_PI;

    var sum = 0.0;
    for (var i: u32 = 0u; i < RT_SHADOW_SAMPLES; i = i + 1u) {
        // Vogel ディスク: 半径 √((i+0.5)/N)、角度 i*黄金角 + 回転。
        let fi    = f32(i) + 0.5;
        let r     = sqrt(fi / f32(RT_SHADOW_SAMPLES));
        let theta = f32(i) * RT_SHADOW_GOLDEN_ANGLE + rot;
        let disk  = vec2<f32>(cos(theta), sin(theta)) * r * cone_radius;
        // l を円錐内へずらして正規化（見込み半径 cone_radius のディスクを張る）。
        let dir   = normalize(l + t * disk.x + b * disk.y);
        sum += rt_trace_occlusion(o, dir, tmax);
    }
    return sum / f32(RT_SHADOW_SAMPLES);
}
