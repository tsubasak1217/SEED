using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 釣りのやり取り中に水面を漂う「漂流物」1 個ぶんの実装【漂流物の見た目と寿命の唯一の担当】。
///
/// <b>漂流物 prefab（<c>runtime/assets/mainGame/actors/Drift/*.actor</c>）の
/// Script スロットに付ける</b>。生成・配置・破棄の指示は <see cref="DriftItemManager"/>、
/// 「ウキが巻き込んだ」判定と効果の適用は <see cref="FishingController"/> が行う。
/// 本スクリプトの責務は次の 4 つだけ：
/// <list type="bullet">
///   <item>水面（<see cref="FishingController.WaterSurfaceY"/>）に浮かぶこと</item>
///   <item>生成時に決めた一定方向へゆっくり漂い、上下に揺れること</item>
///   <item>寿命が来たら自分で消えること</item>
///   <item>自分を <see cref="All"/> に登録し、巻き込み判定から見えるようにすること</item>
/// </list>
///
/// <b>種類（<see cref="kind"/>）は文字列で持つ</b>。インスペクタが enum を扱えないため、
/// prefab 側では <c>"stun"</c> / <c>"fish_recover"</c> / <c>"line_recover"</c> の
/// いずれかを文字列で指定し、値の正しさは <see cref="OnStart"/> で 1 度だけ検証する
/// （未知の種類なら警告を出し、効果を持たない漂流物として漂うだけになる）。
///
/// <b>効果量（<see cref="effectAmount"/>）の意味は種類ごとに違う</b>ので、
/// prefab 側でその種類にふさわしい値を入れること（<see cref="effectAmount"/> の説明を参照）。
/// </summary>
public class DriftItem : SEEDScript
{
    // ─── 種類を表す文字列（prefab の kind に入れる値の唯一の定義）───────────

    /// <summary>種類「スタン」＝ 隙（Rest）フェーズを小節単位で延長する。</summary>
    public const string KindStun = "stun";

    /// <summary>種類「魚 HP 回復」＝ 魚 HP を割合ぶん戻す（＝ウキが沖へ引き戻される）。</summary>
    public const string KindFishRecover = "fish_recover";

    /// <summary>種類「糸の回復」＝ 糸の残り（Line01）を割合ぶん戻す。</summary>
    public const string KindLineRecover = "line_recover";

    // ─── 定数（マジックナンバー禁止）─────────────────────────────

    /// <summary>2π。上下の揺れの周波数[Hz]を角速度[rad/s]へ直すのに使う。</summary>
    private const float TwoPi = 6.2831853f;

    /// <summary>1 回転（ラジアン）。漂う方位をランダムに選ぶときの範囲。</summary>
    private const float FullTurnRadians = TwoPi;

    /// <summary>上下の揺れの初期位相のばらつき範囲（ラジアン）。個体が一斉に揺れないようにする。</summary>
    private const float BobPhaseRandomMax = TwoPi;

    /// <summary>寿命の下限（秒）。0 以下を入れられても生成直後に消えないようにする番人値。</summary>
    private const float MinLifetimeSeconds = 0.1f;

    /// <summary>当たり半径の下限（メートル）。負値で判定が消えないようにする番人値。</summary>
    private const float MinHitRadius = 0f;

    // ─── 全個体の登録簿 ─────────────────────────────────────

    /// <summary>
    /// 生存している漂流物の全個体【巻き込み判定・一括破棄の唯一の入口】。
    ///
    /// 漂流物は prefab から動的生成されるため、参照フィールドでは集められない。
    /// 各個体が <see cref="OnStart"/> で自分を登録し、<see cref="OnDestroy"/> で外す
    /// （<see cref="Fish.All"/> と同じ方式）。
    /// <b>列挙中に <see cref="Kill"/> を呼ぶと集合が変化する</b>ので、
    /// 呼び出し側は必ず一時リストへ写してから回すこと。
    /// </summary>
    public static readonly HashSet<DriftItem> All = new();

    // ─── インスペクタ公開パラメータ ────────────────────────────

    /// <summary>
    /// 漂流物の種類。<see cref="KindStun"/> / <see cref="KindFishRecover"/> /
    /// <see cref="KindLineRecover"/> のいずれかを文字列で入れる
    /// （インスペクタが enum を扱えないため文字列。値は <see cref="OnStart"/> で検証する）。
    /// </summary>
    [Header("種類と効果"), SerializeField(Label = "種類(stun/fish_recover/line_recover)")]
    private string kind = KindStun;

    /// <summary>
    /// 効果量。<b>意味は種類ごとに違う</b>。
    /// <code>
    /// stun          … 隙（Rest）を延長する小節数（1 なら 1 小節）。小数は四捨五入して使う
    /// fish_recover  … 魚 HP 最大値に対する回復割合（0.3 なら最大値の 30%）
    /// line_recover  … 糸の残りへ足す割合（0.3 なら +0.3・上限 1）
    /// </code>
    /// </summary>
    [SerializeField(Label = "効果量")]
    private float effectAmount = 1f;

    /// <summary>
    /// 巻き込み判定の半径（メートル・水平距離）。
    /// ウキとの水平距離がこの値以下になったフレームで効果が発動する。
    /// </summary>
    [SerializeField(Label = "当たり半径(m)")]
    private float hitRadius = 0.8f;

    // ─── 漂い ───────────────────────────────────────────

    /// <summary>漂う速さ（m/秒）。方位は生成時に 1 度だけランダムに決まる。</summary>
    [Header("漂い"), SerializeField(Label = "漂う速さ(m/秒)")]
    private float driftSpeed = 0.3f;

    /// <summary>生存時間（秒）。これを過ぎたら自分で消える。</summary>
    [SerializeField(Label = "寿命(秒)")]
    private float lifetimeSeconds = 25f;

    /// <summary>上下の揺れ幅（メートル）。0 で揺れなし。</summary>
    [SerializeField(Label = "上下の揺れ幅(m)")]
    private float bobAmplitude = 0.06f;

    /// <summary>上下の揺れの周期（Hz）。1 秒あたりの往復回数。</summary>
    [SerializeField(Label = "上下の揺れ周期(Hz)")]
    private float bobFrequency = 0.6f;

    /// <summary>水面からの高さのオフセット（メートル）。モデルの原点が沈む場合の調整用。</summary>
    [SerializeField(Label = "水面からの高さ(m)")]
    private float waterHeightOffset = 0f;

    // ─── 効果音 ─────────────────────────────────────────

    /// <summary>
    /// 巻き込まれた瞬間に鳴らす効果音のアセットパス（空なら鳴らさない）。
    /// <b>種類ごとの音は prefab 側で差し替える</b>（データドリブンの原則どおり、
    /// 種類と音の対応をコード側に持たない）。
    /// </summary>
    [Header("効果音"), SerializeField(Label = "巻き込みの効果音")]
    private string hitSePath = "";

    /// <summary>巻き込み効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "巻き込みの音量")]
    private float hitSeVolume = 1f;

    // ─── 実行時の内部状態 ───────────────────────────────────

    /// <summary>生成からの経過秒数（寿命と上下の揺れに使う）。</summary>
    private float elapsed = 0f;

    /// <summary>漂う方位の X 成分（単位ベクトル・生成時に 1 度だけ決まる）。</summary>
    private float driftDirX = 0f;

    /// <summary>漂う方位の Z 成分（単位ベクトル・生成時に 1 度だけ決まる）。</summary>
    private float driftDirZ = 0f;

    /// <summary>上下の揺れの初期位相（ラジアン・個体ごとにばらつかせる）。</summary>
    private float bobPhase = 0f;

    // ─── 公開プロパティ（巻き込み判定・効果適用で読む値）──────────────

    /// <summary>このスクリプトが乗っているアクタ（<c>gameObject</c> は protected なので公開する）。</summary>
    public SEED.GameObject Actor => gameObject;

    /// <summary>漂流物の種類（<see cref="KindStun"/> などの文字列。未知の値なら効果は起きない）。</summary>
    public string Kind => kind;

    /// <summary>効果量（意味は <see cref="effectAmount"/> の説明を参照）。</summary>
    public float EffectAmount => effectAmount;

    /// <summary>巻き込み判定の半径（メートル・0 未満にはならない）。</summary>
    public float HitRadius => SEED.Mathf.Max(hitRadius, MinHitRadius);

    /// <summary>現在のワールド位置（巻き込み判定はこの XZ とウキの XZ の距離で行う）。</summary>
    public SEED.Vector3 Position => transform.IsValid ? transform.Position : SEED.Vector3.Zero;

    // ─── ライフサイクル ────────────────────────────────────

    /// <summary>
    /// 登録簿へ自分を載せ、漂う方位・揺れの位相をランダムに決める。
    /// 種類の文字列もここで 1 度だけ検証する（毎フレーム検証しないため）。
    /// </summary>
    public override void OnStart()
    {
        All.Add(this);

        float angle = SEED.Random.Range(0f, FullTurnRadians);
        driftDirX = SEED.Mathf.Sin(angle);
        driftDirZ = SEED.Mathf.Cos(angle);
        bobPhase = SEED.Random.Range(0f, BobPhaseRandomMax);

        if (!IsKnownKind(kind))
        {
            SEED.Debug.LogWarning($"[DriftItem] 未知の種類 \"{kind}\" です"
                                + $"（{KindStun} / {KindFishRecover} / {KindLineRecover} のいずれかを指定してください）。"
                                + "効果を持たない漂流物として漂います。");
        }
    }

    /// <summary>登録簿から自分を外す（破棄経路がどれでも必ずここを通る）。</summary>
    public override void OnDestroy()
    {
        All.Remove(this);
    }

    /// <summary>
    /// 毎フレームの更新。水面に浮いたまま一定方向へ漂い、寿命が来たら自分を消す。
    /// 水面の高さは <see cref="FishingController.Current"/> から読む
    /// （漂流物は動的生成されるため参照フィールドでコントローラを注入できない）。
    /// </summary>
    /// <param name="ctx">フレーム情報（経過秒数を読む）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        float dt = ctx.DeltaTime;
        if (dt <= 0f) { return; }

        elapsed += dt;

        // 寿命切れ: 自分で消える（登録簿からは OnDestroy で外れる）
        if (elapsed >= SEED.Mathf.Max(lifetimeSeconds, MinLifetimeSeconds))
        {
            Kill();
            return;
        }

        if (!transform.IsValid) { return; }

        // 水平方向: 生成時に決めた方位へ一定速度で流れる
        var position = transform.Position;
        float x = position.x + driftDirX * driftSpeed * dt;
        float z = position.z + driftDirZ * driftSpeed * dt;

        // 垂直方向: 水面の高さ ＋ オフセット ＋ 上下の揺れ
        float surface = FishingController.Current is { } controller
            ? controller.WaterSurfaceY()
            : position.y - waterHeightOffset;   // コントローラが居ないときは現在の高さを維持する
        float y = surface + waterHeightOffset
                + bobAmplitude * SEED.Mathf.Sin(elapsed * bobFrequency * TwoPi + bobPhase);

        transform.Position = new SEED.Vector3(x, y, z);
    }

    // ─── 公開 API（マネージャ・コントローラから呼ぶ）────────────────

    /// <summary>
    /// 巻き込み効果音を鳴らす（パス未設定なら何もしない）。
    /// <see cref="Kill"/> より前に呼ぶこと（破棄後はフィールドを読む意味が無くなるため）。
    /// </summary>
    public void PlayHitSe()
    {
        if (string.IsNullOrEmpty(hitSePath)) { return; }
        SEED.Audio.Play(hitSePath, SEED.Mathf.Clamped01(hitSeVolume));
    }

    /// <summary>
    /// この漂流物を消す【破棄要求の唯一の入口】。
    /// 実際の破棄はフレーム末尾（<see cref="OnDestroy"/> で登録簿から外れる）だが、
    /// 二重に効果が発動しないよう<b>登録簿からは即座に外す</b>。
    /// </summary>
    public void Kill()
    {
        All.Remove(this);
        gameObject.Destroy();
    }

    // ─── 内部処理 ─────────────────────────────────────────

    /// <summary>種類の文字列が既知のものか（未知なら効果を持たない）。</summary>
    /// <param name="value">検証する種類文字列。</param>
    private static bool IsKnownKind(string value)
        => value == KindStun || value == KindFishRecover || value == KindLineRecover;
}
