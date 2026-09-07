// ============================================================================
//  MoveMission.cs
//  「指定地点まで歩く」ミッションの判定。
// ============================================================================

/// <summary>
/// 指定地点まで歩くミッション【移動の到達判定の唯一の実装】。
///
/// 【流れ】
/// 目標地点（<see cref="TutorialMission.targetActor"/>）へ目印アクタを立て、
/// プレイヤーがその半径内へ入ったらクリア。目印はミッションの終了時に必ず片付ける。
///
/// 【目印について】
/// 着水点マーカー（CastMarker）と同じ「ワールドに置いた 3D キャンバス＋矢印スプライト」の
/// 作りをした専用アクタを生成する。素材を差し替えたいときは .actor を触るだけでよい。
///
/// 【進捗表示】
/// 残り距離をメートルで出す。「あと何歩か」が読めると、方向を間違えたことに自分で気づける。
/// </summary>
public sealed class MoveMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>到達とみなす半径の既定値（メートル）。</summary>
    private const float DefaultArriveRadius = 2.5f;

    /// <summary>目印アクタの既定パス（データで上書きできる）。</summary>
    private const string DefaultMarkerActorPath = "assets://mainGame/actors/UI/MissionMarker.actor";

    /// <summary>目印を浮かせる高さ（メートル。地面に埋まらないよう少し上げる）。</summary>
    private const float MarkerHeightOffset = 0.6f;

    /// <summary>距離を「0 メートル」と表示しないための下限。</summary>
    private const float DistanceDisplayMin = 0f;

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>生成した目印アクタ（終了時に必ず破棄する）。</summary>
    private SEED.GameObject marker;

    /// <summary>到達とみなす半径（メートル）。</summary>
    private float arriveRadius = DefaultArriveRadius;

    /// <summary>目標地点のワールド座標（開始時に 1 度だけ控える）。</summary>
    private SEED.Vector3 goalPosition;

    /// <summary>目標地点を控えられたか（未設定なら判定できないので即クリア扱いにする）。</summary>
    private bool goalResolved;

    /// <summary>いまの残り距離（メートル。進捗表示に使う）。</summary>
    private float remainingDistance;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.Move;

    /// <summary>残り距離の 1 行（例「あと 12.3 m」）。</summary>
    public override string ProgressText
        => goalResolved ? $"あと {SEED.Mathf.Max(remainingDistance, DistanceDisplayMin):F1} m" : string.Empty;

    /// <summary>
    /// 目標地点を控え、目印アクタを立てる。
    /// 目標が未設定なら判定しようがないので、警告を出して即クリアにする（詰まらせない）。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        arriveRadius = ctx.Data.paramValue > 0f ? ctx.Data.paramValue : DefaultArriveRadius;

        if (ctx.Data.targetActor is { IsValid: true } target)
        {
            goalPosition = target.Position;
            goalResolved = true;
            SpawnMarker(ctx);
            return;
        }

        SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: 目標アクタが未設定のため到達判定ができません。即クリアにします。");
        MarkCleared();
    }

    /// <summary>
    /// プレイヤーと目標の水平距離を測り、半径内に入ったらクリアにする。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（未使用）。</param>
    protected override void OnUpdate(MissionContext ctx, float unscaledDelta)
    {
        if (!goalResolved) { return; }
        if (ctx.Player is not { } player) { return; }

        remainingDistance = HorizontalDistance(player.WorldPosition, goalPosition);
        if (remainingDistance <= arriveRadius) { MarkCleared(); }
    }

    /// <summary>目印アクタを片付ける【生成物の後始末の唯一の出口】。</summary>
    /// <param name="ctx">周辺への窓口（未使用）。</param>
    protected override void OnEnd(MissionContext ctx)
    {
        if (marker.IsValid) { marker.Destroy(); }
        marker = default;
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 目標地点へ目印アクタを立てる。
    /// パスはデータで上書きでき、読み込みに失敗しても判定は続く（目印が出ないだけ）。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    private void SpawnMarker(MissionContext ctx)
    {
        string path = string.IsNullOrWhiteSpace(ctx.Data.paramText)
            ? DefaultMarkerActorPath
            : ctx.Data.paramText;

        marker = SEED.GameObject.Instantiate(path);
        if (!marker.IsValid)
        {
            SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: 目印アクタ「{path}」を生成できませんでした。");
            return;
        }

        if (marker.GetComponent<SEED.Transform>() is { } markerTransform && markerTransform.IsValid)
        {
            markerTransform.Position = new SEED.Vector3(
                goalPosition.x,
                goalPosition.y + MarkerHeightOffset,
                goalPosition.z);
        }
    }

    /// <summary>
    /// 2 点の水平距離（XZ 平面）を返す。
    /// 上下の差は到達判定に関係ない（プレイヤーは斜面の上を歩く）ので無視する。
    /// </summary>
    /// <param name="a">点 A。</param>
    /// <param name="b">点 B。</param>
    /// <returns>水平距離（メートル）。</returns>
    private static float HorizontalDistance(SEED.Vector3 a, SEED.Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return SEED.Mathf.Sqrt(dx * dx + dz * dz);
    }
}
