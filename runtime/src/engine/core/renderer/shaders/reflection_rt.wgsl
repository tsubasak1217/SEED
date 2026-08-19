// ============================================================
// reflection_rt.wgsl — レイトレ反射（fragment fs_rt, RAY_QUERY 必須）
//
// G-Buffer から反射ベクトル R を作り、TLAS へ closest-hit レイを 1 本飛ばす。
// ヒット点は【ハイブリッド】でシェーディングする:
//   画面内かつ深度一致（そのヒット面が本画面で実際に見えている）→ scene_hdr（本画面の
//     不透明ライティング済みコピー）を射影 UV でサンプル。本画面と同一のソフト影・GI・AO・
//     バウンスが乗る＝「画面内ヒットは実レンダリング結果（影込み）で反射する」。
//   画面外／深度不一致（本画面で遮蔽され見えていない面）→ 解析近似
//     （albedo*(direct/π + indirect)。間接光は本画面アンビエント規約に合わせ /π なし）。
//     ★この解析近似は【影なし】である（下記「設計判断」）。画面外ヒットは影を反射しない。★
// いずれもフレネル・粗面フェードを掛けて RT_REFLECTION へ出力する。
// レイのミス時は DDGI（無効なら環境光）へフォールバックする。
//
// 連結順: [cluster_common, reflection_common, ddgi_common, reflection_rt]
//   cluster_common   : LIGHT_KIND_* 定数
//   reflection_common: group0/1/2・純関数・reflection_world_pos
//   ddgi_common      : GiParams・ddgi_sample_irradiance
//
// ★同期必須★ 下記 rt_refl_distance_atten は shaders/ddgi_probe_update.wgsl の
// gi_distance_atten の移植である。これを変えたら両ファイルを合わせること。
//
// ── 設計判断（ユーザー決定 2026-07）: 画面外ヒットは影の反射なし ──
//   画面内ヒットは scene_hdr サンプル＝本画面の実レンダリング結果（ソフト影込み）を採用する。
//   一方、画面外ヒット（解析近似側）は【影の反射なしで OK】と決定された。よって
//   rt_refl_direct_irradiance は全ライトを影なしで加算する（距離減衰・スポット角・N·L は維持）。
//   加えてヒット点へアンビエント/DDGI の床（rt_refl_hit_indirect）を足し、直接光の下限を持たせる。
//   反射レイあたりのシャドウレイは 0 本（従来の上位灯シャドウレイ機構を撤去＝コスト削減）。
//   なお DDGI 側 gi_direct_irradiance は従来のまま（主要光1灯のみ影）で、本ファイルとは独立（触らない）。
// 別ファイルにしている理由: ddgi_probe_update.wgsl は group0 に compute 専用バインディングを
// 宣言しており、反射（fragment, group3/4）へ連結するとグループが混入して破綻するため。
// ============================================================

const RT_REFL_PI:        f32 = 3.14159265359;
const RT_REFL_RAY_TMIN:  f32 = 0.001;
const RT_REFL_ORIGIN_N:  f32 = 0.02;
const RT_REFL_RAY_TMAX:  f32 = 1.0e4;
const RT_REFL_CULL_MASK: u32 = 0x01u;

// ── ガラス対応（実装 A）用の定数 ─────────────────────────────
/// 反射レイに映すガラス（半透明レイヤー）のインスタンスマスク（0x02）。rt_shadow.rs::RT_MASK_NON_OPAQUE と一致。
const RT_REFL_TRANSLUCENT_MASK: u32 = 0x02u;
/// 反射レイが貫くガラス層の最大枚数（tmin を進めながら再トレースする上限＝コスト有界。定数化）。
const RT_REFL_MAX_GLASS_HITS: u32 = 4u;
/// ガラス層の再トレース時に tmin を直前ヒットの先へ進める微小オフセット（同一面の再ヒット回避）。
const RT_REFL_GLASS_T_STEP: f32 = 0.002;
/// 色付き影と同一のパック定数（rt_shadow.rs::SHADOW_PACK_QUANT / SHADOW_PACK_RADIX と一致させること）。
/// rt_albedo[.a] から被覆 α・透過率 transmission をデコードする（refract_rt.wgsl::refract_layer_tint と同一）。
const RT_REFL_PACK_QUANT: f32 = 255.0;
const RT_REFL_PACK_RADIX: f32 = 256.0;
/// ガラス表面ツヤ（フレネル反射の代用）の強さ。
/// 【判断根拠（ハイライト近似）】法線メガバッファは反射パスの group3 に**バインドされていない**
/// （reflection.rs の rt_data_bgl は instance_table/UV/index/テクスチャ配列のみで、頂点法線
/// メガバッファを含まない）。よって反射レイが当たったガラス面の補間法線を復元できず、
/// 視点依存のフレネル項（f0 + (1-f0)(1-cosθ)^5）を計算できない。BGL＋frame_renderer 配線を
/// 増やして法線を追加バインドする手もあるが、ツヤは副次的な見た目でありコストに見合わないと判断し、
/// (1-T) ベースの中立ツヤ（層の遮蔽量に比例した白ツヤ）で近似する（タスクの簡易案どおり）。
const RT_REFL_GLASS_SHEEN: f32 = 0.06;

// 画面内ヒット採用（ハイブリッド）の深度一致許容（相対）。
// 反射レイのヒット点を screen へ射影し、その UV の G-Buffer 深度から復元したビュー深度と
// ヒット点のビュー深度の【相対差】がこの割合以内なら「そのヒット面は本画面で実際に見えている」
// と判定し、解析近似ではなく scene_hdr（本画面の不透明ライティング済みコピー）をサンプルして
// 反射色に採用する。本画面と同一のソフト影・GI・AO・バウンスが反射に乗る＝影の濃さが一致する。
// 相対 5% は shadow_mask / joint bilateral の深度一致判定と同じ流儀に揃えたもの。
const HIT_DEPTH_TOLERANCE: f32 = 0.05;

struct GpuLightR {
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

struct LightMetaR {
    count:             u32,
    rt_shadows:        u32,
    view_mode:         u32,
    _pad2:             u32,
    ambient_color:     vec3<f32>,
    ambient_intensity: f32,
}

@group(3) @binding(0) var<storage, read> rt_lights: array<GpuLightR>;
// meta は storage で読む（バインドレス B2: group3 は binding_array を含むため uniform 不可。
// レイアウトは 32B で uniform と一致。reflection.rs 側 BGL も storage_ro(1) に合わせる）。
@group(3) @binding(1) var<storage, read> rt_meta:   LightMetaR;
@group(3) @binding(2) var                rt_tlas:   acceleration_structure;
@group(3) @binding(3) var<storage, read> rt_albedo: array<vec4<f32>>;

@group(4) @binding(0) var<uniform> rt_gi:    GiParams;
@group(4) @binding(1) var          t_gi_irr: texture_2d<f32>;
@group(4) @binding(2) var          t_gi_vis: texture_2d<f32>;
@group(4) @binding(3) var          s_gi:     sampler;

fn rt_refl_distance_atten(dist: f32, range: f32) -> f32 {
    let d2 = dist * dist;
    let inv_sqr = 1.0 / max(d2, 1e-4);
    let factor = d2 / max(range * range, 1e-4);
    let window = clamp(1.0 - factor * factor, 0.0, 1.0);
    return inv_sqr * window * window;
}

// ワールド座標のビュー空間 Z（カメラ前方が負）。画面内ヒットの深度一致判定に使う。
// u_camera.view は列優先アップロード済みのため view*world で正しくビュー座標になる
// （lighting_eval.wgsl / reflection_ssr.wgsl の ssr_view_z と同一）。
fn rt_refl_view_z(world_pos: vec3<f32>) -> f32 {
    return (u_camera.view * vec4<f32>(world_pos, 1.0)).z;
}

// ワールド座標 → screen UV 射影の結果（UV と画面内フラグ）。
struct RtReflProj { uv: vec2<f32>, valid: bool }

// ワールド座標を view_proj で screen UV へ射影する（逆行列不要）。
// clip.w<=0（カメラ背後）や UV が [0,1] の外なら valid=false。
// reflection_ssr.wgsl の ssr_project と同一手法（RT でも共有したいが SSR ファイルは RT へ
// 連結されないため、ここに同式を置く）。
fn rt_refl_project(world_pos: vec3<f32>) -> RtReflProj {
    var r: RtReflProj;
    let clip = u_camera.view_proj * vec4<f32>(world_pos, 1.0);
    if clip.w <= 0.0 {
        r.uv    = vec2<f32>(0.0, 0.0);
        r.valid = false;
        return r;
    }
    let ndc = clip.xyz / clip.w;
    // NDC → **ビューポート相対** UV。画面内判定はこちらで行う（黒帯の外は画面外）。
    let uv_vp = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    r.valid = all(uv_vp >= vec2<f32>(0.0, 0.0)) && all(uv_vp <= vec2<f32>(1.0, 1.0));
    // 返すのは **RT 全面基準** UV。深度・G-Buffer・scene_hdr はすべて RT 全面で
    // 生成されるため、以降のサンプリングはこの規約で統一する。
    r.uv = reflection_rt_uv(uv_vp);
    return r;
}

// 反射ヒット点の直接光放射照度 E を返す（【影なし】）。
//   全ライトを走査し、各ライトの実効寄与（放射輝度 × N·L）を影なしで加算する。
//   距離減衰（point/spot）・スポット角の smoothstep・N·L は維持する（明るさ規約は本画面と同一）。
//   シャドウレイは 1 本も飛ばさない（ユーザー決定: 画面外ヒットは影の反射なしで OK）。
//   これにより反射レイあたりのシャドウレイが 0 本になり、旧実装（上位2灯シャドウレイ＋残り近似）
//   の分岐・挿入ソート・追加レイをすべて撤去できる（コスト削減）。
//   画面内で見えているヒットは fs_rt 側で scene_hdr（本画面の影込み結果）を採用するため、
//   影付きの反射はそちらが担う。本関数が担うのは「本画面で見えていない面」の下地色だけである。
fn rt_refl_direct_irradiance(hit_pos: vec3<f32>, n: vec3<f32>) -> vec3<f32> {
    var e = vec3<f32>(0.0, 0.0, 0.0);

    let count = min(rt_meta.count, arrayLength(&rt_lights));
    for (var i: u32 = 0u; i < count; i = i + 1u) {
        let light = rt_lights[i];
        var l = vec3<f32>(0.0, 0.0, 1.0);
        var radiance = light.color * light.intensity;
        if light.kind == LIGHT_KIND_DIRECTIONAL {
            l = normalize(-light.direction);
        } else {
            let to_light = light.position - hit_pos;
            let dist = length(to_light);
            l = to_light / max(dist, 1e-4);
            radiance = radiance * rt_refl_distance_atten(dist, light.range);
            if light.kind == LIGHT_KIND_SPOT {
                let cos_ang = dot(light.direction, -l);
                radiance = radiance * smoothstep(light.outer_cos, light.inner_cos, cos_ang);
            }
        }
        let ndl = max(dot(n, l), 0.0);
        if ndl <= 0.0 { continue; }
        // 影なしで加算（シャドウレイなし）。
        e = e + radiance * ndl;
    }
    return e;
}

// 反射ヒット点の間接光（アンビエントの床）放射照度を返す。本画面の evaluate_gi_ambient と同じ分岐:
//   GI 有効（rt_gi.enabled != 0）: DDGI プローブ照度を補間し、recursive_weight で反射内の再帰爆発を抑える。
//   GI 無効（0：RT 非対応・GI オフ・プレビューパス）: フラットアンビエント ambient_color × ambient_intensity。
// これがヒット照度の下限（床）になり、直接光が影で 0 になっても反射像が黒くつぶれない。
// ミス時のフォールバック rt_refl_fallback と同じ分岐方針（GI 有効ならプローブ、無効ならフラット）。
fn rt_refl_hit_indirect(hit_pos: vec3<f32>, n: vec3<f32>) -> vec3<f32> {
    if rt_gi.enabled != 0u {
        return ddgi_sample_irradiance(rt_gi, hit_pos, n, t_gi_irr, t_gi_vis, s_gi) * rt_gi.recursive_weight;
    }
    return rt_meta.ambient_color * rt_meta.ambient_intensity;
}

// レイが何にも当たらなかったとき（＝空へ抜けたとき）の色。優先順位には理由がある:
//   ① 天球テクスチャの直接サンプル … 反射方向の空が**画面外**でも本物の空の色が返る。
//      これが無いと、空を映すべき床・磨かれた面が GI プローブの平坦色になり、
//      同じシーンの水面反射（W5.2 は同じ共有関数を使う）と見た目が食い違う。
//      画面内に写っている空を拾う段は D6 には無い（reflection_common.wgsl の理由参照:
//      本パスの時点で scene_hdr にはまだ skybox が描かれていない）。
//   ② GI プローブ／フラットアンビエント … スカイボックスが無いシーンのフォールバック。
// なお `rt_refl_hit_indirect`（ヒット点の間接光の床）は空ではなく面の環境光なので変更しない。
fn rt_refl_fallback(world_pos: vec3<f32>, r_dir: vec3<f32>) -> vec3<f32> {
    let sky = reflection_sky_miss(r_dir);
    if sky.valid {
        return sky.color;
    }
    if rt_gi.enabled != 0u {
        return ddgi_sample_irradiance(rt_gi, world_pos, r_dir, t_gi_irr, t_gi_vis, s_gi);
    }
    return rt_meta.ambient_color * rt_meta.ambient_intensity;
}

// 1 枚のガラス層を通る光の RGB 透過率 T を平均アルベド storage（rt_albedo, group3 binding3）から求める。
// rt_albedo は rt_shadow.rs が所有する共有バッファで、.rgb=生アルベド・.a=パック済み α/transmission。
// モデルは refract_rt.wgsl::refract_layer_tint / 色付き影 rt_shadow_tint_avg と同一:
//   T = (1-α) + α·transmission·albedo.rgb
//     ・α=1, tr=1 → T = albedo（色ガラスは baseColor で透過光を濾過）
//     ・α=1, tr=0 → T = 0     （不透明被覆＝反射に黒シルエット）
//     ・α=0        → T = 1     （素通り＝色を付けない）
// 範囲外インデックスは vec3(1)（色を付けない）。
fn rt_refl_layer_tint(ai: u32) -> vec3<f32> {
    if ai >= arrayLength(&rt_albedo) {
        return vec3<f32>(1.0, 1.0, 1.0);
    }
    let entry  = rt_albedo[ai];
    let packed = entry.a;
    let a_q    = floor(packed / RT_REFL_PACK_RADIX);
    let t_q    = packed - a_q * RT_REFL_PACK_RADIX;
    let alpha  = clamp(a_q / RT_REFL_PACK_QUANT, 0.0, 1.0);
    let tr     = clamp(t_q / RT_REFL_PACK_QUANT, 0.0, 1.0);
    return vec3<f32>(1.0 - alpha) + alpha * tr * entry.rgb;
}

/// 反射レイが貫くガラス層の累積透過色（tint）と表面ツヤ（sheen）。
struct RtReflGlass { tint: vec3<f32>, sheen: vec3<f32> }

// 反射レイ方向 dir に沿ってガラス（0x02）だけを最大 RT_REFL_MAX_GLASS_HITS 枚トレースし、
// 入射面（front_face）ごとに透過色 T を tint に乗算累積、遮蔽量に比例した中立ツヤを sheen に加算する。
// tmax は不透明ヒットまでの距離（不透明ミス時は大定数）＝不透明背景より手前にあるガラス層だけを数える。
//   ・入射面のみでフィルタ＝閉メッシュの表裏で色が二乗になる二重計上を防ぐ（rt_shadow_tint_avg と同一規約）。
//   ・inline ray query は最近ヒットしか返さないため、tmin をヒットの先へ進めながら重なったガラスを貫く。
fn rt_refl_glass(origin: vec3<f32>, dir: vec3<f32>, tmax: f32) -> RtReflGlass {
    var g: RtReflGlass;
    g.tint  = vec3<f32>(1.0, 1.0, 1.0);
    g.sheen = vec3<f32>(0.0, 0.0, 0.0);
    var tmin = RT_REFL_RAY_TMIN;

    for (var i: u32 = 0u; i < RT_REFL_MAX_GLASS_HITS; i = i + 1u) {
        var desc: RayDesc;
        desc.flags     = 0u;                        // 最近ヒットを取り、次反復で tmin を先へ進める（TERMINATE なし）。
        desc.cull_mask = RT_REFL_TRANSLUCENT_MASK;  // 半透明（ガラス）のみを対象。
        desc.tmin      = tmin;
        desc.tmax      = max(tmax, tmin);
        desc.origin    = origin;
        desc.dir       = dir;
        var grq: ray_query;
        rayQueryInitialize(&grq, rt_tlas, desc);
        loop {
            if !rayQueryProceed(&grq) { break; }
        }
        let hit = rayQueryGetCommittedIntersection(&grq);
        if hit.kind == RAY_QUERY_INTERSECTION_NONE {
            break; // これ以上のガラス層は無い＝現在の tint/sheen で確定。
        }
        // 入射面でだけ色フィルタ＋ツヤを乗せる。裏面（出射面）は tmin を進めるだけ（二重計上防止）。
        if hit.front_face {
            // 【レコード索引規約】custom_data は先頭レコード番号。実レコードは
            // `custom_data + geometry_index`（スキン統合 BLAS はプリミティブごとに別レコード）。
            let layer = rt_refl_layer_tint(hit.instance_custom_data + hit.geometry_index);
            g.tint = g.tint * layer;
            // 中立ツヤ: 層の遮蔽量（1 - 相対輝度(T)）に比例した白ツヤ。視点依存フレネルの代用（上の判断根拠参照）。
            let block = clamp(1.0 - dot(layer, vec3<f32>(0.2126, 0.7152, 0.0722)), 0.0, 1.0);
            g.sheen = g.sheen + vec3<f32>(RT_REFL_GLASS_SHEEN * block);
        }
        // 次のガラス層へ: tmin を直前ヒットの先へ進める（同一面の再ヒットを避ける）。
        tmin = hit.t + RT_REFL_GLASS_T_STEP;
        if tmin >= tmax { break; }
    }
    return g;
}

@fragment
fn fs_rt(@builtin(position) frag: vec4<f32>) -> @location(0) vec4<f32> {
    let pix = vec2<i32>(frag.xy);
    let depth = textureLoad(t_depth, pix, 0);
    if depth >= 1.0 {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    let uv        = frag.xy / vec2<f32>(textureDimensions(t_gbuffer0));
    let world_pos = reflection_world_pos(uv, depth);
    let N         = normalize(textureLoad(t_gbuffer1, pix, 0).xyz);
    let V         = normalize(u_camera.position - world_pos);
    let ndotv     = max(dot(N, V), 1e-4);
    let R         = normalize(reflect(-V, N));

    let g0        = textureLoad(t_gbuffer0, pix, 0);
    let g2        = textureLoad(t_gbuffer2, pix, 0);
    let albedo0   = g0.rgb;
    let metallic  = g2.r;
    let roughness = g2.g;

    let smooth_w  = reflection_smoothness_weight(roughness);
    if smooth_w <= 0.0 {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    let f0      = reflection_f0(albedo0, metallic);
    let fresnel = reflection_fresnel(f0, ndotv);

    var desc: RayDesc;
    desc.flags     = 0u;
    desc.cull_mask = RT_REFL_CULL_MASK;
    desc.tmin      = RT_REFL_RAY_TMIN;
    desc.tmax      = RT_REFL_RAY_TMAX;
    desc.origin    = world_pos + N * RT_REFL_ORIGIN_N;
    desc.dir       = R;
    var rq: ray_query;
    rayQueryInitialize(&rq, rt_tlas, desc);
    loop {
        if !rayQueryProceed(&rq) { break; }
    }
    let hit = rayQueryGetCommittedIntersection(&rq);

    var reflected: vec3<f32>;
    if hit.kind == RAY_QUERY_INTERSECTION_NONE {
        reflected = rt_refl_fallback(world_pos, R);
    } else {
        let hit_pos = desc.origin + R * hit.t;
        let n_hit   = -R;

        // ── ハイブリッド：画面内に見えているヒットは本画面のライティング結果を採用 ──
        // 反射レイのヒット点を screen へ射影し、その UV の G-Buffer 深度から復元したビュー深度と
        // ヒット点のビュー深度が相対一致（= その面が本画面で実際に見えており、手前で遮蔽されて
        // いない）なら、scene_hdr（本フレームの不透明ライティング済みコピー。SSR と同じ入力を
        // group2 で共有）をその UV でサンプルして反射色に採用する。これで本画面と同一のソフト影・
        // SSGI/DDGI・AO・疑似バウンスがそのまま反射に乗る＝ユーザー要望「実際の影の濃さで反射」。
        var used_screen      = false;
        var reflected_screen = vec3<f32>(0.0, 0.0, 0.0);
        let proj = rt_refl_project(hit_pos);
        if proj.valid {
            let spix   = vec2<i32>(proj.uv * vec2<f32>(textureDimensions(t_gbuffer0)));
            let sdepth = textureLoad(t_depth, spix, 0);
            // 背景（深度 1.0）は面が無い＝一致対象外。手前に別の面があるケースも下の相対差で弾く。
            if sdepth < 1.0 {
                let scene_world = reflection_world_pos(proj.uv, sdepth);
                let scene_vz    = rt_refl_view_z(scene_world);
                let hit_vz      = rt_refl_view_z(hit_pos);
                // 相対深度一致（HIT_DEPTH_TOLERANCE, 相対 5%）。一致＝その UV の可視面がヒット面本人。
                if abs(scene_vz - hit_vz) <= HIT_DEPTH_TOLERANCE * max(abs(hit_vz), 1e-4) {
                    reflected_screen = textureSampleLevel(t_scene_hdr, s_scene, proj.uv, 0.0).rgb;
                    used_screen      = true;
                }
            }
        }

        if used_screen {
            // 画面内ヒット：本画面の最終ライティング色をそのまま反射色に。影の濃さが本画面と一致する。
            reflected = reflected_screen;
        } else {
            // ── 画面外／深度不一致（本画面で遮蔽され見えていない面）は従来の解析近似へ ──
            // ヒット先のベースカラー アルベド。実体は連結される reflection_rt_hit_{on,off}.wgsl が供給する:
            //   on（バインドレス対応）: instance_custom_data → instance_table → UV 補間 → テクスチャサンプル
            //   off（従来）          : instance_custom_data で平均アルベド storage を引く（ベタ塗り）
            // ミップは 0 固定（レイ微分は将来課題）。フォールバック（flags 対象外・tex 0）は on 側で平均色へ。
            // 第 1 引数は **レコード番号** = custom_data + geometry_index（レコード索引規約）。
            let albedo  = rt_hit_base_color(hit.instance_custom_data + hit.geometry_index, hit.primitive_index, hit.barycentrics);
            let direct  = rt_refl_direct_irradiance(hit_pos, n_hit);
            // 間接光の床（GI 有効なら DDGI プローブ、無効ならフラットアンビエント）。
            // 従来は enabled に関わらず DDGI を無条件サンプルしていたが、GI 無効時にフラットアンビエントへ
            // フォールバックする分岐へ変更（本画面 evaluate_gi_ambient・ミス時 rt_refl_fallback と整合）。
            let indirect = rt_refl_hit_indirect(hit_pos, n_hit);
            // ── 明るさ規約を本画面 evaluate_lighting へ揃える（画面内⇔画面外の境界の飛びを抑える）──
            //   直接光: 拡散 Lambert = albedo/π × E（shade_light の kD*albedo/PI 相当）。BRDF どおり /π を残す。
            //   間接光: albedo × E（/π なし）。本画面アンビエント（ambient_color*intensity*albedo・
            //           evaluate_gi_ambient）は /π を掛けないため、従来の /π は本画面より約 1/π 暗かった。
            //           ここで間接光の /π を外して本画面と同一規約に補正する。
            // 【限界（完全一致は不可能）】画面内 scene_hdr サンプルとの境界には明るさ差が残る:
            //   - specular の視点依存（scene_hdr は本画面視点、反射は反射視点）
            //   - AO/occlusion の有無（解析側はヒット点の occlusion を持たない）
            //   - 影の有無（解析側は【影なし】＝ユーザー決定、本画面はソフト影/デノイズ）
            //   境界のシームを完全には消せないが、/π 補正で明るさの段差を最小化する。
            reflected = albedo * (direct / RT_REFL_PI + indirect);
        }
    }

    // ── ガラス対応（実装 A）: 反射レイ方向のガラス層を色フィルタ＋ツヤとして反射に乗せる ──
    // 上の不透明トレース（0x01）はガラスを素通りする＝反射面にガラスが映らない。ここで反射レイと
    // 同じ原点・方向で 0x02（ガラス）だけを別途トレースし、不透明ヒットまで（ミス時は大定数まで）に
    // ある各ガラス層の透過色 T の積を不透明反射色へ乗算、ガラス表面のツヤ（中立色）を加算する。
    //   ・reflected は「画面内=scene_hdr（不透明のみ）／画面外=解析近似」いずれも不透明由来のため、
    //     ガラス色は含まない＝ここで乗算しても二重計上にならない。
    //   ・tmax は不透明ヒット距離（不透明ミス時は大定数）＝不透明背景より手前のガラスだけを数える。
    // これで反射面（床・磨かれた駒）にガラスの色付きシルエット＋ツヤが映る。
    let glass_tmax = select(RT_REFL_RAY_TMAX, hit.t, hit.kind != RAY_QUERY_INTERSECTION_NONE);
    let glass = rt_refl_glass(desc.origin, R, glass_tmax);
    reflected = reflected * glass.tint + glass.sheen;

    let color = reflected * fresnel * smooth_w * u_reflection.intensity;
    return vec4<f32>(color, 1.0);
}
