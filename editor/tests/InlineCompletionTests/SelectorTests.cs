using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;
using SpriteRigTests;

namespace InlineCompletionTests;

/// <summary>
/// 点付けと予算内の選択（<see cref="ApiReferenceSelector"/>・<see cref="ContextualApiReference"/>）のテスト。
/// 見本のリファレンス（<see cref="Fixture.Markdown"/>）で規則だけを確かめる。
/// </summary>
public static class SelectorTests
{
    /// <summary>予算を総当たりで確かめるときの刻み。</summary>
    private const int BudgetStep = 37;

    /// <summary>分割の確認に使う切れ端の上限。</summary>
    private const int SmallPartChars = 700;

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("選択: ScreenStack を書いているファイルで §7.18 が入り §7.21 は落ちる", ScreenStackFile);
        harness.Add("選択: L10n を書いているファイルで §7.21 が入る", LocalizationFile);
        harness.Add("選択: Bind と書きかけると §7.22 が入る（完全一致＋書きかけ）", BindTyping);
        harness.Add("選択: Bin まで書くと接頭辞の一致で §7.22 が入る", BinPrefix);
        harness.Add("選択: 常に入れる節は手がかりの無いファイルでも必ず入り、それ以外は入らない", AlwaysIncludedOnly);
        harness.Add("選択: どの予算でも本文は予算を超えない", NeverExceedsBudget);
        harness.Add("選択: 選んだ節は元の順（見出しの番号順）に並ぶ", KeepsDocumentOrder);
        harness.Add("選択: 予算が基礎の節より小さいと入る分だけ入れて印を立てる", AlwaysIncludedOverflow);
        harness.Add("選択: 予算が 1 節分しか無ければ最も点の高い節を選ぶ", PicksHighestScoreFirst);
        harness.Add("選択: 点の下限を上げると文脈の節は入らない", MinScoreGate);
        harness.Add("選択: 予算 0 は空・文脈なしの Head は先頭から予算まで", ZeroBudgetAndHead);
        harness.Add("選択: 当たらない always_include は警告になる", UnmatchedAlwaysIncludeWarns);
        harness.Add("選択: 同じ節の続いた切れ端は名前をまとめる", LabelsCompressRuns);
        harness.Add("選択: 切れ端に分けた節も関係する切れ端だけが入る", SelectsOnlyRelevantParts);
    }

    /// <summary>見本のリファレンスの窓口（設定は既定。必要なら上書き）。</summary>
    private static ContextualApiReference Reference(ApiReferenceSettings? settings = null) =>
        ContextualApiReference.FromMarkdown(Fixture.Markdown, settings ?? ApiReferenceSettings.BuiltIn());

    /// <summary>見本のファイルで選ぶ（予算は全切れ端が入る大きさ＝点の下限と順位だけで決まる）。</summary>
    private static ApiReferenceSelection SelectAll(ContextualApiReference reference, string sample)
    {
        var (text, caret) = Fixture.Split(sample);
        return reference.Select(text, caret, reference.Index.TotalPartChars);
    }

    /// <summary>ScreenStack の画面。</summary>
    private static void ScreenStackFile()
    {
        var selection = SelectAll(Reference(), Fixture.ScreenStackFile);
        Check.True(Fixture.Includes(selection, "§7.18"), "§7.18 が入る: " + selection.DescribeLabels());
        Check.True(!Fixture.Includes(selection, "§7.21"), "§7.21 は入らない: " + selection.DescribeLabels());
        Check.True(!Fixture.Includes(selection, "§7.22"), "§7.22 も入らない: " + selection.DescribeLabels());
    }

    /// <summary>L10n。</summary>
    private static void LocalizationFile()
    {
        var selection = SelectAll(Reference(), Fixture.LocalizationFile);
        Check.True(Fixture.Includes(selection, "§7.21"), "§7.21 が入る: " + selection.DescribeLabels());
        Check.True(!Fixture.Includes(selection, "§7.18"), "§7.18 は入らない: " + selection.DescribeLabels());
    }

    /// <summary>Bind の書きかけ。</summary>
    private static void BindTyping()
    {
        var selection = SelectAll(Reference(), Fixture.BindTypingFile);
        Check.True(Fixture.Includes(selection, "§7.22"), "§7.22 が入る: " + selection.DescribeLabels());
    }

    /// <summary>Bin の接頭辞。</summary>
    private static void BinPrefix()
    {
        var reference = Reference();
        Check.True(!reference.Index.Contains("Bin"), "Bin そのものは索引に無い（接頭辞の一致だけが手がかり）");
        var (text, caret) = Fixture.Split(Fixture.BinPrefixFile);
        var scores = reference.ScoreFor(text, caret);
        var binding = Fixture.Part(reference.Index, "§7.22");
        Check.True(scores[binding.Index] > 0, "§7.22 に点が付く");
        var selection = SelectAll(reference, Fixture.BinPrefixFile);
        Check.True(Fixture.Includes(selection, "§7.22"), "§7.22 が入る: " + selection.DescribeLabels());
        Check.True(scores[binding.Index] >= scores.Where((_, i) => i != binding.Index).Max(),
            "見出しにも Binding がある §7.22 が最も点が高い");
    }

    /// <summary>手がかりの無いファイル。</summary>
    private static void AlwaysIncludedOnly()
    {
        var reference = Reference();
        var selection = SelectAll(reference, Fixture.PlainFile);
        var expected = new[] { "§0", "§1", "§2", "§7", "§7/利用可能なコンポーネント一覧" };
        Check.Equal(string.Join(" ", expected), selection.DescribeLabels(), "常に入れる節だけ");
        Check.Equal(reference.AlwaysIncludedChars, selection.Length, "基礎の節の文字数どおり");
        Check.True(selection.Text.Contains("SEED は **Unity ではありません**", StringComparison.Ordinal), "前書きの重要注記が入る");

        foreach (var sample in new[] { Fixture.ScreenStackFile, Fixture.LocalizationFile, Fixture.BindTypingFile })
        {
            var withContext = SelectAll(reference, sample);
            foreach (var label in expected)
                Check.True(Fixture.Includes(withContext, label), $"文脈があっても {label} は入る");
        }
    }

    /// <summary>予算を超えない。</summary>
    private static void NeverExceedsBudget()
    {
        var reference = Reference();
        int max = reference.Index.TotalPartChars + BudgetStep;
        foreach (var sample in new[] { Fixture.ScreenStackFile, Fixture.LocalizationFile, Fixture.BindTypingFile, Fixture.BinPrefixFile, Fixture.PlainFile })
        {
            var (text, caret) = Fixture.Split(sample);
            for (int budget = 0; budget <= max; budget += BudgetStep)
            {
                var selection = reference.Select(text, caret, budget);
                Check.True(selection.Length <= budget, $"予算 {budget} を超えない（{selection.Length}）");
                Check.True(selection.Parts.Sum(p => p.Length) <= budget, "単独の文字数の和も予算以内");
            }
        }
    }

    /// <summary>文書の順。</summary>
    private static void KeepsDocumentOrder()
    {
        var reference = Reference();
        var selection = SelectAll(reference, Fixture.BindTypingFile);
        for (int i = 1; i < selection.Parts.Count; i++)
            Check.True(selection.Parts[i - 1].Index < selection.Parts[i].Index, "切れ端の番号が増える順");
        int last = -1;
        foreach (var part in selection.Parts)
        {
            int at = selection.Text.IndexOf(part.HeadingLine, StringComparison.Ordinal);
            Check.True(at > last, $"{part.Label} の見出しは前の節より後ろ");
            last = at;
        }
    }

    /// <summary>基礎の節が入りきらない予算。</summary>
    private static void AlwaysIncludedOverflow()
    {
        var reference = Reference();
        var (text, caret) = Fixture.Split(Fixture.ScreenStackFile);
        int budget = reference.AlwaysIncludedChars / 2;
        var selection = reference.Select(text, caret, budget);
        Check.True(selection.AlwaysIncludedOverflow, "入りきらない印が立つ");
        Check.True(selection.Length <= budget, "それでも予算は超えない");
        Check.True(selection.Parts.Count > 0, "入る分は入れる");
        var full = reference.Select(text, caret, reference.Index.TotalPartChars);
        Check.True(!full.AlwaysIncludedOverflow, "十分な予算なら印は立たない");
    }

    /// <summary>予算が 1 節分しか無いとき。</summary>
    private static void PicksHighestScoreFirst()
    {
        var reference = Reference();
        var screen = Fixture.Part(reference.Index, "§7.18");
        var (text, caret) = Fixture.Split(Fixture.ScreenStackFile);
        var selection = reference.Select(text, caret, reference.AlwaysIncludedChars + screen.Length);
        var extra = selection.Parts.Where(p => !reference.IsAlwaysIncluded(p)).Select(p => p.Label).ToArray();
        Check.Equal("§7.18", string.Join(" ", extra), "基礎の節のほかは §7.18 だけ");
    }

    /// <summary>点の下限。</summary>
    private static void MinScoreGate()
    {
        var strict = Reference(ApiReferenceSettings.Create(minScore: 1e9));
        var selection = SelectAll(strict, Fixture.ScreenStackFile);
        Check.True(selection.Parts.All(strict.IsAlwaysIncluded), "下限が高いと基礎の節だけ: " + selection.DescribeLabels());
        var loose = Reference(ApiReferenceSettings.Create(minScore: 0.0));
        var looseSelection = SelectAll(loose, Fixture.PlainFile);
        Check.True(looseSelection.Parts.All(loose.IsAlwaysIncluded), "下限 0 でも点が 0 の節は入らない");
    }

    /// <summary>予算 0 と Head。</summary>
    private static void ZeroBudgetAndHead()
    {
        var reference = Reference();
        var (text, caret) = Fixture.Split(Fixture.ScreenStackFile);
        var zero = reference.Select(text, caret, 0);
        Check.Equal(0, zero.Length, "予算 0 は空");
        Check.Equal(0, zero.Parts.Count, "切れ端なし");
        Check.Equal(0, reference.Select(text, caret, -10).Length, "負の予算も 0 に丸める");
        const int headBudget = 100;
        Check.Equal(reference.Index.CompactText[..headBudget], reference.Head(headBudget), "Head は先頭から予算まで");
        Check.Equal(reference.Index.CompactText, reference.Head(int.MaxValue), "予算が全文より大きければ全文");
    }

    /// <summary>当たらない always_include。</summary>
    private static void UnmatchedAlwaysIncludeWarns()
    {
        var reference = Reference(ApiReferenceSettings.Create(alwaysInclude: new[] { "1. スクリプトの基本形", "99. 無い節" }));
        Check.Equal(1, reference.Warnings.Count, "警告 1 件");
        Check.True(reference.Warnings[0].Contains("99. 無い節", StringComparison.Ordinal), "当たらない文字列を名指しする");
        Check.True(reference.IsAlwaysIncluded(Fixture.Part(reference.Index, "§1")), "当たる方は効く");
        var prefixOnly = Reference(ApiReferenceSettings.Create(alwaysInclude: new[] { "7." }));
        Check.True(prefixOnly.IsAlwaysIncluded(Fixture.Part(prefixOnly.Index, "§7.18")),
            "見出しの文の先頭一致なので \"7.\" は §7.18 にも当たる（設定では \"7. GameObject\" のように題まで書く）");
    }

    /// <summary>名前のまとめ方。</summary>
    private static void LabelsCompressRuns()
    {
        var split = ApiReferenceIndex.Build(ApiReferenceCompactor.Compact(Fixture.LongSectionMarkdown()), SmallPartChars);
        var parts = split.Parts.Where(p => p.Number == "5").ToList();
        int n = parts.Count;
        Check.True(n >= 3, "3 つ以上に分かれている");
        var none = new HashSet<int>();
        Check.Equal("§5", new ApiReferenceSelection(parts, none, int.MaxValue, false).DescribeLabels(), "全部なら節の名前だけ");
        Check.Equal($"§5[1-2/{n}]", new ApiReferenceSelection(parts.Take(2).ToList(), none, int.MaxValue, false).DescribeLabels(), "続いた切れ端はまとめる");
        Check.Equal($"§5[1/{n}] §5[3/{n}]", new ApiReferenceSelection(new[] { parts[0], parts[2] }, none, int.MaxValue, false).DescribeLabels(),
            "飛んだ切れ端は別々");
        var merged = new ApiReferenceSelection(parts.Take(2).ToList(), none, int.MaxValue, false);
        Check.True(merged.Length < parts[0].Length + parts[1].Length, "続けて並べると見出しとフェンスの補いの分だけ短い");
    }

    /// <summary>切れ端に分けた節から関係する切れ端だけを選ぶ。</summary>
    private static void SelectsOnlyRelevantParts()
    {
        var settings = ApiReferenceSettings.Create(alwaysInclude: Array.Empty<string>(), maxPartChars: SmallPartChars);
        var reference = ContextualApiReference.FromMarkdown(Fixture.LongSectionMarkdown(), settings);
        var parts = reference.Index.Parts.Where(p => p.Number == "5").ToList();
        var relevant = parts.Where(p => p.BodyWords.Contains("LongWidget1")).ToList();
        Check.True(relevant.Count > 0 && relevant.Count < parts.Count, "LongWidget1 は §5 の一部の切れ端にだけある");

        // 2 つ目の段落にだけある型の名前を書いているファイル。予算はその切れ端の分だけ
        const string file = "var w = UiWidget.Of<LongWidget1>(root);\n";
        var selection = reference.Select(file, file.Length, relevant.Sum(p => p.Length));
        Check.Equal(string.Join(" ", relevant.Select(p => p.Label)), string.Join(" ", selection.Parts.Select(p => p.Label)),
            "点の高い順に LongWidget1 の切れ端が先に入る（UiWidget・Of だけの切れ端より上）");
        Check.True(selection.Parts.All(p => p.Text.Contains("```csharp", StringComparison.Ordinal)), "単独でもコードとして読める");
    }
}
