namespace SEED.UI;

// ============================================================
//  UiIcon.cs — 部品の先頭に出すアイコン（図形か画像）の指定と、見た目の決め方（2026-10-02。純粋な計算）
//
//  使う所: トーストの先頭のアイコン（Toast.Show(文字, アイコン)）・ダイアログの選択肢の一覧の項目のアイコン（DialogMenuItem.Icon）。
//  アイコンは部品のプレハブの子の Sprite 1 つ（Icon）に当てる（当て方は Widgets/UiIconView.cs。エンジンに触れるのはそちらだけ）。
//    画像 … ImagePath（assets:// の画像）を Sprite の画像にする。色は Color（null なら白 = 画像の色のまま。単色の白い絵を
//           文字の色で塗りたいときは Color に色を渡す）
//    図形 … Circle（塗った円）・Ring（輪。太さは size.check_border）・Square（角丸の四角。角丸は radius.checkbox）。
//           色は Color（null なら部品の既定の色 = トーストは文字の色、選択肢はアイコンの既定の色）
//  大きさは Size（0 以下なら size.icon）。アイコンと文字の間は size.icon_gap（部品が並べる）。
// ============================================================

/// <summary>図形のアイコンの形。</summary>
public enum UiIconShape
{
    /// <summary>塗った円（状態の点など）。</summary>
    Circle = 0,
    /// <summary>輪（縁だけの円。太さは size.check_border）。</summary>
    Ring = 1,
    /// <summary>角丸の四角（角丸は radius.checkbox）。</summary>
    Square = 2,
}

/// <summary>部品の先頭に出すアイコン（図形か画像）の指定。</summary>
public sealed record UiIcon
{
    /// <summary>画像（assets:// のパス。空なら図形）。</summary>
    public string ImagePath { get; init; } = string.Empty;

    /// <summary>図形の形（画像が無いときだけ使う）。</summary>
    public UiIconShape Shape { get; init; } = UiIconShape.Circle;

    /// <summary>色（null = 部品の既定。画像は白〈画像の色のまま〉、図形は部品の既定の色）。</summary>
    public Color? Color { get; init; }

    /// <summary>大きさ（一辺。キャンバスの単位。0 以下 = テーマの size.icon）。</summary>
    public float Size { get; init; }

    /// <summary>画像のアイコンか。</summary>
    public bool IsImage => !string.IsNullOrEmpty(ImagePath);

    /// <summary>画像のアイコン。</summary>
    /// <param name="path">画像（assets:// のパス）。</param>
    /// <param name="tint">色（null = 画像の色のまま）。</param>
    /// <param name="size">大きさ（0 = size.icon）。</param>
    public static UiIcon Image(string path, Color? tint = null, float size = 0f)
        => new() { ImagePath = path ?? string.Empty, Color = tint, Size = size };

    /// <summary>図形のアイコン。</summary>
    /// <param name="shape">形。</param>
    /// <param name="color">色（null = 部品の既定の色）。</param>
    /// <param name="size">大きさ（0 = size.icon）。</param>
    public static UiIcon OfShape(UiIconShape shape, Color? color = null, float size = 0f)
        => new() { Shape = shape, Color = color, Size = size };

    /// <summary>塗った円のアイコン。</summary>
    public static UiIcon Circle(Color? color = null, float size = 0f) => OfShape(UiIconShape.Circle, color, size);

    /// <summary>輪のアイコン。</summary>
    public static UiIcon Ring(Color? color = null, float size = 0f) => OfShape(UiIconShape.Ring, color, size);

    /// <summary>角丸の四角のアイコン。</summary>
    public static UiIcon Square(Color? color = null, float size = 0f) => OfShape(UiIconShape.Square, color, size);
}

/// <summary>アイコンの見た目（部品の子の Sprite へ当てる値）。</summary>
/// <param name="Visible">出すか（アイコンの指定が無ければ false）。</param>
/// <param name="IsImage">画像か（false なら図形）。</param>
/// <param name="ImagePath">画像のパス（図形なら空）。</param>
/// <param name="Shape">図形の形（画像なら使わない）。</param>
/// <param name="Color">色（画像は掛ける色・円と四角は塗り・輪は縁の色）。</param>
/// <param name="Size">大きさ（一辺。キャンバスの単位）。</param>
/// <param name="RingWidth">輪の太さ（輪のときだけ）。</param>
/// <param name="CornerRadius">四角の角丸（四角のときだけ）。</param>
public readonly record struct UiIconLook(bool Visible, bool IsImage, string ImagePath, UiIconShape Shape, Color Color, float Size,
    float RingWidth, float CornerRadius)
{
    /// <summary>出さない（アイコンの指定が無い）。</summary>
    public static UiIconLook Hidden => new(false, false, string.Empty, UiIconShape.Circle, UiColorMath.Transparent, 0f, 0f, 0f);
}

/// <summary>アイコンの指定 → 見た目。</summary>
public static class UiIconLooks
{
    /// <summary>画像の既定の色（白 = 画像の色のまま）。</summary>
    private static readonly Color ImageDefaultColor = new(1f, 1f, 1f, 1f);
    /// <summary>濃さの既定（薄めない）。</summary>
    private const float NoFade = 1f;

    /// <summary>
    /// 見た目を決める。
    /// </summary>
    /// <param name="icon">アイコンの指定（null なら出さない）。</param>
    /// <param name="theme">テーマ（size.icon・size.check_border・radius.checkbox）。</param>
    /// <param name="defaultColor">図形の既定の色（部品の文字の色など）。</param>
    /// <param name="fade">濃さ（無効のときの opacity.disabled など。1 = そのまま。範囲の外・有限でない値は 1）。</param>
    public static UiIconLook Resolve(UiIcon? icon, UiThemeData theme, Color defaultColor, float fade = NoFade)
    {
        if (icon is null) return UiIconLook.Hidden;
        float size = float.IsFinite(icon.Size) && icon.Size > 0f ? icon.Size : theme.Number(UiTokens.SizeIcon);
        var color = icon.Color ?? (icon.IsImage ? ImageDefaultColor : defaultColor);
        float alpha = float.IsFinite(fade) && fade >= 0f && fade <= NoFade ? fade : NoFade;
        color = UiColorMath.FadeAlpha(color, alpha);
        if (icon.IsImage)
            return new UiIconLook(true, true, icon.ImagePath, icon.Shape, color, size, 0f, 0f);
        float ring = icon.Shape == UiIconShape.Ring ? theme.Number(UiTokens.SizeCheckBorder) : 0f;
        float radius = icon.Shape == UiIconShape.Square ? theme.Number(UiTokens.RadiusCheckbox) : 0f;
        return new UiIconLook(true, false, string.Empty, icon.Shape, color, size, ring, radius);
    }

    /// <summary>
    /// アイコンを先頭に置いたときの文字の左端（アイコンが無ければ <paramref name="start"/> のまま）。
    /// 文字の左端 = 左の余白 ＋ アイコンの大きさ ＋ アイコンと文字の間（size.icon_gap）。
    /// </summary>
    /// <param name="look">アイコンの見た目。</param>
    /// <param name="start">左の余白（アイコンの左端）。</param>
    /// <param name="gap">アイコンと文字の間。</param>
    public static float TextStart(UiIconLook look, float start, float gap)
        => look.Visible ? start + look.Size + (float.IsFinite(gap) && gap > 0f ? gap : 0f) : start;
}
