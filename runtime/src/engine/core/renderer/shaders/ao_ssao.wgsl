// ============================================================
// ao_ssao.wgsl — スクリーンスペース アンビエントオクルージョン（fragment fs_ssao）
//
// G-Buffer の深度＋ワールド法線から半球サンプルカーネルを張り、各サンプルをワールドで
// 配置 → view_proj でスクリーン投影 → その位置の実ジオメトリ深度と比較して遮蔽率を求める。
// 結果 AO（1=遮蔽なし / 0=完全遮蔽）を半解像度 ao_raw（.r）へ出力する（RAY_QUERY 不要）。
//
// 連結順: [ao_common, ao_ssao]
//
// 【カーネルの張り方（名前付き定数のみ・マジックナンバー禁止）】
// フィボナッチ半球分布を SSAO_KERNEL_SIZE 本、AO_GOLDEN_ANGLE で方位を回して張る。
// 各サンプルは接空間（法線=+z）で生成し、TBN でワールドへ移す。ピクセルごとに IGN 由来の
// 回転 rot を方位へ加えてバンディングを崩す。サンプル長は近距離重視で二乗スケールする。
//
// 【遮蔽判定（カメラ距離ベース。ビュー空間 z の符号依存を避ける）】
// サンプル点までの「期待カメラ距離」と、投影先スクリーンの実ジオメトリのカメラ距離を比べ、
// 実ジオメトリの方が手前（近い）なら遮蔽とみなす（バイアス SSAO_BIAS）。
// 距離差が大きすぎる（遠景の別物）ときは range check で遮蔽から外し、偽の暗さ（ハロー）を防ぐ。
// ============================================================

/// 半球サンプル本数（遮蔽率の分母）。多いほど滑らかだがコスト線形増。
const SSAO_KERNEL_SIZE: i32 = 16;
/// サンプル長の最小スケール（原点付近を重視するための下限。0..1）。
const SSAO_MIN_SCALE:   f32 = 0.1;
/// 自己遮蔽を無視するためのカメラ距離バイアス（世界単位）。面の微小凹凸で自身を暗くしない。
const SSAO_BIAS:        f32 = 0.025;
/// AO の効き（遮蔽率にかける係数）。intensity ノブ（u_ao.intensity）と併せて最終強度を決める。
const SSAO_STRENGTH:    f32 = 1.5;

@fragment
fn fs_ssao(in: AoVsOut) -> @location(0) vec4<f32> {
    let uv  = in.uv;
    let pix = ao_full_pix(uv);

    // ── 1) 深度読み出し。背景（depth>=1）は AO=1（遮蔽なし）で早期 return ─────
    let depth = textureLoad(t_depth, pix, 0);
    if depth >= AO_BACKGROUND_DEPTH {
        return vec4<f32>(1.0, 0.0, 0.0, 1.0);
    }

    // ── 2) ワールド位置＋法線＋接空間基底（TBN）─────────────────────────
    let world_pos = ao_world_pos(uv, depth);
    let n         = normalize(textureLoad(t_gbuffer1, pix, 0).xyz);
    let tangent   = ao_perp(n);
    let bitangent = cross(n, tangent);
    // ピクセルごとの回転（IGN。時間項なし＝静止画で安定）。
    let rot       = ao_ign(in.pos.xy) * 2.0 * AO_PI;

    let cam       = u_camera.position;

    // ── 3) 半球サンプルを張り、遮蔽率を平均する ─────────────────────────
    var occlusion = 0.0;
    for (var i: i32 = 0; i < SSAO_KERNEL_SIZE; i = i + 1) {
        // フィボナッチ半球（接空間, +z 上向き）。
        let fi  = f32(i) + 0.5;
        let z   = fi / f32(SSAO_KERNEL_SIZE);            // 0..1（半球の上向き成分）
        let rr  = sqrt(max(1.0 - z * z, 0.0));
        let phi = f32(i) * AO_GOLDEN_ANGLE + rot;
        // 近距離重視: 長さを二乗で 0..1 スケール（原点付近のサンプルを密に）。
        let t     = fi / f32(SSAO_KERNEL_SIZE);
        let scale = mix(SSAO_MIN_SCALE, 1.0, t * t);
        let k     = vec3<f32>(rr * cos(phi), rr * sin(phi), z) * scale;

        // 接空間 → ワールド方向。半径（世界単位）は u_ao.radius。
        let sample_dir = tangent * k.x + bitangent * k.y + n * k.z;
        let sample_pos = world_pos + sample_dir * u_ao.radius;

        // view_proj でスクリーン投影。
        let clip = u_camera.view_proj * vec4<f32>(sample_pos, 1.0);
        if clip.w <= 0.0 { continue; } // カメラ背後は寄与なし
        let ndc      = clip.xyz / clip.w;
        let sample_uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
        // 画面外は寄与なし（誤遮蔽を避ける）。
        if any(sample_uv < vec2<f32>(0.0, 0.0)) || any(sample_uv > vec2<f32>(1.0, 1.0)) {
            continue;
        }

        // 投影先の実ジオメトリ深度→ワールド→カメラ距離。
        let spix        = ao_full_pix(sample_uv);
        let scene_depth = textureLoad(t_depth, spix, 0);
        if scene_depth >= AO_BACKGROUND_DEPTH { continue; } // 背景は遮蔽しない
        let scene_world = ao_world_pos(sample_uv, scene_depth);

        let sample_cam_dist = distance(cam, sample_pos);
        let scene_cam_dist  = distance(cam, scene_world);

        // 実ジオメトリがサンプルより手前（カメラに近い）＝サンプルは遮蔽されている。
        let occluded = select(0.0, 1.0, scene_cam_dist < sample_cam_dist - SSAO_BIAS);
        // 距離差が半径を超える遠景は遮蔽から除外（ハロー防止）。
        let range_check = smoothstep(0.0, 1.0,
            u_ao.radius / max(abs(sample_cam_dist - scene_cam_dist), 1e-4));
        occlusion = occlusion + occluded * range_check;
    }

    // 平均遮蔽率 → AO（1=遮蔽なし）。強度は定数 × intensity ノブ。
    let occ = (occlusion / f32(SSAO_KERNEL_SIZE)) * SSAO_STRENGTH * u_ao.intensity;
    let ao  = clamp(1.0 - occ, 0.0, 1.0);
    return vec4<f32>(ao, 0.0, 0.0, 1.0);
}
