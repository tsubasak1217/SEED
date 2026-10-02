using System;
using System.Collections.Generic;

namespace SEED.Localization;

// ============================================================
//  PluralRules.cs — 言語のコード → 複数形の規則（CLDR の整数の規則の簡略版の表）と、数 → 形の種類（純粋な計算）
//
//  【表の引き方】言語のコードそのもの（"pt-PT"）→ 言語の部分（"pt"）の順に表を引き、無ければ one / other（英語と同じ）。
//  地域で規則が変わる言語（ポルトガル語: ブラジル = 0 と 1 が one・ポルトガル = 1 だけ one）は、地域つきのコードを表に書く。
//  【数の扱い】CLDR の約束どおり、負の数は絶対値で判定する（-1 は 1 と同じ形）。小数は扱わない（long だけ）。
//  【zero の扱い】ここでは CLDR の規則だけを返す（アラビア語以外は 0 で zero を返さない）。
//  「どの言語でも 0 なら zero の子を先に探す」は表の引き方の約束で、LocaleTable.TryGetPlural が受け持つ。
// ============================================================

/// <summary>言語の複数形の規則。</summary>
public static class PluralRules
{
    /// <summary>表に無い言語の規則（英語と同じ one / other）。</summary>
    public const PluralRuleKind DefaultRule = PluralRuleKind.OneOther;

    /// <summary>10 進の 1 桁（末尾の数字を取る）。</summary>
    private const ulong Radix = 10;
    /// <summary>下 2 桁を取る割る数。</summary>
    private const ulong Hundred = 100;
    /// <summary>数 0（zero の数）。</summary>
    private const ulong CountZero = 0;
    /// <summary>数 1（one の数）。</summary>
    private const ulong CountOne = 1;
    /// <summary>数 2（two の数）。</summary>
    private const ulong CountTwo = 2;
    /// <summary>東スラブ語で末尾が 1 でも one にしない下 2 桁（11）。</summary>
    private const ulong EastSlavicOneException = 11;

    /// <summary>
    /// 言語のコード（言語の部分か、地域つきのコード）→ 規則。大文字小文字を区別しない。
    /// <b>規則を足すときはここに 1 行足す</b>（地域で規則が違う言語は地域つきのコードの行を足す）。
    /// </summary>
    private static readonly Dictionary<string, PluralRuleKind> RuleByLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── other だけ（数で形が変わらない言語）──
        ["ja"] = PluralRuleKind.OtherOnly,
        ["zh"] = PluralRuleKind.OtherOnly,
        ["ko"] = PluralRuleKind.OtherOnly,
        ["yue"] = PluralRuleKind.OtherOnly,
        ["th"] = PluralRuleKind.OtherOnly,
        ["vi"] = PluralRuleKind.OtherOnly,
        ["id"] = PluralRuleKind.OtherOnly,
        ["ms"] = PluralRuleKind.OtherOnly,
        ["lo"] = PluralRuleKind.OtherOnly,
        ["my"] = PluralRuleKind.OtherOnly,
        ["km"] = PluralRuleKind.OtherOnly,

        // ── 0 と 1 が one ──
        ["fr"] = PluralRuleKind.ZeroOneAsOne,
        ["pt"] = PluralRuleKind.ZeroOneAsOne,     // CLDR の "pt" はブラジル
        ["hi"] = PluralRuleKind.ZeroOneAsOne,
        ["bn"] = PluralRuleKind.ZeroOneAsOne,
        ["fa"] = PluralRuleKind.ZeroOneAsOne,
        ["gu"] = PluralRuleKind.ZeroOneAsOne,
        ["kn"] = PluralRuleKind.ZeroOneAsOne,
        ["zu"] = PluralRuleKind.ZeroOneAsOne,
        ["am"] = PluralRuleKind.ZeroOneAsOne,
        ["hy"] = PluralRuleKind.ZeroOneAsOne,

        // ── 地域で規則が変わる言語の地域つきの行 ──
        ["pt-PT"] = PluralRuleKind.OneOther,      // ポルトガルのポルトガル語は 1 だけ one

        // ── 1・2・other ──
        ["he"] = PluralRuleKind.OneTwoOther,
        ["iw"] = PluralRuleKind.OneTwoOther,      // ヘブライ語の古いコード

        // ── スラブ語 ──
        ["ru"] = PluralRuleKind.EastSlavic,
        ["uk"] = PluralRuleKind.EastSlavic,
        ["be"] = PluralRuleKind.EastSlavic,
        ["pl"] = PluralRuleKind.Polish,
        ["cs"] = PluralRuleKind.CzechSlovak,
        ["sk"] = PluralRuleKind.CzechSlovak,

        // ── アラビア語 ──
        ["ar"] = PluralRuleKind.Arabic,

        // 英語・ドイツ語・オランダ語・北欧語・スペイン語・イタリア語・ギリシャ語・ハンガリー語・トルコ語などは
        // 既定（DefaultRule = one / other）なので書かない。
    };

    /// <summary>言語の規則を引く（コードそのもの → 言語の部分 → 既定）。</summary>
    /// <param name="languageCode">言語のコード（"en"・"pt-BR" など）。</param>
    /// <returns>規則。</returns>
    public static PluralRuleKind RuleFor(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return DefaultRule;
        string code = LocaleIndex.Normalize(languageCode);
        if (RuleByLanguage.TryGetValue(code, out var exact)) return exact;
        return RuleByLanguage.TryGetValue(LocaleIndex.BaseLanguage(code), out var byBase) ? byBase : DefaultRule;
    }

    /// <summary>言語と数から形の種類を決める。</summary>
    /// <param name="languageCode">言語のコード。</param>
    /// <param name="count">数（負の数は絶対値で判定する）。</param>
    /// <returns>形の種類。</returns>
    public static PluralCategory Select(string? languageCode, long count) => Select(RuleFor(languageCode), count);

    /// <summary>規則と数から形の種類を決める。</summary>
    /// <param name="rule">規則。</param>
    /// <param name="count">数（負の数は絶対値で判定する）。</param>
    /// <returns>形の種類。</returns>
    public static PluralCategory Select(PluralRuleKind rule, long count)
    {
        ulong n = Magnitude(count);
        ulong lastDigit = n % Radix;
        ulong lastTwo = n % Hundred;
        switch (rule)
        {
            case PluralRuleKind.OtherOnly:
                return PluralCategory.Other;

            case PluralRuleKind.OneOther:
                return n == CountOne ? PluralCategory.One : PluralCategory.Other;

            case PluralRuleKind.ZeroOneAsOne:
                return n <= CountOne ? PluralCategory.One : PluralCategory.Other;

            case PluralRuleKind.OneTwoOther:
                return n == CountOne ? PluralCategory.One : n == CountTwo ? PluralCategory.Two : PluralCategory.Other;

            case PluralRuleKind.EastSlavic:
                // one: 末尾 1（11 を除く）・few: 末尾 2〜4（12〜14 を除く）・many: そのほか（末尾 0・5〜9・11〜14）
                if (lastDigit == CountOne && lastTwo != EastSlavicOneException) return PluralCategory.One;
                if (IsTwoToFour(lastDigit) && !IsTwelveToFourteen(lastTwo)) return PluralCategory.Few;
                return PluralCategory.Many;

            case PluralRuleKind.Polish:
                // one: 1 だけ・few: 末尾 2〜4（12〜14 を除く）・many: そのほか（21・22 の 21 は many）
                if (n == CountOne) return PluralCategory.One;
                if (IsTwoToFour(lastDigit) && !IsTwelveToFourteen(lastTwo)) return PluralCategory.Few;
                return PluralCategory.Many;

            case PluralRuleKind.CzechSlovak:
                // one: 1・few: 2〜4・other: そのほか（many は小数だけなので整数では出ない）
                if (n == CountOne) return PluralCategory.One;
                return IsTwoToFour(n) ? PluralCategory.Few : PluralCategory.Other;

            case PluralRuleKind.Arabic:
                // zero: 0・one: 1・two: 2・few: 下 2 桁 3〜10・many: 下 2 桁 11〜99・other: そのほか（100・101・102 …）
                if (n == CountZero) return PluralCategory.Zero;
                if (n == CountOne) return PluralCategory.One;
                if (n == CountTwo) return PluralCategory.Two;
                if (lastTwo >= ArabicFewMin && lastTwo <= ArabicFewMax) return PluralCategory.Few;
                if (lastTwo >= ArabicManyMin) return PluralCategory.Many;
                return PluralCategory.Other;

            default:
                return PluralCategory.Other;
        }
    }

    /// <summary>アラビア語の few の下 2 桁の下限。</summary>
    private const ulong ArabicFewMin = 3;
    /// <summary>アラビア語の few の下 2 桁の上限。</summary>
    private const ulong ArabicFewMax = 10;
    /// <summary>アラビア語の many の下 2 桁の下限（上限は 99）。</summary>
    private const ulong ArabicManyMin = 11;
    /// <summary>スラブ語の few の末尾の下限。</summary>
    private const ulong FewMin = 2;
    /// <summary>スラブ語の few の末尾の上限。</summary>
    private const ulong FewMax = 4;
    /// <summary>スラブ語の few から外す下 2 桁の下限（12）。</summary>
    private const ulong TeenExceptionMin = 12;
    /// <summary>スラブ語の few から外す下 2 桁の上限（14）。</summary>
    private const ulong TeenExceptionMax = 14;

    /// <summary>2〜4 か。</summary>
    private static bool IsTwoToFour(ulong value) => value >= FewMin && value <= FewMax;

    /// <summary>12〜14 か。</summary>
    private static bool IsTwelveToFourteen(ulong value) => value >= TeenExceptionMin && value <= TeenExceptionMax;

    /// <summary>絶対値（long.MinValue でもあふれない）。</summary>
    private static ulong Magnitude(long value) => value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
}
