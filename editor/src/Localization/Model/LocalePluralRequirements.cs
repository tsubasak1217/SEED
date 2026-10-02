// ============================================================
//  LocalePluralRequirements.cs — 言語ごとに「書くべき複数形の形」を決める（純粋な計算）
//
//  【決め方】SEED.Localization の PluralRules（実行中に形を選ぶ表）で 0〜(SampleUpperBound-1) の数の形を選び、
//  出てきた形を「その言語が使う形」とする。規則の表を二重に持たないため、ここでは数を流すだけにする。
//    ja（OtherOnly）→ other / en（OneOther）→ one・other / ar（Arabic）→ zero・one・two・few・many・other
//  どの言語でも 0 のときに zero の子を先に探す約束（docs/localization.md §5 の ①）は「書いてよい」形なので
//  書くべき形には入れない（アラビア語は規則そのものが 0 を zero にするので入る）。
// ============================================================

using System.Collections.Concurrent;
using System.Collections.Generic;
using SEED.Localization;

namespace SEEDEditor.Localization.Model;

/// <summary>言語ごとに書くべき複数形の形。</summary>
public static class LocalePluralRequirements
{
    /// <summary>
    /// 形を調べる数の上限（これ未満の 0 以上の整数を流す）。どの規則も下 2 桁までで形が決まり、
    /// アラビア語の other（100〜102）まで含むよう 200 にする。
    /// </summary>
    private const long SampleUpperBound = 200;

    /// <summary>規則 → 使う形（調べた結果の控え。表は固定なので捨てない）。</summary>
    private static readonly ConcurrentDictionary<PluralRuleKind, IReadOnlySet<PluralCategory>> CategoriesByRule = new();

    /// <summary>その言語が使う複数形の形。</summary>
    /// <param name="code">言語のコード。</param>
    /// <returns>使う形。</returns>
    public static IReadOnlySet<PluralCategory> CategoriesFor(string code) =>
        CategoriesByRule.GetOrAdd(PluralRules.RuleFor(code), Sample);

    /// <summary>その言語がその形を使うか（書くべきか）。</summary>
    /// <param name="code">言語のコード。</param>
    /// <param name="category">形。</param>
    /// <returns>使うなら true。</returns>
    public static bool Uses(string code, PluralCategory category) => CategoriesFor(code).Contains(category);

    /// <summary>規則に数を流して出てきた形を集める。</summary>
    private static IReadOnlySet<PluralCategory> Sample(PluralRuleKind rule)
    {
        var categories = new HashSet<PluralCategory>();
        for (long n = 0; n < SampleUpperBound; n++) categories.Add(PluralRules.Select(rule, n));
        return categories;
    }
}
