// ============================================================================
//  ReelMission.cs
//  「巻いて魚を釣り上げる」ミッションの判定。
// ============================================================================

/// <summary>
/// 巻き取りの練習ミッション。
///
/// 【ルール（データで指定する上書き）】
///  ・<see cref="TutorialRules.BeatDisabled"/>      … 出題・回答を行わず、魚はずっとひるんだまま
///  ・<see cref="TutorialRules.DriftDisabled"/>     … 漂流物を出さない
///  ・<see cref="TutorialRules.LineBreakDisabled"/> … 糸切れで中断させない
/// ＝ 「ホイールを回して寄せる」という 1 点だけに集中できる状態になる。
///
/// 【判定】
/// 釣果（<c>fishing.catch</c>）でクリア。
///
/// 【進捗】
/// 魚までの残り距離をメートルで出す（縮んでいくのが分かると巻き続けられる）。
/// </summary>
public sealed class ReelMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>距離を負の値で表示しないための下限。</summary>
    private const float DistanceDisplayMin = 0f;

    /// <summary>魚が掛かっていないときに出す案内。</summary>
    private const string HintNoFish = "ホイールで糸を巻こう";

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>いまのウキまでの残り距離（メートル。表示用）。</summary>
    private float remainingDistance;

    /// <summary>残り距離を測れているか。</summary>
    private bool hasDistance;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.Reel;

    /// <summary>残り距離、または案内。</summary>
    public override string ProgressText
        => hasDistance
            ? $"あと {SEED.Mathf.Max(remainingDistance, DistanceDisplayMin):F1} m"
            : HintNoFish;

    /// <summary>釣果を購読する。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        Subscribe(FishingEvents.Catch, OnCaught);
    }

    /// <summary>
    /// 竿先とウキの水平距離を測って進捗に出す。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（未使用）。</param>
    protected override void OnUpdate(MissionContext ctx, float unscaledDelta)
    {
        if (ctx.Controller is not { } controller || !controller.IsHooked)
        {
            hasDistance = false;
            return;
        }

        var rod = controller.RodTipWorldPosition;
        var bob = controller.FloatWorldPosition;
        float dx = rod.x - bob.x;
        float dz = rod.z - bob.z;

        remainingDistance = SEED.Mathf.Sqrt(dx * dx + dz * dz);
        hasDistance = true;
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>釣果を受け取ってクリアにする。</summary>
    /// <param name="fishName">釣った魚の表示名（ここでは使わない）。</param>
    private void OnCaught(string fishName) { MarkCleared(); }
}
