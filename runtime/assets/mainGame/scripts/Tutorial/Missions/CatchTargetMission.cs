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
        caughtCount   = 0;
        otherCount    = 0;
        requiredCount = ctx.Data.paramCount > 0 ? ctx.Data.paramCount : DefaultRequiredCount;
        targetKey     = ctx.Data.paramText ?? string.Empty;

        if (string.IsNullOrWhiteSpace(targetKey))
        {
            SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: 目当ての魚（文字列パラメータ）が未設定です。何を釣ってもクリアになります。");
        }

        Subscribe(FishingEvents.Catch, name => OnCaught(ctx, name));
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 釣果を受け取り、目当ての魚だったときだけ匹数を数える。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="displayName">釣った魚の表示名（ログ用）。</param>
    private void OnCaught(MissionContext ctx, string displayName)
    {
        if (IsTargetFish(ctx))
        {
            caughtCount++;
            if (caughtCount >= requiredCount) { MarkCleared(); }
            return;
        }

        otherCount++;
        SEED.Debug.Log($"[Mission] {ctx.Data.id}: 目当て以外の魚（{displayName}）を釣ったので続行します。");
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
