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

/// <summary>Android ビルドのアーキテクチャ。</summary>
public enum AndroidArch
{
    Arm64V8a,
    X86_64,
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

/// <summary>Android パッケージング設定。</summary>
public class AndroidSettings
{
    [JsonPropertyName("output_path")]
    public string OutputPath { get; set; } = "";

    [JsonPropertyName("build_type")]
    public BuildType BuildType { get; set; } = BuildType.Release;

    [JsonPropertyName("arch")]
    public AndroidArch Arch { get; set; } = AndroidArch.Arm64V8a;

    /// <summary>Android NDK のルートパス。</summary>
    [JsonPropertyName("ndk_path")]
    public string NdkPath { get; set; } = "";
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
