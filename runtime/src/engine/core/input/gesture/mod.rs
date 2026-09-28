// ============================================================
//  gesture/ — ジェスチャーアリーナ（タップ・長押し・ドラッグ・フリック・押下の取り消し・指ごとの捕捉。W2-2）
//
//  【何をするか】（docs/app_platform_roadmap.md W2-P3。規則の正典は docs/input_gestures.md）
//  CanvasGestureComponent を付けた 2D キャンバスのノードだけを対象に、指（ポインタ）ごとに
//  「押した位置の当たり判定の経路（葉 → 根）」のノードが受けたいジェスチャーの認識器を競わせ、
//  最初に勝ちを申し出た 1 つが勝つ（他は負け）。勝ったノードはその指を捕捉する。押下の見た目の
//  イベント（PressDown / PressCancel / PressUp）はノードごとに出す。
//  CanvasGestureComponent を付けていないノードは参加しない（従来の OnPointer* の振る舞いは変わらない）。
//
//  【構成】（1 ファイル 1 責務。World・FFI に触れない純ロジック。App 側は app/gesture_scene.rs・app/gesture_events.rs）
//    thresholds.rs   … 閾値の表（名前付きの既定値と出典・プロジェクト設定の "gestures"・dp → 画素の換算）
//    pointer_log.rs  … 時刻つきの指のイベントの記録（Input が積み、アリーナが読む）と時計・注入の指
//    pointer_track.rs… 指 1 本の追跡（押した位置・時刻・今の位置・速度）
//    velocity.rs     … 速度の推定（直近の標本の最小二乗）
//    hit_slop.rs     … 当たり判定（最小のヒット領域・近い方・遮り・切り抜き）と経路の選び方
//    scene.rs        … アリーナが問い合わせるノードの世界（トレイトと、フレームごとの材料 GestureHitScene）
//    recognizers/    … 認識器ごとの判定の規則（tap・long_press・drag・fling）
//    arena.rs        … 指 1 本のアリーナ（勝ち負け・捕捉・押下の見た目）
//    arena_set.rs    … すべての指（時刻の順の処理・複数指の規則・時刻の出来事・「動いている」の申告）
//    pinch.rs        … 2 本指のピンチ（W2-8。アリーナとは別にすべての指を見て、始まったら 2 本の指を捕捉する）
//    events.rs       … 出すイベント（種類の番号は C# と一致）とドラッグの途中のまとめ
//    tests.rs        … 合成の指の列でアリーナを通して確かめる試験
//
//  【座標と時刻】アリーナの位置はキャンバスの画素（画面の中央が原点・Y 下向き）、時刻は pointer_log の時計の秒
//  （入力イベントを受け取った時刻。フレームの時刻に依らない）。
// ============================================================

pub mod arena;
pub mod arena_set;
pub mod events;
pub mod hit_slop;
pub mod pinch;
pub mod pointer_log;
pub mod pointer_track;
pub mod recognizers;
pub mod scene;
pub mod thresholds;
pub mod velocity;

#[cfg(test)]
mod tests;

pub use arena_set::{GestureActivity, GestureArenaSet};
pub use events::{GestureEmit, GestureEventKind, NO_SCALE};
pub use hit_slop::{ClipAabb, GestureHitNode, PaintOrder};
pub use pointer_log::{
    pointer_clock_now, pointer_clock_secs, InjectedPointerTracker, PointerEventLog, PointerInputEvent, PointerKey,
    PointerLogEntry, PointerPhase, INJECTED_POINTER_KEY,
};
pub use scene::{GestureHitScene, GestureScene};
pub use thresholds::{parse_gesture_thresholds, GestureMetrics, GestureThresholds};
