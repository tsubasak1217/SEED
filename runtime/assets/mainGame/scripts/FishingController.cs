using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 釣りの「キャスト（投げる）→ 着水 → リール（巻く）」を司るコントローラ。
///
/// <b>プレイヤーアクタに付ける</b>（<see cref="PlayerMove"/> と同じアクタ）。
/// <see cref="PlayerMove"/> が釣り姿勢（<see cref="PlayerMove.PlayerState.FishingStance"/>）の
/// あいだだけ動作し、姿勢を抜けたら即座に全部キャンセルして待機へ戻る。
///
/// <b>操作</b>
/// - キャスト … マウスを「下へ引いて → 上へ振る」ジェスチャ（振り幅が飛距離、横成分が方向）
/// - リール   … マウスホイール、またはマウス移動量（インスペクタで切替）
/// - 巻く向き … A / D キーで左右に振れる（島中心方向を基準に ±範囲内）
///
/// <b>担当範囲</b>
/// このスクリプトは「ウキの位置」と「釣り糸の点列」だけを毎フレーム決める。
/// プレイヤーの移動は <see cref="PlayerMove.MoveTowardWorldPoint"/> へ委譲し、
/// カメラ構図は CameraMove が本スクリプトの <see cref="State"/> を見て切り替える
/// （単一責任: 移動＝PlayerMove / 構図＝CameraMove / 釣り＝本スクリプト）。
///
/// <b>ヒット判定は未実装</b>。<see cref="hookedFish"/> のプレースホルダと
/// <see cref="FishState.Result"/> への分岐だけを用意してある。
/// </summary>
public class FishingController : SEEDScript
{
    /// <summary>
    /// 釣りの進行状態。
    ///
    /// スクリプトはファイル名＝型名で 1 ファイル 1 スクリプトクラスとして扱われるため、
    /// この列挙型は独立ファイルにせず本クラスの入れ子として定義する
    /// （外部からは <c>FishingController.FishState</c> で参照できる）。
    /// </summary>
    public enum FishState
    {
        /// <summary>釣り姿勢に入っていない。ウキは竿先に格納し、糸は非表示。</summary>
        Idle,

        /// <summary>釣り姿勢中でキャスト待ち。マウスのジェスチャを待ち受ける。</summary>
        Aiming,

        /// <summary>キャスト直後。ウキが放物線を描いて着水点へ飛んでいる。</summary>
        Casting,

        /// <summary>着水後。ウキが水面で待機している（アタリ待ち）。</summary>
        Floating,

        /// <summary>巻き取り中。ウキが手前へ寄り、プレイヤーもウキの方へ歩く。</summary>
        Reeling,

        /// <summary>釣果の演出中（ヒット機構の実装待ちのプレースホルダ）。</summary>
        Result,
    }

    /// <summary>現在の釣り状態（他スクリプトから参照する読み取り専用プロパティ）。</summary>
    public FishState State { get; private set; } = FishState.Idle;

    /// <summary>
    /// キャストのジェスチャ進行段階（内部専用）。
    /// 「下へ引く（Pull）」→「上へ振る（Push）」の 2 段で 1 回のキャストになる。
    /// </summary>
    private enum GesturePhase
    {
        /// <summary>下方向へ引いている段階（振りかぶり）。</summary>
        Pull,

        /// <summary>上方向へ振っている段階（振り抜き）。ここでしきい値を超えるとキャストする。</summary>
        Push,
    }

    // ゲーム向けエンジン API（Mathf/Vector3/Time/Input/Debug など）は SEED 名前空間にある。
    // System と型名が衝突するため using は付けず「SEED.」で修飾する（docs/scripting_api.md）。

    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>ベクトルの「長さがほぼ 0」を判定する二乗長のしきい値。</summary>
    private const float SqrEpsilon = 1e-6f;

    /// <summary>1 周（ラジアン）。ウキの上下揺れ（サイン波）の位相計算に使う。</summary>
    private const float TwoPi = SEED.Mathf.PI * 2f;

    /// <summary>放物線の頂点係数。<c>4h·t·(1-t)</c> は t=0.5 で h になる（h＝最高点の高さ）。</summary>
    private const float ParabolaApexCoefficient = 4f;

    /// <summary>この値以下のリール入力量（メートル）は「入力なし」とみなす。</summary>
    private const float ReelInputEpsilon = 1e-4f;

    /// <summary>糸の点列の最小分割数（1 ＝ 直線）。</summary>
    private const int MinLineSegments = 1;

    // ─── 参照（インスペクタで割り当てる）───────────────────────

    /// <summary>
    /// プレイヤーの移動スクリプト。釣り姿勢かどうかの判定と、巻き取り中の追従移動に使う。
    /// <b>未設定なら本スクリプトは何もしない</b>（釣り姿勢を知る手段が無いため）。
    /// </summary>
    [Header("参照"), SerializeField(Label = "プレイヤー（PlayerMove）")]
    private PlayerMove? playerMove = null;

    /// <summary>
    /// 竿先のトランスフォーム（糸の始点・キャストの起点・ウキの格納先）。
    /// <see cref="rodRoot"/> を設定した場合は毎フレーム本スクリプトが位置を上書きする。
    /// </summary>
    [SerializeField(Label = "竿先トランスフォーム")]
    private SEED.Transform? rodTip = null;

    /// <summary>
    /// 竿アクタ本体のトランスフォーム（sao）。設定すると竿先を
    /// 「竿の姿勢＋<see cref="rodTipOffsetX"/>〜Z のローカルオフセット」から毎フレーム算出する。
    ///
    /// <b>なぜ必要か</b>: 竿は JointAttach で手のボーンに追従するが、
    /// JointAttach の Transform 更新は transform_sync を経由しないため
    /// <b>子アクタへ伝播しない</b>（jointattach_ops.rs で自アクタの Transform を直接書くだけ）。
    /// つまり竿の子に置いただけの竿先は竿に付いてこない。ここで毎フレーム追従させる。
    /// 未設定なら <see cref="rodTip"/> の位置をそのまま使う（静止した竿先）。
    /// </summary>
    [SerializeField(Label = "竿アクタ（竿先追従用）")]
    private SEED.Transform? rodRoot = null;

    /// <summary>竿先の竿ローカルオフセット X（竿の右方向・竿のスケール込み）。</summary>
    [SerializeField(Label = "竿先オフセットX")]
    private float rodTipOffsetX = 0f;

    /// <summary>竿先の竿ローカルオフセット Y（竿の上方向・竿のスケール込み）。</summary>
    [SerializeField(Label = "竿先オフセットY")]
    private float rodTipOffsetY = 1.0f;

    /// <summary>竿先の竿ローカルオフセット Z（竿の前方向・竿のスケール込み）。</summary>
    [SerializeField(Label = "竿先オフセットZ")]
    private float rodTipOffsetZ = 0f;

    /// <summary>ウキ（浮き）のトランスフォーム。本スクリプトが毎フレーム位置を決める。</summary>
    [SerializeField(Label = "ウキのトランスフォーム")]
    private SEED.Transform? uki = null;

    /// <summary>釣り糸の LineRenderer（ウキ側に付ける想定）。未設定なら糸を描かない。</summary>
    [SerializeField(Label = "釣り糸(LineRenderer)")]
    private SEED.LineRenderer? line = null;

    /// <summary>
    /// 水面（WaterVolume）。着水点の Y とウキの浮かぶ高さに使う。
    /// 未設定なら「竿先の Y − <see cref="waterLevelFallbackDrop"/>」を水面とみなす。
    /// </summary>
    [SerializeField(Label = "水面(WaterVolume)")]
    private SEED.WaterVolume? water = null;

    /// <summary>
    /// 島の中心（巻き取り方向の基準）。未設定ならプレイヤー自身の位置を中心として使う。
    /// </summary>
    [SerializeField(Label = "島の中心（巻く方向の基準）")]
    private SEED.Transform? islandCenter = null;

    /// <summary>釣り竿の Animator。未設定なら竿のアニメ切替は行わない。</summary>
    [SerializeField(Label = "竿の Animator")]
    private SEED.Animator? rodAnimator = null;

    /// <summary>
    /// プレイヤー本体（sakanadori）の Animator。
    ///
    /// <b>竿モデル（sao.glb）のクリップにはアニメーションチャンネルが無く、竿自体は動かない</b>。
    /// キャストや巻き取りの実際の動きはすべてプレイヤー本体側のクリップが担っており、
    /// 竿は JointAttach で手のボーンに追従して見た目上ついてくるだけである。
    /// そのため竿 Animator と本体 Animator は常にペアで同じタイミングのクリップへ切り替える。
    /// 未設定なら本体側のアニメ切替は行わない（竿だけが動く従来動作にフォールバック）。
    /// </summary>
    [SerializeField(Label = "プレイヤー本体の Animator")]
    private SEED.Animator? playerAnimator = null;

    // ─── アニメーション ───────────────────────────────────────

    /// <summary>キャストの瞬間に再生する竿クリップ名。</summary>
    [Header("アニメーション"), SerializeField(Label = "竿のキャストクリップ名")]
    private string castClip = "Cast_竿";

    /// <summary>ウキが浮いているあいだ再生する竿クリップ名。</summary>
    [SerializeField(Label = "竿の待ちクリップ名")]
    private string floatClip = "IdleFishing_竿";

    /// <summary>巻き取り中に再生する竿クリップ名。</summary>
    [SerializeField(Label = "竿の巻き取りクリップ名")]
    private string reelClip = "Reel_竿";

    // ─── 本体アニメーション ───────────────────────────────────

    /// <summary>キャストの瞬間に再生するプレイヤー本体クリップ名。</summary>
    [Header("本体アニメーション"), SerializeField(Label = "本体のキャストクリップ名")]
    private string playerCastClip = "Cast";

    /// <summary>巻き取り中に再生するプレイヤー本体クリップ名。</summary>
    [SerializeField(Label = "本体の巻き取りクリップ名")]
    private string playerReelClip = "Reel";

    /// <summary>ウキが浮いているあいだ再生するプレイヤー本体クリップ名。</summary>
    [SerializeField(Label = "本体の待ちクリップ名")]
    private string playerFloatClip = "IdleFishing";

    /// <summary>竿クリップ切替時のクロスフェード秒数（0 で即時切替）。竿・本体の両方に使う。</summary>
    [SerializeField(Label = "切替フェード(秒)")]
    private float fadeSeconds = 0.15f;

    /// <summary>
    /// キャスト予備動作（引き構え）として <see cref="castClip"/> を止めておく再生位置（秒）。
    /// Pull 段階のあいだ、この時間まで竿が振りかぶった姿勢を追従表示し、そこで止め置く。
    /// キャストが成立したら、この位置から通常速度で再生を続ける。
    /// </summary>
    [SerializeField(Label = "キャスト予備動作の停止位置(秒)")]
    private float castWindupSeconds = 0.4f;

    /// <summary>
    /// 予備動作中、狙いの再生位置（引き量に比例した目標時間）へ追従する速さ。
    /// 大きいほど追従が速く（マウスのブレをそのまま拾いやすく）、小さいほど滑らかになる。
    /// <see cref="ExponentialBlend"/> の減衰率として使う（1 秒あたりの追従率の目安、単位は 1/秒）。
    /// </summary>
    [SerializeField(Label = "予備動作の追従率")]
    private float windupScrubRate = 15f;

    // ─── キャストのジェスチャ ─────────────────────────────────

    /// <summary>
    /// 第 1 段階（下へ引く）の累積移動量のしきい値（px）。
    /// これを超えると第 2 段階（上へ振る）の受付を開始する。
    /// </summary>
    [Header("キャストのジェスチャ"), SerializeField(Label = "引き量のしきい値(px)")]
    private float pullThresholdPx = 60f;

    /// <summary>
    /// 第 2 段階（上へ振る）の累積上方向移動量のしきい値（px）。
    /// これを超えた瞬間にキャストが成立する。
    /// </summary>
    [SerializeField(Label = "振り量のしきい値(px)")]
    private float pushThresholdPx = 80f;

    /// <summary>
    /// この px 未満のフレーム移動量は「動いていない」とみなす（手ぶれ・センサノイズ対策）。
    /// </summary>
    [SerializeField(Label = "移動とみなす最小量(px)")]
    private float gestureMoveEpsilonPx = 1.0f;

    /// <summary>
    /// 逆方向へこの px を超えて動いたらジェスチャを最初からやり直す（振り戻しの誤爆防止）。
    /// </summary>
    [SerializeField(Label = "逆行の許容量(px)")]
    private float gestureReverseTolerancePx = 12f;

    /// <summary>
    /// Pull 段階（引きの途中）でこの秒数だけ有意な動きが無ければジェスチャをリセットする。
    /// Push 段階（振り抜き待ち）には適用されない（→ <see cref="armedTimeoutSeconds"/>）。
    /// </summary>
    [SerializeField(Label = "引き段階のタイムアウト(秒)")]
    private float gestureTimeoutSeconds = 0.6f;

    /// <summary>
    /// Push 段階（振りかぶりポーズで振り抜き待ち）でこの秒数だけ有意な動きが無ければ
    /// ジェスチャを最初からリセットする。0 以下なら無制限に待機し続ける
    /// （振りかぶった姿勢のまま、いつまでも振り抜きを受け付ける）。
    /// </summary>
    [SerializeField(Label = "振り待ちのタイムアウト(秒, 0=無制限)")]
    private float armedTimeoutSeconds = 0f;

    // ─── キャストの飛距離・方向 ───────────────────────────────

    /// <summary>振り量 1px あたりの飛距離（メートル）。</summary>
    [Header("キャスト"), SerializeField(Label = "1pxあたりの飛距離(m)")]
    private float metersPerPixel = 0.05f;

    /// <summary>飛距離の下限（メートル）。</summary>
    [SerializeField(Label = "最短飛距離(m)")]
    private float minCastDistance = 3f;

    /// <summary>飛距離の上限（メートル）。</summary>
    [SerializeField(Label = "最長飛距離(m)")]
    private float maxCastDistance = 25f;

    /// <summary>
    /// 振りの横成分から方向をどれだけ振るかの倍率
    /// （1.0 で「振りの角度そのまま」＝真上に振れば正面、斜めに振ればその角度ぶん横へ）。
    /// </summary>
    [SerializeField(Label = "方向の感度")]
    private float directionSensitivity = 1.0f;

    /// <summary>正面（プレイヤーの向き）からの左右の最大ずれ角（度）。</summary>
    [SerializeField(Label = "最大キャスト角(度)")]
    private float maxCastAngleDegrees = 45f;

    /// <summary>
    /// 水面が未設定のときに使う「竿先からの落差」（メートル）。
    /// この値だけ竿先より下を仮の水面とみなす。
    /// </summary>
    [SerializeField(Label = "水面の代替落差(m)")]
    private float waterLevelFallbackDrop = 2f;

    /// <summary>竿先から着水点まで飛ぶのにかかる秒数。</summary>
    [SerializeField(Label = "飛翔時間(秒)")]
    private float flightSeconds = 0.8f;

    /// <summary>飛翔の放物線の頂点の高さ（直線補間からの持ち上げ量、メートル）。</summary>
    [SerializeField(Label = "飛翔の山の高さ(m)")]
    private float flightApexHeight = 3f;

    // ─── 釣り糸 ───────────────────────────────────────────────

    /// <summary>糸の分割数（点数は分割数＋1。LineRenderer の上限を超える値は丸められる）。</summary>
    [Header("釣り糸"), SerializeField(Label = "糸の分割数")]
    private int lineSegments = 16;

    /// <summary>飛翔中の糸のたるみ（メートル）。飛んでいる最中は糸が張らないので小さめ。</summary>
    [SerializeField(Label = "飛翔中のたるみ(m)")]
    private float flightSlack = 0.3f;

    /// <summary>ウキまでの距離 1m あたりのたるみ量（メートル）。</summary>
    [SerializeField(Label = "距離あたりのたるみ(m/m)")]
    private float slackPerMeter = 0.06f;

    /// <summary>たるみの上限（メートル）。遠投しても糸が地面まで垂れないようにする。</summary>
    [SerializeField(Label = "たるみの上限(m)")]
    private float maxSlack = 1.5f;

    // ─── リール（巻き取り）─────────────────────────────────────

    /// <summary>
    /// 巻き取り入力にマウスホイールを使うか。
    /// true  … <see cref="SEED.Input.MouseScroll"/> の絶対量（どちら回しでも巻ける）
    /// false … <see cref="SEED.Input.MouseDelta"/> の移動量（グルグル回す操作）
    ///
    /// ※ 列挙型は [SerializeField] のインスペクタ対応型に含まれない
    ///   （ScriptBridge の型タグは float/int/bool/string/参照/配列のみ）ため bool で表す。
    /// </summary>
    [Header("リール"), SerializeField(Label = "ホイールで巻く（オフ=マウス移動）")]
    private bool reelByWheel = true;

    /// <summary>入力 1 単位あたりの巻き取り距離（メートル）。ホイール 1 ノッチ／マウス 1px に対する量。</summary>
    [SerializeField(Label = "入力1単位あたりの巻き量(m)")]
    private float metersPerUnit = 0.05f;

    /// <summary>この秒数だけ巻き入力が無ければ巻き取りを止めて待機（Floating）へ戻る。</summary>
    [SerializeField(Label = "巻き取り停止までの猶予(秒)")]
    private float reelIdleSeconds = 0.25f;

    /// <summary>A / D キーで巻く方向を振る速さ（度／秒）。</summary>
    [SerializeField(Label = "方向転換の速さ(度/秒)")]
    private float reelTurnSpeedDegPerSec = 60f;

    /// <summary>巻く方向の左右振れ幅（度）。基準方向から ±この半分まで振れる。</summary>
    [SerializeField(Label = "方向の振れ幅(度)")]
    private float reelAngleRangeDegrees = 100f;

    /// <summary>ウキと竿先の水平距離がこの値以下になったら巻き取り完了とみなす（メートル）。</summary>
    [SerializeField(Label = "巻き取り完了距離(m)")]
    private float reelEndDistance = 1.5f;

    /// <summary>水面に浮いているウキの上下揺れの振幅（メートル）。0 で揺れなし。</summary>
    [SerializeField(Label = "ウキの揺れ幅(m)")]
    private float bobAmplitude = 0.05f;

    /// <summary>ウキの上下揺れの周波数（Hz）。</summary>
    [SerializeField(Label = "ウキの揺れ周期(Hz)")]
    private float bobFrequency = 0.6f;

    /// <summary>
    /// 釣り中（狙い〜巻き取り）にカーソルをロックするか。
    ///
    /// ロック中はカーソルが非表示になり、毎フレーム画面中央へ戻される。
    /// エディタ埋め込み Play ではカーソルがビューポートに閉じ込められる（ClipCursor）ため、
    /// ロックしないと端に当たった瞬間 <see cref="SEED.Input.MouseDelta"/> が 0 になり、
    /// 引く／振るのジェスチャが取れなくなる。巻き取り（マウス移動）でも同じ利点がある。
    /// UI をマウスで操作したい場面が出たらここをオフにする。
    /// </summary>
    [Header("操作"), SerializeField(Label = "狙い中はカーソルをロック")]
    private bool lockCursorWhileFishing = true;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>現在のジェスチャ段階。</summary>
    private GesturePhase gesturePhase = GesturePhase.Pull;

    /// <summary>第 1 段階で下方向へ動いた累積量（px、正値）。</summary>
    private float pullAccumPx = 0f;

    /// <summary>
    /// キャスト予備動作（振りかぶりポーズでの停止スクラブ）を実行中か。
    /// true のあいだ竿 Animator の再生速度は 0 に固定し、<see cref="Time"/> を手動で狙い位置へ寄せる。
    /// </summary>
    private bool windupActive = false;

    /// <summary>第 2 段階で動いた累積ベクトル（px。y は画面下向きが正なので振り上げると負になる）。</summary>
    private SEED.Vector2 pushAccumPx = SEED.Vector2.Zero;

    /// <summary>最後に有意な動きがあってからの経過秒数（タイムアウト判定用）。</summary>
    private float gestureIdleSeconds = 0f;

    /// <summary>飛翔開始位置（ワールド。キャスト時の竿先）。</summary>
    private SEED.Vector3 flightStart = SEED.Vector3.Zero;

    /// <summary>着水点（ワールド）。</summary>
    private SEED.Vector3 flightEnd = SEED.Vector3.Zero;

    /// <summary>飛翔開始からの経過秒数。</summary>
    private float flightElapsed = 0f;

    /// <summary>このキャストの飛距離（メートル）。糸のたるみ量の算出に使う。</summary>
    private float castDistance = 0f;

    /// <summary>巻く方向の基準からのずれ角（度）。A / D キーで増減する。</summary>
    private float reelAngleOffsetDegrees = 0f;

    /// <summary>最後に巻き入力があってからの経過秒数（Floating へ戻す判定用）。</summary>
    private float reelIdleElapsed = 0f;

    /// <summary>ウキの上下揺れの位相用に積算した秒数。</summary>
    private float bobElapsed = 0f;

    /// <summary>
    /// 魚が掛かっているか（<b>ヒット判定は未実装のプレースホルダ</b>）。
    /// true になれば巻き取り完了時に <see cref="FishState.Result"/> へ遷移する。
    /// </summary>
    private bool hookedFish = false;

    // ─── ライフサイクル ───────────────────────────────────────

    /// <summary>
    /// 生成直後の初期化。糸をワールド座標系（親子合成なし）で扱う設定にし、初期状態は非表示にする。
    /// 参照フィールドはこの時点で注入済みだが、参照先スクリプトの OnStart 完了は保証されない。
    /// </summary>
    public override void OnStart()
    {
        if (line is { } l && l.IsValid)
        {
            // 竿先（プレイヤー側）とウキ（別アクタ）を結ぶので、点列はワールド座標で渡す。
            l.LocalSpace = false;
            l.Visible = false;
        }
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
    /// 毎フレームの主更新。竿先の追従 → 釣り姿勢の判定 → 状態ごとの処理、の順に行う。
    ///
    /// ウキの移動をすべてこの Update で終わらせるのが要点。カメラ（CameraMove）は
    /// LateUpdate でウキの子（キャスト時のカメラ目標）を見に来るので、
    /// ウキの位置は Update までに確定させておく必要がある。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 竿先は竿のアニメに追従させる（JointAttach は子へ伝播しないので毎フレーム自前で合わせる）
        SyncRodTip();

        // 釣り姿勢でなければ全部たたんで待機へ戻す（姿勢解除で途中キャンセルされる）
        if (!IsPlayerFishing())
        {
            if (State != FishState.Idle) { CancelToIdle(); }
            return;
        }

        // 姿勢に入った最初のフレーム: 狙い（ジェスチャ待ち）へ
        if (State == FishState.Idle) { EnterAiming(); }

        // ウキの揺れ位相は状態に依らず進めておく（状態遷移で揺れが飛ばないように）
        bobElapsed += ctx.DeltaTime;

        switch (State)
        {
            case FishState.Aiming:
                UpdateAiming(ctx.DeltaTime);
                ParkFloatAtRodTip();      // キャスト前のウキは竿先に格納しておく
                break;

            case FishState.Casting:
                UpdateFlight(ctx.DeltaTime);
                break;

            case FishState.Floating:
            case FishState.Reeling:
                UpdateReeling(ctx.DeltaTime);
                break;

            case FishState.Result:
                // 釣果演出（ヒット機構の実装待ち）。今は何もしない。
                break;
        }
    }

    /// <summary>固定タイムステップの更新。物理など時間刻みを一定にしたい処理向け。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>
    /// Update 後の更新。竿先とウキの位置が確定した後に釣り糸を張り直す。
    /// </summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
        SyncRodTip();
        UpdateLine();
    }

    /// <summary>描画フェーズで呼ばれる。描画に関わる処理向け。</summary>
    public override void Render(ref NativeFrameContext ctx)
    {
    }

    /// <summary>フレーム終了時に呼ばれる。後片付けや状態確定向け。</summary>
    public override void EndFrame(ref NativeFrameContext ctx)
    {
    }

    // ─── 状態遷移 ─────────────────────────────────────────────

    /// <summary>
    /// プレイヤーが釣り姿勢かどうか。参照スクリプトは毎フレーム見に行く
    /// （ホットリロードで実インスタンスが差し替わるため、別フィールドへキャッシュしない）。
    /// </summary>
    private bool IsPlayerFishing()
        => playerMove is { } pm && pm.State == PlayerMove.PlayerState.FishingStance;

    /// <summary>狙い（キャスト待ち）状態へ入る。ジェスチャの累積をすべて初期化する。</summary>
    private void EnterAiming()
    {
        State = FishState.Aiming;
        ResetGesture();
        hookedFish = false;
        ParkFloatAtRodTip();
        CrossFadeBoth(floatClip, playerFloatClip);
        // 狙い中〜巻き取り中はカーソルをロックしたままにする（Casting / Floating / Reeling も同様）。
        // これでマウスが画面端で止まっても MouseDelta が 0 に潰れない。
        ApplyCursorLock(true);
        SEED.Debug.Log("[Fishing] Aiming");
    }

    /// <summary>
    /// 釣りを中断して待機へ戻す（釣り姿勢の解除時に呼ばれる）。
    /// ウキを竿先へ格納し、糸を隠し、竿のアニメ指定は <see cref="PlayerMove"/> 側へ返す。
    /// </summary>
    private void CancelToIdle()
    {
        State = FishState.Idle;
        ResetGesture();
        hookedFish = false;
        ParkFloatAtRodTip();
        HideLine();
        // 釣り状態を抜けたらカーソルを必ず返す（姿勢解除・中断の唯一の出口）。
        ApplyCursorLock(false);
        SEED.Debug.Log("[Fishing] Idle (キャンセル)");
    }

    /// <summary>
    /// カーソルロックの適用。<see cref="lockCursorWhileFishing"/> がオフなら
    /// ロック要求は無視し、解除だけは必ず通す（設定を切り替えた直後に
    /// ロックが張られたまま残らないようにするため）。
    /// </summary>
    private void ApplyCursorLock(bool locked)
    {
        if (locked && !lockCursorWhileFishing) { return; }
        SEED.Input.CursorLocked = locked;
    }

    /// <summary>
    /// ジェスチャの累積・段階・タイムアウトをすべて初期状態へ戻す。
    /// 予備動作スクラブ中であれば、それも打ち切って通常の待ちアニメへ戻す
    /// （タイムアウト・振り戻し・姿勢解除など、キャストに至らなかった全経路がここを通る）。
    /// </summary>
    private void ResetGesture()
    {
        gesturePhase = GesturePhase.Pull;
        pullAccumPx = 0f;
        pushAccumPx = SEED.Vector2.Zero;
        gestureIdleSeconds = 0f;
        EndWindup(continueToCast: false);
    }

    // ─── キャストのジェスチャ判定 ─────────────────────────────

    /// <summary>
    /// マウスの振りジェスチャを解釈し、成立したらキャストする。
    ///
    /// <b>アルゴリズム（2 段階）</b>
    /// 1. Pull（引き）… 下方向（<c>MouseDelta.y &gt; 0</c>）の移動量を累積し、
    ///    <see cref="pullThresholdPx"/> を超えたら Push 段階（振りかぶり完了＝振り抜き待ち）へ。
    ///    上方向へ <see cref="gestureReverseTolerancePx"/> を超えて動いたら引きの累積を
    ///    捨ててやり直す。<see cref="gestureTimeoutSeconds"/> のあいだ有意な動きが無い
    ///    場合も同様にジェスチャ全体をリセットする。
    /// 2. Push（振り抜き待ち／実行）… 上方向（<c>MouseDelta.y &lt; 0</c>）の移動を
    ///    <b>ベクトルのまま</b>累積し、上方向成分が <see cref="pushThresholdPx"/> を
    ///    超えた瞬間にキャストする。
    ///    - まだ振り抜きが始まっていない間（<c>-pushAccumPx.y &lt;= 0</c>）の下方向の
    ///      動きは「引き戻しの一部」として単に無視する（振りかぶりポーズを保ったまま待機）。
    ///      これにより Pull → Push 切り替え直後のオーバーシュートでリセットされない。
    ///    - 振り抜きが始まった後（<c>-pushAccumPx.y &gt; 0</c>）に下方向へ
    ///      <see cref="gestureReverseTolerancePx"/> を超えて戻した場合は「振り直し」として
    ///      <c>pushAccumPx</c> のみを 0 に戻す（Push 段階・振りかぶりポーズは維持し、
    ///      <see cref="ResetGesture"/> は呼ばない＝Pull からやり直させない）。
    ///    - タイムアウトは <see cref="gestureTimeoutSeconds"/> ではなく
    ///      <see cref="armedTimeoutSeconds"/> を使う（0 以下なら無制限に待機）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateAiming(float deltaTime)
    {
        // MouseDelta はウィンドウ内カーソル位置の差分（px、右が +X / 下が +Y）。
        // MouseMove（Raw Input 由来）は埋め込み時に届かないことがあるのでこちらを使う。
        var delta = SEED.Input.MouseDelta;

        // 有意な動きが無いフレームはタイムアウトを進めるだけ
        if (delta.SqrMagnitude < gestureMoveEpsilonPx * gestureMoveEpsilonPx)
        {
            gestureIdleSeconds += deltaTime;

            if (gesturePhase == GesturePhase.Pull)
            {
                // Pull 段階: 引きの途中で放置されたら最初からやり直させる
                if (gestureIdleSeconds > gestureTimeoutSeconds) { ResetGesture(); }
            }
            else
            {
                // Push 段階（振りかぶり待機中）: armedTimeoutSeconds <= 0 なら無制限に待機。
                // 0 より大きい場合のみ、その秒数だけ放置されたらリセットする。
                if (armedTimeoutSeconds > 0f && gestureIdleSeconds > armedTimeoutSeconds) { ResetGesture(); }
            }

            // 動きが無くても予備動作の狙い位置への追従（滑らかな停止）は続ける
            UpdateWindup(deltaTime);
            return;
        }
        gestureIdleSeconds = 0f;

        switch (gesturePhase)
        {
            case GesturePhase.Pull:
                if (delta.y > 0f)
                {
                    // 下へ引いている: 引き量を累積し、しきい値超えで振り抜き受付へ
                    bool wasNotPulling = pullAccumPx <= 0f;
                    pullAccumPx += delta.y;

                    // 引きの累積が 0 から増え始めた最初のフレームで予備動作スクラブを開始する
                    if (wasNotPulling && pullAccumPx > 0f && !windupActive) { BeginWindup(); }

                    if (pullAccumPx >= pullThresholdPx)
                    {
                        gesturePhase = GesturePhase.Push;
                        pushAccumPx = SEED.Vector2.Zero;
                    }
                }
                else if (-delta.y > gestureReverseTolerancePx)
                {
                    // 引く前に上へ大きく動いた: 引きの累積を捨ててやり直す
                    pullAccumPx = 0f;
                }
                break;

            case GesturePhase.Push:
                if (delta.y < 0f)
                {
                    // 上へ振っている: 横成分も含めてベクトルで累積する（方向決めに使う）
                    pushAccumPx += delta;
                    if (-pushAccumPx.y >= pushThresholdPx)
                    {
                        // 画面座標（下が +Y）→ 振り上げ量が正になる形へ直して渡す
                        StartCast(new SEED.Vector2(pushAccumPx.x, -pushAccumPx.y));
                    }
                }
                else
                {
                    // 下方向への動き。振り抜きがまだ始まっていなければ、Pull→Push 切り替え
                    // 直後のオーバーシュートや「引き戻し継続」として単に無視し、
                    // 振りかぶりポーズを保ったまま待機を続ける（リセットしない）。
                    bool swingStarted = -pushAccumPx.y > 0f;
                    if (swingStarted && delta.y > gestureReverseTolerancePx)
                    {
                        // 振り抜きの途中で下へ戻した: 振り抜きだけやり直す
                        // （Push 段階・振りかぶりポーズは維持し、Pull からのやり直しにはしない）
                        pushAccumPx = SEED.Vector2.Zero;
                    }
                }
                break;
        }

        // 予備動作スクラブ中なら、Pull/Push いずれの段階でも狙い位置へ毎フレーム追従させる
        // （Push 段階では pullAccumPx はしきい値で固定されたままなので、狙い位置は
        //  castWindupSeconds に張り付いたまま保持される＝振りかぶりポーズで待機）。
        UpdateWindup(deltaTime);
    }

    /// <summary>
    /// キャストを開始する（ウキを飛ばし始める）。
    ///
    /// <b>飛距離</b>: <c>clamp(|push| × metersPerPixel, minCastDistance, maxCastDistance)</c>
    /// <b>方向</b>  : プレイヤーの正面（水平化）を基準に、振りの傾き
    ///   <c>atan2(push.x, push.y) × directionSensitivity</c> 度（±maxCastAngleDegrees でクランプ）
    ///   だけヨーを回した向き。
    /// <b>着水点</b>: 竿先の XZ ＋ 方向 × 飛距離、Y は水面。
    /// </summary>
    /// <param name="push">振り抜きの累積ベクトル（px。x=右が正 / y=上が正）。</param>
    private void StartCast(SEED.Vector2 push)
    {
        float distance = SEED.Mathf.Clamped(push.Magnitude * metersPerPixel, minCastDistance, maxCastDistance);

        // 基準方向 ＝ プレイヤーの正面を水平化したもの（釣り姿勢では海を向いている）
        var baseDir = new SEED.Vector3(transform.Forward.x, 0f, transform.Forward.z);
        if (baseDir.SqrMagnitude < SqrEpsilon) { return; }   // 真上／真下を向く縮退（起こらない想定の保険）
        float baseYaw = SEED.Mathf.Atan2(baseDir.x, baseDir.z) * SEED.Mathf.Rad2Deg;

        // 振りの傾き（真上へ振れば 0 度、右斜めへ振れば正）を左右のずれ角にする
        float swingAngle = SEED.Mathf.Atan2(push.x, push.y) * SEED.Mathf.Rad2Deg * directionSensitivity;
        float limit = SEED.Mathf.Abs(maxCastAngleDegrees);
        float yaw = baseYaw + SEED.Mathf.Clamped(swingAngle, -limit, limit);

        // ヨー角から水平方向ベクトルへ（エンジン規約: yaw = atan2(x, z)、前方 +Z）
        float yawRad = yaw * SEED.Mathf.Deg2Rad;
        var dir = new SEED.Vector3(SEED.Mathf.Sin(yawRad), 0f, SEED.Mathf.Cos(yawRad));

        flightStart = RodTipPosition();
        flightEnd = new SEED.Vector3(
            flightStart.x + dir.x * distance,
            WaterSurfaceY(),
            flightStart.z + dir.z * distance);

        castDistance = distance;
        flightElapsed = 0f;
        reelAngleOffsetDegrees = 0f;
        reelIdleElapsed = 0f;

        State = FishState.Casting;

        // 予備動作スクラブ中なら、振りかぶりポーズから途切れず本振りへ継続する。
        // スクラブしていなければ（Animator 未設定等）従来どおり通常のクロスフェードで開始する。
        if (windupActive)
        {
            EndWindup(continueToCast: true);
        }
        else
        {
            CrossFadeBoth(castClip, playerCastClip);
        }

        SEED.Debug.Log($"[Fishing] Cast 距離={distance:F1}m 角={yaw - baseYaw:F1}度");
    }

    /// <summary>
    /// 飛翔中のウキを放物線に沿って進める。到達したら <see cref="FishState.Floating"/> へ。
    ///
    /// XZ は直線補間、Y は「直線補間 ＋ <c>4h·t·(1-t)</c>」で山を作る
    /// （t=0,1 で 0、t=0.5 で h になるので、端点は必ず竿先／着水点に一致する）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateFlight(float deltaTime)
    {
        flightElapsed += deltaTime;

        // 飛翔時間が 0 以下に設定されていても止まらないよう、即着水として扱う
        float t = flightSeconds > 0f ? SEED.Mathf.Clamped01(flightElapsed / flightSeconds) : 1f;

        var pos = new SEED.Vector3(
            SEED.Mathf.Lerp(flightStart.x, flightEnd.x, t),
            SEED.Mathf.Lerp(flightStart.y, flightEnd.y, t) + ParabolaApexCoefficient * flightApexHeight * t * (1f - t),
            SEED.Mathf.Lerp(flightStart.z, flightEnd.z, t));
        SetFloatPosition(pos);

        if (t >= 1f)
        {
            State = FishState.Floating;
            CrossFadeBoth(floatClip, playerFloatClip);
            SEED.Debug.Log("[Fishing] Floating");
        }
    }

    /// <summary>
    /// 着水後の待機と巻き取りを処理する（<see cref="FishState.Floating"/> と
    /// <see cref="FishState.Reeling"/> の共通処理）。
    ///
    /// 巻き入力があれば Reeling、無入力が <see cref="reelIdleSeconds"/> 続けば Floating へ戻る。
    /// ウキは常に水面高さ（＋上下揺れ）に保つ。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateReeling(float deltaTime)
    {
        if (uki is not { } floatTf || !floatTf.IsValid) { return; }

        float amount = ReadReelAmount();

        // 巻き入力の有無で Floating ⇔ Reeling を往復する
        if (amount > ReelInputEpsilon)
        {
            reelIdleElapsed = 0f;
            if (State != FishState.Reeling)
            {
                State = FishState.Reeling;
                CrossFadeBoth(reelClip, playerReelClip);
            }
        }
        else
        {
            reelIdleElapsed += deltaTime;
            if (State == FishState.Reeling && reelIdleElapsed > reelIdleSeconds)
            {
                State = FishState.Floating;
                CrossFadeBoth(floatClip, playerFloatClip);
            }
        }

        // 巻く向きを決めて、その向きへ水平に引き寄せる
        var dir = ComputeReelDirection(floatTf.Position, deltaTime);
        var next = floatTf.Position + dir * amount;
        SetFloatPosition(new SEED.Vector3(next.x, WaterSurfaceY() + BobOffset(), next.z));

        // プレイヤーはウキに一番近い経路上の点へ歩いて付いていく（移動の実装は PlayerMove の責務）
        if (State == FishState.Reeling && playerMove is { } pm)
        {
            pm.MoveTowardWorldPoint(floatTf.Position, deltaTime);
        }

        // 手元まで寄ったら 1 回の釣りを終える
        if (HorizontalDistance(floatTf.Position, RodTipPosition()) <= reelEndDistance)
        {
            FinishReeling();
        }
    }

    /// <summary>
    /// 巻き取り完了時の分岐。
    /// 魚が掛かっていれば釣果演出（<see cref="FishState.Result"/>）、
    /// 何も掛かっていなければ再びキャスト待ちへ戻る。
    /// </summary>
    private void FinishReeling()
    {
        // ── ヒット機構の実装待ち: ここが「釣れた」分岐になる ──
        if (hookedFish)
        {
            State = FishState.Result;
            SEED.Debug.Log("[Fishing] Result（釣果演出）");
            return;
        }

        // 空振り: ウキを竿先へ格納し、糸を隠して次のキャストを待つ
        State = FishState.Aiming;
        ResetGesture();
        ParkFloatAtRodTip();
        HideLine();
        CrossFadeBoth(floatClip, playerFloatClip);
        SEED.Debug.Log("[Fishing] Aiming（空振り）");
    }

    /// <summary>
    /// このフレームの巻き取り量（メートル）を読む。
    /// ホイールは<b>絶対量</b>で扱うので、どちら回しでも巻ける。
    /// </summary>
    private float ReadReelAmount()
    {
        float raw = reelByWheel
            ? SEED.Mathf.Abs(SEED.Input.MouseScroll)
            : SEED.Input.MouseDelta.Magnitude;

        return raw * metersPerUnit;
    }

    /// <summary>
    /// 巻く向き（水平・正規化済み）を返す。
    ///
    /// 基準は「ウキ → 島の中心（未設定ならプレイヤー）」の水平方向。
    /// そこから A / D キーで <see cref="reelAngleOffsetDegrees"/> を
    /// ±<see cref="reelAngleRangeDegrees"/>/2 の範囲で振れる。
    /// </summary>
    /// <param name="floatPos">現在のウキ位置（ワールド）。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private SEED.Vector3 ComputeReelDirection(SEED.Vector3 floatPos, float deltaTime)
    {
        // A / D で基準からのずれ角を動かす（範囲外へは出さない）
        float half = SEED.Mathf.Abs(reelAngleRangeDegrees) * 0.5f;
        float turn = 0f;
        if (SEED.Input.GetKey(SEED.KeyCode.A)) { turn -= 1f; }
        if (SEED.Input.GetKey(SEED.KeyCode.D)) { turn += 1f; }
        reelAngleOffsetDegrees = SEED.Mathf.Clamped(
            reelAngleOffsetDegrees + turn * reelTurnSpeedDegPerSec * deltaTime, -half, half);

        // 基準方向（ウキ → 島の中心）。中心が取れない・真上にある場合は動かさない。
        var center = islandCenter is { } c && c.IsValid ? c.Position : transform.Position;
        var toCenter = new SEED.Vector3(center.x - floatPos.x, 0f, center.z - floatPos.z);
        if (toCenter.SqrMagnitude < SqrEpsilon) { return SEED.Vector3.Zero; }

        float yaw = SEED.Mathf.Atan2(toCenter.x, toCenter.z) * SEED.Mathf.Rad2Deg + reelAngleOffsetDegrees;
        float yawRad = yaw * SEED.Mathf.Deg2Rad;
        return new SEED.Vector3(SEED.Mathf.Sin(yawRad), 0f, SEED.Mathf.Cos(yawRad));
    }

    // ─── ウキ・竿先・糸 ───────────────────────────────────────

    /// <summary>
    /// 竿先トランスフォームを竿の姿勢へ追従させる。
    /// <see cref="rodRoot"/> 未設定なら何もしない（<see cref="rodTip"/> の値をそのまま使う）。
    /// </summary>
    private void SyncRodTip()
    {
        if (rodRoot is not { } rod || !rod.IsValid) { return; }
        if (rodTip is not { } tip || !tip.IsValid) { return; }

        tip.Position = ComposeRodTip(rod);
    }

    /// <summary>
    /// 竿のワールド姿勢とローカルオフセットから竿先のワールド位置を合成する。
    /// 方向ベクトルは正規化済みなので、竿のスケールを成分ごとに掛けて実寸へ直す。
    /// </summary>
    /// <param name="rod">竿アクタのトランスフォーム。</param>
    private SEED.Vector3 ComposeRodTip(SEED.Transform rod)
    {
        var scale = rod.Scale;
        return rod.Position
             + rod.Right * (rodTipOffsetX * scale.x)
             + rod.Up * (rodTipOffsetY * scale.y)
             + rod.Forward * (rodTipOffsetZ * scale.z);
    }

    /// <summary>
    /// 糸の始点となる竿先のワールド位置を返す。
    /// 竿アクタ → 竿先トランスフォーム → プレイヤー自身、の順にフォールバックする。
    /// </summary>
    private SEED.Vector3 RodTipPosition()
    {
        if (rodRoot is { } rod && rod.IsValid) { return ComposeRodTip(rod); }
        if (rodTip is { } tip && tip.IsValid) { return tip.Position; }
        return transform.Position;
    }

    /// <summary>
    /// 現在の水面 Y（ワールド）を返す。
    /// <see cref="water"/> 未設定なら「竿先 −<see cref="waterLevelFallbackDrop"/>」を仮の水面とする。
    /// </summary>
    private float WaterSurfaceY()
    {
        if (water is { } w && w.IsValid) { return w.WaterLevel; }
        return RodTipPosition().y - waterLevelFallbackDrop;
    }

    /// <summary>水面に浮くウキの上下揺れのオフセット（メートル）。</summary>
    private float BobOffset()
        => bobAmplitude * SEED.Mathf.Sin(bobElapsed * bobFrequency * TwoPi);

    /// <summary>ウキを指定ワールド位置へ移動する（未設定・無効なら何もしない）。</summary>
    /// <param name="position">移動先のワールド位置。</param>
    private void SetFloatPosition(SEED.Vector3 position)
    {
        if (uki is not { } floatTf || !floatTf.IsValid) { return; }
        floatTf.Position = position;
    }

    /// <summary>
    /// ウキを竿先へ格納する。
    /// アクター／モデルの表示切替 API は存在しないため、
    /// 「竿先に重ねて糸を消す」ことで見た目上しまわれた状態にする。
    /// </summary>
    private void ParkFloatAtRodTip()
    {
        SetFloatPosition(RodTipPosition());
        HideLine();
    }

    /// <summary>釣り糸を非表示にする（点列は残したままフラグだけ落とす）。</summary>
    private void HideLine()
    {
        if (line is { } l && l.IsValid) { l.Visible = false; }
    }

    /// <summary>
    /// 釣り糸の点列を張り直す。
    ///
    /// ウキが外に出ている状態（Casting / Floating / Reeling / Result）のときだけ描く。
    /// Idle / Aiming ではウキが竿先に格納されているので線に意味が無く、非表示にする。
    /// たるみは飛翔中は固定量、着水後は飛距離に比例（上限つき）。
    /// </summary>
    private void UpdateLine()
    {
        if (line is not { } l || !l.IsValid) { return; }

        bool floatIsOut = State is FishState.Casting or FishState.Floating
                                or FishState.Reeling or FishState.Result;
        if (!floatIsOut) { l.Visible = false; return; }

        if (uki is not { } floatTf || !floatTf.IsValid) { l.Visible = false; return; }

        float slack = State == FishState.Casting
            ? flightSlack
            : SEED.Mathf.Clamped(castDistance * slackPerMeter, 0f, maxSlack);

        int segments = SEED.Mathf.Max(lineSegments, MinLineSegments);

        // Catenary は端点を厳密に一致させて返す（糸が竿先／ウキから浮かない）。
        // 分割数が LineRenderer.MaxPoints を超える場合はヘルパ側で丸められる。
        l.SetPoints(SEED.LineHelper.Catenary(RodTipPosition(), floatTf.Position, slack, segments));
        l.Visible = true;
    }

    /// <summary>2 点の水平距離（Y を無視した距離、メートル）。</summary>
    /// <param name="a">点 A（ワールド）。</param>
    /// <param name="b">点 B（ワールド）。</param>
    private static float HorizontalDistance(SEED.Vector3 a, SEED.Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return SEED.Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// 竿の Animator を指定クリップへクロスフェードする（未設定・無効・空名・再生中は何もしない）。
    /// </summary>
    /// <param name="clip">再生するクリップ名。</param>
    private void CrossFadeRod(string clip) => CrossFadeClip(rodAnimator, clip);

    /// <summary>
    /// プレイヤー本体の Animator を指定クリップへクロスフェードする（未設定・無効・空名・再生中は何もしない）。
    /// </summary>
    /// <param name="clip">再生するクリップ名。</param>
    private void CrossFadePlayer(string clip) => CrossFadeClip(playerAnimator, clip);

    /// <summary>
    /// 竿とプレイヤー本体、両方の Animator を対応するクリップへ同時にクロスフェードする。
    ///
    /// 竿モデル（sao.glb）自体にはアニメーションチャンネルが無く、見た目上の動きは
    /// すべて本体（sakanadori.glb）側のクリップが担う。竿は JointAttach で手のボーンへ
    /// 追従するだけなので、状態遷移のたびに竿クリップと本体クリップを必ずペアで切り替える。
    /// 各 Animator は独立に判定する（<see cref="CrossFadeClip"/> 参照）ため、
    /// 片方が未設定・無効でも、もう片方は正しく切り替わる。
    /// </summary>
    /// <param name="rodClip">竿 Animator へ渡すクリップ名。</param>
    /// <param name="playerClip">本体 Animator へ渡すクリップ名。</param>
    private void CrossFadeBoth(string rodClip, string playerClip)
    {
        CrossFadeRod(rodClip);
        CrossFadePlayer(playerClip);
    }

    /// <summary>
    /// 指定 Animator を指定クリップへクロスフェードする共通処理
    /// （未設定・無効・空名・同一クリップ再生中は何もしない）。
    /// </summary>
    /// <param name="animator">対象の Animator（竿・本体いずれか）。</param>
    /// <param name="clip">再生するクリップ名。</param>
    private void CrossFadeClip(SEED.Animator? animator, string clip)
    {
        if (animator is not { } anim || !anim.IsValid) { return; }
        if (string.IsNullOrEmpty(clip)) { return; }

        // 既に同じクリップが再生中なら再指示しない（先頭へ戻ってしまうのを防ぐ）
        if (anim.IsPlaying && anim.CurrentClip == clip) { return; }

        anim.CrossFade(clip, fadeSeconds);
    }

    // ─── キャスト予備動作（振りかぶりポーズでの停止スクラブ）─────

    /// <summary>
    /// キャスト予備動作のスクラブ再生を開始する。
    /// <see cref="castClip"/>（竿）・<see cref="playerCastClip"/>（本体）の両方を
    /// 再生速度 0 でクロスフェード再生させ、以降は <see cref="UpdateWindup"/> が
    /// 手動で両 Animator の <see cref="SEED.Animator.Time"/> を進める。
    ///
    /// 竿・本体は独立に判定する（どちらか一方が未設定・無効でも、
    /// もう一方が始動できていれば <see cref="windupActive"/> は true になる）。
    /// 実際に動くのはほぼ本体側だが、竿 Animator が設定されている場合はそちらも揃えて動かす。
    /// </summary>
    private void BeginWindup()
    {
        bool started = false;

        if (rodAnimator is { } rodAnim && rodAnim.IsValid)
        {
            // 速度 0 で再生開始 = クリップは自動では進まず、Time を手動で操作する下地になる
            rodAnim.Play(castClip, 0f, fadeSeconds);
            started = true;
        }

        if (playerAnimator is { } playerAnim && playerAnim.IsValid)
        {
            playerAnim.Play(playerCastClip, 0f, fadeSeconds);
            started = true;
        }

        windupActive = started;
    }

    /// <summary>
    /// 予備動作スクラブ中の毎フレーム更新。
    /// 引き量（<see cref="pullAccumPx"/> ÷ <see cref="pullThresholdPx"/>）に比例した
    /// 狙い再生位置へ、指数ブレンドで滑らかに追従させる（マウスのブレで竿・本体がガクつかないように）。
    /// 竿・本体の両 Animator へ同じ狙い位置・ブレンド係数を適用する
    /// （<see cref="ScrubWindupTime"/> が個別に null／IsValid を判定する）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateWindup(float deltaTime)
    {
        if (!windupActive) { return; }

        // 引き量の割合（0〜1）ぶんだけ振りかぶりポーズへ近づける。
        // Push 段階では pullAccumPx がしきい値で固定されているので、ここは 1.0 に張り付き、
        // 結果として castWindupSeconds の位置で待機し続ける。
        float ratio = SEED.Mathf.Clamped01(pullAccumPx / pullThresholdPx);
        float targetTime = castWindupSeconds * ratio;
        float blend = ExponentialBlend(windupScrubRate, deltaTime);

        ScrubWindupTime(rodAnimator, targetTime, blend);
        ScrubWindupTime(playerAnimator, targetTime, blend);
    }

    /// <summary>
    /// 指定 Animator の再生位置を、指数ブレンドで狙い位置（<paramref name="targetTime"/>）へ寄せる。
    /// 竿・本体それぞれ独立に呼ばれるため、未設定・無効なら何もしない。
    /// </summary>
    /// <param name="animator">対象の Animator（竿・本体いずれか）。</param>
    /// <param name="targetTime">狙いの再生位置（秒）。</param>
    /// <param name="blend">このフレームで狙い位置へ寄せる割合（0〜1、<see cref="ExponentialBlend"/> の戻り値）。</param>
    private void ScrubWindupTime(SEED.Animator? animator, float targetTime, float blend)
    {
        if (animator is not { } anim || !anim.IsValid) { return; }

        float newTime = anim.Time + (targetTime - anim.Time) * blend;
        anim.Time = SEED.Mathf.Clamped(newTime, 0f, castWindupSeconds);
    }

    /// <summary>
    /// 予備動作スクラブを終了する。竿・本体それぞれの Animator の再生速度を
    /// 必ず等倍へ戻すのはここだけで行う（0 のまま抜けると以降のクリップが固まってしまうため）。
    /// 竿・本体は独立に後始末する（<see cref="FinishWindupFor"/> 参照）。
    /// </summary>
    /// <param name="continueToCast">
    /// true … キャスト成立。振りかぶりポーズから途切れず本振りへ続ける。
    /// false … キャスト不成立（タイムアウト・振り戻し・姿勢解除）。待ちアニメへ戻す。
    /// </param>
    private void EndWindup(bool continueToCast)
    {
        if (!windupActive) { return; }
        windupActive = false;

        FinishWindupFor(rodAnimator, castClip, floatClip, continueToCast);
        FinishWindupFor(playerAnimator, playerCastClip, playerFloatClip, continueToCast);
    }

    /// <summary>
    /// 1 体の Animator について予備動作スクラブの後始末を行う（竿・本体で共通の処理）。
    /// 速度 0 のスクラブ状態を必ず解除したうえで、キャスト成立なら振りかぶり位置から
    /// 途切れず本振りへ継続し（Pause 経由の可能性に備え念のため Resume も呼ぶ）、
    /// 不成立なら待ちクリップへクロスフェードして戻す。
    /// </summary>
    /// <param name="animator">対象の Animator（竿・本体いずれか）。未設定・無効なら何もしない。</param>
    /// <param name="castClipName">この Animator のキャストクリップ名。</param>
    /// <param name="floatClipName">この Animator の待ちクリップ名。</param>
    /// <param name="continueToCast">true ならキャスト続行、false なら待ちアニメへ戻す。</param>
    private void FinishWindupFor(SEED.Animator? animator, string castClipName, string floatClipName, bool continueToCast)
    {
        if (animator is not { } anim || !anim.IsValid) { return; }

        // 速度 0 のスクラブ状態を必ず解除する
        anim.Speed = 1f;

        if (continueToCast)
        {
            if (anim.CurrentClip == castClipName)
            {
                anim.Resume();
            }
            else
            {
                // 想定外: 予備動作中にクリップが崩れていた場合は通常のクロスフェードへフォールバック
                CrossFadeClip(anim, castClipName);
            }
        }
        else
        {
            CrossFadeClip(anim, floatClipName);
        }
    }

    /// <summary>
    /// フレームレート非依存の指数ブレンド係数を返す（0〜1）。
    /// <c>value += (target - value) * ExponentialBlend(rate, dt)</c> の形で使うと、
    /// <paramref name="rate"/> が大きいほど毎フレームの追従が速くなる。
    /// </summary>
    /// <param name="rate">追従率（1/秒）。大きいほど speedy。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private static float ExponentialBlend(float rate, float deltaTime)
        => 1f - SEED.Mathf.Exp(-rate * deltaTime);
}
