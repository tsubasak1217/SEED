using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 開発中の動作確認だけを目的とした<b>デバッグキー</b>をまとめたスクリプト
/// 【キー押下によるデバッグ操作の唯一の置き場】。
///
/// <b>シーン直下の空アクタ「DebugCommands」の Script スロットに付ける</b>。
///
/// [責務]
/// 「デバッグキーが押されたことを検出して、ゲーム側の公開 API を叩く」だけ。
/// ゲームの仕様（釣りの状態遷移・魚の生成規則）は一切持たず、必ず
/// <see cref="FishingController"/> / <see cref="FishManager"/> の公開 API を
/// 通常経路として経由する（＝ここを通した結果は本番と同じになる）。
///
/// [現在の機能]
/// <list type="bullet">
///   <item><see cref="hookKey"/>（既定 F9）… ヒットしていなければ <see cref="fishPrefabPath"/> の魚を
///         即ヒットさせ、<b>既にヒット中ならその魚をその場で釣り上げる</b></item>
/// </list>
///
/// [出荷時]
/// パッケージ版（assets.pak から起動した配布ビルド）では
/// <see cref="SEED.Application.IsDebugAllowed"/> が false になるため、
/// <see cref="OnStart"/> で自動的に丸ごと無効化される（アクタを外す必要は無い）。
/// エディタ実行中だけ、さらに手動で止めたいときに <see cref="enabled"/> を false にする。
/// </summary>
public class DebugCommands : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────────

    /// <summary>強制ヒット時の着水距離（メートル）の既定値。</summary>
    private const float DefaultHookDistanceMeters = 10.0f;

    /// <summary>魚を出す位置のばらつき半径（メートル）。0 ＝ 指定した中心ちょうど。</summary>
    private const float SpawnRadiusExact = 0.0f;

    /// <summary>
    /// 魚を実体化してから <see cref="Fish"/> スクリプトの登録（<see cref="Fish.All"/>）が
    /// 済むのを待つ上限フレーム数。魚アクタの <c>OnStart</c> は生成した次のフレーム以降に
    /// 走るため、生成直後は必ず 1 フレーム以上待つ必要がある。
    /// </summary>
    private const int SpawnWaitFrameLimit = 30;

    /// <summary>「待っている魚は無い」ことを表すフレーム数。</summary>
    private const int NoPendingFrames = -1;

    /// <summary>レベル添字の走査開始位置（levels は 0 始まり）。</summary>
    private const int FirstLevelIndex = 0;

    // ─── インスペクタ公開フィールド ──────────────────────────────

    /// <summary>
    /// デバッグキー全体の有効・無効。false の間は一切の入力を読まない
    /// （＝本番ビルドの誤爆を 1 か所で止められる）。
    /// </summary>
    [Header("全体"), SerializeField(Label = "有効", Tooltip = "false の間はデバッグキーを一切受け付けない")]
    public bool enabled = true;

    /// <summary>強制ヒットを起こすキー。</summary>
    [Header("強制ヒット"), SerializeField(Label = "強制ヒットキー", Tooltip = "押すと指定の魚を即座に掛ける")]
    public SEED.KeyCode hookKey = SEED.KeyCode.F9;

    /// <summary>
    /// 強制ヒットで掛ける魚の .actor パス。
    /// <see cref="FishManager"/> のレベル定義（levels）に登録済みの魚だけが対象
    /// （通常の出現経路をそのまま使うため、未登録の魚は掛けられない）。
    /// </summary>
    [SerializeField(Label = "魚アクタ", Tooltip = "強制ヒットで掛ける魚の .actor（FishManager の levels に登録済みのもの）"),
     AssetReference("actor")]
    public string fishPrefabPath = "";

    /// <summary>
    /// ウキが水上に無いときに、竿先から何メートル前方へ着水させるか。
    /// <see cref="FishingController.DebugForceHook"/> へそのまま渡す
    /// （向こう側でキャスト距離の最小〜最大へクランプされる）。
    /// ウキが既に水上にあるときは使われない（その場所でそのまま掛かる）。
    /// </summary>
    [SerializeField(Label = "着水距離(m)", Tooltip = "ウキが水上に無いとき、竿先から何 m 前方へ着水させるか")]
    public float hookDistanceMeters = DefaultHookDistanceMeters;

    // ─── 内部状態 ────────────────────────────────────────────────

    /// <summary>
    /// 実体化したものの、まだ <see cref="Fish"/> の登録を待っている魚のアクタ。
    /// </summary>
    private SEED.GameObject pendingFish;

    /// <summary>
    /// <see cref="pendingFish"/> を待ち始めてからの経過フレーム数。
    /// <see cref="NoPendingFrames"/> なら待っていない。
    /// </summary>
    private int pendingFrames = NoPendingFrames;

    /// <summary>
    /// このビルドでデバッグ機能を動かしてよいか（<see cref="OnStart"/> で確定する）。
    ///
    /// 値は実行中に変化しないので毎フレーム問い合わせず、ここへ 1 度だけ写し取る。
    /// false（＝パッケージ版）の間は <see cref="Update"/> が即座に戻るため、
    /// デバッグキーは一切読まれない。
    /// </summary>
    private bool debugAllowed;

    // ─── ライフサイクル ──────────────────────────────────────────

    /// <summary>
    /// 初期化。待ち状態を明示的に空にしておく（ホットリロード対策）。
    ///
    /// 併せて、このビルドでデバッグ機能が許されるかを確定する。
    /// パッケージ版では以降 <see cref="Update"/> が何もしないので、
    /// 生成待ちの魚が残らないよう待ち状態も必ず空にしてから抜ける。
    /// </summary>
    public override void OnStart()
    {
        debugAllowed = SEED.Application.IsDebugAllowed;
        ClearPending();

        if (!debugAllowed)
        {
            SEED.Debug.Log("[Debug] パッケージ版のためデバッグキーを無効化した");
        }
    }

    /// <summary>破棄直前の後始末。待ちのまま消えても参照を残さない。</summary>
    public override void OnDestroy()
    {
        ClearPending();
    }

    /// <summary>
    /// 毎フレームの処理。キー入力の検出と、生成待ちの魚の掛け直しを行う。
    /// </summary>
    /// <param name="ctx">フレーム情報（ここでは使わない）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // パッケージ版では丸ごと無効（誤爆をここ 1 か所で止める）
        if (!debugAllowed || !enabled) { return; }

        // 生成待ちがある間はキー入力を読まない（多重に魚を湧かせないため）
        if (pendingFrames != NoPendingFrames)
        {
            UpdatePendingHook();
            return;
        }

        if (SEED.Input.GetKeyDown(hookKey)) { RequestForceHookOrCatch(); }
    }

    // ─── デバッグキーの振り分け ──────────────────────────────────

    /// <summary>
    /// <see cref="hookKey"/> が押されたときの振り分け【デバッグキーの唯一の入口】。
    ///
    /// <code>
    /// ヒット中     → その魚をその場で釣り上げる（FishingController.DebugForceCatch）
    /// ヒットしていない → 従来どおり魚を湧かせて強制ヒットさせる（RequestForceHook）
    /// </code>
    /// 釣り上げ側は本番と同じ経路（釣り上げ演出・図鑑登録・イベント）を通るので、
    /// ここには「どちらを呼ぶか」以外の仕様を持たせない。
    /// </summary>
    private void RequestForceHookOrCatch()
    {
        if (FishingController.Current is { IsHooked: true } hooked)
        {
            if (!hooked.DebugForceCatch())
            {
                SEED.Debug.LogWarning("[Debug] 強制釣り上げ: 受け付けられなかった");
            }
            return;
        }

        RequestForceHook();
    }

    // ─── 強制ヒット ──────────────────────────────────────────────

    /// <summary>
    /// 強制ヒットを要求する【デバッグキーからの唯一の入口】。
    ///
    /// 掛けられない状況（前提が揃っていない）のときは<b>何も変更せず</b>
    /// 理由をログに出して戻る。デバッグ機能がゲーム状態を壊さないための原則。
    /// </summary>
    private void RequestForceHook()
    {
        if (FishingController.Current is not { } controller)
        {
            SEED.Debug.LogWarning("[Debug] 強制ヒット: FishingController が居ないため何もしない");
            return;
        }

        // 既にヒット中なら無視する（乗り換えは「わらしべ」の仕様なのでここでは扱わない）
        if (controller.IsHooked)
        {
            SEED.Debug.Log("[Debug] 強制ヒット: 既にヒット中のため無視した");
            return;
        }

        if (string.IsNullOrWhiteSpace(fishPrefabPath))
        {
            SEED.Debug.LogWarning("[Debug] 強制ヒット: 魚アクタが未設定のため何もしない");
            return;
        }

        if (FishManager.Current is not { } manager)
        {
            SEED.Debug.LogWarning("[Debug] 強制ヒット: FishManager が居ないため何もしない");
            return;
        }

        // 魚を出す位置は「ウキ（餌）の位置」。ウキがまだ水上に無いときは着水点が
        // 決まっていないので竿先を中心に出す（掛かった後の魚はウキへ追従するので、
        // 出現位置の違いは掛かった時点で吸収される）。
        var spawnCenter = controller.BaitActive
            ? controller.BaitPosition
            : controller.RodTipWorldPosition;

        if (!TrySpawnFish(manager, spawnCenter, out var spawned))
        {
            SEED.Debug.LogWarning(
                $"[Debug] 強制ヒット: 魚を生成できない（FishManager の levels に \"{fishPrefabPath}\" が無い可能性）");
            return;
        }

        // Fish スクリプトの OnStart は次フレーム以降に走るので、登録されるまで待つ
        pendingFish   = spawned;
        pendingFrames = 0;
        SEED.Debug.Log($"[Debug] 強制ヒット: {fishPrefabPath} を生成した（掛かるまで待機）");
    }

    /// <summary>
    /// 指定した .actor の魚を 1 匹、通常の生成経路（<see cref="FishManager"/>＋プール）で
    /// 実体化する【魚種を指名した生成の唯一の実装】。
    ///
    /// <see cref="FishManager"/> には「.actor パスを直接指定して出す」公開 API が無いため、
    /// チュートリアルが使う魚種フィルタ（<see cref="TutorialRules.FishPrefabFilter"/> の
    /// 許可リスト指定）を<b>この呼び出しの間だけ</b>立てて、レベルを総当たりする。
    /// 許可リスト指定中は「一致する魚が居ないレベル」では 1 匹も出ないので、
    /// 指定した魚が属するレベルでだけ生成が成立する。
    ///
    /// フィルタの値は関数を抜ける前に必ず元へ戻す（同期的に完結するので、
    /// 途中で <see cref="FishManager"/> の毎フレーム処理が割り込むことはない）。
    /// </summary>
    /// <param name="manager">生成を行うマネージャ。</param>
    /// <param name="center">出現の中心（ワールド座標。Y はマネージャ側の生成高さで上書きされる）。</param>
    /// <param name="fish">生成できた魚のアクタ（失敗時は無効ハンドル）。</param>
    /// <returns>生成できたら true。</returns>
    private bool TrySpawnFish(FishManager manager, SEED.Vector3 center, out SEED.GameObject fish)
    {
        fish = default;

        // チュートリアルが使っている設定を退避する（デバッグが本編の進行を壊さないため）
        bool   savedActive    = TutorialRules.Active;
        string savedFilter    = TutorialRules.FishPrefabFilter;
        bool   savedExclusive = TutorialRules.FishPrefabExclusive;

        TutorialRules.Active              = true;
        TutorialRules.FishPrefabFilter    = fishPrefabPath;
        TutorialRules.FishPrefabExclusive = true;

        try
        {
            // レベルを総当たりする。フィルタに一致しないレベルでは生成が成立しない。
            int levelCount = manager.PooledLevelCount;
            for (int levelIndex = FirstLevelIndex; levelIndex < levelCount; levelIndex++)
            {
                if (manager.SpawnOneNear(levelIndex, center, SpawnRadiusExact, out var spawned))
                {
                    fish = spawned;
                    return true;
                }
            }
            return false;
        }
        finally
        {
            TutorialRules.Active              = savedActive;
            TutorialRules.FishPrefabFilter    = savedFilter;
            TutorialRules.FishPrefabExclusive = savedExclusive;
        }
    }

    /// <summary>
    /// 生成待ちの魚が <see cref="Fish.All"/> へ登録されたかを毎フレーム確かめ、
    /// 登録され次第ヒットさせる【強制ヒットの成立点】。
    /// 上限フレームを過ぎても見つからなければ諦めて魚を片付ける。
    /// </summary>
    private void UpdatePendingHook()
    {
        pendingFrames++;

        // 生成した魚が既に消えている（別の理由で破棄された）なら諦める
        if (!pendingFish.IsValid)
        {
            SEED.Debug.LogWarning("[Debug] 強制ヒット: 生成した魚が消えたため中止した");
            ClearPending();
            return;
        }

        if (FindFishScript(pendingFish) is { } fish)
        {
            ForceHook(fish);
            ClearPending();
            return;
        }

        if (pendingFrames < SpawnWaitFrameLimit) { return; }

        SEED.Debug.LogWarning(
            $"[Debug] 強制ヒット: {SpawnWaitFrameLimit} フレーム待っても Fish スクリプトが見つからないため中止した");
        pendingFish.Destroy();
        ClearPending();
    }

    /// <summary>
    /// 生成した魚を、釣りコントローラの強制ヒット入口へ渡す。
    ///
    /// 着水・判定・SE・バナー・やり取りの開始はすべて
    /// <see cref="FishingController.DebugForceHook"/> の中で本番と同じ手順で起きる
    /// （このスクリプトはゲームの仕様を持たない）。掛からなかったときは、
    /// 湧かせただけの魚が残らないよう必ず片付ける。
    /// </summary>
    /// <param name="fish">掛ける魚。</param>
    private void ForceHook(Fish fish)
    {
        if (FishingController.Current is not { } controller)
        {
            SEED.Debug.LogWarning("[Debug] 強制ヒット: 待っている間に FishingController が居なくなった");
            fish.Actor.Destroy();
            return;
        }

        if (!controller.DebugForceHook(fish, hookDistanceMeters))
        {
            SEED.Debug.LogWarning("[Debug] 強制ヒット: 掛けられなかったため生成した魚を片付けた");
            fish.Actor.Destroy();
            return;
        }

        SEED.Debug.Log($"[Debug] 強制ヒット: {fish.DisplayName}（Lv{fish.Level}）を掛けた");
    }

    /// <summary>
    /// アクタに対応する <see cref="Fish"/> スクリプトを生存レジストリから引く。
    ///
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

    /// <summary>生成待ちの状態を空へ戻す【待ち状態の解除の唯一の出口】。</summary>
    private void ClearPending()
    {
        pendingFish   = default;
        pendingFrames = NoPendingFrames;
    }
}
