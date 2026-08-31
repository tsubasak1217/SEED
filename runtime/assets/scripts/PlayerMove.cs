using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>NewScript スクリプト。</summary>
public class PlayerMove : SEEDScript
{
    // インスペクタに公開するフィールドは [SerializeField] を付ける
    // [SerializeField(Label = "速度")]
    // private float speed = 1.0f;
    
    // ゲーム向けエンジン API（Mathf/Vector3/Time/Random/Debug/GameObject など）は
    // SEED 名前空間にあります。System と型名が衝突する（例: Random ↔ System.Random）ため、
    // エンジン側からは using を付けていません。「SEED.」で修飾して呼び出してください。
    //   例) num += SEED.Random.Range(0, 10);
    //       transform.Position += SEED.Vector3.Right * SEED.Time.DeltaTime;
    // ※ どうしても無修飾で書きたい場合は自分で「using SEED;」を足せます（衝突解決は自己責任）。
    //
    // 使える API 例:
    //   transform.Position / .Rotation / .Scale        … 自分の GameObject の Transform（get/set）
    //   ctx.DeltaTime                                  … 前フレームからの経過秒
    //   SEED.Mathf.Lerp / SEED.Vector3 / SEED.Random / SEED.Debug.Log … 数学・乱数・ログ
    // 詳細は docs/scripting_api.md を参照。
    
    [Header("移動パラメータ"), SerializeField]
    private float moveSpeed = 1.0f;
    
    [SerializeField]
    private SEED.Transform? cameraTransform = null;
    
    /// <summary>フレーム開始時に呼ばれる。入力取得や状態リセット向け。</summary>
    public override void BeginFrame(ref NativeFrameContext ctx)
    {
        // ctx.DeltaTime : 前フレームからの経過秒
        // ctx.AnimTime  : ゲーム内累計時間
    }
    
    /// <summary>Update より前の更新。他スクリプトへ渡す事前計算向け。</summary>
    public override void EarlyUpdate(ref NativeFrameContext ctx)
    {
    }
    
    /// <summary>毎フレーム呼ばれる主更新処理。ゲームロジックの中心。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (cameraTransform is not { } cam) { return; }
        if (gameObject.GetComponent<SEED.InputMap>() is not { } im) { return; }
        
        var input = im.GetVector2("Move");
        var upDown = im.GetAxis("UpDown");
        
        // Vector3 は不変構造体なので、Y を捨てた新しいベクトルを作る
        var fwd = new SEED.Vector3(cam.Forward.x, 0f, cam.Forward.z);
        var right = new SEED.Vector3(cam.Right.x, 0f, cam.Right.z);
        
        // 真下を向いたカメラ等の縮退ガード（SqrMagnitude はプロパティ）
        if (fwd.SqrMagnitude < 1e-6f) { return; }
        fwd = fwd.Normalized;    // Normalized もプロパティ（()なし）
        right = right.Normalized;
        
        var move = right * input.x + fwd * input.y;
        if (move.SqrMagnitude > 1f)
        {
            move = move.Normalized;
        }
        
        transform.Position += move * moveSpeed * ctx.DeltaTime;
        transform.Position += new SEED.Vector3(0,upDown * moveSpeed * ctx.DeltaTime,0);
        
        // 進行方向を向く（EulerAngles プロパティで Transform.Rotation(オイラー度) へ）
        if (move.SqrMagnitude > 1e-6f)
        {
            transform.Rotation = SEED.Quaternion.LookRotation(move).EulerAngles;
        }
    
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