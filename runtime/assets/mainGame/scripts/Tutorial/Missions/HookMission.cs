// ============================================================================
//  HookMission.cs
//  「ウキが沈んだ瞬間に合わせる」ミッションの判定。
// ============================================================================

/// <summary>
/// 合わせ（フッキング）の練習ミッション。
///
/// 【台本】
/// 指定レベルの魚を必ず・短い待ちで食いつかせる（FishManager の台本 API）。
/// さらに <see cref="TutorialRules.RebiteAfterHookMiss"/> により、
/// 合わせを外しても魚は逃げずに前アタリからやり直す。
/// ＝ <b>何度外してもアタリが来続ける</b>ので、成功するまで練習できる。
///
/// 【判定】
/// <c>fishing.fight_begin</c>（合わせが決まってやり取りが始まった瞬間）でクリア。
///
/// 【ルール】
/// 巻きはデータ側で許可しない（合わせだけに集中させるため）。
/// </summary>
public sealed class HookMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>台本で食いつかせる魚のレベルの既定値（1 始まり）。</summary>
    private const int DefaultBiteLevel = 1;

    /// <summary>レベル番号（1 始まり）を台本 API の添字（0 始まり）へ直す差分。</summary>
    private const int LevelNumberToIndex = 1;

    /// <summary>食いつくまでの待ち秒数（短くして待たせない）。</summary>
    private const float BiteDelaySeconds = 2.0f;

    /// <summary>まだ 1 度も外していないときの案内。</summary>
    private const string HintFirst = "ウキが沈んだら左クリック";

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>合わせを外した回数（進捗表示に使う）。</summary>
    private int missCount;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.Hook;

    /// <summary>案内と、外した回数（外していれば「もう一度」を出す）。</summary>
    public override string ProgressText
        => missCount <= 0 ? HintFirst : $"{HintFirst}（{missCount} 回ミス）";

    /// <summary>
    /// 必ず食いつく台本を仕込み、合わせの判定とやり取り開始を購読する。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        ApplyForceBite(ctx);

        // 合わせ判定は Miss も含めて hook_judged で飛ぶ（時間切れ・早合わせは飛ばない）。
        // 回数はあくまで表示用なので、拾えた分だけ数えれば足りる。
        Subscribe(FishingEvents.HookJudged, OnHookJudged);
        Subscribe(FishingEvents.FightBegin, MarkCleared);
    }

    /// <summary>やり直しでも台本を必ず仕込み直す（台本はミッション切り替えで解除されるため）。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnRestart(MissionContext ctx)
    {
        missCount = 0;
        ApplyForceBite(ctx);
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 合わせ判定を受け取り、Miss だけを数える。
    /// </summary>
    /// <param name="judgement">判定名（Excellent / Great / Nice / Miss）。</param>
    private void OnHookJudged(string judgement)
    {
        if (string.Equals(judgement, "Miss", System.StringComparison.OrdinalIgnoreCase))
        {
            missCount++;
        }
    }

    /// <summary>
    /// 「必ず・すぐに食いつく」台本を魚マネージャへ設定する。
    /// マネージャが居なければ警告だけ出して進む（説明自体は読めるため）。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    private static void ApplyForceBite(MissionContext ctx)
    {
        if (ctx.Fish is not { } manager)
        {
            SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: FishManager が居ないため強制の食いつきを仕込めません。");
            return;
        }

        int level = ctx.Data.fishLevelFilter > 0 ? ctx.Data.fishLevelFilter : DefaultBiteLevel;
        manager.SetScriptedSpawn(level - LevelNumberToIndex, BiteDelaySeconds, true);
    }
}
