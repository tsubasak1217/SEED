// ============================================================
//  PackagingData.cs — パッケージ化設定データモデル
//
//  プラットフォームごとのビルド設定を保持し、
//  {assetsPath}/packaging_settings.json に永続化する。
// ============================================================

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Packaging;

// ─── プラットフォーム定義 ──────────────────────────────────────

/// <summary>ビルド対象のプラットフォーム。</summary>
public enum TargetPlatform
{
    Windows,
    macOS,
    Android,
    iOS,
    PlayStation5,
    NintendoSwitch,
}

/// <summary>ビルド種別（最適化レベル）。</summary>
public enum BuildType
{
    Debug,
    Release,
}

/// <summary>Windows ビルドのアーキテクチャ。</summary>
public enum WindowsArch
{
    X64,
    Arm64,
}

/// <summary>macOS ビルドのアーキテクチャ。</summary>
public enum MacArch
{
    X64,
    Arm64,
    Universal,
}

/// <summary>
/// Android ビルドのアーキテクチャ（APK に詰める ABI。表示名と ABI 名の対応は AndroidApkOutput.cs）。
/// </summary>
public enum AndroidArch
{
    /// <summary>arm64-v8a（現行の実機ほぼすべて。配布用）。</summary>
    Arm64V8a,

    /// <summary>x86_64（PC のエミュレータで試すためだけ）。</summary>
    X86_64,

    /// <summary>両方（1 つの APK に arm64-v8a と x86_64 を詰める。実機とエミュレータの両方で試すとき）。</summary>
    Both,
}

// ─── プラットフォームごとの設定 ───────────────────────────────

/// <summary>Windows パッケージング設定。</summary>
public class WindowsSettings
{
    [JsonPropertyName("output_path")]
    public string OutputPath { get; set; } = "";

    [JsonPropertyName("build_type")]
    public BuildType BuildType { get; set; } = BuildType.Release;

    [JsonPropertyName("arch")]
    public WindowsArch Arch { get; set; } = WindowsArch.X64;
}

/// <summary>macOS パッケージング設定。</summary>
public class MacOsSettings
{
    [JsonPropertyName("output_path")]
    public string OutputPath { get; set; } = "";

    [JsonPropertyName("build_type")]
    public BuildType BuildType { get; set; } = BuildType.Release;

    [JsonPropertyName("arch")]
    public MacArch Arch { get; set; } = MacArch.Arm64;
}

/// <summary>
/// Android パッケージング設定（段階C-2 で実働化。中核 editor/src/Android/ の Goal = Build で APK を作る）。
///
/// <para>
/// 以前あった NDK のパス（ndk_path）は段階C-2 で廃止した。道具の場所は環境変数と既定の場所から自動で探す
/// （editor/src/Android/Toolchain/AndroidToolchain.cs。マシン固有のパスをプロジェクトの設定に書かない）。
/// 古い設定ファイルの ndk_path は読み飛ばし、次の保存で消える。
/// </para>
/// </summary>
public class AndroidSettings
{
    [JsonPropertyName("output_path")]
    public string OutputPath { get; set; } = "";

    /// <summary>Rust（libSEED.so）の最適化。Release は cargo --release。APK はどちらもデバッグ署名（配布用の署名は段階D）。</summary>
    [JsonPropertyName("build_type")]
    public BuildType BuildType { get; set; } = BuildType.Release;

    /// <summary>APK に詰める ABI。</summary>
    [JsonPropertyName("arch")]
    public AndroidArch Arch { get; set; } = AndroidArch.Arm64V8a;
}

/// <summary>iOS パッケージング設定。</summary>
public class IosSettings
{
    [JsonPropertyName("output_path")]
    public string OutputPath { get; set; } = "";

    [JsonPropertyName("build_type")]
    public BuildType BuildType { get; set; } = BuildType.Release;
}

// ─── ルートデータ ─────────────────────────────────────────────

/// <summary>パッケージング設定全体のルートデータ。</summary>
public class PackagingData
{
    /// <summary>
    /// 設定ファイルの名前（アセットルート直下に置く）。
    /// パッケージ化ウィンドウと SeedPak ツール（editor/tools/SeedPak）が同じファイルを読む。
    /// エディタ専用の設定なので PAK には入れない（<see cref="Collect.PackagingRules.NeverIncludedRelativePaths"/>）。
    /// </summary>
    public const string SettingsFileName = "packaging_settings.json";

    /// <summary>
    /// ゲームの名前。出力フォルダ名・実行ファイル名に使用される。
    /// 空の場合は project_settings.json の game_name を参照する。
    /// </summary>
    [JsonPropertyName("game_name")]
    public string GameName { get; set; } = "";

    [JsonPropertyName("windows")]
    public WindowsSettings Windows { get; set; } = new();

    [JsonPropertyName("macos")]
    public MacOsSettings MacOs { get; set; } = new();

    [JsonPropertyName("android")]
    public AndroidSettings Android { get; set; } = new();

    [JsonPropertyName("ios")]
    public IosSettings Ios { get; set; } = new();

    /// <summary>
    /// アセット収録ルール（参照グラフによる絞り込みと除外設定）。
    /// 全プラットフォーム共通で、PAK に何を入れるかを決める。
    /// </summary>
    [JsonPropertyName("assets")]
    public Collect.AssetPackagingSettings Assets { get; set; } = new();

    /// <summary>
    /// .NET ランタイムを配布物へ同梱するか（self-contained 配布）。
    ///
    /// <para>
    /// 既定は true。OFF にすると出力サイズは約 75 MB 小さくなるが、
    /// 配布先の PC に .NET のインストールが必要になり、
    /// 未インストールだと **スクリプト無しでゲームが起動する**（ほぼ何も動かない）。
    /// 既定を ON にしているのは、この失敗が配布先でしか再現せず気付きにくいため。
    /// </para>
    /// </summary>
    [JsonPropertyName("bundle_dotnet_runtime")]
    public bool BundleDotnetRuntime { get; set; } = true;

    // ── 永続化 ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>JSON ファイルからロードする。存在しない場合はデフォルト値を返す。</summary>
    public static PackagingData LoadFrom(string path)
    {
        if (!File.Exists(path)) return new PackagingData();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PackagingData>(json, SerializeOptions)
                   ?? new PackagingData();
        }
        catch { return new PackagingData(); }
    }

    /// <summary>JSON ファイルへ保存する。</summary>
    public void SaveTo(string path)
    {
        var json = JsonSerializer.Serialize(this, SerializeOptions);
        File.WriteAllText(path, json);
    }
}

// ─── プラットフォームメタ情報 ─────────────────────────────────

/// <summary>プラットフォーム表示情報（UI 構築用）。</summary>
public record PlatformInfo(
    TargetPlatform Platform,
    string         DisplayName,
    /// <summary>Icons.xaml のアイコンキー（例 "Icon.Platform.Windows"）。絵文字ではない。</summary>
    string         IconKey,
    PlatformAvailability Availability,
    string         Note = "");

/// <summary>現在の環境でビルドが可能かどうかの区分。</summary>
public enum PlatformAvailability
{
    /// <summary>このマシンから直接ビルド可能。</summary>
    Available,
    /// <summary>追加セットアップが必要（NDK など）。</summary>
    RequiresSetup,
    /// <summary>別の OS でのみビルド可能（macOS / iOS はすべて macOS 上が必要）。</summary>
    RequiresOtherOS,
    /// <summary>ライセンス契約が必要（PS5 / Switch）。</summary>
    RequiresLicense,
}
