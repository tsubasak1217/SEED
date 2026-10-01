// ============================================================
//  DebugBuildMarkTests.cs — pak の「開発用のビルドの印」の単体テスト
//
//  【検証範囲】
//   - AssetPakBuilder.Write(debugBuild) … 開発用なら印（.seed/build.json）が入り、中身がランタイムの形式
//                                        （runtime/src/engine/pak/build_manifest.rs: {"format":1,"debug":true}）。配布用（既定）は入らない
//   - 予約の名前                      … 利用者の同じ名前のファイルは、配布用でも開発用でも pak に入らない（警告を出す）
//   - PakWriter の生成エントリ          … 収録ファイルと同じ名前なら書き始める前に例外（pak を作らない）
//   - SeedPakArguments の --debug-build … 既定は false・値を取らない・--scripts-only とは併用できない
//   - PackageLayout.BuildManifestEntryPath … ランタイム側 package_layout::BUILD_MANIFEST_ENTRY と同じ文字列
//  ランタイムは印を SEED.Application.IsDebugBuild として読み、IsDebugAllowed = !IsPackaged || IsDebugBuild にする
//  （開発用の APK でもデバッグの命令・開発用の機能を使えるように。docs/packaging.md §4・docs/android.md §24.8）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SEEDEditor.Packaging;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Packaging.Pak;
using SEEDEditor.Tools.SeedPak;
using SpriteRigTests;

namespace SEEDEditor.Tests.PackagingCollector;

/// <summary>開発用のビルドの印のテスト。</summary>
public static class DebugBuildMarkTests
{
    /// <summary>ランタイムが読める印の形式の版（runtime/src/engine/pak/build_manifest.rs の SUPPORTED_FORMAT）。</summary>
    private const int RuntimeSupportedFormat = 1;

    /// <summary>利用者が予約の名前に置いてしまったファイルの中身（印と同じ形。紛れ込むと開発用の機能が開いてしまうもの）。</summary>
    private const string UserFileContent = """{"format":1,"debug":true,"user_file":true}""";

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("ビルドの印: 開発用の PAK には .seed/build.json が入り、中身はランタイムの形式（format=1・debug=true）", DebugBuildWritesMark);
        h.Add("ビルドの印: 配布用（既定）の PAK には入らない・入れても他のエントリは同じバイト列", ReleaseBuildHasNoMark);
        h.Add("ビルドの印: 予約の名前の利用者のファイルは、配布用でも開発用でも pak に入らない（警告を出す）", ReservedUserFileIsExcluded);
        h.Add("ビルドの印: PakWriter は生成エントリが収録ファイルと同じ名前なら書き始める前に止める", WriterRejectsCollidingGeneratedEntry);
        h.Add("SeedPak の引数: --debug-build は既定 false・値を取らない・--scripts-only とは併用できない", ParsesDebugBuildOption);
        h.Add("ビルドの印の名前がランタイム側 package_layout.rs と一致する（.seed/build.json）", EntryNameMatchesRuntime);
    }

    // ============================================================
    //  テスト本体
    // ============================================================

    /// <summary>開発用の PAK には印が入る。</summary>
    private static void DebugBuildWritesMark()
    {
        using var fx = new AssetFixture();
        var result = AssetPakBuilder.Collect(fx.Root, new AssetPackagingSettings(), runtimeSourceRoot: null, log: null);
        var pakPath = PackageLayout.PakPath(Path.Combine(fx.Root, "out_debug"));
        var log = new List<string>();

        var stats = AssetPakBuilder.Write(pakPath, fx.Root, result, log.Add, progress: null, debugBuild: true);
        var pak = new PakTestReader(pakPath);

        Check.Equal(result.Included.Count + 1, pak.EntryCount, "収録ファイル ＋ 印 1 件");
        Check.Equal(result.Included.Count + 1, stats.EntryCount, "統計のエントリ数も印を含む");
        Check.Equal(pak.FileLength, pak.DataEnd(), "印を足してもデータ部の終端がファイル末尾と一致する");

        var json = pak.ReadText(PackageLayout.BuildManifestEntryPath);
        Check.True(json is not null, "印のエントリが読める");
        using var document = JsonDocument.Parse(json!);
        Check.Equal(RuntimeSupportedFormat, document.RootElement.GetProperty("format").GetInt32(), "format はランタイムが読める版");
        Check.True(document.RootElement.GetProperty("debug").GetBoolean(), "debug=true");
        Check.True(!json!.StartsWith("\uFEFF", StringComparison.Ordinal), "BOM なし（ランタイムの serde_json は BOM を読めない）");

        // 収録ファイルの中身は印を足しても変わらない
        var expected = File.ReadAllBytes(Path.Combine(fx.Root, "models", "box.glb"));
        Check.True(expected.SequenceEqual(pak.Read("models/box.glb")!), "バイナリの中身が変わっていない");
        Check.True(log.Any(line => line.Contains("開発用のビルドの印: 入れる")), "ログに印を入れたことが出る");
    }

    /// <summary>配布用の PAK には印が入らない。</summary>
    private static void ReleaseBuildHasNoMark()
    {
        using var fx = new AssetFixture();
        var result = AssetPakBuilder.Collect(fx.Root, new AssetPackagingSettings(), runtimeSourceRoot: null, log: null);
        var releasePath = PackageLayout.PakPath(Path.Combine(fx.Root, "out_release"));
        var debugPath = PackageLayout.PakPath(Path.Combine(fx.Root, "out_debug"));
        var log = new List<string>();

        AssetPakBuilder.Write(releasePath, fx.Root, result, log.Add, progress: null);
        AssetPakBuilder.Write(debugPath, fx.Root, result, log: null, progress: null, debugBuild: true);
        var release = new PakTestReader(releasePath);
        var debug = new PakTestReader(debugPath);

        Check.Equal(result.Included.Count, release.EntryCount, "配布用は収録ファイルだけ");
        Check.True(release.Read(PackageLayout.BuildManifestEntryPath) is null, "配布用には印のエントリが無い");
        Check.True(log.Any(line => line.Contains("開発用のビルドの印: 入れない")), "ログに入れなかったことが出る");
        foreach (var path in release.EntryPaths)
        {
            Check.True(release.Read(path)!.SequenceEqual(debug.Read(path)!), $"印の有無で他のエントリの中身は変わらない: {path}");
        }
    }

    /// <summary>予約の名前の利用者のファイルは pak に入らない。</summary>
    private static void ReservedUserFileIsExcluded()
    {
        using var fx = new AssetFixture();
        fx.WriteText(PackageLayout.BuildManifestEntryPath, UserFileContent);
        // 全ファイル同梱にして、利用者のファイルを確実に収録一覧へ載せる
        var result = AssetPakBuilder.Collect(fx.Root, new AssetPackagingSettings { IncludeAllFiles = true }, runtimeSourceRoot: null, log: null);
        Check.True(result.Included.Any(asset => PakBuildManifest.IsReservedPath(asset.RelPath)), "前提: 収録一覧には利用者のファイルが載っている");

        var releaseLog = new List<string>();
        var releasePath = PackageLayout.PakPath(Path.Combine(fx.Root, "out_release"));
        AssetPakBuilder.Write(releasePath, fx.Root, result, releaseLog.Add, progress: null);
        Check.True(new PakTestReader(releasePath).Read(PackageLayout.BuildManifestEntryPath) is null,
            "配布用: 利用者のファイルが印として紛れ込まない");
        Check.True(releaseLog.Any(line => line.Contains("予約の名前") && line.Contains(PackageLayout.BuildManifestEntryPath)),
            "外したことを警告する");

        var debugPath = PackageLayout.PakPath(Path.Combine(fx.Root, "out_debug"));
        AssetPakBuilder.Write(debugPath, fx.Root, result, log: null, progress: null, debugBuild: true);
        var debugMark = new PakTestReader(debugPath).ReadText(PackageLayout.BuildManifestEntryPath);
        Check.Equal(Encoding.UTF8.GetString(PakBuildManifest.CreateDebugBuildContent()), debugMark,
            "開発用: 印はパッケージ化が作った中身（利用者のファイルではない）");

        Check.True(PakBuildManifest.IsReservedPath(".SEED\\Build.JSON"), "予約の名前の照合は区切り・大小文字を問わない（端末の pak の引き方と同じ）");
        Check.True(!PakBuildManifest.IsReservedPath("seed/build.json"), "似た名前は予約ではない");
    }

    /// <summary>PakWriter は名前の重なる生成エントリを拒む。</summary>
    private static void WriterRejectsCollidingGeneratedEntry()
    {
        using var fx = new AssetFixture();
        var result = AssetPakBuilder.Collect(fx.Root, new AssetPackagingSettings(), runtimeSourceRoot: null, log: null);
        var pakPath = PackageLayout.PakPath(Path.Combine(fx.Root, "out_collide"));
        var colliding = new[] { new PakGeneratedEntry("MODELS\\Box.glb", new byte[] { 1 }) };

        try
        {
            PakWriter.Write(pakPath, fx.Root, result.Included, generated: colliding);
            throw new AssertionException("収録ファイルと同じ名前の生成エントリで止まらない");
        }
        catch (ArgumentException)
        {
            // 期待どおり
        }
        Check.True(!File.Exists(pakPath), "書き始める前に止まる（壊れた pak を残さない）");
    }

    /// <summary>SeedPak の --debug-build。</summary>
    private static void ParsesDebugBuildOption()
    {
        static SeedPakParseResult Parse(params string[] args) => SeedPakArguments.Parse(args);

        var plain = Parse("--project", @"D:\Game", "--out", @"D:\out", "--scripts");
        Check.True(plain.Error is null && plain.Options is { DebugBuild: false }, "既定は配布用（印を入れない）");

        var debug = Parse("--project", @"D:\Game", "--out", @"D:\out", "--scripts", SeedPakArguments.DebugBuildOption);
        Check.True(debug.Error is null && debug.Options is { DebugBuild: true, Content: SeedPakContent.PakAndScripts },
            $"--debug-build は値を取らないフラグ: {debug.Error}");

        var first = Parse(SeedPakArguments.DebugBuildOption, "--project", @"D:\Game", "--out", @"D:\out");
        Check.True(first.Error is null && first.Options is { DebugBuild: true }, "どの位置に書いてもよい");

        var scriptsOnly = Parse("--project", @"D:\Game", "--out", @"D:\out", "--scripts-only", SeedPakArguments.DebugBuildOption);
        Check.True(scriptsOnly.Options is null && scriptsOnly.Error!.Contains(SeedPakArguments.DebugBuildOption),
            "PAK を作らない --scripts-only とは併用できない（印が入ったと思い込ませない）");

        Check.Equal("--debug-build", SeedPakArguments.DebugBuildOption, "SeedAndroid（SeedPakProcess.DebugBuildOption）が写している名前");
    }

    /// <summary>印の名前の規約。</summary>
    private static void EntryNameMatchesRuntime()
    {
        // 名前がずれると、開発用の APK でも開発用の機能が開かない・配布前の検査が印を見落とす（ビルドは通る）ため、文字列で固定する
        Check.Equal(".seed/build.json", PackageLayout.BuildManifestEntryPath, "ビルドの印のエントリ名が規約と違う");
        Check.Equal(RuntimeSupportedFormat, PakBuildManifest.FormatVersion, "形式の版がランタイムの SUPPORTED_FORMAT と違う");
    }
}
