using System.IO.Compression;
using System.Linq;
using System.Text;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Release;
using SEEDEditor.Android.Steps;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Packaging.Pak;
using SEEDEditor.Tools.SeedAndroid;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// 開発用のビルドの印（pak の .seed/build.json。docs/android.md §5・§24.8）:
/// 開発用（debug）の APK だけが SeedPak に --debug-build を付けて印を入れ、配布用（release）は入れない（Rust の最適化には依らない）・
/// 印の有無が pak の指紋に入る・配布前の検査（AndroidArtifactChecks.DebugBuildMark）は印のある配布物を不合格にする。
/// ランタイムは印を SEED.Application.IsDebugBuild として読み、IsDebugAllowed = !IsPackaged || IsDebugBuild にする。
/// </summary>
public static class DebugBuildMarkTests
{
    /// <summary>印の中身（エディタの PakBuildManifest が書くものと同じ形。検査は有無だけを見る）。</summary>
    private const string MarkJson = """{"format":1,"debug":true}""";

    /// <summary>pak に入れる見本のシーン。</summary>
    private const string SceneEntry = "scenes/Main.scene";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("ビルドの印: debug の APK は入れ（SeedPak --debug-build）、release は入れない・Rust の最適化（--release）には依らない", MarksOnlyDebugVariant);
        harness.Add("ビルドの印: pak の指紋は印の有無で変わり、印を入れないとき（配布用）は従来の式のまま", FingerprintIncludesMark);
        harness.Add("配布前の検査: 印のある release の APK / AAB は不合格・印の無いものは合格（zip の中の pak の表を読む）", ReleaseArtifactWithMarkFails);
        harness.Add("配布前の検査: pak の無い配布物は合格・読めない pak と調べていないものは不合格（確かめていないものを合格にしない）", MarkCheckEdgeCases);
    }

    // ============================================================
    //  テスト本体
    // ============================================================

    /// <summary>印を入れるのは開発用の APK だけ。</summary>
    private static void MarksOnlyDebugVariant()
    {
        Check.True(new AndroidRunRequest().MarksDebugBuild, "既定（debug の APK）は印を入れる");
        Check.True(new AndroidRunRequest { Release = true }.MarksDebugBuild, "--release（Rust の最適化）でも開発用の APK なら印を入れる");
        Check.True(new AndroidRunRequest { NativeProfile = "release" }.MarksDebugBuild, "--native-profile でも同じ");
        Check.True(!new AndroidRunRequest { Variant = AndroidBuildVariant.Release }.MarksDebugBuild, "配布用（release）は必ず入れない");

        // SeedAndroid の引数から: build（既定 debug）は印あり、--variant release / --format aab は印なし
        var debug = Request("build", "--project", @"D:\Game", "--abi", "arm64-v8a");
        Check.True(debug.MarksDebugBuild, "SeedAndroid build（開発用）は印を入れる");
        var release = Request("build", "--variant", "release", "--project", @"D:\Game");
        Check.True(!release.MarksDebugBuild, "SeedAndroid build --variant release は入れない");
        var aab = Request("build", "--format", "aab", "--project", @"D:\Game");
        Check.True(!aab.MarksDebugBuild, "--format aab（配布用とみなす）も入れない");

        // SeedPak の引数: 印を入れるときだけ --debug-build を足す（ほかの並びは従来どおり）
        const string project = @"D:\Game Project";
        const string output = @"C:\repo\runtime\android\app\src\main\assets\seed";
        Check.Equal($"--project|{project}|--out|{output}|--scripts|--debug-build",
            string.Join("|", PackageContentStep.SeedPakArguments(project, output, System.Array.Empty<string>(), debugBuild: true)),
            "開発用の APK は --debug-build を付ける");
        Check.Equal($"--project|{project}|--out|{output}|--scripts|--extra-scene|scenes/B.scene|--debug-build",
            string.Join("|", PackageContentStep.SeedPakArguments(project, output, new[] { "scenes/B.scene" }, debugBuild: true)),
            "追加の起点と一緒でも付ける");
        Check.Equal($"--project|{project}|--out|{output}|--scripts",
            string.Join("|", PackageContentStep.SeedPakArguments(project, output, System.Array.Empty<string>(), debugBuild: false)),
            "配布用は付けない");
        Check.Equal("--debug-build", SeedPakProcess.DebugBuildOption, "SeedPak の引数名（editor/tools/SeedPak/SeedPakArguments.cs と一致）");
    }

    /// <summary>pak の指紋。</summary>
    private static void FingerprintIncludesMark()
    {
        using var temp = new TempDir();
        temp.WriteFile("Game/assets/project_settings.json", "{ \"start_scene\": \"assets://scenes/Main.scene\" }");
        temp.WriteFile("Game/assets/scenes/Main.scene", "{}");
        var engine = new AndroidEnginePaths(temp.Combine("repo"));
        var project = AndroidProjectResolver.Resolve(temp.Combine("Game"), null)!;

        var legacy = AndroidStepFingerprints.PackageContent(engine, project);
        var release = AndroidStepFingerprints.PackageContent(engine, project, null, debugBuild: false);
        var debug = AndroidStepFingerprints.PackageContent(engine, project, null, debugBuild: true);
        var debugAgain = AndroidStepFingerprints.PackageContent(engine, project, null, debugBuild: true);

        Check.Equal(legacy.Inputs, release.Inputs, "印を入れないとき（配布用）は従来の式のまま");
        Check.True(debug.Inputs != release.Inputs, "debug → release へ切り替えた最初のビルドで pak を作り直す（印の入った pak を使い回さない）");
        Check.Equal(debug.Inputs, debugAgain.Inputs, "開発用のままなら同じ指紋（2 回目は飛ばす）");
        Check.Equal(release.Output, debug.Output, "出力の同一性（置き場の中身）は印の指定に依らない");

        var development = AndroidProjectResolver.Resolve(null, temp.Combine("Game/assets"))!;
        Check.Equal(AndroidStepFingerprints.PackageContent(engine, development, null, debugBuild: false).Inputs,
            AndroidStepFingerprints.PackageContent(engine, development, null, debugBuild: true).Inputs,
            "pak を入れない開発用の APK（--assets-dir）は印を材料にしない（置き場を空にするだけ）");
    }

    /// <summary>印のある配布物は不合格。</summary>
    private static void ReleaseArtifactWithMarkFails()
    {
        using var temp = new TempDir();
        var markedPak = WritePak(temp, "marked.pak", withMark: true);
        var cleanPak = WritePak(temp, "clean.pak", withMark: false);

        // APK（pak は非圧縮で assets/seed/ の下）
        var markedApk = Archive(temp.Combine("marked.apk"), AndroidArtifactInspector.ApkPakEntry, markedPak, CompressionLevel.NoCompression);
        var cleanApk = Archive(temp.Combine("clean.apk"), AndroidArtifactInspector.ApkPakEntry, cleanPak, CompressionLevel.NoCompression);
        Check.Equal("assets/seed/assets.pak", AndroidArtifactInspector.ApkPakEntry, "APK の中の pak の名前（runtime/android/native の APK_PACKAGE_ROOT）");

        var markedApkFact = AndroidArtifactInspector.ReadDebugBuildMark(markedApk, AndroidPackageFormat.Apk);
        Check.True(markedApkFact is { PakFound: true, MarkPresent: true, Error: null }, $"印を見つける: {markedApkFact}");
        var markedApkItem = AndroidArtifactChecks.DebugBuildMark(markedApkFact);
        Check.True(markedApkItem.Severity == AndroidRequirementSeverity.Failure && markedApkItem.Id == AndroidRequirementIds.DebugBuildMark,
            $"印のある release の APK は不合格: {markedApkItem.Describe()}");
        Check.True(markedApkItem.Detail.Contains(PackageLayout.BuildManifestEntryPath) && markedApkItem.Detail.Contains("IsDebugAllowed"),
            $"何が入っていて何が起きるかを示す: {markedApkItem.Detail}");

        var cleanApkItem = AndroidArtifactChecks.DebugBuildMark(AndroidArtifactInspector.ReadDebugBuildMark(cleanApk, AndroidPackageFormat.Apk));
        Check.Equal(AndroidRequirementSeverity.Pass, cleanApkItem.Severity, $"印の無い APK は合格: {cleanApkItem.Describe()}");

        // AAB（base/ の下。pak が圧縮されていても表を先頭から読める）
        var markedAab = Archive(temp.Combine("marked.aab"), "base/" + AndroidArtifactInspector.ApkPakEntry, markedPak, CompressionLevel.Optimal);
        var cleanAab = Archive(temp.Combine("clean.aab"), "base/" + AndroidArtifactInspector.ApkPakEntry, cleanPak, CompressionLevel.Optimal);
        Check.Equal(AndroidRequirementSeverity.Failure,
            AndroidArtifactChecks.DebugBuildMark(AndroidArtifactInspector.ReadDebugBuildMark(markedAab, AndroidPackageFormat.Aab)).Severity,
            "印のある AAB は不合格");
        Check.Equal(AndroidRequirementSeverity.Pass,
            AndroidArtifactChecks.DebugBuildMark(AndroidArtifactInspector.ReadDebugBuildMark(cleanAab, AndroidPackageFormat.Aab)).Severity,
            "印の無い AAB は合格");
        Check.True(AndroidArtifactInspector.ReadDebugBuildMark(markedAab, AndroidPackageFormat.Apk) is { PakFound: false },
            "AAB を APK として読むと assets/seed/ には無い（形式ごとの置き場）");

        // 判定の一覧にも項目が入る（ビルドの後の判定）
        var facts = new AndroidArtifactFacts { ArtifactPath = markedApk, Format = AndroidPackageFormat.Apk, DebugBuildMark = markedApkFact };
        var items = AndroidArtifactChecks.Evaluate(new PlayRequirements(), facts, new AndroidArtifactExpectation(Identity(), null));
        Check.True(items.Single(i => i.Id == AndroidRequirementIds.DebugBuildMark).Severity == AndroidRequirementSeverity.Failure,
            "配布物の判定の一覧に debug_build_mark の不合格が入る");
    }

    /// <summary>pak の無い配布物・読めない pak・調べていないもの。</summary>
    private static void MarkCheckEdgeCases()
    {
        using var temp = new TempDir();
        var noPak = Archive(temp.Combine("nopak.apk"), "assets/seed/readme.txt", Encoding.UTF8.GetBytes("x"), CompressionLevel.Optimal);
        var noPakItem = AndroidArtifactChecks.DebugBuildMark(AndroidArtifactInspector.ReadDebugBuildMark(noPak, AndroidPackageFormat.Apk));
        Check.Equal(AndroidRequirementSeverity.Pass, noPakItem.Severity, $"pak の無い配布物は印の入れ先が無いので合格: {noPakItem.Detail}");

        var broken = Archive(temp.Combine("broken.apk"), AndroidArtifactInspector.ApkPakEntry, Encoding.ASCII.GetBytes("NOPE"), CompressionLevel.NoCompression);
        var brokenItem = AndroidArtifactChecks.DebugBuildMark(AndroidArtifactInspector.ReadDebugBuildMark(broken, AndroidPackageFormat.Apk));
        Check.True(brokenItem.Severity == AndroidRequirementSeverity.Failure && brokenItem.Detail.Contains("確かめられません"),
            $"読めない pak は不合格: {brokenItem.Detail}");

        File.WriteAllText(temp.Combine("notzip.apk"), "zip ではない");
        Check.Equal(AndroidRequirementSeverity.Failure,
            AndroidArtifactChecks.DebugBuildMark(AndroidArtifactInspector.ReadDebugBuildMark(temp.Combine("notzip.apk"), AndroidPackageFormat.Apk)).Severity,
            "zip として開けない配布物は不合格");
        Check.Equal(AndroidRequirementSeverity.Failure, AndroidArtifactChecks.DebugBuildMark(null).Severity, "調べていなければ不合格");
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>SeedAndroid の引数から指定を作る。</summary>
    private static AndroidRunRequest Request(params string[] args)
    {
        var parsed = SeedAndroidArguments.Parse(args);
        Check.True(parsed.Error is null, $"解釈できる: {parsed.Error}");
        return SeedAndroidArguments.ToRequest(parsed.CommandLine!, null);
    }

    /// <summary>識別情報（配布物の判定の期待値。印の項目には使わない）。</summary>
    private static AndroidAppIdentity Identity() =>
        AndroidAppIdentityResolver.Resolve(
            new SEEDEditor.ProjectSettings.AndroidAppSettings { ApplicationId = "com.studio.game", VersionCode = 1, VersionName = "1.0" },
            "Game", "Game", hasProjectContext: true);

    /// <summary>
    /// 本物の PakWriter で pak を作る（収録ファイルは無く、見本のシーンと〈印を入れるなら〉印を生成エントリとして入れる）。
    /// </summary>
    private static byte[] WritePak(TempDir temp, string name, bool withMark)
    {
        var entries = new List<PakGeneratedEntry> { new(SceneEntry, Encoding.UTF8.GetBytes("{}")) };
        if (withMark) entries.Add(new PakGeneratedEntry(PackageLayout.BuildManifestEntryPath, Encoding.UTF8.GetBytes(MarkJson)));
        var path = temp.Combine(name);
        PakWriter.Write(path, temp.Path, System.Array.Empty<CollectedAsset>(), generated: entries);
        return File.ReadAllBytes(path);
    }

    /// <summary>zip（APK / AAB の代わり）に 1 項目だけ入れて作る。</summary>
    private static string Archive(string path, string entryName, byte[] content, CompressionLevel level)
    {
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(entryName, level);
            using var stream = entry.Open();
            stream.Write(content);
        }
        return path;
    }
}
