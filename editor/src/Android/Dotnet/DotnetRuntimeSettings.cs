// ============================================================
//  DotnetRuntimeSettings.cs — runtime/android/dotnet_runtime.json（APK に同梱する .NET の設定）の読み取り
//
//  【設定の中身（唯一の置き場。docs/android.md §17.2）】
//    dotnet_runtime        … 使う種類（coreclr / mono。runtimes の中の同名の設定を使う）
//    version / framework / target_framework … パックの版・共有フレームワーク名・パックの中の TFM
//    abis                  … Android の ABI → パック名の {arch}
//    runtimes.<種類>       … runtime_pack / runtime_rid / host_pack / host_rid / excluded_native_files / java_libraries
//    host_libraries        … hostfxr・hostpolicy の dotnet-root 内の置き場（ひな形）
//    native_library_mode   … .so を dotnet-root へ置く方法（symlink / copy）
//    runtime_properties    … CLR の起動前に設定するプロパティ
//  ひな形の {arch} {version} {framework} は ABI ごとの値で埋める。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Android.Dotnet;

/// <summary>1 つの種類（coreclr / mono）のパックの設定。</summary>
/// <param name="RuntimePack">BCL・ランタイムの .so の出どころのパック名（ひな形）。</param>
/// <param name="RuntimeRid">そのパックの中の RID（ひな形）。</param>
/// <param name="HostPack">hostfxr・hostpolicy の出どころのパック名（ひな形）。</param>
/// <param name="HostRid">そのパックの中の RID（ひな形）。</param>
/// <param name="ExcludedNativeFiles">入れない .so（デバッガ用など）。</param>
/// <param name="JavaLibraries">APK の Java クラスへ入れる .jar（パックの native/ にある）。</param>
public sealed record DotnetRuntimeKindSettings(
    string RuntimePack,
    string RuntimeRid,
    string HostPack,
    string HostRid,
    IReadOnlyList<string> ExcludedNativeFiles,
    IReadOnlyList<string> JavaLibraries);

/// <summary>dotnet_runtime.json の中身。</summary>
/// <param name="Text">ファイルの文字列そのまま（中身の識別子 content_id の材料）。</param>
/// <param name="Kind">使う種類（小文字。coreclr / mono）。</param>
/// <param name="Version">ランタイムの版（10.0.12）。</param>
/// <param name="Framework">共有フレームワーク名（Microsoft.NETCore.App）。</param>
/// <param name="TargetFramework">パックの中の TFM（net10.0）。</param>
/// <param name="AbiArchitectures">Android の ABI → パック名の {arch}。</param>
/// <param name="Runtime">使う種類のパックの設定。</param>
/// <param name="HostLibraries">hostfxr・hostpolicy のファイル名 → dotnet-root 内の置き場のひな形（書かれた順）。</param>
/// <param name="NativeLibraryMode">.so を dotnet-root へ置く方法（symlink / copy）。</param>
/// <param name="RuntimeProperties">CLR の起動前に設定するプロパティ（書かれた順）。</param>
public sealed record DotnetRuntimeSettings(
    string Text,
    string Kind,
    string Version,
    string Framework,
    string TargetFramework,
    IReadOnlyDictionary<string, string> AbiArchitectures,
    DotnetRuntimeKindSettings Runtime,
    IReadOnlyList<KeyValuePair<string, string>> HostLibraries,
    string NativeLibraryMode,
    IReadOnlyList<KeyValuePair<string, string>> RuntimeProperties)
{
    /// <summary>
    /// 設定ファイルを読む。
    /// </summary>
    /// <param name="path">dotnet_runtime.json のパス。</param>
    /// <returns>設定。</returns>
    /// <exception cref="AndroidPipelineException">無い・読めない・dotnet_runtime が runtimes に無いとき。</exception>
    public static DotnetRuntimeSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"同梱 .NET の設定がありません: {path}");
        }
        var text = File.ReadAllText(path);
        try
        {
            return Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"同梱 .NET の設定を読めません（{path}）: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 設定の文字列を解釈する。
    /// </summary>
    /// <param name="text">dotnet_runtime.json の中身。</param>
    /// <returns>設定。</returns>
    public static DotnetRuntimeSettings Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        var kind = (root.GetProperty("dotnet_runtime").GetString() ?? string.Empty).Trim().ToLowerInvariant();
        var runtimes = root.GetProperty("runtimes");
        if (!runtimes.TryGetProperty(kind, out var runtimeElement))
        {
            var choices = string.Join(" / ", runtimes.EnumerateObject().Select(p => p.Name));
            throw new AndroidPipelineException(AndroidFailureKind.Build,
                $"dotnet_runtime.json の dotnet_runtime=\"{kind}\" は runtimes にありません（使える値: {choices}）。");
        }

        var runtime = new DotnetRuntimeKindSettings(
            RequiredString(runtimeElement, "runtime_pack"),
            RequiredString(runtimeElement, "runtime_rid"),
            RequiredString(runtimeElement, "host_pack"),
            RequiredString(runtimeElement, "host_rid"),
            OptionalStringArray(runtimeElement, "excluded_native_files"),
            OptionalStringArray(runtimeElement, "java_libraries"));

        return new DotnetRuntimeSettings(
            text,
            kind,
            RequiredString(root, "version"),
            RequiredString(root, "framework"),
            RequiredString(root, "target_framework"),
            root.GetProperty("abis").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty),
            runtime,
            StringPairs(root.GetProperty("host_libraries")),
            RequiredString(root, "native_library_mode"),
            root.TryGetProperty("runtime_properties", out var properties) ? StringPairs(properties) : Array.Empty<KeyValuePair<string, string>>());
    }

    /// <summary>
    /// ひな形（{arch} {version} {framework}）をその ABI の値で埋める。
    /// </summary>
    /// <param name="template">ひな形。</param>
    /// <param name="abi">ABI。</param>
    /// <returns>埋めた文字列。</returns>
    /// <exception cref="AndroidPipelineException">abis にその ABI が無いとき。</exception>
    public string Expand(string template, AndroidAbi abi)
    {
        if (!AbiArchitectures.TryGetValue(abi.Name, out var arch))
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"dotnet_runtime.json の abis に {abi.Name} がありません。");
        }
        return template.Replace("{arch}", arch).Replace("{version}", Version).Replace("{framework}", Framework);
    }

    /// <summary>その ABI の BCL・ランタイムのパック名。</summary>
    /// <param name="abi">ABI。</param>
    /// <returns>パック名。</returns>
    public string RuntimePackId(AndroidAbi abi) => Expand(Runtime.RuntimePack, abi);

    /// <summary>その ABI の hostfxr・hostpolicy のパック名。</summary>
    /// <param name="abi">ABI。</param>
    /// <returns>パック名。</returns>
    public string HostPackId(AndroidAbi abi) => Expand(Runtime.HostPack, abi);

    /// <summary>必須の文字列の値を読む。</summary>
    private static string RequiredString(JsonElement element, string key) =>
        element.GetProperty(key).GetString() ?? throw new FormatException($"{key} が文字列ではありません。");

    /// <summary>省略できる文字列の配列を読む（無ければ空）。</summary>
    private static IReadOnlyList<string> OptionalStringArray(JsonElement element, string key) =>
        element.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();

    /// <summary>オブジェクトを（キー, 文字列の値）の並びにする（書かれた順を保つ）。</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> StringPairs(JsonElement element) =>
        element.EnumerateObject()
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()))
            .ToArray();
}
