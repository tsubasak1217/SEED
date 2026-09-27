namespace SEED.Platform;

/// <summary><see cref="AlarmMissedEvent"/> の理由（W1-3）。</summary>
public enum AlarmMissedReason
{
    /// <summary>知らない理由（新しい版の APK など。<see cref="AlarmMissedEvent.ReasonName"/> に元の文字列）。</summary>
    Unknown,

    /// <summary>"device_off": 電源断・強制停止・更新などで、予約が OS から消えていた間に予定時刻を過ぎた。</summary>
    DeviceOff,

    /// <summary>"permission_revoked": 正確なアラームの許可が取り消されていた間に予定時刻を過ぎた（Android 12 系）。</summary>
    PermissionRevoked,

    /// <summary>
    /// "start_failed": 配信は届いたが、鳴動の前景サービスを起こせなかった（W1-4a。背面からの前景サービス起動の制限など）。
    /// このときは <see cref="AlarmFiredEvent"/> の代わりにこれが届く。
    /// </summary>
    StartFailed,
}
