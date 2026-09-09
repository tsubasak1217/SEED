using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using SEEDEditor.Scripting.Compilation;

namespace SEEDEditor.Scripting;

/// <summary>
/// ユーザースクリプト（アセットフォルダ内の .cs）のコンパイルとロードを管理する。
///
/// 【2 つの入口】
/// - <see cref="CompileAndLoad"/>  : ソースをその場でコンパイルしてロードする（エディタ／Play）
/// - <see cref="CompileToFile"/> + <see cref="LoadPrecompiled"/>
///                                 : パッケージ化時に DLL を作り、配布先ではそれを読むだけにする
///   （パッケージ版へソースと Roslyn を同梱しないための経路）
///
/// 【設計】
/// - 全 .cs を 1 つのアセンブリにまとめてコンパイルする
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
    // ── ロード状態 ───────────────────────────────────────────

    /// <summary>現在ロード中のスクリプトアセンブリのロードコンテキスト。</summary>
    private static AssemblyLoadContext? _context;

    /// <summary>現在ロード中のスクリプトアセンブリ。</summary>
    private static Assembly? _assembly;

    // ── 解決テーブル（Resolve の優先順どおりに 3 段持つ）────────

    /// <summary>
    /// アセットルート相対の正規キー（例 "ui/title.cs"）→ スクリプト型。
    ///
    /// パッケージ版では .scene に <c>assets://ui/Title.cs</c> 形式しか残らないため、
    /// この表が無いと「別フォルダの同名ファイル」を取り違える。
    /// </summary>
    private static readonly Dictionary<string, Type> _typeByAssetKey = new();

    /// <summary>正規化済みフルパス（小文字）→ スクリプト型。エディタ（絶対パス保存）用。</summary>
    private static readonly Dictionary<string, Type> _typeByPath = new();

    /// <summary>ファイル名（例 "myscript.cs"、小文字）→ スクリプト型。パス表記ゆれのフォールバック用。</summary>
    private static readonly Dictionary<string, Type> _typeByFileName = new();

    // ── 定数 ─────────────────────────────────────────────────

    /// <summary>その場コンパイル時のアセンブリ名の接頭辞（毎回ユニークにする）。</summary>
    private const string InMemoryAssemblyNamePrefix = "SEEDUserScripts_";

    /// <summary>collectible ロードコンテキストの表示名。</summary>
    private const string LoadContextName = "SEEDUserScripts";

    /// <summary>コンパイルエラーを表す戻り値（FFI 用。型数は 0 以上なので負値と区別できる）。</summary>
    private const int CompileFailureCode = -1;

    /// <summary>コンパイルエラーの stderr 出力に付ける接頭辞（エディタの Output パネルが拾う）。</summary>
    private const string CompileErrorPrefix = "[ScriptCompileError] ";

    /// <summary>ログの共通接頭辞。</summary>
    private const string LogPrefix = "[SEEDScripting] ";

    // ── 参照アセンブリ ───────────────────────────────────────

    /// <summary>コンパイル参照。ホスト側にロード済みの全アセンブリ（SEEDScripting 自身を含む）。</summary>
    private static List<MetadataReference> BuildReferences() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

    // ============================================================
    //  ① その場コンパイル（エディタ／Play）
    // ============================================================

    /// <summary>
    /// assetsRoot 配下の全 .cs をコンパイルしてロードする。
    /// 既存アセンブリがあればアンロードして置き換える。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <returns>
    /// コンパイルされたスクリプト型の数。
    /// コンパイルエラー時は -1（旧アセンブリは維持され、エラーは stderr に出力）。
    /// </returns>
    public static int CompileAndLoad(string assetsRoot)
    {
        // 収集は「読めないフォルダを飛ばして続行する」方式。
        // 1 つでも開けないフォルダ（権限・削除保留・壊れた再解析ポイント等）があるだけで
        // プロジェクト全体のスクリプトが 1 本も動かなくなるのを防ぐ。
        var files = ScriptSourceCompiler.CollectScriptFiles(assetsRoot);

        // スクリプトが 1 つも無い場合は空状態にして正常終了する
        if (files.Count == 0)
        {
            Unload();
            return 0;
        }

        // ── コンパイル（条件は ScriptSourceCompiler が唯一の定義）──
        var compilation = ScriptSourceCompiler.CreateCompilation(
            InMemoryAssemblyNamePrefix + Guid.NewGuid().ToString("N"),
            files,
            BuildReferences(),
            OptimizationLevel.Debug);

        // 埋め込み PDB 付きで発行する。ソースツリーにファイルパスを設定しているため、
        // Visual Studio を SEED.exe にアタッチするとスクリプトの .cs にブレークポイントを
        // 張ってデバッグできる（PE 内にシンボルが含まれるので追加ファイル不要）。
        using var ms = new MemoryStream();
        var emitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded);
        var result = compilation.Emit(ms, options: emitOptions);
        if (!result.Success)
        {
            foreach (var d in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                Console.Error.WriteLine(CompileErrorPrefix + ScriptSourceCompiler.FormatDiagnostic(d));
            // 旧アセンブリを維持して呼び出し側にエラーを伝える
            return CompileFailureCode;
        }

        // ── 型マップは emit 前の compilation（セマンティックモデル）から作る ──
        // ここで作った対応を、ロード後に実際の Type へ引き当てる。
        var entries = ScriptSourceCompiler.MapScriptTypes(compilation, assetsRoot);

        // ── 旧アセンブリをアンロードし、新アセンブリをロードする ──
        Unload();
        ms.Position = 0;
        _context  = CreateLoadContext();
        _assembly = _context.LoadFromStream(ms);

        var typeCount = RegisterTypes(entries);

        Console.WriteLine($"{LogPrefix}compiled {typeCount} script type(s) from {files.Count} file(s)");
        return typeCount;
    }

    // ============================================================
    //  ② 事前コンパイル（パッケージ化）と、その成果物のロード
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

    /// <summary>
    /// 事前コンパイル済みのユーザースクリプト DLL をロードする（パッケージ版の起動経路）。
    ///
    /// <para>
    /// ファイルをロックしないよう、バイト列を読み切ってからストリームでロードする。
    /// </para>
    /// </summary>
    /// <param name="dllPath">SEEDUserScripts.dll のパス。</param>
    /// <returns>解決可能になったスクリプト型の数。失敗時は -1。</returns>
    public static int LoadPrecompiled(string dllPath)
    {
        try
        {
            if (!File.Exists(dllPath))
            {
                Console.Error.WriteLine($"{LogPrefix}precompiled scripts not found: {dllPath}");
                return CompileFailureCode;
            }

            var bytes = File.ReadAllBytes(dllPath);

            Unload();
            _context = CreateLoadContext();
            using (var ms = new MemoryStream(bytes, writable: false))
                _assembly = _context.LoadFromStream(ms);

            var typeCount = RegisterPrecompiledTypes(_assembly, dllPath);
            Console.WriteLine($"{LogPrefix}loaded {typeCount} precompiled script type(s) from {Path.GetFileName(dllPath)}");
            return typeCount;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{LogPrefix}LoadPrecompiled failed: {ex}");
            return CompileFailureCode;
        }
    }

    /// <summary>
    /// 埋め込み型マップ（無ければ型名からの復元）で解決テーブルを作る。
    /// </summary>
    /// <param name="assembly">ロード済みのユーザースクリプトアセンブリ。</param>
    /// <param name="dllPath">ログ用の DLL パス。</param>
    /// <returns>登録できた型の数。</returns>
    private static int RegisterPrecompiledTypes(Assembly assembly, string dllPath)
    {
        var registered = new HashSet<Type>();

        // ── ① 埋め込み型マップ（正規の経路）──
        using (var stream = assembly.GetManifestResourceStream(PrecompiledScriptArtifact.TypeMapResourceName))
        {
            if (stream is not null)
            {
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                foreach (var (key, typeName) in PrecompiledScriptArtifact.ParseTypeMap(reader.ReadToEnd()))
                {
                    var type = assembly.GetType(typeName, throwOnError: false);
                    if (type is null)
                    {
                        Console.Error.WriteLine($"{LogPrefix}type map entry not found in assembly: {typeName}");
                        continue;
                    }

                    _typeByAssetKey[key] = type;
                    _typeByFileName[ScriptAssetPath.FileNameOfKey(key)] = type;
                    registered.Add(type);
                }

                return registered.Count;
            }
        }

        // ── ② 型マップが無い DLL（旧形式・手動生成）のフォールバック ──
        // 「型名 + .cs」がファイル名だったものと見なして復元する。
        Console.Error.WriteLine(
            $"{LogPrefix}no type map in {Path.GetFileName(dllPath)} — falling back to type-name mapping");

        foreach (var type in EnumerateScriptTypes(assembly))
        {
            _typeByFileName[(type.Name + ScriptAssetPath.ScriptExtension).ToLowerInvariant()] = type;
            registered.Add(type);
        }
        return registered.Count;
    }

    // ============================================================
    //  型の解決
    // ============================================================

    /// <summary>
    /// 型名または .cs ファイルパスからスクリプト型を解決する。
    ///
    /// 優先順:
    ///   ① アセット相対キー一致（assets:// 形式。別フォルダの同名ファイルを取り違えない）
    ///   ② 絶対パス完全一致（エディタ保存の絶対パス）
    ///   ③ ファイル名一致（表記ゆれのフォールバック）
    ///   ④ ユーザーアセンブリ内の型名 → 全ロード済みアセンブリの型名
    /// </summary>
    /// <param name="nameOrPath">.scene 等に保存された型名またはパス。</param>
    /// <returns>見つかった型。見つからなければ null。</returns>
    public static Type? Resolve(string nameOrPath)
    {
        if (ScriptAssetPath.IsScriptFileReference(nameOrPath))
        {
            // ① アセット相対キー（"assets://a/foo.cs" → "a/foo.cs"）
            var assetKey = ScriptAssetPath.KeyFromReference(nameOrPath);
            if (_typeByAssetKey.TryGetValue(assetKey, out var byAssetKey)) return byAssetKey;

            // ② 絶対パス完全一致
            var normalized = NormalizePath(nameOrPath);
            if (_typeByPath.TryGetValue(normalized, out var byPath)) return byPath;

            // ③ ファイル名一致
            if (_typeByFileName.TryGetValue(ScriptAssetPath.FileNameOfKey(assetKey), out var byFile)) return byFile;
            return null;
        }

        // ④ 型名指定: ユーザースクリプトアセンブリを優先して検索する
        if (_assembly is not null)
        {
            var t = SafeGetTypes(_assembly).FirstOrDefault(t => t.FullName == nameOrPath || t.Name == nameOrPath);
            if (t is not null) return t;
        }

        // フォールバック: SEEDScripting 自身などロード済みアセンブリから検索する
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == nameOrPath || t.Name == nameOrPath);
    }

    // ============================================================
    //  内部ヘルパー
    // ============================================================

    /// <summary>
    /// ユーザースクリプト用の collectible ロードコンテキストを作る。
    ///
    /// ユーザースクリプトが参照するホスト側アセンブリ（SEEDScripting 本体や
    /// その依存 DLL）は、すでにこのプロセスにロード済みである。ただし SEEDScripting は
    /// Rust ランタイムが hostfxr（load_assembly_and_get_function_pointer）経由で
    /// Default とは別の独立した AssemblyLoadContext にロードしているため、
    /// collectible ALC の既定のフォールバック（Default ALC）では名前解決できず
    /// FileNotFoundException になる。
    /// そこで Resolving で、プロセス内にロード済みの同名アセンブリ（ALC を問わず
    /// AppDomain 全体から検索）へフォールバックさせる。これで基底クラス SEEDScript や
    /// SEED.* API を含む SEEDScripting をユーザーアセンブリから参照できる。
    /// </summary>
    /// <returns>生成したロードコンテキスト。</returns>
    private static AssemblyLoadContext CreateLoadContext()
    {
        var context = new AssemblyLoadContext(LoadContextName, isCollectible: true);
        context.Resolving += (ctx, name) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name.Name);
        return context;
    }

    /// <summary>
    /// 型マップのエントリを、ロード済みアセンブリの実型へ引き当てて解決テーブルへ登録する。
    /// </summary>
    /// <param name="entries">ソースファイルごとの型エントリ。</param>
    /// <returns>登録できた型の数（重複を除いた実型の数）。</returns>
    private static int RegisterTypes(IReadOnlyList<ScriptTypeEntry> entries)
    {
        if (_assembly is null) return 0;

        var registered = new HashSet<Type>();
        foreach (var entry in entries)
        {
            var type = _assembly.GetType(entry.MetadataName, throwOnError: false);
            if (type is null) continue;

            _typeByAssetKey[entry.AssetKey] = type;
            _typeByPath[NormalizePath(entry.SourcePath)] = type;
            _typeByFileName[ScriptAssetPath.FileNameOfKey(entry.AssetKey)] = type;
            registered.Add(type);
        }
        return registered.Count;
    }

    /// <summary>アセンブリ内の「生成可能なスクリプト型」を列挙する。</summary>
    /// <param name="assembly">対象アセンブリ。</param>
    /// <returns>スクリプト型の列。</returns>
    private static IEnumerable<Type> EnumerateScriptTypes(Assembly assembly) =>
        SafeGetTypes(assembly).Where(t =>
            !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(IScriptComponent).IsAssignableFrom(t));

    /// <summary>
    /// 型列挙で例外（依存アセンブリ欠落など）が出ても落ちないようにする。
    /// </summary>
    /// <param name="assembly">対象アセンブリ。</param>
    /// <returns>取得できた型（失敗時は空）。</returns>
    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); } catch { return Type.EmptyTypes; }
    }

    /// <summary>現在のスクリプトアセンブリをアンロードし、マッピングをクリアする。</summary>
    private static void Unload()
    {
        _typeByAssetKey.Clear();
        _typeByPath.Clear();
        _typeByFileName.Clear();
        _assembly = null;
        _context?.Unload();
        _context = null;
    }

    /// <summary>Windows のパス表記ゆれ（区切り・大文字小文字）を正規化する。</summary>
    /// <param name="path">対象パス。</param>
    /// <returns>正規化したパス。</returns>
    private static string NormalizePath(string path)
    {
        try { path = Path.GetFullPath(path); } catch { }
        return path.Replace('/', '\\').ToLowerInvariant();
    }
}
