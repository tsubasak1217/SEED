using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  UiThemeTable.cs — テーマの表を重ねて作る途中の入れ物（W2-9。純粋な計算）
//
//  解決（UiThemeResolver）は、根（組み込みの既定のテーマ）から葉のテーマへ順に、各テーマの層を上へ重ねる:
//    明暗に依らない値 → その明暗の節 → 種の色から作った値（UiSeedColors。このテーマが明示していないトークンだけ）
//  種の色は、このテーマの面の色（明示していればそれ・無ければ基のテーマの面）に合わせて作るので最後に作る（明示が勝つ）。
//  重ね終えたら Freeze で変わらない表（UiThemeData）にする。同じ名前のトークンの型が層で変わったら（アプリ独自のトークン）
//  後の層の型だけを残す。
// ============================================================

/// <summary>テーマの表を重ねて作る途中の入れ物。</summary>
internal sealed class UiThemeTable
{
    /// <summary>色。</summary>
    private readonly Dictionary<string, Color> _colors;
    /// <summary>数。</summary>
    private readonly Dictionary<string, float> _numbers;
    /// <summary>文字列。</summary>
    private readonly Dictionary<string, string> _texts;

    private UiThemeTable(Dictionary<string, Color> colors, Dictionary<string, float> numbers, Dictionary<string, string> texts)
    {
        _colors = colors;
        _numbers = numbers;
        _texts = texts;
    }

    /// <summary>空の入れ物（<paramref name="data"/> があればその写しから始める）。</summary>
    public static UiThemeTable From(UiThemeData? data)
        => data is null
            ? new UiThemeTable(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal))
            : new UiThemeTable(data.CopyColors(), data.CopyNumbers(), data.CopyTexts());

    /// <summary>色を引く（種の色の配色が面の色を見るため）。</summary>
    public bool TryColor(string token, out Color color) => _colors.TryGetValue(token, out color);

    /// <summary>
    /// 1 つのテーマの層を重ねる（明暗に依らない値 → その明暗の節 → 種の色から作った値〈明示していないトークンだけ〉）。
    /// </summary>
    /// <param name="source">テーマの JSON の読み込みの結果。</param>
    /// <param name="brightness">解決する明暗。</param>
    public void ApplyLayer(UiThemeSource source, UiBrightness brightness)
    {
        var section = source.Section(brightness);
        Overlay(source.Base);
        Overlay(section);
        if (source.SeedColor is not { } seed) return;
        var surface = TryColor(UiTokens.ColorSurface, out var s) ? s : UiSeedColors.DefaultSurface(brightness);
        foreach (var (token, value) in UiSeedColors.Derive(seed, brightness, surface))
        {
            bool explicitlySet = source.Base.ContainsKey(token) || (section is not null && section.ContainsKey(token));
            if (!explicitlySet) Set(token, value);
        }
    }

    /// <summary>値を上へ重ねる（null なら何もしない）。</summary>
    public void Overlay(IReadOnlyDictionary<string, UiThemeValue>? values)
    {
        if (values is null) return;
        foreach (var (token, value) in values) Set(token, value);
    }

    /// <summary>1 つの値を入れる（ほかの型の同じ名前は消す）。</summary>
    public void Set(string token, UiThemeValue value)
    {
        _colors.Remove(token);
        _numbers.Remove(token);
        _texts.Remove(token);
        switch (value.Kind)
        {
            case UiTokenKind.Color:
                _colors[token] = value.Color;
                break;
            case UiTokenKind.Text:
                _texts[token] = value.Text;
                break;
            default:
                _numbers[token] = value.Number;
                break;
        }
    }

    /// <summary>変わらない表にする（以後この入れ物は使わない）。</summary>
    public UiThemeData Freeze(string name, UiBrightness brightness, IReadOnlyList<string> warnings)
        => new(name, brightness, _colors, _numbers, _texts, warnings);
}
