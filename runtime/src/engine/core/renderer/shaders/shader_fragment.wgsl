// ============================================================
// shader_fragment.wgsl  —  PBR フラグメントシェーダ
//
// Cook-Torrance BRDF (GGX + Smith + Schlick) を使用した
// metallic-roughness ワークフロー PBR 実装。
// 現在は 1 方向光 + 環境光（定数）で計算する。
// ============================================================

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
        let tn  = textureSample(t_normal, s_normal, in.uv0).rgb * 2.0 - 1.0;
        let T   = normalize(in.world_tan);
        let B   = normalize(in.world_bitan);
        let tbn = mat3x3<f32>(T, B, N);
        N = normalize(tbn * tn);
    }

    let V = normalize(u_camera.position - in.world_pos);

    // ── Cook-Torrance PBR ────────────────────────────────────
    let albedo = base_color.rgb;
    // 誘電体は F0 = 0.04、金属は albedo を F0 として使用
    let F0 = mix(vec3<f32>(0.04), albedo, metallic);

    // 方向光（将来的には uniform バッファに移行）
    // LH 座標系: カメラは -Z 側にいて +Z を向く。カメラ向きの面の法線は -Z 方向なので
    // ライトの Z は負（カメラ側から照らす）にしないと前面が ndl=0 になり真っ暗になる。
    let light_dir   = normalize(vec3<f32>(0.5, 1.0, -0.3));
    let light_color = vec3<f32>(3.0);

    let L   = light_dir;
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
    let Lo = (kD * albedo / PI + specular) * light_color * ndl;

    // 環境光: 視認性を確保するため影部分を適度に明るくする
    let ambient = vec3<f32>(0.05) * albedo * ao;

    let hdr_color = ambient + Lo + emissive;

    // 輝度ベース Reinhard トーンマッピング（HDR → [0, 1] リニア空間）
    // チャンネル毎 Reinhard では高輝度時に彩度が失われるため、
    // 輝度で Reinhard した後スケールを乗算して色相を保持する。
    let luma = dot(hdr_color, vec3<f32>(0.2126, 0.7152, 0.0722));
    let mapped = hdr_color * (1.0 / (luma + 1.0));

    // ガンマ補正は sRGB サーフェス（Bgra8UnormSrgb）に委ねる。
    // GPU がレンダーターゲット書き込み時に linear → sRGB エンコードを自動適用する。
    return vec4<f32>(mapped, base_color.a);
}
