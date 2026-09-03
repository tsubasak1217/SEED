using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace SEEDEditor.Scripting;

/// <summary>
/// ユーザースクリプト（アセットフォルダ内の .cs）のコンパイルとロードを管理する。
///
/// 【設計】
/// - 全 .cs を 1 つのアセンブリにまとめて Roslyn でコンパイルする
/// - collectible な AssemblyLoadContext に読み込むことで、
///   再コンパイル時に旧アセンブリをアンロードできる（＝ホットリロード）
/// - .cs ファイルパス → スクリプト型 のマッピングを保持し、
///   シーンファイルに保存されたパスから型を解決する
///
/// 【注意】
/// Reload 前に旧アセンブリ型のインスタンス（GCHandle）をすべて解放しておくこと。
/// 生存インスタンスが残っていると ALC のアンロードが完了しない。
/// </summary>
public static class ScriptAssemblyManager
{
    /// <summary>現在ロード中のスクリプトアセンブリのロードコンテキスト。</summary>
    private static AssemblyLoadContext? _context;

    /// <summary>現在ロード中のスクリプトアセンブリ。</summary>
    private static Assembly? _assembly;

    /// <summary>正規化済みフルパス（小文字）→ スクリプト型。</summary>
    private static readonly Dictionary<string, Type> _typeByPath = new();

    /// <summary>ファイル名（例 "MyScript.cs"、小文字）→ スクリプト型。パス表記ゆれのフォールバック用。</summary>
    private static readonly Dictionary<string, Type> _typeByFileName = new();

    /// <summary>コンパイル参照。ホスト側にロード済みの全アセンブリ（SEEDScripting 自身を含む）。</summary>
    private static List<MetadataReference> BuildReferences() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

    /// <summary>
    /// assetsRoot 配下の全 .cs をコンパイルしてロードする。
    /// 既存アセンブリがあればアンロードして置き換える。
    ///
    /// 戻り値: コンパイルされたスクリプト型の数。
    ///         コンパイルエラー時は -1（旧アセンブリは維持され、エラーは stderr に出力）。
    /// </summary>
    public static int CompileAndLoad(string assetsRoot)
    {
        List<string> files;
        try
        {
            files = Directory.Exists(assetsRoot)
                ? Directory.EnumerateFiles(assetsRoot, "*.cs", SearchOption.AllDirectories).ToList()
                : new List<string>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SEEDScripting] script enumeration failed: {ex.Message}");
            return -1;
        }

        // スクリプトが 1 つも無い場合は空状態にして正常終了する
        if (files.Count == 0)
        {
            Unload();
            return 0;
        }

        // ── 構文解析（ファイルパスを tree に紐付け、後で パス→型 を作る）──
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>();
        foreach (var file in files)
        {
            try
            {
                // 埋め込み PDB を発行するにはソーステキストにエンコーディングが必要。
                // UTF-8 を明示することで VS デバッグ時のソースマッピングが有効になる。
                var text = SourceText.From(File.ReadAllText(file), System.Text.Encoding.UTF8);
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SEEDScripting] cannot read '{file}': {ex.Message}");
            }
        }

        // ── コンパイル ──
        var compilation = CSharpCompilation.Create(
            $"SEEDUserScripts_{Guid.NewGuid():N}",
            trees,
            BuildReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                // ユーザースクリプトの `PlayerMove?` 等の null 許容注釈をメタデータへ出力させる
                // （警告は出さない）。エディタ側 ScriptCompiler と同一条件にすること。
                .WithNullableContextOptions(NullableContextOptions.Annotations)
                .WithOptimizationLevel(OptimizationLevel.Debug));

        // 埋め込み PDB 付きで発行する。ソースツリーにファイルパスを設定しているため、
        // Visual Studio を SEED.exe にアタッチするとスクリプトの .cs にブレークポイントを
        // 張ってデバッグできる（PE 内にシンボルが含まれるので追加ファイル不要）。
        using var ms = new MemoryStream();
        var emitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded);
        var result = compilation.Emit(ms, options: emitOptions);
        if (!result.Success)
        {
            foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                Console.Error.WriteLine($"[ScriptCompileError] {d.Location.GetLineSpan().Path}({d.Location.GetLineSpan().StartLinePosition.Line + 1}): {d.GetMessage()}");
            // 旧アセンブリを維持して呼び出し側にエラーを伝える
            return -1;
        }

        // ── 旧アセンブリをアンロードし、新アセンブリをロードする ──
        Unload();
        ms.Position = 0;
        _context  = new AssemblyLoadContext("SEEDUserScripts", isCollectible: true);

        // ユーザースクリプトが参照するホスト側アセンブリ（SEEDScripting 本体や
        // その依存 DLL）は、すでにこのプロセスにロード済みである。ただし SEEDScripting は
        // Rust ランタイムが hostfxr（load_assembly_and_get_function_pointer）経由で
        // Default とは別の独立した AssemblyLoadContext にロードしているため、
        // collectible ALC の既定のフォールバック（Default ALC）では名前解決できず
        // FileNotFoundException になる。
        // そこで Resolving で、プロセス内にロード済みの同名アセンブリ（ALC を問わず
        // AppDomain 全体から検索）へフォールバックさせる。これで基底クラス SEEDScript や
        // SEED.* API を含む SEEDScripting をユーザーアセンブリから参照できる。
        _context.Resolving += (ctx, name) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name.Name);

        _assembly = _context.LoadFromStream(ms);

        // ── パス → 型 マッピングを構築する ──
        // 各ソースファイルで宣言されたクラス名と、コンパイル済み型を突き合わせる。
        var scriptTypes = _assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IScriptComponent).IsAssignableFrom(t))
            .ToList();
        var typeByName = scriptTypes
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var tree in trees)
        {
            // このファイル内で宣言されたクラス名のうち、スクリプト型に一致する最初のものを採用する
            var classNames = tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .Select(c => c.Identifier.Text);
            foreach (var name in classNames)
            {
                if (!typeByName.TryGetValue(name, out var type)) continue;
                var fullPath = NormalizePath(tree.FilePath);
                _typeByPath[fullPath] = type;
                _typeByFileName[Path.GetFileName(fullPath)] = type;
                break;
            }
        }

        Console.WriteLine($"[SEEDScripting] compiled {scriptTypes.Count} script type(s) from {files.Count} file(s)");
        return scriptTypes.Count;
    }

    /// <summary>
    /// 型名または .cs ファイルパスからスクリプト型を解決する。
    /// 優先順: パス完全一致 → ファイル名一致 → ユーザーアセンブリ内の型名 → 全ロード済みアセンブリの型名。
    /// </summary>
    public static Type? Resolve(string nameOrPath)
    {
        if (nameOrPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = NormalizePath(nameOrPath);
            if (_typeByPath.TryGetValue(normalized, out var byPath)) return byPath;
            if (_typeByFileName.TryGetValue(Path.GetFileName(normalized), out var byFile)) return byFile;
            return null;
        }

        // 型名指定: ユーザースクリプトアセンブリを優先して検索する
        if (_assembly is not null)
        {
            var t = _assembly.GetTypes().FirstOrDefault(t => t.FullName == nameOrPath || t.Name == nameOrPath);
            if (t is not null) return t;
        }

        // フォールバック: SEEDScripting 自身などロード済みアセンブリから検索する
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
            .FirstOrDefault(t => t.FullName == nameOrPath || t.Name == nameOrPath);
    }

    /// <summary>現在のスクリプトアセンブリをアンロードし、マッピングをクリアする。</summary>
    private static void Unload()
    {
        _typeByPath.Clear();
        _typeByFileName.Clear();
        _assembly = null;
        _context?.Unload();
        _context = null;
    }

    /// <summary>Windows のパス表記ゆれ（区切り・大文字小文字）を正規化する。</summary>
    private static string NormalizePath(string path)
    {
        try { path = Path.GetFullPath(path); } catch { }
        return path.Replace('/', '\\').ToLowerInvariant();
    }
}
