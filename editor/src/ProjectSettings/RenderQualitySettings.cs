// ============================================================
//  RenderQualitySettings.cs — project_settings.json の "render_quality" 節（描画品質プリセット。Android 段階D-2）
//
//  【役割】
//  端末の性能に合わせて描画を軽くする「描画品質プリセット」を、プラットフォームごとに選ぶ設定を持つ。
//    "render_quality": {
//      "desktop": { "preset": "desktop" },
//      "android": { "preset": "mobile", "render_scale": 0.75, "shadows": false }
//    }
//  各プラットフォームの節は「プリセット名（preset）」と「つまみの上書き」。どちらも省略でき、省略したものは
//  ランタイムの既定（プラットフォームの既定プリセット＝デスクトップ desktop／Android mobile、そのプリセットのつまみ）になる。
//  プリセットの中身は runtime/config/render_presets.json（RenderQualityPresetCatalog がエディタへ埋め込んだものを読む）。
//  決め方の正典はランタイムの runtime/src/engine/core/renderer/quality/resolve.rs（ここのキー名・値域と一致させる）。
//
//  【このクラスが型で持つつまみ】
//  エディタの画面で編集する主要なもの（render_scale・shadows）だけ。それ以外のつまみ（gi・deferred・bloom 等）と
//  知らないプラットフォームの節は ExtraData にそのまま保つ（保存で失わない。手で書いた値もランタイムは読む）。
//
//  【読み方を寛容にしている理由】
//  AndroidAppSettings と同じ。型の違う値 1 つで ProjectSettingsData 全体の読み込みが失敗すると、設定ウィンドウが
//  既定値で開いて保存で全体を上書きしてしまうため、この節は専用の変換器で読み、読めない値は「未設定」扱いにする。
//
//  WPF に依存しない（単体テスト・コンソールツールからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// project_settings.json の "render_quality" 節（プラットフォームごとの描画品質の選び方）。
/// </summary>
[JsonConverter(typeof(RenderQualitySettingsJsonConverter))]
public sealed class RenderQualitySettings
{
    /// <summary>project_settings.json の中での節のキー（ランタイムの RENDER_QUALITY_KEY と一致）。</summary>
    public const string SectionKey = "render_quality";

    /// <summary>デスクトップ（Windows）の節の名前（ランタイムの PlatformTraits::quality_platform_key と一致）。</summary>
    public const string DesktopKey = "desktop";

    /// <summary>Android の節の名前（ランタイムの PlatformTraits::quality_platform_key と一致）。</summary>
    public const string AndroidKey = "android";

    /// <summary>デスクトップの既定のプリセット名（ランタイムの DESKTOP_DEFAULT_RENDER_QUALITY と一致）。</summary>
    public const string DesktopDefaultPreset = "desktop";

    /// <summary>Android の既定のプリセット名（ランタイムの ANDROID_DEFAULT_RENDER_QUALITY と一致）。</summary>
    public const string AndroidDefaultPreset = "mobile";

    /// <summary>デスクトップの節（null ＝ 何も指定しない＝既定）。</summary>
    public RenderQualityPlatformSettings? Desktop { get; set; }

    /// <summary>Android の節（null ＝ 何も指定しない＝既定）。</summary>
    public RenderQualityPlatformSettings? Android { get; set; }

    /// <summary>このクラスが知らない節（新しいプラットフォーム等）。保存で失わないために持つ。</summary>
    public Dictionary<string, JsonElement> ExtraData { get; set; } = new();

    /// <summary>何も設定されていないか（節ごと省略して保存してよいか）。</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        (Desktop?.IsEmpty ?? true) && (Android?.IsEmpty ?? true) && ExtraData.Count == 0;

    /// <summary>プラットフォームの既定のプリセット名（知らないプラットフォームはデスクトップと同じ）。</summary>
    /// <param name="platformKey">節の名前（<see cref="DesktopKey"/> / <see cref="AndroidKey"/>）。</param>
    /// <returns>プリセット名。</returns>
    public static string DefaultPresetFor(string platformKey) =>
        platformKey == AndroidKey ? AndroidDefaultPreset : DesktopDefaultPreset;

    /// <summary>プラットフォームの節を返す（無ければ null）。</summary>
    /// <param name="platformKey">節の名前。</param>
    /// <returns>節。</returns>
    public RenderQualityPlatformSettings? Get(string platformKey) => platformKey switch
    {
        DesktopKey => Desktop,
        AndroidKey => Android,
        _ => null,
    };

    /// <summary>プラットフォームの節を差し替える（空なら null にして節から消す）。</summary>
    /// <param name="platformKey">節の名前（<see cref="DesktopKey"/> / <see cref="AndroidKey"/>）。</param>
    /// <param name="settings">新しい節。</param>
    public void Set(string platformKey, RenderQualityPlatformSettings? settings)
    {
        var value = settings is { IsEmpty: false } ? settings : null;
        switch (platformKey)
        {
            case DesktopKey: Desktop = value; break;
            case AndroidKey: Android = value; break;
            default: throw new ArgumentException($"知らないプラットフォームの節です: {platformKey}", nameof(platformKey));
        }
        // 型の違う値で読んで ExtraData に保っていた同じ名前の節は、型付きの節で置き換える
        // （残すと保存で同じキーが 2 回書かれる）。
        if (value is not null) ExtraData.Remove(platformKey);
    }
}

/// <summary>
/// "render_quality.&lt;プラットフォーム&gt;" の節。すべて省略可（null ＝ プリセット・既定のまま）。
/// </summary>
public sealed class RenderQualityPlatformSettings
{
    /// <summary>プリセット名の欄（ランタイムの PRESET_KEY と一致）。</summary>
    public const string PresetKey = "preset";

    /// <summary>描画スケールのつまみのキー（ランタイムの KEY_RENDER_SCALE と一致）。</summary>
    public const string RenderScaleKey = "render_scale";

    /// <summary>影の有無のつまみのキー（ランタイムの KEY_SHADOWS と一致）。</summary>
    public const string ShadowsKey = "shadows";

    /// <summary>描画スケールの下限（ランタイムの MIN_RENDER_SCALE と一致）。</summary>
    public const double MinRenderScale = 0.5;

    /// <summary>描画スケールの上限（ランタイムの MAX_RENDER_SCALE と一致。等倍）。</summary>
    public const double MaxRenderScale = 1.0;

    /// <summary>プリセット名（null ＝ プラットフォームの既定）。</summary>
    public string? Preset { get; set; }

    /// <summary>描画スケールの上書き（null ＝ プリセットのまま。0.5〜1.0）。</summary>
    public double? RenderScale { get; set; }

    /// <summary>影の有無の上書き（null ＝ プリセットのまま。false で影を描かない）。</summary>
    public bool? Shadows { get; set; }

    /// <summary>このクラスが型で持たないつまみ（gi・deferred 等）と知らないキー。保存で失わないために持つ。</summary>
    public Dictionary<string, JsonElement> ExtraData { get; set; } = new();

    /// <summary>何も設定されていないか。</summary>
    [JsonIgnore]
    public bool IsEmpty => Preset is null && RenderScale is null && Shadows is null && ExtraData.Count == 0;

    /// <summary>描画スケールを値域へ収める（NaN・無限大は null＝プリセットのまま）。</summary>
    /// <param name="scale">入力された値。</param>
    /// <returns>収めた値。</returns>
    public static double? ClampRenderScale(double? scale) =>
        scale is double value && double.IsFinite(value) ? Math.Clamp(value, MinRenderScale, MaxRenderScale) : null;

    /// <summary>プリセット名を正規化する（前後の空白を落とし、空なら null＝既定）。</summary>
    /// <param name="name">入力された名前。</param>
    /// <returns>正規化した名前。</returns>
    public static string? NormalizePreset(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}

/// <summary>
/// <see cref="RenderQualitySettings"/> の JSON 変換（型の違う値は未設定として読む。知らないキーは保つ）。
/// </summary>
internal sealed class RenderQualitySettingsJsonConverter : JsonConverter<RenderQualitySettings>
{
    /// <summary>節を読む。オブジェクトでなければ空の設定にする（読み込み全体を失敗させない）。</summary>
    /// <param name="reader">JSON の読み手。</param>
    /// <param name="typeToConvert">変換先の型。</param>
    /// <param name="options">シリアライズ設定。</param>
    /// <returns>読んだ設定。</returns>
    public override RenderQualitySettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var settings = new RenderQualitySettings();
        if (document.RootElement.ValueKind != JsonValueKind.Object) return settings;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case RenderQualitySettings.DesktopKey when property.Value.ValueKind == JsonValueKind.Object:
                    settings.Desktop = ReadPlatform(property.Value);
                    break;
                case RenderQualitySettings.AndroidKey when property.Value.ValueKind == JsonValueKind.Object:
                    settings.Android = ReadPlatform(property.Value);
                    break;
                default:
                    // 知らない節・型の違う節はそのまま保つ（ランタイムは型の違う節を警告して無視する）
                    settings.ExtraData[property.Name] = property.Value.Clone();
                    break;
            }
        }
        return settings;
    }

    /// <summary>節を書く（設定されている値と、知らないキーだけ）。</summary>
    /// <param name="writer">JSON の書き手。</param>
    /// <param name="value">書く設定。</param>
    /// <param name="options">シリアライズ設定。</param>
    public override void Write(Utf8JsonWriter writer, RenderQualitySettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        var desktopWritten = WritePlatformIfSet(writer, RenderQualitySettings.DesktopKey, value.Desktop);
        var androidWritten = WritePlatformIfSet(writer, RenderQualitySettings.AndroidKey, value.Android);
        foreach (var (key, element) in value.ExtraData)
        {
            // 型付きの節を書いた名前は、保っていた同じ名前の値を書かない（同じキーを 2 回書かない）
            if ((desktopWritten && key == RenderQualitySettings.DesktopKey)
                || (androidWritten && key == RenderQualitySettings.AndroidKey))
            {
                continue;
            }
            writer.WritePropertyName(key);
            element.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    /// <summary>プラットフォームの節を読む（読めない値は未設定。知らないキーは保つ）。</summary>
    /// <param name="element">節のオブジェクト。</param>
    /// <returns>読んだ節。</returns>
    private static RenderQualityPlatformSettings ReadPlatform(JsonElement element)
    {
        var platform = new RenderQualityPlatformSettings();
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case RenderQualityPlatformSettings.PresetKey:
                    platform.Preset = property.Value.ValueKind == JsonValueKind.String
                        ? RenderQualityPlatformSettings.NormalizePreset(property.Value.GetString())
                        : null;
                    break;
                case RenderQualityPlatformSettings.RenderScaleKey:
                    platform.RenderScale = RenderQualityPlatformSettings.ClampRenderScale(ReadNumber(property.Value));
                    break;
                case RenderQualityPlatformSettings.ShadowsKey:
                    platform.Shadows = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
                    break;
                default:
                    platform.ExtraData[property.Name] = property.Value.Clone();
                    break;
            }
        }
        return platform;
    }

    /// <summary>数を読む（数か、数として読める文字列。それ以外は null）。</summary>
    /// <param name="element">値。</param>
    /// <returns>数。</returns>
    private static double? ReadNumber(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number)) return number;
        if (element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>空でないプラットフォームの節だけを書く。</summary>
    /// <param name="writer">JSON の書き手。</param>
    /// <param name="key">節の名前。</param>
    /// <param name="platform">節。</param>
    /// <returns>書いたか。</returns>
    private static bool WritePlatformIfSet(Utf8JsonWriter writer, string key, RenderQualityPlatformSettings? platform)
    {
        if (platform is null || platform.IsEmpty) return false;
        writer.WritePropertyName(key);
        writer.WriteStartObject();
        var preset = RenderQualityPlatformSettings.NormalizePreset(platform.Preset);
        if (preset is not null) writer.WriteString(RenderQualityPlatformSettings.PresetKey, preset);
        if (RenderQualityPlatformSettings.ClampRenderScale(platform.RenderScale) is double scale)
        {
            writer.WriteNumber(RenderQualityPlatformSettings.RenderScaleKey, scale);
        }
        if (platform.Shadows is bool shadows) writer.WriteBoolean(RenderQualityPlatformSettings.ShadowsKey, shadows);
        foreach (var (extraKey, element) in platform.ExtraData)
        {
            writer.WritePropertyName(extraKey);
            element.WriteTo(writer);
        }
        writer.WriteEndObject();
        return true;
    }
}
