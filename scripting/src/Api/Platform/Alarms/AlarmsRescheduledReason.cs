namespace SEED.Platform;

/// <summary><see cref="AlarmsRescheduledEvent"/> の理由（W1-3）。</summary>
public enum AlarmsRescheduledReason
{
    /// <summary>知らない理由（<see cref="AlarmsRescheduledEvent.ReasonName"/> に元の文字列）。</summary>
    Unknown,

    /// <summary>"boot": 再起動（Android 15+ は強制停止の後にアプリが停止状態から出たときにも）。</summary>
    Boot,

    /// <summary>"time_changed": 端末の時刻・タイムゾーンが変わった（アプリは次の時刻を計算し直して予約し直す）。</summary>
    TimeChanged,

    /// <summary>"package_replaced": アプリが更新された。</summary>
    PackageReplaced,

    /// <summary>"permission_changed": 正確なアラームの特別なアクセスが許可された。</summary>
    PermissionChanged,
}
