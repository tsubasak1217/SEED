using System;
using System.Collections.Generic;
using System.Globalization;

namespace SEED.UI;

// ============================================================
//  UiThemeData.cs — 部品が読むテーマの値の表（トークン → 色・数・文字列。W2-4。W2-9 で継承・明暗・書体の解決後の表にした）
//
//  【役割】1 つの明暗で解決し終えた平らな表（変わらない値）。部品は UiTheme.Current（この型）からトークンを引く。
//    - 継承（extends）・明暗の節・種の色・既定のテーマへの落ち方は UiThemeResolver が解いてからここへ入れる
//      （引くたびに鎖をたどらない。切り替えの色の補間 UiThemeBlend も表どうしで作れる）
//    - 引けないトークンは呼び出し側の既定値（Color(token, fallback) など）
//  JSON 1 つを「代わりのテーマの上へ重ねる」だけの簡単な読み方 Parse（W2-4 からの口。テスト・小さな差し替え向け）も残す。
//  リフレクションを使わない JsonDocument で読む（トリミングしても壊れない。UiThemeSource）。
// ============================================================

/// <summary>テーマの値の表（1 つの明暗で解決し終えたもの。変わらない値）。</summary>
public sealed class UiThemeData
{
    /// <summary>JSON を直接読んだときの出どころ（警告の頭）。</summary>
    private const string InlineOrigin = "(json)";

    /// <summary>色のトークン（線形）。</summary>
    private readonly Dictionary<string, Color> _colors;
    /// <summary>数のトークン。</summary>
    private readonly Dictionary<string, float> _numbers;
    /// <summary>文字列のトークン。</summary>
    private readonly Dictionary<string, string> _texts;

    /// <summary>テーマの名前。</summary>
    public string Name { get; }

    /// <summary>この表の明暗。</summary>
    public UiBrightness Brightness { get; }

    /// <summary>作るときに出た警告（知らない名前・型の誤り。無ければ空）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>表のトークンの数（色・数・文字列の合計）。</summary>
    public int Count => _colors.Count + _numbers.Count + _texts.Count;

    /// <summary>色のトークンの名前（補間・診断用）。</summary>
    public IEnumerable<string> ColorTokens => _colors.Keys;

    /// <summary>数のトークンの名前（診断用）。</summary>
    public IEnumerable<string> NumberTokens => _numbers.Keys;

    /// <summary>文字列のトークンの名前（診断用）。</summary>
    public IEnumerable<string> TextTokens => _texts.Keys;

    /// <summary>表から作る（UiThemeTable.Freeze・UiThemeBlend が使う）。</summary>
    internal UiThemeData(string name, UiBrightness brightness, Dictionary<string, Color> colors, Dictionary<string, float> numbers,
        Dictionary<string, string> texts, IReadOnlyList<string> warnings)
    {
        Name = name;
        Brightness = brightness;
        _colors = colors;
        _numbers = numbers;
        _texts = texts;
        Warnings = warnings;
    }

    /// <summary>空の表（<paramref name="fallback"/> があればその写し）。すべて呼び出し側の既定値か代わりのテーマの値になる。</summary>
    public static UiThemeData Empty(UiThemeData? fallback = null)
        => UiThemeTable.From(fallback).Freeze(fallback?.Name ?? string.Empty, fallback?.Brightness ?? UiBrightness.Dark, Array.Empty<string>());

    /// <summary>
    /// JSON 1 つを <paramref name="fallback"/>（既定のテーマなど）の上へ重ねた表を作る。壊れた JSON は例外にせず代わりのテーマの写しにする。
    /// extends は見ない（継承を解くのは UiTheme.Load・UiThemeResolver）。明暗は JSON の brightness（無ければ代わりのテーマの明暗）で、
    /// その明暗の節と種の色も当てる。知らない名前・型の誤りは <see cref="Warnings"/>。
    /// </summary>
    /// <param name="json">テーマの JSON。</param>
    /// <param name="fallback">書いていないトークンの値（null なら空）。</param>
    /// <param name="error">読めなかった理由（読めたら空）。</param>
    public static UiThemeData Parse(string json, UiThemeData? fallback, out string error)
    {
        var source = UiThemeSource.Parse(json, InlineOrigin);
        error = source.Error;
        var brightness = source.Brightness ?? fallback?.Brightness ?? UiBrightness.Dark;
        var table = UiThemeTable.From(fallback);
        if (source.IsValid) table.ApplyLayer(source, brightness);
        string name = source.Name.Length > 0 ? source.Name : fallback?.Name ?? string.Empty;
        return table.Freeze(name, brightness, source.Warnings);
    }

    /// <summary>色のトークンを引く。</summary>
    public bool TryColor(string token, out Color color)
    {
        if (_colors.TryGetValue(token, out color)) return true;
        color = SEED.Color.White;
        return false;
    }

    /// <summary>数のトークンを引く。</summary>
    public bool TryNumber(string token, out float number)
    {
        if (_numbers.TryGetValue(token, out number)) return true;
        number = 0f;
        return false;
    }

    /// <summary>文字列のトークンを引く。</summary>
    public bool TryText(string token, out string text)
    {
        if (_texts.TryGetValue(token, out var t))
        {
            text = t;
            return true;
        }
        text = string.Empty;
        return false;
    }

    /// <summary>色のトークン（引けなければ <paramref name="fallback"/>）。</summary>
    public Color Color(string token, Color fallback) => TryColor(token, out var c) ? c : fallback;

    /// <summary>色のトークン（引けなければ白）。</summary>
    public Color Color(string token) => Color(token, SEED.Color.White);

    /// <summary>数のトークン（引けなければ <paramref name="fallback"/>）。</summary>
    public float Number(string token, float fallback = 0f) => TryNumber(token, out var n) ? n : fallback;

    /// <summary>文字列のトークン（引けなければ <paramref name="fallback"/>）。</summary>
    public string Text(string token, string fallback = "") => TryText(token, out var t) ? t : fallback;

    /// <summary>トークンを持つか（色・数・文字列のどれか）。</summary>
    public bool Has(string token) => _colors.ContainsKey(token) || _numbers.ContainsKey(token) || _texts.ContainsKey(token);

    /// <summary>トークンの値の文字列（ログ・診断用。インバリアント。色は sRGB の 16 進）。</summary>
    public string Describe(string token)
    {
        if (TryColor(token, out var c)) return $"{token} = {UiColorMath.ToHex(c)}";
        if (TryNumber(token, out var n)) return $"{token} = {n.ToString(CultureInfo.InvariantCulture)}";
        return TryText(token, out var t) ? $"{token} = \"{t}\"" : $"{token} = (なし)";
    }

    /// <summary>色の表の写し（UiThemeTable が重ねるとき用）。</summary>
    internal Dictionary<string, Color> CopyColors() => new(_colors, StringComparer.Ordinal);

    /// <summary>数の表の写し。</summary>
    internal Dictionary<string, float> CopyNumbers() => new(_numbers, StringComparer.Ordinal);

    /// <summary>文字列の表の写し。</summary>
    internal Dictionary<string, string> CopyTexts() => new(_texts, StringComparer.Ordinal);
}
