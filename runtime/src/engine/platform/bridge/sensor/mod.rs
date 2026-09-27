// ============================================================
//  platform/bridge/sensor/mod.rs — センサー（SEED.Platform の Sensors。W1-8）のエンジン側の共通部品
//
//  【置くもの】sensor.* の引数の読み取り（種類 kind・頻度 rate_hz・模擬の標本 x / y / z）と標本の大きさ。
//  【規則】（Java の platform/local/SensorArguments と同じ。変えるときは両方）
//    kind    … 必須の文字列で、wire::sensor::KINDS のどれか（今は linear_acceleration だけ）。それ以外は invalid_argument
//    rate_hz … 無い・null なら DEFAULT_RATE_HZ（50）。数でない・有限でない・MIN_RATE_HZ（1）未満は invalid_argument。
//               小数は切り捨て、MAX_RATE_HZ（200）を超えたらそろえる（Android 12 以降の registerListener の上限。超えると
//               デバッグ版の APK では SecurityException になる）
//    x/y/z   … sim_inject だけ。どれも必須の有限の数（m/s²）
//  Android ではセンサーはメインプロセスの Java（platform/sensor/）が受け、デスクトップでは模擬（desktop_sim/sensor_commands.rs）が
//  ここを使って同じ値を返す。名前は wire::sensor。全体像は docs/android.md §25.16。
// ============================================================

use serde_json::Value;

use crate::engine::platform::bridge::wire::sensor as names;

/// 引数の `kind` を読む（約束の種類の名前を返す）。
///
/// # 戻り値
/// 約束の種類なら Ok(wire::sensor::KINDS の中の同じ文字列)。無い・文字列でない・知らない種類なら Err(説明。返答は invalid_argument)
pub fn read_kind(request: &Value) -> Result<&'static str, String> {
    let Some(requested) = request.get(names::KEY_KIND).and_then(Value::as_str) else {
        return Err(format!("{} は文字列にしてください", names::KEY_KIND));
    };
    names::KINDS
        .iter()
        .copied()
        .find(|kind| *kind == requested)
        .ok_or_else(|| format!("{} が約束の種類ではありません（{requested}）", names::KEY_KIND))
}

/// start の引数の `rate_hz` を読む。
///
/// # 戻り値
/// 読めたら Ok(そろえた後の頻度〈Hz〉。無い・null なら既定値)。数でない・有限でない・下限未満なら Err(説明。返答は invalid_argument)
pub fn read_rate_hz(request: &Value) -> Result<i64, String> {
    let requested = match request.get(names::KEY_RATE_HZ) {
        // 省略（と null）は既定の頻度
        None | Some(Value::Null) => return Ok(names::DEFAULT_RATE_HZ),
        Some(value) => value.as_f64().unwrap_or(f64::NAN),
    };
    // 下限の比較は f64 のまま（1 未満の小数を切り捨てて 0 にしてから比べない）
    if !requested.is_finite() || requested < names::MIN_RATE_HZ as f64 {
        return Err(format!("{} は {} 以上の数にしてください", names::KEY_RATE_HZ, names::MIN_RATE_HZ));
    }
    // 小数は切り捨て（as は範囲外を飽和させる）、上限にそろえる
    Ok((requested.trunc() as i64).min(names::MAX_RATE_HZ))
}

/// sim_inject の引数の `x` / `y` / `z` を読む（模擬だけ）。
///
/// # 戻り値
/// 3 つとも有限の数なら Ok([x, y, z])。どれかが無い・数でない・有限でないなら Err(説明。返答は invalid_argument)
pub fn read_sample(request: &Value) -> Result<[f64; 3], String> {
    let mut sample = [0.0; 3];
    for (slot, key) in sample.iter_mut().zip([names::KEY_X, names::KEY_Y, names::KEY_Z]) {
        match request.get(key).and_then(Value::as_f64) {
            Some(value) if value.is_finite() => *slot = value,
            _ => return Err(format!("{key} は有限の数にしてください")),
        }
    }
    Ok(sample)
}

/// 標本の大きさ √(x²+y²+z²)（read の peak_magnitude の元。Java の SampleAccumulator と同じ式）。
pub fn magnitude(sample: [f64; 3]) -> f64 {
    let [x, y, z] = sample;
    (x * x + y * y + z * z).sqrt()
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// kind: 約束の種類だけを通す（大文字・空・数・欠け・知らない種類は誤り。説明に欄の名前が入る）。
    #[test]
    fn kind_accepts_only_known_kinds() {
        assert_eq!(read_kind(&json!({ "kind": "linear_acceleration" })), Ok(names::KIND_LINEAR_ACCELERATION));
        for bad in [json!({}), json!({ "kind": "" }), json!({ "kind": "LINEAR_ACCELERATION" }), json!({ "kind": "gyroscope" }), json!({ "kind": 1 })] {
            assert!(read_kind(&bad).unwrap_err().contains(names::KEY_KIND), "{bad}");
        }
    }

    /// rate_hz: 省略・null は既定値、切り捨て、上限にそろえる。
    #[test]
    fn rate_is_defaulted_truncated_and_clamped() {
        let cases = [
            (json!({}), names::DEFAULT_RATE_HZ),
            (json!({ "rate_hz": null }), names::DEFAULT_RATE_HZ),
            (json!({ "rate_hz": 1 }), names::MIN_RATE_HZ),
            (json!({ "rate_hz": 1.9 }), 1),
            (json!({ "rate_hz": 50 }), 50),
            (json!({ "rate_hz": names::MAX_RATE_HZ }), names::MAX_RATE_HZ),
            (json!({ "rate_hz": names::MAX_RATE_HZ + 1 }), names::MAX_RATE_HZ),
            (json!({ "rate_hz": 1.0e12 }), names::MAX_RATE_HZ),
        ];
        for (request, expected) in cases {
            assert_eq!(read_rate_hz(&request), Ok(expected), "{request}");
        }
    }

    /// rate_hz の誤り: 数でない・下限未満（0・負・1 未満の小数）。説明に欄の名前が入る。
    #[test]
    fn rate_rejects_non_numbers_and_too_small() {
        for bad in [json!({ "rate_hz": "50" }), json!({ "rate_hz": 0 }), json!({ "rate_hz": -5 }), json!({ "rate_hz": 0.5 }), json!({ "rate_hz": true })] {
            assert!(read_rate_hz(&bad).unwrap_err().contains(names::KEY_RATE_HZ), "{bad}");
        }
    }

    /// 模擬の標本: 3 つとも有限の数だけを通す。
    #[test]
    fn sample_needs_three_finite_numbers() {
        assert_eq!(read_sample(&json!({ "x": 1, "y": -2.5, "z": 0 })), Ok([1.0, -2.5, 0.0]));
        for bad in [json!({ "x": 1, "y": 2 }), json!({ "x": "1", "y": 2, "z": 3 }), json!({ "x": null, "y": 2, "z": 3 })] {
            assert!(read_sample(&bad).is_err(), "{bad}");
        }
    }

    /// 大きさは √(x²+y²+z²)（3-4-12 → 13）。
    #[test]
    fn magnitude_is_euclidean_length() {
        assert_eq!(magnitude([3.0, 4.0, 12.0]), 13.0);
        assert_eq!(magnitude([0.0, 0.0, 0.0]), 0.0);
        assert_eq!(magnitude([-3.0, -4.0, 0.0]), 5.0);
    }
}
