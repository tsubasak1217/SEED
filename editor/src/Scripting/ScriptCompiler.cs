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
    /// コンパイル済み型から [SerializeField] フィールド一覧を抽出する。
    /// </summary>
    public static IReadOnlyList<ScriptFieldInfo> GetSerializeFields(Type scriptType)
    {
        object? inst = null;
        try { inst = Activator.CreateInstance(scriptType); } catch { }

        return scriptType
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => HasSerializeField(f))
            .Select(f =>
            {
                var (label, tooltip) = ReadSerializeField(f);
                var defValue = inst is not null ? f.GetValue(inst) : null;
                return new ScriptFieldInfo(f, label ?? PrettifyName(f.Name), tooltip, defValue);
            })
            .ToList();
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

    private static string PrettifyName(string name)
    {
        if (name.StartsWith('_')) name = name[1..];
        if (name.Length == 0)    return name;
        return char.ToUpper(name[0]) + name[1..];
    }
}
