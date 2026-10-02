using System;
using System.Collections.Generic;
using System.Globalization;

namespace SEED.Localization;

// ============================================================
//  LocaleCulture.cs — 言語の文化（CultureInfo）の引き当てと、数・日付・時刻の書式（純粋な計算）
//
//  【Invariant の環境】（docs/localization.md §9）
//  Android の CoreCLR は System.Globalization.Invariant=true で動く（runtime/android/dotnet_runtime.json の runtime_properties。
//  端末の ICU を読めないため。docs/android.md §17.6）。そこでは:
//    - CultureInfo.CurrentUICulture は不変文化（名前が空）→ 端末の言語（SystemLanguage）は取れない（null）
//    - CultureInfo.GetCultureInfo("ja-JP") は CultureNotFoundException → ここで捕まえて不変文化で書く
//  不変文化の書式は英語の月・曜日の名前と "1,234.5" の数になる。言語ごとの書式は言語の表に書き（"format.date": "M月d日"）、
//  数字だけの書式・表の曜日の名前を使えば、Android でも言語どおりに出せる。
//  PC（Windows）は Invariant ではないので、OS の表示言語と文化どおりの書式になる。
// ============================================================

/// <summary>言語の文化の引き当てと書式。</summary>
public static class LocaleCulture
{
    /// <summary>数の書式の小数の桁の上限（大きすぎる桁数を渡されても書式が壊れないように）。</summary>
    public const int MaxFractionDigits = 15;
    /// <summary>日付の書式の既定（文化の短い日付）。</summary>
    public const string DefaultDatePattern = "d";
    /// <summary>時刻の書式の既定（文化の短い時刻）。</summary>
    public const string DefaultTimePattern = "t";
    /// <summary>区切りつきの数の .NET の書式の頭（"N2" = 区切りつき・小数 2 桁）。</summary>
    private const string GroupedNumberFormat = "N";

    /// <summary>文化の名前 → 引いた文化（作れなかった名前は不変文化を覚える）。</summary>
    private static readonly Dictionary<string, CultureInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 文化の名前から文化を引く。空・作れない名前（Invariant の環境のすべての名前を含む）は不変文化。
    /// </summary>
    /// <param name="name">文化の名前（"ja-JP"・"en" など）。</param>
    /// <returns>文化（作れなければ <see cref="CultureInfo.InvariantCulture"/>）。</returns>
    public static CultureInfo Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return CultureInfo.InvariantCulture;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
        }

        CultureInfo resolved;
        try
        {
            resolved = CultureInfo.GetCultureInfo(LocaleIndex.Normalize(name));
        }
        catch (CultureNotFoundException)
        {
            // Invariant の環境（Android）・知らない名前
            resolved = CultureInfo.InvariantCulture;
        }
        catch (ArgumentException)
        {
            // 名前として読めない文字列
            resolved = CultureInfo.InvariantCulture;
        }

        lock (Cache) Cache[name] = resolved;
        return resolved;
    }

    /// <summary>
    /// 端末（OS）の表示の言語（<see cref="CultureInfo.CurrentUICulture"/> の名前。"ja-JP" など）。
    /// Invariant の環境（Android の CoreCLR）・取れない環境では null。
    /// </summary>
    /// <returns>言語の名前か null。</returns>
    public static string? DetectSystemLanguage()
    {
        try
        {
            return LanguageOf(CultureInfo.CurrentUICulture);
        }
        catch (Exception)
        {
            // 文化を引く仕組みそのものが使えない環境（安全側に倒して「わからない」）
            return null;
        }
    }

    /// <summary>文化の言語の名前（不変文化・null は null）。</summary>
    /// <param name="culture">文化。</param>
    /// <returns>名前か null。</returns>
    public static string? LanguageOf(CultureInfo? culture) =>
        culture is null || culture.Name.Length == 0 ? null : culture.Name;

    /// <summary>区切りつきの数（"1,234.5"。区切りと小数点は文化の書き方）。</summary>
    /// <param name="value">数。</param>
    /// <param name="digits">小数の桁（0〜<see cref="MaxFractionDigits"/> に収める）。</param>
    /// <param name="culture">文化。</param>
    /// <returns>書いた数。</returns>
    public static string FormatNumber(double value, int digits, CultureInfo culture)
    {
        int clamped = Math.Clamp(digits, 0, MaxFractionDigits);
        return value.ToString(GroupedNumberFormat + clamped.ToString(CultureInfo.InvariantCulture), culture);
    }

    /// <summary>
    /// 日付（.NET の日付の書式。空なら文化の短い日付。読めない書式は短い日付に戻す）。文化の暦（グレゴリオ暦でない暦。ar-SA の UmAlQura は
    /// 1900〜2077 年だけ）で表せない日付は、同じ書式を不変文化（グレゴリオ暦）で書く（2026-10-03。2 回目のレビュー #33。以前は
    /// ArgumentOutOfRangeException が呼び手へ飛んだ）。
    /// </summary>
    /// <param name="date">日付。</param>
    /// <param name="pattern">書式（"yyyy/M/d"・"D" など）。</param>
    /// <param name="culture">文化。</param>
    /// <returns>書いた日付。</returns>
    public static string FormatDate(DateTime date, string? pattern, CultureInfo culture)
    {
        string format = string.IsNullOrEmpty(pattern) ? DefaultDatePattern : pattern;
        try
        {
            return date.ToString(format, culture);
        }
        catch (FormatException)
        {
            return FormatDateOutsideCalendar(date, DefaultDatePattern, culture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return FormatDateOutsideCalendar(date, format, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 日付を書く（文化の暦の範囲の外なら、同じ書式を不変文化で。不変文化でも読めない書式なら不変文化の短い日付）。
    /// <see cref="FormatDate"/> と差し込み（LocaleFormatter の {name:書式}）の戻り先。
    /// </summary>
    /// <param name="date">日付。</param>
    /// <param name="format">書式。</param>
    /// <param name="culture">文化。</param>
    /// <returns>書いた日付（例外にしない）。</returns>
    internal static string FormatDateOutsideCalendar(DateTime date, string format, CultureInfo culture)
    {
        var invariant = CultureInfo.InvariantCulture;
        try
        {
            return date.ToString(format, culture);
        }
        catch (ArgumentOutOfRangeException) when (!ReferenceEquals(culture, invariant))
        {
            // 文化の暦で表せない: 下で同じ書式を不変文化で書く
        }
        catch (FormatException)
        {
            // 読めない書式: 不変文化（グレゴリオ暦で DateTime の全範囲を表せる）の短い日付は必ず書ける
            return date.ToString(DefaultDatePattern, invariant);
        }
        try
        {
            return date.ToString(format, invariant);
        }
        catch (FormatException)
        {
            return date.ToString(DefaultDatePattern, invariant);
        }
    }

    /// <summary>時刻（.NET の時刻の書式。空なら文化の短い時刻。読めない書式は短い時刻に戻す）。</summary>
    /// <param name="time">時刻。</param>
    /// <param name="pattern">書式（"H:mm"・"h:mm tt" など）。</param>
    /// <param name="culture">文化。</param>
    /// <returns>書いた時刻。</returns>
    public static string FormatTime(TimeOnly time, string? pattern, CultureInfo culture)
    {
        string format = string.IsNullOrEmpty(pattern) ? DefaultTimePattern : pattern;
        try
        {
            return time.ToString(format, culture);
        }
        catch (FormatException)
        {
            return time.ToString(DefaultTimePattern, culture);
        }
    }
}
