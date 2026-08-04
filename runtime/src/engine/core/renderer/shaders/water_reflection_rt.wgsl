// ============================================================
//  water_reflection_rt.wgsl — 水面のハイブリッド RT 反射（Phase W5.2）
//
//  ## 役割（単一責任）
//  水面 1 ピクセルの **反射色そのもの**（フレネルは掛けない）を求めて反射 RT へ書く。
//  フレネル合成は水面パス（`water_surface.wgsl`）が行う ＝ 本パスは
//  「その向きに何が見えるか」だけを答える。
//
//  ## 方式は D6（不透明の RT 反射）の流用である
//  `reflection_rt.wgsl` とまったく同じハイブリッド:
//    ・レイを TLAS へ 1 本飛ばす
//    ・ヒット点が **画面内かつ深度一致**（本画面で実際に見えている面）
//        → シーンカラーのグラブをその UV でサンプル
//          ＝ 本画面と同一のソフト影・GI・AO が反射に乗る（SSR 品質）
//    ・画面外／遮蔽裏 → 解析近似（albedo × (直接光/π ＋ 間接光)、**影なし**）
//    ・ミス → 画面内に空が写っていればスカイボックス色、さもなくば GI プローブ／環境光
//  違いは **入力の出どころ**だけ（不透明は G-Buffer、水面はラスタライズした波の法線）。
//
//  ## 不透明版と違う点（意図的な簡略化）
//  ・**ガラス層（0x02）の再トレースを行わない**。反射レイあたり最大 4 本の追加レイは
//    水面全面に掛かるとコストが重く、水面に映るガラスの色付きシルエットは
//    優先度が低いと判断した（不透明側は従来どおり対応済み）。
//
//  ## 連結順
//    [water_height_field, cluster_common, ddgi_common, water_reflection_common, （本ファイル）,
//     （バインドレス時のみ）bindless_common, water_reflection_hit_on ／ さもなくば hit_off]
//      water_height_field      : WaterParams / 高さ場 / 格子頂点（group1・group2 を宣言）
//      cluster_common          : LIGHT_KIND_* 定数
//      ddgi_common             : GiParams / ddgi_sample_irradiance
//      water_reflection_common : カメラ(group0)・グラブと深度(group3)・GI(group4 の 0..4)・頂点段
//      water_reflection_hit_*  : 画面外ヒットのアルベド解決（実テクスチャ／平均色の 2 変種）
//
//  ★同期必須★ `WaterReflLight` は `lighting.rs::GpuLight`（112B）のミラーであり、
//  `reflection_rt.wgsl::GpuLightR` と同一である。片方だけ変えてはならない。
// ============================================================

// ─── 定数（不透明 RT 反射と同値。根拠はそちらのコメント参照）───

/// 拡散 BRDF の 1/π。
const WATER_REFL_PI: f32 = 3.14159265359;
/// レイの最小距離（自己交差回避）。水面自体は TLAS に無いが、岸の地形との
/// 接触点で数値誤差が出るため不透明版と同じ値を入れる。
const WATER_REFL_RAY_TMIN: f32 = 0.001;
/// レイ原点を法線方向へ持ち上げる量（m）。
const WATER_REFL_ORIGIN_N: f32 = 0.02;
/// レイの最大距離（m）。
const WATER_REFL_RAY_TMAX: f32 = 1.0e4;
/// 不透明インスタンスのマスク（`rt_shadow.rs::RT_MASK_OPAQUE` と一致）。
const WATER_REFL_CULL_MASK: u32 = 0x01u;
/// 画面内ヒット採用の深度一致許容（相対）。`reflection_rt.wgsl::HIT_DEPTH_TOLERANCE` と同値。
/// 0 だと浮動小数の丸めで一致がほぼ成立せず機能が死に、大きすぎると手前の別の面を
/// 誤って「同一面」と判定して誤った色を反射する。
const WATER_REFL_HIT_DEPTH_TOLERANCE: f32 = 0.05;

// ─── Group 4（RT 変種の追加分）: ライト・TLAS・平均アルベド ────
//
// binding 0..4（GI＋メタ）は `water_reflection_common.wgsl` が宣言済み。
// **uniform（GiParams）と同居するため binding_array は置けない**＝バインドレス非対応
//（上の「不透明版と違う点」参照）。

/// `lighting.rs::GpuLight`（112B）のミラー。**`reflection_rt.wgsl::GpuLightR` と同一**。
struct WaterReflLight {
    color:            vec3<f32>,
    intensity:        f32,
    position:         vec3<f32>,
    range:            f32,
    direction:        vec3<f32>,
    kind:             u32,
    inner_cos:        f32,
    outer_cos:        f32,
    rect_half_width:  f32,
    rect_half_height: f32,
    rect_right:       vec3<f32>,
    shadow_index:     f32,
    rect_up:          vec3<f32>,
    soft_radius:      f32,
    bounce_intensity: f32,
    _pad0:            f32,
    _pad1:            f32,
    _pad2:            f32,
}

@group(4) @binding(5) var<storage, read> wr_lights: array<WaterReflLight>;
@group(4) @binding(6) var                wr_tlas:   acceleration_structure;
@group(4) @binding(7) var<storage, read> wr_albedo: array<vec4<f32>>;

// ─── 解析近似（画面外ヒット用）────────────────────────────────

/// 距離減衰（`reflection_rt.wgsl::rt_refl_distance_atten` と同式）。
fn water_refl_distance_atten(dist: f32, range: f32) -> f32 {
    let d2      = dist * dist;
    let inv_sqr = 1.0 / max(d2, 1e-4);
    let factor  = d2 / max(range * range, 1e-4);
    let window  = clamp(1.0 - factor * factor, 0.0, 1.0);
    return inv_sqr * window * window;
}

/// ヒット点の直接光放射照度 E（**影なし**）。
/// シャドウレイは 1 本も飛ばさない（不透明側と同じユーザー決定: 画面外ヒットは影を反射しない）。
/// 影付きの反射は「画面内ヒット → グラブ採用」の経路が担う。
fn water_refl_direct_irradiance(hit_pos: vec3<f32>, n: vec3<f32>) -> vec3<f32> {
    var e = vec3<f32>(0.0, 0.0, 0.0);
    let count = min(wr_meta.count, arrayLength(&wr_lights));
    for (var i: u32 = 0u; i < count; i = i + 1u) {
        let light = wr_lights[i];
        var l        = vec3<f32>(0.0, 0.0, 1.0);
        var radiance = light.color * light.intensity;
        if light.kind == LIGHT_KIND_DIRECTIONAL {
            l = normalize(-light.direction);
        } else {
            let to_light = light.position - hit_pos;
            let dist     = length(to_light);
            l        = to_light / max(dist, 1e-4);
            radiance = radiance * water_refl_distance_atten(dist, light.range);
            if light.kind == LIGHT_KIND_SPOT {
                let cos_ang = dot(light.direction, -l);
                radiance = radiance * smoothstep(light.outer_cos, light.inner_cos, cos_ang);
            }
        }
        let ndl = max(dot(n, l), 0.0);
        if ndl <= 0.0 { continue; }
        e = e + radiance * ndl;
    }
    return e;
}

/// アルベドを引けなかったとき（インスタンス番号が範囲外）の中間グレー。
/// ヒット解決の両変種（`water_reflection_hit_on/off.wgsl`）が共有する唯一の既定値。
const WATER_REFL_MISSING_ALBEDO: vec3<f32> = vec3<f32>(0.5, 0.5, 0.5);

// ヒット先のベースカラーを返す `water_refl_hit_albedo(ai, prim_index, bary)` は
// **連結される変種ファイル**が定義する（WGSL はモジュールスコープの前方参照を許す）:
//   on （バインドレス対応）: water_reflection_hit_on.wgsl  … UV 補間 → テクスチャ実サンプル
//   off（従来／非対応 GPU）: water_reflection_hit_off.wgsl … 平均アルベドのベタ塗り
// 分岐をシェーダ内の if にできないのは、binding_array の**宣言そのもの**が
// 非対応 GPU で BGL 生成を落とすため（不透明 RT 反射 D6 とまったく同じ制約）。

// ─── フラグメント ────────────────────────────────────────────

@fragment
fn fs_water_reflection(in: WaterReflVsOut) -> @location(0) vec4<f32> {
    let s = water_refl_surface(in);
    // 不透明物に隠れている水面ピクセルは書かない（RT はクリア値 0 のまま＝反射なし）。
    if s.occluded {
        discard;
    }
    // 反射強度 0（＝この水域では反射を切っている）ならレイを飛ばさずに 0 を返す。
    if s.intensity <= 0.0 {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    var desc: RayDesc;
    desc.flags     = 0u;
    desc.cull_mask = WATER_REFL_CULL_MASK;
    desc.tmin      = WATER_REFL_RAY_TMIN;
    desc.tmax      = WATER_REFL_RAY_TMAX;
    desc.origin    = s.world_pos + s.n * WATER_REFL_ORIGIN_N;
    desc.dir       = s.r;
    var rq: ray_query;
    rayQueryInitialize(&rq, wr_tlas, desc);
    loop {
        if !rayQueryProceed(&rq) { break; }
    }
    let hit = rayQueryGetCommittedIntersection(&rq);

    var reflected: vec3<f32>;
    if hit.kind == RAY_QUERY_INTERSECTION_NONE {
        // ── ミス：画面内の空 → 天球テクスチャ → GI の順で拾う（共通の窓口）──
        reflected = water_refl_miss(desc.origin, s.world_pos, s.r);
    } else {
        let hit_pos = desc.origin + s.r * hit.t;
        // ヒット面の法線は持てない（頂点法線メガバッファは本パスにバインドしていない）ので、
        // 「レイの逆向き＝こちらを向いている」と近似する（不透明 RT 反射と同一の近似）。
        let n_hit = -s.r;

        // ── ハイブリッド：画面内に見えているヒットは本画面のライティング結果を採用 ──
        var used_screen      = false;
        var reflected_screen = vec3<f32>(0.0, 0.0, 0.0);
        let proj = water_refl_project(hit_pos);
        if proj.valid {
            let dim    = vec2<f32>(textureDimensions(t_water_scene));
            let sdepth = water_refl_load_depth(proj.uv * dim);
            // 背景（空）は面が無い＝一致対象外。手前の別の面も下の相対差で弾く。
            if sdepth < WATER_REFL_SKY_DEPTH {
                let scene_world = water_refl_world_from_depth(proj.uv, sdepth);
                let scene_vz    = water_refl_view_z(scene_world);
                let hit_vz      = water_refl_view_z(hit_pos);
                if abs(scene_vz - hit_vz) <= WATER_REFL_HIT_DEPTH_TOLERANCE * max(abs(hit_vz), 1e-4) {
                    reflected_screen =
                        textureSampleLevel(t_water_scene, s_water_scene, proj.uv, 0.0).rgb;
                    used_screen = true;
                }
            }
        }

        if used_screen {
            // 画面内ヒット：グラブ（不透明＋スカイボックスの最終ライティング色）をそのまま採用。
            reflected = reflected_screen;
        } else {
            // 画面外／遮蔽裏：解析近似。明るさ規約は本画面へ揃える
            //   直接光 … Lambert なので albedo/π × E
            //   間接光 … 本画面のアンビエント規約に合わせて /π を掛けない
            // ヒット解決は連結された変種に委ねる（バインドレス対応なら実テクスチャ色、
            // 非対応なら平均色）。画面外ヒットが「一様な灰色の塊」になる W5.2 の
            // 仕様限界は、この on 変種で解消される。
            let albedo   = water_refl_hit_albedo(
                hit.instance_custom_data, hit.primitive_index, hit.barycentrics);
            let direct   = water_refl_direct_irradiance(hit_pos, n_hit);
            let indirect = water_refl_env(hit_pos, n_hit);
            reflected = albedo * (direct / WATER_REFL_PI + indirect);
        }
    }

    // 出力は **プリマルチプライド**（rgb = 反射色 × 強度 / a = 強度）。
    // 詳細は `water_reflection_common.wgsl::water_refl_output` のコメントを参照。
    // パスが走らなかったフレームは RT がクリア値 0 のまま＝反射寄与ゼロになる。
    return water_refl_output(reflected, s.intensity);
}
