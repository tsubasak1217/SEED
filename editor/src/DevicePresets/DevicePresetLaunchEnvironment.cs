// ============================================================
//  DevicePresetLaunchEnvironment.cs — 端末プリセット → ランタイムの起動に足す環境変数と起動引数（純粋な関数）
//
//  【役割】実行先「PC（端末の模擬: …）」で別ウィンドウの Play を起動するとき、RuntimeManager が従来の起動
//  （エディタの PID・IPC のパイプ・アセットの場所・シーン）に足すものを、端末プリセット 1 件から組み立てる。
//
//  【組み立てるもの】（ランタイムの読み方は右の場所。正典は docs/editor_device_presets.md §3）
//    SEED_SIM_WINDOW_SIZE     = "幅x高さ"          … Play の窓の最初の大きさ（app/app_init.rs・platform/screen/simulated.rs）
//    SEED_SIM_SCALE_FACTOR    = "2.625" など        … 表示倍率（app/screen_publish.rs）
//    SEED_SIM_SAFE_AREA       = "左,上,右,下"       … 安全領域（物理ピクセル。platform/screen/simulated.rs）
//    SEED_SIM_KEYBOARD_HEIGHT = "979" など          … キーボードの模擬の高さ（app/text_input_hooks.rs）。
//                                                     0 のときは変数ごと消す（エディタ自身の環境から受け継いだ値を残さない）
//    起動引数 --render-quality=<名前>               … 描画の品質（main.rs）。render_quality が無ければ付けない
//  4 つの環境変数は、エディタ自身の環境に同じ名前があっても必ずプリセットの値で上書き（または消す）する
//  （プリセットを選んだら、その条件だけで動くように）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using SEEDEditor.Runtime;

namespace SEEDEditor.DevicePresets;

/// <summary>端末プリセットから、ランタイムの起動に足す環境変数と起動引数を組み立てる。</summary>
public static class DevicePresetLaunchEnvironment
{
    // ── ランタイムが読む名前（runtime/src の定数と同じ綴り）──────────

    /// <summary>Play の窓の最初の大きさ（"幅x高さ"。物理ピクセル）。</summary>
    public const string WindowSizeVariable = "SEED_SIM_WINDOW_SIZE";

    /// <summary>表示倍率（正の実数）。</summary>
    public const string ScaleFactorVariable = "SEED_SIM_SCALE_FACTOR";

    /// <summary>安全領域（"左,上,右,下"。物理ピクセル）。</summary>
    public const string SafeAreaVariable = "SEED_SIM_SAFE_AREA";

    /// <summary>ソフトキーボードの模擬の高さ（物理ピクセル）。</summary>
    public const string KeyboardHeightVariable = "SEED_SIM_KEYBOARD_HEIGHT";

    /// <summary>描画の品質の起動引数の頭。</summary>
    public const string RenderQualityArgumentPrefix = "--render-quality=";

    // ── 値の書式 ─────────────────────────────────────────────

    /// <summary>窓の大きさの書式（{0}=幅、{1}=高さ。ランタイムの区切りは小文字の x）。</summary>
    private const string WindowSizeFormat = "{0}x{1}";

    /// <summary>安全領域の書式（{0}=左、{1}=上、{2}=右、{3}=下）。</summary>
    private const string SafeAreaFormat = "{0},{1},{2},{3}";

    /// <summary>ログの説明の書式（{0}=端末の名前）。</summary>
    private const string LabelFormat = "端末の模擬: {0}";

    /// <summary>
    /// 端末プリセットから、起動に足す環境変数と起動引数を組み立てる。
    /// </summary>
    /// <param name="preset">端末プリセット（検証済み）。</param>
    /// <returns>起動に足すもの（常駐の Play の使い回しの判定に使う Key も入る）。</returns>
    public static RuntimeLaunchOverrides Build(DevicePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var area = preset.SafeArea;
        var variables = new List<RuntimeLaunchVariable>
        {
            new(WindowSizeVariable, string.Format(CultureInfo.InvariantCulture, WindowSizeFormat, preset.WidthPx, preset.HeightPx)),
            new(ScaleFactorVariable, DevicePresetFormat.Number(preset.ScaleFactor)),
            new(SafeAreaVariable, string.Format(CultureInfo.InvariantCulture, SafeAreaFormat, area.Left, area.Top, area.Right, area.Bottom)),
            // 0 は「模擬しない」。ランタイムも 0 以下を模擬しないと読むが、高さ 0 の模擬のログを出してしまうので変数ごと消す
            new(KeyboardHeightVariable, preset.SimulatesKeyboard
                ? preset.KeyboardHeightPx.ToString(CultureInfo.InvariantCulture)
                : null),
        };

        var arguments = new List<string>();
        if (preset.RenderQuality is { } quality) arguments.Add(RenderQualityArgumentPrefix + quality);

        return new RuntimeLaunchOverrides(string.Format(LabelFormat, preset.Name), variables, arguments);
    }
}
