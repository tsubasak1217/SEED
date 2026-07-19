// ============================================================
// rt_shadow_tint_bindless.wgsl — 色付き影の透過色（バインドレス: ヒット点テクスチャ実サンプル）
//
// rt_shadow_on.wgsl（コア）＋ bindless_common.wgsl（BindlessInstanceRecord 定義）の後ろに連結し、
// rt_shadow_factor から呼ばれる `rt_trace_translucent_tint` を供給する（rt_shadow_tint_avg.wgsl の
// バインドレス版・排他）。ヒット先の色は「ヒット点 UV でベースカラーテクスチャを実サンプル ×
// base_color_factor」。ステンドグラスの模様がそのまま床の影に落ちる（B3）。
//
// さらに Mask マテリアル（アルファテスト＝葉・鎖・フェンス）を影に落とす:
//   第 2 クエリ（RT_TRANSLUCENT_CULL_MASK 0x02）で Mask 面にヒットしたら、テクスチャ α を
//   alpha_cutoff と比較して「α>=cutoff → 完全遮蔽（tint=0 で早期終了＝葉の形の影）」
//   「α<cutoff → 素通り（フィルタせず tmin を進める）」。→ 葉っぱの形の影が落ちる。
//
// ## group3（バインドレス影資源。deferred/shadow_mask のバインドレス影バリアント専用）
//   WebGPU 制約「binding_array と uniform buffer は同一 bind group に同居不可」を満たすため、
//   バインドレス資源は uniform を含まない group3 に隔離する（group4 のライト uniform とは別グループ）。
//   binding 0 = instance_table（BindlessInstanceRecord 配列。custom_data で引く）
//   binding 1 = UV メガバッファ（vec2<f32>。頂点番号で引く）
//   binding 2 = index メガバッファ（u32。三角形の頂点番号）
//   binding 3 = テクスチャ配列（binding_array<texture_2d<f32>>。albedo_tex_index で引く）
//   binding 4 = 共有サンプラー（リニア・リピート）
// Rust 側 BGL は bindless.rs::colored_shadow_bgl と一致させること。
//
// 透過率 tr（KHR_materials_transmission）はテクスチャに無いマテリアル値のため、コア側と同じく
// avg_albedo.a のパック値（rt_shadow.rs::pack_shadow_alpha_transmission）から復号する。
// 被覆 α とアルベド .rgb のみテクスチャ実サンプルへ差し替える。
// ============================================================

@group(3) @binding(0) var<storage, read> bls_records: array<BindlessInstanceRecord>;
@group(3) @binding(1) var<storage, read> bls_uv:      array<vec2<f32>>;
@group(3) @binding(2) var<storage, read> bls_index:   array<u32>;
@group(3) @binding(3) var bls_tex: binding_array<texture_2d<f32>>;
@group(3) @binding(4) var bls_smp: sampler;

// パック値（avg_albedo.a）から被覆 α を復号する（コア SHADOW_PACK_* と対）。
fn bls_unpack_alpha(packed: f32) -> f32 {
    let a_q = floor(packed / SHADOW_PACK_RADIX);
    return clamp(a_q / SHADOW_PACK_QUANT, 0.0, 1.0);
}
// パック値（avg_albedo.a）から透過率 tr を復号する。
fn bls_unpack_transmission(packed: f32) -> f32 {
    let a_q = floor(packed / SHADOW_PACK_RADIX);
    let t_q = packed - a_q * SHADOW_PACK_RADIX;
    return clamp(t_q / SHADOW_PACK_QUANT, 0.0, 1.0);
}

// ヒット三角形の UV を重心補間し、ベースカラーテクスチャをミップ 0 で実サンプルする（RGBA）。
// フルスクリーンフラグメント（deferred/shadow_mask）では画面微分が無効なため必ず明示 LOD を使う。
// 添字（albedo_tex_index）はヒットごとに非一様（NON_UNIFORM_INDEXING 前提）。
fn bls_sample_texel(rec: BindlessInstanceRecord, prim_index: u32, bary: vec2<f32>) -> vec4<f32> {
    let tri = rec.index_offset + prim_index * 3u;
    let i0 = bls_index[tri + 0u];
    let i1 = bls_index[tri + 1u];
    let i2 = bls_index[tri + 2u];
    let uv0 = bls_uv[rec.uv_offset + i0];
    let uv1 = bls_uv[rec.uv_offset + i1];
    let uv2 = bls_uv[rec.uv_offset + i2];
    let w  = vec3<f32>(1.0 - bary.x - bary.y, bary.x, bary.y);
    let uv = uv0 * w.x + uv1 * w.y + uv2 * w.z;
    return textureSampleLevel(bls_tex[rec.albedo_tex_index], bls_smp, uv, 0.0);
}

/// Blend 面 1 枚の RGB 透過率 T（バインドレス: ヒット点テクスチャ実サンプル）を返す。
/// front/back 共通の評価（呼び出し側がどちらの面で掛けるかを決める）。
/// albedo/α は eligible ならテクスチャ実サンプル×factor、非 ELIGIBLE なら平均アルベドへ縮退。
/// 透過率 tr はテクスチャに無いマテリアル値のため avg_albedo.a のパック値から復号。
/// T = (1-α) + α·tr·albedo。
fn bls_blend_layer_t(rec: BindlessInstanceRecord, eligible: bool, prim_index: u32, bary: vec2<f32>) -> vec3<f32> {
    let tr = bls_unpack_transmission(rec.avg_albedo.a);
    var albedo = rec.avg_albedo.rgb;          // 非 ELIGIBLE の縮退＝平均アルベド
    var alpha  = bls_unpack_alpha(rec.avg_albedo.a);
    if eligible {
        let texel = bls_sample_texel(rec, prim_index, bary);
        albedo = texel.rgb * rec.base_color_factor.rgb; // ステンドグラスの模様
        alpha  = clamp(texel.a * rec.base_color_factor.a, 0.0, 1.0);
    }
    return vec3<f32>(1.0 - alpha) + alpha * tr * albedo;
}

/// 半透明レイヤー（cull_mask 0x02）越しの光を染める透過率（RGB）を返す（色付き影・バインドレス）。
/// rt_shadow_tint_avg.wgsl と同一シグネチャ。差分は「ヒット点テクスチャ実サンプル」＋「Mask アルファ抜き」。
///
/// 各ヒット（0x02 半透明・Mask 含む）で:
///   - Mask（BINDLESS_FLAG_MASK）: テクスチャ α（×factor.a）を alpha_cutoff と比較。
///       α>=cutoff → 完全遮蔽（vec3(0) を返して即終了＝葉の形の影）。α<cutoff → 素通り。
///       front/back は問わない（一枚カードは 1 回貫くだけ。表裏どちらでも遮蔽判定は同じ）。
///       ⇒ Mask は pair_back に依らず表裏どちらから当たっても遮蔽するため、青カーテンが
///          Mask なら本経路では元々遮蔽される（本タスクの裏面対応は下の Blend 分岐が担う）。
///   - Blend: 透過色フィルタ。avg 版と同じ pair_back 規則で裏面の扱いを切り替える（下記）。
///   - 非 ELIGIBLE（UV/tex 未登録）: 平均アルベド（avg_albedo）へ縮退（従来のベタ塗り相当）。
///   - 範囲外 custom_data: 何もせず tmin を進める（tint 不変）。
///
/// `pair_back`（avg 版と同一セマンティクス。Blend 面にのみ作用。Mask は常に表裏遮蔽で不変）:
///   false（色付き影の 4 呼び出し点）: Blend は front-face のみフィルタ・裏面スキップ（挙動不変）。
///   true （rt_diffuse_transmission_factor 専用）: 直前 front と同一 instance の裏面はスキップ
///          （閉ガラスの出射面）、対の無い裏面は T を掛ける（裏から当たった一枚 Blend シート）。
fn rt_trace_translucent_tint(o: vec3<f32>, dir: vec3<f32>, tmax: f32, max_hits: u32, pair_back: bool) -> vec3<f32> {
    var tint = vec3<f32>(1.0, 1.0, 1.0);
    var tmin = RT_SHADOW_TMIN;

    // 拡散透過ペアリング状態（pair_back==true のときだけ使う。avg 版と同一）。
    // Blend 面の閉メッシュ出射面を 1 組だけ追跡する（Mask 分岐はこの状態を触らない）。
    var front_active   = false; // 直前に「front 通過中」の Blend instance があるか
    var front_instance = 0u;    // その instance の custom_data

    // ループ上限は定数で静的に固定（1 ピクセルあたりのレイ本数が上限を超えない）。
    for (var i: u32 = 0u; i < RT_TRANSLUCENT_MAX_HITS_CENTER; i = i + 1u) {
        if i >= max_hits {
            break;
        }
        var desc: RayDesc;
        desc.flags     = RAY_FLAG_NONE;
        desc.cull_mask = RT_TRANSLUCENT_CULL_MASK; // 半透明＋Mask インスタンス
        desc.tmin      = tmin;
        desc.tmax      = max(tmax, tmin);
        desc.origin    = o;
        desc.dir       = dir;

        var rq: ray_query;
        rayQueryInitialize(&rq, rt_accel, desc);
        rayQueryProceed(&rq);
        let hit = rayQueryGetCommittedIntersection(&rq);

        // これ以上レイヤーは無い＝現在の透過色で確定。
        if hit.kind == RAY_QUERY_INTERSECTION_NONE {
            break;
        }

        let ai = hit.instance_custom_data;
        if ai < arrayLength(&bls_records) {
            let rec      = bls_records[ai];
            let is_mask  = (rec.flags & BINDLESS_FLAG_MASK) != 0u;
            let eligible = (rec.flags & BINDLESS_FLAG_ELIGIBLE) != 0u;

            if is_mask {
                // ── Mask（アルファテスト）: テクスチャ α を alpha_cutoff と比較 ──
                // 被覆 α はテクスチャ実サンプル（×factor.a）。未登録なら平均 α へ縮退。
                // front/back を問わず遮蔽する（一枚カードは表裏どちらでも同じ）ため pair_back 非依存。
                var alpha = bls_unpack_alpha(rec.avg_albedo.a);
                if eligible {
                    let texel = bls_sample_texel(rec, hit.primitive_index, hit.barycentrics);
                    alpha = clamp(texel.a * rec.base_color_factor.a, 0.0, 1.0);
                }
                if alpha >= rec.alpha_cutoff {
                    // 完全遮蔽（葉の形の影）。以降のレイヤーは無意味＝即終了。
                    return vec3<f32>(0.0, 0.0, 0.0);
                }
                // α<cutoff は素通り（フィルタせず tmin を進めるだけ）。
            } else {
                // ── Blend（半透明）: avg 版と同じ pair_back 規則で裏面の扱いを切り替える ──
                if hit.front_face {
                    // 入射面: 常にフィルタ。閉メッシュの二重計上回避は下の裏面分岐が担う。
                    tint = tint * bls_blend_layer_t(rec, eligible, hit.primitive_index, hit.barycentrics);
                    if pair_back {
                        front_active   = true;
                        front_instance = ai;
                    }
                } else if pair_back {
                    // 裏面 ＋ 拡散透過遮蔽レイ: ペアリングで扱いを決める。
                    if front_active && front_instance == ai {
                        // 直前 front と同一 instance＝閉メッシュの出射面。二重計上しない。
                        front_active = false;
                    } else {
                        // 対の無い裏面＝裏から当たった一枚 Blend シート。T を掛けて遮蔽する。
                        tint = tint * bls_blend_layer_t(rec, eligible, hit.primitive_index, hit.barycentrics);
                    }
                }
                // 裏面 ＋ pair_back==false（色付き影）: 従来どおりスキップ（front-only 規則を維持）。
            }
        }

        // 次のレイヤーへ: tmin を直前ヒットの手前まで進める（同一面の再ヒットを避ける）。
        tmin = hit.t + RT_TRANSLUCENT_T_STEP;
        if tmin >= tmax {
            break;
        }
    }

    return tint;
}
