using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.HotReload;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Steps;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Ipc;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Packaging.Pak;
using SEEDEditor.Tools.SeedAndroid;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// 実行中の差し替え（docs/android.md §23）の中核: 拡張子 → 命令の表、命令と応答の照合、差分の選び方
/// （手元の中身を pak と同じ形で読み、送った記録 → APK の pak と比べる）、上書き層の記録、tar の作り方、
/// 命令の送り方（応答待ち・時間切れ）、install・run での上書きの解除の判断、SeedAndroid の reload / push --assets の引数。
/// 端末・adb は使わない（pak は PakWriter で一時フォルダに作る。IPC は偽の通信路）。
/// </summary>
public static class HotReloadTests
{
    /// <summary>テストのアプリ ID。</summary>
    private const string AppId = "com.seedengine.runtime";

    /// <summary>テストの端末のシリアル。</summary>
    private const string Serial = "2B011TEST";

    /// <summary>応答を待つ上限（テストは短く）。</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>応答がそろうまで待つ上限（テストが止まらないように）。</summary>
    private const int WaitTimeoutMs = 5000;

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("差し替え: 拡張子 → 命令の表（.cs＝スクリプト・.scene＝シーン・他＝アセット・作業ファイル／生成物＝無視）", ClassifiesChangedFiles);
        harness.Add("差し替え: 相対パスの正規化（ランタイムと同じ規則）と命令 → 応答の宛先", NormalizesPathsAndTargets);
        harness.Add("差し替え: 応答の読み方（RELOAD_DONE / SKIPPED / FAILED・SCRIPTS_RELOADED の成功と失敗）", ParsesReplies);
        harness.Add("差し替え: 差分の選び方（端末に無い・違う → 送る／同じ → 送らない／読めない → 理由。送った記録が pak より優先）", PlansOverlay);
        harness.Add("差し替え: 手元の中身は pak と同じ形（シーンの絶対パスを assets:// へ）で、PakWriter の pak と同じ指紋になる", LocalContentMatchesPak);
        harness.Add("差し替え: APK の pak を比べる土台にしてよいか（記録が無い・pak が変わった → 比べない）・フォルダの接頭辞", DecidesPakBaseline);
        harness.Add("差し替え: 上書き層の記録の往復（アプリ ID が違えば使わない）と run の記録（pak の目印・送ったもの無し）", RoundTripsOverlayState);
        harness.Add("差し替え: files/assets を消す判断（--project の APK を入れ直した直後〈install・run〉と run の起動の前。push・--assets-dir・Build は消さない）", DecidesWhenToClearAssets);
        harness.Add("差し替え: tar（親フォルダを先に・メモリの中身とファイルの中身・日本語と空白の名前）", WritesPayloadTar);
        harness.Add("差し替え: 変わったファイルの分け方と、送ったものごとの命令（シーン → RELOAD_SCENE:・他 → RELOAD_ASSET:・.cs は出さない）", SplitsAndBuildsCommands);
        harness.Add("差し替え: 命令をまとめて送り、順不同の応答を宛先で照合する・届かなければ NoReply・送れなければ待たない", SendsCommandsAndWaitsReplies);
        harness.Add("SeedAndroid: reload scene / scripts / asset <パス>・push --assets の引数と誤り", ParsesReloadArguments);
    }

    /// <summary>拡張子 → 命令の表。</summary>
    private static void ClassifiesChangedFiles()
    {
        var expected = new Dictionary<string, AndroidHotReloadKind>
        {
            ["scripts/Player.cs"] = AndroidHotReloadKind.Scripts,
            ["scenes/Main.scene"] = AndroidHotReloadKind.Scene,
            [@"scenes\日本語 シーン.SCENE"] = AndroidHotReloadKind.Scene,
            ["textures/a.png"] = AndroidHotReloadKind.Asset,
            ["models/Fish.glb"] = AndroidHotReloadKind.Asset,
            ["shaders/water.wgsl"] = AndroidHotReloadKind.Asset,
            ["project_settings.json"] = AndroidHotReloadKind.Asset,
            ["data/unknown.ext"] = AndroidHotReloadKind.Asset,
            ["packaging_settings.json"] = AndroidHotReloadKind.Ignore,
            ["scripts/obj/Debug/x.cs"] = AndroidHotReloadKind.Ignore,
            ["scripts/bin/a.dll"] = AndroidHotReloadKind.Ignore,
            [".git/config"] = AndroidHotReloadKind.Ignore,
            ["scenes/Main.scene.tmp"] = AndroidHotReloadKind.Ignore,
            ["scripts/Player.cs~"] = AndroidHotReloadKind.Ignore,
            ["Assembly.csproj"] = AndroidHotReloadKind.Ignore,
            [""] = AndroidHotReloadKind.Ignore,
        };
        foreach (var (path, kind) in expected)
        {
            Check.Equal(kind, AndroidHotReloadTable.Classify(path), $"{path} の差し替えのしかた");
        }
    }

    /// <summary>相対パスの正規化と宛先。</summary>
    private static void NormalizesPathsAndTargets()
    {
        Check.Equal("textures/a.png", AndroidReloadReplies.NormalizeRelative(@"assets://textures\a.png"), "assets:// と \\ を外す");
        Check.Equal("scenes/日本語 シーン.scene", AndroidReloadReplies.NormalizeRelative("./scenes//日本語 シーン.scene"), "./ と重なった / を落とす");
        foreach (var bad in new[] { "../x.png", "a/../../b", @"C:\x\a.png", "/data/a.png", "", "./" })
        {
            Check.True(AndroidReloadReplies.NormalizeRelative(bad) is null, $"{bad} は受け付けない（ランタイムの normalize_relative と同じ）");
        }

        Check.Equal("scene", AndroidReloadReplies.TargetOf(RuntimeIpcCommands.ReloadScene), "RELOAD_SCENE → scene");
        Check.Equal("scene:scenes/Main.scene", AndroidReloadReplies.TargetOf(RuntimeIpcCommands.ReloadSceneIfCurrent("scenes/Main.scene")), "RELOAD_SCENE:{p} → scene:{p}");
        Check.Equal("asset:ui/a.png", AndroidReloadReplies.TargetOf(RuntimeIpcCommands.ReloadAsset("ui/a.png")), "RELOAD_ASSET:{p} → asset:{p}");
        Check.Equal(AndroidReloadReplies.ScriptsTarget, AndroidReloadReplies.TargetOf(RuntimeIpcCommands.ReloadScripts), "RELOAD_SCRIPTS → scripts");
        Check.True(AndroidReloadReplies.TargetOf("PAUSE") is null, "差し替えでない命令は null");
    }

    /// <summary>応答の読み方。</summary>
    private static void ParsesReplies()
    {
        Check.True(AndroidReloadReplies.TryParse("RELOAD_DONE:asset:ui/a.png|12.5|inplace", out var done), "RELOAD_DONE を読める");
        Check.Equal(new AndroidReloadReply("asset:ui/a.png", AndroidReloadOutcome.Done, 12.5, "inplace"), done, "RELOAD_DONE の欄");
        Check.True(AndroidReloadReplies.TryParse("RELOAD_DONE:scene|40.0|scene:assets://scenes/Main.scene", out var scene), "詳細にコロンを含む");
        Check.Equal("scene:assets://scenes/Main.scene", scene.Detail, "詳細はそのまま");
        Check.True(AndroidReloadReplies.TryParse("RELOAD_SKIPPED:scene:scenes/B.scene|今のシーンは assets://scenes/A.scene", out var skipped), "SKIPPED");
        Check.Equal(AndroidReloadOutcome.Skipped, skipped.Outcome, "SKIPPED の結果");
        Check.True(skipped.Detail.StartsWith("今のシーンは", StringComparison.Ordinal), "SKIPPED の理由");
        Check.True(AndroidReloadReplies.TryParse("RELOAD_FAILED:asset:models/a.glb|シーンを読めません", out var failed), "FAILED");
        Check.Equal(AndroidReloadOutcome.Failed, failed.Outcome, "FAILED の結果");

        Check.True(AndroidReloadReplies.TryParse("SCRIPTS_RELOADED:3,5", out var scripts), "SCRIPTS_RELOADED を読める");
        Check.Equal(AndroidReloadOutcome.Done, scripts.Outcome, "型数が 0 以上は成功");
        Check.Equal(AndroidReloadReplies.ScriptsTarget, scripts.Target, "宛先は scripts");
        Check.True(AndroidReloadReplies.TryParse("SCRIPTS_RELOADED:-1,スクリプトホスト（SEEDScripting.dll）が起動時と違います, 起動し直してください", out var scriptsFailed), "失敗");
        Check.Equal(AndroidReloadOutcome.Failed, scriptsFailed.Outcome, "型数が負は失敗");
        Check.True(scriptsFailed.Detail.Contains("起動し直して", StringComparison.Ordinal), "理由はカンマを含んでも最後まで");
        Check.True(!AndroidReloadReplies.TryParse("FPS:59.9", out _), "差し替えの応答でない行は読まない");
    }

    /// <summary>差分の選び方。</summary>
    private static void PlansOverlay()
    {
        var local = new Dictionary<string, string> { ["a.png"] = "AAA", ["b.png"] = "BBB", ["c.png"] = "CCC", ["d.png"] = "DDD" };
        var pak = new Dictionary<string, string> { ["a.png"] = "AAA", ["b.png"] = "OLD", ["c.png"] = "OLD" };
        var pushed = new Dictionary<string, string> { ["c.png"] = "CCC" };
        var plan = AndroidOverlayPlanner.Plan(
            new[] { "a.png", "b.png", "c.png", "d.png", "e.png", "A.PNG" },
            relative => local.TryGetValue(relative, out var digest)
                ? new AndroidLocalAssetContent(digest, digest.Length, Encoding.UTF8.GetBytes(digest), null)
                : throw new FileNotFoundException("無い", relative),
            relative => pushed.TryGetValue(relative, out var p) ? p : pak.TryGetValue(relative, out var k) ? k : null);

        Check.Equal("b.png:Changed,d.png:New", string.Join(",", plan.ToPush.Select(item => $"{item.Relative}:{item.Change}")),
            "違う（b）・端末に無い（d）だけを送る。c は pak と違うが送った記録と同じ");
        Check.Equal("a.png,c.png", string.Join(",", plan.Unchanged), "pak と同じ（a）・送った記録と同じ（c）は送らない");
        Check.Equal("e.png", string.Join(",", plan.Unreadable.Select(skip => skip.Relative)), "手元で読めないものは理由付きで飛ばす");
        Check.Equal(6L, plan.PushBytes, "送る大きさの合計");
    }

    /// <summary>手元の中身は pak と同じ形で、PakWriter の pak と同じ指紋になる（差し替えと pak の書き換えの食い違いが無いことの継ぎ目の確認）。</summary>
    private static void LocalContentMatchesPak()
    {
        using var temp = new TempDir();
        var assetsRoot = temp.Combine("Game/assets");
        var absoluteTexture = Path.Combine(assetsRoot, "textures", "a.png").Replace('\\', '/');
        temp.WriteFile("Game/assets/scenes/Main.scene", "{\"texture\":\"" + absoluteTexture + "\",\"name\":\"日本語\"}");
        var textureBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3 };
        Directory.CreateDirectory(Path.Combine(assetsRoot, "textures"));
        File.WriteAllBytes(Path.Combine(assetsRoot, "textures", "a.png"), textureBytes);

        var pakPath = temp.Combine("out/assets.pak");
        PakWriter.Write(pakPath, assetsRoot, new[]
        {
            new CollectedAsset("scenes/Main.scene", new FileInfo(Path.Combine(assetsRoot, "scenes", "Main.scene")).Length),
            new CollectedAsset("textures/a.png", textureBytes.Length),
        });
        var pak = AndroidPakContentIndex.Open(pakPath);
        Check.Equal(2, pak.Count, "pak のエントリの数");

        var scene = AndroidLocalAssetReader.Read(assetsRoot, "scenes/Main.scene");
        Check.True(scene.Content is not null && Encoding.UTF8.GetString(scene.Content).Contains("assets://textures/a.png", StringComparison.Ordinal),
            "シーンの中の絶対パスは assets:// へ書き換える（端末に C:\\… を送らない）");
        Check.Equal(pak.DigestOf("scenes/Main.scene"), scene.Digest, "書き換えたシーンの指紋は pak のエントリと同じ");
        var texture = AndroidLocalAssetReader.Read(assetsRoot, "textures/a.png");
        Check.True(texture.Content is null && texture.SourcePath is not null, "書き換えない種類はファイルから流す（メモリへ読まない）");
        Check.Equal(pak.DigestOf(@"TEXTURES\A.PNG"), texture.Digest, "画像の指紋も pak と同じ（pak の引き方は区切り・大文字小文字を問わない）");
        Check.Equal(Convert.ToHexString(SHA256.HashData(textureBytes)), texture.Digest, "指紋は中身の SHA-256");
        Check.True(pak.DigestOf("textures/none.png") is null, "pak に無ければ null");

        // 保存し直しても中身が同じなら送らない。変えたら送る
        var unchangedPlan = AndroidOverlayPlanner.Plan(new[] { "scenes/Main.scene", "textures/a.png" },
            relative => AndroidLocalAssetReader.Read(assetsRoot, relative), pak.DigestOf);
        Check.Equal(0, unchangedPlan.ToPush.Count, "pak と同じ中身は送らない");
        File.WriteAllBytes(Path.Combine(assetsRoot, "textures", "a.png"), textureBytes.Reverse().ToArray());
        var changedPlan = AndroidOverlayPlanner.Plan(new[] { "scenes/Main.scene", "textures/a.png" },
            relative => AndroidLocalAssetReader.Read(assetsRoot, relative), pak.DigestOf);
        Check.Equal("textures/a.png", string.Join(",", changedPlan.ToPush.Select(item => item.Relative)), "変えた画像だけを送る");
    }

    /// <summary>APK の pak を比べる土台にしてよいか・フォルダの接頭辞。</summary>
    private static void DecidesPakBaseline()
    {
        var writeTime = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var record = new AndroidAssetOverlayRecord { ApplicationId = AppId, PakSize = 100, PakWriteTimeUtc = writeTime };
        Check.True(AndroidAssetOverlaySync.PakBaselineProblem(record, (100, writeTime)) is null, "run のときと同じ pak なら比べる");
        Check.Equal(AndroidAssetOverlaySync.PakChangedNote, AndroidAssetOverlaySync.PakBaselineProblem(record, (101, writeTime)), "大きさが違えば比べない");
        Check.Equal(AndroidAssetOverlaySync.PakChangedNote, AndroidAssetOverlaySync.PakBaselineProblem(record, (100, writeTime.AddSeconds(1))), "更新時刻が違えば比べない");
        Check.Equal(AndroidAssetOverlaySync.PakChangedNote, AndroidAssetOverlaySync.PakBaselineProblem(record, (null, null)), "置き場に pak が無ければ比べない");
        Check.Equal(AndroidAssetOverlaySync.NoRecordNote, AndroidAssetOverlaySync.PakBaselineProblem(null, (100, writeTime)), "記録が無ければ比べない");
        var noPak = record with { PakSize = null, PakWriteTimeUtc = null };
        Check.Equal(AndroidAssetOverlaySync.PakChangedNote, AndroidAssetOverlaySync.PakBaselineProblem(noPak, (100, writeTime)), "開発用の APK の記録（pak 無し）は比べない");

        var root = Path.Combine(Path.GetTempPath(), "SEED_HotReload_Prefix", "assets");
        Check.Equal(string.Empty, AndroidAssetOverlaySync.FolderPrefix(root, root + Path.DirectorySeparatorChar), "アセットルートそのもの");
        Check.Equal("textures/ui/", AndroidAssetOverlaySync.FolderPrefix(root, Path.Combine(root, "textures", "ui")), "中のフォルダ");
        Check.True(AndroidAssetOverlaySync.FolderPrefix(root, Path.Combine(root, "..", "other")) is null, "外は null");
        Check.True(AndroidAssetOverlaySync.FolderPrefix(root, root + "2") is null, "名前の途中で切れる別のフォルダは外");
    }

    /// <summary>上書き層の記録の往復と run の記録。</summary>
    private static void RoundTripsOverlayState()
    {
        using var temp = new TempDir();
        var path = temp.Combine("cache/android/asset_overlay.json");
        var state = new AndroidAssetOverlayState();
        state.Devices[Serial] = new AndroidAssetOverlayRecord
        {
            ApplicationId = AppId,
            PakSize = 42,
            PakWriteTimeUtc = new DateTime(2026, 9, 25, 1, 2, 3, DateTimeKind.Utc),
            Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["scenes/日本語 シーン.scene"] = "ABC" },
        };
        state.Save(path);
        var loaded = AndroidAssetOverlayState.Load(path);
        var record = loaded.RecordFor(Serial, AppId);
        Check.True(record is not null, "同じ端末・同じアプリの記録を読める");
        Check.Equal("ABC", record!.Files["SCENES/日本語 シーン.scene"], "送った記録は大文字小文字を問わずに引ける");
        Check.Equal(42L, record.PakSize, "pak の大きさ");
        Check.True(loaded.RecordFor(Serial, "com.other.app") is null, "アプリ ID が違えば使わない");
        Check.True(File.ReadAllText(path).Contains("日本語 シーン", StringComparison.Ordinal), "日本語のパスはエスケープしない（人が読める）");
        Check.Equal(0, AndroidAssetOverlayState.Load(temp.Combine("none.json")).Devices.Count, "無いファイルは空の記録");

        // run の記録: --project なら置き場の pak の目印、--assets-dir なら pak 無し。どちらも送ったもの無し
        temp.WriteFile("Game/assets/project_settings.json", "{}");
        var packaged = AndroidProjectResolver.Resolve(temp.Combine("Game"), null)!;
        var development = AndroidProjectResolver.Resolve(null, temp.Combine("Game/assets"))!;
        var engine = FakeEngine(temp);
        var pakPath = Path.Combine(engine.ApkPackageDir, "assets.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(pakPath)!);
        File.WriteAllBytes(pakPath, new byte[] { 1, 2, 3 });
        var runRecord = PushedOverrides.NewOverlayRecord(AppId, packaged, engine);
        Check.Equal(3L, runRecord.PakSize, "--project の run は置き場の pak の大きさを記録する");
        Check.Equal(0, runRecord.Files.Count, "送ったものは無し（files/assets は消した）");
        var devRecord = PushedOverrides.NewOverlayRecord(AppId, development, engine);
        Check.True(devRecord.PakSize is null && devRecord.PakWriteTimeUtc is null, "--assets-dir の run は pak を記録しない");
    }

    /// <summary>files/assets を消す判断（run の起動の前・APK を入れ直した直後）。</summary>
    private static void DecidesWhenToClearAssets()
    {
        using var temp = new TempDir();
        temp.WriteFile("Game/assets/project_settings.json", "{}");
        var packaged = AndroidProjectResolver.Resolve(temp.Combine("Game"), null)!;
        var development = AndroidProjectResolver.Resolve(null, temp.Combine("Game/assets"))!;
        const PushedOverrideMoment launch = PushedOverrideMoment.BeforeLaunch;
        const PushedOverrideMoment install = PushedOverrideMoment.AfterInstall;

        // run の起動の前
        Check.True(PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = AndroidRunGoal.Run }, packaged, launch), "run（--project）は APK が正なので消す");
        Check.True(PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = AndroidRunGoal.Run, PushScripts = true }, packaged, launch),
            "run --push-scripts でもアセットは APK が正なので消す");
        Check.True(!PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = AndroidRunGoal.Push }, packaged, launch), "push は差し替えを残す");
        Check.True(!PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = AndroidRunGoal.Run }, development, launch),
            "開発用の --assets-dir は files/assets が唯一の置き場なので消さない");
        Check.True(!PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = AndroidRunGoal.Run }, null, launch), "プロジェクト無し");
        foreach (var goal in new[] { AndroidRunGoal.Build, AndroidRunGoal.Install })
        {
            Check.True(!PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = goal }, packaged, launch), $"{goal} は起動しないので起動の前には消さない");
        }

        // APK を入れ直した直後（install・run。同じ APK でインストールを飛ばしたときは工程が走らない）
        foreach (var goal in new[] { AndroidRunGoal.Install, AndroidRunGoal.Run })
        {
            Check.True(PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = goal }, packaged, install), $"{goal} で APK を入れ直したら消す");
            Check.True(PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = goal, PushScripts = true }, packaged, install),
                $"{goal} --push-scripts でもアセットは APK が正なので消す");
            Check.True(!PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = goal }, development, install),
                $"{goal} の開発用の --assets-dir は消さない");
        }
        foreach (var goal in new[] { AndroidRunGoal.Build, AndroidRunGoal.Push })
        {
            Check.True(!PushedOverrides.ClearsAssets(new AndroidRunRequest { Goal = goal }, packaged, install), $"{goal} は APK を入れない");
        }

        Check.Equal($"exec-out|run-as|{AppId}|sh|-c|if [ -e files/assets ]; then rm -rf files/assets && echo removed; fi",
            string.Join("|", AdbClient.RunAsRemoveArguments(AppId, AndroidRuntimeContract.RemoteAssetsDir)),
            "消すのは自分のアプリの files/assets だけ（端末の launch.rs の上書き層と同じ場所）");
        Check.True(PushedOverrides.AssetsClearedMessage.Contains(AndroidRuntimeContract.RemoteAssetsDir, StringComparison.Ordinal), "Output の文言に置き場");
        Check.True(!PushedOverrides.AssetsClearedMessage.Contains("起動します", StringComparison.Ordinal),
            "文言はインストールの後でも正しい（起動するとは言わない）");
    }

    /// <summary>tar の作り方。</summary>
    private static void WritesPayloadTar()
    {
        using var temp = new TempDir();
        var file = temp.WriteFile("src/model.bin", "MODEL");
        var payloads = new List<RunAsTarPayload>
        {
            new("scenes/日本語 シーン.scene", Encoding.UTF8.GetBytes("{\"a\":1}"), null),
            new("models/deep/model.bin", null, file),
            new("top.txt", Encoding.UTF8.GetBytes("T"), null),
        };
        using var stream = new MemoryStream();
        var (files, bytes) = RunAsTarArchive.WritePayloadsAsync(payloads, stream, CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(3, files, "ファイルの数");
        Check.Equal(7L + 5L + 1L, bytes, "中身の合計");

        stream.Position = 0;
        var entries = new List<(string Name, TarEntryType Type, string Content)>();
        using (var reader = new TarReader(stream))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                var content = entry.DataStream is null ? string.Empty : new StreamReader(entry.DataStream, Encoding.UTF8).ReadToEnd();
                entries.Add((entry.Name, entry.EntryType, content));
            }
        }
        Check.Equal("scenes/|scenes/日本語 シーン.scene|models/|models/deep/|models/deep/model.bin|top.txt",
            string.Join("|", entries.Select(e => e.Name)), "親フォルダを先に（重ねない）・名前はそのまま（日本語・空白）");
        Check.Equal("MODEL", entries.Single(e => e.Name == "models/deep/model.bin").Content, "ファイルの中身を流す");
        Check.Equal("{\"a\":1}", entries.Single(e => e.Name == "scenes/日本語 シーン.scene").Content, "メモリの中身（書き換えたシーン）");
        Check.True(entries.Where(e => e.Name.EndsWith('/')).All(e => e.Type == TarEntryType.Directory), "フォルダのエントリ");
    }

    /// <summary>変わったファイルの分け方と命令。</summary>
    private static void SplitsAndBuildsCommands()
    {
        var split = AndroidHotReloadApplier.Split(new[]
        {
            "scripts/A.cs", "scripts/a.cs", @"scenes\Main.scene", "textures/a.png", "packaging_settings.json", "../outside.png",
        });
        Check.Equal("scripts/A.cs", string.Join(",", split.Scripts), "スクリプト（大文字小文字だけ違う重複は 1 つ）");
        Check.Equal("scenes/Main.scene,textures/a.png", string.Join(",", split.Assets), "シーン・アセット（区切りを / に）");
        Check.Equal(2, split.Ignored, "関係ないもの・アセットルートの外は数えるだけ");

        var commands = AndroidHotReloadApplier.CommandsFor(new[]
        {
            "scenes/Main.scene", "textures/a.png", "scripts/Referenced.cs", "textures/A.png", "project_settings.json",
        });
        Check.Equal("RELOAD_SCENE:scenes/Main.scene|RELOAD_ASSET:textures/a.png|RELOAD_ASSET:project_settings.json",
            string.Join("|", commands),
            "シーンは今のシーンのときだけ読み直す命令、他は RELOAD_ASSET（取り込めない種類はランタイムが理由を返す）。.cs には出さない・重複は 1 つ");
    }

    /// <summary>命令の送り方。</summary>
    private static void SendsCommandsAndWaitsReplies()
    {
        // 応答は順不同で届く（ランタイムはフレームの境界でまとめて返す）
        var link = new ScriptedLink(sent => sent == RuntimeIpcCommands.ReloadScene
            ? new[] { "RELOAD_DONE:asset:ui/a.png|3.0|inplace", "FPS:60.0", "RELOAD_DONE:scene|30.0|scene:assets://scenes/Main.scene" }
            : Array.Empty<string>());
        var replies = Wait(AndroidReloadCommandSender.SendAsync(link,
            new[] { RuntimeIpcCommands.ReloadAsset("ui/a.png"), RuntimeIpcCommands.ReloadScene }, TimeSpan.FromSeconds(5), CancellationToken.None));
        Check.Equal("asset:ui/a.png:Done,scene:Done", string.Join(",", replies.Select(r => $"{r.Target}:{r.Outcome}")), "命令の順に、宛先で照合した応答");

        // 片方の応答が来ない → 時間切れで NoReply（理由付き）。もう片方は届いたまま
        var silent = new ScriptedLink(sent => sent == RuntimeIpcCommands.ReloadScripts ? new[] { "SCRIPTS_RELOADED:2,3" } : Array.Empty<string>());
        var partial = Wait(AndroidReloadCommandSender.SendAsync(silent,
            new[] { RuntimeIpcCommands.ReloadScripts, RuntimeIpcCommands.ReloadAsset("models/a.glb") }, ShortTimeout, CancellationToken.None));
        Check.Equal(AndroidReloadOutcome.Done, partial[0].Outcome, "届いた応答");
        Check.Equal(AndroidReloadOutcome.NoReply, partial[1].Outcome, "届かなければ NoReply");
        Check.True(partial[1].Detail.Contains("応答しませんでした", StringComparison.Ordinal), $"時間切れの理由: {partial[1].Detail}");

        // 送れない（切れている）→ 待たずに NoReply
        var broken = new ScriptedLink(_ => Array.Empty<string>()) { FailSend = true };
        var started = DateTime.UtcNow;
        var notSent = Wait(AndroidReloadCommandSender.SendAsync(broken, new[] { RuntimeIpcCommands.ReloadScene }, TimeSpan.FromSeconds(30), CancellationToken.None));
        Check.Equal(AndroidReloadOutcome.NoReply, notSent[0].Outcome, "送れなければ NoReply");
        Check.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "送れなければ時間切れまで待たない");
        Check.Equal(0, link.Subscribers + silent.Subscribers + broken.Subscribers, "終わったら受け手を外す");
    }

    /// <summary>SeedAndroid の reload / push --assets の引数。</summary>
    private static void ParsesReloadArguments()
    {
        var scene = SeedAndroidArguments.Parse(new[] { "reload", "scene", "--project", "P", "--serial", Serial }).CommandLine;
        Check.True(scene is { Command: SeedAndroidCommand.Reload, ReloadTarget: SeedAndroidReloadTarget.Scene, ProjectDir: "P" }, "reload scene");
        var asset = SeedAndroidArguments.Parse(new[] { "reload", "--project", "P", "asset", "textures/日本語 画像.png" }).CommandLine;
        Check.True(asset is { ReloadTarget: SeedAndroidReloadTarget.Asset, ReloadPath: "textures/日本語 画像.png" }, "reload asset <パス>（オプションの後でもよい）");
        Check.True(SeedAndroidArguments.Parse(new[] { "reload", "scripts" }).CommandLine is { ReloadTarget: SeedAndroidReloadTarget.Scripts }, "reload scripts");
        Check.True(SeedAndroidArguments.OperatesRunningDevice(SeedAndroidCommand.Reload), "reload は動いている端末を操作するだけ");

        foreach (var (args, what) in new[]
        {
            (new[] { "reload" }, "対象が無い"),
            (new[] { "reload", "everything" }, "知らない対象"),
            (new[] { "reload", "asset" }, "asset にパスが無い"),
            (new[] { "reload", "scene", "extra" }, "余分な引数"),
            (new[] { "reload", "scene", "--serial", "auto" }, "auto は使えない"),
            (new[] { "run", "--assets", "A" }, "--assets は push だけ"),
            (new[] { "push", "--assets", "A", "--assets-dir", "B" }, "--assets と --assets-dir は同時に使えない"),
            (new[] { "push", "--assets", "A", "--serial", "auto" }, "push --assets は auto を使えない"),
            (new[] { "pause", "scene" }, "reload 以外では対象を取らない"),
        })
        {
            Check.True(SeedAndroidArguments.Parse(args).Error is not null, $"{what}: {string.Join(' ', args)}");
        }

        var push = SeedAndroidArguments.Parse(new[] { "push", "--assets", @"D:\Game\assets\textures", "--serial", Serial }).CommandLine;
        Check.True(push is { Command: SeedAndroidCommand.Push, OverlayAssetsDir: @"D:\Game\assets\textures" }, "push --assets <フォルダ>");
    }

    /// <summary>テスト用のエンジン側の置き場（一時フォルダをリポジトリのルートとみなす）。</summary>
    private static AndroidEnginePaths FakeEngine(TempDir temp) => new(temp.Combine("repo"));

    /// <summary>タスクを待つ（テストが止まらないように上限付き）。</summary>
    private static T Wait<T>(Task<T> task)
    {
        if (!task.Wait(WaitTimeoutMs)) throw new AssertionException($"{WaitTimeoutMs} ms 以内に終わりません");
        return task.Result;
    }

    /// <summary>送った命令に台本どおりの行を返す偽の通信路（端末・adb を使わない）。</summary>
    private sealed class ScriptedLink : IAndroidIpcLink
    {
        /// <summary>命令ごとに返す行。</summary>
        private readonly Func<string, string[]> _script;

        /// <summary>切れると完了する（このテストでは切れない）。</summary>
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>台本を指定して作る。</summary>
        /// <param name="script">命令 → 返す行。</param>
        public ScriptedLink(Func<string, string[]> script) => _script = script;

        /// <summary>送るのを失敗させるか。</summary>
        public bool FailSend { get; init; }

        /// <summary>受け手の数（外し忘れの確認用）。</summary>
        public int Subscribers => _received?.GetInvocationList().Length ?? 0;

        /// <summary>受け手。</summary>
        private Action<string>? _received;

        /// <inheritdoc />
        public event Action<string>? MessageReceived
        {
            add => _received += value;
            remove => _received -= value;
        }

        /// <inheritdoc />
        public Task Closed => _closed.Task;

        /// <inheritdoc />
        public bool Send(string command)
        {
            if (FailSend) return false;
            foreach (var line in _script(command)) _received?.Invoke(line);
            return true;
        }

        /// <inheritdoc />
        public Task CloseAsync(bool detach)
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
