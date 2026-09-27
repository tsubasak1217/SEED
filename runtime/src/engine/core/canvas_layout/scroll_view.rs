// ============================================================
//  canvas_layout/scroll_view.rs — スクロールの領域のレイアウト（W2-3。走査 pass.rs が呼ぶ部品）
//
//  【何をするか】CanvasScrollComponent を持つノード（スクロールの窓）について、レイアウトの走査の中で次を行う:
//    1. 子へ渡す文脈（CanvasParentFrame の行列）を、スクロールの状態の位置だけ平行移動する
//       （位置 × 子の累積スケール = 窓のローカルの画素。子・コンテナ・アンカーはすべてこの文脈の中で置かれる）
//    2. 窓の大きさ・中身の大きさ（子の矩形のいちばん遠い端。または Fixed の値）・換算を `CanvasScrollRegion` として表へ積む
//       （フレームの描画がスクロールの状態の `metrics` へ写し、次のフレームの物理が範囲に使う）
//    3. 見える範囲の外を飛ばすための「ノードの範囲」（キャンバス空間の AABB）を求める（飛ばす判定は canvas_scroll/visibility.rs）
//
//  【ノードの矩形の選び方】切り抜き・当たり判定と同じ: キャンバス領域（CanvasComponent・レイアウトの矩形）→ 最初の有効な
//  Sprite の矩形。どちらも無いノード（テキストだけ・パーティクル・空のノード）は位置の 1 点として扱い、見える範囲の側の
//  余白（cache_extent。既定 250 dp）で文字のはみ出しを受ける。
// ============================================================

use crate::engine::components::{CanvasScrollComponent, ComponentKind, SpriteComponent};
use crate::engine::core::canvas_scroll::state::CanvasScrollState;
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::structs::objects::Actor;

use super::clip::{canvas_area_corners, corners_aabb, sprite_rect_corners};
use super::frame::{CanvasParentFrame, IDENTITY_MAT4};
use super::placement::CanvasNodePlacement;

/// 軸の数（X・Y）。
const AXES: usize = 2;

/// キャンバス空間の軸に沿った矩形（min, max）。切り抜きの AABB（clip.rs の corners_aabb）と同じ形。
pub type ClipAabb = ([f32; 2], [f32; 2]);

/// 向きの長さを 0 とみなす大きさ（退化した行列の軸を正規化しない）。
const DIRECTION_EPSILON: f32 = 1e-9;

/// スクロールの領域（表の 1 行ぶん。値は窓のローカルの画素か、キャンバス空間）。
#[derive(Clone, Debug, PartialEq)]
pub struct CanvasScrollRegion {
    /// 窓のノードの行の添字。
    pub owner: u32,
    /// 窓のノード（アクターの entity）。
    pub node: Entity,
    /// CanvasScrollComponent のスロットのエンティティ（状態 CanvasScrollState の置き場）。
    pub slot: Entity,
    /// 窓の大きさ（窓のローカルの画素）。
    pub viewport_px: [f32; 2],
    /// 中身の大きさ（窓のローカルの画素。原点からいちばん遠い端まで）。
    pub content_px: [f32; 2],
    /// 1 単位の画素数（子の累積スケール）。
    pub px_per_unit: [f32; 2],
    /// 窓のローカルの X 軸・Y 軸の向き（キャンバス空間の単位ベクトル）。
    pub axis_dirs: [[f32; 2]; AXES],
    /// 子へ当てた平行移動（窓のローカルの画素。位置 × 換算）。
    pub offset_px: [f32; 2],
    /// 見える範囲（窓の切り抜きの AABB。キャンバス空間。切り抜きが無ければ None）。
    pub view: Option<ClipAabb>,
    /// 1 dp の画素数。
    pub dp_scale: f32,
}

/// ノードのスクロール（有効なスロットの最初の CanvasScroll で、コンポーネントも有効なもの）。
pub struct ScrollNode<'w> {
    /// 設定。
    pub settings: &'w CanvasScrollComponent,
    /// スロットのエンティティ。
    pub slot: Entity,
    /// 今の位置（状態が無ければ 0＝エディタの Edit・最初のフレーム）。
    pub position: [f64; AXES],
}

/// ノードのスクロールを引く。
pub fn scroll_of<'w>(actor: &Actor, world: &'w World) -> Option<ScrollNode<'w>> {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::CanvasScroll && s.enabled)
        .find_map(|s| world.get::<CanvasScrollComponent>(s.entity).map(|c| (s.entity, c)))
        .filter(|(_, c)| c.enabled)
        .map(|(slot, settings)| ScrollNode {
            settings,
            slot,
            position: world.get::<CanvasScrollState>(slot).map_or([0.0; AXES], |s| s.position),
        })
}

/// スクロールの位置（単位）を、子のローカルの画素の平行移動にする（スクロールしない軸は 0）【純関数】。
pub fn offset_px(settings: &CanvasScrollComponent, position: [f64; AXES], child_cumul: [f32; 2]) -> [f32; 2] {
    [0, 1].map(|a| {
        if settings.direction.scrolls_axis(a) && position[a].is_finite() {
            position[a] as f32 * child_cumul[a]
        } else {
            0.0
        }
    })
}

/// 子へ渡す文脈を、窓のローカルで (−offset) だけ平行移動する【純関数】（中身が位置の分だけ上・左へずれる）。
pub fn translate_frame(frame: &mut CanvasParentFrame, offset: [f32; 2]) {
    if offset == [0.0, 0.0] {
        return;
    }
    let translation = [
        [1.0, 0.0, 0.0, -offset[0]],
        [0.0, 1.0, 0.0, -offset[1]],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ];
    frame.world_rs = mat4x4_mul(frame.world_rs, translation);
}

/// 行列の X 軸・Y 軸の向き（キャンバス空間の単位ベクトル。回転だけの行列を想定）【純関数】。
pub fn axis_directions(m: &[[f32; 4]; 4]) -> [[f32; 2]; AXES] {
    [0, 1].map(|col| {
        let v = [m[0][col], m[1][col]];
        let len = (v[0] * v[0] + v[1] * v[1]).sqrt();
        if len > DIRECTION_EPSILON { [v[0] / len, v[1] / len] } else if col == 0 { [1.0, 0.0] } else { [0.0, 1.0] }
    })
}

/// ノードの最初の有効な Sprite。
fn first_sprite<'w>(actor: &Actor, world: &'w World) -> Option<&'w SpriteComponent> {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::Sprite && s.enabled)
        .find_map(|s| world.get::<SpriteComponent>(s.entity))
}

/// ノードの見た目の矩形の 4 隅（親の行列を掛けた空間）。キャンバス領域・レイアウトの矩形 → 最初の Sprite の順。
/// どちらも無ければ None。
pub fn node_rect_corners(
    actor: &Actor,
    world: &World,
    parent_world_rs: [[f32; 4]; 4],
    placement: &CanvasNodePlacement,
) -> Option<[[f32; 3]; 4]> {
    if placement.canvas_base.is_some() || placement.layout_rect.is_some() {
        return Some(canvas_area_corners(parent_world_rs, &placement.eff_transform, placement.eff_size));
    }
    let sprite = first_sprite(actor, world)?;
    Some(sprite_rect_corners(
        parent_world_rs,
        &placement.eff_transform,
        placement.sprite_size(sprite.width, sprite.height),
    ))
}

/// ノードの範囲（キャンバス空間の AABB。矩形が無ければ位置の 1 点）。
pub fn node_bounds(actor: &Actor, world: &World, frame: &CanvasParentFrame, placement: &CanvasNodePlacement) -> ClipAabb {
    match node_rect_corners(actor, world, frame.world_rs, placement) {
        Some(corners) => corners_aabb(&corners),
        None => {
            let p = placement.eff_transform.position;
            let m = &frame.world_rs;
            let x = m[0][0] * p[0] + m[0][1] * p[1] + m[0][3];
            let y = m[1][0] * p[0] + m[1][1] * p[1] + m[1][3];
            ([x, y], [x, y])
        }
    }
}

/// 子のローカル（窓の子の文脈・平行移動の前）での、ノードの矩形のいちばん遠い端（右端・下端）。
pub fn local_far_edge(actor: &Actor, world: &World, placement: &CanvasNodePlacement) -> [f32; 2] {
    match node_rect_corners(actor, world, IDENTITY_MAT4, placement) {
        Some(corners) => corners_aabb(&corners).1,
        None => placement.eff_transform.position,
    }
}

/// 窓の大きさ（窓のローカルの画素）: 子が並ぶ箱（CanvasComponent・レイアウトの矩形）→ 最初の Sprite の矩形。
pub fn viewport_size(actor: &Actor, world: &World, placement: &CanvasNodePlacement) -> [f32; 2] {
    if let Some(size) = placement.box_size() {
        return size;
    }
    first_sprite(actor, world).map_or([0.0, 0.0], |s| {
        let size = placement.sprite_size(s.width, s.height);
        [size[0] * placement.eff_transform.scale[0], size[1] * placement.eff_transform.scale[1]]
    })
}

/// AABB の和（どちらかが None ならもう一方）【純関数】。
pub fn union(a: Option<ClipAabb>, b: Option<ClipAabb>) -> Option<ClipAabb> {
    match (a, b) {
        (Some((amin, amax)), Some((bmin, bmax))) => Some((
            [amin[0].min(bmin[0]), amin[1].min(bmin[1])],
            [amax[0].max(bmax[0]), amax[1].max(bmax[1])],
        )),
        (a, None) => a,
        (None, b) => b,
    }
}

/// AABB を軸ごとの余白だけ広げる【純関数】。
pub fn expand(a: ClipAabb, margin: [f32; 2]) -> ClipAabb {
    ([a.0[0] - margin[0], a.0[1] - margin[1]], [a.1[0] + margin[0], a.1[1] + margin[1]])
}

/// 2 つの AABB が交わる（接するを含む）か【純関数】。
pub fn intersects(a: ClipAabb, b: ClipAabb) -> bool {
    a.0[0] <= b.1[0] && b.0[0] <= a.1[0] && a.0[1] <= b.1[1] && b.0[1] <= a.1[1]
}
