using System;
using System.Collections.Generic;
using System.Linq;
using SEED;
using SEEDEditor.Controls;
using SEEDEditor.Scripting;
using SpriteRigTests;

namespace SEEDEditor.Tests.ScriptEventCatalog;

/// <summary>
/// <see cref="SEEDEditor.Scripting.ScriptEventCatalog"/> の除外規則を固定する単体テスト。
///
/// UI（WPF）を一切通らない部分だけを対象にしているので、
/// `dotnet run --project editor/tests/ScriptEventCatalogTests` で単体実行できる。
/// </summary>
public static class ScriptEventCatalogTests
{
    /// <summary>テストを登録して実行する。</summary>
    /// <returns>プロセス終了コード（全成功なら 0）。</returns>
    public static int Main()
    {
        var harness = new TestHarness();
        var methods = SEEDEditor.Scripting.ScriptEventCatalog.MethodsOf(typeof(FixtureScript));

        // ── 候補に含まれるべきメソッド（名前と引数種別の組で確認）──
        var expected = new (string Name, ScriptEventArgKind Kind)[]
        {
            ("Fire",            ScriptEventArgKind.None),
            ("Say",             ScriptEventArgKind.String),
            ("SetSpeed",        ScriptEventArgKind.Float),
            ("SetCount",        ScriptEventArgKind.Int),
            ("SetEnabled",      ScriptEventArgKind.Bool),
            ("Target",          ScriptEventArgKind.GameObject),
            ("WithReturnValue", ScriptEventArgKind.None),
            ("InheritedFromBase", ScriptEventArgKind.None),
        };
        foreach (var (name, kind) in expected)
        {
            harness.Add($"候補に含まれる: {name}({kind})", () =>
                Check.True(
                    methods.Contains(new ScriptEventMethod(name, kind)),
                    $"{name} が引数種別 {kind} の候補として列挙されていない"));
        }

        // ── 候補から外れるべきメソッド（名前だけで確認）──
        var excluded = new[]
        {
            "UnsupportedArg",     // double 引数
            "TooManyArgs",        // 引数 2 個
            "WithOutArg",         // out 引数
            "WithRefArg",         // ref 引数
            "Generic",            // ジェネリック
            "StaticMethod",       // static
            "PrivateMethod",      // private
            "ProtectedMethod",    // protected
            "get_SomeProperty",   // プロパティのアクセサ（IsSpecialName）
            "set_SomeProperty",
            "OnStart",            // SEEDScript のライフサイクル override
            "OnCollisionEnter",
            "ToString",           // object 由来の override
            "GetHashCode",        // object 由来（override していないもの）
            "Equals",
        };
        foreach (var name in excluded)
        {
            harness.Add($"候補から外れる: {name}", () =>
                Check.True(
                    !methods.Any(m => m.Name == name),
                    $"{name} が候補に混じっている"));
        }

        // ── null 安全（型が解決できなかった場合）──
        harness.Add("型が null なら空リスト", () =>
            Check.Equal(0, SEEDEditor.Scripting.ScriptEventCatalog.MethodsOf(null).Count,
                        "null 型の候補数"));

        // ── 表示文字列（コンボへ出す形）──
        harness.Add("表示文字列は 引数なしなら Name()", () =>
            Check.Equal("Fire()",
                        new ScriptEventMethod("Fire", ScriptEventArgKind.None).DisplayText,
                        "0 引数の表示"));
        harness.Add("表示文字列は 引数ありなら Name(種別)", () =>
            Check.Equal("Say(string)",
                        new ScriptEventMethod("Say", ScriptEventArgKind.String).DisplayText,
                        "1 引数の表示"));

        // ── スクリプト型の列挙 ──
        AddScriptTypeTests(harness);

        return harness.Run();
    }

    /// <summary>
    /// <see cref="SEEDEditor.Scripting.ScriptEventCatalog.ScriptTypesOnActor"/> のテストを登録する。
    /// ACTOR_COMPONENTS の JSON からスナップショットを組み、型解決関数を差し替えて確認する。
    /// </summary>
    private static void AddScriptTypeTests(TestHarness harness)
    {
        // ScriptComponent 2 スロット（うち 1 つは同じスクリプト）＋ 非スクリプトスロット
        const string json = """
        {
          "id": 3,
          "name": "DialogueManager",
          "transform": {},
          "components": [
            { "type": "ScriptComponent", "slot": 0, "name": "", "model_path": "assets://scripts/QuestFlow.cs" },
            { "type": "CameraComponent", "slot": 1, "name": "Main" },
            { "type": "ScriptComponent", "slot": 2, "name": "", "model_path": "assets://scripts/QuestFlow.cs" },
            { "type": "ScriptComponent", "slot": 3, "name": "", "model_path": "assets://scripts/Broken.cs" }
          ]
        }
        """;

        var snapshot = ActorComponentSnapshot.TryParse(json);

        // パス → 型 の差し替え可能な解決関数。Broken.cs は「コンパイルできなかった」ことにする。
        static Type? Compile(string path)
            => path.EndsWith("QuestFlow.cs", StringComparison.Ordinal) ? typeof(FixtureScript) : null;

        harness.Add("ACTOR_COMPONENTS を解析できる", () =>
            Check.True(snapshot is not null, "スナップショットの解析に失敗"));

        harness.Add("スクリプト型は重複せず、解決できたものだけ返る", () =>
        {
            var types = SEEDEditor.Scripting.ScriptEventCatalog.ScriptTypesOnActor(snapshot!, Compile);
            Check.Equal(1, types.Count, "スクリプト型の件数");
            Check.Equal(nameof(FixtureScript), types[0], "スクリプト型名");
        });

        harness.Add("型解決が例外を投げても落ちない", () =>
        {
            var types = SEEDEditor.Scripting.ScriptEventCatalog.ScriptTypesOnActor(
                snapshot!, _ => throw new InvalidOperationException("compile failed"));
            Check.Equal(0, types.Count, "例外時のスクリプト型の件数");
        });
    }
}
