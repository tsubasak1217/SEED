using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 追従カメラ（ターゲットトランスフォーム方式）。
///
/// <b>目標トランスフォーム</b>の位置・回転を<b>そのまま目標</b>として指数補間で追う。
/// 構図（どこから・どっちを向くか）は完全にシーン側の配置で決める:
/// 経路上を追従する CameraTargetParent（PlayerMove が位置＋経路接線の向きへ毎フレーム更新）の
/// 子に目標を置けば、子のローカル位置＝オフセット、子のローカル回転＝視線の向きになる。
/// 親は逆走しても振り返らない（接線は入力の正負に依存しない）ので、目標も回り込まない。
///
/// プレイヤー移動（Update フェーズ）確定後の LateUpdate で処理する
/// （フェーズ単位実行なのでスクリプトの実行順に依存しない）。
/// </summary>
public class CameraMove : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>半回転（度）。最短回りの差分を求めるのに使う。</summary>
    private const float HalfTurnDegrees = 180f;

    /// <summary>1 回転（度）。角度を周期に畳み込むのに使う。</summary>
    private const float FullTurnDegrees = 360f;

    /// <summary>度→ラジアン変換係数。</summary>
    private const float DegToRad = 3.14159265f / HalfTurnDegrees;

    // ─── 参照 ─────────────────────────────────────────────────

    /// <summary>
    /// カメラが目指す位置・回転を持つトランスフォーム。
    /// 経路上を追従する CameraTargetParent の<b>子</b>を割り当てる想定。未設定なら何もしない。
    /// </summary>
    [Header("参照"), SerializeField(Label = "目標トランスフォーム")]
    private SEED.Transform? target = null;

    /// <summary>
    /// プレイヤーの Transform（<b>移動ロールの算出にのみ</b>使う）。
    /// 未設定なら移動ロールは掛からない（位置・回転の追従はそのまま動く）。
    /// </summary>
    [SerializeField(Label = "プレイヤー（移動ロール用）")]
    private SEED.Transform? player = null;

    /// <summary>
    /// プレイヤーの移動スクリプト（<b>状態の参照にのみ</b>使う）。
    /// 釣り姿勢のあいだだけ目標を <see cref="fishingTarget"/> へ切り替えるために参照する。
    /// 未設定なら常に <see cref="target"/> を追う（従来どおりの動作）。
    /// </summary>
    [SerializeField(Label = "プレイヤー（状態参照）")]
    private PlayerMove? playerMove = null;

    /// <summary>
    /// 釣り姿勢中に目指すトランスフォーム（プレイヤーの子に置く想定）。
    /// 未設定・無効なら釣り姿勢でも <see cref="target"/> を追い続ける。
    /// </summary>
    [SerializeField(Label = "釣り時の目標トランスフォーム")]
    private SEED.Transform? fishingTarget = null;

    /// <summary>
    /// 釣りの進行スクリプト（<b>状態の参照にのみ</b>使う）。
    /// ウキが飛んでいる／浮いている／巻いているあいだだけ
    /// 目標を <see cref="castTarget"/> へ切り替えるために参照する。
    /// 未設定ならキャスト演出は効かず、従来どおり釣り姿勢の判定だけで動く。
    /// </summary>
    [SerializeField(Label = "釣り（FishingController）")]
    private FishingController? fishing = null;

    /// <summary>
    /// キャスト中の目標トランスフォーム（ウキの子に置く想定）。
    /// ウキが動けば子も追従するので、カメラは自然にウキを画面に収め続ける。
    /// 未設定・無効ならキャスト中も <see cref="fishingTarget"/>／<see cref="target"/> を追う。
    /// </summary>
    [SerializeField(Label = "キャスト中の目標トランスフォーム")]
    private SEED.Transform? castTarget = null;

    /// <summary>
    /// 釣り上げ演出の「寄り」フェーズ（<see cref="CatchPresenter.CatchPhase.ApproachCamera"/>）の
    /// 目標トランスフォーム（トップレベルの空アクタ「CatchCameraTarget」を割り当てる想定）。
    /// 位置・向きは <see cref="CatchPresenter"/> が毎フレーム「魚を見る姿勢」へ置き直す。
    /// 未設定なら寄りの構図は効かない（従来の目標を追い続ける）。
    /// </summary>
    [SerializeField(Label = "釣り上げ寄りの目標トランスフォーム")]
    private SEED.Transform? catchTarget = null;

    /// <summary>
    /// 釣果表示の目標トランスフォーム（プレイヤーの子アクタ「ResultCameraTarget」を割り当てる想定）。
    /// <see cref="CatchPresenter.CatchPhase.WhiteOut"/> 以降のあいだ使う。
    /// 切り替えは真っ白の裏で <see cref="RequestSnap"/> により<b>カット</b>されるので、
    /// 構図が飛ぶところは見えない。未設定なら釣果の構図は切り替わらない。
    /// </summary>
    [SerializeField(Label = "釣果表示の目標トランスフォーム")]
    private SEED.Transform? resultTarget = null;

    // ─── 追従パラメータ ───────────────────────────────────────

    /// <summary>
    /// 位置の追従の速さ（1/秒）。大きいほど目標位置に張り付き、小さいほど遅れて付いてくる。
    /// 0 で位置を追わなくなる。
    /// </summary>
    [Header("追従の速さ"), SerializeField(Label = "位置の追従率")]
    private float positionLerpRate = 6.0f;

    /// <summary>
    /// 回転の追従の速さ（1/秒）。位置と別に調整できる。0 で回転を追わなくなる。
    /// </summary>
    [SerializeField(Label = "回転の追従率")]
    private float rotationLerpRate = 8.0f;

    /// <summary>true なら最初のフレームだけ補間せず目標の位置・回転へ瞬間移動する。</summary>
    [SerializeField(Label = "開始時に目標へスナップ")]
    private bool snapOnStart = true;

    // ─── ロール（移動に応じた傾き）───────────────────────────

    /// <summary>
    /// 移動に応じたロール（Z軸回転＝バンク）の強さ。
    /// プレイヤーの横方向速度 1 m/s あたりの傾き（度）。0 で無効。
    /// </summary>
    [Header("移動ロール"), SerializeField(Label = "傾きの強さ(度/(m/s))")]
    private float rollStrength = 2.5f;

    /// <summary>ロールの上限（度）。速く動いてもこれ以上は傾かない。</summary>
    [SerializeField(Label = "最大傾き(度)")]
    private float maxRollDegrees = 10f;

    // ─── 視野角(FOV) ───────────────────────────────────────────

    /// <summary>通常時の視野角（度）。釣り姿勢でないときの目標 FOV。</summary>
    [Header("視野角(FOV)"), SerializeField(Label = "通常時のFOV(度)")]
    private float normalFov = 60f;

    /// <summary>釣り時の視野角（度）。プレイヤーが釣り姿勢のあいだの目標 FOV。</summary>
    [SerializeField(Label = "釣り時のFOV(度)")]
    private float fishingFov = 45f;

    /// <summary>FOV の追従の速さ（1/秒）。位置・回転と同じ指数補間を使う。0 で FOV を追わなくなる。</summary>
    [SerializeField(Label = "FOVの追従率")]
    private float fovLerpRate = 5f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>まだ一度も追従していないか（<see cref="snapOnStart"/> の判定に使う）。</summary>
    private bool isFirstFollow = true;

    /// <summary>前フレームのプレイヤー位置。速度（移動デルタ/dt）を出すために保持。null=未観測。</summary>
    private SEED.Vector3? previousPlayerPosition = null;

    /// <summary>
    /// 次の追従で補間せず目標へ瞬間移動するか（<see cref="RequestSnap"/> が立てる）。
    /// 1 回のスナップで必ず落とすので、要求が残り続けることはない。
    /// </summary>
    private bool snapRequested = false;

    /// <summary>フレーム開始時に呼ばれる。入力取得や状態リセット向け。</summary>
    public override void BeginFrame(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update より前の更新。他スクリプトへ渡す事前計算向け。</summary>
    public override void EarlyUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>毎フレーム呼ばれる主更新処理。カメラは LateUpdate で追従する。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
    }

    /// <summary>固定タイムステップの更新。物理など時間刻みを一定にしたい処理向け。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update 後の更新。目標トランスフォームが確定した後に追従する。</summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
        if (SelectGoalTransform() is not { } t || !t.IsValid) { return; }

        // 目標 = 目標トランスフォームの位置・回転そのまま。
        // 構図の計算はシーンの親子配置に完全に委ねる（このスクリプトは補間とロールだけを担う）。
        var goalPos = t.Position;
        var goalRot = t.Rotation;

        // 移動ロール: プレイヤーの横方向速度（カメラの右方向成分）に比例した
        // 傾き（Z軸）を目標回転へ加算する。停止すれば 0 に戻り水平へ復帰する。
        if (player is { } p && p.IsValid)
        {
            goalRot = new SEED.Vector3(
                goalRot.x,
                goalRot.y,
                goalRot.z + ComputeMovementRoll(p.Position, goalRot.y, ctx.DeltaTime));
        }

        // 初回スナップ（開始時にカメラが遠くから飛んでくるのを防ぐ）
        if (isFirstFollow)
        {
            isFirstFollow = false;
            if (snapOnStart)
            {
                transform.Position = goalPos;
                transform.Rotation = goalRot;
                UpdateFov(ctx.DeltaTime, snap: true);
                return;
            }
        }

        // 明示的なカット要求（RequestSnap）: 補間せず目標へ飛ぶ。
        // 要求は必ずここで落とすので、次フレームからは通常の補間へ戻る。
        if (snapRequested)
        {
            snapRequested = false;
            transform.Position = goalPos;
            transform.Rotation = goalRot;
            UpdateFov(ctx.DeltaTime, snap: true);
            return;
        }

        // 位置を指数補間（フレームレート非依存）
        float pk = ExponentialBlend(positionLerpRate, ctx.DeltaTime);
        if (pk > 0f)
        {
            transform.Position += (goalPos - transform.Position) * pk;
        }

        // 回転を軸ごとの最短回りで指数補間（350°→10° のような巻き戻りでも逆回りしない）
        float rk = ExponentialBlend(rotationLerpRate, ctx.DeltaTime);
        if (rk > 0f)
        {
            var cur = transform.Rotation;
            transform.Rotation = new SEED.Vector3(
                cur.x + ShortestAngleDelta(cur.x, goalRot.x) * rk,
                cur.y + ShortestAngleDelta(cur.y, goalRot.y) * rk,
                cur.z + ShortestAngleDelta(cur.z, goalRot.z) * rk);
        }

        // 視野角(FOV): 釣り姿勢かどうかで目標を切り替え、位置・回転と同じ指数補間で追従する
        UpdateFov(ctx.DeltaTime, snap: false);
    }

    /// <summary>
    /// 次の追従を<b>補間せず</b>目標へ瞬間移動させる（カット）。
    ///
    /// 目標トランスフォームを切り替える瞬間に画面が隠れている（釣り上げ演出の
    /// ホワイトアウトなど）場面で呼ぶと、視点の飛びが見えないままカットできる。
    /// 呼んだ次の <see cref="LateUpdate"/> 1 回だけ効く。
    /// </summary>
    public void RequestSnap() => snapRequested = true;

    /// <summary>描画フェーズで呼ばれる。描画に関わる処理向け。</summary>
    public override void Render(ref NativeFrameContext ctx)
    {
    }

    /// <summary>フレーム終了時に呼ばれる。後片付けや状態確定向け。</summary>
    public override void EndFrame(ref NativeFrameContext ctx)
    {
    }

    // ─── 内部処理 ─────────────────────────────────────────────

    /// <summary>
    /// このフレームに追うべき目標トランスフォームを選ぶ。
    ///
    /// プレイヤーが釣り姿勢のあいだだけ <see cref="fishingTarget"/>、それ以外は
    /// <see cref="target"/>。補間処理は共通なので、切り替えても構図は滑らかに繋がる。
    ///
    /// 参照スクリプトは毎フレーム見に行く（フィールドへ写して保持しない）。
    /// スクリプトのホットリロードや対象の破棄で参照が入れ替わるため、
    /// 別フィールドへキャッシュすると古いインスタンスを掴み続けてしまう。
    /// </summary>
    /// <returns>追従先のトランスフォーム。決められなければ null。</returns>
    private SEED.Transform? SelectGoalTransform()
    {
        // 釣り上げ演出中はフェーズ専用の構図が最優先。
        // ApproachCamera = 水面の魚へ寄る / WhiteOut 以降 = プレイヤーを振り返って見る。
        if (SelectCatchGoal() is { } catchGoal) { return catchGoal; }

        // ウキが外に出ているあいだはウキ側の目標を最優先で追う（キャスト先が画面に入る）
        if (IsFloatOut() && castTarget is { } ct && ct.IsValid) { return ct; }

        if (IsPlayerFishing() && fishingTarget is { } ft && ft.IsValid) { return ft; }

        return target;
    }

    /// <summary>
    /// 釣り上げ演出中に追うべき目標トランスフォームを返す（演出中でなければ null）。
    ///
    /// フェーズは順序どおりに並んでいるので、判定は
    /// 「<see cref="CatchPresenter.CatchPhase.ApproachCamera"/> なら寄り、
    /// それ以外（None を除く）なら釣果」の 2 分岐で済む。
    /// 割り当てが無いフェーズでは null を返し、呼び出し側が従来の選択へ落ちる。
    /// </summary>
    private SEED.Transform? SelectCatchGoal()
    {
        if (fishing is not { } f) { return null; }
        if (f.State != FishingController.FishState.Catching) { return null; }

        var phase = f.CatchPhase;
        if (phase == CatchPresenter.CatchPhase.None) { return null; }

        if (phase == CatchPresenter.CatchPhase.ApproachCamera)
        {
            return catchTarget is { IsValid: true } ct ? ct : null;
        }

        return resultTarget is { IsValid: true } rt ? rt : null;
    }

    /// <summary>
    /// プレイヤーが釣り姿勢かどうかを返す（目標トランスフォームの選択・FOV 目標の両方で使う共通判定）。
    /// <see cref="playerMove"/> が未設定なら常に false（釣り演出は一切効かない）。
    /// </summary>
    private bool IsPlayerFishing()
        => playerMove is { } pm && pm.State == PlayerMove.PlayerState.FishingStance;

    /// <summary>
    /// ウキが外に出ている（飛翔中・浮遊中・巻き取り中）かを返す。
    ///
    /// 参照スクリプトは毎フレーム見に行く（ホットリロードで実インスタンスが差し替わるため、
    /// 別フィールドへキャッシュしない）。<see cref="fishing"/> 未設定なら常に false。
    /// </summary>
    private bool IsFloatOut()
        => fishing is { } f && f.State is FishingController.FishState.Casting
                                      or FishingController.FishState.Floating
                                      or FishingController.FishState.Reeling
                                      or FishingController.FishState.Nibbling
                                      or FishingController.FishState.HookWindow
                                      // わらしべ連鎖のアタリ中もウキは沖に出たまま（構図は釣り中のまま維持する）
                                      or FishingController.FishState.ChainNibbling
                                      or FishingController.FishState.ChainHookWindow
                                      or FishingController.FishState.Hooked;

    /// <summary>
    /// 移動に応じた目標ロール（度）を返す。
    ///
    /// プレイヤーの速度（前フレームからの移動/dt）の水平成分を、
    /// カメラのヨーから求めた<b>右方向</b>へ射影し、横方向速度 × 強さ を上限クランプ。
    /// 停止・初回・dt 異常時は 0。
    /// </summary>
    /// <param name="playerPos">今フレームのプレイヤー位置。</param>
    /// <param name="cameraYawDeg">目標姿勢のヨー（度）。右方向ベクトルの算出に使う。</param>
    /// <param name="deltaTime">経過秒。</param>
    private float ComputeMovementRoll(SEED.Vector3 playerPos, float cameraYawDeg, float deltaTime)
    {
        // 前フレーム位置を更新しつつ速度を求める（初回は 0 扱い）
        var prev = previousPlayerPosition;
        previousPlayerPosition = playerPos;
        if (rollStrength <= 0f || deltaTime <= 0f) { return 0f; }
        if (prev is not { } prevPos) { return 0f; }

        var delta = playerPos - prevPos;
        // カメラヨーの右方向（ワールド）: R_y(yaw) * (1,0,0)
        float yawRad = cameraYawDeg * DegToRad;
        var right = new SEED.Vector3(SEED.Mathf.Cos(yawRad), 0f, -SEED.Mathf.Sin(yawRad));

        // 横方向速度（m/s）＝ 水平デルタの右方向成分 / dt
        float lateralSpeed = (delta.x * right.x + delta.z * right.z) / deltaTime;

        // 符号は「右へ流れているとき右（正）へ傾く」向き（ユーザーがエディタ上で調整した符号を反映）。
        float roll = lateralSpeed * rollStrength;
        float limit = SEED.Mathf.Abs(maxRollDegrees);
        return SEED.Mathf.Clamped(roll, -limit, limit);
    }

    /// <summary>
    /// カメラの視野角(FOV)を目標へ更新する。
    ///
    /// 目標は <see cref="IsPlayerFishing"/> の結果に応じて <see cref="fishingFov"/> /
    /// <see cref="normalFov"/> を切り替える（<see cref="SelectGoalTransform"/> と同じ判定を共有し、
    /// 目標トランスフォームの切替と FOV の切替がズレないようにする）。
    /// Camera コンポーネントは毎フレーム取得する（ホットリロードや GameObject 差し替えに追従するため。
    /// 他の参照フィールドをフィールドへキャッシュしない方針と同じ）。
    /// Camera が未付与・無効なら何もしない（位置・回転の追従には影響しない）。
    /// </summary>
    /// <param name="deltaTime">経過秒。</param>
    /// <param name="snap">true なら補間せず目標へ瞬間的に合わせる（初回スナップ用）。</param>
    private void UpdateFov(float deltaTime, bool snap)
    {
        if (gameObject.GetComponent<SEED.Camera>() is not { } cam || !cam.IsValid) { return; }

        // 釣り姿勢中もキャスト中も同じ寄り（fishingFov）にする。
        // キャスト専用の FOV は今のところ必要が無く、切替が増えるほど画が落ち着かないため。
        float goalFov = (IsPlayerFishing() || IsFloatOut()) ? fishingFov : normalFov;

        if (snap)
        {
            cam.FieldOfView = goalFov;
            return;
        }

        // 位置・回転と同じ指数補間（フレームレート非依存）でブレンドする
        float k = ExponentialBlend(fovLerpRate, deltaTime);
        if (k > 0f)
        {
            cam.FieldOfView += (goalFov - cam.FieldOfView) * k;
        }
    }

    /// <summary>フレームレート非依存の指数補間係数 <c>1 - exp(-rate * dt)</c>（0〜1）。</summary>
    private static float ExponentialBlend(float rate, float deltaTime)
    {
        if (rate <= 0f || deltaTime <= 0f) { return 0f; }
        return SEED.Mathf.Clamped01(1f - SEED.Mathf.Exp(-rate * deltaTime));
    }

    /// <summary>角度 from→to の最短回りの差分（度、-180〜+180）。</summary>
    private static float ShortestAngleDelta(float from, float to)
        => SEED.Mathf.Repeat(to - from + HalfTurnDegrees, FullTurnDegrees) - HalfTurnDegrees;
}
