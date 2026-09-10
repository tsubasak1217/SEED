// ============================================================
//  PackageLayout.cs — 配布パッケージのフォルダ構成【正典（エディタ側）】
//
//  【役割】
//  パッケージ出力フォルダ（`{出力先}/{ゲーム名}/`）の中に作るサブフォルダの
//  名前と、そこへのパスを組み立てる純関数だけを持つ。
//  「何をコピーするか」は各パッケージャ（ScriptPackager / DotnetRuntimeBundler）の
//  責務で、ここは置き場の知識だけを担当する。
//
//  【配布物の構成】
//  ```text
//  {ゲーム名}/
//    {ゲーム名}.exe          … 実行ファイル
//    assets.pak              … アセット（PAK）
//    bin/                    … 実行に必要な副次ファイル（パッケージ化で同梱する）
//      SEEDScripting.dll / SEEDScripting.runtimeconfig.json / SEEDScripting.deps.json
//      Microsoft.CodeAnalysis*.dll / SEEDUserScripts.dll
//      dotnet/               … 同梱 .NET ランタイム（self-contained 配布）
//    caches/                 … 実行時生成（モデル派生キャッシュ / pipeline_cache.bin）
//    logs/                   … 実行時生成（起動ログ seed_*.log）
//    saved/                  … 実行時生成（セーブデータ save.json）
//  ```
//
//  【空フォルダを作らない方針】
//  `caches` / `logs` / `saved` は **パッケージ化では作らない**。
//  空フォルダは zip 化・展開で落ちることが多く、「あるはず」を前提にすると
//  配布先でだけ壊れる。必要になった時点でランタイムが作る。
//  同じ理由で、これらは**削除もしない**（利用者のセーブ・ログが入っているため）。
//
//  【ランタイム側の対応物】
//  `runtime/src/engine/core/package_layout.rs` が同じ名前の定数を持つ。
//  どちらかを変えたら必ず両方直すこと（名前がずれると配布物だけが壊れる）。
//
//  【WPF 非依存】
//  PackagingCollectorTests / ScriptPrecompileTests からリンクで取り込んでテストするため、
//  このファイルは WPF 型に依存してはならない（依存した瞬間にテストが壊れて気付ける）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SEEDEditor.Packaging;

/// <summary>配布パッケージのフォルダ構成（サブフォルダ名とパス組み立て）。</summary>
public static class PackageLayout
{
    // ── フォルダ名（ランタイム側 package_layout.rs と一致必須） ──

    /// <summary>
    /// 実行に必要な副次ファイル（スクリプトホスト DLL・同梱 .NET）を入れるフォルダ名。
    /// ランタイム側 <c>package_layout::BIN_DIR_NAME</c> と一致必須。
    /// </summary>
    public const string BinDirName = "bin";

    /// <summary>
    /// 実行時に生成する派生データキャッシュのフォルダ名。
    /// ランタイム側 <c>package_layout::CACHES_DIR_NAME</c> と一致必須。
    /// </summary>
    public const string CachesDirName = "caches";

    /// <summary>
    /// 起動ログのフォルダ名。
    /// ランタイム側 <c>package_layout::LOGS_DIR_NAME</c> と一致必須。
    /// </summary>
    public const string LogsDirName = "logs";

    /// <summary>
    /// セーブデータのフォルダ名（パッケージ実行のみ）。
    /// ランタイム側 <c>package_layout::SAVED_DIR_NAME</c> と一致必須。
    /// </summary>
    public const string SavedDirName = "saved";

    /// <summary>
    /// 実行時に生成されるフォルダ（パッケージ化では作らない・消さない）。
    /// 中身は利用者のセーブ・ログ・キャッシュなので、後始末の対象から必ず外す。
    /// </summary>
    public static readonly IReadOnlyList<string> RuntimeGeneratedDirNames =
        [CachesDirName, LogsDirName, SavedDirName];

    // ── 旧レイアウトの残骸（出力フォルダ直下に散っていたもの） ──

    /// <summary>同梱 .NET ルートのフォルダ名（旧レイアウトでは出力フォルダ直下にあった）。</summary>
    private const string BundledDotnetDirName = "dotnet";

    /// <summary>旧レイアウトで出力フォルダ直下へコピーしていたファイルの拡張子（DLL）。</summary>
    private const string ManagedAssemblyExtension = ".dll";

    /// <summary>
    /// 旧レイアウトで出力フォルダ直下へコピーしていた設定ファイルの接尾辞。
    ///
    /// <para>
    /// <c>*.json</c> をまとめて対象にはしない。出力フォルダは利用者が中身を足せる
    /// 場所であり、無関係な JSON を巻き込む危険があるため、
    /// スクリプトホストが実際に置く 2 種類だけを名指しする。
    /// </para>
    /// </summary>
    private static readonly string[] LegacyConfigSuffixes =
        [".deps.json", ".runtimeconfig.json"];

    // ── パス組み立て（純関数） ───────────────────────────────

    /// <summary>副次ファイルフォルダ <c>{gameOutDir}/bin</c> を返す【純関数】。</summary>
    /// <param name="gameOutDir">パッケージ出力フォルダ。</param>
    /// <returns><c>{gameOutDir}/bin</c>。</returns>
    public static string BinDirectory(string gameOutDir) =>
        Path.Combine(gameOutDir, BinDirName);

    // ── 旧レイアウトの後始末（判定は純関数） ─────────────────

    /// <summary>
    /// 出力フォルダ直下のファイル名が「旧レイアウトの残骸」かを判定する【純関数】。
    ///
    /// <para>
    /// 新レイアウトでは直下に置くのは実行ファイル（<c>.exe</c>）と <c>assets.pak</c> だけ。
    /// DLL と <c>*.deps.json</c> / <c>*.runtimeconfig.json</c> は必ず <c>bin/</c> 側にあるため、
    /// 直下に残っていれば古いパッケージ化の名残と断定できる。
    /// </para>
    /// </summary>
    /// <param name="fileName">ファイル名（パスではなく名前だけ）。</param>
    /// <returns>削除してよいなら true。</returns>
    public static bool IsLegacyLeftoverFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;

        if (fileName.EndsWith(ManagedAssemblyExtension, StringComparison.OrdinalIgnoreCase))
            return true;

        return LegacyConfigSuffixes.Any(
            suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 出力フォルダ直下のフォルダ名が「旧レイアウトの残骸」かを判定する【純関数】。
    ///
    /// <para>
    /// 対象は旧配置の <c>dotnet/</c> だけ。<c>caches</c> / <c>logs</c> / <c>saved</c> は
    /// 利用者データなので絶対に対象にしない。
    /// </para>
    /// </summary>
    /// <param name="dirName">フォルダ名（パスではなく名前だけ）。</param>
    /// <returns>削除してよいなら true。</returns>
    public static bool IsLegacyLeftoverDirectory(string dirName) =>
        string.Equals(dirName, BundledDotnetDirName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 出力フォルダ直下の一覧から、削除すべき旧レイアウトの残骸を選ぶ【純関数】。
    /// </summary>
    /// <param name="fileNames">直下のファイル名一覧。</param>
    /// <param name="dirNames">直下のフォルダ名一覧。</param>
    /// <returns>削除対象の名前（入力の部分集合。ファイル → フォルダの順）。</returns>
    public static IReadOnlyList<string> SelectLegacyLeftovers(
        IEnumerable<string> fileNames,
        IEnumerable<string> dirNames)
    {
        var result = new List<string>();
        result.AddRange(fileNames.Where(IsLegacyLeftoverFile));
        result.AddRange(dirNames.Where(IsLegacyLeftoverDirectory));
        return result;
    }

    /// <summary>
    /// 出力フォルダ直下から旧レイアウトの残骸を削除する。
    ///
    /// <para>
    /// 配置を <c>bin/</c> へ移したあと、同じフォルダへ再度パッケージ化すると
    /// 古い DLL が直下に残り続ける。ランタイムは <c>bin/</c> しか見ないので
    /// 実行には影響しないが、「exe の隣に DLL が散らかる」状態が消えないうえ、
    /// 利用者から見て新旧どちらが本物か分からなくなるため必ず消す。
    /// </para>
    /// <para>
    /// 削除の失敗（他プロセスが開いている等）は握りつぶさずログに出すが、
    /// パッケージ化そのものは止めない。
    /// </para>
    /// </summary>
    /// <param name="gameOutDir">パッケージ出力フォルダ。</param>
    /// <param name="log">ログ出力（UI へ 1 行ずつ流す）。</param>
    /// <returns>削除できた件数。</returns>
    public static int RemoveLegacyLayout(string gameOutDir, Action<string> log)
    {
        if (!Directory.Exists(gameOutDir)) return 0;

        var fileNames = Directory.EnumerateFiles(gameOutDir).Select(Path.GetFileName)!;
        var dirNames  = Directory.EnumerateDirectories(gameOutDir).Select(Path.GetFileName)!;

        var targets = SelectLegacyLeftovers(fileNames!, dirNames!);
        if (targets.Count == 0) return 0;

        log($"旧レイアウトの残骸を削除します（{targets.Count} 件）");

        int removed = 0;
        foreach (var name in targets)
        {
            var path = Path.Combine(gameOutDir, name);
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else                        File.Delete(path);

                removed++;
                log($"    削除: {name}");
            }
            catch (Exception ex)
            {
                log($"    ⚠ 削除できませんでした: {name}（{ex.Message}）");
            }
        }

        return removed;
    }
}
