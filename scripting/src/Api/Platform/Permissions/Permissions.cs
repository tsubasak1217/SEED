namespace SEED.Platform;

/// <summary>
/// 権限（状態・求める・設定の画面。W1-5）。
///
/// <para><b>仕組み</b><br/>
/// Android ではメインプロセスが Activity を使って答える（IPC なし。:seed_platform を起こさないので <see cref="Platform.ErrorConnecting"/> にならない）。
/// <see cref="Request"/> はすぐ要求の ID を返し、結果は後からイベント <see cref="PermissionResultEvent"/>（"platform.permission_result"）で届く:
/// 通知（Android 13+）は実行時の確認の画面、正確なアラーム・フルスクリーン通知（と設定で切られた通知）は設定の画面を開き、利用者が戻ったときの
/// 状態が結果になる。既に許可されている・要らない種類は画面を出さずに結果だけが届く。前面へ戻ったとき（設定の画面から戻った等）に状態が
/// 前回と違えば <see cref="PermissionChangedEvent"/>（"platform.permission_changed"）が届く。
/// デスクトップの模擬は v1 の 3 種が常に <see cref="PermissionStatus.Granted"/>（v2 の予約の種類は <see cref="PermissionStatus.NotApplicable"/>）で、
/// <see cref="Request"/> は画面を出さずに次のフレームで結果を返す。
/// </para>
///
/// <para><b>状態の決め方（Android）</b><br/>
/// 通知: Android 13+ は許可されていれば <see cref="PermissionStatus.Granted"/>（通知が設定で切られていれば <see cref="PermissionStatus.NeedsSettings"/>）、
/// 未許可は <see cref="PermissionStatus.Denied"/>、二度拒否されて確認の画面が出なくなったら <see cref="PermissionStatus.DeniedPermanently"/>。
/// 12 以前は通知の設定だけで Granted か NeedsSettings。正確なアラーム: 12 系は特別なアクセスで Granted か NeedsSettings、13+ は Granted、
/// 11 以前は NotApplicable。フルスクリーン通知: 14+ は特別なアクセスで Granted か NeedsSettings、13 以前は Granted。
/// APK にその種類の機能（android.features の notifications / alarm）が無いと <see cref="ErrorFeatureNotEnabled"/>。
/// </para>
/// </summary>
public static class Permissions
{
    /// <summary><see cref="Platform.LastError"/>: APK にその種類の機能が無い（通知は "notifications"、正確なアラーム・フルスクリーン通知は "alarm" を android.features に足す）。</summary>
    public const string ErrorFeatureNotEnabled = "feature_not_enabled";

    /// <summary><see cref="Platform.LastError"/>: 種類が約束に無い（<see cref="PermissionKind.Unknown"/> を渡した等）。</summary>
    public const string ErrorInvalidArgument = "invalid_argument";

    /// <summary><see cref="Platform.LastError"/>: 画面（Activity）が無い（Android。<see cref="Request"/>・<see cref="OpenSettings"/>）。</summary>
    public const string ErrorNoActivity = "no_activity";

    /// <summary>
    /// 今の状態（呼ぶたびに問い合わせる。Android でも IPC なしで軽い）。失敗したら <see cref="PermissionStatus.Unknown"/>（<see cref="Platform.LastError"/>）。
    /// </summary>
    /// <param name="kind">種類。</param>
    /// <returns>状態。</returns>
    public static PermissionStatus Check(PermissionKind kind)
    {
        if (!Invoke(PermissionJson.MethodCheck, kind, out string reply))
        {
            return PermissionStatus.Unknown;
        }
        PermissionStatus status = PermissionJson.ParseStatus(PermissionJson.ReadReplyString(reply, PermissionJson.KeyStatus));
        if (status == PermissionStatus.Unknown)
        {
            Platform.LastError = Platform.ErrorInvalidReply;
        }
        return status;
    }

    /// <summary>
    /// 求める（確認の画面か設定の画面。既に許可されていれば画面は出ない）。結果は <see cref="PermissionResultEvent"/> で届く
    /// （<see cref="PermissionResultEvent.RequestId"/> がこの戻り値と同じ）。
    /// </summary>
    /// <param name="kind">種類。</param>
    /// <returns>要求の ID（1 以上）。受け付けなければ 0（<see cref="Platform.LastError"/>）。</returns>
    public static int Request(PermissionKind kind)
    {
        if (!Invoke(PermissionJson.MethodRequest, kind, out string reply))
        {
            return PermissionJson.NoRequestId;
        }
        long requestId = PermissionJson.ReadReplyLong(reply, PermissionJson.KeyRequestId);
        if (requestId <= PermissionJson.NoRequestId || requestId > int.MaxValue)
        {
            Platform.LastError = Platform.ErrorInvalidReply;
            return PermissionJson.NoRequestId;
        }
        return (int)requestId;
    }

    /// <summary>
    /// 種類の設定の画面を開く（通知はアプリの通知の設定、正確なアラーム・フルスクリーン通知は特別なアクセスの画面、無ければアプリ情報）。
    /// 結果のイベントは無い（戻ったときに状態が変わっていれば <see cref="PermissionChangedEvent"/>）。
    /// 案（docs/app_platform_roadmap.md §2.3）では void だったが、失敗を見分けられるよう bool を返す。
    /// </summary>
    /// <param name="kind">種類。</param>
    /// <returns>受け付けたら true（開くのは少し後。false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool OpenSettings(PermissionKind kind) => Invoke(PermissionJson.MethodOpenSettings, kind, out _);

    /// <summary>命令を送る（<see cref="PermissionKind.Unknown"/> は送らずに invalid_argument）。</summary>
    private static bool Invoke(string method, PermissionKind kind, out string reply)
    {
        reply = string.Empty;
        if (kind == PermissionKind.Unknown || PermissionJson.KindName(kind).Length == 0)
        {
            Platform.LastError = ErrorInvalidArgument;
            return false;
        }
        return Platform.TryInvoke(PermissionJson.Module, method, PermissionJson.KindObject(kind), out reply);
    }
}
