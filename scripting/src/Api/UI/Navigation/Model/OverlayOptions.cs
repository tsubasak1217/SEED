namespace SEED.UI;

// ============================================================
//  OverlayOptions.cs — 上からの覆い（プロフィール・オプション）の指定（W2-7）
//
//  覆いは画面の遷移ではなく、今の画面の上に被さる（画面のスタックに乗らない。Wake or Pay の仕様 §3.1）。
//  上から降りて（motion.overlay・motion.overlay_curve）、幕のタップ・戻る・板のフリックで閉じる。
//  閉じるフリックの向きは DismissGesture で選ぶ（既定は Flutter 版の top_sheet.dart と同じ「下へ speed.fling_dismiss 超」。
//  中の一覧がスクロールする所では一覧が指を取るので、板のスクロールしない所〈つまみ・固定の頭〉で払う）。
// ============================================================

/// <summary>上からの覆いを指で閉じる方法。</summary>
public enum OverlayDismissGesture
{
    /// <summary>板を下へ払う（速さが speed.fling_dismiss 超。Flutter 版の top_sheet と同じ。板は指に付いてこない）。</summary>
    FlickDown = 0,
    /// <summary>板を上へ引く（指に付いてくる）。size.drag_dismiss 以上引くか、上へ speed.fling_dismiss 以上で払うと閉じる。</summary>
    DragUp = 1,
    /// <summary>指では閉じない（幕のタップ・戻る・スクリプトだけ）。</summary>
    None = 2,
}

/// <summary>上からの覆いの指定。</summary>
public sealed class OverlayOptions
{
    /// <summary><see cref="FillBottomMargin"/> の「テーマの値を使う」（負の値。テーマの space.m）。</summary>
    public const float ThemeFillMargin = -1f;

    /// <summary>板の中身のプレハブ（assets:// の .actor）。</summary>
    public string ContentPrefab { get; init; } = string.Empty;
    /// <summary>中身へ渡す値（中身の UiScreen.OnScreenEnter）。</summary>
    public object? Args { get; init; }
    /// <summary>幕のタップで閉じるか。</summary>
    public bool DismissOnScrimTap { get; init; } = true;
    /// <summary>戻るで閉じるか（false でも戻るは受ける）。</summary>
    public bool CancelableByBack { get; init; } = true;
    /// <summary>指で閉じる方法（既定 FlickDown = Flutter 版と同じ）。</summary>
    public OverlayDismissGesture DismissGesture { get; init; } = OverlayDismissGesture.FlickDown;

    /// <summary>
    /// 降りる動きを付けるか（2026-10-02。false = 中身が落ち着いたら、幕と板を最初から降りた姿で出す。NavTransition.None 相当。既定 true）。
    /// 閉じる動きを見せないのは ModalHandle.Close(結果, false)・ModalHost.CloseAll(false)。
    /// </summary>
    public bool Animate { get; init; } = true;

    /// <summary>
    /// 高さいっぱい（2026-10-02。板の下端が「画面の下 − 下の安全領域 − <see cref="FillBottomMargin"/>」に来るように、中身の根の
    /// CanvasLayoutItem の高さ〈PreferredSize.y〉を毎フレーム合わせる。Flutter 版の top_sheet の SafeArea の中で下の余白まで広がる板）。
    /// 中身の根に CanvasLayoutItem が無ければ合わせられない（警告）。既定 false = 板は中身の高さ。
    /// </summary>
    public bool FillHeight { get; init; }

    /// <summary>高さいっぱいのときの板の下の余白（キャンバスの単位。負 = テーマの space.m〈12〉。幕が見えてタップで閉じられる）。</summary>
    public float FillBottomMargin { get; init; } = ThemeFillMargin;
}
