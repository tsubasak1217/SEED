// ============================================================
//  placement/tests.rs — 配置パターン生成のユニットテスト
//
//  検証の柱:
//    1. 点数がパラメータどおりであること（円/直線=個数、グリッド=行×列×段）
//    2. 幾何が正しいこと（円は半径一定・角度範囲、グリッドは間隔と中心揃え、
//       直線は方向と間隔）
//    3. ランダム散布が最小間隔を必ず満たすこと・達成不能なら警告を返すこと
//    4. **決定性**（同じシード → 同じ出力 / 違うシード → 違う出力）
//    5. C# ミラーとの一致を固定する既知ベクタ
// ============================================================

use super::spec::{PlacementPattern, PlacementSpec};
use super::{generate_points, MAX_PLACEMENT_POINTS};

/// 浮動小数比較の許容誤差（三角関数の丸め差を吸収する）。
const EPS: f32 = 1.0e-3;

/// 指定パターンの既定 spec を作る（テストの前提を 1 か所に集約する）。
fn spec_for(pattern: PlacementPattern) -> PlacementSpec {
    PlacementSpec { pattern, ..Default::default() }
}

/// XZ 平面上の原点からの距離。
fn radius_xz(p: [f32; 3]) -> f32 {
    (p[0] * p[0] + p[2] * p[2]).sqrt()
}

// ─── 円形 ─────────────────────────────────────────────────────

/// 全周の円は「個数どおり・半径一定・等角間隔」であること。
#[test]
fn circle_full_has_uniform_radius_and_spacing() {
    let spec = PlacementSpec { count: 8, radius: 5.0, ..spec_for(PlacementPattern::Circle) };
    let r = generate_points(&spec);
    assert_eq!(r.points.len(), 8, "個数どおりに生成されること");
    assert!(r.warning.is_none());

    for p in &r.points {
        assert!((radius_xz(p.position) - 5.0).abs() < EPS, "半径が一定であること: {:?}", p.position);
        assert!(p.position[1].abs() < EPS, "平面パターンなので Y は 0");
    }
    // 全周は開始角と終了角が重ならない＝最初と最後が同じ点にならない。
    let first = r.points[0].position;
    let last  = r.points[7].position;
    assert!((first[0] - last[0]).abs() > EPS || (first[2] - last[2]).abs() > EPS,
            "全周では始点と終点が重複しないこと");
}

/// 円弧（角度範囲 < 360）は両端に点が来ること。
#[test]
fn circle_arc_places_points_at_both_ends() {
    let spec = PlacementSpec {
        count: 3, radius: 1.0, start_angle: 0.0, angle_span: 90.0,
        ..spec_for(PlacementPattern::Circle)
    };
    let r = generate_points(&spec);
    assert_eq!(r.points.len(), 3);
    // 0 度 → (1, 0, 0)、90 度 → (0, 0, 1)
    assert!((r.points[0].position[0] - 1.0).abs() < EPS, "始点は開始角 0 度");
    assert!(r.points[0].position[2].abs() < EPS);
    assert!(r.points[2].position[0].abs() < EPS, "終点は 90 度");
    assert!((r.points[2].position[2] - 1.0).abs() < EPS);
}

/// 「中心を向く」でヨーが中心方向を指すこと。
#[test]
fn circle_face_center_points_inward() {
    let spec = PlacementSpec {
        count: 4, radius: 2.0, face_center: true,
        ..spec_for(PlacementPattern::Circle)
    };
    let r = generate_points(&spec);
    for p in &r.points {
        // ヨー規約 yaw = atan2(dir.x, dir.z)。dir は点 → 中心（= -position を正規化）。
        let expected = (-p.position[0]).atan2(-p.position[2]).to_degrees();
        let diff = (p.rotation[1] - expected).abs();
        assert!(diff < EPS || (diff - 360.0).abs() < EPS,
                "中心向きヨー: 期待 {expected} / 実際 {}", p.rotation[1]);
    }
}

/// 個数 0 は空の結果（クラッシュも警告も無し）。
#[test]
fn circle_zero_count_yields_empty() {
    let spec = PlacementSpec { count: 0, ..spec_for(PlacementPattern::Circle) };
    let r = generate_points(&spec);
    assert!(r.points.is_empty());
    assert!(r.warning.is_none());
}

/// 個数 1 の円弧でも 0 除算せず 1 点だけ返すこと。
#[test]
fn circle_single_count_does_not_divide_by_zero() {
    let spec = PlacementSpec {
        count: 1, radius: 3.0, angle_span: 90.0, start_angle: 0.0,
        ..spec_for(PlacementPattern::Circle)
    };
    let r = generate_points(&spec);
    assert_eq!(r.points.len(), 1);
    assert!(r.points[0].position.iter().all(|v| v.is_finite()), "NaN/Inf を出さないこと");
    assert!((r.points[0].position[0] - 3.0).abs() < EPS, "開始角に置かれること");
}

// ─── グリッド ─────────────────────────────────────────────────

/// 行×列×段の点数と間隔が指定どおりであること。
#[test]
fn grid_count_and_spacing() {
    let spec = PlacementSpec {
        rows: 3, cols: 4, layers: 2,
        spacing_x: 2.0, spacing_z: 3.0, spacing_y: 5.0,
        anchor_x: 0.0, anchor_y: 0.0,
        ..spec_for(PlacementPattern::Grid)
    };
    let r = generate_points(&spec);
    assert_eq!(r.points.len(), 3 * 4 * 2, "行×列×段の点数");

    // 走査順は 段 → 行 → 列。先頭は原点、2 番目は X 方向へ 1 間隔。
    assert_eq!(r.points[0].position, [0.0, 0.0, 0.0]);
    assert!((r.points[1].position[0] - 2.0).abs() < EPS, "列方向の間隔 = spacing_x");
    assert!((r.points[4].position[2] - 3.0).abs() < EPS, "行方向の間隔 = spacing_z");
    assert!((r.points[12].position[1] - 5.0).abs() < EPS, "段方向の間隔 = spacing_y");
}

/// アンカー 0.5/0.5（中心揃え）でグリッドの重心が原点に来ること。
#[test]
fn grid_center_align_centers_on_origin() {
    let spec = PlacementSpec {
        rows: 3, cols: 3, layers: 1,
        spacing_x: 2.0, spacing_z: 2.0,
        anchor_x: 0.5, anchor_y: 0.5,
        ..spec_for(PlacementPattern::Grid)
    };
    let r = generate_points(&spec);
    let n = r.points.len() as f32;
    let cx: f32 = r.points.iter().map(|p| p.position[0]).sum::<f32>() / n;
    let cz: f32 = r.points.iter().map(|p| p.position[2]).sum::<f32>() / n;
    assert!(cx.abs() < EPS && cz.abs() < EPS, "重心が原点: ({cx}, {cz})");
    // 3×3・間隔 2 の中心揃えなら端は ±2。
    assert!((r.points[0].position[0] + 2.0).abs() < EPS);
    assert!((r.points[0].position[2] + 2.0).abs() < EPS);
}

/// 市松オフセットが奇数行だけを半間隔ずらすこと。
#[test]
fn grid_checker_offset_shifts_odd_rows() {
    let spec = PlacementSpec {
        rows: 2, cols: 2, layers: 1,
        spacing_x: 4.0, spacing_z: 4.0,
        anchor_x: 0.0, anchor_y: 0.0, checker_offset: true,
        ..spec_for(PlacementPattern::Grid)
    };
    let r = generate_points(&spec);
    assert!(r.points[0].position[0].abs() < EPS, "0 行目はずれない");
    assert!((r.points[2].position[0] - 2.0).abs() < EPS, "1 行目は半間隔（2.0）ずれる");
}

// ─── 直線 ─────────────────────────────────────────────────────

/// 方向 0 度は +Z 方向へ等間隔に並ぶこと（ヨー規約の確認を兼ねる）。
#[test]
fn line_along_positive_z_at_zero_angle() {
    let spec = PlacementSpec {
        count: 4, line_angle: 0.0, line_spacing: 2.5, anchor_x: 0.0,
        ..spec_for(PlacementPattern::Line)
    };
    let r = generate_points(&spec);
    assert_eq!(r.points.len(), 4);
    for (i, p) in r.points.iter().enumerate() {
        assert!(p.position[0].abs() < EPS, "0 度の直線は X が 0");
        assert!((p.position[2] - i as f32 * 2.5).abs() < EPS, "間隔どおりに並ぶ");
    }
}

/// 方向 90 度は +X 方向、かつアンカー 0.5 で線の中心が原点に来ること。
#[test]
fn line_center_align_and_direction() {
    let spec = PlacementSpec {
        count: 3, line_angle: 90.0, line_spacing: 2.0, anchor_x: 0.5,
        ..spec_for(PlacementPattern::Line)
    };
    let r = generate_points(&spec);
    assert!((r.points[0].position[0] + 2.0).abs() < EPS, "中心揃えで始点は -2");
    assert!(r.points[1].position[0].abs() < EPS, "中央の点が原点");
    assert!((r.points[2].position[0] - 2.0).abs() < EPS);
    for p in &r.points {
        assert!(p.position[2].abs() < EPS, "90 度の直線は Z が 0");
    }
}

/// 「進行方向を向く」で直線のヨーが方向角そのものになること。
#[test]
fn line_face_forward_uses_line_angle() {
    let spec = PlacementSpec {
        count: 3, line_angle: 45.0, face_forward: true,
        ..spec_for(PlacementPattern::Line)
    };
    let r = generate_points(&spec);
    for p in &r.points {
        assert!((p.rotation[1] - 45.0).abs() < EPS, "ヨー = 方向角");
    }
}

// ─── ランダム散布 ─────────────────────────────────────────────

/// 最小間隔が必ず守られること（1 組でも近ければ失敗）。
#[test]
fn random_respects_min_spacing() {
    let spec = PlacementSpec {
        count: 20, seed: 42,
        area_circle: true, area_radius: 10.0,
        min_spacing: 2.0,
        ..spec_for(PlacementPattern::Random)
    };
    let r = generate_points(&spec);
    assert!(!r.points.is_empty());
    for i in 0..r.points.len() {
        for j in (i + 1)..r.points.len() {
            let a = r.points[i].position;
            let b = r.points[j].position;
            let d = ((a[0] - b[0]).powi(2) + (a[2] - b[2]).powi(2)).sqrt();
            assert!(d >= 2.0 - EPS, "最小間隔違反: {d} < 2.0（{i} と {j}）");
        }
    }
}

/// 範囲（円）の外へ出ないこと。
#[test]
fn random_stays_inside_circle_area() {
    let spec = PlacementSpec {
        count: 50, seed: 7, area_circle: true, area_radius: 4.0,
        ..spec_for(PlacementPattern::Random)
    };
    let r = generate_points(&spec);
    assert_eq!(r.points.len(), 50, "最小間隔 0 なら要求数を必ず満たす");
    for p in &r.points {
        assert!(radius_xz(p.position) <= 4.0 + EPS, "円範囲外: {:?}", p.position);
    }
}

/// 範囲（矩形）の外へ出ないこと。
#[test]
fn random_stays_inside_rect_area() {
    let spec = PlacementSpec {
        count: 50, seed: 9, area_circle: false,
        area_size_x: 6.0, area_size_z: 4.0,
        ..spec_for(PlacementPattern::Random)
    };
    let r = generate_points(&spec);
    for p in &r.points {
        assert!(p.position[0].abs() <= 3.0 + EPS, "X が矩形外: {:?}", p.position);
        assert!(p.position[2].abs() <= 2.0 + EPS, "Z が矩形外: {:?}", p.position);
    }
}

/// 達成不能な最小間隔では**減らしたうえで警告**すること（黙って減らさない）。
#[test]
fn random_warns_when_min_spacing_unreachable() {
    let spec = PlacementSpec {
        count: 50, seed: 1,
        area_circle: true, area_radius: 1.0,
        min_spacing: 5.0, // 半径 1m の円に 5m 間隔は原理的に 1 点しか置けない
        ..spec_for(PlacementPattern::Random)
    };
    let r = generate_points(&spec);
    assert!(r.points.len() < 50, "置けない要求は減らされること");
    assert!(r.warning.is_some(), "減らしたことを必ず警告すること");
}

/// スケールばらつきが指定割合の範囲に収まること。
#[test]
fn random_scale_variance_within_range() {
    let spec = PlacementSpec {
        count: 40, seed: 3, scale_variance: 0.25,
        ..spec_for(PlacementPattern::Random)
    };
    let r = generate_points(&spec);
    for p in &r.points {
        assert!(p.scale[0] >= 0.75 - EPS && p.scale[0] <= 1.25 + EPS,
                "スケール範囲外: {}", p.scale[0]);
        assert_eq!(p.scale[0], p.scale[1], "均一スケールであること");
        assert_eq!(p.scale[1], p.scale[2]);
    }
}

// ─── 決定性 ───────────────────────────────────────────────────

/// 同じシードなら完全に同じ点列を返すこと（本機能の中核契約）。
#[test]
fn same_seed_produces_identical_points() {
    let spec = PlacementSpec {
        count: 30, seed: 20260901,
        min_spacing: 1.0, jitter_pos: 0.5, jitter_rot: 30.0,
        random_rotation: true, scale_variance: 0.3,
        ..spec_for(PlacementPattern::Random)
    };
    let a = generate_points(&spec);
    let b = generate_points(&spec);
    assert_eq!(a.points, b.points, "同一入力は同一出力であること");
}

/// シードを変えれば結果が変わること（シード指定が機能していること）。
#[test]
fn different_seed_produces_different_points() {
    let base = PlacementSpec { count: 20, seed: 1, ..spec_for(PlacementPattern::Random) };
    let other = PlacementSpec { seed: 2, ..base.clone() };
    assert_ne!(generate_points(&base).points, generate_points(&other).points);
}

/// ジッターは独立ストリーム: ジッター量を変えても**パターン本体**は動かないこと。
#[test]
fn jitter_amount_does_not_disturb_pattern_stream() {
    let a = PlacementSpec { count: 10, seed: 5, ..spec_for(PlacementPattern::Random) };
    let b = PlacementSpec { jitter_pos: 1.0, ..a.clone() };
    let pa = generate_points(&a).points;
    let pb = generate_points(&b).points;
    for (x, y) in pa.iter().zip(pb.iter()) {
        // ジッターは ±1.0 の範囲でしかずれない＝基の点が入れ替わっていない証拠。
        assert!((x.position[0] - y.position[0]).abs() <= 1.0 + EPS);
        assert!((x.position[2] - y.position[2]).abs() <= 1.0 + EPS);
    }
}

/// ジッター 0 なら位置・回転が一切動かないこと。
#[test]
fn zero_jitter_leaves_points_untouched() {
    let spec = PlacementSpec {
        count: 4, radius: 2.0, seed: 99, jitter_pos: 0.0, jitter_rot: 0.0,
        ..spec_for(PlacementPattern::Circle)
    };
    let r = generate_points(&spec);
    for p in &r.points {
        assert!((radius_xz(p.position) - 2.0).abs() < EPS, "ジッター 0 で半径が動かないこと");
        assert_eq!(p.rotation[1], 0.0);
    }
}

// ─── 上限 ─────────────────────────────────────────────────────

/// 上限を超える要求は切り詰めたうえで警告すること。
#[test]
fn exceeding_max_points_truncates_with_warning() {
    let spec = PlacementSpec {
        rows: 100, cols: 100, layers: 1, // 10,000 点 > 上限
        ..spec_for(PlacementPattern::Grid)
    };
    let r = generate_points(&spec);
    assert_eq!(r.points.len(), MAX_PLACEMENT_POINTS);
    assert!(r.warning.is_some(), "切り詰めたことを警告すること");
}

// ─── C# ミラーとの既知ベクタ ──────────────────────────────────

/// **エディタ（C#）プレビューとの一致を固定する既知ベクタ**。
///
/// `editor/tests/PlacementTests` が同じ spec に対して同じ値を要求する。
/// ここが変わったらプレビューと実生成がずれるので、両方を同時に直すこと。
#[test]
fn known_vector_circle_matches_csharp_mirror() {
    let spec = PlacementSpec {
        count: 4, radius: 10.0, start_angle: 0.0, angle_span: 360.0,
        ..spec_for(PlacementPattern::Circle)
    };
    let r = generate_points(&spec);
    let expected = [
        [10.0_f32, 0.0, 0.0],
        [0.0, 0.0, 10.0],
        [-10.0, 0.0, 0.0],
        [0.0, 0.0, -10.0],
    ];
    for (p, e) in r.points.iter().zip(expected.iter()) {
        for k in 0..3 {
            assert!((p.position[k] - e[k]).abs() < EPS,
                    "既知ベクタ不一致: {:?} vs {:?}", p.position, e);
        }
    }
}

/// ランダム散布の既知ベクタ（乱数ストリームの消費順まで固定する）。
#[test]
fn known_vector_random_matches_csharp_mirror() {
    let spec = PlacementSpec {
        count: 3, seed: 1,
        area_circle: false, area_size_x: 10.0, area_size_z: 10.0,
        min_spacing: 0.0,
        ..spec_for(PlacementPattern::Random)
    };
    let r = generate_points(&spec);
    assert_eq!(r.points.len(), 3);
    // 期待値は splitmix64(seed=1) の (u, v) から矩形へ写した値。
    // C# 側テストが同じ数値を持つ（相互一致の固定点）。
    let expected = [
        [ 0.6656152_f32, 0.0, 2.4578172],
        [-0.5573535,     0.0, 2.6289433],
        [-2.1449137,     0.0, 2.9399657],
    ];
    for (p, e) in r.points.iter().zip(expected.iter()) {
        for k in 0..3 {
            assert!((p.position[k] - e[k]).abs() < EPS,
                    "既知ベクタ不一致: {:?} vs {:?}", p.position, e);
        }
    }
}

// ─── 基準位置アンカー ─────────────────────────────────────────
//
// アンカーは「パターンのどの位置を基準点（＝カーソル位置）に合わせるか」を
// 0..1 で指定する。(0,0) が -X/-Z 側の角、(1,1) が +X/+Z 側の角、
// (0.5,0.5) が中心。**C# ミラーと同じ既知ベクタ**をここで固定する。

/// アンカーのテストに使う 3×3・間隔 2 のグリッド spec（幅は X/Z とも 4）。
fn anchor_grid_spec(ax: f32, ay: f32) -> PlacementSpec {
    PlacementSpec {
        rows: 3, cols: 3, layers: 1,
        spacing_x: 2.0, spacing_z: 2.0,
        anchor_x: ax, anchor_y: ay,
        ..spec_for(PlacementPattern::Grid)
    }
}

/// グリッドの XZ 範囲（min_x, max_x, min_z, max_z）を返す。
fn grid_bounds(points: &[super::spec::PlacementPoint]) -> (f32, f32, f32, f32) {
    let mut b = (f32::MAX, f32::MIN, f32::MAX, f32::MIN);
    for p in points {
        b.0 = b.0.min(p.position[0]);
        b.1 = b.1.max(p.position[0]);
        b.2 = b.2.min(p.position[2]);
        b.3 = b.3.max(p.position[2]);
    }
    b
}

/// **アンカー (0,0)**: グリッドの -X/-Z 側の角（2D の左上）が基準点に一致すること。
#[test]
fn anchor_zero_puts_min_corner_on_origin() {
    let r = generate_points(&anchor_grid_spec(0.0, 0.0));
    let (min_x, max_x, min_z, max_z) = grid_bounds(&r.points);
    assert!(min_x.abs() < EPS, "-X 側の辺が基準点: {min_x}");
    assert!(min_z.abs() < EPS, "-Z 側の辺が基準点: {min_z}");
    assert!((max_x - 4.0).abs() < EPS && (max_z - 4.0).abs() < EPS, "幅は (n-1)*spacing = 4");
}

/// **アンカー (1,1)**: グリッドの +X/+Z 側の角（2D の右下）が基準点に一致すること。
#[test]
fn anchor_one_puts_max_corner_on_origin() {
    let r = generate_points(&anchor_grid_spec(1.0, 1.0));
    let (min_x, max_x, min_z, max_z) = grid_bounds(&r.points);
    assert!(max_x.abs() < EPS, "+X 側の辺が基準点: {max_x}");
    assert!(max_z.abs() < EPS, "+Z 側の辺が基準点: {max_z}");
    assert!((min_x + 4.0).abs() < EPS && (min_z + 4.0).abs() < EPS, "反対の辺は -4");
}

/// **アンカー (0.5,0.5)**: 中心が基準点に一致すること（旧「中心揃え」と同じ）。
#[test]
fn anchor_half_centers_the_grid() {
    let r = generate_points(&anchor_grid_spec(0.5, 0.5));
    let (min_x, max_x, min_z, max_z) = grid_bounds(&r.points);
    assert!((min_x + 2.0).abs() < EPS && (max_x - 2.0).abs() < EPS, "X は ±2");
    assert!((min_z + 2.0).abs() < EPS && (max_z - 2.0).abs() < EPS, "Z は ±2");
}

/// アンカーは軸ごとに独立して効くこと（X だけ +X 側・Z は -Z 側、など）。
#[test]
fn anchor_axes_are_independent() {
    let r = generate_points(&anchor_grid_spec(1.0, 0.0));
    let (min_x, max_x, min_z, max_z) = grid_bounds(&r.points);
    assert!(max_x.abs() < EPS && (min_x + 4.0).abs() < EPS, "X は +X 側が基準点");
    assert!(min_z.abs() < EPS && (max_z - 4.0).abs() < EPS, "Z は -Z 側が基準点");
}

/// 範囲外・NaN のアンカーは 0..1 へ丸められること（パターンが吹き飛ばないこと）。
#[test]
fn anchor_is_clamped_to_unit_range() {
    let r_low  = generate_points(&anchor_grid_spec(-5.0, -5.0));
    let r_zero = generate_points(&anchor_grid_spec(0.0, 0.0));
    assert_eq!(r_low.points, r_zero.points, "負値は 0 に丸められること");

    let r_high = generate_points(&anchor_grid_spec(9.0, 9.0));
    let r_one  = generate_points(&anchor_grid_spec(1.0, 1.0));
    assert_eq!(r_high.points, r_one.points, "1 超は 1 に丸められること");

    let r_nan  = generate_points(&anchor_grid_spec(f32::NAN, f32::NAN));
    let r_half = generate_points(&anchor_grid_spec(0.5, 0.5));
    assert_eq!(r_nan.points, r_half.points, "NaN は中心揃えへ倒すこと");
}

/// **段（Y）はアンカーの影響を受けず、常に基準 Y から上へ積む**こと。
///
/// 旧「中心揃え」は段も中央寄せしていたので、これは意図した挙動変更である。
#[test]
fn anchor_does_not_affect_layers() {
    for anchor in [0.0_f32, 0.5, 1.0] {
        let spec = PlacementSpec {
            rows: 1, cols: 1, layers: 3, spacing_y: 4.0,
            anchor_x: anchor, anchor_y: anchor,
            ..spec_for(PlacementPattern::Grid)
        };
        let r = generate_points(&spec);
        let ys: Vec<f32> = r.points.iter().map(|p| p.position[1]).collect();
        assert!((ys[0]).abs() < EPS,       "最下段は基準 Y: {ys:?}");
        assert!((ys[1] - 4.0).abs() < EPS, "上へ 1 段: {ys:?}");
        assert!((ys[2] - 8.0).abs() < EPS, "上へ 2 段: {ys:?}");
    }
}

/// 直線は `anchor_x` を「線に沿ったアンカー」として使うこと。
#[test]
fn anchor_x_slides_the_line_along_its_direction() {
    let line = |a: f32| PlacementSpec {
        count: 3, line_angle: 90.0, line_spacing: 2.0, anchor_x: a,
        ..spec_for(PlacementPattern::Line)
    };
    // 0 = 始点が基準点
    let r0 = generate_points(&line(0.0));
    assert!(r0.points[0].position[0].abs() < EPS && (r0.points[2].position[0] - 4.0).abs() < EPS);
    // 0.5 = 線の中心が基準点
    let rh = generate_points(&line(0.5));
    assert!((rh.points[0].position[0] + 2.0).abs() < EPS && rh.points[1].position[0].abs() < EPS);
    // 1 = 終点が基準点
    let r1 = generate_points(&line(1.0));
    assert!((r1.points[0].position[0] + 4.0).abs() < EPS && r1.points[2].position[0].abs() < EPS);
}

/// **[C# 一致] アンカーの既知ベクタ**。
///
/// `editor/tests/PlacementTests` が同じ spec に対して同じ値を要求する。
/// 2×2・間隔 3（幅 3）のグリッドをアンカー (0.25, 0.75) で置いたときの点列。
#[test]
fn known_vector_anchor_matches_csharp_mirror() {
    let spec = PlacementSpec {
        rows: 2, cols: 2, layers: 1,
        spacing_x: 3.0, spacing_z: 3.0,
        anchor_x: 0.25, anchor_y: 0.75,
        ..spec_for(PlacementPattern::Grid)
    };
    let r = generate_points(&spec);
    // オフセット: X = 3*0.25 = 0.75 / Z = 3*0.75 = 2.25
    let expected = [
        [-0.75_f32, 0.0, -2.25],
        [ 2.25,     0.0, -2.25],
        [-0.75,     0.0,  0.75],
        [ 2.25,     0.0,  0.75],
    ];
    assert_eq!(r.points.len(), expected.len());
    for (p, e) in r.points.iter().zip(expected.iter()) {
        for k in 0..3 {
            assert!((p.position[k] - e[k]).abs() < EPS,
                    "既知ベクタ不一致: {:?} vs {:?}", p.position, e);
        }
    }
}
