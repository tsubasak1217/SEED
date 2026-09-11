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
/// バトル中は BGM 枠でドラムループを鳴らし、再生速度を <c>いまのテンポ ÷ 素材のBPM</c> に
/// 変えて拍を一致させる（<see cref="ApplyDrumSpeed"/>）。<b>隙（Rest）のあいだだけ
/// <see cref="restBpm"/> に同期</b>し、戦闘（余白・出題・回答）へ戻ると魚の BPM に戻る。
/// 開始は余白の頭（余白が拍子の倍数でないときだけ最初の出題の頭まで待つ）。
/// フェーズ長もテンポも可変なので、位相合わせは <see cref="drumRestartAtCall"/>（既定 true）の
/// <b>出題フェーズの頭ごとにループを鳴らし直す</b>方式だけに一本化してある。
/// <see cref="Paused"/>・チュートリアルの説明中は <see cref="SEED.Audio.PauseBgm"/> で
/// <b>再生位置ごと凍結</b>し、再開時に <see cref="SEED.Audio.ResumeBgm"/> で続きから鳴らす。
/// 拍時計も同じ区間で止まっているので、位相は自動的に一致する
/// （停止→頭出しで鳴らし直す方式は、次のループ境界まで最大 1 周ぶん無音になるため廃止）。
/// メトロノームと併用する前提だが、メトロノームの音量を 0 にすればドラムだけにもできる。
///
/// ■ フェーズ（LeadIn と Run だけ<b>拍単位</b>・それ以外は<b>小節単位</b>、切り替えは必ず小節頭）
/// <code>
/// 余白(LeadIn) → 出題(Call) → 回答(Answer) → 隙(Rest) → 出題 → …
///                                            └→ 隙の間に「魚回復」の漂流物を拾っていたら
///                                               走り(Run) を 1 度だけ挟んでから出題へ戻る
/// 既定の長さ: 余白 leadInBeats 拍 / 出題 callBars 小節(既定 2) / 回答 answerBars 小節(既定 2)
///             隙 = 直前の回答の出来で決まる（下表）/ 走り recoverRunBeats 拍
/// 出題・回答の小節数は<b>ビートパターン 1 行の小節数</b>で決まる（＝出題と回答は必ず同じ長さ）。
/// パターンが 1 行も読めなかったときだけ、魚データ（Fish.RhythmCallBars /
/// RhythmAnswerBars）→ バトル側の既定値の順でフォールバックする。
/// 隙の小節数は「回答の出来」で毎回決まる（魚データ側の指定は廃止した）。
/// </code>
/// <b>隙の長さの規則（2026-09-06 改定）</b>
/// <code>
/// 直前の回答が完璧（期待打点が全て Excellent ＆ 余分なクリック 0）→ restBarsPerfect 小節（既定 2）
/// それ以外                                                        → restBarsNormal  小節（既定 1）
/// </code>
/// ＝ うまく叩けたご褒美として巻ける時間が伸びる。
/// <b>隙だけは専用のテンポ</b>（<see cref="restBpm"/>・既定 100）で数えるので、
/// 「1 小節」の実時間は魚の BPM ではなくこの値で決まる。ドラムループも同じ値へ同期する。
/// <see cref="Phase.LeadIn"/> はバトル開始直後にだけ 1 度通る特別な区間で、
/// メトロノームだけが leadInBeats 拍ぶん鳴り、出題・回答・巻きは一切行わない
/// （中央テキストには残り拍数「4 3 2 1」を出す）。時計の原点（clockTime == 0）を
/// 「余白が終わって最初の Call の小節頭になる瞬間」に置き、余白中は clockTime を
/// 負の値として扱うことで、余白の長さが拍子の倍数でなくても Call 側の小節頭と
/// ズレずに接続できる（詳細は <see cref="EnterLeadInOrCall"/>）。
/// 次のフェーズは<b>1 拍前</b>に中央テキストで予告する（LeadIn を除く）。
///
/// ■ ビートパターン（出題・回答で共有する打点の並び）【2026-09-09 改定】
/// 出題データは<b>レベルデザイン用のテキストファイル</b>
/// （<see cref="beatPatternPath"/>・既定 assets://mainGame/rhythm/beat_patterns.txt）から読む。
/// 1 行 ＝ 戦闘サイクル 1 周ぶん（＝<see cref="callBars"/> 小節）で、記法は
/// <c>{1/4}t,,t,,  {1/6},,,{1/4}t,,  [1-3,5]</c>（詳細は docs/beat_patterns.md）。
/// 行末の <c>[...]</c> は対象の魚レベル帯で、魚の <see cref="Fish.Level"/> に該当する行から
/// 抽選する（該当が無ければ全レベル行 → それも無ければフォールバックを合成して警告）。
/// 内部表現は<b>固定グリッドではなく「小節内の位置（0.0〜小節数）」</b>なので、
/// 1/4・1/6・1/8 が混ざった行もそのまま扱える。判定・アイコン配置・ドラムの頭出しは
/// すべて「フェーズ開始時刻 ＋ 位置 × 1 小節の秒数」で求めた<b>時刻</b>だけを見る。
/// 回答フェーズには同じ位置列を先頭から並べ直す（回答が長ければ繰り返す）ので、
/// 出題と回答で打点位置が完全に一致する。
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
/// ■ 糸の残り（1〜0。<b>糸HPは固定値で、強化は無い</b>）
/// <code>
/// 開始値       : 合わせランクで決まる（Excellent 1.0 / Great 0.9 / Nice 0.8）
/// 判定ごと     : 糸の残り -= |Δt| × linePerSecondOfOffset
///                Excellent は減らない
/// Miss・空打ち : 糸の残り -= missLoss
/// 回復手段     : 漂流物「糸の回復」を巻き込んだときだけ（<see cref="RecoverLine"/>）
/// <b>2026-09-10 改定</b>: 判定ズレ・Miss による糸の減りは<b>全レベル・全魚種で共通</b>
///                （以前あった「戦闘力差のレベル補正」は廃止。難度差は魚の引き（距離）側で付ける）
/// 糸の残り ≦ 0 → 糸が切れる（<see cref="LineBroken"/>）
/// </code>
/// <b>2026-09-06 改定</b>: 「糸パワー」による減り軽減（linePower / linePowerLossReduction）は廃止した。
/// 糸の強化で全体の難度を下げるのではなく、<b>固定の糸HPを漂流物の回復でやりくりする</b>設計にしたため
/// （強化要素は漂流物の出現頻度など別の軸へ寄せる）。
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
///
/// <b>2026-09-09 改定 ― 釣り上げ成立は「岸まで寄せ切ったか」だけ</b>
/// 釣り上げの成否を決めるのは <see cref="FishingController"/> 側の
/// 「ウキ→竿先の実測距離 ≦ catchDistanceMeters」<b>のみ</b>で、魚 HP は見ない。
/// 魚 HP 0（<see cref="FishDefeated"/>）はもはや成立条件ではなく、
/// 「魚が抵抗をやめ、見た目距離の下限（<see cref="visibleDistanceMin"/>）を無視して
/// 竿先まで一気に寄る」という<b>寄せ方の切り替え</b>だけを意味する。
///
/// <b>■ 見た目の距離と目標距離の分離【2026-09-09 改定 / 2026-09-11 再改定】</b>
/// 上の「目標距離」をそのままウキの位置にすると、格上の魚（魚力 ≫ 竿）では
/// 巻き効率が小さいぶん目標距離がほとんど縮まず、<b>巻いてもウキが動かない</b>。
/// そこで<b>見た目の距離だけ</b>を目標距離から切り離す（HP の減り＝難度は不変）。
/// <code>
/// 巻いている間      : 巻き量を消化したフレームは reelVisibleSpeed（m/秒）で手元へ寄る。
///                     引き返しは reelPullbackScale 倍に弱まる（既定 0 ＝ 引き返さない）。
///                     「巻いている間」＝最後の巻き入力から reelHoldSeconds 秒以内
///                     （ReelingRecently）。ホイールのこま切れ入力で途切れさせない
/// 手を止めている間  : 魚が fishPullSpeed × 戦闘力比（上限つき）で沖へ引き返す。
///                     ただし目標距離は追い越さない（＝残り HP が「引き返せる余力」になる）
/// 目標より沖に居る  : 差 × visibleDistanceReturnRate で手元へ戻る（糸のテンション）
/// 下限              : visibleDistanceMin（HP が残るうちは竿先へめり込まない）
/// </code>
/// <b>2026-09-11 改定の理由</b>: 「目標が沖側」の引き返し速度に
/// <c>差 × visibleDistanceReturnRate</c>（＝上限の無いバネ）が混ざっていたため、
/// 差が <c>reelVisibleSpeed ÷ visibleDistanceReturnRate</c>（既定 4m）を超えた時点で
/// 引き返しが巻きの寄せを上回り、<b>巻いても正味で寄らなくなっていた</b>
/// （寄る速さが「巻いた距離 × 巻き効率」＝格上ほど 0 に近い値へ落ちていた）。
/// 引き返しを「fishPullSpeed × 戦闘力比」の一定速度だけに戻し、
/// 巻き中は <see cref="reelPullbackScale"/> 倍に弱めることで
/// <b>巻いているあいだは必ず寄る</b>を保証する。難度は「巻いていない間の引き返し」と
/// 「HP の減り（＝引き返せる余力の減り）」で担保する。
/// 距離表示・魚モデルの位置はどちらも<b>ウキの実位置</b>を見ているので自動的に追従する
/// （表示は <see cref="UpdateDistanceDisplay"/>、魚は <c>Fish.UpdateBite</c>）。
///
/// ■ 合わせランクの影響
/// 合わせが悪いほど初期の糸の残りが少ない（＝危険側から始まる）。
/// ────────────────────────────────────────────────────────
///
/// <b>UI（円形のリズム時計）</b>
/// 画面中央の円（<b>プリミティブで描くセグメント＋マーカー</b>
/// ＋ <b>プールした打点アイコン</b>）を時計として使う。
/// セグメントとマーカーは<b>アクタを持たない</b>（2026-09-07 改定。旧 <c>GaugeSeg00</c>…
/// の固定 48 枚のスプライトと <c>GaugeMarker</c> を廃止）。<see cref="SEED.Draw"/> で
/// <see cref="gaugeSpace"/>（キャンバス中央のアクタ）のローカル空間へ毎フレーム描くので、
/// 分割数はインスペクタ（<see cref="gaugeSegmentCount"/>）だけで変えられる。
/// - マーカー（針）… <b>フェーズの進行</b>（<see cref="PhaseProgress01"/> × 360 度・
///   真上がフェーズ頭・右回り）。出題も回答も 2 小節なので、<b>1 フェーズで針が 1 周</b>する。
///   隙（1〜2 小節）・余白でも同じく、その区間の長さで 1 周する。三角形で描く。
/// - セグメント … 真上から右回りに糸の残りの円弧（<see cref="Line01"/> × 360 度・
///   減った分だけ空き色に置き換わる）。円弧の色は満タン(緑)→中間(黄)→危険(赤)へ補間。
///   <b>セグメントは糸ゲージ専用</b>で、打点は一切描かない（旧「拍マーク」は廃止）。
/// - 打点アイコン … フレーズの打点 1 つにつき 1 枚。
///   <b>シーンにアクタを並べず</b>、<see cref="beatIconActorPath"/> の <c>.actor</c> を
///   <see cref="beatIconParent"/> の子として <see cref="initialBeatIconPool"/> 枚だけ
///   作り置きし、使い回す（2026-09-07 改定。旧 <c>BeatIcon00</c>…<c>BeatIcon15</c> の
///   固定 16 枚を廃止）。打点数が足りなければプールを増やし、要らない枚はアルファ 0 で隠す
///   （<b>破棄はしない</b>＝生成・破棄の往復コストを避けるため）。
///   角度は打点の<b>時刻</b>から解析的に求める（θ ＝ (打点時刻 − フェーズ開始) ÷ フェーズ長 × 360 度）
///   ので、セグメントの分割数には一切依存しない。半径は <see cref="beatIconRadiusPx"/>。
///   ライフサイクルは下記。
/// <code>
/// 出題: 打点の音が鳴った<b>後</b>に 1 枚ずつ出現（easeOutBack で 0→1 に拡大・出題色）
/// 回答: 同じ位置のまま暗い未判定色へ。判定した瞬間に判定色（Excellent 白 / Great 淡緑 /
///       Nice 黄 / Miss 赤）で小さく跳ねる。余分なクリックにはアイコンを出さない
/// 隙  : 受付窓が閉じたところから beatIconFadeSeconds 秒でフェードアウト
/// </code>
/// - 中央テキスト … 余白中は開始カウントダウン（常時）／それ以外はフェーズ名（＋予告）と
///   魚 HP ％。後者は<b>デバッグ表示</b>で、パッケージ版では出さない（<see cref="showDebugHud"/>）
/// - 右下テキスト … 残り距離（従来どおり・ゲーム UI なので常時表示）
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

    /// <summary>拍子（1 小節の拍数）の下限。データが壊れていても時計が止まらないようにする。</summary>
    private const int MinBeatsPerBar = 1;

    /// <summary>BPM の下限（0 や負の BPM で時計が破綻しないようにする番人値）。</summary>
    private const float MinBpm = 1f;

    /// <summary>フェーズの長さ（小節数）の下限。</summary>
    private const int MinPhaseBars = 1;

    /// <summary>
    /// フォールバックのビートパターンに付ける行番号（ファイル由来でないことを表す）。
    /// </summary>
    private const int FallbackPatternLineNumber = 0;

    /// <summary>フォールバックのビートパターンの出所を表すラベル（ログ用）。</summary>
    private const string FallbackPatternLabel = "(フォールバック: 各拍の頭)";

    /// <summary>
    /// チュートリアルで「ビートバトルなし」を指定されたときの隙の小節数。
    /// 事実上終わらない長さにして、魚をずっとひるませたまま巻かせる。
    /// </summary>
    private const int TutorialEndlessRestBars = 9999;

    /// <summary>ドラムループ素材の BPM の下限（0 除算・速度破綻の番人値）。</summary>
    private const float MinDrumLoopBpm = 1f;

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

    /// <summary>まだ 1 度も拍を鳴らしていないことを表す番兵値。</summary>
    private const int NoBeatPlayed = -1;

    /// <summary>該当なしを表す添字（<see cref="FindNearestPendingHit"/> の戻り値）。</summary>
    private const int NoIndex = -1;

    /// <summary>「巻いている」とみなす巻き取り量のしきい値（メートル）。</summary>
    private const float ReelInputEpsilon = 0.0001f;

    /// <summary>漂流物「ひるませ」で延長できる隙の小節数の下限（0 小節の延長は無意味なので 1）。</summary>
    private const int MinExtraRestBars = 1;

    /// <summary>
    /// 走り（<see cref="Phase.Run"/>）の開始距離がまだ確定していないことを表す番兵。
    /// 距離はフェーズへ入った時点では分からない（コントローラが毎フレーム渡してくる）ため、
    /// 走りの 1 フレーム目に確定させる（<see cref="ComputeFloatDistanceStep"/>）。
    /// </summary>
    private const float RunStartDistanceUnset = -1f;

    /// <summary>走りを挟まない設定値（<see cref="recoverRunBeats"/> がこれ以下なら走らない）。</summary>
    private const int NoRecoverRunBeats = 0;

    /// <summary>まだ 1 度も巻き入力が無いことを表す時刻の番兵（十分に古い時刻）。</summary>
    private const float NoReelInputTime = float.MinValue;

    /// <summary>戦闘力の基準比（魚 ÷ 竿 がこの値なら「等価」）。</summary>
    private const float EquivalentPowerRatio = 1f;

    /// <summary>倍率の基準値（1 ＝ 効果なし）。</summary>
    private const float NeutralMultiplier = 1f;

    /// <summary>中心から端までの割合（大きさの半分を求めるための係数）。</summary>
    private const float HalfScale = 0.5f;

    /// <summary>マーカー（針）を糸ゲージより手前に出すためのレイヤー差。</summary>
    private const int MarkerLayerOffset = 1;

    /// <summary>マーカーの多角形の頂点数の既定値（3 ＝ 三角形の針）。</summary>
    private const int MarkerVertexCount = 3;

    /// <summary>多角形として成立する頂点数の下限（3 ＝ 三角形）。</summary>
    private const int MinPolygonVertexCount = 3;

    /// <summary>判定リング（打点アイコンの外周）を糸ゲージより手前へ出すためのレイヤー差。</summary>
    private const int JudgeRingLayerOffset = 3;

    /// <summary>リング・縁取りの太さの下限（px）。0 を渡して図形が消えるのを防ぐ番人値。</summary>
    private const float MinStrokeThicknessPx = 0.1f;

    /// <summary>判定表示を収める矩形の一辺の下限（px）。0 除算と潰れた矩形を避ける番人値。</summary>
    private const float MinFitExtentPx = 1f;

    /// <summary>半分の長さから全体の長さへ戻す係数（<see cref="HalfScale"/> の逆数）。</summary>
    private const float FullFromHalfScale = 2f;

    /// <summary>円の分割数の下限（0 除算とゼロ個描画を避ける）。</summary>
    private const int MinSegmentCount = 1;

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
    /// 完璧 ＝ 期待打点がすべて Excellent（<see cref="EvaluateAnswerPerfect"/>）。
    /// 受付窓の外のクリックは無反応なので、空打ちの回数は完璧判定に影響しない。
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

    /// <summary>
    /// 1 小節の拍数（拍子）【拍子の唯一の置き場所】。
    ///
    /// 2026-09-09 改定で<b>魚データ側の「拍子」は廃止</b>し、バトル側の設定に一本化した。
    /// ビートパターンは「小節を 1.0 とした位置」で書くので拍子に依存しないが、
    /// メトロノームの強拍・ドラムの小節線・余白の拍数はこの値で決まる。
    /// </summary>
    [SerializeField(Label = "拍子(1小節の拍数)")]
    private int beatsPerBar = 4;

    /// <summary>
    /// 隙（<see cref="Phase.Rest"/>）を数えるときの BPM【隙専用のテンポ】。
    ///
    /// 巻きに専念する区間だけテンポを落として（上げて）間を作るための設定。
    /// 隙の小節数は <see cref="restBarsPerfect"/> / <see cref="restBarsNormal"/> のままで、
    /// その 1 小節の長さだけがこの BPM で決まる。ドラムループの再生速度も
    /// 隙のあいだはこの BPM に同期し、戦闘（余白・出題・回答）へ戻ると魚の BPM に戻る。
    /// </summary>
    [SerializeField(Label = "隙のBPM")]
    private float restBpm = 100f;

    // ─── ビートパターン（レベルデザイン用テキスト）─────────────

    /// <summary>
    /// ビートパターンを書いたテキストファイルのアセットパス
    /// 【出題データの唯一の供給源】。記法は docs/beat_patterns.md を参照。
    /// 1 行 ＝ 戦闘サイクル 1 周ぶん（＝<see cref="callBars"/> 小節）。
    /// </summary>
    [Header("ビートパターン"), SerializeField(Label = "パターンのファイル")]
    private string beatPatternPath = "assets://mainGame/rhythm/beat_patterns.txt";

    /// <summary>
    /// パターンを<b>サイクルごと</b>に引き直すか。
    ///
    /// true（既定）… 出題フェーズのたびに抽選する（1 戦のあいだに譜面が変わる）
    /// false        … 1 戦につき 1 度だけ抽選し、同じ譜面を繰り返す
    /// </summary>
    [SerializeField(Label = "サイクルごとに抽選する")]
    private bool drawPatternEachCycle = true;

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

    /// <summary>ドラムループの音量（1.0 = 等倍）。</summary>
    [SerializeField(Label = "ドラムループの音量")]
    private float drumLoopVolume = 0.8f;

    /// <summary>
    /// 出題（Call）フェーズの頭ごとにドラムループを鳴らし直すか【唯一の同期手段】。
    ///
    /// 隙の長さ（1〜2 小節）だけでなくテンポ（<see cref="restBpm"/>）まで変わるため、
    /// 放っておくとループ先頭と出題の頭は必ずズレる。出題の頭で鳴らし直せば
    /// <b>1 サイクル＝ループ 1 周</b>（出題 2 小節・素材 2 小節の既定構成）が常に保たれる。
    /// false にすると鳴らし直さないので、素材の周期がサイクルと合っていないと位相がずれていく。
    /// </summary>
    [SerializeField(Label = "出題の頭でループを鳴らし直す")]
    private bool drumRestartAtCall = true;

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

    /// <summary>
    /// 時間差 1 秒あたりに減る糸の残り（全レベル・全魚種で共通。補正は掛からない）。
    /// 【2026-09-11 調整】判定ミスの痛みを強めるため 0.3 → 0.6 へ倍増。
    /// Perfect の回復量はこの値から算出する（<see cref="perfectRecoverGreatCount"/> の式）ので、
    /// ここを変えると回復量も同じ倍率で自動的に追随する。
    /// </summary>
    // ─── 【デバッグ】クリック処理の切り分け ─────────────────────
    //  「ビートバトルでクリックすると重い」原因を切り分けるためのスイッチ。
    //  クリック 1 回の処理は「効果音 → 判定（打点アイコン・糸の減り）→ 判定 UI（画像・ヒント文）」の
    //  3 段なので、それぞれを個別に止められるようにする。パッケージ版では全て無効
    //  （SEED.Application.IsDebugAllowed が false）。

    /// <summary>クリック時の効果音を鳴らさない（音以外はそのまま）。</summary>
    [Header("【デバッグ】クリック処理の切り分け"), SerializeField(Label = "効果音を鳴らさない")]
    private bool debugMuteClickSe = false;

    /// <summary>クリックの判定を行わない（効果音だけ鳴る。打点は打ち逃し扱いになる）。</summary>
    [SerializeField(Label = "判定を行わない(音のみ)")]
    private bool debugSkipJudgement = false;

    /// <summary>判定結果の画像・ヒント文（<see cref="FishingController.ShowFightJudgement"/>）を出さない。</summary>
    [SerializeField(Label = "判定UIを出さない")]
    private bool debugHideJudgementUi = false;

    /// <summary>打点アイコンの色替え・跳ね（<see cref="ShowIconAtHit"/>）をしない。</summary>
    [SerializeField(Label = "打点アイコンの反応を出さない")]
    private bool debugNoBeatIconFeedback = false;

    /// <summary>判定による糸の減りを行わない。</summary>
    [SerializeField(Label = "糸を減らさない")]
    private bool debugNoLineLoss = false;

    /// <summary>デバッグ切り分けが有効か（エディタ実行のみ。パッケージ版では常に false）。</summary>
    private static bool DebugIsolationAllowed => SEED.Application.IsDebugAllowed;

    [Header("糸の残り"), SerializeField(Label = "時間差1秒あたりの糸の減り")]
    private float linePerSecondOfOffset = 0.45f;

    /// <summary>
    /// Miss（打ち逃し・空打ち）1 回で減る糸の残り（全レベル・全魚種で共通）。
    /// 【2026-09-11 調整】上の時間差の減りと足並みを揃えて 0.06 → 0.12 へ倍増。
    /// </summary>
    [SerializeField(Label = "Missの糸の減り")]
    private float missLoss = 0.09f;

    /// <summary>
    /// 回答フレーズを Perfect（全打点 Excellent）で締めたときに回復する糸の残りを、
    /// 「Great 判定 1 個ぶんの減り（<see cref="greatSeconds"/> × <see cref="linePerSecondOfOffset"/>）」
    /// の何個分にするか。0 で回復しない。回復は隙（Rest）へ入る瞬間に 1 回だけ行う。
    /// </summary>
    [SerializeField(Label = "Perfectの糸回復(Great何個分)")]
    private float perfectRecoverGreatCount = 1f;

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

    /// <summary>
    /// わらしべで乗り換えたときに<b>回復する</b>糸の残り（最大 1.0 に対する割合）。
    /// 乗り換え前の残りにこの値を足した値（上限 1.0）で新しいやり取りを始める
    /// （合わせランクによる初期値は使わない）。完全回復にしないのは、連鎖を重ねるほど
    /// 糸が消耗していく緊張感を残すため。
    /// </summary>
    [SerializeField(Label = "乗り換え時の糸回復(最大比)")]
    private float chainSwapLineRecover = 1f / 3f;

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
    /// 目標距離が現在より<b>遠い</b>とき、魚がウキを沖へ引く速度の基準値（m/秒）
    /// 【引き返し速度の唯一の基準値・2026-09-11 改定】。
    ///
    /// 実際の速度は「魚の総合力 ÷ 竿パワー」（<see cref="PullRateClamped"/>・
    /// <see cref="pullRateMultiplierMin"/>〜<see cref="pullRateMultiplierMax"/> でクランプ）を
    /// 掛けた値で、上限は <c>この値 × pullRateMultiplierMax</c>（既定 3.0 m/秒）。
    /// 巻いているあいだはさらに <see cref="reelPullbackScale"/> 倍に弱める。
    ///
    /// <b>2026-09-11 改定</b>: 以前はここに「目標距離とのズレ × <see cref="visibleDistanceReturnRate"/>」
    /// という<b>上限の無いバネ</b>が max で混ざっており、ズレが広がるほど引き返しが強くなって
    /// 巻きの寄せ（<see cref="reelVisibleSpeed"/>）を打ち消していた。引き返しは
    /// この一定速度だけに戻し、バネは「目標より沖に居るとき」専用にした。
    /// </summary>
    [Header("ウキの距離制御"), SerializeField(Label = "魚の引き速度(m/秒)")]
    private float fishPullSpeed = 1.5f;

    /// <summary>
    /// 目標距離が現在より<b>近い</b>とき、ウキが手元へ寄る速度の上限（m/秒）
    /// 【巻きの速さの唯一の上限】。
    ///
    /// 巻き入力（ホイール）は 1 フレームにまとまって飛び込むので、これを上限として
    /// <see cref="Tick"/> が 1 フレームの巻き取り量そのものを頭打ちにする
    /// （<see cref="pendingReelAmount"/> に繰り越すので入力は捨てない）。
    /// こうすると「魚 HP の減り」「ウキの実移動」「距離表示」が同じ速度で揃う。
    /// </summary>
    /// 既定 24 m/秒: 上限導入前（HP がホイール量そのままで減っていた頃）の
    /// 体感に合わせた値。6 m/秒では同じ距離を巻くのに約 4 倍の操作が要った。
    [SerializeField(Label = "寄せ速度の上限(m/秒)")]
    private float reelInSpeedMax = 24f;

    /// <summary>
    /// 巻き取り量の繰り越し上限（秒）。頭打ちで余った巻き量は
    /// <see cref="pendingReelAmount"/> へ貯めて次フレーム以降に消化するが、
    /// 貯めすぎると入力を止めてもしばらく巻け続けてしまうので
    /// 「<see cref="reelInSpeedMax"/> × この秒数」で頭を押さえる。
    /// </summary>
    /// 既定 1.0 秒: ホイールを勢いよく回した瞬間の入力を取りこぼさない程度に貯める。
    [SerializeField(Label = "巻き取りの繰り越し上限(秒)")]
    private float reelCarryOverMaxSeconds = 1.0f;

    /// <summary>
    /// 「いま巻いている」とみなし続ける保持時間（秒）【漂流物の巻き込み判定の唯一の猶予】。
    ///
    /// 巻き入力は連打・こま切れになりやすいので、最後に巻いた時刻からこの秒数のあいだは
    /// 巻き continuing とみなす（<see cref="ReelingRecently"/>）。
    /// 0 にすると「そのフレームに巻いていること」が必須になる。
    /// </summary>
    [SerializeField(Label = "巻き入力の保持時間(秒)")]
    private float reelHoldSeconds = 0.35f;

    /// <summary>引きの速度倍率（魚 ÷ 竿）の下限。</summary>
    [SerializeField(Label = "引きの速度倍率の下限")]
    private float pullRateMultiplierMin = 0.25f;

    /// <summary>引きの速度倍率（魚 ÷ 竿）の上限。</summary>
    [SerializeField(Label = "引きの速度倍率の上限")]
    private float pullRateMultiplierMax = 2f;

    // ─── 見た目距離の分離（手応えの調整）【2026-09-09 追加 / 2026-09-11 改定】─────────
    //
    // 「巻いているのにウキが動かない」を無くすため、<b>見た目の距離</b>を
    // 魚 HP から決まる目標距離（DesiredFloatDistance）から切り離す。
    // 難度（ReelHpPerUnit ＝ 巻き 1m あたりに削れる HP）は一切変えていない。

    /// <summary>
    /// 巻き入力中にウキが手元へ寄る速度（m/秒）【「巻けば必ず寄る」の唯一の速度】。
    ///
    /// 魚がどれだけ強くても、隙（<see cref="Phase.Rest"/>）で巻いているあいだは
    /// 必ずこの速さぶんが手元向きに加算される。
    ///
    /// <b>設計上の不変条件</b>: この値は「巻き中に残る引き返し速度の最大値」
    /// （<see cref="fishPullSpeed"/> × <see cref="pullRateMultiplierMax"/>
    /// × <see cref="reelPullbackScale"/>）より大きくすること。
    /// そうでないと格上の魚に対して巻いても正味で寄らなくなり、この修正の意味が消える。
    /// <see cref="reelPullbackScale"/> が既定の 0 なら、この条件は自動的に満たされる。
    ///
    /// なお実際にウキが寄る速さは「この値 × 巻き入力が続いているフレームの割合」になる。
    /// ホイール入力は <see cref="ThrottleReelAmount"/> が
    /// <see cref="reelInSpeedMax"/> の速さへ均してから消化するので、
    /// ホイールをゆっくり回すと巻き入力のあるフレームが飛び飛びになり、そのぶん寄りも遅くなる
    /// （＝「速く回すほど速く寄る」は保たれる）。
    /// </summary>
    /// 既定 6.0 m/秒: 引き戻しの最大（1.5 × 2 ＝ 3.0 m/秒）の 2 倍。
    [SerializeField(Label = "巻きの見た目寄せ速度(m/秒)")]
    private float reelVisibleSpeed = 6f;

    /// <summary>
    /// 巻いているあいだに残す「魚の引き返し」の倍率（0〜1）
    /// 【「巻けば必ず寄る」を壊さないための唯一のつまみ・2026-09-11 追加】。
    ///
    /// 隙（<see cref="Phase.Rest"/>）で<b>巻き入力が続いている間</b>
    /// （<see cref="ReelingRecently"/> ＝ 最後の巻き入力から <see cref="reelHoldSeconds"/> 秒以内）は、
    /// 魚の引き返し速度（<see cref="fishPullSpeed"/> × 戦闘力比）をこの倍率で弱める。
    /// <code>
    /// 0   … 巻いているあいだは一切引き返さない（＝ reelVisibleSpeed がそのまま寄る速さ）
    /// 0.5 … 半分だけ抵抗する（正味の寄せ ＝ reelVisibleSpeed − 引き返し × 0.5）
    /// 1   … 従来どおり巻き中も全力で引き返す
    /// </code>
    /// 1 に近づけるほど格上の魚で寄りが鈍くなるので、
    /// <see cref="reelVisibleSpeed"/> の不変条件（上記）を必ず確認すること。
    /// </summary>
    /// 既定 0: 「巻いている間は HP がどれだけ高くても必ず寄る」を既定の手触りにする。
    [SerializeField(Label = "巻き中の引き返し倍率(0=引き返さない)")]
    private float reelPullbackScale = 0f;

    /// <summary>
    /// ウキが<b>目標距離より沖に居る</b>とき、糸のテンションで手元へ戻す強さ（1/秒）
    /// 【目標より沖に出たぶんを詰め戻す唯一の係数】。
    ///
    /// 戻す速度は <c>(見た目距離 − 目標距離) × この値</c>（上限は <see cref="reelInSpeedMax"/>、
    /// 目標距離は追い越さない）。巻き効率の高い格下の魚では、巻くほど目標距離が
    /// 速く縮んでウキが目標より沖に取り残されるので、この項が効いて<b>一気に寄る</b>
    /// （＝格下ほど速く寄る、という差はここで付く）。
    ///
    /// <b>2026-09-11 改定</b>: 以前は逆向き（目標のほうが沖側＝魚が引き返す局面）にも
    /// 同じバネを効かせていたが、上限が無いため差が
    /// <c>reelVisibleSpeed ÷ この値</c>（既定 4m）を超えると引き返しが巻きに打ち勝ち、
    /// 「巻いても寄らない」の直接の原因になっていた。逆向きには使わない。
    /// </summary>
    /// 既定 1.5 /秒: 4m 沖に取り残されていたら 6 m/秒で戻る強さ。
    [SerializeField(Label = "沖に出たぶんの復帰レート(1/秒)")]
    private float visibleDistanceReturnRate = 1.5f;

    /// <summary>
    /// 巻きで詰められる見た目距離の下限（メートル）【ウキが竿先へめり込むのを防ぐ番人値】。
    ///
    /// どれだけ巻いてもウキはこれより手前へは寄らない（＝竿先を突き抜けない）。
    ///
    /// <b>2026-09-09 改定</b>: 釣り上げの成立条件が「岸（竿先）まで寄せ切ったか」だけになり、
    /// 魚 HP は成立に関与しなくなった。そのためこの値が釣り上げ成立距離
    /// （<c>FishingController.catchDistanceMeters</c> ＝ 既定 4.0m）より<b>外側</b>にあると、
    /// HP が残っているあいだは永久に成立できなくなる。
    /// 役割は「めり込み防止」だけに絞り、必ず成立距離より内側の値にすること。
    /// </summary>
    /// 既定 0.5m: 釣り上げ成立距離（4.0m）の内側。巻き切れば必ず成立距離へ到達でき、
    /// それでもウキが竿先（距離 0）へ重なることはない。
    [SerializeField(Label = "見た目距離の下限(m)")]
    private float visibleDistanceMin = 0.5f;

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

    // ─── 回復後の走り（隙中に「魚回復」を拾ったときだけ挟まる Run）─────────

    /// <summary>
    /// 走り（<see cref="Phase.Run"/>）の長さ（拍）【走りを入れるかどうかの唯一のスイッチ】。
    ///
    /// 隙（<see cref="Phase.Rest"/>）の最中に「魚回復」の漂流物を巻き込むと
    /// （<see cref="RecoverFishHp"/>）、回復ぶんは<b>その場では効かせずに貯めておき</b>、
    /// <b>隙が終わったタイミング</b>でまとめて魚 HP へ入れて、この拍数ぶんの走りで
    /// 「新しい魚 HP に対応する距離」まで沖へ持って行く。
    /// 数え方は余白（<see cref="leadInBeats"/>）と同じで、魚の BPM（<see cref="secondsPerBeat"/>）で数える。
    ///
    /// <b>0 以下にすると走りを一切挟まない</b>。このとき回復は貯めずに<b>その場で即時</b>効く
    /// （＝2026-09-10 以前の挙動に戻る）。
    ///
    /// 走る距離そのものに倍率は掛けない ―― 距離は「回復した魚 HP × 1HP あたりの距離」で
    /// 決まる（＝肉を 2 個拾えば 2 個ぶん走る）ので、倍率を挟むと HP と距離の対応が壊れる。
    /// 走る距離を変えたいときは漂流物側の効果量（<c>DriftItem.EffectAmount</c>）を調整すること。
    /// </summary>
    [SerializeField(Label = "回復後の走り(拍)")]
    private int recoverRunBeats = 4;

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
    /// 糸ゲージ（円）とマーカーを描く座標空間【ゲージの基準の唯一の置き場】。
    /// キャンバス中央アンカーのアクタ（<c>HpText</c>）を割り当てる想定で、
    /// この空間のローカル原点＋<see cref="gaugeCenterOffsetXPx"/>/<see cref="gaugeCenterOffsetYPx"/>
    /// がゲージの中心になる。
    /// 未設定ならゲージを描かない（誤った場所へ描かないため）。
    /// </summary>
    [Header("UI 参照"), SerializeField(Label = "ゲージの座標空間(CanvasTransform)")]
    private SEED.CanvasTransform? gaugeSpace = null;

    /// <summary>ゲージ中心を <see cref="gaugeSpace"/> の原点からずらす量（X・px）。</summary>
    [SerializeField(Label = "ゲージ中心のずらしX(px)")]
    private float gaugeCenterOffsetXPx = 0f;

    /// <summary>ゲージ中心を <see cref="gaugeSpace"/> の原点からずらす量（Y・px・下が正）。</summary>
    [SerializeField(Label = "ゲージ中心のずらしY(px)")]
    private float gaugeCenterOffsetYPx = 0f;

    /// <summary>
    /// 打点アイコンのプレハブ（<c>.actor</c>）の仮想パス。
    /// Sprite（テクスチャ・大きさ・レイヤー）と CanvasTransform（アンカー中央・ピボット中央）を
    /// 持つ 2D アクタであること。空文字ならアイコンを一切生成しない。
    /// </summary>
    [SerializeField(Label = "打点アイコンのアクタ")]
    private string beatIconActorPath = "assets://mainGame/actors/UI/BeatIcon.actor";

    /// <summary>
    /// 生成した打点アイコンに付ける名前の接頭辞（<see cref="SpawnOnce"/> の照合キー）。
    /// プール添字を 2 桁で足して <c>BeatIcon00</c> のような一意名にする。
    /// </summary>
    private const string BeatIconActorNamePrefix = "BeatIcon";

    /// <summary>
    /// 打点アイコンを生成する親アクタ（釣り UI キャンバス＝<c>FishingUI</c>）。
    /// 2D アクタの親は Canvas を持つアクタ（または 2D アクタ）である必要がある。
    /// 未設定なら <see cref="beatIconParentName"/> の名前で実行時に探す。
    /// </summary>
    [SerializeField(Label = "打点アイコンの親アクタ")]
    private SEED.GameObject? beatIconParent = null;

    /// <summary>
    /// <see cref="beatIconParent"/> が未設定のときに名前で探すためのアクタ名（保険）。
    /// 空文字なら探さない（＝親が決まらないのでアイコンを生成しない）。
    /// </summary>
    [SerializeField(Label = "打点アイコンの親アクタ名(代替)")]
    private string beatIconParentName = "FishingUI";

    /// <summary>
    /// 開始時に作り置きする打点アイコンの枚数。
    /// フレーズの打点数がこれを超えたときは <see cref="ResetIcons"/> がプールを継ぎ足す
    /// （継ぎ足した分は生成が翌フレーム反映のため 1 フレームだけ出ない）。
    /// </summary>
    [SerializeField(Label = "打点アイコンの初期プール数")]
    private int initialBeatIconPool = 16;

    /// <summary>
    /// 円の中心に出す状態テキスト。
    /// 余白（<see cref="Phase.LeadIn"/>）中は開始カウントダウン（常時表示）、
    /// それ以外はフェーズ名（＋予告）と魚 HP ％（<see cref="showDebugHud"/> のデバッグ表示）。
    /// </summary>
    [SerializeField(Label = "状態のText")]
    private SEED.Text? hpText = null;

    /// <summary>画面右下に出す残り距離のテキスト（"9.6m" 形式）。</summary>
    [SerializeField(Label = "残り距離のText")]
    private SEED.Text? distanceText = null;

    /// <summary>
    /// 状態テキスト（<see cref="hpText"/>）へ「フェーズ名 → 次フェーズの予告」と
    /// 「魚 ○○%」を出すかどうか【デバッグ HUD の唯一のつまみ・2026-09-11 改定】。
    ///
    /// どちらも本来はプレイヤーへ見せない内部状態（魚の残り HP ／ 拍時計がいまどの区間か）で、
    /// 開発中の調整のために出している。パッケージ版（配布ビルド）では
    /// <see cref="SEED.Application.IsDebugAllowed"/> が false になるため、
    /// このフラグが true でも表示されない（<see cref="ShowDebugHud"/>）。
    /// エディタからの Play では従来どおり見える。
    ///
    /// <b>これで消えるのは上の 2 つだけ</b>で、次のゲーム UI は常に出る:
    /// 余白（<see cref="Phase.LeadIn"/>）の開始カウントダウン／
    /// 右下の残り距離（<see cref="UpdateDistanceDisplay"/>）／糸ゲージ・マーカー・打点アイコン／
    /// 判定表示と Perfect などのバナー（<c>FightEvalBanner</c>・<c>HitBanner</c>）。
    ///
    /// <b>2026-09-11 改定</b>: 旧 <c>showFishHpDebug</c>（魚 HP ％だけのつまみ）を置き換えた。
    /// フェーズ名・予告も内部状態の可視化なので、まとめて 1 つのつまみで扱う。
    /// </summary>
    [SerializeField(Label = "デバッグ表示（パッケージ版では常に非表示）", Tooltip = "状態テキストにフェーズ名・次フェーズ予告・魚の残り HP ％を出す（パッケージ版では無視される）")]
    private bool showDebugHud = true;

    // ─── UI レイアウト ────────────────────────────────────

    /// <summary>円の半径（ピクセル）。マーカーとセグメントの配置半径。</summary>
    [Header("UI レイアウト"), SerializeField(Label = "円の半径(px)")]
    private float arcRadiusPx = 140f;

    /// <summary>
    /// 糸ゲージの円を何個のセグメントに割るか（＝目盛りの粒度）。
    /// 旧構成のスプライト 48 枚と同じ見た目になるよう既定は 48。
    /// </summary>
    [SerializeField(Label = "円セグメントの個数")]
    private int gaugeSegmentCount = 48;

    /// <summary>
    /// 糸ゲージ（セグメント）の描画レイヤー。打点アイコン（BeatIcon.actor のレイヤー）より
    /// 奥に、レーダー・ホワイトアウトより手前になる値にすること。旧スプライトと同じ 15 が既定。
    /// </summary>
    [SerializeField(Label = "ゲージのレイヤー")]
    private int gaugeLayer = 15;

    /// <summary>
    /// マーカー（針）の大きさ（ピクセル・外接円の半径）。
    /// 旧マーカースプライトは 14×14 px（＝半径 7）だったが、
    /// 「いまどこを指しているか」を見失わないよう既定を一回り大きくしている。
    /// </summary>
    [SerializeField(Label = "マーカーの大きさ(px)")]
    private float markerSizePx = 12f;

    /// <summary>マーカーの多角形の頂点数（3 ＝ 三角形の針。下限 3）。</summary>
    [SerializeField(Label = "マーカーの頂点数")]
    private int markerVertexCount = MarkerVertexCount;

    /// <summary>
    /// マーカーの縁取りの太さ（px）。本体より一回り大きい多角形を
    /// <see cref="markerOutlineColor"/> で先に描いて縁にする。0 以下で縁取りなし。
    /// </summary>
    [SerializeField(Label = "マーカーの縁取り太さ(px)")]
    private float markerOutlineThicknessPx = 2.5f;

    /// <summary>マーカーの縁取りの色（RGB）。セグメントの上でも輪郭が潰れないための暗色。</summary>
    [SerializeField(Label = "マーカーの縁取り色(RGB)")]
    private SEED.Vector3 markerOutlineColor = new SEED.Vector3(0.05f, 0.06f, 0.09f);

    /// <summary>
    /// 拍頭でマーカーが一瞬大きくなる倍率（1 ＝ パルスなし）。
    /// <see cref="markerPulseSeconds"/> 秒かけて等倍へ戻る。
    /// </summary>
    [SerializeField(Label = "マーカーの拍パルス倍率")]
    private float markerPulseScale = 1.8f;

    /// <summary>拍頭のパルスが等倍へ戻るまでの秒数（0 以下でパルスなし）。</summary>
    [SerializeField(Label = "マーカーの拍パルス秒数")]
    private float markerPulseSeconds = 0.12f;

    /// <summary>拍頭の発光（マーカーの後ろに出す円）の半径倍率（マーカーの大きさ基準）。</summary>
    [SerializeField(Label = "マーカーの発光半径倍率")]
    private float markerGlowRadiusScale = 2.2f;

    /// <summary>拍頭の発光の色（RGB）。</summary>
    [SerializeField(Label = "マーカーの発光色(RGB)")]
    private SEED.Vector3 markerGlowColor = new SEED.Vector3(1f, 1f, 1f);

    /// <summary>拍頭の発光の最大不透明度（0 で発光なし。拍頭が最大で、パルスと同じ速さで消える）。</summary>
    [SerializeField(Label = "マーカーの発光の不透明度")]
    private float markerGlowOpacity = 0.45f;

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

    /// <summary>
    /// プレイヤーが<b>叩いて捉えた</b>打点アイコンの色（RGB・黄色系）
    /// 【「叩けた」ことを最優先で伝える色】。
    ///
    /// 判定（Excellent/Great/Nice）ごとの色分けはアイコン本体では行わず、
    /// 外周の判定リング（<see cref="judgeRingEnabled"/>）が担う。
    /// こうすると「叩けたか（黄色く跳ねたか）」と「どれだけ正確だったか（縁の色）」を
    /// 一目で切り分けられる。打ち逃し（Miss）だけは叩いていないので
    /// <see cref="beatIconMissColor"/> のままにする。
    /// </summary>
    [SerializeField(Label = "打点アイコンの色(叩いた)")]
    private SEED.Vector3 beatIconHitColor = new SEED.Vector3(1f, 0.85f, 0.2f);

    /// <summary>
    /// 叩いた瞬間に上乗せする拡大倍率（1 ＝ 上乗せなし）。
    /// 出現ポップ（<see cref="IconPopScale01"/>）へ<b>掛け算</b>で乗る。
    /// </summary>
    [SerializeField(Label = "打点アイコンの叩き拡大率")]
    private float beatIconHitPopScale = 1.45f;

    /// <summary>叩き拡大が等倍へ戻るまでの秒数（0 以下なら拡大しない）。</summary>
    [SerializeField(Label = "打点アイコンの叩き拡大秒数")]
    private float beatIconHitPopSeconds = 0.18f;

    /// <summary>
    /// 判定色のリングを打点アイコンの外周へ描くか
    /// （アイコン本体は叩いた色＝黄色のまま、判定は縁の色で示す）。
    /// </summary>
    [SerializeField(Label = "判定リングを描く")]
    private bool judgeRingEnabled = true;

    /// <summary>判定リングの太さ（px・半径方向）。</summary>
    [SerializeField(Label = "判定リングの太さ(px)")]
    private float judgeRingThicknessPx = 3f;

    /// <summary>判定リングと打点アイコン本体の隙間（px）。</summary>
    [SerializeField(Label = "判定リングの隙間(px)")]
    private float judgeRingGapPx = 3f;

    /// <summary>判定リングの不透明度（アイコンのフェードが別途掛かる）。</summary>
    [SerializeField(Label = "判定リングの不透明度")]
    private float judgeRingOpacity = 1f;

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

    // ─── 判定表示（Excellent/Great/Nice/Miss）の収まり ──────────────

    /// <summary>
    /// 判定画像を糸ゲージの矩形（円の外接矩形 − 余白）へ収めるか
    /// 【判定表示のはみ出し防止の唯一のスイッチ】。
    ///
    /// 判定画像（<c>judge_excellent.png</c> ほか）は横 348px あり、
    /// 糸ゲージの円（直径 ＝ <see cref="arcRadiusPx"/> × 2）から左右へはみ出して
    /// 打点アイコンの輪に重なってしまう。true なら、対象アクタのハンドルが揃った
    /// フレームに<b>1 度だけ</b>キャンバススケールを縮めて矩形の内側へ収める。
    ///
    /// <b>Size ではなく Scale を書く</b>のは、判定画像の Size を
    /// <see cref="FishingController"/> のポップ演出が毎フレーム書き換えているためである
    /// （同じ値を両者で奪い合うと、表示のたびに縮み続ける事故になる）。
    /// </summary>
    [Header("判定表示の収まり"), SerializeField(Label = "判定表示をゲージ内に収める")]
    private bool judgementFitEnabled = true;

    /// <summary>
    /// 判定表示を収める矩形の余白（px）。
    /// 矩形の一辺 ＝ <see cref="arcRadiusPx"/> × 2 − この値 × 2。
    /// </summary>
    [SerializeField(Label = "判定表示の余白(px)")]
    private float judgementPaddingPx = 12f;

    /// <summary>
    /// 判定表示のポップ倍率の見込み（<c>FishingController</c> 側の「判定のポップ倍率」と
    /// 同じ値にする）。ポップで一瞬大きくなった状態でもはみ出さないよう、
    /// この倍率を掛けた大きさで収まるところまで縮める。
    /// </summary>
    [SerializeField(Label = "判定表示のポップ見込み倍率")]
    private float judgementPopAllowance = 1.2f;

    /// <summary>収まり調整の対象となる判定画像アクタ（Excellent）。未設定なら調整しない。</summary>
    [SerializeField(Label = "判定表示のアクタ(Excellent)")]
    private SEED.GameObject? judgementExcellentObject = null;

    /// <summary>収まり調整の対象となる判定画像アクタ（Great）。未設定なら調整しない。</summary>
    [SerializeField(Label = "判定表示のアクタ(Great)")]
    private SEED.GameObject? judgementGreatObject = null;

    /// <summary>収まり調整の対象となる判定画像アクタ（Nice）。未設定なら調整しない。</summary>
    [SerializeField(Label = "判定表示のアクタ(Nice)")]
    private SEED.GameObject? judgementNiceObject = null;

    /// <summary>収まり調整の対象となる判定画像アクタ（Miss）。未設定なら調整しない。</summary>
    [SerializeField(Label = "判定表示のアクタ(Miss)")]
    private SEED.GameObject? judgementMissObject = null;

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

        /// <summary>
        /// 走り（隙の間に「魚回復」の漂流物を拾ったときだけ、その隙の直後に 1 度だけ挟まる区間）。
        ///
        /// 隙のあいだ貯めておいた回復（<see cref="pendingFishHpRecovery"/>）を入り口で
        /// まとめて魚 HP へ入れ、<see cref="recoverRunBeats"/> 拍かけて
        /// <b>新しい魚 HP に対応する距離</b>（<see cref="DesiredFloatDistance"/>）まで
        /// 魚が沖へ走ってウキを引き伸ばす（＝肉 2 個ぶんなら 2 個ぶん走る）。
        /// ＝ 拾った瞬間ではなく<b>この区間で</b>取り返される。
        /// 中身は <see cref="LeadIn"/> とまったく同じ扱いで、出題・回答・巻きは一切行わず、
        /// 糸も減らない。カメラも <c>FishingController.IsRunCameraPhase</c> によって
        /// ヒット直後（<see cref="LeadIn"/>）と同じ構図になる。
        /// 走り終えたら通常どおり出題（<see cref="Call"/>）へ戻る。
        /// </summary>
        Run,

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

    /// <summary>
    /// 現在の魚 HP の割合（0〜1）。UI 表示用。
    /// 漂流物「魚回復」で実値が最大値を超えることがあるので、表示は 1（100%）で頭打ちにする
    /// （超過ぶんは <see cref="DesiredFloatDistance"/> ＝ 走る距離として表に出る）。
    /// </summary>
    public float FishHp01 => fishHpMax > DivideEpsilon
        ? SEED.Mathf.Clamped01(fishHp / fishHpMax)
        : 0f;

    /// <summary>
    /// 魚 HP を削り切ったか（＝魚が力尽きて抵抗をやめた）。
    ///
    /// <b>2026-09-09 改定でこれは釣り上げの成立条件ではなくなった</b>。
    /// 立っているあいだは <see cref="ComputeFloatDistanceStep"/> が
    /// 見た目距離の下限を無視して竿先まで寄せ切る、という意味だけを持つ
    /// （釣り上げの成否は <see cref="FishingController"/> が実測距離だけで決める）。
    /// バトル中だけ true になり得る（<see cref="EndFight"/> で必ず落ちる）。
    /// </summary>
    public bool FishDefeated => Active && fishHp <= FishHpZero;

    /// <summary>
    /// この 1 戦の判定が<b>すべて Excellent</b> だったか
    /// 【釣り上げ時の評価（Perfect / Good）を決める唯一の判断材料】。
    ///
    /// 1 度も判定が発生していない戦い（叩く前に釣れてしまった等）は false を返す
    /// （何もしていないのに「Perfect!」と出るのを避けるため）。
    ///
    /// <see cref="EndFight"/> のあとでも値は保たれる（<see cref="fightAllExcellent"/> の
    /// コメント参照）ので、釣り上げ処理の中でやり取りを畳んだあとに読んでよい。
    /// </summary>
    public bool AllExcellent => fightJudgedCount > 0 && fightAllExcellent;

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
    ///
    /// 対応は<b>線形のまま上限を持たない</b>。漂流物「魚回復」（<see cref="RecoverFishHp"/>）は
    /// 魚 HP を最大値より上へも積めるので、その場合この距離も
    /// 「掛かった距離 ＋ ヒット直後の引き距離」より外側へそのまま外挿される
    /// （＝肉を拾った数だけ遠くへ持って行かれる）。
    /// 実際にウキが出られる上限は <c>FishingController</c> 側の世界端クランプ
    /// （最長飛距離 ＋ 余裕）が決める。
    /// </summary>
    public float DesiredFloatDistance => SEED.Mathf.Max(fishHp, FishHpZero) * metersPerHp;

    /// <summary>
    /// <b>いま巻き取っている最中か</b>【「巻き入力が続いている」の唯一の判定】。
    ///
    /// 隙（<see cref="Phase.Rest"/>）のあいだだけ巻けるので、次の 3 つがすべて成り立つときに true:
    /// バトル中で一時停止していない／フェーズが隙／最後の巻き入力から
    /// <see cref="reelHoldSeconds"/> 秒以内。
    ///
    /// 用途は 2 つ。
    /// - 漂流物の巻き込み判定（「巻いていないときにそばを漂っているだけ」ではすり抜けさせる）
    /// - 魚の引き返しを止めるかどうか（<see cref="ComputeFloatDistanceStep"/>・2026-09-11 追加）。
    ///   ホイールはこま切れに入るので、1 フレームごとの
    ///   <see cref="reelEffectiveThisFrame"/> で引き返しを判断すると、
    ///   目盛と目盛の合間（巻き量を消化していないフレーム）に魚が引き返してしまい、
    ///   ゆっくり回したときに正味で寄らなくなる。保持つきのこちらを使う。
    /// </summary>
    public bool ReelingRecently
        => Active && !Paused && CurrentPhase == Phase.Rest
        && lastReelInputTime > NoReelInputTime
        && clockTime - lastReelInputTime <= SEED.Mathf.Max(reelHoldSeconds, 0f);

    /// <summary>
    /// <b>いま巻き取り操作が実際に効くか</b>【巻きが成立する状況の唯一の判定】。
    ///
    /// <see cref="Tick"/> が巻き取り量を消化するのは
    /// 「バトル中／一時停止でない／チュートリアルの説明で凍結していない／
    ///   魚 HP を削り切っていない／フェーズが隙（<see cref="Phase.Rest"/>）」の
    /// すべてが成り立つときだけ（<see cref="UpdateRest"/> へ到達する条件）なので、
    /// ここではその条件をそのまま式にしている。
    ///
    /// <see cref="ReelingRecently"/>（＝「直近に巻いた」保持つきの判定）とは目的が違う。
    /// あちらは漂流物の巻き込み用、こちらは<b>このフレームに巻けるか</b>の判定で、
    /// 巻き取り音（<c>FishingController.UpdateReelSound</c>）の鳴動可否に使う。
    /// </summary>
    public bool CanReelNow
        => Active && !Paused
        && !(TutorialRules.Active && TutorialRules.FightSuppressed)
        && !FishDefeated
        && CurrentPhase == Phase.Rest;

    /// <summary>糸切れの効果音パス（コントローラ側から鳴らす場合の参照用）。</summary>
    public string LineBreakSePath => lineBreakSePath;

    /// <summary>糸切れの効果音の音量。</summary>
    public float LineBreakSeVolume => lineBreakSeVolume;

    // ─── 拍時計の公開値 ───────────────────────────────────

    /// <summary>バトル開始からの経過秒数（<see cref="Paused"/> 中は進まない）。</summary>
    public float ClockTime => clockTime;

    /// <summary>
    /// <b>いまのフェーズが始まってからの経過秒数</b>（非バトル中は 0）。
    ///
    /// フェーズの「入りたて」を判定したい呼び出し側のための公開値。
    /// たとえば <c>FishingController.TryEatHookedFish</c> は、隙（<see cref="Phase.Rest"/>）へ
    /// 入った直後の数秒間だけわらしべ連鎖を弾く「猶予」に、この値を使う。
    /// 拍時計は <see cref="Paused"/> 中は進まないので、この経過秒数も止まる。
    /// </summary>
    public float SecondsSincePhaseStart
        => Active ? SEED.Mathf.Max(clockTime - phaseStartTime, 0f) : 0f;

    /// <summary>魚のテンポでの 1 拍の秒数（＝ 60 ÷ 魚の BPM）。</summary>
    public float SecondsPerBeat => secondsPerBeat;

    /// <summary>魚のテンポでの 1 小節の秒数（＝ 1 拍の秒数 × 拍子）。</summary>
    public float SecondsPerBar => secondsPerBeat * SEED.Mathf.Max(beatsPerBar, MinBeatsPerBar);

    /// <summary>隙（<see cref="Phase.Rest"/>）のテンポでの 1 拍の秒数（＝ 60 ÷ 隙のBPM）。</summary>
    public float RestSecondsPerBeat => restSecondsPerBeat;

    /// <summary>隙（<see cref="Phase.Rest"/>）のテンポでの 1 小節の秒数。</summary>
    public float RestSecondsPerBar => restSecondsPerBeat * SEED.Mathf.Max(beatsPerBar, MinBeatsPerBar);

    /// <summary>
    /// <b>いまのフェーズ</b>の 1 小節の秒数【小節の物差しを読む唯一の入口】。
    /// 隙だけ隙のBPM で数え、それ以外は魚の BPM で数える。
    /// フェーズに入る前（未設定）は魚のテンポを返す。
    /// </summary>
    public float PhaseBarSeconds => phaseBarSeconds > DivideEpsilon ? phaseBarSeconds : SecondsPerBar;

    /// <summary>いまのフェーズの 1 拍の秒数（＝ <see cref="PhaseBarSeconds"/> ÷ 拍子）。</summary>
    public float PhaseBeatSeconds => PhaseBarSeconds / SEED.Mathf.Max(beatsPerBar, MinBeatsPerBar);

    /// <summary>
    /// いまのフェーズに入ってからの通し拍番号（0 始まり）。
    /// 隙だけテンポが変わるため、拍はバトル全体ではなく<b>フェーズ内</b>で数える。
    /// 余白（LeadIn）は開始時刻が負なので 0,1,2… と素直に増える。
    /// </summary>
    public int BeatIndex => PhaseBeatSeconds > DivideEpsilon
        ? SEED.Mathf.FloorToInt((clockTime - phaseStartTime) / PhaseBeatSeconds)
        : 0;

    /// <summary>いまのフェーズに入ってからの通し小節番号（0 始まり）。</summary>
    public int BarIndex => beatsPerBar > 0 ? BeatIndex / beatsPerBar : 0;

    /// <summary>拍のなかの進行（0〜1）。0 が拍頭。</summary>
    public float BeatPhase01 => PhaseBeatSeconds > DivideEpsilon
        ? SEED.Mathf.Repeat((clockTime - phaseStartTime) / PhaseBeatSeconds, 1f)
        : 0f;

    /// <summary>小節のなかの進行（0〜1）。0 が小節頭。</summary>
    public float BarPhase01 => PhaseBarSeconds > DivideEpsilon
        ? SEED.Mathf.Repeat((clockTime - phaseStartTime) / PhaseBarSeconds, 1f)
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
        float grid = PhaseBeatSeconds / SEED.Mathf.Max(subdivision, 1);
        if (grid <= DivideEpsilon) { return 0f; }

        float offset = SEED.Mathf.Repeat(clockTime - phaseStartTime, grid);
        return offset <= grid * HalfScale ? offset : offset - grid;
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

    /// <summary>
    /// 1 フレームの上限を超えて余った巻き取り量（メートル）。
    /// 次フレーム以降に <see cref="reelInSpeedMax"/> の速さで消化する
    /// （＝入力を捨てずに、巻きの<b>速度</b>だけを一定に保つ）。
    /// </summary>
    private float pendingReelAmount = 0f;

    /// <summary>
    /// このフレームに<b>実際に巻けたか</b>（隙フェーズで有効な巻き入力があったか）。
    /// <see cref="UpdateRest"/> が毎フレーム立て直し、<see cref="ComputeFloatDistanceStep"/> が
    /// 「巻けば必ず寄る」ぶん（<see cref="reelVisibleSpeed"/>）を足すかどうかの判断に使う。
    ///
    /// <see cref="reelInputHeld"/>（巻き始めイベントの立ち上がり検出）とは目的が違うので
    /// 別の状態として持つ（あちらは通知の間引き専用）。
    /// </summary>
    private bool reelEffectiveThisFrame = false;

    /// <summary>バトル開始からの経過秒数（拍時計の唯一の時間源）。</summary>
    private float clockTime = 0f;

    /// <summary>1 拍の秒数（BPM から <see cref="BeginFight"/> で決まる）。</summary>
    private float secondsPerBeat = 0.6f;

    /// <summary>隙（<see cref="Phase.Rest"/>）での 1 拍の秒数（＝ 60 ÷ <see cref="restBpm"/>）。</summary>
    private float restSecondsPerBeat = 0.6f;

    /// <summary>
    /// <b>いまのフェーズ</b>の 1 小節の秒数【フェーズ長・打点時刻の唯一の物差し】。
    ///
    /// 隙だけ <see cref="restBpm"/> で数えるので、全フェーズを 1 本の小節グリッドへ
    /// 揃えることはできない。そこでフェーズごとに物差しを持ち、フェーズの開始時刻は
    /// 「直前のフェーズの終了時刻」をそのまま引き継ぐ（<see cref="EnterPhase"/>）。
    /// </summary>
    private float phaseBarSeconds = 0f;

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
    /// <see cref="Paused"/>（またはチュートリアルの説明）によってドラムを
    /// <b>一時停止</b>しているか（true = <see cref="ResumeDrumLoop"/> 待ち）。
    /// 停止（<see cref="StopDrumLoop"/>）とは違い再生位置は保持されている。
    /// </summary>
    private bool drumPausedByFight = false;

    /// <summary>現在のフェーズが始まった時刻（秒・必ず小節頭）。</summary>
    private float phaseStartTime = 0f;

    /// <summary>現在のフェーズが終わる時刻（秒・必ず小節頭）。</summary>
    private float phaseEndTime = 0f;

    /// <summary>現在のフェーズの長さ（小節）。走り（<see cref="Phase.Run"/>）は拍で数えるので参照しない。</summary>
    private int phaseBars = 1;

    /// <summary>
    /// 「いまの隙が終わったら走り（<see cref="Phase.Run"/>）を挟む」予約
    /// 【走りを挿し込むかどうかの唯一の状態】。
    ///
    /// 隙（<see cref="Phase.Rest"/>）の最中に魚 HP の回復（<see cref="RecoverFishHp"/>）が
    /// 要求されたときだけ立ち、その隙が終わる瞬間に消費される（<see cref="PeekNextPhase"/>）。
    /// <b>隙の外で回復が起きた場合は立てない</b>（走りは「巻いている最中に取り返された」ことを
    /// 見せるための演出なので、巻けない区間で拾った場合まで走らせる意味がないため）。
    /// </summary>
    private bool recoverRunPending = false;

    /// <summary>
    /// 隙の最中に拾った「魚回復」を貯めておく量（魚 HP の実値・複数個ぶん累積）
    /// 【予約回復の唯一の置き場】。
    ///
    /// 隙のあいだは魚 HP を動かさない（＝目標距離も動かない）ため、拾った瞬間は
    /// ここへ足すだけにして、隙を抜ける瞬間に <see cref="CommitPendingFishHpRecovery"/> が
    /// まとめて魚 HP へ入れる。
    /// </summary>
    private float pendingFishHpRecovery = 0f;

    /// <summary>
    /// 走り（<see cref="Phase.Run"/>）を始めた瞬間のウキ→竿先の距離（メートル）。
    /// <see cref="RunStartDistanceUnset"/> なら未確定（走りの 1 フレーム目に確定する）。
    /// </summary>
    private float runStartDistance = RunStartDistanceUnset;

    /// <summary>次のフェーズの予告（1 拍前）を済ませたか。</summary>
    private bool nextPhaseAnnounced = false;

    /// <summary>
    /// ビートパターンのテキストを読み込んで保持する蔵書
    /// 【譜面データの唯一の供給元】。バトルをまたいで使い回し、
    /// ファイルが更新されたら戦闘の合間に読み直す（ホットリロード）。
    /// </summary>
    private readonly BeatPatternLibrary patternLibrary = new();

    /// <summary>
    /// いま出題／回答しているビートパターン（＝戦闘サイクル 1 周ぶんの打点の並び）。
    /// 出題フェーズへ入るたびに <see cref="ResolveCyclePattern"/> が確定させる。
    /// </summary>
    private BeatPattern? currentPattern = null;

    /// <summary>
    /// <see cref="drawPatternEachCycle"/> が false のときに、この 1 戦で使い続ける
    /// パターン（最初の出題で 1 度だけ抽選する）。true のときは常に null。
    /// </summary>
    private BeatPattern? fightPattern = null;

    /// <summary>出題フェーズで鳴らす打点の時刻（秒・絶対時刻・昇順）。</summary>
    private readonly List<float> callHitTimes = new();

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
    /// 判定表示の収まり調整（<see cref="ApplyJudgementFit"/>）が済んだか。
    /// 対象アクタのハンドルが揃った時点で 1 度だけ調整し、以後は何もしない
    /// （毎フレーム大きさを測り直すと、ポップ中の大きさを基準にしてしまうため）。
    /// </summary>
    private bool judgementFitDone = false;

    /// <summary>直前の回答が完璧（期待打点がすべて Excellent）だったか。隙の長さを決める。</summary>
    private bool lastAnswerPerfect = false;

    /// <summary>
    /// この 1 戦（<see cref="BeginFight"/> から釣り上げまで）で下した判定が
    /// <b>すべて Excellent</b> だったか【釣り上げ時の評価表示の元データ】。
    ///
    /// <see cref="lastAnswerPerfect"/> は<b>直前の 1 フレーズ</b>だけの評価で、
    /// 隙の長さを決めるために毎フレーズ上書きされる。
    /// 「戦い全体を通して完璧だったか」はそれとは別の情報なので、
    /// 1 戦を通して落ちっぱなしになるフラグを別に持つ。
    ///
    /// <b>リセットは <see cref="BeginFight"/> だけ</b>で行い、
    /// <see cref="ResetRuntimeState"/>（＝<see cref="EndFight"/> からも呼ばれる）では触らない。
    /// 釣り上げの入口（<c>FishingController.FinishReeling</c>）は
    /// <see cref="EndFight"/> を呼んだ<b>後</b>にこの値を読むため、
    /// 終了処理で消してしまうと必ず初期値になってしまうからである。
    /// </summary>
    private bool fightAllExcellent = true;

    /// <summary>
    /// この 1 戦で下した判定の数（<see cref="fightAllExcellent"/> と同じ寿命）。
    /// 1 度も叩かずに終わった戦いを「完璧」と呼ばないための下駄。
    /// </summary>
    private int fightJudgedCount = 0;

    /// <summary>
    /// 次の隙（Rest）へ持ち越す延長小節数【漂流物「ひるませ」の持ち越し分】。
    /// 隙以外のフェーズで <see cref="AddRestBars"/> が呼ばれたぶんをここへ貯め、
    /// 次に隙へ入るときの長さへ足して 0 に戻す。
    /// </summary>
    private int pendingExtraRestBars = 0;

    /// <summary>
    /// 最後に巻き入力があった時刻（<see cref="clockTime"/> と同じ時間軸）。
    /// <see cref="NoReelInputTime"/> なら「このバトルではまだ 1 度も巻いていない」。
    /// </summary>
    private float lastReelInputTime = NoReelInputTime;

    /// <summary>
    /// 直前のフレームに巻き取り入力があったか。
    /// 「巻き始め」の 1 回だけイベント（<see cref="FishingEvents.Reel"/>）を流すための立ち上がり検出に使う。
    /// </summary>
    private bool reelInputHeld = false;

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
    /// 生成済みの打点アイコン（プール本体）。添字は打点の通し番号と対応する。
    /// <b>使い終わっても破棄しない</b>（アルファ 0 で隠すだけ）。破棄は <see cref="OnDestroy"/> のみ。
    /// </summary>
    private readonly List<SEED.GameObject> iconPool = new();

    /// <summary>
    /// プール内アイコンの Sprite ハンドル（<see cref="iconPool"/> と同じ添字）。
    /// <c>Instantiate</c> は<b>フレーム末尾</b>にアクタを組み立てるため、生成した直後の
    /// フレームは <c>GetComponent</c> が null を返す。そこで遅延取得し、
    /// 取れるようになったフレームで埋める（<see cref="ResolveIconHandles"/>）。
    /// </summary>
    private readonly List<SEED.Sprite?> iconSprites = new();

    /// <summary>
    /// プール内アイコンの CanvasTransform ハンドル（<see cref="iconPool"/> と同じ添字）。
    /// 取得タイミングの事情は <see cref="iconSprites"/> と同じ。
    /// </summary>
    private readonly List<SEED.CanvasTransform?> iconTransforms = new();

    /// <summary>
    /// 打点アイコンのフェードアウト開始時刻（秒・絶対時刻）。
    /// <see cref="NoFadeStart"/> ならフェードしていない。
    /// </summary>
    private float iconFadeStartTime = NoFadeStart;

    /// <summary>打点アイコンへ大きさを書き込んだ枚数（枚数が変わったときだけ書き直すための控え）。</summary>
    private int iconSizeAppliedCount = 0;

    /// <summary>
    /// 糸ゲージを描いてよいか（＝バトル UI を出している最中か）。
    /// <see cref="ApplyUi"/> で立ち、<see cref="HideUi"/> で下りる。
    /// プリミティブ描画は「描かない＝消える」ので、非表示はこのフラグだけで足りる。
    /// </summary>
    private bool gaugeVisible = false;

    // ─── ライフサイクル ───────────────────────────────────

    /// <summary>
    /// 開始時に UI を隠し（バトル中以外は一切見せない）、打点アイコンを作り置きする。
    /// アイコンはアルファ 0 の状態で生成されるので、置いた時点では見えない。
    /// </summary>
    public override void OnStart()
    {
        ResetRuntimeState();
        HideUi();
        EnsureIconPool(initialBeatIconPool);

        // ビートパターンは起動時に 1 度読み込む（以後は LateUpdate のホットリロードで追従する）
        patternLibrary.Configure(beatPatternPath, CyclePatternBars);
    }

    /// <summary>
    /// 破棄時は UI を隠し、プールした打点アイコンをまとめて破棄する
    /// 【プール破棄の唯一の場所】。
    /// </summary>
    public override void OnDestroy()
    {
        HideUi();
        DestroyIconPool();
    }

    // 進行（拍時計・判定・巻き）は自前で回さず、すべて FishingController が Tick() で駆動する
    // （実行順の曖昧さを排除するため）。毎フレーム自前で行うのは下の LateUpdate（描画）だけである。

    /// <summary>
    /// Update 後の更新【糸ゲージとマーカーを描く唯一の場所】。
    ///
    /// ゲージはスプライトではなくプリミティブ（<see cref="SEED.Draw"/>）で描くので、
    /// 出し続けるには毎フレーム積み直す必要がある。積むのはここ 1 か所だけにして、
    /// <see cref="Tick"/>（コントローラの Update から呼ばれる）で更新した最新の状態を
    /// 1 フレームに 1 回だけ描く（2 か所で積むと同じ図形が二重に描かれる）。
    /// </summary>
    /// <param name="ctx">フレーム情報（未使用）。</param>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
        // ビートパターンの更新確認は<b>戦闘中でないときだけ</b>行う。
        // ＝ 読み直しが効くのは必ず次の戦闘からで、進行中の譜面が途中で入れ替わらない。
        if (!Active) { patternLibrary.PollHotReload(SEED.Time.UnscaledElapsedTime); }

        // 判定表示の収まり調整（対象ハンドルが揃うまで毎フレーム試し、揃ったら 1 度だけ実行）
        ApplyJudgementFit();

        // 表示用のゲージは「ゲームが止まっていても動く」演出なので実時間で進める
        UpdateGaugeDisplay(SEED.Time.UnscaledDeltaTime);
        DrawGauge();
    }

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
        => BeginFight(fish, judge, hookDistance, InitialLine(judge));

    /// <summary>
    /// わらしべで乗り換えたときのやり取り開始【乗り換え専用の入口】。
    /// 初期の糸の残りを合わせランクからではなく
    /// 「乗り換え前の残り ＋ <see cref="chainSwapLineRecover"/>」（上限 1.0）にする。
    /// </summary>
    /// <param name="fish">乗り換え先の魚。</param>
    /// <param name="judge">合わせ判定（乗り換えでは Excellent 扱い。評価の初期化にのみ使う）。</param>
    /// <param name="hookDistance">乗り換えた瞬間のウキ→竿先の水平距離（メートル）。</param>
    /// <param name="lineBeforeSwap">乗り換え前の糸の残り（0〜1）。</param>
    public void BeginFightAfterChainSwap(
        Fish fish, FishingController.HookJudgement judge, float hookDistance, float lineBeforeSwap)
        => BeginFight(fish, judge, hookDistance,
                      lineBeforeSwap + SEED.Mathf.Max(chainSwapLineRecover, 0f));

    /// <summary>
    /// やり取り開始の共通実装。初期の糸の残りを引数で受ける
    /// （通常のヒットは合わせランク由来、乗り換えは回復量込みの値）。
    /// </summary>
    /// <param name="initialLine01">初期の糸の残り（<see cref="Line01Min"/>〜<see cref="Line01Max"/> にクランプする）。</param>
    private void BeginFight(
        Fish fish, FishingController.HookJudgement judge, float hookDistance, float initialLine01)
    {
        ResetRuntimeState();

        // 1 戦を通した評価は BeginFight でだけ初期化する
        // （ResetRuntimeState は EndFight からも呼ばれるため、そこで消すと
        //   釣り上げ処理が読む頃には必ず初期値になってしまう）。
        fightAllExcellent = true;
        fightJudgedCount = 0;

        target = fish;
        Active = true;
        Line01 = SEED.Mathf.Clamped(initialLine01, Line01Min, Line01Max);

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

        InvalidateIconCache();
        ApplyUi();

        // やり取り（リズム勝負）が始まった
        SEED.Events.Raise(FishingEvents.FightBegin);

        SEED.Debug.Log($"[Fight] 開始: {fish.DisplayName} / 総合力 {CurrentFishPower():F2} vs 竿 {rodPower:F2}"
                     + $" / 魚HP {fishHpMax:F1}（取り分 {fishShare:P0}）"
                     + $" / 掛かった距離 {hookDistance:F1}m → 目標 {DesiredFloatDistance:F1}m"
                     + $" / {BpmOf(fish):F0}BPM {beatsPerBar}拍子 / 隙 {restBpm:F0}BPM"
                     + $" / パターン {patternLibrary.Count} 行"
                     + $" / 初期の糸の残り {Line01:F2}");
    }

    /// <summary>
    /// 糸の残りを満タンへ戻す【復帰の唯一の入口】。
    ///
    /// チュートリアルの「ミスしたらもう一周」で、周回の頭にゲージを戻すために使う。
    /// 実値は即座に満タンになり、<b>見た目だけ</b>が指定秒数かけて追いつく
    /// （<see cref="Easing.OutBack"/> なので少し行き過ぎてから収まり、「ぬっ」と伸びて見える）。
    /// </summary>
    /// <param name="seconds">見た目が追いつくまでの秒数（0 以下なら即座に満タン表示）。</param>
    public void RestoreLineFull(float seconds)
    {
        gaugeEaseFrom    = gaugeDisplayLine01;
        gaugeEaseElapsed = 0f;
        gaugeEaseSeconds = SEED.Mathf.Max(seconds, 0f);

        Line01     = Line01Max;
        LineBroken = false;

        if (gaugeEaseSeconds <= 0f) { gaugeDisplayLine01 = Line01; }
    }

    /// <summary>
    /// バトルを終了する【終了の唯一の出口】。
    /// 釣り上げ成功・糸切れ・キャンセルのいずれからも呼ばれてよい（多重呼び出し安全）。
    /// </summary>
    public void EndFight()
    {
        // 隙（スタン）の最中に終わった場合も「隙が終わった」ことは通知する。
        // ここで流さないと、購読側（チュートリアル・演出）が
        // 開始だけ受け取って終了を受け取れない状態になる。
        bool wasResting = Active && CurrentPhase == Phase.Rest;

        ResetRuntimeState();
        HideUi();

        if (wasResting) { SEED.Events.Raise(FishingEvents.StunEnd); }
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

        // 「このフレームに巻けたか」は毎フレームここで必ず落とし、
        // 実際に巻けたとき（UpdateRest）だけ立て直す。こうしておかないと
        // 一時停止・チュートリアル凍結・力尽きで早期 return したときに、
        // 前フレームの値が残ってウキが勝手に寄り続ける。
        reelEffectiveThisFrame = false;

        // チュートリアルが説明の台詞を読ませているあいだは、時間停止が効いているかに
        // 関わらずフェーズ進行を凍結する（TutorialRules.FightSuppressed の説明を参照）。
        // 上書きが無ければこの分岐は素通りし、従来どおり進む。
        bool suppressedByTutorial = TutorialRules.Active && TutorialRules.FightSuppressed;

        // 一時停止中（わらしべ連鎖のアタリ受付中・チュートリアルの説明中）は
        // 時計も値も一切進めない。
        if (Paused || suppressedByTutorial)
        {
            // 拍時計だけが止まってドラムが鳴り続けると位相が壊れるので、ドラムも凍結する。
            // 再生位置ごと止める（PauseDrumLoop）ので、再開時は続きから鳴らせば位相が合う。
            PauseDrumLoop();
            ApplyUi();
            return;
        }

        // 巻き取り量は必ずここで 1 フレームの上限へ均す【頭打ちの唯一の適用点】。
        // これより下（UpdateRest → ApplyReel）は均された量だけを見る。
        // 隙（Rest）以外のフェーズでは均した量が誰にも使われずに消えるが、
        // これは従来（生の巻き量をそのまま捨てていた）と同じ挙動である。
        reelAmount = ThrottleReelAmount(reelAmount, deltaTime);

        // 魚 HP を削り切ったあとは、魚はもう抵抗しない。
        // 拍時計・出題・糸の消耗をすべて止め、ウキが竿先へ寄り切る
        // （<see cref="ComputeFloatDistanceStep"/>）のを待つだけの状態にする。
        // 釣り上げの成立判定はコントローラ側（HP 0 かつ竿先の近傍）が行う。
        if (FishDefeated)
        {
            // 拍時計を止めるのと同じ理由でドラムも凍結する（終了時に ResetRuntimeState が止める）
            PauseDrumLoop();
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
    /// 出題・回答中はウキを止めておき、拍を読む画面が揺れないようにする。
    ///
    /// <b>隙（Rest）のあいだ【2026-09-09 改定・見た目距離の分離 / 2026-09-11 引き返しの見直し】</b>
    /// 見た目の距離は「魚 HP から決まる目標距離（<see cref="DesiredFloatDistance"/>）」
    /// そのものではなく、次の 2 項の和で毎フレーム動かす。
    /// <code>
    /// 第1項（ズレ） 目標が沖側 → ＋min(fishPullSpeed × 戦闘力比 × 巻き中係数, 差)
    ///                            （巻き中係数 ＝ 巻いていれば reelPullbackScale／既定 0、
    ///                              巻いていなければ 1。目標距離は追い越さない）
    ///               目標が手前 → −min(|差| × visibleDistanceReturnRate, reelInSpeedMax, |差|)
    /// 第2項（巻き） 巻き入力中は必ず −reelVisibleSpeed（魚の強さに一切依存しない）
    /// 下限クランプ  見た目距離は visibleDistanceMin より手前へは詰まらない
    /// </code>
    /// ＝<b>巻いているあいだは必ず寄り（正味 −reelVisibleSpeed 以下）、
    /// 手を止めたときだけ魚が引き返す</b>。
    /// 引き返しは「目標距離 − 見た目距離」を超えられないので、
    /// <b>残りの魚 HP がそのまま「引き返せる余力」</b>になる（＝HP を削る意味はここに残る）。
    /// <b>魚 HP の減り方（<see cref="ReelHpPerUnit"/>）は一切変えていない</b>ので
    /// 「巻いた距離 × 巻き効率」で HP が減る点は不変。
    ///
    /// <b>2026-09-11 の修正内容</b>: 第 1 項の「目標が沖側」に
    /// <c>|差| × visibleDistanceReturnRate</c> の上限無しバネが max で混ざっていたため、
    /// 差が <c>reelVisibleSpeed ÷ visibleDistanceReturnRate</c>（既定 4m）を超えると
    /// 引き返しが巻きを上回り、正味の寄り速度が
    /// <c>目標距離の縮む速さ ＝ 巻いた距離 × 巻き効率</c>（格上ほど 0 に近い）まで落ちていた。
    /// これが「HP が高いと巻いても寄ってこない」の正体。
    /// <b>余白（<see cref="Phase.LeadIn"/>）中だけは例外</b>で、掛かった直後に沖へ走る演出として
    /// <see cref="leadInStartDistance"/> から <see cref="DesiredFloatDistance"/>（＝この時点では
    /// 魚 HP が満タンなので「掛かった距離 ＋ 引き距離」と一致する）まで、
    /// <see cref="PhaseProgress01"/> を easeOut で使い滑らかに引き伸ばす。
    /// <b>走り（<see cref="Phase.Run"/>）も同じ扱い</b>で、
    /// 「走り始めた距離 → 回復後の <see cref="DesiredFloatDistance"/>」まで easeOut で引き伸ばす。
    /// </summary>
    /// <param name="currentDistance">現在のウキ→竿先の水平距離（メートル）。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <returns>距離の増減量（＋ が沖／− が手元）。</returns>
    public float ComputeFloatDistanceStep(float currentDistance, float deltaTime)
    {
        if (!Active || Paused || target is null) { return 0f; }

        // 力尽きた魚は引かない: フェーズに関わらず、寄せ速度の上限で竿先（距離 0）まで寄せ続ける。
        // 行き過ぎないよう残り距離でクランプする。
        if (FishDefeated)
        {
            float pull = SEED.Mathf.Max(reelInSpeedMax, 0f) * SEED.Mathf.Max(deltaTime, 0f);
            return -SEED.Mathf.Min(pull, SEED.Mathf.Max(currentDistance, 0f));
        }

        if (CurrentPhase == Phase.LeadIn)
        {
            // easeOut(2 次): 立ち上がりは速く、余白の終わりに向けて滑らかに減速する。
            float t = SEED.Mathf.Clamped01(PhaseProgress01);
            float eased = 1f - (1f - t) * (1f - t);
            float leadInTarget = SEED.Mathf.Lerp(leadInStartDistance, DesiredFloatDistance, eased);
            return leadInTarget - currentDistance;
        }

        if (CurrentPhase == Phase.Run)
        {
            // 走り（隙中に拾った「魚回復」を、隙が終わってからまとめて効かせる区間）。
            //
            // 行き先は<b>回復後の魚 HP に対応する目標距離</b>（DesiredFloatDistance）そのもの。
            // 目標距離は「魚 HP × 1HP あたりの距離」の線形なので、肉を 2 個拾っていれば
            // 2 個ぶん遠くなる（魚 HP が最大値を超えていてもそのまま外挿される）。
            // 余白（LeadIn）と同じ easeOut(2 次) で、いま居る距離からそこまで引き伸ばす。
            //
            // 目標が手前にある（＝既に目標より沖に居る）場合は走らない。
            // 「走り」で岸側へ戻ると画として逆になるので、開始距離で下支えする。
            if (runStartDistance <= RunStartDistanceUnset)
            {
                runStartDistance = SEED.Mathf.Max(currentDistance, 0f);
            }

            float runGoal = SEED.Mathf.Max(runStartDistance, DesiredFloatDistance);
            float t = SEED.Mathf.Clamped01(PhaseProgress01);
            float eased = 1f - (1f - t) * (1f - t);
            float runTarget = SEED.Mathf.Lerp(runStartDistance, runGoal, eased);
            return runTarget - currentDistance;
        }

        if (CurrentPhase != Phase.Rest) { return 0f; }

        float dt = SEED.Mathf.Max(deltaTime, 0f);

        // ── 第 1 項: 目標距離とのズレを詰める項 ──────────────────────
        // 見た目距離（currentDistance）と、魚 HP から決まる目標距離（DesiredFloatDistance）の
        // ズレをどちら向きに詰めるかを決める。向きによって速度の決め方が違う。
        float gap = DesiredFloatDistance - currentDistance;          // ＋ ＝ 目標が沖側

        float step = 0f;
        if (gap > 0f)
        {
            // 目標のほうが遠い ＝ 魚が沖へ引き返す局面。
            //
            // 速度は「引き速度（fishPullSpeed × 戦闘力比）」だけで決める【上限つきの一定速度】。
            // 【2026-09-11 修正】以前はここに「ズレ × visibleDistanceReturnRate」という
            // 上限の無いバネを max で混ぜていた。そのためウキを目標より手前へ寄せるほど
            // 引き返しが強くなり、ズレが reelVisibleSpeed ÷ visibleDistanceReturnRate
            // （既定 4m）を超えた時点で第 2 項（巻き）を食い切ってしまい、
            // 正味の寄り速度が「巻いた距離 × 巻き効率」＝格上ほど 0 に近い値まで落ちていた。
            // ＝「魚 HP が高いと巻いてもウキが寄ってこない」の直接の原因。
            //
            // さらに巻いているあいだは reelPullbackScale 倍（既定 0 ＝ 引き返さない）へ弱め、
            // 「巻いている間は必ず寄る」を保証する。
            //
            // 「巻いている間」の判定に ReelingRecently（reelHoldSeconds の保持つき）を使うのは、
            // ホイール入力がこま切れだから。巻き量は ThrottleReelAmount が
            // reelInSpeedMax の速さで均して消化するので、ゆっくり回すと
            // 「消化しているフレーム」が飛び飛びになる。ここで reelEffectiveThisFrame
            // （そのフレームに消化したか）を使うと、目盛と目盛の合間に魚が引き返してしまい、
            // 回す速さが reelInSpeedMax の 1/3 ほどに届かないと正味で寄らなくなる。
            float pullbackScale = ReelingRecently
                                ? SEED.Mathf.Max(reelPullbackScale, 0f)
                                : 1f;
            float outwardSpeed = fishPullSpeed * PullRateClamped() * pullbackScale;
            step += SEED.Mathf.Min(outwardSpeed * dt, gap);          // 目標は追い越さない
        }
        else if (gap < 0f)
        {
            // 目標のほうが近い ＝ 糸のテンションで手元側へ戻る局面。
            // ズレに比例したバネ（visibleDistanceReturnRate）で戻す。
            // 寄せ速度の上限（reelInSpeedMax）でクランプし、目標は追い越さない。
            // 巻き効率の高い格下の魚ほど目標距離が速く縮んでこの局面になりやすく、
            // そのぶん一気に寄る（＝魚の強さによる寄りやすさの差はここで付く）。
            float springSpeed = -gap * SEED.Mathf.Max(visibleDistanceReturnRate, 0f);
            float inwardSpeed = SEED.Mathf.Min(springSpeed, SEED.Mathf.Max(reelInSpeedMax, 0f));
            step -= SEED.Mathf.Min(inwardSpeed * dt, -gap);
        }

        // ── 第 2 項: 巻き入力（「巻けば必ず寄る」の唯一の加算点）──────────
        // 魚 HP の減り方（ReelHpPerUnit）とは無関係に、巻いているあいだは必ず
        // reelVisibleSpeed ぶん手元へ寄せる。格上の魚（魚力 ≫ 竿）でも
        // 「巻いているのにウキが動かない」が起きないのは、この 1 行と
        // 第 1 項の巻き中係数（reelPullbackScale・既定 0）の組み合わせによる。
        if (reelEffectiveThisFrame)
        {
            step -= SEED.Mathf.Max(reelVisibleSpeed, 0f) * dt;
        }

        // ── 見た目距離の下限クランプ ─────────────────────────
        // 魚 HP が残っているあいだはウキを竿先へめり込ませない
        // （魚が力尽きた後は関数冒頭の FishDefeated の枝が竿先まで寄せ切る）。
        float floorDistance = SEED.Mathf.Max(visibleDistanceMin, 0f);
        float allowedInward = SEED.Mathf.Max(currentDistance - floorDistance, 0f);
        if (step < -allowedInward) { step = -allowedInward; }

        return step;
    }

    // ─── 公開 API: 漂流物の効果 ─────────────────────────────
    //
    // 漂流物（DriftItem）を巻き込んだときの効果は、すべてここに集約する。
    // 判定（ウキと漂流物が重なったか）と種類ごとの割り振りは FishingController が行い、
    // 本スクリプトは「値をどう動かすか」だけを担う（単一責任）。

    /// <summary>
    /// 隙（<see cref="Phase.Rest"/>）を小節単位で延長する【漂流物「ひるませ」の効果】。
    ///
    /// ・いま隙の最中なら、その場でフェーズの長さ（<see cref="phaseBars"/>）と
    ///   終了時刻（<see cref="phaseEndTime"/>）を伸ばす。針の 1 周も伸びた長さで割り直される。
    /// ・隙以外（出題・回答・余白）なら <see cref="pendingExtraRestBars"/> へ貯め、
    ///   <b>次に隙へ入るとき</b>にその長さへ足す（<see cref="EnterPhase"/>）。
    ///   ＝ 巻いている最中にしか拾えない仕様なので通常は前者だが、
    ///      拾った直後にフェーズが変わった場合でも効果を捨てない。
    /// </summary>
    /// <param name="bars">延長する小節数（1 未満は 1 小節として扱う）。</param>
    public void AddRestBars(int bars)
    {
        if (!Active) { return; }

        int extra = SEED.Mathf.Max(bars, MinExtraRestBars);

        if (CurrentPhase == Phase.Rest)
        {
            phaseBars += extra;
            phaseEndTime = phaseStartTime + phaseBars * phaseBarSeconds;
            nextPhaseAnnounced = false;     // 予告済みでも、伸びた終わりで出し直す
            SEED.Debug.Log($"[Fight] 漂流物: 隙を {extra} 小節延長（合計 {phaseBars} 小節）");
            return;
        }

        pendingExtraRestBars += extra;
        SEED.Debug.Log($"[Fight] 漂流物: 次の隙へ {pendingExtraRestBars} 小節ぶんの延長を持ち越し");
    }

    /// <summary>
    /// 魚 HP を割合ぶん回復する【漂流物「魚HPの回復」の効果】。
    ///
    /// 回復量 ＝ 魚HP最大値 × <paramref name="fraction"/>。<b>上限クランプはしない</b>
    /// （＝肉をたくさん拾えば魚 HP は最大値の 100% を超える。ゲージ表示 <see cref="FishHp01"/>
    /// は 100% で頭打ちになるが、目標距離 <see cref="DesiredFloatDistance"/> は
    /// 「魚 HP × 1HP あたりの距離」の線形のまま外挿される）。
    ///
    /// <b>効かせるタイミング【2026-09-10 改定】</b>
    /// <code>
    /// 隙（Rest）中 かつ 走りが有効（recoverRunBeats ≧ 1）
    ///     … その場では一切効かせず recoverRunPending へ<b>足して貯める</b>（複数個ぶん累積）。
    ///       隙の間は魚 HP も目標距離も動かないので、ウキが巻いている最中に
    ///       いきなり沖へ引かれることはない。貯めたぶんは隙を抜ける瞬間に
    ///       まとめて入り（CommitPendingFishHpRecovery）、続く走り（Run）で
    ///       「新しい魚 HP に対応する距離」まで一気に持って行かれる。
    /// それ以外（出題・回答・余白・走り中／走りが無効）
    ///     … 貯めずに<b>その場で即時</b>加算する。巻けない区間なので画としての走りは要らず、
    ///       次の隙で通常の距離制御（バネ）が伸びた目標距離へ引き戻す（従来どおり）。
    /// </code>
    /// </summary>
    /// <param name="fraction">魚 HP 最大値に対する回復割合（0 以下なら何もしない）。</param>
    public void RecoverFishHp(float fraction)
    {
        if (!Active || fraction <= 0f) { return; }

        float amount = fishHpMax * fraction;

        // 隙の最中は「予約」だけ。走りが無効なら貯めても出せないので即時へ倒す。
        if (CurrentPhase == Phase.Rest && recoverRunBeats > NoRecoverRunBeats)
        {
            pendingFishHpRecovery += amount;
            recoverRunPending = true;
            SEED.Debug.Log($"[Fight] 漂流物: 魚HP回復 {amount:F2} を予約（合計 {pendingFishHpRecovery:F2}）"
                         + $" ／ 隙のあとに走り（{recoverRunBeats}拍）");
            return;
        }

        float before = fishHp;
        fishHp += amount;
        SEED.Debug.Log($"[Fight] 漂流物: 魚HP回復（即時） {before:F2} → {fishHp:F2}"
                     + $" ／ 目標距離 {DesiredFloatDistance:F1}m");
    }

    /// <summary>
    /// 貯めておいた魚 HP の回復をまとめて効かせる【予約回復の唯一の適用点】。
    ///
    /// 呼ぶのは<b>隙を抜ける瞬間</b>（<see cref="UpdatePhaseTransition"/>）だけ。
    /// 走り（<see cref="Phase.Run"/>）が実際に挟まらない経路（チュートリアルの上書き等）でも
    /// ここを通すので、貯めた回復が黙って消えることはない。
    /// 上限クランプはしない（<see cref="RecoverFishHp"/> の説明を参照）。
    ///
    /// 隙の途中で魚を削り切った（<see cref="FishDefeated"/>）場合は
    /// <see cref="Tick"/> がフェーズ遷移ごと止まるためここへ来ず、予約は
    /// <see cref="ResetRuntimeState"/> で捨てられる（＝倒し切ったあとに生き返らない）。
    /// </summary>
    private void CommitPendingFishHpRecovery()
    {
        if (pendingFishHpRecovery <= 0f) { return; }

        float before = fishHp;
        fishHp += pendingFishHpRecovery;
        SEED.Debug.Log($"[Fight] 漂流物: 予約していた魚HP回復 {pendingFishHpRecovery:F2} を適用"
                     + $"（{before:F2} → {fishHp:F2} ／ 目標距離 {DesiredFloatDistance:F1}m）");
        pendingFishHpRecovery = 0f;
    }

    /// <summary>
    /// 糸の残りを割合ぶん回復する【漂流物「糸の回復」の効果・唯一の回復手段】。
    /// 糸の残り ＝ min(1, 糸の残り ＋ <paramref name="fraction"/>)。
    /// <b>既に切れている糸は戻せない</b>（切れた瞬間にバトルは終わるため）。
    /// </summary>
    /// <param name="fraction">足す割合（0 以下なら何もしない）。</param>
    public void RecoverLine(float fraction) => RecoverLine(fraction, "漂流物");

    /// <summary>
    /// 糸の残りを <paramref name="fraction"/>（1.0 ＝ 満タン）だけ回復する【回復の唯一の実装】。
    /// 漂流物（<see cref="RecoverLine(float)"/>）と Perfect 評価の回復（<see cref="RecoverLineForPerfect"/>）が共用する。
    /// </summary>
    /// <param name="fraction">回復量（糸の残りの割合）。</param>
    /// <param name="reason">ログに出す回復の理由。</param>
    private void RecoverLine(float fraction, string reason)
    {
        if (!Active || LineBroken || fraction <= 0f) { return; }

        float before = Line01;
        Line01 = SEED.Mathf.Min(Line01 + fraction, Line01Max);
        SEED.Debug.Log($"[Fight] {reason}: 糸の残り回復 {before:P0} → {Line01:P0}");
    }

    /// <summary>
    /// 回答フレーズを Perfect で締めたときの糸回復。
    /// 回復量 ＝ Great 判定 1 個ぶんの減り（<see cref="greatSeconds"/> × <see cref="linePerSecondOfOffset"/>）
    /// × <see cref="perfectRecoverGreatCount"/>。減りと同じ式から作るので、判定の設定を変えても
    /// 「Great 何個分」という関係が保たれる。
    /// </summary>
    private void RecoverLineForPerfect()
    {
        float greatLoss = SEED.Mathf.Max(greatSeconds, 0f) * SEED.Mathf.Max(linePerSecondOfOffset, 0f);
        RecoverLine(greatLoss * SEED.Mathf.Max(perfectRecoverGreatCount, 0f), "Perfect");
    }

    // ─── 内部処理: リズムデータの取り込み ───────────────────

    /// <summary>
    /// 魚のテンポとビートパターンを取り込む【拍時計の初期化の唯一の入口】。
    ///
    /// 2026-09-09 改定で、出題データは<b>魚データではなくテキストファイル</b>
    /// （<see cref="beatPatternPath"/>）から読むようになった。魚固有なのは BPM だけで、
    /// 拍子・隙の長さはバトル側の設定に一本化してある。
    /// </summary>
    /// <param name="fish">掛かった魚（BPM だけを読む）。</param>
    private void SetupRhythm(Fish fish)
    {
        beatsPerBar = SEED.Mathf.Max(beatsPerBar, MinBeatsPerBar);
        secondsPerBeat = SecondsPerMinute / SEED.Mathf.Max(BpmOf(fish), MinBpm);
        restSecondsPerBeat = SecondsPerMinute / SEED.Mathf.Max(restBpm, MinBpm);

        // 想定小節数（＝戦闘サイクルの長さ）が変わっていれば読み直される
        patternLibrary.Configure(beatPatternPath, CyclePatternBars);

        currentPattern = null;
        fightPattern = null;
    }

    /// <summary>
    /// ビートパターン 1 行に期待する小節数（＝戦闘サイクルの長さ）
    /// 【想定小節数の唯一の定義】。出題フェーズの既定小節数をそのまま使う。
    /// </summary>
    private int CyclePatternBars => SEED.Mathf.Max(callBars, MinPhaseBars);

    /// <summary>魚の BPM（0 以下なら下限へクランプ）。</summary>
    /// <param name="fish">対象の魚。</param>
    private float BpmOf(Fish fish) => SEED.Mathf.Max(fish.RhythmBpm, MinBpm);

    /// <summary>
    /// このサイクルで使うビートパターンを確定させる【譜面抽選の唯一の入口】。
    ///
    /// 1. <see cref="drawPatternEachCycle"/> が false で既に引いてあれば、それを使い続ける
    /// 2. 魚のレベルに該当する行から抽選する（<see cref="BeatPatternLibrary.Pick"/>）
    /// 3. 1 行も無ければ「各拍の頭を叩くだけ」のフォールバックを合成して警告する
    ///
    /// 戻り値は必ず非 null なので、呼び出し側に「譜面が無い」分岐は要らない。
    /// </summary>
    private BeatPattern ResolveCyclePattern()
    {
        if (!drawPatternEachCycle && fightPattern is { } reused) { return reused; }

        int level = target is { } fish ? fish.Level : Fish.UnknownLevel;
        BeatPattern? picked = patternLibrary.Pick(level);

        if (picked is null)
        {
            picked = BuildFallbackPattern();
            SEED.Debug.LogWarning($"[Fight] レベル {level} に使えるビートパターンが 1 行もありません"
                                + $"（{patternLibrary.AssetPath}）。フォールバックで進めます");
        }

        if (!drawPatternEachCycle) { fightPattern = picked; }
        return picked;
    }

    /// <summary>
    /// ビートパターンが 1 行も使えないときに合成する安全な譜面
    /// 【フォールバック生成の唯一の場所】＝<b>各拍の頭だけを叩く</b>。
    /// 長さは戦闘サイクルの小節数（<see cref="CyclePatternBars"/>）に合わせる。
    /// </summary>
    private BeatPattern BuildFallbackPattern()
    {
        int bars = CyclePatternBars;
        int beats = SEED.Mathf.Max(beatsPerBar, MinBeatsPerBar);

        var positions = new List<double>(bars * beats);
        for (int i = 0; i < bars * beats; i++)
        {
            positions.Add((double)i / beats);
        }

        return new BeatPattern(positions, bars, LevelRange.Any,
                               FallbackPatternLineNumber, FallbackPatternLabel);
    }

    // ─── 内部処理: 拍時計とフェーズ ─────────────────────────

    /// <summary>
    /// メトロノームを進める【拍の音を鳴らす唯一の出口】。
    /// 拍番号が変わったフレームで 1 回だけ鳴らし、小節頭だけ音量を上げる。
    /// </summary>
    private void UpdateMetronome()
    {
        if (string.IsNullOrEmpty(metronomeSePath)) { return; }

        // 拍はフェーズ内で数える（隙だけテンポが変わるため、通し拍番号では強拍がズレる）
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
    /// （テンポと拍時計の原点が確定していないと開始時刻を計算できない）。
    ///
    /// 【設計】2026-09-09 改定
    /// ・素材は 4/4・<see cref="drumLoopBars"/> 小節と仮定し、再生速度を
    ///   <c>いまのテンポ(BPM) ÷ 素材の BPM</c> にして拍を一致させる
    ///   （<see cref="ApplyDrumSpeed"/>）。隙のあいだだけ <see cref="restBpm"/> に同期する。
    /// ・開始時刻は<b>余白の頭</b>。ただし余白が拍子の倍数でない（＝小節の途中で終わる）
    ///   ときだけ、小節線がズレないように最初の出題の頭（時刻 0）まで待つ。
    /// ・フェーズごとにテンポが変わるようになったため、旧「一定間隔での再同期」は廃止した。
    ///   位相合わせは <see cref="drumRestartAtCall"/>（出題の頭で鳴らし直す）に一本化する。
    /// </summary>
    private void SetupDrumLoop()
    {
        StopDrumLoop();
        drumScheduled = false;
        drumPausedByFight = false;

        if (string.IsNullOrEmpty(drumLoopPath)) { return; }

        int beats = SEED.Mathf.Max(leadInBeats, 0);
        int barBeats = SEED.Mathf.Max(beatsPerBar, MinBeatsPerBar);
        bool leadInBreaksBarLine = beats > 0 && beats % barBeats != 0;

        // 半端な余白のときだけ、最初の出題の頭（＝時計の原点 0）まで開始を遅らせる
        drumStartTime = leadInBreaksBarLine ? 0f : phaseStartTime;
        drumScheduled = true;

        // 開始時刻に既に達しているなら（＝余白の頭が小節頭）このフレームで鳴らし始める
        UpdateDrumLoop();
    }

    /// <summary>
    /// ドラムループを進める【開始待ち・一時停止復帰の唯一の集約点】。
    /// 拍時計を進めた直後に毎フレーム呼ぶ。
    /// </summary>
    private void UpdateDrumLoop()
    {
        if (!drumScheduled) { return; }

        // 一時停止から戻ったフレーム: 止めた位置から鳴らし直す。
        // 一時停止中は拍時計（clockTime）もドラムの再生位置も同時に凍結していたので、
        // 続きから再開するだけで位相は一致する（開始時刻の付け替えは不要）。
        if (drumPausedByFight)
        {
            ResumeDrumLoop();
        }

        // 開始待ち
        if (!drumPlaying && clockTime + DivideEpsilon >= drumStartTime)
        {
            StartDrumLoop();
        }
    }

    /// <summary>
    /// ドラムループを実際に鳴らす【BGM 発行の唯一の出口】。
    /// 再生速度は <see cref="ApplyDrumSpeed"/> がフェーズのテンポから決める。
    /// </summary>
    private void StartDrumLoop()
    {
        SEED.Audio.PlayBgm(drumLoopPath, SEED.Mathf.Max(drumLoopVolume, VolumeMin), loop: true);
        drumPlaying = true;
        ApplyDrumSpeed();
    }

    /// <summary>
    /// ドラムループの再生速度を、いまのフェーズのテンポへ合わせる
    /// 【BGM の再生速度を書く唯一の出口】。
    ///
    /// 速度 ＝ <see cref="CurrentTempoBpm"/> ÷ 素材の BPM。
    /// 隙（Rest）へ入った瞬間に <see cref="restBpm"/> のテンポへ、
    /// 戦闘（余白・出題・回答）へ戻った瞬間に魚の BPM へ切り替わる。
    /// 鳴っていないときは何もしない（<see cref="StartDrumLoop"/> が改めて呼ぶ）。
    /// </summary>
    private void ApplyDrumSpeed()
    {
        if (!drumPlaying) { return; }

        float speed = CurrentTempoBpm() / SEED.Mathf.Max(drumLoopBpm, MinDrumLoopBpm);
        SEED.Audio.SetBgmSpeed(speed);
    }

    /// <summary>
    /// いまのフェーズで刻んでいるテンポ（BPM）【テンポの唯一の問い合わせ点】。
    /// 隙（Rest）だけ <see cref="restBpm"/>、それ以外は魚の BPM。
    /// </summary>
    private float CurrentTempoBpm()
    {
        if (CurrentPhase == Phase.Rest) { return SEED.Mathf.Max(restBpm, MinBpm); }

        return secondsPerBeat > DivideEpsilon ? SecondsPerMinute / secondsPerBeat : MinBpm;
    }

    /// <summary>
    /// ドラムループを一時停止する【ドラム凍結の唯一の出口】。
    ///
    /// <see cref="StopDrumLoop"/> と違い <b>再生位置を保持したまま</b>止めるので、
    /// <see cref="ResumeDrumLoop"/> で続きから鳴らせる。拍時計（<see cref="clockTime"/>）も
    /// 同じ区間だけ止まっているため、再開時にドラムと拍のズレが生じない。
    /// 鳴っていない・既に一時停止しているときは何もしない（毎フレーム呼んでよい）。
    /// </summary>
    private void PauseDrumLoop()
    {
        if (!drumPlaying || drumPausedByFight) { return; }

        SEED.Audio.PauseBgm();
        drumPausedByFight = true;
    }

    /// <summary>
    /// 一時停止していたドラムループを、止めた位置から再開する。
    /// 一時停止していなければ何もしない。
    /// </summary>
    private void ResumeDrumLoop()
    {
        if (!drumPausedByFight) { return; }

        drumPausedByFight = false;
        if (!drumPlaying) { return; }   // 停止を挟んでいた場合の保険（開始待ちへ戻す）

        SEED.Audio.ResumeBgm();
    }

    /// <summary>
    /// ドラムループを止める（鳴っていなければ何もしない）。
    /// 停止した Sink は再開できないので、一時停止中の記録もここで捨てる。
    /// </summary>
    private void StopDrumLoop()
    {
        drumPausedByFight = false;
        if (!drumPlaying) { return; }

        SEED.Audio.StopBgm();
        SEED.Audio.SetBgmSpeed(NormalPlaybackSpeed);   // 速度は BGM をまたいで保持されるため戻す
        drumPlaying = false;
    }

    /// <summary>
    /// フェーズの切り替えと予告を行う【フェーズ遷移の唯一の集約点】。
    /// 切り替えは必ず小節頭（フェーズ長がすべて小節単位なので時刻で判定できる）。
    /// </summary>
    private void UpdatePhaseTransition()
    {
        // 1 拍前の予告（UI のテキスト表示で使う）
        if (!nextPhaseAnnounced && clockTime >= phaseEndTime - PhaseBeatSeconds)
        {
            nextPhaseAnnounced = true;
        }

        if (clockTime < phaseEndTime) { return; }

        // 次のフェーズは PeekNextPhase が決める（走りの予約もここで見る）。
        // 予約は「隙を抜ける瞬間」に必ず消費する ―― チュートリアルの上書き
        // （ResolveNextPhase）で走りが握り潰された場合でも消費するので、
        // ずっと後のフェーズで唐突に走り出すことはない。
        Phase next = PeekNextPhase();
        if (next == Phase.Run) { recoverRunPending = false; }

        // 貯めておいた魚 HP の回復は、隙を抜ける<b>この瞬間</b>にまとめて効かせる。
        // 必ずフェーズを切り替える前に行うこと ―― 走り（Run）は「新しい魚 HP に
        // 対応する目標距離」へ走るので、先に HP が入っていないと走る距離が足りない。
        if (CurrentPhase == Phase.Rest) { CommitPendingFishHpRecovery(); }

        EnterPhase(ResolveNextPhase(next));
    }

    /// <summary>
    /// 次に入るフェーズを<b>消費せずに</b>返す【巡回順＋走りの予約を合成する唯一の場所】。
    ///
    /// 通常は巡回順（<see cref="NextPhase"/>）そのものだが、隙（<see cref="Phase.Rest"/>）を
    /// 抜けるときに走りの予約（<see cref="recoverRunPending"/>）が立っていれば
    /// 走り（<see cref="Phase.Run"/>）を割り込ませる。
    /// 予告テキスト（<see cref="ApplyStatusText"/>）と実際の遷移で同じ答えを使うため、
    /// ここは副作用を持たない問い合わせにしてある。
    /// </summary>
    /// <returns>次に入るフェーズ（チュートリアルの上書きは掛けていない素の値）。</returns>
    private Phase PeekNextPhase()
        => recoverRunPending && CurrentPhase == Phase.Rest && recoverRunBeats > NoRecoverRunBeats
            ? Phase.Run
            : NextPhase(CurrentPhase);

    /// <summary>
    /// 巡回順で決まった次のフェーズへ、チュートリアルのルール上書きを掛ける
    /// 【遷移先の上書きの唯一の場所】。
    ///
    /// 上書きが無ければ（<see cref="TutorialRules.Active"/> が false なら）
    /// 引数をそのまま返すので、本編の巡回は一切変わらない。
    /// </summary>
    /// <param name="natural">巡回順（<see cref="NextPhase"/>）が決めた本来の遷移先。</param>
    /// <returns>実際に入るフェーズ。</returns>
    private Phase ResolveNextPhase(Phase natural)
    {
        if (!TutorialRules.Active) { return natural; }

        // ビート無効: 出題・回答を行わず、ずっと隙（＝巻けるだけ）にする
        if (TutorialRules.BeatDisabled) { return Phase.Rest; }

        // ミス即やり直し: 回答を終えた時点でミスがあれば、隙を挟まず出題へ戻す。
        // やり直しの頭でゲージを満タンへ戻すので、何度でも同じ条件で挑める。
        if (TutorialRules.RestartCycleOnMiss
            && CurrentPhase == Phase.Answer
            && missedThisCycle)
        {
            RestoreLineFull(TutorialRules.GaugeRestoreSeconds);
            return Phase.Call;
        }

        return natural;
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
            EnterPhase(ResolveNextPhase(Phase.Call));
            return;
        }

        float leadInSeconds = beats * secondsPerBeat;

        CurrentPhase = Phase.LeadIn;
        clockTime = -leadInSeconds;
        phaseBarSeconds = SecondsPerBar;   // 余白は魚のテンポで数える
        phaseStartTime = clockTime;
        phaseEndTime = 0f;               // 0 に達した瞬間＝最初の小節頭で Call へ
        phaseBars = MinPhaseBars;        // 小節単位のフェーズではないので参照はされない
        nextPhaseAnnounced = false;
        ClearCallHits();
        lastBeatPlayed = NoBeatPlayed;    // 余白 1 拍目の頭で必ずメトロノームが鳴るようにする

        // 魚が沖へ走り出した（余白フェーズのあいだウキが引き伸ばされる）
        SEED.Events.Raise(FishingEvents.FishRun);

        SEED.Debug.Log($"[Fight] {PhaseLabel(Phase.LeadIn)}（{beats}拍）");
    }

    /// <summary>
    /// フェーズの巡回順（余白 → 出題 → 回答 → 隙 → 出題 …）。
    /// 走り（<see cref="Phase.Run"/>）は巡回順には現れない割り込みなので、
    /// 走り終わりの行き先だけをここに書く（＝余白と同じく出題へ戻る）。
    /// </summary>
    /// <param name="phase">現在のフェーズ。</param>
    private static Phase NextPhase(Phase phase) => phase switch
    {
        Phase.LeadIn => Phase.Call,
        Phase.Run => Phase.Call,
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
        if (next == Phase.Rest)
        {
            lastAnswerPerfect = EvaluateAnswerPerfect();
            // 回答を締めた瞬間に、そのフレーズの評価（Perfect! / Good!）を画面に出す。
            // 出す先は FishingController（文言・色を持つ）。回答フェーズを経ずに
            // 隙へ入る経路（ビート無効のチュートリアル等）はここを通らないので出ない。
            if (CurrentPhase == Phase.Answer)
            {
                FishingController.Current?.ShowFightEvalBanner(lastAnswerPerfect);
                // Perfect のご褒美: Great 1 個ぶん（Inspector で個数調整）の糸を回復する。
                if (lastAnswerPerfect) { RecoverLineForPerfect(); }
            }
        }

        // 隙（スタン）の出入りを通知する【スタン通知の唯一の場所】。
        // 実際の切り替え（CurrentPhase への代入）より前に「抜ける」ほうを流し、
        // フェーズ確定後に「入る」ほうを流すので、購読側から見た順序が入れ替わらない。
        bool leavingRest = CurrentPhase == Phase.Rest && next != Phase.Rest;

        // フェーズの開始時刻は<b>直前のフェーズの終了時刻そのもの</b>【時刻を連結する唯一の場所】。
        // 隙だけテンポ（1 小節の秒数）が変わるため、全フェーズを 1 本の小節グリッドへ
        // 丸めることはもうできない。連結にすれば切れ目は必ず一致し、丸め誤差も入らない。
        float start = CurrentPhase == Phase.None ? 0f : phaseEndTime;

        CurrentPhase = next;

        if (leavingRest)          { SEED.Events.Raise(FishingEvents.StunEnd); }
        if (next == Phase.Rest)   { SEED.Events.Raise(FishingEvents.StunBegin); }

        // 出題の頭でこのサイクルの譜面を確定させる【必ずフェーズ長の算出より前】。
        // 出題・回答の小節数はパターン 1 行の小節数で決まるため、順序を入れ替えてはいけない。
        if (next == Phase.Call) { currentPattern = ResolveCyclePattern(); }

        // 隙だけ隙のBPM で数える（＝1 小節の秒数が変わる）
        phaseBarSeconds = next == Phase.Rest ? RestSecondsPerBar : SecondsPerBar;
        phaseBars = PhaseBarsOf(next);

        // 隙以外のフェーズ中に拾った漂流物「ひるませ」の延長ぶんを、ここで 1 度だけ足し込む
        // （PhaseBarsOf は副作用を持たない問い合わせのままにしておきたいので、消費はこの場所で行う）
        if (next == Phase.Rest && pendingExtraRestBars > 0)
        {
            phaseBars += pendingExtraRestBars;
            pendingExtraRestBars = 0;
        }

        phaseStartTime = start;

        // フェーズの長さ: 走り（Run）だけは小節ではなく<b>拍</b>で数える（余白＝LeadIn と同じ）。
        // 走りは「隙のあとに 1 度だけ挟む短い演出」なので、譜面の小節割りに縛られたくない。
        phaseEndTime = phaseStartTime + (next == Phase.Run
            ? SEED.Mathf.Max(recoverRunBeats, NoRecoverRunBeats) * secondsPerBeat
            : phaseBars * phaseBarSeconds);
        nextPhaseAnnounced = false;
        lastBeatPlayed = NoBeatPlayed;   // 拍はフェーズ内で数え直す（強拍を頭に合わせるため）
        ApplyDrumSpeed();                // 隙の出入りでドラムのテンポを切り替える

        switch (next)
        {
            case Phase.Run:
                // 走りは余白（LeadIn）とまったく同じ扱い。出題打点を捨て、
                // 距離の始点を未確定へ戻してから「魚が沖へ走り出した」ことを流す。
                ClearCallHits();
                runStartDistance = RunStartDistanceUnset;
                SEED.Events.Raise(FishingEvents.FishRun);
                break;

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

        // 走りだけは拍で数えるので、長さの単位もそれに合わせて出す
        string lengthLabel = next == Phase.Run
            ? $"{SEED.Mathf.Max(recoverRunBeats, NoRecoverRunBeats)}拍"
            : $"{phaseBars}小節";

        SEED.Debug.Log($"[Fight] {PhaseLabel(next)}（{lengthLabel}"
                     + $"{(next == Phase.Rest ? (lastAnswerPerfect ? "・完璧" : "・通常") : "")}）"
                     + $" / 糸の残り {Line01:P0}"
                     + $" / 魚HP {FishHp01:P0}");
    }

    /// <summary>
    /// 出題フェーズへ入るときの準備【打点の時刻を確定させる唯一の入口】。
    ///
    /// 1. このサイクルの譜面は <see cref="EnterPhase"/> が既に確定させている
    ///    （<see cref="currentPattern"/>。フェーズ長がその小節数で決まるため）
    /// 2. 出題で鳴らす打点の時刻を<b>解析的に</b>全て確定させる（<see cref="BuildCallHits"/>）
    /// 3. 次に来る回答フェーズの期待打点を先読み生成する
    ///    （回答フェーズ開始を待って生成すると、出題→回答の境界をまたぐ早打ちが
    ///      「出題中のお手つき」として弾かれてしまうため）
    /// 4. アイコンを全て未出現へ戻す
    /// 5. ドラムループを鳴らし直してループ先頭をフェーズ頭へ揃える
    ///
    /// <b>順序の注意</b>: 3 の <see cref="BuildExpectedHits"/> は前サイクルの取りこぼしを
    /// Miss として締める（＝前サイクルのアイコンを判定色にする）ので、
    /// アイコンの作り直し（4）は必ずその<b>後</b>に行うこと。逆にすると新しい譜面の
    /// アイコンが前サイクルの判定色で光ってしまう。
    /// </summary>
    private void EnterCallPhase()
    {
        BuildCallHits();

        int nextAnswerBars = PhaseBarsOf(Phase.Answer);
        BuildExpectedHits(phaseEndTime, nextAnswerBars);

        ResetIcons(callHitTimes.Count);
        missedThisCycle = false;   // 新しい周回の頭でミス記録を畳む
        iconFadeStartTime = NoFadeStart;

        RestartDrumAtCallHead();
    }

    /// <summary>
    /// 出題フェーズで鳴らす打点の時刻を作る【出題打点の唯一の生成点】。
    /// 時刻は「フェーズ開始時刻 ＋ 小節内の位置 × 1 小節の秒数」で、
    /// 以後この値だけを見て音を鳴らす（固定グリッドの添字は持たない）。
    /// </summary>
    private void BuildCallHits()
    {
        ClearCallHits();
        if (currentPattern is not { } pattern) { return; }

        // 打点の位置は「小節を 1.0 とした行頭からの位置」なので、
        // 1 小節の秒数を掛けるだけで実時刻になる（固定グリッドへの量子化は一切しない）。
        foreach (double position in pattern.TriggerPositions)
        {
            callHitTimes.Add(phaseStartTime + (float)(position * phaseBarSeconds));
        }
    }

    /// <summary>出題打点の一覧を空にして、再生位置も未再生へ戻す。</summary>
    private void ClearCallHits()
    {
        callHitTimes.Clear();
        lastFiredCallHit = NoHitFired;
    }

    /// <summary>
    /// 回答フェーズへ入るときのアイコン更新。
    /// 出題で出したアイコンを消さずに残したまま、未判定色へ落として角度を回答フェーズ基準で引き直す。
    ///
    /// <b>ただし既に判定済みの打点は色を書き換えない</b>【判定色を守る唯一の分岐】。
    /// 出題→回答の境界をまたぐ早打ち（例: 回答 1 打目を出題フェーズ中に叩いた）は
    /// 回答フェーズへ入る<b>前</b>に判定色が入るため、ここで一律に未判定色へ落とすと
    /// 「判定は出ているのにアイコンだけ灰色のまま」になってしまう。
    /// 添字は打点の通し番号で <see cref="expectedJudged"/> と一致するので、
    /// 特定の打点（1 打目）に限らずすべての添字で同じ扱いになる。
    /// </summary>
    private void BeginAnswerIcons()
    {
        for (int i = 0; i < iconColors.Count; i++)
        {
            if (IsHitJudged(i)) { continue; }   // 判定済み ＝ 判定色を残す
            iconColors[i] = beatIconPendingColor;
        }
        LayoutIconAngles();
    }

    /// <summary>
    /// 打点 <paramref name="index"/> が既に判定済みかを、範囲外でも安全に答える
    /// 【判定済みフラグを読む唯一の入口】。
    ///
    /// アイコンの枚数（出題打点の数）と期待打点の数は魚のフレーズ次第でずれ得るため、
    /// アイコン側の添字でそのまま参照できるようにここで範囲を吸収する。
    /// </summary>
    /// <param name="index">打点の通し番号。</param>
    /// <returns>判定済みなら true（範囲外は未判定として false）。</returns>
    private bool IsHitJudged(int index)
        => index >= 0 && index < expectedJudged.Count && expectedJudged[index];

    /// <summary>
    /// 直前の回答が<b>完璧</b>だったか【隙の長さを決める唯一の判定点】。
    /// 完璧 ＝ 期待打点が 1 つ以上あり、その<b>すべてが判定済みかつ Excellent</b>であること。
    ///
    /// 受付窓の外のクリックは無反応（判定も Miss も発生しない）仕様なので、
    /// 空打ちの回数は完璧判定に一切影響しない。
    ///
    /// 隙へ入る瞬間に評価するため、受付窓が隙側へはみ出したまま未判定で残っている打点は
    /// 「完璧ではない」と扱う（そのまま叩けば Excellent になり得るが、隙の長さは
    /// 隙の開始時刻に確定させる必要があるため。既定の 2 小節フレーズでは
    /// 最後の打点の窓は小節頭より手前で閉じるので、通常この分岐には入らない）。
    /// </summary>
    private bool EvaluateAnswerPerfect()
    {
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
    /// 隙は長さ（1〜2 小節）だけでなくテンポ（隙のBPM）も変わるため、
    /// 放置するとループ先頭と出題の頭は必ずズレていく。
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
        // 走り（Run）は拍で数えるフェーズなので小節数は使わない
        // （長さの算出は EnterPhase 側。ここは最小値を返すだけ）。
        if (phase == Phase.Run) { return MinPhaseBars; }

        if (phase == Phase.Rest)
        {
            // チュートリアルで「ビートバトルなし」のときは隙から出さない
            // （魚は最初からひるんでいて、巻くだけで釣り上げられる状態になる）
            if (TutorialRules.Active && TutorialRules.BeatDisabled) { return TutorialEndlessRestBars; }

            return SEED.Mathf.Max(lastAnswerPerfect ? restBarsPerfect : restBarsNormal, MinPhaseBars);
        }

        // 出題・回答の長さは「ビートパターン 1 行の小節数」で決まる。
        // 出題と回答が必ず同じ長さになるので、打点アイコンが同じ角度に並ぶ。
        if (currentPattern is { } pattern) { return SEED.Mathf.Max(pattern.Bars, MinPhaseBars); }

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
        Phase.Run => "走り",
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
    /// 以内の早打ちであれば回答フェーズと同じ判定（Excellent/Great/Nice）にする。
    /// どの期待打点の受付窓にも入らないクリックは<b>完全に無視</b>する
    /// （Miss にもせず、効果音も鳴らさず、アイコンも触らない）。
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
        // 受付窓の外のクリックは何もしない（Miss にも効果音にもしない）
        int index = FindNearestPendingHit();
        if (index < 0) { return; }

        HandleTimedClick(index);
    }

    // ─── 内部処理: 回答 ───────────────────────────────────

    /// <summary>
    /// 回答フェーズで期待する打点の一覧を作る【期待打点の唯一の生成点】。
    /// 出題と同じ譜面（<see cref="currentPattern"/>）を回答フェーズの先頭から並べ直す。
    /// 回答が出題より長い場合は譜面を繰り返し、短い場合は入り切る分だけを使う。
    ///
    /// 打点の時刻は出題と同じ式（開始時刻 ＋ 小節内の位置 × 1 小節の秒数）で
    /// <b>解析的に</b>求めるので、出題と回答の小節数が同じなら
    /// 「フェーズ頭からの相対時刻」が完全に一致する
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
        if (currentPattern is not { } pattern || pattern.Bars <= 0) { return; }

        // 回答フェーズは魚のテンポで数える（隙のテンポが混ざらないよう SecondsPerBar を使う）
        float barSeconds = SecondsPerBar;
        int bars = SEED.Mathf.Max(answerBars, MinPhaseBars);

        // 回答が出題より長い場合はパターンを後ろへ繰り返す（短ければ入り切る分だけ使う）
        for (int repeat = 0; repeat * pattern.Bars < bars; repeat++)
        {
            double repeatOffsetBars = (double)repeat * pattern.Bars;

            foreach (double position in pattern.TriggerPositions)
            {
                double placed = repeatOffsetBars + position;
                if (placed >= bars) { continue; }

                expectedTimes.Add(answerStartTime + (float)(placed * barSeconds));
                expectedJudged.Add(false);
                expectedResults.Add(FishingController.HookJudgement.None);
            }
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
    /// 2. どの打点の受付窓（<see cref="niceSeconds"/>）にも入らないクリックは
    ///    <b>完全に無視</b>する（Miss にせず、効果音も鳴らさず、アイコンも触らない）
    /// 3. 受付窓（打点 ＋ <see cref="niceSeconds"/>）を過ぎた打点は Miss として締める
    ///    （＝打ち逃しだけが Miss になる）
    /// </summary>
    private void UpdateAnswer()
    {
        if (ReadTapDown())
        {
            int index = FindNearestPendingHit();
            if (index >= 0)
            {
                HandleTimedClick(index);
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

        using (SEED.Profiler.Scope("Fight/判定の記録"))
        {
            MarkHitResult(index, judgement);
        }

        // 糸の残り: Excellent は減らず、それ以外は「ズレの大きさ × 効き」だけ減る。
        // 魚のレベル・種類・戦闘力による補正は掛けない（全レベル・全魚種で共通の減り）。
        if (judgement != FishingController.HookJudgement.Excellent
            && !(DebugIsolationAllowed && debugNoLineLoss))
        {
            SubtractLine(offset * linePerSecondOfOffset);
        }

        if (!(DebugIsolationAllowed && debugHideJudgementUi))
        {
            using (SEED.Profiler.Scope("Fight/判定UI"))
            {
                FishingController.Current?.ShowFightJudgement(judgement, signedOffset);
            }
        }
    }

    /// <summary>
    /// 受付窓内のクリック 1 回を処理する【出題・回答フェーズ共通のクリックの入口】。
    /// 「効果音 → 判定」の順。デバッグの切り分けスイッチはここと <see cref="JudgeHit"/> で効く。
    /// プロファイラには <c>Fight/クリック</c> 配下として出る。
    /// </summary>
    /// <param name="index">判定対象の打点の添字。</param>
    private void HandleTimedClick(int index)
    {
        using (SEED.Profiler.Scope("Fight/クリック"))
        {
            // 効果音は「判定に結び付いたクリック」だけに鳴らす（空打ちは無反応）
            if (!(DebugIsolationAllowed && debugMuteClickSe))
            {
                using (SEED.Profiler.Scope("Fight/効果音"))
                {
                    PlayAnswerClickSe();
                }
            }
            if (DebugIsolationAllowed && debugSkipJudgement) { return; }
            using (SEED.Profiler.Scope("Fight/判定"))
            {
                JudgeHit(index, clockTime - expectedTimes[index]);
            }
        }
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

        // 1 戦を通した評価を更新する。Excellent 以外が 1 つでも混じったら
        // 二度と true には戻らない（次の BeginFight まで落ちたまま）。
        fightJudgedCount++;
        if (judgement != FishingController.HookJudgement.Excellent) { fightAllExcellent = false; }

        // 【デバッグ】打点アイコンの反応を切り分けたいときはここで抜ける（記録は済んでいる）
        if (DebugIsolationAllowed && debugNoBeatIconFeedback) { return; }

        // 判定した瞬間からポップをやり直す（すでに出ているアイコンが小さく跳ねる）
        // アイコン本体の色は「叩けたか」だけを示す（判定の細かさは外周の判定リングが担う）。
        // 打ち逃し（Miss）はそもそも叩いていないので Miss 色のままにする。
        ShowIconAtHit(
            index,
            clockTime,
            judgement == FishingController.HookJudgement.Miss ? beatIconMissColor : beatIconHitColor);
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
        // この周回でミスが出たことを控える（チュートリアルの再周回判断が読む）
        missedThisCycle = true;

        SubtractLine(SEED.Mathf.Max(missLoss, 0f));
        FishingController.Current?.ShowFightJudgement(FishingController.HookJudgement.Miss, 0f);
    }

    /// <summary>
    /// 糸ゲージの<b>表示用</b>の残量【描画が読む唯一の値】。
    ///
    /// 通常は <see cref="Line01"/> と同値だが、<see cref="RestoreLineFull"/> で満タンへ戻すときだけ
    /// この値がイージングで追いつく（「ぬっ」と伸びる手触りを出すため）。
    /// 実値と表示値を分けておかないと、演出のために実値を書き換えることになり
    /// 判定（糸切れ）と見た目がずれる。
    /// </summary>
    public float DisplayLine01 => gaugeDisplayLine01;

    /// <summary>左クリックの押下をこのフレームに読んだか（叩き入力の唯一の入口）。</summary>
    private static bool ReadTapDown()
        // チュートリアル中はリズム回答のタップを止められる（通常時は常に許可）
        => InputGate.Allows(GameAction.Rhythm)
        && SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left);

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
        if (reelAmount <= ReelInputEpsilon)
        {
            reelInputHeld = false;   // 立ち下がり: 次に巻いたら「巻き始め」として通知する
            return;
        }

        // 見た目の距離を寄せてよいフレーム（ここへ到達＝隙フェーズで有効な巻き入力がある）
        reelEffectiveThisFrame = true;

        // 巻き始めの 1 回だけ通知する（巻いている間ずっと流すと購読側が毎フレーム走るため）。
        // 直前フレームに巻き入力が無かったときだけ「巻き始め」とみなす。
        if (!reelInputHeld)
        {
            reelInputHeld = true;
            SEED.Events.Raise(FishingEvents.Reel);
        }

        // 漂流物の巻き込み判定（ReelingRecently）が読む「最後に巻いた時刻」を控える
        lastReelInputTime = clockTime;
        fishHp = SEED.Mathf.Max(fishHp - reelAmount * ReelHpPerUnit, FishHpZero);
    }

    /// <summary>
    /// 1 フレームの巻き取り量を上限（<see cref="reelInSpeedMax"/> × 経過秒）で頭打ちにし、
    /// 余りを <see cref="pendingReelAmount"/> へ繰り越す【巻き速度を一定に保つ唯一の場所】。
    ///
    /// ホイール入力は 1 フレームにまとめて飛び込むので、そのまま HP へ流すと
    /// 「HP は一気に減ったのにウキ（＝表示距離）は上限速度でしか寄れない」というズレが出る。
    /// ここで入力を「上限速度ぶんずつ」へ均すことで、HP の減り・ウキの実移動・
    /// 距離表示の 3 つが同じ速度で進む。
    /// </summary>
    /// <param name="rawAmount">このフレームに読んだ生の巻き取り量（メートル）。</param>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    /// <returns>このフレームに実際に消化してよい巻き取り量（メートル）。</returns>
    private float ThrottleReelAmount(float rawAmount, float deltaTime)
    {
        float maxPerSecond = SEED.Mathf.Max(reelInSpeedMax, 0f);
        float carryLimit = maxPerSecond * SEED.Mathf.Max(reelCarryOverMaxSeconds, 0f);

        // 生の入力を繰り越しへ積む（貯めすぎると入力を止めても巻け続けるので上限で切る）
        pendingReelAmount = SEED.Mathf.Min(
            pendingReelAmount + SEED.Mathf.Max(rawAmount, 0f), carryLimit);

        float allowed = maxPerSecond * SEED.Mathf.Max(deltaTime, 0f);
        float spend = SEED.Mathf.Min(pendingReelAmount, allowed);
        pendingReelAmount -= spend;
        return spend;
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

        // チュートリアルの練習ミッションでは、糸が 0 になっても切らさない
        // （減っていく見た目は残したまま、失敗で中断されないようにする）。
        if (TutorialRules.Active && TutorialRules.LineBreakDisabled) { return; }

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
    /// 大きさスコア【戦闘力に効く係数の唯一の算出点】。
    ///
    /// 個体差 <see cref="Fish.SizeMultiplier"/>（既定 0.8〜1.3）を
    /// <see cref="sizeScoreMin"/>〜<see cref="sizeScoreMax"/>（既定 0.9〜1.1）へ線形写像し、
    /// さらに魚種ごとの戦闘力係数 <see cref="Fish.PowerScale"/>（無次元・既定 1）を掛ける。
    /// 魚側に同じ式を持たせない（旧 <c>Fish.CombatPower</c> は廃止）。
    /// </summary>
    /// <param name="fish">対象の魚。</param>
    private float SizeScore(Fish fish)
    {
        float t = SEED.Mathf.InverseLerp(sizeMultiplierRefMin, sizeMultiplierRefMax, fish.SizeMultiplier);
        float individual = SEED.Mathf.Lerp(sizeScoreMin, sizeScoreMax, t);
        return individual * SEED.Mathf.Max(fish.PowerScale, 0f);
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
    /// ウキを沖へ引く速度の倍率（魚 ÷ 竿）。上下限でクランプする。
    /// </summary>
    private float PullRateClamped()
    {
        float ratio = CurrentFishPower() / SEED.Mathf.Max(rodPower, DivideEpsilon);
        return SEED.Mathf.Clamped(ratio, pullRateMultiplierMin, pullRateMultiplierMax);
    }

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

    // ─── 内部状態: 糸ゲージの表示演出 ───────────────────────

    /// <summary>表示中の糸残量（0〜1。実値 <see cref="Line01"/> に追従する）。</summary>
    private float gaugeDisplayLine01 = Line01Max;

    /// <summary>復帰イージングの開始時点の表示値。</summary>
    private float gaugeEaseFrom = Line01Max;

    /// <summary>復帰イージングの所要秒（0 以下なら演出せず実値へ即追従）。</summary>
    private float gaugeEaseSeconds;

    /// <summary>復帰イージングの経過秒（実時間）。</summary>
    private float gaugeEaseElapsed;

    /// <summary>
    /// この周回（出題→回答）でミスがあったか。
    /// チュートリアルの「1 周ミスなしで刻む」ミッションが再周回の判断に使う。
    /// </summary>
    private bool missedThisCycle;

    /// <summary>実行時の状態をすべて初期値へ戻す（開始前・終了後の共通処理）。</summary>
    private void ResetRuntimeState()
    {
        // ドラムループ（BGM 枠）はバトル中だけ鳴らすので、終了・糸切れ・釣り上げ・
        // 開始直前のいずれからここへ来ても必ず止める【停止の唯一の出口】。
        StopDrumLoop();
        drumScheduled = false;
        drumPausedByFight = false;
        drumStartTime = 0f;

        Active = false;
        Paused = false;                // 一時停止の持ち越しを防ぐ
        LineBroken = false;
        CurrentPhase = Phase.None;
        Line01 = Line01Max;

        // 表示用のゲージも実値へ揃え、掛かりかけの復帰演出を持ち越さない
        gaugeDisplayLine01 = Line01Max;
        gaugeEaseFrom      = Line01Max;
        gaugeEaseSeconds   = 0f;
        gaugeEaseElapsed   = 0f;
        missedThisCycle    = false;
        fishHp = 0f;
        fishHpMax = 0f;
        metersPerHp = 0f;
        pendingReelAmount = 0f;   // 巻き取りの繰り越しは戦いをまたいで持ち越さない
        leadInStartDistance = 0f;
        target = null;

        clockTime = 0f;
        lastBeatPlayed = NoBeatPlayed;
        phaseStartTime = 0f;
        phaseEndTime = 0f;
        phaseBars = MinPhaseBars;
        nextPhaseAnnounced = false;
        phaseBarSeconds = 0f;
        currentPattern = null;
        fightPattern = null;
        ClearCallHits();
        ClearExpectedHits();

        lastAnswerPerfect = false;
        pendingExtraRestBars = 0;
        recoverRunPending = false;              // 走りの予約は戦いをまたいで持ち越さない
        pendingFishHpRecovery = 0f;             // 貯めたままの回復も持ち越さない（倒し切った直後も含む）
        runStartDistance = RunStartDistanceUnset;
        lastReelInputTime = NoReelInputTime;
        reelInputHeld = false;
        reelEffectiveThisFrame = false;
        ResetIcons(0);
        iconFadeStartTime = NoFadeStart;
    }

    // ─── UI ─────────────────────────────────────────────

    /// <summary>
    /// UI をバトル中の見た目へ更新する。
    /// 糸ゲージ・マーカーは<b>ここでは描かず</b>、表示フラグを立てるだけにして
    /// 実際の描画は <see cref="LateUpdate"/> の <see cref="DrawGauge"/> に任せる
    /// （プリミティブ描画を 1 フレーム 1 回に保つため）。
    /// </summary>
    private void ApplyUi()
    {
        gaugeVisible = true;
        ApplyBeatIcons();
        ApplyStatusText();
    }

    /// <summary>
    /// 糸ゲージ（円）とマーカー（針）をプリミティブで描く
    /// 【ゲージ描画の唯一の出口】。
    ///
    /// ・セグメント … 真上から右回りに <see cref="gaugeSegmentCount"/> 個の小片を並べ、
    ///   <see cref="Line01"/> × 個数ぶんだけ点灯色（満タン緑 → 中間黄 → 危険赤）で、
    ///   残りは空き色で描く。1 個の小片は幅 <see cref="segmentWidthPx"/>（円周方向）×
    ///   高さ <see cref="segmentHeightPx"/>（半径方向）の四角形で、角度ぶん回して置く。
    /// ・マーカー … <see cref="PhaseProgress01"/>（フェーズ内の進行）の角度に置く三角形。
    ///   セグメントより 1 レイヤーだけ手前に出す。
    ///
    /// 座標は <see cref="gaugeSpace"/> のローカル空間（原点＝そのアクタの位置・
    /// 単位＝キャンバス px・Y 下向き）で、そこへ中心のずらしを足したところが円の中心になる。
    /// </summary>
    private void DrawGauge()
    {
        if (!gaugeVisible) { return; }
        if (gaugeSpace is not { IsValid: true } space) { return; }

        SEED.Vector2 center = GaugeCenterPx(space);
        int count = SEED.Mathf.Max(gaugeSegmentCount, MinSegmentCount);

        // 点灯色は糸の残りで決まり、点灯するのは「残り × 個数」より手前のセグメント
        float alpha = SEED.Mathf.Clamped01(segmentOpacity);
        SEED.Color litColor = LineDepletionColor(alpha);
        SEED.Color unlitColor = ToColor(emptyColor, alpha);
        float lit = SEED.Mathf.Clamped01(gaugeDisplayLine01) * count;

        float halfWidth = segmentWidthPx * HalfScale;
        float halfHeight = segmentHeightPx * HalfScale;

        for (int i = 0; i < count; i++)
        {
            float degrees = SegmentDegrees(i, count);
            SEED.Vector2 at = OffsetFrom(center, ArcPoint(degrees));

            // 小片は原点中心の四角形として渡し、SRT（位置＋回転）で円周上へ置く
            SEED.Draw.Rect(
                new SEED.Vector2(-halfWidth, -halfHeight),
                new SEED.Vector2(halfWidth, -halfHeight),
                new SEED.Vector2(halfWidth, halfHeight),
                new SEED.Vector2(-halfWidth, halfHeight),
                new SEED.Transform2D(at, degrees),
                i < lit ? litColor : unlitColor,
                layer: gaugeLayer,
                space: space);
        }

        DrawJudgeRings(space, center);
        DrawMarker(space, center);
    }

    /// <summary>
    /// 判定済みの打点アイコンの外周へ<b>判定色のリング</b>を描く
    /// 【判定色の表示の唯一の出口】。
    ///
    /// アイコン本体は「叩いた＝黄色（<see cref="beatIconHitColor"/>）」で跳ねるだけにして、
    /// Excellent / Great / Nice / Miss の違いはこのリングの色で示す。
    /// リングは本体と同じ拡大（出現ポップ＋叩き拡大）に追従させ、
    /// アイコンのフェード（<see cref="IconFadeAlpha01"/>）も同じように掛ける。
    /// </summary>
    /// <param name="space">描画する座標空間（ゲージと同じ）。</param>
    /// <param name="center">円の中心（<paramref name="space"/> のローカル座標）。</param>
    private void DrawJudgeRings(SEED.CanvasTransform space, SEED.Vector2 center)
    {
        if (!judgeRingEnabled || !Active) { return; }

        float alpha = SEED.Mathf.Clamped01(judgeRingOpacity)
                    * SEED.Mathf.Clamped01(beatIconOpacity)
                    * IconFadeAlpha01();
        if (alpha <= 0f) { return; }

        // リングはアイコン本体の外周に沿う（内半径 ＝ 本体の半径 ＋ 隙間）
        float innerRadius = SEED.Mathf.Max(beatIconSizePx * HalfScale + judgeRingGapPx, 0f);
        float outerRadius = innerRadius + SEED.Mathf.Max(judgeRingThicknessPx, MinStrokeThicknessPx);

        int count = SEED.Mathf.Min(expectedResults.Count, iconDegrees.Count);
        for (int i = 0; i < count; i++)
        {
            if (i >= expectedJudged.Count || !expectedJudged[i]) { continue; }
            if (i >= iconPopStartTimes.Count || iconPopStartTimes[i] >= IconHidden) { continue; }

            // 本体と同じ拡大率（出現ポップ × 叩き拡大）でリングも大きくする
            float scale = IconPopScale01(iconPopStartTimes[i]) * IconHitPopScale(i);
            if (scale <= 0f) { continue; }

            SEED.Draw.Ring(
                OffsetFrom(center, ArcPointAt(iconDegrees[i], beatIconRadiusPx)),
                innerRadius * scale,
                outerRadius * scale,
                ToColor(JudgementIconColor(expectedResults[i]), alpha),
                layer: gaugeLayer + JudgeRingLayerOffset,
                space: space);
        }
    }

    /// <summary>
    /// マーカー（フェーズ内の進行を示す針）を描く。
    /// 角度 ＝ <see cref="PhaseProgress01"/> × 360 度＝ 1 フェーズで 1 周。
    /// 色はフェーズ（出題／回答／それ以外）で決まる。
    /// </summary>
    /// <param name="space">描画する座標空間。</param>
    /// <param name="center">円の中心（<paramref name="space"/> のローカル座標）。</param>
    private void DrawMarker(SEED.CanvasTransform space, SEED.Vector2 center)
    {
        float degrees = PhaseProgress01 * FullCircleDegrees;

        SEED.Vector3 rgb = CurrentPhase switch
        {
            Phase.Call => markerCallColor,
            Phase.Answer => markerAnswerColor,
            _ => markerRestColor,
        };

        SEED.Vector2 at = OffsetFrom(center, ArcPoint(degrees));
        float alpha = SEED.Mathf.Clamped01(gaugeMarkerOpacity);
        int layer = gaugeLayer + MarkerLayerOffset;
        int vertices = SEED.Mathf.Max(markerVertexCount, MinPolygonVertexCount);

        // 拍頭で 1 → 0 へ落ちるパルス値。大きさと発光の両方をこれで駆動する。
        float pulse01 = BeatPulse01();
        float size = SEED.Mathf.Max(markerSizePx, 0f)
                   * SEED.Mathf.Lerp(NormalScale, SEED.Mathf.Max(markerPulseScale, NormalScale), pulse01);

        // 発光: 拍頭だけ一瞬だけ出る円（同じレイヤーの中では先に積んだものが奥になる）
        float glow = SEED.Mathf.Clamped01(markerGlowOpacity) * pulse01;
        if (glow > 0f)
        {
            SEED.Draw.Circle(
                at,
                size * SEED.Mathf.Max(markerGlowRadiusScale, NormalScale),
                ToColor(markerGlowColor, glow * alpha),
                layer: layer,
                space: space);
        }

        // 縁取り: 本体より太さぶん大きい同じ多角形を先に描いて輪郭にする
        float outline = SEED.Mathf.Max(markerOutlineThicknessPx, 0f);
        if (outline > 0f)
        {
            SEED.Draw.RegularPolygon(
                at,
                size + outline,
                vertices,
                ToColor(markerOutlineColor, alpha),
                rotationDegrees: degrees,
                layer: layer,
                space: space);
        }

        // 本体（針）
        SEED.Draw.RegularPolygon(
            at,
            size,
            vertices,
            ToColor(rgb, alpha),
            rotationDegrees: degrees,
            layer: layer,
            space: space);
    }

    /// <summary>
    /// 拍頭からの経過で 1 → 0 へ落ちるパルス値【マーカーの拍演出の唯一の時間源】。
    ///
    /// 拍頭（<see cref="BeatPhase01"/> ＝ 0）で 1 になり、
    /// <see cref="markerPulseSeconds"/> 秒かけて 0 まで落ちる。
    /// バトル中でない・拍長やパルス秒数が 0 のときは 0（＝パルスなし）。
    /// </summary>
    private float BeatPulse01()
    {
        if (!Active) { return 0f; }

        float beatSeconds = PhaseBeatSeconds;
        if (beatSeconds <= DivideEpsilon) { return 0f; }

        float duration = SEED.Mathf.Max(markerPulseSeconds, 0f);
        if (duration <= DivideEpsilon) { return 0f; }

        float sinceBeat = BeatPhase01 * beatSeconds;
        return NormalScale - SEED.Mathf.Clamped01(sinceBeat / duration);
    }

    /// <summary>
    /// ゲージの中心（<paramref name="space"/> のローカル座標）
    /// 【中心算出の唯一の点】。
    ///
    /// エンジンのローカル空間行列（<c>CanvasTransform::to_mesh_mat4</c>）は pivot を
    /// ピクセル値として平行移動へ効かせるため、ローカル原点はアクタ位置から pivot 分ずれる。
    /// pivot を足し戻したうえで、インスペクタのずらしを加える。
    /// </summary>
    /// <param name="space">描画する座標空間。</param>
    private SEED.Vector2 GaugeCenterPx(SEED.CanvasTransform space)
    {
        SEED.Vector2 pivot = space.Pivot;
        return new SEED.Vector2(pivot.x + gaugeCenterOffsetXPx, pivot.y + gaugeCenterOffsetYPx);
    }

    /// <summary>基準点に相対位置を足した点を返す（円周配置の共通処理）。</summary>
    /// <param name="origin">基準点。</param>
    /// <param name="offset">相対位置。</param>
    private static SEED.Vector2 OffsetFrom(SEED.Vector2 origin, SEED.Vector2 offset)
        => new SEED.Vector2(origin.x + offset.x, origin.y + offset.y);

    /// <summary>
    /// 糸ゲージの表示値を実値へ追いつかせる【表示値更新の唯一の実装】。
    /// 復帰イージング中でなければ実値をそのまま写す（＝従来と同じ見た目）。
    /// </summary>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    private void UpdateGaugeDisplay(float unscaledDelta)
    {
        if (gaugeEaseSeconds <= 0f)
        {
            gaugeDisplayLine01 = Line01;
            return;
        }

        gaugeEaseElapsed += SEED.Mathf.Max(unscaledDelta, 0f);

        float progress = Easing.Progress01(gaugeEaseElapsed, gaugeEaseSeconds);
        gaugeDisplayLine01 = SEED.Mathf.LerpUnclamped(gaugeEaseFrom, Line01, Easing.OutBack(progress));

        if (progress >= 1f)
        {
            gaugeEaseSeconds   = 0f;
            gaugeDisplayLine01 = Line01;
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
        float line = SEED.Mathf.Clamped01(gaugeDisplayLine01);
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

    // ─── UI: 打点アイコンのプール ───────────────────────────

    /// <summary>
    /// 打点アイコンを <paramref name="count"/> 枚以上になるまで作り足す
    /// 【アイコン生成の唯一の入口】。
    ///
    /// 既に足りているときは何もしない（毎フレーム呼んでも生成は起きない）。
    /// 生成したアクタは <c>.actor</c> の色（アルファ 0）のまま出てくるので、
    /// 置いた瞬間から <see cref="ApplyBeatIcons"/> が色を書くまで見えない。
    /// </summary>
    /// <param name="count">必要な枚数。</param>
    private void EnsureIconPool(int count)
    {
        int want = SEED.Mathf.Max(count, 0);
        if (iconPool.Count >= want) { return; }
        if (string.IsNullOrEmpty(beatIconActorPath)) { return; }

        // 親が決まらないと 2D アクタとして正しい場所に置けないので生成しない
        if (!TryResolveIconParent(out var parent))
        {
            SEED.Debug.LogWarning($"[FishingFight] 打点アイコンの親アクタが見つかりません: {beatIconParentName}");
            return;
        }

        while (iconPool.Count < want)
        {
            // プール添字ごとに一意な名前（BeatIcon00, BeatIcon01, ...）を付けて、
            // 親（FishingUI 等）の配下に既に同名アクタがあればそれを使い回す。
            // スクリプトのホットリロードで OnStart / プール構築が再実行されても
            // アイコンが二重生成されない（増えると 2D 描画が積み上がって重くなる）。
            iconPool.Add(SpawnOnce.GetOrInstantiate(
                $"{BeatIconActorNamePrefix}{iconPool.Count:00}", beatIconActorPath, parent));
            iconSprites.Add(null);       // ハンドルは反映後のフレームで拾う
            iconTransforms.Add(null);
        }

        // 枚数が変わったので大きさを書き直させる（差分更新の控えを無効化）
        iconSizeAppliedCount = 0;
    }

    /// <summary>
    /// 打点アイコンを生成する親アクタを解決する【親解決の唯一の点】。
    /// インスペクタ指定を最優先し、未設定なら名前で探す。
    /// </summary>
    /// <param name="parent">見つかった親アクタ（見つからないときは無効ハンドル）。</param>
    /// <returns>親が見つかったか。</returns>
    private bool TryResolveIconParent(out SEED.GameObject parent)
    {
        if (beatIconParent is { IsValid: true } bound)
        {
            parent = bound;
            return true;
        }

        // 空文字ならどのアクタとも一致しないので、無効ハンドルが返る
        parent = SEED.GameObject.Find(beatIconParentName);
        return parent.IsValid;
    }

    /// <summary>
    /// まだ取れていないアイコンの Sprite / CanvasTransform ハンドルを拾い直す。
    ///
    /// <c>Instantiate</c> はフレーム末尾にアクタを組み立てるため、生成したフレームでは
    /// <c>GetComponent</c> が null を返す。取れるまで毎フレーム試し、取れたら控える
    /// （既に有効なハンドルは触らないので、通常フレームのコストはほぼ無い）。
    /// </summary>
    private void ResolveIconHandles()
    {
        for (int i = 0; i < iconPool.Count; i++)
        {
            var go = iconPool[i];
            if (!go.IsValid) { continue; }

            if (iconSprites[i] is not { IsValid: true })
            {
                iconSprites[i] = go.GetComponent<SEED.Sprite>();
            }
            if (iconTransforms[i] is not { IsValid: true })
            {
                iconTransforms[i] = go.GetComponent<SEED.CanvasTransform>();
            }
        }
    }

    /// <summary>
    /// プールした打点アイコンをすべて破棄する【破棄の唯一の場所】。
    /// バトル中は絶対に呼ばない（隠すのはアルファ 0 で行う）。
    /// </summary>
    private void DestroyIconPool()
    {
        for (int i = 0; i < iconPool.Count; i++)
        {
            if (iconPool[i].IsValid) { iconPool[i].Destroy(); }
        }
        iconPool.Clear();
        iconSprites.Clear();
        iconTransforms.Clear();
        iconSizeAppliedCount = 0;
    }

    // ─── UI: 打点アイコン ──────────────────────────────────

    /// <summary>
    /// 打点アイコンの状態を、打点の個数ぶんだけ「未出現」で作り直す
    /// 【アイコン状態の唯一の初期化点】。
    /// </summary>
    /// <param name="hitCount">いまのフレーズの打点数。</param>
    private void ResetIcons(int hitCount)
    {
        // 打点数がプールを超えるならここで継ぎ足す（超えない限り生成は起きない）
        EnsureIconPool(hitCount);

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
        // 生成が反映されたアイコンのハンドルを拾い直す（生成直後の 1 フレームは null）
        ResolveIconHandles();

        int slots = iconPool.Count;
        if (slots <= 0) { return; }

        ApplyIconSizes(slots);

        // 回答・隙のあいだも角度はフェーズ基準のまま保つ（隙では回答時の角度を凍結して使う）
        if (CurrentPhase is Phase.Call or Phase.Answer) { LayoutIconAngles(); }

        float fade = IconFadeAlpha01();
        float baseAlpha = SEED.Mathf.Clamped01(beatIconOpacity) * fade;

        for (int i = 0; i < slots; i++)
        {
            bool used = Active && i < iconPopStartTimes.Count && iconPopStartTimes[i] < IconHidden;
            float scale = used ? IconPopScale01(iconPopStartTimes[i]) * IconHitPopScale(i) : 0f;
            float alpha = used ? baseAlpha : 0f;

            if (iconTransforms[i] is { IsValid: true } tf)
            {
                if (used) { tf.Position = ArcPointAt(iconDegrees[i], beatIconRadiusPx); }
                tf.Scale = new SEED.Vector2(scale, scale);
            }

            if (iconSprites[i] is not { IsValid: true } sprite) { continue; }

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
            if (iconSprites[i] is { IsValid: true } sprite)
            {
                sprite.Size = new SEED.Vector2(beatIconSizePx, beatIconSizePx);
            }
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
    /// 叩いた打点に上乗せする拡大率（1 ＝ 上乗せなし）
    /// 【叩いた手応えの唯一の実装】。
    ///
    /// 判定した瞬間（<see cref="MarkHitResult"/> がポップ開始時刻を書き直した時刻）から
    /// <see cref="beatIconHitPopSeconds"/> 秒かけて <see cref="beatIconHitPopScale"/> → 1 へ戻る。
    /// 打ち逃し（Miss）は<b>叩いていない</b>ので跳ねない。
    /// </summary>
    /// <param name="index">打点の通し番号（アイコンの添字と同じ）。</param>
    private float IconHitPopScale(int index)
    {
        if (index < 0 || index >= expectedJudged.Count) { return NormalScale; }
        if (!expectedJudged[index]) { return NormalScale; }
        if (index >= expectedResults.Count) { return NormalScale; }
        if (expectedResults[index] == FishingController.HookJudgement.Miss) { return NormalScale; }
        if (index >= iconPopStartTimes.Count || iconPopStartTimes[index] >= IconHidden) { return NormalScale; }

        float duration = SEED.Mathf.Max(beatIconHitPopSeconds, 0f);
        if (duration <= DivideEpsilon) { return NormalScale; }

        float t = SEED.Mathf.Clamped01((clockTime - iconPopStartTimes[index]) / duration);
        return SEED.Mathf.Lerp(SEED.Mathf.Max(beatIconHitPopScale, NormalScale), NormalScale, t);
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
    /// デバッグ HUD（フェーズ名・次フェーズ予告・魚 HP ％）を出してよいか
    /// 【デバッグ表示の可否の唯一の判断】。
    ///
    /// インスペクタの <see cref="showDebugHud"/> と、ビルド種別による許可
    /// （<see cref="SEED.Application.IsDebugAllowed"/> ＝ パッケージ版だけ false）の
    /// 両方が揃ったときだけ true。パッケージ版では設定に関わらず false になる。
    /// </summary>
    private bool ShowDebugHud => showDebugHud && SEED.Application.IsDebugAllowed;

    /// <summary>
    /// 円の中心テキスト（フェーズ名＋予告／魚 HP ％）を更新する。
    /// 余白（<see cref="Phase.LeadIn"/>）中だけは特別扱いで、残り拍数のカウントダウン
    /// （"4" → "3" → "2" → "1"）だけを大きく出す。
    ///
    /// フェーズ名・予告・魚 HP ％は<b>デバッグ表示</b>なので
    /// <see cref="ShowDebugHud"/> が false（パッケージ版など）ならアルファ 0 で伏せる。
    /// 開始カウントダウンだけは「いつ最初の出題が来るか」を伝えるゲーム UI なので、
    /// デバッグ表示の可否に関わらず常に出す。
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

        // デバッグ表示が許可されていないときは、文字を書き換えずアルファだけ 0 にする
        // （このスクリプトの非表示の流儀は <see cref="HideUi"/> と同じくアルファ 0）。
        if (!ShowDebugHud)
        {
            label.Color = label.Color.WithAlpha(0f);
            return;
        }

        string phaseName = PhaseLabel(CurrentPhase);
        // 予告は実際の遷移先と同じ答え（PeekNextPhase）で出す
        // ＝ 隙の直後に走りが挟まる場合は「→ 走り」と予告される。
        string notice = nextPhaseAnnounced ? $" → {PhaseLabel(PeekNextPhase())}" : string.Empty;

        // 2 行目に魚の HP ％を足す（ここへ来ている時点でデバッグ表示は許可済み）。
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
        => CurrentPhase == Phase.LeadIn
            ? SEED.Mathf.Max(SEED.Mathf.Max(leadInBeats, 0) - BeatIndex, 0)
            : 0;

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

    // ─── UI: 判定表示の収まり ──────────────────────────────

    /// <summary>
    /// 判定画像（Excellent/Great/Nice/Miss）を糸ゲージの矩形へ収める
    /// 【判定表示のはみ出し防止の唯一の実装】。
    ///
    /// 対象アクタのハンドル（Sprite ＋ CanvasTransform）が揃ったフレームに
    /// <b>1 度だけ</b>実行し、以後は何もしない。毎フレーム測り直すと
    /// ポップ演出で一時的に大きくなった値を基準にしてしまい、表示のたびに縮んでいくため。
    /// </summary>
    private void ApplyJudgementFit()
    {
        if (!judgementFitEnabled || judgementFitDone) { return; }

        // 収める矩形の半分の大きさ（＝円の半径 − 余白）。円の外接矩形から余白を引いた形。
        float halfExtent = SEED.Mathf.Max(
            SEED.Mathf.Max(arcRadiusPx, 0f) - SEED.Mathf.Max(judgementPaddingPx, 0f),
            MinFitExtentPx * HalfScale);

        // 矩形の中心はゲージの中心と同じ（判定画像もゲージも画面中央アンカーで置いている）
        var rectCenter = new SEED.Vector2(gaugeCenterOffsetXPx, gaugeCenterOffsetYPx);

        bool allDone = true;
        allDone &= FitJudgementObject(judgementExcellentObject, rectCenter, halfExtent);
        allDone &= FitJudgementObject(judgementGreatObject, rectCenter, halfExtent);
        allDone &= FitJudgementObject(judgementNiceObject, rectCenter, halfExtent);
        allDone &= FitJudgementObject(judgementMissObject, rectCenter, halfExtent);

        judgementFitDone = allDone;
    }

    /// <summary>
    /// 判定画像 1 枚を矩形へ収める（拡大率を縮め、はみ出す位置を矩形内へ寄せる）。
    /// </summary>
    /// <param name="target">判定画像のアクタ（未設定なら「処理済み」として扱う）。</param>
    /// <param name="rectCenter">矩形の中心（キャンバス px・判定画像と同じ座標系）。</param>
    /// <param name="halfExtent">矩形の一辺の半分（px）。</param>
    /// <returns>この 1 枚の処理が確定したか（false ＝ ハンドル待ちで次フレーム再挑戦）。</returns>
    private bool FitJudgementObject(SEED.GameObject? target, SEED.Vector2 rectCenter, float halfExtent)
    {
        // 未設定は「調整しない」意思表示なので、待たずに確定扱いにする
        if (target is not { IsValid: true } go) { return true; }

        if (go.GetComponent<SEED.Sprite>() is not { IsValid: true } sprite) { return false; }
        if (go.GetComponent<SEED.CanvasTransform>() is not { IsValid: true } tf) { return false; }

        SEED.Vector2 size = sprite.Size;
        if (size.x <= DivideEpsilon || size.y <= DivideEpsilon) { return false; }

        // ポップで一瞬大きくなる分を見込んだ大きさで判定する
        float allowance = SEED.Mathf.Max(judgementPopAllowance, NormalScale);
        float limit = halfExtent * FullFromHalfScale;
        float scale = SEED.Mathf.Min(
            NormalScale,
            SEED.Mathf.Min(limit / (size.x * allowance), limit / (size.y * allowance)));

        tf.Scale = new SEED.Vector2(scale, scale);

        // 位置も矩形の内側へ寄せる（中心置きなら動かないが、ずらして置いた場合の保険）
        float halfWidth = size.x * allowance * scale * HalfScale;
        float halfHeight = size.y * allowance * scale * HalfScale;
        float limitX = SEED.Mathf.Max(halfExtent - halfWidth, 0f);
        float limitY = SEED.Mathf.Max(halfExtent - halfHeight, 0f);

        SEED.Vector2 position = tf.Position;
        tf.Position = new SEED.Vector2(
            SEED.Mathf.Clamped(position.x, rectCenter.x - limitX, rectCenter.x + limitX),
            SEED.Mathf.Clamped(position.y, rectCenter.y - limitY, rectCenter.y + limitY));

        return true;
    }

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

    /// <summary>次の描画で打点アイコンの大きさを必ず書き直させる。</summary>
    private void InvalidateIconCache()
    {
        iconSizeAppliedCount = 0;
    }

    /// <summary>UI をすべて隠す【非表示の唯一の出口】。</summary>
    private void HideUi()
    {
        // 糸ゲージ・マーカーはプリミティブなので「描くのをやめる」だけで消える
        gaugeVisible = false;

        for (int i = 0; i < iconSprites.Count; i++)
        {
            ApplySpriteOpacity(iconSprites[i], 0f);
        }

        if (hpText is { } hp && hp.IsValid) { hp.Color = hp.Color.WithAlpha(0f); }
        if (distanceText is { } dist && dist.IsValid) { dist.Color = dist.Color.WithAlpha(0f); }

        // 次に表示するときは必ず大きさを書き直す（枚数が変わっている可能性があるため）
        InvalidateIconCache();
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
