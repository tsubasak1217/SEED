// ============================================================================
//  DriftPickupMission.cs
//  「漂流物を拾う」ミッションの判定。
// ============================================================================

using System.Collections.Generic;

/// <summary>
/// 漂流物を 3 種類とも拾うミッション。
///
/// 【台本】
/// 漂流物の自然出現は止め（<see cref="TutorialRules.DriftDisabled"/>）、
/// <b>いま巻いている方向の先</b>へこちらから 1 個ずつ流す。
/// 「巻き方向を A / D で選び、その先にあるものを拾う」という操作がそのまま練習になる。
/// 出す順番は「糸の回復 → 魚をひるませる → 魚の回復」で固定し、
/// 効果が分かりやすいものから体験させる。
///
/// 【判定】
/// 3 種類すべてを拾ったらクリア。サブ目標として 1 種ずつチェックが入る。
/// 1 種拾うたびに、その種類の説明を吹き出しで割り込ませる。
///
/// 【注意】
/// 漂流物を拾えるのは<b>隙（ひるみ）の最中に実際に巻いているとき</b>だけ（釣り本体の仕様）。
/// リズムと巻きの操作はデータ側で許可しておくこと。
/// </summary>
public sealed class DriftPickupMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>ウキから漂流物を出す距離（メートル）。巻けば必ず通る位置。</summary>
    private const float SpawnDistanceMeters = 6.0f;

    /// <summary>次の漂流物を出すまでの間隔（秒・実時間）。</summary>
    private const float SpawnIntervalSeconds = 3.0f;

    /// <summary>出す順番（この順に 1 個ずつ流す）。</summary>
    private static readonly string[] SpawnOrder =
    {
        DriftItem.KindLineRecover,
        DriftItem.KindStun,
        DriftItem.KindFishRecover,
    };

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>種類ごとのサブ目標（拾ったらチェックが入る）。</summary>
    private readonly Dictionary<string, MissionObjective> objectiveByKind = new();

    /// <summary>次に漂流物を出すまでの残り秒（実時間）。</summary>
    private float spawnCooldown;

    /// <summary>拾った種類の数。</summary>
    private int pickedCount;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.DriftPickup;

    /// <summary>拾った種類の数。</summary>
    public override string ProgressText => $"{pickedCount} / {SpawnOrder.Length} 種";

    /// <summary>種類ごとのサブ目標を作り、拾得を購読する。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        spawnCooldown = 0f;
        pickedCount   = 0;

        objectiveByKind.Clear();
        objectiveByKind[DriftItem.KindLineRecover] = AddObjective("糸を直すものを拾おう");
        objectiveByKind[DriftItem.KindStun]        = AddObjective("魚をひるませるものを拾おう");
        objectiveByKind[DriftItem.KindFishRecover] = AddObjective("魚が元気になるものを拾おう");

        Subscribe(FishingEvents.DriftPickup, kind => OnPicked(ctx, kind));
    }

    /// <summary>
    /// 巻いている方向の先へ、まだ拾っていない種類の漂流物を流す。
    ///
    /// 「出し切ったら終わり」ではなく<b>海に無ければ出し直す</b>方式にしてある。
    /// 漂流物には寿命があり、拾う前に消えると二度と出せず詰まってしまうため。
    /// 同時に出るのは 1 個だけなので、狙って拾う練習としても成立する。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    protected override void OnUpdate(MissionContext ctx, float unscaledDelta)
    {
        spawnCooldown -= unscaledDelta;
        if (spawnCooldown > 0f) { return; }

        spawnCooldown = SpawnIntervalSeconds;

        string? kind = NextUnpickedKind();
        if (kind is null) { return; }
        if (IsKindAfloat(kind)) { return; }

        TrySpawn(ctx, kind);
    }

    /// <summary>
    /// まだ拾っていない種類のうち、出す順番がいちばん早いものを返す。
    /// </summary>
    /// <returns>出すべき種類。すべて拾い終えていれば null。</returns>
    private string? NextUnpickedKind()
    {
        for (int i = 0; i < SpawnOrder.Length; i++)
        {
            string kind = SpawnOrder[i];
            if (objectiveByKind.TryGetValue(kind, out var objective) && !objective.Done)
            {
                return kind;
            }
        }
        return null;
    }

    /// <summary>
    /// 指定の種類の漂流物がいま海に浮いているか。
    /// </summary>
    /// <param name="kind">調べる種類。</param>
    /// <returns>1 個でも浮いていれば true。</returns>
    private static bool IsKindAfloat(string kind)
    {
        foreach (var item in DriftItem.All)
        {
            if (item.Kind == kind) { return true; }
        }
        return false;
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 指定の種類の漂流物を、いま巻いている方向の先へ流す。
    /// 巻き方向が決まらない（ウキと竿先が重なっている等）ときは次の間隔でやり直す。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="kind">出す漂流物の種類。</param>
    private void TrySpawn(MissionContext ctx, string kind)
    {
        if (ctx.Controller is not { } controller) { return; }
        if (ctx.Drift is not { } manager) { return; }

        // 巻いた先＝ウキが進む向き。A / D の操舵ぶんも含んだ方向が返る。
        var direction = controller.ReelAimDirection;
        if (direction.SqrMagnitude <= 0f) { return; }

        var origin = controller.FloatWorldPosition;
        var position = new SEED.Vector3(
            origin.x + direction.x * SpawnDistanceMeters,
            controller.WaterSurfaceY(),
            origin.z + direction.z * SpawnDistanceMeters);

        if (!manager.SpawnScripted(kind, position))
        {
            SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: 漂流物「{kind}」を出せませんでした。");
        }
    }

    /// <summary>
    /// 漂流物を拾ったときの処理。初めて拾った種類ならチェックを入れて説明を割り込ませる。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="kind">拾った漂流物の種類。</param>
    private void OnPicked(MissionContext ctx, string kind)
    {
        if (!objectiveByKind.TryGetValue(kind, out var objective)) { return; }
        if (objective.Done) { return; }

        objective.Complete();
        pickedCount++;

        ctx.Interject(SlotOf(kind));

        if (AllObjectivesDone()) { MarkCleared(); }
    }

    /// <summary>
    /// 漂流物の種類に対応する台詞の場面を返す。
    /// </summary>
    /// <param name="kind">漂流物の種類。</param>
    /// <returns>台詞を引く場面。</returns>
    private static TutorialDialogueSlot SlotOf(string kind)
    {
        if (kind == DriftItem.KindStun)        { return TutorialDialogueSlot.DriftStun; }
        if (kind == DriftItem.KindFishRecover) { return TutorialDialogueSlot.DriftFishRecover; }
        return TutorialDialogueSlot.DriftLineRecover;
    }
}
