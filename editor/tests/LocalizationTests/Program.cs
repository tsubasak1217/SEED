using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace LocalizationTests;

/// <summary>
/// SEED.Localization（多言語）の純粋な部分と見本の表・テンプレートの取り込みのテスト（docs/localization.md）。
/// 引数 <c>--invariant-child</c> のときは Invariant の確かめだけを走らせる（CultureTests が子のプロセスとして起動する）。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Invariant の環境（Android の CoreCLR と同じ）の確かめ（親の CultureTests が環境変数つきで起動する）
        if (args.Length > 0 && args[0] == CultureTests.InvariantChildOption) return CultureTests.RunInvariantChecks();

        var h = new TestHarness();
        TableTests.Register(h);
        IndexTests.Register(h);
        FormatterTests.Register(h);
        PluralTests.Register(h);
        CatalogTests.Register(h);
        CultureTests.Register(h);
        TemplateTests.Register(h);
        return h.Run();
    }
}
