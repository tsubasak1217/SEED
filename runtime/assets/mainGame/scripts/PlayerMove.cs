using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// プレイヤーの移動＋移動状態に応じたアニメーション切替。
///
/// 移動モードは <b>参照フィールド「経路」の有無で自動的に切り替わる</b>:
/// - <b>経路移動モード</b>（経路が設定されている）… ControlPoint 経路の上を前後入力で進む／戻る。
///   閉ループならぐるぐる回れる。上下入力（UpDown）は無効。
/// - <b>自由移動モード</b>（経路が未設定）… 従来どおりカメラ基準の平面移動＋上下移動。
///
/// どちらのモードでも、向きは「進行方向へ最短回りで緩やかに補間」する（急な向き変わりを防ぐ）。
/// 動いている間は走りアニメ、止まると待機アニメへクロスフェードで切り替える。
/// </summary>
public class PlayerMove : SEEDScript
{
    // ゲーム向けエンジン API（Mathf/Vector3/Time/Random/Debug/GameObject など）は
    // SEED 名前空間にあります。System と型名が衝突する（例: Random ↔ System.Random）ため、
    // エンジン側からは using を付けていません。「SEED.」で修飾して呼び出してください。
    // 詳細は docs/scripting_api.md を参照。

    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>ベクトルの「長さがほぼ 0」を判定する二乗長のしきい値。</summary>
    private const float SqrEpsilon = 1e-6f;

    /// <summary>入力値の「実質 0」を判定するしきい値（回転の目標更新に使う）。</summary>
    private const float InputEpsilon = 1e-3f;

    /// <summary>半回転（度）。最短回りの差分を求めるのに使う。</summary>
    private const float HalfTurnDegrees = 180f;

    /// <summary>1 回転（度）。角度を周期に畳み込むのに使う。</summary>
    private const float FullTurnDegrees = 360f;

    // ─── 自由移動パラメータ ───────────────────────────────────

    [Header("移動パラメータ"), SerializeField]
    private float moveSpeed = 1.0f;

    [SerializeField]
    private SEED.Transform? cameraTransform = null;

    // ─── 経路移動パラメータ ───────────────────────────────────

    /// <summary>
    /// 移動経路（ControlPoint コンポーネントを持つアクター）。
    /// <b>未設定なら従来のカメラ基準の自由移動へフォールバックする。</b>
    /// </summary>
    [Header("経路移動（未設定なら自由移動）"), SerializeField(Label = "経路（ControlPoint）")]
    private SEED.ControlPointPath? path = null;

    /// <summary>
    /// 経路上を進む速さ（経路時刻の進む倍率）。
    /// 制御点の time が既定（1 点 = 1 秒）なら「1.0 で 1 秒に 1 点ぶん進む」。
    /// </summary>
    [SerializeField(Label = "経路移動速度")]
    private float pathSpeed = 1.0f;

    /// <summary>
    /// 経路の高さ（Y）に追従するか。
    /// true  = 経路の Y をそのまま使う（レールに完全に乗る）。
    /// false = XZ だけ経路に従い、Y は重力・接地処理（Collider の apply_gravity）に任せる。
    /// </summary>
    [SerializeField(Label = "経路の高さに追従")]
    private bool followPathHeight = true;

    // ─── 回転補間 ─────────────────────────────────────────────

    /// <summary>
    /// 進行方向を向くときの回転補間の速さ（1/秒）。
    /// 大きいほど機敏に、小さいほどゆっくり向きが変わる。0 で向きが変わらなくなる。
    /// </summary>
    [Header("回転"), SerializeField(Label = "回転補間率")]
    private float turnLerpRate = 10.0f;

    // ─── アニメーション ───────────────────────────────────────

    [Header("アニメーション"), SerializeField(Label = "Animator（未指定なら自分のを使う）")]
    private SEED.Animator? animator = null;

    /// <summary>止まっているときに再生するクリップ名（Animator の clips に登録した名前）。</summary>
    [SerializeField(Label = "待機クリップ名")]
    private string idleClip = "Idle";

    /// <summary>動いているときに再生するクリップ名（Animator の clips に登録した名前）</summary>
    [SerializeField(Label = "走りクリップ名")]
    private string runningClip = "Running";

    /// <summary>Idle⇄Running 切替時のクロスフェード秒数（0 で即時切替）。</summary>
    [SerializeField(Label = "切替フェード(秒)")]
    private float fadeSeconds = 0.15f;

    /// <summary>この入力量（0〜1）未満は「止まっている」とみなす。スティックのドリフト対策。</summary>
    [SerializeField(Label = "移動判定しきい値")]
    private float moveThreshold = 0.05f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>現在再生を要求しているのが走りか（true=Running / false=Idle / null=未初期化）。</summary>
    private bool? isRunning = null;

    /// <summary>
    /// 経路上の現在時刻（秒）。閉ループなら 1 周ぶん、開経路なら経路の時刻範囲に
    /// 毎フレーム畳み込むので、入力を押しっぱなしにしても青天井にならない
    /// （＝ f32 の精度落ちや「戻すのに押した時間ぶんかかる」現象が起きない）。
    /// </summary>
    private float pathTime = 0f;

    /// <summary>経路上の時刻を初期化したか（最初の評価で経路の開始時刻へ合わせる）。</summary>
    private bool pathTimeInitialized = false;

    /// <summary>
    /// 向きの目標ヨー角（度）。null は「まだ一度も進行方向が決まっていない」。
    ///
    /// 入力が切れた後も最後の目標へ向かって回り続けさせるため、
    /// 「目標の更新（移動中のみ）」と「目標への補間（毎フレーム）」を分けて保持する。
    /// </summary>
    private float? targetYaw = null;

    /// <summary>フレーム開始時に呼ばれる。入力取得や状態リセット向け。</summary>
    public override void BeginFrame(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update より前の更新。他スクリプトへ渡す事前計算向け。</summary>
    public override void EarlyUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>毎フレーム呼ばれる主更新処理。移動→回転→アニメ状態の反映、の順で処理する。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 経路が設定・生存していればレール移動、そうでなければ従来の自由移動。
        // 点が 0 個の経路は評価すると原点へ飛ぶので、自由移動へ退避する。
        bool moving = (path is { } p && p.IsValid && p.PointCount > 0)
            ? UpdatePathMovement(ref ctx, p)
            : UpdateFreeMovement(ref ctx);

        // 目標ヨーへの補間は移動モードに依らず毎フレーム行う
        // （入力を離した後も回りきってから止まるので、向きが途中で固まらない）
        UpdateRotation(ctx.DeltaTime);

        // 移動状態が変わったフレームだけアニメを切り替える（毎フレーム Play すると先頭に戻り続けるため）
        UpdateAnimation(moving);
    }

    /// <summary>
    /// ControlPoint 経路に沿って移動する。このフレームに移動入力があったかを返す。
    ///
    /// 前後入力（InputMap "Move" の y）で経路上の時刻を進め／戻し、
    /// 位置は経路の評価結果（ワールド座標）で毎フレーム上書きする。
    /// 上下入力（"UpDown"）は経路移動では使わない。
    /// </summary>
    /// <param name="ctx">フレームコンテキスト（デルタタイム）。</param>
    /// <param name="p">評価対象の経路（呼び出し側で生存確認済み）。</param>
    private bool UpdatePathMovement(ref NativeFrameContext ctx, SEED.ControlPointPath p)
    {
        if (gameObject.GetComponent<SEED.InputMap>() is not { } im) { return false; }

        // 前後入力だけを使う（左右は経路に沿う移動では意味を持たない）
        float axis = im.GetVector2("Move").y;

        // 初回は経路の開始時刻へ合わせる（時刻の原点は制御点が決めるので 0 とは限らない）
        if (!pathTimeInitialized)
        {
            pathTime = p.StartTime;
            pathTimeInitialized = true;
        }

        // 経路上の時刻を進める。閉ループならエンジン側で周回し、開経路なら両端でクランプされる。
        pathTime += axis * pathSpeed * ctx.DeltaTime;

        // 保持している時刻を経路の範囲へ畳み込む（畳み込んでも評価結果は変わらない）。
        // 閉ループ: 1 周ぶんで巻き戻す ／ 開経路: 両端でクランプする。
        float duration = p.Duration;
        if (duration > 0f)
        {
            float startTime = p.StartTime;
            pathTime = p.Closed
                ? startTime + SEED.Mathf.Repeat(pathTime - startTime, duration)
                : SEED.Mathf.Clamped(pathTime, startTime, startTime + duration);
        }

        // 位置を経路上へ乗せる。高さは設定に応じて経路に従うか、重力へ委ねる。
        var onPath = p.SamplePosition(pathTime);
        transform.Position = followPathHeight
            ? onPath
            : new SEED.Vector3(onPath.x, transform.Position.y, onPath.z);

        // 進行方向（接線）から目標ヨーを更新する。逆走中は接線を反転する。
        if (SEED.Mathf.Abs(axis) > InputEpsilon)
        {
            var tangent = p.SampleTangent(pathTime);
            if (tangent.SqrMagnitude > SqrEpsilon)
            {
                UpdateTargetYaw(axis < 0f ? tangent * -1f : tangent);
            }
        }

        return SEED.Mathf.Abs(axis) > moveThreshold;
    }

    /// <summary>
    /// カメラ基準の自由移動を行い、このフレームに移動入力があったかを返す（経路未設定時のフォールバック）。
    /// </summary>
    private bool UpdateFreeMovement(ref NativeFrameContext ctx)
    {
        if (cameraTransform is not { } cam) { return false; }
        if (gameObject.GetComponent<SEED.InputMap>() is not { } im) { return false; }

        var input = im.GetVector2("Move");
        var upDown = im.GetAxis("UpDown");

        // Vector3 は不変構造体なので、Y を捨てた新しいベクトルを作る
        var fwd = new SEED.Vector3(cam.Forward.x, 0f, cam.Forward.z);
        var right = new SEED.Vector3(cam.Right.x, 0f, cam.Right.z);

        // 真下を向いたカメラ等の縮退ガード（SqrMagnitude はプロパティ）
        if (fwd.SqrMagnitude < SqrEpsilon) { return false; }
        fwd = fwd.Normalized;    // Normalized もプロパティ（()なし）
        right = right.Normalized;

        var move = right * input.x + fwd * input.y;
        if (move.SqrMagnitude > 1f)
        {
            move = move.Normalized;
        }

        transform.Position += move * moveSpeed * ctx.DeltaTime;
        transform.Position += new SEED.Vector3(0, upDown * moveSpeed * ctx.DeltaTime, 0);

        // 進行方向を向く（実際の回転は UpdateRotation が補間して行う）
        if (move.SqrMagnitude > SqrEpsilon)
        {
            UpdateTargetYaw(move);
        }

        // 平面移動か上下移動のどちらかがしきい値を超えていれば「動いている」
        float threshold = moveThreshold * moveThreshold;
        return move.SqrMagnitude > threshold || upDown * upDown > threshold;
    }

    /// <summary>
    /// 進行方向から目標ヨー角を更新する。XZ 平面へ潰すので、真上・真下の成分は向きに影響しない。
    /// </summary>
    /// <param name="direction">進行方向（ワールド。正規化されていなくてよい）。</param>
    private void UpdateTargetYaw(SEED.Vector3 direction)
    {
        var flat = new SEED.Vector3(direction.x, 0f, direction.z);
        if (flat.SqrMagnitude < SqrEpsilon) { return; }   // 真上・真下だけの移動では向きを変えない

        // SEED のローカル前方向は +Z。Atan2(x, z) がそのままヨー角（度）になる。
        targetYaw = SEED.Mathf.Atan2(flat.x, flat.z) * SEED.Mathf.Rad2Deg;
    }

    /// <summary>
    /// 現在のヨー角を目標ヨー角へ<b>最短回りで</b>緩やかに補間する。
    /// 359°→1° のような折り返しでも遠回りしない（差分を -180〜+180 に畳み込むため）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateRotation(float deltaTime)
    {
        if (targetYaw is not { } target) { return; }   // まだ一度も進行方向が決まっていない

        var rotation = transform.Rotation;             // YXZ オイラー角（度）
        float delta = SEED.Mathf.Repeat(target - rotation.y + HalfTurnDegrees, FullTurnDegrees) - HalfTurnDegrees;
        float yaw = rotation.y + delta * SEED.Mathf.Clamped01(turnLerpRate * deltaTime);

        transform.Rotation = new SEED.Vector3(rotation.x, yaw, rotation.z);
    }

    /// <summary>
    /// 移動状態に応じて Idle / Running をクロスフェードで切り替える。
    /// 状態が変化したフレームだけ Animator に指示する。
    /// </summary>
    private void UpdateAnimation(bool moving)
    {
        // 状態に変化がなければ何もしない
        if (isRunning == moving) { return; }

        // インスペクタで指定が無ければ自分自身の Animator を使う（モデルが子アクタの場合は指定が必要）
        if ((animator ?? gameObject.GetComponent<SEED.Animator>()) is not { } anim) { return; }   // Animator が無ければ移動だけ行う

        isRunning = moving;
        var clip = moving ? runningClip : idleClip;
        if (string.IsNullOrEmpty(clip)) { return; }

        // 既に同じクリップが再生中なら再指示しない（初回や外部からの変更にだけ効く保険）
        if (anim.IsPlaying && anim.CurrentClip == clip) { return; }

        anim.CrossFade(clip, fadeSeconds);
    }

    /// <summary>固定タイムステップの更新。物理など時間刻みを一定にしたい処理向け。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update 後の更新。追従カメラなど他更新の結果を使う処理向け。</summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>描画フェーズで呼ばれる。描画に関わる処理向け。</summary>
    public override void Render(ref NativeFrameContext ctx)
    {
    }

    /// <summary>フレーム終了時に呼ばれる。後片付けや状態確定向け。</summary>
    public override void EndFrame(ref NativeFrameContext ctx)
    {
    }
}
