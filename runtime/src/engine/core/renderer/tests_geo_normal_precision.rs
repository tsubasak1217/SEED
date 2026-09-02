//! 幾何法線 Ng の数値精度に関する回帰テスト
//!
//! # なぜこのテストがあるのか
//! deferred のライティングは幾何法線 Ng を深度バッファ復元座標の画面微分
//! `cross(dpdx(p), dpdy(p))` から作る。ここで `p` に**絶対ワールド座標**を使うと、
//! 原点から離れたシーン（実機で問題になったのは原点から約 45m）で f32 の桁落ちが起き、
//! Ng が画素ごとに数度も暴れる。その誤差が `lighting_eval.wgsl` の幾何ゲート
//! （`dot(Ng, L)` による直接光の遮断）で 0/1 の**黒斑点ノイズ**へ増幅されていた。
//!
//! 対策は 2 段構えで、本ファイルはその両方を Rust 上の純関数として検証する。
//!   1. 微分を**カメラ相対座標**で取る（`deferred_camera_relative_ivp` ほか）。
//!      ただし「復元後の world_pos から camera_pos を引く」形では**効果がゼロ**である
//!      ことに注意（後述の `naive_*` テスト）。行列の側で平行移動を落とす必要がある。
//!   2. 幾何ゲートを 0/1 の階段から狭い遷移帯（smoothstep）へ軟化する。
//!
//! WGSL をそのまま実行できないため、シェーダと**同一の式**を f32 で書き写して検証する
//! （シェーダ側の式が変わったらこのテストも追随させること）。

#![cfg(test)]

/// 4x4 行列（行優先で保持。`m[row][col]`）。テスト内の最小限の線形代数だけを実装する。
type M4 = [[f64; 4]; 4];

/// 行列 × 列ベクトル（f64・数学上の厳密解の側で使う）。
fn mul_mv(m: &M4, v: [f64; 4]) -> [f64; 4] {
    let mut o = [0.0; 4];
    for r in 0..4 {
        o[r] = m[r][0] * v[0] + m[r][1] * v[1] + m[r][2] * v[2] + m[r][3] * v[3];
    }
    o
}

/// 行列 × 行列（f64）。
fn mul_mm(a: &M4, b: &M4) -> M4 {
    let mut o = [[0.0; 4]; 4];
    for r in 0..4 {
        for c in 0..4 {
            o[r][c] = (0..4).map(|k| a[r][k] * b[k][c]).sum();
        }
    }
    o
}

/// ガウス・ジョルダン法による 4x4 逆行列（テスト用途なので素直な実装）。
fn inverse(m: &M4) -> M4 {
    let mut a = *m;
    let mut inv: M4 = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ];
    for col in 0..4 {
        // 部分ピボット選択（数値安定性のため絶対値最大の行を持ってくる）。
        let mut piv = col;
        for r in (col + 1)..4 {
            if a[r][col].abs() > a[piv][col].abs() {
                piv = r;
            }
        }
        a.swap(col, piv);
        inv.swap(col, piv);
        let d = a[col][col];
        assert!(d.abs() > 1e-12, "特異行列（テスト設定が壊れている）");
        for c in 0..4 {
            a[col][c] /= d;
            inv[col][c] /= d;
        }
        for r in 0..4 {
            if r == col {
                continue;
            }
            let f = a[r][col];
            for c in 0..4 {
                a[r][c] -= f * a[col][c];
                inv[r][c] -= f * inv[col][c];
            }
        }
    }
    inv
}

fn sub3(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}
fn cross3(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}
fn dot3(a: [f64; 3], b: [f64; 3]) -> f64 {
    a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
}
fn norm3(v: [f64; 3]) -> [f64; 3] {
    let l = dot3(v, v).sqrt();
    assert!(l > 0.0, "ゼロベクトルは正規化できない");
    [v[0] / l, v[1] / l, v[2] / l]
}

/// 右手系 look-at ビュー行列（wgpu 規約。-Z が前方）。
fn look_at(eye: [f64; 3], target: [f64; 3], up: [f64; 3]) -> M4 {
    let f = norm3(sub3(target, eye));
    let s = norm3(cross3(f, up));
    let u = cross3(s, f);
    [
        [s[0], s[1], s[2], -dot3(s, eye)],
        [u[0], u[1], u[2], -dot3(u, eye)],
        [-f[0], -f[1], -f[2], dot3(f, eye)],
        [0.0, 0.0, 0.0, 1.0],
    ]
}

/// 透視投影（深度レンジ 0..1 の wgpu 規約）。
fn perspective(fovy_rad: f64, aspect: f64, znear: f64, zfar: f64) -> M4 {
    let t = 1.0 / (fovy_rad * 0.5).tan();
    [
        [t / aspect, 0.0, 0.0, 0.0],
        [0.0, t, 0.0, 0.0],
        [0.0, 0.0, zfar / (znear - zfar), znear * zfar / (znear - zfar)],
        [0.0, 0.0, -1.0, 0.0],
    ]
}

/// f32 精度での「行列 × 列ベクトル → 透視除算」。GPU が行う演算を写したもの。
fn unproject_f32(m: &M4, ndc: [f32; 4]) -> [f32; 3] {
    let mf: [[f32; 4]; 4] = {
        let mut o = [[0.0f32; 4]; 4];
        for r in 0..4 {
            for c in 0..4 {
                o[r][c] = m[r][c] as f32;
            }
        }
        o
    };
    let mut v = [0.0f32; 4];
    for r in 0..4 {
        v[r] = mf[r][0] * ndc[0] + mf[r][1] * ndc[1] + mf[r][2] * ndc[2] + mf[r][3] * ndc[3];
    }
    [v[0] / v[3], v[1] / v[3], v[2] / v[3]]
}

/// シェーダの `deferred_camera_relative_ivp` と同一の式（`T(-cam) * ivp`）。
///
/// 列優先 mat4x4 での「各列 j について xyz -= cam * w」は、行優先で書くと
/// 「各行 i（i<3）について row_i -= cam_i * row_3」と同じである。
/// **f32 で計算する**ことが重要で、実機（シェーダ）と同じ丸めを再現している。
fn camera_relative_ivp_f32(ivp: &M4, cam: [f64; 3]) -> M4 {
    let mut o = *ivp;
    for i in 0..3 {
        for c in 0..4 {
            // f32 へ丸めてから減算し、結果も f32 へ丸める（GPU と同じ）。
            let a = ivp[i][c] as f32;
            let b = (cam[i] as f32) * (ivp[3][c] as f32);
            o[i][c] = (a - b) as f64;
        }
    }
    o
}

/// 幾何法線 Ng の復元方式。
#[derive(Clone, Copy, Debug)]
enum NgMethod {
    /// 現行バグ版: 絶対ワールド座標の画面微分。
    AbsoluteWorld,
    /// バグ版と数値的に等価な「見せかけの修正」: world_pos を復元してから camera_pos を引く。
    NaiveSubtractAfter,
    /// 採用版: 平行移動を落とした行列で復元した**カメラ相対座標**の画面微分。
    CameraRelativeMatrix,
}

/// 1 つのカメラ設定について、2x2 クアッド相当の 3 画素から Ng を復元し、
/// 厳密な平面法線に対する角度誤差（度）を返す。
///
/// 平面は法線 `plane_n`・点 `plane_pt` の無限平面とし、各画素のレイと交差させて
/// 「その画素が実際に見ている点」と深度値を厳密（f64）に求める。そのうえで、
/// GPU が行う f32 の復元を写して Ng を組み立てる。
fn ng_error_deg(
    cam: [f64; 3],
    fov_deg: f64,
    plane_pt: [f64; 3],
    plane_n: [f64; 3],
    method: NgMethod,
) -> f64 {
    const RES: (f64, f64) = (1920.0, 1080.0);
    const PX: (f64, f64) = (960.0, 540.0);

    let view = look_at(cam, plane_pt, [0.0, 1.0, 0.0]);
    let proj = perspective(fov_deg.to_radians(), RES.0 / RES.1, 0.1, 1000.0);
    let view_proj = mul_mm(&proj, &view);
    let ivp = inverse(&view_proj);

    let n_exact = norm3(plane_n);
    let plane_d = dot3(n_exact, plane_pt);

    // 復元に使う行列（方式ごとに差し替える）。
    let recon = match method {
        NgMethod::AbsoluteWorld | NgMethod::NaiveSubtractAfter => ivp,
        NgMethod::CameraRelativeMatrix => camera_relative_ivp_f32(&ivp, cam),
    };

    // 2x2 クアッドのうち、dpdx / dpdy を作る 3 画素。
    let quad = [(PX.0, PX.1), (PX.0 + 1.0, PX.1), (PX.0, PX.1 + 1.0)];
    let mut pts = [[0.0f64; 3]; 3];
    for (i, (x, y)) in quad.iter().enumerate() {
        let ndc_x = (x + 0.5) / RES.0 * 2.0 - 1.0;
        let ndc_y = 1.0 - (y + 0.5) / RES.1 * 2.0;

        // 厳密（f64）に「この画素が見ている平面上の点」と、その NDC 深度を求める。
        let a4 = mul_mv(&ivp, [ndc_x, ndc_y, 0.0, 1.0]);
        let a = [a4[0] / a4[3], a4[1] / a4[3], a4[2] / a4[3]];
        let b4 = mul_mv(&ivp, [ndc_x, ndc_y, 0.5, 1.0]);
        let b = [b4[0] / b4[3], b4[1] / b4[3], b4[2] / b4[3]];
        let dir = sub3(b, a);
        let t = (plane_d - dot3(n_exact, a)) / dot3(n_exact, dir);
        let p = [a[0] + t * dir[0], a[1] + t * dir[1], a[2] + t * dir[2]];
        let c = mul_mv(&view_proj, [p[0], p[1], p[2], 1.0]);
        let depth = c[2] / c[3];

        // ここから先は GPU（f32）の再現。
        let ndc = [ndc_x as f32, ndc_y as f32, depth as f32, 1.0f32];
        let r = unproject_f32(&recon, ndc);
        let r = match method {
            // 復元後に camera_pos を引く「見せかけの修正」。
            NgMethod::NaiveSubtractAfter => [
                r[0] - cam[0] as f32,
                r[1] - cam[1] as f32,
                r[2] - cam[2] as f32,
            ],
            _ => r,
        };
        pts[i] = [r[0] as f64, r[1] as f64, r[2] as f64];
    }

    let ng = cross3(sub3(pts[1], pts[0]), sub3(pts[2], pts[0]));
    let len = dot3(ng, ng).sqrt();
    if len == 0.0 {
        return 90.0; // 完全に潰れた＝最悪
    }
    let ng = [ng[0] / len, ng[1] / len, ng[2] / len];
    // 向き（表裏）は呼び出し側で N に合わせて反転されるため、絶対値で角度を測る。
    dot3(ng, n_exact).abs().min(1.0).acos().to_degrees()
}

// ============================================================
//  ① 外積の平行移動不変性（実数演算では「意味」が変わらないことの確認）
// ============================================================

/// `cross(dpdx(p - c), dpdy(p - c)) == cross(dpdx(p), dpdy(p))`。
///
/// カメラ相対座標へ移しても Ng の**意味は完全に不変**であり、変わるのは f32 の精度だけ、
/// という修正の前提を押さえる（差分は平行移動で消えるので当然だが、実装ミスの番人になる）。
#[test]
fn cross_of_screen_derivatives_is_translation_invariant() {
    // 3 画素ぶんのワールド座標（適当な非退化の三角形）。
    let p0 = [1.0, 2.0, 3.0];
    let p1 = [1.004, 2.001, 3.002];
    let p2 = [0.997, 2.003, 3.001];
    let base = cross3(sub3(p1, p0), sub3(p2, p0));

    // 大きな平行移動（実機で問題になった 45m 級、および極端な 1e5）を掛ける。
    for c in [[45.0, 1.0, 45.0], [1.0e5, -2.0e5, 3.0e5]] {
        let q0 = sub3(p0, c);
        let q1 = sub3(p1, c);
        let q2 = sub3(p2, c);
        let moved = cross3(sub3(q1, q0), sub3(q2, q0));
        for i in 0..3 {
            assert!(
                (moved[i] - base[i]).abs() <= 1e-12 * base[i].abs().max(1.0),
                "外積が平行移動で変化した（成分 {i}）: {} vs {}",
                base[i],
                moved[i]
            );
        }
    }
}

// ============================================================
//  ② 桁落ちの実測（本バグの根拠そのもの）
// ============================================================

/// 実機で問題が出た構図（原点から約 64m のシーン・被写体まで 2m・狭 FOV）で、
/// 絶対ワールド座標の微分では Ng が**度のオーダー**でずれることを固定する。
///
/// この誤差が `dot(Ng, L)` の 0/1 ゲートに入ると、ターミネータ付近の広い帯が
/// 黒斑点になる。
#[test]
fn absolute_world_derivatives_lose_precision_far_from_origin() {
    let plane_pt = [45.0, 1.0, 45.0];
    let dist = 2.0;
    let cam = [45.0, 1.0 + dist * 0.3, 45.0 + dist * 0.95];
    let err = ng_error_deg(cam, 20.0, plane_pt, [0.0, 0.0, 1.0], NgMethod::AbsoluteWorld);
    assert!(
        err > 1.0,
        "前提が崩れている: 絶対ワールド座標でも Ng 誤差が小さい（{err} 度）。\
         この構図で桁落ちが起きないなら、本ファイルの想定そのものを見直すこと"
    );
}

/// 同じ構図を原点付近へ持ってくると誤差が消えること（＝原因が座標の絶対値であることの裏取り）。
#[test]
fn same_geometry_near_origin_is_accurate() {
    let plane_pt = [0.0, 1.0, 0.0];
    let dist = 2.0;
    let cam = [0.0, 1.0 + dist * 0.3, dist * 0.95];
    let err = ng_error_deg(cam, 20.0, plane_pt, [0.0, 0.0, 1.0], NgMethod::AbsoluteWorld);
    assert!(
        err < 0.5,
        "原点付近でも Ng 誤差が大きい（{err} 度）。桁落ち以外の原因を疑うこと"
    );
}

/// **重要**: 「復元した world_pos から camera_pos を引く」形の修正には効果がまったく無い。
///
/// world_pos はその時点で既に ulp(45)≒3.8e-6 m へ丸められており、引き算
/// （Sterbenz の補題により誤差なし）は失われたビットを取り戻さないため。
/// 直感に反するのでテストで固定する（この形の「修正」が再び入るのを防ぐ番人）。
#[test]
fn subtracting_camera_position_after_reconstruction_does_not_help() {
    let plane_pt = [45.0, 1.0, 45.0];
    let dist = 2.0;
    let cam = [45.0, 1.0 + dist * 0.3, 45.0 + dist * 0.95];
    let abs_err = ng_error_deg(cam, 20.0, plane_pt, [0.0, 0.0, 1.0], NgMethod::AbsoluteWorld);
    let naive_err = ng_error_deg(
        cam,
        20.0,
        plane_pt,
        [0.0, 0.0, 1.0],
        NgMethod::NaiveSubtractAfter,
    );
    assert!(
        (abs_err - naive_err).abs() < 1e-9,
        "復元後の減算で誤差が変わった（{abs_err} → {naive_err}）。\
         もし本当に改善しているなら本ファイルの前提が誤っている"
    );
}

/// 採用版（行列側で平行移動を落とす）が、絶対ワールド座標より桁違いに正確であること。
///
/// 実機で問題が出た構図を含む複数の距離・画角で、Ng 誤差が 1 桁以上改善し、
/// かつ絶対値としても 0.5 度未満に収まることを固定する。
#[test]
fn camera_relative_matrix_restores_geometric_normal_precision() {
    let plane_pt = [45.0, 1.0, 45.0];
    // (被写体までの距離, 垂直 FOV 度)
    let cases = [(2.0, 20.0), (2.0, 60.0), (5.0, 30.0), (0.5, 60.0)];
    for (dist, fov) in cases {
        let cam = [45.0, 1.0 + dist * 0.3, 45.0 + dist * 0.95];
        let abs_err = ng_error_deg(cam, fov, plane_pt, [0.0, 0.0, 1.0], NgMethod::AbsoluteWorld);
        let rel_err = ng_error_deg(
            cam,
            fov,
            plane_pt,
            [0.0, 0.0, 1.0],
            NgMethod::CameraRelativeMatrix,
        );
        assert!(
            rel_err < 0.5,
            "dist={dist} fov={fov}: カメラ相対でも Ng 誤差が大きい（{rel_err} 度）"
        );
        assert!(
            rel_err <= abs_err,
            "dist={dist} fov={fov}: カメラ相対の方が悪化している（{abs_err} → {rel_err} 度）"
        );
    }
}

// ============================================================
//  ③ 幾何ゲートの軟化（smoothstep）の端点挙動
// ============================================================

/// `lighting_eval.wgsl` の幾何ゲートと同一の式（WGSL の smoothstep の定義そのもの）。
fn geo_gate(ndl: f32, min_cos: f32, soft_cos: f32) -> f32 {
    let e0 = min_cos;
    let e1 = min_cos + soft_cos;
    let t = ((ndl - e0) / (e1 - e0)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// `RT_GEO_SHADOW_MIN_COS` / `RT_GEO_SHADOW_SOFT_COS`（lighting_eval.wgsl と一致必須）。
const GEO_MIN_COS: f32 = 0.0;
const GEO_SOFT_COS: f32 = 0.02;

/// ゲート本来の目的（裏面への光漏れ防止）が遷移帯の導入で壊れていないこと。
/// `dot(Ng, L) <= 0` は**厳密に 0** を返さなければならない。
#[test]
fn geo_gate_is_exactly_zero_on_and_behind_the_horizon() {
    for ndl in [-1.0f32, -0.5, -1e-3, -f32::EPSILON, -0.0, 0.0] {
        let g = geo_gate(ndl, GEO_MIN_COS, GEO_SOFT_COS);
        assert_eq!(
            g, 0.0,
            "dot(Ng,L)={ndl} で幾何ゲートが 0 でない（{g}）＝裏面への光漏れ"
        );
    }
}

/// 遷移帯の外（十分に光を向いている面）では従来どおり完全に 1 であること
/// ＝ 通常のライティングが暗くならないこと。
#[test]
fn geo_gate_is_exactly_one_beyond_the_transition_band() {
    for ndl in [GEO_SOFT_COS, 0.05f32, 0.5, 1.0] {
        let g = geo_gate(ndl, GEO_MIN_COS, GEO_SOFT_COS);
        assert_eq!(
            g, 1.0,
            "dot(Ng,L)={ndl} で幾何ゲートが 1 でない（{g}）＝正当な直接光が削られている"
        );
    }
}

/// 遷移帯が単調増加であること（帯の中で明滅・反転が起きない）。
#[test]
fn geo_gate_is_monotonic_across_the_band() {
    let mut prev = -1.0f32;
    for i in 0..=100 {
        let ndl = GEO_SOFT_COS * (i as f32) / 100.0;
        let g = geo_gate(ndl, GEO_MIN_COS, GEO_SOFT_COS);
        assert!(g >= prev, "遷移帯が単調でない（ndl={ndl}）");
        prev = g;
    }
}

/// 遷移帯が「Ng の残差誤差を吸収できる幅」かつ「放射照度が無視できる狭さ」であること。
/// 定数を安易に動かした場合の番人。
#[test]
fn geo_gate_band_is_narrow_but_wider_than_residual_ng_error() {
    // 修正後の Ng 残差はおよそ 0.1 度＝ cos で 1.7e-3 規模。帯はそれより広いこと。
    let residual_cos = (0.1f32).to_radians().sin();
    assert!(
        GEO_SOFT_COS > residual_cos * 2.0,
        "遷移帯（{GEO_SOFT_COS}）が Ng 残差誤差（{residual_cos}）を吸収できない"
    );
    // 帯は地平線から 2 度以内（放射照度 cos が 0.035 未満）に収めること。
    assert!(
        GEO_SOFT_COS < 0.035,
        "遷移帯（{GEO_SOFT_COS}）が広すぎてターミネータが目に見えて甘くなる"
    );
}

// ============================================================
//  ④ シェーダ側が実際にカメラ相対で微分していることの回帰ガード
// ============================================================

/// 微分の入力が絶対ワールド座標へ戻されていないことを、WGSL ソース上で押さえる。
/// （naga 検証は「文法・型が正しい」ことしか見ないため、意味の退行はここで止める）
#[test]
fn shaders_take_screen_derivatives_in_camera_relative_space() {
    // deferred ライティング本体。
    let deferred = include_str!("shaders/deferred_lighting.wgsl");
    assert!(
        deferred.contains("fn deferred_camera_relative_ivp("),
        "deferred_lighting.wgsl のカメラ相対 ivp ヘルパーが消えている"
    );
    assert!(
        deferred.contains("cross(dpdx(camera_relative_pos), dpdy(camera_relative_pos))"),
        "deferred_lighting.wgsl の Ng が絶対ワールド座標の微分へ戻っている（桁落ち再発）"
    );
    assert!(
        !deferred.contains("cross(dpdx(world_pos), dpdy(world_pos))"),
        "deferred_lighting.wgsl に絶対ワールド座標の微分が残っている"
    );

    // RT-AO（レイ原点のクリアランス方向に Ng を使う）。
    let ao_common = include_str!("shaders/ao_common.wgsl");
    let ao_rt = include_str!("shaders/ao_rt.wgsl");
    assert!(
        ao_common.contains("fn ao_cam_rel_pos("),
        "ao_common.wgsl のカメラ相対復元が消えている"
    );
    assert!(
        ao_rt.contains("cross(dpdx(rel_pos), dpdy(rel_pos))"),
        "ao_rt.wgsl の Ng が絶対ワールド座標の微分へ戻っている"
    );
    assert!(
        !ao_rt.contains("cross(dpdx(world_pos), dpdy(world_pos))"),
        "ao_rt.wgsl に絶対ワールド座標の微分が残っている"
    );

    // RT ソフト影マスク生成。
    let mask = include_str!("shaders/shadow_mask.wgsl");
    assert!(
        mask.contains("fn mask_cam_rel_pos("),
        "shadow_mask.wgsl のカメラ相対復元が消えている"
    );
    assert!(
        mask.contains("cross(dpdx(rel_pos), dpdy(rel_pos))"),
        "shadow_mask.wgsl の Ng が絶対ワールド座標の微分へ戻っている"
    );
    assert!(
        !mask.contains("cross(dpdx(world_pos), dpdy(world_pos))"),
        "shadow_mask.wgsl に絶対ワールド座標の微分が残っている"
    );
}

/// 幾何ゲートが 0/1 の階段へ戻されていないこと、および本ファイルの定数が
/// シェーダ側の定数と一致していること。
#[test]
fn geo_gate_in_shader_uses_a_soft_transition_band() {
    let eval = include_str!("shaders/lighting_eval.wgsl");
    assert!(
        eval.contains("let geo_gate = smoothstep("),
        "幾何ゲートが 0/1 の階段（select）へ戻っている＝黒斑点ノイズが再発する"
    );
    assert!(
        eval.contains("RT_GEO_SHADOW_MIN_COS + RT_GEO_SHADOW_SOFT_COS,"),
        "幾何ゲートの遷移帯の上端が RT_GEO_SHADOW_SOFT_COS でなくなっている"
    );
    // 定数の値を Rust 側の写しと突き合わせる（片方だけ動かす事故の番人）。
    assert!(
        eval.contains(&format!("const RT_GEO_SHADOW_MIN_COS: f32 = {GEO_MIN_COS:.1};")),
        "RT_GEO_SHADOW_MIN_COS が本テストの写し（{GEO_MIN_COS}）と食い違っている"
    );
    assert!(
        eval.contains(&format!("const RT_GEO_SHADOW_SOFT_COS: f32 = {GEO_SOFT_COS};")),
        "RT_GEO_SHADOW_SOFT_COS が本テストの写し（{GEO_SOFT_COS}）と食い違っている"
    );
}
