// ============================================================================
//  CutsceneMission.cs
//  締めの演出（怪獣が海面から跳ね、カメラが追う）ミッション。
// ============================================================================

/// <summary>
/// チュートリアルの締めに流す演出ミッション。
///
/// 【流れ】
///  1. 画面の奥（既定は通常カメラの視界の奥・海の上）に怪獣アクタを生成し、海面下へ沈めておく
///  2. 放物線を描いて跳ね上がり、着水するまでを演出する
///  3. カメラ制御が指定されていれば怪獣へ寄せて注視する（既定は通常の追従のまま触らない）
///  4. 着水したら怪獣を片付け、達成にする（このあとディレクタが締めの台詞を出す）
///
/// 【カメラの扱い（3 通り。ミッションデータの「演出:カメラ制御」で決まる）】
/// <list type="bullet">
///   <item><b>FollowNormal（既定）</b> … カメラには<b>一切触らない</b>。移動中と同じ
///         通常の追従（CameraMove）のまま、画面の奥で怪獣が跳ねる絵になる。
///         <see cref="TutorialRules.CameraSuspended"/> も立てず、画角の指定も無効。</item>
///   <item>Orbit … 怪獣の進行方向を基準に「距離・高さ・方位角」で回り込んだ位置から注視する</item>
///   <item>TargetActor … カメラ目標アクタの位置・回転（＋Camera があれば画角）へ寄る
///         （会話のカメラ演出 DialogueCameraDirector と同じ流儀）</item>
/// </list>
/// Orbit / TargetActor では<b>目標の姿勢を毎フレーム求め、そこへ指数補間で寄る</b>。
/// 画角を指定した場合は演出中だけ MainCamera の視野角を上書きし、終了時に必ず元へ戻す。
/// 姿勢そのものは終了時に戻さない（CameraMove が通常の構図へ自然に戻す）。
///
/// 【調整値の出どころ】
/// 怪獣の跳ね方・カメラの構図はすべてミッションデータ（インスペクタの「演出:〜」）で
/// 決まる。読み出しと既定値の補正は <see cref="CutsceneSettings"/> が引き受けるので、
/// このクラスは「補正済みの値をどう使うか」だけを持つ。
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

    // 怪獣の跳ね方とカメラの構図の既定値は CutsceneSettings が持つ
    // （インスペクタの「演出:〜」と同じ値をここで二重に定義しない）。

    /// <summary>放物線の頂点を表す進捗（0〜1 の中央）。</summary>
    private const float ParabolaApex = 0.5f;

    /// <summary>放物線の係数（4 * t * (1 - t) が 0〜1 の山になる）。</summary>
    private const float ParabolaScale = 4f;

    /// <summary>直角（度）。カメラの前方向から真横（画面右）を作るのに使う。</summary>
    private const float RightAngleDegrees = 90f;

    /// <summary>水面の高さが取れなかったときに使う高さ（メートル）。</summary>
    private const float DefaultWaterSurfaceY = 0f;

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

    /// <summary>カメラの Camera コンポーネント（画角を触るときだけ使う。無ければ null）。</summary>
    private SEED.Camera? cameraComponent;

    /// <summary>
    /// このミッションの調整値（データから読んで既定値で補正済み）。
    /// OnBegin で差し替わるまでは全項目が既定値。
    /// </summary>
    private CutsceneSettings settings = new();

    /// <summary>
    /// 演出中に使う画角（度）。<see cref="CutsceneSettings.FieldOfViewUnspecified"/> なら画角を触らない。
    /// カメラ目標アクタの Camera があればその値、無ければデータの「演出:カメラ画角(度)」。
    /// </summary>
    private float desiredFieldOfView = CutsceneSettings.FieldOfViewUnspecified;

    /// <summary>
    /// 演出前のカメラの画角（度）。<see cref="CutsceneSettings.FieldOfViewUnspecified"/> なら
    /// 「預かっていない＝戻す必要が無い」を表す。
    /// </summary>
    private float originalFieldOfView = CutsceneSettings.FieldOfViewUnspecified;

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

        // 以降の計算はすべてこの調整値を見る（データの読み取りはここ 1 箇所だけ）
        settings = CutsceneSettings.FromMission(ctx.Data);

        // カメラは出現位置の基準（視界基準）でも使うので、位置を決める前に掴んでおく
        ResolveCamera();
        ResolveStartPosition(ctx);
        SpawnKaiju(ctx);

        // 通常のカメラ追従を止めるのは、この演出がカメラを握るときだけ。
        // 止めないと CameraMove が LateUpdate で毎フレーム上書きしてしまい、
        // 演出のカメラ操作が一切見えない。逆に FollowNormal では止めてはいけない
        // （通常の追従のままにするのがこのモードの目的）。
        if (settings.UsesCameraControl) { TutorialRules.CameraSuspended = true; }
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

    /// <summary>怪獣を片付け、預かったカメラの画角を戻す【後始末の唯一の出口】。</summary>
    /// <param name="ctx">周辺への窓口（未使用）。</param>
    protected override void OnEnd(MissionContext ctx)
    {
        // カメラを止めたときだけ通常の追従へ返す【預かったものを返す唯一の出口】。
        // 無条件に false にすると、他の演出（KaijuStoryTrigger など）が
        // 止めている最中にこのミッションが終わったときに横取りして解除してしまう。
        if (settings.UsesCameraControl) { TutorialRules.CameraSuspended = false; }

        RestoreFieldOfView();

        if (kaiju.IsValid) { kaiju.Destroy(); }
        kaiju              = default;
        kaijuTransform     = null;
        cameraTransform    = null;
        cameraComponent    = null;
        desiredFieldOfView = CutsceneSettings.FieldOfViewUnspecified;
    }

    // ─── 内部処理: 準備 ─────────────────────────────────────

    /// <summary>
    /// 演出の開始地点と進む向きを決める【出現位置の唯一の入口】。
    ///
    /// データの「演出:出現位置の基準」で 2 通りに分かれる。
    /// 視界基準はメインカメラが要るので、掴めていなければ目標アクタ／ウキ基準へ落ちる。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    private void ResolveStartPosition(MissionContext ctx)
    {
        if (settings.SpawnAnchor == CutsceneSpawnAnchor.CameraView && TryResolveCameraViewStart(ctx)) { return; }

        ResolveTargetOrFloatStart(ctx);
    }

    /// <summary>
    /// 通常カメラの視界を基準に、開始地点と進む向きを決める
    /// 【画面の奥で跳ねさせる構図の唯一の計算点】。
    ///
    /// カメラのヨー角から水平の前方向・右方向を作り、
    /// 「前へ カメラからの距離 ＋ 横へ 横ずれ」だけ進んだ点を開始地点にする
    /// （高さは水面から待機の深さだけ沈めた位置）。カメラの俯角は使わない。
    /// 高さ方向にカメラのピッチを混ぜると、見上げ／見下ろしのたびに
    /// 怪獣が水面から浮いたり沈んだりしてしまうため。
    /// 進む向きは「画面右方向」を 0 度として、跳ぶ方向の指定ぶん水平に回した向き。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <returns>決められたら true。メインカメラを掴めていなければ false。</returns>
    private bool TryResolveCameraViewStart(MissionContext ctx)
    {
        if (cameraTransform is not { } cam || !cam.IsValid) { return false; }

        // カメラのヨー角（度）。エンジンの前方向は +Z なので (sin, 0, cos) が前、
        // そこから直角ぶん回した (cos, 0, -sin) が真横（画面右）になる。
        float cameraYaw  = cam.Rotation.y;
        float yawRadians = cameraYaw * SEED.Mathf.Deg2Rad;
        var   forward    = new SEED.Vector3(SEED.Mathf.Sin(yawRadians), 0f,  SEED.Mathf.Cos(yawRadians));
        var   right      = new SEED.Vector3(SEED.Mathf.Cos(yawRadians), 0f, -SEED.Mathf.Sin(yawRadians));

        var origin = cam.Position;
        startPosition = new SEED.Vector3(
            origin.x + forward.x * settings.CameraViewDistance + right.x * settings.CameraViewLateral,
            ResolveWaterSurfaceY(ctx) + settings.SubmergedOffsetY,
            origin.z + forward.z * settings.CameraViewDistance + right.z * settings.CameraViewLateral);

        // 跳ぶ向き: 画面右方向（カメラのヨー + 直角）から、指定の角度ぶんさらに回す
        float travelYaw = (cameraYaw + RightAngleDegrees + settings.JumpDirectionDegrees) * SEED.Mathf.Deg2Rad;
        travelDirection = new SEED.Vector3(SEED.Mathf.Sin(travelYaw), 0f, SEED.Mathf.Cos(travelYaw));
        return true;
    }

    /// <summary>
    /// 水面の高さ（ワールド Y）を求める。
    /// 釣り本体があればその水面、無ければ目標アクタの高さ、どちらも無ければ既定値。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    /// <returns>水面の高さ（メートル）。</returns>
    private float ResolveWaterSurfaceY(MissionContext ctx)
    {
        if (ctx.Controller is { } controller) { return controller.WaterSurfaceY(); }
        if (ctx.Data.targetActor is { IsValid: true } target) { return target.Position.y; }

        SEED.Debug.LogWarning($"[Mission] {ctx.Data.id}: 水面の高さが取れないので {DefaultWaterSurfaceY} m として演出します。");
        return DefaultWaterSurfaceY;
    }

    /// <summary>
    /// 目標アクタ（無ければウキの沖側）を基準に開始地点と進む向きを決める（従来の決め方）。
    ///
    /// 目標アクタがあればその位置と向き、無ければウキの沖側を使う。
    /// どちらも取れなければ原点から手前向きへ跳ばす（絵にはならないが破綻はしない）。
    /// </summary>
    /// <param name="ctx">周辺への窓口。</param>
    private void ResolveTargetOrFloatStart(MissionContext ctx)
    {
        if (ctx.Data.targetActor is { IsValid: true } target)
        {
            var center = target.Position;
            startPosition = new SEED.Vector3(center.x, center.y + settings.SubmergedOffsetY, center.z);
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
                bob.x - travelDirection.x * settings.JumpTravelDistance,
                controller.WaterSurfaceY() + settings.SubmergedOffsetY,
                bob.z - travelDirection.z * settings.JumpTravelDistance);
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

    /// <summary>
    /// メインカメラの Transform と Camera を掴み、画角を使うなら元の値を預かる
    /// （見つからなければカメラは動かさない）。
    /// </summary>
    private void ResolveCamera()
    {
        var camera = SEED.GameObject.Find(MainCameraActorName);
        if (!camera.IsValid) { return; }
        if (camera.GetComponent<SEED.Transform>() is { } t && t.IsValid) { cameraTransform = t; }
        if (camera.GetComponent<SEED.Camera>() is { } c && c.IsValid) { cameraComponent = c; }

        desiredFieldOfView = ResolveDesiredFieldOfView();

        // 画角を預かる前に、この演出がカメラを握るかどうかで分かれる
        // （FollowNormal では ResolveDesiredFieldOfView が必ず「指定なし」を返す）

        // 画角を動かすときだけ元の値を預かる（戻す必要が無いなら預からない）
        if (desiredFieldOfView > CutsceneSettings.FieldOfViewUnspecified && cameraComponent is { } cam)
        {
            originalFieldOfView = cam.FieldOfView;
        }
    }

    /// <summary>
    /// 演出中に使う画角を決める【画角の指定をどこから読むかの唯一の判断】。
    ///
    /// カメラ目標アクタに Camera が付いていればその視野角を使い（会話カメラと同じ流儀）、
    /// 無ければデータの「演出:カメラ画角(度)」を使う。どちらも無ければ
    /// <see cref="CutsceneSettings.FieldOfViewUnspecified"/>（＝画角を触らない）。
    /// </summary>
    /// <returns>演出中に使う画角（度）。</returns>
    private float ResolveDesiredFieldOfView()
    {
        // 通常の追従のままにするモードでは画角にも触らない。
        // CameraMove.UpdateFov が毎フレーム画角を書いているので、ここで書くと
        // 1 フレームごとに奪い合って画角が震える（どちらの値にも落ち着かない）。
        if (!settings.UsesCameraControl) { return CutsceneSettings.FieldOfViewUnspecified; }

        // 目標アクタの Camera が読めて、かつ実在する画角（正の度数）ならそれを使う。
        // 0 のときは「読めなかった」と同じ扱いにしてデータの指定へ落とす。
        if (settings.CameraMode == CutsceneCameraMode.TargetActor
            && settings.HasCameraTarget
            && settings.CameraTarget.GameObject.GetComponent<SEED.Camera>() is { } targetCam
            && targetCam.IsValid
            && targetCam.FieldOfView > CutsceneSettings.FieldOfViewUnspecified)
        {
            return targetCam.FieldOfView;
        }

        return settings.FieldOfViewDegrees;
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
        float horizontal = settings.JumpTravelDistance * progress;
        float height = ParabolaScale * progress * (1f - progress) * settings.JumpHeightAmplitude;

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
        // モデルの前方向がズレている .actor 向けに、データの補正角をそのまま足す。
        float yaw = TravelYawDegrees() + settings.YawOffsetDegrees;

        // 上昇中は頭を上げ、下降中は頭を下げる（頂点で 0 度）。
        // エンジン規約では pitch の正が「下を向く」ので、進捗 0 で −半分、1 で +半分になる。
        float pitch = (progress - ParabolaApex) * settings.PitchSweepDegrees;

        t.Rotation = new SEED.Vector3(pitch, yaw, 0f);
    }

    /// <summary>
    /// カメラを怪獣へ寄せて注視させる。
    ///
    /// 目標の姿勢（位置と向き）を毎フレーム求め、指数補間でそこへ寄る。
    /// 会話のカメラ演出と同じ流儀なので、動きの手触りが他の演出と揃う。
    /// </summary>
    /// <param name="kaijuPosition">このフレームの怪獣の位置。</param>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    private void ApplyCamera(SEED.Vector3 kaijuPosition, float unscaledDelta)
    {
        // 通常の追従のままにするモードでは、位置・回転・画角のどれにも触らない
        // （CameraMove が握ったままなので、ここで書くと毎フレーム奪い合いになる）
        if (!settings.UsesCameraControl) { return; }

        if (cameraTransform is not { } cam || !cam.IsValid) { return; }

        float blend = ExponentialBlend(settings.CameraLerpRate, unscaledDelta);

        ApplyFieldOfView(blend);

        // カメラ目標アクタ指定なら構図の計算はせず、そのアクタの姿勢へ寄る。
        // 目標が未設定・無効なら回り込みの構図へ落とす（カメラが固まるより絵になる）。
        if (settings.CameraMode == CutsceneCameraMode.TargetActor && settings.HasCameraTarget)
        {
            ApplyCameraTargetPose(cam, blend);
            return;
        }

        ApplyCameraOrbitPose(cam, kaijuPosition, blend);
    }

    /// <summary>
    /// カメラ目標アクタの姿勢へ寄せる（会話カメラと同じ流儀）。
    ///
    /// 回転は成分ごとに最短回りで補間する（350 度 → 10 度 が逆回りしないように）。
    /// </summary>
    /// <param name="cam">カメラの Transform。</param>
    /// <param name="blend">このフレームの補間係数（0〜1）。</param>
    private void ApplyCameraTargetPose(SEED.Transform cam, float blend)
    {
        var target = settings.CameraTarget;
        if (!target.IsValid) { return; }

        cam.Position = SEED.Vector3.Lerp(cam.Position, target.Position, blend);
        cam.Rotation = AngleMath.LerpEuler(cam.Rotation, target.Rotation, blend);
    }

    /// <summary>
    /// 怪獣のまわりを回り込んだ位置へ寄せ、怪獣を注視する
    /// 【距離・高さ・方位角から構図を作る唯一の計算点】。
    /// </summary>
    /// <param name="cam">カメラの Transform。</param>
    /// <param name="kaijuPosition">このフレームの怪獣の位置。</param>
    /// <param name="blend">このフレームの補間係数（0〜1）。</param>
    private void ApplyCameraOrbitPose(SEED.Transform cam, SEED.Vector3 kaijuPosition, float blend)
    {
        // 進行方向から方位角だけ水平に回した向き（0 度なら進行方向側＝怪獣の正面）
        float offsetYaw = (TravelYawDegrees() + settings.CameraAzimuthDegrees) * SEED.Mathf.Deg2Rad;
        var   offsetDir = new SEED.Vector3(SEED.Mathf.Sin(offsetYaw), 0f, SEED.Mathf.Cos(offsetYaw));

        // カメラ位置は怪獣の中心を基準に「方位角の向きへ距離ぶん・高さぶん」ずらした点
        var desired = new SEED.Vector3(
            kaijuPosition.x + offsetDir.x * settings.CameraDistance,
            kaijuPosition.y + settings.CameraHeight,
            kaijuPosition.z + offsetDir.z * settings.CameraDistance);

        cam.Position = SEED.Vector3.Lerp(cam.Position, desired, blend);

        // 注視点は怪獣の中心から指定ぶん上（0 なら中心そのもの）
        var lookAt = new SEED.Vector3(
            kaijuPosition.x,
            kaijuPosition.y + settings.CameraLookAtHeight,
            kaijuPosition.z);

        // 注視: カメラから注視点へのベクトルからヨー・ピッチを作る
        var toTarget = lookAt - cam.Position;
        float distance = toTarget.Magnitude;
        if (distance <= DivideEpsilon) { return; }

        float yaw = SEED.Mathf.Atan2(toTarget.x, toTarget.z) * SEED.Mathf.Rad2Deg;
        float pitch = -SEED.Mathf.Asin(SEED.Mathf.Clamped(toTarget.y / distance, -1f, 1f)) * SEED.Mathf.Rad2Deg;

        cam.Rotation = new SEED.Vector3(pitch, yaw, 0f);
    }

    /// <summary>
    /// 演出中の画角を目標値へ寄せる（指定が無ければ何もしない）。
    /// 位置・回転と同じ補間係数を使うので、構図と画角がちぐはぐに動かない。
    /// </summary>
    /// <param name="blend">このフレームの補間係数（0〜1）。</param>
    private void ApplyFieldOfView(float blend)
    {
        if (desiredFieldOfView <= CutsceneSettings.FieldOfViewUnspecified) { return; }
        if (cameraComponent is not { } cam || !cam.IsValid) { return; }

        cam.FieldOfView = SEED.Mathf.Lerp(cam.FieldOfView, desiredFieldOfView, blend);
    }

    /// <summary>
    /// 預かっていた画角をカメラへ戻す【画角を返す唯一の出口】。
    /// 預かっていなければ（画角を触っていなければ）何もしない。
    /// </summary>
    private void RestoreFieldOfView()
    {
        if (originalFieldOfView <= CutsceneSettings.FieldOfViewUnspecified) { return; }

        if (cameraComponent is { } cam && cam.IsValid) { cam.FieldOfView = originalFieldOfView; }
        originalFieldOfView = CutsceneSettings.FieldOfViewUnspecified;
    }

    // ─── 内部処理: 小道具 ───────────────────────────────────

    /// <summary>
    /// 怪獣が進む向きのヨー角（度）を返す【進行方向 → 角度の唯一の変換】。
    /// エンジンの前方向は +Z なので Atan2(x, z) で求める。
    /// </summary>
    /// <returns>進行方向のヨー角（度）。</returns>
    private float TravelYawDegrees()
        => SEED.Mathf.Atan2(travelDirection.x, travelDirection.z) * SEED.Mathf.Rad2Deg;

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
