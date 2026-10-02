// ============================================================
//  ApiReferenceIndex.cs — リファレンスの切れ端と「語 → 切れ端」の索引（1 回だけ作って使い回す）
//
//  【役割】
//  圧縮後のリファレンスを切れ端に分け（ApiReferenceSectionSplitter）、
//  語ごとに「どの切れ端に、見出しと本文のどちらで出るか」を引ける索引を作る。
//  補完は打鍵ごとに走るので、選ぶときの仕事は「文脈の語で索引を引いて足す」だけにする。
//
//  【語の珍しさ（IDF）】
//  多くの切れ端に出る語（GameObject・Vector3・Get …）は手がかりとして弱く、
//  少ない切れ端にしか出ない語（L10n・ScreenStack …）は強い。
//  点付けではこの珍しさ ln(1 + 切れ端の数 / その語が出る切れ端の数) を掛ける。
//
//  【接頭辞の検索】
//  語を大文字小文字を区別せずに整列した配列を持ち、二分探索で「この接頭辞で始まる語」を引く
//  （"Bind" と書き始めたら Binding・Bindable の節を候補にするため）。
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// リファレンスの切れ端と語の索引。生成は <see cref="FromMarkdown"/> か <see cref="Build"/>。
/// 作った後は変わらない（複数のスレッドから読んでよい）。
/// </summary>
public sealed class ApiReferenceIndex
{
    /// <summary>語が出る場所 1 つ（どの切れ端の、見出しか本文か）。</summary>
    /// <param name="PartIndex">切れ端の通し番号。</param>
    /// <param name="InHeading">見出しに出るか（見出しと本文の両方に出るなら true）。</param>
    public readonly record struct Posting(int PartIndex, bool InHeading);

    /// <summary>珍しさの式 ln(足す数 + 切れ端の数 / 出る切れ端の数) の足す数（どこにでも出る語でも 0 にしない）。</summary>
    private const double IdfSmoothing = 1.0;

    /// <summary>文書の順の切れ端。</summary>
    public IReadOnlyList<ApiReferencePart> Parts { get; }

    /// <summary>圧縮後のリファレンスの全文（文脈を渡さない呼び出しは、この先頭から予算までを使う）。</summary>
    public string CompactText { get; }

    /// <summary>節の数（切れ端に分ける前の数）。</summary>
    public int SectionCount { get; }

    /// <summary>全切れ端を単独で並べたときの文字数の合計（切れ端の見出し・フェンスの補いを含む）。</summary>
    public int TotalPartChars { get; }

    /// <summary>語 → 出る場所（大文字小文字を区別しない）。</summary>
    private readonly Dictionary<string, Posting[]> _postings;

    /// <summary>索引の語を大文字小文字を区別せずに整列したもの（接頭辞の二分探索に使う）。</summary>
    private readonly string[] _sortedWords;

    /// <summary>索引を組み立てる（生成は <see cref="Build"/> から）。</summary>
    private ApiReferenceIndex(string compactText, IReadOnlyList<ApiReferencePart> parts)
    {
        CompactText = compactText;
        Parts = parts;
        SectionCount = parts.Count == 0 ? 0 : parts.Select(p => p.SectionIndex).Distinct().Count();
        TotalPartChars = parts.Sum(p => p.Length);

        // 語 → 出る場所。見出しと本文の両方に出る語は見出しとして 1 つにまとめる
        var builder = new Dictionary<string, Dictionary<int, bool>>(ApiIdentifierTokenizer.WordComparer);
        foreach (var part in parts)
        {
            foreach (var word in part.BodyWords) Note(builder, word, part.Index, inHeading: false);
            foreach (var word in part.HeadingWords) Note(builder, word, part.Index, inHeading: true);
        }
        _postings = new Dictionary<string, Posting[]>(ApiIdentifierTokenizer.WordComparer);
        foreach (var (word, places) in builder)
            _postings[word] = places.Select(kv => new Posting(kv.Key, kv.Value)).OrderBy(p => p.PartIndex).ToArray();

        _sortedWords = _postings.Keys.ToArray();
        Array.Sort(_sortedWords, ApiIdentifierTokenizer.WordComparer);
    }

    /// <summary>語の出る場所を記録する（見出しで出たことは本文より優先して残す）。</summary>
    private static void Note(Dictionary<string, Dictionary<int, bool>> builder, string word, int partIndex, bool inHeading)
    {
        if (!builder.TryGetValue(word, out var places))
        {
            places = new Dictionary<int, bool>();
            builder[word] = places;
        }
        places[partIndex] = inHeading || (places.TryGetValue(partIndex, out var already) && already);
    }

    // ── 生成 ─────────────────────────────────────────────────

    /// <summary>リファレンスの Markdown から作る（圧縮 → 切れ端 → 索引）。</summary>
    /// <param name="markdown">docs/scripting_api.md の全文。</param>
    /// <param name="maxPartChars">切れ端の単独の本文の上限。</param>
    /// <returns>索引。</returns>
    public static ApiReferenceIndex FromMarkdown(string markdown, int maxPartChars) =>
        Build(ApiReferenceCompactor.Compact(markdown), maxPartChars);

    /// <summary>圧縮済みのテキストから作る。</summary>
    /// <param name="compactText">圧縮後のテキスト。</param>
    /// <param name="maxPartChars">切れ端の単独の本文の上限。</param>
    /// <returns>索引。</returns>
    public static ApiReferenceIndex Build(string compactText, int maxPartChars) =>
        new(compactText ?? string.Empty, ApiReferenceSectionSplitter.Split(compactText ?? string.Empty, maxPartChars));

    // ── 引く ─────────────────────────────────────────────────

    /// <summary>索引に載っている語か（大文字小文字を区別しない）。</summary>
    /// <param name="word">語。</param>
    /// <returns>載っていれば true。</returns>
    public bool Contains(string word) => _postings.ContainsKey(word);

    /// <summary>語の出る場所を引く。</summary>
    /// <param name="word">語。</param>
    /// <param name="postings">出る場所（切れ端の番号順）。</param>
    /// <returns>載っていれば true。</returns>
    public bool TryGetPostings(string word, out IReadOnlyList<Posting> postings)
    {
        if (_postings.TryGetValue(word, out var found)) { postings = found; return true; }
        postings = Array.Empty<Posting>();
        return false;
    }

    /// <summary>
    /// 語の珍しさ ln(1 + 切れ端の数 / 出る切れ端の数)。索引に無い語は 0。
    /// </summary>
    /// <param name="word">語。</param>
    /// <returns>珍しさ（大きいほど少ない切れ端にしか出ない）。</returns>
    public double Idf(string word)
    {
        if (!_postings.TryGetValue(word, out var found) || found.Length == 0) return 0.0;
        return Math.Log(IdfSmoothing + (double)Parts.Count / found.Length);
    }

    /// <summary>
    /// この接頭辞で始まる、より長い語を引く（大文字小文字を区別しない。接頭辞そのものの語は含めない）。
    /// </summary>
    /// <param name="prefix">接頭辞。</param>
    /// <param name="maxCount">返す最大の数（多すぎる展開で選択が重くならないように）。</param>
    /// <returns>整列順の語。</returns>
    public IEnumerable<string> WordsWithPrefix(string prefix, int maxCount)
    {
        if (string.IsNullOrEmpty(prefix) || maxCount <= 0) yield break;

        // 整列順で prefix 以上になる最初の位置（下限）を二分探索で求める
        int found = Array.BinarySearch(_sortedWords, prefix, ApiIdentifierTokenizer.WordComparer);
        int start = found >= 0 ? found : ~found;

        int count = 0;
        for (int i = start; i < _sortedWords.Length && count < maxCount; i++)
        {
            var word = _sortedWords[i];
            if (!word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield break;
            if (word.Length == prefix.Length) continue;   // 接頭辞そのもの（完全一致）は別に扱う
            count++;
            yield return word;
        }
    }
}
