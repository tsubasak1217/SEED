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
/// Fade     … whiteoutFadeInSeconds 秒で白へ沈み、
///            whiteoutHoldSeconds 秒の真っ白を保持する。
///            真っ白になった最初のフレームでカット（SwitchToJumpComposition）。
/// LowAngle … 水面すれすれの低アングルで、魚が飛び出す区間（repeatWindowSeconds）
///            だけを repeatTimeScale のスローで repeatCount 回リピートする。
///            リピートの切れ目には白フラッシュ（repeatFlashSeconds）を挟む。
///            repeatCount = 0 なら<b>この段階を通らない</b>（従来どおり）。
/// SlowArc  … 横から見る構図へ toSideSeconds かけて移りつつ、魚が跳び切るまでを見せる
///            （Time.Scale = slowScale）。水平移動はしない（＝ウキの真上を上下するだけ）。
///            jumpSeconds × jumpApexRatio 秒で魚を隠し、次へ。
/// Result   … Time.Scale を戻し、ResultPanel を開く。
///            パネルが閉じ切る（ResultPanel.IsActive が false になる）まで待つ。
/// Close    … closeSeconds 秒の間を置いてから後始末（魚の破棄・カメラ復帰）。
/// </code>
///
/// <b>演出時刻（跳びの時刻）</b>
/// 魚の位置は「跳びの時刻」<see cref="jumpTime"/> だけから決まる純関数
/// （<see cref="PlaceFishOnJump"/>）なので、時刻を巻き戻せば同じ跳びを何度でも見せられる。
/// リピートのスローは <b>この時刻の進み方</b>（<see cref="repeatTimeScale"/>）で表現し、
/// <c>Time.Scale</c> は触らない（チュートリアルなど他の演出と競合させないため）。
///
/// <b>カット（<see cref="SwitchToJumpComposition"/>）</b>
/// 画面が完全に白いあいだに構図を切り替えるので、視点の飛びが見えない。
/// - 「ウキ→プレイヤーの向きに対して直角・跳びの中ほどの高さ」の姿勢を計算し、
///   <see cref="CameraMove.SetOverrideGoal"/> に<b>姿勢そのもの</b>と画角を渡して
///   1 フレームで飛ばす（＝シーンで結線した目標アクタに依存しない）。
///   カメラ距離は「跳びの縦幅が画角に収まる距離」と「基準距離＋ランクぶん」の<b>大きい方</b>
///   （<see cref="RequiredVerticalFitDistance"/>）。
///   以後この姿勢は<b>一切動かさない</b>ので、カットしてから釣果パネルまでカメラは静止する。
/// - 魚をウキから外し、水面のすぐ下（<see cref="fishSubmergeDepth"/>）へ置く。
/// - ウキも魚と一緒に跳ばす（<see cref="FloatFollowPosition"/> を
///   <see cref="FishingController"/> が読んで動かす。釣り糸もウキを追う）。
/// - しぶき（<see cref="splashActorPath"/> のパーティクル）と水音を出す。
///
/// <b>魚の姿勢</b>
/// 跳ねているあいだ、魚は<b>頭を真上へ向けた姿勢</b>で固定する
/// （<see cref="NoseUpPitchDegrees"/> ＋ <see cref="fishNoseTiltDegrees"/>）。
/// 回転はさせないので、モデルのアニメーション（泳ぎ・待機）はそのまま流れ続ける。
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

        /// <summary>
        /// 白が晴れ、水面すれすれの低アングルで「飛び出しの瞬間」だけをスローで繰り返す。
        /// <see cref="repeatCount"/> が 0 のときはこの段階を飛ばして
        /// <see cref="SlowArc"/> へ直行する（従来どおりの流れ）。
        /// </summary>
        LowAngle,

        /// <summary>白が晴れ、魚が水面から真上へ跳ね上がる（スロー）。</summary>
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

    /// <summary>
    /// 跳びの高さ曲線の頂点係数。<c>4·h·t·(1−t)</c> は t=0.5 でちょうど h になるので、
    /// 「頂点の高さ」をそのままインスペクタで指定できる。
    /// </summary>
    private const float ParabolaPeakCoefficient = 4f;

    /// <summary>通常時のゲーム時間の速さ（スローから戻すときの値）。</summary>
    private const float TimeScaleNormal = 1f;

    /// <summary>演出時刻（跳びの時刻）の通常の進み方（等速）。</summary>
    private const float PresentationSpeedNormal = 1f;

    /// <summary>リピート回数の下限（0＝リピートしない）。</summary>
    private const int MinRepeatCount = 0;

    /// <summary>
    /// リピート中の速さの下限。0 を指定されると跳びの時刻が進まず
    /// 演出が永久に終わらなくなるので、必ずこの値以上で進める。
    /// </summary>
    private const float MinRepeatTimeScale = 0.01f;

    /// <summary>着水しぶきの強度倍率の上限（1＝飛び出しと同じ規模）。</summary>
    private const float MaxIntensityScale = 1f;

    /// <summary>サイズランク S の段位（カメラ距離の加算段数。C=0 から数える）。</summary>
    private const int RankStepS = 3;

    /// <summary>サイズランク A の段位。</summary>
    private const int RankStepA = 2;

    /// <summary>サイズランク B の段位。</summary>
    private const int RankStepB = 1;

    /// <summary>サイズランク C（既定）の段位。</summary>
    private const int RankStepC = 0;

    /// <summary>
    /// 「頭を真上へ向ける」ためのピッチ角（度）。
    ///
    /// 本エンジンの回転（オイラー角）は <see cref="LookRotation"/> と同じ規約で、
    /// ピッチ ＝ <c>-asin(向きのY成分)</c>。したがって<b>真上（+Y）を向く</b>ピッチは -90 度。
    /// モデルの前方が +Z である前提（魚の遊泳 AI も <c>Rotation=(0,yaw,0)</c> で
    /// +Z を進行方向として扱っている＝<see cref="Fish"/> と同じ規約）。
    /// </summary>
    private const float NoseUpPitchDegrees = -90f;

    /// <summary>画角から必要距離を出すときの分母 <c>tan(fov/2)</c> の下限（0 除算よけ）。</summary>
    private const float MinTangent = 1e-3f;

    /// <summary>画角の半分を出すための除数。</summary>
    private const float HalfDivisor = 2f;

    /// <summary>半回転（度）。角度の最短回りの差分を求めるのに使う。</summary>
    private const float HalfTurnDegrees = 180f;

    /// <summary>1 回転（度）。角度を周期に畳み込むのに使う。</summary>
    private const float FullTurnDegrees = 360f;

    // ベストサイズ・ベストランク・釣った数の保存キーは FishRecords が一元管理する。

    // ─── 参照（インスペクタで割り当てる）───────────────────────

    /// <summary>
    /// 釣り上げ演出中のカメラ目標トランスフォーム（トップレベルの空アクタ「CatchCameraTarget」）。
    ///
    /// <b>目印としてだけ</b>使う（構図の確認用）。カメラの追従先には使わないので、
    /// 未設定でも構図は成立する（カメラへは <see cref="CameraMove.SetOverrideGoal"/> で
    /// 姿勢を直接渡す）。割り当てがあれば、真っ白の瞬間に計算した姿勢を書き込む。
    /// </summary>
    [Header("参照"), SerializeField(Label = "横カメラの目標(CatchCameraTarget)")]
    private SEED.Transform? catchCameraTarget = null;

    /// <summary>
    /// カメラの追従スクリプト。真っ白の瞬間に <see cref="CameraMove.SetOverrideGoal"/> へ
    /// 「横から見る姿勢」と画角を渡し、補間ではなく<b>カット</b>で切り替える
    /// （白の裏で切るので視点の飛びが見えない）。演出の終わりに
    /// <see cref="CameraMove.ClearOverrideGoal"/> で通常の追従へ返す。
    /// <b>未設定だと構図が切り替わらない</b>（従来の目標を追い続ける）ので必ず結線すること。
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
    ///
    /// 縦跳びは「跳びの中ほどの高さ」（<see cref="cameraFocusRatio"/>）を注視するので、
    /// <b>既定は 0＝完全な真横</b>。カメラ自身の高さも注視点と同じ高さになるため、
    /// 上下どちらへも同じだけ余白が残る（＝3 m 跳んでも頭が切れない）。
    /// </summary>
    [SerializeField(Label = "仰角φ(度)")]
    private float sideCameraPhi = 0f;

    /// <summary>
    /// 横カメラの基準距離（メートル）。実際の距離は
    /// <c>max(sideCameraDistance ＋ sideCameraDistancePerRank × ランク段位,
    /// 縦幅が画角に収まる距離)</c>（<see cref="RequiredVerticalFitDistance"/>）。
    /// ランク段位は C=0 / B=1 / A=2 / S=3（<see cref="RankStep"/>）。
    /// </summary>
    [SerializeField(Label = "距離(m)")]
    private float sideCameraDistance = 5.5f;

    /// <summary>サイズランク 1 段ごとに増やすカメラ距離（メートル）。0 でランク非依存。</summary>
    [SerializeField(Label = "ランク1段あたりの距離(m)")]
    private float sideCameraDistancePerRank = 1.8f;

    /// <summary>
    /// 注視点を跳びのどの高さに置くか（0〜1 の比率。跳ねる高さ <see cref="jumpHeight"/> に掛ける）。
    /// 0.5＝跳びの中ほど（上下の余白が等しくなるので既定）。
    /// </summary>
    [SerializeField(Label = "注視点の高さ比率")]
    private float cameraFocusRatio = 0.5f;

    /// <summary>注視点からのカメラの高さの上乗せ（メートル）。0 で注視点と同じ高さ。</summary>
    [SerializeField(Label = "注視点からの高さ(m)")]
    private float cameraHeightAboveFocus = 0f;

    /// <summary>
    /// 縦幅の収まりを計算するときに使う<b>垂直画角</b>（度）。
    ///
    /// 演出中の実際の画角は <see cref="CameraMove"/> が <c>fishingFov</c>（既定 45 度）へ
    /// 寄せるので、既定はそれに合わせた 45。ここを変えても実画角は変わらない
    /// （＝あくまで「どれだけ引けば収まるか」の見積もりに使う値）。
    /// </summary>
    [SerializeField(Label = "収まり計算に使う画角(度)")]
    private float verticalFitFovDegrees = 45f;

    /// <summary>
    /// 縦幅の収まりの余白（倍率）。1.0 でちょうど画面いっぱい、1.2 で 2 割の余白。
    /// </summary>
    [SerializeField(Label = "収まりの余白(倍)")]
    private float verticalFitMargin = 1.25f;

    // ─── 魚の縦跳び（SlowArc）──────────────────────────────────

    /// <summary>魚が跳ね始める深さ（水面からどれだけ下に置くか。メートル）。</summary>
    [Header("魚の縦跳び"), SerializeField(Label = "水面下の開始深さ(m)")]
    private float fishSubmergeDepth = 0.25f;

    /// <summary>跳びの頂点の高さ（開始点からの高さ。メートル）。</summary>
    [SerializeField(Label = "跳ねる高さ(m)")]
    private float jumpHeight = 3.0f;

    /// <summary>跳びの始めから着水までに掛ける秒数（実時間）。</summary>
    [SerializeField(Label = "跳ねる秒数")]
    private float jumpSeconds = 3.0f;

    /// <summary>
    /// 跳びのどこで釣果パネルへ切り替えるか（0〜1 の比率）。
    /// 0.5＝頂点。既定 0.7 は頂点を過ぎて落ち始めたところ（魚をじっくり見せる）。
    /// </summary>
    [SerializeField(Label = "パネルへ切り替える比率")]
    private float jumpApexRatio = 0.7f;

    /// <summary>
    /// 頭の向きの微調整（度）。0 で真上。正で頭がカメラ側／奥側へ傾く
    /// （<see cref="NoseUpPitchDegrees"/> に加算される）。
    /// </summary>
    [SerializeField(Label = "頭の傾き(度)")]
    private float fishNoseTiltDegrees = 0f;

    /// <summary>
    /// 魚の横向き（ヨー）の微調整（度）。基準は「ウキ→プレイヤー」の水平方向で、
    /// カメラは<b>そこから真横</b>に居るので、0 なら魚の側面がカメラを向く。
    /// </summary>
    [SerializeField(Label = "横向きの微調整(度)")]
    private float fishYawOffsetDegrees = 0f;

    /// <summary>跳びのあいだのゲーム時間の速さ（0.3＝3 割の速さ＝スロー）。</summary>
    [SerializeField(Label = "スローの速さ")]
    private float slowScale = 0.3f;

    /// <summary>
    /// 跳びのあいだ、ウキを魚のどこへ付けるか（魚の位置からのオフセット・メートル）。
    ///
    /// 跳ねている魚は<b>頭を真上へ向けた姿勢</b>で固定されるので、魚の「上（口・鼻先の方向）」は
    /// そのままワールドの +Y になる。したがってここはワールド座標のオフセットとして扱い、
    /// 既定 (0, +0.3, 0) で「口先のすこし上にウキが咥えられている」見え方になる。
    /// 竿先からウキへ張る釣り糸（LineRenderer）は <see cref="FishingController.UpdateLine"/> が
    /// このウキを終端として毎フレーム張り直すので、糸も一緒に跳ね上がる。
    /// </summary>
    [SerializeField(Label = "ウキの位置(魚からのオフセット・m)")]
    private SEED.Vector3 floatOffsetFromFish = new(0f, 0.3f, 0f);

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

    // ─── 水しぶき（波紋＋水柱。全魚共通・規模は大きさで変わる）───
    //
    // 生成そのものは WaterSplashSpawner（静的クラス）が担うので、シーンへの配置・
    // 結線は要らない。ここに並ぶのは「どれくらいの規模で出すか」の調整値だけで、
    // BuildSplashSettings() が WaterSplashSettings へ詰め替えて渡す。
    // 初期値は WaterSplashSettings の定数を参照しているので、
    // 「コード側の既定」と「インスペクタの既定」が食い違わない。

    /// <summary>波紋アクタのパス（空なら波紋を出さない）。</summary>
    [Header("水しぶき"), SerializeField(Label = "波紋のアクタ")]
    private string rippleActorPath = WaterSplashSettings.DefaultRippleActorPath;

    /// <summary>水柱アクタのパス（空なら水柱を出さない）。</summary>
    [SerializeField(Label = "水柱のアクタ")]
    private string columnActorPath = WaterSplashSettings.DefaultColumnActorPath;

    /// <summary>しぶきの強度が 0 になる魚の基準サイズ（cm）。</summary>
    [SerializeField(Label = "強度0のサイズ(cm)")]
    private float splashMinSizeCm = 10f;

    /// <summary>しぶきの強度が 1 になる魚の基準サイズ（cm）。</summary>
    [SerializeField(Label = "強度1のサイズ(cm)")]
    private float splashMaxSizeCm = 120f;

    /// <summary>波紋の個数（強度 0）。</summary>
    [SerializeField(Label = "波紋の個数(小)")]
    private int splashRippleCountMin = WaterSplashSettings.DefaultRippleCountMin;

    /// <summary>波紋の個数（強度 1）。</summary>
    [SerializeField(Label = "波紋の個数(大)")]
    private int splashRippleCountMax = WaterSplashSettings.DefaultRippleCountMax;

    /// <summary>波紋を散らす半径（強度 0・メートル）。</summary>
    [SerializeField(Label = "波紋の半径(小・m)")]
    private float splashRippleRadiusMin = WaterSplashSettings.DefaultRippleRadiusMin;

    /// <summary>波紋を散らす半径（強度 1・メートル）。</summary>
    [SerializeField(Label = "波紋の半径(大・m)")]
    private float splashRippleRadiusMax = WaterSplashSettings.DefaultRippleRadiusMax;

    /// <summary>波紋のスケール倍率（強度 0）。</summary>
    [SerializeField(Label = "波紋のスケール(小)")]
    private float splashRippleScaleMin = WaterSplashSettings.DefaultRippleScaleMin;

    /// <summary>波紋のスケール倍率（強度 1）。</summary>
    [SerializeField(Label = "波紋のスケール(大)")]
    private float splashRippleScaleMax = WaterSplashSettings.DefaultRippleScaleMax;

    /// <summary>水柱の本数（強度 0。中心の 1 本を含む）。</summary>
    [SerializeField(Label = "水柱の本数(小)")]
    private int splashColumnCountMin = WaterSplashSettings.DefaultColumnCountMin;

    /// <summary>水柱の本数（強度 1。中心の 1 本を含む）。</summary>
    [SerializeField(Label = "水柱の本数(大)")]
    private int splashColumnCountMax = WaterSplashSettings.DefaultColumnCountMax;

    /// <summary>水柱を散らす半径（強度 0・メートル）。</summary>
    [SerializeField(Label = "水柱の半径(小・m)")]
    private float splashColumnRadiusMin = WaterSplashSettings.DefaultColumnRadiusMin;

    /// <summary>水柱を散らす半径（強度 1・メートル）。</summary>
    [SerializeField(Label = "水柱の半径(大・m)")]
    private float splashColumnRadiusMax = WaterSplashSettings.DefaultColumnRadiusMax;

    /// <summary>水柱のスケール倍率（強度 0）。</summary>
    [SerializeField(Label = "水柱のスケール(小)")]
    private float splashColumnScaleMin = WaterSplashSettings.DefaultColumnScaleMin;

    /// <summary>水柱のスケール倍率（強度 1）。</summary>
    [SerializeField(Label = "水柱のスケール(大)")]
    private float splashColumnScaleMax = WaterSplashSettings.DefaultColumnScaleMax;

    /// <summary>1 個ごとのスケールのばらつき（±の割合。0 で揃う）。</summary>
    [SerializeField(Label = "スケールのばらつき")]
    private float splashScaleJitter = WaterSplashSettings.DefaultScaleJitter;

    /// <summary>水面から浮かせる高さ（メートル。水面とのちらつき避け）。</summary>
    [SerializeField(Label = "水面から浮かせる高さ(m)")]
    private float splashSurfaceOffsetY = WaterSplashSettings.DefaultSurfaceOffsetY;

    /// <summary>
    /// 着水（跳びの終端で魚が水面へ戻る瞬間）にも小さめのしぶきを出すか。
    ///
    /// <b>注意</b>: 跳びは <see cref="jumpApexRatio"/> の時点で釣果パネルへ切り替わるため、
    /// 既定（0.7）のままでは<b>終端まで進まない＝この着水しぶきは出ない</b>。
    /// 出したい場合は <see cref="jumpApexRatio"/> を 1.0 に近づけること。
    /// </summary>
    [SerializeField(Label = "着水にもしぶきを出す")]
    private bool splashOnLanding = true;

    /// <summary>着水しぶきの強度倍率（飛び出しに対する割合。0.5 で半分の規模）。</summary>
    [SerializeField(Label = "着水しぶきの強度倍率")]
    private float splashLandingIntensityScale = 0.5f;

    // ─── 飛び出しリピート（低アングル）───────────────────────
    //
    // 白フェード明けに「水面すれすれの低い視点」で飛び出しの瞬間だけを繰り返す。
    // 巻き戻すのは<b>跳びの時刻</b>（jumpTime）だけなので、魚の位置・姿勢を決める式
    // （PlaceFishOnJump）は 1 つのまま使い回せる。

    /// <summary>
    /// 飛び出しを繰り返す回数。<b>0 で従来どおり</b>（低アングル段階を通らず、
    /// 白フェード明けにいきなり横カメラの跳びが始まる）。
    /// </summary>
    [Header("飛び出しリピート"), SerializeField(Label = "リピート回数(0で無効)")]
    private int repeatCount = 3;

    /// <summary>
    /// 1 回のリピートで見せる区間（跳びの時刻で何秒ぶんか）。
    /// 跳びの先頭からこの秒数だけを繰り返す（＝水面から飛び出すところ）。
    /// </summary>
    [SerializeField(Label = "リピートする区間(秒)")]
    private float repeatWindowSeconds = 0.5f;

    /// <summary>
    /// リピート中の演出時刻の進み方（0.3＝3 割の速さ＝スロー）。
    /// <c>Time.Scale</c> は使わないので、チュートリアル等の他演出と競合しない。
    /// </summary>
    [SerializeField(Label = "リピート中の速さ")]
    private float repeatTimeScale = 0.3f;

    /// <summary>リピートの切れ目に入れる白フラッシュの秒数（0 で入れない）。</summary>
    [SerializeField(Label = "切替の白フラッシュ(秒)")]
    private float repeatFlashSeconds = 0.12f;

    /// <summary>白フラッシュの強さ（0〜1。1 で真っ白）。</summary>
    [SerializeField(Label = "白フラッシュの強さ")]
    private float repeatFlashStrength = 0.7f;

    /// <summary>低アングルのカメラ高さ（水面からの高さ・メートル）。</summary>
    [SerializeField(Label = "低アングルの高さ(m)")]
    private float lowAngleHeight = 0.3f;

    /// <summary>低アングルのカメラ距離（飛び出し点からの水平距離・メートル）。</summary>
    [SerializeField(Label = "低アングルの距離(m)")]
    private float lowAngleDistance = 3.0f;

    /// <summary>
    /// 低アングルの方位角（度）。基準は「ウキ→プレイヤー」の水平方向で、
    /// 90 度＝真横（横カメラと同じ側）。
    /// </summary>
    [SerializeField(Label = "低アングルの方位角θ(度)")]
    private float lowAngleTheta = 90f;

    /// <summary>低アングルの注視点の高さ（水面からの高さ・メートル）。</summary>
    [SerializeField(Label = "低アングルの注視点の高さ(m)")]
    private float lowAngleFocusHeight = 0.8f;

    /// <summary>低アングル中の画角（度）。小さいほど寄って見える。</summary>
    [SerializeField(Label = "低アングルの画角(度)")]
    private float lowAngleFovDegrees = 40f;

    /// <summary>低アングルから横カメラへ移るのに掛ける秒数（実時間）。0 で即座に切り替わる。</summary>
    [SerializeField(Label = "横カメラへ移る秒数")]
    private float toSideSeconds = 0.6f;

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

    /// <summary>
    /// 跳びのあいだ、ウキを置くべきワールド位置【ウキを一緒に跳ばすための唯一の窓口】。
    /// null なら「演出はウキの位置を指定しない」＝従来どおり <see cref="FishingController"/> の
    /// 都合（格納・追従）で決めてよい、という意味。
    ///
    /// ウキ本体を持っているのは <see cref="FishingController"/> なので、本スクリプトは
    /// 位置を<b>教えるだけ</b>にして、実際に動かすのは持ち主に任せる
    /// （＝ウキの参照をこちらにも結線する必要が無く、シーンの結線が増えない）。
    /// </summary>
    public SEED.Vector3? FloatFollowPosition { get; private set; } = null;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>演出中の魚（null = 演出していない）。<see cref="Begin"/> で束縛し、<see cref="Finish"/> で破棄する。</summary>
    private Fish? shownFish = null;

    /// <summary>釣り上げた瞬間のウキのワールド位置（＝水面の基準点）。<see cref="Begin"/> で受け取る。</summary>
    private SEED.Vector3 floatPosition = SEED.Vector3.Zero;

    /// <summary>
    /// 魚が跳ね始める点（ウキの真下・水面のすこし下）。カットの瞬間に確定する。
    /// 縦跳びなので、魚はこの点の<b>真上</b>だけを行き来する（水平方向へは動かない）。
    /// </summary>
    private SEED.Vector3 jumpStart = SEED.Vector3.Zero;

    /// <summary>
    /// 魚のヨー角（度）。「ウキ→プレイヤー」の水平方向 ＋ <see cref="fishYawOffsetDegrees"/>。
    /// カメラはこの向きに対して真横（θ=90°）に居るので、既定では魚の側面がカメラを向く。
    /// </summary>
    private float fishYawDegrees = 0f;

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

    /// <summary>
    /// 跳びの時刻（秒）【魚の位置・姿勢を決める唯一の時間軸】。
    /// 0 で水面、<see cref="jumpSeconds"/> で着水。リピートはこの値を 0 へ巻き戻すだけ。
    /// フェーズの経過秒（<see cref="phaseElapsed"/>）とは別に持つので、
    /// 巻き戻してもフェーズの進行（＝いつ次へ行くか）は壊れない。
    /// </summary>
    private float jumpTime = 0f;

    /// <summary>これまでに見せたリピートの回数（<see cref="repeatCount"/> に達したら横カメラへ）。</summary>
    private int repeatIndex = 0;

    /// <summary>白フラッシュの残り秒数（0 で消灯）。</summary>
    private float flashRemaining = 0f;

    /// <summary>白が晴れ始めてからの経過秒数（実時間）。フェーズをまたいで数える。</summary>
    private float fadeOutElapsed = 0f;

    /// <summary>低アングルのカメラ位置（カット時に確定し、以後は固定）。</summary>
    private SEED.Vector3 lowAngleCameraPosition = SEED.Vector3.Zero;

    /// <summary>低アングルのカメラ回転（オイラー角・度）。</summary>
    private SEED.Vector3 lowAngleCameraRotation = SEED.Vector3.Zero;

    /// <summary>横カメラの位置（カット時に確定し、以後は固定）。</summary>
    private SEED.Vector3 sideCameraPosition = SEED.Vector3.Zero;

    /// <summary>横カメラの回転（オイラー角・度）。</summary>
    private SEED.Vector3 sideCameraRotation = SEED.Vector3.Zero;

    /// <summary>低アングル→横カメラの移行中か。</summary>
    private bool cameraBlending = false;

    /// <summary>移行を始めてからの経過秒数（実時間）。</summary>
    private float cameraBlendElapsed = 0f;

    /// <summary>着水しぶきを出したか（1 回の跳びにつき 1 回だけ出すためのガード）。</summary>
    private bool landingSplashDone = false;

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
            case CatchPhase.Fade:     UpdateFade();          break;
            case CatchPhase.LowAngle: UpdateLowAngle(dt);    break;
            case CatchPhase.SlowArc:  UpdateSlowArc(dt);     break;
            case CatchPhase.Result:   UpdateResult();        break;
            case CatchPhase.Close:    UpdateClose();         break;
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
    /// アルファが 1 に達した最初のフレームで <see cref="SwitchToJumpComposition"/> を呼び、
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
        if (reachedFullWhite) { SwitchToJumpComposition(); }

        if (phaseElapsed < fadeIn + SEED.Mathf.Max(whiteoutHoldSeconds, 0f)) { return; }

        // 白はここから晴れ始める（LowAngle / SlowArc をまたいで数える）
        fadeOutElapsed = 0f;
        EnterPhase(UseJumpRepeat ? CatchPhase.LowAngle : CatchPhase.SlowArc);
    }

    /// <summary>
    /// <see cref="CatchPhase.LowAngle"/> の更新（低アングルで飛び出しをリピート）。
    ///
    /// - 白: <see cref="whiteoutFadeOutSeconds"/> で晴れる（フラッシュがあればそちらが優先）
    /// - 魚: 跳びの時刻を <see cref="repeatTimeScale"/> 倍の速さで進める（スロー）
    /// - 時刻が <see cref="repeatWindowSeconds"/> を越えたら 0 へ巻き戻して撮り直す
    ///   （＝同じ飛び出しをもう一度見せる）。巻き戻しのたびに白フラッシュとしぶきを出す。
    /// - <see cref="repeatCount"/> 回ぶん見せ切ったら <see cref="CatchPhase.SlowArc"/> へ。
    ///
    /// <b>Time.Scale は触らない</b>。スローは跳びの時刻の進み方だけで表現するので、
    /// チュートリアルなど他の演出が Time.Scale を使っていても喧嘩しない。
    /// </summary>
    /// <param name="deltaTime">このフレームの実時間の経過秒数。</param>
    private void UpdateLowAngle(float deltaTime)
    {
        UpdateWhiteout(deltaTime);

        float span = SEED.Mathf.Max(jumpSeconds, DivideEpsilon);
        jumpTime += deltaTime * SEED.Mathf.Max(repeatTimeScale, MinRepeatTimeScale);
        PlaceFishOnJump(jumpTime / span);

        // リピートの区間は跳び全体を超えない（超えると着水後まで映してしまう）
        float window = SEED.Mathf.Clamped(repeatWindowSeconds, DivideEpsilon, span);
        if (jumpTime < window) { return; }

        repeatIndex++;
        if (repeatIndex >= SEED.Mathf.Max(repeatCount, MinRepeatCount))
        {
            EnterPhase(CatchPhase.SlowArc);
            return;
        }

        // まだ繰り返す: 時刻を巻き戻し、切り替わりが分かるよう白を一瞬入れる
        BeginJump(spawnSplash: true);
        flashRemaining = SEED.Mathf.Max(repeatFlashSeconds, 0f);
    }

    /// <summary>
    /// <see cref="CatchPhase.SlowArc"/> の更新（白が晴れる／魚が真上へ跳ぶ）。
    ///
    /// - 白: <see cref="whiteoutFadeOutSeconds"/> で 1 → 0
    /// - 魚: <see cref="jumpStart"/> の真上を <see cref="jumpSeconds"/> 秒で往復する
    ///   （姿勢は頭を真上へ向けたまま固定。回転はしない）
    /// - <see cref="jumpSeconds"/> × <see cref="jumpApexRatio"/> 秒で次のフェーズへ
    /// - 低アングルのリピートから来た場合は、<see cref="toSideSeconds"/> かけて
    ///   カメラを横の構図へ移す（<see cref="UpdateCameraBlend"/>）
    /// </summary>
    /// <param name="deltaTime">このフレームの実時間の経過秒数。</param>
    private void UpdateSlowArc(float deltaTime)
    {
        // 白を晴らす（LowAngle から来た場合は既に晴れているので変化しない）
        UpdateWhiteout(deltaTime);

        // 低アングルから来たときは、ここで横カメラへ滑らかに移る
        UpdateCameraBlend(deltaTime);

        // 跳びの位置と姿勢を置き直す（この段階の時刻は等速で進む）
        float span = SEED.Mathf.Max(jumpSeconds, DivideEpsilon);
        jumpTime += deltaTime * PresentationSpeedNormal;
        PlaceFishOnJump(jumpTime / span);

        // 着水（跳びの終端）に達したら、小さめのしぶきをもう一度
        UpdateLandingSplash(span);

        // 指定の比率まで来たら魚を隠して釣果パネルへ
        float switchSeconds = span * SEED.Mathf.Clamped01(jumpApexRatio);
        if (jumpTime < switchSeconds) { return; }

        HideFish();
        EnterPhase(CatchPhase.Result);
    }

    /// <summary>
    /// 着水しぶきの判定【着水を検出する唯一の場所】。
    ///
    /// 跳びの時刻が終端（<see cref="jumpSeconds"/>）に達した最初のフレームで
    /// 小さめのしぶきを出す。<see cref="jumpApexRatio"/> が 1 未満のときは
    /// その前に釣果パネルへ切り替わるため、実際には出ない（仕様どおり）。
    /// </summary>
    /// <param name="span">跳び 1 回ぶんの秒数（0 除算よけ済み）。</param>
    private void UpdateLandingSplash(float span)
    {
        if (!splashOnLanding || landingSplashDone) { return; }
        if (jumpTime < span) { return; }

        landingSplashDone = true;
        SpawnWaterSplash(splashLandingIntensityScale);
        PlaySplashSe();
    }

    /// <summary>
    /// ホワイトアウトの更新【白のアルファを決める唯一の場所】。
    ///
    /// 白は 2 つの源を持ち、<b>濃いほうを採る</b>:
    /// <list type="bullet">
    ///   <item>フェードアウト … <see cref="whiteoutFadeOutSeconds"/> で 1 → 0（1 回だけ）</item>
    ///   <item>フラッシュ     … リピートの切れ目に <see cref="repeatFlashSeconds"/> で減衰</item>
    /// </list>
    /// フェードアウトの経過は <see cref="fadeOutElapsed"/> でフェーズをまたいで数えるので、
    /// <see cref="CatchPhase.LowAngle"/> → <see cref="CatchPhase.SlowArc"/> と進んでも
    /// 白が戻ったりしない。
    /// </summary>
    /// <param name="deltaTime">このフレームの実時間の経過秒数。</param>
    private void UpdateWhiteout(float deltaTime)
    {
        fadeOutElapsed += deltaTime;

        float fadeOut = SEED.Mathf.Max(whiteoutFadeOutSeconds, 0f);
        float alpha = fadeOut <= DivideEpsilon
            ? 0f
            : 1f - SEED.Mathf.Clamped01(fadeOutElapsed / fadeOut);

        if (flashRemaining > 0f)
        {
            flashRemaining = SEED.Mathf.Max(flashRemaining - deltaTime, 0f);
            float flashSpan = SEED.Mathf.Max(repeatFlashSeconds, DivideEpsilon);
            float flash = SEED.Mathf.Clamped01(flashRemaining / flashSpan)
                        * SEED.Mathf.Clamped01(repeatFlashStrength);
            alpha = SEED.Mathf.Max(alpha, flash);
        }

        SetWhiteoutAlpha(alpha);
    }

    /// <summary>
    /// 低アングル → 横カメラの移行【カメラ移行の唯一の実装】。
    ///
    /// 位置・回転・画角を <see cref="Easing.InOutSine"/> で補間し、毎フレーム
    /// <see cref="CameraMove.SetOverrideGoal"/> へ<b>カット指定で</b>渡す。
    /// カメラ側の指数補間に任せず、こちらの曲線どおりに動かすためで、
    /// これにより「移行の秒数」がそのまま画に出る。
    /// </summary>
    /// <param name="deltaTime">このフレームの実時間の経過秒数。</param>
    private void UpdateCameraBlend(float deltaTime)
    {
        if (!cameraBlending) { return; }

        cameraBlendElapsed += deltaTime;
        float duration = SEED.Mathf.Max(toSideSeconds, 0f);
        float t = Easing.InOutSine(Easing.Progress01(cameraBlendElapsed, duration));

        ApplyCameraPose(
            SEED.Vector3.Lerp(lowAngleCameraPosition, sideCameraPosition, t),
            LerpAngles(lowAngleCameraRotation, sideCameraRotation, t),
            SEED.Mathf.Lerp(lowAngleFovDegrees, verticalFitFovDegrees, t));

        if (cameraBlendElapsed < duration) { return; }

        // 端数で微妙にずれた姿勢が残らないよう、最後は目標そのものを入れて締める
        cameraBlending = false;
        ApplyCameraPose(sideCameraPosition, sideCameraRotation, verticalFitFovDegrees);
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
        CatchPhase previous = Phase;
        Phase = next;
        phaseElapsed = 0f;

        switch (next)
        {
            case CatchPhase.LowAngle:
                // 跳びそのものは白の裏（SwitchToJumpComposition）で始めてあるので、
                // ここではリピートの数え直しだけ。Time.Scale は<b>触らない</b>
                // （スローは跳びの時刻の進み方で表現する）。
                repeatIndex = 0;
                break;

            case CatchPhase.SlowArc:
                // 弧を見せているあいだだけゲーム時間を遅くする（戻しは Result / Finish）
                ApplySlow(true);

                // 低アングルのリピートから来たときは、もう一度頭から跳ばせながら
                // カメラを横の構図へ移す（＝最後の 1 回だけを横から見せ切る）。
                if (previous == CatchPhase.LowAngle)
                {
                    BeginJump(spawnSplash: true);
                    cameraBlending = true;
                    cameraBlendElapsed = 0f;
                }
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
    /// 1. 跳びの始点と魚の向きを決める（水面と「ウキ→プレイヤー」の向きが基準）
    /// 2. 低アングルと横カメラの姿勢を<b>両方</b>計算し、
    ///    いま使うほう（リピートするなら低アングル）を
    ///    <see cref="CameraMove.SetOverrideGoal"/> へ渡してカットする
    /// 3. 魚をウキから外して始点（水面のすこし下）へ置く
    /// 4. しぶき（パーティクル・波紋・水柱）と水音を出す
    /// </summary>
    private void SwitchToJumpComposition()
    {
        // 1. 跳びの始点（ウキの真下・水面のすこし下）と魚の向き
        SEED.Vector3 toPlayer = HorizontalToPlayer();
        jumpStart = floatPosition - SEED.Vector3.Up * SEED.Mathf.Max(fishSubmergeDepth, 0f);
        fishYawDegrees = SEED.Mathf.Atan2(toPlayer.x, toPlayer.z) * SEED.Mathf.Rad2Deg
                       + fishYawOffsetDegrees;

        // 2. カメラの構図を<b>2 つとも</b>先に計算しておく
        //    （低アングル → 横カメラの移行で両方の姿勢が要るため、
        //      計算はここ 1 回だけ・以後は補間するだけにする）
        ComputeSideCameraPose(toPlayer);
        ComputeLowAnglePose(toPlayer);

        // いま使うほうへカット（白の裏なので視点の飛びは見えない）
        if (UseJumpRepeat)
        {
            ApplyCameraPose(lowAngleCameraPosition, lowAngleCameraRotation, lowAngleFovDegrees);
        }
        else
        {
            ApplyCameraPose(sideCameraPosition, sideCameraRotation, verticalFitFovDegrees);
        }

        // 3. 跳びを頭から始める（魚を始点へ・しぶき・水音）
        BeginJump(spawnSplash: true);

        SEED.Debug.Log(
            $"[Catch] カット: start={jumpStart} 高さ={jumpHeight:F1}m yaw={fishYawDegrees:F1}"
          + $" リピート={(UseJumpRepeat ? repeatCount : 0)}");
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

        // ウキの位置指定を返上する（以後は FishingController の都合＝格納に戻る）
        FloatFollowPosition = null;

        // カメラの姿勢の上書きも必ず外す（外し忘れると演出が終わっても
        // カメラが横向きのまま固まる）。戻りは補間なので構図は滑らかに繋がる。
        cameraMove?.ClearOverrideGoal();

        Phase = CatchPhase.None;
        phaseElapsed = 0f;

        // リピート・カメラ移行の状態も必ず初期化する
        // （次の釣り上げが前回の途中状態を引き継がないように）
        jumpTime = 0f;
        repeatIndex = 0;
        flashRemaining = 0f;
        fadeOutElapsed = 0f;
        cameraBlending = false;
        cameraBlendElapsed = 0f;
        landingSplashDone = false;

        SetWhiteoutAlpha(0f);
    }

    // ─── 魚の放物線 ───────────────────────────────────────────

    /// <summary>
    /// 縦跳びの位置・姿勢へ魚を置く【魚の見た目を決める唯一の場所】。
    ///
    /// 位置 ＝ <c>始点 ＋ 上 × 4·h·t·(1−t)</c>（水平方向へは一切動かさない）。
    /// <c>4·h·t·(1−t)</c> は t=0.5 でちょうど h になるので、
    /// インスペクタの「跳ねる高さ」がそのまま頂点の高さになる。
    ///
    /// 姿勢 ＝ ピッチ <see cref="NoseUpPitchDegrees"/>（＝頭が真上）＋
    /// <see cref="fishNoseTiltDegrees"/>、ヨーは <see cref="fishYawDegrees"/> で固定。
    /// <b>回転させない</b>ので、モデルに付いているアニメーション（泳ぎ・待機）は
    /// そのまま流れ続け、跳ねながらヒレを動かしているように見える。
    /// </summary>
    /// <param name="t">跳びの進行度（0〜1）。</param>
    private void PlaceFishOnJump(float t)
    {
        float ratio = SEED.Mathf.Clamped01(t);
        float height = ParabolaPeakCoefficient * jumpHeight * ratio * (1f - ratio);
        var fishPosition = jumpStart + SEED.Vector3.Up * height;

        // ウキの追従先は魚の有無に関わらず更新する（魚が破棄されていても
        // ウキだけ跳んだ位置に取り残されない＝位置の真実を 1 か所に保つ）。
        FloatFollowPosition = fishPosition + floatOffsetFromFish;

        if (shownFish is not { } fish || !fish.Actor.IsValid) { return; }

        var fishTf = fish.Transform;
        fishTf.Position = fishPosition;
        fishTf.Rotation = new SEED.Vector3(
            NoseUpPitchDegrees + fishNoseTiltDegrees, fishYawDegrees, 0f);
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
    /// 横カメラの目標（<see cref="catchCameraTarget"/>）を縦跳びに合わせて置く
    /// 【この構図の唯一の算出点】。
    ///
    /// <code>
    /// 注視点   ＝ 跳びの真上・中ほどの高さ（始点 ＋ 上 × 跳ねる高さ × 注視点の高さ比率）
    /// 水平方向 ＝ 「ウキ→プレイヤー」を方位角θぶん右へ回した向き（θ=90 で真横）
    /// 距離     ＝ max(基準距離 ＋ ランク段位 × ランクあたりの距離, 縦幅が収まる距離)
    /// 位置     ＝ (注視点の水平位置 ＋ 水平方向 × 距離·cosφ,
    ///              注視点の高さ ＋ 注視点からの高さ ＋ 距離·sinφ,
    ///              …)
    /// 向き     ＝ その位置から注視点を見る向き
    /// </code>
    ///
    /// <b>縦跳びに合わせた変更点</b>
    /// 放物線だったころは「水面のすこし上から見上げる」構図でよかったが、
    /// 縦跳びは<b>画面の上下いっぱい</b>を使うので、
    /// - カメラの高さを<b>注視点と同じ</b>にして（φ=0・上乗せ 0）上下の余白を等しくし、
    /// - 距離は「跳びの縦幅が画角に収まる距離」を下限にする
    /// （<see cref="RequiredVerticalFitDistance"/>）。
    /// これで「跳ねる高さ」を大きくしてもカメラが自動で引き、頭が切れない。
    ///
    /// <b>計算だけ</b>を行い、結果は <see cref="sideCameraPosition"/> /
    /// <see cref="sideCameraRotation"/> に控える（カメラへ渡すのは
    /// <see cref="ApplyCameraPose"/> の役目）。低アングルからの移行で
    /// 「移行先の姿勢」として補間に使うため、計算と適用を分けてある。
    /// </summary>
    /// <param name="toPlayer">「ウキ→プレイヤー」の水平方向（正規化済み）。</param>
    private void ComputeSideCameraPose(SEED.Vector3 toPlayer)
    {
        // 注視点＝跳びの真上・中ほどの高さ（水平位置は跳びの始点＝ウキの真下と同じ）
        var focus = jumpStart
                  + SEED.Vector3.Up * (jumpHeight * SEED.Mathf.Clamped01(cameraFocusRatio));

        // toPlayer を右へ 90° 回した水平方向（θ=90° の方位に対応）
        var right = new SEED.Vector3(toPlayer.z, 0f, -toPlayer.x);

        float thetaRad = sideCameraTheta * SEED.Mathf.Deg2Rad;
        float phiRad = sideCameraPhi * SEED.Mathf.Deg2Rad;
        var horizDir = toPlayer * SEED.Mathf.Cos(thetaRad) + right * SEED.Mathf.Sin(thetaRad);

        // ランクぶんの引きと「縦幅が収まる引き」の大きい方を採る。
        // ランク指定だけだと、跳ねる高さを上げたときに必ず頭が切れる。
        float rankDistance = SEED.Mathf.Max(
            sideCameraDistance + sideCameraDistancePerRank * RankStep(), 0f);
        float distance = SEED.Mathf.Max(rankDistance, RequiredVerticalFitDistance());

        var camPos = new SEED.Vector3(
            focus.x + horizDir.x * distance * SEED.Mathf.Cos(phiRad),
            focus.y + cameraHeightAboveFocus + distance * SEED.Mathf.Sin(phiRad),
            focus.z + horizDir.z * distance * SEED.Mathf.Cos(phiRad));

        sideCameraPosition = camPos;
        sideCameraRotation = LookRotation(focus - camPos);
    }

    /// <summary>
    /// 低アングル（水面すれすれ）の構図を計算する【この構図の唯一の算出点】。
    ///
    /// <code>
    /// 注視点   ＝ 飛び出し点の真上・水面から lowAngleFocusHeight
    /// 水平方向 ＝ 「ウキ→プレイヤー」を方位角 lowAngleTheta ぶん右へ回した向き
    /// 位置     ＝ 飛び出し点 ＋ 水平方向 × lowAngleDistance、高さは水面 ＋ lowAngleHeight
    /// 向き     ＝ その位置から注視点を見上げる向き
    /// </code>
    ///
    /// 水面（<see cref="floatPosition"/> の高さ）を基準にするので、
    /// 魚の潜り深さ（<see cref="fishSubmergeDepth"/>）を変えてもカメラは水面に貼り付く。
    /// </summary>
    /// <param name="toPlayer">「ウキ→プレイヤー」の水平方向（正規化済み）。</param>
    private void ComputeLowAnglePose(SEED.Vector3 toPlayer)
    {
        float surfaceY = floatPosition.y;

        // 注視点は飛び出し点のすこし上（飛び出す魚が画面の中ほどへ抜けていく）
        var focus = new SEED.Vector3(
            jumpStart.x, surfaceY + SEED.Mathf.Max(lowAngleFocusHeight, 0f), jumpStart.z);

        // toPlayer を右へ 90° 回した水平方向（θ=90° の方位に対応）
        var right = new SEED.Vector3(toPlayer.z, 0f, -toPlayer.x);
        float thetaRad = lowAngleTheta * SEED.Mathf.Deg2Rad;
        var horizDir = toPlayer * SEED.Mathf.Cos(thetaRad) + right * SEED.Mathf.Sin(thetaRad);

        float distance = SEED.Mathf.Max(lowAngleDistance, 0f);
        var camPos = new SEED.Vector3(
            jumpStart.x + horizDir.x * distance,
            surfaceY + lowAngleHeight,
            jumpStart.z + horizDir.z * distance);

        lowAngleCameraPosition = camPos;
        lowAngleCameraRotation = LookRotation(focus - camPos);
    }

    /// <summary>
    /// 計算した姿勢をカメラへ渡す【カメラへ触る唯一の場所】。
    ///
    /// カメラへは<b>姿勢そのもの</b>を渡してカットする。
    /// シーンで結線した目標アクタ（CameraMove.catchTarget）に依存しないので、
    /// 結線の有無・取り違えで構図が変わらない。画角も同時に固定して、
    /// 位置・回転だけが飛んで FOV が補間で寄る（＝止まっているのに画が動く）のを防ぐ。
    ///
    /// 移行中（<see cref="UpdateCameraBlend"/>）も毎フレームここを通す。
    /// 補間の曲線はこちらが持ち、カメラ側の追従は挟まない（＝指定した秒数どおりに動く）。
    /// </summary>
    /// <param name="position">カメラの位置（ワールド）。</param>
    /// <param name="rotationDegrees">カメラの回転（オイラー角・度）。</param>
    /// <param name="fovDegrees">画角（度）。</param>
    private void ApplyCameraPose(
        SEED.Vector3 position, SEED.Vector3 rotationDegrees, float fovDegrees)
    {
        // 目印アクタが割り当ててあれば同じ姿勢を書いておく（エディタで構図を確認するため。
        // カメラの追従先としては使わないので、未設定でも構図は成立する）。
        if (catchCameraTarget is { IsValid: true } goal)
        {
            goal.Position = position;
            goal.Rotation = rotationDegrees;
        }

        if (cameraMove is { } cam)
        {
            cam.SetOverrideGoal(position, rotationDegrees, snap: true, fovDegrees: fovDegrees);
        }
    }

    /// <summary>
    /// 跳びの縦幅が画角に収まる最小のカメラ距離（メートル）
    /// 【「頭が切れない距離」の唯一の計算式】。
    ///
    /// <code>
    /// 縦幅      ＝ 跳ねる高さ ＋ 水面下の開始深さ（＝水中の始点から頂点まで）
    /// 半分の幅  ＝ 縦幅 / 2 × 余白（注視点が縦幅の真ん中に来る前提）
    /// 必要距離  ＝ 半分の幅 / tan(画角 / 2)
    /// </code>
    ///
    /// 既定値（跳ねる高さ 3.0 m・開始深さ 0.25 m・画角 45 度・余白 1.25 倍）なら
    /// <c>(3.25 / 2 × 1.25) / tan(22.5°) ≒ 4.9 m</c>。
    /// 既定の基準距離 5.5 m のほうが大きいので、既定値のままでは
    /// この下限は効かない（＝跳ねる高さを 3.5 m 以上に上げたときに効き始める）。
    ///
    /// <b>画角について</b>: 実際の画角は <see cref="CameraMove"/> が決めるので、
    /// ここでは <see cref="verticalFitFovDegrees"/> を「見積もりに使う値」として使う。
    /// 実画角と食い違うと余白の量がずれるだけで、破綻はしない。
    /// </summary>
    private float RequiredVerticalFitDistance()
    {
        // 魚と一緒に跳ぶウキは魚より上に居るので、その高さぶんも収める対象に含める。
        float span = SEED.Mathf.Max(jumpHeight, 0f)
                   + SEED.Mathf.Max(fishSubmergeDepth, 0f)
                   + SEED.Mathf.Max(floatOffsetFromFish.y, 0f);
        float halfSpan = span / HalfDivisor * SEED.Mathf.Max(verticalFitMargin, 0f);

        float halfFovRad = SEED.Mathf.Max(verticalFitFovDegrees, 0f)
                         / HalfDivisor * SEED.Mathf.Deg2Rad;
        float tangent = SEED.Mathf.Max(SEED.Mathf.Tan(halfFovRad), MinTangent);
        return halfSpan / tangent;
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

    // ─── 跳びの開始 ───────────────────────────────────────────

    /// <summary>
    /// 跳びを頭から始める【跳びの時刻を 0 にする唯一の場所】。
    ///
    /// 初回のカット（<see cref="SwitchToJumpComposition"/>）でも、リピートの
    /// 巻き戻しでも、最後の 1 回（<see cref="CatchPhase.SlowArc"/> の頭）でも、
    /// 「水面から飛び出す瞬間」はすべてここを通る。したがって
    /// <b>しぶきを出す瞬間もここ 1 か所</b>に集約できる。
    /// </summary>
    /// <param name="spawnSplash">true でしぶき（パーティクル・波紋・水柱）と水音を出す。</param>
    private void BeginJump(bool spawnSplash)
    {
        jumpTime = 0f;
        landingSplashDone = false;
        PlaceFishOnJump(0f);

        if (!spawnSplash) { return; }

        SpawnSplash();                          // 既存のパーティクル（プレハブ）
        SpawnWaterSplash(MaxIntensityScale);    // 波紋＋水柱（魚の大きさで規模が変わる）
        PlaySplashSe();
    }

    /// <summary>
    /// 現在の演出設定でリピートを行うか（<see cref="repeatCount"/> が 1 以上）。
    /// 0 なら <see cref="CatchPhase.LowAngle"/> を通らず、従来どおりの流れになる。
    /// </summary>
    private bool UseJumpRepeat => repeatCount > MinRepeatCount;

    // ─── しぶき ───────────────────────────────────────────────

    /// <summary>
    /// 波紋＋水柱のしぶきをまく【新しいしぶきの唯一の発火点】。
    ///
    /// 規模は魚の基準サイズ（<see cref="Fish.BaseSizeCm"/>）を
    /// <see cref="splashMinSizeCm"/>〜<see cref="splashMaxSizeCm"/> で 0〜1 に正規化した
    /// 強度で決まる（個数・半径・スケールをその強度で線形補間する）。
    /// 演出中の魚が居なければ強度 0（＝いちばん小さい規模）で出す。
    /// </summary>
    /// <param name="intensityScale">強度に掛ける倍率（着水では小さくする）。</param>
    private void SpawnWaterSplash(float intensityScale)
    {
        float sizeCm = shownFish is { } fish ? fish.BaseSizeCm : 0f;
        float intensity = WaterSplashSpawner.Intensity01(sizeCm, splashMinSizeCm, splashMaxSizeCm)
                        * SEED.Mathf.Clamped(intensityScale, 0f, MaxIntensityScale);

        // 中心は跳びの真上（＝ウキの真下）、水面の高さはウキの高さで代表する
        var center = new SEED.Vector3(jumpStart.x, floatPosition.y, jumpStart.z);
        WaterSplashSpawner.Spawn(BuildSplashSettings(), center, floatPosition.y, intensity);
    }

    /// <summary>
    /// インスペクタの調整値から <see cref="WaterSplashSettings"/> を組み立てる
    /// 【設定の詰め替えの唯一の場所】。
    /// 生成側（<see cref="WaterSplashSpawner"/>）はシーンに置かない静的クラスなので、
    /// 調整値の持ち主はこのスクリプトになる。
    /// </summary>
    private WaterSplashSettings BuildSplashSettings() => new()
    {
        rippleActorPath = rippleActorPath,
        columnActorPath = columnActorPath,
        rippleCountMin  = splashRippleCountMin,
        rippleCountMax  = splashRippleCountMax,
        rippleRadiusMin = splashRippleRadiusMin,
        rippleRadiusMax = splashRippleRadiusMax,
        rippleScaleMin  = splashRippleScaleMin,
        rippleScaleMax  = splashRippleScaleMax,
        columnCountMin  = splashColumnCountMin,
        columnCountMax  = splashColumnCountMax,
        columnRadiusMin = splashColumnRadiusMin,
        columnRadiusMax = splashColumnRadiusMax,
        columnScaleMin  = splashColumnScaleMin,
        columnScaleMax  = splashColumnScaleMax,
        scaleJitter     = splashScaleJitter,
        surfaceOffsetY  = splashSurfaceOffsetY,
    };

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
        float displaySize = fish.DisplaySize;   // cm（書式は Fish.FormatSize に一元化）

        FishRecords.CatchRecordResult record =
            FishRecords.RecordCatch(displayName, displaySize, fish.SizeRank);
        float best = FishRecords.BestSize(displayName);

        // 図鑑画像は表示名でカタログを引く（見つからなければ空＝パネル側の代替画像）
        string imagePath = FishCatalog.TryGetByDisplayName(displayName, out FishCatalogEntry entry)
            ? entry.imagePath
            : string.Empty;

        ResultPanel.Show(resultPanelActorPath, new ResultPanel.ResultData(
            name: displayName,
            sizeText: Fish.FormatSize(displaySize),
            bestText: bestLabelPrefix + Fish.FormatSize(best),
            rankText: rankLabelPrefix + RankLabel(fish.SizeRank),
            // 配色は「見せる文字列」ではなく素のランク文字で引く（書式変更に強くするため）
            rankKey: fish.SizeRank,
            imagePath: imagePath,
            newRecord: record.NewBest,
            firstCatch: record.FirstCatch));
    }


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
    /// オイラー角（度）を軸ごとの<b>最短回り</b>で補間する
    /// 【カメラ移行の回転補間の唯一の実装】。
    /// 350°→10° のような巻き戻りでも逆回りしない。
    /// </summary>
    /// <param name="from">開始の回転（度）。</param>
    /// <param name="to">終了の回転（度）。</param>
    /// <param name="t">補間係数（0〜1）。</param>
    private static SEED.Vector3 LerpAngles(SEED.Vector3 from, SEED.Vector3 to, float t)
    {
        float k = SEED.Mathf.Clamped01(t);
        return new SEED.Vector3(
            from.x + ShortestAngleDelta(from.x, to.x) * k,
            from.y + ShortestAngleDelta(from.y, to.y) * k,
            from.z + ShortestAngleDelta(from.z, to.z) * k);
    }

    /// <summary>角度 from→to の最短回りの差分（度、-180〜+180）。</summary>
    /// <param name="from">開始角（度）。</param>
    /// <param name="to">終了角（度）。</param>
    private static float ShortestAngleDelta(float from, float to)
        => SEED.Mathf.Repeat(to - from + HalfTurnDegrees, FullTurnDegrees) - HalfTurnDegrees;

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
