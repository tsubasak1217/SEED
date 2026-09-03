using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 魚のレベル 1 段ぶんの定義（データドリブン）。
///
/// 「レベルの島を追加していく」運用に合わせ、レベルはこの構造体のリスト
/// （<see cref="FishManager"/> の levels）へ [+] で追加していく。
/// - 出現距離: 中心点からこの距離の水域に、このレベルの魚が出る
/// - 魚 prefab リスト: 出現しうる魚の .actor パス（GameObject.Instantiate で生成する）
/// </summary>
[System.Serializable]
public struct FishLevelEntry
{
    /// <summary>
    /// 出現距離の下限を示す<b>マーカーアクタ</b>。
    /// 「中心点からこのアクタまでの XZ 平面距離」が下限になる。
    /// シーン上でマーカーを動かすだけで距離を直感的にレベルデザインできる。
    /// </summary>
    [SerializeField(Label = "距離マーカー(近)")]
    public SEED.Transform? distanceMinMarker;

    /// <summary>
    /// 出現距離の上限を示す<b>マーカーアクタ</b>（中心点からの XZ 平面距離）。
    /// 近マーカーと同位置なら正確に円周上に出る。
    /// </summary>
    [SerializeField(Label = "距離マーカー(遠)")]
    public SEED.Transform? distanceMaxMarker;

    /// <summary>このレベルで出現する魚の .actor ファイルパスのリスト。</summary>
    [SerializeField(Label = "魚prefab(.actorパス)")]
    public List<string> fishPrefabs;
}

/// <summary>
/// 魚の生成マネージャ。
///
/// レベル定義（<see cref="FishLevelEntry"/> のリスト）を持ち、
/// 「中心点から出現距離だけ離れた水域」へレベルに応じた魚 prefab を生成する。
/// レベル＝リストの添字（0 始まり）で、島（レベル）を増やすときは
/// インスペクタでリストへ 1 要素足すだけでよい。
///
/// 生成のスケジュール（いつ・何匹・補充ルール）は未確定のため、
/// 本クラスは「1 匹生成する」最小の部品（<see cref="SpawnOne"/>）までを提供し、
/// ルールが決まり次第 Update から呼ぶ形で拡張する。
/// </summary>
public class FishManager : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>1 回転（ラジアン）。出現方位の乱数範囲に使う。</summary>
    private const float FullTurnRadians = 2f * 3.14159265f;

    // ─── 中心点 ───────────────────────────────────────────────

    /// <summary>
    /// 出現距離の基準になる中心点（島の中心のアクタ）。
    /// 未設定なら下の中心X/Zを使う（アクタを置かずに座標だけで指定したい場合）。
    /// </summary>
    [Header("中心点"), SerializeField(Label = "中心アクタ（省略可）")]
    private SEED.Transform? centerActor = null;

    /// <summary>中心アクタ未設定時に使うワールド X 座標。</summary>
    [SerializeField(Label = "中心X")]
    private float centerX = 0f;

    /// <summary>中心アクタ未設定時に使うワールド Z 座標。</summary>
    [SerializeField(Label = "中心Z")]
    private float centerZ = 0f;

    // ─── レベル定義 ───────────────────────────────────────────

    /// <summary>
    /// レベル定義のリスト（添字＝レベル、0 始まり）。
    /// 各要素は「出現距離」と「魚 prefab リスト」を持つ。
    /// レベル（島）を追加するときはここへ [+] で 1 要素足す。
    /// </summary>
    [Header("レベル"), SerializeField(Label = "レベル")]
    private List<FishLevelEntry> levels = new();

    // ─── 生成パラメータ ───────────────────────────────────────

    /// <summary>生成する魚の水面高さ（ワールド Y）。水面の高さに合わせる。</summary>
    [Header("生成"), SerializeField(Label = "生成する高さ(Y)")]
    private float spawnHeight = 0f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>出現位置の乱数源（方位と距離の揺らぎに使う）。</summary>
    private readonly System.Random random = new();

    /// <summary>フレーム開始時に呼ばれる。入力取得や状態リセット向け。</summary>
    public override void BeginFrame(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update より前の更新。他スクリプトへ渡す事前計算向け。</summary>
    public override void EarlyUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>
    /// 毎フレーム呼ばれる主更新処理。
    /// 生成ルール（いつ・何匹・補充）が決まったらここから <see cref="SpawnOne"/> を呼ぶ。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
    }

    /// <summary>固定タイムステップの更新。物理など時間刻みを一定にしたい処理向け。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update 後の更新。</summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>描画フェーズで呼ばれる。描画に関わる処理向け。</summary>
    public override void Render(ref NativeFrameContext ctx)
    {
    }

    /// <summary>フレーム終了時に呼ばれる。後片付けや状態確定向け。</summary>
    public override void EndFrame(ref NativeFrameContext ctx)
    {
    }

    // ─── 生成の部品 ───────────────────────────────────────────

    /// <summary>
    /// 指定レベルの魚を 1 匹生成する。
    ///
    /// - prefab はそのレベルのリストからランダムに 1 つ選ぶ
    /// - 位置は「中心点から、距離マーカー2つで決まる範囲内のランダム距離、
    ///   ランダム方位」の水面上
    /// - レベルが範囲外・prefab リストが空・パスが空・マーカー未設定のときは
    ///   何もしない（false）
    /// </summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <returns>生成できたら true。</returns>
    public bool SpawnOne(int levelIndex)
    {
        if (levelIndex < 0 || levelIndex >= levels.Count) { return false; }
        var level = levels[levelIndex];
        if (level.fishPrefabs is not { Count: > 0 } prefabs) { return false; }

        // prefab をランダムに 1 つ選ぶ（空文字は未設定とみなしてスキップ）
        string path = prefabs[random.Next(prefabs.Count)];
        if (string.IsNullOrWhiteSpace(path)) { return false; }

        // 出現距離の範囲 = 中心点から各マーカーまでの XZ 平面距離。
        // マーカーが未設定・無効なら生成できない（シーン設定の不備を静かに握り潰さない）。
        var center = CenterPosition();
        if (level.distanceMinMarker is not { } minMarker || !minMarker.IsValid) { return false; }
        if (level.distanceMaxMarker is not { } maxMarker || !maxMarker.IsValid) { return false; }
        float distA = DistanceXZ(center, minMarker.Position);
        float distB = DistanceXZ(center, maxMarker.Position);

        // 出現位置: 範囲内の一様ランダム距離 × ランダム方位の水面上。
        // 近/遠マーカーの取り違えは入れ替えて許容する（データ入力に寛容にする）。
        float angle = (float)random.NextDouble() * FullTurnRadians;
        float near = SEED.Mathf.Min(distA, distB);
        float far  = SEED.Mathf.Max(distA, distB);
        float distance = near + (float)random.NextDouble() * (far - near);
        var spawnPos = new SEED.Vector3(
            center.x + SEED.Mathf.Sin(angle) * distance,
            spawnHeight,
            center.z + SEED.Mathf.Cos(angle) * distance);

        // 生成して位置を合わせる（Instantiate 失敗時は false）
        var fish = SEED.GameObject.Instantiate(path);
        if (!fish.IsValid) { return false; }
        if (fish.GetComponent<SEED.Transform>() is not { } t || !t.IsValid) { return false; }
        t.Position = spawnPos;
        return true;
    }

    /// <summary>中心点のワールド座標（XZ）。中心アクタがあればその位置、無ければ設定値。</summary>
    private SEED.Vector3 CenterPosition()
    {
        if (centerActor is { } c && c.IsValid) { return c.Position; }
        return new SEED.Vector3(centerX, 0f, centerZ);
    }

    /// <summary>2 点間の XZ 平面上の距離（高さの差は無視する）。</summary>
    private static float DistanceXZ(SEED.Vector3 a, SEED.Vector3 b)
    {
        float dx = b.x - a.x;
        float dz = b.z - a.z;
        return SEED.Mathf.Sqrt(dx * dx + dz * dz);
    }
}
