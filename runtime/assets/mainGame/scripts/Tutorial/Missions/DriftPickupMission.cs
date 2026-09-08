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

    /// <summary>並べ直しを試みる間隔（秒）。位置が決まらない間の再試行を間引く。</summary>
    private const float SpawnRetryIntervalSeconds = 0.5f;

    /// <summary>
    /// 一直線に並べるときの、隣り合う漂流物どうしの最小間隔（メートル）。
    /// 近すぎると 1 回の巻きで 2 個まとめて拾ってしまい、解説が重なる。
    /// </summary>
    private const float MinSpacingMeters = 2.0f;

    /// <summary>一直線に並べるときの、隣り合う漂流物どうしの最大間隔（メートル）。</summary>
    private const float MaxSpacingMeters = 4.0f;

    /// <summary>
    /// いちばん竿先寄りの漂流物と竿先のあいだに必ず空ける余白（メートル）。
    ///
    /// 巻き取りはウキが竿先へ着いた時点で完了するので、竿先ぎりぎりに置いた個体は
    /// 拾う前に巻き取りが終わってしまう。<b>並べる範囲は「線長 − この余白」まで</b>に限る。
    /// </summary>
    private const float NearRodMarginMeters = 1.0f;

    /// <summary>間隔を割り出すときの区間数の加算（残り n 個なら n+1 等分して手前から置く）。</summary>
    private const int SpacingSegmentBias = 1;

    /// <summary>1 個目を置く区間の番号（ウキから数えて 1 区間先）。</summary>
    private const int FirstSlotIndex = 1;

    /// <summary>並べる順番（この順にウキから近い側へ置く）。</summary>
    private static readonly string[] SpawnOrder =
    {
        DriftItem.KindLineRecover,
        DriftItem.KindStun,
        DriftItem.KindFishRecover,
    };

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>種類ごとのサブ目標（拾ったらチェックが入る）。</summary>
    private readonly Dictionary<string, MissionObjective> objectiveByKind = new();

    /// <summary>並べ直しを試みるまでの残り時間（秒）。</summary>
    private float spawnCooldown;

    /// <summary>拾った種類の数。</summary>
    private int pickedCount;

    /// <summary>
    /// 片付けのために <see cref="DriftItem.All"/> を写す作業用リスト（毎フレームの確保を避ける）。
    /// <see cref="DriftItem.Kill"/> は登録簿を書き換えるので、直接列挙しながら消してはいけない。
    /// </summary>
    private readonly List<DriftItem> workItems = new();

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
    /// まだ拾っていない漂流物を、巻き方向の一直線上へまとめて並べる。
    ///
    /// 【出し直す方式】
    /// 「出し切ったら終わり」ではなく<b>海に 1 個も無ければ並べ直す</b>。
    /// 投げ直し・糸切れで漂流物が失われても詰まらないようにするため。
    ///
    /// 【まとめて並べる理由】
    /// このミッションは左右の操舵を止めてあるので、まっすぐ巻けば一直線上の
    /// 漂流物を手前から順に必ず拾える。1 個ずつ出すより手順が読みやすい。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    protected override void OnUpdate(MissionContext ctx, float unscaledDelta)
    {
        // 掛かっていない間（糸切れ・投げ直しの最中）は、置いた漂流物を一度片付ける。
        // 次に掛かったときの「ウキ → 竿先」の線はまったく別の場所になるので、
        // 古い位置に残った個体は拾えないまま居座り、
        // 「1 個でも浮いていたら並べ直さない」規則と噛み合ってミッションが詰む。
        if (ctx.Controller is not { IsHooked: true })
        {
            ClearAfloatItems();
            return;
        }

        // 拾い残しが 1 つでも浮いている間は何もしない（並べ直して二重に出さない）
        if (AnyItemAfloat()) { return; }

        // まだ拾っていない種類が無ければ、このミッションはもう並べる物が無い
        if (CountUnpicked() <= 0) { return; }

        spawnCooldown -= unscaledDelta;
        if (spawnCooldown > 0f) { return; }
        spawnCooldown = SpawnRetryIntervalSeconds;

        TrySpawnLine(ctx);
    }

    /// <summary>
    /// まだ拾っていない種類の数を数える。
    /// </summary>
    /// <returns>未取得の種類の数（0 なら全部拾っている）。</returns>
    private int CountUnpicked()
    {
        int count = 0;
        for (int i = 0; i < SpawnOrder.Length; i++)
        {
            if (objectiveByKind.TryGetValue(SpawnOrder[i], out var objective) && !objective.Done)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 漂流物が 1 個でも水面に出ているか（種類は問わない）。
    /// </summary>
    /// <returns>1 個でも浮いていれば true。</returns>
    private static bool AnyItemAfloat() => DriftItem.All.Count > 0;

    /// <summary>
    /// 浮いている漂流物をすべて消す【並べ直しのための片付け】。
    /// <see cref="DriftItem.Kill"/> が登録簿を書き換えるので、必ず作業用リストへ写してから回す。
    /// </summary>
    private void ClearAfloatItems()
    {
        if (DriftItem.All.Count == 0) { return; }

        workItems.Clear();
        foreach (var item in DriftItem.All) { workItems.Add(item); }
        for (int i = 0; i < workItems.Count; i++) { workItems[i].Kill(); }
        workItems.Clear();
    }

    /// <summary>
    /// まだ拾っていない漂流物を<b>巻き方向の一直線上へ等間隔に</b>並べる
    /// 【台本生成の唯一の実装】。
    ///
    /// 【なぜ一直線なのか】
    /// このミッションは左右の操舵（A / D）を止めてあるので、巻けばウキは
    /// 「ウキ → 竿先」の直線上を手前へ進む。その直線上に等間隔で置けば、
    /// まっすぐ巻くだけで必ず順番どおり 1 個ずつ拾える。
    ///
    /// 【位置の決め方】
    /// 並べられる範囲は「ウキ → 竿先の水平距離 − <see cref="NearRodMarginMeters"/>」。
    /// これを「残り個数 + 1」で等分し、ウキ側から 1 区間目・2 区間目…へ置く。
    /// 間隔は近すぎ／遠すぎを避けるため <see cref="MinSpacingMeters"/> 〜
    /// <see cref="MaxSpacingMeters"/> に丸めたうえで、<b>いちばん遠い個体が必ず
    /// 範囲内へ収まるように詰める</b>。
    ///
    /// 【間隔より「線上に収める」を優先する理由】
    /// 投げが短いと、丸めた間隔のままでは<b>竿先より奥に置いてしまい</b>、
    /// 巻き切っても拾えない個体が残る（＝ミッションが詰む）。
    /// 間隔が最小を下回るのは「解説が 2 つ同時に出る」だけで済むが、
    /// 範囲外に置くのは「二度と拾えない」ので、収めるほうを常に優先する。
    /// 生成位置は必ず線上なので、拾い判定（DriftItem の当たり半径）に確実に入る。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    private void TrySpawnLine(MissionContext ctx)
    {
        if (ctx.Controller is not { } controller) { return; }
        if (ctx.Drift is not { } manager) { return; }

        // 巻いたときにウキが進む向き（操舵が止まっているので「ウキ → 竿先」と一致する）
        var direction = controller.ReelAimDirection;
        if (direction.SqrMagnitude <= 0f) { return; }

        var origin = controller.FloatWorldPosition;
        var rodTip = controller.RodTipWorldPosition;

        int remaining = CountUnpicked();
        if (remaining <= 0) { return; }

        // ウキ → 竿先の水平距離から、竿先手前の余白を引いた「並べられる範囲」
        float dx = rodTip.x - origin.x;
        float dz = rodTip.z - origin.z;
        float lineLength = SEED.Mathf.Sqrt(dx * dx + dz * dz);
        float span = lineLength - NearRodMarginMeters;

        // 余白しか無い（＝ほぼ巻き切っている）ときは並べる余地が無いので投げ直し・巻き戻しを待つ
        if (span <= 0f) { return; }

        // 見やすい間隔へ丸めたうえで、いちばん遠い個体（span/remaining の位置）が
        // 必ず範囲内へ収まるように詰める（丸めた間隔が範囲を食い破らないようにする）
        float spacing = SEED.Mathf.Clamped(
            span / (remaining + SpacingSegmentBias),
            MinSpacingMeters,
            MaxSpacingMeters);
        spacing = SEED.Mathf.Min(spacing, span / remaining);

        float surfaceY = controller.WaterSurfaceY();

        // 未取得の種類を SpawnOrder の順に、ウキへ近い側から並べる
        int slot = FirstSlotIndex;
        for (int i = 0; i < SpawnOrder.Length; i++)
        {
            string kind = SpawnOrder[i];
            if (!objectiveByKind.TryGetValue(kind, out var objective) || objective.Done) { continue; }

            float distance = spacing * slot;
            var position = new SEED.Vector3(
                origin.x + direction.x * distance,
                surfaceY,
                origin.z + direction.z * distance);

            if (!manager.SpawnScripted(kind, position))
            {
                SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: 漂流物「{kind}」を出せませんでした。");
            }

            slot++;
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
