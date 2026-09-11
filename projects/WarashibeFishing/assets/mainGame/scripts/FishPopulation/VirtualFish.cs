/// <summary>
/// 実体（アクタ）を持たない「仮想の魚」1 匹ぶんの状態
/// 【1 個体の座標・向き・素性を保持する唯一の器】。
///
/// [なぜ必要か]
/// 海の魚を全レベルぶん実体化すると（10 レベル × 維持数）数百体のアクタが
/// 常時 ECS・描画・スクリプト更新に乗ってしまい、フレーム時間を食い潰す。
/// しかしレーダーには「どこに何レベルの魚が居るか」だけが要るので、
/// <b>座標・向き・魚種・レベルだけを持つ軽量なレコード</b>を代わりに泳がせ、
/// プレイヤーの関心の内側に入った個体だけをアクタとして実体化する。
///
/// [責務]
/// このクラスは<b>1 匹ぶんの状態と、その漂い（ランダムウォーク）</b>だけを持つ。
/// 「何匹維持するか」は <see cref="VirtualFishPool"/>、
/// 「どれを実体化するか」は <see cref="FishMaterializationPolicy"/>、
/// 実際の生成・破棄は <see cref="FishManager"/> の責務（単一責任原則）。
///
/// [実体との対応]
/// 実体化中は <see cref="Actor"/> にアクタのハンドルを持ち、<see cref="Materialized"/>
/// が true になる。仮想へ戻すときは実体の座標を <see cref="Position"/> へ書き戻すので、
/// 実体化／仮想化を往復しても魚の居場所は連続する（瞬間移動して見えない）。
/// </summary>
public sealed class VirtualFish
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>1 回転（ラジアン）。向きの正規化に使う。</summary>
    private const float FullTurnRadians = 2f * 3.14159265f;

    /// <summary>レベル添字（0 始まり）をレベル番号（1 始まり）へ直す差分。</summary>
    private const int LevelNumberToIndex = 1;

    // ─── 素性（生成時に決まり、以後変わらない）─────────────────

    /// <summary>この個体が属するレベルの添字（0 始まり＝<c>FishManager.levels</c> の添字）。</summary>
    public int LevelIndex { get; }

    /// <summary>この個体のレベル番号（1 始まり）。レーダーの格上判定などに使う。</summary>
    public int Level => LevelIndex + LevelNumberToIndex;

    /// <summary>
    /// 実体化するときに使う .actor パス。
    /// <b>仮想の時点で確定させる</b>ので、実体化・仮想化を往復しても魚種が入れ替わらない。
    /// </summary>
    public string PrefabPath { get; }

    /// <summary>
    /// 台本（チュートリアル）が明示的に出した個体か。
    /// true の個体は<b>常に実体化</b>され、維持数の勘定にも入らない
    /// （「必ずここに 1 匹居る」ことを保証するための特別枠）。
    /// </summary>
    public bool Pinned { get; }

    // ─── 状態（毎回の更新で変わる）─────────────────────────────

    /// <summary>
    /// ワールド座標【この個体の位置の唯一の真実】。
    /// 仮想のあいだは <see cref="Drift"/> が進め、実体化中は実体の Transform から書き戻す。
    /// </summary>
    public SEED.Vector3 Position { get; set; }

    /// <summary>進行方向（ラジアン。エンジン規約 yaw = atan2(x, z)、前方 +Z）。</summary>
    public float HeadingRad { get; set; }

    /// <summary>実体化中のアクタ（未実体化なら無効ハンドル）。</summary>
    public SEED.GameObject Actor { get; private set; }

    /// <summary>いま実体（アクタ）を持っているか。</summary>
    public bool Materialized { get; private set; }

    /// <summary>次に進行方向を変えるまでの残り秒数（ランダムウォークの間隔）。</summary>
    private float turnTimer;

    /// <summary>
    /// 仮想の魚を 1 匹作る。
    /// </summary>
    /// <param name="levelIndex">レベルの添字（0 始まり）。</param>
    /// <param name="prefabPath">実体化に使う .actor パス。</param>
    /// <param name="position">初期のワールド座標。</param>
    /// <param name="headingRad">初期の進行方向（ラジアン）。</param>
    /// <param name="pinned">台本が出した個体（常に実体化する）なら true。</param>
    public VirtualFish(int levelIndex, string prefabPath, SEED.Vector3 position, float headingRad, bool pinned)
    {
        LevelIndex  = levelIndex;
        PrefabPath  = prefabPath;
        Position    = position;
        HeadingRad  = headingRad;
        Pinned      = pinned;
        Actor       = default;
        Materialized = false;
        turnTimer   = 0f;
    }

    /// <summary>
    /// 実体（アクタ）と結びつける【実体化の記録の唯一の入口】。
    /// 実際の <c>Instantiate</c> は <see cref="FishManager"/> が行い、その結果をここへ預ける。
    /// </summary>
    /// <param name="actor">生成済みの魚アクタ。</param>
    public void AttachActor(SEED.GameObject actor)
    {
        Actor = actor;
        Materialized = true;
    }

    /// <summary>
    /// 実体との結びつきを外す【仮想へ戻す記録の唯一の出口】。
    /// アクタの破棄そのものは呼び出し側（<see cref="FishManager"/>）が行う。
    /// </summary>
    public void DetachActor()
    {
        Actor = default;
        Materialized = false;
    }

    /// <summary>
    /// 仮想のまま「ゆっくり漂う」1 ステップ分の更新
    /// 【仮想個体の移動の唯一の実装】。
    ///
    /// 実体の魚（<see cref="Fish"/>）のような餌への反応・回遊半径の管理は行わない。
    /// レーダーの点が生きているように見えれば十分なので、
    /// <b>一定間隔で進行方向を少し振り、その向きへ一定速度で進む</b>だけにしている
    /// （数フレームに 1 回しか呼ばれない前提の、極めて軽い処理）。
    /// </summary>
    /// <param name="deltaTime">前回の更新からの経過秒数。</param>
    /// <param name="random">方向転換の乱数源（プール全体で共有する）。</param>
    /// <param name="speed">遊泳速度（m/秒）。</param>
    /// <param name="turnIntervalSeconds">進行方向を振り直す間隔（秒）。</param>
    /// <param name="turnJitterRadians">1 回の振り直しで変える角度の最大値（ラジアン）。</param>
    public void Drift(float deltaTime, System.Random random, float speed, float turnIntervalSeconds, float turnJitterRadians)
    {
        if (deltaTime <= 0f) { return; }

        // 一定間隔で進行方向を少しだけ振る（急旋回させないので魚らしい軌跡になる）
        turnTimer -= deltaTime;
        if (turnTimer <= 0f)
        {
            float jitter = ((float)random.NextDouble() * 2f - 1f) * turnJitterRadians;
            HeadingRad = NormalizeAngle(HeadingRad + jitter);
            turnTimer = turnIntervalSeconds;
        }

        // 進行方向へ等速で進む（高さは変えない＝水面高さのまま）
        Position = new SEED.Vector3(
            Position.x + SEED.Mathf.Sin(HeadingRad) * speed * deltaTime,
            Position.y,
            Position.z + SEED.Mathf.Cos(HeadingRad) * speed * deltaTime);
    }

    /// <summary>角度を [0, 2π) に丸める（浮動小数の累積で値が発散しないようにする）。</summary>
    /// <param name="angleRad">丸める角度（ラジアン）。</param>
    /// <returns>[0, 2π) に収めた角度。</returns>
    private static float NormalizeAngle(float angleRad)
    {
        while (angleRad < 0f) { angleRad += FullTurnRadians; }
        while (angleRad >= FullTurnRadians) { angleRad -= FullTurnRadians; }
        return angleRad;
    }
}
