// ============================================================
//  canvas_layout/ — 2D キャンバスノードのレイアウト計算（W2-1a で一本化）
//
//  【何をするか】
//  2D キャンバスのアクター木を 1 回だけ深さ優先でたどり、各ノードの
//  「親から受け取った文脈（アンカー基準・ワールド行列・累積スケール・描画ゾーン）」と
//  「自身の配置（有効トランスフォーム・サイズ倍率・キャンバス領域・子へ渡す文脈）」、
//  切り抜き（クリップ）の番号と領域の表を 1 つの表（CanvasLayoutTable）にまとめる。
//
//  描画（スプライト・テキスト・パーティクル・SEED.Draw の座標空間）、エディタのキャンバス枠、
//  GPU の ID 描画、CPU の当たり判定（pick_2d・ポインタイベント）、2D 物理とギズモは、
//  以前はそれぞれが同じ 60〜80 行の計算（root_auto 上書き → eff_viewport → アンカー →
//  eff_ct → size_scale → self_world_rs → 子への継承）を複製して持っていた
//  （docs/backlog.md「2D ノードのレイアウト計算が 5 か所に重複コピーされている」）。
//  今はこの表を読むだけにして、計算は placement.rs の純関数 `resolve` の 1 か所にある。
//
//  【構成】（1 ファイル 1 責務）
//    anchor.rs    … アンカーの基準サイズとオフセットの規則（最上位＝ビューポート基準／子＝親キャンバス基準）
//    frame.rs     … 親から子へ渡す文脈（CanvasParentFrame）と走査全体の入力（CanvasLayoutEnv）
//    placement.rs … ノード 1 つの配置を求める純関数 resolve（副作用なし）と、矩形への配置・箱の置き換え（W2-1b）
//    clip.rs      … 切り抜きの領域（キャンバス空間の 4 隅と入れ子の親）と、点が切り抜きの内側かの判定
//    table.rs     … 表の型（CanvasLayoutNode / CanvasLayoutTable）と、アクター木と表を並べて読む反復子
//    pass.rs      … 木を 1 回たどって表を作る走査（CanvasLayoutPass）
//    ── W2-1b（レイアウトの部品・dp・安全領域）──
//    containers/  … コンテナの並べ方の純関数（stack.rs・wrap.rs・grid.rs）とコンポーネントからの指定（spec.rs）
//    measure.rs   … ノードの「自分の大きさ」を測る（コンテナの 2 段の計算の 1 段目。結果を覚える）
//    lookup.rs    … レイアウトの部品・スプライト・テキストの枠の引き方
//    units.rs     … dp の換算と、走査が読む画面の情報（CanvasScreenEnv）
//    safe_area.rs … 安全領域で箱を縮める純関数
//    ── W2-3（スクロール）──
//    scroll_view.rs … スクロールの窓の平行移動・窓と中身の大きさ・ノードの範囲（見える範囲の外を飛ばす判定の材料）
//
//  【表の作り手と持ち主】（ECS の流儀: 表はフレームの文脈として作って読み手へ渡す）
//    - フレームの描画（frame_renderer）は、メインの 2D キャンバスの表をフレームに 1 回作り、
//      スプライト・テキスト等の収集とキャンバス枠と（同じ文脈なら）ID 描画で使い回す。
//    - 文脈（ビューポート・自動解像度の表・設計空間表示）が違う読み手（エディタのクリック選択・
//      Play のポインタイベント・2D 物理）は、同じ `CanvasLayoutPass` で自分の文脈の表を作る。
//      計算の実体は 1 つなので、文脈が同じなら値は必ず一致する。
//
//  【番号（DFS）】表の並びは `find_actor_by_dfs` と同じ規則（世界線の一致するルート → 自身 → 子を
//  深さ優先。子は世界線を問わず数える）。表の添字がそのまま DFS 番号になる。
// ============================================================

pub mod anchor;
pub mod clip;
pub mod containers;
pub mod frame;
pub mod lookup;
pub mod measure;
pub mod pass;
pub mod placement;
pub mod safe_area;
pub mod scroll_view;
pub mod table;
pub mod units;

#[cfg(test)]
mod tests;
#[cfg(test)]
mod layout_tests;
#[cfg(test)]
mod scroll_tests;

pub use anchor::{child_anchor_basis, node_anchor_offset, root_anchor_offset, NO_ANCHOR_BASIS};
pub use clip::{CanvasClipRegion, ClipRectSource};
pub use frame::{AutoScaleDivisor, CanvasLayoutEnv, CanvasParentFrame, IDENTITY_MAT4};
pub use pass::CanvasLayoutPass;
pub use placement::{biased_layer, resolve, resolve_in_rect, CanvasNodeInput, CanvasNodePlacement};
pub use safe_area::CanvasRect;
pub use scroll_view::CanvasScrollRegion;
pub use table::{CanvasLayoutNode, CanvasLayoutStats, CanvasLayoutTable, CanvasNodeFlags, CanvasNodeKind};
pub use units::{dp_scale_from_dpi, target_rect_to_canvas_world, CanvasScreenEnv};
