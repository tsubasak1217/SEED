using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// プレイヤーの<b>進行方向に応じて 2 つの目標（アンカー）を選び分け</b>、
/// 選んだ方へ位置と回転を<b>指数追従</b>させるカメラ。
///
/// <b>設計の要点: 方向判定はこのスクリプト内で完結する。</b>
/// SEED のスクリプト参照（[SerializeField]）で指せるのは GameObject と
/// コンポーネントハンドル（Transform / Camera など）だけで、<b>他スクリプトのインスタンスは
/// 参照できない</b>（scripting/src/Api/ScriptReference.cs）。
/// つまりカメラから PlayerMove へ「今どちら回りか」を問い合わせることはできない。
/// そこで<b>2 つの目標の中点を毎フレーム観測し、その位置変化（移動方向）から
/// 自前で方向を決める</b>（アンカーはプレイヤーの子なので中点＝プレイヤーの動き）。
/// 設定はアンカー 2 参照だけで済み、カメラ側 1 箇所で完結する。
///
/// <b>どちらのアンカーを選ぶか</b>:
/// カメラはプレイヤーの<b>後ろ</b>から見たい。よって
/// <c>dot(アンカー位置 - 中点, 移動方向) &lt; 0</c>（＝移動方向の後方にある）側を選ぶ。
/// アンカーはプレイヤーの子アクタとして前後に置く想定なので、
/// プレイヤーが逆走して向きが 180 度変われば前後関係も入れ替わり、選択も入れ替わる。
/// 両方が後方／両方が前方（＝判定が曖昧）なら<b>現状維持</b>する。
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

    /// <summary>
    /// 方向判定を行うのに必要な、プレイヤーの<b>累積移動距離</b>（メートル）。
    ///
    /// 1 フレームぶんの微小な移動（物理の押し戻し・数値誤差・アニメーションによる
    /// わずかな位置ブレ）で向きを決めると、停止中にアンカーがパタパタ入れ替わる。
    /// そこで移動デルタを累積し、この距離を超えたときだけ判定して累積をリセットする
    /// （＝距離ベースのヒステリシス）。ゆっくり歩いていてもいずれ超えるので、
    /// 「遅いと永久に切り替わらない」ことはない。
    /// </summary>
    private const float DirectionDecisionDistance = 0.05f;

    /// <summary>ベクトルの「長さがほぼ 0」を判定する二乗長のしきい値。</summary>
    private const float SqrEpsilon = 1e-6f;

    // ─── 追従目標（進行方向で切替）─────────────────────────────

    /// <summary>
    /// プレイヤーが<b>正方向</b>（例: 反時計回り）に進んでいるときに使う追従目標。
    /// プレイヤーの子アクタとして「進行方向の後ろ側」に置くのが基本。
    /// 片方だけ設定した場合は常にそちらを使う。
    /// </summary>
    [Header("追従目標（進行方向で切替）"), SerializeField(Label = "正方向時の目標")]
    private SEED.Transform? forwardTarget = null;

    /// <summary>
    /// プレイヤーが<b>逆方向</b>（例: 時計回り）に進んでいるときに使う追従目標。
    /// 未設定なら常に <see cref="forwardTarget"/> を使う。
    /// </summary>
    [SerializeField(Label = "逆方向時の目標")]
    private SEED.Transform? backwardTarget = null;

    // ─── 追従パラメータ ───────────────────────────────────────

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

    /// <summary>
    /// 現在選んでいるのが正方向側の目標か。
    /// 判定が曖昧・プレイヤーが止まっているあいだはこの値を維持する（＝ヒステリシス）。
    /// </summary>
    private bool useForwardTarget = true;

    /// <summary>
    /// 前フレームの<b>2 目標の中点</b>のワールド位置。null は「まだ観測していない」。
    /// 移動デルタを取るためだけに保持する。
    /// </summary>
    private SEED.Vector3? previousMidpoint = null;

    /// <summary>
    /// 最後に方向判定してからの<b>移動デルタの累積</b>（ワールド、メートル）。
    /// 長さが <see cref="DirectionDecisionDistance"/> を超えたら判定してリセットする。
    /// </summary>
    private SEED.Vector3 accumulatedMove = new SEED.Vector3(0f, 0f, 0f);

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
    /// Update 後の更新。<b>方向判定と追従はここで行う。</b>
    ///
    /// PlayerMove は Update フェーズでプレイヤーを動かし、向きも補間する。
    /// フェーズはスクリプト単位ではなく<b>フェーズ単位</b>で回る（全スクリプトの Update →
    /// 全スクリプトの LateUpdate）ので、ここで読めば必ず「今フレームの確定した位置・姿勢」が取れる。
    /// Update で読むと、スクリプトの実行順によって 1 フレーム遅れたり遅れなかったりする。
    /// </summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
        // 1) プレイヤーの移動を観測して、どちらの目標を使うか決める
        UpdateTargetSelection();

        // 2) 決まった目標へ追従する
        if (ResolveTarget() is not { } t) { return; }

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

    // ─── 方向判定 ─────────────────────────────────────────────

    /// <summary>
    /// <b>2 目標の中点</b>の位置変化を観測し、<see cref="useForwardTarget"/> を更新する。
    ///
    /// アンカー 2 つはプレイヤーの子アクタとして前後に置く想定なので、
    /// その<b>中点はプレイヤーと同じ動き</b>をする。つまり中点を見れば
    /// プレイヤー参照が無くても移動方向が分かり、設定は 2 参照だけで済む。
    ///
    /// <b>手順</b>:
    /// <code>
    ///   1. 前フレームからの中点の移動デルタを累積する
    ///   2. 累積が DirectionDecisionDistance 未満なら何もしない（＝ヒステリシス／停止中は現状維持）
    ///   3. 超えたら「移動方向の後方にあるアンカー」を選ぶ
    ///      （dot(アンカー位置 - 中点, 移動方向) &lt; 0 の側）
    ///   4. 後方が 1 つに定まらない（両方後方／両方前方）ときは現状維持
    ///   5. 判定したら累積をリセットする
    /// </code>
    ///
    /// 注意: プレイヤーの向き（Rotation）は使わない。プレイヤーは常に進行方向を向く仕様
    /// （PlayerMove が回転補間で向ける）なので向きからは正逆を区別できないため、
    /// <b>アンカーの相対位置と移動方向の関係</b>で判定する。
    /// </summary>
    private void UpdateTargetSelection()
    {
        // 判定に必要な 2 つの目標が揃っていなければ何もしない。
        // 片側しか無い構成では ResolveTarget が常にそちらを返すので、判定自体が不要。
        if (forwardTarget is not { } fwd || !fwd.IsValid) { return; }
        if (backwardTarget is not { } bwd || !bwd.IsValid) { return; }

        // 2 目標の中点 ≒ プレイヤー位置（アンカーがプレイヤーの子で前後対称に置かれている前提。
        // 対称でなくても「プレイヤーと一緒に動く点」であれば移動方向の観測には十分）。
        var current = (fwd.Position + bwd.Position) * 0.5f;

        // 初回は前フレームが無いのでデルタを取れない。基準だけ覚えて抜ける。
        if (previousMidpoint is not { } previous)
        {
            previousMidpoint = current;
            return;
        }
        previousMidpoint = current;

        // 1) 移動デルタを累積する（1 フレームぶんでは微小すぎて向きが定まらないため）
        accumulatedMove += current - previous;
        if (accumulatedMove.SqrMagnitude < DirectionDecisionDistance * DirectionDecisionDistance)
        {
            return;   // まだ判定に足る距離を動いていない（停止中もここで抜ける＝現状維持）
        }

        var direction = accumulatedMove;
        accumulatedMove = new SEED.Vector3(0f, 0f, 0f);   // 判定したので累積をリセット

        if (direction.SqrMagnitude < SqrEpsilon) { return; }   // 念のための縮退ガード

        // 3) 「移動方向の後方にあるか」を内積の符号で見る
        bool forwardIsBehind = SEED.Vector3.Dot(fwd.Position - current, direction) < 0f;
        bool backwardIsBehind = SEED.Vector3.Dot(bwd.Position - current, direction) < 0f;

        // 4) 後方が 1 つに定まったときだけ切り替える（曖昧なら現状維持）
        if (forwardIsBehind == backwardIsBehind) { return; }
        useForwardTarget = forwardIsBehind;
    }

    /// <summary>
    /// 現在使うべき追従目標を返す。有効な目標が 1 つも無ければ null。
    ///
    /// <b>フォールバック</b>: 選んだ側が未設定・無効ならもう片方を使う。
    /// 片側だけ設定した構成（切替なしの単純追従）でもそのまま動く。
    /// </summary>
    private SEED.Transform? ResolveTarget()
    {
        var primary = useForwardTarget ? forwardTarget : backwardTarget;
        if (primary is { } a && a.IsValid) { return a; }

        var fallback = useForwardTarget ? backwardTarget : forwardTarget;
        if (fallback is { } b && b.IsValid) { return b; }

        return null;
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
