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
/// - 竿を振る … 着水後はいつでもマウスを振れる（<b>自由な竿振り</b>。詳細は下記）
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
/// <b>アタリと合わせ（どうぶつの森方式）</b>
/// 魚が餌へ届くと <see cref="BeginNibbling"/> で前アタリ（コツコツ）が 1〜4 回起き、
/// ウキが <see cref="nibbleDipDepth"/> だけ小さく沈む。撃ち切ってさらに 1 間隔経つと
/// 本アタリ（<see cref="biteDipDepth"/> の大きな沈み込み）と反応受付
/// （<see cref="FishState.HookWindow"/>）が始まる。<b>左クリック</b>する（合わせる）と
/// 反応時間から Excellent / Great / Nice / Miss を判定し、成功なら従来の
/// ヒット（<see cref="FishState.Hooked"/>）へ、失敗なら魚が逃げて待機へ戻る。
/// 判定は <see cref="LastJudgement"/> に残り、画面中央へ判定画像を出す。
/// アタリ〜合わせのあいだ巻き取り入力（ホイール・A/D）は受け付けない。
///
/// <b>合わせ＝左クリック（自由な竿振り／どうぶつの森方式の「いつでも振れる」）</b>
/// 餌が水にあるあいだ（<see cref="FishState.Floating"/> /
/// <see cref="FishState.Reeling"/> / <see cref="FishState.Nibbling"/> /
/// <see cref="FishState.HookWindow"/>）は、状態に関わらず
/// <see cref="UpdateSwingDetection"/> が<b>左クリックの押下</b>を読む
/// （マウスの振り上げ検出は廃止。押下フレームだけを見る単純な 1 クリック操作）。
/// 振りの<b>意味</b>だけが状態ごとに変わる:
/// - Floating / Reeling … <b>空振り</b>。ウキが竿先方向へ小さく跳ねる
///   （<see cref="hopSeconds"/> / <see cref="hopPullDistance"/> / <see cref="hopHeight"/>）。
///   跳ねているあいだ巻き取り入力（ホイール・A/D）は受け付けないが、糸は張られたまま。
/// - Nibbling … 早合わせ（<see cref="HookJudgement.Miss"/>。魚は逃げる）
/// - HookWindow … 合わせ判定（Excellent / Great / Nice / Miss）
/// - Hooked … 振りを読まない（ヒット中の竿振りは未仕様）
/// - ChainNibbling / ChainHookWindow … わらしべ連鎖の合わせ。意味は Nibbling / HookWindow と同じで、
///   成功すると掛かっている魚が食われて、より大きい魚へ乗り換わる
///
/// <b>わらしべ連鎖</b>
/// ヒット中（<see cref="FishState.Hooked"/>）は、掛かっている魚そのものが餌になる
/// （<see cref="HookedFishBaitActive"/>）。<see cref="Fish.CanPreyOn"/> でその魚を捕食できる
/// （＝明確に大きい）個体だけが寄って来て、通常とまったく同じ
/// 前アタリ → 本アタリ → 左クリックの合わせを行う。合わせに成功すると
/// <see cref="SwapHookedFish"/> で魚が乗り換わり（前の魚は食われて消滅）、失敗すると
/// 連鎖の魚だけが逃げて元のやり取りが再開する。連鎖中のやり取りは
/// <see cref="FishingFight.Paused"/> で凍結され、巻き取りも引きも起きない。
/// 乗り換えた後の魚がまた餌になるので、連鎖は何段でも続く。
/// どの状態で振っても <see cref="SwingSerial"/> が 1 増えるので、魚（<see cref="Fish"/>）は
/// これを見て「餌に寄っている最中に振られたら驚いて逃げる」反応を取る。
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
    /// Casting --着水--> Floating --巻き入力--> Reeling --手元まで--> Aiming（空振り）/ Catching
    /// Floating/Reeling --魚が BeginNibbling--> Nibbling（前アタリ）
    /// Nibbling --前アタリを撃ち切り＋1 間隔--> HookWindow（本アタリ・反応受付）
    /// Nibbling --早すぎる合わせ--> Floating（Miss。魚は逃げる）
    /// Floating/Reeling --竿を振る（アタリ無し）--> 同じ状態のままウキが跳ねる（空振り）
    /// HookWindow --niceSeconds 以内に合わせ--> Hooked（Excellent/Great/Nice）
    /// HookWindow --遅い合わせ／時間切れ--> Floating（Miss。魚は逃げる）
    /// Aiming（巻き取り後）--左クリックを離していれば即--> Idle
    ///
    /// ── わらしべ連鎖（掛かっている魚を餌に、より大きい魚が食いに来る）──
    /// Hooked          --より大きい魚が BeginNibbling--> ChainNibbling（やり取りは一時停止）
    /// ChainNibbling   --前アタリを撃ち切り＋1 間隔--> ChainHookWindow
    /// ChainNibbling   --早すぎる合わせ--> Hooked（Miss。連鎖の魚だけ逃げる）
    /// ChainHookWindow --niceSeconds 以内に合わせ--> Hooked（魚が乗り換わる。前の魚は食われて消滅）
    /// ChainHookWindow --遅い合わせ／時間切れ--> Hooked（Miss。連鎖の魚だけ逃げる）
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

        /// <summary>
        /// 着水後。ウキが水面で待機している（アタリ待ち）。
        /// この状態で竿を振ると空振りになり、ウキが手前へ小さく跳ねる
        /// （跳ねているあいだだけ巻き取り入力を受け付けない）。
        /// </summary>
        Floating,

        /// <summary>
        /// 前アタリ（コツコツ）の最中。魚が餌をつついてウキが小さく沈む。
        /// <b>まだ食っていない</b>ので、ここで合わせると早合わせ＝<see cref="HookJudgement.Miss"/>。
        /// この状態のあいだ巻き取り入力（ホイール・A/D）は一切受け付けない。
        /// </summary>
        Nibbling,

        /// <summary>
        /// 本アタリ（大きくウキが沈んだ）直後の反応受付。
        /// <see cref="niceSeconds"/> 以内にマウスを振る（合わせる）と反応時間で
        /// Excellent / Great / Nice を判定してヒットへ移る。時間切れは Miss。
        /// この状態のあいだ巻き取り入力（ホイール・A/D）は一切受け付けない。
        /// </summary>
        HookWindow,

        /// <summary>
        /// 巻き取り中。ウキが手前へ寄り、プレイヤーもウキの方へ歩く。
        /// <see cref="Floating"/> と同じく竿を振れて、振れば空振りの跳ねが入る。
        /// </summary>
        Reeling,

        /// <summary>
        /// 魚が食いついている（ヒット中）。
        /// 巻き取りの操作は <see cref="Reeling"/> と完全に同じで、
        /// ウキが <c>食いつき時のウキ沈み量</c> だけ沈み、掛かった魚がウキに追従する。
        /// 手元まで巻き切ると <see cref="Catching"/>（釣り上げ演出）へ遷移する。
        /// 糸のテンション／HP は未実装（釣り仕様の後続タスク）。
        /// </summary>
        Hooked,

        /// <summary>
        /// 釣り上げ演出中。進行は <see cref="CatchPresenter"/> が握り、本スクリプトは
        /// 毎フレーム <see cref="CatchPresenter.Tick"/> を呼ぶだけになる
        /// （カメラ寄り → ホワイトアウト → 魚のポップ／釣果表示 → クリックで閉じる）。
        /// 演出が終わる（<see cref="CatchPresenter.Phase"/> が
        /// <see cref="CatchPresenter.CatchPhase.None"/> に戻る）と、釣り姿勢を解いて
        /// <see cref="Idle"/>（移動）へ復帰する。
        /// </summary>
        Catching,

        /// <summary>
        /// <b>わらしべ連鎖</b>の前アタリ。掛かっている魚（<see cref="Hooked"/> の獲物）を餌として、
        /// より大きい魚がつつきに来ている状態。<see cref="Nibbling"/> の完全な写しで、
        /// 違いは「失敗しても <see cref="Floating"/> ではなく <see cref="Hooked"/> へ戻る」ことだけ。
        ///
        /// このあいだ、やり取り（<see cref="FishingFight"/>）は
        /// <see cref="FishingFight.Paused"/> で凍結され、巻き取り入力も一切受け付けない。
        /// ウキ・掛かっている魚はその場に留まり、連鎖の魚はウキの下へ寄ってくる。
        /// </summary>
        ChainNibbling,

        /// <summary>
        /// <b>わらしべ連鎖</b>の本アタリ（反応受付）。<see cref="HookWindow"/> の写しで、
        /// 成功すると掛かっている魚が食われて<b>より大きい魚へ乗り換わる</b>
        /// （<see cref="SwapHookedFish"/>）。失敗・時間切れなら連鎖の魚だけが逃げて
        /// <see cref="Hooked"/>（元の魚とのやり取り）へ戻る。
        /// </summary>
        ChainHookWindow,
    }

    /// <summary>
    /// いま魚を引き寄せている「餌」の種類。魚（<see cref="Fish"/>）が
    /// 何を狙って寄るかの判断に使う。
    /// <see cref="FishState"/> と同じ理由で本クラスの入れ子として定義する。
    /// </summary>
    public enum BaitKind
    {
        /// <summary>餌なし（キャスト前・飛翔中・連鎖のアタリ受付中など）。</summary>
        None,

        /// <summary>ルアー（ウキ）。通常の釣り。</summary>
        Lure,

        /// <summary>掛かっている魚そのもの（わらしべ連鎖）。</summary>
        HookedFish,
    }

    /// <summary>
    /// 合わせ（フッキング）の判定結果。
    /// 反応時間が短いほど良い評価になり、糸 HP ボーナスなどの後続仕様で参照する。
    /// <see cref="FishState"/> と同じ理由で本クラスの入れ子として定義する
    /// （外部からは <c>FishingController.HookJudgement</c>）。
    /// </summary>
    public enum HookJudgement
    {
        /// <summary>未判定（まだ 1 度も合わせていない／表示なし）。</summary>
        None,

        /// <summary>最速の合わせ（<see cref="excellentSeconds"/> 以内）。</summary>
        Excellent,

        /// <summary>速い合わせ（<see cref="greatSeconds"/> 以内）。</summary>
        Great,

        /// <summary>間に合った合わせ（<see cref="niceSeconds"/> 以内）。</summary>
        Nice,

        /// <summary>早合わせ・遅すぎ・時間切れ（魚は逃げる）。</summary>
        Miss,
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

    /// <summary>
    /// ヒット中のやり取りのフェーズ（<see cref="FishingFight.CurrentPhase"/> の中継）。
    ///
    /// カメラ（<c>CameraMove</c>）が「回答中だけ構図を切り替える」判断に使う。
    /// やり取りスクリプトが未設定なら常に <see cref="FishingFight.Phase.None"/>。
    /// </summary>
    public FishingFight.Phase FightPhase
        => fight is { } f ? f.CurrentPhase : FishingFight.Phase.None;

    /// <summary>
    /// 釣り上げ演出のフェーズ（<see cref="CameraMove"/> がカメラ目標の切替に使う）。
    /// プレゼンタ未設定・演出していないときは <see cref="CatchPresenter.CatchPhase.None"/>。
    /// 参照スクリプトは毎フレーム見に行く（ホットリロードで実インスタンスが差し替わるため）。
    /// </summary>
    public CatchPresenter.CatchPhase CatchPhase
        => presenter is { } p ? p.Phase : CatchPresenter.CatchPhase.None;

    /// <summary>
    /// 直近の合わせ判定（糸 HP ボーナスなど後続仕様のための公開値）。
    /// 前アタリ開始時に <see cref="HookJudgement.None"/> へ戻し、合わせの成否で確定する。
    /// </summary>
    public HookJudgement LastJudgement { get; private set; } = HookJudgement.None;

    /// <summary>
    /// 竿を振った回数の通し番号【魚が「振られた」ことを知る唯一の手掛かり】。
    ///
    /// <see cref="UpdateSwingDetection"/> が左クリックの押下を検出するたびに、どの状態
    /// （Floating / Reeling / Nibbling / HookWindow）でも 1 だけ増える。
    /// 魚（<see cref="Fish"/>）は前フレームに見た番号を覚えておき、値が変わっていたら
    /// 「竿が振られた」と判断する（イベント購読の仕組みが無いためのポーリング方式。
    /// 番号なので取りこぼしても「変わった」ことだけは必ず伝わる）。
    /// </summary>
    public int SwingSerial { get; private set; } = 0;

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

    /// <summary>「ウキが沈んでいない」ことを表す沈みタイマーの番人値（負値＝無効）。</summary>
    private const float NoDipElapsed = -1f;

    /// <summary>判定表示（画像・ヒント）を出しているときの不透明度。</summary>
    private const float JudgeVisibleOpacity = 1f;

    /// <summary>SEED.Random.Range(int, int) の上限は排他なので、回数の上限に足す 1。</summary>
    private const int InclusiveUpperBound = 1;

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
    /// 着水後（Floating / Reeling / Nibbling / HookWindow）は合わせが左クリック 1 回になり
    /// マウスの移動量を読まなくなったため、ロックする理由が無い（カーソルは出したまま）。
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
    /// <b>わらしべ連鎖</b>の餌（＝掛かっている魚）の影響半径（メートル）。
    /// 意味は <see cref="baitInfluenceRadius"/> と同じで、掛かった魚を餌と見なすときに使う。
    /// ルアーとは別に調整できるよう独立したパラメータにしてある（既定値は同じ）。
    /// </summary>
    [SerializeField(Label = "連鎖餌の影響半径(m)")]
    private float chainInfluenceRadius = 2f;

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

    // ─── アタリ（前アタリ〜本アタリ）───────────────────────────

    /// <summary>前アタリ（コツコツ）の回数の下限。実回数はこの範囲の一様乱数。</summary>
    [Header("アタリ"), SerializeField(Label = "前アタリの最小回数")]
    private int nibbleCountMin = 1;

    /// <summary>前アタリの回数の上限（この値を含む）。</summary>
    [SerializeField(Label = "前アタリの最大回数")]
    private int nibbleCountMax = 4;

    /// <summary>前アタリ 1 回ごとの間隔の下限（秒）。本アタリまでの間隔にも使う。</summary>
    [SerializeField(Label = "アタリ間隔の最小(秒)")]
    private float nibbleIntervalMin = 0.6f;

    /// <summary>前アタリ 1 回ごとの間隔の上限（秒）。</summary>
    [SerializeField(Label = "アタリ間隔の最大(秒)")]
    private float nibbleIntervalMax = 1.6f;

    /// <summary>前アタリ 1 回でウキが沈む量（メートル）。本アタリより小さくする。</summary>
    [SerializeField(Label = "前アタリのウキ沈み量(m)")]
    private float nibbleDipDepth = 0.08f;

    /// <summary>前アタリ 1 回の沈み〜浮き上がりに掛ける秒数（山なりに沈んで戻る）。</summary>
    [SerializeField(Label = "前アタリの沈み時間(秒)")]
    private float nibbleDipSeconds = 0.25f;

    // ─── 空振り（ウキの跳ね）─────────────────────────────────
    //
    // 「まだ魚がアタっていない」状態（Floating / Reeling）で竿を振ったときの演出。
    // 竿は<b>いつでも</b>振れる（＝左クリック 1 回でいつでも合わせられる）ので、
    // アタリが無いときの振りはウキが手前へ小さく跳ねるだけの空振りになる。
    //
    // 合わせの操作は「左クリックの押下」そのものなので、しきい値・時間窓・
    // クールダウンといった調整パラメータは持たない（旧フリック検出は廃止）。

    /// <summary>ウキの跳ね 1 回に掛ける秒数。この間は巻き取り入力（ホイール・A/D）を受け付けない。</summary>
    [Header("合わせ"), SerializeField(Label = "ウキの跳ね時間(秒)")]
    private float hopSeconds = 0.35f;

    /// <summary>
    /// ウキの跳ねで竿先方向へ引き寄せる水平距離（メートル）。
    /// 「竿先までの残り距離 −<see cref="reelEndDistance"/>」でクランプするので、
    /// 跳ねだけで巻き取りが完了してしまうことはない。
    /// </summary>
    [SerializeField(Label = "ウキの跳ねの引き寄せ距離(m)")]
    private float hopPullDistance = 1f;

    /// <summary>ウキの跳ねの最高到達高さ（メートル、水面からの相対）。放物線 4h·t·(1−t) の h。</summary>
    [SerializeField(Label = "ウキの跳ねの高さ(m)")]
    private float hopHeight = 0.4f;

    /// <summary>Excellent と判定される反応時間の上限（秒）。</summary>
    [SerializeField(Label = "Excellent の反応時間(秒)")]
    private float excellentSeconds = 0.25f;

    /// <summary>Great と判定される反応時間の上限（秒）。</summary>
    [SerializeField(Label = "Great の反応時間(秒)")]
    private float greatSeconds = 0.5f;

    /// <summary>Nice と判定される反応時間の上限（秒）。＝反応受付そのものの制限時間。</summary>
    [SerializeField(Label = "Nice の反応時間(秒)")]
    private float niceSeconds = 0.9f;

    // ─── 判定表示（スクリーンスペース UI）─────────────────────

    /// <summary>判定画像を表示し続ける秒数。</summary>
    [Header("判定表示"), SerializeField(Label = "判定表示の秒数")]
    private float judgeShowSeconds = 1.2f;

    /// <summary>判定画像の「ポップ」（拡大から等倍へ戻る）に掛ける秒数。0 でポップ無し。</summary>
    [SerializeField(Label = "判定ポップの秒数")]
    private float judgePopSeconds = 0.15f;

    /// <summary>判定画像のポップ開始倍率（この倍率から 1.0 へ縮む）。</summary>
    [SerializeField(Label = "判定ポップの拡大率")]
    private float judgePopScale = 1.2f;

    /// <summary>Excellent 判定の画像（スクリーンスペースキャンバスの子スプライト）。</summary>
    [SerializeField(Label = "判定Sprite(Excellent)")]
    private SEED.Sprite? judgeExcellentSprite = null;

    /// <summary>Great 判定の画像。</summary>
    [SerializeField(Label = "判定Sprite(Great)")]
    private SEED.Sprite? judgeGreatSprite = null;

    /// <summary>Nice 判定の画像。</summary>
    [SerializeField(Label = "判定Sprite(Nice)")]
    private SEED.Sprite? judgeNiceSprite = null;

    /// <summary>Miss 判定の画像。</summary>
    [SerializeField(Label = "判定Sprite(Miss)")]
    private SEED.Sprite? judgeMissSprite = null;

    /// <summary>
    /// 判定画像に添える「早い／遅い」のヒント（<c>JudgeHint</c> の Text を割り当てる）。
    ///
    /// リズムのやり取り（<see cref="FishingFight"/>）が
    /// <see cref="ShowFightJudgement"/> で書き込む。表示・非表示は判定画像と完全に同期し、
    /// 合わせ（HookWindow）の判定では常に空文字になる（＝ヒントを出さない）。
    /// </summary>
    [SerializeField(Label = "判定ヒントのText(早い/遅い)")]
    private SEED.Text? judgeHintText = null;

    /// <summary>「早い（打点より前に叩いた）」ときにヒントへ出す文言。</summary>
    [SerializeField(Label = "ヒントの文言(早い)")]
    private string judgeHintEarly = "早い";

    /// <summary>「遅い（打点より後に叩いた）」ときにヒントへ出す文言。</summary>
    [SerializeField(Label = "ヒントの文言(遅い)")]
    private string judgeHintLate = "遅い";

    /// <summary>
    /// 釣り上げ演出（<see cref="FishState.Catching"/>）を進行させるプレゼンタ。
    /// 同じアクタ（プレイヤー）に 2 本目のスクリプトスロット「Catch」として置き、ここへ割り当てる。
    ///
    /// <b>未設定でも釣りは成立する</b>: 演出を飛ばして魚を消し、そのまま移動へ戻る
    /// （<see cref="FinishReeling"/> のフォールバック）。
    /// </summary>
    [SerializeField(Label = "釣り上げ演出(CatchPresenter)")]
    private CatchPresenter? presenter = null;

    /// <summary>
    /// ヒット中のやり取り（テンションゲージ・糸 HP）を司るスクリプト。
    /// 同じアクタ（プレイヤー）に 3 本目のスクリプトスロット「Fight」として置き、ここへ割り当てる。
    ///
    /// <b>未設定でも釣りは成立する</b>: 糸は切れず、魚もウキを引かない
    /// （＝従来どおり巻けば必ず釣れる）。
    /// </summary>
    [SerializeField(Label = "釣りバトル(FishingFight)")]
    private FishingFight? fight = null;

    /// <summary>
    /// 魚がウキを沖へ引ける限界の、最長飛距離からの余裕（メートル）。
    /// 竿先からの水平距離が <see cref="maxCastDistance"/> ＋ この値を超えないようにする
    /// （魚に無限に引かれてウキが世界の外へ行かないための安全弁）。
    /// </summary>
    [SerializeField(Label = "引きの限界余裕(m)")]
    private float floatDragMarginDistance = 5f;

    // ─── 効果音 ─────────────────────────────

    /// <summary>竿を振ってキャストを開始した瞬間（<see cref="StartCast"/>）に鳴らす効果音のアセットパス。空文字なら鳴らさない。</summary>
    [Header("効果音")]
    [SerializeField(Label = "キャストの効果音")]
    private string castSePath = "assets://mainGame/audios/Motion-Swish07-1.mp3";

    /// <summary>キャスト効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "キャストの音量")]
    private float castSeVolume = 1f;

    /// <summary>竿を引いて構えた瞬間（<see cref="EnterWindup"/>、投げる前の振りかぶり）に鳴らす擦れ音のアセットパス。空文字なら鳴らさない。</summary>
    [SerializeField(Label = "構え（引き）の効果音")]
    private string windupSePath = "assets://mainGame/audios/kosure.mp3";

    /// <summary>構え効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "構えの音量")]
    private float windupSeVolume = 1f;

    /// <summary>ウキが着水した瞬間（<see cref="UpdateFlight"/> で Casting → Floating へ遷移する瞬間）に鳴らす効果音のアセットパス。空文字なら鳴らさない。</summary>
    [SerializeField(Label = "着水の効果音")]
    private string splashSePath = "assets://mainGame/audios/sei_ge_mizu_chapon06.mp3";

    /// <summary>着水効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "着水の音量")]
    private float splashSeVolume = 1f;

    /// <summary>前アタリ（ウキが小さく沈む瞬間）に鳴らす効果音のアセットパス。空文字なら鳴らさない。</summary>
    [SerializeField(Label = "前アタリの効果音")]
    private string nibbleSePath = "assets://mainGame/audios/tstsuki.mp3";

    /// <summary>前アタリ効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "前アタリの音量")]
    private float nibbleSeVolume = 1f;

    /// <summary>本アタリ（<see cref="FishState.HookWindow"/> 開始）に鳴らす効果音のアセットパス。空文字なら鳴らさない。</summary>
    [SerializeField(Label = "本アタリの効果音")]
    private string hookSePath = "assets://mainGame/audios/hit.mp3";

    /// <summary>本アタリ効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "本アタリの音量")]
    private float hookSeVolume = 1f;

    /// <summary>
    /// 竿を振った瞬間（<see cref="UpdateSwingDetection"/> が左クリックを拾った瞬間）に鳴らす
    /// 効果音のアセットパス。空文字なら鳴らさない。振りはどの状態
    /// （Floating / Reeling / Nibbling / HookWindow）でも同じ 1 か所で検出するので、
    /// 効果音もそこ 1 か所から鳴らす。
    /// </summary>
    [SerializeField(Label = "竿振りの効果音")]
    private string swingSePath = "";

    /// <summary>竿振り効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "竿振りの音量")]
    private float swingSeVolume = 1f;

    /// <summary>
    /// 現在掛かっている魚（null = 掛かっていない）。
    /// <see cref="TryHook"/> で束縛し、釣り上げ・リリース・キャンセルで必ず解除する。
    /// </summary>
    private Fish? hookedFish = null;

    // ─── アタリ／合わせの内部状態 ─────────────────────────────

    /// <summary>いま前アタリ〜本アタリを起こしている魚（null = アタリ進行中でない）。</summary>
    private Fish? nibblingFish = null;

    /// <summary>
    /// <b>わらしべ連鎖</b>で、掛かっている魚（<see cref="hookedFish"/>）をつつきに来ている魚
    /// （null = 連鎖のアタリ進行中でない）。同時に 1 匹だけ受け付ける。
    ///
    /// これが非 null のあいだは <see cref="FishState.ChainNibbling"/> /
    /// <see cref="FishState.ChainHookWindow"/> で、やり取りは一時停止している。
    /// 合わせ成功で <see cref="SwapHookedFish"/> により <see cref="hookedFish"/> へ昇格し、
    /// 失敗・中断では <see cref="ReleaseChainNibbler"/> で逃がす。
    /// </summary>
    private Fish? chainNibbler = null;

    /// <summary>残りの前アタリ回数。0 になった次の間隔で本アタリへ移る。</summary>
    private int nibbleRemaining = 0;

    /// <summary>次のアタリ（前アタリ or 本アタリ）までの残り秒数。</summary>
    private float nibbleTimer = 0f;

    /// <summary>
    /// 前アタリの沈みアニメの経過秒数。<see cref="NoDipElapsed"/> のあいだは沈んでいない。
    /// <see cref="nibbleDipSeconds"/> を超えたら無効値へ戻す。
    /// </summary>
    private float nibbleDipElapsed = NoDipElapsed;

    /// <summary>本アタリからの経過秒数（＝合わせの反応時間）。</summary>
    private float reactionElapsed = 0f;

    /// <summary>ウキの跳ね（空振り演出）を再生中か。true のあいだ <see cref="UpdateReeling"/> は走らせない。</summary>
    private bool hopActive = false;

    /// <summary>ウキの跳ねの経過秒数（0〜<see cref="hopSeconds"/>）。</summary>
    private float hopElapsed = 0f;

    /// <summary>跳ね開始時のウキの水平位置（Y は使わない）。</summary>
    private SEED.Vector3 hopStart = SEED.Vector3.Zero;

    /// <summary>跳ね終了時のウキの水平位置（竿先方向へ <see cref="hopPullDistance"/> だけ寄せた点。Y は使わない）。</summary>
    private SEED.Vector3 hopEnd = SEED.Vector3.Zero;

    /// <summary>いま表示している判定（<see cref="HookJudgement.None"/> = 非表示）。</summary>
    private HookJudgement judgeDisplay = HookJudgement.None;

    /// <summary>判定画像を表示し始めてからの経過秒数。</summary>
    private float judgeElapsed = 0f;

    /// <summary>
    /// いま表示している判定に添えるヒント（"早い" / "遅い"。空文字 ＝ ヒント無し）。
    /// リズムのやり取りの判定（<see cref="ShowFightJudgement"/>）だけが入れる。
    /// </summary>
    private string judgeHint = "";

    /// <summary>
    /// 表示中の判定スプライトの元サイズ（ポップで書き換える前の値）。
    /// null = ポップ中でない。非表示にするとき必ずこの値へ戻す。
    /// </summary>
    private SEED.Vector2? judgeBaseSize = null;

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
        // 判定画像は 4 枚ともアルファ 0（非表示）から始める。
        HideJudgement();
    }

    /// <summary>
    /// 破棄直前の後始末。掛かっている魚を解放し、静的アクセサの参照を落とす。
    /// 別インスタンスが既に登録済みなら上書きしない（自分の分だけ取り消す）。
    /// </summary>
    public override void OnDestroy()
    {
        AbortBiteTiming();
        ReleaseHook();
        fight?.EndFight();
        engagedFish.Clear();
        if (ReferenceEquals(Current, this)) { Current = null; }
    }

    // ─── 魚から参照する公開 API ───────────────────────────────

    /// <summary>
    /// 餌（ウキ）が水中にあって、魚が食いつける状態か。
    /// 着水後の待機（<see cref="FishState.Floating"/>）・巻き取り中
    /// （<see cref="FishState.Reeling"/>）・アタリ中（<see cref="FishState.Nibbling"/> /
    /// <see cref="FishState.HookWindow"/>）が対象で、飛翔中やキャスト前は false。
    /// </summary>
    public bool BaitActive
        => State is FishState.Floating or FishState.Reeling
                 or FishState.Nibbling or FishState.HookWindow
        && uki is { IsValid: true };

    /// <summary>餌（ウキ）のワールド位置。<see cref="BaitActive"/> が false のときの値は無意味。</summary>
    public SEED.Vector3 BaitPosition
        => uki is { IsValid: true } floatTf ? floatTf.Position : SEED.Vector3.Zero;

    /// <summary>餌の影響半径（メートル）。魚の感知距離に加算される。</summary>
    public float BaitInfluenceRadius => baitInfluenceRadius;

    /// <summary>わらしべ連鎖の餌（＝掛かっている魚）の影響半径（メートル）。</summary>
    public float ChainInfluenceRadius => chainInfluenceRadius;

    /// <summary>食いつき距離（メートル）。ルアー・わらしべ連鎖のどちらでも共通。</summary>
    public float BiteDistance => biteDistance;

    /// <summary>いま魚が掛かっているか。</summary>
    public bool IsHooked => hookedFish is not null;

    // ─── わらしべ連鎖（掛かっている魚が次の餌になる）───────────

    /// <summary>
    /// <b>掛かっている魚が餌として有効か</b>【連鎖の成立条件の唯一の定義】。
    ///
    /// ヒット中（<see cref="FishState.Hooked"/>）で魚が掛かっており、かつ
    /// まだ誰も連鎖のアタリを起こしていない（<see cref="chainNibbler"/> が null）とき。
    /// 連鎖のアタリ中（ChainNibbling / ChainHookWindow）に false になるので、
    /// 2 匹目・3 匹目が同時に寄ってくることはない。
    /// </summary>
    public bool HookedFishBaitActive
        => State == FishState.Hooked && hookedFish is not null && chainNibbler is null;

    /// <summary>
    /// 餌になっている「掛かっている魚」（null = 連鎖の餌なし）。
    /// 魚はこれに対して <see cref="Fish.CanPreyOn"/> で捕食可否を判断する。
    /// </summary>
    public Fish? HookedFishBait => HookedFishBaitActive ? hookedFish : null;

    /// <summary>
    /// 連鎖の餌（掛かっている魚）のワールド位置。
    /// <see cref="HookedFishBaitActive"/> が false のときの値は無意味。
    /// </summary>
    public SEED.Vector3 HookedFishBaitPosition
        => hookedFish is { } fish && fish.Transform is { IsValid: true } t
            ? t.Position
            : SEED.Vector3.Zero;

    /// <summary>
    /// いま魚を引き寄せている餌の種類【餌の種類の唯一の判定点】。
    /// ルアーが優先で、ルアーが無効なときだけ連鎖の餌を見る。
    /// </summary>
    public BaitKind CurrentBaitKind
        => BaitActive ? BaitKind.Lure
         : HookedFishBaitActive ? BaitKind.HookedFish
         : BaitKind.None;

    /// <summary>
    /// 指定の魚が、いまアタリの主（通常の前アタリ or 連鎖の前アタリ）として
    /// コントローラに保持されているか。
    ///
    /// 魚側（<see cref="Fish.BehaviorState.Nibbling"/>）が「自分はまだつつき中か」を
    /// 確かめるために使う。状態の列挙（BaitActive など）で代用すると連鎖の状態を
    /// 取りこぼすので、保持している参照そのもので判定する。
    /// </summary>
    /// <param name="fish">確かめる魚。</param>
    /// <returns>アタリの主なら true。</returns>
    public bool IsNibbling(Fish fish)
        => ReferenceEquals(nibblingFish, fish) || ReferenceEquals(chainNibbler, fish);

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
        // やり取り（テンションゲージ・糸 HP・魚 HP）を開始する。
        // 初期ゲージは直前に確定した合わせ判定（LastJudgement）で決まり、
        // 掛かった瞬間の距離は「魚 HP 1 あたりの距離」の基準になる。
        fight?.BeginFight(fish, LastJudgement, CurrentFloatDistance());
        SEED.Debug.Log($"[Fishing] ヒット! {fish.DisplayName}（大きさ {fish.Size:F2}）");
        return true;
    }

    /// <summary>
    /// 魚が餌をつつき始めた（前アタリ開始）ときに呼ぶ。受け付けたら true。
    ///
    /// 餌が無効・既に別の魚が掛かっている／つついている場合は false を返す
    /// （呼んだ側は興味を失って回遊へ戻る）。成立すると前アタリの回数と間隔を抽選し、
    /// <see cref="FishState.Nibbling"/> へ遷移してウキが小さく沈み始める。
    /// </summary>
    /// <param name="fish">つつき始めた魚。</param>
    /// <returns>前アタリを受け付けたら true。</returns>
    public bool BeginNibbling(Fish fish)
    {
        // ── わらしべ連鎖 ──
        // ヒット中は、掛かっている魚そのものが餌になる。より大きい魚が食いに来た場合だけ受け付ける。
        if (State == FishState.Hooked) { return BeginChainNibbling(fish); }

        // 餌が水にあり、かつ「まだ誰もアタっていない」ときだけ受け付ける
        if (State is not (FishState.Floating or FishState.Reeling)) { return false; }
        if (!BaitActive || IsHooked || nibblingFish is not null) { return false; }

        nibblingFish = fish;
        State = FishState.Nibbling;
        LastJudgement = HookJudgement.None;
        CancelHop();                   // 跳ねの最中に前アタリが始まったら跳ねを打ち切る
        RollNibbleSequence();

        SEED.Debug.Log($"[Fishing] 前アタリ開始: {fish.DisplayName}（{nibbleRemaining} 回）");
        return true;
    }

    /// <summary>
    /// <b>わらしべ連鎖</b>の前アタリを開始する【連鎖開始の唯一の入口】。
    ///
    /// 成立条件は「ヒット中」「連鎖の魚がまだ居ない」「掛かっている魚を
    /// <see cref="Fish.CanPreyOn"/> で捕食できる」の 3 つ。
    /// 成立すると <see cref="FishState.ChainNibbling"/> へ移り、
    /// やり取り（<see cref="FishingFight"/>）を <see cref="FishingFight.Paused"/> で凍結する。
    /// 以後の進行（前アタリ → 本アタリ → 合わせ判定）は通常のアタリと完全に同じ経路
    /// （<see cref="UpdateBiteTiming"/>）を通る。
    /// </summary>
    /// <param name="fish">掛かっている魚を狙って来た魚。</param>
    /// <returns>連鎖の前アタリを受け付けたら true。</returns>
    private bool BeginChainNibbling(Fish fish)
    {
        if (hookedFish is not { } prey) { return false; }
        if (chainNibbler is not null) { return false; }         // 連鎖の魚は同時に 1 匹だけ
        if (ReferenceEquals(fish, prey)) { return false; }      // 自分自身は食えない
        if (!fish.CanPreyOn(prey)) { return false; }            // 明確に大きい魚だけが食いに来る

        chainNibbler = fish;
        State = FishState.ChainNibbling;
        LastJudgement = HookJudgement.None;
        CancelHop();                   // ヒット中は跳ねないが、フラグの持ち越しを防ぐため必ず畳む
        RollNibbleSequence();

        // やり取りを凍結する（ゲージ・糸 HP・スタミナ・引きは進まず、UI だけ出したまま）
        if (fight is { } f) { f.Paused = true; }

        SEED.Debug.Log(
            $"[Fishing] わらしべ前アタリ開始: {fish.DisplayName}（{fish.DisplaySize:F1}）が"
          + $" {prey.DisplayName}（{prey.DisplaySize:F1}）を狙う（{nibbleRemaining} 回）");
        return true;
    }

    /// <summary>
    /// 前アタリの回数と最初の間隔を抽選し、アタリ用のタイマ類を初期化する
    /// （通常のアタリと連鎖のアタリで共通）。
    /// </summary>
    private void RollNibbleSequence()
    {
        // 前アタリの回数（下限〜上限、上限を含む）と最初の間隔を抽選する
        int minCount = SEED.Mathf.Max(0, nibbleCountMin);
        int maxCount = SEED.Mathf.Max(minCount, nibbleCountMax);
        nibbleRemaining = SEED.Random.Range(minCount, maxCount + InclusiveUpperBound);
        nibbleTimer = NextNibbleInterval();
        nibbleDipElapsed = NoDipElapsed;
        reactionElapsed = 0f;
    }

    /// <summary>
    /// 連鎖の魚を逃がす【連鎖の魚の解放の唯一の出口】。居なければ何もしない。
    /// 状態は変えない（呼び出し側が <see cref="FishState.Hooked"/> などへ遷移させる）。
    /// </summary>
    private void ReleaseChainNibbler()
    {
        if (chainNibbler is not { } fish) { return; }

        chainNibbler = null;
        fish.ReleaseFromHook();
    }

    /// <summary>
    /// 掛かっている魚を、連鎖で食いついた魚へ<b>乗り換える</b>【わらしべ成立の唯一の出口】。
    ///
    /// 元の魚は食われた扱いで破棄する（<see cref="FishManager"/> は生存チェックで
    /// 欠けた枠を自動的に補充するので、個体数は勝手に戻る）。
    /// 新しい魚を掛け直し、やり取りは一度畳んでから新しい魚のパラメータで開始する
    /// （＝ゲージ・糸 HP・スタミナは新しい魚基準でリセットされ、
    ///   初期ゲージは今回の合わせランクで決まる）。
    /// </summary>
    /// <param name="newFish">新しく掛かる魚（連鎖でアタっていた魚）。</param>
    /// <param name="judgement">今回の合わせ判定（新しいやり取りの初期ゲージに使う）。</param>
    private void SwapHookedFish(Fish newFish, HookJudgement judgement)
    {
        if (hookedFish is not { } eaten) { return; }

        SEED.Debug.Log(
            $"[Fishing] わらしべ成立: {newFish.DisplayName}（{newFish.DisplaySize:F1}）が"
          + $" {eaten.DisplayName}（{eaten.DisplaySize:F1}）を食べた");

        // 食われた魚: AI を止め、円環クランプの除外登録を外してから破棄する
        // （登録解除は破棄より前に行う。破棄処理中のシーンアクセスは保証されないため）
        eaten.OnCaught();
        UnregisterEngaged(eaten.Actor);
        eaten.Actor.Destroy();

        // 新しい魚を掛け直す
        hookedFish = newFish;
        chainNibbler = null;
        newFish.OnHooked();
        State = FishState.Hooked;

        // やり取りを畳んでから、新しい魚で開始し直す（Paused も EndFight で解除される）
        if (fight is { } f)
        {
            f.EndFight();
            f.BeginFight(newFish, judgement, CurrentFloatDistance());
        }

        // ヒット用クリップを引き直し（既に同じクリップならラッチで間引かれる）、カーソルロックも同期
        CrossFadeBoth(hookedClip, playerHookedClip);
        UpdateCursorLock();
    }

    /// <summary>
    /// 連鎖のアタリを畳んで、元の魚とのやり取り（<see cref="FishState.Hooked"/>）へ戻す
    /// 【連鎖失敗時の唯一の出口】。
    ///
    /// 掛かっている魚が何らかの理由で失われていた場合は、ヒット中を続けられないので
    /// <see cref="FishState.Floating"/>（餌だけが浮いている状態）へ落とす。
    /// </summary>
    private void ResumeHookedFromChain()
    {
        chainNibbler = null;
        if (fight is { } f) { f.Paused = false; }

        if (!IsHooked)
        {
            // 異常系: 掛かっていた魚が消えている。ヒット中を続けられないので待機へ落とす。
            State = FishState.Floating;
            return;
        }

        State = FishState.Hooked;
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

        // 判定画像の表示は釣り状態に依らず進める（早期 return の経路でも必ず消える）。
        UpdateJudgementUi(ctx.DeltaTime);

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
                // 餌が水にあるあいだは、アタリが無くても竿を振れる（自由な竿振り）。
                // 振ればウキが手前へ小さく跳ねる空振りになり、跳ねているあいだは
                // 巻き取り入力（ホイール・A/D）を受け付けない。
                if (UpdateSwingDetection()) { TryStartHop(); }

                if (hopActive) { UpdateHop(ctx.DeltaTime); }
                else { UpdateReeling(ctx.DeltaTime); }
                break;

            case FishState.Hooked:
                // ヒット中も巻き取りの操作系（ホイール・A/D 操舵）はまったく同じ。
                // ただし竿振りは読まない（ヒット中の振りは未仕様）ので跳ねもしない。
                //
                // 先にやり取り（テンションゲージ・糸 HP）を 1 フレーム進める。
                // 糸が切れたらこのフレームは巻き取りへ進まず、糸切れ処理で締める。
                UpdateFight(ctx.DeltaTime);
                if (State != FishState.Hooked) { break; }
                UpdateReeling(ctx.DeltaTime);
                break;

            case FishState.Nibbling:
            case FishState.HookWindow:
                // アタリ〜合わせの受付中。巻き取り入力（ホイール・A/D）は一切見ない。
                UpdateBiteTiming(ctx.DeltaTime);
                break;

            case FishState.ChainNibbling:
            case FishState.ChainHookWindow:
                // わらしべ連鎖のアタリ受付中。進行は通常のアタリとまったく同じ経路を通る。
                // やり取りは FishingFight.Paused で凍結されているが、UI（ゲージ・糸 HP・
                // 残り距離）は出したままにしたいので UpdateFight は毎フレーム呼ぶ。
                UpdateFight(ctx.DeltaTime);
                UpdateBiteTiming(ctx.DeltaTime);
                break;

            case FishState.Catching:
                UpdateCatching(ctx.DeltaTime);
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
        CancelHop();                   // 跳ね中に狙いへ戻ったら跳ねも畳む
        AbortBiteTiming();             // アタリ進行中の魚が居れば逃がす
        ReleaseHook();                 // 掛かったままの魚が居れば逃がす
        fight?.EndFight();             // やり取りの UI・内部値も畳む
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
        CancelHop();                   // 姿勢解除・中断でも跳ねを畳む（フラグの持ち越し防止）
        AbortBiteTiming();             // 姿勢解除・中断でもアタリ進行を打ち切る
        ReleaseHook();                 // 姿勢解除・中断でも必ず魚を逃がす
        fight?.EndFight();             // 姿勢解除・中断でもやり取りを畳む
        presenter?.Abort();            // 釣り上げ演出中なら畳む（魚の破棄・白／テキストの消去も込み）
        HideJudgement();               // 判定画像も消す
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
    /// ウキが手元を離れて外に出ているか【「ウキが出ている」判定の唯一の定義】。
    ///
    /// 飛翔中（<see cref="FishState.Casting"/>）・着水後の待機
    /// （<see cref="FishState.Floating"/>）・巻き取り中（<see cref="FishState.Reeling"/>）・
    /// アタリ中（<see cref="FishState.Nibbling"/> / <see cref="FishState.HookWindow"/>）・
    /// わらしべ連鎖のアタリ中（<see cref="FishState.ChainNibbling"/> /
    /// <see cref="FishState.ChainHookWindow"/>）・
    /// ヒット中（<see cref="FishState.Hooked"/>）が該当する。
    /// 釣り上げ演出（<see cref="FishState.Catching"/>）は<b>寄りのフェーズだけ</b>該当する:
    /// ホワイトアウトで構図を切り替えたあとは、沖のウキと釣り糸が釣果の画に映り込まないよう
    /// ウキを手元へ畳むため（<see cref="UpdateCatching"/>）。
    /// 逆に <see cref="FishState.Idle"/> / <see cref="FishState.Aiming"/> /
    /// <see cref="FishState.Windup"/> ではウキは非表示で手元にある。
    ///
    /// 状態を 1 つ増やすたびに各所の列挙を直して回る（＝直し漏れが必ず出る）のを避けるため、
    /// 「ウキが出ている」を意味する判定はすべてこの関数を通す。
    /// ただし<b>意味が違うもの</b>（餌として有効か＝<see cref="BaitActive"/>、
    /// 竿を振ってよいか＝<see cref="TryStartHop"/> の前提）はここに混ぜない。
    /// </summary>
    private bool IsFloatOut()
        => State is FishState.Casting or FishState.Floating or FishState.Reeling
                 or FishState.Nibbling or FishState.HookWindow
                 or FishState.ChainNibbling or FishState.ChainHookWindow
                 or FishState.Hooked
        || (State == FishState.Catching
            && CatchPhase == CatchPresenter.CatchPhase.ApproachCamera);

    /// <summary>
    /// カーソルロックの望ましい状態。
    ///
    /// 既定でロックするのは、マウスの左右の振りでキャストを組み立てる区間＝
    /// <see cref="FishState.Aiming"/>（振りかぶり待ち）と <see cref="FishState.Windup"/>（振り抜き待ち）
    /// だけ（<see cref="lockCursorWhileFishing"/> がオフなら常に false ＝解除は必ず通る）。
    ///
    /// 着水後（Floating / Reeling / Nibbling / HookWindow）は合わせが左クリック 1 回になり
    /// マウスの移動量を一切読まないので、ロックしない（カーソルは出したままにする）。
    /// </summary>
    private bool WantsCursorLock()
        // キャストのジェスチャ区間（ロックする唯一の区間）
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
        // 合わせは左クリックの押下フレームだけを見る「状態を持たない」判定になったので、
        // ここで捨てるべき合わせ側の累積は無い（旧: 振り上げウィンドウの掃除）。
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

        // A/D でプレイヤー自身を海の正面から左右へ振る（キャスト方向はプレイヤーの正面＝
        // transform.Forward に追従するので、CastYawDegrees / 着水点プレビューには何もしなくても反映される）。
        UpdateStanceTurn(deltaTime);

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
    /// 構え中（<see cref="FishState.Aiming"/> / <see cref="FishState.Windup"/>）だけ有効な
    /// A/D 入力を読み、<see cref="PlayerMove.TurnInStance"/> でプレイヤー自身を振る。
    ///
    /// <b>符号</b>: D を押すとプレイヤー視点で右へ、A で左へ回る。これはリールの操舵
    /// （<see cref="ComputeReelDirection"/>）が「プレイヤーの操作感として D で右」に
    /// なるよう符号を反転しているのと結果として同じ操作感になるが、あちらは
    /// 「ウキ→竿先」を基準にした角度への変換で符号が反転しているのに対し、
    /// こちらはプレイヤー自身のヨー角を直接動かすため反転は不要（+yaw が
    /// そのままプレイヤー視点の右回転になる）。キャストの角度オフセットは廃止し、
    /// プレイヤーの正面（<c>transform.Forward</c>）がそのままキャスト方向になる。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateStanceTurn(float deltaTime)
    {
        if (playerMove is not { } pm) { return; }

        float turn = 0f;
        if (SEED.Input.GetKey(SEED.KeyCode.A)) { turn -= 1f; }   // A: 左（プレイヤー視点）
        if (SEED.Input.GetKey(SEED.KeyCode.D)) { turn += 1f; }   // D: 右（プレイヤー視点）

        pm.TurnInStance(turn, deltaTime);
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

        // 竿を引いた手応えとして擦れ音を鳴らす（投げる前の振りかぶりに入った瞬間）
        PlaySe(windupSePath, windupSeVolume);

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

        // 振りかぶり中も引き続き A/D で振れる（着水点マーカーはプレイヤーの正面に
        // 追従するので、UpdateCastPreview 側は何も変えなくてよい）。
        UpdateStanceTurn(deltaTime);

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
        PlaySe(castSePath, castSeVolume);

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
            PlaySe(splashSePath, splashSeVolume);
            SEED.Debug.Log("[Fishing] Floating");
        }
    }

    /// <summary>
    /// ヒット中のやり取り（<see cref="FishingFight"/>）を 1 フレーム進める。
    ///
    /// 巻き取り量は <see cref="ReadReelAmount"/> をそのまま渡す
    /// （<see cref="UpdateReeling"/> と同じ入力を見る）。残り距離の表示も
    /// ここで一括して更新する。糸が切れたら <see cref="BreakLine"/> で締める
    /// （この呼び出しの後、状態は <see cref="FishState.Aiming"/> になっている）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateFight(float deltaTime)
    {
        if (fight is not { } f) { return; }

        // 出題の打点演出（PlayNibbleCue）で使う沈みアニメを進める。
        // アタリ中（Nibbling / HookWindow / 連鎖）は UpdateBiteTiming が同じタイマーを
        // 進めるので、二重に進めないよう Hooked のときだけここで進める。
        if (State == FishState.Hooked && nibbleDipElapsed >= 0f)
        {
            nibbleDipElapsed += deltaTime;
            if (nibbleDipElapsed >= nibbleDipSeconds) { nibbleDipElapsed = NoDipElapsed; }
        }

        f.Tick(deltaTime, ReadReelAmount());

        // 残り距離（ウキ→竿先の水平距離）の表示。ウキが無ければ 0 を出す。
        f.UpdateDistanceDisplay(CurrentFloatDistance());

        if (f.LineBroken) { BreakLine(); return; }

        // 魚 HP を削り切ったら釣り上げ成立【新仕様の主たる成功条件】。
        // 連鎖のアタリ受付中（Paused）は魚 HP が減らないので、ここは Hooked のときだけ通る。
        if (State == FishState.Hooked && f.FishDefeated) { FinishReeling(); }
    }

    /// <summary>
    /// 現在のウキ→竿先の水平距離（メートル）。ウキが無ければ 0。
    /// やり取りの基準距離（開始時の <see cref="FishingFight.BeginFight"/>・残り距離表示・
    /// ウキの距離制御）で共通に使う。
    /// </summary>
    private float CurrentFloatDistance()
        => uki is { IsValid: true } floatTf
            ? HorizontalDistance(floatTf.Position, ReelTargetPosition())
            : 0f;

    /// <summary>
    /// 糸が切れたときの締め【糸切れの唯一の出口】。
    /// 魚を逃がし、判定表示の Miss を流用して失敗を示し、狙い（キャスト待ち）へ戻す。
    /// </summary>
    private void BreakLine()
    {
        fight?.EndFight();             // Paused も EndFight で必ず解除される
        nibbleDipElapsed = NoDipElapsed;   // 出題の沈みアニメが途中なら必ず戻す
        CancelHop();
        ReleaseChainNibbler();         // 連鎖でつつきに来ていた魚が居れば一緒に逃がす
        ReleaseHook();                 // 掛かっていた魚を逃がす（Escape → 退場）

        LastJudgement = HookJudgement.Miss;
        ShowJudgement(HookJudgement.Miss);

        State = FishState.Aiming;
        ResetGesture();
        ParkFloatHidden();
        HideLine();
        CrossFadeBoth(floatClip, playerFloatClip);
        // 再び振りを読む区間へ戻るのでカーソルロックを引き直す。
        UpdateCursorLock();
        SEED.Debug.Log("[Fishing] 糸が切れた");
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

        // ── ウキの移動 ───────────────────────────────────
        // 掛かっていないとき: 従来どおり「巻いた量そのまま」手前へ寄る
        //   （残りの水平距離でクランプして基準点を追い越さない）。
        // ヒット中: 巻き入力はウキを直接動かさず、魚 HP を削る仕事に変わった。
        //   ウキは FishingFight が持つ「目標距離」へ寄る／引かれるだけなので、
        //   移動量は ComputeFloatDistanceStep（＋ が沖 / − が手元）に一本化する。
        SEED.Vector3 next;
        if (IsHooked && fight is { Active: true } activeFight)
        {
            float signedStep = activeFight.ComputeFloatDistanceStep(remaining, deltaTime);

            // 沖へ出る向きだけは「最長飛距離 ＋ 余裕」でクランプし、
            // 引かれ続けてウキが世界の外へ出るのを防ぐ。
            if (signedStep > 0f)
            {
                float dragLimit = maxCastDistance + floatDragMarginDistance;
                signedStep = SEED.Mathf.Min(signedStep, SEED.Mathf.Max(dragLimit - remaining, 0f));
            }

            // dir は竿先の方向（A / D の操舵込み）。沖へは その逆向きへ動かす。
            next = floatTf.Position - dir * signedStep;
        }
        else
        {
            float step = SEED.Mathf.Min(amount, remaining);
            next = floatTf.Position + dir * step;
        }

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
    /// 魚が掛かっていれば釣り上げ演出（<see cref="FishState.Catching"/>）、
    /// 何も掛かっていなければ再びキャスト待ちへ戻る。
    /// </summary>
    private void FinishReeling()
    {
        CancelHop();                   // 巻き取りが終わったら跳ねも必ず畳む
        ReleaseChainNibbler();         // 連鎖でつつきに来ていた魚が居れば逃がす（釣り上げに巻き込まない）
        fight?.EndFight();             // やり取り（テンション・魚HP）は成否にかかわらずここで畳む
        nibbleDipElapsed = NoDipElapsed;   // 出題の沈みアニメが途中なら必ず戻す（ウキが沈んだまま残らないように）

        // ── 釣り上げ成立 ──
        if (hookedFish is { } caught)
        {
            SEED.Debug.Log($"[Fishing] 釣り上げ: {caught.DisplayName}（大きさ {caught.DisplaySize:F1} {caught.SizeUnitLabel}）");

            // 魚の AI を止める（以後の位置・向き・スケールは CatchPresenter が決める）。
            // 円環クランプの除外登録は<b>外さない</b>: 外すと演出中の魚が FishManager に
            // 出現円環内へ引き戻されてしまう。登録は魚の破棄時に自動で外れる。
            caught.OnCaught();
            hookedFish = null;           // ReleaseFromHook は呼ばない（この個体は演出側が持つ）

            State = FishState.Catching;
            // 演出中はマウスの振りを読まないのでカーソルロックを引き直す（解除される）。
            UpdateCursorLock();

            // 演出プレゼンタが未設定なら演出を飛ばす（魚を消して移動へ戻すだけ）
            if (presenter is { } p) { p.Begin(caught); }
            else
            {
                caught.Actor.Destroy();
                ExitToMovement();
            }
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
    /// 釣り上げ演出（<see cref="FishState.Catching"/>）の更新。
    ///
    /// 進行そのものは <see cref="CatchPresenter"/> が持つので、ここは
    /// 「1 フレーム進める」→「終わっていたら移動へ戻す」の 2 行だけを担う
    /// （演出の内容が変わっても本スクリプトを触らずに済ませる）。
    /// プレゼンタ未設定なら演出できないので、即座に移動へ戻す。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateCatching(float deltaTime)
    {
        if (presenter is not { } p)
        {
            ExitToMovement();
            return;
        }

        p.Tick(deltaTime);

        // 寄りのフェーズを抜けた（＝白で覆われている）以降はウキを手元へ畳む。
        // ParkFloatHidden は表示フラグと位置を引き直すだけなので毎フレーム呼んで安全。
        if (p.Phase != CatchPresenter.CatchPhase.ApproachCamera) { ParkFloatHidden(); }

        // Phase が None に戻った ＝ 演出完了（魚もプレゼンタ側で破棄済み）
        if (p.Phase != CatchPresenter.CatchPhase.None) { return; }

        ExitToMovement();
        SEED.Debug.Log("[Fishing] Idle（釣り上げ演出おわり）");
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
    /// 水面 ＋ 上下揺れ − 現在の沈み量（<see cref="CurrentDipDepth"/>）。
    /// </summary>
    private float FloatSurfaceY()
        => WaterSurfaceY() + BobOffset() - CurrentDipDepth();

    /// <summary>
    /// 現在のウキの沈み量（メートル）【沈みの唯一の算出点】。
    ///
    /// - ヒット中（<see cref="FishState.Hooked"/>）… <see cref="biteDipDepth"/> で沈みっぱなし
    /// - 本アタリ（<see cref="FishState.HookWindow"/>）… <see cref="nibbleDipSeconds"/> を掛けて
    ///   <see cref="biteDipDepth"/> まで滑らかに沈み、そのまま保持する
    /// - 前アタリの沈み中 … <see cref="nibbleDipDepth"/> まで sin 半周で沈んで戻る（山なり）
    /// - それ以外 … 0
    ///
    /// わらしべ連鎖（<see cref="FishState.ChainNibbling"/> /
    /// <see cref="FishState.ChainHookWindow"/>）では、ヒット中の沈み
    /// （<see cref="biteDipDepth"/>）を土台に、上記のアタリの沈みを<b>加算</b>する。
    /// </summary>
    private float CurrentDipDepth()
    {
        // ヒット中（＝わらしべ連鎖のアタリ中も含む）は、まず biteDipDepth だけ沈んでいる。
        // 連鎖のアタリの沈みは、その上へ<b>重ねて</b>乗せる（掛かった魚の分の沈みは維持する）。
        // ヒット中でなければ 0 なので、通常のアタリの挙動は従来とまったく同じ。
        float baseDip = IsHooked ? biteDipDepth : 0f;

        if (State is FishState.HookWindow or FishState.ChainHookWindow)
        {
            // 0→1 へ滑らかに沈み込み、以降は 1（＝沈みきったまま）で保持する
            float sinkRatio = SEED.Mathf.Clamped01(
                reactionElapsed / SEED.Mathf.Max(nibbleDipSeconds, DivideEpsilon));
            return baseDip + biteDipDepth * SmoothStep01(sinkRatio);
        }

        if (nibbleDipElapsed < 0f) { return baseDip; }

        // 山なりの沈み: t=0 と t=1 で 0、t=0.5 で最大（sin(πt)）
        float t = SEED.Mathf.Clamped01(
            nibbleDipElapsed / SEED.Mathf.Max(nibbleDipSeconds, DivideEpsilon));
        return baseDip + nibbleDipDepth * SEED.Mathf.Sin(SEED.Mathf.PI * t);
    }

    /// <summary>
    /// 0〜1 の値を滑らかな S 字（3t²-2t³）へ変換する。沈み込みの緩急に使う。
    /// </summary>
    /// <param name="t">0〜1 の進行度（範囲外はクランプ済みを想定）。</param>
    private static float SmoothStep01(float t) => t * t * (3f - 2f * t);

    // ─── アタリ（前アタリ→本アタリ）と合わせ判定 ─────────────

    /// <summary>
    /// <see cref="FishState.Nibbling"/> / <see cref="FishState.HookWindow"/> と、
    /// わらしべ連鎖の <see cref="FishState.ChainNibbling"/> /
    /// <see cref="FishState.ChainHookWindow"/> の毎フレーム更新
    /// 【アタリ進行の唯一の更新点。通常も連鎖も同じ経路を通る】。
    ///
    /// 通常と連鎖の違いは 2 点だけ:
    /// アタリの主が <see cref="nibblingFish"/> か <see cref="chainNibbler"/> かと、
    /// 決着後に戻る状態が <see cref="FishState.Floating"/> か <see cref="FishState.Hooked"/> か。
    ///
    /// <b>やること</b>
    /// 1. ウキはその場に留め置く（XZ は動かさず、Y だけ沈みアニメを反映する）
    /// 2. マウスの振り（合わせ）を検出する
    /// 3. 前アタリ … 合わせがあれば早合わせ（Miss）。無ければ次の前アタリ／本アタリへ進める
    /// 4. 本アタリ … 反応時間を積算し、合わせがあれば判定、<see cref="niceSeconds"/> 超過で時間切れ Miss
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateBiteTiming(float deltaTime)
    {
        // 通常のアタリと、わらしべ連鎖のアタリで「主」の置き場所だけが違う
        bool chain = IsChainBite;
        var current = chain ? chainNibbler : nibblingFish;

        // アタリの主が居なくなった（魚が破棄されたなど）ら元の状態へ戻す
        if (current is not { } fish)
        {
            ClearBiteTiming();
            if (chain) { ResumeHookedFromChain(); }
            else { State = FishState.Floating; }
            return;
        }

        // 1) ウキはその場（XZ 固定）。沈みアニメだけ Y に乗せる。
        if (uki is { IsValid: true } floatTf)
        {
            var p = floatTf.Position;
            SetFloatPosition(new SEED.Vector3(p.x, FloatSurfaceY(), p.z));
        }

        // 前アタリの沈みアニメを進める（時間切れで無効値へ戻す）
        if (nibbleDipElapsed >= 0f)
        {
            nibbleDipElapsed += deltaTime;
            if (nibbleDipElapsed >= nibbleDipSeconds) { nibbleDipElapsed = NoDipElapsed; }
        }

        // 2) 合わせ（左クリック）の検出。検出そのものは Floating / Reeling と共通で、
        //    ここでは「アタリ中の振り＝合わせ」として解釈する（跳ねさせない）。
        bool swung = UpdateSwingDetection();

        // 3) 前アタリ中（通常・連鎖のどちらも同じ進行）
        if (State is FishState.Nibbling or FishState.ChainNibbling)
        {
            if (swung)
            {
                // まだ食っていないのに合わせた＝早合わせ
                FailBite("早合わせ");
                return;
            }

            nibbleTimer -= deltaTime;
            if (nibbleTimer > 0f) { return; }

            if (nibbleRemaining > 0)
            {
                // 前アタリを 1 回打つ（ウキが小さく沈んで戻る）
                nibbleRemaining--;
                nibbleDipElapsed = 0f;
                nibbleTimer = NextNibbleInterval();
                PlaySe(nibbleSePath, nibbleSeVolume);
                return;
            }

            // 前アタリを撃ち切ってさらに 1 間隔経過 → 本アタリ
            BeginHookWindow(fish);
            return;
        }

        // 4) 本アタリの反応受付
        reactionElapsed += deltaTime;

        if (swung)
        {
            JudgeHook(fish, reactionElapsed);
            return;
        }

        if (reactionElapsed > niceSeconds)
        {
            // 反応できなかった（時間切れ）
            FailBite("時間切れ");
        }
    }

    /// <summary>
    /// 本アタリ（<see cref="FishState.HookWindow"/>）へ入る。
    /// ウキが <see cref="biteDipDepth"/> まで大きく沈み、反応時間の計測が始まる。
    /// </summary>
    /// <param name="fish">アタっている魚（ログ用）。</param>
    private void BeginHookWindow(Fish fish)
    {
        bool chain = IsChainBite;
        State = chain ? FishState.ChainHookWindow : FishState.HookWindow;
        reactionElapsed = 0f;
        nibbleDipElapsed = NoDipElapsed;
        PlaySe(hookSePath, hookSeVolume);
        SEED.Debug.Log($"[Fishing] {(chain ? "わらしべ本アタリ!" : "本アタリ!")} {fish.DisplayName}");
    }

    /// <summary>
    /// いま進行しているのが<b>わらしべ連鎖</b>のアタリか
    /// 【通常のアタリと連鎖のアタリを見分ける唯一の判定点】。
    /// </summary>
    private bool IsChainBite
        => State is FishState.ChainNibbling or FishState.ChainHookWindow;

    /// <summary>
    /// アタリ演出用の単発効果音を再生する共通ヘルパー。
    /// <paramref name="path"/> が空文字／null の場合は何もしない（未設定＝無音を許容するため）。
    /// </summary>
    /// <param name="path">再生するアセットパス（例: "assets://mainGame/audios/hit.mp3"）。</param>
    /// <param name="volume">再生音量（0〜1）。</param>
    private static void PlaySe(string path, float volume)
    {
        if (string.IsNullOrEmpty(path)) { return; }
        SEED.Audio.Play(path, volume);
    }

    /// <summary>
    /// 反応時間から判定を決めて結果へ分岐する【合わせ成立時の唯一の出口】。
    /// </summary>
    /// <param name="fish">アタっている魚。</param>
    /// <param name="reactionSeconds">本アタリからの経過秒数。</param>
    private void JudgeHook(Fish fish, float reactionSeconds)
    {
        HookJudgement judgement =
            reactionSeconds <= excellentSeconds ? HookJudgement.Excellent :
            reactionSeconds <= greatSeconds ? HookJudgement.Great :
            reactionSeconds <= niceSeconds ? HookJudgement.Nice :
            HookJudgement.Miss;

        if (judgement == HookJudgement.Miss)
        {
            FailBite("遅すぎ");
            return;
        }

        // ── ヒット成立 ──
        bool chain = IsChainBite;
        ClearBiteTiming();
        LastJudgement = judgement;
        ShowJudgement(judgement);

        // わらしべ連鎖の合わせ成功: 掛かっている魚が食われ、この魚へ乗り換わる
        if (chain)
        {
            SwapHookedFish(fish, judgement);
            SEED.Debug.Log($"[Fishing] わらしべ合わせ成功: {judgement}（反応 {reactionSeconds:F3} 秒）");
            return;
        }

        if (TryHook(fish))
        {
            fish.OnHooked();
            SEED.Debug.Log($"[Fishing] 合わせ成功: {judgement}（反応 {reactionSeconds:F3} 秒）");
            return;
        }

        // ここへ来るのは餌が無効化された等の例外だけ。魚を逃がして待機へ戻す。
        State = FishState.Floating;
        fish.ReleaseFromHook();
    }

    /// <summary>
    /// 合わせ失敗【Miss の唯一の出口】。魚を逃がし、餌はそのままで待機へ戻す。
    /// </summary>
    /// <param name="reason">ログに出す失敗理由（早合わせ／遅すぎ／時間切れ）。</param>
    private void FailBite(string reason)
    {
        bool chain = IsChainBite;
        var fish = chain ? chainNibbler : nibblingFish;
        ClearBiteTiming();
        LastJudgement = HookJudgement.Miss;
        ShowJudgement(HookJudgement.Miss);

        // 連鎖の失敗は「元の魚とのやり取りへ戻る」、通常の失敗は「餌だけが浮いた待機へ戻る」
        if (chain) { ResumeHookedFromChain(); }
        else { State = FishState.Floating; }

        fish?.ReleaseFromHook();       // 魚は逃げる（Escape → クールダウン付きで回遊へ）
        SEED.Debug.Log($"[Fishing] {(chain ? "わらしべ Miss" : "Miss")}（{reason}）");
    }

    /// <summary>
    /// アタリ進行を外部都合で打ち切る（釣り姿勢の解除・スクリプト破棄など）。
    /// 判定は表示せず、魚だけ逃がす。
    /// </summary>
    private void AbortBiteTiming()
    {
        // 通常のアタリ・わらしべ連鎖のアタリのどちらも、居る方だけが逃げる
        var fish = nibblingFish;
        var chained = chainNibbler;
        ClearBiteTiming();
        fish?.ReleaseFromHook();
        chained?.ReleaseFromHook();
    }

    /// <summary>アタリ進行の内部状態をすべて初期化する（状態遷移は行わない）。</summary>
    private void ClearBiteTiming()
    {
        nibblingFish = null;
        chainNibbler = null;
        nibbleRemaining = 0;
        nibbleTimer = 0f;
        nibbleDipElapsed = NoDipElapsed;
        reactionElapsed = 0f;
    }

    /// <summary>次のアタリまでの間隔（秒）を抽選する。</summary>
    private float NextNibbleInterval()
        => SEED.Random.Range(nibbleIntervalMin, SEED.Mathf.Max(nibbleIntervalMin, nibbleIntervalMax));

    /// <summary>
    /// 竿振り（＝合わせ）の検出【振りを読む唯一の入口】。
    ///
    /// 餌が水に有るあいだ（<see cref="FishState.Floating"/> /
    /// <see cref="FishState.Reeling"/> / <see cref="FishState.Nibbling"/> /
    /// <see cref="FishState.HookWindow"/>）は、状態に関わらずこの関数で振りを読む。
    ///
    /// <b>操作は「左クリックを押した瞬間」だけ</b>。
    /// <see cref="SEED.Input.GetMouseButtonDown"/> は押下フレームでしか true を返さないので、
    /// しきい値・時間窓・クールダウンといった内部状態を一切持たずに、
    /// 1 回のクリックがちょうど 1 回の振りになる
    /// （旧実装の上方向フリック検出は、ゆっくり動かしただけの暴発とカーソルロック依存を
    ///   抱えていたため廃止した）。
    ///
    /// <b>釣り姿勢に入るクリックを合わせとして食わない理由</b>:
    /// 姿勢に入るのは <see cref="FishState.Idle"/> で押下した<b>そのフレーム</b>だけで、
    /// その時点で状態は <see cref="FishState.Aiming"/>（＝ウキは手元にあり水に出ていない）。
    /// この関数は Floating / Reeling / Nibbling / HookWindow でしか呼ばれないので、
    /// 姿勢に入る押下がここへ届くことはない。キャストは左クリックを押したまま成立し、
    /// 押しっぱなしのあいだは押下フレームが来ないため、着水後に<b>改めて押し直した</b>
    /// クリックだけが合わせになる。
    ///
    /// ここで足すのは「振りが成立した瞬間に必ず起きること」だけ:
    /// <see cref="SwingSerial"/> の加算（魚への通知）と効果音。
    ///
    /// <b>振りの結果は呼び出し側が決める</b>（状態ごとに意味が違うため）:
    /// - Floating / Reeling … ウキが跳ねる空振り（<see cref="TryStartHop"/>）
    /// - Nibbling           … 早合わせ（Miss。魚は逃げる）
    /// - HookWindow         … 合わせ判定（Excellent / Great / Nice / Miss）
    /// </summary>
    /// <returns>このフレームに振りが成立したら true。</returns>
    private bool UpdateSwingDetection()
    {
        if (!SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left)) { return false; }

        // 番号は「どの状態で振ったか」に依らず増やす（魚は状態を見ずに変化だけを見る）
        SwingSerial++;
        PlaySe(swingSePath, swingSeVolume);
        return true;
    }

    // ─── 空振り（ウキの跳ね）─────────────────────────────────

    /// <summary>
    /// ウキの跳ね（空振り演出）を開始する【跳ね開始の唯一の入口】。
    ///
    /// アタリが無い状態（<see cref="FishState.Floating"/> /
    /// <see cref="FishState.Reeling"/>）で竿を振ったときだけ成立する。
    /// アタリ中（Nibbling / HookWindow）とヒット中（Hooked）は振りの意味が
    /// まったく別なので、ここで弾いて絶対に跳ねさせない。
    ///
    /// 引き寄せ距離は「竿先までの残り水平距離 −<see cref="reelEndDistance"/>」で
    /// クランプするので、跳ねだけで巻き取りが完了することはない
    /// （＝跳ねが <see cref="FinishReeling"/> を誘発しない）。
    /// </summary>
    private void TryStartHop()
    {
        // 跳ねてよい状態か（アタリ中・ヒット中は不可）
        if (State is not (FishState.Floating or FishState.Reeling)) { return; }
        if (IsHooked || nibblingFish is not null) { return; }
        if (hopActive) { return; }                                   // 跳ね中の多重発火は無視する
        if (hopSeconds <= DivideEpsilon) { return; }                 // 0 秒の跳ねは演出にならないので行わない
        if (uki is not { IsValid: true } floatTf) { return; }

        // ウキ→竿先の水平ベクトル（＝引き寄せる向き）と残り距離
        var target = ReelTargetPosition();
        var toTarget = new SEED.Vector3(target.x - floatTf.Position.x, 0f, target.z - floatTf.Position.z);
        float remaining = SEED.Mathf.Sqrt(toTarget.x * toTarget.x + toTarget.z * toTarget.z);

        // 引き寄せ量: 要求値を「巻き取りが完了しない範囲」へクランプする（負なら 0 ＝その場で跳ねるだけ）
        float pull = SEED.Mathf.Max(0f, SEED.Mathf.Min(hopPullDistance, remaining - reelEndDistance));

        hopStart = floatTf.Position;
        hopEnd = remaining > DivideEpsilon
            ? hopStart + new SEED.Vector3(toTarget.x / remaining, 0f, toTarget.z / remaining) * pull
            : hopStart;                                              // 竿先に重なっている異常時はその場で跳ねる
        hopElapsed = 0f;
        hopActive = true;
    }

    /// <summary>
    /// ウキの跳ねの毎フレーム更新（<see cref="hopActive"/> のあいだ
    /// <see cref="UpdateReeling"/> の代わりに走る）。
    ///
    /// 水平は開始点→終了点の線形補間、垂直は水面から <c>4h·t·(1−t)</c> の放物線で、
    /// t=1（＝<see cref="hopSeconds"/> 経過）でちょうど水面へ戻って跳ねが終わる。
    /// 釣り糸は従来どおり <see cref="LateUpdate"/> の <see cref="UpdateLine"/> が
    /// ウキの位置から引き直すので、跳ねているあいだも糸は繋がったままになる。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateHop(float deltaTime)
    {
        hopElapsed += deltaTime;

        // 進行度 t（0〜1）。hopSeconds は TryStartHop で 0 でないことを保証済みだが、
        // インスペクタで実行中に 0 へ書き換えられても壊れないよう分母を守る。
        float t = SEED.Mathf.Clamped01(hopElapsed / SEED.Mathf.Max(hopSeconds, DivideEpsilon));

        // 水平: 開始点 → 終了点の線形補間
        var horizontal = hopStart + (hopEnd - hopStart) * t;

        // 垂直: 水面（通常のウキ高さ）＋ 放物線の持ち上げ。t=0 と t=1 で持ち上げは 0 になる。
        float lift = ParabolaApexCoefficient * hopHeight * t * (1f - t);
        SetFloatPosition(new SEED.Vector3(horizontal.x, FloatSurfaceY() + lift, horizontal.z));

        if (t >= 1f) { CancelHop(); }
    }

    /// <summary>
    /// 跳ねを終了・中断する【跳ね解除の唯一の出口】。
    /// 自然終了（着水）だけでなく、状態が Floating / Reeling を離れるとき
    /// （キャンセル・狙いへの復帰・前アタリ開始・巻き取り完了）にも呼び、
    /// 「跳ねフラグが立ったまま別の状態へ持ち越される」ことを防ぐ。
    /// ウキの Y は次フレームの通常更新（<see cref="FloatSurfaceY"/>）が水面へ戻すので、
    /// ここでは位置を触らない。
    /// </summary>
    private void CancelHop()
    {
        hopActive = false;
        hopElapsed = 0f;
    }

    // ─── 判定表示（スクリーンスペース UI）─────────────────────

    /// <summary>判定画像の表示を開始する（表示中なら差し替えて再生し直す）。</summary>
    /// <param name="judgement">表示する判定。</param>
    /// <param name="hint">
    /// 判定に添えるヒント（"早い" / "遅い"）。空文字ならヒントは出さない。
    /// 合わせ（<see cref="FishState.HookWindow"/>）の判定では常に空文字。
    /// </param>
    private void ShowJudgement(HookJudgement judgement, string hint = "")
    {
        if (judgement == HookJudgement.None) { return; }

        // 直前の表示のポップを元へ戻してから切り替える（サイズの取りこぼしを防ぐ）
        RestoreJudgeSize();

        judgeDisplay = judgement;
        judgeElapsed = 0f;
        judgeHint = hint;
        if (JudgeSprite(judgement) is { IsValid: true } sprite) { judgeBaseSize = sprite.Size; }
        ApplyJudgeVisibility(judgement, JudgeVisibleOpacity);
    }

    /// <summary>
    /// リズムのやり取り（<see cref="FishingFight"/>）の判定を表示する
    /// 【やり取り側から判定 UI を触る唯一の入口】。
    ///
    /// 判定画像は合わせのものをそのまま流用し、加えて「早い／遅い」のヒントを添える。
    /// Excellent（ぴったり）とヒントを出す意味が無い場合（<paramref name="signedOffsetSeconds"/> が 0）は
    /// ヒントを空にする。
    /// </summary>
    /// <param name="judgement">表示する判定。</param>
    /// <param name="signedOffsetSeconds">打点との時間差（＋ ＝ 遅い / − ＝ 早い、秒）。</param>
    public void ShowFightJudgement(HookJudgement judgement, float signedOffsetSeconds)
    {
        ShowJudgement(judgement, JudgeHintFor(judgement, signedOffsetSeconds));
    }

    /// <summary>
    /// 判定と時間差に対応するヒント文言を返す（出さない場合は空文字）。
    /// </summary>
    /// <param name="judgement">判定。</param>
    /// <param name="signedOffsetSeconds">打点との時間差（＋ ＝ 遅い / − ＝ 早い、秒）。</param>
    private string JudgeHintFor(HookJudgement judgement, float signedOffsetSeconds)
    {
        if (judgement == HookJudgement.Excellent) { return string.Empty; }
        if (SEED.Mathf.Abs(signedOffsetSeconds) <= DivideEpsilon) { return string.Empty; }
        return signedOffsetSeconds > 0f ? judgeHintLate : judgeHintEarly;
    }

    /// <summary>
    /// リズムのやり取りの「出題」で、前アタリとまったく同じ演出（つつき音＋ウキの沈み）を出す
    /// 【出題の打点演出の唯一の入口】。
    ///
    /// 沈みアニメは前アタリと同じ <see cref="nibbleDipElapsed"/> を使い回すので、
    /// ヒット中の沈み（<see cref="biteDipDepth"/>）へ<b>加算</b>される形で見える。
    /// </summary>
    public void PlayNibbleCue()
    {
        nibbleDipElapsed = 0f;
        PlaySe(nibbleSePath, nibbleSeVolume);
    }

    /// <summary>
    /// 判定画像の毎フレーム更新（表示時間の経過とポップの縮小）。
    /// 表示していないあいだは何もしない。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateJudgementUi(float deltaTime)
    {
        if (judgeDisplay == HookJudgement.None) { return; }

        judgeElapsed += deltaTime;
        if (judgeElapsed >= judgeShowSeconds)
        {
            HideJudgement();
            return;
        }

        // ポップ: judgePopScale → 1.0 へ judgePopSeconds かけて縮む
        if (judgeBaseSize is { } baseSize && judgePopSeconds > DivideEpsilon)
        {
            float t = SEED.Mathf.Clamped01(judgeElapsed / judgePopSeconds);
            float scale = SEED.Mathf.Lerp(judgePopScale, 1f, SmoothStep01(t));
            if (JudgeSprite(judgeDisplay) is { IsValid: true } sprite)
            {
                sprite.Size = new SEED.Vector2(baseSize.x * scale, baseSize.y * scale);
            }
        }
    }

    /// <summary>
    /// 判定画像をすべて隠す【非表示の唯一の出口】。ポップで変えたサイズも元へ戻す。
    /// </summary>
    private void HideJudgement()
    {
        RestoreJudgeSize();
        judgeDisplay = HookJudgement.None;
        judgeElapsed = 0f;
        judgeHint = string.Empty;
        ApplyJudgeVisibility(HookJudgement.None, 0f);
    }

    /// <summary>ポップで書き換えた表示サイズを元の値へ戻す（戻す対象が無ければ何もしない）。</summary>
    private void RestoreJudgeSize()
    {
        if (judgeBaseSize is not { } baseSize) { return; }
        if (JudgeSprite(judgeDisplay) is { IsValid: true } sprite) { sprite.Size = baseSize; }
        judgeBaseSize = null;
    }

    /// <summary>
    /// 4 枚の判定画像の不透明度をまとめて設定する
    /// （指定の 1 枚だけ <paramref name="opacity"/>、残りは 0）。
    /// </summary>
    /// <param name="visible">表示する判定（None ならすべて非表示）。</param>
    /// <param name="opacity">表示する 1 枚の不透明度。</param>
    private void ApplyJudgeVisibility(HookJudgement visible, float opacity)
    {
        ApplySpriteOpacity(judgeExcellentSprite, visible == HookJudgement.Excellent ? opacity : 0f);
        ApplySpriteOpacity(judgeGreatSprite, visible == HookJudgement.Great ? opacity : 0f);
        ApplySpriteOpacity(judgeNiceSprite, visible == HookJudgement.Nice ? opacity : 0f);
        ApplySpriteOpacity(judgeMissSprite, visible == HookJudgement.Miss ? opacity : 0f);

        // ヒント（早い／遅い）は判定画像と完全に同期させる。
        // 文言が空（合わせの判定・Excellent など）のときは不透明度も 0 にして必ず消す。
        if (judgeHintText is not { } hint || !hint.IsValid) { return; }

        bool showHint = visible != HookJudgement.None && !string.IsNullOrEmpty(judgeHint);
        hint.Content = showHint ? judgeHint : string.Empty;
        hint.Color = hint.Color.WithAlpha(showHint ? opacity : 0f);
    }

    /// <summary>判定に対応するスプライト（未設定なら null）。</summary>
    /// <param name="judgement">判定。</param>
    private SEED.Sprite? JudgeSprite(HookJudgement judgement) => judgement switch
    {
        HookJudgement.Excellent => judgeExcellentSprite,
        HookJudgement.Great => judgeGreatSprite,
        HookJudgement.Nice => judgeNiceSprite,
        HookJudgement.Miss => judgeMissSprite,
        _ => null,
    };

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
    /// ウキが外に出ている状態（<see cref="IsFloatOut"/>）のときだけ描く。
    /// Idle / Aiming / Windup ではウキが非表示になっているので線に意味が無く、非表示にする。
    /// たるみは飛翔中は固定量、着水後は飛距離に比例（上限つき）。
    ///
    /// 判定は必ず <see cref="IsFloatOut"/> を通す（ここで独自に状態を列挙していたため、
    /// 後から増えた Nibbling / HookWindow / Hooked でアタリの瞬間に糸が消えるバグがあった）。
    /// </summary>
    private void UpdateLine()
    {
        if (line is not { } l || !l.IsValid) { return; }

        if (!IsFloatOut()) { l.Visible = false; return; }

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
