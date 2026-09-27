// ============================================================
//  canvas_scroll/events.rs — スクロールのイベント（開始・位置・終了。W2-3）
//
//  スクロールのシステムはフレームの処理の後に、状態の「前に知らせた位置・スクロール中か」と今の状態を比べて
//  イベントを作る（1 フレームに 1 スクロールにつき最大で 開始 → 位置 → 終了 の 3 件。Flutter の
//  ScrollStartNotification / ScrollUpdateNotification / ScrollEndNotification と同じ区切り）。
//    - 開始: スクロールしていない → スクロールしている（ドラッグ・慣性・ScrollTo）になった、または止まったまま位置が変わった
//    - 位置: 前に知らせた位置から動いた（位置の差を持つ）
//    - 終了: スクロールしている → していない（止まった・触れて止めた）。止まったまま位置が変わった（Jump）ときも同じフレームで出す
//
//  【種類の番号】`ScrollEventKind` の数値は FFI で C# へ渡す。C# の `SEED.ScrollEventKind`（scripting/src/Api/ScrollEvent.cs）と
//  必ず一致させる。足すときは末尾へ。
// ============================================================

use crate::engine::ecs::Entity;

use super::state::{CanvasScrollState, AXES};

/// 位置が変わったとみなす差（単位。これより小さな動きでは位置のイベントを出さない）。
pub const POSITION_EVENT_EPSILON: f64 = 1e-4;

/// スクロールのイベントの種類（数値は C# の SEED.ScrollEventKind と同じ）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(i32)]
pub enum ScrollEventKind {
    /// スクロールが始まった。
    Start = 0,
    /// 位置が変わった（1 フレームに 1 回まで）。
    Update = 1,
    /// スクロールが終わった。
    End = 2,
}

impl ScrollEventKind {
    /// FFI・ログで使う数値。
    #[inline]
    pub fn id(self) -> i32 {
        self as i32
    }

    /// 診断ログ用の名前。
    pub fn label(self) -> &'static str {
        match self {
            ScrollEventKind::Start => "Start",
            ScrollEventKind::Update => "Update",
            ScrollEventKind::End => "End",
        }
    }
}

/// ノードへ届けるスクロールのイベント 1 件（値はスクロールの単位）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ScrollEmit {
    /// 届け先のノード（アクターの entity）。
    pub node: Entity,
    /// 種類。
    pub kind: ScrollEventKind,
    /// 今の位置。
    pub position: [f64; AXES],
    /// 前に知らせた位置からの差（位置のイベントだけ。他は 0）。
    pub delta: [f64; AXES],
    /// 速度（単位/秒・位置の向き）。
    pub velocity: [f64; AXES],
    /// 位置の最大（中身 − 窓）。
    pub max_position: [f64; AXES],
    /// 窓の大きさ。
    pub viewport: [f64; AXES],
    /// 中身の大きさ。
    pub content: [f64; AXES],
    /// 指でドラッグしているか。
    pub dragging: bool,
}

/// 状態の変化からイベントを作り、知らせた内容を状態へ控える【純関数（状態の控えだけ書く）】。
///
/// # 引数
/// * `node`  - 届け先のノード
/// * `state` - スクロールの状態（`reported_*` を書き換える）
/// * `out`   - イベントの積み先
pub fn collect_events(node: Entity, state: &mut CanvasScrollState, out: &mut Vec<ScrollEmit>) {
    let scrolling = state.phase.is_scrolling();
    let delta = [0, 1].map(|a| state.position[a] - state.reported_position[a]);
    let moved = delta.iter().any(|d| d.abs() > POSITION_EVENT_EPSILON);
    let metrics = state.metrics;
    let base = ScrollEmit {
        node,
        kind: ScrollEventKind::Start,
        position: state.position,
        delta: [0.0; AXES],
        velocity: state.velocity,
        max_position: [0, 1].map(|a| metrics.map_or(0.0, |m| m.max_position(a))),
        viewport: metrics.map_or([0.0; AXES], |m| m.viewport),
        content: metrics.map_or([0.0; AXES], |m| m.content),
        dragging: state.phase == super::state::ScrollPhase::Dragging,
    };
    if !state.reported_scrolling && (scrolling || moved) {
        out.push(base);
        state.reported_scrolling = true;
    }
    if moved {
        out.push(ScrollEmit { kind: ScrollEventKind::Update, delta, ..base });
        state.reported_position = state.position;
    }
    if state.reported_scrolling && !scrolling {
        out.push(ScrollEmit { kind: ScrollEventKind::End, velocity: [0.0; AXES], ..base });
        state.reported_scrolling = false;
    }
}
