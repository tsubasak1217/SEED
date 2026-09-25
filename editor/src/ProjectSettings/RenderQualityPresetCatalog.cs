// ============================================================
//  RenderQualityPresetCatalog.cs — 描画品質プリセットの一覧（runtime/config/render_presets.json を読む）
//
//  【役割】
//  プロジェクト設定「レンダリング品質」のプリセットの選択肢（名前・表示名・説明・つまみ）を出す。
//  定義ファイルはランタイムと同じ runtime/config/render_presets.json を、エディタのビルドで埋め込みリソースとして
//  取り込む（SEEDEditor.csproj の EmbeddedResource。論理名 <see cref="ResourceName"/>）。ランタイムも同じファイルを
//  ビルド時に埋め込むので、エディタに出る一覧とランタイムが使う中身は食い違わない。
//
//  【壊れていたら】
//  読めなければ空の一覧を返す（画面は「既定」だけを出す）。書式はランタイムの単体テストと
//  ProjectSystemTests が確かめる。
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace SEEDEditor.ProjectSettings;

/// <summary>描画品質プリセット 1 つ（定義ファイルの 1 要素）。</summary>
/// <param name="Name">名前（設定に書くもの。例 mobile）。</param>
/// <param name="Label">表示名。</param>
/// <param name="Description">説明。</param>
/// <param name="Knobs">つまみ（キー → 値。書かれていないつまみは「設定のまま」）。</param>
public sealed record RenderQualityPreset(
    string Name,
    string Label,
    string Description,
    IReadOnlyDictionary<string, JsonElement> Knobs);

/// <summary>描画品質プリセットの一覧（埋め込みの runtime/config/render_presets.json）。</summary>
public static class RenderQualityPresetCatalog
{
    /// <summary>埋め込みリソースの論理名（SEEDEditor.csproj・テストの csproj と一致させる）。</summary>
    public const string ResourceName = "SEEDEditor.render_presets.json";

    /// <summary>このエディタが読めるプリセット定義の書式の版（ランタイムの PRESET_FORMAT_VERSION と一致）。</summary>
    public const int FormatVersion = 1;

    /// <summary>書式の版の欄。</summary>
    private const string FormatVersionKey = "format_version";

    /// <summary>プリセットの配列の欄。</summary>
    private const string PresetsKey = "presets";

    /// <summary>各欄の名前（ランタイムの catalog.rs と一致）。</summary>
    private const string NameKey = "name";
    private const string LabelKey = "label";
    private const string DescriptionKey = "description";
    private const string KnobsKey = "knobs";

    /// <summary>一覧（初めて使うときに 1 回だけ読む）。</summary>
    private static readonly Lazy<IReadOnlyList<RenderQualityPreset>> BuiltIn = new(LoadBuiltIn);

    /// <summary>埋め込みのプリセット一覧（定義順。読めなければ空）。</summary>
    public static IReadOnlyList<RenderQualityPreset> Presets => BuiltIn.Value;

    /// <summary>名前でプリセットを引く（前後の空白・大文字小文字は区別しない。無ければ null）。</summary>
    /// <param name="name">プリセット名。</param>
    /// <returns>プリセット。</returns>
    public static RenderQualityPreset? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Presets.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>プリセット定義の JSON を読む（書式が違えば空の一覧）。</summary>
    /// <param name="json">定義ファイルの中身。</param>
    /// <returns>プリセット一覧。</returns>
    public static IReadOnlyList<RenderQualityPreset> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(FormatVersionKey, out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != FormatVersion
                || !root.TryGetProperty(PresetsKey, out var presets)
                || presets.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<RenderQualityPreset>();
            }

            var list = new List<RenderQualityPreset>();
            foreach (var entry in presets.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var name = ReadText(entry, NameKey);
                if (string.IsNullOrWhiteSpace(name) || list.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue; // 名前が無い・重複（ランタイムと同じく読み飛ばす）
                }
                var knobs = new Dictionary<string, JsonElement>();
                if (entry.TryGetProperty(KnobsKey, out var knobObject) && knobObject.ValueKind == JsonValueKind.Object)
                {
                    foreach (var knob in knobObject.EnumerateObject())
                    {
                        knobs[knob.Name] = knob.Value.Clone();
                    }
                }
                list.Add(new RenderQualityPreset(name.Trim(), ReadText(entry, LabelKey), ReadText(entry, DescriptionKey), knobs));
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<RenderQualityPreset>();
        }
    }

    /// <summary>
    /// プリセットのつまみを人が読む短い文にする（画面の説明用。例「描画スケール 0.5・前方描画・影 1024」）。
    /// つまみが無ければ「何も下げない」。
    /// </summary>
    /// <param name="knobs">つまみ。</param>
    /// <returns>説明。</returns>
    public static string DescribeKnobs(IReadOnlyDictionary<string, JsonElement> knobs)
    {
        if (knobs.Count == 0) return "何も下げない（プロジェクト・シーンの設定のまま）";
        var parts = new List<string>();
        foreach (var (key, value) in knobs)
        {
            parts.Add(DescribeKnob(key, value));
        }
        return string.Join("・", parts);
    }

    /// <summary>つまみ 1 つを人が読む短い文にする。知らないキーは「キー=値」。</summary>
    /// <param name="key">キー。</param>
    /// <param name="value">値。</param>
    /// <returns>説明。</returns>
    private static string DescribeKnob(string key, JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        var isFalse = value.ValueKind == JsonValueKind.False;
        return key switch
        {
            RenderQualityPlatformSettings.RenderScaleKey => $"描画スケール {FormatNumber(value)}",
            RenderQualityPlatformSettings.ShadowsKey => isFalse ? "影なし" : "影あり",
            "shadow" => $"影の方式の上限 {raw}",
            "shadow_resolution" => $"影の解像度 {raw} まで",
            "shadow_distance" => $"影の距離 {FormatNumber(value)} m まで",
            "shadow_pcf_taps" => $"影のぼかし {raw} タップまで",
            "gi" => raw == "flat" ? "GI なし" : $"GI {raw} まで",
            "ao" => raw == "off" ? "AO なし" : $"AO {raw} まで",
            "reflection" => raw == "off" ? "反射なし" : $"反射 {raw} まで",
            "translucency" => raw == "raster" ? "RT 半透明なし" : $"半透明 {raw} まで",
            "deferred" => isFalse ? "前方描画（G-Buffer なし）" : "デファード可",
            "bloom" => isFalse ? "ブルームなし" : "ブルーム可",
            "fxaa" => isFalse ? "FXAA なし" : "FXAA 可",
            "vignette" => isFalse ? "ビネットなし" : "ビネット可",
            "water_reflection" => isFalse ? "水面反射なし" : "水面反射可",
            "water_caustics" => isFalse ? "コースティクスなし" : "コースティクス可",
            "target_fps" => $"{raw} fps まで",
            _ => $"{key}={raw}",
        };
    }

    /// <summary>数を短く書く（整数なら小数点なし）。</summary>
    /// <param name="value">値。</param>
    /// <returns>文字列。</returns>
    private static string FormatNumber(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number.ToString("0.##", CultureInfo.InvariantCulture)
            : value.GetRawText();

    /// <summary>文字列の欄を読む（無い・文字列でなければ空）。</summary>
    /// <param name="entry">オブジェクト。</param>
    /// <param name="key">欄。</param>
    /// <returns>文字列。</returns>
    private static string ReadText(JsonElement entry, string key) =>
        entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>埋め込みリソースから一覧を読む（無い・読めなければ空）。</summary>
    /// <returns>プリセット一覧。</returns>
    private static IReadOnlyList<RenderQualityPreset> LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return Array.Empty<RenderQualityPreset>();
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
