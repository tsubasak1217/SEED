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
///
/// さらに<b>経路移動モードのときだけ</b>、Space キーで釣り姿勢の状態機械が動く:
/// 通常 →（Space）→ その場で釣り姿勢（海の方を向く） →（Space）→ 通常。
/// 詳細は <see cref="PlayerState"/> を参照。自由移動モードでは Space は何もしない。
/// </summary>
public class PlayerMove : SEEDScript
{
    /// <summary>
    /// プレイヤーの行動状態。<b>経路移動モードでのみ</b> Normal 以外になる。
    ///
    /// スクリプトはファイル名＝型名で 1 ファイル 1 スクリプトクラスとして扱われるため、
    /// この列挙型は独立ファイルにせず <see cref="PlayerMove"/> の入れ子として定義する
    /// （外部からは <c>PlayerMove.PlayerState</c> で参照できる）。
    /// </summary>
    public enum PlayerState
    {
        /// <summary>通常。入力で経路上を前後に移動できる。</summary>
        Normal,

        /// <summary>釣り姿勢。入力は無視し、海（経路進行方向の右手側）を向いて待機する。</summary>
        FishingStance,
    }

    /// <summary>現在の行動状態（他スクリプトから参照する読み取り専用プロパティ）。</summary>
    public PlayerState State { get; private set; } = PlayerState.Normal;

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

    // ─── 経路の等速化（弧長補正）────────────────────────────────
    //
    // Catmull-Rom は弧長パラメータ化されていないため、時刻を等速で進めても
    // ワールド上の速度は区間内で脈動する（等間隔の点列でも継ぎ目付近で遅く、
    // 区間中央で速い。実測 ±8% 程度 → 「等間隔にかくつく」見え方になる）。
    // そこで「経路全体の平均パラメータ速度 ÷ その場の局所パラメータ速度」を
    // 時刻の進みに掛けて、ワールド上の移動速度を一定化する。
    // 平均を分子にするので 1 周にかかる時間は補正前と変わらない（再調整不要）。

    /// <summary>平均パラメータ速度を求めるときの経路の分割数（折れ線近似）。</summary>
    private const int AverageSpeedSampleCount = 64;

    /// <summary>局所パラメータ速度を中央差分で求めるときの時刻の刻み幅（秒）。</summary>
    private const float LocalSpeedEpsilonSeconds = 0.01f;

    /// <summary>等速化補正の下限倍率（異常値・数値ノイズで急減速しないための安全弁）。</summary>
    private const float MinTimeScale = 0.25f;

    /// <summary>等速化補正の上限倍率（停留点・Step 区間で無限加速しないための安全弁）。</summary>
    private const float MaxTimeScale = 4.0f;

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

    /// <summary>
    /// カメラ目標の親（CameraTargetParent）。設定すると、毎フレーム
    /// <b>経路上の現在位置＋経路接線の向き</b>へ更新する。
    ///
    /// 接線は「経路の時刻が進む向き」で入力の正負を掛けない（＝逆走しても
    /// <b>振り返らない</b>）ので、この親の子に置いたオフセット（カメラの目標点）は
    /// プレイヤーが反転しても反対側へ回り込まない。
    /// CameraMove はその子のトランスフォームを目標に補間するだけでよい。
    /// </summary>
    [SerializeField(Label = "カメラ目標の親（経路上を追従）")]
    private SEED.Transform? cameraTargetParent = null;

    // ─── 釣り姿勢（経路移動モード限定）───────────────────────

    /// <summary>釣り姿勢中に再生するクリップ名（プレイヤー本体の Animator）。</summary>
    [Header("釣り姿勢（経路移動時のみ）"), SerializeField(Label = "釣り姿勢クリップ名")]
    private string fishingClip = "IdleFishing";

    // ─── 回転補間 ─────────────────────────────────────────────
    //
    // カメラ（CameraMove）は移動方向から自前で視点を安定化させ、逆走しても
    // 回り込まない（常に同じ側から見る）。そのため画面上の「前後」は反転せず、
    // 入力の意味も常に一定（+y = 経路の正方向）。入力の切替処理は存在しない。

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

    /// <summary>
    /// 釣り竿（子アクター）の Animator。未設定なら竿のアニメ切替は行わない
    /// （プレイヤー本体のアニメだけが切り替わる）。
    /// </summary>
    [SerializeField(Label = "竿の Animator")]
    private SEED.Animator? rodAnimator = null;

    /// <summary>釣り姿勢中に再生する竿のクリップ名。</summary>
    [SerializeField(Label = "竿の釣り姿勢クリップ名")]
    private string rodFishingClip = "IdleFishing_竿";

    /// <summary>釣り姿勢を解除したときに戻す竿のクリップ名。</summary>
    [SerializeField(Label = "竿の待機クリップ名")]
    private string rodIdleClip = "Idle_竿";

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
    /// 経路全体の平均パラメータ速度（m/経路秒）。null=未計測。
    /// 初回に折れ線近似で 1 度だけ計測してキャッシュする
    /// （スクリプトのホットリロードで状態は破棄されるので、経路を編集したら再計測される）。
    /// </summary>
    private float? averageParamSpeed = null;

    /// <summary>
    /// 向きの目標ヨー角（度）。null は「まだ一度も進行方向が決まっていない」。
    ///
    /// 入力が切れた後も最後の目標へ向かって回り続けさせるため、
    /// 「目標の更新（移動中のみ）」と「目標への補間（毎フレーム）」を分けて保持する。
    /// </summary>
    private float? targetYaw = null;

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

        bool moving;
        if (onPath)
        {
            var activePath = path!.Value;

            // Space による状態遷移（通常⇔釣り姿勢）を先に処理する。
            // 遷移した結果を同フレームの移動処理へ反映させたいので移動より前に呼ぶ。
            UpdateFishingStateInput(activePath);

            moving = State switch
            {
                // 釣り姿勢中: 移動しない（向きの補間だけ続ける）
                PlayerState.FishingStance => false,
                // 通常: 従来どおりの入力による経路移動
                _ => UpdatePathMovement(ref ctx, activePath),
            };
        }
        else
        {
            moving = UpdateFreeMovement(ref ctx);
        }

        // 目標ヨーへの補間は移動モードに依らず毎フレーム行う
        // （入力を離した後も回りきってから止まるので、向きが途中で固まらない）
        // 釣り姿勢でも回し続けるので、海の方を向く回転が途中で止まらない。
        UpdateRotation(ctx.DeltaTime);

        // 移動状態が変わったフレームだけアニメを切り替える（毎フレーム Play すると先頭に戻り続けるため）。
        // 釣り姿勢中は専用クリップを再生しているので、Idle/Running のラッチに触れさせない。
        if (State != PlayerState.FishingStance)
        {
            UpdateAnimation(moving);
        }
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

        // 前後入力だけを使う（左右は経路に沿う移動では意味を持たない）。
        // カメラは逆走でも回り込まない（CameraMove が視点を安定化する）ため、
        // 入力の意味は常に一定: +y = 経路の正方向。ラッチや反転処理は不要。
        float effectiveAxis = im.GetVector2("Move").y;

        // 初回は経路の開始時刻へ合わせる（時刻の原点は制御点が決めるので 0 とは限らない）
        if (!pathTimeInitialized)
        {
            pathTime = p.StartTime;
            pathTimeInitialized = true;
        }

        // 経路上の時刻を進める。閉ループならエンジン側で周回し、開経路なら両端でクランプされる。
        // 等速化補正（弧長補正）を掛け、Catmull-Rom の区間内の速度脈動を打ち消して
        // ワールド上の移動速度を一定にする（かくつき対策。詳細は定数群のコメント参照）。
        var previousPosition = transform.Position;
        pathTime += effectiveAxis * pathSpeed * ComputeConstantSpeedScale(p, pathTime) * ctx.DeltaTime;

        // 保持している時刻を経路の範囲へ畳み込み、位置とカメラ目標の親を更新する
        // （畳み込み・配置の規則は自動移動と共有する。詳細は各ヘルパのコメント参照）。
        pathTime = WrapPathTime(p, pathTime);
        ApplyPathTransform(p, pathTime);

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
    /// 経路上の時刻を経路の範囲へ畳み込んで返す（畳み込んでも評価結果は変わらない）。
    /// 閉ループ: 1 周ぶんで巻き戻す ／ 開経路: 両端でクランプする。
    ///
    /// ※ Duration は閉ループでは制御点の配置に依存する（閉じる区間の所要時刻が
    ///   距離比例で決まるため。docs/control_points.md「閉じる区間の時刻の定義」）。
    ///   定数 1.0 を前提に自前計算せず、毎フレーム問い合わせること。
    /// </summary>
    /// <param name="p">評価対象の経路。</param>
    /// <param name="time">畳み込む前の経路時刻（秒）。</param>
    /// <returns>経路の範囲へ畳み込んだ経路時刻（秒）。</returns>
    private float WrapPathTime(SEED.ControlPointPath p, float time)
    {
        float duration = p.Duration;
        if (duration <= 0f) { return time; }   // 点が 1 個など、時間幅の無い経路

        float startTime = p.StartTime;
        return p.Closed
            ? startTime + SEED.Mathf.Repeat(time - startTime, duration)
            : SEED.Mathf.Clamped(time, startTime, startTime + duration);
    }

    /// <summary>
    /// 経路上の指定時刻へプレイヤーを乗せ、カメラ目標の親も同じ点へ更新する。
    /// 入力による移動と釣り位置への自動移動で共通に使う（配置規則を一箇所に集約する）。
    /// </summary>
    /// <param name="p">評価対象の経路。</param>
    /// <param name="time">経路の範囲へ畳み込み済みの経路時刻（秒）。</param>
    private void ApplyPathTransform(SEED.ControlPointPath p, float time)
    {
        // 位置を経路上へ乗せる。高さは設定に応じて経路に従うか、重力へ委ねる。
        var onPath = p.SamplePosition(time);
        transform.Position = followPathHeight
            ? onPath
            : new SEED.Vector3(onPath.x, transform.Position.y, onPath.z);

        // カメラ目標の親を経路上の現在位置＋経路接線の向きへ更新する。
        // 接線に入力の正負を掛けないのが要点: 逆走しても親は振り返らないので、
        // 子に置いたカメラ目標点が反対側へ回り込まない。
        if (cameraTargetParent is { } anchor && anchor.IsValid)
        {
            anchor.Position = onPath;
            if (p.SampleTangent(time) is { } pathDir && pathDir.SqrMagnitude > SqrEpsilon)
            {
                // ヨーだけ向ける（エンジン規約: yaw = atan2(x, z)、前方 +Z）。
                // ピッチ/ロールは 0 のまま（坂でも子のオフセットの高さ関係を崩さない）。
                float anchorYaw = SEED.Mathf.Atan2(pathDir.x, pathDir.z) * SEED.Mathf.Rad2Deg;
                anchor.Rotation = new SEED.Vector3(0f, anchorYaw, 0f);
            }
            // 接線が定まらない場所（Step 区間・停留点）では直前の向きを維持する
        }
    }

    // ─── 釣り姿勢の状態機械 ───────────────────────────────────

    /// <summary>
    /// Space キーによる状態遷移を処理する（経路移動モードでのみ呼ばれる）。
    ///
    /// - 通常     → その場で釣り姿勢へ入る（移動なし。海の方へ向き直るのみ）
    /// - 釣り姿勢 → 通常へ戻し、待機アニメへ復帰
    /// </summary>
    /// <param name="p">評価対象の経路。</param>
    private void UpdateFishingStateInput(SEED.ControlPointPath p)
    {
        if (!SEED.Input.GetKeyDown(SEED.KeyCode.Space)) { return; }

        switch (State)
        {
            case PlayerState.Normal:
                EnterFishingStance(p);
                break;

            case PlayerState.FishingStance:
                ExitFishingStance();
                break;
        }
    }

    /// <summary>
    /// 釣り姿勢へ入る。海の方（経路進行方向の右手側）へ向き直り、釣りアニメへ切り替える。
    ///
    /// <b>右手方向の求め方</b>: SEED は左手系で前方 +Z、ヨー 0 のとき右 = (1,0,0)。
    /// 回転行列の列（transform.rs の rotation_basis）はピッチ・ロール 0 のとき
    /// 前 = (sinY, 0, cosY) / 右 = (cosY, 0, -sinY) になる。
    /// よって前方が (x, ?, z) のとき右方向は (z, 0, -x) である。
    /// </summary>
    /// <param name="p">評価対象の経路。</param>
    private void EnterFishingStance(SEED.ControlPointPath p)
    {
        State = PlayerState.FishingStance;

        // 経路の進行方向に対して常に右手側が海。接線から右方向を作って向く。
        if (p.SampleTangent(pathTime) is { } dir && dir.SqrMagnitude > SqrEpsilon)
        {
            UpdateTargetYaw(new SEED.Vector3(dir.z, 0f, -dir.x));
        }

        // 本体と竿を釣りアニメへ。実際の向き直しは UpdateRotation が補間して行う。
        CrossFadeTo(ResolveAnimator(), fishingClip);
        CrossFadeTo(rodAnimator, rodFishingClip);
    }

    /// <summary>
    /// 釣り姿勢を解除して通常状態へ戻す。本体・竿を待機アニメへ戻す。
    ///
    /// <see cref="isRunning"/> を未初期化へ戻すのが要点: 釣り姿勢中は
    /// <see cref="UpdateAnimation"/> を呼んでいないためラッチが実態とずれている。
    /// null にしておけば次のフレームで移動状態に合わせて必ず貼り直される。
    /// </summary>
    private void ExitFishingStance()
    {
        State = PlayerState.Normal;

        CrossFadeTo(ResolveAnimator(), idleClip);
        CrossFadeTo(rodAnimator, rodIdleClip);

        isRunning = null;
    }

    /// <summary>
    /// 指定 Animator を指定クリップへクロスフェードする（未設定・無効・空名・再生中は何もしない）。
    /// </summary>
    /// <param name="target">対象の Animator（null 可）。</param>
    /// <param name="clip">再生するクリップ名。</param>
    private void CrossFadeTo(SEED.Animator? target, string clip)
    {
        if (target is not { } anim || !anim.IsValid) { return; }
        if (string.IsNullOrEmpty(clip)) { return; }

        // 既に同じクリップが再生中なら再指示しない（先頭へ戻ってしまうのを防ぐ）
        if (anim.IsPlaying && anim.CurrentClip == clip) { return; }

        anim.CrossFade(clip, fadeSeconds);
    }

    /// <summary>
    /// 使用するプレイヤー本体の Animator を返す。
    /// インスペクタで指定が無ければ自分自身の Animator を使う（モデルが子アクタの場合は指定が必要）。
    /// </summary>
    private SEED.Animator? ResolveAnimator()
        => animator ?? gameObject.GetComponent<SEED.Animator>();

    /// <summary>
    /// 経路の等速化補正倍率を返す（平均パラメータ速度 ÷ 局所パラメータ速度）。
    ///
    /// - 局所速度が平均より<b>遅い</b>場所（継ぎ目付近）では 1 より大きく → 時刻を速く進める
    /// - 局所速度が平均より<b>速い</b>場所（区間中央）では 1 より小さく → 時刻を遅く進める
    /// 結果としてワールド上の速度が一定になり、1 周にかかる時間は補正前と同じ。
    ///
    /// 平均は初回に 1 度だけ折れ線近似（<see cref="AverageSpeedSampleCount"/> 分割）で計測し、
    /// 局所は毎フレーム中央差分（±<see cref="LocalSpeedEpsilonSeconds"/> 秒）で求める。
    /// 停留点（局所速度ほぼ 0）や異常値は <see cref="MinTimeScale"/>〜<see cref="MaxTimeScale"/> で
    /// クランプして発散させない。
    /// </summary>
    /// <param name="p">評価対象の経路。</param>
    /// <param name="t">現在の経路時刻（秒）。</param>
    private float ComputeConstantSpeedScale(SEED.ControlPointPath p, float t)
    {
        float duration = p.Duration;
        if (duration <= 0f) { return 1f; }

        // 平均パラメータ速度（キャッシュが無ければ計測する）
        if (averageParamSpeed is not { } average)
        {
            float total = 0f;
            var prev = p.SamplePosition(p.StartTime);
            for (int i = 1; i <= AverageSpeedSampleCount; i++)
            {
                var cur = p.SamplePosition(p.StartTime + duration * i / AverageSpeedSampleCount);
                total += SEED.Vector3.Distance(prev, cur);
                prev = cur;
            }
            average = total / duration;
            averageParamSpeed = average;
        }
        if (average <= SqrEpsilon) { return 1f; } // 全点同一座標など、動きの無い経路

        // 局所パラメータ速度（中央差分）。閉ループは時刻が周回するので範囲外でも正しく評価される。
        var a = p.SamplePosition(t - LocalSpeedEpsilonSeconds);
        var b = p.SamplePosition(t + LocalSpeedEpsilonSeconds);
        float local = SEED.Vector3.Distance(a, b) / (2f * LocalSpeedEpsilonSeconds);
        if (local <= SqrEpsilon) { return MaxTimeScale; } // 停留点・Step 区間は上限倍率で通過

        return SEED.Mathf.Clamped(average / local, MinTimeScale, MaxTimeScale);
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

        if (ResolveAnimator() is not { } anim) { return; }   // Animator が無ければ移動だけ行う

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
