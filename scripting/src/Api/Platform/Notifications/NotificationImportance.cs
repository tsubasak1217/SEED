namespace SEED.Platform;

/// <summary>
/// 通知チャネルの重要度（<see cref="Notifications.EnsureChannel"/>。W1-5）。
/// Android では NotificationManager.IMPORTANCE_LOW / DEFAULT / HIGH になる。チャネルを作った後に変えられるのは利用者だけ
/// （同じ ID でもう一度作っても、名前と説明だけが変わる）。
/// </summary>
public enum NotificationImportance
{
    /// <summary>"low": 低い（音なし。ステータスバーの通知の一覧には出る）。</summary>
    Low,

    /// <summary>"default": 普通（音あり。画面の上には出ない）。</summary>
    Default,

    /// <summary>"high": 高い（音あり・ヘッドアップ通知で画面の上に出る）。</summary>
    High,
}
