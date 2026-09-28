using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SEED.UI;

// ============================================================
//  UiThemeSource.cs — テーマの JSON 1 つの読み込み（W2-9。docs/ui_theme.md §2。純粋な計算）
//
//  【JSON の形】
//    {
//      "name": "forest",                  … 名前（ログ・Changed の知らせ）
//      "extends": "base.json",            … 基のテーマ（省略 = 組み込みの既定のテーマ。"default" も同じ。相対パスはこのファイルから）
//      "brightness": "dark",              … このテーマの明暗（省略 = 基のテーマのまま。書くと「この明暗のテーマ」になる）
//      "seed_color": "#2E7D32",           … 種の色（主の色と選択の色を明暗の規則で作る。UiSeedColors）
//      "color": { "primary": "#…" },      … グループ（color・radius・space・size・text・font・motion・opacity・ratio・speed・count・layer）
//      "color.surface": "#…",             … 「グループ.名前」を最上位に直接書いてもよい
//      "app": { "kakugo": { "danger": "#FF5252" } },  … アプリ独自のトークン（表で調べない）
//      "light": { "color": { … } },       … 明るい方で表示するときだけ上書きする（これがあると明るい方にも対応する）
//      "dark":  { … }                     … 暗い方で表示するときだけ上書きする
//    }
//  先頭が _ の鍵は説明（読まない）。コメント（// と /* */）と末尾のカンマは許す（手で書くファイルのため）。
//  【読み方の約束】知らない名前・型の合わない値は警告（Warnings）に積んで読まない（基のテーマ・既定の値が残る）。
//  例外は投げない。壊れた JSON は Error に理由（空の表として扱う）。
// ============================================================

/// <summary>テーマの JSON 1 つの読み込みの結果（継承は解かない。UiThemeResolver が解く）。</summary>
public sealed class UiThemeSource
{
    /// <summary>名前の鍵。</summary>
    public const string KeyName = "name";
    /// <summary>基のテーマの鍵。</summary>
    public const string KeyExtends = "extends";
    /// <summary>明暗の鍵。</summary>
    public const string KeyBrightness = "brightness";
    /// <summary>種の色の鍵。</summary>
    public const string KeySeedColor = "seed_color";
    /// <summary>読まない鍵（説明）の接頭辞。</summary>
    private const string CommentPrefix = "_";

    /// <summary>JSON の読み方（手で書くファイルなのでコメントと末尾のカンマを許す）。</summary>
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>節の中に書けない鍵（最上位だけの鍵）。</summary>
    private static readonly HashSet<string> TopLevelOnlyKeys = new(StringComparer.Ordinal)
    {
        KeyName, KeyExtends, KeyBrightness, KeySeedColor, UiBrightnessRules.LightWord, UiBrightnessRules.DarkWord,
    };

    /// <summary>明暗に依らない値。</summary>
    private readonly Dictionary<string, UiThemeValue> _base = new(StringComparer.Ordinal);
    /// <summary>明るい方の節（無ければ null）。</summary>
    private Dictionary<string, UiThemeValue>? _light;
    /// <summary>暗い方の節（無ければ null）。</summary>
    private Dictionary<string, UiThemeValue>? _dark;
    /// <summary>警告。</summary>
    private readonly List<string> _warnings = new();

    /// <summary>どこから読んだか（assets:// のパス・"builtin:default" など。警告の頭に付ける）。</summary>
    public string Origin { get; }
    /// <summary>名前（書いていなければ空）。</summary>
    public string Name { get; private set; } = string.Empty;
    /// <summary>基のテーマ（書いていなければ null ＝ 組み込みの既定のテーマ）。</summary>
    public string? Extends { get; private set; }
    /// <summary>このテーマの明暗（書いていなければ null ＝ 基のテーマのまま）。</summary>
    public UiBrightness? Brightness { get; private set; }
    /// <summary>種の色（線形。書いていなければ null）。</summary>
    public Color? SeedColor { get; private set; }
    /// <summary>明暗に依らない値。</summary>
    public IReadOnlyDictionary<string, UiThemeValue> Base => _base;
    /// <summary>明るい方の節（無ければ null）。</summary>
    public IReadOnlyDictionary<string, UiThemeValue>? Light => _light;
    /// <summary>暗い方の節（無ければ null）。</summary>
    public IReadOnlyDictionary<string, UiThemeValue>? Dark => _dark;
    /// <summary>警告（知らない名前・型の誤り・書けない場所の鍵）。</summary>
    public IReadOnlyList<string> Warnings => _warnings;
    /// <summary>壊れた JSON の理由（読めたら空）。</summary>
    public string Error { get; private set; } = string.Empty;
    /// <summary>JSON として読めたか。</summary>
    public bool IsValid => Error.Length == 0;

    private UiThemeSource(string origin)
    {
        Origin = origin;
    }

    /// <summary>明暗の節（無ければ null）。</summary>
    public IReadOnlyDictionary<string, UiThemeValue>? Section(UiBrightness brightness)
        => brightness == UiBrightness.Light ? _light : _dark;

    /// <summary>
    /// JSON を読む（例外を投げない）。
    /// </summary>
    /// <param name="json">テーマの JSON。</param>
    /// <param name="origin">どこから読んだか（警告の頭）。</param>
    public static UiThemeSource Parse(string json, string origin)
    {
        var source = new UiThemeSource(origin);
        try
        {
            using var doc = JsonDocument.Parse(json ?? string.Empty, ReadOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                source.Error = "テーマの JSON の最上位がオブジェクトではありません";
                return source;
            }
            foreach (var prop in doc.RootElement.EnumerateObject()) source.ReadTopLevel(prop);
        }
        catch (JsonException ex)
        {
            source.Error = ex.Message;
        }
        return source;
    }

    /// <summary>最上位の鍵を 1 つ読む（予約の鍵・節・トークン）。</summary>
    private void ReadTopLevel(JsonProperty prop)
    {
        string key = prop.Name;
        var value = prop.Value;
        if (key.StartsWith(CommentPrefix, StringComparison.Ordinal)) return;
        switch (key)
        {
            case KeyName:
                if (value.ValueKind == JsonValueKind.String) Name = value.GetString() ?? string.Empty;
                else Warn(key, "名前は文字列で書きます");
                return;
            case KeyExtends:
                if (value.ValueKind == JsonValueKind.String && (value.GetString() ?? string.Empty).Length > 0) Extends = value.GetString();
                else Warn(key, "基のテーマは空でない文字列（パスか \"default\"）で書きます");
                return;
            case KeyBrightness:
                if (value.ValueKind == JsonValueKind.String && UiBrightnessRules.TryParse(value.GetString(), out var b)) Brightness = b;
                else Warn(key, $"明暗は \"{UiBrightnessRules.DarkWord}\" か \"{UiBrightnessRules.LightWord}\" で書きます");
                return;
            case KeySeedColor:
                if (value.ValueKind == JsonValueKind.String && UiColorMath.TryParseHex(value.GetString(), out var seed)) SeedColor = seed;
                else Warn(key, "種の色は \"#RRGGBB\" で書きます");
                return;
            case UiBrightnessRules.LightWord:
                _light = ReadSection(key, value);
                return;
            case UiBrightnessRules.DarkWord:
                _dark = ReadSection(key, value);
                return;
            default:
                Collect(key, value, _base);
                return;
        }
    }

    /// <summary>明暗の節を読む（オブジェクトでなければ警告して無し）。</summary>
    private Dictionary<string, UiThemeValue>? ReadSection(string key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            Warn(key, "明暗の節はオブジェクトで書きます");
            return null;
        }
        var section = new Dictionary<string, UiThemeValue>(StringComparer.Ordinal);
        foreach (var child in value.EnumerateObject())
        {
            if (child.Name.StartsWith(CommentPrefix, StringComparison.Ordinal)) continue;
            if (TopLevelOnlyKeys.Contains(child.Name))
            {
                Warn(key + UiTokenCatalog.Separator + child.Name, "明暗の節の中には書けません（最上位に書きます）");
                continue;
            }
            Collect(child.Name, child.Value, section);
        }
        return section;
    }

    /// <summary>1 つの鍵と値を表へ入れる（オブジェクトは中へ入って「親.子」にする）。</summary>
    private void Collect(string token, JsonElement value, Dictionary<string, UiThemeValue> target)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in value.EnumerateObject())
            {
                if (child.Name.StartsWith(CommentPrefix, StringComparison.Ordinal)) continue;
                Collect(token + UiTokenCatalog.Separator + child.Name, child.Value, target);
            }
            return;
        }
        switch (UiTokenCatalog.Match(token, out var kind))
        {
            case UiTokenMatch.Known:
                ReadKnown(token, kind, value, target);
                return;
            case UiTokenMatch.AppDefined:
                ReadAppDefined(token, value, target);
                return;
            case UiTokenMatch.UnknownName:
                Warn(token, token.IndexOf(UiTokenCatalog.Separator) < 0
                    ? "グループはオブジェクトで書きます（例 \"color\": { \"primary\": \"#…\" }）"
                    : "トークンの表に無い名前です（打ち間違い？ アプリ独自のトークンは app のグループへ）");
                return;
            default:
                Warn(token, $"知らないグループです（SEED のグループか、アプリ独自の {UiTokenCatalog.AppGroup} へ）");
                return;
        }
    }

    /// <summary>表にあるトークンの値を型どおりに読む（合わなければ警告して読まない）。</summary>
    private void ReadKnown(string token, UiTokenKind kind, JsonElement value, Dictionary<string, UiThemeValue> target)
    {
        switch (kind)
        {
            case UiTokenKind.Color:
                if (value.ValueKind == JsonValueKind.String && UiColorMath.TryParseHex(value.GetString(), out var color))
                    target[token] = UiThemeValue.FromColor(color);
                else
                    Warn(token, "色は \"#RRGGBB\" か \"#RRGGBBAA\"（sRGB）で書きます");
                return;
            case UiTokenKind.Number:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number) && float.IsFinite(number))
                    target[token] = UiThemeValue.FromNumber(number);
                else
                    Warn(token, "数で書きます");
                return;
            case UiTokenKind.Text:
                if (value.ValueKind == JsonValueKind.String) target[token] = UiThemeValue.FromText(value.GetString() ?? string.Empty);
                else Warn(token, "文字列で書きます");
                return;
            default:
                Warn(token, "曲線は { \"x1\", \"y1\", \"x2\", \"y2\" } の 4 つの数で書きます");
                return;
        }
    }

    /// <summary>アプリ独自のトークンを値の形で読む（#… の文字列は色・ほかの文字列は文字列・数は数）。</summary>
    private void ReadAppDefined(string token, JsonElement value, Dictionary<string, UiThemeValue> target)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                string text = value.GetString() ?? string.Empty;
                target[token] = UiColorMath.TryParseHex(text, out var color) ? UiThemeValue.FromColor(color) : UiThemeValue.FromText(text);
                return;
            case JsonValueKind.Number when value.TryGetSingle(out var number) && float.IsFinite(number):
                target[token] = UiThemeValue.FromNumber(number);
                return;
            default:
                Warn(token, "アプリ独自のトークンは色（\"#RRGGBB\"）・数・文字列で書きます");
                return;
        }
    }

    /// <summary>警告を積む。</summary>
    private void Warn(string token, string reason) => _warnings.Add($"{Origin}: {token}: {reason}");
}
