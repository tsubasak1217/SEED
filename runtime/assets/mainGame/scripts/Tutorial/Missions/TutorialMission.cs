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
/// 【使い方（シーン側）】
/// TutorialDirector の「ミッション」リストへ 1 件ずつ追加し、
/// 「台詞」リストへ同じ <see cref="id"/> の説明を必要な枚数だけ足す。
/// </summary>
[System.Serializable]
public struct TutorialMission
{
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

    /// <summary>true の間、わらしべ連鎖（他の魚が掛かった魚を食う）を成立させない。</summary>
    [SerializeField(Label = "連鎖なし", Tooltip = "true で掛かった魚を他の魚が食う連鎖を起こさない")]
    public bool chainDisabled;

    /// <summary>true の間、漂流物を自然出現させない。</summary>
    [SerializeField(Label = "漂流物なし", Tooltip = "true で漂流物を自然出現させない（台本での生成は別）")]
    public bool driftDisabled;

    /// <summary>true の間、漂流物を漂わせず寿命でも消さない（台本が並べた位置に留める）。</summary>
    [SerializeField(Label = "漂流物を固定", Tooltip = "true で漂流物が流れず寿命でも消えない（一直線に並べる台本用）")]
    public bool driftStationary;

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

    // ─── 前後で呼ぶイベント ─────────────────────────────────

    /// <summary>このミッションを開始した瞬間に呼ぶイベント（カメラ寄せ・SE などの結線用）。</summary>
    [Header("イベント"), SerializeField(Label = "開始時イベント", Tooltip = "ミッションを開始した瞬間に呼ばれる")]
    public SEED.ScriptEvent onStart;

    /// <summary>このミッションを達成して次へ進む瞬間に呼ぶイベント。</summary>
    [SerializeField(Label = "終了時イベント", Tooltip = "ミッションを達成して次へ進む瞬間に呼ばれる")]
    public SEED.ScriptEvent onEnd;
}
