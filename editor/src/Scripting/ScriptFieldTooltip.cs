using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SEEDEditor.Scripting;

/// <summary>
/// スクリプトフィールド行のラベルに出すツールチップを組み立てる。
///
/// ラベル列は幅が限られており長い名前は「…」で切れるため、
/// ツールチップでは必ず「省略していない完全なラベル」を先頭に太字で出す。
/// その下に、説明（[Tooltip] 属性 → <c>/// &lt;summary&gt;</c> の順）、
/// 見出し（[Header]）、最後に実体の情報（フィールド名 : 型名）を添える。
///
/// 背景はエディタ既定のツールチップ（明色）なので、文字色は暗色で指定する。
/// </summary>
internal static class ScriptFieldTooltip
{
    // ── レイアウト・配色定数 ─────────────────────────────────

    /// <summary>本文の折り返し幅（px）。長い説明でも画面を覆わない程度に抑える。</summary>
    private const double MaxTooltipWidth = 420;

    /// <summary>ツールチップが出るまでの待ち時間（ms）。触れてすぐ邪魔にならない程度。</summary>
    public const int InitialShowDelayMs = 400;

    /// <summary>ツールチップの表示継続時間（ms）。説明を読み切れるよう長めにする。</summary>
    public const int ShowDurationMs = 60000;

    /// <summary>見出し（完全なラベル）のフォントサイズ（px）。</summary>
    private const double TitleFontSize = 12;

    /// <summary>本文・補足のフォントサイズ（px）。</summary>
    private const double BodyFontSize = 11;

    /// <summary>ブロック間の縦の間隔（px）。</summary>
    private const double BlockSpacing = 4;

    private static readonly SolidColorBrush BrushTitle = new(Color.FromRgb(0x11, 0x11, 0x11));
    private static readonly SolidColorBrush BrushBody  = new(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly SolidColorBrush BrushMeta  = new(Color.FromRgb(0x70, 0x70, 0x70));

    /// <summary>
    /// フィールド 1 件分のツールチップ内容を作る。
    /// </summary>
    /// <param name="field">フィールド情報（ラベル・説明・型）。</param>
    /// <returns>ToolTip プロパティへそのまま設定できる要素。</returns>
    public static object Build(ScriptFieldInfo field)
    {
        var panel = new StackPanel { MaxWidth = MaxTooltipWidth };

        // 1. 省略していない完全なラベル（ラベル列が「…」で切れていても必ず読める）
        panel.Children.Add(new TextBlock
        {
            Text         = field.Label,
            FontWeight   = FontWeights.Bold,
            FontSize     = TitleFontSize,
            Foreground   = BrushTitle,
            TextWrapping = TextWrapping.Wrap,
        });

        // 2. [Header] があれば文脈として添える
        if (!string.IsNullOrWhiteSpace(field.Header))
            panel.Children.Add(MakeLine($"［{field.Header}］", BrushMeta, BlockSpacing));

        // 3. 説明: 明示指定の [Tooltip] を優先し、無ければソースの /// <summary> を使う。
        //    両方あって内容が違う場合は補足として summary も続けて出す。
        if (!string.IsNullOrWhiteSpace(field.Tooltip))
            panel.Children.Add(MakeLine(field.Tooltip!, BrushBody, BlockSpacing));

        if (!string.IsNullOrWhiteSpace(field.Summary) && field.Summary != field.Tooltip)
            panel.Children.Add(MakeLine(field.Summary!, BrushBody, BlockSpacing));

        // 4. 実体の情報（説明が無いときはこれが手がかりになる）
        panel.Children.Add(MakeLine(
            $"{field.Field.Name} : {FriendlyTypeName(field)}", BrushMeta, BlockSpacing));

        return panel;
    }

    /// <summary>ツールチップ内の 1 ブロック（折り返し付きテキスト）を作る。</summary>
    private static TextBlock MakeLine(string text, Brush brush, double topMargin) => new()
    {
        Text         = text,
        Foreground   = brush,
        FontSize     = BodyFontSize,
        TextWrapping = TextWrapping.Wrap,
        Margin       = new Thickness(0, topMargin, 0, 0),
    };

    /// <summary>
    /// 型名を読みやすい形にする。配列フィールドは要素型を添えて「配列であること」が分かるようにする。
    /// </summary>
    private static string FriendlyTypeName(ScriptFieldInfo field)
    {
        if (field.Array is { } arr)
            return arr.IsList ? $"List<{arr.ElementType.Name}>" : $"{arr.ElementType.Name}[]";
        return field.Field.FieldType.Name;
    }
}
