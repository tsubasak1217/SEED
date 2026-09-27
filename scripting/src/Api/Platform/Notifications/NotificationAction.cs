namespace SEED.Platform;

/// <summary>
/// 通知の操作（ボタン）1 つ（<see cref="NotificationRequest.Actions"/>。W1-5）。
///
/// <para>
/// 押すとアプリが起動し（死んでいれば起動、生きていれば前面へ）、起動理由が <see cref="LaunchKind.NotificationAction"/>・
/// <see cref="LaunchInfo.ActionId"/> == <see cref="Id"/>・<see cref="LaunchInfo.Id"/> == 通知の ID になる（起動のときは
/// <see cref="App.LaunchReason"/>、動いている間はイベント <see cref="LaunchInfo.EventName"/>）。押しても通知は消えないので、
/// 用が済んだら <see cref="Notifications.Cancel"/> で消す。
/// </para>
/// </summary>
public sealed class NotificationAction
{
    /// <summary>操作の ID（起動理由の ActionId。1〜128 文字。同じ通知の中で重ならないこと）。</summary>
    public string Id = string.Empty;

    /// <summary>ボタンの文字（1〜4096 文字）。</summary>
    public string Label = string.Empty;

    /// <summary>空の操作を作る（欄は後から入れる）。</summary>
    public NotificationAction()
    {
    }

    /// <summary>ID と文字を指定して作る。</summary>
    /// <param name="id">操作の ID。</param>
    /// <param name="label">ボタンの文字。</param>
    public NotificationAction(string id, string label)
    {
        Id = id;
        Label = label;
    }
}
