namespace SEED.Platform;

/// <summary><see cref="AlarmRingStoppedEvent"/> の理由（W1-4a）。</summary>
public enum AlarmRingStopReason
{
    /// <summary>知らない理由（新しい版の APK など。<see cref="AlarmRingStoppedEvent.ReasonName"/> に元の文字列）。</summary>
    Unknown,

    /// <summary>"stopped": アプリが止めた（<see cref="Alarms.StopRinging"/>）。</summary>
    Stopped,

    /// <summary>"timeout": 安全弁（<see cref="AlarmRequest.MaxRingMinutes"/>）で自動で止まった。</summary>
    Timeout,

    /// <summary>"error": 鳴らし続けられなかった（前景サービスにできない・サービスが壊された等）。</summary>
    Error,
}
