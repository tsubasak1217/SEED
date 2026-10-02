// ============================================================
//  DevicePresetJson.cs — device_presets.json の "presets" の 1 要素の形と、その検証
//
//  【役割】
//  JSON の 1 要素をそのまま受ける入れ物（キーが欠けていれば null）と、検証して DevicePreset にする処理。
//  ファイル全体の読み込み・既定へのフォールバック・id の重複は DevicePresetCatalog が受け持つ。
//
//  【欠けたキーの既定】（データを足すときに毎回書かなくてよいもの）
//    name               → id と同じ
//    scale_factor       → 1.0（dp と画素が同じ）
//    safe_area_px       → [0, 0, 0, 0]（画面全体）
//    keyboard_height_px → 0（キーボードを模擬しない）
//    render_quality     → 指定しない（起動引数を付けない＝PC の既定の品質）
//    description        → 空
//  【無いと捨てるキー】id・width_px・height_px（窓の大きさが模擬の本体なので既定を作らない）
//  【間違った値】（負・0・数が合わない・安全領域が画面より大きい・起動引数に入れられない文字）はその 1 件だけを捨てて
//  理由を返す（1 件の書き間違いで全部の端末が消えないように）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SEEDEditor.DevicePresets;

/// <summary>device_presets.json の "presets" の 1 要素（デシリアライズ専用。アプリは <see cref="DevicePreset"/> を使う）。</summary>
public sealed class DevicePresetJson
{
    // ── 既定・上限（マジックナンバーの一元化）────────────────────

    /// <summary>scale_factor が無いときの表示倍率（dp と画素が同じ）。</summary>
    public const double DefaultScaleFactor = 1.0;

    /// <summary>keyboard_height_px が無いときの高さ（模擬しない）。</summary>
    public const int DefaultKeyboardHeightPx = 0;

    /// <summary>
    /// 窓の 1 辺の上限（物理ピクセル）。桁の打ち間違い（10800 など）を捕まえるための目安で、
    /// デスクトップの GPU の 2D テクスチャの上限としてよくある値（これを超える描画面は作れないことが多い）。
    /// </summary>
    public const int MaxWindowSidePx = 16384;

    /// <summary>safe_area_px の要素の数（左・上・右・下）。</summary>
    public const int SafeAreaEdgeCount = 4;

    /// <summary>safe_area_px の要素の番号。</summary>
    private const int LeftIndex = 0, TopIndex = 1, RightIndex = 2, BottomIndex = 3;

    /// <summary>
    /// render_quality に使える文字（英数字・ハイフン・下線）。起動引数は 1 本の文字列に空白区切りで並べて渡すので、
    /// 空白・引用符が入ると引数が割れる。ランタイムのプリセット名（desktop / mobile など）はこの範囲に収まる。
    /// </summary>
    private static readonly Regex RenderQualityPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    // ── 検証の失敗の文言 ─────────────────────────────────────

    /// <summary>id が無い。</summary>
    private const string MissingIdProblem = "id がありません";

    /// <summary>幅・高さが無い・範囲外の書式（{0}=キー、{1}=上限）。</summary>
    private const string SizeProblemFormat = "{0} が無いか、1〜{1} の整数ではありません";

    /// <summary>倍率が正の有限の実数でない書式（{0}=値）。</summary>
    private const string ScaleProblemFormat = "scale_factor={0} は正の実数ではありません";

    /// <summary>安全領域の数が合わない書式（{0}=要素の数）。</summary>
    private const string SafeAreaCountProblemFormat = "safe_area_px は [左, 上, 右, 下] の 4 つの整数にしてください（{0} 個あります）";

    /// <summary>安全領域に負の値がある。</summary>
    private const string SafeAreaNegativeProblem = "safe_area_px に負の値があります";

    /// <summary>安全領域が窓を覆い尽くす。</summary>
    private const string SafeAreaTooLargeProblem = "safe_area_px が窓の大きさ以上です（左＋右 < 幅・上＋下 < 高さ にしてください）";

    /// <summary>キーボードの高さが範囲外の書式（{0}=値）。</summary>
    private const string KeyboardProblemFormat = "keyboard_height_px={0} は 0 以上・窓の高さ未満の整数にしてください";

    /// <summary>render_quality に使えない文字がある書式（{0}=値）。</summary>
    private const string RenderQualityProblemFormat = "render_quality=\"{0}\" には英数字・ハイフン・下線だけを使ってください";

    /// <summary>キーの名前（文言用）。</summary>
    private const string WidthKey = "width_px", HeightKey = "height_px";

    // ── JSON のキー（欠けていれば null）────────────────────────

    /// <summary>識別子。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>表示名。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>窓の幅（物理ピクセル）。</summary>
    [JsonPropertyName("width_px")]
    public int? WidthPx { get; set; }

    /// <summary>窓の高さ（物理ピクセル）。</summary>
    [JsonPropertyName("height_px")]
    public int? HeightPx { get; set; }

    /// <summary>表示倍率。</summary>
    [JsonPropertyName("scale_factor")]
    public double? ScaleFactor { get; set; }

    /// <summary>安全領域 [左, 上, 右, 下]（物理ピクセル）。</summary>
    [JsonPropertyName("safe_area_px")]
    public int[]? SafeAreaPx { get; set; }

    /// <summary>ソフトキーボードの模擬の高さ（物理ピクセル）。</summary>
    [JsonPropertyName("keyboard_height_px")]
    public int? KeyboardHeightPx { get; set; }

    /// <summary>描画の品質のプリセット名。</summary>
    [JsonPropertyName("render_quality")]
    public string? RenderQuality { get; set; }

    /// <summary>説明。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // ── 検証 ─────────────────────────────────────────────────

    /// <summary>
    /// 検証して端末プリセットにする（欠けたキーは既定で埋める）。
    /// </summary>
    /// <param name="problem">捨てた理由（使えれば null）。</param>
    /// <returns>端末プリセット（使えなければ null）。</returns>
    public DevicePreset? ToPreset(out string? problem)
    {
        // ① 識別子（選択の記録のキー）。前後の空白は落とす
        var id = Id?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            problem = MissingIdProblem;
            return null;
        }

        // ② 窓の大きさ（模擬の本体なので既定を作らない）
        if (!IsSideInRange(WidthPx))
        {
            problem = string.Format(CultureInfo.InvariantCulture, SizeProblemFormat, WidthKey, MaxWindowSidePx);
            return null;
        }
        if (!IsSideInRange(HeightPx))
        {
            problem = string.Format(CultureInfo.InvariantCulture, SizeProblemFormat, HeightKey, MaxWindowSidePx);
            return null;
        }
        var width = WidthPx!.Value;
        var height = HeightPx!.Value;

        // ③ 表示倍率（ランタイムと同じく正の有限の実数だけ）
        var scale = ScaleFactor ?? DefaultScaleFactor;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            problem = string.Format(CultureInfo.InvariantCulture, ScaleProblemFormat, scale);
            return null;
        }

        // ④ 安全領域（4 つ・負でない・窓を覆い尽くさない）
        var safeArea = DeviceSafeArea.None;
        if (SafeAreaPx is { } edges)
        {
            if (edges.Length != SafeAreaEdgeCount)
            {
                problem = string.Format(CultureInfo.InvariantCulture, SafeAreaCountProblemFormat, edges.Length);
                return null;
            }
            safeArea = new DeviceSafeArea(edges[LeftIndex], edges[TopIndex], edges[RightIndex], edges[BottomIndex]);
            if (safeArea.Left < 0 || safeArea.Top < 0 || safeArea.Right < 0 || safeArea.Bottom < 0)
            {
                problem = SafeAreaNegativeProblem;
                return null;
            }
            // 足し算は long で（巨大な値の桁あふれで検査をすり抜けないように）
            if ((long)safeArea.Left + safeArea.Right >= width || (long)safeArea.Top + safeArea.Bottom >= height)
            {
                problem = SafeAreaTooLargeProblem;
                return null;
            }
        }

        // ⑤ キーボードの高さ（0 = 模擬しない。窓より高いキーボードは書き間違い）
        var keyboard = KeyboardHeightPx ?? DefaultKeyboardHeightPx;
        if (keyboard < 0 || keyboard >= height)
        {
            problem = string.Format(CultureInfo.InvariantCulture, KeyboardProblemFormat, keyboard);
            return null;
        }

        // ⑥ 描画の品質（空・空白だけなら指定しない。起動引数に入れられない文字は書き間違い）
        var quality = string.IsNullOrWhiteSpace(RenderQuality) ? null : RenderQuality.Trim();
        if (quality is not null && !RenderQualityPattern.IsMatch(quality))
        {
            problem = string.Format(CultureInfo.InvariantCulture, RenderQualityProblemFormat, quality);
            return null;
        }

        problem = null;
        return new DevicePreset
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(Name) ? id : Name.Trim(),
            WidthPx = width,
            HeightPx = height,
            ScaleFactor = scale,
            SafeArea = safeArea,
            KeyboardHeightPx = keyboard,
            RenderQuality = quality,
            Description = Description?.Trim() ?? string.Empty,
        };
    }

    /// <summary>窓の 1 辺が範囲内か（1 以上・上限以下）。</summary>
    /// <param name="side">値（無ければ null）。</param>
    /// <returns>範囲内なら true。</returns>
    private static bool IsSideInRange(int? side) => side is >= 1 and <= MaxWindowSidePx;
}
