// ============================================================================
//  CutsceneMission.cs
//  締めの演出（怪獣が海面から跳ね、カメラが追う）ミッション。
// ============================================================================

/// <summary>
/// チュートリアルの締めに流す演出ミッション。
///
/// 【流れ】
///  1. 画面奥（目標アクタの位置）に怪獣アクタを生成し、海面下へ沈めておく
///  2. 放物線を描いて跳ね上がり、着水するまでを演出する
///  3. カメラを怪獣へ寄せて注視する（MainCamera を毎フレーム補間で動かす）
///  4. 着水したら怪獣を片付け、達成にする（このあとディレクタが締めの台詞を出す）
///
/// 【カメラの扱い】
/// 会話のカメラ演出（DialogueCameraDirector）と同じ流儀で、
/// <b>目標の姿勢を毎フレーム求め、そこへ指数補間で寄る</b>。
/// 演出が終わったらカメラには何もしない（CameraMove が通常の構図へ自然に戻す）。
///
/// 【時間軸】
/// 演出は実時間で進める。締めの間はゲーム時間を止めておきたいため。
/// </summary>
public sealed class CutsceneMission : MissionBase
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>怪獣アクタの既定パス（データで上書きできる）。</summary>
    private const string DefaultKaijuActorPath = "assets://mainGame/actors/Fish/Lv10/kaiju.actor";

    /// <summary>ジャンプ全体の既定の所要秒数。</summary>
    private const float DefaultJumpSeconds = 3.2f;

    /// <summary>跳ね上がる高さ（メートル）。</summary>
    private const float JumpApexHeight = 14f;

    /// <summary>助走で進む水平距離（メートル。手前へ向かって跳ぶ）。</summary>
    private const float JumpTravelDistance = 26f;

    /// <summary>待機中に怪獣を沈めておく深さ（メートル。水面より下）。</summary>
    private const float SubmergedDepth = -8f;

    /// <summary>カメラが怪獣から離れて構える距離（メートル）。</summary>
    private const float CameraDistance = 34f;

    /// <summary>カメラの高さ（怪獣の中心からの相対。メートル）。</summary>
    private const float CameraHeight = 8f;

    /// <summary>カメラ補間の速さ（1 秒あたりの収束率。大きいほど速く寄る）。</summary>
    private const float CameraLerpRate = 3.0f;

    /// <summary>放物線の頂点を表す進捗（0〜1 の中央）。</summary>
    private const float ParabolaApex = 0.5f;

    /// <summary>放物線の係数（4 * t * (1 - t) が 0〜1 の山になる）。</summary>
    private const float ParabolaScale = 4f;

    /// <summary>回転を 1 周させるための度数。</summary>
    private const float FullTurnDegrees = 360f;

    /// <summary>ラジアンから度への変換で使う半周の度数。</summary>
    private const float HalfTurnDegrees = 180f;

    /// <summary>ゼロ除算を避けるための微小値。</summary>
    private const float DivideEpsilon = 1e-4f;

    /// <summary>メインカメラのアクタ名（シーン上の名前）。</summary>
    private const string MainCameraActorName = "MainCamera";

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>生成した怪獣アクタ（終了時に必ず破棄する）。</summary>
    private SEED.GameObject kaiju;

    /// <summary>怪獣の Transform（毎フレーム位置を書く）。</summary>
    private SEED.Transform? kaijuTransform;

    /// <summary>カメラの Transform（毎フレーム寄せる）。</summary>
    private SEED.Transform? cameraTransform;

    /// <summary>演出の開始地点（水面下の待機位置）。</summary>
    private SEED.Vector3 startPosition;

    /// <summary>演出で進む水平方向（正規化）。</summary>
    private SEED.Vector3 travelDirection = SEED.Vector3.Forward;

    /// <summary>演出の経過秒（実時間）。</summary>
    private float elapsed;

    /// <summary>演出の所要秒。</summary>
    private float jumpSeconds = DefaultJumpSeconds;

    // ─── IMission ────────────────────────────────────────────

    /// <summary>このミッションの種類。</summary>
    public override MissionKind Kind => MissionKind.Cutscene;

    /// <summary>演出中は進捗を出さない（プレイヤーの操作は無いため）。</summary>
    public override string ProgressText => string.Empty;

    /// <summary>
    /// 怪獣を生成して水面下へ沈め、カメラの参照を掴む。
    /// 生成に失敗しても演出時間だけは流して先へ進む（チュートリアルを詰まらせない）。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    protected override void OnBegin(MissionContext ctx)
    {
        elapsed     = 0f;
        jumpSeconds = ctx.Data.paramValue > 0f ? ctx.Data.paramValue : DefaultJumpSeconds;

        ResolveStartPosition(ctx);
        SpawnKaiju(ctx);
        ResolveCamera();

        // 通常のカメラ追従を止める。止めないと CameraMove が LateUpdate で
        // 毎フレーム上書きしてしまい、この演出のカメラ操作が一切見えない。
        TutorialRules.CameraSuspended = true;
    }

    /// <summary>
    /// 怪獣を放物線で跳ねさせ、カメラをそこへ寄せる。着水したら達成にする。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    protected override void OnUpdate(MissionContext ctx, float unscaledDelta)
    {
        elapsed += unscaledDelta;

        float progress = Easing.Progress01(elapsed, jumpSeconds);
        var position = ComputeJumpPosition(progress);

        ApplyKaijuPose(position, progress);
        ApplyCamera(position, unscaledDelta);

        if (progress >= 1f) { MarkCleared(); }
    }

    /// <summary>怪獣を片付ける【生成物の後始末の唯一の出口】。</summary>
    /// <param name="ctx">周辺への窓口（未使用）。</param>
    protected override void OnEnd(MissionContext ctx)
    {
        // カメラは必ず通常の追従へ返す【預かったものを返す唯一の出口】
        TutorialRules.CameraSuspended = false;

        if (kaiju.IsValid) { kaiju.Destroy(); }
        kaiju           = default;
        kaijuTransform  = null;
        cameraTransform = null;
    }

    // ─── 内部処理: 準備 ─────────────────────────────────────

    /// <summary>
    /// 演出の開始地点と進む向きを決める。
    ///
    /// 目標アクタがあればその位置と向き、無ければウキの沖側を使う。
    /// どちらも取れなければ原点から手前向きへ跳ばす（絵にはならないが破綻はしない）。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    private void ResolveStartPosition(MissionContext ctx)
    {
        if (ctx.Data.targetActor is { IsValid: true } target)
        {
            var center = target.Position;
            startPosition = new SEED.Vector3(center.x, center.y + SubmergedDepth, center.z);
            travelDirection = NormalizeHorizontal(target.Forward);
            return;
        }

        if (ctx.Controller is { } controller)
        {
            var bob = controller.FloatWorldPosition;
            var rod = controller.RodTipWorldPosition;

            // 岸（竿先）へ向かって跳ねてくると、手前へ迫る絵になって迫力が出る
            travelDirection = NormalizeHorizontal(new SEED.Vector3(rod.x - bob.x, 0f, rod.z - bob.z));
            startPosition = new SEED.Vector3(
                bob.x - travelDirection.x * JumpTravelDistance,
                controller.WaterSurfaceY() + SubmergedDepth,
                bob.z - travelDirection.z * JumpTravelDistance);
            return;
        }

        SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: 演出位置を決められませんでした（目標アクタも釣り本体も見つかりません）。");
        startPosition   = SEED.Vector3.Zero;
        travelDirection = SEED.Vector3.Forward;
    }

    /// <summary>
    /// 怪獣アクタを生成して待機位置へ置く。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    private void SpawnKaiju(MissionContext ctx)
    {
        string path = string.IsNullOrWhiteSpace(ctx.Data.paramText)
            ? DefaultKaijuActorPath
            : ctx.Data.paramText;

        kaiju = SEED.GameObject.Instantiate(path);
        if (!kaiju.IsValid)
        {
            SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: 怪獣アクタ「{path}」を生成できませんでした。演出は時間だけ流します。");
            return;
        }

        if (kaiju.GetComponent<SEED.Transform>() is { } t && t.IsValid)
        {
            kaijuTransform = t;
            t.Position = startPosition;
        }
    }

    /// <summary>メインカメラの Transform を掴む（見つからなければカメラは動かさない）。</summary>
    private void ResolveCamera()
    {
        var camera = SEED.GameObject.Find(MainCameraActorName);
        if (!camera.IsValid) { return; }
        if (camera.GetComponent<SEED.Transform>() is { } t && t.IsValid) { cameraTransform = t; }
    }

    // ─── 内部処理: 演出 ─────────────────────────────────────

    /// <summary>
    /// 進捗から怪獣の位置を求める【放物線の唯一の計算点】。
    /// 水平は等速、垂直は 4t(1-t) の山（0 で水面下、中央で頂点、1 で再び水面下）。
    /// </summary>
    /// <param name="progress">演出の進捗（0〜1）。</param>
    /// <returns>そのフレームのワールド位置。</returns>
    private SEED.Vector3 ComputeJumpPosition(float progress)
    {
        float horizontal = JumpTravelDistance * progress;
        float height = ParabolaScale * progress * (1f - progress) * (JumpApexHeight - SubmergedDepth);

        return new SEED.Vector3(
            startPosition.x + travelDirection.x * horizontal,
            startPosition.y + height,
            startPosition.z + travelDirection.z * horizontal);
    }

    /// <summary>
    /// 怪獣の位置と向きを適用する。
    /// 進む向きへ体を向け、頂点を過ぎたら頭を下げて着水姿勢にする。
    /// </summary>
    /// <param name="position">このフレームの位置。</param>
    /// <param name="progress">演出の進捗（0〜1）。</param>
    private void ApplyKaijuPose(SEED.Vector3 position, float progress)
    {
        if (kaijuTransform is not { } t || !t.IsValid) { return; }

        t.Position = position;

        // 進行方向のヨー角。エンジンの前方向は +Z なので Atan2(x, z)。
        float yaw = SEED.Mathf.Atan2(travelDirection.x, travelDirection.z) * SEED.Mathf.Rad2Deg;

        // 上昇中は頭を上げ、下降中は頭を下げる（頂点で 0 度）。
        // エンジン規約では pitch の正が「下を向く」なので、上昇側を負にする。
        float pitch = (progress - ParabolaApex) * HalfTurnDegrees * ParabolaApex;

        t.Rotation = new SEED.Vector3(pitch, yaw, 0f);
    }

    /// <summary>
    /// カメラを怪獣へ寄せて注視させる。
    ///
    /// 目標の姿勢（位置と向き）を毎フレーム求め、指数補間でそこへ寄る。
    /// 会話のカメラ演出と同じ流儀なので、動きの手触りが他の演出と揃う。
    /// </summary>
    /// <param name="lookAt">注視したい点（怪獣の位置）。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    private void ApplyCamera(SEED.Vector3 lookAt, float unscaledDelta)
    {
        if (cameraTransform is not { } cam || !cam.IsValid) { return; }

        // 怪獣の進行方向の正面へ回り込み、少し高い位置から見下ろす構図を作る
        var desired = new SEED.Vector3(
            lookAt.x + travelDirection.x * CameraDistance,
            lookAt.y + CameraHeight,
            lookAt.z + travelDirection.z * CameraDistance);

        float blend = ExponentialBlend(CameraLerpRate, unscaledDelta);
        cam.Position = SEED.Vector3.Lerp(cam.Position, desired, blend);

        // 注視: カメラから怪獣へのベクトルからヨー・ピッチを作る
        var toTarget = lookAt - cam.Position;
        float distance = toTarget.Magnitude;
        if (distance <= DivideEpsilon) { return; }

        float yaw = SEED.Mathf.Atan2(toTarget.x, toTarget.z) * SEED.Mathf.Rad2Deg;
        float pitch = -SEED.Mathf.Asin(SEED.Mathf.Clamped(toTarget.y / distance, -1f, 1f)) * SEED.Mathf.Rad2Deg;

        cam.Rotation = new SEED.Vector3(pitch, yaw, 0f);
    }

    // ─── 内部処理: 小道具 ───────────────────────────────────

    /// <summary>
    /// フレームレートに依存しない指数補間の係数を返す。
    /// </summary>
    /// <param name="rate">1 秒あたりの収束率。</param>
    /// <param name="deltaTime">このフレームの経過秒。</param>
    /// <returns>0〜1 の補間係数。</returns>
    private static float ExponentialBlend(float rate, float deltaTime)
        => 1f - SEED.Mathf.Exp(-SEED.Mathf.Max(rate, 0f) * SEED.Mathf.Max(deltaTime, 0f));

    /// <summary>
    /// 水平成分だけを取り出して正規化する（長さ 0 なら前方向へフォールバック）。
    /// </summary>
    /// <param name="v">元のベクトル。</param>
    /// <returns>水平方向の単位ベクトル。</returns>
    private static SEED.Vector3 NormalizeHorizontal(SEED.Vector3 v)
    {
        var flat = new SEED.Vector3(v.x, 0f, v.z);
        return flat.SqrMagnitude > DivideEpsilon ? flat.Normalized : SEED.Vector3.Forward;
    }
}
