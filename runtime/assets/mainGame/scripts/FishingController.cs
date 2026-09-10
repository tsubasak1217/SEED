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
/// <b>合わせ＝左クリック（掛かる可能性がある状態でだけ振りを読む）</b>
/// <see cref="UpdateSwingDetection"/> が<b>左クリックの押下</b>を読むのは、掛かる可能性がある
/// 状態（<see cref="FishState.Nibbling"/> / <see cref="FishState.HookWindow"/>）だけに限る。
/// 振りの<b>意味</b>だけが状態ごとに変わる:
/// - Nibbling … 早合わせ（<see cref="HookJudgement.Miss"/>。魚は逃げる）
/// - HookWindow … 合わせ判定（Excellent / Great / Nice / Miss）
///
/// <b>アタリが無いとき（Floating / Reeling）の左クリックは無視する</b>
/// まだ何もアタっていないあいだ（<see cref="FishState.Floating"/> / <see cref="FishState.Reeling"/>）は
/// 左クリックの押下フレームを一切読まない。ウキが跳ねる演出も、それに伴う効果音も、
/// イベント発火も、状態遷移も起こさない（＝入力そのものを無視する）。
/// - Hooked … 振りを読まない（ヒット中の竿振りは未仕様）
///
/// <b>わらしべ連鎖</b>
/// ヒット中（<see cref="FishState.Hooked"/>）は、掛かっている魚そのものが餌になる
/// （<see cref="HookedFishBaitActive"/>）。<see cref="Fish.CanPreyOn"/> でその魚を捕食できる
/// （＝魚レベルが十分高い）個体が寄って来る。<b>前アタリ・合わせは一切無い</b>:
/// 食いつき距離（<see cref="BiteDistance"/>）まで詰めた瞬間に <see cref="TryEatHookedFish"/> を
/// 試み、<b>やり取りの「隙（<see cref="FishingFight.Phase.Rest"/>）」中だけ</b>即座に食い付いて成立する
/// （<see cref="SwapHookedFish"/> で乗り換わり、前の魚は食われて消滅。新しい魚で
/// <see cref="FishingFight.BeginFight"/> からやり取りをやり直す＝通常のヒットと同じ入り口）。
/// 食われた魚は値（<see cref="ChainCatchEntry"/>）で控えておき、釣り上げたときに
/// 掛かった順で全部リザルトへ出して図鑑へ登録する（<see cref="ChainCatchHistory"/>）。
/// 隙以外（出題・回答中）に届いた場合は失敗を返すだけで、魚は興味を失わず
/// 食いつき距離付近でホーミングしながら隙が来るのを待つ（<see cref="Fish"/> 側の実装）。
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
    /// Floating/Reeling --竿を振る（アタリ無し）--> 無視（状態遷移も演出も無し）
    /// HookWindow --niceSeconds 以内に合わせ--> Hooked（Excellent/Great/Nice）
    /// HookWindow --遅い合わせ／時間切れ--> Floating（Miss。魚は逃げる）
    /// Aiming（巻き取り後）--左クリックを離していれば即--> Idle
    ///
    /// ── わらしべ連鎖（掛かっている魚を餌に、より大きい魚が食いに来る）──
    /// 専用の状態遷移は無い。Hooked のまま、より大きい魚が食いつき距離まで詰めた瞬間に
    /// <see cref="TryEatHookedFish"/> を試み、やり取りの「隙（Rest）」中だけ成立する
    /// （成立しても状態は Hooked のまま＝<see cref="SwapHookedFish"/> で魚だけが乗り換わる）。
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
        /// まだ何もアタっていないので、この状態で竿を振っても無視される
        /// （ウキは跳ねず、演出・効果音・状態遷移のいずれも起きない）。
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
        /// <see cref="Floating"/> と同じく、まだ何もアタっていないので竿を振っても無視される。
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
    /// 巻き方向インジケータを出してよい状況か【表示可否の唯一の判断】。
    ///
    /// ヒットしていない（ウキを普通に巻いているだけ）ときは従来どおり常に許可する。
    /// ヒット中は<b>実際に巻ける「隙（<see cref="FishingFight.Phase.Rest"/>）」だけ</b>に絞る
    /// （出題・回答・余白のあいだは巻いても進まないので、方向を示すと誤解を招くため）。
    /// false のあいだは <see cref="UpdateReelArrow"/> が呼ばれないので、
    /// 「このフレームに表示されなかったら格納」の経路が自動でインジケータを隠す。
    /// </summary>
    private bool IsReelArrowAllowed
        => !IsHooked || FightPhase == FishingFight.Phase.Rest;

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

    /// <summary>
    /// <see cref="DebugForceHook"/> が名乗る合わせ判定。
    /// デバッグは「最良の合わせ」から始めたいので Excellent 固定。
    /// </summary>
    private const HookJudgement DebugForceHookJudgement = HookJudgement.Excellent;

    /// <summary>着水直後に自動回収の判定を行わない猶予秒数の既定値（<see cref="landingGraceSeconds"/> の初期値）。</summary>
    private const float DefaultLandingGraceSeconds = 1.0f;

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

    /// <summary>1 回転の角度（度）。方位角の最短回り計算に使う。</summary>
    private const float FullTurnDegrees = 360f;

    /// <summary>半回転の角度（度）。最短回りの折り返しと「真後ろ」の算出に使う。</summary>
    private const float HalfTurnDegrees = 180f;

    /// <summary>smoothstep（3t² − 2t³）の 2 次項の係数。</summary>
    private const float SmoothStepSquareCoefficient = 3f;

    /// <summary>smoothstep（3t² − 2t³）の 3 次項の係数。</summary>
    private const float SmoothStepCubicCoefficient = 2f;

    /// <summary>
    /// <see cref="DebugForceCatch"/> がウキを寄せる距離の、成立距離に対する割合。
    /// 1 未満にして「確実に成立距離の内側」へ置く（浮動小数の誤差で外れないように）。
    /// </summary>
    private const float DebugCatchDistanceRatio = 0.5f;

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
    /// ヒット直後の「引き」演出中（<see cref="FishingFight.Phase.LeadIn"/>）のカメラ目標
    /// トランスフォーム（トップレベルの空アクタ「RunCameraTarget」を割り当てる想定）。
    /// 位置・向きは毎フレーム <see cref="UpdateRunCameraTarget"/> が
    /// 「プレイヤーとウキの両方が画面に収まる斜め上からの構図」へ置き直す。
    /// 未設定ならこの構図は効かない（<see cref="CameraMove"/> 側が従来の目標へフォールバックする）。
    /// </summary>
    [SerializeField(Label = "引き演出のカメラ目標(RunCameraTarget)")]
    private SEED.Transform? runCameraTarget = null;

    /// <summary>
    /// キャスト中のカメラ目標トランスフォーム（ウキの子アクタ「CastCameraTarget」を割り当てる想定）。
    ///
    /// これ自体は <see cref="CameraMove"/> がキャスト中の構図として直接追うもので、
    /// 本スクリプトは<b>読むだけ</b>（位置・向きは一切書き換えない）。
    /// 出題（<see cref="FishingFight.Phase.Call"/>）と巻き取り（<see cref="FishingFight.Phase.Rest"/>）
    /// の構図（<see cref="callCameraTarget"/>）を「同じ向きのままウキからの距離だけ変えた位置」
    /// として算出するための<b>基準</b>に使う。
    /// </summary>
    [SerializeField(Label = "キャスト中のカメラ目標(CastCameraTarget)")]
    private SEED.Transform? castCameraTarget = null;

    /// <summary>
    /// <b>出題（<see cref="FishingFight.Phase.Call"/>）と巻き取り（<see cref="FishingFight.Phase.Rest"/>）</b>
    /// で使うカメラ目標トランスフォーム
    /// （トップレベルの空アクタ「CallCameraTarget」を割り当てる想定）。
    ///
    /// 位置・向きは毎フレーム <see cref="UpdateCallRestCameraTarget"/> が
    /// 「<see cref="castCameraTarget"/> と同じ向きのまま、ウキからの距離だけを
    /// フェーズごとの倍率（<see cref="callCameraDistanceScale"/> ／
    /// <see cref="restCameraDistanceScale"/>）へ変えた位置」へ置き直す。
    /// 未設定ならこの構図は効かない（<see cref="CameraMove"/> 側が従来の目標へフォールバックする）。
    /// </summary>
    [SerializeField(Label = "出題/巻き取り中のカメラ目標(CallCameraTarget)")]
    private SEED.Transform? callCameraTarget = null;

    /// <summary>
    /// 出題中のカメラ距離倍率。ウキから <see cref="castCameraTarget"/> までの距離へ掛ける。
    /// 1 でキャスト中とまったく同じ構図、0.5 で「向きは同じまま距離が半分」＝ウキへ寄る。
    /// </summary>
    [SerializeField(Label = "出題中のカメラ距離倍率")]
    private float callCameraDistanceScale = 0.5f;

    /// <summary>
    /// <b>巻き取り中（隙＝<see cref="FishingFight.Phase.Rest"/>）のカメラ距離倍率</b>
    /// 【巻き取り中の画の広さを決める唯一のパラメータ】。
    /// <see cref="callCameraDistanceScale"/> と同じく、ウキから
    /// <see cref="castCameraTarget"/> までの距離へ掛ける（向きは変えない）。
    /// 1 でキャスト中とまったく同じ構図、1.5 で「向きは同じまま距離が 1.5 倍」＝引いて広く見せる。
    ///
    /// 【2026-09-11 追加】それまで巻き取り中は
    /// <see cref="castCameraTarget"/>（ウキの子アクタ）をそのまま追っていたため、
    /// 距離を触るには<b>キャスト中・浮遊中・未ヒットの巻き取り中と共有のアクタ</b>を
    /// 動かすしかなく、巻き取りだけを調整できなかった。出題中の寄り
    /// （<see cref="callCameraDistanceScale"/>）と同じ仕組みに乗せることで、
    /// 巻き取りの距離だけをインスペクタから独立に調整できるようにした。
    /// </summary>
    [SerializeField(Label = "巻き取り中のカメラ距離倍率")]
    private float restCameraDistanceScale = 1.5f;

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

    /// <summary>飛距離の上限（メートル）。着水点マーカーの往復の上端でもある。</summary>
    [Header("キャスト"), SerializeField(Label = "最長飛距離(m)")]
    private float maxCastDistance = 25f;

    /// <summary>
    /// 最短飛距離を釣り上げ成立距離（<see cref="catchDistanceMeters"/>）から
    /// どれだけ沖へ離すかの余裕（メートル）【最短飛距離を決める唯一のパラメータ】。
    ///
    /// 【なぜ最短飛距離そのものを持たないのか】2026-09-10 改定
    /// 釣り上げは<b>距離だけ</b>で成立する（<see cref="UpdateFight"/>）ため、成立距離より
    /// 近くに着水した状態でヒットすると、やり取りを 1 度もせずに次のフレームで釣れてしまう。
    /// つまり最短飛距離は「成立距離より必ず外側」でなければならず、独立した値として
    /// 置くと成立距離を調整するたびに両方を直す必要がある（＝片方だけ直して破綻する）。
    /// そこで<b>最短飛距離 ＝ 成立距離 ＋ この余裕</b>に一本化した
    /// （<see cref="EffectiveMinCastDistance"/>）。
    /// </summary>
    /// 既定 5.0m: 成立距離 4.0m と合わせて最短 9.0m。最短で投げてもやり取りが数往復は入る。
    [SerializeField(Label = "最短飛距離の余裕(m)")]
    private float minCastMarginBeyondCatch = 5.0f;

    /// <summary>
    /// 実効の最短飛距離（メートル）＝「釣り上げ成立距離（<see cref="catchDistanceMeters"/>）
    /// ＋ <see cref="minCastMarginBeyondCatch"/>」【最短飛距離の唯一の算出点】。
    /// 着水点マーカーの往復・キャストのクランプ・デバッグ投擲のすべてがこれを使う。
    /// </summary>
    private float EffectiveMinCastDistance
        => SEED.Mathf.Max(catchDistanceMeters, 0f) + SEED.Mathf.Max(minCastMarginBeyondCatch, 0f);

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

    /// <summary>
    /// 着水してからこの秒数のあいだは<b>自動回収（巻き取り完了）の判定を一切行わない</b>猶予（秒）
    /// 【短いキャストが着水と同時に回収される事故の防止】。
    ///
    /// 【なぜ必要か】
    /// <see cref="UpdateReeling"/> は <see cref="FishState.Floating"/> の間も毎フレーム走り、
    /// 「ウキ→竿先の残り距離が <see cref="reelEndDistance"/> 以下」または
    /// 「巻く向きが竿先の方向から 90 度以上外れた」時点で <see cref="FinishReeling"/> を呼ぶ。
    /// 短いキャスト・竿先の真横への着水・着水と同時にプレイヤーが動いた場合など、
    /// <b>着水した瞬間に既に条件を満たしている</b>と、プレイヤーが一度も巻かないまま
    /// その場で回収されて狙い（<see cref="FishState.Aiming"/>）へ戻ってしまう。
    /// 着水直後だけ判定を止めれば、少なくともウキが浮いているところを見せられる。
    ///
    /// 0 以下で猶予なし（＝着水した次のフレームから判定する）。
    /// </summary>
    [SerializeField(Label = "着水後の回収猶予(秒)")]
    private float landingGraceSeconds = DefaultLandingGraceSeconds;

    /// <summary>
    /// 釣り上げが成立するウキ→竿先の水平距離（メートル）【ヒット中の完了距離】。
    ///
    /// ヒット中は<b>実測距離がこの値以下になった瞬間</b>に釣り上げが成立する
    /// （<see cref="UpdateFight"/>）。<b>魚 HP は成立条件に入らない</b>
    /// （2026-09-09 改定。HP 0 は「見た目距離の下限が外れて竿先まで一気に寄る」という
    ///  意味だけを持つ）。HUD に出している距離は実測値なので、
    /// <b>表示がこの値に近づいた瞬間に釣れる</b>という見た目と判定の一致が保たれる。
    ///
    /// <b>値を上げるときの注意</b>: 成立が「距離だけ」なので、この値を大きくするほど
    /// 巻き切るまでの距離が短くなる（＝やり取りがそのぶん短くなる）。
    /// また <see cref="hookDistanceMin"/> 相当の至近距離で掛かった場合、
    /// 掛かった次のフレームに成立してしまうので、上げ過ぎないこと。
    ///
    /// <b>既定を 4.0m にしている理由【2026-09-10 改定】</b>:
    /// 岸のすぐ際（1m）まで寄せ切ってから釣り上げると、釣り上げ演出の側面固定カメラ
    /// （<see cref="CatchPresenter"/>）の画に<b>海がほとんど映らず</b>、魚が砂浜から
    /// 跳ね上がったように見えてしまう。成立距離を海側へ寄せると、跳ねる魚の背景に
    /// 必ず海面が入る。<see cref="FishingFight"/> 側の「見た目距離の下限」
    /// （既定 0.5m）より外側であれば、巻き切れば必ずこの距離へ到達できる。
    /// </summary>
    [SerializeField(Label = "釣り上げ成立距離(m)")]
    private float catchDistanceMeters = 4.0f;

    // ─── 岸際（岸に近づいたときの挙動）【2026-09-09 追加】─────────────
    //
    // ウキが竿先（＝プレイヤーが立つ岸）へ近づいたら、次の 3 つを同時に切り替える。
    //   (a) 漂流物を新規に出さない（DriftItemManager が NearShore を見る）
    //   (b) 巻きの方向変更（A / D）を殺してウキを竿先へ直進させる
    //   (c) カメラを「陸側から海を見る」構図（ウキ→竿先の延長線上）へ回り込ませる
    // 3 つとも同じ 1 つの判定（NearShore）から決まるので、閾値の食い違いが起きない。

    /// <summary>
    /// 「岸に近づいた」とみなすウキ→竿先の水平距離（メートル）
    /// 【岸際判定の唯一の閾値】。
    ///
    /// この距離<b>以内</b>に入ったら <see cref="NearShore"/> が立つ。
    /// 立っているあいだの効果は上のコメント (a)〜(c) のとおり。
    /// </summary>
    [Header("岸際"), SerializeField(Label = "岸に近い距離(m)")]
    private float nearShoreDistanceMeters = 6f;

    /// <summary>
    /// 岸際判定を<b>抜ける</b>ときにだけ上乗せする距離（メートル・ヒステリシス幅）。
    ///
    /// 入るのは <see cref="nearShoreDistanceMeters"/> 以内、抜けるのは
    /// 「<see cref="nearShoreDistanceMeters"/> ＋ この値」を超えたとき。
    /// 境界付近でウキが前後するたびに漂流物の出現・操舵・カメラ構図が
    /// パタパタ切り替わるのを防ぐ。0 でヒステリシスなし。
    /// </summary>
    [SerializeField(Label = "岸際判定のヒステリシス幅(m)")]
    private float nearShoreExitMarginMeters = 1.5f;

    /// <summary>
    /// 岸際のカメラが「陸側」へ回り込む速さ（度/秒）【回り込みの唯一の速度】。
    ///
    /// カメラはウキを中心に方位角（<see cref="shoreCamAzimuthDegrees"/>）で回り、
    /// 目標方位（ウキ→竿先の向き＝陸側）へこの速さで近づく。
    /// 大きいほど素早く回り込み、0 にすると回り込まない（＝入った時点の方位で固定）。
    /// </summary>
    [SerializeField(Label = "岸際カメラの回り込み速度(度/秒)")]
    private float shoreCamOrbitSpeedDegPerSec = 70f;

    /// <summary>岸際カメラのウキからの水平距離（メートル）。</summary>
    [SerializeField(Label = "岸際カメラの距離(m)")]
    private float shoreCamDistance = 20f;

    /// <summary>岸際カメラのウキからの高さ（メートル）。</summary>
    [SerializeField(Label = "岸際カメラの高さ(m)")]
    private float shoreCamHeight = 3f;

    // ─── 釣り上げ時のカメラ寄り【2026-09-09 追加】──────────────────
    //
    // 「釣れた判定 → カメラがウキ（魚）へ寄る → 釣り上げ演出」の中段。
    // 演出が始まる<b>前</b>の区間なので、CatchPresenter ではなく本スクリプトが持つ。

    /// <summary>
    /// 釣れた判定の瞬間からカメラがウキへ寄り切るまでの秒数【寄りの唯一の所要時間】。
    ///
    /// この秒数が経ってから <see cref="CatchPresenter.Begin"/> を呼ぶ。
    /// 0 以下なら寄りを行わず、従来どおり即座に演出へ入る。
    /// 評価バナーの読ませ時間（<see cref="fightEvalLeadSeconds"/>）とは
    /// <b>どちらか長いほう</b>が待ち時間になる（両方を足して間延びさせない）。
    /// </summary>
    [Header("釣り上げ時のカメラ寄り"), SerializeField(Label = "寄りの秒数")]
    private float catchZoomSeconds = 0.9f;

    /// <summary>寄り切ったときのカメラのウキからの水平距離（メートル）。</summary>
    [SerializeField(Label = "寄り切ったときの距離(m)")]
    private float catchZoomDistance = 3f;

    /// <summary>寄り切ったときのカメラのウキからの高さ（メートル）。</summary>
    [SerializeField(Label = "寄り切ったときの高さ(m)")]
    private float catchZoomHeight = 1.4f;

    /// <summary>
    /// カメラの追従スクリプト【構図を握るための唯一の参照】。
    ///
    /// 岸際の回り込みと釣り上げの寄りは、シーンに置いた目標アクタではなく
    /// <see cref="CameraMove.SetOverrideGoal"/> へ姿勢そのものを渡して実現する
    /// （目標アクタをシーンへ増やさずに済み、構図の計算がこのスクリプトに閉じる）。
    /// 未設定ならカメラ演出は一切効かない（判定・演出の進行そのものには影響しない）。
    /// </summary>
    [SerializeField(Label = "カメラ(CameraMove)")]
    private CameraMove? cameraMove = null;

    // ─── 岸際・寄りカメラの実行時状態（インスペクタには出さない）───────

    /// <summary>
    /// ウキが岸（竿先）の近くに居るか【岸際の効果すべての唯一の判定結果】。
    /// <see cref="UpdateNearShore"/> がヒステリシス付きで毎フレーム立て直す。
    /// 漂流物マネージャ（<see cref="DriftItemManager"/>）も外からこれを読む。
    /// </summary>
    public bool NearShore { get; private set; } = false;

    /// <summary>
    /// 岸際カメラの現在の方位角（度）。ウキ<b>から見たカメラの向き</b>で、
    /// 目標方位（ウキ→竿先＝陸側）へ <see cref="shoreCamOrbitSpeedDegPerSec"/> で近づく。
    /// </summary>
    private float shoreCamAzimuthDegrees = 0f;

    /// <summary>
    /// いま <see cref="CameraMove.SetOverrideGoal"/> で構図を握っているか
    /// 【上書きの解除を 1 か所に閉じるためのフラグ】。
    /// 握っていないときに <see cref="CameraMove.ClearOverrideGoal"/> を呼ぶと、
    /// 釣り上げ演出（CatchPresenter）が握った構図まで外してしまうため。
    /// </summary>
    private bool cameraOverrideActive = false;

    /// <summary>釣り上げの寄りを進行中か（<see cref="UpdateCatchZoom"/> が進める）。</summary>
    private bool catchZoomActive = false;

    /// <summary>釣り上げの寄りの経過秒数（実時間）。</summary>
    private float catchZoomElapsed = 0f;

    /// <summary>寄りの開始姿勢（判定の瞬間のカメラ位置）。</summary>
    private SEED.Vector3 catchZoomStartPosition = SEED.Vector3.Zero;

    /// <summary>寄りの開始姿勢（判定の瞬間のカメラ回転・度）。</summary>
    private SEED.Vector3 catchZoomStartRotation = SEED.Vector3.Zero;

    /// <summary>寄り切ったときのカメラ位置。</summary>
    private SEED.Vector3 catchZoomGoalPosition = SEED.Vector3.Zero;

    /// <summary>寄り切ったときのカメラ回転（度）。</summary>
    private SEED.Vector3 catchZoomGoalRotation = SEED.Vector3.Zero;

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

    /// <summary>
    /// 着水してからの経過秒数（<see cref="landingGraceSeconds"/> の判定用）。
    /// キャスト開始で 0 に戻し、掛かっていない間（Floating / Reeling）だけ進める。
    /// </summary>
    private float landingElapsed = 0f;

    /// <summary>
    /// 着水してから<b>一度でも巻き入力があったか</b>
    /// 【自動回収を「実際に巻いた結果」に限定するための控え】。
    ///
    /// 着水時点で既にウキが手元に近い場合でも、巻いていないうちは回収しない
    /// （＝回収の判定がキャスト直後の距離に依存しなくなる）。
    /// キャスト開始で false に戻す。
    /// </summary>
    private bool reeledSinceLanding = false;

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
    /// <b>わらしべ連鎖の猶予（秒）</b>【隙に入った直後の連鎖を弾く唯一のパラメータ】。
    ///
    /// 隙（<see cref="FishingFight.Phase.Rest"/>）へ入ってからこの秒数が経つまでは、
    /// <see cref="TryEatHookedFish"/> を成立させない。隙へ入った瞬間に横取りされると
    /// プレイヤーが 1 度も巻けないまま乗り換えが連発してしまうため、
    /// 「隙に入った → 少し巻ける → そこから横取りされ得る」という間を必ず作る。
    ///
    /// 待っている魚は弾かれても回遊へは戻らず、<c>Fish.chainWaitTimeoutSeconds</c> の
    /// 上限まで待ち続けるので、猶予が明けた次のフレームに改めて成立し得る。
    /// 乗り換え直後は新しいやり取りが余白（<see cref="FishingFight.Phase.LeadIn"/>）から
    /// 始まるため、そもそも隙ではなく、この猶予は自動的に効く。
    /// 0 以下にすると猶予なし（従来どおり隙の頭から成立する）。
    /// </summary>
    [SerializeField(Label = "わらしべ連鎖の猶予(秒)")]
    private float chainEatGraceSeconds = 1.5f;

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

    // ─── 合わせ（Nibbling / HookWindow だけが竿振りを読む）───────
    //
    // アタリが無い状態（Floating / Reeling）は竿を振っても無視するため、
    // 空振り演出（ウキの跳ね）とそれ専用のパラメータは廃止した。
    // 合わせの操作は「左クリックの押下」そのものなので、しきい値・時間窓・
    // クールダウンといった調整パラメータは持たない（旧フリック検出は廃止）。

    /// <summary>Excellent と判定される反応時間の上限（秒）。</summary>
    [Header("合わせ"), SerializeField(Label = "Excellent の反応時間(秒)")]
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

    // ─── バトル評価バナー（Perfect! / Good!）─────────────────────
    //
    // ビートバトルで「出題に回答し終えた瞬間」（回答 → 隙 の切り替わり）に、
    // その 1 フレーズの出来を一言で返す。呼ぶのは FishingFight（回答の締め）で、
    // 出す・見せる・引っ込めるは FightEvalBanner の責務。
    // ここが持つのは「どちらの文言を、どの色で出すか」だけ（釣り上げ時には出さない）。

    /// <summary>
    /// 評価バナーのプレハブ（<c>assets://</c> パス）。
    /// シーンに <c>FightEvalBanner</c> のインスタンスを置いていない場合の
    /// 生成フォールバックにだけ使う。空にすると生成できない（＝評価は出ない）。
    /// </summary>
    [Header("バトル評価バナー"), SerializeField(Label = "バナーのプレハブ")]
    private string fightEvalBannerActorPath = "assets://mainGame/actors/UI/FightEvalBanner.actor";

    /// <summary>全判定が Excellent だったときの文言。</summary>
    [SerializeField(Label = "完璧のときの文言")]
    private string fightEvalPerfectLabel = "Perfect!";

    /// <summary>Excellent 以外が混じったときの文言。</summary>
    [SerializeField(Label = "通常のときの文言")]
    private string fightEvalGoodLabel = "Good!";

    /// <summary>
    /// 完璧のときの文字色（16 進カラーコード）。既定は金。
    /// 16 進文字列で持つ理由は <see cref="UiColorUtil"/> のクラスコメントを参照。
    /// </summary>
    [SerializeField(Label = "完璧のときの色(16進)")]
    private string fightEvalPerfectColor = "#FFD54A";

    /// <summary>通常のときの文字色（16 進カラーコード）。既定は白。</summary>
    [SerializeField(Label = "通常のときの色(16進)")]
    private string fightEvalGoodColor = "#4CE07A";

    /// <summary>完璧のときに鳴らす SE（<c>assets://</c> パス。空で無音）。</summary>
    [SerializeField(Label = "完璧のときのSE")]
    private string fightEvalPerfectSePath = "assets://mainGame/audios/beat_battle_perfect.mp3";

    /// <summary>通常評価のときに鳴らす SE（<c>assets://</c> パス。空で無音）。</summary>
    [SerializeField(Label = "通常のときのSE")]
    private string fightEvalGoodSePath = "assets://mainGame/audios/beat_battle.mp3";

    /// <summary>評価 SE の音量（1.0 = 等倍）。</summary>
    [SerializeField(Label = "評価SEの音量")]
    private float fightEvalSeVolume = 0.9f;

    /// <summary>
    /// 評価バナーを出してから釣り上げ演出（ホワイトアウト）を始めるまでの秒数。
    ///
    /// 0 にすると評価と同時に白へ飛び込むため、評価がほとんど読めない。
    /// かといって長く取ると釣れた手応えが遅れるので、
    /// 「読めるが待たされない」ぎりぎりの短い間だけ空ける。
    /// この間はゲームの見た目が止まらない（ウキも糸もそのまま）ので、
    /// 実時間（<c>Time.UnscaledDeltaTime</c>）で数える。
    /// </summary>
    [SerializeField(Label = "釣り上げ確定から演出開始までの秒数")]
    private float fightEvalLeadSeconds = 0f;

    /// <summary>
    /// 評価バナーを出したあと、釣り上げ演出の開始を待っている魚。
    /// <c>null</c> なら待ちは無い。<see cref="FloatWorldPosition"/> は待っている間に
    /// 変わり得るので、位置も <see cref="pendingCatchFloatPosition"/> へ控えておく。
    /// </summary>
    private Fish? pendingCatchFish = null;

    /// <summary>釣り上げが決まった瞬間のウキのワールド位置（演出の「水面の基準点」）。</summary>
    private SEED.Vector3 pendingCatchFloatPosition = default;

    /// <summary>釣り上げ演出を始めるまでの残り秒数（実時間）。0 以下で開始する。</summary>
    private float pendingCatchDelaySeconds = 0f;

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
    /// ヒット直後に「Lv◯ 魚名 ／ HIT!!!」の帯を出す演出スクリプト。
    /// FishingUI の子アクタ「HitBanner」に置き、ここへ割り当てる。
    ///
    /// <b>未設定でも釣りは成立する</b>（演出が出ないだけ）。
    /// </summary>
    [SerializeField(Label = "ヒット演出(HitBanner)")]
    private HitBanner? hitBanner = null;

    /// <summary>
    /// 魚がウキを沖へ引ける限界の余裕（メートル）。
    ///
    /// 竿先からの水平距離の上限を
    /// <c>max(いま目指している距離, <see cref="maxCastDistance"/>) ＋ この値</c> とする
    /// （魚に無限に引かれてウキが世界の外へ行かないための安全弁）。
    ///
    /// 【2026-09-11 改定】以前は <see cref="maxCastDistance"/> ＋ この値の<b>固定上限</b>だったが、
    /// 漂流物「魚回復」で魚 HP が最大値を超えたときの目標距離
    /// （<c>FishingFight.DesiredFloatDistance</c>・上限なしで線形に伸びる）に届かず、
    /// 拾っても距離が HP 100% の位置で止まっていた。目標距離のほうが大きいときは
    /// そちらを基準にするので、この値は「目標より行き過ぎてよい量」という意味になった。
    /// </summary>
    [SerializeField(Label = "引きの限界余裕(m)")]
    private float floatDragMarginDistance = 30f;

    // ─── 魚レーダー（着水中とヒット中に出す） ───────────────────

    /// <summary>
    /// 画面へ出す魚レーダー（<see cref="FishRadar"/>）。
    /// 未設定でも釣りは成立する（レーダーが出ないだけ）。
    /// </summary>
    [Header("魚レーダー"), SerializeField(Label = "魚レーダー(FishRadar)")]
    private FishRadar? radar = null;

    /// <summary>
    /// 着水してからレーダーが出るまでの待ち時間（秒）。
    /// 投げた直後にいきなり出さず、水面が落ち着いてからフェードインさせるための間。
    /// ヒット中はこの待ちを無視して出し続ける（<see cref="UpdateFishRadar"/>）。
    /// フェードそのものの秒数は <see cref="FishRadar"/> 側のインスペクタが持つ。
    /// </summary>
    [SerializeField(Label = "レーダーが出るまでの待ち(秒)")]
    private float radarAppearDelaySeconds = 2f;

    /// <summary>
    /// 掛かっている魚から見て「これ以上レベルが離れたら相手にしない」差
    /// （<see cref="Fish.Level"/> の差）。
    ///
    /// ヒット中、レベルが「掛かっている魚のレベル ＋ この値」以上の個体は
    /// <b>AI・移動の更新も描画も止める</b>（<see cref="Fish"/> 側のカリング）。
    /// どうやっても乗り換えられない格上なので、沖合の大量の魚を丸ごと処理から外せる。
    /// レーダーの不透明度とは無関係（そちらは捕食可否だけで決まる）。
    /// </summary>
    [SerializeField(Label = "更新を止めるレベル差")]
    private int radarSkipLevelGap = 3;

    // ─── 引き演出のカメラ（LeadIn 中に <see cref="runCameraTarget"/> を置く構図）───
    // 中点（プレイヤーとウキの中点）を球面座標（方位角θ・仰角φ・距離）で見る構図。
    // 基準方向は「プレイヤー→ウキ」の水平方向（dirH）：
    //   θ=0°   … ウキ側（dirH 方向）から中点越しにプレイヤーを見返す。
    //   θ=180° … プレイヤー側（-dirH 方向）から中点越しに海（ウキ）を見る。
    //   θ=90°  … 糸に対して右側（right = (dirH.z, 0, -dirH.x)）から見る。
    //   φ=0°   … 水平、φ=90° … 中点の真上。

    /// <summary>
    /// ヒット時カメラの方位角θ（度）。プレイヤー→ウキの水平方向（dirH）を 0° として、
    /// dirH × cosθ + right × sinθ が水平オフセット方向になる（right は dirH を右へ 90° 回した方向）。
    /// </summary>
    [Header("引き演出のカメラ"), SerializeField(Label = "ヒット時カメラの方位角θ(度)")]
    private float runCamThetaDegrees = 180f;

    /// <summary>
    /// ヒット時カメラの仰角φ（度）。0°で水平、90°で中点の真上からの見下ろしになる。
    /// </summary>
    [SerializeField(Label = "ヒット時カメラの仰角φ(度)")]
    private float runCamPhiDegrees = 35f;

    /// <summary>
    /// ヒット時カメラの基本距離（メートル）。中点からカメラまでの実効距離の基準値
    /// （実効距離 ＝ この値 ＋ 間隔 × <see cref="runCamDistancePerSeparation"/>）。
    /// </summary>
    [SerializeField(Label = "ヒット時カメラの距離(m)")]
    private float runCamDistance = 8f;

    /// <summary>
    /// プレイヤー→ウキの間隔に応じてカメラ距離を伸ばす比率。
    /// 間隔が開くほどカメラも遠くなり、プレイヤーとウキの両方が画面に収まりやすくなる。
    /// </summary>
    [SerializeField(Label = "距離に加える間隔比率")]
    private float runCamDistancePerSeparation = 0.6f;

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
    /// 効果音のアセットパス。空文字なら鳴らさない。振りを読むのは
    /// 掛かる可能性がある状態（Nibbling / HookWindow）だけなので、効果音もそこでしか鳴らない
    /// （Floating / Reeling は竿振り自体を無視するため対象外）。
    /// </summary>
    [SerializeField(Label = "竿振りの効果音")]
    private string swingSePath = "";

    /// <summary>竿振り効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "竿振りの音量")]
    private float swingSeVolume = 1f;

    // ─── スタン演出（隙フェーズ） ───────────────────────────

    /// <summary>
    /// 「隙（<see cref="FishingFight.Phase.Rest"/>）」のあいだ、魚の頭上で星を回す演出。
    /// 専用アクタ「StunEffect」のスクリプトスロットを割り当てる（未設定なら演出なし）。
    /// </summary>
    [Header("スタン演出"), SerializeField(Label = "スタン演出(StunEffect)")]
    private StunEffect? stunEffect = null;

    /// <summary>
    /// 星を回す中心を、魚の頭のてっぺんからさらに何メートル上へ置くか。
    /// </summary>
    [SerializeField(Label = "星の追加高さ(m)")]
    private float stunStarHeightOffset = 0.3f;

    /// <summary>
    /// 魚の高さが測れない（Model 無し・モデル未ロード）ときに使う代替の高さ（m）。
    /// </summary>
    [SerializeField(Label = "魚の高さの代替値(m)")]
    private float stunFallbackFishHeight = 0.5f;

    /// <summary>
    /// スタン演出を出しているか【<see cref="UpdateStunEffect"/> が持つ唯一の状態】。
    /// 「畳んでいた → 出す」の立ち上がりを検出して <see cref="StunEffect.Show"/> を
    /// 呼ぶタイミングを 1 回に絞るために使う（効果音自体は StunEffect 側が管理）。
    /// </summary>
    private bool stunShown = false;

    /// <summary>
    /// 現在掛かっている魚（null = 掛かっていない）。
    /// <see cref="TryHook"/> で束縛し、釣り上げ・リリース・キャンセルで必ず解除する。
    /// </summary>
    private Fish? hookedFish = null;

    /// <summary>
    /// <b>わらしべ連鎖で餌になった魚</b>の控え（最初に掛かった魚から順）
    /// 【連鎖履歴の唯一の置き場】。
    ///
    /// 乗り換え（<see cref="SwapHookedFish"/>）のたびに、食べられる<b>前</b>の魚を
    /// 値（<see cref="ChainCatchEntry"/>）で積む。魚の実体はその直後に破棄されるので、
    /// 参照ではなく値でなければ釣り上げ演出まで残らない。
    ///
    /// 釣り上げると、この履歴＋最後の 1 匹が順番に釣果パネルへ出て、
    /// それぞれ図鑑へ登録される（<see cref="CatchPresenter.Begin"/>）。
    /// 新しく魚を掛けた瞬間と、逃げられた（<see cref="ReleaseHook"/>）瞬間に空になる。
    /// </summary>
    private readonly System.Collections.Generic.List<ChainCatchEntry> chainCatchHistory = new();

    // ─── アタリ／合わせの内部状態 ─────────────────────────────

    /// <summary>いま前アタリ〜本アタリを起こしている魚（null = アタリ進行中でない）。</summary>
    private Fish? nibblingFish = null;

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

    // ─── ポーズメニュー ───────────────────────────────────────

    /// <summary>
    /// ポーズメニューのプレハブ（assets:// パス）。
    ///
    /// Esc を拾ったときに <see cref="PauseMenu.Toggle"/> へ渡す。
    /// メニュー本体はこのパスのプレハブが初回だけ動的生成されるので、
    /// シーンへポーズメニューを置いておく必要はない。
    /// </summary>
    [Header("ポーズメニュー"), SerializeField(Label = "メニューのprefab")]
    private string pauseMenuActorPath = "assets://mainGame/actors/UI/PauseMenu.actor";

    // ─── 巻き取り音（ループ素材）───────────────────────────
    //
    // 竿を巻いている間だけ鳴らす。素材はループ前提なので AudioSource（AudioComponent）で
    // Play / Stop を切り替える。シーンに音源を置かなくて済むよう、音源だけを持つ
    // プレハブ（ReelSound.actor）を OnStart で生成して抱える。

    /// <summary>
    /// 巻き取り音のプレハブ（<c>assets://</c> パス）。ループ設定済みの AudioSource を
    /// 1 つ持つアクタ。空にすると巻き取り音は鳴らない。
    /// </summary>
    [Header("巻き取り音"), SerializeField(Label = "音源のprefab")]
    private string reelSoundActorPath = "assets://mainGame/actors/FX/ReelSound.actor";

    /// <summary>
    /// 巻き入力が途切れてから音を止めるまでの猶予（秒）。
    /// ホイール入力はフレームごとに 0 と正の値が交互に来るため、猶予無しだと
    /// 音が細かく途切れる。「まだ巻いている」とみなす短い間だけ鳴らし続ける。
    /// </summary>
    [SerializeField(Label = "止めるまでの猶予(秒)")]
    private float reelSoundHoldSeconds = 0.2f;

    /// <summary>
    /// 巻き取り音アクタに付ける目印の名前（<see cref="SpawnOnce"/> の照合キー）。
    /// プレハブ（ReelSound.actor）のルート名と同じにしてあるので、
    /// 既存の生成物・シーンへ手で置いたものも同じ名前で拾える。
    /// </summary>
    private const string ReelSoundActorName = "ReelSound";

    /// <summary>
    /// 「ゲーム時間が止まっている」とみなす <see cref="SEED.Time.Scale"/> の閾値。
    /// スロー演出（0 より大きい Scale）では音を止めたくないので、完全停止だけを拾う。
    /// </summary>
    private const float TimeStoppedScaleEpsilon = 0.0001f;

    /// <summary>巻き取り音のアクタ（OnStart で用意。取得失敗時は無効）。</summary>
    private SEED.GameObject reelSoundActor;

    /// <summary>巻き取り音の音源（生成の翌フレーム以降に遅延取得する）。</summary>
    private SEED.AudioSource? reelSoundSource = null;

    /// <summary>最後に巻き入力があってからの経過秒（実時間）。</summary>
    private float sinceLastReelInput = float.MaxValue;

    /// <summary>
    /// 生成直後の初期化。糸をワールド座標系（親子合成なし）で扱う設定にし、初期状態は非表示にする。
    /// 参照フィールドはこの時点で注入済みだが、参照先スクリプトの OnStart 完了は保証されない。
    /// </summary>
    public override void OnStart()
    {
        // 動的生成される魚から参照できるよう、自分を静的アクセサへ登録する。
        Current = this;

        // ポーズの静的状態はシーン遷移で作り直されないので、シーン開始時に必ず戻す
        // （前のシーンでポーズしたまま遷移した場合に、操作不能で始まるのを防ぐ）。
        PauseMenu.ResetStaticState();
        // 巻き取り音の音源を用意しておく（音源の取得は次フレーム以降に UpdateReelSound が行う）。
        // SpawnOnce 経由にして、既に同名の音源アクタがあれば使い回す。
        // スクリプトのホットリロードで OnStart が再実行されても音源が増えない
        //（増えると同じループ音が重なって鳴り、フレーム時間も伸びる）。
        if (!string.IsNullOrWhiteSpace(reelSoundActorPath))
        {
            reelSoundActor = SpawnOnce.GetOrInstantiate(ReelSoundActorName, reelSoundActorPath);
        }

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

        // 開発用のデバッグコマンドを登録する（エディタ／MCP から叩ける）。
        // パッケージ版（配布ビルド）では登録そのものを行わない。
        // ＝ 製品では seed_script_debug で釣果の全消しなどが一切通らない。
        debugCommandsRegistered = SEED.Application.IsDebugAllowed;
        if (debugCommandsRegistered)
        {
            SEED.Debug.OnCommand(DebugCommandCatchTest, HandleCatchTestCommand);
            SEED.Debug.OnCommand(DebugCommandRecordsReset, HandleRecordsResetCommand);
            SEED.Debug.OnCommand(DebugCommandHitTest, HandleHitTestCommand);
        }
    }

    /// <summary>
    /// 破棄直前の後始末。掛かっている魚を解放し、静的アクセサの参照を落とす。
    /// 別インスタンスが既に登録済みなら上書きしない（自分の分だけ取り消す）。
    /// </summary>
    public override void OnDestroy()
    {
        // 破棄したスクリプトのハンドラが呼ばれ続けないよう、必ず外す。
        // 登録したときだけ外す（登録・解除を対称にして、無登録の解除を呼ばない）。
        if (debugCommandsRegistered)
        {
            debugCommandsRegistered = false;
            SEED.Debug.OffCommand(DebugCommandCatchTest, HandleCatchTestCommand);
            SEED.Debug.OffCommand(DebugCommandRecordsReset, HandleRecordsResetCommand);
            SEED.Debug.OffCommand(DebugCommandHitTest, HandleHitTestCommand);
        }
        StopReelSound();
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

    /// <summary>
    /// ウキのワールド位置【状態に依らず必ず現在値を返す】。
    /// <see cref="BaitPosition"/> は「餌として有効なときだけ意味がある」値なので、
    /// ヒット中も含めて位置だけが欲しい用途（漂流物の出現中心・巻き込み判定）はこちらを使う。
    /// ウキ未設定なら原点を返す。
    /// </summary>
    public SEED.Vector3 FloatWorldPosition
        => uki is { IsValid: true } floatTf ? floatTf.Position : SEED.Vector3.Zero;

    /// <summary>
    /// 竿先のワールド位置（＝プレイヤーが立つ岸側の基準点）。
    /// 漂流物の出現位置が岸に寄りすぎないかの判定に使う。
    /// </summary>
    public SEED.Vector3 RodTipWorldPosition => RodTipPosition();

    /// <summary>
    /// いま掛かっている魚（掛かっていなければ null）。
    /// チュートリアルの台本が「掛かっている魚のそばに別の魚を出す」ために位置を読む。
    /// </summary>
    public Fish? HookedFish => hookedFish;

    /// <summary>
    /// わらしべ連鎖で餌になった魚の控え（最初に掛かった順・読み取り専用）。
    /// いま掛かっている魚は<b>含まない</b>（あちらは <see cref="HookedFish"/>）。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<ChainCatchEntry> ChainCatchHistory
        => chainCatchHistory;

    /// <summary>
    /// やり取り（リズム勝負）の本体。UI やチュートリアルが状態を読むための参照。
    /// シーンで未設定なら null。
    /// </summary>
    public FishingFight? Fight => fight;

    /// <summary>
    /// 直前に釣り上げた魚（釣果演出中のあいだ有効。演出が終わっても値は残る）。
    /// <see cref="FishingEvents.Catch"/> は表示名しか運ばないので、
    /// 「どの種類を釣ったか」を知りたい購読側はこの魚と
    /// <see cref="FishManager.PrefabPathOf"/> を突き合わせる。
    /// </summary>
    public Fish? LastCaughtFish { get; private set; }

    /// <summary>
    /// チュートリアルの「糸切れで仕切り直し」が発動した回数。
    /// ミッション側が「何度やり直したか」を表示するために読む
    /// （仕切り直しでは fishing.line_break を飛ばさないので、代わりの手がかりになる）。
    /// </summary>
    public int TutorialFightRestartCount { get; private set; }

    /// <summary>
    /// 巻き取りの操舵角（度）。0 が「ウキ → 竿先」の基準方向、正が左・負が右。
    /// チュートリアルが「どちら向きに巻いているか」を判定するのに使う。
    /// </summary>
    public float ReelAngleOffsetDegrees => reelAngleOffsetDegrees;

    /// <summary>
    /// いま巻いたときにウキが進む水平方向（正規化。決められないときは長さ 0）。
    ///
    /// 「ウキ → 竿先」の水平方向を <see cref="ReelAngleOffsetDegrees"/> だけ回したもので、
    /// チュートリアルが「巻いた先へ必ず漂流物を流す」ために出現位置を作るのに使う。
    /// </summary>
    public SEED.Vector3 ReelAimDirection
    {
        get
        {
            var toRod = RodTipWorldPosition - FloatWorldPosition;
            var flat  = new SEED.Vector3(toRod.x, 0f, toRod.z);
            if (flat.SqrMagnitude < SqrEpsilon) { return SEED.Vector3.Zero; }

            float yaw = SEED.Mathf.Atan2(flat.x, flat.z) * SEED.Mathf.Rad2Deg + reelAngleOffsetDegrees;
            float yawRad = yaw * SEED.Mathf.Deg2Rad;
            return new SEED.Vector3(SEED.Mathf.Sin(yawRad), 0f, SEED.Mathf.Cos(yawRad));
        }
    }

    /// <summary>
    /// 掛かっている魚のレベル（1 始まり）。掛かっていない／レベル不明なら
    /// <see cref="Fish.UnknownLevel"/>。魚側の格上カリングの基準になる。
    /// </summary>
    public int HookedFishLevel => hookedFish is { } fish ? fish.Level : Fish.UnknownLevel;

    /// <summary>
    /// 「掛かっている魚のレベル ＋ この値」以上の魚は更新・描画を止める、そのレベル差。
    /// 魚（<see cref="Fish"/>）側のカリング判定が読む。
    /// </summary>
    public int RadarSkipLevelGap => radarSkipLevelGap;

    // ─── わらしべ連鎖（掛かっている魚が次の餌になる）───────────

    /// <summary>
    /// <b>掛かっている魚が餌として有効か</b>【連鎖の成立条件の唯一の定義】。
    ///
    /// ヒット中（<see cref="FishState.Hooked"/>）で魚が掛かっているあいだずっと true。
    /// 前アタリの排他制御は無い（複数匹が同時に寄って来てよい）。実際に食えるのは
    /// <see cref="TryEatHookedFish"/> が「隙（Rest）」中の 1 匹だけを通す。
    /// </summary>
    public bool HookedFishBaitActive
        => State == FishState.Hooked && hookedFish is not null;

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
    /// 指定の魚が、いまアタリの主（前アタリ）としてコントローラに保持されているか。
    ///
    /// 魚側（<see cref="Fish.BehaviorState.Nibbling"/>）が「自分はまだつつき中か」を
    /// 確かめるために使う。状態の列挙（BaitActive など）で代用せず、
    /// 保持している参照そのもので判定する。
    /// </summary>
    /// <param name="fish">確かめる魚。</param>
    /// <returns>アタリの主なら true。</returns>
    public bool IsNibbling(Fish fish)
        => ReferenceEquals(nibblingFish, fish);

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

        // 新しい 1 匹が掛かった ＝ 連鎖はここから数え直す【履歴を捨てる場所 その2】。
        // 前回の釣り上げ・糸切れで消し損ねた控えが残っていても、ここで必ず断ち切れる。
        chainCatchHistory.Clear();

        // ヒット中はマウスの振りを読まないのでカーソルロックを引き直す（解除される）。
        UpdateCursorLock();
        CrossFadeBoth(hookedClip, playerHookedClip);
        // やり取り（テンションゲージ・糸 HP・魚 HP）を開始する。
        // 初期ゲージは直前に確定した合わせ判定（LastJudgement）で決まり、
        // 掛かった瞬間の距離は「魚 HP 1 あたりの距離」の基準になる。
        fight?.BeginFight(fish, LastJudgement, CurrentFloatDistance());
        // ヒット演出（帯＋「Lv◯ 魚名」「HIT!!!」）を出す。
        // 演出の総尺は余白（LeadIn）より短く作ってあるので、やり取りの進行は妨げない。
        ShowHitBanner(fish);
        SEED.Debug.Log($"[Fishing] ヒット! {fish.DisplayName}（サイズ {Fish.FormatSize(fish.DisplaySize)}）");
        return true;
    }

    // ─── デバッグ用の強制ヒット ───────────────────────────────

    /// <summary>
    /// 【デバッグ用】いまの状態に関わらず、指定の魚を強制的に掛ける
    /// 【デバッグからのヒットの唯一の入口】。
    ///
    /// <b>手順</b>
    /// <list type="number">
    ///   <item>ウキが水上に無ければ（<see cref="BaitActive"/> が false なら）
    ///         竿先から <paramref name="distanceMeters"/> 前方の水面へ強制着水させる
    ///         （<see cref="DebugForceLanding"/>）</item>
    ///   <item>合わせ成立（<see cref="JudgeHook"/> のヒット側）とまったく同じ外形で掛ける</item>
    /// </list>
    /// ＝ここを通した結果は本番のヒットと同じになる（バナー・SE・イベント・やり取りの開始）。
    ///
    /// 既にヒット中なら何もしない（掛かっている魚の乗り換えは
    /// <see cref="TryEatHookedFish"/> の仕事なので、ここでは扱わない）。
    /// </summary>
    /// <param name="fish">掛ける魚。</param>
    /// <param name="distanceMeters">
    /// ウキが水上に無いときに着水させる、竿先からの水平距離（メートル）。
    /// <see cref="EffectiveMinCastDistance"/>〜<see cref="maxCastDistance"/> にクランプする。
    /// </param>
    /// <returns>
    /// 掛かったら true。パッケージ版（配布ビルド）では何もせず false を返す
    /// （＝「掛けられなかった」ときと同じ戻り値なので、呼び出し側の後始末はそのまま働く）。
    /// </returns>
    public bool DebugForceHook(Fish fish, float distanceMeters)
    {
        // パッケージ版ではデバッグ入口を塞ぐ（ゲーム状態には一切触れない）
        if (!SEED.Application.IsDebugAllowed) { return false; }

        if (IsHooked)
        {
            SEED.Debug.LogWarning("[Fishing] DebugForceHook: 既にヒット中のため何もしない");
            return false;
        }

        // 1) ウキが水上に無ければ「着水済み（Floating）」を作ってから掛ける
        if (!BaitActive && !DebugForceLanding(distanceMeters)) { return false; }

        // 2) 合わせ成立と同じ外形（JudgeHook のヒット側）を再現する。
        //    判定イベント → 内部判定の確定 → 判定画像 → ヒット SE の順は本物と同じ。
        SEED.Events.Raise(FishingEvents.HookJudged, DebugForceHookJudgement.ToString());
        ClearBiteTiming();
        LastJudgement = DebugForceHookJudgement;
        ShowJudgement(DebugForceHookJudgement);
        PlaySe(hookSePath, hookSeVolume);

        if (!TryHook(fish))
        {
            // 餌が無効化された等の例外。魚を逃がして待機へ戻す（JudgeHook と同じ後始末）。
            SEED.Debug.LogWarning("[Fishing] DebugForceHook: TryHook が拒否したため中止した");
            State = FishState.Floating;
            fish.ReleaseFromHook();
            return false;
        }

        fish.OnHooked();
        SEED.Debug.Log(
            $"[Fishing] DebugForceHook: {fish.DisplayName}（Lv{fish.Level}）を強制的に掛けた"
          + $"（判定 {DebugForceHookJudgement}）");
        return true;
    }

    /// <summary>
    /// 【デバッグ用】ヒット中の魚をその場で釣り上げる【強制釣り上げの唯一の入口】。
    ///
    /// <b>やること</b>は「ウキを釣り上げ成立距離の内側へ置いて、通常の釣り上げ経路
    /// （<see cref="FinishReeling"/>）へ入る」だけ。判定・カメラの寄り・釣り上げ演出・
    /// 図鑑登録・各種イベントはすべて本番と同じ道を通る（デバッグ専用の分岐を作らない）。
    ///
    /// ウキの置き先は「竿先から、いまウキが居る向きへ
    /// <see cref="catchDistanceMeters"/> × <see cref="DebugCatchDistanceRatio"/>」の水面上。
    /// 向きが定まらない（ウキが竿先に重なっている）ときはプレイヤーの正面へ置く。
    ///
    /// ヒットしていないときは何もせず false を返す
    /// （呼び出し側＝<c>DebugCommands</c> が従来の強制ヒットへ回す）。
    /// </summary>
    /// <returns>
    /// 釣り上げ処理へ入ったら true。パッケージ版（配布ビルド）では何もせず false を返す
    /// （＝「受け付けられなかった」ときと同じ戻り値で、呼び出し側の分岐は変わらない）。
    /// </returns>
    public bool DebugForceCatch()
    {
        // パッケージ版ではデバッグ入口を塞ぐ（ゲーム状態には一切触れない）
        if (!SEED.Application.IsDebugAllowed) { return false; }

        if (State != FishState.Hooked || hookedFish is null)
        {
            SEED.Debug.LogWarning("[Fishing] DebugForceCatch: ヒット中ではないため何もしない");
            return false;
        }

        if (uki is { IsValid: true } floatTf)
        {
            var rodTip = ReelTargetPosition();
            float dx = floatTf.Position.x - rodTip.x;
            float dz = floatTf.Position.z - rodTip.z;
            float horizontal = SEED.Mathf.Sqrt(dx * dx + dz * dz);

            float dirX;
            float dirZ;
            if (horizontal > DivideEpsilon)
            {
                dirX = dx / horizontal;
                dirZ = dz / horizontal;
            }
            else
            {
                // ウキが竿先に重なっている: プレイヤーの正面（沖側）を向きとして使う。
                float yawRadians = transform.Rotation.y * SEED.Mathf.Deg2Rad;
                dirX = SEED.Mathf.Sin(yawRadians);
                dirZ = SEED.Mathf.Cos(yawRadians);
            }

            float goalDistance = SEED.Mathf.Max(catchDistanceMeters, 0f) * DebugCatchDistanceRatio;
            SetFloatPosition(new SEED.Vector3(
                rodTip.x + dirX * goalDistance,
                FloatSurfaceY(),
                rodTip.z + dirZ * goalDistance));
        }

        SEED.Debug.Log($"[Fishing] DebugForceCatch: {hookedFish.DisplayName} を強制的に釣り上げる");
        FinishReeling();
        return true;
    }

    /// <summary>
    /// 【デバッグ用】竿先から指定距離だけ前方の水面へ、ウキを強制的に着水させる
    /// 【強制着水の唯一の実装】。
    ///
    /// 進行中の釣り（飛翔・アタリ・やり取り）を <see cref="CancelToIdle"/> で畳んでから
    /// 釣り姿勢へ入れ直し、<see cref="UpdateFlight"/> の着水処理と同じ後始末を行って
    /// <see cref="FishState.Floating"/> を作る。
    ///
    /// 釣り姿勢へ入れない（＝経路移動モードでない）ときは、毎フレームの更新が
    /// 「待機以外なのに釣り姿勢でない」状態を見つけて問答無用で畳んでしまうため、
    /// 何もせず false を返す。
    /// </summary>
    /// <param name="distanceMeters">竿先からの水平距離（メートル）。キャスト距離の範囲へクランプする。</param>
    /// <returns>着水させられたら true。</returns>
    private bool DebugForceLanding(float distanceMeters)
    {
        // 進行中の釣りをすべて畳む（ウキ・糸・やり取り・アタリ・巻き取り音を初期化する）
        CancelToIdle();

        // 釣り姿勢へ入れ直す（catch_test と同じ前提。ここを飛ばすと次のフレームで畳まれる）
        if (playerMove is not { } pm || !pm.EnterFishingStance())
        {
            SEED.Debug.LogWarning(
                "[Fishing] DebugForceHook: 釣り姿勢へ入れないため中止（経路移動モードで実行すること）");
            return false;
        }

        // 着水点＝竿先からプレイヤーの向き（Yaw）へ distance だけ進んだ水面上の一点。
        // 距離は通常のキャストと同じ範囲へクランプして、糸・カメラの前提を崩さない。
        float yaw = transform.Rotation.y;
        float distance = SEED.Mathf.Clamped(distanceMeters, EffectiveMinCastDistance, maxCastDistance);
        var landing = LandingPoint(distance, yaw);

        // ウキを出して着水点へ置く。向きは StartCast と同じく沖側へ揃える
        // （ウキの子アクタ CastCameraTarget が親の回転を継承してカメラの向きを決めるため）。
        ShowFloat();
        SetFloatPosition(landing);
        if (uki is { IsValid: true } floatTf)
        {
            floatTf.Rotation = new SEED.Vector3(0f, yaw + floatYawOffsetDegrees, 0f);
        }

        // UpdateFlight の着水処理と同じ後始末（自動回収の猶予・巻き取りの操舵・レーダーの待ち）
        castDistance = distance;
        flightElapsed = 0f;
        reelAngleOffsetDegrees = 0f;
        reelIdleElapsed = 0f;
        landingElapsed = 0f;
        reeledSinceLanding = false;
        radarVisibleElapsed = 0f;

        State = FishState.Floating;
        HideCastPreview();
        // 直前に PlayerMove.EnterFishingStance が本体アニメを触っているのでラッチを捨てる
        ResetPlayerClipLatch();
        CrossFadeBoth(floatClip, playerFloatClip);
        PlaySe(splashSePath, splashSeVolume);
        // マウスの振りを読まない区間なのでカーソルロックを引き直す
        UpdateCursorLock();
        // 着水した（通常のキャストと同じくチュートリアル等が購読する）
        SEED.Events.Raise(FishingEvents.Land);
        SEED.Debug.Log($"[Fishing] DebugForceHook: 竿先から {distance:F1}m 前方へ強制着水");
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
        // 餌が水にあり、かつ「まだ誰もアタっていない」ときだけ受け付ける
        // （ヒット中＝わらしべ連鎖は前アタリを経由しない。<see cref="TryEatHookedFish"/> を参照）。
        if (State is not (FishState.Floating or FishState.Reeling)) { return false; }
        if (!BaitActive || IsHooked || nibblingFish is not null) { return false; }

        nibblingFish = fish;
        State = FishState.Nibbling;
        LastJudgement = HookJudgement.None;
        RollNibbleSequence();

        // 前アタリ（コツコツ）が始まった
        SEED.Events.Raise(FishingEvents.Nibble);
        SEED.Debug.Log($"[Fishing] 前アタリ開始: {fish.DisplayName}（{nibbleRemaining} 回）");
        return true;
    }

    /// <summary>
    /// 前アタリの回数と最初の間隔を抽選し、アタリ用のタイマ類を初期化する。
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
    /// 掛かっている魚を、わらしべで食いついた魚へ<b>乗り換える</b>【わらしべ成立の唯一の出口】。
    ///
    /// 元の魚は食われた扱いで破棄する（<see cref="FishManager"/> は生存チェックで
    /// 欠けた枠を自動的に補充するので、個体数は勝手に戻る）。
    /// 新しい魚を掛け直し、やり取りは一度畳んでから新しい魚のパラメータで開始する
    /// （＝ゲージ・糸 HP・スタミナは新しい魚基準でリセットされ、
    ///   初期ゲージは <paramref name="judgement"/> で決まる。通常のヒットと完全に同じ入口）。
    /// </summary>
    /// <param name="newFish">新しく掛かる魚（掛かっている魚を食べた魚）。</param>
    /// <param name="judgement">新しいやり取りの初期ゲージに使う判定（わらしべは常に Excellent）。</param>
    private void SwapHookedFish(Fish newFish, HookJudgement judgement)
    {
        if (hookedFish is not { } eaten) { return; }

        SEED.Debug.Log(
            $"[Fishing] わらしべ成立: {newFish.DisplayName}（{Fish.FormatSize(newFish.DisplaySize)}）が"
          + $" {eaten.DisplayName}（{Fish.FormatSize(eaten.DisplaySize)}）を食べた");

        // 連鎖の履歴へ「餌になった魚」を積む【履歴を積む唯一の場所】。
        // 実体はこの直後に破棄されるので、必ず<b>破棄より前</b>に値で控える。
        // 釣り上げたときに、この順（最初に掛かった魚 → … → 最後の魚）で釣果を見せる。
        chainCatchHistory.Add(ChainCatchEntry.From(eaten));

        // 食われた魚: AI を止め、円環クランプの除外登録を外してから破棄する
        // （登録解除は破棄より前に行う。破棄処理中のシーンアクセスは保証されないため）
        eaten.OnCaught();
        UnregisterEngaged(eaten.Actor);
        eaten.Actor.Destroy();

        // 新しい魚を掛け直す
        hookedFish = newFish;
        newFish.OnHooked();
        State = FishState.Hooked;

        // やり取りを畳んでから、新しい魚で開始し直す（通常のヒットと同じ入口: LeadIn からやり直す）
        if (fight is { } f)
        {
            f.EndFight();
            f.BeginFight(newFish, judgement, CurrentFloatDistance());
        }

        // ヒット用クリップを引き直し（既に同じクリップならラッチで間引かれる）、カーソルロックも同期
        CrossFadeBoth(hookedClip, playerHookedClip);
        UpdateCursorLock();

        // 通常のヒットと同じくヒット演出を出す（乗り換わった「新しい魚」の名前とレベルで）
        ShowHitBanner(newFish);

        // わらしべ成立（掛かっている魚がより大きな魚へ乗り換わった）
        SEED.Events.Raise(FishingEvents.LevelUp);
    }

    /// <summary>
    /// ヒット演出（帯＋「Lv◯ 魚名」「HIT!!!」）を再生する【演出呼び出しの唯一の入口】。
    /// 演出スクリプトが未設定・破棄済みなら何もしない。
    /// </summary>
    /// <param name="fish">ヒットした（乗り換わった）魚。</param>
    private void ShowHitBanner(Fish fish)
    {
        if (hitBanner is not { } banner) { return; }
        banner.Play(fish.Level, fish.DisplayName);
    }

    /// <summary>
    /// <b>わらしべ連鎖</b>: より大きい魚が、掛かっている魚を<b>即座に食べる</b>ことを試みる
    /// 【連鎖成立の唯一の入口】。前アタリ・合わせは無く、成立すれば即ヒットが乗り換わる。
    ///
    /// 成立条件は「ヒット中」「掛かっている魚を <see cref="Fish.CanPreyOn"/> で捕食できる」
    /// に加えて、<b>やり取りの「隙（<see cref="FishingFight.Phase.Rest"/>）」中であること</b>、
    /// さらに<b>隙へ入ってから <see cref="chainEatGraceSeconds"/> 秒が経っていること</b>。
    /// 出題・回答中（Call / Answer）や猶予中に届いた場合は false を返すだけで、掛かっている魚も
    /// やり取りも一切変化しない（呼び出し側の魚は待機を続ける想定。<see cref="Fish"/> 側の実装）。
    ///
    /// 成立すると <see cref="SwapHookedFish"/> で乗り換え、初期の糸の残りは
    /// <see cref="HookJudgement.Excellent"/>（＝満タン）で始める。合わせの手応え代わりに
    /// 通常のヒットと同じ効果音（<see cref="hookSePath"/>）を鳴らす。
    /// </summary>
    /// <param name="eater">掛かっている魚を食べようとしている魚。</param>
    /// <returns>成立したら true。</returns>
    public bool TryEatHookedFish(Fish eater)
    {
        // チュートリアルが連鎖を止めている間は成立させない
        // （巻き上げの練習中に横取りされると、手順が飛んで説明と噛み合わなくなる）。
        if (TutorialRules.Active && TutorialRules.ChainDisabled) { return false; }

        if (State != FishState.Hooked) { return false; }
        if (hookedFish is not { } prey) { return false; }
        if (ReferenceEquals(eater, prey)) { return false; }         // 自分自身は食えない
        if (!eater.CanPreyOn(prey)) { return false; }                // 魚レベルが十分高い魚だけが食える

        // 隙（Rest）中でなければ食えない（出題・回答中の捕食は許さない）
        if (fight is not { CurrentPhase: FishingFight.Phase.Rest } activeFight) { return false; }

        // 隙へ入った直後の猶予（chainEatGraceSeconds）が明けるまでは成立させない。
        // 弾かれた魚は回遊へ戻らず待ち続ける（Fish.chainWaitTimeoutSeconds の上限まで）ので、
        // 猶予が明けた次のフレームで改めて成立し得る。
        if (activeFight.SecondsSincePhaseStart < chainEatGraceSeconds) { return false; }

        PlaySe(hookSePath, hookSeVolume);
        SwapHookedFish(eater, HookJudgement.Excellent);
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

        // 釣り上げずに終わった（糸切れ・キャンセル・逃走）ので、
        // 連鎖の途中で食べさせた魚も成果にはならない【履歴を捨てる場所 その1】。
        chainCatchHistory.Clear();
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
        // ポーズの開閉はどの釣り状態でも最優先で受け付ける。
        // ここで拾った Esc をメニュー側が同じフレームで再処理しないよう、
        // PauseMenu 側に「開閉と同じ刻の入力は無視する」門が入っている。
        if (!PauseMenu.IsOpen && SEED.Input.GetKeyDown(SEED.KeyCode.Escape))
        {
            PauseMenu.Toggle(pauseMenuActorPath);
        }

        // 巻き演出（音・アニメ）の停止判定は<b>どの早期 return よりも前</b>で必ず通す。
        // ここより下の return（ポーズ・プレイヤー未設定・状態別 switch）を通ると
        // UpdateReeling → UpdateReelSound が呼ばれず、ループ音が鳴りっぱなしになる。
        UpdateReelFeedbackGate();

        // ポーズ中はゲーム側の更新も入力も止める。
        // カーソルロックの再適用（UpdateCursorLock）より前に抜けることが重要で、
        // ここを通してしまうとメニュー操作中にカーソルが消える。
        if (PauseMenu.IsOpen) { return; }

        // カーソルロックを状態へ同期する。ここで毎フレーム引き直しておけば、
        // 途中で return する経路（プレイヤー未設定・待機中など）でもロックが残らない。
        UpdateCursorLock();

        // 判定画像の表示は釣り状態に依らず進める（早期 return の経路でも必ず消える）。
        UpdateJudgementUi(ctx.DeltaTime);

        // 魚レーダーも釣り状態に依らず毎フレーム引き直す（早期 return の経路でも必ず消える）。
        UpdateFishRadar(ctx.DeltaTime);

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
            // チュートリアル中は「構え」に入る操作を止められる（通常時は常に許可）
            if (!InputGate.Allows(GameAction.Ready)) { return; }
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
                // まだ何もアタっていないので、この状態での左クリック（竿振り）は
                // 完全に無視する（ウキを跳ねさせない・演出/SE/イベントも出さない・
                // 状態も変えない）。そのため UpdateSwingDetection は呼ばず、
                // 常に巻き取り更新だけを行う。
                UpdateReeling(ctx.DeltaTime);
                break;

            case FishState.Hooked:
                // ヒット中も巻き取りの操作系（ホイール・A/D 操舵）はまったく同じ。
                // ただし竿振りは読まない（ヒット中の振りは未仕様）ので跳ねもしない。
                //
                // 先にやり取り（テンションゲージ・糸 HP）を 1 フレーム進める。
                // 糸が切れたらこのフレームは巻き取りへ進まず、糸切れ処理で締める。
                UpdateFight(ctx.DeltaTime);
                if (State != FishState.Hooked) { break; }
                UpdateRunCameraTarget();    // 引き演出（LeadIn / 回復後の走り Run）中だけカメラ目標を置き直す
                UpdateCallRestCameraTarget();   // 出題（Call）／巻き取り（Rest）中だけカメラ目標を置き直す
                UpdateReeling(ctx.DeltaTime);
                UpdateShoreCamera(ctx.DeltaTime);   // 岸際の巻き中だけ「陸側から海を見る」構図へ回り込む
                break;

            case FishState.Nibbling:
            case FishState.HookWindow:
                // アタリ〜合わせの受付中。巻き取り入力（ホイール・A/D）は一切見ない。
                UpdateBiteTiming(ctx.DeltaTime);
                break;

            case FishState.Catching:
                UpdateCatching(ctx.DeltaTime);
                break;
        }

        // ── スタン演出（隙フェーズの星）の唯一の出口 ────────────────
        // 状態遷移・糸切れ・釣り上げ・キャンセルのどれで抜けても、
        // ここが毎フレーム「今出すべきか」を見て開始／追従／停止を決める。
        UpdateStunEffect();

        // ── カメラ姿勢の上書きを返す唯一の出口 ─────────────────────
        // 上書きを掛けてよいのは「ヒット中の岸際（巻き）」と
        // 「釣り上げ（寄り〜演出）」だけ。そのどちらでもない状態へ抜けたら、
        // どの経路（糸切れ・空振り・キャンセル・姿勢解除）で来ても必ず通常の追従へ返す。
        // 経路ごとに解除を書き足すと必ず取り残しが出るため、ここ 1 か所に集約する。
        if (State is not (FishState.Hooked or FishState.Catching)) { ReleaseCameraOverride(); }

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
        // 構えに入った（チュートリアルの手順送りなどが購読する）
        SEED.Events.Raise(FishingEvents.ReadyBegin);
        SEED.Debug.Log("[Fishing] Aiming");
    }

    /// <summary>
    /// 釣りを中断して待機へ戻す（外部から釣り姿勢を解除された場合の後始末）。
    /// ウキを非表示にし、糸と着水点マーカーを隠し、竿のアニメ指定は <see cref="PlayerMove"/> 側へ返す。
    /// </summary>
    private void CancelToIdle()
    {
        // 「構えていた状態から待機へ戻った」ときだけ通知する
        //（既に Idle のときに呼ばれてもイベントは飛ばさない）
        bool wasFishing = State != FishState.Idle;

        State = FishState.Idle;
        StopReelSound();               // 姿勢解除・中断でも巻き取り音を必ず止める
        ResetGesture();
        AbortBiteTiming();             // 姿勢解除・中断でもアタリ進行を打ち切る
        ReleaseHook();                 // 姿勢解除・中断でも必ず魚を逃がす
        fight?.EndFight();             // 姿勢解除・中断でもやり取りを畳む
        ClearPendingCatchBegin();      // 評価バナー待ちで演出が始まっていない場合の取り残しも断つ
        presenter?.Abort();            // 釣り上げ演出中なら畳む（魚の破棄・白／テキストの消去も込み）
        catchZoomActive = false;       // 釣り上げの寄りが途中なら捨てる
        ReleaseCameraOverride();       // 岸際／寄りでカメラを握っていたら必ず返す
        NearShore = false;             // 岸際の効果（漂流物停止・直進巻き）も必ず解く
        HideJudgement();               // 判定画像も消す
        // この後 PlayerMove 側（ExitFishingStance・通常移動のアニメ）が本体を触るのでラッチを捨てる
        ResetPlayerClipLatch();
        ParkFloatHidden();
        HideLine();
        HideCastPreview();
        ParkReelArrow();
        StopStunEffect();              // 隙の星が出ていたら必ず畳む（早期 return 経路の保険）
        radar?.HideImmediate();        // 中断はフェードせず即座に消す（次の投げは 0 からフェードイン）
        radarVisibleElapsed = 0f;      // 着水からの待ち時間も戻す
        // 釣り状態を抜けたらカーソルを必ず返す（姿勢解除・中断の唯一の出口）。
        UpdateCursorLock();
        if (wasFishing) { SEED.Events.Raise(FishingEvents.ReadyEnd); }
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
        StopReelSound();
        CancelToIdle();
        if (playerMove is { } pm) { pm.ExitFishingStance(); }
    }

    /// <summary>
    /// ウキが手元を離れて外に出ているか【「ウキが出ている」判定の唯一の定義】。
    ///
    /// 飛翔中（<see cref="FishState.Casting"/>）・着水後の待機
    /// （<see cref="FishState.Floating"/>）・巻き取り中（<see cref="FishState.Reeling"/>）・
    /// アタリ中（<see cref="FishState.Nibbling"/> / <see cref="FishState.HookWindow"/>）・
    /// ヒット中（<see cref="FishState.Hooked"/>。わらしべ連鎖はこの状態のまま進む）が該当する。
    /// 釣り上げ演出（<see cref="FishState.Catching"/>）は<b>白へ沈むまで（Fade）と
    /// スロー放物線（SlowArc）だけ</b>該当する: 魚はウキの位置から跳ね上がるので、
    /// その 2 フェーズはウキが画に映っていてよい。釣果パネルへ移ったあとは
    /// ウキを手元へ畳む（<see cref="UpdateCatching"/>）。
    /// 逆に <see cref="FishState.Idle"/> / <see cref="FishState.Aiming"/> /
    /// <see cref="FishState.Windup"/> ではウキは非表示で手元にある。
    ///
    /// 状態を 1 つ増やすたびに各所の列挙を直して回る（＝直し漏れが必ず出る）のを避けるため、
    /// 「ウキが出ている」を意味する判定はすべてこの関数を通す。
    /// ただし<b>意味が違うもの</b>（餌として有効か＝<see cref="BaitActive"/>）はここに混ぜない。
    /// </summary>
    private bool IsFloatOut()
        => State is FishState.Casting or FishState.Floating or FishState.Reeling
                 or FishState.Nibbling or FishState.HookWindow
                 or FishState.Hooked
        || (State == FishState.Catching
            && (IsWaitingCatchStart
                || CatchPhase is CatchPresenter.CatchPhase.Fade
                              or CatchPresenter.CatchPhase.LowAngle
                              or CatchPresenter.CatchPhase.SlowArc));

    /// <summary>
    /// 釣り上げは決まったが、評価バナーを読ませるために演出の開始をまだ待っているか。
    ///
    /// この間は <see cref="CatchPresenter.Phase"/> がまだ <c>None</c> なので、
    /// 「演出中の区間」を <c>Phase</c> だけで判定すると<b>糸だけが消える</b>
    /// （ウキは掛かったときの位置に残るので、宙に浮いたウキだけが見える）。
    /// 待ち区間も「ウキが出ている区間」に含めることで、
    /// 掛かった瞬間の絵をそのまま保ったまま評価を読ませられる。
    /// </summary>
    private bool IsWaitingCatchStart => pendingCatchFish is not null;

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
        // チュートリアルで「構え」を止めている間は解除も判定しない
        //（説明中にボタンを離しただけで構えが解けると、台本どおりに進まなくなるため）。
        if (InputGate.Allows(GameAction.Ready) && !SEED.Input.GetMouseButton(SEED.MouseButton.Left))
        {
            ExitToMovement();
            return;
        }

        // A/D でプレイヤー自身を海の正面から左右へ振る（キャスト方向はプレイヤーの正面＝
        // transform.Forward に追従するので、CastYawDegrees / 着水点プレビューには何もしなくても反映される）。
        UpdateStanceTurn(deltaTime);

        // MouseDelta はウィンドウ内カーソル位置の差分（px、右が +X / 下が +Y）。
        // MouseMove（Raw Input 由来）は埋め込み時に届かないことがあるのでこちらを使う。
        // チュートリアル中は振りかぶりのジェスチャを止められる（通常時は常に許可）
        float deltaX = InputGate.Allows(GameAction.Cast) ? SEED.Input.MouseDelta.x : 0f;
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

        // チュートリアル中は左右の狙いを止められる（通常時は常に許可）
        float turn = 0f;
        if (InputGate.Allows(GameAction.Aim))
        {
            if (SEED.Input.GetKey(SEED.KeyCode.A)) { turn -= 1f; }   // A: 左（プレイヤー視点）
            if (SEED.Input.GetKey(SEED.KeyCode.D)) { turn += 1f; }   // D: 右（プレイヤー視点）
        }

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
        // 振り抜く前に離したらキャンセル（振りかぶりを解いて移動へ戻る）。
        // 構えを止めている間は解除も判定しない（UpdateAiming と同じ理由）。
        if (InputGate.Allows(GameAction.Ready) && !SEED.Input.GetMouseButton(SEED.MouseButton.Left))
        {
            ExitToMovement();
            return;
        }

        // 振りかぶり中も引き続き A/D で振れる（着水点マーカーはプレイヤーの正面に
        // 追従するので、UpdateCastPreview 側は何も変えなくてよい）。
        UpdateStanceTurn(deltaTime);

        // 飛距離プレビューの往復位相を進める
        previewElapsed += deltaTime;

        // チュートリアル中は振り抜きのジェスチャを止められる（通常時は常に許可）
        float deltaX = InputGate.Allows(GameAction.Cast) ? SEED.Input.MouseDelta.x : 0f;
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
    /// <see cref="EffectiveMinCastDistance"/>⇔<see cref="maxCastDistance"/> をピンポン往復する。
    /// </summary>
    private float PreviewDistance()
    {
        float period = SEED.Mathf.Max(previewCycleSeconds, DivideEpsilon);

        // PingPong(u, 1) は u が 2 進むと 1 往復するので、1 周期ぶんを 2 単位に伸ばす
        float ratio = SEED.Mathf.PingPong(previewElapsed / period * PingPongCycleUnits, 1f);
        return SEED.Mathf.Lerp(EffectiveMinCastDistance, maxCastDistance, ratio);
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
    /// <param name="distance">飛距離（メートル）。<see cref="EffectiveMinCastDistance"/>〜<see cref="maxCastDistance"/> にクランプする。</param>
    /// <param name="yawDegrees">キャスト方向のヨー角（度）。null なら縮退のためキャストしない。</param>
    private void StartCast(float distance, float? yawDegrees)
    {
        if (yawDegrees is not { } yaw) { return; }   // 真上／真下を向く縮退（起こらない想定の保険）

        float clamped = SEED.Mathf.Clamped(distance, EffectiveMinCastDistance, maxCastDistance);

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
        // 自動回収の猶予はキャストのたびに仕切り直す（飛翔中も判定させない）
        landingElapsed = 0f;
        reeledSinceLanding = false;

        State = FishState.Casting;
        HideCastPreview();
        PlaySe(castSePath, castSeVolume);
        // 仕掛けを投げた（チュートリアルの手順送りなどが購読する）
        SEED.Events.Raise(FishingEvents.Cast);

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
            // 着水の瞬間を基点に自動回収の猶予を数え直す（飛翔中の経過は数えない）。
            landingElapsed = 0f;
            reeledSinceLanding = false;
            CrossFadeBoth(floatClip, playerFloatClip);
            PlaySe(splashSePath, splashSeVolume);
            // 着水した
            SEED.Events.Raise(FishingEvents.Land);
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

        // 出題の打点演出（PlayNibbleCue）で使う沈みアニメを進める（この関数は Hooked からしか
        // 呼ばれないので、UpdateBiteTiming の前アタリ演出と二重に進むことはない）。
        if (nibbleDipElapsed >= 0f)
        {
            nibbleDipElapsed += deltaTime;
            if (nibbleDipElapsed >= nibbleDipSeconds) { nibbleDipElapsed = NoDipElapsed; }
        }

        f.Tick(deltaTime, ReadReelAmount());

        // 巻いているあいだだけ、ウキが漂流物を巻き込んだかを見る（詳細は UpdateDriftPickup）
        UpdateDriftPickup(f);

        // 残り距離の表示。「釣り上げ成立距離までの残り」＝ 実測距離 − 成立距離 を出す
        // （0 になった瞬間に釣れる、という見た目と判定の一致を保つ。負値は表示側で 0 に丸める）。
        f.UpdateDistanceDisplay(CurrentFloatDistance() - catchDistanceMeters);

        // ── 釣り上げ成立【成功条件の唯一の判定点・2026-09-09 改定】──────────
        // 条件は「ウキが竿先の近傍（catchDistanceMeters）まで寄っていること」だけで、
        // <b>魚 HP は一切見ない</b>（＝岸まで寄せ切れたら釣れる）。
        // 魚 HP 0（FishingFight.FishDefeated）はもはや成立条件ではなく、
        // 「見た目距離の下限が解除されて竿先まで一気に寄る」という意味だけを持つ。
        //
        // 糸切れ判定より<b>先</b>に置いてあるのは、「岸まで寄せ切った」を
        // 何よりも優先させるため（同じフレームに両方成立したら釣り上げが勝つ）。
        // FinishReeling が FishingFight.EndFight() を呼ぶので、
        // これ以降は糸の減りも拍時計も止まる。
        if (CurrentFloatDistance() <= catchDistanceMeters)
        {
            FinishReeling();
            return;
        }

        if (f.LineBroken)
        {
            // チュートリアルの「釣り上げよう」ミッションでは、糸が切れても投げ直しへ戻さず
            // 掛かった状態のままやり取りだけを仕切り直す（練習を続けさせるため）。
            // 上書きが無ければ従来どおり糸切れで終わる。
            if (TutorialRules.Active && TutorialRules.RestartFightOnLineBreak && hookedFish is { } stillHooked)
            {
                f.EndFight();
                f.BeginFight(stillHooked, HookJudgement.Nice, CurrentFloatDistance());
                TutorialFightRestartCount++;
                SEED.Debug.Log("[Fishing] 糸切れ → チュートリアルのため掛かった状態から仕切り直し");
                return;
            }

            BreakLine();
            return;
        }
    }

    /// <summary>
    /// 漂流物（<see cref="DriftItem"/>）の巻き込み判定と効果の適用
    /// 【漂流物とやり取りを繋ぐ唯一の場所】。
    ///
    /// <b>発動条件は「巻き取りながら巻き込んだとき」だけ</b>
    /// （<see cref="FishingFight.ReelingRecently"/> ＝ 隙（Rest）フェーズで、かつ
    ///   最後の巻き入力から保持時間以内）。巻いていないあいだにウキの近くを
    /// 漂っているだけの漂流物はすり抜ける ―― ウキが動くのは巻いているときだけなので、
    /// 「巻き寄せて拾う」という操作感を判定そのものに一致させるため。
    ///
    /// 判定はウキと漂流物の<b>水平距離</b>が漂流物の当たり半径以下かどうか。
    /// 拾った漂流物は効果を適用し、効果音を鳴らしてから消す。
    /// </summary>
    /// <param name="f">進行中のやり取り（効果の適用先）。</param>
    private void UpdateDriftPickup(FishingFight f)
    {
        if (!f.ReelingRecently) { return; }
        if (DriftItem.All.Count == 0) { return; }
        if (uki is not { IsValid: true } floatTf) { return; }

        // DriftItem.Kill() は登録簿を書き換えるので、必ず作業用リストへ写してから回す
        driftWorkItems.Clear();
        foreach (var item in DriftItem.All) { driftWorkItems.Add(item); }

        var floatPosition = floatTf.Position;
        for (int i = 0; i < driftWorkItems.Count; i++)
        {
            var item = driftWorkItems[i];
            if (HorizontalDistance(floatPosition, item.Position) > DriftHitRadiusOf(item)) { continue; }

            ApplyDriftEffect(f, item);
            // 漂流物を巻き込んだ（引数は種類。効果を適用した直後・破棄する前に流す）
            SEED.Events.Raise(FishingEvents.DriftPickup, item.Kind);
            item.PlayHitSe();
            item.Kill();
        }

        driftWorkItems.Clear();
    }

    /// <summary>
    /// 漂流物の種類に応じた効果をやり取りへ適用する【種類と効果の唯一の対応表】。
    ///
    /// <code>
    /// ひるませ  … 隙（Rest）を効果量ぶんの小節数だけ延長する（FishingFight.AddRestBars）
    /// 魚回復    … 魚 HP を「最大値 × 効果量」だけ戻す。<b>隙の最中に拾ったぶんはその場では効かず</b>、
    ///             隙が終わってから走り（Phase.Run）でまとめて取り返される（FishingFight.RecoverFishHp）
    /// 糸回復    … 糸の残りへ効果量ぶんを足す（上限 1）
    /// </code>
    /// 未知の種類は警告だけ出して何もしない（prefab の設定ミスを黙って握り潰さない）。
    /// </summary>
    /// <param name="f">効果の適用先。</param>
    /// <param name="item">拾った漂流物。</param>
    private static void ApplyDriftEffect(FishingFight f, DriftItem item)
    {
        switch (item.Kind)
        {
            case DriftItem.KindStun:
                f.AddRestBars(SEED.Mathf.RoundToInt(item.EffectAmount));
                break;

            case DriftItem.KindFishRecover:
                f.RecoverFishHp(item.EffectAmount);
                break;

            case DriftItem.KindLineRecover:
                f.RecoverLine(item.EffectAmount);
                break;

            default:
                SEED.Debug.LogWarning($"[FishingController] 未知の漂流物の種類 \"{item.Kind}\" を巻き込みました（効果なし）。");
                break;
        }
    }

    /// <summary>
    /// この漂流物の巻き込み判定に使う半径（メートル）
    /// 【当たり半径の唯一の問い合わせ口】。
    ///
    /// 通常は prefab に設定された <see cref="DriftItem.HitRadius"/> をそのまま使うが、
    /// チュートリアルが <see cref="TutorialRules.DriftPickupRadiusOverride"/> を
    /// 指定しているあいだはその値で上書きする（説明ミッションで
    /// 「巻けば必ず拾える」を保証するため）。上書きが無ければ従来どおり。
    /// </summary>
    /// <param name="item">判定する漂流物。</param>
    /// <returns>使用する当たり半径（メートル）。</returns>
    private static float DriftHitRadiusOf(DriftItem item)
    {
        if (TutorialRules.Active
            && TutorialRules.DriftPickupRadiusOverride > TutorialRules.NoDriftPickupRadiusOverride)
        {
            return TutorialRules.DriftPickupRadiusOverride;
        }
        return item.HitRadius;
    }

    /// <summary>漂流物の走査用リスト（毎フレームの確保を避けて使い回す）。</summary>
    private readonly System.Collections.Generic.List<DriftItem> driftWorkItems = new();

    /// <summary>
    /// 現在のウキ→竿先の水平距離（メートル）。ウキが無ければ 0。
    /// やり取りの基準距離（開始時の <see cref="FishingFight.BeginFight"/>・残り距離表示・
    /// ウキの距離制御）で共通に使う。
    /// </summary>
    private float CurrentFloatDistance()
        => uki is { IsValid: true } floatTf
            ? HorizontalDistance(floatTf.Position, ReelTargetPosition())
            : 0f;

    // ─── 魚レーダー ───────────────────────────────────────────

    /// <summary>
    /// レーダーへ載せる候補 1 匹ぶん（距離順に並べ替えるための一時データ）。
    /// 点の数には限りがあるので、近い順に詰めて溢れたぶんを落とす。
    /// </summary>
    private readonly struct RadarCandidate
    {
        /// <summary>ウキからの水平距離の 2 乗（並べ替えのキー。平方根を取らない）。</summary>
        public readonly float SqrDistance;

        /// <summary>レーダーへ渡す表示データ。</summary>
        public readonly RadarEntry Entry;

        /// <summary>各値を指定して候補を作る。</summary>
        /// <param name="sqrDistance">ウキからの水平距離の 2 乗。</param>
        /// <param name="entry">レーダーへ渡す表示データ。</param>
        public RadarCandidate(float sqrDistance, RadarEntry entry)
        {
            SqrDistance = sqrDistance;
            Entry = entry;
        }
    }

    /// <summary>レーダー候補の作業用リスト（毎フレームの確保を避けて使い回す）。</summary>
    private readonly System.Collections.Generic.List<RadarCandidate> radarCandidates = new();

    /// <summary>レーダーへ渡す表示データの作業用リスト（同上）。</summary>
    private readonly System.Collections.Generic.List<RadarEntry> radarEntries = new();

    /// <summary>候補の並べ替え規則（近い順）。毎フレームのデリゲート確保を避けて静的に持つ。</summary>
    private static readonly System.Comparison<RadarCandidate> RadarNearestFirst =
        (a, b) => a.SqrDistance.CompareTo(b.SqrDistance);

    /// <summary>
    /// 魚レーダーの表示を更新する【レーダーへ載せるものを決める唯一の判断点】。
    ///
    /// [出す区間]
    /// <code>
    /// 着水中（Floating / Reeling / Nibbling / HookWindow）
    ///   … 着水から radarAppearDelaySeconds 秒経ってからフェードインで出す
    ///     （投げた直後にいきなり出ると、狙いを定める画が忙しくなるため）
    /// ヒット中（Hooked）
    ///   … 遅延を待たずに出し続ける（ヒット直後の余白でも消さない）
    /// それ以外（待機・狙い・キャスト・釣り上げ演出）
    ///   … 隠す（フェードアウトはレーダー側が持つ）
    /// </code>
    /// フェードの秒数と進行は <see cref="FishRadar"/> 側の責務で、ここは
    /// <see cref="FishRadar.Show"/> / <see cref="FishRadar.Hide"/> を毎フレーム呼ぶだけ。
    ///
    /// [載せるもの]
    /// <code>
    /// ヒット前 … 射程内の魚を<b>全て</b>（釣れる／釣れないの区別はしない＝すべて不透明）
    ///            ＝「餌に反応している魚がどこに居るか」だけが分かればよい区間
    /// ヒット中 … 掛かっている魚より<b>魚レベルが高い</b>個体だけ（＝乗り換えの脅威／好機、
    ///            Fish.OutranksForRadar で判定）。
    ///            Fish.CanPreyOn（いま掛かっている魚を食べられるか）が真なら不透明、偽なら半透明
    /// 共通     … 漂流物（DriftItem）を種類ごとの色で不透明に載せる
    /// </code>
    /// 点のプールは有限（既定 24）なので、<b>魚を先に・近い順で</b>詰め、
    /// 漂流物は残り枠へ近い順で載せる（レーダー側が溢れたぶんを落とす）。
    /// 色の値そのものはレーダーのインスペクタが持ち、ここはそれを読んで詰めるだけ。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数（出現までの待ち時間の計測に使う）。</param>
    private void UpdateFishRadar(float deltaTime)
    {
        if (radar is not { } r) { return; }

        // ヒット中か（掛かっている魚が居て、ウキも生きている）
        bool hooked = State == FishState.Hooked && hookedFish is not null;

        // 出す区間から外れた／ウキが無いなら隠して待ち時間も戻す
        if ((!hooked && !BaitActive) || uki is not { IsValid: true } floatTf)
        {
            radarVisibleElapsed = 0f;
            r.Hide();
            return;
        }

        // 着水してからの経過。ヒット中は遅延を待たずに出す（ヒットで初めて見えるのでは遅い）
        radarVisibleElapsed += deltaTime;
        if (!hooked && radarVisibleElapsed < SEED.Mathf.Max(radarAppearDelaySeconds, 0f))
        {
            r.Hide();
            return;
        }

        r.Show();

        var center = floatTf.Position;
        float range = r.RangeMeters;
        float sqrRange = range * range;
        float opaque = r.OpaqueDotAlpha;

        radarEntries.Clear();

        // ── 1) 魚（近い順・先に枠を取る）──
        radarCandidates.Clear();
        foreach (var fish in Fish.All)
        {
            if (fish.Transform is not { IsValid: true } fishTf) { continue; }

            if (hooked && hookedFish is { } hookedTarget)
            {
                // 掛かっている本人は中心の点で表しているので載せない
                if (ReferenceEquals(fish, hookedTarget)) { continue; }
                // 掛かっている魚よりレベルが低い（格下の）個体は乗り換えに関与しないので載せない
                if (!fish.OutranksForRadar(hookedTarget)) { continue; }
            }

            var pos = fishTf.Position;
            float dx = pos.x - center.x;
            float dz = pos.z - center.z;
            float sqrDistance = dx * dx + dz * dz;
            if (sqrDistance > sqrRange) { continue; }   // 射程外は載せない

            // ヒット前は区別しない（すべて不透明）。ヒット中だけ「食べられるか」で薄さを変える。
            float alpha = !hooked || (hookedFish is { } prey && fish.CanPreyOn(prey))
                ? opaque
                : r.UncatchableAlpha;

            radarCandidates.Add(new RadarCandidate(sqrDistance, new RadarEntry(pos, r.CatchableColor, alpha)));
        }

        // -- 1-b) 仮想の魚（アクタを持たない個体）--
        // 沖合の魚は負荷対策で実体化していない（FishManager の仮想魚プール）。
        // レーダーの魚影は従来どおり「維持数ぶん」出したいので、未実体化の個体も載せる。
        // 実体化済みの個体は上のループ（Fish.All）で載せているため、ここでは重複させない。
        if (FishManager.Current is { } fishManager)
        {
            int hookedLevel = hooked ? HookedFishLevel : Fish.UnknownLevel;
            for (int level = 0; level < fishManager.PooledLevelCount; level++)
            {
                var records = fishManager.PooledFishOf(level);
                for (int i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    if (record.Materialized) { continue; }

                    // ヒット中は「掛かっている魚より格上」だけ載せる（実体の魚と同じ規則）。
                    // 掛かっている魚のレベルが不明なときは格上か判定できないので載せない
                    // （Fish.OutranksForRadar の大きさ比較は、実体の無い個体では行えない）。
                    if (hooked && (hookedLevel == Fish.UnknownLevel || record.Level <= hookedLevel)) { continue; }

                    var virtualPos = record.Position;
                    float vdx = virtualPos.x - center.x;
                    float vdz = virtualPos.z - center.z;
                    float virtualSqrDistance = vdx * vdx + vdz * vdz;
                    if (virtualSqrDistance > sqrRange) { continue; }   // 射程外は載せない

                    // 未実体化の個体は実体化レベル帯の外＝掛かっている魚より確実に格上なので、
                    // 実体の魚と同じく「捕食できる（＝不透明）」扱いで描く。
                    radarCandidates.Add(new RadarCandidate(
                        virtualSqrDistance, new RadarEntry(virtualPos, r.CatchableColor, opaque)));
                }
            }
        }

        radarCandidates.Sort(RadarNearestFirst);
        for (int i = 0; i < radarCandidates.Count; i++) { radarEntries.Add(radarCandidates[i].Entry); }

        // ── 2) 漂流物（近い順・魚の残り枠へ）──
        radarCandidates.Clear();
        foreach (var item in DriftItem.All)
        {
            var pos = item.Position;
            float dx = pos.x - center.x;
            float dz = pos.z - center.z;
            float sqrDistance = dx * dx + dz * dz;
            if (sqrDistance > sqrRange) { continue; }

            radarCandidates.Add(new RadarCandidate(sqrDistance, new RadarEntry(pos, r.DriftColorOf(item.Kind), opaque)));
        }

        radarCandidates.Sort(RadarNearestFirst);
        for (int i = 0; i < radarCandidates.Count; i++) { radarEntries.Add(radarCandidates[i].Entry); }

        // プレイヤーの正面を上にする（キャスト方向と同じ「正面のヨー角」を使う）
        r.UpdateRadar(center, CastYawDegrees() ?? 0f, radarEntries);
    }

    /// <summary>
    /// レーダーを出してよい状態が続いている秒数（着水／ヒットしてからの経過）。
    /// <see cref="radarAppearDelaySeconds"/> と比べてフェードインの開始を決める。
    /// 出す区間から外れたフレームで 0 に戻る。
    /// </summary>
    private float radarVisibleElapsed = 0f;

    // ─── スタン演出（隙フェーズの星） ───────────────────────────

    /// <summary>
    /// 魚の頭上で星を回す「スタン演出」を 1 フレーム分更新する
    /// 【この演出の開始・追従・停止を決める唯一の場所】。
    ///
    /// 出すべき条件は<b>ヒット中かつ隙（<see cref="FishingFight.Phase.Rest"/>）</b>の 1 つだけ。
    /// 出題・回答・余白へ移った、糸が切れた、釣り上げた、姿勢を解除した——
    /// どの理由で条件から外れても同じ経路で <see cref="StunEffect.Stop"/> に落ちるので、
    /// 出しっぱなしにならない。効果音は <see cref="StunEffect.Show"/> 側が
    /// 立ち上がり（畳んでいた → 出す）の 1 回だけ鳴らすので、ここでは意識しない。
    /// </summary>
    private void UpdateStunEffect()
    {
        if (stunEffect is not { } effect) { return; }

        bool shouldShow = State == FishState.Hooked
                          && IsHooked
                          && FightPhase == FishingFight.Phase.Rest;

        if (!shouldShow)
        {
            // 出ていたぶんだけ畳む（毎フレーム Stop を叩かないよう立ち下がりで判定）
            if (stunShown)
            {
                effect.Stop();
                stunShown = false;
            }
            return;
        }

        // 隙へ入った瞬間: 演出を開始する（効果音は StunEffect.Show 内で鳴る）
        if (!stunShown)
        {
            effect.Show(StunAnchorPosition());
            stunShown = true;
            return;
        }

        // 隙のあいだ: 魚は動き続けるので中心を毎フレーム置き直す
        effect.SetAnchor(StunAnchorPosition());
    }

    /// <summary>
    /// スタン演出を必ず畳む（釣りを中断する経路から呼ぶ後始末）。
    /// </summary>
    private void StopStunEffect()
    {
        if (!stunShown) { return; }
        stunEffect?.Stop();
        stunShown = false;
    }

    /// <summary>
    /// 星を回す中心（ワールド座標）＝<b>掛かっている魚の頭のてっぺんの少し上</b>。
    ///
    /// <code>
    /// 中心 = 魚の位置 + 上 × (魚の高さ + stunStarHeightOffset)
    /// </code>
    ///
    /// 魚の高さはモデルのローカル AABB の上端に描画オフセットスケールとアクタースケールを
    /// 掛けて求める（<see cref="CatchPresenter"/> の実寸算出と同じ考え方）。
    /// 魚が居ない・Model が無い・モデル未ロードで AABB が縮退している場合は
    /// <see cref="stunFallbackFishHeight"/> を代替の高さとして使う。
    /// </summary>
    private SEED.Vector3 StunAnchorPosition()
    {
        if (hookedFish is not { } fish || !fish.Actor.IsValid)
        {
            // 魚が居ないなら自分（プレイヤー）の頭上を仮の中心にする（実際には表示条件を満たさない）
            return transform.Position + SEED.Vector3.Up * (stunFallbackFishHeight + stunStarHeightOffset);
        }

        float height = stunFallbackFishHeight;
        if (fish.Actor.GetComponent<SEED.Model>() is { IsValid: true } model)
        {
            // AABB 上端 × 描画オフセットスケール × アクタースケール ＝ ワールドでの頭上の高さ
            float measured = model.LocalBoundsMax.y * model.OffsetScale.y * fish.Transform.Scale.y;
            if (measured > DivideEpsilon) { height = measured; }
        }

        return fish.Transform.Position + SEED.Vector3.Up * (height + stunStarHeightOffset);
    }

    /// <summary>
    /// 魚が沖へ走っているあいだ（<see cref="FishingFight.Phase.LeadIn"/> ＝ 初回ヒット直後／
    /// <see cref="FishingFight.Phase.Run"/> ＝ 隙中に魚回復を拾った直後）のカメラ目標
    /// （<see cref="runCameraTarget"/>）を毎フレーム置き直す【この構図の唯一の算出点】。
    ///
    /// プレイヤーとウキの<b>中点</b>を中心に、<see cref="runCamThetaDegrees"/>（方位角）・
    /// <see cref="runCamPhiDegrees"/>（仰角）・<see cref="runCamDistance"/>（距離）の球面座標で
    /// カメラを置き、中点を注視させる。間隔（プレイヤー↔ウキの水平距離）が開くほど
    /// <see cref="runCamDistancePerSeparation"/> ぶん距離も伸びるので、両者が画面に収まりやすくなる。
    ///
    /// <c>this.transform</c> がそのままプレイヤー位置になる（本スクリプトはプレイヤーアクタに
    /// 付ける前提のため）。走りのフェーズ以外・参照未設定・ウキ未配置では何もしない
    /// （<see cref="CameraMove"/> 側も同じ判定（<see cref="IsRunCameraPhase"/>）で構図を選ぶので、
    /// 古い値のまま放置しても実害は無い）。
    /// </summary>
    private void UpdateRunCameraTarget()
    {
        if (runCameraTarget is not { IsValid: true } camTf) { return; }
        if (!IsRunCameraPhase(FightPhase)) { return; }
        if (uki is not { IsValid: true } floatTf) { return; }

        var playerPos = transform.Position;
        var floatPos = floatTf.Position;

        // プレイヤー→ウキの水平方向（dirH、正規化）。重なっていて方位が定まらない場合は
        // カメラ自身の現在の向きを流用する（真下を向くなどの破綻を避ける）。
        float dx = floatPos.x - playerPos.x;
        float dz = floatPos.z - playerPos.z;
        float horizDistance = SEED.Mathf.Sqrt(dx * dx + dz * dz);
        float dirX, dirZ;
        if (horizDistance > DivideEpsilon)
        {
            dirX = dx / horizDistance;
            dirZ = dz / horizDistance;
        }
        else
        {
            float fallbackYawRad = camTf.Rotation.y * SEED.Mathf.Deg2Rad;
            dirX = SEED.Mathf.Sin(fallbackYawRad);
            dirZ = SEED.Mathf.Cos(fallbackYawRad);
        }

        float centerX = (playerPos.x + floatPos.x) * 0.5f;
        float centerY = (playerPos.y + floatPos.y) * 0.5f;
        float centerZ = (playerPos.z + floatPos.z) * 0.5f;

        // dirH を右へ 90° 回した水平方向（θ=90° の方位に対応）。
        float rightX = dirZ;
        float rightZ = -dirX;

        float thetaRad = runCamThetaDegrees * SEED.Mathf.Deg2Rad;
        float phiRad = runCamPhiDegrees * SEED.Mathf.Deg2Rad;
        float cosTheta = SEED.Mathf.Cos(thetaRad);
        float sinTheta = SEED.Mathf.Sin(thetaRad);

        // 水平オフセット方向 ＝ dirH × cosθ + right × sinθ。
        float horizDirX = dirX * cosTheta + rightX * sinTheta;
        float horizDirZ = dirZ * cosTheta + rightZ * sinTheta;

        // 実効距離 ＝ 基本距離 ＋ 間隔 × 比率（間隔が開くほど両者が画面に収まるよう遠ざかる）。
        float dist = runCamDistance + horizDistance * runCamDistancePerSeparation;
        float horizComponent = dist * SEED.Mathf.Cos(phiRad);
        float vertComponent = dist * SEED.Mathf.Sin(phiRad);

        // camPos = center + horizDir × (dist・cosφ) + up × (dist・sinφ)
        var camPos = new SEED.Vector3(
            centerX + horizDirX * horizComponent,
            centerY + vertComponent,
            centerZ + horizDirZ * horizComponent);

        // 注視は中点固定。yaw はカメラ→中点の水平方向、pitch はカメラ→中点の俯角
        // （dy は「カメラ→中点」の高さ差。エンジン規約は pitch 正 = 下を向く なので、
        //   CatchPresenter と同じく 方向ベクトル.y の符号を反転して asin に掛ける）。
        float toCenterX = centerX - camPos.x;
        float toCenterZ = centerZ - camPos.z;
        float dy = centerY - camPos.y;
        float yawDeg = SEED.Mathf.Atan2(toCenterX, toCenterZ) * SEED.Mathf.Rad2Deg;
        float lookDistance = SEED.Mathf.Sqrt(toCenterX * toCenterX + toCenterZ * toCenterZ + dy * dy);
        float pitchDeg = lookDistance > DivideEpsilon
            ? -SEED.Mathf.Asin(SEED.Mathf.Clamped(dy / lookDistance, -1f, 1f)) * SEED.Mathf.Rad2Deg
            : 0f;

        camTf.Position = camPos;
        camTf.Rotation = new SEED.Vector3(pitchDeg, yawDeg, 0f);
    }

    /// <summary>
    /// 「引き（沖へ走る）」のカメラ構図を使うフェーズか
    /// 【<see cref="runCameraTarget"/> を選ぶ条件の唯一の定義】。
    ///
    /// 初回ヒット直後の余白（<see cref="FishingFight.Phase.LeadIn"/>）と、
    /// 隙の間に「魚回復」の漂流物を拾ったあとの走り（<see cref="FishingFight.Phase.Run"/>）は
    /// どちらも「掛かった魚が沖へ逃げていくのを見せる」区間なので、同じ構図で撮る。
    /// カメラ側（<see cref="CameraMove"/>）もこの判定を呼ぶので、
    /// 目標の置き直しと構図の選択が食い違うことがない。
    /// </summary>
    /// <param name="phase">判定するやり取りのフェーズ。</param>
    /// <returns>引きの構図を使うなら true。</returns>
    public static bool IsRunCameraPhase(FishingFight.Phase phase)
        => phase is FishingFight.Phase.LeadIn or FishingFight.Phase.Run;

    /// <summary>
    /// 出題（<see cref="FishingFight.Phase.Call"/>）と巻き取り（<see cref="FishingFight.Phase.Rest"/>）の
    /// カメラ目標を毎フレーム置き直す【この 2 つの構図の唯一の算出点】。
    ///
    /// 構図はキャスト中（<see cref="castCameraTarget"/>）と<b>同じ視線方向のまま</b>、
    /// ウキからの距離だけをフェーズごとの倍率へ変えたもの。
    /// <code>
    /// 位置 ＝ ウキ位置 ＋ (キャスト目標位置 − ウキ位置) × 距離倍率
    /// 向き ＝ キャスト目標の向き（そのまま）
    /// 距離倍率 … 出題 = callCameraDistanceScale（既定 0.5 ＝ 寄る）
    ///            巻き取り = restCameraDistanceScale（既定 1.5 ＝ 引く）
    /// </code>
    /// 出題は「魚が出す合図（ウキの沈み）を大きく見せる」、
    /// 巻き取りは「漂流物とウキの位置関係を広く見せる」という狙いの違いがそのまま倍率の差になる。
    ///
    /// 参照（ウキ／キャスト目標／この目標）のいずれかが欠けていれば何もしない。
    /// 対象フェーズ以外でも何もしない（フェーズ外で目標を動かすと、
    /// <see cref="CameraMove"/> が構図を切り替えた瞬間に飛んで見えるため）。
    /// なお岸際（<see cref="NearShore"/>）の巻き取り中は
    /// <see cref="UpdateShoreCamera"/> の姿勢上書きが優先されるので、ここの結果は使われない。
    /// </summary>
    private void UpdateCallRestCameraTarget()
    {
        if (callCameraTarget is not { IsValid: true } camTf) { return; }
        if (!TryPhaseCameraDistanceScale(FightPhase, out float scale)) { return; }
        if (uki is not { IsValid: true } floatTf) { return; }
        if (castCameraTarget is not { IsValid: true } castTf) { return; }

        var floatPos = floatTf.Position;
        var castPos = castTf.Position;

        camTf.Position = new SEED.Vector3(
            floatPos.x + (castPos.x - floatPos.x) * scale,
            floatPos.y + (castPos.y - floatPos.y) * scale,
            floatPos.z + (castPos.z - floatPos.z) * scale);

        // 向きはキャスト中の構図をそのまま流用する（視線方向を変えず距離だけ変えるため）
        camTf.Rotation = castTf.Rotation;
    }

    /// <summary>
    /// フェーズに対応する「ウキからのカメラ距離倍率」を引く
    /// 【フェーズと倍率の唯一の対応表】。
    ///
    /// 対応表に無いフェーズ（余白・走り・回答）は専用の構図を持つので false を返し、
    /// <see cref="UpdateCallRestCameraTarget"/> は何もしない。
    /// </summary>
    /// <param name="phase">いまのやり取りのフェーズ。</param>
    /// <param name="scale">見つかった距離倍率（見つからなければ 1）。</param>
    /// <returns>この構図を使うフェーズなら true。</returns>
    private bool TryPhaseCameraDistanceScale(FishingFight.Phase phase, out float scale)
    {
        switch (phase)
        {
            case FishingFight.Phase.Call:
                scale = callCameraDistanceScale;
                return true;

            case FishingFight.Phase.Rest:
                scale = restCameraDistanceScale;
                return true;

            default:
                scale = 1f;
                return false;
        }
    }

    /// <summary>
    /// 糸が切れたときの締め【糸切れの唯一の出口】。
    /// 魚を逃がし、判定表示の Miss を流用して失敗を示し、狙い（キャスト待ち）へ戻す。
    /// </summary>
    private void BreakLine()
    {
        StopReelSound();               // 糸切れ＝もう巻けないので巻き取り音を必ず止める
        fight?.EndFight();             // Paused も EndFight で必ず解除される
        nibbleDipElapsed = NoDipElapsed;   // 出題の沈みアニメが途中なら必ず戻す
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
        // 糸が切れた
        SEED.Events.Raise(FishingEvents.LineBreak);
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
    ///     （＝内積が 0 以下＝もう竿先へ近づけない）場合は巻き取りを完了させる。
    ///
    /// <b>着水直後は回収しない（短いキャスト対策）</b>
    /// (2) の完了判定は「着水から <see cref="landingGraceSeconds"/> 秒が過ぎ」かつ
    /// 「着水後に一度でも巻き入力があった」ときだけ通す（内部の canAutoFinish）。
    /// これが無いと、短いキャストや竿先の脇への着水では<b>着水した瞬間に条件が成立</b>し、
    /// 一度も巻いていないのにその場で回収されて狙いへ戻ってしまう。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateReeling(float deltaTime)
    {
        // 岸際判定はここで毎フレーム引き直す【巻きに関わる唯一の常時経路のため】。
        // Floating / Reeling / Hooked のどの状態でも通るので、判定が古いまま残らない。
        UpdateNearShore();

        if (uki is not { } floatTf || !floatTf.IsValid) { return; }

        float amount = ReadReelAmount();

        // ── 自動回収を許してよいかの前提を更新する ───────────────
        // 掛かっていない間（Floating / Reeling）だけ着水後の経過を数え、
        // 巻き入力があったフレームで「巻いた実績」を立てる。
        // 掛かっている間は釣り上げの成否を UpdateFight が持つので数えない。
        if (!IsHooked)
        {
            landingElapsed += deltaTime;
            if (amount > ReelInputEpsilon) { reeledSinceLanding = true; }
        }

        // 自動回収（巻き取り完了）の判定を行ってよいか【回収を許す唯一の判断点】。
        //  (1) 着水直後の猶予（landingGraceSeconds）を過ぎていること
        //  (2) 着水後に一度でも巻いていること
        //      ＝「キャスト直後から近かった」だけでは回収しない。距離が閾値以内でも
        //        プレイヤーが巻いて初めて回収される。
        bool canAutoFinish = !IsHooked
                          && landingElapsed >= SEED.Mathf.Max(landingGraceSeconds, 0f)
                          && reeledSinceLanding;

        // 巻き取り音は<b>実際に巻けているときだけ</b>鳴らす。
        // ヒット中の巻きが効くのは隙（Rest）フェーズだけなので、やり取り側の
        // 判定（FishingFight.CanReelNow）をそのまま使う（同じ条件を二重に持たない）。
        // ヒットしていないときは従来どおり「巻き入力があること」だけが条件。
        bool reelingEffective = amount > ReelInputEpsilon
                             && (!IsHooked || (fight is { } reelGate && reelGate.CanReelNow));
        UpdateReelSound(reelingEffective);

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

        // 安全策: 残り距離が完了距離以下になっていれば、これ以上巻いても意味が無いので
        // 完了させる（ウキが竿先を素通りするのを防ぐ）。
        //
        // 【着水直後は通さない】canAutoFinish に「猶予経過」と「巻いた実績」を
        // 含めているため、短いキャストで着水した瞬間から近くても回収されない。
        // 巻き始めて初めてここに到達する（＝回収はキャスト直後の距離に依存しない）。
        // ヒット中はここで完了させない（釣り上げの成否は UpdateFight が
        // 「HP 0 かつ釣り上げ成立距離」で決める）。
        if (canAutoFinish && remaining <= reelEndDistance)
        {
            FinishReeling();
            return;
        }

        // 岸際の直進巻き係数（1＝遠くて操舵フル、0＝直進距離以内で操舵ゼロ）。
        // remaining はこの関数で既に算出済みなので、ここで一度だけ求めて
        // ComputeReelDirection / UpdateReelArrow の両方へ使い回す（距離計算の重複を避ける）。
        float steerFactor = SEED.Mathf.Clamped01(
            (remaining - straightReelDistance) / SEED.Mathf.Max(straightFadeBand, DivideEpsilon));

        // 岸際（NearShore）に入っているあいだは<b>方向変更入力そのものを殺す</b>
        // （ウキは竿先へ直進する）。straightReelDistance のフェードと二重に効くが、
        // どちらも「操舵を弱める」向きの効果なので競合しない
        // （nearShoreDistanceMeters のほうを広く取れば、こちらが先に効く）。
        if (NearShore) { steerFactor = 0f; }

        // 巻く向き（A / D による左右のずれを含む、基準方向からの水平単位ベクトル）
        var dir = ComputeReelDirection(toTarget, deltaTime, steerFactor);

        // 進行方向が基準点への方向から 90 度以上外れている（内積 <= 0）＝
        // これ以上巻いても基準点へ近づけない向きなので、素通りする前に巻き取りを完了させる。
        // ヒット中はウキの移動を ComputeFloatDistanceStep が支配しており、
        // 巻く向きは操舵にしか使わないので、この打ち切りは非ヒット時だけに効かせる。
        // 【着水直後は通さない】着水の瞬間だけ向きが縮退している（ウキが竿先の
        // 真横・真上に来ている）場合に、巻いてもいないのに回収されるのを防ぐ。
        float approach = toTarget.x * dir.x + toTarget.z * dir.z;
        if (canAutoFinish && approach <= 0f)
        {
            FinishReeling();
            return;
        }

        // 巻く向きが確定したので、ウキの足元へインジケータを寝かせて置く。
        // steerFactor が 0（直進距離以内）ならインジケータは表示せず格納する。
        // さらにヒット中は「隙（巻ける区間）」だけに絞る（IsReelArrowAllowed 参照）。
        if (steerFactor > 0f && IsReelArrowAllowed)
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

            // 沖へ出る向きだけは安全弁のクランプを掛け、引かれ続けてウキが世界の外へ出るのを防ぐ。
            //
            // 【2026-09-11 修正】上限を「最長飛距離 ＋ 余裕」の固定値にしていたため、
            // 漂流物「魚回復」で魚 HP が最大値を超えても<b>ウキがそれ以上沖へ出られなかった</b>
            // （＝拾っても距離が HP 100% の位置で止まる不具合）。
            // 遠投気味に掛けると「HP 100% の距離 ＝ 掛かった距離 ＋ ヒット直後の引き距離」が
            // ちょうどこの固定上限に達してしまい、回復ぶんがまるごと画に出なかった。
            //
            // 上限は<b>「いま目指している距離 ＋ 余裕」と「最長飛距離 ＋ 余裕」の大きいほう</b>に変える。
            // こうすると安全弁としての役割（目標より余裕ぶん以上は行き過ぎない）は残したまま、
            // 魚 HP の超過ぶんだけ素直に沖へ出られる。
            if (signedStep > 0f)
            {
                float dragLimit = SEED.Mathf.Max(activeFight.DesiredFloatDistance, maxCastDistance)
                                + floatDragMarginDistance;
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
        // ＝「巻き取りで実際に距離が縮んで閾値以内に入った」ときの回収経路。
        // （ヒット中は UpdateFight の釣り上げ判定に一本化しているのでここでは終えない）。
        if (canAutoFinish && HorizontalDistance(next, target) <= reelEndDistance)
        {
            FinishReeling();
        }
    }

    // ─── 巻き取り音 ─────────────────────────────────────

    /// <summary>
    /// 巻き入力に合わせて巻き取り音（ループ）を出し入れする【巻き取り音の唯一の制御点】。
    ///
    /// 「実際に巻けている」フレームがあった瞬間に鳴らし始め、それが
    /// <see cref="reelSoundHoldSeconds"/> 秒途切れたら止める。音源は生成の翌フレーム
    /// 以降にしか取れないため、毎回遅延取得を試みる（取れるまでは何もしない）。
    ///
    /// 判定材料を「巻き取り量」ではなく<b>巻けているか</b>にしているのは、
    /// ヒット中に隙（Rest）以外でホイールを回しても魚 HP は削れず（＝巻けていない）、
    /// そこで音だけ鳴ると操作が通っているように誤解させるため。
    /// </summary>
    /// <b>猶予は必ず実時間（<see cref="SEED.Time.UnscaledDeltaTime"/>）で数える</b>。
    /// ゲーム時間（ctx.DeltaTime）で数えると、チュートリアルの合いの手などで
    /// <c>Time.Scale = 0</c> が掛かった瞬間に猶予が 1 秒も進まなくなり、
    /// 「巻いている」判定のまま音が鳴り続けてしまう（実際に起きていた不具合）。
    ///
    /// <param name="reelingEffective">
    /// このフレームに巻き取りが<b>実際に効いた</b>か（呼び出し側が判定して渡す）。
    /// </param>
    private void UpdateReelSound(bool reelingEffective)
    {
        // 停止条件（ポーズ・時間停止・巻き入力の遮断）が立っているあいだは
        // 何があっても鳴らさない。門（UpdateReelFeedbackGate）と同じ判断を
        // ここでも見ることで、同じフレームに鳴らし直されるのを防ぐ。
        if (IsReelFeedbackSuspended) { StopReelSound(); return; }

        if (reelingEffective) { sinceLastReelInput = 0f; }
        else if (sinceLastReelInput < float.MaxValue)
        {
            sinceLastReelInput += SEED.Time.UnscaledDeltaTime;
        }

        var source = ResolveReelSoundSource();
        if (source is not { } s) { return; }

        bool shouldPlay = sinceLastReelInput <= reelSoundHoldSeconds;
        bool playing = s.IsPlaying;
        if (shouldPlay && !playing) { s.Play(); }
        else if (!shouldPlay && playing) { s.Stop(); }
    }

    /// <summary>
    /// 巻き演出（ループ音・巻きアニメ）を止めるべき状況か
    /// 【「今は巻いていない」とみなす条件の唯一の定義】。
    ///
    /// <list type="bullet">
    ///   <item>ポーズメニューが開いている（<see cref="PauseMenu.IsOpen"/>）</item>
    ///   <item>ゲーム時間が止まっている（<c>Time.Scale == 0</c>。チュートリアルの合いの手）</item>
    ///   <item>巻き入力そのものが遮断されている（<see cref="InputGate"/> の Reel が閉じている）</item>
    /// </list>
    /// いずれも「Update が回らない／入力が来ない」ため、
    /// 猶予を数える通常経路だけでは音を止められない状況である。
    /// </summary>
    private bool IsReelFeedbackSuspended
        => PauseMenu.IsOpen
        || SEED.Time.Scale <= TimeStoppedScaleEpsilon
        || !InputGate.Allows(GameAction.Reel);

    /// <summary>
    /// 巻き演出（ループ音・巻きアニメ）の停止判定を毎フレーム必ず通す門
    /// 【鳴りっぱなしを防ぐ唯一の保険】。
    ///
    /// <see cref="Update"/> の<b>いちばん最初</b>（ポーズの早期 return より前）で呼ぶ。
    /// 次のどちらかなら巻き演出をたたむ。
    /// <list type="number">
    ///   <item>停止条件が立っている（<see cref="IsReelFeedbackSuspended"/>）</item>
    ///   <item>そもそも巻ける状態ではない（Floating / Reeling / Hooked 以外。
    ///         ＝ 釣り上げ演出中 Catching・狙い Aiming・待機 Idle など）</item>
    /// </list>
    /// 巻きアニメは<b>掛かっていない Reeling 状態のときだけ</b>待機クリップへ戻す
    /// （ヒット中はヒット用クリップ固定・釣り上げ演出中は演出側がアニメを持つため）。
    ///
    /// 時間停止から復帰したときは条件が外れるので、巻き入力があれば
    /// <see cref="UpdateReelSound"/> の通常経路がそのまま鳴らし直す（専用の復帰処理は不要）。
    /// </summary>
    private void UpdateReelFeedbackGate()
    {
        bool reelableState = State == FishState.Floating
                          || State == FishState.Reeling
                          || State == FishState.Hooked;

        if (!IsReelFeedbackSuspended && reelableState) { return; }

        StopReelSound();

        // 掛かっていない巻き取り中だけ、見た目も待機へ戻す（巻きアニメの止め忘れ対策）。
        if (State == FishState.Reeling)
        {
            State = FishState.Floating;
            reelIdleElapsed = 0f;
            CrossFadeBoth(floatClip, playerFloatClip);
        }
    }

    /// <summary>巻き取り音を止める【巻き取り音を止める唯一の入口】。未生成なら何もしない。</summary>
    private void StopReelSound()
    {
        sinceLastReelInput = float.MaxValue;
        if (ResolveReelSoundSource() is { } s && s.IsPlaying) { s.Stop(); }
    }

    /// <summary>
    /// 巻き取り音の音源を返す（初回は生成済みアクタから取得して控える）。
    /// 生成がまだ反映されていない／失敗している場合は null。
    /// </summary>
    private SEED.AudioSource? ResolveReelSoundSource()
    {
        if (reelSoundSource is { IsValid: true } cached) { return cached; }
        if (!reelSoundActor.IsValid) { return null; }
        reelSoundSource = reelSoundActor.GetComponent<SEED.AudioSource>();
        return reelSoundSource;
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
        // 釣り上げ成立でも空振りでも、この先は巻ける状態ではない。
        // 釣り上げ側は State が Catching になり UpdateReeling が二度と走らないので、
        // ここで止めないとループ音が演出中ずっと鳴り続ける（実際に起きていた不具合）。
        StopReelSound();
        fight?.EndFight();             // やり取り（テンション・魚HP）は成否にかかわらずここで畳む
        nibbleDipElapsed = NoDipElapsed;   // 出題の沈みアニメが途中なら必ず戻す（ウキが沈んだまま残らないように）

        // ── 釣り上げ成立 ──
        if (hookedFish is { } caught)
        {
            SEED.Debug.Log($"[Fishing] 釣り上げ: {caught.DisplayName}（サイズ {Fish.FormatSize(caught.DisplaySize)}）");

            // 魚の AI を止める（以後の位置・向き・スケールは CatchPresenter が決める）。
            // 円環クランプの除外登録は<b>外さない</b>: 外すと演出中の魚が FishManager に
            // 出現円環内へ引き戻されてしまう。登録は魚の破棄時に自動で外れる。
            caught.OnCaught();
            hookedFish = null;           // ReleaseFromHook は呼ばない（この個体は演出側が持つ）

            // 「何を釣ったか」を購読側が種類まで辿れるよう控えておく
            // （fishing.catch は表示名しか運ばないため）
            LastCaughtFish = caught;

            State = FishState.Catching;
            // 演出中はマウスの振りを読まないのでカーソルロックを引き直す（解除される）。
            UpdateCursorLock();

            // 演出プレゼンタが未設定なら演出を飛ばす（魚を消して移動へ戻すだけ）
            // ウキの位置は演出側の「水面の基準点」になる（魚の跳ね始め・カメラの高さ・
            // しぶきの位置がすべてここから決まる）ので、この瞬間の値を渡す。
            // 評価バナー（Perfect! / Good!）は釣り上げ時ではなく、各フレーズの回答が
            // 締まった瞬間に FishingFight から出している（ShowFightEvalBanner）。

            if (presenter is not null)
            {
                // 必要なら演出の開始を少しだけ待つ（待ち時間は UpdateCatching が数える。既定 0）。
                // 待っている間もウキ・糸は掛かったときのまま残るので、絵が飛ばない。
                pendingCatchFish = caught;
                pendingCatchFloatPosition = FloatWorldPosition;

                // 「釣れた判定 → カメラが寄る → 釣り上げ演出」の中段をここで始める。
                // 待ち時間は「評価バナーの読ませ時間」と「寄りの秒数」の<b>長いほう</b>。
                // 足し合わせると、寄り切ったあとにただ待つ間延びした時間ができるため。
                BeginCatchZoom(pendingCatchFloatPosition);
                float zoomWait = catchZoomActive ? SEED.Mathf.Max(catchZoomSeconds, 0f) : 0f;
                pendingCatchDelaySeconds = SEED.Mathf.Max(
                    SEED.Mathf.Max(fightEvalLeadSeconds, zoomWait), 0f);
            }
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

    // ─── 岸際の判定とカメラ【2026-09-09 追加】────────────────────

    /// <summary>
    /// 「ウキが岸（竿先）の近くに居るか」を毎フレーム立て直す
    /// 【岸際判定の唯一の実装】。
    ///
    /// 入るのは <see cref="nearShoreDistanceMeters"/> 以内、抜けるのは
    /// 「<see cref="nearShoreDistanceMeters"/> ＋ <see cref="nearShoreExitMarginMeters"/>」超え、
    /// という<b>ヒステリシス</b>にしてあるので、境界でウキが前後しても
    /// 漂流物の出現・操舵・カメラ構図がちらつかない。
    ///
    /// ウキが出ていない（未生成・破棄済み）ときは必ず false に落とす
    /// （<see cref="CurrentFloatDistance"/> が 0 を返すため、素通しにすると
    ///   ウキが無いだけで「岸に居る」と誤判定してしまう）。
    /// </summary>
    private void UpdateNearShore()
    {
        if (uki is not { IsValid: true } floatTf || !IsFloatOut())
        {
            NearShore = false;
            return;
        }

        float distance = CurrentFloatDistance();
        float enterDistance = SEED.Mathf.Max(nearShoreDistanceMeters, 0f);
        float exitDistance = enterDistance + SEED.Mathf.Max(nearShoreExitMarginMeters, 0f);

        NearShore = NearShore ? distance <= exitDistance : distance <= enterDistance;
    }

    /// <summary>
    /// 岸際の巻き中だけ、カメラを「陸側から海を見る」構図へ回り込ませる
    /// 【岸際カメラの唯一の制御点】。
    ///
    /// 構図は<b>プレイヤーの背後</b>ではなく<b>ウキと竿先を結ぶ線の延長上（陸側）</b>で、
    /// ウキを注視する。ウキが操舵で横へずれていても、必ずその線の上に回り込む。
    /// <code>
    /// 注視点   ＝ ウキの位置
    /// 目標方位 ＝ ウキ → 竿先 の水平方向（＝陸側）
    /// 位置     ＝ 注視点 ＋ 方位ベクトル × shoreCamDistance ＋ 上 × shoreCamHeight
    /// </code>
    /// 方位は瞬間的に切り替えず、<see cref="shoreCamOrbitSpeedDegPerSec"/> の速さで
    /// 最短回りに近づける（＝ぐるりと回り込んで見える）。
    ///
    /// 効かせるのは<b>巻ける区間（隙＝<see cref="FishingFight.Phase.Rest"/>）</b>だけ。
    /// 出題・回答フェーズには専用の構図（CallCameraTarget / AnswerCameraTarget）が
    /// あるので、そちらを潰さないよう上書きを返す。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数（回り込み量の算出に使う）。</param>
    private void UpdateShoreCamera(float deltaTime)
    {
        bool wantsShoreView = NearShore
                           && State == FishState.Hooked
                           && FightPhase == FishingFight.Phase.Rest;

        if (!wantsShoreView || cameraMove is not { } cam || uki is not { IsValid: true } floatTf)
        {
            ReleaseCameraOverride();
            return;
        }

        var focus = floatTf.Position;
        float goalAzimuth = ShoreAzimuthDegrees(focus);

        // 回り込みの開始方位は「いまカメラが居る方位」。こうすると上書きへ切り替えた
        // 最初のフレームに画が飛ばず、そこから陸側へ回り込む動きになる。
        if (!cameraOverrideActive)
        {
            cameraOverrideActive = true;
            shoreCamAzimuthDegrees = CurrentCameraAzimuthDegrees(focus, goalAzimuth);
        }

        // 目標方位へ最短回りで近づく（1 フレームの上限 ＝ 回り込み速度 × dt）
        float delta = ShortestAngleDelta(shoreCamAzimuthDegrees, goalAzimuth);
        float maxStep = SEED.Mathf.Max(shoreCamOrbitSpeedDegPerSec, 0f)
                      * SEED.Mathf.Max(deltaTime, 0f);
        shoreCamAzimuthDegrees += SEED.Mathf.Clamped(delta, -maxStep, maxStep);

        ComputeShoreSidePose(
            focus, shoreCamAzimuthDegrees, shoreCamDistance, shoreCamHeight,
            out var position, out var rotation);

        // 補間は CameraMove 側（positionLerpRate / rotationLerpRate）に任せるので snap は要らない。
        cam.SetOverrideGoal(position, rotation, snap: false);
    }

    /// <summary>
    /// カメラの姿勢上書きを返す【上書き解除の唯一の出口】。
    ///
    /// 自分が握っていたときだけ外す。無条件に
    /// <see cref="CameraMove.ClearOverrideGoal"/> を呼ぶと、
    /// 釣り上げ演出（<see cref="CatchPresenter"/>）が握っている構図まで外してしまう。
    /// </summary>
    private void ReleaseCameraOverride()
    {
        if (!cameraOverrideActive) { return; }

        cameraOverrideActive = false;
        cameraMove?.ClearOverrideGoal();
    }

    /// <summary>
    /// 「陸側」の方位角（度）＝ ウキから見た竿先の水平方向を返す
    /// 【岸際カメラと寄りカメラが共有する唯一の方位】。
    ///
    /// ウキと竿先がほぼ重なって方位が定まらないときは、
    /// プレイヤーの真後ろ（＝向いている方向の逆）を陸側とみなす。
    /// </summary>
    /// <param name="focus">注視点（通常はウキの位置）。</param>
    private float ShoreAzimuthDegrees(SEED.Vector3 focus)
    {
        var rodTip = ReelTargetPosition();
        float dx = rodTip.x - focus.x;
        float dz = rodTip.z - focus.z;
        if (dx * dx + dz * dz < SqrEpsilon) { return transform.Rotation.y + HalfTurnDegrees; }

        return SEED.Mathf.Atan2(dx, dz) * SEED.Mathf.Rad2Deg;
    }

    /// <summary>
    /// いまカメラが注視点から見てどの方位に居るかを度で返す
    /// （回り込みの開始方位に使う）。
    /// カメラが取れない・注視点と重なっているときは <paramref name="fallback"/> を返す。
    /// </summary>
    /// <param name="focus">注視点。</param>
    /// <param name="fallback">方位が定まらないときに返す値（度）。</param>
    private float CurrentCameraAzimuthDegrees(SEED.Vector3 focus, float fallback)
    {
        if (ResolveCameraTransform() is not { } camTf) { return fallback; }

        var position = camTf.Position;
        float dx = position.x - focus.x;
        float dz = position.z - focus.z;
        if (dx * dx + dz * dz < SqrEpsilon) { return fallback; }

        return SEED.Mathf.Atan2(dx, dz) * SEED.Mathf.Rad2Deg;
    }

    /// <summary>
    /// 注視点まわりの球面座標からカメラの姿勢（位置・回転）を組み立てる
    /// 【岸際カメラ／寄りカメラが共有する唯一の構図計算】。
    /// </summary>
    /// <param name="focus">注視点（ワールド）。</param>
    /// <param name="azimuthDegrees">注視点から見たカメラの方位角（度）。</param>
    /// <param name="horizontalDistance">注視点からの水平距離（メートル）。</param>
    /// <param name="height">注視点からの高さ（メートル）。</param>
    /// <param name="position">求まったカメラ位置。</param>
    /// <param name="rotation">求まったカメラ回転（オイラー角・度）。</param>
    private static void ComputeShoreSidePose(
        SEED.Vector3 focus, float azimuthDegrees,
        float horizontalDistance, float height,
        out SEED.Vector3 position, out SEED.Vector3 rotation)
    {
        float radians = azimuthDegrees * SEED.Mathf.Deg2Rad;
        position = new SEED.Vector3(
            focus.x + SEED.Mathf.Sin(radians) * horizontalDistance,
            focus.y + height,
            focus.z + SEED.Mathf.Cos(radians) * horizontalDistance);

        rotation = LookRotationTo(position, focus);
    }

    /// <summary>
    /// <paramref name="from"/> から <paramref name="to"/> を見る回転（オイラー角・度）を返す。
    /// エンジンの規約に合わせて pitch は「正で下を向く」符号にする。
    /// 2 点が重なっているときは無回転を返す。
    /// </summary>
    /// <param name="from">視点（カメラ位置）。</param>
    /// <param name="to">注視点。</param>
    private static SEED.Vector3 LookRotationTo(SEED.Vector3 from, SEED.Vector3 to)
    {
        float dx = to.x - from.x;
        float dy = to.y - from.y;
        float dz = to.z - from.z;
        float length = SEED.Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < DivideEpsilon) { return SEED.Vector3.Zero; }

        float yaw = SEED.Mathf.Atan2(dx, dz) * SEED.Mathf.Rad2Deg;
        float pitch = -SEED.Mathf.Asin(SEED.Mathf.Clamped(dy / length, -1f, 1f)) * SEED.Mathf.Rad2Deg;
        return new SEED.Vector3(pitch, yaw, 0f);
    }

    /// <summary>
    /// 角度の最短回りの差（度・−180〜180）を返す。
    /// 350° → 10° のような巻き戻りで逆回りしないようにするための共通計算。
    /// </summary>
    /// <param name="from">現在の角度（度）。</param>
    /// <param name="to">目標の角度（度）。</param>
    private static float ShortestAngleDelta(float from, float to)
    {
        float delta = (to - from) % FullTurnDegrees;
        if (delta > HalfTurnDegrees) { delta -= FullTurnDegrees; }
        if (delta < -HalfTurnDegrees) { delta += FullTurnDegrees; }
        return delta;
    }

    // ─── 釣り上げ時のカメラ寄り【2026-09-09 追加】──────────────────

    /// <summary>
    /// 釣れた判定の瞬間に、カメラの寄りを仕込む【寄りの唯一の開始点】。
    ///
    /// 開始姿勢は<b>その瞬間のカメラの実姿勢</b>なので、直前がどの構図でも画が飛ばない。
    /// 寄り先は岸際カメラと同じ「陸側から海（ウキ）を見る」方位のまま、
    /// 距離と高さだけを <see cref="catchZoomDistance"/> / <see cref="catchZoomHeight"/> へ詰めたもの。
    ///
    /// カメラ参照が無い・寄りの秒数が 0 以下・カメラの実体が取れない場合は
    /// 何も仕込まない（＝<see cref="catchZoomActive"/> が false のままなので
    /// 呼び出し側は待たずに演出へ入る）。
    /// </summary>
    /// <param name="focus">寄り先の注視点（釣れた瞬間のウキの位置）。</param>
    private void BeginCatchZoom(SEED.Vector3 focus)
    {
        catchZoomActive = false;
        catchZoomElapsed = 0f;

        if (cameraMove is null) { return; }
        if (catchZoomSeconds <= 0f) { return; }
        if (ResolveCameraTransform() is not { } camTf) { return; }

        catchZoomStartPosition = camTf.Position;
        catchZoomStartRotation = camTf.Rotation;

        ComputeShoreSidePose(
            focus, ShoreAzimuthDegrees(focus), catchZoomDistance, catchZoomHeight,
            out catchZoomGoalPosition, out catchZoomGoalRotation);

        catchZoomActive = true;
    }

    /// <summary>
    /// 釣り上げの寄りを 1 フレーム進める【寄りの唯一の更新点】。
    ///
    /// 開始姿勢から寄り先まで smoothstep（3t² − 2t³）で補間し、
    /// <see cref="CameraMove.SetOverrideGoal"/> へ<b>カット指定</b>で渡す
    /// （補間はこちらが持つので、CameraMove 側の追従で二重に鈍らせない）。
    /// 寄り切ったら進行を止めるが、上書きはそのまま残す ―― 直後に始まる
    /// <see cref="CatchPresenter"/> が白の裏で自分の構図へ差し替えるため、
    /// ここで通常の追従へ戻すと 1 フレームだけ画が飛ぶ。
    /// </summary>
    /// <param name="unscaledDeltaTime">このフレームの実時間の経過秒数。</param>
    private void UpdateCatchZoom(float unscaledDeltaTime)
    {
        if (!catchZoomActive) { return; }
        if (cameraMove is not { } cam) { catchZoomActive = false; return; }

        catchZoomElapsed += SEED.Mathf.Max(unscaledDeltaTime, 0f);

        float span = SEED.Mathf.Max(catchZoomSeconds, DivideEpsilon);
        float t = SEED.Mathf.Clamped01(catchZoomElapsed / span);
        float eased = t * t * (SmoothStepSquareCoefficient - SmoothStepCubicCoefficient * t);

        var position = new SEED.Vector3(
            SEED.Mathf.Lerp(catchZoomStartPosition.x, catchZoomGoalPosition.x, eased),
            SEED.Mathf.Lerp(catchZoomStartPosition.y, catchZoomGoalPosition.y, eased),
            SEED.Mathf.Lerp(catchZoomStartPosition.z, catchZoomGoalPosition.z, eased));

        // 回転は軸ごとに最短回りで補間する（180 度をまたぐときに逆回りしないように）
        var rotation = new SEED.Vector3(
            catchZoomStartRotation.x + ShortestAngleDelta(catchZoomStartRotation.x, catchZoomGoalRotation.x) * eased,
            catchZoomStartRotation.y + ShortestAngleDelta(catchZoomStartRotation.y, catchZoomGoalRotation.y) * eased,
            catchZoomStartRotation.z + ShortestAngleDelta(catchZoomStartRotation.z, catchZoomGoalRotation.z) * eased);

        cam.SetOverrideGoal(position, rotation, snap: true);
        cameraOverrideActive = true;

        if (t >= 1f) { catchZoomActive = false; }
    }

    // ─── バトル評価バナー ────────────────────────────────────

    /// <summary>
    /// 1 フレーズの評価（Perfect! / Good!）を出す【評価表示の唯一の場所】。
    ///
    /// <see cref="FishingFight"/> が回答フェーズを締めた瞬間（回答 → 隙）に、
    /// そのフレーズが完璧だったか（全打点 Excellent かつ余分なクリック無し）を渡して呼ぶ。
    /// </summary>
    /// <param name="perfect">直前のフレーズが完璧だったか。</param>
    public void ShowFightEvalBanner(bool perfect)
    {

        // 第 4 引数は「完璧かどうか」そのもの。バナー側は判断せず、
        // 弾ける粒を金（完璧）と白（通常）のどちらにするかにだけ使う。
        FightEvalBanner.Show(
            fightEvalBannerActorPath,
            perfect ? fightEvalPerfectLabel : fightEvalGoodLabel,
            perfect ? fightEvalPerfectColor : fightEvalGoodColor,
            perfect);

        // 評価に合わせた SE をバナーと同じ瞬間に鳴らす（一発再生。空パスなら無音）。
        string se = perfect ? fightEvalPerfectSePath : fightEvalGoodSePath;
        if (!string.IsNullOrWhiteSpace(se)) { SEED.Audio.Play(se, fightEvalSeVolume); }
    }

    /// <summary>
    /// 釣り上げ演出の開始待ちを取り消す【待ち状態を捨てる唯一の場所】。
    /// 魚そのものはここでは触らない（破棄の責任は演出側／中断処理側にある）。
    /// </summary>
    private void ClearPendingCatchBegin()
    {
        pendingCatchFish = null;
        pendingCatchDelaySeconds = 0f;
    }

    // ─── デバッグコマンド（開発・AI 検証用）──────────────────

    /// <summary>
    /// デバッグコマンドを登録済みか（パッケージ版では登録しないので常に false）。
    /// <see cref="OnDestroy"/> の解除を登録と対称にするために持つ。
    /// </summary>
    private bool debugCommandsRegistered;

    /// <summary>
    /// デバッグコマンド名: 釣り上げ演出をその場で起こす。
    /// <c>seed_script_debug(name:"catch_test", arg:"<魚のアクタ名>")</c> で叩く。
    /// </summary>
    private const string DebugCommandCatchTest = "catch_test";

    /// <summary>
    /// デバッグコマンド名: 図鑑（釣果記録）を全消しする。
    /// <c>seed_script_debug(name:"records_reset")</c> で叩く（引数は不要）。
    ///
    /// 図鑑の「未捕獲の見た目」や初捕獲演出は、一度釣ってしまうと二度と確認できない。
    /// 釣りシーンと図鑑シーンの<b>両方</b>から同じ名前で叩けるよう、
    /// 本スクリプト（釣りシーンに常駐）と <c>Zukan</c>（図鑑シーンに常駐）の
    /// 両方が同じ名前で登録している。
    /// </summary>
    private const string DebugCommandRecordsReset = "records_reset";

    /// <summary>
    /// デバッグコマンド名: HIT バナーの演出だけをその場で流す。
    /// <c>seed_script_debug(name:"hit_test", arg:"<魚の表示名 もしくは レベル数値>")</c> で叩く。
    ///
    /// ヒットは「アタリ → 合わせ成功」を通らないと起きないため、
    /// バナーの絵（帯・レベル配色・火花）を確認する手段が無かった。
    /// <b>釣りの状態は一切変えず</b>、演出の入口（<see cref="ShowHitBanner"/>）だけを叩く。
    /// </summary>
    private const string DebugCommandHitTest = "hit_test";

    /// <summary>
    /// レベルを数値で直接指定されたときに、バナーへ渡す魚名（種類が無いことを示す）。
    /// </summary>
    private const string DebugHitTestFallbackName = "デバッグ";

    /// <summary>
    /// <see cref="DebugCommandHitTest"/> のハンドラ【HIT バナーだけを流す近道】。
    ///
    /// 引数の解釈は 3 通り:
    /// <list type="bullet">
    ///   <item>数値（例 <c>"4"</c>）… そのレベルでバナーを流す（魚が 1 匹も居なくても確認できる）</item>
    ///   <item>魚の表示名 … その種類のうち一番近い個体のレベル・名前で流す</item>
    ///   <item>空 … プレイヤー（竿先）に一番近い魚で流す</item>
    /// </list>
    /// 対象が見つからないときは警告だけ出して何もしない（状態を壊さない）。
    /// </summary>
    /// <param name="arg">魚の表示名、またはレベルの数値。空なら一番近い魚。</param>
    private void HandleHitTestCommand(string arg)
    {
        string wanted = arg is null ? string.Empty : arg.Trim();

        // 数値ならレベル直接指定として扱う（魚が湧いていない場面でも絵を確認できる）
        if (int.TryParse(wanted, out int level))
        {
            if (hitBanner is not { } directBanner)
            {
                SEED.Debug.LogWarning("[Fishing] hit_test: HitBanner が未設定のため出せない");
                return;
            }
            SEED.Debug.Log($"[Fishing] hit_test: レベル {level} 指定で HIT バナーを流す");
            directBanner.Play(level, DebugHitTestFallbackName);
            return;
        }

        if (SelectDebugCatchTarget(wanted) is not { } target)
        {
            SEED.Debug.LogWarning($"[Fishing] hit_test: 対象の魚が見つからない（arg=\"{wanted}\"）");
            return;
        }

        SEED.Debug.Log(
            $"[Fishing] hit_test: {target.DisplayName}（Lv{target.Level}）で HIT バナーを流す");
        ShowHitBanner(target);
    }

    /// <summary>
    /// <see cref="DebugCommandRecordsReset"/> のハンドラ。
    /// 釣果記録だけを消す（進行フラグなど他のセーブデータは触らない）。
    /// </summary>
    /// <param name="arg">引数（使わない）。</param>
    private void HandleRecordsResetCommand(string arg)
    {
        int deleted = FishRecords.ResetAll();
        SEED.Debug.Log($"[Fishing] records_reset: 釣果記録を消去した（削除キー {deleted} 本）");
    }

    /// <summary>
    /// <see cref="DebugCommandCatchTest"/> のハンドラ
    /// 【釣り上げ演出を検証するための唯一の近道】。
    ///
    /// <b>やること</b>は「本物の釣り上げと同じ入口（<see cref="FinishReeling"/>）へ、
    /// 掛かった魚とウキの位置を用意して飛び込む」だけ。演出の内容には一切触らないので、
    /// ここを通した結果は<b>実際に釣ったときと同じ</b>になる
    /// （＝この経路で確認した見た目は本番でもそのまま出る）。
    ///
    /// <b>手順</b>
    /// 1. 進行中の釣り（キャスト・やり取り）を畳んで待機へ戻す（状態を確定させる）
    /// 2. 対象の魚を決める（引数が表示名ならその種類、空ならプレイヤーに一番近い魚）
    /// 3. プレイヤーを釣り姿勢へ入れる（本物の釣り上げと同じ前提を揃える）
    /// 4. ウキを「魚の真上の水面」へ置く（演出の水面基準点になる）
    /// 5. 魚を掛かった状態にして <see cref="FinishReeling"/> を呼ぶ
    ///
    /// <b>Play 中以外</b>では届かない（ランタイムが受け取り自体を拒否する）。
    /// </summary>
    /// <param name="arg">対象の魚の表示名（種類名）。空ならプレイヤーに一番近い魚。</param>
    private void HandleCatchTestCommand(string arg)
    {
        if (SelectDebugCatchTarget(arg) is not { } target)
        {
            SEED.Debug.LogWarning($"[Fishing] catch_test: 対象の魚が見つからない（arg=\"{arg}\"）");
            return;
        }

        // 1. 進行中の釣りを畳む（ウキ・糸・やり取り・アタリをすべて初期化する）
        CancelToIdle();

        // 2. プレイヤーを釣り姿勢へ入れる。
        //    毎フレームの更新は「待機以外なのに釣り姿勢でない」状態を見つけると
        //    問答無用で畳む（Update の IsPlayerFishing 判定）ので、
        //    ここを飛ばすと演出が次のフレームで即座にキャンセルされる。
        if (playerMove is not { } pm || !pm.EnterFishingStance())
        {
            SEED.Debug.LogWarning(
                "[Fishing] catch_test: 釣り姿勢へ入れないため中止（経路移動モードで実行すること）");
            return;
        }

        // 3. ウキを魚の真上の水面へ。演出はこの位置を「水面の基準点」に使うので、
        //    高さは必ず水面へ合わせる（魚の居る深さをそのまま渡すと構図が沈む）。
        var fishPosition = target.Transform.Position;
        SetFloatPosition(new SEED.Vector3(fishPosition.x, WaterSurfaceY(), fishPosition.z));

        // 4. 本物の釣り上げと同じ入口へ入る。
        //    hookedFish を埋めてから呼ぶのが「釣り上げ成立」の条件（FinishReeling 参照）。
        //    連鎖を経ていない 1 匹なので、履歴は空にしてから入る。
        chainCatchHistory.Clear();
        hookedFish = target;
        SEED.Debug.Log($"[Fishing] catch_test: {target.DisplayName} で釣り上げ演出を起こす");
        FinishReeling();
    }

    /// <summary>
    /// <see cref="HandleCatchTestCommand"/> の対象になる魚を選ぶ。
    ///
    /// 引数が表示名（種類名。例 <c>トビウオ</c>）なら<b>その種類の魚のうち一番近い個体</b>、
    /// 空ならプレイヤー（竿先）に一番近い魚を返す。1 匹も居なければ null。
    ///
    /// アクタ名ではなく表示名で引くのは、魚が実行時に生成されるためアクタ名が
    /// 一意に決まらない（連番が付く）のに対し、表示名は図鑑・釣果パネルに
    /// 出る名前そのもので、指定する側が確実に知っているため。
    /// </summary>
    /// <param name="displayName">魚の表示名（種類名）。空・空白なら「一番近い魚」。</param>
    private Fish? SelectDebugCatchTarget(string displayName)
    {
        bool byName = !string.IsNullOrWhiteSpace(displayName);
        string wanted = byName ? displayName.Trim() : string.Empty;

        var origin = RodTipWorldPosition;
        Fish? nearest = null;
        float nearestSqr = float.MaxValue;

        foreach (var fish in Fish.All)
        {
            // 破棄済みの個体が Fish.All に残っていることがある（前回の演出で消した魚など）。
            // アクタのハンドルだけでは見分けが付かないので、トランスフォームまで見て弾く。
            if (fish is null || !fish.Actor.IsValid || !fish.Transform.IsValid) { continue; }
            // 演出中／演出済みの個体は選ばない（同じ魚で二重に演出を起こさないため）
            if (fish.State == Fish.BehaviorState.Caught) { continue; }

            // 種類の指定があるときは、名前が違う個体を数えない（近さの比較は同じ式を使う）
            if (byName
             && !string.Equals(fish.DisplayName, wanted, System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            float sqr = (fish.Transform.Position - origin).SqrMagnitude;
            if (sqr >= nearestSqr) { continue; }
            nearestSqr = sqr;
            nearest = fish;
        }
        return nearest;
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

        // 評価バナーを読ませるための待ちが残っているあいだは、演出をまだ始めない。
        // ここで早期 return するので、この間は「Phase == None ＝ 演出完了」と
        // 誤判定されて移動へ戻ってしまうこともない。
        if (pendingCatchFish is not null)
        {
            // 待っているあいだにカメラをウキ（魚）へ寄せ切る。
            // 寄り切った時点で待ちも終わるようにしてあるので、
            // 「寄り切ったら演出開始」の順序がこの 2 行だけで保たれる。
            UpdateCatchZoom(SEED.Time.UnscaledDeltaTime);

            pendingCatchDelaySeconds -= SEED.Time.UnscaledDeltaTime;
            if (pendingCatchDelaySeconds > 0f) { return; }

            Fish start = pendingCatchFish;
            ClearPendingCatchBegin();

            // 連鎖の履歴（最初に掛かった魚 → … → 直前の魚）をそのまま渡す。
            // 演出側はこれに「最後に釣り上げた 1 匹」を足した順で釣果を見せ、
            // 1 匹ずつ図鑑へ登録する。渡した控えは演出側が写し取るので、
            // ここで持ち続ける必要はない（次のヒットで TryHook が空にする）。
            p.Begin(start, pendingCatchFloatPosition, chainCatchHistory);
            return;   // 開始したフレームは進めない（従来も Begin の次フレームから Tick していた）
        }

        p.Tick(deltaTime);

        // ── ウキの扱い（釣り上げ演出中の唯一の分岐）──────────────
        // 魚が跳ねる区間（Fade / SlowArc）はウキを畳まない。さらに演出が跳びの位置を
        // 指定してきたら（CatchPresenter.FloatFollowPosition）、ウキをそこへ置いて
        // 魚と一緒に水面から引き抜かれるように見せる。
        // 釣り糸（LineRenderer）は LateUpdate の UpdateLine が「竿先 → ウキ」で毎フレーム
        // 張り直し、その表示判定 IsFloatOut() は Fade / SlowArc を<b>含む</b>ので、
        // ここでウキを動かすだけで糸も一緒に跳ね上がる（追加の制御は要らない）。
        // 跳びの区間を抜けたら従来どおり手元へ畳む
        // （ParkFloatHidden は表示フラグと位置を引き直すだけなので毎フレーム呼んで安全）。
        if (p.Phase is CatchPresenter.CatchPhase.Fade
                    or CatchPresenter.CatchPhase.LowAngle
                    or CatchPresenter.CatchPhase.SlowArc)
        {
            if (p.FloatFollowPosition is { } floatGoal)
            {
                ShowFloat();
                SetFloatPosition(floatGoal);
            }
        }
        else
        {
            ParkFloatHidden();
        }

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
        // チュートリアル中は巻き取りを止められる（通常時は常に許可）。
        // ここは全ての巻き経路（UpdateReeling / FishingFight.Tick）の唯一の入力源なので、
        // 1 か所ゲートを挟めば巻きに関わる挙動すべてが止まる。
        if (!InputGate.Allows(GameAction.Reel)) { return 0f; }
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
        // チュートリアル中は操舵だけを止められる（通常時は常に許可）。
        // 巻き取り（ホイール）は GameAction.Reel、左右の操舵は GameAction.Aim で分けて見る。
        // こうすると「巻けるが向きは変えられない（＝まっすぐ巻く）」場面が作れる。
        if (steerFactor > 0f && InputGate.Allows(GameAction.Aim))
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
    /// 現在の水面 Y（ワールド）を返す【水面高さの唯一の問い合わせ口】。
    /// <see cref="water"/> 未設定なら「竿先 −<see cref="waterLevelFallbackDrop"/>」を仮の水面とする。
    ///
    /// 漂流物（<see cref="DriftItem"/>）も水面に浮くためにこれを読むので <b>public</b>
    /// （漂流物は動的生成されるため、参照フィールドで水面を注入できない）。
    /// </summary>
    public float WaterSurfaceY()
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
    /// </summary>
    private float CurrentDipDepth()
    {
        // ヒット中は、まず biteDipDepth だけ沈んでいる（わらしべで乗り換わっても Hooked のまま
        // なので、この沈みは連鎖の最中もずっと維持される）。ヒット中でなければ 0。
        float baseDip = IsHooked ? biteDipDepth : 0f;

        if (State == FishState.HookWindow)
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
    /// <see cref="FishState.Nibbling"/> / <see cref="FishState.HookWindow"/> の毎フレーム更新
    /// 【アタリ進行の唯一の更新点】。
    ///
    /// わらしべ連鎖は前アタリ・合わせを経由しないため、ここには一切現れない
    /// （<see cref="TryEatHookedFish"/> を参照）。
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
        // アタリの主が居なくなった（魚が破棄されたなど）ら元の状態へ戻す
        if (nibblingFish is not { } fish)
        {
            ClearBiteTiming();
            State = FishState.Floating;
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

        // 3) 前アタリ中
        if (State == FishState.Nibbling)
        {
            if (swung)
            {
                // まだ食っていないのに合わせた＝早合わせ
                FailBite("早合わせ");
                return;
            }

            // チュートリアルの説明台詞を表示している間（TutorialRules.BiteSuppressed）は、
            // 前アタリから本アタリ（HookWindow）へ進むカウントダウンも同様に保留する。
            // （入力ゲートで Hook は既に閉じられているので swung は通常発生しないが、
            //  安全策としてタイマー進行自体も止めておく）。
            if (TutorialRules.Active && TutorialRules.BiteSuppressed) { return; }

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
        State = FishState.HookWindow;
        reactionElapsed = 0f;
        nibbleDipElapsed = NoDipElapsed;
        PlaySe(hookSePath, hookSeVolume);
        // 本アタリ（合わせの受付が開いた）
        SEED.Events.Raise(FishingEvents.Bite);
        SEED.Debug.Log($"[Fishing] 本アタリ! {fish.DisplayName}");
    }

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

        // 判定が確定した（引数は判定名。Miss も含めて必ずここで 1 回だけ流す）
        SEED.Events.Raise(FishingEvents.HookJudged, judgement.ToString());

        if (judgement == HookJudgement.Miss)
        {
            FailBite("遅すぎ");
            return;
        }

        // ── ヒット成立 ──
        ClearBiteTiming();
        LastJudgement = judgement;
        ShowJudgement(judgement);

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
        var fish = nibblingFish;
        ClearBiteTiming();
        LastJudgement = HookJudgement.Miss;
        ShowJudgement(HookJudgement.Miss);
        State = FishState.Floating;

        // チュートリアルの「合わせ」練習中は、ミスしても魚を逃がさず前アタリからやり直す。
        // 逃がすと次のアタリが来るまで待たされ、練習にならないため。
        // 上書きが無ければ（TutorialRules.Active が false なら）従来どおり魚は逃げる。
        if (TutorialRules.Active && TutorialRules.RebiteAfterHookMiss && fish is { } retryFish)
        {
            if (BeginNibbling(retryFish))
            {
                SEED.Debug.Log($"[Fishing] Miss（{reason}）→ チュートリアルのため同じ魚で再アタリ");
                return;
            }
        }

        fish?.ReleaseFromHook();       // 魚は逃げる（Escape → クールダウン付きで回遊へ）
        SEED.Debug.Log($"[Fishing] Miss（{reason}）");
    }

    /// <summary>
    /// アタリ進行を外部都合で打ち切る（釣り姿勢の解除・スクリプト破棄など）。
    /// 判定は表示せず、魚だけ逃がす。
    /// </summary>
    private void AbortBiteTiming()
    {
        var fish = nibblingFish;
        ClearBiteTiming();
        fish?.ReleaseFromHook();
    }

    /// <summary>アタリ進行の内部状態をすべて初期化する（状態遷移は行わない）。</summary>
    private void ClearBiteTiming()
    {
        nibblingFish = null;
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
    /// 掛かる可能性がある状態（<see cref="FishState.Nibbling"/> /
    /// <see cref="FishState.HookWindow"/>）でだけこの関数を呼び、振りを読む。
    /// アタリが無い状態（<see cref="FishState.Floating"/> / <see cref="FishState.Reeling"/>）は
    /// 呼び出し側（<see cref="Update"/> の switch）がそもそもこの関数を呼ばないため、
    /// その状態での左クリックは完全に無視される（ウキは跳ねず、演出・SE・イベントも出ない）。
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
    /// この関数は Nibbling / HookWindow でしか呼ばれないので、
    /// 姿勢に入る押下がここへ届くことはない。キャストは左クリックを押したまま成立し、
    /// 押しっぱなしのあいだは押下フレームが来ないため、着水後に<b>改めて押し直した</b>
    /// クリックだけが合わせになる。
    ///
    /// ここで足すのは「振りが成立した瞬間に必ず起きること」だけ:
    /// <see cref="SwingSerial"/> の加算（魚への通知）と効果音。
    ///
    /// <b>振りの結果は呼び出し側が決める</b>（状態ごとに意味が違うため）:
    /// - Nibbling           … 早合わせ（Miss。魚は逃げる）
    /// - HookWindow         … 合わせ判定（Excellent / Great / Nice / Miss）
    /// </summary>
    /// <returns>このフレームに振りが成立したら true。</returns>
    private bool UpdateSwingDetection()
    {
        // チュートリアル中は「竿を振る（合わせ）」を止められる（通常時は常に許可）
        if (!InputGate.Allows(GameAction.Hook)) { return false; }
        if (!SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left)) { return false; }

        // 番号は振るたびに増やす（魚は状態を見ずに変化だけを見る）
        SwingSerial++;
        PlaySe(swingSePath, swingSeVolume);
        return true;
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
