// ============================================================
// ao_rt.wgsl — レイトレ アンビエントオクルージョン（fragment fs_rt, RAY_QUERY 必須）
//
// G-Buffer のワールド位置＋法線から、法線半球へコサイン加重の短い遮蔽レイを複数飛ばし、
// ヒット率＝近接遮蔽率として AO を求める。ライト情報は不要（AO は「近くに何かあるか」だけ）。
// 結果 AO（1=遮蔽なし / 0=完全遮蔽）を半解像度 ao_raw（.r）へ出力する。
//
// 連結順: [ao_common, ao_rt]（RAY_QUERY ケイパビリティ必須）
//
// 【半解像度で計算する理由】
// SSAO と出力先（ao_raw）・ブラー（imos）・合成（deferred の occlusion 乗算）を共有するため、
// RT-AO も半解像度で計算する。フル解像度はレイ本数×4 でコスト過大（35W GPU 予算に不見合い）。
// AO は低周波なので半解像度＋バイリニアアップサンプルで十分。
//
// 【サンプル方向（コサイン半球, Vogel + IGN 回転）】
// Vogel ディスク（半径 √((i+0.5)/N)・角度 i*黄金角+回転）を接空間ディスクに張り、
// z=√(1-r²) で半球へ持ち上げる（＝コサイン加重）。ピクセルごとに IGN で回転しバンディングを崩す。
//
// 【自己交差回避】原点をシェーディング法線へ RTAO_NORMAL_OFFSET、幾何法線へ
// RTAO_GEO_CLEARANCE 押し出し（RT 影 rt_shadow_on.wgsl と同一の 2 項）、tmin=RTAO_TMIN で
// 下限を切る。法線マップの効いた面／曲がったスキンメッシュではシェーディング法線だけの
// 押し出しが面の裏側に残り、黒い斑点（自己交差）になるため幾何法線項が要る。
// cull_mask=0x01（不透明のみ）。RAY_FLAG_TERMINATE_ON_FIRST_HIT で最初のヒットで打ち切る。
// ============================================================

/// group 3 binding 0: RT-AO 用 TLAS（RT 影と共有される加速構造。ライト等は bind しない）。
@group(3) @binding(0) var ao_tlas: acceleration_structure;

/// 1 フラグメントあたりの遮蔽レイ本数（ヒット率の分母）。ブラーで均す前提の少なめ設定。
///
/// 【4→8 に増やした理由】遮蔽率は二値ヒット判定の平均であり、4 本では 5 段階（0/.25/.5/.75/1）に
/// しか量子化されない。Play 中はスキン BLAS が毎フレーム再構築されて判定の位相が入れ替わるため、
/// この粗い段差がフレーム間のチカチカとして見える。8 本＝9 段階で 1 段差の振幅が半分になる。
/// コスト: RTAO は半解像度（AO_RESOLUTION_DIVISOR）で評価されるためフル解像度換算 ×1/4。
const RTAO_RAY_COUNT:      u32 = 8u;
/// レイ最小距離（自己交差の下限。法線オフセットと併用。rt_shadow_on.wgsl と同値）。
const RTAO_TMIN:           f32 = 0.001;
/// 原点の法線方向オフセット（世界単位。rt_shadow_on.wgsl の RT_SHADOW_NORMAL_BIAS と同値）。
const RTAO_NORMAL_OFFSET:  f32 = 0.02;
/// 幾何法線方向の最小クリアランス（rt_shadow_on.wgsl の RT_SHADOW_GEO_CLEARANCE と同値）。
///
/// 【なぜ法線オフセットだけでは足りないのか】`RTAO_NORMAL_OFFSET` が押し出す `n` は
/// **G-Buffer のシェーディング法線**（法線マップ適用後）であり、実際の三角形の向き
/// （幾何法線 Ng）とは大きくずれうる。法線マップが強い面や、スキンメッシュのように
/// 面が曲がってポリゴンが縮む箇所では、シェーディング法線方向へ 2cm 押し出しても
/// **原点が自分の三角形の裏側に残る**ことがあり、そのレイが自分自身に当たって
/// AO が 0（＝黒い斑点）になる。Ng 方向にも最小クリアランスを足すと、押し出しが
/// 必ず面の表側へ抜ける。総オフセットは最大でも 0.02 + 0.005 = 0.025 ワールド単位で、
/// RT 影（rt_shadow_on.wgsl）とまったく同じ量に揃う（AO と影の接地感がずれない）。
const RTAO_GEO_CLEARANCE:  f32 = 0.005;
/// G-Buffer 法線が「authored（草・地形など、深度復元 Ng が信用できない面）」かの閾値。
/// deferred_lighting.wgsl の GBUFFER_NORMAL_AUTHORED_THRESHOLD と同値であること。
const RTAO_AUTHORED_THRESHOLD: f32 = 0.5;
/// 影のインスタンスカリングマスク（不透明のみ。rt_shadow_on.wgsl の RT_SHADOW_CULL_MASK と同値）。
const RTAO_CULL_MASK:      u32 = 0x01u;
/// AO の効き（ヒット率にかける係数）。intensity ノブ（u_ao.intensity）と併せて最終強度を決める。
const RTAO_STRENGTH:       f32 = 1.0;

/// 単一の遮蔽レイを飛ばして遮蔽（1.0）/ 非遮蔽（0.0）を返す。
/// 近接遮蔽なので tmax は AO 半径（u_ao.radius）＝短いレイ。最初のヒットで打ち切る。
fn rtao_trace(o: vec3<f32>, dir: vec3<f32>, tmax: f32) -> f32 {
    var desc: RayDesc;
    desc.flags     = RAY_FLAG_TERMINATE_ON_FIRST_HIT;
    desc.cull_mask = RTAO_CULL_MASK;
    desc.tmin      = RTAO_TMIN;
    desc.tmax      = max(tmax, RTAO_TMIN);
    desc.origin    = o;
    desc.dir       = dir;

    var rq: ray_query;
    rayQueryInitialize(&rq, ao_tlas, desc);
    rayQueryProceed(&rq);
    let hit = rayQueryGetCommittedIntersection(&rq);
    if hit.kind != RAY_QUERY_INTERSECTION_NONE {
        return 1.0; // ヒット＝近くに遮蔽物あり
    }
    return 0.0;
}

@fragment
fn fs_rt(in: AoVsOut) -> @location(0) vec4<f32> {
    let uv  = in.uv;
    let pix = ao_full_pix(uv);

    // ── 1) ワールド位置と画面微分（**早期 return より前**に済ませる）───────
    // dpdx/dpdy は一様制御フローの中でしか呼べないため、背景判定の分岐より前で
    // 評価する。背景ピクセルの値は使われないので、無効な world_pos を通しても害はない。
    let depth     = textureLoad(t_depth, pix, 0);
    let world_pos = ao_world_pos(uv, depth);
    // 幾何法線の微分は**カメラ相対座標**で取る（絶対ワールド座標だと f32 の桁落ちで
    // Ng が数度ずれ、レイ原点のクリアランス方向が暴れて AO が黒斑点になる。
    // 外積は平行移動不変なので Ng の意味は不変・精度だけが上がる。
    // 根拠は ao_common.wgsl の ao_cam_rel_pos のコメント）。
    let rel_pos   = ao_cam_rel_pos(uv, depth);
    let ng_raw    = cross(dpdx(rel_pos), dpdy(rel_pos));

    // ── 2) 背景（depth>=1）は AO=1（遮蔽なし）で早期 return ─────────────
    if depth >= AO_BACKGROUND_DEPTH {
        return vec4<f32>(1.0, 0.0, 0.0, 1.0);
    }

    // ── 3) 法線＋幾何法線＋接空間基底 ───────────────────────────────────
    let g1        = textureLoad(t_gbuffer1, pix, 0);
    let n         = normalize(g1.xyz);
    let tangent   = ao_perp(n);
    let bitangent = cross(n, tangent);
    let rot       = ao_ign(in.pos.xy) * 2.0 * AO_PI;

    // 幾何法線 Ng（deferred_lighting.wgsl の復元とまったく同じ規則）:
    //   - 画面上でほぼゼロ面積（縮退）なら n で代用
    //   - n と同じ半球へ符号を揃える
    //   - authored 法線（草・地形。g1.w>=閾値）は深度不連続で暴れる Ng を信用せず n を使う
    let ng_len = length(ng_raw);
    var ng: vec3<f32>;
    if ng_len < 1e-8 {
        ng = n;
    } else {
        ng = ng_raw / ng_len;
    }
    if dot(ng, n) < 0.0 {
        ng = -ng;
    }
    if g1.w >= RTAO_AUTHORED_THRESHOLD {
        ng = n;
    }

    // 原点をシェーディング法線＋幾何法線の両方向へ固定量押し出す（自己交差回避）。
    // 総オフセットは最大でも RTAO_NORMAL_OFFSET + RTAO_GEO_CLEARANCE = 0.025 ワールド単位。
    let o = world_pos + n * RTAO_NORMAL_OFFSET + ng * RTAO_GEO_CLEARANCE;

    // ── 4) コサイン半球へ短いレイを分散し、ヒット率を平均する ───────────
    var hits = 0.0;
    for (var i: u32 = 0u; i < RTAO_RAY_COUNT; i = i + 1u) {
        // Vogel ディスク（接空間）→ z=√(1-r²) で半球へ（コサイン加重）。
        let fi    = f32(i) + 0.5;
        let r     = sqrt(fi / f32(RTAO_RAY_COUNT));
        let theta = f32(i) * AO_GOLDEN_ANGLE + rot;
        let dx    = r * cos(theta);
        let dy    = r * sin(theta);
        let dz    = sqrt(max(1.0 - r * r, 0.0));
        let dir   = normalize(tangent * dx + bitangent * dy + n * dz);
        hits = hits + rtao_trace(o, dir, u_ao.radius);
    }

    // ヒット率 → AO（1=遮蔽なし）。強度は定数 × intensity ノブ。
    let occ = (hits / f32(RTAO_RAY_COUNT)) * RTAO_STRENGTH * u_ao.intensity;
    let ao  = clamp(1.0 - occ, 0.0, 1.0);
    return vec4<f32>(ao, 0.0, 0.0, 1.0);
}
