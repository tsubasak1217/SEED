// ============================================================
//  canvas_layout_results_api.rs — CanvasTransform の読み取り専用の欄（前のフレームの描画のレイアウト。W2 Item 4）
//
//  C# の SEED.CanvasTransform.HasLayout・LayoutSize・LayoutRect が、汎用のレジストリ（host_api.rs の read_floats の
//  "CanvasTransform" の分岐）を通して名前で読む欄。値の出どころはシーンの World の資源 CanvasLayoutResults
//  （canvas_layout/results.rs。前のフレームの描画が作った Play のゲームの画面の表）。書き込みは無い（write_floats に足さない）。
//
//  【データ表現】（host_api.rs 冒頭の規約どおり、すべて f32 の配列）
//    has_layout  … 1 要素（0/1）。表にこのノードがあったか
//    layout_size … 2 要素（幅, 高さ。ノードのキャンバスの単位＝Sprite.Width/Height と同じ。dp のキャンバスなら dp）
//    layout_rect … 4 要素（x, y, 幅, 高さ。画面の画素・左上が原点・Y 下向き。ScreenPosition・Screen.SafeArea と同じ座標）
//  表に無いノード（まだ描画していない・3D ワールドキャンバスの下など）は has_layout = 0、ほかの 2 つは 0 を並べる
//  （CanvasScroll の viewport_size が大きさの分かる前は 0 を返すのと同じ流儀）。
//  C# 側 scripting/src/Api/CanvasTransform.cs の欄の名前・要素数と一致させること。
// ============================================================

use crate::engine::core::canvas_layout::{CanvasLayoutResults, OwnSize};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

/// 欄: 表にこのノードがあったか（1 要素。0/1）。
pub const FIELD_HAS_LAYOUT: &str = "has_layout";
/// 欄: レイアウトの大きさ（2 要素。ノードのキャンバスの単位）。
pub const FIELD_LAYOUT_SIZE: &str = "layout_size";
/// 欄: 画面の矩形（4 要素。x・y・幅・高さの画素）。
pub const FIELD_LAYOUT_RECT: &str = "layout_rect";

/// layout_size の要素数（幅, 高さ）。
const SIZE_LEN: usize = 2;
/// layout_rect の要素数（x, y, 幅, 高さ）。
const RECT_LEN: usize = 4;

/// bool の真（0/1 の表現）。
const TRUE_VALUE: f32 = 1.0;
/// bool の偽（0/1 の表現）。
const FALSE_VALUE: f32 = 0.0;

/// この欄を受け持つか（host_api の "CanvasTransform" の分岐が振り分けに使う）。
pub fn is_field(field: &str) -> bool {
    matches!(field, FIELD_HAS_LAYOUT | FIELD_LAYOUT_SIZE | FIELD_LAYOUT_RECT)
}

/// 値を out へ写して要素数を返す（out が短ければ None）。
fn put(out: &mut [f32], v: &[f32]) -> Option<usize> {
    out.get_mut(..v.len())?.copy_from_slice(v);
    Some(v.len())
}

/// 欄を読む。
///
/// # 引数
/// * `world`  - スクリプトのフェーズの World（シーンの World。資源 CanvasLayoutResults を持つ）
/// * `entity` - アクター本体の entity（CanvasTransform の持ち主）
/// * `actor`  - アクターを引く関数（レイアウトが大きさを決めていない軸で、Sprite・Text の枠を読むときだけ呼ぶ）
/// * `field`  - 欄の名前（`is_field` が true のもの）
/// * `out`    - 書き出し先（4 要素以上）
///
/// # 戻り値
/// 書いた要素数。知らない欄なら None。
pub fn read<'a>(
    world: &World,
    entity: Entity,
    actor: impl FnOnce() -> Option<&'a Actor>,
    field: &str,
    out: &mut [f32],
) -> Option<usize> {
    let results = world.resource::<CanvasLayoutResults>();
    match field {
        FIELD_HAS_LAYOUT => {
            let has = results.is_some_and(|r| r.has_layout(entity));
            put(out, &[if has { TRUE_VALUE } else { FALSE_VALUE }])
        }
        FIELD_LAYOUT_SIZE | FIELD_LAYOUT_RECT => {
            // 自分の大きさ（Sprite・Text の枠）は要るときだけスロットから読む（アクターが引けなければ何も無いのと同じ）
            let own = || actor().map_or_else(OwnSize::default, |a| OwnSize::of(a, world));
            let readout = results.and_then(|r| r.readout(entity, own));
            if field == FIELD_LAYOUT_SIZE {
                put(out, &readout.map_or([0.0; SIZE_LEN], |r| r.size))
            } else {
                put(out, &readout.map_or([0.0; RECT_LEN], |r| r.screen_rect))
            }
        }
        _ => None,
    }
}
