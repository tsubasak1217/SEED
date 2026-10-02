// ============================================================
//  DevicePresetFormat.cs — 端末プリセットを画面とログに出すときの文言（純粋な処理）
//
//  【役割】実行先の行のツールチップ・プレイバーのツールチップの「模擬: …」・画面に収まらない警告・Output の行が
//  同じ書き方で端末を表すように、文言の組み立てを 1 か所に集める。
//  数は必ずカルチャに依らない書き方（小数点は "."）にする（ランタイムへ渡す環境変数と同じ見た目にするため）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Globalization;

namespace SEEDEditor.DevicePresets;

/// <summary>端末プリセットの文言。</summary>
public static class DevicePresetFormat
{
    /// <summary>大きさの書式（{0}=幅、{1}=高さ。例「540×1200」）。</summary>
    private const string SizeFormat = "{0}×{1}";

    /// <summary>倍率の書式（{0}=倍率。例「×1.3125」）。</summary>
    private const string ScaleFormat = "×{0}";

    /// <summary>要約の書式（{0}=名前、{1}=大きさ、{2}=倍率。例「Pixel 6a 半分（540×1200・×1.3125）」）。</summary>
    private const string SummaryFormat = "{0}（{1}・{2}）";

    /// <summary>プレイバーなどに出す模擬の注記の書式（{0}=要約）。</summary>
    private const string SimulationNoteFormat = "模擬: {0}";

    /// <summary>窓の行の書式（{0}=大きさ px、{1}=大きさ dp、{2}=倍率）。</summary>
    private const string WindowLineFormat = "窓: {0} px（{1} dp）・表示倍率 {2}";

    /// <summary>安全領域の行の書式（{0}=左、{1}=上、{2}=右、{3}=下）。</summary>
    private const string SafeAreaLineFormat = "安全領域: 左 {0}・上 {1}・右 {2}・下 {3} px";

    /// <summary>キーボードの行の書式（{0}=高さ）。</summary>
    private const string KeyboardLineFormat = "キーボードの模擬: 高さ {0} px（入力欄にフォーカスがある間）";

    /// <summary>キーボードを模擬しないときの行。</summary>
    private const string KeyboardOffLine = "キーボードの模擬: なし";

    /// <summary>描画の品質の行の書式（{0}=プリセット名）。</summary>
    private const string RenderQualityLineFormat = "描画の品質: {0}（--render-quality）";

    /// <summary>描画の品質を指定しないときの行。</summary>
    private const string RenderQualityDefaultLine = "描画の品質: 指定しない（PC の既定）";

    /// <summary>行の区切り。</summary>
    private const string LineSeparator = "\n";

    /// <summary>
    /// 数をカルチャに依らない最短の書き方にする（2.625 → "2.625"、1.0 → "1"。小数点は常に "."）。
    /// </summary>
    /// <param name="value">数。</param>
    /// <returns>文字列。</returns>
    public static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>大きさ（例「540×1200」）。</summary>
    /// <param name="width">幅。</param>
    /// <param name="height">高さ。</param>
    /// <returns>文字列。</returns>
    public static string Size(int width, int height) =>
        string.Format(CultureInfo.InvariantCulture, SizeFormat, width, height);

    /// <summary>倍率（例「×1.3125」）。</summary>
    /// <param name="scaleFactor">倍率。</param>
    /// <returns>文字列。</returns>
    public static string Scale(double scaleFactor) => string.Format(CultureInfo.InvariantCulture, ScaleFormat, Number(scaleFactor));

    /// <summary>要約（例「Pixel 6a 半分（540×1200・×1.3125）」）。</summary>
    /// <param name="preset">端末。</param>
    /// <returns>文字列。</returns>
    public static string Summary(DevicePreset preset) =>
        string.Format(CultureInfo.InvariantCulture, SummaryFormat, preset.Name, Size(preset.WidthPx, preset.HeightPx), Scale(preset.ScaleFactor));

    /// <summary>プレイバーのツールチップなどに出す注記（例「模擬: Pixel 6a 半分（540×1200・×1.3125）」）。</summary>
    /// <param name="preset">端末。</param>
    /// <returns>文字列。</returns>
    public static string SimulationNote(DevicePreset preset) => string.Format(SimulationNoteFormat, Summary(preset));

    /// <summary>
    /// 条件の詳しい説明（窓・安全領域・キーボード・描画の品質の 4 行）。実行先の行のツールチップに出す。
    /// </summary>
    /// <param name="preset">端末。</param>
    /// <returns>複数行の文字列。</returns>
    public static string Details(DevicePreset preset)
    {
        var area = preset.SafeArea;
        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, WindowLineFormat,
                Size(preset.WidthPx, preset.HeightPx), Size(preset.WidthDp, preset.HeightDp), Scale(preset.ScaleFactor)),
            string.Format(CultureInfo.InvariantCulture, SafeAreaLineFormat, area.Left, area.Top, area.Right, area.Bottom),
            preset.SimulatesKeyboard
                ? string.Format(CultureInfo.InvariantCulture, KeyboardLineFormat, preset.KeyboardHeightPx)
                : KeyboardOffLine,
            preset.RenderQuality is { } quality
                ? string.Format(CultureInfo.InvariantCulture, RenderQualityLineFormat, quality)
                : RenderQualityDefaultLine,
        };
        return string.Join(LineSeparator, lines);
    }
}
