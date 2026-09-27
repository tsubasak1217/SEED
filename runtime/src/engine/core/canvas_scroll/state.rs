// ============================================================
//  canvas_scroll/state.rs — スクロールの実行中の状態（ECS のコンポーネント。保存しない。W2-3）
//
//  CanvasScrollComponent（設定。保存する）と同じスロットのエンティティに、スクロールのシステム
//  （app/scroll_events.rs）が置く。設定を Undo・インスペクタで差し替えても（world.insert で設定だけが替わる）、
//  位置と動きはこちらに残る。スロットを消すときは両方を外す（canvas_layout_slots.rs）。
//
//  【読み書きする者】
//    - レイアウトの走査（canvas_layout/pass.rs）… `position` を読み、子へ渡す文脈を平行移動する
//    - フレームの描画（frame_renderer）… 表の `scroll_regions` から `metrics`（窓・中身・換算）を書く（次のフレームの物理が使う）
//    - スクロールのシステム … ドラッグ・慣性・ScrollTo で `position`・`motion`・`phase` を進め、イベントを配る
//    - スクリプト（SEED.CanvasScroll）… `position` の書き込み（すぐ移す）・`pending`（ScrollTo）・読み取り
// ============================================================

use crate::engine::ecs::{Component, Entity};

use super::overscroll::AxisRange;
use super::physics::{ScrollSim, UnitScale};

/// 軸の数（X・Y）。
pub const AXES: usize = 2;

/// スクロールの今の段階。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
#[repr(i32)]
pub enum ScrollPhase {
    /// 止まっている。
    #[default]
    Idle = 0,
    /// 指でドラッグしている。
    Dragging = 1,
    /// 指を離した後の慣性・跳ね返り・スナップ。
    Ballistic = 2,
    /// ScrollTo で動いている。
    Animating = 3,
    /// 動いている途中に指で触れて止めた（指を離すまで。離したら跳ね返りの戻り・スナップへ）。
    Held = 4,
}

impl ScrollPhase {
    /// スクロールしている（開始のイベントを出した・終了のイベントを出していない）段階か。
    pub fn is_scrolling(self) -> bool {
        matches!(self, ScrollPhase::Dragging | ScrollPhase::Ballistic | ScrollPhase::Animating)
    }

    /// 指に触れずに動いている段階か（触れたら止める・中の子へ指を渡さない。Flutter の IgnorePointer・Android の onInterceptTouchEvent）。
    pub fn moves_by_itself(self) -> bool {
        matches!(self, ScrollPhase::Ballistic | ScrollPhase::Animating)
    }
}

/// レイアウトが測った大きさ（フレームの描画の表から。スクロールの単位）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ScrollMetrics {
    /// 窓の大きさ。
    pub viewport: [f64; 2],
    /// 中身の大きさ（原点からいちばん遠い端まで）。
    pub content: [f64; 2],
    /// 1 単位の画素数（軸ごと）。
    pub px_per_unit: [f64; 2],
    /// 1 dp の画素数。
    pub dp_scale: f64,
    /// ノードのローカルの X 軸・Y 軸の向き（キャンバスの画素の空間の単位ベクトル。回転したノードの指の移動の換算）。
    pub axis_dirs: [[f32; 2]; AXES],
}

impl ScrollMetrics {
    /// 軸の位置の最大（中身 − 窓。0 以上）。
    pub fn max_position(&self, axis: usize) -> f64 {
        (self.content[axis] - self.viewport[axis]).max(0.0)
    }

    /// 軸の範囲。
    pub fn range(&self, axis: usize) -> AxisRange {
        AxisRange::new(0.0, self.max_position(axis), self.viewport[axis])
    }

    /// 軸の換算。
    pub fn unit_scale(&self, axis: usize) -> UnitScale {
        UnitScale::new(self.px_per_unit[axis], self.dp_scale)
    }

    /// キャンバスの画素の移動（または速度）を、ノードのローカルの軸ごとの単位へ直す。
    pub fn canvas_to_units(&self, v: [f32; 2]) -> [f64; 2] {
        let mut out = [0.0; AXES];
        for (axis, value) in out.iter_mut().enumerate() {
            let dir = self.axis_dirs[axis];
            let along = f64::from(v[0] * dir[0] + v[1] * dir[1]);
            *value = along / UnitScale::new(self.px_per_unit[axis], self.dp_scale).px_per_unit;
        }
        out
    }
}

/// 軸 1 本の動き（シミュレーションと、始めてからの時間）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AxisMotion {
    /// シミュレーション。
    pub sim: ScrollSim,
    /// 始めてからの時間（秒）。
    pub elapsed: f64,
}

/// スクリプトからの位置の要求（次のスクロールのシステムの処理で当てる）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ScrollRequest {
    /// すぐ移す（範囲へ収める）。
    Jump([f64; AXES]),
    /// 時間をかけて移す（Curves.easeInOut）。
    Animate {
        /// 目標の位置。
        target: [f64; AXES],
        /// 時間（秒。正）。
        duration: f64,
    },
}

/// スクロールの実行中の状態（World に置く。シリアライズしない）。
#[derive(Clone, Debug, PartialEq, Default)]
pub struct CanvasScrollState {
    /// このスクロールのノード（アクターの entity。システムが毎フレーム書く）。
    pub owner: Option<Entity>,
    /// 位置（スクロールの単位。0 = 中身の先頭が窓の先頭）。
    pub position: [f64; AXES],
    /// 速度（単位/秒。位置の向き。ドラッグ中は指の速度・慣性中はシミュレーションの速度）。
    pub velocity: [f64; AXES],
    /// 今の段階。
    pub phase: ScrollPhase,
    /// 軸ごとの動き（慣性・跳ね返り・スナップ・ScrollTo）。
    pub motion: [Option<AxisMotion>; AXES],
    /// レイアウトが測った大きさ（最初のフレームの描画までは None）。
    pub metrics: Option<ScrollMetrics>,
    /// スクリプトからの位置の要求。
    pub pending: Option<ScrollRequest>,
    /// 物理が最後に使った範囲の最大（中身・窓が変わったことを知る）。
    pub applied_max: Option<[f64; AXES]>,
    /// ドラッグしている指の番号。
    pub drag_pointer: Option<u32>,
    /// 最後にイベントで知らせた位置（位置のイベントの差分）。
    pub reported_position: [f64; AXES],
    /// 開始のイベントを出して、まだ終了のイベントを出していないか。
    pub reported_scrolling: bool,
}

impl CanvasScrollState {
    /// 軸の位置の最大（大きさが分からなければ None）。
    pub fn max_position(&self, axis: usize) -> Option<f64> {
        self.metrics.map(|m| m.max_position(axis))
    }

    /// 動いている（慣性・ScrollTo・ドラッグ・スクリプトの要求の処理待ち）か。「描く理由」の motion に使う。
    pub fn is_active(&self) -> bool {
        self.phase.is_scrolling() || self.pending.is_some() || self.motion.iter().any(Option::is_some)
    }

    /// 動きを止める（位置はそのまま・速度 0）。
    pub fn stop_motion(&mut self) {
        self.motion = [None, None];
        self.velocity = [0.0; AXES];
    }
}

impl Component for CanvasScrollState {}
