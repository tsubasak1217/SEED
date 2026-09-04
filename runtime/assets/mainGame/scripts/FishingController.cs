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
/// - 飛距離   … Windup 中は着水点マーカー（CastMarker）が最短⇔最長を往復するので、投げたい距離で振る
/// - キャスト … マウスを<b>右へ振る</b>（累積が <see cref="castSwingThresholdPx"/> px 超で成立）
/// - 方向     … プレイヤーの正面（沖側）が常にキャスト方向。左右の角度調整はできない
/// - 中断     … キャスト前に左クリックを離すと姿勢を解除して移動へ戻る
/// - リール   … マウスホイール回転量のみで巻き取る
///   （<see cref="metersPerWheelUnit"/> を 0 にすれば無効化できる）
/// - 巻く向き … A / D キーで左右に振れる（<b>ウキ→竿先</b>方向を基準に ±範囲内）
///
/// <b>竿先の取得</b>
/// 竿先は <see cref="rodTip"/>（竿アクタ sao の子アクタ「RodTip」）を<b>読むだけ</b>で得る。
/// 竿は JointAttach で手のボーンへ追従し、その追従はエンジン側
/// （jointattach_ops::propagate_attach_to_descendants）が子孫アクタへ行列差分として
/// 厳密に伝播するので、スクリプト側で竿先を合成する必要はない。
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
        /// <summary>釣り姿勢に入っていない。ウキは非表示、糸も非表示。</summary>
        Idle,

        /// <summary>
        /// 釣り姿勢中でキャスト待ち。左クリックを押したまま「左へ振る」のを待ち受ける。
        /// 左への累積がしきい値未満のあいだは、その割合ぶんだけ振りかぶり姿勢を追従表示する。
        /// </summary>
        Aiming,

        /// <summary>
        /// 振りかぶり完了。着水点マーカーが最短⇔最長を往復し、
        /// 「右へ振る」ジェスチャでキャストが成立する。
        /// </summary>
        Windup,

        /// <summary>キャスト直後。ウキが放物線を描いて着水点へ飛んでいる。</summary>
        Casting,

        /// <summary>着水後。ウキが水面で待機している（アタリ待ち）。</summary>
        Floating,

        /// <summary>巻き取り中。ウキが手前へ寄り、プレイヤーもウキの方へ歩く。</summary>
        Reeling,

        /// <summary>
        /// 魚が食いついている（ヒット中）。
        /// 巻き取りの操作は <see cref="Reeling"/> と完全に同じで、
        /// ウキが <c>食いつき時のウキ沈み量</c> だけ沈み、掛かった魚がウキに追従する。
        /// 手元まで巻き切ると <see cref="Result"/>（釣果）へ遷移する。
        /// 糸のテンション／HP は未実装（釣り仕様の後続タスク）。
        /// </summary>
        Hooked,

        /// <summary>釣果の演出中（<see cref="resultSeconds"/> 後に狙いへ自動復帰する暫定実装）。</summary>
        Result,
    }

    // ─── 他スクリプトからの参照点（静的アクセサ）───────────────

    /// <summary>
    /// 現在シーンで動いている釣りコントローラ（実質シングルトン）。
    ///
    /// 魚は prefab から <c>GameObject.Instantiate</c> で動的生成されるため、
    /// インスペクタの参照フィールドでコントローラを注入できない。そこで
    /// <see cref="OnStart"/> で自分を登録し、<see cref="OnDestroy"/> で解除する。
    ///
    /// <b>ホットリロード</b>: スクリプトアセンブリが差し替わると静的フィールドごと
    /// 作り直され、各スクリプトの <see cref="OnStart"/> が再実行されるので、
    /// この参照も新しいインスタンスで貼り直される（古い値が残ることはない）。
    /// </summary>
    public static FishingController? Current { get; private set; } = null;

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

    /// <summary>ピンポン往復 1 周（往路＋復路）の長さ。<c>PingPong(u, 1)</c> は u が 2 で 1 周する。</summary>
    private const float PingPongCycleUnits = 2f;

    /// <summary>
    /// 巻き方向インジケータ（3D ワールドキャンバス）に与える X 回転（度）。
    ///
    /// 3D キャンバスはアクタのローカル XY 平面に張られ、面の法線はローカル +Z である
    /// （エンジンの canvas_to_world: キャンバス X+ → ローカル X+ /
    ///  キャンバス Y+（＝2D の下方向）→ ローカル Y-）。
    /// YXZ 規約で X=-90 度を与えると各ローカル軸のワールド向きは
    ///   ローカル X = ( cos yaw, 0, -sin yaw)
    ///   ローカル Y = (-sin yaw, 0, -cos yaw)   ← 水平
    ///   ローカル Z = (0, +1, 0)                ← 真上（＝板が水面に寝て上を向く）
    /// になる。テクスチャの矢印は画像の下（＝キャンバス Y+ ＝ ローカル -Y）を指すので、
    /// 矢印のワールド方向は -ローカルY = (sin yaw, 0, cos yaw) ＝ エンジンの yaw 前方そのもの。
    /// つまり yaw に「巻く向きの方位角」をそのまま入れれば矢印が巻く向きを指すため、
    /// <see cref="reelArrowYawOffsetDegrees"/> の既定値は 0 でよい。
    /// </summary>
    private const float ReelArrowPitchDegrees = -90f;

    /// <summary>ビルボード計算で「カメラがほぼ真上／真下」とみなす水平距離の下限（m）。</summary>
    private const float BillboardMinHorizontal = 1e-4f;

    /// <summary><see cref="cameraTransform"/> 未設定時にフォールバックで探すアクタ名。</summary>
    private const string MainCameraActorName = "MainCamera";

    // ─── 参照（インスペクタで割り当てる）───────────────────────

    /// <summary>
    /// プレイヤーの移動スクリプト。釣り姿勢かどうかの判定と、巻き取り中の追従移動に使う。
    /// <b>未設定なら本スクリプトは何もしない</b>（釣り姿勢を知る手段が無いため）。
    /// </summary>
    [Header("参照"), SerializeField(Label = "プレイヤー（PlayerMove）")]
    private PlayerMove? playerMove = null;

    /// <summary>
    /// 竿先アクタのトランスフォーム（糸の始点・キャストの起点・ウキの格納先・巻き取りの基準点）。
    ///
    /// <b>置き方</b>: 竿アクタ（sao）の<b>子</b>として、見た目の竿先の位置に空アクタを置く。
    /// 竿は JointAttach で手のボーンへ追従するが、その追従はエンジン側
    /// （jointattach_ops::propagate_attach_to_descendants）で子孫アクタへも行列差分として
    /// 厳密に伝播するので、子に置いた竿先はせん断（非一様スケール×回転オフセット）込みで正確に付いてくる。
    /// 本スクリプトはこの値を<b>読むだけ</b>で、書き戻しは行わない（書き戻すとエンジンの伝播と競合する）。
    ///
    /// 未設定の場合はプレイヤー自身の位置を竿先の代わりに使う（1 回だけ警告を出す）。
    /// </summary>
    [SerializeField(Label = "竿先アクタ（sao の子・JointAttach に追従）")]
    private SEED.Transform? rodTip = null;

    /// <summary>ウキ（浮き）のトランスフォーム。本スクリプトが毎フレーム位置を決める。</summary>
    [SerializeField(Label = "ウキのトランスフォーム")]
    private SEED.Transform? uki = null;

    /// <summary>
    /// ウキの <see cref="SEED.Model"/>（ウキアクタの Model コンポーネント）。
    ///
    /// キャスト前はこれを <c>Visible = false</c> にして「見えないが位置は竿先に追従している」
    /// 状態を作る（<see cref="ParkFloatHidden"/>）。ウキを地中へ退避させる旧方式だと、
    /// ウキの子アクタ <c>CastCameraTarget</c>（カメラの注視点）まで地中へ行ってしまい、
    /// キャスト開始フレームにカメラが変な場所へ補間される不具合が出ていた。
    ///
    /// 未設定なら旧方式（<see cref="markerParkY"/> へ退避）へフォールバックするので、
    /// インスペクタで割り当てるまでも従来どおり動く。
    /// </summary>
    [SerializeField(Label = "ウキの Model")]
    private SEED.Model? ukiModel = null;

    /// <summary>釣り糸の LineRenderer（ウキ側に付ける想定）。未設定なら糸を描かない。</summary>
    [SerializeField(Label = "釣り糸(LineRenderer)")]
    private SEED.LineRenderer? line = null;

    /// <summary>
    /// 着水点マーカーのトランスフォーム（矢印スプライトを載せた 3D キャンバス「CastMarker」に付ける想定）。
    /// <b>着水点の提示はこのマーカーが唯一の手段である</b>（線によるプレビューは廃止済み）。
    ///
    /// <see cref="FishState.Windup"/> のあいだだけ着水点へ置き、それ以外では
    /// <see cref="markerParkY"/> の高さ（水面のはるか下）へ格納して見えなくする。
    /// マーカーには表示切替の参照を持たせていないため、「画面外へ動かす」ことで
    /// 非表示を表現している（ウキは <see cref="ukiModel"/> による表示切替を使う）。
    /// 未設定なら着水点の提示は行われない（操作自体は同じように成立する）。
    /// </summary>
    [SerializeField(Label = "着水点マーカー")]
    private SEED.Transform? castMarker = null;

    /// <summary>
    /// 着水点マーカーの矢印スプライト（<see cref="castMarker"/> の 3D キャンバス配下にある
    /// SpriteComponent）。<see cref="castMarkerOpacity"/> を毎フレーム色のアルファへ書き込む。
    /// 未設定なら不透明度を触らない（シーンに保存された色のまま表示される）。
    /// </summary>
    [SerializeField(Label = "着水点マーカーのSprite")]
    private SEED.Sprite? castMarkerSprite = null;

    /// <summary>着水点マーカーの不透明度（0〜1）。</summary>
    [SerializeField(Label = "着水点マーカーの不透明度")]
    private float castMarkerOpacity = 0.9f;

    /// <summary>
    /// ビルボードの向き基準にするカメラのトランスフォーム。
    /// 未設定なら <see cref="MainCameraActorName"/> という名前のアクタを<b>1 度だけ</b>探して使う。
    /// どちらも解決できない場合はビルボード回転を行わない（位置だけ更新する）。
    /// </summary>
    [SerializeField(Label = "カメラ")]
    private SEED.Transform? cameraTransform = null;

    /// <summary>
    /// 巻き方向インジケータのトランスフォーム（3D キャンバス「ReelArrow」に付ける想定）。
    ///
    /// <see cref="FishState.Floating"/> / <see cref="FishState.Reeling"/> のあいだだけ
    /// ウキの位置の水面すぐ上へ寝かせて置き、それ以外では <see cref="markerParkY"/> へ格納する
    /// （表示切替 API が無いため、マーカーと同じ「画面外へ動かす」方式）。
    /// </summary>
    [SerializeField(Label = "巻き方向インジケータ")]
    private SEED.Transform? reelArrow = null;

    /// <summary>巻き方向インジケータの矢印スプライト（不透明度の書き込み先）。</summary>
    [SerializeField(Label = "巻き方向インジケータのSprite")]
    private SEED.Sprite? reelArrowSprite = null;

    /// <summary>
    /// 巻き方向インジケータの配置オフセット群。
    /// 基準位置はウキの XZ ＋（<paramref name="reelDirection"/> 方向へ <see cref="reelArrowForwardOffset"/>）
    /// ＋（その右方向へ <see cref="reelArrowSideOffset"/>）で、Y は水面 ＋ <see cref="reelArrowHoverHeight"/>。
    /// 詳細な合成は <see cref="UpdateReelArrow"/> を参照。
    /// </summary>
    [Header("巻き方向インジケータの配置")]
    [SerializeField(Label = "インジケータの前方オフセット(m)")]
    private float reelArrowForwardOffset = 1.5f;

    /// <summary>巻き方向インジケータをウキから見て巻く向きの右方向へどれだけずらすか（メートル）。</summary>
    [SerializeField(Label = "インジケータの横オフセット(m)")]
    private float reelArrowSideOffset = 0f;

    /// <summary>巻き方向インジケータを水面からどれだけ浮かせるか（メートル）。</summary>
    [SerializeField(Label = "インジケータの高さオフセット(m)")]
    private float reelArrowHoverHeight = 0.05f;

    /// <summary>
    /// 巻き方向インジケータの Y 回転オフセット（度）。既定 0 で矢印が巻く向きを指す
    /// （導出は <see cref="ReelArrowPitchDegrees"/> のコメント）。
    /// テクスチャを差し替えて矢印の向きが変わったときだけ調整する。
    /// </summary>
    [SerializeField(Label = "巻き方向インジケータの角度オフセット(度)")]
    private float reelArrowYawOffsetDegrees = 0f;

    /// <summary>巻き方向インジケータの基準不透明度（A/D を振っていないときの値）。</summary>
    [SerializeField(Label = "巻き方向インジケータの基準不透明度")]
    private float reelArrowBaseOpacity = 0.35f;

    /// <summary>
    /// 巻き方向インジケータの追加不透明度（時間で |sin| 往復する振幅）。
    /// 実効不透明度 ＝ clamp01(基準 ＋ 追加 × |sin(2π · f · t)|)（f ＝
    /// <see cref="reelArrowPulseFrequency"/>、t ＝ インジケータを表示し続けている経過秒数
    /// <see cref="reelArrowPulseElapsed"/>）。角度には依存せず、表示中は常に明滅する。
    /// </summary>
    [SerializeField(Label = "巻き方向インジケータの追加不透明度")]
    private float reelArrowExtraOpacity = 0.5f;

    /// <summary>巻き方向インジケータの明滅周波数（Hz）。<see cref="UpdateReelArrow"/> 参照。</summary>
    [SerializeField(Label = "インジケータの明滅周波数(Hz)")]
    private float reelArrowPulseFrequency = 1f;

    /// <summary>
    /// 巻き方向インジケータの一辺の長さ（メートル）。アクタの Transform.Scale の X/Y に入れる
    /// （3D キャンバスは 100px = 1m 換算なので、100x100px のスプライトではこの値が実寸になる）。
    /// </summary>
    [SerializeField(Label = "巻き方向インジケータの長さ(m)")]
    private float reelArrowLength = 2f;

    /// <summary>
    /// 岸際（竿先）に近づいたときの直進巻きに関する設定群。
    ///
    /// 竿先までの残り水平距離 <c>remaining</c> から
    /// <c>steerFactor = clamp01((remaining - straightReelDistance) / straightFadeBand)</c>
    /// を求め、1（遠い＝操舵フル）→0（<see cref="straightReelDistance"/> 以内＝直進のみ）へ
    /// 滑らかに補間する。詳細な適用箇所は <see cref="ComputeReelDirection"/> と
    /// <see cref="UpdateReelArrow"/> のコメントを参照。
    /// </summary>
    [Header("岸際の直進巻き")]
    [SerializeField(Label = "直進のみになる距離(m)")]
    private float straightReelDistance = 6f;

    /// <summary>
    /// <see cref="straightReelDistance"/> の外側に設ける、操舵が徐々に弱まるフェード帯の幅（メートル）。
    /// この帯の中では A/D によるずれ角の上限が線形に絞られていき、既存のずれも自然に 0 へ寄せられる。
    /// </summary>
    [SerializeField(Label = "フェード帯の幅(m)")]
    private float straightFadeBand = 3f;

    /// <summary>
    /// マーカーを隠すときに置く Y 座標（ワールド）。水面よりも十分下に取る。
    /// 表示 API が無いため、この高さへ退避させることで「非表示」を表現する。
    /// </summary>
    [SerializeField(Label = "マーカーの格納位置Y")]
    private float markerParkY = -100f;

    /// <summary>マーカーを水面からどれだけ浮かせて置くか（メートル）。</summary>
    [SerializeField(Label = "マーカーの水面からの高さ")]
    private float markerHoverHeight = 0.1f;

    /// <summary>
    /// 水面（WaterVolume）。着水点の Y とウキの浮かぶ高さに使う。
    /// 未設定なら「竿先の Y − <see cref="waterLevelFallbackDrop"/>」を水面とみなす。
    /// </summary>
    [SerializeField(Label = "水面(WaterVolume)")]
    private SEED.WaterVolume? water = null;

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

    /// <summary>
    /// 巻き取り中、プレイヤーから見て<b>右</b>へ移動しているあいだ再生する本体クリップ名。
    /// 釣り姿勢では海を向いたまま経路上を前後するので、移動は必ず横歩きになる。
    /// </summary>
    [SerializeField(Label = "本体の右横歩きクリップ名")]
    private string playerWalkFishingRightClip = "WalkFishingR";

    /// <summary>巻き取り中、プレイヤーから見て<b>左</b>へ移動しているあいだ再生する本体クリップ名。</summary>
    [SerializeField(Label = "本体の左横歩きクリップ名")]
    private string playerWalkFishingLeftClip = "WalkFishingL";

    /// <summary>魚が食いついているあいだ再生する竿クリップ名（竿 Animator に登録済み）。</summary>
    [SerializeField(Label = "竿のヒットクリップ名")]
    private string hookedClip = "Hooked_竿";

    /// <summary>魚が食いついているあいだ再生するプレイヤー本体クリップ名（本体 Animator に登録済み）。</summary>
    [SerializeField(Label = "本体のヒットクリップ名")]
    private string playerHookedClip = "Hooked";

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

    /// <summary>飛距離の下限（メートル）。着水点マーカーの往復の下端でもある。</summary>
    [Header("キャスト"), SerializeField(Label = "最短飛距離(m)")]
    private float minCastDistance = 3f;

    /// <summary>飛距離の上限（メートル）。着水点マーカーの往復の上端でもある。</summary>
    [SerializeField(Label = "最長飛距離(m)")]
    private float maxCastDistance = 25f;

    /// <summary>
    /// 着水点マーカーが最短⇔最長を 1 往復する秒数（往路＋復路で 1 周）。
    /// 短いほど狙いがシビアになる。
    /// </summary>
    [SerializeField(Label = "着水点の往復周期(秒)")]
    private float previewCycleSeconds = 2.0f;

    /// <summary>
    /// ウキのY回転オフセット（度）。
    /// CastCameraTarget（カメラが追従する子アクタ）はウキの回転に追従するため、
    /// キャスト方向を向かせて常に沖側（海）を見るようにする際、ウキモデル自体の
    /// 制作時の正面向きが実際のモデル正面とズレている場合に補正するための値。
    /// </summary>
    [SerializeField(Label = "ウキのY回転オフセット(度)")]
    private float floatYawOffsetDegrees = 0f;

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
    /// マウスの振り（ジェスチャ）で操作する区間だけカーソルをロックするか。
    ///
    /// ロック中はカーソルが非表示になり、毎フレーム画面中央へ戻される。
    /// エディタ埋め込み Play ではカーソルがビューポートに閉じ込められる（ClipCursor）ため、
    /// ロックしないと端に当たった瞬間 <see cref="SEED.Input.MouseDelta"/> が 0 に潰れ、
    /// 引く／振るのジェスチャが取れなくなる。
    ///
    /// <b>ロックするのは <see cref="FishState.Aiming"/> と <see cref="FishState.Windup"/> だけ</b>。
    /// キャスト以降（Casting / Floating / Reeling / Result）はホイールと A / D しか使わず、
    /// マウスの振りを見ないので、カーソルを隠したままにする理由が無い。
    /// 詳細は <see cref="UpdateCursorLock"/>。
    /// UI をマウスで操作したい場面が出たらここをオフにする。
    /// </summary>
    [Header("操作"), SerializeField(Label = "狙い/振りかぶり中はカーソルをロック")]
    private bool lockCursorWhileFishing = true;

    /// <summary>
    /// 現在エンジンへ適用済みのカーソルロック状態（<see cref="UpdateCursorLock"/> のキャッシュ）。
    /// 望む状態と一致しているあいだは FFI 越しのセッタを呼ばないための番人。
    /// </summary>
    private bool cursorLockApplied = false;

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

    /// <summary>着水点マーカーの往復位相用に積算した秒数（<see cref="FishState.Windup"/> 中のみ進む）。</summary>
    private float previewElapsed = 0f;

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
    /// このフレームに巻き方向インジケータを更新（表示）したか。
    /// <see cref="Update"/> の末尾でこのフラグが false なら必ず格納する
    /// （＝「表示したフレーム以外は必ず隠す」を 1 か所で保証する唯一の出口）。
    /// </summary>
    private bool reelArrowShownThisFrame = false;

    /// <summary>
    /// 巻き方向インジケータを表示し続けている経過秒数（明滅の位相 t）。
    /// <see cref="UpdateReelArrow"/> が呼ばれるフレームごとに deltaTime を加算し、
    /// <see cref="ParkReelArrow"/> で格納されるタイミングで 0 にリセットする
    /// （＝再表示されるたびに基準不透明度から明滅を始める）。
    /// </summary>
    private float reelArrowPulseElapsed = 0f;

    /// <summary>
    /// 名前検索で解決したカメラのトランスフォーム（<see cref="cameraTransform"/> 未設定時のみ使う）。
    /// 毎フレーム探索するのを避けるため 1 度だけ引いて控える。
    /// </summary>
    private SEED.Transform? resolvedCameraTransform = null;

    /// <summary><see cref="resolvedCameraTransform"/> の検索を試行済みか（失敗も含め 1 度だけ）。</summary>
    private bool cameraLookupAttempted = false;

    // ─── 餌（魚の食いつき）───────────────────────────────────

    /// <summary>
    /// 餌（ウキ）の影響半径（メートル）。
    /// 魚は「自分の餌の感知距離 ＋ この値」以内に入った餌に気づいて寄ってくる
    /// （餌そのものの匂いの強さにあたる、釣り側のパラメータ）。
    /// </summary>
    [Header("餌"), SerializeField(Label = "餌の影響半径(m)")]
    private float baitInfluenceRadius = 2f;

    /// <summary>
    /// 食いつき距離（メートル）。魚と餌の水平距離がこれ以下になると
    /// 食いつき待ち（<c>Fish</c> 側の待ち時間）へ入る。
    /// </summary>
    [SerializeField(Label = "食いつき距離(m)")]
    private float biteDistance = 0.4f;

    /// <summary>
    /// 食いつき時のウキ沈み量（メートル）。ヒット中はウキの水面高さから
    /// この分だけ下げて「引き込まれている」見た目を作る。
    /// </summary>
    [SerializeField(Label = "食いつき時のウキ沈み量(m)")]
    private float biteDipDepth = 0.15f;

    /// <summary>
    /// 釣果表示（<see cref="FishState.Result"/>）を維持する秒数。
    /// 経過したら自動で狙い（<see cref="FishState.Aiming"/>）へ戻り、続けて釣れるようにする。
    /// <b>本来の釣果画面が入るまでの暫定処理</b>。
    /// </summary>
    [SerializeField(Label = "釣果表示の秒数")]
    private float resultSeconds = 2f;

    /// <summary>
    /// 現在掛かっている魚（null = 掛かっていない）。
    /// <see cref="TryHook"/> で束縛し、釣り上げ・リリース・キャンセルで必ず解除する。
    /// </summary>
    private Fish? hookedFish = null;

    /// <summary>釣果表示の残り秒数（<see cref="FishState.Result"/> のあいだだけ減る）。</summary>
    private float resultElapsed = 0f;

    /// <summary>
    /// いま餌に関わっている（寄っている・掛かっている）魚のエンティティ集合。
    ///
    /// <see cref="FishManager"/> は魚を出現円環の内側へ毎フレーム押し戻すため、
    /// 餌へ寄っている魚まで引き戻されてしまう。魚は <see cref="RegisterEngaged"/> /
    /// <see cref="UnregisterEngaged"/> で自分を登録し、FishManager は
    /// <see cref="IsEngaged"/> が true の個体をクランプ対象から外す。
    ///
    /// キーは (エンティティ添字, 世代) の組。<c>SEED.GameObject</c> は等価比較を
    /// 実装していないため、値型タプルで確実に一致判定できるようにしている。
    /// </summary>
    private readonly System.Collections.Generic.HashSet<(uint Index, uint Generation)> engagedFish = new();

    // ─── ライフサイクル ───────────────────────────────────────

    /// <summary>
    /// 生成直後の初期化。糸をワールド座標系（親子合成なし）で扱う設定にし、初期状態は非表示にする。
    /// 参照フィールドはこの時点で注入済みだが、参照先スクリプトの OnStart 完了は保証されない。
    /// </summary>
    public override void OnStart()
    {
        // 動的生成される魚から参照できるよう、自分を静的アクセサへ登録する。
        Current = this;

        if (line is { } l && l.IsValid)
        {
            // 竿先（プレイヤー側）とウキ（別アクタ）を結ぶので、点列はワールド座標で渡す。
            l.LocalSpace = false;
            l.Visible = false;
        }

        // 着水点マーカーは開始時点で必ず格納位置へ落としておく
        // （シーン上の初期位置に置き忘れても、実行開始と同時に隠れる）。
        ParkCastMarker();
        // 巻き方向インジケータも同様に隠しておく。
        ParkReelArrow();
    }

    /// <summary>
    /// 破棄直前の後始末。掛かっている魚を解放し、静的アクセサの参照を落とす。
    /// 別インスタンスが既に登録済みなら上書きしない（自分の分だけ取り消す）。
    /// </summary>
    public override void OnDestroy()
    {
        ReleaseHook();
        engagedFish.Clear();
        if (ReferenceEquals(Current, this)) { Current = null; }
    }

    // ─── 魚から参照する公開 API ───────────────────────────────

    /// <summary>
    /// 餌（ウキ）が水中にあって、魚が食いつける状態か。
    /// 着水後の待機（<see cref="FishState.Floating"/>）と巻き取り中
    /// （<see cref="FishState.Reeling"/>）だけが対象で、飛翔中やキャスト前は false。
    /// </summary>
    public bool BaitActive
        => State is FishState.Floating or FishState.Reeling
        && uki is { IsValid: true };

    /// <summary>餌（ウキ）のワールド位置。<see cref="BaitActive"/> が false のときの値は無意味。</summary>
    public SEED.Vector3 BaitPosition
        => uki is { IsValid: true } floatTf ? floatTf.Position : SEED.Vector3.Zero;

    /// <summary>餌の影響半径（メートル）。魚の感知距離に加算される。</summary>
    public float BaitInfluenceRadius => baitInfluenceRadius;

    /// <summary>食いつき距離（メートル）。</summary>
    public float BiteDistance => biteDistance;

    /// <summary>いま魚が掛かっているか。</summary>
    public bool IsHooked => hookedFish is not null;

    /// <summary>
    /// 魚が餌に食いつこうとしたときに呼ぶ。掛かれば true。
    ///
    /// 餌が有効でない、または既に別の魚が掛かっている場合は false を返す
    /// （呼んだ側は回遊へ戻る）。成立時はヒット状態（<see cref="FishState.Hooked"/>）へ
    /// 遷移し、竿・本体をヒット用クリップへ切り替える。
    /// </summary>
    /// <param name="fish">食いつこうとしている魚。</param>
    /// <returns>掛かったら true。</returns>
    public bool TryHook(Fish fish)
    {
        if (!BaitActive || IsHooked) { return false; }

        hookedFish = fish;
        State = FishState.Hooked;
        // ヒット中はマウスの振りを読まないのでカーソルロックを引き直す（解除される）。
        UpdateCursorLock();
        CrossFadeBoth(hookedClip, playerHookedClip);
        SEED.Debug.Log($"[Fishing] ヒット! {fish.DisplayName}（大きさ {fish.Size:F2}）");
        return true;
    }

    /// <summary>
    /// 掛かっている魚を逃がす（外部・内部の共通出口）。掛かっていなければ何もしない。
    /// 状態は変えない（呼び出し側が Idle / Aiming などへ遷移させる）。
    /// </summary>
    public void ReleaseHook()
    {
        if (hookedFish is not { } fish) { return; }

        hookedFish = null;
        fish.ReleaseFromHook();
    }

    /// <summary>
    /// 餌に関わっている魚として登録する（円環クランプの除外対象になる）。
    /// 同じ魚を重ねて登録しても安全。
    /// </summary>
    /// <param name="fish">登録する魚のアクタ。</param>
    public void RegisterEngaged(SEED.GameObject fish)
    {
        if (!fish.IsValid) { return; }
        engagedFish.Add((fish.Entity.Index, fish.Entity.Generation));
    }

    /// <summary>餌に関わっている魚の登録を外す（回遊へ戻ったとき・破棄されたとき）。</summary>
    /// <param name="fish">登録を外す魚のアクタ。</param>
    public void UnregisterEngaged(SEED.GameObject fish)
    {
        if (!fish.IsValid) { return; }
        engagedFish.Remove((fish.Entity.Index, fish.Entity.Generation));
    }

    /// <summary>
    /// 指定の魚が餌に関わっている（寄っている・掛かっている）か。
    /// <see cref="FishManager"/> の円環クランプが除外判定に使う。
    /// </summary>
    /// <param name="fish">判定する魚のアクタ。</param>
    /// <returns>関わっていれば true。</returns>
    public bool IsEngaged(SEED.GameObject fish)
        => fish.IsValid && engagedFish.Contains((fish.Entity.Index, fish.Entity.Generation));

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
        // カーソルロックを状態へ同期する。ここで毎フレーム引き直しておけば、
        // 途中で return する経路（プレイヤー未設定・待機中など）でもロックが残らない。
        UpdateCursorLock();

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
                // UpdateAiming は Windup/Idle へしか遷移しない（Casting への直接遷移はない）が、
                // 念のため「まだ Aiming のままか」を確認してから隠す。同フレーム内で
                // Windup へ遷移していた場合でも、この直後 switch には戻らないため
                // ここで隠しても Windup 側の表示状態には影響しない。
                if (State == FishState.Aiming) { ParkFloatHidden(); }        // キャスト前のウキは非表示にしておく
                break;

            case FishState.Windup:
                UpdateWindupState(ctx.DeltaTime);
                // UpdateWindupState は StartCast() を呼ぶことがあり、その中で State が
                // Casting に変わり ShowFloat() でウキが竿先に表示される。ここで無条件に
                // ParkFloatHidden() を呼ぶと、Casting へ遷移した同じフレームでウキを
                // 再び非表示に戻してしまい、キャスト中ずっと見えなくなるバグになる。
                // そのため「まだ Windup のままか」を確認してから隠す。
                if (State == FishState.Windup) { ParkFloatHidden(); }        // キャスト前のウキは非表示にしておく
                break;

            case FishState.Casting:
                UpdateFlight(ctx.DeltaTime);
                break;

            case FishState.Floating:
            case FishState.Reeling:
            case FishState.Hooked:
                // ヒット中も巻き取りの操作系（ホイール・A/D 操舵）はまったく同じ。
                UpdateReeling(ctx.DeltaTime);
                break;

            case FishState.Result:
                UpdateResult(ctx.DeltaTime);
                break;
        }

        // ── 巻き方向インジケータの表示／非表示の唯一の出口 ──────────────
        // このフレームに UpdateReelArrow が走らなかった（＝表示すべき状況ではない、
        // または向きが確定しなかった）なら必ず格納する。状態遷移や早期 return が
        // 増えても「表示しっぱなし」にならないよう、判定をここ 1 か所に集約する。
        if (!reelArrowShownThisFrame) { ParkReelArrow(); }
        reelArrowShownThisFrame = false;
    }

    /// <summary>固定タイムステップの更新。物理など時間刻みを一定にしたい処理向け。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>
    /// Update 後の更新。ウキの位置が確定した後に釣り糸を張り直す。
    /// 竿先アクタはエンジン（JointAttach 伝播）が更新するので、ここでは触らず読むだけ。
    /// </summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
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
        ReleaseHook();                 // 掛かったままの魚が居れば逃がす
        ParkFloatHidden();
        HideCastPreview();
        // 直前に PlayerMove.EnterFishingStance が本体アニメを触っているのでラッチを捨てる
        ResetPlayerClipLatch();
        CrossFadeBoth(floatClip, playerFloatClip);
        // マウスの振りを読む区間へ入ったのでカーソルをロックする（判断は UpdateCursorLock が一元管理）。
        UpdateCursorLock();
        SEED.Debug.Log("[Fishing] Aiming");
    }

    /// <summary>
    /// 釣りを中断して待機へ戻す（外部から釣り姿勢を解除された場合の後始末）。
    /// ウキを非表示にし、糸と着水点マーカーを隠し、竿のアニメ指定は <see cref="PlayerMove"/> 側へ返す。
    /// </summary>
    private void CancelToIdle()
    {
        State = FishState.Idle;
        ResetGesture();
        ReleaseHook();                 // 姿勢解除・中断でも必ず魚を逃がす
        // この後 PlayerMove 側（ExitFishingStance・通常移動のアニメ）が本体を触るのでラッチを捨てる
        ResetPlayerClipLatch();
        ParkFloatHidden();
        HideLine();
        HideCastPreview();
        ParkReelArrow();
        // 釣り状態を抜けたらカーソルを必ず返す（姿勢解除・中断の唯一の出口）。
        UpdateCursorLock();
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
    /// カーソルロックの望ましい状態。
    ///
    /// マウスの振り（<see cref="SEED.Input.MouseDelta"/>）を読む区間＝
    /// <see cref="FishState.Aiming"/>（振りかぶり待ち）と <see cref="FishState.Windup"/>（振り抜き待ち）
    /// のあいだだけ true。キャスト以降はホイールと A / D しか使わないので false。
    /// <see cref="lockCursorWhileFishing"/> がオフなら常に false（解除は必ず通る）。
    /// </summary>
    private bool WantsCursorLock()
        => lockCursorWhileFishing && (State == FishState.Aiming || State == FishState.Windup);

    /// <summary>
    /// カーソルロックを現在の状態へ合わせる【適用の唯一の集約点】。
    ///
    /// 状態から望ましい値を毎回引き直すので、状態遷移の経路を数える必要が無い
    /// （巻き取り終了で <see cref="FinishReeling"/> が <see cref="FishState.Aiming"/> へ戻り、
    /// 左クリックを押しっぱなしのまま次のキャストへ入る経路でも、自動的に再ロックされる）。
    /// 毎フレーム呼んでも安全なように、実際に変化したときだけ FFI のセッタを叩く。
    /// </summary>
    private void UpdateCursorLock()
    {
        bool desired = WantsCursorLock();
        if (desired == cursorLockApplied) { return; }

        cursorLockApplied = desired;
        SEED.Input.CursorLocked = desired;
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
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateAiming(float deltaTime)
    {
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

        // 振りかぶりポーズを保持しつつ、着水点マーカーを更新する
        UpdateWindup(deltaTime);
        UpdateCastPreview();
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
    /// プレイヤーの正面（水平化）そのもの。
    /// 正面が真上／真下に潰れている縮退時は null（起こらない想定の保険）。
    /// </summary>
    private float? CastYawDegrees()
    {
        var baseDir = new SEED.Vector3(transform.Forward.x, 0f, transform.Forward.z);
        if (baseDir.SqrMagnitude < SqrEpsilon) { return null; }

        // エンジン規約: yaw = atan2(x, z)、前方 +Z
        return SEED.Mathf.Atan2(baseDir.x, baseDir.z) * SEED.Mathf.Rad2Deg;
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

        // ウキをキャスト方向（沖側）へ向ける。
        // CastCameraTarget はウキの子アクタで、親の回転を継承してカメラの向きを決めるため、
        // ここでウキのYawをキャスト方向に合わせておくとカメラが常に海を向く。
        // Casting/Floating/Reeling 中は SetFloatPosition が Position のみを書き換えるので、
        // この回転はキャスト開始時に一度設定すればそのまま保持される。
        if (uki is { IsValid: true } floatTf)
        {
            floatTf.Rotation = new SEED.Vector3(0f, yaw + floatYawOffsetDegrees, 0f);
        }

        // ── ウキを「同じフレームのうちに」飛翔開始位置（竿先）へ置く ──────────
        // CameraMove は LateUpdate でカメラの注視点を CastCameraTarget（ウキの子アクタ）へ
        // 切り替える。ここで位置を確定させておかないと、注視点が「前フレームの退避位置」
        // のままカメラが補間され、キャスト開始の一瞬だけ変な場所を向いてしまう。
        // 表示も同時に戻す（ParkFloatHidden で Visible=false にしてあるため）。
        ShowFloat();
        SetFloatPosition(flightStart);

        castDistance = clamped;
        flightElapsed = 0f;
        reelAngleOffsetDegrees = 0f;
        reelIdleElapsed = 0f;

        State = FishState.Casting;
        HideCastPreview();

        // 以降はホイールと A / D だけの操作になるのでカーソルを返す（振りを読む区間の終わり）。
        UpdateCursorLock();

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

        SEED.Debug.Log($"[Fishing] Cast 距離={clamped:F1}m 角={yawDegrees:F1}度");
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

        // 巻き入力の有無で Floating ⇔ Reeling を往復する。
        // ヒット中（Hooked）は状態もクリップもヒット用のまま固定し、往復させない
        // （巻き取りの移動処理だけを同じロジックで走らせる）。
        if (amount > ReelInputEpsilon)
        {
            reelIdleElapsed = 0f;
            if (!IsHooked && State != FishState.Reeling)
            {
                State = FishState.Reeling;
                CrossFadeBoth(reelClip, playerReelClip);
            }
        }
        else
        {
            reelIdleElapsed += deltaTime;
            if (!IsHooked && State == FishState.Reeling && reelIdleElapsed > reelIdleSeconds)
            {
                State = FishState.Floating;
                CrossFadeBoth(floatClip, playerFloatClip);
            }
        }

        // 巻き取りの基準点（＝竿先。未設定時のフォールバックは RodTipPosition が受け持つ）
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

        // 岸際の直進巻き係数（1＝遠くて操舵フル、0＝直進距離以内で操舵ゼロ）。
        // remaining はこの関数で既に算出済みなので、ここで一度だけ求めて
        // ComputeReelDirection / UpdateReelArrow の両方へ使い回す（距離計算の重複を避ける）。
        float steerFactor = SEED.Mathf.Clamped01(
            (remaining - straightReelDistance) / SEED.Mathf.Max(straightFadeBand, DivideEpsilon));

        // 巻く向き（A / D による左右のずれを含む、基準方向からの水平単位ベクトル）
        var dir = ComputeReelDirection(toTarget, deltaTime, steerFactor);

        // 進行方向が基準点への方向から 90 度以上外れている（内積 <= 0）＝
        // これ以上巻いても基準点へ近づけない向きなので、素通りする前に巻き取りを完了させる。
        float approach = toTarget.x * dir.x + toTarget.z * dir.z;
        if (approach <= 0f)
        {
            FinishReeling();
            return;
        }

        // 巻く向きが確定したので、ウキの足元へインジケータを寝かせて置く。
        // steerFactor が 0（直進距離以内）ならインジケータは表示せず格納する。
        if (steerFactor > 0f)
        {
            UpdateReelArrow(floatTf.Position, dir, steerFactor, deltaTime);
        }

        // このフレームの移動量を「残りの水平距離」でクランプし、基準点を追い越さないようにする
        float step = SEED.Mathf.Min(amount, remaining);
        var next = floatTf.Position + dir * step;
        SetFloatPosition(new SEED.Vector3(next.x, FloatSurfaceY(), next.z));

        // プレイヤーはウキに一番近い経路上の点へ歩いて付いていく（移動の実装は PlayerMove の責務）。
        // 戻り値は「正面から見てどちらへ動いたか」なので、そのまま横歩きアニメの選択に使う。
        if ((State == FishState.Reeling || IsHooked) && playerMove is { } pm)
        {
            int lateral = pm.MoveTowardWorldPoint(floatTf.Position, deltaTime);
            UpdatePlayerReelBodyClip(lateral, amount);
        }

        // 移動後の残り距離が完了距離以下になったら 1 回の釣りを終える
        if (HorizontalDistance(next, target) <= reelEndDistance)
        {
            FinishReeling();
        }
    }

    /// <summary>
    /// 巻き取り中のプレイヤー本体クリップを決める。
    ///
    /// 横移動しているあいだは左右の横歩きクリップ、止まっているあいだは
    /// 巻き入力の有無に応じて巻き取り／待ちクリップへ戻す。
    /// 実際の切替は <see cref="SetPlayerClip"/> がラッチで間引くので毎フレーム呼んでよい。
    /// </summary>
    /// <param name="lateral">
    /// <see cref="PlayerMove.MoveTowardWorldPoint"/> の戻り値
    /// （+1 = 右へ移動 / -1 = 左へ移動 / 0 = 停止）。
    /// </param>
    /// <param name="reelAmount">このフレームの巻き取り量（メートル）。停止時の分岐に使う。</param>
    private void UpdatePlayerReelBodyClip(int lateral, float reelAmount)
    {
        if (lateral == PlayerMove.LateralRight) { SetPlayerClip(playerWalkFishingRightClip); return; }
        if (lateral == PlayerMove.LateralLeft) { SetPlayerClip(playerWalkFishingLeftClip); return; }

        // 停止中: ヒット中はヒットアニメ固定。それ以外は巻き入力の有無で巻き取り／待ちへ。
        if (IsHooked) { SetPlayerClip(playerHookedClip); return; }
        SetPlayerClip(reelAmount > ReelInputEpsilon ? playerReelClip : playerFloatClip);
    }

    /// <summary>
    /// 巻き取り完了時の分岐。
    /// 魚が掛かっていれば釣果（<see cref="FishState.Result"/>）、
    /// 何も掛かっていなければ再びキャスト待ちへ戻る。
    /// </summary>
    private void FinishReeling()
    {
        // ── 釣り上げ成立 ──
        if (hookedFish is { } caught)
        {
            SEED.Debug.Log($"[Fishing] 釣り上げ: {caught.DisplayName}（大きさ {caught.Size:F2}）");

            // 釣り上げた魚はシーンから消す（破棄はフレーム末尾に遅延適用される）。
            // 破棄前に円環クランプの除外登録も外しておく。
            var caughtObject = caught.Actor;
            UnregisterEngaged(caughtObject);
            caughtObject.Destroy();

            hookedFish = null;           // ReleaseFromHook は呼ばない（この個体は消えるため）
            State = FishState.Result;
            resultElapsed = 0f;
            return;
        }

        // 空振り: ウキを非表示にし、糸を隠して次のキャストを待つ
        State = FishState.Aiming;
        ResetGesture();
        ParkFloatHidden();
        HideLine();
        CrossFadeBoth(floatClip, playerFloatClip);
        // 再び振りを読む区間へ戻るのでロックし直す（左クリック押しっぱなしでの連続キャスト対応）。
        UpdateCursorLock();
        SEED.Debug.Log("[Fishing] Aiming（空振り）");
    }

    /// <summary>
    /// 釣果表示（<see cref="FishState.Result"/>）の更新。
    ///
    /// <b>本来の釣果画面が入るまでの暫定処理</b>: <see cref="resultSeconds"/> 経過したら
    /// 自動で狙い（<see cref="FishState.Aiming"/>）へ戻し、続けて釣れるようにする。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateResult(float deltaTime)
    {
        resultElapsed += deltaTime;
        if (resultElapsed < resultSeconds) { return; }

        // 空振り時と同じ後片付けで狙いへ戻る。
        State = FishState.Aiming;
        ResetGesture();
        ParkFloatHidden();
        HideLine();
        CrossFadeBoth(floatClip, playerFloatClip);
        UpdateCursorLock();
        SEED.Debug.Log("[Fishing] Aiming（釣果表示おわり）");
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
    /// 巻き取りの基準点（ワールド）を返す。＝竿先（<see cref="RodTipPosition"/>）。
    ///
    /// 「ウキ → 竿先」がそのまま巻く向きの基準になるので、専用の基準アクタは持たない
    /// （竿先が未設定のときのフォールバックは <see cref="RodTipPosition"/> が一元的に受け持つ）。
    /// </summary>
    private SEED.Vector3 ReelTargetPosition() => RodTipPosition();

    /// <summary>
    /// 巻く向き（水平・正規化済み）を返す。
    ///
    /// 基準は「ウキ → 巻き取りの基準点（<see cref="ReelTargetPosition"/>、通常は竿先）」の水平方向。
    /// そこから A / D キーで <see cref="reelAngleOffsetDegrees"/> を
    /// ±<see cref="reelAngleRangeDegrees"/>/2 の範囲で振れる。
    ///
    /// <b>岸際の直進巻き</b>: <paramref name="steerFactor"/>（1＝操舵フル、0＝直進のみ）に応じて
    /// 許容範囲そのものを ±(<see cref="reelAngleRangeDegrees"/>/2 × steerFactor) へ絞る。
    /// これにより steerFactor が 0 に近づくほど A/D の効きが弱まるだけでなく、
    /// 既に付いていたずれ角もこのクランプによって自動的に 0 へ寄せられ、
    /// 岸へ近づくにつれて滑らかに直進へ収束する。
    /// </summary>
    /// <param name="toTarget">ウキ → 基準点の水平ベクトル（Y 成分は無視する。呼び出し側で算出済み）。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <param name="steerFactor">岸際の直進巻き係数（1＝遠くて操舵フル、0＝直進距離以内で操舵ゼロ）。</param>
    private SEED.Vector3 ComputeReelDirection(SEED.Vector3 toTarget, float deltaTime, float steerFactor)
    {
        // A / D で基準からのずれ角を動かす（範囲外へは出さない）。
        // ずれ角は「ウキ → 竿先」を基準にした角度なので、ウキ側から見ると左右が反転する。
        // プレイヤーの操作感（D でウキが右へ寄る）に合わせて符号を逆に取る。
        // steerFactor で範囲を絞るので、岸際では A/D 入力自体をここで無視する
        // （steerFactor <= 0 のときは範囲が ±0 になり turn を加えても即クランプされるが、
        //   入力の意図を明確にするため先に無視しておく）。
        float half = SEED.Mathf.Abs(reelAngleRangeDegrees) * 0.5f * steerFactor;
        float turn = 0f;
        if (steerFactor > 0f)
        {
            if (SEED.Input.GetKey(SEED.KeyCode.A)) { turn += 1f; }   // A: ウキを左へ寄せる
            if (SEED.Input.GetKey(SEED.KeyCode.D)) { turn -= 1f; }   // D: ウキを右へ寄せる
        }
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
    /// 糸の始点・キャストの起点・巻き取りの基準点となる竿先のワールド位置を返す。
    ///
    /// 竿先アクタ（<see cref="rodTip"/>＝sao の子「RodTip」）の値は
    /// エンジンの JointAttach 伝播が毎フレーム厳密に更新しているので、そのまま読んで使う。
    /// 未設定・破棄済みならプレイヤー自身の位置へフォールバックする
    /// （竿先がプレイヤー原点に潰れるため、着水点も糸も明らかにずれる。1 回だけ警告を出す）。
    /// </summary>
    private SEED.Vector3 RodTipPosition()
    {
        if (rodTip is { } tip && tip.IsValid) { return tip.Position; }

        // ── 未解決フォールバック（1 回だけ警告する）──
        if (!rodTipMissingWarned)
        {
            rodTipMissingWarned = true;
            SEED.Debug.LogWarning(
                "[FishingController] 竿先アクタ（rodTip）の参照が解決できません。" +
                "インスペクタの「竿先アクタ」に sao の子アクタ RodTip を割り当ててください。" +
                "プレイヤー自身の位置を竿先の代わりに使います。");
        }
        return transform.Position;
    }

    /// <summary>竿先参照の未解決警告を出したか（ログを 1 回に絞るためのフラグ）。</summary>
    private bool rodTipMissingWarned = false;

    /// <summary>
    /// 現在の水面 Y（ワールド）を返す。
    /// <see cref="water"/> 未設定なら「竿先 −<see cref="waterLevelFallbackDrop"/>」を仮の水面とする。
    /// </summary>
    private float WaterSurfaceY()
    {
        if (water is { } w && w.IsValid) { return w.WaterLevel; }

        // ── 未解決フォールバック（1 回だけ警告する）──
        // ここへ落ちるとウキは「竿先の少し下」に浮くだけになり、
        // 見た目には「ウキが空中に浮いている」不具合として現れる。
        // 毎フレーム出すとログが埋まるのでフラグで 1 回に絞る。
        if (!waterMissingWarned)
        {
            waterMissingWarned = true;
            SEED.Debug.LogWarning(
                "[FishingController] 水面(WaterVolume) の参照が解決できません。" +
                "インスペクタの「水面(WaterVolume)」が未設定か、参照先アクタ／スロット名が" +
                "見つかりません。竿先-" + waterLevelFallbackDrop + "m を仮の水面として使います。");
        }
        return RodTipPosition().y - waterLevelFallbackDrop;
    }

    /// <summary>水面参照の未解決警告を出したか（ログを 1 回に絞るためのフラグ）。</summary>
    private bool waterMissingWarned = false;

    /// <summary>水面に浮くウキの上下揺れのオフセット（メートル）。</summary>
    private float BobOffset()
        => bobAmplitude * SEED.Mathf.Sin(bobElapsed * bobFrequency * TwoPi);

    /// <summary>
    /// ウキを置くべき Y（ワールド）を返す【ウキ高さの唯一の算出点】。
    /// 水面 ＋ 上下揺れ。ヒット中は <see cref="biteDipDepth"/> だけ沈める。
    /// </summary>
    private float FloatSurfaceY()
        => WaterSurfaceY() + BobOffset() - (IsHooked ? biteDipDepth : 0f);

    /// <summary>ウキを指定ワールド位置へ移動する（未設定・無効なら何もしない）。</summary>
    /// <param name="position">移動先のワールド位置。</param>
    private void SetFloatPosition(SEED.Vector3 position)
    {
        if (uki is not { } floatTf || !floatTf.IsValid) { return; }
        floatTf.Position = position;
    }

    /// <summary>
    /// ウキを非表示にする（キャスト前・キャンセル後・釣り終了後）。
    ///
    /// <see cref="ukiModel"/> が設定されていれば <b>描画だけ</b>を切り、位置は竿先に
    /// 置いたまま追従させる。こうするとウキの子アクタ <c>CastCameraTarget</c>
    /// （カメラの注視点）が常に妥当な位置に居るので、キャスト開始フレームに
    /// カメラが地中へ補間される不具合が起きない。
    ///
    /// <see cref="ukiModel"/> 未設定のときだけ、旧方式（竿先の XZ ＋
    /// <see cref="markerParkY"/> へ退避させて画面外へ追いやる）へフォールバックする。
    /// </summary>
    private void ParkFloatHidden()
    {
        var tip = RodTipPosition();
        if (ukiModel is { IsValid: true } model)
        {
            // 表示だけを切り、位置は竿先へ置いて追従させ続ける。
            model.Visible = false;
            SetFloatPosition(tip);
        }
        else
        {
            // 旧方式（表示切替の参照が未設定のときのフォールバック）。
            SetFloatPosition(new SEED.Vector3(tip.x, markerParkY, tip.z));
        }
        HideLine();
    }

    /// <summary>
    /// ウキを表示状態へ戻す（キャスト開始時）。
    /// <see cref="ukiModel"/> 未設定なら何もしない（旧方式では飛翔中の位置更新で
    /// そのまま見えるようになるため、追加の処理は要らない）。
    /// </summary>
    private void ShowFloat()
    {
        if (ukiModel is { IsValid: true } model) { model.Visible = true; }
    }

    /// <summary>釣り糸を非表示にする（点列は残したままフラグだけ落とす）。</summary>
    private void HideLine()
    {
        if (line is { } l && l.IsValid) { l.Visible = false; }
    }

    /// <summary>
    /// 着水点の提示を隠す（マーカーを格納位置へ退避させる）。
    /// Windup 以外の全経路（狙い開始・キャンセル・キャスト開始）から呼ばれる唯一の出口。
    /// </summary>
    private void HideCastPreview() => ParkCastMarker();

    /// <summary>
    /// 着水点マーカーを格納位置（<see cref="markerParkY"/>）へ退避させる。
    ///
    /// アクタ／モデルの表示切替 API が無いため、これが「非表示」の実装である。
    /// XZ はその場に残し、Y だけを水面のはるか下へ落とす。
    /// </summary>
    private void ParkCastMarker()
    {
        if (castMarker is not { } marker || !marker.IsValid) { return; }

        var p = marker.Position;
        marker.Position = new SEED.Vector3(p.x, markerParkY, p.z);
    }

    /// <summary>
    /// 着水点マーカー（矢印スプライトを載せた 3D キャンバス）を着水点へ置き、
    /// カメラの方を向くようビルボード回転させる。
    ///
    /// キャンバスの面はアクタのローカル XY 平面（法線＝ローカル +Z）なので、
    /// 「+Z をカメラへ向ける」＝「板をカメラ正面に立てる」になる。
    /// テクスチャの矢印は画像の下（＝キャンバス Y+ ＝ ローカル -Y）を向くので、
    /// ロール 0 のビルボードでは画面上でも下＝着水点を指したままになる。
    /// </summary>
    /// <param name="landing">着水点（ワールド）。Y は水面高さ。</param>
    private void UpdateCastMarker(SEED.Vector3 landing)
    {
        if (castMarker is not { } marker || !marker.IsValid) { return; }

        marker.Position = new SEED.Vector3(landing.x, landing.y + markerHoverHeight, landing.z);
        BillboardToward(marker);
        ApplySpriteOpacity(castMarkerSprite, castMarkerOpacity);
    }

    /// <summary>
    /// アクタの +Z（＝3D キャンバスの面法線）がカメラを向くよう、YXZ オイラー角を書き込む。
    ///
    /// <b>向きの導出</b>: エンジンの回転規約（YXZ・+Z 前方）では
    /// <c>forward = (sin Y · cos X, -sin X, cos Y · cos X)</c> である。
    /// 目標方向 d（アクタ → カメラ・単位化前）に対して
    ///   Y = atan2(d.x, d.z)          … 水平成分の方位角（forward.x / forward.z が一致）
    ///   X = asin(-d.y / |d|)         … forward.y = -sin X = d.y / |d| が成り立つ
    /// とすれば forward が d と一致する。ロール（Z）は 0 のまま＝画面上の上下が保たれる。
    /// カメラが真上／真下にあり水平成分が消える縮退時は方位角を更新しない
    /// （atan2(0,0) の不定値でマーカーがちらつくのを防ぐ）。
    /// </summary>
    /// <param name="target">向きを書き込む対象（着水点マーカーなど）。</param>
    private void BillboardToward(SEED.Transform target)
    {
        if (ResolveCameraTransform() is not { } cam) { return; }

        var d = cam.Position - target.Position;
        float lenSq = d.x * d.x + d.y * d.y + d.z * d.z;
        if (lenSq < SqrEpsilon) { return; }

        float len = SEED.Mathf.Sqrt(lenSq);
        float horizontal = SEED.Mathf.Sqrt(d.x * d.x + d.z * d.z);
        if (horizontal < BillboardMinHorizontal) { return; }

        float yaw = SEED.Mathf.Atan2(d.x, d.z) * SEED.Mathf.Rad2Deg;
        float pitch = SEED.Mathf.Asin(SEED.Mathf.Clamped(-d.y / len, -1f, 1f)) * SEED.Mathf.Rad2Deg;
        target.Rotation = new SEED.Vector3(pitch, yaw, 0f);
    }

    /// <summary>
    /// ビルボードの基準にするカメラを返す。
    /// インスペクタ指定（<see cref="cameraTransform"/>）が最優先で、
    /// 未設定なら <see cref="MainCameraActorName"/> のアクタを 1 度だけ名前検索して控える。
    /// どちらも取れなければ null（呼び出し側はビルボードを諦める）。
    /// </summary>
    private SEED.Transform? ResolveCameraTransform()
    {
        if (cameraTransform is { } assigned && assigned.IsValid) { return assigned; }

        // 名前検索は 1 度だけ（毎フレーム引くとシーン全体の DFS を繰り返すことになる）。
        if (!cameraLookupAttempted)
        {
            cameraLookupAttempted = true;
            var found = SEED.GameObject.Find(MainCameraActorName);
            if (found.IsValid) { resolvedCameraTransform = found.GetComponent<SEED.Transform>(); }
        }

        if (resolvedCameraTransform is { } cached && cached.IsValid) { return cached; }
        return null;
    }

    /// <summary>
    /// スプライトの色のアルファだけを書き換える（RGB はシーンで設定した色を保つ）。
    /// スプライト未設定・破棄済みなら何もしない。
    /// </summary>
    /// <param name="sprite">対象スプライト（未設定可）。</param>
    /// <param name="opacity">不透明度（0〜1 へクランプする）。</param>
    private void ApplySpriteOpacity(SEED.Sprite? sprite, float opacity)
    {
        if (sprite is not { } s || !s.IsValid) { return; }
        s.Color = s.Color.WithAlpha(SEED.Mathf.Clamped01(opacity));
    }

    /// <summary>
    /// 巻き方向インジケータを格納位置（<see cref="markerParkY"/>）へ退避させる。
    /// マーカーと同じく「画面外へ動かす」ことで非表示を表現する。
    /// </summary>
    private void ParkReelArrow()
    {
        // 次に表示されたとき明滅を基準不透明度から再開させる。
        reelArrowPulseElapsed = 0f;

        if (reelArrow is not { } arrow || !arrow.IsValid) { return; }

        var p = arrow.Position;
        arrow.Position = new SEED.Vector3(p.x, markerParkY, p.z);
    }

    /// <summary>
    /// 巻き方向インジケータをウキより手前（プレイヤー側）の水面すぐ上へ置き、巻く向きへ回す。
    ///
    /// <b>位置</b>: ウキの XZ を基準に、<paramref name="reelDirection"/>（＝竿先へ向かう巻く向き）
    /// へ <see cref="reelArrowForwardOffset"/>、その右方向
    /// （<c>(reelDirection.z, 0, -reelDirection.x)</c>）へ <see cref="reelArrowSideOffset"/> だけ
    /// ずらす。Y は水面 ＋ <see cref="reelArrowHoverHeight"/>。
    ///
    /// <b>姿勢</b>: X 回転は <see cref="ReelArrowPitchDegrees"/> 固定（板が水面に寝て上を向く）、
    /// Y 回転は「巻く向きの方位角 ＋ <see cref="reelArrowYawOffsetDegrees"/>」。
    /// 方位角と矢印の向きの対応は <see cref="ReelArrowPitchDegrees"/> のコメントで導出している。
    ///
    /// <b>不透明度</b>: <c>clamp01(基準 ＋ 追加 × |sin(2π · f · t)|) × steerFactor</c>
    /// （f ＝ <see cref="reelArrowPulseFrequency"/>、t ＝ インジケータを表示し続けている経過秒数
    /// <see cref="reelArrowPulseElapsed"/>、steerFactor ＝ 岸際の直進巻き係数）。角度には依存しない
    /// 時間ベースの明滅で、岸（<see cref="straightReelDistance"/>）へ近づくほど steerFactor で
    /// 全体がフェードアウトする。なお steerFactor が 0 の呼び出しはこの関数が呼ばれる前
    /// （<see cref="UpdateReeling"/>）でスキップされ、その場合はフレーム末尾の
    /// 「表示されなかったら格納」経路（<see cref="ParkReelArrow"/>）でインジケータが自動的に隠され、
    /// 明滅の経過秒数も 0 へリセットされる。
    ///
    /// <b>大きさ</b>: <see cref="reelArrowLength"/> を Transform.Scale の X/Y に入れる
    /// （3D キャンバスは 100px = 1m 換算。Z はキャンバス平面に効かないので 1 固定）。
    /// </summary>
    /// <param name="floatPosition">ウキのワールド位置（XZ だけ使う）。</param>
    /// <param name="reelDirection">巻く向き（水平・正規化済み。ウキから竿先へ向かう方向）。</param>
    /// <param name="steerFactor">岸際の直進巻き係数（1＝遠くて操舵フル、0＝直進距離以内で操舵ゼロ）。不透明度の乗数にも使う。</param>
    /// <param name="deltaTime">このフレームの経過秒数（明滅の位相を進めるのに使う）。</param>
    private void UpdateReelArrow(SEED.Vector3 floatPosition, SEED.Vector3 reelDirection, float steerFactor, float deltaTime)
    {
        if (reelArrow is not { } arrow || !arrow.IsValid) { return; }
        // 向きが縮退しているフレームは表示しない（このあと Update 末尾で格納される）。
        if (reelDirection.SqrMagnitude < SqrEpsilon) { return; }

        // ウキから見て「前方＝巻く向き」「右＝前方を時計回りに90度回した向き」で配置をオフセットする。
        var right = new SEED.Vector3(reelDirection.z, 0f, -reelDirection.x);
        float baseX = floatPosition.x
                    + reelDirection.x * reelArrowForwardOffset
                    + right.x * reelArrowSideOffset;
        float baseZ = floatPosition.z
                    + reelDirection.z * reelArrowForwardOffset
                    + right.z * reelArrowSideOffset;

        arrow.Position = new SEED.Vector3(
            baseX, WaterSurfaceY() + reelArrowHoverHeight, baseZ);

        float yaw = SEED.Mathf.Atan2(reelDirection.x, reelDirection.z) * SEED.Mathf.Rad2Deg;
        arrow.Rotation = new SEED.Vector3(
            ReelArrowPitchDegrees, yaw + reelArrowYawOffsetDegrees, 0f);

        // 板の一辺の長さ（メートル）。キャンバス平面は X/Y なので Z は 1 のまま。
        arrow.Scale = new SEED.Vector3(reelArrowLength, reelArrowLength, 1f);

        // 表示され続けている経過秒数を進め、時間ベースの明滅位相を求める。
        // |sin(2π f t)| は角度に依存せず、表示中は常に基準⇔基準+追加の間で往復する。
        reelArrowPulseElapsed += deltaTime;
        float phase = TwoPi * reelArrowPulseFrequency * reelArrowPulseElapsed;
        float opacity = SEED.Mathf.Clamped01(reelArrowBaseOpacity
                      + reelArrowExtraOpacity * SEED.Mathf.Abs(SEED.Mathf.Sin(phase))) * steerFactor;
        ApplySpriteOpacity(reelArrowSprite, opacity);

        reelArrowShownThisFrame = true;
    }

    // ─── 着水点の提示（キャンバスのマーカー）───────────────────

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
    /// 着水点の提示（キャンバスの着水点マーカー）を更新する。
    /// <see cref="FishState.Windup"/> のあいだ毎フレーム呼ぶ。
    ///
    /// 着水点は「いま投げたら落ちる点」（<see cref="PreviewDistance"/>＋<see cref="CastYawDegrees"/>）で、
    /// <see cref="StartCast"/> が実際に使う値と同じ計算経路を通す（見えているものと結果を必ず一致させる）。
    /// 方向が縮退している場合はマーカーを隠す。
    /// </summary>
    private void UpdateCastPreview()
    {
        if (CastYawDegrees() is not { } yaw) { HideCastPreview(); return; }

        UpdateCastMarker(LandingPoint(PreviewDistance(), yaw));
    }

    /// <summary>
    /// 釣り糸の点列を張り直す。
    ///
    /// ウキが外に出ている状態（Casting / Floating / Reeling / Result）のときだけ描く。
    /// Idle / Aiming / Windup ではウキが非表示になっているので線に意味が無く、非表示にする。
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
    /// 本体 Animator へ<b>いま指示してあるクリップ名</b>（ラッチ）。未指示・不明なら null。
    /// <see cref="SetPlayerClip"/> がここと比較して、同じクリップを毎フレーム
    /// CrossFade し直す（＝先頭へ戻り続ける）のを防ぐ。
    /// <see cref="PlayerMove"/> 側が本体アニメを触りうる区間の出入りでは
    /// <see cref="ResetPlayerClipLatch"/> で null に戻し、次の指示を必ず通す。
    /// </summary>
    private string? currentPlayerClip = null;

    /// <summary>
    /// プレイヤー本体のクリップ指示を一元化する窓口。
    /// 直前の指示と同じなら何もしない（横歩き⇔巻き取りの切替を毎フレーム出しても安全にする）。
    /// 本体側のクロスフェードはすべてこの関数を通す（竿側は従来どおり直接切り替える）。
    /// </summary>
    /// <param name="clip">再生したいクリップ名。</param>
    private void SetPlayerClip(string clip)
    {
        if (string.IsNullOrEmpty(clip)) { return; }
        if (currentPlayerClip == clip) { return; }

        currentPlayerClip = clip;
        CrossFadePlayer(clip);
    }

    /// <summary>
    /// 本体クリップのラッチを未指示（null）へ戻す。
    /// <see cref="PlayerMove"/> が本体アニメを差し替えうる区間（釣り姿勢の出入り）をまたぐと
    /// ラッチが実態とずれるため、その前後で必ず呼んで次の指示を通す。
    /// </summary>
    private void ResetPlayerClipLatch() => currentPlayerClip = null;

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
        SetPlayerClip(playerClip);   // 本体側は必ずラッチ経由（毎フレーム再指示を防ぐ）
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
            currentPlayerClip = playerCastClip;   // Play で直接指示したのでラッチも合わせる
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

        FinishWindupFor(rodAnimator, castClip, floatClip, continueToCast, isPlayerBody: false);
        FinishWindupFor(playerAnimator, playerCastClip, playerFloatClip, continueToCast, isPlayerBody: true);
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
    /// <param name="isPlayerBody">
    /// true … 本体 Animator（クリップ指示はラッチ <see cref="currentPlayerClip"/> を経由・更新する）。
    /// false … 竿 Animator（ラッチの対象外）。
    /// </param>
    private void FinishWindupFor(
        SEED.Animator? animator, string castClipName, string floatClipName, bool continueToCast, bool isPlayerBody)
    {
        if (animator is not { } anim || !anim.IsValid) { return; }

        // 速度 0 のスクラブ状態を必ず解除する
        anim.Speed = 1f;

        if (continueToCast)
        {
            if (anim.CurrentClip == castClipName)
            {
                anim.Resume();
                if (isPlayerBody) { currentPlayerClip = castClipName; }   // 継続再生もラッチへ反映
            }
            else if (isPlayerBody)
            {
                // 想定外: 予備動作中にクリップが崩れていた場合は通常のクロスフェードへフォールバック
                ResetPlayerClipLatch();
                SetPlayerClip(castClipName);
            }
            else
            {
                CrossFadeClip(anim, castClipName);
            }
        }
        else if (isPlayerBody)
        {
            SetPlayerClip(floatClipName);
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
