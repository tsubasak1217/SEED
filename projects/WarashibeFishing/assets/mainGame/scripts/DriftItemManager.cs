using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 漂流物の<b>種類</b>の決め方【出現ルールの唯一の列挙】。
///
/// 出現位置の抽選はこの設定に関係なく常にランダムで、
/// ここが決めるのは「次に出すのがどの種類か」だけである。
/// </summary>
public enum DriftSpawnRule
{
    /// <summary>
    /// 糸 HP に応じた重み付き抽選（既定）。
    /// 糸が減っているほど糸回復が、残っているほど魚回復が出やすくなる。
    /// ひるませ（スタン）は糸 HP に関係なく一定割合で出る。
    /// </summary>
    LineHpWeighted,

    /// <summary>
    /// 出現順（<see cref="DriftItemManager"/> の「出現順」）を先頭から 1 個ずつ巡回する固定順。
    /// 「ひるませ 2 回につき回復 1 回」のような比率をデータの並びだけで作りたいときに使う。
    /// </summary>
    FixedOrder,
}

/// <summary>
/// 漂流物（<see cref="DriftItem"/>）の<b>出現と一括片付けだけ</b>を司るスクリプト
/// 【漂流物の生成の唯一の入口】。
///
/// <b>シーン直下の空アクタ「DriftItems」の Script スロットに付ける</b>。
///
/// [責務の分担]
/// <list type="bullet">
///   <item>本スクリプト … いつ・どこに・どの種類を出すか／いつ全部消すか</item>
///   <item><see cref="DriftItem"/> … 1 個の漂い・浮き・寿命</item>
///   <item><see cref="FishingController"/> … ウキが巻き込んだ判定と効果の適用</item>
/// </list>
///
/// [出現の規則]
/// <code>
/// ヒット中（FishingController.Current.IsHooked）で、かつ岸際でない
/// （FishingController.NearShore が false）のあいだだけ
///   spawnIntervalSeconds ごとに 1 個、DriftItem.All.Count が maxItems 未満なら生成する
///   位置 = 「ウキから竿先の方向へ shoreShiftMeters だけ寄せた点」を中心とした
///         spawnRadiusMin〜spawnRadiusMax の円環内のランダムな一点（水面上）
///         ただし竿先（＝岸側）から spawnRadiusMin より近い点は捨てて引き直す
///   種類 = 「出現の決め方」に従う（既定: 糸 HP 連動の重み付き抽選。
///          FixedOrder なら spawnOrder を先頭から 1 個ずつ巡回）
/// ヒットしていないあいだは、残っている漂流物をすべて消す
/// </code>
///
/// 生存個体の管理簿は <see cref="DriftItem.All"/> 一本に統一してある
/// （マネージャ側で別のリストを持つと、寿命による自滅と二重管理になるため）。
/// </summary>
public class DriftItemManager : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────────

    /// <summary>1 回転（ラジアン）。出現方位のランダム抽選に使う。</summary>
    private const float FullTurnRadians = 6.2831853f;

    /// <summary>出現位置の抽選をやり直す最大回数（竿先に近すぎる点を弾くため）。</summary>
    private const int SpawnPositionRetryCount = 8;

    /// <summary>0 除算を避けるための「実質 0 メートル」しきい値（向きが定まらない判定に使う）。</summary>
    private const float DivideEpsilon = 1e-4f;

    /// <summary>1 フレームに生成する個数の上限（生成が一気に固まらないようにする）。</summary>
    private const int SpawnPerTick = 1;

    /// <summary>アセット仮想パスの接頭辞。prefab パスに付いていなければ補う。</summary>
    private const string AssetSchemePrefix = "assets://";

    /// <summary>割合・重みの下限（0）。抽選の各値をここから上へ丸める。</summary>
    private const float RatioMin = 0f;

    /// <summary>割合の上限（1 ＝ 100%）。糸 HP（0〜1）の上限も兼ねる。</summary>
    private const float RatioMax = 1f;

    /// <summary>
    /// 糸 HP を読めない（＝ヒットしていない）ことを表す値。
    /// 0〜1 に収まらない負値なので、正規の糸 HP と取り違えることがない。
    /// </summary>
    private const float NoLineHp = -1f;

    /// <summary>糸 HP を読めないときに糸回復・魚回復へ与える等しい重み（＝ 0.5 / 0.5 になる）。</summary>
    private const float EqualRecoverWeight = 1f;

    /// <summary>0〜1 の割合をログへ「%」で出すときの倍率。</summary>
    private const float PercentScale = 100f;

    // ─── prefab（データドリブン: 種類を増やすときはここへ足す）──────────

    /// <summary>漂流物「ひるませ（スタン）」の prefab（.actor）パス。</summary>
    [Header("prefab"), SerializeField(Label = "ひるませの prefab パス")]
    private string stunPrefabPath = "assets://mainGame/actors/Drift/DriftStun.actor";

    /// <summary>漂流物「魚HPの回復」の prefab（.actor）パス。</summary>
    [SerializeField(Label = "魚回復の prefab パス")]
    private string fishRecoverPrefabPath = "assets://mainGame/actors/Drift/DriftFishRecover.actor";

    /// <summary>漂流物「糸の回復」の prefab（.actor）パス。</summary>
    [SerializeField(Label = "糸回復の prefab パス")]
    private string lineRecoverPrefabPath = "assets://mainGame/actors/Drift/DriftLineRecover.actor";

    // ─── 出現パラメータ ─────────────────────────────────────

    /// <summary>同時に存在できる漂流物の最大数。</summary>
    [Header("出現"), SerializeField(Label = "同時出現数の上限")]
    private int maxItems = 6;

    /// <summary>生成の間隔（秒）。上限に達しているあいだは待つだけで消費しない。</summary>
    [SerializeField(Label = "生成間隔(秒)")]
    private float spawnIntervalSeconds = 2.5f;

    /// <summary>出現距離の下限（メートル）。円環の中心に近すぎる位置には湧かせない。</summary>
    [SerializeField(Label = "出現距離の下限(m)")]
    private float spawnRadiusMin = 4f;

    /// <summary>出現距離の上限（メートル）。</summary>
    [SerializeField(Label = "出現距離の上限(m)")]
    private float spawnRadiusMax = 14f;

    /// <summary>
    /// 円環の中心を<b>ウキから竿先（岸）の方向へ寄せる量</b>（メートル）
    /// 【漂流物を「少し岸側」に寄せる唯一のパラメータ】。
    ///
    /// 拾えるのはウキとの接触だけ（<c>FishingController.UpdateDriftPickup</c>）なので、
    /// ウキを中心に湧かせると半分は「これから巻く側」ではなく沖側に出てしまい、
    /// 巻いても近づかないぶんが無駄になる。中心を岸側へ寄せると
    /// 「これから通る側」に出る割合が増える。
    /// 0 にすると従来どおりウキ中心の円環になる。
    /// </summary>
    [SerializeField(Label = "岸側へのずらし(m)")]
    private float shoreShiftMeters = 4.0f;

    /// <summary>
    /// 出現する種類の決め方【種類決定の分岐の唯一のデータ】。
    ///
    /// <see cref="DriftSpawnRule.LineHpWeighted"/>（既定）は糸 HP に連動した重み付き抽選、
    /// <see cref="DriftSpawnRule.FixedOrder"/> は「出現順」の固定巡回になる。
    /// </summary>
    [Header("出現の決め方"), SerializeField(Label = "出現の決め方")]
    private DriftSpawnRule spawnRule = DriftSpawnRule.LineHpWeighted;

    /// <summary>
    /// 糸 HP 連動の抽選で「ひるませ」が出る割合（0〜1）【ひるませの固定確率】。
    ///
    /// ひるませは糸 HP と無関係に一定割合で出したいので、まずこのぶんを取り置き、
    /// <b>残り（1 − この値）</b>を糸回復と魚回復で分け合う。
    /// </summary>
    [SerializeField(Label = "ひるみの割合")]
    private float stunRatio = 0.34f;

    /// <summary>
    /// 糸回復・魚回復の重みへ一律に足す最小重み【片方が 0% になるのを防ぐ唯一の値】。
    ///
    /// 重みは「糸回復 = 1 − 糸HP」「魚回復 = 糸HP」なので、糸 HP が満タン（1.0）だと
    /// 糸回復の重みが 0 になり<b>二度と出なくなる</b>。両方へこの値を足しておくと、
    /// 満タンでも糸回復が「この値 ÷ (1 + この値×2)」相当の割合で出る
    /// （既定 0.15 なら残り枠の約 11.5%）。
    /// </summary>
    [SerializeField(Label = "回復の最小重み")]
    private float minRecoverWeight = 0.15f;

    /// <summary>
    /// 出現する種類の<b>順番</b>【種類決定の唯一のデータ】。
    /// 先頭から 1 個ずつ順に出し、末尾まで行ったら先頭へ戻る（＝固定順の巡回）。
    ///
    /// 【2026-09-11 変更】以前は重み付きのランダム抽選（stunWeight ほか 2 つ）だったが、
    /// 「ひるませばかり続いて回復が来ない」といった偏りが体験を壊すため、
    /// <b>種類はデータで決めた固定順・ランダムなのは出現位置だけ</b>に変えた。
    /// 旧フィールドは削除済みなので、シーン（.scene）に stunWeight などの保存値が
    /// 残っていても<b>読まれない</b>（害は無いので放置してよい）。
    ///
    /// 入れられる文字列は <see cref="DriftItem.KindStun"/> /
    /// <see cref="DriftItem.KindFishRecover"/> / <see cref="DriftItem.KindLineRecover"/>
    /// （＝ "stun" / "fish_recover" / "line_recover"）。未知の文字列は警告を出して読み飛ばす。
    /// 同じ種類を複数回並べれば「ひるませ 2 回につき回復 1 回」のような比率も
    /// データを並べ替えるだけで作れる。
    /// </summary>
    [SerializeField(Label = "出現順")]
    private string[] spawnOrder =
    {
        DriftItem.KindStun,
        DriftItem.KindFishRecover,
        DriftItem.KindLineRecover,
    };

    /// <summary>
    /// ヒット中だけ漂流物を出すか。
    /// true（既定）＝ヒットしていないあいだは 1 個も出さず、残りも全部消す。
    /// false にすると常時漂わせられる（デバッグ・演出用）。
    /// </summary>
    [SerializeField(Label = "ヒット中だけ出す")]
    private bool activeOnlyWhileHooked = true;

    // ─── 実行時の内部状態 ───────────────────────────────────

    /// <summary>次の生成までの残り秒数。</summary>
    private float spawnTimer = 0f;

    /// <summary>
    /// 次に出す <see cref="spawnOrder"/> の添字（＝固定順のどこまで進んだか）。
    ///
    /// 【リセットの規則】<see cref="spawnTimer"/> と<b>まったく同じ場所</b>で 0 に戻す。
    /// つまり「出せない状態」（＝<see cref="ShouldSpawn"/> が false ＝ 既定設定では
    /// ヒットしていない／岸際）になったフレームで先頭に戻る。
    /// その結果、既定の「ヒット中だけ出す」設定では<b>1 回のやり取りごとに
    /// 必ず出現順の先頭から始まる</b>（毎回同じ順序で出るので調整しやすい）。
    /// 「ヒット中だけ出す」を false にした常時出現モードでは戻る機会が無いため、
    /// マネージャの生存期間を通してひたすら巡回し続ける。
    /// </summary>
    private int spawnOrderIndex = 0;

    /// <summary>片付けのために <see cref="DriftItem.All"/> を写す作業用リスト（毎フレームの確保を避ける）。</summary>
    private readonly List<DriftItem> workItems = new();

    // ─── 他スクリプトからの参照点（静的アクセサ）───────────────

    /// <summary>
    /// 現在シーンで動いている漂流物マネージャ（実質シングルトン）。
    ///
    /// チュートリアル（<c>TutorialDirector</c>）は動的に生成されるわけではないが、
    /// 「シーンにこのマネージャが居れば台本生成を頼む」という緩い結び付きにしたいので、
    /// <see cref="FishManager.Current"/> / <c>FishingController.Current</c> と同じ方式で公開する。
    /// ホットリロードでは静的フィールドごと作り直され OnStart が再実行されるため、
    /// 古い参照が残ることはない。
    /// </summary>
    public static DriftItemManager? Current { get; private set; } = null;

    // ─── ライフサイクル ────────────────────────────────────

    /// <summary>
    /// 最初の生成をすぐ行わないよう、生成間隔ぶん待ってから始める。
    /// 出現順の位置も先頭に戻す（ホットリロード後も必ず同じ順で出る）。
    /// </summary>
    public override void OnStart()
    {
        Current = this;
        spawnTimer = spawnIntervalSeconds;
        spawnOrderIndex = 0;
    }

    /// <summary>破棄されるときは残った漂流物も片付ける（シーン遷移で置き去りにしない）。</summary>
    public override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) { Current = null; }
        ClearAll();
    }

    /// <summary>
    /// 毎フレームの更新。出す条件が成り立っているあいだだけ生成し、
    /// 成り立たなくなったら残りを全部消す。
    /// </summary>
    /// <param name="ctx">フレーム情報（経過秒数を読む）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        float dt = ctx.DeltaTime;
        if (dt <= 0f) { return; }

        if (!ShouldSpawn())
        {
            // やり取りが終わった（糸切れ・釣り上げ・キャンセル）: 残りを一掃して待機に戻る。
            // ただし台本が位置を決めて置いた個体（ScriptedPlacementActive）は残す。
            if (!ScriptedPlacementActive()) { ClearAll(); }
            spawnTimer = spawnIntervalSeconds;

            // 出現順もここで先頭に戻す（次のやり取りは必ず spawnOrder[0] から始まる）。
            // 待機に戻ったら次の 1 個目まで満タンの間隔を空ける spawnTimer と足並みを揃えて
            // おきたいので、2 つの状態は必ず同じ場所でリセットする。
            spawnOrderIndex = 0;
            return;
        }

        spawnTimer -= dt;
        if (spawnTimer > 0f) { return; }

        spawnTimer = SEED.Mathf.Max(spawnIntervalSeconds, 0f);

        for (int i = 0; i < SpawnPerTick; i++)
        {
            if (DriftItem.All.Count >= SEED.Mathf.Max(maxItems, 0)) { return; }
            SpawnOne();
        }
    }

    // ─── 内部処理: 出す条件 ──────────────────────────────────

    /// <summary>
    /// いま漂流物を出してよいか【出現条件の唯一の判断点】。
    /// <see cref="activeOnlyWhileHooked"/> が true なら「ヒット中」であることが必須。
    /// </summary>
    private bool ShouldSpawn()
    {
        // チュートリアルのミッションが「漂流物なし」と言っていれば自然出現は止める
        // （台本による明示生成 SpawnScripted は別経路なので影響を受けない）。
        // 上書きが無ければこの分岐は素通りし、従来どおり出現する。
        if (TutorialRules.Active && TutorialRules.DriftDisabled) { return false; }

        if (FishingController.Current is not { } controller) { return false; }

        // 岸際（ウキが竿先の近く）では新規出現を止める【2026-09-09 追加】。
        // 拾う余地が無いところに湧かせても、巻き切るまでの数秒で
        // 画面手前にゴミが増えるだけになるため。既に浮いている個体は残す
        // （拾える位置に居るものを目の前で消すほうが不自然）。
        if (controller.NearShore) { return false; }

        if (!activeOnlyWhileHooked) { return true; }
        return controller.IsHooked;
    }

    /// <summary>
    /// いま海に浮いている漂流物が<b>台本（チュートリアル）の置いた個体</b>か
    /// 【一括片付けを見送る唯一の判断点】。
    ///
    /// 【なぜ必要か】
    /// チュートリアルの「漂流物を拾おう」ミッションは、自然出現を止めたうえで
    /// （<see cref="TutorialRules.DriftDisabled"/>）自分で位置を決めて並べる。
    /// ところが自然出現の停止は <see cref="ShouldSpawn"/> を false にするため、
    /// 何もしないと<b>並べた次のフレームに <see cref="ClearAll"/> が全部消して</b>しまい、
    /// ミッション側の「海に 1 個も無ければ並べ直す」自己回復と噛み合って
    /// 「出ては消える」を延々と繰り返す（＝いつまでも拾えない）。
    ///
    /// 「自然出現を止める」と「浮いている物を一掃する」は別の判断なので、
    /// 台本が留めると宣言している間（<see cref="TutorialRules.DriftStationary"/>）は
    /// 一掃を見送り、片付けはチュートリアル側の解除（<see cref="TutorialRules.Clear"/>）に任せる。
    /// </summary>
    /// <returns>台本が置いた個体を残すべきなら true。</returns>
    private static bool ScriptedPlacementActive()
        => TutorialRules.Active && TutorialRules.DriftStationary;

    // ─── 内部処理: 生成 ─────────────────────────────────────

    /// <summary>
    /// 漂流物を 1 個生成する。種類は <see cref="spawnOrder"/> の固定順、
    /// 位置は「少し岸側へ寄せた円環」内のランダムな一点。
    /// prefab が読めない・位置が決まらないときは静かに諦める（次の間隔でまた試す）。
    /// </summary>
    private void SpawnOne()
    {
        if (FishingController.Current is not { } controller) { return; }
        if (!TryPickSpawnPosition(controller, out var position)) { return; }

        SpawnAt(PickPrefabPath(), position);
    }

    /// <summary>
    /// 指定した prefab パスの漂流物を指定位置に 1 個生成する【生成の唯一の実体】。
    ///
    /// 通常の抽選生成も、チュートリアルの台本生成も必ずここを通るので、
    /// 出現イベント（<see cref="FishingEvents.DriftSpawn"/>）の発火もここ 1 か所で済む。
    /// </summary>
    /// <param name="prefabPath">生成する prefab（.actor）のパス。空なら何もしない。</param>
    /// <param name="position">生成位置（ワールド）。</param>
    /// <returns>生成できたら true。</returns>
    private bool SpawnAt(string prefabPath, SEED.Vector3 position)
    {
        string path = NormalizeAssetPath(prefabPath);
        if (string.IsNullOrEmpty(path)) { return false; }

        var obj = SEED.GameObject.Instantiate(path);
        if (!obj.IsValid)
        {
            SEED.Debug.LogWarning($"[DriftItemManager] 漂流物 prefab の読み込みに失敗しました: {path}");
            return false;
        }

        // 生成直後のフレームでも位置を反映しておく（Instantiate 直後の Transform 設定は有効）
        if (obj.GetComponent<SEED.Transform>() is { IsValid: true } t) { t.Position = position; }

        // 漂流物が 1 個出た（チュートリアルの手順送り・SE などが購読できる）
        SEED.Events.Raise(FishingEvents.DriftSpawn);
        return true;
    }

    // ─── 台本 API（チュートリアルから呼ぶ強制生成）─────────

    /// <summary>
    /// 種類を指定して漂流物を 1 個、指定位置に生成する【台本生成の唯一の入口】。
    ///
    /// 固定順の巡回（<see cref="PickPrefabPath"/>）を通さず種類を直接指定するので、
    /// 出現順の進み具合（<see cref="spawnOrderIndex"/>）にも一切影響しない。
    /// チュートリアルで「必ずこの種類の漂流物を出す」ことができる。
    /// 出現間隔・同時出現数の上限も見ない（説明の手順を確実に進めるため）。
    /// </summary>
    /// <param name="kind">漂流物の種類（<see cref="DriftItem.KindStun"/> など）。</param>
    /// <param name="position">生成位置（ワールド）。</param>
    /// <returns>生成できたら true。種類が未知・prefab が読めないときは false。</returns>
    public bool SpawnScripted(string kind, SEED.Vector3 position)
    {
        string path = PrefabPathOf(kind);
        if (string.IsNullOrEmpty(path))
        {
            SEED.Debug.LogWarning($"[DriftItemManager] 未知の漂流物の種類 \"{kind}\" が指定されました（生成しません）。");
            return false;
        }

        return SpawnAt(path, position);
    }

    /// <summary>
    /// 種類を指定して漂流物を 1 個、通常の出現範囲（少し岸側へ寄せた円環内）に生成する。
    /// 位置を自分で決められない呼び出し側（チュートリアル）はこちらを使う。
    /// </summary>
    /// <param name="kind">漂流物の種類（<see cref="DriftItem.KindStun"/> など）。</param>
    /// <returns>生成できたら true。釣り中でない・位置が決まらない場合は false。</returns>
    public bool SpawnScriptedNearFloat(string kind)
    {
        if (FishingController.Current is not { } controller) { return false; }
        if (!TryPickSpawnPosition(controller, out var position)) { return false; }

        return SpawnScripted(kind, position);
    }

    /// <summary>
    /// 種類の文字列から prefab パスを引く【種類とパスの唯一の対応表】。
    /// 未知の種類は空文字を返す。
    /// </summary>
    /// <param name="kind">漂流物の種類。</param>
    /// <returns>prefab パス（未知なら空文字）。</returns>
    private string PrefabPathOf(string kind) => kind switch
    {
        DriftItem.KindStun         => stunPrefabPath,
        DriftItem.KindFishRecover  => fishRecoverPrefabPath,
        DriftItem.KindLineRecover  => lineRecoverPrefabPath,
        _ => "",
    };

    /// <summary>
    /// 次に出す prefab のパスを 1 つ返す【種類決定の唯一の入口】。
    /// 決め方（<see cref="spawnRule"/>）で実装を切り替えるだけで、
    /// 実際の抽選・巡回はそれぞれの実装が持つ。
    /// </summary>
    /// <returns>生成する prefab のパス（出せる種類が 1 つも無ければ空文字）。</returns>
    private string PickPrefabPath() => spawnRule switch
    {
        DriftSpawnRule.FixedOrder => PickPrefabPathInOrder(),
        _                         => PickPrefabPathByLineHp(),
    };

    /// <summary>
    /// 糸 HP に応じた重み付き抽選で prefab のパスを 1 つ返す
    /// 【糸 HP 連動の抽選の唯一の実装】。
    ///
    /// <code>
    /// ひるませ = stunRatio（糸 HP と無関係の固定確率）
    /// 残り枠   = 1 − stunRatio  を次の重みで分ける
    ///   糸回復の重み = (1 − 糸HP) + minRecoverWeight   ← 糸が減っているほど大きい
    ///   魚回復の重み = 糸HP      + minRecoverWeight   ← 糸が残っているほど大きい
    /// ヒット外（糸 HP が無い）は 0.5 / 0.5
    /// </code>
    /// 重みの合計は必ず 1 以上になる（0 除算は起きない）。
    /// </summary>
    /// <returns>生成する prefab のパス（prefab パス未設定なら空文字）。</returns>
    private string PickPrefabPathByLineHp()
    {
        float line01    = CurrentLine01();
        float stun      = SEED.Mathf.Clamped(stunRatio, RatioMin, RatioMax);
        float minWeight = SEED.Mathf.Max(minRecoverWeight, RatioMin);
        bool  hasLine   = line01 >= RatioMin;

        // 糸 HP が読めないヒット外では、回復 2 種を等しい重み（＝ 0.5 / 0.5）にする
        float lineWeight = hasLine ? (RatioMax - line01) + minWeight : EqualRecoverWeight;
        float fishWeight = hasLine ? line01 + minWeight             : EqualRecoverWeight;

        float totalWeight  = lineWeight + fishWeight;
        float recoverRatio = RatioMax - stun;
        float lineChance   = recoverRatio * lineWeight / totalWeight;
        float fishChance   = recoverRatio * fishWeight / totalWeight;

        // 0〜1 の一様乱数を [ひるませ][糸回復][魚回復] の 3 区間へ割り当てる
        float roll = SEED.Random.Range(RatioMin, RatioMax);
        string kind = roll < stun                ? DriftItem.KindStun
                    : roll < stun + lineChance   ? DriftItem.KindLineRecover
                    :                              DriftItem.KindFishRecover;

        string lineText = hasLine ? $"{line01 * PercentScale:F0}%" : "なし";
        SEED.Debug.Log($"[DriftItemManager] 抽選: {kind}（糸HP {lineText} → ひるみ {stun * PercentScale:F0}%"
            + $" / 糸回復 {lineChance * PercentScale:F0}% / 魚回復 {fishChance * PercentScale:F0}%）");

        string path = PrefabPathOf(kind);
        if (string.IsNullOrEmpty(path))
        {
            SEED.Debug.LogWarning($"[DriftItemManager] 漂流物「{kind}」の prefab パスが未設定です（今回は生成しません）。");
        }
        return path;
    }

    /// <summary>
    /// いまのやり取りの糸 HP（0〜1）を読む【糸 HP を読む唯一の場所】。
    ///
    /// ヒットしていない（＝糸 HP という概念が無い）ときは <see cref="NoLineHp"/> を返す。
    /// <see cref="FishingFight.Line01"/> は<b>やり取りが終わっても最後の値を保持する</b>ので、
    /// 「掛かっているか」（<see cref="FishingController.IsHooked"/>）で必ず門を作ること。
    /// </summary>
    /// <returns>糸 HP（0〜1）。ヒットしていなければ <see cref="NoLineHp"/>。</returns>
    private static float CurrentLine01()
    {
        if (FishingController.Current is not { IsHooked: true } controller) { return NoLineHp; }
        if (controller.Fight is not { } fight) { return NoLineHp; }
        return SEED.Mathf.Clamped(fight.Line01, RatioMin, RatioMax);
    }

    /// <summary>
    /// <see cref="spawnOrder"/> を先頭から順に巡回して、次に出す prefab のパスを 1 つ返す
    /// 【固定順の巡回の唯一の実装】。
    ///
    /// 呼ぶたびに添字が 1 つ進み、末尾まで行ったら先頭へ戻る。
    /// 未知の種類名や prefab パス未設定の要素は警告を出して読み飛ばし、次の要素を試す。
    /// 「1 周まわしても出せる要素が 1 つも無い」ときだけ空文字（＝今回は生成しない）を返す。
    /// 試行回数を配列 1 周ぶんで必ず打ち切るので、全要素が不正でも無限ループにはならない。
    /// </summary>
    /// <returns>生成する prefab のパス（出せる種類が 1 つも無ければ空文字）。</returns>
    private string PickPrefabPathInOrder()
    {
        var order = spawnOrder;
        int count = order is null ? 0 : order.Length;
        if (count <= 0) { return ""; }

        // インスペクタで配列を短くされた直後でも安全に読めるよう、添字を範囲内へ丸めておく。
        if (spawnOrderIndex < 0 || spawnOrderIndex >= count) { spawnOrderIndex = 0; }

        for (int tried = 0; tried < count; tried++)
        {
            int index = spawnOrderIndex;
            spawnOrderIndex = (spawnOrderIndex + 1) % count;   // 次回はこの続きから

            string kind = (order[index] ?? "").Trim();
            string path = PrefabPathOf(kind);
            if (!string.IsNullOrEmpty(path)) { return path; }

            SEED.Debug.LogWarning(
                $"[DriftItemManager] 出現順の {index} 番目 \"{kind}\" は未知の種類か prefab パスが未設定です"
                + $"（{DriftItem.KindStun} / {DriftItem.KindFishRecover} / {DriftItem.KindLineRecover}"
                + " のいずれかを指定してください。この要素は読み飛ばします）。");
        }

        return "";
    }

    /// <summary>
    /// 出現位置（水面上の一点）を抽選する【出現位置の唯一の算出点】。
    ///
    /// <b>ウキを少しだけ岸側（竿先の方向）へ寄せた点</b>を中心に、
    /// <see cref="spawnRadiusMin"/>〜<see cref="spawnRadiusMax"/> の円環内でランダムな一点を取り、
    /// <b>竿先（＝プレイヤーが立つ岸側）から <see cref="spawnRadiusMin"/> より近い点は
    /// 捨てて引き直す</b>（岸際に湧いて「拾いようがない／不自然に足元へ流れてくる」のを防ぐため）。
    ///
    /// <code>
    /// 中心   = ウキ + (竿先 − ウキ) の向き × shoreShiftMeters
    ///          ただし中心と竿先の距離は spawnRadiusMin 以上になるようクランプ
    ///          （＝寄せすぎて円環が岸へめり込まないようにする）
    /// 角度   = Random(0, 2π) / 距離 = Random(spawnRadiusMin, spawnRadiusMax)
    /// 位置   = 中心 + (sinθ, cosθ) × 距離（高さは水面）
    /// </code>
    /// </summary>
    /// <param name="controller">位置の基準（ウキ・竿先・水面）を読むコントローラ。</param>
    /// <param name="position">決まった出現位置（ワールド）。</param>
    /// <returns>位置が決まったか（規定回数引き直しても決まらなければ false）。</returns>
    private bool TryPickSpawnPosition(FishingController controller, out SEED.Vector3 position)
    {
        position = SEED.Vector3.Zero;

        var floatPosition = controller.FloatWorldPosition;
        var rodTip        = controller.RodTipWorldPosition;
        float surface     = controller.WaterSurfaceY();

        float radiusMin = SEED.Mathf.Max(spawnRadiusMin, 0f);
        float radiusMax = SEED.Mathf.Max(spawnRadiusMax, radiusMin);

        // 円環の中心 ＝ ウキから竿先の方向へ shoreShiftMeters だけ寄せた点。
        // ウキと竿先が重なっていて向きが決まらない場合は、ずらさずウキ中心のままにする。
        float toRodX = rodTip.x - floatPosition.x;
        float toRodZ = rodTip.z - floatPosition.z;
        float floatToRod = SEED.Mathf.Sqrt(toRodX * toRodX + toRodZ * toRodZ);

        float centerX = floatPosition.x;
        float centerZ = floatPosition.z;
        if (floatToRod > DivideEpsilon)
        {
            // 寄せる量は「岸へめり込まない範囲」に抑える。
            // 中心と竿先の距離を radiusMin 以上に保ちたいので、ずらせるのは
            // (ウキ〜竿先の距離 − radiusMin) まで。ウキ自身が既に岸へ近い場合は 0（ずらさない）。
            float shiftLimit = SEED.Mathf.Max(floatToRod - radiusMin, 0f);
            float shift      = SEED.Mathf.Min(SEED.Mathf.Max(shoreShiftMeters, 0f), shiftLimit);
            centerX += toRodX / floatToRod * shift;
            centerZ += toRodZ / floatToRod * shift;
        }

        for (int i = 0; i < SpawnPositionRetryCount; i++)
        {
            float angle    = SEED.Random.Range(0f, FullTurnRadians);
            float distance = SEED.Random.Range(radiusMin, radiusMax);

            float x = centerX + SEED.Mathf.Sin(angle) * distance;
            float z = centerZ + SEED.Mathf.Cos(angle) * distance;

            // 岸（竿先）に近すぎる点は捨てる
            float dx = x - rodTip.x;
            float dz = z - rodTip.z;
            if (dx * dx + dz * dz < radiusMin * radiusMin) { continue; }

            position = new SEED.Vector3(x, surface, z);
            return true;
        }

        return false;
    }

    // ─── 内部処理: 片付け ────────────────────────────────────

    /// <summary>
    /// 生存している漂流物をすべて消す【一括片付けの唯一の入口】。
    /// <see cref="DriftItem.Kill"/> は登録簿を書き換えるので、必ず作業用リストへ写してから回す。
    /// </summary>
    private void ClearAll()
    {
        if (DriftItem.All.Count == 0) { return; }

        workItems.Clear();
        foreach (var item in DriftItem.All) { workItems.Add(item); }
        for (int i = 0; i < workItems.Count; i++) { workItems[i].Kill(); }
        workItems.Clear();
    }

    // ─── 内部処理: パス ─────────────────────────────────────

    /// <summary>
    /// prefab パスを正規化する（前後の空白を落とし、<c>assets://</c> が無ければ補う）。
    /// 空文字なら空文字のまま返す（＝生成しない）。
    /// </summary>
    /// <param name="path">インスペクタで指定されたパス。</param>
    private static string NormalizeAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return ""; }

        string trimmed = path.Trim();
        return trimmed.StartsWith(AssetSchemePrefix) ? trimmed : AssetSchemePrefix + trimmed;
    }
}
