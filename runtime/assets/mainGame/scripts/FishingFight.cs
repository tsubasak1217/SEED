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
/// <see cref="Paused"/>（外部都合の一時停止フック）のあいだは時計ごと止まる＝無音になる。
///
/// ■ ドラムループ（<see cref="SetupDrumLoop"/>）
/// バトル中は BGM 枠でドラムループを鳴らし、再生速度を <c>魚のBPM ÷ 素材のBPM</c> に
/// 変えて魚の拍と同じテンポにする。開始は余白の頭（余白が拍子の倍数でないときだけ
/// 次の小節頭まで待つ）。
/// フェーズ長が可変（隙が 1 小節または 2 小節）になったため、ドラムの 2 小節ループは
/// 放っておくと出題の頭とズレる。そこで <see cref="drumRestartAtCall"/>（既定 true）で
/// <b>出題フェーズの頭ごとにループを鳴らし直す</b>ことを既定の同期手段とし、
/// <see cref="drumResyncBars"/> 小節ごとの定期再同期は任意の補助として残す。
/// <see cref="Paused"/> 中は止め、再開時は次のループ境界へ揃えて鳴らし直す。
/// メトロノームと併用する前提だが、メトロノームの音量を 0 にすればドラムだけにもできる。
///
/// ■ フェーズ（LeadIn だけ<b>拍単位</b>・それ以外は<b>小節単位</b>、切り替えは必ず小節頭）
/// <code>
/// 余白(LeadIn) → 出題(Call) → 回答(Answer) → 隙(Rest) → 出題 → …
/// 既定の長さ: 余白 leadInBeats 拍 / 出題 callBars 小節(既定 2) / 回答 answerBars 小節(既定 2)
///             隙 = 直前の回答の出来で決まる（下表）
/// 出題・回答の小節数は魚データ（Fish.RhythmCallBars / RhythmAnswerBars）が 1 以上ならそちらが優先。
/// 隙の小節数は「回答の出来」で毎回決まるので、魚データの RhythmRestBars は<b>参照しない</b>。
/// </code>
/// <b>隙の長さの規則（2026-09-06 改定）</b>
/// <code>
/// 直前の回答が完璧（期待打点が全て Excellent ＆ 余分なクリック 0）→ restBarsPerfect 小節（既定 2）
/// それ以外                                                        → restBarsNormal  小節（既定 1）
/// </code>
/// ＝ うまく叩けたご褒美として巻ける時間が伸びる。
/// <see cref="Phase.LeadIn"/> はバトル開始直後にだけ 1 度通る特別な区間で、
/// メトロノームだけが leadInBeats 拍ぶん鳴り、出題・回答・巻きは一切行わない
/// （中央テキストには残り拍数「4 3 2 1」を出す）。時計の原点（clockTime == 0）を
/// 「余白が終わって最初の Call の小節頭になる瞬間」に置き、余白中は clockTime を
/// 負の値として扱うことで、余白の長さが拍子の倍数でなくても Call 側の小節頭と
/// ズレずに接続できる（詳細は <see cref="EnterLeadInOrCall"/>）。
/// 次のフェーズは<b>1 拍前</b>に中央テキストで予告する（LeadIn を除く）。
///
/// ■ フレーズ（出題・回答で共有する打点の並び）
/// 魚のリズムパターン（8 分音符の並び。'x' が打点・'.' が休符）は<b>1 行 ＝ 1 小節</b>が基本だが、
/// 小節数ぶんの長さ（例: 2 小節 ＝ 拍子 × 2 × 2 文字）の行もそのまま使える。
/// 出題フェーズの小節数ぶんの「フレーズ」を、パターンをランダムに（重複可で）
/// 連結して組み立てる（<see cref="BuildPhrase"/>）。回答フェーズでは同じフレーズを
/// 先頭から並べ直す（回答が長ければ循環させる）ので、出題と回答で打点位置が一致する。
///
/// ■ 出題（Call）
/// 打点の時刻ごとに<b>前アタリと同じ演出</b>（つつき音＋ウキの沈み）を出す
/// （<see cref="FishingController.PlayNibbleCue"/>）。打点の時刻は
/// 「フェーズ開始時刻 ＋ 分割番号 × 1 分割の秒数」で<b>解析的に</b>先に確定させ、
/// clockTime がその時刻に達した最初のフレームで鳴らす（1 フレームが長くても取りこぼさない）。
/// 出題中の左クリックは、次の回答の受付窓に入っていなければ Miss。
///
/// ■ 回答（Answer）
/// 同じフレーズを<b>左クリック</b>で再現する。打点ごとに最も近いクリックとの時間差 |Δt| で
/// <code>
/// |Δt| ≦ excellentSeconds → Excellent   （テンション減）
/// |Δt| ≦ greatSeconds     → Great
/// |Δt| ≦ niceSeconds      → Nice
/// 上記のいずれでもない（窓を過ぎた）→ Miss
/// どの打点にも結び付かない余分なクリック → Miss
/// </code>
/// を判定し、判定画像と「早い／遅い」のヒントを出す（表示はコントローラ側の共通 UI）。
///
/// ■ 糸の残り（1〜0・一方向・回復なし。旧「テンション」を置き換えたもの）
/// <code>
/// 開始値       : 合わせランクで決まる（Excellent 1.0 / Great 0.9 / Nice 0.8）
/// 判定ごと     : 糸の残り -= |Δt| × linePerSecondOfOffset × レベル補正 ÷ 糸パワー補正
///                Excellent は減らない
/// Miss・空打ち : 糸の残り -= missLoss ÷ 糸パワー補正
/// 回復手段     : 一切無い（隙(Rest)中も回復しない）
/// レベル補正   = 1 + tensionLevelScale × (魚の総合力 ÷ 竿パワー − 1)（下限 0.5）
/// 糸パワー補正 = 1 + linePowerLossReduction × (糸パワー − 1)
/// 糸の残り ≦ 0 → 糸が切れる（<see cref="LineBroken"/>）
/// </code>
///
/// ■ 魚 HP（巻き取り）
/// <code>
/// 掛かった瞬間の魚の総合力 p0 ＝ 基礎パワー × 大きさスコア
/// 魚の取り分 share    ＝ p0 ÷ (竿パワー ＋ p0)
/// 魚HP最大            ＝ 基礎HP ＋ 基礎HP × share
/// 1HP あたりの距離     ＝ 掛かった瞬間の距離 ÷ 基礎HP（距離は hookDistanceMin で下限クランプ）
/// 目標距離            ＝ 現在の魚HP × 1HP あたりの距離
/// 巻き効率            ＝ 竿パワー ÷ (竿パワー ＋ 魚の総合力)
/// 巻き 1m あたりの HP  ＝ 巻き効率 ÷ 1HP あたりの距離（<see cref="ReelHpPerUnit"/>）
/// </code>
/// <b>巻けるのは「隙(Rest)」のあいだだけ</b>。出題・回答中は巻き入力を無視し、
/// ウキも動かさない（拍を読むあいだ画が暴れないようにするため）。
/// 魚 HP が 0 になった瞬間が釣り上げ成立（<see cref="FishDefeated"/>）。
///
/// ■ 合わせランクの影響
/// 合わせが悪いほど初期の糸の残りが少ない（＝危険側から始まる）。
/// ────────────────────────────────────────────────────────
///
/// <b>UI（円形のリズム時計）</b>
/// 画面中央の円（<c>GaugeSeg00</c>… の 48 セグメント＋<c>GaugeMarker</c>
/// ＋ <c>BeatIcon00</c>… の打点アイコン）を時計として使う。
/// - マーカー（針）… <b>フェーズの進行</b>（<see cref="PhaseProgress01"/> × 360 度・
///   真上がフェーズ頭・右回り）。出題も回答も 2 小節なので、<b>1 フェーズで針が 1 周</b>する。
///   隙（1〜2 小節）・余白でも同じく、その区間の長さで 1 周する。
/// - セグメント … 真上から右回りに糸の残りの円弧（<see cref="Line01"/> × 360 度・
///   減った分だけ空きセグメントに置き換わる）。円弧の色は満タン(緑)→中間(黄)→危険(赤)へ補間。
///   <b>セグメントは糸ゲージ専用</b>で、打点は一切描かない（旧「拍マーク」は廃止）。
/// - 打点アイコン … フレーズの打点 1 つにつき 1 枚（<see cref="beatIconSprites"/>）。
///   角度は打点の<b>時刻</b>から解析的に求める（θ ＝ (打点時刻 − フェーズ開始) ÷ フェーズ長 × 360 度）
///   ので、セグメントの分割数には一切依存しない。半径は <see cref="beatIconRadiusPx"/>。
///   ライフサイクルは下記。
/// <code>
/// 出題: 打点の音が鳴った<b>後</b>に 1 枚ずつ出現（easeOutBack で 0→1 に拡大・出題色）
/// 回答: 同じ位置のまま暗い未判定色へ。判定した瞬間に判定色（Excellent 白 / Great 淡緑 /
///       Nice 黄 / Miss 赤）で小さく跳ねる。余分なクリックにはアイコンを出さない
/// 隙  : 受付窓が閉じたところから beatIconFadeSeconds 秒でフェードアウト
/// </code>
/// - 中央テキスト … フェーズ名（＋予告）／魚 HP ％
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

    /// <summary>糸の残りの下限（ここに達すると糸が切れる）。</summary>
    private const float Line01Min = 0f;

    /// <summary>糸の残りの上限（満タン）。</summary>
    private const float Line01Max = 1f;

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

    /// <summary>
    /// ドラムループ素材の拍子（4/4 前提）。素材の小節数から総拍数を出すのに使う。
    /// 魚側の拍子（<see cref="beatsPerBar"/>）とは別物なので混同しないこと。
    /// </summary>
    private const int DrumMaterialBeatsPerBar = 4;

    /// <summary>ドラムループ素材の小節数の下限。</summary>
    private const int MinDrumLoopBars = 1;

    /// <summary>ドラムループ素材の BPM の下限（0 除算・速度破綻の番人値）。</summary>
    private const float MinDrumLoopBpm = 1f;

    /// <summary>ドラムループを再同期しないことを表す小節数。</summary>
    private const int DrumResyncDisabled = 0;

    /// <summary>再生速度の既定値（等倍）。</summary>
    private const float NormalPlaybackSpeed = 1f;

    /// <summary>音量の下限（負の音量を渡さないためのクランプ値）。</summary>
    private const float VolumeMin = 0f;

    /// <summary>
    /// 「小節頭／ループ境界にいるか」を判定するときの許容誤差（小節数・ループ数の単位）。
    /// ちょうど境界の時刻が浮動小数の丸めで境界の直前に見えると、
    /// 切り上げが 1 区間ぶん余計に進んでしまうため、その幅だけ手前を境界とみなす。
    /// </summary>
    private const float GridEpsilon = 0.0001f;

    /// <summary>「魚データ側の指定なし（＝バトル側の既定を使う）」を表す値。</summary>
    private const int UseFightDefaultBars = 0;

    /// <summary>レベル補正（テンションの効き）の下限。</summary>
    private const float LevelScaleMin = 0.5f;

    /// <summary>まだ 1 度も拍を鳴らしていないことを表す番兵値。</summary>
    private const int NoBeatPlayed = -1;

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

    /// <summary>
    /// easeOutBack の跳ね返り係数（一般的な実装値）。
    /// 0→1 の補間の終わりで 1 を少し超えてから戻る「ポップ」感を作る。
    /// </summary>
    private const float EaseBackOvershoot = 1.70158f;

    /// <summary>easeOutBack の 3 次項の係数（＝ <see cref="EaseBackOvershoot"/> ＋ 1）。</summary>
    private const float EaseBackCubic = EaseBackOvershoot + 1f;

    /// <summary>スケールの基準値（1 ＝ 等倍）。</summary>
    private const float NormalScale = 1f;

    /// <summary>まだ 1 度も打点の音を鳴らしていないことを表す番兵（打点番号）。</summary>
    private const int NoHitFired = -1;

    /// <summary>「この打点はまだ出現していない」ことを表すポップ開始時刻の番兵。</summary>
    private const float IconHidden = float.MaxValue;

    /// <summary>フェードしていない（＝完全に不透明側）ことを表すフェード開始時刻の番兵。</summary>
    private const float NoFadeStart = float.MaxValue;

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

    /// <summary>
    /// 出題（魚が叩く）フェーズの長さ（小節）。魚データが 1 以上ならそちらが優先。
    /// 既定は 2 小節（＝針が 1 周するあいだに 2 小節ぶんのフレーズを出題する）。
    /// </summary>
    [Header("フェーズの長さ(小節)"), SerializeField(Label = "出題の小節数")]
    private int callBars = 2;

    /// <summary>
    /// 回答（プレイヤーが叩く）フェーズの長さ（小節）。魚データが 1 以上ならそちらが優先。
    /// 出題と同じ長さにしておくと、打点アイコンが出題時とまったく同じ角度に並ぶ。
    /// </summary>
    [SerializeField(Label = "回答の小節数")]
    private int answerBars = 2;

    /// <summary>
    /// 隙（巻きに専念できる）フェーズの長さ（小節）― <b>回答が完璧だったとき</b>。
    /// 完璧 ＝ 期待打点がすべて Excellent かつ余分なクリックが 0（<see cref="EvaluateAnswerPerfect"/>）。
    /// </summary>
    [SerializeField(Label = "隙の小節数(完璧)")]
    private int restBarsPerfect = 2;

    /// <summary>隙フェーズの長さ（小節）― <b>完璧でなかったとき</b>（既定の隙）。</summary>
    [SerializeField(Label = "隙の小節数(通常)")]
    private int restBarsNormal = 1;

    /// <summary>
    /// バトル開始直後に置く「余白」の長さ（拍）。<see cref="Phase.LeadIn"/> の長さそのもの。
    /// 秒数固定ではなく<b>魚の BPM（<see cref="secondsPerBeat"/>）で数える拍数</b>なので、
    /// 連鎖で乗り換えて BPM が変わった場合も新しい魚の拍でこの余白が取られる
    /// （<see cref="BeginFight"/> が毎回 <see cref="SetupRhythm"/> の後に長さを計算し直すため）。
    /// 0 なら余白なしで即座に出題（Call）から始まる。
    /// </summary>
    [SerializeField(Label = "開始時の余白(拍)")]
    private int leadInBeats = 4;

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

    // ─── ドラムループ ────────────────────────────────────
    //
    // バトル中だけ BGM 枠でドラムループを鳴らし、再生速度を
    // 「魚の BPM ÷ 素材の BPM」に変えて魚の拍と完全に同じテンポにする。
    // メトロノームと二重で鳴らす前提だが、metronomeVolume 系を 0 にすれば
    // ドラムだけにもできる（メトロノームは拍の頭を鳴らす別系統のまま）。

    /// <summary>ドラムループ素材のアセットパス（空ならドラムを鳴らさない）。</summary>
    [Header("ドラムループ"), SerializeField(Label = "ドラムループの音源")]
    private string drumLoopPath = "assets://mainGame/audios/drum.wav";

    /// <summary>ドラムループ素材そのものの BPM（この値を基準に再生速度を決める）。</summary>
    [SerializeField(Label = "素材のBPM")]
    private float drumLoopBpm = 100f;

    /// <summary>ドラムループ素材の小節数（4/4 前提）。ループ長＝この小節数ぶんの拍。</summary>
    [SerializeField(Label = "素材の小節数(4/4)")]
    private int drumLoopBars = 2;

    /// <summary>ドラムループの音量（1.0 = 等倍）。</summary>
    [SerializeField(Label = "ドラムループの音量")]
    private float drumLoopVolume = 0.8f;

    /// <summary>
    /// 出題（Call）フェーズの頭ごとにドラムループを鳴らし直すか【既定の同期手段】。
    ///
    /// 隙の長さが 1 小節／2 小節と変わるため、ループ長（既定 2 小節）と小節線の関係が
    /// サイクルごとにズレる。出題の頭で必ず鳴らし直せば、<b>各フェーズ＝ループ 1 周</b>が
    /// 常に保証される（出題 2 小節・素材 2 小節の既定構成の場合）。
    /// false にすると <see cref="drumResyncBars"/> による定期再同期だけになる。
    /// </summary>
    [SerializeField(Label = "出題の頭でループを鳴らし直す")]
    private bool drumRestartAtCall = true;

    /// <summary>
    /// ドラムループを鳴らし直して位相ズレを消す間隔（小節）。0 なら再同期しない。
    /// <see cref="drumRestartAtCall"/> が true なら基本的に不要な補助機能。
    /// 実際の再同期はこの小節数<b>以上</b>で、かつ「ループ先頭かつ魚の小節頭」に
    /// なる最小周期の整数倍になる（<see cref="SetupDrumLoop"/> で算出）。
    /// </summary>
    [SerializeField(Label = "再同期の間隔(小節・0で無効)")]
    private int drumResyncBars = 8;

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

    // ─── 糸の残り ────────────────────────────────────────

    /// <summary>時間差 1 秒あたりに減る糸の残り（レベル補正・糸パワー補正が掛かる）。</summary>
    [Header("糸の残り"), SerializeField(Label = "時間差1秒あたりの糸の減り")]
    private float linePerSecondOfOffset = 0.6f;

    /// <summary>Miss（打ち逃し・空打ち）1 回で減る糸の残り（糸パワー補正が掛かる）。</summary>
    [SerializeField(Label = "Missの糸の減り")]
    private float missLoss = 0.12f;

    /// <summary>
    /// 戦闘力差が糸の減り方へ効く強さ。
    /// レベル補正 ＝ 1 + 本値 × (魚の総合力 ÷ 竿パワー − 1)（下限 <see cref="LevelScaleMin"/>）。
    /// 0 なら戦闘力差を無視する。
    /// </summary>
    [SerializeField(Label = "戦闘力差の効き")]
    private float tensionLevelScale = 1f;

    /// <summary>
    /// 糸パワーによる減り軽減の強さ。
    /// 糸パワー補正 ＝ 1 + 本値 × (糸パワー − 1)。糸の減り量はこの補正で割られる
    /// （糸パワーが高いほど、同じ判定でも糸の減りが小さくなる）。
    /// </summary>
    [SerializeField(Label = "糸パワーの減り軽減")]
    private float linePowerLossReduction = 0.25f;

    // ─── 合わせランクによる初期の糸の残り ─────────────────────

    /// <summary>Excellent で合わせたときの初期の糸の残り。</summary>
    [Header("初期の糸の残り(合わせランク)"), SerializeField(Label = "初期の糸の残り(Excellent)")]
    private float initialLineExcellent = 1.0f;

    /// <summary>Great で合わせたときの初期の糸の残り。</summary>
    [SerializeField(Label = "初期の糸の残り(Great)")]
    private float initialLineGreat = 0.9f;

    /// <summary>
    /// Nice で合わせたときの初期の糸の残り。
    /// 判定が取れていない場合（None など）のフォールバックにも使う（＝最も不利な値）。
    /// </summary>
    [SerializeField(Label = "初期の糸の残り(Nice)")]
    private float initialLineNice = 0.8f;

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

    // ─── ヒット直後の引き（余白＝LeadIn 中に沖へ走る演出）───────────

    /// <summary>
    /// <see cref="Fish.HookRunDistance"/> が 0 以下（未設定）だったときのフォールバック値（メートル）。
    /// 通常は魚データ側（Fish）の値を使うので、これは保険。
    /// </summary>
    [Header("ヒット直後の引き(LeadIn)"), SerializeField(Label = "引き距離の既定値(m)")]
    private float hookRunDistanceDefault = 30f;

    /// <summary>
    /// <see cref="Fish.SizeRank"/> が S（最大サイズ帯）のときに <see cref="Fish.HookRunDistance"/>
    /// へ掛ける係数。ランクの閾値そのものは <see cref="Fish"/> 側（<see cref="Fish.SizeRank"/>）に
    /// 集約してあり、ここには持たない（重複させると閾値だけ食い違う事故の元になる）。
    /// </summary>
    [SerializeField(Label = "引き距離倍率(Sランク)")]
    private float runDistanceRankS = 1.5f;

    /// <summary>Fish.SizeRank が A のときの倍率。</summary>
    [SerializeField(Label = "引き距離倍率(Aランク)")]
    private float runDistanceRankA = 1.25f;

    /// <summary>Fish.SizeRank が B のときの倍率。</summary>
    [SerializeField(Label = "引き距離倍率(Bランク)")]
    private float runDistanceRankB = 1.0f;

    /// <summary>Fish.SizeRank が C（それ以外）のときの倍率。</summary>
    [SerializeField(Label = "引き距離倍率(Cランク)")]
    private float runDistanceRankC = 0.8f;

    // ─── 効果音 ──────────────────────────────────────────

    /// <summary>糸が切れた瞬間に鳴らす効果音のアセットパス（空なら鳴らさない）。</summary>
    [Header("効果音"), SerializeField(Label = "糸切れの効果音")]
    private string lineBreakSePath = "";

    /// <summary>糸切れ効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "糸切れの音量")]
    private float lineBreakSeVolume = 1f;

    /// <summary>
    /// 回答（<see cref="Phase.Answer"/>）中に左クリックするたび鳴らす効果音のアセットパス（空なら鳴らさない）。
    /// 判定（Excellent/Great/Nice/Miss）に関わらず、クリックそのものの手応えとして毎回鳴らす。
    /// </summary>
    [SerializeField(Label = "回答クリックの効果音")]
    private string answerClickSePath = "assets://mainGame/audios/Motion-Swish07-1.mp3";

    /// <summary>回答クリック効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "回答クリックの音量")]
    private float answerClickSeVolume = 0.8f;

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

    /// <summary>
    /// 打点アイコンのスプライト（<c>BeatIcon00</c>… を順に割り当てる）。
    /// アイコン i は「いまのフレーズの i 番目の<b>打点</b>」に対応する
    /// （分割番号ではなく打点の通し番号）。フレーズの打点数がこの個数を超えた分は
    /// アイコンを持たない（＝表示されないだけで判定には影響しない）。未設定でもロジックは成立する。
    /// </summary>
    [SerializeField(Label = "打点アイコンのSprite")]
    private List<SEED.Sprite> beatIconSprites = new();

    /// <summary>
    /// 打点アイコンの CanvasTransform（位置と拡大率を書き換える）。
    /// <see cref="beatIconSprites"/> と<b>同じ順・同じアクタ</b>を割り当てること。
    /// </summary>
    [SerializeField(Label = "打点アイコンのCanvasTransform")]
    private List<SEED.CanvasTransform> beatIconTransforms = new();

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

    /// <summary>糸の残りが尽きた（円弧が届いていない）セグメントの色（RGB）。</summary>
    [SerializeField(Label = "空きセグメントの色(RGB)")]
    private SEED.Vector3 emptyColor = new SEED.Vector3(0.25f, 0.28f, 0.32f);

    /// <summary>糸の残りが満タン（<see cref="Line01"/> ＝ 1）のときの色（RGB）。</summary>
    [SerializeField(Label = "満タンの色(RGB)")]
    private SEED.Vector3 fullColor = new SEED.Vector3(0.2f, 0.9f, 0.3f);

    /// <summary>糸の残りが中間（<see cref="Line01"/> ＝ 0.5）のときの色（RGB）。</summary>
    [SerializeField(Label = "中間の色(RGB)")]
    private SEED.Vector3 midColor = new SEED.Vector3(1f, 0.85f, 0.2f);

    /// <summary>糸の残りが危険（<see cref="Line01"/> ＝ 0）のときの色（RGB）。</summary>
    [SerializeField(Label = "危険の色(RGB)")]
    private SEED.Vector3 dangerColor = new SEED.Vector3(1f, 0.2f, 0.15f);

    /// <summary>
    /// 打点アイコンを置く円の半径（ピクセル）。
    /// 糸ゲージの円（<see cref="arcRadiusPx"/>）の外側に置きたいので、既定は
    /// 「円の半径 140 ＋ 26」＝ 166。半径だけ独立に動かせるよう別フィールドにしてある。
    /// </summary>
    [SerializeField(Label = "打点アイコンの半径(px)")]
    private float beatIconRadiusPx = 166f;

    /// <summary>打点アイコンの一辺の大きさ（ピクセル・正方形）。</summary>
    [SerializeField(Label = "打点アイコンの大きさ(px)")]
    private float beatIconSizePx = 22f;

    /// <summary>打点アイコンが出現するときの拡大アニメの秒数（easeOutBack で 0 → 1）。</summary>
    [SerializeField(Label = "打点アイコンの出現秒数")]
    private float beatIconPopSeconds = 0.25f;

    /// <summary>隙に入ってから打点アイコンが消えるまでの秒数（アルファのフェード）。</summary>
    [SerializeField(Label = "打点アイコンの消滅秒数")]
    private float beatIconFadeSeconds = 0.3f;

    /// <summary>出題中に出現した打点アイコンの色（RGB・魚が叩いた合図の色）。</summary>
    [SerializeField(Label = "打点アイコンの色(出題)")]
    private SEED.Vector3 beatIconCallColor = new SEED.Vector3(1f, 0.85f, 0.3f);

    /// <summary>回答中でまだ判定していない打点アイコンの色（RGB・暗め）。</summary>
    [SerializeField(Label = "打点アイコンの色(未判定)")]
    private SEED.Vector3 beatIconPendingColor = new SEED.Vector3(0.42f, 0.46f, 0.52f);

    /// <summary>Excellent と判定した打点アイコンの色（RGB）。</summary>
    [SerializeField(Label = "打点アイコンの色(Excellent)")]
    private SEED.Vector3 beatIconExcellentColor = new SEED.Vector3(1f, 1f, 1f);

    /// <summary>Great と判定した打点アイコンの色（RGB）。</summary>
    [SerializeField(Label = "打点アイコンの色(Great)")]
    private SEED.Vector3 beatIconGreatColor = new SEED.Vector3(0.55f, 1f, 0.6f);

    /// <summary>Nice と判定した打点アイコンの色（RGB）。</summary>
    [SerializeField(Label = "打点アイコンの色(Nice)")]
    private SEED.Vector3 beatIconNiceColor = new SEED.Vector3(1f, 0.95f, 0.35f);

    /// <summary>Miss（打ち逃し）だった打点アイコンの色（RGB）。</summary>
    [SerializeField(Label = "打点アイコンの色(Miss)")]
    private SEED.Vector3 beatIconMissColor = new SEED.Vector3(1f, 0.25f, 0.25f);

    /// <summary>打点アイコンの不透明度（バトル中・フェード前）。</summary>
    [SerializeField(Label = "打点アイコンの不透明度")]
    private float beatIconOpacity = 1f;

    /// <summary>出題中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "マーカーの色(出題)")]
    private SEED.Vector3 markerCallColor = new SEED.Vector3(0.4f, 0.85f, 1f);

    /// <summary>回答中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "マーカーの色(回答)")]
    private SEED.Vector3 markerAnswerColor = new SEED.Vector3(1f, 0.95f, 0.3f);

    /// <summary>隙（Rest）中のマーカー色（RGB）。</summary>
    [SerializeField(Label = "マーカーの色(隙)")]
    private SEED.Vector3 markerRestColor = new SEED.Vector3(1f, 1f, 1f);

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

        /// <summary>
        /// 開始直後の余白（<see cref="leadInBeats"/> 拍ぶんメトロノームだけが鳴る）。
        /// 出題・回答・巻きは一切行わず、ウキも動かさない。この拍数を数え終えた
        /// 次の拍（＝時計の原点 0）から通常の <see cref="Call"/> が始まる。
        /// </summary>
        LeadIn,

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
    /// バトルの一時停止フラグ【外部都合で進行を止めたいときの汎用フック】。
    ///
    /// true のあいだ <see cref="Tick"/> は拍時計・テンション・疲労・魚 HP を一切進めず、
    /// メトロノームも鳴らさず、ウキの移動量（<see cref="ComputeFloatDistanceStep"/>）も 0 にして、
    /// UI の再描画だけを行う。<see cref="EndFight"/> で必ず false へ戻る。
    ///
    /// 2026-09-06 現在、わらしべ連鎖は前アタリを経由しなくなった（<c>FishingController.
    /// TryEatHookedFish</c> が「隙（Rest）」中に即成立させるだけ）ため、これを true にする
    /// 呼び出し元は無い。将来また進行を止める必要が出たときのためのフックとして残してある。
    /// </summary>
    public bool Paused { get; set; } = false;

    /// <summary>現在のフェーズ（非バトル中は <see cref="Phase.None"/>）。</summary>
    public Phase CurrentPhase { get; private set; } = Phase.None;

    /// <summary>現在の糸の残り（1〜0）。満タン 1.0 から一方向に減り、0 で糸が切れる。回復手段は無い。</summary>
    public float Line01 { get; private set; } = Line01Max;

    /// <summary>
    /// 糸が切れたか。糸の残り（<see cref="Line01"/>）が 0 に達したフレームで true になる。
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
    /// ＝ 巻き効率（竿パワー ÷ (竿パワー ＋ 魚の総合力)）÷ 1HP あたりの距離。
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
    /// <b>いまのフェーズのなかの進行（0〜1）</b>【円形 UI の針の角度の唯一の元】。
    /// 0 がフェーズ頭・1 がフェーズ終わり。針は 1 フェーズで必ず 1 周する。
    /// フェーズ長が 0 以下（データ異常）のときは 0 を返す。
    /// </summary>
    public float PhaseProgress01
    {
        get
        {
            float duration = phaseEndTime - phaseStartTime;
            if (duration <= DivideEpsilon) { return 0f; }
            return SEED.Mathf.Clamped01((clockTime - phaseStartTime) / duration);
        }
    }

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
    /// 魚 HP 1 あたりの距離（メートル）＝（掛かった瞬間の距離 ＋ 引き距離）÷ 魚 HP 最大値。
    /// 魚 HP と「ウキ→竿先の距離」を相互変換する唯一の係数。
    /// この定義により、余白（LeadIn）終了時点（＝魚 HP がまだ満タン）でちょうど
    /// 「距離 ＝ 掛かった瞬間の距離 ＋ 引き距離」（＝<see cref="DesiredFloatDistance"/>）に一致する。
    /// </summary>
    private float metersPerHp = 0f;

    /// <summary>
    /// 掛かった瞬間のウキ→竿先の距離（下限クランプ済み・メートル）。
    /// 余白（<see cref="Phase.LeadIn"/>）中に <see cref="DesiredFloatDistance"/> まで
    /// 滑らかに引き伸ばすときの開始距離として使う。
    /// </summary>
    private float leadInStartDistance = 0f;

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

    /// <summary>このバトルでドラムループを鳴らす予定か（パス未設定なら false）。</summary>
    private bool drumScheduled = false;

    /// <summary>ドラムループがいま実際に鳴っているか（開始待ち・一時停止中は false）。</summary>
    private bool drumPlaying = false;

    /// <summary>
    /// ドラムループの（再）開始時刻＝ループ位相 0 の時刻（<see cref="clockTime"/> と同じ時間軸）。
    /// 必ず魚の小節頭に一致する。
    /// </summary>
    private float drumStartTime = 0f;

    /// <summary>
    /// ドラムループの位相が「ループ先頭かつ魚の小節頭」へ戻る最小周期（秒）。
    /// 開始・再同期・一時停止からの復帰はすべてこの周期の整数倍の時刻で行う。
    /// </summary>
    private float drumUnitSeconds = 0f;

    /// <summary>ドラムループを鳴らし直す周期（秒）。0 以下なら再同期しない。</summary>
    private float drumResyncSeconds = 0f;

    /// <summary><see cref="Paused"/> によってドラムを止めたか（再開時に整列して鳴らし直す）。</summary>
    private bool drumPausedByFight = false;

    /// <summary>現在のフェーズが始まった時刻（秒・必ず小節頭）。</summary>
    private float phaseStartTime = 0f;

    /// <summary>現在のフェーズが終わる時刻（秒・必ず小節頭）。</summary>
    private float phaseEndTime = 0f;

    /// <summary>現在のフェーズの長さ（小節）。</summary>
    private int phaseBars = 1;

    /// <summary>次のフェーズの予告（1 拍前）を済ませたか。</summary>
    private bool nextPhaseAnnounced = false;

    /// <summary>
    /// このバトルで使う有効なリズムパターン（1 要素 ＝ 1 小節<b>以上</b>ぶんの文字列。
    /// 長さは必ず <see cref="subsPerBar"/> の正の整数倍）。
    /// </summary>
    private readonly List<string> patterns = new();

    /// <summary>
    /// いま出題／回答しているフレーズ（出題フェーズの小節数ぶんの長さ）。
    /// パターンを連結して <see cref="BuildPhrase"/> が組み立てる。
    /// </summary>
    private string currentPhrase = "";

    /// <summary>出題フェーズで鳴らす打点の時刻（秒・絶対時刻・昇順）。</summary>
    private readonly List<float> callHitTimes = new();

    /// <summary>出題フェーズの打点の分割番号（フェーズ内の通し番号。UI の角度算出に使う）。</summary>
    private readonly List<int> callHitSubs = new();

    /// <summary>
    /// 出題フェーズで最後に音を鳴らした打点の添字（<see cref="NoHitFired"/> ＝ 未再生）。
    /// 打点は時刻順なので、この添字より後ろを順に見るだけで取りこぼしなく鳴らせる。
    /// </summary>
    private int lastFiredCallHit = NoHitFired;

    /// <summary>回答フェーズで期待している打点の時刻（秒・絶対時刻）。</summary>
    private readonly List<float> expectedTimes = new();

    /// <summary>期待打点が判定済みか（true ＝ もう結び付かない）。</summary>
    private readonly List<bool> expectedJudged = new();

    /// <summary>期待打点の判定結果（アイコン色の決定に使う）。</summary>
    private readonly List<FishingController.HookJudgement> expectedResults = new();

    /// <summary>
    /// このサイクルで発生した「どの打点にも結び付かないクリック」の回数。
    /// 出題フェーズの頭で 0 に戻り、隙の長さ（完璧判定）に使う。
    /// </summary>
    private int extraClickCount = 0;

    /// <summary>直前の回答が完璧（全 Excellent ＆ 余分なクリック 0）だったか。隙の長さを決める。</summary>
    private bool lastAnswerPerfect = false;

    /// <summary>
    /// 打点アイコンの出現（ポップ）開始時刻（秒・絶対時刻）。
    /// <see cref="IconHidden"/> なら「まだ出現していない」。判定した瞬間に判定時刻で上書きして跳ねさせる。
    /// 添字は<b>打点の通し番号</b>（分割番号ではない）。
    /// </summary>
    private readonly List<float> iconPopStartTimes = new();

    /// <summary>打点アイコンの色（RGB）。出題色 → 未判定色 → 判定色と書き換わる。</summary>
    private readonly List<SEED.Vector3> iconColors = new();

    /// <summary>打点アイコンを置く角度（度・真上が 0・右回り）。フェーズが変わるたびに計算し直す。</summary>
    private readonly List<float> iconDegrees = new();

    /// <summary>
    /// 打点アイコンのフェードアウト開始時刻（秒・絶対時刻）。
    /// <see cref="NoFadeStart"/> ならフェードしていない。
    /// </summary>
    private float iconFadeStartTime = NoFadeStart;

    /// <summary>打点アイコンへ大きさを書き込んだ枚数（枚数が変わったときだけ書き直すための控え）。</summary>
    private int iconSizeAppliedCount = 0;

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
    /// 初期の糸の残りは<b>合わせランク</b>から決める（ランクが悪いほど少ない＝危険側）。
    /// </summary>
    /// <param name="fish">掛かった魚（パラメータを読むだけで一切動かさない）。</param>
    /// <param name="judge">合わせ判定（初期の糸の残りの決定に使う）。</param>
    /// <param name="hookDistance">
    /// 掛かった瞬間のウキ→竿先の水平距離（メートル）。
    /// 「魚 HP 1 あたりの距離」の基準になる（<see cref="hookDistanceMin"/> で下限クランプ）。
    /// </param>
    public void BeginFight(Fish fish, FishingController.HookJudgement judge, float hookDistance)
    {
        ResetRuntimeState();

        target = fish;
        Active = true;
        Line01 = SEED.Mathf.Clamped(InitialLine(judge), Line01Min, Line01Max);

        // 魚 HP: 掛かった瞬間の総合力で「魚の取り分」を出し、その割合ぶんだけ基礎HP へ乗せる
        float rod = SEED.Mathf.Max(rodPower, DivideEpsilon);
        float hookPower = SEED.Mathf.Max(fish.BasePower * SizeScore(fish), 0f);
        float fishShare = hookPower / SEED.Mathf.Max(rod + hookPower, DivideEpsilon);
        float baseHp = SEED.Mathf.Max(fish.BaseHp, DivideEpsilon);
        fishHpMax = baseHp + baseHp * fishShare;
        fishHp = fishHpMax;

        // 距離との対応付け: 「掛かった瞬間の距離 ＋ ヒット直後に沖へ引かれる距離」を
        // 魚 HP 最大値ぶんの距離とみなす（＝余白(LeadIn)終了時点で距離とHPがちょうど対応する）。
        leadInStartDistance = SEED.Mathf.Max(hookDistance, hookDistanceMin);
        float runDistance = HookRunDistanceFor(fish);
        metersPerHp = (leadInStartDistance + runDistance) / SEED.Mathf.Max(fishHpMax, DivideEpsilon);

        // 拍時計とパターンを魚データから作る（この魚の BPM で secondsPerBeat が決まる）
        SetupRhythm(fish);

        // 最初のフェーズへ入る。
        //
        // 余白（LeadIn）の実装方針: 時計の原点（clockTime == 0）を「余白が終わって
        // 最初の Call の小節頭になる瞬間」に固定し、余白のあいだは clockTime を
        // 負の値（-leadInBeats 拍ぶん）から 0 へ向けて進める。
        // BeatIndex / BarPhase01 などの拍時計はすべて clockTime の単純な割り算・floor で
        // できているので、負の時刻でも「0 を跨ぐと拍・小節が変わる」という性質はそのまま保たれる。
        // ＝ 0 に達した瞬間が必ず小節頭になり、Call 側の EnterPhase(CurrentBarStartTime()) と
        // 完全に一致する（余白の拍数が拍子の倍数でなくてもズレない）。
        EnterLeadInOrCall();

        // ドラムループは拍時計の原点が決まってから仕込む
        //（開始時刻を余白の開始＝いまの clockTime から算出するため、必ずこの順序で呼ぶ）
        SetupDrumLoop();

        InvalidateSegmentCache();
        ApplyUi();

        SEED.Debug.Log($"[Fight] 開始: {fish.DisplayName} / 総合力 {CurrentFishPower():F2} vs 竿 {rodPower:F2}"
                     + $" / 魚HP {fishHpMax:F1}（取り分 {fishShare:P0}）"
                     + $" / 掛かった距離 {hookDistance:F1}m → 目標 {DesiredFloatDistance:F1}m"
                     + $" / {BpmOf(fish):F0}BPM {beatsPerBar}拍子 / パターン {patterns.Count} 種"
                     + $" / 初期の糸の残り {Line01:F2}");
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
            // 拍時計だけが止まってドラムが鳴り続けると位相が壊れるので、ドラムも止める。
            // 再開時は次のループ境界（＝小節頭）へ揃えて鳴らし直す（UpdateDrumLoop 参照）。
            if (drumPlaying)
            {
                StopDrumLoop();
                drumPausedByFight = true;
            }
            ApplyUi();
            return;
        }

        clockTime += deltaTime;

        UpdateDrumLoop();
        UpdateMetronome();
        UpdatePhaseTransition();

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

        // 打点アイコンの出現・フェードはすべて絶対時刻から求めるので、進めるタイマーは無い
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

    /// <summary>
    /// このフレームにウキ→竿先の距離を動かす量（メートル・符号つき）を返す
    /// 【ウキの移動量の唯一の算出点】。
    ///
    /// ＋ ＝ 沖へ（距離が増える） / − ＝ 手元へ（距離が減る）。
    /// <b>隙（Rest）のあいだ</b>は魚 HP に応じた目標距離へ寄る／引かれる。
    /// 出題・回答中はウキを止めておき、拍を読む画面が揺れないようにする。
    /// <b>余白（<see cref="Phase.LeadIn"/>）中だけは例外</b>で、掛かった直後に沖へ走る演出として
    /// <see cref="leadInStartDistance"/> から <see cref="DesiredFloatDistance"/>（＝この時点では
    /// 魚 HP が満タンなので「掛かった距離 ＋ 引き距離」と一致する）まで、
    /// <see cref="PhaseProgress01"/> を easeOut で使い滑らかに引き伸ばす。
    /// </summary>
    /// <param name="currentDistance">現在のウキ→竿先の水平距離（メートル）。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <returns>距離の増減量（＋ が沖／− が手元）。</returns>
    public float ComputeFloatDistanceStep(float currentDistance, float deltaTime)
    {
        if (!Active || Paused || target is null) { return 0f; }

        if (CurrentPhase == Phase.LeadIn)
        {
            // easeOut(2 次): 立ち上がりは速く、余白の終わりに向けて滑らかに減速する。
            float t = SEED.Mathf.Clamped01(PhaseProgress01);
            float eased = 1f - (1f - t) * (1f - t);
            float leadInTarget = SEED.Mathf.Lerp(leadInStartDistance, DesiredFloatDistance, eased);
            return leadInTarget - currentDistance;
        }

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
                                + $"（1 行 = {subsPerBar} 文字の整数倍・'{PatternHitChar}' か '{PatternRestChar}' のみ）");
        }

        if (patterns.Count == 0) { patterns.Add(BuildFallbackPattern()); }
    }

    /// <summary>魚の BPM（0 以下なら下限へクランプ）。</summary>
    /// <param name="fish">対象の魚。</param>
    private float BpmOf(Fish fish) => SEED.Mathf.Max(fish.RhythmBpm, MinBpm);

    /// <summary>
    /// パターン文字列が有効か【パターン検証の唯一の判定点】。
    ///
    /// 長さが 1 小節の分割数（<see cref="subsPerBar"/>）の<b>正の整数倍</b>で、
    /// 打点／休符の文字だけで出来ていること。
    /// ＝ 1 小節ぶんの行も、2 小節ぶん（拍子 × 2 × 2 文字）以上の行もそのまま使える。
    /// </summary>
    /// <param name="pattern">検証する文字列。</param>
    private bool IsValidPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) { return false; }
        if (subsPerBar <= 0) { return false; }
        if (pattern.Length % subsPerBar != 0) { return false; }

        foreach (char c in pattern)
        {
            char lower = char.ToLowerInvariant(c);
            if (lower != PatternHitChar && lower != PatternRestChar) { return false; }
        }
        return true;
    }

    /// <summary>
    /// フレーズ（出題 1 回ぶんの打点の並び）を組み立てる【フレーズ生成の唯一の入口】。
    ///
    /// 必要な小節数になるまで、<b>残り小節数に収まるパターンをランダムに（重複可で）</b>
    /// 選んで後ろへ連結する。2 小節ぶんの長さを持つパターンはそのまま 2 小節として使われる。
    /// 残りに収まるパターンが 1 つも無い（例: 残り 1 小節・パターンが全部 2 小節）ときだけ、
    /// 安全なフォールバックパターン（各拍の頭）で 1 小節ぶん埋める。
    /// </summary>
    /// <param name="bars">組み立てるフレーズの小節数。</param>
    /// <returns>長さが <c>bars × <see cref="subsPerBar"/></c> の文字列。</returns>
    private string BuildPhrase(int bars)
    {
        int need = SEED.Mathf.Max(bars, MinPhaseBars);
        var buffer = new System.Text.StringBuilder(need * subsPerBar);
        int remaining = need;

        while (remaining > 0)
        {
            // 残り小節数に収まる候補だけを集める（毎回作り直すので候補が尽きても破綻しない）
            var candidates = new List<string>();
            foreach (string p in patterns)
            {
                if (p.Length / subsPerBar <= remaining) { candidates.Add(p); }
            }

            if (candidates.Count == 0)
            {
                buffer.Append(BuildFallbackPattern());
                remaining--;
                continue;
            }

            string picked = candidates[SEED.Random.Range(0, candidates.Count)];
            buffer.Append(picked);
            remaining -= picked.Length / subsPerBar;
        }

        return buffer.ToString();
    }

    /// <summary>
    /// 有効なパターンが 1 つも無いときに使う安全なパターン（各拍の頭だけを叩く・1 小節ぶん）。
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

    // ─── 内部処理: ドラムループ ─────────────────────────────

    /// <summary>
    /// ドラムループを仕込む【ドラムの時間設計を決める唯一の入口】。
    /// <see cref="SetupRhythm"/> と <see cref="EnterLeadInOrCall"/> の<b>後</b>に呼ぶこと
    /// （魚の BPM と拍時計の原点が確定していないと開始時刻を計算できない）。
    ///
    /// 【設計】
    /// ・素材は 4/4・<see cref="drumLoopBars"/> 小節と仮定し、総拍数
    ///   <c>loopBeats = drumLoopBars × 4</c> で長さを数える。
    ///   再生速度を <c>魚のBPM ÷ 素材のBPM</c> にすると、素材の 1 拍が魚の 1 拍に一致するので、
    ///   ループ長は<b>魚のテンポでの loopBeats 拍</b>になる。
    /// ・開始時刻は「いまの時刻（＝余白の開始。余白なしなら最初の Call）以降で最初の魚の小節頭」。
    ///   余白が拍子の倍数（例: 1 小節 = 4 拍）なら余白の開始そのものが小節頭なので、
    ///   要求どおり<b>余白の頭からドラムが鳴る</b>。倍数でない半端な余白のときだけ、
    ///   小節頭が来るまで開始を遅らせる（＝小節線が絶対にズレない）。
    /// ・ループ先頭が再び魚の小節頭に重なる最小周期
    ///   <c>drumUnitSeconds = loopBeats × (beatsPerBar / gcd(loopBeats, beatsPerBar)) 拍</c>
    ///   を求めておき、再同期も一時停止からの復帰もこの周期の時刻でだけ行う。
    ///   （余白 1 小節・素材 2 小節・4 拍子なら loopBeats=8, beatsPerBar=4 → 8 拍 = 2 小節ごと。）
    /// </summary>
    private void SetupDrumLoop()
    {
        StopDrumLoop();
        drumScheduled = false;
        drumPausedByFight = false;
        drumUnitSeconds = 0f;
        drumResyncSeconds = 0f;

        if (string.IsNullOrEmpty(drumLoopPath)) { return; }

        // 素材の総拍数（4/4 前提）と、位相が魚の小節頭へ戻る最小のループ回数
        int loopBeats = SEED.Mathf.Max(drumLoopBars, MinDrumLoopBars) * DrumMaterialBeatsPerBar;
        int barBeats = SEED.Mathf.Max(beatsPerBar, MinBeatsPerBar);
        int unitBeats = loopBeats * (barBeats / Gcd(loopBeats, barBeats));
        drumUnitSeconds = unitBeats * secondsPerBeat;

        // 再同期の周期: 指定小節数以上で、かつ最小周期の整数倍（＝必ずループ先頭かつ小節頭）
        if (drumResyncBars > DrumResyncDisabled && unitBeats > 0)
        {
            int neededBeats = drumResyncBars * barBeats;
            int multiplier = SEED.Mathf.Max(CeilDiv(neededBeats, unitBeats), 1);
            drumResyncSeconds = unitBeats * multiplier * secondsPerBeat;
        }

        drumStartTime = NextBarHeadAtOrAfter(clockTime);
        drumScheduled = true;

        // 開始時刻に既に達しているなら（＝余白の頭が小節頭）このフレームで鳴らし始める
        UpdateDrumLoop();
    }

    /// <summary>
    /// ドラムループを進める【開始待ち・再同期・一時停止復帰の唯一の集約点】。
    /// 拍時計を進めた直後に毎フレーム呼ぶ。
    /// </summary>
    private void UpdateDrumLoop()
    {
        if (!drumScheduled) { return; }

        // 一時停止から戻ったフレーム: 次のループ境界（＝小節頭）まで開始を持ち越す
        if (drumPausedByFight)
        {
            drumPausedByFight = false;
            drumStartTime = NextDrumBoundaryAtOrAfter(clockTime);
        }

        // 開始待ち
        if (!drumPlaying)
        {
            if (clockTime + DivideEpsilon >= drumStartTime) { StartDrumLoop(); }
            return;
        }

        // 再同期（鳴らし直しで累積したズレを消す。わずかな途切れは許容する）
        if (drumResyncSeconds <= DivideEpsilon) { return; }
        if (clockTime + DivideEpsilon >= drumStartTime + drumResyncSeconds)
        {
            drumStartTime += drumResyncSeconds;
            StartDrumLoop();
        }
    }

    /// <summary>
    /// ドラムループを実際に鳴らす【BGM 発行の唯一の出口】。
    /// 再生速度＝<c>魚の BPM ÷ 素材の BPM</c>（速度に比例してピッチも上がる点は素材側で許容する）。
    /// </summary>
    private void StartDrumLoop()
    {
        float fishBpm = secondsPerBeat > DivideEpsilon
            ? SecondsPerMinute / secondsPerBeat
            : MinBpm;
        float speed = fishBpm / SEED.Mathf.Max(drumLoopBpm, MinDrumLoopBpm);

        SEED.Audio.PlayBgm(drumLoopPath, SEED.Mathf.Max(drumLoopVolume, VolumeMin), loop: true);
        SEED.Audio.SetBgmSpeed(speed);
        drumPlaying = true;
    }

    /// <summary>ドラムループを止める（鳴っていなければ何もしない）。</summary>
    private void StopDrumLoop()
    {
        if (!drumPlaying) { return; }

        SEED.Audio.StopBgm();
        SEED.Audio.SetBgmSpeed(NormalPlaybackSpeed);   // 速度は BGM をまたいで保持されるため戻す
        drumPlaying = false;
    }

    /// <summary>指定時刻以降で最初の魚の小節頭の時刻（秒）。</summary>
    /// <param name="time">基準の時刻（<see cref="clockTime"/> と同じ時間軸）。</param>
    private float NextBarHeadAtOrAfter(float time)
    {
        float barSeconds = SecondsPerBar;
        if (barSeconds <= DivideEpsilon) { return time; }
        return SEED.Mathf.Ceil(time / barSeconds - GridEpsilon) * barSeconds;
    }

    /// <summary>
    /// 指定時刻以降で最初の「ループ先頭かつ魚の小節頭」になる時刻（秒）。
    /// <see cref="drumStartTime"/> からの <see cref="drumUnitSeconds"/> の整数倍で求める。
    /// </summary>
    /// <param name="time">基準の時刻（<see cref="clockTime"/> と同じ時間軸）。</param>
    private float NextDrumBoundaryAtOrAfter(float time)
    {
        if (drumUnitSeconds <= DivideEpsilon) { return NextBarHeadAtOrAfter(time); }
        float units = SEED.Mathf.Ceil((time - drumStartTime) / drumUnitSeconds - GridEpsilon);
        return drumStartTime + units * drumUnitSeconds;
    }

    /// <summary>最大公約数（0 以下が混ざっても 1 以上を返す番人つき）。</summary>
    /// <param name="a">値 A。</param>
    /// <param name="b">値 B。</param>
    private static int Gcd(int a, int b)
    {
        a = SEED.Mathf.Abs(a);
        b = SEED.Mathf.Abs(b);
        while (b != 0)
        {
            int t = a % b;
            a = b;
            b = t;
        }
        return a > 0 ? a : 1;
    }

    /// <summary>切り上げ除算（除数が 0 以下なら 1 を返す番人つき）。</summary>
    /// <param name="value">被除数。</param>
    /// <param name="divisor">除数。</param>
    private static int CeilDiv(int value, int divisor)
        => divisor > 0 ? (value + divisor - 1) / divisor : 1;

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

    /// <summary>
    /// バトル開始直後の入り口【<see cref="Phase.LeadIn"/> 生成の唯一の入口】。
    ///
    /// <see cref="leadInBeats"/> が 1 以上なら、時計の原点を「余白が終わる瞬間（＝最初の
    /// Call の小節頭）」に置き、余白のあいだは clockTime を負の値から 0 へ進める形で
    /// <see cref="Phase.LeadIn"/> へ入る。0 以下なら余白なしで即座に <see cref="Phase.Call"/>
    /// （通常どおり <see cref="EnterPhase"/> 経由）から始める。
    /// </summary>
    private void EnterLeadInOrCall()
    {
        int beats = SEED.Mathf.Max(leadInBeats, 0);
        if (beats <= 0)
        {
            clockTime = 0f;
            EnterPhase(Phase.Call);
            return;
        }

        float leadInSeconds = beats * secondsPerBeat;

        CurrentPhase = Phase.LeadIn;
        clockTime = -leadInSeconds;
        phaseStartTime = clockTime;
        phaseEndTime = 0f;               // 0 に達した瞬間＝最初の小節頭で Call へ
        phaseBars = MinPhaseBars;        // 小節単位のフェーズではないので参照はされない
        nextPhaseAnnounced = false;
        ClearCallHits();
        lastBeatPlayed = NoBeatPlayed;    // 余白 1 拍目の頭で必ずメトロノームが鳴るようにする

        SEED.Debug.Log($"[Fight] {PhaseLabel(Phase.LeadIn)}（{beats}拍）");
    }

    /// <summary>フェーズの巡回順（余白 → 出題 → 回答 → 隙 → 出題 …）。</summary>
    /// <param name="phase">現在のフェーズ。</param>
    private static Phase NextPhase(Phase phase) => phase switch
    {
        Phase.LeadIn => Phase.Call,
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
        // 回答フェーズを抜けても、叩かれなかった打点はここでは Miss にしない。
        // 受付窓（打点 ＋ niceSeconds）がまだ残っているものは、隙（Rest）に入ってからも
        // UpdateRest 側の CloseExpiredHits で窓を過ぎた瞬間に Miss として締める
        // （出題⇔回答の境界と同じく、回答⇔隙の境界でも早期に打ち切らないようにするため）。

        // 隙へ入る瞬間に「直前の回答が完璧だったか」を確定させる。
        // 隙の長さ（PhaseBarsOf）がこの結果を読むので、必ず長さの算出より前に評価する。
        if (next == Phase.Rest) { lastAnswerPerfect = EvaluateAnswerPerfect(); }

        CurrentPhase = next;
        phaseBars = PhaseBarsOf(next);
        phaseStartTime = CurrentBarStartTime();
        phaseEndTime = phaseStartTime + phaseBars * SecondsPerBar;
        nextPhaseAnnounced = false;

        switch (next)
        {
            case Phase.Call:
                EnterCallPhase();
                break;

            case Phase.Answer:
                // 出題で出したアイコンをそのまま残し、未判定色へ落として位置を回答フェーズ基準へ計算し直す
                //（出題も回答も同じ長さなら角度は変わらないが、長さが違う魚でも破綻しないようにする）
                BeginAnswerIcons();
                break;

            case Phase.Rest:
                // 受付窓が閉じ切ってからアイコンを消し始める（隙頭より窓のほうが後ろへはみ出す場合がある）
                iconFadeStartTime = SEED.Mathf.Max(phaseStartTime, LastExpectedWindowCloseTime());
                break;
        }

        SEED.Debug.Log($"[Fight] {PhaseLabel(next)}（{phaseBars}小節"
                     + $"{(next == Phase.Rest ? (lastAnswerPerfect ? "・完璧" : "・通常") : "")}）"
                     + $" / 糸の残り {Line01:P0}"
                     + $" / 魚HP {FishHp01:P0}");
    }

    /// <summary>
    /// 出題フェーズへ入るときの準備【フレーズ確定の唯一の入口】。
    ///
    /// 1. フレーズを引き直す（出題の小節数ぶん）
    /// 2. 出題で鳴らす打点の時刻を<b>解析的に</b>全て確定させる（<see cref="BuildCallHits"/>）
    /// 3. 次に来る回答フェーズの期待打点を先読み生成する
    ///    （回答フェーズ開始を待って生成すると、出題→回答の境界をまたぐ早打ちが
    ///      「出題中のお手つき」として弾かれてしまうため）
    /// 4. アイコンを全て未出現へ戻し、余分クリックの数を 0 に戻す
    /// 5. ドラムループを鳴らし直してループ先頭をフェーズ頭へ揃える
    ///
    /// <b>順序の注意</b>: 3 の <see cref="BuildExpectedHits"/> は前サイクルの取りこぼしを
    /// Miss として締める（＝前サイクルのアイコンを判定色にする）ので、
    /// アイコンの作り直し（4）は必ずその<b>後</b>に行うこと。逆にすると新しいフレーズの
    /// アイコンが前サイクルの判定色で光ってしまう。
    /// </summary>
    private void EnterCallPhase()
    {
        currentPhrase = BuildPhrase(phaseBars);
        BuildCallHits();

        int nextAnswerBars = PhaseBarsOf(Phase.Answer);
        BuildExpectedHits(phaseEndTime, nextAnswerBars);

        ResetIcons(callHitSubs.Count);
        extraClickCount = 0;
        iconFadeStartTime = NoFadeStart;

        RestartDrumAtCallHead();
    }

    /// <summary>
    /// 出題フェーズで鳴らす打点の時刻・分割番号を作る【出題打点の唯一の生成点】。
    /// 時刻は「フェーズ開始時刻 ＋ 分割番号 × 1 分割の秒数」で、以後この値だけを見て音を鳴らす。
    /// </summary>
    private void BuildCallHits()
    {
        ClearCallHits();

        for (int s = 0; s < currentPhrase.Length; s++)
        {
            if (currentPhrase[s] != PatternHitChar) { continue; }

            callHitSubs.Add(s);
            callHitTimes.Add(phaseStartTime + s * secondsPerSub);
        }
    }

    /// <summary>出題打点の一覧を空にして、再生位置も未再生へ戻す。</summary>
    private void ClearCallHits()
    {
        callHitTimes.Clear();
        callHitSubs.Clear();
        lastFiredCallHit = NoHitFired;
    }

    /// <summary>
    /// 回答フェーズへ入るときのアイコン更新。
    /// 出題で出したアイコンを消さずに残したまま、未判定色へ落として角度を回答フェーズ基準で引き直す。
    /// </summary>
    private void BeginAnswerIcons()
    {
        for (int i = 0; i < iconColors.Count; i++)
        {
            iconColors[i] = beatIconPendingColor;
        }
        LayoutIconAngles();
    }

    /// <summary>
    /// 直前の回答が<b>完璧</b>だったか【隙の長さを決める唯一の判定点】。
    /// 完璧 ＝ 期待打点が 1 つ以上あり、その<b>すべてが判定済みかつ Excellent</b>で、
    /// かつ余分なクリック（どの打点にも結び付かないクリック）が 0 回。
    ///
    /// 隙へ入る瞬間に評価するため、受付窓が隙側へはみ出したまま未判定で残っている打点は
    /// 「完璧ではない」と扱う（そのまま叩けば Excellent になり得るが、隙の長さは
    /// 隙の開始時刻に確定させる必要があるため。既定の 2 小節フレーズでは
    /// 最後の打点の窓は小節頭より手前で閉じるので、通常この分岐には入らない）。
    /// </summary>
    private bool EvaluateAnswerPerfect()
    {
        if (extraClickCount > 0) { return false; }
        if (expectedTimes.Count == 0) { return false; }

        for (int i = 0; i < expectedTimes.Count; i++)
        {
            if (!expectedJudged[i]) { return false; }
            if (expectedResults[i] != FishingController.HookJudgement.Excellent) { return false; }
        }
        return true;
    }

    /// <summary>
    /// 期待打点の受付窓がすべて閉じ切る時刻（秒）。期待打点が無ければ現在時刻。
    /// アイコンのフェード開始を「窓が閉じてから」にするために使う。
    /// </summary>
    private float LastExpectedWindowCloseTime()
    {
        float last = clockTime;
        for (int i = 0; i < expectedTimes.Count; i++)
        {
            float close = expectedTimes[i] + SEED.Mathf.Max(niceSeconds, 0f);
            if (close > last) { last = close; }
        }
        return last;
    }

    /// <summary>
    /// 出題フェーズの頭でドラムループを鳴らし直す【フェーズとループの位相合わせの唯一の出口】。
    ///
    /// 隙の長さが 1 小節／2 小節と変わるため、放置するとループ先頭と出題の頭がズレていく。
    /// フェーズ頭で鳴らし直せば「1 フェーズ ＝ ループ 1 周」（出題 2 小節・素材 2 小節の既定構成）
    /// が常に保たれる。一時停止中・開始待ちの最中は触らない（そちらの整列処理に任せる）。
    /// </summary>
    private void RestartDrumAtCallHead()
    {
        if (!drumRestartAtCall || !drumScheduled) { return; }
        if (Paused || drumPausedByFight) { return; }
        if (clockTime + DivideEpsilon < drumStartTime) { return; }   // まだ最初の開始時刻に達していない

        drumStartTime = phaseStartTime;
        StartDrumLoop();
    }

    /// <summary>いまの小節が始まった時刻（秒）。フェーズの開始時刻を小節頭へ揃えるのに使う。</summary>
    private float CurrentBarStartTime()
    {
        float barSeconds = SecondsPerBar;
        if (barSeconds <= DivideEpsilon) { return clockTime; }
        return SEED.Mathf.Floor(clockTime / barSeconds) * barSeconds;
    }

    /// <summary>
    /// フェーズの長さ（小節）【フェーズ長の唯一の算出点】。
    ///
    /// ・出題／回答 … 魚データの指定（1 以上）があればそれを、無ければバトル側の既定値（既定 2 小節）
    /// ・隙        … <b>直前の回答の出来</b>で決まる（完璧なら <see cref="restBarsPerfect"/>、
    ///                そうでなければ <see cref="restBarsNormal"/>）。回答の出来で毎回変わる値なので、
    ///                魚データの <c>RhythmRestBars</c> は<b>参照しない</b>。
    /// </summary>
    /// <param name="phase">対象のフェーズ。</param>
    private int PhaseBarsOf(Phase phase)
    {
        if (phase == Phase.Rest)
        {
            return SEED.Mathf.Max(lastAnswerPerfect ? restBarsPerfect : restBarsNormal, MinPhaseBars);
        }

        int fromFish = target is { } fish
            ? phase switch
            {
                Phase.Call => fish.RhythmCallBars,
                Phase.Answer => fish.RhythmAnswerBars,
                _ => UseFightDefaultBars,
            }
            : UseFightDefaultBars;

        int fallback = phase == Phase.Call ? callBars : answerBars;

        return SEED.Mathf.Max(fromFish > UseFightDefaultBars ? fromFish : fallback, MinPhaseBars);
    }

    /// <summary>フェーズの表示名。</summary>
    /// <param name="phase">対象のフェーズ。</param>
    private static string PhaseLabel(Phase phase) => phase switch
    {
        Phase.LeadIn => "余白",
        Phase.Call => "出題",
        Phase.Answer => "回答",
        Phase.Rest => "隙",
        _ => "―",
    };

    // ─── 内部処理: 出題 ───────────────────────────────────

    /// <summary>
    /// 出題フェーズの更新【打点キューを出す唯一の出口】。
    ///
    /// 打点の時刻（<see cref="callHitTimes"/>）は出題フェーズに入った時点で解析的に確定して
    /// いるので、ここでは「まだ鳴らしていない打点のうち、時刻に達したもの」を先頭から順に
    /// 鳴らすだけでよい。1 フレームが長くて複数の打点を跨いだ場合も、そのフレームで
    /// すべて 1 回ずつ鳴る（取りこぼしも二重再生も起きない）。
    /// 音を鳴らした打点はその瞬間からアイコンが出現する（＝音より先にアイコンは出ない）。
    ///
    /// 出題中のクリックは、次に来る回答フェーズの最初の打点より <see cref="niceSeconds"/>
    /// 以内の早打ちであれば回答フェーズと同じ判定（Excellent/Great/Nice）にし、
    /// どの期待打点の受付窓にも入らないものだけ Miss（お手つき）として扱う。
    /// </summary>
    private void UpdateCall()
    {
        for (int i = lastFiredCallHit + 1; i < callHitTimes.Count; i++)
        {
            if (clockTime < callHitTimes[i]) { break; }      // 時刻順なので、ここから先はまだ先の打点

            FishingController.Current?.PlayNibbleCue();
            ShowIconAtHit(i, callHitTimes[i], beatIconCallColor);
            lastFiredCallHit = i;
        }

        if (!ReadTapDown()) { return; }

        // 次の回答フェーズぶんの期待打点は出題フェーズ開始時点で既に用意済み（EnterPhase 参照）
        // なので、境界をまたいだ早打ちもここでそのまま回答と同じ判定にできる。
        int index = FindNearestPendingHit();
        if (index >= 0)
        {
            PlayAnswerClickSe();
            JudgeHit(index, clockTime - expectedTimes[index]);
        }
        else
        {
            CountExtraClick();
            ApplyMiss();
        }
    }

    // ─── 内部処理: 回答 ───────────────────────────────────

    /// <summary>
    /// 回答フェーズで期待する打点の一覧を作る【期待打点の唯一の生成点】。
    /// 出題と同じフレーズ（<see cref="currentPhrase"/>）を回答フェーズの先頭から並べ直す。
    /// 回答が出題より長い場合はフレーズを循環させ、短い場合は入り切る分だけを使う。
    ///
    /// 打点の時刻は出題と同じ式（開始時刻 ＋ 分割番号 × 1 分割の秒数）で<b>解析的に</b>求めるので、
    /// 出題と回答の小節数が同じなら「フェーズ頭からの相対時刻」が完全に一致する
    /// ＝ 打点アイコンが出題時とまったく同じ角度に並ぶ。
    ///
    /// 出題→回答の境界をまたぐ早打ちを判定できるように、回答フェーズへ入る前
    /// （出題フェーズ開始時点）で呼ばれる。そのため <c>answerStartTime</c>・<c>answerBars</c> は
    /// 「これから始まる回答フェーズ」の値を明示的に受け取り、そのときの
    /// <see cref="phaseStartTime"/> 等（出題側の値）には依存しない。
    /// </summary>
    /// <param name="answerStartTime">回答フェーズの開始時刻（＝出題フェーズの終了時刻）。</param>
    /// <param name="answerBars">回答フェーズの小節数。</param>
    private void BuildExpectedHits(float answerStartTime, int answerBars)
    {
        // 安全策: 前サイクルの期待打点がまだ残っていた場合、ここで一覧を作り直す前に
        // 必ず Miss として締めておく（通常は隙のあいだに CloseExpiredHits で締め切られる
        // ため到達しないが、取りこぼしたまま黙って消してしまわないための保険）。
        FailRemainingHits();
        ClearExpectedHits();
        if (currentPhrase.Length <= 0 || subsPerBar <= 0) { return; }

        int answerSubs = SEED.Mathf.Max(answerBars, MinPhaseBars) * subsPerBar;
        for (int phaseSub = 0; phaseSub < answerSubs; phaseSub++)
        {
            if (currentPhrase[phaseSub % currentPhrase.Length] != PatternHitChar) { continue; }

            expectedTimes.Add(answerStartTime + phaseSub * secondsPerSub);
            expectedJudged.Add(false);
            expectedResults.Add(FishingController.HookJudgement.None);
        }
    }

    /// <summary>期待打点の一覧を空にする。</summary>
    private void ClearExpectedHits()
    {
        expectedTimes.Clear();
        expectedJudged.Clear();
        expectedResults.Clear();
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
            // クリックの手応えは判定結果に関わらず毎回鳴らす（空打ち・多重クリックも含む）。
            PlayAnswerClickSe();

            int index = FindNearestPendingHit();
            if (index >= 0)
            {
                JudgeHit(index, clockTime - expectedTimes[index]);
            }
            else
            {
                // 余分なクリックにはアイコンを出さない（打点の並びを汚さないため）。
                // 回数だけ数えて、隙の長さ（完璧判定）に反映する。
                CountExtraClick();
                ApplyMiss();
            }
        }

        // 受付窓を過ぎた打点は打ち逃し（境界を越えて隙に入っても続けて呼ばれる）
        CloseExpiredHits();
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

        // 糸の残り: Excellent は減らず、それ以外は「ズレの大きさ × 効き ÷ 糸パワー補正」だけ減る
        if (judgement != FishingController.HookJudgement.Excellent)
        {
            SubtractLine(offset * linePerSecondOfOffset * LevelScale() / LinePowerFactor());
        }

        FishingController.Current?.ShowFightJudgement(judgement, signedOffset);
    }

    /// <summary>
    /// 期待打点へ判定結果を書き込み、対応する打点アイコンを判定色で跳ねさせる。
    /// </summary>
    /// <param name="index">期待打点の添字（＝打点の通し番号なのでアイコンの添字と一致する）。</param>
    /// <param name="judgement">判定結果。</param>
    private void MarkHitResult(int index, FishingController.HookJudgement judgement)
    {
        expectedJudged[index] = true;
        expectedResults[index] = judgement;

        // 判定した瞬間からポップをやり直す（すでに出ているアイコンが小さく跳ねる）
        ShowIconAtHit(index, clockTime, JudgementIconColor(judgement));
    }

    /// <summary>判定結果に対応する打点アイコンの色（RGB）。</summary>
    /// <param name="judgement">判定結果。</param>
    private SEED.Vector3 JudgementIconColor(FishingController.HookJudgement judgement) => judgement switch
    {
        FishingController.HookJudgement.Excellent => beatIconExcellentColor,
        FishingController.HookJudgement.Great => beatIconGreatColor,
        FishingController.HookJudgement.Nice => beatIconNiceColor,
        _ => beatIconMissColor,
    };

    /// <summary>
    /// どの打点にも結び付かないクリック（空打ち・お手つき）を 1 回数える
    /// 【余分クリック計上の唯一の入口】。隙の長さ（完璧判定）にだけ影響する。
    /// </summary>
    private void CountExtraClick() => extraClickCount++;

    /// <summary>
    /// 受付窓（打点 ＋ <see cref="niceSeconds"/>）を過ぎてもまだ未判定の打点を、
    /// その場で Miss（打ち逃し）として締める【期限切れ判定の唯一の適用点】。
    ///
    /// 出題・回答・隙のいずれのフェーズでも呼んでよい（時刻の絶対値で判定するため
    /// 現在フェーズに依存しない）。回答フェーズの最後の打点は窓が隙側へはみ出すことが
    /// あるため、隙フェーズでも引き続き呼び続けて締め切る。
    /// </summary>
    private void CloseExpiredHits()
    {
        for (int i = 0; i < expectedTimes.Count; i++)
        {
            if (expectedJudged[i]) { continue; }
            if (clockTime <= expectedTimes[i] + niceSeconds) { continue; }

            MarkHitResult(i, FishingController.HookJudgement.Miss);
            ApplyMiss();
        }
    }

    /// <summary>
    /// 期待打点の一覧を作り直す（新しいサイクルの分を積む）直前に、
    /// まだ残っている未判定の打点をすべて無条件で Miss として締める【保険専用】。
    ///
    /// 通常は隙のあいだに <see cref="CloseExpiredHits"/> が受付窓を過ぎたものから
    /// 順に締め切るため、ここに未判定のまま残ることは想定していない。それでも
    /// 一覧をまるごと差し替える前に、取りこぼしを黙って消してしまわないための保険として置く。
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
    /// 糸の残りを減らし、判定画像（Miss）を出す。
    /// </summary>
    private void ApplyMiss()
    {
        SubtractLine(SEED.Mathf.Max(missLoss, 0f) / LinePowerFactor());
        FishingController.Current?.ShowFightJudgement(FishingController.HookJudgement.Miss, 0f);
    }

    /// <summary>左クリックの押下をこのフレームに読んだか（叩き入力の唯一の入口）。</summary>
    private static bool ReadTapDown() => SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left);

    // ─── 内部処理: 隙（巻き取り）─────────────────────────────

    /// <summary>
    /// 隙フェーズの更新。糸の残りは回復しない（本仕様では回復手段が無い）。巻き取りで魚 HP を削れる唯一の区間。
    ///
    /// 直前の回答フェーズ最後の打点は受付窓が隙側へはみ出すことがあるので、
    /// その窓の内側のクリックだけは回答フェーズと同じ判定へ結び付ける
    /// （窓の外の隙中クリックは、通常の隙の入力として何もせず無視する）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <param name="reelAmount">このフレームの巻き取り量（メートル）。</param>
    private void UpdateRest(float deltaTime, float reelAmount)
    {
        if (ReadTapDown())
        {
            int index = FindNearestPendingHit();
            if (index >= 0)
            {
                // 回答フェーズからはみ出した受付窓の内側 → 回答フェーズと同じ判定にする
                PlayAnswerClickSe();
                JudgeHit(index, clockTime - expectedTimes[index]);
            }
            // 受付窓の外のクリックは、隙中の通常入力として何もしない（Miss にもしない）
        }

        // 受付窓を過ぎてもまだ残っている打点は、ここで打ち逃しとして締め切る
        CloseExpiredHits();

        // 巻き取り: 巻いた距離ぶんだけ魚 HP を削る
        if (reelAmount <= ReelInputEpsilon) { return; }
        fishHp = SEED.Mathf.Max(fishHp - reelAmount * ReelHpPerUnit, FishHpZero);
    }

    // ─── 内部処理: 糸の残りと疲労 ─────────────────────────

    /// <summary>
    /// 糸の残りを減らす【減少の唯一の適用点】。回復手段は無い（一方向）。
    /// 下限（0）に達したら糸切れ（<see cref="LineBroken"/>）を立てて効果音を鳴らす。
    /// </summary>
    /// <param name="amount">減分（負なら何もしない）。</param>
    private void SubtractLine(float amount)
    {
        if (amount <= 0f) { return; }

        Line01 = SEED.Mathf.Max(Line01 - amount, Line01Min);
        if (Line01 > Line01Min) { return; }
        if (LineBroken) { return; }             // 既に通知済みなら二重に鳴らさない

        LineBroken = true;
        PlayLineBreakSe();
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
    /// ヒット直後（余白＝LeadIn）に沖へ引かれる実効距離（メートル）
    /// 【引き距離の唯一の算出点】＝ <see cref="Fish.HookRunDistance"/>（0 以下なら
    /// <see cref="hookRunDistanceDefault"/> にフォールバック）× <see cref="RunDistanceRankMultiplier"/>。
    /// ランクの閾値そのものは持たない（<see cref="Fish.SizeRank"/> に一元化してある）。
    /// </summary>
    /// <param name="fish">掛かった魚。</param>
    private float HookRunDistanceFor(Fish fish)
    {
        float baseDistance = fish.HookRunDistance > 0f ? fish.HookRunDistance : hookRunDistanceDefault;
        return SEED.Mathf.Max(baseDistance, 0f) * RunDistanceRankMultiplier(fish.SizeRank);
    }

    /// <summary>
    /// <see cref="Fish.SizeRank"/>（"S" / "A" / "B" / それ以外＝"C"）に対応する引き距離の倍率。
    /// </summary>
    /// <param name="rank">魚のサイズランク。</param>
    private float RunDistanceRankMultiplier(string rank) => rank switch
    {
        "S" => runDistanceRankS,
        "A" => runDistanceRankA,
        "B" => runDistanceRankB,
        _ => runDistanceRankC,
    };

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

    /// <summary>
    /// 糸パワーによる糸の減り軽減の倍率。
    /// ＝ 1 + <see cref="linePowerLossReduction"/> × (糸パワー − 1)。
    /// 糸の減り量はこの値で割られる（大きいほど減りにくくなる）。
    /// </summary>
    private float LinePowerFactor()
        => SEED.Mathf.Max(NeutralMultiplier + linePowerLossReduction * (linePower - NeutralMultiplier), DivideEpsilon);

    /// <summary>合わせ判定に対応する初期の糸の残り（判定が無ければ最も不利な Nice 値）。</summary>
    /// <param name="judge">合わせ判定。</param>
    private float InitialLine(FishingController.HookJudgement judge) => judge switch
    {
        FishingController.HookJudgement.Excellent => initialLineExcellent,
        FishingController.HookJudgement.Great => initialLineGreat,
        _ => initialLineNice,
    };

    /// <summary>糸切れの効果音を鳴らす（パス未設定なら何もしない）。</summary>
    private void PlayLineBreakSe()
    {
        if (string.IsNullOrEmpty(lineBreakSePath)) { return; }
        SEED.Audio.Play(lineBreakSePath, lineBreakSeVolume);
    }

    /// <summary>回答フェーズの左クリック 1 回ごとに鳴らす効果音を鳴らす（パス未設定なら何もしない）。</summary>
    private void PlayAnswerClickSe()
    {
        if (string.IsNullOrEmpty(answerClickSePath)) { return; }
        SEED.Audio.Play(answerClickSePath, answerClickSeVolume);
    }

    /// <summary>実行時の状態をすべて初期値へ戻す（開始前・終了後の共通処理）。</summary>
    private void ResetRuntimeState()
    {
        // ドラムループ（BGM 枠）はバトル中だけ鳴らすので、終了・糸切れ・釣り上げ・
        // 開始直前のいずれからここへ来ても必ず止める【停止の唯一の出口】。
        StopDrumLoop();
        drumScheduled = false;
        drumPausedByFight = false;
        drumStartTime = 0f;
        drumUnitSeconds = 0f;
        drumResyncSeconds = 0f;

        Active = false;
        Paused = false;                // 一時停止の持ち越しを防ぐ
        LineBroken = false;
        CurrentPhase = Phase.None;
        Line01 = Line01Max;
        fishHp = 0f;
        fishHpMax = 0f;
        metersPerHp = 0f;
        leadInStartDistance = 0f;
        target = null;

        clockTime = 0f;
        lastBeatPlayed = NoBeatPlayed;
        phaseStartTime = 0f;
        phaseEndTime = 0f;
        phaseBars = MinPhaseBars;
        nextPhaseAnnounced = false;
        currentPhrase = "";
        patterns.Clear();
        ClearCallHits();
        ClearExpectedHits();

        extraClickCount = 0;
        lastAnswerPerfect = false;
        ResetIcons(0);
        iconFadeStartTime = NoFadeStart;
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
        ApplyBeatIcons();
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
    /// セグメントの色を決めて書き込む【糸ゲージの描画の唯一の出口】。
    ///
    /// セグメントは<b>糸の残りの円弧専用</b>（真上から右回りに <see cref="Line01"/> × 360 度ぶんだけ
    /// 点灯。減るほど円弧が短くなる＝空きセグメントが増える）。
    /// 打点はセグメントではなく打点アイコン（<see cref="ApplyBeatIcons"/>）が担当するので、
    /// ここでは一切扱わない。
    /// 値が変わっていないセグメントには書き込まない（毎フレーム 48 回の書き込みを避ける）。
    /// </summary>
    private void ApplySegmentColors()
    {
        int count = segmentSprites.Count;
        if (count <= 0) { return; }

        float alpha = SEED.Mathf.Clamped01(segmentOpacity);
        float lit = SEED.Mathf.Clamped01(Line01) * count;
        SEED.Color litColor = LineDepletionColor(alpha);

        for (int i = 0; i < count; i++)
        {
            // 糸の残りの円弧（i 番目が円弧の内側なら点灯色、外なら空き色）
            SEED.Color color = i < lit ? litColor : ToColor(emptyColor, alpha);

            if (i < cachedSegmentColors.Count && SameColor(cachedSegmentColors[i], color)) { continue; }

            var sprite = segmentSprites[i];
            if (sprite.IsValid) { sprite.Color = color; }
            if (i < cachedSegmentColors.Count) { cachedSegmentColors[i] = color; }
        }
    }

    /// <summary>
    /// 糸の残り（<see cref="Line01"/>）に応じた点灯セグメントの色。
    /// 満タン(1.0)＝<see cref="fullColor"/> → 中間(0.5)＝<see cref="midColor"/> →
    /// 危険(0.0)＝<see cref="dangerColor"/> の 2 区間線形補間。
    /// </summary>
    /// <param name="alpha">不透明度。</param>
    private SEED.Color LineDepletionColor(float alpha)
    {
        float line = SEED.Mathf.Clamped01(Line01);
        const float Mid = 0.5f;

        if (line >= Mid)
        {
            float u = (line - Mid) / (Line01Max - Mid);
            return SEED.Color.Lerp(ToColor(midColor, alpha), ToColor(fullColor, alpha), u);
        }
        else
        {
            float u = line / Mid;
            return SEED.Color.Lerp(ToColor(dangerColor, alpha), ToColor(midColor, alpha), u);
        }
    }

    // ─── UI: 打点アイコン ──────────────────────────────────

    /// <summary>
    /// 打点アイコンの状態を、打点の個数ぶんだけ「未出現」で作り直す
    /// 【アイコン状態の唯一の初期化点】。
    /// </summary>
    /// <param name="hitCount">いまのフレーズの打点数。</param>
    private void ResetIcons(int hitCount)
    {
        iconPopStartTimes.Clear();
        iconColors.Clear();
        iconDegrees.Clear();

        for (int i = 0; i < hitCount; i++)
        {
            iconPopStartTimes.Add(IconHidden);
            iconColors.Add(beatIconCallColor);
            iconDegrees.Add(0f);
        }
        LayoutIconAngles();
    }

    /// <summary>
    /// 打点アイコンの角度を<b>いまのフェーズ基準</b>で計算し直す
    /// 【アイコンの角度算出の唯一の点】。
    ///
    /// θ ＝ (打点時刻 − フェーズ開始時刻) ÷ フェーズ長 × 360 度（真上が 0・右回り）。
    /// 打点の時刻から直接求めるので、セグメントの分割数（48）には量子化されない。
    /// 出題中は出題の打点時刻、それ以外（回答・隙）は期待打点の時刻を使う。
    /// </summary>
    private void LayoutIconAngles()
    {
        float duration = phaseEndTime - phaseStartTime;
        if (duration <= DivideEpsilon) { return; }

        bool useCall = CurrentPhase == Phase.Call;
        var times = useCall ? callHitTimes : expectedTimes;

        for (int i = 0; i < iconDegrees.Count && i < times.Count; i++)
        {
            iconDegrees[i] = (times[i] - phaseStartTime) / duration * FullCircleDegrees;
        }
    }

    /// <summary>
    /// 打点アイコン <paramref name="index"/> を出現（または再出現）させる
    /// 【アイコンのポップ開始の唯一の入口】。
    /// </summary>
    /// <param name="index">打点の通し番号（アイコンの添字と同じ）。</param>
    /// <param name="startTime">ポップを始める時刻（秒・絶対時刻）。出題は打点の時刻、判定時は判定の時刻。</param>
    /// <param name="rgb">アイコンの色（RGB）。</param>
    private void ShowIconAtHit(int index, float startTime, SEED.Vector3 rgb)
    {
        if (index < 0 || index >= iconPopStartTimes.Count) { return; }

        iconPopStartTimes[index] = startTime;
        iconColors[index] = rgb;
    }

    /// <summary>
    /// 打点アイコンを描く【打点表示の唯一の出口】。
    ///
    /// ・出現していないアイコン（<see cref="IconHidden"/>）とフレーズの打点数を超えた予備は
    ///   アルファ 0 で完全に隠す
    /// ・出現済みは角度から位置を置き、easeOutBack で 0 → 1 に拡大する
    /// ・隙に入った後は <see cref="beatIconFadeSeconds"/> 秒でアルファを 0 まで落とす
    /// </summary>
    private void ApplyBeatIcons()
    {
        int slots = beatIconSprites.Count;
        if (slots <= 0) { return; }

        ApplyIconSizes(slots);

        // 回答・隙のあいだも角度はフェーズ基準のまま保つ（隙では回答時の角度を凍結して使う）
        if (CurrentPhase is Phase.Call or Phase.Answer) { LayoutIconAngles(); }

        float fade = IconFadeAlpha01();
        float baseAlpha = SEED.Mathf.Clamped01(beatIconOpacity) * fade;

        for (int i = 0; i < slots; i++)
        {
            bool used = Active && i < iconPopStartTimes.Count && iconPopStartTimes[i] < IconHidden;
            float scale = used ? IconPopScale01(iconPopStartTimes[i]) : 0f;
            float alpha = used ? baseAlpha : 0f;

            if (i < beatIconTransforms.Count)
            {
                var tf = beatIconTransforms[i];
                if (tf.IsValid)
                {
                    if (used) { tf.Position = ArcPointAt(iconDegrees[i], beatIconRadiusPx); }
                    tf.Scale = new SEED.Vector2(scale, scale);
                }
            }

            var sprite = beatIconSprites[i];
            if (!sprite.IsValid) { continue; }

            sprite.Color = ToColor(used ? iconColors[i] : beatIconPendingColor, alpha);
        }
    }

    /// <summary>
    /// 打点アイコンの大きさを 1 度だけ書き込む（毎フレーム同じ値を送らないための差分更新）。
    /// 拡大アニメは <see cref="SEED.CanvasTransform.Scale"/> 側で行うので、Size は不変でよい。
    /// </summary>
    /// <param name="slots">アイコンの枚数。</param>
    private void ApplyIconSizes(int slots)
    {
        if (iconSizeAppliedCount == slots) { return; }

        for (int i = 0; i < slots; i++)
        {
            var sprite = beatIconSprites[i];
            if (sprite.IsValid) { sprite.Size = new SEED.Vector2(beatIconSizePx, beatIconSizePx); }
        }
        iconSizeAppliedCount = slots;
    }

    /// <summary>
    /// 出現アニメの拡大率（0 →（少し 1 を超えて）→ 1）。
    /// <see cref="beatIconPopSeconds"/> が 0 以下なら即座に等倍。
    /// </summary>
    /// <param name="popStartTime">ポップを始めた時刻（秒・絶対時刻）。</param>
    private float IconPopScale01(float popStartTime)
    {
        float duration = SEED.Mathf.Max(beatIconPopSeconds, 0f);
        if (duration <= DivideEpsilon) { return NormalScale; }

        float t = SEED.Mathf.Clamped01((clockTime - popStartTime) / duration);
        return EaseOutBack(t);
    }

    /// <summary>
    /// easeOutBack（0→1 の終わりで少し行き過ぎてから戻る補間）。
    /// f(t) = 1 + c3·(t−1)³ + c1·(t−1)²（c1 ＝ <see cref="EaseBackOvershoot"/>、c3 ＝ c1 ＋ 1）。
    /// </summary>
    /// <param name="t">進行度（0〜1）。</param>
    private static float EaseOutBack(float t)
    {
        float u = t - NormalScale;
        return NormalScale + EaseBackCubic * u * u * u + EaseBackOvershoot * u * u;
    }

    /// <summary>
    /// 打点アイコンのフェード係数（1 ＝ 完全表示・0 ＝ 消滅）。
    /// 隙に入って受付窓が閉じたところから <see cref="beatIconFadeSeconds"/> 秒かけて 0 になる。
    /// </summary>
    private float IconFadeAlpha01()
    {
        if (iconFadeStartTime >= NoFadeStart) { return 1f; }

        float duration = SEED.Mathf.Max(beatIconFadeSeconds, 0f);
        if (duration <= DivideEpsilon) { return clockTime >= iconFadeStartTime ? 0f : 1f; }

        return 1f - SEED.Mathf.Clamped01((clockTime - iconFadeStartTime) / duration);
    }

    /// <summary>
    /// マーカー（<b>フェーズ内</b>の進行を示す針）を更新する。
    /// 角度 ＝ <see cref="PhaseProgress01"/> × 360 度＝ 1 フェーズで 1 周。
    /// 色はフェーズ（疲労中は専用色）で決まる。
    /// </summary>
    private void ApplyMarker()
    {
        float degrees = PhaseProgress01 * FullCircleDegrees;

        if (gaugeMarkerTransform is { } markerTf && markerTf.IsValid)
        {
            markerTf.Position = ArcPoint(degrees);
            markerTf.Rotation = degrees;
        }

        if (gaugeMarker is not { } marker || !marker.IsValid) { return; }

        SEED.Vector3 rgb = CurrentPhase switch
        {
            Phase.Call => markerCallColor,
            Phase.Answer => markerAnswerColor,
            _ => markerRestColor,
        };
        marker.Color = ToColor(rgb, SEED.Mathf.Clamped01(gaugeMarkerOpacity));
    }

    /// <summary>
    /// 円の中心テキスト（フェーズ名＋予告／魚 HP ％）を更新する。
    /// 余白（<see cref="Phase.LeadIn"/>）中だけは特別扱いで、残り拍数のカウントダウン
    /// （"4" → "3" → "2" → "1"）だけを大きく出す。
    /// </summary>
    private void ApplyStatusText()
    {
        if (hpText is not { } label || !label.IsValid) { return; }

        if (CurrentPhase == Phase.LeadIn)
        {
            label.Content = $"{LeadInRemainingBeats()}";
            label.Color = label.Color.WithAlpha(SEED.Mathf.Clamped01(hpTextOpacity));
            return;
        }

        string phaseName = PhaseLabel(CurrentPhase);
        string notice = nextPhaseAnnounced ? $" → {PhaseLabel(NextPhase(CurrentPhase))}" : string.Empty;

        label.Content = $"{phaseName}{notice}\n"
                      + $"魚 {SEED.Mathf.RoundToInt(FishHp01 * PercentScale)}%";
        label.Color = label.Color.WithAlpha(SEED.Mathf.Clamped01(hpTextOpacity));
    }

    /// <summary>
    /// 余白（<see cref="Phase.LeadIn"/>）で残っている拍数（表示用。1 以上）。
    /// 余白中でなければ 0。
    ///
    /// clockTime は余白のあいだ「0 に達する（＝最初の Call の小節頭になる）」まで負の値を
    /// 取るので、<see cref="BeatIndex"/>（<c>floor(clockTime / secondsPerBeat)</c>）も
    /// 同じ符号で負になる。余白開始時に <c>-leadInBeats</c>、最後の 1 拍で <c>-1</c> になり
    /// 0 で Call へ切り替わるので、符号を反転するだけで「4 3 2 1」のカウントダウンになる。
    /// </summary>
    private int LeadInRemainingBeats()
        => CurrentPhase == Phase.LeadIn ? SEED.Mathf.Max(-BeatIndex, 0) : 0;

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
    private SEED.Vector2 ArcPoint(float degrees) => ArcPointAt(degrees, arcRadiusPx);

    /// <summary>
    /// 半径を指定して円周上の点（キャンバス座標）を返す
    /// 【円周配置の唯一の計算点】。
    /// 位置 ＝ 中心 ＋ (sin θ, −cos θ) × 半径（角度 0 が真上・＋ が右回り・キャンバスの Y は下向き）。
    /// </summary>
    /// <param name="degrees">頂点からの角度（度。＋ が右回り）。</param>
    /// <param name="radiusPx">円の半径（ピクセル）。</param>
    private static SEED.Vector2 ArcPointAt(float degrees, float radiusPx)
    {
        float rad = degrees * SEED.Mathf.Deg2Rad;
        return new SEED.Vector2(SEED.Mathf.Sin(rad) * radiusPx, -SEED.Mathf.Cos(rad) * radiusPx);
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

    /// <summary>次の描画でセグメントと打点アイコンを必ず組み直させる。</summary>
    private void InvalidateSegmentCache()
    {
        cachedSegmentCount = 0;
        cachedSegmentColors.Clear();
        iconSizeAppliedCount = 0;
    }

    /// <summary>UI をすべて隠す【非表示の唯一の出口】。</summary>
    private void HideUi()
    {
        for (int i = 0; i < segmentSprites.Count; i++)
        {
            ApplySpriteOpacity(segmentSprites[i], 0f);
        }
        for (int i = 0; i < beatIconSprites.Count; i++)
        {
            ApplySpriteOpacity(beatIconSprites[i], 0f);
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
