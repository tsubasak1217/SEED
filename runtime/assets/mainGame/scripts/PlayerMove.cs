using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// プレイヤーの移動＋移動状態に応じたアニメーション切替。
/// - カメラ基準の平面移動（InputMap "Move" / "UpDown"）
/// - 動いている間は走りアニメ、止まると待機アニメへクロスフェードで切り替える
/// </summary>
public class PlayerMove : SEEDScript
{
    // ゲーム向けエンジン API（Mathf/Vector3/Time/Random/Debug/GameObject など）は
    // SEED 名前空間にあります。System と型名が衝突する（例: Random ↔ System.Random）ため、
    // エンジン側からは using を付けていません。「SEED.」で修飾して呼び出してください。
    // 詳細は docs/scripting_api.md を参照。

    [Header("移動パラメータ"), SerializeField]
    private float moveSpeed = 1.0f;

    [SerializeField]
    private SEED.Transform? cameraTransform = null;

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

    /// <summary>現在再生を要求しているのが走りか（true=Running / false=Idle / null=未初期化）。</summary>
    private bool? isRunning = null;

    /// <summary>フレーム開始時に呼ばれる。入力取得や状態リセット向け。</summary>
    public override void BeginFrame(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update より前の更新。他スクリプトへ渡す事前計算向け。</summary>
    public override void EarlyUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>毎フレーム呼ばれる主更新処理。移動→アニメ状態の反映、の順で処理する。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 移動処理。カメラや入力が無い場合は「動いていない」扱いで待機アニメに落とす。
        bool moving = UpdateMovement(ref ctx);

        // 移動状態が変わったフレームだけアニメを切り替える（毎フレーム Play すると先頭に戻り続けるため）
        UpdateAnimation(moving);
    }

    /// <summary>
    /// カメラ基準の移動を行い、このフレームに移動入力があったかを返す。
    /// </summary>
    private bool UpdateMovement(ref NativeFrameContext ctx)
    {
        if (cameraTransform is not { } cam) { return false; }
        if (gameObject.GetComponent<SEED.InputMap>() is not { } im) { return false; }

        var input = im.GetVector2("Move");
        var upDown = im.GetAxis("UpDown");

        // Vector3 は不変構造体なので、Y を捨てた新しいベクトルを作る
        var fwd = new SEED.Vector3(cam.Forward.x, 0f, cam.Forward.z);
        var right = new SEED.Vector3(cam.Right.x, 0f, cam.Right.z);

        // 真下を向いたカメラ等の縮退ガード（SqrMagnitude はプロパティ）
        if (fwd.SqrMagnitude < 1e-6f) { return false; }
        fwd = fwd.Normalized;    // Normalized もプロパティ（()なし）
        right = right.Normalized;

        var move = right * input.x + fwd * input.y;
        if (move.SqrMagnitude > 1f)
        {
            move = move.Normalized;
        }

        transform.Position += move * moveSpeed * ctx.DeltaTime;
        transform.Position += new SEED.Vector3(0, upDown * moveSpeed * ctx.DeltaTime, 0);

        // 進行方向を向く（EulerAngles プロパティで Transform.Rotation(オイラー度) へ）
        if (move.SqrMagnitude > 1e-6f)
        {
            transform.Rotation = SEED.Quaternion.LookRotation(move).EulerAngles;
        }

        // 平面移動か上下移動のどちらかがしきい値を超えていれば「動いている」
        float threshold = moveThreshold * moveThreshold;
        return move.SqrMagnitude > threshold || upDown * upDown > threshold;
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