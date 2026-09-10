// ============================================================================
//  ResultPanel.cs
//  釣果リザルトパネル（魚の絵・名前・サイズ・ランク・自己ベスト・図鑑登録）。
// ============================================================================

using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext

/// <summary>
/// 釣果リザルトパネル本体【釣果 UI の唯一の持ち主】。
///
/// 【責務】
/// 「釣果（<see cref="ResultData"/>）を<b>受け取った順に</b>出し、閉じ終わったことを
/// 知らせる」だけ。<b>何を釣ったか・記録がどうなったかは一切知らない</b>
/// （それを決めるのは <see cref="CatchPresenter"/> と <see cref="FishRecords"/>）。
///
/// 【魚の絵の出入り（動きは Animator のキーフレームクリップが持つ）】
/// 絵の拡大縮小を<b>スクリプトが数値で作ることはしない</b>。<c>FishImage</c> 自身に
/// 付けた <c>Animator</c> の 2 本のクリップが <c>canvas_transform.scale</c> を動かす。
/// <code>
/// result_fish_in  … 画面いっぱい相当の倍率 → 表示サイズ（等倍）。easeOutCubic
/// result_fish_out … 表示サイズ → 0 倍（消える）。easeInCubic
/// </code>
/// クリップは <c>tools/gen_result_fish_clips.py</c> が生成する（開始倍率はキャンバスの
/// 設計解像度と絵の実寸から逆算するので、絵の大きさを変えたら流し直す）。
/// このスクリプトが持つのは「いつどちらを再生するか」だけで、倍率も秒数も持たない。
/// Animator が無い／クリップが未登録のときは<b>警告 1 行</b>を出し、絵を表示サイズで
/// 出したまま即時に切り替える（＝連鎖の進行は必ず最後まで通る）。
///
/// 【連鎖（わらしべで複数匹まとめて釣り上げたとき）】
/// パネルは<b>1 枚だけ</b>で、中身を差し替えながら順に見せる（<see cref="ShowChain"/>）。
/// <code>
/// 1 匹目を開く → 絵が登場（result_fish_in）
///   → chainHoldSeconds 秒見せる
///   → 絵が縮んで消える（result_fish_out）→ 消えている裏で絵を次の魚へ差し替える
///   → 次の魚の絵が登場（result_fish_in）
///   → 登場し切った瞬間に文字（名前・サイズ・自己ベスト・New Record・ランク）も次の魚へ
///   → 最後の 1 匹まで繰り返し、そこで決定入力を待つ（最後の絵は消さずに残す）
/// 「図鑑に登録されました」は、一覧に初捕獲が 1 匹でも居れば<b>最後に 1 回だけ</b>出す
/// </code>
///
/// 【配置方式】<see cref="PauseMenu"/> と同じ。
/// このスクリプトはプレハブ <c>assets://mainGame/actors/UI/ResultPanel.actor</c> の
/// ルート（Canvas を持つ Actor2D）に付く。推奨は<b>プレハブのインスタンスをあらかじめ
/// シーンのルートへ置いておく</b>こと。<see cref="OnStart"/> が自分を本体として登録し、
/// 出すまで非表示にする。シーンに置かれていない場合に限り <see cref="Show"/> が
/// プレハブを <c>Instantiate</c> する（フォールバック。生成したアクタの
/// <c>OnStart</c> は<b>次のフレーム</b>に走るので、渡された内容は
/// <see cref="chainEntries"/> にいったん預け、<c>OnStart</c> が拾って表示する）。
///
/// 【構成（プレハブ側）】
/// <code>
/// ResultPanel                  … このスクリプト（Canvas / auto_scale）
///  ResultBody                  … 拡大縮小の親（ここの CanvasTransform.Scale を動かす）
///   ResultBg                   … Sprite（下敷き）
///   FishImage                  … Sprite（図鑑画像。実行時に TexturePath を差し替える）
///                                 ＋ Animator（登場・退場クリップ。倍率だけを動かす）
///   FishName / FishSize / FishBest / FishRank / Prompt … Text
///   NewRecord                  … Text（新記録のときだけ点滅表示）
///   NewRecordSparkle           … ParticleEmitter（新記録のあいだ小さくきらめき続ける）
///   RegisteredPanel            … 図鑑登録の追加パネル（既定は非表示）
///    RegisteredBg              … Sprite
///    RegisteredTitle / RegisteredHint … Text
///    RegisteredConfetti        … ParticleEmitter（登録パネルが開いた瞬間の紙吹雪）
/// </code>
/// <b>拡大縮小を <c>ResultBody</c> に掛ける理由</b>: キャンバスのルート
/// （Canvas を持つアクタ）は 2D 座標系そのものなので、そこへスケールを掛けても
/// 子がまとめて拡大縮小されるとは限らない。子の位置・サイズは
/// 「親の累積スケール」で乗算される規約（ランタイム <c>canvas_collect.rs</c>）なので、
/// <b>中間の入れ物を 1 枚挟んでそこを動かす</b>のが確実。
///
/// 【子アクタの参照の持ち方】
/// <see cref="PauseMenu"/> と同じく、参照は<b>相対パスの文字列</b>で持ち
/// <see cref="OnStart"/> で <c>gameObject.FindChild</c> により解決する
/// （参照ハンドル型フィールドは C# の初期化子で既定値を持てず、シーンで結線しないと
/// 　必ず未設定になるため）。パス書式は docs/scripting_api.md 第 7 節と同じで、
/// 末尾の <c>|スロット名</c> がコンポーネントのスロット名。
///
/// 【時間軸】
/// 釣り上げ演出はスロー（<c>Time.Scale</c> を下げる）区間を含むので、
/// パネルの拡大縮小・点滅・入力受付はすべて<b>実時間</b>（<c>Time.Unscaled*</c>）で行う。
///
/// 例外は <c>Animator</c> で、エンジンの AnimationSystem は<b>ゲーム時間</b>
/// （<c>Time.Scale</c> 適用後）でクリップを進める。パネルを開くのはスローを戻した後
/// （<see cref="CatchPresenter"/> が <c>Time.Scale</c> を 1 に戻してから開く）なので
/// 実時間と一致し、ポーズ中（<c>Time.Scale</c> = 0）はクリップも待ちの安全網も
/// 一緒に止まる（安全網の時計だけは<b>ゲーム時間</b>で数える。理由は
/// <see cref="IsFishClipFinished"/>）。
/// </summary>
public class ResultPanel : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>参照文字列の「アクタパス」と「スロット名」の区切り文字（例 <c>./ResultBody/FishName|Text</c>）。</summary>
    private const char ReferenceSlotSeparator = '|';

    /// <summary>0 除算を避けるための「実質 0 秒」しきい値。</summary>
    private const float DivideEpsilon = 1e-4f;

    /// <summary>不透明を表すアルファ。</summary>
    private const float AlphaOpaque = 1f;

    /// <summary>透明を表すアルファ。</summary>
    private const float AlphaClear = 0f;

    /// <summary>easeOutBack / easeInBack の跳ね返り係数 c1 の標準値（跳ね返り量 1.0 のとき）。</summary>
    private const float BackEaseBaseC1 = 1.70158f;

    /// <summary>スケールの下限（負スケールで裏返らないようにするクランプ値）。</summary>
    private const float MinScale = 0f;

    /// <summary>点滅の明滅比（<c>PingPong</c> の振幅。0〜1 の全域を使う）。</summary>
    private const float BlinkAmplitude = 1f;

    /// <summary>連鎖の一覧で最初に見せるエントリの添字（＝最初に掛かった魚）。</summary>
    private const int ChainFirstEntryIndex = 0;

    /// <summary>
    /// 魚の絵の表示サイズの倍率（＝プレハブの実寸そのまま。登場クリップの終端値と同じ）。
    /// Animator が使えないときのフォールバックでだけ書き込む。
    /// </summary>
    private const float FishShownScale = 1f;

    /// <summary>
    /// 魚の絵のクリップ待ちの安全網: クリップの尺の何倍まで待つか
    /// （<see cref="IsFishClipFinished"/>。1 未満にすると正常な再生を切ってしまう）。
    /// </summary>
    private const float FishAnimWaitFactor = 3f;

    /// <summary>
    /// 魚の絵のクリップ待ちの安全網: 尺の何倍かに加えて足す余裕（秒・ゲーム時間）。
    /// 尺 0 のクリップでも必ず 1 度は評価される長さを確保するために足す。
    /// </summary>
    private const float FishAnimWaitMarginSeconds = 1f;

    // ─── 表示内容（呼び出し側が組み立てて渡す）─────────────────

    /// <summary>
    /// 釣果パネルに出す 1 匹ぶんの内容【パネルの入力の唯一の形】。
    ///
    /// パネルは「この構造体に入っているものだけ」を出す。項目を増やしたくなったら
    /// ここへフィールドを足し、<see cref="ApplyData"/> の流し込みを 1 行足すだけで済む
    /// （呼び出し側の引数の並びを増やして回らずに済むよう構造体で渡す）。
    /// </summary>
    public readonly struct ResultData
    {
        /// <summary>魚の表示名（例「マダイ」）。</summary>
        public readonly string Name;

        /// <summary>サイズの表示文字列（例「32.5cm」。単位まで込みで組み立て済み）。</summary>
        public readonly string SizeText;

        /// <summary>自己ベストの表示文字列（例「自己ベスト: 30.0cm」。組み立て済み）。</summary>
        public readonly string BestText;

        /// <summary>ランクの表示文字列（例「ランク S」。組み立て済み）。</summary>
        public readonly string RankText;

        /// <summary>
        /// <b>素の</b>サイズランク文字（<c>"S"</c> / <c>"A"</c> / <c>"B"</c> / <c>"C"</c>）。
        ///
        /// <see cref="RankText"/> は「ランク S」のように接頭辞やラベルを付けた
        /// <b>見せるための文字列</b>なので、そこからランクを切り出すのは書式変更に弱い。
        /// 配色（ランクごとの文字色）は<b>素のランク文字</b>で引くため、
        /// 見せる文字列とは別にこちらを運ぶ。
        /// </summary>
        public readonly string RankKey;

        /// <summary>図鑑画像の <c>assets://</c> パス（空なら単色のままにする）。</summary>
        public readonly string ImagePath;

        /// <summary>ベストを更新したか（true のとき「New Record!!」を点滅表示する）。</summary>
        public readonly bool NewRecord;

        /// <summary>その魚種の初捕獲か（true のとき「図鑑に登録されました」を追加で出す）。</summary>
        public readonly bool FirstCatch;

        /// <summary>全項目を指定して作る。</summary>
        /// <param name="name">魚の表示名。</param>
        /// <param name="sizeText">サイズの表示文字列。</param>
        /// <param name="bestText">自己ベストの表示文字列。</param>
        /// <param name="rankText">ランクの表示文字列。</param>
        /// <param name="rankKey">素のサイズランク文字（配色に使う）。</param>
        /// <param name="imagePath">図鑑画像の assets:// パス。</param>
        /// <param name="newRecord">ベストを更新したか。</param>
        /// <param name="firstCatch">その魚種の初捕獲か。</param>
        public ResultData(
            string name, string sizeText, string bestText, string rankText, string rankKey,
            string imagePath, bool newRecord, bool firstCatch)
        {
            Name = name;
            SizeText = sizeText;
            BestText = bestText;
            RankText = rankText;
            RankKey = rankKey;
            ImagePath = imagePath;
            NewRecord = newRecord;
            FirstCatch = firstCatch;
        }
    }

    // ─── パネルの進行フェーズ ────────────────────────────────

    /// <summary>
    /// パネルの進行フェーズ【表示中かどうかの唯一の判断材料】。
    /// <see cref="Hidden"/> 以外なら表示中（<see cref="IsActive"/>）。
    /// </summary>
    private enum PanelPhase
    {
        /// <summary>出ていない（待機）。</summary>
        Hidden,

        /// <summary>本体が easeOutBack で 0 → 原寸へ膨らんでいる。</summary>
        Opening,

        /// <summary>
        /// 魚の絵が登場している（<c>result_fish_in</c> の再生中）。
        /// <b>どの魚でも必ず通る</b>（1 匹目も含む）。再生し切った瞬間に文字
        /// （名前・サイズ・自己ベスト・New Record・ランク）をその魚のものへ確定させる。
        /// </summary>
        FishIn,

        /// <summary>
        /// <b>連鎖の途中</b>: いま出している魚をそのまま見せて次の切り替えを待っている
        /// （<see cref="chainHoldSeconds"/> 秒）。連鎖が 1 匹だけならこのフェーズは通らない。
        /// </summary>
        ChainHolding,

        /// <summary>
        /// <b>連鎖の途中</b>: いま出している魚の絵が縮んで消えている
        /// （<c>result_fish_out</c> の再生中）。消え切ったら絵だけを次の魚へ差し替え、
        /// <see cref="FishIn"/> へ戻る。<b>最後の魚は消さない</b>ので通らない。
        /// </summary>
        FishOut,

        /// <summary>本体が出きって、決定入力を待っている。</summary>
        Idle,

        /// <summary>図鑑登録パネルが easeOutBack で膨らんでいる（初捕獲のときだけ通る）。</summary>
        RegisteredOpening,

        /// <summary>図鑑登録パネルも出きって、決定入力を待っている。</summary>
        RegisteredIdle,

        /// <summary>
        /// 図鑑登録パネルだけが easeInBack で原寸 → 0 へ縮んでいる。
        /// 縮み切ったら本体の <see cref="Closing"/> へ続く。
        ///
        /// <b>本体と同時に縮めない理由</b>: 登録パネルは本体（ResultBody）の子なので、
        /// スケールは親の累積スケールで<b>乗算</b>される。同時に縮めると二乗で潰れて
        /// 一瞬で消えたように見えるうえ、どちらの演出も読み取れない。
        /// 「登録パネルが引っ込む → 本体が引っ込む」の順に見せる。
        /// </summary>
        RegisteredClosing,

        /// <summary>全体が easeInBack で原寸 → 0 へ縮んでいる。縮み切ったら <see cref="Hidden"/>。</summary>
        Closing,
    }

    // ─── 静的状態（パネルの単一の真実）───────────────────────

    /// <summary>
    /// パネル本体（シーン配置済みなら <see cref="OnStart"/> で登録、無ければ
    /// <see cref="Show"/> が生成して登録）。未登録なら <c>IsValid == false</c>。
    /// </summary>
    /// <summary>
    /// リザルトパネル本体に付ける目印の名前（<see cref="SpawnOnce"/> の照合キー）。
    /// プレハブ（ResultPanel.actor）のルート名と同じにしてある。
    /// </summary>
    private const string PanelActorName = "ResultPanel";

    private static SEED.GameObject panelRoot;

    /// <summary>実行中のインスタンス（生成フォールバック時に <c>OnStart</c> が自分を登録する）。</summary>
    private static ResultPanel? Current { get; set; }

    /// <summary>
    /// いまパネルが出ているか【呼び出し側が「閉じ終わり」を待つための唯一の窓口】。
    ///
    /// <see cref="Show"/> の呼び出しの中で即座に true になる（生成フォールバックで
    /// 実体が次フレームでも、呼び出し側は同じフレームから「出ている」として扱える）。
    /// 閉じ切った瞬間に false へ戻る。
    /// </summary>
    public static bool IsActive { get; private set; }

    /// <summary>
    /// 閉じ切った瞬間に 1 回だけ呼ばれる通知（未設定なら何もしない）。
    ///
    /// 進行の主導権は呼び出し側（<see cref="CatchPresenter"/>）にあり、そちらは
    /// <see cref="IsActive"/> のポーリングで閉じ終わりを知る作りになっている。
    /// この通知は「閉じた瞬間だけ何かしたい」他の購読者のための補助口で、
    /// パネル側は成否を問わず 1 回呼ぶだけ（例外は握らない）。
    /// </summary>
    public static System.Action? OnClosed { get; set; }

    /// <summary>
    /// これから見せる釣果の一覧（先頭 ＝ 最初に見せる魚）
    /// 【パネルが「何匹ぶん出すか」の唯一の元データ】。
    ///
    /// わらしべ連鎖で複数匹まとめて釣り上げたときは、この順に
    /// <b>1 枚のパネルの中で</b>絵と文字を差し替えて見せていく
    /// （<see cref="PanelPhase.ChainHolding"/> → <see cref="PanelPhase.FishOut"/>
    /// 　→ <see cref="PanelPhase.FishIn"/>）。
    /// 連鎖なし（1 匹）のときは要素 1 つだけの一覧になり、登場アニメのあとそのまま決定待ちになる。
    ///
    /// 静的に持つのは、実体がまだ無い（プレハブ生成待ち）ときも呼び出し側から
    /// 内容を預けられるようにするため（<see cref="hasPendingData"/>）。
    /// </summary>
    private static readonly System.Collections.Generic.List<ResultData> chainEntries = new();

    /// <summary><see cref="chainEntries"/> に未消化の内容が入っているか（実体の <c>OnStart</c> 待ち）。</summary>
    private static bool hasPendingData;

    // ─── インスペクタ設定（子アクタへの参照文字列）─────────────

    /// <summary>拡大縮小させる入れ物（<c>CanvasTransform</c> を持つアクタ）への相対パス。</summary>
    [Header("参照（自分からの相対パス）"), SerializeField(Label = "本体の入れ物")]
    private string bodyPath = "./ResultBody";

    /// <summary>魚の絵（Sprite）への相対パス。</summary>
    [SerializeField(Label = "魚の絵")]
    private string fishImagePath = "./ResultBody/FishImage|Sprite";

    /// <summary>魚の名前（Text）への相対パス。</summary>
    [SerializeField(Label = "名前")]
    private string namePath = "./ResultBody/FishName|Text";

    /// <summary>サイズ（Text）への相対パス。</summary>
    [SerializeField(Label = "サイズ")]
    private string sizePath = "./ResultBody/FishSize|Text";

    /// <summary>自己ベスト（Text）への相対パス。</summary>
    [SerializeField(Label = "自己ベスト")]
    private string bestPath = "./ResultBody/FishBest|Text";

    /// <summary>ランク（Text）への相対パス。</summary>
    [SerializeField(Label = "ランク")]
    private string rankPath = "./ResultBody/FishRank|Text";

    /// <summary>新記録（Text）への相対パス。新記録のときだけ点滅表示する。</summary>
    [SerializeField(Label = "新記録")]
    private string newRecordPath = "./ResultBody/NewRecord|Text";

    /// <summary>操作案内（Text）への相対パス。</summary>
    [SerializeField(Label = "操作案内")]
    private string promptPath = "./ResultBody/Prompt|Text";

    /// <summary>図鑑登録パネル（入れ物アクタ）への相対パス。初捕獲のときだけ出す。</summary>
    [SerializeField(Label = "図鑑登録パネル")]
    private string registeredPath = "./ResultBody/RegisteredPanel";

    /// <summary>
    /// 新記録のきらめき（<c>ParticleEmitter</c>）への相対パス。
    /// 新記録のあいだだけループ放出させる（点滅する文字の周りで小さく光る）。
    /// 粒の見た目（寿命・初速・大きさ・色・放出間隔）は<b>プレハブのエミッタが持つ</b>ので、
    /// 詰めたくなったらエディタで <c>NewRecordSparkle</c> を選んで触る。
    /// </summary>
    [SerializeField(Label = "新記録のきらめき")]
    private string newRecordSparklePath = "./ResultBody/NewRecordSparkle|ParticleEmitter";

    /// <summary>
    /// 図鑑登録の紙吹雪（<c>ParticleEmitter</c>）への相対パス。
    /// 登録パネルが開く瞬間に一度だけ一括放出する。色は 3 本の色カーブを
    /// 粒ごとにランダム選択するので、エミッタ 1 つで多色になる。
    /// </summary>
    [SerializeField(Label = "図鑑登録の紙吹雪")]
    private string registeredConfettiPath = "./ResultBody/RegisteredPanel/RegisteredConfetti|ParticleEmitter";

    // ─── インスペクタ設定（文言）──────────────────────────────

    /// <summary>新記録の文言。</summary>
    [Header("文言"), SerializeField(Label = "新記録の文言")]
    private string newRecordLabel = "New Record!!";

    /// <summary>操作案内の文言（本体だけ出ているとき）。</summary>
    [SerializeField(Label = "操作案内(通常)")]
    private string promptLabel = "決定で閉じる";

    /// <summary>操作案内の文言（図鑑登録パネルまで出ているとき）。</summary>
    [SerializeField(Label = "操作案内(登録後)")]
    private string promptAfterRegisteredLabel = "決定で閉じる";

    /// <summary>図鑑画像が無い（パスが空・解決できない）ときに使うテクスチャ。空なら単色のまま。</summary>
    [SerializeField(Label = "画像が無いときのテクスチャ")]
    private string fallbackImagePath = "assets://mainGame/textures/ui/white.png";

    // ─── インスペクタ設定（ランク配色）────────────────────────
    //
    // ランクの色は図鑑カード（ZukanCard）と揃える必要があるので、
    // 「ランク文字 → 4 色のどれか」の選択は共通の RankColorTable に任せ、
    // 色そのもの（16 進カラーコード）だけをここで持つ。
    // 16 進文字列で持つ理由は UiColorUtil のクラスコメントを参照
    // （SEED.Color / SEED.Vector3 の [SerializeField] はインスペクタで編集できない）。

    /// <summary>ランク S の文字色（16 進カラーコード）。既定は金。</summary>
    [Header("ランク配色（図鑑カードと揃える）"), SerializeField(Label = "Sの色(16進)")]
    private string rankColorS = "#FFD54A";

    /// <summary>ランク A の文字色（16 進カラーコード）。既定は珊瑚色。</summary>
    [SerializeField(Label = "Aの色(16進)")]
    private string rankColorA = "#FF7A6B";

    /// <summary>ランク B の文字色（16 進カラーコード）。既定は若草色。</summary>
    [SerializeField(Label = "Bの色(16進)")]
    private string rankColorB = "#7CE38B";

    /// <summary>ランク C の文字色（16 進カラーコード）。既定は水色。</summary>
    [SerializeField(Label = "Cの色(16進)")]
    private string rankColorC = "#8FD3FF";

    /// <summary>ランクが未記録・想定外だったときの文字色（16 進カラーコード）。既定は生成り。</summary>
    [SerializeField(Label = "ランク不明の色(16進)")]
    private string rankColorUnknown = "#FFF5DB";

    // ─── インスペクタ設定（演出）──────────────────────────────

    /// <summary>本体が 0 → 原寸へ膨らむ秒数（easeOutBack・実時間）。</summary>
    [Header("演出"), SerializeField(Label = "開く秒数")]
    private float openSeconds = 0.32f;

    /// <summary>図鑑登録パネルが 0 → 原寸へ膨らむ秒数（easeOutBack・実時間）。</summary>
    [SerializeField(Label = "登録パネルを開く秒数")]
    private float registeredOpenSeconds = 0.28f;

    /// <summary>全体が原寸 → 0 へ縮む秒数（easeInBack・実時間）。</summary>
    [SerializeField(Label = "閉じる秒数")]
    private float closeSeconds = 0.22f;

    /// <summary>
    /// 図鑑登録パネルが原寸 → 0 へ縮む秒数（easeInBack・実時間）。
    /// この縮みが終わってから本体が <see cref="closeSeconds"/> で閉じる。
    /// </summary>
    [SerializeField(Label = "登録パネルを閉じる秒数")]
    private float registeredCloseSeconds = 0.22f;

    /// <summary>
    /// easeOutBack / easeInBack の跳ね返り量（1 で標準。0 でただの 3 次補間、
    /// 大きいほど大げさに行き過ぎてから戻る）。
    /// </summary>
    [SerializeField(Label = "跳ね返り量")]
    private float backOvershoot = 1f;

    /// <summary>「New Record!!」が明→暗→明を一巡する秒数（実時間）。</summary>
    [SerializeField(Label = "点滅の周期(秒)")]
    private float blinkPeriodSeconds = 0.8f;

    /// <summary>点滅の一番暗いときのアルファ（0 で完全に消える）。</summary>
    [SerializeField(Label = "点滅の最小アルファ")]
    private float blinkMinAlpha = 0.15f;

    /// <summary>
    /// 図鑑登録パネルが開く瞬間に一括放出する紙吹雪の個数（0 以下なら出さない）。
    /// </summary>
    [SerializeField(Label = "紙吹雪の個数")]
    private int registeredConfettiCount = 48;

    /// <summary>
    /// 各フェーズへ入ってから決定入力を受け付けるまでの待ち（秒・実時間）。
    /// 直前の演出で押していたクリックが、出た瞬間のパネルを閉じてしまうのを防ぐ。
    /// </summary>
    [SerializeField(Label = "入力を受け付けるまでの待ち(秒)")]
    private float inputDelaySeconds = 0.25f;

    // ─── 連鎖（わらしべで複数匹まとめて釣り上げたときの見せ方）─────────

    /// <summary>
    /// 連鎖の途中で、1 匹ぶんの釣果を見せたままにする秒数（実時間）。
    /// この秒数が過ぎると、いまの魚の絵が縮んで消え始める（退場クリップ）。
    /// </summary>
    [Header("連鎖"), SerializeField(Label = "連鎖の表示保持(秒)")]
    private float chainHoldSeconds = 1.0f;

    // ─── 魚の絵のアニメ（FishImage の Animator に登録したクリップ）──────
    //
    // 動き（倍率・秒数・カーブ）は<b>クリップが持つ</b>。ここで指定するのは
    // 「どの名前のクリップを流すか」と、待ちの安全網に使う目安の尺だけ。
    // クリップは tools/gen_result_fish_clips.py が生成する。

    /// <summary>
    /// 登場クリップの名前（FishImage の Animator の <c>clips</c> に登録した名前と一致させる）。
    /// 空にすると登場アニメを使わず、絵は即座に表示サイズで出る。
    /// </summary>
    [Header("魚の絵のアニメ"), SerializeField(Label = "登場クリップ名")]
    private string fishInClipName = "result_fish_in";

    /// <summary>
    /// 退場クリップの名前（同上）。空にすると退場アニメを使わず、絵は即座に切り替わる。
    /// </summary>
    [SerializeField(Label = "退場クリップ名")]
    private string fishOutClipName = "result_fish_out";

    /// <summary>
    /// 登場クリップの尺の目安（秒）【待ちの安全網にだけ使う】。
    ///
    /// 実際の切り替わりは<b>Animator の再生終了</b>で判定するので、ここが多少ズレても
    /// 見た目は変わらない。クリップが読めない等で再生が終わらなくなったときに
    /// 進行を止めないための上限（<see cref="FishAnimWaitFactor"/> 倍まで待つ）。
    /// </summary>
    [SerializeField(Label = "登場クリップの尺(秒)")]
    private float fishInSeconds = 0.4f;

    /// <summary>退場クリップの尺の目安（秒）【待ちの安全網にだけ使う】。</summary>
    [SerializeField(Label = "退場クリップの尺(秒)")]
    private float fishOutSeconds = 0.27f;

    // ─── 実行時の状態 ────────────────────────────────────────

    /// <summary>いまのフェーズ。</summary>
    private PanelPhase phase = PanelPhase.Hidden;

    /// <summary>いまのフェーズに入ってからの経過秒数（実時間）。</summary>
    private float phaseElapsed = 0f;

    /// <summary>
    /// いまのフェーズに入ってからの経過秒数（<b>ゲーム時間</b>＝<c>Time.Scale</c> 適用後）。
    /// Animator と同じ時計なので、クリップ待ちの安全網だけがこちらを使う
    /// （理由は <see cref="IsFishClipFinished"/>）。
    /// </summary>
    private float phaseGameElapsed = 0f;

    /// <summary>表示中の内容（<see cref="Hidden"/> のときの値は無意味）。</summary>
    private ResultData data;

    /// <summary>
    /// いま見せている釣果の添字（<see cref="chainEntries"/> の中の位置）
    /// 【連鎖の進行の唯一の状態】。<c>chainEntries.Count - 1</c> が最後の 1 匹。
    /// </summary>
    private int chainIndex = ChainFirstEntryIndex;

    /// <summary>
    /// これまでに見せた魚のうち<b>1 匹でも初捕獲が居たか</b>
    /// 【「図鑑に登録されました」を出すかどうかの唯一の判断材料】。
    ///
    /// 連鎖の魚 1 匹ごとに登録演出を挟むと決定入力が何度も要る（テンポが悪い）ので、
    /// 演出は最後にまとめて 1 回だけ出す。1 匹ずつの初捕獲フラグは
    /// <see cref="ApplyEntry"/> でここへ畳み込む。
    /// </summary>
    private bool chainAnyFirstCatch = false;

    /// <summary>本体の入れ物の <c>CanvasTransform</c>（解決失敗なら <c>IsValid == false</c>）。</summary>
    private SEED.CanvasTransform bodyTransform;

    /// <summary>
    /// 魚の絵の <c>CanvasTransform</c>
    /// （Animator が使えないときに表示サイズを書き込む先。解決失敗なら <c>IsValid == false</c>）。
    /// </summary>
    private SEED.CanvasTransform fishImageTransform;

    /// <summary>
    /// 魚の絵の<b>アクタ</b>（登場するまで隠すために <c>Visible</c> を触る。
    /// 解決失敗なら無効ハンドル）。倍率で隠さない理由は <see cref="SetFishImageVisible"/>。
    /// </summary>
    private SEED.GameObject fishImageRoot;

    /// <summary>魚の絵の Animator（未アタッチ・解決失敗なら null）。</summary>
    private SEED.Animator? fishAnimator;

    /// <summary>
    /// いま魚の絵のクリップが流れているか
    /// （false ＝ 再生できなかったので、待たずに次へ進める）。
    /// </summary>
    private bool fishClipPlaying = false;

    /// <summary>
    /// 登場クリップを流したあと、まだ絵を表示状態へ戻していないか
    /// （1 フレーム遅らせて戻す理由は <see cref="UpdateFishIn"/>）。
    /// </summary>
    private bool fishRevealPending = false;

    /// <summary>
    /// 魚の絵のアニメが使えない旨の警告をもう出したか
    /// 【同じ警告でログを埋めないための番人】。連鎖の切り替えごとに出すと大量に出る。
    /// </summary>
    private bool fishAnimWarned = false;

    /// <summary>図鑑登録パネルのアクタ（解決失敗なら <c>IsValid == false</c>）。</summary>
    private SEED.GameObject registeredRoot;

    /// <summary>図鑑登録パネルの <c>CanvasTransform</c>（解決失敗なら <c>IsValid == false</c>）。</summary>
    private SEED.CanvasTransform registeredTransform;

    /// <summary>新記録のきらめき（解決失敗なら null）。</summary>
    private SEED.ParticleEmitter? newRecordSparkle;

    /// <summary>図鑑登録の紙吹雪（解決失敗なら null）。</summary>
    private SEED.ParticleEmitter? registeredConfetti;

    /// <summary>魚の絵の Sprite（解決失敗なら null）。</summary>
    private SEED.Sprite? fishImage;

    /// <summary>名前の Text（解決失敗なら null）。</summary>
    private SEED.Text? nameText;

    /// <summary>サイズの Text（解決失敗なら null）。</summary>
    private SEED.Text? sizeText;

    /// <summary>自己ベストの Text（解決失敗なら null）。</summary>
    private SEED.Text? bestText;

    /// <summary>ランクの Text（解決失敗なら null）。</summary>
    private SEED.Text? rankText;

    /// <summary>新記録の Text（解決失敗なら null）。</summary>
    private SEED.Text? newRecordText;

    /// <summary>
    /// 新記録テキストを持つ<b>アクタ</b>（解決失敗なら無効ハンドル）。
    ///
    /// <b>なぜ Text だけでなくアクタも持つのか</b>: 文字の<b>影</b>（<c>shadow_*</c>）は
    /// 文字本体の色のアルファとは独立に描かれる。ランタイムの文字描画
    /// （<c>runtime/src/engine/core/font/canvas_text.rs</c> の <c>append_item</c>）は
    /// 「本体が完全透明でも、影が見えるなら描く」という設計になっており、
    /// 影のアルファは <c>shadow_color</c> だけで決まる。
    /// そのため<b>アルファ 0 で隠したはずの「New Record!!」の影だけが画面に残る</b>。
    ///
    /// 隠す正典は<b>アクタの <c>Visible</c></b> にする（描画そのものを止めるので
    /// 本体も影も確実に消える）。アルファ 0 は保険として併用する。
    /// </summary>
    private SEED.GameObject newRecordRoot;

    /// <summary>操作案内の Text（解決失敗なら null）。</summary>
    private SEED.Text? promptText;

    // ─── 静的 API（呼び出し側の入口）──────────────────────────

    /// <summary>
    /// 釣果パネルを<b>1 匹ぶん</b>出す【1 匹だけ見せたいときの入口】。
    /// 中身は「要素 1 つの一覧」として <see cref="ShowChain"/> と同じ経路を通る。
    /// すでに出ているときは内容だけ差し替えて頭から出し直す。
    /// </summary>
    /// <param name="actorPath">パネルのプレハブ（<c>assets://</c> パス）。生成フォールバックにだけ使う。</param>
    /// <param name="showData">表示する内容。</param>
    public static void Show(string actorPath, ResultData showData)
    {
        chainEntries.Clear();
        chainEntries.Add(showData);
        ShowEntries(actorPath);
    }

    /// <summary>
    /// 釣果パネルを<b>複数匹ぶん</b>出す【わらしべ連鎖の表示の唯一の入口】。
    ///
    /// パネルは 1 枚のまま、<paramref name="chain"/> の順に
    /// 「見せる → 絵が縮んで消える → 次の魚の絵が登場 → 文字も次の魚へ」を自動で繰り返し、
    /// <b>最後の 1 匹まで出し終えてから</b>決定入力を待つ。
    /// 「図鑑に登録されました」は、一覧の中に初捕獲が 1 匹でも居れば最後に 1 回だけ出す。
    /// </summary>
    /// <param name="actorPath">パネルのプレハブ（<c>assets://</c> パス）。生成フォールバックにだけ使う。</param>
    /// <param name="chain">
    /// 表示する釣果の一覧（<b>見せる順</b>＝連鎖の最初に掛かった魚から）。
    /// 値はここで写し取るので、呼び出し側は渡したリストをそのまま使い回してよい。
    /// null／空のときは何もしない（出す中身が無いのにパネルを開かない）。
    /// </param>
    public static void ShowChain(string actorPath, System.Collections.Generic.IReadOnlyList<ResultData> chain)
    {
        if (chain is null || chain.Count == 0)
        {
            SEED.Debug.LogWarning("[ResultPanel] 表示する釣果が 1 件も無いため開かない");
            return;
        }

        chainEntries.Clear();
        for (int i = 0; i < chain.Count; i++) { chainEntries.Add(chain[i]); }
        ShowEntries(actorPath);
    }

    /// <summary>
    /// <see cref="chainEntries"/> に積んだ内容でパネルを開く【表示開始の実体】。
    ///
    /// 実体がシーンにあれば即座に表示へ入れ、無ければプレハブを生成して
    /// 内容は次フレームの <see cref="OnStart"/> へ預ける（<see cref="hasPendingData"/>）。
    /// </summary>
    /// <param name="actorPath">パネルのプレハブ（<c>assets://</c> パス）。生成フォールバックにだけ使う。</param>
    private static void ShowEntries(string actorPath)
    {
        // 実体があるなら即座に表示へ入れる（同フレームから見た目が変わる）
        if (Current is { } instance && panelRoot.IsValid)
        {
            SEED.Debug.Log($"[ResultPanel] 表示（既存のインスタンス）: {chainEntries[ChainFirstEntryIndex].Name}"
                         + $" 他 {chainEntries.Count - 1} 匹");
            panelRoot.Visible = true;
            IsActive = true;
            instance.BeginShow();
            return;
        }

        // 実体がまだ無い: プレハブを生成し、内容は次フレームの OnStart へ預ける
        if (!panelRoot.IsValid)
        {
            if (string.IsNullOrWhiteSpace(actorPath))
            {
                SEED.Debug.LogWarning("[ResultPanel] シーンに ResultPanel が無く、プレハブのパスも未設定のため出せない");
                return;
            }
            // 既に同名のパネルアクタがあれば使い回す（SpawnOnce）。
            // ホットリロードで静的状態が初期化されると panelRoot は無効へ戻るため、
            // 素の Instantiate だとリザルトパネルが重なって増えていく。
            panelRoot = SpawnOnce.GetOrInstantiate(PanelActorName, actorPath);
            if (!panelRoot.IsValid)
            {
                SEED.Debug.LogWarning($"[ResultPanel] プレハブを生成できない: {actorPath}");
                return;
            }
        }

        hasPendingData = true;
        IsActive = true;
        SEED.Debug.Log("[ResultPanel] 表示（生成したプレハブの OnStart 待ち）: "
                     + $"{chainEntries[ChainFirstEntryIndex].Name} 他 {chainEntries.Count - 1} 匹");
    }

    /// <summary>
    /// 静的状態をシーン開始時の値へ戻す【取り残しを断ち切る唯一の場所】。
    ///
    /// 静的フィールドはシーン遷移で作り直されないため、
    /// <see cref="OnDestroy"/> と、ゲーム側の初期化から呼んで
    /// 「破棄済みのパネルを掴んだまま表示中扱い」になるのを防ぐ。
    /// </summary>
    public static void ResetStaticState()
    {
        IsActive = false;
        panelRoot = default;
        Current = null;
        hasPendingData = false;
        chainEntries.Clear();
        OnClosed = null;
    }

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 初期化。子アクタの参照を相対パスから解決し、出すまで非表示にする。
    /// 生成フォールバックで作られた実体は、ここで預かった内容を拾って表示へ入る。
    /// </summary>
    public override void OnStart()
    {
        Current = this;
        panelRoot = gameObject;

        ResolveReferences();

        SetContent(newRecordText, newRecordLabel);
        // 出すまでは影ごと消しておく（アルファ 0 では影が残るため Visible で消す）
        SetNewRecordVisible(false);
        // きらめきは新記録のときだけ回す。待機中は必ず止めておく
        // （プレハブ側が playing=true で保存されていても、ここで確実に止まる）。
        SetNewRecordSparklePlaying(false);

        // 出すまでは隠す（シーン上で visible=true のまま保存されていても必ず隠れる）。
        // スケールはここで 0 にする。プレハブ／シーンには原寸（1）で保存しておき、
        // エディタの編集画面では原寸のまま見えるようにする（保存値 0 だと編集画面で消えて見える）。
        phase = PanelPhase.Hidden;
        phaseElapsed = 0f;
        SetRegisteredVisible(false);
        ApplyBodyScale(MinScale);
        ApplyRegisteredScale(MinScale);
        panelRoot.Visible = false;

        // 開発用のデバッグコマンドを登録する（エディタ／MCP から叩ける）。
        // パッケージ版（配布ビルド）では外から演出を送れないよう、登録自体を行わない。
        debugCommandsRegistered = SEED.Application.IsDebugAllowed;
        if (debugCommandsRegistered)
        {
            SEED.Debug.OnCommand(DebugCommandResultConfirm, HandleResultConfirmCommand);
        }

        // 生成フォールバック経由なら、預かっていた内容でそのまま表示へ入る
        if (!hasPendingData)
        {
            SEED.Debug.Log("[ResultPanel] OnStart（待機中の内容なし＝シーン配置のインスタンス）");
            return;
        }
        hasPendingData = false;
        panelRoot.Visible = true;
        IsActive = true;
        SEED.Debug.Log($"[ResultPanel] OnStart（待機中の内容を表示）: {chainEntries.Count} 匹");
        BeginShow();
    }

    /// <summary>破棄時の後始末。自分が現役のときだけ静的状態を戻す。</summary>
    public override void OnDestroy()
    {
        // 破棄したスクリプトのハンドラが呼ばれ続けないよう、必ず外す
        //（登録したときだけ外して、登録・解除を対称に保つ）。
        if (debugCommandsRegistered)
        {
            debugCommandsRegistered = false;
            SEED.Debug.OffCommand(DebugCommandResultConfirm, HandleResultConfirmCommand);
        }
        if (ReferenceEquals(Current, this)) { ResetStaticState(); }
    }

    // ─── デバッグコマンド（開発・AI 検証用）──────────────────

    /// <summary>
    /// デバッグコマンドを登録済みか（パッケージ版では登録しないので常に false）。
    /// <see cref="OnDestroy"/> の解除を登録と対称にするために持つ。
    /// </summary>
    private bool debugCommandsRegistered;

    /// <summary>
    /// デバッグコマンド名: 決定入力の代わりにパネルを次へ進める。
    /// <c>seed_script_debug(name:"result_confirm")</c> で叩く。
    /// </summary>
    private const string DebugCommandResultConfirm = "result_confirm";

    /// <summary>
    /// <see cref="DebugCommandResultConfirm"/> のハンドラ
    /// 【パネルの開閉を検証するための唯一の近道】。
    ///
    /// 実際の決定入力と同じ分岐（<see cref="AdvanceOnConfirm"/>）を、
    /// 入力の待ち時間と <see cref="InputGate"/> だけ飛ばして呼ぶ。
    /// 進行の中身は本物と同じなので、ここで確認した見た目は本番でもそのまま出る
    /// （チュートリアル中で決定が塞がれていても検証できる）。
    /// </summary>
    /// <param name="arg">未使用（コマンドの引数は取らない）。</param>
    private void HandleResultConfirmCommand(string arg)
    {
        SEED.Debug.Log($"[ResultPanel] result_confirm: phase={phase}");
        AdvanceOnConfirm();
    }

    /// <summary>
    /// 毎フレームの更新。拡大縮小・点滅・決定入力をすべて<b>実時間</b>で進める
    /// （釣り上げ演出はスロー区間を含むため、ゲーム時間では速度が変わってしまう）。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (phase == PanelPhase.Hidden) { return; }

        phaseElapsed += SEED.Time.UnscaledDeltaTime;
        phaseGameElapsed += SEED.Time.DeltaTime;
        UpdateBlink();

        switch (phase)
        {
            case PanelPhase.Opening:           UpdateOpening();           break;
            case PanelPhase.FishIn:            UpdateFishIn();            break;
            case PanelPhase.ChainHolding:      UpdateChainHolding();      break;
            case PanelPhase.FishOut:           UpdateFishOut();           break;
            case PanelPhase.Idle:              UpdateIdle();              break;
            case PanelPhase.RegisteredOpening: UpdateRegisteredOpening(); break;
            case PanelPhase.RegisteredIdle:    UpdateIdle();              break;
            case PanelPhase.RegisteredClosing: UpdateRegisteredClosing(); break;
            case PanelPhase.Closing:           UpdateClosing();           break;
        }
    }

    // ─── フェーズごとの更新 ──────────────────────────────────

    /// <summary>
    /// 本体の拡大（easeOutBack で 0 → 原寸）。
    /// 出きったら、いま見せる魚の絵を登場させる（1 匹目も必ず登場アニメを通る）。
    /// </summary>
    private void UpdateOpening()
    {
        float ratio = Progress(openSeconds);
        ApplyBodyScale(EaseOutBack(ratio));
        if (ratio < 1f) { return; }
        EnterPhase(PanelPhase.FishIn);
    }

    /// <summary>
    /// 魚の絵の登場（<c>result_fish_in</c> の再生中）。再生し切ったら
    /// <see cref="FinishFishIn"/> が文字をこの魚のものへ確定させて次へ進む。
    ///
    /// <b>絵を表示へ戻すのがここ（＝再生を始めた次のフレーム）である理由</b>:
    /// エンジンの AnimationSystem はスクリプトより<b>前</b>に走るので、
    /// <see cref="EnterPhase"/> で <c>Play</c> した瞬間はまだクリップの 1 コマ目
    /// （画面いっぱいの倍率）が書かれていない。そこで表示へ戻すと、
    /// <b>前の魚が残した倍率のまま 1 フレームだけ絵が見えてしまう</b>。
    /// </summary>
    private void UpdateFishIn()
    {
        RevealFishIfPending();
        if (!IsFishClipFinished(fishInSeconds)) { return; }
        FinishFishIn();
    }

    /// <summary>
    /// 連鎖の「見せたまま待つ」区間。<see cref="chainHoldSeconds"/> 秒たったら
    /// いまの絵を縮めて消し始める。
    /// </summary>
    private void UpdateChainHolding()
    {
        if (phaseElapsed < SEED.Mathf.Max(chainHoldSeconds, 0f)) { return; }
        EnterPhase(PanelPhase.FishOut);
    }

    /// <summary>
    /// 魚の絵の退場（<c>result_fish_out</c> の再生中）。消え切ったら
    /// <see cref="CommitChainEntry"/> が絵だけを次の魚へ差し替える（＝見えない裏で入れ替わる）。
    /// </summary>
    private void UpdateFishOut()
    {
        if (!IsFishClipFinished(fishOutSeconds)) { return; }
        CommitChainEntry();
    }

    /// <summary>図鑑登録パネルの拡大（easeOutBack で 0 → 原寸）。出きったら決定待ちへ。</summary>
    private void UpdateRegisteredOpening()
    {
        float ratio = Progress(registeredOpenSeconds);
        ApplyRegisteredScale(EaseOutBack(ratio));
        if (ratio < 1f) { return; }
        EnterPhase(PanelPhase.RegisteredIdle);
    }

    /// <summary>決定入力の待ち。押されたら <see cref="AdvanceOnConfirm"/> が次を決める。</summary>
    private void UpdateIdle()
    {
        if (!IsConfirmPressed()) { return; }
        AdvanceOnConfirm();
    }

    /// <summary>
    /// 決定が入ったときに次へ進む【次に何が起きるかを決める唯一の分岐】。
    ///
    /// - 本体だけ出ていて<b>初捕獲</b>なら … 図鑑登録パネルを開く
    /// - 本体だけ出ていて初捕獲でないなら … 全体を閉じる
    /// - 図鑑登録パネルまで出ているなら … まず登録パネルを畳み、その後で本体を閉じる
    ///
    /// 入力の可否（待ち時間・<see cref="InputGate"/>）は呼び出し側の責務。
    /// 待ち受け中でないフェーズで呼ばれても何もしない。
    /// </summary>
    private void AdvanceOnConfirm()
    {
        switch (phase)
        {
            case PanelPhase.Idle:
                // 図鑑登録は「連鎖の中に初捕獲が 1 匹でも居たか」で決める
                //（1 匹ずつ演出を挟まず、最後にまとめて 1 回だけ出す）
                EnterPhase(chainAnyFirstCatch ? PanelPhase.RegisteredOpening : PanelPhase.Closing);
                break;

            case PanelPhase.RegisteredIdle:
                EnterPhase(PanelPhase.RegisteredClosing);
                break;
        }
    }

    /// <summary>
    /// 図鑑登録パネルの縮小（easeInBack で原寸 → 0）。
    /// 縮み切ったら登録パネルを隠し、続けて本体を閉じる。
    /// </summary>
    private void UpdateRegisteredClosing()
    {
        float ratio = Progress(registeredCloseSeconds);
        ApplyRegisteredScale(1f - EaseInBack(ratio));
        if (ratio < 1f) { return; }

        // 縮み切った状態を確定させてから本体の閉じへ渡す
        // （中途半端なスケールのまま隠すと、次に開いたとき一瞬その大きさで見える）。
        ApplyRegisteredScale(MinScale);
        SetRegisteredVisible(false);
        EnterPhase(PanelPhase.Closing);
    }

    /// <summary>全体の縮小（easeInBack で原寸 → 0）。縮み切ったら非表示にして通知する。</summary>
    private void UpdateClosing()
    {
        float ratio = Progress(closeSeconds);
        ApplyBodyScale(1f - EaseInBack(ratio));
        if (ratio < 1f) { return; }
        FinishClose();
    }

    // ─── フェーズ遷移 ────────────────────────────────────────

    /// <summary>
    /// 表示を頭から始める【内容差し替えの唯一の入口】。
    /// 見せる中身は <see cref="chainEntries"/>（先頭から順に見せる）。
    /// すでに出ている最中に呼ばれても、内容を差し替えて開き直す。
    /// </summary>
    private void BeginShow()
    {
        if (chainEntries.Count == 0)
        {
            // 呼び出し側の組み立て漏れ。開かずに「閉じ終わった」ことにして進行を止めない。
            SEED.Debug.LogWarning("[ResultPanel] 見せる釣果が空のまま開こうとした（何も出さずに閉じる）");
            FinishClose();
            return;
        }

        chainIndex = ChainFirstEntryIndex;
        chainAnyFirstCatch = false;
        ApplyEntry(chainEntries[ChainFirstEntryIndex]);

        SetRegisteredVisible(false);
        ApplyRegisteredScale(MinScale);
        // 絵は「開き切ってから登場アニメで出す」ので、開いているあいだは隠しておく
        HideFishImageUntilEntrance();
        EnterPhase(PanelPhase.Opening);
        ApplyBodyScale(MinScale);   // 開きの初期値（1 フレーム目に原寸で見えるのを防ぐ）
    }

    // ─── 連鎖（複数匹を 1 枚のパネルで見せる）────────────────────

    /// <summary>
    /// いま見せている魚を見せ終えたあとに入るフェーズ
    /// 【連鎖を続けるか決定待ちにするかの唯一の分岐】。
    /// </summary>
    /// <returns>まだ次の魚が残っていれば <see cref="PanelPhase.ChainHolding"/>、無ければ <see cref="PanelPhase.Idle"/>。</returns>
    private PanelPhase NextPhaseAfterEntry()
        => chainIndex + 1 < chainEntries.Count ? PanelPhase.ChainHolding : PanelPhase.Idle;

    /// <summary>
    /// 表示中の内容を 1 匹ぶん差し替える【表示中の魚を切り替える唯一の場所】。
    /// 初捕獲フラグはここで <see cref="chainAnyFirstCatch"/> へ畳み込む。
    /// </summary>
    /// <param name="entry">見せる内容。</param>
    private void ApplyEntry(ResultData entry)
    {
        data = entry;
        chainAnyFirstCatch = chainAnyFirstCatch || entry.FirstCatch;
        ApplyData();
    }

    /// <summary>
    /// 絵が消え切った瞬間に、<b>絵だけ</b>を次の魚へ差し替える【連鎖を 1 つ進める唯一の場所】。
    ///
    /// 文字（名前・サイズ・自己ベスト・New Record・ランク）はまだ前の魚のままにしておき、
    /// 次の登場アニメが終わった瞬間（<see cref="FinishFishIn"/>）にまとめて切り替える。
    /// </summary>
    private void CommitChainEntry()
    {
        // 一覧が途中で作り直された（ホットリロード・シーン遷移）などの異常時の番人。
        // 添字が範囲を越えることは通常あり得ないが、越えたら決定待ちへ落として進行を止めない。
        if (chainIndex + 1 >= chainEntries.Count)
        {
            EnterPhase(PanelPhase.Idle);
            return;
        }

        chainIndex++;
        // 絵は消えている（倍率 0）ので、ここで差し替えても入れ替わりは見えない
        ApplyFishImage(chainEntries[chainIndex].ImagePath);
        EnterPhase(PanelPhase.FishIn);
    }

    /// <summary>
    /// 登場アニメが終わった瞬間の確定処理
    /// 【文字（名前・サイズ・自己ベスト・New Record・ランク）を切り替える唯一の場所】。
    ///
    /// 絵は登場アニメが始まる前に差し替え済みなので、ここで変わるのは文字と
    /// 新記録まわり（点滅・きらめき）だけ。1 匹目はすでに同じ内容が入っているため
    /// 見た目は変わらない（同じ経路を通すことで分岐を増やさない）。
    /// </summary>
    private void FinishFishIn()
    {
        // 一覧が途中で作り直された（ホットリロード・シーン遷移）などの異常時の番人。
        if (chainIndex >= ChainFirstEntryIndex && chainIndex < chainEntries.Count)
        {
            ApplyEntry(chainEntries[chainIndex]);
        }

        // 本体はもう原寸なので、きらめきはこの瞬間から出してよい
        //（開き途中に出さない理由は EnterPhase の Idle を参照）
        SetNewRecordSparklePlaying(data.NewRecord);

        EnterPhase(NextPhaseAfterEntry());
    }

    /// <summary>
    /// フェーズを切り替える【遷移の唯一の入口】。経過秒数を必ず 0 に戻し、
    /// 入った瞬間だけ行う処理をここへ集約する。
    /// </summary>
    /// <param name="next">次のフェーズ。</param>
    private void EnterPhase(PanelPhase next)
    {
        phase = next;
        phaseElapsed = 0f;
        phaseGameElapsed = 0f;

        switch (next)
        {
            case PanelPhase.FishIn:
                // 絵の登場はクリップが作る（倍率・秒数・カーブはすべてクリップが持つ）。
                // 表示へ戻すのは次のフレーム（理由は UpdateFishIn）。
                // 流せなかったときはアニメを諦め、そのまま表示サイズで出す。
                fishRevealPending = TryPlayFishClip(fishInClipName);
                if (!fishRevealPending) { ShowFishImmediately(); }
                break;

            case PanelPhase.FishOut:
                // 絵の退場もクリップが作る。流せなければ待たずに次の魚へ進む
                //（IsFishClipFinished が fishClipPlaying を見て即座に true を返す）。
                TryPlayFishClip(fishOutClipName);
                break;

            case PanelPhase.Idle:
                // 本体が開き切ってからきらめかせる（開き途中は入れ物のスケールが
                // 小さく、粒まで潰れて見えてしまうため）。新記録でなければ出さない。
                SetNewRecordSparklePlaying(data.NewRecord);
                break;

            case PanelPhase.RegisteredOpening:
                // 図鑑登録パネルはここで初めて姿を現す（0 スケールから膨らませる）
                SetRegisteredVisible(true);
                ApplyRegisteredScale(MinScale);
                SetContent(promptText, promptAfterRegisteredLabel);
                // 「登録された」瞬間の紙吹雪は、パネルの出現と同じ瞬間に弾く
                BurstRegisteredConfetti();
                break;

            case PanelPhase.Closing:
                // 閉じ始めたら案内は消す（閉じ切るまで押せる文言が残るのを避ける）
                SetTextAlpha(promptText, AlphaClear);
                // 閉じ始めたらきらめきも止める（閉じ切ったあとに粒が残らない）
                SetNewRecordSparklePlaying(false);
                break;
        }
    }

    /// <summary>
    /// 閉じ切ったときの後始末【終了の唯一の出口】。
    /// 非表示へ戻し、<see cref="IsActive"/> を下ろして <see cref="OnClosed"/> を 1 回呼ぶ。
    /// </summary>
    private void FinishClose()
    {
        phase = PanelPhase.Hidden;
        phaseElapsed = 0f;
        phaseGameElapsed = 0f;
        ApplyBodyScale(MinScale);
        SetNewRecordSparklePlaying(false);
        SetRegisteredVisible(false);
        if (panelRoot.IsValid) { panelRoot.Visible = false; }

        IsActive = false;
        SEED.Debug.Log("[ResultPanel] 閉じ終わり");

        var notify = OnClosed;
        OnClosed = null;         // 1 回きりの通知（同じ購読が次の釣果へ持ち越されないように）
        notify?.Invoke();
    }

    // ─── 表示内容の流し込み ──────────────────────────────────

    /// <summary>
    /// <see cref="data"/> の内容を各コンポーネントへ流し込む【表示内容を決める唯一の場所】。
    /// 項目を増やすときはここへ 1 行足す。
    /// </summary>
    private void ApplyData()
    {
        SetContent(nameText, data.Name);
        SetContent(sizeText, data.SizeText);
        SetContent(bestText, data.BestText);
        SetContent(rankText, data.RankText);
        ApplyRankColor();
        SetContent(promptText, promptLabel);
        SetTextAlpha(promptText, AlphaOpaque);

        // 新記録は「新記録のときだけ」出す。
        // 出さないときはアクタごと非表示にする（アルファ 0 だけだと影が残るため。
        // 詳細は newRecordRoot のコメントを参照）。アルファ 0 も保険として併用する。
        SetContent(newRecordText, newRecordLabel);
        SetTextAlpha(newRecordText, data.NewRecord ? AlphaOpaque : AlphaClear);
        SetNewRecordVisible(data.NewRecord);

        ApplyFishImage(data.ImagePath);
    }

    /// <summary>
    /// 魚の絵のテクスチャを差し替える【絵を載せ替える唯一の場所】。
    /// 図鑑画像のパスが空なら <see cref="fallbackImagePath"/>（既定は白）を使う
    /// （テクスチャを空文字にすると単色表示になり、絵の枠が消えて見えるため）。
    /// </summary>
    /// <param name="imagePath">載せる図鑑画像の <c>assets://</c> パス。</param>
    private void ApplyFishImage(string imagePath)
    {
        if (fishImage is not { } sprite || !sprite.IsValid) { return; }
        sprite.TexturePath = string.IsNullOrWhiteSpace(imagePath) ? fallbackImagePath : imagePath;
    }

    /// <summary>
    /// 「New Record!!」の点滅を進める（新記録でないときは何もしない）。
    /// 明るさは <see cref="blinkMinAlpha"/> 〜 1 を <see cref="blinkPeriodSeconds"/> で往復する。
    /// </summary>
    private void UpdateBlink()
    {
        if (!data.NewRecord) { return; }
        if (newRecordText is not { } text || !text.IsValid) { return; }

        float period = SEED.Mathf.Max(blinkPeriodSeconds, DivideEpsilon);
        // PingPong は 0 → amplitude → 0 を周期 2×amplitude で往復するので、
        // 実時間を「半周期あたり 1」へ写してから掛ける（＝period 秒で 1 往復）。
        float wave = SEED.Mathf.PingPong(
            SEED.Time.UnscaledElapsedTime / period * (BlinkAmplitude + BlinkAmplitude), BlinkAmplitude);
        float minAlpha = SEED.Mathf.Clamped01(blinkMinAlpha);
        SetTextAlpha(text, minAlpha + (AlphaOpaque - minAlpha) * wave);
    }

    // ─── 見た目の操作 ────────────────────────────────────────

    /// <summary>本体の入れ物のスケールを設定する（縦横同倍率・負値は 0 へ丸める）。</summary>
    /// <param name="scale">適用する倍率（0＝消える / 1＝原寸）。</param>
    private void ApplyBodyScale(float scale)
    {
        if (!bodyTransform.IsValid) { return; }
        float safe = SEED.Mathf.Max(scale, MinScale);
        bodyTransform.Scale = new SEED.Vector2(safe, safe);
    }

    /// <summary>図鑑登録パネルのスケールを設定する（縦横同倍率・負値は 0 へ丸める）。</summary>
    /// <param name="scale">適用する倍率。</param>
    private void ApplyRegisteredScale(float scale)
    {
        if (!registeredTransform.IsValid) { return; }
        float safe = SEED.Mathf.Max(scale, MinScale);
        registeredTransform.Scale = new SEED.Vector2(safe, safe);
    }

    /// <summary>図鑑登録パネルの表示・非表示を切り替える（子孫までまとめて効く）。</summary>
    /// <param name="visible">表示するか。</param>
    private void SetRegisteredVisible(bool visible)
    {
        if (!registeredRoot.IsValid) { return; }
        registeredRoot.Visible = visible;
    }

    // ─── 魚の絵のアニメ（Animator に登録したクリップを流すだけ）─────────
    //
    // 動き（倍率・秒数・カーブ）は .anim クリップが持ち、ここには一切書かない。
    // このスクリプトの仕事は「いつどちらのクリップを流すか」「終わったか」だけ。

    /// <summary>魚の絵のアニメが使えるか（Animator を解決できていて生きているか）。</summary>
    private bool HasFishAnimator() => fishAnimator is { IsValid: true };

    /// <summary>
    /// 魚の絵のクリップを流す【再生を頼む唯一の場所】。
    ///
    /// 流せたかは<b>その場で</b>確かめる。Animator は未登録・未ロードのクリップ名を
    /// 渡されても（警告を出して）何もしないので、頼んだ直後に「そのクリップが
    /// 現在クリップになったか」を見れば分かる。流せなければ
    /// <see cref="fishClipPlaying"/> を下ろし、呼び出し側は待たずに次へ進む
    /// （＝演出は省かれるが、連鎖の進行は必ず最後まで通る）。
    /// </summary>
    /// <param name="clipName">流すクリップ名（空なら演出を使わない）。</param>
    /// <returns>再生を始められたら true。</returns>
    private bool TryPlayFishClip(string clipName)
    {
        fishClipPlaying = false;

        if (string.IsNullOrWhiteSpace(clipName)) { return false; }   // 未設定 ＝ 演出を使わない
        if (fishAnimator is not { IsValid: true } animator)
        {
            WarnFishAnimOnce($"魚の絵に Animator が無い（{fishImagePath}）ため、絵のアニメを省く");
            return false;
        }

        animator.Play(clipName);

        // Play が弾かれた（clips 未登録・.anim 未ロード）ときは現在クリップが変わらない
        if (animator.CurrentClip != clipName || !animator.IsPlaying)
        {
            WarnFishAnimOnce($"クリップ '{clipName}' を再生できないため、絵のアニメを省く");
            return false;
        }

        fishClipPlaying = true;
        return true;
    }

    /// <summary>
    /// 流している魚の絵のクリップが終わったか【アニメ待ちの唯一の判定】。
    ///
    /// 正典は<b>Animator の再生状態</b>（尺の末尾に達するとエンジンが自動で下ろす）。
    /// ただし .anim が読めない等で「再生中のまま終わらない」状況もあり得るので、
    /// 上限を設けて必ず先へ進めるようにする。
    ///
    /// 上限を<b>ゲーム時間</b>（<see cref="phaseGameElapsed"/>）で数えるのは、
    /// Animator がゲーム時間で進むため。実時間で数えると、ポーズ中
    /// （<c>Time.Scale</c> = 0 でクリップも止まっている間）に上限へ達してしまい、
    /// 「止まっているのに次へ進む」ことになる。
    /// </summary>
    /// <param name="clipSeconds">そのクリップの尺の目安（秒）。</param>
    /// <returns>次へ進んでよいなら true。</returns>
    private bool IsFishClipFinished(float clipSeconds)
    {
        if (!fishClipPlaying) { return true; }                          // 流せなかった ＝ 待たない
        if (fishAnimator is not { IsValid: true } animator) { return true; }
        if (!animator.IsPlaying) { return true; }                       // 通常の終了

        float limit = SEED.Mathf.Max(clipSeconds, 0f) * FishAnimWaitFactor + FishAnimWaitMarginSeconds;
        if (phaseGameElapsed < limit) { return false; }

        WarnFishAnimOnce($"クリップ '{animator.CurrentClip}' が {limit} 秒たっても終わらないため、待たずに次へ進む");
        return true;
    }

    /// <summary>
    /// 登場アニメを始めた<b>次の</b>フレームで魚の絵を表示へ戻す
    /// （1 フレーム遅らせる理由は <see cref="UpdateFishIn"/>）。
    /// </summary>
    private void RevealFishIfPending()
    {
        if (!fishRevealPending) { return; }
        fishRevealPending = false;
        SetFishImageVisible(true);
    }

    /// <summary>
    /// 登場アニメが始まるまで魚の絵を隠す
    /// 【開いた瞬間に絵が出てしまわないようにする唯一の場所】。
    ///
    /// アニメが使えないときは<b>隠さない</b>。隠すと登場アニメが無いぶん
    /// 絵が二度と出てこなくなるため、その場合は表示サイズで出しっぱなしにする。
    /// </summary>
    private void HideFishImageUntilEntrance()
    {
        if (!HasFishAnimator())
        {
            ShowFishImmediately();
            return;
        }

        fishRevealPending = false;
        SetFishImageVisible(false);
    }

    /// <summary>
    /// アニメを使わずに、いますぐ絵を出す【フォールバックの唯一の出口】。
    /// Animator が無い／クリップを流せないときに呼ぶ。
    ///
    /// <b>Animator が居るなら先に止める</b>: 退場クリップだけが流せる（登場だけ名前が違う等の）
    /// 半端な設定だと、直前の退場で倍率 0 のまま毎フレーム書かれ続け、
    /// ここで書いた表示サイズが上書きされて<b>絵が二度と出てこない</b>。
    /// <c>Stop</c> は再生位置を先頭へ戻すので、以後はそのクリップの<b>1 コマ目</b>
    /// （退場クリップなら表示サイズ）が書かれ、絵は必ず見える状態に落ち着く。
    /// </summary>
    private void ShowFishImmediately()
    {
        fishRevealPending = false;
        if (fishAnimator is { IsValid: true } animator) { animator.Stop(); }
        ApplyFishScale(FishShownScale);
        SetFishImageVisible(true);
    }

    /// <summary>
    /// 魚の絵の表示・非表示を切り替える【絵を隠す唯一の手段】。
    ///
    /// <b>倍率で隠さない理由</b>: エンジンはクリップが終わっても
    /// 「最後のコマの値」を毎フレーム書き続ける（現在クリップがある限り評価するため）。
    /// したがってスクリプトが 1 回書いた倍率は次のフレームで上書きされてしまう。
    /// クリップが触らない <c>Visible</c> なら確実に隠せる。
    /// </summary>
    /// <param name="visible">表示するなら true。</param>
    private void SetFishImageVisible(bool visible)
    {
        // GameObject はプロパティ経由だと構造体のコピーへ書くことになる（CS1612）ため
        // ローカルへ受けてから設定する（<see cref="SetNewRecordVisible"/> と同じ理由）。
        var root = fishImageRoot;
        if (!root.IsValid) { return; }
        root.Visible = visible;
    }

    /// <summary>
    /// 魚の絵の倍率を直接書き込む【Animator が使えないときだけ通る道】。
    /// クリップが流れているあいだに呼ぶと次のフレームで上書きされる
    /// （理由は <see cref="SetFishImageVisible"/>）。
    /// </summary>
    /// <param name="scale">適用する倍率（0＝消える / 1＝表示サイズ）。</param>
    private void ApplyFishScale(float scale)
    {
        if (!fishImageTransform.IsValid) { return; }
        float safe = SEED.Mathf.Max(scale, MinScale);
        fishImageTransform.Scale = new SEED.Vector2(safe, safe);
    }

    /// <summary>
    /// 魚の絵のアニメについての警告を<b>1 回だけ</b>出す
    /// 【同じ警告でログを埋めないための番人】。
    /// 連鎖の切り替えごとに出すと、1 回の釣果で何行も同じ警告が並ぶため。
    /// </summary>
    /// <param name="message">出す文言（接頭辞はここで付ける）。</param>
    private void WarnFishAnimOnce(string message)
    {
        if (fishAnimWarned) { return; }
        fishAnimWarned = true;
        SEED.Debug.LogWarning($"[ResultPanel] {message}");
    }

    // ─── 入力 ────────────────────────────────────────────────

    /// <summary>
    /// 決定入力（Enter / 左クリック）が押されたか【入力判定の唯一の場所】。
    ///
    /// フェーズへ入ってから <see cref="inputDelaySeconds"/> 秒は必ず無視する
    /// （直前の演出で押したクリックが持ち越されて即座に閉じるのを防ぐ）。
    /// チュートリアル中は <see cref="InputGate"/> で止められる。
    /// </summary>
    private bool IsConfirmPressed()
    {
        if (phaseElapsed < SEED.Mathf.Max(inputDelaySeconds, 0f)) { return false; }
        if (!InputGate.Allows(GameAction.UiConfirm)) { return false; }
        return SEED.Input.GetKeyDown(SEED.KeyCode.Enter)
            || SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left);
    }

    // ─── 参照の解決（相対パス → ハンドル）──────────────────────

    /// <summary>
    /// インスペクタの相対パス文字列から子アクタのコンポーネントを解決する
    /// 【子参照を作る唯一の場所】。書式は <see cref="PauseMenu"/> と同じ。
    /// </summary>
    private void ResolveReferences()
    {
        SEED.GameObject bodyActor = ResolveActor(bodyPath);
        bodyTransform = bodyActor.IsValid
            ? bodyActor.GetComponent<SEED.CanvasTransform>() ?? default
            : default;
        if (!bodyTransform.IsValid)
        {
            SEED.Debug.LogWarning($"[ResultPanel] 本体の入れ物を解決できない: {bodyPath}");
        }

        registeredRoot = ResolveActor(registeredPath);
        registeredTransform = registeredRoot.IsValid
            ? registeredRoot.GetComponent<SEED.CanvasTransform>() ?? default
            : default;

        // 魚の絵は「隠す（Visible）」「アニメを流す（Animator）」「アニメが使えないときに
        // 倍率を書く（CanvasTransform）」の 3 つを触るので、アクタごと持っておく。
        fishImageRoot = ResolveActor(fishImagePath);
        fishImageTransform = fishImageRoot.IsValid
            ? fishImageRoot.GetComponent<SEED.CanvasTransform>() ?? default
            : default;
        fishAnimator = fishImageRoot.IsValid ? fishImageRoot.GetComponent<SEED.Animator>() : null;
        if (!HasFishAnimator())
        {
            // 出す前（OnStart）に気づけるよう、ここでも 1 回だけ知らせる
            WarnFishAnimOnce($"魚の絵に Animator が無い（{fishImagePath}）ため、絵のアニメを省く");
        }

        fishImage     = ResolveSprite(fishImagePath);
        nameText      = ResolveText(namePath);
        sizeText      = ResolveText(sizePath);
        bestText      = ResolveText(bestPath);
        rankText      = ResolveText(rankPath);
        newRecordText = ResolveText(newRecordPath);
        newRecordRoot = ResolveActor(newRecordPath);
        promptText    = ResolveText(promptPath);

        // 粒は「無くても情報は伝わる」飾りなので、解決できなくても警告だけで先へ進む。
        newRecordSparkle   = ResolveEmitter(newRecordSparklePath);
        registeredConfetti = ResolveEmitter(registeredConfettiPath);
    }

    /// <summary>参照文字列から ParticleEmitter を解決する（見つからなければ警告して null）。</summary>
    /// <param name="reference">参照文字列（例 <c>./ResultBody/NewRecordSparkle|ParticleEmitter</c>）。</param>
    private SEED.ParticleEmitter? ResolveEmitter(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) { return null; }
        SEED.GameObject owner = ResolveActor(reference);
        if (!owner.IsValid)
        {
            SEED.Debug.LogWarning($"[ResultPanel] 粒のアクタを解決できない: {reference}");
            return null;
        }
        SEED.ParticleEmitter? emitter = owner.GetComponent<SEED.ParticleEmitter>();
        if (emitter is null)
        {
            SEED.Debug.LogWarning($"[ResultPanel] ParticleEmitter スロットが見つからない: {reference}");
        }
        return emitter;
    }

    // ─── 粒（パーティクル）の制御 ─────────────────────────────

    /// <summary>
    /// 新記録のきらめきを出す／止める【きらめきの唯一の切替点】。
    /// 止めても既に出ている粒は寿命で自然に消える（急に消えない）。
    /// </summary>
    /// <param name="playing">出すなら true。</param>
    private void SetNewRecordSparklePlaying(bool playing)
    {
        if (newRecordSparkle is not { } e || !e.IsValid) { return; }
        if (playing) { e.Play(); } else { e.Stop(); }
    }

    /// <summary>
    /// 図鑑登録の紙吹雪を一度だけ弾く。
    ///
    /// 放出要求（<c>Burst</c>）は<b>非表示のあいだ消費されない</b>
    /// （GPU パーティクルは非表示のサブツリーを収集しないため）。
    /// したがって登録パネルを表示へ切り替えた直後に積んでよく、
    /// パネルが最初に描かれるフレームでまとめて弾ける。
    /// </summary>
    private void BurstRegisteredConfetti()
    {
        if (registeredConfettiCount <= 0) { return; }
        if (registeredConfetti is not { } e || !e.IsValid) { return; }
        e.Burst(registeredConfettiCount);
    }

    /// <summary>参照文字列（<c>./Child/Grand</c> 形式。<c>|スロット名</c> は無視）からアクタを解決する。</summary>
    /// <param name="reference">参照文字列。空なら無効なハンドルを返す。</param>
    private SEED.GameObject ResolveActor(string reference)
    {
        string actorPath = SplitActorPath(reference);
        if (string.IsNullOrWhiteSpace(actorPath)) { return default; }
        return gameObject.FindChild(actorPath);
    }

    /// <summary>参照文字列から Text コンポーネントを解決する（見つからなければ null）。</summary>
    /// <param name="reference">参照文字列（例 <c>./ResultBody/FishName|Text</c>）。</param>
    private SEED.Text? ResolveText(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) { return null; }

        SEED.GameObject owner = ResolveActor(reference);
        if (!owner.IsValid)
        {
            SEED.Debug.LogWarning($"[ResultPanel] テキストのアクタを解決できない: {reference}");
            return null;
        }

        string slot = SplitSlotName(reference);
        SEED.Text? text = string.IsNullOrEmpty(slot)
            ? owner.GetComponent<SEED.Text>()
            : owner.GetComponent<SEED.Text>(slot);
        if (text is null) { SEED.Debug.LogWarning($"[ResultPanel] Text スロットが見つからない: {reference}"); }
        return text;
    }

    /// <summary>参照文字列から Sprite コンポーネントを解決する（見つからなければ null）。</summary>
    /// <param name="reference">参照文字列（例 <c>./ResultBody/FishImage|Sprite</c>）。</param>
    private SEED.Sprite? ResolveSprite(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) { return null; }

        SEED.GameObject owner = ResolveActor(reference);
        if (!owner.IsValid)
        {
            SEED.Debug.LogWarning($"[ResultPanel] スプライトのアクタを解決できない: {reference}");
            return null;
        }

        string slot = SplitSlotName(reference);
        SEED.Sprite? sprite = string.IsNullOrEmpty(slot)
            ? owner.GetComponent<SEED.Sprite>()
            : owner.GetComponent<SEED.Sprite>(slot);
        if (sprite is null) { SEED.Debug.LogWarning($"[ResultPanel] Sprite スロットが見つからない: {reference}"); }
        return sprite;
    }

    /// <summary>参照文字列のうち、区切り文字より前の「アクタパス」部分。</summary>
    /// <param name="reference">参照文字列。</param>
    private static string SplitActorPath(string reference)
    {
        if (string.IsNullOrEmpty(reference)) { return string.Empty; }
        int sep = reference.IndexOf(ReferenceSlotSeparator);
        return sep < 0 ? reference : reference.Substring(0, sep);
    }

    /// <summary>参照文字列のうち、区切り文字より後の「スロット名」部分（無ければ空文字）。</summary>
    /// <param name="reference">参照文字列。</param>
    private static string SplitSlotName(string reference)
    {
        if (string.IsNullOrEmpty(reference)) { return string.Empty; }
        int sep = reference.IndexOf(ReferenceSlotSeparator);
        return sep < 0 ? string.Empty : reference.Substring(sep + 1);
    }

    // ─── 汎用ヘルパー ────────────────────────────────────────

    /// <summary>
    /// いまのフェーズの進行度（0〜1）。秒数が実質 0 なら即座に 1 を返す。
    /// </summary>
    /// <param name="seconds">そのフェーズに掛ける秒数。</param>
    private float Progress(float seconds)
    {
        float span = SEED.Mathf.Max(seconds, 0f);
        return span <= DivideEpsilon ? 1f : SEED.Mathf.Clamped01(phaseElapsed / span);
    }

    /// <summary>
    /// ランクのテキストを、サイズランクに対応した色で塗る
    /// 【リザルト側のランク配色の唯一の場所】。
    ///
    /// 対応表（ランク文字 → どの色か）は図鑑カードと共有する
    /// <see cref="RankColorTable"/> に任せ、ここは色の実体（16 進文字列）を渡すだけ。
    /// アルファは触らない（パネルのフェードが所有しているため）。
    /// </summary>
    private void ApplyRankColor()
    {
        string hex = RankColorTable.Select(
            data.RankKey, rankColorS, rankColorA, rankColorB, rankColorC, rankColorUnknown);
        UiColorUtil.ApplyRgb(rankText, hex);
    }

    /// <summary>
    /// 新記録テキストのアクタを表示／非表示にする【影ごと消すための唯一の入口】。
    /// 解決できていなければ何もしない。
    /// </summary>
    /// <param name="visible">表示するなら true。</param>
    private void SetNewRecordVisible(bool visible)
    {
        // GameObject はプロパティ経由だと構造体のコピーへ書くことになる（CS1612）ため
        // ローカルへ受けてから設定する。Visible の setter は FFI 呼び出しなので
        // コピー経由でも意味は変わらない。
        var root = newRecordRoot;
        if (!root.IsValid) { return; }
        root.Visible = visible;
    }

    /// <summary>テキストへ文言を設定する（未設定・破棄済みなら何もしない）。</summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="content">表示する文字列。</param>
    private static void SetContent(SEED.Text? text, string content)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Content = content;
    }

    /// <summary>テキストのアルファだけを書き換える（RGB と文字列は保つ）。</summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="alpha">不透明度（0〜1 へクランプする）。</param>
    private static void SetTextAlpha(SEED.Text? text, float alpha)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Color = t.Color.WithAlpha(SEED.Mathf.Clamped01(alpha));
    }

    /// <summary>
    /// easeOutBack: <c>1 + c3·(t−1)³ + c1·(t−1)²</c>（1 を少し行き過ぎてから戻る）。
    /// 跳ね返り量は <see cref="backOvershoot"/> 倍（0 で行き過ぎ無し）。
    /// </summary>
    /// <param name="t">進行度（0〜1）。</param>
    private float EaseOutBack(float t)
    {
        float c1 = BackEaseBaseC1 * SEED.Mathf.Max(backOvershoot, 0f);
        float c3 = c1 + 1f;
        float u = SEED.Mathf.Clamped01(t) - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    /// <summary>
    /// easeInBack: <c>c3·t³ − c1·t²</c>（0 側へ少し引いてから縮む）。
    /// 跳ね返り量は <see cref="backOvershoot"/> 倍。
    /// </summary>
    /// <param name="t">進行度（0〜1）。</param>
    private float EaseInBack(float t)
    {
        float c1 = BackEaseBaseC1 * SEED.Mathf.Max(backOvershoot, 0f);
        float c3 = c1 + 1f;
        float u = SEED.Mathf.Clamped01(t);
        return c3 * u * u * u - c1 * u * u;
    }
}
