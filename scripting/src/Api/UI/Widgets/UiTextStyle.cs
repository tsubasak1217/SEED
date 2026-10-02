namespace SEED.UI;

// ============================================================
//  UiTextStyle.cs — 部品の文字へテーマの文字の大きさ・書体・太さを当てる（W2-9。全部品で同じ当て方にする）
//
//  部品の ApplyLook が文字（SEED.Text）ごとに呼ぶ。書体（font.family）と太さ（font.weight・font.weight_title）は
//  前の値と違うときだけ書く（同じ値の書き直しで文字の組み直しを起こさない）。大きさのトークンが空なら大きさは変えない。
// ============================================================

/// <summary>部品の文字へテーマの文字の見た目を当てる。</summary>
public static class UiTextStyle
{
    /// <summary>太さを同じとみなす差。</summary>
    private const float WeightEpsilon = 1e-4f;

    /// <summary>
    /// 文字へ大きさ・書体・太さを当てる。
    /// </summary>
    /// <param name="text">当てる文字。</param>
    /// <param name="theme">テーマ。</param>
    /// <param name="sizeToken">
    /// 大きさの指定（トークンの名前か数〈"18"〉。2026-10-02 から数も書ける。空・読めない指定なら大きさは変えない。読み方は UiTextSize）。
    /// </param>
    /// <param name="weightToken">太さのトークン（既定は font.weight。見出しは font.weight_title）。</param>
    public static void Apply(Text text, UiThemeData theme, string sizeToken, string weightToken = UiTokens.FontWeight)
    {
        if (UiTextSize.TryResolve(theme, sizeToken, out float size) && text.FontSize != size) text.FontSize = size;
        ApplyFont(text, theme, weightToken);
    }

    /// <summary>書体と太さだけを当てる（大きさを部品が別に決めるとき）。</summary>
    public static void ApplyFont(Text text, UiThemeData theme, string weightToken = UiTokens.FontWeight)
    {
        string family = theme.Text(UiTokens.FontFamily);
        if (text.FontPath != family) text.FontPath = family;
        float weight = theme.Number(weightToken);
        if (System.MathF.Abs(text.Weight - weight) > WeightEpsilon) text.Weight = weight;
    }
}
