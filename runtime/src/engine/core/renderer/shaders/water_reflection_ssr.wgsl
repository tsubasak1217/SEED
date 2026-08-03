// ============================================================
//  water_reflection_ssr.wgsl — 水面のスクリーンスペース反射（Phase W5.2 フォールバック）
//
//  ## 役割（単一責任）
//  **RT 非対応 GPU／RT 反射を使わない設定**のときに、RT 変種
//  （`water_reflection_rt.wgsl`）の代わりに同じ反射 RT を埋める。
//  出力の意味・フォーマット・A の規約（＝反射強度）は RT 変種と完全に同一で、
//  水面パス（`water_surface.wgsl`）から見ればどちらが走ったかは区別できない。
//
//  ## 方式
//  水面の波法線から反射ベクトル R を作り、シーン深度（group3）に対して
//  ワールド空間の線形マーチ＋二分細分で交差を探し、当たったらシーンカラーの
//  グラブをその UV でサンプルする（`reflection_ssr.wgsl` と同一手法）。
//  外れたら「画面内に写っている空」→ GI プローブ／環境光の順にフォールバックする。
//
//  ## 不透明 SSR と違う点
//  ヒット面の法線による裏面棄却（`dot(n_hit, R) > 0` で捨てる）は行わない。
//  水面パスの group1 は水パラメータで埋まっており **G-Buffer 法線をバインドできない**
//  ため。裏面を拾った場合は「水面に映るはずのない面の色」が出るが、
//  ・薄い厚み判定（`WATER_SSR_THICKNESS`）を通過する必要がある
//  ・そもそも SSR は RT 非対応 GPU 用のフォールバックである
//  ことから、追加のバインドグループを消費してまで潰す価値は無いと判断した。
//
//  ## 連結順
//    [water_height_field, ddgi_common, water_reflection_common, （本ファイル）]
//  RAY_QUERY を一切使わないので、レイトレ非対応の GPU でもモジュール生成が通る。
// ============================================================

// ─── マーチ定数（`reflection_ssr.wgsl` と同じ根拠・同じ値）─────

/// 最大サンプル数（コスト上限）。
const WATER_SSR_MAX_STEPS: i32 = 64;
/// ワールド単位の最大到達距離（m）。
const WATER_SSR_MAX_DISTANCE: f32 = 30.0;
/// 1 ステップの前進量（= 最大距離 / 最大ステップ数）。
const WATER_SSR_STEP: f32 = WATER_SSR_MAX_DISTANCE / 64.0;
/// 交差判定のビュー空間 Z 厚み（すり抜け防止と誤ヒット抑制の折衷）。
const WATER_SSR_THICKNESS: f32 = 0.5;
/// 交差区間の二分細分回数（2^5 = 32 分解能）。
const WATER_SSR_BINARY_STEPS: i32 = 5;

// ─── フラグメント ────────────────────────────────────────────

@fragment
fn fs_water_reflection(in: WaterReflVsOut) -> @location(0) vec4<f32> {
    let s = water_refl_surface(in);
    // 不透明物に隠れている水面ピクセルは書かない（RT はクリア値 0 のまま＝反射なし）。
    if s.occluded {
        discard;
    }
    if s.intensity <= 0.0 {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    let dim = vec2<f32>(textureDimensions(t_water_scene));

    var hit    = false;
    var hit_uv = vec2<f32>(0.0, 0.0);
    var t      = WATER_SSR_STEP;
    var prev_t = 0.0;
    for (var i: i32 = 0; i < WATER_SSR_MAX_STEPS; i = i + 1) {
        let p    = s.world_pos + s.r * t;
        let proj = water_refl_project(p);
        if !proj.valid {
            break; // 画面外へ出た＝これ以上は追えない。
        }
        let scene_depth = water_refl_load_depth(proj.uv * dim);
        if scene_depth >= WATER_REFL_SKY_DEPTH {
            // 背景（空）には面が無いので通過させる。空自体はミス後のフォールバックが拾う。
            prev_t = t;
            t = t + WATER_SSR_STEP;
            continue;
        }
        let scene_world = water_refl_world_from_depth(proj.uv, scene_depth);
        let diff        = water_refl_view_z(p) - water_refl_view_z(scene_world);
        // diff > 0 … レイ点が可視面より奥へ潜った＝交差。厚みで上限を切って
        //            「はるか奥まで潜った（＝別の物体の裏）」の誤ヒットを弾く。
        if diff > 0.0 && diff < WATER_SSR_THICKNESS {
            var lo = prev_t;
            var hi = t;
            for (var k: i32 = 0; k < WATER_SSR_BINARY_STEPS; k = k + 1) {
                let mid = (lo + hi) * 0.5;
                let pm  = s.world_pos + s.r * mid;
                let pjm = water_refl_project(pm);
                if !pjm.valid {
                    hi = mid;
                    continue;
                }
                let sdm = water_refl_load_depth(pjm.uv * dim);
                let swm = water_refl_world_from_depth(pjm.uv, sdm);
                if water_refl_view_z(pm) - water_refl_view_z(swm) > 0.0 {
                    hi = mid;
                } else {
                    lo = mid;
                }
            }
            let pjf = water_refl_project(s.world_pos + s.r * hi);
            hit_uv  = select(proj.uv, pjf.uv, pjf.valid);
            hit     = true;
            break;
        }
        prev_t = t;
        t = t + WATER_SSR_STEP;
    }

    var reflected: vec3<f32>;
    if hit {
        reflected = textureSampleLevel(t_water_scene, s_water_scene, hit_uv, 0.0).rgb;
    } else {
        // ミス：まず「画面内に写っている空」、無ければ GI プローブ／環境光。
        let sky = water_refl_screen_sky(s.world_pos, s.r);
        if sky.valid {
            reflected = sky.color;
        } else {
            reflected = water_refl_env(s.world_pos, s.r);
        }
    }

    // A に強度を入れる（規約は RT 変種と同一）。
    return vec4<f32>(reflected, s.intensity);
}
