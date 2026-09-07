// ============================================================
//  AnimationTimelineConstants.Colors.cs — タイムラインの配色定数
//
//  AnimationTimelineConstants の partial。WPF の Color 型を使う定義だけを
//  こちらへ分けてあるのは、寸法・挙動の定数（本体ファイル）を WPF 非依存に保ち、
//  純ロジック（AnimDopeSheetLayout / AnimTimelineZoom）ごと editor/tests から
//  リンクできるようにするため。
// ============================================================

using System.Windows.Media;

namespace SEEDEditor.Panels.AnimationTimeline;

internal static partial class AnimationTimelineConstants
{
    public static readonly Color BackgroundColor      = Color.FromRgb(0x1E, 0x1E, 0x1E);
    public static readonly Color ToolbarBackground     = Color.FromRgb(0x2D, 0x2D, 0x2D);
    public static readonly Color RulerBackground        = Color.FromRgb(0x25, 0x25, 0x25);
    public static readonly Color TrackRowEvenBackground = Color.FromRgb(0x22, 0x22, 0x22);
    public static readonly Color TrackRowOddBackground  = Color.FromRgb(0x1E, 0x1E, 0x1E);
    public static readonly Color TrackRowSelectedBackground = Color.FromRgb(0x25, 0x3A, 0x50);
    public static readonly Color GridLineColor           = Color.FromRgb(0x33, 0x33, 0x33);
    public static readonly Color RulerTickColor          = Color.FromRgb(0x77, 0x77, 0x77);
    public static readonly Color RulerTextColor          = Color.FromRgb(0xAA, 0xAA, 0xAA);
    public static readonly Color PlayheadColor           = Color.FromRgb(0xE5, 0xC0, 0x7B);
    public static readonly Color KeyDiamondFill          = Color.FromRgb(0x61, 0xAF, 0xEF);
    public static readonly Color KeyDiamondSelectedFill  = Color.FromRgb(0xE5, 0xC0, 0x7B);
    /// <summary>選択中トラックのキー◆の塗り（どのトラックを編集中か一目で分かるようにする）。</summary>
    public static readonly Color KeyDiamondTrackHighlightFill = Color.FromRgb(0x98, 0xC3, 0x79);
    /// <summary>副目盛（1 フレーム線）の色。主目盛より暗くして主従を付ける。</summary>
    public static readonly Color RulerMinorTickColor     = Color.FromRgb(0x4A, 0x4A, 0x4A);
    public static readonly Color KeyDiamondBorder        = Color.FromRgb(0x1E, 0x1E, 0x1E);
    public static readonly Color TextColor               = Color.FromRgb(0xCC, 0xCC, 0xCC);
    public static readonly Color SubTextColor            = Color.FromRgb(0x88, 0x88, 0x88);
    /// <summary>サマリー行（全チャンネル）の背景。通常のトラック行と区別するため専用色にする。</summary>
    public static readonly Color SummaryRowBackground    = Color.FromRgb(0x2E, 0x2A, 0x1A);

    // ── ラバーバンド（矩形選択）の配色 ───────────────────────────

    /// <summary>矩形選択の塗り（半透明）。</summary>
    public static readonly Color MarqueeFillColor = Color.FromArgb(0x33, 0x61, 0xAF, 0xEF);

    /// <summary>矩形選択の枠線。</summary>
    public static readonly Color MarqueeBorderColor = Color.FromRgb(0x61, 0xAF, 0xEF);
}
