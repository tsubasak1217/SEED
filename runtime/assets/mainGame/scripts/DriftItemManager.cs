using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

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
///   位置 = 竿先→ウキを結ぶ線分（＝これから巻き取ってウキが通る道筋）上の比率
///         spawnSegmentTMin〜spawnSegmentTMax の一点に、道筋と直交する横ずれ
///         （±spawnLateralMaxMeters）を足した水面上の一点
///         竿先〜ウキの距離が spawnMinSegmentMeters 未満（ウキが岸に近い）なら出さない
///   種類 = spawnWeights（ひるませ／魚回復／糸回復）の重み付き抽選
/// ヒットしていないあいだは、残っている漂流物をすべて消す
/// </code>
///
/// 生存個体の管理簿は <see cref="DriftItem.All"/> 一本に統一してある
/// （マネージャ側で別のリストを持つと、寿命による自滅と二重管理になるため）。
/// </summary>
public class DriftItemManager : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────────

    /// <summary>竿先→ウキの線分上の比率（<c>t</c>）が取り得る下限（＝竿先そのもの）。</summary>
    private const float MinSegmentRatio = 0f;

    /// <summary>竿先→ウキの線分上の比率（<c>t</c>）が取り得る上限（＝ウキそのもの）。</summary>
    private const float MaxSegmentRatio = 1f;

    /// <summary>重みの合計がこれ以下なら抽選できない（＝生成しない）。</summary>
    private const float MinTotalWeight = 1e-4f;

    /// <summary>1 フレームに生成する個数の上限（生成が一気に固まらないようにする）。</summary>
    private const int SpawnPerTick = 1;

    /// <summary>アセット仮想パスの接頭辞。prefab パスに付いていなければ補う。</summary>
    private const string AssetSchemePrefix = "assets://";

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

    /// <summary>
    /// 出現位置の比率（<c>t</c>）の<b>下限</b>【竿先を 0・ウキを 1 とした線分上の位置】。
    ///
    /// 漂流物は<b>ウキが巻かれて通る道筋の上</b>にしか置かない
    /// （拾えるのはウキとの接触だけなので、ウキより沖に湧かせても拾いようがない）。
    /// 0 に近いほど岸寄り（＝拾えるまでが長い）、1 に近いほどウキの手前に湧く。
    /// 既定 0.35: 竿先のすぐそばは絵として窮屈なので、少し沖から出す。
    /// </summary>
    [SerializeField(Label = "出現位置の比率(下限)")]
    private float spawnSegmentTMin = 0.35f;

    /// <summary>
    /// 出現位置の比率（<c>t</c>）の<b>上限</b>（<see cref="spawnSegmentTMin"/> と同じ座標系）。
    /// 既定 0.9: ウキに重ねて湧かせない（湧いた瞬間に拾えてしまうのを避ける）。
    /// </summary>
    [SerializeField(Label = "出現位置の比率(上限)")]
    private float spawnSegmentTMax = 0.9f;

    /// <summary>
    /// 道筋（竿先→ウキ）と直交する向きへの<b>最大横ずれ</b>（メートル）。
    /// ±この値のあいだで一様に抽選する。0 にすると必ず道筋の真上に並ぶ。
    /// 巻きながら A / D で左右へ寄せれば拾える幅なので、大きくするほど操作が要る。
    /// </summary>
    [SerializeField(Label = "横方向の最大ずれ(m)")]
    private float spawnLateralMaxMeters = 3f;

    /// <summary>
    /// 出現に必要な竿先〜ウキの水平距離（メートル）【区間が短すぎるときの棄却しきい値】。
    ///
    /// ウキが岸に近いと「竿先とウキの間」がほとんど無くなり、湧かせても
    /// 拾う間もなく巻き切ってしまう。その場合はこのフレームの生成を諦める
    /// （次の生成間隔でまた試す）。
    /// </summary>
    [SerializeField(Label = "出現に必要な竿先〜ウキの距離(m)")]
    private float spawnMinSegmentMeters = 4f;

    /// <summary>抽選の重み（ひるませ）。0 なら出現しない。</summary>
    [SerializeField(Label = "抽選の重み(ひるませ)")]
    private float stunWeight = 1f;

    /// <summary>抽選の重み（魚回復）。0 なら出現しない。</summary>
    [SerializeField(Label = "抽選の重み(魚回復)")]
    private float fishRecoverWeight = 1f;

    /// <summary>抽選の重み（糸回復）。0 なら出現しない。</summary>
    [SerializeField(Label = "抽選の重み(糸回復)")]
    private float lineRecoverWeight = 1f;

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

    /// <summary>最初の生成をすぐ行わないよう、生成間隔ぶん待ってから始める。</summary>
    public override void OnStart()
    {
        Current = this;
        spawnTimer = spawnIntervalSeconds;
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
    /// 漂流物を 1 個生成する。種類は重み付き抽選、位置は竿先〜ウキの線分上のランダムな一点。
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
    /// 抽選（<see cref="PickPrefabPath"/>）を通さず種類を直接指定するので、
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
    /// 種類を指定して漂流物を 1 個、通常の出現範囲（竿先〜ウキの線分上）に生成する。
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
    /// 重み付き抽選で prefab のパスを 1 つ選ぶ【種類の抽選の唯一の実装】。
    /// 重みの合計が 0 なら空文字（＝生成しない）を返す。
    /// </summary>
    private string PickPrefabPath()
    {
        float stun = SEED.Mathf.Max(stunWeight, 0f);
        float fish = SEED.Mathf.Max(fishRecoverWeight, 0f);
        float line = SEED.Mathf.Max(lineRecoverWeight, 0f);
        float total = stun + fish + line;
        if (total <= MinTotalWeight) { return ""; }

        float roll = SEED.Random.Range(0f, total);
        if (roll < stun) { return stunPrefabPath; }
        if (roll < stun + fish) { return fishRecoverPrefabPath; }
        return lineRecoverPrefabPath;
    }

    /// <summary>
    /// 出現位置（水面上の一点）を抽選する【出現位置の唯一の算出点】。
    ///
    /// <b>竿先 → ウキを結ぶ線分＝これから巻き取ってウキが通る道筋</b>の上に置く。
    /// 拾得判定はウキとの接触だけ（<c>FishingController.UpdateDriftPickup</c>）なので、
    /// ウキより沖や真横に湧かせても拾いようがない ―― だから道筋の上に限定する。
    ///
    /// <code>
    /// t        = Random(spawnSegmentTMin, spawnSegmentTMax)   // 竿先 0 〜 ウキ 1
    /// lateral  = Random(-spawnLateralMaxMeters, +spawnLateralMaxMeters)
    /// 位置     = 竿先 + 道筋方向 × (区間長 × t) + 道筋の直交方向 × lateral（高さは水面）
    /// </code>
    ///
    /// 竿先〜ウキの水平距離が <see cref="spawnMinSegmentMeters"/> 未満のときは
    /// 「置ける区間が無い」として false を返す（＝このフレームは出さない）。
    /// </summary>
    /// <param name="controller">位置の基準（ウキ・竿先・水面）を読むコントローラ。</param>
    /// <param name="position">決まった出現位置（ワールド）。</param>
    /// <returns>位置が決まったか（区間が短すぎるときは false）。</returns>
    private bool TryPickSpawnPosition(FishingController controller, out SEED.Vector3 position)
    {
        position = SEED.Vector3.Zero;

        var floatPosition = controller.FloatWorldPosition;
        var rodTip        = controller.RodTipWorldPosition;
        float surface     = controller.WaterSurfaceY();

        // 道筋（竿先 → ウキ）の水平ベクトルと、その長さ＝置ける区間の長さ
        float axisX = floatPosition.x - rodTip.x;
        float axisZ = floatPosition.z - rodTip.z;
        float segmentLength = SEED.Mathf.Sqrt(axisX * axisX + axisZ * axisZ);

        // 区間が短すぎる（ウキが岸のすぐそば）なら出さない
        if (segmentLength < SEED.Mathf.Max(spawnMinSegmentMeters, 0f)) { return false; }

        float dirX = axisX / segmentLength;
        float dirZ = axisZ / segmentLength;

        // 比率は 0〜1 に丸めたうえで、下限 ≦ 上限 を必ず満たすように整える
        // （インスペクタで逆に入れても抽選が壊れないようにするため）
        float ratioMin = SEED.Mathf.Clamped(spawnSegmentTMin, MinSegmentRatio, MaxSegmentRatio);
        float ratioMax = SEED.Mathf.Clamped(spawnSegmentTMax, ratioMin, MaxSegmentRatio);
        float ratio    = SEED.Random.Range(ratioMin, ratioMax);

        // 横ずれ: 道筋を右へ 90° 回した水平方向（正規化済み）へ ±lateralMax
        float lateralMax = SEED.Mathf.Max(spawnLateralMaxMeters, 0f);
        float lateral    = SEED.Random.Range(-lateralMax, lateralMax);
        float rightX     = dirZ;
        float rightZ     = -dirX;

        float along = segmentLength * ratio;
        position = new SEED.Vector3(
            rodTip.x + dirX * along + rightX * lateral,
            surface,
            rodTip.z + dirZ * along + rightZ * lateral);
        return true;
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
