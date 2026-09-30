// ============================================================
//  platform/bridge/permission/mod.rs — 権限（SEED.Platform の Permissions。W1-5）のエンジン側の共通部品
//
//  【置くもの】種類（PermissionKind）と状態（PermissionStatus。2026-10-01 の模擬の権限の操作で足した）の語彙と、
//  命令の引数 { kind } の読み取り。
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

    /// ALL の中の位置（種類ごとの表〈配列〉の添字。ALL の並びを変えると表の並びも変わるので、表は必ずこれで引く）。
    pub fn index(self) -> usize {
        // ALL はすべての種類を 1 つずつ持つので必ず見つかる（見つからないのは ALL の書き漏れ＝単体テストで気づく）
        Self::ALL.iter().position(|kind| *kind == self).unwrap_or_default()
    }
}

/// 権限の状態（wire の STATUS_* と 1 対 1。Android の PermissionStatusProbe が返す語彙と同じ）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PermissionStatus {
    /// 許可されている。
    Granted,
    /// 許可されていない（もう一度求めれば確認の画面が出る見込み）。
    Denied,
    /// 許可されず、求めても確認の画面が出ない（設定の画面へ案内する）。
    DeniedPermanently,
    /// 設定の画面で利用者が切り替える種類で、今は切られている。
    NeedsSettings,
    /// この OS の版・この段階では要らない（扱わない）。
    NotApplicable,
}

impl PermissionStatus {
    /// すべての状態（wire の順）。
    pub const ALL: [PermissionStatus; 5] =
        [Self::Granted, Self::Denied, Self::DeniedPermanently, Self::NeedsSettings, Self::NotApplicable];

    /// wire の名前から引く（知らない名前は None）。
    pub fn from_wire(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|status| status.wire_name() == name)
    }

    /// wire の名前。
    pub fn wire_name(self) -> &'static str {
        match self {
            Self::Granted => names::STATUS_GRANTED,
            Self::Denied => names::STATUS_DENIED,
            Self::DeniedPermanently => names::STATUS_DENIED_PERMANENTLY,
            Self::NeedsSettings => names::STATUS_NEEDS_SETTINGS,
            Self::NotApplicable => names::STATUS_NOT_APPLICABLE,
        }
    }

    /// 知らない名前のときの説明（返答の detail・ログ用。知っている名前を並べる）。
    pub fn describe_unknown(field: &str, name: &str) -> String {
        let known: Vec<&str> = Self::ALL.iter().map(|status| status.wire_name()).collect();
        format!("{field} は {} のどれかにしてください（{name}）", known.join(" / "))
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

    /// 状態: wire の名前との行き来が 1 対 1。知らない名前は None で、説明に欄の名前と知っている名前が入る。
    #[test]
    fn statuses_round_trip_through_wire_names() {
        for status in PermissionStatus::ALL {
            assert_eq!(PermissionStatus::from_wire(status.wire_name()), Some(status));
        }
        assert_eq!(PermissionStatus::from_wire("Granted"), None);
        let detail = PermissionStatus::describe_unknown("status", "maybe");
        assert!(detail.contains("status") && detail.contains(names::STATUS_NEEDS_SETTINGS) && detail.contains("maybe"), "{detail}");
    }

    /// 種類の添字: ALL の位置と一致し、重ならない（種類ごとの表を引く）。
    #[test]
    fn kind_index_matches_all_order() {
        for (position, kind) in PermissionKind::ALL.into_iter().enumerate() {
            assert_eq!(kind.index(), position);
        }
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
