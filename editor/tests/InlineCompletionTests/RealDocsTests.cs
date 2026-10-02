using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;
using SpriteRigTests;

namespace InlineCompletionTests;

/// <summary>
/// 実際の docs/scripting_api.md と同梱の editor/config/inline_completion_reference.json で確かめる。
/// 各節の文字数・全節の合計・基礎の節の合計と、見本のファイルごとに選ばれる節を標準出力へ出す。
/// </summary>
public static class RealDocsTests
{
    /// <summary>以前の固定の予算（MaxChars）。「基礎の節だけで収まる」の基準。</summary>
    private const int LegacyBudgetChars = 12000;

    /// <summary>節の文字数の一覧を 1 行に並べる数。</summary>
    private const int SizesPerLine = 6;

    /// <summary>見本ごとに出す点の上位の数。</summary>
    private const int TopScoresToPrint = 8;

    /// <summary>大きなファイルの見本の繰り返しの数（行数を数千行にする）。</summary>
    private const int LargeFileRepeat = 120;

    /// <summary>選択 1 回にかかってよい時間の上限（ミリ秒。異常に遅くなっていないかの粗い検知）。</summary>
    private const double MaxSelectMilliseconds = 500.0;

    /// <summary>実際の docs で作った窓口（1 回だけ作る）。</summary>
    private static readonly Lazy<ContextualApiReference> Real = new(() =>
        ContextualApiReference.FromMarkdown(
            File.ReadAllText(RepoFiles.Find(RepoFiles.ScriptingApiRelative)),
            ApiReferenceSettings.Load(RepoFiles.Find(RepoFiles.ReferenceSettingsRelative))));

    /// <summary>ScreenStack で画面を積む見本（Localization は使わない）。</summary>
    private const string ScreenStackSample = """
        using SEEDEditor.Scripting;
        using SEED.UI;

        public class AlarmListScreen : UiScreen
        {
            private ScreenStack? _stack;

            protected override void OnScreenEnter(object? args)
            {
                _stack = SEED.UI.UiWidget.Of<ScreenStack>(SEED.GameObject.Find("RootStack"));
            }

            public void OpenEdit(int alarmId)
            {
                ScreenHandle h = _stack!.Push("assets://alarm/prefabs/edit.actor", NavTransition.Push, args: alarmId);
                /*|*/
            }
        }
        """;

    /// <summary>L10n で文字を引く見本。</summary>
    private const string LocalizationSample = """
        using SEEDEditor.Scripting;
        using SEED.Localization;

        public class TitleLabel : SEEDScript
        {
            public override void OnStart()
            {
                string title = L10n.Get("menu.start");
                L10n.SetLanguage("en");
                /*|*/
            }
        }
        """;

    /// <summary>Transform・Input・Time を使うゲームの見本（UI も Localization も使わない）。</summary>
    private const string GameplaySample = """
        using SEEDEditor.Scripting;

        public class Mover : SEEDScript
        {
            [SerializeField] private float speed = 2.0f;

            public override void Update(ref NativeFrameContext ctx)
            {
                if (SEED.Input.GetKey(SEED.KeyCode.Space))
                    transform.Position += SEED.Vector3.Up * speed * SEED.Time.DeltaTime;
                /*|*/
            }
        }
        """;

    /// <summary>Bind. と書きかけた見本（SEED.Binding を使い始めるところ）。</summary>
    private const string BindSample = """
        using SEEDEditor.Scripting;

        public class CounterView : SEEDScript
        {
            public override void OnStart()
            {
                var label = SEED.GameObject.Find("Count");
                Bind./*|*/
            }
        }
        """;

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("実 docs: 全節の文字数と、基礎の節だけで予算 12000 に収まること", BaseFitsInLegacyBudget);
        harness.Add("実 docs: ScreenStack を書いているファイルで §7.18 が入る", ScreenStackSelects718);
        harness.Add("実 docs: Localization を使わないファイルで §7.21 が落ちる", NoLocalizationDrops721);
        harness.Add("実 docs: L10n を書いているファイルで §7.21 が入る", LocalizationSelects721);
        harness.Add("実 docs: Bind. と書き始めると §7.22 が入る（docs に §7.22 があるとき）", BindSelects722);
        harness.Add("実 docs: どの見本・予算でも予算を超えず文書の順", BudgetAndOrderOnRealDocs);
        harness.Add("実 docs: 文脈を渡さない呼び出しは従来どおり先頭から予算まで", HeadIsLegacyBehavior);
        harness.Add("実 docs: 数千行のファイルでも選択は軽い（時間を出す）", SelectionIsFast);
    }

    // ── 文字数 ─────────────────────────────────────────────

    /// <summary>全節の文字数を出し、基礎の節だけで以前の予算に収まることを確かめる。</summary>
    private static void BaseFitsInLegacyBudget()
    {
        var real = Real.Value;
        var index = real.Index;
        Check.True(real.Warnings.Count == 0, "always_include がすべて実 docs の節に当たる: " + string.Join(" / ", real.Warnings));

        // 節ごとの文字数（切れ端の単独の文字数の和ではなく、元の節の本文の文字数）
        var sections = index.Parts.GroupBy(p => p.SectionIndex)
            .Select(g => (Label: g.First().SectionLabel, Chars: ApiReferenceSelection.Render(g.ToList()).Length, Parts: g.Count()))
            .ToList();
        Console.WriteLine($"         圧縮後の全文 {index.CompactText.Length:N0} 文字・節 {index.SectionCount} 個・切れ端 {index.Parts.Count} 個" +
                          $"（切れ端の単独の合計 {index.TotalPartChars:N0} 文字）・全節の合計 {sections.Sum(s => s.Chars):N0} 文字");
        Console.WriteLine($"         基礎の節（always_include）の合計 {real.AlwaysIncludedChars:N0} 文字 / 予算 {real.Settings.BudgetChars:N0}" +
                          $"（max_part_chars {real.Settings.MaxPartChars}）");
        var line = new StringBuilder();
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            line.Append($"{s.Label}={s.Chars}{(s.Parts > 1 ? $"({s.Parts}分割)" : "")}  ");
            if ((i + 1) % SizesPerLine == 0 || i == sections.Count - 1)
            {
                Console.WriteLine("           " + line.ToString().TrimEnd());
                line.Clear();
            }
        }

        Check.True(real.AlwaysIncludedChars <= LegacyBudgetChars,
            $"基礎の節の合計 {real.AlwaysIncludedChars} は予算 {LegacyBudgetChars} 以下");
        Check.True(sections.Sum(s => s.Chars) == index.CompactText.Length,
            "全節を続けて並べると圧縮後の全文と同じ長さ（切れ端の補いが本文に残らない）");
        Check.True(!index.CompactText.Contains("メンテナ向け", StringComparison.Ordinal), "メンテナ向けの節以降は入らない");
    }

    // ── 選択 ─────────────────────────────────────────────

    /// <summary>ScreenStack を書いているファイルで §7.18 の ScreenStack の切れ端が入る。</summary>
    private static void ScreenStackSelects718()
    {
        var selection = SelectAndPrint("ScreenStack の見本", ScreenStackSample);
        Check.True(selection.Parts.Any(p => p.Number == "7.18" && p.BodyWords.Contains("ScreenStack")),
            "§7.18 の ScreenStack を含む切れ端が入る: " + selection.DescribeLabels());
    }

    /// <summary>Localization を使わないファイル（画面・ゲーム）では §7.21 が入らない。</summary>
    private static void NoLocalizationDrops721()
    {
        foreach (var (name, sample) in new[] { ("ScreenStack の見本", ScreenStackSample), ("ゲームの見本", GameplaySample) })
        {
            var selection = SelectAndPrint(name, sample);
            Check.True(!selection.Parts.Any(p => p.Number == "7.21"), $"{name}に §7.21 が入らない: " + selection.DescribeLabels());
        }
    }

    /// <summary>L10n を書いているファイルで §7.21 が入る。</summary>
    private static void LocalizationSelects721()
    {
        var selection = SelectAndPrint("Localization の見本", LocalizationSample);
        Check.True(selection.Parts.Any(p => p.Number == "7.21" && p.BodyWords.Contains("L10n")),
            "§7.21 の L10n を含む切れ端が入る: " + selection.DescribeLabels());
        Check.True(!selection.Parts.Any(p => p.Number == "7.18"), "Localization の見本に §7.18 は入らない: " + selection.DescribeLabels());
    }

    /// <summary>Bind. と書き始めると §7.22 が入る（この作業ツリーの docs に §7.22 が無ければ印だけ出して飛ばす）。</summary>
    private static void BindSelects722()
    {
        var index = Real.Value.Index;
        if (!index.Parts.Any(p => p.Number == "7.22"))
        {
            Console.WriteLine("         （この docs に §7.22 Binding がまだ無いので飛ばす。規則は見本の docs の BindTyping で確かめている）");
            return;
        }
        var selection = SelectAndPrint("Bind. の見本", BindSample);
        Check.True(selection.Parts.Any(p => p.Number == "7.22"), "§7.22 が入る: " + selection.DescribeLabels());
    }

    /// <summary>どの見本・どの予算でも予算を超えず、切れ端は文書の順に並ぶ。</summary>
    private static void BudgetAndOrderOnRealDocs()
    {
        var real = Real.Value;
        int[] budgets = { 0, 500, 4000, LegacyBudgetChars, 30000, 120000, real.Index.CompactText.Length * 2 };
        foreach (var sample in new[] { ScreenStackSample, LocalizationSample, GameplaySample, BindSample })
        {
            var (text, caret) = Fixture.Split(sample);
            foreach (var budget in budgets)
            {
                var selection = real.Select(text, caret, budget);
                Check.True(selection.Length <= budget, $"予算 {budget} を超えない（{selection.Length}）");
                for (int i = 1; i < selection.Parts.Count; i++)
                    Check.True(selection.Parts[i - 1].Index < selection.Parts[i].Index, "切れ端は文書の順");
            }
        }
    }

    /// <summary>文脈を渡さない呼び出し（Head）は圧縮後の全文の先頭から予算まで。</summary>
    private static void HeadIsLegacyBehavior()
    {
        var real = Real.Value;
        var head = real.Head(LegacyBudgetChars);
        Check.Equal(LegacyBudgetChars, head.Length, "先頭から予算ちょうど");
        Check.True(real.Index.CompactText.StartsWith(head, StringComparison.Ordinal), "圧縮後の全文の先頭");
    }

    /// <summary>数千行のファイルで選択（キャッシュなし）の時間を測る。</summary>
    private static void SelectionIsFast()
    {
        var real = Real.Value;
        var (one, _) = Fixture.Split(ScreenStackSample);
        var big = new StringBuilder();
        for (int i = 0; i < LargeFileRepeat; i++) big.Append(one.Replace("AlarmListScreen", $"Screen{i}")).Append('\n');
        string text = big.ToString();
        int caret = text.Length / 2;

        // 1 回目は JIT を含むので 2 回目（内容を 1 文字変えてキャッシュを外す）を測る
        real.Select(text, caret, LegacyBudgetChars);
        var watch = Stopwatch.StartNew();
        var selection = real.Select(text + " ", caret, LegacyBudgetChars);
        watch.Stop();
        int lines = text.Count(c => c == '\n');
        Console.WriteLine($"         {lines:N0} 行・{text.Length:N0} 文字のファイル: 選択 {watch.Elapsed.TotalMilliseconds:F1} ms（{selection.Length} 文字）");

        // 同じ内容・位置・予算はキャッシュから
        var again = real.Select(text + " ", caret, LegacyBudgetChars);
        Check.True(ReferenceEquals(selection, again), "同じ内容・位置・予算はキャッシュを返す");
        Check.True(watch.Elapsed.TotalMilliseconds < MaxSelectMilliseconds, $"選択が {MaxSelectMilliseconds} ms 未満");
    }

    // ── 補助 ─────────────────────────────────────────────

    /// <summary>見本で選び、選ばれた節と点の上位を出す。</summary>
    private static ApiReferenceSelection SelectAndPrint(string name, string sample)
    {
        var real = Real.Value;
        var (text, caret) = Fixture.Split(sample);
        var selection = real.Select(text, caret, real.Settings.BudgetChars);
        var scores = real.ScoreFor(text, caret);
        var top = scores.Select((score, i) => (score, i)).Where(t => t.score > 0)
            .OrderByDescending(t => t.score).Take(TopScoresToPrint)
            .Select(t => $"{real.Index.Parts[t.i].Label}={t.score:F1}{(selection.Contains(real.Index.Parts[t.i]) ? "*" : "")}");
        Console.WriteLine($"         [{name}] 注入: {selection.DescribeLabels()}（{selection.Length} 文字）");
        Console.WriteLine($"           点の上位（* = 入った）: {string.Join("  ", top)}");
        return selection;
    }
}
