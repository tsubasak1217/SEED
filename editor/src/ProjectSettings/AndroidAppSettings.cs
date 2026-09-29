// ============================================================
//  AndroidAppSettings.cs — project_settings.json の "android" 節（Android アプリの識別情報）
//
//  【役割】
//  Android の APK に焼き込むアプリの識別情報を、プロジェクトのデータとして持つ（データドリブン）。
//    application_id … アプリ ID（パッケージ名。端末はこれで別のアプリかを見分ける）
//    app_name       … ランチャーに出る名前（マニフェストの android:label）
//    version_code   … 整数の版（大きいほど新しい。ストアの更新判定に使われる）
//    version_name   … 人が読む版の文字列
//    icon           … ランチャーのアイコンの元の PNG（アセットルートからの相対パスか絶対パス。段階D。
//                      ビルド時に各密度の mipmap とアダプティブアイコンを生成する。editor/src/Android/Icons/。無ければシステムの既定のアイコン）
//    icon_background … アダプティブアイコンの背景色（#RRGGBB / #AARRGGBB。省略時は白。段階D）
//    features       … アプリのプラットフォーム機能の opt-in（W1-2。例 ["alarm", "notifications"]。語彙は
//                      runtime/android/platform_features.json。書いた機能の権限・部品だけがマニフェストの断片に入る。
//                      知らない名前は保存で失わず、ビルドでは警告して無視する）
//    deep_links     … アプリを開く URL（W1-2。AndroidDeepLinkSetting の配列。機能 deep_links を書いたときだけ intent-filter になる）
//    system_bars    … システムバーの既定の出し方（W1-2。hidden＝既定・ゲーム／visible＝アプリ。AndroidSystemBarsSetting）
//    app_category   … android:appCategory（W1-2。game＝既定／productivity 等。AndroidAppCategorySetting）
//    predictive_back … 予測型の戻るを使うか（W2 の手直し P1-3。真偽。無い・false＝従来の戻るキー → Escape。true のときだけ
//                      マニフェストの android:enableOnBackInvokedCallback を "true" にし、res の印 seed_predictive_back を足す。
//                      docs/android.md §25.18）
//  W1-2 の 4 つと predictive_back は SeedAndroid の editor/src/Android/Platform/ が読み、マニフェストの断片・Gradle のプロパティにする
//  （docs/android.md §25.10）。
//  どれも省略できる。省略したものはビルド時に既定値になる（既定値の作り方と値の検査は
//  editor/src/Android/Project/AndroidAppIdentityResolver.cs。ID は .seedproj の名前から
//  com.seedengine.<英数字化した名前>、名前はプロジェクトの表示名、版は 1 / "1.0"）。
//  値は SeedAndroid（editor/src/Android/）が読み、Gradle へ -Pseed.applicationId 等で渡し、
//  runtime/android/app/build.gradle.kts がマニフェスト・applicationId へ反映する（docs/android.md §18）。
//
//  【読み方を寛容にしている理由】
//  project_settings.json は手で書き換えることもある。"version_code": "2" のように型が違うだけで
//  ProjectSettingsData 全体の読み込みが失敗すると、プロジェクト設定ウィンドウが既定値で開き、
//  保存で全体を上書きしてしまう。そこでこの節だけは専用の変換器で読み、読めない値は「未設定」扱いにする。
//  知らないキーは保存で失われない（ExtraData）。
//
//  WPF に依存しない（単体テスト・コンソールツールからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// project_settings.json の "android" 節。すべて省略可（null ＝ 既定値を使う）。
/// </summary>
[JsonConverter(typeof(AndroidAppSettingsJsonConverter))]
public sealed class AndroidAppSettings
{
    /// <summary>project_settings.json の中での節のキー。</summary>
    public const string SectionKey = "android";

    /// <summary>アプリ ID のキー。</summary>
    public const string ApplicationIdKey = "application_id";

    /// <summary>アプリ名のキー。</summary>
    public const string AppNameKey = "app_name";

    /// <summary>整数の版のキー。</summary>
    public const string VersionCodeKey = "version_code";

    /// <summary>版の文字列のキー。</summary>
    public const string VersionNameKey = "version_name";

    /// <summary>ランチャーのアイコンの元の PNG のキー（段階D）。</summary>
    public const string IconKey = "icon";

    /// <summary>アダプティブアイコンの背景色のキー（段階D）。</summary>
    public const string IconBackgroundKey = "icon_background";

    /// <summary>プラットフォーム機能の opt-in のキー（W1-2。文字列の配列）。</summary>
    public const string FeaturesKey = "features";

    /// <summary>ディープリンクの一覧のキー（W1-2。AndroidDeepLinkSetting の配列）。</summary>
    public const string DeepLinksKey = "deep_links";

    /// <summary>システムバーの既定の出し方のキー（W1-2。AndroidSystemBarsSetting）。</summary>
    public const string SystemBarsKey = "system_bars";

    /// <summary>アプリの分類のキー（W1-2。AndroidAppCategorySetting）。</summary>
    public const string AppCategoryKey = "app_category";

    /// <summary>予測型の戻るを使うかのキー（W2 の手直し P1-3。真偽）。</summary>
    public const string PredictiveBackKey = "predictive_back";

    /// <summary>真偽を文字列で書いたときの「真」（寛容な読み取り。大文字小文字・前後の空白は問わない）。</summary>
    private const string TrueText = "true";

    /// <summary>真偽を文字列で書いたときの「偽」（寛容な読み取り）。</summary>
    private const string FalseText = "false";

    /// <summary>アプリ ID（例 com.example.mygame）。null / 空なら既定値。</summary>
    public string? ApplicationId { get; set; }

    /// <summary>ランチャーに出る名前。null / 空なら既定値（プロジェクトの表示名）。</summary>
    public string? AppName { get; set; }

    /// <summary>整数の版（1 以上）。null なら既定値。</summary>
    public int? VersionCode { get; set; }

    /// <summary>版の文字列（例 1.0.3）。null / 空なら既定値。</summary>
    public string? VersionName { get; set; }

    /// <summary>
    /// ランチャーのアイコンの元の PNG（アセットルートからの相対パスか絶対パス。例 icons/app_icon.png）。null / 空ならシステムの既定のアイコン（段階D）。
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>アダプティブアイコンの背景色（#RRGGBB / #AARRGGBB）。null / 空なら白（段階D）。</summary>
    public string? IconBackground { get; set; }

    /// <summary>
    /// プラットフォーム機能の opt-in（W1-2。書かれた表記のまま〈前後の空白は落とす〉・書かれた順。知らない名前も保つ）。
    /// null / 空なら機能なし（権限・部品を足さない）。
    /// </summary>
    public List<string>? Features { get; set; }

    /// <summary>ディープリンクの一覧（W1-2）。null / 空なら無し。機能 deep_links を書いたときだけ intent-filter になる。</summary>
    public List<AndroidDeepLinkSetting>? DeepLinks { get; set; }

    /// <summary>システムバーの既定の出し方（W1-2。書かれた表記のまま）。null / 空なら hidden（従来のゲームの振る舞い）。</summary>
    public string? SystemBars { get; set; }

    /// <summary>アプリの分類（W1-2。書かれた表記のまま）。null / 空なら game（従来の固定値）。</summary>
    public string? AppCategory { get; set; }

    /// <summary>
    /// 予測型の戻るを使うか（W2 の手直し P1-3）。true のときだけ有効（null・false は従来の戻るキー → Escape）。
    /// 保存は true のときだけ書く（false・null はキーを省く＝既定）。
    /// </summary>
    public bool? PredictiveBack { get; set; }

    /// <summary>このクラスが知らないキー（新しいエディタが足したもの）。保存で失わないために持つ。</summary>
    public Dictionary<string, JsonElement> ExtraData { get; set; } = new();

    /// <summary>何も設定されていないか（節ごと省略して保存してよいか。predictive_back は true のときだけ「設定あり」）。</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ApplicationId)
        && string.IsNullOrWhiteSpace(AppName)
        && VersionCode is null
        && string.IsNullOrWhiteSpace(VersionName)
        && string.IsNullOrWhiteSpace(Icon)
        && string.IsNullOrWhiteSpace(IconBackground)
        && (Features is null || Features.All(string.IsNullOrWhiteSpace))
        && (DeepLinks is null || DeepLinks.All(link => link.IsBlank))
        && string.IsNullOrWhiteSpace(SystemBars)
        && string.IsNullOrWhiteSpace(AppCategory)
        && PredictiveBack != true
        && ExtraData.Count == 0;

    /// <summary>
    /// 真偽の値を寛容に読む（W2 の手直し P1-3 の predictive_back。JSON の true / false と、文字列の "true" / "false"〈大文字小文字・前後の
    /// 空白は問わない〉。それ以外の型・語は null＝未設定）。
    /// </summary>
    /// <param name="element">値。</param>
    /// <returns>真偽（読めなければ null）。</returns>
    internal static bool? ReadBooleanOrNull(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                var text = element.GetString()?.Trim();
                if (string.Equals(text, TrueText, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(text, FalseText, StringComparison.OrdinalIgnoreCase)) return false;
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// 入力欄の文字列を値にする（前後の空白を落とし、空なら null＝既定値）。
    /// </summary>
    /// <param name="text">入力された文字列。</param>
    /// <returns>設定値（空なら null）。</returns>
    public static string? NormalizeText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>
/// <see cref="AndroidAppSettings"/> の JSON 変換（型の違う値は未設定として読む。知らないキーは保つ）。
/// </summary>
internal sealed class AndroidAppSettingsJsonConverter : JsonConverter<AndroidAppSettings>
{
    /// <summary>節を読む。オブジェクトでなければ空の設定にする（読み込み全体を失敗させない）。</summary>
    /// <param name="reader">JSON の読み手。</param>
    /// <param name="typeToConvert">変換先の型。</param>
    /// <param name="options">シリアライズ設定。</param>
    /// <returns>読んだ設定。</returns>
    public override AndroidAppSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var settings = new AndroidAppSettings();
        if (document.RootElement.ValueKind != JsonValueKind.Object) return settings;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case AndroidAppSettings.ApplicationIdKey:
                    settings.ApplicationId = ReadText(property.Value);
                    break;
                case AndroidAppSettings.AppNameKey:
                    settings.AppName = ReadText(property.Value);
                    break;
                case AndroidAppSettings.VersionCodeKey:
                    settings.VersionCode = ReadInteger(property.Value);
                    break;
                case AndroidAppSettings.VersionNameKey:
                    settings.VersionName = ReadText(property.Value);
                    break;
                case AndroidAppSettings.IconKey:
                    settings.Icon = ReadText(property.Value);
                    break;
                case AndroidAppSettings.IconBackgroundKey:
                    settings.IconBackground = ReadText(property.Value);
                    break;
                case AndroidAppSettings.FeaturesKey:
                    settings.Features = ReadTextList(property.Value);
                    break;
                case AndroidAppSettings.DeepLinksKey:
                    settings.DeepLinks = AndroidDeepLinkSetting.ReadList(property.Value);
                    break;
                case AndroidAppSettings.SystemBarsKey:
                    settings.SystemBars = ReadText(property.Value);
                    break;
                case AndroidAppSettings.AppCategoryKey:
                    settings.AppCategory = ReadText(property.Value);
                    break;
                case AndroidAppSettings.PredictiveBackKey:
                    settings.PredictiveBack = AndroidAppSettings.ReadBooleanOrNull(property.Value);
                    break;
                default:
                    // 知らないキーはそのまま保つ（Clone で JsonDocument の寿命から切り離す）
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
    public override void Write(Utf8JsonWriter writer, AndroidAppSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteTextIfSet(writer, AndroidAppSettings.ApplicationIdKey, value.ApplicationId);
        WriteTextIfSet(writer, AndroidAppSettings.AppNameKey, value.AppName);
        if (value.VersionCode is int versionCode) writer.WriteNumber(AndroidAppSettings.VersionCodeKey, versionCode);
        WriteTextIfSet(writer, AndroidAppSettings.VersionNameKey, value.VersionName);
        WriteTextIfSet(writer, AndroidAppSettings.IconKey, value.Icon);
        WriteTextIfSet(writer, AndroidAppSettings.IconBackgroundKey, value.IconBackground);
        // W1-2 のプラットフォーム機能（空の一覧・未設定は書かない＝既定）
        var features = value.Features?.Select(AndroidAppSettings.NormalizeText).OfType<string>().ToList();
        if (features is { Count: > 0 })
        {
            writer.WriteStartArray(AndroidAppSettings.FeaturesKey);
            foreach (var feature in features) writer.WriteStringValue(feature);
            writer.WriteEndArray();
        }
        // 何も書かれていない行（エディタで足しただけの空の行）は書かない
        var deepLinks = value.DeepLinks?.Where(link => !link.IsBlank).ToList();
        if (deepLinks is { Count: > 0 })
        {
            writer.WriteStartArray(AndroidAppSettings.DeepLinksKey);
            foreach (var link in deepLinks) link.Write(writer);
            writer.WriteEndArray();
        }
        WriteTextIfSet(writer, AndroidAppSettings.SystemBarsKey, value.SystemBars);
        WriteTextIfSet(writer, AndroidAppSettings.AppCategoryKey, value.AppCategory);
        // W2 の手直し P1-3: 予測型の戻るは true のときだけ書く（false・未設定は既定なのでキーを省く）
        if (value.PredictiveBack == true) writer.WriteBoolean(AndroidAppSettings.PredictiveBackKey, true);
        foreach (var (key, element) in value.ExtraData)
        {
            writer.WritePropertyName(key);
            element.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    /// <summary>文字列の値を読む（数値は文字列として受け付ける。それ以外・空は null）。</summary>
    /// <param name="element">値。</param>
    /// <returns>文字列（前後の空白は落とす）。</returns>
    private static string? ReadText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => AndroidAppSettings.NormalizeText(element.GetString()),
        JsonValueKind.Number => element.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// 文字列の一覧を読む（W1-2 の features）。配列なら文字列の要素だけ（空・文字列でない要素は読み飛ばす）、
    /// 文字列 1 つなら 1 要素の一覧（手で "features": "alarm" と書いたときも機能を落とさない）、それ以外は null（未設定）。
    /// </summary>
    /// <param name="element">値。</param>
    /// <returns>一覧（前後の空白は落とす）。</returns>
    private static List<string>? ReadTextList(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => AndroidAppSettings.NormalizeText(item.GetString()))
            .OfType<string>()
            .ToList(),
        JsonValueKind.String => AndroidAppSettings.NormalizeText(element.GetString()) is { } single ? new List<string> { single } : null,
        _ => null,
    };

    /// <summary>整数の値を読む（数値か、整数として読める文字列。それ以外は null）。</summary>
    /// <param name="element">値。</param>
    /// <returns>整数。</returns>
    private static int? ReadInteger(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>空でない文字列だけを書く。</summary>
    /// <param name="writer">JSON の書き手。</param>
    /// <param name="key">キー。</param>
    /// <param name="text">値。</param>
    private static void WriteTextIfSet(Utf8JsonWriter writer, string key, string? text)
    {
        var normalized = AndroidAppSettings.NormalizeText(text);
        if (normalized is not null) writer.WriteString(key, normalized);
    }
}
