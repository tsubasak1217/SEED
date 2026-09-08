// ============================================================================
//  CatchPresenter.cs
//  釣り上げ演出（ホワイトアウト → スロー放物線 → 釣果パネル）の進行。
// ============================================================================

using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext

/// <summary>
/// 釣り上げ演出（<c>Catching</c> 状態）の進行だけを担うプレゼンタ。
///
/// <b>プレイヤーアクタに付ける</b>（<see cref="FishingController"/> と同じアクタに
/// 2 本目のスクリプトスロットとして置き、コントローラの <c>presenter</c> フィールドから
/// 参照する）。
///
/// <b>責務の分割</b>
/// - 釣りの進行（キャスト〜巻き取り〜ヒット判定）… <see cref="FishingController"/>
/// - <b>釣り上げた瞬間からの見せ場の進行</b> … 本スクリプト
/// - 釣果の中身の表示（絵・名前・サイズ・図鑑登録）… <see cref="ResultPanel"/>
/// - 釣果の保存（ベスト・釣った数）… <see cref="FishRecords"/>
/// - カメラの追従補間そのもの … <see cref="CameraMove"/>
///
/// 本スクリプトは「どこを見せるか（目標トランスフォームの座標）」だけを決め、
/// カメラ本体は一切動かさない。カメラは <see cref="CameraMove"/> が
/// <see cref="FishingController.State"/> と <see cref="FishingController.CatchPhase"/> を見て
/// 目標を切り替える。
///
/// <b>フェーズ（<see cref="CatchPhase"/>）と時間</b>
/// <code>
/// Fade    … whiteoutFadeInSeconds 秒で白へ沈み、
///           whiteoutHoldSeconds 秒の真っ白を保持する。
///           真っ白になった最初のフレームでカット（SwitchToSlowArcComposition）。
/// SlowArc … 白が whiteoutFadeOutSeconds 秒で晴れ、水面を真横から見る構図で
///           魚がウキの位置から放物線を描いて跳ね上がる（Time.Scale = slowScale）。
///           arcSeconds × arcApexRatio 秒（＝頂点）で魚を隠し、次へ。
/// Result  … Time.Scale を戻し、ResultPanel を開く。
///           パネルが閉じ切る（ResultPanel.IsActive が false になる）まで待つ。
/// Close   … closeSeconds 秒の間を置いてから後始末（魚の破棄・カメラ復帰）。
/// </code>
///
/// <b>カット（<see cref="SwitchToSlowArcComposition"/>）</b>
/// 画面が完全に白いあいだに構図を切り替えるので、視点の飛びが見えない。
/// - 横カメラの目標（<see cref="catchCameraTarget"/>）を「ウキ→プレイヤーの向きに対して
///   直角・水面のすこし上」へ置き、<see cref="CameraMove.RequestSnap"/> で<b>補間せず</b>飛ばす。
///   カメラ距離は魚のサイズランクぶん遠のく（大きい魚ほど弧が大きいため）。
/// - 魚をウキから外し、水面のすぐ下（<see cref="fishSubmergeDepth"/>）へ置く。
/// - しぶき（<see cref="splashActorPath"/> のパーティクル）と水音を出す。
///
/// <b>時間軸</b>
/// スロー中（<c>Time.Scale</c> を下げている区間）でも演出の秒数がぶれないよう、
/// 本スクリプトの進行はすべて<b>実時間</b>（<c>Time.UnscaledDeltaTime</c>）で数える。
/// <see cref="Tick"/> の引数の経過秒（ゲーム時間）は意図的に使わない。
/// </summary>
public class CatchPresenter : SEEDScript
{
    // ─── フェーズ ─────────────────────────────────────────────

    /// <summary>
    /// 釣り上げ演出の進行フェーズ。
    ///
    /// <see cref="CameraMove"/> は「<c>Fade</c> なら通常の構図のまま／それ以降は
    /// 横カメラの目標」という 2 分岐だけで済むよう順序どおりに並べてある。
    /// スクリプトはファイル名＝型名で 1 ファイル 1 クラスとして扱われるため、
    /// この列挙型は独立ファイルにせず本クラスの入れ子として定義する
    /// （外部からは <c>CatchPresenter.CatchPhase</c> で参照できる）。
    /// </summary>
    public enum CatchPhase
    {
        /// <summary>演出していない（待機）。</summary>
        None,

        /// <summary>白へフェードイン＋真っ白の保持。保持へ入る瞬間に構図と魚を差し替える。</summary>
        Fade,

        /// <summary>白が晴れ、魚が水面から放物線を描いて跳ね上がる（スロー）。</summary>
        SlowArc,

        /// <summary>釣果パネルを開き、閉じ切るまで待つ。</summary>
        Result,

        /// <summary>後始末の間（この後 <see cref="None"/> へ戻る）。</summary>
        Close,
    }

    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>ベクトルの「長さがほぼ 0」を判定する二乗長のしきい値。</summary>
    private const float SqrEpsilon = 1e-6f;

    /// <summary>0 除算を避けるための「実質 0」しきい値（秒数などの分母に使う）。</summary>
    private const float DivideEpsilon = 1e-4f;

    /// <summary>「半分」を表す係数（中点・半角など、2 で割る場面の共通定数）。</summary>
    private const float Half = 0.5f;

    /// <summary>
    /// 放物線の頂点係数。<c>4·h·t·(1−t)</c> は t=0.5 でちょうど h になるので、
    /// 「頂点の高さ」をそのままインスペクタで指定できる。
    /// </summary>
    private const float ParabolaPeakCoefficient = 4f;

    /// <summary>通常時のゲーム時間の速さ（スローから戻すときの値）。</summary>
    private const float TimeScaleNormal = 1f;

    /// <summary>サイズランク S の段位（カメラ距離の加算段数。C=0 から数える）。</summary>
    private const int RankStepS = 3;

    /// <summary>サイズランク A の段位。</summary>
    private const int RankStepA = 2;

    /// <summary>サイズランク B の段位。</summary>
    private const int RankStepB = 1;

    /// <summary>サイズランク C（既定）の段位。</summary>
    private const int RankStepC = 0;

    // ベストサイズ・ベストランク・釣った数の保存キーは FishRecords が一元管理する。

    // ─── 参照（インスペクタで割り当てる）───────────────────────

    /// <summary>
    /// 釣り上げ演出中のカメラ目標トランスフォーム（トップレベルの空アクタ「CatchCameraTarget」）。
    ///
    /// 真っ白の瞬間に本スクリプトが「ウキ→プレイヤーの向きに対して直角・水面のすこし上」の
    /// 姿勢へ置き直し、<see cref="CameraMove.RequestSnap"/> でカットする。以後この構図は
    /// 動かさない（跳ね上がる魚だけが動く画になる）。
    /// 未設定なら構図は切り替わらない（<see cref="CameraMove"/> が従来の目標を追い続ける）。
    /// </summary>
    [Header("参照"), SerializeField(Label = "横カメラの目標(CatchCameraTarget)")]
    private SEED.Transform? catchCameraTarget = null;

    /// <summary>
    /// カメラの追従スクリプト。真っ白の瞬間に <see cref="CameraMove.RequestSnap"/> を呼び、
    /// 構図の切り替えを補間ではなく<b>カット</b>にする（白の裏で切るので視点の飛びが見えない）。
    /// 未設定なら補間で繋がる（白が晴れたあとにカメラが動いて見える可能性がある）。
    /// </summary>
    [SerializeField(Label = "カメラ(CameraMove)")]
    private CameraMove? cameraMove = null;

    /// <summary>
    /// プレイヤー本体のトランスフォーム（魚が跳ねてくる向きの基準）。
    /// 未設定なら本スクリプトが乗っているアクタ自身のトランスフォームを使う。
    /// </summary>
    [SerializeField(Label = "プレイヤーのトランスフォーム")]
    private SEED.Transform? playerTransform = null;

    /// <summary>
    /// 全画面ホワイトアウト用のスプライト（FishingUI キャンバスの子「Whiteout」）。
    /// 本スクリプトは<b>色のアルファだけ</b>を書き換える（RGB とサイズはシーンの設定を保つ）。
    /// 未設定ならホワイトアウトは効かない（構図の切り替えがそのまま見える）。
    /// </summary>
    [SerializeField(Label = "ホワイトアウトのSprite")]
    private SEED.Sprite? whiteoutSprite = null;

    // ─── ホワイトアウト（Fade → SlowArc）───────────────────────

    /// <summary>白へ塗り潰すまでの秒数（アルファ 0 → 1）。</summary>
    [Header("ホワイトアウト"), SerializeField(Label = "フェードイン(秒)")]
    private float whiteoutFadeInSeconds = 0.35f;

    /// <summary>真っ白のまま保持する秒数（この区間の頭で構図と魚を差し替える）。</summary>
    [SerializeField(Label = "真っ白の保持(秒)")]
    private float whiteoutHoldSeconds = 0.15f;

    /// <summary>白が晴れるまでの秒数（アルファ 1 → 0。<see cref="CatchPhase.SlowArc"/> の頭で進む）。</summary>
    [SerializeField(Label = "フェードアウト(秒)")]
    private float whiteoutFadeOutSeconds = 0.30f;

    // ─── 横カメラ（SlowArc の構図）─────────────────────────────

    /// <summary>
    /// 横カメラの方位角（度）。基準は「ウキ→プレイヤー」の水平方向で、
    /// そこから右回りにこの角度だけ回した向きへカメラを置く。
    /// <b>90 度＝真横</b>（＝ウキ→プレイヤーの向きに対して直角）で、魚が描く弧を
    /// 真横から見る構図になる。0 でプレイヤーの背後側、180 で沖側。
    /// </summary>
    [Header("横カメラ"), SerializeField(Label = "方位角θ(度)")]
    private float sideCameraTheta = 90f;

    /// <summary>
    /// 横カメラの仰角（度）。0 で水平、正で上から見下ろす。
    /// 「水面のすこし上から真横に見る」構図なので既定は 0 に近い値。
    /// </summary>
    [SerializeField(Label = "仰角φ(度)")]
    private float sideCameraPhi = 6f;

    /// <summary>
    /// 横カメラの基準距離（メートル）。実際の距離は
    /// <c>sideCameraDistance ＋ sideCameraDistancePerRank × ランク段位</c>。
    /// ランク段位は C=0 / B=1 / A=2 / S=3（<see cref="RankStep"/>）。
    /// </summary>
    [SerializeField(Label = "距離(m)")]
    private float sideCameraDistance = 5.5f;

    /// <summary>サイズランク 1 段ごとに増やすカメラ距離（メートル）。0 でランク非依存。</summary>
    [SerializeField(Label = "ランク1段あたりの距離(m)")]
    private float sideCameraDistancePerRank = 1.8f;

    /// <summary>
    /// 水面からのカメラの高さ（メートル）。カメラの高さは
    /// <c>水面 ＋ この値 ＋ 距離 × sin(仰角)</c> で決まる（＝水面のすこし上に置く）。
    /// </summary>
    [SerializeField(Label = "水面からの高さ(m)")]
    private float sideCameraHeight = 0.5f;

    // ─── 魚の放物線（SlowArc）──────────────────────────────────

    /// <summary>魚が跳ね始める深さ（水面からどれだけ下に置くか。メートル）。</summary>
    [Header("魚の放物線"), SerializeField(Label = "水面下の開始深さ(m)")]
    private float fishSubmergeDepth = 0.25f;

    /// <summary>放物線の頂点の高さ（開始点からの高さ。メートル）。</summary>
    [SerializeField(Label = "跳ねる高さ(m)")]
    private float arcHeight = 3.0f;

    /// <summary>放物線がプレイヤー側へ進む水平距離（メートル）。</summary>
    [SerializeField(Label = "プレイヤー側へ進む距離(m)")]
    private float arcHorizontalDistance = 2.5f;

    /// <summary>放物線を端から端まで描くのに掛ける秒数（実時間）。</summary>
    [SerializeField(Label = "放物線の秒数")]
    private float arcSeconds = 3.0f;

    /// <summary>
    /// 放物線のどこで釣果パネルへ切り替えるか（0〜1 の比率）。
    /// 0.5＝頂点。既定 0.7 は頂点を過ぎて落ち始めたところ（魚をじっくり見せる）。
    /// </summary>
    [SerializeField(Label = "パネルへ切り替える比率")]
    private float arcApexRatio = 0.7f;

    /// <summary>魚が横軸まわりに回る速さ（度／秒）。0 で回転しない。</summary>
    [SerializeField(Label = "回転速度(度/秒)")]
    private float fishSpinDegPerSecond = 150f;

    /// <summary>放物線のあいだのゲーム時間の速さ（0.3＝3 割の速さ＝スロー）。</summary>
    [SerializeField(Label = "スローの速さ")]
    private float slowScale = 0.3f;

    // ─── しぶき（SlowArc の頭）─────────────────────────────────

    /// <summary>
    /// しぶきのパーティクルのプレハブ（<c>assets://</c> パス）。空なら出さない。
    /// プレハブ側で「一定個数を放出したら止まる」設定にしてあるので、
    /// 本スクリプトは生成して置くだけで、放出の制御は行わない。
    /// </summary>
    [Header("しぶき"), SerializeField(Label = "しぶきのプレハブ")]
    private string splashActorPath = "assets://mainGame/actors/FX/Splash.actor";

    /// <summary>しぶきのアクタを消すまでの秒数（実時間）。パーティクルの寿命より長くすること。</summary>
    [SerializeField(Label = "しぶきを消すまでの秒数")]
    private float splashLifeSeconds = 2.0f;

    /// <summary>着水音（<c>assets://</c> パス）。空なら鳴らさない。</summary>
    [SerializeField(Label = "水音")]
    private string splashSePath = "assets://mainGame/audios/sei_ge_mizu_chapon06.mp3";

    /// <summary>着水音の音量（0〜1）。</summary>
    [SerializeField(Label = "水音の音量")]
    private float splashSeVolume = 0.9f;

    // ─── 釣果パネル（Result）───────────────────────────────────

    /// <summary>
    /// 釣果パネルのプレハブ（<c>assets://</c> パス）。
    /// シーンに <c>ResultPanel</c> のインスタンスを置いていない場合のフォールバックとして
    /// <see cref="ResultPanel.Show"/> が生成に使う。空にすると生成できない。
    /// </summary>
    [Header("釣果パネル"), SerializeField(Label = "パネルのプレハブ")]
    private string resultPanelActorPath = "assets://mainGame/actors/UI/ResultPanel.actor";

    /// <summary>釣果パネルが閉じ切ってから移動へ戻すまでの間（秒・実時間）。</summary>
    [SerializeField(Label = "閉じたあとの間(秒)")]
    private float closeSeconds = 0.15f;

    // ─── 表示ラベル ───────────────────────────────────────────
    //
    // ランクの<b>しきい値</b>は <see cref="Fish.SizeRank"/> に一元化してある
    // （FishingFight もヒット直後の引き距離の算出に同じランクを参照するため、
    //  ここで重複して持つと閾値だけ食い違う事故の元になる）。ここに残すのは表示用の文字列だけ。

    /// <summary>ランク S のラベル。</summary>
    [Header("表示ラベル（しきい値は Fish.SizeRank が持つ）"), SerializeField(Label = "S のラベル")]
    private string rankSLabel = "S";

    /// <summary>ランク A のラベル。</summary>
    [SerializeField(Label = "A のラベル")]
    private string rankALabel = "A";

    /// <summary>ランク B のラベル。</summary>
    [SerializeField(Label = "B のラベル")]
    private string rankBLabel = "B";

    /// <summary>ランク C のラベル。</summary>
    [SerializeField(Label = "C のラベル")]
    private string rankCLabel = "C";

    /// <summary>ランク行の接頭辞（例「ランク S」）。</summary>
    [SerializeField(Label = "ランク行の見出し")]
    private string rankLabelPrefix = "ランク ";

    /// <summary>自己ベスト行の接頭辞（例「自己ベスト: 30.0cm」）。</summary>
    [SerializeField(Label = "ベスト行の見出し")]
    private string bestLabelPrefix = "自己ベスト: ";

    // ─── 公開状態 ─────────────────────────────────────────────

    /// <summary>現在のフェーズ（<see cref="CameraMove"/> がカメラ目標の選択に使う読み取り専用値）。</summary>
    public CatchPhase Phase { get; private set; } = CatchPhase.None;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>演出中の魚（null = 演出していない）。<see cref="Begin"/> で束縛し、<see cref="Finish"/> で破棄する。</summary>
    private Fish? shownFish = null;

    /// <summary>釣り上げた瞬間のウキのワールド位置（＝水面の基準点）。<see cref="Begin"/> で受け取る。</summary>
    private SEED.Vector3 floatPosition = SEED.Vector3.Zero;

    /// <summary>魚が跳ね始める点（水面のすこし下）。カットの瞬間に確定する。</summary>
    private SEED.Vector3 arcStart = SEED.Vector3.Zero;

    /// <summary>魚が跳ね終わる点（プレイヤー側・開始点と同じ高さ）。カットの瞬間に確定する。</summary>
    private SEED.Vector3 arcEnd = SEED.Vector3.Zero;

    /// <summary>魚の進行方向のヨー角（度）。放物線のあいだ固定で、回転はピッチだけが進む。</summary>
    private float arcYawDegrees = 0f;

    /// <summary>現在のフェーズに入ってからの経過秒数（実時間）。</summary>
    private float phaseElapsed = 0f;

    /// <summary>白のアルファ（0＝透明 / 1＝真っ白）。フェーズ間で持ち越すので状態として持つ。</summary>
    private float whiteoutAlpha = 0f;

    /// <summary>生成したしぶきのアクタ（<see cref="splashElapsed"/> 秒後に破棄する）。</summary>
    private SEED.GameObject splashActor;

    /// <summary>しぶきを生成してからの経過秒数（実時間）。</summary>
    private float splashElapsed = 0f;

    /// <summary>スロー（<see cref="slowScale"/>）を掛けているか。戻し忘れを防ぐためのガード。</summary>
    private bool slowApplied = false;

    // ─── ライフサイクル ───────────────────────────────────────

    /// <summary>生成直後の初期化。白は必ず消えた状態から始める。</summary>
    public override void OnStart()
    {
        Phase = CatchPhase.None;
        SetWhiteoutAlpha(0f);
    }

    /// <summary>破棄直前の後始末。演出中なら畳む（スローの戻しも込み）。</summary>
    public override void OnDestroy()
    {
        Abort();
    }

    /// <summary>フレーム開始時に呼ばれる。</summary>
    public override void BeginFrame(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update より前の更新。</summary>
    public override void EarlyUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>
    /// 毎フレームの更新。
    ///
    /// <b>ここでは進行しない</b>。演出の進行は <see cref="FishingController"/> が
    /// <c>Catching</c> 状態のあいだ <see cref="Tick"/> を呼ぶことで駆動される
    /// （＝進行の主導権をコントローラ側に一本化し、スクリプトの実行順に依存させない）。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
    }

    /// <summary>固定タイムステップの更新。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update 後の更新。</summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>描画フェーズ。</summary>
    public override void Render(ref NativeFrameContext ctx)
    {
    }

    /// <summary>フレーム終了時。</summary>
    public override void EndFrame(ref NativeFrameContext ctx)
    {
    }

    // ─── コントローラから呼ばれる公開 API ─────────────────────

    /// <summary>
    /// 釣り上げ演出を開始する【演出開始の唯一の入口】。
    ///
    /// 呼び出し側（<see cref="FishingController.FinishReeling"/>）は、これを呼ぶ前に
    /// 魚を <see cref="Fish.OnCaught"/> で AI 停止させ、自身の状態を
    /// <c>Catching</c> にしておくこと。
    /// </summary>
    /// <param name="fish">釣り上げた魚。</param>
    /// <param name="floatWorldPosition">
    /// 釣り上げた瞬間のウキのワールド位置。<b>水面の基準点</b>として、魚の跳ね始め・
    /// カメラの高さ・しぶきの位置のすべてがここから決まる。
    /// </param>
    public void Begin(Fish fish, SEED.Vector3 floatWorldPosition)
    {
        // 直前の演出が残っていたら畳んでから始める（多重開始でも状態が壊れないようにする）
        if (Phase != CatchPhase.None) { Abort(); }

        shownFish = fish;
        floatPosition = floatWorldPosition;
        whiteoutAlpha = 0f;
        SetWhiteoutAlpha(0f);
        EnterPhase(CatchPhase.Fade);

        // 魚を釣り上げた（引数は魚の表示名。チュートリアル・図鑑・SE などが購読する）
        SEED.Events.Raise(FishingEvents.Catch, fish.DisplayName);

        SEED.Debug.Log($"[Catch] 演出開始: {fish.DisplayName}");
    }

    /// <summary>
    /// 演出を 1 フレーム進める。<see cref="FishingController"/> が
    /// <c>Catching</c> 状態のあいだ毎フレーム呼ぶ。
    /// 進行が終わると <see cref="Phase"/> が <see cref="CatchPhase.None"/> に戻るので、
    /// 呼び出し側はそれを見て移動状態へ復帰させる。
    /// </summary>
    /// <param name="deltaTime">
    /// このフレームの<b>ゲーム時間</b>の経過秒数。本スクリプトはスロー区間を持つため
    /// この値は使わず、常に実時間（<c>Time.UnscaledDeltaTime</c>）で数える
    /// （引数は呼び出し側の作法を変えないために残してある）。
    /// </param>
    public void Tick(float deltaTime)
    {
        if (Phase == CatchPhase.None) { return; }

        float dt = SEED.Time.UnscaledDeltaTime;
        phaseElapsed += dt;
        UpdateSplashLife(dt);

        switch (Phase)
        {
            case CatchPhase.Fade:    UpdateFade();    break;
            case CatchPhase.SlowArc: UpdateSlowArc(); break;
            case CatchPhase.Result:  UpdateResult();  break;
            case CatchPhase.Close:   UpdateClose();   break;
        }
    }

    /// <summary>
    /// 演出を強制的に打ち切る（釣り姿勢が外部から解除された・破棄された場合の後始末）。
    /// 魚・しぶきを消し、白を消し、スローを戻す。
    /// </summary>
    public void Abort()
    {
        if (Phase == CatchPhase.None) { return; }
        Finish();
    }

    // ─── フェーズごとの更新 ───────────────────────────────────

    /// <summary>
    /// <see cref="CatchPhase.Fade"/> の更新（白へフェードイン → 真っ白を保持）。
    ///
    /// アルファが 1 に達した最初のフレームで <see cref="SwitchToSlowArcComposition"/> を呼び、
    /// 構図と魚の差し替えを<b>白の裏で</b>済ませる。保持時間が過ぎたら
    /// <see cref="CatchPhase.SlowArc"/> へ移り、そこで白が晴れる。
    /// </summary>
    private void UpdateFade()
    {
        float fadeIn = SEED.Mathf.Max(whiteoutFadeInSeconds, 0f);

        // アルファ: フェードイン秒数までは 0→1、それ以降は 1 で保持
        float alpha = fadeIn <= DivideEpsilon ? 1f : SEED.Mathf.Clamped01(phaseElapsed / fadeIn);
        bool reachedFullWhite = whiteoutAlpha < 1f && alpha >= 1f;
        SetWhiteoutAlpha(alpha);

        // 真っ白になった最初のフレームでカット（構図と魚の差し替え）を済ませる
        if (reachedFullWhite) { SwitchToSlowArcComposition(); }

        if (phaseElapsed < fadeIn + SEED.Mathf.Max(whiteoutHoldSeconds, 0f)) { return; }
        EnterPhase(CatchPhase.SlowArc);
    }

    /// <summary>
    /// <see cref="CatchPhase.SlowArc"/> の更新（白が晴れる／魚が放物線を描く）。
    ///
    /// - 白: <see cref="whiteoutFadeOutSeconds"/> で 1 → 0
    /// - 魚: <see cref="arcStart"/> → <see cref="arcEnd"/> の放物線を
    ///   <see cref="arcSeconds"/> 秒で進み、横軸まわりに <see cref="fishSpinDegPerSecond"/> で回る
    /// - <see cref="arcSeconds"/> × <see cref="arcApexRatio"/> 秒（既定＝頂点）で次のフェーズへ
    /// </summary>
    private void UpdateSlowArc()
    {
        // 白を晴らす
        float fadeOut = SEED.Mathf.Max(whiteoutFadeOutSeconds, 0f);
        float alpha = fadeOut <= DivideEpsilon ? 0f : 1f - SEED.Mathf.Clamped01(phaseElapsed / fadeOut);
        SetWhiteoutAlpha(alpha);

        // 放物線上の位置と回転を置き直す
        float span = SEED.Mathf.Max(arcSeconds, DivideEpsilon);
        float t = SEED.Mathf.Clamped01(phaseElapsed / span);
        PlaceFishOnArc(t);

        // 頂点（既定）まで来たら魚を隠して釣果パネルへ
        float switchSeconds = span * SEED.Mathf.Clamped01(arcApexRatio);
        if (phaseElapsed < switchSeconds) { return; }

        HideFish();
        EnterPhase(CatchPhase.Result);
    }

    /// <summary>
    /// <see cref="CatchPhase.Result"/> の更新（釣果パネルが閉じ切るのを待つ）。
    ///
    /// パネルの開閉・図鑑登録の追加表示・入力受付はすべて <see cref="ResultPanel"/> の責務。
    /// ここは「まだ出ているか」（<see cref="ResultPanel.IsActive"/>）を見るだけにして、
    /// パネルの中身が変わっても本スクリプトを触らずに済ませる。
    /// </summary>
    private void UpdateResult()
    {
        if (ResultPanel.IsActive) { return; }
        EnterPhase(CatchPhase.Close);
    }

    /// <summary>
    /// <see cref="CatchPhase.Close"/> の更新（後始末までの間）。
    /// <see cref="closeSeconds"/> 秒たったら <see cref="Finish"/> し、
    /// <see cref="Phase"/> を <see cref="CatchPhase.None"/> へ戻す
    /// （＝コントローラが移動へ復帰する合図）。
    /// </summary>
    private void UpdateClose()
    {
        if (phaseElapsed < SEED.Mathf.Max(closeSeconds, 0f)) { return; }

        // 表示名は Finish() で参照が消えるので、閉じる前に控えておく
        string presentedName = shownFish is { } presented ? presented.DisplayName : string.Empty;

        Finish();

        // 獲得演出を最後まで見せ切った（中断＝Abort ではここを通らない）。
        // チュートリアルの「巻き上げ／釣り上げ」ミッションはこの瞬間をクリアの合図にする。
        SEED.Events.Raise(FishingEvents.CatchPresented, presentedName);
    }

    // ─── フェーズ遷移 ─────────────────────────────────────────

    /// <summary>
    /// フェーズを切り替える【遷移の唯一の入口】。経過秒数を必ず 0 に戻し、
    /// 入った瞬間だけ行う処理（フェーズの「頭」の処理）をここに集約する。
    /// </summary>
    /// <param name="next">次のフェーズ。</param>
    private void EnterPhase(CatchPhase next)
    {
        Phase = next;
        phaseElapsed = 0f;

        switch (next)
        {
            case CatchPhase.SlowArc:
                // 弧を見せているあいだだけゲーム時間を遅くする（戻しは Result / Finish）
                ApplySlow(true);
                break;

            case CatchPhase.Result:
                // スローは必ずここで戻す（パネル表示中は等倍）
                ApplySlow(false);
                ShowResultPanel();
                break;
        }
    }

    /// <summary>
    /// 真っ白の瞬間に行うカット【構図・魚の差し替えの唯一の集約点】。
    ///
    /// 1. 放物線の始点・終点・向きを決める（水面と「ウキ→プレイヤー」の向きが基準）
    /// 2. 横カメラの目標を置き、<see cref="CameraMove.RequestSnap"/> で補間を飛ばす
    /// 3. 魚をウキから外して始点（水面のすこし下）へ置く
    /// 4. しぶきと水音を出す
    /// </summary>
    private void SwitchToSlowArcComposition()
    {
        // 1. 放物線の始点・終点・向き
        SEED.Vector3 toPlayer = HorizontalToPlayer();
        arcStart = floatPosition - SEED.Vector3.Up * SEED.Mathf.Max(fishSubmergeDepth, 0f);
        arcEnd = arcStart + toPlayer * arcHorizontalDistance;
        arcYawDegrees = SEED.Mathf.Atan2(toPlayer.x, toPlayer.z) * SEED.Mathf.Rad2Deg;

        // 2. 横カメラの構図を作ってカット
        ApplySideCameraFraming(toPlayer);
        if (cameraMove is { } cam) { cam.RequestSnap(); }

        // 3. 魚を始点へ（放物線の t=0 の姿勢）
        PlaceFishOnArc(0f);

        // 4. しぶき・水音
        SpawnSplash();
        PlaySplashSe();

        SEED.Debug.Log($"[Catch] カット: start={arcStart} end={arcEnd} yaw={arcYawDegrees:F1}");
    }

    /// <summary>
    /// 演出を畳む【終了の唯一の出口】。
    /// 魚としぶきを破棄し、白を消し、スローを戻す。
    /// </summary>
    private void Finish()
    {
        if (shownFish is { } fish)
        {
            // 破棄はフレーム末尾に遅延適用される。円環クランプの除外登録は
            // Fish.OnDestroy が自分で外すので、ここでは触らない。
            fish.Actor.Destroy();
        }
        shownFish = null;

        DestroySplash();
        ApplySlow(false);

        Phase = CatchPhase.None;
        phaseElapsed = 0f;
        SetWhiteoutAlpha(0f);
    }

    // ─── 魚の放物線 ───────────────────────────────────────────

    /// <summary>
    /// 放物線上の位置・姿勢へ魚を置く【魚の見た目を決める唯一の場所】。
    ///
    /// 位置 ＝ <c>lerp(始点, 終点, t) ＋ 上 × 4·h·t·(1−t)</c>
    /// （<c>4·h·t·(1−t)</c> は t=0.5 でちょうど h になるので、
    /// 　インスペクタの「跳ねる高さ」がそのまま頂点の高さになる）。
    ///
    /// 姿勢 ＝ ヨーは進行方向（<see cref="arcYawDegrees"/>）で固定、
    /// ピッチだけが <see cref="fishSpinDegPerSecond"/> で進む。
    /// カメラは進行方向に対して直角に置いてあるので、この回転は
    /// 画面内で<b>横軸まわりの一回転</b>（前転）に見える。
    /// </summary>
    /// <param name="t">放物線の進行度（0〜1）。</param>
    private void PlaceFishOnArc(float t)
    {
        if (shownFish is not { } fish || !fish.Actor.IsValid) { return; }

        float ratio = SEED.Mathf.Clamped01(t);
        float height = ParabolaPeakCoefficient * arcHeight * ratio * (1f - ratio);

        var flat = arcStart + (arcEnd - arcStart) * ratio;
        var fishTf = fish.Transform;
        fishTf.Position = flat + SEED.Vector3.Up * height;

        // 回転は「放物線に入ってからの経過秒数 × 速度」。位置と同じ引数で決まるよう
        // 経過秒数ではなく進行度から復元する（t=1 で arcSeconds ぶん回った状態になる）。
        float spunDegrees = fishSpinDegPerSecond * SEED.Mathf.Max(arcSeconds, 0f) * ratio;
        fishTf.Rotation = new SEED.Vector3(spunDegrees, arcYawDegrees, 0f);
    }

    /// <summary>
    /// 魚を見えなくする（破棄はしない。破棄は <see cref="Finish"/> の責務）。
    /// 描画だけを止めるので、直後に釣果パネルが開いても魚が画面に残らない。
    /// </summary>
    private void HideFish()
    {
        if (shownFish is not { } fish || !fish.Actor.IsValid) { return; }

        // ハンドルは一旦ローカルへ受ける（プロパティの戻り値へ直接代入すると CS1612）。
        // GameObject はハンドル（構造体）なので、控えへの代入でも実体に効く。
        var actor = fish.Actor;
        actor.Visible = false;
    }

    /// <summary>
    /// 「ウキ→プレイヤー」の水平方向（正規化済み）。
    ///
    /// 巻き切った直後はウキとプレイヤーが重なっていることがあり、その場合は向きが
    /// 定まらないので、<b>プレイヤーの正面の逆</b>（＝プレイヤーは水面を向いているので、
    /// 水面からプレイヤーへ向かう向き）へフォールバックする。
    /// </summary>
    private SEED.Vector3 HorizontalToPlayer()
    {
        var player = ResolvePlayerTransform();
        if (player is { } tf)
        {
            var flat = new SEED.Vector3(tf.Position.x - floatPosition.x, 0f, tf.Position.z - floatPosition.z);
            if (flat.SqrMagnitude > SqrEpsilon) { return flat.Normalized; }

            // 重なっている: プレイヤーの正面の逆向き（水面 → プレイヤー）
            var back = new SEED.Vector3(-tf.Forward.x, 0f, -tf.Forward.z);
            if (back.SqrMagnitude > SqrEpsilon) { return back.Normalized; }
        }
        return SEED.Vector3.Forward;
    }

    // ─── 横カメラの構図 ───────────────────────────────────────

    /// <summary>
    /// 横カメラの目標（<see cref="catchCameraTarget"/>）を放物線に合わせて置く
    /// 【この構図の唯一の算出点】。
    ///
    /// <code>
    /// 注視点   ＝ 弧の中心（始点と終点の中点 ＋ 上 × 跳ねる高さ/2）
    /// 水平方向 ＝ 「ウキ→プレイヤー」を方位角θぶん右へ回した向き（θ=90 で真横）
    /// 距離     ＝ 基準距離 ＋ ランク段位 × ランクあたりの距離
    /// 位置     ＝ (注視点の水平位置 ＋ 水平方向 × 距離·cosφ,
    ///              水面 ＋ 高さ ＋ 距離·sinφ,
    ///              …)
    /// 向き     ＝ その位置から注視点を見る向き
    /// </code>
    /// カメラの高さだけは注視点ではなく<b>水面</b>を基準にする（「水面のすこし上から
    /// 真横に見る」という構図の指定をそのまま数式にするため）。
    /// </summary>
    /// <param name="toPlayer">「ウキ→プレイヤー」の水平方向（正規化済み）。</param>
    private void ApplySideCameraFraming(SEED.Vector3 toPlayer)
    {
        if (catchCameraTarget is not { IsValid: true } goal) { return; }

        // 注視点＝弧の中心
        var focus = (arcStart + arcEnd) * Half + SEED.Vector3.Up * (arcHeight * Half);

        // toPlayer を右へ 90° 回した水平方向（θ=90° の方位に対応）
        var right = new SEED.Vector3(toPlayer.z, 0f, -toPlayer.x);

        float thetaRad = sideCameraTheta * SEED.Mathf.Deg2Rad;
        float phiRad = sideCameraPhi * SEED.Mathf.Deg2Rad;
        var horizDir = toPlayer * SEED.Mathf.Cos(thetaRad) + right * SEED.Mathf.Sin(thetaRad);

        float distance = SEED.Mathf.Max(
            sideCameraDistance + sideCameraDistancePerRank * RankStep(), 0f);

        var camPos = new SEED.Vector3(
            focus.x + horizDir.x * distance * SEED.Mathf.Cos(phiRad),
            floatPosition.y + sideCameraHeight + distance * SEED.Mathf.Sin(phiRad),
            focus.z + horizDir.z * distance * SEED.Mathf.Cos(phiRad));

        goal.Position = camPos;
        goal.Rotation = LookRotation(focus - camPos);
    }

    /// <summary>
    /// 演出中の魚のサイズランクの段位（C=0 / B=1 / A=2 / S=3）。
    /// カメラ距離を「ランクが上がるほど遠のく」形で伸ばすのに使う。
    /// しきい値の判定自体は <see cref="Fish.SizeRank"/> に一元化してある。
    /// </summary>
    private int RankStep()
    {
        if (shownFish is not { } fish) { return RankStepC; }
        return fish.SizeRank switch
        {
            "S" => RankStepS,
            "A" => RankStepA,
            "B" => RankStepB,
            _ => RankStepC,
        };
    }

    // ─── しぶき ───────────────────────────────────────────────

    /// <summary>
    /// しぶきのパーティクルを水面へ置く【しぶき生成の唯一の場所】。
    /// プレハブ側が「一定個数を放出したら止まる」設定なので、生成して置くだけでよい。
    /// パスが空・生成に失敗した場合は何もしない（演出は続く）。
    /// </summary>
    private void SpawnSplash()
    {
        DestroySplash();   // 前回の取り残しがあれば先に片付ける
        if (string.IsNullOrWhiteSpace(splashActorPath)) { return; }

        splashActor = SEED.GameObject.Instantiate(splashActorPath);
        if (!splashActor.IsValid)
        {
            SEED.Debug.LogWarning($"[Catch] しぶきを生成できない: {splashActorPath}");
            return;
        }

        // 3D アクタなので生成と同じフレームに位置を設定してよい（2D と違いここで効く）
        if (splashActor.GetComponent<SEED.Transform>() is { } tf) { tf.Position = floatPosition; }
        splashElapsed = 0f;
    }

    /// <summary>しぶきの寿命を数え、過ぎていたら破棄する。</summary>
    /// <param name="deltaTime">このフレームの実時間の経過秒数。</param>
    private void UpdateSplashLife(float deltaTime)
    {
        if (!splashActor.IsValid) { return; }
        splashElapsed += deltaTime;
        if (splashElapsed < SEED.Mathf.Max(splashLifeSeconds, 0f)) { return; }
        DestroySplash();
    }

    /// <summary>しぶきのアクタを破棄する（生成していなければ何もしない）。</summary>
    private void DestroySplash()
    {
        if (splashActor.IsValid) { splashActor.Destroy(); }
        splashActor = default;
        splashElapsed = 0f;
    }

    /// <summary>着水音を鳴らす（パスが空なら何もしない）。</summary>
    private void PlaySplashSe()
    {
        if (string.IsNullOrWhiteSpace(splashSePath)) { return; }
        SEED.Audio.Play(splashSePath, SEED.Mathf.Clamped01(splashSeVolume));
    }

    // ─── 釣果パネル ───────────────────────────────────────────

    /// <summary>
    /// 釣果を記録して釣果パネルを開く【表示内容を組み立てる唯一の場所】。
    ///
    /// 記録（ベスト・釣った数）は <see cref="FishRecords.RecordCatch"/> に任せ、
    /// その戻り値（初捕獲か・新記録か）をそのままパネルへ渡す。
    /// このメソッドは <see cref="CatchPhase.Result"/> へ入った瞬間に 1 回だけ呼ばれるので、
    /// ここで釣った数を 1 増やしても二重加算にはならない。
    /// </summary>
    private void ShowResultPanel()
    {
        if (shownFish is not { } fish) { return; }

        string displayName = fish.DisplayName;
        float displaySize = fish.DisplaySize;
        string unit = fish.SizeUnitLabel;

        FishRecords.CatchRecordResult record =
            FishRecords.RecordCatch(displayName, displaySize, fish.SizeRank);
        float best = FishRecords.BestSize(displayName);

        // 図鑑画像は表示名でカタログを引く（見つからなければ空＝パネル側の代替画像）
        string imagePath = FishCatalog.TryGetByDisplayName(displayName, out FishCatalogEntry entry)
            ? entry.imagePath
            : string.Empty;

        ResultPanel.Show(resultPanelActorPath, new ResultPanel.ResultData(
            name: displayName,
            sizeText: FormatSize(displaySize, unit),
            bestText: bestLabelPrefix + FormatSize(best, unit),
            rankText: rankLabelPrefix + RankLabel(fish.SizeRank),
            imagePath: imagePath,
            newRecord: record.NewBest,
            firstCatch: record.FirstCatch));
    }

    /// <summary>サイズ表示の書式（小数第 1 位＋単位ラベル。例「32.5cm」）。</summary>
    /// <param name="size">表示するサイズ。</param>
    /// <param name="unit">単位ラベル（例 "cm"）。</param>
    private static string FormatSize(float size, string unit) => $"{size:F1}{unit}";

    /// <summary>
    /// <see cref="Fish.SizeRank"/>（"S" / "A" / "B" / それ以外＝"C"）を表示ラベルへ変換する。
    /// しきい値の判定自体は Fish 側に一元化してあり、ここではラベル文字列の差し替えだけ担う。
    /// </summary>
    /// <param name="sizeRank">個体のサイズランク。</param>
    private string RankLabel(string sizeRank) => sizeRank switch
    {
        "S" => rankSLabel,
        "A" => rankALabel,
        "B" => rankBLabel,
        _ => rankCLabel,
    };

    // ─── 汎用ヘルパー ─────────────────────────────────────────

    /// <summary>
    /// スローの掛け外し【<c>Time.Scale</c> を触る唯一の場所】。
    /// すでにその状態なら何もしないので、二重に呼んでも安全
    /// （＝<see cref="Finish"/> の戻し忘れ防止をここ 1 か所で担保できる）。
    /// </summary>
    /// <param name="slow">true でスロー、false で等倍へ戻す。</param>
    private void ApplySlow(bool slow)
    {
        if (slowApplied == slow) { return; }
        slowApplied = slow;
        SEED.Time.Scale = slow ? SEED.Mathf.Max(slowScale, 0f) : TimeScaleNormal;
    }

    /// <summary>
    /// プレイヤーのトランスフォームを解決する。
    /// インスペクタ未設定なら、本スクリプトが乗っているアクタ自身のものを使う
    /// （プレイヤーアクタに付ける想定なので、既定でも正しく動く）。
    /// </summary>
    private SEED.Transform? ResolvePlayerTransform()
    {
        if (playerTransform is { IsValid: true } assigned) { return assigned; }
        return transform.IsValid ? transform : null;
    }

    /// <summary>
    /// ホワイトアウトのアルファを設定する（RGB はシーンで設定した色を保つ）。
    /// 現在値をフィールドへ控えるので、「真っ白に達した最初のフレーム」を検出できる。
    /// </summary>
    /// <param name="alpha">不透明度（0〜1 へクランプする）。</param>
    private void SetWhiteoutAlpha(float alpha)
    {
        whiteoutAlpha = SEED.Mathf.Clamped01(alpha);
        if (whiteoutSprite is not { } sprite || !sprite.IsValid) { return; }
        sprite.Color = sprite.Color.WithAlpha(whiteoutAlpha);
    }

    /// <summary>
    /// 方向ベクトルから「その向きを見る」オイラー角（度）を作る。
    /// エンジン規約: yaw = atan2(x, z)（前方 +Z）、pitch = -asin(y / 長さ)、ロールは 0。
    /// 長さが 0 なら無回転を返す。
    /// </summary>
    /// <param name="direction">見る方向（正規化していなくてよい）。</param>
    private static SEED.Vector3 LookRotation(SEED.Vector3 direction)
    {
        float length = direction.Magnitude;
        if (length < DivideEpsilon) { return SEED.Vector3.Zero; }

        float yaw = SEED.Mathf.Atan2(direction.x, direction.z) * SEED.Mathf.Rad2Deg;
        float pitch = -SEED.Mathf.Asin(SEED.Mathf.Clamped(direction.y / length, -1f, 1f)) * SEED.Mathf.Rad2Deg;
        return new SEED.Vector3(pitch, yaw, 0f);
    }
}
