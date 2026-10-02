// ============================================================
//  ApiReferenceSelector.cs — 文脈の語で切れ端に点を付け、予算の中で選ぶ
//
//  【点の付け方】
//  切れ端の点 = Σ（文脈の語ごと）語の重み × 語の珍しさ（IDF）× 一致の倍率
//    - 完全一致（大文字小文字は区別しない）: 見出しに出れば heading_match、本文なら body_match
//    - 接頭辞の一致: 完全一致の無い語と、書きかけの語（"Bind"）は、その語で始まるより長い語
//      （Binding・Bindable …）も見る。1 つの文脈の語につき切れ端ごとに最も強い一致 1 つだけを数え、
//      prefix_match を掛ける（Get で始まる API を山ほど持つ節が、それだけで勝たないように）
//
//  【選び方】
//  ① 常に入れる節（設定 always_include。見出しの文がその文字列で始まる節の全切れ端）を文書の順に、
//     予算に入る限り入れる
//  ② 残りは点の高い順（同点なら文書の順）に、予算に入るものを入れる。点が 0・min_score 未満・
//     「最も点の高い切れ端の点 × relative_min_score」未満の切れ端は予算が余っていても入れない
//     （関係の薄い節でトークンを使わない。ScreenStack に強く当たるファイルで、見出しに UI と
//     書いてあるだけの節が残りの予算を埋めないように）
//  ③ 選んだ切れ端は文書の順（見出しの番号順）に並べる（ApiReferenceSelection）
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// 切れ端の点付けと、予算の中での選択（状態を持たない）。
/// </summary>
public static class ApiReferenceSelector
{
    /// <summary>接頭辞 1 つから展開する語の最大の数（短い接頭辞で選択が重くならないように）。</summary>
    public const int MaxPrefixExpansions = 256;

    /// <summary>
    /// 常に入れる切れ端の印を作る（見出しの文が設定の文字列で始まる節の全切れ端）。
    /// </summary>
    /// <param name="index">索引。</param>
    /// <param name="settings">設定。</param>
    /// <param name="unmatched">どの節にも当たらなかった設定の文字列（見出しの変更で外れた合図）。</param>
    /// <returns>切れ端の通し番号ごとの印。</returns>
    public static bool[] AlwaysIncludeMask(ApiReferenceIndex index, ApiReferenceSettings settings, out List<string> unmatched)
    {
        var mask = new bool[index.Parts.Count];
        unmatched = new List<string>();
        foreach (var entry in settings.AlwaysInclude)
        {
            bool any = false;
            foreach (var part in index.Parts)
            {
                if (!part.HeadingText.StartsWith(entry, StringComparison.Ordinal)) continue;
                mask[part.Index] = true;
                any = true;
            }
            if (!any) unmatched.Add(entry);
        }
        return mask;
    }

    /// <summary>
    /// 文脈の語で全切れ端に点を付ける。
    /// </summary>
    /// <param name="index">索引。</param>
    /// <param name="context">文脈の語。</param>
    /// <param name="settings">設定（重み・接頭辞の最短の文字数）。</param>
    /// <returns>切れ端の通し番号ごとの点（0 以上）。</returns>
    public static double[] Score(ApiReferenceIndex index, CompletionContext context, ApiReferenceSettings settings)
    {
        var scores = new double[index.Parts.Count];
        var weights = settings.Weights;
        var prefixBest = new Dictionary<int, double>();

        foreach (var (word, weight) in context.Weights)
        {
            if (weight <= 0.0) continue;

            // ① 完全一致
            bool exact = index.TryGetPostings(word, out var postings);
            if (exact)
            {
                double idf = index.Idf(word);
                foreach (var posting in postings)
                    scores[posting.PartIndex] += weight * idf * MatchFactor(posting, weights);
            }

            // ② 接頭辞の一致（完全一致の無い語と書きかけの語だけ）
            bool isTyping = context.TypingWord is not null
                            && ApiIdentifierTokenizer.WordComparer.Equals(word, context.TypingWord);
            if ((exact && !isTyping) || word.Length < settings.MinPrefixLength || weights.PrefixMatch <= 0.0) continue;

            prefixBest.Clear();
            foreach (var longer in index.WordsWithPrefix(word, MaxPrefixExpansions))
            {
                double idf = index.Idf(longer);
                index.TryGetPostings(longer, out var longerPostings);
                foreach (var posting in longerPostings)
                {
                    // 切れ端ごとに最も強い一致 1 つだけを数える
                    double strength = idf * MatchFactor(posting, weights);
                    if (!prefixBest.TryGetValue(posting.PartIndex, out var best) || strength > best)
                        prefixBest[posting.PartIndex] = strength;
                }
            }
            foreach (var (partIndex, best) in prefixBest)
                scores[partIndex] += weight * weights.PrefixMatch * best;
        }
        return scores;
    }

    /// <summary>
    /// 予算の中で切れ端を選ぶ（常に入れる節 → 点の高い順）。
    /// </summary>
    /// <param name="index">索引。</param>
    /// <param name="alwaysMask">常に入れる切れ端の印（<see cref="AlwaysIncludeMask"/>）。</param>
    /// <param name="scores">切れ端ごとの点（<see cref="Score"/>）。</param>
    /// <param name="settings">設定（点の下限）。</param>
    /// <param name="budgetChars">予算（本文の最大文字数）。</param>
    /// <returns>選択。</returns>
    public static ApiReferenceSelection Select(
        ApiReferenceIndex index, IReadOnlyList<bool> alwaysMask, IReadOnlyList<double> scores,
        ApiReferenceSettings settings, int budgetChars)
    {
        var parts = index.Parts;
        var chosen = new bool[parts.Count];
        var always = new HashSet<int>();
        int used = 0;
        bool overflow = false;

        // ① 常に入れる節（文書の順。入りきらない切れ端は飛ばして次を試す）
        for (int i = 0; i < parts.Count; i++)
        {
            if (!alwaysMask[i]) continue;
            if (used + parts[i].Length > budgetChars) { overflow = true; continue; }
            chosen[i] = true;
            always.Add(i);
            used += parts[i].Length;
        }

        // ② 点の高い順（同点なら文書の順）。点の無い・下限未満の切れ端は入れない。
        //    下限 = max(min_score, 常に入れる節以外で最も高い点 × relative_min_score)
        double top = 0.0;
        for (int i = 0; i < parts.Count; i++)
            if (!alwaysMask[i]) top = Math.Max(top, scores[i]);
        double floor = Math.Max(settings.MinScore, top * settings.RelativeMinScore);
        var candidates = Enumerable.Range(0, parts.Count)
            .Where(i => !chosen[i] && !alwaysMask[i] && scores[i] > 0.0 && scores[i] >= floor)
            .OrderByDescending(i => scores[i])
            .ThenBy(i => i);
        foreach (var i in candidates)
        {
            if (used + parts[i].Length > budgetChars) continue;
            chosen[i] = true;
            used += parts[i].Length;
        }

        // ③ 文書の順に並べる
        var inOrder = new List<ApiReferencePart>();
        for (int i = 0; i < parts.Count; i++)
            if (chosen[i]) inOrder.Add(parts[i]);
        return new ApiReferenceSelection(inOrder, always, budgetChars, overflow);
    }

    /// <summary>一致の倍率（見出しか本文か）。</summary>
    private static double MatchFactor(ApiReferenceIndex.Posting posting, ApiReferenceWeights weights) =>
        posting.InHeading ? weights.HeadingMatch : weights.BodyMatch;
}
