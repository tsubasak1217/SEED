// ============================================================================
//  CatchTargetMission.cs
//  「指定した種類の魚を釣る」ミッションの判定（ステージ 1）。
// ============================================================================

/// <summary>
/// 指定した種類の魚を釣り上げるミッション。
///
/// 【狙い】
/// 同じレベルの魚がいろいろ出るなかから、狙った 1 種を釣り当てる。
/// 目当て以外の魚を釣ってもクリアにはならず、そのまま続行する
/// （「釣れた ＝ 正解」ではないことを体で覚えてもらう）。
///
/// 【ルール（データで指定する上書き）】
///  ・<see cref="TutorialMission.fishLevelFilter"/> … 出す魚のレベルを 1 つに絞る
///  ・<see cref="TutorialMission.driftDisabled"/>   … 漂流物は出さない
/// <b>魚種の指定（fishPrefabFilter）は設定しない。</b>
/// そのレベルの魚が満遍なく出てこそ「狙って釣る」が成立するため。
///
/// 【種類の判定】
/// 釣果イベントは表示名しか運ばないので、
/// <see cref="FishingController.LastCaughtFish"/> と
/// <see cref="FishManager.PrefabPathOf"/> を突き合わせ、
/// 生成に使った .actor パスに目当ての名前（<see cref="TutorialMission.paramText"/>）が
/// 含まれているかで判定する。
///
/// 【判定は 2 段階（種類は釣った瞬間・クリアは演出のあと）】
/// <list type="number">
///   <item>
///     <c>fishing.catch</c>（獲得演出の開始）で<b>種類だけ</b>を見て控える。
///     この瞬間なら <see cref="FishingController.LastCaughtFish"/> が確実に生きている。
///   </item>
///   <item>
///     <c>fishing.catch_presented</c>（獲得演出を見せ終えて閉じた瞬間）でクリアにする。
///   </item>
/// </list>
/// 演出の開始でクリアにすると、魚を掲げている最中にクリアバナーが重なって
/// 「何を釣ったのか見えないまま話が進む」ため、巻き上げミッション
/// （<see cref="ReelMission"/>）と同じく演出が閉じるのを待つ。
/// 目当て以外の魚だったときは演出が閉じてもクリアにせず、そのまま続行する。
/// </summary>
public sealed class CatchTargetMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>目当ての魚を釣る必要匹数の既定値。</summary>
    private const int DefaultRequiredCount = 1;

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>目当ての魚を釣った匹数。</summary>
    private int caughtCount;

    /// <summary>目当て以外を釣った匹数（表示用）。</summary>
    private int otherCount;

    /// <summary>クリアに必要な匹数。</summary>
    private int requiredCount = DefaultRequiredCount;

    /// <summary>目当ての魚を表す .actor 名の一部（例 kumanomi）。</summary>
    private string targetKey = string.Empty;

    /// <summary>
    /// 獲得演出の開始時に控えた「いま釣った魚は目当ての種類だったか」。
    /// 演出が閉じる（<see cref="FishingEvents.CatchPresented"/>）まで持ち越す。
    /// </summary>
    private bool pendingIsTarget;

    /// <summary>
    /// 獲得演出の開始時に控えた魚の表示名（ログ用）。
    /// 演出の終了イベントも表示名を運ぶが、控えた時点の値と突き合わせずに使うと
    /// 「種類の判定は A、ログは B」というねじれが起きうるので、対で控えておく。
    /// </summary>
    private string pendingDisplayName = string.Empty;

    /// <summary>釣果の控えが有効か（演出の開始を受け取っていれば true）。</summary>
    private bool hasPendingCatch;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.CatchTarget;

    /// <summary>釣った匹数と、目当て以外を釣った回数。</summary>
    public override string ProgressText
        => otherCount > 0
            ? $"{caughtCount} / {requiredCount} 匹（ほかの魚 {otherCount} 匹）"
            : $"{caughtCount} / {requiredCount} 匹";

    /// <summary>目当ての魚と必要匹数を控え、釣果を購読する。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        caughtCount        = 0;
        otherCount         = 0;
        hasPendingCatch    = false;
        pendingIsTarget    = false;
        pendingDisplayName = string.Empty;
        requiredCount      = ctx.Data.paramCount > 0 ? ctx.Data.paramCount : DefaultRequiredCount;
        targetKey          = ctx.Data.paramText ?? string.Empty;

        if (string.IsNullOrWhiteSpace(targetKey))
        {
            SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: 目当ての魚（文字列パラメータ）が未設定です。何を釣ってもクリアになります。");
        }

        // 種類の判定は「釣った瞬間」に済ませ、クリアの成立は「演出が閉じた瞬間」まで待つ
        // （詳細はクラス説明の【判定は 2 段階】を参照）。
        Subscribe(FishingEvents.Catch, name => OnCatchBegan(ctx, name));
        Subscribe(FishingEvents.CatchPresented, _ => OnCatchPresented(ctx));
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 獲得演出が始まった瞬間の処理。<b>種類の判定だけ</b>を行って控える。
    ///
    /// クリアにはしない（演出中にクリアバナーを重ねないため）。
    /// 判定をここで行うのは、この瞬間なら
    /// <see cref="FishingController.LastCaughtFish"/> が確実に生きているからで、
    /// 演出が閉じたあとまで待つと参照が失われて種類が分からなくなる恐れがある。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="displayName">釣った魚の表示名（ログ用）。</param>
    private void OnCatchBegan(MissionContext ctx, string displayName)
    {
        pendingIsTarget    = IsTargetFish(ctx);
        pendingDisplayName = displayName ?? string.Empty;
        hasPendingCatch    = true;
    }

    /// <summary>
    /// 獲得演出が閉じた瞬間の処理。控えておいた種類の判定でクリアを決める。
    ///
    /// 演出が中断された場合はこのイベント自体が飛ばないので、控えは次の釣果で
    /// 上書きされるまで残るだけで、誤って加算されることはない。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    private void OnCatchPresented(MissionContext ctx)
    {
        // 演出の開始を受け取っていない釣果（このミッションが始まる前に開いた演出など）は無視する
        if (!hasPendingCatch) { return; }
        hasPendingCatch = false;

        if (pendingIsTarget)
        {
            caughtCount++;
            if (caughtCount >= requiredCount) { MarkCleared(); }
            return;
        }

        otherCount++;
        SEED.Debug.Log($"[Mission] {ctx.Data.id}: 目当て以外の魚（{pendingDisplayName}）を釣ったので続行します。");
    }

    /// <summary>
    /// 直前に釣った魚が目当ての種類か【種類判定の唯一の実装】。
    /// 目当てが未設定なら、どの魚でも「目当て」とみなす（詰まらせない）。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <returns>目当ての魚なら true。</returns>
    private bool IsTargetFish(MissionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(targetKey)) { return true; }
        if (ctx.Controller is not { } controller) { return false; }
        if (controller.LastCaughtFish is not { } fish) { return false; }
        if (ctx.Fish is not { } manager) { return false; }

        string path = manager.PrefabPathOf(fish.Actor);
        return !string.IsNullOrEmpty(path)
            && path.Contains(targetKey, System.StringComparison.OrdinalIgnoreCase);
    }
}
