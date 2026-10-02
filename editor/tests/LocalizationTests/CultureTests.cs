using System.Diagnostics;
using System.Globalization;
using SEED.Localization;
using SpriteRigTests;

namespace LocalizationTests;

// ============================================================
//  CultureTests.cs — 文化の引き当てと数・日付・時刻の書式、Invariant の環境（Android の CoreCLR と同じ）での振る舞い
//
//  Invariant の確かめは、このテスト自身を子のプロセスとして DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 で走らせる
//  （同じプロセスの中では途中で切り替えられないため）。子は InvariantChecks だけを走らせて終わりのコードを返す。
// ============================================================

/// <summary>文化と書式のテスト。</summary>
internal static class CultureTests
{
    /// <summary>子のプロセスで Invariant の確かめだけを走らせる引数。</summary>
    public const string InvariantChildOption = "--invariant-child";

    /// <summary>Invariant にする環境変数。</summary>
    private const string InvariantVariable = "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT";

    /// <summary>子のプロセスを待つ上限（ミリ秒）。</summary>
    private const int ChildTimeoutMs = 60_000;

    /// <summary>確かめに使う日付（2026-10-02 は金曜日）。</summary>
    private static readonly DateTime Sample = new(2026, 10, 2, 7, 5, 0);

    /// <summary>不変文化。</summary>
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>テストを登録する（Invariant ではない親のプロセス）。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("文化: 空・読めない名前は不変文化・文化の名前（不変文化は null）", () =>
        {
            Check.True(ReferenceEquals(Inv, LocaleCulture.Resolve(null)), "null");
            Check.True(ReferenceEquals(Inv, LocaleCulture.Resolve(" ")), "空白");
            Check.True(ReferenceEquals(Inv, LocaleCulture.Resolve("not a culture!!")), "読めない名前");
            Check.Equal(null, LocaleCulture.LanguageOf(Inv), "不変文化は null");
            Check.Equal("ja-JP", LocaleCulture.LanguageOf(CultureInfo.GetCultureInfo("ja-JP")), "ja-JP");
            Check.Equal("en-US", LocaleCulture.Resolve("en_US").Name, "_ を - にして引く");
        });

        h.Add("文化: 区切りつきの数（文化の書き方・桁は 0〜15 に収める）", () =>
        {
            Check.Equal("1,234.5", LocaleCulture.FormatNumber(1234.5, 1, CultureInfo.GetCultureInfo("en-US")), "en-US");
            Check.Equal("1.234,5", LocaleCulture.FormatNumber(1234.5, 1, CultureInfo.GetCultureInfo("de-DE")), "de-DE");
            Check.Equal("1,234.5", LocaleCulture.FormatNumber(1234.5, 1, Inv), "不変文化");
            Check.Equal("1,234", LocaleCulture.FormatNumber(1234.4, -3, Inv), "負の桁は 0");
            string many = LocaleCulture.FormatNumber(0.5, 99, Inv);
            Check.Equal(LocaleCulture.MaxFractionDigits, many.Length - many.IndexOf('.') - 1, "桁の上限");
        });

        h.Add("文化: 日付・時刻（書式・既定の短い形・読めない書式は短い形に戻す）", () =>
        {
            var ja = CultureInfo.GetCultureInfo("ja-JP");
            var en = CultureInfo.GetCultureInfo("en-US");
            Check.Equal("10月2日", LocaleCulture.FormatDate(Sample, "M月d日", ja), "ja の書式");
            Check.Equal("Oct 2", LocaleCulture.FormatDate(Sample, "MMM d", en), "en の書式");
            Check.Equal("10/02/2026", LocaleCulture.FormatDate(Sample, null, Inv), "既定は短い日付");
            Check.Equal("10/02/2026", LocaleCulture.FormatDate(Sample, "%", Inv), "読めない書式は短い日付");
            Check.Equal("7:05", LocaleCulture.FormatTime(new TimeOnly(7, 5), "H:mm", Inv), "時刻の書式");
            Check.Equal("7:05 AM", LocaleCulture.FormatTime(new TimeOnly(7, 5), "h:mm tt", en), "en の午前");
            Check.Equal("07:05", LocaleCulture.FormatTime(new TimeOnly(7, 5), "", Inv), "既定は短い時刻");
        });

        // 2 回目のレビュー（docs/reviews/2026-10-03_code_review.md）#33（3 件目）: グレゴリオ暦でない文化（ar-SA の UmAlQura など）で
        // 暦の範囲の外の日付を書くと ArgumentOutOfRangeException が呼び手へ飛んだ（FormatException しか捕まえていなかった）
        h.Add("文化: 暦の範囲の外の日付（グレゴリオ暦でない文化）でも例外にせず書く・差し込みの {date:書式} も（レビュー #33）", () =>
        {
            // 既定の暦の範囲が DateTime より狭い文化を探す（ICU の環境なら ar-SA などがある。無ければ確かめられないので失敗にする）
            CultureInfo? narrow = null;
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
            {
                if (culture.Calendar.MaxSupportedDateTime >= DateTime.MaxValue.Date) continue;
                narrow = culture;
                break;
            }
            Check.True(narrow is not null, "暦の範囲が狭い文化がある（このプロセスは Invariant ではない）");
            var beyond = narrow!.Calendar.MaxSupportedDateTime.AddDays(1);
            string date = LocaleCulture.FormatDate(beyond, null, narrow);
            Check.True(date.Length > 0, $"{narrow.Name}: 範囲の外の日付も書ける（{date}）");
            string withPattern = LocaleCulture.FormatDate(beyond, "yyyy/M/d", narrow);
            Check.Equal(beyond.ToString("yyyy/M/d", Inv), withPattern, $"{narrow.Name}: 書式つきは不変文化（グレゴリオ暦）で書く");
            string inserted = LocaleFormatter.Format("期限 {when:yyyy/M/d}", new (string, object?)[] { ("when", beyond) }, narrow);
            Check.Equal("期限 " + beyond.ToString("yyyy/M/d", Inv), inserted, $"{narrow.Name}: 差し込みも例外にしない");
        });

        h.Add("文化: 端末の言語（このプロセスは Invariant ではないので CurrentUICulture の名前か、不変文化なら null）", () =>
        {
            string? system = LocaleCulture.DetectSystemLanguage();
            string expected = CultureInfo.CurrentUICulture.Name;
            Check.Equal(expected.Length == 0 ? null : expected, system, "CurrentUICulture の名前");
        });

        h.Add("文化: Invariant の環境（Android の CoreCLR と同じ）で子のプロセスを走らせる", () =>
        {
            var (exitCode, output) = RunInvariantChild();
            Check.True(exitCode == 0, $"子のプロセスの確かめが通る（終わりのコード {exitCode}）\n{output}");
            Console.Write(Indent(output));
        });
    }

    /// <summary>子のプロセス（Invariant）で走らせる確かめ。</summary>
    /// <returns>終わりのコード（全部通れば 0）。</returns>
    public static int RunInvariantChecks()
    {
        var h = new TestHarness();
        h.Add("Invariant: CurrentUICulture は不変文化で、端末の言語は null", () =>
        {
            Check.Equal("", CultureInfo.CurrentUICulture.Name, "CurrentUICulture");
            Check.Equal(null, LocaleCulture.DetectSystemLanguage(), "SystemLanguage");
        });

        h.Add("Invariant: 文化を引いても不変文化の書式（例外にしない）", () =>
        {
            var de = LocaleCulture.Resolve("de-DE");
            Check.Equal("1,234.5", LocaleCulture.FormatNumber(1234.5, 1, de), "de-DE でも不変文化の数");
            Check.Equal("10月2日", LocaleCulture.FormatDate(Sample, "M月d日", LocaleCulture.Resolve("ja-JP")), "数字だけの書式は言語どおり");
            Check.Equal("Oct 2", LocaleCulture.FormatDate(Sample, "MMM d", LocaleCulture.Resolve("ja-JP")), "月の名前は英語になる");
            Check.Equal("7:05 AM", LocaleCulture.FormatTime(new TimeOnly(7, 5), "h:mm tt", LocaleCulture.Resolve("en-US")), "午前は AM");
        });

        h.Add("Invariant: 本体は端末の言語が取れないので既定の言語で始まり、書式は不変文化", () =>
        {
            var source = new MemoryLocaleSource()
                .Put("assets://locale/index.json", """{ "default": "ja", "languages": [ { "code": "ja", "culture": "ja-JP" }, { "code": "en", "culture": "en-US" } ] }""")
                .Put("assets://locale/ja.json", """{ "a": "あ" }""")
                .Put("assets://locale/en.json", """{ "a": "A" }""");
            var catalog = new LocaleCatalog(source, new MemoryLocaleStore(), null, LocaleCulture.DetectSystemLanguage);
            Check.Equal("ja", catalog.Language, "既定の言語");
            Check.Equal("1,234,567", catalog.FormatNumber(1234567, 0), "数");
            catalog.SetLanguage("en");
            Check.Equal("A", catalog.Get("a", ReadOnlySpan<(string Name, object? Value)>.Empty), "切り替えは文化に依らない");
        });
        return h.Run();
    }

    /// <summary>このテストを子のプロセス（Invariant）で走らせる。</summary>
    /// <returns>（終わりのコード, 出力）。</returns>
    private static (int ExitCode, string Output) RunInvariantChild()
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所がわかりません");
        var info = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // dotnet LocalizationTests.dll で走っているときは dll を最初の引数に渡す（apphost なら直接）
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = processPath;
            info.ArgumentList.Add(typeof(CultureTests).Assembly.Location);
        }
        else
        {
            info.FileName = processPath;
        }
        info.ArgumentList.Add(InvariantChildOption);
        info.Environment[InvariantVariable] = "1";

        using var process = Process.Start(info) ?? throw new InvalidOperationException("子のプロセスを起動できません");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ChildTimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "子のプロセスが時間内に終わりません");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>子の出力を字下げする（親の結果の中で見分けやすく）。</summary>
    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(line => "      │" + line.TrimEnd('\r'))) + Environment.NewLine;
}
