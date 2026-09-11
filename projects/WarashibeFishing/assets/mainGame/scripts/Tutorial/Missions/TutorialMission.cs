// ============================================================================
//  TutorialMission.cs
//  ミッション 1 件ぶんのデータ定義（データドリブンの最小単位）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// チュートリアルのミッション 1 件ぶんのデータ。
///
/// 【責務】
/// 「どんな種類のミッションで」「何という名前で」「何を目的とし」
/// 「その間どのルールを上書きし」「どの操作を許すか」という
/// <b>1 件ぶんの設定値だけ</b>を持つ。判定も表示も進行も持たない
/// （判定は <see cref="IMission"/> の実装、表示は <see cref="MissionPanel"/>、
///  進行は <see cref="TutorialDirector"/> の責務）。
///
/// 【説明台詞はここに無い】
/// 台詞は <see cref="TutorialDialogue"/> の別リストに置き、<see cref="id"/> で結びつける。
/// 構造体の入れ子は 1 段までというインスペクタの制限があり、
/// 「1 ミッションに複数枚の説明」を構造体の中のリストでは表現できないため
/// （TextArea 属性はリストの要素に効かず、複数行の台詞が書けなくなる）。
///
/// 【締め演出(Cutscene)の調整】
/// 種類が Cutscene のミッションでは、怪獣の跳ね方とカメラの扱いを「演出:〜」の
/// 各項目で調整できる。<b>既定は「カメラは通常の追従のまま、画面の奥（海の上・
/// 少し右寄り）で怪獣が跳ぶ」</b>形になる。
/// <list type="bullet">
///   <item>カメラ制御 … FollowNormal（既定・カメラに触らない）/ Orbit（距離・高さ・
///         方位角で回り込む）/ TargetActor（カメラ目標アクタへ寄る）</item>
///   <item>出現位置の基準 … CameraView（既定・通常カメラの視界の奥。カメラからの距離／
///         横ずれ／跳ぶ方向で置く）/ TargetOrFloat（目標アクタ、無ければウキの沖側）</item>
///   <item>怪獣の動き … 跳ねる高さ / 助走の水平距離 / 待機の深さ / 向きの補正 / 着水までの回転量</item>
///   <item>カメラ（制御が FollowNormal 以外のときだけ有効）… 距離 / 高さ /
///         方位角（0＝正面・90＝真横・180＝背後）/ 注視点の高さ / 補間の速さ /
///         画角（0 で変更しない）/ カメラ目標アクタ</item>
/// </list>
/// 演出の秒数は「数値パラメータ」、怪獣の .actor は「文字列パラメータ」で指定する
/// （従来どおり）。「目標アクタ」は出現位置の基準が TargetOrFloat のときの跳ねる位置になる。
/// 値の読み出しと既定値の補正は <see cref="CutsceneSettings"/> が一手に引き受ける。
///
/// 【使い方（シーン側）】
/// TutorialDirector の「ミッション」リストへ 1 件ずつ追加し、
/// 「台詞」リストへ同じ <see cref="id"/> の説明を必要な枚数だけ足す。
/// </summary>
[System.Serializable]
public struct TutorialMission
{
    // ─── 既定値の与え方 ─────────────────────────────────────

    /// <summary>
    /// 何も指定しないミッション 1 件を作る（フィールド初期化子の既定値がそのまま入る）。
    ///
    /// 【なぜ空のコンストラクタが要るか】
    /// C# では<b>構造体にフィールド初期化子を書くにはパラメータ無しコンストラクタの
    /// 明示宣言が必要</b>で、これが無いと下の「= 既定値」が書けない。
    /// エンジンのインスペクタ／実行時注入（SEED.ScriptStructArray）は
    /// <c>Activator.CreateInstance</c> でこの型の見本を作り、そこから
    /// 「各メンバの既定値」を読む。つまりここで初期化した値が
    /// <b>インスペクタの初期表示と、シーンに保存値が無いメンバの実行時の値</b>になる。
    /// （<c>default(TutorialMission)</c> はコンストラクタを通らず全 0 になるので、
    ///   ミッションデータを自前で作る場合は必ず <c>new TutorialMission()</c> を使うこと。）
    ///
    /// 未代入のフィールドは C# 11 以降の規則で自動的に既定値（0 / false / null）になる。
    /// </summary>
    public TutorialMission() { }

    // ─── 識別と種類 ─────────────────────────────────────────

    /// <summary>
    /// ミッションの識別子（例 t1_move）。
    /// <see cref="TutorialDialogue.missionId"/> と突き合わせて台詞を引くキーになる。
    /// 重複するとどのミッションの台詞か決まらないので、リスト内で一意にすること。
    /// </summary>
    [SerializeField(Label = "ID", Tooltip = "台詞と結びつけるキー。リスト内で一意にすること")]
    public string id;

    /// <summary>ミッションの種類（どの判定クラスを使うか）。</summary>
    [SerializeField(Label = "種類", Tooltip = "どの判定を使うか。種類ごとに専用の判定クラスが走る")]
    public MissionKind kind;

    // ─── パネルの表示内容 ───────────────────────────────────

    /// <summary>パネル上段に出すミッション名（例「海辺へ行こう」）。</summary>
    [SerializeField(Label = "ミッション名", Tooltip = "ミッションパネルの見出しに出す名前")]
    public string title;

    /// <summary>パネル中段に出す目的文（例「桟橋のそばまで歩く」）。</summary>
    [SerializeField(Label = "目的", Tooltip = "何をすればよいかの一文")]
    public string objective;

    /// <summary>
    /// true でクリアバナーを出さずに達成後の台詞へ直行する。
    /// 締めの演出など「ミッションクリア！」の帯が場違いになる場面で使う。
    /// </summary>
    [SerializeField(Label = "クリアバナー無し", Tooltip = "true で達成バナーを出さず、そのまま次の台詞へ進む")]
    public bool skipClearBanner;

    /// <summary>
    /// 達成バナーを出しているあいだゲーム時間を止めるか。
    /// 釣りの最中に達成するミッション（魚が泳いで逃げる）だけ true にする。
    /// 止める必要が無い場面で止めると波まで固まって「フリーズした」ように見える。
    /// </summary>
    [SerializeField(Label = "達成中は時間停止", Tooltip = "true で達成バナーの間ゲーム時間を止める（釣りの最中のミッション用）")]
    public bool pauseOnClear;

    /// <summary>
    /// true の場合、このミッションの台本（<see cref="TutorialDirector.StartCurrentMission"/> が
    /// 行うルール上書き・入力許可・判定クラスの Begin 一式）の適用を、
    /// 開始前の説明（Intro）を読み終えるまで遅らせる。
    ///
    /// 【何のためにあるか】
    /// 既定では台本は説明より<b>先に</b>適用される（移動ミッションが目印を説明時点で
    /// 見せるための仕様）。しかし合わせ・ビート・巻きなど「台本がすぐにアタリや
    /// 出題を仕込む」種類のミッションでは、説明を読んでいる最中に台本が進んでしまい、
    /// 読み終える前に魚が食いついてしまう事故が起きる。true にすると、台本の適用が
    /// <see cref="TutorialDirector.OnIntroFinished"/> まで丸ごと遅れるので、
    /// 読み終えるまでは何も仕込まれていない安全な状態になる。
    ///
    /// 目印を先に見せたい移動系のミッションでは false のままにする。
    /// </summary>
    [SerializeField(Label = "台本を説明後に開始", Tooltip = "true で台本の適用(ルール上書き・魚の仕込み等)をIntro終了後に遅らせる")]
    public bool scriptAfterIntro;

    // ─── ルール上書き（釣りシステムへの例外規則）───────────

    /// <summary>
    /// 出現させる魚のレベル（1 始まり）。0 で制限なし。
    /// 指定すると、そのレベル以外の魚は補充されなくなる。
    /// </summary>
    [Header("ルール上書き"), SerializeField(Label = "魚レベル制限", Tooltip = "このレベルの魚だけ出す。0 で制限なし")]
    public int fishLevelFilter;

    /// <summary>
    /// 出現させたい魚の .actor パスに含まれる文字列（例 kumanomi）。
    /// 空なら制限なし。一致する魚がそのレベルに居なければ通常の抽選になる。
    /// </summary>
    [SerializeField(Label = "魚種の指定", Tooltip = "必ず出したい魚の .actor 名の一部（例 kumanomi）。空で制限なし")]
    public string fishPrefabFilter;

    /// <summary>true で「魚種の指定」に一致しない魚だけにする（許可リスト扱い）。</summary>
    [SerializeField(Label = "魚種を固定", Tooltip = "true で「魚種の指定」に一致しない魚を一切出さず、泳いでいる個体も取り除く")]
    public bool fishPrefabExclusive;

    /// <summary>
    /// <b>最低 1 匹は海に居させたい</b>魚の .actor パスに含まれる文字列（例 kumanomi）。空なら指定なし。
    ///
    /// 【<see cref="fishPrefabFilter"/> との違い】
    /// <list type="bullet">
    ///   <item><see cref="fishPrefabFilter"/> は「出す魚を<b>その 1 種に固定</b>する」指定
    ///         （抽選より優先されるので、補充される魚が全部その種類になる）。</item>
    ///   <item>こちらは「<b>ほかの魚種は普通に抽選しつつ</b>、その魚種が 1 匹も居なければ
    ///         優先的に 1 匹だけ補充する」指定。釣り上げて居なくなればまた 1 匹補充される。</item>
    /// </list>
    /// 「〇〇を釣ろう」という狙い撃ちのミッションで、海の顔ぶれはランダムのまま
    /// <b>目当ての魚が必ず 1 匹は居る</b>状態を保証するために使う。
    /// 指定した魚がそのレベルの候補（FishLevelEntry の魚 prefab リスト）に無ければ何も起きない。
    /// </summary>
    [SerializeField(Label = "必ず含める魚種", Tooltip = "最低 1 匹は居させたい魚の .actor 名の一部（例 kumanomi）。空で指定なし")]
    public string fishPrefabRequired;

    /// <summary>
    /// 「必ず含める魚種」を何匹まで維持するか（0 以下は 1 扱い）。
    /// 1 だと 10 匹中 1 匹しか目当ての魚が居らず、しかも遠くの仮想個体になりやすく
    /// 「全然出てこない」体感になる。狙い撃ちのミッションでは 3〜4 程度を推奨。
    /// </summary>
    [SerializeField(Label = "必ず含める魚種の匹数", Tooltip = "必ず含める魚種を何匹まで維持するか（0 以下は 1）")]
    public int fishPrefabRequiredCount;

    /// <summary>
    /// このミッションの間、対象レベルの自然出現の維持数をこの値へ強制的に置き換える。
    /// 0（既定）は「上書きしない＝<see cref="FishLevelEntry.maintainCount"/> のまま」を表す
    /// （<see cref="TutorialRules.NoPopulationOverride"/> と同じ約束）。
    ///
    /// 「魚種を固定」で許可リストにした魚が何匹も泳いでいると、狙わせたい 1 匹以外にも
    /// 目移りしてしまう。個体数そのものを絞りたい説明ミッション向けの上書き。
    /// 効くのは「魚レベル制限」の対象レベル、または台本（<see cref="FishManager.SetScriptedSpawn"/>）
    /// が固定しているレベルだけで、それ以外のレベルには影響しない。
    /// </summary>
    [SerializeField(Label = "維持数の上書き(0で無指定)", Tooltip = "0 以外を指定すると、このミッションの間だけ対象レベルの自然出現数をこの値に固定する")]
    public int fishPopulationOverride;

    /// <summary>
    /// true の間、魚の食いつき（ルアーへのアタリ）の進行を一時停止する
    /// （<see cref="TutorialRules.BiteSuppressed"/> をミッション本編中も適用する）。
    ///
    /// 既定では読む側（<see cref="TutorialDirector.SetBiteSuppressed"/>）は説明・クリアバナー・
    /// Outro のあいだだけ抑止し、ミッション本編（Playing）に入ると必ず解除する。しかし
    /// 「まず海を空にして、次のミッションで狙った魚だけを仕込みたい」場面（例: 投げの
    /// 練習中は誰も食いつかせたくない）では、本編中も抑止を続けたい。true にすると
    /// Playing 中もこのフラグが立ったままになり、次のミッションへ切り替わるときに
    /// （<see cref="TutorialDirector.ApplyMissionRules"/> と同じタイミングで）自動的に
    /// そのミッションの設定へ差し替わる。
    /// </summary>
    [SerializeField(Label = "食いつきを抑止", Tooltip = "true でミッション本編中も魚の食いつきの進行を止める（海を空にしてから次で仕込みたい場面用）")]
    public bool suppressBite;

    /// <summary>true の間、わらしべ連鎖（他の魚が掛かった魚を食う）を成立させない。</summary>
    [SerializeField(Label = "連鎖なし", Tooltip = "true で掛かった魚を他の魚が食う連鎖を起こさない")]
    public bool chainDisabled;

    /// <summary>
    /// このミッションの間に許す<b>わらしべ連鎖の回数の上限</b>。
    /// <see cref="TutorialRules.NoChainLimit"/>（0・既定）で無制限。
    ///
    /// 【何のためにあるか】
    /// 「魚で魚を釣る」を 1 回だけ体験させたいミッション（t2_chain）では、上限が無いと
    /// 1 回連鎖したあとも大物が寄り続けて 2 段目・3 段目の連鎖が起きてしまい、
    /// 達成演出の最中に状況がどんどん変わってしまう。上限に達した時点で
    /// <see cref="TutorialRules.ChainDisabled"/> による抑止を掛け、そのミッションの間は
    /// もう連鎖させない（回数を数えて抑止を掛けるのは <see cref="ChainCatchMission"/> の責務）。
    /// </summary>
    [SerializeField(Label = "連鎖回数の上限(0で無制限)", Tooltip = "この回数だけ連鎖したら以降の連鎖を止める。0 で無制限")]
    public int chainLimit;

    /// <summary>true の間、漂流物を自然出現させない。</summary>
    [SerializeField(Label = "漂流物なし", Tooltip = "true で漂流物を自然出現させない（台本での生成は別）")]
    public bool driftDisabled;

    /// <summary>true の間、漂流物を漂わせず寿命でも消さない（台本が並べた位置に留める）。</summary>
    [SerializeField(Label = "漂流物を固定", Tooltip = "true で漂流物が流れず寿命でも消えない（一直線に並べる台本用）")]
    public bool driftStationary;

    /// <summary>
    /// このミッションの間だけ、漂流物の巻き込み判定の半径（メートル）をこの値へ置き換える。
    /// 0（既定）は「上書きしない＝prefab の当たり半径のまま」を表す
    /// （<see cref="TutorialRules.NoDriftPickupRadiusOverride"/> と同じ約束）。
    ///
    /// 台本が一直線上に並べた漂流物を「巻けば必ず拾える」ことにしたい説明ミッション用。
    /// </summary>
    [SerializeField(Label = "漂流物の拾い半径(0で無指定)", Tooltip = "0 以外を指定すると、このミッションの間だけ漂流物の巻き込み半径をこのメートル数にする")]
    public float driftPickupRadius;

    /// <summary>true でビートバトルを行わず、魚をずっとひるませたままにする。</summary>
    [SerializeField(Label = "ビートなし", Tooltip = "true で出題・回答を行わず、魚をずっとひるませる（巻くだけ）")]
    public bool beatDisabled;

    /// <summary>true で糸の残りが 0 になっても切れない。</summary>
    [SerializeField(Label = "糸切れ無効", Tooltip = "true で糸が 0 になっても切れない（減りはする）")]
    public bool lineBreakDisabled;

    /// <summary>true でビートをミスしたら隙を挟まず即もう一周する。</summary>
    [SerializeField(Label = "ミスで即やり直し", Tooltip = "true でミスしたら隙を挟まず出題からやり直す")]
    public bool restartCycleOnMiss;

    /// <summary>true で合わせをミスしても魚を逃がさず前アタリからやり直す。</summary>
    [SerializeField(Label = "合わせミスで再アタリ", Tooltip = "true で合わせを外しても魚が必ずウキへ戻ってくる")]
    public bool rebiteAfterHookMiss;

    /// <summary>true で糸が切れても掛かった状態のままやり取りを仕切り直す。</summary>
    [SerializeField(Label = "糸切れで仕切り直し", Tooltip = "true で糸が切れても投げ直しに戻らず、掛かった続きから再開する")]
    public bool restartFightOnLineBreak;

    // ─── 許可する操作 ───────────────────────────────────────

    /// <summary>移動（W / S）を許可するか。</summary>
    [Header("許可する操作"), SerializeField(Label = "移動", Tooltip = "W / S での移動を許可する")]
    public bool allowMove;

    /// <summary>構え（左クリック押下・保持）を許可するか。</summary>
    [SerializeField(Label = "構え", Tooltip = "左クリックで竿を構える操作を許可する")]
    public bool allowReady;

    /// <summary>構え中の左右の狙い（A / D）を許可するか。</summary>
    [SerializeField(Label = "狙い", Tooltip = "構え中に A / D で向きを変える操作を許可する")]
    public bool allowAim;

    /// <summary>振りかぶり・振り抜き（マウスの振り）を許可するか。</summary>
    [SerializeField(Label = "投げ", Tooltip = "マウスを左から右へ振る投げの操作を許可する")]
    public bool allowCast;

    /// <summary>巻き取り（ホイール）と操舵（A / D）を許可するか。</summary>
    [SerializeField(Label = "巻き", Tooltip = "ホイールでの巻き取りと A / D の操舵を許可する")]
    public bool allowReel;

    /// <summary>竿を振る（合わせ・空振り）を許可するか。</summary>
    [SerializeField(Label = "合わせ", Tooltip = "左クリックで竿を振る（合わせ）操作を許可する")]
    public bool allowHook;

    /// <summary>やり取り中のリズム回答タップを許可するか。</summary>
    [SerializeField(Label = "リズム", Tooltip = "やり取り中の左クリック（リズム回答）を許可する")]
    public bool allowRhythm;

    /// <summary>釣果表示など UI の決定入力を許可するか。</summary>
    [SerializeField(Label = "UI決定", Tooltip = "釣果表示を閉じるなど UI の決定入力を許可する")]
    public bool allowUiConfirm;

    // ─── ミッション固有のパラメータ ─────────────────────────

    /// <summary>
    /// 目標地点（Move ミッションが到達判定に使う空アクタ）。
    /// 締めの演出（Cutscene）では、怪獣が跳ねる位置の基準としても使う。
    /// </summary>
    [Header("パラメータ"), SerializeField(Label = "目標アクタ", Tooltip = "Move の目的地 / Cutscene の演出位置")]
    public SEED.Transform targetActor;

    /// <summary>
    /// 文字列パラメータ。
    /// Cutscene では出す .actor パス、その他は種類ごとの補助指定に使う。
    /// </summary>
    [SerializeField(Label = "文字列パラメータ", Tooltip = "Cutscene の .actor パスなど、種類ごとの補助指定")]
    public string paramText;

    /// <summary>
    /// 数値パラメータ。
    /// Move では到達半径（メートル）、Cutscene では演出の秒数など。
    /// </summary>
    [SerializeField(Label = "数値パラメータ", Tooltip = "Move の到達半径(m) / Cutscene の演出秒数など")]
    public float paramValue;

    /// <summary>
    /// 個数パラメータ。
    /// DriftPickup では拾う個数、ChainCatch では連鎖の回数、CatchTarget では釣る匹数。
    /// 0 以下なら各ミッションの既定値が使われる。
    /// </summary>
    [SerializeField(Label = "個数パラメータ", Tooltip = "拾う個数 / 連鎖回数 / 釣る匹数。0 以下で既定値")]
    public int paramCount;

    // ─── 締め演出(Cutscene)の調整値 ─────────────────────────
    //
    // 【なぜ入れ子の構造体にまとめていないか】
    // このミッションデータは TutorialDirector の「ミッション」リスト
    // （List<TutorialMission>）の要素としてインスペクタに並ぶ。構造体配列の要素は
    // エンジン側（SEED.ScriptStructArray）が「メンバはスカラ／参照／1 段の配列だけ」と
    // 決めており、入れ子の構造体メンバが 1 つでもあると配列フィールド全体が
    // 非対応になってしまう（ミッション一覧がインスペクタから消え、実行時にも
    // シーンの保存値が流し込まれなくなる）。そのため平坦なメンバとして並べ、
    // 読み出しと既定値の補正は CutsceneSettings.FromMission が引き受ける。
    //
    // 既定値は下のフィールド初期化子が持つ（＝インスペクタにも実行時にもこの値が出る）。
    // 加えて CutsceneSettings 側で「0 以下なら既定値」の補正も掛かるため、
    // 保存値が無いミッションでも従来どおりの演出になる。

    /// <summary>
    /// 【Cutscene】カメラ制御（誰がカメラを動かすか）。
    /// 既定の <see cref="CutsceneCameraMode.FollowNormal"/> では演出はカメラに一切触らず、
    /// 移動中と同じ通常の追従（CameraMove）のまま画面の奥で怪獣が跳ねる。
    /// 「演出:カメラ距離/高さ/方位角/注視点/補間の速さ/画角/カメラ目標アクタ」は
    /// この項目が FollowNormal 以外のときだけ効く。
    /// </summary>
    [SerializeField(Label = "演出:カメラ制御", Tooltip = "FollowNormal=通常の追従のまま(カメラに触らない) / Orbit=距離・高さ・方位角で回り込む / TargetActor=カメラ目標アクタへ寄る")]
    public CutsceneCameraMode cutsceneCameraMode = CutsceneSettings.DefaultCameraMode;

    /// <summary>
    /// 【Cutscene】出現位置の基準（怪獣をどこへ出すか）。
    /// 既定の <see cref="CutsceneSpawnAnchor.CameraView"/> は通常カメラの視界の奥（海の上）へ出す。
    /// <see cref="CutsceneSpawnAnchor.TargetOrFloat"/> は目標アクタ、無ければウキの沖側を使う。
    /// </summary>
    [SerializeField(Label = "演出:出現位置の基準", Tooltip = "CameraView=通常カメラの視界の奥へ出す / TargetOrFloat=目標アクタ、無ければウキの沖側")]
    public CutsceneSpawnAnchor cutsceneSpawnAnchor = CutsceneSettings.DefaultSpawnAnchor;

    /// <summary>
    /// 【Cutscene】視界基準のとき、カメラの水平前方向へ何メートル先に出すか。
    /// 0 以下なら <see cref="CutsceneSettings.DefaultCameraViewDistance"/> が使われる。
    /// </summary>
    [SerializeField(Label = "演出:カメラからの距離(m)", Tooltip = "出現位置の基準が CameraView のとき、カメラの前方向へ何 m 先に出すか。0 以下で既定値")]
    public float cutsceneCameraViewDistance = CutsceneSettings.DefaultCameraViewDistance;

    /// <summary>
    /// 【Cutscene】視界基準のとき、カメラの真横へ何メートルずらすか（正で画面右・負で画面左）。
    /// 0 で画面中央。
    /// </summary>
    [SerializeField(Label = "演出:横ずれ(m)", Tooltip = "出現位置の基準が CameraView のとき、カメラの真横へ何 m ずらすか。正で画面右・負で画面左")]
    public float cutsceneCameraViewLateral = CutsceneSettings.DefaultCameraViewLateral;

    /// <summary>
    /// 【Cutscene】跳ぶ方向（度）。視界基準のとき、<b>画面右方向</b>を 0 度として水平に回した向きへ跳ぶ。
    /// 0 で画面を左から右へ横切り、90 でカメラへ向かってきて、−90 で遠ざかる。
    /// </summary>
    [SerializeField(Label = "演出:跳ぶ方向(度)", Tooltip = "0=画面左から右へ横切る / 90=カメラへ向かってくる / -90=遠ざかる（CameraView のときだけ有効）")]
    public float cutsceneJumpDirectionDegrees = CutsceneSettings.DefaultJumpDirectionDegrees;

    /// <summary>
    /// 【Cutscene】怪獣が跳ね上がる高さ（メートル。水面から頂点まで）。
    /// 0 以下なら <see cref="CutsceneSettings.DefaultJumpApexHeight"/> が使われる。
    /// </summary>
    [SerializeField(Label = "演出:跳ねる高さ(m)", Tooltip = "怪獣が水面から跳ね上がる頂点の高さ。0 以下で既定値")]
    public float cutsceneJumpApexHeight = CutsceneSettings.DefaultJumpApexHeight;

    /// <summary>
    /// 【Cutscene】怪獣が助走で進む水平距離（メートル。開始地点から着水地点まで）。
    /// 0 以下なら <see cref="CutsceneSettings.DefaultJumpTravelDistance"/> が使われる。
    /// </summary>
    [SerializeField(Label = "演出:助走の水平距離(m)", Tooltip = "跳び始めから着水までに進む水平距離。0 以下で既定値")]
    public float cutsceneJumpTravelDistance = CutsceneSettings.DefaultJumpTravelDistance;

    /// <summary>
    /// 【Cutscene】跳ぶ前に怪獣を沈めておく深さ（メートル・<b>正値で指定</b>し、内部で下向きに使う）。
    /// 0 以下なら <see cref="CutsceneSettings.DefaultSubmergedDepth"/> が使われる。
    /// </summary>
    [SerializeField(Label = "演出:待機の深さ(m)", Tooltip = "跳ぶ前に怪獣を沈めておく深さ。正の値で指定する。0 以下で既定値")]
    public float cutsceneSubmergedDepth = CutsceneSettings.DefaultSubmergedDepth;

    /// <summary>
    /// 【Cutscene】怪獣モデルの向きの補正角（度）。進行方向へ向けた上からさらに水平に回す。
    /// モデルの前方向が +Z でない .actor を使うときにここで直す。0 で補正なし。
    /// </summary>
    [SerializeField(Label = "演出:向きの補正(度)", Tooltip = "進行方向を向けたあと、さらに水平に回す角度。モデルの前方向のズレを直す用。0 で補正なし")]
    public float cutsceneYawOffsetDegrees = CutsceneSettings.DefaultYawOffsetDegrees;

    /// <summary>
    /// 【Cutscene】着水までに掛けるピッチ回転量（度）。
    /// 跳び始めで −半分（頭を上げる）、頂点で 0、着水で +半分（頭を下げる）になる。
    /// 既定の 90 は従来の実装（±45 度）と同じ。0 で回転させない、負値で逆回り。
    /// </summary>
    [SerializeField(Label = "演出:着水までの回転量(度)", Tooltip = "跳び始め〜着水で頭を上げ下げする角度の合計。既定 90（±45）。0 で回転なし")]
    public float cutscenePitchSweepDegrees = CutsceneSettings.DefaultPitchSweepDegrees;

    /// <summary>
    /// 【Cutscene】カメラが怪獣から離れて構える距離（メートル）。
    /// 0 以下なら <see cref="CutsceneSettings.DefaultCameraDistance"/> が使われる。
    /// 「演出:カメラ目標アクタ」を設定した場合は使われない。
    /// </summary>
    [SerializeField(Label = "演出:カメラ距離(m)", Tooltip = "カメラが怪獣から離れて構える距離。0 以下で既定値。カメラ目標アクタ指定時は無視")]
    public float cutsceneCameraDistance = CutsceneSettings.DefaultCameraDistance;

    /// <summary>
    /// 【Cutscene】カメラの高さ（怪獣の中心からの相対。メートル）。
    /// 0 で怪獣と同じ高さ、負値で見上げる構図になる（0 も有効な指定なので補正しない）。
    /// 「演出:カメラ目標アクタ」を設定した場合は使われない。
    /// </summary>
    [SerializeField(Label = "演出:カメラ高さ(m)", Tooltip = "怪獣の中心から何 m 上にカメラを置くか。負値で見上げる。カメラ目標アクタ指定時は無視")]
    public float cutsceneCameraHeight = CutsceneSettings.DefaultCameraHeight;

    /// <summary>
    /// 【Cutscene】カメラの方位角（度）。怪獣の<b>進行方向</b>を基準に水平へ回り込ませる。
    /// 0＝進行方向側（正面から迫ってくる絵。従来の構図）、90＝真横、180＝背後、
    /// 負値で 90 とは反対側の真横。
    /// 「演出:カメラ目標アクタ」を設定した場合は使われない。
    /// </summary>
    [SerializeField(Label = "演出:カメラ方位角(度)", Tooltip = "0=進行方向側(正面) / 90=真横 / 180=背後 / 負値で反対側の真横。カメラ目標アクタ指定時は無視")]
    public float cutsceneCameraAzimuthDegrees = CutsceneSettings.DefaultCameraAzimuthDegrees;

    /// <summary>
    /// 【Cutscene】カメラが見る点を怪獣の中心から何メートル上へずらすか。
    /// 0（既定）で中心をそのまま見る。大きくすると画面の下寄りに怪獣が入る。
    /// 「演出:カメラ目標アクタ」を設定した場合は使われない。
    /// </summary>
    [SerializeField(Label = "演出:注視点の高さ(m)", Tooltip = "カメラが見る点を怪獣の中心から何 m 上へずらすか。カメラ目標アクタ指定時は無視")]
    public float cutsceneCameraLookAtHeight = CutsceneSettings.DefaultCameraLookAtHeight;

    /// <summary>
    /// 【Cutscene】カメラ補間の速さ（1 秒あたりの収束率。大きいほど速く寄る）。
    /// 0 以下なら <see cref="CutsceneSettings.DefaultCameraLerpRate"/> が使われる。
    /// </summary>
    [SerializeField(Label = "演出:カメラ補間の速さ", Tooltip = "カメラが目標の構図へ寄る速さ。大きいほど速い。0 以下で既定値")]
    public float cutsceneCameraLerpRate = CutsceneSettings.DefaultCameraLerpRate;

    /// <summary>
    /// 【Cutscene】演出中だけ使うカメラの画角（度）。0 で画角を変えない。
    /// 指定すると演出の間だけ MainCamera の視野角をこの値へ寄せ、演出が終わると元へ戻す。
    /// 「演出:カメラ目標アクタ」に Camera が付いていれば、そちらの画角が優先される。
    /// </summary>
    [SerializeField(Label = "演出:カメラ画角(度)", Tooltip = "演出中だけ使う垂直視野角。0 で変更しない。終了時は元の画角へ戻す")]
    public float cutsceneCameraFieldOfView = CutsceneSettings.FieldOfViewUnspecified;

    /// <summary>
    /// 【Cutscene】カメラ目標アクタ（任意）。
    /// 設定すると距離・高さ・方位角からの構図計算をやめ、このアクタの位置・回転へ補間する
    /// （そのアクタに Camera が付いていれば画角も合わせる）。会話カメラの目標アクタと同じ流儀。
    /// </summary>
    [SerializeField(Label = "演出:カメラ目標アクタ", Tooltip = "指定するとカメラはこのアクタの位置・回転（Camera があれば画角も）へ寄る。距離・高さ・方位角は無視")]
    public SEED.Transform cutsceneCameraTarget;

    // ─── ヒント演出（実践中だけ出す操作ガイド）───────────────

    /// <summary>
    /// 実践中（<see cref="TutorialDirector"/> の Playing 段階）だけ表示してアニメーションを
    /// 再生するヒントアクタ（例: マウス操作の見本を示す TutorialMouse）。未設定
    /// （<see cref="SEED.GameObject.IsValid"/> == false）のミッションでは何もしない。
    ///
    /// 【表示・再生のタイミング】
    /// ミッションが Playing に入った瞬間（説明＝Intro が無ければ開始と同時、
    /// あれば読み終えた直後）に <c>Visible = true</c> にして
    /// <see cref="SEED.Animator.Play(string)"/> を呼ぶ。ミッションが終わる
    /// （達成バナー開始・チュートリアル終了・<see cref="TutorialDirector.OnDestroy"/> など）
    /// と同時に停止して <c>Visible = false</c> へ戻す。
    /// 合いの手（Interjecting）で説明を挟んでいる間は明示的には止めない
    /// （その間はゲーム時間ごと止まるため、Animator も自然に止まる）。
    /// </summary>
    [Header("ヒント演出"), SerializeField(Label = "ヒントアクタ", Tooltip = "実践中だけ表示してアニメーションを再生するアクタ（任意）")]
    public SEED.GameObject hintActor;

    /// <summary>
    /// <see cref="hintActor"/> の Animator に再生させるクリップ名。
    ///
    /// 【空文字の扱い】
    /// 本来は「Animator に登録された先頭クリップを再生する」を既定にしたいところだが、
    /// 現行のスクリプト API（<see cref="SEED.Animator"/>）には登録済みクリップの一覧や
    /// 先頭クリップ名を取得する手段が無い（公開されているのは Play/CrossFade/Stop 等の
    /// 操作と IsPlaying/CurrentClip/Time/Speed 等、再生「中」の状態だけ）。そのため
    /// 空文字のときはクリップの再生を行わず、ヒントアクタの表示切替だけを行う。
    /// アニメーションを再生させたいミッションでは、このフィールドへ再生したい
    /// クリップ名を明示的に指定すること。
    /// </summary>
    [SerializeField(Label = "ヒントクリップ名", Tooltip = "hintActor の Animator に再生させるクリップ名。空文字は「表示切替のみ・再生なし」を意味する（先頭クリップの自動解決はスクリプト API 未対応のため行わない）")]
    public string hintClipName;

    /// <summary>
    /// ヒントを出し始める<b>きっかけのイベント名</b>（<see cref="FishingEvents"/> の定数）。
    /// 空文字（既定）なら実践（Playing）に入った瞬間に出す。
    ///
    /// 【何のためにあるか】
    /// 投げの説明（t1_cast）では「左クリックで構える → マウスを振る」の 2 段構えなので、
    /// 実践に入った瞬間から振りの見本を出すと、まだ構えてもいないのに
    /// 「振れ」と言われることになる。ここに <c>fishing.ready_begin</c>（構えた瞬間）を
    /// 指定すると、竿を構えてから初めて見本が動き出す。
    /// </summary>
    [SerializeField(Label = "ヒント開始イベント", Tooltip = "このイベントを受けてからヒントを出す（例 fishing.ready_begin）。空で実践開始と同時")]
    public string hintStartEvent;

    /// <summary>
    /// ヒントを<b>一旦引っ込める</b>きっかけのイベント名（<see cref="FishingEvents"/> の定数）。
    /// 空文字（既定）なら、ミッションが終わるまで出しっぱなしにする。
    ///
    /// 受け取ると <see cref="hintStartEvent"/> の待ち受けへ戻るので、
    /// 「構える → 見本が出る → 構えを解く → 消える → もう一度構える → また出る」を繰り返せる。
    /// <see cref="hintStartEvent"/> が空のときは意味を持たない（開始待ちが無いため）。
    /// </summary>
    [SerializeField(Label = "ヒント停止イベント", Tooltip = "このイベントでヒントを一旦止め、再び開始イベントを待つ（例 fishing.ready_end）。空で止めない")]
    public string hintStopEvent;

    // ─── 前後で呼ぶイベント ─────────────────────────────────

    /// <summary>このミッションを開始した瞬間に呼ぶイベント（カメラ寄せ・SE などの結線用）。</summary>
    [Header("イベント"), SerializeField(Label = "開始時イベント", Tooltip = "ミッションを開始した瞬間に呼ばれる")]
    public SEED.ScriptEvent onStart;

    /// <summary>このミッションを達成して次へ進む瞬間に呼ぶイベント。</summary>
    [SerializeField(Label = "終了時イベント", Tooltip = "ミッションを達成して次へ進む瞬間に呼ばれる")]
    public SEED.ScriptEvent onEnd;
}
