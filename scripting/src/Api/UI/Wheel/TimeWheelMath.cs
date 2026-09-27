using System;

namespace SEED.UI;

// ============================================================
//  TimeWheelMath.cs — 時刻ホイールの時・分・午前/午後の計算（12/24 時間・連動・分の刻み。W2-5。純粋な計算）
//
//  【時の列】24 行（端をつなげる）。24 時間表記は 0〜23、12 時間表記は「12, 1, …, 11（午前）, 12, 1, …, 11（午後）」
//  （表示の時 =（時 + 11）mod 12 + 1）。12 時間表記の表示は 12 時間で同じなので、午前/午後が入れ替わっても時の列は動かさない。
//  【連動】Flutter の CupertinoDatePicker（date_picker.dart の _CupertinoDatePickerDateTimeState。2026-09-28 に取得して確かめた）と同じ:
//    - 時の列の行の「半日」（region = 項目 ÷ 12）と、選んでいる午前/午後（amPm）を別に持つ。region ≠ amPm のとき
//      「入れ替わっている」（flipped）で、時 =（項目 + 12）mod 24、そうでなければ 時 = 項目
//    - 時の列が 11 ↔ 12・23 ↔ 0 を越えて region が変わったら amPm も入れ替える（flipped はそのまま）＝ 11 時 → 12 時は午前 → 午後。
//      12 時間表記なら午前/午後の列をその値へ動かす（Flutter は 300ms・easeOut）
//    - 午前/午後の列を指で変えたら amPm だけを変える（flipped が入れ替わり、時が 12 ずれる。時の列は動かない）
//  【分の刻み】1 時間を割り切る刻み（1・2・3・4・5・6・10・12・15・20・30・60。Flutter の minuteInterval と同じ条件）。
//  刻みに合わない時刻は最も近い刻みへ丸める（23:58 を 5 分刻み → 0:00。時・日をまたいで繰り上がる）。
//  エンジンの API に触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>時の列の半日と、選んでいる午前/午後（<see cref="TimeWheelMath"/> の連動の状態）。</summary>
public struct MeridiemState
{
    /// <summary>選んでいる午前/午後（<see cref="TimeWheelMath.Am"/> / <see cref="TimeWheelMath.Pm"/>）。</summary>
    public int AmPm;

    /// <summary>時の列の中央の行の半日（項目 ÷ 12。0 = 前半・1 = 後半）。</summary>
    public int Region;

    /// <summary>午前/午後が時の列の半日と入れ替わっているか（入れ替わっていれば時 = 項目 + 12）。</summary>
    public readonly bool Flipped => AmPm != Region;

    /// <summary>作る。</summary>
    public MeridiemState(int amPm, int region)
    {
        AmPm = amPm;
        Region = region;
    }
}

/// <summary>時刻ホイールの計算（12/24 時間の変換・午前/午後の連動・分の刻み）。</summary>
public static class TimeWheelMath
{
    /// <summary>1 日の時間の数。</summary>
    public const int HoursPerDay = 24;
    /// <summary>半日の時間の数。</summary>
    public const int HoursPerHalfDay = 12;
    /// <summary>1 時間の分の数。</summary>
    public const int MinutesPerHour = 60;
    /// <summary>1 日の分の数。</summary>
    public const int MinutesPerDay = HoursPerDay * MinutesPerHour;
    /// <summary>午前（午前/午後の列の項目 0）。</summary>
    public const int Am = 0;
    /// <summary>午後（午前/午後の列の項目 1）。</summary>
    public const int Pm = 1;
    /// <summary>午前/午後の列の項目の数。</summary>
    public const int MeridiemCount = 2;
    /// <summary>時の列の行の数（12 時間表記でも 24 行＝午前と午後の半日）。</summary>
    public const int HourRows = HoursPerDay;
    /// <summary>分の刻みの既定（刻みが使えない値のとき）。</summary>
    public const int DefaultMinuteStep = 1;

    /// <summary>
    /// 分の刻みを使える値にする（1 時間を割り切る 1〜60。それ以外は <see cref="DefaultMinuteStep"/>）。
    /// </summary>
    public static int NormalizeMinuteStep(int step)
        => step > 0 && step <= MinutesPerHour && MinutesPerHour % step == 0 ? step : DefaultMinuteStep;

    /// <summary>表示する時（24 時間表記は 0〜23、12 時間表記は 1〜12）。</summary>
    /// <param name="hour24">時（0〜23。範囲の外は 24 で回す）。</param>
    /// <param name="use24Hour">24 時間表記か。</param>
    public static int DisplayHour(int hour24, bool use24Hour)
    {
        int h = WheelLoop.ItemOfRow(hour24, HoursPerDay);
        return use24Hour ? h : (h + HoursPerHalfDay - 1) % HoursPerHalfDay + 1;
    }

    /// <summary>時の午前/午後（0〜11 は午前、12〜23 は午後）。</summary>
    public static int MeridiemOf(int hour24) => WheelLoop.ItemOfRow(hour24, HoursPerDay) / HoursPerHalfDay;

    /// <summary>時から連動の状態を作る（入れ替わりなし: 半日 = 午前/午後 = 時の午前/午後）。</summary>
    public static MeridiemState StateFor(int hour24)
    {
        int m = MeridiemOf(hour24);
        return new MeridiemState(m, m);
    }

    /// <summary>時の列の項目（0〜23）が表す時（入れ替わっていれば 12 ずらす）。</summary>
    public static int HourOfItem(int item, in MeridiemState state)
    {
        int h = WheelLoop.ItemOfRow(item, HoursPerDay);
        return state.Flipped ? (h + HoursPerHalfDay) % HoursPerDay : h;
    }

    /// <summary>
    /// 時の列の中央の項目が変わった（指・慣性）: 半日が変わったら午前/午後も入れ替える（入れ替わりの有無はそのまま）。
    /// </summary>
    /// <returns>午前/午後が入れ替わった（12 時間表記なら午前/午後の列を動かす合図）なら true。</returns>
    public static bool OnHourItemChanged(ref MeridiemState state, int item)
    {
        int region = MeridiemOf(item);
        if (region == state.Region) return false;
        state.Region = region;
        state.AmPm = MeridiemCount - 1 - state.AmPm;
        return true;
    }

    /// <summary>午前/午後の列で選んだ（入れ替わりが変わり、時が 12 ずれる）。</summary>
    public static void OnMeridiemSelected(ref MeridiemState state, int amPm)
        => state.AmPm = Math.Clamp(amPm, Am, Pm);

    /// <summary>列の見た目（時の列の項目・午前/午後の列の項目）から連動の状態を作り直す（プログラムの動きを指で止めたとき等）。</summary>
    /// <param name="hourItem">時の列の中央の項目。</param>
    /// <param name="meridiemItem">午前/午後の列の中央の項目（24 時間表記では使わない）。</param>
    /// <param name="use24Hour">24 時間表記か（24 時間表記は入れ替えない）。</param>
    public static MeridiemState Reconcile(int hourItem, int meridiemItem, bool use24Hour)
    {
        int region = MeridiemOf(hourItem);
        return new MeridiemState(use24Hour ? region : Math.Clamp(meridiemItem, Am, Pm), region);
    }

    /// <summary>分の列の行の数（60 ÷ 刻み）。</summary>
    public static int MinuteRows(int step) => MinutesPerHour / NormalizeMinuteStep(step);

    /// <summary>分の列の項目の分（項目 × 刻み。項目は行の数で回す）。</summary>
    public static int MinuteOfItem(int item, int step)
    {
        int s = NormalizeMinuteStep(step);
        return WheelLoop.ItemOfRow(item, MinutesPerHour / s) * s;
    }

    /// <summary>分の項目（分 ÷ 刻み。刻みに合わない分は切り捨て。先に <see cref="RoundToStep"/> で丸めておく）。</summary>
    public static int ItemOfMinute(int minute, int step)
    {
        int s = NormalizeMinuteStep(step);
        return WheelLoop.ItemOfRow(minute, MinutesPerHour) / s;
    }

    /// <summary>
    /// 時刻を分の刻みの最も近い値へ丸める（秒は捨てる。ちょうど間は遅い方。時・日をまたいで繰り上がる: 23:58 → 0:00）。
    /// </summary>
    public static TimeOnly RoundToStep(TimeOnly time, int step)
    {
        int s = NormalizeMinuteStep(step);
        int total = time.Hour * MinutesPerHour + time.Minute;
        int rounded = (int)Math.Round(total / (double)s, MidpointRounding.AwayFromZero) * s;
        rounded = WheelLoop.ItemOfRow(rounded, MinutesPerDay);
        return new TimeOnly(rounded / MinutesPerHour, rounded % MinutesPerHour);
    }

    /// <summary>時と分から時刻を作る（範囲の外は 1 日・1 時間で回す）。</summary>
    public static TimeOnly Compose(int hour24, int minute)
        => new(WheelLoop.ItemOfRow(hour24, HoursPerDay), WheelLoop.ItemOfRow(minute, MinutesPerHour));
}
