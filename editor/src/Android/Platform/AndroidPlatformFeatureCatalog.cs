// ============================================================
//  AndroidPlatformFeatureCatalog.cs — 機能の表（runtime/android/platform_features.json）を読む（W1-2）
//
//  【役割】
//  android.features に書ける機能の一覧と、機能ごとにマニフェストの断片へ足す権限・要素を出す（データドリブン。
//  W1-3・W1-4 は表に行を足すだけで受信機・サービスを出し入れできる）。表はエディタ本体・SeedAndroid・単体テストの
//  ビルドで埋め込みリソースとして取り込む（csproj の EmbeddedResource。論理名 <see cref="ResourceName"/>）。
//  プロジェクト設定ウィンドウのチェックボックスとビルドの生成物が、同じ表から作られる。
//
//  【壊れていたら】
//  表はエンジンのソースの一部なので、読めない・形が違うのはエンジンの不具合として例外（InvalidDataException）にする
//  （ビルドは何も作らずに止め、設定ウィンドウは理由を出す）。黙って空の表にすると、目覚ましの権限が入らないまま
//  APK ができてしまうため。形の検査は単体テスト（AndroidPipelineTests の PlatformFeatureTests）も確かめる。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SEEDEditor.Android.Platform;

/// <summary>機能の表（読み込んだもの）。</summary>
public sealed class AndroidPlatformFeatureCatalog
{
    /// <summary>埋め込みリソースの論理名（SEEDEditor.csproj・SeedAndroid.csproj・テストの csproj と一致させる）。</summary>
    public const string ResourceName = "SEEDEditor.platform_features.json";

    /// <summary>このエディタが読める表の書式の版。</summary>
    public const int FormatVersion = 1;

    /// <summary>Android の名前空間の属性の前置き（android:name 等）。</summary>
    public const string AndroidAttributePrefix = "android:";

    /// <summary>マニフェストの真偽の属性値（真）。JSON の true をこの文字列で書く。</summary>
    public const string XmlTrue = "true";

    /// <summary>マニフェストの真偽の属性値（偽）。</summary>
    public const string XmlFalse = "false";

    // ── 表の欄の名前（runtime/android/platform_features.json と一致）──
    private const string FormatVersionKey = "format_version";
    private const string FeaturesKey = "features";
    private const string NameKey = "name";
    private const string LabelKey = "label";
    private const string DescriptionKey = "description";
    private const string PermissionsKey = "permissions";
    private const string MaxSdkVersionKey = "max_sdk_version";
    private const string ApplicationElementsKey = "application_elements";
    private const string DeepLinkFiltersKey = "deep_link_filters";
    private const string TagKey = "tag";
    private const string AttributesKey = "attributes";
    private const string ChildrenKey = "children";

    /// <summary>機能の名前の形（features に書く名前。小文字英字で始まり小文字英数字と _）。</summary>
    private static readonly Regex FeatureNamePattern = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>要素名・属性名（前置きを除いた部分）の形（XML の名前の安全な部分集合）。</summary>
    private static readonly Regex XmlNamePattern = new("^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

    /// <summary>権限の名前の形（パッケージ名の形。例 android.permission.WAKE_LOCK）。</summary>
    private static readonly Regex PermissionNamePattern = new(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant);

    /// <summary>読み込みの設定（コメント・末尾のカンマを許す。play_requirements.json と同じ）。</summary>
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>埋め込みの表（初めて使うときに 1 回だけ読む。読めなければ毎回同じ例外）。</summary>
    private static readonly Lazy<AndroidPlatformFeatureCatalog> BuiltInCatalog = new(LoadBuiltIn);

    /// <summary>表の順の機能。</summary>
    public IReadOnlyList<AndroidPlatformFeature> Features { get; }

    /// <summary>表を作る（<see cref="Parse"/> か埋め込みの表から）。</summary>
    /// <param name="features">表の順の機能。</param>
    private AndroidPlatformFeatureCatalog(IReadOnlyList<AndroidPlatformFeature> features)
    {
        Features = features;
    }

    /// <summary>
    /// 埋め込みの表（エディタ・SeedAndroid のビルドに入ったもの）。
    /// </summary>
    /// <exception cref="InvalidDataException">埋め込まれていない・形が違うとき（エンジンの不具合）。</exception>
    public static AndroidPlatformFeatureCatalog BuiltIn => BuiltInCatalog.Value;

    /// <summary>機能の名前の一覧（表の順）。</summary>
    public IReadOnlyList<string> Names => Features.Select(f => f.Name).ToList();

    /// <summary>
    /// 名前で機能を引く（前後の空白・大文字小文字は区別しない。無ければ null）。
    /// </summary>
    /// <param name="name">features に書かれた名前。</param>
    /// <returns>機能。</returns>
    public AndroidPlatformFeature? Find(string? name)
    {
        var normalized = NormalizeName(name);
        return normalized is null ? null : Features.FirstOrDefault(f => f.Name == normalized);
    }

    /// <summary>
    /// 権限を要求する機能の名前（表の順。どの機能の権限でもなければ空）。Google Play の要件チェックが
    /// 「features に無い機能の権限が入っていないか」を見るのに使う。
    /// </summary>
    /// <param name="permission">権限の名前。</param>
    /// <returns>機能の名前。</returns>
    public IReadOnlyList<string> FeaturesRequesting(string permission) =>
        Features.Where(f => f.Permissions.Any(p => string.Equals(p.Name, permission, StringComparison.Ordinal)))
            .Select(f => f.Name)
            .ToList();

    /// <summary>features に書かれた名前をそろえる（前後の空白を落として小文字。空なら null）。</summary>
    /// <param name="name">名前。</param>
    /// <returns>そろえた名前。</returns>
    public static string? NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToLowerInvariant();

    /// <summary>
    /// 表の JSON を読む（形を確かめる）。
    /// </summary>
    /// <param name="json">表の中身。</param>
    /// <returns>表。</returns>
    /// <exception cref="InvalidDataException">JSON として読めない・欄が足りない・形が違うとき。</exception>
    public static AndroidPlatformFeatureCatalog Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"機能の表を JSON として読めません: {ex.Message}", ex);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("ルートがオブジェクトではありません。");
            if (!root.TryGetProperty(FormatVersionKey, out var version) || !version.TryGetInt32(out var versionNumber)
                || versionNumber != FormatVersion)
            {
                throw Invalid($"{FormatVersionKey} が {FormatVersion} ではありません（このエディタが読めない版）。");
            }
            if (!root.TryGetProperty(FeaturesKey, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw Invalid($"{FeaturesKey} の配列がありません。");
            }

            var features = new List<AndroidPlatformFeature>();
            foreach (var entry in list.EnumerateArray())
            {
                var feature = ReadFeature(entry);
                if (features.Any(f => f.Name == feature.Name)) throw Invalid($"機能 {feature.Name} が 2 回あります。");
                features.Add(feature);
            }
            return new AndroidPlatformFeatureCatalog(features);
        }
    }

    /// <summary>埋め込みの表を読む。</summary>
    private static AndroidPlatformFeatureCatalog LoadBuiltIn()
    {
        using var stream = typeof(AndroidPlatformFeatureCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException(
                $"機能の表（{ResourceName}）がこのプログラムに埋め込まれていません。csproj の EmbeddedResource に " +
                "runtime/android/platform_features.json を足してください。");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>機能 1 行を読む。</summary>
    private static AndroidPlatformFeature ReadFeature(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) throw Invalid($"{FeaturesKey} の要素がオブジェクトではありません。");
        var name = RequiredText(entry, NameKey, "機能");
        if (!FeatureNamePattern.IsMatch(name)) throw Invalid($"機能の名前 \"{name}\" は小文字英字で始まる小文字英数字と _ にしてください。");
        var label = RequiredText(entry, LabelKey, name);
        var description = OptionalText(entry, DescriptionKey) ?? string.Empty;

        var permissions = new List<AndroidManifestPermission>();
        if (entry.TryGetProperty(PermissionsKey, out var permissionList))
        {
            if (permissionList.ValueKind != JsonValueKind.Array) throw Invalid($"機能 {name} の {PermissionsKey} が配列ではありません。");
            foreach (var item in permissionList.EnumerateArray()) permissions.Add(ReadPermission(item, name));
        }

        var elements = new List<AndroidManifestElement>();
        if (entry.TryGetProperty(ApplicationElementsKey, out var elementList))
        {
            if (elementList.ValueKind != JsonValueKind.Array) throw Invalid($"機能 {name} の {ApplicationElementsKey} が配列ではありません。");
            foreach (var item in elementList.EnumerateArray()) elements.Add(ReadElement(item, name));
        }

        var deepLinkFilters = entry.TryGetProperty(DeepLinkFiltersKey, out var flag) && flag.ValueKind == JsonValueKind.True;
        return new AndroidPlatformFeature(name, label, description, permissions, elements, deepLinkFilters);
    }

    /// <summary>権限 1 つを読む。</summary>
    private static AndroidManifestPermission ReadPermission(JsonElement item, string feature)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Invalid($"機能 {feature} の権限がオブジェクトではありません。");
        var permission = RequiredText(item, NameKey, feature);
        if (!PermissionNamePattern.IsMatch(permission)) throw Invalid($"機能 {feature} の権限の名前 \"{permission}\" の形が違います。");
        int? maxSdk = null;
        if (item.TryGetProperty(MaxSdkVersionKey, out var max))
        {
            if (!max.TryGetInt32(out var value) || value <= 0) throw Invalid($"機能 {feature} の {permission} の {MaxSdkVersionKey} は正の整数にしてください。");
            maxSdk = value;
        }
        return new AndroidManifestPermission(permission, maxSdk);
    }

    /// <summary>要素の木を読む（子も同じ形で読む）。</summary>
    private static AndroidManifestElement ReadElement(JsonElement item, string feature)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Invalid($"機能 {feature} の要素がオブジェクトではありません。");
        var tag = RequiredText(item, TagKey, feature);
        if (!XmlNamePattern.IsMatch(tag)) throw Invalid($"機能 {feature} の要素名 \"{tag}\" の形が違います。");

        var attributes = new List<KeyValuePair<string, string>>();
        if (item.TryGetProperty(AttributesKey, out var attributeObject))
        {
            if (attributeObject.ValueKind != JsonValueKind.Object) throw Invalid($"機能 {feature} の {tag} の {AttributesKey} がオブジェクトではありません。");
            foreach (var attribute in attributeObject.EnumerateObject())
            {
                var localName = attribute.Name.StartsWith(AndroidAttributePrefix, StringComparison.Ordinal)
                    ? attribute.Name[AndroidAttributePrefix.Length..]
                    : attribute.Name;
                if (!XmlNamePattern.IsMatch(localName) || localName.Contains(':'))
                {
                    throw Invalid($"機能 {feature} の {tag} の属性名 \"{attribute.Name}\" は android:名前 か前置きなしの名前にしてください。");
                }
                var value = attribute.Value.ValueKind switch
                {
                    JsonValueKind.String => attribute.Value.GetString()!,
                    JsonValueKind.True => XmlTrue,
                    JsonValueKind.False => XmlFalse,
                    JsonValueKind.Number => attribute.Value.GetRawText(),
                    _ => throw Invalid($"機能 {feature} の {tag} の属性 {attribute.Name} の値は文字列・数値・真偽にしてください。"),
                };
                attributes.Add(new KeyValuePair<string, string>(attribute.Name, value));
            }
        }

        var children = new List<AndroidManifestElement>();
        if (item.TryGetProperty(ChildrenKey, out var childList))
        {
            if (childList.ValueKind != JsonValueKind.Array) throw Invalid($"機能 {feature} の {tag} の {ChildrenKey} が配列ではありません。");
            foreach (var child in childList.EnumerateArray()) children.Add(ReadElement(child, feature));
        }
        return new AndroidManifestElement(tag, attributes, children);
    }

    /// <summary>必須の文字列の欄を読む（無い・空なら例外）。</summary>
    private static string RequiredText(JsonElement item, string key, string owner) =>
        OptionalText(item, key) ?? throw Invalid($"{owner} の {key} がありません。");

    /// <summary>文字列の欄を読む（無い・空・文字列でなければ null。前後の空白は落とす）。</summary>
    private static string? OptionalText(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>形の誤りの例外。</summary>
    private static InvalidDataException Invalid(string message) =>
        new(string.Format(CultureInfo.InvariantCulture, "機能の表（runtime/android/platform_features.json）の誤り: {0}", message));
}
