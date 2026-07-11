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
// TODO(v1): rect/point のソフトシャドウ（面光源の複数サンプル）は未対応。ハード 1 本のみ。
// ============================================================

/// group 4 binding 6: RT 影用 TLAS。
@group(4) @binding(6) var rt_accel: acceleration_structure;

// ─── 定数 ────────────────────────────────────────────────────

/// レイ最小距離。自己交差を避けるための下限（法線オフセットと併用）。
const RT_SHADOW_TMIN: f32 = 0.001;
/// 自己交差防止の原点オフセット量（法線方向 ε）。表面から少し浮かせてレイを飛ばす。
const RT_SHADOW_NORMAL_BIAS: f32 = 0.02;

// ─── 遮蔽判定 ────────────────────────────────────────────────

/// RT 影が有効か。RT パイプラインでは LightMeta.rt_shadows で実行時分岐する。
fn rt_shadow_enabled() -> bool {
    return u_light_meta.rt_shadows != 0u;
}

/// 表面（origin, 法線 n）からライト方向 l へ遮蔽レイを 1 本飛ばし、遮蔽率を返す。
/// - 戻り値: 1.0=非遮蔽（照射）, 0.0=遮蔽（影）。
/// - `tmax`: レイの最大距離。directional は大きな定数、point/spot/rect はライトまでの距離。
///
/// 不透明ジオメトリのみを対象とし、最初のヒットで打ち切る（ハードシャドウ 1 本）。
fn rt_shadow_factor(origin: vec3<f32>, n: vec3<f32>, l: vec3<f32>, tmax: f32) -> f32 {
    // 自己交差防止: 原点を法線方向へ少し押し出す。
    let o = origin + n * RT_SHADOW_NORMAL_BIAS;

    var desc: RayDesc;
    // 最初のヒットで打ち切り（影は「何かに当たったか」だけが必要）。
    desc.flags     = RAY_FLAG_TERMINATE_ON_FIRST_HIT;
    desc.cull_mask = 0xFFu;               // 全インスタンスを対象（TLAS 側 mask=0xFF）
    desc.tmin      = RT_SHADOW_TMIN;
    desc.tmax      = max(tmax, RT_SHADOW_TMIN);
    desc.origin    = o;
    desc.dir       = l;                   // 面→光源方向（呼び出し側で正規化済み）

    var rq: ray_query;
    rayQueryInitialize(&rq, rt_accel, desc);
    // 不透明ジオメトリのみのため、traverse は候補を自動コミットしながら進む。
    // 打ち切りフラグにより最初のヒットで false を返す。
    rayQueryProceed(&rq);
    let hit = rayQueryGetCommittedIntersection(&rq);

    // ヒットあり（TRIANGLE 等）＝遮蔽。ヒットなし（NONE）＝照射。
    if hit.kind != RAY_QUERY_INTERSECTION_NONE {
        return 0.0;
    }
    return 1.0;
}
