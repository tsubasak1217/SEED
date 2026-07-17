// ============================================================
// ssgi_gen.wgsl — スクリーンスペース GI 生成（fragment fs_ssgi, RAY_QUERY 不要）
//
// G-Buffer の深度＋ワールド法線からコサイン半球方向へ数本のスクリーンスペースレイを
// マーチし、ヒットしたら scene_hdr（不透明ライティング済み）の色を拾って平均する
// ＝ 1 バウンスの間接光。ミス（画面外/背景へ抜け）はフラットアンビエント色で埋める
// （完全な黒にしない。画面外・遮蔽の情報欠落を目立たせないため）。結果を半解像度
// ssgi_raw（.rgb）へ出力する。ピクセルごとの IGN 回転でノイズを分散し、後段のいもす法
// カラーブラー（2〜3 反復）でデノイズする。
//
// 連結順: [ssgi_common, ssgi_gen]
//
// 【マーチ定数（根拠）】反射（SSR）より短距離・粗ステップでよい（間接光は低周波で、鏡面反射の
// ような精密なヒット位置を必要としないため）。
//   SSGI_NUM_DIRS=3       : コサイン半球のレイ本数（2〜4 本の中庸。多いほど滑らかだがコスト線形増）
//   SSGI_MAX_STEPS=16     : 1 レイあたり最大マーチ数（SSR の 64 より粗い）
//   SSGI_MAX_DISTANCE=5.0 : ワールド単位の最大到達距離（近傍バウンス重視。SSR の 30 より短い）
//   SSGI_STEP             : 1 ステップ前進量（= MAX_DISTANCE / MAX_STEPS）
//   SSGI_THICKNESS=0.5    : 交差判定のビュー空間 Z 厚み（擦り抜け防止と誤ヒット抑制の折衷。SSR と同値）
//
// 【時間的蓄積は今回やらない】モーションベクタが無いため、時間的再投影による蓄積は行わない。
// 静止画では空間ブラーのみで十分収束する。TODO(SSGI-Temporal): モーションベクタ導入時に
// 前フレーム再投影＋履歴ブレンドでノイズをさらに下げる（動きの残像対策込みで）。
// ============================================================

/// コサイン半球のレイ本数。名前付き定数（マジックナンバー禁止）。
const SSGI_NUM_DIRS:     i32 = 3;
/// 1 レイあたり最大マーチ数。
const SSGI_MAX_STEPS:    i32 = 16;
/// ワールド単位の最大到達距離（メートル）。
const SSGI_MAX_DISTANCE: f32 = 5.0;
/// 1 ステップ前進量。
const SSGI_STEP:         f32 = SSGI_MAX_DISTANCE / 16.0;
/// 交差判定のビュー空間 Z 厚み。
const SSGI_THICKNESS:    f32 = 0.5;

// ビュー空間 Z（負が前方）。
fn ssgi_view_z(world_pos: vec3<f32>) -> f32 {
    return (u_camera.view * vec4<f32>(world_pos, 1.0)).z;
}

// ワールド点をスクリーン UV へ投影。valid=画面内かつカメラ前方。
struct SsgiProj { uv: vec2<f32>, valid: bool }
fn ssgi_project(world_pos: vec3<f32>) -> SsgiProj {
    var r: SsgiProj;
    let clip = u_camera.view_proj * vec4<f32>(world_pos, 1.0);
    if clip.w <= 0.0 {
        r.uv = vec2<f32>(0.0, 0.0);
        r.valid = false;
        return r;
    }
    let ndc = clip.xyz / clip.w;
    r.uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    r.valid = all(r.uv >= vec2<f32>(0.0, 0.0)) && all(r.uv <= vec2<f32>(1.0, 1.0));
    return r;
}

// 1 本のレイをスクリーン空間でマーチし、ヒットしたら scene_hdr 色を返す。
// ミス（画面外/背景/擦り抜け）は false を返し、呼び出し側がフラットアンビエントで埋める。
struct SsgiHit { color: vec3<f32>, hit: bool }
fn ssgi_march(origin: vec3<f32>, dir: vec3<f32>) -> SsgiHit {
    var out: SsgiHit;
    out.color = vec3<f32>(0.0);
    out.hit = false;
    var t = SSGI_STEP;
    for (var i: i32 = 0; i < SSGI_MAX_STEPS; i = i + 1) {
        let p    = origin + dir * t;
        let proj = ssgi_project(p);
        if !proj.valid {
            return out; // 画面外へ抜けた＝ミス。
        }
        let spix        = ssgi_full_pix(proj.uv);
        let scene_depth = textureLoad(t_depth, spix, 0);
        if scene_depth >= SSGI_BACKGROUND_DEPTH {
            t = t + SSGI_STEP;
            continue; // 背景（何も無い）＝この位置ではヒットしない。
        }
        let scene_world = ssgi_world_pos(proj.uv, scene_depth);
        let ray_vz      = ssgi_view_z(p);
        let scene_vz    = ssgi_view_z(scene_world);
        let diff        = ray_vz - scene_vz;
        // レイがジオメトリの裏（奥）へ回り込み、かつ厚み内なら交差とみなす。
        if diff > 0.0 && diff < SSGI_THICKNESS {
            out.color = textureSampleLevel(t_scene_hdr, s_scene, proj.uv, 0.0).rgb;
            out.hit = true;
            return out;
        }
        t = t + SSGI_STEP;
    }
    return out; // 最大ステップまでヒットせず＝ミス。
}

@fragment
fn fs_ssgi(in: SsgiVsOut) -> @location(0) vec4<f32> {
    let uv  = in.uv;
    let pix = ssgi_full_pix(uv);

    // ── 背景（depth>=1）はフラットアンビエントで埋めて早期 return ─────
    // 背景はそもそも deferred ライティングされない（discard）ため参照されないが、
    // 半解像度アップサンプルの端で背景が混ざっても黒縁にならないようフラット色で埋める。
    let depth = textureLoad(t_depth, pix, 0);
    if depth >= SSGI_BACKGROUND_DEPTH {
        return vec4<f32>(u_ssgi.ambient, 1.0);
    }

    // ── ワールド位置＋法線＋接空間基底（TBN）─────────────────
    let world_pos = ssgi_world_pos(uv, depth);
    let n         = normalize(textureLoad(t_gbuffer1, pix, 0).xyz);
    let tangent   = ssgi_perp(n);
    let bitangent = cross(n, tangent);
    // ピクセルごとの回転（IGN。時間項なし＝静止画で安定。ブラーでさらに均す）。
    let rot       = ssgi_ign(in.pos.xy) * 2.0 * SSGI_PI;

    // ── コサイン半球へ SSGI_NUM_DIRS 本のレイを張り、間接光を平均する ───
    var acc = vec3<f32>(0.0);
    for (var i: i32 = 0; i < SSGI_NUM_DIRS; i = i + 1) {
        // コサイン重み付き半球サンプル（接空間, +z 上向き）。
        //   u1 = (i+0.5)/N（層化した動径）、phi = i*黄金角 + 回転。
        //   コサイン重み: 半径 r=sqrt(u1)、高さ z=sqrt(1-u1)。
        let fi  = f32(i) + 0.5;
        let u1  = fi / f32(SSGI_NUM_DIRS);
        let phi = f32(i) * SSGI_GOLDEN_ANGLE + rot;
        let rr  = sqrt(u1);
        let z   = sqrt(max(1.0 - u1, 0.0));
        let k   = vec3<f32>(rr * cos(phi), rr * sin(phi), z);
        // 接空間 → ワールド方向。
        let dir = normalize(tangent * k.x + bitangent * k.y + n * k.z);

        let m = ssgi_march(world_pos, dir);
        // ヒット＝拾った間接光、ミス＝フラットアンビエント（黒で埋めない）。
        acc = acc + select(u_ssgi.ambient, m.color, m.hit);
    }
    let indirect = acc / f32(SSGI_NUM_DIRS);
    return vec4<f32>(indirect, 1.0);
}
