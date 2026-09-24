// ============================================================
//  DotnetRuntimeBundle.cs — APK に同梱する .NET を ABI ごとに dotnet-root 形式へ組み立てる
//
//  【出力（置き場 = runtime/android/app/src/seedDotnet/。build.gradle.kts の sourceSets が APK へ入れる）】
//    jniLibs/<ABI>/*.so                       … hostfxr・hostpolicy・coreclr 等（APK の lib/<ABI>/。端末では nativeLibraryDir）
//    assets/seed/dotnet/<ABI>/<dotnet-root 内の相対パス> … BCL の DLL・deps.json・runtimeconfig
//    assets/seed/dotnet/<ABI>/bundle.json     … 目録（ランタイムはこれを読んで files/dotnet/ へ並べる）
//    libs/*.jar                                … 暗号ライブラリの Java 側（APK の Java クラス。CoreCLR だけ）
//  目録の書式の正典はエンジンの runtime/src/engine/core/scripting/embedded_runtime/manifest.rs（BundleManifest）。
//  書式を変えるときは <see cref="ManifestFormatVersion"/> と manifest.rs の SUPPORTED_FORMAT_VERSION を一緒に上げる。
//
//  【中身の識別子 content_id】
//  「目録の書式の版・ABI・設定ファイルの文字列・この組み立て方の版（<see cref="AssemblerRevision"/>）」の SHA-256 の先頭 16 桁。
//  同じなら組み立てを省き（変更なし）、端末も展開を使い回す（展開先のフォルダ名に入る）。NuGet のパックは版ごとに
//  中身が変わらないので、パックの中身は材料に入れない。組み立て方を変えたら <see cref="AssemblerRevision"/> を上げる。
//  （以前の build_and_run.ps1 はスクリプト自身の文字列を材料にしていた。移行後の最初のビルドで content_id が変わり、
//    端末は 1 回だけ展開し直す。）
//
//  処理は従来の build_and_run.ps1（New-DotnetBundle / Update-DotnetJavaLibraries / Update-DotnetBundles）と同じ。
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Android.Dotnet;

/// <summary>1 つの ABI を組み立てた結果。</summary>
/// <param name="Abi">ABI。</param>
/// <param name="ContentId">中身の識別子。</param>
/// <param name="Unchanged">前回と同じで組み立てを省いたか。</param>
/// <param name="AssetFileCount">asset（BCL 等）のファイル数。</param>
/// <param name="AssetBytes">asset の合計バイト数。</param>
/// <param name="NativeFileCount">.so の数。</param>
/// <param name="NativeBytes">.so の合計バイト数。</param>
public sealed record DotnetBundleAbiResult(
    AndroidAbi Abi, string ContentId, bool Unchanged, int AssetFileCount, long AssetBytes, int NativeFileCount, long NativeBytes);

/// <summary>APK に同梱する .NET の組み立て。</summary>
public static class DotnetRuntimeBundle
{
    /// <summary>目録の書式の版（manifest.rs の SUPPORTED_FORMAT_VERSION と同じ）。</summary>
    public const int ManifestFormatVersion = 1;

    /// <summary>この組み立て方の版（出力の中身が変わる変更をしたら上げる。content_id の材料）。</summary>
    public const int AssemblerRevision = 1;

    /// <summary>中身の識別子の桁数（16 進）。端末の展開先のフォルダ名に入る。</summary>
    public const int ContentIdLength = 16;

    /// <summary>APK の assets/seed/ の中の同梱 .NET のフォルダ名（エンジンの embedded_runtime::BUNDLE_DIR_NAME）。</summary>
    public const string BundleDirName = "dotnet";

    /// <summary>目録のファイル名（embedded_runtime::MANIFEST_FILE_NAME）。</summary>
    public const string ManifestFileName = "bundle.json";

    /// <summary>hostfxr のファイル名（目録の hostfxr。エンジンが最初に読み込む .so）。</summary>
    public const string HostfxrFileName = "libhostfxr.so";

    /// <summary>目録の source: APK の assets から複製するファイル。</summary>
    private const string AssetSource = "asset";

    /// <summary>目録の source: APK の lib/&lt;ABI&gt;/ の .so（端末では nativeLibraryDir）。</summary>
    private const string NativeLibrarySource = "native_library";

    /// <summary>共有フレームワークの deps.json の接尾辞（パックの lib/&lt;TFM&gt;/ にある）。</summary>
    private const string DepsFileSuffix = ".deps.json";

    /// <summary>共有フレームワークの runtimeconfig の接尾辞。</summary>
    private const string RuntimeConfigFileSuffix = ".runtimeconfig.json";

    /// <summary>dotnet-root の中の相対パスの区切り（目録の中は OS に関係なく /）。</summary>
    private const char BundlePathSeparator = '/';

    /// <summary>JSON の書き出し設定（人が読める整形）。</summary>
    private static readonly JsonWriterOptions IndentedWriter = new() { Indented = true };

    /// <summary>同梱 .NET の置き場の中の、ABI ごとの asset の親フォルダ（assets/seed/dotnet）。</summary>
    /// <param name="stagingDir">同梱 .NET の置き場。</param>
    /// <returns>フォルダ。</returns>
    public static string AssetsParentDir(string stagingDir) =>
        Path.Combine(stagingDir, "assets", AndroidRuntimeContract.ApkPackageRootName, BundleDirName);

    /// <summary>同梱 .NET の置き場の中の、ABI ごとの .so の親フォルダ（jniLibs）。</summary>
    /// <param name="stagingDir">同梱 .NET の置き場。</param>
    /// <returns>フォルダ。</returns>
    public static string JniLibsParentDir(string stagingDir) => Path.Combine(stagingDir, "jniLibs");

    /// <summary>同梱 .NET の置き場の中の .jar のフォルダ（libs）。</summary>
    /// <param name="stagingDir">同梱 .NET の置き場。</param>
    /// <returns>フォルダ。</returns>
    public static string JavaLibsDir(string stagingDir) => Path.Combine(stagingDir, "libs");

    /// <summary>
    /// その ABI の中身の識別子（content_id）。
    /// </summary>
    /// <param name="settings">同梱 .NET の設定。</param>
    /// <param name="abi">ABI。</param>
    /// <returns>16 進の小文字 <see cref="ContentIdLength"/> 桁。</returns>
    public static string ContentId(DotnetRuntimeSettings settings, AndroidAbi abi)
    {
        var material = $"{ManifestFormatVersion}\n{abi.Name}\n{settings.Text}\n{AssemblerRevision}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..ContentIdLength].ToLowerInvariant();
    }

    /// <summary>
    /// 置き場の目録の content_id を読む（無い・読めなければ null）。
    /// </summary>
    /// <param name="stagingDir">同梱 .NET の置き場。</param>
    /// <param name="abi">ABI。</param>
    /// <returns>content_id。</returns>
    public static string? ReadStagedContentId(string stagingDir, AndroidAbi abi)
    {
        var manifestPath = Path.Combine(AssetsParentDir(stagingDir), abi.Name, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("content_id", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 要らない ABI（今回の ABI に無いもの）の前回分を消す（APK の assets は ABI で絞られないため）。
    /// </summary>
    /// <param name="stagingDir">同梱 .NET の置き場。</param>
    /// <param name="abis">今回の ABI。</param>
    /// <param name="log">1 行ずつの報告。</param>
    public static void RemoveOtherAbis(string stagingDir, IReadOnlyList<AndroidAbi> abis, Action<string> log)
    {
        foreach (var parent in new[] { AssetsParentDir(stagingDir), JniLibsParentDir(stagingDir) })
        {
            if (!Directory.Exists(parent)) continue;
            foreach (var directory in Directory.GetDirectories(parent))
            {
                var name = Path.GetFileName(directory);
                if (abis.Any(abi => abi.Name == name)) continue;
                log($"前回の {name} を消します（今回の ABI に無い）: {directory}");
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// 1 つの ABI の同梱 .NET を組み立てる。前回と同じ content_id の目録と .so の置き場があれば何もしない。
    /// </summary>
    /// <param name="settings">同梱 .NET の設定。</param>
    /// <param name="abi">ABI。</param>
    /// <param name="packFolders">パック名 → 展開済みのパックのフォルダ（NuGet のキャッシュの &lt;名前&gt;/&lt;版&gt;）。</param>
    /// <param name="stagingDir">同梱 .NET の置き場。</param>
    /// <returns>組み立ての結果。</returns>
    public static DotnetBundleAbiResult AssembleAbi(
        DotnetRuntimeSettings settings, AndroidAbi abi, IReadOnlyDictionary<string, string> packFolders, string stagingDir)
    {
        var contentId = ContentId(settings, abi);
        var assetsDir = Path.Combine(AssetsParentDir(stagingDir), abi.Name);
        var jniDir = Path.Combine(JniLibsParentDir(stagingDir), abi.Name);
        if (ReadStagedContentId(stagingDir, abi) == contentId && Directory.Exists(jniDir))
        {
            return new DotnetBundleAbiResult(abi, contentId, Unchanged: true, 0, 0, 0, 0);
        }

        foreach (var dir in new[] { assetsDir, jniDir })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }

        // パックの中の置き場（NuGet のランタイムパックは runtimes/<RID>/lib/<TFM>/ と runtimes/<RID>/native/）
        var runtimeRoot = Path.Combine(PackFolder(packFolders, settings.RuntimePackId(abi)), "runtimes", settings.Expand(settings.Runtime.RuntimeRid, abi));
        var hostRoot = Path.Combine(PackFolder(packFolders, settings.HostPackId(abi)), "runtimes", settings.Expand(settings.Runtime.HostRid, abi));
        var managedDir = Path.Combine(runtimeRoot, "lib", settings.TargetFramework);
        var nativeDir = Path.Combine(runtimeRoot, "native");
        var hostNativeDir = Path.Combine(hostRoot, "native");
        var frameworkDir = $"shared/{settings.Framework}/{settings.Version}";

        var files = new List<(string Path, string Source, long? Size)>();
        var shippedNative = new HashSet<string>(StringComparer.Ordinal);

        // asset（BCL 等）を置いて目録へ足す
        void AddAsset(string sourcePath, string relativePath)
        {
            var destination = Path.Combine(assetsDir, relativePath.Replace(BundlePathSeparator, Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination, overwrite: true);
            files.Add((relativePath, AssetSource, new FileInfo(sourcePath).Length));
        }

        // .so を jniLibs へ置いて目録へ足す（dotnet-root 内の置き場は relativePath。端末では nativeLibraryDir から写す）
        void AddNative(string sourcePath, string relativePath)
        {
            var name = Path.GetFileName(sourcePath);
            File.Copy(sourcePath, Path.Combine(jniDir, name), overwrite: true);
            shippedNative.Add(name);
            files.Add((relativePath, NativeLibrarySource, null));
        }

        // 1) BCL（lib/<TFM>/*.dll。Mono は System.Private.CoreLib.dll が native/ にある）
        foreach (var dll in SortedFiles(managedDir, "*.dll").Concat(SortedFiles(nativeDir, "*.dll")))
        {
            AddAsset(dll, $"{frameworkDir}/{Path.GetFileName(dll)}");
        }
        // 2) 共有フレームワークの runtimeconfig（そのまま）
        var runtimeConfig = Path.Combine(managedDir, settings.Framework + RuntimeConfigFileSuffix);
        RequireFile(runtimeConfig);
        AddAsset(runtimeConfig, $"{frameworkDir}/{Path.GetFileName(runtimeConfig)}");
        // 3) ランタイムの .so（除外するもの・hostfxr / hostpolicy は除く）
        var hostLibraryNames = settings.HostLibraries.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var so in SortedFiles(nativeDir, "*.so"))
        {
            var name = Path.GetFileName(so);
            if (settings.Runtime.ExcludedNativeFiles.Contains(name) || hostLibraryNames.Contains(name)) continue;
            AddNative(so, $"{frameworkDir}/{name}");
        }
        // 4) hostfxr / hostpolicy（host_pack から。置き場は host_libraries のひな形）
        string? hostfxrPath = null;
        foreach (var (fileName, dirTemplate) in settings.HostLibraries)
        {
            var source = Path.Combine(hostNativeDir, fileName);
            RequireFile(source);
            var relative = $"{settings.Expand(dirTemplate, abi)}/{fileName}";
            AddNative(source, relative);
            if (fileName == HostfxrFileName) hostfxrPath = relative;
        }
        if (hostfxrPath is null)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"dotnet_runtime.json の host_libraries に {HostfxrFileName} がありません。");
        }
        // 5) 共有フレームワークの deps.json（native の一覧を実際に入れた .so だけにする。.a・.jar・デバッガ用は入れないため）
        var depsName = settings.Framework + DepsFileSuffix;
        var depsSource = Path.Combine(managedDir, depsName);
        RequireFile(depsSource);
        var depsRelative = $"{frameworkDir}/{depsName}";
        var depsDestination = Path.Combine(assetsDir, depsRelative.Replace(BundlePathSeparator, Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(depsDestination)!);
        File.WriteAllText(depsDestination, FilterDepsNative(File.ReadAllText(depsSource), shippedNative), new UTF8Encoding(false));
        files.Add((depsRelative, AssetSource, new FileInfo(depsDestination).Length));

        // 6) 目録（書式は embedded_runtime::manifest::BundleManifest）
        File.WriteAllBytes(Path.Combine(assetsDir, ManifestFileName), WriteManifest(settings, abi, contentId, hostfxrPath, files));

        var assets = files.Where(f => f.Source == AssetSource).ToList();
        var nativeBytes = Directory.GetFiles(jniDir).Sum(path => new FileInfo(path).Length);
        return new DotnetBundleAbiResult(
            abi, contentId, Unchanged: false, assets.Count, assets.Sum(f => f.Size ?? 0), shippedNative.Count, nativeBytes);
    }

    /// <summary>
    /// 同梱 .NET の Java 側（.jar）を置き場の libs/ へ置き直す（無ければ libs/ ごと消す）。
    /// CoreCLR の暗号ライブラリは JNI_OnLoad でこの .jar のクラスを探し、無ければ abort() する（docs/android.md §17.8）。
    /// .jar は ABI に依らないので、最初の ABI のパックから取る。
    /// </summary>
    /// <param name="settings">同梱 .NET の設定。</param>
    /// <param name="firstAbi">パックを選ぶ ABI（今回の ABI の先頭）。</param>
    /// <param name="packFolders">パック名 → 展開済みのパックのフォルダ。</param>
    /// <param name="stagingDir">同梱 .NET の置き場。</param>
    /// <returns>置いた .jar のパス。</returns>
    public static IReadOnlyList<string> UpdateJavaLibraries(
        DotnetRuntimeSettings settings, AndroidAbi firstAbi, IReadOnlyDictionary<string, string> packFolders, string stagingDir)
    {
        var libsDir = JavaLibsDir(stagingDir);
        if (Directory.Exists(libsDir)) Directory.Delete(libsDir, recursive: true);
        if (settings.Runtime.JavaLibraries.Count == 0) return Array.Empty<string>();

        Directory.CreateDirectory(libsDir);
        var nativeDir = Path.Combine(
            PackFolder(packFolders, settings.RuntimePackId(firstAbi)), "runtimes", settings.Expand(settings.Runtime.RuntimeRid, firstAbi), "native");
        var placed = new List<string>();
        foreach (var jar in settings.Runtime.JavaLibraries)
        {
            var source = Path.Combine(nativeDir, jar);
            RequireFile(source);
            var destination = Path.Combine(libsDir, jar);
            File.Copy(source, destination, overwrite: true);
            placed.Add(destination);
        }
        return placed;
    }

    /// <summary>
    /// deps.json の native の一覧を、実際に入れた .so だけに絞る（runtime＝BCL は全部入れるのでそのまま）。
    /// </summary>
    /// <param name="depsJson">パックの deps.json の中身。</param>
    /// <param name="shippedNative">入れた .so のファイル名。</param>
    /// <returns>書き直した deps.json。</returns>
    public static string FilterDepsNative(string depsJson, IReadOnlySet<string> shippedNative)
    {
        var root = JsonNode.Parse(depsJson) ?? throw new FormatException("deps.json が空です。");
        if (root["targets"] is JsonObject targets)
        {
            foreach (var (_, target) in targets)
            {
                if (target is not JsonObject libraries) continue;
                foreach (var (_, library) in libraries)
                {
                    if (library is not JsonObject libraryObject || libraryObject["native"] is not JsonObject native) continue;
                    foreach (var key in native.Select(pair => pair.Key).ToList())
                    {
                        if (!shippedNative.Contains(Path.GetFileName(key))) native.Remove(key);
                    }
                }
            }
        }
        // 元のファイルと同じく読める形で書く（+ 等を \u エスケープしない。JSON としてはどちらでも同じ意味）
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>目録（bundle.json）を書く。</summary>
    private static byte[] WriteManifest(
        DotnetRuntimeSettings settings, AndroidAbi abi, string contentId, string hostfxrPath,
        IReadOnlyList<(string Path, string Source, long? Size)> files)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, IndentedWriter))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format_version", ManifestFormatVersion);
            writer.WriteString("runtime", settings.Kind);
            writer.WriteString("framework", settings.Framework);
            writer.WriteString("version", settings.Version);
            writer.WriteString("abi", abi.Name);
            writer.WriteString("content_id", contentId);
            writer.WriteString("hostfxr", hostfxrPath);
            writer.WriteString("native_library_mode", settings.NativeLibraryMode);
            writer.WriteStartObject("runtime_properties");
            foreach (var (name, value) in settings.RuntimeProperties) writer.WriteString(name, value);
            writer.WriteEndObject();
            writer.WriteStartArray("files");
            foreach (var (path, source, size) in files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteString("source", source);
                if (size is long bytes) writer.WriteNumber("size", bytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>パックのフォルダを引く（無ければ例外）。</summary>
    private static string PackFolder(IReadOnlyDictionary<string, string> packFolders, string packId) =>
        packFolders.TryGetValue(packId, out var folder)
            ? folder
            : throw new AndroidPipelineException(AndroidFailureKind.Build, $"パック {packId} の取り寄せ先が分かりません。");

    /// <summary>フォルダのファイルを名前順に列挙する（フォルダが無ければ空）。</summary>
    private static IEnumerable<string> SortedFiles(string directory, string pattern) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, pattern).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    /// <summary>ファイルがあることを確かめる（無ければパックの取り寄せの失敗として例外）。</summary>
    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"パックにあるはずのファイルがありません: {path}");
        }
    }
}
