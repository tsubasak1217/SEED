namespace SEED.Platform;

/// <summary>権限の種類（<see cref="Permissions"/>。W1-5）。</summary>
public enum PermissionKind
{
    /// <summary>知らない種類（新しい版の APK のイベントなど。<see cref="PermissionResultEvent.KindName"/> に元の文字列）。命令には渡せない。</summary>
    Unknown,

    /// <summary>"post_notifications": 通知（Android 13+ は実行時の確認の画面。12 以前は通知の設定）。機能 notifications（か alarm）が要る。</summary>
    PostNotifications,

    /// <summary>"exact_alarm": 正確なアラーム（Android 12 系は特別なアクセスの設定の画面。13+ は USE_EXACT_ALARM で常に許可。11 以前は要らない）。機能 alarm が要る。</summary>
    ExactAlarm,

    /// <summary>"full_screen_intent": フルスクリーン通知（Android 14+ は特別なアクセスの設定の画面。13 以前は許可）。機能 alarm が要る。</summary>
    FullScreenIntent,

    /// <summary>"record_audio": 録音（v2 の予約。今は常に <see cref="PermissionStatus.NotApplicable"/>）。</summary>
    RecordAudio,

    /// <summary>"send_sms": SMS の送信（v2 の予約。今は常に <see cref="PermissionStatus.NotApplicable"/>）。</summary>
    SendSms,
}
