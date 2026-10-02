// ============================================================
//  RenderProfileSettings.cs — project_settings.json の "render" 節（描画の構成。render.profile と旗の上書き）
//
//  【役割】
//    "render": { "profile": "ui", "post": true, "memory_hint": "memory_usage" }
//  profile で構成（runtime/config/render_profiles.json の name）を選び、ほかのキーは構成の旗の個別の上書き。
//  どれも省略でき、省略したものはランタイムの既定（構成は default_profile＝full、旗は構成のとおり）になる。
//  決め方の正典はランタイムの runtime/src/engine/core/renderer/render_profile/resolve.rs と flags.rs
//  （キー名・値の読み方はここと一致させる。キーの表は RenderProfileFlagCatalog）。
//
//  【書き方】（Rust 側は節もキーも省略を既定として読むので、C# は「指定が無いものは書かない」）
//    - Profile が null（既定の構成）なら "profile" を書かない
//    - 旗は 3 状態: 上書きなし（構成のまま）＝キーを書かない / true / false
//    - 何も無ければ節ごと書かない（ProjectSettingsData.SaveTo）
//    - 知らないキー・ランタイムが読めない値（型の違う旗・知らない memory_hint）は ExtraData にそのまま保つ
//      （保存で失わない。ランタイムは警告して捨てる）
//    - 節がオブジェクトでない（"render": "ui" など）ときは、画面で何も選ばない限りそのまま書き戻す
//
//  【読み方を寛容にしている理由】
//  RenderQualitySettings と同じ。型の違う値 1 つで ProjectSettingsData 全体の読み込みが失敗すると、設定ウィンドウが
//  既定値で開いて保存で全体を上書きしてしまうため、この節は専用の変換器で読み、読めない値は「未設定」扱いにする。
//
//  WPF に依存しない（単体テスト・コンソールツールからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// project_settings.json の "render" 節（描画の構成の選び方と旗の上書き）。
/// </summary>
[JsonConverter(typeof(RenderProfileSettingsJsonConverter))]
public sealed class RenderProfileSettings
{
    /// <summary>project_settings.json の中での節のキー（ランタイムの RENDER_KEY と一致）。</summary>
    public const string SectionKey = "render";

    /// <summary>構成の名前のキー（ランタイムの PROFILE_KEY と一致）。</summary>
    public const string ProfileKey = "profile";

    /// <summary>真偽の旗の上書き（キー → 値。無いキーは「構成のまま」）。</summary>
    private readonly Dictionary<string, bool> _flagOverrides = new(StringComparer.Ordinal);

    /// <summary>
    /// 構成の名前（null ＝ 既定の構成＝キーを書かない）。読んだ文字列をそのまま持つ（前後の空白・大文字小文字は
    /// ランタイムも区別しないので、一覧と照合するときに吸収する）。
    /// </summary>
    public string? Profile { get; private set; }

    /// <summary>GPU メモリの確保の方針の上書き（null ＝ 構成のまま。値は正規化した綴り）。</summary>
    public string? MemoryHint { get; private set; }

    /// <summary>このクラスが型で持たないキー・ランタイムが読めない値。保存で失わないために持つ。</summary>
    public Dictionary<string, JsonElement> ExtraData { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 節がオブジェクトでなかったときの元の値（null ＝ オブジェクトだった）。画面で何か選ぶまではそのまま書き戻す
    /// （ランタイムは警告して節を無視する）。
    /// </summary>
    public JsonElement? UnreadableSection { get; private set; }

    /// <summary>上書きしている真偽の旗（キー → 値）。</summary>
    public IReadOnlyDictionary<string, bool> FlagOverrides => _flagOverrides;

    /// <summary>型で持つ指定が何も無いか（構成の名前・旗の上書き・memory_hint）。</summary>
    [JsonIgnore]
    public bool HasNoTypedValues => Profile is null && _flagOverrides.Count == 0 && MemoryHint is null;

    /// <summary>何も設定されていないか（節ごと省略して保存してよいか）。</summary>
    [JsonIgnore]
    public bool IsEmpty => HasNoTypedValues && ExtraData.Count == 0 && UnreadableSection is null;

    /// <summary>真偽の旗の上書きを返す（null ＝ 構成のまま）。</summary>
    /// <param name="key">旗のキー。</param>
    /// <returns>上書きの値。</returns>
    public bool? GetFlagOverride(string key) => _flagOverrides.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// 構成の名前を選ぶ（null・空白 ＝ 既定の構成＝キーを書かない）。
    /// 画面で選んだ値なので、同じキーの読めなかった値・オブジェクトでなかった節は置き換える。
    /// </summary>
    /// <param name="name">構成の名前（一覧の綴り）。</param>
    public void SetProfile(string? name)
    {
        Profile = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        ExtraData.Remove(ProfileKey);
        UnreadableSection = null;
    }

    /// <summary>
    /// 真偽の旗の上書きを選ぶ（null ＝ 構成のまま＝キーを書かない）。
    /// 同じキーの読めなかった値・オブジェクトでなかった節は置き換える。
    /// </summary>
    /// <param name="key">旗のキー（<see cref="RenderProfileFlagCatalog.ToggleFlags"/> にあるもの）。</param>
    /// <param name="value">値。</param>
    /// <exception cref="ArgumentException">表に無いキー。</exception>
    public void SetFlagOverride(string key, bool? value)
    {
        if (!RenderProfileFlagCatalog.IsToggleKey(key))
        {
            throw new ArgumentException($"知らない旗です: {key}", nameof(key));
        }
        if (value is bool flag) _flagOverrides[key] = flag;
        else _flagOverrides.Remove(key);
        ExtraData.Remove(key);
        UnreadableSection = null;
    }

    /// <summary>
    /// GPU メモリの確保の方針の上書きを選ぶ（null ＝ 構成のまま＝キーを書かない。知らない綴りも null）。
    /// 同じキーの読めなかった値・オブジェクトでなかった節は置き換える。
    /// </summary>
    /// <param name="value">値（<see cref="RenderProfileFlagCatalog.MemoryHintChoices"/> の値）。</param>
    public void SetMemoryHint(string? value)
    {
        MemoryHint = RenderProfileFlagCatalog.NormalizeMemoryHint(value);
        ExtraData.Remove(RenderProfileFlagCatalog.MemoryHintKey);
        UnreadableSection = null;
    }

    /// <summary>JSON の 1 つの欄を読み込む（変換器から呼ぶ。読めない値は ExtraData へ）。</summary>
    /// <param name="key">キー。</param>
    /// <param name="value">値。</param>
    internal void ReadProperty(string key, JsonElement value)
    {
        if (key == ProfileKey)
        {
            // 構成の名前は空でない文字列だけ（ランタイムは型の違い・知らない名前を警告して既定のまま）
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                Profile = value.GetString();
                return;
            }
        }
        else if (RenderProfileFlagCatalog.IsToggleKey(key))
        {
            if (RenderProfileFlagCatalog.ReadToggle(value) is bool flag)
            {
                _flagOverrides[key] = flag;
                return;
            }
        }
        else if (key == RenderProfileFlagCatalog.MemoryHintKey)
        {
            if (RenderProfileFlagCatalog.ReadMemoryHint(value) is { } hint)
            {
                MemoryHint = hint;
                return;
            }
        }
        // 知らないキー・読めない値はそのまま保つ（ランタイムは警告して捨てる）
        ExtraData[key] = value.Clone();
    }

    /// <summary>節がオブジェクトでなかったことを記録する（変換器から呼ぶ）。</summary>
    /// <param name="value">元の値。</param>
    internal void MarkUnreadable(JsonElement value) => UnreadableSection = value.Clone();
}

/// <summary>
/// <see cref="RenderProfileSettings"/> の JSON 変換（型の違う値は未設定として読む。知らないキーは保つ）。
/// </summary>
internal sealed class RenderProfileSettingsJsonConverter : JsonConverter<RenderProfileSettings>
{
    /// <summary>節を読む。オブジェクトでなければ元の値を覚えた空の設定にする（読み込み全体を失敗させない）。</summary>
    /// <param name="reader">JSON の読み手。</param>
    /// <param name="typeToConvert">変換先の型。</param>
    /// <param name="options">シリアライズ設定。</param>
    /// <returns>読んだ設定。</returns>
    public override RenderProfileSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var settings = new RenderProfileSettings();
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

    /// <summary>
    /// 節を書く（profile → 旗〈表の順〉→ memory_hint → 知らないキーの順。指定の無いものは書かない）。
    /// </summary>
    /// <param name="writer">JSON の書き手。</param>
    /// <param name="value">書く設定。</param>
    /// <param name="options">シリアライズ設定。</param>
    public override void Write(Utf8JsonWriter writer, RenderProfileSettings value, JsonSerializerOptions options)
    {
        // オブジェクトでなかった節は、画面で何も選んでいなければそのまま書き戻す
        if (value.UnreadableSection is JsonElement raw && value.HasNoTypedValues && value.ExtraData.Count == 0)
        {
            raw.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        if (value.Profile is { } profile) writer.WriteString(RenderProfileSettings.ProfileKey, profile);
        foreach (var flag in RenderProfileFlagCatalog.ToggleFlags)
        {
            if (value.GetFlagOverride(flag.Key) is bool enabled) writer.WriteBoolean(flag.Key, enabled);
        }
        if (value.MemoryHint is { } hint) writer.WriteString(RenderProfileFlagCatalog.MemoryHintKey, hint);
        foreach (var (key, element) in value.ExtraData)
        {
            // 型で書いたキーは、保っていた同じキーの値を書かない（同じキーを 2 回書かない）
            if (IsWrittenAsTyped(value, key)) continue;
            writer.WritePropertyName(key);
            element.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    /// <summary>キーを型の値として書いたか。</summary>
    /// <param name="value">設定。</param>
    /// <param name="key">キー。</param>
    /// <returns>書いたなら true。</returns>
    private static bool IsWrittenAsTyped(RenderProfileSettings value, string key) =>
        (key == RenderProfileSettings.ProfileKey && value.Profile is not null)
        || (key == RenderProfileFlagCatalog.MemoryHintKey && value.MemoryHint is not null)
        || value.GetFlagOverride(key) is not null;
}
