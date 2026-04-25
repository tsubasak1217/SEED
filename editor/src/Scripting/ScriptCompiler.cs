using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SEEDEditor.Scripting;

public static class ScriptCompiler
{
    private static readonly List<MetadataReference> _refs = BuildRefs();

    private static List<MetadataReference> BuildRefs() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

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
                .Select(d => d.GetMessage())
                .ToList());
        }

        var asm = Assembly.Load(ms.ToArray());
        var t   = asm.GetTypes().FirstOrDefault(t => t.IsSubclassOf(typeof(SEEDScript)));
        return t is not null
            ? (t, Array.Empty<string>())
            : (null, ["SEEDScript を継承したクラスがスクリプト内に見つかりません"]);
    }

    public static IReadOnlyList<ScriptFieldInfo> GetSerializeFields(Type scriptType)
    {
        object? inst = null;
        try { inst = Activator.CreateInstance(scriptType); } catch { }

        return scriptType
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.GetCustomAttribute<SerializeFieldAttribute>() is not null)
            .Select(f =>
            {
                var attr     = f.GetCustomAttribute<SerializeFieldAttribute>()!;
                var label    = attr.Label ?? PrettifyName(f.Name);
                var defValue = inst is not null ? f.GetValue(inst) : null;
                return new ScriptFieldInfo(f, label, attr.Tooltip, defValue);
            })
            .ToList();
    }

    private static string PrettifyName(string name)
    {
        if (name.StartsWith('_')) name = name[1..];
        if (name.Length == 0)    return name;
        return char.ToUpper(name[0]) + name[1..];
    }
}
