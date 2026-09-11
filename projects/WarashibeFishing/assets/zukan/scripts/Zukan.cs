// ============================================================================
//  Zukan.cs
//  図鑑シーンの本体（レベルごとに 1 ページ。捕獲状況をカードで並べる）。
// ============================================================================

using System.Collections.Generic;
using SEEDEditor.Scripting;

/// <summary>
/// 魚図鑑の表示【図鑑ページ構築の唯一の置き場】。
///
/// 【責務】
/// <c>FishCatalog</c>（何の魚がいるか）と <c>FishRecords</c>（何を釣ったか）を
/// 突き合わせて、カード（<see cref="ZukanCard"/>）へ「どの魚を出すか」を配る。
/// カード 1 枚の見た目（シルエット・文言）は <see cref="ZukanCard"/> の責務で、
/// こちらはページングとカードの配置だけを行う。
///
/// 【ページの単位】
/// 魚レベル 1 ページ（Lv1 〜 <c>FishCatalog.MaxLevel</c>）。
/// そのレベルの魚を <c>FishCatalog.ForLevel</c> の順（＝アクタ名順）で左から並べる。
///
/// 【カード枠の持ち方 — プレハブインスタンス】
/// カードはプレハブ <c>assets://zukan/actors/ZukanCard.actor</c> のインスタンスとして
/// シーンへ並べてある（ZukanCard0 … ZukanCard3）。
/// 子アクタ（Image / Name / …）への参照はプレハブ側が
/// <b>相対パス（<c>./Image</c>）</b>で持つため、名前が重複しても壊れない。
/// 枠を増やすときは、シーンへプレハブをもう 1 つ置いて
/// <see cref="cards"/> へ結線するだけでよい（見た目はプレハブ側を直せば全枚に反映される）。
/// </summary>
public class Zukan : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>最初に開くページ（＝魚レベル）。</summary>
    private const int FirstLevel = 1;

    /// <summary>ページを 1 つ戻す向き。</summary>
    public const int PageStepPrev = -1;

    /// <summary>ページを 1 つ進める向き。</summary>
    public const int PageStepNext = 1;

    /// <summary>不透明を表すアルファ。</summary>
    private const float AlphaOpaque = 1f;

    /// <summary>完全に透明（＝隠す）を表すアルファ。</summary>
    private const float AlphaHidden = 0f;

    /// <summary>カードを左右対称に並べるための中心係数（(n-1)/2 の 2）。</summary>
    private const float CenteringDivisor = 2f;

    /// <summary>
    /// デバッグコマンド名: 釣果記録（図鑑）を全消しする。
    /// <c>seed_script_debug(name:"records_reset")</c> で叩く（引数は不要）。
    /// 釣りシーン側（<c>FishingController</c>）も同じ名前で登録しており、
    /// どちらのシーンに居ても同じ 1 手で図鑑を初期状態へ戻せる。
    /// </summary>
    private const string DebugCommandRecordsReset = "records_reset";

    /// <summary>長押し判定を「押していない」に戻すときの経過秒。</summary>
    private const float HoldElapsedNone = 0f;

    // ─── 静的アクセサ ────────────────────────────────────────

    /// <summary>実行中のインスタンス（矢印スプライトのクリックから呼ぶ）。</summary>
    public static Zukan? Current { get; private set; }

    // ─── インスペクタ設定（文言・書式）─────────────────────────

    /// <summary>見出しの書式（{0} にレベル番号が入る）。</summary>
    [Header("文言"), SerializeField(Label = "見出しの書式")]
    private string headerFormat = "ずかん   Lv {0}";

    /// <summary>ページ位置の書式（{0} = 現在のレベル / {1} = 最大レベル）。</summary>
    [SerializeField(Label = "ページ表示の書式")]
    private string pageFormat = "{0} / {1}";

    /// <summary>操作案内の文言。</summary>
    [SerializeField(Label = "操作案内")]
    private string hintLabel = "A / D ・ ← → でページ切り替え     Esc / B でもどる";

    // ─── インスペクタ設定（レイアウト）─────────────────────────

    /// <summary>カードの横間隔（キャンバスピクセル）。</summary>
    [Header("レイアウト"), SerializeField(Label = "カードの横間隔")]
    private float cardSpacing = 290f;

    /// <summary>カードを並べる帯の Y 座標（カード枠の親から見た相対）。</summary>
    [SerializeField(Label = "カード列のY")]
    private float cardRowY = 0f;

    // ─── インスペクタ設定（遷移）───────────────────────────────

    /// <summary>戻り先のシーン名を保存してあるセーブキー。</summary>
    [Header("遷移"), SerializeField(Label = "戻り先のセーブキー")]
    private string returnSceneKey = "zukan_return";

    /// <summary>
    /// フェード演出つきのシーン遷移を担う <see cref="SceneFlow"/>（シーンに置いた "Flow" アクタ）。
    ///
    /// 結線されていれば <see cref="SceneFlow.GoTo"/>（フェードアウト → 切替）を通す。
    /// 未設定・解決失敗のときは従来どおり即座に切り替える（演出は落ちるが退出はできる）。
    /// </summary>
    [SerializeField(Label = "シーン遷移(SceneFlow)", Tooltip = "シーンに置いた Flow アクタを指定する。未設定ならフェード無しの即時遷移になる")]
    private SceneFlow? sceneFlow;

    // ─── インスペクタ設定（BGM）──────────────────────────────

    /// <summary>
    /// 図鑑を開いている間ループ再生する BGM（<c>assets://</c> パス）。空なら鳴らさない。
    /// 開始時に <see cref="SEED.Audio.PlayBgm"/> で流し、図鑑を閉じるときに止める
    /// （戻り先のシーンは自分の BGM を自分で流す前提）。
    /// </summary>
    [Header("BGM"), SerializeField(Label = "BGM(パス)"), AssetReference("mp3", "wav", "ogg")]
    private string bgmPath = "assets://zukan/audios/bgm.mp3";

    /// <summary>BGM の音量（1.0 = 等倍）。</summary>
    [SerializeField(Label = "BGMの音量")]
    private float bgmVolume = 0.6f;

    /// <summary>戻り先が保存されていないときの既定のシーン名。</summary>
    [SerializeField(Label = "既定の戻り先シーン")]
    private string defaultReturnScene = "title";

    // ─── インスペクタ設定（デバッグ）───────────────────────────

    /// <summary>
    /// 図鑑を初期状態へ戻すデバッグ用のキー【開発中の確認用。製品では無効化する】。
    ///
    /// 「未捕獲のシルエット」「初捕獲の演出」は一度釣ると二度と確認できないため、
    /// 図鑑を見ながらその場で戻せる口を用意する。
    /// </summary>
    [Header("デバッグ"), SerializeField(Label = "図鑑リセットのキー")]
    private SEED.KeyCode debugResetKey = SEED.KeyCode.F12;

    /// <summary>
    /// <see cref="debugResetKey"/> を消去が実行されるまで押し続ける必要のある秒数。
    ///
    /// <b>なぜ長押しなのか</b>: 釣果はやり直しの効かないデータで、
    /// 単押しだと「図鑑を見ていたら手が滑って全部消えた」が起こり得る。
    /// 押し続けを必須にすることで、意図しない実行をほぼ確実に防ぐ。
    /// 0 以下にすると単押しで消える（自己責任）。
    /// </summary>
    [SerializeField(Label = "図鑑リセットの長押し秒数")]
    private float debugResetHoldSeconds = 1.5f;

    /// <summary>
    /// リセットを実行したあと操作案内へ出す文言（<c>{0}</c> に削除したキーの本数が入る）。
    /// 空にすると案内を書き換えない。
    /// </summary>
    [SerializeField(Label = "リセット後の案内")]
    private string debugResetDoneLabel = "図鑑をリセットしました（削除 {0} 件）";

    // ─── 参照（インスペクタで結線）─────────────────────────────

    /// <summary>見出しのテキスト。</summary>
    [Header("参照（ヘッダ・フッタ）"), SerializeField(Label = "見出しText")]
    private SEED.Text? headerText;

    /// <summary>ページ位置のテキスト。</summary>
    [SerializeField(Label = "ページ表示Text")]
    private SEED.Text? pageText;

    /// <summary>操作案内のテキスト。</summary>
    [SerializeField(Label = "操作案内Text")]
    private SEED.Text? hintText;

    /// <summary>
    /// カード（プレハブインスタンスのスクリプト）。要素数＝枠の数。
    /// 解決できなかった要素は null になるので、必ず null チェックしてから使う。
    /// </summary>
    [Header("参照（カード枠。要素数＝枠の数）"), SerializeField(Label = "カード")]
    private List<ZukanCard?> cards = new();

    // ─── 実行時の状態 ────────────────────────────────────────

    /// <summary>いま表示しているページ（＝魚レベル）。</summary>
    private int currentLevel = FirstLevel;

    /// <summary>
    /// デバッグコマンドを登録済みか（パッケージ版では登録しないので常に false）。
    /// <c>OnDestroy</c> の解除を登録と対称にするために持つ。
    /// </summary>
    private bool debugCommandsRegistered;

    /// <summary>
    /// <see cref="debugResetKey"/> を押し続けている秒数（実時間）。
    /// 離した瞬間に 0 へ戻すので、断続的に押しても貯まらない。
    /// </summary>
    private float debugResetHeldSeconds = HoldElapsedNone;

    /// <summary>
    /// 最初のページ組み立てがまだ済んでいないか。
    ///
    /// 【なぜ OnStart で組み立てないか】
    /// カードは別スクリプト（<see cref="ZukanCard"/>）のインスタンスで、
    /// そのスクリプトの <c>gameObject</c> / <c>transform</c> は
    /// **自分のライフサイクル呼び出しが 1 度走るまで束縛されない**。
    /// スクリプトの OnStart 実行順は保証されないため、Zukan の OnStart から
    /// カードのメソッドを呼ぶと、カード側は自分のアクタを掴めておらず
    /// 表示・位置設定がまるごと空振りする（実際にそうなって図鑑が真っ白になった）。
    /// そこで最初の組み立ては <see cref="Update"/>（＝全スクリプトが 1 度は
    /// フェーズを通った後）まで遅らせる。
    /// </summary>
    private bool pageDirty = true;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>開始時の初期化。1 ページ目を組み立てる。</summary>
    public override void OnStart()
    {
        Current = this;

        // 図鑑はマウスで矢印を押せる必要があるので、カーソルロックは必ず外す
        // （釣りシーンからロック状態のまま遷移してくる経路があるため）。
        SEED.Input.CursorLocked = false;

        // 図鑑の BGM をループで流す（前のシーンの BGM があれば置き換わる）。
        if (!string.IsNullOrWhiteSpace(bgmPath))
        {
            SEED.Audio.PlayBgm(bgmPath, bgmVolume, loop: true);
        }

        SetContent(hintText, hintLabel);
        currentLevel = FirstLevel;
        // 実際の組み立ては最初の Update で行う（pageDirty の説明を参照）
        pageDirty = true;

        // 開発用のデバッグコマンドを登録する（エディタ／MCP から叩ける）。
        // パッケージ版（配布ビルド）では釣果の全消しを外から叩けないよう、登録自体を行わない。
        debugCommandsRegistered = SEED.Application.IsDebugAllowed;
        if (debugCommandsRegistered)
        {
            SEED.Debug.OnCommand(DebugCommandRecordsReset, HandleRecordsResetCommand);
        }
    }

    /// <summary>破棄時の後始末。静的アクセサを取り消す。</summary>
    public override void OnDestroy()
    {
        // 破棄したスクリプトのハンドラが呼ばれ続けないよう、必ず外す
        //（登録したときだけ外して、登録・解除を対称に保つ）。
        if (debugCommandsRegistered)
        {
            debugCommandsRegistered = false;
            SEED.Debug.OffCommand(DebugCommandRecordsReset, HandleRecordsResetCommand);
        }
        if (ReferenceEquals(Current, this)) { Current = null; }
    }

    /// <summary>毎フレームの入力処理（ページ送りと退出）。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 最初のページ組み立て（カードのライフサイクルが 1 度走った後に行う）
        if (pageDirty)
        {
            pageDirty = false;
            BuildPage();
        }

        // 退出のフェード中は一切の操作を受け付けない（二重遷移・暗転中のページ送りを防ぐ）
        if (sceneFlow is { } flow && flow.IsTransitioning) { return; }

        if (SEED.Input.GetKeyDown(SEED.KeyCode.A) || SEED.Input.GetKeyDown(SEED.KeyCode.LeftArrow))
        {
            ChangePage(PageStepPrev);
        }
        if (SEED.Input.GetKeyDown(SEED.KeyCode.D) || SEED.Input.GetKeyDown(SEED.KeyCode.RightArrow))
        {
            ChangePage(PageStepNext);
        }
        if (SEED.Input.GetKeyDown(SEED.KeyCode.Escape) || SEED.Input.GetKeyDown(SEED.KeyCode.B))
        {
            LeaveZukan();
        }

        UpdateDebugReset();
    }

    // ─── デバッグ（釣果リセット）──────────────────────────────

    /// <summary>
    /// 図鑑リセットの長押しを進める【キー操作からのリセットの唯一の場所】。
    ///
    /// 押し続けている間だけ秒を貯め、<see cref="debugResetHoldSeconds"/> に達した
    /// <b>その 1 フレームだけ</b>実行する（達したあとは押しっぱなしでも二度実行しない）。
    /// 途中で離すと貯めた秒は捨てる。時間は実時間（<c>UnscaledDeltaTime</c>）で数える
    /// （図鑑はゲーム時間を止めていることがあるため）。
    /// </summary>
    private void UpdateDebugReset()
    {
        // パッケージ版（配布ビルド）ではキー入力の判定ごと行わない【ゲートはこの 1 か所】。
        // 貯めた秒も初期値へ戻して、押しっぱなしのまま許可が変わっても誤爆しないようにする。
        if (!SEED.Application.IsDebugAllowed)
        {
            debugResetHeldSeconds = HoldElapsedNone;
            return;
        }

        if (!SEED.Input.GetKey(debugResetKey))
        {
            debugResetHeldSeconds = HoldElapsedNone;
            return;
        }

        // 必要秒数に達した瞬間だけ実行したいので、加算の前後で境界をまたいだかを見る
        float required = SEED.Mathf.Max(debugResetHoldSeconds, HoldElapsedNone);
        float before = debugResetHeldSeconds;
        debugResetHeldSeconds += SEED.Time.UnscaledDeltaTime;

        if (before >= required || debugResetHeldSeconds < required) { return; }

        ExecuteRecordsReset();
    }

    /// <summary>
    /// <see cref="DebugCommandRecordsReset"/> のハンドラ。
    /// </summary>
    /// <param name="arg">引数（使わない）。</param>
    private void HandleRecordsResetCommand(string arg) => ExecuteRecordsReset();

    /// <summary>
    /// 釣果記録を消してページを組み直す【リセット実行の唯一の実装】。
    /// キー長押しとデバッグコマンドの両方がここへ集まる。
    /// </summary>
    private void ExecuteRecordsReset()
    {
        int deleted = FishRecords.ResetAll();
        SEED.Debug.Log($"[Zukan] 釣果記録を消去した（削除キー {deleted} 本）");

        // 消したら見えている内容も即座に未捕獲へ戻す（次のページ送りを待たせない）
        BuildPage();

        // 実行できたことを画面でも分かるようにする（新しいアクタは足さず操作案内を流用する）
        if (!string.IsNullOrEmpty(debugResetDoneLabel))
        {
            SetContent(hintText, string.Format(debugResetDoneLabel, deleted));
        }
    }

    // ─── ページ操作 ──────────────────────────────────────────

    /// <summary>
    /// ページを送る【ページ変更の唯一の入口】（矢印スプライトのクリックからも呼ばれる）。
    /// 端は反対側へ巻き戻る。
    /// </summary>
    /// <param name="step">送る向き（<see cref="PageStepPrev"/> / <see cref="PageStepNext"/>）。</param>
    public void ChangePage(int step)
    {
        int max = SafeMaxLevel();
        if (max < FirstLevel) { return; }

        // レベルは 1 始まりなので、0 始まりへ直してから剰余で巻き戻す
        int zeroBased = currentLevel - FirstLevel + step;
        int wrapped = ((zeroBased % max) + max) % max;
        currentLevel = wrapped + FirstLevel;
        BuildPage();
    }

    /// <summary>
    /// 図鑑を閉じて、開く前のシーンへ戻る【退出の唯一の出口】。
    ///
    /// <see cref="sceneFlow"/> が結線されていればフェードアウトしてから切り替える。
    /// 無ければ従来どおり即座に切り替える（演出だけが落ちる）。
    /// </summary>
    private void LeaveZukan()
    {
        string scene = SEED.SaveData.GetString(returnSceneKey, defaultReturnScene);
        if (string.IsNullOrWhiteSpace(scene)) { scene = defaultReturnScene; }

        // 図鑑の BGM は図鑑の持ち物なので、出ていく前に止める
        // （戻り先のシーンは自分の BGM を自分で流す前提）。
        SEED.Audio.StopBgm();

        // フェードつき退出
        if (sceneFlow is { } flow)
        {
            if (flow.IsTransitioning) { return; }   // 既にフェード中（二重遷移の防止）
            if (flow.GoTo(scene)) { return; }
            // 受け付けられなかった場合だけ即時遷移へ落ちる
        }

        SEED.Scene.Transition(scene);
    }

    // ─── ページの組み立て ────────────────────────────────────

    /// <summary>
    /// 現在のページ（レベル）ぶんのカードを配り直す
    /// 【どのカードに何を載せるかを決める唯一の場所】。
    ///
    /// カード枠は使い回し（生成も破棄もしない）。今回のページで使わない枠は
    /// <see cref="ZukanCard.Hide"/> で隠す。
    /// </summary>
    private void BuildPage()
    {
        SetContent(headerText, string.Format(headerFormat, currentLevel));
        SetContent(pageText, string.Format(pageFormat, currentLevel, SafeMaxLevel()));

        // 今のページに載る魚を、枠の数を上限に集める
        int slotCount = cards.Count;
        var entries = new FishCatalogEntry[slotCount];
        int used = 0;
        foreach (FishCatalogEntry entry in FishCatalog.ForLevel(currentLevel))
        {
            if (used >= slotCount)
            {
                SEED.Debug.LogWarning(
                    $"[Zukan] Lv{currentLevel} の魚がカード枠({slotCount})を超えたため一部を表示できない");
                break;
            }
            entries[used] = entry;
            used++;
        }

        // 使う枠だけを中央揃えで並べ、余った枠は隠す
        for (int i = 0; i < slotCount; i++)
        {
            if (cards[i] is not { } card) { continue; }

            if (i >= used) { card.Hide(); continue; }

            PlaceCard(card, i, used);
            FillCard(card, entries[i]);
        }
    }

    /// <summary>カード 1 枚を、使用枚数に対して中央揃えになる位置へ置く。</summary>
    /// <param name="card">対象のカード。</param>
    /// <param name="slot">カード枠の添字。</param>
    /// <param name="usedCount">今のページで使う枚数。</param>
    private void PlaceCard(ZukanCard card, int slot, int usedCount)
    {
        float centerOffset = (usedCount - 1) / CenteringDivisor;
        card.PlaceAt(new SEED.Vector2((slot - centerOffset) * cardSpacing, cardRowY));
    }

    /// <summary>カード 1 枚へ、1 種ぶんのカタログと釣果を渡す。</summary>
    /// <param name="card">対象のカード。</param>
    /// <param name="entry">その枠に載せる魚。</param>
    private static void FillCard(ZukanCard card, FishCatalogEntry entry)
    {
        bool caught = FishRecords.IsCaught(entry.displayName);
        card.Show(
            entry,
            caught,
            caught ? FishRecords.BestSize(entry.displayName) : 0f,
            caught ? FishRecords.BestRank(entry.displayName) : string.Empty,
            caught ? FishRecords.CatchCount(entry.displayName) : 0);
    }

    // ─── 小さなヘルパー ──────────────────────────────────────

    /// <summary>テキストへ文字列を設定する（未設定・破棄済みなら何もしない）。</summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="content">表示する文字列。空文字なら消える。</param>
    private static void SetContent(SEED.Text? text, string content)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Content = content;
        t.Color = t.Color.WithAlpha(string.IsNullOrEmpty(content) ? AlphaHidden : AlphaOpaque);
    }

    /// <summary>収録されている最大レベル（0 以下なら図鑑が空＝ページ送り不能）。</summary>
    private static int SafeMaxLevel() => FishCatalog.MaxLevel;
}
