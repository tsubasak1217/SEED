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

    /// <summary>
    /// 「視野角を上書きしない」ことを表す値（<see cref="SetOverrideGoal"/> の既定）。
    /// 0 度の画角には意味が無いので、0 以下を「指定なし」の合図に使う。
    /// </summary>
    private const float FovOverrideDisabled = 0f;

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
    /// リズムのやり取りの<b>回答フェーズ</b>（<see cref="FishingFight.Phase.Answer"/>）で使う
    /// 目標トランスフォーム（プレイヤーの子アクタ「AnswerCameraTarget」を割り当てる想定）。
    ///
    /// プレイヤーを正面斜めから見る構図にしておくと、叩くタイミングに集中させやすい。
    /// 未設定・無効なら回答中も従来どおり <see cref="castTarget"/> を追う。
    /// </summary>
    [SerializeField(Label = "回答中の目標トランスフォーム")]
    private SEED.Transform? answerTarget = null;

    /// <summary>
    /// 魚が沖へ走っているフェーズ（<see cref="FishingFight.Phase.LeadIn"/> ＝ ヒット直後の余白／
    /// <see cref="FishingFight.Phase.Run"/> ＝ 隙中に魚回復を拾ったあとの走り）で使う
    /// 目標トランスフォーム（トップレベルの空アクタ「RunCameraTarget」を割り当てる想定）。
    ///
    /// プレイヤーとウキの両方が画面に収まる斜め上からの構図にしたいため、位置・向きは
    /// 固定のシーン配置ではなく <see cref="FishingController.UpdateRunCameraTarget"/> が
    /// 毎フレーム計算して置き直す（<see cref="catchTarget"/> と同じ「アクタは空・中身は
    /// スクリプトが決める」方式）。未設定・無効なら引き演出中も従来の目標を追い続ける。
    /// </summary>
    [SerializeField(Label = "引き中の目標トランスフォーム")]
    private SEED.Transform? runTarget = null;

    /// <summary>
    /// リズムのやり取りの<b>出題フェーズ</b>（<see cref="FishingFight.Phase.Call"/>）と
    /// <b>巻き取り（隙）フェーズ</b>（<see cref="FishingFight.Phase.Rest"/>）で使う
    /// 目標トランスフォーム（トップレベルの空アクタ「CallCameraTarget」を割り当てる想定）。
    ///
    /// 位置・向きは FishingController が毎フレーム
    /// 「<see cref="castTarget"/> と同じ向きのまま、ウキからの距離だけをフェーズごとの倍率へ
    /// 変えた位置」へ置き直す（出題＝寄る／巻き取り＝引く）。
    /// 未設定・無効ならどちらのフェーズでも従来どおり <see cref="castTarget"/> を追う。
    /// </summary>
    [SerializeField(Label = "出題/巻き取り中の目標トランスフォーム")]
    private SEED.Transform? callTarget = null;

    /// <summary>
    /// 釣り上げ演出のスロー放物線以降（<see cref="CatchPresenter.CatchPhase.SlowArc"/> 以降）の
    /// 目標トランスフォーム（トップレベルの空アクタ「CatchCameraTarget」を割り当てる想定）。
    /// 位置・向きは <see cref="CatchPresenter"/> が毎フレーム「魚を見る姿勢」へ置き直す。
    /// 未設定なら寄りの構図は効かない（従来の目標を追い続ける）。
    /// </summary>
    [SerializeField(Label = "釣り上げ寄りの目標トランスフォーム")]
    private SEED.Transform? catchTarget = null;

    /// <summary>
    /// 釣果表示の目標トランスフォーム（プレイヤーの子アクタ「ResultCameraTarget」を割り当てる想定）。
    /// <see cref="catchTarget"/> が未設定のときの予備として使う。
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

    /// <summary>
    /// 上書き姿勢が入っているか（<see cref="SetOverrideGoal"/> が立て、
    /// <see cref="ClearOverrideGoal"/> が落とす）。
    /// true のあいだ <see cref="SelectGoalTransform"/> は一切参照しない。
    /// </summary>
    private bool hasOverrideGoal = false;

    /// <summary>上書き姿勢の位置（ワールド）。<see cref="hasOverrideGoal"/> が true のときだけ意味を持つ。</summary>
    private SEED.Vector3 overrideGoalPosition = SEED.Vector3.Zero;

    /// <summary>上書き姿勢の回転（オイラー角・度）。<see cref="hasOverrideGoal"/> が true のときだけ意味を持つ。</summary>
    private SEED.Vector3 overrideGoalRotation = SEED.Vector3.Zero;

    /// <summary>
    /// 上書き中の視野角（度）。<see cref="FovOverrideDisabled"/> 以下なら上書きしない
    /// （＝従来どおり釣り姿勢の判定で <see cref="fishingFov"/> / <see cref="normalFov"/> を選ぶ）。
    /// </summary>
    private float overrideFovDegrees = FovOverrideDisabled;

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
        // チュートリアルの締めの演出中は、演出側がカメラを直接動かすので追従しない
        // （両方が同じフレームで書くと、こちらが後から上書きして演出が効かなくなる）。
        // 上書きが無ければ従来どおり毎フレーム追従する。
        if (TutorialRules.Active && TutorialRules.CameraSuspended) { return; }

        // ── 目標の姿勢を決める ─────────────────────────────
        // 上書き姿勢（SetOverrideGoal）が入っているあいだは、シーンのどの
        // トランスフォームも見ずにその姿勢だけを使う
        // （＝構図を渡した側が「どこから・どっちを向くか」を完全に決める。
        //   シーンで結線した目標アクタの有無に結果が左右されない）。
        SEED.Vector3 goalPos;
        SEED.Vector3 goalRot;
        if (hasOverrideGoal)
        {
            goalPos = overrideGoalPosition;
            goalRot = overrideGoalRotation;
        }
        else
        {
            // 目標 = 目標トランスフォームの位置・回転そのまま。
            // 構図の計算はシーンの親子配置に完全に委ねる（このスクリプトは補間とロールだけを担う）。
            if (SelectGoalTransform() is not { } t || !t.IsValid) { return; }
            goalPos = t.Position;
            goalRot = t.Rotation;
        }

        // 移動ロール: プレイヤーの横方向速度（カメラの右方向成分）に比例した
        // 傾き（Z軸）を目標回転へ加算する。停止すれば 0 に戻り水平へ復帰する。
        //
        // 上書き姿勢のあいだは<b>加算しない</b>（止めた画が揺れて見えないように）。
        // ただし速度の基準になる前フレーム位置は毎フレーム必ず引き直す
        // （飛ばすと、上書きを外した最初のフレームに溜まった移動量が
        //   1 フレームぶんの速度として現れ、画面が跳ねて傾く）。
        if (player is { } p && p.IsValid)
        {
            float roll = ComputeMovementRoll(p.Position, goalRot.y, ctx.DeltaTime);
            if (!hasOverrideGoal)
            {
                goalRot = new SEED.Vector3(goalRot.x, goalRot.y, goalRot.z + roll);
            }
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

    /// <summary>
    /// 追従先を<b>姿勢そのもの</b>で上書きする【演出側が構図を握る唯一の入口】。
    ///
    /// シーンで結線した目標トランスフォーム（<see cref="catchTarget"/> など）に依存せず、
    /// 呼び出し側が計算した位置・回転（・視野角）へカメラを合わせる。
    /// 上書きは <see cref="ClearOverrideGoal"/> を呼ぶまで続き、そのあいだ
    /// 移動ロールも掛からない（＝渡した姿勢がそのまま画になる）。
    ///
    /// <paramref name="snap"/> を true にすると次の <see cref="LateUpdate"/> 1 回だけ
    /// 補間せず飛ぶ（＝カット）。上書きの設定と同じ呼び出しでカットまで済ませるので、
    /// 「切り替える前の目標へスナップしてしまう」取り違えが起こらない。
    /// </summary>
    /// <param name="position">目標の位置（ワールド）。</param>
    /// <param name="rotationDegrees">目標の回転（オイラー角・度）。</param>
    /// <param name="snap">true で補間せず 1 フレームで飛ぶ（カット）。</param>
    /// <param name="fovDegrees">
    /// 上書きする視野角（度）。<see cref="FovOverrideDisabled"/> 以下（既定）なら上書きせず、
    /// 従来どおり釣り姿勢の判定で決める。<paramref name="snap"/> が true ならこの値へも瞬時に合う。
    /// </param>
    public void SetOverrideGoal(
        SEED.Vector3 position, SEED.Vector3 rotationDegrees,
        bool snap, float fovDegrees = FovOverrideDisabled)
    {
        hasOverrideGoal = true;
        overrideGoalPosition = position;
        overrideGoalRotation = rotationDegrees;
        overrideFovDegrees = fovDegrees;
        if (snap) { snapRequested = true; }
    }

    /// <summary>
    /// 姿勢の上書きを外して通常の追従（<see cref="SelectGoalTransform"/>）へ戻す。
    /// 補間で戻るので、演出の終わりに呼べば構図が滑らかに繋がる。
    /// </summary>
    public void ClearOverrideGoal()
    {
        hasOverrideGoal = false;
        overrideFovDegrees = FovOverrideDisabled;
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

        // 魚が沖へ走っているあいだ（ヒット直後の余白 LeadIn ／ 隙のあとの走り Run）は
        // プレイヤー・ウキの両方を映す構図（回答中の構図より優先。走りのフェーズは
        // Answer と同時には起こり得ないので、実際に競合することは無い）。
        if (IsLeadInPhase() && runTarget is { } rt && rt.IsValid) { return rt; }

        // リズムの回答中はプレイヤーを見る構図へ切り替える（叩くタイミングに集中させる）
        if (IsAnswerPhase() && answerTarget is { } at && at.IsValid) { return at; }

        // リズムの出題中と巻き取り（隙）中は、キャスト中と同じ向きのまま距離だけを変えた構図。
        // 出題は寄って合図を大きく、巻き取りは引いて漂流物との位置関係を広く見せる
        // （倍率はどちらも FishingController 側のインスペクタ値で決まる）。
        if ((IsCallPhase() || IsRestPhase()) && callTarget is { } clt && clt.IsValid) { return clt; }

        // ウキが外に出ているあいだはウキ側の目標を最優先で追う（キャスト先が画面に入る）
        if (IsFloatOut() && castTarget is { } ct && ct.IsValid) { return ct; }

        if (IsPlayerFishing() && fishingTarget is { } ft && ft.IsValid) { return ft; }

        return target;
    }

    /// <summary>
    /// 釣り上げ演出中に追うべき目標トランスフォームを返す（演出中でなければ null）。
    ///
    /// フェーズは順序どおりに並んでいるので、判定は
    /// 「<see cref="CatchPresenter.CatchPhase.Fade"/>（白へ沈むまで）は<b>通常の構図のまま</b>、
    /// それ以降（None を除く）は演出専用の構図」の 2 分岐で済む。
    ///
    /// <b>Fade で切り替えない理由</b>: 構図のカットは画面が真っ白になった裏で
    /// <see cref="CatchPresenter"/> が行う（<c>RequestSnap</c>）。白より前に切り替えると
    /// 視点の飛びがそのまま見えてしまう。
    /// 割り当てが無いフェーズでは null を返し、呼び出し側が従来の選択へ落ちる。
    /// </summary>
    private SEED.Transform? SelectCatchGoal()
    {
        if (fishing is not { } f) { return null; }
        if (f.State != FishingController.FishState.Catching) { return null; }

        var phase = f.CatchPhase;
        if (phase == CatchPresenter.CatchPhase.None) { return null; }
        if (phase == CatchPresenter.CatchPhase.Fade) { return null; }

        if (catchTarget is { IsValid: true } ct) { return ct; }
        return resultTarget is { IsValid: true } rt ? rt : null;
    }

    /// <summary>
    /// プレイヤーが釣り姿勢かどうかを返す（目標トランスフォームの選択・FOV 目標の両方で使う共通判定）。
    /// <see cref="playerMove"/> が未設定なら常に false（釣り演出は一切効かない）。
    /// </summary>
    private bool IsPlayerFishing()
        => playerMove is { } pm && pm.State == PlayerMove.PlayerState.FishingStance;

    /// <summary>
    /// リズムのやり取りが<b>回答フェーズ</b>かを返す（構図切替の唯一の判定点）。
    ///
    /// 魚が掛かっている（<see cref="FishingController.FishState.Hooked"/>）ときだけ見る。
    /// <see cref="fishing"/> 未設定なら常に false。
    /// </summary>
    private bool IsAnswerPhase()
        => fishing is { } f
        && f.State == FishingController.FishState.Hooked
        && f.FightPhase == FishingFight.Phase.Answer;

    /// <summary>
    /// リズムのやり取りが<b>出題フェーズ</b>かを返す（構図切替の唯一の判定点）。
    ///
    /// 魚が掛かっている（<see cref="FishingController.FishState.Hooked"/>）ときだけ見る。
    /// <see cref="fishing"/> 未設定なら常に false。
    /// </summary>
    private bool IsCallPhase()
        => fishing is { } f
        && f.State == FishingController.FishState.Hooked
        && f.FightPhase == FishingFight.Phase.Call;

    /// <summary>
    /// リズムのやり取りが<b>巻き取り（隙）フェーズ</b>かを返す（構図切替の唯一の判定点）。
    ///
    /// 魚が掛かっている（<see cref="FishingController.FishState.Hooked"/>）ときだけ見る。
    /// <see cref="fishing"/> 未設定なら常に false。
    /// なお岸際まで寄せたときは FishingController が姿勢を直接上書きするので、
    /// この判定より上書きのほうが優先される。
    /// </summary>
    private bool IsRestPhase()
        => fishing is { } f
        && f.State == FishingController.FishState.Hooked
        && f.FightPhase == FishingFight.Phase.Rest;

    /// <summary>
    /// 魚が沖へ走っているフェーズ（ヒット直後の余白 <see cref="FishingFight.Phase.LeadIn"/> と、
    /// 隙中に魚回復を拾ったあとの走り <see cref="FishingFight.Phase.Run"/>）中かを返す
    /// （構図切替の唯一の判定点）。
    ///
    /// どのフェーズを「引きの構図」で撮るかの定義は
    /// <see cref="FishingController.IsRunCameraPhase"/> に一元化してある
    /// （目標トランスフォームを置き直す側と同じ判定を使うので、構図の選択と目標の更新が
    /// 食い違うことがない）。<see cref="fishing"/> 未設定なら常に false。
    /// </summary>
    private bool IsLeadInPhase()
        => fishing is { } f
        && f.State == FishingController.FishState.Hooked
        && FishingController.IsRunCameraPhase(f.FightPhase);

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

        // 上書き中（演出が構図を握っている区間）は指定された画角を最優先で使う。
        // ここを通さないと、カットで位置・回転だけが飛んで画角だけが補間で寄っていき、
        // 「止まっているのに画が動く」状態になる。
        //
        // 上書きが無ければ、釣り姿勢中もキャスト中も同じ寄り（fishingFov）にする。
        // キャスト専用の FOV は今のところ必要が無く、切替が増えるほど画が落ち着かないため。
        float goalFov = hasOverrideGoal && overrideFovDegrees > FovOverrideDisabled
            ? overrideFovDegrees
            : ((IsPlayerFishing() || IsFloatOut()) ? fishingFov : normalFov);

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
