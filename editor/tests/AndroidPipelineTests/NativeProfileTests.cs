using System;
using System.IO;
using System.Linq;
using SEEDEditor.Android;
using SEEDEditor.Android.Native;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Steps;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;
using SEEDEditor.Runtime.BuildConfig;
using SEEDEditor.Tools.SeedAndroid;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// libSEED.so の cargo のプロファイルの決め方（2026-09-28。src/Android/Native/AndroidNativeProfile.cs）。
///
/// 開発用の APK の .so を PC の Play と同じ構成の表（editor/config/runtime_build_configs.json）の既定（develop）で作ること、
/// --release・配布用は release、--native-profile debug で cargo の dev へ戻せること、指定の誤りを弾くこと、
/// 指紋・cargo の引数・出力の場所・jniLibs への写しを確かめる。cargo・端末は使わない。
/// </summary>
public static class NativeProfileTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("libSEED.so の構成: 指定が無ければ構成の表の既定（develop・--profile develop）", DefaultIsCatalogDefault);
        harness.Add("libSEED.so の構成: リポジトリの runtime_build_configs.json の既定は develop（PC の Play と同じ）", RepositoryCatalogDefaultsToDevelop);
        harness.Add("libSEED.so の構成: --release・配布用は release（--release）、debug は cargo の dev（引数なし）", ReleaseAndDebugProfiles);
        harness.Add("libSEED.so の構成: 知らない id・配布用と食い違う id は誤り（準備で弾く）", RejectsInvalidRequests);
        harness.Add("libSEED.so の構成: 出力の場所は runtime/target/<ターゲット>/<出力フォルダ>/libSEED.so", BuiltLibraryPathFollowsTargetDir);
        harness.Add("libSEED.so の構成: プロファイルが変われば .so の指紋が変わる（作り直す）", FingerprintIncludesProfile);
        harness.Add("libSEED.so の構成: cargo-ndk が写さなかった出力は jniLibs へ写す（同じ大きさなら触らない）", CopiesProfileOutputWhenMissing);
        harness.Add("SeedAndroid の引数: --native-profile と設定 JSON の native_profile（使える場面の検査）", ParsesNativeProfileOption);
    }

    /// <summary>組み込みの構成の表（develop が既定）。</summary>
    private static RuntimeBuildConfigCatalog Catalog => RuntimeBuildConfigCatalog.BuiltIn();

    /// <summary>指定が無ければ表の既定。</summary>
    private static void DefaultIsCatalogDefault()
    {
        var profile = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest(), Catalog);
        Check.Equal("develop", profile.ConfigId, "既定の構成");
        Check.Equal("develop", profile.CargoProfile, "cargo のプロファイル");
        Check.Equal(AndroidNativeProfileSource.CatalogDefault, profile.Source, "表の既定から決めた");
        Check.Equal("--profile develop", string.Join(' ', profile.CargoArguments), "cargo ndk へ --profile develop");
        Check.True(!profile.IsRelease, "release ではない");
        Check.True(profile.Describe().Contains("--profile develop"), $"ログの説明: {profile.Describe()}");
    }

    /// <summary>リポジトリの表の既定が develop（Android の既定と PC の Play の既定が同じ表から決まる）。</summary>
    private static void RepositoryCatalogDefaultsToDevelop()
    {
        var engine = AndroidEnginePaths.Locate(AppContext.BaseDirectory, Environment.CurrentDirectory);
        Check.True(engine is not null, "リポジトリが見つかる");
        var catalog = AndroidNativeProfileResolver.LoadCatalog(engine!, out var warnings);
        Check.True(warnings.Count == 0, $"表を警告なしで読める: {string.Join(" / ", warnings)}");
        Check.True(catalog.SourcePath is not null && catalog.SourcePath.EndsWith(RuntimeBuildConfigCatalog.FileName, StringComparison.Ordinal),
            $"リポジトリの表を読んだ: {catalog.SourcePath}");
        var profile = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest(), catalog);
        Check.Equal("develop", profile.ConfigId, "リポジトリの表の既定");
        Check.Equal("dev", AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { NativeProfile = "debug" }, catalog).CargoProfile,
            "debug は cargo の dev");
    }

    /// <summary>--release・配布用・debug。</summary>
    private static void ReleaseAndDebugProfiles()
    {
        var release = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { Release = true }, Catalog);
        Check.Equal("release", release.CargoProfile, "--release は release");
        Check.Equal(AndroidNativeProfileSource.ReleaseSwitch, release.Source, "--release から決めた");
        Check.Equal("--release", string.Join(' ', release.CargoArguments), "cargo ndk へ --release（従来と同じ引数）");

        var variant = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { Variant = AndroidBuildVariant.Release }, Catalog);
        Check.Equal("release", variant.CargoProfile, "配布用は release");
        Check.Equal(AndroidNativeProfileSource.ReleaseVariant, variant.Source, "配布用から決めた");
        Check.True(variant.IsRelease, "IsRelease");

        var debug = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { NativeProfile = "debug" }, Catalog);
        Check.Equal("dev", debug.CargoProfile, "debug は cargo の dev");
        Check.Equal("debug", debug.TargetDir, "dev の出力は debug フォルダ");
        Check.Equal(0, debug.CargoArguments.Count, "dev は引数なし（従来の開発用と同じ）");
        Check.Equal(AndroidNativeProfileSource.Requested, debug.Source, "指定から決めた");

        // 大文字小文字・前後の空白は同じ id とみなす（エディタの設定の書き方の揺れ）
        Check.Equal("develop", AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { NativeProfile = " Develop " }, Catalog).ConfigId,
            "大文字・空白の揺れ");
        // 空白だけの指定は「指定なし」
        Check.Equal(AndroidNativeProfileSource.CatalogDefault,
            AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { NativeProfile = "  " }, Catalog).Source, "空白だけは既定");
        // --release と release の指定は食い違わない
        Check.True(AndroidNativeProfileResolver.Validate(new AndroidRunRequest { Release = true, NativeProfile = "release" }, Catalog) is null,
            "--release ＋ release は可");
    }

    /// <summary>指定の誤り。</summary>
    private static void RejectsInvalidRequests()
    {
        var unknown = AndroidNativeProfileResolver.Validate(new AndroidRunRequest { NativeProfile = "fast" }, Catalog);
        Check.True(unknown is not null && unknown.Contains("debug") && unknown.Contains("develop"), $"知らない id は使える値を添えて誤り: {unknown}");
        var thrown = false;
        try
        {
            AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { NativeProfile = "fast" }, Catalog);
        }
        catch (AndroidPipelineException ex)
        {
            thrown = ex.Kind == AndroidFailureKind.InvalidRequest;
        }
        Check.True(thrown, "Resolve は InvalidRequest の例外");

        Check.True(AndroidNativeProfileResolver.Validate(
            new AndroidRunRequest { Variant = AndroidBuildVariant.Release, NativeProfile = "debug" }, Catalog) is not null,
            "配布用に debug は誤り（配布物に最適化なしの .so を入れない）");
        Check.True(AndroidNativeProfileResolver.Validate(new AndroidRunRequest { Release = true, NativeProfile = "develop" }, Catalog) is not null,
            "--release と develop は食い違い");
    }

    /// <summary>出力の場所。</summary>
    private static void BuiltLibraryPathFollowsTargetDir()
    {
        var engine = new AndroidEnginePaths(@"C:\repo");
        var develop = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest(), Catalog);
        Check.Equal(Path.Combine(@"C:\repo", "runtime", "target", "aarch64-linux-android", "develop", "libSEED.so"),
            develop.BuiltLibraryPath(engine, AndroidAbis.Arm64), "develop の出力");
        var debug = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { NativeProfile = "debug" }, Catalog);
        Check.Equal(Path.Combine(@"C:\repo", "runtime", "target", "x86_64-linux-android", "debug", "libSEED.so"),
            debug.BuiltLibraryPath(engine, AndroidAbis.X86_64), "dev の出力は debug フォルダ");
        Check.Equal(Path.Combine(@"C:\repo", "editor", "config"), engine.RuntimeBuildConfigDir, "構成の表のフォルダ");
    }

    /// <summary>指紋にプロファイルが入る。</summary>
    private static void FingerprintIncludesProfile()
    {
        using var temp = new TempDir();
        var engine = new AndroidEnginePaths(temp.Path);
        var develop = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest(), Catalog);
        var debug = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { NativeProfile = "debug" }, Catalog);
        var release = AndroidNativeProfileResolver.Resolve(new AndroidRunRequest { Release = true }, Catalog);
        var a = AndroidStepFingerprints.Native(engine, AndroidAbis.Arm64, develop, ndkPath: null);
        var b = AndroidStepFingerprints.Native(engine, AndroidAbis.Arm64, debug, ndkPath: null);
        var c = AndroidStepFingerprints.Native(engine, AndroidAbis.Arm64, release, ndkPath: null);
        var again = AndroidStepFingerprints.Native(engine, AndroidAbis.Arm64, develop, ndkPath: null);
        Check.True(a.Inputs != b.Inputs && a.Inputs != c.Inputs && b.Inputs != c.Inputs, "プロファイルごとに入力の指紋が違う");
        Check.Equal(a.Inputs, again.Inputs, "同じプロファイルなら同じ指紋");
    }

    /// <summary>cargo-ndk が写さなかったときの写し。</summary>
    private static void CopiesProfileOutputWhenMissing()
    {
        using var temp = new TempDir();
        var built = temp.WriteFile("target/aarch64-linux-android/develop/libSEED.so", "develop-build-output");
        var library = temp.Combine("jniLibs/arm64-v8a/libSEED.so");
        var log = new AndroidPhaseLog(AndroidPipelinePhase.NativeBuild, null);

        // 無ければ写す
        NativeBuildStep.EnsureCopiedFromProfileOutput(built, library, log);
        Check.Equal("develop-build-output", File.ReadAllText(library), "無ければプロファイルの出力を写す");

        // 同じ大きさなら触らない（cargo-ndk が写したもの）
        File.WriteAllText(library, "copied-by-cargo-ndk!");
        Check.Equal(new FileInfo(built).Length, new FileInfo(library).Length, "前提: 同じ大きさ");
        NativeBuildStep.EnsureCopiedFromProfileOutput(built, library, log);
        Check.Equal("copied-by-cargo-ndk!", File.ReadAllText(library), "同じ大きさなら触らない");

        // 大きさが違えば（別のプロファイルの写し）写し直す
        File.WriteAllText(library, "stale");
        NativeBuildStep.EnsureCopiedFromProfileOutput(built, library, log);
        Check.Equal("develop-build-output", File.ReadAllText(library), "大きさが違えば写し直す");

        // 出力が無ければ何もしない（target-dir を変えている環境。jniLibs の有無は呼び出し元が確かめる）
        var other = temp.Combine("jniLibs/x86_64/libSEED.so");
        NativeBuildStep.EnsureCopiedFromProfileOutput(temp.Combine("missing/libSEED.so"), other, log);
        Check.True(!File.Exists(other), "出力が無ければ写さない");
    }

    /// <summary>SeedAndroid の --native-profile と設定 JSON。</summary>
    private static void ParsesNativeProfileOption()
    {
        var line = SeedAndroidArguments.Parse(new[] { "run", "--project", "P", "--native-profile", "debug" }).CommandLine!;
        Check.Equal("debug", line.NativeProfile, "--native-profile");
        Check.Equal("debug", SeedAndroidArguments.ToRequest(line, null).NativeProfile, "指定へ入る");

        using var temp = new TempDir();
        var configPath = temp.WriteFile("android.json", "{ \"project\": \"Game\", \"native_profile\": \"release\" }");
        var config = RunRequestConfig.Load(configPath, out var error)!;
        Check.True(error is null, $"設定 JSON を読めた: {error}");
        Check.Equal("release", config.NativeProfile, "設定 JSON の native_profile");
        var fromConfig = SeedAndroidArguments.ToRequest(SeedAndroidArguments.Parse(new[] { "build" }).CommandLine!, config);
        Check.Equal("release", fromConfig.NativeProfile, "指定が無ければ設定 JSON");
        var overridden = SeedAndroidArguments.ToRequest(SeedAndroidArguments.Parse(new[] { "build", "--native-profile", "develop" }).CommandLine!, config);
        Check.Equal("develop", overridden.NativeProfile, "コマンドラインが優先");
        Check.True(SeedAndroidArguments.ToRequest(SeedAndroidArguments.Parse(new[] { "build" }).CommandLine!, null).NativeProfile is null,
            "どちらにも無ければ null（表の既定）");

        foreach (var bad in new[]
        {
            new[] { "run", "--native-profile" },
            new[] { "push", "--native-profile", "debug" },
            new[] { "stop", "--native-profile", "debug" },
        })
        {
            Check.True(SeedAndroidArguments.Parse(bad).Error is not null, $"誤り: {string.Join(' ', bad)}");
        }
        Check.True(SeedAndroidArguments.Parse(new[] { "install", "--native-profile", "release" }).Error is null, "install では使える");
    }
}
