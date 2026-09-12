using System;

namespace SEEDEditor.Panels.Inspector;

/// <summary>
/// 「画像比率に設定」でどちらの辺を保つかの基準。
///
/// メニュー表記との対応:
///   <see cref="KeepHeight"/> … 「縦基準（高さを保ち幅を合わせる）」
///   <see cref="KeepWidth"/>  … 「横基準（幅を保ち高さを合わせる）」
/// </summary>
public enum ImageAspectAxis
{
    /// <summary>高さを保ち、幅を元画像の比率に合わせる。</summary>
    KeepHeight,

    /// <summary>幅を保ち、高さを元画像の比率に合わせる。</summary>
    KeepWidth,
}

/// <summary>幅・高さの組（キャンバスユニット）。</summary>
/// <param name="Width">幅。</param>
/// <param name="Height">高さ。</param>
public readonly record struct SizePair(float Width, float Height);

/// <summary>
/// 元画像のピクセル寸法から、片方の辺を保ったままもう片方を縦横比に合わせる計算。
///
/// <para>
/// WPF に依存しない純粋関数だけを置く（画像のデコードは
/// <c>ImageSizeCache</c>、UI の組み立ては <c>InspectorPanel.ImageAspect.cs</c> の担当）。
/// こうしておくと <c>editor/tests/InspectorLogicTests</c> から直接検証でき、
/// 比率計算の退行がビルド時ではなくテストで捕まる。
/// </para>
/// </summary>
public static class ImageAspectCalculator
{
    /// <summary>
    /// 片方の辺を保ったまま、もう片方を元画像の縦横比に合わせた寸法を求める。
    ///
    /// <para><b>符号の扱い</b>: スプライトの幅・高さは負値で反転表示に使われることがある。
    /// 比率は「保つ辺の絶対値」から求め、書き換える辺の符号は元の値の符号を維持する
    /// （元が 0 のときは正）。単純に比率を掛けると、保つ辺が負のときに
    /// 書き換える辺まで巻き添えで反転してしまうため。</para>
    /// </summary>
    /// <param name="imagePixelWidth">元画像の幅（ピクセル）。</param>
    /// <param name="imagePixelHeight">元画像の高さ（ピクセル）。</param>
    /// <param name="current">現在の幅・高さ。</param>
    /// <param name="axis">どちらの辺を保つか。</param>
    /// <param name="result">計算後の幅・高さ。失敗時は <paramref name="current"/> と同じ値。</param>
    /// <returns>
    /// 計算できたら true。元画像の寸法が 0 以下、保つ辺が 0、値が非有限のときは
    /// 比率が定義できないので false（呼び出し側は何も書き換えない）。
    /// </returns>
    public static bool TryApply(
        int             imagePixelWidth,
        int             imagePixelHeight,
        SizePair        current,
        ImageAspectAxis axis,
        out SizePair    result)
    {
        result = current;

        // 元画像の寸法が取れていなければ比率が定義できない
        if (imagePixelWidth <= 0 || imagePixelHeight <= 0) return false;

        // 現在値が非有限（NaN / ∞）なら計算しない
        if (!IsFinite(current.Width) || !IsFinite(current.Height)) return false;

        // 元画像の縦横比（幅 ÷ 高さ）
        float imageAspect = (float)imagePixelWidth / imagePixelHeight;

        switch (axis)
        {
            case ImageAspectAxis.KeepHeight:
            {
                // 高さを保ち、幅を「高さ × 比率」にする
                float keep = MathF.Abs(current.Height);
                if (keep <= 0f) return false;
                float magnitude = keep * imageAspect;
                result = new SizePair(ApplySign(magnitude, current.Width), current.Height);
                return IsFinite(result.Width);
            }
            case ImageAspectAxis.KeepWidth:
            {
                // 幅を保ち、高さを「幅 ÷ 比率」にする
                float keep = MathF.Abs(current.Width);
                if (keep <= 0f) return false;
                float magnitude = keep / imageAspect;
                result = new SizePair(current.Width, ApplySign(magnitude, current.Height));
                return IsFinite(result.Height);
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// 大きさ <paramref name="magnitude"/>（非負）に、元の値 <paramref name="original"/> の符号を与える。
    /// 元が 0 のときは正とみなす（反転表示の意図が無いため）。
    /// </summary>
    private static float ApplySign(float magnitude, float original)
        => original < 0f ? -magnitude : magnitude;

    /// <summary>NaN / ±∞ でないことを判定する。</summary>
    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
