// ============================================================
// shader_fragment.wgsl  —  PBR フラグメントシェーダ
//
// Cook-Torrance BRDF (GGX + Smith + Schlick) を使用した
// metallic-roughness ワークフロー PBR 実装。
//
// ライトは group 4 の storage buffer（array<GpuLight>）＋ライト数
// uniform（u_light_meta.count）から供給される（shader_common.wgsl 参照）。
// directional / point / spot / rect の 4 種をフォワードの
// per-fragment ループで加算する。ライト 0 灯でもアンビエントのみで破綻しない。
//
// クラスタリング／タイル分割は将来課題（現状は全ライトを線形走査）。
//
// 影は 2 経路を実行時分岐する（Phase R2/R8）:
//   - rt_shadow_enabled()==false: シャドウマップ（shadow.wgsl, group4 binding2〜5）。
//   - rt_shadow_enabled()==true : インラインレイトレ（rt_shadow_on.wgsl, group4 binding6）。
// rt_shadow_enabled()/rt_shadow_factor() の実体は連結される rt_shadow_{on,off}.wgsl が供給する。
// ============================================================

/// 平行光の RT 影レイの最大距離（実質無限。ライトまでの距離が定義できないため大定数）。
const RT_DIR_TMAX: f32 = 10000.0;

// ── ライト減衰ヘルパー ────────────────────────────────────────

/// 距離減衰（inverse-square ＋ range ウィンドウ）。
///
/// 物理的な 1/d^2 に、range 付近でスムーズに 0 へ落とすウィンドウ関数
/// （Karis / UE4 方式）を掛ける。range を超えると寄与が 0 になる。
///   window = (1 - (d^2/range^2)^2)^2   （0..1 にクランプ）
fn distance_attenuation(dist: f32, range: f32) -> f32 {
    let d2      = dist * dist;
    let inv_sqr = 1.0 / max(d2, 1e-4);
    let factor  = d2 / max(range * range, 1e-4);
    let window  = clamp(1.0 - factor * factor, 0.0, 1.0);
    return inv_sqr * window * window;
}

/// 1 ライト分の Cook-Torrance BRDF を評価して放射輝度を返す。
///
/// - `L`        : 面から光源への方向（正規化済み）
/// - `radiance` : そのライトの実効放射輝度（color * intensity * 減衰）
fn shade_light(
    N: vec3<f32>, V: vec3<f32>, L: vec3<f32>,
    albedo: vec3<f32>, F0: vec3<f32>, metallic: f32, roughness: f32,
    radiance: vec3<f32>,
) -> vec3<f32> {
    let H   = normalize(V + L);
    let ndl = max(dot(N, L), 0.0);
    let ndv = max(dot(N, V), 0.0001);
    let hdv = max(dot(H, V), 0.0);

    let D = distribution_ggx(N, H, roughness);
    let G = geometry_smith(N, V, L, roughness);
    let F = fresnel_schlick(hdv, F0);

    let kS = F;
    let kD = (vec3<f32>(1.0) - kS) * (1.0 - metallic);

    // 分母にクランプして除算ゼロを防止
    let specular = D * G * F / max(4.0 * ndv * ndl, 0.001);
    return (kD * albedo / PI + specular) * radiance * ndl;
}

@fragment
fn fs_main(in: VertexOutput) -> @location(0) vec4<f32> {

    // ── ベースカラー ──────────────────────────────────────────
    var base_color = u_material.base_color_factor;
    if u_material.has_base_color_tex != 0u {
        base_color *= textureSample(t_base_color, s_base_color, in.uv0);
    }
    base_color *= in.color;

    // アルファテスト（Mask モード: alpha_cutoff > 0）
    //if u_material.alpha_cutoff > 0.0 && base_color.a < u_material.alpha_cutoff {
    //    discard;
    //}

    // ── メタリック・ラフネス ──────────────────────────────────
    var metallic  = u_material.metallic_factor;
    var roughness = u_material.roughness_factor;
    if u_material.has_mr_tex != 0u {
        let mr = textureSample(t_metallic_roughness, s_metallic_roughness, in.uv0);
        metallic  *= mr.b;   // glTF: B = metallic
        roughness *= mr.g;   // glTF: G = roughness
    }
    roughness = clamp(roughness, 0.04, 1.0);

    // ── エミッシブ ────────────────────────────────────────────
    var emissive = u_material.emissive_factor;
    if u_material.has_emissive_tex != 0u {
        emissive *= textureSample(t_emissive, s_emissive, in.uv0).rgb;
    }

    // ── アンビエントオクルージョン ────────────────────────────
    var ao = 1.0;
    if u_material.has_occlusion_tex != 0u {
        ao = textureSample(t_occlusion, s_occlusion, in.uv0).r;
    }

    // ── 法線（法線マップ対応）────────────────────────────────
    var N = normalize(in.world_normal);
    if u_material.has_normal_tex != 0u {
        // 法線マップの Z は RG から再構築する。
        // BC5 圧縮（RG 2ch）フォーマットは B チャンネルを持たないため直接読めない。
        // 接空間法線は単位ベクトルなので z = sqrt(1 - x^2 - y^2) で復元でき、
        // 非圧縮 RGB 法線マップでも同じ結果になる（後方互換）。
        let nxy = textureSample(t_normal, s_normal, in.uv0).rg * 2.0 - 1.0;
        let nz  = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
        let tn  = vec3<f32>(nxy, nz);
        let T   = normalize(in.world_tan);
        let B   = normalize(in.world_bitan);
        let tbn = mat3x3<f32>(T, B, N);
        N = normalize(tbn * tn);
    }

    let V = normalize(u_camera.position - in.world_pos);

    // ── Cook-Torrance PBR（マテリアル項）──────────────────────
    let albedo = base_color.rgb;
    // 誘電体は F0 = 0.04、金属は albedo を F0 として使用
    let F0 = mix(vec3<f32>(0.04), albedo, metallic);

    // ── ライトループ ──────────────────────────────────────────
    // group 4 の storage 配列を u_light_meta.count 件だけ走査して加算する。
    var Lo = vec3<f32>(0.0);
    let light_count = min(u_light_meta.count, arrayLength(&u_lights));
    // シャドウ（group 4 binding 2〜5, shadow.wgsl）用: ビュー空間深度（正）をカスケード選択に使う。
    // u_camera.view は列優先アップロード済みのため view*world で正しくビュー座標になる。
    let view_z = (u_camera.view * vec4<f32>(in.world_pos, 1.0)).z;
    for (var i: u32 = 0u; i < light_count; i = i + 1u) {
        let light = u_lights[i];

        var L        = vec3<f32>(0.0, 0.0, 1.0);
        var radiance = vec3<f32>(0.0);
        let base_col = light.color * light.intensity;
        // RT 影レイの最大距離。directional は大定数、局所ライトはライトまでの距離を代入する。
        var light_dist: f32 = RT_DIR_TMAX;

        if light.kind == LIGHT_KIND_DIRECTIONAL {
            // 平行光: L = -照射方向。減衰なし（従来の 1 方向光と同等）。
            L        = normalize(-light.direction);
            radiance = base_col;

        } else if light.kind == LIGHT_KIND_POINT {
            // 点光源: 位置から全方向。inverse-square ＋ range ウィンドウで減衰。
            let to_light = light.position - in.world_pos;
            let dist     = length(to_light);
            L            = to_light / max(dist, 1e-4);
            light_dist   = dist;
            radiance     = base_col * distance_attenuation(dist, light.range);

        } else if light.kind == LIGHT_KIND_SPOT {
            // スポット光: point の減衰に加え、内外コーン角のスムーズ円錐減衰を掛ける。
            let to_light = light.position - in.world_pos;
            let dist     = length(to_light);
            L            = to_light / max(dist, 1e-4);
            light_dist   = dist;
            // 照射軸（light.direction）と「光源→フラグメント」方向の角度で円錐判定。
            let cos_ang  = dot(light.direction, -L);
            // outer_cos → inner_cos で 0→1（inner 内側は全光量、outer 外側は 0）。
            let cone     = smoothstep(light.outer_cos, light.inner_cos, cos_ang);
            radiance     = base_col * distance_attenuation(dist, light.range) * cone;

        } else {
            // 矩形エリアライト（LIGHT_KIND_RECT）。
            // ── R1 簡易近似（最近接点近似）──
            //   矩形上でフラグメントに最も近い点を求め、その点からの点光源として扱う。
            //   さらに発光面の表側（direction を法線とする前面）に対してのみ寄与させる。
            //   物理的に正しい面積分（LTC: Linearly Transformed Cosines）は
            //   TODO(R1.5) として別途実装する。
            let d          = in.world_pos - light.position;
            let px         = clamp(dot(d, light.rect_right), -light.rect_half_width,  light.rect_half_width);
            let py         = clamp(dot(d, light.rect_up),    -light.rect_half_height, light.rect_half_height);
            let closest    = light.position + light.rect_right * px + light.rect_up * py;
            let to_light   = closest - in.world_pos;
            let dist       = length(to_light);
            L              = to_light / max(dist, 1e-4);
            light_dist     = dist;
            // 前面判定: フラグメントが発光面の表側（direction 側）にあるほど強い。
            let facing     = clamp(dot(light.direction, -L), 0.0, 1.0);
            radiance       = base_col * distance_attenuation(dist, light.range) * facing;
        }

        // ── シャドウ減衰（2 経路を実行時分岐）─────────────────
        if rt_shadow_enabled() {
            // インラインレイトレ影（Phase R8）: 全ライト種で表面→ライト方向の遮蔽レイ 1 本。
            // shadow_index に依存せず、cast_shadows=true のキャスターは TLAS 側で登録済み。
            // directional は tmax=大定数、point/spot/rect は light_dist（ライトまでの距離）。
            radiance = radiance * rt_shadow_factor(in.world_pos, N, L, light_dist);
        } else {
            // 従来のシャドウマップ経路（group 4 binding 2〜5, shadow.wgsl）。
            // shadow_index < 0 のライトは影計算をスキップ（cast_shadows=false 含む）。
            // 方向光は CSM、スポットは自身のマップを PCF 3x3 でサンプルして減衰する。
            // point/rect の影はシャドウマップ非対応（RT 影経路で対応）。
            let sidx = i32(light.shadow_index);
            if sidx >= 0 {
                if light.kind == LIGHT_KIND_DIRECTIONAL {
                    radiance = radiance * sample_shadow_dir(in.world_pos, view_z);
                } else if light.kind == LIGHT_KIND_SPOT {
                    radiance = radiance * sample_shadow_spot(in.world_pos, sidx);
                }
            }
        }

        Lo += shade_light(N, V, L, albedo, F0, metallic, roughness, radiance);
    }

    // ── アンビエント ──────────────────────────────────────────
    // 当面は定数。将来は IBL（環境マップの irradiance / prefiltered specular）へ置換する。
    // TODO(IBL): 環境光を一定値から画像ベースライティングへ。
    let ambient = vec3<f32>(0.05) * albedo * ao;

    let hdr_color = ambient + Lo + emissive;

    // トーンマッピングは撤去し、リニア HDR 色をそのまま HDR オフスクリーン
    // （Rgba16Float, renderer::HDR_FORMAT）へ出力する（Phase R3）。
    // 輝度ベース Reinhard は全メッシュシェーダから撤去され、フルスクリーンの
    // トーンマップパス（post_tonemap.wgsl）へ一元化された。ガンマ補正（sRGB
    // エンコード）はトーンマップ出力先のスワップチェーンが担う。
    return vec4<f32>(hdr_color, base_color.a);
}
