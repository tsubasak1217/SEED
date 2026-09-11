// ============================================================================
//  MissionFactory.cs
//  ミッションの種類から判定クラスを作る対応表。
// ============================================================================

/// <summary>
/// ミッションの種類（<see cref="MissionKind"/>）から判定クラスを作る
/// 【種類と実装の対応表の唯一の置き場】。
///
/// 【なぜ 1 か所へ集めるか】
/// 「種類を増やす」という作業を、
///  1. <see cref="MissionKind"/> に列挙子を足す
///  2. ここに 1 行足す
///  3. 判定クラスを 1 つ書く
/// の 3 手順に固定するため。進行側（<see cref="TutorialDirector"/>）は
/// 種類ごとの分岐を一切持たない。
/// </summary>
public static class MissionFactory
{
    /// <summary>
    /// 種類に対応する判定クラスを作る【生成の唯一の入口】。
    ///
    /// 未知の種類（列挙子を足したのにここへ書き忘れた場合）は警告を出し、
    /// 何もしない代わりに「移動」の判定を返さず null を返す。
    /// 呼び出し側はそのミッションを飛ばして次へ進む。
    /// </summary>
    /// <param name="kind">ミッションの種類。</param>
    /// <returns>判定クラス。未対応なら null。</returns>
    public static IMission? Create(MissionKind kind) => kind switch
    {
        MissionKind.Move        => new MoveMission(),
        MissionKind.Ready       => new ReadyMission(),
        MissionKind.Cast        => new CastMission(),
        MissionKind.Hook        => new HookMission(),
        MissionKind.Beat        => new BeatMission(),
        MissionKind.Reel        => new ReelMission(),
        MissionKind.CatchTarget => new CatchTargetMission(),
        MissionKind.ChainCatch  => new ChainCatchMission(),
        MissionKind.DriftPickup => new DriftPickupMission(),
        MissionKind.Land        => new LandMission(),
        MissionKind.Cutscene    => new CutsceneMission(),
        _ => LogUnsupported(kind),
    };

    /// <summary>
    /// 未対応の種類を警告して null を返す（switch 式の中から呼ぶための小道具）。
    /// </summary>
    /// <param name="kind">未対応だった種類。</param>
    /// <returns>常に null。</returns>
    private static IMission? LogUnsupported(MissionKind kind)
    {
        SEED.Debug.LogWarning($"[Mission] 種類「{kind}」に対応する判定クラスがありません（MissionFactory へ追加してください）。");
        return null;
    }
}
