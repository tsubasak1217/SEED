using System.Globalization;

namespace SEED.UI;

// ============================================================
//  UiThemeValue.cs — テーマの 1 つの値（色・数・文字列のどれか。W2-9。純粋な値）
//
//  テーマの JSON の葉の値を型つきで持つ（読み込み UiThemeSource → 解決 UiThemeResolver → 表 UiThemeData）。
//  曲線は成分ごとの数になるので、値の型は色・数・文字列の 3 つ。
// ============================================================

/// <summary>テーマの 1 つの値。</summary>
public readonly struct UiThemeValue
{
    /// <summary>型（Color・Number・Text のどれか。Curve にはならない）。</summary>
    public UiTokenKind Kind { get; }
    /// <summary>色（線形。Kind == Color のとき）。</summary>
    public Color Color { get; }
    /// <summary>数（Kind == Number のとき）。</summary>
    public float Number { get; }
    /// <summary>文字列（Kind == Text のとき）。</summary>
    public string Text { get; }

    private UiThemeValue(UiTokenKind kind, Color color, float number, string text)
    {
        Kind = kind;
        Color = color;
        Number = number;
        Text = text;
    }

    /// <summary>色の値。</summary>
    public static UiThemeValue FromColor(Color color) => new(UiTokenKind.Color, color, 0f, string.Empty);

    /// <summary>数の値。</summary>
    public static UiThemeValue FromNumber(float number) => new(UiTokenKind.Number, SEED.Color.White, number, string.Empty);

    /// <summary>文字列の値。</summary>
    public static UiThemeValue FromText(string text) => new(UiTokenKind.Text, SEED.Color.White, 0f, text ?? string.Empty);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        UiTokenKind.Color => UiColorMath.ToHex(Color),
        UiTokenKind.Text => "\"" + Text + "\"",
        _ => Number.ToString(CultureInfo.InvariantCulture),
    };
}
