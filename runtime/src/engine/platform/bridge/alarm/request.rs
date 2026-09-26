// ============================================================
//  platform/bridge/alarm/request.rs — alarm.schedule / alarm.cancel の引数の読み取りと検査（W1-3）
//
//  【規則】（Java の :seed_platform の AlarmRequestReader と同じ。変えるときは両方）
//    id                … 必須。1〜MAX_ID_LENGTH 文字（Unicode の符号位置）の文字列
//    trigger_at_utc_ms … 必須。正の数（UTC の epoch ミリ秒。小数は切り捨て）。過去の時刻も受け付ける（すぐ鳴る）
//    sound_path / sound_asset … 任意の文字列（模擬は鳴らさないので、渡されたものを控えに持つだけ）
//    vibrate / keep_volume … 任意の真偽（無い・null は既定値）
//    force_volume      … 任意の数。負は「触らない」（-1）にそろえ、1 を超えたら 1
//    fade_in_seconds   … 任意の数。負は 0
//    max_ring_minutes  … 任意の数（小数は切り捨て）。1 未満は 1
//    title / body      … 任意の文字列（MAX_TEXT_LENGTH 文字まで）
//    payload_json      … 任意の文字列（MAX_PAYLOAD_LENGTH 文字まで。中身は検査しない）
//  欄があるのに型が違うときは Err(説明)（返答は invalid_argument。detail に説明）。
// ============================================================

use serde_json::Value;

use crate::engine::platform::bridge::wire::alarm as names;

/// 検査・正規化済みの予約の引数。
#[derive(Debug, Clone, PartialEq)]
pub struct AlarmRequest {
    /// 予約の ID。
    pub id: String,
    /// 鳴らす時刻（UTC の epoch ミリ秒）。
    pub trigger_at_utc_ms: i64,
    /// 音源（sound_path があればそれ、無ければ sound_asset。空なら既定の音）。
    pub sound: String,
    /// バイブするか。
    pub vibrate: bool,
    /// 鳴っている間の音量（0..1。names::VOLUME_UNCHANGED なら触らない）。
    pub force_volume: f64,
    /// 利用者が音量を下げても戻すか。
    pub keep_volume: bool,
    /// 音量の漸増の秒（0 以上）。
    pub fade_in_seconds: f64,
    /// 鳴り続ける上限の分（1 以上）。
    pub max_ring_minutes: i64,
    /// 鳴動の通知の題。
    pub title: String,
    /// 鳴動の通知の本文。
    pub body: String,
    /// アプリの任意の JSON（文字列のまま）。
    pub payload_json: String,
}

/// 音量の漸増の下限（秒）。
const MIN_FADE_IN_SECONDS: f64 = 0.0;

/// 長さの上限を設けない文字列の欄（音源のパス）の上限の代わり。
const UNLIMITED_TEXT_LENGTH: usize = usize::MAX;

/// alarm.schedule の引数を読む。
///
/// # 戻り値
/// 読めたら Ok(予約)。約束に合わなければ Err(説明。返答の detail)
pub fn read_schedule(request: &Value) -> Result<AlarmRequest, String> {
    let id = required_id(request)?;
    let trigger_at_utc_ms = required_trigger_at(request)?;
    let sound_path = opt_text(request, names::KEY_SOUND_PATH, UNLIMITED_TEXT_LENGTH)?;
    let sound = if sound_path.is_empty() { opt_text(request, names::KEY_SOUND_ASSET, UNLIMITED_TEXT_LENGTH)? } else { sound_path };
    let force_volume = normalize_volume(opt_number(request, names::KEY_FORCE_VOLUME, names::VOLUME_UNCHANGED)?);
    let fade_in_seconds = opt_number(request, names::KEY_FADE_IN_SECONDS, names::DEFAULT_FADE_IN_SECONDS)?.max(MIN_FADE_IN_SECONDS);
    // 小数は切り捨て（Java の Number.longValue と同じ。範囲外は飽和）
    let max_ring_minutes =
        (opt_number(request, names::KEY_MAX_RING_MINUTES, names::DEFAULT_MAX_RING_MINUTES as f64)? as i64).max(names::MIN_MAX_RING_MINUTES);
    Ok(AlarmRequest {
        id,
        trigger_at_utc_ms,
        sound,
        vibrate: opt_bool(request, names::KEY_VIBRATE, names::DEFAULT_VIBRATE)?,
        force_volume,
        keep_volume: opt_bool(request, names::KEY_KEEP_VOLUME, names::DEFAULT_KEEP_VOLUME)?,
        fade_in_seconds,
        max_ring_minutes,
        title: opt_text(request, names::KEY_TITLE, names::MAX_TEXT_LENGTH)?,
        body: opt_text(request, names::KEY_BODY, names::MAX_TEXT_LENGTH)?,
        payload_json: opt_text(request, names::KEY_PAYLOAD_JSON, names::MAX_PAYLOAD_LENGTH)?,
    })
}

/// alarm.cancel の引数（ID だけ）を読む。
pub fn read_id(request: &Value) -> Result<String, String> {
    required_id(request)
}

/// 必須の ID（1〜MAX_ID_LENGTH 文字の文字列）。
fn required_id(request: &Value) -> Result<String, String> {
    let Some(id) = request.get(names::KEY_ID).and_then(Value::as_str) else {
        return Err(format!("{} は文字列にしてください", names::KEY_ID));
    };
    let length = id.chars().count();
    if length == 0 || length > names::MAX_ID_LENGTH {
        return Err(format!("{} は 1〜{} 文字にしてください", names::KEY_ID, names::MAX_ID_LENGTH));
    }
    Ok(id.to_string())
}

/// 必須の予定時刻（正の数。小数は切り捨て）。
fn required_trigger_at(request: &Value) -> Result<i64, String> {
    let value = request.get(names::KEY_TRIGGER_AT_UTC_MS);
    let Some(trigger_at) = value.and_then(|v| v.as_i64().or_else(|| v.as_f64().map(|f| f as i64))) else {
        return Err(format!("{} は数にしてください", names::KEY_TRIGGER_AT_UTC_MS));
    };
    if trigger_at <= 0 {
        return Err(format!("{} は正の数（UTC の epoch ミリ秒）にしてください", names::KEY_TRIGGER_AT_UTC_MS));
    }
    Ok(trigger_at)
}

/// 音量の正規化（負は「触らない」、上限を超えたら上限）。
fn normalize_volume(volume: f64) -> f64 {
    if volume < 0.0 { names::VOLUME_UNCHANGED } else { volume.min(names::MAX_FORCE_VOLUME) }
}

/// 任意の真偽（無い・null は既定値。型が違えば誤り）。
fn opt_bool(request: &Value, key: &str, default: bool) -> Result<bool, String> {
    match request.get(key) {
        None | Some(Value::Null) => Ok(default),
        Some(Value::Bool(flag)) => Ok(*flag),
        Some(_) => Err(format!("{key} は真偽にしてください")),
    }
}

/// 任意の数（無い・null は既定値。型が違えば誤り。JSON は NaN・無限大を持てない）。
fn opt_number(request: &Value, key: &str, default: f64) -> Result<f64, String> {
    match request.get(key) {
        None | Some(Value::Null) => Ok(default),
        Some(value) => value.as_f64().ok_or_else(|| format!("{key} は数にしてください")),
    }
}

/// 任意の文字列（無い・null は空。型が違う・長すぎれば誤り）。
fn opt_text(request: &Value, key: &str, max_length: usize) -> Result<String, String> {
    match request.get(key) {
        None | Some(Value::Null) => Ok(String::new()),
        Some(Value::String(text)) if text.chars().count() <= max_length => Ok(text.clone()),
        Some(Value::String(_)) => Err(format!("{key} は {max_length} 文字までにしてください")),
        Some(_) => Err(format!("{key} は文字列にしてください")),
    }
}

// ============================================================
//  ユニットテスト（Java の AlarmRequestReader と同じ規則であること）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// 予定時刻の例（2026-09-27 ごろの UTC の epoch ミリ秒）。
    const TRIGGER: i64 = 1_790_000_000_000;

    /// ID と予定時刻だけなら、ほかは既定値。
    #[test]
    fn minimal_request_uses_defaults() {
        let request = read_schedule(&json!({ "id": "morning", "trigger_at_utc_ms": TRIGGER })).unwrap();
        assert_eq!(request.id, "morning");
        assert_eq!(request.trigger_at_utc_ms, TRIGGER);
        assert_eq!(request.sound, "");
        assert_eq!(request.vibrate, names::DEFAULT_VIBRATE);
        assert_eq!(request.force_volume, names::VOLUME_UNCHANGED);
        assert_eq!(request.keep_volume, names::DEFAULT_KEEP_VOLUME);
        assert_eq!(request.fade_in_seconds, names::DEFAULT_FADE_IN_SECONDS);
        assert_eq!(request.max_ring_minutes, names::DEFAULT_MAX_RING_MINUTES);
        assert_eq!((request.title.as_str(), request.body.as_str(), request.payload_json.as_str()), ("", "", ""));
    }

    /// 値の正規化: 負の音量は -1、1 超は 1、負の漸増は 0、安全弁は切り捨てて 1 以上。小数の予定時刻は切り捨て。
    #[test]
    fn values_are_normalized() {
        let low = read_schedule(&json!({ "id": "a", "trigger_at_utc_ms": 1234.9, "force_volume": -5.0,
            "fade_in_seconds": -2.0, "max_ring_minutes": 0 })).unwrap();
        assert_eq!(low.trigger_at_utc_ms, 1234);
        assert_eq!(low.force_volume, names::VOLUME_UNCHANGED);
        assert_eq!(low.fade_in_seconds, 0.0);
        assert_eq!(low.max_ring_minutes, names::MIN_MAX_RING_MINUTES);
        let high = read_schedule(&json!({ "id": "a", "trigger_at_utc_ms": TRIGGER, "force_volume": 3.0,
            "max_ring_minutes": 2.9, "vibrate": false, "keep_volume": true })).unwrap();
        assert_eq!(high.force_volume, names::MAX_FORCE_VOLUME);
        assert_eq!(high.max_ring_minutes, 2);
        assert!(!high.vibrate && high.keep_volume);
        let null_fields = read_schedule(&json!({ "id": "a", "trigger_at_utc_ms": TRIGGER, "title": null, "vibrate": null })).unwrap();
        assert_eq!(null_fields.title, "");
        assert_eq!(null_fields.vibrate, names::DEFAULT_VIBRATE);
    }

    /// 音源: sound_path があればそれ、無ければ sound_asset。
    #[test]
    fn sound_prefers_path_over_asset() {
        let both = read_schedule(&json!({ "id": "a", "trigger_at_utc_ms": TRIGGER,
            "sound_path": "/data/x.ogg", "sound_asset": "assets://sounds/x.ogg" })).unwrap();
        assert_eq!(both.sound, "/data/x.ogg");
        let asset = read_schedule(&json!({ "id": "a", "trigger_at_utc_ms": TRIGGER, "sound_asset": "assets://sounds/x.ogg" })).unwrap();
        assert_eq!(asset.sound, "assets://sounds/x.ogg");
    }

    /// ID の誤り: 無い・空・長すぎ・文字列でない。上限ちょうど（多バイト文字でも符号位置で数える）は通る。
    #[test]
    fn invalid_ids_are_rejected() {
        for bad in [json!({}), json!({ "id": "" }), json!({ "id": 7 }), json!({ "id": "あ".repeat(names::MAX_ID_LENGTH + 1) })] {
            assert!(read_id(&bad).is_err(), "{bad}");
        }
        assert_eq!(read_id(&json!({ "id": "あ".repeat(names::MAX_ID_LENGTH) })).unwrap().chars().count(), names::MAX_ID_LENGTH);
    }

    /// 予定時刻の誤り: 無い・0・負・文字列。
    #[test]
    fn invalid_trigger_times_are_rejected() {
        for bad in [json!(null), json!(0), json!(-1), json!("1790000000000")] {
            let error = read_schedule(&json!({ "id": "a", "trigger_at_utc_ms": bad })).unwrap_err();
            assert!(error.contains(names::KEY_TRIGGER_AT_UTC_MS), "{error}");
        }
    }

    /// 型の違う任意の欄と、長すぎる文字列は誤り（欄の名前が説明に入る）。
    #[test]
    fn wrong_types_and_long_texts_are_rejected() {
        let cases = [
            (names::KEY_VIBRATE, json!("yes")),
            (names::KEY_FORCE_VOLUME, json!("loud")),
            (names::KEY_MAX_RING_MINUTES, json!(true)),
            (names::KEY_TITLE, json!(1)),
            (names::KEY_BODY, json!("x".repeat(names::MAX_TEXT_LENGTH + 1))),
            (names::KEY_PAYLOAD_JSON, json!("x".repeat(names::MAX_PAYLOAD_LENGTH + 1))),
        ];
        for (key, value) in cases {
            let mut request = json!({ "id": "a", "trigger_at_utc_ms": TRIGGER });
            request[key] = value;
            let error = read_schedule(&request).unwrap_err();
            assert!(error.contains(key), "{key}: {error}");
        }
    }
}
