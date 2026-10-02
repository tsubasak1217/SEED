using SEED.Scripting;

namespace ScriptRegistryTests;

// ============================================================
//  Fakes.cs — 登録簿のテストに使う偽のスクリプトとランタイムのスロット
//
//  型の関係（エンジンの IScriptComponent / SEEDScript / ユーザースクリプトに見立てる）:
//    IFakeComponent                  … IScriptComponent に当たる（GetScripts() の「全部」）
//      ├ FakeScript（抽象）           … SEEDScript に当たる
//      │   ├ FakeEnemy               … 基底のユーザースクリプト
//      │   │   └ FakeBoss            … 派生のユーザースクリプト（GetScript<FakeEnemy>() にも当たる）
//      │   ├ FakeFish
//      │   └ IFakeDamageable を実装する FakeCrate
//      └ FakeRawComponent             … SEEDScript ではない IScriptComponent に当たる
//    Other.FakeFish                  … 名前だけ同じ別の型（ランタイムは型の名前で照合するので取り違えうる）
// ============================================================

/// <summary>IScriptComponent に見立てた印。</summary>
public interface IFakeComponent { }

/// <summary>スクリプトのインターフェースでの引き当て用。</summary>
public interface IFakeDamageable { }

/// <summary>SEEDScript に見立てた基底。</summary>
public abstract class FakeScript : IFakeComponent
{
    /// <summary>表示用の名前（失敗時のメッセージを読みやすくする）。</summary>
    public string Label { get; }

    /// <summary>名前を付けて作る。</summary>
    /// <param name="label">表示用の名前。</param>
    protected FakeScript(string label) => Label = label;

    /// <summary>表示用の名前を返す。</summary>
    public override string ToString() => Label;

    /// <summary>
    /// 登録簿が参照の等しさで比べることの確認用: わざとどれも等しいと答える（値の等しさで比べると取り違える）。
    /// </summary>
    public override bool Equals(object? obj) => obj is FakeScript;

    /// <summary>わざとどれも同じハッシュを返す（Equals と対）。</summary>
    public override int GetHashCode() => 0;
}

/// <summary>基底のユーザースクリプトに見立てる。</summary>
public class FakeEnemy : FakeScript
{
    /// <summary>名前を付けて作る。</summary>
    public FakeEnemy(string label) : base(label) { }
}

/// <summary>派生のユーザースクリプトに見立てる。</summary>
public sealed class FakeBoss : FakeEnemy
{
    /// <summary>名前を付けて作る。</summary>
    public FakeBoss(string label) : base(label) { }
}

/// <summary>別系統のユーザースクリプトに見立てる。</summary>
public sealed class FakeFish : FakeScript
{
    /// <summary>名前を付けて作る。</summary>
    public FakeFish(string label) : base(label) { }
}

/// <summary>インターフェースを実装するユーザースクリプトに見立てる。</summary>
public sealed class FakeCrate : FakeScript, IFakeDamageable
{
    /// <summary>名前を付けて作る。</summary>
    public FakeCrate(string label) : base(label) { }
}

/// <summary>SEEDScript ではない IScriptComponent に見立てる。</summary>
public sealed class FakeRawComponent : IFakeComponent
{
    /// <summary>表示用の名前。</summary>
    public string Label { get; }

    /// <summary>名前を付けて作る。</summary>
    public FakeRawComponent(string label) => Label = label;

    /// <summary>表示用の名前を返す。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// ランタイムのスクリプトのスロットに見立てた表（ScriptHost.TryResolveScriptInstance の代わり）。
/// アクタごとにスロットの順でインスタンスを持ち、問い合わせには「型の名前が一致する先頭のスロット」を返す。
/// </summary>
internal sealed class FakeRuntimeSlots
{
    /// <summary>アクタ → スロットの順のインスタンス。</summary>
    private readonly Dictionary<ScriptOwnerKey, List<IFakeComponent>> _slots = new();

    /// <summary>問い合わせの記録（アクタと型の名前）。</summary>
    public List<string> Calls { get; } = new();

    /// <summary>アクタの末尾のスロットへインスタンスを足す。</summary>
    /// <param name="owner">アクタ。</param>
    /// <param name="instance">インスタンス。</param>
    public void Add(ScriptOwnerKey owner, IFakeComponent instance)
    {
        if (!_slots.TryGetValue(owner, out var list)) _slots[owner] = list = new List<IFakeComponent>();
        list.Add(instance);
    }

    /// <summary>
    /// 問い合わせ（ScriptSlotProbe）。型の名前（名前空間なし）で照合し、スロットの先頭に近い 1 つを返す。
    /// </summary>
    /// <param name="owner">アクタ。</param>
    /// <param name="scriptType">探す型。</param>
    public IFakeComponent? Probe(ScriptOwnerKey owner, Type scriptType)
    {
        Calls.Add($"{owner.Index}:{owner.Generation}/{scriptType.Name}");
        if (!_slots.TryGetValue(owner, out var list)) return null;
        foreach (var instance in list)
            if (instance.GetType().Name == scriptType.Name) return instance;
        return null;
    }
}

/// <summary>テストで使う短い道具。</summary>
internal static class TestKit
{
    /// <summary>アクタ A（index 1, generation 0）。</summary>
    public static readonly ScriptOwnerKey ActorA = new(1, 0);

    /// <summary>アクタ B（index 2, generation 0）。</summary>
    public static readonly ScriptOwnerKey ActorB = new(2, 0);

    /// <summary>登録簿を作る（エンジンの ScriptInstanceRegistry&lt;IScriptComponent&gt; に当たる）。</summary>
    public static ScriptInstanceRegistry<IFakeComponent> NewRegistry() => new();

    /// <summary>生成を知らせてから持ち主を決める（OnStart を迎えたスクリプトに当たる）。</summary>
    /// <param name="registry">登録簿。</param>
    /// <param name="instance">インスタンス。</param>
    /// <param name="owner">持ち主のアクタ。</param>
    public static T Started<T>(ScriptInstanceRegistry<IFakeComponent> registry, T instance, ScriptOwnerKey owner)
        where T : class, IFakeComponent
    {
        registry.NoteCreated(instance);
        registry.Bind(instance, owner);
        return instance;
    }

    /// <summary>配列を表示用の文字へ（並びの確認用）。</summary>
    /// <param name="items">並び。</param>
    public static string Names<T>(IEnumerable<T> items) => string.Join(",", items.Select(i => i?.ToString()));

    /// <summary>
    /// 同じインスタンスであることを表明する（偽のスクリプトは Equals をわざと「どれも等しい」にしてあるので、
    /// Check.Equal では取り違えを見逃す。参照で比べる）。
    /// </summary>
    /// <param name="expected">期待するインスタンス（null 可）。</param>
    /// <param name="actual">実際のインスタンス（null 可）。</param>
    /// <param name="what">対象の説明。</param>
    public static void Same(object? expected, object? actual, string what)
        => SpriteRigTests.Check.True(ReferenceEquals(expected, actual), $"{what}: 期待 {expected?.ToString() ?? "null"} / 実際 {actual?.ToString() ?? "null"}");
}
