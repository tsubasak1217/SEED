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

    // ── ルーラー目盛（フレーム基準）──────────────────────────────

    /// <summary>ラベル付きの主目盛を描く最小間隔（ピクセル）。これを下回る密度の刻みは間引く。</summary>
    public const double MinRulerTickSpacingPx = 48.0;

    /// <summary>
    /// 副目盛（フレーム 1 個ぶんの細い線）を描く最小間隔（ピクセル）。
    /// これを下回るズームではフレーム線が潰れて可読性を損なうため描かない。
    /// </summary>
    public const double MinFrameTickSpacingPx = 5.0;

    /// <summary>
    /// 主目盛のフレーム刻み候補。ズームに応じてこの中から
    /// 「MinRulerTickSpacingPx 以上の間隔になる最小の刻み」を選ぶ。
    /// 5 / 10 / 30 のような区切りのよい値を並べ、フレーム番号を読みやすくする。
    /// </summary>
    public static readonly int[] RulerTickStepsFrames =
    {
        1, 2, 5, 10, 15, 20, 30, 60, 120, 300, 600, 1800, 3600,
    };

    /// <summary>ルーラー主目盛の縦線の長さ（ピクセル、ルーラー下端から上へ）。</summary>
    public const double RulerMajorTickLength = 8.0;

    /// <summary>ルーラー副目盛（1 フレーム）の縦線の長さ（ピクセル）。</summary>
    public const double RulerMinorTickLength = 4.0;

    // ── スナップ ────────────────────────────────────────────────

    /// <summary>
    /// スナップ単位はクリップの fps（AnimClip.Fps）から算出する。
    /// 固定値を持たないのは、刻みたい単位がクリップごとに違うため
    /// （ドット絵 8fps / UI 演出 30fps / カメラ 60fps）。
    /// 変換は <see cref="AnimFrameMath"/> が担当する。
    /// </summary>
    /// <summary>クリップの最小長（秒）。0 長クリップでの除算を避けるための下限。</summary>
    public const float MinDuration = 0.01f;

    // ── フレーム送り操作 ────────────────────────────────────────

    /// <summary>◀ ▶ ボタン・←→ キーでのフレーム送り量。</summary>
    public const int FrameStepSmall = 1;

    /// <summary>Shift + ←→ でのフレーム送り量（粗送り）。</summary>
    public const int FrameStepLarge = 10;

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
    /// <summary>選択中トラックのキー◆の塗り（どのトラックを編集中か一目で分かるようにする）。</summary>
    public static readonly Color KeyDiamondTrackHighlightFill = Color.FromRgb(0x98, 0xC3, 0x79);
    /// <summary>副目盛（1 フレーム線）の色。主目盛より暗くして主従を付ける。</summary>
    public static readonly Color RulerMinorTickColor     = Color.FromRgb(0x4A, 0x4A, 0x4A);
    public static readonly Color KeyDiamondBorder        = Color.FromRgb(0x1E, 0x1E, 0x1E);
    public static readonly Color TextColor               = Color.FromRgb(0xCC, 0xCC, 0xCC);
    public static readonly Color SubTextColor            = Color.FromRgb(0x88, 0x88, 0x88);
}
