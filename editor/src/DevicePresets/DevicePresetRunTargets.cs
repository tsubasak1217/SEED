// ============================================================
//  DevicePresetRunTargets.cs — 端末プリセット → 実行先セレクタの「PC（端末の模擬: …）」の行（純粋な処理）
//
//  【役割】
//  端末プリセット 1 件を実行先の行（RunTargetEntry。種類は PcSimulated）にする。並べるのは
//  RunTargetCatalogBuilder（PC の行の直後に、JSON の順で）。行の識別子は "pcsim:<プリセットの id>" で、
//  プロジェクトの実行状態の editor_target（RunTargetSelectionStore）にそのまま記録される。
//  "pcsim:" で始まるので、Android の端末のシリアル（adb の serial）・"pc"・"auto" と混ざらない。
//
//  この行は PC の仲間（PlayBarPolicy は PC の Play と同じ表示・動き）で、違いは起動のしかただけ
//  （別ウィンドウの Play を端末の模擬の環境変数付きで起動する。MainWindow.DevicePresets.cs）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Globalization;
using SEEDEditor.AndroidRun;

namespace SEEDEditor.DevicePresets;

/// <summary>端末プリセットの実行先の行。</summary>
public static class DevicePresetRunTargets
{
    /// <summary>行の識別子の頭（端末のシリアル・"pc"・"auto" と混ざらないように）。</summary>
    public const string IdPrefix = "pcsim:";

    /// <summary>行のアイコン（Icons.xaml のキー。PC の画面に端末を写す絵）。</summary>
    public const string IconKey = "Icon.Platform.DeviceSimulation";

    /// <summary>行の文言の書式（{0}=端末の名前。例「PC（端末の模擬: Pixel 6a 半分）」）。</summary>
    private const string TextFormat = "PC（端末の模擬: {0}）";

    /// <summary>ツールチップの頭の書式（{0}=端末の名前）。</summary>
    private const string ToolTipHeadFormat =
        "この PC で、{0} を模擬した別ウィンドウの Play を起動します" +
        "（「ウィンドウを出してプレイ」がオフでも、この行のときだけ別プロセス。設定は変えません）。";

    /// <summary>ツールチップの区切り（段落）。</summary>
    private const string ParagraphSeparator = "\n";

    /// <summary>
    /// 端末プリセットの行の識別子（"pcsim:&lt;id&gt;"）。
    /// </summary>
    /// <param name="preset">端末。</param>
    /// <returns>識別子。</returns>
    public static string TargetId(DevicePreset preset) => IdPrefix + preset.Id;

    /// <summary>
    /// 識別子が端末プリセットの行のものか（"pcsim:" で始まるか。大文字小文字は区別しない）。
    /// </summary>
    /// <param name="targetId">識別子（null 可）。</param>
    /// <returns>端末プリセットの行なら true。</returns>
    public static bool IsTargetId(string? targetId) =>
        targetId is not null && targetId.StartsWith(IdPrefix, DevicePresetCatalog.IdComparison);

    /// <summary>
    /// 識別子が指す行か（"pcsim:" の後ろのプリセットの id を大文字小文字を区別せずに比べる）。
    /// </summary>
    /// <param name="entry">行。</param>
    /// <param name="targetId">識別子。</param>
    /// <returns>同じ行なら true。</returns>
    public static bool Matches(RunTargetEntry entry, string? targetId) =>
        entry.Kind == RunTargetKind.PcSimulated && string.Equals(entry.Id, targetId, DevicePresetCatalog.IdComparison);

    /// <summary>
    /// 端末プリセット 1 件の行を作る。
    /// </summary>
    /// <param name="preset">端末。</param>
    /// <returns>行（いつでも選べる）。</returns>
    public static RunTargetEntry FromPreset(DevicePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var toolTip = string.Format(CultureInfo.InvariantCulture, ToolTipHeadFormat, preset.Name)
                      + ParagraphSeparator + DevicePresetFormat.Details(preset);
        if (!string.IsNullOrWhiteSpace(preset.Description)) toolTip += ParagraphSeparator + preset.Description;

        return new RunTargetEntry
        {
            Id = TargetId(preset),
            Kind = RunTargetKind.PcSimulated,
            Name = preset.Name,
            Text = string.Format(CultureInfo.InvariantCulture, TextFormat, preset.Name),
            ToolTip = toolTip,
            CanRun = true,
            IconKey = IconKey,
            DevicePreset = preset,
        };
    }
}
