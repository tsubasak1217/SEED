namespace SEED.Platform;

/// <summary>
/// 予約を張り直したイベント "platform.alarms.rescheduled" の中身（不変値型。W1-3）。
///
/// <para>
/// 再起動・端末の時刻／タイムゾーンの変更・アプリの更新・正確なアラームの許可のときに、:seed_platform が控えから
/// 予約を張り直した（控えに予約があったときだけ届く）。予約は UTC の絶対時刻のままなので、<see cref="AlarmsRescheduledReason.TimeChanged"/>
/// では、アプリが壁時計で次の時刻を計算し直して予約し直す（例「毎朝 7:00」はタイムゾーンが変わると UTC がずれる）。
/// </para>
/// </summary>
public readonly struct AlarmsRescheduledEvent
{
    /// <summary>イベントの名前（SEED.Events でもこの名前で届く）。</summary>
    public const string Name = "platform.alarms.rescheduled";

    /// <summary>理由の文字列: 再起動。</summary>
    public const string ReasonBoot = "boot";

    /// <summary>理由の文字列: 時刻・タイムゾーンの変更。</summary>
    public const string ReasonTimeChanged = "time_changed";

    /// <summary>理由の文字列: アプリの更新。</summary>
    public const string ReasonPackageReplaced = "package_replaced";

    /// <summary>理由の文字列: 正確なアラームの許可。</summary>
    public const string ReasonPermissionChanged = "permission_changed";

    /// <summary>理由。</summary>
    public AlarmsRescheduledReason Reason { get; }

    /// <summary>理由の元の文字列（"boot" など）。</summary>
    public string ReasonName { get; }

    /// <summary>張り直した件数。</summary>
    public int Count { get; }

    /// <summary>鳴らなかったと記録した件数（それぞれ <see cref="AlarmMissedEvent"/> が別に届く）。</summary>
    public int Missed { get; }

    /// <summary>張り直せなかった件数（正確なアラームの許可が無い等。控えには残り、許可が戻ったときに張り直す）。</summary>
    public int Failed { get; }

    /// <summary>値を作る。</summary>
    internal AlarmsRescheduledEvent(string reasonName, int count, int missed, int failed)
    {
        ReasonName = reasonName;
        Reason = reasonName switch
        {
            ReasonBoot => AlarmsRescheduledReason.Boot,
            ReasonTimeChanged => AlarmsRescheduledReason.TimeChanged,
            ReasonPackageReplaced => AlarmsRescheduledReason.PackageReplaced,
            ReasonPermissionChanged => AlarmsRescheduledReason.PermissionChanged,
            _ => AlarmsRescheduledReason.Unknown,
        };
        Count = count;
        Missed = missed;
        Failed = failed;
    }

    /// <summary>
    /// イベントの JSON（SEED.Events の引数）を読む。
    /// </summary>
    /// <param name="json">イベントの JSON 全体。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が <see cref="Name"/> で、中身が読めたら true。</returns>
    public static bool TryParse(string json, out AlarmsRescheduledEvent value) =>
        AlarmJson.TryReadEvent(json, Name, data => new AlarmsRescheduledEvent(
            AlarmJson.GetString(data, AlarmJson.KeyReason),
            (int)AlarmJson.GetLong(data, AlarmJson.KeyCount),
            (int)AlarmJson.GetLong(data, AlarmJson.KeyMissed),
            (int)AlarmJson.GetLong(data, AlarmJson.KeyFailed)), out value);

    /// <summary>ログ向けの 1 行。</summary>
    public override string ToString() => $"{ReasonName}: 張り直し {Count}・鳴らなかった {Missed}・失敗 {Failed}";
}
