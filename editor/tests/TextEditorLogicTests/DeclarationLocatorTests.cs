using System;
using System.IO;
using SEEDEditor.Panels.ScriptEditor;
using SpriteRigTests;                // TestHarness / Check（テストランナーは共用）

namespace TextEditorLogicTests;

/// <summary>
/// F12（定義へ移動）の飛び先を決める <see cref="DeclarationLocator"/> の単体テスト。
///
/// 守りたい性質:
///   1. 宣言より前のコメント・別メンバの本文に同じ単語があっても、**宣言そのもの**へ飛ぶ
///      （2026-09-19 の報告: GameObject.Instantiate へ F12 → summary の &lt;c&gt;Instantiate&lt;/c&gt; へ飛んだ）
///   2. オーバーロードは引数の個数・型で正しい方を選ぶ
///   3. プロパティ・フィールド・列挙子・コンストラクタ・インデクサも宣言へ飛ぶ
///   4. メンバがこのファイルに無ければ型の宣言へ（partial の別ファイルを探す側が続きを引き取る）
/// </summary>
public static class DeclarationLocatorTests
{
    /// <summary>報告された状況を最小化した見本（summary に同じ単語が先に出る）。</summary>
    private const string Sample = """
        namespace SEED;

        /// <summary>見本の型。Spawn という単語がコメントに先に出てくる。</summary>
        public readonly struct Thing
        {
            /// <summary>名前。<c>Spawn</c> で同じものを複数作ると全部同名になる。</summary>
            public string Name => "x";

            private readonly int _count;
            public static int Shared, Other;

            /// <summary>生成する。</summary>
            public static Thing Spawn(string path)
                => new Thing();

            /// <summary><see cref="Spawn(string)"/> の親つき版。</summary>
            public static Thing Spawn(string path, Thing parent)
                => new Thing();

            public Thing(int count) { _count = count; }

            public int this[int index] => index;

            public enum Mode { Idle, Spawn }
        }

        public enum Color { Red, Green }
        """;

    /// <summary>このファイルのテストをランナーへ登録する。</summary>
    /// <param name="harness">登録先のテストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("F12: コメントの同じ単語ではなくメソッドの宣言へ飛ぶ",     FindsMethodDeclarationNotComment);
        harness.Add("F12: オーバーロードは引数の型で選ぶ",                     ChoosesOverloadByParameterTypes);
        harness.Add("F12: 引数の型が分からなければ最初の宣言へ飛ぶ",           FallsBackToFirstOverload);
        harness.Add("F12: プロパティ・フィールド・列挙子の宣言へ飛ぶ",         FindsPropertyFieldAndEnumMember);
        harness.Add("F12: コンストラクタとインデクサの宣言へ飛ぶ",             FindsConstructorAndIndexer);
        harness.Add("F12: 入れ子の型のメンバを外側の型のメンバと取り違えない", DoesNotLeakNestedTypeMembers);
        harness.Add("F12: メンバが無ければ型の宣言へ飛ぶ",                     FallsBackToTypeDeclaration);
        harness.Add("F12: 型が無ければ None（呼び出し側が別ファイルを探す）",  ReturnsNoneWhenTypeMissing);
        harness.Add("F12: 名前空間つきの型表記も同じ型として扱う",             NormalizesQualifiedTypeText);
        harness.Add("F12: 実物の GameObject.cs で Instantiate の宣言へ飛ぶ",   FindsInstantiateInRealEngineSource);
    }

    /// <summary>オフセットの位置から始まる 1 行を返す（どこへ飛んだかを読める形で確かめるため）。</summary>
    /// <param name="text">ソース。</param>
    /// <param name="offset">調べる位置。</param>
    private static string LineAt(string text, int offset)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        int end   = text.IndexOf('\n', offset);
        return text[start..(end < 0 ? text.Length : end)].Trim();
    }

    private static void FindsMethodDeclarationNotComment()
    {
        var found = DeclarationLocator.Find(Sample, "Thing", "Spawn", new[] { "string" });
        Check.Equal(DeclarationMatchKind.Member, found.Kind, "メンバに一致する");
        Check.Equal("public static Thing Spawn(string path)", LineAt(Sample, found.Offset), "飛び先の行");
        Check.True(Sample.Substring(found.Offset).StartsWith("Spawn(", StringComparison.Ordinal),
                   "キャレットは識別子の先頭に来る");
    }

    private static void ChoosesOverloadByParameterTypes()
    {
        var found = DeclarationLocator.Find(Sample, "Thing", "Spawn", new[] { "string", "Thing" });
        Check.Equal("public static Thing Spawn(string path, Thing parent)", LineAt(Sample, found.Offset),
                    "2 引数版へ飛ぶ");

        // 型の表記が合わなくても、個数が合う宣言を選ぶ
        var byCount = DeclarationLocator.Find(Sample, "Thing", "Spawn", new[] { "System.String", "Unknown" });
        Check.Equal("public static Thing Spawn(string path, Thing parent)", LineAt(Sample, byCount.Offset),
                    "個数で選ぶ");
    }

    private static void FallsBackToFirstOverload()
    {
        var found = DeclarationLocator.Find(Sample, "Thing", "Spawn", null);
        Check.Equal("public static Thing Spawn(string path)", LineAt(Sample, found.Offset), "最初の宣言");
    }

    private static void FindsPropertyFieldAndEnumMember()
    {
        Check.Equal("public string Name => \"x\";",
                    LineAt(Sample, DeclarationLocator.Find(Sample, "Thing", "Name").Offset), "プロパティ");
        Check.Equal("private readonly int _count;",
                    LineAt(Sample, DeclarationLocator.Find(Sample, "Thing", "_count").Offset), "フィールド");

        // 1 つの宣言に変数が 2 つあるフィールドは、その変数の識別子へ飛ぶ
        var other = DeclarationLocator.Find(Sample, "Thing", "Other");
        Check.True(Sample.Substring(other.Offset).StartsWith("Other;", StringComparison.Ordinal),
                   "2 つ目の変数の識別子");

        var green = DeclarationLocator.Find(Sample, "Color", "Green");
        Check.Equal(DeclarationMatchKind.Member, green.Kind, "列挙子に一致する");
        Check.True(Sample.Substring(green.Offset).StartsWith("Green", StringComparison.Ordinal), "列挙子");
    }

    private static void FindsConstructorAndIndexer()
    {
        var ctor = DeclarationLocator.Find(Sample, "Thing", DeclarationLocator.CONSTRUCTOR_NAME, new[] { "int" });
        Check.Equal("public Thing(int count) { _count = count; }", LineAt(Sample, ctor.Offset), "コンストラクタ");

        var indexer = DeclarationLocator.Find(Sample, "Thing", DeclarationLocator.INDEXER_NAME, new[] { "int" });
        Check.Equal("public int this[int index] => index;", LineAt(Sample, indexer.Offset), "インデクサ");
    }

    private static void DoesNotLeakNestedTypeMembers()
    {
        // 入れ子の enum Mode にも Spawn という列挙子があるが、Thing.Spawn はメソッドの方。
        var method = DeclarationLocator.Find(Sample, "Thing", "Spawn", new[] { "string" });
        Check.Equal("public static Thing Spawn(string path)", LineAt(Sample, method.Offset), "外側のメソッド");

        var enumMember = DeclarationLocator.Find(Sample, "Mode", "Spawn");
        Check.Equal("public enum Mode { Idle, Spawn }", LineAt(Sample, enumMember.Offset), "入れ子の列挙子");
    }

    private static void FallsBackToTypeDeclaration()
    {
        var found = DeclarationLocator.Find(Sample, "Thing", "NoSuchMember");
        Check.Equal(DeclarationMatchKind.Type, found.Kind, "型に一致する");
        Check.Equal("public readonly struct Thing", LineAt(Sample, found.Offset), "型の宣言の行");

        var typeOnly = DeclarationLocator.Find(Sample, "Thing", null);
        Check.Equal(DeclarationMatchKind.Type, typeOnly.Kind, "メンバ指定なしは型");
        Check.True(Sample.Substring(typeOnly.Offset).StartsWith("Thing", StringComparison.Ordinal), "型名の先頭");
    }

    private static void ReturnsNoneWhenTypeMissing()
    {
        Check.Equal(DeclarationMatchKind.None, DeclarationLocator.Find(Sample, "Nope", "Spawn").Kind, "型が無い");
        Check.Equal(DeclarationMatchKind.None, DeclarationLocator.Find("", "Thing", null).Kind, "空のソース");
        Check.Equal(DeclarationMatchKind.None, DeclarationLocator.Find(Sample, null, null).Kind, "型名なし");
    }

    private static void NormalizesQualifiedTypeText()
    {
        Check.Equal("List<int>", DeclarationLocator.NormalizeTypeText("System.Collections.Generic.List<int>"), "名前空間を落とす");
        Check.Equal("Vector3", DeclarationLocator.NormalizeTypeText("global::SEED.Vector3"), "別名修飾を落とす");
        Check.Equal("Dictionary<string,System.Int32>",
                    DeclarationLocator.NormalizeTypeText("Dictionary<string, System.Int32>"), "ジェネリクスの中は触らない");
        Check.Equal("int[]", DeclarationLocator.NormalizeTypeText(" int [] "), "空白を除く");
    }

    /// <summary>
    /// 報告された実例そのもの。リポジトリの scripting/src/Api/GameObject.cs を読み、
    /// Instantiate(string) の宣言へ飛ぶこと（summary の &lt;c&gt;Instantiate&lt;/c&gt; ではなく）。
    /// </summary>
    private static void FindsInstantiateInRealEngineSource()
    {
        var path = FindRepoFile(Path.Combine("scripting", "src", "Api", "GameObject.cs"));
        Check.True(path is not null, "scripting/src/Api/GameObject.cs が見つかる");
        var text = File.ReadAllText(path!);

        var one = DeclarationLocator.Find(text, "GameObject", "Instantiate", new[] { "string" });
        Check.Equal(DeclarationMatchKind.Member, one.Kind, "メンバに一致する");
        Check.Equal("public static GameObject Instantiate(string actorPath)", LineAt(text, one.Offset),
                    "1 引数版の宣言の行");

        var two = DeclarationLocator.Find(text, "GameObject", "Instantiate", new[] { "string", "GameObject" });
        Check.Equal("public static GameObject Instantiate(string actorPath, GameObject parent)",
                    LineAt(text, two.Offset), "2 引数版の宣言の行");
    }

    /// <summary>実行ディレクトリから親を遡って、リポジトリ内のファイルを探す。</summary>
    /// <param name="relative">リポジトリルートからの相対パス。</param>
    private static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
