using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace ScriptRegistryTests;

/// <summary>
/// スクリプトのインスタンスの登録簿（GameObject.GetScript / SEEDScript.Instances の元）の純粋な部分のテスト
/// （docs/scripting_api.md §7「スクリプトを引く」）。スクリプトとランタイムのスロットの代わりに偽の型（Fakes.cs）を差して、
/// ScriptBridge の FFI を通さずに登録簿の規則を確かめる。
/// </summary>
public static class Program
{
    /// <summary>全テストを登録して走らせる。</summary>
    /// <returns>全部成功なら 0。</returns>
    public static int Main()
    {
        var h = new TestHarness();
        LifecycleTests.Register(h);
        QueryTests.Register(h);
        ProbeTests.Register(h);
        TreeWalkTests.Register(h);
        return h.Run();
    }
}
