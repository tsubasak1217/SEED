namespace SEED.UI;

// ============================================================
//  PrewarmOptions.cs — 画面の作り置き（ScreenStack.Prewarm）の指定（2026-10-02。lane3。docs/ui_navigation.md §2.8）
//
//  重い画面（Wake or Pay の編集画面: アクタ 110・スクリプト約 90・時刻ホイールの行）は、積んだときの組み立てで
//  数フレーム（実機 4 × 95 ms）止まる。空いた時間に隠した枠の中で組み立てておき、次に同じプレハブを積んだときに
//  それを使う（作り置き）。使い終わった後の扱いを PrewarmMode で選ぶ。
// ============================================================

/// <summary>作り置きを使い終わった後（画面が外れた後）の扱い。</summary>
public enum PrewarmMode
{
    /// <summary>1 回だけ使う（画面と一緒に消え、作り置きは無くなる）。</summary>
    Once = 0,
    /// <summary>使ったら、空いた時間（出入りの動きの無い間）にもう 1 つ作り直す（毎回まっさらな画面）。</summary>
    Refill = 1,
    /// <summary>
    /// 外れた画面を消さずに隠した枠へ戻し、次に積むときに使い回す（作り直さない）。
    /// 画面のスクリプトには使うたびに OnScreenEnter が届き、外れるたびに OnScreenExit が届く（前の状態は Enter で作り直すこと）。
    /// </summary>
    Reuse = 2,
}

/// <summary>画面の作り置きの指定（<see cref="ScreenStack.Prewarm(string, PrewarmOptions?)"/>）。</summary>
public sealed class PrewarmOptions
{
    /// <summary>使い終わった後の扱い（既定 Once）。</summary>
    public PrewarmMode Mode { get; init; } = PrewarmMode.Once;

    /// <summary>
    /// 温め描きのフレーム数（0 = 描かない。既定 0）。隠したノードは描かれないので、文字の字形は最初に画面へ出したフレームで焼かれる
    /// （PC で 1 回目に約 33 ms）。1 以上なら、温まった作り置きの枠をスタックのいちばん下の画面より奥のレイヤー（不透明な背景の下）で
    /// このフレーム数だけ描いて字形を焼いておく（利用者には見えない。根の画面が透ける〈Opaque = false〉スタックでは見えるので使わない）。
    /// </summary>
    public int WarmDrawFrames { get; init; }

    /// <summary>中身を枠の安全領域の中（Body）に作るか（積むときの ScreenOptions.SafeArea と違えば、貸すときに付け替える）。</summary>
    public bool SafeArea { get; init; } = true;

    /// <summary>既定の指定。</summary>
    public static PrewarmOptions Default { get; } = new();
}
