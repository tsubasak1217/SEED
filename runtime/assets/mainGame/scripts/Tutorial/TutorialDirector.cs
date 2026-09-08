// ============================================================================
//  TutorialDirector.cs
//  チュートリアルの進行（ミッションの開始・達成・説明の再生・制限の適用）。
// ============================================================================

using System.Collections.Generic;
using SEEDEditor.Scripting;

/// <summary>
/// チュートリアルの進行を司るスクリプト【ミッションの進行判断の唯一の置き場】。
///
/// 【責務】
///  1. ミッション（<see cref="TutorialMission"/>）を上から順に進める
///  2. ミッションごとに「ルール上書き」「入力制限」を適用し、終わったら必ず戻す
///  3. 説明の台詞（<see cref="TutorialDialogue"/>）を吹き出しで再生する
///  4. 達成したら「ミッションクリア！」のバナーを出し、一拍置いて自動フェードアウト
///     で閉じたら（決定入力は待たない）次へ進む
///  5. 全ミッションが終わったら完了フラグを保存し、すべての制限を解除する
///
/// 判定（達成条件）は <see cref="IMission"/> の各実装、
/// 表示は <see cref="MissionPanel"/> / <see cref="TutorialWindow"/> /
/// <see cref="MissionClearBanner"/>、入力の可否判定は <see cref="InputGate"/>、
/// 釣りへの例外規則は <see cref="TutorialRules"/> の責務（単一責任）。
///
/// 【吹き出しは出しっぱなしにしない】
/// 説明は「読む時間」だけ出して必ず引っ込める。ミッション中に常に見えているのは
/// パネル（ミッション名・目的・チェック・進捗）だけにして、操作の邪魔をしない。
///
/// 【一瞬で次へ進めない】
/// 達成した瞬間に次の説明が出ると、何を達成したのか読めないまま流れてしまう。
/// 必ずバナーを出し、<see cref="MissionClearBanner.holdSeconds"/> のあいだ一拍置いてから
/// バナー自身が自動でフェードアウトして閉じる（決定入力は待たない。次への進行は
/// バナーの完了通知（<see cref="MissionClearBanner.ConsumeFinished"/>）を合図にする）。
///
/// 【時間軸】
/// 進行のタイマー・決定入力の判定はすべて実時間（Time.UnscaledDeltaTime）で行う。
/// 説明中は時間停止（Time.Scale = 0）を掛けるため、ゲーム時間で待つと止まってしまう。
///
/// 【安全策】
/// 時間停止・入力制限・ルール上書き・台本は「掛けた側が必ず戻す」責任を持つ。
/// 全ミッションの完了時だけでなく <see cref="OnDestroy"/>（シーン遷移・Play 停止・
/// ホットリロード）でも必ず解除するので、制限が焼き付いて操作不能になることはない。
/// </summary>
public class TutorialDirector : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>ゲーム時間を止めるときの時間倍率。</summary>
    private const float TimeScalePaused = 0f;

    /// <summary>ゲーム時間を通常速度へ戻すときの時間倍率。</summary>
    private const float TimeScaleNormal = 1f;

    /// <summary>台詞の表示開始からこの秒数（実時間）は決定入力を受け付けない（二重送り防止）。</summary>
    private const float DefaultInputLockSeconds = 0.15f;

    /// <summary>チュートリアル完了として保存する値。</summary>
    private const bool TutorialDoneValue = true;

    /// <summary>ミッションが 1 件も無いことを表す添字。</summary>
    private const int NoMissionIndex = -1;

    /// <summary>チュートリアル開始から最初の台詞までに置く既定の待ち時間（秒・実時間）。</summary>
    private const float DefaultIntroDelaySeconds = 1.5f;

    /// <summary>台詞でカメラを寄せるときの、対象からの既定の水平距離（メートル）。</summary>
    private const float DefaultCameraZoomDistance = 14.0f;

    /// <summary>台詞でカメラを寄せるときの、対象からの既定の高さ（メートル）。</summary>
    private const float DefaultCameraZoomHeight = 6.0f;

    /// <summary>台詞でカメラを寄せるときの既定の追従率（大きいほど速く寄る）。</summary>
    private const float DefaultCameraZoomLerpRate = 3.0f;

    /// <summary>台詞でカメラを寄せるときに、対象のどれだけ上を見るかの既定値（メートル）。</summary>
    private const float DefaultCameraZoomLookHeight = 2.0f;

    /// <summary>ゼロ除算・縮退ベクトルの判定に使う微小値。</summary>
    private const float DivideEpsilon = 1e-4f;

    /// <summary>台詞の列が空であることを表す添字。</summary>
    private const int NoQueueIndex = 0;

    // ─── 進行段階 ────────────────────────────────────────────

    /// <summary>チュートリアル全体の進行段階。</summary>
    private enum DirectorPhase
    {
        /// <summary>まだ始まっていない（autoStart が false のときの待機）。</summary>
        Idle,

        /// <summary>
        /// 開始直後の猶予。シーン遷移のフェードインが明けるまで喋らずに待つ段階。
        /// ここでは入力だけ止め、ゲーム時間は流したまま（波・鳥は動く）にする。
        /// </summary>
        StartDelay,

        /// <summary>ミッション開始前の説明を読ませている。</summary>
        Intro,

        /// <summary>ミッションを遊んでもらっている（判定が走る唯一の段階）。</summary>
        Playing,

        /// <summary>ミッションの最中に説明を割り込ませている。</summary>
        Interjecting,

        /// <summary>達成バナーを出している。</summary>
        ClearBanner,

        /// <summary>達成後の一言を読ませている。</summary>
        Outro,

        /// <summary>全ミッションが終わった（以降は何もしない）。</summary>
        Finished,
    }

    // ─── インスペクタ公開フィールド（参照）───────────────────

    /// <summary>説明を表示する吹き出し（UI キャンバス配下の TutorialWindow）。</summary>
    [Header("参照"), SerializeField(Label = "説明窓", Tooltip = "UI キャンバス配下の TutorialWindow")]
    public TutorialWindow? window;

    /// <summary>常設のミッションパネル（レーダーの下）。</summary>
    [SerializeField(Label = "ミッションパネル", Tooltip = "レーダーの下に置く MissionPanel")]
    public MissionPanel? panel;

    /// <summary>達成バナー（画面中央）。</summary>
    [SerializeField(Label = "クリアバナー", Tooltip = "達成時に出す MissionClearBanner")]
    public MissionClearBanner? banner;

    /// <summary>プレイヤーの移動スクリプト（到達判定に位置を使う）。</summary>
    [SerializeField(Label = "プレイヤー", Tooltip = "位置の取得に使う PlayerMove")]
    public PlayerMove? playerMove;

    /// <summary>
    /// カメラ本体のトランスフォーム（台詞でカメラを寄せるときに直接動かす）。
    /// 未設定ならカメラ寄せは行わない（説明はそのまま流れる）。
    /// </summary>
    [SerializeField(Label = "カメラ", Tooltip = "台詞でカメラを寄せる対象（MainCamera の Transform）")]
    public SEED.Transform? cameraTransform;

    // ─── インスペクタ公開フィールド（データ）───────────────

    /// <summary>ミッションのリスト（上から順に進む）。</summary>
    [Header("データ"), SerializeField(Label = "ミッション", Tooltip = "チュートリアルのミッションを上から順に並べる")]
    public List<TutorialMission> missions = new();

    /// <summary>説明台詞のリスト（ミッション ID と場面で引かれる）。</summary>
    [SerializeField(Label = "台詞", Tooltip = "ミッション ID と場面で引かれる説明台詞。同じ組が複数あれば順に送られる")]
    public List<TutorialDialogue> dialogues = new();

    // ─── インスペクタ公開フィールド（進行）───────────────────

    /// <summary>シーン開始と同時にチュートリアルを始めるか。</summary>
    [Header("進行"), SerializeField(Label = "自動開始", Tooltip = "シーン開始と同時に始める。false なら Begin() を呼ぶまで待つ")]
    public bool autoStart = true;

    /// <summary>
    /// 【デバッグ用】true の間、セーブデータの完了フラグを無視して必ずチュートリアルを流す。
    /// 本番出荷時は false のままにしておくこと。
    /// </summary>
    [SerializeField(Label = "【デバッグ】必ず流す", Tooltip = "true でセーブデータの完了フラグを無視して必ずチュートリアルを実行する")]
    public bool debugForceTutorial = false;

    /// <summary>台詞の表示開始から決定入力を受け付けないまでの秒数（実時間）。</summary>
    [SerializeField(Label = "送り無効時間(秒)", Tooltip = "台詞の表示直後に決定入力を無視する秒数（二重送りの防止）")]
    public float inputLockSeconds = DefaultInputLockSeconds;

    /// <summary>糸ゲージを満タンへ戻すときの所要秒数（ミスして周回をやり直したとき）。</summary>
    [SerializeField(Label = "ゲージ復帰の秒数", Tooltip = "周回のやり直しで糸ゲージが満タンへ戻るまでの秒数")]
    public float gaugeRestoreSeconds = TutorialRules.DefaultGaugeRestoreSeconds;

    /// <summary>
    /// チュートリアル開始から最初の台詞までの待ち時間（秒・実時間）。
    /// シーン遷移のフェードインが明けてから喋り出すための猶予。
    /// </summary>
    [SerializeField(Label = "開始の猶予(秒)", Tooltip = "Play 開始から最初の台詞までの待ち時間（実時間）")]
    public float introDelaySeconds = DefaultIntroDelaySeconds;

    /// <summary>台詞でカメラを寄せるときの、対象からの水平距離（メートル）。</summary>
    [Header("台詞中のカメラ寄せ"), SerializeField(Label = "寄りの距離(m)", Tooltip = "カメラ寄せ先からの水平距離")]
    public float cameraZoomDistance = DefaultCameraZoomDistance;

    /// <summary>台詞でカメラを寄せるときの、対象からの高さ（メートル）。</summary>
    [SerializeField(Label = "寄りの高さ(m)", Tooltip = "カメラ寄せ先からの高さ")]
    public float cameraZoomHeight = DefaultCameraZoomHeight;

    /// <summary>台詞でカメラを寄せるときの追従率（大きいほど速く寄る）。</summary>
    [SerializeField(Label = "寄りの追従率", Tooltip = "カメラが寄る速さ。大きいほど速い")]
    public float cameraZoomLerpRate = DefaultCameraZoomLerpRate;

    /// <summary>
    /// 寄せたカメラが見る点を、対象からどれだけ上へずらすか（メートル）。
    /// 地面の一点を真っ直ぐ見ると画面が地面で埋まるので、少し上を見て水平線を残す。
    /// </summary>
    [SerializeField(Label = "注視点の高さ(m)", Tooltip = "カメラが見る点を対象から何 m 上へずらすか")]
    public float cameraZoomLookHeight = DefaultCameraZoomLookHeight;

    /// <summary>全ミッションが終わった瞬間に呼ぶイベント（BGM 切替・HUD 表示などの結線用）。</summary>
    [SerializeField(Label = "完了時イベント", Tooltip = "全ミッションを終えた瞬間に呼ばれる")]
    public SEED.ScriptEvent onTutorialFinished;

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>現在の進行段階。</summary>
    private DirectorPhase phase = DirectorPhase.Idle;

    /// <summary>現在のミッションの添字（missions の添字）。</summary>
    private int missionIndex = NoMissionIndex;

    /// <summary>現在のミッションの判定クラス。</summary>
    private IMission? currentMission;

    /// <summary>現在のミッションへ渡している文脈。</summary>
    private MissionContext? currentContext;

    /// <summary>いま再生している台詞の列（Intro / Clear / 割り込みで使い回す）。</summary>
    private readonly List<TutorialDialogue> dialogueQueue = new();

    /// <summary>台詞の列のうち、いま表示している位置。</summary>
    private int dialogueIndex = NoQueueIndex;

    /// <summary>いまの台詞を表示してからの経過秒（実時間）。</summary>
    private float dialogueElapsed;

    /// <summary>開始の猶予（<see cref="DirectorPhase.StartDelay"/>）の残り秒数（実時間）。</summary>
    private float startDelayRemaining;

    /// <summary>いま台詞がカメラを寄せている対象（寄せていなければ null）。</summary>
    private SEED.Transform? activeCameraTarget;

    /// <summary>割り込みが終わったあとに戻る段階。</summary>
    private DirectorPhase resumePhase = DirectorPhase.Playing;

    /// <summary>
    /// 現在のミッションの後始末を済ませたか【二重解除の防止】。
    ///
    /// 達成時はバナーを出す前に後始末を済ませるが、そのあと次のミッションへ進むときにも
    /// 同じ後始末の入口を通る。フラグが無いと購読解除と終了イベントが 2 回走る。
    /// </summary>
    private bool currentMissionEnded;

    /// <summary>
    /// 自動開始を「最初の Update」まで持ち越すための予約フラグ。
    ///
    /// OnStart の呼び出し順はスクリプト間で保証されない。OnStart の中で説明窓を
    /// 表示すると、そのあとに走る TutorialWindow.OnStart が窓を隠してしまい、
    /// 説明が一切見えないまま進行が止まる。開始を 1 フレーム遅らせれば
    /// 全スクリプトの OnStart が済んでいることが保証される。
    /// </summary>
    private bool autoStartPending;

    // ─── 公開プロパティ ──────────────────────────────────────

    /// <summary>プレイヤーの移動スクリプト（ミッションが位置を読むための窓口）。</summary>
    public PlayerMove? Player => playerMove;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 初期化。完了済みなら自分と UI を無効化して何もしない。
    /// </summary>
    public override void OnStart()
    {
        // 制限の焼き付き防止: 前回の Play・ホットリロードの残りがあれば必ず解除してから始める
        ReleaseAllRestrictions();

        bool done = SEED.SaveData.GetBool(GameProgressKeys.TutorialDone, false);
        if (done && !debugForceTutorial)
        {
            // 済み: UI を隠し、以降は一切動かない（Update も即 return する）
            HideAllUi();
            phase = DirectorPhase.Finished;
            SEED.Debug.Log("[Tutorial] 完了済みのためスキップします。");
            return;
        }

        HideAllUi();

        // 開始は次の Update まで持ち越す（他スクリプトの OnStart 完了を待つ）
        autoStartPending = autoStart;
    }

    /// <summary>
    /// 破棄直前の後始末【制限解除の最後の砦】。
    /// シーン遷移・Play 停止・ホットリロードのいずれでも必ず通るので、
    /// ミッションの購読・時間停止・入力制限・ルール上書き・台本をすべて元へ戻す。
    /// </summary>
    public override void OnDestroy()
    {
        EndCurrentMission();
        ReleaseAllRestrictions();
    }

    /// <summary>
    /// 毎フレームの進行。ゲーム時間が止まっていても進む必要があるので、必ず実時間で計る。
    /// </summary>
    /// <param name="ctx">フレーム情報（ここでは使わず Time.UnscaledDeltaTime を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 予約されていた自動開始をここで実行する（全スクリプトの OnStart 完了後）
        if (autoStartPending)
        {
            autoStartPending = false;
            Begin();
        }

        if (phase is DirectorPhase.Idle or DirectorPhase.Finished) { return; }

        float unscaledDelta = SEED.Time.UnscaledDeltaTime;
        dialogueElapsed += unscaledDelta;

        switch (phase)
        {
            case DirectorPhase.StartDelay:
                UpdateStartDelay(unscaledDelta);
                break;

            case DirectorPhase.Intro:
                UpdateDialogue(OnIntroFinished);
                break;

            case DirectorPhase.Playing:
                UpdatePlaying(unscaledDelta);
                break;

            case DirectorPhase.Interjecting:
                UpdateDialogue(OnInterjectionFinished);
                break;

            case DirectorPhase.ClearBanner:
                UpdateClearBanner();
                break;

            case DirectorPhase.Outro:
                UpdateDialogue(AdvanceToNextMission);
                break;
        }

        // 台詞によるカメラ寄せは段階に関わらず毎フレーム進める（実時間で動かす）
        UpdateDialogueCamera(unscaledDelta);
    }

    // ─── 公開メソッド ────────────────────────────────────────

    /// <summary>
    /// チュートリアルを最初のミッションから開始する（自動開始が false のときの入口）。
    /// 既に開始済み・完了済みなら何もしない。
    /// </summary>
    public void Begin()
    {
        if (phase != DirectorPhase.Idle) { return; }
        if (missions.Count == 0)
        {
            SEED.Debug.LogWarning("[Tutorial] ミッションが 1 件も設定されていません。即座に完了扱いにします。");
            FinishTutorial();
            return;
        }

        missionIndex = NoMissionIndex;

        // 猶予が設定されていれば、フェードインを見せてから喋り出す。
        // ここでは入力だけ止める（時間は止めない＝波や鳥はそのまま動く）。
        float delay = SEED.Mathf.Max(introDelaySeconds, 0f);
        if (delay > 0f)
        {
            startDelayRemaining = delay;
            phase = DirectorPhase.StartDelay;
            InputGate.DenyAll();
            HideAllUi();
            return;
        }

        AdvanceToNextMission();
    }

    /// <summary>
    /// 開始の猶予を進める。時間切れで最初のミッションへ入る。
    /// </summary>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    private void UpdateStartDelay(float unscaledDelta)
    {
        startDelayRemaining -= unscaledDelta;
        if (startDelayRemaining > 0f) { return; }

        startDelayRemaining = 0f;
        AdvanceToNextMission();
    }

    /// <summary>
    /// チュートリアルを途中で打ち切る（デバッグ用のスキップ。ScriptEvent から結線できる）。
    /// 完了フラグも保存するので、次回以降は流れない。
    /// </summary>
    public void Skip()
    {
        if (phase == DirectorPhase.Finished) { return; }
        SEED.Debug.Log("[Tutorial] スキップされました。");
        FinishTutorial();
    }

    /// <summary>
    /// ミッションの最中に説明を 1 回割り込ませる【割り込みの唯一の入口】。
    ///
    /// 該当する台詞が 1 枚も無ければ何も起きない。
    /// 割り込み中はミッションの判定が止まり、読み終えると元の状態へ戻る。
    /// </summary>
    /// <param name="slot">出したい台詞の場面。</param>
    public void Interject(TutorialDialogueSlot slot)
    {
        if (phase != DirectorPhase.Playing) { return; }
        if (missionIndex < 0 || missionIndex >= missions.Count) { return; }

        if (!QueueDialogues(missions[missionIndex].id, slot)) { return; }

        resumePhase = DirectorPhase.Playing;
        SetBiteSuppressed(true);
        phase       = DirectorPhase.Interjecting;
        ShowCurrentDialogue();
    }

    // ─── 内部処理: ミッションの進行 ─────────────────────────

    /// <summary>
    /// 次のミッションへ進む【ミッション切り替えの唯一の入口】。
    /// ミッションが尽きていれば完了処理へ入る。
    /// </summary>
    private void AdvanceToNextMission()
    {
        EndCurrentMission();
        ReleaseAllRestrictions();

        missionIndex++;
        if (missionIndex >= missions.Count)
        {
            FinishTutorial();
            return;
        }

        var data = missions[missionIndex];

        currentMission = MissionFactory.Create(data.kind);
        if (currentMission is null)
        {
            // 未対応の種類は飛ばす（警告は MissionFactory が出している）
            AdvanceToNextMission();
            return;
        }

        currentContext      = new MissionContext(this, data);
        currentMissionEnded = false;
        data.onStart.Invoke();

        // パネルはミッションを始める前から出しておく（説明を読む前に「何をするのか」が見える）
        ApplyPanel(data);

        // ミッションの台本（目印の設置・魚の仕込み）は既定では<b>説明より先に</b>済ませる。
        // 説明中にカメラを目印へ寄せる演出があるため、説明の時点で目印が
        // 置かれていないと「何も無い場所」を映すことになる。
        // 判定（Update）は Playing 段階でしか走らないので、先に始めても進行はしない。
        //
        // ただし data.scriptAfterIntro が true のミッション（合わせ・ビート・巻きなど、
        // 台本が「アタリ」「出題」を即座に仕込む種類）は、台本の適用そのものを
        // 説明を読み終えるまで遅らせる。説明中はアタリのカウントダウンを
        // TutorialRules.BiteSuppressed で止める安全策（SetBiteSuppressed）もあるが、
        // それとは別に「そもそも仕込みを後回しにする」ことで二重に取りこぼしを防ぐ。
        if (data.scriptAfterIntro)
        {
            if (QueueDialogues(data.id, TutorialDialogueSlot.Intro))
            {
                SetBiteSuppressed(true);
                phase = DirectorPhase.Intro;
                ShowCurrentDialogue();
                return;
            }

            // 説明が無いミッションは従来どおりすぐに台本を適用する
            StartCurrentMission();
            return;
        }

        StartCurrentMission();

        // 開始に失敗して次のミッションへ飛んだ場合は、ここで割り込まない
        // （飛んだ先の段階を、このミッションの説明で上書きしてしまうため）
        if (phase != DirectorPhase.Playing) { return; }

        // 開始前の説明があれば読ませる。読み終えたら操作を解禁して本編へ入る
        if (QueueDialogues(data.id, TutorialDialogueSlot.Intro))
        {
            SetBiteSuppressed(true);
            phase = DirectorPhase.Intro;
            ShowCurrentDialogue();
        }
    }

    /// <summary>
    /// 開始前の説明を読み終えたときの処理。
    /// 説明のあいだ止めていた操作・時間を、このミッションの設定へ戻して本編を始める。
    ///
    /// <see cref="TutorialMission.scriptAfterIntro"/> が true のミッションでは、
    /// ここが初めて台本を適用する場所になる（<see cref="StartCurrentMission"/> を
    /// そのまま呼び、ルール上書き・入力許可・判定クラスの Begin をまとめて行わせる）。
    /// </summary>
    private void OnIntroFinished()
    {
        if (currentMission is null || currentContext is null)
        {
            AdvanceToNextMission();
            return;
        }

        var data = missions[missionIndex];

        window?.Hide();                 // 説明は出しっぱなしにしない

        if (data.scriptAfterIntro)
        {
            // 台本（ルール上書き・入力許可・魚の仕込み等）をここで初めて適用する。
            // StartCurrentMission が SetBiteSuppressed(false) を含めて面倒を見る。
            StartCurrentMission();
            return;
        }

        ApplyMissionRules(data);
        ApplyMissionGates(data);
        SEED.Time.Scale = TimeScaleNormal;
        SetBiteSuppressed(false);        // 読み終えたのでアタリの足止めを解く

        phase = DirectorPhase.Playing;
    }

    /// <summary>
    /// 現在のミッションを開始する【ミッション開始処理の唯一の入口】。
    /// ルール上書きと入力制限を適用し、判定クラスの台本（目印・魚の仕込み）を走らせる。
    ///
    /// 開始前の説明がある場合は、この直後に説明の段階（Intro）へ移る
    /// （<see cref="TutorialMission.scriptAfterIntro"/> が false のとき）。
    /// 説明の間は <see cref="ShowCurrentDialogue"/> が入力を止め直すので、
    /// ここで許可した操作が説明中に効いてしまうことはない。
    /// </summary>
    private void StartCurrentMission()
    {
        if (currentMission is not { } mission || currentContext is not { } context)
        {
            AdvanceToNextMission();
            return;
        }

        var data = missions[missionIndex];

        window?.Hide();                 // 説明は出しっぱなしにしない
        ApplyMissionRules(data);
        ApplyMissionGates(data);
        SEED.Time.Scale = TimeScaleNormal;
        SetBiteSuppressed(false);        // 台本の適用＝本編開始なのでアタリの足止めを解く

        mission.Begin(context);
        phase = DirectorPhase.Playing;

        SEED.Debug.Log($"[Tutorial] ミッション {missionIndex + 1}/{missions.Count}「{data.title}」を開始");
    }

    /// <summary>
    /// ミッション本編の更新（判定を進め、パネルを最新の内容へ差し替える）。
    /// </summary>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    private void UpdatePlaying(float unscaledDelta)
    {
        if (currentMission is not { } mission || currentContext is not { } context)
        {
            AdvanceToNextMission();
            return;
        }

        mission.Update(context, unscaledDelta);
        ApplyPanel(missions[missionIndex], mission);

        if (mission.IsCleared) { BeginClearBanner(); }
    }

    /// <summary>
    /// 達成バナーを出す【達成演出の唯一の入口】。
    /// 判定と台本はここで畳み、以降はバナーを閉じるまで何も進めない。
    /// </summary>
    private void BeginClearBanner()
    {
        EndCurrentMissionKeepingData();
        ReleaseAllRestrictions();

        // ReleaseAllRestrictions() で一旦解除されるため、バナー表示中も
        // アタリを進めたくないのであらためて抑止を掛け直す
        SetBiteSuppressed(true);

        // バナーを読ませるあいだはゲームを止める（背後で釣りが進んで状況が変わらないように）
        // 達成バナー中に時間を止めるかはミッションごとの設定に従う
        // （釣りの最中でないミッションまで止めると画面が固まって見える）
        //
        // ただし MissionClearBanner が Animator でクリップ演出をする場合は例外。
        // Animator の再生位置はスケール後のゲーム時間（engine 側 AnimationSystem が
        // 使う dt はスクリプトの SEED.Time.DeltaTime と同源）で進むため、
        // Time.Scale = 0 にすると Animator ごと止まってバナーが動かなくなってしまう。
        // そのため Animator 演出になる見込みのときは時間を止めず、決定操作以外の
        // 入力を直後の InputGate.DenyAll() で塞ぐことだけで裏側の進行を抑える
        // （skipClearBanner でバナー自体を出さない場合はこの配慮は不要）。
        bool bannerWillPlay      = !missions[missionIndex].skipClearBanner;
        bool bannerUsesAnimator  = bannerWillPlay && (banner?.WillUseAnimator ?? false);
        bool shouldPauseForClear = missions[missionIndex].pauseOnClear && !bannerUsesAnimator;
        SEED.Time.Scale = shouldPauseForClear ? TimeScalePaused : TimeScaleNormal;
        InputGate.DenyAll();

        SEED.Debug.Log($"[Tutorial] ミッション {missionIndex + 1}「{missions[missionIndex].title}」を達成");

        // 締めの演出など、バナーが場違いになるミッションは帯を出さずに次の台詞へ進む
        if (missions[missionIndex].skipClearBanner)
        {
            GoToOutroOrNext();
            return;
        }

        banner?.Play();
        phase = DirectorPhase.ClearBanner;
    }

    /// <summary>
    /// バナーの更新。閉じ切ったら達成後の一言へ、無ければ次のミッションへ進む。
    /// </summary>
    private void UpdateClearBanner()
    {
        // バナーがシーンに無い場合でも詰まらないよう、その場合は即座に次へ進む
        if (banner is not { } activeBanner)
        {
            GoToOutroOrNext();
            return;
        }

        if (activeBanner.ConsumeFinished()) { GoToOutroOrNext(); }
    }

    /// <summary>
    /// 達成後の一言があれば読ませ、無ければ次のミッションへ進む。
    /// </summary>
    private void GoToOutroOrNext()
    {
        if (QueueDialogues(missions[missionIndex].id, TutorialDialogueSlot.Clear))
        {
            SetBiteSuppressed(true);   // 既に true のはずだが明示しておく
            phase = DirectorPhase.Outro;
            ShowCurrentDialogue();
            return;
        }

        AdvanceToNextMission();
    }

    /// <summary>
    /// 割り込みの説明を読み終えたときの処理（元の状態へ戻す）。
    /// </summary>
    private void OnInterjectionFinished()
    {
        window?.Hide();

        var data = missions[missionIndex];
        ApplyMissionRules(data);
        ApplyMissionGates(data);
        SEED.Time.Scale = TimeScaleNormal;
        SetBiteSuppressed(false);   // 読み終えたので元の状態へ戻す

        phase = resumePhase;
    }

    /// <summary>
    /// 全ミッションを終えたときの締め【完了処理の唯一の出口】。
    /// 完了フラグを永続化し、制限を解除して UI を隠す。
    /// </summary>
    private void FinishTutorial()
    {
        EndCurrentMission();
        ReleaseAllRestrictions();
        HideAllUi();

        phase        = DirectorPhase.Finished;
        missionIndex = NoMissionIndex;

        // フラグはタイトルの分岐（TitleFlow）が読むので、必ずディスクへ書き出す
        SEED.SaveData.SetBool(GameProgressKeys.TutorialDone, TutorialDoneValue);
        SEED.SaveData.Save();

        onTutorialFinished.Invoke();
        SEED.Debug.Log("[Tutorial] 完了しました。");
    }

    /// <summary>
    /// 現在のミッションを終了して参照ごと畳む【後始末の唯一の出口】。
    /// </summary>
    private void EndCurrentMission()
    {
        EndCurrentMissionKeepingData();
        currentMission = null;
        currentContext = null;
    }

    /// <summary>
    /// 現在のミッションの購読・生成物だけを畳む（判定クラスの参照は残す）。
    /// バナー表示中もミッション名を出し続けたいので、データは保持する。
    /// </summary>
    private void EndCurrentMissionKeepingData()
    {
        if (currentMissionEnded) { return; }
        if (currentMission is not { } mission || currentContext is not { } context) { return; }

        currentMissionEnded = true;
        mission.End(context);

        if (missionIndex >= 0 && missionIndex < missions.Count)
        {
            missions[missionIndex].onEnd.Invoke();
        }
    }

    // ─── 内部処理: 台詞の再生 ───────────────────────────────

    /// <summary>
    /// 指定のミッション ID と場面に一致する台詞を再生列へ積む
    /// 【台詞を引く唯一の実装】。
    /// </summary>
    /// <param name="missionId">対象のミッション ID。</param>
    /// <param name="slot">対象の場面。</param>
    /// <returns>1 枚以上積めたら true。</returns>
    private bool QueueDialogues(string missionId, TutorialDialogueSlot slot)
    {
        dialogueQueue.Clear();
        dialogueIndex = NoQueueIndex;

        for (int i = 0; i < dialogues.Count; i++)
        {
            var line = dialogues[i];
            if (line.slot != slot) { continue; }
            if (!string.Equals(line.missionId, missionId, System.StringComparison.Ordinal)) { continue; }
            dialogueQueue.Add(line);
        }

        return dialogueQueue.Count > 0;
    }

    /// <summary>
    /// 再生列のいまの 1 枚を吹き出しへ出す。
    /// 表示中はゲーム時間を止め（台詞側の指定に従う）、操作は一切受け付けない。
    /// </summary>
    private void ShowCurrentDialogue()
    {
        if (dialogueIndex < 0 || dialogueIndex >= dialogueQueue.Count) { return; }

        var line = dialogueQueue[dialogueIndex];
        dialogueElapsed = 0f;

        // 読ませている間は操作を止める（説明中に釣りが進むと台本どおりにならない）
        InputGate.DenyAll();
        SEED.Time.Scale = line.pauseTime ? TimeScalePaused : TimeScaleNormal;

        // この台詞がカメラ寄せを指定していれば寄せ始める（指定が無ければ追従へ戻す）
        BeginDialogueCamera(line.cameraTarget);

        if (window is not { } w)
        {
            SEED.Debug.LogWarning("[Tutorial] 説明窓が未設定のため、台詞を表示できません。");
            return;
        }

        w.SetText(line.text);
        w.ShowAtAnchor(line.anchorTarget);
    }

    /// <summary>
    /// 台詞の送りを進める【送り処理の唯一の実装】。
    /// 列を読み終えたら、渡された「次に何をするか」を呼ぶ。
    /// </summary>
    /// <param name="onFinished">列を読み終えたときに呼ぶ処理。</param>
    private void UpdateDialogue(System.Action onFinished)
    {
        if (!IsConfirmAccepted()) { return; }

        dialogueIndex++;
        if (dialogueIndex < dialogueQueue.Count)
        {
            ShowCurrentDialogue();
            return;
        }

        dialogueQueue.Clear();
        dialogueIndex = NoQueueIndex;

        // 台詞の列を出し切ったらカメラは通常の追従へ戻す
        EndDialogueCamera();
        onFinished();
    }

    /// <summary>
    /// 決定入力（送り）が成立したか【送り判定の唯一の実装】。
    ///
    /// 本文がまだ流れている途中なら、決定入力は「全文表示」に消費して送りには使わない
    /// （会話窓と同じ操作感）。台詞の表示直後の一定時間は、前の台詞を送ったクリックが
    /// そのまま次まで送ってしまうのを防ぐため受け付けない。
    /// </summary>
    /// <returns>この台詞を送ってよければ true。</returns>
    private bool IsConfirmAccepted()
    {
        if (dialogueElapsed < SEED.Mathf.Max(inputLockSeconds, 0f)) { return false; }

        bool pressed = SceneFlow.IsConfirmPressed()
                    || SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left);
        if (!pressed) { return false; }

        // 文字送り中なら、まず全文表示に使う（送りには使わない）
        if (window is { } w && !w.IsTextComplete)
        {
            w.CompleteText();
            return false;
        }

        return true;
    }

    // ─── 内部処理: パネルの表示 ─────────────────────────────

    /// <summary>
    /// ミッションパネルへ内容を流し込む【パネル更新の唯一の入口】。
    /// </summary>
    /// <param name="data">表示するミッションのデータ。</param>
    /// <param name="mission">判定クラス（サブ目標と進捗の出どころ。null なら空欄で出す）。</param>
    private void ApplyPanel(TutorialMission data, IMission? mission = null)
    {
        panel?.Apply(
            data.title,
            data.objective,
            mission?.Objectives,
            mission?.ProgressText ?? string.Empty);
    }

    // ─── 内部処理: ルール上書きと入力制限 ───────────────────

    /// <summary>
    /// ミッションのルール上書きを釣りシステムへ適用する
    /// 【上書き適用の唯一の入口】。
    /// </summary>
    /// <param name="data">現在のミッションのデータ。</param>
    private void ApplyMissionRules(TutorialMission data)
    {
        TutorialRules.Active                  = true;
        TutorialRules.FishLevelFilter         = data.fishLevelFilter;
        TutorialRules.FishPrefabFilter        = data.fishPrefabFilter ?? TutorialRules.NoPrefabFilter;
        TutorialRules.FishPrefabExclusive     = data.fishPrefabExclusive;
        TutorialRules.ChainDisabled           = data.chainDisabled;
        TutorialRules.DriftDisabled           = data.driftDisabled;
        TutorialRules.DriftStationary         = data.driftStationary;
        TutorialRules.BeatDisabled            = data.beatDisabled;
        TutorialRules.LineBreakDisabled       = data.lineBreakDisabled;
        TutorialRules.RestartCycleOnMiss      = data.restartCycleOnMiss;
        TutorialRules.RebiteAfterHookMiss     = data.rebiteAfterHookMiss;
        TutorialRules.RestartFightOnLineBreak = data.restartFightOnLineBreak;
        TutorialRules.GaugeRestoreSeconds     = SEED.Mathf.Max(gaugeRestoreSeconds, 0f);
    }

    /// <summary>
    /// ミッションの入力制限を適用する。
    /// 許可フラグは既定 false なので、まず全部止めてから必要なものだけ開ける。
    /// </summary>
    /// <param name="data">現在のミッションのデータ。</param>
    private void ApplyMissionGates(TutorialMission data)
    {
        InputGate.DenyAll();
        InputGate.SetAllowed(GameAction.Move,      data.allowMove);
        InputGate.SetAllowed(GameAction.Ready,     data.allowReady);
        InputGate.SetAllowed(GameAction.Aim,       data.allowAim);
        InputGate.SetAllowed(GameAction.Cast,      data.allowCast);
        InputGate.SetAllowed(GameAction.Reel,      data.allowReel);
        InputGate.SetAllowed(GameAction.Hook,      data.allowHook);
        InputGate.SetAllowed(GameAction.Rhythm,    data.allowRhythm);
        InputGate.SetAllowed(GameAction.UiConfirm, data.allowUiConfirm);
    }

    /// <summary>
    /// 魚の食いつきを一時停止するかどうかを切り替える【アタリ抑止の唯一の入口】。
    ///
    /// 説明の台詞・クリアバナー・Outro を表示しているあいだ（＝プレイヤーがまだ読んでいる間）は
    /// true にして、Fish / FishingController のアタリ進行カウントダウンを止める。
    /// ミッション本編（Playing）へ入るときは必ず false へ戻す。
    ///
    /// 【なぜここで Active も立てるのか】
    /// TutorialRules.Active は「読む側が個別フィールドを見てよいか」の門番。
    /// TutorialMission.scriptAfterIntro が true のミッションでは、台本の適用
    /// （ApplyMissionRules を含む StartCurrentMission 一式）を Intro が終わるまで
    /// 遅らせるため、Intro の時点ではまだ Active が立っていないことがある。
    /// ここで先に true にしておかないと、抑止フラグが門番で弾かれて素通りしてしまう。
    /// 他のフィールドは TutorialRules.Clear() の既定値（上書き無し）のままなので、
    /// Active を早めても挙動には一切影響しない。
    /// </summary>
    /// <param name="suppressed">true でアタリの進行を止める。</param>
    private void SetBiteSuppressed(bool suppressed)
    {
        if (suppressed) { TutorialRules.Active = true; }
        TutorialRules.BiteSuppressed = suppressed;
    }

    /// <summary>
    /// 時間停止・入力制限・ルール上書き・台本をすべて解除する
    /// 【制限解除の唯一の出口】。
    /// </summary>
    private void ReleaseAllRestrictions()
    {
        SEED.Time.Scale = TimeScaleNormal;
        InputGate.AllowAll();
        EndDialogueCamera();
        TutorialRules.Clear();
        FishManager.Current?.ClearScripted();
    }

    /// <summary>チュートリアルの UI をすべて隠す。</summary>
    private void HideAllUi()
    {
        window?.Hide();
        panel?.Hide();
        banner?.Cancel();
    }

    // ─── 台詞中のカメラ寄せ ─────────────────────────────────

    /// <summary>
    /// 台詞のカメラ寄せを始める【カメラ寄せを掛ける唯一の入口】。
    ///
    /// 対象が未設定・無効なら寄せを解除する（台詞ごとに指定が変わるため、
    /// 「指定が無い台詞＝寄せない」を毎回ここで反映する）。
    /// 寄せている間は <see cref="TutorialRules.CameraSuspended"/> で
    /// <see cref="CameraMove"/> の通常追従を止め、こちらが直接動かす。
    /// </summary>
    /// <param name="target">寄せる対象のトランスフォーム。</param>
    private void BeginDialogueCamera(SEED.Transform target)
    {
        if (!target.IsValid || cameraTransform is not { IsValid: true })
        {
            EndDialogueCamera();
            return;
        }

        activeCameraTarget = target;

        // 上書きの門番（Active）が閉じていると CameraMove が寄せを見ないので必ず開ける
        TutorialRules.Active          = true;
        TutorialRules.CameraSuspended = true;
    }

    /// <summary>
    /// 台詞のカメラ寄せをやめて通常の追従へ戻す【解除の唯一の出口】。
    /// 追従側（<see cref="CameraMove"/>）が補間で戻すので、切り替えは滑らかになる。
    /// </summary>
    private void EndDialogueCamera()
    {
        activeCameraTarget            = null;
        TutorialRules.CameraSuspended = false;
    }

    /// <summary>
    /// 寄せ中のカメラを対象へ近づける（実時間で動かす）。
    ///
    /// 目標位置は「対象から見て<b>いまカメラが居る向き</b>へ
    /// <see cref="cameraZoomDistance"/> 離れ、<see cref="cameraZoomHeight"/> 高い点」。
    /// 画角の向きを大きく変えずに寄るだけなので、寄り／戻りで絵が回らない。
    /// </summary>
    /// <param name="unscaledDelta">前フレームからの実時間（秒）。</param>
    private void UpdateDialogueCamera(float unscaledDelta)
    {
        if (activeCameraTarget is not { IsValid: true } target) { return; }
        if (cameraTransform is not { IsValid: true } cam) { return; }

        // 見る点は対象の少し上（地面の一点を真正面から見ると画面が地面で埋まるため）
        var targetPosition = target.Position;
        var focus = new SEED.Vector3(
            targetPosition.x,
            targetPosition.y + cameraZoomLookHeight,
            targetPosition.z);

        // 対象 → カメラの水平方向（縮退しているときは寄せ先を決められないので何もしない）
        var flat = new SEED.Vector3(cam.Position.x - focus.x, 0f, cam.Position.z - focus.z);
        if (flat.SqrMagnitude <= DivideEpsilon) { return; }
        var dir = flat.Normalized;

        var desired = new SEED.Vector3(
            focus.x + dir.x * cameraZoomDistance,
            focus.y + cameraZoomHeight,
            focus.z + dir.z * cameraZoomDistance);

        float blend = ExponentialBlend(cameraZoomLerpRate, unscaledDelta);
        cam.Position = SEED.Vector3.Lerp(cam.Position, desired, blend);

        // 寄った位置から対象を見る（位置が補間なので向きも滑らかに変わる）
        var toTarget = focus - cam.Position;
        float distance = toTarget.Magnitude;
        if (distance <= DivideEpsilon) { return; }

        float yaw   = SEED.Mathf.Atan2(toTarget.x, toTarget.z) * SEED.Mathf.Rad2Deg;
        float pitch = -SEED.Mathf.Asin(SEED.Mathf.Clamped(toTarget.y / distance, -1f, 1f)) * SEED.Mathf.Rad2Deg;
        cam.Rotation = new SEED.Vector3(pitch, yaw, 0f);
    }

    /// <summary>
    /// フレームレートに依存しない補間係数を返す（追従率 rate の指数ブレンド）。
    /// </summary>
    /// <param name="rate">追従率（大きいほど速く寄る）。0 以下なら動かさない。</param>
    /// <param name="deltaTime">経過秒数。</param>
    /// <returns>0〜1 の補間係数。</returns>
    private static float ExponentialBlend(float rate, float deltaTime)
    {
        if (rate <= 0f || deltaTime <= 0f) { return 0f; }
        return SEED.Mathf.Clamped01(1f - SEED.Mathf.Exp(-rate * deltaTime));
    }
}
