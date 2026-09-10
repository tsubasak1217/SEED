// ============================================================
//  frame_pacing.rs — 目標フレームレート制御とフレーム統計（fps 計測）
//
//  【役割】
//  1 フレームの末尾で「次のフレームまでどれだけ待つか」を決めて待ち、
//  同時に「直近 1 秒の平均 fps」「直近フレームの実時間」を集計する。
//
//  【なぜ必要か】
//  イベントループは ControlFlow::Poll ＋ 毎フレーム request_redraw で回っており、
//  present がブロックしない構成（Mailbox / Immediate）だと CPU・GPU が上限なく
//  回り続ける。結果として、たかだか 60fps あれば十分なゲームでも GPU 使用率が
//  張り付き、発熱・ファン全開・ノート PC のバッテリー消費を招く。
//  従来は「非フォーカス時だけ 30fps に抑える」応急処置しか無かったため、
//  フォーカスがある通常プレイ中はまったく制限が掛かっていなかった。
//
//  【なぜ独立ファイルなのか】
//  待ち時間の計算は「目標 fps・フォーカス・実行モード」だけで決まる純粋な算術であり、
//  ウィンドウや GPU に触れずに単体テストできる。frame_renderer.rs（1 万行規模）へ
//  埋めるとテストも読み解きも困難になるため、純関数と統計だけをここへ集約する。
//
//  【スリープ精度について】
//  Windows の thread::sleep はスケジューラのタイマ粒度（既定 15.6ms）に丸められ、
//  16.6ms 待ちたいのに 31ms 待たされる（＝ 60fps 狙いが 32fps になる）ことがある。
//  対策は 2 段構え。
//    1. timeBeginPeriod(1) で粒度を 1ms へ上げる（プロセスで一度だけ）。
//    2. それでも残る誤差に備え、目標の手前 SPIN_MARGIN までを sleep で待ち、
//       最後の猶予だけビジースピンで詰める（1〜2ms のスピンなら CPU 負荷は軽微で、
//       フレーム境界の揺れは目に見えて減る）。
// ============================================================

use std::sync::atomic::{AtomicU32, Ordering};
use std::time::{Duration, Instant};

use super::{App, RuntimeMode};

// ── 設定値・定数（マジックナンバー禁止のため全てここに集約）─────────────

/// project_settings.json 内のキー名（目標フレームレート）。
const KEY_TARGET_FPS: &str = "target_fps";

/// 目標フレームレートの既定値（fps）。設定が無い・壊れているときはこれを使う。
pub const DEFAULT_TARGET_FPS: u32 = 60;

/// 「無制限」を表す目標フレームレート値。設定ファイルでも 0 を無制限として扱う。
pub const TARGET_FPS_UNLIMITED: u32 = 0;

/// 目標フレームレートの下限（無制限＝0 を除く）。
/// 1fps 未満は事実上フリーズなので許可しない。
pub const TARGET_FPS_MIN: u32 = 1;

/// 目標フレームレートの上限。
/// 高リフレッシュレートモニタ（360Hz 等）でも十分な余裕があり、
/// これを超える値は「実質無制限」なので丸めても体感差が無い。
pub const TARGET_FPS_MAX: u32 = 1000;

/// 非フォーカス時のフレームレート上限（fps）。
///
/// ウィンドウが非アクティブ／遮蔽されると present が即座に返るため、
/// 制限が無いとループが毎秒数千フレームで暴走する。バックグラウンド描画に
/// 見栄えは要らないので、目標 fps とこの値の小さい方まで落とす。
pub const UNFOCUSED_MAX_FPS: u32 = 30;

/// スピン猶予。目標時刻のこれだけ手前で sleep をやめ、以降はビジースピンで詰める。
///
/// Windows のタイマ粒度を 1ms へ上げても sleep は「最低でも指定時間」しか保証せず、
/// 1ms 前後の超過が起こる。2ms 見ておけば超過を吸収でき、スピン時間も
/// 1 フレーム当たり最大 2ms（60fps で CPU の 12% 程度・1 コア分）に収まる。
const SPIN_MARGIN: Duration = Duration::from_millis(2);

/// fps 平均を取る窓の長さ（秒）。仕様どおり「直近 1 秒の平均」。
const FPS_WINDOW_SECS: f64 = 1.0;

/// 1 秒あたりのマイクロ秒。フレーム予算の算出に使う。
const MICROS_PER_SEC: u64 = 1_000_000;

/// 秒 → ミリ秒の換算係数。フレーム時間の公開値に使う。
const MILLIS_PER_SEC: f32 = 1_000.0;

// ── スクリプトへ公開する最新フレーム統計（プロセス全体で 1 組）─────────
//
// スクリプト API（SEED.Time.Fps / FrameTimeMs）は RawFrameContext 経由で値を受け取る。
// RawFrameContext を作る scripting::mod は App を参照できないため、
// 「最新値だけ」をここのアトミックへ発行して読み出させる。
// f32 はアトミックにできないのでビットパターン（to_bits / from_bits）で持つ。

/// 直近 1 秒の平均フレームレート（f32 のビットパターン）。
static LATEST_FPS_BITS: AtomicU32 = AtomicU32::new(0);

/// 直近フレームの実時間（ミリ秒。f32 のビットパターン）。
static LATEST_FRAME_MS_BITS: AtomicU32 = AtomicU32::new(0);

/// 直近 1 秒の平均フレームレートを読む（スクリプト公開用）。
///
/// 起動直後（最初の窓が閉じる前）は 0.0 を返す。
pub fn latest_fps() -> f32 {
    f32::from_bits(LATEST_FPS_BITS.load(Ordering::Relaxed))
}

/// プロジェクト設定の目標フレームレート（0 = 無制限）。
///
/// `App::target_fps` と同じ値だが、FFI（`ffi_app_env`）は `App` を参照できないため
/// 起動時に一度だけここへ写し取る。実行中に変化しない。
static CONFIGURED_TARGET_FPS: AtomicU32 = AtomicU32::new(DEFAULT_TARGET_FPS);

/// 設定を読み終えた時点で目標フレームレートを公開する（起動時に 1 回だけ呼ぶ）。
pub fn publish_configured_target_fps(fps: u32) {
    CONFIGURED_TARGET_FPS.store(fps, Ordering::Relaxed);
}

/// プロジェクト設定の目標フレームレートを読む（スクリプト公開用。0 = 無制限）。
pub fn configured_target_fps() -> u32 {
    CONFIGURED_TARGET_FPS.load(Ordering::Relaxed)
}

/// 直近フレームの実時間（ミリ秒）を読む（スクリプト公開用）。
///
/// 「実時間」はフレーム開始から次フレーム開始までの実測周期であり、
/// フレーム制限の待ち時間も含む（＝ 60fps 制限中は約 16.7ms になる）。
/// 描画そのものに掛かった時間を見たい場合はプロファイラを使うこと。
pub fn latest_frame_time_ms() -> f32 {
    f32::from_bits(LATEST_FRAME_MS_BITS.load(Ordering::Relaxed))
}

// ── 純関数（単体テスト対象）───────────────────────────────────

/// project_settings.json のテキストから目標フレームレートを読む純関数。
///
/// JSON が壊れている・キーが無い・値が数値でない場合は既定値を返す。
/// 値は `sanitize_target_fps` で有効範囲へ丸める。
pub fn parse_target_fps(json: &str) -> u32 {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(json) else {
        return DEFAULT_TARGET_FPS;
    };
    match v[KEY_TARGET_FPS].as_i64() {
        Some(n) => sanitize_target_fps_i64(n),
        None    => DEFAULT_TARGET_FPS,
    }
}

/// 設定ファイル由来の整数値（負値もあり得る）を有効な目標 fps へ丸める。
///
/// - 0        → 無制限（そのまま）
/// - 負値     → 既定値（「無制限のつもりで -1 を書いた」より、既定へ戻すほうが安全）
/// - 範囲外   → 上下限へクランプ
fn sanitize_target_fps_i64(n: i64) -> u32 {
    if n == TARGET_FPS_UNLIMITED as i64 { return TARGET_FPS_UNLIMITED; }
    if n < 0 { return DEFAULT_TARGET_FPS; }
    sanitize_target_fps(n.min(u32::MAX as i64) as u32)
}

/// 目標 fps を有効範囲へ丸める（0 = 無制限は素通し）。
pub fn sanitize_target_fps(fps: u32) -> u32 {
    if fps == TARGET_FPS_UNLIMITED { return TARGET_FPS_UNLIMITED; }
    fps.clamp(TARGET_FPS_MIN, TARGET_FPS_MAX)
}

/// このフレームで実際に守るべき目標 fps を決める純関数。
///
/// - `is_edit`  : Edit モード（エディタ埋め込みビューポート）かどうか。
///                編集操作の応答性を落としたくないので**常に無制限**（従来動作）。
/// - `focused`  : ウィンドウにフォーカスがあるか。無い間は `UNFOCUSED_MAX_FPS` を上限に
///                重ねる（無制限設定でも 30fps へ落ちる）。
///
/// 戻り値 0 は「無制限（待たない）」を意味する。
pub fn effective_target_fps(configured: u32, focused: bool, is_edit: bool) -> u32 {
    // Edit モードは従来どおり一切制限しない。
    if is_edit { return TARGET_FPS_UNLIMITED; }

    let base = sanitize_target_fps(configured);
    if focused { return base; }

    // 非フォーカス時: 無制限設定なら UNFOCUSED_MAX_FPS、そうでなければ小さい方。
    if base == TARGET_FPS_UNLIMITED {
        UNFOCUSED_MAX_FPS
    } else {
        base.min(UNFOCUSED_MAX_FPS)
    }
}

/// 目標 fps から 1 フレームの予算（時間）を求める純関数。
///
/// 無制限（0）のときは `None`（＝待ちを一切行わない）。
pub fn frame_budget(fps: u32) -> Option<Duration> {
    if fps == TARGET_FPS_UNLIMITED { return None; }
    Some(Duration::from_micros(MICROS_PER_SEC / fps as u64))
}

/// 予算・経過時間・スピン猶予から「thread::sleep に渡すべき時間」を求める純関数。
///
/// - 既に予算を使い切っている（重いフレーム）→ `None`（待たない）
/// - 残りが猶予以下 → `None`（sleep せずスピンだけで詰める）
/// - それ以外 → 残り − 猶予
pub fn sleep_duration(
    budget:      Duration,
    elapsed:     Duration,
    spin_margin: Duration,
) -> Option<Duration> {
    let remaining = budget.checked_sub(elapsed)?;
    // checked_sub は「猶予以下」のとき None を返すので、そのままスピン専用になる。
    remaining.checked_sub(spin_margin)
}

// ── フレーム統計（fps 計測）────────────────────────────────────

/// 直近 1 秒の平均 fps と、直近フレームの実時間を集計する。
///
/// 平均を「移動平均」ではなく「1 秒ごとの窓」で取るのは、
/// 表示値がチラチラ動かず読み取りやすいため（デバッグ表示の用途に合う）。
pub struct FrameStats {
    /// 現在の集計窓を開始した時刻。
    window_start:  Instant,
    /// 現在の集計窓で数えたフレーム数。
    window_frames: u32,
    /// 直近に確定した平均 fps（窓が閉じるまでは前回値を保持する）。
    fps:           f32,
    /// 直近フレームの実時間（ミリ秒）。
    frame_time_ms: f32,
}

impl Default for FrameStats {
    fn default() -> Self {
        Self {
            window_start:  Instant::now(),
            window_frames: 0,
            fps:           0.0,
            frame_time_ms: 0.0,
        }
    }
}

impl FrameStats {
    /// 1 フレーム分の実時間を記録し、必要なら平均 fps を更新する。
    ///
    /// `frame_duration` はフレーム開始から（フレーム制限の待ちを含む）末尾までの実測時間。
    pub fn record(&mut self, frame_duration: Duration) {
        self.frame_time_ms = frame_duration.as_secs_f32() * MILLIS_PER_SEC;
        self.window_frames = self.window_frames.saturating_add(1);

        let elapsed = self.window_start.elapsed().as_secs_f64();
        if elapsed >= FPS_WINDOW_SECS {
            // 経過が 0 になることは実質無いが、0 除算だけは構造的に防ぐ。
            if elapsed > 0.0 {
                self.fps = (self.window_frames as f64 / elapsed) as f32;
            }
            self.window_start  = Instant::now();
            self.window_frames = 0;
        }

        // スクリプト公開用の最新値を発行する。
        LATEST_FPS_BITS.store(self.fps.to_bits(), Ordering::Relaxed);
        LATEST_FRAME_MS_BITS.store(self.frame_time_ms.to_bits(), Ordering::Relaxed);
    }

    /// 直近 1 秒の平均 fps。
    pub fn fps(&self) -> f32 { self.fps }

    /// 直近フレームの実時間（ミリ秒）。
    pub fn frame_time_ms(&self) -> f32 { self.frame_time_ms }
}

// ── OS タイマ粒度（Windows のみ）──────────────────────────────

/// timeBeginPeriod をプロセスで一度だけ呼ぶためのガード。
static HIGH_RES_TIMER_ONCE: std::sync::Once = std::sync::Once::new();

/// 要求するタイマ粒度（ミリ秒）。1ms が Windows で指定できる最小値。
#[cfg(windows)]
const TIMER_PERIOD_MS: u32 = 1;

/// スリープの粒度を 1ms へ上げる（プロセスで一度だけ有効）。
///
/// 既定の粒度（15.6ms）のままだと 60fps（16.7ms）狙いの sleep が 31ms に丸められ、
/// フレームレートが半分に落ちる。timeEndPeriod は呼ばない ——
/// プロセス終了時に OS が自動で解除するうえ、途中で解除するとその後のフレームが
/// 再び粗い粒度に戻ってしまうため。
///
/// Windows 以外では何もしない（Linux / macOS の sleep はもともと十分に細かい）。
pub fn ensure_high_resolution_timer() {
    HIGH_RES_TIMER_ONCE.call_once(|| {
        #[cfg(windows)]
        unsafe {
            // Safety: timeBeginPeriod は引数を検証する純粋な OS 呼び出しで、
            // 不正値でもエラーコードを返すだけ（未定義動作は無い）。
            windows_sys::Win32::Media::timeBeginPeriod(TIMER_PERIOD_MS);
        }
    });
}

// ── App への組み込み ─────────────────────────────────────────

impl App {
    /// フレーム末尾で目標フレームレートまで待ち、フレーム統計を更新する
    /// 【フレームレート制限の唯一の入口】。
    ///
    /// 旧 `pace_frame_if_unfocused`（非フォーカス時のみ 30fps 制限）を置き換えたもので、
    /// フォーカスの有無にかかわらず `target_fps` を守る。判定は
    /// `effective_target_fps` に集約してあり、この関数は「待つ」「数える」だけを行う。
    ///
    /// `frame_start` はそのフレームの計測開始時刻（`handle_redraw_requested` 冒頭で取得）。
    /// 早期 return するフレーム（描画一時停止・最小化）からも呼ばれるため、
    /// どの経路を通っても 1 フレーム 1 回だけ呼ぶこと。
    pub(super) fn pace_frame(&mut self, frame_start: Instant) {
        let fps = effective_target_fps(
            self.target_fps,
            self.window_focused,
            self.mode == RuntimeMode::Edit,
        );

        if let Some(budget) = frame_budget(fps) {
            // 粒度を上げるのは「実際に待つ」ときだけで十分
            //（無制限運用のエディタでプロセス全体のタイマ粒度を上げない）。
            ensure_high_resolution_timer();

            // 1 段目: 目標の SPIN_MARGIN 手前まで sleep（CPU を明け渡す）。
            if let Some(sleep) = sleep_duration(budget, frame_start.elapsed(), SPIN_MARGIN) {
                std::thread::sleep(sleep);
            }
            // 2 段目: 残りをビジースピンで詰める（sleep の丸め誤差を吸収）。
            while frame_start.elapsed() < budget {
                std::hint::spin_loop();
            }
        }

        // 待ちを含めた実測周期を統計へ記録する（＝ fps の分母になる時間）。
        self.frame_stats.record(frame_start.elapsed());
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    // ── parse_target_fps ─────────────────────────────────────

    /// キーが無い・壊れた JSON・型違いは既定値。
    #[test]
    fn missing_or_broken_json_is_default() {
        assert_eq!(parse_target_fps("{}"), DEFAULT_TARGET_FPS);
        assert_eq!(parse_target_fps(""), DEFAULT_TARGET_FPS);
        assert_eq!(parse_target_fps("not json"), DEFAULT_TARGET_FPS);
        assert_eq!(parse_target_fps(r#"{"target_fps": "60"}"#), DEFAULT_TARGET_FPS);
        assert_eq!(parse_target_fps(r#"{"target_fps": null}"#), DEFAULT_TARGET_FPS);
    }

    /// 明示値の読み取りと丸め。
    #[test]
    fn explicit_values_are_parsed_and_clamped() {
        assert_eq!(parse_target_fps(r#"{"target_fps": 30}"#), 30);
        assert_eq!(parse_target_fps(r#"{"target_fps": 144}"#), 144);
        // 0 は「無制限」としてそのまま通す
        assert_eq!(parse_target_fps(r#"{"target_fps": 0}"#), TARGET_FPS_UNLIMITED);
        // 上限超過はクランプ
        assert_eq!(parse_target_fps(r#"{"target_fps": 99999}"#), TARGET_FPS_MAX);
        // 負値は既定へ（無制限のつもりの -1 でゲームが止まらないように）
        assert_eq!(parse_target_fps(r#"{"target_fps": -1}"#), DEFAULT_TARGET_FPS);
    }

    // ── effective_target_fps ─────────────────────────────────

    /// Edit モードは設定・フォーカスによらず常に無制限（従来動作の維持）。
    #[test]
    fn edit_mode_is_always_unlimited() {
        assert_eq!(effective_target_fps(60, true,  true), TARGET_FPS_UNLIMITED);
        assert_eq!(effective_target_fps(60, false, true), TARGET_FPS_UNLIMITED);
        assert_eq!(effective_target_fps(0,  false, true), TARGET_FPS_UNLIMITED);
    }

    /// フォーカス時は設定値をそのまま守る（0 = 無制限も含む）。
    #[test]
    fn focused_uses_configured_value() {
        assert_eq!(effective_target_fps(60,  true, false), 60);
        assert_eq!(effective_target_fps(144, true, false), 144);
        assert_eq!(effective_target_fps(0,   true, false), TARGET_FPS_UNLIMITED);
    }

    /// 非フォーカス時は UNFOCUSED_MAX_FPS を上限に重ねる。
    #[test]
    fn unfocused_is_capped() {
        // 無制限設定でも 30 まで落ちる（暴走ループの防止が主目的）
        assert_eq!(effective_target_fps(0, false, false), UNFOCUSED_MAX_FPS);
        // 目標のほうが高ければ 30 へ
        assert_eq!(effective_target_fps(60, false, false), UNFOCUSED_MAX_FPS);
        // 目標のほうが低ければ目標を守る（さらに上げてしまわない）
        assert_eq!(effective_target_fps(15, false, false), 15);
    }

    // ── frame_budget ─────────────────────────────────────────

    /// 無制限は None、有限値は 1/fps。
    #[test]
    fn budget_matches_fps() {
        assert_eq!(frame_budget(TARGET_FPS_UNLIMITED), None);
        assert_eq!(frame_budget(60), Some(Duration::from_micros(16_666)));
        assert_eq!(frame_budget(30), Some(Duration::from_micros(33_333)));
    }

    // ── sleep_duration ───────────────────────────────────────

    /// 余裕があるフレームでは「残り − 猶予」を sleep する。
    #[test]
    fn sleeps_remaining_minus_margin() {
        let budget  = Duration::from_millis(16);
        let elapsed = Duration::from_millis(4);
        let margin  = Duration::from_millis(2);
        assert_eq!(
            sleep_duration(budget, elapsed, margin),
            Some(Duration::from_millis(10)),
        );
    }

    /// 残りが猶予以下ならスピンだけ（sleep しない）。
    #[test]
    fn no_sleep_within_spin_margin() {
        let budget = Duration::from_millis(16);
        let margin = Duration::from_millis(2);
        assert_eq!(sleep_duration(budget, Duration::from_millis(15), margin), None);
        // ちょうど猶予ぶんだけ残っている境界は 0 秒 sleep（実質スピンのみ）
        assert_eq!(
            sleep_duration(budget, Duration::from_millis(14), margin),
            Some(Duration::ZERO),
        );
    }

    /// 予算を使い切った重いフレームは待たない（遅れをさらに広げない）。
    #[test]
    fn no_sleep_when_over_budget() {
        let budget = Duration::from_millis(16);
        let margin = Duration::from_millis(2);
        assert_eq!(sleep_duration(budget, Duration::from_millis(16), margin), None);
        assert_eq!(sleep_duration(budget, Duration::from_millis(50), margin), None);
    }

    // ── FrameStats ───────────────────────────────────────────

    /// フレーム時間はミリ秒で即座に反映される。
    #[test]
    fn frame_time_is_recorded_in_millis() {
        let mut stats = FrameStats::default();
        stats.record(Duration::from_micros(16_666));
        assert!((stats.frame_time_ms() - 16.666).abs() < 0.01);
        // 窓が閉じるまで fps は初期値 0 のまま（前回値保持の確認）
        assert_eq!(stats.fps(), 0.0);
    }

    /// 集計窓（1 秒）が閉じると平均 fps が確定する。
    #[test]
    fn fps_is_published_after_window() {
        let mut stats = FrameStats::default();
        // 窓の開始時刻を 1 秒前へずらし、次の record で窓が閉じる状態にする。
        stats.window_start  = Instant::now() - Duration::from_secs_f64(FPS_WINDOW_SECS);
        stats.window_frames = 59;
        stats.record(Duration::from_micros(16_666));
        // 60 フレーム／約 1 秒 → 60fps 前後（スケジューラ誤差を見て広めに判定）
        assert!(stats.fps() > 50.0 && stats.fps() < 70.0, "fps={}", stats.fps());
        // 公開値（スクリプト API 側が読む静的値）が発行されていること。
        // 静的値はプロセス共有なので、並列実行される他テストの record と競合しうる。
        // 「同一値であること」までは断定できないため、有限な値が入ったことだけを見る。
        assert!(latest_fps().is_finite());
        assert!(latest_frame_time_ms().is_finite());
    }
}
