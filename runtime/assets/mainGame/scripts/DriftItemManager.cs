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
/// ヒット中（FishingController.Current.IsHooked）のあいだだけ
///   spawnIntervalSeconds ごとに 1 個、DriftItem.All.Count が maxItems 未満なら生成する
///   位置 = ウキを中心とした spawnRadiusMin〜spawnRadiusMax の円環内のランダムな一点（水面上）
///         ただし竿先（＝岸側）から spawnRadiusMin より近い点は捨てて引き直す
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

    /// <summary>1 回転（ラジアン）。出現方位のランダム抽選に使う。</summary>
    private const float FullTurnRadians = 6.2831853f;

    /// <summary>重みの合計がこれ以下なら抽選できない（＝生成しない）。</summary>
    private const float MinTotalWeight = 1e-4f;

    /// <summary>1 フレームに生成する個数の上限（生成が一気に固まらないようにする）。</summary>
    private const int SpawnPerTick = 1;

    /// <summary>出現位置の抽選をやり直す最大回数（竿先に近すぎる点を弾くため）。</summary>
    private const int SpawnPositionRetryCount = 8;

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

    /// <summary>ウキからの出現距離の下限（メートル）。近すぎる位置に湧かせない。</summary>
    [SerializeField(Label = "出現距離の下限(m)")]
    private float spawnRadiusMin = 4f;

    /// <summary>ウキからの出現距離の上限（メートル）。</summary>
    [SerializeField(Label = "出現距離の上限(m)")]
    private float spawnRadiusMax = 14f;

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

    // ─── ライフサイクル ────────────────────────────────────

    /// <summary>最初の生成をすぐ行わないよう、生成間隔ぶん待ってから始める。</summary>
    public override void OnStart()
    {
        spawnTimer = spawnIntervalSeconds;
    }

    /// <summary>破棄されるときは残った漂流物も片付ける（シーン遷移で置き去りにしない）。</summary>
    public override void OnDestroy()
    {
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
            // やり取りが終わった（糸切れ・釣り上げ・キャンセル）: 残りを一掃して待機に戻る
            ClearAll();
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
        if (FishingController.Current is not { } controller) { return false; }
        if (!activeOnlyWhileHooked) { return true; }
        return controller.IsHooked;
    }

    // ─── 内部処理: 生成 ─────────────────────────────────────

    /// <summary>
    /// 漂流物を 1 個生成する。種類は重み付き抽選、位置はウキ周りの円環内のランダムな一点。
    /// prefab が読めない・位置が決まらないときは静かに諦める（次の間隔でまた試す）。
    /// </summary>
    private void SpawnOne()
    {
        if (FishingController.Current is not { } controller) { return; }

        string path = NormalizeAssetPath(PickPrefabPath());
        if (string.IsNullOrEmpty(path)) { return; }

        if (!TryPickSpawnPosition(controller, out var position)) { return; }

        var obj = SEED.GameObject.Instantiate(path);
        if (!obj.IsValid)
        {
            SEED.Debug.LogWarning($"[DriftItemManager] 漂流物 prefab の読み込みに失敗しました: {path}");
            return;
        }

        // 生成直後のフレームでも位置を反映しておく（Instantiate 直後の Transform 設定は有効）
        if (obj.GetComponent<SEED.Transform>() is { IsValid: true } t) { t.Position = position; }
    }

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
    /// ウキを中心に <see cref="spawnRadiusMin"/>〜<see cref="spawnRadiusMax"/> の円環内で
    /// ランダムな一点を取り、<b>竿先（＝プレイヤーが立つ岸側）から
    /// <see cref="spawnRadiusMin"/> より近い点は捨てて引き直す</b>
    /// （岸際に湧いて「拾いようがない／不自然に足元へ流れてくる」のを防ぐため）。
    /// </summary>
    /// <param name="controller">位置の基準（ウキ・竿先・水面）を読むコントローラ。</param>
    /// <param name="position">決まった出現位置（ワールド）。</param>
    /// <returns>位置が決まったか（規定回数引き直しても決まらなければ false）。</returns>
    private bool TryPickSpawnPosition(FishingController controller, out SEED.Vector3 position)
    {
        var center = controller.FloatWorldPosition;
        var rodTip = controller.RodTipWorldPosition;
        float surface = controller.WaterSurfaceY();

        float radiusMin = SEED.Mathf.Max(spawnRadiusMin, 0f);
        float radiusMax = SEED.Mathf.Max(spawnRadiusMax, radiusMin);

        for (int i = 0; i < SpawnPositionRetryCount; i++)
        {
            float angle = SEED.Random.Range(0f, FullTurnRadians);
            float distance = SEED.Random.Range(radiusMin, radiusMax);

            float x = center.x + SEED.Mathf.Sin(angle) * distance;
            float z = center.z + SEED.Mathf.Cos(angle) * distance;

            // 岸（竿先）に近すぎる点は捨てる
            float dx = x - rodTip.x;
            float dz = z - rodTip.z;
            if (dx * dx + dz * dz < radiusMin * radiusMin) { continue; }

            position = new SEED.Vector3(x, surface, z);
            return true;
        }

        position = SEED.Vector3.Zero;
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
