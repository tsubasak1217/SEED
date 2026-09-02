using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 追従カメラ。<b>プレイヤーのローカル座標系で指定したオフセット位置</b>（既定: 右斜め後ろ）に
/// 位置取り、<b>常にプレイヤーを注視</b>する。位置・回転とも指数補間で滑らかに追従する。
///
/// - オフセットはプレイヤーの向き（ヨー）に追従して回るため、
///   どちら回りに進んでいても「右斜め後ろから見る」関係が保たれる。
///   プレイヤーが反転したときはオフセット位置が反対側へ回り込み、補間が滑らかに繋ぐ。
/// - 注視はカメラの現在位置（補間後）からプレイヤーへ向けるので、
///   回り込み中も視線がプレイヤーから外れない。
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

    // ─── 参照・オフセット ─────────────────────────────────────

    /// <summary>追従・注視する対象（プレイヤーの Transform）。未設定なら何もしない。</summary>
    [Header("追従対象"), SerializeField(Label = "プレイヤー")]
    private SEED.Transform? player = null;

    /// <summary>
    /// カメラの位置オフセット（<b>プレイヤーのローカル座標系</b>・メートル）。
    /// 右+ / 左-。既定はやや右。
    /// </summary>
    [Header("位置オフセット（プレイヤー基準）"), SerializeField(Label = "右へ")]
    private float offsetRight = 2.0f;

    /// <summary>上+ / 下-。見下ろし気味にするなら正の値。</summary>
    [SerializeField(Label = "上へ")]
    private float offsetUp = 3.0f;

    /// <summary>後ろ+ / 前-（プレイヤーの背中側が正）。</summary>
    [SerializeField(Label = "後ろへ")]
    private float offsetBack = 5.0f;

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

    /// <summary>
    /// 視点ヨー（オフセットの回り込み）の追従の速さ（1/秒）。
    /// 移動方向は毎フレームの位置デルタから求めるためノイズを含む。
    /// ここで平滑してからオフセットを回すことで、カメラのかくつきを防ぐ。
    /// 大きいほどカーブへの回り込みが機敏になり、小さいほど滑らかで遅れる。
    /// </summary>
    [SerializeField(Label = "視点回り込みの速さ")]
    private float yawLerpRate = 5.0f;

    /// <summary>true なら最初のフレームだけ補間せず目標位置・注視姿勢へ瞬間移動する。</summary>
    [SerializeField(Label = "開始時に目標へスナップ")]
    private bool snapOnStart = true;

    // ─── ロール（移動に応じた傾き）───────────────────────────

    /// <summary>
    /// 移動に応じたロール（Z軸回転＝バンク）の強さ。
    /// プレイヤーの横方向速度 1 m/s あたりの傾き（度）。0 で無効。
    /// カメラから見て右へ動いているときに右へ傾く（画面が進行方向へ倒れ込む）。
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

    /// <summary>
    /// オフセットの基準に使う<b>安定化ヨー</b>（度）。null=未初期化。
    ///
    /// プレイヤーの向きをそのまま使うと、逆走で 180 度反転した瞬間にカメラが
    /// 反対側へ回り込んでしまう。そこで「プレイヤーのヨーと、その +180 度のうち、
    /// 現在の安定化ヨーに近い方」を採用して<b>反転を無視</b>する。
    /// これにより、どちら向きに走っても「島を左・海を右」の視点が維持され、
    /// 逆走時はプレイヤーがカメラの方を向いて走ってくる構図になる。
    /// （90 度以内の通常の曲がりには追従する）
    /// </summary>
    private float? stableYawDeg = null;

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

    /// <summary>Update 後の更新。プレイヤーの位置・向きが確定した後に追従する。</summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
        if (player is not { } p || !p.IsValid) { return; }

        // 1) 目標位置 = プレイヤー位置 + 安定化ヨーで回したローカルオフセット。
        //    ピッチ/ロールは無視してヨーだけで回す（プレイヤーが坂で傾いても
        //    カメラの高さ関係が崩れないようにするため）。
        //    ヨーの基準は「プレイヤーの向き」ではなく「移動方向」から取る。
        //    向きは回転補間で滑らかに 180 度回るため反転を検出できないが、
        //    移動方向は逆走の瞬間に 180 度入れ替わるので、mod180 の安定化が正しく効く。
        float stabilized = StabilizeYaw(p.Position, p.Rotation.y, ctx.DeltaTime);
        float yawRad = stabilized * DegToRad;
        float sin = SEED.Mathf.Sin(yawRad);
        float cos = SEED.Mathf.Cos(yawRad);
        // ローカル(右=+X, 上=+Y, 後ろ=-Z の -1 倍) をヨー回転でワールドへ。
        // ワールドオフセット = R_y(yaw) * (offsetRight, offsetUp, -offsetBack)
        float localX = offsetRight;
        float localZ = -offsetBack;
        var worldOffset = new SEED.Vector3(
            cos * localX + sin * localZ,
            offsetUp,
            -sin * localX + cos * localZ);
        var goalPos = p.Position + worldOffset;

        // 2) 初回スナップ（開始時にカメラが遠くから飛んでくるのを防ぐ）
        if (isFirstFollow)
        {
            isFirstFollow = false;
            if (snapOnStart)
            {
                transform.Position = goalPos;
                transform.Rotation = LookAtEuler(transform.Position, p.Position);
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
        //    回転も指数補間する。回り込み中も視線がプレイヤーから外れない。
        var goalRot = LookAtEuler(transform.Position, p.Position);

        // 4.5) 移動ロール: プレイヤーの横方向速度（カメラの右方向成分）に比例して
        //      目標ロール（Z軸）を与える。画面が進行方向へ自然に倒れ込み、
        //      停止すれば目標が 0 になるので既存の補間で水平へ戻る。
        goalRot = new SEED.Vector3(goalRot.x, goalRot.y, ComputeMovementRoll(p.Position, goalRot.y, ctx.DeltaTime));
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
    /// 右へ流れているときに正（右へ傾く）。停止・初回・dt 異常時は 0。
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

    /// <summary>
    /// 移動方向のヨーを採用するのに必要な、1 フレームの最小水平移動量（m）の二乗。
    /// 停止中・微動（物理の微小揺れなど）で方向がでたらめに振れるのを弾く。
    /// 1mm/フレーム ≒ 60fps で 6cm/s 未満の移動は「止まっている」とみなす。
    /// </summary>
    private const float MinMoveSqForYaw = 0.001f * 0.001f;

    /// <summary>
    /// <b>移動方向</b>から 180 度反転を無視した<b>安定化ヨー</b>を求めて内部状態を更新する。
    ///
    /// - 移動方向のヨーと「そのヨー+180 度」のうち、現在の安定化ヨーに近い方を採用
    ///   （＝逆走の瞬間の 180 度反転を無視。カーブに伴う緩やかな変化には追従）
    /// - 基準にプレイヤーの<b>向き</b>を使わないのは、向きは回転補間で連続的に
    ///   180 度回るため「小さな変化の連続」となり、反転を区別できないから
    /// - 停止中（移動がほぼゼロ）は現状維持。初回だけプレイヤーの向きで初期化する
    /// - 生の移動方向はフレームごとの位置デルタ由来でノイズを含むため、
    ///   そのまま使わず<b>指数平滑</b>（<see cref="yawLerpRate"/>）してから返す。
    ///   オフセット腕の長さでヨーの揺れが増幅される「かくつき」を防ぐ。
    /// </summary>
    /// <param name="playerPos">今フレームのプレイヤー位置（移動方向の算出用）。</param>
    /// <param name="initialYawDeg">初回初期化に使うヨー（プレイヤーの向き）。</param>
    /// <param name="deltaTime">経過秒（平滑係数の算出用）。</param>
    private float StabilizeYaw(SEED.Vector3 playerPos, float initialYawDeg, float deltaTime)
    {
        // 初回はまだ移動方向が無いので、プレイヤーの向きで初期化する
        if (stableYawDeg is not { } stable)
        {
            stableYawDeg = initialYawDeg;
            return initialYawDeg;
        }

        // 前フレーム位置が無い/ほぼ動いていないなら現状維持
        // （previousPlayerPosition はロール計算と共有。更新はロール側で毎フレーム行われる）
        if (previousPlayerPosition is not { } prevPos) { return stable; }
        var delta = playerPos - prevPos;
        float sqXZ = delta.x * delta.x + delta.z * delta.z;
        if (sqXZ < MinMoveSqForYaw) { return stable; }

        // 移動方向のヨー（エンジン規約: yaw = atan2(x, z)、前方 +Z）
        float moveYaw = SEED.Mathf.Atan2(delta.x, delta.z) / DegToRad;

        // 移動ヨーとその反対向きのうち、現在の安定化ヨーへの回転量が小さい方を選ぶ
        float dRaw = ShortestAngleDelta(stable, moveYaw);
        float dFlip = ShortestAngleDelta(stable, moveYaw + HalfTurnDegrees);
        float shortestDelta = SEED.Mathf.Abs(dRaw) <= SEED.Mathf.Abs(dFlip) ? dRaw : dFlip;

        // 目標へ一気に飛ばず指数平滑で寄せる（フレームレート非依存）。
        // 位置デルタ由来のノイズはここで減衰し、カーブの緩やかな変化だけが通る。
        float smoothed = stable + shortestDelta * ExponentialBlend(yawLerpRate, deltaTime);

        stableYawDeg = smoothed;
        return smoothed;
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
