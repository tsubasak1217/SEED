// ============================================================
//  canvas_scroll/visibility.rs — スクロールの見える範囲の外を飛ばす（W2-3）
//
//  【規則】（docs/ui_scroll_list.md §5）
//    - 対象: 切り抜き（CanvasClipComponent）と「見える範囲の外を飛ばす」（cull_outside。既定 true）が両方有効な
//      スクロールの窓の子孫だけ。切り抜きの外なので、飛ばしても見た目は変わらない
//    - 見える範囲 = 窓の切り抜きの AABB を cache_extent（既定 250 dp）だけ広げたもの（キャンバス空間）
//    - 子孫の「部分木の範囲」（そのノードと子孫の範囲の和。矩形の無いノードは位置の 1 点）が見える範囲と交わらなければ、
//      その部分木をまるごと飛ばす（表の行の `culled`。描画アイテム・ID 描画・当たり判定・ジェスチャーを作らない）
//    - 交わる部分木はその中へ降りて、子の部分木ごとに同じ判定をする（1,000 行の一覧なら行ごとに判定される）
//  表の行はアクター木の深さ優先の並びなので、部分木は [行, subtree_end) の連続した範囲になる。
// ============================================================

use std::collections::HashMap;

use crate::engine::core::canvas_layout::scroll_view::{intersects, ClipAabb};
use crate::engine::core::canvas_layout::CanvasLayoutNode;

/// 行の範囲 [first, end) の部分木のうち、見える範囲と交わらないものに `culled` を立てる【純関数（表を書く）】。
///
/// # 引数
/// * `nodes`  - 表の行
/// * `first` / `end` - 調べる行の範囲（窓の子孫。窓の行は含めない）
/// * `bounds` - 行 → 部分木の範囲（キャンバス空間。範囲の分からない行は無い＝飛ばさずに中へ降りる）
/// * `view`   - 見える範囲（余白を含む）
///
/// # 戻り値
/// 新しく飛ばした行の数。
pub fn cull_outside(
    nodes: &mut [CanvasLayoutNode],
    first: usize,
    end: usize,
    bounds: &HashMap<u32, ClipAabb>,
    view: ClipAabb,
) -> u32 {
    let end = end.min(nodes.len());
    let mut culled = 0;
    let mut row = first;
    while row < end {
        let subtree_end = (nodes[row].subtree_end as usize).clamp(row + 1, end);
        match bounds.get(&(row as u32)) {
            Some(b) if !intersects(*b, view) => {
                for node in &mut nodes[row..subtree_end] {
                    if !node.culled {
                        node.culled = true;
                        culled += 1;
                    }
                }
                row = subtree_end;
            }
            _ => row += 1,
        }
    }
    culled
}
