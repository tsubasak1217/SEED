// ============================================================================
//  DialogueCameraDirector.cs
//  会話中のカメラ移動（カット／補間）だけを担当する。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// カメラ移動担当コンポーネント。カメラアクター（MainCamera）に付ける。
///
/// 【責務】
///  指定された Transform（空アクター）の位置・回転へ、カメラ自身の Transform を
///  「即座に（cut）」または「時間を掛けて（lerp）」寄せる。それだけを行う。
///  どの台詞でどこへ寄せるかは DialogueDirector が決める（単一責任）。
///
/// 【回転の補間について】
///  Transform.Rotation は YXZ オイラー角（度）で、単純な線形補間では
///  350 度 → 10 度 が逆回り（350→180→10）になってしまう。
///  そのため成分ごとに最短回りで補間する AngleMath.LerpEuler を使う。
///
/// 【シーン側の設定】
///  - MainCamera にこのスクリプトを付ける。
///  - DialogueDirector の「カメラ演出」参照にこのアクターを指定する。
/// </summary>
public class DialogueCameraDirector : SEEDScript
{
    // ── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>移動時間がこの値以下なら「即時（cut）」として扱う（0 除算回避）。</summary>
    private const float MinMoveDuration = 0.0001f;

    /// <summary>進捗の完了値。</summary>
    private const float ProgressComplete = 1f;

    // ── 内部状態 ────────────────────────────────────────────

    /// <summary>補間移動の最中か。</summary>
    private bool _moving;

    /// <summary>補間開始時のカメラ位置。</summary>
    private SEED.Vector3 _startPosition;

    /// <summary>補間開始時のカメラ回転（YXZ オイラー角・度）。</summary>
    private SEED.Vector3 _startRotation;

    /// <summary>
    /// 補間先の位置。
    /// 目標 Transform は開始時に一度だけサンプリングする
    /// （移動中に目標アクターが動いても演出が破綻しないようにするため）。
    /// </summary>
    private SEED.Vector3 _endPosition;

    /// <summary>補間先の回転（YXZ オイラー角・度）。</summary>
    private SEED.Vector3 _endRotation;

    /// <summary>補間に掛ける秒数。</summary>
    private float _duration;

    /// <summary>補間開始からの経過秒。</summary>
    private float _elapsed;

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

        _elapsed += ctx.DeltaTime;

        // 生の進捗 → イージング済みの進捗
        float rawProgress = SEED.Mathf.Clamped01(_elapsed / _duration);
        float eased       = AngleMath.SmoothStep01(rawProgress);

        transform.Position = SEED.Vector3.Lerp(_startPosition, _endPosition, eased);
        transform.Rotation = AngleMath.LerpEuler(_startRotation, _endRotation, eased);

        // 完了したら目標値へぴたりと合わせて終了する（誤差の蓄積を残さない）
        if (rawProgress >= ProgressComplete)
        {
            ApplyImmediately(_endPosition, _endRotation);
            _moving = false;
        }
    }

    // ── 公開メソッド ────────────────────────────────────────

    /// <summary>
    /// 指定した Transform の姿勢へカメラを移動させる。
    /// </summary>
    /// <param name="target">目標の空アクターの Transform。無効なら何もしない。</param>
    /// <param name="mode">移動方法（DialogueCameraMode.Cut / Lerp）。</param>
    /// <param name="duration">lerp のときの移動時間（秒）。0 以下なら cut と同じ。</param>
    public void MoveTo(SEED.Transform target, string mode, float duration)
    {
        // 目標未設定（IsValid == false）ならカメラを動かさない
        if (!target.IsValid) return;

        // 目標姿勢はここで 1 回だけ読む
        var endPosition = target.Position;
        var endRotation = target.Rotation;

        // 未知の文字列は cut に倒す（データの打ち間違いで会話が止まらないように）
        string normalized = DialogueCameraMode.Normalize(mode);
        if (normalized != DialogueCameraMode.Lerp || duration <= MinMoveDuration)
        {
            ApplyImmediately(endPosition, endRotation);
            _moving = false;
            return;
        }

        _startPosition = transform.Position;
        _startRotation = transform.Rotation;
        _endPosition   = endPosition;
        _endRotation   = endRotation;
        _duration      = duration;
        _elapsed       = 0f;
        _moving        = true;
    }

    /// <summary>
    /// 進行中の補間を打ち切り、目標姿勢へ即座に合わせる。
    /// </summary>
    public void SnapToTarget()
    {
        if (!_moving) return;
        ApplyImmediately(_endPosition, _endRotation);
        _moving = false;
    }

    // ── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// カメラの姿勢を即座に設定する。
    /// </summary>
    /// <param name="position">設定する位置。</param>
    /// <param name="rotation">設定する回転（YXZ オイラー角・度）。</param>
    private void ApplyImmediately(SEED.Vector3 position, SEED.Vector3 rotation)
    {
        transform.Position = position;
        transform.Rotation = rotation;
    }
}
