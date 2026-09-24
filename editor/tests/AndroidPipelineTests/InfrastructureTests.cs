using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Project;
using SEEDEditor.Tools.SeedAndroid;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>指紋・tar・行の読み取り・記録・道具の解決・プロジェクトの読み取り・SeedAndroid の引数。</summary>
public static class InfrastructureTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("指紋: ファイルの大きさ・更新時刻・パラメータが変われば変わる（除外フォルダは見ない）", FingerprintTracksChanges);
        harness.Add("出力の同一性: 無いファイルは missing、空・無いフォルダは empty", OutputIdentityMarkers);
        harness.Add("tar: フォルダの中身を相対パス・権限 0600/0700 で書く", TarWritesDirectory);
        harness.Add("行の読み取り: \\n・\\r\\n・単独の \\r で区切り、UTF-8 と ANSI の混在を読む", LineReaderSplitsAndDecodes);
        harness.Add("記録: 置き場の記録と実行状態は保存 → 読み込みで往復し、壊れた・版違いは空", StateRoundTrip);
        harness.Add("道具: 環境変数 → 既定の場所の順に探し、無ければ理由付きのエラー", ToolchainDetection);
        harness.Add("プロジェクト: .seedproj の名前・設定の向きと android 節を読む", ProjectResolution);
        harness.Add("プロジェクト設定: 壊れた JSON・知らない向きは警告して既定値", ProjectSettingsWarnings);
        harness.Add("SeedAndroid の引数: サブコマンドとオプション・誤りの検出", ParsesArguments);
        harness.Add("SeedAndroid の引数: 設定 JSON の上にコマンドラインを重ねる（相対パスは JSON のフォルダから）", MergesConfig);
        harness.Add("子プロセス: 出力を行ごとに集め、終了コード・標準入力・環境変数を渡す", ChildProcessCapturesOutput);
        harness.Add("子プロセス: 中断すると子を止めて OperationCanceledException", ChildProcessCancellationKillsChild);
    }

    /// <summary>Windows のコマンドプロンプト（子プロセスのテストに使う。どの Windows にもある）。</summary>
    private static string CommandPrompt => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>出力・終了コード・標準入力・環境変数。</summary>
    private static void ChildProcessCapturesOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var capture = ChildProcessRunner.CaptureAsync(new ChildProcessSpec
        {
            FileName = CommandPrompt,
            Arguments = new[] { "/d", "/c", "echo out-%SEED_TEST_VALUE%& echo err 1>&2& exit /b 3" },
            Environment = new Dictionary<string, string?> { ["SEED_TEST_VALUE"] = "ok" },
        }, CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(3, capture.ExitCode, "終了コード");
        Check.Equal("out-ok", capture.StandardOutput.Single().Trim(), "標準出力と環境変数");
        Check.Equal("err", capture.StandardError.Single().Trim(), "標準エラー");

        // 標準入力へ書いたものを子が読む（findstr は標準入力の行を出す）
        var echo = ChildProcessRunner.CaptureAsync(new ChildProcessSpec
        {
            FileName = CommandPrompt,
            Arguments = new[] { "/d", "/c", "findstr x" },
        }, CancellationToken.None, async (stream, token) =>
        {
            var bytes = Encoding.UTF8.GetBytes("abc\nxyz\n");
            await stream.WriteAsync(bytes, token);
        }).GetAwaiter().GetResult();
        Check.Equal("xyz", echo.StandardOutput.Single().Trim(), "標準入力");

        try
        {
            ChildProcessRunner.RunAsync(new ChildProcessSpec { FileName = MissingToolPath }, null, CancellationToken.None).GetAwaiter().GetResult();
            throw new AssertionException("無い実行ファイルで例外にならない");
        }
        catch (ChildProcessStartException)
        {
            // 期待どおり（道具が無いことを理由付きで知らせる）
        }
    }

    /// <summary>起動できない実行ファイル（テスト用の存在しないパス）。</summary>
    private static readonly string MissingToolPath = Path.Combine(Path.GetTempPath(), "seed_no_such_tool_" + Guid.NewGuid().ToString("N") + ".exe");

    /// <summary>中断。</summary>
    private static void ChildProcessCancellationKillsChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lines = new List<string>();
        try
        {
            // 約 30 秒かかるコマンド（ping の間隔 1 秒 × 30 回）を途中で止める
            ChildProcessRunner.RunAsync(new ChildProcessSpec
            {
                FileName = Path.Combine(Environment.SystemDirectory, "PING.EXE"),
                Arguments = new[] { "-n", "30", "127.0.0.1" },
            }, (_, line) => { lock (lines) lines.Add(line); }, cancellation.Token).GetAwaiter().GetResult();
            throw new AssertionException("中断しても例外にならない");
        }
        catch (OperationCanceledException)
        {
            // 期待どおり
        }
        Check.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"すぐ戻る（{watch.Elapsed.TotalSeconds:F1} 秒）");
        Check.True(lines.Count > 0, "止めるまでの出力は届いている");
    }

    /// <summary>指紋の変化。</summary>
    private static void FingerprintTracksChanges()
    {
        using var temp = new TempDir();
        var file = temp.WriteFile("src/a.rs", "fn main() {}");
        temp.WriteFile("src/target/ignored.o", "x");
        string Print() => new AndroidFingerprintBuilder().AddValue("abi", "x86_64").AddTree("src", temp.Combine("src"), new HashSet<string> { "target" }).Build();

        var first = Print();
        Check.Equal(first, Print(), "同じなら同じ");
        temp.WriteFile("src/target/ignored.o", "changed!");
        Check.Equal(first, Print(), "除外フォルダの変化は見ない");
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(1));
        var touched = Print();
        Check.True(touched != first, "更新時刻で変わる");
        temp.WriteFile("src/b.rs", "");
        Check.True(Print() != touched, "ファイルが増えれば変わる");
        var withParam = new AndroidFingerprintBuilder().AddValue("abi", "arm64-v8a").AddTree("src", temp.Combine("src"), new HashSet<string> { "target" }).Build();
        Check.True(withParam != Print(), "パラメータで変わる");
        var missing = new AndroidFingerprintBuilder().AddTree("nope", temp.Combine("nope")).Build();
        Check.True(missing != new AndroidFingerprintBuilder().AddTree("nope2", temp.Combine("nope")).Build(), "無いことも名前付きで材料になる");
    }

    /// <summary>出力の同一性。</summary>
    private static void OutputIdentityMarkers()
    {
        using var temp = new TempDir();
        Check.Equal(AndroidFingerprintBuilder.MissingMarker, AndroidOutputIdentity.OfFile(temp.Combine("none.apk")), "無いファイル");
        Check.Equal(AndroidOutputIdentity.Empty, AndroidOutputIdentity.OfDirectory(temp.Combine("none")), "無いフォルダ");
        Directory.CreateDirectory(temp.Combine("empty"));
        Check.Equal(AndroidOutputIdentity.Empty, AndroidOutputIdentity.OfDirectory(temp.Combine("empty")), "空のフォルダ");
        var apk = temp.WriteFile("app.apk", "12345");
        Check.True(AndroidOutputIdentity.OfFile(apk).StartsWith("5:"), "大きさ:更新時刻");
        temp.WriteFile("dir/x", "1");
        Check.True(AndroidOutputIdentity.OfDirectory(temp.Combine("dir")) != AndroidOutputIdentity.Empty, "中身があれば一覧のハッシュ");
    }

    /// <summary>tar の中身。</summary>
    private static void TarWritesDirectory()
    {
        using var temp = new TempDir();
        temp.WriteFile("assets/project_settings.json", "{}");
        temp.WriteFile("assets/scenes/Main.scene", "scene");
        var longName = string.Concat(Enumerable.Repeat("very_long_folder_name_", 8)) + "/model.glb";
        temp.WriteFile("assets/" + longName, "glb");

        using var buffer = new MemoryStream();
        var (files, bytes) = RunAsTarArchive.WriteDirectoryAsync(temp.Combine("assets"), buffer, CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(3, files, "ファイル数");
        Check.Equal(2L + 5 + 3, bytes, "バイト数");

        buffer.Position = 0;
        using var reader = new TarReader(buffer);
        var entries = new List<(string Name, TarEntryType Type, UnixFileMode Mode, string Content)>();
        while (reader.GetNextEntry() is { } entry)
        {
            var content = entry.DataStream is null ? string.Empty : new StreamReader(entry.DataStream).ReadToEnd();
            entries.Add((entry.Name, entry.EntryType, entry.Mode, content));
        }
        Check.True(entries.Any(e => e.Name == "project_settings.json" && e.Content == "{}"), "直下のファイル（./ を付けない）");
        Check.True(entries.Any(e => e.Name == "scenes/" && e.Type == TarEntryType.Directory), "フォルダのエントリ");
        Check.True(entries.Any(e => e.Name == "scenes/Main.scene" && e.Content == "scene"), "/ 区切りの相対パス");
        Check.True(entries.Any(e => e.Name == longName && e.Content == "glb"), $"100 文字を超える名前（{longName.Length} 文字）");
        Check.True(entries.Where(e => e.Type == TarEntryType.RegularFile).All(e => e.Mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite)), "ファイルは 0600");
        Check.True(entries.Where(e => e.Type == TarEntryType.Directory).All(e => e.Mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)), "フォルダは 0700");
    }

    /// <summary>行の読み取り。</summary>
    private static void LineReaderSplitsAndDecodes()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes("utf8 日本語\r\nsecond\nprogress 1\rprogress 2\r\n"));
        var cp932 = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
        bytes.AddRange(cp932.GetBytes("シフトJIS の行\n"));
        bytes.AddRange(Encoding.UTF8.GetBytes("last-without-newline"));

        var lines = new List<string>();
        MixedEncodingLineReader.ReadLinesAsync(new MemoryStream(bytes.ToArray()), lines.Add, CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal("utf8 日本語", lines[0], "UTF-8");
        Check.Equal("second", lines[1], "\\n");
        Check.Equal("progress 1", lines[2], "単独の \\r");
        Check.Equal("progress 2", lines[3], "\\r\\n は 1 つの区切り");
        Check.Equal("last-without-newline", lines[^1], "改行で終わらない最後の行");
        Check.Equal(6, lines.Count, "行数");
        // ANSI コードページが 932（日本語の Windows）のときだけ中身まで確かめる（他の環境では化けるのが正しい）
        if (OperatingSystem.IsWindows() && GetACP() == Cp932)
        {
            Check.Equal("シフトJIS の行", lines[4], "UTF-8 として読めない行は ANSI（CP932）");
        }
    }

    /// <summary>日本語の Windows の ANSI コードページ。</summary>
    private const uint Cp932 = 932;

    /// <summary>Windows の ANSI コードページ（MixedEncodingLineReader の読み替え先）。</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetACP();

    /// <summary>記録の往復。</summary>
    private static void StateRoundTrip()
    {
        using var temp = new TempDir();
        var stampsPath = temp.Combine("build/seed/step_stamps.json");
        var stamps = new AndroidStepStamps();
        stamps.Steps[AndroidStepKeys.Native(AndroidAbis.X86_64)] = new AndroidStepFingerprint("in", "out");
        stamps.Apk = new AndroidApkStamp("1:2", "sha", new[] { "x86_64" }, "com.x.y");
        stamps.Save(stampsPath);
        var loaded = AndroidStepStamps.Load(stampsPath);
        Check.Equal(new AndroidStepFingerprint("in", "out"), loaded.Steps[AndroidStepKeys.Native(AndroidAbis.X86_64)], "工程の記録");
        Check.Equal("sha", loaded.Apk?.Sha256, "APK の記録");
        Check.True(File.ReadAllText(stampsPath).Contains("\"inputs\""), "JSON のキーは snake_case");

        File.WriteAllText(stampsPath, "{ broken");
        Check.Equal(0, AndroidStepStamps.Load(stampsPath).Steps.Count, "壊れた記録は空");
        File.WriteAllText(stampsPath, "{\"format_version\": 99, \"steps\": {\"x\": {\"inputs\": \"a\", \"output\": \"b\"}}}");
        Check.Equal(0, AndroidStepStamps.Load(stampsPath).Steps.Count, "版違いは空");

        var runStatePath = AndroidRunState.PathForProject(temp.Path);
        Check.True(runStatePath.EndsWith(Path.Combine("cache", "android", "run_state.json")), "プロジェクトの cache/android/");
        var state = new AndroidRunState
        {
            LastTarget = new AndroidTargetRecord { Serial = "emulator-5554", Kind = "emulator", ApplicationId = "com.x.y", Abi = "x86_64" },
        };
        state.Installs["emulator-5554"] = new AndroidInstallStateRecord { ApplicationId = "com.x.y", ApkSha256 = "sha", InstalledPath = "/data/app/a/base.apk" };
        state.Save(runStatePath);
        var loadedState = AndroidRunState.Load(runStatePath);
        Check.Equal("emulator-5554", loadedState.LastTarget?.Serial, "前回の実行先");
        Check.Equal(new AndroidInstallRecord("com.x.y", "sha", "/data/app/a/base.apk"), loadedState.InstallRecordFor("emulator-5554"), "入れた APK の記録");
        Check.True(loadedState.InstallRecordFor("other") is null, "無い端末");
    }

    /// <summary>道具の解決。</summary>
    private static void ToolchainDetection()
    {
        using var temp = new TempDir();
        temp.WriteFile("sdk/platform-tools/adb.exe", "");
        temp.WriteFile("sdk/ndk/27.0.1/source.properties", "");
        temp.WriteFile("sdk/ndk/28.2.13676358/source.properties", "");
        temp.WriteFile("sdk/ndk/9.9.9/source.properties", "");
        temp.WriteFile("jdk/bin/java.exe", "");
        temp.WriteFile("profile/.cargo/bin/cargo.exe", "");
        temp.WriteFile("pf/dotnet/dotnet.exe", "");
        var env = new Dictionary<string, string?>
        {
            ["ANDROID_HOME"] = temp.Combine("sdk"),
            ["JAVA_HOME"] = temp.Combine("jdk"),
            ["USERPROFILE"] = temp.Combine("profile"),
            ["ProgramFiles"] = temp.Combine("pf"),
            ["PATH"] = string.Empty,
        };
        var toolchain = AndroidToolchain.Detect(name => env.GetValueOrDefault(name));
        Check.Equal(temp.Combine("sdk/platform-tools/adb.exe"), toolchain.RequireAdb(), "adb は SDK の platform-tools");
        Check.Equal(temp.Combine("sdk/ndk/28.2.13676358"), toolchain.RequireNdk(), "NDK は版の最も新しいもの（文字列の順ではない）");
        Check.True(toolchain.Notes.Any(n => n.Contains("最新 NDK")), "既定の場所を使ったことを知らせる");
        Check.Equal(temp.Combine("profile/.cargo/bin/cargo.exe"), toolchain.RequireCargo(), "cargo は %USERPROFILE%\\.cargo\\bin");
        Check.Equal(temp.Combine("pf/dotnet/dotnet.exe"), toolchain.RequireDotnet(), "dotnet は %ProgramFiles%\\dotnet");

        env["ANDROID_NDK_HOME"] = temp.Combine("sdk/ndk/27.0.1");
        Check.Equal(temp.Combine("sdk/ndk/27.0.1"), AndroidToolchain.Detect(name => env.GetValueOrDefault(name)).RequireNdk(), "ANDROID_NDK_HOME が優先");

        var empty = AndroidToolchain.Detect(_ => null);
        try
        {
            empty.RequireJavaHome();
            throw new AssertionException("JDK が無いのに例外にならない");
        }
        catch (AndroidPipelineException ex)
        {
            Check.Equal(AndroidFailureKind.Toolchain, ex.Kind, "失敗の種類");
            Check.True(ex.Message.Contains("JAVA_HOME"), $"対処を示す: {ex.Message}");
        }
    }

    /// <summary>プロジェクトの解決。</summary>
    private static void ProjectResolution()
    {
        using var temp = new TempDir();
        SeedProjectFile.Create("MyGame", "私のゲーム").Save(temp.Combine("MyGame/MyGame.seedproj"));
        temp.WriteFile("MyGame/assets/project_settings.json",
            "{ \"screen_orientation\": \" Landscape \", \"android\": { \"application_id\": \"com.example.mygame\", \"version_code\": 3 } }");

        var project = AndroidProjectResolver.Resolve(temp.Combine("MyGame"), null)!;
        Check.Equal(AndroidProjectMode.Packaged, project.Mode, "--project はパッケージ実行");
        Check.Equal(temp.Combine("MyGame/assets"), project.Folder.AssetsRoot, "アセットルート");
        Check.Equal("MyGame", project.Folder.ProjectName, "プロジェクト名");
        Check.Equal("landscape", project.Settings.ScreenOrientation, "向き（正規化）");
        var identity = AndroidProjectResolver.ResolveIdentity(project);
        Check.Equal("com.example.mygame", identity.ApplicationId, "書かれた ID");
        Check.Equal("私のゲーム", identity.AppName.Value, "名前は表示名");
        Check.Equal(3, identity.VersionCode.Value, "版");

        var dev = AndroidProjectResolver.Resolve(null, temp.Combine("MyGame/assets"))!;
        Check.Equal(AndroidProjectMode.DevelopmentAssets, dev.Mode, "--assets-dir は開発用");
        Check.Equal("MyGame", dev.Folder.ProjectName, "親の .seedproj から名前を読む");
        Check.Equal(temp.Combine("MyGame"), dev.Folder.ProjectRoot, "プロジェクトルートは親");

        Check.True(AndroidProjectResolver.Resolve(null, null) is null, "指定が無ければ null");
        try
        {
            AndroidProjectResolver.Resolve(temp.Combine("nope"), null);
            throw new AssertionException("無いフォルダで例外にならない");
        }
        catch (AndroidPipelineException ex)
        {
            Check.Equal(AndroidFailureKind.InvalidRequest, ex.Kind, "指定の誤り");
        }

        temp.WriteFile("Bad/assets/project_settings.json", "{ \"android\": { \"application_id\": \"bad id\" } }");
        try
        {
            AndroidProjectResolver.ResolveIdentity(AndroidProjectResolver.Resolve(temp.Combine("Bad"), null));
            throw new AssertionException("誤った ID で例外にならない");
        }
        catch (AndroidPipelineException ex)
        {
            Check.True(ex.Message.Contains("bad id"), $"誤りを示す: {ex.Message}");
        }
    }

    /// <summary>設定の読み取りの警告。</summary>
    private static void ProjectSettingsWarnings()
    {
        using var temp = new TempDir();
        temp.WriteFile("a/project_settings.json", "{ broken");
        var broken = AndroidProjectSettingsReader.Read(temp.Combine("a"));
        Check.Equal("both", broken.ScreenOrientation, "壊れた JSON は既定値");
        Check.Equal(1, broken.Warnings.Count, "警告");

        temp.WriteFile("b/project_settings.json", "{ \"screen_orientation\": \"sideways\", // コメント\n }");
        var unknown = AndroidProjectSettingsReader.Read(temp.Combine("b"));
        Check.Equal("both", unknown.ScreenOrientation, "知らない値は既定値");
        Check.True(unknown.Warnings.Single().Contains("sideways"), "知らない値を警告");

        var none = AndroidProjectSettingsReader.Read(temp.Combine("c"));
        Check.True(!none.Found && none.Warnings.Count == 0, "ファイルが無ければ既定値（警告なし）");
    }

    /// <summary>引数の解釈。</summary>
    private static void ParsesArguments()
    {
        var run = SeedAndroidArguments.Parse(new[] { "run", "--project", "P", "--serial", "S", "--abi", "x86_64", "--skip-rust", "--logcat-seconds", "20", "--log-file", "l.txt" });
        var line = run.CommandLine!;
        Check.Equal(SeedAndroidCommand.Run, line.Command, "サブコマンド");
        Check.Equal("P", line.ProjectDir, "--project");
        Check.Equal("x86_64", line.Abis!.Single(), "--abi");
        Check.True(line.SkipNativeBuild, "--skip-rust");
        Check.Equal(20, line.LogcatSeconds, "--logcat-seconds");

        Check.True(SeedAndroidArguments.Parse(Array.Empty<string>()).ShowHelp, "引数なしはヘルプ");
        Check.True(SeedAndroidArguments.Parse(new[] { "build", "--help" }).ShowHelp, "--help");
        foreach (var bad in new[]
        {
            new[] { "fly" }, new[] { "run", "--nope" }, new[] { "run", "--project" }, new[] { "run", "--abi", "mips" },
            new[] { "run", "--logcat-seconds", "-1" }, new[] { "run", "--project", "a", "--assets-dir", "b" },
        })
        {
            Check.True(SeedAndroidArguments.Parse(bad).Error is not null, $"誤り: {string.Join(' ', bad)}");
        }
    }

    /// <summary>設定 JSON との重ね合わせ。</summary>
    private static void MergesConfig()
    {
        using var temp = new TempDir();
        var configPath = temp.WriteFile("conf/android.json",
            "{ \"project\": \"../Game\", \"serial\": \"emulator-5554\", \"abis\": [\"x86_64\"], \"no_logcat\": true, \"log_file\": \"logs/l.txt\" }");
        var config = RunRequestConfig.Load(configPath, out var error)!;
        Check.True(error is null, $"読めた: {error}");
        Check.Equal(temp.Combine("Game"), config.ProjectDir, "相対パスは JSON のフォルダから");
        Check.Equal(temp.Combine("conf/logs/l.txt"), config.LogFile, "log_file も");

        var line = SeedAndroidArguments.Parse(new[] { "install", "--serial", "2B011JEGR02535", "--release" }).CommandLine!;
        var request = SeedAndroidArguments.ToRequest(line, config);
        Check.Equal(AndroidRunGoal.Install, request.Goal, "目的はサブコマンド");
        Check.Equal("2B011JEGR02535", request.Serial, "コマンドラインが優先");
        Check.Equal(temp.Combine("Game"), request.ProjectDir, "指定が無ければ設定 JSON");
        Check.True(request.Release && request.NoLogcat, "スイッチは足し合わせ");

        var assets = SeedAndroidArguments.ToRequest(SeedAndroidArguments.Parse(new[] { "run", "--assets-dir", "A" }).CommandLine!, config);
        Check.True(assets.ProjectDir is null && assets.AssetsDir == "A", "コマンドラインで出どころを選べば設定 JSON の他方は使わない");

        Check.True(RunRequestConfig.Load(temp.Combine("none.json"), out var missing) is null && missing is not null, "無いファイル");
    }
}
