// ============================================================
// lighting_eval.wgsl  —  ライト評価（Surface → HDR 放射輝度）
//
// ## 役割（単一責任）
// アンビエント ＋ ライトループ（group 4: u_lights / u_light_meta）＋ 影
// （シャドウマップ or インラインレイトレ）＋ Cook-Torrance PBR を評価する。
// マテリアルのテクスチャ採取は行わない（それは surface_gather.wgsl の責務）。
//
// ## 最重要の設計制約
// evaluate_lighting は **VertexOutput（補間頂点属性）に依存してはならない**。
// 入力は Surface（surface.wgsl）だけである。これにより:
//   - 現在  : フォワード不透明（fs_main）／WBOIT 半透明（fs_wboit）が同一実装を共有
//   - 将来  : Deferred の G-Buffer ライティングパス（フルスクリーン三角形。補間属性を
//             持たず、G-Buffer から Surface を復元する）が**そのまま**本関数を呼べる
// ライト種別・BRDF・影方式を変えるとき、直す箇所が常に 1 つで済む状態を保つこと。
//
// ## 依存
//   - shader_common.wgsl        : u_camera / u_lights / u_light_meta / PBR ヘルパー
//   - shadow.wgsl               : sample_shadow_dir / sample_shadow_spot
//   - rt_shadow_{on,off}.wgsl   : rt_shadow_enabled / rt_shadow_factor（連結で切替）
//   - surface.wgsl              : Surface
//
// ## ステージ制約
// 本ファイルは dpdx/dpdy を一切使わない（幾何法線は Surface に採取済みの値を使う）。
// そのためフラグメント以外のステージ（将来の compute ベースのライティング等）からも
// 呼べる。画面微分に依存するのは surface_gather.wgsl 側だけである。
//
// ## Clustered Lighting（Phase C1・実装済み）
// ライトの走査対象は「全ライトの線形走査」から「そのフラグメントが属するクラスタの
// ライトリスト ＋ 全平行光」へ置き換わった。変更はライトループ冒頭に閉じ込めてある
// （gather_surface / shade_pbr のシグネチャは不変）。
//   - 依存: cluster_common.wgsl（定数・構造体・索引）／shader_common.wgsl（group4 binding 7〜9）
//   - 構築: cluster_build.wgsl（compute。毎フレーム メインカメラぶんだけ構築）
//   - 無効化: u_cluster_params.enabled == 0 のとき従来の線形走査へフォールバックする
//             （カメラプレビューのパス・透視でないカメラ）。
// ============================================================

/// 平行光の RT 影レイの最大距離（実質無限。ライトまでの距離が定義できないため大定数）。
const RT_DIR_TMAX: f32 = 10000.0;

/// RT ソフト影の「見込み半径（cone_radius）」の上限。
///
/// cone_radius は「ライト方向 l を軸とする円錐の、l の単位長あたりの横方向の広がり」
/// ＝ tan(見込み半角) である。局所ライトでは cone_radius = soft_radius / light_dist と
/// 距離に反比例するため、フラグメントがライトに近づくほど**無限に発散する**。
/// 上限を設けないと:
///   - 円錐サンプルが半球全体に広がり、面の幾何的地平線より下を向くレイが大量に出る。
///   - ペナンブラ（半影）の幅がピクセル間で暴れ、少ないサンプル数では量子化ノイズ
///     （ディザ状のまだら）として可視化される。
/// そもそも light_dist <= soft_radius（＝ライトの発光球の内部に入り込んだ状態）では
/// 「点から見た光源の見込み角」という近似そのものが破綻しており、これ以上広げても
/// 物理的な意味はない。
///
/// 値 0.5 の根拠: tan(半角) = 0.5 → 見込み半角 ≈ 26.6°（直径 53°）。太陽（0.5°）や
/// 一般的な室内光源の見込み角を大きく上回り、実用上のペナンブラ表現には十分広い。
/// これを超える広がりは「面光源に埋まっている」領域であり、サンプル数を増やしても
/// ノイズが残るだけで見た目の利得がない。この値は rt_shadow_on.wgsl の適応サンプル数
/// （RT_SHADOW_SAMPLES_MAX に到達する cone_radius）とも対応させている。
///
/// ## 「影が硬くなった」原因ではない（再検討の記録）
/// このクランプ導入と同時に影が硬く・黒くなったが、原因はクランプではなく
/// rt_shadow_on.wgsl 側で**フラットな幾何法線 Ng を影の減衰判定に使っていたこと**だった
/// （面境界で判定が 0/1 に飛び、ファセット状の黒帯になる）。判定を補間法線 Nv に移して解決済み。
/// クランプを緩める（例 1.0）と:
///   - 見た目のボケ幅は増えるが、16 本のサンプル予算では 1 本あたりの担当立体角が広がり、
///     遮蔽率の量子化がそのままディザノイズとして再発する（クランプ導入前の症状）。
///   - 平行光（太陽・見込み角 0.5° ⇒ cone_radius ≈ 0.009）には一切影響しない。
///     すなわち主光源の影のボケ幅はこのクランプでは決まらない（soft_radius が決める）。
/// ゆえに 0.5 のまま据え置く。ボケが足りない場合はライトの soft_radius を上げること
/// （こちらは cone_radius がクランプに達しない範囲で自由に効き、サンプル数も適応で増える）。
const RT_SHADOW_MAX_CONE_RADIUS: f32 = 0.5;

/// 幾何法線による直接光の自己シャドウ・ゲートのしきい値 cos（Ng・L の下限）。
///
/// ## 何を保証するか（光漏れの根絶）
/// 各ライトの直接光の寄与を、**フラットな幾何法線 Ng（三角形の面法線）** で無条件に
/// ゲートする。`dot(Ng, L) <= 0`（面が幾何的に光へ背を向けている）なら、そのライトの
/// 直接光を **完全に 0** にする。これは「面が光に背を向けていれば直接光は当たらない」
/// という物理的に正しい自己シャドウそのものであり、
///   - 法線マップ後のシェーディング法線 N（BRDF の ndl の元）
///   - 補間頂点法線 Nv（RT 影のターミネータ判定の元）
///   - RT レイの結果（自己交差精度・折り目の隙間）
/// の **いずれにも依存しない**。ゆえにレイ精度や折り目に関係なく、幾何的に裏向きの面が
/// 直接光で光ることが原理的に起きえない。
///
/// ## しきい値を 0.0 にする根拠
/// 値は「幾何地平線そのもの」＝ 0.0。ゲートは `dot(Ng, L) > 0.0` のときだけ通す
/// （下の select による厳密判定）ので、`dot(Ng, L) <= 0` の面は必ず直接光 0 になる。
/// - 負のマージン（甘くする側）は光漏れを許すため採らない。
/// - 正のマージン（例 0.01）は面すれすれの光で正当に照らされる領域まで削るが、
///   漏れは 0.0 の時点で既に幾何的に閉じているため、これ以上締める必要はない。
///   dot(Ng, L) == 0 ちょうどは面と光が平行＝幾何的な放射照度が 0 の測度ゼロの縁で、
///   ここを 0 にしても見た目の損失はない（`>` により 0 側に含める）。
///
/// ## 副作用（許容）
/// ゲートは三角形ごとに一定の Ng で 0/1 に切り替わるため、シャドウターミネータが
/// 面境界でハード（ファセット状）になる。「見た目が崩れてもよいので絶対漏らさない」
/// という今回の要件どおりであり、意図的に許容する。
const RT_GEO_SHADOW_MIN_COS: f32 = 0.0;

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

// ── ライト評価本体 ────────────────────────────────────────────

/// Surface に対するライティングを評価し、リニア HDR 放射輝度（rgb）を返す。
///
/// 内訳: アンビエント（環境光×AO）＋ 全ライトの Cook-Torrance 寄与（影減衰込み）
///       ＋ エミッシブ。アルファは扱わない（呼び出し側が Surface.alpha を使う）。
///
/// ライトは group 4 の storage buffer（array<GpuLight>）＋ライト数 uniform
/// （u_light_meta.count）から供給される（shader_common.wgsl 参照）。
/// directional / point / spot / rect の 4 種を per-fragment ループで加算する。
/// ライト 0 灯でもアンビエントのみで破綻しない。
/// 走査対象はクラスタ（3D フロクセル）でカリング済み（Phase C1。下記ライトループ参照）。
///
/// 影は 2 経路を実行時分岐する（Phase R2/R8）:
///   - rt_shadow_enabled()==false: シャドウマップ（shadow.wgsl, group4 binding2〜5）。
///   - rt_shadow_enabled()==true : インラインレイトレ（rt_shadow_on.wgsl, group4 binding6）。
/// rt_shadow_enabled()/rt_shadow_factor() の実体は連結される rt_shadow_{on,off}.wgsl が供給する。
// --- DDGI ambient (Phase RT-GI) ---
// Replaces the flat ambient term. When GI is disabled (u_gi_params.enabled==0, i.e.
// RT-unsupported GPU, GI off, or the camera-preview pass which binds a disabled
// GiParams) it returns the previous flat ambient, keeping full backward compatibility.
// When enabled it interpolates the 8 surrounding probes (trilinear + Chebyshev
// visibility + cosine weight; see ddgi_common.wgsl) and scales by intensity.
fn evaluate_gi_ambient(world_pos: vec3<f32>, n: vec3<f32>, albedo: vec3<f32>, occlusion: f32) -> vec3<f32> {
    if u_gi_params.enabled == 0u {
        return u_light_meta.ambient_color * u_light_meta.ambient_intensity * albedo * occlusion;
    }
    let irr = ddgi_sample_irradiance(u_gi_params, world_pos, n, t_gi_irradiance, t_gi_visibility, s_gi);
    return irr * albedo * occlusion * u_gi_params.intensity;
}

fn evaluate_lighting(s: Surface) -> vec3<f32> {
    // ── ビューモード分岐（エディタのシーンビュー専用・アンリット／ワイヤ）──────
    // u_light_meta.view_mode が 0 以外（Unlit=1 / Wireframe=2）のときは、ライティング
    // 計算（アンビエント・ライトループ・影）を一切行わず、アルベド（頂点カラー・ベース
    // カラー畳み込み済み）＋エミッシブをそのまま返すフラット表示にする。
    // この分岐が効くのはメインカメラのパスだけ（プレビュー用 LightMeta は常に view_mode=0）。
    // Wireframe も色はアンリットで同一（線と塗りの違いはパイプラインの PolygonMode が担う）。
    if u_light_meta.view_mode != 0u {
        return s.albedo + s.emissive;
    }

    // 3 本の法線（いずれも採取段で裏面反転済み。surface.wgsl の解説を参照）。
    //   N  : 法線マップ後のシェーディング法線 → BRDF 専用
    //   Nv : 補間頂点法線（滑らか）           → RT 影の減衰判定（ターミネータ／バイアス）
    //   Ng : フラットな面法線                 → RT 影の自己交差回避専用
    let N  = s.normal;
    let Nv = s.vertex_normal;
    let Ng = s.geo_normal;

    let V = normalize(u_camera.position - s.world_pos);

    // ── Cook-Torrance PBR（マテリアル項）──────────────────────
    let albedo    = s.albedo;
    let metallic  = s.metallic;
    let roughness = s.roughness;
    // 誘電体は F0 = 0.04、金属は albedo を F0 として使用
    let F0 = mix(vec3<f32>(0.04), albedo, metallic);

    // ── ライトループ（Clustered Lighting, Phase C1）────────────
    //
    // 走査対象は 2 通り（u_cluster_params.enabled で切り替わる）:
    //
    //   enabled = 1（メインカメラのパス）:
    //     [0, local_light_offset) の**平行光**（視錐台全体に影響するためクラスタに入れない）
    //     ＋ このフラグメントが属するクラスタのライトリスト（局所ライトのみ）。
    //
    //   enabled = 0（カメラプレビューのパス／透視でないカメラ）:
    //     従来どおり [0, count) の**全ライト線形走査**。
    //     クラスタはカメラごとに固有（near/far/fov/ビューポート依存）なので、メインカメラ
    //     基準で構築したクラスタを別カメラのパスで使うとライティングが壊れる。プレビューは
    //     クラスタを一切参照しないこの経路へ落とす（バッファは共有しつつ params だけ差し替え）。
    //
    // クラスタの構築は cluster_build.wgsl（compute）が毎フレーム行う。
    // 索引・定数は cluster_common.wgsl（Rust: renderer/clustered.rs と定数一致をテストで担保）。
    var Lo = vec3<f32>(0.0);
    let light_count = min(u_light_meta.count, arrayLength(&u_lights));
    // シャドウ（group 4 binding 2〜5, shadow.wgsl）用: ビュー空間深度（正）をカスケード選択に使う。
    // クラスタの Z スライス選択にも同じ値を使う。
    // u_camera.view は列優先アップロード済みのため view*world で正しくビュー座標になる。
    let view_z = (u_camera.view * vec4<f32>(s.world_pos, 1.0)).z;

    // 走査範囲の決定。use_cluster=false のときは dir_end=light_count / list_count=0 となり、
    // ループは従来と完全に同一（[0, count) の線形走査）になる。
    let use_cluster = u_cluster_params.enabled != 0u;
    var dir_end:     u32 = light_count;   // 無条件に走査する範囲の終端（平行光）
    var list_offset: u32 = 0u;            // クラスタのライトリスト先頭
    var list_count:  u32 = 0u;            // クラスタのライト数
    // light_count == 0（ライト 0 灯）のときはクラスタも参照しない。
    // グリッドに前フレームの内容が残っていても走査しないため、下の light_count - 1u が
    // アンダーフローすることはない（ループにも入らない）。
    if use_cluster && light_count > 0u {
        dir_end = min(u_cluster_params.local_light_offset, light_count);
        let cell = u_cluster_grid[cluster_index_for_fragment(s.frag_coord, view_z, u_cluster_params)];
        list_offset = cell.offset;
        list_count  = min(cell.count, MAX_LIGHTS_PER_CLUSTER);
    }

    let iter_count = dir_end + list_count;
    for (var it: u32 = 0u; it < iter_count; it = it + 1u) {
        // 前半 = 平行光（または非クラスタ時の全ライト）、後半 = クラスタのライトリスト。
        var li: u32 = 0u;
        if it < dir_end {
            li = it;
        } else {
            li = u_cluster_lights[list_offset + (it - dir_end)];
        }
        // 破損したインデックス（ありえないが）で配列外を読まないための保険。
        // light_count == 0 ならこのループには入らないため減算は安全。
        let light = u_lights[min(li, light_count - 1u)];

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
            let to_light = light.position - s.world_pos;
            let dist     = length(to_light);
            L            = to_light / max(dist, 1e-4);
            light_dist   = dist;
            radiance     = base_col * distance_attenuation(dist, light.range);

        } else if light.kind == LIGHT_KIND_SPOT {
            // スポット光: point の減衰に加え、内外コーン角のスムーズ円錐減衰を掛ける。
            let to_light = light.position - s.world_pos;
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
            let d          = s.world_pos - light.position;
            let px         = clamp(dot(d, light.rect_right), -light.rect_half_width,  light.rect_half_width);
            let py         = clamp(dot(d, light.rect_up),    -light.rect_half_height, light.rect_half_height);
            let closest    = light.position + light.rect_right * px + light.rect_up * py;
            let to_light   = closest - s.world_pos;
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
            //
            // 局所ライトの radius/距離 は距離が縮むと発散するため、必ず
            // RT_SHADOW_MAX_CONE_RADIUS でクランプする（未クランプだとライト近傍の面が
            // 半球全域へレイを撒き、ディザ状のノイズと偽の自己遮蔽で真っ黒になる）。
            // directional 側も同じ上限を掛ける（インスペクタから非現実的な角径を入れられても
            // 同じ破綻を起こすため、経路によらず一箇所で頭を押さえる）。
            var cone_radius: f32 = 0.0;
            if light.soft_radius > 0.0 {
                if light.kind == LIGHT_KIND_DIRECTIONAL {
                    cone_radius = light.soft_radius;
                } else {
                    cone_radius = light.soft_radius / max(light_dist, 1e-4);
                }
                cone_radius = min(cone_radius, RT_SHADOW_MAX_CONE_RADIUS);
            }
            // 影には Ng（フラットな面法線）と Nv（滑らかな補間頂点法線）の**両方**を渡す。
            //   Ng → レイ原点の最小クリアランス／レイ方向の地平線リフト（自己交差回避のみ）
            //   Nv → ターミネータのランプ／スロープスケールバイアス（減衰カーブの判定）
            // Ng を減衰判定に使うと三角形の面境界で判定が飛び、ファセット状の硬い黒帯になる。
            // シェーディング法線 N（法線マップ後）は影へは渡さない（BRDF 専用）。
            radiance = radiance * rt_shadow_factor(
                s.world_pos, Ng, Nv, L, light_dist, cone_radius, s.frag_coord,
            );
        } else {
            // 従来のシャドウマップ経路（group 4 binding 2〜5, shadow.wgsl）。
            // shadow_index < 0 のライトは影計算をスキップ（cast_shadows=false 含む）。
            // 方向光は CSM、スポットは自身のマップを PCF 3x3 でサンプルして減衰する。
            // point/rect の影はシャドウマップ非対応（RT 影経路で対応）。
            let sidx = i32(light.shadow_index);
            if sidx >= 0 {
                if light.kind == LIGHT_KIND_DIRECTIONAL {
                    radiance = radiance * sample_shadow_dir(s.world_pos, view_z);
                } else if light.kind == LIGHT_KIND_SPOT {
                    radiance = radiance * sample_shadow_spot(s.world_pos, sidx);
                }
            }
        }

        // ── 幾何法線による直接光の自己シャドウ・ゲート（光漏れの根絶）──────
        // フラットな幾何法線 Ng（三角形の面法線）で、そのライトの直接光を無条件にゲートする。
        // dot(Ng, L) > 0（面が幾何的に光の側を向く）のときだけ寄与を通し、
        // dot(Ng, L) <= 0（幾何的に光へ背を向ける面）では geo_gate=0 で **完全に 0** にする。
        //
        // このゲートは影経路（RT / シャドウマップ）や法線マップ後の N・補間法線 Nv・RT レイ
        // 精度に **一切依存しない**。よって「薄い一枚布の裏にライトがあるのに、法線マップの N が
        // 光を拾い、折り目で Nv も光側を向くため影係数が 0 にならず点状に光る」という漏れが、
        // どの分岐（RT on/off・ライト種別）でも幾何的に閉じる。
        //
        // 判定は `>`（厳密）なので dot(Ng, L) == 0 ちょうども 0 側に含める（RT_GEO_SHADOW_MIN_COS
        // の解説参照）。step ではなく select を使うのは、この「<= 0 → 必ず 0」を境界の丸めなく
        // 保証するため。アンビエント（下の ambient）とエミッシブ（s.emissive）にはこのゲートを
        // 掛けない（方向性がなく漏れではないため）。
        let geo_gate = select(0.0, 1.0, dot(Ng, L) > RT_GEO_SHADOW_MIN_COS);
        Lo += geo_gate * shade_light(N, V, L, albedo, F0, metallic, roughness, radiance);

        // ── 疑似バウンス（間接光近似・Phase 間接光）──────────────
        // ライトに照らされた周囲の面からの 1 バウンスを「ライト位置からの無方向光」で
        // 近似する。レイトレ無しで「影の中にも光が回り込む」間接光風の見た目を出すのが目的。
        //   - 影係数（rt_shadow_factor / sample_shadow_*）は掛けない（影の中へ届くのが目的）。
        //   - geo_gate も掛けない（面がライトへ背を向けても回り込む）。
        //   - スポットの円錐減衰・rect の前面判定も掛けない（照射範囲の外へ回り込むのが本質）。
        //     radiance ではなく base_col（color*intensity）に距離減衰だけを掛ける。
        //   - AO（s.occlusion）は掛ける（隅は間接光も届きにくい）。
        //   - wrap = dot(N,L)*0.5+0.5（wrap lighting）。完全裏面でも 0.5 残り＝回り込み。
        // directional は Rust 側（light_ops）で bounce_intensity=0 に固定されるため寄与しない
        // （減衰の基準距離が無い。全体の底上げは既存のアンビエントで行う）。
        // クラスタ整合: 距離減衰（range ウィンドウ）で range を超えると 0 になる。バウンスは
        // ライトの影響半径 range 内に収まる＝このライトはこのフラグメントのクラスタに必ず
        // 入っているので、クラスタ走査のまま（追加のライトを引かず）正しく効く。
        if light.bounce_intensity > 0.0 {
            let wrap   = dot(N, L) * 0.5 + 0.5;
            let bounce = albedo / PI * base_col
                       * distance_attenuation(light_dist, light.range)
                       * light.bounce_intensity * wrap * s.occlusion;
            Lo += bounce;
        }
    }

    // ── アンビエント（環境光）──────────────────────────────────
    // 制御可能な環境光（Phase R1.5）。色・強度は LightMeta（group 4 binding 1）から供給し、
    // エディタのビューポート設定／project_settings.json（SET_AMBIENT）で変更できる。
    // ambient_intensity=0 で完全な暗闇になる（全ライト強度 0 と合わせて真っ暗）。
    // 既定は色白×強度 0.05（従来のハードコード値と同一の見た目）。
    // TODO(IBL): 将来は一定値から画像ベースライティング（環境マップ irradiance）へ。
    let ambient = evaluate_gi_ambient(s.world_pos, N, albedo, s.occlusion);

    // リニア HDR 色（トーンマップ前）。トーンマップは post_tonemap.wgsl が一元的に行う。
    return ambient + Lo + s.emissive;
}
