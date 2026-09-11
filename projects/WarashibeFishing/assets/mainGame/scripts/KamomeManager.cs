using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

// ゲーム向けエンジン API（Mathf / Vector3 / Time / Random / Debug / GameObject など）は
// SEED 名前空間にあります。System と型名が衝突する（例: Random ↔ System.Random）ため、
// エンジン側からは using を付けていません。「SEED.」で修飾して呼び出しています。
// 詳細は docs/scripting_api.md を参照。

/// <summary>
/// カモメの群れを 1 アクタから生成・駆動するマネージャ。
///
/// 役割:
/// - Play 開始時にカモメ prefab（.actor）を指定数 Instantiate する
/// - 各個体を状態機械（Flying / Dive）で毎フレーム駆動する
/// - ウェイポイントを「魚の多い場所（ホットスポット）」へバイアスをかけて選ぶ
///
/// カモメ側のアクタにはスクリプトを付けません（個体数が増えてもスクリプトインスタンスは
/// 1 つで済み、群れ全体の判断を 1 か所に集約できるため）。
///
/// 魚密度は現状プレースホルダ（起動時のランダム点）です。
/// 実データへ差し替えるときは <see cref="GetFishHotspots"/> だけを書き換えれば済むように
/// 参照点の供給をこの 1 関数へ閉じ込めています。
/// </summary>
public class KamomeManager : SEEDScript
{
    // ------------------------------------------------------------------
    // 定数（マジックナンバー禁止のため、インスペクタへ出さない値はすべて const 化）
    // ------------------------------------------------------------------

    /// <summary>1 回転の角度（度）。ヨー角の最短差分計算に使う。</summary>
    private const float FullTurnDegrees = 360f;

    /// <summary>半回転の角度（度）。ヨー角の最短差分計算に使う。</summary>
    private const float HalfTurnDegrees = 180f;

    /// <summary>ゼロ除算・ゼロ長ベクトル判定用のしきい値（2 乗長で比較）。</summary>
    private const float SqrEpsilon = 1e-6f;

    /// <summary>アセット仮想パスの接頭辞。prefab パスに付いていなければ補う。</summary>
    private const string AssetSchemePrefix = "assets://";

    /// <summary>個体ごとの到着判定半径のゆらぎ幅（基準半径に対する倍率の下限／上限）。</summary>
    private const float ArriveRadiusJitterMin = 0.7f;
    private const float ArriveRadiusJitterMax = 1.4f;

    /// <summary>個体ごとの上下動（サイン波）の周期倍率のゆらぎ幅。</summary>
    private const float BobSpeedJitterMin = 0.6f;
    private const float BobSpeedJitterMax = 1.5f;

    /// <summary>上下動の初期位相のばらつき範囲（ラジアン）。群れが一斉に上下しないようにする。</summary>
    private const float BobPhaseRandomMax = 6.2831853f;   // 2π

    /// <summary>スポーン試行が全滅したときに警告を出すための、生成成功数のしきい値。</summary>
    private const int NoSpawnCount = 0;

    // ------------------------------------------------------------------
    // インスペクタ公開パラメータ
    // ------------------------------------------------------------------

    /// <summary>カモメ prefab（.actor）の仮想パス。空なら生成せず警告のみ出して動作継続する。</summary>
    [Header("生成")]
    [SerializeField(Label = "カモメ prefab パス", Tooltip = "assets:// から始まる .actor パス。省略時は assets:// を自動で補う")]
    private string kamomePrefabPath = "assets://actors/kamome.actor";

    /// <summary>生成する個体数。</summary>
    [SerializeField(Label = "生成数")]
    private int spawnCount = 8;

    /// <summary>行動範囲の XZ 半径（half extent）。このアクタの位置を中心とした正方形範囲。</summary>
    [SerializeField(Label = "行動範囲 XZ 半径")]
    private float areaHalfExtent = 40f;

    /// <summary>飛行高度帯の下限（このアクタの Y からの相対値）。</summary>
    [SerializeField(Label = "高度帯 下限")]
    private float altitudeMin = 6f;

    /// <summary>飛行高度帯の上限（このアクタの Y からの相対値）。</summary>
    [SerializeField(Label = "高度帯 上限")]
    private float altitudeMax = 14f;

    /// <summary>個体ごとの巡航速度の下限（m/s）。</summary>
    [Header("飛行")]
    [SerializeField(Label = "速度 最小")]
    private float speedMin = 3.0f;

    /// <summary>個体ごとの巡航速度の上限（m/s）。</summary>
    [SerializeField(Label = "速度 最大")]
    private float speedMax = 6.0f;

    /// <summary>ウェイポイント到着とみなす距離の基準値。個体ごとに倍率でゆらがせる。</summary>
    [SerializeField(Label = "到着判定距離")]
    private float arriveRadius = 2.0f;

    /// <summary>1 つのウェイポイントに費やせる最大秒数。超えたら諦めて次を選ぶ（詰まり対策）。</summary>
    [SerializeField(Label = "ウェイポイント制限時間(秒)")]
    private float waypointTimeout = 12f;

    /// <summary>進行方向へ向き直る速さ（1 秒あたりの補間係数）。大きいほど機敏に曲がる。</summary>
    [SerializeField(Label = "旋回追従の速さ")]
    private float turnLerpRate = 2.5f;

    /// <summary>サイン波による上下動の振幅（m）。0 で無効。</summary>
    [Header("上下動（見た目のゆらぎ）")]
    [SerializeField(Label = "上下動 振幅")]
    private float bobAmplitude = 0.5f;

    /// <summary>サイン波による上下動の基準角速度（rad/s）。個体ごとに倍率でゆらがせる。</summary>
    [SerializeField(Label = "上下動 速さ")]
    private float bobSpeed = 1.2f;

    /// <summary>ウェイポイントをホットスポット近傍から選ぶ確率（0〜1）。残りは範囲内の一様ランダム。</summary>
    [Header("魚の多い場所（ホットスポット）")]
    [SerializeField(Label = "ホットスポット指向度")]
    [Range(0f, 1f)]
    private float hotspotBias = 0.7f;

    /// <summary>ホットスポット近傍を選ぶときの散らばり半径（m）。</summary>
    [SerializeField(Label = "ホットスポット散らばり半径")]
    private float hotspotRadius = 8f;

    /// <summary>プレースホルダのホットスポット個数（手動指定が 1 つも無いときだけ使われる）。</summary>
    [SerializeField(Label = "仮ホットスポット数")]
    private int placeholderHotspotCount = 3;

    /// <summary>
    /// 手動指定のホットスポット（Hierarchy から D&D）。1 つでも指定されていれば、
    /// プレースホルダのランダム生成より優先される。
    /// インスペクタは配列を扱えないため固定スロットで用意している。
    /// </summary>
    [SerializeField(Label = "ホットスポット手動指定 1")]
    private SEED.Transform? hotspot1 = null;

    [SerializeField(Label = "ホットスポット手動指定 2")]
    private SEED.Transform? hotspot2 = null;

    [SerializeField(Label = "ホットスポット手動指定 3")]
    private SEED.Transform? hotspot3 = null;

    [SerializeField(Label = "ホットスポット手動指定 4")]
    private SEED.Transform? hotspot4 = null;

    // ------------------------------------------------------------------
    // 実行時状態
    // ------------------------------------------------------------------

    /// <summary>
    /// カモメ 1 個体の状態。
    /// ユーザースクリプトは全ファイルが 1 つのアセンブリへまとめてコンパイルされるため、
    /// 名前衝突を避けてマネージャの入れ子型として宣言している。
    /// </summary>
    public enum KamomeState
    {
        /// <summary>ウェイポイントへ向けて巡航中。</summary>
        Flying,

        /// <summary>水面へ急降下して魚を捕る（現状はスタブ。即 Flying へ戻る）。</summary>
        Diving,
    }

    /// <summary>カモメ 1 個体の実行時状態。マネージャ内部でのみ使う。</summary>
    private sealed class Kamome
    {
        /// <summary>生成したアクタ。</summary>
        public SEED.GameObject Obj;

        /// <summary>現在の状態。</summary>
        public KamomeState State = KamomeState.Flying;

        /// <summary>上下動を除いた論理位置。transform へは これ + 上下動 を書き込む。</summary>
        public SEED.Vector3 Pos;

        /// <summary>現在向かっているウェイポイント。</summary>
        public SEED.Vector3 Waypoint;

        /// <summary>この個体の巡航速度（m/s）。</summary>
        public float Speed;

        /// <summary>この個体の到着判定距離（基準値 × 個体ごとのゆらぎ）。</summary>
        public float ArriveRadius;

        /// <summary>現在のウェイポイントを追いかけている秒数。</summary>
        public float WaypointElapsed;

        /// <summary>現在のヨー角（度）。目標方向へ緩やかに補間する。</summary>
        public float Yaw;

        /// <summary>上下動サイン波の位相（rad）。</summary>
        public float BobPhase;

        /// <summary>上下動サイン波の角速度倍率。</summary>
        public float BobSpeedScale;
    }

    /// <summary>駆動対象の全個体。</summary>
    private readonly List<Kamome> _flock = new List<Kamome>();

    /// <summary>魚の多い場所（ワールド座標）。<see cref="GetFishHotspots"/> の結果をキャッシュしたもの。</summary>
    private readonly List<SEED.Vector3> _hotspots = new List<SEED.Vector3>();

    /// <summary>初回 Update での初期化が済んだか。</summary>
    private bool _initialized = false;

    // ------------------------------------------------------------------
    // ライフサイクル
    // ------------------------------------------------------------------

    /// <summary>
    /// 毎フレームの主更新。初回だけ群れを生成し、以降は全個体の状態機械を回す。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 初期化は「Play 開始後の最初の Update」で 1 度だけ行う。
        // （OnStart ではなく Update にしているのは、生成したアクタの Transform を
        //   同フレーム中に設定できる経路と足並みを揃えるため）
        if (!_initialized)
        {
            _initialized = true;
            Initialize();
        }

        // 全個体を駆動する。dt は ctx から取る（SEED.Time.DeltaTime と同値）。
        float dt = ctx.DeltaTime;
        for (int i = 0; i < _flock.Count; ++i)
        {
            UpdateKamome(i, dt);
        }
    }

    // ------------------------------------------------------------------
    // 初期化
    // ------------------------------------------------------------------

    /// <summary>
    /// ホットスポットの構築と、カモメ個体の生成をまとめて行う。
    /// prefab が読めなくても例外にせず、生成できた分だけで動作を続ける。
    /// </summary>
    private void Initialize()
    {
        // 魚密度（ホットスポット）を先に決める。ウェイポイント選定がこれに依存するため。
        RefreshHotspots();

        // prefab パスが空なら生成しない（警告のみ。マネージャ自体は動作継続）
        string path = NormalizeAssetPath(kamomePrefabPath);
        if (string.IsNullOrEmpty(path))
        {
            SEED.Debug.LogWarning("[KamomeManager] カモメ prefab パスが未設定のため、カモメを生成しませんでした。");
            return;
        }

        SEED.Vector3 origin = transform.Position;
        for (int i = 0; i < spawnCount; ++i)
        {
            var obj = SEED.GameObject.Instantiate(path);
            if (!obj.IsValid)
            {
                // 1 体でも失敗したら以降も同じ理由で失敗するため、1 回だけ警告して打ち切る
                SEED.Debug.LogWarning($"[KamomeManager] カモメ prefab の読み込みに失敗しました: {path}（生成済み {_flock.Count} 体で継続します）");
                break;
            }

            var bird = CreateKamome(obj, origin);
            _flock.Add(bird);

            // 生成直後のフレームでも位置を反映しておく（初期化子が効くのは同フレーム中）
            ApplyTransform(bird);
        }

        if (_flock.Count == NoSpawnCount)
        {
            SEED.Debug.LogWarning("[KamomeManager] カモメを 1 体も生成できませんでした。prefab パスと生成数を確認してください。");
        }
    }

    /// <summary>
    /// 生成済みアクタから 1 個体分の実行時状態を作る。
    /// 速度・到着距離・上下動の位相／速さを個体ごとにばらつかせ、群れの動きを揃わせない。
    /// </summary>
    /// <param name="obj">Instantiate 済みのアクタ。</param>
    /// <param name="origin">行動範囲の中心（マネージャのワールド位置）。</param>
    private Kamome CreateKamome(SEED.GameObject obj, SEED.Vector3 origin)
    {
        var bird = new Kamome
        {
            Obj = obj,
            State = KamomeState.Flying,
            Pos = RandomPointInArea(origin),
            Speed = SEED.Random.Range(speedMin, speedMax),
            ArriveRadius = arriveRadius * SEED.Random.Range(ArriveRadiusJitterMin, ArriveRadiusJitterMax),
            WaypointElapsed = 0f,
            BobPhase = SEED.Random.Range(0f, BobPhaseRandomMax),
            BobSpeedScale = SEED.Random.Range(BobSpeedJitterMin, BobSpeedJitterMax),
        };

        // 最初のウェイポイントを決め、その方向を初期の向きにしておく（初回だけ急旋回しないように）
        bird.Waypoint = PickWaypoint();
        var toTarget = bird.Waypoint - bird.Pos;
        bird.Yaw = YawFromDirection(toTarget, 0f);
        return bird;
    }

    // ------------------------------------------------------------------
    // 状態機械
    // ------------------------------------------------------------------

    /// <summary>
    /// 1 個体を 1 フレーム分進める。状態に応じて処理を振り分ける。
    /// </summary>
    /// <param name="index">個体のインデックス（_flock 内）。</param>
    /// <param name="dt">経過秒。</param>
    private void UpdateKamome(int index, float dt)
    {
        var bird = _flock[index];
        if (!bird.Obj.IsValid) { return; }   // 外部から破棄された個体は触らない

        switch (bird.State)
        {
            case KamomeState.Flying:
                UpdateFlying(bird, dt);
                break;

            case KamomeState.Diving:
                UpdateDiving(bird, dt);
                break;
        }

        // 上下動を乗せてワールドへ反映する
        bird.BobPhase += bobSpeed * bird.BobSpeedScale * dt;
        ApplyTransform(bird);
    }

    /// <summary>
    /// Flying: ウェイポイントへ等速直線移動し、到着かタイムアウトで次のウェイポイントを選ぶ。
    /// 向きは進行方向へヨーのみ緩やかに補間する（ピッチ・ロールは原型では扱わない）。
    /// </summary>
    private void UpdateFlying(Kamome bird, float dt)
    {
        var toTarget = bird.Waypoint - bird.Pos;

        // 等速直線移動（行き過ぎないよう MoveTowards でクランプ）
        bird.Pos = SEED.Vector3.MoveTowards(bird.Pos, bird.Waypoint, bird.Speed * dt);

        // 進行方向へヨーを補間
        bird.Yaw = YawFromDirection(toTarget, bird.Yaw, turnLerpRate * dt);

        // 到着判定 or タイムアウトで次のウェイポイントへ
        bird.WaypointElapsed += dt;
        bool arrived = toTarget.SqrMagnitude <= bird.ArriveRadius * bird.ArriveRadius;
        bool timedOut = bird.WaypointElapsed >= waypointTimeout;
        if (arrived || timedOut)
        {
            bird.Waypoint = PickWaypoint();
            bird.WaypointElapsed = 0f;
        }
    }

    /// <summary>
    /// Dive: スタブ。
    /// TODO: 魚を捕る演出（急降下 → 着水 → 上昇 → 捕獲判定）をここに実装する。
    ///       実装時は Diving 中の位置制御・水面高さ・魚の消費をこの関数に閉じ込め、
    ///       完了時に Flying へ戻す（ウェイポイントも取り直す）。
    /// 現状は何もせず即座に Flying へ戻す。
    /// </summary>
    private void UpdateDiving(Kamome bird, float dt)
    {
        bird.State = KamomeState.Flying;
        bird.Waypoint = PickWaypoint();
        bird.WaypointElapsed = 0f;
    }

    /// <summary>
    /// 指定個体を Dive 状態へ遷移させる。魚を捕捉したときの呼び出し口。
    /// TODO: 実際の捕獲判定（魚の位置・確率）が入ったら、その側からここを呼ぶ。
    /// </summary>
    /// <param name="index">個体のインデックス（_flock 内）。</param>
    /// <returns>遷移できたら true。インデックス不正・Flying でない場合は false。</returns>
    public bool TryStartDive(int index)
    {
        if (index < 0 || index >= _flock.Count) { return false; }

        var bird = _flock[index];
        if (bird.State != KamomeState.Flying) { return false; }

        bird.State = KamomeState.Diving;
        return true;
    }

    /// <summary>駆動中のカモメの個体数（外部から TryStartDive のインデックス範囲を知るため）。</summary>
    public int KamomeCount => _flock.Count;

    // ------------------------------------------------------------------
    // ウェイポイント選定
    // ------------------------------------------------------------------

    /// <summary>
    /// 次のウェイポイントを選ぶ。
    /// 確率 <see cref="hotspotBias"/> で「魚の多い場所」の近傍、それ以外は行動範囲内の一様ランダム。
    /// 高度はいずれの場合も高度帯の中からランダムに取り直す。
    /// </summary>
    private SEED.Vector3 PickWaypoint()
    {
        SEED.Vector3 origin = transform.Position;

        // ホットスポットが 1 つも無ければ常に一様ランダム
        if (_hotspots.Count > 0 && SEED.Random.Value < hotspotBias)
        {
            var spot = _hotspots[SEED.Random.Range(0, _hotspots.Count)];
            var offset = SEED.Random.InsideUnitCircle * hotspotRadius;

            // XZ はホットスポット周辺、Y は高度帯から取り直す（ホットスポット自身の高さは使わない）
            float x = SEED.Mathf.Clamped(spot.x + offset.x, origin.x - areaHalfExtent, origin.x + areaHalfExtent);
            float z = SEED.Mathf.Clamped(spot.z + offset.y, origin.z - areaHalfExtent, origin.z + areaHalfExtent);
            return new SEED.Vector3(x, origin.y + SEED.Random.Range(altitudeMin, altitudeMax), z);
        }

        return RandomPointInArea(origin);
    }

    /// <summary>
    /// 行動範囲（XZ half extent）内・高度帯内のランダムな 1 点を返す。
    /// </summary>
    /// <param name="origin">行動範囲の中心（マネージャのワールド位置）。</param>
    private SEED.Vector3 RandomPointInArea(SEED.Vector3 origin)
    {
        return new SEED.Vector3(
            origin.x + SEED.Random.Range(-areaHalfExtent, areaHalfExtent),
            origin.y + SEED.Random.Range(altitudeMin, altitudeMax),
            origin.z + SEED.Random.Range(-areaHalfExtent, areaHalfExtent));
    }

    // ------------------------------------------------------------------
    // 魚密度（プレースホルダ）
    // ------------------------------------------------------------------

    /// <summary>
    /// <see cref="GetFishHotspots"/> の結果を取り直してキャッシュへ入れる。
    /// 実魚データを使うようになったら、任意のタイミングでこれを呼べば群れの狙いが更新される。
    /// </summary>
    private void RefreshHotspots()
    {
        _hotspots.Clear();
        GetFishHotspots(_hotspots);
    }

    /// <summary>
    /// 「魚の多い場所」のワールド座標を返す。**魚密度の供給点はこの関数だけ**。
    ///
    /// 現在の実装（プレースホルダ）:
    /// 1. 手動指定の Transform 参照（hotspot1〜4）が 1 つでもあれば、それを優先して返す
    /// 2. 無ければ、行動範囲内にランダムな N 点（placeholderHotspotCount）を生成して返す
    ///
    /// 実データへの差し替え手順:
    /// この関数の中身だけを「実際の魚アクタ／魚密度フィールドを走査して密度の高い座標を積む」
    /// 実装へ置き換える。呼び出し側（PickWaypoint）は座標のリストしか見ていないため、
    /// 他の箇所は一切変更しなくてよい。
    /// </summary>
    /// <param name="into">結果を積むリスト（呼び出し側で Clear 済み）。</param>
    private void GetFishHotspots(List<SEED.Vector3> into)
    {
        // 1) 手動指定が優先
        AddHotspotIfSet(into, hotspot1);
        AddHotspotIfSet(into, hotspot2);
        AddHotspotIfSet(into, hotspot3);
        AddHotspotIfSet(into, hotspot4);
        if (into.Count > 0) { return; }

        // 2) プレースホルダ: 行動範囲内のランダム点
        SEED.Vector3 origin = transform.Position;
        for (int i = 0; i < placeholderHotspotCount; ++i)
        {
            // 高度は使わない（ウェイポイントの Y は高度帯から取り直す）ので origin.y のままでよい
            into.Add(new SEED.Vector3(
                origin.x + SEED.Random.Range(-areaHalfExtent, areaHalfExtent),
                origin.y,
                origin.z + SEED.Random.Range(-areaHalfExtent, areaHalfExtent)));
        }
    }

    /// <summary>手動指定スロットが設定されていれば、その位置をホットスポットとして積む。</summary>
    private void AddHotspotIfSet(List<SEED.Vector3> into, SEED.Transform? slot)
    {
        if (slot is { } t) { into.Add(t.Position); }
    }

    // ------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------

    /// <summary>
    /// 論理位置＋上下動＋ヨー角を、実際のアクタの Transform へ書き込む。
    /// </summary>
    private void ApplyTransform(Kamome bird)
    {
        if (bird.Obj.GetComponent<SEED.Transform>() is not { } t) { return; }

        float bob = SEED.Mathf.Sin(bird.BobPhase) * bobAmplitude;
        t.Position = bird.Pos + SEED.Vector3.Up * bob;
        t.Rotation = new SEED.Vector3(0f, bird.Yaw, 0f);
    }

    /// <summary>
    /// 進行方向からヨー角（度）を求め、現在角から最短方向へ補間して返す。
    /// エンジンの前方向は +Z なので、Atan2(x, z) がヨー角になる。
    /// </summary>
    /// <param name="direction">向きたい方向（正規化不要。ゼロ長なら現在角を維持）。</param>
    /// <param name="currentYaw">現在のヨー角（度）。</param>
    /// <param name="t">補間率。1 以上で即座に目標角へ一致する（既定は即時）。</param>
    private float YawFromDirection(SEED.Vector3 direction, float currentYaw, float t = 1f)
    {
        // XZ 平面へ潰す。真上・真下方向だけの移動ではヨーを変えない。
        var flat = new SEED.Vector3(direction.x, 0f, direction.z);
        if (flat.SqrMagnitude < SqrEpsilon) { return currentYaw; }

        float targetYaw = SEED.Mathf.Atan2(flat.x, flat.z) * SEED.Mathf.Rad2Deg;

        // 最短回り（-180〜+180）の差分を取ってから補間する（359°→1° で遠回りしないように）
        float delta = SEED.Mathf.Repeat(targetYaw - currentYaw + HalfTurnDegrees, FullTurnDegrees) - HalfTurnDegrees;
        return currentYaw + delta * SEED.Mathf.Clamped01(t);
    }

    /// <summary>
    /// アセットパスを正規化する。空文字はそのまま、接頭辞が無ければ assets:// を補う。
    /// </summary>
    private string NormalizeAssetPath(string path)
    {
        if (string.IsNullOrEmpty(path)) { return string.Empty; }
        return path.StartsWith(AssetSchemePrefix) ? path : AssetSchemePrefix + path;
    }
}
