use std::time::Instant;

// ============================================================
//  FrameContext
// ============================================================

/// 各フレームライフサイクルメソッドに渡される時間情報。
#[derive(Clone, Copy)]
pub struct FrameContext {
    /// 前フレームからの経過時間（秒）。ConstantUpdate では FIXED_DELTA が入る。
    pub delta_time: f32,
    /// ゲーム内累計時間（Edit モードでは進まない）。
    pub anim_time:  f32,
}

// ============================================================
//  定数
// ============================================================

/// `ConstantUpdate` の固定タイムステップ（秒）。
pub const FIXED_DELTA: f32 = 1.0 / 60.0;

// ============================================================
//  Clock
// ============================================================

/// フレーム時間・ゲーム内時間・固定ステップアキュムレータを一元管理する。
pub struct Clock {
    last_frame:        Instant,
    anim_time:         f32,
    fixed_accumulator: f32,
}

impl Clock {
    pub fn new() -> Self {
        Self {
            last_frame:        Instant::now(),
            anim_time:         0.0,
            fixed_accumulator: 0.0,
        }
    }

    /// フレーム開始時に呼ぶ。
    /// `time_running` が true の場合のみゲーム内時間を進める（Play・非ポーズ時）。
    /// 返り値は今フレームの FrameContext。
    pub fn tick(&mut self, time_running: bool) -> FrameContext {
        let now        = Instant::now();
        let delta_time = now.duration_since(self.last_frame).as_secs_f32();
        self.last_frame = now;

        if time_running {
            self.anim_time         += delta_time;
            self.fixed_accumulator += delta_time;
        }

        FrameContext { delta_time, anim_time: self.anim_time }
    }

    /// `FIXED_DELTA` 分ずつアキュムレータを消費するイテレータを返す。
    /// ConstantUpdate のループに使う：
    /// ```rust
    /// for fixed_ctx in clock.drain_fixed() {
    ///     scene.constant_update(&fixed_ctx);
    /// }
    /// ```
    pub fn drain_fixed(&mut self) -> FixedDrain<'_> {
        FixedDrain { clock: self }
    }

    pub fn anim_time(&self) -> f32 { self.anim_time }
}

impl Default for Clock {
    fn default() -> Self { Self::new() }
}

// ============================================================
//  FixedDrain — drain_fixed() が返すイテレータ
// ============================================================

pub struct FixedDrain<'a> {
    clock: &'a mut Clock,
}

impl Iterator for FixedDrain<'_> {
    type Item = FrameContext;

    fn next(&mut self) -> Option<FrameContext> {
        if self.clock.fixed_accumulator >= FIXED_DELTA {
            self.clock.fixed_accumulator -= FIXED_DELTA;
            Some(FrameContext {
                delta_time: FIXED_DELTA,
                anim_time:  self.clock.anim_time,
            })
        } else {
            None
        }
    }
}
