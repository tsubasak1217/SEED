using System.Buffers.Binary;
using System.IO.Compression;
using System.Linq;
using SEEDEditor.Android;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Release;
using SEEDEditor.Android.Signing;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;
using SEEDEditor.ProjectSettings;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// Google Play の要件チェック（段階D。docs/android.md §24）: 表（play_requirements.json）とエンジンの値の一致、
/// ビルドの前の判定（targetSdk・versionCode の単調増加・ABI・アプリ ID・署名・形式・アイコン）、配布物の判定
/// （中身・debuggable・権限・16 KB・署名）、道具の出力の読み方、ELF の整列、配布用ビルドの記録。
/// </summary>
public static class ReleaseRequirementTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("要件の表: リポジトリの play_requirements.json を読め、targetSdk の下限と build.gradle.kts・中核の値が合う", RepositoryTableMatchesContract);
        harness.Add("要件: targetSdk（下限以上で合格・未満は不合格で日付と延長を示す）", TargetSdk);
        harness.Add("要件: versionCode（記録なし＝知らせ・大きい＝合格・同じ＝注意・小さい＝不合格）", VersionCode);
        harness.Add("要件: ABI（arm64 だけ＝合格・x86_64 だけ＝注意・両方は AAB で注意）・アプリ ID（com.example.＝不合格・既定の ID＝注意）", AbiAndApplicationId);
        harness.Add("要件: 署名（鍵が無い＝不合格）・形式（APK は知らせ）・アイコン（未設定は注意）・表が無いときの 1 件", SigningFormatIconAndTable);
        harness.Add("配布物: 中身の ID・版・debuggable・INTERNET・targetSdk の判定", ArtifactManifestChecks);
        harness.Add("配布物: 16 KB（.so の LOAD・zip の整列）と署名（デバッグ用の鍵・指定と違う鍵・確かめられない）と道具の問題", ArtifactAlignmentAndCertificate);
        harness.Add("一覧: 同じ項目はビルドの後の判定で上書き・要約・不合格の有無", ReportPutAndSummary);
        harness.Add("aapt2 dump badging の読み方（ID・版・SDK・権限・debuggable・native-code）", BadgingParser);
        harness.Add("署名の出力の読み方（apksigner の DN・SHA-256・方式／keytool -printcert・署名なし）", SignerParsers);
        harness.Add("ELF: 64 bit / 32 bit の LOAD の整列の最小値・LOAD 以外は見ない・壊れた ELF は理由付き", ElfAlignment);
        harness.Add("配布物の .so を zip の中で読む（APK は lib/・AAB は base/lib/・圧縮の有無）", NativeLibrariesInArchive);
        harness.Add("配布用ビルドの記録: アプリ ID・形式ごとの最後の versionCode の往復（壊れた記録は空）", ReleaseHistoryRoundTrip);
    }

    /// <summary>テスト用の要件の表。</summary>
    private static PlayRequirements Table() => new()
    {
        TargetSdk = new PlayTargetSdkRequirement { MinApiLevel = 36, RequiredSince = "2026-08-31", ExtensionUntil = "2026-11-01" },
        RequiredAbis = new[] { "arm64-v8a" },
        Only64BitAbis = true,
        PageSizeBytes = 16384,
        ReservedApplicationIdPrefixes = new[] { "com.example." },
        EngineDefaultApplicationIds = new[] { "com.seedengine.runtime" },
        ReleaseUnexpectedPermissions = new[] { "android.permission.INTERNET" },
        DebugCertificateSubjectMarker = "CN=Android Debug",
    };

    /// <summary>識別情報。</summary>
    private static AndroidAppIdentity Identity(int? versionCode = 5, string? applicationId = "com.studio.game") =>
        AndroidAppIdentityResolver.Resolve(
            new AndroidAppSettings { ApplicationId = applicationId, VersionCode = versionCode, VersionName = "1.0.5" }, "Game", "Game", hasProjectContext: true);

    /// <summary>ビルドの前の材料。</summary>
    private static AndroidPreBuildFacts Facts(AndroidReleaseRecord? previous = null, int? versionCode = 5) => new()
    {
        Format = AndroidPackageFormat.Aab,
        TargetApiLevel = 36,
        Abis = new[] { AndroidAbis.Arm64 },
        Identity = Identity(versionCode),
        PreviousRelease = previous,
        Signing = new AndroidSigningConfig(@"C:\k\upload.jks", "upload", new AndroidSigningSecrets("x", null, "t"), "指定", System.Array.Empty<string>()),
        LauncherIcon = true,
    };

    /// <summary>リポジトリの表と契約。</summary>
    private static void RepositoryTableMatchesContract()
    {
        var engine = AndroidEnginePaths.Locate(System.AppContext.BaseDirectory, System.Environment.CurrentDirectory)!;
        Check.True(engine is not null, "リポジトリを見つける");
        var table = PlayRequirements.Load(engine.PlayRequirementsPath, out var error);
        Check.True(table is not null && error is null, $"表を読める: {error}");
        Check.True(table!.TargetSdk.MinApiLevel <= AndroidRuntimeContract.TargetApiLevel,
            $"エンジンの targetSdk（{AndroidRuntimeContract.TargetApiLevel}）が Google Play の下限（{table.TargetSdk.MinApiLevel}）以上");
        var gradle = File.ReadAllText(Path.Combine(engine.AndroidDir, "app", "build.gradle.kts"));
        Check.True(gradle.Contains($"val seedTargetSdk = {AndroidRuntimeContract.TargetApiLevel}"), "build.gradle.kts の seedTargetSdk と中核の値が同じ");
        Check.True(gradle.Contains($"val seedMinSdk = {AndroidRuntimeContract.MinApiLevel}"), "minSdk も同じ");
        var manifest = File.ReadAllText(Path.Combine(engine.AndroidDir, "app", "src", "main", "AndroidManifest.xml"));
        Check.True(manifest.Contains("android:enableOnBackInvokedCallback=\"false\""), "targetSdk 36 でも戻るキーを受け取る（予測型の戻るを使わない）");
        Check.True(manifest.Contains("android:icon=\"${seedAppIcon}\""), "アイコンはビルドで決める");
        Check.True(table.PageSizeBytes == 16384 && table.RequiredAbis.Contains("arm64-v8a"), "16 KB と arm64");
        Check.True(PlayRequirements.Load(Path.Combine(engine.WorkDir, "nothing.json"), out var missing) is null && missing!.Contains("ありません"), "無い表は null と理由");
    }

    /// <summary>targetSdk。</summary>
    private static void TargetSdk()
    {
        var pass = AndroidRequirementChecks.TargetSdk(Table(), 36);
        Check.Equal(AndroidRequirementSeverity.Pass, pass.Severity, "36 は合格");
        var fail = AndroidRequirementChecks.TargetSdk(Table(), 35);
        Check.Equal(AndroidRequirementSeverity.Failure, fail.Severity, "35 は不合格");
        Check.True(fail.Detail.Contains("2026-08-31") && fail.Detail.Contains("2026-11-01") && fail.Detail.Contains("seedTargetSdk"), $"日付・延長・直し方: {fail.Detail}");
    }

    /// <summary>versionCode。</summary>
    private static void VersionCode()
    {
        Check.Equal(AndroidRequirementSeverity.Info, AndroidRequirementChecks.VersionCode(Facts()).Severity, "記録なしは知らせ");
        var previous = new AndroidReleaseRecord { VersionCode = 4, VersionName = "1.0.4", BuiltAt = System.DateTimeOffset.Now };
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidRequirementChecks.VersionCode(Facts(previous)).Severity, "大きいので合格");
        var same = AndroidRequirementChecks.VersionCode(Facts(previous, versionCode: 4));
        Check.True(same.Severity == AndroidRequirementSeverity.Warning && same.Detail.Contains("同じ"), $"同じは注意: {same.Detail}");
        var lower = AndroidRequirementChecks.VersionCode(Facts(previous, versionCode: 3));
        Check.True(lower.Severity == AndroidRequirementSeverity.Failure && lower.Detail.Contains("バージョン番号"), $"小さいは不合格と直し方: {lower.Detail}");
    }

    /// <summary>ABI とアプリ ID。</summary>
    private static void AbiAndApplicationId()
    {
        var table = Table();
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidRequirementChecks.Abis(table, new[] { "arm64-v8a" }, AndroidPackageFormat.Aab).Severity, "arm64 だけ");
        Check.Equal(AndroidRequirementSeverity.Warning, AndroidRequirementChecks.Abis(table, new[] { "x86_64" }, AndroidPackageFormat.Apk).Severity, "x86_64 だけは実機で動かない");
        Check.Equal(AndroidRequirementSeverity.Warning, AndroidRequirementChecks.Abis(table, new[] { "arm64-v8a", "x86_64" }, AndroidPackageFormat.Aab).Severity, "AAB に両方は BCL が両方配られる");
        Check.Equal(AndroidRequirementSeverity.Info, AndroidRequirementChecks.Abis(table, new[] { "arm64-v8a", "x86_64" }, AndroidPackageFormat.Apk).Severity, "APK に両方は知らせ");
        Check.Equal(AndroidRequirementSeverity.Failure, AndroidRequirementChecks.Abis(table, new[] { "armeabi-v7a", "arm64-v8a" }, AndroidPackageFormat.Aab).Severity, "32 bit は SEED が作らないもの");

        Check.Equal(AndroidRequirementSeverity.Failure, AndroidRequirementChecks.ApplicationId(table, "com.example.game").Severity, "com.example. は受け付けられない");
        Check.Equal(AndroidRequirementSeverity.Warning, AndroidRequirementChecks.ApplicationId(table, "com.seedengine.runtime").Severity, "エンジンの既定の ID");
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidRequirementChecks.ApplicationId(table, "com.studio.game").Severity, "自分の ID");
    }

    /// <summary>署名・形式・アイコン・表。</summary>
    private static void SigningFormatIconAndTable()
    {
        var items = AndroidRequirementChecks.Evaluate(Table(), Facts());
        Check.Equal("target_sdk,version_code,abi_64bit,application_id,signing,format,icon", string.Join(",", items.Select(i => i.Id)), "項目の並び");
        Check.True(items.All(i => i.Severity is AndroidRequirementSeverity.Pass or AndroidRequirementSeverity.Info), "揃った設定は不合格・注意なし");
        var noSigning = AndroidRequirementChecks.Signing(Facts() with { Signing = null, SigningProblem = "鍵がありません" });
        Check.True(noSigning.Severity == AndroidRequirementSeverity.Failure && noSigning.Detail == "鍵がありません", "鍵が決まらなければ理由付きの不合格");
        Check.Equal(AndroidRequirementSeverity.Info, AndroidRequirementChecks.Format(AndroidPackageFormat.Apk).Severity, "APK は知らせ");
        Check.Equal(AndroidRequirementSeverity.Warning, AndroidRequirementChecks.Icon(false).Severity, "アイコン未設定は注意");
        var table = AndroidRequirementChecks.TableMissing("表がありません。");
        Check.True(table.Severity == AndroidRequirementSeverity.Failure && table.Id == AndroidRequirementIds.Table, "表が無いときは判定できない不合格");
    }

    /// <summary>テスト用のマニフェストの要点。</summary>
    private static AaptBadging Manifest(bool debuggable = false, params string[] permissions) =>
        new("com.studio.game", 5, "1.0.5", 29, 36, debuggable, permissions, new[] { "arm64-v8a" });

    /// <summary>配布物のマニフェスト。</summary>
    private static void ArtifactManifestChecks()
    {
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidArtifactChecks.Identity(Manifest(), Identity()).Severity, "指定どおり");
        var wrong = AndroidArtifactChecks.Identity(Manifest() with { VersionCode = 4 }, Identity());
        Check.True(wrong.Severity == AndroidRequirementSeverity.Failure && wrong.Detail.Contains("versionCode"), "版が違えば不合格");
        Check.Equal(AndroidRequirementSeverity.Failure, AndroidArtifactChecks.Debuggable(Manifest(debuggable: true)).Severity, "debuggable は不合格");
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidArtifactChecks.Debuggable(Manifest()).Severity, "debuggable でない");
        var internet = AndroidArtifactChecks.Permissions(Table(), Manifest(false, "android.permission.INTERNET", "android.permission.VIBRATE"));
        Check.True(internet.Severity == AndroidRequirementSeverity.Warning && internet.Detail.Contains("INTERNET"), "INTERNET は注意");
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidArtifactChecks.Permissions(Table(), Manifest()).Severity, "権限なし");

        var facts = new AndroidArtifactFacts
        {
            ArtifactPath = "x.aab", Format = AndroidPackageFormat.Aab, Manifest = Manifest() with { TargetSdk = 35 },
            NativeLibraries = new[] { Library(0x4000) },
            Signer = new SignerCertificate(true, "CN=Up", "ab", new[] { "JAR" }),
        };
        var items = AndroidArtifactChecks.Evaluate(Table(), facts, new AndroidArtifactExpectation(Identity(), "ab"));
        Check.Equal(AndroidRequirementSeverity.Failure, items.Single(i => i.Id == AndroidRequirementIds.TargetSdk).Severity, "実物の targetSdk で判定");
    }

    /// <summary>テスト用の .so。</summary>
    private static AndroidNativeLibraryFact Library(ulong alignment, bool compressed = true) =>
        new("lib/arm64-v8a/libSEED.so", "arm64-v8a", compressed, new ElfLoadAlignment(true, 183, 2, alignment), null);

    /// <summary>16 KB と署名。</summary>
    private static void ArtifactAlignmentAndCertificate()
    {
        var table = Table();
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidArtifactChecks.PageSizeElf(table, new[] { Library(0x4000), Library(0x10000) }).Severity, "16 KB 以上");
        var small = AndroidArtifactChecks.PageSizeElf(table, new[] { Library(0x1000) });
        Check.True(small.Severity == AndroidRequirementSeverity.Failure && small.Detail.Contains("0x1000"), $"4 KB は不合格: {small.Detail}");
        Check.Equal(AndroidRequirementSeverity.Failure, AndroidArtifactChecks.PageSizeElf(table, System.Array.Empty<AndroidNativeLibraryFact>()).Severity, ".so が無い");
        var unreadable = new AndroidNativeLibraryFact("lib/arm64-v8a/libX.so", "arm64-v8a", true, null, "ELF でない");
        Check.Equal(AndroidRequirementSeverity.Failure, AndroidArtifactChecks.PageSizeElf(table, new[] { unreadable }).Severity, "読めない .so は合格にしない");

        AndroidArtifactFacts Apk(bool? aligned) => new()
        {
            ArtifactPath = "x.apk", Format = AndroidPackageFormat.Apk, NativeLibraries = new[] { Library(0x4000) }, ZipAligned = aligned, ZipAlignDetail = "lib/x.so (BAD - 123)",
        };
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidArtifactChecks.PageSizeZip(Apk(true)).Severity, "zipalign の合格");
        Check.True(AndroidArtifactChecks.PageSizeZip(Apk(false)).Detail.Contains("BAD"), "整列していない項目を示す");
        Check.Equal(AndroidRequirementSeverity.Failure, AndroidArtifactChecks.PageSizeZip(Apk(null)).Severity, "確かめられなければ不合格");
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidArtifactChecks.PageSizeZip(Apk(null) with { Format = AndroidPackageFormat.Aab }).Severity, "AAB は Google Play が整列する");

        var debugCert = AndroidArtifactChecks.Certificate(table, new SignerCertificate(true, "C=US, O=Android, CN=Android Debug", "aa", new[] { "v2" }), null, AndroidPackageFormat.Apk);
        Check.True(debugCert.Severity == AndroidRequirementSeverity.Failure && debugCert.Detail.Contains("デバッグ用の鍵"), "デバッグ用の鍵は不合格");
        var mismatch = AndroidArtifactChecks.Certificate(table, new SignerCertificate(true, "CN=Other", "bb", new[] { "v2" }), "aa", AndroidPackageFormat.Apk);
        Check.True(mismatch.Severity == AndroidRequirementSeverity.Failure && mismatch.Detail.Contains("違う証明書"), "指定と違う鍵は不合格");
        Check.Equal(AndroidRequirementSeverity.Pass, AndroidArtifactChecks.Certificate(table, new SignerCertificate(true, "CN=Up", "aa", new[] { "v2", "v3" }), "aa", AndroidPackageFormat.Apk).Severity, "指定の鍵");
        Check.Equal(AndroidRequirementSeverity.Failure, AndroidArtifactChecks.Certificate(table, null, "aa", AndroidPackageFormat.Aab).Severity, "署名が無ければ不合格");

        var withProblems = AndroidArtifactChecks.Evaluate(table, Apk(true) with { ToolProblems = new[] { "aapt2 を起動できません" } },
            new AndroidArtifactExpectation(Identity(), null));
        Check.True(withProblems.Any(i => i.Id == AndroidRequirementIds.Tools && i.Severity == AndroidRequirementSeverity.Failure), "道具の問題は不合格として出す");
    }

    /// <summary>一覧。</summary>
    private static void ReportPutAndSummary()
    {
        var report = new AndroidRequirementReport();
        foreach (var item in AndroidRequirementChecks.Evaluate(Table(), Facts())) report.Put(item);
        var count = report.Items.Count;
        report.Put(AndroidRequirementChecks.TargetSdk(Table(), 35));
        Check.Equal(count, report.Items.Count, "同じ項目は上書き（増えない）");
        Check.True(report.HasFailures && report.Count(AndroidRequirementSeverity.Failure) == 1, "上書きした不合格");
        Check.True(report.Summary().Contains("不合格 1"), $"要約: {report.Summary()}");
        Check.True(report.Items[0].Describe().StartsWith("[不合格] targetSdk: "), $"一行: {report.Items[0].Describe()}");
    }

    /// <summary>aapt2 dump badging。</summary>
    private static void BadgingParser()
    {
        var badging = AaptBadgingParser.Parse(new[]
        {
            // 行は build-tools 36.0.0 の aapt2 が実物の APK に出したものと同じ形（minSdk は minSdkVersion:）
            "package: name='com.studio.game' versionCode='12' versionName='1.2 (b)' platformBuildVersionName='16' compileSdkVersion='36'",
            "minSdkVersion:'29'",
            "targetSdkVersion:'36'",
            "uses-permission: name='android.permission.INTERNET'",
            "application-label:'ゲーム'",
            "application-debuggable",
            "launchable-activity: name='com.seedengine.runtime.MainActivity'  label='' icon=''",
            "native-code: 'arm64-v8a' 'x86_64'",
        });
        Check.Equal("com.studio.game", badging.PackageName, "ID");
        Check.Equal(12, badging.VersionCode, "versionCode");
        Check.Equal("1.2 (b)", badging.VersionName, "versionName（空白・括弧入り）");
        Check.True(badging.MinSdk == 29 && badging.TargetSdk == 36, "SDK（sdkVersion と targetSdkVersion を取り違えない）");
        Check.True(badging.Debuggable, "debuggable");
        Check.Equal("android.permission.INTERNET", badging.Permissions.Single(), "権限");
        Check.Equal("arm64-v8a,x86_64", string.Join(",", badging.NativeCode), "native-code");
        Check.True(!AaptBadgingParser.Parse(new[] { "package: name='a.b'" }).Debuggable, "行が無ければ debuggable でない");
        Check.Equal(24, AaptBadgingParser.Parse(new[] { "sdkVersion:'24'" }).MinSdk, "古い aapt の sdkVersion: も読む");
    }

    /// <summary>署名の出力。</summary>
    private static void SignerParsers()
    {
        var apk = SignerCertificateParser.ParseApksigner(new[]
        {
            "Verifies",
            "Verified using v1 scheme (JAR signing): false",
            "Verified using v2 scheme (APK Signature Scheme v2): true",
            "Verified using v3 scheme (APK Signature Scheme v3): true",
            "Number of signers: 1",
            "Signer #1 certificate DN: CN=SEED Upload Key",
            "Signer #1 certificate SHA-256 digest: 0a1b2c",
        }, 0);
        Check.True(apk.Verified && apk.Subject == "CN=SEED Upload Key" && apk.Sha256 == "0a1b2c", "apksigner の DN と SHA-256");
        Check.Equal("v2,v3", string.Join(",", apk.Schemes), "確かめられた方式");
        Check.True(!SignerCertificateParser.ParseApksigner(new[] { "DOES NOT VERIFY" }, 1).Verified, "確かめられない");

        var aab = SignerCertificateParser.ParseKeytoolPrintCert(new[] { "Signer #1:", "Owner: CN=SEED Upload Key", "SHA1: 11:22", "SHA256: 0A:1B:2C" }, 0);
        Check.True(aab.Verified && aab.Sha256 == "0a1b2c", "keytool の SHA-256 は小文字・コロン無しにそろえる");
        Check.True(!SignerCertificateParser.ParseKeytoolPrintCert(new[] { "Not a signed jar file" }, 0).Verified, "署名なし");
        Check.Equal("0a1b2c", SignerCertificateParser.NormalizeFingerprint(" 0A:1B:2C "), "指紋のそろえ方");
    }

    /// <summary>テスト用の ELF（64 bit・リトルエンディアン）。</summary>
    private static byte[] Elf64(params (uint Type, ulong Align)[] headers)
    {
        const int header = 64, entry = 56;
        var bytes = new byte[header + entry * headers.Length];
        new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 183);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), header);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), entry);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), (ushort)headers.Length);
        for (var i = 0; i < headers.Length; i++)
        {
            var at = header + entry * i;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), headers[i].Type);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 48), headers[i].Align);
        }
        return bytes;
    }

    /// <summary>ELF。</summary>
    private static void ElfAlignment()
    {
        var aligned = ElfAlignmentReader.Read(new MemoryStream(Elf64((1, 0x4000), (2, 8), (1, 0x10000))));
        Check.True(aligned.Is64Bit && aligned.Machine == 183 && aligned.LoadSegments == 2 && aligned.MinLoadAlignment == 0x4000, $"LOAD だけの最小: {aligned}");
        Check.Equal(0x1000UL, ElfAlignmentReader.Read(new MemoryStream(Elf64((1, 0x1000)))).MinLoadAlignment, "4 KB");

        // 32 bit: 見出し 52 バイト・プログラムヘッダ 32 バイト（p_align は +28）
        var elf32 = new byte[52 + 32];
        new byte[] { 0x7F, 0x45, 0x4C, 0x46, 1, 1, 1 }.CopyTo(elf32, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(elf32.AsSpan(18), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(elf32.AsSpan(28), 52);
        BinaryPrimitives.WriteUInt16LittleEndian(elf32.AsSpan(42), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(elf32.AsSpan(44), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(elf32.AsSpan(52), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(elf32.AsSpan(52 + 28), 0x1000);
        var small = ElfAlignmentReader.Read(new MemoryStream(elf32));
        Check.True(!small.Is64Bit && small.MinLoadAlignment == 0x1000, $"32 bit: {small}");

        foreach (var broken in new[] { new byte[] { 1, 2, 3 }, Elf64((1, 0x4000))[..70] })
        {
            try
            {
                ElfAlignmentReader.Read(new MemoryStream(broken));
                throw new AssertionException("壊れた ELF で止まらない");
            }
            catch (InvalidDataException)
            {
                // 期待どおり
            }
        }
    }

    /// <summary>zip の中の .so。</summary>
    private static void NativeLibrariesInArchive()
    {
        using var temp = new TempDir();
        var aab = temp.Combine("app.aab");
        using (var zip = ZipFile.Open(aab, ZipArchiveMode.Create))
        {
            Add(zip, "base/lib/arm64-v8a/libSEED.so", Elf64((1, 0x4000)), CompressionLevel.Optimal);
            Add(zip, "base/lib/arm64-v8a/libcoreclr.so", Elf64((1, 0x1000)), CompressionLevel.NoCompression);
            Add(zip, "base/assets/seed/readme.txt", new byte[] { 1, 2 }, CompressionLevel.Optimal);
        }
        var problems = new List<string>();
        var libraries = AndroidArtifactInspector.ReadNativeLibraries(aab, AndroidPackageFormat.Aab, problems);
        Check.Equal(0, problems.Count, "問題なし");
        Check.Equal(2, libraries.Count, "base/lib/ の .so だけ");
        Check.True(libraries.All(l => l.Abi == "arm64-v8a"), "ABI は lib/ の下のフォルダ名");
        Check.True(libraries.Single(l => l.EntryName.EndsWith("libcoreclr.so")).Alignment!.MinLoadAlignment == 0x1000, "整列を読む");
        Check.True(!libraries.Single(l => l.EntryName.EndsWith("libcoreclr.so")).Compressed, "非圧縮を見分ける");
        Check.Equal(0, AndroidArtifactInspector.ReadNativeLibraries(aab, AndroidPackageFormat.Apk, problems).Count, "APK として読むと lib/ は無い");
        File.WriteAllText(temp.Combine("broken.apk"), "zip ではない");
        AndroidArtifactInspector.ReadNativeLibraries(temp.Combine("broken.apk"), AndroidPackageFormat.Apk, problems);
        Check.True(problems.Single().Contains("zip"), "zip でなければ理由");
    }

    /// <summary>zip へ 1 項目足す。</summary>
    private static void Add(ZipArchive zip, string name, byte[] content, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(content);
    }

    /// <summary>記録。</summary>
    private static void ReleaseHistoryRoundTrip()
    {
        using var temp = new TempDir();
        var path = temp.Combine("cache/android/release_history.json");
        var history = AndroidReleaseHistory.Load(path);
        Check.True(history.Last("com.a.b", AndroidPackageFormat.Aab) is null, "最初は無い");
        history.Record("com.a.b", AndroidPackageFormat.Aab, new AndroidReleaseRecord { VersionCode = 7, VersionName = "1.7", BuiltAt = System.DateTimeOffset.Now });
        history.Record("com.a.b", AndroidPackageFormat.Apk, new AndroidReleaseRecord { VersionCode = 3 });
        history.Save(path);
        var loaded = AndroidReleaseHistory.Load(path);
        Check.Equal(7, loaded.Last("com.a.b", AndroidPackageFormat.Aab)?.VersionCode, "AAB の記録");
        Check.Equal(3, loaded.Last("com.a.b", AndroidPackageFormat.Apk)?.VersionCode, "APK は別に記録");
        Check.True(loaded.Last("com.other", AndroidPackageFormat.Aab) is null, "アプリ ID ごと");
        File.WriteAllText(path, "{ 壊れた");
        Check.True(AndroidReleaseHistory.Load(path).Last("com.a.b", AndroidPackageFormat.Aab) is null, "壊れた記録は空");
    }
}
