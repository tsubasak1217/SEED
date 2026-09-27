using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 目覚まし（決まった時刻に確実に鳴らす。W1-3 の予約の基盤＋W1-4a の鳴動）。
///
/// <para><b>仕組み</b><br/>
/// Android では別プロセス :seed_platform（Java）が予約の控え（端末保護ストレージ）を持ち、AlarmManager.setAlarmClock で張る
/// （Doze でも時刻どおり。ステータスバーに目覚ましの印）。再起動・時刻の変更・アプリの更新・権限の変化で控えから張り直し、
/// 電源断などで過ぎた予約は鳴らさずに <see cref="AlarmMissedEvent"/> を記録する。鳴ったら <see cref="AlarmFiredEvent"/>。
/// 鳴ると同じ :seed_platform の前景サービスが音（USAGE_ALARM）・振動・フルスクリーン通知を出し、<see cref="StopRinging"/> か
/// 安全弁（MaxRingMinutes）まで鳴り続ける（エンジンが落ちても・最近のタスクから消しても止まらない）。
/// デスクトップ（エディタの Play・単体起動）はエンジンの中の模擬が壁時計で鳴らす（音は鳴らさない。Play を止めると予約も鳴動も消える）。
/// </para>
///
/// <para><b>呼び方</b><br/>
/// 呼び出しは同期で「受け付けたか」だけを返す（false なら <see cref="Platform.LastError"/>）。Android の最初の呼び出しは
/// :seed_platform の起動を待たずに <see cref="Platform.ErrorConnecting"/> で失敗するので、<see cref="PlatformEvents.Connected"/> の後に呼び直す。
/// APK に機能 alarm が入っていない（project_settings.json の android.features に "alarm" が無い）と <see cref="ErrorFeatureNotEnabled"/>。
/// 時刻はすべて UTC の epoch ミリ秒。
/// </para>
/// </summary>
public static class Alarms
{
    /// <summary><see cref="Platform.LastError"/>: 正確なアラームを張れない（Android 12 系で特別なアクセスが無い）。黙って不正確な予約にはしない。</summary>
    public const string ErrorExactAlarmNotAllowed = "exact_alarm_not_allowed";

    /// <summary><see cref="Platform.LastError"/>: APK に機能 alarm が入っていない（android.features に "alarm" を足す）。</summary>
    public const string ErrorFeatureNotEnabled = "feature_not_enabled";

    /// <summary><see cref="Platform.LastError"/>: 引数の値が約束に合わない（ID が空・時刻が 0 以下・文字列が長すぎる等）。</summary>
    public const string ErrorInvalidArgument = "invalid_argument";

    /// <summary><see cref="Platform.LastError"/>: 予約の数が上限（<see cref="MaxScheduledAlarms"/>）に達している。</summary>
    public const string ErrorTooManyAlarms = "too_many_alarms";

    /// <summary><see cref="Platform.LastError"/>: 予約の控えを書けなかった（空き容量など）。予約は張られていない。</summary>
    public const string ErrorStoreWriteFailed = "store_write_failed";

    /// <summary><see cref="Platform.LastError"/>: AlarmManager が予約を受け付けなかった。</summary>
    public const string ErrorScheduleFailed = "schedule_failed";

    /// <summary>控えに持てる予約の数の上限（新しい ID だけを断る。置き換えは通る）。</summary>
    public const int MaxScheduledAlarms = 64;

    /// <summary>APK に機能 alarm が無いと分かったか（最初に feature_not_enabled が返った後は true）。</summary>
    private static bool _featureMissing;

    /// <summary>
    /// 目覚ましが使えるか（<see cref="Platform.IsSupported"/> で、機能 alarm が無いと分かっていない）。デスクトップの模擬は true。
    /// IPC を通らない（APK に機能が無いことは、最初の呼び出しが <see cref="ErrorFeatureNotEnabled"/> で失敗したときに分かり、以後 false）。
    /// </summary>
    public static bool IsSupported => Platform.IsSupported && !_featureMissing;

    /// <summary>
    /// 正確なアラームを張れるか（Android 12 系の特別なアクセス。11 以前と 13 以降の USE_EXACT_ALARM は true。模擬は true）。
    /// 呼ぶたびに :seed_platform へ問い合わせる（毎フレーム読まない）。失敗したら false（<see cref="Platform.LastError"/>）。
    /// </summary>
    public static bool CanScheduleExact =>
        Invoke(AlarmJson.MethodCanScheduleExact, PlatformJson.StringObject(), out string reply)
        && AlarmJson.ReadReplyBool(reply, AlarmJson.KeyCanScheduleExact);

    /// <summary>
    /// 予約する（控えにも書く。再起動の後も残る）。同じ <see cref="AlarmRequest.Id"/> の予約は置き換わる。
    /// </summary>
    /// <param name="request">予約。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool Schedule(AlarmRequest request)
    {
        if (request == null)
        {
            Platform.LastError = ErrorInvalidArgument;
            return false;
        }
        return Invoke(AlarmJson.MethodSchedule, AlarmJson.WriteRequest(request), out _);
    }

    /// <summary>
    /// 予約を 1 つ取り消す（無い ID でも成功）。
    /// </summary>
    /// <param name="id">予約の ID。</param>
    /// <returns>取り消せた（その ID の予約がもう無い）なら true。</returns>
    public static bool Cancel(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            Platform.LastError = ErrorInvalidArgument;
            return false;
        }
        return Invoke(AlarmJson.MethodCancel, AlarmJson.IdObject(id), out _);
    }

    /// <summary>予約を全部取り消す。</summary>
    /// <returns>取り消せたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool CancelAll() => Invoke(AlarmJson.MethodCancelAll, PlatformJson.StringObject(), out _);

    /// <summary>
    /// 控えの一覧（予定時刻の順）。失敗したら空の配列（<see cref="Platform.LastError"/>）。
    /// </summary>
    public static ScheduledAlarm[] GetScheduled()
    {
        if (!Invoke(AlarmJson.MethodList, PlatformJson.StringObject(), out string reply))
        {
            return Array.Empty<ScheduledAlarm>();
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(reply);
            if (!document.RootElement.TryGetProperty(AlarmJson.KeyAlarms, out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
            {
                Platform.LastError = Platform.ErrorInvalidReply;
                return Array.Empty<ScheduledAlarm>();
            }
            var alarms = new List<ScheduledAlarm>(rows.GetArrayLength());
            foreach (JsonElement row in rows.EnumerateArray())
            {
                alarms.Add(ScheduledAlarm.FromJson(row));
            }
            return alarms.ToArray();
        }
        catch (JsonException)
        {
            Platform.LastError = Platform.ErrorInvalidReply;
            return Array.Empty<ScheduledAlarm>();
        }
    }

    /// <summary>
    /// 鳴動中の目覚まし（W1-4a）。鳴っていなければ null（<see cref="Platform.LastError"/> は空）。
    /// 失敗したときも null（<see cref="Platform.LastError"/> に理由）。呼ぶたびに :seed_platform へ問い合わせる（毎フレーム読まない）。
    /// </summary>
    public static RingingAlarm? GetRinging()
    {
        if (!Invoke(AlarmJson.MethodGetRinging, PlatformJson.StringObject(), out string reply))
        {
            return null;
        }
        return AlarmJson.TryReadReplyObject(reply, AlarmJson.KeyRinging, RingingAlarm.FromJson, out RingingAlarm ringing)
            ? ringing
            : null;
    }

    /// <summary>
    /// 鳴動を止める（解除・スヌーズ。W1-4a）。音・振動・鳴動の通知が止まり、<see cref="AlarmRingStoppedEvent"/>（Stopped）が届く。
    /// 待ち行列に次の予約があれば続けて鳴り始める。鳴っていなくても true（冪等）。
    /// </summary>
    /// <param name="id">止める予約の ID（null・空なら今鳴っているもの。待ち行列にいる予約の ID なら鳴らさずに外す）。</param>
    /// <returns>受け付けたら true（false なら <see cref="Platform.LastError"/>）。</returns>
    public static bool StopRinging(string? id = null) =>
        Invoke(AlarmJson.MethodStopRinging, AlarmJson.IdObject(id ?? string.Empty), out _);

    /// <summary>命令を送り、機能の有無を覚える（feature_not_enabled なら以後 <see cref="IsSupported"/> は false）。</summary>
    private static bool Invoke(string method, string json, out string reply)
    {
        bool ok = Platform.TryInvoke(AlarmJson.Module, method, json, out reply);
        if (ok)
        {
            _featureMissing = false;
        }
        else if (Platform.LastError == ErrorFeatureNotEnabled)
        {
            _featureMissing = true;
        }
        return ok;
    }
}
