namespace SEED.Platform;

/// <summary>
/// アプリとしての振る舞い（起動理由など。W1-4a）。
///
/// <para><b>起動理由</b><br/>
/// <see cref="LaunchReason"/> は「この起動の理由」（最後に Activity へ届いた Intent の理由）。目覚ましの鳴動で起きたなら
/// <see cref="LaunchKind.Alarm"/>（予約の ID・予定時刻・payload つき）で、そのときアプリはロック画面の上に出て画面が点いている。
/// 鳴動画面を出して、解除・スヌーズで <see cref="Alarms.StopRinging"/>、片付けたら <see cref="Window.SetShowWhenLocked"/>(false) で
/// ロック画面の上から降りる。起動した後（アプリが動いている間）に届いた起動（目覚まし・通知の操作）は、イベント
/// <see cref="LaunchInfo.EventName"/>（"platform.launch"）で届く（<see cref="LaunchInfo.TryParseEvent"/> で読む）。
/// Android では IPC を通らない（メインプロセスが答える）。デスクトップの模擬は常に <see cref="LaunchKind.Launcher"/>。
/// </para>
/// </summary>
public static class App
{
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
}
