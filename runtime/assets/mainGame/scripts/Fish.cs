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
///
/// [餌への反応]（<see cref="BehaviorState"/>）
/// <code>
/// Roam     --餌が「感知距離＋餌の影響半径」以内--> Approach
/// Approach --餌が消えた／前アタリを断られた--> Roam（loseInterestSeconds のクールダウン付き）
/// Approach --竿を振られた（FishingController.SwingSerial が変化）--> Escape（驚いて逃げる）
/// Approach --食いつき距離以内 → biteDelay 待ち → BeginNibbling 成功--> Nibbling
/// Nibbling --合わせ成功（コントローラが OnHooked）--> Bite
/// Nibbling --合わせ失敗／中断（ReleaseFromHook）--> Escape
/// Bite     --リリース（ReleaseFromHook）--> Escape
/// Bite     --釣り上げ成立（OnCaught）--> Caught（AI 停止。演出の終わりに破棄）
/// Escape   --escapeSeconds 逃げ切る--> Roam（クールダウン付き）
/// </code>
/// 餌の位置・判定距離はすべて <see cref="FishingController.Current"/> から読む
/// （魚は prefab から動的生成されるため、参照フィールドでコントローラを注入できない）。
/// </summary>
public class Fish : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>度→ラジアン変換係数。</summary>
    private const float DegToRad = 3.14159265f / 180f;

    /// <summary>1 回転（ラジアン）。ランダム方位の生成に使う。</summary>
    private const float FullTurnRadians = 2f * 3.14159265f;

    /// <summary>半回転（ラジアン）。角度差を ±π の範囲へ畳むのに使う。</summary>
    private const float HalfTurnRadians = 3.14159265f;

    /// <summary>「距離が実質 0」とみなすしきい値（0 除算・方位の不定を避ける）。</summary>
    private const float DistanceEpsilon = 1e-4f;

    /// <summary>
    /// サイズ倍率の下限クランプ（0 以下や負の倍率でモデルが潰れる／裏返るのを防ぐ番人値）。
    /// </summary>
    private const float MinSizeMultiplier = 0.01f;

    /// <summary>
    /// 餌（の少し下）へ<b>完全に張り付く</b>距離（メートル）。
    ///
    /// 掛かって巻かれている（<see cref="BehaviorState.Bite"/>）あいだ、これ以下まで
    /// 詰められたら座標を目標点そのものへ揃える。こうしないと、ウキが毎フレーム
    /// 動いているあいだ <see cref="attachSpeed"/> の追従が常にわずかに遅れ、
    /// 魚がウキから微妙にずれたまま引かれているように見える。
    /// 逆に <see cref="BehaviorState.Nibbling"/>（まだ掛かっていない）では張り付かせない。
    /// </summary>
    private const float AttachSnapDistance = 0.05f;

    // ─── 行動状態 ─────────────────────────────────────────────

    /// <summary>
    /// 魚の行動状態。スクリプトはファイル名＝型名で 1 ファイル 1 クラスとして扱われるため、
    /// この列挙型は独立ファイルにせず本クラスの入れ子として定義する
    /// （外部からは <c>Fish.BehaviorState</c> で参照できる）。
    /// </summary>
    public enum BehaviorState
    {
        /// <summary>回遊中（既定の振る舞い）。生成地点の周りを気ままに泳ぐ。</summary>
        Roam,

        /// <summary>
        /// 餌に気づいて近づいている最中。
        /// この最中に竿を振られる（<see cref="FishingController.SwingSerial"/> が変化する）と
        /// 驚いて <see cref="Escape"/> へ逃げる。回遊中（<see cref="Roam"/>）の個体は
        /// まだ餌に反応していないので竿振りを無視する。
        /// </summary>
        Approach,

        /// <summary>
        /// 餌をつついている（前アタリ〜本アタリ）。位置は餌に追従するが、
        /// まだ掛かってはいない。抜け方はコントローラ側（合わせの成否）が決める。
        /// </summary>
        Nibbling,

        /// <summary>餌に食いついている（釣られている）。位置は餌に追従する。</summary>
        Bite,

        /// <summary>
        /// 逃走中。合わせ失敗・リリース直後に、餌と反対方向へ
        /// <see cref="escapeSeconds"/> のあいだ全速で泳いで離れる（見た目に分かりやすくする）。
        /// </summary>
        Escape,

        /// <summary>
        /// <b>釣り上げられた</b>（釣果演出中）。AI は完全に止まり、位置・向き・スケールは
        /// すべて外部（<see cref="CatchPresenter"/>）が決める。
        ///
        /// この状態からは自力で抜けない（演出の終わりに個体ごと破棄される）。
        /// <see cref="FishManager"/> の円環クランプの除外登録も、破棄されるまで
        /// <b>外さない</b>（外すと演出中の魚が出現円環へ引き戻されてしまう）。
        /// </summary>
        Caught,
    }

    /// <summary>現在の行動状態（デバッグ・他スクリプトからの参照用）。</summary>
    public BehaviorState State { get; private set; } = BehaviorState.Roam;

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

    /// <summary>
    /// 表示名（釣果ログ・UI 用）。空なら <see cref="DefaultDisplayName"/> を使う。
    /// prefab ごとに魚の名前を入れる想定。
    /// </summary>
    [SerializeField(Label = "表示名")]
    private string fishName = "";

    // ─── サイズの個体差（釣果表示用）───────────────────────────

    /// <summary>
    /// 個体ごとのサイズ倍率の下限。生成時（<see cref="OnStart"/>）に
    /// [下限, 上限] の一様乱数を 1 度だけ引き、<see cref="SizeMultiplier"/> に控える。
    /// </summary>
    [Header("サイズの個体差"), SerializeField(Label = "サイズ倍率の下限")]
    private float sizeMultiplierMin = 0.8f;

    /// <summary>個体ごとのサイズ倍率の上限。</summary>
    [SerializeField(Label = "サイズ倍率の上限")]
    private float sizeMultiplierMax = 1.3f;

    /// <summary>
    /// 釣果表示に添える長さの単位ラベル。
    /// <see cref="size"/> は本来「大きさスコアの元になる無次元の値」なので、
    /// 表示上の単位はデータ側（prefab / インスペクタ）で決められるようにしてある。
    /// </summary>
    [SerializeField(Label = "サイズの単位ラベル")]
    private string sizeUnitLabel = "cm";

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

    // ─── 餌への反応 ───────────────────────────────────────────

    /// <summary>餌へ寄っていくときの速さ（m/s）。回遊より少し速い想定。</summary>
    [Header("餌への反応"), SerializeField(Label = "接近の速さ(m/s)")]
    private float approachSpeed = 2.25f;

    /// <summary>食いつくまでの待ち時間の下限（秒）。実際の待ち時間はこの範囲の一様乱数。</summary>
    [SerializeField(Label = "食いつき待ちの最小(秒)")]
    private float biteDelayMin = 0.5f;

    /// <summary>食いつくまでの待ち時間の上限（秒）。</summary>
    [SerializeField(Label = "食いつき待ちの最大(秒)")]
    private float biteDelayMax = 2f;

    /// <summary>
    /// 餌の直進ホーミングに切り替える水平距離（メートル）。
    /// 旋回半径付きの <see cref="SwimTowardHeading"/> のままだと、餌の近くで
    /// 旋回しきれず周回してしまい <see cref="FishingController.BiteDistance"/> 以内へ
    /// 入れないことがあるため、この距離以下では旋回を無視して餌へ直進させる。
    /// </summary>
    [SerializeField(Label = "餌の直進ホーミング距離(m)")]
    private float homingDistance = 2.0f;

    /// <summary>
    /// 食いつき待ちを解除するヒステリシス倍率。
    /// 待ちが開始した後は、距離が「<see cref="FishingController.BiteDistance"/> ×
    /// この倍率」を超えるまで待ちを解除しない。境界付近の微小な出入りで
    /// 待ちタイマーがリセットされ続けて食いつきに至らない事態を防ぐ。
    /// </summary>
    private const float BiteLeaveMultiplier = 3.0f;

    /// <summary>
    /// 餌に興味を失ってから再び反応するまでのクールダウン（秒）。
    /// 他の魚に先を越された・逃がされた直後に即座に食いつき直すのを防ぐ。
    /// </summary>
    [SerializeField(Label = "興味を失う時間(秒)")]
    private float loseInterestSeconds = 3f;

    /// <summary>食いついているあいだ、餌（ウキ）から何メートル下に居るか。</summary>
    [SerializeField(Label = "ヒット中の沈み量(m)")]
    private float hookedDepthOffset = 0.3f;

    /// <summary>
    /// 餌への追従速度（m/s）。
    ///
    /// つつき中・ヒット中（<see cref="UpdateBite"/>）は、この速さで
    /// 目標点「餌 −(0, <see cref="hookedDepthOffset"/>, 0)」へ<b>3 次元的に</b>寄る。
    /// 接近中（<see cref="UpdateApproach"/>）でも、同じ速さで高さ（Y）だけを
    /// 目標の深さへ寄せていく（XZ の泳ぎは <see cref="approachSpeed"/> のまま）。
    ///
    /// 以前は座標を目標点へ直接代入していたため、餌の影響圏に入った瞬間に
    /// 遊泳深度から水面直下まで一瞬でワープしていた。移動量を
    /// 「min(この速さ × dt, 残り距離)」で刻むことでその瞬間移動を無くす。
    /// </summary>
    [SerializeField(Label = "餌への追従速度(m/s)")]
    private float attachSpeed = 8f;

    /// <summary>
    /// 合わせ失敗・リリース後に餌から全速で逃げる秒数。
    /// この間は <see cref="approachSpeed"/> で餌と反対方向へ泳ぐ。
    /// </summary>
    [SerializeField(Label = "逃走の秒数(秒)")]
    private float escapeSeconds = 1.5f;

    /// <summary>
    /// 逃げ切った瞬間（<see cref="escapeSeconds"/> 経過時）に魚自身を消すか。
    /// true（既定）: <see cref="SEED.GameObject.Destroy"/> で消える。
    /// FishManager の自動補充（EnsurePopulation）が次フレームでこれを検知し、
    /// 同じレベルの円環内のランダムな位置へ別個体を生成し直す。
    /// false: 従来どおり回遊（<see cref="BehaviorState.Roam"/>）へ
    /// クールダウン付きで戻るだけ（個体は消えない）。
    /// </summary>
    [SerializeField(Label = "逃げた後に消える")]
    private bool despawnAfterEscape = true;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>回遊の中心（生成地点）。null = 未初期化（最初の Update で現在地を採る）。</summary>
    private SEED.Vector3? homePosition = null;

    /// <summary>現在の目標方位（ラジアン、yaw = atan2(x, z) 規約）。</summary>
    private float targetHeadingRad = 0f;

    /// <summary>次の転回までの残り秒数。</summary>
    private float turnTimer = 0f;

    /// <summary>方位の乱数源。</summary>
    private readonly System.Random random = new();

    /// <summary>食いつきまでの残り待ち時間（秒）。<see cref="BehaviorState.Approach"/> で餌に届いてから減る。</summary>
    private float biteWaitRemaining = 0f;

    /// <summary>食いつき待ちに入っているか（<see cref="biteWaitRemaining"/> が有効か）。</summary>
    private bool biteWaitStarted = false;

    /// <summary>餌へ反応しないクールダウンの残り秒数。0 以下で再び反応できる。</summary>
    private float loseInterestRemaining = 0f;

    /// <summary>逃走（<see cref="BehaviorState.Escape"/>）の残り秒数。</summary>
    private float escapeRemaining = 0f;

    /// <summary>逃走中に向かう方位（ラジアン）。逃走開始時に「餌の反対側」で固定する。</summary>
    private float escapeHeadingRad = 0f;

    /// <summary>
    /// 最後に見た竿振りの通し番号（<see cref="FishingController.SwingSerial"/>）。
    ///
    /// <see cref="BehaviorState.Approach"/> へ入る瞬間に必ず現在値へ同期するので、
    /// 「寄り始める前に振られた分」を数えてしまうことはない。接近中はこの値と
    /// コントローラ側の値を毎フレーム比べ、変わっていたら驚いて逃げる。
    /// </summary>
    private int lastSeenSwingSerial = 0;

    /// <summary>
    /// <see cref="FishingController.RegisterEngaged"/> に登録済みか。
    /// 二重登録・登録漏れを避けるため、登録／解除は必ずこのフラグ経由で行う。
    /// </summary>
    private bool engagedRegistered = false;

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

    /// <summary>大きさ（釣果ログ・UI から参照する）。</summary>
    public float Size => size;

    /// <summary>表示名。未設定なら既定名を返す。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(fishName) ? DefaultDisplayName : fishName;

    /// <summary>表示名が未設定のときに使う既定名。</summary>
    private const string DefaultDisplayName = "魚";

    /// <summary>
    /// このスクリプトが乗っているアクタ（<c>gameObject</c> は protected なので公開する）。
    /// 釣り上げ時に <see cref="FishingController"/> が破棄するために使う。
    /// </summary>
    public SEED.GameObject Actor => gameObject;

    /// <summary>
    /// このスクリプトが乗っているアクタのトランスフォーム
    /// （<c>transform</c> は protected なので公開する）。
    /// 釣果演出（<see cref="CatchPresenter"/>）が位置・向き・スケールを直接決めるために使う。
    /// </summary>
    public SEED.Transform Transform => transform;

    /// <summary>
    /// この個体のサイズ倍率（<see cref="sizeMultiplierMin"/>〜<see cref="sizeMultiplierMax"/>）。
    /// 出現時に 1 度だけ抽選し、以後は変わらない。見た目のスケールと釣果表示の両方に効く。
    /// </summary>
    public float SizeMultiplier { get; private set; } = 1f;

    /// <summary>釣果表示用のサイズ（＝<see cref="Size"/> × <see cref="SizeMultiplier"/>）。</summary>
    public float DisplaySize => size * SizeMultiplier;

    /// <summary>釣果表示に添える単位ラベル（例: "cm"）。</summary>
    public string SizeUnitLabel => sizeUnitLabel;

    /// <summary>
    /// 出現時のスケール（＝シーン／prefab のスケール × <see cref="SizeMultiplier"/>）。
    /// 釣果演出のポップ（0 → 原寸）の<b>原寸</b>がこの値。
    /// </summary>
    public SEED.Vector3 CaughtScale { get; private set; } = SEED.Vector3.One;

    /// <summary>
    /// 見た目の大きさ指標（＝<see cref="CaughtScale"/> の最大成分）。
    /// 釣果カメラを魚の大きさに応じて後ろへ引く量の算出に使う
    /// （<see cref="CatchPresenter"/>）。サイズ倍率は既に掛かっているので二重に掛けないこと。
    /// </summary>
    public float VisualSizeMetric
        => SEED.Mathf.Max(CaughtScale.x, SEED.Mathf.Max(CaughtScale.y, CaughtScale.z));

    /// <summary>
    /// 生成直後の初期化。個体ごとのサイズ倍率を抽選し、見た目のスケールへ反映する。
    ///
    /// スケールは「シーン／prefab に保存されたスケール × 倍率」で上書きし、その結果を
    /// <see cref="CaughtScale"/> へ控える（釣果演出のポップはこの値を原寸として使う）。
    /// </summary>
    public override void OnStart()
    {
        // 下限＞上限のデータ入力ミスでも壊れないよう、必ず [min, max] へ整える
        float min = sizeMultiplierMin;
        float max = SEED.Mathf.Max(sizeMultiplierMin, sizeMultiplierMax);
        SizeMultiplier = SEED.Mathf.Max(SEED.Random.Range(min, max), MinSizeMultiplier);

        var scaled = transform.Scale * SizeMultiplier;
        transform.Scale = scaled;
        CaughtScale = scaled;
    }

    /// <summary>フレーム開始時に呼ばれる。</summary>
    public override void BeginFrame(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update より前の更新。</summary>
    public override void EarlyUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>
    /// 毎フレームの行動更新。
    /// 餌の状況を見て行動状態（回遊／接近／食いつき）を決め、その状態の移動を行う。
    /// </summary>
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

        // 釣り上げられた個体は AI を完全に止める（位置・向き・スケールは CatchPresenter が決める）。
        // 状態遷移も移動も行わないので、演出中に回遊へ戻ったり逃げ出したりしない。
        if (State == BehaviorState.Caught) { return; }

        // 興味を失っているあいだのクールダウンを進める（状態に依らず常に進める）
        if (loseInterestRemaining > 0f) { loseInterestRemaining -= dt; }

        // 行動状態を更新してから、その状態の移動を行う（判断と移動で責務を分ける）
        UpdateBehaviorState(dt);

        switch (State)
        {
            case BehaviorState.Nibbling:
            case BehaviorState.Bite:
                // つつき中もヒット中も「餌の少し下に張り付いて餌の方を向く」で同じ。
                UpdateBite(dt);
                break;

            case BehaviorState.Escape:
                UpdateEscape(dt);
                break;

            case BehaviorState.Approach:
                UpdateApproach(dt);
                break;

            default:
                UpdateRoam(dt, home);
                break;
        }
    }

    /// <summary>
    /// 餌の状況から行動状態を決める【状態遷移の唯一の集約点】。
    ///
    /// 餌が無効になった／コントローラが居ない場合は必ず回遊へ戻すので、
    /// 「餌へ寄ったまま固まる」状態は生まれない。
    /// </summary>
    /// <param name="dt">このフレームの経過秒数。</param>
    private void UpdateBehaviorState(float dt)
    {
        // 釣り上げられた個体は状態遷移しない（Update 側でも弾いているが、唯一の集約点にも置く）
        if (State == BehaviorState.Caught) { return; }

        // 逃走中は時間経過だけで抜ける（この間は餌に反応しない）
        if (State == BehaviorState.Escape)
        {
            escapeRemaining -= dt;
            if (escapeRemaining <= 0f)
            {
                if (despawnAfterEscape) { DespawnAfterEscape(); }
                else { BackToRoam(withCooldown: true); }
            }
            return;
        }

        // つつき中はコントローラ側（合わせの成否）からしか抜けない。
        // ただし餌そのものが消えた場合だけは自力で回遊へ戻る（固まり防止）。
        if (State == BehaviorState.Nibbling)
        {
            if (FishingController.Current is not { BaitActive: true }) { BackToRoam(withCooldown: false); }
            return;
        }

        // 食いつき中（ヒット〜巻き取り〜釣り上げ演出開始まで）はコントローラ側
        // （釣り上げ成立／リリース）からしか抜けない。
        //
        // ここで BaitActive を見てはいけない: BaitActive は Floating/Reeling/Nibbling/
        // HookWindow のときだけ true で、掛かった瞬間（TryHook が State を Hooked へ
        // 進める）から false になる。もし BaitActive で判定すると、掛かった直後・
        // 巻いている最中の魚が「餌が消えた」と誤認して毎フレーム回遊へ戻ってしまい、
        // 巻いても魚が付いてこない（置き去りになる）バグになる。
        // 代わりに「コントローラがまだこの魚を掛けた状態として保持しているか」
        // （<see cref="FishingController.IsHooked"/>）を見る。掛かっている個体の解除は
        // 必ず <see cref="ReleaseFromHook"/>（Escape へ）か <see cref="OnCaught"/>
        // （Caught へ）のどちらかを経由するので、この自力回帰は「両方とも呼ばれずに
        // hookedFish だけが外れた」異常系のみを拾う保険。
        if (State == BehaviorState.Bite)
        {
            if (FishingController.Current is not { IsHooked: true }) { BackToRoam(withCooldown: false); }
            return;
        }

        // 餌が無い（コントローラ未起動・キャスト前・飛翔中）なら回遊へ戻す
        if (FishingController.Current is not { BaitActive: true } fc)
        {
            BackToRoam(withCooldown: false);
            return;
        }

        var bait = fc.BaitPosition;
        float distance = HorizontalDistance(transform.Position, bait);

        // 回遊中: 「自分の感知距離 ＋ 餌の影響半径」以内へ入ったら寄っていく
        if (State == BehaviorState.Roam)
        {
            if (loseInterestRemaining > 0f) { return; }
            if (distance > baitSenseDistance + fc.BaitInfluenceRadius) { return; }

            State = BehaviorState.Approach;
            biteWaitStarted = false;
            // 寄り始める前に振られた竿振りを数えないよう、この瞬間の番号へ揃えておく
            lastSeenSwingSerial = fc.SwingSerial;
            SetEngaged(fc, engaged: true);
            return;
        }

        // 接近中: 竿を振られたら驚いて逃げる（餌に反応している個体だけが驚く）。
        // 番号（SwingSerial）は振るたびに増えるので、変化の有無だけを見れば取りこぼしがない。
        if (fc.SwingSerial != lastSeenSwingSerial)
        {
            lastSeenSwingSerial = fc.SwingSerial;
            BeginEscape();             // 逃走 → 逃げ切りで despawnAfterEscape に従って消える
            return;
        }

        // 接近中: 食いつき距離まで届いたら待ち時間を数え、経過したら食いつきを試みる
        //
        // ヒステリシス: 待ちが一度始まったら、境界付近の微小な出入り（旋回の揺れなど）で
        // 毎フレーム解除されないよう、BiteDistance × BiteLeaveMultiplier を超えて
        // 離れるまでは待ちを維持する。
        float leaveDistance = biteWaitStarted ? fc.BiteDistance * BiteLeaveMultiplier : fc.BiteDistance;
        if (distance > leaveDistance)
        {
            // 一度も届いていない（またはしきい値を大きく超えて離れた）あいだは待ちを解除しておく
            biteWaitStarted = false;
            return;
        }

        if (!biteWaitStarted)
        {
            biteWaitStarted = true;
            biteWaitRemaining = SEED.Random.Range(biteDelayMin, SEED.Mathf.Max(biteDelayMin, biteDelayMax));
        }

        biteWaitRemaining -= dt;
        if (biteWaitRemaining > 0f) { return; }

        // 前アタリ（コツコツ）を始める。既に別の魚がアタっていれば false が返るので興味を失う。
        if (fc.BeginNibbling(this))
        {
            State = BehaviorState.Nibbling;
            biteWaitStarted = false;
            return;
        }

        BackToRoam(withCooldown: true);
    }

    /// <summary>回遊（既定の振る舞い）の移動。生成地点の周りを気ままに泳ぐ。</summary>
    /// <param name="dt">このフレームの経過秒数。</param>
    /// <param name="home">回遊の中心（生成地点）。</param>
    private void UpdateRoam(float dt, SEED.Vector3 home)
    {
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

        SwimTowardHeading(targetHeadingRad, swimSpeed, dt);
    }

    /// <summary>
    /// 餌へ近づく移動。餌の方位へ向き直しながら <see cref="approachSpeed"/> で前進する。
    ///
    /// 行動半径のクランプは掛けない（餌が行動半径の外にあっても寄れるようにする）。
    /// FishManager の円環クランプについても <see cref="SetEngaged"/> で対象外にしてある。
    /// </summary>
    /// <param name="dt">このフレームの経過秒数。</param>
    private void UpdateApproach(float dt)
    {
        if (FishingController.Current is not { BaitActive: true } fc) { return; }

        var pos = transform.Position;
        var bait = fc.BaitPosition;
        float dx = bait.x - pos.x;
        float dz = bait.z - pos.z;
        float sqDistance = dx * dx + dz * dz;

        // 真上に重なっているときは方位が定まらないので、いまの向きのまま進む
        if (sqDistance > DistanceEpsilon * DistanceEpsilon)
        {
            targetHeadingRad = SEED.Mathf.Atan2(dx, dz);
        }

        // 高さは XZ の詰め方に関係なく、常に目標深度（餌の hookedDepthOffset m 下）へ
        // attachSpeed で少しずつ寄せる。これにより、食いつく頃には既にその深さまで
        // 浮上し終えていて、UpdateBite へ移った瞬間に Y が飛ぶことがなくなる。
        float targetY = bait.y - hookedDepthOffset;
        float easedY = MoveTowards(pos.y, targetY, attachSpeed * dt);

        // 餌が十分近い（homingDistance 以内）場合は旋回半径のある SwimTowardHeading を使わず、
        // 餌へ向けて直進させる。SwimTowardHeading のまま食いつき距離まで詰めようとすると
        // 旋回が追いつかず餌の周りを周回し続けてしまうため。
        float distance = SEED.Mathf.Sqrt(sqDistance);
        if (distance <= homingDistance)
        {
            // 向きだけは従来どおり滑らかに餌の方位へ補間する（見た目のための回頭）
            transform.Rotation = new SEED.Vector3(0f, SmoothYawRad(targetHeadingRad, dt) / DegToRad, 0f);

            // 移動は水平単位ベクトル方向へ、行き過ぎない距離だけ直進させる
            float step = SEED.Mathf.Min(approachSpeed * dt, distance);
            float nextX = pos.x;
            float nextZ = pos.z;
            if (distance > DistanceEpsilon)
            {
                float invDistance = 1f / distance;
                nextX += dx * invDistance * step;
                nextZ += dz * invDistance * step;
            }
            transform.Position = new SEED.Vector3(nextX, easedY, nextZ);
            return;
        }

        // 遠い間は従来どおり旋回付きで泳ぎ（XZ）、高さだけを別途寄せる
        SwimTowardHeading(targetHeadingRad, approachSpeed, dt);
        var swum = transform.Position;
        transform.Position = new SEED.Vector3(swum.x, easedY, swum.z);
    }

    /// <summary>
    /// つつき中（<see cref="BehaviorState.Nibbling"/>）・食いつき中
    /// （<see cref="BehaviorState.Bite"/>）の追従。餌（ウキ）の
    /// <see cref="hookedDepthOffset"/> m 下を目標点に、<see cref="attachSpeed"/> で
    /// 3 次元的に寄りながら、餌のある向きを向く。
    ///
    /// <b>ここは以前ワープしていた場所</b>: 目標点を <c>transform.Position</c> へ直接
    /// 代入していたため、前アタリが始まった最初の 1 フレームで、遊泳深度から
    /// 水面直下（餌の少し下）まで一瞬で飛んでいた。移動量を
    /// 「min(<see cref="attachSpeed"/> × dt, 残り距離)」に刻むことで解消する。
    /// 完全に張り付くのは「掛かって巻かれていて（<see cref="BehaviorState.Bite"/>）、
    /// かつ残り距離が <see cref="AttachSnapDistance"/> 以下」のときだけ。
    /// </summary>
    /// <param name="dt">このフレームの経過秒数。</param>
    private void UpdateBite(float dt)
    {
        if (FishingController.Current is not { } fc) { return; }

        var pos = transform.Position;
        var bait = fc.BaitPosition;

        // 餌の方向（＝自分が引かれている向き）へ向き直す
        float dx = bait.x - pos.x;
        float dz = bait.z - pos.z;
        if (dx * dx + dz * dz > DistanceEpsilon * DistanceEpsilon)
        {
            targetHeadingRad = SEED.Mathf.Atan2(dx, dz);
        }
        transform.Rotation = new SEED.Vector3(0f, SmoothYawRad(targetHeadingRad, dt) / DegToRad, 0f);

        // 目標点＝餌の hookedDepthOffset m 下（Y も含めた 3 次元の 1 点）
        float toX = bait.x - pos.x;
        float toY = (bait.y - hookedDepthOffset) - pos.y;
        float toZ = bait.z - pos.z;
        float distance = SEED.Mathf.Sqrt(toX * toX + toY * toY + toZ * toZ);

        // 掛かって巻かれている最中に十分詰められたら、ズレを残さず目標点へ揃える
        if (State == BehaviorState.Bite && distance <= AttachSnapDistance)
        {
            transform.Position = new SEED.Vector3(bait.x, bait.y - hookedDepthOffset, bait.z);
            return;
        }

        // それ以外は行き過ぎない距離だけ目標点へ近づく（＝瞬間移動しない）
        if (distance <= DistanceEpsilon) { return; }
        float step = SEED.Mathf.Min(attachSpeed * dt, distance) / distance;
        transform.Position = new SEED.Vector3(
            pos.x + toX * step,
            pos.y + toY * step,
            pos.z + toZ * step);
    }

    /// <summary>
    /// 逃走中の移動。開始時に決めた「餌と反対方向」へ <see cref="approachSpeed"/> で泳ぎ続ける。
    /// </summary>
    /// <param name="dt">このフレームの経過秒数。</param>
    private void UpdateEscape(float dt)
    {
        SwimTowardHeading(escapeHeadingRad, approachSpeed, dt);
    }

    /// <summary>
    /// 指定方位へ向きを補間しつつ、その向きへ前進する（高さは変えない）。
    /// 回遊・接近で共通の移動処理。
    /// </summary>
    /// <param name="headingRad">目標方位（ラジアン、yaw = atan2(x, z) 規約）。</param>
    /// <param name="speed">前進の速さ（m/s）。</param>
    /// <param name="dt">このフレームの経過秒数。</param>
    private void SwimTowardHeading(float headingRad, float speed, float dt)
    {
        float newYawRad = SmoothYawRad(headingRad, dt);
        var pos = transform.Position;

        transform.Rotation = new SEED.Vector3(0f, newYawRad / DegToRad, 0f);
        transform.Position = new SEED.Vector3(
            pos.x + SEED.Mathf.Sin(newYawRad) * speed * dt,
            pos.y,
            pos.z + SEED.Mathf.Cos(newYawRad) * speed * dt);
    }

    /// <summary>
    /// 現在の yaw を目標方位へ指数補間した結果（ラジアン）を返す。
    /// 角度差は ±π へ畳んでから補間するので、遠回りに回らない。
    /// </summary>
    /// <param name="headingRad">目標方位（ラジアン）。</param>
    /// <param name="dt">このフレームの経過秒数。</param>
    private float SmoothYawRad(float headingRad, float dt)
    {
        float currentYawRad = transform.Rotation.y * DegToRad;
        float delta = SEED.Mathf.Repeat(headingRad - currentYawRad + HalfTurnRadians, FullTurnRadians) - HalfTurnRadians;
        return currentYawRad + delta * SEED.Mathf.Clamped01(turnLerpRate * dt);
    }

    /// <summary>
    /// スカラ値を目標へ「行き過ぎない一定量だけ」近づける（Unity の Mathf.MoveTowards 相当）。
    /// 高さ（Y）を毎フレーム少しずつ寄せるために使う。
    /// </summary>
    /// <param name="current">現在値。</param>
    /// <param name="target">目標値。</param>
    /// <param name="maxDelta">このフレームに動かしてよい最大量（0 以上）。</param>
    /// <returns>目標へ近づけた後の値（目標を追い越さない）。</returns>
    private static float MoveTowards(float current, float target, float maxDelta)
    {
        float diff = target - current;
        if (SEED.Mathf.Abs(diff) <= maxDelta) { return target; }
        return current + (diff > 0f ? maxDelta : -maxDelta);
    }

    /// <summary>2 点間の XZ 平面上の距離（高さの差は無視する）。</summary>
    /// <param name="a">始点。</param>
    /// <param name="b">終点。</param>
    private static float HorizontalDistance(SEED.Vector3 a, SEED.Vector3 b)
    {
        float dx = b.x - a.x;
        float dz = b.z - a.z;
        return SEED.Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ─── 餌との関わりの出入り ─────────────────────────────────

    /// <summary>
    /// 合わせの成功時にコントローラから呼ばれる。つつき中から食いつき中へ移す
    /// （位置の追従は同じなので、状態ラベルだけが変わる）。
    /// </summary>
    public void OnHooked() => State = BehaviorState.Bite;

    /// <summary>
    /// 釣り上げ成立時にコントローラから呼ばれる。AI を止めて
    /// <see cref="BehaviorState.Caught"/> へ移す（以後は <see cref="CatchPresenter"/> が
    /// 位置・向き・スケールを決める）。
    ///
    /// 円環クランプの除外登録（<see cref="FishingController.RegisterEngaged"/>）は
    /// <b>外さない</b>。演出中の魚が <see cref="FishManager"/> に出現円環内へ
    /// 引き戻されるのを防ぐためで、登録は破棄時（<see cref="OnDestroy"/>）に外れる。
    /// </summary>
    public void OnCaught() => State = BehaviorState.Caught;

    /// <summary>
    /// リリース・合わせ失敗・釣り中断でコントローラから呼ばれる共通の出口。
    ///
    /// 餌に関わっていた（つつき中・食いつき中）なら、まず餌と反対方向へ全速で逃げ
    /// （<see cref="BehaviorState.Escape"/>）、逃げ切ってから回遊へ戻る。
    /// それ以外（既に回遊中など）はクールダウン付きで回遊へ戻すだけ。
    /// </summary>
    public void ReleaseFromHook()
    {
        // 釣り上げ済み（演出中）の個体は逃がさない（演出の終わりに破棄される）
        if (State == BehaviorState.Caught) { return; }
        if (State is BehaviorState.Nibbling or BehaviorState.Bite) { BeginEscape(); return; }
        BackToRoam(withCooldown: true);
    }

    /// <summary>
    /// 逃走（<see cref="BehaviorState.Escape"/>）へ入る【逃走開始の唯一の入口】。
    /// 合わせ失敗・リリース（<see cref="ReleaseFromHook"/>）に加えて、
    /// 接近中に竿を振られて驚いたときもここを通る。
    /// 餌と反対の方位を固定し、<see cref="escapeSeconds"/> のあいだ全速で離れる。
    /// 餌の位置が取れない場合は今の向きのまま逃げる。
    /// </summary>
    private void BeginEscape()
    {
        State = BehaviorState.Escape;
        escapeRemaining = escapeSeconds;
        biteWaitStarted = false;
        SetEngaged(FishingController.Current, engaged: false);

        // 餌 → 自分 の向き（＝餌から遠ざかる向き）を逃走方位にする
        escapeHeadingRad = transform.Rotation.y * DegToRad;
        if (FishingController.Current is { } fc)
        {
            var pos = transform.Position;
            var bait = fc.BaitPosition;
            float dx = pos.x - bait.x;
            float dz = pos.z - bait.z;
            if (dx * dx + dz * dz > DistanceEpsilon * DistanceEpsilon)
            {
                escapeHeadingRad = SEED.Mathf.Atan2(dx, dz);
            }
        }
    }

    /// <summary>
    /// 逃げ切った（<see cref="escapeSeconds"/> 経過）ときに魚を消す
    /// （<see cref="despawnAfterEscape"/> が true のときの既定の抜け方）。
    ///
    /// 餌との関わり登録は <see cref="BeginEscape"/> で既に解除済みだが、
    /// 「破棄前に必ず外れている」ことを保証するため念のためここでも呼ぶ
    /// （<see cref="SetEngaged"/> は登録状態が変わらなければ何もしない）。
    /// 登録解除は破棄要求より<b>前</b>に行う: <see cref="OnDestroy"/> は
    /// 破棄処理中に呼ばれ、その時点でのシーンアクセスは保証されないため
    /// （docs/scripting_api.md 参照）、後始末は破棄前に済ませておく。
    /// </summary>
    private void DespawnAfterEscape()
    {
        SetEngaged(FishingController.Current, engaged: false);
        gameObject.Destroy();
    }

    /// <summary>
    /// 回遊へ戻す【Roam 復帰の唯一の出口】。餌との関わり登録も必ずここで外す。
    /// </summary>
    /// <param name="withCooldown">true なら <see cref="loseInterestSeconds"/> のクールダウンを掛ける。</param>
    private void BackToRoam(bool withCooldown)
    {
        // 既に回遊中なら、登録解除だけ確認して抜ける（毎フレーム中心が動くのを防ぐ）
        if (State == BehaviorState.Roam)
        {
            biteWaitStarted = false;
            SetEngaged(FishingController.Current, engaged: false);
            return;
        }

        State = BehaviorState.Roam;
        escapeRemaining = 0f;
        if (withCooldown) { loseInterestRemaining = loseInterestSeconds; }
        biteWaitStarted = false;
        SetEngaged(FishingController.Current, engaged: false);

        // 回遊の中心（生成地点）は<b>書き換えない</b>。
        // 餌を追って出現円環の外まで出ている可能性があるが、中心を現在地へ移すと
        // 「FishManager が円環内へ押し戻す ⇔ 魚が円環外の中心へ戻ろうとする」で
        // 縁に張り付いてしまう。生成地点は必ず円環内なので、そのまま戻らせるのが正しい。
    }

    /// <summary>
    /// 「餌に関わっている魚」としての登録／解除を切り替える。
    /// 登録されているあいだ、<see cref="FishManager"/> の円環クランプの対象から外れる。
    /// </summary>
    /// <param name="fc">釣りコントローラ（null なら登録フラグを落とすだけ）。</param>
    /// <param name="engaged">true = 登録 / false = 解除。</param>
    private void SetEngaged(FishingController? fc, bool engaged)
    {
        if (engaged == engagedRegistered) { return; }

        engagedRegistered = engaged;
        if (fc is null) { return; }

        if (engaged) { fc.RegisterEngaged(gameObject); }
        else { fc.UnregisterEngaged(gameObject); }
    }

    /// <summary>
    /// 破棄直前の後始末。餌との関わり登録を外す（釣り上げで破棄されるときの登録漏れを防ぐ）。
    /// </summary>
    public override void OnDestroy()
    {
        SetEngaged(FishingController.Current, engaged: false);
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
