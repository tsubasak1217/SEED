using System;
using System.Collections.Concurrent;
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

    // ── 共通コンパイル設定 ─────────────────────────────────
    // ランタイム（scripting/src/ScriptAssemblyManager.cs）と同一条件にすること。
    // 条件がずれると「エディタでは通るのに実行時にエラー」等の不一致が起きる。

    /// <summary>ユーザースクリプトの構文解析オプション（言語バージョンは最新固定）。</summary>
    public static CSharpParseOptions ParseOptions { get; } =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    /// <summary>ロード済みアセンブリから作ったメタデータ参照一覧。</summary>
    public static IReadOnlyList<MetadataReference> References => _refs;

    /// <summary>
    /// ユーザースクリプト用のコンパイルオプションを作る。
    /// `PlayerMove?` 等の null 許容注釈をメタデータへ出力させる（警告は出さない）。
    /// 参照フィールドの null 許容判定に必要。
    /// </summary>
    public static CSharpCompilationOptions CreateCompilationOptions() =>
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithNullableContextOptions(NullableContextOptions.Annotations);

    // ── プロジェクト全体の構文木収集（キャッシュ付き）─────────

    /// <summary>構文木キャッシュ 1 件（最終更新時刻とサイズが一致する限り再利用する）。</summary>
    private readonly record struct CachedTree(DateTime WriteTimeUtc, long Length, SyntaxTree Tree);

    /// <summary>
    /// ディスク上の .cs → 構文木のキャッシュ（キーは小文字化したフルパス）。
    /// エディタの入力補完・診断は打鍵のたびに走るため、変更のないファイルの
    /// 再読込・再パースを避ける。複数スレッドから触るので Concurrent を使う。
    /// </summary>
    private static readonly ConcurrentDictionary<string, CachedTree> _treeCache = new();

    /// <summary>キャッシュキー（大文字小文字を無視するためフルパスを小文字化）。</summary>
    private static string CacheKey(string path)
    {
        try { path = Path.GetFullPath(path); } catch { }
        return path.ToLowerInvariant();
    }

    /// <summary>
    /// アセットルート配下の全 .cs を構文解析し、(パス, 構文木) の一覧を返す。
    ///
    /// ランタイムはアセットルート配下の全 .cs を 1 アセンブリとしてコンパイルするため、
    /// 他スクリプトの型（`[SerializeField] PlayerMove? playerMove;` など）を解決するには
    /// エディタ側も同じ木の集合でコンパイルする必要がある。
    /// </summary>
    /// <param name="assetsRoot">アセットルート（存在しなければ override のみを返す）。</param>
    /// <param name="overrideFile">
    /// ディスクの内容ではなく <paramref name="overrideText"/> を使うファイル（編集中のタブなど）。
    /// 同一パスのディスク版はスキップされる（フルパス・大文字小文字無視で比較）。
    /// </param>
    /// <param name="overrideText">上記ファイルの現在のテキスト。</param>
    public static List<(string path, SyntaxTree tree)> CollectProjectSyntaxTrees(
        string? assetsRoot, string? overrideFile = null, string? overrideText = null)
    {
        var trees = new List<(string path, SyntaxTree tree)>();

        // 編集中ファイルはメモリ上のテキストで先に登録する（キャッシュしない）
        string? overrideKey = null;
        if (overrideFile is not null && overrideText is not null)
        {
            overrideKey = CacheKey(overrideFile);
            var full = Path.GetFullPath(overrideFile);
            trees.Add((full, CSharpSyntaxTree.ParseText(overrideText, ParseOptions, path: full)));
        }

        if (string.IsNullOrEmpty(assetsRoot) || !Directory.Exists(assetsRoot)) return trees;

        foreach (var f in Directory.EnumerateFiles(assetsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var key = CacheKey(f);
            if (key == overrideKey) continue;   // 編集中タブのディスク版は使わない

            try
            {
                var info = new FileInfo(f);
                // 最終更新時刻とサイズが一致すればパース済みの木を再利用する
                if (_treeCache.TryGetValue(key, out var cached)
                    && cached.WriteTimeUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                {
                    trees.Add((f, cached.Tree));
                    continue;
                }

                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(f), ParseOptions, path: f);
                _treeCache[key] = new CachedTree(info.LastWriteTimeUtc, info.Length, tree);
                trees.Add((f, tree));
            }
            catch { /* 読めない/壊れたファイルはスキップ（ランタイム側と同じ扱い） */ }
        }
        return trees;
    }

    /// <summary>
    /// 単一の .cs ファイルをコンパイルし、SEEDScript 派生型を返す。
    /// エラー時は (null, エラーメッセージ一覧)。
    /// </summary>
    public static (Type? scriptType, IReadOnlyList<string> errors) CompileFile(string filePath)
    {
        var source = File.ReadAllText(filePath);
        var tree   = CSharpSyntaxTree.ParseText(source, ParseOptions, path: filePath);

        // インスペクタのツールチップ用に、このファイルのドキュメントコメントを索引へ入れる
        // （プロジェクト全体コンパイルが失敗してこの経路へ落ちた場合の受け皿）。
        ScriptDocComments.IndexSingle(tree);

        var comp   = CSharpCompilation.Create(
            $"SEEDScript_{Guid.NewGuid():N}",
            [tree], _refs, CreateCompilationOptions());

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
    /// プロジェクト全体コンパイルのエラー診断 1 件分。
    /// エラー一覧パネルへの表示・該当箇所ジャンプに必要な位置情報を持つ。
    /// </summary>
    public readonly record struct ProjectDiagnostic(
        string Id, string Message, string FilePath, int Line, int Column, int Offset);

    /// <summary>
    /// アセットルート配下の全 .cs をランタイムと同じ条件で一括コンパイルし、
    /// エラー診断一覧を返す（成功時は空リスト）。
    ///
    /// ランタイム（ScriptAssemblyManager）は全 .cs を 1 アセンブリにまとめて
    /// コンパイルするため、型名の重複などファイル横断のエラーがあると
    /// **全スクリプトが実行されなくなる**。単一ファイルコンパイル
    /// （CompileFile）では検出できないため、保存時・Play 開始時の検証に使う。
    /// </summary>
    public static IReadOnlyList<ProjectDiagnostic> CompileProjectDiagnostics(string assetsRoot)
    {
        try
        {
            if (!Directory.Exists(assetsRoot)) return Array.Empty<ProjectDiagnostic>();

            var trees = CollectProjectSyntaxTrees(assetsRoot);
            if (trees.Count == 0) return Array.Empty<ProjectDiagnostic>();

            var comp = CSharpCompilation.Create(
                $"SEEDScriptProj_{Guid.NewGuid():N}",
                trees.Select(t => t.tree), _refs, CreateCompilationOptions());

            using var ms = new MemoryStream();
            var result   = comp.Emit(ms);
            if (result.Success) return Array.Empty<ProjectDiagnostic>();

            return result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d =>
                {
                    var span = d.Location.GetLineSpan();
                    return new ProjectDiagnostic(
                        d.Id,
                        d.GetMessage(),
                        string.IsNullOrEmpty(span.Path) ? "" : span.Path,
                        span.StartLinePosition.Line + 1,
                        span.StartLinePosition.Character + 1,
                        d.Location.SourceSpan.Start);
                })
                .ToList();
        }
        catch
        {
            // 検証自体の失敗は保存・実行をブロックしない（ランタイム側が最終判定する）
            return Array.Empty<ProjectDiagnostic>();
        }
    }

    /// <summary>
    /// 全体コンパイルエラーをメッセージ文字列の一覧で返す（Output パネル表示用）。
    /// </summary>
    public static IReadOnlyList<string> CompileProjectErrors(string assetsRoot)
        => CompileProjectDiagnostics(assetsRoot)
            .Select(d =>
            {
                var file = string.IsNullOrEmpty(d.FilePath) ? "(不明)" : Path.GetFileName(d.FilePath);
                return $"{file}({d.Line}行目): {d.Message}";
            })
            .ToList();

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

            var trees = CollectProjectSyntaxTrees(assetsRoot);
            if (trees.Count == 0) return null;

            // インスペクタのツールチップ用に [SerializeField] の /// <summary> を索引化する。
            // 構文木は既にここで揃っており、木ごとに結果をキャッシュするので追加コストは小さい。
            ScriptDocComments.Index(trees);

            var comp = CSharpCompilation.Create(
                $"SEEDScriptProj_{Guid.NewGuid():N}",
                trees.Select(t => t.tree), _refs, CreateCompilationOptions());

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

        // 参照フィールド（GameObject / Transform / Camera / ユーザースクリプト … と
        // その Nullable 版）か判定する。
        // 判定の正典は SEED.ScriptReference（ランタイム側の注入処理と同じ実装を共有する）。
        //
        // FieldInfo を渡すオーバーロードを使う: 参照型（class）の `T?` は Nullable<T> に
        // ならないため、型情報だけでは null 許容を判別できない（NullabilityInfoContext が要る）。
        SEED.ScriptReference.ReferenceKind? reference =
            SEED.ScriptReference.TryGetKind(f, out var refKind) ? refKind : null;

        // 配列フィールド（T[] / List<T>）は 1 本の JSON 配列文字列として扱う葉。
        // List<T> は BCL で [Serializable] が付いているため、ネスト判定より
        // 先に配列判定を行わないと List の内部フィールドへ降りてしまう。
        ScriptArrayFieldInfo? arrayInfo = null;
        if (reference is null && SEED.ScriptArray.TryGetElementType(f.FieldType, out var elemType, out var isList)
            && SEED.ScriptArray.TryGetElementKind(elemType, out var elemKind, out _))
        {
            arrayInfo = new ScriptArrayFieldInfo(
                elemType, isList, elemKind,
                SEED.ScriptReference.TryGetKind(elemType, out var elemRef) ? elemRef : null);
        }

        // 要素が [Serializable] 構造体の配列（List<FishLevelEntry> など）。
        // メンバ行は通常のフィールド行ビルダーで組めるよう ScriptFieldInfo として展開しておく。
        // TryGetLayout が真＝全メンバがスカラ／参照／1 段の配列であることが保証されているので、
        // ここで展開した子に Children（入れ子の構造体）が現れることはない。
        if (reference is null && arrayInfo is null
            && SEED.ScriptArray.TryGetElementType(f.FieldType, out var structElem, out var structIsList)
            && SEED.ScriptStructArray.TryGetLayout(structElem, out _))
        {
            arrayInfo = new ScriptArrayFieldInfo(
                structElem, structIsList, SEED.ScriptArrayElementKind.Struct, null)
            {
                StructMembers = ExtractFields(structElem, depth + 1),
            };
        }

        // [Serializable] なネストクラスなら子フィールドを再帰展開する。
        // 参照フィールドはハンドル構造体なので展開対象から除外する
        // （ハンドルの内部 entity をインスペクタに晒さないため）。
        IReadOnlyList<ScriptFieldInfo>? children = null;
        if (reference is null && arrayInfo is null && depth < MaxNestDepth && IsNestedSerializable(f.FieldType))
            children = ExtractFields(f.FieldType, depth + 1);

        // フィールドの /// <summary> ドキュメントコメント（リフレクションでは取れないので
        // 構文木から作った索引を引く。索引が無ければ null＝説明なしとして扱う）。
        var summary = ScriptDocComments.Lookup(f.DeclaringType?.Name, f.Name);

        return new ScriptFieldInfo(f, label ?? PrettifyName(f.Name), tooltip, defValue)
        {
            Summary   = summary,
            Header    = header,
            RangeMin  = rangeMin,
            RangeMax  = rangeMax,
            Children  = children,
            Reference = reference,
            Array     = arrayInfo,
            // [Serializable] ネストクラスそのものにはボタンを出さない
            // （子を一括で戻すと Undo が 1 手にまとまらないため。子フィールド個別には付けられる）。
            ShowResetButton = children is null && HasResetButton(f),
        };
    }

    /// <summary>
    /// [Serializable] が付いた、インスペクタで展開すべきネストクラス型かを判定する。
    /// プリミティブ・string・列挙型・配列などは対象外。
    /// </summary>
    private static bool IsNestedSerializable(Type t)
    {
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t.IsArray) return false;
        // List<T> は BCL で [Serializable] が付いているが、内部フィールド（_items 等）を
        // 展開したいわけではないので必ず除外する（配列フィールドとして扱う対象）。
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>))
            return false;
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

    /// <summary>
    /// [ResetButton] 属性の有無を型名で判定する。
    /// 判定方式は HasSerializeField と同じ理由（アセンブリ ID 差異の吸収）で属性名照合にする。
    /// </summary>
    private static bool HasResetButton(FieldInfo f) =>
        f.GetCustomAttributesData().Any(a => a.AttributeType.Name == nameof(ResetButtonAttribute));

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
