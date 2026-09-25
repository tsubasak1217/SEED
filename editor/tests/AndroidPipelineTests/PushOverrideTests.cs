using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Steps;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// 端末に置いた上書き（push の DLL＝files/bin/、実行中の差し替えのアセット＝files/assets/）の解除（段階C-4・docs/android.md §17.7・§23.4）。
/// 消すかの判断（--project の APK を入れ直した直後〈install・run〉と run の起動の前。Push・--push-scripts〈DLL〉・--assets-dir では消さない）、
/// 消し方と上書き層の記録の合わせ方（偽の消す口で。端末は使わない）、run-as の引数（自分のアプリのデータフォルダの相対パスだけ）、
/// 端末の出力の読み方、Output の文言。
/// </summary>
public static class PushOverrideTests
{
    /// <summary>テストのアプリ ID。</summary>
    private const string AppId = "com.seedengine.runtime";

    /// <summary>テストの端末のシリアル。</summary>
    private const string Serial = "2B011TEST";

    /// <summary>記録に入れておく「前に送った」ファイル（消せなかったときに残ることを確かめる）。</summary>
    private const string SeededFile = "scenes/Main.scene";

    /// <summary>記録に入れておく指紋。</summary>
    private const string SeededDigest = "SEEDED";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("上書きの解除: DLL は --project の APK を入れ直した直後（install・run）と run の起動の前に消し、Push・--push-scripts・--assets-dir・プロジェクト無し・Build では消さない／記録を作り直す条件", DecidesWhenToClear);
        harness.Add("上書きの解除: 消し方（場面ごとに消すフォルダ・消したときだけ 1 行・消せなければ警告）と上書き層の記録（消せたら作り直し、消せなければ残す）", ClearsAndKeepsRecordInSync);
        harness.Add("push の上書きの解除: run-as の引数は自分のアプリのデータフォルダの相対パスだけ（絶対パス・.. は受け付けない）", BuildsRemoveArguments);
        harness.Add("push の上書きの解除: 端末の出力（removed＝消した・空＝無かった・それ以外＝失敗）と Output の文言", ParsesRemoveOutput);
    }

    /// <summary>消すかの判断と、上書き層の記録を作り直すかの判断。</summary>
    private static void DecidesWhenToClear()
    {
        using var temp = new TempDir();
        temp.WriteFile("Game/assets/project_settings.json", "{ \"start_scene\": \"assets://scenes/Main.scene\" }");
        var packaged = AndroidProjectResolver.Resolve(temp.Combine("Game"), null)!;
        var development = AndroidProjectResolver.Resolve(null, temp.Combine("Game/assets"))!;
        const PushedOverrideMoment launch = PushedOverrideMoment.BeforeLaunch;
        const PushedOverrideMoment install = PushedOverrideMoment.AfterInstall;

        // ── run の起動の前（段階C-4 からの規則）──
        Check.True(PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Run }, packaged, launch),
            "run（--project）は APK の内容を正とするので消す");
        Check.True(!PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Push }, packaged, launch),
            "push はこれから files/bin/ に置くので消さない");
        Check.True(!PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Run, PushScripts = true }, packaged, launch),
            "run --push-scripts は起動の前に置いたものを使うので消さない");
        Check.True(!PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Run }, development, launch),
            "開発用の --assets-dir は APK に bin/ が無く files/bin/ が唯一の置き場なので消さない");
        Check.True(!PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Run }, null, launch), "プロジェクト無し");
        foreach (var goal in new[] { AndroidRunGoal.Build, AndroidRunGoal.Install })
        {
            Check.True(!PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = goal }, packaged, launch), $"{goal} は起動しないので起動の前には消さない");
        }

        // ── APK を入れ直した直後（adb install -r はアプリのデータを残すので、入れ直しても上書きが残る）──
        foreach (var goal in new[] { AndroidRunGoal.Install, AndroidRunGoal.Run })
        {
            Check.True(PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = goal }, packaged, install), $"{goal} で APK を入れ直したら消す");
            Check.True(!PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = goal, PushScripts = true }, packaged, install),
                $"{goal} --push-scripts はこの後で置くので消さない");
            Check.True(!PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = goal }, development, install),
                $"{goal} の開発用の --assets-dir は消さない");
        }
        foreach (var goal in new[] { AndroidRunGoal.Build, AndroidRunGoal.Push })
        {
            Check.True(!PushedOverrides.ClearsScripts(new AndroidRunRequest { Goal = goal }, packaged, install), $"{goal} は APK を入れない");
        }
        Check.Equal("files/bin", AndroidRuntimeContract.RemoteScriptsDir, "消すのは push の置き場と同じ files/bin（端末の script_sources.rs と一致）");

        // ── 上書き層の記録を作り直すか（消せた・元から無かった → 作り直す、消せなかった → 残す、消さない場面 → 開発用の run の起動の前だけ）──
        var run = new AndroidRunRequest { Goal = AndroidRunGoal.Run };
        var installOnly = new AndroidRunRequest { Goal = AndroidRunGoal.Install };
        Check.True(PushedOverrides.ResetsOverlayRecord(installOnly, install, assetsCleared: true), "入れ直して消せたら作り直す");
        Check.True(!PushedOverrides.ResetsOverlayRecord(installOnly, install, assetsCleared: false), "消せなければ残す（端末に残った上書きと合ったまま）");
        Check.True(!PushedOverrides.ResetsOverlayRecord(installOnly, install, assetsCleared: null), "消さない場面の install（--assets-dir）は触らない");
        Check.True(PushedOverrides.ResetsOverlayRecord(run, launch, assetsCleared: true), "run の起動の前に消せたら作り直す");
        Check.True(!PushedOverrides.ResetsOverlayRecord(run, launch, assetsCleared: false), "run でも消せなければ残す");
        Check.True(PushedOverrides.ResetsOverlayRecord(run, launch, assetsCleared: null), "開発用の run は起動の前に pak の無い記録にする");
    }

    /// <summary>消し方と、上書き層の記録の合わせ方（偽の消す口）。</summary>
    private static void ClearsAndKeepsRecordInSync()
    {
        using var temp = new TempDir();
        temp.WriteFile("Game/assets/project_settings.json", "{}");
        var packaged = AndroidProjectResolver.Resolve(temp.Combine("Game"), null)!;
        var development = AndroidProjectResolver.Resolve(null, temp.Combine("Game/assets"))!;
        var engine = new AndroidEnginePaths(temp.Combine("repo"));
        var pakPath = Path.Combine(engine.ApkPackageDir, PackageLayout.PakFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(pakPath)!);
        File.WriteAllBytes(pakPath, new byte[] { 1, 2, 3, 4 });
        var recordPath = AndroidAssetOverlayState.PathFor(packaged, engine);
        var install = new AndroidRunRequest { Goal = AndroidRunGoal.Install };

        // 1) install で入れ直した: files/bin → files/assets の順に消し、消したものごとに 1 行、記録は「送ったもの無し・今の pak」
        SeedRecord(recordPath);
        var remover = new FakeRemover(AndroidRuntimeContract.RemoteScriptsDir, AndroidRuntimeContract.RemoteAssetsDir);
        var lines = Clear(Scope(PushedOverrideMoment.AfterInstall, install, packaged, engine), remover);
        Check.Equal("files/bin,files/assets", string.Join(",", remover.Calls), "DLL → アセットの順に消す");
        Check.Equal($"Info:{PushedOverrides.ScriptsClearedMessage}|Info:{PushedOverrides.AssetsClearedMessage}", string.Join("|", lines),
            "消したものごとに 1 行");
        var record = AndroidAssetOverlayState.Load(recordPath).RecordFor(Serial, AppId)!;
        Check.Equal(0, record.Files.Count, "送った記録は空に（端末の上書き層は空）");
        Check.Equal(4L, record.PakSize, "土台は今の置き場の pak（入れ直した APK の中身）");

        // 2) 元から無い（run のインストールの工程）: 何も出さず、記録は作り直す
        SeedRecord(recordPath);
        remover = new FakeRemover();
        lines = Clear(Scope(PushedOverrideMoment.AfterInstall, new AndroidRunRequest { Goal = AndroidRunGoal.Run }, packaged, engine), remover);
        Check.Equal(2, remover.Calls.Count, "両方とも確かめる");
        Check.Equal(0, lines.Count, "無ければ何も出さない");
        Check.Equal(0, AndroidAssetOverlayState.Load(recordPath).RecordFor(Serial, AppId)!.Files.Count, "記録は作り直す");

        // 3) files/assets を消せない: 警告を出して続け、記録は残す（端末に残った上書きと合ったまま＝差分を取り違えない）
        SeedRecord(recordPath);
        remover = new FakeRemover(AndroidRuntimeContract.RemoteScriptsDir, AndroidRuntimeContract.RemoteAssetsDir)
        {
            Failing = AndroidRuntimeContract.RemoteAssetsDir,
        };
        lines = Clear(Scope(PushedOverrideMoment.AfterInstall, install, packaged, engine), remover);
        Check.True(lines.Count == 2
                   && lines[0] == $"Info:{PushedOverrides.ScriptsClearedMessage}"
                   && lines[1].StartsWith("Warning:", StringComparison.Ordinal)
                   && lines[1].Contains(AndroidRuntimeContract.RemoteAssetsDir, StringComparison.Ordinal),
            $"DLL は消した 1 行・アセットは警告: {string.Join(" | ", lines)}");
        Check.Equal(SeededDigest, AndroidAssetOverlayState.Load(recordPath).RecordFor(Serial, AppId)!.Files[SeededFile], "消せなければ記録を残す");

        // 4) --push-scripts の install: DLL はこの後で置くので消さず、アセットだけ消す
        remover = new FakeRemover(AndroidRuntimeContract.RemoteScriptsDir, AndroidRuntimeContract.RemoteAssetsDir);
        Clear(Scope(PushedOverrideMoment.AfterInstall, install with { PushScripts = true }, packaged, engine), remover);
        Check.Equal("files/assets", string.Join(",", remover.Calls), "--push-scripts では files/bin を消さない");

        // 5) 開発用の --assets-dir: install では何も消さず記録も触らない。run の起動の前は消さずに pak の無い記録にする
        var devRecordPath = AndroidAssetOverlayState.PathFor(development, engine);
        SeedRecord(devRecordPath);
        remover = new FakeRemover(AndroidRuntimeContract.RemoteScriptsDir, AndroidRuntimeContract.RemoteAssetsDir);
        Clear(Scope(PushedOverrideMoment.AfterInstall, install, development, engine), remover);
        Check.Equal(0, remover.Calls.Count, "--assets-dir は files/bin・files/assets が唯一の置き場なので消さない");
        Check.Equal(SeededDigest, AndroidAssetOverlayState.Load(devRecordPath).RecordFor(Serial, AppId)!.Files[SeededFile], "install は記録を触らない");
        Clear(Scope(PushedOverrideMoment.BeforeLaunch, new AndroidRunRequest { Goal = AndroidRunGoal.Run }, development, engine), remover);
        var devRecord = AndroidAssetOverlayState.Load(devRecordPath).RecordFor(Serial, AppId)!;
        Check.True(remover.Calls.Count == 0 && devRecord.Files.Count == 0 && devRecord.PakSize is null,
            "開発用の run は消さずに pak の無い記録にする");
    }

    /// <summary>run-as の引数。</summary>
    private static void BuildsRemoveArguments()
    {
        Check.Equal($"exec-out|run-as|{AppId}|sh|-c|if [ -e files/bin ]; then rm -rf files/bin && echo removed; fi",
            string.Join("|", AdbClient.RunAsRemoveArguments(AppId, AndroidRuntimeContract.RemoteScriptsDir)),
            "exec-out で run-as（アプリの権限・作業フォルダはそのアプリのデータフォルダ）。スクリプトは 1 引数のまま");

        foreach (var bad in new[] { "/data/local/tmp", "../other", "files/../../x", "", "  " })
        {
            try
            {
                AdbClient.RunAsRemoveArguments(AppId, bad);
                throw new AssertionException($"受け付けてしまった: '{bad}'");
            }
            catch (ArgumentException)
            {
                // アプリのデータフォルダの外を指す・空の指定は受け付けない
            }
        }
    }

    /// <summary>端末の出力の読み方と Output の文言。</summary>
    private static void ParsesRemoveOutput()
    {
        Check.Equal(true, AdbClient.ParseRunAsRemoveOutput("removed\r\n"), "removed は消した");
        Check.Equal(true, AdbClient.ParseRunAsRemoveOutput("run-as: warning\nremoved\n"), "端末の注意の行が混ざっても removed があれば消した（rm が成功したときだけ出る）");
        Check.Equal(false, AdbClient.ParseRunAsRemoveOutput(""), "空はもともと無かった");
        Check.Equal(false, AdbClient.ParseRunAsRemoveOutput("  \r\n"), "空白だけも無かった");
        Check.Equal(null, AdbClient.ParseRunAsRemoveOutput("run-as: package not debuggable: com.x"), "run-as のエラーは失敗");
        Check.Equal(null, AdbClient.ParseRunAsRemoveOutput("rm: files/bin/x.dll: Permission denied"), "rm のエラーは失敗");

        Check.True(PushedOverrides.ScriptsClearedMessage.StartsWith("push した DLL の上書きを解除しました", StringComparison.Ordinal),
            $"Output の 1 行: {PushedOverrides.ScriptsClearedMessage}");
        Check.True(PushedOverrides.ScriptsClearedMessage.Contains("files/bin/") && PushedOverrides.ScriptsClearedMessage.Contains("APK の bin/"),
            "何を消し、どれを使うか");
        Check.True(!PushedOverrides.ScriptsClearedMessage.Contains("起動します", StringComparison.Ordinal),
            "文言はインストールの後でも正しい（起動するとは言わない）");
    }

    /// <summary>解除の材料を作る。</summary>
    private static PushedOverrideScope Scope(
        PushedOverrideMoment moment, AndroidRunRequest request, AndroidProjectInfo project, AndroidEnginePaths engine) =>
        new(moment, request, project, Serial, AppId, engine);

    /// <summary>解除を走らせ、出た行（種類:本文）を返す。</summary>
    private static List<string> Clear(PushedOverrideScope scope, FakeRemover remover)
    {
        var collector = new LineCollector();
        PushedOverrides.ClearAsync(scope, remover.RemoveAsync, new AndroidPhaseLog(AndroidPipelinePhase.Install, collector), CancellationToken.None)
            .GetAwaiter().GetResult();
        return collector.Lines;
    }

    /// <summary>「前に送ったファイルがある」記録を書いておく。</summary>
    private static void SeedRecord(string path)
    {
        var state = new AndroidAssetOverlayState();
        state.Devices[Serial] = new AndroidAssetOverlayRecord
        {
            ApplicationId = AppId,
            Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [SeededFile] = SeededDigest },
        };
        state.Save(path);
    }

    /// <summary>偽の消す口（呼ばれたフォルダを覚える。あるものは消して true、無ければ false、Failing は失敗）。</summary>
    private sealed class FakeRemover
    {
        /// <summary>端末にあるフォルダ。</summary>
        private readonly HashSet<string> _present;

        /// <summary>端末にあるフォルダを指定して作る。</summary>
        /// <param name="present">あるフォルダ。</param>
        public FakeRemover(params string[] present) => _present = new HashSet<string>(present, StringComparer.Ordinal);

        /// <summary>消せないフォルダ（run-as の失敗を真似る）。</summary>
        public string? Failing { get; init; }

        /// <summary>呼ばれたフォルダ（順に）。</summary>
        public List<string> Calls { get; } = new();

        /// <summary>消す（<see cref="RemoteDirectoryRemover"/> の形）。</summary>
        public Task<bool> RemoveAsync(string remoteDir, CancellationToken cancellationToken)
        {
            Calls.Add(remoteDir);
            if (remoteDir == Failing) throw new AdbCommandException($"run-as で {remoteDir} を消せませんでした（テスト）");
            return Task.FromResult(_present.Remove(remoteDir));
        }
    }

    /// <summary>工程のログの行を集める（同期。Progress&lt;T&gt; は別スレッドへ投げるので使わない）。</summary>
    private sealed class LineCollector : IProgress<AndroidPipelineEvent>
    {
        /// <summary>集めた行（種類:本文）。</summary>
        public List<string> Lines { get; } = new();

        /// <inheritdoc />
        public void Report(AndroidPipelineEvent value)
        {
            if (value is AndroidLogLine line) Lines.Add($"{line.Level}:{line.Text}");
        }
    }
}
