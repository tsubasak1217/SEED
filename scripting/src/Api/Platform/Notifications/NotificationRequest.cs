namespace SEED.Platform;

/// <summary>
/// 出す通知 1 件（<see cref="Notifications.Show"/> に渡す。W1-5）。
///
/// <para>
/// 受け付ける値（Android の :seed_platform とデスクトップの模擬で同じ規則。合わなければ Show が false・LastError "invalid_argument"）:
/// Id・ChannelId は 1〜128 文字（ChannelId は先に <see cref="Notifications.EnsureChannel"/> で作ったもの。"seed_platform" で始まる ID は
/// プラットフォーム層が使うので不可）、Title・Body は 4096 文字まで、Actions は <see cref="MaxActions"/> 個まで（ID が重なっては駄目）、
/// PayloadJson は 16384 文字まで。Category は下の Category* の 5 つ（それ以外は付けない。誤りにはしない）。
/// </para>
/// </summary>
public sealed class NotificationRequest
{
    /// <summary>操作（ボタン）の最大の数。</summary>
    public const int MaxActions = 3;

    /// <summary><see cref="Category"/>: 目覚まし。</summary>
    public const string CategoryAlarm = "alarm";

    /// <summary><see cref="Category"/>: 利用者が決めた予定の知らせ（スヌーズ中の知らせなど）。</summary>
    public const string CategoryReminder = "reminder";

    /// <summary><see cref="Category"/>: 状態の表示。</summary>
    public const string CategoryStatus = "status";

    /// <summary><see cref="Category"/>: 予定表の出来事。</summary>
    public const string CategoryEvent = "event";

    /// <summary><see cref="Category"/>: 長い処理の進み具合。</summary>
    public const string CategoryProgress = "progress";

    /// <summary>通知の ID（アプリが決める。同じ ID の通知は置き換わる。<see cref="Notifications.Cancel"/> もこの ID）。</summary>
    public string Id = string.Empty;

    /// <summary>出すチャネルの ID（<see cref="Notifications.EnsureChannel"/> で作ったもの）。</summary>
    public string ChannelId = string.Empty;

    /// <summary>題。</summary>
    public string Title = string.Empty;

    /// <summary>本文（長文は通知を開くと全部見える。Android の BigTextStyle）。</summary>
    public string Body = string.Empty;

    /// <summary>常駐（スワイプで消えにくい。本文を押しても消えない。Android 14+ は利用者が消せる場合がある）。常駐でなければ本文を押すと消える。</summary>
    public bool Ongoing;

    /// <summary>種類（<see cref="CategoryAlarm"/> など。空なら無し）。</summary>
    public string Category = string.Empty;

    /// <summary>操作（ボタン。null・空なら無し。最大 <see cref="MaxActions"/>）。押すと起動理由 <see cref="LaunchKind.NotificationAction"/>。</summary>
    public NotificationAction[]? Actions;

    /// <summary>起動理由（本文のタップ・操作）にそのまま返す任意の JSON。</summary>
    public string PayloadJson = string.Empty;
}
