// ============================================================
//  AndroidApkOutput.cs — パッケージ化ウィンドウの Android 出力の決まり（ABI の選択肢・APK の名前と置き場。純粋な処理）
//
//  【出力】{出力フォルダ}/{ゲーム名}/{ゲーム名}-{ABI（+ でつなぐ）}-{debug|release}.{apk|aab}
//    例: build/android/WarashibeFishing/WarashibeFishing-arm64-v8a-debug.apk     … 開発用（デバッグ署名）
//        build/android/WarashibeFishing/WarashibeFishing-arm64-v8a-release.apk   … 配布用（アップロード鍵で署名。段階D）
//        build/android/WarashibeFishing/WarashibeFishing-arm64-v8a-release.aab   … Google Play へ出す AAB（段階D）
//  名前の debug は「Android のデバッグ版（debuggable・デバッグ用の鍵で署名）」の意味。Rust を Release で最適化しても
//  開発用の APK はデバッグ署名のまま。配布用（release）は同じフォルダへ APK と AAB を並べて置ける（docs/android.md §20.6・§24）。
//  配布物自体は中核（Goal = Build）が runtime/android/app/build/outputs/ の下に作り（AndroidPipelineResult.ArtifactPath）、
//  ここで決めた場所へ写す。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Android;

namespace SEEDEditor.Packaging;

/// <summary>ABI の選択肢 1 つ（パッケージ化ウィンドウのコンボ）。</summary>
/// <param name="Arch">設定の値。</param>
/// <param name="Label">表示名。</param>
public sealed record AndroidArchChoice(AndroidArch Arch, string Label);

/// <summary>パッケージ化ウィンドウの Android 出力の決まり。</summary>
public static class AndroidApkOutput
{
    /// <summary>APK の拡張子。</summary>
    public const string ApkExtension = ".apk";

    /// <summary>AAB の拡張子（段階D）。</summary>
    public const string AabExtension = ".aab";

    /// <summary>APK の名前の末尾（Android のデバッグ版＝デバッグ署名であることを示す）。</summary>
    public const string DebugVariantSuffix = "debug";

    /// <summary>配布用の名前の末尾（アップロード鍵で署名した release。段階D）。</summary>
    public const string ReleaseVariantSuffix = "release";

    /// <summary>名前の中で ABI どうしをつなぐ文字。</summary>
    public const char AbiJoiner = '+';

    /// <summary>名前の部品の区切り。</summary>
    private const char NameSeparator = '-';

    /// <summary>ABI の選択肢（表示の順。先頭が既定）。</summary>
    public static readonly IReadOnlyList<AndroidArchChoice> ArchChoices = new[]
    {
        new AndroidArchChoice(AndroidArch.Arm64V8a, "arm64-v8a（実機・配布用）"),
        new AndroidArchChoice(AndroidArch.X86_64,   "x86_64（PC のエミュレータ用）"),
        new AndroidArchChoice(AndroidArch.Both,     "両方（arm64-v8a + x86_64）"),
    };

    /// <summary>設定の値の表示名（知らない値は先頭の選択肢）。</summary>
    /// <param name="arch">設定の値。</param>
    /// <returns>表示名。</returns>
    public static string LabelFor(AndroidArch arch) =>
        (ArchChoices.FirstOrDefault(choice => choice.Arch == arch) ?? ArchChoices[0]).Label;

    /// <summary>表示名から設定の値を引く（知らない表示名は先頭の選択肢）。</summary>
    /// <param name="label">表示名。</param>
    /// <returns>設定の値。</returns>
    public static AndroidArch ArchFor(string? label) =>
        (ArchChoices.FirstOrDefault(choice => choice.Label == label) ?? ArchChoices[0]).Arch;

    /// <summary>
    /// 設定の値から APK に詰める ABI の名前を決める（中核の ABI の表の順）。
    /// </summary>
    /// <param name="arch">設定の値。</param>
    /// <returns>ABI の名前。</returns>
    public static IReadOnlyList<string> AbisFor(AndroidArch arch) => arch switch
    {
        AndroidArch.X86_64 => new[] { AndroidAbis.X86_64.Name },
        AndroidArch.Both   => AndroidAbis.Supported.Select(abi => abi.Name).ToArray(),
        _                  => new[] { AndroidAbis.Arm64.Name },
    };

    /// <summary>
    /// APK のファイル名（{ゲーム名}-{ABI}-debug.apk）。
    /// </summary>
    /// <param name="gameName">ゲーム名（ファイル名に使える文字に揃えたもの）。</param>
    /// <param name="abis">詰めた ABI。</param>
    /// <returns>ファイル名。</returns>
    public static string FileName(string gameName, IReadOnlyList<string> abis) =>
        FileName(gameName, abis, AndroidBuildVariant.Debug, AndroidPackageFormat.Apk);

    /// <summary>
    /// 配布物のファイル名（{ゲーム名}-{ABI}-{debug|release}.{apk|aab}。段階D）。
    /// </summary>
    /// <param name="gameName">ゲーム名（ファイル名に使える文字に揃えたもの）。</param>
    /// <param name="abis">詰めた ABI。</param>
    /// <param name="variant">ビルドの種類。</param>
    /// <param name="format">形式。</param>
    /// <returns>ファイル名。</returns>
    public static string FileName(string gameName, IReadOnlyList<string> abis, AndroidBuildVariant variant, AndroidPackageFormat format) =>
        $"{gameName}{NameSeparator}{string.Join(AbiJoiner, abis)}{NameSeparator}" +
        $"{(variant == AndroidBuildVariant.Release ? ReleaseVariantSuffix : DebugVariantSuffix)}" +
        $"{(format == AndroidPackageFormat.Aab ? AabExtension : ApkExtension)}";

    /// <summary>
    /// APK を写す先（{出力フォルダ}/{ゲーム名}/{ファイル名}。Windows の出力と同じくゲーム名のフォルダを作る）。
    /// </summary>
    /// <param name="outputPath">出力フォルダ。</param>
    /// <param name="gameName">ゲーム名。</param>
    /// <param name="abis">詰めた ABI。</param>
    /// <returns>写す先の絶対パス。</returns>
    public static string DestinationPath(string outputPath, string gameName, IReadOnlyList<string> abis) =>
        Path.Combine(outputPath, gameName, FileName(gameName, abis));

    /// <summary>
    /// 配布物を写す先（{出力フォルダ}/{ゲーム名}/{ファイル名}。段階D）。
    /// </summary>
    /// <param name="outputPath">出力フォルダ。</param>
    /// <param name="gameName">ゲーム名。</param>
    /// <param name="abis">詰めた ABI。</param>
    /// <param name="variant">ビルドの種類。</param>
    /// <param name="format">形式。</param>
    /// <returns>写す先の絶対パス。</returns>
    public static string DestinationPath(
        string outputPath, string gameName, IReadOnlyList<string> abis, AndroidBuildVariant variant, AndroidPackageFormat format) =>
        Path.Combine(outputPath, gameName, FileName(gameName, abis, variant, format));

    // ── ビルドの種類と形式の選択肢（段階D）──────────────────────

    /// <summary>ビルドの種類の選択肢（表示の順。先頭が既定）。</summary>
    public static readonly IReadOnlyList<(AndroidBuildVariant Variant, string Label)> VariantChoices = new[]
    {
        (AndroidBuildVariant.Debug, "開発用（デバッグ署名。端末で試す）"),
        (AndroidBuildVariant.Release, "配布用（release。アップロード鍵で署名）"),
    };

    /// <summary>形式の選択肢（表示の順。先頭が既定）。</summary>
    public static readonly IReadOnlyList<(AndroidPackageFormat Format, string Label)> FormatChoices = new[]
    {
        (AndroidPackageFormat.Apk, "APK（端末へ直接入れる）"),
        (AndroidPackageFormat.Aab, "AAB（Google Play へ出す。配布用だけ）"),
    };

    /// <summary>ビルドの種類の表示名（知らない値は先頭）。</summary>
    /// <param name="variant">値。</param>
    /// <returns>表示名。</returns>
    public static string LabelFor(AndroidBuildVariant variant) =>
        VariantChoices.FirstOrDefault(choice => choice.Variant == variant).Label ?? VariantChoices[0].Label;

    /// <summary>表示名からビルドの種類を引く（知らない表示名は先頭）。</summary>
    /// <param name="label">表示名。</param>
    /// <returns>値。</returns>
    public static AndroidBuildVariant VariantFor(string? label)
    {
        foreach (var choice in VariantChoices)
        {
            if (choice.Label == label) return choice.Variant;
        }
        return VariantChoices[0].Variant;
    }

    /// <summary>形式の表示名（知らない値は先頭）。</summary>
    /// <param name="format">値。</param>
    /// <returns>表示名。</returns>
    public static string LabelFor(AndroidPackageFormat format) =>
        FormatChoices.FirstOrDefault(choice => choice.Format == format).Label ?? FormatChoices[0].Label;

    /// <summary>表示名から形式を引く（知らない表示名は先頭）。</summary>
    /// <param name="label">表示名。</param>
    /// <returns>値。</returns>
    public static AndroidPackageFormat FormatFor(string? label)
    {
        foreach (var choice in FormatChoices)
        {
            if (choice.Label == label) return choice.Format;
        }
        return FormatChoices[0].Format;
    }
}
