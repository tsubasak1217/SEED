// ============================================================
//  collider3d_pick.rs — 3D コライダーの面ピッキング用クワッド生成
//
//  【責務】
//    エディタ編集ビューで 3D コライダー（ColliderComponent）は 1px の
//    ワイヤーフレームとして描画されるため、線をクリックしてもアクターを
//    選択できない。本モジュールは各コライダー形状の「塗りつぶしピッキング
//    領域」を近似するクワッド（canvas_id パイプライン用の GPU 列優先
//    モデル行列）を生成し、ID パスへの描画を可能にする。
//
//  【設計方針】
//    - canvas_id パイプライン（ユニットクワッド [0,1]×[0,1] を model 行列で
//      配置し raw ID を書き込む）をそのまま流用する。専用パイプラインや
//      塗りつぶし 3D メッシュ生成を追加しない最小構成。
//    - ID パスは「後勝ち」のため、生成したクワッドを他の描画物より先に
//      描画することで、コライダーは常に最背面のピッキング対象となり
//      既存のメッシュ・スプライト選択を妨げない。
//    - 形状の正確なシルエットではなく、以下の近似で面領域を構成する:
//        Box:               ローカル 3 直交中央断面（XY / YZ / XZ 平面）の 3 クワッド。
//                           軸に細長い箱でも過大にならず、任意視点で内部の大半を覆う。
//        Sphere:            バウンディング半径のカメラ正対ビルボード 1 枚（正確な外接シルエット）。
//        Capsule/Cylinder/Cone:
//                           形状軸に沿った「軸ビルボード」1 枚。長軸 = 形状軸方向、
//                           短軸 = 軸×視線の横方向。シルエット矩形にほぼ一致する。
//        ConvexHull:        頂点最大ノルムを半径とするカメラ正対ビルボード。
//        TriangleMesh:      対象外（None）。地形等の巨大メッシュをバウンディング
//                           ビルボードで覆うと空クリック（選択解除）を大きく妨げる
//                           うえ、トライメッシュは通常可視メッシュを伴い MC ピックで
//                           選択できるため、面ピッキングを生成しない。
// ============================================================

use crate::engine::components::ColliderShapeData;
use crate::engine::structs::tensor::Vector3;
use crate::engine::structs::transforms::Quaternion;

/// 視線と形状軸がほぼ平行なときの横方向ベクトル縮退判定のしきい値。
/// 外積ノルムがこれ未満の場合はカメラ正対ビルボードへフォールバックする。
const AXIS_VIEW_PARALLEL_EPS: f32 = 1e-4;

/// 面積ゼロ（半径・半サイズが実質 0）のクワッドを除外するしきい値。
const DEGENERATE_SIZE_EPS: f32 = 1e-6;

// ─── ベクトル小道具（[f32;3] ベース。GPU 行列構築用）──────────────────────────

/// 3 成分ベクトルの減算。
#[inline]
fn v_sub(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

/// 3 成分ベクトルのスカラー倍。
#[inline]
fn v_scale(a: [f32; 3], s: f32) -> [f32; 3] {
    [a[0] * s, a[1] * s, a[2] * s]
}

/// 3 成分ベクトルの外積。
#[inline]
fn v_cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}

/// 3 成分ベクトルのノルム。
#[inline]
fn v_len(a: [f32; 3]) -> f32 {
    (a[0] * a[0] + a[1] * a[1] + a[2] * a[2]).sqrt()
}

/// 3 成分ベクトルの正規化。ノルムが `eps` 未満なら None。
#[inline]
fn v_normalize(a: [f32; 3], eps: f32) -> Option<[f32; 3]> {
    let l = v_len(a);
    if l < eps { None } else { Some(v_scale(a, 1.0 / l)) }
}

/// 中心 `center`・半軸ベクトル `half_u` / `half_v` のクワッドを表す
/// GPU 列優先モデル行列（canvas_id パイプライン用）を構築する。
///
/// ユニットクワッド点 (u, v) ∈ [0,1]² は
///   world = origin + u * (2*half_u) + v * (2*half_v),  origin = center - half_u - half_v
/// へ写される（col0 = u 基底、col1 = v 基底、col3 = 原点）。
#[inline]
fn quad_model(center: [f32; 3], half_u: [f32; 3], half_v: [f32; 3]) -> [[f32; 4]; 4] {
    let ex = v_scale(half_u, 2.0);
    let ey = v_scale(half_v, 2.0);
    let origin = [
        center[0] - half_u[0] - half_v[0],
        center[1] - half_u[1] - half_v[1],
        center[2] - half_u[2] - half_v[2],
    ];
    [
        [ex[0], ex[1], ex[2], 0.0],
        [ey[0], ey[1], ey[2], 0.0],
        [0.0,   0.0,   1.0,   0.0],   // Z 基底は未使用（クワッドは平面）だが恒等にしておく
        [origin[0], origin[1], origin[2], 1.0],
    ]
}

/// カメラ正対ビルボードクワッド（半径 `radius` の正方形）のモデル行列を構築する。
///
/// 視線方向はコライダー中心 − カメラ位置（透視投影で正確な正対）。
/// 縮退時（中心 = カメラ位置）はワールド軸フォールバックで基底を作る。
fn billboard_quad(center: [f32; 3], cam_pos: [f32; 3], radius: f32) -> Option<[[f32; 4]; 4]> {
    if radius < DEGENERATE_SIZE_EPS { return None; }
    // 視線方向（中心 − カメラ）。縮退時は Z+ を仮の視線とする。
    let dir = v_normalize(v_sub(center, cam_pos), AXIS_VIEW_PARALLEL_EPS)
        .unwrap_or([0.0, 0.0, 1.0]);
    // 視線に直交する right / up を構築する（up の初期候補はワールド Y、平行なら X）。
    let world_up = [0.0f32, 1.0, 0.0];
    let right = v_normalize(v_cross(world_up, dir), AXIS_VIEW_PARALLEL_EPS)
        .or_else(|| v_normalize(v_cross([1.0, 0.0, 0.0], dir), AXIS_VIEW_PARALLEL_EPS))?;
    let up = v_cross(dir, right);    // dir・right は正規直交なので up も単位ベクトル
    Some(quad_model(center, v_scale(right, radius), v_scale(up, radius)))
}

/// 軸ビルボードクワッド（長軸 = `axis` 方向 ±`half_len`、短軸 = 視線×軸 ±`half_wid`）を構築する。
///
/// カプセル・シリンダー・コーンのシルエット矩形にほぼ一致する。
/// 軸と視線がほぼ平行（真上/真下から見ている）場合はカメラ正対ビルボードへ
/// フォールバックする（そのときのシルエットは円に近いため半径 = max(half_len, half_wid)）。
fn axis_billboard_quad(
    center:   [f32; 3],
    cam_pos:  [f32; 3],
    axis:     [f32; 3],   // 単位ベクトルであること（呼び出し側で正規化済み）
    half_len: f32,
    half_wid: f32,
) -> Option<[[f32; 4]; 4]> {
    if half_len < DEGENERATE_SIZE_EPS || half_wid < DEGENERATE_SIZE_EPS { return None; }
    let dir = v_normalize(v_sub(center, cam_pos), AXIS_VIEW_PARALLEL_EPS)
        .unwrap_or([0.0, 0.0, 1.0]);
    // 横方向 = 視線 × 形状軸。軸と視線が平行に近い場合は縮退 → 正対ビルボード。
    match v_normalize(v_cross(dir, axis), AXIS_VIEW_PARALLEL_EPS) {
        Some(side) => Some(quad_model(
            center,
            v_scale(axis, half_len),
            v_scale(side, half_wid),
        )),
        None => billboard_quad(center, cam_pos, half_len.max(half_wid)),
    }
}

// ─── 公開 API ─────────────────────────────────────────────────────────────────

/// 3D コライダー形状の面ピッキング用クワッド（GPU 列優先モデル行列）を生成して
/// `out` へ追加する。
///
/// 引数はワイヤーフレーム描画（frame_renderer.rs の 3D コライダーバッチ）と
/// 同一の値を渡すこと。これによりピッキング領域とワイヤーフレーム表示が一致する。
///
/// # 引数
/// - `shape`:   コライダー形状データ。
/// - `pos`:     コライダー中心のワールド座標（Transform 位置 + 回転済みオフセット）。
/// - `q`:       アクターのワールド回転クォータニオン。
/// - `scale`:   Transform スケール（ワイヤーフレームと同じ軸別絶対値適用）。
/// - `cam_pos`: 編集カメラのワールド位置（ビルボード正対計算用）。
/// - `out`:     クワッドのモデル行列追加先（1 形状につき 0〜3 枚）。
pub(super) fn collect_collider3d_pick_quads(
    shape:   &ColliderShapeData,
    pos:     [f32; 3],
    q:       &Quaternion,
    scale:   [f32; 3],
    cam_pos: [f32; 3],
    out:     &mut Vec<[[f32; 4]; 4]>,
) {
    // 回転クォータニオンでローカル方向ベクトルをワールドへ写すヘルパー。
    let rot = |v: [f32; 3]| -> [f32; 3] {
        let r = q.rotate(Vector3::new(v[0], v[1], v[2]));
        [r.x, r.y, r.z]
    };

    match shape {
        // ── 箱: ローカル 3 直交中央断面（XY / YZ / XZ）───────────────────────────
        // バウンディングビルボードだと細長い箱（壁・床など）で過大な領域になるため、
        // 箱のローカル平面 3 枚で内部を覆う（任意視点でシルエットの大半をカバー）。
        ColliderShapeData::Box { half_extents } => {
            let hx = half_extents[0] * scale[0].abs();
            let hy = half_extents[1] * scale[1].abs();
            let hz = half_extents[2] * scale[2].abs();
            let ax = rot([1.0, 0.0, 0.0]);
            let ay = rot([0.0, 1.0, 0.0]);
            let az = rot([0.0, 0.0, 1.0]);
            // 各断面: 面積ゼロ軸の組は quad_model 側でなく事前サイズ判定で除外する
            let planes: [([f32; 3], f32, [f32; 3], f32); 3] = [
                (ax, hx, ay, hy),   // XY 断面
                (ay, hy, az, hz),   // YZ 断面
                (ax, hx, az, hz),   // XZ 断面
            ];
            for (u_dir, hu, v_dir, hv) in planes {
                if hu < DEGENERATE_SIZE_EPS || hv < DEGENERATE_SIZE_EPS { continue; }
                out.push(quad_model(pos, v_scale(u_dir, hu), v_scale(v_dir, hv)));
            }
        }

        // ── 球: バウンディング半径の正対ビルボード（正確な外接シルエット）─────────
        ColliderShapeData::Sphere { radius } => {
            let r = radius * scale[0].abs().max(scale[1].abs()).max(scale[2].abs());
            if let Some(m) = billboard_quad(pos, cam_pos, r) { out.push(m); }
        }

        // ── カプセル: 軸ビルボード（長軸 = ローカル Y、キャップ込み hh+r）──────────
        ColliderShapeData::Capsule { radius, half_height } => {
            let r  = radius * scale[0].abs().max(scale[2].abs());
            let hh = half_height * scale[1].abs();
            let axis = rot([0.0, 1.0, 0.0]);
            if let Some(m) = axis_billboard_quad(pos, cam_pos, axis, hh + r, r) { out.push(m); }
        }

        // ── シリンダー: 軸ビルボード（長軸 = ローカル Y）────────────────────────
        ColliderShapeData::Cylinder { radius, half_height } => {
            let r  = radius * scale[0].abs().max(scale[2].abs());
            let hh = half_height * scale[1].abs();
            let axis = rot([0.0, 1.0, 0.0]);
            if let Some(m) = axis_billboard_quad(pos, cam_pos, axis, hh, r) { out.push(m); }
        }

        // ── コーン: 軸ビルボード（シルエット三角形を外接矩形で近似）──────────────
        ColliderShapeData::Cone { radius, half_height } => {
            let r  = radius * scale[0].abs().max(scale[2].abs());
            let hh = half_height * scale[1].abs();
            let axis = rot([0.0, 1.0, 0.0]);
            if let Some(m) = axis_billboard_quad(pos, cam_pos, axis, hh, r) { out.push(m); }
        }

        // ── 凸包: 頂点最大ノルムを半径とする正対ビルボード ────────────────────────
        ColliderShapeData::ConvexHull { vertices } => {
            let mut max_r2 = 0.0f32;
            for &[x, y, z] in vertices.iter() {
                let sx = x * scale[0];
                let sy = y * scale[1];
                let sz = z * scale[2];
                max_r2 = max_r2.max(sx * sx + sy * sy + sz * sz);
            }
            if let Some(m) = billboard_quad(pos, cam_pos, max_r2.sqrt()) { out.push(m); }
        }

        // ── トライメッシュ: 面ピッキング対象外（モジュール冒頭コメント参照）─────────
        ColliderShapeData::TriangleMesh { .. } => {}
    }
}
