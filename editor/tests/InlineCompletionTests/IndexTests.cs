using System;
using System.Linq;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;
using SpriteRigTests;

namespace InlineCompletionTests;

/// <summary>
/// 節と切れ端への分割（<see cref="ApiReferenceSectionSplitter"/>）・語の索引（<see cref="ApiReferenceIndex"/>）の テスト。
/// </summary>
public static class IndexTests
{
    /// <summary>分割を確かめるときの切れ端の上限（長い節の見本が必ず分かれる大きさ）。</summary>
    private const int SmallPartChars = 700;

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("節: # / ## / ### で分かれ、#### は親の節に残る", SplitsByHeadingLevels);
        harness.Add("節: 名前は §番号・§親/短い題・前書きは §0", LabelsAndNumbers);
        harness.Add("語: コード・表・見出しの識別子を拾い、キーワードと 1 文字の語は捨てる", ExtractsIdentifiers);
        harness.Add("語: 見出しに出る語は見出しの一致として記録される", HeadingPostings);
        harness.Add("語: 珍しい語ほど珍しさ（IDF）が大きい", IdfOrdersRareWordsHigher);
        harness.Add("語: 接頭辞で始まる語を整列順に引ける（完全一致は除く）", PrefixLookup);
        harness.Add("切れ端: 長い節は上限内に分かれ、続けて並べると元の節と同じ", LongSectionSplitsLosslessly);
        harness.Add("切れ端: 単独の本文はフェンスが閉じ、見出しと表の頭を持つ", PartsAreSelfContained);
        harness.Add("切れ端: 上限内の節は分けない", ShortSectionsStayWhole);
    }

    /// <summary>見出しの段ごとの分かれ方。</summary>
    private static void SplitsByHeadingLevels()
    {
        var index = Fixture.Index();
        var labels = index.Parts.Select(p => p.Label).ToArray();
        string[] expected =
        {
            "§0", "§1", "§1/インスペクタ公開の属性", "§2", "§3", "§6.5", "§7", "§7/Transform",
            "§7/利用可能なコンポーネント一覧", "§7.18", "§7.21", "§7.22",
        };
        Check.Equal(string.Join(" ", expected), string.Join(" ", labels), "節の並び");
        Check.True(Fixture.Part(index, "§7/Transform").Body.Contains("#### レシピ: 回す", StringComparison.Ordinal),
            "#### のレシピは §7/Transform の中");
        Check.Equal(index.Parts.Count, index.SectionCount, "上限内なので節と切れ端の数が同じ");
    }

    /// <summary>番号と名前。</summary>
    private static void LabelsAndNumbers()
    {
        var index = Fixture.Index();
        Check.Equal("7.18", Fixture.Part(index, "§7.18").Number, "## 7.18 の番号");
        Check.Equal("1", Fixture.Part(index, "§1").Number, "## 1. の番号（末尾の . は落とす）");
        Check.Equal("6.5", Fixture.Part(index, "§6.5").Number, "## 6.5 の番号");
        Check.Equal<string?>(null, Fixture.Part(index, "§7/Transform").Number, "### には番号が無い");
        Check.Equal("7.18 画面の組み立て（SEED.UI：画面のスタック・ダイアログ）", Fixture.Part(index, "§7.18").HeadingText, "見出しの文");
        Check.Equal("Transform", ApiReferenceSectionSplitter.ShortTitle("Transform（3D 位置・回転・スケール）", null), "短い題は括弧の前まで");
        Check.Equal("Animator", ApiReferenceSectionSplitter.ShortTitle("Animator (キーフレーム再生)", null), "半角の括弧の前でも切る");
        Check.Equal("スクリプトの基本形", ApiReferenceSectionSplitter.ShortTitle("1. スクリプトの基本形", "1"), "番号と . は落とす");
        Check.True(ApiReferenceSectionSplitter.ShortTitle(new string('長', 40), null).Length == ApiReferenceSectionSplitter.MaxShortTitleChars + 1,
            "長い題は上限で切って … を付ける");
    }

    /// <summary>識別子の抽出。</summary>
    private static void ExtractsIdentifiers()
    {
        var index = Fixture.Index();
        var screen = Fixture.Part(index, "§7.18");
        foreach (var word in new[] { "ScreenStack", "UiWidget", "Push", "Pop", "Dialog", "DialogOptions", "ScreenHandle" })
            Check.True(screen.BodyWords.Contains(word), $"§7.18 の本文の語に {word}");
        var l10n = Fixture.Part(index, "§7.21");
        Check.True(l10n.BodyWords.Contains("L10n") && l10n.BodyWords.Contains("Get") && l10n.BodyWords.Contains("SetLanguage"),
            "L10n.Get は L10n と Get の 2 語");
        Check.True(Fixture.Part(index, "§7.22").BodyWords.Contains("Bind"), "Bind.Text の Bind");
        Check.True(Fixture.Part(index, "§7/利用可能なコンポーネント一覧").BodyWords.Contains("Transform"), "表の中の識別子も拾う");
        Check.True(!screen.BodyWords.Contains("var") && !screen.BodyWords.Contains("new"), "キーワードは捨てる");
        Check.True(!Fixture.Part(index, "§3").BodyWords.Contains("float"), "型のキーワードも捨てる");
        Check.True(!Fixture.Part(index, "§7.22").BodyWords.Contains("n"), "1 文字の語は捨てる");
        Check.True(!index.Contains("MaintainerOnlyApi"), "メンテナ向けの語は索引に無い");
        Check.True(index.Contains("screenstack"), "大文字小文字を区別せずに引ける");
    }

    /// <summary>見出しの一致。</summary>
    private static void HeadingPostings()
    {
        var index = Fixture.Index();
        Check.True(index.TryGetPostings("Localization", out var postings), "Localization が索引にある");
        var l10n = Fixture.Part(index, "§7.21");
        Check.True(postings.Any(p => p.PartIndex == l10n.Index && p.InHeading), "§7.21 では見出しの一致（本文にもあるが見出しを優先）");
        index.TryGetPostings("Transform", out var transform);
        Check.True(transform.Any(p => p.PartIndex == Fixture.Part(index, "§7/Transform").Index && p.InHeading), "§7/Transform は見出し");
        Check.True(transform.Any(p => p.PartIndex == Fixture.Part(index, "§7").Index && !p.InHeading), "§7 は本文");
    }

    /// <summary>珍しさの順。</summary>
    private static void IdfOrdersRareWordsHigher()
    {
        var index = Fixture.Index();
        Check.True(index.Idf("L10n") > index.Idf("Transform"), "1 か所の L10n は 3 か所の Transform より珍しい");
        Check.True(index.Idf("GameObject") > index.Idf("Transform"),
            "2 か所の GameObject は 3 か所の Transform（transform も同じ語）より珍しい");
        Check.Equal(0.0, index.Idf("NoSuchWord"), "索引に無い語は 0");
    }

    /// <summary>接頭辞の検索。</summary>
    private static void PrefixLookup()
    {
        var index = Fixture.Index();
        var bin = index.WordsWithPrefix("bin", ApiReferenceSelector.MaxPrefixExpansions).ToArray();
        Check.Equal("Bind Bindable Binding", string.Join(" ", bin), "bin で始まる語（大文字小文字を区別しない・整列順）");
        var bind = index.WordsWithPrefix("Bind", ApiReferenceSelector.MaxPrefixExpansions).ToArray();
        Check.Equal("Bindable Binding", string.Join(" ", bind), "完全一致の Bind は含めない");
        Check.Equal(1, index.WordsWithPrefix("bin", 1).Count(), "最大の数で止まる");
        Check.Equal(0, index.WordsWithPrefix("Zzz", ApiReferenceSelector.MaxPrefixExpansions).Count(), "当たらなければ空");
    }

    /// <summary>長い節を分けても、続けて並べると元の節と同じ本文になる。</summary>
    private static void LongSectionSplitsLosslessly()
    {
        var compact = ApiReferenceCompactor.Compact(Fixture.LongSectionMarkdown());
        var whole = ApiReferenceIndex.Build(compact, ApiReferenceSettings.MaxBudgetChars);
        var split = ApiReferenceIndex.Build(compact, SmallPartChars);

        var longWhole = whole.Parts.Single(p => p.Number == "5");
        var longParts = split.Parts.Where(p => p.Number == "5").ToList();
        Check.True(longParts.Count > 2, $"長い節が 3 つ以上に分かれる（{longParts.Count}）");
        foreach (var part in longParts)
            Check.True(part.Length <= SmallPartChars, $"{part.Label} は上限 {SmallPartChars} 以内（{part.Length}）");
        Check.Equal(longWhole.Text, ApiReferenceSelection.Render(longParts), "続けて並べると元の節と同じ");
        Check.Equal(compact, ApiReferenceSelection.Render(split.Parts), "全切れ端を並べると圧縮後の全文と同じ");
        Check.True(longParts.All(p => p.PartCount == longParts.Count), "どの切れ端も切れ端の数を知っている");
        Check.Equal("§5[2/" + longParts.Count + "]", longParts[1].Label, "切れ端の名前");
    }

    /// <summary>単独の切れ端は、見出し・閉じたフェンス・表の頭を持つ。</summary>
    private static void PartsAreSelfContained()
    {
        var split = ApiReferenceIndex.Build(ApiReferenceCompactor.Compact(Fixture.LongSectionMarkdown()), SmallPartChars);
        var longParts = split.Parts.Where(p => p.Number == "5").ToList();
        bool sawReopenedFence = false, sawTableHead = false;
        foreach (var part in longParts)
        {
            Check.True(part.Text.StartsWith(part.HeadingLine + "\n", StringComparison.Ordinal), $"{part.Label} は見出しで始まる");
            int fences = part.Text.Split('\n').Count(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));
            Check.True(fences % 2 == 0, $"{part.Label} のフェンスは閉じている（{fences} 本）");
            if (part.Open.StartsWith("```csharp", StringComparison.Ordinal)) sawReopenedFence = true;
            if (part.Open.StartsWith("| トークン", StringComparison.Ordinal)) sawTableHead = true;
        }
        Check.True(sawReopenedFence, "コードの途中から始まる切れ端はフェンスを開き直す");
        Check.True(sawTableHead, "表の途中から始まる切れ端は表の頭を持つ");
        Check.True(longParts.Any(p => p.Close.Length > 0), "コードの途中で終わる切れ端はフェンスを閉じる");
        Check.True(longParts.Skip(1).All(p => p.HeadingWords.SetEquals(longParts[0].HeadingWords)), "切れ端はすべて節の見出しの語を持つ");
    }

    /// <summary>上限内の節は 1 つのまま。</summary>
    private static void ShortSectionsStayWhole()
    {
        var split = ApiReferenceIndex.Build(ApiReferenceCompactor.Compact(Fixture.LongSectionMarkdown()), SmallPartChars);
        var next = split.Parts.Single(p => p.Number == "6");
        Check.Equal(1, next.PartCount, "短い §6 は分けない");
        Check.Equal(string.Empty, next.Open + next.Close, "補いが無い");
    }
}
