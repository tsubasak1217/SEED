// ============================================================
//  collider2d_wireframe.rs — 2D コライダーワイヤーフレーム生成の共通処理
//
//  【責務】
//    Collider2d（Box / Circle / Capsule / ConvexHull）のワイヤーフレーム線分を
//    生成して LineBatch へ追加する共通関数 `emit_collider2d_wireframe` を提供する。
//
//    従来 frame_renderer.rs の 2D シーン用ワイヤーフレーム生成ブロックに
//    形状ごとの線分生成ロジックが直書きされていたが、これを本モジュールへ抽出し、
//      - 通常の 2D シーン（トップレベル Actor2D、ortho / ワールドスペース）
//      - 3D シーン内キャンバス（Actor3D + CanvasComponent）配下の Actor2D
//    の双方から共有できるようにした（二重実装の排除）。
//
//  【座標変換の設計】
//    形状の線分はすべて「正準キャンバス空間（Y+ が下）」で生成する。
//    最終的な描画空間への変換（Y 反転・ピクセル→ワールドのスケール・
//    3D キャンバスの canvas_to_world 行列変換など）は、呼び出し側が渡す
//    クロージャ `map: Fn([f32;2]) -> [f32;3]` に委ねる。
//
//    これにより 1 つの形状生成ロジックで、
//      - 2D シーン: map = |[x,y]| [x, y * y_sign, 0.0]（Y 反転のみ）
//      - 3D キャンバス: map = |[x,y]| canvas_to_world * [x, y, 0, 1]（平面→3D 変換）
//    という異なる描画空間を扱える。
//
//    ※ 従来の 2D シーン向けコード（中心を Y 反転しつつ回転を rot_rad * y_sign に
//      していた実装）と厳密に同一の頂点列を生成するため、`map_y_sign` 引数で
//      「map が Y を反転するかどうか（y_sign）」を受け取り、形状ローカル Y へ
//      同じ符号を乗算する。恒等式
//        F_s( R(θ)·(lx, s·ly) + c ) = R(s·θ)·(lx, ly) + F_s(c)   （s = ±1, F_s: Y×s）
//      により、任意の回転・任意のローカル点で旧実装と数値一致する。
//      矩形・円は ly→-ly の反転に対して形状が対称なため符号なしでも見た目は
//      変わらないが、カプセルの上下キャップ弧（上=左半円・下=右半円）は
//      非対称であり、符号を掛けないと y_sign=-1 のとき左右反転して描画される。
//      凸包は従来コードが回転を y_sign 反転していなかったため符号を掛けない
//      （現行の凸包パスがそのまま旧実装と一致する）。
// ============================================================

use std::collections::HashMap;
use std::f32::consts::PI;

use crate::engine::components::{ColliderShape2dData, ComponentKind};
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::actor::Actor;
use crate::engine::core::app_base::scene::Scene;
use crate::engine::methods::drawer::LineBatch;
use super::actor_utils::get_3d_canvas_world_mat;

// ─── 形状分割数（マジックナンバー回避）─────────────────────────────────────────

/// 円ワイヤーフレームの分割数（従来の add_circle_2d 呼び出しと同一）。
const CIRCLE_SEGMENTS: usize = 32;
/// カプセルワイヤーフレームの分割数指定（従来の add_capsule_2d 呼び出しと同一）。
/// 実際の半円分割数は `capsule_half_segments()` で算出する。
const CAPSULE_SEGMENTS: usize = 16;

/// カプセルの半円 1 つあたりの分割数を算出する。
/// 従来 add_capsule_2d 内の計算 `(segments.max(8) / 2).max(4)` を踏襲する。
#[inline]
fn capsule_half_segments() -> usize {
    (CAPSULE_SEGMENTS.max(8) / 2).max(4)
}

// ─── 共通ワイヤーフレーム生成 ─────────────────────────────────────────────────

/// Collider2d 形状のワイヤーフレーム線分を生成して LineBatch へ追加する共通関数。
///
/// # 引数
/// - `lb`: 追加先の LineBatch。
/// - `shape`: コライダー形状データ。
/// - `center`: コライダー中心（正準キャンバス空間、Y+ 下・未反転・未スケール変換）。
/// - `rot_rad`: 形状の回転（正準キャンバス空間、ラジアン）。
/// - `scale`: CanvasTransform.scale（形状の半径・頂点へ乗算する基本スケール）。
/// - `eff_sx` / `eff_sy`: size_sx / size_sy 由来の実効スケール
///   （2D シーンのワールドスペースでは canvas_scale、SS レイアウトでは size_sx/sy）。
/// - `color`: 線色。
/// - `map_y_sign`: `map` が Y を反転する場合はその符号（2D シーンの y_sign）、
///   反転しない場合（3D キャンバスの canvas_to_world 変換など）は +1.0。
///   ファイル冒頭コメントの恒等式により、形状ローカル Y へこの符号を乗算する
///   ことで従来実装（回転を rot_rad * y_sign にしていたコード）と頂点列が
///   厳密に一致する。特にカプセルのキャップ弧は左右非対称なため必須。
/// - `map`: 正準キャンバス空間の 2D 点 [x, y] を最終描画空間の 3D 点 [x, y, z] へ変換する。
///
/// 形状は中心・回転・スケールを正準空間で適用したうえで `map` を各点に通す。
#[allow(clippy::too_many_arguments)]
pub(super) fn emit_collider2d_wireframe<F>(
    lb:         &mut LineBatch,
    shape:      &ColliderShape2dData,
    center:     [f32; 2],
    rot_rad:    f32,
    scale:      [f32; 2],
    eff_sx:     f32,
    eff_sy:     f32,
    color:      [f32; 4],
    map_y_sign: f32,
    map:        F,
) where
    F: Fn([f32; 2]) -> [f32; 3],
{
    let [cx, cy] = center;
    let (sin, cos) = rot_rad.sin_cos();

    // 正準ローカル点（コライダー中心基準・回転前）を回転・平行移動して描画空間へ写すヘルパー。
    // ローカル Y へ map_y_sign を乗算することで、map の Y 反転と合成した結果が
    // 従来実装（R(rot_rad * y_sign) を反転済み中心へ適用）と厳密に一致する。
    let place = |lx: f32, ly: f32| -> [f32; 3] {
        let ly = ly * map_y_sign;
        map([cx + cos * lx - sin * ly, cy + sin * lx + cos * ly])
    };

    match shape {
        // ── 矩形（回転あり）─────────────────────────────────────────────
        ColliderShape2dData::Box { half_extents } => {
            let hx = half_extents[0] * scale[0].abs() * eff_sx;
            let hy = half_extents[1] * scale[1].abs() * eff_sy;
            let c0 = place(-hx, -hy);
            let c1 = place( hx, -hy);
            let c2 = place( hx,  hy);
            let c3 = place(-hx,  hy);
            lb.add_line(c0, c1, color);
            lb.add_line(c1, c2, color);
            lb.add_line(c2, c3, color);
            lb.add_line(c3, c0, color);
        }

        // ── 円（回転不要・等方スケール）───────────────────────────────────
        ColliderShape2dData::Circle { radius } => {
            let r = radius * scale[0].abs().max(scale[1].abs()) * eff_sx.max(eff_sy);
            let n = CIRCLE_SEGMENTS.max(8);
            for i in 0..n {
                let t0 = 2.0 * PI * i as f32 / n as f32;
                let t1 = 2.0 * PI * (i + 1) as f32 / n as f32;
                // 円は回転で見た目が変わらないため place のローカル回転を使ってよい
                let p0 = place(r * t0.cos(), r * t0.sin());
                let p1 = place(r * t1.cos(), r * t1.sin());
                lb.add_line(p0, p1, color);
            }
        }

        // ── カプセル（長軸 Y・回転あり）──────────────────────────────────
        ColliderShape2dData::Capsule { radius, half_height } => {
            let r  = radius * scale[0].abs().max(scale[1].abs()) * eff_sx.max(eff_sy);
            let hh = half_height * scale[1].abs() * eff_sy;
            let n  = capsule_half_segments();

            // 円筒部の両脇 2 本（接線）
            lb.add_line(place(-r,  hh), place(-r, -hh), color);
            lb.add_line(place( r,  hh), place( r, -hh), color);

            // 上端半円（Y+ 方向）
            for i in 0..n {
                let t0 = PI * i as f32 / n as f32;
                let t1 = PI * (i + 1) as f32 / n as f32;
                lb.add_line(
                    place(-r * t0.sin(),  hh + r * t0.cos()),
                    place(-r * t1.sin(),  hh + r * t1.cos()),
                    color,
                );
            }
            // 下端半円（Y- 方向）
            for i in 0..n {
                let t0 = PI * i as f32 / n as f32;
                let t1 = PI * (i + 1) as f32 / n as f32;
                lb.add_line(
                    place( r * t0.sin(), -hh - r * t0.cos()),
                    place( r * t1.sin(), -hh - r * t1.cos()),
                    color,
                );
            }
        }

        // ── 凸包（頂点へ scale・eff を非等方に適用）──────────────────────────
        ColliderShape2dData::ConvexHull { vertices } => {
            let n = vertices.len();
            if n < 2 { return; }
            // 頂点ごとに scale を乗算 → 回転 → eff_sx/eff_sy を非等方に適用 → 中心へ平行移動。
            // 従来コードと同じく eff_sx/eff_sy は回転後の各軸へ掛ける。
            // 【注意】凸包は従来コードが回転を y_sign 反転していなかったため、
            // place（map_y_sign 乗算あり）を使わず、この専用パス（乗算なし）のまま
            // map へ通すことで従来の頂点列と厳密に一致する。
            let world_verts: Vec<[f32; 3]> = vertices.iter()
                .map(|&[vx, vy]| {
                    let svx = vx * scale[0];
                    let svy = vy * scale[1];
                    let rwx = cos * svx - sin * svy;
                    let rwy = sin * svx + cos * svy;
                    map([cx + rwx * eff_sx, cy + rwy * eff_sy])
                })
                .collect();
            for i in 0..n {
                lb.add_line(world_verts[i], world_verts[(i + 1) % n], color);
            }
        }
    }
}

// ─── コライダーピッキング用フィルクワッド ─────────────────────────────────────

/// コライダー形状の「塗りつぶしピッキング領域」を表す GPU 列優先モデル行列を生成する。
///
/// エディタ編集ビューでは、コライダーは線 1px のワイヤーフレームとして描画されるため
/// 線そのものをクリックしてもアクターを選択できない。そこで ID パス（canvas_id パイプライン）に
/// **コライダーの外接矩形を覆うユニットクワッド**を描画し、面（塗りつぶし領域）を
/// ピッキング対象にする。返す行列はユニットクワッド `[0,1]×[0,1]` を最終描画空間上の
/// コライダー外接クワッドへ写す（`canvas_id.wgsl` は `view_proj * model * [pos,0,1]` を行う）。
///
/// # 座標変換の設計
/// `emit_collider2d_wireframe` と**まったく同じ**中心・回転・スケール・`map` 変換を用いるため、
/// 描画されるワイヤーフレームとピッキング領域は常に一致する（形状ロジックの二重管理を避ける）。
///
/// # 引数
/// 各引数の意味は `emit_collider2d_wireframe` と同一。
/// - `map`: 正準キャンバス空間の 2D 点 [x, y] を最終描画空間の 3D 点 [x, y, z] へ変換する。
///
/// # 戻り値
/// - `Some(model)`: 面ピッキング用の列優先モデル行列（`model[col][row]`）。
/// - `None`: 面を構成できない場合（頂点数不足の凸包など）。
#[allow(clippy::too_many_arguments)]
pub(super) fn collider2d_pick_quad_model<F>(
    shape:      &ColliderShape2dData,
    center:     [f32; 2],
    rot_rad:    f32,
    scale:      [f32; 2],
    eff_sx:     f32,
    eff_sy:     f32,
    map_y_sign: f32,
    map:        F,
) -> Option<[[f32; 4]; 4]>
where
    F: Fn([f32; 2]) -> [f32; 3],
{
    let [cx, cy] = center;
    let (sin, cos) = rot_rad.sin_cos();

    // 外接クワッドの 3 隅（C00=u,v(0,0) / C10=(1,0) / C01=(0,1)）から
    // アフィン変換のモデル行列を構築する共通クロージャ。
    // ユニットクワッド点 (u,v) → C00 + u*(C10-C00) + v*(C01-C00)（平面上）。
    let model_from_corners = |c00: [f32; 3], c10: [f32; 3], c01: [f32; 3]| -> [[f32; 4]; 4] {
        let ex = [c10[0] - c00[0], c10[1] - c00[1], c10[2] - c00[2]]; // u 方向基底
        let ey = [c01[0] - c00[0], c01[1] - c00[1], c01[2] - c00[2]]; // v 方向基底
        // GPU 列優先（model[col][row]）: col0=u 基底, col1=v 基底, col2=Z 恒等, col3=原点。
        [
            [ex[0], ex[1], ex[2], 0.0],
            [ey[0], ey[1], ey[2], 0.0],
            [0.0,   0.0,   1.0,   0.0],
            [c00[0], c00[1], c00[2], 1.0],
        ]
    };

    match shape {
        // ── 矩形・円・カプセル: ローカル外接半矩形 (hx, hy) を place で写す ──────────
        // place は emit_collider2d_wireframe と同一（ローカル Y へ map_y_sign を乗算 → 回転 → map）。
        ColliderShape2dData::Box { .. }
        | ColliderShape2dData::Circle { .. }
        | ColliderShape2dData::Capsule { .. } => {
            // 形状ごとのローカル外接半矩形（emit と同じ実効スケール適用）
            let (hx, hy) = match shape {
                ColliderShape2dData::Box { half_extents } => (
                    half_extents[0] * scale[0].abs() * eff_sx,
                    half_extents[1] * scale[1].abs() * eff_sy,
                ),
                ColliderShape2dData::Circle { radius } => {
                    let r = radius * scale[0].abs().max(scale[1].abs()) * eff_sx.max(eff_sy);
                    (r, r)
                }
                ColliderShape2dData::Capsule { radius, half_height } => {
                    let r  = radius * scale[0].abs().max(scale[1].abs()) * eff_sx.max(eff_sy);
                    let hh = half_height * scale[1].abs() * eff_sy;
                    (r, hh + r)   // 長軸 Y のカプセルはキャップ込みで hh+r
                }
                _ => unreachable!(),
            };
            // ローカル点をワイヤーフレームと同一変換で最終空間へ写す（place と等価）。
            let place = |lx: f32, ly: f32| -> [f32; 3] {
                let ly = ly * map_y_sign;
                map([cx + cos * lx - sin * ly, cy + sin * lx + cos * ly])
            };
            let c00 = place(-hx, -hy);
            let c10 = place( hx, -hy);
            let c01 = place(-hx,  hy);
            Some(model_from_corners(c00, c10, c01))
        }

        // ── 凸包: 変換後頂点のキャンバス空間 AABB を外接クワッドとする ─────────────
        // 凸包は emit と同じく eff を回転後の各軸へ非等方に適用し、map_y_sign は用いない。
        ColliderShape2dData::ConvexHull { vertices } => {
            if vertices.len() < 2 { return None; }
            let (mut min_x, mut min_y) = (f32::INFINITY, f32::INFINITY);
            let (mut max_x, mut max_y) = (f32::NEG_INFINITY, f32::NEG_INFINITY);
            for &[vx, vy] in vertices.iter() {
                let svx = vx * scale[0];
                let svy = vy * scale[1];
                let rwx = (cos * svx - sin * svy) * eff_sx;   // 中心相対のキャンバス空間 X
                let rwy = (sin * svx + cos * svy) * eff_sy;   // 中心相対のキャンバス空間 Y
                min_x = min_x.min(rwx); max_x = max_x.max(rwx);
                min_y = min_y.min(rwy); max_y = max_y.max(rwy);
            }
            // 退化（面積 0）は面ピッキング不可としてスキップする。
            if !(max_x > min_x && max_y > min_y) { return None; }
            let c00 = map([cx + min_x, cy + min_y]);
            let c10 = map([cx + max_x, cy + min_y]);
            let c01 = map([cx + min_x, cy + max_y]);
            Some(model_from_corners(c00, c10, c01))
        }
    }
}

// ─── 3D キャンバス配下コライダーの収集補助 ─────────────────────────────────────

/// 3D シーン内キャンバス（Actor3D + CanvasComponent）配下の Actor2D エンティティを
/// 「所属する 3D キャンバスの canvas_to_world 行列」へ対応付けたマップを構築する。
///
/// スプライトの「3D Canvas 配下収集パス」（canvas_collect.rs / frame_renderer.rs）と
/// 同じくトップレベルの Actor3D + CanvasComponent のみをルートとして扱う。
/// ネストした 3D キャンバス（キャンバス配下の別 Actor3D + CanvasComponent）は
/// スプライト描画パスと同様に対象外とし、そのサブツリーを走査から除外する。
///
/// このマップは 2 つの用途で使う:
///   - 通常の 2D シーン用ワイヤーフレーム生成では「キー = 3D キャンバス配下」を
///     除外する（誤った ortho 空間への描画を防ぐ）。
///   - 3D キャンバス用ワイヤーフレーム生成では「キー = 描画対象」として
///     対応する canvas_to_world 行列を引く。
///
/// # 引数
/// - `scene`: 対象シーン。
/// - `world_line`: 現在の世界線。
///
/// # 戻り値
/// - `HashMap<Entity, [[f32;4];4]>`: Actor2D エンティティ → 所属 3D キャンバスの canvas_to_world。
pub(super) fn build_3d_canvas_collider_descendant_map(
    scene:      &Scene,
    world_line: u32,
) -> HashMap<Entity, [[f32; 4]; 4]> {
    let mut map: HashMap<Entity, [[f32; 4]; 4]> = HashMap::new();

    // トップレベルアクターのうち Actor3D + CanvasComponent をルートとして走査する。
    for actor in scene.actors.iter() {
        if actor.world_line != world_line { continue; }
        // 非アクティブアクターは（配下スプライト同様）描画対象外。
        if !actor.active { continue; }
        // 3D キャンバスでなければスキップ（Actor2D / CanvasComponent なしは None）。
        let Some(ctw) = get_3d_canvas_world_mat(actor, &scene.world) else { continue };

        // このルート配下の全 Actor2D 子孫を ctw へ対応付ける。
        collect_canvas_descendants(&actor.children, &ctw, &mut map);
    }

    map
}

/// `build_3d_canvas_collider_descendant_map` の再帰ヘルパー。
///
/// `actors` 配下を DFS で走査し、各 Actor2D エンティティを `ctw` に対応付ける。
/// ネストした 3D キャンバス（Actor3D + CanvasComponent）に到達した場合、
/// そのサブツリーはスプライト描画パスの対象外であるため走査を打ち切る。
fn collect_canvas_descendants(
    actors: &[Actor],
    ctw:    &[[f32; 4]; 4],
    map:    &mut HashMap<Entity, [[f32; 4]; 4]>,
) {
    for child in actors.iter() {
        // 非アクティブサブツリーは描画されないため対応付けない。
        if !child.active { continue; }

        // ネストした 3D キャンバスはスプライト描画パスの対象外 → サブツリーを除外。
        let is_nested_3d_canvas = !child.is_2d()
            && child.slots().iter().any(|s| s.kind == ComponentKind::Canvas);
        if is_nested_3d_canvas { continue; }

        // Actor2D なら対応付ける（Collider2d の有無はここでは問わない。
        // 実際の描画判定は呼び出し側が collider_slot_entity で行う）。
        if child.is_2d() {
            map.insert(child.entity, *ctw);
        }

        // 子孫を継続走査する。
        collect_canvas_descendants(&child.children, ctw, map);
    }
}

/// canvas_to_world 行列（行優先・平行移動は各行の第 4 列）で正準キャンバス空間の
/// 2D 点 [x, y]（Z=0）を 3D ワールド座標 [X, Y, Z] へ変換する。
///
/// スプライトの 3D キャンバス描画と同一の変換（キャンバス空間 → 3D 空間）を通す。
#[inline]
pub(super) fn canvas_point_to_world(ctw: &[[f32; 4]; 4], p: [f32; 2]) -> [f32; 3] {
    let [x, y] = p;
    [
        ctw[0][0] * x + ctw[0][1] * y + ctw[0][3],
        ctw[1][0] * x + ctw[1][1] * y + ctw[1][3],
        ctw[2][0] * x + ctw[2][1] * y + ctw[2][3],
    ]
}
