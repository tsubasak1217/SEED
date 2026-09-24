// ============================================================
//  ScriptAssemblyEmitter.cs — ユーザースクリプトのコンパイル（Roslyn を使う部分だけ）
//
//  【役割】
//  アセットルート配下の .cs を Roslyn でコンパイルし、アセンブリの中身（メモリ上）か DLL ファイルを作る。
//    ① EmitInMemory  … メモリ上へ出す（エディタ／Play のその場コンパイル。ロードは ScriptAssemblyManager）
//    ② CompileToFile … DLL ファイルへ出す（パッケージ化。型マップをマニフェストリソースとして焼き込む）
//  コンパイル条件そのもの（収集・言語バージョン・型判定）は ScriptSourceCompiler が唯一の定義。
//
//  【なぜ ScriptAssemblyManager から分けたのか】
//  事前コンパイル DLL を読むだけの経路（配布物・Android の同梱 .NET）では Roslyn（Microsoft.CodeAnalysis*.dll）が
//  解決できないことがある（Android は SEEDScripting.dll をバイト列から読むので deps.json による依存解決が無い）。
//  C# コンパイラはラムダやメソッドグループの委譲を、宣言したクラスごとの隠れた入れ子クラス（<>c・<>O）の
//  静的フィールドにまとめて持つ。Roslyn の型を使うラムダ（Func<Diagnostic, bool> 等）と、読むだけの経路の
//  ラムダが同じクラスにあると、読むだけの経路でもその入れ子クラスが読み込まれ、Mono はフィールドの型を
//  先に解決するため Roslyn を探しに行って TypeLoadException になった（Android の Mono で確認。docs/android.md §17）。
//  CoreCLR は遅延して解決するので今は動くが、同じ理由でいつ壊れてもおかしくない。
//  そこで Roslyn に触れるコードはすべてこのクラスへ集め、ScriptAssemblyManager（読む・解決する側）からは
//  Roslyn の型が一切見えないようにした。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

namespace SEEDEditor.Scripting.Compilation;

/// <summary>メモリ上へのコンパイル（その場コンパイル）の結果。Roslyn の型を含まない。</summary>
public sealed class ScriptInMemoryBuild
{
    /// <summary>対象になった .cs の数（0 ならスクリプトが無い＝空の状態にする）。</summary>
    public int SourceFileCount { get; init; }

    /// <summary>コンパイルに成功したか（失敗時のエラーは stderr へ出し済み）。</summary>
    public bool Success { get; init; }

    /// <summary>アセンブリの中身（埋め込み PDB 付き）。失敗・ソース無しなら null。</summary>
    public MemoryStream? Image { get; init; }

    /// <summary>ソースファイルごとのスクリプト型（ロード後に実型へ引き当てる）。</summary>
    public IReadOnlyList<ScriptTypeEntry> Entries { get; init; } = [];
}

/// <summary>ユーザースクリプトのコンパイル（Roslyn を使う部分）。</summary>
public static class ScriptAssemblyEmitter
{
    /// <summary>その場コンパイル時のアセンブリ名の接頭辞（毎回ユニークにする）。</summary>
    private const string InMemoryAssemblyNamePrefix = "SEEDUserScripts_";

    /// <summary>その場コンパイルのアセンブリ名に付ける一意な部分の書式（GUID のハイフン無し）。</summary>
    private const string InMemoryAssemblyNameFormat = "N";

    /// <summary>コンパイルエラーの stderr 出力に付ける接頭辞（エディタの Output パネルが拾う）。</summary>
    private const string CompileErrorPrefix = "[ScriptCompileError] ";

    // ============================================================
    //  ① その場コンパイル（エディタ／Play）
    // ============================================================

    /// <summary>
    /// assetsRoot 配下の全 .cs をメモリ上のアセンブリへコンパイルする（ロードはしない）。
    /// コンパイルエラーは stderr へ全件出す。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <returns>結果（ソース無し・失敗・成功）。</returns>
    public static ScriptInMemoryBuild EmitInMemory(string assetsRoot)
    {
        // 収集は「読めないフォルダを飛ばして続行する」方式（ScriptSourceCompiler.CollectScriptFiles）。
        var files = ScriptSourceCompiler.CollectScriptFiles(assetsRoot);
        if (files.Count == 0)
            return new ScriptInMemoryBuild { SourceFileCount = 0, Success = true };

        // ── コンパイル（条件は ScriptSourceCompiler が唯一の定義）──
        var compilation = ScriptSourceCompiler.CreateCompilation(
            InMemoryAssemblyNamePrefix + Guid.NewGuid().ToString(InMemoryAssemblyNameFormat),
            files,
            BuildLoadedAssemblyReferences(),
            OptimizationLevel.Debug);

        // 埋め込み PDB 付きで発行する。ソースツリーにファイルパスを設定しているため、
        // Visual Studio を SEED.exe にアタッチするとスクリプトの .cs にブレークポイントを
        // 張ってデバッグできる（PE 内にシンボルが含まれるので追加ファイル不要）。
        var image = new MemoryStream();
        var emitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded);
        var result = compilation.Emit(image, options: emitOptions);
        if (!result.Success)
        {
            foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                Console.Error.WriteLine(CompileErrorPrefix + ScriptSourceCompiler.FormatDiagnostic(d));
            image.Dispose();
            return new ScriptInMemoryBuild { SourceFileCount = files.Count, Success = false };
        }

        // ── 型マップは emit 前の compilation（セマンティックモデル）から作る ──
        // 呼び出し側（ScriptAssemblyManager）がロード後に実際の Type へ引き当てる。
        return new ScriptInMemoryBuild
        {
            SourceFileCount = files.Count,
            Success         = true,
            Image           = image,
            Entries         = ScriptSourceCompiler.MapScriptTypes(compilation, assetsRoot),
        };
    }

    /// <summary>その場コンパイルの参照。ホスト側にロード済みの全アセンブリ（SEEDScripting 自身を含む）。</summary>
    private static List<MetadataReference> BuildLoadedAssemblyReferences() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

    // ============================================================
    //  ② 事前コンパイル（パッケージ化）
    // ============================================================

    /// <summary>
    /// assetsRoot 配下の全 .cs を DLL ファイルへコンパイルする（アセンブリはロードしない）。
    ///
    /// <para>
    /// 参照アセンブリを引数で明示するのは、呼び出し元（エディタプロセス）の
    /// ロード済みアセンブリに結果が左右されないようにするため。パッケージ化は
    /// 「同じ入力なら同じ DLL が出る」ことが重要で、プロセスの状態に依存させない。
    /// </para>
    /// <para>
    /// 型マップ（ソース相対パス → 型名）は DLL のマニフェストリソースとして埋め込む。
    /// パッケージ版にはソースを同梱しないため、これが無いと .scene のパスから型を引けない。
    /// </para>
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="outputDllPath">出力する DLL のパス。</param>
    /// <param name="referenceAssemblyPaths">参照アセンブリの絶対パス一覧。</param>
    /// <returns>成功可否・型数・エラー内容を含む結果。</returns>
    public static ScriptCompileResult CompileToFile(
        string              assetsRoot,
        string              outputDllPath,
        IEnumerable<string> referenceAssemblyPaths)
    {
        var files = ScriptSourceCompiler.CollectScriptFiles(assetsRoot);

        // 参照アセンブリを作る（存在しないパス・重複した単純名は落とす）。
        // 同じ単純名が 2 つあると Roslyn が CS1704 で全体を失敗させるため、
        // 「後勝ち」で 1 つに畳む（呼び出し側が本命の DLL を後ろへ置く）。
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in referenceAssemblyPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            byName[Path.GetFileNameWithoutExtension(path)] = path;
        }

        var references = new List<MetadataReference>();
        var refErrors  = new List<string>();
        foreach (var path in byName.Values)
        {
            try { references.Add(MetadataReference.CreateFromFile(path)); }
            catch (Exception ex) { refErrors.Add($"参照アセンブリを読めません: {path} — {ex.Message}"); }
        }

        var compilation = ScriptSourceCompiler.CreateCompilation(
            PrecompiledScriptArtifact.AssemblyName,
            files,
            references,
            OptimizationLevel.Release);

        // 型マップは emit の前に作る（emit 結果ではなくセマンティックモデルから得る）
        var entries = ScriptSourceCompiler.MapScriptTypes(compilation, assetsRoot);

        // ソースがあるのに 1 型も見つからない＝ SEEDScripting.dll が参照に無い可能性が高い。
        // 黙って「0 型の DLL」を配ると、実行時に全スクリプトが Script type not found になる。
        if (files.Count > 0 && entries.Count == 0)
        {
            refErrors.Add(
                "スクリプト型が 1 つも見つかりません（参照に SEEDScripting.dll が含まれているか確認してください）");
            return ScriptCompileResult.Failed(refErrors, files.Count);
        }

        // 型マップを UTF-8 テキストのマニフェストリソースとして埋め込む
        var typeMapText  = PrecompiledScriptArtifact.SerializeTypeMap(
            entries.Select(e => new KeyValuePair<string, string>(e.AssetKey, e.MetadataName)));
        var typeMapBytes = System.Text.Encoding.UTF8.GetBytes(typeMapText);
        var resources    = new[]
        {
            // dataProvider は Roslyn から複数回呼ばれ得るので、毎回新しいストリームを返す
            new ResourceDescription(
                PrecompiledScriptArtifact.TypeMapResourceName,
                () => new MemoryStream(typeMapBytes, writable: false),
                isPublic: true),
        };

        try
        {
            var dir = Path.GetDirectoryName(outputDllPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 埋め込み PDB（配布先でも例外のスタックに行番号が出る。追加ファイル不要）
            var emitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded);

            EmitResult emitResult;
            using (var fs = new FileStream(outputDllPath, FileMode.Create, FileAccess.Write, FileShare.None))
                emitResult = compilation.Emit(fs, manifestResources: resources, options: emitOptions);

            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(ScriptSourceCompiler.FormatDiagnostic)
                .ToList();
            errors.InsertRange(0, refErrors);

            var warnings = emitResult.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

            if (!emitResult.Success)
            {
                // 中途半端な DLL を残さない（次のビルドが古い DLL を配ってしまうため）
                try { File.Delete(outputDllPath); } catch { /* 消せなくても報告済みなので続行 */ }
                return new ScriptCompileResult
                {
                    Success         = false,
                    SourceFileCount = files.Count,
                    ScriptTypeCount = 0,
                    Errors          = errors,
                    WarningCount    = warnings,
                };
            }

            return new ScriptCompileResult
            {
                Success         = true,
                SourceFileCount = files.Count,
                ScriptTypeCount = entries.Select(e => e.MetadataName).Distinct(StringComparer.Ordinal).Count(),
                Errors          = errors,
                WarningCount    = warnings,
                OutputPath      = outputDllPath,
            };
        }
        catch (Exception ex)
        {
            refErrors.Add($"DLL の書き出しに失敗しました: {outputDllPath} — {ex.Message}");
            return ScriptCompileResult.Failed(refErrors, files.Count);
        }
    }
}
