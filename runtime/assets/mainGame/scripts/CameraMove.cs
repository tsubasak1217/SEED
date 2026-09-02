using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 追従カメラ（ターゲットトランスフォーム方式）。
///
/// <b>目標トランスフォーム</b>（例: 経路上を追従する CameraTargetParent の子）の
/// ワールド位置へ位置を指数補間し、<b>常にプレイヤーを注視</b>する。
///
/// - カメラの「どこから見るか」はシーン上のオブジェクト配置（親子関係）で決める。
///   親（CameraTargetParent）は PlayerMove が経路上の現在位置＋経路接線の向きへ
///   毎フレーム更新する。親は逆走しても振り返らない（接線は入力の正負に依存しない）ので、
///   その子に置いた目標も回り込まない。オフセットの調整は子のローカル位置を動かすだけ。
/// - 注視はカメラの現在位置（補間後）からプレイヤーへ向けるので、
///   移動中も視線がプレイヤーから外れない。回転オフセットで構図を微調整できる。
/// - プレイヤー移動（Update フェーズ）確定後の LateUpdate で処理する
///   （フェーズ単位実行なのでスクリプトの実行順に依存しない）。
/// </summary>
public class CameraMove : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>半回転（度）。最短回りの差分を求めるのに使う。</summary>
    private const float HalfTurnDegrees = 180f;

    /// <summary>1 回転（度）。角度を周期に畳み込むのに使う。</summary>
    private const float FullTurnDegrees = 360f;

    /// <summary>ベクトルの「長さがほぼ 0」を判定する二乗長のしきい値。</summary>
    private const float SqrEpsilon = 1e-6f;

    /// <summary>度→ラジアン変換係数。</summary>
    private const float DegToRad = 3.14159265f / HalfTurnDegrees;

    // ─── 参照 ─────────────────────────────────────────────────

    /// <summary>
    /// カメラが目指す位置（ワールド）を持つトランスフォーム。
    /// 経路上を追従する CameraTargetParent の<b>子</b>を割り当てる想定
    /// （子のローカル位置＝カメラのオフセットとして編集できる）。未設定なら何もしない。
    /// </summary>
    [Header("参照"), SerializeField(Label = "目標トランスフォーム")]
    private SEED.Transform? target = null;

    /// <summary>注視する対象（プレイヤーの Transform）。未設定なら何もしない。</summary>
    [SerializeField(Label = "プレイヤー")]
    private SEED.Transform? player = null;

    /// <summary>
    /// 注視点の高さオフセット（メートル）。プレイヤーの原点が足元にある場合、
    /// 少し上（胸〜頭）を見た方が構図が安定する。
    /// </summary>
    [SerializeField(Label = "注視点の高さ")]
    private float lookAtHeight = 1.0f;

    // ─── 追従パラメータ ───────────────────────────────────────

    /// <summary>
    /// 位置の追従の速さ（1/秒）。大きいほど目標位置に張り付き、小さいほど遅れて付いてくる。
    /// 0 で位置を追わなくなる。
    /// </summary>
    [Header("追従の速さ"), SerializeField(Label = "位置の追従率")]
    private float positionLerpRate = 6.0f;

    /// <summary>
    /// 回転（注視）の追従の速さ（1/秒）。位置と別に調整できる。
    /// 0 で回転を追わなくなる。
    /// </summary>
    [SerializeField(Label = "回転の追従率")]
    private float rotationLerpRate = 8.0f;

    /// <summary>true なら最初のフレームだけ補間せず目標位置・注視姿勢へ瞬間移動する。</summary>
    [SerializeField(Label = "開始時に目標へスナップ")]
    private bool snapOnStart = true;

    // ─── 回転オフセット（目標姿勢への追加回転）───────────────

    /// <summary>注視で決まる目標姿勢に足すピッチ（X軸・度）。正で下を向く。</summary>
    [Header("回転オフセット"), SerializeField(Label = "ピッチ(度)")]
    private float rotationOffsetPitch = 0f;

    /// <summary>注視で決まる目標姿勢に足すヨー（Y軸・度）。正で右を向く。</summary>
    [SerializeField(Label = "ヨー(度)")]
    private float rotationOffsetYaw = 0f;

    /// <summary>注視で決まる目標姿勢に足すロール（Z軸・度）。移動ロールとは加算で併用される。</summary>
    [SerializeField(Label = "ロール(度)")]
    private float rotationOffsetRoll = 0f;

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

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>まだ一度も追従していないか（<see cref="snapOnStart"/> の判定に使う）。</summary>
    private bool isFirstFollow = true;

    /// <summary>前フレームのプレイヤー位置。速度（移動デルタ/dt）を出すために保持。null=未観測。</summary>
    private SEED.Vector3? previousPlayerPosition = null;

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

    /// <summary>Update 後の更新。プレイヤー・目標の位置が確定した後に追従する。</summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
        if (target is not { } t || !t.IsValid) { return; }
        if (player is not { } p || !p.IsValid) { return; }

        // 1) 目標位置 = 目標トランスフォームのワールド位置。
        //    「どこから見るか」の計算はシーンの親子配置（PlayerMove が動かす親＋子の
        //    ローカルオフセット）に委ねるので、ここでは位置をそのまま受け取るだけ。
        var goalPos = t.Position;

        // 2) 初回スナップ（開始時にカメラが遠くから飛んでくるのを防ぐ）。
        //    回転オフセットも含めた最終的な構図で開始する。
        if (isFirstFollow)
        {
            isFirstFollow = false;
            if (snapOnStart)
            {
                transform.Position = goalPos;
                var snapRot = LookAtEuler(transform.Position, p.Position);
                transform.Rotation = new SEED.Vector3(
                    snapRot.x + rotationOffsetPitch,
                    snapRot.y + rotationOffsetYaw,
                    snapRot.z + rotationOffsetRoll);
                return;
            }
        }

        // 3) 位置を指数補間（フレームレート非依存）
        float pk = ExponentialBlend(positionLerpRate, ctx.DeltaTime);
        if (pk > 0f)
        {
            transform.Position += (goalPos - transform.Position) * pk;
        }

        // 4) 注視: 「補間後のカメラ位置」からプレイヤー（+注視高さ）を見る姿勢を目標に、
        //    回転も指数補間する。移動中も視線がプレイヤーから外れない。
        var goalRot = LookAtEuler(transform.Position, p.Position);

        // 4.5) 移動ロール: プレイヤーの横方向速度（カメラの右方向成分）に比例して
        //      目標ロール（Z軸）を与える。停止すれば目標が 0 になり水平へ戻る。
        // 4.6) 回転オフセット: 注視で決まる姿勢へインスペクタ指定の追加回転を足す
        //      （構図の微調整用。ロールは移動ロールと加算で併用）。
        goalRot = new SEED.Vector3(
            goalRot.x + rotationOffsetPitch,
            goalRot.y + rotationOffsetYaw,
            ComputeMovementRoll(p.Position, goalRot.y, ctx.DeltaTime) + rotationOffsetRoll);

        float rk = ExponentialBlend(rotationLerpRate, ctx.DeltaTime);
        if (rk > 0f)
        {
            var cur = transform.Rotation;
            transform.Rotation = new SEED.Vector3(
                cur.x + ShortestAngleDelta(cur.x, goalRot.x) * rk,
                cur.y + ShortestAngleDelta(cur.y, goalRot.y) * rk,
                cur.z + ShortestAngleDelta(cur.z, goalRot.z) * rk);
        }
    }

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
    /// <paramref name="from"/> から プレイヤー位置+注視高さ を見るオイラー角（度）を返す。
    /// 方向が縮退している（真上/真下・同位置）場合は現在の回転を維持する。
    /// </summary>
    private SEED.Vector3 LookAtEuler(SEED.Vector3 from, SEED.Vector3 playerPos)
    {
        var to = playerPos + new SEED.Vector3(0f, lookAtHeight, 0f);
        var dir = to - from;
        if (dir.SqrMagnitude < SqrEpsilon) { return transform.Rotation; }
        return SEED.Quaternion.LookRotation(dir.Normalized).EulerAngles;
    }

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

        // 符号は「右へ流れているとき左（負）へ傾く」向き（ユーザー指定で反転）。
        float roll = -lateralSpeed * rollStrength;
        float limit = SEED.Mathf.Abs(maxRollDegrees);
        return SEED.Mathf.Clamped(roll, -limit, limit);
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
