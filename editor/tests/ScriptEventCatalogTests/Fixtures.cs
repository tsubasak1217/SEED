using SEED;
using SEEDEditor.Scripting;

namespace SEEDEditor.Tests.ScriptEventCatalog;

/// <summary>
/// 候補算出の除外規則を確かめるための「ユーザースクリプト役」の基底クラス。
///
/// 中間のユーザー基底クラスで宣言したメソッドが候補に残ることを確かめるために、
/// SEEDScript と派生スクリプトの間に 1 段はさむ。
/// </summary>
public abstract class FixtureBaseScript : SEEDScript
{
    /// <summary>基底クラスで宣言した 0 引数メソッド（候補に含まれるべき）。</summary>
    public void InheritedFromBase() { }
}

/// <summary>
/// 候補算出のあらゆる分岐を 1 つの型で網羅するテスト用スクリプト。
///
/// 各メンバのコメントに「候補に入るべきか」を明記してある。
/// このクラスの内容と <see cref="ScriptEventCatalogTests"/> の期待値が
/// 除外規則の仕様書そのものになる。
/// </summary>
/// <remarks>
/// sealed にしていないのは、protected メンバ（候補から外れることの確認材料）を
/// sealed 型で宣言すると CS0628 の警告になるため。
/// </remarks>
public class FixtureScript : FixtureBaseScript
{
    // ── 候補に入るもの ───────────────────────────────────────

    /// <summary>0 引数（ArgKind = None）。</summary>
    public void Fire() { }

    /// <summary>string 1 引数（ArgKind = String）。</summary>
    public void Say(string message) { }

    /// <summary>float 1 引数（ArgKind = Float）。</summary>
    public void SetSpeed(float value) { }

    /// <summary>int 1 引数（ArgKind = Int）。</summary>
    public void SetCount(int value) { }

    /// <summary>bool 1 引数（ArgKind = Bool）。</summary>
    public void SetEnabled(bool value) { }

    /// <summary>GameObject 1 引数（ArgKind = GameObject）。</summary>
    public void Target(GameObject other) { }

    /// <summary>戻り値があっても候補になる（値は捨てられる）。</summary>
    public int WithReturnValue() => 0;

    // ── 候補から外れるもの ───────────────────────────────────

    /// <summary>非対応の引数型（double は ScriptEventArgKind に対応が無い）。</summary>
    public void UnsupportedArg(double value) { }

    /// <summary>引数 2 個は非対応。</summary>
    public void TooManyArgs(int a, int b) { }

    /// <summary>out 引数は非対応。</summary>
    public void WithOutArg(out int value) { value = 0; }

    /// <summary>ref 引数は非対応。</summary>
    public void WithRefArg(ref int value) { value = 0; }

    /// <summary>ジェネリックメソッドは型引数を決められないので非対応。</summary>
    public void Generic<T>(T value) { }

    /// <summary>static メソッドはインスタンスへの結線ではないので非対応。</summary>
    public static void StaticMethod() { }

    /// <summary>private メソッドは非対応。</summary>
    private void PrivateMethod() { }

    /// <summary>protected メソッドは非対応。</summary>
    protected void ProtectedMethod() { }

    /// <summary>プロパティのアクセサ（IsSpecialName）は非対応。</summary>
    public int SomeProperty { get; set; }

    /// <summary>SEEDScript のライフサイクルメソッドの override は非対応。</summary>
    public override void OnStart() { }

    /// <summary>引数付きライフサイクルメソッドの override も非対応。</summary>
    public override void OnCollisionEnter(GameObject other) { }

    /// <summary>object 由来メソッドの override も非対応。</summary>
    public override string ToString() => nameof(FixtureScript);
}
