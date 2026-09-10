// ============================================================
//  ScriptPackager.cs — ユーザースクリプトのパッケージ化
//
//  【役割】
//  パッケージ版でユーザースクリプトを動かすために必要な 2 つの成果物を作る。
//    ① SEEDUserScripts.dll … アセット配下の .cs を事前コンパイルした DLL
//    ② スクリプトホスト一式 … SEEDScripting.dll とその依存（CLR から読ませるもの）
//  どちらも出力フォルダ直下ではなく **bin/**（PackageLayout.BinDirName）へ置く。
//  実行ファイルの隣に DLL が数十個並ぶと、利用者から見て「どれが本体で
//  どれが消してよいのか」が判別できないため、副次ファイルは 1 フォルダへ畳む。
//  ランタイム側の探索先も同じ規約（runtime/src/engine/core/package_layout.rs）。
//
//  【なぜ事前コンパイルするのか】
//  従来はアセットに .cs を同梱し、起動時に Roslyn でコンパイルしていた
//  ……という前提だったが、パッケージ版はそもそも --assets-root を持たないので
//  コンパイル自体が呼ばれず、スクリプトが 1 つも動いていなかった。
//  仮に起動時コンパイルへ倒しても「ソースを配布物に晒す」「起動が数秒遅くなる」
//  という代償が付く。そこで **ビルド時に 1 回だけコンパイルして DLL を配る**。
//
//  【型解決の仕組み】
//  .scene には型名ではなくソースのパス（assets://ui/Title.cs）が入っている。
//  そのため DLL には「相対パス → 型名」の対応表を埋め込んである
//  （scripting/src/Compilation/PrecompiledScriptArtifact.cs）。
//  ランタイムは DLL を読むだけでパスから型を引ける。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Scripting;
using SEEDEditor.Scripting.Compilation;

namespace SEEDEditor.Packaging.Scripts;

/// <summary>スクリプトのパッケージ化結果（UI へ返す要約）。</summary>
public sealed class ScriptPackagingResult
{
    /// <summary>すべて成功したか。false ならパッケージ化を中止すること。</summary>
    public bool Success { get; init; }

    /// <summary>失敗時の要約（1 行）。成功時は空文字。</summary>
    public string FailureSummary { get; init; } = "";

    /// <summary>コンパイルエラーの明細（UI ログへ全件出す）。</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>事前コンパイルできたスクリプト型の数。</summary>
    public int ScriptTypeCount { get; init; }

    /// <summary>対象になった .cs の数。</summary>
    public int SourceFileCount { get; init; }

    /// <summary>出力へコピーしたホストファイル数（SEEDUserScripts.dll を含む）。</summary>
    public int CopiedFileCount { get; init; }

    /// <summary>出力へコピーした合計バイト数。</summary>
    public long CopiedBytes { get; init; }
}

/// <summary>
/// アセット配下のユーザースクリプトを事前コンパイルし、
/// スクリプトホスト一式とともにパッケージ出力フォルダへ配置する。
/// </summary>
public static class ScriptPackager
{
    // ── 配置の規約 ───────────────────────────────────────────

    /// <summary>
    /// スクリプトホストのビルド出力フォルダ（runtime フォルダからの相対）。
    /// ランタイムが開発時に探す場所（scripting/mod.rs の
    /// DEV_SCRIPTING_HOST_RELATIVE_DIR）と同じ場所を指す。
    /// </summary>
    private const string HostBuildOutputRelativeDir = @"..\scripting\bin\Debug\net9.0";

    /// <summary>スクリプトホスト本体の DLL 名（存在確認に使う）。</summary>
    private const string HostAssemblyFileName = "SEEDScripting.dll";

    /// <summary>
    /// 出力へコピーしないホストファイルの拡張子。
    /// PDB はデバッグシンボルで、配布物には不要（かつソースパスが露出する）。
    /// </summary>
    private static readonly string[] ExcludedHostExtensions = [".pdb"];

    /// <summary>TRUSTED_PLATFORM_ASSEMBLIES の区切り文字（Windows）。</summary>
    private const char TrustedAssembliesSeparator = ';';

    /// <summary>AppContext から参照アセンブリ一覧を取り出すキー。</summary>
    private const string TrustedAssembliesKey = "TRUSTED_PLATFORM_ASSEMBLIES";

    /// <summary>バイト数を MB 表記へ直すための除数。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>ログへ列挙するコンパイルエラーの最大件数（多すぎるとログが読めない）。</summary>
    private const int MaxLoggedErrors = 100;

    // ── エントリポイント ─────────────────────────────────────

    /// <summary>
    /// ユーザースクリプトを事前コンパイルし、ホスト一式とともに出力フォルダへ配置する。
    /// </summary>
    /// <param name="runtimePath">runtime フォルダの絶対パス（ホスト出力の基準）。</param>
    /// <param name="assetsPath">アセットルートの絶対パス（.cs の収集元）。</param>
    /// <param name="gameOutDir">
    /// パッケージ出力フォルダ（実行ファイルと同じ場所）。
    /// 成果物はこの直下ではなく <c>{gameOutDir}/bin/</c> へ置く。
    /// </param>
    /// <param name="log">ログ出力（UI へ 1 行ずつ流す）。</param>
    /// <returns>結果。Success が false ならパッケージ化を中止すること。</returns>
    public static ScriptPackagingResult Run(
        string          runtimePath,
        string          assetsPath,
        string          gameOutDir,
        Action<string>  log)
    {
        // ── ① スクリプトホストのビルド出力を探す ──
        var hostDir = Path.GetFullPath(Path.Combine(runtimePath, HostBuildOutputRelativeDir));
        var hostDll = Path.Combine(hostDir, HostAssemblyFileName);
        if (!File.Exists(hostDll))
        {
            return new ScriptPackagingResult
            {
                Success        = false,
                FailureSummary =
                    $"スクリプトホストが見つかりません: {hostDll}\n" +
                    "  → エディタ（またはソリューション）をビルドしてから、もう一度パッケージ化してください。",
            };
        }

        // ── ② ユーザースクリプトを DLL へ事前コンパイルする ──
        // 出力先は bin/（配布物の副次ファイル置き場）。コンパイル前に必ず作る
        // ——CompileToFile は書き出し先フォルダの存在を前提にしている。
        var binDir = PackageLayout.BinDirectory(gameOutDir);
        Directory.CreateDirectory(binDir);

        var outputDll = Path.Combine(binDir, PrecompiledScriptArtifact.AssemblyFileName);
        log($"スクリプトを事前コンパイル: {assetsPath}");

        var compile = ScriptAssemblyManager.CompileToFile(assetsPath, outputDll, BuildReferences(hostDll));
        if (!compile.Success)
        {
            return new ScriptPackagingResult
            {
                Success         = false,
                FailureSummary  = $"ユーザースクリプトのコンパイルに失敗しました（{compile.Errors.Count} 件）",
                Errors          = compile.Errors,
                SourceFileCount = compile.SourceFileCount,
            };
        }

        log($"✓ {PrecompiledScriptArtifact.AssemblyFileName}: " +
            $"{compile.ScriptTypeCount} 型 / {compile.SourceFileCount} ファイル" +
            (compile.WarningCount > 0 ? $"（警告 {compile.WarningCount} 件）" : ""));

        // ── ③ スクリプトホスト一式を出力（bin/）へコピーする ──
        var (copied, bytes) = CopyHostFiles(hostDir, binDir, log);

        // DLL 本体もコピー物として計上する（同梱サイズの見積もりに使うため）
        var dllBytes = new FileInfo(outputDll).Length;

        log($"✓ スクリプトホスト同梱: {copied + 1} ファイル / " +
            $"{(bytes + dllBytes) / BytesPerMegabyte:F1} MB");

        return new ScriptPackagingResult
        {
            Success         = true,
            ScriptTypeCount = compile.ScriptTypeCount,
            SourceFileCount = compile.SourceFileCount,
            CopiedFileCount = copied + 1,
            CopiedBytes     = bytes + dllBytes,
        };
    }

    /// <summary>
    /// コンパイルエラーをログへ列挙する（件数が多い場合は打ち切る）。
    /// </summary>
    /// <param name="result">失敗した結果。</param>
    /// <param name="log">ログ出力。</param>
    public static void LogErrors(ScriptPackagingResult result, Action<string> log)
    {
        if (!string.IsNullOrEmpty(result.FailureSummary)) log(result.FailureSummary);
        foreach (var error in result.Errors.Take(MaxLoggedErrors)) log($"    {error}");
        if (result.Errors.Count > MaxLoggedErrors)
            log($"    …ほか {result.Errors.Count - MaxLoggedErrors} 件");
    }

    // ── 内部処理 ─────────────────────────────────────────────

    /// <summary>
    /// 事前コンパイルに使う参照アセンブリの一覧を作る。
    ///
    /// <para>
    /// 基本は実行中プロセス（エディタ）の信頼済みアセンブリ一覧
    /// （TRUSTED_PLATFORM_ASSEMBLIES）で、これで .NET の基本ライブラリが揃う。
    /// そこへ「ランタイムが実際にロードするほうの」SEEDScripting.dll を **末尾に** 足す。
    /// </para>
    /// <para>
    /// 末尾に置くのは、同じ単純名の重複を CompileToFile 側が「後勝ち」で畳むため。
    /// エディタ出力の SEEDScripting.dll ではなく、配布物が読む側と同じ DLL を
    /// 参照させることで、両者のバージョンずれを構造的に防ぐ。
    /// </para>
    /// </summary>
    /// <param name="hostDll">スクリプトホスト（SEEDScripting.dll）の絶対パス。</param>
    /// <returns>参照アセンブリの絶対パス一覧。</returns>
    private static List<string> BuildReferences(string hostDll)
    {
        var references = new List<string>();

        if (AppContext.GetData(TrustedAssembliesKey) is string trusted)
        {
            references.AddRange(trusted
                .Split(TrustedAssembliesSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        references.Add(hostDll);
        return references;
    }

    /// <summary>
    /// スクリプトホストのビルド出力（フォルダ直下のファイル）を出力先へコピーする。
    ///
    /// <para>
    /// サブフォルダ（Roslyn の言語別 resources）はコピーしない。中身は診断メッセージの
    /// ローカライズだけで、実行に必要な参照ではない。開発時のシャドウコピー
    /// （scripting/mod.rs の shadow_copy）も同じくフォルダ直下しか写しておらず、
    /// その構成で長く動いているため、無くても起動できることが実績で分かっている。
    /// </para>
    /// </summary>
    /// <param name="hostDir">スクリプトホストのビルド出力フォルダ。</param>
    /// <param name="destDir">コピー先（パッケージ出力フォルダの <c>bin/</c>）。</param>
    /// <param name="log">ログ出力。</param>
    /// <returns>コピーしたファイル数と合計バイト数。</returns>
    private static (int Count, long Bytes) CopyHostFiles(string hostDir, string destDir, Action<string> log)
    {
        int  count = 0;
        long bytes = 0;

        foreach (var source in Directory.EnumerateFiles(hostDir))
        {
            var extension = Path.GetExtension(source);
            if (ExcludedHostExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

            var fileName = Path.GetFileName(source);
            var dest     = Path.Combine(destDir, fileName);
            File.Copy(source, dest, overwrite: true);

            count++;
            bytes += new FileInfo(dest).Length;
            log($"    {fileName}");
        }

        return (count, bytes);
    }
}
