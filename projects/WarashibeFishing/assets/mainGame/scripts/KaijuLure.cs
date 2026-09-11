// ============================================================================
//  KaijuLure.cs
//  「呼び出しレベルの魚が掛かったら、必ず怪獣が寄ってきて食いつく」
//  ＝わらしべの最終段を確定させるための、怪獣の強制呼び出しの進行管理。
// ============================================================================

/// <summary>
/// 怪獣（最上位レベルの魚）の<b>強制呼び出し</b>を進行させる
/// 【怪獣ルアーの唯一の状態機械】。
///
/// [なぜ要るか]
/// わらしべ連鎖は「格上の魚がたまたま感知距離に居たら寄ってくる」確率的な仕組みなので、
/// Lv9 まで積み上げても最後の怪獣（Lv10）が来ないまま終わることがある。
/// 「Lv9 まで行ったら必ず怪獣まで辿り着く」をゲーム体験として保証するため、
/// 条件を満たした瞬間に<b>怪獣を実体化して強制的に寄せる</b>のがこのクラスの役目。
///
/// [責務]
/// このクラスが持つのは<b>呼び出しの段取り（いつ・どこへ・誰を）だけ</b>。
/// <list type="bullet">
///   <item>「どう泳いで寄るか」は <see cref="Fish.BeginLure"/>（魚側の振る舞い）</item>
///   <item>「どう実体化するか」は <see cref="FishManager.TryMaterializeByPrefabKey"/></item>
///   <item>「食いついたらどうなるか」は <c>FishingController.TryEatHookedFish</c>
///         → <c>SwapHookedFish</c>（既存のわらしべ成立経路をそのまま通る）</item>
/// </list>
/// ＝ここには釣りのルールもアニメーションも入れない（単一責任）。
///
/// [駆動]
/// <see cref="FishingController"/> が所有し、毎フレーム <see cref="Tick"/> を呼ぶ。
/// <see cref="SEEDEditor.Scripting.SEEDScript"/> ではないので、シーンにアクタは要らない。
/// インスペクタ値は毎フレーム <see cref="Settings"/> として渡されるため、
/// 実行中にインスペクタで値を変えてもそのフレームから効く。
///
/// [状態遷移]
/// <code>
/// Idle    --掛かっている魚が呼び出しレベル以上--> Delay（呼び出しの遅延を数える）
/// Delay   --遅延が明けた--> 怪獣を確保
///           ├ 既に実体化している怪獣が居る ------> Luring
///           └ 居ない → 沖側へ実体化 ------------> Waiting（Fish スクリプトの登録待ち）
/// Waiting --Fish スクリプトが登録された--------> Luring
/// Luring  --怪獣が食いついた（NotifyEaten）----> Idle
/// (全状態) --ヒット終了・チュートリアル開始・設定無効--> Idle（Cancel）
/// </code>
/// </summary>
public sealed class KaijuLure
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>「怪獣を呼ぶレベル」がこの値以下なら機能を丸ごと無効にする。</summary>
    public const int DisabledLevel = 0;

    /// <summary>実体化した怪獣の <see cref="Fish"/> スクリプト登録を待つ上限フレーム数。</summary>
    /// <remarks>
    /// アクタを生成しても <c>Fish.OnStart</c> は次フレーム以降に走るため、
    /// 登録されるまで数フレーム待つ必要がある（<c>DebugCommands</c> と同じ事情）。
    /// 上限を超えたら「そもそも Fish が乗っていない .actor を指名した」等の
    /// 設定不備とみなして諦める（無限に待ち続けない）。
    /// </remarks>
    private const int SpawnWaitFrameLimit = 30;

    /// <summary>実体化した瞬間のフレーム待ち数の初期値。</summary>
    private const int NoWaitedFrames = 0;

    /// <summary>位置指定生成のばらつき半径（メートル）。怪獣は狙った 1 点に出す。</summary>
    private const float NoSpawnScatter = 0f;

    /// <summary>「方向が定まらない」とみなす水平距離の 2 乗のしきい値。</summary>
    private const float SqrEpsilon = 1e-6f;

    /// <summary>
    /// 竿先と掛かっている魚が重なっていて沖方向を決められないときの代替方向（+Z）。
    /// 方向が無い場合でも必ず「魚から一定距離離れた点」を作れるようにするための番人値。
    /// </summary>
    private static readonly SEED.Vector3 FallbackOffshoreDirection = new(0f, 0f, 1f);

    // ─── 呼び出しの設定（インスペクタ値の受け渡し）───────────────

    /// <summary>
    /// 怪獣の強制呼び出しのパラメータ一式
    /// 【インスペクタ値をこのクラスへ渡す唯一の器】。
    ///
    /// <see cref="FishingController"/> が自分のインスペクタ値から毎フレーム組み立てて
    /// <see cref="Tick"/> へ渡す。値を<b>保持しない</b>ことで、実行中にインスペクタを
    /// いじった結果が次のフレームからそのまま効く（状態と設定を混ぜない）。
    /// </summary>
    public readonly struct Settings
    {
        /// <summary>怪獣を呼ぶレベル（掛かっている魚がこのレベル以上で呼ぶ。0 で無効）。</summary>
        public int LureLevel { get; }

        /// <summary>怪獣の .actor パスに含まれる識別キー（例 "kaiju"）。</summary>
        public string PrefabKey { get; }

        /// <summary>掛かっている魚から沖側へ、この距離（メートル）離して実体化する。</summary>
        public float SpawnDistanceMeters { get; }

        /// <summary>呼ばれた怪獣の遊泳速度倍率（通常の接近速度に掛かる）。</summary>
        public float SpeedMultiplier { get; }

        /// <summary>条件成立から実際に呼ぶまでの遅延（秒）。ヒット直後の演出と重ねないための間。</summary>
        public float DelaySeconds { get; }

        /// <summary>各値を指定して設定を作る。</summary>
        /// <param name="lureLevel">怪獣を呼ぶレベル（0 で無効）。</param>
        /// <param name="prefabKey">怪獣の prefab 識別キー。</param>
        /// <param name="spawnDistanceMeters">出現距離（メートル）。</param>
        /// <param name="speedMultiplier">速度倍率。</param>
        /// <param name="delaySeconds">呼び出しの遅延（秒）。</param>
        public Settings(int lureLevel, string prefabKey, float spawnDistanceMeters, float speedMultiplier, float delaySeconds)
        {
            LureLevel           = lureLevel;
            PrefabKey           = prefabKey;
            SpawnDistanceMeters = spawnDistanceMeters;
            SpeedMultiplier     = speedMultiplier;
            DelaySeconds        = delaySeconds;
        }

        /// <summary>この設定で怪獣の呼び出しが有効か（レベル・キーの両方が埋まっているか）。</summary>
        public bool Enabled => LureLevel > DisabledLevel && !string.IsNullOrWhiteSpace(PrefabKey);
    }

    // ─── 進行状態 ─────────────────────────────────────────────

    /// <summary>呼び出しの進行段階。</summary>
    private enum Phase
    {
        /// <summary>何もしていない（条件成立を待っている）。</summary>
        Idle,

        /// <summary>条件が成立し、呼び出しの遅延を数えている。</summary>
        Delay,

        /// <summary>怪獣のアクタを実体化し、<see cref="Fish"/> スクリプトの登録を待っている。</summary>
        Waiting,

        /// <summary>怪獣が掛かっている魚へ寄ってきている。</summary>
        Luring,
    }

    /// <summary>いまの進行段階。</summary>
    private Phase phase = Phase.Idle;

    /// <summary>呼び出しの遅延の残り秒数（<see cref="Phase.Delay"/> のあいだ減る）。</summary>
    private float delayRemaining = 0f;

    /// <summary>実体化した怪獣のアクタ（<see cref="Phase.Waiting"/> のあいだだけ有効）。</summary>
    private SEED.GameObject summonedActor = default;

    /// <summary>スクリプト登録を待っているフレーム数（<see cref="SpawnWaitFrameLimit"/> で打ち切る）。</summary>
    private int waitedFrames = NoWaitedFrames;

    /// <summary>いま寄せている怪獣（<see cref="Phase.Luring"/> のあいだだけ有効）。</summary>
    private Fish? luredFish = null;

    /// <summary>
    /// いまのヒットで既に呼び出しを始めたか【二重呼び出しを防ぐ唯一のフラグ】。
    ///
    /// ヒットが終わる（<see cref="FishingController.IsHooked"/> が false になる）までは
    /// 立ったままなので、失敗しても同じヒット中に何度も呼び直すことはない。
    /// わらしべで魚が乗り換わってもヒットは続いているので、フラグも維持される。
    /// </summary>
    private bool requestedThisHook = false;

    /// <summary>いま怪獣を寄せている最中か（デバッグ表示・外部からの確認用）。</summary>
    public bool IsLuring => phase == Phase.Luring;

    // ─── 駆動 ─────────────────────────────────────────────────

    /// <summary>
    /// 毎フレームの進行【呼び出しの唯一の駆動点】。
    ///
    /// 解除条件の判定をここ 1 か所に集約しているので、糸切れ・リリース・釣り上げ・
    /// 姿勢解除といった<b>ヒットの終わり方が何であっても</b>必ず解除される
    /// （終了経路ごとに解除を書き足す必要がない）。
    /// </summary>
    /// <param name="fc">釣りコントローラ（このクラスの所有者）。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <param name="settings">インスペクタから渡された設定一式。</param>
    public void Tick(FishingController fc, float deltaTime, in Settings settings)
    {
        // ── 1) 解除条件（どの段階よりも先に見る）──────────────────
        // ヒットが終わっていたら、次のヒットのために呼び出し済みフラグも落とす。
        if (!fc.IsHooked || fc.HookedFish is not { } hooked)
        {
            requestedThisHook = false;
            Cancel("ヒットが終わった");
            return;
        }

        // チュートリアル中は呼ばない（締めの演出は台本側が持っているため）。
        if (TutorialRules.Active) { Cancel("チュートリアル中"); return; }

        // 設定が無効化された（レベル 0・キー空）なら、進行中でも畳む。
        if (!settings.Enabled) { Cancel("設定が無効"); return; }

        // ── 2) 段階ごとの進行 ───────────────────────────────────
        switch (phase)
        {
            case Phase.Delay:
                UpdateDelay(fc, deltaTime, settings);
                return;

            case Phase.Waiting:
                UpdateWaiting(fc, settings);
                return;

            case Phase.Luring:
                UpdateLuring(hooked, settings);
                return;

            default:
                TryRequest(hooked, settings);
                return;
        }
    }

    /// <summary>
    /// 呼び出しの条件を満たしていれば遅延カウントを始める【呼び出し開始の唯一の判断点】。
    ///
    /// 条件は「まだこのヒットで呼んでいない」「掛かっている魚のレベルが分かっている」
    /// 「そのレベルが呼び出しレベル以上」「掛かっているのが怪獣<b>ではない</b>」の 4 つ。
    /// 最後の条件が無いと、怪獣へ乗り換わった瞬間に次の怪獣を呼び続けてしまう。
    /// </summary>
    /// <param name="hooked">いま掛かっている魚。</param>
    /// <param name="settings">設定一式。</param>
    private void TryRequest(Fish hooked, in Settings settings)
    {
        if (requestedThisHook) { return; }
        if (hooked.Level == Fish.UnknownLevel) { return; }
        if (hooked.Level < settings.LureLevel) { return; }
        if (IsKaiju(hooked, settings.PrefabKey)) { return; }

        requestedThisHook = true;
        delayRemaining    = SEED.Mathf.Max(settings.DelaySeconds, 0f);
        phase             = Phase.Delay;

        SEED.Debug.Log(
            $"[KaijuLure] 怪獣の呼び出しを予約: 掛かっている {hooked.DisplayName}（Lv{hooked.Level}）"
          + $" / {delayRemaining:F1} 秒後");
    }

    /// <summary>呼び出しの遅延を数え、明けたら怪獣を確保しに行く。</summary>
    /// <param name="fc">釣りコントローラ。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <param name="settings">設定一式。</param>
    private void UpdateDelay(FishingController fc, float deltaTime, in Settings settings)
    {
        delayRemaining -= deltaTime;
        if (delayRemaining > 0f) { return; }

        // 確保に失敗しても requestedThisHook は立てたままなので、同じヒットでは再試行しない
        // （prefab キーの設定ミス等で毎フレーム生成を試み続けるのを防ぐ）。
        if (!Summon(fc, settings)) { phase = Phase.Idle; }
    }

    /// <summary>
    /// 怪獣を 1 匹確保する【怪獣の入手の唯一の実装】。
    ///
    /// <list type="number">
    ///   <item>既に実体化している怪獣が居ればそれを使う（余分なアクタを増やさない）</item>
    ///   <item>居なければ、掛かっている魚の<b>沖側</b>へ実体化する</item>
    /// </list>
    /// </summary>
    /// <param name="fc">釣りコントローラ。</param>
    /// <param name="settings">設定一式。</param>
    /// <returns>確保できた（＝寄せ始めた／登録待ちに入った）なら true。</returns>
    private bool Summon(FishingController fc, in Settings settings)
    {
        if (fc.HookedFish is not { } hooked) { return false; }

        // 遅延（DelaySeconds）を数えているあいだに、通常のわらしべ連鎖で怪獣まで
        // 乗り換わっていることがある。その場合は呼ぶ必要がないので取りやめる
        // （呼ぶと「怪獣が怪獣を食う」ことになってしまう）。
        if (IsKaiju(hooked, settings.PrefabKey))
        {
            SEED.Debug.Log("[KaijuLure] 遅延中に怪獣が掛かったため呼び出しを取りやめた");
            return false;
        }

        var spawnPosition = OffshorePosition(fc, hooked, settings.SpawnDistanceMeters);

        // 1) 既に居る怪獣を使い回す（余分なアクタを増やさない）
        if (FindIdleKaiju(settings.PrefabKey) is { } existing)
        {
            PullCloser(existing, hooked, spawnPosition, settings.SpawnDistanceMeters);
            BeginLure(existing, settings, "既に実体化していた個体を使う");
            return true;
        }

        // 2) 新しく実体化する
        if (FishManager.Current is not { } manager)
        {
            SEED.Debug.LogWarning("[KaijuLure] FishManager が居ないため怪獣を出せない");
            return false;
        }

        if (!manager.TryMaterializeByPrefabKey(settings.PrefabKey, spawnPosition, out var actor))
        {
            SEED.Debug.LogWarning(
                $"[KaijuLure] 怪獣を実体化できなかった（prefab キー \"{settings.PrefabKey}\" を"
              + " 含む .actor が FishManager の levels に無い可能性）");
            return false;
        }

        // 生成した瞬間から「餌に関わっている魚」として登録しておく。
        // Fish スクリプトはまだ走っていないので自分では登録できず、登録が無い 1 フレームの
        // あいだに FishManager の円環クランプ（LateUpdate）で出現円環まで引き戻されてしまう。
        fc.RegisterEngaged(actor);

        summonedActor = actor;
        waitedFrames  = NoWaitedFrames;
        phase         = Phase.Waiting;

        SEED.Debug.Log(
            $"[KaijuLure] 怪獣を実体化: {settings.PrefabKey} を"
          + $" ({spawnPosition.x:F1}, {spawnPosition.z:F1}) へ"
          + $"（掛かっている魚から沖側 {settings.SpawnDistanceMeters:F1}m）");
        return true;
    }

    /// <summary>
    /// 実体化した怪獣の <see cref="Fish"/> スクリプトが登録されるのを待つ。
    /// アクタ生成の次フレーム以降に <c>Fish.OnStart</c> が走って
    /// <see cref="Fish.All"/> へ載るので、それを突き合わせて掴む。
    /// </summary>
    /// <param name="fc">釣りコントローラ。</param>
    /// <param name="settings">設定一式。</param>
    private void UpdateWaiting(FishingController fc, in Settings settings)
    {
        waitedFrames++;

        // 生成した怪獣が既に消えている（別の理由で破棄された）なら諦める
        if (!summonedActor.IsValid)
        {
            SEED.Debug.LogWarning("[KaijuLure] 実体化した怪獣が消えたため呼び出しを中止した");
            summonedActor = default;
            phase = Phase.Idle;
            return;
        }

        if (FindFishScript(summonedActor) is { } fish)
        {
            summonedActor = default;
            BeginLure(fish, settings, "実体化した個体を使う");
            return;
        }

        if (waitedFrames < SpawnWaitFrameLimit) { return; }

        SEED.Debug.LogWarning(
            $"[KaijuLure] {SpawnWaitFrameLimit} フレーム待っても Fish スクリプトが見つからないため中止した");
        fc.UnregisterEngaged(summonedActor);
        summonedActor.Destroy();
        summonedActor = default;
        phase = Phase.Idle;
    }

    /// <summary>
    /// 寄せている最中の見張り。実際の接近・食いつきは <see cref="Fish"/> 側と
    /// わらしべ連鎖の既存経路が行うので、ここでは<b>終わり方を 2 つだけ</b>見る。
    ///
    /// <list type="number">
    ///   <item>掛かっているのが既に怪獣になった ―― 呼び出しの目的は果たされたので終える。
    ///         自分の怪獣が食いついた場合は <see cref="NotifyEaten"/> が先に畳むので、
    ///         ここへ来るのは<b>別の怪獣に先を越された</b>場合。寄せ続けると、
    ///         レベル差の判定を素通りするせいで「怪獣が怪獣を食う」ことになってしまう。</item>
    ///   <item>寄せていた怪獣そのものが消えた（別経路で破棄された）</item>
    /// </list>
    /// </summary>
    /// <param name="hooked">いま掛かっている魚。</param>
    /// <param name="settings">設定一式。</param>
    private void UpdateLuring(Fish hooked, in Settings settings)
    {
        // 1) 既に怪獣が掛かっている（別の怪獣に先を越された）なら目的は果たされている
        if (IsKaiju(hooked, settings.PrefabKey))
        {
            Cancel("既に別の怪獣が掛かった");
            return;
        }

        // 2) 寄せていた怪獣が生きている限り、あとは魚側に任せる
        if (luredFish is { } lured && lured.Actor.IsValid) { return; }

        SEED.Debug.LogWarning("[KaijuLure] 寄せていた怪獣が居なくなったため呼び出しを終えた");
        luredFish = null;
        phase = Phase.Idle;
    }

    /// <summary>
    /// 怪獣に「強制的に寄れ」と伝えて <see cref="Phase.Luring"/> へ入る
    /// 【寄せ開始の唯一の入口】。
    /// </summary>
    /// <param name="kaiju">寄せる怪獣。</param>
    /// <param name="settings">設定一式。</param>
    /// <param name="how">ログに残す確保のしかた。</param>
    private void BeginLure(Fish kaiju, in Settings settings, string how)
    {
        luredFish = kaiju;
        phase     = Phase.Luring;
        kaiju.BeginLure(settings.SpeedMultiplier);

        SEED.Debug.Log(
            $"[KaijuLure] 怪獣を呼んだ: {kaiju.DisplayName}（Lv{kaiju.Level}）"
          + $" 速度 ×{settings.SpeedMultiplier:F1}（{how}）");
    }

    /// <summary>
    /// 怪獣が掛かっている魚を食べた（わらしべで乗り換わった）ことを受け取る
    /// 【食いつき成立の受け口】。
    ///
    /// <c>FishingController.SwapHookedFish</c> から呼ばれる。乗り換わった魚が
    /// 自分の寄せていた怪獣なら、その場で寄せを終える（以後は普通の「掛かっている魚」）。
    /// 関係ない魚（別の格上が横取りした）なら何もしない ―― 怪獣はまだ寄り続けてよい。
    /// </summary>
    /// <param name="eater">掛かっている魚を食べた魚。</param>
    public void NotifyEaten(Fish eater)
    {
        if (luredFish is not { } lured || !ReferenceEquals(lured, eater)) { return; }

        SEED.Debug.Log($"[KaijuLure] 怪獣が食いついた: {eater.DisplayName}（Lv{eater.Level}）へ乗り換え");
        lured.EndLure();
        luredFish = null;
        phase = Phase.Idle;
    }

    /// <summary>
    /// 呼び出しを解除する【解除の唯一の出口】。
    ///
    /// 寄せている最中の怪獣は<b>破棄しない</b>（<see cref="Fish.EndLure"/> で通常の回遊へ戻す）。
    /// 実体化を待っている途中だった場合だけ、行き場のないアクタを片付ける。
    /// 何も進行していなければ何もしない（ログも出さない）ので、毎フレーム呼んでよい。
    /// </summary>
    /// <param name="reason">ログに残す解除理由。</param>
    public void Cancel(string reason)
    {
        if (phase == Phase.Idle) { return; }

        if (luredFish is { } lured)
        {
            if (lured.Actor.IsValid) { lured.EndLure(); }
            luredFish = null;
        }

        // 登録待ちのまま解除された怪獣は、誰も面倒を見ないので片付ける
        // （Fish スクリプトが乗る前なので、自力で回遊へ戻ることができない）。
        if (summonedActor.IsValid)
        {
            FishingController.Current?.UnregisterEngaged(summonedActor);
            summonedActor.Destroy();
        }
        summonedActor  = default;

        delayRemaining = 0f;
        waitedFrames   = NoWaitedFrames;
        phase          = Phase.Idle;

        SEED.Debug.Log($"[KaijuLure] 呼び出しを解除した（{reason}）");
    }

    // ─── 補助（探索・位置決め）─────────────────────────────────

    /// <summary>
    /// 指定の魚が怪獣か【怪獣判定の唯一の実装】。
    /// 判定は <c>KaijuStoryTrigger</c> と同じく「生成に使った .actor パスに
    /// 識別キーが含まれるか」で行う（同じキーを使うので両者の判定は必ず一致する）。
    /// </summary>
    /// <param name="fish">判定する魚。</param>
    /// <param name="prefabKey">怪獣の prefab 識別キー。</param>
    /// <returns>怪獣なら true。</returns>
    private static bool IsKaiju(Fish fish, string prefabKey)
    {
        if (string.IsNullOrWhiteSpace(prefabKey)) { return false; }
        if (FishManager.Current is not { } manager) { return false; }
        if (!fish.Actor.IsValid) { return false; }

        string path = manager.PrefabPathOf(fish.Actor);
        return path.Contains(prefabKey, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// いま実体化していて、かつ<b>まだ何にも関わっていない</b>怪獣を 1 匹探す。
    ///
    /// 回遊中・接近中・逃走中の個体だけを対象にする。つつき中・掛かっている・
    /// 釣り上げ演出中の個体を横取りすると、進行中のやり取りが壊れるため。
    /// </summary>
    /// <param name="prefabKey">怪獣の prefab 識別キー。</param>
    /// <returns>見つかった怪獣。居なければ null。</returns>
    private static Fish? FindIdleKaiju(string prefabKey)
    {
        foreach (var fish in Fish.All)
        {
            if (fish is null || !fish.Actor.IsValid) { continue; }
            if (fish.State is Fish.BehaviorState.Nibbling
                           or Fish.BehaviorState.Bite
                           or Fish.BehaviorState.Caught) { continue; }
            if (!IsKaiju(fish, prefabKey)) { continue; }
            return fish;
        }
        return null;
    }

    /// <summary>
    /// アクタに対応する <see cref="Fish"/> スクリプトを生存レジストリから引く。
    /// <c>SEED.GameObject</c> からスクリプト実体を直接引く API が無いため、
    /// <see cref="Fish.All"/> をエンティティ（インデックス＋世代）で突き合わせる。
    /// </summary>
    /// <param name="actor">探したい魚のアクタ。</param>
    /// <returns>見つかった魚。まだ登録されていなければ null。</returns>
    private static Fish? FindFishScript(SEED.GameObject actor)
    {
        foreach (var fish in Fish.All)
        {
            if (fish is null || !fish.Actor.IsValid) { continue; }
            if (fish.Actor.Entity.Index != actor.Entity.Index) { continue; }
            if (fish.Actor.Entity.Generation != actor.Entity.Generation) { continue; }
            return fish;
        }
        return null;
    }

    /// <summary>
    /// 使い回す怪獣が遠すぎるときだけ、出現位置まで引き寄せる
    /// 【使い回し個体の位置合わせの唯一の実装】。
    ///
    /// [なぜ要るか]
    /// 自然に実体化している怪獣は最上位レベルの円環＝<b>いちばん沖</b>に居るため、
    /// そのまま泳がせると到着まで数十秒かかり、先に釣り上がって「必ず食いつく」が
    /// 成立しない。到着までの時間を「出現距離 ÷ 速度」に揃えるため、
    /// 出現距離より遠い個体だけを出現位置へ移す。
    ///
    /// 出現距離より近い個体は<b>動かさない</b>（すでにもっと早く着くうえ、
    /// 画面に映っている個体を瞬間移動させると目に付くため）。
    /// 高さ（Y）は現在の遊泳深度のまま保つ（水面へ浮き上がらせない）。
    /// </summary>
    /// <param name="kaiju">使い回す怪獣。</param>
    /// <param name="hooked">掛かっている魚（距離を測る基準）。</param>
    /// <param name="spawnPosition">出現位置（沖側）。</param>
    /// <param name="spawnDistanceMeters">出現距離（メートル）。</param>
    private static void PullCloser(Fish kaiju, Fish hooked, SEED.Vector3 spawnPosition, float spawnDistanceMeters)
    {
        if (kaiju.Transform is not { IsValid: true } kaijuTransform) { return; }
        if (hooked.Transform is not { IsValid: true } preyTransform) { return; }

        var kaijuPosition = kaijuTransform.Position;
        var preyPosition  = preyTransform.Position;

        float dx = kaijuPosition.x - preyPosition.x;
        float dz = kaijuPosition.z - preyPosition.z;
        float distance = SEED.Mathf.Sqrt(dx * dx + dz * dz);
        if (distance <= spawnDistanceMeters) { return; }

        kaijuTransform.Position = new SEED.Vector3(spawnPosition.x, kaijuPosition.y, spawnPosition.z);
        SEED.Debug.Log(
            $"[KaijuLure] 使い回す怪獣が遠い（{distance:F1}m）ため、出現距離"
          + $"（{spawnDistanceMeters:F1}m）まで引き寄せた");
    }

    /// <summary>
    /// 怪獣を出す位置を決める【出現位置の唯一の計算】。
    ///
    /// 「竿先 → 掛かっている魚」の水平方向をそのまま延長した先＝<b>沖側</b>に置く。
    /// 岸側に出すとプレイヤーの背後から寄ってくる形になり、寄ってくる姿が見えないため。
    /// 高さ（Y）は <see cref="FishManager"/> の「生成する高さ」で上書きされるので、
    /// ここでは水平位置だけを決める。
    /// </summary>
    /// <param name="fc">釣りコントローラ（竿先の位置を読む）。</param>
    /// <param name="hooked">掛かっている魚（沖方向の基準点）。</param>
    /// <param name="distanceMeters">魚から沖側へ離す距離（メートル）。</param>
    /// <returns>怪獣を出すワールド座標。</returns>
    private static SEED.Vector3 OffshorePosition(FishingController fc, Fish hooked, float distanceMeters)
    {
        var preyPosition = hooked.Transform is { IsValid: true } t ? t.Position : fc.FloatWorldPosition;

        var fromRod = preyPosition - fc.RodTipWorldPosition;
        var flat    = new SEED.Vector3(fromRod.x, 0f, fromRod.z);
        var offshore = flat.SqrMagnitude > SqrEpsilon ? flat.Normalized : FallbackOffshoreDirection;

        float distance = SEED.Mathf.Max(distanceMeters, 0f);
        return new SEED.Vector3(
            preyPosition.x + offshore.x * distance,
            preyPosition.y,
            preyPosition.z + offshore.z * distance);
    }
}
