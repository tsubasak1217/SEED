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
    /// 壁時計の累計時間（秒）。**モード・ポーズに関係なく毎フレーム進む**。
    ///
    /// ゲーム内時間（`anim_time`）と違い Edit / ポーズ中も止まらないため、
    /// 「編集中でも動いていてほしい」見た目のアニメーション（水面の波、
    /// L3 シェーディングアセットの時間応答）の駆動に使う。
    /// ゲームロジック（スクリプトの `SEED.Time.ElapsedTime`）には使わないこと
    /// ＝ ゲームの時間はあくまで `anim_time` が権威である。
    pub ambient_time: f32,
}

// ============================================================
//  定数
// ============================================================

/// `ConstantUpdate` の固定タイムステップ（秒）。
pub const FIXED_DELTA: f32 = 1.0 / 60.0;

/// デバッグセッション中、このフレーム delta（秒）を超えたら
/// 「デバッガのブレークポイント停止による巨大な壁時計経過」とみなす閾値。
///
/// 通常プレイでは 1 フレームが 0.5 秒に達することはまずないため、
/// この閾値を超えるのは実質的にブレークポイントで長時間停止した復帰フレームのみ。
/// 超過フレームは delta を `FIXED_DELTA` に丸め、`fixed_accumulator` の暴走
/// （ConstantUpdate の数百〜数千回追いつき = スパイラル・オブ・デス）を防ぐ。
pub const DEBUG_PAUSE_THRESHOLD: f32 = 0.5;

// ============================================================
//  Clock
// ============================================================

/// フレーム時間・ゲーム内時間・固定ステップアキュムレータを一元管理する。
pub struct Clock {
    last_frame:        Instant,
    anim_time:         f32,
    /// 壁時計の累計時間（秒）。`time_running` に関係なく毎フレーム加算する。
    ///
    /// **f64 で持つ理由**: エディタは何時間も起動しっぱなしになり得る。f32 は
    /// 累計が大きくなるほど加算の刻みが丸められ、最終的に時間が進まなくなる
    /// （典型的な累積時計の精度崩壊）。加算は f64 で行い、シェーダへ配る直前に
    /// f32 へ落とす。
    ambient_time:      f64,
    fixed_accumulator: f32,
    /// デバッグセッション（内蔵デバッガ）がアタッチ中かどうか。
    /// true の間だけ `DEBUG_PAUSE_THRESHOLD` によるブレークポイント停止ガードが働く。
    /// エディタから DBG_GUARD IPC で切り替える。通常プレイ（非デバッグ）では常に false。
    debug_guard:       bool,
}

impl Clock {
    pub fn new() -> Self {
        Self {
            last_frame:        Instant::now(),
            anim_time:         0.0,
            ambient_time:      0.0,
            fixed_accumulator: 0.0,
            debug_guard:       false,
        }
    }

    /// デバッグセッションのアタッチ/デタッチに合わせてブレークポイント停止ガードを切り替える。
    pub fn set_debug_guard(&mut self, on: bool) { self.debug_guard = on; }

    /// フレーム開始時に呼ぶ。
    /// `time_running` が true の場合のみゲーム内時間を進める（Play・非ポーズ時）。
    /// 返り値は今フレームの FrameContext。
    pub fn tick(&mut self, time_running: bool) -> FrameContext {
        let now            = Instant::now();
        let mut delta_time = now.duration_since(self.last_frame).as_secs_f32();
        self.last_frame = now;

        // デバッグセッション中のみ: ブレークポイントで停止していた間に進んだ
        // 巨大な壁時計経過をそのまま積むと ConstantUpdate が大量に追いつき実行され、
        // ブレークポイントに延々と再ヒットしてしまう。閾値超のフレームは
        // 1 フレーム分（FIXED_DELTA）に丸めて、この暴走を防ぐ。
        if self.debug_guard && delta_time > DEBUG_PAUSE_THRESHOLD {
            delta_time = FIXED_DELTA;
        }

        if time_running {
            self.anim_time         += delta_time;
            self.fixed_accumulator += delta_time;
        }

        // 壁時計は常時進める（Edit・ポーズ・Play を問わない）。
        // ブレークポイントガードで丸めた delta をそのまま使う点は anim_time と同じ
        //（デバッガ停止中に見た目の時間だけ大きく飛ぶのを防ぐ）。
        self.ambient_time += delta_time as f64;

        FrameContext {
            delta_time,
            anim_time:    self.anim_time,
            ambient_time: self.ambient_time as f32,
        }
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

    /// 壁時計の累計時間（秒）。モード・ポーズに関係なく進む。
    pub fn ambient_time(&self) -> f32 { self.ambient_time as f32 }
}

impl Default for Clock {
    fn default() -> Self { Self::new() }
}

// ============================================================
//  FixedDrain — drain_fixed() が返すイテレータ
// ============================================================

/// `Clock::drain_fixed` が返すイテレータ。`FIXED_DELTA` 分ずつアキュムレータを消費しながら
/// ConstantUpdate 用の `FrameContext` を順次生成する。
pub struct FixedDrain<'a> {
    clock: &'a mut Clock,
}

impl Iterator for FixedDrain<'_> {
    type Item = FrameContext;

    fn next(&mut self) -> Option<FrameContext> {
        if self.clock.fixed_accumulator >= FIXED_DELTA {
            self.clock.fixed_accumulator -= FIXED_DELTA;
            Some(FrameContext {
                delta_time:   FIXED_DELTA,
                anim_time:    self.clock.anim_time,
                // 固定ステップは「今フレームの壁時計」をそのまま配る
                //（壁時計は固定ステップの回数では進まない）。
                ambient_time: self.clock.ambient_time as f32,
            })
        } else {
            None
        }
    }
}
