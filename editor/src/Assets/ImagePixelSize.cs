using System.Globalization;

namespace SEEDEditor.Assets;

/// <summary>
/// 画像 1 枚のピクセル寸法（幅×高さ）と、その表示文字列の作り方。
///
/// 「どう表示するか」をここに集約しているので、プロジェクトパネルのキャプションでも
/// ツールチップでも、同じ値が同じ書式で出る。
///
/// WPF に依存しない値と整形だけを置くこと
/// （editor/tests/ProjectPanelLogicTests が直接リンクして検証している）。
/// </summary>
/// <param name="Width">幅（ピクセル）。</param>
/// <param name="Height">高さ（ピクセル）。</param>
public readonly record struct ImagePixelSize(int Width, int Height)
{
    /// <summary>幅と高さの間に挟む記号（全角の乗算記号。等幅で見栄えが崩れにくい）。</summary>
    private const string DimensionSeparator = "×";

    /// <summary>ツールチップで寸法の後ろに付ける単位。</summary>
    private const string PixelUnitSuffix = " px";

    /// <summary>幅・高さともに正の値か（0 以下は「取得できなかった」と同義）。</summary>
    public bool IsValid => Width > 0 && Height > 0;

    /// <summary>
    /// タイルのキャプション用の短い表記（例 <c>1024×512</c>）。
    /// </summary>
    public string ToCaption()
        => Width.ToString(CultureInfo.InvariantCulture)
         + DimensionSeparator
         + Height.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// ツールチップ用の表記（例 <c>1024×512 px</c>）。単位を付けて意味を明示する。
    /// </summary>
    public string ToTooltipText() => ToCaption() + PixelUnitSuffix;
}
