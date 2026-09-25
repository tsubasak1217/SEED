using System.Text;
using SEEDEditor.Accounts.Storage;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Signing;
using SEEDEditor.AndroidRun;
using SEEDEditor.Packaging;
using SEEDEditor.Settings;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// パッケージ化ウィンドウの Android の配布用（段階D。docs/android.md §24）の判断:
/// 出力の名前（release の APK / AAB）・ビルドの種類と形式の選択肢・中核への指定・署名のパスワードの保護保存。
/// </summary>
public static class ReleasePackagingTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("配布用: 出力の名前（{ゲーム名}-{ABI}-release.apk / .aab）は開発用の APK と並べて置ける", ReleaseNames);
        harness.Add("配布用: ビルドの種類・形式の選択肢と表示名の往復（知らない表示名は既定＝開発用・APK）", VariantAndFormatChoices);
        harness.Add("配布用: 中核への指定（Build・release・形式・Rust は --release・鍵は設定ファイルから・パスワードはメモリだけ）", ReleaseRequest);
        harness.Add("署名のパスワードの保護保存: 往復・パスの大小文字と別名・消す・解けない記録は無いもの・ファイルに平文を書かない", SecretStore);
        harness.Add("packaging_settings.json: 配布用の設定（variant・format・signing）の往復と、古いファイル・signing: null の既定値", PackagingDataRoundTrip);
    }

    /// <summary>出力の名前。</summary>
    private static void ReleaseNames()
    {
        var abis = new[] { "arm64-v8a" };
        Check.Equal("Game-arm64-v8a-release.apk", AndroidApkOutput.FileName("Game", abis, AndroidBuildVariant.Release, AndroidPackageFormat.Apk), "配布用の APK");
        Check.Equal("Game-arm64-v8a-release.aab", AndroidApkOutput.FileName("Game", abis, AndroidBuildVariant.Release, AndroidPackageFormat.Aab), "AAB");
        Check.Equal("Game-arm64-v8a-debug.apk", AndroidApkOutput.FileName("Game", abis, AndroidBuildVariant.Debug, AndroidPackageFormat.Apk), "開発用は従来の名前");
        Check.Equal(AndroidApkOutput.FileName("Game", abis), AndroidApkOutput.FileName("Game", abis, AndroidBuildVariant.Debug, AndroidPackageFormat.Apk), "従来の関数と同じ");
        var aab = AndroidApkOutput.DestinationPath(Path.Combine("D:", "out"), "Game", abis, AndroidBuildVariant.Release, AndroidPackageFormat.Aab);
        var apk = AndroidApkOutput.DestinationPath(Path.Combine("D:", "out"), "Game", abis, AndroidBuildVariant.Release, AndroidPackageFormat.Apk);
        Check.Equal(Path.GetDirectoryName(aab), Path.GetDirectoryName(apk), "APK と AAB は同じフォルダに並ぶ");
        Check.True(aab != apk, "名前は別");
    }

    /// <summary>選択肢。</summary>
    private static void VariantAndFormatChoices()
    {
        Check.Equal(AndroidBuildVariant.Debug, AndroidApkOutput.VariantChoices[0].Variant, "既定は開発用");
        Check.Equal(AndroidPackageFormat.Apk, AndroidApkOutput.FormatChoices[0].Format, "既定は APK");
        foreach (var (variant, _) in AndroidApkOutput.VariantChoices)
        {
            Check.Equal(variant, AndroidApkOutput.VariantFor(AndroidApkOutput.LabelFor(variant)), $"{variant}: 往復");
        }
        foreach (var (format, _) in AndroidApkOutput.FormatChoices)
        {
            Check.Equal(format, AndroidApkOutput.FormatFor(AndroidApkOutput.LabelFor(format)), $"{format}: 往復");
        }
        Check.Equal(AndroidBuildVariant.Debug, AndroidApkOutput.VariantFor("知らない"), "知らない表示名は開発用");
        Check.Equal(AndroidPackageFormat.Apk, AndroidApkOutput.FormatFor(null), "null は APK");
    }

    /// <summary>中核への指定。</summary>
    private static void ReleaseRequest()
    {
        var secrets = new AndroidSigningSecrets("secret-pass", null, "テスト");
        var request = AndroidEditorRunRequests.ForReleasePackage("D:/proj", new[] { "arm64-v8a" }, AndroidPackageFormat.Aab, null, "  ", secrets);
        Check.Equal(AndroidRunGoal.Build, request.Goal, "パッケージ化は Build");
        Check.Equal(AndroidBuildVariant.Release, request.Variant, "配布用");
        Check.Equal(AndroidPackageFormat.Aab, request.Format, "形式");
        Check.True(request.OptimizesNative, "Rust は --release");
        Check.True(request.KeystorePath is null && request.KeyAlias is null, "鍵の場所は中核が packaging_settings.json から読む（空白は null）");
        Check.True(ReferenceEquals(secrets, request.SigningSecrets), "パスワードはメモリの中で渡す");
        Check.True(AndroidRunPipeline.ValidateVariant(request) is null, "指定の食い違いは無い");
        Check.True(!request.ToString().Contains("secret-pass"), $"指定を文字列にしてもパスワードは出ない: {request}");

        var json = System.Text.Json.JsonSerializer.Serialize(request);
        Check.True(!json.Contains("secret-pass"), "JSON にもパスワードは出ない");
        Check.True(json.Contains("\"variant\":\"Release\"") && json.Contains("\"format\":\"Aab\""), $"種類と形式は JSON の文字列: {json}");
    }

    /// <summary>テスト用の包み方（中身を反転するだけ。平文がファイルにそのまま出ないことを確かめられる）。</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        /// <summary>解けないふりをするか。</summary>
        public bool Broken { get; set; }

        /// <inheritdoc />
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();

        /// <inheritdoc />
        public byte[] Unprotect(byte[] protectedBytes) =>
            Broken ? throw new System.Security.Cryptography.CryptographicException("別の PC") : protectedBytes.Reverse().ToArray();
    }

    /// <summary>保護保存。</summary>
    private static void SecretStore()
    {
        using var temp = new AndroidPipelineTests.TempDir();
        var protector = new ReversingProtector();
        var file = temp.Combine("settings/android_signing_secrets.json");
        var store = new AndroidSigningSecretStore(file, protector);
        var keystore = temp.Combine("keys/Upload.jks");

        Check.True(!store.Has(keystore, "upload") && store.Load(keystore, "upload") is null, "最初は無い");
        store.Save(keystore, "upload", new AndroidSigningSecrets("pass-1234", null, "テスト"));
        Check.True(store.Has(keystore.ToUpperInvariant(), "UPLOAD"), "パスの大小文字と別名の大小文字は区別しない");
        var loaded = store.Load(keystore, "upload");
        Check.Equal("pass-1234", loaded?.KeystorePassword, "取り出せる");
        Check.Equal("pass-1234", loaded?.KeyPassword, "キーのパスワードは同じ");
        Check.Equal(AndroidSigningSecretStore.Origin, loaded?.Origin, "出どころ");
        var text = File.ReadAllText(file, Encoding.UTF8);
        Check.True(!text.Contains("pass-1234"), "ファイルに平文を書かない");
        Check.True(store.Load(keystore, "other") is null, "別名が違えば無い");

        store.Save(keystore, "upload", new AndroidSigningSecrets("pass-5678", "key-9999", "テスト"));
        var replaced = store.Load(keystore, "upload");
        Check.True(replaced?.KeystorePassword == "pass-5678" && replaced.KeyPassword == "key-9999", "置き換え（キーのパスワードも）");

        protector.Broken = true;
        Check.True(store.Load(keystore, "upload") is null, "解けない記録（別の PC・別のユーザー）は無いものとして扱う");
        protector.Broken = false;

        Check.True(store.Remove(keystore, "upload"), "消す");
        Check.True(!store.Has(keystore, "upload") && !store.Remove(keystore, "upload"), "消した後は無い");
        File.WriteAllText(file, "{ 壊れた");
        Check.True(store.Load(keystore, "upload") is null, "壊れたファイルは空として読む");
    }

    /// <summary>packaging_settings.json の配布用の設定。</summary>
    private static void PackagingDataRoundTrip()
    {
        using var temp = new AndroidPipelineTests.TempDir();
        var path = temp.Combine(PackagingData.SettingsFileName);
        File.WriteAllText(path, "{ \"android\": { \"output_path\": \"out\", \"build_type\": \"Debug\" } }");
        var old = PackagingData.LoadFrom(path);
        Check.Equal(AndroidBuildVariant.Debug, old.Android.Variant, "古いファイルは開発用");
        Check.Equal(AndroidPackageFormat.Apk, old.Android.Format, "古いファイルは APK");
        Check.True(old.Android.Signing.IsEmpty, "鍵は未設定");

        old.Android.Variant = AndroidBuildVariant.Release;
        old.Android.Format = AndroidPackageFormat.Aab;
        old.Android.Signing.KeystorePath = "../keys/upload.jks";
        old.Android.Signing.KeyAlias = "upload";
        old.SaveTo(path);
        var text = File.ReadAllText(path);
        Check.True(text.Contains("\"variant\": \"Release\"") && text.Contains("\"format\": \"Aab\"") && text.Contains("\"keystore_path\""), $"書き出し: {text}");
        Check.True(!text.Contains("password", StringComparison.OrdinalIgnoreCase), "パスワードの欄は無い");
        var loaded = PackagingData.LoadFrom(path);
        Check.True(loaded.Android.Variant == AndroidBuildVariant.Release && loaded.Android.Format == AndroidPackageFormat.Aab, "種類と形式を読み戻す");
        Check.Equal("upload", loaded.Android.Signing.KeyAlias, "別名");

        File.WriteAllText(path, "{ \"android\": { \"signing\": null, \"variant\": \"release\" } }");
        var nullSigning = PackagingData.LoadFrom(path);
        Check.True(nullSigning.Android.Signing is not null, "signing: null でも節を持つ");
        Check.Equal(AndroidBuildVariant.Release, nullSigning.Android.Variant, "小文字の release も読む");
    }
}
