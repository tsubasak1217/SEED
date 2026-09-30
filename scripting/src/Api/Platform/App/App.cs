// App の中では、下で足した App.Platform（OS の種類。2026-10-01）がクラス SEED.Platform.Platform の名前を隠すので、
// クラスはこの別名で呼ぶ（利用者のスクリプトの Platform.IsSupported などには影響しない）。
using PlatformApi = SEED.Platform.Platform;

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
/// <see cref="SEED.Platform.Platform.LastError"/>）。
/// </para>
///
/// <para><b>OS の種類と版・前面と背面（2026-10-01）</b><br/>
/// <see cref="Platform"/>（<see cref="PlatformKind"/>）と <see cref="OsVersion"/>（Android の API レベル）で OS の版による出し分けをする。
/// 前面へ戻った・前面を離れたことはイベント <see cref="ResumedEvent"/> / <see cref="PausedEvent"/>（<see cref="AppLifecycleEvent.TryParse"/>）
/// で届く（設定の画面から戻ったときに権限を問い直す契機）。
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
    /// <summary><see cref="SEED.Platform.Platform.LastError"/>: その URL を開けるアプリが端末に無い（<see cref="OpenUrl"/>。Android）。</summary>
    public const string ErrorNoHandler = "no_handler";

    /// <summary><see cref="SEED.Platform.Platform.LastError"/>: 開かない scheme（file: / content: / javascript:。<see cref="OpenUrl"/>）。</summary>
    public const string ErrorSchemeNotAllowed = "scheme_not_allowed";

    /// <summary><see cref="SEED.Platform.Platform.LastError"/>: URL の形が約束に合わない（空・scheme が無い・制御文字・8192 文字を超える。<see cref="OpenUrl"/>）。</summary>
    public const string ErrorInvalidArgument = "invalid_argument";

    /// <summary><see cref="SEED.Platform.Platform.LastError"/>: 操作する画面（Activity）が無い（Android）。</summary>
    public const string ErrorNoActivity = Window.ErrorNoActivity;

    /// <summary>URL の最大の長さ（Unicode の符号位置の数。<see cref="OpenUrl"/> と <see cref="LaunchInfo.Uri"/>）。</summary>
    public const int MaxUrlLength = 8192;

    /// <summary>
    /// この起動の理由（呼ぶたびに問い合わせる。Android でも IPC なしで答えるので軽い）。
    /// 取れなければ <see cref="LaunchKind.Launcher"/>（<see cref="SEED.Platform.Platform.LastError"/> に理由）。
    /// </summary>
    public static LaunchInfo LaunchReason
    {
        get
        {
            if (!PlatformApi.TryInvoke(LaunchJson.ModulePlatform, LaunchJson.MethodLaunchReason, PlatformJson.StringObject(), out string reply))
            {
                return LaunchInfo.Launcher;
            }
            if (!AlarmJson.TryReadReplyObject(reply, LaunchJson.KeyLaunch, LaunchInfo.FromJson, out LaunchInfo launch))
            {
                PlatformApi.LastError = PlatformApi.ErrorInvalidReply;
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
    /// <returns>受け付けたら true（false なら <see cref="SEED.Platform.Platform.LastError"/>）。</returns>
    public static bool MoveTaskToBack() =>
        PlatformApi.TryInvoke(AppJson.Module, AppJson.MethodMoveTaskToBack, PlatformJson.StringObject(), out _);

    /// <summary>
    /// URL を端末のアプリで開く（Android の ACTION_VIEW。W1-6）。http / https（ブラウザ）・mailto（メール）・tel（電話）・
    /// アプリ独自の scheme（そのアプリ）を開ける。file: / content: / javascript: は開かない（<see cref="ErrorSchemeNotAllowed"/>）。
    /// 開けるアプリが無ければ <see cref="ErrorNoHandler"/>。デスクトップの模擬は同じ規則で判定し、http / https / mailto だけを
    /// PC の既定のアプリで開く（ほかの scheme は判定だけで true。環境変数 SEED_PLATFORM_SIM_NO_OPEN=1 なら何も開かない）。
    /// </summary>
    /// <param name="url">開く URL（例 "https://example.com"）。</param>
    /// <returns>開いたら true（false なら <see cref="SEED.Platform.Platform.LastError"/>）。</returns>
    public static bool OpenUrl(string url) =>
        PlatformApi.TryInvoke(AppJson.Module, AppJson.MethodOpenUrl, PlatformJson.StringObject((AppJson.KeyUrl, url)), out _);

    /// <summary>
    /// 端末の「アプリ情報」の画面を開く（Android の Settings.ACTION_APPLICATION_DETAILS_SETTINGS。W1-6）。権限の種類ごとの画面は
    /// <see cref="Permissions.OpenSettings"/>。すぐ返る（開くのは UI スレッドで少し後）。デスクトップの模擬は何もしない（ログだけ）。
    /// </summary>
    /// <returns>受け付けたら true（false なら <see cref="SEED.Platform.Platform.LastError"/>）。</returns>
    public static bool OpenAppSettings() =>
        PlatformApi.TryInvoke(AppJson.Module, AppJson.MethodOpenAppSettings, PlatformJson.StringObject(), out _);

    // ── OS の種類と版（2026-10-01。W3-5 で見つかった不足）──

    /// <summary>
    /// 動いている OS の種類（Android の端末は <see cref="PlatformKind.Android"/>、デスクトップの模擬はホストの OS〈<see cref="PlatformKind.Windows"/> など〉）。
    /// 最初に成功した値を控えるので毎フレーム読んでよい（Android でも IPC なし）。取れなければ <see cref="PlatformKind.Unknown"/>
    /// （<see cref="SEED.Platform.Platform.LastError"/> に理由。次に読まれたときに問い直す）。
    /// </summary>
    public static PlatformKind Platform => AppOsInfo.TryGet(out PlatformKind platform, out _) ? platform : PlatformKind.Unknown;

    /// <summary>
    /// OS の版の番号（Android は Build.VERSION.SDK_INT＝API レベル。例 Android 13 = 33・14 = 34）。
    /// 権限の段の出し分け（通知の実行時の許可は 33 以上・フルスクリーン通知の特別なアクセスは 34 以上など）に使う。
    /// デスクトップの模擬は 0（環境変数 SEED_PLATFORM_SIM_OS_VERSION に整数を書くとその値。版で分ける画面を PC で試す）。
    /// 最初に成功した値を控えるので毎フレーム読んでよい。取れなければ 0（<see cref="SEED.Platform.Platform.LastError"/> に理由）。
    /// </summary>
    public static int OsVersion => AppOsInfo.TryGet(out _, out int osVersion) ? osVersion : 0;

    // ── 前面・背面の知らせ（2026-10-01。W3-5 で見つかった不足）──

    /// <summary>
    /// 前面へ戻ったイベントの名前（"platform.resumed"。data は <see cref="AppLifecycleEvent.TryParse"/> で読む: Count・BackgroundMs）。
    /// Android は MainActivity.onResume（<b>プロセスの起動の最初の onResume では届かない</b>＝必ず <see cref="PausedEvent"/> の後）。
    /// 戻ったときの権限の結果・変化のイベントより後に届くので、設定の画面から戻ったときの問い直しはこれを受けて
    /// <see cref="Permissions.Check"/> すればよい。デスクトップの模擬は窓がフォーカスを得たとき（Play の間だけ）と
    /// <see cref="PlatformDiagnostics.SimulateLifecycle"/>。
    /// </summary>
    public const string ResumedEvent = "platform.resumed";

    /// <summary>
    /// 前面を離れたイベントの名前（"platform.paused"。data は Count）。Android は MainActivity.onPause。背面にいる間は描画の面が無く
    /// フレームが回らないことが多いので、背面にいる間には届かず、戻ったときに <see cref="ResumedEvent"/> の直前に届くことがある
    /// （保存などをこれに頼らない）。デスクトップの模擬は窓がフォーカスを失ったとき（エディタの別のパネルを押しただけでも）と
    /// <see cref="PlatformDiagnostics.SimulateLifecycle"/>。
    /// </summary>
    public const string PausedEvent = "platform.paused";

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
        PlatformApi.TryInvoke(AppJson.Module, AppJson.MethodUiMode, PlatformJson.StringObject(), out string reply)
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
    /// false なら何もしていない（無効・失敗。失敗は <see cref="SEED.Platform.Platform.LastError"/>）。</returns>
    public static bool SetBackCallbackEnabled(bool on)
    {
        bool accepted = PlatformApi.TryInvoke(AppJson.Module, BackJson.MethodSetBackCallback, PlatformJson.BoolObject(BackJson.KeyOn, on), out string reply);
        IsPredictiveBackEnabled = accepted && AlarmJson.ReadReplyBool(reply, BackJson.KeyEnabled);
        return IsPredictiveBackEnabled;
    }
}
