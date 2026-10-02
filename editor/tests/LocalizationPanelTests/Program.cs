using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace LocalizationPanelTests;

/// <summary>
/// 文字列表のパネルの WPF に依らない部分のテスト（docs/localization.md §15）。
/// </summary>
public static class Program
{
    public static int Main()
    {
        var h = new TestHarness();
        WriterTests.Register(h);
        DocumentTests.Register(h);
        ModelTests.Register(h);
        ModelEditTests.Register(h);
        LanguageTests.Register(h);
        IoTests.Register(h);
        RuleTests.Register(h);
        return h.Run();
    }
}
