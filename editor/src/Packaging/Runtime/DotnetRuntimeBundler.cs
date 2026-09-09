// ============================================================
//  DotnetRuntimeBundler.cs — .NET ランタイムの同梱（self-contained 配布）
//
//  【役割】
//  配布先の PC に .NET がインストールされていなくてもスクリプトが動くよう、
//  この PC にインストール済みの .NET ランタイムを丸ごと出力フォルダへ写す。
//  責務は「同梱する .NET の検出」と「コピー」の 2 つだけで、
//  何を同梱するかの判断材料（必要バージョン）は runtimeconfig.json から読む。
//
//  【なぜ必要か】
//  スクリプトホスト（SEEDScripting.dll）は framework-dependent なので、
//  ランタイムは起動時に hostfxr で CLR を初期化する。従来はこの hostfxr を
//  「PC にインストール済みの .NET」からしか探せず、未インストールの PC では
//  CLR 初期化に失敗 → スクリプト無しでゲームが起動していた
//  （ゲームは落ちないが、ほぼ何も動かない状態になる）。
//
//  【出力レイアウト】
//  ランタイム側（runtime/src/engine/core/scripting/mod.rs の BUNDLED_DOTNET_ROOT_DIR）が
//  実行ファイルの隣の dotnet/ を .NET ルートとして使う。中身は
//  インストール版の .NET と同じ形にしておく必要がある。
//
//    {gameOutDir}/dotnet/host/fxr/<ver>/hostfxr.dll
//    {gameOutDir}/dotnet/shared/Microsoft.NETCore.App/<ver>/*   （CLR 一式）
//
//  hostfxr は「自分自身の DLL パス」から .NET ルートを逆算し、その下の
//  shared/Microsoft.NETCore.App から CLR（hostpolicy.dll / coreclr.dll）を解決する。
//  そのため 2 つを同じルート配下へ揃えて置くことがそのまま動作条件になる。
//
//  【WPF 非依存】
//  PackagingCollectorTests からリンクで取り込んでテストするため、
//  このファイルは WPF 型に依存してはならない（依存した瞬間にテストが壊れて気付ける）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.Packaging.Runtime;

// ─── バージョン表現 ───────────────────────────────────────────

/// <summary>
/// .NET のバージョン（major.minor.patch[-prerelease]）。
///
/// <para>
/// フォルダ名（<c>9.0.20</c>）や <c>dotnet --list-runtimes</c> の出力から作る。
/// <see cref="System.Version"/> を使わないのは、プレビュー版のサフィックス
/// （<c>10.0.0-preview.5.25277.114</c>）を含む文字列が Version.Parse で弾かれるため。
/// </para>
/// </summary>
/// <param name="Major">メジャー番号。</param>
/// <param name="Minor">マイナー番号。</param>
/// <param name="Patch">パッチ番号。</param>
/// <param name="PreRelease">プレリリース識別子（無ければ空文字）。</param>
public readonly record struct DotnetVersion(int Major, int Minor, int Patch, string PreRelease)
    : IComparable<DotnetVersion>
{
    /// <summary>major.minor.patch を区切る文字。</summary>
    private const char VersionSeparator = '.';

    /// <summary>プレリリース識別子を切り離す文字（<c>9.0.0-preview.1</c> の <c>-</c>）。</summary>
    private const char PreReleaseSeparator = '-';

    /// <summary>major.minor.patch の要素数（この数に満たない文字列は受け付けない）。</summary>
    private const int RequiredComponentCount = 3;

    /// <summary>プレリリース版が無いことを表す値。</summary>
    private const string NoPreRelease = "";

    /// <summary>
    /// バージョン文字列を解析する。<c>major.minor.patch</c> の 3 要素が揃っているものだけ受け付ける。
    /// </summary>
    /// <param name="text">解析する文字列（例 <c>9.0.20</c> / <c>10.0.0-preview.5</c>）。</param>
    /// <param name="version">解析結果。</param>
    /// <returns>解析できたら true。</returns>
    public static bool TryParse(string? text, out DotnetVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // プレリリース識別子を先に切り離す（数値部分だけを解析対象にする）
        var trimmed    = text.Trim();
        var dashIndex  = trimmed.IndexOf(PreReleaseSeparator);
        var preRelease = dashIndex >= 0 ? trimmed[(dashIndex + 1)..] : NoPreRelease;
        var numeric    = dashIndex >= 0 ? trimmed[..dashIndex]       : trimmed;

        var parts = numeric.Split(VersionSeparator);
        if (parts.Length < RequiredComponentCount) return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return false;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;

        version = new DotnetVersion(major, minor, patch, preRelease);
        return true;
    }

    /// <summary>major.minor が一致するか（同じフレームワークバンドの判定）。</summary>
    /// <param name="other">比較対象。</param>
    /// <returns>major と minor がともに一致すれば true。</returns>
    public bool HasSameMajorMinor(DotnetVersion other) => Major == other.Major && Minor == other.Minor;

    /// <summary>
    /// バージョンの大小を比較する。
    /// 同じ数値ならプレリリース版を正式版より **小さい** とみなす（9.0.0-preview &lt; 9.0.0）。
    /// </summary>
    /// <param name="other">比較対象。</param>
    /// <returns>負なら自分が小さい。</returns>
    public int CompareTo(DotnetVersion other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        var selfIsPre  = PreRelease.Length       > 0;
        var otherIsPre = other.PreRelease.Length > 0;
        if (selfIsPre != otherIsPre) return selfIsPre ? -1 : 1;

        // どちらもプレリリース（または、どちらも正式版）なら識別子の辞書順で決める
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }
}

// ─── 検出結果 ─────────────────────────────────────────────────

/// <summary><c>dotnet --list-runtimes</c> の 1 行を表す。</summary>
/// <param name="FrameworkName">フレームワーク名（例 <c>Microsoft.NETCore.App</c>）。</param>
/// <param name="Version">バージョン文字列（フォルダ名と一致する）。</param>
/// <param name="SharedDirectory">そのフレームワークの共有フォルダ（<c>...\shared\Microsoft.NETCore.App</c>）。</param>
public readonly record struct ListedRuntime(string FrameworkName, string Version, string SharedDirectory);

/// <summary>.NET ランタイム同梱の結果（UI へ返す要約）。</summary>
public sealed class DotnetBundleResult
{
    /// <summary>同梱できたか（スキップ時は false。false でもパッケージ化は続行してよい）。</summary>
    public bool Bundled { get; init; }

    /// <summary>スキップ・失敗の理由（1 行）。同梱できたときは空文字。</summary>
    public string SkipReason { get; init; } = "";

    /// <summary>同梱した CLR のバージョン（例 <c>9.0.20</c>）。</summary>
    public string FrameworkVersion { get; init; } = "";

    /// <summary>同梱した hostfxr のバージョン。</summary>
    public string HostFxrVersion { get; init; } = "";

    /// <summary>コピーしたファイル数（再利用時は 0）。</summary>
    public int CopiedFileCount { get; init; }

    /// <summary>同梱物の合計バイト数（再利用時も実測値を入れる）。</summary>
    public long TotalBytes { get; init; }

    /// <summary>出力先に同じものが既にあり、コピーを省いたか。</summary>
    public bool ReusedExisting { get; init; }
}

// ─── 本体 ─────────────────────────────────────────────────────

/// <summary>
/// この PC にインストール済みの .NET ランタイムを、パッケージ出力フォルダへ同梱する。
/// </summary>
public static class DotnetRuntimeBundler
{
    // ── 規約（ランタイム側と揃える必要がある名前） ───────────

    /// <summary>
    /// 出力先に作る .NET ルートのフォルダ名。
    /// ランタイム側 <c>scripting/mod.rs</c> の <c>BUNDLED_DOTNET_ROOT_DIR</c> と一致必須。
    /// </summary>
    public const string BundledRootDirName = "dotnet";

    /// <summary>hostfxr の置き場（.NET ルートからの相対）の 1 段目。</summary>
    private const string HostDirName = "host";

    /// <summary>hostfxr の置き場（.NET ルートからの相対）の 2 段目。</summary>
    private const string FxrDirName = "fxr";

    /// <summary>共有フレームワークの置き場（.NET ルートからの相対）。</summary>
    private const string SharedDirName = "shared";

    /// <summary>同梱対象のフレームワーク名（CLR 本体）。</summary>
    public const string FrameworkName = "Microsoft.NETCore.App";

    /// <summary>hostfxr 本体のファイル名（Windows）。</summary>
    private const string HostFxrFileName = "hostfxr.dll";

    /// <summary>必要バージョンの読み取り元（出力フォルダにコピー済みの runtimeconfig）。</summary>
    private const string RuntimeConfigFileName = "SEEDScripting.runtimeconfig.json";

    // ── runtimeconfig.json の構造 ────────────────────────────

    /// <summary>runtimeconfig.json のルート要素名。</summary>
    private const string RuntimeOptionsProperty = "runtimeOptions";

    /// <summary>単一フレームワーク指定の要素名。</summary>
    private const string FrameworkProperty = "framework";

    /// <summary>複数フレームワーク指定の要素名（<c>framework</c> の代わりに使われることがある）。</summary>
    private const string FrameworksProperty = "frameworks";

    /// <summary>フレームワーク名の要素名。</summary>
    private const string NameProperty = "name";

    /// <summary>フレームワークバージョンの要素名。</summary>
    private const string VersionProperty = "version";

    // ── 検出 ─────────────────────────────────────────────────

    /// <summary>.NET ルートを指す環境変数名（最優先で見る）。</summary>
    private const string DotnetRootEnvVar = "DOTNET_ROOT";

    /// <summary>インストール済みランタイムの一覧を得るコマンド。</summary>
    private const string DotnetExecutable = "dotnet";

    /// <summary>インストール済みランタイムの一覧を得る引数。</summary>
    private const string ListRuntimesArgument = "--list-runtimes";

    /// <summary><c>dotnet --list-runtimes</c> の応答待ち上限（ミリ秒）。</summary>
    private const int ListRuntimesTimeoutMs = 10_000;

    /// <summary><c>--list-runtimes</c> の行でパスを囲む開き括弧。</summary>
    private const char PathOpenBracket = '[';

    /// <summary><c>--list-runtimes</c> の行でパスを囲む閉じ括弧。</summary>
    private const char PathCloseBracket = ']';

    /// <summary><c>--list-runtimes</c> の行の「名前 バージョン」を分ける文字。</summary>
    private const char ListRuntimesFieldSeparator = ' ';

    /// <summary>「名前 バージョン [パス]」の名前とバージョンの 2 要素。</summary>
    private const int ListRuntimesHeadFieldCount = 2;

    /// <summary>%ProgramFiles% 配下の既定インストール先フォルダ名。</summary>
    private const string DefaultInstallDirName = "dotnet";

    // ── 表示 ─────────────────────────────────────────────────

    /// <summary>バイト数を MB 表記へ直すための除数。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    // ============================================================
    //  エントリポイント
    // ============================================================

    /// <summary>
    /// 出力フォルダへ .NET ランタイムを同梱する。
    ///
    /// <para>
    /// 必要バージョンは <c>{gameOutDir}/SEEDScripting.runtimeconfig.json</c> から読む。
    /// これは **配布物が実際に読む** ファイルそのものなので、ここを正典にしておけば
    /// スクリプトホストのターゲットフレームワークを上げたときに自動で追従する
    /// （バージョンをこのコードへ書かない理由）。
    /// </para>
    /// <para>
    /// 検出できない場合はエラーにせず警告してスキップする。
    /// 同梱が無くても「.NET がインストールされた PC でなら動くパッケージ」にはなるため、
    /// パッケージ化そのものを止めるほどの失敗ではない。
    /// </para>
    /// </summary>
    /// <param name="gameOutDir">パッケージ出力フォルダ（実行ファイルと同じ場所）。</param>
    /// <param name="log">ログ出力（UI へ 1 行ずつ流す）。</param>
    /// <returns>結果。<c>Bundled == false</c> でもパッケージ化は続行してよい。</returns>
    public static DotnetBundleResult Run(string gameOutDir, Action<string> log)
    {
        // ── ① 必要な major.minor を runtimeconfig.json から読む ──
        var configPath = Path.Combine(gameOutDir, RuntimeConfigFileName);
        if (!File.Exists(configPath))
        {
            return Skip($"{RuntimeConfigFileName} が出力に無いため、必要な .NET バージョンが判定できません");
        }

        DotnetVersion required;
        try
        {
            if (!TryParseRequiredFrameworkVersion(File.ReadAllText(configPath), out required))
            {
                return Skip($"{RuntimeConfigFileName} から framework version を読めませんでした");
            }
        }
        catch (Exception ex)
        {
            return Skip($"{RuntimeConfigFileName} の読み取りに失敗しました: {ex.Message}");
        }

        log($"必要な .NET: {FrameworkName} {required.Major}.{required.Minor}");

        // ── ② インストール済み .NET の中から同梱するものを選ぶ ──
        var source = ResolveSource(required, log);
        if (source is null)
        {
            return Skip(
                $".NET {required.Major}.{required.Minor} のインストールが見つからないため同梱をスキップしました" +
                "（配布先に .NET のインストールが必要なパッケージになります）");
        }

        var (frameworkDir, frameworkVersion, hostFxrFile, hostFxrVersion) = source.Value;
        log($"同梱元: {frameworkDir}");
        if (!string.Equals(frameworkVersion, hostFxrVersion, StringComparison.OrdinalIgnoreCase))
            log($"  hostfxr は {hostFxrVersion} を使用（{frameworkVersion} 同梱の hostfxr が無いため）");

        // ── ③ 出力へコピーする ──
        var destFrameworkDir = BundledFrameworkDirectory(gameOutDir, frameworkVersion);
        var destHostFxrFile  = BundledHostFxrPath(gameOutDir, hostFxrVersion);

        // 既に同じ内容が置いてあるならコピーを省く（ファイル数と合計サイズで判定）。
        // 数百ファイル・75 MB のコピーは毎回だと無視できない時間になる。
        if (IsSameDirectoryContent(frameworkDir, destFrameworkDir, out var existingBytes)
            && File.Exists(destHostFxrFile))
        {
            log($"✓ .NET ランタイムは同梱済み（{frameworkVersion} / {existingBytes / BytesPerMegabyte:F1} MB）— コピーを省略");
            return new DotnetBundleResult
            {
                Bundled          = true,
                FrameworkVersion = frameworkVersion,
                HostFxrVersion   = hostFxrVersion,
                TotalBytes       = existingBytes,
                ReusedExisting   = true,
            };
        }

        var watch = Stopwatch.StartNew();
        var (count, bytes) = CopyDirectoryRecursive(frameworkDir, destFrameworkDir);

        Directory.CreateDirectory(Path.GetDirectoryName(destHostFxrFile)!);
        File.Copy(hostFxrFile, destHostFxrFile, overwrite: true);
        count++;
        bytes += new FileInfo(destHostFxrFile).Length;
        watch.Stop();

        log($"✓ .NET ランタイム同梱: {count} ファイル / {bytes / BytesPerMegabyte:F1} MB " +
            $"（CLR {frameworkVersion} + hostfxr {hostFxrVersion}、{watch.Elapsed.TotalSeconds:F1} 秒）");

        return new DotnetBundleResult
        {
            Bundled          = true,
            FrameworkVersion = frameworkVersion,
            HostFxrVersion   = hostFxrVersion,
            CopiedFileCount  = count,
            TotalBytes       = bytes,
        };
    }

    /// <summary>スキップ結果を作る（呼び出し側でログへ出す）。</summary>
    /// <param name="reason">スキップ理由。</param>
    /// <returns>同梱しなかったことを表す結果。</returns>
    private static DotnetBundleResult Skip(string reason) =>
        new() { Bundled = false, SkipReason = reason };

    // ============================================================
    //  必要バージョンの読み取り（純関数）
    // ============================================================

    /// <summary>
    /// runtimeconfig.json から <c>Microsoft.NETCore.App</c> の要求バージョンを読む【純関数】。
    ///
    /// <para>
    /// <c>framework</c>（単一）と <c>frameworks</c>（配列）の両形式に対応する。
    /// SDK のバージョンによってどちらの形で吐かれるかが変わるため。
    /// </para>
    /// </summary>
    /// <param name="json">runtimeconfig.json の中身。</param>
    /// <param name="version">読み取ったバージョン。</param>
    /// <returns>読み取れたら true。</returns>
    public static bool TryParseRequiredFrameworkVersion(string json, out DotnetVersion version)
    {
        version = default;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(RuntimeOptionsProperty, out var options)) return false;

            // 単一指定: "framework": { "name": ..., "version": ... }
            if (options.TryGetProperty(FrameworkProperty, out var single)
                && TryReadFrameworkVersion(single, out version))
            {
                return true;
            }

            // 配列指定: "frameworks": [ { "name": ..., "version": ... }, ... ]
            if (options.TryGetProperty(FrameworksProperty, out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in list.EnumerateArray())
                {
                    if (TryReadFrameworkVersion(element, out version)) return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    /// <summary>framework 要素 1 個から <c>Microsoft.NETCore.App</c> のバージョンを読む。</summary>
    /// <param name="element">framework 要素。</param>
    /// <param name="version">読み取ったバージョン。</param>
    /// <returns>対象フレームワークでバージョンも読めたら true。</returns>
    private static bool TryReadFrameworkVersion(JsonElement element, out DotnetVersion version)
    {
        version = default;
        if (element.ValueKind != JsonValueKind.Object) return false;

        if (!element.TryGetProperty(NameProperty, out var name)) return false;
        if (!string.Equals(name.GetString(), FrameworkName, StringComparison.OrdinalIgnoreCase)) return false;

        if (!element.TryGetProperty(VersionProperty, out var value)) return false;
        return DotnetVersion.TryParse(value.GetString(), out version);
    }

    // ============================================================
    //  バージョン選択（純関数）
    // ============================================================

    /// <summary>
    /// <c>dotnet --list-runtimes</c> の出力を解析する【純関数】。
    ///
    /// <para>1 行の形式: <c>Microsoft.NETCore.App 9.0.20 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]</c></para>
    /// <para>形式に合わない行（空行・警告など）は黙って読み飛ばす。</para>
    /// </summary>
    /// <param name="output">コマンドの標準出力。</param>
    /// <returns>解析できた行の一覧。</returns>
    public static IReadOnlyList<ListedRuntime> ParseListedRuntimes(string output)
    {
        var results = new List<ListedRuntime>();
        if (string.IsNullOrEmpty(output)) return results;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // パス部分 "[...]" を取り出す
            var open  = line.IndexOf(PathOpenBracket);
            var close = line.LastIndexOf(PathCloseBracket);
            if (open < 0 || close <= open) continue;

            var path = line[(open + 1)..close].Trim();
            if (path.Length == 0) continue;

            // 括弧より前の「名前 バージョン」を分解する
            var head = line[..open].Split(ListRuntimesFieldSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (head.Length != ListRuntimesHeadFieldCount) continue;

            results.Add(new ListedRuntime(head[0], head[1], path));
        }

        return results;
    }

    /// <summary>
    /// <c>--list-runtimes</c> の結果から .NET ルート（<c>C:\Program Files\dotnet</c>）を割り出す【純関数】。
    ///
    /// <para>
    /// 出力に載るのは共有フォルダ（<c>&lt;root&gt;\shared\Microsoft.NETCore.App</c>）なので、
    /// 2 段上がルートになる。複数の場所にインストールされている場合は最初のものを使う。
    /// </para>
    /// </summary>
    /// <param name="runtimes">解析済みの一覧。</param>
    /// <returns>.NET ルートの一覧（重複は除く。出現順）。</returns>
    public static IReadOnlyList<string> DotnetRootsFrom(IReadOnlyList<ListedRuntime> runtimes)
    {
        var roots = new List<string>();

        foreach (var runtime in runtimes)
        {
            if (!string.Equals(runtime.FrameworkName, FrameworkName, StringComparison.OrdinalIgnoreCase)) continue;

            // <root>/shared/Microsoft.NETCore.App → <root>
            var sharedParent = Path.GetDirectoryName(runtime.SharedDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var root = sharedParent is null ? null : Path.GetDirectoryName(sharedParent);
            if (string.IsNullOrEmpty(root)) continue;

            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
        }

        return roots;
    }

    /// <summary>
    /// バージョンフォルダ名の一覧から、要求 major.minor に一致する最新パッチを選ぶ【純関数】。
    ///
    /// <para>
    /// major.minor が違うものは選ばない。CLR は major.minor をまたいで
    /// 互換であることが保証されないため、勝手にロールフォワードさせない
    /// （ロールフォワードの判断は hostfxr が runtimeconfig の rollForward に従って行う領分）。
    /// </para>
    /// </summary>
    /// <param name="versionDirNames">バージョンフォルダ名の一覧（例 <c>9.0.19</c>、<c>9.0.20</c>）。</param>
    /// <param name="required">要求バージョン（major.minor だけを見る）。</param>
    /// <returns>選ばれたフォルダ名。見つからなければ null。</returns>
    public static string? SelectLatestPatch(IEnumerable<string> versionDirNames, DotnetVersion required)
    {
        string?        best        = null;
        DotnetVersion  bestVersion = default;

        foreach (var name in versionDirNames)
        {
            if (!DotnetVersion.TryParse(name, out var version)) continue;
            if (!version.HasSameMajorMinor(required))           continue;

            if (best is null || version.CompareTo(bestVersion) > 0)
            {
                best        = name;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>
    /// hostfxr のバージョンフォルダを選ぶ【純関数】。
    ///
    /// <para>
    /// 優先順は「CLR と同じバージョン」→「それ以上で最新」。
    /// hostfxr は自分より新しいフレームワークを解決できない一方、
    /// 古いフレームワークは解決できるため、CLR 以上であればよい。
    /// CLR より古いものしか無い場合は選ばない（起動できない組み合わせを作らない）。
    /// </para>
    /// </summary>
    /// <param name="versionDirNames">host/fxr 配下のフォルダ名一覧。</param>
    /// <param name="frameworkVersionName">同梱する CLR のバージョンフォルダ名。</param>
    /// <returns>選ばれたフォルダ名。見つからなければ null。</returns>
    public static string? SelectHostFxrVersion(IEnumerable<string> versionDirNames, string frameworkVersionName)
    {
        if (!DotnetVersion.TryParse(frameworkVersionName, out var frameworkVersion)) return null;

        var names = versionDirNames as IList<string> ?? versionDirNames.ToList();

        // ① 完全一致（インストール版と同じ組み合わせなので最も安全）
        foreach (var name in names)
        {
            if (string.Equals(name, frameworkVersionName, StringComparison.OrdinalIgnoreCase)) return name;
        }

        // ② CLR 以上のもののうち最新
        string?       best        = null;
        DotnetVersion bestVersion = default;
        foreach (var name in names)
        {
            if (!DotnetVersion.TryParse(name, out var version)) continue;
            if (version.CompareTo(frameworkVersion) < 0)        continue;

            if (best is null || version.CompareTo(bestVersion) > 0)
            {
                best        = name;
                bestVersion = version;
            }
        }

        return best;
    }

    // ============================================================
    //  出力パスの組み立て（純関数）
    // ============================================================

    /// <summary>同梱 .NET ルートのパスを作る【純関数】。</summary>
    /// <param name="gameOutDir">パッケージ出力フォルダ。</param>
    /// <returns><c>{gameOutDir}/dotnet</c>。</returns>
    public static string BundledRootDirectory(string gameOutDir) =>
        Path.Combine(gameOutDir, BundledRootDirName);

    /// <summary>同梱 CLR の置き場を作る【純関数】。</summary>
    /// <param name="gameOutDir">パッケージ出力フォルダ。</param>
    /// <param name="frameworkVersionName">CLR のバージョンフォルダ名。</param>
    /// <returns><c>{gameOutDir}/dotnet/shared/Microsoft.NETCore.App/&lt;ver&gt;</c>。</returns>
    public static string BundledFrameworkDirectory(string gameOutDir, string frameworkVersionName) =>
        Path.Combine(BundledRootDirectory(gameOutDir), SharedDirName, FrameworkName, frameworkVersionName);

    /// <summary>同梱 hostfxr のパスを作る【純関数】。</summary>
    /// <param name="gameOutDir">パッケージ出力フォルダ。</param>
    /// <param name="hostFxrVersionName">hostfxr のバージョンフォルダ名。</param>
    /// <returns><c>{gameOutDir}/dotnet/host/fxr/&lt;ver&gt;/hostfxr.dll</c>。</returns>
    public static string BundledHostFxrPath(string gameOutDir, string hostFxrVersionName) =>
        Path.Combine(BundledRootDirectory(gameOutDir), HostDirName, FxrDirName, hostFxrVersionName, HostFxrFileName);

    // ============================================================
    //  インストール済み .NET の探索（ファイルシステム / プロセス）
    // ============================================================

    /// <summary>同梱元として選ばれた .NET の内訳。</summary>
    /// <param name="FrameworkDirectory">CLR 一式のフォルダ。</param>
    /// <param name="FrameworkVersion">CLR のバージョンフォルダ名。</param>
    /// <param name="HostFxrPath">hostfxr.dll の絶対パス。</param>
    /// <param name="HostFxrVersion">hostfxr のバージョンフォルダ名。</param>
    private readonly record struct BundleSource(
        string FrameworkDirectory,
        string FrameworkVersion,
        string HostFxrPath,
        string HostFxrVersion);

    /// <summary>
    /// 同梱元の .NET を探す。候補を順に試し、CLR と hostfxr が両方揃った最初のものを採用する。
    ///
    /// <para>候補の順: <c>DOTNET_ROOT</c> → <c>dotnet --list-runtimes</c> → <c>%ProgramFiles%\dotnet</c>。</para>
    /// <para>
    /// 環境変数を最優先にするのは、ビルドマシンが「どの .NET を使うか」を
    /// 明示的に切り替えられる逃げ道を残すため（CI で複数バージョンを使い分ける場合など）。
    /// </para>
    /// </summary>
    /// <param name="required">要求バージョン。</param>
    /// <param name="log">ログ出力。</param>
    /// <returns>採用した同梱元。見つからなければ null。</returns>
    private static BundleSource? ResolveSource(DotnetVersion required, Action<string> log)
    {
        foreach (var root in EnumerateDotnetRoots(log))
        {
            var source = TryResolveInRoot(root, required);
            if (source is not null) return source;
        }

        return null;
    }

    /// <summary>.NET ルートの候補を優先順に列挙する（重複は除く）。</summary>
    /// <param name="log">ログ出力。</param>
    /// <returns>候補パスの列挙。</returns>
    private static IEnumerable<string> EnumerateDotnetRoots(Action<string> log)
    {
        var seen = new List<string>();

        // ① 環境変数
        var fromEnv = Environment.GetEnvironmentVariable(DotnetRootEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) AddCandidate(seen, fromEnv.Trim());

        // ② dotnet --list-runtimes
        foreach (var root in DotnetRootsFrom(ParseListedRuntimes(RunListRuntimes(log)))) AddCandidate(seen, root);

        // ③ 既定のインストール先
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            AddCandidate(seen, Path.Combine(programFiles, DefaultInstallDirName));

        return seen;
    }

    /// <summary>候補リストへ重複なく追加する。</summary>
    /// <param name="candidates">候補リスト（この場で書き換える）。</param>
    /// <param name="path">追加するパス。</param>
    private static void AddCandidate(List<string> candidates, string path)
    {
        if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase)) candidates.Add(path);
    }

    /// <summary>
    /// 1 つの .NET ルートの中から、要求に合う CLR と hostfxr を選ぶ。
    /// </summary>
    /// <param name="root">.NET ルート。</param>
    /// <param name="required">要求バージョン。</param>
    /// <returns>両方揃えば同梱元。片方でも欠ければ null。</returns>
    private static BundleSource? TryResolveInRoot(string root, DotnetVersion required)
    {
        var sharedDir = Path.Combine(root, SharedDirName, FrameworkName);
        var fxrDir    = Path.Combine(root, HostDirName, FxrDirName);
        if (!Directory.Exists(sharedDir) || !Directory.Exists(fxrDir)) return null;

        var frameworkVersion = SelectLatestPatch(DirectoryNames(sharedDir), required);
        if (frameworkVersion is null) return null;

        var hostFxrVersion = SelectHostFxrVersion(DirectoryNames(fxrDir), frameworkVersion);
        if (hostFxrVersion is null) return null;

        var hostFxrPath = Path.Combine(fxrDir, hostFxrVersion, HostFxrFileName);
        if (!File.Exists(hostFxrPath)) return null;

        return new BundleSource(
            Path.Combine(sharedDir, frameworkVersion), frameworkVersion, hostFxrPath, hostFxrVersion);
    }

    /// <summary>フォルダ直下のサブフォルダ名を列挙する（存在しなければ空）。</summary>
    /// <param name="directory">対象フォルダ。</param>
    /// <returns>サブフォルダ名の一覧。</returns>
    private static IReadOnlyList<string> DirectoryNames(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>().ToList();
    }

    /// <summary>
    /// <c>dotnet --list-runtimes</c> を実行して標準出力を返す。
    /// dotnet が PATH に無い環境でも例外にせず空文字を返す（この経路は候補の 1 つに過ぎない）。
    /// </summary>
    /// <param name="log">ログ出力。</param>
    /// <returns>標準出力。失敗時は空文字。</returns>
    private static string RunListRuntimes(Action<string> log)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName               = DotnetExecutable,
                Arguments              = ListRuntimesArgument,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var process = Process.Start(info);
            if (process is null) return "";

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(ListRuntimesTimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return "";
            }

            return output;
        }
        catch (Exception ex)
        {
            log($"  （{DotnetExecutable} {ListRuntimesArgument} を実行できませんでした: {ex.Message}）");
            return "";
        }
    }

    // ============================================================
    //  コピー
    // ============================================================

    /// <summary>
    /// フォルダをサブフォルダごと再帰的にコピーする。
    ///
    /// <para>
    /// CLR のフォルダは通常フラットだが、将来サブフォルダが増えても取りこぼさないよう
    /// 再帰でコピーする（1 ファイルでも欠けると CLR が起動しない）。
    /// </para>
    /// </summary>
    /// <param name="sourceDir">コピー元。</param>
    /// <param name="destDir">コピー先。</param>
    /// <returns>コピーしたファイル数と合計バイト数。</returns>
    private static (int Count, long Bytes) CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        int  count = 0;
        long bytes = 0;

        foreach (var source in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(source));
            File.Copy(source, dest, overwrite: true);
            count++;
            bytes += new FileInfo(dest).Length;
        }

        foreach (var childDir in Directory.EnumerateDirectories(sourceDir))
        {
            var (childCount, childBytes) =
                CopyDirectoryRecursive(childDir, Path.Combine(destDir, Path.GetFileName(childDir)));
            count += childCount;
            bytes += childBytes;
        }

        return (count, bytes);
    }

    /// <summary>
    /// コピー先に同じ内容が既にあるかを、ファイル数と合計バイト数で判定する。
    ///
    /// <para>
    /// ハッシュまでは取らない。ここで守りたいのは「同じバージョンを毎回 75 MB 写し直さない」
    /// ことであり、バージョンフォルダ名が一致している前提で数とサイズが合えば十分とみなす。
    /// </para>
    /// </summary>
    /// <param name="sourceDir">コピー元。</param>
    /// <param name="destDir">コピー先。</param>
    /// <param name="destBytes">コピー先の合計バイト数（存在しない場合は 0）。</param>
    /// <returns>同じ内容とみなせるなら true。</returns>
    private static bool IsSameDirectoryContent(string sourceDir, string destDir, out long destBytes)
    {
        destBytes = 0;
        if (!Directory.Exists(destDir)) return false;

        var (sourceCount, sourceBytes) = MeasureDirectory(sourceDir);
        var (destCount,   measured)    = MeasureDirectory(destDir);
        destBytes = measured;

        return sourceCount == destCount && sourceBytes == measured;
    }

    /// <summary>フォルダ配下のファイル数と合計バイト数を測る（再帰）。</summary>
    /// <param name="directory">対象フォルダ。</param>
    /// <returns>ファイル数と合計バイト数。</returns>
    private static (int Count, long Bytes) MeasureDirectory(string directory)
    {
        int  count = 0;
        long bytes = 0;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            count++;
            bytes += new FileInfo(file).Length;
        }

        return (count, bytes);
    }
}
