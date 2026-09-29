// ============================================================
//  canvas_layout/results.rs — 前のフレームの描画のレイアウトの表（スクリプトが読む ECS の資源。W2 Item 4）
//
//  【何をするか】
//  フレームの描画（app/frame_renderer.rs）がフレームに 1 回作るメインの 2D キャンバスの表を、描画の後に
//  シーンの World の資源 `CanvasLayoutResults` へ移す。スクリプトの CanvasTransform.HasLayout・LayoutSize・LayoutRect
//  （scripting/canvas_layout_results_api.rs）は、この資源から行を引いて node_extent.rs で大きさと矩形を求める。
//
//  【1 フレーム遅れ】表はスクリプトのフェーズの後（描画）で作るので、スクリプトが Update などで読む値は必ず
//  **前のフレームの描画**が作った表のもの（CanvasScroll の窓の大きさと同じ流儀）。まだ描画していないとき
//  （Play の最初のフレーム・シーンを読み込んで最初の描画の前）と、表に無いノードは HasLayout = false。
//
//  【どの表を渡すか】（`LayoutFrameView`）
//    - Play のゲームの画面（エディタに埋め込んだ Play・SEED.exe の単体の Play。一時停止していない）の表だけを渡す
//    - Play の一時停止中（エディタの見た目で描く＝ゲームの画面ではない）は資源をそのままにする（再開した最初のフレームも
//      止める前の表を読める）
//    - エディタの Edit・アクター編集タブ・サムネイルの撮影は資源を空にする（前の Play の表を次の Play へ持ち越さない）
//    - 3D ワールドキャンバスの子の表（build_world_canvas_layout）は渡さない。メインの表の行のうち 3D ワールドキャンバス
//      （CanvasTransform を持たない 3D アクター）の下の行は `in_2d_tree` が false なので、スクリプトへ見せない
//  資源はシーンの World にあるので、シーンを差し替えれば（install_loaded_scene）古いシーンの表ごと消える
//  （別のシーンの Entity と取り違えない）。
//
//  【費用】表は描画が作ったものを Arc で受け取るだけ（写しを作らない）。Entity → 行の索引は最初に読まれたときにだけ作る
//  （OnceLock）。スクリプトが一度も読まなければ、フレームごとの追加の仕事は Arc の受け渡しと資源の差し替えだけ。
// ============================================================

use std::collections::HashMap;
use std::sync::{Arc, OnceLock};

use crate::engine::ecs::{Entity, World};

use super::node_extent::{node_extent, OwnSize};
use super::table::{CanvasLayoutNode, CanvasLayoutTable};

/// ビューポートの半分（キャンバスのワールド座標〈画面の中央が原点〉→ 画面の画素〈左上が原点〉の換算）。
const HALF_VIEWPORT: f32 = 0.5;

/// 表の行のうち、スクリプトへ見せる行か【純関数】。
///
/// 配置を持ち（CanvasTransform を持つ・フォルダでない）、2D レイアウト木の中（3D ワールドキャンバス・3D アクターの下でない）で、
/// 自身と祖先のすべてが表の世界線にいる行だけ。非表示・非アクティブ・スクロールの見える範囲の外の行も見せる（配置は求めてある）。
pub fn is_script_visible(node: &CanvasLayoutNode) -> bool {
    node.placement().is_some() && node.flags.in_2d_tree && node.flags.world_line_chain
}

/// スクリプトへ見せる 1 ノードぶんのレイアウト（値は前のフレームの描画のもの）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NodeLayoutReadout {
    /// 大きさ（ノードのキャンバスの単位。LayoutSize）。
    pub size: [f32; 2],
    /// 画面の矩形（x, y, 幅, 高さ。画素・左上が原点・Y 下向き。LayoutRect）。
    pub screen_rect: [f32; 4],
}

/// 前のフレームの描画のレイアウトの表（シーンの World の資源）。
#[derive(Debug, Default)]
pub struct CanvasLayoutResults {
    /// 表（None = まだ描画していない・渡す表が無い）。
    table: Option<Arc<CanvasLayoutTable>>,
    /// 表の基準ビューポート（画素。描画ターゲット〈内部解像度の固定ではその解像度〉の大きさ）。
    viewport_px: [f32; 2],
    /// Entity → 行の添字（スクリプトへ見せる行だけ。最初に読まれたときにだけ作る）。
    index: OnceLock<HashMap<Entity, u32>>,
}

impl CanvasLayoutResults {
    /// 表を持つ資源を作る（索引はまだ作らない）。
    ///
    /// # 引数
    /// * `table`       - フレームの描画が作ったメインの 2D キャンバスの表
    /// * `viewport_px` - 表の基準ビューポート（画素）
    pub fn new(table: Arc<CanvasLayoutTable>, viewport_px: [f32; 2]) -> Self {
        Self { table: Some(table), viewport_px, index: OnceLock::new() }
    }

    /// 表を持っているか。
    pub fn has_table(&self) -> bool {
        self.table.is_some()
    }

    /// 索引を作ったか（テスト・診断用。スクリプトが読むまで false のまま）。
    pub fn index_built(&self) -> bool {
        self.index.get().is_some()
    }

    /// スクリプトへ見せる行を引く（表が無い・表に無い・見せない行は None）。最初の呼び出しで索引を作る。
    ///
    /// # 引数
    /// * `entity` - アクター本体の entity（CanvasTransform の持ち主）
    pub fn laid_out_node(&self, entity: Entity) -> Option<&CanvasLayoutNode> {
        let table = self.table.as_deref()?;
        let index = self.index.get_or_init(|| build_index(table));
        let row = *index.get(&entity)?;
        table.nodes.get(row as usize)
    }

    /// 表にこのノードがあったか（スクリプトの HasLayout）。
    pub fn has_layout(&self, entity: Entity) -> bool {
        self.laid_out_node(entity).is_some()
    }

    /// ノードのレイアウトの大きさと画面の矩形（スクリプトの LayoutSize・LayoutRect）。
    ///
    /// # 引数
    /// * `entity` - アクター本体の entity
    /// * `own`    - ノードの自分の大きさを読む関数（レイアウトが大きさを決めていない軸があるときだけ呼ぶ）
    pub fn readout(&self, entity: Entity, own: impl FnOnce() -> OwnSize) -> Option<NodeLayoutReadout> {
        let row = self.laid_out_node(entity)?;
        let placement = row.placement()?;
        let extent = node_extent(&row.frame, placement, own);
        Some(NodeLayoutReadout {
            size: extent.size,
            screen_rect: world_rect_to_screen(extent.world_rect.min, extent.world_rect.size(), self.viewport_px),
        })
    }
}

/// スクリプトへ見せる行の Entity → 行の添字を作る（同じ entity が 2 度あれば先の行。表は木と 1 対 1 なので通常は無い）。
fn build_index(table: &CanvasLayoutTable) -> HashMap<Entity, u32> {
    let mut index = HashMap::with_capacity(table.nodes.len());
    for (row, node) in table.nodes.iter().enumerate() {
        if is_script_visible(node) {
            index.entry(node.entity).or_insert(row as u32);
        }
    }
    index
}

/// キャンバスのワールド座標の矩形（左上と大きさ）を画面の画素の矩形（x, y, 幅, 高さ）へ換算する【純関数】。
///
/// Play・スクリーンスペースの合成では、オーバーレイのカメラが描画ターゲット全体を中央原点で写すので、
/// 画素 = ワールド + ビューポート ÷ 2（units.rs の target_rect_to_canvas_world の逆。ScreenPosition と同じ基準）。
///
/// # 引数
/// * `min`         - 左上（キャンバスのワールド座標）
/// * `size`        - 大きさ（画素）
/// * `viewport_px` - 表の基準ビューポート（画素）
pub fn world_rect_to_screen(min: [f32; 2], size: [f32; 2], viewport_px: [f32; 2]) -> [f32; 4] {
    [
        min[0] + viewport_px[0] * HALF_VIEWPORT,
        min[1] + viewport_px[1] * HALF_VIEWPORT,
        size[0],
        size[1],
    ]
}

/// フレームの描画がどの画面を描いたか（スクリプトへ表を渡すかの判定の入力）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LayoutFrameView {
    /// Play のゲームの画面（エディタに埋め込んだ Play・SEED.exe の単体の Play。一時停止していない）。
    GameScreen,
    /// Play の一時停止中（エディタの見た目で描く。ゲームの画面ではない）。
    PausedGame,
    /// それ以外（エディタの Edit・アクター編集タブ・サムネイルの撮影）。
    NotGame,
}

impl LayoutFrameView {
    /// フレームの状態から決める【純関数】。
    ///
    /// # 引数
    /// * `playing`    - Play か（RuntimeMode::Play）
    /// * `paused`     - エディタの一時停止（PAUSE。エディタの見た目で描く）か
    /// * `other_view` - ゲームの画面でない世界線を描いているか（アクター編集タブ・サムネイルの撮影）
    pub fn classify(playing: bool, paused: bool, other_view: bool) -> Self {
        match (playing, paused, other_view) {
            (false, _, _) => Self::NotGame,
            (true, true, _) => Self::PausedGame,
            (true, false, true) => Self::NotGame,
            (true, false, false) => Self::GameScreen,
        }
    }
}

/// フレームの描画の後に、資源をどうするか。
#[derive(Debug)]
pub enum CanvasLayoutHandoff {
    /// 何もしない（描画しなかった・一時停止の見た目。前の表を読み続ける）。
    Keep,
    /// このフレームの表を渡す（Play のゲームの画面の表）。
    Publish {
        /// 表（描画が使ったものを共有する。写さない）。
        table: Arc<CanvasLayoutTable>,
        /// 表の基準ビューポート（画素）。
        viewport_px: [f32; 2],
    },
    /// 渡す表が無い（Edit・キャンバスの無いシーン・アクター編集タブ）。前の表も捨てる。
    Clear,
}

impl CanvasLayoutHandoff {
    /// フレームの画面と表から決める【純関数】。
    ///
    /// # 引数
    /// * `view`        - フレームの描画がどの画面を描いたか
    /// * `table`       - フレームのメインの 2D キャンバスの表（キャンバスの無いシーンは None）
    /// * `viewport_px` - 表の基準ビューポート（画素）
    pub fn for_frame(view: LayoutFrameView, table: Option<&Arc<CanvasLayoutTable>>, viewport_px: [f32; 2]) -> Self {
        match (view, table) {
            (LayoutFrameView::GameScreen, Some(table)) => Self::Publish { table: Arc::clone(table), viewport_px },
            (LayoutFrameView::GameScreen, None) | (LayoutFrameView::NotGame, _) => Self::Clear,
            (LayoutFrameView::PausedGame, _) => Self::Keep,
        }
    }

    /// シーンの World の資源へ当てる（資源があれば中身を差し替え、確保を毎フレーム作り直さない）。
    ///
    /// # 引数
    /// * `world` - シーンの World
    pub fn apply(self, world: &mut World) {
        match self {
            Self::Keep => {}
            Self::Publish { table, viewport_px } => {
                let results = CanvasLayoutResults::new(table, viewport_px);
                match world.resource_mut::<CanvasLayoutResults>() {
                    Some(slot) => *slot = results,
                    None => world.insert_resource(results),
                }
            }
            Self::Clear => {
                // 資源が無い・もう空なら何もしない（Edit のフレームごとに作り直さない）
                if let Some(slot) = world.resource_mut::<CanvasLayoutResults>() {
                    if slot.has_table() {
                        *slot = CanvasLayoutResults::default();
                    }
                }
            }
        }
    }
}
