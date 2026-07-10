// ============================================================
//  AnimationTimelineConstants.cs — アニメーションタイムライン用定数
//
//  ドープシートの描画・スナップ・配色に関するマジックナンバーを
//  すべてここへ集約する（プロジェクト規約: マジックナンバー禁止）。
// ============================================================

using System.Windows.Media;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>
/// アニメーションタイムラインパネル／ドープシートで使用する定数群。
/// 数値・色を一元管理し、見た目やスナップ挙動の調整をここだけで完結させる。
/// </summary>
internal static class AnimationTimelineConstants
{
    // ── レイアウト寸法 ──────────────────────────────────────────

    /// <summary>時間ルーラーの高さ（ピクセル）。</summary>
    public const double RulerHeight = 24.0;

    /// <summary>トラック 1 行の高さ（ピクセル）。</summary>
    public const double TrackRowHeight = 26.0;

    /// <summary>デフォルトの水平スケール（1 秒あたりのピクセル数）。</summary>
    public const double DefaultPixelsPerSecond = 160.0;

    /// <summary>水平スケールの最小値・最大値（ズーム範囲）。</summary>
    public const double MinPixelsPerSecond = 20.0;
    public const double MaxPixelsPerSecond = 2000.0;

    /// <summary>キーフレーム◆マーカーの半径（中心から頂点までの距離、ピクセル）。</summary>
    public const double KeyDiamondRadius = 5.0;

    /// <summary>キーフレーム◆のヒット判定半径（クリック・ドラッグ開始判定用、見た目より少し大きめ）。</summary>
    public const double KeyDiamondHitRadius = 8.0;

    /// <summary>プレイヘッド（再生位置）三角ハンドルのサイズ（ピクセル）。</summary>
    public const double PlayheadHandleSize = 8.0;

    /// <summary>プレイヘッド縦線の太さ（ピクセル）。</summary>
    public const double PlayheadLineThickness = 1.5;

    // ── ルーラー目盛 ────────────────────────────────────────────

    /// <summary>ルーラーに目盛を描く最小間隔（ピクセル）。これを下回る密度になる時間刻みは間引く。</summary>
    public const double MinRulerTickSpacingPx = 48.0;

    /// <summary>ルーラー目盛の時間刻み候補（秒）。ズームに応じてこの中から最適なものを選ぶ。</summary>
    public static readonly double[] RulerTickStepsSeconds =
    {
        0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 30.0, 60.0,
    };

    // ── スナップ ────────────────────────────────────────────────

    /// <summary>タイムライン編集の基準フレームレート（キー時刻のスナップ単位算出に使用）。</summary>
    public const float TimelineFrameRate = 60f;

    /// <summary>キー移動・新規作成時のスナップ間隔（秒）。1 フレーム分＝1/60 秒。</summary>
    public const float SnapSeconds = 1f / TimelineFrameRate;

    /// <summary>クリップの最小長（秒）。0 長クリップでの除算を避けるための下限。</summary>
    public const float MinDuration = 0.01f;

    // ── プレビュー再生 ──────────────────────────────────────────

    /// <summary>プレビュー再生タイマの間隔（ミリ秒）。約 60fps。</summary>
    public const int PreviewTickIntervalMs = 16;

    // ── 配色（既存パネルのダークテーマに合わせる） ────────────────

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
    public static readonly Color KeyDiamondBorder        = Color.FromRgb(0x1E, 0x1E, 0x1E);
    public static readonly Color TextColor               = Color.FromRgb(0xCC, 0xCC, 0xCC);
    public static readonly Color SubTextColor            = Color.FromRgb(0x88, 0x88, 0x88);
}
