using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace BindingTests;

/// <summary>
/// SEED.Binding（観測できる値と UI 部品への結び付け）の純粋な部分のテスト（docs/ui_binding.md）。
/// UI 部品の代わりに偽の当てる先（Fakes.cs）を差して、エンジン無しで結び付けの規則を確かめる。
/// </summary>
public static class Program
{
    /// <summary>全テストを登録して走らせる。</summary>
    /// <returns>全部成功なら 0。</returns>
    public static int Main()
    {
        var h = new TestHarness();
        ObservableTests.Register(h);
        ComputedTests.Register(h);
        ObservableListTests.Register(h);
        BindTests.Register(h);
        FrameTests.Register(h);
        return h.Run();
    }
}
