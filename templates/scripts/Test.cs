using SEEDEditor.Scripting;   // SEEDScript・[SerializeField] など

/// <summary>NewScript スクリプト。</summary>
public class Test : SEEDScript
{
    // インスペクタに公開するフィールドは [SerializeField] を付ける
    // [SerializeField(Label = "速度")]
    // private float speed = 1.0f;
    
    // 使える API 例:
    //   transform.Position / .Rotation / .Scale  … 自分の GameObject の Transform（get/set）
    //   Time.DeltaTime                            … 前フレームからの経過秒
    //   Mathf.Lerp / Vector3 / Random / Debug.Log … 数学・乱数・ログ
    // 詳細は docs/scripting_api.md を参照。
    
    [SerializeField] private int num;
    
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
        // ランダムな数を足していく
        num++;
        
        // 数値を出力
        SEED.Debug.Log(num);
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