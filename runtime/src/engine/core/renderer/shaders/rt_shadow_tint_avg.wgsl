// ============================================================
// rt_shadow_tint_avg.wgsl — 色付き影の透過色（従来: プリミティブ平均アルベド経路）
//
// rt_shadow_on.wgsl（コア）の後ろに連結し、rt_shadow_factor から呼ばれる
// `rt_trace_translucent_tint` を供給する。ヒット先の色は平均アルベド storage
// （rt_shadow_albedo, group4 binding14。コア側で宣言）を custom_data で引くベタ塗り近似。
//
// バインドレス非対応 GPU、および対応 GPU でも forward（透明パスの影＝mesh_rt/skinned）と
// 非バインドレスの deferred/shadow_mask がこのファイルを連結する。
//
// 連結順（例, deferred 非バインドレス）:
//   [cluster_common, pbr_common, ddgi_common, light_common, shadow, rt_shadow_on,
//    rt_shadow_tint_avg, surface, lighting_eval, deferred_lighting]
//
// バインドレス版（テクスチャ実サンプル＋Mask アルファ抜き）は rt_shadow_tint_bindless.wgsl。
// 両者は排他（rt_shadow_factor から呼ばれる同名関数を 1 本だけ供給する）。
// ============================================================

/// 半透明レイヤー（cull_mask 0x02）越しの光を染める透過率（RGB）を返す（色付き影）。
/// 不透明で遮蔽されていない方向へ、半透明インスタンスだけをトレースし、**入射面（front-facing）
/// のヒットごと**にそのプリミティブの平均アルベド・被覆 α・透過率 tr で光をフィルタする
/// （T = (1-α)+α·tr·albedo）。
/// inline ray query は最近ヒットしか返さないため、tmin をヒットの先へ進めながら最大
/// max_hits 回（呼び出し側が CENTER=4 / SAMPLE=2 を渡す）再トレースして重なったガラスを貫く。
/// ヒットが無ければ vec3(1)（＝色を付けない）。
///
/// ## 入射面（front_face）だけでフィルタする理由（＝色が二乗になって暗すぎる不具合の修正）
/// 吸収・透過は媒質（物体）1 個につき 1 回起きる物理量である。ところがガラスの駒のような
/// **閉じたメッシュは、レイが表面（入射面）と裏面（出射面）の 2 枚を貫く**。ヒットするたびに
/// 一律 T を掛けると、同じ物体で色フィルタが 2 回掛かって二乗になり、影が暗くなりすぎる
/// （例: シアン albedo=0.6 → 0.6²=0.36）。これは面ごとの二重計上で、物理的に誤り。
/// naga 25 の committed intersection は三角形の表裏を `front_face`（bool）で返すので、
/// **入射面（front_face==true）のヒットでだけ T を掛け、裏面（出射面）は tmin を進めるだけ**にする。
/// これで閉メッシュ 1 個につき T が 1 回だけ掛かり、影が「表面色そのままの明るさ」になる。
///
/// ## 片面ポリゴン（カーテン等の一枚布）での妥当性
/// 巻き順が信頼できない一枚布では front_face 判定が視線（レイ）方向依存になるが、
/// **一枚布はレイが 1 回しか貫かない**ため、その 1 回が front と判定されようが back と判定されようが
/// T の適用回数は最大 1 回で変わらない（二重計上が起きない）。ゆえに実害はない。
/// 表側から見て裏向きと判定された 1 枚が素通り（T 未適用＝色が乗らない）になり得るが、
/// 一枚布は透過色より被覆 α の寄与が主で、色付き影としての見え方への影響は無視できる。
/// - `o`   : レイ原点（自己交差防止のオフセット適用済み）。
/// - `dir` : 光源方向（rt_trace_occlusion と同じ向き）。
/// - `tmax`: レイ最大距離（光源までの距離 or directional の大定数）。
/// - `max_hits`: 貫通する半透明レイヤーの最大枚数（CENTER=4 / SAMPLE=2）。ループの静的上限は
///               常に RT_TRANSLUCENT_MAX_HITS_CENTER（両者の大きい方）で、これを超える反復は
///               `i >= max_hits` で早期 break する（1 ピクセルあたりのレイ本数を静的に有界化）。
fn rt_trace_translucent_tint(o: vec3<f32>, dir: vec3<f32>, tmax: f32, max_hits: u32) -> vec3<f32> {
    var tint = vec3<f32>(1.0, 1.0, 1.0);
    var tmin = RT_SHADOW_TMIN;

    // ループ上限は定数で静的に固定（1 ピクセルあたりのレイ本数が上限を超えない）。
    // 実効上限は引数 max_hits（<= CENTER）で、下の early break で締める。
    for (var i: u32 = 0u; i < RT_TRANSLUCENT_MAX_HITS_CENTER; i = i + 1u) {
        if i >= max_hits {
            break;
        }
        var desc: RayDesc;
        // 最近ヒットを取り、次の反復で tmin をその先へ進める（TERMINATE は付けない）。
        desc.flags     = RAY_FLAG_NONE;
        desc.cull_mask = RT_TRANSLUCENT_CULL_MASK; // 半透明インスタンスのみ
        desc.tmin      = tmin;
        desc.tmax      = max(tmax, tmin);
        desc.origin    = o;
        desc.dir       = dir;

        var rq: ray_query;
        rayQueryInitialize(&rq, rt_accel, desc);
        rayQueryProceed(&rq);
        let hit = rayQueryGetCommittedIntersection(&rq);

        // これ以上半透明レイヤーは無い＝現在の透過色で確定。
        if hit.kind == RAY_QUERY_INTERSECTION_NONE {
            break;
        }

        // 入射面（front-facing）のヒットでだけ色フィルタを掛ける。
        // 閉メッシュは表面（入射）＋裏面（出射）の 2 枚を貫くため、両方で掛けると
        // 色フィルタが二乗になって暗すぎる。裏面（front_face==false）は下で tmin を
        // 進めるだけにして、媒質 1 個あたり T を 1 回だけに保つ（関数 doc の詳細参照）。
        // naga 25 の committed intersection では front_face は bool（type_gen.rs で検証済み）。
        if hit.front_face {
        // 平均アルベド（.rgb）＋パック済み α/transmission（.a）を custom_data で引く。
        // .a は rt_shadow.rs::pack_shadow_alpha_transmission が
        //   a_q = round(α * SHADOW_PACK_QUANT), t_q = round(tr * SHADOW_PACK_QUANT)
        //   .a  = a_q * SHADOW_PACK_RADIX + t_q
        // と詰めた固定小数（f32 の整数完全表現域内）。ここでその逆でデコードする。
        // 範囲外インデックスは何もフィルタしない（tint 不変＝白のまま＝色を付けない）。
        let ai = hit.instance_custom_data;
        if ai < arrayLength(&rt_shadow_albedo) {
            let entry  = rt_shadow_albedo[ai];
            let packed = entry.a;
            let a_q    = floor(packed / SHADOW_PACK_RADIX);
            let t_q    = packed - a_q * SHADOW_PACK_RADIX;
            let alpha  = clamp(a_q / SHADOW_PACK_QUANT, 0.0, 1.0); // 被覆（base_color_factor.a）
            let tr     = clamp(t_q / SHADOW_PACK_QUANT, 0.0, 1.0); // 透過率（KHR_materials_transmission）
            // 1 枚の Blend 面を通る光の RGB 透過率:
            //   T = (1-α) + α·transmission·albedo.rgb
            //   ・α=1, tr=1 → T = albedo   （色の付いた影＝色ガラスは baseColor で透過光を濾過する）
            //   ・α=1, tr=0 → T = 0        （透過しない被覆面は光を通さない＝暗い影）
            //   ・α=0        → T = 1        （影なし）
            // 非透過成分 α·(1-tr) は光を通さない（＝0）ため式に現れない。透過光だけがアルベドで色付く。
            let layer_t = vec3<f32>(1.0 - alpha) + alpha * tr * entry.rgb;
            tint = tint * layer_t;
        }
        } // if hit.front_face（裏面ヒットはフィルタせず tmin を進めるだけ）

        // 次のレイヤーへ: tmin を直前ヒットの手前まで進める（同一面の再ヒットを避ける）。
        tmin = hit.t + RT_TRANSLUCENT_T_STEP;
        if tmin >= tmax {
            break;
        }
    }

    return tint;
}
