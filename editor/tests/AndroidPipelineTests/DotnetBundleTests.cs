using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SEEDEditor.Android;
using SEEDEditor.Android.Dotnet;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>同梱 .NET の組み立て（一時フォルダに偽のパックを作って dotnet-root 形式へ組み立てる）。</summary>
public static class DotnetBundleTests
{
    /// <summary>テスト用の設定（実物の dotnet_runtime.json と同じ形。x86_64 だけのパックを作る）。</summary>
    private const string SettingsJson = """
        {
          "dotnet_runtime": "coreclr",
          "version": "10.0.12",
          "framework": "Microsoft.NETCore.App",
          "target_framework": "net10.0",
          "abis": { "arm64-v8a": "arm64", "x86_64": "x64" },
          "runtimes": {
            "coreclr": {
              "runtime_pack": "Microsoft.NETCore.App.Runtime.android-{arch}",
              "runtime_rid": "android-{arch}",
              "host_pack": "Microsoft.NETCore.App.Runtime.linux-bionic-{arch}",
              "host_rid": "linux-bionic-{arch}",
              "excluded_native_files": [ "libmscordaccore.so" ],
              "java_libraries": [ "libSystem.Security.Cryptography.Native.Android.jar" ]
            },
            "mono": {
              "runtime_pack": "Microsoft.NETCore.App.Runtime.linux-bionic-{arch}",
              "runtime_rid": "linux-bionic-{arch}",
              "host_pack": "Microsoft.NETCore.App.Runtime.linux-bionic-{arch}",
              "host_rid": "linux-bionic-{arch}"
            }
          },
          "host_libraries": { "libhostfxr.so": "host/fxr/{version}", "libhostpolicy.so": "shared/{framework}/{version}" },
          "native_library_mode": "symlink",
          "runtime_properties": { "System.Globalization.Invariant": "true" }
        }
        """;

    /// <summary>パックの deps.json（native に入れる .so・入れない .so・.a が並ぶ）。</summary>
    private const string DepsJson = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0/android-x64" },
          "targets": {
            ".NETCoreApp,Version=v10.0/android-x64": {
              "Microsoft.NETCore.App.Runtime.android-x64/10.0.12": {
                "runtime": { "lib/net10.0/System.Runtime.dll": { "assemblyVersion": "10.0.0.0" } },
                "native": {
                  "native/libcoreclr.so": { "fileVersion": "0.0.0.0" },
                  "native/libmscordaccore.so": { "fileVersion": "0.0.0.0" },
                  "native/libSystem.Native.a": { "fileVersion": "0.0.0.0" }
                }
              }
            }
          },
          "libraries": { "Microsoft.NETCore.App.Runtime.android-x64/10.0.12": { "type": "runtimepack", "serviceable": false, "sha512": "sha512-ab+c/d==" } }
        }
        """;

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("同梱 .NET: 設定を読み、ひな形を ABI の値で埋める", ParsesSettings);
        harness.Add("同梱 .NET: dotnet-root 形式へ組み立てる（.so は jniLibs・BCL は assets・目録）", AssemblesDotnetRoot);
        harness.Add("同梱 .NET: 2 回目は content_id が同じなので組み立てを省く", SecondAssemblyIsUnchanged);
        harness.Add("同梱 .NET: content_id は設定・ABI で変わる", ContentIdDependsOnSettingsAndAbi);
        harness.Add("同梱 .NET: 今回の ABI に無い前回分を消す", RemovesOtherAbis);
        harness.Add("同梱 .NET: 暗号ライブラリの .jar を libs/ へ置く", PlacesJavaLibraries);
        harness.Add("同梱 .NET: NuGet の取り寄せ用プロジェクトは版を固定した PackageDownload", RestoreProjectPinsVersion);
    }

    /// <summary>偽のパック（x86_64）を作り、パック名 → フォルダを返す。</summary>
    private static IReadOnlyDictionary<string, string> CreateFakePacks(TempDir temp)
    {
        const string runtimeRoot = "packs/microsoft.netcore.app.runtime.android-x64/10.0.12/runtimes/android-x64";
        const string hostRoot = "packs/microsoft.netcore.app.runtime.linux-bionic-x64/10.0.12/runtimes/linux-bionic-x64";
        temp.WriteFile($"{runtimeRoot}/lib/net10.0/System.Runtime.dll", "bcl");
        temp.WriteFile($"{runtimeRoot}/lib/net10.0/Microsoft.NETCore.App.runtimeconfig.json", "{}");
        temp.WriteFile($"{runtimeRoot}/lib/net10.0/Microsoft.NETCore.App.deps.json", DepsJson);
        temp.WriteFile($"{runtimeRoot}/native/libcoreclr.so", "coreclr");
        temp.WriteFile($"{runtimeRoot}/native/libmscordaccore.so", "dac");
        temp.WriteFile($"{runtimeRoot}/native/libSystem.Native.a", "static");
        temp.WriteFile($"{runtimeRoot}/native/libSystem.Security.Cryptography.Native.Android.jar", "jar");
        temp.WriteFile($"{hostRoot}/native/libhostfxr.so", "fxr");
        temp.WriteFile($"{hostRoot}/native/libhostpolicy.so", "policy");
        return new Dictionary<string, string>
        {
            ["Microsoft.NETCore.App.Runtime.android-x64"] = temp.Combine("packs/microsoft.netcore.app.runtime.android-x64/10.0.12"),
            ["Microsoft.NETCore.App.Runtime.linux-bionic-x64"] = temp.Combine("packs/microsoft.netcore.app.runtime.linux-bionic-x64/10.0.12"),
        };
    }

    /// <summary>設定の読み取り。</summary>
    private static void ParsesSettings()
    {
        var settings = DotnetRuntimeSettings.Parse(SettingsJson);
        Check.Equal("coreclr", settings.Kind, "種類");
        Check.Equal("Microsoft.NETCore.App.Runtime.android-arm64", settings.RuntimePackId(AndroidAbis.Arm64), "パック名");
        Check.Equal("Microsoft.NETCore.App.Runtime.linux-bionic-x64", settings.HostPackId(AndroidAbis.X86_64), "host のパック名");
        Check.Equal("shared/Microsoft.NETCore.App/10.0.12", settings.Expand("shared/{framework}/{version}", AndroidAbis.X86_64), "ひな形");
        Check.Equal("libhostfxr.so", settings.HostLibraries[0].Key, "host_libraries は書かれた順");
        Check.Equal("true", settings.RuntimeProperties.Single().Value, "ランタイムプロパティ");
        Check.Equal(0, DotnetRuntimeSettings.Parse(SettingsJson.Replace("\"dotnet_runtime\": \"coreclr\"", "\"dotnet_runtime\": \"mono\"")).Runtime.JavaLibraries.Count, "mono は .jar なし");
    }

    /// <summary>組み立て。</summary>
    private static void AssemblesDotnetRoot()
    {
        using var temp = new TempDir();
        var packs = CreateFakePacks(temp);
        var staging = temp.Combine("seedDotnet");
        var settings = DotnetRuntimeSettings.Parse(SettingsJson);

        var result = DotnetRuntimeBundle.AssembleAbi(settings, AndroidAbis.X86_64, packs, staging);
        Check.True(!result.Unchanged, "初回は組み立てる");
        Check.Equal(16, result.ContentId.Length, "content_id の桁数");

        // .so は jniLibs/<ABI>/（除外したデバッガ用と .a は入れない）
        var jni = Directory.GetFiles(Path.Combine(staging, "jniLibs", "x86_64")).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Check.Equal("libcoreclr.so,libhostfxr.so,libhostpolicy.so", string.Join(",", jni), ".so の一覧");

        // BCL・runtimeconfig・deps.json は assets/seed/dotnet/<ABI>/shared/<framework>/<版>/
        var frameworkDir = Path.Combine(staging, "assets", "seed", "dotnet", "x86_64", "shared", "Microsoft.NETCore.App", "10.0.12");
        Check.True(File.Exists(Path.Combine(frameworkDir, "System.Runtime.dll")), "BCL");
        Check.True(File.Exists(Path.Combine(frameworkDir, "Microsoft.NETCore.App.runtimeconfig.json")), "runtimeconfig");
        using (var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(frameworkDir, "Microsoft.NETCore.App.deps.json"))))
        {
            var native = deps.RootElement.GetProperty("targets").GetProperty(".NETCoreApp,Version=v10.0/android-x64")
                .GetProperty("Microsoft.NETCore.App.Runtime.android-x64/10.0.12").GetProperty("native");
            var names = native.EnumerateObject().Select(p => p.Name).ToArray();
            Check.Equal("native/libcoreclr.so", string.Join(",", names), "deps.json の native は入れた .so だけ");
            Check.Equal("sha512-ab+c/d==", deps.RootElement.GetProperty("libraries").EnumerateObject().Single().Value.GetProperty("sha512").GetString(), "他の値はそのまま");
        }

        // 目録（エンジンの BundleManifest の形）
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "assets", "seed", "dotnet", "x86_64", "bundle.json")));
        var root = manifest.RootElement;
        Check.Equal(1, root.GetProperty("format_version").GetInt32(), "書式の版");
        Check.Equal("coreclr", root.GetProperty("runtime").GetString(), "種類");
        Check.Equal("x86_64", root.GetProperty("abi").GetString(), "ABI");
        Check.Equal(result.ContentId, root.GetProperty("content_id").GetString(), "content_id");
        Check.Equal("host/fxr/10.0.12/libhostfxr.so", root.GetProperty("hostfxr").GetString(), "hostfxr の置き場");
        Check.Equal("symlink", root.GetProperty("native_library_mode").GetString(), ".so の置き方");
        Check.Equal("true", root.GetProperty("runtime_properties").GetProperty("System.Globalization.Invariant").GetString(), "プロパティは文字列");
        var files = root.GetProperty("files").EnumerateArray().ToList();
        var hostfxr = files.Single(f => f.GetProperty("path").GetString() == "host/fxr/10.0.12/libhostfxr.so");
        Check.Equal("native_library", hostfxr.GetProperty("source").GetString(), "hostfxr は native_library");
        Check.True(!hostfxr.TryGetProperty("size", out _), "native_library は大きさを持たない");
        Check.True(files.Any(f => f.GetProperty("path").GetString() == "shared/Microsoft.NETCore.App/10.0.12/libhostpolicy.so"), "hostpolicy は framework の中");
        var bcl = files.Single(f => f.GetProperty("path").GetString() == "shared/Microsoft.NETCore.App/10.0.12/System.Runtime.dll");
        Check.Equal("asset", bcl.GetProperty("source").GetString(), "BCL は asset");
        Check.Equal(3L, bcl.GetProperty("size").GetInt64(), "asset は大きさを持つ");
        Check.True(files.All(f => !f.GetProperty("path").GetString()!.Contains('\\')), "目録のパスは / 区切り");
    }

    /// <summary>2 回目は省く。</summary>
    private static void SecondAssemblyIsUnchanged()
    {
        using var temp = new TempDir();
        var packs = CreateFakePacks(temp);
        var staging = temp.Combine("seedDotnet");
        var settings = DotnetRuntimeSettings.Parse(SettingsJson);
        var first = DotnetRuntimeBundle.AssembleAbi(settings, AndroidAbis.X86_64, packs, staging);
        var second = DotnetRuntimeBundle.AssembleAbi(settings, AndroidAbis.X86_64, packs, staging);
        Check.True(second.Unchanged, "変更なし");
        Check.Equal(first.ContentId, second.ContentId, "同じ content_id");
        Check.Equal(first.ContentId, DotnetRuntimeBundle.ReadStagedContentId(staging, AndroidAbis.X86_64), "置き場の目録から読める");
    }

    /// <summary>content_id の材料。</summary>
    private static void ContentIdDependsOnSettingsAndAbi()
    {
        var settings = DotnetRuntimeSettings.Parse(SettingsJson);
        var changed = DotnetRuntimeSettings.Parse(SettingsJson.Replace("\"symlink\"", "\"copy\""));
        Check.True(DotnetRuntimeBundle.ContentId(settings, AndroidAbis.X86_64) != DotnetRuntimeBundle.ContentId(changed, AndroidAbis.X86_64), "設定で変わる");
        Check.True(DotnetRuntimeBundle.ContentId(settings, AndroidAbis.X86_64) != DotnetRuntimeBundle.ContentId(settings, AndroidAbis.Arm64), "ABI で変わる");
        Check.Equal(DotnetRuntimeBundle.ContentId(settings, AndroidAbis.X86_64), DotnetRuntimeBundle.ContentId(settings, AndroidAbis.X86_64), "同じ材料なら同じ");
    }

    /// <summary>他の ABI の前回分を消す。</summary>
    private static void RemovesOtherAbis()
    {
        using var temp = new TempDir();
        var staging = temp.Combine("seedDotnet");
        temp.WriteFile("seedDotnet/assets/seed/dotnet/arm64-v8a/bundle.json", "{}");
        temp.WriteFile("seedDotnet/jniLibs/arm64-v8a/libcoreclr.so", "x");
        temp.WriteFile("seedDotnet/jniLibs/x86_64/libcoreclr.so", "x");
        var removed = new List<string>();
        DotnetRuntimeBundle.RemoveOtherAbis(staging, new[] { AndroidAbis.X86_64 }, removed.Add);
        Check.True(!Directory.Exists(temp.Combine("seedDotnet/jniLibs/arm64-v8a")), "arm64 の .so を消す");
        Check.True(!Directory.Exists(temp.Combine("seedDotnet/assets/seed/dotnet/arm64-v8a")), "arm64 の asset を消す");
        Check.True(Directory.Exists(temp.Combine("seedDotnet/jniLibs/x86_64")), "今回の ABI は残す");
        Check.Equal(2, removed.Count, "消したことを報告する");
    }

    /// <summary>.jar。</summary>
    private static void PlacesJavaLibraries()
    {
        using var temp = new TempDir();
        var packs = CreateFakePacks(temp);
        var staging = temp.Combine("seedDotnet");
        var placed = DotnetRuntimeBundle.UpdateJavaLibraries(DotnetRuntimeSettings.Parse(SettingsJson), AndroidAbis.X86_64, packs, staging);
        Check.Equal(1, placed.Count, "1 つ");
        Check.True(File.Exists(Path.Combine(staging, "libs", "libSystem.Security.Cryptography.Native.Android.jar")), "libs/ に置く");

        var mono = DotnetRuntimeSettings.Parse(SettingsJson.Replace("\"dotnet_runtime\": \"coreclr\"", "\"dotnet_runtime\": \"mono\""));
        DotnetRuntimeBundle.UpdateJavaLibraries(mono, AndroidAbis.X86_64, packs, staging);
        Check.True(!Directory.Exists(Path.Combine(staging, "libs")), ".jar が無い種類では libs/ ごと消す");
    }

    /// <summary>取り寄せ用プロジェクト。</summary>
    private static void RestoreProjectPinsVersion()
    {
        var project = NuGetRuntimePackRestorer.CreateRestoreProject(new[] { "A.Pack", "B.Pack", "A.Pack" }, "10.0.12", "net10.0");
        Check.True(project.Contains("<PackageDownload Include=\"A.Pack\" Version=\"[10.0.12]\" />"), "版を固定");
        Check.Equal(2, project.Split("PackageDownload").Length - 1, "重複はまとめる");
        Check.True(project.Contains("<TargetFramework>net10.0</TargetFramework>"), "TFM");
    }
}
