using System.Linq;
using SEEDEditor.Android;
using SEEDEditor.Android.Gradle;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Signing;
using SEEDEditor.Android.Steps;
using SEEDEditor.Packaging;
using SEEDEditor.Tools.SeedAndroid;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// 配布用（release）のビルドの署名と Gradle への受け渡し（段階D。docs/android.md §24）:
/// 鍵の決め方・環境変数でのパスワードの受け渡し・keytool の出力の読み方・計画・指定の検査・SeedAndroid の引数。
/// </summary>
public static class ReleaseSigningTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("署名の鍵: 指定 → packaging_settings.json（相対パスはプロジェクトのルートから）・パスワードは指定 → 環境変数", ResolvesSigning);
        harness.Add("署名の鍵: 無い・見つからない・アセットの中は理由付きの指定の誤り（デバッグ署名にしない）・プロジェクトの中は警告", RejectsBadSigning);
        harness.Add("署名のパスワード: 環境変数（キーは省略時キーストアと同じ）・ToString は伏せ字", SecretsFromEnvironment);
        harness.Add("Gradle: 配布用のタスク（assembleRelease / bundleRelease）と署名の受け渡し（パスワードは環境変数だけ・一覧は伏せ字）", GradleReleaseInvocation);
        harness.Add("Gradle: アイコンを作ったときだけ seed.launcherIcon=generated・開発用は従来の引数のまま", GradleLauncherIconProperty);
        harness.Add("Gradle の指紋: パスワードを材料にしない・ビルドの種類と形式で出力と記録のキーが別", GradleFingerprintExcludesSecrets);
        harness.Add("keytool: -list -v の読み方・証明書の名前の逃がし方・作る前の検査（既にある・アセットの中・短いパスワード）", KeytoolParsing);
        harness.Add("計画: 配布用は Gradle の後に要件の確認・Gradle の記録は種類と形式ごと", ReleasePlan);
        harness.Add("指定の検査: AAB は配布用の build だけ・配布用は push / --push-scripts / --assets-dir 不可・開発用に鍵は不可", ValidatesVariant);
        harness.Add("push の上書きの解除: 配布用（run-as が使えない）では消さない・記録も触らない", ReleaseSkipsPushedOverrides);
        harness.Add("SeedAndroid: --variant / --format / --keystore / --key-alias（aab・鍵で配布用とみなす）・keystore create・check の引数と誤り", SeedAndroidReleaseArguments);
    }

    /// <summary>テスト用の環境変数（無ければ null）。</summary>
    private static System.Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    /// <summary>鍵の決め方。</summary>
    private static void ResolvesSigning()
    {
        using var temp = new TempDir();
        var keystore = temp.WriteFile("keys/upload.jks", "x");
        var projectRoot = temp.Combine("proj");
        System.IO.Directory.CreateDirectory(projectRoot);
        var settings = new AndroidSigningSettings { KeystorePath = "../keys/upload.jks", KeyAlias = " upload " };

        var fromSettings = AndroidSigningResolver.Resolve(
            new AndroidSigningInputs(null, null, null, settings, projectRoot, temp.Combine("proj/assets")),
            Env(("SEED_ANDROID_KEYSTORE_PASSWORD", "env-pass")));
        Check.Equal(keystore, fromSettings.KeystorePath, "設定ファイルの相対パスはプロジェクトのルートから");
        Check.Equal("upload", fromSettings.KeyAlias, "別名は前後の空白を落とす");
        Check.Equal(AndroidSigningResolver.ProjectSettingsOrigin, fromSettings.KeystoreOrigin, "出どころ");
        Check.Equal("env-pass", fromSettings.Secrets.KeystorePassword, "パスワードは環境変数");
        Check.Equal(0, fromSettings.Warnings.Count, "プロジェクトの外なので警告なし");

        var other = temp.WriteFile("other/second.jks", "y");
        var requestSecrets = new AndroidSigningSecrets("request-pass", null, "指定");
        var fromRequest = AndroidSigningResolver.Resolve(
            new AndroidSigningInputs(other, "second", requestSecrets, settings, projectRoot, null),
            Env(("SEED_ANDROID_KEYSTORE_PASSWORD", "env-pass")));
        Check.Equal(other, fromRequest.KeystorePath, "指定が設定ファイルより強い");
        Check.Equal("second", fromRequest.KeyAlias, "別名も指定が強い");
        Check.Equal("request-pass", fromRequest.Secrets.KeystorePassword, "パスワードも指定が環境変数より強い");
        Check.True(!fromRequest.Describe().Contains("request-pass"), $"説明にパスワードを出さない: {fromRequest.Describe()}");
        Check.True(!fromRequest.ToString().Contains("request-pass"), "record の ToString にも出さない");
    }

    /// <summary>使えない鍵。</summary>
    private static void RejectsBadSigning()
    {
        using var temp = new TempDir();
        var env = Env(("SEED_ANDROID_KEYSTORE_PASSWORD", "env-pass"));
        var none = Expect(() => AndroidSigningResolver.Resolve(new AndroidSigningInputs(null, null, null, null, null, null), env));
        Check.True(none.Kind == AndroidFailureKind.InvalidRequest && none.Message.Contains("--keystore") && none.Message.Contains("デバッグ署名"),
            $"鍵が無い: {none.Message}");

        var missing = Expect(() => AndroidSigningResolver.Resolve(
            new AndroidSigningInputs(temp.Combine("nothing.jks"), "a", null, null, null, null), env));
        Check.True(missing.Message.Contains("キーストアがありません"), $"見つからない: {missing.Message}");

        var inAssets = temp.WriteFile("proj/assets/keys/upload.jks", "x");
        var assets = Expect(() => AndroidSigningResolver.Resolve(
            new AndroidSigningInputs(inAssets, "a", null, null, temp.Combine("proj"), temp.Combine("proj/assets")), env));
        Check.True(assets.Message.Contains("アセットフォルダの中"), $"アセットの中は使わない: {assets.Message}");

        var inProject = temp.WriteFile("proj/keys/upload.jks", "x");
        var warned = AndroidSigningResolver.Resolve(
            new AndroidSigningInputs(inProject, "a", null, null, temp.Combine("proj"), temp.Combine("proj/assets")), env);
        Check.True(warned.Warnings.Count == 1 && warned.Warnings[0].Contains("プロジェクトのフォルダの中"), "プロジェクトの中は警告");

        var noAlias = Expect(() => AndroidSigningResolver.Resolve(new AndroidSigningInputs(inProject, " ", null, null, null, null), env));
        Check.True(noAlias.Message.Contains("別名"), $"別名が無い: {noAlias.Message}");

        var noPassword = Expect(() => AndroidSigningResolver.Resolve(new AndroidSigningInputs(inProject, "a", null, null, null, null), Env()));
        Check.True(noPassword.Message.Contains("SEED_ANDROID_KEYSTORE_PASSWORD"), $"パスワードが無い: {noPassword.Message}");

        Check.True(AndroidSigningResolver.IsUnder(temp.Combine("proj/assets/a.jks"), temp.Combine("proj/assets")), "中");
        Check.True(!AndroidSigningResolver.IsUnder(temp.Combine("proj/assets2/a.jks"), temp.Combine("proj/assets")), "名前が前方一致するだけの別フォルダは外");
    }

    /// <summary>環境変数のパスワード。</summary>
    private static void SecretsFromEnvironment()
    {
        Check.True(AndroidSigningSecrets.FromEnvironment(Env()) is null, "無ければ null");
        var same = AndroidSigningSecrets.FromEnvironment(Env(("SEED_ANDROID_KEYSTORE_PASSWORD", " spaced ")))!;
        Check.Equal(" spaced ", same.KeystorePassword, "前後の空白もパスワードの一部（落とさない）");
        Check.Equal(" spaced ", same.KeyPassword, "キーは省略時キーストアと同じ（PKCS12）");
        var both = AndroidSigningSecrets.FromEnvironment(Env(("SEED_ANDROID_KEYSTORE_PASSWORD", "s1"), ("SEED_ANDROID_KEY_PASSWORD", "k1")))!;
        Check.Equal("k1", both.KeyPassword, "キーのパスワードの環境変数");
        Check.True(!both.ToString().Contains("s1") && !both.ToString().Contains("k1") && both.ToString().Contains(AndroidSigningSecrets.Mask), "ToString は伏せ字");
    }

    /// <summary>テスト用の署名。</summary>
    private static AndroidSigningConfig Signing(string keystore = @"C:\keys\upload.jks") =>
        new(keystore, "upload", new AndroidSigningSecrets("p@ss&word", null, "テスト"), AndroidSigningResolver.RequestOrigin, System.Array.Empty<string>());

    /// <summary>プロジェクトの識別情報。</summary>
    private static AndroidAppIdentity Identity() => AndroidAppIdentityResolver.Resolve(null, "MyGame", "My Game", hasProjectContext: true);

    /// <summary>配布用の Gradle の呼び出し。</summary>
    private static void GradleReleaseInvocation()
    {
        var baseParameters = new GradleBuildParameters(new[] { AndroidAbis.Arm64 }, "both", Identity(), null);
        Check.Equal("assembleDebug", GradleInvocation.Build(baseParameters).Task, "開発用は従来どおり");
        var releaseApk = GradleInvocation.Build(baseParameters with { Variant = AndroidBuildVariant.Release, Signing = Signing() });
        Check.Equal("assembleRelease", releaseApk.Task, "配布用の APK");
        var command = GradleInvocation.Build(baseParameters with
        {
            Variant = AndroidBuildVariant.Release, Format = AndroidPackageFormat.Aab, Signing = Signing(),
        });
        Check.Equal("bundleRelease", command.Task, "AAB");
        Check.True(command.Arguments.Contains(@"-Pseed.signing.storeFile=C:\keys\upload.jks"), "キーストアの場所は安全な文字なので -P");
        Check.True(command.Arguments.Contains("-Pseed.signing.keyAlias=upload"), "別名も -P");
        Check.True(!command.Arguments.Any(a => a.Contains("p@ss") || a.Contains("Password")), "パスワードはコマンドラインに載せない");
        Check.Equal("p@ss&word", command.Environment["ORG_GRADLE_PROJECT_seed.signing.storePassword"], "パスワードは環境変数（本当の値）");
        Check.Equal("p@ss&word", command.Environment["ORG_GRADLE_PROJECT_seed.signing.keyPassword"], "キーのパスワードも環境変数");
        var secret = command.Properties.Single(p => p.Name == "seed.signing.storePassword");
        Check.True(secret.IsSecret && secret.ViaEnvironment && secret.Value == AndroidSigningSecrets.Mask, "一覧（ログ・指紋）は伏せ字");
        Check.True(!command.ToString().Contains("p@ss"), "呼び出しを文字列にしてもパスワードは出ない");

        var debugWithSigning = GradleInvocation.Build(baseParameters with { Signing = Signing() });
        Check.True(!debugWithSigning.Properties.Any(p => p.Name.StartsWith("seed.signing")), "開発用には署名を渡さない");

        var unsafePath = GradleInvocation.Build(baseParameters with { Variant = AndroidBuildVariant.Release, Signing = Signing(@"C:\keys & co\upload.jks") });
        Check.True(unsafePath.Environment.ContainsKey("ORG_GRADLE_PROJECT_seed.signing.storeFile"), "& を含む場所は環境変数");
    }

    /// <summary>アイコンのプロパティ。</summary>
    private static void GradleLauncherIconProperty()
    {
        var plain = GradleInvocation.Build(new GradleBuildParameters(new[] { AndroidAbis.Arm64 }, "both", Identity(), null));
        Check.True(!plain.Arguments.Any(a => a.StartsWith("-Pseed.launcherIcon")), "アイコンが無ければ渡さない（従来の引数のまま）");
        var icon = GradleInvocation.Build(new GradleBuildParameters(new[] { AndroidAbis.Arm64 }, "both", Identity(), null) { LauncherIcon = true });
        Check.True(icon.Arguments.Contains("-Pseed.launcherIcon=generated"), "生成したら generated（build.gradle.kts の generatedLauncherIconMarker と同じ）");
    }

    /// <summary>指紋。</summary>
    private static void GradleFingerprintExcludesSecrets()
    {
        using var temp = new TempDir();
        var engine = new SEEDEditor.Android.Toolchain.AndroidEnginePaths(temp.Path);
        var keystore = temp.WriteFile("keys/upload.jks", "x");
        var parameters = new GradleBuildParameters(new[] { AndroidAbis.Arm64 }, "both", Identity(), null)
        {
            Variant = AndroidBuildVariant.Release, Format = AndroidPackageFormat.Aab, Signing = Signing(keystore),
        };
        var first = AndroidStepFingerprints.Gradle(engine, parameters);
        var otherPassword = parameters with
        {
            Signing = Signing(keystore) with { Secrets = new AndroidSigningSecrets("different-password", null, "テスト") },
        };
        Check.Equal(first.Inputs, AndroidStepFingerprints.Gradle(engine, otherPassword).Inputs, "パスワードが変わっても指紋は同じ（材料にしない）");
        Check.True(!first.Inputs.Contains("p@ss"), "指紋にパスワードの文字が入らない");
        Check.Equal("gradle", AndroidStepKeys.GradleFor(AndroidBuildVariant.Debug, AndroidPackageFormat.Apk), "開発用は従来のキー");
        Check.Equal("gradle/release_aab", AndroidStepKeys.GradleFor(AndroidBuildVariant.Release, AndroidPackageFormat.Aab), "AAB のキー");
        Check.True(engine.ArtifactPath(AndroidBuildVariant.Release, AndroidPackageFormat.Aab).EndsWith("app-release.aab"), "AAB の出力");
        Check.True(engine.ArtifactPath(AndroidBuildVariant.Release, AndroidPackageFormat.Apk).EndsWith("app-release.apk"), "配布用の APK の出力");
        Check.Equal(engine.DebugApkPath, engine.ArtifactPath(AndroidBuildVariant.Debug, AndroidPackageFormat.Aab), "開発用は常にデバッグ版の APK");
    }

    /// <summary>keytool。</summary>
    private static void KeytoolParsing()
    {
        var lines = new[]
        {
            "Alias name: upload",
            "Creation date: Sep 26, 2026",
            "Entry type: PrivateKeyEntry",
            "Certificate chain length: 1",
            "Certificate[1]:",
            "Owner: CN=SEED Upload Key",
            "Issuer: CN=SEED Upload Key",
            "Valid from: Sat Sep 26 12:00:00 JST 2026 until: Mon Feb 11 12:00:00 JST 2054",
            "Certificate fingerprints:",
            "\t SHA1: AA:BB",
            "\t SHA256: 12:AB:CD",
        };
        var certificate = AndroidKeystoreTool.ParseList(lines);
        Check.Equal("upload", certificate.Alias, "別名");
        Check.True(certificate.IsPrivateKey, "秘密鍵の項目");
        Check.Equal("CN=SEED Upload Key", certificate.Owner, "持ち主");
        Check.Equal("12:AB:CD", certificate.Sha256, "SHA-256（SHA1 の行と取り違えない）");
        Check.Equal("Mon Feb 11 12:00:00 JST 2054", certificate.ValidUntil, "有効期限");
        Check.True(!AndroidKeystoreTool.ParseList(new[] { "Entry type: trustedCertEntry" }).IsPrivateKey, "証明書だけの項目は署名に使えない");

        Check.Equal("CN=SEED Upload Key", AndroidKeystoreTool.DistinguishedName(null), "既定の名前");
        Check.Equal(@"CN=Tom\, Jerry \+ Co", AndroidKeystoreTool.DistinguishedName(" Tom, Jerry + Co "), "特別な文字は逃がす");

        using var temp = new TempDir();
        var secrets = new AndroidSigningSecrets("abcdef", null, "テスト");
        var existing = temp.WriteFile("k/existing.jks", "x");
        Check.True(AndroidKeystoreTool.ValidateCreate(new AndroidKeystoreCreateRequest(existing, "upload", secrets, null, null))!.Contains("上書きしません"), "既にあるファイル");
        Check.True(AndroidKeystoreTool.ValidateCreate(new AndroidKeystoreCreateRequest(temp.Combine("assets/k.jks"), "upload", secrets, null, temp.Combine("assets")))!.Contains("アセットフォルダ"), "アセットの中");
        Check.True(AndroidKeystoreTool.ValidateCreate(new AndroidKeystoreCreateRequest(temp.Combine("k/new.jks"), "upload", new AndroidSigningSecrets("12345", null, "t"), null, null))!.Contains("6 文字"), "短いパスワード");
        Check.True(AndroidKeystoreTool.ValidateCreate(new AndroidKeystoreCreateRequest(temp.Combine("k/new.jks"), " ", secrets, null, null))!.Contains("別名"), "別名が無い");
        Check.True(AndroidKeystoreTool.ValidateCreate(new AndroidKeystoreCreateRequest(temp.Combine("k/new.jks"), "upload", secrets, null, null)) is null, "作れる");
        Check.True(!new AndroidKeystoreCreateRequest(temp.Combine("k/new.jks"), "upload", secrets, null, null).ToString().Contains("abcdef"), "指定を文字列にしてもパスワードは出ない");
    }

    /// <summary>計画。</summary>
    private static void ReleasePlan()
    {
        var abis = new[] { AndroidAbis.Arm64 };
        var release = new AndroidRunRequest { Goal = AndroidRunGoal.Build, Variant = AndroidBuildVariant.Release, Format = AndroidPackageFormat.Aab };
        var plan = AndroidBuildPlan.Create(new AndroidPlanInput { Request = release, Abis = abis });
        var phases = plan.Steps.Select(s => s.Phase).ToList();
        Check.Equal(phases.IndexOf(AndroidPipelinePhase.Gradle) + 1, phases.IndexOf(AndroidPipelinePhase.ReleaseCheck), "要件の確認は Gradle の直後");
        Check.True(plan.Runs(AndroidPipelinePhase.ReleaseCheck), "要件の確認は毎回行う");

        var debug = AndroidBuildPlan.Create(new AndroidPlanInput { Request = new AndroidRunRequest { Goal = AndroidRunGoal.Build }, Abis = abis });
        Check.True(debug.Find(AndroidPipelinePhase.ReleaseCheck) is null, "開発用には要件の確認が無い");

        // Gradle の記録は種類と形式ごと: debug の記録があっても release は作り直す
        var fingerprint = new AndroidStepFingerprint("in", "out");
        var current = new System.Collections.Generic.Dictionary<string, AndroidStepFingerprint>
        {
            [AndroidStepKeys.Native(AndroidAbis.Arm64)] = fingerprint,
            [AndroidStepKeys.PackageContent] = fingerprint,
            [AndroidStepKeys.DotnetBundle] = fingerprint,
            [AndroidStepKeys.GradleFor(AndroidBuildVariant.Release, AndroidPackageFormat.Aab)] = fingerprint,
        };
        var recorded = new System.Collections.Generic.Dictionary<string, AndroidStepFingerprint>(current)
        {
            [AndroidStepKeys.Gradle] = fingerprint,
        };
        recorded.Remove(AndroidStepKeys.GradleFor(AndroidBuildVariant.Release, AndroidPackageFormat.Aab));
        var mixed = AndroidBuildPlan.Create(new AndroidPlanInput { Request = release, Abis = abis, Current = current, Recorded = recorded });
        Check.True(mixed.Runs(AndroidPipelinePhase.Gradle) && mixed.Find(AndroidPipelinePhase.Gradle)!.Reason == "前回の記録が無い", "AAB の記録が無ければ作る");
        recorded[AndroidStepKeys.GradleFor(AndroidBuildVariant.Release, AndroidPackageFormat.Aab)] = fingerprint;
        var same = AndroidBuildPlan.Create(new AndroidPlanInput { Request = release, Abis = abis, Current = current, Recorded = recorded });
        Check.True(!same.Runs(AndroidPipelinePhase.Gradle), "AAB の記録が同じなら飛ばす（要件の確認は行う）");
        Check.True(same.Runs(AndroidPipelinePhase.ReleaseCheck), "飛ばしても要件の確認は行う");
    }

    /// <summary>指定の検査。</summary>
    private static void ValidatesVariant()
    {
        var aabDebug = new AndroidRunRequest { Goal = AndroidRunGoal.Build, Format = AndroidPackageFormat.Aab };
        Check.True(AndroidRunPipeline.ValidateVariant(aabDebug)!.Contains("配布用"), "開発用の AAB は無い");
        var aabInstall = new AndroidRunRequest { Goal = AndroidRunGoal.Install, Variant = AndroidBuildVariant.Release, Format = AndroidPackageFormat.Aab };
        Check.True(AndroidRunPipeline.ValidateVariant(aabInstall)!.Contains("直接入れられません"), "AAB は入れられない");
        var releasePush = new AndroidRunRequest { Goal = AndroidRunGoal.Push, Variant = AndroidBuildVariant.Release };
        Check.True(AndroidRunPipeline.ValidateVariant(releasePush)!.Contains("run-as"), "配布用は push 不可");
        var releaseAssets = new AndroidRunRequest { Goal = AndroidRunGoal.Run, Variant = AndroidBuildVariant.Release, AssetsDir = "x" };
        Check.True(AndroidRunPipeline.ValidateVariant(releaseAssets) is not null, "配布用は --assets-dir 不可");
        var debugKeystore = new AndroidRunRequest { Goal = AndroidRunGoal.Build, KeystorePath = "k.jks" };
        Check.True(AndroidRunPipeline.ValidateVariant(debugKeystore)!.Contains("配布用"), "開発用に鍵は使わない");
        var releaseRun = new AndroidRunRequest { Goal = AndroidRunGoal.Run, Variant = AndroidBuildVariant.Release };
        Check.True(AndroidRunPipeline.ValidateVariant(releaseRun) is null, "配布用の APK の run はできる");
        Check.True(releaseRun.OptimizesNative && !new AndroidRunRequest().OptimizesNative, "配布用は常に --release");
    }

    /// <summary>上書きの解除。</summary>
    private static void ReleaseSkipsPushedOverrides()
    {
        using var temp = new TempDir();
        temp.WriteFile("proj/assets/project_settings.json", "{}");
        var project = AndroidProjectResolver.Resolve(temp.Combine("proj"), null);
        var release = new AndroidRunRequest { Goal = AndroidRunGoal.Run, Variant = AndroidBuildVariant.Release, ProjectDir = temp.Combine("proj") };
        var debug = release with { Variant = AndroidBuildVariant.Debug };
        foreach (var moment in new[] { PushedOverrideMoment.AfterInstall, PushedOverrideMoment.BeforeLaunch })
        {
            Check.True(!PushedOverrides.ClearsScripts(release, project, moment), $"{moment}: 配布用は files/bin を消さない");
            Check.True(!PushedOverrides.ClearsAssets(release, project, moment), $"{moment}: 配布用は files/assets を消さない");
            Check.True(PushedOverrides.ClearsScripts(debug, project, moment), $"{moment}: 開発用は従来どおり消す");
        }
        Check.True(!PushedOverrides.ResetsOverlayRecord(release, PushedOverrideMoment.BeforeLaunch, null), "配布用は上書き層の記録も触らない");
    }

    /// <summary>SeedAndroid の引数。</summary>
    private static void SeedAndroidReleaseArguments()
    {
        var full = Parse("build", "--release", "--format", "aab", "--keystore", "upload.jks", "--key-alias", "upload", "--project", "P");
        var request = SeedAndroidArguments.ToRequest(full, null);
        Check.True(request.Variant == AndroidBuildVariant.Release && request.Format == AndroidPackageFormat.Aab, "--format aab で配布用");
        Check.True(request.KeystorePath == "upload.jks" && request.KeyAlias == "upload" && request.Release, "鍵と --release");

        Check.Equal(AndroidBuildVariant.Release, SeedAndroidArguments.ToRequest(Parse("build", "--keystore", "k.jks"), null).Variant, "--keystore で配布用");
        Check.Equal(AndroidBuildVariant.Debug, SeedAndroidArguments.ToRequest(Parse("build", "--release"), null).Variant, "--release だけは従来どおり開発用（Rust の最適化）");
        Check.Equal(AndroidBuildVariant.Release, SeedAndroidArguments.ToRequest(Parse("check", "--project", "P"), null).Variant, "check の既定は配布用");
        Check.Equal(AndroidRunGoal.Build, SeedAndroidArguments.ToRequest(Parse("check"), null).Goal, "check は作らないので Build の材料");
        var config = new AndroidRunRequest { Variant = AndroidBuildVariant.Release, Format = AndroidPackageFormat.Aab, KeystorePath = "c.jks" };
        var fromConfig = SeedAndroidArguments.ToRequest(Parse("build"), config);
        Check.True(fromConfig.Variant == AndroidBuildVariant.Release && fromConfig.KeystorePath == "c.jks", "設定 JSON の配布用の値を使う");

        // 設定 JSON のファイル（小文字の値・キーストアの相対パスは JSON のフォルダから・パスワードの欄は読まない）
        using (var temp = new TempDir())
        {
            var json = temp.WriteFile("cfg/android.json",
                "{ \"variant\": \"release\", \"format\": \"aab\", \"keystore\": \"../keys/up.jks\", \"key_alias\": \"upload\", \"signing_secrets\": \"x\" }");
            var loaded = RunRequestConfig.Load(json, out var configError);
            Check.True(loaded is not null && configError is null, $"読める: {configError}");
            Check.True(loaded!.Variant == AndroidBuildVariant.Release && loaded.Format == AndroidPackageFormat.Aab, "小文字の release / aab を読む");
            Check.Equal(temp.Combine("keys/up.jks"), loaded.KeystorePath, "キーストアの相対パスは JSON のフォルダから");
            Check.True(loaded.SigningSecrets is null, "パスワードは JSON から読まない");
        }

        ExpectError("--variant と --keystore の食い違い", "配布用", "build", "--variant", "debug", "--keystore", "k.jks");
        ExpectError("install の AAB", "直接入れられません", "install", "--format", "aab");
        ExpectError("配布用の --push-scripts", "run-as", "run", "--variant", "release", "--push-scripts");
        ExpectError("push に配布用", "build / install / run / check", "push", "--variant", "release");
        ExpectError("知らない種類", "debug / release", "build", "--variant", "beta");
        ExpectError("知らない形式", "apk / aab", "build", "--format", "zip");
        ExpectError("keystore の操作が無い", "keystore には操作", "keystore", "--keystore", "k.jks");
        ExpectError("keystore create の場所が無い", "--keystore", "keystore", "create");
        ExpectError("知らない操作", "keystore の操作は", "keystore", "delete");
        ExpectError("--cert-name は keystore create だけ", "--cert-name", "build", "--cert-name", "X");
        ExpectError("--artifact は check だけ", "--artifact", "build", "--artifact", "a.aab");

        var create = Parse("keystore", "create", "--keystore", "new.jks", "--cert-name", "My Game");
        Check.True(create.KeystoreAction == SeedAndroidKeystoreAction.Create && create.KeystorePath == "new.jks" && create.CertificateName == "My Game", "keystore create");
        Check.True(create.KeyAlias is null, "別名は省略できる（既定 upload）");
        var check = Parse("check", "--artifact", "x.aab", "--project", "P");
        Check.Equal("x.aab", check.ArtifactPath, "check --artifact");
    }

    /// <summary>引数を解釈する（誤りなら失敗）。</summary>
    private static SeedAndroidCommandLine Parse(params string[] args)
    {
        var result = SeedAndroidArguments.Parse(args);
        Check.True(result.Error is null && result.CommandLine is not null, $"{string.Join(' ', args)}: {result.Error}");
        return result.CommandLine!;
    }

    /// <summary>引数の誤りを確かめる。</summary>
    private static void ExpectError(string what, string expected, params string[] args)
    {
        var result = SeedAndroidArguments.Parse(args);
        Check.True(result.Error is not null && result.Error.Contains(expected), $"{what}: {result.Error ?? "誤りにならない"}");
    }

    /// <summary>中核の例外を期待する。</summary>
    private static AndroidPipelineException Expect(System.Action action)
    {
        try
        {
            action();
        }
        catch (AndroidPipelineException ex)
        {
            return ex;
        }
        throw new AssertionException("AndroidPipelineException が投げられませんでした");
    }
}
