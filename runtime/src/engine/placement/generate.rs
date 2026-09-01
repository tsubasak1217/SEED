// ============================================================
//  placement/generate.rs — 配置パターンの点列生成（純関数）
//
//  【責務】
//  `PlacementSpec` → `PlacementResult` の変換だけを行う。ECS・地形・IPC・
//  ファイル IO への依存を一切持たない（＝そのままユニットテストできる）。
//
//  【決定性の契約】
//  同じ `PlacementSpec`（シード込み）を与えれば、いつ・何度呼んでも
//  ビット単位で同じ点列を返す。これが崩れると
//    ・エディタのプレビューと実生成がずれる
//    ・「同じシードでやり直す」が成立しない
//  ため、以下を厳守する:
//    1. 乱数は `PlacementRng`（splitmix64）のみ。`rand` も時刻も使わない
//    2. **乱数の消費数を分岐で変えない**。「ジッター 0 なら引かない」のような
//       最適化は禁止（設定値でストリームがずれる）
//    3. 走査順は固定（段 y → 行 z → 列 x）
//
//  【C# ミラーとの関係】
//  `editor/src/Placement/Patterns/PlacementGenerator.cs` が本ファイルの写しで、
//  ダイアログのプレビューはそちらを使う。両者の一致は Rust 側テストと
//  `editor/tests/PlacementTests` が**同じ既知入力の期待値**で固定する。
//  三角関数はプラットフォーム・言語で最終 1ulp が異なりうるため、
//  一致検証は微小許容誤差付きで行う（点の個数・順序・構造は完全一致）。
// ============================================================

use super::rng::PlacementRng;
use super::spec::{PlacementPattern, PlacementPoint, PlacementResult, PlacementSpec};

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// 1 回の配置で生成できる点数の上限。
///
/// 上限が無いと、行×列×段や個数へ極端な値を入れられたときに
/// アクタ生成（ECS エンティティ・ヒエラルキー行・GPU バッファ）が
/// 実質フリーズする。エディタ側の入力上限と一致させること。
pub const MAX_PLACEMENT_POINTS: usize = 4096;

/// ランダム散布の 1 点あたり試行回数の上限。
///
/// 最小間隔が厳しいほど棄却が増える。「要求個数 × この回数」試して
/// 埋まらなければ諦めて警告を返す（無限ループにしない）。
const RANDOM_ATTEMPTS_PER_POINT: usize = 32;

/// 「全周」とみなす角度範囲のしきい値 [度]。
///
/// 全周のときは開始角と終了角が同じ点になるため、分母を `count`（開き）に
/// する。円弧のときは両端に点を置きたいので分母を `count - 1`（閉じ）にする。
const FULL_CIRCLE_DEGREES: f32 = 360.0;

/// 全周判定の許容誤差 [度]（UI から 359.999… が来た場合を全周として扱う）。
const FULL_CIRCLE_EPSILON: f32 = 1.0e-3;

/// ジッター用 RNG のシードソルト。
///
/// パターン本体の乱数（ランダム散布）とジッターで**同じストリームを共有しない**
/// ようにする。共有すると「ジッター量を変えただけで散布位置まで変わる」という
/// 直感に反する挙動になる。奇数を XOR するだけで独立したストリームになる。
const JITTER_SEED_SALT: u64 = 0x5EED_10C1_C0DE_1111;

/// 度 → ラジアン。
#[inline]
fn to_radians(deg: f32) -> f32 { deg * std::f32::consts::PI / 180.0 }

/// ラジアン → 度。
#[inline]
fn to_degrees(rad: f32) -> f32 { rad * 180.0 / std::f32::consts::PI }

/// 方向ベクトル（XZ 成分）からヨー角 [度] を求める。
///
/// 規約は `yaw = atan2(dir.x, dir.z)`（ヨー 0 で +Z を向く）。
/// ゼロベクトルには 0 を返す（`atan2(0,0)` の実装差を避けるため明示的に分岐する）。
#[inline]
fn yaw_from_dir(dx: f32, dz: f32) -> f32 {
    if dx == 0.0 && dz == 0.0 { return 0.0; }
    to_degrees(dx.atan2(dz))
}

// ============================================================
//  入口
// ============================================================

/// 配置指定から点列を生成する。
///
/// 戻り値の点は**基準点を原点とするローカル座標**であり、
/// ワールドへの移動（基準点の加算）と地形接地は呼び出し側の責務。
pub fn generate_points(spec: &PlacementSpec) -> PlacementResult {
    let mut result = match spec.pattern {
        PlacementPattern::Circle => generate_circle(spec),
        PlacementPattern::Grid   => generate_grid(spec),
        PlacementPattern::Line   => generate_line(spec),
        PlacementPattern::Random => generate_random(spec),
    };

    // ── 上限で切り詰める（黙って切らずに警告へ載せる）──
    if result.points.len() > MAX_PLACEMENT_POINTS {
        let dropped = result.points.len() - MAX_PLACEMENT_POINTS;
        result.points.truncate(MAX_PLACEMENT_POINTS);
        result.warning = Some(format!(
            "生成点が上限（{MAX_PLACEMENT_POINTS} 点）を超えたため {dropped} 点を切り詰めました"
        ));
    }

    // ── 「進行方向を向く」の後処理（円形・直線はパターン側で決めるので対象外）──
    apply_face_forward(spec, &mut result.points);

    // ── ジッター（位置・回転）を独立ストリームで適用する ──
    apply_jitter(spec, &mut result.points);

    result
}

// ============================================================
//  パターン実装
// ============================================================

/// 円形・円弧。
///
/// 全周（`angle_span` が 360 度相当）のときは開始角と終了角が重なるため
/// 分母を `count` にして重複点を作らない。円弧のときは両端に点を置く。
fn generate_circle(spec: &PlacementSpec) -> PlacementResult {
    let count = spec.count as usize;
    let mut points = Vec::with_capacity(count);
    if count == 0 { return PlacementResult { points, warning: None }; }

    let full = (spec.angle_span.abs() - FULL_CIRCLE_DEGREES).abs() <= FULL_CIRCLE_EPSILON;
    // 分母 0 を避ける（count == 1 の円弧は開始角に 1 点だけ置く）。
    let denom = if full { count } else { count.max(2) - 1 } as f32;

    for i in 0..count {
        let deg = spec.start_angle + spec.angle_span * (i as f32) / denom;
        let rad = to_radians(deg);
        let (sin, cos) = (rad.sin(), rad.cos());
        // 角度 0 が +X、反時計回り（+Z 方向へ回る）。
        let x = spec.radius * cos;
        let z = spec.radius * sin;

        // 向き: 中心向きが最優先、次に接線（進行方向）、どちらも無ければ 0。
        let yaw = if spec.face_center {
            yaw_from_dir(-cos, -sin)
        } else if spec.face_forward {
            // 反時計回りの接線 = (-sin, cos)
            yaw_from_dir(-sin, cos)
        } else {
            0.0
        };

        points.push(PlacementPoint {
            position: [x, 0.0, z],
            rotation: [0.0, yaw, 0.0],
            ..Default::default()
        });
    }
    PlacementResult { points, warning: None }
}

/// グリッド（行 × 列 × 段）。
///
/// 走査順は **段 y → 行 z → 列 x** で固定する（この順が生成アクタの
/// 連番 `_01, _02, …` の順序になる）。
///
/// 平面上のどこを基準点に合わせるかは `anchor_x` / `anchor_y`（各 0..1）で決まる。
/// 段（Y）はアンカーの対象外で、常に基準 Y から上へ積む。
fn generate_grid(spec: &PlacementSpec) -> PlacementResult {
    let cols   = spec.cols.max(1) as usize;
    let rows   = spec.rows.max(1) as usize;
    let layers = spec.layers.max(1) as usize;

    // ── 基準位置アンカーのオフセット ──────────────────────────
    // 全体の広がり（(n-1)*spacing）に対しアンカー比を掛けたぶんだけ手前へ寄せる。
    //   アンカー 0   → オフセット 0        … 手前側の辺（-X / -Z）が基準点に一致
    //   アンカー 0.5 → オフセット 幅/2     … 中心が基準点に一致（旧「中心揃え」）
    //   アンカー 1   → オフセット 幅       … 奥側の辺（+X / +Z）が基準点に一致
    let anchor_offset = |n: usize, spacing: f32, anchor: f32| -> f32 {
        (n as f32 - 1.0) * spacing * anchor
    };
    let off_x = anchor_offset(cols, spec.spacing_x, spec.clamped_anchor_x());
    let off_z = anchor_offset(rows, spec.spacing_z, spec.clamped_anchor_y());
    // 段（Y）はアンカーの対象外。**基準 Y から上へ積む**（地面に置いた山を
    // 積み上げる直感に合わせる）。アンカーは「平面上のどこを基準点に合わせるか」
    // という 2 次元の概念なので、高さ方向へは持ち込まない。
    let off_y = 0.0f32;

    let mut points = Vec::with_capacity(cols * rows * layers);
    for ly in 0..layers {
        for r in 0..rows {
            // 市松オフセット: 奇数行だけ X 方向へ半間隔ずらす。
            let checker = if spec.checker_offset && r % 2 == 1 { spec.spacing_x * 0.5 } else { 0.0 };
            for c in 0..cols {
                points.push(PlacementPoint {
                    position: [
                        c as f32 * spec.spacing_x - off_x + checker,
                        ly as f32 * spec.spacing_y - off_y,
                        r as f32 * spec.spacing_z - off_z,
                    ],
                    ..Default::default()
                });
            }
        }
    }
    PlacementResult { points, warning: None }
}

/// 直線（方向角 + 間隔 × 個数）。
///
/// `anchor_x` が線に沿ったアンカー（0 = 始点 / 0.5 = 中心 / 1 = 終点が基準点）。
fn generate_line(spec: &PlacementSpec) -> PlacementResult {
    let count = spec.count as usize;
    let mut points = Vec::with_capacity(count);
    if count == 0 { return PlacementResult { points, warning: None }; }

    let rad = to_radians(spec.line_angle);
    // ヨー規約 `yaw = atan2(x, z)` に合わせ、方向ベクトルは (sin, cos)。
    let (dx, dz) = (rad.sin(), rad.cos());
    // 線に沿ったアンカー（0 = 始点が基準点 / 0.5 = 線の中心 / 1 = 終点が基準点）。
    // 直線は 1 次元なので `anchor_x` だけを使う（`anchor_y` は意味を持たない）。
    let start = -(count as f32 - 1.0) * spec.line_spacing * spec.clamped_anchor_x();
    // 直線は「点の並ぶ向き」が自明なので、進行方向指定はここで確定させる
    // （apply_face_forward の一般則より正確：間隔 0 でも向きが定まる）。
    let yaw = if spec.face_forward { spec.line_angle } else { 0.0 };

    for i in 0..count {
        let t = start + i as f32 * spec.line_spacing;
        points.push(PlacementPoint {
            position: [dx * t, 0.0, dz * t],
            rotation: [0.0, yaw, 0.0],
            ..Default::default()
        });
    }
    PlacementResult { points, warning: None }
}

/// ランダム散布（拒否サンプリングによる最小間隔保証）。
///
/// 【乱数の消費順（C# ミラーと厳密に一致させること）】
/// 1 回の試行につき必ず `u`, `v` の 2 個を引く。候補が採用されたときだけ
/// さらに `rot`, `scale` の 2 個を引く（フラグの有無に関わらず必ず引く）。
fn generate_random(spec: &PlacementSpec) -> PlacementResult {
    let count = spec.count as usize;
    let mut points: Vec<PlacementPoint> = Vec::with_capacity(count);
    if count == 0 { return PlacementResult { points, warning: None }; }

    let mut rng = PlacementRng::new(spec.seed);
    let min_sq = spec.min_spacing.max(0.0) * spec.min_spacing.max(0.0);
    let max_attempts = count * RANDOM_ATTEMPTS_PER_POINT;

    for _ in 0..max_attempts {
        if points.len() >= count { break; }

        let u = rng.next_f32();
        let v = rng.next_f32();

        // ── 候補位置（円 = 面積一様になるよう半径に sqrt を掛ける）──
        let (x, z) = if spec.area_circle {
            let r = spec.area_radius * u.sqrt();
            let a = v * std::f32::consts::TAU;
            (r * a.cos(), r * a.sin())
        } else {
            ((u - 0.5) * spec.area_size_x, (v - 0.5) * spec.area_size_z)
        };

        // ── 最小間隔チェック（XZ 距離。既に採用済みの点すべてと比較）──
        if min_sq > 0.0 {
            let too_close = points.iter().any(|p| {
                let dx = p.position[0] - x;
                let dz = p.position[2] - z;
                dx * dx + dz * dz < min_sq
            });
            if too_close { continue; }
        }

        // ── 採用。回転・スケールの乱数は**フラグに関わらず必ず引く**（決定性）──
        let rot_r = rng.next_f32();
        let scl_r = rng.next_f32();
        let yaw = if spec.random_rotation { rot_r * FULL_CIRCLE_DEGREES } else { 0.0 };
        let s = if spec.scale_variance != 0.0 {
            (1.0 + (scl_r * 2.0 - 1.0) * spec.scale_variance).max(0.0)
        } else {
            1.0
        };

        points.push(PlacementPoint {
            position: [x, 0.0, z],
            rotation: [0.0, yaw, 0.0],
            scale:    [s, s, s],
        });
    }

    // ── 埋まらなかった場合は減らした事実を必ず伝える ──
    let warning = (points.len() < count).then(|| format!(
        "最小間隔 {:.2}m では {} 個を配置できませんでした（{} 個で打ち切り）。\
         範囲を広げるか最小間隔を小さくしてください",
        spec.min_spacing, count, points.len()
    ));

    PlacementResult { points, warning }
}

// ============================================================
//  後処理
// ============================================================

/// 「進行方向を向く」をグリッド・ランダムへ適用する。
///
/// 円形・直線はパターン側で向きを確定済みなので触らない
/// （接線・線方向のほうが「進行方向」として正確なため）。
/// ここでは「1 つ前の点から自分へ向かうベクトル」を進行方向とし、
/// 先頭の点は次の点の向きを借りる（先頭だけ 0 度になるのを避ける）。
fn apply_face_forward(spec: &PlacementSpec, points: &mut [PlacementPoint]) {
    if !spec.face_forward { return; }
    if !matches!(spec.pattern, PlacementPattern::Grid | PlacementPattern::Random) { return; }
    if points.len() < 2 { return; }

    // 先に全区間のヨーを求めてから書き戻す（前の点の書き換えに引きずられないため）。
    let yaws: Vec<f32> = (0..points.len())
        .map(|i| {
            // i == 0 は「0 → 1」の向き、それ以外は「i-1 → i」の向き。
            let (a, b) = if i == 0 { (0, 1) } else { (i - 1, i) };
            let dx = points[b].position[0] - points[a].position[0];
            let dz = points[b].position[2] - points[a].position[2];
            yaw_from_dir(dx, dz)
        })
        .collect();
    for (p, yaw) in points.iter_mut().zip(yaws) {
        p.rotation[1] = yaw;
    }
}

/// 位置・回転のジッターを適用する。
///
/// パターン本体とは独立したストリーム（シードにソルトを XOR）を使い、
/// **点ごとに必ず 4 個の乱数を引く**（位置 XYZ + ヨー）。
/// ジッター量が 0 でも引くのは、設定値によって乱数ストリームがずれないようにするため。
fn apply_jitter(spec: &PlacementSpec, points: &mut [PlacementPoint]) {
    let mut rng = PlacementRng::new(spec.seed ^ JITTER_SEED_SALT);
    for p in points.iter_mut() {
        let jx = rng.next_signed();
        let jy = rng.next_signed();
        let jz = rng.next_signed();
        let jr = rng.next_signed();
        p.position[0] += jx * spec.jitter_pos;
        p.position[1] += jy * spec.jitter_pos;
        p.position[2] += jz * spec.jitter_pos;
        p.rotation[1] += jr * spec.jitter_rot;
    }
}
