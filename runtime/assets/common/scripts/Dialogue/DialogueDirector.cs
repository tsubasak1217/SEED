// ============================================================================
//  DialogueDirector.cs
//  会話の進行（どの行を、いつ、どう送るか）だけを担当する。
// ============================================================================

using System.Collections.Generic;
using SEEDEditor.Scripting;

/// <summary>
/// 会話進行の司令塔コンポーネント。専用アクター（DialogueDirector）に付ける。
///
/// 【責務】
///  1. 会話データ（DialogueEntry のリスト）を先頭から順に進める。
///  2. 送り入力を判定する（決定キー／マウス左クリック）。
///  3. 各行の開始・終了イベントと、全会話終了イベントを発火する。
///  4. 会話前のカメラ姿勢を控えさせ、会話終了時にそこへ戻させる
///     （控える／戻すの実装は DialogueCameraDirector 側）。
///
/// 表示（文字送り・名札）は DialogueWindow、カメラ移動は DialogueCameraDirector が
/// 担当し、このクラスはそれらへ指示を出すだけ（単一責任）。
///
/// 【送り入力の仕様】
///  - 本文がまだ流れている間 → 全文表示（CompleteText）
///  - 本文を出し切っている   → その行の終了イベントを発火して次の行へ
///  最後の行を送ると窓を閉じ、onDialogueFinished を発火する。
///
/// 【シーン側の設定】
///  - window / cameraDirector に、それぞれ会話窓アクターと MainCamera を指定する。
///  - onDialogueFinished に「会話後にやること」（例: PrologueFlow.GoToMainGame）を結線する。
///  - 「終了時にカメラを戻す」を切ると、会話終了後もカメラは最後の台詞の構図のまま残る。
///    会話の直後にシーン遷移する構成（プロローグ）では、戻す動きが見えるので切ってよい。
///
/// 【終了イベントのタイミング】
///  「終了時にカメラを戻す」かつ戻り方が Lerp のときだけ、onDialogueFinished は
///  <b>カメラが戻り終わってから</b>発火する（それ以外は従来どおり即時発火）。
/// </summary>
public class DialogueDirector : SEEDScript
{
    // ── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>会話データの先頭インデックス。</summary>
    private const int FirstEntryIndex = 0;

    /// <summary>「終了時の補間時間(秒)」の既定値。</summary>
    private const float DefaultFinishReturnDuration = 1f;

    /// <summary>
    /// カメラ戻しの完了を待つ上限に足す余裕（秒）。
    ///
    /// 待ちの上限は「終了時の補間時間 + この値」。カメラ側が何らかの理由で
    /// 進まなくなっても（Time.Scale = 0 のまま実時間 off、スクリプトの差し替え等）、
    /// ここで必ず打ち切って終了イベントを発火する。
    /// 終了イベントの先で入力制限の解除やシーン遷移が行われるため、
    /// 待ち続けると操作不能のまま詰んでしまう。
    /// </summary>
    private const float ReturnWaitGraceSeconds = 1f;

    // ── 進行段階 ────────────────────────────────────────────

    /// <summary>会話の進行段階。</summary>
    private enum DialogueState
    {
        /// <summary>まだ開始していない（窓は閉じている）。</summary>
        Idle,
        /// <summary>会話中。</summary>
        Playing,
        /// <summary>
        /// 台詞は全て終わり、カメラを会話開始前の姿勢へ戻している最中。
        /// 戻り終わってから <see cref="onDialogueFinished"/> を発火する
        /// （終了イベントでカメラ追従の再開やシーン遷移が走るため、
        ///  戻り切る前に渡すと画が飛ぶ）。
        /// </summary>
        ReturningCamera,
        /// <summary>全会話が終わった（以降は入力を受け付けない）。</summary>
        Finished,
    }

    // ── インスペクタ公開フィールド ──────────────────────────

    /// <summary>会話データ。1 件が 1 行の台詞に対応する。</summary>
    [SerializeField(Label = "会話データ", Tooltip = "先頭から順に再生される台詞のリスト")]
    public List<DialogueEntry> entries = new();

    /// <summary>表示を担当する会話窓。</summary>
    [SerializeField(Label = "会話窓", Tooltip = "DialogueWindow を付けた会話窓アクター")]
    public DialogueWindow? window;

    /// <summary>カメラ移動を担当するコンポーネント（MainCamera に付いているもの）。</summary>
    [SerializeField(Label = "カメラ演出", Tooltip = "DialogueCameraDirector を付けたカメラアクター")]
    public DialogueCameraDirector? cameraDirector;

    /// <summary>シーン開始と同時に会話を始めるか。</summary>
    [SerializeField(Label = "自動開始", Tooltip = "シーン開始と同時に会話を始める")]
    public bool autoStart = true;

    /// <summary>
    /// 会話が終わったとき、カメラを<b>会話開始前の姿勢（位置・回転・画角）</b>へ戻すか。
    ///
    /// 戻し先は会話開始時（最初のカメラ移動より前）に控える。
    /// 会話の直後にシーン遷移するような構成（プロローグなど）では、
    /// 戻す動きが見えてしまうので false にする。
    /// </summary>
    [SerializeField(Label = "終了時にカメラを戻す", Tooltip = "会話開始前の位置・回転・画角へカメラを戻す（直後にシーン遷移する会話では false 推奨）")]
    public bool restoreCameraOnFinish = true;

    /// <summary>終了時にカメラを戻す方法（Cut = 即座に / Lerp = 補間して）。</summary>
    [SerializeField(Label = "終了時の戻り方", Tooltip = "Cut = 即座に戻す / Lerp = 補間しながら戻す")]
    public DialogueCameraMode finishReturnMode = DialogueCameraMode.Lerp;

    /// <summary>終了時に Lerp で戻すときの時間（秒）。Cut のときは使われない。</summary>
    [SerializeField(Label = "終了時の補間時間(秒)", Tooltip = "終了時の戻り方が Lerp のときの戻し時間")]
    public float finishReturnDuration = DefaultFinishReturnDuration;

    /// <summary>全会話が終わった瞬間に呼ぶイベント（シーン遷移などを結線する）。</summary>
    [SerializeField(Label = "会話終了時", Tooltip = "最後の台詞を送り終えた瞬間に呼ばれる")]
    public SEED.ScriptEvent onDialogueFinished;

    // ── 内部状態 ────────────────────────────────────────────

    /// <summary>現在の進行段階。</summary>
    private DialogueState _state = DialogueState.Idle;

    /// <summary>現在表示中の行のインデックス。</summary>
    private int _index = FirstEntryIndex;

    /// <summary>初期化（窓を閉じる・自動開始）を済ませたか。</summary>
    private bool _bootstrapped;

    /// <summary>
    /// 終了後のカメラ戻しを待っている時間（秒・実時間）。
    /// <see cref="ReturnWaitGraceSeconds"/> の打ち切り判定に使う。
    /// </summary>
    private float _returnWaitElapsed;

    // ── 公開プロパティ ──────────────────────────────────────

    /// <summary>会話中か。</summary>
    public bool IsPlaying => _state == DialogueState.Playing;

    /// <summary>
    /// 全会話が終わっているか。
    /// 終了後のカメラ戻し（<see cref="DialogueState.ReturningCamera"/>）の最中も
    /// 台詞自体は終わっているため true を返す。
    /// </summary>
    public bool IsFinished => _state is DialogueState.ReturningCamera or DialogueState.Finished;

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 毎フレーム、初期化と送り入力の処理を行う。
    ///
    /// 初期化を OnStart ではなく最初の Update で行うのは、参照先スクリプト
    /// （DialogueWindow）の OnStart が自分より先に走る保証が無いため
    /// （docs/scripting_api.md「自作スクリプトへの参照」）。
    /// Update まで待てば全スクリプトの OnStart が済んでいる。
    /// </summary>
    /// <param name="ctx">フレーム情報（未使用）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (!_bootstrapped)
        {
            _bootstrapped = true;

            // 会話が始まるまで窓は閉じておく
            window?.Hide();
            if (autoStart) StartDialogue();
            return;   // 開始フレームの入力は送りに使わない（開幕即送りを防ぐ）
        }

        // 終了後のカメラ戻しの最中は、戻り終わりを待ってから終了イベントを発火する
        if (_state == DialogueState.ReturningCamera)
        {
            // 待ちの計測は必ず実時間で行う（カメラ側がゲーム時間で止まっていても打ち切れるように）
            _returnWaitElapsed += SEED.Time.UnscaledDeltaTime;
            bool waitExpired = _returnWaitElapsed >= finishReturnDuration + ReturnWaitGraceSeconds;

            if (!waitExpired && cameraDirector is { IsMoving: true }) return;

            // 打ち切る場合は中途半端な姿勢で残さず、戻り先へ合わせてから終わる
            if (waitExpired) { cameraDirector?.SnapToTarget(); }

            FireFinished();
            return;
        }

        if (_state != DialogueState.Playing) return;
        if (!IsAdvancePressed()) return;

        Advance();
    }

    // ── 公開メソッド（ScriptEvent から呼べる）───────────────

    /// <summary>
    /// 会話を先頭から開始する。会話データが空なら即座に終了扱いにする
    /// （結線した後続処理が実行されないまま止まるのを避けるため）。
    /// </summary>
    public void StartDialogue()
    {
        _index = FirstEntryIndex;

        if (entries is null || entries.Count <= FirstEntryIndex)
        {
            SEED.Debug.LogWarning("[DialogueDirector] 会話データが空です。そのまま終了イベントを発火します。");
            Finish();
            return;
        }

        _state = DialogueState.Playing;

        // 会話前のカメラ姿勢（位置・回転・画角）を控える。
        // 最初のカメラ移動（BeginEntry → MoveTo）より前に行わないと、
        // 1 行目の目標姿勢を「会話前の姿勢」として控えてしまう。
        if (restoreCameraOnFinish) { cameraDirector?.CaptureReturnPoint(); }

        window?.Show();
        BeginEntry(_index);
    }

    /// <summary>
    /// 会話を途中で打ち切る。
    /// 現在の行の終了イベントだけは発火してから終わる
    /// （その行の onStart で変えた状態を onEnd で戻す作りを壊さないため）。
    /// </summary>
    public void Skip()
    {
        if (_state != DialogueState.Playing) return;

        InvokeSafely(CurrentEntryOnEnd());
        Finish();
    }

    // ── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 送り入力が押された瞬間か。
    ///
    /// 決定キーの判定は SceneFlow.IsConfirmPressed に集約されているのでそれを使い、
    /// 会話送りでだけ有効にしたいマウス左クリックをここで OR する
    /// （SceneFlow 側を変えると他シーンの操作感まで変わってしまうため）。
    ///
    /// 受け付けてよいかの判断は InputGate（ゲーム入力の唯一の関門）に任せる。
    /// ポーズ中とポーズを閉じたそのフレームは、ここで false になる。
    /// </summary>
    /// <returns>このフレームに送り入力があったら true。</returns>
    private static bool IsAdvancePressed()
        => InputGate.Allows(GameAction.Advance)
        && (SceneFlow.IsConfirmPressed()
         || SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left));

    /// <summary>
    /// 送り入力 1 回ぶんの処理。
    /// 本文が流れている途中なら全文表示、出し切っていれば次の行へ進む。
    /// </summary>
    private void Advance()
    {
        // 窓が無い構成では文字送りの状態を判断できないので、そのまま次の行へ送る
        if (window is { } w && !w.IsTextComplete)
        {
            w.CompleteText();
            return;
        }

        // 現在の行を終える
        InvokeSafely(CurrentEntryOnEnd());

        _index++;
        if (_index >= entries.Count)
        {
            Finish();
            return;
        }

        BeginEntry(_index);
    }

    /// <summary>
    /// 指定した行の表示を開始する（イベント発火 → 名札 → 本文 → カメラ）。
    /// </summary>
    /// <param name="index">開始する行のインデックス。</param>
    private void BeginEntry(int index)
    {
        var entry = entries[index];

        // 行の開始イベントは、表示より先に呼ぶ（表情差し替えなどを反映させるため）
        InvokeSafely(entry.onStart);

        window?.SetSpeaker(entry.speaker);
        window?.BeginText(entry.text);

        cameraDirector?.MoveTo(entry.cameraTarget, entry.cameraMode, entry.lerpDuration);
    }

    /// <summary>
    /// 会話を終了する（窓を閉じ、カメラを戻し、終了イベントを発火する）。
    ///
    /// カメラを<b>補間で</b>戻す場合だけは、戻り終わるまで終了イベントを遅らせる
    /// （<see cref="DialogueState.ReturningCamera"/>）。終了イベントの先では
    /// カメラ追従の再開（MainGame）やシーン遷移（プロローグ）が走るため、
    /// 戻り切る前に渡すと画が飛ぶ。
    /// 即時（Cut）で戻した場合・戻さない場合・戻り先が無い場合は、
    /// 従来どおり<b>その場で同期的に</b>発火する
    /// （会話データが空のときに同期発火する前提へ依存している呼び出し側がある）。
    /// 遅らせる場合でも待ちには上限があり（<see cref="ReturnWaitGraceSeconds"/>）、
    /// 必ず終了イベントへ到達する。
    /// </summary>
    private void Finish()
    {
        window?.Hide();

        if (restoreCameraOnFinish
            && cameraDirector is { } camera
            && camera.ReturnToCapturedPoint(finishReturnMode, finishReturnDuration))
        {
            _state             = DialogueState.ReturningCamera;
            _returnWaitElapsed = 0f;
            return;
        }

        FireFinished();
    }

    /// <summary>
    /// 会話終了イベントを発火して完全に終わる【終了の唯一の出口】。
    /// </summary>
    private void FireFinished()
    {
        _state = DialogueState.Finished;
        InvokeSafely(onDialogueFinished);
    }

    /// <summary>
    /// 現在の行の終了イベントを取り出す（範囲外なら null）。
    /// </summary>
    /// <returns>終了イベント。取り出せなければ null。</returns>
    private SEED.ScriptEvent? CurrentEntryOnEnd()
    {
        if (entries is null) return null;
        if (_index < FirstEntryIndex || _index >= entries.Count) return null;
        return entries[_index].onEnd;
    }

    /// <summary>
    /// ScriptEvent を安全に発火する。
    ///
    /// 通常 ScriptEvent はエンジンが実体を注入するため null にならないが、
    /// 構造体リストの要素は既定値（null）で作られる経路があり得るため、
    /// 呼び出し側で 1 か所にまとめて null を吸収する。
    /// </summary>
    /// <param name="scriptEvent">発火するイベント（null なら何もしない）。</param>
    private static void InvokeSafely(SEED.ScriptEvent? scriptEvent)
        => scriptEvent?.Invoke();
}
