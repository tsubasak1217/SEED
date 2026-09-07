// ============================================================================
//  BeatMission.cs
//  「1 周ミスなしでビートを刻む」ミッションの判定。
// ============================================================================

/// <summary>
/// リズム（ビート）の練習ミッション。
///
/// 【ルール（データで指定する上書き）】
///  ・<see cref="TutorialRules.RestartCycleOnMiss"/> … ミスしたら隙を挟まず即もう一周
///  ・<see cref="TutorialRules.LineBreakDisabled"/>  … 糸は減るが 0 でも切れない
/// 周回のやり直し時には糸ゲージが「ぬっ」と満タンへ戻る（FishingFight 側で処理）。
///
/// 【判定】
/// 隙（<c>fishing.stun_begin</c>）に入ればクリア。
/// ミスなしで回答を終えたときにしか隙へ入らない（ミスがあれば出題へ戻される）ので、
/// このイベント 1 つで「1 周ミスなし」がそのまま表現できる。
///
/// 【進捗】
/// 何周目かを出す。ミスするたびに周が進むので、あと少しであることが読める。
/// </summary>
public sealed class BeatMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>最初の周を 1 周目と数えるための初期値。</summary>
    private const int FirstRound = 1;

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>いま何周目か（ミスするたびに増える）。</summary>
    private int round = FirstRound;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.Beat;

    /// <summary>何周目かの表示。</summary>
    public override string ProgressText => $"{round} 周目 / ミスなしで 1 周";

    /// <summary>
    /// 出題の開始と隙の到達を購読する。
    /// ルールの上書き（ミスで即やり直し・糸切れ無効）はデータ側で指定し、
    /// ディレクタがミッション開始時にまとめて適用する。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        round = FirstRound;

        Subscribe(FishingEvents.StunBegin, MarkCleared);
        Subscribe(FishingEvents.HookJudged, CountMissAsRetry);
    }

    /// <summary>やり直し時は周回カウントを 1 から数え直す。</summary>
    /// <param name="ctx">周辺への窓口（未使用）。</param>
    protected override void OnRestart(MissionContext ctx)
    {
        round = FirstRound;
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 合わせ・リズムの判定を受け取り、Miss なら「もう一周」として数える。
    /// </summary>
    /// <param name="judgement">判定名（Excellent / Great / Nice / Miss）。</param>
    private void CountMissAsRetry(string judgement)
    {
        if (string.Equals(judgement, "Miss", System.StringComparison.OrdinalIgnoreCase))
        {
            round++;
        }
    }

    /// <summary>
    /// 隙へ入った瞬間の通知を取りこぼしていても詰まらないよう、
    /// 毎フレーム「いま隙に居るか」も見る【達成判定の保険】。
    ///
    /// ミッション開始時点で既に隙へ入っていた場合、通知はもう飛ばないので
    /// イベントだけに頼ると永久にクリアできない。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（未使用）。</param>
    protected override void OnUpdate(MissionContext ctx, float unscaledDelta)
    {
        if (ctx.Controller is not { } controller) { return; }
        if (controller.FightPhase == FishingFight.Phase.Rest) { MarkCleared(); }
    }
}
