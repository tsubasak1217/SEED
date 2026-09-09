// ============================================================================
//  KaijuStoryTrigger.cs
//  「怪獣を初めて釣り上げた」ときのストーリー会話を起動する条件判定だけを担当する。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// 怪獣（Lv10 の kaiju）初回釣果のストーリー会話トリガ。
/// 専用アクター（KaijuStory）に <see cref="DialogueDirector"/> と並べて付ける。
///
/// 【責務】
///  1. 釣果が怪獣だったかを判定して控える。
///  2. 釣果演出（リザルト）が閉じた瞬間に、条件を満たしていれば会話を開始する。
///  3. 会話中はゲーム時間と入力を止め、会話が終わったら元へ戻して既読フラグを保存する。
///
/// 会話の中身（台詞・カメラ・送り）は <see cref="DialogueDirector"/> の責務であり、
/// このクラスは「いつ始めていつ後始末するか」だけを持つ（単一責任）。
///
/// 【判定を 2 段階に分ける理由】
/// <see cref="FishingController.LastCaughtFish"/> は釣果演出のあいだしか生きておらず、
/// 演出が閉じたあと（<see cref="FishingEvents.CatchPresented"/>）に参照すると
/// 種類が分からなくなる恐れがある。そこで
///  - 演出の開始（<see cref="FishingEvents.Catch"/>）で「怪獣だったか」だけを判定して控え、
///  - 演出が閉じた瞬間に、その控えを見て会話を始める。
/// チュートリアルの CatchTargetMission と同じ手順を踏襲している。
///
/// 【シーン側の設定】
///  - director / controller / fishManager に、それぞれ会話進行役・釣りコントローラ
///    （Player）・魚マネージャ（fishManager）を指定する。
///  - <see cref="DialogueDirector.onDialogueFinished"/> をこのスクリプトの
///    <see cref="OnStoryFinished"/> へ結線する（会話終了の合図はそこからしか来ない）。
///  - <see cref="DialogueDirector.autoStart"/> は false にしておくこと。
/// </summary>
public class KaijuStoryTrigger : SEEDScript
{
    // ── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>
    /// 怪獣の .actor パスに含まれる識別キーの既定値。
    /// <see cref="FishManager.PrefabPathOf"/> が返すパス
    /// （assets://mainGame/actors/Fish/Lv10/kaiju.actor）との部分一致で判定する。
    /// </summary>
    private const string DefaultKaijuPrefabKey = "kaiju";

    /// <summary>ゲーム時間を完全に止めるときの <c>Time.Scale</c>。</summary>
    private const float TimeScalePaused = 0f;

    /// <summary>ゲーム時間を通常速度へ戻すときの <c>Time.Scale</c>。</summary>
    private const float TimeScaleNormal = 1f;

    /// <summary>既読フラグの既定値（未保存なら「まだ見ていない」）。</summary>
    private const bool DefaultStorySeen = false;

    // ── インスペクタ公開フィールド（参照）───────────────────

    /// <summary>会話の進行役。同じアクターに付けた DialogueDirector を指定する。</summary>
    [SerializeField(Label = "会話進行役", Tooltip = "DialogueDirector を付けたアクター（自動開始は false にしておく）")]
    public DialogueDirector? director;

    /// <summary>釣りコントローラ（直前の釣果を読むために使う）。</summary>
    [SerializeField(Label = "釣りコントローラ", Tooltip = "FishingController を付けたアクター（Player）")]
    public FishingController? controller;

    /// <summary>魚マネージャ（釣果が「どの .actor から生まれたか」を引くために使う）。</summary>
    [SerializeField(Label = "魚マネージャ", Tooltip = "FishManager を付けたアクター（fishManager）")]
    public FishManager? fishManager;

    // ── インスペクタ公開フィールド（値）─────────────────────

    /// <summary>怪獣と判定する .actor パスの部分一致キー。</summary>
    [SerializeField(Label = "怪獣の識別キー", Tooltip = "釣果の .actor パスにこの文字列が含まれていれば怪獣とみなす")]
    public string kaijuPrefabKey = DefaultKaijuPrefabKey;

    /// <summary>
    /// デバッグ用。true にすると「怪獣かどうか」も「既読かどうか」も無視して、
    /// 釣果演出が閉じるたびに会話を再生する（台詞やカメラの確認用）。
    /// </summary>
    [SerializeField(Label = "デバッグ強制再生", Tooltip = "true なら魚種と既読を無視して毎回会話を再生する（パッケージ版では無視される）")]
    public bool debugForceStory;

    /// <summary>
    /// <see cref="debugForceStory"/> の実効値【強制再生を読む唯一の入口】。
    ///
    /// インスペクタのフラグはシーンへ保存されるため、開発中の設定が付いたまま
    /// 出荷されると本編で毎回会話が再生されてしまう。パッケージ版（配布ビルド）では
    /// <see cref="SEED.Application.IsDebugAllowed"/> が false になるので必ず無効になる。
    /// </summary>
    private bool DebugForceStory => debugForceStory && SEED.Application.IsDebugAllowed;

    // ── 内部状態 ────────────────────────────────────────────

    /// <summary>釣果演出の開始時に控えた「いま釣った魚は怪獣だったか」。</summary>
    private bool _pendingKaiju;

    /// <summary>釣果の控えが有効か（演出の開始を受け取っていれば true）。</summary>
    private bool _hasPendingCatch;

    /// <summary>ストーリー会話を再生中か（時間・入力を戻す責任があるか）。</summary>
    private bool _storyPlaying;

    // ── 公開プロパティ ──────────────────────────────────────

    /// <summary>ストーリー会話を再生中か（他スクリプトが割り込みを避けるために読む）。</summary>
    public bool IsStoryPlaying => _storyPlaying;

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 釣果イベントを購読する。
    /// <c>this.On</c> はスクリプト破棄時に自動解除されるので解除漏れが起きない。
    /// </summary>
    public override void OnStart()
    {
        // 引数（魚の表示名）は使わないが、購読側の引数の型は発火側（string）に合わせる必要がある。
        // 型を省略すると On(string, Action<string>) と On(string, Action<float>) が
        // 曖昧になるため、ラムダの引数型を明示する。
        this.On(FishingEvents.Catch,          (string _) => OnCatchBegan());
        this.On(FishingEvents.CatchPresented, (string _) => OnCatchPresented());
    }

    /// <summary>
    /// 破棄時の後始末。会話の途中でシーンが切り替わっても、
    /// 時間停止と入力制限が焼き付いたまま残らないようにする。
    /// </summary>
    public override void OnDestroy()
    {
        if (!_storyPlaying) { return; }
        _storyPlaying = false;
        RestoreGameplay();
    }

    // ── 公開メソッド（ScriptEvent から呼ばれる）─────────────

    /// <summary>
    /// ストーリー会話が終わった瞬間の処理【後始末の唯一の出口】。
    /// <see cref="DialogueDirector.onDialogueFinished"/> から結線して呼ばせる。
    ///
    /// 時間と入力を戻し、既読フラグを保存する。
    /// 保存はここでしか行わないので、途中でシーンを抜けた場合は既読にならない
    /// （＝次回また最初から見られる）。
    /// </summary>
    public void OnStoryFinished()
    {
        // 自分が始めていない会話の終了通知は無視する（二重の後始末を防ぐ）
        if (!_storyPlaying) { return; }
        _storyPlaying = false;

        RestoreGameplay();

        SEED.SaveData.SetBool(GameProgressKeys.KaijuStorySeen, true);
        SEED.SaveData.Save();

        SEED.Debug.Log("[KaijuStory] 怪獣のストーリー会話を再生し終えました（既読フラグを保存）。");
    }

    // ── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 釣果演出が始まった瞬間の処理。<b>種類の判定だけ</b>を行って控える。
    /// </summary>
    private void OnCatchBegan()
    {
        _pendingKaiju    = IsKaijuCaught();
        _hasPendingCatch = true;
    }

    /// <summary>
    /// 釣果演出（リザルト）が閉じた瞬間の処理。条件を満たしていれば会話を始める。
    /// </summary>
    private void OnCatchPresented()
    {
        // 控えは 1 回の釣果につき 1 回だけ使う
        bool wasKaiju = _hasPendingCatch && _pendingKaiju;
        _hasPendingCatch = false;
        _pendingKaiju    = false;

        if (!ShouldStartStory(wasKaiju)) { return; }

        StartStory();
    }

    /// <summary>
    /// いまストーリー会話を始めてよいか【開始条件の唯一の判定】。
    /// </summary>
    /// <param name="wasKaiju">直前の釣果が怪獣だったか。</param>
    /// <returns>会話を始めてよければ true。</returns>
    private bool ShouldStartStory(bool wasKaiju)
    {
        // すでに会話中なら重ねて始めない
        if (_storyPlaying) { return false; }

        // チュートリアル中は TutorialDirector が時間停止と入力制限を握っている。
        // ここで割り込むと後始末が競合して操作不能になり得るので開始しない
        // （チュートリアルの締めで怪獣を釣る演出はチュートリアル側の責務）。
        if (TutorialRules.Active) { return false; }

        // デバッグ強制再生は魚種も既読も無視する（パッケージ版では実効値が false になる）
        if (DebugForceStory) { return true; }

        if (!wasKaiju) { return false; }

        return !SEED.SaveData.GetBool(GameProgressKeys.KaijuStorySeen, DefaultStorySeen);
    }

    /// <summary>
    /// ストーリー会話を開始し、ゲーム側（時間・入力・カメラ追従）を止める。
    /// </summary>
    private void StartStory()
    {
        if (director is not { } dialogue)
        {
            SEED.Debug.LogWarning("[KaijuStory] 会話進行役（director）が未設定です。会話を開始できません。");
            return;
        }

        // 先に「再生中」を立ててから開始する。
        // 会話データが空のとき DialogueDirector は同期で終了イベントを発火するため、
        // 立てる順序を逆にすると OnStoryFinished が素通りして後始末が漏れる。
        _storyPlaying = true;

        SuspendGameplay();
        dialogue.StartDialogue();
    }

    /// <summary>
    /// 会話中のあいだゲームを止める（時間・入力・カメラ追従）。
    /// </summary>
    private void SuspendGameplay()
    {
        // 魚も漂流物もアニメーションも止める（明転のまま世界だけ静止させる）
        SEED.Time.Scale = TimeScalePaused;

        // 釣り操作を全面的に遮断する。会話送り（決定キー／左クリック）は
        // DialogueDirector が InputGate を通さず直接読むので影響を受けない。
        InputGate.DenyAll();

        // CameraMove の毎フレーム追従を止める。
        // CameraMove は「TutorialRules.Active かつ CameraSuspended」でだけ手を引くので、
        // TutorialDirector と同じく Active も併せて立てる
        // （ここへ来るのは TutorialRules.Active == false のときだけなので、
        //   チュートリアルの状態を踏み荒らすことはない）。
        TutorialRules.Active          = true;
        TutorialRules.CameraSuspended = true;
    }

    /// <summary>
    /// 会話のために止めていたものをすべて元へ戻す【復帰の唯一の実装】。
    /// </summary>
    private void RestoreGameplay()
    {
        SEED.Time.Scale = TimeScaleNormal;
        InputGate.AllowAll();

        TutorialRules.CameraSuspended = false;
        TutorialRules.Active          = false;
    }

    /// <summary>
    /// 直前に釣った魚が怪獣か【種類判定の唯一の実装】。
    /// 参照が 1 つでも欠けていたら false（＝会話を出さない）を返す。
    /// </summary>
    /// <returns>怪獣なら true。</returns>
    private bool IsKaijuCaught()
    {
        if (string.IsNullOrWhiteSpace(kaijuPrefabKey)) { return false; }
        if (controller is not { } fishing) { return false; }
        if (fishing.LastCaughtFish is not { } fish) { return false; }
        if (fishManager is not { } manager) { return false; }

        string prefabPath = manager.PrefabPathOf(fish.Actor);
        return !string.IsNullOrEmpty(prefabPath)
            && prefabPath.Contains(kaijuPrefabKey, System.StringComparison.OrdinalIgnoreCase);
    }
}
