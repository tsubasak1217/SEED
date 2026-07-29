// ============================================================
//  control_point_scene_gizmo.rs — コントロールポイントのビューポート可視化とピック
//
//  選択中のアクターが `ControlPointComponent` を持つとき、その各制御点に
//  **ワイヤキューブ**を、点と点のあいだに**補間方法ごとに描き分けたライン**を出す。
//  さらに、キューブへのレイヒットテスト（点のピック）もここに置く。
//
//  ## なぜ「選択中アクターのみ」か
//  ライト／パーティクルのシーンギズモと同じ方針。全アクタのパスを常時描くと
//  シーンが線だらけになり、しかも「クリックしたのは点かメッシュか」が
//  ユーザーにも実装にも曖昧になる。アクタを選ぶ → その点が現れる → 点を掴む、
//  という段階を踏ませることで、操作の対象が常に一意に定まる。
//
//  ## ピックを GPU の ID パスではなく CPU レイで行う理由
//  制御点キューブは「シーンに存在する物体」ではなく**編集ハンドル**であり、
//  移動ギズモの軸ハンドルと同じ性質のものである。本エンジンでは軸ハンドルの
//  当たり判定は既に CPU レイ（`gizmo_interact::hit_test_gizmo`）で行っており、
//  ハンドル類の判定をそこに揃える。
//  GPU の ID 帯（`frame_renderer` の [MC | カメラ | ライト | エミッタ | キャンバス]）は
//  毎フレーム個数から積み上げる動的レイアウトで、キャンバス帯が「それ以降すべて」を
//  受ける終端帯になっているため、帯を 1 つ増やすと書き込み・デコードの両方に
//  手を入れる必要がある。数十個のハンドルのためにその不可逆な複雑さを足すより、
//  数十回のレイ×AABB 判定（1 フレーム 1 回、クリック時のみ）の方が安い。
//
//  ## 線の描き分け（区間の補間方法が一目で分かること）
//    ・CatmullRom … シアンの実線（曲線を折れ線化したもの）
//    ・Linear     … 緑の実線（2 点を直接結ぶ）
//    ・Step       … オレンジの L 字（水平に送ってから垂直に上げる）
//      L 字にするのは「その経路を通る」のではなく「保持したのち飛ぶ」ことを
//      直線と区別して示すため。実際の評価では中間位置は手前の点のままである。
// ============================================================

use crate::engine::components::control_point_component::{ControlPointComponent, ControlPointInterp};
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::ecs::World;
use crate::engine::methods::drawer::LineBatch;
use crate::engine::path::{PathEval, PATH_DEFAULT_STEP_M};
use crate::engine::structs::objects::Actor;

// ─── 色・寸法の定数（マジックナンバー禁止）───────────────────

/// 通常の制御点キューブの色（淡いオレンジ。地形・水のどちらの上でも視認できる）。
const POINT_CUBE_COLOR: [f32; 4] = [1.0, 0.65, 0.20, 0.95];
/// 選択中の制御点キューブの色（白。掴んでいる点が一目で分かるように）。
const POINT_CUBE_SELECTED_COLOR: [f32; 4] = [1.0, 1.0, 1.0, 1.0];
/// CatmullRom 区間の線色（シアン）。
const SEGMENT_CATMULL_COLOR: [f32; 4] = [0.35, 0.90, 1.0, 0.90];
/// Linear 区間の線色（緑）。
const SEGMENT_LINEAR_COLOR: [f32; 4] = [0.40, 1.0, 0.45, 0.90];
/// Step 区間の線色（オレンジ。L 字で描く）。
const SEGMENT_STEP_COLOR: [f32; 4] = [1.0, 0.55, 0.20, 0.90];

/// 制御点キューブの半辺長を「そこに置いたギズモの見かけ半径」の何倍にするか。
///
/// 移動ギズモより明確に小さくして、点キューブがギズモの軸ハンドルを
/// 隠さないようにする。画面上の見かけの大きさは距離・ズームに依らず一定になる
/// （半径の算出を呼び出し側の `editor_3d_gizmo_radius` に委ねているため）。
pub const POINT_CUBE_RADIUS_RATIO: f32 = 0.10;

/// 制御点キューブの半辺長の下限（m）。極端に寄ったときに潰れないように。
pub const POINT_CUBE_HALF_MIN: f32 = 0.02;

/// ピック時にキューブへ与える当たり判定の余裕倍率。
///
/// 見た目どおりの大きさだと「線の上をクリックしたのに掴めない」が頻発するため、
/// 判定だけ少し大きくする（ギズモの軸ハンドルと同じ考え方）。
pub const POINT_PICK_TOLERANCE_SCALE: f32 = 1.6;

/// レイ×AABB 判定でゼロ除算を避けるための下限。
const RAY_EPSILON: f32 = 1.0e-8;

// ─── 収集結果 ────────────────────────────────────────────────

/// 1 つの `ControlPointComponent` スロットを、ワールド解決済みパスとして持つもの。
pub struct ResolvedPathSlot {
    /// アクタ内のスロット添字（IPC・選択状態の識別に必要）。
    pub slot_idx: u32,
    /// ワールド空間へ解決済みのパス。
    pub eval: PathEval,
}

/// レイが当たった制御点。
pub struct ControlPointHit {
    /// スロット添字。
    pub slot_idx: u32,
    /// 点列内の添字。
    pub index: u32,
    /// レイ原点からの距離（複数当たったとき手前を選ぶために使う）。
    pub distance: f32,
}

// ─── 収集 ────────────────────────────────────────────────────

/// 指定アクタが持つ全 `ControlPointComponent` スロットを、ワールド解決済みで集める。
///
/// スロットが無効（`enabled == false`）のものは除く（無効なコンポーネントは
/// ビューポートにも現れないのが他コンポーネントと揃った挙動）。
pub fn collect_paths_for_actor(
    actors:   &Vec<Actor>,
    world:    &World,
    wl:       u32,
    dfs_id:   u32,
) -> Vec<ResolvedPathSlot> {
    use super::find_actor_by_dfs;

    let mut counter = 0u32;
    let Some(actor) = find_actor_by_dfs(actors, wl, dfs_id, &mut counter) else { return Vec::new() };
    // アクタの Transform（点はアクタ相対なのでワールド解決に必要）。
    let tf = world.get::<Transform>(actor.entity).cloned().unwrap_or_default();

    actor.slots().iter().enumerate()
        .filter(|(_, s)| s.kind == ComponentKind::ControlPoint && s.enabled)
        .filter_map(|(i, s)| {
            let comp = world.get::<ControlPointComponent>(s.entity)?;
            Some(ResolvedPathSlot {
                slot_idx: i as u32,
                eval:     PathEval::from_points(&comp.points, &tf),
            })
        })
        .collect()
}

// ─── 描画 ────────────────────────────────────────────────────

/// 解決済みパス群から、点キューブ＋区間ラインの `LineBatch`（CPU 側）を組む。
///
/// - `selected`: 強調表示する点（スロット添字, 点添字）。無ければ全点を通常色で描く。
/// - `half_size_for`: ある位置に置くキューブの半辺長を返す関数。
///   画面上の見かけの大きさを一定にするため、呼び出し側のカメラ都合に委ねている。
///
/// GPU バッファ化（`LineBatch::build`）は呼び出し側で行う。
/// ここで `wgpu::Device` を要求しないのは、フレームループ側でレンダラを可変借用する
/// 前にこのバッチを組む必要があるため（借用の都合を描画層へ持ち込まない）。
///
/// 描くものが 1 つも無ければ None（＝呼び出し側は GPU バッファを作らない）。
pub fn build_control_point_lines(
    paths:         &[ResolvedPathSlot],
    selected:      Option<(u32, u32)>,
    half_size_for: impl Fn([f32; 3]) -> f32,
) -> Option<LineBatch> {
    let mut lb = LineBatch::new();
    for path in paths {
        add_path_lines(&mut lb, &path.eval);
        add_path_cubes(&mut lb, path, selected, &half_size_for);
    }
    if lb.is_empty() { None } else { Some(lb) }
}

/// 区間ラインを補間方法ごとに描き分けて追加する。
fn add_path_lines(lb: &mut LineBatch, eval: &PathEval) {
    let pts = eval.points();
    if pts.len() < 2 { return; }
    for i in 0..pts.len() - 1 {
        match pts[i].interp {
            // 階段保持: 「保持 → 飛ぶ」ことが直線と区別できるよう L 字で描く。
            // まず水平（XZ）に送り、そこから垂直（Y）に上げる。
            ControlPointInterp::Step => {
                let a = pts[i].position;
                let b = pts[i + 1].position;
                let corner = [b[0], a[1], b[2]];
                lb.add_line(a, corner, SEGMENT_STEP_COLOR);
                lb.add_line(corner, b, SEGMENT_STEP_COLOR);
            }
            // 直線・曲線は折れ線化した頂点列をそのまま繋ぐ
            //（Linear は分割されないので線分 1 本になる）。
            ControlPointInterp::Linear | ControlPointInterp::CatmullRom => {
                let color = if pts[i].interp == ControlPointInterp::Linear {
                    SEGMENT_LINEAR_COLOR
                } else {
                    SEGMENT_CATMULL_COLOR
                };
                let poly = eval.segment_polyline(i, PATH_DEFAULT_STEP_M);
                for w in poly.windows(2) {
                    lb.add_line(w[0], w[1], color);
                }
            }
        }
    }
}

/// 各制御点にワイヤキューブを追加する（選択中の点だけ色を変える）。
fn add_path_cubes(
    lb:            &mut LineBatch,
    path:          &ResolvedPathSlot,
    selected:      Option<(u32, u32)>,
    half_size_for: &impl Fn([f32; 3]) -> f32,
) {
    for (i, p) in path.eval.points().iter().enumerate() {
        let is_selected = selected == Some((path.slot_idx, i as u32));
        let color = if is_selected { POINT_CUBE_SELECTED_COLOR } else { POINT_CUBE_COLOR };
        add_wire_cube(lb, p.position, cube_half_size(p.position, half_size_for), color);
    }
}

/// 指定位置に置くキューブの半辺長（下限つき）。
fn cube_half_size(pos: [f32; 3], half_size_for: &impl Fn([f32; 3]) -> f32) -> f32 {
    half_size_for(pos).max(POINT_CUBE_HALF_MIN)
}

/// 中心と半辺長から軸平行ワイヤキューブ（12 辺）を追加する。
///
/// `LineBatch::add_aabb` は `Aabb` 型を要求するため、
/// 中心＋半辺長で描きたいここでは頂点を直接組む
/// （`Aabb` を作って渡すのと同じ 12 辺だが、変換の往復が無い）。
fn add_wire_cube(lb: &mut LineBatch, center: [f32; 3], half: f32, color: [f32; 4]) {
    let mn = [center[0] - half, center[1] - half, center[2] - half];
    let mx = [center[0] + half, center[1] + half, center[2] + half];
    let v = [
        [mn[0], mn[1], mn[2]], [mx[0], mn[1], mn[2]],
        [mx[0], mx[1], mn[2]], [mn[0], mx[1], mn[2]],
        [mn[0], mn[1], mx[2]], [mx[0], mn[1], mx[2]],
        [mx[0], mx[1], mx[2]], [mn[0], mx[1], mx[2]],
    ];
    // 下面 4 辺
    lb.add_line(v[0], v[1], color); lb.add_line(v[1], v[2], color);
    lb.add_line(v[2], v[3], color); lb.add_line(v[3], v[0], color);
    // 上面 4 辺
    lb.add_line(v[4], v[5], color); lb.add_line(v[5], v[6], color);
    lb.add_line(v[6], v[7], color); lb.add_line(v[7], v[4], color);
    // 垂直 4 辺
    lb.add_line(v[0], v[4], color); lb.add_line(v[1], v[5], color);
    lb.add_line(v[2], v[6], color); lb.add_line(v[3], v[7], color);
}

// ─── ピック ──────────────────────────────────────────────────

/// レイに当たった制御点のうち、**最も手前のもの**を返す。
///
/// 判定はキューブの軸平行 AABB（見た目より `POINT_PICK_TOLERANCE_SCALE` 倍だけ大きい）。
/// レイ原点より後ろ（`t < 0`）の交差は採らない。
pub fn pick_control_point(
    paths:         &[ResolvedPathSlot],
    ray_origin:    [f32; 3],
    ray_dir:       [f32; 3],
    half_size_for: impl Fn([f32; 3]) -> f32,
) -> Option<ControlPointHit> {
    let mut best: Option<ControlPointHit> = None;
    for path in paths {
        for (i, p) in path.eval.points().iter().enumerate() {
            let half = cube_half_size(p.position, &half_size_for) * POINT_PICK_TOLERANCE_SCALE;
            let Some(t) = ray_aabb_distance(ray_origin, ray_dir, p.position, half) else { continue };
            if best.as_ref().map(|b| t < b.distance).unwrap_or(true) {
                best = Some(ControlPointHit { slot_idx: path.slot_idx, index: i as u32, distance: t });
            }
        }
    }
    best
}

/// レイと「中心 `center`・半辺長 `half` の軸平行立方体」の交差距離（スラブ法）。
///
/// 交差しない、またはレイの後方でしか交差しないなら None。
/// レイ原点が内部にある場合は 0 を返す（＝掴んでいるものが最優先で当たる）。
fn ray_aabb_distance(origin: [f32; 3], dir: [f32; 3], center: [f32; 3], half: f32) -> Option<f32> {
    let mut t_min = f32::NEG_INFINITY;
    let mut t_max = f32::INFINITY;
    for a in 0..3 {
        let lo = center[a] - half;
        let hi = center[a] + half;
        if dir[a].abs() < RAY_EPSILON {
            // 軸に平行なレイ: スラブの外に居るなら永久に当たらない。
            if origin[a] < lo || origin[a] > hi { return None; }
            continue;
        }
        let inv = 1.0 / dir[a];
        let mut t0 = (lo - origin[a]) * inv;
        let mut t1 = (hi - origin[a]) * inv;
        if t0 > t1 { std::mem::swap(&mut t0, &mut t1); }
        t_min = t_min.max(t0);
        t_max = t_max.min(t1);
        if t_min > t_max { return None; }
    }
    if t_max < 0.0 { return None; }
    Some(t_min.max(0.0))
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 正面から撃ったレイが立方体に当たり、距離が「中心距離 − 半辺長」になること。
    #[test]
    fn ray_hits_cube_in_front() {
        let t = ray_aabb_distance([0.0, 0.0, -10.0], [0.0, 0.0, 1.0], [0.0, 0.0, 0.0], 1.0);
        assert!((t.expect("当たること") - 9.0).abs() < 1.0e-4);
    }

    /// 外れたレイは当たらないこと。
    #[test]
    fn ray_misses_cube() {
        assert!(ray_aabb_distance([5.0, 5.0, -10.0], [0.0, 0.0, 1.0], [0.0, 0.0, 0.0], 1.0).is_none());
    }

    /// 背後の立方体には当たらないこと（レイの後方は採らない）。
    #[test]
    fn ray_ignores_cube_behind_origin() {
        assert!(ray_aabb_distance([0.0, 0.0, 10.0], [0.0, 0.0, 1.0], [0.0, 0.0, 0.0], 1.0).is_none());
    }

    /// レイ原点が立方体の内部にあるときは距離 0 で当たること。
    #[test]
    fn ray_from_inside_cube_hits_at_zero() {
        let t = ray_aabb_distance([0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 0.0, 0.0], 1.0);
        assert_eq!(t, Some(0.0));
    }

    /// 軸に平行なレイでも、スラブの外なら当たらず内なら当たること（ゼロ除算の回避）。
    #[test]
    fn axis_parallel_ray_is_handled() {
        // Y が範囲外のまま X 方向へ飛ぶ → 当たらない
        assert!(ray_aabb_distance([-10.0, 5.0, 0.0], [1.0, 0.0, 0.0], [0.0, 0.0, 0.0], 1.0).is_none());
        // Y が範囲内 → 当たる
        assert!(ray_aabb_distance([-10.0, 0.5, 0.0], [1.0, 0.0, 0.0], [0.0, 0.0, 0.0], 1.0).is_some());
    }

    /// 複数の点が並ぶとき、レイに沿って**手前の点**が選ばれること。
    #[test]
    fn pick_returns_nearest_point() {
        use crate::engine::components::control_point_component::ControlPoint;

        let pts = [
            ControlPoint { position: [0.0, 0.0, 0.0],  ..Default::default() },
            ControlPoint { position: [0.0, 0.0, 10.0], ..Default::default() },
        ];
        let paths = vec![ResolvedPathSlot {
            slot_idx: 3,
            eval: PathEval::from_points(&pts, &Transform::identity()),
        }];
        // -Z 側から +Z へ撃つ → 手前は index 0
        let hit = pick_control_point(&paths, [0.0, 0.0, -20.0], [0.0, 0.0, 1.0], |_| 1.0)
            .expect("当たること");
        assert_eq!(hit.slot_idx, 3);
        assert_eq!(hit.index, 0);

        // +Z 側から -Z へ撃つ → 手前は index 1
        let hit2 = pick_control_point(&paths, [0.0, 0.0, 30.0], [0.0, 0.0, -1.0], |_| 1.0)
            .expect("当たること");
        assert_eq!(hit2.index, 1);
    }

    /// どの点にも当たらないレイでは None が返ること。
    #[test]
    fn pick_returns_none_when_nothing_hit() {
        use crate::engine::components::control_point_component::ControlPoint;

        let pts = [ControlPoint { position: [0.0, 0.0, 0.0], ..Default::default() }];
        let paths = vec![ResolvedPathSlot {
            slot_idx: 0,
            eval: PathEval::from_points(&pts, &Transform::identity()),
        }];
        assert!(pick_control_point(&paths, [100.0, 100.0, -20.0], [0.0, 0.0, 1.0], |_| 1.0).is_none());
    }

    /// キューブの半辺長は下限で切られること（遠景で潰れない・近景で消えない）。
    #[test]
    fn cube_half_size_has_lower_bound() {
        assert_eq!(cube_half_size([0.0; 3], &|_| 0.0), POINT_CUBE_HALF_MIN);
        assert_eq!(cube_half_size([0.0; 3], &|_| 1.0), 1.0);
    }
}
