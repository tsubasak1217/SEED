using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion;
using SpriteRigTests;

namespace InlineCompletionTests;

/// <summary>
/// 提供元からプロンプトまでのつなぎ目のテスト。偽物の提供元（通信しない）が
/// <see cref="InlineCompletionSystemPrompt.Build"/> を呼び、実際の docs と同梱の設定で
/// 選ばれた節がシステムプロンプトへ入ること・注入のログ・環境設定の予算が効くことを確かめる。
/// 外部の AI サービスは呼ばない。
/// </summary>
public static class PromptTests
{
    /// <summary>注入のログの頭。</summary>
    private const string InjectionLogHead = "[インライン補完] 注入: ";

    /// <summary>偽物の提供元が返す補完。</summary>
    private const string FakeCompletion = "// fake";

    /// <summary>ScreenStack で画面を積む見本。</summary>
    private const string ScreenSample = """
        using SEEDEditor.Scripting;
        using SEED.UI;

        public class AlarmListScreen : UiScreen
        {
            public void OpenEdit(int alarmId)
            {
                var stack = SEED.UI.UiWidget.Of<ScreenStack>(SEED.GameObject.Find("RootStack"));
                ScreenHandle h = stack.Push("assets://alarm/prefabs/edit.actor", NavTransition.Push, args: alarmId);
                /*|*/
            }
        }
        """;

    /// <summary>
    /// 偽物の提供元: 通信せず、本物の提供元と同じ口でシステムプロンプトを組み立てて覚えるだけ。
    /// </summary>
    private sealed class FakeProvider : IInlineCompletionProvider
    {
        /// <summary>最後に組み立てたシステムプロンプト。</summary>
        public string LastSystemPrompt { get; private set; } = string.Empty;

        /// <summary>いつでも使える。</summary>
        public bool IsAvailable => true;

        /// <summary>システムプロンプトを組み立てて決まった補完を返す。</summary>
        public Task<string?> GetCompletionAsync(string prefix, string suffix, CancellationToken ct)
        {
            LastSystemPrompt = InlineCompletionSystemPrompt.Build(prefix, suffix);
            return Task.FromResult<string?>(FakeCompletion);
        }
    }

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("つなぎ目: 偽物の提供元のシステムプロンプトに文脈の節が入り、注入のログが出る", PromptCarriesSelectedSections);
        harness.Add("つなぎ目: 同じ選択が続くとログは短い行になる", RepeatedInjectionLogsShort);
        harness.Add("つなぎ目: 環境設定の予算が効く（0 で注入なし・大きくすると増える）", PreferenceBudgetApplies);
        harness.Add("つなぎ目: 文脈を渡さない Load() は従来どおり先頭から予算まで", LegacyLoadIsHead);
    }

    /// <summary>偽物の提供元で 1 回補完する。</summary>
    private static (FakeProvider Provider, string? Result) Complete(string sample)
    {
        var (text, caret) = Fixture.Split(sample);
        var provider = new FakeProvider();
        var result = provider.GetCompletionAsync(text[..caret], text[caret..], CancellationToken.None).GetAwaiter().GetResult();
        return (provider, result);
    }

    /// <summary>選んだ節がプロンプトへ入る。</summary>
    private static void PromptCarriesSelectedSections()
    {
        EditorPreferences.Instance.InlineCompletionReferenceChars = null;
        int before = EditorLog.Lines.Count;
        var (provider, result) = Complete(ScreenSample);

        Check.Equal(FakeCompletion, result, "偽物の補完が返る");
        var prompt = provider.LastSystemPrompt;
        Check.True(prompt.StartsWith(InlineCompletionSystemPrompt.Base + InlineCompletionSystemPrompt.ReferenceHeader, StringComparison.Ordinal),
            "基本方針 → 区切り → リファレンスの順");
        Check.True(prompt.Contains("## 7.18 画面の組み立て", StringComparison.Ordinal), "§7.18 の見出しが入る");
        Check.True(prompt.Contains("UiWidget.Of<ScreenStack>", StringComparison.Ordinal), "ScreenStack のコードが入る");
        Check.True(!prompt.Contains("## 7.21 Localization", StringComparison.Ordinal), "§7.21 は入らない");
        int reference = prompt.Length - InlineCompletionSystemPrompt.Base.Length - InlineCompletionSystemPrompt.ReferenceHeader.Length;
        Check.True(reference <= ScriptApiReference.BudgetChars, $"リファレンスは予算 {ScriptApiReference.BudgetChars} 以内（{reference}）");

        var logs = EditorLog.Lines.Skip(before).ToList();
        var injection = logs.LastOrDefault(l => l.StartsWith(InjectionLogHead, StringComparison.Ordinal));
        Console.WriteLine($"         ログ: {injection}");
        Check.True(injection is not null && injection.Contains("§7.18", StringComparison.Ordinal) && injection.EndsWith($"（{reference} 文字）", StringComparison.Ordinal),
            "注入のログに節の名前と文字数が出る");
        Check.True(EditorLog.Lines.Any(l => l.Contains("APIリファレンス索引", StringComparison.Ordinal)), "初回に索引の要約がログに出る");
    }

    /// <summary>同じ選択のログ。</summary>
    private static void RepeatedInjectionLogsShort()
    {
        EditorPreferences.Instance.InlineCompletionReferenceChars = null;
        Complete(ScreenSample);
        int before = EditorLog.Lines.Count;
        Complete(ScreenSample);
        var line = EditorLog.Lines.Skip(before).Single(l => l.StartsWith(InjectionLogHead, StringComparison.Ordinal));
        Check.True(line.Contains("前回と同じ", StringComparison.Ordinal), "2 回目は短い行: " + line);
    }

    /// <summary>環境設定の予算。</summary>
    private static void PreferenceBudgetApplies()
    {
        try
        {
            EditorPreferences.Instance.InlineCompletionReferenceChars = 0;
            var (none, _) = Complete(ScreenSample);
            Check.Equal(InlineCompletionSystemPrompt.Base, none.LastSystemPrompt, "予算 0 なら基本方針だけ");
            Check.True(EditorLog.Lines.Last().Contains("なし", StringComparison.Ordinal), "注入なしのログ");

            // L10n の見本は §7.21 の 4 切れ端すべてが点の下限を越えるが、既定の予算には 3 つしか入らない
            EditorPreferences.Instance.InlineCompletionReferenceChars = null;
            int defaultLength = Complete(Fixture.LocalizationFile).Provider.LastSystemPrompt.Length;

            const int largeBudget = 60000;
            EditorPreferences.Instance.InlineCompletionReferenceChars = largeBudget;
            Check.Equal(largeBudget, ScriptApiReference.BudgetChars, "環境設定の値が予算になる");
            var (large, _) = Complete(Fixture.LocalizationFile);
            int reference = large.LastSystemPrompt.Length - InlineCompletionSystemPrompt.Base.Length - InlineCompletionSystemPrompt.ReferenceHeader.Length;
            Check.True(large.LastSystemPrompt.Length > defaultLength, $"予算を大きくすると多くの節が入る（{defaultLength} → {large.LastSystemPrompt.Length}）");
            Check.True(reference <= largeBudget, "それでも予算以内");
        }
        finally
        {
            EditorPreferences.Instance.InlineCompletionReferenceChars = null;
        }
    }

    /// <summary>従来の Load()。</summary>
    private static void LegacyLoadIsHead()
    {
        EditorPreferences.Instance.InlineCompletionReferenceChars = null;
        var head = ScriptApiReference.Load();
        Check.Equal(ScriptApiReference.BudgetChars, head.Length, "予算ちょうどの長さ");
        Check.True(head.StartsWith("# SEED スクリプト API リファレンス\n", StringComparison.Ordinal), "圧縮後の先頭から");
    }
}
