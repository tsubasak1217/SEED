// ============================================================
//  ScriptSourceCompiler.cs — ユーザースクリプトのコンパイル共通実装
//
//  【役割】
//  「アセットルート配下の .cs を集める → 構文解析する → CSharpCompilation を作る →
//    ソースファイル と スクリプト型 の対応を求める」までを 1 か所に持つ。
//
//  【なぜ分離したか】
//  同じ工程を 2 つの入口が使う。
//    ① ScriptAssemblyManager.CompileAndLoad  … メモリ上へ出してその場でロード（エディタ／Play）
//    ② ScriptAssemblyManager.CompileToFile   … DLL ファイルへ出力（パッケージ化）
//  コンパイル条件（言語バージョン・null 許容・型判定）が両者でずれると
//  「エディタでは動くのにパッケージ版だけ落ちる」形で表面化するため、条件は
//  この 1 ファイルだけが持つ。
//
//  【型の対応をリフレクションで取らない理由】
//  ②はアセンブリをロードせずに型マップを作る必要がある（ロードするとファイルを
//  掴んでしまうし、参照解決も要る）。そこで Roslyn のセマンティックモデルから
//  「IScriptComponent を実装する具象クラス」を判定する。①も同じ経路にすることで、
//  両者の判定規則が一致することを構造的に保証する。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SEEDEditor.Scripting.Compilation;

/// <summary>1 つのソースファイルに対応するスクリプト型のエントリ。</summary>
/// <param name="AssetKey">アセットルート相対の正規キー（例 "ui/title.cs"）。</param>
/// <param name="SourcePath">ソースファイルの絶対パス（エディタ側の完全一致解決用）。</param>
/// <param name="MetadataName">型のメタデータ名（例 "Game.Title"、ネストは "Outer+Inner"）。</param>
public readonly record struct ScriptTypeEntry(string AssetKey, string SourcePath, string MetadataName);

/// <summary>ユーザースクリプトのコンパイル工程（収集・解析・型対応）の共通実装。</summary>
public static class ScriptSourceCompiler
{
    // ── コンパイル条件（①②で共有する唯一の定義）───────────────

    /// <summary>探索するファイルの拡張子パターン（C# ソースのみ）。</summary>
    private const string ScriptSearchPattern = "*.cs";

    /// <summary>スクリプト型と見なす基底インターフェースのメタデータ名。</summary>
    private const string ScriptComponentInterfaceName = "SEEDEditor.Scripting.IScriptComponent";

    /// <summary>診断メッセージの整形書式（パス・行番号・内容）。</summary>
    private const string DiagnosticFormat = "{0}({1}): {2}";

    /// <summary>Roslyn の行番号は 0 始まりなので表示時に足す量。</summary>
    private const int LineNumberOffset = 1;

    /// <summary>ネスト型をメタデータ名で連結する区切り。</summary>
    private const string NestedTypeSeparator = "+";

    /// <summary>名前空間と型名を連結する区切り。</summary>
    private const string NamespaceSeparator = ".";

    // ── スクリプトファイルの収集 ─────────────────────────────

    /// <summary>
    /// アセットルート配下の .cs を再帰的に集める【スクリプト収集の唯一の実装】。
    ///
    /// <para>
    /// <b>なぜ <c>SearchOption.AllDirectories</c> を使わないのか</b><br/>
    /// 1 回の列挙で全階層をなめる書き方だと、途中に「開けないフォルダ」が
    /// 1 つでもあった時点で例外が飛び、<b>プロジェクト全体のスクリプトが
    /// 1 本もコンパイルされなくなる</b>（＝ゲームのスクリプトが全滅する）。
    /// 実際に、削除済みフォルダがハンドル保持で消えきらず ACL が読めない状態になり、
    /// 全スクリプトが起動しない不具合が起きた。
    /// </para>
    /// <para>
    /// そこでフォルダ単位に <c>try/catch</c> を掛けて幅優先で自前に降り、
    /// 読めないフォルダはその 1 つだけを警告して読み飛ばす。
    /// アセットの一部が読めなくても、残りのスクリプトは正しく動く。
    /// </para>
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <returns>見つかった .cs の絶対パス一覧（読めなかったフォルダの中身は含まない）。</returns>
    public static List<string> CollectScriptFiles(string assetsRoot)
    {
        var files = new List<string>();
        if (string.IsNullOrEmpty(assetsRoot) || !Directory.Exists(assetsRoot)) return files;

        // 幅優先で自前に降りる（再帰だと深いツリーでスタックを消費するため）
        var pending = new Queue<string>();
        pending.Enqueue(assetsRoot);

        while (pending.Count > 0)
        {
            var dir = pending.Dequeue();

            // このフォルダ直下のファイル（読めなければこのフォルダだけ諦める）
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir, ScriptSearchPattern));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[SEEDScripting] skip unreadable folder (files) [{dir}]: {ex.Message}");
                continue;   // 中身が読めないフォルダは子も辿らない
            }

            // 子フォルダ（読めなければこのフォルダの子は諦める）
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir)) pending.Enqueue(sub);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[SEEDScripting] skip unreadable folder (subdirs) [{dir}]: {ex.Message}");
            }
        }

        return files;
    }

    // ── 構文解析とコンパイル生成 ─────────────────────────────

    /// <summary>
    /// 収集済みソースを構文解析して <see cref="CSharpCompilation"/> を組み立てる。
    /// </summary>
    /// <param name="assemblyName">生成するアセンブリの単純名。</param>
    /// <param name="files">ソースファイルの絶対パス一覧。</param>
    /// <param name="references">参照アセンブリ。</param>
    /// <param name="optimization">最適化レベル（Play は Debug、配布は Release）。</param>
    /// <returns>組み立てた compilation。読めなかったファイルは含まれない。</returns>
    public static CSharpCompilation CreateCompilation(
        string                          assemblyName,
        IEnumerable<string>             files,
        IEnumerable<MetadataReference>  references,
        OptimizationLevel               optimization)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees        = new List<SyntaxTree>();

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
                Console.Error.WriteLine($"[SEEDScripting] cannot read [{file}]: {ex.Message}");
            }
        }

        return CSharpCompilation.Create(
            assemblyName,
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                // ユーザースクリプトの `PlayerMove?` 等の null 許容注釈をメタデータへ出力させる
                // （警告は出さない）。エディタ側 ScriptCompiler と同一条件にすること。
                .WithNullableContextOptions(NullableContextOptions.Annotations)
                .WithOptimizationLevel(optimization));
    }

    // ── ソースファイル → スクリプト型 の対応 ──────────────────

    /// <summary>
    /// 各ソースファイルで最初に宣言された「具象スクリプト型」を求める。
    /// 1 ファイルに複数のスクリプト型があっても、パス解決に使うのは先頭の 1 つだけ
    /// （残りは型名指定で解決できる。従来の CompileAndLoad と同じ規則）。
    /// </summary>
    /// <param name="compilation">解析済み compilation。</param>
    /// <param name="assetsRoot">アセットルートの絶対パス（相対キーの基準）。</param>
    /// <returns>ソースファイルごとの型エントリ。</returns>
    public static List<ScriptTypeEntry> MapScriptTypes(CSharpCompilation compilation, string assetsRoot)
    {
        var entries = new List<ScriptTypeEntry>();

        // 基底インターフェースが解決できない＝ SEEDScripting.dll が参照に無い。
        // その場合は 1 件も判定できないので空を返す（呼び出し側がエラーとして扱う）。
        var scriptInterface = compilation.GetTypeByMetadataName(ScriptComponentInterfaceName);
        if (scriptInterface is null) return entries;

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);

            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol symbol) continue;

                // 生成できない型（抽象・静的・ジェネリック）はスクリプトとして扱わない
                if (symbol.IsAbstract || symbol.IsStatic || symbol.IsGenericType) continue;
                if (!ImplementsInterface(symbol, scriptInterface)) continue;

                entries.Add(new ScriptTypeEntry(
                    ScriptAssetPath.KeyFromAbsolute(assetsRoot, tree.FilePath),
                    tree.FilePath,
                    MetadataFullName(symbol)));
                break;   // このファイルの代表は 1 つ
            }
        }

        return entries;
    }

    /// <summary>指定インターフェースを（継承経由も含めて）実装しているかを判定する。</summary>
    /// <param name="symbol">対象の型シンボル。</param>
    /// <param name="target">判定するインターフェース。</param>
    /// <returns>実装していれば true。</returns>
    private static bool ImplementsInterface(INamedTypeSymbol symbol, INamedTypeSymbol target) =>
        symbol.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, target));

    /// <summary>
    /// リフレクション（<c>Assembly.GetType</c>）で引ける完全名を組み立てる。
    /// ネスト型は + 、名前空間は . で連結する。
    /// </summary>
    /// <param name="symbol">対象の型シンボル。</param>
    /// <returns>メタデータ完全名。</returns>
    private static string MetadataFullName(INamedTypeSymbol symbol)
    {
        var name = symbol.MetadataName;
        for (var outer = symbol.ContainingType; outer is not null; outer = outer.ContainingType)
            name = outer.MetadataName + NestedTypeSeparator + name;

        var ns = symbol.ContainingNamespace;
        if (ns is { IsGlobalNamespace: false })
            name = ns.ToDisplayString() + NamespaceSeparator + name;

        return name;
    }

    // ── 診断の整形 ───────────────────────────────────────────

    /// <summary>
    /// 診断を「パス(行): 内容」の 1 行へ整形する。
    /// </summary>
    /// <param name="diagnostic">Roslyn の診断。</param>
    /// <returns>整形済みメッセージ。</returns>
    public static string FormatDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return string.Format(
            DiagnosticFormat,
            span.Path,
            span.StartLinePosition.Line + LineNumberOffset,
            diagnostic.GetMessage());
    }
}
