using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// ヒット後の「魚とのやり取り（釣りバトル）」を司るスクリプト。
///
/// <b>プレイヤーアクタに 3 本目のスクリプトスロット「Fight」として付ける</b>
/// （<see cref="FishingController"/> と同じアクタ。コントローラの
/// <c>fight</c> フィールドから参照される）。
///
/// <b>単一責任</b>
/// 本スクリプトが持つのは「テンションゲージ」「糸 HP」「魚の暴れ度」「その UI 表示」だけ。
/// ウキの移動・状態遷移・魚の解放は <see cref="FishingController"/> の責務で、
/// 本スクリプトは<b>毎フレーム値を進めて結果（<see cref="LineBroken"/> /
/// <see cref="FloatDragSpeed"/>）を返すだけ</b>。自前の Update は持たず、
/// すべてコントローラ側から <see cref="Tick"/> で駆動される
/// （ヒット中だけ進む＝実行順の曖昧さを持ち込まないため）。
///
/// ────────────────────────────────────────────────────────
/// <b>仕様（2026-09-03 確定）</b>
///
/// ■ 失敗条件
/// 糸 HP（内部値）が 0 になると糸が切れて釣り失敗。
///
/// ■ テンションゲージ（−1 〜 +1 の正規化値）
/// - 中央（|gauge| ≦ 回復区間の半幅）… 糸 HP が<b>回復</b>する安全帯
/// - 両端に寄るほど糸 HP が<b>減少</b>する（端 ±1 で最大減少）
/// - <b>巻く</b>と ＋ 側へ、<b>操作しない</b>と − 側（魚が沖へ泳ぐ）へ動く
/// - ＋ 側に居るときは「操作しない」ことで、− 側に居るときは「巻く」ことで
///   中央（0）へ<b>回復</b>する。回復は上昇／下降より遅い固定速度
///   （<see cref="gaugeRecoverySpeed"/>）
///
/// ■ 糸パワー
/// 上げると回復区間の半幅（安全帯）が広がり、同時に UI の円弧の開き角も広がる。
/// 投げるごとにリセットされる想定（強化漂流物は未実装なのでインスペクタ値がそのまま使われる）。
///
/// ■ 増減速度（戦闘力の比較）
/// 装備と魚の戦闘力を同じ単位で算出し、その<b>比</b>でゲージの動く速さを決める。
/// - 等価（魚＝竿）… 上昇／下降速度 ＝ 回復速度（既定値がそう並べてある）
/// - 魚が強い     … 差が大きいほど急激に動く（＝厳しい）
/// - 竿が強い     … 差が大きいほど変化量が少ない（＝楽）
///
/// ■ 戦闘力
/// - 魚   ＝ 基礎パワー × 大きさスコア × 暴れ度
///   （大きさスコアは個体差 <see cref="Fish.SizeMultiplier"/> を 0.9〜1.1 へ写像。
///    暴れ度は暴れると 1.5・ひるむと 0.2・規定 1）
/// - 装備 ＝ 竿パワー 1 本（強化は将来）
///
/// ■ 合わせランクの影響
/// 合わせが悪いほど初期テンションゲージが ＋ 側（危険側）へ寄る。
/// ────────────────────────────────────────────────────────
///
/// <b>UI（ファミリーフィッシング風の円形テンションゲージ）</b>
/// 画面中央に円弧のゲージを描く。ゲージ値 0 が円の<b>頂点（真上）</b>で、
/// ＋ が右回り・− が左回り。円弧の開き角（表示半角）は糸パワーで広がる。
///
/// Sprite には UV 矩形（円弧の描き分け）が無いので、
/// <b>白テクスチャを着色した小さなセグメントスプライトを N 個並べて円弧を作る</b>
/// （シーンの FishingUI 直下に <c>GaugeSeg00</c>… という名前で並べ、
///  <see cref="segmentSprites"/> / <see cref="segmentTransforms"/> へ順に割り当てる）。
/// セグメントの配置・色・表示は、表示半角か安全帯の幅が変わったときだけ計算し直す
/// （毎フレーム 48 個を触らないための差分更新）。
/// </summary>
public class FishingFight : SEEDScript
{
    // ─── 定数（内部計算の下駄・ゼロ割回避）─────────────────────

    /// <summary>ゼロ割回避に使う微小値。</summary>
    private const float DivideEpsilon = 0.0001f;

    /// <summary>ゲージの下限値（− 側の端）。</summary>
    private const float GaugeMin = -1f;

    /// <summary>ゲージの上限値（＋ 側の端）。</summary>
    private const float GaugeMax = 1f;

    /// <summary>ゲージ中央（回復目標）。</summary>
    private const float GaugeCenter = 0f;

    /// <summary>「巻いている」とみなす巻き取り量のしきい値（メートル）。</summary>
    private const float ReelInputEpsilon = 0.0001f;

    /// <summary>戦闘力の基準比（魚 ÷ 竿 がこの値なら「等価」）。</summary>
    private const float EquivalentPowerRatio = 1f;

    /// <summary>暴れ度の規定値（<see cref="Fish"/> 側の仕様と同じ。1 が標準）。</summary>
    private const float DefaultRampage = 1f;

    /// <summary>円弧の開き角の上限（半角・度）。ここまで開くと全円になる。</summary>
    private const float ArcHalfAngleLimit = 180f;

    /// <summary>割合（0〜1）をパーセント表示へ直す係数。</summary>
    private const float PercentScale = 100f;

    /// <summary>キャッシュ判定に使う「変化なし」とみなす許容差。</summary>
    private const float CacheEpsilon = 0.001f;

    /// <summary>キャッシュ未計算を表す番兵値（実値として現れない負の大きな値）。</summary>
    private const float UncachedSentinel = -9999f;

    // ─── 装備パラメータ ───────────────────────────────────

    /// <summary>
    /// 竿パワー。魚の戦闘力と<b>同じ単位</b>で比較され、ゲージの動く速さを決める。
    /// 大きいほど楽になる（＝ゲージがゆっくり動く）。
    /// </summary>
    [Header("装備"), SerializeField(Label = "竿パワー")]
    private float rodPower = 1f;

    /// <summary>
    /// 糸パワー。大きいほど回復区間（安全帯）と円弧の開き角が広がる。
    /// 投げるごとにリセットされる想定（強化は将来のフィールド要素）。
    /// </summary>
    [SerializeField(Label = "糸パワー")]
    private float linePower = 1f;

    // ─── 糸 HP ───────────────────────────────────────────

    /// <summary>糸 HP の最大値（内部値。0 になると糸が切れる）。</summary>
    [Header("糸HP"), SerializeField(Label = "糸HPの最大値")]
    private float lineHpMax = 100f;

    /// <summary>
    /// ゲージが端（|gauge| = 1）に居るときの糸 HP 減少速度（/秒）。
    /// 回復区間の外側での減少量は「区間からの食み出し具合」で線形にスケールする。
    /// </summary>
    [SerializeField(Label = "端での糸HP減少速度(/秒)")]
    private float hpDrainPerSecondAtEdge = 25f;

    /// <summary>回復区間（安全帯）の中に居るときの糸 HP 回復速度（/秒）。</summary>
    [SerializeField(Label = "安全帯での糸HP回復速度(/秒)")]
    private float hpRecoverPerSecond = 10f;

    // ─── テンションゲージ ─────────────────────────────────

    /// <summary>
    /// 回復区間の半幅の基準値（糸パワー 1 のときの値）。
    /// |gauge| がこの値以下なら糸 HP が回復する。
    /// </summary>
    [Header("テンションゲージ"), SerializeField(Label = "回復区間の半幅(基準)")]
    private float recoveryZoneHalfWidthBase = 0.25f;

    /// <summary>糸パワー 1 あたりに広がる回復区間の半幅。</summary>
    [SerializeField(Label = "糸パワー1あたりの半幅増分")]
    private float recoveryZonePerLinePower = 0.1f;

    /// <summary>回復区間の半幅の下限（これ以下には狭まらない）。</summary>
    [SerializeField(Label = "回復区間の半幅の下限")]
    private float recoveryZoneHalfWidthMin = 0.05f;

    /// <summary>回復区間の半幅の上限（これ以上には広がらない）。</summary>
    [SerializeField(Label = "回復区間の半幅の上限")]
    private float recoveryZoneHalfWidthMax = 0.9f;

    /// <summary>
    /// ゲージが中央（0）へ戻る速度（/秒・固定）。
    /// ＋ 側では「操作しない」、− 側では「巻く」ことでこの速度が適用される。
    /// </summary>
    [SerializeField(Label = "ゲージの回復速度(/秒)")]
    private float gaugeRecoverySpeed = 0.4f;

    /// <summary>
    /// ゲージの上昇／下降速度の基準値（/秒）。
    /// 魚と竿の戦闘力が<b>等価</b>のときにそのまま使われる。
    /// 既定値が <see cref="gaugeRecoverySpeed"/> と同じなのは、仕様の
    /// 「等価 ＝ 回復速度と上昇／下降速度が同じ」を数値で表現しているため。
    /// </summary>
    [SerializeField(Label = "ゲージの基準速度(/秒)")]
    private float gaugeBaseRate = 0.4f;

    /// <summary>
    /// 戦闘力の差がゲージ速度へ効く強さ。
    ///
    /// 速度倍率 ＝ 1 + (魚の戦闘力 ÷ 竿パワー − 1) × 本値。
    /// - 本値 0   … 戦闘力差を無視（常に基準速度）
    /// - 本値 1   … 速度倍率がそのまま戦闘力比（既定）
    /// - 本値 &gt; 1 … 差がより誇張される
    /// いずれの値でも「等価なら倍率 1（＝基準速度＝回復速度）」は保たれる。
    /// </summary>
    [SerializeField(Label = "戦闘力差の効き")]
    private float powerDiffRateScale = 1f;

    /// <summary>ゲージ速度倍率の下限（竿が強すぎても止まらないようにする）。</summary>
    [SerializeField(Label = "ゲージ速度倍率の下限")]
    private float rateMultiplierMin = 0.25f;

    /// <summary>ゲージ速度倍率の上限（魚が強すぎても即死にならないようにする）。</summary>
    [SerializeField(Label = "ゲージ速度倍率の上限")]
    private float rateMultiplierMax = 3f;

    // ─── 合わせランクによる初期ゲージ ─────────────────────────

    /// <summary>Excellent で合わせたときの初期ゲージ値。</summary>
    [Header("初期ゲージ(合わせランク)"), SerializeField(Label = "初期ゲージ(Excellent)")]
    private float initialGaugeExcellent = 0f;

    /// <summary>Great で合わせたときの初期ゲージ値。</summary>
    [SerializeField(Label = "初期ゲージ(Great)")]
    private float initialGaugeGreat = 0.25f;

    /// <summary>
    /// Nice で合わせたときの初期ゲージ値。
    /// 判定が取れていない場合（None など）のフォールバックにも使う（＝最も不利な値）。
    /// </summary>
    [SerializeField(Label = "初期ゲージ(Nice)")]
    private float initialGaugeNice = 0.5f;

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

    // ─── 暴れ度 ──────────────────────────────────────────

    /// <summary>暴れているあいだの暴れ度倍率（仕様: 1.5）。</summary>
    [Header("暴れ度"), SerializeField(Label = "暴れ中の倍率")]
    private float rageMultiplier = 1.5f;

    /// <summary>ひるんでいるあいだの暴れ度倍率（仕様: 0.2）。</summary>
    [SerializeField(Label = "ひるみ中の倍率")]
    private float flinchMultiplier = 0.2f;

    /// <summary>
    /// 暴れ出すまでの間隔の下限（秒）。
    /// 実際の間隔は魚の暴れ度（<see cref="Fish.Rampage"/>）で<b>割られる</b>ので、
    /// 暴れ度が高い魚ほど頻繁に暴れる。
    /// </summary>
    [SerializeField(Label = "暴れ間隔の下限(秒)")]
    private float rageIntervalMin = 3f;

    /// <summary>暴れ出すまでの間隔の上限（秒）。魚の暴れ度で割られる。</summary>
    [SerializeField(Label = "暴れ間隔の上限(秒)")]
    private float rageIntervalMax = 7f;

    /// <summary>1 回の暴れが続く秒数の下限。</summary>
    [SerializeField(Label = "暴れ時間の下限(秒)")]
    private float rageDurationMin = 0.8f;

    /// <summary>1 回の暴れが続く秒数の上限。</summary>
    [SerializeField(Label = "暴れ時間の上限(秒)")]
    private float rageDurationMax = 1.8f;

    /// <summary>暴れ終わりに「ひるみ」へ移行する確率（0〜1）。</summary>
    [SerializeField(Label = "ひるみに移る確率")]
    private float flinchChance = 0.3f;

    /// <summary>1 回のひるみが続く秒数の下限。</summary>
    [SerializeField(Label = "ひるみ時間の下限(秒)")]
    private float flinchDurationMin = 0.5f;

    /// <summary>1 回のひるみが続く秒数の上限。</summary>
    [SerializeField(Label = "ひるみ時間の上限(秒)")]
    private float flinchDurationMax = 1.2f;

    // ─── ウキの引き（魚が沖へ引く力）───────────────────────────

    /// <summary>
    /// 巻いていないあいだに魚がウキを沖へ引く速度の基準値（m/秒）。
    /// 実際の速度は「魚の戦闘力 ÷ 竿パワー」を掛けた値になる
    /// （倍率は <see cref="rateMultiplierMin"/>〜<see cref="rateMultiplierMax"/> でクランプ）。
    /// </summary>
    [Header("ウキの引き"), SerializeField(Label = "魚の引き速度(m/秒)")]
    private float fishPullSpeed = 1.5f;

    // ─── 効果音 ──────────────────────────────────────────

    /// <summary>糸が切れた瞬間に鳴らす効果音のアセットパス（空なら鳴らさない）。</summary>
    [Header("効果音"), SerializeField(Label = "糸切れの効果音")]
    private string lineBreakSePath = "";

    /// <summary>糸切れ効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "糸切れの音量")]
    private float lineBreakSeVolume = 1f;

    // ─── UI 参照 ─────────────────────────────────────────

    /// <summary>
    /// 円弧を構成するセグメントのスプライト（<c>GaugeSeg00</c>… を順に割り当てる）。
    /// 個数はそのまま円弧の分割数になる。未設定でもロジックは成立する。
    /// </summary>
    [Header("UI 参照"), SerializeField(Label = "円弧セグメントのSprite")]
    private List<SEED.Sprite> segmentSprites = new();

    /// <summary>
    /// 円弧セグメントの CanvasTransform（位置と回転を書き換える）。
    /// <see cref="segmentSprites"/> と<b>同じ順・同じアクタ</b>を割り当てること。
    /// </summary>
    [SerializeField(Label = "円弧セグメントのCanvasTransform")]
    private List<SEED.CanvasTransform> segmentTransforms = new();

    /// <summary>現在のテンションを示すマーカーのスプライト（白い小片）。</summary>
    [SerializeField(Label = "マーカーのSprite")]
    private SEED.Sprite? gaugeMarker = null;

    /// <summary>
    /// マーカーの CanvasTransform（円周上の位置を毎フレーム書き換える）。
    /// <see cref="gaugeMarker"/> と<b>同じアクタ</b>を割り当てること。
    /// </summary>
    [SerializeField(Label = "マーカーのCanvasTransform")]
    private SEED.CanvasTransform? gaugeMarkerTransform = null;

    /// <summary>円の中心に出す糸 HP のテキスト（"HP 87%" 形式）。</summary>
    [SerializeField(Label = "糸HPのText")]
    private SEED.Text? hpText = null;

    /// <summary>画面右下に出す残り距離のテキスト（"9.6m" 形式）。</summary>
    [SerializeField(Label = "残り距離のText")]
    private SEED.Text? distanceText = null;

    // ─── UI レイアウト ────────────────────────────────────

    /// <summary>円弧の半径（ピクセル）。マーカーとセグメントの配置半径。</summary>
    [Header("UI レイアウト"), SerializeField(Label = "円弧の半径(px)")]
    private float arcRadiusPx = 140f;

    /// <summary>
    /// 円弧の表示半角の基準値（度・糸パワー 1 のとき）。
    /// 180 度で全円になる。
    /// </summary>
    [SerializeField(Label = "円弧の表示半角(基準・度)")]
    private float arcHalfAngleBase = 100f;

    /// <summary>糸パワー 1 あたりに広がる円弧の表示半角（度）。</summary>
    [SerializeField(Label = "糸パワー1あたりの半角増分(度)")]
    private float arcHalfAnglePerLinePower = 30f;

    /// <summary>セグメントの幅（ピクセル）。円周方向の長さ。</summary>
    [SerializeField(Label = "セグメントの幅(px)")]
    private float segmentWidthPx = 16f;

    /// <summary>セグメントの高さ（ピクセル）。半径方向の太さ。</summary>
    [SerializeField(Label = "セグメントの高さ(px)")]
    private float segmentHeightPx = 10f;

    /// <summary>安全帯（回復区間）のセグメント色（RGB）。</summary>
    [SerializeField(Label = "安全帯の色(RGB)")]
    private SEED.Vector3 safeColor = new SEED.Vector3(0.2f, 0.9f, 0.3f);

    /// <summary>危険帯の入口の色（RGB）。安全帯のすぐ外側。</summary>
    [SerializeField(Label = "警告帯の色(RGB)")]
    private SEED.Vector3 warnColor = new SEED.Vector3(1f, 0.85f, 0.2f);

    /// <summary>円弧の端（最も危険）の色（RGB）。</summary>
    [SerializeField(Label = "危険帯の色(RGB)")]
    private SEED.Vector3 dangerColor = new SEED.Vector3(1f, 0.2f, 0.15f);

    /// <summary>セグメントの不透明度（バトル中）。</summary>
    [SerializeField(Label = "セグメントの不透明度")]
    private float segmentOpacity = 0.9f;

    /// <summary>マーカーの不透明度（バトル中）。</summary>
    [SerializeField(Label = "マーカーの不透明度")]
    private float gaugeMarkerOpacity = 1f;

    /// <summary>糸 HP テキストの不透明度（バトル中）。</summary>
    [SerializeField(Label = "糸HPテキストの不透明度")]
    private float hpTextOpacity = 1f;

    /// <summary>残り距離テキストの不透明度（バトル中）。</summary>
    [SerializeField(Label = "残り距離テキストの不透明度")]
    private float distanceTextOpacity = 1f;

    // ─── 実行時の状態 ─────────────────────────────────────

    /// <summary>バトル進行中か（<see cref="BeginFight"/> 〜 <see cref="EndFight"/>）。</summary>
    public bool Active { get; private set; } = false;

    /// <summary>現在のテンションゲージ（−1 〜 +1）。</summary>
    public float Gauge { get; private set; } = 0f;

    /// <summary>糸 HP の割合（0〜1）。UI と外部表示用。</summary>
    public float LineHp01 => lineHpMax > DivideEpsilon
        ? SEED.Mathf.Clamped01(lineHp / lineHpMax)
        : 0f;

    /// <summary>
    /// 糸が切れたか。HP が 0 に達したフレームで true になる。
    /// コントローラ側が拾ったら <see cref="EndFight"/> で false へ戻る。
    /// </summary>
    public bool LineBroken { get; private set; } = false;

    /// <summary>
    /// このフレームに魚がウキを沖へ引く速度（m/秒）。
    /// 巻いているあいだは 0、巻いていないあいだは戦闘力比に応じた引き速度。
    /// </summary>
    public float FloatDragSpeed { get; private set; } = 0f;

    /// <summary>糸切れの効果音パス（コントローラ側から鳴らす場合の参照用）。</summary>
    public string LineBreakSePath => lineBreakSePath;

    /// <summary>糸切れの効果音の音量。</summary>
    public float LineBreakSeVolume => lineBreakSeVolume;

    /// <summary>現在の糸 HP（内部値）。</summary>
    private float lineHp = 0f;

    /// <summary>戦っている魚（null ＝ 非戦闘中）。位置・状態は一切触らず、パラメータだけ読む。</summary>
    private Fish? target = null;

    /// <summary>魚の暴れ度の基準値（インスタンスは差し替わり得るので毎回読む）。</summary>
    private float TargetRampage => target is { } fish ? fish.Rampage : DefaultRampage;

    /// <summary>暴れ／ひるみの進行状態。</summary>
    private enum RampageState
    {
        /// <summary>平常（暴れ度は規定値）。</summary>
        Calm,

        /// <summary>暴れている（暴れ度 × <see cref="rageMultiplier"/>）。</summary>
        Rage,

        /// <summary>ひるんでいる（暴れ度 × <see cref="flinchMultiplier"/>）。</summary>
        Flinch,
    }

    /// <summary>現在の暴れ状態。</summary>
    private RampageState rampageState = RampageState.Calm;

    /// <summary>現在の暴れ状態が終わるまでの残り秒数（Calm なら次に暴れ出すまでの残り）。</summary>
    private float rampageTimer = 0f;

    /// <summary>
    /// セグメントの配置・色を最後に計算したときの表示半角（度）。
    /// これと安全帯の幅が変わらないあいだは 48 個のセグメントに触らない。
    /// </summary>
    private float cachedArcHalfAngle = UncachedSentinel;

    /// <summary>セグメントの配置・色を最後に計算したときの安全帯の半幅。</summary>
    private float cachedZoneHalfWidth = UncachedSentinel;

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
    /// 糸 HP を満タンにし、初期テンションゲージを<b>合わせランク</b>から決める
    /// （ランクが悪いほど ＋ 側＝危険側から始まる）。暴れの抽選も開始する。
    /// </summary>
    /// <param name="fish">掛かった魚（パラメータを読むだけで一切動かさない）。</param>
    /// <param name="judge">合わせ判定（初期ゲージの決定に使う）。</param>
    public void BeginFight(Fish fish, FishingController.HookJudgement judge)
    {
        target = fish;
        Active = true;
        LineBroken = false;
        FloatDragSpeed = 0f;
        lineHp = SEED.Mathf.Max(lineHpMax, 0f);
        Gauge = SEED.Mathf.Clamped(InitialGauge(judge), GaugeMin, GaugeMax);

        // 暴れの抽選をリセットし、最初の「暴れ出すまで」を引く
        rampageState = RampageState.Calm;
        rampageTimer = NextRageInterval();

        // 隠していたぶんセグメントは必ず作り直す（キャッシュを無効化する）
        InvalidateArcCache();
        ApplyUi();

        SEED.Debug.Log($"[Fight] 開始: {fish.DisplayName} / 戦闘力 {CurrentFishPower():F2} vs 竿 {rodPower:F2}"
                     + $" / 初期ゲージ {Gauge:F2} / 安全帯 ±{RecoveryZoneHalfWidth():F2}"
                     + $" / 円弧 ±{ArcHalfAngle():F0}度");
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
    /// <param name="reelAmount">このフレームの巻き取り量（メートル）。0 なら「操作していない」。</param>
    public void Tick(float deltaTime, float reelAmount)
    {
        if (!Active || target is null) { return; }

        bool reeling = reelAmount > ReelInputEpsilon;

        UpdateRampage(deltaTime);
        UpdateGauge(deltaTime, reeling);
        UpdateLineHp(deltaTime);

        // ウキを沖へ引く速度。巻いているあいだは引かれない（＝プレイヤーが勝っている）。
        FloatDragSpeed = reeling ? 0f : fishPullSpeed * PowerRatioClamped();

        ApplyUi();
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

    // ─── 内部処理: 戦闘力 ──────────────────────────────────

    /// <summary>
    /// 現在の魚の戦闘力 ＝ 基礎パワー × 大きさスコア × 暴れ度倍率。
    /// 魚が居なければ竿パワーと同値（＝等価）を返し、速度が暴れないようにする。
    /// </summary>
    private float CurrentFishPower()
    {
        if (target is not { } fish) { return rodPower; }
        return fish.BasePower * SizeScore(fish) * CurrentRampageValue();
    }

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
    /// 現在の暴れ度 ＝ 魚の暴れ度（規定 1）× 状態倍率
    /// （暴れ中 1.5 / ひるみ中 0.2 / 平常 1）。
    /// </summary>
    private float CurrentRampageValue() => rampageState switch
    {
        RampageState.Rage => TargetRampage * rageMultiplier,
        RampageState.Flinch => TargetRampage * flinchMultiplier,
        _ => TargetRampage,
    };

    /// <summary>
    /// ゲージ速度の倍率 ＝ 1 + (魚の戦闘力 ÷ 竿パワー − 1) × <see cref="powerDiffRateScale"/>。
    /// <see cref="rateMultiplierMin"/>〜<see cref="rateMultiplierMax"/> でクランプする。
    ///
    /// 等価（比 1）なら倍率 1 ＝ 基準速度 ＝ 既定では回復速度と同じ、
    /// 魚が強いほど大きく（急激）、竿が強いほど小さく（緩やか）なる。
    /// </summary>
    private float PowerRatioClamped()
    {
        float ratio = CurrentFishPower() / SEED.Mathf.Max(rodPower, DivideEpsilon);
        float scaled = EquivalentPowerRatio + (ratio - EquivalentPowerRatio) * powerDiffRateScale;
        return SEED.Mathf.Clamped(scaled, rateMultiplierMin, rateMultiplierMax);
    }

    /// <summary>このフレームのゲージ上昇／下降速度（/秒）。</summary>
    private float GaugeRate() => gaugeBaseRate * PowerRatioClamped();

    /// <summary>
    /// 回復区間（安全帯）の半幅。糸パワーが 1 を超えた分だけ広がる。
    /// 下限・上限でクランプするので、極端な糸パワーでも破綻しない。
    /// </summary>
    private float RecoveryZoneHalfWidth()
    {
        float raw = recoveryZoneHalfWidthBase + recoveryZonePerLinePower * (linePower - 1f);
        return SEED.Mathf.Clamped(raw, recoveryZoneHalfWidthMin, recoveryZoneHalfWidthMax);
    }

    /// <summary>
    /// 円弧の表示半角（度）。糸パワーが高いほど広がり、180 度で全円になる。
    /// </summary>
    private float ArcHalfAngle()
    {
        float raw = arcHalfAngleBase + arcHalfAnglePerLinePower * (linePower - 1f);
        return SEED.Mathf.Clamped(raw, 0f, ArcHalfAngleLimit);
    }

    // ─── 内部処理: 暴れ度の進行 ────────────────────────────

    /// <summary>
    /// 暴れ／ひるみのタイマーを進める。
    /// 平常 →（間隔経過）→ 暴れ →（暴れ時間経過）→ 確率でひるみ／平常 → …
    /// 間隔は魚の暴れ度で割られるので、暴れ度が高い魚ほど頻繁に暴れる。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateRampage(float deltaTime)
    {
        rampageTimer -= deltaTime;
        if (rampageTimer > 0f) { return; }

        switch (rampageState)
        {
            case RampageState.Calm:
                // 平常の待ち時間が尽きたので暴れ出す
                rampageState = RampageState.Rage;
                rampageTimer = SEED.Random.Range(rageDurationMin, rageDurationMax);
                break;

            case RampageState.Rage:
                // 暴れ終わり。確率でひるみへ、外れたら平常へ戻って次の暴れを待つ
                if (SEED.Random.Value < flinchChance)
                {
                    rampageState = RampageState.Flinch;
                    rampageTimer = SEED.Random.Range(flinchDurationMin, flinchDurationMax);
                }
                else
                {
                    rampageState = RampageState.Calm;
                    rampageTimer = NextRageInterval();
                }
                break;

            default:
                // ひるみ終わり。平常へ戻して次の暴れを待つ
                rampageState = RampageState.Calm;
                rampageTimer = NextRageInterval();
                break;
        }
    }

    /// <summary>
    /// 次に暴れ出すまでの秒数を抽選する。
    /// 魚の暴れ度（規定 1）で割るので、暴れ度 2 の魚は約 2 倍の頻度で暴れる。
    /// </summary>
    private float NextRageInterval()
    {
        float raw = SEED.Random.Range(rageIntervalMin, rageIntervalMax);
        return raw / SEED.Mathf.Max(TargetRampage, DivideEpsilon);
    }

    // ─── 内部処理: ゲージと糸 HP ───────────────────────────

    /// <summary>
    /// テンションゲージを 1 フレーム進める。
    ///
    /// - 巻いている   … ＋ 側へ上昇。ただし − 側に居るあいだは中央へ<b>回復</b>
    /// - 操作していない… − 側へ下降。ただし ＋ 側に居るあいだは中央へ<b>回復</b>
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <param name="reeling">このフレームに巻いているか。</param>
    private void UpdateGauge(float deltaTime, bool reeling)
    {
        float rate = GaugeRate();
        float recovery = gaugeRecoverySpeed * deltaTime;

        if (reeling)
        {
            // 低（−）側に居るなら「巻く」ことで中央へ回復。中央〜＋ 側なら張りが増す。
            Gauge = Gauge < GaugeCenter
                ? SEED.Mathf.MoveTowards(Gauge, GaugeCenter, recovery)
                : Gauge + rate * deltaTime;
        }
        else
        {
            // 高（＋）側に居るなら「操作しない」ことで中央へ回復。中央〜− 側なら魚が沖へ泳ぐ。
            Gauge = Gauge > GaugeCenter
                ? SEED.Mathf.MoveTowards(Gauge, GaugeCenter, recovery)
                : Gauge - rate * deltaTime;
        }

        Gauge = SEED.Mathf.Clamped(Gauge, GaugeMin, GaugeMax);
    }

    /// <summary>
    /// 糸 HP を 1 フレーム進める。
    /// 安全帯の中なら回復、外なら「食み出し具合（0〜1）」に比例して減少する。
    /// 0 に達したら <see cref="LineBroken"/> を立てる（状態遷移は呼び出し側の責務）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateLineHp(float deltaTime)
    {
        float zone = RecoveryZoneHalfWidth();
        float outside = SEED.Mathf.Abs(Gauge) - zone;

        if (outside <= 0f)
        {
            // 安全帯の中: 回復
            lineHp = SEED.Mathf.Min(lineHp + hpRecoverPerSecond * deltaTime, lineHpMax);
            return;
        }

        // 安全帯の外: 端（|gauge| = 1）で最大になるよう線形にスケールした減少
        float severity = SEED.Mathf.Clamped01(outside / SEED.Mathf.Max(GaugeMax - zone, DivideEpsilon));
        lineHp -= hpDrainPerSecondAtEdge * severity * deltaTime;

        if (lineHp > 0f) { return; }

        lineHp = 0f;
        if (LineBroken) { return; }         // 既に通知済みなら二重に鳴らさない

        LineBroken = true;
        PlayLineBreakSe();
    }

    /// <summary>糸切れの効果音を鳴らす（パス未設定なら何もしない）。</summary>
    private void PlayLineBreakSe()
    {
        if (string.IsNullOrEmpty(lineBreakSePath)) { return; }
        SEED.Audio.Play(lineBreakSePath, lineBreakSeVolume);
    }

    /// <summary>合わせ判定に対応する初期ゲージ値（判定が無ければ最も不利な Nice 値）。</summary>
    /// <param name="judge">合わせ判定。</param>
    private float InitialGauge(FishingController.HookJudgement judge) => judge switch
    {
        FishingController.HookJudgement.Excellent => initialGaugeExcellent,
        FishingController.HookJudgement.Great => initialGaugeGreat,
        _ => initialGaugeNice,
    };

    /// <summary>実行時の状態をすべて初期値へ戻す（開始前・終了後の共通処理）。</summary>
    private void ResetRuntimeState()
    {
        Active = false;
        LineBroken = false;
        FloatDragSpeed = 0f;
        Gauge = GaugeCenter;
        lineHp = 0f;
        target = null;
        rampageState = RampageState.Calm;
        rampageTimer = 0f;
    }

    // ─── UI ─────────────────────────────────────────────

    /// <summary>
    /// UI をバトル中の見た目へ更新する。
    /// 円弧（セグメント）は表示半角・安全帯が変わったときだけ組み直し、
    /// マーカーとテキストだけを毎フレーム更新する。
    /// </summary>
    private void ApplyUi()
    {
        float halfAngle = ArcHalfAngle();
        float zone = RecoveryZoneHalfWidth();

        // 円弧: 形が変わったときだけ 全セグメントを配置・着色し直す
        if (SEED.Mathf.Abs(halfAngle - cachedArcHalfAngle) > CacheEpsilon
         || SEED.Mathf.Abs(zone - cachedZoneHalfWidth) > CacheEpsilon)
        {
            RebuildArc(halfAngle, zone);
            cachedArcHalfAngle = halfAngle;
            cachedZoneHalfWidth = zone;
        }

        // マーカー: ゲージ値（−1〜1）を角度 θ ＝ gauge × 表示半角 へ写して円周上へ置く
        ApplySpriteOpacity(gaugeMarker, gaugeMarkerOpacity);
        if (gaugeMarkerTransform is { } markerTf && markerTf.IsValid)
        {
            float degrees = Gauge * halfAngle;
            markerTf.Position = ArcPoint(degrees);
            markerTf.Rotation = degrees;
        }

        // 糸 HP: 円の中心にパーセント表示
        if (hpText is { } label && label.IsValid)
        {
            label.Content = $"HP {SEED.Mathf.RoundToInt(LineHp01 * PercentScale)}%";
            label.Color = label.Color.WithAlpha(SEED.Mathf.Clamped01(hpTextOpacity));
        }
    }

    /// <summary>
    /// 円弧のセグメントを配置・着色し直す。
    ///
    /// セグメント i は「全円を等分した固定の角度」に居て、
    /// その角度が表示半角の内側なら表示、外側なら不透明度 0 で消える
    /// （＝糸パワーを上げると円弧が伸びていくように見える）。
    /// 色は「安全帯の内側なら緑、外側は角度に応じて 黄 → 赤」。
    /// </summary>
    /// <param name="halfAngle">現在の表示半角（度）。</param>
    /// <param name="zone">現在の安全帯の半幅（ゲージ空間 0〜1）。</param>
    private void RebuildArc(float halfAngle, float zone)
    {
        int count = segmentSprites.Count;
        if (count <= 0) { return; }

        // 分割は「全円（±ArcHalfAngleLimit）」に対して行う。
        // 分母は count-1（両端を含めるため）。1 個しか無い場合は頂点だけに置く。
        float step = count > 1 ? (ArcHalfAngleLimit * 2f) / (count - 1) : 0f;

        for (int i = 0; i < count; i++)
        {
            float degrees = count > 1 ? -ArcHalfAngleLimit + step * i : 0f;

            // 位置と回転（表示半角の外でも位置は入れておく。消えるのは色のアルファ）
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
            if (!sprite.IsValid) { continue; }

            sprite.Size = new SEED.Vector2(segmentWidthPx, segmentHeightPx);

            // 表示半角の外側は消す
            if (SEED.Mathf.Abs(degrees) > halfAngle || halfAngle <= DivideEpsilon)
            {
                sprite.Color = sprite.Color.WithAlpha(0f);
                continue;
            }

            // ゲージ空間での位置（0〜1）に直して帯の色を決める
            float t = SEED.Mathf.Clamped01(SEED.Mathf.Abs(degrees) / SEED.Mathf.Max(halfAngle, DivideEpsilon));
            sprite.Color = SegmentColor(t, zone);
        }
    }

    /// <summary>
    /// ゲージ空間の位置 <paramref name="t"/>（0＝中央 / 1＝端）に対応する帯の色。
    /// 安全帯の内側は緑、外側は 警告色 → 危険色 へ線形補間する。
    /// </summary>
    /// <param name="t">ゲージ空間での中央からの距離（0〜1）。</param>
    /// <param name="zone">安全帯の半幅（0〜1）。</param>
    private SEED.Color SegmentColor(float t, float zone)
    {
        float alpha = SEED.Mathf.Clamped01(segmentOpacity);

        if (t <= zone) { return ToColor(safeColor, alpha); }

        float u = SEED.Mathf.Clamped01((t - zone) / SEED.Mathf.Max(1f - zone, DivideEpsilon));
        return SEED.Color.Lerp(ToColor(warnColor, alpha), ToColor(dangerColor, alpha), u);
    }

    /// <summary>
    /// 円弧上の点（キャンバス座標）を返す。
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

    /// <summary>次の <see cref="ApplyUi"/> で円弧を必ず組み直させる。</summary>
    private void InvalidateArcCache()
    {
        cachedArcHalfAngle = UncachedSentinel;
        cachedZoneHalfWidth = UncachedSentinel;
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

        // 次に表示するときは必ず配置し直す（隠すためにアルファを潰しているため）
        InvalidateArcCache();
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
