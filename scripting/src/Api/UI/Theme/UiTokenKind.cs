namespace SEED.UI;

// ============================================================
//  UiTokenKind.cs — テーマのトークンの型（W2-9。docs/ui_theme.md §8）
//
//  テーマの JSON の値の書き方はトークンの型で決まる。型は UiTokenCatalog の表が持ち、読み込み（UiThemeSource）は
//  型に合わない値を警告して読まない（既定・基のテーマの値が残る）。
// ============================================================

/// <summary>テーマのトークンの型。</summary>
public enum UiTokenKind
{
    /// <summary>色（JSON は sRGB の "#RRGGBB" / "#RRGGBBAA"。読み込みで線形へ直す）。</summary>
    Color = 0,
    /// <summary>数（長さ・時間・濃さ・割合・数・レイヤー）。</summary>
    Number = 1,
    /// <summary>動きの曲線（JSON は {"x1","y1","x2","y2"} の 4 つの数＝CSS の cubic-bezier。成分ごとの数のトークンになる）。</summary>
    Curve = 2,
    /// <summary>文字列（書体の assets:// のパス。空 = 組み込み）。</summary>
    Text = 3,
}
