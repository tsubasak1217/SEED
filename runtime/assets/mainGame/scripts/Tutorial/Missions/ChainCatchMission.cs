// ============================================================================
//  ChainCatchMission.cs
//  「魚で魚を釣る（わらしべ連鎖）」ミッションの判定。
// ============================================================================

/// <summary>
/// 掛かった魚をより大きな魚に食わせる（わらしべ連鎖）ミッション。
///
/// 【台本】
/// 連鎖の相手が来ないと成立しないので、掛かっている魚のそばへ
/// 指定レベルの魚を<b>こちらから出す</b>（FishManager の位置指定生成）。
/// 一定間隔で補充し続けるので、逃げられても必ずやり直せる。
///
/// 【判定】
/// <c>fishing.level_up</c>（乗り換え成立）を指定回数受けたらクリア。
///
/// 【注意】
/// 連鎖が成立するのは<b>隙（ひるみ）の最中</b>だけ（釣り本体の仕様）。
/// プレイヤーがリズムを刻んで隙を作るまで成立しないので、
/// リズムの操作はデータ側で許可しておくこと。
/// </summary>
public sealed class ChainCatchMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>連鎖の相手を出すレベルの既定値（1 始まり）。</summary>
    private const int DefaultPreyLevel = 2;

    /// <summary>連鎖の相手は「掛ける魚のレベル」より何段上か。</summary>
    private const int PreyLevelStep = 1;

    /// <summary>レベル番号（1 始まり）を生成 API の添字（0 始まり）へ直す差分。</summary>
    private const int LevelNumberToIndex = 1;

    /// <summary>クリアに必要な連鎖回数の既定値。</summary>
    private const int DefaultRequiredCount = 1;

    /// <summary>相手を出す間隔（秒・実時間）。短すぎると海が魚で埋まる。</summary>
    private const float SpawnIntervalSeconds = 4.0f;

    /// <summary>掛かっている魚から相手を出す距離のばらつき半径（メートル）。</summary>
    private const float SpawnRadius = 3.0f;

    /// <summary>
    /// 相手を出す回数の上限。
    /// 台本で出した魚は FishManager の個体管理に載らない（＝自動で片付かない）ので、
    /// 際限なく出すと海が魚で埋まる。上限に達しても先に出した魚は残るので詰まらない。
    /// </summary>
    private const int MaxSpawnCount = 12;

    /// <summary>魚が掛かっていないときに出す案内。</summary>
    private const string HintNoFish = "まずは魚を掛けよう";

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>成立した連鎖の回数。</summary>
    private int chainCount;

    /// <summary>クリアに必要な連鎖回数。</summary>
    private int requiredCount = DefaultRequiredCount;

    /// <summary>相手を出すレベル（1 始まり）。</summary>
    private int preyLevel = DefaultPreyLevel;

    /// <summary>次に相手を出すまでの残り秒（実時間）。</summary>
    private float spawnCooldown;

    /// <summary>これまでに相手を出した回数。</summary>
    private int spawnCount;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.ChainCatch;

    /// <summary>連鎖の回数。</summary>
    public override string ProgressText => $"{chainCount} / {requiredCount} 回";

    /// <summary>必要回数と相手のレベルを控え、連鎖の成立を購読する。</summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        chainCount    = 0;
        requiredCount = ctx.Data.paramCount > 0 ? ctx.Data.paramCount : DefaultRequiredCount;
        // 出現制限のレベル（＝プレイヤーが最初に掛ける魚）の 1 つ上を連鎖の相手にする。
        // ルール上書きは「制限レベルの魚しか湧かない」ので、相手をそこから採ると
        // 掛ける魚が居なくなってしまう。
        preyLevel = ctx.Data.fishLevelFilter > 0
            ? ctx.Data.fishLevelFilter + PreyLevelStep
            : DefaultPreyLevel;
        spawnCooldown = 0f;
        spawnCount    = 0;

        Subscribe(FishingEvents.LevelUp, OnChained);
    }

    /// <summary>
    /// 掛かっている魚のそばへ、一定間隔で連鎖の相手を出し続ける。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    protected override void OnUpdate(MissionContext ctx, float unscaledDelta)
    {
        if (spawnCount >= MaxSpawnCount) { return; }

        spawnCooldown -= unscaledDelta;
        if (spawnCooldown > 0f) { return; }

        spawnCooldown = SpawnIntervalSeconds;
        TrySpawnPrey(ctx);
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>連鎖が成立したときの処理。</summary>
    private void OnChained()
    {
        chainCount++;
        if (chainCount >= requiredCount) { MarkCleared(); }
    }

    /// <summary>
    /// 掛かっている魚のそばへ相手を 1 匹出す。
    /// 掛かっていない・マネージャが居ないときは何もしない（次の間隔でまた試す）。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    private void TrySpawnPrey(MissionContext ctx)
    {
        if (ctx.Controller is not { } controller) { return; }
        if (!controller.HookedFishBaitActive) { return; }
        if (ctx.Fish is not { } manager) { return; }

        var center = controller.HookedFishBaitPosition;
        if (!manager.SpawnOneNear(preyLevel - LevelNumberToIndex, center, SpawnRadius, out _))
        {
            SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: Lv{preyLevel} の魚を出せませんでした（レベル定義を確認してください）。");
            return;
        }

        spawnCount++;
    }
}
