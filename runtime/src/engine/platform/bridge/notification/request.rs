// ============================================================
//  platform/bridge/notification/request.rs — notification.ensure_channel / show / cancel の引数の読み取りと検査（W1-5）
//
//  【規則】（Java の :seed_platform の NotificationRequestReader と同じ。変えるときは両方）
//    ensure_channel:
//      channel_id  … 必須。1〜MAX_ID_LENGTH 文字（Unicode の符号位置）の文字列。RESERVED_CHANNEL_PREFIX で始まる ID は不可
//                    （鳴動の通知チャネル seed_platform_alarm などプラットフォーム層のもの）
//      name        … 必須。1〜MAX_TEXT_LENGTH 文字
//      importance  … 任意。"low" / "default" / "high"（無い・null は "default"。それ以外は誤り）
//      description … 任意の文字列（MAX_TEXT_LENGTH 文字まで）
//    show:
//      id・channel_id … 必須（channel_id は上と同じ規則）
//      title / body   … 任意の文字列（MAX_TEXT_LENGTH 文字まで）
//      ongoing        … 任意の真偽（既定 false）
//      category       … 任意の文字列（MAX_ID_LENGTH 文字まで）。知らない値は誤りにせず、Android では付けない（ログだけ）
//      actions        … 任意の配列（null は空）。MAX_ACTIONS 個まで。各要素は { id: 1〜MAX_ID_LENGTH 文字, label: 1〜MAX_TEXT_LENGTH 文字 }。
//                       同じ id の操作が 2 つあると、押されたほうを起動理由で見分けられないので誤り
//      payload_json   … 任意の文字列（MAX_PAYLOAD_LENGTH 文字まで。中身は検査しない）
//    cancel: id … 必須（show の id と同じ規則）
//  欄があるのに型が違う・長すぎる・数が多すぎるときは Err(説明)（返答は invalid_argument。detail に説明）。
// ============================================================

use serde_json::Value;

use crate::engine::platform::bridge::wire::notification as names;

/// 検査済みの ensure_channel の引数。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChannelRequest {
    /// チャネルの ID。
    pub channel_id: String,
    /// 表示名。
    pub name: String,
    /// 重要度（names::IMPORTANCE_* のどれか）。
    pub importance: String,
    /// 説明（空可）。
    pub description: String,
}

/// 通知の操作（ボタン）1 つ。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NotificationActionRequest {
    /// 操作の ID（起動理由の action_id）。
    pub id: String,
    /// 表示の文字。
    pub label: String,
}

/// 検査済みの show の引数。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NotificationRequest {
    /// 通知の ID（同じ ID は置き換え）。
    pub id: String,
    /// 出すチャネルの ID。
    pub channel_id: String,
    /// 題。
    pub title: String,
    /// 本文。
    pub body: String,
    /// 常駐か。
    pub ongoing: bool,
    /// 種類（空なら無し。知らない値もそのまま持つ）。
    pub category: String,
    /// 操作（最大 names::MAX_ACTIONS）。
    pub actions: Vec<NotificationActionRequest>,
    /// 起動理由にそのまま返す任意の JSON。
    pub payload_json: String,
}

/// 重要度の語彙（importance に書ける値）。
const IMPORTANCES: [&str; 3] = [names::IMPORTANCE_LOW, names::IMPORTANCE_DEFAULT, names::IMPORTANCE_HIGH];

/// 種類の語彙（Android の Notification.CATEGORY_* へ写せる値。これ以外は付けない）。
const CATEGORIES: [&str; 5] =
    [names::CATEGORY_ALARM, names::CATEGORY_REMINDER, names::CATEGORY_STATUS, names::CATEGORY_EVENT, names::CATEGORY_PROGRESS];

/// 空を許す文字列の欄の下限（0 文字）。
const ALLOW_EMPTY: usize = 0;

/// 空を許さない文字列の欄の下限（1 文字）。
const NON_EMPTY: usize = 1;

/// notification.ensure_channel の引数を読む。
///
/// # 戻り値
/// 読めたら Ok(チャネル)。約束に合わなければ Err(説明。返答の detail)
pub fn read_channel(request: &Value) -> Result<ChannelRequest, String> {
    let channel_id = channel_id(request)?;
    let name = text(request, names::KEY_CHANNEL_NAME, NON_EMPTY, names::MAX_TEXT_LENGTH)?;
    let importance = match request.get(names::KEY_IMPORTANCE) {
        None | Some(Value::Null) => names::IMPORTANCE_DEFAULT.to_string(),
        Some(Value::String(value)) if IMPORTANCES.contains(&value.as_str()) => value.clone(),
        Some(_) => return Err(format!("{} は {} のどれかにしてください", names::KEY_IMPORTANCE, IMPORTANCES.join(" / "))),
    };
    let description = text(request, names::KEY_DESCRIPTION, ALLOW_EMPTY, names::MAX_TEXT_LENGTH)?;
    Ok(ChannelRequest { channel_id, name, importance, description })
}

/// notification.show の引数を読む。
///
/// # 戻り値
/// 読めたら Ok(通知)。約束に合わなければ Err(説明)
pub fn read_show(request: &Value) -> Result<NotificationRequest, String> {
    let id = read_id(request)?;
    let channel_id = channel_id(request)?;
    let title = text(request, names::KEY_TITLE, ALLOW_EMPTY, names::MAX_TEXT_LENGTH)?;
    let body = text(request, names::KEY_BODY, ALLOW_EMPTY, names::MAX_TEXT_LENGTH)?;
    let ongoing = match request.get(names::KEY_ONGOING) {
        None | Some(Value::Null) => false,
        Some(Value::Bool(flag)) => *flag,
        Some(_) => return Err(format!("{} は真偽にしてください", names::KEY_ONGOING)),
    };
    let category = text(request, names::KEY_CATEGORY, ALLOW_EMPTY, names::MAX_ID_LENGTH)?;
    let actions = actions(request)?;
    let payload_json = text(request, names::KEY_PAYLOAD_JSON, ALLOW_EMPTY, names::MAX_PAYLOAD_LENGTH)?;
    Ok(NotificationRequest { id, channel_id, title, body, ongoing, category, actions, payload_json })
}

/// notification.cancel（と show）の ID を読む（必須。1〜MAX_ID_LENGTH 文字）。
pub fn read_id(request: &Value) -> Result<String, String> {
    text(request, names::KEY_ID, NON_EMPTY, names::MAX_ID_LENGTH)
}

/// 種類が Android の CATEGORY_* へ写せる値か（模擬のログで「付けない」と知らせるため）。
pub fn is_known_category(category: &str) -> bool {
    CATEGORIES.contains(&category)
}

/// チャネルの ID（必須。予約の接頭辞で始まるものは不可）。
fn channel_id(request: &Value) -> Result<String, String> {
    let channel_id = text(request, names::KEY_CHANNEL_ID, NON_EMPTY, names::MAX_ID_LENGTH)?;
    if channel_id.starts_with(names::RESERVED_CHANNEL_PREFIX) {
        return Err(format!(
            "{} が {} で始まるチャネルはプラットフォーム層が使うので使えません",
            names::KEY_CHANNEL_ID,
            names::RESERVED_CHANNEL_PREFIX
        ));
    }
    Ok(channel_id)
}

/// 操作の配列（null・無しは空。MAX_ACTIONS 個まで。ID の重なりは誤り）。
fn actions(request: &Value) -> Result<Vec<NotificationActionRequest>, String> {
    let items = match request.get(names::KEY_ACTIONS) {
        None | Some(Value::Null) => return Ok(Vec::new()),
        Some(Value::Array(items)) => items,
        Some(_) => return Err(format!("{} は配列にしてください", names::KEY_ACTIONS)),
    };
    if items.len() > names::MAX_ACTIONS {
        return Err(format!("{} は {} 個までにしてください（{} 個あります）", names::KEY_ACTIONS, names::MAX_ACTIONS, items.len()));
    }
    let mut actions: Vec<NotificationActionRequest> = Vec::with_capacity(items.len());
    for (index, item) in items.iter().enumerate() {
        if !item.is_object() {
            return Err(format!("{}[{index}] はオブジェクト（{{ id, label }}）にしてください", names::KEY_ACTIONS));
        }
        let id = text(item, names::KEY_ACTION_ID, NON_EMPTY, names::MAX_ID_LENGTH)
            .map_err(|detail| format!("{}[{index}] の {detail}", names::KEY_ACTIONS))?;
        let label = text(item, names::KEY_ACTION_LABEL, NON_EMPTY, names::MAX_TEXT_LENGTH)
            .map_err(|detail| format!("{}[{index}] の {detail}", names::KEY_ACTIONS))?;
        if actions.iter().any(|existing| existing.id == id) {
            return Err(format!("{}[{index}] の {} {id} が重なっています", names::KEY_ACTIONS, names::KEY_ACTION_ID));
        }
        actions.push(NotificationActionRequest { id, label });
    }
    Ok(actions)
}

/// 文字列の欄（無い・null は空。長さは Unicode の符号位置で数え、min_length〜max_length 文字。型が違えば誤り）。
fn text(request: &Value, key: &str, min_length: usize, max_length: usize) -> Result<String, String> {
    let value = match request.get(key) {
        None | Some(Value::Null) => String::new(),
        Some(Value::String(text)) => text.clone(),
        Some(_) => return Err(format!("{key} は文字列にしてください")),
    };
    let length = value.chars().count();
    if length < min_length || length > max_length {
        return Err(if min_length == ALLOW_EMPTY {
            format!("{key} は {max_length} 文字までにしてください")
        } else {
            format!("{key} は {min_length}〜{max_length} 文字にしてください")
        });
    }
    Ok(value)
}

// ============================================================
//  ユニットテスト（Java の NotificationRequestReader と同じ規則であること）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// チャネル: 重要度は既定 default、語彙の 3 つは通る、説明は任意。
    #[test]
    fn channel_defaults_and_importances() {
        let minimal = read_channel(&json!({ "channel_id": "reminders", "name": "リマインダー" })).unwrap();
        assert_eq!(minimal.importance, names::IMPORTANCE_DEFAULT);
        assert_eq!(minimal.description, "");
        for importance in IMPORTANCES {
            let channel = read_channel(&json!({ "channel_id": "c", "name": "n", "importance": importance, "description": "説明" })).unwrap();
            assert_eq!((channel.importance.as_str(), channel.description.as_str()), (importance, "説明"));
        }
    }

    /// チャネルの誤り: ID・名前が無い／空、知らない重要度、予約の接頭辞、長すぎる説明。
    #[test]
    fn invalid_channels_are_rejected() {
        let long_description = "x".repeat(names::MAX_TEXT_LENGTH + 1);
        let cases = [
            (json!({ "name": "n" }), names::KEY_CHANNEL_ID),
            (json!({ "channel_id": "", "name": "n" }), names::KEY_CHANNEL_ID),
            (json!({ "channel_id": "c" }), names::KEY_CHANNEL_NAME),
            (json!({ "channel_id": "c", "name": "n", "importance": "urgent" }), names::KEY_IMPORTANCE),
            (json!({ "channel_id": "c", "name": "n", "importance": 3 }), names::KEY_IMPORTANCE),
            (json!({ "channel_id": "seed_platform_alarm", "name": "n" }), names::KEY_CHANNEL_ID),
            (json!({ "channel_id": "c", "name": "n", "description": long_description }), names::KEY_DESCRIPTION),
        ];
        for (request, key) in cases {
            let error = read_channel(&request).unwrap_err();
            assert!(error.contains(key), "{request}: {error}");
        }
    }

    /// 通知: 最小の形は既定値（常駐なし・操作なし）。全部の欄が読める。
    #[test]
    fn show_reads_all_fields() {
        let minimal = read_show(&json!({ "id": "snooze", "channel_id": "reminders" })).unwrap();
        assert!(!minimal.ongoing && minimal.actions.is_empty());
        assert_eq!((minimal.title.as_str(), minimal.body.as_str(), minimal.category.as_str()), ("", "", ""));
        let full = read_show(&json!({
            "id": "snooze", "channel_id": "reminders", "title": "スヌーズ中", "body": "7:10 にもう一度鳴ります",
            "ongoing": true, "category": "reminder", "payload_json": "{\"a\":1}",
            "actions": [ { "id": "stop", "label": "止める" }, { "id": "open", "label": "開く" } ],
        }))
        .unwrap();
        assert!(full.ongoing);
        assert_eq!(full.category, names::CATEGORY_REMINDER);
        assert_eq!(full.actions.len(), 2);
        assert_eq!((full.actions[1].id.as_str(), full.actions[1].label.as_str()), ("open", "開く"));
        assert_eq!(full.payload_json, "{\"a\":1}");
    }

    /// 知らない種類は誤りにしない（Android では付けないだけ）。語彙の判定。
    #[test]
    fn unknown_category_is_accepted_but_not_known() {
        let request = read_show(&json!({ "id": "a", "channel_id": "c", "category": "social" })).unwrap();
        assert_eq!(request.category, "social");
        assert!(!is_known_category("social"));
        assert!(CATEGORIES.iter().all(|category| is_known_category(category)));
    }

    /// 操作の誤り: 4 個以上・配列でない・要素がオブジェクトでない・ID／文字が無い・ID の重なり。3 個ちょうどは通る。
    #[test]
    fn invalid_actions_are_rejected() {
        let action = |id: &str| json!({ "id": id, "label": "L" });
        let three = json!({ "id": "a", "channel_id": "c", "actions": [action("1"), action("2"), action("3")] });
        assert_eq!(read_show(&three).unwrap().actions.len(), names::MAX_ACTIONS);
        let bad_actions = [
            json!([action("1"), action("2"), action("3"), action("4")]),
            json!({ "id": "1" }),
            json!(["stop"]),
            json!([{ "label": "L" }]),
            json!([{ "id": "1" }]),
            json!([{ "id": "1", "label": "" }]),
            json!([action("dup"), action("dup")]),
        ];
        for actions in bad_actions {
            let error = read_show(&json!({ "id": "a", "channel_id": "c", "actions": actions })).unwrap_err();
            assert!(error.contains(names::KEY_ACTIONS), "{actions}: {error}");
        }
        assert!(read_show(&json!({ "id": "a", "channel_id": "c", "actions": null })).unwrap().actions.is_empty());
    }

    /// ID と型の誤り: ID が無い・空・長すぎ（符号位置で数える）・文字列でない、常駐が真偽でない、payload が長すぎる。
    #[test]
    fn invalid_show_fields_are_rejected() {
        assert_eq!(read_id(&json!({ "id": "あ".repeat(names::MAX_ID_LENGTH) })).unwrap().chars().count(), names::MAX_ID_LENGTH);
        for bad in [json!({}), json!({ "id": "" }), json!({ "id": 7 }), json!({ "id": "あ".repeat(names::MAX_ID_LENGTH + 1) })] {
            assert!(read_id(&bad).is_err(), "{bad}");
        }
        let cases = [
            (names::KEY_ONGOING, json!("yes")),
            (names::KEY_TITLE, json!(1)),
            (names::KEY_BODY, json!("x".repeat(names::MAX_TEXT_LENGTH + 1))),
            (names::KEY_PAYLOAD_JSON, json!("x".repeat(names::MAX_PAYLOAD_LENGTH + 1))),
            (names::KEY_CHANNEL_ID, json!("seed_platform_x")),
        ];
        for (key, value) in cases {
            let mut request = json!({ "id": "a", "channel_id": "c" });
            request[key] = value;
            let error = read_show(&request).unwrap_err();
            assert!(error.contains(key), "{key}: {error}");
        }
    }
}
