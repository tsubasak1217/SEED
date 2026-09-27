// ============================================================
//  canvas_layout/table.rs — レイアウトの表（1 フレーム・1 文脈ぶん）
//
//  表はアクター木の深さ優先の並び（find_actor_by_dfs と同じ規則）で、ノード 1 つに 1 行を持つ。
//  表は値だけを持つ（アクターへの参照を持たない）ので、フレームの文脈として持ち回れる。
//  スロット（スプライト・テキスト等）を読むときは `iter_with_actors` でアクター木と並べて読む
//  （同じ木から作った表なら、並びは必ず一致する。一致しなければ反復を打ち切り、1 度だけ警告する）。
//
//  読み手ごとの「どのノードを扱うか」は、旧実装の再帰の打ち切り規則をフラグで表す:
//    - 描画・キャンバス枠・ID 描画 … 世界線・active・visible がすべて祖先まで真、かつ 2D レイアウト木の中
//    - 当たり判定                 … 同上（ただしエディタの選択は active を見ない）
//    - 2D 物理                     … すべてのノード（active だけを祖先まで追う）
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};

use crate::engine::core::renderer::ui_clip::UiClipId;
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;

use super::clip::CanvasClipRegion;
use super::frame::CanvasParentFrame;
use super::placement::CanvasNodePlacement;

/// 表とアクター木の並びが食い違ったことを 1 度だけ警告したか（ログ爆発防止）。
static MISMATCH_WARNED: AtomicBool = AtomicBool::new(false);

/// ノードの種類。
#[derive(Clone, Debug, PartialEq)]
pub enum CanvasNodeKind {
    /// フォルダ（レイアウト透明。受け取った文脈をそのまま子へ渡し、自身は何も描かない）。
    Folder,
    /// CanvasTransform を持たない（3D アクター等）。描画・当たり判定はここで打ち切る。
    /// 2D 物理だけが子孫をたどるので、子へ渡した素通しの文脈を持つ（placement::pass_through_frame）。
    NoTransform {
        /// 子へ渡した文脈（行列・累積スケール・ゾーンは親のまま、アンカー基準は自身の CanvasComponent）。
        child_frame: CanvasParentFrame,
    },
    /// CanvasTransform を持つ 2D ノード（配置を持つ）。
    Placed(CanvasNodePlacement),
}

/// 祖先までをたどったノードの状態（旧実装の「サブツリーごと省く」規則をフラグにしたもの）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CanvasNodeFlags {
    /// 自身と祖先のすべてが対象の世界線にいる。
    pub world_line_chain: bool,
    /// 自身と祖先のすべてが active。
    pub active_chain: bool,
    /// 自身と祖先のすべてが visible。
    pub visible_chain: bool,
    /// 2D レイアウト木の中にいる（フォルダ以外の祖先がすべて CanvasTransform を持つ）。
    pub in_2d_tree: bool,
}

/// 表の 1 行（ノード 1 つ）。
#[derive(Clone, Debug, PartialEq)]
pub struct CanvasLayoutNode {
    /// アクター本体の entity。
    pub entity: Entity,
    /// 親の行の添字（最上位は None）。
    pub parent: Option<u32>,
    /// このノードの子孫の行が終わる添字（この添字は含まない）。
    pub subtree_end: u32,
    /// 階層の深さ（フォルダを数えない。当たり判定の「子を優先」の順位に使う）。
    pub depth: u32,
    /// 親から受け取った文脈（フォルダはこれをそのまま子へ渡す）。
    pub frame: CanvasParentFrame,
    /// ノードの種類と配置。
    pub kind: CanvasNodeKind,
    /// 祖先までをたどった状態。
    pub flags: CanvasNodeFlags,
    /// このノード自身の描画アイテムが入る切り抜きの番号（いちばん内側の祖先の領域。無ければ None）。
    pub clip: Option<UiClipId>,
    /// このノードが子孫を切り抜く領域の番号（切り抜きのコンポーネントが有効で矩形が決まったときだけ Some）。
    pub own_clip_region: Option<UiClipId>,
}

impl CanvasLayoutNode {
    /// 配置（CanvasTransform を持つ 2D ノードだけ Some）。
    #[inline]
    pub fn placement(&self) -> Option<&CanvasNodePlacement> {
        match &self.kind {
            CanvasNodeKind::Placed(p) => Some(p),
            _ => None,
        }
    }

    /// フォルダか。
    #[inline]
    pub fn is_folder(&self) -> bool {
        matches!(self.kind, CanvasNodeKind::Folder)
    }

    /// 描画・キャンバス枠・ID 描画の対象か（旧実装の再帰の打ち切り規則: 世界線・active・visible が
    /// 祖先まで真で、2D レイアウト木の中）。フォルダも true になりうる（座標空間の登録に使う）。
    #[inline]
    pub fn is_drawn(&self) -> bool {
        let f = &self.flags;
        f.world_line_chain && f.active_chain && f.visible_chain && f.in_2d_tree
    }

    /// 当たり判定の対象になりうるか（visible と世界線は常に見る。active は読み手が選ぶ）。
    ///
    /// # 引数
    /// * `respect_active` - true: 非アクティブを除く（ポインタイベント）／false: 含める（エディタの選択）
    #[inline]
    pub fn is_pickable(&self, respect_active: bool) -> bool {
        let f = &self.flags;
        f.world_line_chain && f.visible_chain && f.in_2d_tree && (!respect_active || f.active_chain)
    }
}

/// レイアウトの部品（W2-1b）が 1 回の走査でした仕事の数（性能のテストと診断用）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CanvasLayoutStats {
    /// 並べたコンテナの数。
    pub containers: u32,
    /// レイアウトが矩形を割り当てて置いたノードの数（コンテナの子・親に合わせた子）。
    pub placed_by_layout: u32,
    /// 安全領域で縮めたノードの数。
    pub safe_areas: u32,
    /// 子の大きさを実際に測った回数（覚えた結果を引いただけの回数は含まない。ノード数に比例すること）。
    pub measure_calls: u64,
}

/// レイアウトの表（1 フレーム・1 文脈ぶん）。
#[derive(Clone, Debug, Default, PartialEq)]
pub struct CanvasLayoutTable {
    /// 表の行（深さ優先の並び。添字が DFS 番号）。
    pub nodes: Vec<CanvasLayoutNode>,
    /// 切り抜きの領域（番号 = 添字。キャンバス空間）。
    pub clip_regions: Vec<CanvasClipRegion>,
    /// 表を作った世界線（アクター木と並べて読むときのルートの絞り込み）。
    pub world_line: u32,
    /// レイアウトの部品の仕事の数（W2-1b）。
    pub stats: CanvasLayoutStats,
}

impl CanvasLayoutTable {
    /// 行の数。
    #[inline]
    pub fn len(&self) -> usize {
        self.nodes.len()
    }

    /// 行が 1 つも無いか。
    #[inline]
    pub fn is_empty(&self) -> bool {
        self.nodes.is_empty()
    }

    /// 表の行とアクターを深さ優先の並びで組にして返す反復子。
    ///
    /// # 引数
    /// * `roots` - 表を作ったときと同じルートの並び（世界線での絞り込みは反復子が行う）
    ///
    /// 表とアクター木の並びが食い違ったら（表を作った後に木が変わった等）そこで打ち切り、
    /// 1 度だけ警告を出す（別のアクターへ値を当てるよりは描かない方が害が小さい）。
    pub fn iter_with_actors<'t, 'a>(&'t self, roots: &'a [Actor]) -> NodeActorIter<'t, 'a> {
        NodeActorIter {
            table: self,
            stack: vec![roots.iter()],
            index: 0,
            world_line: self.world_line,
        }
    }
}

/// `CanvasLayoutTable::iter_with_actors` の反復子（アクター木を明示のスタックでたどる）。
pub struct NodeActorIter<'t, 'a> {
    /// 読む表。
    table: &'t CanvasLayoutTable,
    /// 子の並びのスタック（先頭はルートの並び）。
    stack: Vec<std::slice::Iter<'a, Actor>>,
    /// 次に読む行の添字。
    index: usize,
    /// ルートを絞り込む世界線。
    world_line: u32,
}

impl<'t, 'a> Iterator for NodeActorIter<'t, 'a> {
    type Item = (&'t CanvasLayoutNode, &'a Actor);

    fn next(&mut self) -> Option<Self::Item> {
        loop {
            let depth = self.stack.len();
            let top = self.stack.last_mut()?;
            let Some(actor) = top.next() else {
                self.stack.pop();
                continue;
            };
            // ルートの並びだけ世界線で絞り込む（子は世界線を問わず数える＝find_actor_by_dfs と同じ）
            if depth == 1 && actor.world_line != self.world_line {
                continue;
            }
            let Some(node) = self.table.nodes.get(self.index) else {
                warn_mismatch("表の行が足りません");
                self.stack.clear();
                return None;
            };
            if node.entity != actor.entity {
                warn_mismatch("表とアクター木の並びが一致しません");
                self.stack.clear();
                return None;
            }
            self.index += 1;
            self.stack.push(actor.children.iter());
            return Some((node, actor));
        }
    }
}

/// 表とアクター木の食い違いを 1 度だけ警告する。
fn warn_mismatch(reason: &str) {
    if !MISMATCH_WARNED.swap(true, Ordering::Relaxed) {
        eprintln!(
            "[SEED canvas_layout] {reason}（表を作った後にアクター木が変わった可能性があります。以降の同じ警告は出しません）"
        );
    }
}
