// ============================================================
//  FontFieldSettings.cs — project_settings.json の "font" 節（キャンバスの文字の距離場の種類と辺の色分け）
//
//  【役割】
//    "font": { "distance_field": "sdf", "msdf_coloring": "simple" }
//  どちらも省略でき、省略したものはランタイムの既定（mtsdf・ink_trap）になる。値の表と読み方は FontFieldCatalog、
//  決め方の正典はランタイムの runtime/src/engine/core/font/field_settings.rs（resolve_font_field_settings）。
//
//  【書き方】
//    - 画面で既定値（mtsdf・ink_trap）を選んだらキーを書かない（ランタイムの既定が変わったときに追従できるように）。
//      手で書いた既定値（"distance_field": "mtsdf"）は、画面でその欄を選び直さない限りそのまま残す
//    - 何も無ければ節ごと書かない（ProjectSettingsData.SaveTo）
//    - 知らないキー・ランタイムが読めない値（"bitmap" など）は ExtraData にそのまま保つ（保存で失わない。
//      ランタイムは警告して既定で動く）
//    - 節がオブジェクトでない（"font": "sdf" など）ときは、画面で何も選ばない限りそのまま書き戻す
//
//  WPF に依存しない（単体テスト・コンソールツールからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// project_settings.json の "font" 節（キャンバスの文字の距離場の設定）。
/// </summary>
[JsonConverter(typeof(FontFieldSettingsJsonConverter))]
public sealed class FontFieldSettings
{
    /// <summary>project_settings.json の中での節のキー（ランタイムの FONT_KEY と一致）。</summary>
    public const string SectionKey = "font";

    /// <summary>距離場の種類のキー（ランタイムの DISTANCE_FIELD_KEY と一致）。</summary>
    public const string DistanceFieldKey = "distance_field";

    /// <summary>MSDF の辺の色分けのキー（ランタイムの COLORING_KEY と一致）。</summary>
    public const string ColoringKey = "msdf_coloring";

    /// <summary>距離場の種類（null ＝ 既定＝キーを書かない。値は正規化した綴り）。</summary>
    public string? DistanceField { get; private set; }

    /// <summary>辺の色分け（null ＝ 既定＝キーを書かない。値は正規化した綴り）。</summary>
    public string? MsdfColoring { get; private set; }

    /// <summary>このクラスが型で持たないキー・ランタイムが読めない値。保存で失わないために持つ。</summary>
    public Dictionary<string, JsonElement> ExtraData { get; } = new(StringComparer.Ordinal);

    /// <summary>節がオブジェクトでなかったときの元の値（null ＝ オブジェクトだった）。</summary>
    public JsonElement? UnreadableSection { get; private set; }

    /// <summary>実効の距離場の種類（書かれていなければ既定）。</summary>
    [JsonIgnore]
    public string EffectiveDistanceField => DistanceField ?? FontFieldCatalog.DefaultDistanceField;

    /// <summary>実効の辺の色分け（書かれていなければ既定）。</summary>
    [JsonIgnore]
    public string EffectiveColoring => MsdfColoring ?? FontFieldCatalog.DefaultColoring;

    /// <summary>型で持つ指定が何も無いか。</summary>
    [JsonIgnore]
    public bool HasNoTypedValues => DistanceField is null && MsdfColoring is null;

    /// <summary>何も設定されていないか（節ごと省略して保存してよいか）。</summary>
    [JsonIgnore]
    public bool IsEmpty => HasNoTypedValues && ExtraData.Count == 0 && UnreadableSection is null;

    /// <summary>
    /// 距離場の種類を選ぶ（既定値・知らない値は null ＝ キーを書かない）。
    /// 同じキーの読めなかった値・オブジェクトでなかった節は置き換える。
    /// </summary>
    /// <param name="value">値（<see cref="FontFieldCatalog.DistanceFieldChoices"/> の値）。</param>
    public void SetDistanceField(string? value)
    {
        var parsed = FontFieldCatalog.ParseDistanceField(value);
        DistanceField = parsed == FontFieldCatalog.DefaultDistanceField ? null : parsed;
        ExtraData.Remove(DistanceFieldKey);
        UnreadableSection = null;
    }

    /// <summary>
    /// 辺の色分けを選ぶ（既定値・知らない値は null ＝ キーを書かない）。
    /// 同じキーの読めなかった値・オブジェクトでなかった節は置き換える。
    /// </summary>
    /// <param name="value">値（<see cref="FontFieldCatalog.ColoringChoices"/> の値）。</param>
    public void SetColoring(string? value)
    {
        var parsed = FontFieldCatalog.ParseColoring(value);
        MsdfColoring = parsed == FontFieldCatalog.DefaultColoring ? null : parsed;
        ExtraData.Remove(ColoringKey);
        UnreadableSection = null;
    }

    /// <summary>JSON の 1 つの欄を読み込む（変換器から呼ぶ。読めない値は ExtraData へ）。</summary>
    /// <param name="key">キー。</param>
    /// <param name="value">値。</param>
    internal void ReadProperty(string key, JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (key == DistanceFieldKey && FontFieldCatalog.ParseDistanceField(text) is { } kind)
        {
            // 手で書いた既定値もそのまま持つ（画面で選び直したときだけ既定値を省く）
            DistanceField = kind;
            return;
        }
        if (key == ColoringKey && FontFieldCatalog.ParseColoring(text) is { } coloring)
        {
            MsdfColoring = coloring;
            return;
        }
        // 知らないキー・読めない値はそのまま保つ（ランタイムは警告して既定で動く）
        ExtraData[key] = value.Clone();
    }

    /// <summary>節がオブジェクトでなかったことを記録する（変換器から呼ぶ）。</summary>
    /// <param name="value">元の値。</param>
    internal void MarkUnreadable(JsonElement value) => UnreadableSection = value.Clone();
}

/// <summary>
/// <see cref="FontFieldSettings"/> の JSON 変換（読めない値は未設定として読む。知らないキーは保つ）。
/// </summary>
internal sealed class FontFieldSettingsJsonConverter : JsonConverter<FontFieldSettings>
{
    /// <summary>節を読む。オブジェクトでなければ元の値を覚えた空の設定にする（読み込み全体を失敗させない）。</summary>
    /// <param name="reader">JSON の読み手。</param>
    /// <param name="typeToConvert">変換先の型。</param>
    /// <param name="options">シリアライズ設定。</param>
    /// <returns>読んだ設定。</returns>
    public override FontFieldSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var settings = new FontFieldSettings();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            settings.MarkUnreadable(document.RootElement);
            return settings;
        }
        foreach (var property in document.RootElement.EnumerateObject())
        {
            settings.ReadProperty(property.Name, property.Value);
        }
        return settings;
    }

    /// <summary>節を書く（distance_field → msdf_coloring → 知らないキーの順。指定の無いものは書かない）。</summary>
    /// <param name="writer">JSON の書き手。</param>
    /// <param name="value">書く設定。</param>
    /// <param name="options">シリアライズ設定。</param>
    public override void Write(Utf8JsonWriter writer, FontFieldSettings value, JsonSerializerOptions options)
    {
        // オブジェクトでなかった節は、画面で何も選んでいなければそのまま書き戻す
        if (value.UnreadableSection is JsonElement raw && value.HasNoTypedValues && value.ExtraData.Count == 0)
        {
            raw.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        if (value.DistanceField is { } kind) writer.WriteString(FontFieldSettings.DistanceFieldKey, kind);
        if (value.MsdfColoring is { } coloring) writer.WriteString(FontFieldSettings.ColoringKey, coloring);
        foreach (var (key, element) in value.ExtraData)
        {
            // 型で書いたキーは、保っていた同じキーの値を書かない（同じキーを 2 回書かない）
            if ((key == FontFieldSettings.DistanceFieldKey && value.DistanceField is not null)
                || (key == FontFieldSettings.ColoringKey && value.MsdfColoring is not null))
            {
                continue;
            }
            writer.WritePropertyName(key);
            element.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
