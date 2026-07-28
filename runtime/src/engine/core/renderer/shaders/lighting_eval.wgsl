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
//   - shading_contract.wgsl     : ShadingSurface / LightSample / shade_model_0（契約 v1）
//   - shading_dispatch.wgsl（またはアセット生成版） : shade_surface（モデル ID の振り分け）
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

// RT_DIR_TMAX（平行光レイの最大距離）と RT_SHADOW_MAX_CONE_RADIUS（見込み半径の上限）、
// および L・光源距離・cone_radius を求める light_shadow_geometry は light_common.wgsl へ移設した
// （Phase RT-Shadow-Denoise）。インライン影（本ファイル）と事前計算マスク（shadow_mask.wgsl）が
// 同一実装を共有し、両経路で影がズレないことを保証するため。詳細な根拠コメントは移設先を参照。

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

// 1 ライト分の Cook-Torrance BRDF（旧 shade_light）は shading_contract.wgsl へ移設した
// （L3-a 第 1 段）。バインディングを持たない純関数であり、シェーディング契約の「モデル 0」の
// 実体そのものだからである。本ファイルは契約関数 shade_surface 経由でそれを呼ぶ。

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
fn evaluate_gi_ambient(world_pos: vec3<f32>, n: vec3<f32>, albedo: vec3<f32>, occlusion: f32, screen_gi: vec4<f32>) -> vec3<f32> {
    // フラットアンビエント（従来値）。GI 無効時・SSGI フォワードフォールバック時の共通フォールバック。
    let flat_ambient = u_light_meta.ambient_color * u_light_meta.ambient_intensity * albedo * occlusion;

    // enabled==0（RT 非対応・GI オフ・プレビューパス）は方式に関わらずフラットへ。
    if u_gi_params.enabled == 0u {
        return flat_ambient;
    }

    // SSGI（スクリーンスペース GI, 1 フレーム遅延）。
    // deferred ライティングパスだけが screen_gi.a=1 で有効な間接光を供給する。
    // フォワードパス（screen_gi.a<=0）はスクリーン入力が不透明前提のためフラットへフォールバックする
    // （半透明フォワードに SSGI を効かせない設計。screen_gi はゼロ初期化＝.a=0 で自動的にこの枝へ）。
    if u_gi_params.gi_mode == GI_MODE_SSGI {
        if screen_gi.a > 0.5 {
            // screen_gi.rgb は「拾った間接放射照度（ミスはフラットアンビエント色で埋め済み）」。
            // × albedo × occlusion × intensity（gi_intensity ノブ。DDGI と共通）。
            return screen_gi.rgb * albedo * occlusion * u_gi_params.intensity;
        }
        return flat_ambient;
    }

    // DDGI（プローブ補間）。
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
    // F0（誘電体 0.04／金属 albedo）の算出は shading_contract.wgsl の shade_model_0 が
    // 同一式で行う（契約上、F0 はモデルごとの実装詳細であり契約フィールドではないため）。

    // ── シェーディング契約の面情報（ShadingSurface）を 1 回だけ組み立てる ──────
    // ライトに依存しない値しか含まないので、**ループの外**で 1 回作って使い回す
    // （ループ内で作り直すと N 灯ぶんの無駄なコピーが出る）。
    // 各フィールドは Surface からの素直な写しであり、値の加工は一切していない。
    var sf: ShadingSurface;
    sf.world_pos     = s.world_pos;
    sf.normal        = N;
    sf.geo_normal    = Ng;
    sf.vertex_normal = Nv;
    sf.view_dir      = V;
    sf.base_color    = albedo;
    sf.roughness     = roughness;
    sf.metallic      = metallic;
    sf.occlusion     = s.occlusion;
    sf.emissive      = s.emissive;
    sf.transmission  = s.diffuse_transmission;
    sf.user_data     = s.user_data;
    sf.render_tag    = s.render_tag;
    sf.shading_model = s.shading_model;
    sf.frag_coord    = s.frag_coord;
    // 唯一 Surface 由来でないフィールド。時間はピクセルごとの属性ではなく
    // フレーム全体で 1 つの値なので、G-Buffer を経由せずカメラ uniform から直接読む
    // （フォワードは shader_common.wgsl、デファードは deferred_lighting.wgsl が
    //   宣言する同一レイアウトの u_camera。どちらの経路でも同じ値になる）。
    sf.time          = u_camera.time;

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

        let base_col = light.color * light.intensity;
        // L・光源距離・見込み半径は共有関数（light_common.wgsl の light_shadow_geometry）で求める。
        // 事前計算マスク経路（shadow_mask.wgsl）と**同一実装**なので、インライン影とマスク影で
        // L/距離/cone_radius が食い違わない（両経路で影が一致する保証）。
        let geo        = light_shadow_geometry(light, s.world_pos);
        let L          = geo.L;
        let light_dist = geo.dist;
        var radiance   = vec3<f32>(0.0);

        if light.kind == LIGHT_KIND_DIRECTIONAL {
            // 平行光: 減衰なし（従来の 1 方向光と同等）。
            radiance = base_col;
        } else if light.kind == LIGHT_KIND_POINT {
            // 点光源: inverse-square ＋ range ウィンドウで減衰。
            radiance = base_col * distance_attenuation(light_dist, light.range);
        } else if light.kind == LIGHT_KIND_SPOT {
            // スポット光: point の減衰に加え、内外コーン角のスムーズ円錐減衰を掛ける。
            // 照射軸（light.direction）と「光源→フラグメント」方向の角度で円錐判定。
            let cos_ang = dot(light.direction, -L);
            // outer_cos → inner_cos で 0→1（inner 内側は全光量、outer 外側は 0）。
            let cone    = smoothstep(light.outer_cos, light.inner_cos, cos_ang);
            radiance    = base_col * distance_attenuation(light_dist, light.range) * cone;
        } else {
            // 矩形エリアライト（LIGHT_KIND_RECT）。R1 簡易近似（最近接点は light_shadow_geometry が算出）。
            //   発光面の表側（direction を法線とする前面）に対してのみ寄与させる。
            //   物理的に正しい面積分（LTC: Linearly Transformed Cosines）は TODO(R1.5)。
            let facing = clamp(dot(light.direction, -L), 0.0, 1.0);
            radiance   = base_col * distance_attenuation(light_dist, light.range) * facing;
        }

        // 拡散透過（逆光透け）用に、影を掛ける前の radiance を退避する。
        // この時点の radiance は「color×intensity × 距離減衰 × スポット円錐/rect 前面」を含み、
        // 影（この後の乗算）は含まない。逆光透け項は影を掛けない（後述の理由）ため、この値を使う。
        let radiance_direct = radiance;

        // ── シャドウ減衰（2 経路を実行時分岐）─────────────────
        if rt_shadow_enabled() {
            // インラインレイトレ影（Phase R8）: 全ライト種で表面→ライト方向の遮蔽レイ。
            // shadow_index に依存せず、cast_shadows=true のキャスターは TLAS 側で登録済み。
            // directional は tmax=大定数、point/spot/rect は light_dist（ライトまでの距離）。
            //
            // 見込み半径（cone_radius）は共有関数が算出済み（geo.cone_radius）。
            //   directional : soft_radius=tan(角径)（距離非依存）／局所: soft_radius/距離。
            //   RT_SHADOW_MAX_CONE_RADIUS で発散をクランプ済み。0 のときハード 1 本。
            let cone_radius = geo.cone_radius;

            // ── ソフト影マスク経路（Phase RT-Shadow-Denoise）───────────────
            // deferred ライティング（s.shadow_mask_valid>0）で、このライトがマスク対象
            // （shadow_mask_slot>=0）なら、レイを飛ばさず**事前計算したデノイズ済み半解像度マスク**を
            // サンプルして遮蔽率にする（ディザ状ガサガサの根治）。マスク対象外（5 灯目以降のソフト影・
            // ハード影）や forward パス（shadow_mask_valid=0＝ゼロ初期化）は従来どおりインラインで評価する。
            let slot = i32(light.shadow_mask_slot);
            if s.shadow_mask_valid > 0.5 && slot >= 0 {
                // マスクは色付き影込みの透過率（rgb）。レイヤ=スロット。上限はマスク配列サイズで縛る。
                radiance = radiance * s.shadow_mask[clamp(slot, 0, i32(SURFACE_SHADOW_MASK_SLOTS) - 1)].rgb;
            } else {
                // 影には Ng（フラットな面法線）と Nv（滑らかな補間頂点法線）の**両方**を渡す。
                //   Ng → レイ原点の最小クリアランス（自己交差回避のみ）
                //   Nv → ターミネータのランプ（減衰カーブの判定）
                // シェーディング法線 N（法線マップ後）は影へは渡さない（BRDF 専用）。
                // deterministic_tint=true: このインライン経路はバイラテラルで均されず、
                // ソフト影の per-sample 色付き tint がそのまま scene_hdr へ焼かれて赤い斑点ノイズに
                // なる。よってソフト影の tint は中心 L の決定的 1 評価へ縮退させる（可視性の
                // ソフトサンプリングは維持）。マスク経路（shadow_mask.wgsl）は false で高品質側。
                radiance = radiance * rt_shadow_factor(
                    s.world_pos, Ng, Nv, L, light_dist, cone_radius, s.frag_coord, true,
                );
            }
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

        // ── シェーディング契約への受け渡し（L3-a 第 1 段）─────────────────
        // このライト 1 灯ぶんを LightSample へ詰めて、契約関数 shade_surface に BRDF を委ねる。
        // 面情報（sf）はループ外で組み立て済みなので、ここで作るのは LightSample だけ。
        //   color            : 影を掛けた後の radiance（減衰・円錐・影を全て織り込んだ実効値）
        //   color_unshadowed : 影を掛ける前の radiance_direct（逆光透けなど影を無視する表現用）
        //
        // ★ ID 0（標準 PBR）経路の同値性:
        //   shade_surface（shading_dispatch.wgsl・アセット未連結時の既定）
        //     → shade_model_0(sf, li)
        //     → shade_light(N, V, L, albedo, mix(vec3(0.04), albedo, metallic),
        //                   metallic, roughness, radiance)
        //   であり、shade_light は旧実装を演算順序ごと無改変で移設したもの、F0 も旧
        //   `mix(vec3<f32>(0.04), albedo, metallic)` と同一式である。**モデル 0 には
        //   shading_nan_guard を掛けない**（ガードはユーザー実装 1..3 の返り値専用。
        //   詳細は shading_dispatch.wgsl のコメント参照）ため、旧
        //   `shade_light(N, V, L, albedo, F0, metallic, roughness, radiance)` と
        //   ビット単位で同一の式が評価される。アセット指定時も default 腕は素通しなので、
        //   ID 0 のピクセルはアセットの有無に関わらず常に既存と同じ値になる。
        var li_sample: LightSample;
        li_sample.direction        = L;
        li_sample.color            = radiance;
        li_sample.color_unshadowed = radiance_direct;
        li_sample.distance         = light_dist;
        li_sample.kind             = light.kind;
        Lo += geo_gate * shade_surface(sf, li_sample);

        // ── 拡散透過（葉・布・紙の逆光透け, KHR_materials_diffuse_transmission 簡易版）──
        // 薄い面が「面の裏側にあるライト」の光を内部散乱で拾い、base_color で色付いて透ける表現。
        // ガラスの鏡面透過（屈折）とは別物で、屈折を伴わない拡散（ランバート）透過。
        //   back = saturate(-dot(N, L))  … 面がライトに背を向けている側の逆光の強さ
        //                                   （N は法線マップ後のシェーディング法線）。
        //   Lo  += radiance_direct × back × diffuse_transmission × albedo / PI
        // 透過色は albedo（base_color）を流用する（専用の拡散透過色は将来拡張）。
        //
        // ★ geo_gate（幾何ゲート）は掛けない。geo_gate は「面が幾何的に光へ背を向けたら直接光を 0 に
        //    する光漏れ防止ゲート」で、まさに dot(Ng,L)<=0（＝逆光側）を殺す。拡散透過は**その逆光側の
        //    光こそが本体**なので、目的が正反対のこのゲートを掛けてはならない。
        // ★ 影（rt_shadow_factor / シャドウマップ）も掛けない。ゆえに影を含まない radiance_direct を使う。
        //    薄い一枚面では、自身の面がシャドウレイをすぐ遮って自己遮蔽になり、逆光透けが常に真っ黒へ
        //    潰れてしまう。葉の逆光透けは「影なしで光る」近似とする。
        //    〔限界〕このため、透ける面と光源の間に**別の遮蔽物**があっても透けは減衰しない
        //           （手前の枝の影が葉の透けに落ちない）。厳密には「自身の面だけをレイ除外した
        //           薄物専用シャドウ」等が要るが、本実装では割り切る（将来の拡張余地）。
        // ★ スポットの円錐減衰・距離減衰は掛ける（radiance_direct に既に含まれている）。
        //
        // ★ 厚み減衰（Beer-Lambert）＋遮蔽（Phase RT-DiffTrans-Thickness）。
        //    薄物向けの上記近似は、厚い不透明物体（石のルーク等）に付けると (1) 厚み減衰が無く
        //    前面全体が一様発光して「薄い紙の塔」に見え、(2) 自身の厚い胴で自己遮蔽された部分まで
        //    明るくなる。そこで RT 有効時は、シェーディング点→光源方向へ 1 本レイを飛ばして不透明
        //    内部の通過厚みを測り、exp(-σ·thickness) を逆光項へ乗じる（rt_diffuse_transmission_factor）。
        //    別の不透明遮蔽物も内部区間として厚みに積まれるため遮蔽部が明るくならない（(2) の解消）。
        //    片面の薄物（葉・布）は front→back ペアが成立せず厚み 0＝係数 1.0 で従来どおり透ける。
        //    RT 無効（rt_shadow_off／実行時 rt_shadows=0）は係数 1.0＝この拡張前の挙動に一致する。
        //    tmax は既存影レイと同流儀（light_dist=geo.dist: directional=RT_DIR_TMAX / 局所=光源距離）。
        if s.diffuse_transmission > 0.0 {
            let back = saturate(-dot(N, L));
            // 厚み減衰（スカラー）× 半透明遮蔽物越しの色付き透過率（RGB）を 1 本の vec3 係数で受ける。
            // RT 無効時は vec3(1)＝この拡張前の挙動（純粋な逆光透け）に一致する。
            var dt_atten = vec3<f32>(1.0, 1.0, 1.0);
            if rt_shadow_enabled() {
                dt_atten = rt_diffuse_transmission_factor(s.world_pos, L, light_dist);
            }
            // dt_atten は成分ごとに乗じる（半透明の青カーテンの陰では透けが暗く、色ガラス越しでは色付く）。
            Lo += radiance_direct * back * s.diffuse_transmission * albedo / PI * dt_atten;
        }

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
    let ambient = evaluate_gi_ambient(s.world_pos, N, albedo, s.occlusion, s.screen_gi);

    // リニア HDR 色（トーンマップ前）。トーンマップは post_tonemap.wgsl が一元的に行う。
    return ambient + Lo + s.emissive;
}
