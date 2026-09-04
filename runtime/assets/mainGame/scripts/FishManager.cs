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

    /// <summary>このレベルで出現する<b>通常魚</b>の .actor ファイルパスのリスト。</summary>
    [SerializeField(Label = "魚prefab(.actorパス)")]
    public List<string> fishPrefabs;

    /// <summary>
    /// このレベルで出現する<b>レア魚</b>の .actor ファイルパスのリスト。
    /// レア枠は合計 10%（<see cref="FishManager.RareFishRate"/>）で出現し、
    /// 枠内は均等割り。残り 90% は通常リスト内で均等割りされる。
    /// 空なら通常魚のみ（100%）になる。
    /// </summary>
    [SerializeField(Label = "レア魚prefab(.actorパス)")]
    public List<string> rareFishPrefabs;

    /// <summary>
    /// このレベルの水域に泳がせておく魚の生成総数。
    /// 釣られる・消えるなどで減ったら自動で補充される。0 なら自動生成しない。
    /// </summary>
    [SerializeField(Label = "生成総数")]
    public int maintainCount;
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

    /// <summary>
    /// レア魚の出現率（レア枠全体の合計。仕様: 10%）。
    /// 残り（90%）は通常魚リスト内で均等割りされる。
    /// </summary>
    private const float RareFishRate = 0.1f;

    /// <summary>
    /// 距離の一致とみなす微小量（メートル）。
    /// 近／遠マーカーが同距離のときの 0 除算を避けるための下限に使う。
    /// </summary>
    private const float DistanceEpsilon = 0.0001f;

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

    /// <summary>
    /// 生成する魚の水面高さ（ワールド Y）。
    /// </summary>
    [Header("生成"), SerializeField(Label = "生成する高さ(Y)")]
    private float spawnHeight = 0f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>出現位置の乱数源（方位と距離の揺らぎに使う）。</summary>
    private readonly System.Random random = new();

    /// <summary>
    /// レベルごとの生存中の魚のハンドル（添字 = レベル）。
    /// 釣られる・破棄されると実体が消えるので、毎フレーム掃除して
    /// 「維持数」まで自動補充する（<see cref="EnsurePopulation"/>）。
    /// </summary>
    private readonly List<List<SEED.GameObject>> spawnedFish = new();

    /// <summary>
    /// 設定不備の警告をすでに出したレベルの添字。
    /// 毎フレーム同じ警告でログが埋まるのを防ぐ（1 レベルにつき 1 回だけ出す）。
    /// </summary>
    private readonly HashSet<int> warnedLevels = new();

    /// <summary>レベル定義の一括検証（<see cref="ValidateLevelsOnce"/>）を実施済みか。</summary>
    private bool validated = false;

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
    /// 各レベルの魚の数を「維持数」まで自動補充する（釣られたら自然に補充される）。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        ValidateLevelsOnce();
        EnsurePopulation();
    }

    /// <summary>固定タイムステップの更新。物理など時間刻みを一定にしたい処理向け。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>
    /// Update 後の更新。
    /// 各魚（Fish スクリプトが自由に回遊する）が自分のレベルの円環から
    /// はみ出していたら、中心からの方位はそのままに距離だけ円環内へ戻す。
    /// Fish 側の回遊処理（Update）の後に効かせたいので LateUpdate で行う。
    /// </summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
        ClampFishToRings();
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
    ///   ランダム方位」。高さ(Y)は「生成する高さ(Y)」の固定値になる
    /// - レベルが範囲外・prefab リストが空・パスが空・マーカー未設定のときは
    ///   何もしない（false）
    /// </summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <returns>生成できたら true。</returns>
    public bool SpawnOne(int levelIndex) => TrySpawnOne(levelIndex, out _);

    /// <summary>
    /// 各レベルの魚の数を「維持数」まで自動補充する。
    /// 無効になった（釣られた・破棄された）ハンドルは追跡から外す。
    /// マーカー未設定など生成に失敗するレベルは、そのフレームは諦めて次を見る。
    /// </summary>
    private void EnsurePopulation()
    {
        // レベル数の増加に合わせて追跡リストを拡張する（縮小はしない: 添字の安定を優先）
        while (spawnedFish.Count < levels.Count) { spawnedFish.Add(new List<SEED.GameObject>()); }

        for (int i = 0; i < levels.Count; i++)
        {
            var alive = spawnedFish[i];
            alive.RemoveAll(f => !IsAlive(f));

            int want = levels[i].maintainCount;
            while (alive.Count < want)
            {
                if (!TrySpawnOne(i, out var fish)) { break; }
                alive.Add(fish);
            }
        }
    }

    /// <summary><see cref="SpawnOne"/> の実体。生成した魚のハンドルも返す（自動補充の追跡用）。</summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <param name="fish">生成した魚（失敗時は無効ハンドル）。</param>
    private bool TrySpawnOne(int levelIndex, out SEED.GameObject fish)
    {
        fish = default;
        if (levelIndex < 0 || levelIndex >= levels.Count) { return false; }
        var level = levels[levelIndex];
        bool hasNormal = level.fishPrefabs is { Count: > 0 };
        bool hasRare   = level.rareFishPrefabs is { Count: > 0 };
        if (!hasNormal && !hasRare) { return false; }

        // 出現枠の抽選: レア枠は合計 RareFishRate(10%)、残り(90%)は通常枠。
        // 片方の枠しか無いレベルではその枠が 100% になる。枠内は均等割りなので、
        // 「レア魚の出現率 10%・残りを残りの魚で割る」という仕様がそのまま成立する。
        bool pickRare = hasRare && (!hasNormal || random.NextDouble() < RareFishRate);
        var pool = pickRare ? level.rareFishPrefabs : level.fishPrefabs;

        // prefab をランダムに 1 つ選ぶ（空文字は未設定とみなしてスキップ）
        string path = pool[random.Next(pool.Count)];
        if (string.IsNullOrWhiteSpace(path)) { return false; }

        // 出現距離の範囲（円環）= 中心点から各マーカーまでの XZ 平面距離。
        // マーカー未設定・近遠が逆などの設定不備なら生成しない（静かに握り潰さない）。
        var center = CenterPosition();
        if (!TryGetRing(levelIndex, out var ring)) { return false; }

        // 出現位置: 円環内の一様ランダム距離 × ランダム方位。
        float angle = (float)random.NextDouble() * FullTurnRadians;
        float span = ring.Far - ring.Near;
        float distance = ring.Near + (float)random.NextDouble() * span;

        // 高さ: 「生成する高さ(Y)」の固定値をそのまま使う。
        var spawnPos = new SEED.Vector3(
            center.x + SEED.Mathf.Sin(angle) * distance,
            spawnHeight,
            center.z + SEED.Mathf.Cos(angle) * distance);

        // 生成して位置を合わせる（Instantiate 失敗時は false）
        fish = SEED.GameObject.Instantiate(path);
        if (!fish.IsValid) { return false; }
        if (fish.GetComponent<SEED.Transform>() is not { } t || !t.IsValid) { return false; }
        t.Position = spawnPos;
        return true;
    }

    // ─── 円環（出現範囲）の解決・検証・維持 ───────────────────

    /// <summary>
    /// 1 レベルぶんの出現範囲（中心点からの XZ 距離の円環）。
    /// 距離マーカー 2 つから毎回この値を作り、生成にも「はみ出しの押し戻し」にも使う。
    /// </summary>
    private readonly struct SpawnRing
    {
        /// <summary>円環の内側半径（中心からの XZ 距離）。</summary>
        public readonly float Near;

        /// <summary>円環の外側半径（中心からの XZ 距離）。</summary>
        public readonly float Far;

        /// <summary>各値を指定して円環を作る。</summary>
        public SpawnRing(float near, float far)
        {
            Near = near;
            Far = far;
        }
    }

    /// <summary>
    /// 指定レベルの円環（出現範囲）を距離マーカーから求める。
    ///
    /// マーカー未設定／無効、あるいは「遠マーカーが近マーカーより内側」のときは
    /// <b>入れ替えて救済せず</b> false を返す。近遠が逆になるのはシーン設定の不備
    /// （マーカーの取り違え・同名アクタへの誤解決）であり、黙って入れ替えると
    /// レベル同士の円環が重なって「浅瀬に大物が出る」といった不具合の原因が隠れるため。
    /// </summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <param name="ring">求めた円環（失敗時は既定値）。</param>
    /// <returns>円環を求められたら true。</returns>
    private bool TryGetRing(int levelIndex, out SpawnRing ring)
    {
        ring = default;
        if (levelIndex < 0 || levelIndex >= levels.Count) { return false; }
        var level = levels[levelIndex];

        if (level.distanceMinMarker is not { } minMarker || !minMarker.IsValid)
        {
            WarnLevelOnce(levelIndex, "距離マーカー(近) が未設定、または参照先アクタが見つかりません。");
            return false;
        }
        if (level.distanceMaxMarker is not { } maxMarker || !maxMarker.IsValid)
        {
            WarnLevelOnce(levelIndex, "距離マーカー(遠) が未設定、または参照先アクタが見つかりません。");
            return false;
        }

        var center = CenterPosition();
        var nearPos = minMarker.Position;
        var farPos = maxMarker.Position;
        float near = DistanceXZ(center, nearPos);
        float far = DistanceXZ(center, farPos);

        if (far - near < DistanceEpsilon)
        {
            WarnLevelOnce(levelIndex,
                $"距離マーカー(遠)={far:F1}m が (近)={near:F1}m より内側です。"
                + "マーカーの取り違えか、同名アクタが複数あって参照が別のマーカーへ解決されています"
                + "（参照はアクタ名の DFS 最初の一致で解決されるため、名前は一意にしてください）。");
            return false;
        }

        ring = new SpawnRing(near, far);
        return true;
    }

    /// <summary>
    /// レベル定義を 1 度だけ一括検証し、結果をログへ出す。
    ///
    /// 「シーンで見えている円と実際の出現範囲が違う」不具合の大半は、
    /// マーカーの<b>アクタ名の重複</b>（参照が DFS 最初の一致へ吸われる）で起きる。
    /// そこで各レベルの実効レンジを列挙し、レンジの重なり・マーカー座標の一致を警告する。
    /// </summary>
    private void ValidateLevelsOnce()
    {
        if (validated) { return; }
        validated = true;

        var center = CenterPosition();
        SEED.Debug.Log($"[FishManager] 中心点=({center.x:F1}, {center.z:F1}) / レベル数={levels.Count}");

        float previousFar = 0f;
        bool hasPrevious = false;
        for (int i = 0; i < levels.Count; i++)
        {
            if (!TryGetRing(i, out var ring))
            {
                SEED.Debug.LogError($"[FishManager] Lv{i + 1}: 出現範囲を決められないため生成しません。");
                hasPrevious = false;
                continue;
            }

            SEED.Debug.Log($"[FishManager] Lv{i + 1}: 出現範囲 {ring.Near:F1}m 〜 {ring.Far:F1}m"
                + $" 維持数={levels[i].maintainCount}");

            // 直前のレベルと範囲が重なっていれば、そのレベルの魚が混ざって出現する
            if (hasPrevious && ring.Near < previousFar - DistanceEpsilon)
            {
                SEED.Debug.LogWarning($"[FishManager] Lv{i + 1} の出現範囲が Lv{i} と重なっています"
                    + $"（Lv{i} は {previousFar:F1}m まで、Lv{i + 1} は {ring.Near:F1}m から）。"
                    + "上位レベルの魚が手前の水域へ出ます。");
            }
            previousFar = ring.Far;
            hasPrevious = true;
        }

        WarnDuplicatedMarkers();
    }

    /// <summary>
    /// 距離マーカーの参照が別レベル間で同じアクタへ解決されていないか検査する。
    ///
    /// 参照はアクタ名で保存され、同名アクタが複数あるとヒエラルキーの DFS 順で
    /// 最初の 1 つへ全部吸われる。ハンドルの同一性は取れないので、
    /// 「ワールド座標が完全に一致する」ことを同一マーカーの証拠として警告する。
    /// </summary>
    private void WarnDuplicatedMarkers()
    {
        for (int i = 0; i < levels.Count; i++)
        {
            for (int j = i + 1; j < levels.Count; j++)
            {
                WarnIfSameMarker(i, j, levels[i].distanceMinMarker, levels[j].distanceMinMarker, "近");
                WarnIfSameMarker(i, j, levels[i].distanceMaxMarker, levels[j].distanceMaxMarker, "遠");
            }
        }
    }

    /// <summary>2 レベルの同種マーカーが同一座標なら、名前重複の疑いとして警告する。</summary>
    /// <param name="levelA">比較元のレベル添字。</param>
    /// <param name="levelB">比較先のレベル添字。</param>
    /// <param name="a">比較元のマーカー。</param>
    /// <param name="b">比較先のマーカー。</param>
    /// <param name="kind">マーカーの種別表示（"近" / "遠"）。</param>
    private static void WarnIfSameMarker(int levelA, int levelB, SEED.Transform? a, SEED.Transform? b, string kind)
    {
        if (a is not { } ta || !ta.IsValid) { return; }
        if (b is not { } tb || !tb.IsValid) { return; }
        var pa = ta.Position;
        var pb = tb.Position;
        if (SEED.Mathf.Abs(pa.x - pb.x) > DistanceEpsilon) { return; }
        if (SEED.Mathf.Abs(pa.y - pb.y) > DistanceEpsilon) { return; }
        if (SEED.Mathf.Abs(pa.z - pb.z) > DistanceEpsilon) { return; }

        SEED.Debug.LogWarning($"[FishManager] Lv{levelA + 1} と Lv{levelB + 1} の距離マーカー({kind}) が"
            + "同じ座標です。同名アクタが複数あり、参照が同一アクタへ解決されている可能性が高いです"
            + "（マーカーのアクタ名を一意にしてから参照を設定し直してください）。");
    }

    /// <summary>
    /// 生成済みの魚を自分のレベルの円環内へ押し戻す。
    ///
    /// 魚は Fish スクリプトが「生成地点±行動半径」で自由に回遊するため、
    /// 円環の縁で生成された個体は隣の水域へ出てしまう。ここで中心からの方位は
    /// 保ったまま距離だけ [内側半径, 外側半径] にクランプする（高さは触らない）。
    ///
    /// <b>ただし餌に関わっている魚は除外する</b>。餌（ウキ）へ寄っている・食いついている
    /// 個体をここで押し戻すと、円環の外にある餌へ永久に届かない・ヒット中に魚が
    /// ウキから引き剥がされる、といった不具合になる。除外対象かどうかは
    /// <see cref="FishingController.IsEngaged"/> に問い合わせる
    /// （魚は動的生成なので、参照は静的アクセサ <c>FishingController.Current</c> から得る）。
    /// </summary>
    private void ClampFishToRings()
    {
        var center = CenterPosition();
        // 釣りコントローラ（未起動なら null）。毎フレーム引き直す（ホットリロードで実体が変わるため）。
        var fishing = FishingController.Current;
        int levelCount = spawnedFish.Count < levels.Count ? spawnedFish.Count : levels.Count;
        for (int i = 0; i < levelCount; i++)
        {
            if (!TryGetRing(i, out var ring)) { continue; }

            var alive = spawnedFish[i];
            for (int k = 0; k < alive.Count; k++)
            {
                // 餌に寄っている／食いついている魚はクランプしない（上のコメント参照）
                if (fishing is { } fc && fc.IsEngaged(alive[k])) { continue; }

                if (alive[k].GetComponent<SEED.Transform>() is not { } t || !t.IsValid) { continue; }

                var pos = t.Position;
                float dx = pos.x - center.x;
                float dz = pos.z - center.z;
                float distance = SEED.Mathf.Sqrt(dx * dx + dz * dz);
                float clamped = SEED.Mathf.Clamped(distance, ring.Near, ring.Far);
                if (SEED.Mathf.Abs(clamped - distance) < DistanceEpsilon) { continue; }

                // 中心に重なっているときは方位を決められないので、既定の方位(+X)で外へ出す
                float dirX = distance > DistanceEpsilon ? dx / distance : 1f;
                float dirZ = distance > DistanceEpsilon ? dz / distance : 0f;
                t.Position = new SEED.Vector3(center.x + dirX * clamped, pos.y, center.z + dirZ * clamped);
            }
        }
    }

    /// <summary>
    /// 魚がまだシーンに生きているか。
    ///
    /// <c>GameObject.IsValid</c> は「ハンドルが束縛されているか」しか見ず、破棄後も true のままなので
    /// 補充判定には使えない。Transform の有無（＝エンティティの実在）で生存を判定する。
    /// </summary>
    /// <param name="fish">判定する魚。</param>
    /// <returns>生きていれば true。</returns>
    private static bool IsAlive(SEED.GameObject fish)
        => fish.IsValid
        && fish.GetComponent<SEED.Transform>() is { } t
        && t.IsValid;

    /// <summary>同じレベルについて 1 度だけ警告を出す（毎フレームのログ汚染を防ぐ）。</summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <param name="message">警告本文。</param>
    private void WarnLevelOnce(int levelIndex, string message)
    {
        if (!warnedLevels.Add(levelIndex)) { return; }
        SEED.Debug.LogWarning($"[FishManager] Lv{levelIndex + 1}: {message}");
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
