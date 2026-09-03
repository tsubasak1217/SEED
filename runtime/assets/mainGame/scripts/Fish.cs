using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 魚 1 匹の共通パラメータと簡易遊泳（釣り仕様 2026-09-03 準拠）。
///
/// [共通パラメータ]（仕様書どおり）
/// - 大きさ / スタミナ / 基礎パワー / 餌の感知距離 / 好みの魚 / 暴れ度（規定 1）
/// [戦闘力] = 基礎パワー × 大きさスコア × 暴れ度（<see cref="CombatPower"/>）
///
/// 泳ぎは「生成地点の周りを気ままに回遊する」最小実装:
/// 数秒ごとにランダムに向きを変え、行動半径から出そうになったら中心へ向き直す。
/// 魚ごとの固有の泳ぎ方・暴れ方は、このスクリプトを継承 or 差し替えて拡張する想定。
/// </summary>
public class Fish : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>度→ラジアン変換係数。</summary>
    private const float DegToRad = 3.14159265f / 180f;

    /// <summary>1 回転（ラジアン）。ランダム方位の生成に使う。</summary>
    private const float FullTurnRadians = 2f * 3.14159265f;

    // ─── 共通パラメータ（釣り仕様）───────────────────────────

    /// <summary>大きさ。戦闘力の「大きさスコア」の元になる値（1 で標準）。</summary>
    [Header("釣りパラメータ"), SerializeField(Label = "大きさ")]
    private float size = 1f;

    /// <summary>スタミナ。釣りバトル中に魚が暴れ続けられる体力。</summary>
    [SerializeField(Label = "スタミナ")]
    private float stamina = 10f;

    /// <summary>基礎パワー。竿パワーと同じ単位で比較される戦闘力の基礎値。</summary>
    [SerializeField(Label = "基礎パワー")]
    private float basePower = 10f;

    /// <summary>餌の感知距離（メートル）。この距離まで餌（浮き）に気づく。</summary>
    [SerializeField(Label = "餌の感知距離")]
    private float baitSenseDistance = 5f;

    /// <summary>
    /// 好みの魚（餌として好む魚の名前。空 = 特になし）。
    /// わらしべ要素: 釣った魚を餌にすると好みの魚が釣れやすくなる想定。
    /// </summary>
    [SerializeField(Label = "好みの魚")]
    private string preferredFish = "";

    /// <summary>
    /// 暴れ度の規定値（仕様では規定 1）。釣りバトル中は状態に応じて
    /// 「暴れると 1.5 / ひるむと 0.2」のように変動する（変動は釣りバトル側の実装）。
    /// </summary>
    [SerializeField(Label = "暴れ度(規定1)")]
    private float rampage = 1f;

    // ─── 泳ぎ方（簡易回遊）───────────────────────────────────

    /// <summary>泳ぐ速さ（m/s）。</summary>
    [Header("泳ぎ方"), SerializeField(Label = "泳ぐ速さ(m/s)")]
    private float swimSpeed = 1.5f;

    /// <summary>生成地点からの行動半径（メートル）。出そうになると中心へ戻る。</summary>
    [SerializeField(Label = "行動半径(m)")]
    private float wanderRadius = 6f;

    /// <summary>向きを変える間隔（秒）。この間隔ごとにランダムな方位へ転回する。</summary>
    [SerializeField(Label = "転回間隔(秒)")]
    private float turnIntervalSeconds = 3f;

    /// <summary>向きの補間の速さ（1/秒）。大きいほど機敏に曲がる。</summary>
    [SerializeField(Label = "旋回の速さ")]
    private float turnLerpRate = 2f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>回遊の中心（生成地点）。null = 未初期化（最初の Update で現在地を採る）。</summary>
    private SEED.Vector3? homePosition = null;

    /// <summary>現在の目標方位（ラジアン、yaw = atan2(x, z) 規約）。</summary>
    private float targetHeadingRad = 0f;

    /// <summary>次の転回までの残り秒数。</summary>
    private float turnTimer = 0f;

    /// <summary>方位の乱数源。</summary>
    private readonly System.Random random = new();

    /// <summary>
    /// 戦闘力 = 基礎パワー × 大きさスコア × 暴れ度（仕様の算出式）。
    /// 大きさスコアは「大きさ 1 を標準に ±10% 程度」の緩い係数
    /// （size=0.5 → 0.95 / size=1 → 1.0 / size=2 → 1.1 のような線形）。
    /// </summary>
    /// <param name="currentRampage">現在の暴れ度（釣りバトル側が渡す。省略時は規定値）。</param>
    public float CombatPower(float? currentRampage = null)
    {
        // 大きさスコア: 1.0 + (大きさ - 1) * 0.1 を 0.9〜1.1 にクランプ
        float sizeScore = SEED.Mathf.Clamped(1f + (size - 1f) * 0.1f, 0.9f, 1.1f);
        return basePower * sizeScore * (currentRampage ?? rampage);
    }

    /// <summary>スタミナ（釣りバトル側から参照する）。</summary>
    public float Stamina => stamina;

    /// <summary>餌の感知距離（釣りバトル側から参照する）。</summary>
    public float BaitSenseDistance => baitSenseDistance;

    /// <summary>好みの魚の名前（わらしべ判定用）。</summary>
    public string PreferredFish => preferredFish;

    /// <summary>フレーム開始時に呼ばれる。</summary>
    public override void BeginFrame(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update より前の更新。</summary>
    public override void EarlyUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>毎フレームの回遊処理。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 初回: 生成地点を回遊の中心として記憶し、ランダムな方位で泳ぎ始める
        if (homePosition is not { } home)
        {
            home = transform.Position;
            homePosition = home;
            targetHeadingRad = (float)random.NextDouble() * FullTurnRadians;
            turnTimer = turnIntervalSeconds;
        }

        float dt = ctx.DeltaTime;
        if (dt <= 0f) { return; }

        // 一定間隔でランダムに転回する
        turnTimer -= dt;
        if (turnTimer <= 0f)
        {
            targetHeadingRad = (float)random.NextDouble() * FullTurnRadians;
            turnTimer = turnIntervalSeconds;
        }

        // 行動半径から出そうなら中心の方へ向き直す（境界で不自然に止まらない）
        var pos = transform.Position;
        float dx = pos.x - home.x;
        float dz = pos.z - home.z;
        if (dx * dx + dz * dz > wanderRadius * wanderRadius)
        {
            targetHeadingRad = SEED.Mathf.Atan2(-dx, -dz);
        }

        // 向きを目標方位へ指数補間し、その向きへ前進（高さは変えない）
        float currentYawRad = transform.Rotation.y * DegToRad;
        float delta = SEED.Mathf.Repeat(targetHeadingRad - currentYawRad + 3.14159265f, FullTurnRadians) - 3.14159265f;
        float newYawRad = currentYawRad + delta * SEED.Mathf.Clamped01(turnLerpRate * dt);

        transform.Rotation = new SEED.Vector3(0f, newYawRad / DegToRad, 0f);
        transform.Position = new SEED.Vector3(
            pos.x + SEED.Mathf.Sin(newYawRad) * swimSpeed * dt,
            pos.y,
            pos.z + SEED.Mathf.Cos(newYawRad) * swimSpeed * dt);
    }

    /// <summary>固定タイムステップの更新。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update 後の更新。</summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>描画フェーズ。</summary>
    public override void Render(ref NativeFrameContext ctx)
    {
    }

    /// <summary>フレーム終了時。</summary>
    public override void EndFrame(ref NativeFrameContext ctx)
    {
    }
}
