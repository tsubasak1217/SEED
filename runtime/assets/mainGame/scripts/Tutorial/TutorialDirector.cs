// ============================================================================
//  TutorialDirector.cs
//  チュートリアルの進行（手順の開始・終了・時間停止・入力制限・台本の適用）。
// ============================================================================

using System.Collections.Generic;
using SEEDEditor.Scripting;

/// <summary>
/// チュートリアルの進行を司るスクリプト【手順の進行判断の唯一の置き場】。
///
/// 【責務】
///  1. 手順（<see cref="TutorialStep"/>）を上から順に進める
///  2. 手順ごとに「時間停止」「入力制限」「台本（釣りシステムへの強制設定）」を適用する
///  3. 手順の開始条件・終了条件を待つ（即時／名前付きイベント／決定入力／秒数）
///  4. 全手順が終わったら完了フラグを保存し、窓を隠して制限を解除する
///
/// 表示（窓の位置・文字送り・点滅）は <see cref="TutorialWindow"/>、
/// 入力の可否判定は <see cref="InputGate"/> の責務（単一責任）。
///
/// 【時間軸】
/// 手順のタイマー・決定入力の判定はすべて実時間（Time.UnscaledDeltaTime）で行う。
/// 時間停止中（Time.Scale = 0）はゲーム時間が進まないため、
/// ゲーム時間で待つと永久に手順が終わらなくなる。
///
/// 【安全策】
/// 時間停止と入力制限は「掛けた側が必ず戻す」責任を持つ。
/// 全手順の完了時だけでなく <see cref="OnDestroy"/>（シーン遷移・Play 停止・
/// ホットリロード）でも必ず解除するので、制限が焼き付いて操作不能になることはない。
///
/// 【シーン側の設定】
///  - UI キャンバス配下に置いた説明窓アクタを window へ指定する。
///  - 手順リストへ手順を 1 件ずつ足す（本文・位置・条件・許可入力・台本）。
/// </summary>
public class TutorialDirector : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>ゲーム時間を止めるときの時間倍率。</summary>
    private const float TimeScalePaused = 0f;

    /// <summary>ゲーム時間を通常速度へ戻すときの時間倍率。</summary>
    private const float TimeScaleNormal = 1f;

    /// <summary>手順開始からこの秒数（実時間）は決定入力を受け付けない（送りの取りこぼし・二重送り防止）。</summary>
    private const float DefaultStepInputLockSeconds = 0.15f;

    /// <summary>チュートリアル完了として保存する値。</summary>
    private const bool TutorialDoneValue = true;

    /// <summary>手順が 1 件も無いことを表す添字。</summary>
    private const int NoStepIndex = -1;

    /// <summary>レベル指定（1 始まり）を levels の添字（0 始まり）へ直す差分。</summary>
    private const int LevelIndexOffset = 1;

    /// <summary>台本のレベル指定が未設定・不正なときに使う既定レベル（1 始まり）。</summary>
    private const int DefaultScriptedLevel = 1;

    // ─── 進行段階 ────────────────────────────────────────────

    /// <summary>チュートリアル全体の進行段階。</summary>
    private enum DirectorPhase
    {
        /// <summary>まだ始まっていない（autoStart が false のときの待機）。</summary>
        Idle,

        /// <summary>手順の開始条件（イベント）を待っている。</summary>
        WaitingStart,

        /// <summary>手順を表示中（時間停止・入力制限が効いている）。</summary>
        Showing,

        /// <summary>全手順が終わった（以降は何もしない）。</summary>
        Finished,
    }

    // ─── インスペクタ公開フィールド ──────────────────────────

    /// <summary>説明を表示する窓（UI キャンバス配下の説明窓アクタ）。</summary>
    [Header("参照"), SerializeField(Label = "説明窓", Tooltip = "UI キャンバス配下の TutorialWindow")]
    public TutorialWindow? window;

    /// <summary>手順のリスト（上から順に進む）。</summary>
    [Header("手順"), SerializeField(Label = "手順", Tooltip = "チュートリアルの手順を上から順に並べる")]
    public List<TutorialStep> steps = new();

    /// <summary>シーン開始と同時にチュートリアルを始めるか。</summary>
    [Header("進行"), SerializeField(Label = "自動開始", Tooltip = "シーン開始と同時に始める。false なら Begin() を呼ぶまで待つ")]
    public bool autoStart = true;

    /// <summary>
    /// 【デバッグ用】true の間、セーブデータの完了フラグを無視して必ずチュートリアルを流す。
    /// 本番出荷時は false のままにしておくこと。
    /// </summary>
    [SerializeField(Label = "【デバッグ】必ず流す", Tooltip = "true でセーブデータの完了フラグを無視して必ずチュートリアルを実行する")]
    public bool debugForceTutorial = false;

    /// <summary>手順開始から決定入力を受け付けないまでの秒数（実時間）。</summary>
    [SerializeField(Label = "送り無効時間(秒)", Tooltip = "手順開始直後に決定入力を無視する秒数（二重送りの防止）")]
    public float stepInputLockSeconds = DefaultStepInputLockSeconds;

    /// <summary>全手順が終わった瞬間に呼ぶイベント（BGM 切替・HUD 表示などの結線用）。</summary>
    [SerializeField(Label = "完了時イベント", Tooltip = "全手順を終えた瞬間に呼ばれる")]
    public SEED.ScriptEvent onTutorialFinished;

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>現在の進行段階。</summary>
    private DirectorPhase phase = DirectorPhase.Idle;

    /// <summary>現在の手順の添字（steps の添字）。</summary>
    private int stepIndex = NoStepIndex;

    /// <summary>現在の手順の開始からの経過秒（実時間）。</summary>
    private float stepElapsed;

    /// <summary>開始条件のイベントが飛んできたか。</summary>
    private bool startSignaled;

    /// <summary>終了条件のイベントが飛んできたか。</summary>
    private bool finishSignaled;

    /// <summary>開始条件のイベント購読（手順を切り替えるたびに張り直す）。</summary>
    private SEED.EventSubscription? startSubscription;

    /// <summary>終了条件のイベント購読（手順を切り替えるたびに張り直す）。</summary>
    private SEED.EventSubscription? finishSubscription;

    /// <summary>
    /// 自動開始を「最初の Update」まで持ち越すための予約フラグ。
    ///
    /// OnStart の呼び出し順はスクリプト間で保証されない。OnStart の中で説明窓を
    /// 表示すると、そのあとに走る TutorialWindow.OnStart が窓を隠してしまい、
    /// 説明が一切見えないまま進行が止まる。開始を 1 フレーム遅らせれば
    /// 全スクリプトの OnStart が済んでいることが保証される。
    /// </summary>
    private bool autoStartPending;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 初期化。完了済みなら自分と窓を無効化して何もしない。
    /// </summary>
    public override void OnStart()
    {
        // 制限の焼き付き防止: 前回の Play・ホットリロードの残りがあれば必ず解除してから始める
        InputGate.AllowAll();
        SEED.Time.Scale = TimeScaleNormal;

        bool done = SEED.SaveData.GetBool(GameProgressKeys.TutorialDone, false);
        if (done && !debugForceTutorial)
        {
            // 済み: 窓を隠し、以降は一切動かない（Update も即 return する）
            window?.Hide();
            phase = DirectorPhase.Finished;
            SEED.Debug.Log("[Tutorial] 完了済みのためスキップします。");
            return;
        }

        window?.Hide();

        // 開始は次の Update まで持ち越す（他スクリプトの OnStart 完了を待つ）
        autoStartPending = autoStart;
    }

    /// <summary>
    /// 破棄直前の後始末【制限解除の最後の砦】。
    /// シーン遷移・Play 停止・ホットリロードのいずれでも必ず通るので、
    /// 時間停止・入力制限・台本設定をすべて元へ戻す。
    /// </summary>
    public override void OnDestroy()
    {
        ClearSubscriptions();
        ReleaseRestrictions();
        ClearScriptedActions();
    }

    /// <summary>
    /// 毎フレームの進行。開始条件の待ちと終了条件の判定を行う。
    /// ゲーム時間が止まっていても進む必要があるので、必ず実時間で計る。
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
        stepElapsed += unscaledDelta;

        switch (phase)
        {
            case DirectorPhase.WaitingStart:
                if (startSignaled) { BeginShow(); }
                break;

            case DirectorPhase.Showing:
                if (IsStepFinished()) { EndStep(); }
                break;
        }
    }

    // ─── 公開メソッド ────────────────────────────────────────

    /// <summary>
    /// チュートリアルを最初の手順から開始する（自動開始が false のときの入口）。
    /// 既に開始済み・完了済みなら何もしない。
    /// </summary>
    public void Begin()
    {
        if (phase != DirectorPhase.Idle) { return; }
        if (steps.Count == 0)
        {
            SEED.Debug.LogWarning("[Tutorial] 手順が 1 件も設定されていません。即座に完了扱いにします。");
            FinishTutorial();
            return;
        }

        stepIndex = NoStepIndex;
        AdvanceToNextStep();
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

    // ─── 内部処理: 手順の進行 ───────────────────────────────

    /// <summary>
    /// 次の手順へ進む【手順の切り替えの唯一の入口】。
    /// 手順が尽きていれば完了処理へ入る。
    /// </summary>
    private void AdvanceToNextStep()
    {
        ClearSubscriptions();
        ReleaseRestrictions();

        stepIndex++;
        if (stepIndex >= steps.Count)
        {
            FinishTutorial();
            return;
        }

        stepElapsed    = 0f;
        startSignaled  = false;
        finishSignaled = false;

        var step = steps[stepIndex];

        // 開始条件が「即時」ならそのまま表示へ、「イベント待ち」なら購読して待つ。
        // 待っている間は操作を制限しない（プレイヤーがそのイベントを起こす必要があるため）。
        if (step.startCondition == TutorialStartCondition.OnEvent
            && !string.IsNullOrWhiteSpace(step.startEventName))
        {
            phase = DirectorPhase.WaitingStart;
            startSubscription = On(step.startEventName, OnStartEventRaised);
            return;
        }

        BeginShow();
    }

    /// <summary>
    /// 現在の手順の表示を始める【手順開始処理の唯一の入口】。
    /// 開始イベント → 台本の適用 → 窓の表示 → 時間停止 → 入力制限 → 終了条件の準備、の順で行う。
    /// </summary>
    private void BeginShow()
    {
        var step = steps[stepIndex];

        phase          = DirectorPhase.Showing;
        stepElapsed    = 0f;
        finishSignaled = false;

        // 開始条件の購読はもう不要（同じイベントで二重に開始しないよう必ず外す）
        startSubscription?.Dispose();
        startSubscription = null;

        step.onStart.Invoke();
        ApplyScriptedAction(step);
        ShowWindow(step);
        ApplyRestrictions(step);
        PrepareFinishCondition(step);

        SEED.Debug.Log($"[Tutorial] 手順 {stepIndex + 1}/{steps.Count} を開始");
    }

    /// <summary>
    /// 現在の手順を終えて次へ進む【手順終了処理の唯一の入口】。
    /// </summary>
    private void EndStep()
    {
        steps[stepIndex].onEnd.Invoke();
        AdvanceToNextStep();
    }

    /// <summary>
    /// 全手順を終えたときの締め【完了処理の唯一の出口】。
    /// 完了フラグを永続化し、制限を解除して窓を隠す。
    /// </summary>
    private void FinishTutorial()
    {
        ClearSubscriptions();
        ReleaseRestrictions();
        ClearScriptedActions();

        window?.Hide();
        phase     = DirectorPhase.Finished;
        stepIndex = NoStepIndex;

        // フラグはタイトルの分岐（TitleFlow）が読むので、必ずディスクへ書き出す
        SEED.SaveData.SetBool(GameProgressKeys.TutorialDone, TutorialDoneValue);
        SEED.SaveData.Save();

        onTutorialFinished.Invoke();
        SEED.Debug.Log("[Tutorial] 完了しました。");
    }

    // ─── 内部処理: 終了条件 ─────────────────────────────────

    /// <summary>
    /// 現在の手順の終了条件を準備する（イベント待ちなら購読する）。
    /// </summary>
    /// <param name="step">現在の手順。</param>
    private void PrepareFinishCondition(TutorialStep step)
    {
        if (step.finishCondition != TutorialFinishCondition.OnEvent) { return; }
        if (string.IsNullOrWhiteSpace(step.finishEventName))
        {
            SEED.Debug.LogWarning($"[Tutorial] 手順 {stepIndex + 1}: 終了イベント名が空です。決定入力で送れるようにします。");
            return;
        }

        finishSubscription = On(step.finishEventName, OnFinishEventRaised);
    }

    /// <summary>
    /// 現在の手順の終了条件が満たされたか。
    /// </summary>
    /// <returns>次の手順へ進んでよければ true。</returns>
    private bool IsStepFinished()
    {
        var step = steps[stepIndex];

        switch (step.finishCondition)
        {
            case TutorialFinishCondition.Seconds:
                return stepElapsed >= SEED.Mathf.Max(step.finishSeconds, 0f);

            case TutorialFinishCondition.OnEvent:
                // 1) 待っているイベントが飛んできた
                if (finishSignaled) { return true; }
                // 2) イベント名が空（設定漏れ）なら詰まないよう決定入力でも送れるようにする
                if (finishSubscription is null) { return IsConfirmAccepted(); }
                // 3) 保険のタイムアウト。待っているイベントが二度と起きない状況
                //    （やり取り中に糸が切れた等）でチュートリアルが詰まるのを防ぐ。
                //    finishSeconds が 0 以下ならタイムアウト無し（イベントを待ち続ける）。
                return step.finishSeconds > 0f && stepElapsed >= step.finishSeconds;

            default:
                return IsConfirmAccepted();
        }
    }

    /// <summary>
    /// 決定入力（送り）が成立したか【送り判定の唯一の実装】。
    ///
    /// 本文がまだ流れている途中なら、決定入力は「全文表示」に消費して送りには使わない
    /// （会話窓と同じ操作感）。手順開始直後の一定時間は、前の手順を送ったクリックが
    /// そのまま次の手順まで送ってしまうのを防ぐため受け付けない。
    /// </summary>
    /// <returns>この手順を送ってよければ true。</returns>
    private bool IsConfirmAccepted()
    {
        if (stepElapsed < SEED.Mathf.Max(stepInputLockSeconds, 0f)) { return false; }

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

    /// <summary>開始条件のイベントを受けたときのハンドラ。</summary>
    private void OnStartEventRaised() { startSignaled = true; }

    /// <summary>終了条件のイベントを受けたときのハンドラ。</summary>
    private void OnFinishEventRaised() { finishSignaled = true; }

    /// <summary>張ってあるイベント購読をすべて外す（手順の切り替え・破棄時に必ず呼ぶ）。</summary>
    private void ClearSubscriptions()
    {
        startSubscription?.Dispose();
        startSubscription = null;
        finishSubscription?.Dispose();
        finishSubscription = null;
        startSignaled  = false;
        finishSignaled = false;
    }

    // ─── 内部処理: 窓の表示 ─────────────────────────────────

    /// <summary>
    /// 現在の手順の内容で説明窓を表示する。
    /// </summary>
    /// <param name="step">現在の手順。</param>
    private void ShowWindow(TutorialStep step)
    {
        if (window is not { } w)
        {
            SEED.Debug.LogWarning("[Tutorial] 説明窓が未設定のため、本文を表示できません。");
            return;
        }

        w.SetText(step.text);

        if (step.anchorMode == TutorialAnchorMode.AboveTarget)
        {
            w.ShowAboveTarget(step.target, step.scale);
            return;
        }

        w.ShowAt(new SEED.Vector2(step.screenX, step.screenY), step.scale);
    }

    // ─── 内部処理: 時間停止と入力制限 ───────────────────────

    /// <summary>
    /// 現在の手順の「時間停止」と「入力制限」を適用する。
    /// 許可フラグは既定 false なので、まず全部止めてから必要なものだけ開ける。
    /// </summary>
    /// <param name="step">現在の手順。</param>
    private void ApplyRestrictions(TutorialStep step)
    {
        SEED.Time.Scale = step.pauseTime ? TimeScalePaused : TimeScaleNormal;

        InputGate.DenyAll();
        InputGate.SetAllowed(GameAction.Move,      step.allowMove);
        InputGate.SetAllowed(GameAction.Ready,     step.allowReady);
        InputGate.SetAllowed(GameAction.Aim,       step.allowAim);
        InputGate.SetAllowed(GameAction.Cast,      step.allowCast);
        InputGate.SetAllowed(GameAction.Reel,      step.allowReel);
        InputGate.SetAllowed(GameAction.Hook,      step.allowHook);
        InputGate.SetAllowed(GameAction.Rhythm,    step.allowRhythm);
        InputGate.SetAllowed(GameAction.UiConfirm, step.allowUiConfirm);
    }

    /// <summary>
    /// 時間停止と入力制限を解除する【制限解除の唯一の出口】。
    /// </summary>
    private void ReleaseRestrictions()
    {
        SEED.Time.Scale = TimeScaleNormal;
        InputGate.AllowAll();
    }

    // ─── 内部処理: 台本（釣りシステムへの強制設定）─────────

    /// <summary>
    /// 現在の手順の台本を釣りシステムへ適用する【台本適用の唯一の入口】。
    ///
    /// チュートリアルは「必ずこの出来事が起きる」必要があるため、
    /// 乱数任せの本編挙動を手順ごとに強制設定で上書きする。
    /// 対象のマネージャがシーンに居ない場合は警告だけ出して進む
    /// （説明そのものは読めるので、チュートリアルを止めるほどではない）。
    /// </summary>
    /// <param name="step">現在の手順。</param>
    private void ApplyScriptedAction(TutorialStep step)
    {
        switch (step.scriptedAction)
        {
            case TutorialScriptedAction.ForceBite:
                ApplyForceBite(step);
                break;

            case TutorialScriptedAction.SpawnDrift:
                ApplySpawnDrift(step);
                break;

            default:
                // None: 何も強制しない（前の手順の台本は AdvanceToNextStep では消さない。
                // 消してしまうと「食いつくまで待つ」手順の途中で強制が外れてしまうため、
                // 解除はチュートリアル完了時・破棄時にまとめて行う）
                break;
        }
    }

    /// <summary>
    /// 「必ず・すぐに食いつく」台本を魚マネージャへ設定する。
    /// </summary>
    /// <param name="step">現在の手順（レベルと待ち秒数を読む）。</param>
    private void ApplyForceBite(TutorialStep step)
    {
        if (FishManager.Current is not { } manager)
        {
            SEED.Debug.LogWarning("[Tutorial] FishManager がシーンに居ないため ForceBite を適用できません。");
            return;
        }

        // レベルは 1 始まりで入力し、内部の添字（0 始まり）へ直す
        int level = step.scriptedLevel > 0 ? step.scriptedLevel : DefaultScriptedLevel;
        manager.SetScriptedSpawn(level - LevelIndexOffset, step.scriptedSeconds, true);
    }

    /// <summary>
    /// 漂流物を 1 個その場に出す台本を漂流物マネージャへ設定する。
    /// </summary>
    /// <param name="step">現在の手順（種類を読む）。</param>
    private void ApplySpawnDrift(TutorialStep step)
    {
        if (DriftItemManager.Current is not { } manager)
        {
            SEED.Debug.LogWarning("[Tutorial] DriftItemManager がシーンに居ないため SpawnDrift を適用できません。");
            return;
        }

        if (!manager.SpawnScriptedNearFloat(step.scriptedParam))
        {
            SEED.Debug.LogWarning($"[Tutorial] 漂流物「{step.scriptedParam}」を出せませんでした（種類名か出現位置を確認してください）。");
        }
    }

    /// <summary>
    /// 台本の強制設定をすべて解除する【台本解除の唯一の出口】。
    /// チュートリアルが終わったら、以降は本編どおりの乱数挙動へ戻す。
    /// </summary>
    private void ClearScriptedActions()
    {
        FishManager.Current?.ClearScripted();
    }
}
