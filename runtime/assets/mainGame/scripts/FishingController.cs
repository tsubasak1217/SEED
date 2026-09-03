using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 釣りの「キャスト（投げる）→ 着水 → リール（巻く）」を司るコントローラ。
///
/// <b>プレイヤーアクタに付ける</b>（<see cref="PlayerMove"/> と同じアクタ）。
/// <b>釣り姿勢の出入りは本スクリプトが握る</b>: 左クリックの押下／解放を解釈して
/// <see cref="PlayerMove.EnterFishingStance"/> / <see cref="PlayerMove.ExitFishingStance"/> を呼ぶ
/// （<see cref="PlayerMove"/> 側は姿勢の見た目だけを担当し、入力を一切見ない）。
///
/// <b>操作（左クリック押しっぱなしで 1 回のキャストが完結する）</b>
/// - 構える   … 移動中に<b>左クリックを押す</b>と釣り姿勢＋狙い（Aiming）へ
/// - 振りかぶり… 押したままマウスを<b>左へ振る</b>（累積が
///   <see cref="windupThresholdPx"/> px を超えると Windup へ。途中まででも姿勢は追従する）
/// - 飛距離   … Windup 中は着弾点プレビューが最短⇔最長を往復するので、投げたい距離で振る
/// - キャスト … マウスを<b>右へ振る</b>（累積が <see cref="castSwingThresholdPx"/> px 超で成立）
/// - 方向     … A / D キーでキャスト角を左右に振る（±<see cref="maxCastAngleDegrees"/> 度）
/// - 中断     … キャスト前に左クリックを離すと姿勢を解除して移動へ戻る
/// - リール   … マウスホイール回転量のみで巻き取る
///   （<see cref="metersPerWheelUnit"/> を 0 にすれば無効化できる）
/// - 巻く向き … A / D キーで左右に振れる（<b>ウキ→竿先</b>方向を基準に ±範囲内。<see cref="islandCenter"/> は竿先が未設定のときのみのフォールバック）
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
    /// <b>遷移表</b>
    /// <code>
    /// Idle    --左クリック押下（移動中）--> Aiming
    /// Aiming  --左へ振る（累積 >= windupThresholdPx）--> Windup
    /// Aiming  --左クリック解放--> Idle（姿勢解除して移動へ）
    /// Windup  --右へ振る（累積 >= castSwingThresholdPx）--> Casting
    /// Windup  --左クリック解放--> Idle（振りかぶりを取り消して移動へ）
    /// Casting --着水--> Floating --巻き入力--> Reeling --手元まで--> Aiming（空振り）/ Result
    /// Aiming（巻き取り後）--左クリックを離していれば即--> Idle
    /// </code>
    ///
    /// スクリプトはファイル名＝型名で 1 ファイル 1 スクリプトクラスとして扱われるため、
    /// この列挙型は独立ファイルにせず本クラスの入れ子として定義する
    /// （外部からは <c>FishingController.FishState</c> で参照できる）。
    /// </summary>
    public enum FishState
    {
        /// <summary>釣り姿勢に入っていない。ウキは竿先に格納し、糸は非表示。</summary>
        Idle,

        /// <summary>
        /// 釣り姿勢中でキャスト待ち。左クリックを押したまま「左へ振る」のを待ち受ける。
        /// 左への累積がしきい値未満のあいだは、その割合ぶんだけ振りかぶり姿勢を追従表示する。
        /// </summary>
        Aiming,

        /// <summary>
        /// 振りかぶり完了。着弾点プレビューが最短⇔最長を往復し、
        /// 「右へ振る」ジェスチャでキャストが成立する。
        /// </summary>
        Windup,

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

    /// <summary>0 除算を避けるための「実質 0」しきい値（しきい値・周期などの分母に使う）。</summary>
    private const float DivideEpsilon = 1e-4f;

    /// <summary>着弾点プレビューの円（リング）の最小分割数（3 ＝ 三角形）。</summary>
    private const int MinRingSegments = 3;

    /// <summary>着弾点プレビューの放物線の最小分割数（1 ＝ 直線）。</summary>
    private const int MinArcSegments = 1;

    /// <summary>ピンポン往復 1 周（往路＋復路）の長さ。<c>PingPong(u, 1)</c> は u が 2 で 1 周する。</summary>
    private const float PingPongCycleUnits = 2f;

    /// <summary>
    /// 着水点マーカーの脈動の振幅（基準スケールに対する割合）。
    /// 0.2 なら 0.8 倍〜1.2 倍のあいだで拡縮する（サイン波が -1〜+1 を取るため）。
    /// </summary>
    private const float MarkerPulseAmplitude = 0.2f;

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
    /// 着弾点プレビューの LineRenderer（専用アクタ「CastPreview」に付ける想定）。
    /// <see cref="FishState.Windup"/> のあいだだけ「竿先→着弾点の放物線＋着弾点の円」を描く。
    /// 釣り糸とは寿命も見た目も別物なので、糸（<see cref="line"/>）とは別スロットにする。
    /// 未設定ならプレビューを描かない（操作自体は同じように成立する）。
    /// </summary>
    [SerializeField(Label = "着弾点プレビュー(LineRenderer)")]
    private SEED.LineRenderer? previewLine = null;

    /// <summary>
    /// 着水点マーカーのトランスフォーム（球モデルを持つ専用アクタ「CastMarker」に付ける想定）。
    ///
    /// <see cref="FishState.Windup"/> のあいだだけ着水点へ置き、それ以外では
    /// <see cref="markerParkY"/> の高さ（水面のはるか下）へ格納して見えなくする。
    /// <b>モデル／アクタの表示切替 API は存在しない</b>ため、ウキ（<see cref="ParkFloatAtRodTip"/>）と
    /// 同じく「画面外へ動かす」ことで非表示を表現している。
    /// 未設定ならマーカーは使わない（プレビュー線だけの従来動作になる）。
    /// </summary>
    [SerializeField(Label = "着水点マーカー")]
    private SEED.Transform? castMarker = null;

    /// <summary>
    /// マーカーを隠すときに置く Y 座標（ワールド）。水面よりも十分下に取る。
    /// 表示 API が無いため、この高さへ退避させることで「非表示」を表現する。
    /// </summary>
    [SerializeField(Label = "マーカーの格納位置Y")]
    private float markerParkY = -100f;

    /// <summary>マーカーを水面からどれだけ浮かせて置くか（メートル）。</summary>
    [SerializeField(Label = "マーカーの水面からの高さ")]
    private float markerHoverHeight = 0.1f;

    /// <summary>マーカーの脈動（拡縮）の周波数（Hz）。0 にすると実質止まる。</summary>
    [SerializeField(Label = "マーカーの脈動周期(Hz)")]
    private float markerPulseFrequency = 2f;

    /// <summary>
    /// 水面（WaterVolume）。着水点の Y とウキの浮かぶ高さに使う。
    /// 未設定なら「竿先の Y − <see cref="waterLevelFallbackDrop"/>」を水面とみなす。
    /// </summary>
    [SerializeField(Label = "水面(WaterVolume)")]
    private SEED.WaterVolume? water = null;

    /// <summary>
    /// 島の中心（巻き取り方向の基準の<b>フォールバック</b>）。
    ///
    /// 通常は「ウキ→竿先」の方向を巻き取りの基準にするため、この値は使われない。
    /// <see cref="rodRoot"/> と <see cref="rodTip"/> がどちらも未設定で
    /// 竿先の位置が実質プレイヤー自身に潰れてしまう場合にだけ、代わりの基準として使う
    /// （それも未設定ならプレイヤー自身の位置を中心として使う）。
    /// </summary>
    [SerializeField(Label = "島の中心（巻く方向の代替基準・竿先未設定時のみ使用）")]
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
    /// 振りかぶり成立に必要な「左方向」への累積マウス移動量（px）。
    /// <see cref="FishState.Aiming"/> で左へ振った量がこれを超えると
    /// <see cref="FishState.Windup"/>（振りかぶり完了）へ入る。
    /// 途中までの量は振りかぶり姿勢のスクラブ比率としてそのまま使う。
    /// </summary>
    [Header("キャストのジェスチャ"), SerializeField(Label = "振りかぶりのしきい値(px)")]
    private float windupThresholdPx = 40f;

    /// <summary>
    /// キャスト成立に必要な「右方向」への累積マウス移動量（px）。
    /// <see cref="FishState.Windup"/> で右へ振った量がこれを超えた瞬間にキャストする。
    /// </summary>
    [SerializeField(Label = "振り抜きのしきい値(px)")]
    private float castSwingThresholdPx = 40f;

    // ─── キャストの飛距離・方向 ───────────────────────────────

    /// <summary>飛距離の下限（メートル）。プレビューの往復の下端でもある。</summary>
    [Header("キャスト"), SerializeField(Label = "最短飛距離(m)")]
    private float minCastDistance = 3f;

    /// <summary>飛距離の上限（メートル）。プレビューの往復の上端でもある。</summary>
    [SerializeField(Label = "最長飛距離(m)")]
    private float maxCastDistance = 25f;

    /// <summary>
    /// 着弾点プレビューが最短⇔最長を 1 往復する秒数（往路＋復路で 1 周）。
    /// 短いほど狙いがシビアになる。
    /// </summary>
    [SerializeField(Label = "プレビューの往復周期(秒)")]
    private float previewCycleSeconds = 2.0f;

    /// <summary>A / D キーでキャスト方向を振る速さ（度／秒）。</summary>
    [SerializeField(Label = "キャスト方向転換の速さ(度/秒)")]
    private float castTurnSpeedDegPerSec = 60f;

    /// <summary>正面（プレイヤーの向き）からの左右の最大ずれ角（度）。</summary>
    [SerializeField(Label = "最大キャスト角(度)")]
    private float maxCastAngleDegrees = 45f;

    /// <summary>着弾点プレビューの放物線の分割数（点数は分割数＋1）。</summary>
    [SerializeField(Label = "プレビューの弧の分割数")]
    private int previewArcSegments = 24;

    /// <summary>着弾点プレビューの円（着弾点マーカー）の半径（メートル）。</summary>
    [SerializeField(Label = "プレビューの円の半径(m)")]
    private float previewRingRadius = 0.3f;

    /// <summary>着弾点プレビューの円の分割数（点数は分割数＋1＝始点を閉じるぶん）。</summary>
    [SerializeField(Label = "プレビューの円の分割数")]
    private int previewRingSegments = 16;

    /// <summary>
    /// 着弾点プレビューの線（放物線＋円）を描くか。
    ///
    /// 既定は false。細い線は水面上でほとんど視認できないため、着水点の提示は
    /// 3D の球モデル（<see cref="castMarker"/>）を主役にしている。
    /// 線も併用したい場合だけ true にする。
    /// </summary>
    [SerializeField(Label = "プレビュー線を表示")]
    private bool showPreviewLine = false;

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
    /// マウスホイール 1 目盛（<see cref="SEED.Input.MouseScroll"/> の絶対量 1 単位）あたりの巻き取り距離（メートル）。
    /// 0 にするとホイール入力を無効化できる。
    ///
    /// リールの巻き取り入力はホイールのみ（回転方向は問わず絶対量で扱う）。
    /// </summary>
    [Header("リール"), SerializeField(Label = "ホイール1目盛あたりの巻き距離(m)")]
    private float metersPerWheelUnit = 0.5f;

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

    /// <summary>
    /// キャスト予備動作（振りかぶりポーズでの停止スクラブ）を実行中か。
    /// true のあいだ竿 Animator の再生速度は 0 に固定し、<see cref="Time"/> を手動で狙い位置へ寄せる。
    /// </summary>
    private bool windupActive = false;

    /// <summary>左方向へ動いた累積量（px、正値）。<see cref="FishState.Windup"/> 到達で頭打ちにする。</summary>
    private float windupAccumPx = 0f;

    /// <summary>右方向へ動いた累積量（px、正値）。<see cref="FishState.Windup"/> でのみ積算する。</summary>
    private float swingAccumPx = 0f;

    /// <summary>着弾点プレビューの往復位相用に積算した秒数（<see cref="FishState.Windup"/> 中のみ進む）。</summary>
    private float previewElapsed = 0f;

    /// <summary>キャスト方向の基準（プレイヤー正面）からのずれ角（度）。A / D キーで増減する。</summary>
    private float castAngleOffsetDegrees = 0f;

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
    /// 着水点マーカーの基準スケール（脈動していない状態の大きさ）。
    /// 初回表示時のスケールを 1 度だけ控え、以降の脈動と格納時の復元はこの値を基準にする
    /// （毎フレーム読み直すと、脈動後のスケールを基準にしてしまい発散するため）。
    /// </summary>
    private SEED.Vector3 markerBaseScale = SEED.Vector3.One;

    /// <summary><see cref="markerBaseScale"/> を控え済みか。</summary>
    private bool markerBaseScaleCaptured = false;

    /// <summary>マーカーの脈動の位相用に積算した秒数（表示中のみ進み、格納時に 0 へ戻す）。</summary>
    private float markerPulseElapsed = 0f;

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

        if (previewLine is { } preview && preview.IsValid)
        {
            // プレビューも竿先〜水面のワールド座標を直接渡す（アクタの姿勢に依存させない）。
            preview.LocalSpace = false;
            preview.Visible = false;
        }

        // 着水点マーカーは開始時点で必ず格納位置へ落としておく
        // （シーン上の初期位置に置き忘れても、実行開始と同時に隠れる）。
        ParkCastMarker();
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
    /// 毎フレームの主更新。竿先の追従 → 釣り開始／中断の判定 → 状態ごとの処理、の順に行う。
    ///
    /// ウキの移動をすべてこの Update で終わらせるのが要点。カメラ（CameraMove）は
    /// LateUpdate でウキの子（キャスト時のカメラ目標）を見に来るので、
    /// ウキの位置は Update までに確定させておく必要がある。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 竿先は竿のアニメに追従させる（JointAttach は子へ伝播しないので毎フレーム自前で合わせる）
        SyncRodTip();

        // プレイヤー参照が無ければ姿勢の出入りができないので何もしない
        if (playerMove is not { } pm) { return; }

        // 何らかの理由で外部から釣り姿勢を解除された場合は全部たたんで待機へ戻す
        if (State != FishState.Idle && !IsPlayerFishing())
        {
            CancelToIdle();
            return;
        }

        // 待機中: 移動中に左クリックを押したら釣り姿勢＋狙いへ入る
        // （経路移動モードでないと EnterFishingStance が false を返し、釣りは始まらない）
        if (State == FishState.Idle)
        {
            if (!SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left)) { return; }
            if (!pm.EnterFishingStance()) { return; }
            EnterAiming();
        }

        // ウキの揺れ位相は状態に依らず進めておく（状態遷移で揺れが飛ばないように）
        bobElapsed += ctx.DeltaTime;

        switch (State)
        {
            case FishState.Aiming:
                UpdateAiming(ctx.DeltaTime);
                ParkFloatAtRodTip();      // キャスト前のウキは竿先に格納しておく
                break;

            case FishState.Windup:
                UpdateWindupState(ctx.DeltaTime);
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
        HidePreviewLine();
        CrossFadeBoth(floatClip, playerFloatClip);
        // 狙い中〜巻き取り中はカーソルをロックしたままにする（Casting / Floating / Reeling も同様）。
        // これでマウスが画面端で止まっても MouseDelta が 0 に潰れない。
        ApplyCursorLock(true);
        SEED.Debug.Log("[Fishing] Aiming");
    }

    /// <summary>
    /// 釣りを中断して待機へ戻す（外部から釣り姿勢を解除された場合の後始末）。
    /// ウキを竿先へ格納し、糸とプレビューを隠し、竿のアニメ指定は <see cref="PlayerMove"/> 側へ返す。
    /// </summary>
    private void CancelToIdle()
    {
        State = FishState.Idle;
        ResetGesture();
        hookedFish = false;
        ParkFloatAtRodTip();
        HideLine();
        HidePreviewLine();
        // 釣り状態を抜けたらカーソルを必ず返す（姿勢解除・中断の唯一の出口）。
        ApplyCursorLock(false);
        SEED.Debug.Log("[Fishing] Idle (キャンセル)");
    }

    /// <summary>
    /// 左クリックを離してキャスト前に釣りをやめ、移動できる状態へ戻す。
    /// <see cref="CancelToIdle"/> の後始末に加えて、
    /// <see cref="PlayerMove.ExitFishingStance"/> を呼んで姿勢自体も解除する
    /// （＝入力起因の出口はこの 1 本だけにまとめる）。
    /// </summary>
    private void ExitToMovement()
    {
        CancelToIdle();
        if (playerMove is { } pm) { pm.ExitFishingStance(); }
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
        windupAccumPx = 0f;
        swingAccumPx = 0f;
        previewElapsed = 0f;
        castAngleOffsetDegrees = 0f;
        EndWindup(continueToCast: false);
    }

    // ─── キャストのジェスチャ判定 ─────────────────────────────

    /// <summary>
    /// <see cref="FishState.Aiming"/> の毎フレーム更新。
    ///
    /// <b>アルゴリズム</b>
    /// - 左クリックを離した … キャスト前なので釣りをやめて移動へ戻る（<see cref="ExitToMovement"/>）。
    /// - マウスが左（<c>MouseDelta.x &lt; 0</c>）へ動いた … その量を
    ///   <see cref="windupAccumPx"/> へ積算し、振りかぶり姿勢のスクラブを開始／進行させる。
    ///   累積が <see cref="windupThresholdPx"/> を超えたら <see cref="FishState.Windup"/> へ。
    /// - 右へ動いた量は無視する（振りかぶる前の振り抜きは受け付けない）。
    ///
    /// A / D によるキャスト方向の調整は <see cref="FishState.Windup"/> と同じく毎フレーム効く。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateAiming(float deltaTime)
    {
        // 方向合わせはキャスト前ならいつでも受け付ける（プレビューが出る前から狙いを作れる）
        UpdateCastAngle(deltaTime);

        // キャスト前に左クリックを離したら中断（＝この状態の唯一の終了条件）。
        // 巻き取り完了で Aiming へ戻ってきたときも、押していなければここで即座に移動へ戻る。
        if (!SEED.Input.GetMouseButton(SEED.MouseButton.Left))
        {
            ExitToMovement();
            return;
        }

        // MouseDelta はウィンドウ内カーソル位置の差分（px、右が +X / 下が +Y）。
        // MouseMove（Raw Input 由来）は埋め込み時に届かないことがあるのでこちらを使う。
        float deltaX = SEED.Input.MouseDelta.x;
        if (deltaX < 0f)
        {
            // 左へ振っている: 振りかぶり量を積算する（最初の 1 フレームでスクラブを開始）
            bool wasNotWindingUp = windupAccumPx <= 0f;
            windupAccumPx += -deltaX;
            if (wasNotWindingUp && !windupActive) { BeginWindup(); }

            if (windupAccumPx >= WindupThreshold()) { EnterWindup(); }
        }

        // 振りかぶり姿勢を累積量の割合へ追従させる（動きが無いフレームも滑らかに止める）
        UpdateWindup(deltaTime);
    }

    /// <summary>
    /// 振りかぶり完了（<see cref="FishState.Windup"/>）へ入る。
    /// 累積をしきい値で頭打ちにして振りかぶり姿勢を保持し、
    /// 振り抜きの累積とプレビューの位相を新たに開始する。
    /// </summary>
    private void EnterWindup()
    {
        State = FishState.Windup;

        // 以降 UpdateWindup のスクラブ比率が 1.0 に張り付く＝振りかぶりポーズで待機する
        windupAccumPx = WindupThreshold();
        swingAccumPx = 0f;
        previewElapsed = 0f;

        SEED.Debug.Log("[Fishing] Windup");
    }

    /// <summary>
    /// <see cref="FishState.Windup"/> の毎フレーム更新。
    ///
    /// - 左クリックを離した … 振りかぶりを取り消して移動へ戻る。
    /// - マウスが右（<c>MouseDelta.x &gt; 0</c>）へ動いた … その量を
    ///   <see cref="swingAccumPx"/> へ積算し、<see cref="castSwingThresholdPx"/> を
    ///   超えた瞬間に「そのときプレビューが示していた距離・方向」でキャストする。
    /// - 左へ動いた量は無視する（振りかぶりは既に完了しているため）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateWindupState(float deltaTime)
    {
        UpdateCastAngle(deltaTime);

        // 振り抜く前に離したらキャンセル（振りかぶりを解いて移動へ戻る）
        if (!SEED.Input.GetMouseButton(SEED.MouseButton.Left))
        {
            ExitToMovement();
            return;
        }

        // 飛距離プレビューの往復位相を進める
        previewElapsed += deltaTime;

        float deltaX = SEED.Input.MouseDelta.x;
        if (deltaX > 0f)
        {
            swingAccumPx += deltaX;
            if (swingAccumPx >= castSwingThresholdPx)
            {
                // 「見えている着弾点」をそのまま投げる（プレビューと結果を一致させる）
                StartCast(PreviewDistance(), CastYawDegrees());
                return;
            }
        }

        // 振りかぶりポーズを保持しつつ、着弾点プレビュー（マーカー＋線）を更新する
        UpdateWindup(deltaTime);
        UpdateCastPreview(deltaTime);
    }

    /// <summary>
    /// A / D キーでキャスト方向のずれ角（<see cref="castAngleOffsetDegrees"/>）を動かす。
    /// 範囲は正面から ±<see cref="maxCastAngleDegrees"/> 度。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateCastAngle(float deltaTime)
    {
        float turn = 0f;
        if (SEED.Input.GetKey(SEED.KeyCode.A)) { turn -= 1f; }
        if (SEED.Input.GetKey(SEED.KeyCode.D)) { turn += 1f; }
        if (turn == 0f) { return; }

        float limit = SEED.Mathf.Abs(maxCastAngleDegrees);
        castAngleOffsetDegrees = SEED.Mathf.Clamped(
            castAngleOffsetDegrees + turn * castTurnSpeedDegPerSec * deltaTime, -limit, limit);
    }

    /// <summary>
    /// 振りかぶりのしきい値（px）。0 除算と「0px で即成立」を避けるため下限を設ける。
    /// </summary>
    private float WindupThreshold()
        => SEED.Mathf.Max(windupThresholdPx, DivideEpsilon);

    /// <summary>
    /// いまキャストしたときの飛距離（メートル）。
    /// <see cref="previewCycleSeconds"/> を 1 周期として
    /// <see cref="minCastDistance"/>⇔<see cref="maxCastDistance"/> をピンポン往復する。
    /// </summary>
    private float PreviewDistance()
    {
        float period = SEED.Mathf.Max(previewCycleSeconds, DivideEpsilon);

        // PingPong(u, 1) は u が 2 進むと 1 往復するので、1 周期ぶんを 2 単位に伸ばす
        float ratio = SEED.Mathf.PingPong(previewElapsed / period * PingPongCycleUnits, 1f);
        return SEED.Mathf.Lerp(minCastDistance, maxCastDistance, ratio);
    }

    /// <summary>
    /// いまキャストする方向のヨー角（度）。
    /// プレイヤーの正面（水平化）を基準に <see cref="castAngleOffsetDegrees"/> だけ回した向き。
    /// 正面が真上／真下に潰れている縮退時は null（起こらない想定の保険）。
    /// </summary>
    private float? CastYawDegrees()
    {
        var baseDir = new SEED.Vector3(transform.Forward.x, 0f, transform.Forward.z);
        if (baseDir.SqrMagnitude < SqrEpsilon) { return null; }

        // エンジン規約: yaw = atan2(x, z)、前方 +Z
        float baseYaw = SEED.Mathf.Atan2(baseDir.x, baseDir.z) * SEED.Mathf.Rad2Deg;
        float limit = SEED.Mathf.Abs(maxCastAngleDegrees);
        return baseYaw + SEED.Mathf.Clamped(castAngleOffsetDegrees, -limit, limit);
    }

    /// <summary>ヨー角（度）から水平方向の単位ベクトルを作る（エンジン規約: 前方 +Z）。</summary>
    /// <param name="yawDegrees">ヨー角（度）。</param>
    private static SEED.Vector3 YawToDirection(float yawDegrees)
    {
        float yawRad = yawDegrees * SEED.Mathf.Deg2Rad;
        return new SEED.Vector3(SEED.Mathf.Sin(yawRad), 0f, SEED.Mathf.Cos(yawRad));
    }

    /// <summary>
    /// 指定の飛距離・ヨー角の着水点（ワールド）を返す。
    /// XZ は竿先＋方向×距離、Y は水面。プレビューと本番のキャストで共通に使う。
    /// </summary>
    /// <param name="distance">飛距離（メートル）。</param>
    /// <param name="yawDegrees">キャスト方向のヨー角（度）。</param>
    private SEED.Vector3 LandingPoint(float distance, float yawDegrees)
    {
        var dir = YawToDirection(yawDegrees);
        var tip = RodTipPosition();
        return new SEED.Vector3(tip.x + dir.x * distance, WaterSurfaceY(), tip.z + dir.z * distance);
    }

    /// <summary>
    /// キャストを開始する（ウキを飛ばし始める）。
    ///
    /// 飛距離・方向は <see cref="FishState.Windup"/> のプレビューが示していた値をそのまま受け取る
    /// （＝見えていた着弾点に必ず落ちる）。方向が縮退している場合は何もしない。
    /// </summary>
    /// <param name="distance">飛距離（メートル）。<see cref="minCastDistance"/>〜<see cref="maxCastDistance"/> にクランプする。</param>
    /// <param name="yawDegrees">キャスト方向のヨー角（度）。null なら縮退のためキャストしない。</param>
    private void StartCast(float distance, float? yawDegrees)
    {
        if (yawDegrees is not { } yaw) { return; }   // 真上／真下を向く縮退（起こらない想定の保険）

        float clamped = SEED.Mathf.Clamped(distance, minCastDistance, maxCastDistance);

        flightStart = RodTipPosition();
        flightEnd = LandingPoint(clamped, yaw);

        castDistance = clamped;
        flightElapsed = 0f;
        reelAngleOffsetDegrees = 0f;
        reelIdleElapsed = 0f;

        State = FishState.Casting;
        HidePreviewLine();

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

        SEED.Debug.Log($"[Fishing] Cast 距離={clamped:F1}m 角={castAngleOffsetDegrees:F1}度");
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

        SetFloatPosition(ArcPoint(flightStart, flightEnd, t));

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
    ///
    /// <b>巻き取りの終了判定（元の不具合修正）</b>
    /// 以前は巻く向きの基準が「ウキ→島の中心」だったため、ウキが竿先の脇や後方を通り抜けても
    /// 「竿先との水平距離」がたまたま縮まらず、巻き切っても回収されない不具合があった。
    /// 基準を「ウキ→竿先」へ変更したうえで、
    /// (1) 毎フレームの移動量を「竿先までの残り水平距離」でクランプして行き過ぎを防ぎ、
    /// (2) 残り距離が完了距離以下、または進行方向が竿先への方向から 90 度以上外れた
    ///     （＝内積が 0 以下＝もう竿先へ近づけない）場合は即座に巻き取りを完了させる。
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

        // 巻き取りの基準点（通常は竿先。竿先が実質未設定なら島の中心へフォールバック）
        var target = ReelTargetPosition();

        // ウキ→基準点の水平ベクトルと、その長さ（＝残りの巻き取り距離）
        var toTarget = new SEED.Vector3(target.x - floatTf.Position.x, 0f, target.z - floatTf.Position.z);
        float remaining = SEED.Mathf.Sqrt(toTarget.x * toTarget.x + toTarget.z * toTarget.z);

        // 安全策: Reeling に入った時点（または既に）残り距離が完了距離以下なら、
        // 巻く前から手元にあるということなので即座に完了させる（ウキが素通りするのを防ぐ）。
        if (remaining <= reelEndDistance)
        {
            FinishReeling();
            return;
        }

        // 巻く向き（A / D による左右のずれを含む、基準方向からの水平単位ベクトル）
        var dir = ComputeReelDirection(toTarget, deltaTime);

        // 進行方向が基準点への方向から 90 度以上外れている（内積 <= 0）＝
        // これ以上巻いても基準点へ近づけない向きなので、素通りする前に巻き取りを完了させる。
        float approach = toTarget.x * dir.x + toTarget.z * dir.z;
        if (approach <= 0f)
        {
            FinishReeling();
            return;
        }

        // このフレームの移動量を「残りの水平距離」でクランプし、基準点を追い越さないようにする
        float step = SEED.Mathf.Min(amount, remaining);
        var next = floatTf.Position + dir * step;
        SetFloatPosition(new SEED.Vector3(next.x, WaterSurfaceY() + BobOffset(), next.z));

        // プレイヤーはウキに一番近い経路上の点へ歩いて付いていく（移動の実装は PlayerMove の責務）
        if (State == FishState.Reeling && playerMove is { } pm)
        {
            pm.MoveTowardWorldPoint(floatTf.Position, deltaTime);
        }

        // 移動後の残り距離が完了距離以下になったら 1 回の釣りを終える
        if (HorizontalDistance(next, target) <= reelEndDistance)
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
    ///
    /// マウスホイールの回転量のみを入力源とする（絶対量で扱うため、どちら回しでも巻ける）。
    /// 係数を 0 にすればホイール入力を無効化できる（<see cref="metersPerWheelUnit"/>）。
    /// </summary>
    private float ReadReelAmount()
    {
        return SEED.Mathf.Abs(SEED.Input.MouseScroll) * metersPerWheelUnit;
    }

    /// <summary>
    /// 巻き取りの基準点（ワールド）を返す。
    ///
    /// 通常は竿先（<see cref="RodTipPosition"/> と同じ優先順位: 竿アクタ追従 → 竿先トランスフォーム）。
    /// <see cref="rodRoot"/> と <see cref="rodTip"/> のどちらも未設定で竿先が定義できない場合だけ、
    /// <see cref="islandCenter"/>（未設定ならプレイヤー自身の位置）へフォールバックする。
    /// </summary>
    private SEED.Vector3 ReelTargetPosition()
    {
        if (rodRoot is { } rod && rod.IsValid) { return ComposeRodTip(rod); }
        if (rodTip is { } tip && tip.IsValid) { return tip.Position; }
        if (islandCenter is { } c && c.IsValid) { return c.Position; }
        return transform.Position;
    }

    /// <summary>
    /// 巻く向き（水平・正規化済み）を返す。
    ///
    /// 基準は「ウキ → 巻き取りの基準点（<see cref="ReelTargetPosition"/>、通常は竿先）」の水平方向。
    /// そこから A / D キーで <see cref="reelAngleOffsetDegrees"/> を
    /// ±<see cref="reelAngleRangeDegrees"/>/2 の範囲で振れる。
    /// </summary>
    /// <param name="toTarget">ウキ → 基準点の水平ベクトル（Y 成分は無視する。呼び出し側で算出済み）。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private SEED.Vector3 ComputeReelDirection(SEED.Vector3 toTarget, float deltaTime)
    {
        // A / D で基準からのずれ角を動かす（範囲外へは出さない）
        float half = SEED.Mathf.Abs(reelAngleRangeDegrees) * 0.5f;
        float turn = 0f;
        if (SEED.Input.GetKey(SEED.KeyCode.A)) { turn -= 1f; }
        if (SEED.Input.GetKey(SEED.KeyCode.D)) { turn += 1f; }
        reelAngleOffsetDegrees = SEED.Mathf.Clamped(
            reelAngleOffsetDegrees + turn * reelTurnSpeedDegPerSec * deltaTime, -half, half);

        // 基準方向（ウキ → 基準点）。基準点がウキの真上にある等の縮退時は動かさない。
        if (toTarget.SqrMagnitude < SqrEpsilon) { return SEED.Vector3.Zero; }

        float yaw = SEED.Mathf.Atan2(toTarget.x, toTarget.z) * SEED.Mathf.Rad2Deg + reelAngleOffsetDegrees;
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
    /// 着弾点プレビューを隠す（線はフラグを落とし、マーカーは格納位置へ退避させる）。
    /// Windup 以外の全経路（狙い開始・キャンセル・キャスト開始）から呼ばれる唯一の出口。
    /// </summary>
    private void HidePreviewLine()
    {
        if (previewLine is { } preview && preview.IsValid) { preview.Visible = false; }
        ParkCastMarker();
    }

    /// <summary>
    /// 着水点マーカーを格納位置（<see cref="markerParkY"/>）へ退避させ、脈動を初期化する。
    ///
    /// アクタ／モデルの表示切替 API が無いため、これが「非表示」の実装である。
    /// XZ はその場に残し、Y だけを水面のはるか下へ落とす。
    /// スケールは控えておいた基準値へ戻す（脈動途中の大きさで固まらないように）。
    /// </summary>
    private void ParkCastMarker()
    {
        markerPulseElapsed = 0f;

        if (castMarker is not { } marker || !marker.IsValid) { return; }

        var p = marker.Position;
        marker.Position = new SEED.Vector3(p.x, markerParkY, p.z);
        if (markerBaseScaleCaptured) { marker.Scale = markerBaseScale; }
    }

    /// <summary>
    /// 着水点マーカーを着水点へ置き、脈動（拡縮）させる。
    ///
    /// 基準スケールは初回表示時に 1 度だけ控える（<see cref="markerBaseScale"/>）。
    /// 以降は <c>基準 ×(1 + 振幅·sin(2π·f·t))</c> で
    /// <c>1 - MarkerPulseAmplitude</c> 倍〜<c>1 + MarkerPulseAmplitude</c> 倍のあいだを往復する。
    /// </summary>
    /// <param name="landing">着水点（ワールド）。Y は水面高さ。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateCastMarker(SEED.Vector3 landing, float deltaTime)
    {
        if (castMarker is not { } marker || !marker.IsValid) { return; }

        // 初回表示時のスケールを基準として控える（以降は基準からの倍率で動かす）
        if (!markerBaseScaleCaptured)
        {
            markerBaseScale = marker.Scale;
            markerBaseScaleCaptured = true;
        }

        marker.Position = new SEED.Vector3(landing.x, landing.y + markerHoverHeight, landing.z);

        markerPulseElapsed += deltaTime;
        float pulse = 1f + MarkerPulseAmplitude
                         * SEED.Mathf.Sin(markerPulseElapsed * markerPulseFrequency * TwoPi);
        marker.Scale = new SEED.Vector3(
            markerBaseScale.x * pulse,
            markerBaseScale.y * pulse,
            markerBaseScale.z * pulse);
    }

    // ─── 着弾点プレビュー ─────────────────────────────────────

    /// <summary>
    /// 放物線（キャストの軌道）上の 1 点を返す。
    /// XZ は直線補間、Y は「直線補間 ＋ <c>4h·t·(1-t)</c>」で山を作る
    /// （t=0,1 で 0、t=0.5 で h になるので端点は必ず始点／終点に一致する）。
    /// 飛翔中のウキとプレビューの弧で<b>同じ式</b>を使い、見た目と結果を一致させる。
    /// </summary>
    /// <param name="start">始点（竿先）。</param>
    /// <param name="end">終点（着水点）。</param>
    /// <param name="t">進行度（0〜1）。</param>
    private SEED.Vector3 ArcPoint(SEED.Vector3 start, SEED.Vector3 end, float t)
        => new(
            SEED.Mathf.Lerp(start.x, end.x, t),
            SEED.Mathf.Lerp(start.y, end.y, t) + ParabolaApexCoefficient * flightApexHeight * t * (1f - t),
            SEED.Mathf.Lerp(start.z, end.z, t));

    /// <summary>
    /// 着弾点プレビュー全体（マーカー＋線）を更新する。
    /// <see cref="FishState.Windup"/> のあいだ毎フレーム呼ぶ。
    ///
    /// 着水点は「いま投げたら落ちる点」（<see cref="PreviewDistance"/>＋<see cref="CastYawDegrees"/>）で、
    /// マーカーと線の双方が同じ値を使う（見えているものと結果を必ず一致させる）。
    /// 方向が縮退している場合はプレビューを丸ごと隠す。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateCastPreview(float deltaTime)
    {
        if (CastYawDegrees() is not { } yaw) { HidePreviewLine(); return; }

        var landing = LandingPoint(PreviewDistance(), yaw);

        // 主役は 3D マーカー。線は showPreviewLine が true のときだけ添える。
        UpdateCastMarker(landing, deltaTime);
        UpdatePreviewLine(landing);
    }

    /// <summary>
    /// 着弾点プレビューの<b>線</b>の点列を張り直す
    /// （<see cref="showPreviewLine"/> が true のときだけ描く。既定は false）。
    ///
    /// 点列は「竿先→着弾点の放物線」＋「着水点に置く円」を 1 本に連結したもの。
    /// 円は始点を末尾へ繰り返して閉じる。円の始点は弧の終点（着弾点そのもの）から
    /// 半径ぶん離れているので、その渡り 1 本ぶんの線が余分に描かれるが、
    /// 着弾点マーカーとしては十分に読み取れる（LineRenderer は 1 本の折れ線しか持てないため）。
    ///
    /// 総点数が <see cref="SEED.LineRenderer.MaxPoints"/> を超えないよう、
    /// 弧と円の分割数を上限内へ抑える。
    /// </summary>
    /// <param name="landing">着水点（ワールド）。<see cref="UpdateCastPreview"/> が算出済みの値を渡す。</param>
    private void UpdatePreviewLine(SEED.Vector3 landing)
    {
        if (!showPreviewLine) { return; }
        if (previewLine is not { } preview || !preview.IsValid) { return; }

        var start = RodTipPosition();

        // 分割数の下限を確保したうえで、点数の合計が LineRenderer の上限に収まるよう詰める。
        // 点数 ＝ (弧の分割数 + 1) ＋ (円の分割数 + 1)
        int arcSegments = SEED.Mathf.Max(previewArcSegments, MinArcSegments);
        int ringSegments = SEED.Mathf.Max(previewRingSegments, MinRingSegments);
        int overflow = (arcSegments + 1) + (ringSegments + 1) - SEED.LineRenderer.MaxPoints;
        if (overflow > 0)
        {
            // 上限超過ぶんは弧から優先して削る（円は形が崩れると着弾点が読めなくなるため）。
            // 最小構成（弧 1 分割＋円 3 分割 ＝ 6 点）は上限に必ず収まるので、この 2 段で足りる。
            int reducible = SEED.Mathf.Min(overflow, arcSegments - MinArcSegments);
            arcSegments -= reducible;
            overflow -= reducible;
            if (overflow > 0) { ringSegments = SEED.Mathf.Max(ringSegments - overflow, MinRingSegments); }
        }

        var points = new SEED.Vector3[(arcSegments + 1) + (ringSegments + 1)];

        // 弧: 竿先 → 着弾点
        for (int i = 0; i <= arcSegments; i++)
        {
            points[i] = ArcPoint(start, landing, (float)i / arcSegments);
        }

        // 円: 着弾点を中心に水面上へ描く（末尾は始点を繰り返して閉じる）
        int ringHead = arcSegments + 1;
        float radius = SEED.Mathf.Abs(previewRingRadius);
        for (int i = 0; i <= ringSegments; i++)
        {
            float angle = TwoPi * (i % ringSegments) / ringSegments;
            points[ringHead + i] = new SEED.Vector3(
                landing.x + SEED.Mathf.Sin(angle) * radius,
                landing.y,
                landing.z + SEED.Mathf.Cos(angle) * radius);
        }

        preview.SetPoints(points);
        preview.Visible = true;
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
    /// 左振り量（<see cref="windupAccumPx"/> ÷ <see cref="windupThresholdPx"/>）に比例した
    /// 狙い再生位置へ、指数ブレンドで滑らかに追従させる（マウスのブレで竿・本体がガクつかないように）。
    /// 竿・本体の両 Animator へ同じ狙い位置・ブレンド係数を適用する
    /// （<see cref="ScrubWindupTime"/> が個別に null／IsValid を判定する）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateWindup(float deltaTime)
    {
        if (!windupActive) { return; }

        // 左振り量の割合（0〜1）ぶんだけ振りかぶりポーズへ近づける。
        // Windup 状態では windupAccumPx がしきい値で固定されているので、ここは 1.0 に張り付き、
        // 結果として castWindupSeconds の位置で待機し続ける。
        float ratio = SEED.Mathf.Clamped01(windupAccumPx / WindupThreshold());
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
