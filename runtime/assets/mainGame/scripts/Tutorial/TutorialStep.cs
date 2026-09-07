// ============================================================================
//  TutorialStep.cs
//  チュートリアル 1 手順ぶんのデータ定義（データドリブンの最小単位）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// チュートリアル 1 手順ぶんのデータ。
///
/// 【責務】
/// 「何を説明し」「どこに出し」「いつ始まり」「いつ終わり」「その間どの操作を許し」
/// 「釣りシステムに何を強制するか」という 1 手順ぶんの情報だけを持つ。
/// 進行や表示のロジックは一切持たない（<see cref="TutorialDirector"/> と
/// <see cref="TutorialWindow"/> の責務）。
///
/// 【使い方（シーン側）】
/// TutorialDirector の「手順」リストへ 1 件ずつ追加する。
/// 本文にはインライン画像記法（[icon:key_w] など）を書ける。
/// 名前は TutorialWindow に設定したアイコンセット（.icons）の key と一致させること。
///
/// 【入力許可の考え方】
/// 許可フラグはすべて<b>既定 false</b>（＝説明中は何も操作できない）。
/// その手順で実際に試してほしい操作だけを true にする。
/// 「決定で送る手順」で操作も許すと同じ左クリックが二重に効くため、
/// 操作を許す手順は終了条件をイベントか秒数にすること。
/// </summary>
[System.Serializable]
public struct TutorialStep
{
    // ─── 説明の内容 ─────────────────────────────────────────

    /// <summary>
    /// 説明文。インスペクタでは複数行テキストボックスになる。
    /// Text のインライン画像記法（[icon:key_w] など）と自動折り返しが効く。
    /// </summary>
    [SerializeField(Label = "説明文", Tooltip = "説明する文章。[icon:key_w] のようにキーアイコンを混ぜられる")]
    [TextArea(4)]
    public string text;

    // ─── 表示位置 ───────────────────────────────────────────

    /// <summary>説明窓の配置モード。</summary>
    [SerializeField(Label = "配置モード", Tooltip = "ScreenFixed = 画面固定 / AboveTarget = 対象アクタの上に追従")]
    public TutorialAnchorMode anchorMode;

    /// <summary>AboveTarget のときに追従する対象アクタ（未設定なら画面固定へフォールバックする）。</summary>
    [SerializeField(Label = "追従対象", Tooltip = "配置モードが AboveTarget のときに追従するアクタ")]
    public SEED.Transform target;

    /// <summary>
    /// ScreenFixed のときに窓を重ねる「位置アンカー」アクタの CanvasTransform。
    ///
    /// 【なぜ座標値ではなく参照なのか】
    /// 画面位置を数値（X / Y / 倍率）で持つと、解像度・キャンバス基準が変わるたびに
    /// 全手順の数値を打ち直すことになり、しかもエディタ上で位置を目視できない。
    /// 空の 2D アクタ（TutorialAnchors/AnchorCenter など）を画面上の狙った場所に置き、
    /// その CanvasTransform（アンカー・ピボット・位置・拡大率）を丸ごと窓へ写せば、
    /// 位置調整は「アクタをドラッグするだけ」で済み、レイアウトはシーン側のデータになる。
    ///
    /// 未設定・無効なら窓は現在の位置のまま出る（詰まらないためのフォールバック）。
    /// </summary>
    [SerializeField(Label = "位置アンカー", Tooltip = "画面固定時に窓を重ねる 2D アクタ（TutorialAnchors 配下）")]
    public SEED.CanvasTransform anchorTarget;

    // ─── 開始条件 ───────────────────────────────────────────

    /// <summary>この手順を開始する条件。</summary>
    [SerializeField(Label = "開始条件", Tooltip = "Immediately = すぐ / OnEvent = 指定イベントを待つ")]
    public TutorialStartCondition startCondition;

    /// <summary>開始条件が OnEvent のときに待つイベント名（FishingEvents の値）。</summary>
    [SerializeField(Label = "開始イベント名", Tooltip = "開始条件が OnEvent のときに待つイベント名（例 fishing.bite）")]
    public string startEventName;

    // ─── 終了条件 ───────────────────────────────────────────

    /// <summary>この手順を終了する条件。</summary>
    [SerializeField(Label = "終了条件", Tooltip = "Confirm = 決定入力 / OnEvent = 指定イベント / Seconds = 秒数")]
    public TutorialFinishCondition finishCondition;

    /// <summary>終了条件が OnEvent のときに待つイベント名（FishingEvents の値）。</summary>
    [SerializeField(Label = "終了イベント名", Tooltip = "終了条件が OnEvent のときに待つイベント名（例 fishing.cast）")]
    public string finishEventName;

    /// <summary>
    /// 終了条件が Seconds のときの待ち時間（秒・実時間）。
    ///
    /// 終了条件が OnEvent のときは<b>保険のタイムアウト</b>として働く
    /// （0 以下ならタイムアウト無し）。待っているイベントが二度と起きない状況
    /// （例: 巻きの説明中に糸が切れてやり取りが終わってしまった）でも、
    /// この秒数が過ぎれば次の手順へ進めるので、チュートリアルが詰まらない。
    /// </summary>
    [SerializeField(Label = "終了までの秒数", Tooltip = "Seconds のときの待ち時間。OnEvent のときは保険のタイムアウト（0 で無効）")]
    public float finishSeconds;

    // ─── 時間停止と入力許可 ─────────────────────────────────

    /// <summary>この手順のあいだゲーム時間を止めるか（Time.Scale = 0）。</summary>
    [SerializeField(Label = "時間を止める", Tooltip = "true でゲーム時間を停止する（UI は実時間で動き続ける）")]
    public bool pauseTime;

    /// <summary>移動（W / S）を許可するか。</summary>
    [SerializeField(Label = "許可: 移動", Tooltip = "W / S での移動を許可する")]
    public bool allowMove;

    /// <summary>構え（左クリック押下・保持）を許可するか。</summary>
    [SerializeField(Label = "許可: 構え", Tooltip = "左クリックで竿を構える操作を許可する")]
    public bool allowReady;

    /// <summary>構え中の左右の狙い（A / D）を許可するか。</summary>
    [SerializeField(Label = "許可: 狙い", Tooltip = "構え中に A / D で向きを変える操作を許可する")]
    public bool allowAim;

    /// <summary>振りかぶり・振り抜き（マウスの振り）を許可するか。</summary>
    [SerializeField(Label = "許可: 投げ", Tooltip = "マウスを左→右へ振る投げの操作を許可する")]
    public bool allowCast;

    /// <summary>巻き取り（ホイール）と操舵（A / D）を許可するか。</summary>
    [SerializeField(Label = "許可: 巻き", Tooltip = "ホイールでの巻き取りと A / D の操舵を許可する")]
    public bool allowReel;

    /// <summary>竿を振る（合わせ・空振り）を許可するか。</summary>
    [SerializeField(Label = "許可: 合わせ", Tooltip = "左クリックで竿を振る（合わせ）操作を許可する")]
    public bool allowHook;

    /// <summary>やり取り中のリズム回答タップを許可するか。</summary>
    [SerializeField(Label = "許可: リズム", Tooltip = "やり取り中の左クリック（リズム回答）を許可する")]
    public bool allowRhythm;

    /// <summary>釣果表示など UI の決定入力を許可するか。</summary>
    [SerializeField(Label = "許可: UI決定", Tooltip = "釣果表示を閉じるなど UI の決定入力を許可する")]
    public bool allowUiConfirm;

    // ─── 台本（釣りシステムへの強制設定）───────────────────

    /// <summary>この手順の開始時に釣りシステムへ強制する台本の種類。</summary>
    [SerializeField(Label = "台本", Tooltip = "None / ForceBite（必ず食いつかせる）/ SpawnDrift（漂流物を出す）")]
    public TutorialScriptedAction scriptedAction;

    /// <summary>
    /// 台本のパラメータ（文字列）。
    /// SpawnDrift のときは漂流物の種類（stun / fish_recover / line_recover）を入れる。
    /// ForceBite のときは使わない。
    /// </summary>
    [SerializeField(Label = "台本パラメータ", Tooltip = "SpawnDrift のときの種類（stun / fish_recover / line_recover）")]
    public string scriptedParam;

    /// <summary>
    /// 台本のパラメータ（数値）。
    /// ForceBite のときは「食いつくまでの秒数」。0 以下なら即座に食いつく。
    /// </summary>
    [SerializeField(Label = "台本の秒数", Tooltip = "ForceBite のときの食いつくまでの秒数（0 以下なら即座）")]
    public float scriptedSeconds;

    /// <summary>
    /// 台本のレベル指定。ForceBite のときに出す魚のレベル（1 始まり。0 以下なら Lv1）。
    /// </summary>
    [SerializeField(Label = "台本のレベル", Tooltip = "ForceBite のときに食いつかせる魚のレベル（1 始まり）")]
    public int scriptedLevel;

    // ─── 前後で呼ぶイベント ─────────────────────────────────

    /// <summary>この手順の表示を始めた瞬間に呼ぶイベント（カメラ寄せ・SE などの結線用）。</summary>
    [SerializeField(Label = "開始時イベント", Tooltip = "この手順の表示を始めた瞬間に呼ばれる")]
    public SEED.ScriptEvent onStart;

    /// <summary>この手順を終えて次へ進む瞬間に呼ぶイベント。</summary>
    [SerializeField(Label = "終了時イベント", Tooltip = "この手順を終えて次へ進む瞬間に呼ばれる")]
    public SEED.ScriptEvent onEnd;
}
