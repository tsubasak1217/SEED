// ============================================================
//  platform/bridge/haptics/mod.rs — 触感（SEED.Platform の Haptics。W1-6）のエンジン側の共通部品
//
//  【置くもの】haptics.vibrate の引数 { ms } の読み取り。
//  【ms の規則】（Java の local/HapticsVibrateCommand と同じ。変えるときは両方）
//    数でない・有限でない・MIN_VIBRATE_MS（1）未満 → 誤り（invalid_argument）。小数は切り捨て。MAX_VIBRATE_MS（5000）を超えたらそろえる。
//  Android では振動はメインプロセスの Java（platform/haptics/HapticFeedback）が鳴らし、デスクトップでは模擬
//  （desktop_sim/haptics_commands.rs）がここを使って同じ値を返し、記録する。名前は wire::haptics。全体像は docs/android.md §25.15。
// ============================================================

use serde_json::Value;

use crate::engine::platform::bridge::wire::haptics as names;

/// haptics.vibrate の引数 `{ ms }` を読む。
///
/// # 戻り値
/// 読めたら Ok(そろえた後の長さ〈ミリ秒〉)。数でない・有限でない・下限未満なら Err(説明。返答は invalid_argument)
pub fn read_vibrate_ms(request: &Value) -> Result<i64, String> {
    let requested = request.get(names::KEY_MS).and_then(Value::as_f64).unwrap_or(f64::NAN);
    // 下限の比較は f64 のまま（1 未満の小数を切り捨てて 0 にしてから比べない）
    if !requested.is_finite() || requested < names::MIN_VIBRATE_MS as f64 {
        return Err(format!("{} は {} 以上の数にしてください", names::KEY_MS, names::MIN_VIBRATE_MS));
    }
    // 小数は切り捨て（as は範囲外を飽和させる）、上限にそろえる
    Ok((requested.trunc() as i64).min(names::MAX_VIBRATE_MS))
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// 下限・切り捨て・上限にそろえる。
    #[test]
    fn vibrate_ms_is_truncated_and_clamped() {
        let cases = [
            (json!(1), names::MIN_VIBRATE_MS),
            (json!(1.9), 1),
            (json!(40), 40),
            (json!(names::MAX_VIBRATE_MS), names::MAX_VIBRATE_MS),
            (json!(names::MAX_VIBRATE_MS + 1), names::MAX_VIBRATE_MS),
            (json!(1.0e12), names::MAX_VIBRATE_MS),
        ];
        for (ms, expected) in cases {
            assert_eq!(read_vibrate_ms(&json!({ "ms": ms })), Ok(expected), "{ms}");
        }
    }

    /// 誤り: 欄が無い・数でない・下限未満（0・負・1 未満の小数）。説明に欄の名前が入る。
    #[test]
    fn vibrate_ms_rejects_missing_non_numbers_and_too_small() {
        for bad in [json!({}), json!({ "ms": "40" }), json!({ "ms": null }), json!({ "ms": 0 }), json!({ "ms": -5 }), json!({ "ms": 0.5 })] {
            assert!(read_vibrate_ms(&bad).unwrap_err().contains(names::KEY_MS), "{bad}");
        }
    }
}
