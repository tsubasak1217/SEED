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

/// 幾何法線を画面微分から復元する際の、外積の長さの下限。
/// 三角形が画面上でほぼ縮退（面積ゼロ）していると cross(dpdx, dpdy) が 0 ベクトルになり
/// normalize が NaN になるため、この閾値未満なら補間法線へフォールバックする。
const GEOM_NORMAL_MIN_LEN: f32 = 1e-8;

// ── 幾何法線（RT 影のレイ原点バイアス専用）──────────────────
//
/// フラグメントの**幾何法線**（＝三角形の面法線）をワールド位置の画面微分から復元する。
///
/// なぜ必要か: RT 影のレイ原点は「面から少し浮かせる」必要があるが、押し出し方向に
/// 法線マップ適用後のシェーディング法線を使うと、法線が面から傾いている分だけ実効的な
/// クリアランスが落ち、面が自分自身に遮蔽されて真っ黒になる（Sponza の壁・柱で発生）。
/// 押し出しは必ず面そのものの向き（幾何法線）で行う。
///
/// - `world_pos`   : 補間済みワールド座標（dpdx/dpdy を取る＝フラグメントステージ限定）。
/// - `world_normal`: 法線マップ適用**前**の補間法線。外積の向き（符号）合わせにのみ使う
///                   （glTF の巻き順・スケール反転で外積の向きが反転しうるため）。
///
/// 注意: dpdx/dpdy は一様制御フロー内で呼ぶ必要があるため、必ず関数の先頭側
///       （分岐・discard より前）で呼ぶこと。
fn geometric_normal(world_pos: vec3<f32>, world_normal: vec3<f32>) -> vec3<f32> {
    let n_interp = normalize(world_normal);
    let ng_raw   = cross(dpdx(world_pos), dpdy(world_pos));
    let ng_len   = length(ng_raw);
    // 縮退時は補間法線で代用（従来挙動と同等の押し出しになる）。
    if ng_len < GEOM_NORMAL_MIN_LEN {
        return n_interp;
    }
    let ng = ng_raw / ng_len;
    // 補間法線と同じ半球へ向ける（faceforward 相当）。
    return select(-ng, ng, dot(ng, n_interp) >= 0.0);
}

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

/// PBR ライティングを評価して HDR カラー（rgb）＋アルファ（a）を返す純関数。
///
/// fs_main（不透明・Mask パス）と shader_wboit.wgsl の fs_wboit（透明 WBOIT パス）が
/// 同一のライティングを共有するために本体をここへ切り出した（Phase R5）。
/// Mask モードのアルファテスト（discard）も本関数内で行うため、両パスで
/// カットオフ挙動が一致する。alpha_cutoff は非 Mask マテリアルでは 0.0 のため
/// Opaque/Blend は影響を受けない（GpuMaterial::upload 参照）。
///
/// `front_facing` は `@builtin(front_facing)`（各エントリポイントが受け取る）。
/// カリング面 None（両面描画）／Front のマテリアルでは裏面フラグメントが生成されるが、
/// 頂点法線は表面向きに定義されているため、そのままだと N・V が逆半球になり
/// ndl / ndv がほぼ 0 に潰れて面が真っ黒になる。裏面ではシェーディング法線 N と
/// 幾何法線 Ng の両方を反転して「見えている側の法線」に揃える。
fn shade_pbr(in: VertexOutput, front_facing: bool) -> vec4<f32> {

    // 裏面のときだけ法線を反転させる符号（表面 = +1 / 裏面 = -1）。
    // 背面カリング（CullFace::Back）のマテリアルでは裏面が生成されないため常に +1 となり、
    // 従来と完全に同一の結果になる。
    let facing_sign = select(-1.0, 1.0, front_facing);

    // ── 幾何法線（RT 影のレイ原点バイアス専用）────────────────
    // 画面微分（dpdx/dpdy）を使うため、discard やライトループなどの分岐に入る前
    // （＝一様制御フロー）でここ一度だけ求める。シェーディングには使わない。
    // Ng も裏面では反転する。反転しないと裏面でレイ原点のバイアス押し出しが面の内側
    // （＝可視側と反対）へ向き、自分自身に遮蔽されて影が真っ黒になる。
    let Ng = geometric_normal(in.world_pos, in.world_normal) * facing_sign;

    // ── ベースカラー ──────────────────────────────────────────
    var base_color = u_material.base_color_factor;
    if u_material.has_base_color_tex != 0u {
        base_color *= textureSample(t_base_color, s_base_color, in.uv0);
    }
    base_color *= in.color;

    // アルファテスト（Mask モード: alpha_cutoff > 0）。
    // GpuMaterial::upload により alpha_cutoff は Mask のときのみ正値、
    // Opaque/Blend では 0.0 になるため、この分岐は Mask のみで発火する。
    if u_material.alpha_cutoff > 0.0 && base_color.a < u_material.alpha_cutoff {
        discard;
    }

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
    // 裏面では補間法線を反転してから TBN を組む（接空間法線マップも反転後の N を基準に載る）。
    var N = normalize(in.world_normal) * facing_sign;
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
            // インラインレイトレ影（Phase R8）: 全ライト種で表面→ライト方向の遮蔽レイ。
            // shadow_index に依存せず、cast_shadows=true のキャスターは TLAS 側で登録済み。
            // directional は tmax=大定数、point/spot/rect は light_dist（ライトまでの距離）。
            //
            // ソフトシャドウ: light.soft_radius から「見込み半径（cone_radius）」を求める。
            //   directional : soft_radius は tan(角径) の無次元スロープ（距離非依存）。
            //   point/spot/rect: soft_radius はワールド半径なので radius/距離＝見込み角に換算する。
            // cone_radius=0 のとき rt_shadow_factor はハード 1 本へ分岐して高速を保つ。
            var cone_radius: f32 = 0.0;
            if light.soft_radius > 0.0 {
                if light.kind == LIGHT_KIND_DIRECTIONAL {
                    cone_radius = light.soft_radius;
                } else {
                    cone_radius = light.soft_radius / max(light_dist, 1e-4);
                }
            }
            // 第 2 引数はシェーディング法線 N ではなく**幾何法線 Ng**を渡す。
            // レイ原点の押し出しと「幾何的な裏面＝即遮蔽」判定は面そのものの向きで行う必要がある
            // （N は法線マップで傾いており、押し出しに使うと自己交差して真っ黒になる）。
            radiance = radiance * rt_shadow_factor(in.world_pos, Ng, L, light_dist, cone_radius, in.clip_pos.xy);
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

    // ── アンビエント（環境光）──────────────────────────────────
    // 制御可能な環境光（Phase R1.5）。色・強度は LightMeta（group 4 binding 1）から供給し、
    // エディタのビューポート設定／project_settings.json（SET_AMBIENT）で変更できる。
    // ambient_intensity=0 で完全な暗闇になる（全ライト強度 0 と合わせて真っ暗）。
    // 既定は色白×強度 0.05（従来のハードコード値と同一の見た目）。
    // TODO(IBL): 将来は一定値から画像ベースライティング（環境マップ irradiance）へ。
    let ambient = u_light_meta.ambient_color * u_light_meta.ambient_intensity * albedo * ao;

    let hdr_color = ambient + Lo + emissive;

    // トーンマッピングは撤去し、リニア HDR 色をそのまま HDR オフスクリーン
    // （Rgba16Float, renderer::HDR_FORMAT）へ出力する（Phase R3）。
    // 輝度ベース Reinhard は全メッシュシェーダから撤去され、フルスクリーンの
    // トーンマップパス（post_tonemap.wgsl）へ一元化された。ガンマ補正（sRGB
    // エンコード）はトーンマップ出力先のスワップチェーンが担う。
    return vec4<f32>(hdr_color, base_color.a);
}

// ── 不透明／Mask パスのフラグメントエントリ ──────────────────
// ライティング本体は shade_pbr に集約済み。単一カラーターゲット
// （@location(0)）へ HDR 色＋アルファを出力する。
//
// `@builtin(front_facing)` は「このフラグメントが三角形の表面（CCW 面）由来か」を示す。
// 両面描画（cull_mode = None）・前面カリング（Front）のパイプラインでのみ false が来る。
// 頂点シェーダ出力（VertexOutput）の変更は不要（builtin はフラグメント入力に直接足せる）。
@fragment
fn fs_main(in: VertexOutput, @builtin(front_facing) front_facing: bool) -> @location(0) vec4<f32> {
    return shade_pbr(in, front_facing);
}
