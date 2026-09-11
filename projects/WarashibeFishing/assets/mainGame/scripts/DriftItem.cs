using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 漂流物を<b>取得した瞬間</b>に出す演出の種類【取得演出の唯一の選択肢表】。
/// 実際の見た目は <see cref="DriftItem.PlayPickupEffect"/> が種類ごとに実装する。
/// </summary>
public enum DriftPickupEffect
{
    /// <summary>演出なし（効果音だけ鳴らす）。</summary>
    None,

    /// <summary>水しぶき（<see cref="WaterSplashSpawner"/> の波紋＋水柱）。</summary>
    Splash,

    /// <summary>星の弾け（中心から放射状に伸びて消える光の筋）。</summary>
    Sparkle,
}

/// <summary>
/// 釣りのやり取り中に水面を漂う「漂流物」1 個ぶんの実装【漂流物の見た目と寿命の唯一の担当】。
///
/// <b>漂流物 prefab（<c>runtime/assets/mainGame/actors/Drift/*.actor</c>）の
/// Script スロットに付ける</b>。生成・配置・破棄の指示は <see cref="DriftItemManager"/>、
/// 「ウキが巻き込んだ」判定と効果の適用は <see cref="FishingController"/> が行う。
/// 本スクリプトの責務は次の 5 つだけ：
/// <list type="bullet">
///   <item>水面（<see cref="FishingController.WaterSurfaceY"/>）に浮かぶこと</item>
///   <item>生成時に決めた一定方向へゆっくり漂い、上下に揺れ、くるくる回りながら少し傾くこと</item>
///   <item>生成直後は小さく現れ、消えるときは小さくしぼんで消えること</item>
///   <item>寿命が来たら（消滅演出のあと）自分で消えること</item>
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

    /// <summary>2π。上下の揺れの周波数[Hz]を角速度[rad/s]へ直す・角度をひと巡りさせるのに使う。</summary>
    private const float TwoPi = 6.2831853f;

    /// <summary>1 回転（ラジアン）。漂う方位をランダムに選ぶときの範囲。</summary>
    private const float FullTurnRadians = TwoPi;

    /// <summary>1 回転（度）。初期ヨー角をランダムに選ぶときの範囲。</summary>
    private const float FullTurnDegrees = 360f;

    /// <summary>上下の揺れの初期位相のばらつき範囲（ラジアン）。個体が一斉に揺れないようにする。</summary>
    private const float BobPhaseRandomMax = TwoPi;

    /// <summary>寿命の下限（秒）。0 以下を入れられても生成直後に消えないようにする番人値。</summary>
    private const float MinLifetimeSeconds = 0.1f;

    /// <summary>当たり半径の下限（メートル）。負値で判定が消えないようにする番人値。</summary>
    private const float MinHitRadius = 0f;

    /// <summary>星の弾けの筋の本数の下限（1 本は無いと演出にならない）。</summary>
    private const int MinSparkleRayCount = 1;

    /// <summary>星の弾けが始まる内半径の割合（外半径に対する比）。中心を少し空けて筋に見せる。</summary>
    private const float SparkleInnerRatio = 0.25f;

    /// <summary>不透明度の最大値（星の弾けの出だし）。</summary>
    private const float FullAlpha = 1f;

    /// <summary>時間の割り算で 0 除算を避けるための下限（秒）。</summary>
    private const float MinPositiveSeconds = 1e-4f;

    /// <summary>スケール（拡大率）の下限。0 未満のスケールを描画に渡さないための番人値。</summary>
    private const float MinScale = 0f;

    /// <summary>
    /// 回転方向くじの閾値。<see cref="SEED.Random.Value"/>（0〜1 未満）がこれ未満なら
    /// 反時計回り（-1）、以上なら時計回り（+1）とする、ちょうど五分五分のコイントス。
    /// </summary>
    private const float SpinDirectionCoinFlip = 0.5f;

    /// <summary>回転方向のうち反時計回り（Y 軸角速度の符号）。</summary>
    private const float SpinDirectionNegative = -1f;

    /// <summary>回転方向のうち時計回り（Y 軸角速度の符号）。</summary>
    private const float SpinDirectionPositive = 1f;

    /// <summary>
    /// Z 軸（ロール方向）の傾きに掛ける減衰係数。
    /// X 軸（ピッチ）と同じ振幅で傾けると機械的な「棒が転がる」ような動きに見えるため、
    /// ロール側は半分の振幅に抑えて上下の揺れに対する「おまけの傾き」に留める。
    /// </summary>
    private const float TiltRollDamping = 0.5f;

    // ─── 全個体の登録簿 ─────────────────────────────────────

    /// <summary>
    /// 生存している漂流物の全個体【巻き込み判定・一括破棄の唯一の入口】。
    ///
    /// 漂流物は prefab から動的生成されるため、参照フィールドでは集められない。
    /// 各個体が <see cref="OnStart"/> で自分を登録し、<see cref="OnDestroy"/> で外す
    /// （<see cref="Fish.All"/> と同じ方式）。
    /// <b>列挙中に <see cref="Kill"/> を呼ぶと集合が変化する</b>ので、
    /// 呼び出し側は必ず一時リストへ写してから回すこと。
    ///
    /// <b>消滅演出の開始と同時に外れる</b>（実際の破棄は演出後）ため、
    /// 演出中の個体は二重に巻き込み判定へ掛からない。
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
    private float hitRadius = 1.2f;

    // ─── 見た目（出現・消滅・回転）───────────────────────────

    /// <summary>
    /// 通常時（出現演出が終わったあと）の表示スケール。
    /// モデルの元の大きさが用途に合わないときの一括調整用（1.0 で等倍）。
    /// </summary>
    [Header("見た目（出現・消滅・回転）"), SerializeField(Label = "表示スケール")]
    private float modelScale = 0.7f;

    /// <summary>
    /// 出現演出の所要秒数。生成直後、スケール 0 からこの秒数かけて
    /// <see cref="modelScale"/> まで（<see cref="Easing.OutBack"/> で少し行き過ぎてから）拡大する。
    /// </summary>
    [SerializeField(Label = "出現の秒数")]
    private float appearSeconds = 0.35f;

    /// <summary>
    /// 消滅演出の所要秒数。<see cref="Kill"/> が呼ばれた瞬間のスケールから
    /// この秒数かけて（<see cref="Easing.InCubic"/> で加速しながら）0 まで縮め、破棄する。
    /// 0 以下を入れると演出を飛ばして即座に破棄する。
    /// </summary>
    [SerializeField(Label = "消滅の秒数")]
    private float disappearSeconds = 0.2f;

    /// <summary>
    /// Y 軸まわりの自転速度（度/秒）。回る向き（時計回り／反時計回り）は生成時にランダムで決まる。
    /// </summary>
    [SerializeField(Label = "回転速度(度/秒)")]
    private float spinDegPerSec = 25f;

    /// <summary>
    /// 上下の揺れに連動して X 軸・Z 軸へ傾く最大角度（度）。
    /// 0 にすると傾かず自転のみになる。
    /// </summary>
    [SerializeField(Label = "傾きの最大角(度)")]
    private float tiltDeg = 6f;

    // ─── 漂い ───────────────────────────────────────────

    /// <summary>漂う速さ（m/秒）。方位は生成時に 1 度だけランダムに決まる。</summary>
    [Header("漂い"), SerializeField(Label = "漂う速さ(m/秒)")]
    private float driftSpeed = 0.3f;

    /// <summary>生存時間（秒）。これを過ぎたら自分で消える（消滅演出を経て破棄される）。</summary>
    [SerializeField(Label = "寿命(秒)")]
    private float lifetimeSeconds = 25f;

    /// <summary>上下の揺れ幅（メートル）。0 で揺れなし。</summary>
    [SerializeField(Label = "上下の揺れ幅(m)")]
    private float bobAmplitude = 0.06f;

    /// <summary>上下の揺れの周期（Hz）。1 秒あたりの往復回数。傾きの周期にも連動する。</summary>
    [SerializeField(Label = "上下の揺れ周期(Hz)")]
    private float bobFrequency = 0.6f;

    /// <summary>水面からの高さのオフセット（メートル）。モデルの原点が沈む場合の調整用。</summary>
    [SerializeField(Label = "水面からの高さ(m)")]
    private float waterHeightOffset = 0f;

    // ─── 効果音 ─────────────────────────────────────────

    /// <summary>
    /// 巻き込まれた瞬間に鳴らす効果音の<b>音声辞書キー</b>（空なら鳴らさない）。
    /// 素材も音量もシーン上の音声辞書が持つので、ここはキーだけを持つ。
    ///
    /// <b>種類ごとの音は prefab 側で差し替える</b>（データドリブンの原則どおり、
    /// 種類と音の対応をコード側に持たない）。
    /// 既定は空＝どの種類にも共通で鳴る音は無い。種類別の音は
    /// <c>FishingController.ApplyDriftEffect</c>（種類 → 効果・音の対応表）が鳴らすので、
    /// ここへキーを入れるとその音と<b>重ねて</b>鳴ることに注意。
    /// </summary>
    [Header("効果音"), SerializeField(Label = "巻き込みの音（辞書キー）")]
    private string hitSeKey = "";

    // ─── 取得演出（拾われた瞬間だけ・寿命切れでは出ない）──────────

    /// <summary>
    /// <b>取得された瞬間</b>に鳴らす効果音の<b>音声辞書キー</b>（空なら鳴らさない）。
    /// <see cref="hitSeKey"/> と別に持つのは、種類ごとの「拾った音」を
    /// prefab 側で差し替えられるようにするため（データドリブンの原則）。
    /// 寿命切れの消滅では鳴らさない。
    /// </summary>
    [Header("取得演出"), SerializeField(Label = "取得の音（辞書キー）")]
    private string pickupSeKey = "";

    /// <summary>取得した瞬間に出す演出の種類。</summary>
    [SerializeField(Label = "取得の演出")]
    private DriftPickupEffect pickupEffect = DriftPickupEffect.Splash;

    /// <summary>
    /// 水しぶき演出（<see cref="DriftPickupEffect.Splash"/>）の規模（0〜1）。
    /// 0 で最小（波紋 2・水柱 1 個）、1 で最大。
    /// </summary>
    [SerializeField(Label = "水しぶきの規模(0〜1)")]
    private float splashIntensity01 = 0.35f;

    /// <summary>星の弾け（<see cref="DriftPickupEffect.Sparkle"/>）の筋の色（RGB）。</summary>
    [SerializeField(Label = "星の弾けの色(RGB)")]
    private SEED.Vector3 sparkleColor = new(1f, 0.95f, 0.45f);

    /// <summary>星の弾けの筋の本数（放射状に等間隔で並ぶ）。</summary>
    [SerializeField(Label = "星の弾けの本数")]
    private int sparkleRayCount = 6;

    /// <summary>星の弾けが最終的に広がる半径（メートル）。</summary>
    [SerializeField(Label = "星の弾けの半径(m)")]
    private float sparkleRadiusMeters = 0.7f;

    /// <summary>
    /// 星の弾けの表示秒数。<b>消滅演出（<see cref="disappearSeconds"/>）が終わると
    /// アクタごと破棄される</b>ので、実際に見えるのは短い方の秒数になる。
    /// </summary>
    [SerializeField(Label = "星の弾けの秒数")]
    private float sparkleSeconds = 0.3f;

    /// <summary>星の弾けの筋の太さ（画面ピクセル）。</summary>
    [SerializeField(Label = "星の弾けの太さ(px)")]
    private float sparkleThicknessPx = 3f;

    // ─── 実行時の内部状態 ───────────────────────────────────

    /// <summary>
    /// 生成からの経過秒数（出現演出・寿命・上下の揺れ・回転・傾きに使う）。
    /// 消滅演出が始まったあとも（回転・傾きを進め続けるため）加算し続ける。
    /// </summary>
    private float elapsed = 0f;

    /// <summary>漂う方位の X 成分（単位ベクトル・生成時に 1 度だけ決まる）。</summary>
    private float driftDirX = 0f;

    /// <summary>漂う方位の Z 成分（単位ベクトル・生成時に 1 度だけ決まる）。</summary>
    private float driftDirZ = 0f;

    /// <summary>上下の揺れの初期位相（ラジアン・個体ごとにばらつかせる）。傾きの位相にも使う。</summary>
    private float bobPhase = 0f;

    /// <summary>初期ヨー角（度・生成時に 1 度だけランダムに決まる）。自転はこの角度から進む。</summary>
    private float initialYawDeg = 0f;

    /// <summary>
    /// 自転の向き（<see cref="SpinDirectionPositive"/> か <see cref="SpinDirectionNegative"/>）。
    /// 生成時に 1 度だけランダムに決まり、個体ごとに回る向きをばらつかせる。
    /// </summary>
    private float spinDirection = SpinDirectionPositive;

    /// <summary>
    /// その場に留まるか（漂わず・寿命でも消えない）。
    /// <see cref="TutorialRules.DriftStationary"/> を<b>毎フレーム引き直す</b>。
    ///
    /// 【latch しない理由】
    /// 生成時に 1 度だけ決めると、(1) Instantiate と OnStart が同フレームとは限らないため
    /// 台本が置いた個体を留め損ねる、(2) 台本が終わっても留まったままになり、
    /// 拾われなかった個体が寿命でも消えず海に居座る、の 2 つの取りこぼしが起きる。
    /// 引き直せば「台本が終わった＝また漂って寿命で消える」まで自動的に戻る。
    ///
    /// なお回転・上下の揺れ・出現/消滅スケールは「留める」個体でも止めない
    /// （止めるのは水平方向の移動だけ）。
    /// </summary>
    private bool stationary = false;

    /// <summary>
    /// 消滅演出の最中か【<see cref="Kill"/> が呼ばれてから破棄されるまで true】。
    /// true の間は漂い・寿命判定・当たり判定（<see cref="All"/> 登録）を止め、
    /// スケールを縮めるだけの専用更新（<see cref="UpdateDying"/>）に切り替える。
    /// </summary>
    private bool isDying = false;

    /// <summary>消滅演出の開始からの経過秒数（<see cref="disappearSeconds"/> と比較する）。</summary>
    private float dyingElapsed = 0f;

    /// <summary>
    /// 取得演出（効果音・エフェクト）を既に出したか
    /// 【多重再生を防ぐ唯一の関門】。取得は 1 個につき 1 回しか起きない。
    /// </summary>
    private bool pickupPlayed = false;

    /// <summary>星の弾けを描いている最中か（<see cref="DriftPickupEffect.Sparkle"/> のときだけ true）。</summary>
    private bool sparkleActive = false;

    /// <summary>星の弾けの開始からの経過秒数。</summary>
    private float sparkleElapsed = 0f;

    /// <summary>
    /// 星の弾けの中心（取得した瞬間のワールド位置）。
    /// アクタ自身は消滅演出で縮んでいくので、位置は開始時に控えておく。
    /// </summary>
    private SEED.Vector3 sparkleCenter = SEED.Vector3.Zero;

    /// <summary>
    /// 消滅演出を開始した瞬間のスケール。ここから 0 へ向けて縮める
    /// （出現演出の途中で <see cref="Kill"/> が呼ばれても、その時点の大きさから自然に縮む）。
    /// </summary>
    private SEED.Vector3 dyingStartScale = SEED.Vector3.One;

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
    /// 登録簿へ自分を載せ、漂う方位・揺れの位相・初期ヨー・回転方向をランダムに決める。
    /// また出現演出のため、スケールを 0 から始める。
    /// 種類の文字列もここで 1 度だけ検証する（毎フレーム検証しないため）。
    /// </summary>
    public override void OnStart()
    {
        All.Add(this);

        // 台本が位置を決めて置いた個体は、その場に留める（漂うと一直線が崩れる）
        stationary = IsStationaryNow();

        float angle = SEED.Random.Range(0f, FullTurnRadians);
        driftDirX = SEED.Mathf.Sin(angle);
        driftDirZ = SEED.Mathf.Cos(angle);
        bobPhase = SEED.Random.Range(0f, BobPhaseRandomMax);

        // 初期ヨー・自転方向: 一斉に同じ向きを向いて同じ方向へ回ると人工的に見えるため、
        // 個体ごとにランダムへばらつかせる
        initialYawDeg = SEED.Random.Range(0f, FullTurnDegrees);
        spinDirection = SEED.Random.Value < SpinDirectionCoinFlip
            ? SpinDirectionNegative
            : SpinDirectionPositive;

        // 出現演出: スケール 0 から始め、Update で modelScale まで拡大していく
        if (transform.IsValid)
        {
            transform.Scale = new SEED.Vector3(MinScale, MinScale, MinScale);
        }

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
    /// 毎フレームの更新。
    ///
    /// 消滅演出中（<see cref="isDying"/>）は <see cref="UpdateDying"/> へ専用処理を委譲して
    /// 抜ける（<c>dt &lt;= 0</c> の早期 return より<b>前</b>に判定することで、
    /// デルタタイムが 0 のフレームでも消滅演出の完了・破棄判定が妨げられないようにする）。
    ///
    /// 通常時は水面に浮いたまま一定方向へ漂い、上下に揺れ、自転しながら少し傾き、
    /// 寿命が来たら消滅演出を開始する。水面の高さは <see cref="FishingController.Current"/>
    /// から読む（漂流物は動的生成されるため参照フィールドでコントローラを注入できない）。
    /// </summary>
    /// <param name="ctx">フレーム情報（経過秒数を読む）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        float dt = ctx.DeltaTime;

        // 星の弾けは「取得 → 消滅演出」の間ずっと描き続ける必要があるので、
        // 消滅中かどうかの分岐より前で進める
        UpdateSparkle(dt);

        if (isDying)
        {
            UpdateDying(dt);
            return;
        }

        if (dt <= 0f) { return; }

        elapsed += dt;

        // 「留める」指定は毎フレーム引き直す（stationary フィールドの説明を参照）
        stationary = IsStationaryNow();

        // 寿命切れ: 消滅演出を開始する（実際の破棄は演出後、登録簿からは即座に外れる）。
        // その場に留める個体は台本が拾わせる前提なので寿命では消さない。
        if (!stationary && elapsed >= SEED.Mathf.Max(lifetimeSeconds, MinLifetimeSeconds))
        {
            Kill();
            return;
        }

        if (!transform.IsValid) { return; }

        // 水平方向: 生成時に決めた方位へ一定速度で流れる（留める個体は動かさない）
        var position = transform.Position;
        float speed = stationary ? 0f : driftSpeed;
        float x = position.x + driftDirX * speed * dt;
        float z = position.z + driftDirZ * speed * dt;

        // 垂直方向: 水面の高さ ＋ オフセット ＋ 上下の揺れ
        float surface = FishingController.Current is { } controller
            ? controller.WaterSurfaceY()
            : position.y - waterHeightOffset;   // コントローラが居ないときは現在の高さを維持する
        float y = surface + waterHeightOffset
                + bobAmplitude * SEED.Mathf.Sin(elapsed * bobFrequency * TwoPi + bobPhase);

        transform.Position = new SEED.Vector3(x, y, z);

        // 出現演出: スケール 0 → modelScale（EaseOutBack で少し行き過ぎてから収まる）
        ApplyAppearScale();

        // 自転 ＋ 上下の揺れに連動した傾き（留める個体でも止めない）
        ApplySpinAndTilt();
    }

    // ─── 公開 API（マネージャ・コントローラから呼ぶ）────────────────

    /// <summary>
    /// 巻き込み効果音を鳴らす（＝取得の演出を出す）。
    /// <see cref="Kill()"/> より前に呼ぶこと（破棄後はフィールドを読む意味が無くなるため）。
    ///
    /// 巻き込み＝取得なので、中身は <see cref="Pickup"/>（取得演出の唯一の入口）へ委譲する。
    /// 呼び出し側の名前を変えずに演出を足せるようにしてある。
    /// </summary>
    public void PlayHitSe()
    {
        Pickup();
    }

    /// <summary>
    /// 取得された（ウキが巻き込んだ）ことをこの漂流物へ伝える
    /// 【取得演出の唯一の入口】。
    ///
    /// 効果音（<see cref="hitSeKey"/> ＋ <see cref="pickupSeKey"/>）を鳴らし、
    /// <see cref="pickupEffect"/> の演出を出す。<b>消滅そのものは行わない</b>
    /// （消すのは <see cref="Kill()"/> の役目）ので、拾った側は
    /// 「<see cref="Pickup"/> → <see cref="Kill()"/>」または
    /// 「<c>Kill(pickedUp: true)</c>」のどちらでも良い。
    ///
    /// 何度呼んでも演出は 1 回だけ（<see cref="pickupPlayed"/> で番をする）。
    /// </summary>
    public void Pickup()
    {
        if (pickupPlayed) { return; }
        pickupPlayed = true;

        PlaySe(hitSeKey);
        PlaySe(pickupSeKey);
        PlayPickupEffect();
    }

    /// <summary>
    /// この漂流物の消滅演出を開始する【破棄要求の唯一の入口】。
    ///
    /// 実際の <c>gameObject.Destroy()</c> は消滅演出（<see cref="disappearSeconds"/> 秒）の
    /// 完了後だが、二重に効果が発動しないよう<b>登録簿からは即座に外す</b>
    /// （呼び出した瞬間から巻き込み判定・寿命判定の対象外になる）。
    ///
    /// 既に消滅演出中であれば何もしない（多重呼び出しでも 1 回しか演出が走らない）。
    /// <see cref="disappearSeconds"/> が 0 以下なら演出を飛ばして即座に破棄する。
    /// </summary>
    public void Kill() => Kill(pickedUp: false);

    /// <summary>
    /// 消滅演出を開始する（取得によるものかどうかを指定できる版）
    /// 【消滅と取得を区別する唯一の分岐】。
    ///
    /// <paramref name="pickedUp"/> が true のときだけ取得演出
    /// （<see cref="Pickup"/>）を出す。寿命切れ・一括破棄では false のまま呼ばれるので、
    /// 拾っていないのに取得音が鳴ることはない。
    /// </summary>
    /// <param name="pickedUp">取得（巻き込み）による消滅なら true。</param>
    public void Kill(bool pickedUp)
    {
        if (pickedUp) { Pickup(); }

        if (isDying) { return; }   // 既に消滅中の多重呼び出しは無視する

        All.Remove(this);

        if (disappearSeconds <= 0f)
        {
            gameObject.Destroy();
            return;
        }

        isDying = true;
        dyingElapsed = 0f;

        // 演出開始時点の見た目のスケールを基準に縮める
        // （出現演出の途中で Kill された場合も、その時の大きさから自然に縮む）
        dyingStartScale = transform.IsValid
            ? transform.Scale
            : new SEED.Vector3(modelScale, modelScale, modelScale);
    }

    // ─── 内部処理 ─────────────────────────────────────────

    /// <summary>
    /// 効果音を 1 つ鳴らす（辞書キーが空なら何もしない）【効果音再生の唯一の実装】。
    /// 音量は音声辞書の既定値を使う（素材も音量も辞書 1 か所で差し替えられるようにするため）。
    /// </summary>
    /// <param name="key">効果音の音声辞書キー（例: "Drift/stun"）。</param>
    private static void PlaySe(string key)
    {
        if (string.IsNullOrEmpty(key)) { return; }
        SEED.Audio.PlayDict(key);
    }

    /// <summary>
    /// 取得演出（<see cref="pickupEffect"/>）を 1 回だけ出す
    /// 【演出の種類と実装の唯一の対応表】。
    /// </summary>
    private void PlayPickupEffect()
    {
        switch (pickupEffect)
        {
            case DriftPickupEffect.Splash:
                SpawnPickupSplash();
                break;

            case DriftPickupEffect.Sparkle:
                BeginSparkle();
                break;

            // None: 効果音だけで演出は出さない
            default:
                break;
        }
    }

    /// <summary>
    /// 水しぶき（波紋＋水柱）を水面へまく。
    /// 規模は <see cref="splashIntensity01"/>、水面の高さは
    /// <see cref="FishingController.WaterSurfaceY"/>（居なければ自分の高さ）を使う。
    /// </summary>
    private void SpawnPickupSplash()
    {
        SEED.Vector3 position = Position;
        float surfaceY = FishingController.Current is { } controller
            ? controller.WaterSurfaceY()
            : position.y;

        WaterSplashSpawner.Spawn(
            WaterSplashSettings.Default, position, surfaceY, SEED.Mathf.Clamped01(splashIntensity01));
    }

    /// <summary>星の弾けを開始する（中心位置を控えて経過をリセットするだけ）。</summary>
    private void BeginSparkle()
    {
        sparkleActive = true;
        sparkleElapsed = 0f;
        sparkleCenter = Position;
    }

    /// <summary>
    /// 星の弾けを 1 フレームぶん描く【星の弾けの唯一の描画点】。
    ///
    /// 中心から放射状に <see cref="sparkleRayCount"/> 本の筋を描き、
    /// 時間とともに外へ広がりながら薄くなる（<see cref="SEED.Draw3D"/> の
    /// イミディエイト描画なので、毎フレーム呼ばないと消える）。
    /// </summary>
    /// <param name="dt">このフレームのデルタタイム。</param>
    private void UpdateSparkle(float dt)
    {
        if (!sparkleActive) { return; }

        if (dt > 0f) { sparkleElapsed += dt; }

        float duration = SEED.Mathf.Max(sparkleSeconds, MinPositiveSeconds);
        float progress = SEED.Mathf.Clamped01(sparkleElapsed / duration);
        if (progress >= 1f)
        {
            sparkleActive = false;
            return;
        }

        // 外へ広がりながら薄くなる
        float outerRadius = SEED.Mathf.Max(sparkleRadiusMeters, 0f) * progress;
        float innerRadius = outerRadius * SparkleInnerRatio;
        var color = new SEED.Color(
            sparkleColor.x, sparkleColor.y, sparkleColor.z, FullAlpha - progress);

        int rays = SEED.Mathf.Max(sparkleRayCount, MinSparkleRayCount);
        float stepRadians = FullTurnRadians / rays;

        for (int i = 0; i < rays; i++)
        {
            float angle = stepRadians * i;
            float dirX = SEED.Mathf.Sin(angle);
            float dirZ = SEED.Mathf.Cos(angle);

            var from = new SEED.Vector3(
                sparkleCenter.x + dirX * innerRadius,
                sparkleCenter.y,
                sparkleCenter.z + dirZ * innerRadius);
            var to = new SEED.Vector3(
                sparkleCenter.x + dirX * outerRadius,
                sparkleCenter.y,
                sparkleCenter.z + dirZ * outerRadius);

            SEED.Draw3D.Line(from, to, color, thicknessPx: SEED.Mathf.Max(sparkleThicknessPx, 0f));
        }
    }

    /// <summary>
    /// 消滅演出専用の毎フレーム更新【<see cref="isDying"/> の間だけ呼ばれる】。
    ///
    /// 漂い・寿命判定・当たり判定は行わない（<see cref="Kill"/> の時点で登録簿から
    /// 外れているため当たり判定はそもそも対象外）。行うのは、
    /// (1) スケールを <see cref="Easing.InCubic"/> で加速しながら 0 へ縮める、
    /// (2) 自転・傾きは止めずに続ける（消えかけでもくるくる回り続ける方が自然なため）、
    /// (3) 演出が終わったら破棄する、の 3 つだけ。
    /// </summary>
    /// <param name="dt">このフレームのデルタタイム（0 以下なら経過を進めない）。</param>
    private void UpdateDying(float dt)
    {
        if (dt > 0f)
        {
            dyingElapsed += dt;
            elapsed += dt;   // 自転・傾きの位相を消滅中も進め続ける
        }

        if (transform.IsValid)
        {
            // 0（開始）→1（終了）の進捗を InCubic で加速させ、
            // 「開始時スケール × (1 - 進捗)」で 0 まで縮める
            float progress = Easing.Progress01(dyingElapsed, disappearSeconds);
            float remain = 1f - Easing.InCubic(progress);
            transform.Scale = new SEED.Vector3(
                SEED.Mathf.Max(dyingStartScale.x * remain, MinScale),
                SEED.Mathf.Max(dyingStartScale.y * remain, MinScale),
                SEED.Mathf.Max(dyingStartScale.z * remain, MinScale));

            ApplySpinAndTilt();
        }

        // disappearSeconds <= 0 は Kill() 側で即破棄しているため、
        // ここに来る時点で disappearSeconds > 0 が保証されている
        if (dyingElapsed >= disappearSeconds)
        {
            gameObject.Destroy();
        }
    }

    /// <summary>
    /// 出現演出のスケールを適用する【通常更新（消滅中でない間）専用】。
    /// 生成からの経過秒を <see cref="appearSeconds"/> で正規化し、
    /// <see cref="Easing.OutBack"/> で「勢い余って少し行き過ぎてから収まる」拡大を作る。
    /// 演出完了後（progress == 1）は <see cref="modelScale"/> に張り付いたまま安定する。
    /// </summary>
    private void ApplyAppearScale()
    {
        float progress = Easing.Progress01(elapsed, appearSeconds);
        float scale = SEED.Mathf.Max(modelScale * Easing.OutBack(progress), MinScale);
        transform.Scale = new SEED.Vector3(scale, scale, scale);
    }

    /// <summary>
    /// 自転（Y 軸）と上下の揺れに連動した傾き（X 軸・Z 軸）をまとめて適用する
    /// 【回転計算の唯一の実装。通常更新・消滅演出のどちらからも呼ばれる】。
    ///
    /// - Y（ヨー）: 初期ヨー角から <see cref="spinDegPerSec"/> × <see cref="spinDirection"/>
    ///   で一定速度で回り続ける。
    /// - X（ピッチ）・Z（ロール）: 上下の揺れと同じ位相
    ///   （<c>elapsed × bobFrequency × 2π + bobPhase</c>）を使い、X は sin、
    ///   Z は cos を <see cref="TiltRollDamping"/> で減衰させたものを掛けて
    ///   <see cref="tiltDeg"/> 以内で傾ける（上下に浮き沈みする動きと呼吸を合わせるため）。
    /// </summary>
    private void ApplySpinAndTilt()
    {
        float yawDeg = initialYawDeg + spinDegPerSec * spinDirection * elapsed;

        float phase = elapsed * bobFrequency * TwoPi + bobPhase;
        float tiltXDeg = tiltDeg * SEED.Mathf.Sin(phase);
        float tiltZDeg = tiltDeg * SEED.Mathf.Cos(phase) * TiltRollDamping;

        transform.Rotation = new SEED.Vector3(tiltXDeg, yawDeg, tiltZDeg);
    }

    /// <summary>
    /// いま「その場に留める」指定が掛かっているか
    /// 【留め指定の唯一の問い合わせ口】。
    /// チュートリアルが居なければ必ず false なので、通常プレイの挙動は変わらない。
    /// </summary>
    /// <returns>留めるなら true。</returns>
    private static bool IsStationaryNow()
        => TutorialRules.Active && TutorialRules.DriftStationary;

    /// <summary>種類の文字列が既知のものか（未知なら効果を持たない）。</summary>
    /// <param name="value">検証する種類文字列。</param>
    private static bool IsKnownKind(string value)
        => value == KindStun || value == KindFishRecover || value == KindLineRecover;
}
