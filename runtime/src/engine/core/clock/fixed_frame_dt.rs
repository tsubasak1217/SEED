// ============================================================
//  fixed_frame_dt.rs — 検証用: フレームの経過時間（delta）を決まった値にする（環境変数 SEED_FIXED_FRAME_DT）
//
//  【なぜ要るか】ゲームの時間（anim_time）と壁時計の累計（ambient_time）は、フレームごとに実際に経った時間を
//  足して進む。最初のフレームの delta には起動（パイプラインの生成・シーンの読み込み）にかかった時間が入る
//  （PC の debug で 2.5 秒前後・実行ごとに数十〜数百 ms 揺れる）ので、同じフレーム番号で撮った画面でも
//  ゲームの時刻が実行ごと・版ごとにずれ、動く 3D（スキンメッシュ・水面）の画素の比べ合いが「揺れ」に埋もれる。
//  この値を決めると、すべてのフレームの delta をその値にする＝同じフレーム番号のゲームの時刻が実行に依らず揃い、
//  `SEED_SCREENSHOT_FRAMES` で撮った画面を版の前後で画素単位に比べられる（docs/rendering_profiles.md §14.6）。
//
//  【効かないもの】物理のスレッド（physics/thread.rs）は自分の時計で進むので固定されない。
//  ゲームの見た目を実時間からずらすので、検証の起動の時だけに使う（既定は未設定＝従来どおり実際の経過時間）。
// ============================================================

use std::sync::LazyLock;

/// フレームの経過時間を固定する環境変数（秒。例 `SEED_FIXED_FRAME_DT=0.016666668`）。
pub const FIXED_FRAME_DT_ENV: &str = "SEED_FIXED_FRAME_DT";

/// 受け付ける上限（秒）。これより大きい値は打ち間違いとみなして使わない（1 フレームで 1 秒進むほどの値は検証にならない）。
const MAX_FIXED_FRAME_DT_SECS: f32 = 1.0;

/// 起動時に 1 回だけ読んだ固定の delta（未設定・読めない値なら None＝従来どおり）。
static FIXED_FRAME_DT: LazyLock<Option<f32>> = LazyLock::new(|| {
    let raw = std::env::var(FIXED_FRAME_DT_ENV).ok();
    let parsed = parse_fixed_frame_dt(raw.as_deref());
    match (&raw, parsed) {
        (Some(_), Some(dt)) => eprintln!(
            "[SEED CLOCK] {FIXED_FRAME_DT_ENV}={dt}: フレームの経過時間をこの値に固定します（検証用。物理のスレッドは固定されません）"
        ),
        (Some(value), None) => eprintln!(
            "[SEED CLOCK][WARN] {FIXED_FRAME_DT_ENV}={value} は使えない値です（0 より大きく {MAX_FIXED_FRAME_DT_SECS} 以下の秒）。実際の経過時間で進めます"
        ),
        (None, _) => {}
    }
    parsed
});

/// 環境変数の値を秒として読む【純関数】（有限・0 より大きく上限以下だけを受け付ける）。
pub fn parse_fixed_frame_dt(value: Option<&str>) -> Option<f32> {
    let secs: f32 = value?.trim().parse().ok()?;
    (secs.is_finite() && secs > 0.0 && secs <= MAX_FIXED_FRAME_DT_SECS).then_some(secs)
}

/// 固定の delta（設定されていれば）。`Clock::tick` が実際の経過時間の代わりに使う。
pub fn fixed_frame_dt() -> Option<f32> {
    *FIXED_FRAME_DT
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 正の有限の秒だけを受け付け、空・数でない・0 以下・上限超え・無限は使わない。
    #[test]
    fn parses_only_positive_finite_seconds_within_limit() {
        assert_eq!(parse_fixed_frame_dt(Some("0.016666668")), Some(0.016666668));
        assert_eq!(parse_fixed_frame_dt(Some(" 0.02 ")), Some(0.02));
        assert_eq!(parse_fixed_frame_dt(Some("1")), Some(1.0));
        for bad in ["", "abc", "0", "-0.01", "1.5", "inf", "NaN"] {
            assert_eq!(parse_fixed_frame_dt(Some(bad)), None, "{bad:?}");
        }
        assert_eq!(parse_fixed_frame_dt(None), None);
    }
}
