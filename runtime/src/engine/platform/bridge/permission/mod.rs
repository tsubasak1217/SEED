// ============================================================
//  platform/bridge/permission/mod.rs — 権限（SEED.Platform の Permissions。W1-5）のエンジン側の共通部品
//
//  【置くもの】種類（PermissionKind）の語彙と、命令の引数 { kind } の読み取り。
//  Android では権限の命令はメインプロセスの Java（platform/local/Permission*Command・platform/permission/）が答え、
//  デスクトップでは模擬（desktop_sim/permission_commands.rs）がここを使って答える。
//  名前・欄・状態は wire::permission（Java の PlatformContract・C# の PermissionJson と一致させる）。全体像は docs/android.md §25.14。
// ============================================================

use serde_json::Value;

use crate::engine::platform::bridge::wire::permission as names;

/// 権限の種類（wire の KIND_* と 1 対 1）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PermissionKind {
    /// 通知（Android 13+ の POST_NOTIFICATIONS）。
    PostNotifications,
    /// 正確なアラーム（Android 12 系の特別なアクセス）。
    ExactAlarm,
    /// フルスクリーン通知（Android 14+ の特別なアクセス）。
    FullScreenIntent,
    /// 録音（v2 の予約。今は扱わない）。
    RecordAudio,
    /// SMS の送信（v2 の予約。今は扱わない）。
    SendSms,
}

impl PermissionKind {
    /// すべての種類（wire の順）。
    pub const ALL: [PermissionKind; 5] =
        [Self::PostNotifications, Self::ExactAlarm, Self::FullScreenIntent, Self::RecordAudio, Self::SendSms];

    /// wire の名前から引く（知らない名前は None）。
    pub fn from_wire(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|kind| kind.wire_name() == name)
    }

    /// wire の名前。
    pub fn wire_name(self) -> &'static str {
        match self {
            Self::PostNotifications => names::KIND_POST_NOTIFICATIONS,
            Self::ExactAlarm => names::KIND_EXACT_ALARM,
            Self::FullScreenIntent => names::KIND_FULL_SCREEN_INTENT,
            Self::RecordAudio => names::KIND_RECORD_AUDIO,
            Self::SendSms => names::KIND_SEND_SMS,
        }
    }

    /// この段階（v1）で扱う種類か（false の種類は状態が常に not_applicable。v2 で中身を足す）。
    pub fn is_implemented(self) -> bool {
        !matches!(self, Self::RecordAudio | Self::SendSms)
    }
}

/// 命令の引数 `{ kind }` を読む。
///
/// # 戻り値
/// 読めたら Ok(種類)。文字列でない・知らない名前なら Err(説明。返答は invalid_argument)
pub fn read_kind(request: &Value) -> Result<PermissionKind, String> {
    let Some(name) = request.get(names::KEY_KIND).and_then(Value::as_str) else {
        return Err(format!("{} は文字列にしてください", names::KEY_KIND));
    };
    PermissionKind::from_wire(name).ok_or_else(|| {
        let known: Vec<&str> = PermissionKind::ALL.iter().map(|kind| kind.wire_name()).collect();
        format!("{} は {} のどれかにしてください（{name}）", names::KEY_KIND, known.join(" / "))
    })
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// wire の名前との行き来が 1 対 1 で、v2 の 2 つだけが扱わない種類。
    #[test]
    fn kinds_round_trip_through_wire_names() {
        for kind in PermissionKind::ALL {
            assert_eq!(PermissionKind::from_wire(kind.wire_name()), Some(kind));
        }
        let implemented: Vec<_> = PermissionKind::ALL.into_iter().filter(|kind| kind.is_implemented()).collect();
        assert_eq!(implemented, vec![PermissionKind::PostNotifications, PermissionKind::ExactAlarm, PermissionKind::FullScreenIntent]);
        assert_eq!(PermissionKind::from_wire("camera"), None);
    }

    /// 引数: 種類が無い・文字列でない・知らない名前は誤り（欄の名前が説明に入る）。
    #[test]
    fn read_kind_rules() {
        assert_eq!(read_kind(&json!({ "kind": "exact_alarm" })).unwrap(), PermissionKind::ExactAlarm);
        for bad in [json!({}), json!({ "kind": 1 }), json!({ "kind": "camera" }), json!({ "kind": "PostNotifications" })] {
            assert!(read_kind(&bad).unwrap_err().contains(names::KEY_KIND), "{bad}");
        }
    }
}
