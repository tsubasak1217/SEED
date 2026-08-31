using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>NewScript スクリプト。</summary>
public class FollowCamera : SEEDScript
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
    //   gameObject.GetComponent<SEED.Camera>()         … 他コンポーネント取得（T?。未アタッチは null）
    //       例) if (gameObject.GetComponent<SEED.InputMap>() is { } input) { input.GetAction("Jump"); }
    //   ctx.DeltaTime                                  … 前フレームからの経過秒
    //   SEED.Mathf.Lerp / SEED.Vector3 / SEED.Random / SEED.Debug.Log … 数学・乱数・ログ
    // 詳細は docs/scripting_api.md を参照。
    
    [Header("追従対象トランスフォーム"), SerializeField]
    private SEED.Transform? target_ = null;
    
    [Header("目標からの距離"), SerializeField]
    private float distancefromTarget_ = 10.0f;
 
    [Header("目標地点のオフセット"), SerializeField]
    private SEED.Vector3 targetOffset_ = new SEED.Vector3(0,3,0);
 
    [Header("初期pitch/yaw"), SerializeField]
    private float pitch_ = 45.0f;
    [SerializeField]
    private float yaw_ = 90.0f;
    
    [Header("回転速度(秒あたり度)"), SerializeField]
    public float rotationSpeed_ = 45.0f;
    
    // 目標のセッター
    public void SetTarget(SEED.Transform target)
    {
        target_ = target;
    }
    
    // 
    void UpdateTransform()
    {
        if (target_ != null)
        {
            var v = SEED.Vector3.FromPitchYaw(pitch_, yaw_);
            transform.Position = target_.Value.Position + (v * distancefromTarget_) + targetOffset_;
            transform.Rotation = SEED.Quaternion.LookRotation(-v).EulerAngles;
        }
    }
    
    // 開始時関数
    public override void OnStart()
    {
        UpdateTransform();
    }
    
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
    }
    
    /// <summary>固定タイムステップの更新。物理など時間刻みを一定にしたい処理向け。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
        if (gameObject.GetComponent<SEED.InputMap>() is { } im)
        {
            var input = im.GetVector2("CameraMove") * rotationSpeed_ * ctx.DeltaTime;
            
            // 球面座表情を移動
            yaw_ += input.x;
            pitch_ += input.y;
            SEED.Mathf.Clamp(ref pitch_, -80.0f, 80.0f);
            UpdateTransform();
        }
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