// ============================================================================
//  LandMission.cs
//  「釣り上げよう」ミッションの判定（失敗しても掛かった状態から再開）。
// ============================================================================

/// <summary>
/// 最後まで釣り上げるミッション。
///
/// 【ルール（データで指定する上書き）】
///  ・<see cref="TutorialRules.RestartFightOnLineBreak"/>
///    … 糸が切れても投げ直しへ戻さず、掛かった状態のままやり取りを仕切り直す。
/// 糸切れ自体は起こる（＝失敗の手応えは残る）が、投げるところからやり直さずに済む。
///
/// 【判定】
/// 釣果（<c>fishing.catch</c>）でクリア。
///
/// 【進捗】
/// 何度仕切り直したかを出す。失敗が無駄になっていないことが読める。
/// </summary>
public sealed class LandMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>まだ失敗していないときに出す案内。</summary>
    private const string HintFirst = "隙を作って巻き上げよう";

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>糸を切らして仕切り直した回数。</summary>
    private int retryCount;

    /// <summary>ミッション開始時点の仕切り直し通し番号（差分を取るための基準）。</summary>
    private int restartBaseline;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.Land;

    /// <summary>案内と、仕切り直した回数。</summary>
    public override string ProgressText
        => retryCount <= 0 ? HintFirst : $"{HintFirst}（仕切り直し {retryCount} 回）";

    /// <summary>釣果を購読し、仕切り直し回数の基準値を控える。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        retryCount     = 0;
        restartBaseline = ctx.Controller?.TutorialFightRestartCount ?? 0;

        Subscribe(FishingEvents.Catch, _ => MarkCleared());
    }

    /// <summary>
    /// 仕切り直しの回数を表示用に拾う。
    ///
    /// 仕切り直しでは fishing.line_break を飛ばさない（釣り本体が糸切れ処理そのものを
    /// 迂回するため）ので、イベントではなく釣り本体の通し番号の差分で数える。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（未使用）。</param>
    protected override void OnUpdate(MissionContext ctx, float unscaledDelta)
    {
        if (ctx.Controller is not { } controller) { return; }
        retryCount = SEED.Mathf.Max(controller.TutorialFightRestartCount - restartBaseline, 0);
    }

    /// <summary>やり直し時は回数を数え直す。</summary>
    /// <param name="ctx">周辺への窓口（未使用）。</param>
    protected override void OnRestart(MissionContext ctx)
    {
        retryCount      = 0;
        restartBaseline = ctx.Controller?.TutorialFightRestartCount ?? 0;
    }
}
