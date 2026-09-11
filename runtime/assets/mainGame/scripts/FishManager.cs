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
    /// このレベルの水域に泳がせておく魚の総数（＝レーダーに出る点の数）。
    ///
    /// この総数は<b>仮想魚（<see cref="VirtualFish"/>）の数</b>であって、
    /// 実際に生成されるアクタの数ではない。実体になるのは
    /// <see cref="FishMaterializationPolicy"/> が「いま要る」と判断した個体だけで、
    /// それ以外は座標だけを持って漂う（レーダーには従来どおり全数が出る）。
    ///
    /// 0 なら自動生成しない。負値なら <c>FishManager</c> の「維持数の既定値」を使う。
    /// </summary>
    [SerializeField(Label = "生成総数")]
    public int maintainCount;
}

/// <summary>
/// 魚の生成マネージャ【海に居る魚の総数と、その実体化を司る唯一の場所】。
///
/// [基本のしくみ]
/// レベル定義（<see cref="FishLevelEntry"/> のリスト）を持ち、
/// 「中心点から出現距離だけ離れた水域」へレベルに応じた魚を配置する。
/// レベル＝リストの添字（0 始まり）で、島（レベル）を増やすときは
/// インスペクタでリストへ 1 要素足すだけでよい。
///
/// [仮想魚方式（2026-09-08 改定）]
/// 以前は全レベルの魚をすべてアクタとして実体化していたため、
/// 10 レベル × 維持数 32 ＝ 300 体超が常時 ECS・描画・スクリプト更新に乗り、
/// フレーム時間を食い潰していた。現在は次の 2 層に分けている。
/// <list type="number">
///   <item><b>仮想魚</b>（<see cref="VirtualFishPool"/>）… 座標・向き・魚種・レベルだけを持つ
///         軽量レコード。数フレームに 1 回ゆっくり漂わせるだけで、アクタは持たない。
///         レーダーの点はここから描かれるので、<b>見た目の魚影の数は従来どおり</b>。</item>
///   <item><b>実体</b>（アクタ）… <see cref="FishMaterializationPolicy"/> が
///         「レベル帯の中」「ウキから近い」と判定した個体だけを <c>Instantiate</c> する。
///         条件から外れたら座標を仮想レコードへ書き戻して <c>Destroy</c> する。</item>
/// </list>
/// 実体化・仮想化は座標を引き継ぐので、往復しても魚が瞬間移動して見えることはない。
///
/// [台本（チュートリアル）との関係]
/// 台本が明示的に出した魚（<see cref="SpawnOne"/> / <see cref="SpawnOneNear"/>）は
/// <see cref="VirtualFish.Pinned"/> が立ち、距離やレベル帯に関わらず<b>必ず実体化</b>される。
/// </summary>
public class FishManager : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>1 回転（ラジアン）。出現方位の乱数範囲に使う。</summary>
    private const float FullTurnRadians = 2f * 3.14159265f;

    /// <summary>度→ラジアン変換係数。</summary>
    private const float DegToRad = 3.14159265f / 180f;

    /// <summary>
    /// レア魚の出現率（レア枠全体の合計。仕様: 10%）。
    /// 残り（90%）は通常魚リスト内で均等割りされる。
    /// </summary>
    private const float RareFishRate = 0.1f;

    /// <summary>
    /// 「必ず含める魚種」（<see cref="TutorialRules.FishPrefabRequired"/>）が指定されている間、
    /// 自然出現の抽選でその魚種を引く確率。維持匹数の補充とは別に、入れ替わりの抽選でも
    /// 目当ての魚が出やすくする（1/種類数 のままだと「全然出ない」体感になるため）。
    /// </summary>
    private const float RequiredPrefabPickRate = 0.5f;

    /// <summary>レベル番号（1 始まり）を <see cref="levels"/> の添字（0 始まり）へ直す差分。</summary>
    private const int LevelNumberToIndex = 1;

    /// <summary>
    /// 距離の一致とみなす微小量（メートル）。
    /// 近／遠マーカーが同距離のときの 0 除算を避けるための下限に使う。
    /// </summary>
    private const float DistanceEpsilon = 0.0001f;

    /// <summary>基準レベル（実体化するレベル帯の下限）の既定＝最小レベルの添字。</summary>
    private const int MinimumLevelIndex = 0;

    /// <summary>
    /// 実体化半径を「自動」にする設定値。
    /// 0 以下なら「最遠レベルの円環をすべて覆う半径」を毎回計算して使う
    /// （＝距離による絞り込みを実質無効にし、レベル帯だけで絞る安全側の既定）。
    /// </summary>
    private const float AutoRadius = 0f;

    /// <summary>
    /// 自動の実体化半径を作るときに、最遠リングの外周へ掛ける倍率。
    /// 判定原点（ウキ）が中心から離れていても円環全体を覆えるよう 2 倍にする。
    /// </summary>
    private const float AutoRadiusCoverFactor = 2f;

    /// <summary>
    /// 仮想へ戻す半径を自動で決めるときに、実体化半径へ掛ける倍率（ヒステリシス）。
    /// 1 より大きくすることで、境界上の個体が生成と破棄を繰り返すのを防ぐ。
    /// </summary>
    private const float AutoDematerializeFactor = 1.25f;

    /// <summary>実体化の入れ替え数に上限を設けないことを表す値。</summary>
    private const int UnlimitedChanges = 0;

    /// <summary>「生成総数」が未指定（既定値を使う）であることを表す下限。</summary>
    private const int MaintainCountUnspecified = 0;

    /// <summary>
    /// 位置指定生成で「ばらつかせない」ことを表す半径（＝指定した 1 点ちょうどに出す）。
    /// <see cref="TryMaterializeByPrefabKey"/> が使う。
    /// </summary>
    private const float NoSpawnScatterRadius = 0f;

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

    /// <summary>
    /// レベル定義の「生成総数」が未指定（負値）のときに使う既定値。
    /// 仮想魚は軽いので大きくしてもよいが、レーダーの点が混み合うので 10 前後が目安。
    /// 「生成総数 = 0」は<b>このレベルは出さない</b>という明示なので、この既定値では上書きしない。
    /// </summary>
    [SerializeField(Label = "維持数の既定値")]
    private int defaultMaintainCount = 10;

    // ─── 実体化（仮想魚 → アクタ）のパラメータ ─────────────────

    /// <summary>
    /// 基準レベル L から何段上までを実体化するか（L 〜 L+この値）。
    /// 基準レベルは「掛かっている魚のレベル」、掛かっていなければ最小レベル。
    /// わらしべ連鎖で乗り換えられるのは L+1 以上なので、
    /// <c>FishingController</c> の「更新を止めるレベル差」から 1 引いた値と揃える。
    /// </summary>
    [Header("実体化"), SerializeField(Label = "実体化するレベル段数(上)")]
    private int activeLevelSpan = 2;

    /// <summary>
    /// 基準レベル L から何段<b>下</b>までを実体化するか（L-この値 〜 L）。
    /// ウキが落ちたリングの 1 つ内側の魚も画面に映り、餌の感知範囲にも入るため、
    /// 既定は 1（1 段下まで実体化する）。0 にすると基準レベル以上だけになる。
    /// </summary>
    [SerializeField(Label = "実体化するレベル段数(下)")]
    private int activeLevelSpanBelow = 1;

    /// <summary>
    /// この距離（メートル）より近い仮想魚だけを実体化する。
    /// 0 以下なら<b>自動</b>（最遠レベルの円環をすべて覆う半径）＝距離では絞らない。
    /// </summary>
    [SerializeField(Label = "実体化半径(m・0で自動)")]
    private float materializeRadius = AutoRadius;

    /// <summary>
    /// この距離（メートル）より遠ざかった実体を仮想へ戻す（実体化半径より大きくすること）。
    /// 0 以下なら自動（実体化半径 × 1.25）。しきい値を分けて生成／破棄のばたつきを防ぐ。
    /// </summary>
    [SerializeField(Label = "仮想へ戻す半径(m・0で自動)")]
    private float dematerializeRadius = AutoRadius;

    /// <summary>
    /// 仮想魚の漂いと実体化判定を行う間隔（フレーム）。1 なら毎フレーム。
    /// 数フレームに 1 回で十分な処理なので、既定は 10。
    /// </summary>
    [SerializeField(Label = "プール更新間隔(フレーム)")]
    private int poolUpdateIntervalFrames = 10;

    /// <summary>
    /// 1 回の判定で行う実体化／仮想化の最大件数（0 なら無制限）。
    /// レベル帯が動いた瞬間の <c>Instantiate</c> / <c>Destroy</c> の山を均したいときに使う。
    /// ただし連鎖の候補が出てくるのが遅れるため、既定は無制限。
    /// </summary>
    [SerializeField(Label = "1回の入れ替え上限(0で無制限)")]
    private int maxMaterializeChangesPerTick = UnlimitedChanges;

    // ─── 仮想魚の漂い ─────────────────────────────────────────

    /// <summary>仮想魚の遊泳速度（m/秒）。実体の魚より遅めにして目立たせない。</summary>
    [Header("仮想魚の漂い"), SerializeField(Label = "遊泳速度(m/s)")]
    private float virtualSwimSpeed = 1.0f;

    /// <summary>仮想魚が進行方向を振り直す間隔（秒）。</summary>
    [SerializeField(Label = "方向転換の間隔(秒)")]
    private float virtualTurnIntervalSeconds = 3.0f;

    /// <summary>1 回の方向転換で変える角度の最大値（度）。</summary>
    [SerializeField(Label = "方向転換の角度(度)")]
    private float virtualTurnJitterDegrees = 60f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>出現位置の乱数源（方位と距離の揺らぎに使う）。</summary>
    private readonly System.Random random = new();

    /// <summary>
    /// 海に居る魚の台帳（仮想＋実体）【個体を数える唯一の置き場】。
    /// 実体化済みの個体もここに居座り、<see cref="VirtualFish.Actor"/> でアクタを指す。
    /// </summary>
    private readonly VirtualFishPool pool = new();

    /// <summary>実体化の条件（レベル帯・距離）を判定する規則。インスペクタ値を毎回流し込む。</summary>
    private readonly FishMaterializationPolicy policy = new();

    /// <summary>実体化の候補を距離順に並べる作業用リスト（毎回の確保を避けて使い回す）。</summary>
    private readonly List<(float SqrDistance, VirtualFish Record)> materializeCandidates = new();

    /// <summary>同上（仮想へ戻す候補）。</summary>
    private readonly List<(float SqrDistance, VirtualFish Record)> dematerializeCandidates = new();

    /// <summary>候補の並べ替え規則（近い順）。毎回のデリゲート確保を避けて静的に持つ。</summary>
    private static readonly System.Comparison<(float SqrDistance, VirtualFish Record)> NearestFirst =
        (a, b) => a.SqrDistance.CompareTo(b.SqrDistance);

    /// <summary>候補の並べ替え規則（遠い順）。仮想へ戻すときは遠い個体から処理する。</summary>
    private static readonly System.Comparison<(float SqrDistance, VirtualFish Record)> FarthestFirst =
        (a, b) => b.SqrDistance.CompareTo(a.SqrDistance);

    /// <summary>プール更新までの残りフレーム数（0 以下になったフレームで更新する）。</summary>
    private int framesUntilPoolUpdate = 0;

    /// <summary>前回のプール更新からの経過秒数（仮想魚の漂いに渡す）。</summary>
    private float secondsSincePoolUpdate = 0f;

    /// <summary>
    /// 設定不備の警告をすでに出したレベルの添字。
    /// 毎フレーム同じ警告でログが埋まるのを防ぐ（1 レベルにつき 1 回だけ出す）。
    /// </summary>
    private readonly HashSet<int> warnedLevels = new();

    /// <summary>レベル定義の一括検証（<see cref="ValidateLevelsOnce"/>）を実施済みか。</summary>
    private bool validated = false;

    /// <summary>
    /// 生成した魚のエンティティ → レベル（1 始まり）の対応表
    /// 【魚が自分のレベルを知る唯一の手段】。
    ///
    /// 魚アクタから <see cref="Fish"/> スクリプトのインスタンスを引く API は無いので、
    /// 生成側（ここ）が「どのエンティティを何レベルで作ったか」を控え、
    /// 魚側が <see cref="Fish.OnStart"/> で <see cref="LevelOf"/> を 1 度だけ引く。
    ///
    /// キーは (エンティティ添字, 世代) の組。<c>SEED.GameObject</c> は等価比較を
    /// 実装していないため、値型タプルで確実に一致判定できるようにしている
    /// （<see cref="FishingController"/> の餌関与レジストリと同じ方式）。
    /// </summary>
    private readonly Dictionary<(uint Index, uint Generation), int> fishLevels = new();

    /// <summary>
    /// 生成した魚のエンティティ → 生成に使った .actor パスの対応表
    /// 【魚の「種類」を後から知る唯一の手段】。
    ///
    /// <see cref="Fish"/> は表示名しか持たず、どの prefab から生まれたかを覚えていない。
    /// 「クマノミを釣ったらクリア」のように<b>種類で判定したい</b>場面のために、
    /// 生成側（ここ）がパスを控えておく。キーの作り方は <see cref="fishLevels"/> と同じ。
    /// </summary>
    private readonly Dictionary<(uint Index, uint Generation), string> fishPrefabPaths = new();

    // ─── 台本（チュートリアル用の強制設定）─────────────────

    /// <summary>台本のレベル指定が無いことを表す値（負のレベルは存在しないため番人値に使える）。</summary>
    private const int NoScriptedLevel = -1;

    /// <summary>台本の食いつき待ち秒数が指定されていないことを表す値。</summary>
    private const float NoScriptedBiteDelay = -1f;

    /// <summary>
    /// 台本で固定するレベル（levels の添字、0 始まり）。
    /// <see cref="NoScriptedLevel"/> なら台本は無効で、通常どおり全レベルを維持する。
    /// </summary>
    private int scriptedLevelIndex = NoScriptedLevel;

    /// <summary>台本の食いつき待ち秒数。<see cref="NoScriptedBiteDelay"/> なら指定なし。</summary>
    private float scriptedBiteDelaySeconds = NoScriptedBiteDelay;

    /// <summary>台本で「必ず食いつかせる」か（<see cref="Fish"/> が食いつき待ちの抽選前に読む）。</summary>
    private bool scriptedForceBite = false;

    // ─── 他スクリプトからの参照点（静的アクセサ）───────────────

    /// <summary>
    /// 現在シーンで動いている魚マネージャ（実質シングルトン）。
    ///
    /// 魚は動的生成されるためインスペクタ参照でマネージャを注入できない。
    /// そこで <see cref="OnStart"/> で自分を登録し、<see cref="OnDestroy"/> で解除する
    /// （<see cref="FishingController.Current"/> とまったく同じ方式）。
    /// ホットリロードでは静的フィールドごと作り直され、各スクリプトの OnStart が
    /// 再実行されるので、古い参照が残ることはない。
    /// </summary>
    public static FishManager? Current { get; private set; } = null;

    /// <summary>生成直後の初期化。動的生成される魚から参照できるよう自分を登録する。</summary>
    public override void OnStart()
    {
        Current = this;
    }

    /// <summary>破棄直前の後始末。静的アクセサの参照を落とす（自分の分だけ取り消す）。</summary>
    public override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) { Current = null; }
    }

    /// <summary>
    /// 指定の魚アクタが属するレベル（1 始まり）を返す。
    /// このマネージャが生成していない魚（手置きなど）は <see cref="Fish.UnknownLevel"/>。
    /// </summary>
    /// <param name="fish">魚のアクタ。</param>
    /// <returns>レベル（1 始まり）。未登録なら <see cref="Fish.UnknownLevel"/>。</returns>
    public int LevelOf(SEED.GameObject fish)
    {
        if (!fish.IsValid) { return Fish.UnknownLevel; }
        return fishLevels.TryGetValue((fish.Entity.Index, fish.Entity.Generation), out int level)
            ? level
            : Fish.UnknownLevel;
    }

    /// <summary>
    /// 指定の魚アクタが「どの .actor から生まれたか」を返す。
    /// このマネージャが生成していない魚（手置きなど）は空文字。
    /// </summary>
    /// <param name="fish">魚のアクタ。</param>
    /// <returns>生成に使った assets:// パス。未登録なら空文字。</returns>
    public string PrefabPathOf(SEED.GameObject fish)
    {
        if (!fish.IsValid) { return string.Empty; }
        return fishPrefabPaths.TryGetValue((fish.Entity.Index, fish.Entity.Generation), out string? path)
            ? path
            : string.Empty;
    }

    // ─── 仮想魚の公開（レーダー用）─────────────────────────────

    /// <summary>台帳が持っているレベルの数（レーダーの走査に使う）。</summary>
    public int PooledLevelCount => pool.LevelCount;

    /// <summary>
    /// 指定レベルの魚のレコード一覧【レーダーが魚影を引く唯一の窓口】。
    ///
    /// 実体化済みの個体も含まれる（<see cref="VirtualFish.Materialized"/> が true）。
    /// レーダー側は実体化済みの個体を <see cref="Fish.All"/> 経由で描くので、
    /// ここからは<b>未実体化の個体だけ</b>を拾えばよい（二重に点が出ないようにする）。
    /// </summary>
    /// <param name="levelIndex">レベルの添字（0 始まり）。</param>
    /// <returns>そのレベルのレコード一覧（範囲外なら空）。</returns>
    public IReadOnlyList<VirtualFish> PooledFishOf(int levelIndex) => pool.RecordsOf(levelIndex);

    /// <summary>いま実体（アクタ）として存在している魚の数（負荷の目安）。</summary>
    public int MaterializedFishCount => pool.MaterializedCount;

    /// <summary>台帳が持つ魚の総数（仮想＋実体。レーダーに出る点の総数）。</summary>
    public int TotalFishCount => pool.TotalCount;

    // ─── 台本 API（チュートリアルから呼ぶ強制設定）─────────

    /// <summary>台本が有効か（レベル指定が入っているか）。</summary>
    public bool HasScriptedSpawn => scriptedLevelIndex >= 0;

    /// <summary>台本で固定しているレベル（levels の添字、0 始まり）。無効なら負値。</summary>
    public int ScriptedLevelIndex => scriptedLevelIndex;

    /// <summary>台本で「必ず食いつかせる」設定か（<see cref="Fish"/> が読む）。</summary>
    public bool ScriptedForceBite => scriptedForceBite;

    /// <summary>
    /// 台本の食いつき待ち秒数（0 以上なら有効・負なら指定なし）。
    /// <see cref="Fish"/> が食いつき待ちの抽選をする直前にこの値を優先して読む。
    /// </summary>
    public float ScriptedBiteDelaySeconds => scriptedBiteDelaySeconds;

    /// <summary>
    /// チュートリアル用の強制設定を掛ける【台本設定の唯一の入口】。
    ///
    /// 掛けている間は次の 2 点が本編の乱数挙動より優先される。
    /// <list type="number">
    ///   <item>自動補充の対象を <paramref name="levelIndex"/> のレベルだけに絞る
    ///         （他のレベルは減っても補充しない。既に泳いでいる個体は消さない）</item>
    ///   <item><paramref name="forceBite"/> が true なら、魚が餌に着いてからの
    ///         食いつき待ち時間を <paramref name="delaySeconds"/> で固定する
    ///         （乱数の待ち時間を使わないので「必ず・すぐに」アタリが来る）</item>
    /// </list>
    /// なお台本レベルは<b>必ず実体化の対象</b>になる（レベル帯の外でも実体化する）。
    /// </summary>
    /// <param name="levelIndex">固定するレベル（levels の添字、0 始まり）。範囲外なら台本は無効になる。</param>
    /// <param name="delaySeconds">食いつくまでの待ち秒数（負なら 0 として扱う）。</param>
    /// <param name="forceBite">true で食いつき待ち時間を固定する。</param>
    public void SetScriptedSpawn(int levelIndex, float delaySeconds, bool forceBite)
    {
        if (levelIndex < 0 || levelIndex >= levels.Count)
        {
            SEED.Debug.LogWarning($"[FishManager] 台本のレベル {levelIndex + 1} は定義されていません（定義数 {levels.Count}）。台本を無効にします。");
            ClearScripted();
            return;
        }

        scriptedLevelIndex       = levelIndex;
        scriptedForceBite        = forceBite;
        scriptedBiteDelaySeconds = forceBite ? SEED.Mathf.Max(delaySeconds, 0f) : NoScriptedBiteDelay;

        SEED.Debug.Log($"[FishManager] 台本: Lv{levelIndex + 1} / 食いつき待ち {scriptedBiteDelaySeconds:F2} 秒 / 強制 {forceBite}");
    }

    /// <summary>
    /// 台本の強制設定を解除して、本編どおりの乱数挙動へ戻す【台本解除の唯一の出口】。
    /// </summary>
    public void ClearScripted()
    {
        scriptedLevelIndex       = NoScriptedLevel;
        scriptedBiteDelaySeconds = NoScriptedBiteDelay;
        scriptedForceBite        = false;
    }

    /// <summary>
    /// 台本で固定しているレベルの魚を、いますぐ 1 匹その場で生成する。
    /// 自動補充を待たずに確実に 1 匹用意したいとき（チュートリアルの手順頭）に使う。
    /// </summary>
    /// <returns>生成できたら true。台本が無効・生成に失敗したら false。</returns>
    public bool SpawnScriptedNow()
    {
        if (!HasScriptedSpawn) { return false; }
        return SpawnOne(scriptedLevelIndex);
    }

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
    ///
    /// 重い処理（台帳の補充・実体化判定・仮想魚の漂い）は
    /// <see cref="poolUpdateIntervalFrames"/> フレームに 1 回にまとめる。
    /// 毎フレーム行うのは、チュートリアルの許可リストによる掃除だけ
    /// （こちらは「禁止された魚が 1 フレームでも見えてはいけない」ため）。
    /// </summary>
    /// <param name="ctx">フレーム情報（経過秒数を読む）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        ValidateLevelsOnce();
        EnforceExclusivePrefabFilter();

        secondsSincePoolUpdate += ctx.DeltaTime;
        framesUntilPoolUpdate--;
        if (framesUntilPoolUpdate > 0) { return; }
        framesUntilPoolUpdate = SEED.Mathf.Max(poolUpdateIntervalFrames, 1);

        float elapsed = secondsSincePoolUpdate;
        secondsSincePoolUpdate = 0f;

        SyncMaterializedRecords();
        EnsurePopulation();
        DriftVirtualFish(elapsed);
        UpdateMaterialization();
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

    // ─── 台帳の維持 ───────────────────────────────────────────

    /// <summary>
    /// 実体化済みレコードの状態を実体に合わせて整える
    /// 【実体 → 台帳の同期の唯一の場所】。
    ///
    /// <list type="number">
    ///   <item>実体が生きていれば、その座標をレコードへ写す
    ///         （仮想へ戻すときに位置が飛ばないようにするため）</item>
    ///   <item>実体が消えていた（釣られた・食われた・逃げた）ら、レコードごと台帳から外す。
    ///         抜けた分は <see cref="EnsurePopulation"/> が新しい個体として補充する</item>
    /// </list>
    /// </summary>
    private void SyncMaterializedRecords()
    {
        for (int level = 0; level < pool.LevelCount; level++)
        {
            var records = pool.RecordsOf(level);
            // 途中で削除するので後ろから走査する（添字がずれない）
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                if (!record.Materialized) { continue; }

                if (IsAlive(record.Actor)
                    && record.Actor.GetComponent<SEED.Transform>() is { IsValid: true } t)
                {
                    record.Position = t.Position;
                    continue;
                }

                // 実体が消えた: 対応表とレコードを同時に片付ける
                ForgetActor(record.Actor);
                record.DetachActor();
                pool.Remove(record);
            }
        }
    }

    /// <summary>
    /// 各レベルの魚（仮想＋実体）の数を「維持数」まで補充する
    /// 【台帳の補充の唯一の場所】。
    ///
    /// ここで作られるのは<b>仮想レコードだけ</b>で、アクタは生成しない
    /// （実体化するかどうかは <see cref="UpdateMaterialization"/> が決める）。
    /// マーカー未設定など位置を決められないレベルは、その回は諦めて次を見る。
    /// </summary>
    private void EnsurePopulation()
    {
        // レベル数の増加に合わせて台帳を拡張する（縮小はしない: 添字の安定を優先）
        pool.EnsureLevelCount(levels.Count);

        for (int i = 0; i < levels.Count; i++)
        {
            // 台本（チュートリアル）が掛かっている間は、指定レベル以外を補充しない。
            // 「説明したいレベルの魚だけが寄ってくる」状態を作るための強制設定で、
            // 台本を解除すれば次の更新から通常どおり全レベルが補充される。
            if (HasScriptedSpawn && i != scriptedLevelIndex) { continue; }

            // チュートリアルのミッションが「このレベルの魚だけ出す」と指定していれば従う。
            // 上書きが無ければ（TutorialRules.Active が false なら）この分岐は素通りし、
            // 従来どおり全レベルが補充される。
            if (TutorialRules.Active
                && TutorialRules.FishLevelFilter > TutorialRules.NoLevelFilter
                && i != TutorialRules.FishLevelFilter - LevelNumberToIndex)
            {
                continue;
            }

            // チュートリアルが維持数を上書きしていれば従う（0 = 無指定でレベル定義どおり）。
            // 上の 2 つの continue により、ここへ来る時点で i は「制限が掛かっているなら
            // その対象レベル」に絞られているので、単純に上書き値を使ってよい。
            int want = (TutorialRules.Active && TutorialRules.FishPopulationOverride > TutorialRules.NoPopulationOverride)
                ? TutorialRules.FishPopulationOverride
                : MaintainCountOf(i);

            // チュートリアルが「この魚は最低 1 匹居ること」と指定していれば、
            // 通常の補充より先に枠を 1 つ確保する（指定が無ければ何もしない）。
            EnsureRequiredPrefabPopulated(i, want);

            while (pool.UnpinnedCount(i) < want)
            {
                if (!TryCreateRecord(i, false, SEED.Vector3.Zero, 0f, false, out _)) { break; }
            }
        }
    }

    /// <summary>
    /// 「必ず含める魚種」（<see cref="TutorialRules.FishPrefabRequired"/>）が
    /// このレベルに最低 1 匹居る状態を維持する
    /// 【必須魚種の補充の唯一の実装】。
    ///
    /// [手順]
    /// <list type="number">
    ///   <item>指定が無い／このレベルの候補にその魚が居ないなら何もしない</item>
    ///   <item>既に 1 匹以上（仮想でも実体でも）居るなら何もしない</item>
    ///   <item>維持数がいっぱいなら、差し替え可能な 1 匹を退かして枠を空ける</item>
    ///   <item>空いた枠へ、魚種を指名してレコードを 1 件作る</item>
    /// </list>
    /// 釣り上げられて居なくなれば <see cref="SyncMaterializedRecords"/> がレコードを外すので、
    /// 次の補充でまた 1 匹だけ供給される（＝常に「最低 1 匹」が保たれる）。
    /// </summary>
    /// <param name="levelIndex">対象レベルの添字（0 始まり）。</param>
    /// <param name="maintainCount">このレベルで維持する個体数（自然出現の上限）。</param>
    private void EnsureRequiredPrefabPopulated(int levelIndex, int maintainCount)
    {
        string required = RequiredPrefabNeedle();
        if (required.Length == 0) { return; }

        // そのレベルに存在しない魚を指定された場合は黙って無視する
        // （台本の書き間違いでレベルの個体数を壊さないための番人）。
        var level = levels[levelIndex];
        if ((FindPrefabContaining(level.fishPrefabs, required)
             ?? FindPrefabContaining(level.rareFishPrefabs, required)) is not { } namedPath)
        {
            return;
        }

        // 許可リスト（魚種を固定）と矛盾する指定は無視する。
        // ここで作っても EnforceExclusivePrefabFilter が即座に取り除くため、
        // 生成と削除を毎フレーム繰り返すだけになる。
        if (IsExclusivePrefabFilterActive()
            && !namedPath.Contains(TutorialRules.FishPrefabFilter, System.StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 維持匹数（TutorialRules.FishPrefabRequiredCount）ぶん居るなら何もしない
        // （仮想レコードも「居る」に数える）。1 フレームに 1 匹ずつ補充する。
        int wantCount = SEED.Mathf.Max(TutorialRules.FishPrefabRequiredCount, TutorialRules.DefaultRequiredCount);
        if (CountPrefabMatches(levelIndex, required) >= wantCount) { return; }

        // 枠が埋まっているなら 1 匹退かして空ける（退かせなければ今回は諦める）
        if (pool.UnpinnedCount(levelIndex) >= maintainCount && !TryDropOneReplaceable(levelIndex, required))
        {
            return;
        }

        TryCreateRecord(levelIndex, false, SEED.Vector3.Zero, 0f, false, out _, required);
    }

    /// <summary>
    /// 「必ず含める魚種」の指定文字列を返す
    /// 【必須魚種の指定を読む唯一の場所】。指定が無ければ空文字。
    /// </summary>
    /// <returns>指定文字列（無指定なら空文字）。</returns>
    private static string RequiredPrefabNeedle()
    {
        if (!TutorialRules.Active) { return string.Empty; }

        string required = TutorialRules.FishPrefabRequired;
        return string.IsNullOrWhiteSpace(required) ? string.Empty : required;
    }

    /// <summary>
    /// 指定レベルに、指定文字列を含む prefab の個体が何匹居るかを数える（仮想・実体の別を問わない）。
    /// </summary>
    /// <param name="levelIndex">対象レベルの添字（0 始まり）。</param>
    /// <param name="needle">prefab パスに含まれていてほしい文字列。</param>
    /// <returns>一致した個体数。</returns>
    private int CountPrefabMatches(int levelIndex, string needle)
    {
        var records = pool.RecordsOf(levelIndex);
        int count = 0;
        for (int i = 0; i < records.Count; i++)
        {
            if (MatchesPrefab(records[i], needle)) { count++; }
        }
        return count;
    }

    /// <summary>レコードの prefab パスが指定文字列を含むか。</summary>
    /// <param name="record">判定する個体。</param>
    /// <param name="needle">含まれていてほしい文字列。</param>
    /// <returns>含んでいれば true。</returns>
    private static bool MatchesPrefab(VirtualFish record, string needle)
        => !string.IsNullOrEmpty(record.PrefabPath)
        && record.PrefabPath.Contains(needle, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 必須魚種を入れる枠を空けるために、差し替えても支障のない個体を 1 匹だけ退かす。
    ///
    /// 退かしてよいのは「台本の個体（<see cref="VirtualFish.Pinned"/>）でなく、
    /// 餌に関与していない（寄り・つつき・掛かり・釣り上げ演出中でない）」個体だけ。
    /// <b>仮想の個体を優先</b>する（実体を消すと画面から魚が突然消えるため）。
    /// </summary>
    /// <param name="levelIndex">対象レベルの添字（0 始まり）。</param>
    /// <param name="needle">必須魚種の指定文字列（一致する個体は退かさない）。</param>
    /// <returns>1 匹退かせたら true。</returns>
    private bool TryDropOneReplaceable(int levelIndex, string needle)
    {
        var records = pool.RecordsOf(levelIndex);
        var fishing = FishingController.Current;

        VirtualFish? materializedCandidate = null;

        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Pinned) { continue; }
            if (MatchesPrefab(record, needle)) { continue; }

            if (!record.Materialized)
            {
                // 仮想の個体は見つけ次第その場で退かす（画面には映っていない）
                pool.Remove(record);
                return true;
            }

            // 実体は「ほかに候補が無かったとき用」に控えておく（餌に関与中の個体は除く）
            if (fishing is { } fc && fc.IsEngaged(record.Actor)) { continue; }
            materializedCandidate ??= record;
        }

        if (materializedCandidate is not { } victim) { return false; }

        Dematerialize(victim);
        pool.Remove(victim);
        return true;
    }

    /// <summary>
    /// レベル定義の「生成総数」を解決する（未指定なら既定値を使う）。
    /// 0 は「このレベルは自動生成しない」という明示なので、既定値では上書きしない。
    /// </summary>
    /// <param name="levelIndex">レベルの添字（0 始まり）。</param>
    /// <returns>維持する仮想魚の数。</returns>
    private int MaintainCountOf(int levelIndex)
    {
        int declared = levels[levelIndex].maintainCount;
        return declared < MaintainCountUnspecified ? SEED.Mathf.Max(defaultMaintainCount, 0) : declared;
    }

    /// <summary>
    /// 仮想魚の漂いを進める（未実体化の個体だけが対象）。
    /// </summary>
    /// <param name="deltaTime">前回のプール更新からの経過秒数。</param>
    private void DriftVirtualFish(float deltaTime)
    {
        pool.DriftVirtual(
            deltaTime,
            random,
            SEED.Mathf.Max(virtualSwimSpeed, 0f),
            SEED.Mathf.Max(virtualTurnIntervalSeconds, DistanceEpsilon),
            SEED.Mathf.Max(virtualTurnJitterDegrees, 0f) * DegToRad,
            ClampPositionToRing);
    }

    // ─── 実体化（仮想 ⇄ アクタ）─────────────────────────────

    /// <summary>
    /// 実体化すべき個体とそうでない個体を入れ替える
    /// 【実体化・仮想化の唯一の実行点】。
    ///
    /// [手順]
    /// <list type="number">
    ///   <item>実体化の規則（レベル帯・距離）をインスペクタ値から作り直す</item>
    ///   <item>全レコードを見て「実体なのに条件から外れた」「仮想なのに条件に入った」を仕分ける</item>
    ///   <item><b>ウキから遠い実体から順に</b>仮想へ戻し、<b>近い仮想から順に</b>実体化する
    ///         （入れ替え上限を設けたときに、プレイヤーに近い個体を優先するため）</item>
    /// </list>
    /// 台本が出した個体（<see cref="VirtualFish.Pinned"/>）と、餌に関与している個体
    /// （<c>FishingController.IsEngaged</c>：寄っている・つついている・掛かっている・釣り上げ演出中）は
    /// 距離やレベル帯に関わらず<b>絶対に仮想へ戻さない</b>。やり取りの最中に魚が消えるため。
    /// </summary>
    private void UpdateMaterialization()
    {
        RefreshPolicy();

        var origin = MaterializeOrigin();
        int baseLevelIndex = BaseLevelIndex();
        var fishing = FishingController.Current;

        materializeCandidates.Clear();
        dematerializeCandidates.Clear();

        for (int level = 0; level < pool.LevelCount; level++)
        {
            bool activeLevel = IsActiveLevel(level, baseLevelIndex);
            var records = pool.RecordsOf(level);

            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                float sqrDistance = SqrDistanceXZ(origin, record.Position);

                if (record.Materialized)
                {
                    // 台本の個体は常に実体のまま。餌に関与中の個体も外せない。
                    if (record.Pinned) { continue; }
                    if (fishing is { } fc && fc.IsEngaged(record.Actor)) { continue; }

                    if (!activeLevel || policy.IsBeyondDematerializeRadius(sqrDistance))
                    {
                        dematerializeCandidates.Add((sqrDistance, record));
                    }
                    continue;
                }

                // 仮想の個体: 台本の個体は無条件、それ以外はレベル帯かつ実体化半径の内側
                if (record.Pinned || (activeLevel && policy.IsWithinMaterializeRadius(sqrDistance)))
                {
                    materializeCandidates.Add((sqrDistance, record));
                }
            }
        }

        // 遠い個体から仮想へ戻す（近くの魚は最後まで実体で残す）
        dematerializeCandidates.Sort(FarthestFirst);
        int budget = maxMaterializeChangesPerTick <= UnlimitedChanges ? int.MaxValue : maxMaterializeChangesPerTick;
        for (int i = 0; i < dematerializeCandidates.Count && budget > 0; i++)
        {
            Dematerialize(dematerializeCandidates[i].Record);
            budget--;
        }

        // 近い個体から実体化する（連鎖の候補がすぐ出てくるようにする）
        materializeCandidates.Sort(NearestFirst);
        for (int i = 0; i < materializeCandidates.Count && budget > 0; i++)
        {
            if (Materialize(materializeCandidates[i].Record)) { budget--; }
        }
    }

    /// <summary>
    /// インスペクタ値から実体化の規則を作り直す【規則の値を決める唯一の場所】。
    /// 半径が「自動（0 以下）」なら、最遠レベルの円環を覆う値を毎回計算して入れる。
    /// </summary>
    private void RefreshPolicy()
    {
        policy.ActiveLevelSpan = SEED.Mathf.Max(activeLevelSpan, 0);
        policy.ActiveLevelSpanBelow = SEED.Mathf.Max(activeLevelSpanBelow, 0);

        float materialize = materializeRadius > AutoRadius
            ? materializeRadius
            : FarthestRingRadius() * AutoRadiusCoverFactor;

        float dematerialize = dematerializeRadius > AutoRadius
            ? dematerializeRadius
            : materialize * AutoDematerializeFactor;

        policy.MaterializeRadius = materialize;
        // ヒステリシスが逆転していると生成と破棄を毎回繰り返すので、必ず実体化半径以上にする
        policy.DematerializeRadius = SEED.Mathf.Max(dematerialize, materialize);
    }

    /// <summary>
    /// 実体化の対象レベルか【レベル帯判定の唯一の場所】。
    ///
    /// 基準レベル帯（L 〜 L+段数）に加えて、
    /// 台本レベルは<b>必ず対象</b>にする（そのレベルの魚を見せるための指定なので帯の外でも実体化する）。
    /// チュートリアルの魚レベル制限が掛かっているあいだは、そのレベル<b>だけ</b>を対象にする
    /// （関係ないレベルの魚が画面に混ざらないようにする）。
    /// </summary>
    /// <param name="levelIndex">判定するレベルの添字（0 始まり）。</param>
    /// <param name="baseLevelIndex">基準レベルの添字（0 始まり）。</param>
    /// <returns>実体化の対象なら true。</returns>
    private bool IsActiveLevel(int levelIndex, int baseLevelIndex)
    {
        if (HasScriptedSpawn && levelIndex == scriptedLevelIndex) { return true; }

        if (TutorialRules.Active && TutorialRules.FishLevelFilter > TutorialRules.NoLevelFilter)
        {
            return levelIndex == TutorialRules.FishLevelFilter - LevelNumberToIndex;
        }

        return policy.IsWithinSpan(levelIndex, baseLevelIndex);
    }

    /// <summary>
    /// 実体化するレベル帯の基準レベル L（0 始まりの添字）
    /// 【基準レベルの唯一の決定点】。
    ///
    /// <list type="number">
    ///   <item>魚が掛かっていれば<b>その魚のレベル</b>。連鎖で乗り換えて
    ///         掛かっている魚のレベルが上がれば、帯もそれに追従して上がる。</item>
    ///   <item>掛かっていなければ<b>ウキが落ちている水域のレベル</b>。
    ///         遠投して深場（上位レベルの円環）へ落とせば、そこの魚が実体化して
    ///         食いついてくる ―― この規則が無いと「遠投しても最小レベルの魚しか
    ///         居ない」ことになり、飛距離でレベルを狙う遊びが成立しない。</item>
    /// </list>
    /// </summary>
    /// <returns>基準レベルの添字（0 始まり）。</returns>
    private int BaseLevelIndex()
    {
        if (FishingController.Current is { IsHooked: true } fc && fc.HookedFishLevel != Fish.UnknownLevel)
        {
            return SEED.Mathf.Max(fc.HookedFishLevel - LevelNumberToIndex, MinimumLevelIndex);
        }

        return RingLevelIndexAt(DistanceXZ(CenterPosition(), MaterializeOrigin()));
    }

    /// <summary>
    /// 中心点からの距離が「どのレベルの水域か」を返す
    /// 【距離 → レベルの唯一の対応付け】。
    ///
    /// 円環同士に隙間があっても迷子にならないよう、
    /// 「内側半径がその距離以下である最も外側のレベル」を採用する
    /// （どのレベルの内側にも届いていなければ最小レベル）。
    /// </summary>
    /// <param name="distanceFromCenter">中心点からの XZ 平面距離（メートル）。</param>
    /// <returns>その水域のレベル添字（0 始まり）。</returns>
    private int RingLevelIndexAt(float distanceFromCenter)
    {
        int found = MinimumLevelIndex;
        for (int i = 0; i < levels.Count; i++)
        {
            if (!TryGetRing(i, out var ring)) { continue; }
            if (distanceFromCenter >= ring.Near) { found = i; }
        }
        return found;
    }

    /// <summary>
    /// 距離判定の原点【「プレイヤーの関心の中心」の唯一の定義】。
    /// ウキ（着水点）があればその位置、無ければ出現円環の中心。
    /// </summary>
    /// <returns>距離判定に使うワールド座標。</returns>
    private SEED.Vector3 MaterializeOrigin()
    {
        if (FishingController.Current is { } fc)
        {
            var floatPos = fc.FloatWorldPosition;
            if (floatPos.x != 0f || floatPos.z != 0f) { return floatPos; }
        }
        return CenterPosition();
    }

    /// <summary>
    /// もっとも外側のレベルの円環半径（メートル）。自動の実体化半径の計算に使う。
    /// 円環を 1 つも解決できないときは 0 を返す（＝実体化半径 0＝実体化しない）。
    /// </summary>
    /// <returns>最遠リングの外周半径。</returns>
    private float FarthestRingRadius()
    {
        float farthest = 0f;
        for (int i = 0; i < levels.Count; i++)
        {
            if (!TryGetRing(i, out var ring)) { continue; }
            if (ring.Far > farthest) { farthest = ring.Far; }
        }
        return farthest;
    }

    /// <summary>
    /// 仮想レコードをアクタとして実体化する【Instantiate の唯一の呼び出し点】。
    /// レベルと .actor パスの対応表もここで登録する（魚側が OnStart で読む）。
    /// </summary>
    /// <param name="record">実体化する仮想魚。</param>
    /// <returns>実体化できたら true。</returns>
    private bool Materialize(VirtualFish record)
    {
        if (record.Materialized) { return false; }
        if (string.IsNullOrWhiteSpace(record.PrefabPath)) { return false; }

        var fish = SEED.GameObject.Instantiate(record.PrefabPath);
        if (!fish.IsValid) { return false; }
        if (fish.GetComponent<SEED.Transform>() is not { } t || !t.IsValid) { return false; }
        t.Position = record.Position;

        fishLevels[(fish.Entity.Index, fish.Entity.Generation)] = record.Level;
        fishPrefabPaths[(fish.Entity.Index, fish.Entity.Generation)] = record.PrefabPath;
        record.AttachActor(fish);
        return true;
    }

    /// <summary>
    /// 実体を破棄して仮想レコードへ戻す【Destroy（間引き）の唯一の呼び出し点】。
    /// 破棄の直前に実体の座標をレコードへ写すので、次に実体化したとき同じ場所に現れる。
    /// </summary>
    /// <param name="record">仮想へ戻す個体。</param>
    private void Dematerialize(VirtualFish record)
    {
        if (!record.Materialized) { return; }

        var actor = record.Actor;
        if (actor.IsValid)
        {
            if (actor.GetComponent<SEED.Transform>() is { IsValid: true } t) { record.Position = t.Position; }
            ForgetActor(actor);
            actor.Destroy();
        }
        record.DetachActor();
    }

    /// <summary>
    /// 破棄する（した）アクタの対応表エントリを消す。
    /// エンティティは使い回されるので、残したままだと別個体へ誤ったレベル・魚種が付く。
    /// </summary>
    /// <param name="actor">破棄するアクタ。</param>
    private void ForgetActor(SEED.GameObject actor)
    {
        if (!actor.IsValid) { return; }
        fishLevels.Remove((actor.Entity.Index, actor.Entity.Generation));
        fishPrefabPaths.Remove((actor.Entity.Index, actor.Entity.Generation));
    }

    // ─── 生成の部品（外部 API）─────────────────────────────────

    /// <summary>
    /// 指定レベルの魚を 1 匹、いますぐ実体として生成する【台本用】。
    ///
    /// - prefab はそのレベルのリストからランダムに 1 つ選ぶ
    /// - 位置は「中心点から、距離マーカー2つで決まる範囲内のランダム距離、
    ///   ランダム方位」。高さ(Y)は「生成する高さ(Y)」の固定値になる
    /// - 生成された個体は<b>常に実体化されたまま</b>になる（維持数の勘定にも入らない）
    /// - レベルが範囲外・prefab リストが空・パスが空・マーカー未設定のときは
    ///   何もしない（false）
    /// </summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <returns>生成できたら true。</returns>
    public bool SpawnOne(int levelIndex) => SpawnOnePinned(levelIndex, SEED.Vector3.Zero, 0f, out _, false);

    /// <summary>
    /// 指定した地点のまわりに魚を 1 匹その場で生成する【台本用の位置指定生成】。
    ///
    /// わらしべ連鎖のチュートリアルのように「掛かっている魚のすぐそばに
    /// 上位レベルの魚を必ず出す」場面で使う。通常の出現円環を無視するので、
    /// 本編の出現ロジックには一切影響しない。
    /// </summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <param name="center">出現の中心（ワールド座標。Y は生成高さで上書きされる）。</param>
    /// <param name="radius">中心からのばらつき半径（メートル。0 以下なら中心ちょうど）。</param>
    /// <param name="fish">生成した魚（失敗時は無効ハンドル）。</param>
    /// <returns>生成できたら true。</returns>
    public bool SpawnOneNear(int levelIndex, SEED.Vector3 center, float radius, out SEED.GameObject fish)
        => SpawnOnePinned(levelIndex, center, radius, out fish, true);

    /// <summary>
    /// <b>魚種を指名して</b>指定地点に 1 匹その場で実体化する
    /// 【prefab キー指定生成の唯一の入口】。
    ///
    /// <see cref="SpawnOneNear"/> がレベル番号で出すのに対し、こちらは
    /// <b>.actor パスに含まれる文字列</b>（例 "kaiju"）で出す。「怪獣を必ず呼ぶ」
    /// （<see cref="KaijuLure"/>）のように、レベル構成が変わっても同じ魚を指名したい
    /// 用途のための入口で、指定キーを含む prefab を持つレベルを探して使う。
    ///
    /// 生成した個体は<b>常時実体化（<see cref="VirtualFish.Pinned"/>）</b>になる。
    /// ＝距離やレベル帯で仮想へ戻されることも、維持数（<see cref="EnsurePopulation"/>）の
    /// 勘定で押し出されることもない。「呼んだ怪獣が寄ってくる途中で消える」のを
    /// 防ぐための特別枠で、既存の台本生成と同じ仕組みをそのまま使っている。
    ///
    /// <b>注意</b>: 常時実体化なので、呼び出し側が使い終わっても自動では消えない。
    /// <see cref="KaijuLure"/> は「既に実体化している怪獣が居ればそれを使い回す」ため、
    /// この経路で増えるアクタは高々 1 体に収まる。
    /// </summary>
    /// <param name="prefabKey">
    /// .actor パスに含まれていてほしい文字列（大文字小文字は無視）。空なら失敗。
    /// </param>
    /// <param name="worldPosition">出現位置（ワールド座標。Y は「生成する高さ」で上書きされる）。</param>
    /// <param name="fish">生成した魚（失敗時は無効ハンドル）。</param>
    /// <returns>生成できたら true。指定キーを含む prefab がどのレベルにも無ければ false。</returns>
    public bool TryMaterializeByPrefabKey(string prefabKey, SEED.Vector3 worldPosition, out SEED.GameObject fish)
    {
        fish = default;
        if (string.IsNullOrWhiteSpace(prefabKey)) { return false; }

        pool.EnsureLevelCount(levels.Count);

        for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)
        {
            // そのレベルの候補（通常枠・レア枠）にキーを含む prefab があるか
            var level = levels[levelIndex];
            if (FindPrefabContaining(level.fishPrefabs, prefabKey) is null
             && FindPrefabContaining(level.rareFishPrefabs, prefabKey) is null)
            {
                continue;
            }

            // 見つかったレベルで、魚種を指名して常時実体化のレコードを 1 件作る
            if (!TryCreateRecord(
                    levelIndex,
                    usePositionOverride: true,
                    overrideCenter: worldPosition,
                    overrideRadius: NoSpawnScatterRadius,
                    pinned: true,
                    out var record,
                    requiredPrefab: prefabKey))
            {
                return false;
            }

            if (!Materialize(record))
            {
                // 実体化に失敗したレコードを残すと「常時実体化のはずが仮想のまま」になるので取り消す
                pool.Remove(record);
                return false;
            }

            fish = record.Actor;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 台本生成の実体【台本による 1 匹生成の唯一の実装】。
    /// 仮想レコードを「常時実体化（Pinned）」で作り、その場で実体化する。
    /// </summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <param name="center">位置指定生成の中心（ワールド座標）。</param>
    /// <param name="radius">位置指定生成のばらつき半径（メートル）。</param>
    /// <param name="fish">生成した魚（失敗時は無効ハンドル）。</param>
    /// <param name="usePositionOverride">true なら円環ではなく <paramref name="center"/> の近くに出す。</param>
    /// <returns>生成できたら true。</returns>
    private bool SpawnOnePinned(int levelIndex, SEED.Vector3 center, float radius, out SEED.GameObject fish, bool usePositionOverride)
    {
        fish = default;
        pool.EnsureLevelCount(levels.Count);

        if (!TryCreateRecord(levelIndex, usePositionOverride, center, SEED.Mathf.Max(radius, 0f), true, out var record))
        {
            return false;
        }

        if (!Materialize(record))
        {
            // 実体化に失敗したレコードを残すと「常時実体化のはずが仮想のまま」になるので取り消す
            pool.Remove(record);
            return false;
        }

        fish = record.Actor;
        return true;
    }

    /// <summary>
    /// 仮想レコードを 1 件作って台帳へ加える【レコード生成の唯一の出口】。
    /// prefab の抽選と出現位置の決定をここへ集約する（実体化は行わない）。
    /// </summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <param name="usePositionOverride">true なら円環ではなく <paramref name="overrideCenter"/> の近くに出す。</param>
    /// <param name="overrideCenter">位置指定生成の中心（ワールド座標）。</param>
    /// <param name="overrideRadius">位置指定生成のばらつき半径（メートル）。</param>
    /// <param name="pinned">常時実体化する台本個体なら true。</param>
    /// <param name="record">作成したレコード（失敗時は null）。</param>
    /// <param name="requiredPrefab">
    /// 魚種の指名（.actor パスに含まれる文字列）。空なら通常どおり抽選する。
    /// 「必ず含める魚種」の補充（<see cref="EnsureRequiredPrefabPopulated"/>）だけが使う。
    /// </param>
    /// <returns>作成できたら true。</returns>
    private bool TryCreateRecord(
        int levelIndex,
        bool usePositionOverride,
        SEED.Vector3 overrideCenter,
        float overrideRadius,
        bool pinned,
        out VirtualFish record,
        string requiredPrefab = "")
    {
        record = null!;
        if (levelIndex < 0 || levelIndex >= levels.Count) { return false; }
        if (!TryResolvePrefab(levels[levelIndex], requiredPrefab, out string path)) { return false; }
        if (!TryPickSpawnPosition(levelIndex, usePositionOverride, overrideCenter, overrideRadius, out var spawnPos))
        {
            return false;
        }

        float heading = (float)random.NextDouble() * FullTurnRadians;
        record = new VirtualFish(levelIndex, path, spawnPos, heading, pinned);
        pool.Add(record);
        return true;
    }

    /// <summary>
    /// 出す魚の .actor パスを決める【レコード生成から見た魚種決定の唯一の入口】。
    ///
    /// 魚種の指名（<paramref name="requiredPrefab"/>）があり、それがこのレベルの候補に
    /// 実在すればそれを使う。無ければ通常の抽選（<see cref="TryPickPrefab"/>）へ落ちる。
    /// </summary>
    /// <param name="level">対象のレベル定義。</param>
    /// <param name="requiredPrefab">魚種の指名（空なら抽選）。</param>
    /// <param name="path">決まった .actor パス（失敗時は空文字）。</param>
    /// <returns>パスを決められたら true。</returns>
    private bool TryResolvePrefab(FishLevelEntry level, string requiredPrefab, out string path)
    {
        path = string.Empty;

        if (!string.IsNullOrWhiteSpace(requiredPrefab))
        {
            string? named = FindPrefabContaining(level.fishPrefabs, requiredPrefab)
                         ?? FindPrefabContaining(level.rareFishPrefabs, requiredPrefab);
            if (named is not null)
            {
                path = named;
                return true;
            }
        }

        return TryPickPrefab(level, out path);
    }

    /// <summary>
    /// このレベルで出す魚の .actor パスを抽選する【魚種抽選の唯一の実装】。
    ///
    /// チュートリアルの魚種指定があればそれを最優先し、
    /// 許可リスト（<see cref="IsExclusivePrefabFilterActive"/>）指定中に一致が無ければ
    /// <b>何も出さない</b>（通常抽選へ落とすと「この魚しか居ない」状態が作れない）。
    /// </summary>
    /// <param name="level">対象のレベル定義。</param>
    /// <param name="path">抽選された .actor パス（失敗時は空文字）。</param>
    /// <returns>パスを決められたら true。</returns>
    private bool TryPickPrefab(FishLevelEntry level, out string path)
    {
        path = string.Empty;
        bool hasNormal = level.fishPrefabs is { Count: > 0 };
        bool hasRare   = level.rareFishPrefabs is { Count: > 0 };
        if (!hasNormal && !hasRare) { return false; }

        // チュートリアルが「必ずこの魚を出せ」と言っていれば、抽選より先にそれを探す。
        // 見つからなければ（そのレベルに居ない魚を指定した等）通常の抽選へ落ちる。
        path = PickTutorialPrefab(level);

        if (string.IsNullOrWhiteSpace(path))
        {
            if (IsExclusivePrefabFilterActive()) { return false; }

            // 「必ず含める魚種」の指定があれば、自然出現の抽選でも RequiredPrefabPickRate の
            // 確率でその魚種を引く（維持匹数の補充だけでは目当ての魚が少なすぎるため）。
            string required = RequiredPrefabNeedle();
            if (required.Length > 0
                && random.NextDouble() < RequiredPrefabPickRate
                && (FindPrefabContaining(level.fishPrefabs, required)
                    ?? FindPrefabContaining(level.rareFishPrefabs, required)) is { } requiredPath)
            {
                path = requiredPath;
                return true;
            }

            // 出現枠の抽選: レア枠は合計 RareFishRate(10%)、残り(90%)は通常枠。
            // 片方の枠しか無いレベルではその枠が 100% になる。枠内は均等割りなので、
            // 「レア魚の出現率 10%・残りを残りの魚で割る」という仕様がそのまま成立する。
            bool pickRare = hasRare && (!hasNormal || random.NextDouble() < RareFishRate);
            var candidates = pickRare ? level.rareFishPrefabs : level.fishPrefabs;
            path = candidates[random.Next(candidates.Count)];
        }

        return !string.IsNullOrWhiteSpace(path);
    }

    /// <summary>
    /// 魚種の「許可リスト」指定が効いているか
    /// 【許可リスト判定の唯一の実装】。
    ///
    /// チュートリアルが有効で、魚種フィルタが指定されていて、かつ
    /// それを許可リストとして扱う設定になっているときだけ true。
    /// </summary>
    /// <returns>許可リストとして扱うなら true。</returns>
    private static bool IsExclusivePrefabFilterActive()
        => TutorialRules.Active
        && TutorialRules.FishPrefabExclusive
        && !string.IsNullOrWhiteSpace(TutorialRules.FishPrefabFilter);

    /// <summary>
    /// 許可リストに一致しない魚を取り除く【既存個体の掃除の唯一の実装】。
    ///
    /// 補充を止めるだけでは、指定より前から泳いでいた魚が残って連鎖や横取りが起きる。
    /// <b>仮想レコードも対象</b>にする（レーダーに禁止された魚影が残らないようにする）。
    /// 掛かっている魚だけは除外する（やり取りの最中に消すとヒットが宙に浮くため）。
    /// </summary>
    private void EnforceExclusivePrefabFilter()
    {
        if (!IsExclusivePrefabFilterActive()) { return; }

        string filter = TutorialRules.FishPrefabFilter;
        var hooked = FishingController.Current?.HookedFish;

        for (int level = 0; level < pool.LevelCount; level++)
        {
            var records = pool.RecordsOf(level);
            // 途中で削除するので後ろから走査する（添字がずれない）
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];

                // 許可された魚はそのまま
                if (!string.IsNullOrEmpty(record.PrefabPath)
                    && record.PrefabPath.Contains(filter, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 掛かっている魚は消さない（やり取りが壊れる）
                if (record.Materialized
                    && hooked is { } hookedFish
                    && hookedFish.Actor.IsValid
                    && record.Actor.IsValid
                    && hookedFish.Actor.Entity.Index == record.Actor.Entity.Index
                    && hookedFish.Actor.Entity.Generation == record.Actor.Entity.Generation)
                {
                    continue;
                }

                Dematerialize(record);
                pool.Remove(record);
            }
        }
    }

    /// <summary>
    /// チュートリアルの魚種フィルタに一致する prefab を、このレベルの候補から探す。
    /// フィルタが無い・一致が無い場合は空文字を返し、呼び出し側は通常の抽選へ落ちる。
    /// </summary>
    /// <param name="level">対象のレベル定義。</param>
    /// <returns>一致した .actor パス。無ければ空文字。</returns>
    private static string PickTutorialPrefab(FishLevelEntry level)
    {
        if (!TutorialRules.Active) { return string.Empty; }

        string filter = TutorialRules.FishPrefabFilter;
        if (string.IsNullOrWhiteSpace(filter)) { return string.Empty; }

        string? hit = FindPrefabContaining(level.fishPrefabs, filter)
                   ?? FindPrefabContaining(level.rareFishPrefabs, filter);
        return hit ?? string.Empty;
    }

    /// <summary>
    /// prefab パスのリストから、指定の文字列を含む最初のパスを探す。
    /// </summary>
    /// <param name="paths">探索対象のリスト（null 可）。</param>
    /// <param name="needle">含まれていてほしい文字列（ファイル名の一部など）。</param>
    /// <returns>見つかったパス。無ければ null。</returns>
    private static string? FindPrefabContaining(List<string>? paths, string needle)
    {
        if (paths is null) { return null; }
        for (int i = 0; i < paths.Count; i++)
        {
            string candidate = paths[i];
            if (string.IsNullOrWhiteSpace(candidate)) { continue; }
            if (candidate.Contains(needle, System.StringComparison.OrdinalIgnoreCase)) { return candidate; }
        }
        return null;
    }

    /// <summary>
    /// 魚 1 匹の出現位置を決める【出現位置決定の唯一の実装】。
    /// 通常は円環内のランダム、台本の位置指定があればその近傍。
    /// </summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <param name="usePositionOverride">true なら位置指定を使う。</param>
    /// <param name="overrideCenter">位置指定の中心。</param>
    /// <param name="overrideRadius">位置指定のばらつき半径。</param>
    /// <param name="spawnPos">決定した出現位置。</param>
    /// <returns>位置を決められたら true（円環の設定不備なら false）。</returns>
    private bool TryPickSpawnPosition(
        int levelIndex,
        bool usePositionOverride,
        SEED.Vector3 overrideCenter,
        float overrideRadius,
        out SEED.Vector3 spawnPos)
    {
        spawnPos = SEED.Vector3.Zero;

        if (usePositionOverride)
        {
            // 台本の位置指定: 指定点まわりの円内へ一様に散らす。
            float overrideAngle    = (float)random.NextDouble() * FullTurnRadians;
            float overrideDistance = (float)random.NextDouble() * overrideRadius;
            spawnPos = new SEED.Vector3(
                overrideCenter.x + SEED.Mathf.Sin(overrideAngle) * overrideDistance,
                spawnHeight,
                overrideCenter.z + SEED.Mathf.Cos(overrideAngle) * overrideDistance);
            return true;
        }

        // 出現距離の範囲（円環）= 中心点から各マーカーまでの XZ 平面距離。
        // マーカー未設定・近遠が逆などの設定不備なら生成しない（静かに握り潰さない）。
        var center = CenterPosition();
        if (!TryGetRing(levelIndex, out var ring)) { return false; }

        // 出現位置: 円環内の一様ランダム距離 × ランダム方位。
        float angle = (float)random.NextDouble() * FullTurnRadians;
        float span = ring.Far - ring.Near;
        float distance = ring.Near + (float)random.NextDouble() * span;

        // 高さ: 「生成する高さ(Y)」の固定値をそのまま使う。
        spawnPos = new SEED.Vector3(
            center.x + SEED.Mathf.Sin(angle) * distance,
            spawnHeight,
            center.z + SEED.Mathf.Cos(angle) * distance);
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
        SEED.Debug.Log($"[FishManager] 中心点=({center.x:F1}, {center.z:F1}) / レベル数={levels.Count}"
            + $" / 実体化レベル段数={activeLevelSpan} / プール更新間隔={poolUpdateIntervalFrames}f");

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
                + $" 維持数={MaintainCountOf(i)}");

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
    /// 実体化済みの魚を自分のレベルの円環内へ押し戻す。
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
    ///
    /// 仮想の個体は <see cref="VirtualFishPool.DriftVirtual"/> が同じ規則
    /// （<see cref="ClampPositionToRing"/>）で押し戻すので、ここでは触らない。
    /// </summary>
    private void ClampFishToRings()
    {
        // 釣りコントローラ（未起動なら null）。毎フレーム引き直す（ホットリロードで実体が変わるため）。
        var fishing = FishingController.Current;

        var center = CenterPosition();

        for (int level = 0; level < pool.LevelCount; level++)
        {
            // 円環はレベルごとに 1 回だけ解決する（魚 1 匹ごとに引くとマーカーの
            // Transform 読み出しが個体数ぶん走ってしまうため）
            if (!TryGetRing(level, out var ring)) { continue; }

            var records = pool.RecordsOf(level);
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (!record.Materialized) { continue; }

                // 餌に寄っている／食いついている魚はクランプしない（上のコメント参照）
                if (fishing is { } fc && fc.IsEngaged(record.Actor)) { continue; }

                if (record.Actor.GetComponent<SEED.Transform>() is not { } t || !t.IsValid) { continue; }

                var pos = t.Position;
                var clamped = ClampToRing(ring, center, pos);
                if (SEED.Mathf.Abs(clamped.x - pos.x) < DistanceEpsilon
                 && SEED.Mathf.Abs(clamped.z - pos.z) < DistanceEpsilon)
                {
                    continue;
                }
                t.Position = clamped;
            }
        }
    }

    /// <summary>
    /// ワールド座標を指定レベルの円環内へ収める
    /// 【はみ出しの押し戻しの唯一の実装】（実体・仮想の両方が使う）。
    ///
    /// 中心からの方位は保ったまま、距離だけを [内側半径, 外側半径] にクランプする。
    /// 円環を解決できないレベルでは何もしない（座標をそのまま返す）。
    /// </summary>
    /// <param name="levelIndex">レベル（levels の添字、0 始まり）。</param>
    /// <param name="position">押し戻す前のワールド座標。</param>
    /// <returns>円環内へ収めたワールド座標（高さはそのまま）。</returns>
    private SEED.Vector3 ClampPositionToRing(int levelIndex, SEED.Vector3 position)
    {
        if (!TryGetRing(levelIndex, out var ring)) { return position; }
        return ClampToRing(ring, CenterPosition(), position);
    }

    /// <summary>
    /// 解決済みの円環と中心点を使って座標を押し戻す【押し戻しの計算そのもの】。
    /// 円環の解決（マーカーの読み出し）は呼び出し側で済ませておくので、
    /// 多数の個体へ同じ円環を適用するときに無駄な読み出しが起きない。
    /// </summary>
    /// <param name="ring">解決済みの円環。</param>
    /// <param name="center">出現円環の中心（ワールド座標）。</param>
    /// <param name="position">押し戻す前のワールド座標。</param>
    /// <returns>円環内へ収めたワールド座標（高さはそのまま）。</returns>
    private static SEED.Vector3 ClampToRing(SpawnRing ring, SEED.Vector3 center, SEED.Vector3 position)
    {
        float dx = position.x - center.x;
        float dz = position.z - center.z;
        float distance = SEED.Mathf.Sqrt(dx * dx + dz * dz);
        float clamped = SEED.Mathf.Clamped(distance, ring.Near, ring.Far);
        if (SEED.Mathf.Abs(clamped - distance) < DistanceEpsilon) { return position; }

        // 中心に重なっているときは方位を決められないので、既定の方位(+X)で外へ出す
        float dirX = distance > DistanceEpsilon ? dx / distance : 1f;
        float dirZ = distance > DistanceEpsilon ? dz / distance : 0f;
        return new SEED.Vector3(center.x + dirX * clamped, position.y, center.z + dirZ * clamped);
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

    /// <summary>2 点間の XZ 平面上の距離の 2 乗（比較専用。平方根を省く）。</summary>
    private static float SqrDistanceXZ(SEED.Vector3 a, SEED.Vector3 b)
    {
        float dx = b.x - a.x;
        float dz = b.z - a.z;
        return dx * dx + dz * dz;
    }
}
