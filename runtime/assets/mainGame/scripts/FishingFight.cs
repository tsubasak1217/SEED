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
/// <see cref="ComputeFloatDistanceStep"/>）を返すだけ</b>。自前の Update は持たず、
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
/// ■ 魚 HP（2026-09-06 改定：テクニックがあればどんなに強い魚でも釣れる）
/// 巻き取りは「力比べ」ではなく<b>魚 HP を削る作業</b>になった。
/// <code>
/// 掛かった瞬間の魚の総合力 p0 ＝ 基礎パワー × 大きさスコア（状態倍率 1・スタミナ満タン）
/// 魚の取り分 share    ＝ p0 ÷ (竿パワー ＋ p0)          … 99:1 なら約 0.01 / 1:99 なら約 0.99
/// ボーナス HP         ＝ 基礎HP × share
/// 魚HP最大            ＝ 基礎HP ＋ ボーナス HP
/// 1HP あたりの距離     ＝ 掛かった瞬間の距離 ÷ 基礎HP      （距離は hookDistanceMin で下限クランプ）
/// 目標距離            ＝ 現在の魚HP × 1HP あたりの距離
/// 巻き効率            ＝ 竿パワー ÷ (竿パワー ＋ 現在の魚の総合力)  … 等価で 0.5・格上でも 0 より大きい
/// 巻き 1m あたりの HP  ＝ 巻き効率 ÷ 1HP あたりの距離        （<see cref="ReelHpPerUnit"/>）
/// </code>
/// 巻けば<b>必ず</b>魚 HP が減る（＝どれだけ格上でも時間さえ掛ければ削り切れる）。
/// 一方、掛けた直後は 目標距離（魚HP最大 × 1HP あたりの距離）が現在の距離より<b>遠い</b>ので、
/// ウキはまず沖へ引かれていく（＝「沖へ持っていかれる」演出はこのボーナス HP の分）。
/// 魚 HP が 0 になった瞬間が釣り上げ成立。
///
/// ■ ウキの距離制御（<see cref="ComputeFloatDistanceStep"/>）
/// ウキ→竿先の水平距離を<b>目標距離へ寄せる</b>だけの単純な制御にした。
/// - 目標が現在より遠い … 魚が沖へ引く（<see cref="fishPullSpeed"/> × 戦闘力比）
/// - 目標が現在より近い … 手元へ寄る（上限 <see cref="reelInSpeedMax"/> m/秒）
/// どちらも目標を追い越さない。符号は ＋ が沖・− が手元。
///
/// ■ 暴れ＝回避すべき「攻撃」（2026-09-06 追加）
/// 暴れ／大暴れの<b>1 回につき 1 度だけ</b>、プレイヤーの操作が判定される。
/// <code>
/// ゲージが ＋ 側（0 を含む）で「巻いている」    → 回避失敗: ゲージ ＋= 押し込み量
/// ゲージが − 側で「巻いていない」               → 回避失敗: ゲージ −= 押し込み量
/// 押し込み量 ＝ 基準値 × (1 + 効き × (魚の総合力 ÷ 竿パワー − 1)) × (大暴れなら大暴れ倍率)
/// </code>
/// ゲージちょうど 0 は<b>＋ 側として扱う</b>（＝巻くほうが危険側。仕様として明示）。
/// 暴れが終わるまで失敗しなければ回避成功（ログのみ）。
///
/// ■ 増減速度（戦闘力の比較）
/// 装備と魚の戦闘力を同じ単位で算出し、その<b>比</b>でゲージの動く速さを決める。
/// - 等価（魚＝竿）… 上昇／下降速度 ＝ 回復速度（既定値がそう並べてある）
/// - 魚が強い     … 差が大きいほど急激に動く（＝厳しい）
/// - 竿が強い     … 差が大きいほど変化量が少ない（＝楽）
///
/// ■ 総合力（戦闘力）
/// - 魚   ＝ 基礎パワー × 大きさスコア × 状態倍率 × スタミナ倍率
///   （大きさスコアは個体差 <see cref="Fish.SizeMultiplier"/> を 0.9〜1.1 へ写像。
///    状態倍率は 暴れ 1.5 / 大暴れ 2.0 / ステイ 0.6 / 待機（ひるみ）0.2 で、
///    暴れ・大暴れは魚の暴れ度で (倍率 − 1) × 暴れ度 + 1 にスケールされる。
///    スタミナ倍率は Lerp(<see cref="staminaFactorMin"/>, 1, スタミナ割合)
///    ＝ スタミナが少ないほど総合力にマイナス倍率が掛かる）
/// - 装備 ＝ 竿パワー 1 本（強化は将来）
///
/// ■ 魚の行動（フローチャート仕様 2026-09-05・毎フレーム評価）
/// <code>
/// 待機タイマー &gt; 0 → 待機（漂流物で付与。未実装なので通常は通らない）
/// else スタミナ 0  → 疲労 ON → ステイ（回復）
/// else 疲労中      → 復帰しきい値未満ならステイ / 達したら疲労解除 → 暴れ
/// else             → 暴れ or 大暴れ（重み 70:30・継続時間はランダム。
///                     継続中はスタミナを消費し、切れたら次フレームでステイへ）
/// </code>
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

    /// <summary>押し込み量（暴れの攻撃）の下限。負の押し込み＝逆方向への救済にならないよう 0 で止める。</summary>
    private const float MinAttackPush = 0f;

    /// <summary>魚 HP の下限（これ以下で釣り上げ成立）。</summary>
    private const float FishHpZero = 0f;

    /// <summary>スタミナ倍率の上限（スタミナ満タン時の総合力倍率）。</summary>
    private const float StaminaFactorMax = 1f;

    /// <summary>状態倍率の基準値（1 ＝ 平常）。暴れ度によるスケールの基点にも使う。</summary>
    private const float NeutralMultiplier = 1f;

    /// <summary>
    /// 「中央付近の変動倍率」（<see cref="gaugeCenterRateScale"/>）の下限。
    /// 0 だと中央（gauge＝0）ちょうどで通常の増減速度が完全に 0 になり、
    /// ゲージが 0 から動けなくなってしまうためのガード。
    /// </summary>
    private const float GaugeCenterRateScaleMin = 0.001f;

    // ─── 装備パラメータ ───────────────────────────────────

    /// <summary>
    /// 竿パワー。魚の戦闘力と<b>同じ単位</b>で比較され、ゲージの動く速さを決める。
    /// 大きいほど楽になる（＝ゲージがゆっくり動く）。
    /// </summary>
    [Header("装備"), SerializeField(Label = "竿パワー")]
    private float rodPower = 10f;

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
    private float gaugeRecoverySpeed = 0.3f;

    /// <summary>
    /// ゲージの上昇／下降速度の基準値（/秒）。
    /// 魚と竿の戦闘力が<b>等価</b>のときにそのまま使われる。
    /// 既定値が <see cref="gaugeRecoverySpeed"/> と同じなのは、仕様の
    /// 「等価 ＝ 回復速度と上昇／下降速度が同じ」を数値で表現しているため。
    /// </summary>
    [SerializeField(Label = "ゲージの基準速度(/秒)")]
    private float gaugeBaseRate = 0.2f;

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
    private float powerDiffRateScale = 0.5f;

    /// <summary>ゲージ速度倍率の下限（竿が強すぎても止まらないようにする）。</summary>
    [SerializeField(Label = "ゲージ速度倍率の下限")]
    private float rateMultiplierMin = 0.25f;

    /// <summary>ゲージ速度倍率の上限（魚が強すぎても即死にならないようにする）。</summary>
    [SerializeField(Label = "ゲージ速度倍率の上限")]
    private float rateMultiplierMax = 2f;

    /// <summary>
    /// ゲージが中央（0）付近に居るときの「通常の増減速度」への倍率（下限側）。
    /// <see cref="AmplitudeScale"/> 参照。0 だと中央ちょうどで速度が 0 になり
    /// ゲージが動けなくなるため、<see cref="GaugeCenterRateScaleMin"/> で下限クランプする。
    ///
    /// <b>効く範囲</b>: <see cref="UpdateGauge"/> の「巻いていれば ＋、操作しなければ −」という
    /// <b>通常の増減</b>にのみ掛かる。中央への<b>回復</b>（<see cref="gaugeRecoverySpeed"/>）や
    /// 暴れの<b>攻撃による押し込み</b>（<see cref="AttackPush"/>）には掛からない
    /// （回復は常に一定速度で 0 へ届く必要があり、攻撃の押し込みは仕様通りの威力を保つため）。
    /// </summary>
    [SerializeField(Label = "中央付近の変動倍率(0で最小)")]
    private float gaugeCenterRateScale = 0.3f;

    /// <summary>
    /// <see cref="AmplitudeScale"/> の補間カーブの指数。
    /// 1 なら線形、1 より大きいと中央付近がより緩やかに、1 未満だと端に近い領域まで緩やかになる。
    /// </summary>
    [SerializeField(Label = "変動倍率カーブの指数")]
    private float gaugeRateCurvePower = 1f;

    /// <summary>
    /// 「巻いている」とみなす保持時間（秒）【マウスホイール入力の離散性を吸収する唯一のつまみ】。
    ///
    /// マウスホイールは巻き取り量を「ノッチが来たフレームだけ」離散的に渡してくる。
    /// これをそのまま <c>reelAmount &gt; 0</c> で「巻いている／いない」に使うと、
    /// ノッチの来ないフレーム（大半）が「操作していない」扱いになってしまい、
    /// − 側（巻くことでしか回復しない側）のゲージが回復できず沖側へ流れ続けるバグになる。
    /// そこで「ノッチが来たら本値の秒数だけ巻いている状態を保持する」ホールド式にして、
    /// 連続してホイールを回している限り <see cref="IsReeling"/> が途切れないようにする
    /// （＝糸 HP の減少に使う実巻き取り量そのものは離散のまま。ここで滑らかにするのは
    /// 「巻いている／いない」の状態判定だけ）。
    /// </summary>
    [SerializeField(Label = "巻き入力の保持時間(秒)")]
    private float reelHoldSeconds = 0.35f;

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

    // ─── 魚の行動（暴れ／大暴れ／スタミナ切れ／ひるみ）────────────

    /// <summary>
    /// 「暴れ」を引く重み。<see cref="bigRageWeight"/> との比で抽選される
    /// （既定 70 : 30 ＝ 暴れ 70% / 大暴れ 30%）。
    /// </summary>
    [Header("魚の行動"), SerializeField(Label = "暴れの重み")]
    private float rageWeight = 70f;

    /// <summary>「大暴れ」を引く重み（<see cref="rageWeight"/> との比で抽選）。</summary>
    [SerializeField(Label = "大暴れの重み")]
    private float bigRageWeight = 30f;

    /// <summary>1 回の「暴れ」が続く秒数の下限。</summary>
    [SerializeField(Label = "暴れ時間の下限(秒)")]
    private float rageDurationMin = 1.5f;

    /// <summary>1 回の「暴れ」が続く秒数の上限。</summary>
    [SerializeField(Label = "暴れ時間の上限(秒)")]
    private float rageDurationMax = 3f;

    /// <summary>1 回の「大暴れ」が続く秒数の下限。</summary>
    [SerializeField(Label = "大暴れ時間の下限(秒)")]
    private float bigRageDurationMin = 0.8f;

    /// <summary>1 回の「大暴れ」が続く秒数の上限。</summary>
    [SerializeField(Label = "大暴れ時間の上限(秒)")]
    private float bigRageDurationMax = 1.6f;

    /// <summary>「暴れ」中の状態倍率（仕様: 1.5）。魚の暴れ度でスケールされる。</summary>
    [SerializeField(Label = "暴れ中の倍率")]
    private float rageMultiplier = 1.5f;

    /// <summary>「大暴れ」中の状態倍率（仕様: 2.0）。魚の暴れ度でスケールされる。</summary>
    [SerializeField(Label = "大暴れ中の倍率")]
    private float bigRageMultiplier = 2f;

    /// <summary>「待機（ひるみ）」中の状態倍率（仕様: 0.2）。漂流物などで与えられる隙。</summary>
    [SerializeField(Label = "待機(ひるみ)中の倍率")]
    private float flinchMultiplier = 0.2f;

    /// <summary>「ステイ（スタミナ回復）」中の状態倍率（仕様: 0.6）。</summary>
    [SerializeField(Label = "ステイ中の倍率")]
    private float stayMultiplier = 0.6f;

    // ─── スタミナ ────────────────────────────────────────

    /// <summary>「暴れ」中に 1 秒あたり消費するスタミナ。</summary>
    [Header("スタミナ"), SerializeField(Label = "暴れのスタミナ消費(/秒)")]
    private float rageStaminaDrainPerSec = 12f;

    /// <summary>「大暴れ」中に 1 秒あたり消費するスタミナ。</summary>
    [SerializeField(Label = "大暴れのスタミナ消費(/秒)")]
    private float bigRageStaminaDrainPerSec = 25f;

    /// <summary>「ステイ」中に 1 秒あたり回復するスタミナ。</summary>
    [SerializeField(Label = "ステイのスタミナ回復(/秒)")]
    private float stayRecoverPerSec = 15f;

    /// <summary>
    /// 疲労状態から復帰する（再び暴れ出す）スタミナ割合（0〜1）。
    /// スタミナが 0 になると疲労状態に入り、この割合まで回復するまでステイし続ける。
    /// </summary>
    [SerializeField(Label = "疲労からの復帰しきい値(0〜1)")]
    private float recoverThreshold01 = 0.4f;

    /// <summary>
    /// スタミナが 0 のときに総合力へ掛かる倍率（仕様: スタミナが少ないほどマイナス倍率）。
    /// スタミナ満タンで 1.0、0 でこの値まで線形に落ちる。
    /// </summary>
    [SerializeField(Label = "スタミナ0時の総合力倍率")]
    private float staminaFactorMin = 0.5f;

    /// <summary>
    /// 魚の <see cref="Fish.Stamina"/> が 0 以下（未設定）のときに使うスタミナ最大値。
    /// </summary>
    [SerializeField(Label = "スタミナ最大値の既定値")]
    private float defaultStaminaMax = 100f;

    /// <summary>
    /// 魚のスタミナ値へ一律に掛ける倍率（バトルの長さを一括で調整するためのつまみ）。
    /// </summary>
    [SerializeField(Label = "スタミナ倍率")]
    private float staminaScale = 1f;

    // ─── ウキの距離制御（目標距離への追従）─────────────────────

    /// <summary>
    /// 目標距離が現在より<b>遠い</b>とき、魚がウキを沖へ引く速度の基準値（m/秒）。
    /// 実際の速度は「魚の戦闘力 ÷ 竿パワー」を掛けた値になる
    /// （倍率は <see cref="rateMultiplierMin"/>〜<see cref="rateMultiplierMax"/> でクランプ）。
    /// </summary>
    [Header("ウキの距離制御"), SerializeField(Label = "魚の引き速度(m/秒)")]
    private float fishPullSpeed = 1.5f;

    /// <summary>
    /// 目標距離が現在より<b>近い</b>とき、ウキが手元へ寄る速度の上限（m/秒）。
    /// 魚 HP を削った分だけ目標距離が縮むので、その差をこの速度で追いかける
    /// （＝巻いた手応えが見た目に出る速さ。目標は追い越さない）。
    /// </summary>
    [SerializeField(Label = "寄せ速度の上限(m/秒)")]
    private float reelInSpeedMax = 6f;

    // ─── 魚 HP ───────────────────────────────────────────

    /// <summary>
    /// 掛かった瞬間の距離（ウキ→竿先）の下限（メートル）。
    /// 手元で掛かったときに「1HP あたりの距離」が 0 に潰れるのを防ぐ番人値。
    /// </summary>
    [Header("魚HP"), SerializeField(Label = "掛かった距離の下限(m)")]
    private float hookDistanceMin = 2f;

    // ─── 暴れの攻撃（回避判定）─────────────────────────────

    /// <summary>
    /// 回避失敗時にゲージを危険側へ押し込む量の基準値（等価の相手・暴れのとき）。
    /// </summary>
    [Header("暴れの攻撃"), SerializeField(Label = "押し込み量の基準")]
    private float attackPushBase = 0.35f;

    /// <summary>
    /// 戦闘力差が押し込み量へ効く強さ。
    /// 押し込み量 ＝ 基準 × (1 + 本値 × (魚の総合力 ÷ 竿パワー − 1))。
    /// 0 なら戦闘力差を無視、1 ならそのまま比例。
    /// </summary>
    [SerializeField(Label = "押し込みへの戦闘力差の効き")]
    private float attackPushPowerScale = 0.5f;

    /// <summary>「大暴れ」の攻撃に掛かる押し込み量の倍率。</summary>
    [SerializeField(Label = "大暴れの押し込み倍率")]
    private float bigRageAttackScale = 1.6f;

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

    // ─── デバッグ表示（暴れ状態の可視化・調整用の一時機能）──────────

    /// <summary>
    /// デバッグ用: マーカーの色・サイズ・中心テキストへ魚の行動状態を反映するか。
    /// 調整が終わったら false にして常設 UI へ戻せるよう Inspector から切り替えられる
    /// （一時的な確認用機能。恒久的な演出になったら専用の仕組みへ差し替える想定）。
    /// </summary>
    [Header("デバッグ: 暴れ状態の可視化"), SerializeField(Label = "デバッグ: 暴れ状態を表示")]
    private bool debugShowFishAction = true;

    /// <summary>通常（暴れていない状態が来た場合のフォールバック）のマーカー色（RGB）。</summary>
    [SerializeField(Label = "デバッグ色: 通常(白)")]
    private SEED.Vector3 debugColorNormal = new SEED.Vector3(1f, 1f, 1f);

    /// <summary>「暴れ」中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "デバッグ色: 暴れ(橙)")]
    private SEED.Vector3 debugColorRage = new SEED.Vector3(1f, 0.6f, 0.1f);

    /// <summary>「大暴れ」中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "デバッグ色: 大暴れ(赤)")]
    private SEED.Vector3 debugColorBigRage = new SEED.Vector3(1f, 0.15f, 0.15f);

    /// <summary>「ステイ」中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "デバッグ色: ステイ(青)")]
    private SEED.Vector3 debugColorStay = new SEED.Vector3(0.4f, 0.6f, 1f);

    /// <summary>「待機（ひるみ）」中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "デバッグ色: 待機/ひるみ(灰)")]
    private SEED.Vector3 debugColorWait = new SEED.Vector3(0.5f, 0.5f, 0.5f);

    /// <summary>「暴れ」中にマーカーへ掛ける拡大倍率（<see cref="markerBaseSize"/> への倍率）。</summary>
    [SerializeField(Label = "デバッグ: 暴れ時のマーカー拡大率")]
    private float debugMarkerScaleRage = 1.6f;

    /// <summary>「大暴れ」中にマーカーへ掛ける拡大倍率（<see cref="markerBaseSize"/> への倍率）。</summary>
    [SerializeField(Label = "デバッグ: 大暴れ時のマーカー拡大率")]
    private float debugMarkerScaleBigRage = 2f;

    // ─── 実行時の状態 ─────────────────────────────────────

    /// <summary>バトル進行中か（<see cref="BeginFight"/> 〜 <see cref="EndFight"/>）。</summary>
    public bool Active { get; private set; } = false;

    /// <summary>
    /// バトルの一時停止フラグ【わらしべ連鎖のアタリ中に使う】。
    ///
    /// true のあいだ <see cref="Tick"/> はゲージ・糸 HP・スタミナ・魚 HP を一切進めず、
    /// ウキの移動量（<see cref="ComputeFloatDistanceStep"/>）も 0 にして、UI の再描画だけを行う（＝画面からゲージが消えず、値も動かない）。
    /// 掛かっている魚を餌に別の魚がつつきに来ているあいだ、やり取りを凍結するための仕組みで、
    /// 連鎖の決着（乗り換え成功／失敗）でコントローラが false へ戻す。
    /// <see cref="EndFight"/>（<see cref="ResetRuntimeState"/>）でも必ず false へ戻るので、
    /// 一時停止したままバトルが終わって次のバトルが止まる、という持ち越しは起きない。
    /// </summary>
    public bool Paused { get; set; } = false;

    /// <summary>現在のテンションゲージ（−1 〜 +1）。</summary>
    public float Gauge { get; private set; } = 0f;

    /// <summary>
    /// 「巻いている」とみなされているか（<see cref="reelHoldSeconds"/> のホールド込み）。
    /// マウスホイールの離散ノッチをそのまま使わず、直近のノッチから本値の秒数が
    /// 経つまでは true を維持する。ゲージの増減・暴れの回避判定・UI 表示はすべてこれを使う
    /// （糸切れに直結する糸 HP の増減だけは実際の <c>reelAmount</c> を使い続ける）。
    /// </summary>
    public bool IsReeling { get; private set; } = false;

    /// <summary>糸 HP の割合（0〜1）。UI と外部表示用。</summary>
    public float LineHp01 => lineHpMax > DivideEpsilon
        ? SEED.Mathf.Clamped01(lineHp / lineHpMax)
        : 0f;

    /// <summary>
    /// 糸が切れたか。HP が 0 に達したフレームで true になる。
    /// コントローラ側が拾ったら <see cref="EndFight"/> で false へ戻る。
    /// </summary>
    public bool LineBroken { get; private set; } = false;

    /// <summary>現在のスタミナの割合（0〜1）。UI・チューニング表示用。</summary>
    public float Stamina01 => staminaMax > DivideEpsilon
        ? SEED.Mathf.Clamped01(stamina / staminaMax)
        : 0f;

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
    /// ＝ 巻き効率（竿パワー ÷ (竿パワー ＋ 現在の魚の総合力)）÷ 1HP あたりの距離。
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
            return efficiency / SEED.Mathf.Max(metersPerHp, DivideEpsilon);
        }
    }

    /// <summary>
    /// いまウキが居るべき距離（ウキ→竿先の水平距離、メートル）
    /// ＝ 現在の魚 HP × 1HP あたりの距離。
    /// 掛かった直後は現在の距離より遠い（＝沖へ引かれる）。
    /// </summary>
    public float DesiredFloatDistance => SEED.Mathf.Max(fishHp, FishHpZero) * metersPerHp;

    /// <summary>糸切れの効果音パス（コントローラ側から鳴らす場合の参照用）。</summary>
    public string LineBreakSePath => lineBreakSePath;

    /// <summary>糸切れの効果音の音量。</summary>
    public float LineBreakSeVolume => lineBreakSeVolume;

    /// <summary>現在の糸 HP（内部値）。</summary>
    private float lineHp = 0f;

    /// <summary>現在の魚 HP（内部値）。0 で釣り上げ成立。</summary>
    private float fishHp = 0f;

    /// <summary>このバトルでの魚 HP の最大値（＝基礎HP ＋ ボーナス HP）。</summary>
    private float fishHpMax = 0f;

    /// <summary>
    /// 魚 HP 1 あたりの距離（メートル）＝ 掛かった瞬間の距離 ÷ 魚の基礎HP。
    /// 魚 HP と「ウキ→竿先の距離」を相互変換する唯一の係数。
    /// </summary>
    private float metersPerHp = 0f;

    /// <summary>
    /// いまの暴れ／大暴れの攻撃判定が既に決着したか。
    /// 1 回の暴れにつき押し込みは 1 度だけなので、失敗を適用したら true にする。
    /// </summary>
    private bool attackResolved = false;

    /// <summary>
    /// 「巻いている」ホールドの残り秒数（<see cref="reelHoldSeconds"/> 参照）。
    /// ノッチが来るたびに満タンへ戻し、そうでないフレームは <c>deltaTime</c> ぶん減らす。
    /// 0 より大きいあいだ <see cref="IsReeling"/> が true になる。
    /// </summary>
    private float reelHoldRemaining = 0f;

    /// <summary>戦っている魚（null ＝ 非戦闘中）。位置・状態は一切触らず、パラメータだけ読む。</summary>
    private Fish? target = null;

    /// <summary>魚の暴れ度の基準値（インスタンスは差し替わり得るので毎回読む）。</summary>
    private float TargetRampage => target is { } fish ? fish.Rampage : DefaultRampage;

    /// <summary>
    /// 魚の行動状態（フローチャート仕様 2026-09-05）。
    /// 判定順は「待機 → スタミナ切れ → 疲労中 → 暴れ抽選」で、毎フレーム評価する。
    /// </summary>
    private enum FishAction
    {
        /// <summary>暴れている（状態倍率 <see cref="rageMultiplier"/>）。</summary>
        Rage,

        /// <summary>大暴れしている（状態倍率 <see cref="bigRageMultiplier"/>）。</summary>
        BigRage,

        /// <summary>ステイ（スタミナ回復中。状態倍率 <see cref="stayMultiplier"/>）。</summary>
        Stay,

        /// <summary>待機＝ひるみ（漂流物などで隙ができた状態。倍率 <see cref="flinchMultiplier"/>）。</summary>
        Wait,
    }

    /// <summary>現在の魚の行動状態。</summary>
    private FishAction action = FishAction.Rage;

    /// <summary>ログを出した最後の行動状態（状態が変わったときだけ 1 回ログするための控え）。</summary>
    private FishAction loggedAction = FishAction.Rage;

    /// <summary>現在の暴れ／大暴れが終わるまでの残り秒数（0 以下で次を抽選し直す）。</summary>
    private float rageTimer = 0f;

    /// <summary>待機（ひるみ）の残り秒数。0 より大きいあいだは他の判定より優先される。</summary>
    private float flinchTimer = 0f;

    /// <summary>疲労中か（スタミナが 0 になってから復帰しきい値まで回復するまで true）。</summary>
    private bool isTired = false;

    /// <summary>現在のスタミナ（内部値）。</summary>
    private float stamina = 0f;

    /// <summary>このバトルでのスタミナ最大値（<see cref="BeginFight"/> で魚から決まる）。</summary>
    private float staminaMax = 0f;

    /// <summary>
    /// セグメントの配置・色を最後に計算したときの表示半角（度）。
    /// これと安全帯の幅が変わらないあいだは 48 個のセグメントに触らない。
    /// </summary>
    private float cachedArcHalfAngle = UncachedSentinel;

    /// <summary>セグメントの配置・色を最後に計算したときの安全帯の半幅。</summary>
    private float cachedZoneHalfWidth = UncachedSentinel;

    /// <summary>
    /// <see cref="gaugeMarker"/> の元々のサイズ（デバッグ拡大表示の基準値）。
    /// <see cref="BeginFight"/> で 1 度だけ読み取り、<see cref="ResetRuntimeState"/> で必ず書き戻す
    /// （＝暴れ演出で拡大したままバトルが終わって次回以降サイズが狂う、を防ぐ）。
    /// </summary>
    private SEED.Vector2 markerBaseSize = default;

    /// <summary><see cref="markerBaseSize"/> を読み取り済みか（未読のまま書き戻さないためのガード）。</summary>
    private bool markerBaseSizeCaptured = false;

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
    /// <param name="hookDistance">
    /// 掛かった瞬間のウキ→竿先の水平距離（メートル）。
    /// 「魚 HP 1 あたりの距離」の基準になる（<see cref="hookDistanceMin"/> で下限クランプ）。
    /// </param>
    public void BeginFight(Fish fish, FishingController.HookJudgement judge, float hookDistance)
    {
        target = fish;
        Active = true;
        LineBroken = false;
        lineHp = SEED.Mathf.Max(lineHpMax, 0f);
        Gauge = SEED.Mathf.Clamped(InitialGauge(judge), GaugeMin, GaugeMax);

        // 開始時点では「保留中の攻撃なし」＝決着済み扱いにしておく。
        // 直後の PickRage が最初の暴れを引いた時点で false へ開け直される
        // （こうしないと開始直後に「回避成功」のログが 1 回出てしまう）。
        attackResolved = true;

        // 魚 HP: 掛かった瞬間の総合力（状態倍率 1・スタミナ満タン）で「魚の取り分」を出し、
        // その割合ぶんだけ基礎HP へボーナスを乗せる（99:1 なら約 +1% / 1:99 なら約 +99%）。
        float rod = SEED.Mathf.Max(rodPower, DivideEpsilon);
        float hookPower = SEED.Mathf.Max(fish.BasePower * SizeScore(fish), 0f);
        float fishShare = hookPower / SEED.Mathf.Max(rod + hookPower, DivideEpsilon);
        float baseHp = SEED.Mathf.Max(fish.BaseHp, DivideEpsilon);
        fishHpMax = baseHp + baseHp * fishShare;
        fishHp = fishHpMax;

        // 距離との対応付け: 掛かった距離を「基礎HP ぶんの距離」とみなす。
        // ボーナス HP はその外側なので、掛かった直後の目標距離は現在の距離より遠くなり、
        // 魚がウキを沖へ引いていく（＝「沖へ持っていかれる」演出の実体）。
        metersPerHp = SEED.Mathf.Max(hookDistance, hookDistanceMin) / baseHp;

        // スタミナを満タンから始める（最大値は魚のパラメータ × 倍率。未設定なら既定値）
        staminaMax = SEED.Mathf.Max(
            (fish.Stamina > 0f ? fish.Stamina : defaultStaminaMax) * staminaScale,
            DivideEpsilon);
        stamina = staminaMax;

        // 行動状態をリセットし、最初の暴れ／大暴れを抽選する
        flinchTimer = 0f;
        isTired = false;
        rageTimer = 0f;
        reelHoldRemaining = 0f;     // 前回バトルの巻き入力を持ち越さない
        IsReeling = false;
        PickRage();

        // マーカーの元サイズを 1 度だけ読み取る（デバッグの拡大表示の基準・終了時に書き戻す）
        if (gaugeMarker is { } marker && marker.IsValid)
        {
            markerBaseSize = marker.Size;
            markerBaseSizeCaptured = true;
        }

        // 隠していたぶんセグメントは必ず作り直す（キャッシュを無効化する）
        InvalidateArcCache();
        ApplyUi();

        SEED.Debug.Log($"[Fight] 開始: {fish.DisplayName} / 戦闘力 {CurrentFishPower():F2} vs 竿 {rodPower:F2}"
                     + $" / 魚HP {fishHpMax:F1}（取り分 {fishShare:P0}）"
                     + $" / 掛かった距離 {hookDistance:F1}m → 目標 {DesiredFloatDistance:F1}m"
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

        // 一時停止中（わらしべ連鎖のアタリ受付中）は内部値を一切進めない。
        // 距離の目標も動かないので（ComputeFloatDistanceStep は呼ばれない前提ではなく、
        // 呼ばれても目標が変わらないだけ）、UI だけ現在値のまま描き直す。
        if (Paused)
        {
            ApplyUi();
            return;
        }

        // マウスホイールは「ノッチが来たフレームだけ」離散的に reelAmount を渡してくるため、
        // これをそのまま「巻いている／いない」に使うとノッチの来ない大半のフレームが
        // 「操作していない」扱いになってしまう（− 側は「巻く」ことでしか回復できないため詰む）。
        // ノッチが来たらホールド時間を満タンへ戻し、そうでなければ減らしていくことで
        // 「巻き続けている」とみなせる区間を作る（＝ IsReeling）。
        if (reelAmount > ReelInputEpsilon)
        {
            reelHoldRemaining = reelHoldSeconds;
        }
        else
        {
            reelHoldRemaining -= deltaTime;
        }
        IsReeling = reelHoldRemaining > 0f;

        UpdateFishAction(deltaTime);
        UpdateGauge(deltaTime, IsReeling);
        UpdateAttack(IsReeling);
        UpdateLineHp(deltaTime);
        UpdateFishHp(reelAmount);   // ← 糸 HP に直結する減少量は実際の巻き取り量（離散）のまま使う

        ApplyUi();
    }

    /// <summary>
    /// 待機（ひるみ）を与える【隙を作る唯一の入口】。
    ///
    /// 漂流物アイテムを当てたときにコントローラ側から呼ぶ想定の API
    /// （<b>漂流物そのものは未実装</b>なので、現時点では誰も呼ばない）。
    /// 待機中は他のどの判定よりも優先され、状態倍率が
    /// <see cref="flinchMultiplier"/> まで落ちる（＝巻きやすくなる）。
    /// 既に待機中なら残り秒数へ<b>加算</b>する（連続ヒットで伸びる）。
    /// </summary>
    /// <param name="seconds">与える待機の秒数（0 以下なら何もしない）。</param>
    public void ApplyFlinch(float seconds)
    {
        if (!Active || seconds <= 0f) { return; }
        flinchTimer += seconds;
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
    /// 現在の魚の総合力
    /// ＝ 基礎パワー × 大きさスコア × 状態倍率 × スタミナ倍率。
    ///
    /// 魚が居なければ竿パワーと同値（＝等価）を返し、速度が暴れないようにする。
    /// </summary>
    private float CurrentFishPower()
    {
        if (target is not { } fish) { return rodPower; }
        return fish.BasePower * SizeScore(fish) * CurrentActionMultiplier() * StaminaFactor();
    }

    /// <summary>
    /// スタミナ倍率 ＝ Lerp(<see cref="staminaFactorMin"/>, 1, スタミナ割合)。
    /// スタミナが少ないほど総合力にマイナス倍率が掛かる（仕様）。
    /// </summary>
    private float StaminaFactor()
        => SEED.Mathf.Lerp(staminaFactorMin, StaminaFactorMax, Stamina01);

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
    /// 現在の状態倍率（総合力へ掛かる係数）。
    ///
    /// - 暴れ / 大暴れ … <see cref="ScaledByRampage"/> で魚の暴れ度によりスケールされる
    ///   （暴れ度 1 ならインスペクタ値そのまま、2 なら 1.5 → 2.0 のように誇張される）
    /// - ステイ / 待機 … 「隙」の大きさは個体差で変えたくないので倍率をそのまま使う
    /// </summary>
    private float CurrentActionMultiplier() => action switch
    {
        FishAction.Rage => ScaledByRampage(rageMultiplier),
        FishAction.BigRage => ScaledByRampage(bigRageMultiplier),
        FishAction.Stay => stayMultiplier,
        _ => flinchMultiplier,
    };

    /// <summary>
    /// 状態倍率を魚の暴れ度でスケールする ＝ (倍率 − 1) × 暴れ度 + 1。
    /// 暴れ度 1（規定）ならインスペクタ値そのまま、0 なら平常（1）へ潰れる。
    /// </summary>
    /// <param name="multiplier">スケール前の状態倍率。</param>
    private float ScaledByRampage(float multiplier)
        => (multiplier - NeutralMultiplier) * TargetRampage + NeutralMultiplier;

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
    /// ゲージの現在位置による「通常の増減速度」への倍率【中央ほど遅く・端ほど速くする唯一の算出点】。
    ///
    /// <c>AmplitudeScale(g) = Lerp(gaugeCenterRateScale, 1, |g|^gaugeRateCurvePower)</c>
    /// ＝ 中央（|g|＝0）で <see cref="gaugeCenterRateScale"/>、端（|g|＝1）で等倍（1）になる。
    /// <see cref="gaugeCenterRateScale"/> は 0 だと中央で完全停止してしまうため
    /// <see cref="GaugeCenterRateScaleMin"/> で下限をクランプしてから使う
    /// （＝ゲージがちょうど 0 からでも必ず動き出せる）。
    ///
    /// <b>適用範囲の注意</b>: この倍率は <see cref="UpdateGauge"/> の<b>通常の増減</b>にのみ掛ける。
    /// 中央への<b>回復</b>と暴れの<b>攻撃の押し込み</b>には掛けない（クラス側コメント参照）。
    /// </summary>
    /// <param name="gauge">現在のゲージ値（−1〜1）。</param>
    private float AmplitudeScale(float gauge)
    {
        float centerScale = SEED.Mathf.Max(gaugeCenterRateScale, GaugeCenterRateScaleMin);
        float t = SEED.Mathf.Pow(SEED.Mathf.Clamped01(SEED.Mathf.Abs(gauge)), SEED.Mathf.Max(gaugeRateCurvePower, DivideEpsilon));
        return SEED.Mathf.Lerp(centerScale, 1f, t);
    }

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

    // ─── 内部処理: 魚の行動（状態遷移）─────────────────────

    /// <summary>
    /// 魚の行動状態を 1 フレーム進める【状態遷移の唯一の集約点】。
    ///
    /// 判定順（フローチャート仕様）:
    /// <code>
    /// 待機タイマー &gt; 0        → 待機（タイマーを減らすだけ。倍率 0.2）
    /// else スタミナ == 0      → 疲労フラグ ON → ステイ（少し回復。倍率 0.6）
    /// else 疲労中             → 復帰しきい値未満ならステイ / 達したら疲労解除して暴れへ
    /// else                    → 暴れ or 大暴れ（重み抽選・時間切れで引き直し）
    /// </code>
    /// 漂流物アイテムの命中は <see cref="ApplyFlinch"/>（外部から呼ばれる API）で
    /// 待機タイマーへ加算される。漂流物自体は未実装。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void UpdateFishAction(float deltaTime)
    {
        // 1) 待機（ひるみ）… 最優先。時間を消化するだけで何もしない
        if (flinchTimer > 0f)
        {
            flinchTimer -= deltaTime;
            ResolveRageEnd();          // 暴れの途中でひるんだ場合も攻撃はそこで終わり
            SetAction(FishAction.Wait);
            return;
        }

        // 2) スタミナ切れ … 疲労状態へ入り、ステイで回復し始める
        if (stamina <= 0f)
        {
            isTired = true;
            EnterStay(deltaTime);
            return;
        }

        // 3) 疲労中 … 復帰しきい値まではステイを続け、達したら暴れへ戻る
        if (isTired)
        {
            if (stamina < RecoverThresholdStamina())
            {
                EnterStay(deltaTime);
                return;
            }
            isTired = false;
        }

        // 4) 暴れ／大暴れ … 状態でなければ抽選、時間切れでも引き直す
        if (action is not (FishAction.Rage or FishAction.BigRage) || rageTimer <= 0f)
        {
            PickRage();
        }

        rageTimer -= deltaTime;
        stamina = SEED.Mathf.Max(stamina - CurrentRageDrainPerSec() * deltaTime, 0f);
    }

    /// <summary>
    /// ステイ（スタミナ回復）へ入り、このフレームぶん回復する。
    /// 暴れの残り時間は捨てる（ステイから戻るときは必ず抽選し直すため）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void EnterStay(float deltaTime)
    {
        ResolveRageEnd();              // 暴れからステイへ移る＝その攻撃はここで終わる
        SetAction(FishAction.Stay);
        rageTimer = 0f;
        stamina = SEED.Mathf.Min(stamina + stayRecoverPerSec * deltaTime, staminaMax);
    }

    /// <summary>
    /// 暴れ／大暴れを重み抽選し、その継続秒数を引く。
    /// 重みが両方 0 のようなデータでも成立するよう、合計が 0 なら「暴れ」を選ぶ。
    /// </summary>
    private void PickRage()
    {
        // 前の暴れ（＝攻撃）を締めてから次を引く。回避成功のログもここで出る。
        ResolveRageEnd();

        float total = SEED.Mathf.Max(rageWeight, 0f) + SEED.Mathf.Max(bigRageWeight, 0f);
        bool big = total > DivideEpsilon && SEED.Random.Range(0f, total) >= SEED.Mathf.Max(rageWeight, 0f);

        SetAction(big ? FishAction.BigRage : FishAction.Rage);
        rageTimer = big
            ? SEED.Random.Range(bigRageDurationMin, SEED.Mathf.Max(bigRageDurationMin, bigRageDurationMax))
            : SEED.Random.Range(rageDurationMin, SEED.Mathf.Max(rageDurationMin, rageDurationMax));
    }

    /// <summary>現在の状態のスタミナ消費（/秒）。暴れ・大暴れ以外は消費しない。</summary>
    private float CurrentRageDrainPerSec() => action switch
    {
        FishAction.Rage => rageStaminaDrainPerSec,
        FishAction.BigRage => bigRageStaminaDrainPerSec,
        _ => 0f,
    };

    /// <summary>疲労から復帰するスタミナの実値（＝最大値 × <see cref="recoverThreshold01"/>）。</summary>
    private float RecoverThresholdStamina()
        => staminaMax * SEED.Mathf.Clamped01(recoverThreshold01);

    /// <summary>
    /// 行動状態を切り替える【状態変更の唯一の出口】。
    /// 変わったときだけ 1 回ログを出す（チューニング用。毎フレーム出さない）。
    /// </summary>
    /// <param name="next">次の行動状態。</param>
    private void SetAction(FishAction next)
    {
        action = next;
        if (loggedAction == next) { return; }

        loggedAction = next;
        SEED.Debug.Log($"[Fight] {ActionLabel(next)} / ST {SEED.Mathf.RoundToInt(Stamina01 * PercentScale)}%"
                     + $" / 総合力 {CurrentFishPower():F2}"
                     + $" / 魚HP {SEED.Mathf.RoundToInt(FishHp01 * PercentScale)}%"
                     + $" / 巻き効率 {ReelHpPerUnit * metersPerHp:F2}");
    }

    /// <summary>ログ表示用の行動状態の名前。</summary>
    /// <param name="value">行動状態。</param>
    private static string ActionLabel(FishAction value) => value switch
    {
        FishAction.Rage => "暴れ",
        FishAction.BigRage => "大暴れ",
        FishAction.Stay => "stay",
        _ => "待機",
    };

    // ─── 内部処理: ウキの距離制御 ───────────────────────────

    /// <summary>
    /// このフレームにウキ→竿先の距離を動かす量（メートル・符号つき）を返す
    /// 【ウキの移動量の唯一の算出点】。
    ///
    /// ＋ ＝ 沖へ（距離が増える） / − ＝ 手元へ（距離が減る）。
    /// - 目標距離が現在より<b>遠い</b> … 魚が引く。速度 ＝ <see cref="fishPullSpeed"/> × 戦闘力比
    /// - 目標距離が現在より<b>近い</b> … 巻けた分だけ寄る。速度上限 ＝ <see cref="reelInSpeedMax"/>
    /// どちらも<b>目標を追い越さない</b>ようにクランプする。
    /// 非アクティブ・一時停止中は 0（ウキを動かさない）。
    /// </summary>
    /// <param name="currentDistance">現在のウキ→竿先の水平距離（メートル）。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <returns>距離の増減量（＋ が沖／− が手元）。</returns>
    public float ComputeFloatDistanceStep(float currentDistance, float deltaTime)
    {
        if (!Active || Paused || target is null) { return 0f; }

        float difference = DesiredFloatDistance - currentDistance;

        // 目標のほうが遠い: 魚が沖へ引く（戦闘力比ぶん速い）
        if (difference > 0f)
        {
            float outward = fishPullSpeed * PowerRatioClamped() * deltaTime;
            return SEED.Mathf.Min(outward, difference);
        }

        // 目標のほうが近い: 手元へ寄せる（上限速度でクランプ・目標は追い越さない）
        float inward = reelInSpeedMax * deltaTime;
        return -SEED.Mathf.Min(inward, -difference);
    }

    // ─── 内部処理: 魚 HP ────────────────────────────────

    /// <summary>
    /// 魚 HP を 1 フレームぶん削る。
    /// 削れる量 ＝ このフレームの巻き取り量（m）× <see cref="ReelHpPerUnit"/>。
    /// 巻いていなければ削れない（回復もしない＝削った分は戻らない）。
    /// </summary>
    /// <param name="reelAmount">このフレームの巻き取り量（メートル）。</param>
    private void UpdateFishHp(float reelAmount)
    {
        if (reelAmount <= ReelInputEpsilon) { return; }
        fishHp = SEED.Mathf.Max(fishHp - reelAmount * ReelHpPerUnit, FishHpZero);
    }

    // ─── 内部処理: 暴れの攻撃（回避判定）───────────────────

    /// <summary>
    /// 暴れ／大暴れ中の「攻撃」に対する回避判定【押し込みの唯一の適用点】。
    ///
    /// ＋ 側（0 を含む）に居るなら「巻かない」のが回避、
    /// − 側に居るなら「巻く」のが回避。失敗した瞬間に 1 度だけゲージを危険側へ押し込む。
    /// 暴れ 1 回につき 1 度きり（<see cref="attackResolved"/>）。
    /// </summary>
    /// <param name="reeling">巻いているとみなせるか（<see cref="IsReeling"/>。ホールド込み）。</param>
    private void UpdateAttack(bool reeling)
    {
        if (attackResolved) { return; }
        if (action is not (FishAction.Rage or FishAction.BigRage)) { return; }

        // ゲージちょうど 0 は ＋ 側として扱う（＝巻くほうが危険側という仕様）
        bool plusSide = Gauge >= GaugeCenter;
        bool failed = plusSide ? reeling : !reeling;
        if (!failed) { return; }

        float push = AttackPush();
        Gauge = SEED.Mathf.Clamped(plusSide ? Gauge + push : Gauge - push, GaugeMin, GaugeMax);
        attackResolved = true;

        SEED.Debug.Log($"[Fight] 回避失敗（{ActionLabel(action)}）"
                     + $" / 押し込み {push:F2} → ゲージ {Gauge:F2}");
    }

    /// <summary>
    /// 回避失敗時の押し込み量
    /// ＝ <see cref="attackPushBase"/> × (1 + <see cref="attackPushPowerScale"/> ×
    ///    (魚の総合力 ÷ 竿パワー − 1)) × (大暴れなら <see cref="bigRageAttackScale"/>)。
    /// 戦闘力比が小さいと負になり得るので <see cref="MinAttackPush"/> で下限クランプする。
    /// </summary>
    private float AttackPush()
    {
        float ratio = CurrentFishPower() / SEED.Mathf.Max(rodPower, DivideEpsilon);
        float scaled = NeutralMultiplier + attackPushPowerScale * (ratio - EquivalentPowerRatio);
        float big = action == FishAction.BigRage ? bigRageAttackScale : NeutralMultiplier;
        return SEED.Mathf.Max(attackPushBase * scaled * big, MinAttackPush);
    }

    /// <summary>
    /// 暴れ（＝攻撃）が終わるときの締め【回避成功の唯一の判定点】。
    /// 失敗が一度も起きないまま暴れが終わったら回避成功としてログを残し、
    /// 次の暴れのために判定フラグを開け直す。
    /// </summary>
    private void ResolveRageEnd()
    {
        if ((action is FishAction.Rage or FishAction.BigRage) && !attackResolved)
        {
            SEED.Debug.Log($"[Fight] 回避成功（{ActionLabel(action)}）");
        }
        attackResolved = false;
    }

    // ─── 内部処理: ゲージと糸 HP ───────────────────────────

    /// <summary>
    /// テンションゲージを 1 フレーム進める。
    ///
    /// - 巻いている   … ＋ 側へ上昇。ただし − 側に居るあいだは中央へ<b>回復</b>
    /// - 操作していない… − 側へ下降。ただし ＋ 側に居るあいだは中央へ<b>回復</b>
    ///
    /// 通常の増減（上昇／下降）だけ <see cref="AmplitudeScale"/> を掛けて、
    /// 中央付近ほどゆっくり・端に近いほど素早く動くようにする
    /// （＝中央からの「離れ始め」が穏やかになり、操作の起点をつかみやすくする）。
    /// 中央への<b>回復</b>は仕様上「回復は必ず一定速度で 0 へ届く」ことが前提なので、
    /// この倍率を<b>掛けない</b>（掛けると回復が中央付近で止まりかけてしまう）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <param name="reeling">巻いているとみなせるか（<see cref="IsReeling"/>。ホールド込み）。</param>
    private void UpdateGauge(float deltaTime, bool reeling)
    {
        float rate = GaugeRate() * AmplitudeScale(Gauge);
        float recovery = gaugeRecoverySpeed * deltaTime;   // ← 回復は倍率を掛けない（常に一定速度）

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
        Paused = false;                // 一時停止の持ち越しを防ぐ（次のバトルが凍ったまま始まらないように）
        LineBroken = false;
        Gauge = GaugeCenter;
        lineHp = 0f;
        fishHp = 0f;
        fishHpMax = 0f;
        metersPerHp = 0f;
        attackResolved = true;         // 保留中の攻撃なし（次の PickRage で開け直される）
        target = null;
        action = FishAction.Rage;
        loggedAction = FishAction.Rage;
        rageTimer = 0f;
        flinchTimer = 0f;
        isTired = false;
        stamina = 0f;
        staminaMax = 0f;
        reelHoldRemaining = 0f;        // 巻き入力のホールドも次バトルへ持ち越さない
        IsReeling = false;

        // デバッグ拡大で書き換えたマーカーサイズを元へ戻す（拡大したまま終わらないように）
        if (markerBaseSizeCaptured && gaugeMarker is { } marker && marker.IsValid)
        {
            marker.Size = markerBaseSize;
        }
        markerBaseSizeCaptured = false;
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

        // マーカー: ゲージ値（−1〜1）を角度 θ ＝ gauge × 表示半角 へ写して円周上へ置く。
        // 色は通常は白のまま、デバッグ表示 ON なら魚の行動状態で着色する（ApplyMarkerDebugTint 参照）。
        ApplyMarkerDebugTint();
        ApplyMarkerDebugScale();
        if (gaugeMarkerTransform is { } markerTf && markerTf.IsValid)
        {
            float degrees = Gauge * halfAngle;
            markerTf.Position = ArcPoint(degrees);
            markerTf.Rotation = degrees;
        }

        // 糸 HP と魚のスタミナ: 円の中心にパーセント表示（スタミナはチューニング用）。
        // デバッグ表示 ON なら先頭へ現在の行動状態（暴れ! / 大暴れ!! / 休み / ひるみ）を付ける。
        if (hpText is { } label && label.IsValid)
        {
            string prefix = debugShowFishAction ? DebugActionPrefix() : string.Empty;
            label.Content = $"{prefix}HP {SEED.Mathf.RoundToInt(LineHp01 * PercentScale)}%"
                          + $"  ST {SEED.Mathf.RoundToInt(Stamina01 * PercentScale)}%"
                          + $"  魚 {SEED.Mathf.RoundToInt(FishHp01 * PercentScale)}%";
            label.Color = label.Color.WithAlpha(SEED.Mathf.Clamped01(hpTextOpacity));
        }
    }

    // ─── デバッグ表示: 暴れ状態の可視化 ─────────────────────

    /// <summary>
    /// マーカーの色を魚の行動状態で着色する【デバッグ着色の唯一の適用点】。
    /// <see cref="debugShowFishAction"/> が false のときは常に白（着色しない）に戻す。
    /// </summary>
    private void ApplyMarkerDebugTint()
    {
        if (gaugeMarker is not { } marker || !marker.IsValid) { return; }

        SEED.Vector3 rgb = debugShowFishAction ? DebugActionColorRgb() : debugColorNormal;
        marker.Color = ToColor(rgb, SEED.Mathf.Clamped01(gaugeMarkerOpacity));
    }

    /// <summary>
    /// マーカーのサイズを行動状態で拡大する【デバッグ拡大の唯一の適用点】。
    /// 暴れ／大暴れのときだけ <see cref="markerBaseSize"/> へ倍率を掛け、
    /// それ以外（デバッグ OFF を含む）は元のサイズへ戻す。
    /// </summary>
    private void ApplyMarkerDebugScale()
    {
        if (gaugeMarker is not { } marker || !marker.IsValid || !markerBaseSizeCaptured) { return; }

        float scale = debugShowFishAction
            ? action switch
            {
                FishAction.Rage => debugMarkerScaleRage,
                FishAction.BigRage => debugMarkerScaleBigRage,
                _ => 1f,
            }
            : 1f;
        marker.Size = markerBaseSize * scale;
    }

    /// <summary>行動状態に対応するデバッグ色（RGB）。</summary>
    private SEED.Vector3 DebugActionColorRgb() => action switch
    {
        FishAction.Rage => debugColorRage,
        FishAction.BigRage => debugColorBigRage,
        FishAction.Stay => debugColorStay,
        FishAction.Wait => debugColorWait,
        _ => debugColorNormal,
    };

    /// <summary>糸 HP テキストの先頭へ付ける行動状態のラベル（末尾に半角スペースを含む）。</summary>
    private string DebugActionPrefix() => action switch
    {
        FishAction.Rage => "暴れ! ",
        FishAction.BigRage => "大暴れ!! ",
        FishAction.Stay => "休み ",
        FishAction.Wait => "ひるみ ",
        _ => string.Empty,
    };

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
