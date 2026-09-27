// ============================================================
//  redraw/schedule.rs — 「次に起きる時刻」の合成と、別の時計の予定の換算【純関数・単体テスト付き】（W2-10a）
//
//  on_demand で止めている間も、時刻で起こる出来事（ジェスチャーの長押しの期限・模擬の目覚まし・スクリプトの
//  `SEED.Redraw.RequestAfter`）は落とせない。それぞれの予定をイベントループの時計（Instant）へ直し、いちばん早いものを
//  ControlFlow::WaitUntil に渡す（予定が無ければ ControlFlow::Wait）。
//  予定は出どころごとに時計が違う（ジェスチャーは pointer_log の秒・模擬の目覚ましは模擬の壁時計で測った残りのミリ秒）ので、
//  換算をここにまとめる。
// ============================================================

use std::time::{Duration, Instant};

/// 予定を先へずらせる上限（秒）。これより先の予定はこの長さで起きて決め直す（Instant のあふれを避ける）。
///
/// 1 日。予定がこれより遠くても、起きたときに次の予定をもう一度計算するので問題ない。
pub const MAX_SCHEDULE_AHEAD_SECS: f64 = 86_400.0;

/// 予定の中でいちばん早いもの（無ければ None）。
pub fn earliest(deadlines: impl IntoIterator<Item = Option<Instant>>) -> Option<Instant> {
    deadlines.into_iter().flatten().min()
}

/// 秒の長さを、上限つきの Duration にする（負・NaN は 0、上限を超えたら上限）。
pub fn clamped_delay(secs: f64) -> Duration {
    if !secs.is_finite() || secs <= 0.0 {
        // NaN・負は「すぐ」。+∞ は上限（is_finite で弾いたので下で扱う）
        if secs == f64::INFINITY {
            return Duration::from_secs_f64(MAX_SCHEDULE_AHEAD_SECS);
        }
        return Duration::ZERO;
    }
    Duration::from_secs_f64(secs.min(MAX_SCHEDULE_AHEAD_SECS))
}

/// 別の時計（秒）の予定を Instant へ直す（ジェスチャーの pointer_log の時計など）。
///
/// # 引数
/// * `now`           - イベントループの時計の今
/// * `now_secs`      - 同じ瞬間の、予定の時計の今（秒）
/// * `deadline_secs` - 予定の時計での予定（秒）。過ぎていれば `now` を返す
pub fn instant_from_clock_secs(now: Instant, now_secs: f64, deadline_secs: f64) -> Instant {
    now + clamped_delay(deadline_secs - now_secs)
}

/// 「今から何ミリ秒後」の予定を Instant へ直す（デスクトップの模擬の目覚まし・鳴動の安全弁。模擬が自分の壁時計で測った残り）。
///
/// # 引数
/// * `now`      - イベントループの時計の今
/// * `delay_ms` - 予定までの残り（ミリ秒。0 は今）
pub fn instant_after_millis(now: Instant, delay_ms: u64) -> Instant {
    /// ミリ秒 → 秒。
    const MILLIS_PER_SEC: f64 = 1_000.0;
    now + clamped_delay(delay_ms as f64 / MILLIS_PER_SEC)
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// いちばん早いものを選び、全部 None なら None。
    #[test]
    fn earliest_picks_minimum() {
        let t0 = Instant::now();
        let a = t0 + Duration::from_millis(30);
        let b = t0 + Duration::from_millis(10);
        assert_eq!(earliest([Some(a), None, Some(b)]), Some(b));
        assert_eq!(earliest([None, None]), None);
        assert_eq!(earliest(std::iter::empty()), None);
    }

    /// 長さの丸め: 負・NaN は 0、+∞ と上限超えは上限。
    #[test]
    fn delay_is_clamped() {
        assert_eq!(clamped_delay(-1.0), Duration::ZERO);
        assert_eq!(clamped_delay(f64::NAN), Duration::ZERO);
        assert_eq!(clamped_delay(0.25), Duration::from_millis(250));
        assert_eq!(clamped_delay(f64::INFINITY), Duration::from_secs_f64(MAX_SCHEDULE_AHEAD_SECS));
        assert_eq!(clamped_delay(MAX_SCHEDULE_AHEAD_SECS * 2.0), Duration::from_secs_f64(MAX_SCHEDULE_AHEAD_SECS));
    }

    /// 別の時計の予定の換算（過ぎた予定は今）。
    #[test]
    fn converts_other_clocks() {
        let now = Instant::now();
        assert_eq!(instant_from_clock_secs(now, 10.0, 10.5), now + Duration::from_millis(500));
        assert_eq!(instant_from_clock_secs(now, 10.0, 9.0), now, "過ぎた予定は今");
        assert_eq!(instant_after_millis(now, 2_500), now + Duration::from_millis(2_500));
        assert_eq!(instant_after_millis(now, 0), now, "0 は今");
        assert_eq!(
            instant_after_millis(now, u64::MAX),
            now + Duration::from_secs_f64(MAX_SCHEDULE_AHEAD_SECS),
            "遠すぎる予定は上限"
        );
    }
}
