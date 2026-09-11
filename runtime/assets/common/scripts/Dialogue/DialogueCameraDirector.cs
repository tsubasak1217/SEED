// ============================================================================
//  DialogueCameraDirector.cs
//  会話中のカメラ移動（カット／補間）だけを担当する。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// カメラ移動担当コンポーネント。カメラアクター（MainCamera）に付ける。
///
/// 【責務】
///  指定された Transform（空アクター）の位置・回転・<b>画角(FOV)</b> へ、カメラ自身を
///  「即座に（cut）」または「時間を掛けて（lerp）」寄せる。それだけを行う。
///  どの台詞でどこへ寄せるかは DialogueDirector が決める（単一責任）。
///
/// 【回転の補間について】
///  Transform.Rotation は YXZ オイラー角（度）で、単純な線形補間では
///  350 度 → 10 度 が逆回り（350→180→10）になってしまう。
///  そのため成分ごとに最短回りで補間する AngleMath.LerpEuler を使う。
///
/// 【画角(FOV)の補間について】
///  カメラ目標アクター（CamTarget_*）に <b>Camera コンポーネント</b>を付けて
///  「垂直視野角」を設定しておくと、その台詞ではその画角へも補間される
///  （位置・回転とまったく同じイージング・同じ進捗で線形補間する）。
///  Camera が付いていない目標アクターでは画角は変更されない（従来どおりの挙動）。
///  画角補間そのものを切りたい場合は「画角も補間する」を false にする。
///
/// 【会話前の姿勢へ戻す】
///  DialogueDirector は会話開始時に <see cref="CaptureReturnPoint"/> で
///  カメラの姿勢と画角を控え、会話終了時に <see cref="ReturnToCapturedPoint"/> で
///  そこへ戻す。戻し方（Cut / Lerp と秒数）は DialogueDirector 側の設定で決まる。
///
/// 【シーン側の設定】
///  - MainCamera にこのスクリプトを付ける。
///  - DialogueDirector の「カメラ演出」参照にこのアクターを指定する。
///  - カメラ目標アクターに Camera を付けて FOV を設定すると、その台詞でその画角へ補間される。
/// </summary>
public class DialogueCameraDirector : SEEDScript
{
    // ── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>移動時間がこの値以下なら「即時（cut）」として扱う（0 除算回避）。</summary>
    private const float MinMoveDuration = 0.0001f;

    /// <summary>進捗の完了値。</summary>
    private const float ProgressComplete = 1f;

    /// <summary>
    /// 「画角の指定なし」を表す番兵値。
    ///
    /// 視野角は必ず正の度数なので 0 は実在しない値であり、
    /// 「目標アクターに Camera が無い」「自分に Camera が無い」「画角補間が off」を
    /// まとめてこの値で表せる（別フラグを増やさずに済む）。
    /// この値以下の画角は読み取り時も書き込み時も無視する。
    /// </summary>
    private const float FovUnspecified = 0f;

    // ── インスペクタ公開フィールド ──────────────────────────

    /// <summary>
    /// カメラの補間移動を<b>実時間</b>（<see cref="SEED.Time.UnscaledDeltaTime"/>）で
    /// 進めるか。
    ///
    /// 既定は false（ゲーム時間）。<c>Time.Scale = 0</c> でゲームを止めたまま
    /// 会話を流すシーンでは true にしないと、カメラが動き出さずに固まる。
    /// </summary>
    [SerializeField(Label = "実時間で進める", Tooltip = "Time.Scale = 0 で止めたままカメラを動かすシーンでは true にする")]
    public bool useUnscaledTime;

    /// <summary>
    /// 画角(FOV)も補間するか。
    ///
    /// true（既定）なら、カメラ目標アクターに付いた Camera の「垂直視野角」を
    /// その台詞の画角の目標として使い、位置・回転と同じイージングで寄せる。
    /// false にすると画角には一切触らない（位置・回転だけを動かす従来の挙動）。
    /// </summary>
    [SerializeField(Label = "画角も補間する", Tooltip = "カメラ目標アクターの Camera の視野角へ、位置・回転と同じイージングで寄せる")]
    public bool interpolateFov = true;

    // ── 内部状態 ────────────────────────────────────────────

    /// <summary>補間移動の最中か。</summary>
    private bool _moving;

    /// <summary>補間開始時のカメラ位置。</summary>
    private SEED.Vector3 _startPosition;

    /// <summary>補間開始時のカメラ回転（YXZ オイラー角・度）。</summary>
    private SEED.Vector3 _startRotation;

    /// <summary>補間開始時のカメラ画角（度）。<see cref="FovUnspecified"/> なら画角は補間しない。</summary>
    private float _startFov;

    /// <summary>
    /// 補間先の位置。
    /// 目標 Transform は開始時に一度だけサンプリングする
    /// （移動中に目標アクターが動いても演出が破綻しないようにするため）。
    /// </summary>
    private SEED.Vector3 _endPosition;

    /// <summary>補間先の回転（YXZ オイラー角・度）。</summary>
    private SEED.Vector3 _endRotation;

    /// <summary>
    /// 補間先の画角（度）。<see cref="FovUnspecified"/> なら画角を動かさない。
    /// 開始値・目標値の<b>どちらか一方でも読めなかったとき</b>もここが
    /// <see cref="FovUnspecified"/> になり、位置・回転だけの補間へ自動的に落ちる。
    /// </summary>
    private float _endFov;

    /// <summary>補間に掛ける秒数。</summary>
    private float _duration;

    /// <summary>補間開始からの経過秒。</summary>
    private float _elapsed;

    // ── 戻り先（会話開始前のカメラ姿勢）──────────────────────

    /// <summary>戻り先を控えてあるか（<see cref="CaptureReturnPoint"/> 済みか）。</summary>
    private bool _hasReturnPoint;

    /// <summary>会話開始前のカメラ位置。</summary>
    private SEED.Vector3 _returnPosition;

    /// <summary>会話開始前のカメラ回転（YXZ オイラー角・度）。</summary>
    private SEED.Vector3 _returnRotation;

    /// <summary>会話開始前のカメラ画角（度）。<see cref="FovUnspecified"/> なら画角は戻さない。</summary>
    private float _returnFov;

    // ── 公開プロパティ ──────────────────────────────────────

    /// <summary>補間移動の最中か（cut の場合は常に false）。</summary>
    public bool IsMoving => _moving;

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 毎フレーム、補間移動を進める。
    /// </summary>
    /// <param name="ctx">フレーム情報（DeltaTime を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (!_moving) return;

        // ゲーム時間か実時間かは useUnscaledTime で切り替える（時間停止中の演出に対応）
        _elapsed += useUnscaledTime ? SEED.Time.UnscaledDeltaTime : ctx.DeltaTime;

        // 生の進捗 → イージング済みの進捗
        float rawProgress = SEED.Mathf.Clamped01(_elapsed / _duration);
        float eased       = AngleMath.SmoothStep01(rawProgress);

        transform.Position = SEED.Vector3.Lerp(_startPosition, _endPosition, eased);
        transform.Rotation = AngleMath.LerpEuler(_startRotation, _endRotation, eased);

        // 画角は位置・回転と同じ進捗で線形補間する（画と構図がずれないように同じ eased を使う）
        if (_endFov > FovUnspecified)
        {
            ApplyFieldOfView(SEED.Mathf.Lerp(_startFov, _endFov, eased));
        }

        // 完了したら目標値へぴたりと合わせて終了する（誤差の蓄積を残さない）
        if (rawProgress >= ProgressComplete)
        {
            ApplyImmediately(_endPosition, _endRotation, _endFov);
            _moving = false;
        }
    }

    // ── 公開メソッド ────────────────────────────────────────

    /// <summary>
    /// 指定した Transform の姿勢（と、その アクターに Camera があれば画角）へカメラを移動させる。
    /// </summary>
    /// <param name="target">目標の空アクターの Transform。無効なら何もしない。</param>
    /// <param name="mode">移動方法（DialogueCameraMode.Cut / Lerp）。</param>
    /// <param name="duration">lerp のときの移動時間（秒）。0 以下なら cut と同じ。</param>
    public void MoveTo(SEED.Transform target, DialogueCameraMode mode, float duration)
    {
        // 目標未設定（IsValid == false）ならカメラを動かさない
        if (!target.IsValid) return;

        // 目標姿勢はここで 1 回だけ読む
        BeginMove(target.Position, target.Rotation, ReadTargetFieldOfView(target), mode, duration);
    }

    /// <summary>
    /// 現在のカメラ姿勢（位置・回転・画角）を「戻り先」として控える。
    ///
    /// 会話開始時に DialogueDirector が呼ぶ。控えた値は
    /// <see cref="ReturnToCapturedPoint"/> で 1 回使うと破棄される
    /// （次の会話では改めて控え直す）。
    /// </summary>
    public void CaptureReturnPoint()
    {
        _returnPosition = transform.Position;
        _returnRotation = transform.Rotation;

        // 画角補間が off、または Camera が無いときは FovUnspecified になり、
        // 戻すときも画角には触らない（位置・回転だけ戻す）。
        _returnFov      = interpolateFov ? CurrentFieldOfView() : FovUnspecified;

        _hasReturnPoint = true;
    }

    /// <summary>
    /// <see cref="CaptureReturnPoint"/> で控えた姿勢（位置・回転・画角）へカメラを戻す。
    ///
    /// 控え値が無い（会話開始を経由していない）場合は何もしない。
    /// 控え値は 1 回使うと破棄されるので、二重に呼んでも二度は戻らない。
    /// </summary>
    /// <param name="mode">戻し方（DialogueCameraMode.Cut / Lerp）。</param>
    /// <param name="duration">lerp のときの戻し時間（秒）。0 以下なら cut と同じ。</param>
    /// <returns>
    /// 補間（lerp）で戻し始めたら true。
    /// 即時（cut）で戻した場合と、控え値が無くて何もしなかった場合は false
    /// （呼び出し側が「戻り終わるまで待つ」かどうかの判断に使う）。
    /// </returns>
    public bool ReturnToCapturedPoint(DialogueCameraMode mode, float duration)
    {
        if (!_hasReturnPoint) return false;

        // 控え値は使い捨て（会話ごとに開始時へ取り直す）
        _hasReturnPoint = false;

        return BeginMove(_returnPosition, _returnRotation, _returnFov, mode, duration);
    }

    /// <summary>
    /// 進行中の補間を打ち切り、目標姿勢（画角を含む）へ即座に合わせる。
    /// </summary>
    public void SnapToTarget()
    {
        if (!_moving) return;
        ApplyImmediately(_endPosition, _endRotation, _endFov);
        _moving = false;
    }

    // ── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 目標姿勢への移動を開始する【cut / lerp の分岐の唯一の実装】。
    /// </summary>
    /// <param name="endPosition">目標位置。</param>
    /// <param name="endRotation">目標回転（YXZ オイラー角・度）。</param>
    /// <param name="endFov">目標画角（度）。<see cref="FovUnspecified"/> なら画角は動かさない。</param>
    /// <param name="mode">移動方法。</param>
    /// <param name="duration">lerp のときの移動時間（秒）。</param>
    /// <returns>補間（lerp）を開始したら true、即時（cut）で終えたら false。</returns>
    private bool BeginMove(
        SEED.Vector3 endPosition, SEED.Vector3 endRotation, float endFov,
        DialogueCameraMode mode, float duration)
    {
        // Lerp 以外（未知の値は宣言時の初期値である Cut に丸められる）は即時切り替え
        switch (mode)
        {
            case DialogueCameraMode.Lerp when duration > MinMoveDuration:
                break;

            case DialogueCameraMode.Cut:
            default:
                ApplyImmediately(endPosition, endRotation, endFov);
                _moving = false;
                return false;
        }

        float startFov = CurrentFieldOfView();

        _startPosition = transform.Position;
        _startRotation = transform.Rotation;
        _startFov      = startFov;
        _endPosition   = endPosition;
        _endRotation   = endRotation;

        // 開始値と目標値の両方が読めたときだけ画角を補間する。
        // 片方でも欠けていると「0 度から寄っていく」ような破綻した補間になるため、
        // そのときは画角を触らず位置・回転だけの移動へ落とす。
        _endFov        = (endFov > FovUnspecified && startFov > FovUnspecified)
            ? endFov
            : FovUnspecified;

        _duration      = duration;
        _elapsed       = 0f;
        _moving        = true;
        return true;
    }

    /// <summary>
    /// カメラの姿勢を即座に設定する。
    /// </summary>
    /// <param name="position">設定する位置。</param>
    /// <param name="rotation">設定する回転（YXZ オイラー角・度）。</param>
    /// <param name="fovDegrees">設定する画角（度）。<see cref="FovUnspecified"/> 以下なら画角は変更しない。</param>
    private void ApplyImmediately(SEED.Vector3 position, SEED.Vector3 rotation, float fovDegrees)
    {
        transform.Position = position;
        transform.Rotation = rotation;
        ApplyFieldOfView(fovDegrees);
    }

    /// <summary>
    /// カメラ目標アクターに設定された画角を読む。
    ///
    /// 目標アクターの Camera（CameraComponent）の「垂直視野角」をその台詞の画角目標として使う。
    /// 目標 Transform からアクターを辿るのに <c>Transform.GameObject</c> を使う
    /// （Transform はアクターのルート entity 直付けなので、そのままアクターとして扱える）。
    /// </summary>
    /// <param name="target">カメラ目標アクターの Transform。</param>
    /// <returns>
    /// 目標画角（度）。画角補間が off か、目標アクターに Camera が無いときは
    /// <see cref="FovUnspecified"/>（＝画角を動かさない）。
    /// </returns>
    private float ReadTargetFieldOfView(SEED.Transform target)
    {
        if (!interpolateFov) return FovUnspecified;

        return target.GameObject.GetComponent<SEED.Camera>() is { } cam
            ? cam.FieldOfView
            : FovUnspecified;
    }

    /// <summary>
    /// 自分（カメラアクター）の現在の画角を読む。
    /// </summary>
    /// <returns>
    /// 現在の画角（度）。画角補間が off か、Camera が付いていないときは
    /// <see cref="FovUnspecified"/>。
    /// </returns>
    private float CurrentFieldOfView()
    {
        if (!interpolateFov) return FovUnspecified;

        return SelfCamera() is { } cam ? cam.FieldOfView : FovUnspecified;
    }

    /// <summary>
    /// 自分（カメラアクター）の画角を設定する。
    /// </summary>
    /// <param name="fovDegrees">設定する画角（度）。<see cref="FovUnspecified"/> 以下なら何もしない。</param>
    private void ApplyFieldOfView(float fovDegrees)
    {
        if (fovDegrees <= FovUnspecified) return;

        if (SelfCamera() is { } cam) { cam.FieldOfView = fovDegrees; }
    }

    /// <summary>
    /// 自分に付いている Camera（未付与なら null）。
    ///
    /// フィールドへキャッシュせず毎回取得する。スクリプトのホットリロードや
    /// アクター構成の差し替えに追従させるためで、同じ方針を CameraMove も採っている
    /// （キャッシュしても生存確認に同じだけの FFI 呼び出しが要るため、実コストは変わらない）。
    /// </summary>
    /// <returns>自分の Camera。付いていなければ null。</returns>
    private SEED.Camera? SelfCamera()
    {
        if (gameObject.GetComponent<SEED.Camera>() is not { } cam || !cam.IsValid) return null;
        return cam;
    }
}
