// ============================================================================
//  TutorialWindow.cs
//  チュートリアル説明窓（ミニキャラ・吹き出し・本文・送りマーク）の「表示」だけを担当する。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// チュートリアル説明窓の表示担当コンポーネント。説明窓アクタ（Actor2D）のルートに付ける。
///
/// 【責務】
///  1. 窓全体の表示／非表示（ShowAtAnchor / ShowAboveTarget / Hide）
///  2. 表示位置の適用（位置アンカーアクタの写し取り・ワールド対象への追従）
///  3. 出現／退場の演出（吹き出し・本文・送りマーク・ミニキャラの拡大縮小と回転）
///  4. 本文の 1 文字ずつの表示（SetText / CompleteText / IsTextComplete）
///  5. 送りマークの点滅（本文を出し切っている間だけ）
///
/// 「次に何を説明するか」「入力で送るか」といった進行判断は持たない。
/// それは <see cref="TutorialDirector"/> の責務（単一責任）。
///
/// 【時間軸】
/// チュートリアルはゲーム時間を止めた（Time.Scale = 0）状態でも動く必要があるため、
/// 文字送りも点滅も追従も演出も<b>すべて Time.UnscaledDeltaTime</b> で進める。
///
/// 【位置の決め方（データドリブン）】
/// 画面固定の位置は数値ではなく「位置アンカーアクタ」で与える
/// （<see cref="TutorialStep.anchorTarget"/>）。空の 2D アクタを画面上の狙った場所に置き、
/// その CanvasTransform（アンカー・ピボット・位置・拡大率・回転）を丸ごと窓へ写す。
/// 位置調整はエディタでアクタを動かすだけで済み、スクリプトの変更は要らない。
///
/// 【表示切替の実装方針】
/// アクター単位の表示 ON/OFF を行うスクリプト API は無いので、
/// 参照している Sprite / Text のアルファを 0 にして隠す（DialogueWindow と同じ方式）。
/// 元の色はシーンで設定された値を初回に控え、表示時にそれへ戻す。
/// 拡大縮小・回転は各パーツの CanvasTransform を毎フレーム書き換えて表現する。
///
/// 【シーン側の設定】
/// 参照フィールドには同じ説明窓アクタ配下の子を指定する
/// （Sprite / Text は「子名|スロット名」、CanvasTransform は「子名」）。
/// </summary>
public class TutorialWindow : SEEDScript
{
    // ─── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>完全に透明にするアルファ倍率。</summary>
    private const float AlphaScaleHidden = 0f;

    /// <summary>元の色をそのまま使うアルファ倍率。</summary>
    private const float AlphaScaleVisible = 1f;

    /// <summary>1 文字あたりの表示間隔（秒）の既定値。</summary>
    private const float DefaultCharInterval = 0.03f;

    /// <summary>文字送り効果音の既定パス（吹き出しの「ぴこぴこ」音）。</summary>
    private const string DefaultMessageSePath = "assets://mainGame/audios/message.mp3";

    /// <summary>送りマークの点滅周期（秒）の既定値。</summary>
    private const float DefaultArrowBlinkPeriod = 0.8f;

    /// <summary>点滅周期がこれ以下なら点滅させない（0 除算回避）。</summary>
    private const float MinBlinkPeriod = 0.0001f;

    /// <summary>送りマークの点滅で取り得るアルファ倍率の下限。</summary>
    private const float ArrowBlinkMin = 0.15f;

    /// <summary>送りマークの点滅で取り得るアルファ倍率の上限。</summary>
    private const float ArrowBlinkMax = 1f;

    /// <summary>点滅周期を往路の長さへ直す係数（1 周期 = 往復）。</summary>
    private const float BlinkHalfPeriodRatio = 0.5f;

    /// <summary>出現演出の所要秒（既定）。</summary>
    private const float DefaultAppearSeconds = 0.35f;

    /// <summary>吹き出しに対して中身（本文・送りマーク）を遅らせる秒数（既定）。</summary>
    private const float DefaultContentDelaySeconds = 0.08f;

    /// <summary>吹き出しに対してミニキャラを遅らせる秒数（既定）。</summary>
    private const float DefaultCharaDelaySeconds = 0.05f;

    /// <summary>ミニキャラの出現開始時の回転角（度）。ここから 0 へ戻りながら現れる。</summary>
    private const float DefaultCharaAppearRotation = -25f;

    /// <summary>退場演出の所要秒（既定。出現より短くして待たされ感を無くす）。</summary>
    private const float DefaultDisappearSeconds = 0.18f;

    /// <summary>説明中にミニキャラが左右へ揺れる振れ幅（度・既定）。</summary>
    private const float DefaultSwayAmplitudeDegrees = 3f;

    /// <summary>説明中にミニキャラが 1 往復する秒数（既定）。</summary>
    private const float DefaultSwayPeriodSeconds = 2.5f;

    /// <summary>演出の進捗が完了とみなせる値（0〜1 の 1）。</summary>
    private const float ProgressComplete = 1f;

    /// <summary>拡大率 0（＝完全に潰れた状態）。</summary>
    private const float ScaleZero = 0f;

    /// <summary>回転なし（度）。</summary>
    private const float RotationNone = 0f;

    /// <summary>キャンバスの既定の幅（px）。画面外クランプの基準に使う。</summary>
    private const float DefaultCanvasWidth = 1280f;

    /// <summary>キャンバスの既定の高さ（px）。画面外クランプの基準に使う。</summary>
    private const float DefaultCanvasHeight = 720f;

    /// <summary>キャンバス半分を求める係数（中央原点なので幅・高さの半分が端になる）。</summary>
    private const float CanvasHalf = 0.5f;

    /// <summary>
    /// AboveTarget で使うアンカー／ピボットの正規化値（0.5 = 中央）。
    /// Camera.WorldToCanvas は「画面中央が原点」の座標を返すため、
    /// 窓側も中央基準にそろえないと射影結果と位置がずれる。
    /// </summary>
    private const float CenterNormalized = 0.5f;

    /// <summary>カメラ前方距離がこの値以下なら「カメラの背後」とみなす。</summary>
    private const float BehindCameraDepth = 0f;

    /// <summary>カメラ背後のとき、方向を保ったまま画面外へ押し出す倍率（クランプで端に張り付く）。</summary>
    private const float BehindPushFactor = 1000f;

    /// <summary>位置アンカーが「同じ場所」かを判定する許容誤差（px・正規化値）。</summary>
    private const float AnchorSameEpsilon = 0.01f;

    /// <summary>空文字（未設定）。</summary>
    private const string EmptyText = "";

    // ─── 表示演出の段階 ──────────────────────────────────────

    /// <summary>説明窓の見た目の段階（出現・表示・退場のどこに居るか）。</summary>
    private enum WindowVisualState
    {
        /// <summary>完全に隠れている（更新も止める）。</summary>
        Hidden,

        /// <summary>出現演出の再生中。</summary>
        Appearing,

        /// <summary>出現し切って読ませている最中（ミニキャラだけ揺れる）。</summary>
        Shown,

        /// <summary>退場演出の再生中（出現の逆再生）。</summary>
        Disappearing,
    }

    // ─── インスペクタ公開フィールド（参照: 見た目）───────────

    /// <summary>ミニキャラのスプライト。</summary>
    [Header("参照"), SerializeField(Label = "ミニキャラ", Tooltip = "ミニキャラの Sprite（子名|Sprite）")]
    public SEED.Sprite? charaSprite;

    /// <summary>ミニ吹き出しのスプライト。</summary>
    [SerializeField(Label = "吹き出し", Tooltip = "吹き出しの Sprite（子名|Sprite）")]
    public SEED.Sprite? balloonSprite;

    /// <summary>説明本文の Text（枠あり・自動折り返し）。</summary>
    [SerializeField(Label = "本文テキスト", Tooltip = "説明本文の Text（子名|Text）")]
    public SEED.Text? bodyText;

    /// <summary>送りマーク（本文を出し切ったときだけ点滅表示する）。</summary>
    [SerializeField(Label = "送りマーク", Tooltip = "本文を出し切ったときに点滅する Sprite（子名|Sprite）")]
    public SEED.Sprite? nextArrowSprite;

    /// <summary>ワールド座標を画面へ射影するカメラ（AboveTarget の追従に使う）。</summary>
    [SerializeField(Label = "射影カメラ", Tooltip = "対象アクタの追従に使うカメラ（MainCamera）")]
    public SEED.Camera? projectionCamera;

    // ─── インスペクタ公開フィールド（参照: 演出用の変形）─────

    /// <summary>
    /// 吹き出しの CanvasTransform（出現演出の拡大縮小に使う）。
    /// Sprite スロットの参照だけでは位置・拡大率を触れないため、変形は別に受け取る。
    /// </summary>
    [Header("参照（演出用の変形）")]
    [SerializeField(Label = "吹き出しの変形", Tooltip = "吹き出し子アクタの CanvasTransform（子名）")]
    public SEED.CanvasTransform? balloonTransform;

    /// <summary>ミニキャラの CanvasTransform（拡大縮小と回転・揺れに使う）。</summary>
    [SerializeField(Label = "ミニキャラの変形", Tooltip = "ミニキャラ子アクタの CanvasTransform（子名）")]
    public SEED.CanvasTransform? charaTransform;

    /// <summary>本文テキストの CanvasTransform（出現演出の拡大縮小に使う）。</summary>
    [SerializeField(Label = "本文の変形", Tooltip = "本文子アクタの CanvasTransform（子名）")]
    public SEED.CanvasTransform? bodyTransform;

    /// <summary>送りマークの CanvasTransform（出現演出の拡大縮小に使う）。</summary>
    [SerializeField(Label = "送りマークの変形", Tooltip = "送りマーク子アクタの CanvasTransform（子名）")]
    public SEED.CanvasTransform? arrowTransform;

    // ─── インスペクタ公開フィールド（値）─────────────────────

    /// <summary>1 文字あたりの表示間隔（秒）。0 以下なら即座に全文表示する。</summary>
    [Header("文字送り"), SerializeField(Label = "文字送り間隔(秒)", Tooltip = "1 文字を表示する間隔。0 で即時全文表示")]
    public float charInterval = DefaultCharInterval;

    /// <summary>送りマークの点滅周期（秒）。</summary>
    [SerializeField(Label = "送りマーク点滅周期(秒)", Tooltip = "送りマークが 1 往復するのに掛かる秒数")]
    public float arrowBlinkPeriod = DefaultArrowBlinkPeriod;

    /// <summary>
    /// 文字送り中に鳴らす効果音のアセットパス（空なら鳴らさない）。
    /// 実際の再生は <see cref="TypewriterText"/> が間隔を見て行う（窓は設定を渡すだけ）。
    /// </summary>
    [SerializeField(Label = "文字送りSE", Tooltip = "文字送り中に鳴らす効果音。空なら鳴らさない")]
    [AssetReference("mp3", "wav", "ogg")]
    public string messageSePath = DefaultMessageSePath;

    /// <summary>文字送り効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "文字送りSEの音量", Tooltip = "文字送り効果音の音量（0〜1）")]
    public float messageSeVolume = TypewriterText.DefaultSeVolume;

    /// <summary>
    /// 文字送り効果音の再生間隔（秒）。1 文字ごとではなくこの間隔で間引く。
    /// 実際の間隔は <see cref="messageSeLength"/> との大きい方（音が重ならないため）。
    /// </summary>
    [SerializeField(Label = "文字送りSEの間隔(秒)", Tooltip = "効果音を鳴らす間隔。音の実尺より短くしても重ねては鳴らさない")]
    public float messageSeInterval = TypewriterText.DefaultSeIntervalSeconds;

    /// <summary>
    /// 文字送り効果音の実尺（秒）。前の音が鳴り終わる前に次を鳴らさないための下限。
    /// 音を差し替えたら、その音の長さをここへ入れる。
    /// </summary>
    [SerializeField(Label = "文字送りSEの長さ(秒)", Tooltip = "効果音の実尺。この時間が経つまで次を鳴らさない（重複防止）")]
    public float messageSeLength = TypewriterText.DefaultSeLengthSeconds;

    /// <summary>本文に使うフォント（.ttf / .otf）。空なら Text 側の設定をそのまま使う。</summary>
    [SerializeField(Label = "フォント", Tooltip = "本文に使うフォント。空なら Text の設定のまま")]
    [AssetReference("ttf", "otf")]
    public string fontPath = EmptyText;

    /// <summary>本文のインライン画像に使うアイコンセット（.icons）。空なら Text 側の設定のまま。</summary>
    [SerializeField(Label = "アイコンセット", Tooltip = "[icon:名前] を解決する .icons ファイル。空なら Text の設定のまま")]
    [AssetReference("icons")]
    public string iconSetPath = EmptyText;

    /// <summary>
    /// 本文の枠幅（px）。<b>0 なら Text コンポーネント側のシーン設定をそのまま使う</b>。
    /// 素材に合わせて枠を作り込むのはシーン側の仕事なので、既定は 0（＝触らない）。
    /// </summary>
    [Header("本文の枠"), SerializeField(Label = "枠の幅(px)", Tooltip = "本文の折り返し幅。0 なら Text 側のシーン設定を使う")]
    public float boxWidth = 0f;

    /// <summary>本文の枠の最小高さ（px）。枠の幅が 0 のときは使わない。</summary>
    [SerializeField(Label = "枠の最小高さ(px)", Tooltip = "本文の枠の最小高さ（枠の幅が 0 のときは無視）")]
    public float boxHeight = 0f;

    /// <summary>画面外クランプに使うキャンバスの幅（px）。</summary>
    [Header("画面外クランプ"), SerializeField(Label = "キャンバス幅(px)", Tooltip = "追従時の画面内クランプに使う幅")]
    public float canvasWidth = DefaultCanvasWidth;

    /// <summary>画面外クランプに使うキャンバスの高さ（px）。</summary>
    [SerializeField(Label = "キャンバス高さ(px)", Tooltip = "追従時の画面内クランプに使う高さ")]
    public float canvasHeight = DefaultCanvasHeight;

    /// <summary>画面端から内側へ確保する余白（px）。窓が画面外へはみ出さないようにする。</summary>
    [SerializeField(Label = "画面端の余白(px)", Tooltip = "追従時に画面端から内側へ確保する余白")]
    public float screenMargin = 200f;

    /// <summary>追従時に対象の何メートル上を狙うか（ワールド単位）。</summary>
    [Header("追従"), SerializeField(Label = "対象の上のオフセット(m)", Tooltip = "追従時に対象の何メートル上へ出すか")]
    public float aboveTargetWorldOffsetY = 1.5f;

    /// <summary>追従時にさらに画面上でどれだけ持ち上げるか（px・負で上）。</summary>
    [SerializeField(Label = "追従の画面オフセットY(px)", Tooltip = "追従時に画面上でさらにずらす量（負で上）")]
    public float aboveTargetCanvasOffsetY = -80f;

    // ─── インスペクタ公開フィールド（出現・退場の演出）───────

    /// <summary>吹き出しが 0 倍から等倍になるまでの秒数（実時間）。</summary>
    [Header("出現演出"), SerializeField(Label = "出現の秒数", Tooltip = "吹き出しが 0 倍から等倍になるまでの秒数")]
    public float appearSeconds = DefaultAppearSeconds;

    /// <summary>吹き出しより中身（本文・送りマーク）を遅らせる秒数。</summary>
    [SerializeField(Label = "中身の遅れ(秒)", Tooltip = "吹き出しより本文・送りマークを遅らせる秒数")]
    public float contentDelaySeconds = DefaultContentDelaySeconds;

    /// <summary>吹き出しよりミニキャラを遅らせる秒数。</summary>
    [SerializeField(Label = "キャラの遅れ(秒)", Tooltip = "吹き出しよりミニキャラを遅らせる秒数")]
    public float charaDelaySeconds = DefaultCharaDelaySeconds;

    /// <summary>ミニキャラが現れ始めるときの回転角（度）。ここから 0 へ戻りながら出る。</summary>
    [SerializeField(Label = "キャラの開始角(度)", Tooltip = "ミニキャラが現れ始めるときの傾き（度）")]
    public float charaAppearRotation = DefaultCharaAppearRotation;

    /// <summary>退場演出の秒数（出現の逆再生）。</summary>
    [SerializeField(Label = "退場の秒数", Tooltip = "隠すときに縮んで消えるまでの秒数")]
    public float disappearSeconds = DefaultDisappearSeconds;

    /// <summary>説明中にミニキャラが左右へ揺れる振れ幅（度）。0 で揺らさない。</summary>
    [SerializeField(Label = "キャラの揺れ幅(度)", Tooltip = "説明中にミニキャラが左右へ傾く角度。0 で揺らさない")]
    public float swayAmplitudeDegrees = DefaultSwayAmplitudeDegrees;

    /// <summary>説明中にミニキャラが 1 往復する秒数。</summary>
    [SerializeField(Label = "キャラの揺れ周期(秒)", Tooltip = "ミニキャラが 1 往復するのに掛かる秒数")]
    public float swayPeriodSeconds = DefaultSwayPeriodSeconds;

    // ─── 内部状態: 元の色 ────────────────────────────────────

    // 元の色は「部品ごとに」控える。窓全体で 1 つのフラグにすると、
    // 参照がまだ解決されていない時点で 1 度呼ばれただけで「控えた」ことになってしまい、
    // 以降ずっと真っ黒・完全透明（既定色）で表示される（下の CaptureBaseColors 参照）。

    /// <summary>ミニキャラの元の色を控えたか。</summary>
    private bool charaColorCaptured;

    /// <summary>吹き出しの元の色を控えたか。</summary>
    private bool balloonColorCaptured;

    /// <summary>本文テキストの元の色を控えたか。</summary>
    private bool bodyColorCaptured;

    /// <summary>送りマークの元の色を控えたか。</summary>
    private bool arrowColorCaptured;

    /// <summary>ミニキャラの元の色。</summary>
    private SEED.Color charaBaseColor;

    /// <summary>吹き出しの元の色。</summary>
    private SEED.Color balloonBaseColor;

    /// <summary>本文テキストの元の色。</summary>
    private SEED.Color bodyBaseColor;

    /// <summary>送りマークの元の色。</summary>
    private SEED.Color arrowBaseColor;

    // ─── 内部状態: 元の変形 ──────────────────────────────────

    // 拡大率・回転は毎フレーム上書きするため、シーンで設定された値を必ず控えてから触る。
    // 控える前に書き換えると、ホットリロードのたびに素材の基準サイズが失われてしまう。

    /// <summary>吹き出しの元の拡大率を控えたか。</summary>
    private bool balloonTransformCaptured;

    /// <summary>ミニキャラの元の拡大率・回転を控えたか。</summary>
    private bool charaTransformCaptured;

    /// <summary>本文の元の拡大率を控えたか。</summary>
    private bool bodyTransformCaptured;

    /// <summary>送りマークの元の拡大率を控えたか。</summary>
    private bool arrowTransformCaptured;

    /// <summary>吹き出しの元の拡大率。</summary>
    private SEED.Vector2 balloonBaseScale = SEED.Vector2.One;

    /// <summary>ミニキャラの元の拡大率。</summary>
    private SEED.Vector2 charaBaseScale = SEED.Vector2.One;

    /// <summary>本文の元の拡大率。</summary>
    private SEED.Vector2 bodyBaseScale = SEED.Vector2.One;

    /// <summary>送りマークの元の拡大率。</summary>
    private SEED.Vector2 arrowBaseScale = SEED.Vector2.One;

    /// <summary>ミニキャラの元の回転角（度）。揺れはここを中心に振れる。</summary>
    private float charaBaseRotation = RotationNone;

    // ─── 内部状態: 表示と位置 ────────────────────────────────

    /// <summary>窓を表示中か（アルファを戻してあるか）。</summary>
    private bool visible;

    /// <summary>現在の見た目の段階。</summary>
    private WindowVisualState visualState = WindowVisualState.Hidden;

    /// <summary>出現・退場演出の経過秒（実時間）。</summary>
    private float animTimer;

    /// <summary>ミニキャラの揺れの経過秒（実時間）。</summary>
    private float swayTimer;

    /// <summary>現在の配置モード（表示中のみ意味を持つ）。</summary>
    private TutorialAnchorMode anchorMode = TutorialAnchorMode.ScreenFixed;

    /// <summary>直近に写し取った位置アンカーの位置（同じ場所への再表示を見分けるために控える）。</summary>
    private SEED.Vector2 appliedAnchorPosition = SEED.Vector2.Zero;

    /// <summary>直近に写し取った位置アンカーのアンカー値（同上）。</summary>
    private SEED.Vector2 appliedAnchorAnchor = SEED.Vector2.Zero;

    /// <summary>位置アンカーを 1 度でも写し取ったか（初回は必ず演出を再生する）。</summary>
    private bool anchorApplied;

    /// <summary>追従対象（配置モードが AboveTarget のときだけ使う）。</summary>
    private SEED.Transform followTarget;

    /// <summary>文字送りの進行（インライン画像記法を 1 文字として扱う共通ヘルパー）。</summary>
    private readonly TypewriterText writer = new();

    /// <summary>点滅用の経過時間（秒・実時間）。</summary>
    private float blinkTimer;

    // ─── 公開プロパティ ──────────────────────────────────────

    /// <summary>本文をすべて表示し終えているか（送り待ちの状態か）。</summary>
    public bool IsTextComplete => writer.IsComplete;

    /// <summary>窓を表示中か（退場演出の最中も true）。</summary>
    public bool IsVisible => visible;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 初期化。元の色と変形を控え、フォント・アイコンセット・本文の枠を適用してから隠す。
    /// </summary>
    public override void OnStart()
    {
        CaptureBaseColors();
        CaptureBaseTransforms();
        ApplyTextStyle();

        // 自分より先に TutorialDirector の OnStart が走って表示要求済みのことがある
        // （スクリプトの OnStart 順は保証されない）。
        // そこで無条件に隠さず、「まだ表示要求が来ていないときだけ」隠す。
        // 表示要求済みなら、参照が解決された今の状態で色と本文を貼り直す。
        if (visible)
        {
            ApplyVisibility();
            ApplyBodyContent();
            return;
        }

        HideImmediate();
    }

    /// <summary>
    /// 毎フレーム、追従・文字送り・送りマークの点滅・出現演出を進める。
    /// ゲーム時間が止まっていても動く必要があるので、必ず実時間で進める。
    /// </summary>
    /// <param name="ctx">フレーム情報（ここでは使わず Time.UnscaledDeltaTime を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 完全に隠れている間は演出も文字送りも進めない。
        // ただし色の適用だけは試み続ける: 参照（子の Text / Sprite）は自分の OnStart の
        // 直前に解決されるため、TutorialDirector から先に隠された場合は
        // 「まだ参照が無効で alpha を 0 にできなかった」ことがある。
        // 一度も表示要求が来ないまま（＝開始の猶予中など）だと、シーンに置いた
        // 初期テキストが画面に残り続けてしまうので、毎フレーム貼り直す。
        if (visualState == WindowVisualState.Hidden)
        {
            CaptureBaseColors();
            CaptureBaseTransforms();
            ApplyVisibility();
            CollapseParts();
            return;
        }

        // 参照が後から有効になった場合に備えて、未取得の部品だけ控える
        CaptureBaseColors();
        CaptureBaseTransforms();

        float unscaledDelta = SEED.Time.UnscaledDeltaTime;
        animTimer += unscaledDelta;
        swayTimer += unscaledDelta;

        UpdateFollow();
        if (writer.Advance(unscaledDelta, charInterval)) { ApplyBodyContent(); }
        UpdateArrow(unscaledDelta);
        UpdateVisual();
    }

    // ─── 公開メソッド: 表示・非表示 ─────────────────────────

    /// <summary>
    /// 位置アンカーアクタの上に窓を重ねて表示する【画面固定表示の唯一の入口】。
    ///
    /// アンカーの CanvasTransform（アンカー・ピボット・位置・拡大率・回転）を
    /// そのまま自分へ写すので、画面のどこに出すかはシーン側のアクタ配置で決まる。
    /// </summary>
    /// <param name="anchor">位置アンカーアクタの CanvasTransform（無効なら現在位置のまま出す）。</param>
    public void ShowAtAnchor(SEED.CanvasTransform anchor)
    {
        // 直前と同じ場所へ続けて出す場合は演出をやり直さない
        // （説明が続く手順ごとに窓が跳ね直すと、読んでいる側の目が疲れる）
        bool samePlace = visualState == WindowVisualState.Shown
                      && anchorMode == TutorialAnchorMode.ScreenFixed
                      && IsSameAnchor(anchor);

        anchorMode   = TutorialAnchorMode.ScreenFixed;
        followTarget = default;

        ApplyAnchor(anchor);

        if (samePlace) { Show(); return; }
        BeginAppear();
    }

    /// <summary>
    /// 指定アクタの少し上へ追従する形で窓を表示する。
    ///
    /// 位置は毎フレーム <c>Camera.WorldToCanvas</c> で引き直す。
    /// 対象がカメラの背後にある場合は方向を保ったまま画面端へクランプする
    /// （背後の点は射影結果の X / Y が意味を持たないため、符号を反転して押し出す）。
    /// </summary>
    /// <param name="target">追従する対象の Transform。無効なら現在位置のまま表示する。</param>
    public void ShowAboveTarget(SEED.Transform target)
    {
        if (!target.IsValid)
        {
            SEED.Debug.LogWarning("[TutorialWindow] 追従対象が未設定・無効です。現在の位置に表示します。");
            anchorMode = TutorialAnchorMode.ScreenFixed;
            BeginAppear();
            return;
        }

        bool samePlace = visualState == WindowVisualState.Shown
                      && anchorMode == TutorialAnchorMode.AboveTarget;

        anchorMode   = TutorialAnchorMode.AboveTarget;
        followTarget = target;

        // WorldToCanvas は画面中央原点で返るので、窓側も中央基準にそろえる
        ApplyCenterAnchor();
        UpdateFollow();       // 表示した最初のフレームから正しい位置に出す

        if (samePlace) { Show(); return; }
        BeginAppear();
    }

    /// <summary>
    /// 表示する本文を差し替え、文字送りを最初からやり直す。
    /// </summary>
    /// <param name="text">説明文（インライン画像記法を含んでよい）。</param>
    public void SetText(string text)
    {
        CaptureBaseColors();
        ApplyTypewriterSeSettings();
        writer.Begin(text);
        ApplyBodyContent();
        blinkTimer = 0f;
    }

    /// <summary>本文を最後まで一気に表示する（送り入力による全文表示に使う）。</summary>
    public void CompleteText()
    {
        if (writer.Complete()) { ApplyBodyContent(); }
    }

    /// <summary>
    /// 文字送り効果音の設定を <see cref="writer"/> へ渡す【SE 設定の唯一の受け渡し点】。
    /// 本文を差し替えるたびに呼ぶので、インスペクタでの変更がその場で効く。
    /// </summary>
    private void ApplyTypewriterSeSettings()
    {
        writer.SePath           = messageSePath;
        writer.SeVolume         = messageSeVolume;
        writer.SeIntervalSeconds = messageSeInterval;
        writer.SeLengthSeconds   = messageSeLength;
    }

    /// <summary>
    /// 窓を隠す【非表示要求の唯一の入口】。
    /// 表示中なら退場演出（出現の逆再生）を始め、既に隠れているなら即座に隠す。
    /// </summary>
    public void Hide()
    {
        CaptureBaseColors();
        CaptureBaseTransforms();

        if (visualState == WindowVisualState.Hidden) { HideImmediate(); return; }

        visualState = WindowVisualState.Disappearing;
        animTimer   = 0f;
    }

    // ─── 内部処理: 表示状態と演出 ───────────────────────────

    /// <summary>出現演出を最初から再生する【出現演出の唯一の入口】。</summary>
    private void BeginAppear()
    {
        CaptureBaseColors();
        CaptureBaseTransforms();

        visualState = WindowVisualState.Appearing;
        animTimer   = 0f;
        swayTimer   = 0f;

        Show();
        UpdateVisual();   // 出だしのフレームから 0 倍で始める（1 フレーム等倍で見える事故を防ぐ）
    }

    /// <summary>窓を表示状態にする（各パーツの色を元の値へ戻す）。</summary>
    private void Show()
    {
        CaptureBaseColors();
        visible = true;
        if (visualState == WindowVisualState.Hidden) { visualState = WindowVisualState.Shown; }
        ApplyVisibility();
    }

    /// <summary>演出を挟まずに即座に隠す（初期化時・完全に隠れているときの再要求用）。</summary>
    private void HideImmediate()
    {
        visible     = false;
        visualState = WindowVisualState.Hidden;
        animTimer   = 0f;
        ApplyVisibility();

        // 潰しておかないと、一度も表示していない窓が「シーンに置いたままの大きさ」で
        // 見えてしまう（本文 Text は色の alpha だけでは消えない）。
        CollapseParts();
    }

    /// <summary>
    /// 窓のパーツを拡大率 0 へ潰す【「見えなくする」ことの唯一の実装】。
    /// 退場演出の終わりと、表示前の初期状態の両方から使う。
    /// </summary>
    private void CollapseParts()
    {
        ApplyPartScale(balloonTransform, balloonBaseScale, ScaleZero);
        ApplyPartScale(bodyTransform,    bodyBaseScale,    ScaleZero);
        ApplyPartScale(arrowTransform,   arrowBaseScale,   ScaleZero);
        ApplyPartScale(charaTransform,   charaBaseScale,   ScaleZero);
    }

    /// <summary>
    /// 出現・表示・退場の各段階に応じて、パーツの拡大率と回転を更新する
    /// 【演出計算の唯一の実装】。
    /// </summary>
    private void UpdateVisual()
    {
        switch (visualState)
        {
            case WindowVisualState.Appearing:
                UpdateAppearing();
                break;

            case WindowVisualState.Shown:
                UpdateShown();
                break;

            case WindowVisualState.Disappearing:
                UpdateDisappearing();
                break;
        }
    }

    /// <summary>
    /// 出現演出の 1 フレームぶん。
    /// 吹き出し → 中身 → ミニキャラの順に、少しずつ遅らせて立ち上げる。
    /// </summary>
    private void UpdateAppearing()
    {
        // 吹き出しと中身は「行き過ぎて戻る」曲線でポンと出す
        float balloonRate = Easing.OutBack(Easing.Progress01(animTimer, appearSeconds));
        float contentRate = Easing.OutBack(Easing.Progress01(animTimer - contentDelaySeconds, appearSeconds));

        // ミニキャラは行き過ぎない曲線で、傾きを戻しながら立ち上がる
        float charaProgress = Easing.OutCubic(Easing.Progress01(animTimer - charaDelaySeconds, appearSeconds));

        ApplyPartScale(balloonTransform, balloonBaseScale, balloonRate);
        ApplyPartScale(bodyTransform,    bodyBaseScale,    contentRate);
        ApplyPartScale(arrowTransform,   arrowBaseScale,   contentRate);
        ApplyPartScale(charaTransform,   charaBaseScale,   charaProgress);
        ApplyCharaRotation(SEED.Mathf.LerpUnclamped(
            charaBaseRotation + charaAppearRotation, charaBaseRotation, charaProgress));

        // いちばん遅く始まるパーツが終わったら「表示中」へ移る
        float lastDelay = SEED.Mathf.Max(contentDelaySeconds, charaDelaySeconds);
        if (animTimer >= appearSeconds + SEED.Mathf.Max(lastDelay, 0f))
        {
            visualState = WindowVisualState.Shown;
            swayTimer   = 0f;
        }
    }

    /// <summary>説明を読ませている最中の 1 フレームぶん（ミニキャラだけゆっくり揺らす）。</summary>
    private void UpdateShown()
    {
        ApplyPartScale(balloonTransform, balloonBaseScale, ProgressComplete);
        ApplyPartScale(bodyTransform,    bodyBaseScale,    ProgressComplete);
        ApplyPartScale(arrowTransform,   arrowBaseScale,   ProgressComplete);
        ApplyPartScale(charaTransform,   charaBaseScale,   ProgressComplete);

        float sway = Easing.PingPongSigned(swayTimer, swayPeriodSeconds) * swayAmplitudeDegrees;
        ApplyCharaRotation(charaBaseRotation + sway);
    }

    /// <summary>退場演出の 1 フレームぶん（出現の逆再生。終わったら完全に隠す）。</summary>
    private void UpdateDisappearing()
    {
        // 残り率 1 → 0。InCubic で「すっと吸い込まれる」縮み方にする
        float remain = ProgressComplete - Easing.Progress01(animTimer, disappearSeconds);
        float rate   = Easing.InCubic(remain);

        ApplyPartScale(balloonTransform, balloonBaseScale, rate);
        ApplyPartScale(bodyTransform,    bodyBaseScale,    rate);
        ApplyPartScale(arrowTransform,   arrowBaseScale,   rate);
        ApplyPartScale(charaTransform,   charaBaseScale,   rate);
        ApplyCharaRotation(SEED.Mathf.LerpUnclamped(
            charaBaseRotation + charaAppearRotation, charaBaseRotation, rate));

        if (animTimer >= disappearSeconds)
        {
            // 次に出すときに等倍のまま一瞬見えないよう、潰した状態で隠す
            HideImmediate();
        }
    }

    /// <summary>
    /// パーツの拡大率を「シーンで設定された元の拡大率 × 演出の倍率」で書き換える。
    /// </summary>
    /// <param name="part">対象の CanvasTransform（未設定・無効なら何もしない）。</param>
    /// <param name="baseScale">シーンで設定された元の拡大率。</param>
    /// <param name="rate">演出の倍率（0 で消滅・1 で等倍。OutBack では 1 を少し超える）。</param>
    private static void ApplyPartScale(SEED.CanvasTransform? part, SEED.Vector2 baseScale, float rate)
    {
        if (part is not { } ct || !ct.IsValid) { return; }
        ct.Scale = new SEED.Vector2(baseScale.x * rate, baseScale.y * rate);
    }

    /// <summary>ミニキャラの回転角（度）を書き換える。</summary>
    /// <param name="degrees">適用する角度（度）。</param>
    private void ApplyCharaRotation(float degrees)
    {
        if (charaTransform is not { } ct || !ct.IsValid) { return; }
        ct.Rotation = degrees;
    }

    /// <summary>
    /// シーンで設定された各パーツの色を初回だけ控える。
    ///
    /// OnStart だけでは足りない。参照元（TutorialDirector）の処理が自分の OnStart より
    /// 先に走って Hide() を呼ぶことがあり得るため、公開 API の入口でも呼んで
    /// 「必ず控えてから触る」ようにしている（DialogueWindow と同じ理由）。
    /// </summary>
    private void CaptureBaseColors()
    {
        // 「参照が有効になった最初の 1 回」を部品ごとに判定する。
        // 参照フィールドの解決は自分の OnStart の直前に行われるため、
        // それより先に TutorialDirector から呼ばれた場合は参照がまだ無効で、
        // ここで一括に「控え済み」にしてしまうと元の色を永久に取り込めない。
        if (!charaColorCaptured   && charaSprite     is { } chara   && chara.IsValid)
        {
            charaBaseColor     = chara.Color;
            charaColorCaptured = true;
        }
        if (!balloonColorCaptured && balloonSprite   is { } balloon && balloon.IsValid)
        {
            balloonBaseColor     = balloon.Color;
            balloonColorCaptured = true;
        }
        if (!bodyColorCaptured    && bodyText        is { } body    && body.IsValid)
        {
            bodyBaseColor     = body.Color;
            bodyColorCaptured = true;
        }
        if (!arrowColorCaptured   && nextArrowSprite is { } arrow   && arrow.IsValid)
        {
            arrowBaseColor     = arrow.Color;
            arrowColorCaptured = true;
        }
    }

    /// <summary>
    /// シーンで設定された各パーツの拡大率・回転を初回だけ控える。
    /// 色と同じ理由（参照の解決タイミングが読めない）で、部品ごとに判定する。
    /// </summary>
    private void CaptureBaseTransforms()
    {
        if (!balloonTransformCaptured && balloonTransform is { } balloon && balloon.IsValid)
        {
            balloonBaseScale         = balloon.Scale;
            balloonTransformCaptured = true;
        }
        if (!bodyTransformCaptured    && bodyTransform    is { } body    && body.IsValid)
        {
            bodyBaseScale         = body.Scale;
            bodyTransformCaptured = true;
        }
        if (!arrowTransformCaptured   && arrowTransform   is { } arrow   && arrow.IsValid)
        {
            arrowBaseScale         = arrow.Scale;
            arrowTransformCaptured = true;
        }
        if (!charaTransformCaptured   && charaTransform   is { } chara   && chara.IsValid)
        {
            charaBaseScale         = chara.Scale;
            charaBaseRotation      = chara.Rotation;
            charaTransformCaptured = true;
        }
    }

    /// <summary>
    /// 現在の表示状態を各パーツの色へ反映する。
    /// 送りマークは「本文完了時のみ表示」なのでここでは必ず消し、
    /// <see cref="UpdateArrow"/> が毎フレーム上書きする。
    /// </summary>
    private void ApplyVisibility()
    {
        float alphaScale = visible ? AlphaScaleVisible : AlphaScaleHidden;

        if (charaSprite     is { } chara   && chara.IsValid)
        {
            chara.Color = charaBaseColor.WithAlpha(charaBaseColor.a * alphaScale);
        }
        if (balloonSprite   is { } balloon && balloon.IsValid)
        {
            balloon.Color = balloonBaseColor.WithAlpha(balloonBaseColor.a * alphaScale);
        }
        if (bodyText        is { } body    && body.IsValid)
        {
            body.Color = bodyBaseColor.WithAlpha(bodyBaseColor.a * alphaScale);
        }
        if (nextArrowSprite is { } arrow   && arrow.IsValid)
        {
            arrow.Color = arrowBaseColor.WithAlpha(arrowBaseColor.a * AlphaScaleHidden);
        }
    }

    /// <summary>
    /// インスペクタで指定したフォント・アイコンセット・枠設定を本文 Text へ適用する。
    /// 未設定（空文字・0）の項目は Text 側のシーン設定を尊重して触らない。
    /// </summary>
    private void ApplyTextStyle()
    {
        if (bodyText is not { } body || !body.IsValid) { return; }

        if (!string.IsNullOrEmpty(fontPath))    { body.FontPath = fontPath; }
        if (!string.IsNullOrEmpty(iconSetPath)) { body.IconSet  = iconSetPath; }

        // 枠幅が正のときだけ「枠あり」レイアウトを上書きする。
        // 0 のときはシーンで作り込んだ枠（素材に合わせた幅・高さ）をそのまま活かす。
        if (boxWidth > 0f)
        {
            body.BoxWidth  = boxWidth;
            body.BoxHeight = SEED.Mathf.Max(boxHeight, 0f);
            body.Wrap      = true;
        }
    }

    // ─── 内部処理: 位置 ─────────────────────────────────────

    /// <summary>ルートの CanvasTransform を取得する（未アタッチなら null）。</summary>
    /// <returns>ルートの CanvasTransform、または null。</returns>
    private SEED.CanvasTransform? RootTransform()
    {
        if (gameObject.GetComponent<SEED.CanvasTransform>() is not { } ct || !ct.IsValid) { return null; }
        return ct;
    }

    /// <summary>
    /// 位置アンカーアクタの変形を自分のルートへ写す【画面固定位置の唯一の実装】。
    /// </summary>
    /// <param name="anchor">位置アンカーアクタの CanvasTransform（無効なら何もしない）。</param>
    private void ApplyAnchor(SEED.CanvasTransform anchor)
    {
        if (!anchor.IsValid)
        {
            SEED.Debug.LogWarning("[TutorialWindow] 位置アンカーが未設定・無効です。現在の位置に表示します。");
            return;
        }
        if (RootTransform() is not { } ct) { return; }

        // アンカー・ピボット・位置・拡大率・回転をまとめて写す。
        // アンカーと同じ親（同じキャンバス）に置いてある前提なので、位置はそのまま使える。
        ct.Anchor   = anchor.Anchor;
        ct.Pivot    = anchor.Pivot;
        ct.Position = anchor.Position;
        ct.Scale    = anchor.Scale;
        ct.Rotation = anchor.Rotation;

        appliedAnchorPosition = anchor.Position;
        appliedAnchorAnchor   = anchor.Anchor;
        anchorApplied         = true;
    }

    /// <summary>
    /// 与えられた位置アンカーが、直前に写し取ったものと同じ場所か。
    /// </summary>
    /// <param name="anchor">判定する位置アンカー。</param>
    /// <returns>同じ場所とみなせるなら true。</returns>
    private bool IsSameAnchor(SEED.CanvasTransform anchor)
    {
        if (!anchorApplied || !anchor.IsValid) { return false; }

        return SEED.Mathf.Abs(anchor.Position.x - appliedAnchorPosition.x) <= AnchorSameEpsilon
            && SEED.Mathf.Abs(anchor.Position.y - appliedAnchorPosition.y) <= AnchorSameEpsilon
            && SEED.Mathf.Abs(anchor.Anchor.x   - appliedAnchorAnchor.x)   <= AnchorSameEpsilon
            && SEED.Mathf.Abs(anchor.Anchor.y   - appliedAnchorAnchor.y)   <= AnchorSameEpsilon;
    }

    /// <summary>
    /// ルートのアンカー・ピボットを画面中央基準にそろえる（AboveTarget 用）。
    /// </summary>
    private void ApplyCenterAnchor()
    {
        if (RootTransform() is not { } ct) { return; }

        ct.Anchor   = new SEED.Vector2(CenterNormalized, CenterNormalized);
        ct.Pivot    = new SEED.Vector2(CenterNormalized, CenterNormalized);
        ct.Scale    = SEED.Vector2.One;
        ct.Rotation = RotationNone;

        // 画面固定へ戻ったときに必ず写し直させる（同じ場所判定の取りこぼしを防ぐ）
        anchorApplied = false;
    }

    /// <summary>
    /// 窓の表示位置をルートの CanvasTransform へ適用する。
    /// </summary>
    /// <param name="canvasPosition">キャンバス座標（画面中央が原点・Y 下向き）。</param>
    private void ApplyPosition(SEED.Vector2 canvasPosition)
    {
        if (RootTransform() is not { } ct) { return; }
        ct.Position = canvasPosition;
    }

    /// <summary>
    /// 追従モードのとき、対象のワールド位置を画面へ射影して窓を置き直す。
    /// 画面固定モードでは何もしない（位置アンカーで置いた位置のまま）。
    /// </summary>
    private void UpdateFollow()
    {
        if (anchorMode != TutorialAnchorMode.AboveTarget) { return; }
        if (!followTarget.IsValid) { return; }
        if (projectionCamera is not { } cam || !cam.IsValid) { return; }

        // 対象の少し上のワールド点を射影する
        var worldPoint = followTarget.Position + SEED.Vector3.Up * aboveTargetWorldOffsetY;
        var screen     = cam.WorldToScreen(worldPoint);
        var canvasPos  = cam.WorldToCanvas(worldPoint);

        // カメラ背後の点は X / Y が意味を持たないので、方向を反転して大きく押し出し、
        // このあとのクランプで「対象が居る側の画面端」へ張り付かせる。
        if (screen.z <= BehindCameraDepth)
        {
            canvasPos = new SEED.Vector2(-canvasPos.x * BehindPushFactor, -canvasPos.y * BehindPushFactor);
        }

        // 画面上でさらに持ち上げてから、画面内へクランプする
        var lifted = new SEED.Vector2(canvasPos.x, canvasPos.y + aboveTargetCanvasOffsetY);
        ApplyPosition(ClampToCanvas(lifted));
    }

    /// <summary>
    /// キャンバス座標を画面内（余白ぶん内側）へクランプする。
    /// </summary>
    /// <param name="position">クランプ前のキャンバス座標。</param>
    /// <returns>画面内に収めたキャンバス座標。</returns>
    private SEED.Vector2 ClampToCanvas(SEED.Vector2 position)
    {
        float margin  = SEED.Mathf.Max(screenMargin, 0f);
        float halfX   = SEED.Mathf.Max(canvasWidth  * CanvasHalf - margin, 0f);
        float halfY   = SEED.Mathf.Max(canvasHeight * CanvasHalf - margin, 0f);

        return new SEED.Vector2(
            SEED.Mathf.Clamped(position.x, -halfX, halfX),
            SEED.Mathf.Clamped(position.y, -halfY, halfY));
    }

    // ─── 内部処理: 本文と送りマーク ─────────────────────────

    /// <summary>現在の表示ぶんを本文 Text へ反映する。</summary>
    private void ApplyBodyContent()
    {
        if (bodyText is { } body && body.IsValid) { body.Content = writer.Shown; }
    }

    /// <summary>
    /// 送りマークの表示を更新する（本文完了時のみ点滅表示）。
    /// </summary>
    /// <param name="unscaledDelta">前フレームからの経過秒（実時間）。</param>
    private void UpdateArrow(float unscaledDelta)
    {
        if (nextArrowSprite is not { } arrow || !arrow.IsValid) { return; }

        // 本文がまだ流れている間は出さない（もう送れると誤解させないため）
        if (!writer.IsComplete)
        {
            blinkTimer  = 0f;
            arrow.Color = arrowBaseColor.WithAlpha(arrowBaseColor.a * AlphaScaleHidden);
            return;
        }

        blinkTimer += unscaledDelta;

        // 周期が実質 0 なら点滅させず出しっぱなしにする
        float blink = ArrowBlinkMax;
        if (arrowBlinkPeriod > MinBlinkPeriod)
        {
            float half = arrowBlinkPeriod * BlinkHalfPeriodRatio;
            float wave = SEED.Mathf.PingPong(blinkTimer, half) / half;
            blink = SEED.Mathf.Lerp(ArrowBlinkMin, ArrowBlinkMax, wave);
        }

        arrow.Color = arrowBaseColor.WithAlpha(arrowBaseColor.a * blink);
    }
}
