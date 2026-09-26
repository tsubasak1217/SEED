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
}
