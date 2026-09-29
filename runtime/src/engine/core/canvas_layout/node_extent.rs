// ============================================================
//  canvas_layout/node_extent.rs — 表の 1 行から「レイアウトが決めたノードの大きさ」と矩形を求める【純関数】（W2 Item 4）
//
//  スクリプトの CanvasTransform.LayoutSize・LayoutRect の中身。表（CanvasLayoutTable）の行の配置
//  （CanvasNodePlacement）と親の文脈（CanvasParentFrame）だけから求める。画面の画素への換算は results.rs。
//
//  【大きさの決め方】（軸ごと。先にあるものが勝つ）
//    CanvasComponent を持つノード（canvas_base が Some）:
//      キャンバス領域。大きさ = 基準の大きさ（canvas_base。コンテナ・親に合わせる・セル・安全領域・中身に合わせる・
//      dp のルートを反映済み）、矩形 = キャンバス領域の実効の大きさ（eff_size。エディタのキャンバス枠・CanvasClip の矩形と同じ）
//    持たないノード:
//      1. レイアウトが大きさを決めた軸（sprite_fill: コンテナが伸ばした・セルいっぱい・親に合わせた）→ その大きさ ÷ サイズ倍率
//      2. 最初の有効な Sprite の大きさ（lookup.rs の first_sprite_size。レイアウトの走査の「自分の大きさ」と同じ関数）
//      3. 最初の有効な Text の枠（0 より大きい軸。lookup.rs の text_box_size）
//      4. レイアウトが割り当てた矩形（layout_rect。Sprite も Text も無いコンテナの子・親に合わせた子の、伸ばしていない軸）
//      5. 0
//      矩形は描くスプライトと同じ計算（clip.rs の sprite_rect_corners）なので、Sprite を持つノードでは
//      「大きさ × サイズ倍率 ＝ 描かれるスプライトの大きさ」になり、4 隅は描かれるスプライトの 4 隅と一致する。
//
//  【単位】
//    - 大きさ: ノードのキャンバスの単位（Sprite.Width・Height と同じ。親のローカルの画素 ÷ サイズ倍率。dp のキャンバスの下なら dp）。
//      CanvasComponent を持つノードはキャンバスの基準の大きさ（dp のルートキャンバス自身も dp）
//    - 矩形: キャンバスのワールド座標（Play・スクリーンスペースの合成では画面の中央が原点・Y 下向き・画素）の外接矩形。
//      自分の Rotation・Scale・pivot を含む（描画と同じ行列）。回転していれば 4 隅の外接矩形
// ============================================================

use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::clip::{corners_aabb, sprite_rect_corners};
use super::containers::{AXIS_X, AXIS_Y};
use super::frame::CanvasParentFrame;
use super::lookup::{first_sprite_size, text_box_size};
use super::placement::CanvasNodePlacement;
use super::safe_area::CanvasRect;

/// サイズ倍率が 0 とみなせる大きさ（画素 → 単位の換算で 0 除算しない。placement.rs・measure.rs と同じ値）。
const SCALE_EPSILON: f32 = 1e-12;

/// 大きさが何も決まらない軸の値（CanvasComponent・Sprite・Text の枠・レイアウトの矩形のどれも無い）。
const NO_EXTENT: f32 = 0.0;

/// ノードの自分の大きさ（キャンバスの単位）。レイアウトが大きさを決めていない軸にだけ使う。
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct OwnSize {
    /// 最初の有効な Sprite の大きさ（無ければ None）。
    pub sprite: Option<[f32; 2]>,
    /// 最初の有効な Text の枠（軸ごと。枠が 0 以下の軸は None）。
    pub text_box: [Option<f32>; 2],
}

impl OwnSize {
    /// ノードのスロットから読む（レイアウトの走査が「自分の大きさ」を測るのと同じ関数。無効なスロットは付いていないのと同じ）。
    ///
    /// # 引数
    /// * `actor` - ノード
    /// * `world` - コンポーネントの置き場
    pub fn of(actor: &Actor, world: &World) -> Self {
        Self { sprite: first_sprite_size(actor, world), text_box: text_box_size(actor, world) }
    }
}

/// ノード 1 つのレイアウトの大きさと矩形（`node_extent` の結果）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NodeExtent {
    /// 大きさ（ノードのキャンバスの単位。スクリプトの LayoutSize）。
    pub size: [f32; 2],
    /// 矩形の大きさ（親のローカルの画素。描くスプライト・キャンバス領域の大きさ）。
    pub px_size: [f32; 2],
    /// 矩形の 4 隅の外接矩形（キャンバスのワールド座標。スクリプトの LayoutRect の元）。
    pub world_rect: CanvasRect,
}

/// 親のローカルの画素をノードのキャンバスの単位へ換算する【純関数】（サイズ倍率が 0 の退化した軸は画素のまま）。
///
/// # 引数
/// * `px`         - 親のローカルの画素
/// * `size_scale` - その軸のサイズ倍率
fn px_to_units(px: f32, size_scale: f32) -> f32 {
    if size_scale.abs() > SCALE_EPSILON {
        px / size_scale
    } else {
        px
    }
}

/// CanvasComponent を持たないノードの 1 軸の（大きさ, 画素の大きさ）を決める【純関数】（冒頭の 1〜5 の順）。
///
/// # 引数
/// * `placement` - ノードの配置
/// * `own`       - ノードの自分の大きさ
/// * `axis`      - 軸（AXIS_X・AXIS_Y）
fn axis_extent(placement: &CanvasNodePlacement, own: &OwnSize, axis: usize) -> (f32, f32) {
    let scale = placement.size_scale[axis];
    // 1. レイアウトが大きさを決めた軸（描くスプライトもこの大きさ。placement.sprite_size と同じ規則）
    if let Some(px) = placement.sprite_fill[axis] {
        return (px_to_units(px, scale), px);
    }
    // 2. Sprite（描く大きさ = 幅・高さ × サイズ倍率。placement.sprite_size と同じ式）
    if let Some(sprite) = own.sprite {
        return (sprite[axis], sprite[axis] * scale);
    }
    // 3. Text の枠
    if let Some(text_box) = own.text_box[axis] {
        return (text_box, text_box * scale);
    }
    // 4. レイアウトが割り当てた矩形（描くものが無いノードの、伸ばしていない軸）
    if let Some(rect) = placement.layout_rect {
        return (px_to_units(rect[axis], scale), rect[axis]);
    }
    // 5. 何も無い
    (NO_EXTENT, NO_EXTENT)
}

/// 表の 1 行からノードのレイアウトの大きさと矩形を求める【純関数】。
///
/// # 引数
/// * `frame`     - 行の親の文脈（`CanvasLayoutNode.frame`。描画が使う親の行列）
/// * `placement` - 行の配置
/// * `own`       - ノードの自分の大きさを読む関数（CanvasComponent を持たず、レイアウトが大きさを決めていない軸があるときだけ呼ぶ）
///
/// # 戻り値
/// 大きさ（キャンバスの単位）・矩形の大きさ（親のローカルの画素）・外接矩形（キャンバスのワールド座標）。
pub fn node_extent(
    frame: &CanvasParentFrame,
    placement: &CanvasNodePlacement,
    own: impl FnOnce() -> OwnSize,
) -> NodeExtent {
    let (size, px_size) = match placement.canvas_base {
        // CanvasComponent: キャンバス領域（基準の大きさと、その実効の画素の大きさ）
        Some(base) => (base, placement.eff_size),
        None => {
            // レイアウトがすべての軸の大きさを決めていれば、スロットを読まない
            let own = if placement.sprite_fill.iter().all(Option::is_some) { OwnSize::default() } else { own() };
            let x = axis_extent(placement, &own, AXIS_X);
            let y = axis_extent(placement, &own, AXIS_Y);
            ([x.0, y.0], [x.1, y.1])
        }
    };
    // 描くスプライトと同じ行列（親の行列 × 有効トランスフォーム〈自分の Rotation・Scale・pivot〉）で 4 隅を写す。
    // キャンバス領域も同じ式で求まる（canvas_area_corners と同じ 4 隅）
    let corners = sprite_rect_corners(frame.world_rs, &placement.eff_transform, px_size);
    let (min, max) = corners_aabb(&corners);
    NodeExtent { size, px_size, world_rect: CanvasRect { min, max } }
}
