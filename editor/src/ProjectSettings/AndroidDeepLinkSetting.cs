// ============================================================
//  AndroidDeepLinkSetting.cs — project_settings.json の android.deep_links の 1 件（W1-2）
//
//  【役割】
//  アプリを開く URL（ディープリンク）1 件。機能 deep_links を android.features に書いたプロジェクトでは、
//  SeedAndroid が 1 件ごとに MainActivity への <intent-filter>（VIEW・DEFAULT・BROWSABLE と
//  <data android:scheme android:host android:pathPrefix>）を生成したマニフェストの断片へ書く
//  （editor/src/Android/Platform/AndroidPlatformManifestWriter.cs。値の検査は AndroidDeepLinkRules.cs）。
//    scheme      … 必須（例 wakeorpay / https）。小文字（Android の照合は大文字小文字を区別する）
//    host        … 省略可（例 example.com・*.example.com）。path_prefix を使うなら必須
//    path_prefix … 省略可（例 /alarm）。/ で始まる
//    auto_verify … true なら intent-filter に android:autoVerify="true"（Android App Links の検証。https/http と host が要る）
//  JSON の例: { "scheme": "https", "host": "example.com", "path_prefix": "/wake", "auto_verify": true }
//
//  【読み方を寛容にしている理由】AndroidAppSettings と同じ。型の違う値は未設定として読み、知らないキーは保存で失わない。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.ProjectSettings;

/// <summary>ディープリンク 1 件（project_settings.json の android.deep_links の要素）。</summary>
public sealed class AndroidDeepLinkSetting
{
    /// <summary>スキームのキー。</summary>
    public const string SchemeKey = "scheme";

    /// <summary>ホストのキー。</summary>
    public const string HostKey = "host";

    /// <summary>パスの前半のキー。</summary>
    public const string PathPrefixKey = "path_prefix";

    /// <summary>Android App Links の検証のキー。</summary>
    public const string AutoVerifyKey = "auto_verify";

    /// <summary>JSON の真偽の文字列表記（手で "true" と書かれたときも読む）。</summary>
    private const string TrueText = "true";

    /// <summary>スキーム（例 https）。null / 空は未設定（検査で誤り）。</summary>
    public string? Scheme { get; set; }

    /// <summary>ホスト（例 example.com）。null / 空は指定なし。</summary>
    public string? Host { get; set; }

    /// <summary>パスの前半（例 /wake）。null / 空は指定なし。</summary>
    public string? PathPrefix { get; set; }

    /// <summary>Android App Links の検証（intent-filter の android:autoVerify）。</summary>
    public bool AutoVerify { get; set; }

    /// <summary>このクラスが知らないキー（新しいエディタが足したもの）。保存で失わないために持つ。</summary>
    public Dictionary<string, JsonElement> ExtraData { get; set; } = new();

    /// <summary>
    /// 複製を作る（編集用の作業コピー。ExtraData の値は読み取り専用の JsonElement なので共有してよい）。
    /// </summary>
    /// <returns>複製。</returns>
    public AndroidDeepLinkSetting Clone() => new()
    {
        Scheme = Scheme,
        Host = Host,
        PathPrefix = PathPrefix,
        AutoVerify = AutoVerify,
        ExtraData = new Dictionary<string, JsonElement>(ExtraData),
    };

    /// <summary>
    /// 表示・ログ用の URL の形（例 https://example.com/wake…）。空の欄は省く。
    /// </summary>
    /// <returns>説明。</returns>
    public string Describe()
    {
        var scheme = string.IsNullOrWhiteSpace(Scheme) ? "(スキームなし)" : Scheme.Trim();
        var host = Host?.Trim() ?? string.Empty;
        var path = string.IsNullOrWhiteSpace(PathPrefix) ? string.Empty : PathPrefix.Trim() + "…";
        var verify = AutoVerify ? "（autoVerify）" : string.Empty;
        return $"{scheme}://{host}{path}{verify}";
    }

    /// <summary>
    /// JSON の要素 1 つを読む（オブジェクトでなければ null＝読み飛ばす。欄の型が違えば未設定）。
    /// </summary>
    /// <param name="element">deep_links の配列の要素。</param>
    /// <returns>読んだ 1 件（オブジェクトでなければ null）。</returns>
    public static AndroidDeepLinkSetting? Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var link = new AndroidDeepLinkSetting();
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case SchemeKey:
                    link.Scheme = ReadText(property.Value);
                    break;
                case HostKey:
                    link.Host = ReadText(property.Value);
                    break;
                case PathPrefixKey:
                    link.PathPrefix = ReadText(property.Value);
                    break;
                case AutoVerifyKey:
                    link.AutoVerify = ReadBoolean(property.Value);
                    break;
                default:
                    // 知らないキーはそのまま保つ（Clone で JsonDocument の寿命から切り離す）
                    link.ExtraData[property.Name] = property.Value.Clone();
                    break;
            }
        }
        return link;
    }

    /// <summary>
    /// 1 件を書く（設定されている欄と知らないキーだけ。auto_verify は true のときだけ）。
    /// </summary>
    /// <param name="writer">JSON の書き手。</param>
    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        WriteTextIfSet(writer, SchemeKey, Scheme);
        WriteTextIfSet(writer, HostKey, Host);
        WriteTextIfSet(writer, PathPrefixKey, PathPrefix);
        if (AutoVerify) writer.WriteBoolean(AutoVerifyKey, true);
        foreach (var (key, value) in ExtraData)
        {
            writer.WritePropertyName(key);
            value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// 何も書かれていないか（欄がすべて空・検証なし・知らないキーなし。エディタで足しただけの空の行）。
    /// </summary>
    public bool IsBlank =>
        string.IsNullOrWhiteSpace(Scheme) && string.IsNullOrWhiteSpace(Host) && string.IsNullOrWhiteSpace(PathPrefix)
        && !AutoVerify && ExtraData.Count == 0;

    /// <summary>
    /// 配列を読む（配列でなければ null＝未設定。オブジェクトでない要素は読み飛ばす）。
    /// </summary>
    /// <param name="element">deep_links の値。</param>
    /// <returns>読んだ一覧（配列でなければ null）。</returns>
    public static List<AndroidDeepLinkSetting>? ReadList(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return null;
        return element.EnumerateArray().Select(Read).OfType<AndroidDeepLinkSetting>().ToList();
    }

    /// <summary>文字列の値を読む（数値は文字列として受け付ける。それ以外・空は null。前後の空白は落とす）。</summary>
    private static string? ReadText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString()!.Trim(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null,
    };

    /// <summary>真偽の値を読む（true / false と、文字列の "true"。それ以外は false）。</summary>
    private static bool ReadBoolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => string.Equals(element.GetString()?.Trim(), TrueText, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>空でない文字列だけを書く（前後の空白は落とす）。</summary>
    private static void WriteTextIfSet(Utf8JsonWriter writer, string key, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) writer.WriteString(key, text.Trim());
    }
}
