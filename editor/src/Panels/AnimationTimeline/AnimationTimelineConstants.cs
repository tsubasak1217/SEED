// ============================================================
//  AnimationTimelineConstants.cs — アニメーションタイムライン用定数
//
//  ドープシートの描画・スナップ・操作に関するマジックナンバーを
//  すべてここへ集約する（プロジェクト規約: マジックナンバー禁止）。
//  配色だけは WPF 依存のため AnimationTimelineConstants.Colors.cs に分離してある。
// ============================================================

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>
/// アニメーションタイムラインパネル／ドープシートで使用する定数群（寸法・挙動・文言）。
/// 調整をここだけで完結させる。
///
/// 配色は WPF の Color 型を使うため <c>AnimationTimelineConstants.Colors.cs</c> へ分けてある。
/// 本ファイルは WPF に依存しない（AnimDopeSheetLayout / AnimTimelineZoom などの純ロジックが
/// 参照し、editor/tests からもリンクされるため、ここへ WPF 型を持ち込まないこと）。
/// </summary>
internal static partial class AnimationTimelineConstants
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

    /// <summary>
    /// 描画コンテンツ右端に足す余白（ピクセル）。
    /// クリップ末尾の◆が画面際で切れないようにする。「F」（全体表示）の
    /// 収まり幅計算にも同じ値を使い、余白の定義を 1 か所に保つ。
    /// </summary>
    public const double FitContentMarginPx = 40.0;

    // ── ナビゲーション（ズーム・スクロール・パン）────────────────

    /// <summary>Ctrl+ホイール 1 ノッチあたりのズーム倍率（1 より大きい値で拡大方向）。</summary>
    public const double WheelZoomFactor = 1.15;

    /// <summary>ホイール 1 ノッチあたりの横スクロール量（ピクセル）。</summary>
    public const double WheelScrollStepPx = 90.0;

    /// <summary>Shift+ホイール 1 ノッチあたりの縦スクロール行数。</summary>
    public const int WheelVerticalScrollRows = 2;

    /// <summary>WPF のホイール 1 ノッチぶんの Delta 値（System.Windows.Input.Mouse.MouseWheelDeltaForOneLine と同値）。</summary>
    public const double WheelDeltaPerNotch = 120.0;

    /// <summary>
    /// クリックとラバーバンド（矩形選択）を分ける移動量のしきい値（ピクセル）。
    /// これ未満の移動は「クリック」として扱い、選択解除だけを行う。
    /// </summary>
    public const double MarqueeStartThresholdPx = 3.0;

    /// <summary>矩形選択の枠線の太さ（ピクセル）。</summary>
    public const double MarqueeBorderThickness = 1.0;

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

    // ── 現在値スナップショットの問い合わせ ─────────────────────

    /// <summary>
    /// キー挿入時にキー対象アクタの現在値スナップショットがまだ届いていない場合、
    /// GET_ACTOR_COMPONENTS を送って応答を待つ最大時間（秒）。
    /// 超過したら諦めて「取得できません」を表示する（ランタイム未接続等で無限待ちにしないため）。
    /// </summary>
    public const double SnapshotFetchTimeoutSeconds = 2.0;

    // ── サマリー行（全チャンネル）──────────────────────────────

    /// <summary>サマリー行のラベル文字列。トラックリスト先頭に常に表示する。</summary>
    public const string SummaryRowLabel = "全チャンネル";

    // ── UI 文言（右クリックメニュー・値エディタ）──────────────────

    /// <summary>◆右クリックメニューの削除項目（単一選択時）。</summary>
    public const string DeleteKeyMenuHeader = "キーを削除";

    /// <summary>◆右クリックメニューの削除項目（複数選択時。{0} は選択件数）。</summary>
    public const string DeleteSelectedKeysMenuFormat = "選択キー {0} 個を削除";

    /// <summary>値エディタの複数選択見出し（{0} は選択件数）。</summary>
    public const string MultiSelectionLabelFormat = "{0} 個のキーを選択中";

    /// <summary>コピー完了時のステータス表示（{0} はコピー件数）。</summary>
    public const string CopiedKeysStatusFormat = "{0} 個のキーをコピーしました";

    /// <summary>クリップボードに貼り付け可能なキーが無いときのステータス表示。</summary>
    public const string NothingToPasteStatus = "貼り付けできるキーがありません";
}
