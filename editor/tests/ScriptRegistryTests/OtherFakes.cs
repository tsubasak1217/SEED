namespace ScriptRegistryTests.Other;

// ============================================================
//  OtherFakes.cs — 名前だけ同じ別の型（別の名前空間）
//
//  ランタイムはスクリプトのスロットを「型の名前（.cs のファイル名の語幹）」で照合するので、名前空間だけ違う同名の型を
//  取り違えて返しうる。登録簿が「返ってきたものが探している型に当たるか」を確かめていることのテストに使う。
// ============================================================

/// <summary>名前だけ ScriptRegistryTests.FakeFish と同じ別の型。</summary>
public sealed class FakeFish : FakeScript
{
    /// <summary>名前を付けて作る。</summary>
    /// <param name="label">表示用の名前。</param>
    public FakeFish(string label) : base(label) { }
}
