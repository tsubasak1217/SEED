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
///  - onDialogueFinished に「会話後にやること」（例: PrologueFlow.GoToTutorial）を結線する。
/// </summary>
public class DialogueDirector : SEEDScript
{
    // ── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>会話データの先頭インデックス。</summary>
    private const int FirstEntryIndex = 0;

    // ── 進行段階 ────────────────────────────────────────────

    /// <summary>会話の進行段階。</summary>
    private enum DialogueState
    {
        /// <summary>まだ開始していない（窓は閉じている）。</summary>
        Idle,
        /// <summary>会話中。</summary>
        Playing,
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

    // ── 公開プロパティ ──────────────────────────────────────

    /// <summary>会話中か。</summary>
    public bool IsPlaying => _state == DialogueState.Playing;

    /// <summary>全会話が終わっているか。</summary>
    public bool IsFinished => _state == DialogueState.Finished;

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
    /// </summary>
    /// <returns>このフレームに送り入力があったら true。</returns>
    private static bool IsAdvancePressed()
        => SceneFlow.IsConfirmPressed()
        || SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left);

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
    /// 会話を終了する（窓を閉じ、終了イベントを発火する）。
    /// </summary>
    private void Finish()
    {
        _state = DialogueState.Finished;
        window?.Hide();
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
