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

    /// <summary>入力マッピングの符号「そのまま」。前後入力の +y が経路の時刻を進める向き。</summary>
    private const float MappingForward = 1f;

    /// <summary>入力マッピングの符号「反転」。前後入力の +y が経路の時刻を戻す向き。</summary>
    private const float MappingReversed = -1f;

    /// <summary>
    /// 接線の符号ガードが働く、1 フレームの経路上の移動距離のしきい値（メートル）。
    ///
    /// 経路の進行方向（接線）が 1 フレームで反転したとき、
    /// <b>ほとんど動いていないのに反転した</b>なら、それは経路の形ではなく
    /// 数値微分のブレなので採用しない。逆に、この距離以上進んでいるなら
    /// ヘアピン（本当に折り返す経路）を通過した可能性があるので反転を受け入れる。
    /// 60fps で毎秒 0.6m 相当。歩行速度（数 m/s）ならガードに掛からない。
    /// </summary>
    private const float TangentFlipGuardDistance = 0.01f;

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
    //
    // カメラのアンカー切替は <b>CameraMove 側で完結する</b>。
    // CameraMove はプレイヤーの Transform の位置変化を観測して
    // 「移動方向の後方にあるアンカー」を自分で選ぶので、
    // このスクリプトはカメラのことを何も知らなくてよい。

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

    /// <summary>
    /// 前後入力の符号を経路方向へ写す<b>マッピング符号</b>（+1 = そのまま / -1 = 反転）。
    ///
    /// 進行方向が反転するとカメラが反対側へ回り込み、画面上の「前」が入れ替わるので、
    /// 入力の意味も入れ替える必要がある。詳しくは <see cref="UpdateInputLatch"/>。
    /// </summary>
    private float inputMapping = MappingForward;

    /// <summary>
    /// 前フレームに前後入力が押されていたか（押下 → 解放のエッジ検出に使う）。
    /// </summary>
    private bool wasInputHeld = false;

    /// <summary>
    /// 直近に<b>実際に走った</b>経路上の向き（+1 = 時刻が進む / -1 = 時刻が戻る）。
    /// 入力を離しても保持するので、止まった瞬間にカメラが反対側へ戻らない。
    /// null は「まだ一度も動いていない」。
    /// </summary>
    private float? travelSign = null;

    /// <summary>
    /// 前フレームの経路の接線（進行方向）。接線の符号ガードに使う。
    /// null は「まだ有効な接線を得ていない」。
    /// </summary>
    private SEED.Vector3? previousTangent = null;

    /// <summary>
    /// <see cref="previousTangent"/> を採用してからの経路上の移動距離（メートル）。
    /// 接線の符号ガードが「本物の折り返し」と「微分のブレ」を見分けるのに使う。
    /// </summary>
    private float distanceSinceTangent = 0f;

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
        bool onPath = path is { } p && p.IsValid && p.PointCount > 0;
        bool moving = onPath
            ? UpdatePathMovement(ref ctx, path!.Value)
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
        float rawAxis = im.GetVector2("Move").y;

        // 入力ラッチを更新し、「経路に対して実際に効かせる入力」へ写す。
        // ここより後ろは effectiveAxis だけを見る（生の入力は使わない）。
        UpdateInputLatch(rawAxis);
        float effectiveAxis = rawAxis * inputMapping;

        // 初回は経路の開始時刻へ合わせる（時刻の原点は制御点が決めるので 0 とは限らない）
        if (!pathTimeInitialized)
        {
            pathTime = p.StartTime;
            pathTimeInitialized = true;
        }

        // 実際に走っている向きを覚えておく（入力ラッチの更新に使う）。
        // 入力が無いフレームでは更新しないので、止まっても「最後に走った向き＝前」を保てる。
        if (SEED.Mathf.Abs(effectiveAxis) > InputEpsilon)
        {
            travelSign = SEED.Mathf.Sign(effectiveAxis);
        }

        // 経路上の時刻を進める。閉ループならエンジン側で周回し、開経路なら両端でクランプされる。
        var previousPosition = transform.Position;
        pathTime += effectiveAxis * pathSpeed * ctx.DeltaTime;

        // 保持している時刻を経路の範囲へ畳み込む（畳み込んでも評価結果は変わらない）。
        // 閉ループ: 1 周ぶんで巻き戻す ／ 開経路: 両端でクランプする。
        //
        // ※ Duration は閉ループでは制御点の配置に依存する（閉じる区間の所要時刻が
        //   距離比例で決まるため。docs/control_points.md「閉じる区間の時刻の定義」）。
        //   定数 1.0 を前提に自前計算せず、毎フレーム問い合わせること。
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
        if (SEED.Mathf.Abs(effectiveAxis) > InputEpsilon)
        {
            var tangent = GuardedTangent(p.SampleTangent(pathTime), previousPosition, transform.Position);
            if (tangent is { } dir && dir.SqrMagnitude > SqrEpsilon)
            {
                UpdateTargetYaw(effectiveAxis < 0f ? dir * -1f : dir);
            }
        }

        return SEED.Mathf.Abs(effectiveAxis) > moveThreshold;
    }

    /// <summary>
    /// 前後入力のマッピング符号（<see cref="inputMapping"/>）を更新する。
    ///
    /// <b>解きたい問題</b>: プレイヤーが逆向きに走り出すとカメラが反対側へ回り込むので、
    /// 画面上の「前」が入れ替わる。入力の意味を入れ替えないと、
    /// 「前へ進もうとして下を入れる」という不自然な操作になる。
    ///
    /// <b>ただし切り替えていいタイミングは限られる</b>: 反転のきっかけになった入力を
    /// 押している最中に意味を入れ替えると、押しっぱなしのまま挙動が前後に振れて
    /// その場で往復してしまう。そこで<b>入力を離すまでマッピングを凍結する</b>。
    ///
    /// <b>状態機械</b>（状態は「マッピング符号」と「押されているか」の 2 つだけ）:
    /// <code>
    ///   押されている間 ────────── マッピングは凍結（何が起きても変えない）
    ///   押下 → 解放のエッジ ───── マッピング := 直近に実際に走った向き（travelSign）
    ///   解放されている間 ──────── 何もしない
    /// </code>
    ///
    /// <b>「解放時に travelSign を採る」で正しくなる理由</b>:
    /// カメラ（CameraMove）は<b>実際の移動方向の後方にあるアンカー</b>を選ぶので、
    /// カメラが背中側に付く向き＝直近に実際に走った向き＝travelSign。
    /// つまり画面上の「前」は経路の travelSign 方向。よってマッピングを travelSign にすれば、
    /// 次に上入力（+1）を押したとき effectiveAxis = travelSign となり、
    /// <b>画面の前へ進む</b>。
    ///
    /// <b>例</b>（マッピング +1 で正方向に走行中）:
    /// 下入力（-1）→ effective = -1 で逆走開始・カメラが反対側へ →
    /// 押している間はずっと effective = -1 のまま（＝画面の前へ進み続ける）→
    /// 離した瞬間にマッピング := -1 → 次は上入力（+1）で effective = -1、
    /// つまり同じ向きに走り続ける。
    /// </summary>
    /// <param name="rawAxis">InputMap から取った生の前後入力。</param>
    private void UpdateInputLatch(float rawAxis)
    {
        bool isHeld = SEED.Mathf.Abs(rawAxis) > moveThreshold;

        // 押下 → 解放のエッジでだけマッピングを更新する
        if (wasInputHeld && !isHeld && travelSign is { } sign)
        {
            inputMapping = sign >= 0f ? MappingForward : MappingReversed;
        }

        wasInputHeld = isHeld;
    }

    /// <summary>
    /// 経路の接線に<b>符号ガード</b>を掛けて返す。ガードに掛かった場合は null。
    ///
    /// 接線は経路上の位置の数値微分なので、経路の形が急に変わる場所では
    /// 1 フレームで大きく向きが変わることがある。向きが<b>逆を向いた</b>のに
    /// プレイヤーがほとんど動いていないなら、それは経路の形（ヘアピン）ではなく
    /// 微分のブレなので、目標ヨーを更新しない
    /// （更新すると最短回りの補間が 180 度回してしまい、「クルっと回る」）。
    ///
    /// 逆に <see cref="TangentFlipGuardDistance"/> 以上動いているなら、
    /// 本当に折り返す経路を通過した可能性があるので反転をそのまま受け入れる
    /// （＝ヘアピンのある経路を壊さない）。
    /// </summary>
    /// <param name="tangent">今フレームの接線（長さ 0 なら向き無し）。</param>
    /// <param name="previousPosition">移動前のワールド位置。</param>
    /// <param name="currentPosition">移動後のワールド位置。</param>
    /// <returns>採用する接線。採用しない場合は null。</returns>
    private SEED.Vector3? GuardedTangent(
        SEED.Vector3 tangent, SEED.Vector3 previousPosition, SEED.Vector3 currentPosition)
    {
        if (tangent.SqrMagnitude <= SqrEpsilon) { return null; }   // 向きが定まらない区間

        // 「前回採用した接線からどれだけ進んだか」を積算する。
        // 1 フレームぶんの距離で判定すると、ゆっくり進んでいるあいだ反転が
        // 永久に却下され続け、本物の折り返しで向きを変えられなくなる。
        distanceSinceTangent += SEED.Vector3.Distance(previousPosition, currentPosition);

        if (previousTangent is { } previous)
        {
            // 内積が負 ＝ 前回採用した接線と逆を向いた
            bool flipped = SEED.Vector3.Dot(previous, tangent) < 0f;
            bool movedEnough = distanceSinceTangent > TangentFlipGuardDistance;

            if (flipped && !movedEnough)
            {
                // ほとんど動いていないのに反転した ＝ 経路の形ではないので採用しない。
                // previousTangent は更新せず、積算距離も持ち越す
                // （進み続ければいずれ movedEnough になり、本物の折り返しは通る）。
                return null;
            }
        }

        previousTangent = tangent;
        distanceSinceTangent = 0f;
        return tangent;
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
