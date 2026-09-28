// ============================================================
//  AndroidNativeProfile.cs — libSEED.so を作る cargo のプロファイルを決める（純粋な処理）
//
//  【なぜ要るか】
//  2026-09-28 の実機の計測（docs/app_platform_roadmap.md §3.9）で、開発用の APK の .so が cargo の dev
//  （SEED クレートは最適化なし）だと UI の見本のスクロールが 19〜26 fps しか出ず、develop（opt-level 1）で
//  59.4〜59.7 fps になった。PC の Play は既に develop が既定（editor/config/runtime_build_configs.json）なので、
//  Android の開発用の .so も**同じ構成の表**から選ぶ（id・cargo のプロファイル名・出力フォルダの対応を 2 か所に書かない）。
//
//  【決め方（上ほど強い）】
//    1. 配布用（Variant = Release）… 常に release（配布物は最適化した .so。native_profile に release 以外を書けば誤り）
//    2. --release（Request.Release）… release（従来どおりの意味。native_profile と食い違えば誤り）
//    3. native_profile（SeedAndroid の --native-profile・設定 JSON・エディタの実行ボタンはツールバーの構成）… その構成
//    4. どれも無ければ構成の表の既定（runtime_build_configs.json の default。2026-09-28 時点で develop）
//  ネイティブのデバッガで追うときは --native-profile debug（cargo の dev・最適化なし）に戻す。
//
//  【cargo ndk へ渡す引数】
//    dev     … 何も足さない（cargo の既定。従来の開発用と同じ）
//    release … --release（従来の配布用と同じ）
//    その他  … --profile <名前>（develop など。出力は target/<ターゲット>/<出力フォルダ>/。cargo-ndk が -o へ写す）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;
using SEEDEditor.Runtime.BuildConfig;

namespace SEEDEditor.Android.Native;

/// <summary>プロファイルをどこから決めたか（ログと検査用）。</summary>
public enum AndroidNativeProfileSource
{
    /// <summary>配布用のビルド（常に release）。</summary>
    ReleaseVariant,

    /// <summary>--release の指定。</summary>
    ReleaseSwitch,

    /// <summary>native_profile の指定（--native-profile・設定 JSON・エディタのツールバーの構成）。</summary>
    Requested,

    /// <summary>構成の表の既定（runtime_build_configs.json の default）。</summary>
    CatalogDefault,
}

/// <summary>libSEED.so を作る cargo のプロファイル（構成の表の 1 件と、それを選んだ理由）。</summary>
/// <param name="ConfigId">構成の id（debug / develop / release。PC の Play と同じ）。</param>
/// <param name="Label">構成の表示名。</param>
/// <param name="CargoProfile">cargo のプロファイル名（dev / develop / release）。</param>
/// <param name="TargetDir">target/&lt;ターゲット&gt;/ の下の出力フォルダ名（dev だけ debug）。</param>
/// <param name="Source">どこから決めたか。</param>
public sealed record AndroidNativeProfile(
    string ConfigId, string Label, string CargoProfile, string TargetDir, AndroidNativeProfileSource Source)
{
    /// <summary>cargo の組み込みの開発用プロファイル名（引数を足さないときのプロファイル）。</summary>
    public const string CargoDevProfile = "dev";

    /// <summary>cargo の組み込みの配布用プロファイル名（--release と同じ）。</summary>
    public const string CargoReleaseProfile = "release";

    /// <summary>cargo の --release。</summary>
    public const string CargoReleaseSwitch = "--release";

    /// <summary>cargo の --profile。</summary>
    public const string CargoProfileOption = "--profile";

    /// <summary>このプロファイルが最適化した配布相当の .so か（cargo の release）。</summary>
    public bool IsRelease => string.Equals(CargoProfile, CargoReleaseProfile, StringComparison.Ordinal);

    /// <summary>cargo ndk … build の後ろへ足す引数（上の【cargo ndk へ渡す引数】）。</summary>
    public IReadOnlyList<string> CargoArguments =>
        CargoProfile switch
        {
            CargoDevProfile => Array.Empty<string>(),
            CargoReleaseProfile => new[] { CargoReleaseSwitch },
            _ => new[] { CargoProfileOption, CargoProfile },
        };

    /// <summary>
    /// cargo が作る .so の場所（runtime/target/&lt;Rust のターゲット&gt;/&lt;出力フォルダ&gt;/libSEED.so）。
    /// cargo-ndk はここから jniLibs へ写す（写し漏れの検査と記録に使う）。
    /// </summary>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="abi">ABI。</param>
    /// <returns>パス。</returns>
    public string BuiltLibraryPath(AndroidEnginePaths engine, AndroidAbi abi) =>
        Path.Combine(engine.CargoTargetDir, abi.RustTarget, TargetDir, AndroidRuntimeContract.NativeLibraryFileName);

    /// <summary>ログ用の説明（例: 「Develop（cargo --profile develop・構成の表の既定）」）。</summary>
    /// <returns>説明。</returns>
    public string Describe()
    {
        var arguments = CargoArguments.Count == 0 ? $"cargo の {CargoDevProfile}（引数なし）" : "cargo " + string.Join(' ', CargoArguments);
        return $"{Label}（id={ConfigId}・{arguments}・{AndroidNativeProfileResolver.DescribeSource(Source)}）";
    }
}

/// <summary>libSEED.so のプロファイルの決め方（上の【決め方】）。</summary>
public static class AndroidNativeProfileResolver
{
    /// <summary>
    /// 構成の表（editor/config/runtime_build_configs.json）を読む。読めなければ組み込みの表（警告は <paramref name="warnings"/> へ）。
    /// </summary>
    /// <param name="engine">エンジン側の置き場（リポジトリのルートから表の場所を決める）。</param>
    /// <param name="warnings">表を読めなかった理由（無ければ空）。</param>
    /// <returns>構成の表。</returns>
    public static RuntimeBuildConfigCatalog LoadCatalog(AndroidEnginePaths engine, out IReadOnlyList<string> warnings)
    {
        var catalog = RuntimeBuildConfigCatalog.LoadFromDir(engine.RuntimeBuildConfigDir);
        warnings = catalog.Warnings;
        return catalog;
    }

    /// <summary>
    /// 指定から libSEED.so のプロファイルを決める。指定の誤り（知らない id・配布用と食い違う id）は例外。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="catalog">構成の表。</param>
    /// <returns>プロファイル。</returns>
    /// <exception cref="AndroidPipelineException">指定の誤り（<see cref="AndroidFailureKind.InvalidRequest"/>）。</exception>
    public static AndroidNativeProfile Resolve(AndroidRunRequest request, RuntimeBuildConfigCatalog catalog)
    {
        if (Validate(request, catalog) is { } error)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, error);
        }
        var requested = Normalize(request.NativeProfile);
        // 1・2: 配布用と --release は常に cargo の release（表に release の構成が無くても従来どおり --release で作る）
        if (request.Variant == AndroidBuildVariant.Release || request.Release)
        {
            var source = request.Variant == AndroidBuildVariant.Release
                ? AndroidNativeProfileSource.ReleaseVariant
                : AndroidNativeProfileSource.ReleaseSwitch;
            var releaseConfig = FindByCargoProfile(catalog, AndroidNativeProfile.CargoReleaseProfile);
            return releaseConfig is null
                ? new AndroidNativeProfile(AndroidNativeProfile.CargoReleaseProfile, AndroidNativeProfile.CargoReleaseProfile,
                    AndroidNativeProfile.CargoReleaseProfile, AndroidNativeProfile.CargoReleaseProfile, source)
                : FromConfig(releaseConfig, source);
        }
        // 3: 指定された構成（Validate で表にあることを確かめた）
        if (requested is not null)
        {
            return FromConfig(catalog.Find(requested)!, AndroidNativeProfileSource.Requested);
        }
        // 4: 表の既定
        return FromConfig(catalog.Default, AndroidNativeProfileSource.CatalogDefault);
    }

    /// <summary>
    /// 指定の食い違い（純粋な処理。問題が無ければ null）。
    /// 知らない構成の id・配布用（--variant release / --release）と release 以外の構成の組み合わせを弾く。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="catalog">構成の表。</param>
    /// <returns>問題の説明。</returns>
    public static string? Validate(AndroidRunRequest request, RuntimeBuildConfigCatalog catalog)
    {
        var requested = Normalize(request.NativeProfile);
        if (requested is null) return null;
        var config = catalog.Find(requested);
        if (config is null)
        {
            return $"libSEED.so の構成 '{requested}' は構成の表にありません（使える値: {string.Join(" / ", catalog.Configs.Select(c => c.Id))}。" +
                   $"表は {RuntimeBuildConfigCatalog.FileName}）。";
        }
        var releaseOnly = request.Variant == AndroidBuildVariant.Release || request.Release;
        if (releaseOnly && !string.Equals(config.CargoProfile, AndroidNativeProfile.CargoReleaseProfile, StringComparison.Ordinal))
        {
            return request.Variant == AndroidBuildVariant.Release
                ? $"配布用（release）のビルドの libSEED.so は常に release で作ります（--native-profile {config.Id} とは一緒に使えません）。"
                : $"--release と --native-profile {config.Id} は食い違っています（--release は --native-profile release と同じです）。";
        }
        return null;
    }

    /// <summary>ログ用の「どこから決めたか」。</summary>
    /// <param name="source">決め方。</param>
    /// <returns>説明。</returns>
    public static string DescribeSource(AndroidNativeProfileSource source) => source switch
    {
        AndroidNativeProfileSource.ReleaseVariant => "配布用のビルドは常に release",
        AndroidNativeProfileSource.ReleaseSwitch => "--release の指定",
        AndroidNativeProfileSource.Requested => "native_profile の指定",
        AndroidNativeProfileSource.CatalogDefault => $"{RuntimeBuildConfigCatalog.FileName} の既定",
        _ => source.ToString(),
    };

    /// <summary>
    /// 指定の中身だけの短い説明（構成の表を読まずに言える範囲。パッケージ化ウィンドウのログ用）。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <returns>説明。</returns>
    public static string DescribeRequested(AndroidRunRequest request)
    {
        if (request.Variant == AndroidBuildVariant.Release || request.Release) return $"{AndroidNativeProfile.CargoReleaseProfile}（--release）";
        return Normalize(request.NativeProfile) is { } requested
            ? $"{requested}（native_profile の指定）"
            : $"{RuntimeBuildConfigCatalog.FileName} の既定";
    }

    /// <summary>構成の表の 1 件からプロファイルを作る。</summary>
    private static AndroidNativeProfile FromConfig(RuntimeBuildConfig config, AndroidNativeProfileSource source) =>
        new(config.Id, config.Label, config.CargoProfile, config.TargetDir, source);

    /// <summary>cargo のプロファイル名で構成を引く（無ければ null）。</summary>
    private static RuntimeBuildConfig? FindByCargoProfile(RuntimeBuildConfigCatalog catalog, string cargoProfile) =>
        catalog.Configs.FirstOrDefault(c => string.Equals(c.CargoProfile, cargoProfile, StringComparison.Ordinal));

    /// <summary>空白だけの指定は「指定なし」にする。</summary>
    private static string? Normalize(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
}
