namespace SEED.Platform;

/// <summary>
/// 通知（チャネル・常駐・ボタン。W1-5）。
///
/// <para><b>仕組み</b><br/>
/// Android では別プロセス :seed_platform（Java）が NotificationManager で出す（エンジンが落ちても・アプリを閉じても通知は残る）。
/// 本文のタップとボタン（最大 3 つ）は、どちらもアプリを直接開く（Android 12+ の通知のトランポリンの禁止に合わせ、受信機を挟まない）。
/// 開いたときの起動理由は <see cref="LaunchKind.NotificationTap"/>（<see cref="LaunchInfo.Id"/> = 通知の ID）か
/// <see cref="LaunchKind.NotificationAction"/>（<see cref="LaunchInfo.ActionId"/> = ボタンの ID）で、どちらも <see cref="LaunchInfo.PayloadJson"/>
/// に <see cref="NotificationRequest.PayloadJson"/> が戻る（起動のときは <see cref="App.LaunchReason"/>、動いている間はイベント "platform.launch"）。
/// デスクトップ（エディタの Play・単体起動）はエンジンの中の模擬で、画面には何も出さず <c>[SEED PLATFORM] 通知: …</c> のログだけ
/// （チャネルと出ている通知は Play を止めると消える）。
/// </para>
///
/// <para><b>呼び方</b><br/>
/// 先に <see cref="EnsureChannel"/> でチャネルを作り（起動のたびに呼んでよい）、<see cref="Show"/> で出す。呼び出しは同期で「受け付けたか」だけを
/// 返す（false なら <see cref="Platform.LastError"/>）。Android の最初の呼び出しは :seed_platform の起動を待たずに
/// <see cref="Platform.ErrorConnecting"/> で失敗するので、<see cref="PlatformEvents.Connected"/> の後に呼び直す。
/// Android 13 以降は通知の実行時の許可が要る（無いと <see cref="ErrorNotificationsDisabled"/>。<see cref="Permissions.Request"/> で求める）。
/// APK に機能 notifications（か alarm）が入っていない（project_settings.json の android.features）と <see cref="ErrorFeatureNotEnabled"/>。
/// </para>
/// </summary>
public static class Notifications
{
    /// <summary><see cref="Platform.LastError"/>: APK に機能 notifications が無い（android.features に "notifications" を足す）。</summary>
    public const string ErrorFeatureNotEnabled = "feature_not_enabled";

    /// <summary><see cref="Platform.LastError"/>: 引数の値が約束に合わない（ID が空・ボタンが 4 つ以上・予約済みのチャネル ID 等）。</summary>
    public const string ErrorInvalidArgument = "invalid_argument";

    /// <summary><see cref="Platform.LastError"/>: 通知が端末で無効（Android 13+ の許可が無い・利用者が切った・チャネルが止められた）。</summary>
    public const string ErrorNotificationsDisabled = "notifications_disabled";

    /// <summary><see cref="Platform.LastError"/>: チャネルが無い（先に <see cref="EnsureChannel"/>）。</summary>
    public const string ErrorChannelNotFound = "channel_not_found";

    /// <summary>APK に機能が無いと分かったか（最初に feature_not_enabled が返った後は true）。</summary>
    private static bool _featureMissing;

    /// <summary>
    /// 通知が使えるか（<see cref="Platform.IsSupported"/> で、機能が無いと分かっていない）。デスクトップの模擬は true。IPC を通らない。
    /// </summary>
    public static bool IsSupported => Platform.IsSupported && !_featureMissing;

    /// <summary>
    /// アプリの通知が端末で有効か（Android 13+ は通知の許可も含む。模擬は true）。呼ぶたびに :seed_platform へ問い合わせる（毎フレーム読まない）。
    /// 失敗したら false（<see cref="Platform.LastError"/>）。
    /// </summary>
    public static bool AreEnabled =>
        Invoke(NotificationJson.MethodAreEnabled, PlatformJson.StringObject(), out string reply)
        && AlarmJson.ReadReplyBool(reply, NotificationJson.KeyEnabled);

    /// <summary>
    /// 通知チャネルを作る（同じ ID が既にあれば名前と説明だけが変わる。重要度は利用者だけが変えられる）。起動のたびに呼んでよい。
    /// 案（docs/app_platform_roadmap.md §2.3）では void だったが、失敗（接続中・機能なし）を見分けられるよう bool を返す。
    /// </summary>
    /// <param name="channelId">チャネルの ID（1〜128 文字。"seed_platform" で始まる ID は不可）。</param>
    /// <param name="name">端末の設定の「通知」に出る名前（1〜4096 文字）。</param>
    /// <param name="importance">重要度。</param>
    /// <param name="description">説明（空可）。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool EnsureChannel(string channelId, string name, NotificationImportance importance, string description = "") =>
        Invoke(NotificationJson.MethodEnsureChannel,
            NotificationJson.WriteChannel(channelId ?? string.Empty, name ?? string.Empty, importance, description ?? string.Empty), out _);

    /// <summary>
    /// 通知を出す（同じ <see cref="NotificationRequest.Id"/> の通知は置き換わる）。
    /// </summary>
    /// <param name="request">通知。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>。許可が無ければ <see cref="ErrorNotificationsDisabled"/>）。</returns>
    public static bool Show(NotificationRequest request)
    {
        if (request == null)
        {
            Platform.LastError = ErrorInvalidArgument;
            return false;
        }
        return Invoke(NotificationJson.MethodShow, NotificationJson.WriteRequest(request), out _);
    }

    /// <summary>
    /// 通知を消す（出ていない ID でも true＝冪等）。案では void だったが、失敗を見分けられるよう bool を返す。
    /// </summary>
    /// <param name="id">通知の ID。</param>
    /// <returns>受け付けたら true。</returns>
    public static bool Cancel(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            Platform.LastError = ErrorInvalidArgument;
            return false;
        }
        return Invoke(NotificationJson.MethodCancel, NotificationJson.IdObject(id), out _);
    }

    /// <summary>命令を送り、機能の有無を覚える（feature_not_enabled なら以後 <see cref="IsSupported"/> は false）。</summary>
    private static bool Invoke(string method, string json, out string reply)
    {
        bool ok = Platform.TryInvoke(NotificationJson.Module, method, json, out reply);
        if (ok)
        {
            _featureMissing = false;
        }
        else if (Platform.LastError == ErrorFeatureNotEnabled)
        {
            _featureMissing = true;
        }
        return ok;
    }
}
