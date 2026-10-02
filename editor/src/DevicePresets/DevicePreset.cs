// ============================================================
//  DevicePreset.cs — 端末プリセット 1 件（PC の Play で端末を模擬するときの画面の条件）
//
//  【役割】
//  editor/config/device_presets.json の "presets" の 1 要素を検証したあとの値（読み込みと検証は
//  DevicePresetJson / DevicePresetCatalog）。実行先セレクタの「PC（端末の模擬: …）」の行 1 つに対応し、
//  別ウィンドウの Play を起動するときの環境変数・起動引数（DevicePresetLaunchEnvironment）の元になる。
//
//  【値の意味】（ランタイムの読み方: runtime/src/engine/platform/screen/simulated.rs・app/text_input_hooks.rs・main.rs）
//    WidthPx / HeightPx … Play の窓の最初の大きさ（物理ピクセル。SEED_SIM_WINDOW_SIZE）
//    ScaleFactor        … 表示倍率（1 dp の画素数。SEED_SIM_SCALE_FACTOR。例: Pixel 6a は 2.625）
//    SafeArea           … 安全領域（描画面の各辺からの距離。物理ピクセル。SEED_SIM_SAFE_AREA）
//    KeyboardHeightPx   … 入力欄にフォーカスがある間に「出ている」ことにするソフトキーボードの高さ
//                         （物理ピクセル。0 = 模擬しない。SEED_SIM_KEYBOARD_HEIGHT）
//    RenderQuality      … 描画の品質のプリセット名（起動引数 --render-quality=。null = 指定しない＝PC の既定）
//
//  値はすべて record の値で比べる（実行先の行 RunTargetEntry の値の比較に含まれるため）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.DevicePresets;

/// <summary>安全領域（描画面の各辺からの距離。物理ピクセル。並びはランタイムの環境変数と同じ 左・上・右・下）。</summary>
/// <param name="Left">左。</param>
/// <param name="Top">上（ステータスバー・切り欠き）。</param>
/// <param name="Right">右。</param>
/// <param name="Bottom">下（ナビゲーションバー・ジェスチャーの帯）。</param>
public readonly record struct DeviceSafeArea(int Left, int Top, int Right, int Bottom)
{
    /// <summary>安全領域なし（画面全体）。</summary>
    public static readonly DeviceSafeArea None = new(0, 0, 0, 0);
}

/// <summary>端末プリセット 1 件（検証済み）。</summary>
public sealed record DevicePreset
{
    /// <summary>
    /// dp の数を整数へ切り捨てるときに足す小さな値（px ÷ 倍率が 359.9999… のような浮動小数の誤差で
    /// 1 つ小さく切り捨てないため。表示だけに使う）。
    /// </summary>
    private const double DpTruncationEpsilon = 1e-6;

    /// <summary>識別子（JSON の id。実行先の行の識別子 "pcsim:&lt;id&gt;" と選択の記録に使う。大文字小文字は区別しない）。</summary>
    public required string Id { get; init; }

    /// <summary>表示名（例「Pixel 6a 半分」）。</summary>
    public required string Name { get; init; }

    /// <summary>窓の幅（物理ピクセル）。</summary>
    public required int WidthPx { get; init; }

    /// <summary>窓の高さ（物理ピクセル）。</summary>
    public required int HeightPx { get; init; }

    /// <summary>表示倍率（1 dp の画素数。正の有限の実数）。</summary>
    public required double ScaleFactor { get; init; }

    /// <summary>安全領域（物理ピクセル）。</summary>
    public required DeviceSafeArea SafeArea { get; init; }

    /// <summary>ソフトキーボードの模擬の高さ（物理ピクセル。0 = 模擬しない）。</summary>
    public required int KeyboardHeightPx { get; init; }

    /// <summary>描画の品質のプリセット名（"mobile" など。null = 起動引数を付けない）。</summary>
    public string? RenderQuality { get; init; }

    /// <summary>説明（実行先の行のツールチップの最後に出す。無ければ空）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>窓の幅の dp の数（Android の Configuration.screenWidthDp と同じく切り捨て）。</summary>
    public int WidthDp => ToDp(WidthPx);

    /// <summary>窓の高さの dp の数（切り捨て）。</summary>
    public int HeightDp => ToDp(HeightPx);

    /// <summary>ソフトキーボードを模擬するか。</summary>
    public bool SimulatesKeyboard => KeyboardHeightPx > 0;

    /// <summary>画素の数を dp の数へ（切り捨て）。</summary>
    /// <param name="px">物理ピクセル。</param>
    /// <returns>dp の数。</returns>
    private int ToDp(int px) => (int)Math.Floor(px / ScaleFactor + DpTruncationEpsilon);
}
