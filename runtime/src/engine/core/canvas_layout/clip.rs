// ============================================================
//  canvas_layout/clip.rs — 切り抜き（クリップ）の領域（W2-1a で本番化）
//
//  「子を切り抜く」コンポーネント（CanvasClipComponent）を持つノードは、自身のレイアウトの
//  矩形で子孫を切る。領域はレイアウトの走査（pass.rs）が表へ積み、各ノードは
//  「自分の描画アイテムが入る切り抜きの番号」（いちばん内側の祖先の領域）を持つ。
//  ノード自身の描画アイテムは自分の領域では切らない（自分の背景＝切り抜きの矩形そのもの）。
//
//  【矩形の決め方】（ClipRectSource）
//    1. CanvasComponent を持つ … キャンバス領域（エディタのキャンバス枠・Canvas の当たり判定と同じ矩形）
//    2. 持たない               … 最初の有効な SpriteComponent の矩形（W2-0 の試作と同じ。背景の板を枠にする）
//    3. どちらも無い           … 切らない（領域を積まない）
//
//  【座標】領域の 4 隅はキャンバス空間（キャンバス px・Y 下向き。3D ワールドキャンバスでは親の行列の z も持つ）。
//  描画はワールド座標（canvas_scale と y_sign を掛けたもの）へ写して renderer/ui_clip.rs の scissor へ、
//  当たり判定はキャンバス空間のまま「祖先のすべての領域の AABB の内側」を調べる。
//  回転したノードは 4 隅の AABB で切る（scissor と同じ。正確に切るのはステンシルが要る。roadmap §3.8.4）。
// ============================================================

use crate::engine::components::CanvasTransform;
use crate::engine::core::renderer::ui_clip::{UiClipId, UiClipRegion, MAX_CLIP_REGIONS};
use crate::engine::methods::gizmo_interact::mat4x4_mul;

/// 領域の 4 隅を取るローカル座標の割合（(0,0)・(1,0)・(0,1)・(1,1)。renderer/ui_clip.rs と同じ並び）。
const RECT_CORNER_FRACTIONS: [[f32; 2]; 4] = [[0.0, 0.0], [1.0, 0.0], [0.0, 1.0], [1.0, 1.0]];

/// 切り抜きの矩形の出どころ（ノードのどの矩形で子孫を切ったか）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ClipRectSource {
    /// CanvasComponent のキャンバス領域。
    CanvasArea,
    /// 最初の有効な SpriteComponent の矩形（CanvasComponent が無いとき）。
    FirstSprite,
}

/// 切り抜きの 1 領域（キャンバス空間）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CanvasClipRegion {
    /// 矩形の 4 隅（キャンバス空間 xyz。並びは (0,0)・(1,0)・(0,1)・(1,1)）。
    pub corners: [[f32; 3]; 4],
    /// 外側の切り抜き（入れ子の親。無ければ None）。
    pub parent: Option<UiClipId>,
    /// この領域を作ったノード（表の添字）。
    pub owner: u32,
    /// 矩形の出どころ。
    pub source: ClipRectSource,
}

/// 行優先の行列で、ローカルの矩形 [0,w]×[0,h] の 4 隅をキャンバス空間へ写す【純関数】。
///
/// # 引数
/// * `m`    - ローカル → キャンバス空間の行列（行優先）
/// * `size` - ローカルの矩形の大きさ（ユニットクワッド用の行列なら 1×1）
pub fn rect_corners(m: &[[f32; 4]; 4], size: [f32; 2]) -> [[f32; 3]; 4] {
    RECT_CORNER_FRACTIONS.map(|[fx, fy]| {
        let (lx, ly) = (size[0] * fx, size[1] * fy);
        let mut out = [0.0f32; 3];
        for (row, value) in out.iter_mut().enumerate() {
            *value = m[row][0] * lx + m[row][1] * ly + m[row][3];
        }
        out
    })
}

/// キャンバス領域の 4 隅（エディタのキャンバス枠・Canvas の当たり判定と同じ行列）【純関数】。
///
/// # 引数
/// * `parent_world_rs` - 親のワールド行列
/// * `eff_transform`   - ノードの有効トランスフォーム
/// * `eff_size`        - キャンバス領域の実効の大きさ
pub fn canvas_area_corners(
    parent_world_rs: [[f32; 4]; 4],
    eff_transform: &CanvasTransform,
    eff_size: [f32; 2],
) -> [[f32; 3]; 4] {
    let m = mat4x4_mul(parent_world_rs, eff_transform.to_mat4_sized(eff_size[0], eff_size[1]));
    rect_corners(&m, eff_size)
}

/// スプライトの矩形の 4 隅（スプライトの描画と同じ行列でユニットクワッドを写す）【純関数】。
///
/// # 引数
/// * `parent_world_rs` - 親のワールド行列
/// * `eff_transform`   - ノードの有効トランスフォーム
/// * `sprite_eff_size` - スプライトの実効の大きさ（大きさ × サイズ倍率）
pub fn sprite_rect_corners(
    parent_world_rs: [[f32; 4]; 4],
    eff_transform: &CanvasTransform,
    sprite_eff_size: [f32; 2],
) -> [[f32; 3]; 4] {
    let m = mat4x4_mul(
        parent_world_rs,
        eff_transform.to_sprite_mat4(sprite_eff_size[0], sprite_eff_size[1]),
    );
    rect_corners(&m, [1.0, 1.0])
}

/// 4 隅の XY の AABB（min, max）【純関数】。
pub fn corners_aabb(corners: &[[f32; 3]; 4]) -> ([f32; 2], [f32; 2]) {
    let mut min = [f32::INFINITY; 2];
    let mut max = [f32::NEG_INFINITY; 2];
    for c in corners {
        min = [min[0].min(c[0]), min[1].min(c[1])];
        max = [max[0].max(c[0]), max[1].max(c[1])];
    }
    (min, max)
}

/// 点（キャンバス空間）が、切り抜きの番号とそのすべての祖先の領域の内側にあるか【純関数】。
///
/// 当たり判定（pick_2d・ポインタイベント）が使う。描画の scissor と同じく各領域の 4 隅の AABB で調べ、
/// 境界上は内側とみなす（スプライトの当たり判定 `hit_test_rect_2d` と同じ）。
///
/// # 引数
/// * `regions` - 領域の表
/// * `clip`    - 調べる切り抜きの番号（None なら切り抜きの外＝常に内側）
/// * `point`   - キャンバス空間の点
pub fn point_inside_clip_chain(
    regions: &[CanvasClipRegion],
    clip: Option<UiClipId>,
    point: [f32; 2],
) -> bool {
    let mut current = clip;
    // 親は必ず子より先に積まれる（番号が小さい）ので、鎖は表の長さ以内で終わる
    for _ in 0..regions.len() {
        let Some(id) = current else { return true };
        let Some(region) = regions.get(id as usize) else { return true };
        let (min, max) = corners_aabb(&region.corners);
        let inside = point[0] >= min[0] && point[0] <= max[0] && point[1] >= min[1] && point[1] <= max[1];
        if !inside {
            return false;
        }
        current = region.parent;
    }
    true
}

/// 領域を描画のワールド座標の領域へ写す【純関数】（`canvas_collect::canvas_mat_to_gpu` と同じ写像）。
///
/// # 引数
/// * `region`       - キャンバス空間の領域
/// * `canvas_scale` - キャンバス px → ワールドの倍率（スクリーンスペースは 1）
/// * `y_sign`       - Y の向き（スクリーンスペースは 1、ワールドスペースは -1）
pub fn to_render_region(region: &CanvasClipRegion, canvas_scale: f32, y_sign: f32) -> UiClipRegion {
    let csy = canvas_scale * y_sign;
    UiClipRegion {
        corners: region
            .corners
            .map(|[x, y, z]| [x * canvas_scale, y * csy, z]),
        parent: region.parent,
    }
}

/// 領域の表に 1 つ積めるか（番号が u16 に収まる数まで）。
#[inline]
pub fn has_room(regions: &[CanvasClipRegion]) -> bool {
    regions.len() < MAX_CLIP_REGIONS
}
