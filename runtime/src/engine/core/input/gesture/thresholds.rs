// ============================================================
//  gesture/thresholds.rs — ジェスチャーの閾値の表（時間・距離・速度。W2-2）
//
//  【何を持つか】
//  ジェスチャーアリーナ（arena_set.rs）と認識器（recognizers/）が使う閾値を 1 つの表にまとめる。
//  既定値はすべて名前付きの定数で、出典（Android の ViewConfiguration・Flutter の gestures/constants.dart と
//  velocity_tracker.dart）をコメントに残す。プロジェクト設定（project_settings.json の "gestures"）で
//  上書きでき、書いていない欄は既定値のまま（データ駆動。旧い設定ファイルはそのまま読める）。
//
//  【単位】距離は dp（端末に依らない単位。1 dp = 表示倍率の画素。canvas_layout/units.rs の dp_scale）、
//  時間はミリ秒、速度は dp/秒。アリーナは画素で計算するので、フレームごとに `GestureMetrics`
//  （画素・秒に直した値）へ換算して渡す（dp の倍率が回転・表示の切り替えで変わっても次のフレームから追従する）。
//
//  出典の確認（2026-09-28 に取得して読んだもの）:
//    - Android ViewConfiguration.java（AOSP main）: TOUCH_SLOP = 8、TAP_TIMEOUT = 100、DEFAULT_LONG_PRESS_TIMEOUT = 400、
//      MINIMUM_FLING_VELOCITY = 50、MAXIMUM_FLING_VELOCITY = 8000
//    - Flutter gestures/constants.dart: kPressTimeout = 100ms、kLongPressTimeout = 500ms、kTouchSlop = 18.0、
//      kMinFlingVelocity = 50.0、kMaxFlingVelocity = 8000.0
//    - Flutter gestures/velocity_tracker.dart: _horizonMilliseconds = 100、_historySize = 20、
//      _assumePointerMoveStoppedMilliseconds = 40、最小二乗の次数 2
//
//  速度の推定の頑健化（2026-09-29。velocity.rs の R2〜R4。docs/input_gestures.md §5.1）の 3 つの鍵
//  （velocity_lift_off_ms・velocity_min_span_ms・velocity_min_sample_interval_ms）は SEED が決めた値で、
//  出典は各定数のコメント（Android の vsync ごとの入力のまとめ・実機の記録・USB HID の報告の間隔）。0 でその規則を切る。
// ============================================================

use serde::{Deserialize, Serialize};

// ─── 既定値（名前付きの定数と出典）──────────────────────────────

/// ドラッグが始まるまでの許容移動（dp）。これを超えて動いたらドラッグが競いに勝てる。
/// 出典: Android ViewConfiguration.TOUCH_SLOP = 8 dp（W2-2 の要件も 8 dp）。
pub const DEFAULT_TOUCH_SLOP_DP: f32 = 8.0;

/// タップ・長押しの許容移動（dp）。押した位置からこれを超えて動いたらタップ・長押しは成り立たない（「動かず」）。
/// 出典: Flutter kTouchSlop = 18.0（タップの preAcceptSlopTolerance）。ドラッグの 8 dp より大きいので、
/// ドラッグが競いにいる所（スクロールの中）では先にドラッグが勝ち、ドラッグの無い所では指の小さな揺れでタップを落とさない。
pub const DEFAULT_TAP_SLOP_DP: f32 = 18.0;

/// 長押しが成り立つまでの時間（ミリ秒）。
/// 出典: docs/app_platform_roadmap.md W2-P3（500ms）= Flutter kLongPressTimeout。Android 12 以降の既定は 400ms。
pub const DEFAULT_LONG_PRESS_MS: f32 = 500.0;

/// 押下の見た目を出すまでの待ち（ミリ秒）。競いにドラッグ（スクロール）がいて、まだ勝負が決まっていないときだけ待つ。
/// 出典: Android ViewConfiguration.TAP_TIMEOUT = 100ms（スクロールの中の子の押下を遅らせる）= Flutter kPressTimeout。
pub const DEFAULT_PRESS_DELAY_MS: f32 = 100.0;

/// タップとして認める押している時間の上限（ミリ秒）。0 = 上限なし（Android の View のクリック・Flutter の onTap と同じ）。
/// 長押しが同じ指の競いにいれば、長押しの時間を過ぎた時点で長押しが勝つので、実質の上限は長押しの時間になる。
pub const DEFAULT_TAP_MAX_MS: f32 = 0.0;

/// フリックとみなす最小の速度（dp/秒）。
/// 出典: Android ViewConfiguration.MINIMUM_FLING_VELOCITY = 50 = Flutter kMinFlingVelocity。
pub const DEFAULT_MIN_FLING_VELOCITY_DP: f32 = 50.0;

/// フリックの速度の上限（dp/秒。これより速い推定は切り詰める）。
/// 出典: Android ViewConfiguration.MAXIMUM_FLING_VELOCITY = 8000 = Flutter kMaxFlingVelocity。
pub const DEFAULT_MAX_FLING_VELOCITY_DP: f32 = 8000.0;

/// 速度の推定に使う標本の時間の窓（ミリ秒。最新の標本からこれより古い標本は使わない）。
/// 出典: Flutter VelocityTracker._horizonMilliseconds = 100。
pub const DEFAULT_VELOCITY_HORIZON_MS: f32 = 100.0;

/// 速度の推定に使う標本の最大数（直近のこの数まで）。
/// 出典: Flutter VelocityTracker._historySize = 20。
pub const DEFAULT_VELOCITY_MAX_SAMPLES: usize = 20;

/// 指が止まったとみなす標本の間隔（ミリ秒）。直前の標本からこれ以上あいていたら、それより前は使わない
/// （離す直前に止まっていたらフリックにしない）。
/// 出典: Flutter VelocityTracker._assumePointerMoveStoppedMilliseconds = 40。
pub const DEFAULT_VELOCITY_STOP_MS: f32 = 40.0;

/// 速度の推定の多項式の次数（最小二乗）。
/// 出典: Flutter VelocityTracker（LeastSquaresSolver.solve(2)）。標本が少なければ次数を下げる（標本数 − 1 まで）。
pub const DEFAULT_VELOCITY_DEGREE: usize = 2;

/// 速度の推定の次数の上限（2 次より高い次数は指の揺れを拾いすぎるので受け付けない）。
pub const MAX_VELOCITY_DEGREE: usize = 2;

/// 「1 フレーム」の長さを決める画面の更新の頻度（Hz）。R2・R3 の既定値（1 フレーム = 1000 / 60 ms）の元。
/// 出典: Android は指の移動を vsync（Pixel 6a の表示は 60 Hz）ごとに 1 つの MotionEvent へまとめて届ける
/// （Choreographer の入力のまとめ）。受け取りの時刻で並べた標本は、1 フレームの中では µs しか離れない。
pub const REFERENCE_FRAME_RATE_HZ: f32 = 60.0;

/// R2（持ち上げの揺れ）: 離した時刻からこの長さだけ前までを「持ち上げの間」とみなす（ミリ秒。0 = R2 を切る）。
/// 離した時刻 − `velocity_stop_ms` から、離した時刻 − この長さまでの標本の速度が最小のフリックの速度より遅ければ
/// （＝離す前に指がほぼ止まっていた）、離しの速度を 0 にする（velocity.rs・recognizers/fling.rs）。
/// 出典: 1 フレーム（1000 / 60 ms）。2026-09-29 の実機の記録（docs/app_platform_roadmap.md §3.9.2 の (c)）で、止めてから離した指の
/// 位置の飛び（1.5〜4.2 dp。指の腹が転がる）は 3 回とも離したフレームの 1 回分の中にあった。`velocity_stop_ms` 以上にすると R2 は効かない。
pub const DEFAULT_VELOCITY_LIFT_OFF_MS: f32 = MILLIS_PER_SECOND / REFERENCE_FRAME_RATE_HZ;

/// R3（幅の短すぎる推定）: 推定に使った標本の時間の幅（最新 − 最古）がこれより短ければ速度 0（ミリ秒。0 = R3 を切る）。
/// 出典: 1 フレーム（1000 / 60 ms）。1 フレームより短い幅は、受け取りのまとまり（1 回の vsync の入力）の中の標本だけから出た値で、
/// 物理の速度ではない（受け取りの時刻では µs しか離れない標本の傾きが膨らむ。実機で 5,800〜7,700 dp/秒になった）。
pub const DEFAULT_VELOCITY_MIN_SPAN_MS: f32 = MILLIS_PER_SECOND / REFERENCE_FRAME_RATE_HZ;

/// R4（標本の間隔の下限）: 前の標本との間隔がこれより短い標本は、前の標本と入れ替える（新しい方を残す。ミリ秒。0 = R4 を切る）。
/// 出典: USB HID の最速の報告の間隔 1 ms（1000 Hz のポーリング）。タッチパネルの走査は速い機種でも 720 Hz ≈ 1.4 ms。
/// これより短い間隔の標本は受け取りのまとまり（同じ入力の塊を µs 差で受け取った）で、物理の標本ではない。
pub const DEFAULT_VELOCITY_MIN_SAMPLE_INTERVAL_MS: f32 = 1.0;

/// 標本の数の下限（2 未満では速度を推定しない）。
pub const MIN_VELOCITY_MAX_SAMPLES: usize = 2;

/// 1 秒のミリ秒数（ミリ秒 ↔ 秒の換算）。
pub const MILLIS_PER_SECOND: f32 = 1000.0;

// ─── 表（プロジェクト設定の "gestures"）──────────────────────────

/// ジェスチャーの閾値の表（dp・ミリ秒・dp/秒）。project_settings.json の "gestures" と同じ形。
///
/// 欄が無ければ既定値（`#[serde(default)]`）。壊れた値（負・NaN）は `sanitized` で既定値へ戻す。
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct GestureThresholds {
    /// ドラッグが始まるまでの許容移動（dp）。
    pub touch_slop_dp: f32,
    /// タップ・長押しの許容移動（dp）。
    pub tap_slop_dp: f32,
    /// 長押しが成り立つまでの時間（ミリ秒）。
    pub long_press_ms: f32,
    /// 押下の見た目を出すまでの待ち（ミリ秒）。
    pub press_delay_ms: f32,
    /// タップとして認める押している時間の上限（ミリ秒。0 = 上限なし）。
    pub tap_max_ms: f32,
    /// フリックとみなす最小の速度（dp/秒）。
    pub min_fling_velocity_dp: f32,
    /// フリックの速度の上限（dp/秒）。
    pub max_fling_velocity_dp: f32,
    /// 速度の推定の時間の窓（ミリ秒）。
    pub velocity_horizon_ms: f32,
    /// 速度の推定の標本の最大数。
    pub velocity_max_samples: usize,
    /// 指が止まったとみなす標本の間隔（ミリ秒）。
    pub velocity_stop_ms: f32,
    /// 速度の推定の多項式の次数（1 = 直線・2 = 2 次）。
    pub velocity_degree: usize,
    /// R2: 持ち上げの間（離した時刻からさかのぼるミリ秒。0 = R2 を切る）。
    pub velocity_lift_off_ms: f32,
    /// R3: 推定に使う標本の時間の幅の下限（ミリ秒。0 = R3 を切る）。
    pub velocity_min_span_ms: f32,
    /// R4: 標本の間隔の下限（ミリ秒。これより短い間隔の標本は前の標本と入れ替える。0 = R4 を切る）。
    pub velocity_min_sample_interval_ms: f32,
}

impl Default for GestureThresholds {
    fn default() -> Self {
        Self {
            touch_slop_dp: DEFAULT_TOUCH_SLOP_DP,
            tap_slop_dp: DEFAULT_TAP_SLOP_DP,
            long_press_ms: DEFAULT_LONG_PRESS_MS,
            press_delay_ms: DEFAULT_PRESS_DELAY_MS,
            tap_max_ms: DEFAULT_TAP_MAX_MS,
            min_fling_velocity_dp: DEFAULT_MIN_FLING_VELOCITY_DP,
            max_fling_velocity_dp: DEFAULT_MAX_FLING_VELOCITY_DP,
            velocity_horizon_ms: DEFAULT_VELOCITY_HORIZON_MS,
            velocity_max_samples: DEFAULT_VELOCITY_MAX_SAMPLES,
            velocity_stop_ms: DEFAULT_VELOCITY_STOP_MS,
            velocity_degree: DEFAULT_VELOCITY_DEGREE,
            velocity_lift_off_ms: DEFAULT_VELOCITY_LIFT_OFF_MS,
            velocity_min_span_ms: DEFAULT_VELOCITY_MIN_SPAN_MS,
            velocity_min_sample_interval_ms: DEFAULT_VELOCITY_MIN_SAMPLE_INTERVAL_MS,
        }
    }
}

/// ミリ秒（f32）を秒（f64）へ直す【純関数】（f64 で割るので 100ms が 0.1 秒ちょうどになる）。
fn ms_to_secs(ms: f32) -> f64 {
    f64::from(ms) / f64::from(MILLIS_PER_SECOND)
}

/// 0 以上の有限値ならそれ、そうでなければ既定値【純関数】。
fn non_negative_or(value: f32, fallback: f32) -> f32 {
    if value.is_finite() && value >= 0.0 { value } else { fallback }
}

/// 0 より大きい有限値ならそれ、そうでなければ既定値【純関数】。
fn positive_or(value: f32, fallback: f32) -> f32 {
    if value.is_finite() && value > 0.0 { value } else { fallback }
}

impl GestureThresholds {
    /// 壊れた値（負・NaN・無限大・範囲外の次数）を既定値へ戻した表【純関数】。
    ///
    /// フリックの上限が下限より小さいときは下限にそろえる（上限で切り詰めた速度がフリックにならない矛盾を避ける）。
    pub fn sanitized(self) -> Self {
        let d = Self::default();
        let min_fling = non_negative_or(self.min_fling_velocity_dp, d.min_fling_velocity_dp);
        Self {
            touch_slop_dp: non_negative_or(self.touch_slop_dp, d.touch_slop_dp),
            tap_slop_dp: non_negative_or(self.tap_slop_dp, d.tap_slop_dp),
            long_press_ms: positive_or(self.long_press_ms, d.long_press_ms),
            press_delay_ms: non_negative_or(self.press_delay_ms, d.press_delay_ms),
            tap_max_ms: non_negative_or(self.tap_max_ms, d.tap_max_ms),
            min_fling_velocity_dp: min_fling,
            max_fling_velocity_dp: positive_or(self.max_fling_velocity_dp, d.max_fling_velocity_dp).max(min_fling),
            velocity_horizon_ms: positive_or(self.velocity_horizon_ms, d.velocity_horizon_ms),
            velocity_max_samples: self.velocity_max_samples.max(MIN_VELOCITY_MAX_SAMPLES),
            velocity_stop_ms: positive_or(self.velocity_stop_ms, d.velocity_stop_ms),
            velocity_degree: self.velocity_degree.clamp(1, MAX_VELOCITY_DEGREE),
            // R2〜R4 は 0 で規則を切れる（負・NaN は既定値へ戻す）
            velocity_lift_off_ms: non_negative_or(self.velocity_lift_off_ms, d.velocity_lift_off_ms),
            velocity_min_span_ms: non_negative_or(self.velocity_min_span_ms, d.velocity_min_span_ms),
            velocity_min_sample_interval_ms: non_negative_or(
                self.velocity_min_sample_interval_ms,
                d.velocity_min_sample_interval_ms,
            ),
        }
    }

    /// 画素・秒へ換算した値を作る【純関数】。
    ///
    /// # 引数
    /// * `dp_scale` - 1 dp の画素数（canvas_layout の `CanvasScreenEnv::dp_scale()`。0 以下・NaN なら 1）
    pub fn metrics(&self, dp_scale: f32) -> GestureMetrics {
        let t = self.sanitized();
        let scale = if dp_scale.is_finite() && dp_scale > 0.0 { dp_scale } else { 1.0 };
        GestureMetrics {
            dp_scale: scale,
            touch_slop_px: t.touch_slop_dp * scale,
            tap_slop_px: t.tap_slop_dp * scale,
            long_press_secs: ms_to_secs(t.long_press_ms),
            press_delay_secs: ms_to_secs(t.press_delay_ms),
            tap_max_secs: ms_to_secs(t.tap_max_ms),
            min_fling_px: t.min_fling_velocity_dp * scale,
            max_fling_px: t.max_fling_velocity_dp * scale,
            velocity: VelocityParams {
                horizon_secs: ms_to_secs(t.velocity_horizon_ms),
                max_samples: t.velocity_max_samples,
                stop_secs: ms_to_secs(t.velocity_stop_ms),
                degree: t.velocity_degree,
                lift_off_secs: ms_to_secs(t.velocity_lift_off_ms),
                min_span_secs: ms_to_secs(t.velocity_min_span_ms),
                min_sample_interval_secs: ms_to_secs(t.velocity_min_sample_interval_ms),
            },
        }
    }
}

/// 速度の推定の設定（秒）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct VelocityParams {
    /// 時間の窓（秒）。
    pub horizon_secs: f64,
    /// 標本の最大数。
    pub max_samples: usize,
    /// 指が止まったとみなす標本の間隔（秒）。R1 と、R2 の見る区間の始まり（離した時刻 − これ）。
    pub stop_secs: f64,
    /// 多項式の次数（1〜2）。
    pub degree: usize,
    /// R2: 持ち上げの間（秒。0 = R2 を切る）。
    pub lift_off_secs: f64,
    /// R3: 推定に使う標本の時間の幅の下限（秒。0 = R3 を切る）。
    pub min_span_secs: f64,
    /// R4: 標本の間隔の下限（秒。0 = R4 を切る）。
    pub min_sample_interval_secs: f64,
}

/// 画素・秒へ換算した閾値（1 フレームぶん。アリーナと認識器はこれだけを読む）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct GestureMetrics {
    /// 1 dp の画素数（イベントの dp の値を作るのに使う）。
    pub dp_scale: f32,
    /// ドラッグが始まるまでの許容移動（画素）。
    pub touch_slop_px: f32,
    /// タップ・長押しの許容移動（画素）。
    pub tap_slop_px: f32,
    /// 長押しの時間（秒）。
    pub long_press_secs: f64,
    /// 押下の見た目を出すまでの待ち（秒）。
    pub press_delay_secs: f64,
    /// タップの時間の上限（秒。0 = 上限なし）。
    pub tap_max_secs: f64,
    /// フリックの最小の速さ（画素/秒）。
    pub min_fling_px: f32,
    /// フリックの速さの上限（画素/秒）。
    pub max_fling_px: f32,
    /// 速度の推定の設定。
    pub velocity: VelocityParams,
}

impl Default for GestureMetrics {
    /// 既定の表を 1 dp = 1 px で換算した値（テスト・画面の情報が無い文脈）。
    fn default() -> Self {
        GestureThresholds::default().metrics(1.0)
    }
}

// ─── プロジェクト設定からの読み込み ─────────────────────────────

/// project_settings.json の中の閾値の表の鍵。
pub const SETTINGS_KEY: &str = "gestures";

/// project_settings.json のテキストから閾値の表を読む【純関数】。
///
/// JSON が壊れている・鍵が無い・形が違うときは既定値（ジェスチャーを使わないプロジェクトの起動を妨げない）。
/// 書いてある欄だけ上書きし、壊れた値は既定値へ戻す。
pub fn parse_gesture_thresholds(settings_json: &str) -> GestureThresholds {
    let Ok(root) = serde_json::from_str::<serde_json::Value>(settings_json) else {
        return GestureThresholds::default();
    };
    root.get(SETTINGS_KEY)
        .and_then(|v| serde_json::from_value::<GestureThresholds>(v.clone()).ok())
        .unwrap_or_default()
        .sanitized()
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 既定値は出典の値（dp・ミリ秒・dp/秒）。
    #[test]
    fn defaults_match_sources() {
        let t = GestureThresholds::default();
        assert_eq!(t.touch_slop_dp, 8.0, "Android TOUCH_SLOP");
        assert_eq!(t.tap_slop_dp, 18.0, "Flutter kTouchSlop");
        assert_eq!(t.long_press_ms, 500.0, "roadmap W2-P3 / Flutter kLongPressTimeout");
        assert_eq!(t.press_delay_ms, 100.0, "Android TAP_TIMEOUT / Flutter kPressTimeout");
        assert_eq!((t.min_fling_velocity_dp, t.max_fling_velocity_dp), (50.0, 8000.0));
        assert_eq!((t.velocity_horizon_ms, t.velocity_max_samples, t.velocity_stop_ms), (100.0, 20, 40.0));
        // 頑健化（R2・R3 は 60 Hz の 1 フレーム、R4 は USB HID の 1 ms）
        assert!((t.velocity_lift_off_ms - 1000.0 / 60.0).abs() < 1e-4, "R2 = 1 フレーム");
        assert!((t.velocity_min_span_ms - 1000.0 / 60.0).abs() < 1e-4, "R3 = 1 フレーム");
        assert_eq!(t.velocity_min_sample_interval_ms, 1.0, "R4 = 1 ms");
        let v = GestureMetrics::default().velocity;
        assert!((v.lift_off_secs - 1.0 / 60.0).abs() < 1e-6 && (v.min_span_secs - 1.0 / 60.0).abs() < 1e-6);
        assert!((v.min_sample_interval_secs - 0.001).abs() < 1e-9);
    }

    /// 頑健化の鍵（R2〜R4）: 設定で上書きでき、0 は規則を切る値として残り、負・NaN は既定値へ戻す。
    #[test]
    fn robust_velocity_keys_parse_and_sanitize() {
        let t = parse_gesture_thresholds(
            r#"{"gestures":{"velocity_lift_off_ms":0,"velocity_min_span_ms":25,"velocity_min_sample_interval_ms":2}}"#,
        );
        assert_eq!((t.velocity_lift_off_ms, t.velocity_min_span_ms, t.velocity_min_sample_interval_ms), (0.0, 25.0, 2.0));
        let t = parse_gesture_thresholds(r#"{"gestures":{"velocity_lift_off_ms":-5,"velocity_min_span_ms":-1}}"#);
        let d = GestureThresholds::default();
        assert_eq!((t.velocity_lift_off_ms, t.velocity_min_span_ms), (d.velocity_lift_off_ms, d.velocity_min_span_ms));
        assert_eq!(t.velocity_min_sample_interval_ms, d.velocity_min_sample_interval_ms, "書いていない欄は既定値");
    }

    /// 換算: dp の倍率 1・2・3 で slop が 8・16・24 画素、時間は秒。
    #[test]
    fn metrics_scale_distances_by_dp() {
        for (scale, slop) in [(1.0, 8.0), (2.0, 16.0), (3.0, 24.0)] {
            let m = GestureThresholds::default().metrics(scale);
            assert_eq!(m.touch_slop_px, slop);
            assert_eq!(m.tap_slop_px, 18.0 * scale);
            assert_eq!(m.min_fling_px, 50.0 * scale);
        }
        let m = GestureMetrics::default();
        assert!((m.long_press_secs - 0.5).abs() < 1e-9);
        assert!((m.press_delay_secs - 0.1).abs() < 1e-9);
        assert_eq!(GestureThresholds::default().metrics(f32::NAN).dp_scale, 1.0, "壊れた倍率は 1");
    }

    /// 設定の JSON: 書いた欄だけ上書き、無い・壊れていれば既定値、壊れた値は既定値へ戻す。
    #[test]
    fn parse_from_project_settings() {
        let t = parse_gesture_thresholds(r#"{"target_fps":60,"gestures":{"long_press_ms":400,"touch_slop_dp":10}}"#);
        assert_eq!((t.long_press_ms, t.touch_slop_dp, t.tap_slop_dp), (400.0, 10.0, 18.0));
        assert_eq!(parse_gesture_thresholds("{}"), GestureThresholds::default());
        assert_eq!(parse_gesture_thresholds("not json"), GestureThresholds::default());
        let t = parse_gesture_thresholds(r#"{"gestures":{"long_press_ms":-1,"velocity_degree":9,"max_fling_velocity_dp":10}}"#);
        assert_eq!(t.long_press_ms, 500.0, "負は既定値");
        assert_eq!(t.velocity_degree, 2, "次数は 1〜2");
        assert_eq!(t.max_fling_velocity_dp, 50.0, "上限は下限まで引き上げる");
    }
}
