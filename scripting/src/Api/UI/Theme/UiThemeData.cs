using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SEED.UI;

// ============================================================
//  UiThemeData.cs — テーマの値の表（トークン → 色・数）と JSON の読み込み（W2-4。純粋な計算）
//
//  JSON の形（default_theme.json）:
//    { "name": "default", "color": { "primary": "#7C5CFF", … }, "radius": { "button": 12, … }, … }
//  グループの中の名前を「グループ.名前」のトークンにする（入れ子はさらに . でつなぐ）。最上位に
//  "color.primary": "#…" のように直接書いてもよい（Wake or Pay の themes.json の fixed と同じ書き方）。
//  文字列の "#RRGGBB" / "#RRGGBBAA"（sRGB）は色（線形へ直す）、数は数。先頭が _ の鍵（説明）は読まない。
//  引けないトークンは「代わりのテーマ」（既定のテーマ）→ 呼び出し側の既定値の順で引く。
//  リフレクションを使わない JsonDocument で読む（トリミングしても壊れない）。
// ============================================================

/// <summary>テーマの値の表（変わらない値。差し替えは <see cref="UiTheme"/> が表ごと入れ替える）。</summary>
public sealed class UiThemeData
{
    /// <summary>トークンの区切り。</summary>
    private const char TokenSeparator = '.';
    /// <summary>読まない鍵（説明）の接頭辞。</summary>
    private const string CommentPrefix = "_";
    /// <summary>名前の鍵。</summary>
    private const string NameKey = "name";

    /// <summary>色のトークン。</summary>
    private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
    /// <summary>数のトークン。</summary>
    private readonly Dictionary<string, float> _numbers = new(StringComparer.Ordinal);
    /// <summary>引けないときの代わり（既定のテーマ。無ければ null）。</summary>
    private readonly UiThemeData? _fallback;

    /// <summary>テーマの名前。</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>このテーマ自身が持つトークンの数（代わりのテーマの分は含まない）。</summary>
    public int Count => _colors.Count + _numbers.Count;

    private UiThemeData(UiThemeData? fallback)
    {
        _fallback = fallback;
    }

    /// <summary>空のテーマ（すべて代わりのテーマか呼び出し側の既定値になる）。</summary>
    public static UiThemeData Empty(UiThemeData? fallback = null) => new(fallback);

    /// <summary>
    /// JSON からテーマを作る。壊れた JSON は例外にせず空のテーマ（＝代わりのテーマ）にする。
    /// </summary>
    /// <param name="json">テーマの JSON。</param>
    /// <param name="fallback">引けないトークンの代わり（既定のテーマ）。</param>
    /// <param name="error">読めなかった理由（読めたら空）。</param>
    public static UiThemeData Parse(string json, UiThemeData? fallback, out string error)
    {
        var data = new UiThemeData(fallback);
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "テーマの JSON の最上位がオブジェクトではありません";
                return data;
            }
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == NameKey && prop.Value.ValueKind == JsonValueKind.String)
                {
                    data.Name = prop.Value.GetString() ?? string.Empty;
                    continue;
                }
                data.Collect(prop.Name, prop.Value);
            }
        }
        catch (JsonException ex)
        {
            error = ex.Message;
        }
        return data;
    }

    /// <summary>1 つの鍵と値を表へ入れる（オブジェクトは中へ入って「親.子」にする）。</summary>
    private void Collect(string token, JsonElement value)
    {
        if (token.StartsWith(CommentPrefix, StringComparison.Ordinal)) return;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var child in value.EnumerateObject())
                {
                    if (child.Name.StartsWith(CommentPrefix, StringComparison.Ordinal)) continue;
                    Collect(token + TokenSeparator + child.Name, child.Value);
                }
                break;
            case JsonValueKind.Number:
                if (value.TryGetSingle(out var number) && float.IsFinite(number)) _numbers[token] = number;
                break;
            case JsonValueKind.String:
                if (UiColorMath.TryParseHex(value.GetString(), out var color)) _colors[token] = color;
                break;
        }
    }

    /// <summary>色のトークンを引く（このテーマ → 代わりのテーマ）。</summary>
    public bool TryColor(string token, out Color color)
    {
        if (_colors.TryGetValue(token, out color)) return true;
        if (_fallback is not null) return _fallback.TryColor(token, out color);
        color = SEED.Color.White;
        return false;
    }

    /// <summary>数のトークンを引く（このテーマ → 代わりのテーマ）。</summary>
    public bool TryNumber(string token, out float number)
    {
        if (_numbers.TryGetValue(token, out number)) return true;
        if (_fallback is not null) return _fallback.TryNumber(token, out number);
        number = 0f;
        return false;
    }

    /// <summary>色のトークン（引けなければ <paramref name="fallback"/>）。</summary>
    public Color Color(string token, Color fallback) => TryColor(token, out var c) ? c : fallback;

    /// <summary>色のトークン（引けなければ白）。</summary>
    public Color Color(string token) => Color(token, SEED.Color.White);

    /// <summary>数のトークン（引けなければ <paramref name="fallback"/>）。</summary>
    public float Number(string token, float fallback = 0f) => TryNumber(token, out var n) ? n : fallback;

    /// <summary>トークンを持つか（色か数。代わりのテーマも見る）。</summary>
    public bool Has(string token) => TryColor(token, out _) || TryNumber(token, out _);

    /// <summary>数の文字列（ログ・診断用。インバリアント）。</summary>
    public string Describe(string token)
    {
        if (TryColor(token, out var c)) return $"{token} = {c}";
        return TryNumber(token, out var n) ? $"{token} = {n.ToString(CultureInfo.InvariantCulture)}" : $"{token} = (なし)";
    }
}
