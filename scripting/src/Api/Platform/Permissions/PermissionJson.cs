using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 権限（<see cref="Permissions"/>）の JSON の名前と読み書き（内部用。W1-5）。
///
/// 名前の正典は Rust 側 runtime/src/engine/platform/bridge/wire.rs の <c>wire::permission</c>（Java 側は PlatformContract.java の
/// <c>*PERMISSION*</c>）。値を変えるときは 3 か所を必ず揃える。
/// </summary>
internal static class PermissionJson
{
    /// <summary>権限のモジュール。</summary>
    internal const string Module = "permission";

    /// <summary>今の状態。</summary>
    internal const string MethodCheck = "check";

    /// <summary>求める。</summary>
    internal const string MethodRequest = "request";

    /// <summary>設定の画面を開く。</summary>
    internal const string MethodOpenSettings = "open_settings";

    // ── 欄の名前 ──
    internal const string KeyKind = "kind";
    internal const string KeyStatus = "status";
    internal const string KeyRequestId = "request_id";

    // ── 種類の文字列（PermissionKind と対応）──
    internal const string KindPostNotifications = "post_notifications";
    internal const string KindExactAlarm = "exact_alarm";
    internal const string KindFullScreenIntent = "full_screen_intent";
    internal const string KindRecordAudio = "record_audio";
    internal const string KindSendSms = "send_sms";

    // ── 状態の文字列（PermissionStatus と対応）──
    internal const string StatusGranted = "granted";
    internal const string StatusDenied = "denied";
    internal const string StatusDeniedPermanently = "denied_permanently";
    internal const string StatusNeedsSettings = "needs_settings";
    internal const string StatusNotApplicable = "not_applicable";

    /// <summary>「要求できなかった」の要求の ID（払い出す ID は 1 から）。</summary>
    internal const int NoRequestId = 0;

    /// <summary>種類を wire の名前にする（<see cref="PermissionKind.Unknown"/> は空文字）。</summary>
    internal static string KindName(PermissionKind kind) => kind switch
    {
        PermissionKind.PostNotifications => KindPostNotifications,
        PermissionKind.ExactAlarm => KindExactAlarm,
        PermissionKind.FullScreenIntent => KindFullScreenIntent,
        PermissionKind.RecordAudio => KindRecordAudio,
        PermissionKind.SendSms => KindSendSms,
        _ => string.Empty,
    };

    /// <summary>wire の名前を種類にする（知らない名前は <see cref="PermissionKind.Unknown"/>）。</summary>
    internal static PermissionKind ParseKind(string name) => name switch
    {
        KindPostNotifications => PermissionKind.PostNotifications,
        KindExactAlarm => PermissionKind.ExactAlarm,
        KindFullScreenIntent => PermissionKind.FullScreenIntent,
        KindRecordAudio => PermissionKind.RecordAudio,
        KindSendSms => PermissionKind.SendSms,
        _ => PermissionKind.Unknown,
    };

    /// <summary>wire の名前を状態にする（知らない名前は <see cref="PermissionStatus.Unknown"/>）。</summary>
    internal static PermissionStatus ParseStatus(string name) => name switch
    {
        StatusGranted => PermissionStatus.Granted,
        StatusDenied => PermissionStatus.Denied,
        StatusDeniedPermanently => PermissionStatus.DeniedPermanently,
        StatusNeedsSettings => PermissionStatus.NeedsSettings,
        StatusNotApplicable => PermissionStatus.NotApplicable,
        _ => PermissionStatus.Unknown,
    };

    /// <summary>種類だけのオブジェクト（命令の引数）を作る。</summary>
    internal static string KindObject(PermissionKind kind) => PlatformJson.StringObject((KeyKind, KindName(kind)));

    /// <summary>返答の JSON の文字列の欄（読めなければ空文字）。</summary>
    internal static string ReadReplyString(string reply, string key)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(reply);
            return AlarmJson.GetString(document.RootElement, key);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>返答の JSON の整数の欄（読めなければ 0）。</summary>
    internal static long ReadReplyLong(string reply, string key)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(reply);
            return AlarmJson.GetLong(document.RootElement, key);
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
