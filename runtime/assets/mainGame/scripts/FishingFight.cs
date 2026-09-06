using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// ヒット後の「魚とのやり取り（釣りバトル）」を司るスクリプト【リズムゲーム版 2026-09-06 改定】。
///
/// <b>プレイヤーアクタに 3 本目のスクリプトスロット「Fight」として付ける</b>
/// （<see cref="FishingController"/> と同じアクタ。コントローラの
/// <c>fight</c> フィールドから参照される）。
///
/// <b>単一責任</b>
/// 本スクリプトが持つのは「拍時計」「出題／回答／隙のフェーズ進行」「判定とテンション／疲労」
/// 「その UI 表示」だけ。ウキの移動・状態遷移・魚の解放は <see cref="FishingController"/> の
/// 責務で、本スクリプトは<b>毎フレーム値を進めて結果（<see cref="LineBroken"/> /
/// <see cref="FishDefeated"/> / <see cref="ComputeFloatDistanceStep"/>）を返すだけ</b>。
/// 自前の Update は持たず、すべてコントローラ側から <see cref="Tick"/> で駆動される
/// （ヒット中だけ進む＝実行順の曖昧さを持ち込まないため）。
///
/// ────────────────────────────────────────────────────────
/// <b>仕様（2026-09-06 リズム版）</b>
///
/// ■ 拍時計（<see cref="BeatIndex"/> / <see cref="BarIndex"/> / <see cref="BeatPhase01"/> /
///   <see cref="BarPhase01"/> / <see cref="TimeToNearestBeat"/>）
/// バトル開始と同時に魚の BPM・拍子でメトロノームが走り出す。時間は <c>dt</c> の積算で
/// 管理する（音の再生位置を問い合わせる API が無いため）。拍が変わるたびに
/// <see cref="metronomeSePath"/> を鳴らし、小節頭だけ音量を上げる。
/// <see cref="Paused"/>（わらしべ連鎖のアタリ受付中）のあいだは時計ごと止まる＝無音になる。
///
/// ■ フェーズ（すべて<b>小節単位</b>・切り替えは必ず小節頭）
/// <code>
/// 出題(Call) → 回答(Answer) → 隙(Rest) → 出題 → …
/// 既定の長さ: 出題 callBars 小節 / 回答 answerBars 小節 / 隙 restBars 小節
///             （疲労中は隙が restBarsWhenTired 小節ぶん延びる）
/// 魚データ（Fish.RhythmCallBars など）が 1 以上ならそちらが優先される。
/// </code>
/// 次のフェーズは<b>1 拍前</b>に中央テキストで予告する。
///
/// ■ 出題（Call）
/// 魚のリズムパターン（8 分音符の並び。'x' が打点・'.' が休符）を 1 つ抽選し、
/// 打点の時刻ごとに<b>前アタリと同じ演出</b>（つつき音＋ウキの沈み）を出す
/// （<see cref="FishingController.PlayNibbleCue"/>）。出題中の左クリックは Miss。
///
/// ■ 回答（Answer）
/// 同じパターンを<b>左クリック</b>で再現する。打点ごとに最も近いクリックとの時間差 |Δt| で
/// <code>
/// |Δt| ≦ excellentSeconds → Excellent   （テンション減）
/// |Δt| ≦ greatSeconds     → Great
/// |Δt| ≦ niceSeconds      → Nice
/// 上記のいずれでもない（窓を過ぎた）→ Miss
/// どの打点にも結び付かない余分なクリック → Miss
/// </code>
/// を判定し、判定画像と「早い／遅い」のヒントを出す（表示はコントローラ側の共通 UI）。
///
/// ■ テンション（0〜1・一方向。旧「糸 HP」と ±ゲージを置き換えたもの）
/// <code>
/// 判定ごと     : テンション += |Δt| × tensionPerSecondOfOffset × レベル補正
///                Excellent は加算せず excellentTensionRelief だけ減る
/// Miss・空打ち : テンション += missTension
/// 隙(Rest)中   : テンション −= tensionRecoverPerSec × dt
/// レベル補正   = 1 + tensionLevelScale × (魚の総合力 ÷ 竿パワー − 1)（下限 0.5）
/// 安全帯の幅   = safeZoneBase + safeZonePerLinePower × (糸パワー − 1)（表示のみ）
/// テンション ≧ 1 → 糸が切れる（<see cref="LineBroken"/>）
/// </code>
///
/// ■ 疲労（0〜1・内側の指標）
/// 判定成功ごとに溜まり（Excellent 0.12 / Great 0.08 / Nice 0.03）、
/// 1 に達すると <see cref="IsTired"/> が <see cref="tiredBars"/> 小節のあいだ true になる。
/// 疲労中は 隙が延び、巻き効率が <see cref="tiredReelBonus"/> 倍になる。
/// 期間が終わると疲労は 0 へリセットされる。
///
/// ■ 魚 HP（巻き取り）
/// <code>
/// 掛かった瞬間の魚の総合力 p0 ＝ 基礎パワー × 大きさスコア
/// 魚の取り分 share    ＝ p0 ÷ (竿パワー ＋ p0)
/// 魚HP最大            ＝ 基礎HP ＋ 基礎HP × share
/// 1HP あたりの距離     ＝ 掛かった瞬間の距離 ÷ 基礎HP（距離は hookDistanceMin で下限クランプ）
/// 目標距離            ＝ 現在の魚HP × 1HP あたりの距離
/// 巻き効率            ＝ 竿パワー ÷ (竿パワー ＋ 魚の総合力) × (疲労中なら tiredReelBonus)
/// 巻き 1m あたりの HP  ＝ 巻き効率 ÷ 1HP あたりの距離（<see cref="ReelHpPerUnit"/>）
/// </code>
/// <b>巻けるのは「隙(Rest)」のあいだだけ</b>。出題・回答中は巻き入力を無視し、
/// ウキも動かさない（拍を読むあいだ画が暴れないようにするため）。
/// 魚 HP が 0 になった瞬間が釣り上げ成立（<see cref="FishDefeated"/>）。
///
/// ■ 合わせランクの影響
/// 合わせが悪いほど初期テンションが高い（＝危険側から始まる）。
/// ────────────────────────────────────────────────────────
///
/// <b>UI（円形のリズム時計）</b>
/// 画面中央の円（<c>GaugeSeg00</c>… の 48 セグメント＋<c>GaugeMarker</c>）を時計として使う。
/// - マーカー … 小節内の進行（<see cref="BarPhase01"/> × 360 度・真上が小節頭・右回り）
/// - セグメント … 真上から右回りにテンションの円弧（安全帯の内側は緑、外は黄→赤）
///   ＋ パターンの打点位置に拍マーク（出題中は明るく、回答中は暗く、判定した瞬間だけ判定色）
/// - 中央テキスト … フェーズ名（＋予告）／魚 HP ％／疲労 ％
/// - 右下テキスト … 残り距離（従来どおり）
/// </summary>
public class FishingFight : SEEDScript
{
    // ─── 定数（内部計算の下駄・ゼロ割回避）─────────────────────

    /// <summary>ゼロ割回避に使う微小値。</summary>
    private const float DivideEpsilon = 0.0001f;

    /// <summary>割合（0〜1）をパーセント表示へ直す係数。</summary>
    private const float PercentScale = 100f;

    /// <summary>魚 HP の下限（これ以下で釣り上げ成立）。</summary>
    private const float FishHpZero = 0f;

    /// <summary>テンションの下限。</summary>
    private const float TensionMin = 0f;

    /// <summary>テンションの上限（ここに達すると糸が切れる）。</summary>
    private const float TensionMax = 1f;

    /// <summary>疲労の上限（ここに達すると疲労状態へ入る）。</summary>
    private const float FatigueMax = 1f;

    /// <summary>全周の角度（度）。円形 UI の写像に使う。</summary>
    private const float FullCircleDegrees = 360f;

    /// <summary>1 分の秒数（BPM →「1 拍の秒数」の換算に使う）。</summary>
    private const float SecondsPerMinute = 60f;

    /// <summary>1 拍を分割する数（8 分音符 ＝ 1 拍を 2 分割）。</summary>
    private const int SubdivisionsPerBeat = 2;

    /// <summary>パターン文字列で「打点」を表す文字。</summary>
    private const char PatternHitChar = 'x';

    /// <summary>パターン文字列で「休符」を表す文字。</summary>
    private const char PatternRestChar = '.';

    /// <summary>拍子（1 小節の拍数）の下限。データが壊れていても時計が止まらないようにする。</summary>
    private const int MinBeatsPerBar = 1;

    /// <summary>BPM の下限（0 や負の BPM で時計が破綻しないようにする番人値）。</summary>
    private const float MinBpm = 1f;

    /// <summary>フェーズの長さ（小節数）の下限。</summary>
    private const int MinPhaseBars = 1;

    /// <summary>「魚データ側の指定なし（＝バトル側の既定を使う）」を表す値。</summary>
    private const int UseFightDefaultBars = 0;

    /// <summary>レベル補正（テンションの効き）の下限。</summary>
    private const float LevelScaleMin = 0.5f;

    /// <summary>まだ 1 度も拍を鳴らしていないことを表す番兵値。</summary>
    private const int NoBeatPlayed = -1;

    /// <summary>まだ 1 度も打点キューを出していないことを表す番兵値。</summary>
    private const int NoCueFired = -1;

    /// <summary>該当なしを表す添字（<see cref="FindNearestPendingHit"/> の戻り値）。</summary>
    private const int NoIndex = -1;

    /// <summary>「巻いている」とみなす巻き取り量のしきい値（メートル）。</summary>
    private const float ReelInputEpsilon = 0.0001f;

    /// <summary>戦闘力の基準比（魚 ÷ 竿 がこの値なら「等価」）。</summary>
    private const float EquivalentPowerRatio = 1f;

    /// <summary>倍率の基準値（1 ＝ 効果なし）。</summary>
    private const float NeutralMultiplier = 1f;

    /// <summary>色キャッシュの未書き込みを表す番兵アルファ（実値として現れない負値）。</summary>
    private const float UncachedAlpha = -1f;

    // ─── 装備パラメータ ───────────────────────────────────

    /// <summary>
    /// 竿パワー。魚の総合力と<b>同じ単位</b>で比較され、巻き効率とテンションの効きを決める。
    /// 大きいほど楽になる。
    /// </summary>
    [Header("装備"), SerializeField(Label = "竿パワー")]
    private float rodPower = 10f;

    /// <summary>
    /// 糸パワー。大きいほど安全帯（緑の円弧）が広がる。
    /// 投げるごとにリセットされる想定（強化は将来のフィールド要素）。
    /// </summary>
    [SerializeField(Label = "糸パワー")]
    private float linePower = 1f;

    // ─── フェーズの長さ（小節数）─────────────────────────────

    /// <summary>出題（魚が叩く）フェーズの長さ（小節）。魚データが 1 以上ならそちらが優先。</summary>
    [Header("フェーズの長さ(小節)"), SerializeField(Label = "出題の小節数")]
    private int callBars = 1;

    /// <summary>回答（プレイヤーが叩く）フェーズの長さ（小節）。</summary>
    [SerializeField(Label = "回答の小節数")]
    private int answerBars = 1;

    /// <summary>隙（巻きに専念できる）フェーズの長さ（小節）。</summary>
    [SerializeField(Label = "隙の小節数")]
    private int restBars = 2;

    /// <summary>疲労中に隙へ<b>加算</b>される小節数。</summary>
    [SerializeField(Label = "疲労中に延びる隙の小節数")]
    private int restBarsWhenTired = 2;

    // ─── メトロノーム ────────────────────────────────────

    /// <summary>拍ごとに鳴らすクリック音のアセットパス（空なら鳴らさない）。</summary>
    [Header("メトロノーム"), SerializeField(Label = "メトロノームの効果音")]
    private string metronomeSePath = "assets://mainGame/audios/metronome.mp3";

    /// <summary>小節頭以外の拍で鳴らす音量（0〜1）。</summary>
    [SerializeField(Label = "メトロノームの音量")]
    private float metronomeVolume = 0.45f;

    /// <summary>小節頭の拍で鳴らす音量（0〜1）。強拍を分かりやすくするため大きめにする。</summary>
    [SerializeField(Label = "メトロノームの音量(小節頭)")]
    private float metronomeBarHeadVolume = 0.9f;

    // ─── 判定窓 ──────────────────────────────────────────

    /// <summary>
    /// Excellent と判定される時間差の上限（秒）。
    /// 合わせ（<see cref="FishingController"/>）の判定窓とは別物で、リズム用に大幅に狭い。
    /// </summary>
    [Header("判定窓"), SerializeField(Label = "Excellent の時間差(秒)")]
    private float excellentSeconds = 0.06f;

    /// <summary>Great と判定される時間差の上限（秒）。</summary>
    [SerializeField(Label = "Great の時間差(秒)")]
    private float greatSeconds = 0.12f;

    /// <summary>Nice と判定される時間差の上限（秒）。＝打点ごとの受付窓そのもの。</summary>
    [SerializeField(Label = "Nice の時間差(秒)")]
    private float niceSeconds = 0.2f;

    // ─── テンション ──────────────────────────────────────

    /// <summary>時間差 1 秒あたりに増えるテンション（レベル補正が掛かる）。</summary>
    [Header("テンション"), SerializeField(Label = "時間差1秒あたりのテンション")]
    private float tensionPerSecondOfOffset = 0.6f;

    /// <summary>Excellent 1 回で減るテンション。</summary>
    [SerializeField(Label = "Excellentのテンション回復")]
    private float excellentTensionRelief = 0.03f;

    /// <summary>Miss（打ち逃し・空打ち）1 回で増えるテンション。</summary>
    [SerializeField(Label = "Missのテンション増加")]
    private float missTension = 0.12f;

    /// <summary>隙（Rest）中にテンションが減る速度（/秒）。</summary>
    [SerializeField(Label = "隙のテンション回復(/秒)")]
    private float tensionRecoverPerSec = 0.12f;

    /// <summary>
    /// 戦闘力差がテンションの増え方へ効く強さ。
    /// レベル補正 ＝ 1 + 本値 × (魚の総合力 ÷ 竿パワー − 1)（下限 <see cref="LevelScaleMin"/>）。
    /// 0 なら戦闘力差を無視する。
    /// </summary>
    [SerializeField(Label = "戦闘力差の効き")]
    private float tensionLevelScale = 1f;

    /// <summary>安全帯（緑の円弧）の幅の基準値（テンション換算・糸パワー 1 のとき）。</summary>
    [SerializeField(Label = "安全帯の幅(基準)")]
    private float safeZoneBase = 0.5f;

    /// <summary>糸パワー 1 あたりに広がる安全帯の幅。</summary>
    [SerializeField(Label = "糸パワー1あたりの安全帯増分")]
    private float safeZonePerLinePower = 0.08f;

    // ─── 合わせランクによる初期テンション ─────────────────────

    /// <summary>Excellent で合わせたときの初期テンション。</summary>
    [Header("初期テンション(合わせランク)"), SerializeField(Label = "初期テンション(Excellent)")]
    private float initialTensionExcellent = 0f;

    /// <summary>Great で合わせたときの初期テンション。</summary>
    [SerializeField(Label = "初期テンション(Great)")]
    private float initialTensionGreat = 0.15f;

    /// <summary>
    /// Nice で合わせたときの初期テンション。
    /// 判定が取れていない場合（None など）のフォールバックにも使う（＝最も不利な値）。
    /// </summary>
    [SerializeField(Label = "初期テンション(Nice)")]
    private float initialTensionNice = 0.3f;

    // ─── 疲労 ────────────────────────────────────────────

    /// <summary>Excellent 1 回で溜まる疲労。</summary>
    [Header("疲労"), SerializeField(Label = "Excellentの疲労")]
    private float fatigueExcellent = 0.12f;

    /// <summary>Great 1 回で溜まる疲労。</summary>
    [SerializeField(Label = "Greatの疲労")]
    private float fatigueGreat = 0.08f;

    /// <summary>Nice 1 回で溜まる疲労。</summary>
    [SerializeField(Label = "Niceの疲労")]
    private float fatigueNice = 0.03f;

    /// <summary>疲労が満タンになってから疲労状態が続く小節数。</summary>
    [SerializeField(Label = "疲労状態の小節数")]
    private int tiredBars = 3;

    /// <summary>疲労中の巻き効率の倍率。</summary>
    [SerializeField(Label = "疲労中の巻き効率倍率")]
    private float tiredReelBonus = 1.5f;

    // ─── 戦闘力（魚側）─────────────────────────────────────

    /// <summary>大きさスコアの下限（<see cref="sizeMultiplierRefMin"/> に対応）。</summary>
    [Header("戦闘力(魚)"), SerializeField(Label = "大きさスコアの下限")]
    private float sizeScoreMin = 0.9f;

    /// <summary>大きさスコアの上限（<see cref="sizeMultiplierRefMax"/> に対応）。</summary>
    [SerializeField(Label = "大きさスコアの上限")]
    private float sizeScoreMax = 1.1f;

    /// <summary>
    /// 大きさスコアの写像元となる <see cref="Fish.SizeMultiplier"/> の下限。
    /// Fish 側のサイズ倍率の抽選範囲（既定 0.8〜1.3）に合わせておく。
    /// </summary>
    [SerializeField(Label = "サイズ倍率の基準下限")]
    private float sizeMultiplierRefMin = 0.8f;

    /// <summary>大きさスコアの写像元となる <see cref="Fish.SizeMultiplier"/> の上限。</summary>
    [SerializeField(Label = "サイズ倍率の基準上限")]
    private float sizeMultiplierRefMax = 1.3f;

    // ─── ウキの距離制御（目標距離への追従）─────────────────────

    /// <summary>
    /// 目標距離が現在より<b>遠い</b>とき、魚がウキを沖へ引く速度の基準値（m/秒）。
    /// 実際の速度は「魚の総合力 ÷ 竿パワー」を掛けた値になる。
    /// </summary>
    [Header("ウキの距離制御"), SerializeField(Label = "魚の引き速度(m/秒)")]
    private float fishPullSpeed = 1.5f;

    /// <summary>目標距離が現在より<b>近い</b>とき、ウキが手元へ寄る速度の上限（m/秒）。</summary>
    [SerializeField(Label = "寄せ速度の上限(m/秒)")]
    private float reelInSpeedMax = 6f;

    /// <summary>引きの速度倍率（魚 ÷ 竿）の下限。</summary>
    [SerializeField(Label = "引きの速度倍率の下限")]
    private float pullRateMultiplierMin = 0.25f;

    /// <summary>引きの速度倍率（魚 ÷ 竿）の上限。</summary>
    [SerializeField(Label = "引きの速度倍率の上限")]
    private float pullRateMultiplierMax = 2f;

    // ─── 魚 HP ───────────────────────────────────────────

    /// <summary>
    /// 掛かった瞬間の距離（ウキ→竿先）の下限（メートル）。
    /// 手元で掛かったときに「1HP あたりの距離」が 0 に潰れるのを防ぐ番人値。
    /// </summary>
    [Header("魚HP"), SerializeField(Label = "掛かった距離の下限(m)")]
    private float hookDistanceMin = 2f;

    // ─── 効果音 ──────────────────────────────────────────

    /// <summary>糸が切れた瞬間に鳴らす効果音のアセットパス（空なら鳴らさない）。</summary>
    [Header("効果音"), SerializeField(Label = "糸切れの効果音")]
    private string lineBreakSePath = "";

    /// <summary>糸切れ効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "糸切れの音量")]
    private float lineBreakSeVolume = 1f;

    // ─── UI 参照 ─────────────────────────────────────────

    /// <summary>
    /// 円を構成するセグメントのスプライト（<c>GaugeSeg00</c>… を順に割り当てる）。
    /// 個数はそのまま円の分割数になる。未設定でもロジックは成立する。
    /// </summary>
    [Header("UI 参照"), SerializeField(Label = "円セグメントのSprite")]
    private List<SEED.Sprite> segmentSprites = new();

    /// <summary>
    /// 円セグメントの CanvasTransform（位置と回転を書き換える）。
    /// <see cref="segmentSprites"/> と<b>同じ順・同じアクタ</b>を割り当てること。
    /// </summary>
    [SerializeField(Label = "円セグメントのCanvasTransform")]
    private List<SEED.CanvasTransform> segmentTransforms = new();

    /// <summary>小節内の進行を示すマーカーのスプライト（白い小片）。</summary>
    [SerializeField(Label = "マーカーのSprite")]
    private SEED.Sprite? gaugeMarker = null;

    /// <summary>
    /// マーカーの CanvasTransform（円周上の位置を毎フレーム書き換える）。
    /// <see cref="gaugeMarker"/> と<b>同じアクタ</b>を割り当てること。
    /// </summary>
    [SerializeField(Label = "マーカーのCanvasTransform")]
    private SEED.CanvasTransform? gaugeMarkerTransform = null;

    /// <summary>円の中心に出す状態テキスト（フェーズ名／魚 HP ％／疲労 ％）。</summary>
    [SerializeField(Label = "状態のText")]
    private SEED.Text? hpText = null;

    /// <summary>画面右下に出す残り距離のテキスト（"9.6m" 形式）。</summary>
    [SerializeField(Label = "残り距離のText")]
    private SEED.Text? distanceText = null;

    // ─── UI レイアウト ────────────────────────────────────

    /// <summary>円の半径（ピクセル）。マーカーとセグメントの配置半径。</summary>
    [Header("UI レイアウト"), SerializeField(Label = "円の半径(px)")]
    private float arcRadiusPx = 140f;

    /// <summary>セグメントの幅（ピクセル）。円周方向の長さ。</summary>
    [SerializeField(Label = "セグメントの幅(px)")]
    private float segmentWidthPx = 16f;

    /// <summary>セグメントの高さ（ピクセル）。半径方向の太さ。</summary>
    [SerializeField(Label = "セグメントの高さ(px)")]
    private float segmentHeightPx = 10f;

    /// <summary>テンションの円弧が届いていないセグメントの色（RGB）。</summary>
    [SerializeField(Label = "空きセグメントの色(RGB)")]
    private SEED.Vector3 emptyColor = new SEED.Vector3(0.25f, 0.28f, 0.32f);

    /// <summary>安全帯（テンションが低い領域）の色（RGB）。</summary>
    [SerializeField(Label = "安全帯の色(RGB)")]
    private SEED.Vector3 safeColor = new SEED.Vector3(0.2f, 0.9f, 0.3f);

    /// <summary>安全帯のすぐ外側（警告）の色（RGB）。</summary>
    [SerializeField(Label = "警告帯の色(RGB)")]
    private SEED.Vector3 warnColor = new SEED.Vector3(1f, 0.85f, 0.2f);

    /// <summary>テンション上限付近（危険）の色（RGB）。</summary>
    [SerializeField(Label = "危険帯の色(RGB)")]
    private SEED.Vector3 dangerColor = new SEED.Vector3(1f, 0.2f, 0.15f);

    /// <summary>出題中に光る拍マークの色（RGB）。</summary>
    [SerializeField(Label = "拍マークの色(出題)")]
    private SEED.Vector3 beatCallColor = new SEED.Vector3(0.4f, 0.85f, 1f);

    /// <summary>回答中の拍マーク（未判定）の色（RGB）。</summary>
    [SerializeField(Label = "拍マークの色(回答)")]
    private SEED.Vector3 beatAnswerColor = new SEED.Vector3(0.5f, 0.55f, 0.6f);

    /// <summary>判定成功の瞬間に拍マークが光る色（RGB）。</summary>
    [SerializeField(Label = "拍マークの色(判定成功)")]
    private SEED.Vector3 beatHitColor = new SEED.Vector3(1f, 1f, 1f);

    /// <summary>判定失敗（Miss）の瞬間に拍マークが光る色（RGB）。</summary>
    [SerializeField(Label = "拍マークの色(Miss)")]
    private SEED.Vector3 beatMissColor = new SEED.Vector3(1f, 0.25f, 0.25f);

    /// <summary>判定した拍マークが光り続ける秒数。</summary>
    [SerializeField(Label = "判定マークの点灯秒数")]
    private float beatFlashSeconds = 0.25f;

    /// <summary>出題中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "マーカーの色(出題)")]
    private SEED.Vector3 markerCallColor = new SEED.Vector3(0.4f, 0.85f, 1f);

    /// <summary>回答中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "マーカーの色(回答)")]
    private SEED.Vector3 markerAnswerColor = new SEED.Vector3(1f, 0.95f, 0.3f);

    /// <summary>隙（Rest）中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "マーカーの色(隙)")]
    private SEED.Vector3 markerRestColor = new SEED.Vector3(1f, 1f, 1f);

    /// <summary>疲労中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "マーカーの色(疲労)")]
    private SEED.Vector3 markerTiredColor = new SEED.Vector3(0.6f, 1f, 0.6f);

    /// <summary>セグメントの不透明度（バトル中）。</summary>
    [SerializeField(Label = "セグメントの不透明度")]
    private float segmentOpacity = 0.9f;

    /// <summary>マーカーの不透明度（バトル中）。</summary>
    [SerializeField(Label = "マーカーの不透明度")]
    private float gaugeMarkerOpacity = 1f;

    /// <summary>状態テキストの不透明度（バトル中）。</summary>
    [SerializeField(Label = "状態テキストの不透明度")]
    private float hpTextOpacity = 1f;

    /// <summary>残り距離テキストの不透明度（バトル中）。</summary>
    [SerializeField(Label = "残り距離テキストの不透明度")]
    private float distanceTextOpacity = 1f;

    // ─── 公開状態 ────────────────────────────────────────

    /// <summary>
    /// やり取りのフェーズ【外部（カメラ）から見える唯一の進行状態】。
    /// <see cref="Answer"/> のあいだだけカメラが回答用の構図へ切り替わる。
    /// </summary>
    public enum Phase
    {
        /// <summary>バトルしていない。</summary>
        None,

        /// <summary>出題中（魚がリズムを叩く）。</summary>
        Call,

        /// <summary>回答中（プレイヤーが同じリズムを叩く）。</summary>
        Answer,

        /// <summary>隙（巻きに専念できる）。</summary>
        Rest,
    }

    /// <summary>バトル進行中か（<see cref="BeginFight"/> 〜 <see cref="EndFight"/>）。</summary>
    public bool Active { get; private set; } = false;

    /// <summary>
    /// バトルの一時停止フラグ【わらしべ連鎖のアタリ中に使う】。
    ///
    /// true のあいだ <see cref="Tick"/> は拍時計・テンション・疲労・魚 HP を一切進めず、
    /// メトロノームも鳴らさず、ウキの移動量（<see cref="ComputeFloatDistanceStep"/>）も 0 にして、
    /// UI の再描画だけを行う。連鎖の決着でコントローラが false へ戻す。
    /// <see cref="EndFight"/> でも必ず false へ戻る。
    /// </summary>
    public bool Paused { get; set; } = false;

    /// <summary>現在のフェーズ（非バトル中は <see cref="Phase.None"/>）。</summary>
    public Phase CurrentPhase { get; private set; } = Phase.None;

    /// <summary>現在のテンション（0〜1）。1 で糸が切れる。</summary>
    public float Tension { get; private set; } = 0f;

    /// <summary>現在の疲労（0〜1）。</summary>
    public float Fatigue01 { get; private set; } = 0f;

    /// <summary>疲労状態か（隙が延び、巻き効率が上がる）。</summary>
    public bool IsTired { get; private set; } = false;

    /// <summary>
    /// 糸が切れたか。テンションが 1 に達したフレームで true になる。
    /// コントローラ側が拾ったら <see cref="EndFight"/> で false へ戻る。
    /// </summary>
    public bool LineBroken { get; private set; } = false;

    /// <summary>現在の魚 HP の割合（0〜1）。UI 表示用。</summary>
    public float FishHp01 => fishHpMax > DivideEpsilon
        ? SEED.Mathf.Clamped01(fishHp / fishHpMax)
        : 0f;

    /// <summary>
    /// 魚 HP を削り切ったか（＝釣り上げ成立）。
    /// バトル中だけ true になり得る（<see cref="EndFight"/> で必ず落ちる）。
    /// </summary>
    public bool FishDefeated => Active && fishHp <= FishHpZero;

    /// <summary>
    /// 巻き取り 1m あたりに削れる魚 HP【巻きの仕様の中核】。
    /// ＝ 巻き効率（竿パワー ÷ (竿パワー ＋ 魚の総合力) × 疲労ボーナス）÷ 1HP あたりの距離。
    ///
    /// 巻き効率は魚がどれだけ強くても<b>必ず 0 より大きい</b>ので、
    /// 「格上でも巻き続ければ削り切れる（テクニックで釣れる）」が成立する。
    /// </summary>
    public float ReelHpPerUnit
    {
        get
        {
            float rod = SEED.Mathf.Max(rodPower, DivideEpsilon);
            float efficiency = rod / SEED.Mathf.Max(rod + CurrentFishPower(), DivideEpsilon);
            float bonus = IsTired ? SEED.Mathf.Max(tiredReelBonus, DivideEpsilon) : NeutralMultiplier;
            return efficiency * bonus / SEED.Mathf.Max(metersPerHp, DivideEpsilon);
        }
    }

    /// <summary>
    /// いまウキが居るべき距離（ウキ→竿先の水平距離、メートル）
    /// ＝ 現在の魚 HP × 1HP あたりの距離。
    /// </summary>
    public float DesiredFloatDistance => SEED.Mathf.Max(fishHp, FishHpZero) * metersPerHp;

    /// <summary>糸切れの効果音パス（コントローラ側から鳴らす場合の参照用）。</summary>
    public string LineBreakSePath => lineBreakSePath;

    /// <summary>糸切れの効果音の音量。</summary>
    public float LineBreakSeVolume => lineBreakSeVolume;

    // ─── 拍時計の公開値 ───────────────────────────────────

    /// <summary>バトル開始からの経過秒数（<see cref="Paused"/> 中は進まない）。</summary>
    public float ClockTime => clockTime;

    /// <summary>1 拍の秒数（＝ 60 ÷ BPM）。</summary>
    public float SecondsPerBeat => secondsPerBeat;

    /// <summary>1 小節の秒数（＝ 1 拍の秒数 × 拍子）。</summary>
    public float SecondsPerBar => secondsPerBeat * beatsPerBar;

    /// <summary>開始からの通し拍番号（0 始まり）。</summary>
    public int BeatIndex => secondsPerBeat > DivideEpsilon
        ? SEED.Mathf.FloorToInt(clockTime / secondsPerBeat)
        : 0;

    /// <summary>開始からの通し小節番号（0 始まり）。</summary>
    public int BarIndex => beatsPerBar > 0 ? BeatIndex / beatsPerBar : 0;

    /// <summary>拍のなかの進行（0〜1）。0 が拍頭。</summary>
    public float BeatPhase01 => secondsPerBeat > DivideEpsilon
        ? SEED.Mathf.Repeat(clockTime / secondsPerBeat, 1f)
        : 0f;

    /// <summary>小節のなかの進行（0〜1）。0 が小節頭。</summary>
    public float BarPhase01 => SecondsPerBar > DivideEpsilon
        ? SEED.Mathf.Repeat(clockTime / SecondsPerBar, 1f)
        : 0f;

    /// <summary>
    /// いまの時刻から見た、最も近い分割線までの<b>符号つき</b>時間差（秒）。
    /// ＋ ＝ 分割線を過ぎている（遅れている） / − ＝ まだ手前（早い）。
    /// </summary>
    /// <param name="subdivision">1 拍の分割数（1 ＝ 拍・2 ＝ 8 分音符）。</param>
    public float TimeToNearestBeat(int subdivision)
    {
        float grid = secondsPerBeat / SEED.Mathf.Max(subdivision, 1);
        if (grid <= DivideEpsilon) { return 0f; }

        float offset = SEED.Mathf.Repeat(clockTime, grid);
        return offset <= grid * 0.5f ? offset : offset - grid;
    }

    // ─── 実行時の内部状態 ─────────────────────────────────

    /// <summary>戦っている魚（null ＝ 非戦闘中）。位置・状態は一切触らず、パラメータだけ読む。</summary>
    private Fish? target = null;

    /// <summary>現在の魚 HP（内部値）。0 で釣り上げ成立。</summary>
    private float fishHp = 0f;

    /// <summary>このバトルでの魚 HP の最大値（＝基礎HP ＋ ボーナス HP）。</summary>
    private float fishHpMax = 0f;

    /// <summary>
    /// 魚 HP 1 あたりの距離（メートル）＝ 掛かった瞬間の距離 ÷ 魚の基礎HP。
    /// 魚 HP と「ウキ→竿先の距離」を相互変換する唯一の係数。
    /// </summary>
    private float metersPerHp = 0f;

    /// <summary>バトル開始からの経過秒数（拍時計の唯一の時間源）。</summary>
    private float clockTime = 0f;

    /// <summary>1 拍の秒数（BPM から <see cref="BeginFight"/> で決まる）。</summary>
    private float secondsPerBeat = 0.6f;

    /// <summary>1 小節の拍数（拍子）。</summary>
    private int beatsPerBar = 4;

    /// <summary>1 小節の分割数（＝ 拍子 × <see cref="SubdivisionsPerBeat"/>）。</summary>
    private int subsPerBar = 8;

    /// <summary>1 分割（8 分音符）の秒数。</summary>
    private float secondsPerSub = 0.3f;

    /// <summary>最後にメトロノームを鳴らした拍番号（<see cref="NoBeatPlayed"/> ＝ 未再生）。</summary>
    private int lastBeatPlayed = NoBeatPlayed;

    /// <summary>現在のフェーズが始まった時刻（秒・必ず小節頭）。</summary>
    private float phaseStartTime = 0f;

    /// <summary>現在のフェーズが終わる時刻（秒・必ず小節頭）。</summary>
    private float phaseEndTime = 0f;

    /// <summary>現在のフェーズの長さ（小節）。</summary>
    private int phaseBars = 1;

    /// <summary>次のフェーズの予告（1 拍前）を済ませたか。</summary>
    private bool nextPhaseAnnounced = false;

    /// <summary>このバトルで使う有効なリズムパターン（1 要素 ＝ 1 小節ぶんの文字列）。</summary>
    private readonly List<string> patterns = new();

    /// <summary>いま出題／回答しているパターン（1 小節ぶん）。</summary>
    private string currentPattern = "";

    /// <summary>出題中に最後に打点キューを出した分割番号（フェーズ内の通し番号）。</summary>
    private int lastCueSub = NoCueFired;

    /// <summary>回答フェーズで期待している打点の時刻（秒・絶対時刻）。</summary>
    private readonly List<float> expectedTimes = new();

    /// <summary>期待打点の分割番号（フェーズ内の通し番号。UI の角度算出に使う）。</summary>
    private readonly List<int> expectedSubs = new();

    /// <summary>期待打点が判定済みか（true ＝ もう結び付かない）。</summary>
    private readonly List<bool> expectedJudged = new();

    /// <summary>期待打点の判定結果（点灯色の決定に使う）。</summary>
    private readonly List<FishingController.HookJudgement> expectedResults = new();

    /// <summary>期待打点の点灯残り秒数（0 以下 ＝ 消灯）。</summary>
    private readonly List<float> expectedFlash = new();

    /// <summary>疲労状態が終わる小節番号（<see cref="IsTired"/> が true のあいだ有効）。</summary>
    private int tiredEndBar = 0;

    /// <summary>セグメントの配置を計算したときの個数（個数が変わったときだけ組み直す）。</summary>
    private int cachedSegmentCount = 0;

    /// <summary>各セグメントへ最後に書き込んだ色（差分更新のための控え）。</summary>
    private readonly List<SEED.Color> cachedSegmentColors = new();

    // ─── ライフサイクル ───────────────────────────────────

    /// <summary>開始時に UI を隠す（バトル中以外は一切見せない）。</summary>
    public override void OnStart()
    {
        ResetRuntimeState();
        HideUi();
    }

    /// <summary>破棄時も UI を隠す（表示しっぱなしを防ぐ）。</summary>
    public override void OnDestroy()
    {
        HideUi();
    }

    // 本スクリプトは自前の毎フレーム更新を持たない。
    // 進行はすべて FishingController が Tick() で駆動する（実行順の曖昧さを排除するため）。

    // ─── 公開 API ────────────────────────────────────────

    /// <summary>
    /// バトルを開始する（コントローラが合わせ成功で魚を掛けた瞬間に呼ぶ）。
    ///
    /// 魚のリズムデータ（BPM・拍子・パターン・フェーズ長）を取り込み、
    /// 拍時計を 0 から回し始めて<b>出題フェーズ</b>へ入る。
    /// 初期テンションは<b>合わせランク</b>から決める（ランクが悪いほど高い＝危険側）。
    /// </summary>
    /// <param name="fish">掛かった魚（パラメータを読むだけで一切動かさない）。</param>
    /// <param name="judge">合わせ判定（初期テンションの決定に使う）。</param>
    /// <param name="hookDistance">
    /// 掛かった瞬間のウキ→竿先の水平距離（メートル）。
    /// 「魚 HP 1 あたりの距離」の基準になる（<see cref="hookDistanceMin"/> で下限クランプ）。
    /// </param>
    public void BeginFight(Fish fish, FishingController.HookJudgement judge, float hookDistance)
    {
        ResetRuntimeState();

        target = fish;
        Active = true;
        Tension = SEED.Mathf.Clamped(InitialTension(judge), TensionMin, TensionMax);

        // 魚 HP: 掛かった瞬間の総合力で「魚の取り分」を出し、その割合ぶんだけ基礎HP へ乗せる
        float rod = SEED.Mathf.Max(rodPower, DivideEpsilon);
        float hookPower = SEED.Mathf.Max(fish.BasePower * SizeScore(fish), 0f);
        float fishShare = hookPower / SEED.Mathf.Max(rod + hookPower, DivideEpsilon);
        float baseHp = SEED.Mathf.Max(fish.BaseHp, DivideEpsilon);
        fishHpMax = baseHp + baseHp * fishShare;
        fishHp = fishHpMax;

        // 距離との対応付け: 掛かった距離を「基礎HP ぶんの距離」とみなす
        metersPerHp = SEED.Mathf.Max(hookDistance, hookDistanceMin) / baseHp;

        // 拍時計とパターンを魚データから作る
        SetupRhythm(fish);

        // 最初のフェーズ（出題）へ入る。時計は 0 から回り始める。
        EnterPhase(Phase.Call);

        InvalidateSegmentCache();
        ApplyUi();

        SEED.Debug.Log($"[Fight] 開始: {fish.DisplayName} / 総合力 {CurrentFishPower():F2} vs 竿 {rodPower:F2}"
                     + $" / 魚HP {fishHpMax:F1}（取り分 {fishShare:P0}）"
                     + $" / 掛かった距離 {hookDistance:F1}m → 目標 {DesiredFloatDistance:F1}m"
                     + $" / {BpmOf(fish):F0}BPM {beatsPerBar}拍子 / パターン {patterns.Count} 種"
                     + $" / 初期テンション {Tension:F2}");
    }

    /// <summary>
    /// バトルを終了する【終了の唯一の出口】。
    /// 釣り上げ成功・糸切れ・キャンセルのいずれからも呼ばれてよい（多重呼び出し安全）。
    /// </summary>
    public void EndFight()
    {
        ResetRuntimeState();
        HideUi();
    }

    /// <summary>
    /// バトルを 1 フレーム進める（コントローラがヒット中に毎フレーム呼ぶ）。
    ///
    /// 非アクティブなら何もしない。糸が切れたフレームで <see cref="LineBroken"/> が
    /// true になるので、呼び出し側はその場で糸切れ処理へ分岐すること
    /// （本スクリプトは状態遷移も魚の解放も行わない）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <param name="reelAmount">このフレームの巻き取り量（メートル）。隙のときだけ使う。</param>
    public void Tick(float deltaTime, float reelAmount)
    {
        if (!Active || target is null) { return; }

        // 一時停止中（わらしべ連鎖のアタリ受付中）は時計も値も一切進めない。
        if (Paused)
        {
            ApplyUi();
            return;
        }

        clockTime += deltaTime;

        UpdateMetronome();
        UpdatePhaseTransition();
        UpdateTiredTimer();

        switch (CurrentPhase)
        {
            case Phase.Call:
                UpdateCall();
                break;

            case Phase.Answer:
                UpdateAnswer();
                break;

            case Phase.Rest:
                UpdateRest(deltaTime, reelAmount);
                break;
        }

        UpdateFlashTimers(deltaTime);
        ApplyUi();
    }

    /// <summary>
    /// 待機（ひるみ）を与える API の名残【現仕様では効果なし】。
    ///
    /// 旧仕様（暴れ／ステイの状態機械）で漂流物アイテムの隙を作るために用意していたが、
    /// リズム版では魚の行動が拍で決まるため<b>意味を持たない</b>。
    /// 呼び出し側（漂流物は未実装）を壊さないよう、空実装として残してある。
    /// リズム版で隙を作る手段を足すときは、疲労を直接足す形へ作り替えるのが筋。
    /// </summary>
    /// <param name="seconds">与える待機の秒数（現仕様では無視される）。</param>
    public void ApplyFlinch(float seconds)
    {
        // 現仕様では何もしない（引数は互換のために受けるだけ）。
    }

    /// <summary>
    /// 残り距離（ウキ→竿先の水平距離）の表示を更新する。
    /// コントローラがヒット中に毎フレーム呼ぶ（非アクティブなら非表示にする）。
    /// </summary>
    /// <param name="meters">残り距離（メートル）。</param>
    public void UpdateDistanceDisplay(float meters)
    {
        if (distanceText is not { } label || !label.IsValid) { return; }

        if (!Active)
        {
            label.Color = label.Color.WithAlpha(0f);
            return;
        }

        label.Content = $"{SEED.Mathf.Max(meters, 0f):F1}m";
        label.Color = label.Color.WithAlpha(SEED.Mathf.Clamped01(distanceTextOpacity));
    }

    /// <summary>
    /// このフレームにウキ→竿先の距離を動かす量（メートル・符号つき）を返す
    /// 【ウキの移動量の唯一の算出点】。
    ///
    /// ＋ ＝ 沖へ（距離が増える） / − ＝ 手元へ（距離が減る）。
    /// <b>隙（Rest）のあいだだけ</b>動かす。出題・回答中はウキを止めておき、
    /// 拍を読む画面が揺れないようにする。
    /// </summary>
    /// <param name="currentDistance">現在のウキ→竿先の水平距離（メートル）。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <returns>距離の増減量（＋ が沖／− が手元）。</returns>
    public float ComputeFloatDistanceStep(float currentDistance, float deltaTime)
    {
        if (!Active || Paused || target is null) { return 0f; }
        if (CurrentPhase != Phase.Rest) { return 0f; }

        float difference = DesiredFloatDistance - currentDistance;

        // 目標のほうが遠い: 魚が沖へ引く（戦闘力比ぶん速い）
        if (difference > 0f)
        {
            float outward = fishPullSpeed * PullRateClamped() * deltaTime;
            return SEED.Mathf.Min(outward, difference);
        }

        // 目標のほうが近い: 手元へ寄せる（上限速度でクランプ・目標は追い越さない）
        float inward = reelInSpeedMax * deltaTime;
        return -SEED.Mathf.Min(inward, -difference);
    }

    // ─── 内部処理: リズムデータの取り込み ───────────────────

    /// <summary>
    /// 魚のリズムデータ（BPM・拍子・パターン）を取り込む【拍時計の初期化の唯一の入口】。
    /// パターン文字列は長さと文字を検証し、壊れているものは捨てて 1 度だけ警告する。
    /// 有効なパターンが 1 つも無ければ「各拍の頭を叩くだけ」の安全なパターンを合成する。
    /// </summary>
    /// <param name="fish">掛かった魚。</param>
    private void SetupRhythm(Fish fish)
    {
        beatsPerBar = SEED.Mathf.Max(fish.RhythmBeatsPerBar, MinBeatsPerBar);
        subsPerBar = beatsPerBar * SubdivisionsPerBeat;
        secondsPerBeat = SecondsPerMinute / SEED.Mathf.Max(BpmOf(fish), MinBpm);
        secondsPerSub = secondsPerBeat / SubdivisionsPerBeat;

        patterns.Clear();
        int invalidCount = 0;
        // 魚データ側のリストが未設定（null）でも落ちないようにしてから検証する
        foreach (var raw in fish.RhythmPatterns ?? new List<string>())
        {
            if (IsValidPattern(raw)) { patterns.Add(raw.ToLowerInvariant()); }
            else { invalidCount++; }
        }

        if (invalidCount > 0)
        {
            SEED.Debug.LogWarning($"[Fight] {fish.DisplayName} のリズムパターンに無効な行が {invalidCount} 件あります"
                                + $"（1 行 = {subsPerBar} 文字・'{PatternHitChar}' か '{PatternRestChar}' のみ）");
        }

        if (patterns.Count == 0) { patterns.Add(BuildFallbackPattern()); }
    }

    /// <summary>魚の BPM（0 以下なら下限へクランプ）。</summary>
    /// <param name="fish">対象の魚。</param>
    private float BpmOf(Fish fish) => SEED.Mathf.Max(fish.RhythmBpm, MinBpm);

    /// <summary>
    /// パターン文字列が有効か（長さが 1 小節の分割数と一致し、打点／休符だけで出来ているか）。
    /// </summary>
    /// <param name="pattern">検証する文字列。</param>
    private bool IsValidPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) { return false; }
        if (pattern.Length != subsPerBar) { return false; }

        foreach (char c in pattern)
        {
            char lower = char.ToLowerInvariant(c);
            if (lower != PatternHitChar && lower != PatternRestChar) { return false; }
        }
        return true;
    }

    /// <summary>
    /// 有効なパターンが 1 つも無いときに使う安全なパターン（各拍の頭だけを叩く）。
    /// </summary>
    private string BuildFallbackPattern()
    {
        var buffer = new System.Text.StringBuilder(subsPerBar);
        for (int i = 0; i < subsPerBar; i++)
        {
            buffer.Append(i % SubdivisionsPerBeat == 0 ? PatternHitChar : PatternRestChar);
        }
        return buffer.ToString();
    }

    // ─── 内部処理: 拍時計とフェーズ ─────────────────────────

    /// <summary>
    /// メトロノームを進める【拍の音を鳴らす唯一の出口】。
    /// 拍番号が変わったフレームで 1 回だけ鳴らし、小節頭だけ音量を上げる。
    /// </summary>
    private void UpdateMetronome()
    {
        if (string.IsNullOrEmpty(metronomeSePath)) { return; }

        int beat = BeatIndex;
        if (beat == lastBeatPlayed) { return; }

        lastBeatPlayed = beat;
        bool barHead = beatsPerBar > 0 && beat % beatsPerBar == 0;
        SEED.Audio.Play(metronomeSePath, SEED.Mathf.Clamped01(barHead ? metronomeBarHeadVolume : metronomeVolume));
    }

    /// <summary>
    /// フェーズの切り替えと予告を行う【フェーズ遷移の唯一の集約点】。
    /// 切り替えは必ず小節頭（フェーズ長がすべて小節単位なので時刻で判定できる）。
    /// </summary>
    private void UpdatePhaseTransition()
    {
        // 1 拍前の予告（UI のテキスト表示で使う）
        if (!nextPhaseAnnounced && clockTime >= phaseEndTime - secondsPerBeat)
        {
            nextPhaseAnnounced = true;
        }

        if (clockTime < phaseEndTime) { return; }

        EnterPhase(NextPhase(CurrentPhase));
    }

    /// <summary>フェーズの巡回順（出題 → 回答 → 隙 → 出題 …）。</summary>
    /// <param name="phase">現在のフェーズ。</param>
    private static Phase NextPhase(Phase phase) => phase switch
    {
        Phase.Call => Phase.Answer,
        Phase.Answer => Phase.Rest,
        _ => Phase.Call,
    };

    /// <summary>
    /// フェーズへ入る【フェーズ開始処理の唯一の入口】。
    /// 開始時刻は「いまの小節頭」に合わせるので、遷移は常に小節頭で揃う。
    /// </summary>
    /// <param name="next">入るフェーズ。</param>
    private void EnterPhase(Phase next)
    {
        // 回答フェーズを抜けるときは、叩かれなかった打点をすべて Miss として締める
        if (CurrentPhase == Phase.Answer) { FailRemainingHits(); }

        CurrentPhase = next;
        phaseBars = PhaseBarsOf(next);
        phaseStartTime = CurrentBarStartTime();
        phaseEndTime = phaseStartTime + phaseBars * SecondsPerBar;
        nextPhaseAnnounced = false;
        lastCueSub = NoCueFired;

        if (next == Phase.Call)
        {
            // 出題ごとにパターンを引き直す
            currentPattern = patterns[SEED.Random.Range(0, patterns.Count)];
        }
        else if (next == Phase.Answer)
        {
            BuildExpectedHits();
        }

        SEED.Debug.Log($"[Fight] {PhaseLabel(next)}（{phaseBars}小節）"
                     + $" / テンション {Tension:P0} / 疲労 {Fatigue01:P0}{(IsTired ? "（疲労中）" : "")}"
                     + $" / 魚HP {FishHp01:P0}");
    }

    /// <summary>いまの小節が始まった時刻（秒）。フェーズの開始時刻を小節頭へ揃えるのに使う。</summary>
    private float CurrentBarStartTime()
    {
        float barSeconds = SecondsPerBar;
        if (barSeconds <= DivideEpsilon) { return clockTime; }
        return SEED.Mathf.Floor(clockTime / barSeconds) * barSeconds;
    }

    /// <summary>
    /// フェーズの長さ（小節）。魚データの指定（1 以上）があればそれを、
    /// 無ければバトル側の既定値を使う。隙は疲労中に延びる。
    /// </summary>
    /// <param name="phase">対象のフェーズ。</param>
    private int PhaseBarsOf(Phase phase)
    {
        int fromFish = target is { } fish
            ? phase switch
            {
                Phase.Call => fish.RhythmCallBars,
                Phase.Answer => fish.RhythmAnswerBars,
                Phase.Rest => fish.RhythmRestBars,
                _ => UseFightDefaultBars,
            }
            : UseFightDefaultBars;

        int fallback = phase switch
        {
            Phase.Call => callBars,
            Phase.Answer => answerBars,
            _ => restBars,
        };

        int bars = SEED.Mathf.Max(fromFish > UseFightDefaultBars ? fromFish : fallback, MinPhaseBars);
        if (phase == Phase.Rest && IsTired) { bars += SEED.Mathf.Max(restBarsWhenTired, 0); }
        return bars;
    }

    /// <summary>フェーズの表示名。</summary>
    /// <param name="phase">対象のフェーズ。</param>
    private static string PhaseLabel(Phase phase) => phase switch
    {
        Phase.Call => "出題",
        Phase.Answer => "回答",
        Phase.Rest => "隙",
        _ => "―",
    };

    // ─── 内部処理: 出題 ───────────────────────────────────

    /// <summary>
    /// 出題フェーズの更新【打点キューを出す唯一の出口】。
    ///
    /// フェーズ内の分割番号が進むたびに、その位置がパターンの打点なら
    /// コントローラへ「前アタリと同じ演出」を依頼する（つつき音＋ウキの沈み）。
    /// 出題中のクリックは Miss として扱う（テンションが上がる）。
    /// </summary>
    private void UpdateCall()
    {
        int sub = CurrentPhaseSub();
        if (sub > lastCueSub)
        {
            // 1 フレームで複数の分割をまたいだ場合も取りこぼさない
            for (int s = lastCueSub + 1; s <= sub; s++)
            {
                if (IsHitSub(s)) { FishingController.Current?.PlayNibbleCue(); }
            }
            lastCueSub = sub;
        }

        // 出題中に叩いたら Miss（お手つき）
        if (ReadTapDown()) { ApplyMiss(); }
    }

    /// <summary>フェーズ開始からの分割番号（8 分音符単位・0 始まり）。</summary>
    private int CurrentPhaseSub()
        => secondsPerSub > DivideEpsilon
            ? SEED.Mathf.FloorToInt((clockTime - phaseStartTime) / secondsPerSub)
            : 0;

    /// <summary>フェーズ内の分割番号が、いまのパターンの打点にあたるか。</summary>
    /// <param name="phaseSub">フェーズ開始からの分割番号。</param>
    private bool IsHitSub(int phaseSub)
    {
        if (phaseSub < 0 || currentPattern.Length != subsPerBar) { return false; }
        return currentPattern[phaseSub % subsPerBar] == PatternHitChar;
    }

    // ─── 内部処理: 回答 ───────────────────────────────────

    /// <summary>
    /// 回答フェーズで期待する打点の一覧を作る【期待打点の唯一の生成点】。
    /// 出題と同じパターンを、回答フェーズの各小節へ並べる。
    /// </summary>
    private void BuildExpectedHits()
    {
        ClearExpectedHits();
        if (currentPattern.Length != subsPerBar) { return; }

        for (int bar = 0; bar < phaseBars; bar++)
        {
            for (int s = 0; s < subsPerBar; s++)
            {
                if (currentPattern[s] != PatternHitChar) { continue; }

                int phaseSub = bar * subsPerBar + s;
                expectedTimes.Add(phaseStartTime + phaseSub * secondsPerSub);
                expectedSubs.Add(phaseSub);
                expectedJudged.Add(false);
                expectedResults.Add(FishingController.HookJudgement.None);
                expectedFlash.Add(0f);
            }
        }
    }

    /// <summary>期待打点の一覧を空にする。</summary>
    private void ClearExpectedHits()
    {
        expectedTimes.Clear();
        expectedSubs.Clear();
        expectedJudged.Clear();
        expectedResults.Clear();
        expectedFlash.Clear();
    }

    /// <summary>
    /// 回答フェーズの更新【判定の唯一の集約点】。
    ///
    /// 1. クリックがあれば、まだ判定していない打点のうち<b>最も近い</b>ものへ結び付ける
    ///    （<see cref="niceSeconds"/> 以内に無ければ空打ち＝Miss）
    /// 2. 受付窓（打点 ＋ <see cref="niceSeconds"/>）を過ぎた打点は Miss として締める
    /// </summary>
    private void UpdateAnswer()
    {
        if (ReadTapDown())
        {
            int index = FindNearestPendingHit();
            if (index >= 0) { JudgeHit(index, clockTime - expectedTimes[index]); }
            else { ApplyMiss(); }
        }

        // 受付窓を過ぎた打点は打ち逃し
        for (int i = 0; i < expectedTimes.Count; i++)
        {
            if (expectedJudged[i]) { continue; }
            if (clockTime <= expectedTimes[i] + niceSeconds) { continue; }

            MarkHitResult(i, FishingController.HookJudgement.Miss);
            ApplyMiss();
        }
    }

    /// <summary>
    /// いまのクリックに結び付けるべき打点の添字を返す（無ければ <see cref="NoIndex"/>）。
    /// 未判定かつ受付窓（<see cref="niceSeconds"/>）以内で、時間差が最も小さいものを選ぶ。
    /// </summary>
    private int FindNearestPendingHit()
    {
        int best = NoIndex;
        float bestOffset = niceSeconds;

        for (int i = 0; i < expectedTimes.Count; i++)
        {
            if (expectedJudged[i]) { continue; }

            float offset = SEED.Mathf.Abs(clockTime - expectedTimes[i]);
            if (offset > bestOffset) { continue; }

            bestOffset = offset;
            best = i;
        }
        return best;
    }

    /// <summary>
    /// 打点 1 つを判定して、テンション・疲労・表示へ反映する。
    /// </summary>
    /// <param name="index">期待打点の添字。</param>
    /// <param name="signedOffset">時間差（＋ ＝ 遅い / − ＝ 早い、秒）。</param>
    private void JudgeHit(int index, float signedOffset)
    {
        float offset = SEED.Mathf.Abs(signedOffset);
        var judgement =
            offset <= excellentSeconds ? FishingController.HookJudgement.Excellent :
            offset <= greatSeconds ? FishingController.HookJudgement.Great :
            FishingController.HookJudgement.Nice;

        MarkHitResult(index, judgement);

        // テンション: Excellent だけは減り、それ以外は「ズレの大きさ × 効き」だけ増える
        if (judgement == FishingController.HookJudgement.Excellent)
        {
            Tension = SEED.Mathf.Max(Tension - SEED.Mathf.Max(excellentTensionRelief, 0f), TensionMin);
        }
        else
        {
            AddTension(offset * tensionPerSecondOfOffset * LevelScale());
        }

        // 疲労: きれいに叩けたほど大きく溜まる
        AddFatigue(judgement switch
        {
            FishingController.HookJudgement.Excellent => fatigueExcellent,
            FishingController.HookJudgement.Great => fatigueGreat,
            _ => fatigueNice,
        });

        FishingController.Current?.ShowFightJudgement(judgement, signedOffset);
    }

    /// <summary>期待打点へ判定結果を書き込み、点灯タイマーを開始する。</summary>
    /// <param name="index">期待打点の添字。</param>
    /// <param name="judgement">判定結果。</param>
    private void MarkHitResult(int index, FishingController.HookJudgement judgement)
    {
        expectedJudged[index] = true;
        expectedResults[index] = judgement;
        expectedFlash[index] = SEED.Mathf.Max(beatFlashSeconds, 0f);
    }

    /// <summary>
    /// 回答フェーズを抜けるときに、叩かれずに残った打点をすべて Miss で締める。
    /// （受付窓の判定で拾い切れなかった端の打点を取りこぼさないための保険）
    /// </summary>
    private void FailRemainingHits()
    {
        for (int i = 0; i < expectedTimes.Count; i++)
        {
            if (expectedJudged[i]) { continue; }

            MarkHitResult(i, FishingController.HookJudgement.Miss);
            ApplyMiss();
        }
    }

    /// <summary>
    /// Miss（打ち逃し・空打ち・出題中のお手つき）の共通処理【Miss の唯一の適用点】。
    /// テンションを上げ、判定画像（Miss）を出す。
    /// </summary>
    private void ApplyMiss()
    {
        AddTension(SEED.Mathf.Max(missTension, 0f));
        FishingController.Current?.ShowFightJudgement(FishingController.HookJudgement.Miss, 0f);
    }

    /// <summary>左クリックの押下をこのフレームに読んだか（叩き入力の唯一の入口）。</summary>
    private static bool ReadTapDown() => SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left);

    // ─── 内部処理: 隙（巻き取り）─────────────────────────────

    /// <summary>
    /// 隙フェーズの更新。テンションが回復し、巻き取りで魚 HP を削れる唯一の区間。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <param name="reelAmount">このフレームの巻き取り量（メートル）。</param>
    private void UpdateRest(float deltaTime, float reelAmount)
    {
        // テンションの回復（隙のあいだだけ）
        Tension = SEED.Mathf.Max(Tension - SEED.Mathf.Max(tensionRecoverPerSec, 0f) * deltaTime, TensionMin);

        // 巻き取り: 巻いた距離ぶんだけ魚 HP を削る
        if (reelAmount <= ReelInputEpsilon) { return; }
        fishHp = SEED.Mathf.Max(fishHp - reelAmount * ReelHpPerUnit, FishHpZero);
    }

    // ─── 内部処理: テンションと疲労 ─────────────────────────

    /// <summary>
    /// テンションを増やす【増加の唯一の適用点】。
    /// 上限に達したら糸切れ（<see cref="LineBroken"/>）を立てて効果音を鳴らす。
    /// </summary>
    /// <param name="amount">増分（負なら何もしない）。</param>
    private void AddTension(float amount)
    {
        if (amount <= 0f) { return; }

        Tension = SEED.Mathf.Min(Tension + amount, TensionMax);
        if (Tension < TensionMax) { return; }
        if (LineBroken) { return; }             // 既に通知済みなら二重に鳴らさない

        LineBroken = true;
        PlayLineBreakSe();
    }

    /// <summary>
    /// 疲労を溜める【疲労状態へ入る唯一の入口】。
    /// 満タンに達したら <see cref="tiredBars"/> 小節のあいだ疲労状態にする。
    /// </summary>
    /// <param name="amount">増分（負なら何もしない）。</param>
    private void AddFatigue(float amount)
    {
        if (amount <= 0f || IsTired) { return; }

        Fatigue01 = SEED.Mathf.Min(Fatigue01 + amount, FatigueMax);
        if (Fatigue01 < FatigueMax) { return; }

        IsTired = true;
        tiredEndBar = BarIndex + SEED.Mathf.Max(tiredBars, MinPhaseBars);
        SEED.Debug.Log($"[Fight] 魚が疲労した（{SEED.Mathf.Max(tiredBars, MinPhaseBars)}小節・巻き効率 ×{tiredReelBonus:F2}）");
    }

    /// <summary>疲労状態の残り小節を見て、期間が終わったら疲労を 0 へリセットする。</summary>
    private void UpdateTiredTimer()
    {
        if (!IsTired || BarIndex < tiredEndBar) { return; }

        IsTired = false;
        Fatigue01 = 0f;
        SEED.Debug.Log("[Fight] 魚が疲労から復帰した");
    }

    /// <summary>点灯中の拍マークのタイマーを進める。</summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateFlashTimers(float deltaTime)
    {
        for (int i = 0; i < expectedFlash.Count; i++)
        {
            if (expectedFlash[i] <= 0f) { continue; }
            expectedFlash[i] = SEED.Mathf.Max(expectedFlash[i] - deltaTime, 0f);
        }
    }

    // ─── 内部処理: 戦闘力 ──────────────────────────────────

    /// <summary>
    /// 魚の総合力 ＝ 基礎パワー × 大きさスコア。
    /// リズム版では状態倍率もスタミナ倍率も無い（暴れの状態機械を廃止したため）。
    /// 魚が居なければ竿パワーと同値（＝等価）を返す。
    /// </summary>
    private float CurrentFishPower()
        => target is { } fish ? fish.BasePower * SizeScore(fish) : rodPower;

    /// <summary>
    /// 大きさスコア。個体差 <see cref="Fish.SizeMultiplier"/>（既定 0.8〜1.3）を
    /// <see cref="sizeScoreMin"/>〜<see cref="sizeScoreMax"/>（既定 0.9〜1.1）へ線形写像する。
    /// </summary>
    /// <param name="fish">対象の魚。</param>
    private float SizeScore(Fish fish)
    {
        float t = SEED.Mathf.InverseLerp(sizeMultiplierRefMin, sizeMultiplierRefMax, fish.SizeMultiplier);
        return SEED.Mathf.Lerp(sizeScoreMin, sizeScoreMax, t);
    }

    /// <summary>
    /// テンションのレベル補正 ＝ 1 + <see cref="tensionLevelScale"/> × (魚 ÷ 竿 − 1)。
    /// 下限は <see cref="LevelScaleMin"/>（格下の魚でも判定が無意味にならないように）。
    /// </summary>
    private float LevelScale()
    {
        float ratio = CurrentFishPower() / SEED.Mathf.Max(rodPower, DivideEpsilon);
        float scaled = NeutralMultiplier + tensionLevelScale * (ratio - EquivalentPowerRatio);
        return SEED.Mathf.Max(scaled, LevelScaleMin);
    }

    /// <summary>
    /// ウキを沖へ引く速度の倍率（魚 ÷ 竿）。上下限でクランプする。
    /// </summary>
    private float PullRateClamped()
    {
        float ratio = CurrentFishPower() / SEED.Mathf.Max(rodPower, DivideEpsilon);
        return SEED.Mathf.Clamped(ratio, pullRateMultiplierMin, pullRateMultiplierMax);
    }

    /// <summary>安全帯（緑の円弧）の幅（テンション換算 0〜1）。糸パワーで広がる。</summary>
    private float SafeZoneWidth()
    {
        float raw = safeZoneBase + safeZonePerLinePower * (linePower - 1f);
        return SEED.Mathf.Clamped01(raw);
    }

    /// <summary>合わせ判定に対応する初期テンション（判定が無ければ最も不利な Nice 値）。</summary>
    /// <param name="judge">合わせ判定。</param>
    private float InitialTension(FishingController.HookJudgement judge) => judge switch
    {
        FishingController.HookJudgement.Excellent => initialTensionExcellent,
        FishingController.HookJudgement.Great => initialTensionGreat,
        _ => initialTensionNice,
    };

    /// <summary>糸切れの効果音を鳴らす（パス未設定なら何もしない）。</summary>
    private void PlayLineBreakSe()
    {
        if (string.IsNullOrEmpty(lineBreakSePath)) { return; }
        SEED.Audio.Play(lineBreakSePath, lineBreakSeVolume);
    }

    /// <summary>実行時の状態をすべて初期値へ戻す（開始前・終了後の共通処理）。</summary>
    private void ResetRuntimeState()
    {
        Active = false;
        Paused = false;                // 一時停止の持ち越しを防ぐ
        LineBroken = false;
        CurrentPhase = Phase.None;
        Tension = TensionMin;
        Fatigue01 = 0f;
        IsTired = false;
        tiredEndBar = 0;
        fishHp = 0f;
        fishHpMax = 0f;
        metersPerHp = 0f;
        target = null;

        clockTime = 0f;
        lastBeatPlayed = NoBeatPlayed;
        lastCueSub = NoCueFired;
        phaseStartTime = 0f;
        phaseEndTime = 0f;
        phaseBars = MinPhaseBars;
        nextPhaseAnnounced = false;
        currentPattern = "";
        patterns.Clear();
        ClearExpectedHits();
    }

    // ─── UI ─────────────────────────────────────────────

    /// <summary>
    /// UI をバトル中の見た目へ更新する。
    /// セグメントの配置は個数が変わったときだけ組み直し、色は差分があるものだけ書き換える。
    /// </summary>
    private void ApplyUi()
    {
        LayoutSegments();
        ApplySegmentColors();
        ApplyMarker();
        ApplyStatusText();
    }

    /// <summary>
    /// セグメントを円周へ等間隔に並べる（個数が変わらないかぎり 1 度だけ）。
    /// セグメント i の角度 ＝ i ÷ 個数 × 360 度（真上が 0・右回り）。
    /// </summary>
    private void LayoutSegments()
    {
        int count = segmentSprites.Count;
        if (count <= 0 || count == cachedSegmentCount) { return; }

        for (int i = 0; i < count; i++)
        {
            float degrees = SegmentDegrees(i, count);

            if (i < segmentTransforms.Count)
            {
                var tf = segmentTransforms[i];
                if (tf.IsValid)
                {
                    tf.Position = ArcPoint(degrees);
                    tf.Rotation = degrees;
                }
            }

            var sprite = segmentSprites[i];
            if (sprite.IsValid) { sprite.Size = new SEED.Vector2(segmentWidthPx, segmentHeightPx); }
        }

        cachedSegmentCount = count;

        // 色のキャッシュを個数に合わせて張り直す（次の描画で必ず全数書き込まれる）
        cachedSegmentColors.Clear();
        for (int i = 0; i < count; i++)
        {
            cachedSegmentColors.Add(new SEED.Color(0f, 0f, 0f, UncachedAlpha));
        }
    }

    /// <summary>
    /// セグメントの色を決めて書き込む【円の描画の唯一の出口】。
    ///
    /// 下地はテンションの円弧（真上から右回りに <see cref="Tension"/> × 360 度ぶん）。
    /// その上に、いまのパターンの打点位置へ拍マークを重ねる。
    /// 値が変わっていないセグメントには書き込まない（毎フレーム 48 回の書き込みを避ける）。
    /// </summary>
    private void ApplySegmentColors()
    {
        int count = segmentSprites.Count;
        if (count <= 0) { return; }

        float alpha = SEED.Mathf.Clamped01(segmentOpacity);
        float zone = SafeZoneWidth();
        float filled = SEED.Mathf.Clamped01(Tension) * count;

        for (int i = 0; i < count; i++)
        {
            // 下地: テンションの円弧（i 番目が円弧の内側なら帯色、外なら空き色）
            float t = count > 1 ? (float)i / count : 0f;
            SEED.Color color = i < filled
                ? TensionBandColor(t, zone, alpha)
                : ToColor(emptyColor, alpha);

            // 上書き: 拍マーク
            if (BeatMarkColor(i, count, alpha) is { } mark) { color = mark; }

            if (i < cachedSegmentColors.Count && SameColor(cachedSegmentColors[i], color)) { continue; }

            var sprite = segmentSprites[i];
            if (sprite.IsValid) { sprite.Color = color; }
            if (i < cachedSegmentColors.Count) { cachedSegmentColors[i] = color; }
        }
    }

    /// <summary>
    /// テンションの円弧の帯色。安全帯の内側は緑、外側は 警告色 → 危険色 へ線形補間する。
    /// </summary>
    /// <param name="t">円周上の位置（0＝真上／1＝一周）。テンション換算と同じ尺度。</param>
    /// <param name="zone">安全帯の幅（0〜1）。</param>
    /// <param name="alpha">不透明度。</param>
    private SEED.Color TensionBandColor(float t, float zone, float alpha)
    {
        if (t <= zone) { return ToColor(safeColor, alpha); }

        float u = SEED.Mathf.Clamped01((t - zone) / SEED.Mathf.Max(1f - zone, DivideEpsilon));
        return SEED.Color.Lerp(ToColor(warnColor, alpha), ToColor(dangerColor, alpha), u);
    }

    /// <summary>
    /// セグメント <paramref name="index"/> に重ねる拍マークの色（重ねないなら null）。
    ///
    /// - 出題中 … いまのパターンの打点位置を明るく光らせる
    /// - 回答中 … 同じ位置を暗く出し、判定した瞬間だけ判定色で光らせる
    /// </summary>
    /// <param name="index">セグメントの添字。</param>
    /// <param name="count">セグメントの総数。</param>
    /// <param name="alpha">不透明度。</param>
    private SEED.Color? BeatMarkColor(int index, int count, float alpha)
    {
        if (currentPattern.Length != subsPerBar) { return null; }
        if (CurrentPhase is not (Phase.Call or Phase.Answer)) { return null; }
        if (SubIndexAt(index, count) is not { } sub) { return null; }
        if (currentPattern[sub] != PatternHitChar) { return null; }

        if (CurrentPhase == Phase.Call) { return ToColor(beatCallColor, alpha); }

        // 回答中: 同じ小節内位置の打点で、いま点灯しているものがあれば判定色を優先する
        for (int i = 0; i < expectedSubs.Count; i++)
        {
            if (expectedSubs[i] % subsPerBar != sub) { continue; }
            if (expectedFlash[i] <= 0f) { continue; }

            return ToColor(
                expectedResults[i] == FishingController.HookJudgement.Miss ? beatMissColor : beatHitColor,
                alpha);
        }

        return ToColor(beatAnswerColor, alpha);
    }

    /// <summary>
    /// セグメント <paramref name="index"/> がちょうど小節内の分割位置に重なるなら、その分割番号。
    /// 重ならなければ null（＝拍マークを置かない）。
    /// </summary>
    /// <param name="index">セグメントの添字。</param>
    /// <param name="count">セグメントの総数。</param>
    private int? SubIndexAt(int index, int count)
    {
        if (subsPerBar <= 0 || count <= 0) { return null; }
        if (count % subsPerBar != 0) { return null; }        // 割り切れない構成では拍マークを置かない

        int step = count / subsPerBar;
        return index % step == 0 ? index / step : null;
    }

    /// <summary>
    /// マーカー（小節内の進行を示す針）を更新する。
    /// 角度 ＝ <see cref="BarPhase01"/> × 360 度、色はフェーズ（疲労中は専用色）で決まる。
    /// </summary>
    private void ApplyMarker()
    {
        float degrees = BarPhase01 * FullCircleDegrees;

        if (gaugeMarkerTransform is { } markerTf && markerTf.IsValid)
        {
            markerTf.Position = ArcPoint(degrees);
            markerTf.Rotation = degrees;
        }

        if (gaugeMarker is not { } marker || !marker.IsValid) { return; }

        SEED.Vector3 rgb = IsTired
            ? markerTiredColor
            : CurrentPhase switch
            {
                Phase.Call => markerCallColor,
                Phase.Answer => markerAnswerColor,
                _ => markerRestColor,
            };
        marker.Color = ToColor(rgb, SEED.Mathf.Clamped01(gaugeMarkerOpacity));
    }

    /// <summary>
    /// 円の中心テキスト（フェーズ名＋予告／魚 HP ％／疲労 ％）を更新する。
    /// </summary>
    private void ApplyStatusText()
    {
        if (hpText is not { } label || !label.IsValid) { return; }

        string phaseName = IsTired ? "疲労中" : PhaseLabel(CurrentPhase);
        string notice = nextPhaseAnnounced ? $" → {PhaseLabel(NextPhase(CurrentPhase))}" : string.Empty;

        label.Content = $"{phaseName}{notice}\n"
                      + $"魚 {SEED.Mathf.RoundToInt(FishHp01 * PercentScale)}%"
                      + $"  疲労 {SEED.Mathf.RoundToInt(Fatigue01 * PercentScale)}%";
        label.Color = label.Color.WithAlpha(SEED.Mathf.Clamped01(hpTextOpacity));
    }

    /// <summary>セグメント <paramref name="index"/> の角度（度・真上が 0・右回り）。</summary>
    /// <param name="index">セグメントの添字。</param>
    /// <param name="count">セグメントの総数。</param>
    private static float SegmentDegrees(int index, int count)
        => count > 0 ? (float)index / count * FullCircleDegrees : 0f;

    /// <summary>
    /// 円周上の点（キャンバス座標）を返す。
    /// 角度 0 が円の頂点（真上）で、＋ が右回り。
    /// キャンバスの Y は<b>下向き</b>なので、上方向は −cos になる。
    /// </summary>
    /// <param name="degrees">頂点からの角度（度。＋ が右回り）。</param>
    private SEED.Vector2 ArcPoint(float degrees)
    {
        float rad = degrees * SEED.Mathf.Deg2Rad;
        return new SEED.Vector2(SEED.Mathf.Sin(rad) * arcRadiusPx, -SEED.Mathf.Cos(rad) * arcRadiusPx);
    }

    /// <summary>RGB の Vector3 と不透明度から <see cref="SEED.Color"/> を作る。</summary>
    /// <param name="rgb">RGB（0〜1）。</param>
    /// <param name="alpha">不透明度（0〜1）。</param>
    private static SEED.Color ToColor(SEED.Vector3 rgb, float alpha)
        => new SEED.Color(rgb.x, rgb.y, rgb.z, alpha);

    /// <summary>2 つの色が（差分更新の観点で）同じとみなせるか。</summary>
    /// <param name="a">色 A。</param>
    /// <param name="b">色 B。</param>
    private static bool SameColor(SEED.Color a, SEED.Color b)
        => SEED.Mathf.Approximately(a.r, b.r)
        && SEED.Mathf.Approximately(a.g, b.g)
        && SEED.Mathf.Approximately(a.b, b.b)
        && SEED.Mathf.Approximately(a.a, b.a);

    /// <summary>次の描画でセグメントを必ず組み直させる。</summary>
    private void InvalidateSegmentCache()
    {
        cachedSegmentCount = 0;
        cachedSegmentColors.Clear();
    }

    /// <summary>UI をすべて隠す【非表示の唯一の出口】。</summary>
    private void HideUi()
    {
        for (int i = 0; i < segmentSprites.Count; i++)
        {
            ApplySpriteOpacity(segmentSprites[i], 0f);
        }
        ApplySpriteOpacity(gaugeMarker, 0f);

        if (hpText is { } hp && hp.IsValid) { hp.Color = hp.Color.WithAlpha(0f); }
        if (distanceText is { } dist && dist.IsValid) { dist.Color = dist.Color.WithAlpha(0f); }

        // 次に表示するときは必ず配置・着色し直す（隠すためにアルファを潰しているため）
        InvalidateSegmentCache();
    }

    /// <summary>スプライトのアルファだけを書き換える（RGB はシーン／計算で入れた色を保つ）。</summary>
    /// <param name="sprite">対象スプライト（未設定可）。</param>
    /// <param name="opacity">不透明度（0〜1 へクランプする）。</param>
    private static void ApplySpriteOpacity(SEED.Sprite? sprite, float opacity)
    {
        if (sprite is not { } s || !s.IsValid) { return; }
        s.Color = s.Color.WithAlpha(SEED.Mathf.Clamped01(opacity));
    }
}
