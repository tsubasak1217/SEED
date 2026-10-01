// ============================================================
//  AndroidArtifactInspector.cs — できた配布物（APK / AAB）を道具で読み、事実（AndroidArtifactFacts）を集める（段階D）
//
//  【読み方】（道具は Android SDK の build-tools の最新版と JDK。Toolchain/AndroidToolchain）
//    マニフェスト … APK: aapt2 dump badging
//                   AAB: 中の base/manifest/AndroidManifest.xml・base/resources.pb・base/res/ を「proto 形式の APK」として
//                        一時フォルダへ並べ直し、aapt2 convert で通常の形式へ変えてから aapt2 dump badging（bundletool 無しで読む）
//    .so の整列   … zip の中の lib/<ABI>/*.so（AAB は base/lib/）の先頭だけを読み、ELF の LOAD の整列（ElfAlignmentReader）
//    zip の整列   … APK だけ zipalign -c -P 16 4（非圧縮の .so が 16 KB 境界にあるか。圧縮した .so は対象外）
//    署名         … APK: apksigner verify --print-certs -v ／ AAB: keytool -printcert -jarfile（JAR 形式の署名）
//    ビルドの印   … zip の中の pak（APK: assets/seed/assets.pak・AAB: base/assets/seed/assets.pak）のエントリ表だけを読み、
//                   開発用のビルドの印（PackageLayout.BuildManifestEntryPath）のエントリがあるか（道具は使わない。配布前の安全弁）
//  道具が無い・失敗したときは例外にせず ToolProblems に理由を入れる（判定は「調べられなかった」＝不合格として出す）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Signing;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Release;

/// <summary>配布物を道具で読む。</summary>
public static class AndroidArtifactInspector
{
    /// <summary>APK の中の .so の置き場の頭。</summary>
    public const string ApkLibPrefix = "lib/";

    /// <summary>APK の中のアセットの置き場の頭（AAB では base/ の下）。</summary>
    private const string ApkAssetsPrefix = "assets/";

    /// <summary>
    /// APK の中の pak の名前（assets/seed/assets.pak。AAB は <see cref="BundleBasePrefix"/> を前に付ける）。
    /// 配布物のルートの名前は AndroidRuntimeContract.ApkPackageRootName（runtime/android/native の APK_PACKAGE_ROOT と一致）。
    /// </summary>
    public const string ApkPakEntry = ApkAssetsPrefix + AndroidRuntimeContract.ApkPackageRootName + "/" + PackageLayout.PakFileName;

    /// <summary>AAB の base モジュールの頭。</summary>
    public const string BundleBasePrefix = "base/";

    /// <summary>.so の拡張子。</summary>
    private const string SharedLibraryExtension = ".so";

    /// <summary>AAB の中のマニフェスト（proto 形式）。</summary>
    private const string BundleManifestEntry = "base/manifest/AndroidManifest.xml";

    /// <summary>AAB の中のリソースの表（proto 形式）。</summary>
    private const string BundleResourcesEntry = "base/resources.pb";

    /// <summary>AAB の中のリソースのフォルダ。</summary>
    private const string BundleResPrefix = "base/res/";

    /// <summary>proto 形式の APK の中のマニフェストの名前。</summary>
    private const string ApkManifestEntry = "AndroidManifest.xml";

    /// <summary>proto 形式の APK の中のリソースの表の名前。</summary>
    private const string ApkResourcesEntry = "resources.pb";

    /// <summary>zipalign の整列の単位（バイト。非圧縮の中身の 4 バイト境界。Android の標準）。</summary>
    private const int ZipAlignBytes = 4;

    /// <summary>1 KiB（zipalign の -P は KB で渡す）。</summary>
    private const int BytesPerKilobyte = 1024;

    /// <summary>zipalign の出力のうち、整列していない項目の印。</summary>
    private const string ZipAlignBadMarker = "BAD";

    /// <summary>zipalign の出力の要点に載せる行の数の上限。</summary>
    private const int ZipAlignDetailMaxLines = 5;

    /// <summary>一時フォルダの名前の頭（AAB のマニフェストを読むための作業場所）。</summary>
    private const string TempFolderPrefix = "seed_aab_manifest_";

    /// <summary>
    /// 配布物を読む。
    /// </summary>
    /// <param name="toolchain">道具の場所。</param>
    /// <param name="artifactPath">APK か AAB。</param>
    /// <param name="format">形式。</param>
    /// <param name="requirements">要件の表（zip の整列のページの大きさ）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>事実。</returns>
    public static async Task<AndroidArtifactFacts> InspectAsync(
        AndroidToolchain toolchain, string artifactPath, AndroidPackageFormat format, PlayRequirements requirements,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var libraries = ReadNativeLibraries(artifactPath, format, problems);

        string? buildTools = null;
        try
        {
            buildTools = toolchain.RequireBuildTools();
        }
        catch (AndroidPipelineException ex)
        {
            problems.Add(ex.Message);
        }

        AaptBadging? manifest = null;
        bool? zipAligned = null;
        string? zipAlignDetail = null;
        if (buildTools is not null)
        {
            var aapt2 = Path.Combine(buildTools, AndroidToolchain.Aapt2FileName);
            manifest = format == AndroidPackageFormat.Aab
                ? await ReadBundleManifestAsync(aapt2, artifactPath, problems, cancellationToken).ConfigureAwait(false)
                : await ReadBadgingAsync(aapt2, artifactPath, problems, cancellationToken).ConfigureAwait(false);
            if (format == AndroidPackageFormat.Apk)
            {
                (zipAligned, zipAlignDetail) = await CheckZipAlignAsync(
                    Path.Combine(buildTools, AndroidToolchain.ZipalignFileName), artifactPath, requirements.PageSizeBytes, problems, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var signer = await ReadSignerAsync(toolchain, buildTools, artifactPath, format, problems, cancellationToken).ConfigureAwait(false);
        return new AndroidArtifactFacts
        {
            ArtifactPath = artifactPath,
            Format = format,
            Manifest = manifest,
            NativeLibraries = libraries,
            ZipAligned = zipAligned,
            ZipAlignDetail = zipAlignDetail,
            Signer = signer,
            // 開発用のビルドの印（道具は使わない。読めなかった理由は判定の項目そのものに出す）
            DebugBuildMark = ReadDebugBuildMark(artifactPath, format),
            ToolProblems = problems,
        };
    }

    /// <summary>
    /// 配布物の zip の中の pak のエントリ表を読み、開発用のビルドの印（<see cref="PackageLayout.BuildManifestEntryPath"/>）が
    /// あるかを調べる（配布前の安全弁）。pak の中身は読まない（表だけを先頭から読み進める。AAB の中で圧縮されていても読める）。
    /// </summary>
    /// <param name="artifactPath">APK か AAB。</param>
    /// <param name="format">形式（AAB は base/ の下を見る）。</param>
    /// <returns>調べた結果（例外は投げず、読めなかった理由を入れて返す）。</returns>
    public static AndroidDebugBuildMarkFact ReadDebugBuildMark(string artifactPath, AndroidPackageFormat format)
    {
        var pakEntry = (format == AndroidPackageFormat.Aab ? BundleBasePrefix : string.Empty) + ApkPakEntry;
        try
        {
            using var archive = ZipFile.OpenRead(artifactPath);
            var entry = archive.GetEntry(pakEntry);
            if (entry is null)
            {
                // pak を入れない配布物（プロジェクト無しのビルド）。印の入れ先が無い
                return new AndroidDebugBuildMarkFact(pakEntry, PakFound: false, MarkPresent: false, Error: null);
            }
            using var stream = entry.Open();
            var paths = PakEntryIndex.ReadEntryPaths(stream, $"{Path.GetFileName(artifactPath)} の {pakEntry}");
            return new AndroidDebugBuildMarkFact(
                pakEntry, PakFound: true, MarkPresent: PakEntryIndex.Contains(paths, PackageLayout.BuildManifestEntryPath), Error: null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new AndroidDebugBuildMarkFact(pakEntry, PakFound: false, MarkPresent: false, Error: ex.Message);
        }
    }

    /// <summary>
    /// zip の中の .so を列挙し、それぞれの ELF の LOAD の整列を読む（先頭だけを展開する）。
    /// </summary>
    /// <param name="artifactPath">APK か AAB。</param>
    /// <param name="format">形式。</param>
    /// <param name="problems">読めなかった理由の足し先。</param>
    /// <returns>.so の一覧。</returns>
    public static IReadOnlyList<AndroidNativeLibraryFact> ReadNativeLibraries(string artifactPath, AndroidPackageFormat format, List<string> problems)
    {
        var prefix = format == AndroidPackageFormat.Aab ? BundleBasePrefix + ApkLibPrefix : ApkLibPrefix;
        var result = new List<AndroidNativeLibraryFact>();
        try
        {
            using var archive = ZipFile.OpenRead(artifactPath);
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
                    || !entry.FullName.EndsWith(SharedLibraryExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var abi = entry.FullName[prefix.Length..].Split('/')[0];
                var compressed = entry.CompressedLength < entry.Length;
                try
                {
                    using var stream = entry.Open();
                    result.Add(new AndroidNativeLibraryFact(entry.FullName, abi, compressed, ElfAlignmentReader.Read(stream), null));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    result.Add(new AndroidNativeLibraryFact(entry.FullName, abi, compressed, null, ex.Message));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"配布物を zip として開けません（{artifactPath}）: {ex.Message}");
        }
        return result;
    }

    /// <summary>aapt2 dump badging で APK のマニフェストの要点を読む。</summary>
    private static async Task<AaptBadging?> ReadBadgingAsync(
        string aapt2, string apkPath, List<string> problems, CancellationToken cancellationToken)
    {
        var capture = await CaptureAsync(new ChildProcessSpec { FileName = aapt2, Arguments = new[] { "dump", "badging", apkPath } },
            problems, cancellationToken).ConfigureAwait(false);
        if (capture is null) return null;
        if (capture.ExitCode != 0)
        {
            problems.Add($"aapt2 dump badging が失敗しました（終了コード {capture.ExitCode}）: {LastLine(capture)}");
            return null;
        }
        return AaptBadgingParser.Parse(capture.StandardOutput);
    }

    /// <summary>
    /// AAB のマニフェストを読む（proto 形式の APK に並べ直して aapt2 convert → aapt2 dump badging。一時フォルダは消す）。
    /// </summary>
    private static async Task<AaptBadging?> ReadBundleManifestAsync(
        string aapt2, string bundlePath, List<string> problems, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), TempFolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var protoApk = Path.Combine(work, "proto.apk");
            var binaryApk = Path.Combine(work, "binary.apk");
            using (var bundle = ZipFile.OpenRead(bundlePath))
            using (var proto = ZipFile.Open(protoApk, ZipArchiveMode.Create))
            {
                foreach (var entry in bundle.Entries)
                {
                    var name = entry.FullName switch
                    {
                        BundleManifestEntry => ApkManifestEntry,
                        BundleResourcesEntry => ApkResourcesEntry,
                        _ when entry.FullName.StartsWith(BundleResPrefix, StringComparison.Ordinal) => entry.FullName[BundleBasePrefix.Length..],
                        _ => null,
                    };
                    if (name is null) continue;
                    var copy = proto.CreateEntry(name, CompressionLevel.Fastest);
                    using var from = entry.Open();
                    using var to = copy.Open();
                    await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
                }
            }
            var convert = await CaptureAsync(new ChildProcessSpec
            {
                FileName = aapt2,
                Arguments = new[] { "convert", "--output-format", "binary", "-o", binaryApk, protoApk },
            }, problems, cancellationToken).ConfigureAwait(false);
            if (convert is null) return null;
            if (convert.ExitCode != 0 || !File.Exists(binaryApk))
            {
                problems.Add($"AAB のマニフェストを aapt2 convert で読める形にできません（終了コード {convert.ExitCode}）: {LastLine(convert)}");
                return null;
            }
            return await ReadBadgingAsync(aapt2, binaryApk, problems, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"AAB（{bundlePath}）のマニフェストを取り出せません: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* 一時フォルダの後始末の失敗は無視 */ }
        }
    }

    /// <summary>zipalign -c -P &lt;KB&gt; 4 で非圧縮の .so の整列を確かめる。</summary>
    private static async Task<(bool? Aligned, string? Detail)> CheckZipAlignAsync(
        string zipalign, string apkPath, long pageSizeBytes, List<string> problems, CancellationToken cancellationToken)
    {
        var pageKilobytes = (pageSizeBytes / BytesPerKilobyte).ToString(CultureInfo.InvariantCulture);
        var capture = await CaptureAsync(new ChildProcessSpec
        {
            FileName = zipalign,
            Arguments = new[] { "-c", "-P", pageKilobytes, "-v", ZipAlignBytes.ToString(CultureInfo.InvariantCulture), apkPath },
        }, problems, cancellationToken).ConfigureAwait(false);
        if (capture is null) return (null, null);
        var bad = capture.StandardOutput.Concat(capture.StandardError)
            .Where(line => line.Contains(ZipAlignBadMarker, StringComparison.Ordinal))
            .Take(ZipAlignDetailMaxLines)
            .ToList();
        return (capture.ExitCode == 0, bad.Count == 0 ? null : string.Join(" / ", bad.Select(line => line.Trim())));
    }

    /// <summary>署名を読む（APK は apksigner、AAB は keytool -printcert -jarfile）。</summary>
    private static async Task<SignerCertificate?> ReadSignerAsync(
        AndroidToolchain toolchain, string? buildTools, string artifactPath, AndroidPackageFormat format, List<string> problems,
        CancellationToken cancellationToken)
    {
        try
        {
            if (format == AndroidPackageFormat.Aab)
            {
                var arguments = new List<string> { "-printcert", "-jarfile", artifactPath };
                arguments.AddRange(AndroidKeystoreTool.EnglishOutputArguments);
                var capture = await CaptureAsync(new ChildProcessSpec { FileName = toolchain.RequireKeytool(), Arguments = arguments },
                    problems, cancellationToken).ConfigureAwait(false);
                return capture is null ? null : SignerCertificateParser.ParseKeytoolPrintCert(capture.StandardOutput.Concat(capture.StandardError), capture.ExitCode);
            }
            if (buildTools is null) return null;
            var apksigner = await CaptureAsync(new ChildProcessSpec
            {
                FileName = Path.Combine(buildTools, AndroidToolchain.ApksignerFileName),
                Arguments = new[] { "verify", "--print-certs", "-v", artifactPath },
                // apksigner.bat は JAVA_HOME の java を使う（道具の解決で見つけた JDK を渡す）
                Environment = new Dictionary<string, string?> { [AndroidToolchain.JavaHomeVariable] = toolchain.RequireJavaHome() },
            }, problems, cancellationToken).ConfigureAwait(false);
            return apksigner is null ? null : SignerCertificateParser.ParseApksigner(apksigner.StandardOutput.Concat(apksigner.StandardError), apksigner.ExitCode);
        }
        catch (AndroidPipelineException ex)
        {
            problems.Add(ex.Message);
            return null;
        }
    }

    /// <summary>子プロセスの出力を集める（起動できなければ理由を足して null）。</summary>
    private static async Task<ChildProcessCapture?> CaptureAsync(ChildProcessSpec spec, List<string> problems, CancellationToken cancellationToken)
    {
        try
        {
            return await ChildProcessRunner.CaptureAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (ChildProcessStartException ex)
        {
            problems.Add($"{Path.GetFileName(spec.FileName)} を起動できません: {ex.Message}");
            return null;
        }
    }

    /// <summary>出力の最後の空でない行（エラーの説明用）。</summary>
    private static string LastLine(ChildProcessCapture capture) =>
        capture.StandardOutput.Concat(capture.StandardError).LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "（出力なし）";
}
