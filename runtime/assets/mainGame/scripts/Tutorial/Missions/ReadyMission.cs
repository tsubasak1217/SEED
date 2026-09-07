// ============================================================================
//  ReadyMission.cs
//  「構えて左右を向く」ミッションの判定。
// ============================================================================

/// <summary>
/// 竿を構えて左右いっぱいまで向くミッション【構えと狙いの練習】。
///
/// 【サブ目標】
///  ・構えて右を向こう
///  ・構えて左を向こう
/// 両方にチェックが入ったらクリア。
///
/// 【判定のしかた】
/// 構えている（fishing.ready_begin を受けてから fishing.ready_end までの間）に、
/// <see cref="PlayerMove.StanceYawOffsetDegrees"/> が振れ角の限界の
/// <see cref="EndReachedRatio"/> 倍を超えたら「端まで振った」とみなす。
/// ぴったり限界値に達したかを見ると、クランプの丸め誤差で永久に達成できない恐れがある。
///
/// 【ルール】
/// 投げてしまうと構えが解けて練習にならないので、投げ入力はデータ側で許可しない
/// （<see cref="TutorialMission.allowCast"/> を false にする）。
/// </summary>
public sealed class ReadyMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>「端まで振った」とみなす、振れ角の限界に対する割合。</summary>
    private const float EndReachedRatio = 0.9f;

    /// <summary>振れ角の限界が取れなかったときに使う既定値（度）。</summary>
    private const float FallbackTurnRangeDegrees = 45f;

    /// <summary>サブ目標「右」の表示名。</summary>
    private const string LabelRight = "構えて右を向こう";

    /// <summary>サブ目標「左」の表示名。</summary>
    private const string LabelLeft = "構えて左を向こう";

    /// <summary>構えていないときに出す案内。</summary>
    private const string HintNotReady = "左クリックで構えよう";

    /// <summary>構えているときに出す案内。</summary>
    private const string HintReady = "A / D で向きを変えよう";

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>サブ目標「右を向く」。</summary>
    private MissionObjective? rightObjective;

    /// <summary>サブ目標「左を向く」。</summary>
    private MissionObjective? leftObjective;

    /// <summary>いま構えているか（ready_begin / ready_end で切り替わる）。</summary>
    private bool isReady;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.Ready;

    /// <summary>いま何をすればよいかの案内。</summary>
    public override string ProgressText => isReady ? HintReady : HintNotReady;

    /// <summary>サブ目標を 2 つ作り、構えの出入りを購読する。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        rightObjective = AddObjective(LabelRight);
        leftObjective  = AddObjective(LabelLeft);

        Subscribe(FishingEvents.ReadyBegin, () => isReady = true);
        Subscribe(FishingEvents.ReadyEnd,   () => isReady = false);
    }

    /// <summary>
    /// 構えている間だけ振れ角を見て、端まで振れたサブ目標にチェックを入れる。
    /// 両方そろったらクリア。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（未使用）。</param>
    protected override void OnUpdate(MissionContext ctx, float unscaledDelta)
    {
        if (!isReady) { return; }
        if (ctx.Player is not { } player) { return; }

        float range = player.StanceTurnRangeDegrees > 0f
            ? player.StanceTurnRangeDegrees
            : FallbackTurnRangeDegrees;
        float threshold = range * EndReachedRatio;
        float yaw = player.StanceYawOffsetDegrees;

        // 振れ角の符号は「プレイヤーから見た向き」。正が右、負が左。
        if (yaw >= threshold)  { rightObjective?.Complete(); }
        if (yaw <= -threshold) { leftObjective?.Complete(); }

        if (AllObjectivesDone()) { MarkCleared(); }
    }
}
