using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 指定した Transform（追従目標）へ、位置と回転を<b>指数追従</b>させるカメラ。
///
/// 目標そのものを毎フレーム誰が動かすかは問わない設計にしてある。
/// 本ゲームでは <see cref="PlayerMove"/> が「周回方向に応じて選んだアンカーのポーズ」を
/// 追従目標へ書き込むので、<b>本スクリプトは追従目標を 1 つ見るだけでよい</b>。
///
/// <b>なぜカメラ側で「時計回り用／反時計回り用」を持たないのか</b>:
/// SEED のスクリプト参照（[SerializeField]）で指せるのは GameObject と
/// コンポーネントハンドル（Transform / Camera など）だけで、<b>他スクリプトのインスタンスは
/// 参照できない</b>（scripting/src/Api/ScriptReference.cs）。
/// つまりカメラから「プレイヤーが今どちら回りか」を問い合わせる手段が無い。
/// そこで「方向を知っている側（PlayerMove）が目標のポーズを書き、
/// カメラはその 1 点を追う」という一方向の依存にしてある。
///
/// 追従が<b>指数補間</b>なのは、目標が別アンカーへ瞬間的に切り替わっても
/// カメラが飛ばずに滑らかに回り込むため（切替の演出を別途書かなくてよい）。
/// </summary>
public class CameraMove : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>半回転（度）。最短回りの差分を求めるのに使う。</summary>
    private const float HalfTurnDegrees = 180f;

    /// <summary>1 回転（度）。角度を周期に畳み込むのに使う。</summary>
    private const float FullTurnDegrees = 360f;

    // ─── 追従パラメータ ───────────────────────────────────────

    /// <summary>
    /// 追従する目標。位置・回転ともにこの Transform のワールド値へ寄っていく。
    /// 未設定なら何もしない（カメラを手で置いたまま固定できる）。
    /// </summary>
    [Header("追従目標"), SerializeField(Label = "追従する Transform")]
    private SEED.Transform? target = null;

    /// <summary>
    /// 位置の追従の速さ（1/秒）。大きいほど目標に張り付き、小さいほど遅れて付いてくる。
    /// 0 で位置を追わなくなる。
    /// </summary>
    [Header("追従の速さ"), SerializeField(Label = "位置の追従率")]
    private float positionLerpRate = 6.0f;

    /// <summary>
    /// 回転の追従の速さ（1/秒）。位置と別にできるのは、
    /// 「素早く向きだけ合わせて位置はゆっくり」といった詰めができるようにするため。
    /// 0 で回転を追わなくなる。
    /// </summary>
    [SerializeField(Label = "回転の追従率")]
    private float rotationLerpRate = 6.0f;

    /// <summary>
    /// true なら最初のフレームだけ補間せず目標へ瞬間移動する。
    /// シーン開始直後にカメラが遠くから飛んでくるのを防ぐ。
    /// </summary>
    [SerializeField(Label = "開始時に目標へスナップ")]
    private bool snapOnStart = true;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>まだ一度も追従していないか（<see cref="snapOnStart"/> の判定に使う）。</summary>
    private bool isFirstFollow = true;

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

    /// <summary>
    /// Update 後の更新。<b>追従はここで行う。</b>
    ///
    /// PlayerMove は Update フェーズでプレイヤーを動かし、追従目標のポーズを書き込む。
    /// フェーズはスクリプト単位ではなく<b>フェーズ単位</b>で回る（全スクリプトの Update →
    /// 全スクリプトの LateUpdate）ので、ここで読めば必ず「今フレームの確定した目標」が取れる。
    /// Update で追うと、スクリプトの実行順によって 1 フレーム遅れたり遅れなかったりする。
    /// </summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
        if (target is not { } t || !t.IsValid) { return; }

        // 初回だけスナップ（開始位置から目標まで長距離を補間して飛んでくるのを防ぐ）
        if (isFirstFollow)
        {
            isFirstFollow = false;
            if (snapOnStart)
            {
                transform.Position = t.Position;
                transform.Rotation = t.Rotation;
                return;
            }
        }

        transform.Position = FollowPosition(transform.Position, t.Position, positionLerpRate, ctx.DeltaTime);
        transform.Rotation = FollowRotation(transform.Rotation, t.Rotation, rotationLerpRate, ctx.DeltaTime);
    }

    /// <summary>描画フェーズで呼ばれる。描画に関わる処理向け。</summary>
    public override void Render(ref NativeFrameContext ctx)
    {
    }

    /// <summary>フレーム終了時に呼ばれる。後片付けや状態確定向け。</summary>
    public override void EndFrame(ref NativeFrameContext ctx)
    {
    }

    // ─── 追従の実装 ───────────────────────────────────────────

    /// <summary>
    /// 位置を目標へ指数補間する。
    ///
    /// 補間率にそのまま deltaTime を掛けると、フレームレートが変わったときに
    /// 追従の速さが変わってしまう（60fps と 30fps で挙動が違う）。
    /// <c>1 - exp(-rate * dt)</c> にすると<b>フレームレートに依存しない</b>
    /// 同じ時定数の追従になる。
    /// </summary>
    /// <param name="current">現在位置。</param>
    /// <param name="goal">目標位置。</param>
    /// <param name="rate">追従率（1/秒）。0 以下なら追従しない。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private static SEED.Vector3 FollowPosition(SEED.Vector3 current, SEED.Vector3 goal, float rate, float deltaTime)
    {
        float k = ExponentialBlend(rate, deltaTime);
        if (k <= 0f) { return current; }
        return current + (goal - current) * k;
    }

    /// <summary>
    /// 回転（YXZ オイラー角・度）を目標へ<b>軸ごとに最短回りで</b>指数補間する。
    ///
    /// 359°→1° のような折り返しで遠回りしないよう、差分を -180〜+180 に畳み込んでから補間する。
    /// 軸ごとに独立して回すのは、カメラの姿勢が「ヨーとピッチ」で作られており、
    /// 軸をまたぐ補間（クォータニオン）を挟むと目標の見た目とずれるため。
    /// </summary>
    private static SEED.Vector3 FollowRotation(SEED.Vector3 current, SEED.Vector3 goal, float rate, float deltaTime)
    {
        float k = ExponentialBlend(rate, deltaTime);
        if (k <= 0f) { return current; }
        return new SEED.Vector3(
            current.x + ShortestAngleDelta(current.x, goal.x) * k,
            current.y + ShortestAngleDelta(current.y, goal.y) * k,
            current.z + ShortestAngleDelta(current.z, goal.z) * k);
    }

    /// <summary>
    /// フレームレート非依存の指数補間係数 <c>1 - exp(-rate * dt)</c>（0〜1）を返す。
    /// rate が 0 以下なら 0（＝補間しない）。
    /// </summary>
    private static float ExponentialBlend(float rate, float deltaTime)
    {
        if (rate <= 0f || deltaTime <= 0f) { return 0f; }
        return SEED.Mathf.Clamped01(1f - SEED.Mathf.Exp(-rate * deltaTime));
    }

    /// <summary>
    /// 角度 <paramref name="from"/> から <paramref name="to"/> への<b>最短回りの差分</b>（度、-180〜+180）。
    /// </summary>
    private static float ShortestAngleDelta(float from, float to)
        => SEED.Mathf.Repeat(to - from + HalfTurnDegrees, FullTurnDegrees) - HalfTurnDegrees;
}
