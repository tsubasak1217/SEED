// ============================================================
//  canvas_scroll/ — スクロールの本体（慣性・跳ね返り・スナップ・入れ子・見える範囲の外を飛ばす。W2-3）
//
//  【何をするか】（規則の正典は docs/ui_scroll_list.md）
//  CanvasScrollComponent（設定）を付けた 2D キャンバスのノードを「窓」にして、中身（子）をスクロールの位置だけずらす。
//  位置は指のドラッグ（W2-2 のジェスチャーアリーナ）・離した後の慣性・端の跳ね返り・スナップ・スクリプトの ScrollTo で動く。
//
//  【分担】（docs/app_platform_roadmap.md §3.2 の W2-P1 を W2-3 で具体化）
//    - Rust（ここ）: スクロールの物理と状態・入れ子の受け渡し・レイアウトへの反映・見える範囲の外を飛ばす
//      （レイアウトの走査の入力で、「動いている」の申告と 60 fps が要るため）
//    - C#（SEED.UI）: 一覧の仮想化（見えている行だけを作って使い回す）・スワイプの操作・部品の見た目
//
//  【構成】（1 ファイル 1 責務。World・FFI に触れない純ロジック。App 側は app/scroll_events.rs、
//  レイアウトの走査の側は canvas_layout/scroll_view.rs）
//    constants.rs  … 物理の定数の表（出典つき: Flutter・Android）
//    physics.rs    … 1 次元のシミュレーション（端で止める慣性・減衰・ばね・跳ね返り・ScrollTo）
//    overscroll.rs … 指のドラッグを位置へ当てる（範囲・端の外の摩擦）
//    ballistic.rs  … 離した後の動きを選ぶ（慣性・跳ね返り・ページ・間隔のスナップ）
//    state.rs      … 実行中の状態のコンポーネント（CanvasScrollState。保存しない）
//    controller.rs … スクロール 1 つの段階の移り変わり（ドラッグ・離す・触れて止める・進める・要求）
//    nesting.rs    … 入れ子の受け渡し（同じ向きは内側から外側へ）
//    visibility.rs … 見える範囲の外の部分木を飛ばす
//    events.rs     … スクロールのイベント（開始・位置・終了）
//    tests.rs      … 単体テスト
// ============================================================

pub mod ballistic;
pub mod constants;
pub mod controller;
pub mod events;
pub mod nesting;
pub mod overscroll;
pub mod physics;
pub mod state;
pub mod visibility;

#[cfg(test)]
mod tests;

pub use events::{collect_events, ScrollEmit, ScrollEventKind};
pub use state::{AxisMotion, CanvasScrollState, ScrollMetrics, ScrollPhase, ScrollRequest};
