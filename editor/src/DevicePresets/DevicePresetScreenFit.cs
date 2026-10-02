// ============================================================
//  DevicePresetScreenFit.cs — 端末の模擬の窓が画面に収まるか（純粋な判断）
//
//  【役割】
//  別ウィンドウの Play を端末の模擬で起動する前に、窓（描画面＝クライアント領域。物理ピクセル）が
//  画面の使える範囲（作業領域から窓の枠とタイトルバーを除いた大きさ。物理ピクセル）に収まるかを判断し、
//  収まらなければ警告の文言を返す。起動は止めない（はみ出した所は見えず、OS が窓を縮めることもあるので、
//  実寸のプリセットで寸法を確かめるときに気付けるようにするだけ）。
//  画面の大きさの読み取り（SystemParameters と DPI）は WPF 側（MainWindow.DevicePresets.cs）が行う。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System.Globalization;

namespace SEEDEditor.DevicePresets;

/// <summary>端末の模擬の窓が画面に収まるかの判断。</summary>
public static class DevicePresetScreenFit
{
    /// <summary>
    /// 収まらないときの警告の書式（{0}=端末の名前、{1}=窓の大きさ、{2}=使える範囲の大きさ）。
    /// トーストに出すので短く、確かめ方は Output の行（呼び出し側）に書く。
    /// </summary>
    private const string WarningFormat =
        "端末の模擬「{0}」の窓 {1} px は画面（使える範囲 {2} px）に収まりません。はみ出すか、OS が窓を縮めることがあります";

    /// <summary>
    /// 窓が使える範囲に収まるか確かめる。
    /// </summary>
    /// <param name="preset">端末。</param>
    /// <param name="availableWidthPx">窓の中身に使える幅（物理ピクセル）。</param>
    /// <param name="availableHeightPx">窓の中身に使える高さ（物理ピクセル）。</param>
    /// <returns>収まらなければ警告の文言、収まれば null。</returns>
    public static string? Warn(DevicePreset preset, int availableWidthPx, int availableHeightPx)
    {
        if (preset.WidthPx <= availableWidthPx && preset.HeightPx <= availableHeightPx) return null;
        return string.Format(CultureInfo.InvariantCulture, WarningFormat,
            preset.Name,
            DevicePresetFormat.Size(preset.WidthPx, preset.HeightPx),
            DevicePresetFormat.Size(availableWidthPx, availableHeightPx));
    }
}
