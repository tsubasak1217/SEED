using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 釣り上げ演出（Catching 状態）の進行だけを担うプレゼンタ。
///
/// <b>プレイヤーアクタに付ける</b>（<see cref="FishingController"/> と同じアクタに
/// 2 本目のスクリプトスロット「Catch」として置き、コントローラの
/// <c>presenter</c> フィールドから参照する）。
///
/// <b>責務の分割</b>
/// - 釣りの進行（キャスト〜巻き取り〜ヒット判定）… <see cref="FishingController"/>
/// - <b>釣り上げた瞬間からの見せ場</b>（カメラ寄り／ホワイトアウト／魚のポップ／釣果 UI）… 本スクリプト
/// - カメラの追従補間そのもの … <see cref="CameraMove"/>
///
/// 本スクリプトは「どこを見せるか（目標トランスフォームの座標）」だけを毎フレーム決め、
/// カメラ本体は一切動かさない。カメラは <see cref="CameraMove"/> が
/// <see cref="FishingController.State"/> と <see cref="FishingController.CatchPhase"/> を見て
/// 目標を切り替え、いつもの指数補間で追う（＝構図の作り方が他の場面と完全に同じになる）。
///
/// <b>フェーズ（<see cref="CatchPhase"/>）と時間</b>
/// <code>
/// ApproachCamera … catchCameraSeconds 秒       カメラが魚へ寄る（魚はウキに付いたまま）
/// WhiteOut       … whiteoutFadeInSeconds       白へフェードイン
///                  ＋ whiteoutHoldSeconds      真っ白の保持（この境目で構図と魚を差し替える）
/// Show           … whiteoutFadeOutSeconds で白が晴れ、
///                  fishPopSeconds で魚が easeOutBack で 0 → 原寸へ膨らむ。
///                  釣果テキストを表示し、ポップ完了後の左クリックを待つ。
/// Close          … fishCloseSeconds 秒で easeInBack で原寸 → 0 へ縮み、テキストを消す。
///                  終わったら Phase を None に戻し、コントローラが移動へ復帰させる。
/// </code>
///
/// <b>真っ白の瞬間にやること（<see cref="SwitchToResultComposition"/>）</b>
/// 画面が完全に白いあいだにカット（構図の切り替え）を済ませるので、視点の飛びが見えない。
/// - カメラ目標を <see cref="resultCameraTarget"/> へ切り替え、<see cref="CameraMove.RequestSnap"/> で
///   補間せず瞬間移動させる
/// - 魚をウキから外し、プレイヤーの頭上（<c>fishHoldOffsetX/Y/Z</c>）へ運んでカメラを向かせる
/// - 魚のスケールを 0 にする（Show のポップの開始値）
/// - プレイヤー本体・竿を釣り上げポーズのクリップへ切り替える
/// </summary>
public class CatchPresenter : SEEDScript
{
    // ─── フェーズ ─────────────────────────────────────────────

    /// <summary>
    /// 釣り上げ演出の進行フェーズ。
    ///
    /// <see cref="CameraMove"/> は <c>ApproachCamera</c> なら魚へ寄る目標、
    /// それ以外（<c>WhiteOut</c> 以降）なら釣果用の目標を追う、という 2 分岐だけで済むよう
    /// 順序どおりに並べてある。
    /// スクリプトはファイル名＝型名で 1 ファイル 1 クラスとして扱われるため、
    /// この列挙型は独立ファイルにせず本クラスの入れ子として定義する
    /// （外部からは <c>CatchPresenter.CatchPhase</c> で参照できる）。
    /// </summary>
    public enum CatchPhase
    {
        /// <summary>演出していない（待機）。</summary>
        None,

        /// <summary>カメラが水面の魚へ寄っていく。魚はまだウキに付いている。</summary>
        ApproachCamera,

        /// <summary>白へフェードイン＋真っ白の保持。保持へ入る瞬間に構図と魚を差し替える。</summary>
        WhiteOut,

        /// <summary>白が晴れ、魚がポップして釣果テキストが出る。左クリック待ち。</summary>
        Show,

        /// <summary>魚が縮んで消える。終わり次第 <see cref="None"/> へ戻る。</summary>
        Close,
    }

    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>ベクトルの「長さがほぼ 0」を判定する二乗長のしきい値。</summary>
    private const float SqrEpsilon = 1e-6f;

    /// <summary>0 除算を避けるための「実質 0」しきい値（秒数などの分母に使う）。</summary>
    private const float DivideEpsilon = 1e-4f;

    /// <summary>ラジアン→度変換係数。</summary>
    private const float RadToDeg = HalfTurnDegrees / SEED.Mathf.PI;

    /// <summary>半回転（度）。魚をカメラへ向ける（プレイヤーの真逆を向く）のに使う。</summary>
    private const float HalfTurnDegrees = 180f;

    /// <summary>easeOutBack / easeInBack の跳ね返り係数 c1（標準値）。</summary>
    private const float BackEaseC1 = 1.70158f;

    /// <summary>easeOutBack / easeInBack の跳ね返り係数 c3 ＝ c1 + 1（標準値）。</summary>
    private const float BackEaseC3 = BackEaseC1 + 1f;

    /// <summary>ベストサイズの保存キーの接頭辞（キーは <c>best_size:&lt;魚の表示名&gt;</c>）。</summary>
    private const string BestSizeKeyPrefix = "best_size:";

    /// <summary>ベスト更新時にサイズ表示へ添える文言。</summary>
    private const string NewRecordSuffix = "  NEW!";

    // ─── 参照（インスペクタで割り当てる）───────────────────────

    /// <summary>
    /// カメラが魚へ寄るときの目標トランスフォーム（トップレベルの空アクタ「CatchCameraTarget」）。
    /// <see cref="CatchPhase.ApproachCamera"/> のあいだ、本スクリプトが毎フレーム
    /// 「魚の少し手前・少し上から魚を見る」姿勢へ置き直す。
    /// 未設定ならカメラ寄りの演出は効かない（<see cref="CameraMove"/> が従来の目標を追い続ける）。
    /// </summary>
    [Header("参照"), SerializeField(Label = "寄りカメラの目標(CatchCameraTarget)")]
    private SEED.Transform? catchCameraTarget = null;

    /// <summary>
    /// 釣果表示中のカメラ目標トランスフォーム（プレイヤーの子アクタ「ResultCameraTarget」）。
    /// プレイヤーの正面に置き、プレイヤー（と頭上の魚）を振り返って見る構図をシーン側で作る。
    /// <see cref="CatchPhase.Show"/> では魚の大きさに応じて後方へ押し出す
    /// （<see cref="ApplyResultCameraPull"/>）。未設定なら釣果の構図は切り替わらない。
    /// </summary>
    [SerializeField(Label = "釣果カメラの目標(ResultCameraTarget)")]
    private SEED.Transform? resultCameraTarget = null;

    /// <summary>
    /// カメラ本体のトランスフォーム（<b>読むだけ</b>）。
    /// <see cref="CatchPhase.ApproachCamera"/> で「カメラ→魚」の向きを求めるのに使う。
    /// 未設定なら寄りの目標は魚の真後ろ（ワールド -Z 側）を既定の向きとして置く。
    /// </summary>
    [SerializeField(Label = "カメラ本体")]
    private SEED.Transform? cameraTransform = null;

    /// <summary>
    /// カメラの追従スクリプト。真っ白の瞬間に <see cref="CameraMove.RequestSnap"/> を呼び、
    /// 構図の切り替えを補間ではなく<b>カット</b>にする（白の裏で切るので視点の飛びが見えない）。
    /// 未設定なら補間で繋がる（白が晴れたあとにカメラが動いて見える可能性がある）。
    /// </summary>
    [SerializeField(Label = "カメラ(CameraMove)")]
    private CameraMove? cameraMove = null;

    /// <summary>
    /// プレイヤー本体のトランスフォーム（魚を頭上へ置く基準）。
    /// 未設定なら本スクリプトが乗っているアクタ自身のトランスフォームを使う。
    /// </summary>
    [SerializeField(Label = "プレイヤーのトランスフォーム")]
    private SEED.Transform? playerTransform = null;

    /// <summary>プレイヤー本体の Animator（釣り上げポーズの再生先）。未設定なら本体アニメを触らない。</summary>
    [SerializeField(Label = "プレイヤー本体の Animator")]
    private SEED.Animator? playerAnimator = null;

    /// <summary>竿の Animator（釣り上げポーズの再生先）。未設定なら竿アニメを触らない。</summary>
    [SerializeField(Label = "竿の Animator")]
    private SEED.Animator? rodAnimator = null;

    /// <summary>
    /// 全画面ホワイトアウト用のスプライト（FishingUI キャンバスの子「Whiteout」）。
    /// 本スクリプトは<b>色のアルファだけ</b>を書き換える（RGB とサイズはシーンの設定を保つ）。
    /// 未設定ならホワイトアウトは効かない（構図の切り替えがそのまま見える）。
    /// </summary>
    [SerializeField(Label = "ホワイトアウトのSprite")]
    private SEED.Sprite? whiteoutSprite = null;

    /// <summary>釣果テキスト: 魚の名前。</summary>
    [Header("釣果テキスト"), SerializeField(Label = "名前")]
    private SEED.Text? nameText = null;

    /// <summary>釣果テキスト: サイズ。</summary>
    [SerializeField(Label = "サイズ")]
    private SEED.Text? sizeText = null;

    /// <summary>釣果テキスト: サイズランク。</summary>
    [SerializeField(Label = "サイズランク")]
    private SEED.Text? rankText = null;

    /// <summary>釣果テキスト: ベストサイズ（自己ベスト）。</summary>
    [SerializeField(Label = "ベストサイズ")]
    private SEED.Text? bestText = null;

    /// <summary>釣果テキスト: 「クリックで戻る」の操作案内。</summary>
    [SerializeField(Label = "操作案内")]
    private SEED.Text? promptText = null;

    // ─── 寄りカメラ（ApproachCamera）───────────────────────────

    /// <summary>カメラが魚へ寄っているフェーズの長さ（秒）。</summary>
    [Header("寄りカメラ"), SerializeField(Label = "寄りの時間(秒)")]
    private float catchCameraSeconds = 0.8f;

    /// <summary>
    /// 寄りの目標を魚から何メートル手前（カメラ側）へ置くか。
    /// 目標位置 ＝ 魚 − normalize(魚 − カメラ) × この距離 ＋ 上方向 × <see cref="catchCamHeight"/>。
    /// </summary>
    [SerializeField(Label = "魚からの距離(m)")]
    private float catchCamDistance = 1.6f;

    /// <summary>寄りの目標を魚からどれだけ高い位置へ置くか（メートル）。</summary>
    [SerializeField(Label = "魚からの高さ(m)")]
    private float catchCamHeight = 0.6f;

    // ─── ホワイトアウト（WhiteOut → Show）─────────────────────

    /// <summary>白へ塗り潰すまでの秒数（アルファ 0 → 1）。</summary>
    [Header("ホワイトアウト"), SerializeField(Label = "フェードイン(秒)")]
    private float whiteoutFadeInSeconds = 0.4f;

    /// <summary>真っ白のまま保持する秒数（この区間の頭で構図と魚を差し替える）。</summary>
    [SerializeField(Label = "真っ白の保持(秒)")]
    private float whiteoutHoldSeconds = 0.2f;

    /// <summary>白が晴れるまでの秒数（アルファ 1 → 0。<see cref="CatchPhase.Show"/> の頭で進む）。</summary>
    [SerializeField(Label = "フェードアウト(秒)")]
    private float whiteoutFadeOutSeconds = 0.4f;

    // ─── 釣果の見せ方（Show / Close）───────────────────────────

    /// <summary>
    /// 魚を掲げる位置のオフセット（プレイヤーのローカル座標系の<b>右</b>成分、メートル）。
    /// [SerializeField] がシーンから復元できるのは数値・真偽・文字列だけなので、
    /// ベクトルは成分ごとの float フィールドとして持つ（インスペクタでも個別に調整できる）。
    /// </summary>
    [Header("釣果の見せ方"), SerializeField(Label = "魚を掲げる位置X(右, m)")]
    private float fishHoldOffsetX = 0f;

    /// <summary>魚を掲げる位置のオフセットの<b>上</b>成分（メートル）。既定は頭上 2.2m。</summary>
    [SerializeField(Label = "魚を掲げる位置Y(上, m)")]
    private float fishHoldOffsetY = 2.2f;

    /// <summary>魚を掲げる位置のオフセットの<b>前</b>成分（メートル）。</summary>
    [SerializeField(Label = "魚を掲げる位置Z(前, m)")]
    private float fishHoldOffsetZ = 0f;

    /// <summary>
    /// 掲げた魚のヨー角オフセット（度）。プレイヤーのヨー ＋ この値を魚のヨーにする。
    /// 既定 180 度＝プレイヤーの真逆＝振り返って見ているカメラの方を向く。
    /// </summary>
    [SerializeField(Label = "魚のヨーオフセット(度)")]
    private float fishFacingYawOffsetDegrees = HalfTurnDegrees;

    /// <summary>魚が 0 から原寸へ膨らむのに掛ける秒数（easeOutBack）。</summary>
    [SerializeField(Label = "魚のポップ(秒)")]
    private float fishPopSeconds = 0.5f;

    /// <summary>魚が原寸から 0 へ縮むのに掛ける秒数（easeInBack）。</summary>
    [SerializeField(Label = "魚の閉じ(秒)")]
    private float fishCloseSeconds = 0.35f;

    /// <summary>
    /// 釣り上げポーズで再生するプレイヤー本体のクリップ名。
    ///
    /// <b>暫定</b>: sakanadori.glb には「掲げる」専用のクリップがまだ無い
    /// （収録済み: Walk / Idle / WalkCarry / IdleFishing / Cast / Reel / Hooked /
    ///  IdleFree / WalkFishingL / WalkFishingR）。本来のクリップを追加したら
    /// インスペクタでこの値を差し替えること。
    /// </summary>
    [SerializeField(Label = "本体の釣り上げクリップ名")]
    private string playerCatchClip = "IdleFree";

    /// <summary>釣り上げポーズで再生する竿のクリップ名。</summary>
    [SerializeField(Label = "竿の釣り上げクリップ名")]
    private string rodCatchClip = "Idle_竿";

    /// <summary>クリップ切替時のクロスフェード秒数（0 で即時切替）。</summary>
    [SerializeField(Label = "切替フェード(秒)")]
    private float catchFadeSeconds = 0.15f;

    // ─── 釣果カメラの引き（魚が大きいほど後ろへ下がる）─────────

    /// <summary>
    /// 釣果カメラを後方へ押し出す基準量（メートル）。
    /// 引き量 ＝ <see cref="resultCamPullBase"/> ＋ <see cref="resultCamPullPerSize"/> × 見た目サイズ指標。
    /// 見た目サイズ指標は <see cref="Fish.VisualSizeMetric"/>（＝出現時に
    /// サイズ倍率を掛けたあとの Transform.Scale の最大成分）で、魚が大きいほど大きくなる。
    /// </summary>
    [Header("釣果カメラの引き"), SerializeField(Label = "引きの基準量(m)")]
    private float resultCamPullBase = 0f;

    /// <summary>見た目サイズ指標 1 あたりの追加の引き量（メートル）。0 で引きを無効化できる。</summary>
    [SerializeField(Label = "サイズあたりの引き量(m)")]
    private float resultCamPullPerSize = 0.6f;

    // ─── サイズランク ─────────────────────────────────────────

    /// <summary>ランク S になるサイズ倍率の下限。</summary>
    [Header("サイズランク"), SerializeField(Label = "S のしきい値(倍率)")]
    private float rankSThreshold = 1.2f;

    /// <summary>ランク A になるサイズ倍率の下限。</summary>
    [SerializeField(Label = "A のしきい値(倍率)")]
    private float rankAThreshold = 1.05f;

    /// <summary>ランク B になるサイズ倍率の下限（これ未満は C）。</summary>
    [SerializeField(Label = "B のしきい値(倍率)")]
    private float rankBThreshold = 0.9f;

    /// <summary>ランク表示のラベル（S / A / B / C）。</summary>
    [SerializeField(Label = "S のラベル")]
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

    /// <summary>ランク行の書式の接頭辞（例: 「ランク: S」）。</summary>
    [SerializeField(Label = "ランク行の見出し")]
    private string rankLabelPrefix = "ランク: ";

    /// <summary>ベスト行の書式の接頭辞（例: 「ベスト: 12.3 cm」）。</summary>
    [SerializeField(Label = "ベスト行の見出し")]
    private string bestLabelPrefix = "ベスト: ";

    /// <summary>操作案内の文言。</summary>
    [SerializeField(Label = "操作案内の文言")]
    private string promptMessage = "クリックで戻る";

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>現在のフェーズ（<see cref="CameraMove"/> がカメラ目標の選択に使う読み取り専用値）。</summary>
    public CatchPhase Phase { get; private set; } = CatchPhase.None;

    /// <summary>演出中の魚（null = 演出していない）。<see cref="Begin"/> で束縛し、<see cref="Finish"/> で破棄する。</summary>
    private Fish? shownFish = null;

    /// <summary>魚の原寸スケール（ポップの目標値。生成時にサイズ倍率を掛けたあとの値）。</summary>
    private SEED.Vector3 fishTargetScale = SEED.Vector3.One;

    /// <summary>現在のフェーズに入ってからの経過秒数。</summary>
    private float phaseElapsed = 0f;

    /// <summary>白のアルファ（0＝透明 / 1＝真っ白）。フェーズ間で持ち越すので状態として持つ。</summary>
    private float whiteoutAlpha = 0f;

    /// <summary>
    /// 釣果カメラ目標の押し出し前の位置（<see cref="CatchPhase.Show"/> 開始時に控える）。
    /// 演出の終わりに必ずここへ戻すので、引きが次回へ蓄積することはない。
    /// null = まだ押し出していない。
    /// </summary>
    private SEED.Vector3? resultCameraBasePosition = null;

    // ─── ライフサイクル ───────────────────────────────────────

    /// <summary>生成直後の初期化。白と釣果テキストは必ず消えた状態から始める。</summary>
    public override void OnStart()
    {
        Phase = CatchPhase.None;
        SetWhiteoutAlpha(0f);
        HideTexts();
    }

    /// <summary>破棄直前の後始末。演出中なら魚を消し、カメラ目標の押し出しも戻す。</summary>
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
    /// <see cref="FishingController.FishState.Catching"/> にしておくこと。
    /// </summary>
    /// <param name="fish">釣り上げた魚。</param>
    public void Begin(Fish fish)
    {
        // 直前の演出が残っていたら畳んでから始める（多重開始でも状態が壊れないようにする）
        if (Phase != CatchPhase.None) { Abort(); }

        shownFish = fish;
        fishTargetScale = fish.CaughtScale;
        whiteoutAlpha = 0f;
        SetWhiteoutAlpha(0f);
        HideTexts();
        EnterPhase(CatchPhase.ApproachCamera);

        SEED.Debug.Log($"[Catch] 演出開始: {fish.DisplayName}");
    }

    /// <summary>
    /// 演出を 1 フレーム進める。<see cref="FishingController"/> が
    /// <c>Catching</c> 状態のあいだ毎フレーム呼ぶ。
    /// 進行が終わると <see cref="Phase"/> が <see cref="CatchPhase.None"/> に戻るので、
    /// 呼び出し側はそれを見て移動状態へ復帰させる。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    public void Tick(float deltaTime)
    {
        if (Phase == CatchPhase.None) { return; }

        phaseElapsed += deltaTime;

        switch (Phase)
        {
            case CatchPhase.ApproachCamera: UpdateApproachCamera(); break;
            case CatchPhase.WhiteOut:       UpdateWhiteOut();       break;
            case CatchPhase.Show:           UpdateShow(deltaTime);  break;
            case CatchPhase.Close:          UpdateClose();          break;
        }
    }

    /// <summary>
    /// 演出を強制的に打ち切る（釣り姿勢が外部から解除された・破棄された場合の後始末）。
    /// 魚を消し、白とテキストを消し、カメラ目標の押し出しを元へ戻す。
    /// </summary>
    public void Abort()
    {
        if (Phase == CatchPhase.None) { return; }
        Finish();
    }

    // ─── フェーズごとの更新 ───────────────────────────────────

    /// <summary>
    /// <see cref="CatchPhase.ApproachCamera"/> の更新。
    ///
    /// 「魚の少し手前・少し上」を毎フレーム求めて <see cref="catchCameraTarget"/> へ書き込む。
    /// カメラ本体は <see cref="CameraMove"/> がこの目標へ<b>補間で</b>寄っていくので、
    /// 目標を置くだけで「寄っていく」動きになる（本スクリプトはカメラを直接動かさない）。
    /// 魚はこのあいだウキに付いたまま（＝どちらも動かないので見た目は静止）。
    /// </summary>
    private void UpdateApproachCamera()
    {
        UpdateCatchCameraTarget();

        if (phaseElapsed < catchCameraSeconds) { return; }
        EnterPhase(CatchPhase.WhiteOut);
    }

    /// <summary>
    /// <see cref="CatchPhase.WhiteOut"/> の更新（白へフェードイン → 真っ白を保持）。
    ///
    /// アルファが 1 に達した最初のフレームで <see cref="SwitchToResultComposition"/> を呼び、
    /// 構図と魚の差し替えを<b>白の裏で</b>済ませる。保持時間が過ぎたら
    /// <see cref="CatchPhase.Show"/> へ移り、そこで白が晴れる。
    /// </summary>
    private void UpdateWhiteOut()
    {
        float fadeIn = SEED.Mathf.Max(whiteoutFadeInSeconds, 0f);
        // フェードイン中は寄りカメラの目標を更新し続ける（白の下でもカメラは寄り続ける）
        if (phaseElapsed < fadeIn) { UpdateCatchCameraTarget(); }

        // アルファ: フェードイン秒数までは 0→1、それ以降は 1 で保持
        float alpha = fadeIn <= DivideEpsilon ? 1f : SEED.Mathf.Clamped01(phaseElapsed / fadeIn);
        bool reachedFullWhite = whiteoutAlpha < 1f && alpha >= 1f;
        SetWhiteoutAlpha(alpha);

        // 真っ白になった最初のフレームでカット（構図の切り替え）を済ませる
        if (reachedFullWhite) { SwitchToResultComposition(); }

        if (phaseElapsed < fadeIn + SEED.Mathf.Max(whiteoutHoldSeconds, 0f)) { return; }
        EnterPhase(CatchPhase.Show);
    }

    /// <summary>
    /// <see cref="CatchPhase.Show"/> の更新（白が晴れる／魚のポップ／左クリック待ち）。
    ///
    /// - 白: <see cref="whiteoutFadeOutSeconds"/> で 1 → 0
    /// - 魚: <see cref="fishPopSeconds"/> で easeOutBack により 0 → 原寸
    /// - 入力: ポップが終わってからの左クリックで <see cref="CatchPhase.Close"/> へ
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数（魚の姿勢の再計算に使う）。</param>
    private void UpdateShow(float deltaTime)
    {
        // 白を晴らす
        float fadeOut = SEED.Mathf.Max(whiteoutFadeOutSeconds, 0f);
        float alpha = fadeOut <= DivideEpsilon ? 0f : 1f - SEED.Mathf.Clamped01(phaseElapsed / fadeOut);
        SetWhiteoutAlpha(alpha);

        // 魚の位置・向きは毎フレーム置き直す（プレイヤーが微動しても頭上に付いてくる）
        PlaceFishAbovePlayer();

        // ポップ（easeOutBack で 0 → 原寸）
        float popSeconds = SEED.Mathf.Max(fishPopSeconds, DivideEpsilon);
        float popRatio = SEED.Mathf.Clamped01(phaseElapsed / popSeconds);
        SetFishScale(EaseOutBack(popRatio));

        // ポップが終わるまでは入力を受け付けない（見せる前に閉じられるのを防ぐ）
        if (popRatio < 1f) { return; }
        if (!SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left)) { return; }

        EnterPhase(CatchPhase.Close);
    }

    /// <summary>
    /// <see cref="CatchPhase.Close"/> の更新（魚が easeInBack で縮んで消える）。
    /// 縮み切ったら <see cref="Finish"/> で後始末し、<see cref="Phase"/> を
    /// <see cref="CatchPhase.None"/> へ戻す（＝コントローラが移動へ復帰する合図）。
    /// </summary>
    private void UpdateClose()
    {
        PlaceFishAbovePlayer();

        float closeSeconds = SEED.Mathf.Max(fishCloseSeconds, DivideEpsilon);
        float ratio = SEED.Mathf.Clamped01(phaseElapsed / closeSeconds);
        SetFishScale(1f - EaseInBack(ratio));

        if (ratio < 1f) { return; }
        Finish();
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
            case CatchPhase.Show:
                // 釣果カメラを魚の大きさに応じて後方へ押し出し、テキストを出す。
                // 押し出しは Show の頭で確定させ、フェーズ中は動かさない
                // （CameraMove の通常補間で滑らかに後退して見える）。
                ApplyResultCameraPull();
                ShowTexts();
                break;

            case CatchPhase.Close:
                HideTexts();
                break;
        }
    }

    /// <summary>
    /// 真っ白の瞬間に行うカット【構図・魚の差し替えの唯一の集約点】。
    ///
    /// 1. カメラ目標を釣果用へ切り替える（フェーズが WhiteOut 以降なら
    ///    <see cref="CameraMove"/> が <c>resultTarget</c> を選ぶ）＋ <see cref="CameraMove.RequestSnap"/>
    ///    で補間を飛ばす
    /// 2. 魚をウキから外してプレイヤーの頭上へ運び、カメラの方を向かせる
    /// 3. 魚のスケールを 0 にする（Show のポップの開始値）
    /// 4. プレイヤー本体・竿を釣り上げポーズへ切り替える
    /// </summary>
    private void SwitchToResultComposition()
    {
        // 1. カメラをカット（この時点で SelectGoalTransform は resultTarget を返す）
        if (cameraMove is { } cam) { cam.RequestSnap(); }

        // 2 / 3. 魚を頭上へ運び、スケール 0 から膨らませる下地を作る
        PlaceFishAbovePlayer();
        SetFishScale(0f);

        // 4. 釣り上げポーズ
        CrossFade(playerAnimator, playerCatchClip);
        CrossFade(rodAnimator, rodCatchClip);
    }

    /// <summary>
    /// 演出を畳む【終了の唯一の出口】。
    /// 魚を破棄し、白・テキストを消し、釣果カメラの押し出しを元へ戻す。
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
        Phase = CatchPhase.None;
        phaseElapsed = 0f;
        SetWhiteoutAlpha(0f);
        HideTexts();
        RestoreResultCameraPull();
    }

    // ─── カメラ目標の計算 ─────────────────────────────────────

    /// <summary>
    /// 寄りカメラの目標（<see cref="catchCameraTarget"/>）を魚に合わせて置き直す。
    ///
    /// <b>位置</b> ＝ 魚 − normalize(魚 − カメラ) × <see cref="catchCamDistance"/>
    ///              ＋ (0, <see cref="catchCamHeight"/>, 0)
    /// （＝いまカメラが居る方向へ <see cref="catchCamDistance"/> だけ手前、少し上）。
    /// <b>回転</b> ＝ その位置から魚を見る向き（エンジン規約 yaw = atan2(x, z) /
    /// pitch = -asin(dy / len)、ロールは 0）。
    /// </summary>
    private void UpdateCatchCameraTarget()
    {
        if (catchCameraTarget is not { } goal || !goal.IsValid) { return; }
        if (shownFish is not { } fish || !fish.Actor.IsValid) { return; }

        var fishPos = fish.Transform.Position;

        // 「カメラ → 魚」の向き（カメラが未設定・ほぼ同一点なら既定の -Z 側から見る）
        var viewDir = SEED.Vector3.Forward;
        if (cameraTransform is { IsValid: true } camTf)
        {
            var d = fishPos - camTf.Position;
            if (d.SqrMagnitude > SqrEpsilon) { viewDir = d.Normalized; }
        }

        // 魚の手前・少し上へ目標を置き、そこから魚を見る姿勢にする
        var goalPos = fishPos - viewDir * catchCamDistance + SEED.Vector3.Up * catchCamHeight;
        goal.Position = goalPos;
        goal.Rotation = LookRotation(fishPos - goalPos);
    }

    /// <summary>
    /// 釣果カメラの目標を、魚の見た目の大きさに応じて<b>後方へ</b>押し出す。
    ///
    /// <code>
    /// 引き量 = resultCamPullBase + resultCamPullPerSize × Fish.VisualSizeMetric
    /// 押し出し後の位置 = 元の位置 − 水平化した目標の前方 × 引き量
    /// </code>
    /// 上下方向は変えない（水平に後退するだけ）ので、構図の高さは崩れない。
    /// 元の位置は <see cref="resultCameraBasePosition"/> に控え、演出の終わりに必ず戻す。
    /// </summary>
    private void ApplyResultCameraPull()
    {
        if (resultCameraTarget is not { } goal || !goal.IsValid) { return; }
        if (shownFish is not { } fish) { return; }

        // 元位置を控える（多重適用しても基準がずれないよう、控えていなければ今の値を採る）
        var basePos = resultCameraBasePosition ?? goal.Position;
        resultCameraBasePosition = basePos;

        float pull = resultCamPullBase + resultCamPullPerSize * fish.VisualSizeMetric;
        if (pull <= 0f) { goal.Position = basePos; return; }

        // 目標の前方を水平化して「後退方向」を作る（縮退時は押し出さない）
        var forward = goal.Forward;
        var horizontal = new SEED.Vector3(forward.x, 0f, forward.z);
        if (horizontal.SqrMagnitude < SqrEpsilon) { goal.Position = basePos; return; }

        goal.Position = basePos - horizontal.Normalized * pull;
    }

    /// <summary>
    /// 釣果カメラの押し出しを元の位置へ戻す（演出の終わり・中断の共通出口）。
    /// 控えが無ければ何もしない（押し出していない ＝ 戻す必要が無い）。
    /// </summary>
    private void RestoreResultCameraPull()
    {
        if (resultCameraBasePosition is not { } basePos) { return; }
        resultCameraBasePosition = null;

        if (resultCameraTarget is not { } goal || !goal.IsValid) { return; }
        goal.Position = basePos;
    }

    // ─── 魚の配置・スケール ───────────────────────────────────

    /// <summary>
    /// 魚をプレイヤーの頭上（<c>fishHoldOffsetX/Y/Z</c>）へ置き、カメラの方へ向ける。
    /// オフセットはプレイヤーのローカル軸（右／上／前）で解釈する。
    /// </summary>
    private void PlaceFishAbovePlayer()
    {
        if (shownFish is not { } fish || !fish.Actor.IsValid) { return; }
        if (ResolvePlayerTransform() is not { } player) { return; }

        var basePos = player.Position;
        var pos = basePos
                + player.Right * fishHoldOffsetX
                + player.Up * fishHoldOffsetY
                + player.Forward * fishHoldOffsetZ;

        var fishTf = fish.Transform;
        fishTf.Position = pos;
        // プレイヤーの真逆（既定）を向く ＝ 振り返って見ているカメラと正対する
        fishTf.Rotation = new SEED.Vector3(0f, player.Rotation.y + fishFacingYawOffsetDegrees, 0f);
    }

    /// <summary>
    /// 魚のスケールを「原寸 × <paramref name="ratio"/>」に設定する。
    /// 負の値は 0 に丸める（easeInBack / easeOutBack の行き過ぎで裏返らないようにする）。
    /// </summary>
    /// <param name="ratio">原寸に対する倍率（0＝消える / 1＝原寸）。</param>
    private void SetFishScale(float ratio)
    {
        if (shownFish is not { } fish || !fish.Actor.IsValid) { return; }

        // ハンドルは一旦ローカルへ受ける（プロパティの戻り値へ直接代入すると CS1612）
        var fishTf = fish.Transform;
        float safe = SEED.Mathf.Max(ratio, 0f);
        fishTf.Scale = fishTargetScale * safe;
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

    // ─── UI（ホワイトアウト・釣果テキスト）─────────────────────

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
    /// 釣果テキストを組み立てて表示する【表示内容を決める唯一の場所】。
    ///
    /// サイズは「魚の基準サイズ × 個体のサイズ倍率」で、単位ラベルは魚側の設定
    /// （<see cref="Fish.SizeUnitLabel"/>）を使う。ベストサイズはセーブデータの
    /// <c>best_size:&lt;表示名&gt;</c> に魚種ごとに記録し、更新したらその場で保存する。
    /// </summary>
    private void ShowTexts()
    {
        if (shownFish is not { } fish) { return; }

        string displayName = fish.DisplayName;
        float displaySize = fish.DisplaySize;
        string unit = fish.SizeUnitLabel;

        // ベストサイズ（魚種ごと）を読み、更新していれば書き戻して保存する
        string bestKey = BestSizeKeyPrefix + displayName;
        float previousBest = SEED.SaveData.GetFloat(bestKey, 0f);
        bool isNewRecord = displaySize > previousBest;
        if (isNewRecord)
        {
            SEED.SaveData.SetFloat(bestKey, displaySize);
            SEED.SaveData.Save();
        }
        float best = isNewRecord ? displaySize : previousBest;

        SetText(nameText, displayName);
        SetText(sizeText, FormatSize(displaySize, unit) + (isNewRecord ? NewRecordSuffix : ""));
        SetText(rankText, rankLabelPrefix + RankLabel(fish.SizeMultiplier));
        SetText(bestText, bestLabelPrefix + FormatSize(best, unit));
        SetText(promptText, promptMessage);
    }

    /// <summary>釣果テキストをすべて消す（アルファ 0）。</summary>
    private void HideTexts()
    {
        SetTextAlpha(nameText, 0f);
        SetTextAlpha(sizeText, 0f);
        SetTextAlpha(rankText, 0f);
        SetTextAlpha(bestText, 0f);
        SetTextAlpha(promptText, 0f);
    }

    /// <summary>
    /// テキストへ文字列を設定して表示する（アルファを 1 へ戻す）。
    /// 未設定・破棄済みなら何もしない。
    /// </summary>
    /// <param name="text">対象のテキスト（未設定可）。</param>
    /// <param name="content">表示する文字列。</param>
    private void SetText(SEED.Text? text, string content)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Content = content;
        t.Color = t.Color.WithAlpha(1f);
    }

    /// <summary>テキストのアルファだけを書き換える（RGB と文字列は保つ）。</summary>
    /// <param name="text">対象のテキスト（未設定可）。</param>
    /// <param name="alpha">不透明度（0〜1 へクランプする）。</param>
    private void SetTextAlpha(SEED.Text? text, float alpha)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Color = t.Color.WithAlpha(SEED.Mathf.Clamped01(alpha));
    }

    /// <summary>サイズ表示の書式（小数第 1 位＋単位ラベル）。</summary>
    /// <param name="size">表示するサイズ。</param>
    /// <param name="unit">単位ラベル（例: "cm"）。</param>
    private static string FormatSize(float size, string unit) => $"{size:F1} {unit}";

    /// <summary>
    /// サイズ倍率からランクのラベルを決める（S ≧ <see cref="rankSThreshold"/> …）。
    /// しきい値・ラベルはすべてインスペクタで差し替えられる。
    /// </summary>
    /// <param name="multiplier">個体のサイズ倍率。</param>
    private string RankLabel(float multiplier)
    {
        if (multiplier >= rankSThreshold) { return rankSLabel; }
        if (multiplier >= rankAThreshold) { return rankALabel; }
        if (multiplier >= rankBThreshold) { return rankBLabel; }
        return rankCLabel;
    }

    // ─── 汎用ヘルパー ─────────────────────────────────────────

    /// <summary>
    /// 指定 Animator を指定クリップへクロスフェードする
    /// （未設定・無効・空名・同一クリップ再生中は何もしない）。
    /// </summary>
    /// <param name="animator">対象の Animator（未設定可）。</param>
    /// <param name="clip">再生するクリップ名。</param>
    private void CrossFade(SEED.Animator? animator, string clip)
    {
        if (animator is not { } anim || !anim.IsValid) { return; }
        if (string.IsNullOrEmpty(clip)) { return; }
        if (anim.IsPlaying && anim.CurrentClip == clip) { return; }

        anim.CrossFade(clip, catchFadeSeconds);
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

        float yaw = SEED.Mathf.Atan2(direction.x, direction.z) * RadToDeg;
        float pitch = -SEED.Mathf.Asin(SEED.Mathf.Clamped(direction.y / length, -1f, 1f)) * RadToDeg;
        return new SEED.Vector3(pitch, yaw, 0f);
    }

    /// <summary>
    /// easeOutBack: <c>1 + c3·(t−1)³ + c1·(t−1)²</c>（1 を少し行き過ぎてから戻る）。
    /// </summary>
    /// <param name="t">進行度（0〜1）。</param>
    private static float EaseOutBack(float t)
    {
        float u = SEED.Mathf.Clamped01(t) - 1f;
        return 1f + BackEaseC3 * u * u * u + BackEaseC1 * u * u;
    }

    /// <summary>
    /// easeInBack: <c>c3·t³ − c1·t²</c>（0 側へ少し引いてから縮む）。
    /// </summary>
    /// <param name="t">進行度（0〜1）。</param>
    private static float EaseInBack(float t)
    {
        float u = SEED.Mathf.Clamped01(t);
        return BackEaseC3 * u * u * u - BackEaseC1 * u * u;
    }
}
