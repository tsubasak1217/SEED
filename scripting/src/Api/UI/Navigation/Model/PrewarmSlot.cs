using System;

namespace SEED.UI;

// ============================================================
//  PrewarmSlot.cs — 画面の作り置き 1 つの段階の移り変わり（2026-10-02。lane3。純粋な計算。docs/ui_navigation.md §2.8）
//
//  ScreenStack.Prewarm が、プレハブ 1 つにつき 1 つ持つ。エンジンには触れず、部品が毎フレーム渡す様子
//  （出入りの途中か・枠と中身ができあがったか・画面のスクリプトが温まったと言うか）から段階を進め、部品がすること
//  （作り始める・温め描きを始める・終える）を返す。
//
//    Waiting ──(出入りの途中でない)──▶ Building ──(できあがって SettleFrames 経ち、温まった)──▶ Ready
//                                        │                                   ▲
//                                        └──(WarmDrawFrames > 0)──▶ WarmDrawing ┘
//    Building・WarmDrawing・Ready ──Lend──▶ Lent ──Return──▶ Reuse: Ready / Refill: Waiting / Once: Discarded
//
//  - 作っている途中・温め描きの途中でも貸せる（もう 1 つ作るより軽い。残りの準備は画面の中で続く）
//  - 温まるのを待つ上限（MaxWaitFrames）を過ぎたら、温まったとみなして先へ進める（TimedOut。部品が警告を出す）
//  - 作り置きが消えた（シーンの切り替えなど。Lost）ら、Once は終わり、Refill・Reuse は作り直しを待つ
// ============================================================

/// <summary>作り置きの段階。</summary>
public enum PrewarmStage
{
    /// <summary>作り始めを待つ（出入りの動きの途中は作らない）。</summary>
    Waiting = 0,
    /// <summary>隠した枠の中で作っている（できあがり・部品の開始・温まるのを待つ）。</summary>
    Building = 1,
    /// <summary>温まった作り置きを、見えない奥のレイヤーで描いている（文字の字形を焼く）。</summary>
    WarmDrawing = 2,
    /// <summary>貸せる。</summary>
    Ready = 3,
    /// <summary>画面に貸している。</summary>
    Lent = 4,
    /// <summary>捨てた（終わり）。</summary>
    Discarded = 5,
}

/// <summary>作り置きの 1 フレームで部品がすること。</summary>
public enum PrewarmAction
{
    /// <summary>何もしない。</summary>
    None = 0,
    /// <summary>隠した枠を作り始める。</summary>
    StartBuild = 1,
    /// <summary>温め描きを始める（枠を奥のレイヤーで見せる）。</summary>
    BeginWarmDraw = 2,
    /// <summary>温め描きを終える（枠を隠し直す）。貸せるようになった。</summary>
    EndWarmDraw = 3,
    /// <summary>温め描きなしで貸せるようになった（ログ用）。</summary>
    BecameReady = 4,
}

/// <summary>画面の作り置き 1 つの段階。</summary>
public sealed class PrewarmSlot
{
    /// <summary>
    /// できあがってから温まったかを見始めるまでのフレーム数（部品のスクリプトの OnStart と最初の Update を済ませる。
    /// 作ったフレームの末尾にできあがり、次のフレームでスクリプトが始まる）。
    /// </summary>
    public const int SettleFrames = 2;

    /// <summary>温まるのを待つ上限（フレーム。越えたら温まったとみなして先へ進める＝<see cref="TimedOut"/>）。</summary>
    public const int MaxWaitFrames = 300;

    /// <summary>作り置き 1 つを作る（段階は Waiting）。</summary>
    /// <param name="prefab">画面のプレハブ（assets:// の .actor）。</param>
    /// <param name="options">指定（null = 既定）。</param>
    public PrewarmSlot(string prefab, PrewarmOptions? options)
    {
        Prefab = prefab ?? string.Empty;
        Options = options ?? PrewarmOptions.Default;
    }

    /// <summary>画面のプレハブ。</summary>
    public string Prefab { get; }

    /// <summary>指定。</summary>
    public PrewarmOptions Options { get; }

    /// <summary>今の段階。</summary>
    public PrewarmStage Stage { get; private set; } = PrewarmStage.Waiting;

    /// <summary>できあがってから数えたフレーム（Building の間）。</summary>
    public int BuiltFrames { get; private set; }

    /// <summary>温め描きの残りのフレーム（WarmDrawing の間）。</summary>
    public int WarmDrawLeft { get; private set; }

    /// <summary>温まるのを待つ上限を過ぎて先へ進めたか（画面のスクリプトの IsPrewarmReady が true にならなかった）。</summary>
    public bool TimedOut { get; private set; }

    /// <summary>貸した回数（ログ・確かめ用）。</summary>
    public int LendCount { get; private set; }

    /// <summary>貸せるか（作っている途中・温め描きの途中・貸せる）。</summary>
    public bool CanLend => Stage is PrewarmStage.Building or PrewarmStage.WarmDrawing or PrewarmStage.Ready;

    /// <summary>
    /// 1 フレーム進める（毎フレーム 1 回。貸している・捨てた後は何もしない）。
    /// </summary>
    /// <param name="transitioning">スタックが出入りの動きの途中か（途中なら作り始めない＝重いフレームを動きと重ねない）。</param>
    /// <param name="built">枠と中身ができあがったか。</param>
    /// <param name="screenReady">画面のスクリプトが温まったと言うか（スクリプトが無ければ true）。</param>
    /// <returns>部品がすること。</returns>
    public PrewarmAction Tick(bool transitioning, bool built, bool screenReady)
    {
        switch (Stage)
        {
            case PrewarmStage.Waiting:
                // 出入りの動きの途中は作り始めない（組み立ての重いフレームで動きを止めない）
                if (transitioning) return PrewarmAction.None;
                Stage = PrewarmStage.Building;
                BuiltFrames = 0;
                TimedOut = false;
                return PrewarmAction.StartBuild;
            case PrewarmStage.Building:
                return AdvanceBuilding(built, screenReady);
            case PrewarmStage.WarmDrawing:
                // 温め描きは決めたフレーム数だけ（見せる・隠すはフレームの末尾に当たり、そのフレームの描画から効く）
                WarmDrawLeft = Math.Max(0, WarmDrawLeft - 1);
                if (WarmDrawLeft > 0) return PrewarmAction.None;
                Stage = PrewarmStage.Ready;
                return PrewarmAction.EndWarmDraw;
            default:
                return PrewarmAction.None;
        }
    }

    /// <summary>作っている間: できあがってから SettleFrames 経ち、温まったら（上限を過ぎたら）先へ。</summary>
    private PrewarmAction AdvanceBuilding(bool built, bool screenReady)
    {
        if (!built) return PrewarmAction.None;
        BuiltFrames = Math.Min(BuiltFrames + 1, MaxWaitFrames);
        bool settled = BuiltFrames >= SettleFrames && screenReady;
        bool gaveUp = BuiltFrames >= MaxWaitFrames;
        if (!settled && !gaveUp) return PrewarmAction.None;
        TimedOut = !settled;
        if (Options.WarmDrawFrames > 0)
        {
            Stage = PrewarmStage.WarmDrawing;
            WarmDrawLeft = Options.WarmDrawFrames;
            return PrewarmAction.BeginWarmDraw;
        }
        Stage = PrewarmStage.Ready;
        return PrewarmAction.BecameReady;
    }

    /// <summary>貸す（貸せる段階なら Lent へ。温め描きの途中なら部品が打ち切る）。</summary>
    /// <returns>貸したら true。</returns>
    public bool Lend()
    {
        if (!CanLend) return false;
        Stage = PrewarmStage.Lent;
        LendCount++;
        return true;
    }

    /// <summary>
    /// 貸した画面が外れた（下ろした・置き換えた・覆われて手放した）。Reuse は貸せる状態へ戻り、Refill は作り直しを待ち、Once は終わる。
    /// </summary>
    /// <returns>戻った後の段階。</returns>
    public PrewarmStage Return()
    {
        if (Stage != PrewarmStage.Lent) return Stage;
        Stage = Options.Mode switch
        {
            PrewarmMode.Reuse => PrewarmStage.Ready,
            PrewarmMode.Refill => PrewarmStage.Waiting,
            _ => PrewarmStage.Discarded,
        };
        return Stage;
    }

    /// <summary>作り置きの実体が消えた（シーンの切り替え・部品の破棄）。Once は終わり、ほかは作り直しを待つ。</summary>
    /// <returns>消えた後の段階。</returns>
    public PrewarmStage Lost()
    {
        if (Stage == PrewarmStage.Discarded) return Stage;
        Stage = Options.Mode == PrewarmMode.Once ? PrewarmStage.Discarded : PrewarmStage.Waiting;
        BuiltFrames = 0;
        WarmDrawLeft = 0;
        return Stage;
    }

    /// <summary>捨てる（DiscardPrewarm）。</summary>
    public void Discard() => Stage = PrewarmStage.Discarded;
}
