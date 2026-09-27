namespace SEED.Platform;

/// <summary>
/// アプリとしての振る舞い（起動理由〈W1-4a〉・背面へ・URL・アプリ情報〈W1-6〉）。
///
/// <para><b>起動理由</b><br/>
/// <see cref="LaunchReason"/> は「この起動の理由」（最後に Activity へ届いた Intent の理由）。目覚ましの鳴動で起きたなら
/// <see cref="LaunchKind.Alarm"/>（予約の ID・予定時刻・payload つき）で、そのときアプリはロック画面の上に出て画面が点いている。
/// 鳴動画面を出して、解除・スヌーズで <see cref="Alarms.StopRinging"/>、片付けたら <see cref="Window.SetShowWhenLocked"/>(false) で
/// ロック画面の上から降りる。URL（ディープリンク）で開かれたなら <see cref="LaunchKind.DeepLink"/> と <see cref="LaunchInfo.Uri"/>（W1-6）。
/// 起動した後（アプリが動いている間）に届いた起動（目覚まし・通知の操作・ディープリンク）は、イベント
/// <see cref="LaunchInfo.EventName"/>（"platform.launch"）で届く（<see cref="LaunchInfo.TryParseEvent"/> で読む）。
/// Android では IPC を通らない（メインプロセスが答える）。デスクトップの模擬は <see cref="LaunchKind.Launcher"/>
/// （単体起動の SEED.exe に --deep-link=&lt;URI&gt; を付けたときだけ <see cref="LaunchKind.DeepLink"/>）。
/// </para>
///
/// <para><b>背面へ・URL・アプリ情報（W1-6）</b><br/>
/// 戻る（Android の戻るキーはスクリプトには <c>KeyCode.Escape</c> で届く）の最上位では <see cref="MoveTaskToBack"/> で閉じずに背面へ回す
/// （閉じるとプロセスごと終わり、次の起動が冷える）。<see cref="OpenUrl"/> はブラウザ・メール・電話・その URL を受けるアプリで開き、
/// <see cref="OpenAppSettings"/> は端末の「アプリ情報」を開く。どれも同期で「受け付けたか」だけを返す（false なら
/// <see cref="Platform.LastError"/>）。
/// </para>
/// </summary>
public static class App
{
    /// <summary><see cref="Platform.LastError"/>: その URL を開けるアプリが端末に無い（<see cref="OpenUrl"/>。Android）。</summary>
    public const string ErrorNoHandler = "no_handler";

    /// <summary><see cref="Platform.LastError"/>: 開かない scheme（file: / content: / javascript:。<see cref="OpenUrl"/>）。</summary>
    public const string ErrorSchemeNotAllowed = "scheme_not_allowed";

    /// <summary><see cref="Platform.LastError"/>: URL の形が約束に合わない（空・scheme が無い・制御文字・8192 文字を超える。<see cref="OpenUrl"/>）。</summary>
    public const string ErrorInvalidArgument = "invalid_argument";

    /// <summary><see cref="Platform.LastError"/>: 操作する画面（Activity）が無い（Android）。</summary>
    public const string ErrorNoActivity = Window.ErrorNoActivity;

    /// <summary>URL の最大の長さ（Unicode の符号位置の数。<see cref="OpenUrl"/> と <see cref="LaunchInfo.Uri"/>）。</summary>
    public const int MaxUrlLength = 8192;

    /// <summary>
    /// この起動の理由（呼ぶたびに問い合わせる。Android でも IPC なしで答えるので軽い）。
    /// 取れなければ <see cref="LaunchKind.Launcher"/>（<see cref="Platform.LastError"/> に理由）。
    /// </summary>
    public static LaunchInfo LaunchReason
    {
        get
        {
            if (!Platform.TryInvoke(LaunchJson.ModulePlatform, LaunchJson.MethodLaunchReason, PlatformJson.StringObject(), out string reply))
            {
                return LaunchInfo.Launcher;
            }
            if (!AlarmJson.TryReadReplyObject(reply, LaunchJson.KeyLaunch, LaunchInfo.FromJson, out LaunchInfo launch))
            {
                Platform.LastError = Platform.ErrorInvalidReply;
                return LaunchInfo.Launcher;
            }
            return launch;
        }
    }

    /// <summary>
    /// 閉じずに背面へ回す（Android の moveTaskToBack。W1-6）。戻るの最上位で使う（閉じるとプロセスごと終わり、次の起動が冷える）。
    /// 背面へ回ったアプリはランチャー・最近のタスクから開き直すと前面に戻る（起動理由は <see cref="LaunchKind.Launcher"/> の platform.launch）。
    /// すぐ返る（回すのは UI スレッドで少し後）。デスクトップの模擬は何もしない（ログだけ）。
    /// </summary>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool MoveTaskToBack() =>
        Platform.TryInvoke(AppJson.Module, AppJson.MethodMoveTaskToBack, PlatformJson.StringObject(), out _);

    /// <summary>
    /// URL を端末のアプリで開く（Android の ACTION_VIEW。W1-6）。http / https（ブラウザ）・mailto（メール）・tel（電話）・
    /// アプリ独自の scheme（そのアプリ）を開ける。file: / content: / javascript: は開かない（<see cref="ErrorSchemeNotAllowed"/>）。
    /// 開けるアプリが無ければ <see cref="ErrorNoHandler"/>。デスクトップの模擬は同じ規則で判定し、http / https / mailto だけを
    /// PC の既定のアプリで開く（ほかの scheme は判定だけで true。環境変数 SEED_PLATFORM_SIM_NO_OPEN=1 なら何も開かない）。
    /// </summary>
    /// <param name="url">開く URL（例 "https://example.com"）。</param>
    /// <returns>開いたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool OpenUrl(string url) =>
        Platform.TryInvoke(AppJson.Module, AppJson.MethodOpenUrl, PlatformJson.StringObject((AppJson.KeyUrl, url)), out _);

    /// <summary>
    /// 端末の「アプリ情報」の画面を開く（Android の Settings.ACTION_APPLICATION_DETAILS_SETTINGS。W1-6）。権限の種類ごとの画面は
    /// <see cref="Permissions.OpenSettings"/>。すぐ返る（開くのは UI スレッドで少し後）。デスクトップの模擬は何もしない（ログだけ）。
    /// </summary>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool OpenAppSettings() =>
        Platform.TryInvoke(AppJson.Module, AppJson.MethodOpenAppSettings, PlatformJson.StringObject(), out _);
}
