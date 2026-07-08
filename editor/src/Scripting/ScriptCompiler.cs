using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SEEDEditor.Scripting;

namespace SEEDEditor.Scripting;

/// <summary>
/// エディタ内でのスクリプトコンパイル（インスペクタ表示・保存時の検証用）。
///
/// ユーザースクリプトはランタイム側と同じ SEEDEditor.Scripting.SEEDScript を
/// 継承する。エディタは SEEDScripting.dll を参照してコンパイルするため、
/// ランタイム（CLR ホスト側の Roslyn コンパイル）と同一の型体系で検証できる。
/// </summary>
public static class ScriptCompiler
{
    private static readonly List<MetadataReference> _refs = BuildRefs();

    /// <summary>
    /// メタデータ参照の構築（全ロード済みアセンブリの列挙＋メタデータ読み込み）を先に済ませておく。
    /// 静的フィールド _refs は初回コンパイル時に初期化されるため、それを起動時に
    /// バックグラウンドで先取りしておくと、最初のスクリプト選択時のコンパイルが速くなる。
    /// </summary>
    public static void WarmUp() => _ = _refs.Count;

    private static List<MetadataReference> BuildRefs()
    {
        // SEEDScripting.dll のロードを強制する（参照アセンブリは遅延ロードのため）
        _ = typeof(SEEDScript).Assembly;

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
    }

    /// <summary>
    /// 単一の .cs ファイルをコンパイルし、SEEDScript 派生型を返す。
    /// エラー時は (null, エラーメッセージ一覧)。
    /// </summary>
    public static (Type? scriptType, IReadOnlyList<string> errors) CompileFile(string filePath)
    {
        var source = File.ReadAllText(filePath);
        var tree   = CSharpSyntaxTree.ParseText(source);
        var comp   = CSharpCompilation.Create(
            $"SEEDScript_{Guid.NewGuid():N}",
            [tree], _refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result   = comp.Emit(ms);

        if (!result.Success)
        {
            return (null, result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"({d.Location.GetLineSpan().StartLinePosition.Line + 1}行目) {d.GetMessage()}")
                .ToList());
        }

        var asm = Assembly.Load(ms.ToArray());
        var t   = asm.GetTypes().FirstOrDefault(t =>
            !t.IsAbstract && typeof(IScriptComponent).IsAssignableFrom(t));
        return t is not null
            ? (t, Array.Empty<string>())
            : (null, ["SEEDScript (SEEDEditor.Scripting) を継承したクラスがスクリプト内に見つかりません"]);
    }

    /// <summary>
    /// アセットルート配下の全 .cs をランタイムと同じ条件で一括コンパイルし、
    /// エラーメッセージ一覧を返す（成功時は空リスト）。
    ///
    /// ランタイム（ScriptAssemblyManager）は全 .cs を 1 アセンブリにまとめて
    /// コンパイルするため、型名の重複などファイル横断のエラーがあると
    /// **全スクリプトが実行されなくなる**。単一ファイルコンパイル
    /// （CompileFile）では検出できないため、保存時の検証にこちらを併用する。
    /// </summary>
    public static IReadOnlyList<string> CompileProjectErrors(string assetsRoot)
    {
        try
        {
            if (!Directory.Exists(assetsRoot)) return Array.Empty<string>();

            var trees = new List<SyntaxTree>();
            foreach (var f in Directory.EnumerateFiles(assetsRoot, "*.cs", SearchOption.AllDirectories))
            {
                try { trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f)); }
                catch { /* 読めない/壊れたファイルはスキップ（ランタイム側と同じ扱い） */ }
            }
            if (trees.Count == 0) return Array.Empty<string>();

            var comp = CSharpCompilation.Create(
                $"SEEDScriptProj_{Guid.NewGuid():N}",
                trees, _refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new MemoryStream();
            var result   = comp.Emit(ms);
            if (result.Success) return Array.Empty<string>();

            return result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d =>
                {
                    var span = d.Location.GetLineSpan();
                    var file = string.IsNullOrEmpty(span.Path) ? "(不明)" : Path.GetFileName(span.Path);
                    return $"{file}({span.StartLinePosition.Line + 1}行目): {d.GetMessage()}";
                })
                .ToList();
        }
        catch
        {
            // 検証自体の失敗は保存をブロックしない（ランタイム側が最終判定する）
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// アセットルート配下の全 .cs をまとめてコンパイルし、指定ファイルが宣言する
    /// スクリプト型を返す。他スクリプトを typeof で参照する [RequireComponent] などは
    /// 単一ファイルコンパイルでは解決できないため、こちらを使う。失敗時は null。
    /// </summary>
    public static Type? ResolveScriptTypeInProject(string filePath, string assetsRoot)
    {
        try
        {
            if (!Directory.Exists(assetsRoot)) return null;

            var trees = new List<(string path, SyntaxTree tree)>();
            foreach (var f in Directory.EnumerateFiles(assetsRoot, "*.cs", SearchOption.AllDirectories))
            {
                try { trees.Add((f, CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))); }
                catch { /* 読めない/壊れたファイルはスキップ */ }
            }
            if (trees.Count == 0) return null;

            var comp = CSharpCompilation.Create(
                $"SEEDScriptProj_{Guid.NewGuid():N}",
                trees.Select(t => t.tree), _refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new MemoryStream();
            if (!comp.Emit(ms).Success) return null;

            var asm = Assembly.Load(ms.ToArray());

            // 対象ファイルが宣言するクラス名を取得し、その型を解決する
            var targetFull = Path.GetFullPath(filePath);
            var target = trees.FirstOrDefault(t =>
                string.Equals(Path.GetFullPath(t.path), targetFull, StringComparison.OrdinalIgnoreCase));
            if (target.tree is null) return null;

            var classNames = ClassNamesOf(target.tree);
            return asm.GetTypes().FirstOrDefault(t =>
                !t.IsAbstract && typeof(IScriptComponent).IsAssignableFrom(t) && classNames.Contains(t.Name));
        }
        catch { return null; }
    }

    /// <summary>
    /// 指定スクリプト型名を宣言する .cs ファイルをアセットルート配下から探す。
    /// まず「型名.cs」を優先し、無ければ全 .cs を走査して class 宣言を照合する。
    /// </summary>
    public static string? FindScriptFile(string typeName, string assetsRoot)
    {
        try
        {
            if (!Directory.Exists(assetsRoot)) return null;

            var direct = Directory
                .EnumerateFiles(assetsRoot, typeName + ".cs", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (direct is not null) return direct;

            foreach (var f in Directory.EnumerateFiles(assetsRoot, "*.cs", SearchOption.AllDirectories))
            {
                try
                {
                    if (ClassNamesOf(CSharpSyntaxTree.ParseText(File.ReadAllText(f))).Contains(typeName))
                        return f;
                }
                catch { /* スキップ */ }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>構文木からクラス宣言名の集合を取得する。</summary>
    private static HashSet<string> ClassNamesOf(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
            .Select(c => c.Identifier.Text)
            .ToHashSet();

    /// <summary>[RequireComponent] 1 件の要求内容（型名またはネイティブ名のいずれか）。</summary>
    public readonly record struct RequireInfo(string? ComponentName, string? ScriptTypeName);

    /// <summary>スクリプト型の [RequireComponent] 一覧を読み取る。</summary>
    public static IReadOnlyList<RequireInfo> GetRequiredComponents(Type scriptType)
    {
        var list = new List<RequireInfo>();
        foreach (var a in scriptType.GetCustomAttributesData()
            .Where(a => a.AttributeType.Name == nameof(RequireComponentAttribute)))
        {
            if (a.ConstructorArguments.Count == 0) continue;
            var arg = a.ConstructorArguments[0];
            if (arg.Value is Type t)          list.Add(new RequireInfo(null, t.Name));
            else if (arg.Value is string s)   list.Add(new RequireInfo(s, null));
        }
        return list;
    }

    /// <summary>スクリプト型に [DisallowMultipleComponent] が付いているか。</summary>
    public static bool HasDisallowMultiple(Type scriptType) =>
        scriptType.GetCustomAttributesData()
            .Any(a => a.AttributeType.Name == nameof(DisallowMultipleComponentAttribute));

    /// <summary>
    /// コンパイル済み型から [SerializeField] フィールド一覧を抽出する。
    /// [Serializable] なネストクラス型のフィールドは再帰的に子フィールドを展開する。
    /// </summary>
    public static IReadOnlyList<ScriptFieldInfo> GetSerializeFields(Type scriptType)
        => ExtractFields(scriptType, depth: 0);

    // ネスト展開の再帰上限（循環参照・過度な深さによる無限ループを防ぐ安全弁）
    private const int MaxNestDepth = 8;

    /// <summary>指定型の [SerializeField] フィールドを抽出する（ネスト再帰用）。</summary>
    private static IReadOnlyList<ScriptFieldInfo> ExtractFields(Type type, int depth)
    {
        object? inst = null;
        try { inst = Activator.CreateInstance(type); } catch { }

        return type
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => HasSerializeField(f))
            .Select(f => BuildFieldInfo(f, inst, depth))
            .ToList();
    }

    /// <summary>フィールド 1 件分の ScriptFieldInfo を生成する（属性・ネストを解釈）。</summary>
    private static ScriptFieldInfo BuildFieldInfo(FieldInfo f, object? owner, int depth)
    {
        var (label, sfTooltip)   = ReadSerializeField(f);
        var tooltip              = ReadTooltip(f) ?? sfTooltip;   // 独立 [Tooltip] を優先
        var header               = ReadHeader(f);
        var (rangeMin, rangeMax) = ReadRange(f);
        var defValue             = owner is not null ? f.GetValue(owner) : null;

        // [Serializable] なネストクラスなら子フィールドを再帰展開する
        IReadOnlyList<ScriptFieldInfo>? children = null;
        if (depth < MaxNestDepth && IsNestedSerializable(f.FieldType))
            children = ExtractFields(f.FieldType, depth + 1);

        return new ScriptFieldInfo(f, label ?? PrettifyName(f.Name), tooltip, defValue)
        {
            Header   = header,
            RangeMin = rangeMin,
            RangeMax = rangeMax,
            Children = children,
        };
    }

    /// <summary>
    /// [Serializable] が付いた、インスペクタで展開すべきネストクラス型かを判定する。
    /// プリミティブ・string・列挙型・配列などは対象外。
    /// </summary>
    private static bool IsNestedSerializable(Type t)
    {
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t.IsArray) return false;
        if (!t.IsClass && !(t.IsValueType && !t.IsPrimitive)) return false;
        // System.SerializableAttribute の有無を名前で判定（アセンブリ ID 差異を吸収）
        return t.GetCustomAttributesData()
            .Any(a => a.AttributeType.Name == "SerializableAttribute");
    }

    /// <summary>
    /// [SerializeField] 属性の有無を型名で判定する。
    /// エディタの Roslyn コンパイルとランタイムの ALC コンパイルでは属性の
    /// アセンブリ ID が異なる場合があるため、GetCustomAttribute の型一致ではなく
    /// 属性名で照合する。
    /// </summary>
    private static bool HasSerializeField(FieldInfo f) =>
        f.GetCustomAttributesData().Any(a => a.AttributeType.Name == nameof(SerializeFieldAttribute));

    /// <summary>[SerializeField] の Label / Tooltip を属性データから読み取る。</summary>
    private static (string? label, string? tooltip) ReadSerializeField(FieldInfo f)
    {
        var data = f.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.Name == nameof(SerializeFieldAttribute));
        if (data is null) return (null, null);

        string? label = null, tooltip = null;
        foreach (var na in data.NamedArguments)
        {
            if (na.MemberName == "Label")   label   = na.TypedValue.Value as string;
            if (na.MemberName == "Tooltip") tooltip = na.TypedValue.Value as string;
        }
        return (label, tooltip);
    }

    /// <summary>独立した [Tooltip("...")] 属性の文言を読み取る（無ければ null）。</summary>
    private static string? ReadTooltip(FieldInfo f)
    {
        var data = f.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.Name == nameof(TooltipAttribute));
        return data?.ConstructorArguments.Count > 0
            ? data.ConstructorArguments[0].Value as string
            : null;
    }

    /// <summary>[Header("...")] 属性の見出し文言を読み取る（無ければ null）。</summary>
    private static string? ReadHeader(FieldInfo f)
    {
        var data = f.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.Name == nameof(HeaderAttribute));
        return data?.ConstructorArguments.Count > 0
            ? data.ConstructorArguments[0].Value as string
            : null;
    }

    /// <summary>[Range(min, max)] 属性の範囲を読み取る（無ければ (null, null)）。</summary>
    private static (float? min, float? max) ReadRange(FieldInfo f)
    {
        var data = f.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.Name == nameof(RangeAttribute));
        if (data is null || data.ConstructorArguments.Count < 2) return (null, null);
        try
        {
            var min = Convert.ToSingle(data.ConstructorArguments[0].Value);
            var max = Convert.ToSingle(data.ConstructorArguments[1].Value);
            return (min, max);
        }
        catch { return (null, null); }
    }

    private static string PrettifyName(string name)
    {
        if (name.StartsWith('_')) name = name[1..];
        if (name.Length == 0)    return name;
        return char.ToUpper(name[0]) + name[1..];
    }
}
