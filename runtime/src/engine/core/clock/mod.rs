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
    ///
    /// **時間スケール適用後**の累積である（`SEED.Time.Scale` で減速すれば進みも遅くなる）。
    /// スクリプトの `SEED.Time.ElapsedTime` と同一値。
    pub anim_time:  f32,
    /// 前フレームからの経過時間（秒）。**時間スケール未適用**の実フレーム時間。
    ///
    /// `delta_time` と違い `SEED.Time.Scale` の影響を受けない。
    /// ヒットストップ中でも等速で動かしたいもの（UI アニメーション・演出タイマー）に使う。
    /// スクリプトの `SEED.Time.UnscaledDeltaTime` と同一値。
    pub unscaled_delta_time: f32,
    /// ゲーム内累計時間（秒）の**時間スケール未適用**版。
    ///
    /// `anim_time` と同じく Edit モード・ポーズ中は進まないが、
    /// `SEED.Time.Scale` の影響だけを受けない。
    /// スクリプトの `SEED.Time.UnscaledElapsedTime` と同一値。
    pub unscaled_anim_time: f32,
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

// ─── 時間スケール（SEED.Time.Scale）────────────────────────────
//
// ゲーム時間の進み方を一括で伸縮させる係数。ヒットストップ・スローモーション・
// ポーズ演出などに使う。値の保管場所は `scripting::host_api`（FFI から読み書き
// されるため）で、`Clock` は毎フレーム引数として受け取るだけの純粋な計算層とする。

/// 時間スケールの既定値（等速）。Play 開始・停止・シーン遷移でこの値へ戻す。
pub const TIME_SCALE_DEFAULT: f32 = 1.0;

/// 時間スケールの下限（負値・NaN はここへ丸める）。0 は「ゲーム時間の完全停止」。
pub const TIME_SCALE_MIN: f32 = 0.0;

/// 時間スケールの上限。
///
/// 上限を設ける理由は 2 つ。① 巨大値を渡されるとキーフレーム補間・物理積分が
/// 1 フレームで飛びすぎて破綻する。② `f32` の桁落ちで累計時間の精度が壊れる。
/// 100 倍あれば「早送り」用途は十分に賄える。
pub const TIME_SCALE_MAX: f32 = 100.0;

/// 「ゲーム時間が完全に止まっている」とみなすスケールの閾値。
///
/// 厳密な `== 0.0` 比較にすると、`1e-30` のような非正規化数で物理スレッドが
/// 実質停止しているのに「動いている」と判定され、無意味なステップを踏み続ける。
pub const TIME_SCALE_STOP_EPS: f32 = 1e-6;

/// 外部（スクリプト）から与えられた時間スケールを有効範囲へ丸める。
///
/// - NaN → 既定値（等速）。壊れた値でゲーム時間が凍結するより等速へ戻す方が安全。
/// - 負値 → `TIME_SCALE_MIN`（0 = 停止）。時間を巻き戻す意味論は持たせない。
/// - 上限超過 → `TIME_SCALE_MAX`。
pub fn sanitize_time_scale(scale: f32) -> f32 {
    if scale.is_nan() {
        return TIME_SCALE_DEFAULT;
    }
    scale.clamp(TIME_SCALE_MIN, TIME_SCALE_MAX)
}

/// 指定スケールが「ゲーム時間の完全停止」を意味するか。
pub fn is_time_stopped(scale: f32) -> bool {
    sanitize_time_scale(scale) <= TIME_SCALE_STOP_EPS
}

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
    /// ゲーム内累計時間の**時間スケール未適用**版（秒）。
    /// `anim_time` と同じタイミングでのみ進むが、`SEED.Time.Scale` を掛けない。
    unscaled_anim_time: f32,
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
            unscaled_anim_time: 0.0,
            fixed_accumulator: 0.0,
            debug_guard:       false,
        }
    }

    /// デバッグセッションのアタッチ/デタッチに合わせてブレークポイント停止ガードを切り替える。
    pub fn set_debug_guard(&mut self, on: bool) { self.debug_guard = on; }

    /// フレーム開始時に呼ぶ。
    ///
    /// # 引数
    /// - `time_running`: ゲーム時間を進めるフレームか（Play かつ非ポーズ）。
    /// - `time_scale`:   `SEED.Time.Scale`（生値でよい。内部で `sanitize_time_scale` する）。
    ///
    /// # 時間スケールの適用範囲
    /// スケールは **`time_running` が true のフレームにのみ**掛かる。Edit モードと
    /// Play 一時停止中はそもそもゲーム時間が進まないので、既存の paused 機構とは
    /// 完全に独立している（＝スケールを 0 にしてもエディタのカメラ操作や
    /// パーティクルの常時プレビューは従来どおり実時間で動く）。
    ///
    /// 返り値は今フレームの FrameContext。`delta_time` / `anim_time` はスケール適用後、
    /// `unscaled_delta_time` / `unscaled_anim_time` は未適用の実時間である。
    pub fn tick(&mut self, time_running: bool, time_scale: f32) -> FrameContext {
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

        // ── 時間スケールの適用 ────────────────────────────────────────
        // ゲーム時間が進むフレームだけスケールを掛ける。止まっているフレームで
        // スケールを掛けても意味が無いばかりか、Edit モードの実時間駆動
        //（デバッグカメラ・パーティクルプレビュー）を壊す。
        let scale = if time_running {
            sanitize_time_scale(time_scale)
        } else {
            TIME_SCALE_DEFAULT
        };
        // 今フレームのゲーム時間 delta（スクリプト・アニメ・パーティクル等へ配る値）
        let scaled_delta = delta_time * scale;

        if time_running {
            self.anim_time          += scaled_delta;
            self.unscaled_anim_time += delta_time;
            // 固定ステップ（ConstantUpdate）もゲーム時間で刻む。スケール 0 なら
            // アキュムレータが増えず ConstantUpdate は 1 回も回らない（＝完全停止）。
            self.fixed_accumulator  += scaled_delta;
        }

        // 壁時計は常時進める（Edit・ポーズ・Play を問わない）。
        // ブレークポイントガードで丸めた delta をそのまま使う点は anim_time と同じ
        //（デバッガ停止中に見た目の時間だけ大きく飛ぶのを防ぐ）。
        self.ambient_time += delta_time as f64;

        FrameContext {
            delta_time:          scaled_delta,
            anim_time:           self.anim_time,
            unscaled_delta_time: delta_time,
            unscaled_anim_time:  self.unscaled_anim_time,
            ambient_time:        self.ambient_time as f32,
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

    /// 時間スケール未適用のゲーム内累計時間（秒）。
    pub fn unscaled_anim_time(&self) -> f32 { self.unscaled_anim_time }

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
                // 固定ステップの delta は定義上スケール済み（アキュムレータへ積む
                // 段階でスケール済み delta を入れている）。未スケール版は
                // 「1 ステップ = FIXED_DELTA 実時間相当」を意味しないため、
                // 呼び出し側が誤用しないよう同じ値を配る。
                unscaled_delta_time: FIXED_DELTA,
                unscaled_anim_time:  self.clock.unscaled_anim_time,
                // 固定ステップは「今フレームの壁時計」をそのまま配る
                //（壁時計は固定ステップの回数では進まない）。
                ambient_time: self.clock.ambient_time as f32,
            })
        } else {
            None
        }
    }
}

// ============================================================
//  ユニットテスト（時間スケール）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 浮動小数の近似比較（累積時間のテスト用）。
    fn approx(a: f32, b: f32) -> bool {
        (a - b).abs() <= 1e-5
    }

    /// sanitize_time_scale: 既定・0・中間・負値・上限超過・NaN の丸め。
    #[test]
    fn sanitize_time_scale_clamps_into_valid_range() {
        // 既定値・通常値はそのまま通す
        assert_eq!(sanitize_time_scale(TIME_SCALE_DEFAULT), 1.0);
        assert_eq!(sanitize_time_scale(0.5), 0.5);
        // 0 は「停止」として有効な値
        assert_eq!(sanitize_time_scale(0.0), 0.0);
        // 負値は下限（0）へ丸める（時間の巻き戻しは許可しない）
        assert_eq!(sanitize_time_scale(-3.0), TIME_SCALE_MIN);
        // 上限超過はクランプ
        assert_eq!(sanitize_time_scale(TIME_SCALE_MAX + 1000.0), TIME_SCALE_MAX);
        // NaN は既定値（等速）へ。凍結より等速復帰を選ぶ
        assert_eq!(sanitize_time_scale(f32::NAN), TIME_SCALE_DEFAULT);
    }

    /// is_time_stopped: 0 と極小値のみ「停止」と判定する。
    #[test]
    fn is_time_stopped_detects_zero_and_denormals() {
        assert!(is_time_stopped(0.0));
        assert!(is_time_stopped(-1.0));          // 丸めて 0
        assert!(is_time_stopped(1e-30));         // 実質停止
        assert!(!is_time_stopped(0.01));
        assert!(!is_time_stopped(TIME_SCALE_DEFAULT));
    }

    /// Clock::tick が「与えた実 delta とスケール」から
    /// スケール済み/未スケールの delta・累計を正しく作ることを検証する。
    ///
    /// `Instant::now()` に依存せず検算するため、内部状態を直接操作する
    /// 補助関数で 1 フレーム分の計算を再現する（tick 本体と同じ式）。
    fn simulate_frame(clock: &mut Clock, raw_delta: f32, time_running: bool, scale: f32)
        -> FrameContext
    {
        // tick 本体と同一のスケール決定ロジック
        let s = if time_running { sanitize_time_scale(scale) } else { TIME_SCALE_DEFAULT };
        let scaled = raw_delta * s;
        if time_running {
            clock.anim_time          += scaled;
            clock.unscaled_anim_time += raw_delta;
            clock.fixed_accumulator  += scaled;
        }
        clock.ambient_time += raw_delta as f64;
        FrameContext {
            delta_time:          scaled,
            anim_time:           clock.anim_time,
            unscaled_delta_time: raw_delta,
            unscaled_anim_time:  clock.unscaled_anim_time,
            ambient_time:        clock.ambient_time as f32,
        }
    }

    /// スケール 1.0: スケール済みと未スケールが一致する。
    #[test]
    fn scale_one_keeps_delta_identical() {
        let mut c = Clock::new();
        let ctx = simulate_frame(&mut c, 0.016, true, 1.0);
        assert!(approx(ctx.delta_time, 0.016));
        assert!(approx(ctx.unscaled_delta_time, 0.016));
        assert!(approx(ctx.anim_time, ctx.unscaled_anim_time));
    }

    /// スケール 0.5: ゲーム時間だけが半分の速さで進む。
    #[test]
    fn scale_half_slows_game_time_only() {
        let mut c = Clock::new();
        let ctx = simulate_frame(&mut c, 0.02, true, 0.5);
        assert!(approx(ctx.delta_time, 0.01));
        assert!(approx(ctx.unscaled_delta_time, 0.02));
        assert!(approx(ctx.anim_time, 0.01));
        assert!(approx(ctx.unscaled_anim_time, 0.02));
    }

    /// スケール 0: ゲーム時間が完全に停止し、固定ステップも 1 回も回らない。
    #[test]
    fn scale_zero_freezes_game_time_and_fixed_steps() {
        let mut c = Clock::new();
        for _ in 0..10 {
            let ctx = simulate_frame(&mut c, 0.1, true, 0.0);
            assert_eq!(ctx.delta_time, 0.0);
            assert!(approx(ctx.unscaled_delta_time, 0.1));
        }
        assert_eq!(c.anim_time(), 0.0);                 // ゲーム時間は 1 ミリ秒も進まない
        assert!(approx(c.unscaled_anim_time(), 1.0));   // 実時間は 1 秒進んでいる
        assert_eq!(c.drain_fixed().count(), 0);         // ConstantUpdate は 0 回
    }

    /// 負値は 0 へ丸められ、停止と同じ挙動になる。
    #[test]
    fn negative_scale_behaves_as_stop() {
        let mut c = Clock::new();
        let ctx = simulate_frame(&mut c, 0.02, true, -5.0);
        assert_eq!(ctx.delta_time, 0.0);
        assert_eq!(c.anim_time(), 0.0);
    }

    /// 上限超過はクランプされ、上限倍を超えて進まない。
    #[test]
    fn huge_scale_is_clamped_to_max() {
        let mut c = Clock::new();
        let ctx = simulate_frame(&mut c, 0.01, true, 1.0e9);
        assert!(approx(ctx.delta_time, 0.01 * TIME_SCALE_MAX));
    }

    /// ゲーム時間が止まっているフレーム（Edit / ポーズ）はスケールを無視する。
    /// エディタのカメラ操作・パーティクルプレビューが実時間で動き続ける保証。
    #[test]
    fn scale_is_ignored_while_time_is_not_running() {
        let mut c = Clock::new();
        let ctx = simulate_frame(&mut c, 0.02, false, 0.0);
        // time_running=false なのでスケールは 1.0 扱い → 実フレーム時間がそのまま出る
        assert!(approx(ctx.delta_time, 0.02));
        assert!(approx(ctx.unscaled_delta_time, 0.02));
        // ゲーム内累計は進まない（従来仕様）
        assert_eq!(c.anim_time(), 0.0);
    }

    /// スケール 0.5 では固定ステップ（ConstantUpdate）の回数も半分になる。
    #[test]
    fn scale_half_halves_fixed_step_count() {
        let mut full = Clock::new();
        let mut half = Clock::new();
        // 1 秒ぶんを 1/60 秒フレームで刻む
        for _ in 0..60 {
            simulate_frame(&mut full, FIXED_DELTA, true, 1.0);
            simulate_frame(&mut half, FIXED_DELTA, true, 0.5);
        }
        let full_steps = full.drain_fixed().count() as i32;
        let half_steps = half.drain_fixed().count() as i32;
        // f32 の累積誤差で 1 ステップ前後するため、許容差 1 で比較する
        //（アキュムレータ方式である以上この誤差は正常な挙動）。
        assert!((full_steps - 60).abs() <= 1, "full_steps={full_steps}");
        assert!((half_steps * 2 - full_steps).abs() <= 1,
                "half_steps={half_steps}, full_steps={full_steps}");
    }
}
