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
///
/// <para><b>予測型の戻る（W2 の手直し P1-3・opt-in）</b><br/>
/// プロジェクト設定 android.predictive_back を true にした APK の Android 13 以上では、戻るの手ぶりの進み具合がイベント
/// <see cref="BackStartedEvent"/> / <see cref="BackProgressedEvent"/> / <see cref="BackCancelledEvent"/> / <see cref="BackInvokedEvent"/>
/// （<see cref="BackGestureEvent.TryParse"/> で読む）で届き、確定すると従来どおり <c>KeyCode.Escape</c> も届く。
/// 受ける層が無い（根）ときは <see cref="SetBackCallbackEnabled"/>(false) でシステムに任せる（背面へ回る見た目が出る）。
/// ふつうは SEED.UI の戻るの段（BackDispatcher）が呼ぶので、スクリプトから直接は呼ばない。
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

    /// <summary>
    /// 端末の明暗の設定が変わったイベントの名前（W2-9。data.night = "yes" / "no" / "unknown"。<see cref="TryParseUiModeEvent"/> で読む）。
    /// Android は MainActivity.onConfigurationChanged で夜の bit が変わったとき、PC は OS のアプリのモードが変わったとき
    /// （単体起動のウィンドウの ThemeChanged。エディタに埋め込んだ Play では届かない）と模擬の <see cref="PlatformDiagnostics.SimulateUiMode"/>。
    /// </summary>
    public const string UiModeChangedEvent = "platform.ui_mode_changed";

    /// <summary>
    /// 端末の明暗の設定（W2-9。呼ぶたびに問い合わせる。Android でも IPC なしで答えるので軽い）。
    /// SEED.UI の <c>UiTheme.SetBrightnessMode(UiBrightnessMode.System)</c> がこれを使う。取れなければ <see cref="SystemUiMode.Unknown"/>。
    /// デスクトップの模擬は Windows の「既定のアプリ モード」（レジストリの AppsUseLightTheme）か、差し替えの値。
    /// </summary>
    public static SystemUiMode UiMode =>
        Platform.TryInvoke(AppJson.Module, AppJson.MethodUiMode, PlatformJson.StringObject(), out string reply)
            ? AppJson.ReadNight(reply)
            : SystemUiMode.Unknown;

    /// <summary>
    /// <see cref="UiModeChangedEvent"/> のイベントの JSON を読む。
    /// </summary>
    /// <param name="json">イベントの JSON（SEED.Events・PlatformEvents.OnEvent が渡すもの）。</param>
    /// <param name="mode">新しい明暗。</param>
    /// <returns>このイベントで、data を読めたら true。</returns>
    public static bool TryParseUiModeEvent(string json, out SystemUiMode mode)
    {
        bool ok = AlarmJson.TryReadEvent(json, UiModeChangedEvent,
            data => AppJson.ToUiMode(AlarmJson.GetString(data, AppJson.KeyNight)), out SystemUiMode read);
        mode = ok ? read : SystemUiMode.Unknown;
        return ok;
    }

    // ── 予測型の戻る（W2 の手直し P1-3。docs/android.md §25.18）──

    /// <summary>
    /// 戻るの手ぶりが始まったイベントの名前（Android 14 以上。data の gesture・progress・edge・touch_x・touch_y を
    /// <see cref="BackGestureEvent.TryParse"/> で読む）。
    /// </summary>
    public const string BackStartedEvent = "platform.back_started";

    /// <summary>戻るの手ぶりが進んだイベントの名前（Android 14 以上。毎フレーム届く。data は <see cref="BackStartedEvent"/> と同じ形）。</summary>
    public const string BackProgressedEvent = "platform.back_progressed";

    /// <summary>戻るの手ぶりが取り消されたイベントの名前（Android 14 以上。指を戻した。data は gesture だけ）。</summary>
    public const string BackCancelledEvent = "platform.back_cancelled";

    /// <summary>
    /// 戻るが確定したイベントの名前（Android 13 以上。data は gesture だけ）。Android では続けて <c>KeyCode.Escape</c> が届く
    /// （別の道で届くので同じフレームとは限らない）。
    /// </summary>
    public const string BackInvokedEvent = "platform.back_invoked";

    /// <summary>
    /// 予測型の戻るが有効か（最後の <see cref="SetBackCallbackEnabled"/> の返答。一度も呼んでいなければ false）。
    /// Android でプロジェクト設定 android.predictive_back が true の APK・Android 13 以上のときだけ true になる（デスクトップの模擬は常に false）。
    /// </summary>
    public static bool IsPredictiveBackEnabled { get; private set; }

    /// <summary>
    /// アプリが戻るを受けるかを知らせる（予測型の戻る。W2 の手直し P1-3）。受ける層（ダイアログ・画面のスタックなど）があるときは true
    /// （アプリのコールバックが戻るを受け、手ぶりのイベントと Escape が届く）、無い（根）ときは false（システムに任せ、背面へ回る見た目が出る。
    /// Android 13〜15 でランチャー以外から起動した根は従来どおり Escape が届くので、<see cref="MoveTaskToBack"/> で背面へ回す）。
    /// 起動したときは「受ける」状態。すぐ返る（切り替えは UI スレッドで少し後）。ふつうは SEED.UI の BackDispatcher が状態の変わったときだけ呼ぶ。
    /// 予測型の戻るが無効なら何もしない。デスクトップの模擬は記録してログだけ。
    /// </summary>
    /// <param name="on">受ける層があるなら true、無ければ false。</param>
    /// <returns>基盤が受け付けて、予測型の戻るが有効なら true（<see cref="IsPredictiveBackEnabled"/> も同じ値になる）。
    /// false なら何もしていない（無効・失敗。失敗は <see cref="Platform.LastError"/>）。</returns>
    public static bool SetBackCallbackEnabled(bool on)
    {
        bool accepted = Platform.TryInvoke(AppJson.Module, BackJson.MethodSetBackCallback, PlatformJson.BoolObject(BackJson.KeyOn, on), out string reply);
        IsPredictiveBackEnabled = accepted && AlarmJson.ReadReplyBool(reply, BackJson.KeyEnabled);
        return IsPredictiveBackEnabled;
    }
}
