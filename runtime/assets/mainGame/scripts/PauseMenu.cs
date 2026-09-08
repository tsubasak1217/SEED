// ============================================================================
//  PauseMenu.cs
//  ゲーム中のポーズメニュー（ゲームにもどる / 図鑑 / タイトルへ）。
// ============================================================================

using System.Collections.Generic;
using SEEDEditor.Scripting;

/// <summary>
/// ポーズメニュー本体【ポーズ状態の唯一の持ち主】。
///
/// 【責務】
/// 「いまポーズ中か」を静的に答え、メニューの表示・選択・決定を行う。
/// 誰がポーズを要求したかは知らない（<c>FishingController</c> が Esc を拾って
/// <see cref="Toggle"/> を呼ぶだけ）。
///
/// 【配置方式】
/// このスクリプトはプレハブ <c>assets://mainGame/actors/UI/PauseMenu.actor</c> の
/// ルート（Canvas を持つ Actor2D）に付いている。推奨は<b>プレハブのインスタンスを
/// あらかじめシーン（MainGame.scene）のルートへ置いておく</b>こと。<see cref="OnStart"/> が
/// 自分を本体として登録し、開くまで非表示にする。出し入れは <c>Visible</c> の切り替えだけで、
/// 生成・破棄は行わない（ルートを非表示にすると子孫もまとめて描画・ピック対象外になる。
/// ランタイム側 <c>canvas_collect.rs</c> の「実効表示」規則）。
/// シーンに置かれていない場合に限り、<see cref="Open"/> がフォールバックとして
/// プレハブを <c>Instantiate</c> する。
///
/// 【構成（シーン／プレハブ側）】
///  PauseMenu                        … このスクリプト（Canvas / auto_scale）
///   PauseOverlay                    … Sprite（画面全体を覆う暗幕）
///   PauseTitle                      … Text（見出し）
///   PauseItem0/1/2                  … Sprite（行の下敷き兼クリック判定。PauseMenuItem が付く）
///    PauseItem0Label/1Label/2Label  … Text（行のラベル）
///
/// 【子アクタの参照の持ち方（重要）】
/// 参照フィールド（<c>SEED.Text</c> 等のハンドル型）は<b>C# の初期化子で既定値を持てない</b>。
/// エディタ側は参照フィールドの既定値を常に「未設定」として扱うため
/// （<c>ScriptBridge.DefaultValueString</c>／<c>[ResetButton]</c> の仕様）、
/// 「シーンで結線しなくても既定で子アクタへ届く」を実現するには
/// <b>参照文字列（相対パス）を string フィールドで持ち、<see cref="OnStart"/> で
/// <c>gameObject.FindChild</c> により解決する</b>しかない。本スクリプトはその方式を採る。
/// パス書式は docs/scripting_api.md 第 7 節「参照文字列のパス指定」と同じで、
/// 末尾に <c>|スロット名</c> を付けるとそのスロットのコンポーネントを取る。
///
/// この方式には副次的な利点がある。シーンに残っている<b>古いフィールド値
/// （highlightSprite など）は、対応するフィールドが無くなった時点で読み捨てられる</b>
/// （ランタイムは保存済みマップのうち現在のフィールド定義に一致するものだけを流し込む）。
/// つまりシーンを編集し直さなくても、この差し替えは安全に効く。
///
/// 【時間軸】
/// ポーズ中はゲーム時間が止まる（<c>Time.Scale = 0</c>）ため、
/// メニュー自身の更新はすべて実時間（<c>Time.Unscaled*</c> 系）で行う。
///
/// 【シーン遷移との関係】
/// 静的フィールドはシーン遷移では作り直されない。遷移する直前と
/// <see cref="OnDestroy"/> で必ず <see cref="ResetStaticState"/> を呼び、
/// 「ポーズ中のまま次のシーンへ持ち越す」事故を防ぐ。
/// </summary>
public class PauseMenu : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>「ゲームにもどる」行の添字。</summary>
    private const int IndexResume = 0;

    /// <summary>「図鑑」行の添字。</summary>
    private const int IndexZukan = 1;

    /// <summary>「タイトルへ」行の添字。</summary>
    private const int IndexTitle = 2;

    /// <summary>ポーズ中のゲーム時間の速さ（＝停止）。</summary>
    private const float TimeScalePaused = 0f;

    /// <summary>通常時のゲーム時間の速さ。</summary>
    private const float TimeScaleRunning = 1f;

    /// <summary>不透明を表すアルファ。</summary>
    private const float AlphaOpaque = 1f;

    /// <summary>選択を 1 つ上へ動かす量。</summary>
    private const int SelectionStepUp = -1;

    /// <summary>選択を 1 つ下へ動かす量。</summary>
    private const int SelectionStepDown = 1;

    /// <summary>入力無視の刻の初期値（どのフレームとも一致しない値）。</summary>
    private const float NoInputIgnoreStamp = -1f;

    /// <summary>参照文字列の「アクタパス」と「スロット名」の区切り文字（例 <c>./PauseTitle|Text</c>）。</summary>
    private const char ReferenceSlotSeparator = '|';

    /// <summary>
    /// 拡大アニメーションに使う秒数の下限。
    /// 0 秒を指定されても 0 除算にならないよう、この値でクランプする（＝ほぼ即時）。
    /// </summary>
    private const float MinScaleLerpSeconds = 0.0001f;

    // ─── メニューの動作 ──────────────────────────────────────

    /// <summary>行を決定したときの動作。</summary>
    private enum MenuAction
    {
        /// <summary>メニューを閉じてゲームへ戻る。</summary>
        Resume,

        /// <summary>図鑑シーンへ遷移する。</summary>
        OpenZukan,

        /// <summary>タイトルシーンへ遷移する。</summary>
        BackToTitle,
    }

    // ─── 静的状態（ポーズの単一の真実）───────────────────────

    /// <summary>
    /// メニュー本体（シーン配置済みなら <see cref="OnStart"/> で登録、無ければ
    /// <see cref="Open"/> が生成して登録。未登録なら <c>IsValid == false</c>）。
    /// </summary>
    private static SEED.GameObject menuRoot;

    /// <summary>いまポーズ中か。ゲーム側はこれを見て入力を止める。</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>実行中のインスタンス（行のクリック用スクリプトから選択を伝えるのに使う）。</summary>
    public static PauseMenu? Current { get; private set; }

    /// <summary>
    /// 入力を無視する実時間の刻（<c>Time.UnscaledElapsedTime</c>）。
    ///
    /// 開いた／閉じたそのフレームに、呼び出し元が拾ったのと同じ Esc・クリックを
    /// メニュー側でもう一度処理してしまう（＝開いた瞬間に閉じる）のを防ぐ。
    /// スクリプトの実行順に依存しないよう「同じ刻の入力は無視」という形にしてある。
    /// </summary>
    private static float inputIgnoreStamp = NoInputIgnoreStamp;

    // ─── インスペクタ設定（文言）──────────────────────────────

    /// <summary>見出しの文言。</summary>
    [Header("文言"), SerializeField(Label = "見出し")]
    private string titleLabel = "ポーズ";

    /// <summary>
    /// 各行の文言（<see cref="itemActorPaths"/> と同じ並び・同じ件数）。
    /// 件数が足りない行は、シーン側の Text をそのまま残す。
    /// </summary>
    [SerializeField(Label = "行の文言")]
    private List<string> itemLabels = new() { "ゲームにもどる", "図鑑", "タイトルへ" };

    /// <summary>
    /// 操作案内の文言。<see cref="hintTextPath"/> が空（＝案内テキストを置いていない）なら使われない。
    /// </summary>
    [SerializeField(Label = "操作案内")]
    private string hintLabel = "W / S で選択    Enter・左クリックで決定    Esc でもどる";

    // ─── インスペクタ設定（子アクタへの参照文字列）─────────────

    /// <summary>
    /// 行アクタ（＝下敷きスプライトを持つアクタ）への相対パス。<b>行数はこのリストの件数で決まる</b>。
    /// 行を増やすときはここへパスを足すだけでよい（<see cref="itemLabelPaths"/> も同じ並びで足す）。
    /// </summary>
    [Header("参照（自分からの相対パス）"), SerializeField(Label = "行アクタのパス")]
    private List<string> itemActorPaths = new() { "./PauseItem0", "./PauseItem1", "./PauseItem2" };

    /// <summary>
    /// 行ラベル（Text）への相対パス。末尾の <c>|Text</c> はコンポーネントのスロット名。
    /// <see cref="itemActorPaths"/> と同じ並び。
    /// </summary>
    [SerializeField(Label = "行ラベルのパス")]
    private List<string> itemLabelPaths = new()
    {
        "./PauseItem0/PauseItem0Label|Text",
        "./PauseItem1/PauseItem1Label|Text",
        "./PauseItem2/PauseItem2Label|Text",
    };

    /// <summary>見出し（Text）への相対パス。空なら見出しを触らない。</summary>
    [SerializeField(Label = "見出しのパス")]
    private string titleTextPath = "./PauseTitle|Text";

    /// <summary>
    /// 操作案内（Text）への相対パス。<b>既定は空（案内テキストを置いていないレイアウト）</b>。
    /// 案内を出したくなったら子アクタを足してここへパスを入れる。
    /// </summary>
    [SerializeField(Label = "操作案内のパス")]
    private string hintTextPath = "";

    // ─── インスペクタ設定（選択中の見た目）────────────────────

    /// <summary>非選択の行のスケール（シーンに保存されたスケールではなくこの値を正典とする）。</summary>
    [Header("選択の見た目"), SerializeField(Label = "通常スケール")]
    private float normalScale = 1.4f;

    /// <summary>選択中（キーボード選択 or マウスホバー）の行のスケール。</summary>
    [SerializeField(Label = "選択スケール")]
    private float selectedScale = 1.5f;

    /// <summary>
    /// 通常スケール↔選択スケールを行き来するのにかける秒数（実時間）。
    /// ポーズ中はゲーム時間が止まっているため、必ず <c>Time.UnscaledDeltaTime</c> で進める。
    /// </summary>
    [SerializeField(Label = "拡大にかける秒数")]
    private float scaleLerpSeconds = 0.12f;

    /// <summary>選択されていない行のラベル色（RGB）。</summary>
    [SerializeField(Label = "通常色(RGB)")]
    private SEED.Vector3 normalColor = new(0.86f, 0.92f, 1.0f);

    /// <summary>選択中の行のラベル色（RGB）。</summary>
    [SerializeField(Label = "選択色(RGB)")]
    private SEED.Vector3 selectedColor = new(1.0f, 0.92f, 0.45f);

    // ─── インスペクタ設定（遷移先）────────────────────────────

    /// <summary>遷移先の図鑑シーン名（プロジェクト設定のシーンマネージャ登録名）。</summary>
    [Header("遷移先"), SerializeField(Label = "図鑑シーン名")]
    private string zukanSceneName = "zukan";

    /// <summary>遷移先のタイトルシーン名。</summary>
    [SerializeField(Label = "タイトルシーン名")]
    private string titleSceneName = "title";

    /// <summary>図鑑から戻る先のシーン名を保存するセーブキー。</summary>
    [SerializeField(Label = "図鑑の戻り先キー")]
    private string zukanReturnKey = "zukan_return";

    /// <summary>図鑑から戻る先のシーン名（このメニューを開いたシーン）。</summary>
    [SerializeField(Label = "図鑑の戻り先シーン名")]
    private string zukanReturnScene = "mainGame";

    // ─── 実行時の状態（解決済みの参照とアニメーションの現在値）─────

    /// <summary>行アクタの CanvasTransform（<see cref="itemActorPaths"/> と同じ並び・同じ件数）。</summary>
    private readonly List<SEED.CanvasTransform> itemTransforms = new();

    /// <summary>行ラベルの Text（<see cref="itemActorPaths"/> と同じ並び・同じ件数。解決失敗は null）。</summary>
    private readonly List<SEED.Text?> itemTexts = new();

    /// <summary>行スケールの現在値（<see cref="itemActorPaths"/> と同じ並び・同じ件数）。</summary>
    private readonly List<float> itemScales = new();

    /// <summary>見出しの Text（解決失敗・未設定なら null）。</summary>
    private SEED.Text? resolvedTitleText;

    /// <summary>操作案内の Text（解決失敗・未設定なら null）。</summary>
    private SEED.Text? resolvedHintText;

    /// <summary>いま選択している行の添字（0〜行数-1）。</summary>
    private int selectedIndex = IndexResume;

    /// <summary>行数（＝解決した行の件数）。行が 1 つも無ければ 0。</summary>
    private int ItemCount => itemTransforms.Count;

    // ─── 静的 API（ゲーム側から呼ぶ入口）───────────────────────

    /// <summary>
    /// ポーズメニューを開閉する【ポーズ切り替えの唯一の入口】。
    /// </summary>
    /// <param name="actorPath">ポーズメニューのプレハブ（assets:// パス）。</param>
    public static void Toggle(string actorPath)
    {
        if (IsOpen) { Close(); return; }
        Open(actorPath);
    }

    /// <summary>
    /// ポーズメニューを開く（すでに開いていれば何もしない）。
    ///
    /// 初回はプレハブをシーンのルートへ生成する（生成はフレーム末尾に反映されるので、
    /// メニュー自身の <c>OnStart</c> は次フレームから走る）。
    /// <see cref="IsOpen"/> はこの呼び出しの中で即座に切り替わるので、
    /// 呼び出し元は同じフレームからゲーム入力を止められる。
    /// </summary>
    /// <param name="actorPath">ポーズメニューのプレハブ（assets:// パス）。</param>
    public static void Open(string actorPath)
    {
        if (IsOpen) { return; }

        if (!menuRoot.IsValid)
        {
            // シーンに配置されたインスタンスが無い場合だけプレハブを生成する（フォールバック）。
            // 推奨はシーンへあらかじめ置いて非表示にしておくこと（OnStart が登録する）。
            if (string.IsNullOrWhiteSpace(actorPath))
            {
                SEED.Debug.LogWarning("[PauseMenu] シーンに PauseMenu が無く、プレハブのパスも未設定のため開けない");
                return;
            }
            menuRoot = SEED.GameObject.Instantiate(actorPath);
            if (!menuRoot.IsValid)
            {
                SEED.Debug.LogWarning($"[PauseMenu] プレハブを生成できない: {actorPath}");
                return;
            }
        }
        else
        {
            menuRoot.Visible = true;
        }

        IsOpen = true;
        inputIgnoreStamp = SEED.Time.UnscaledElapsedTime;
        SEED.Time.Scale = TimeScalePaused;
        SEED.Input.CursorLocked = false;   // メニュー操作にカーソルが要る
        Current?.OnMenuOpened();
    }

    /// <summary>ポーズメニューを閉じてゲームへ戻す【ポーズ解除の唯一の出口】。</summary>
    public static void Close()
    {
        if (!IsOpen) { return; }
        IsOpen = false;
        inputIgnoreStamp = SEED.Time.UnscaledElapsedTime;
        if (menuRoot.IsValid) { menuRoot.Visible = false; }
        SEED.Time.Scale = TimeScaleRunning;
        // カーソルロックはゲーム側（FishingController.UpdateCursorLock）が
        // 次のフレームで状態に合わせて引き直すので、ここでは触らない。
    }

    /// <summary>
    /// 静的状態をシーン開始時の値へ戻す。
    ///
    /// 静的フィールドはシーン遷移では作り直されないため、
    /// 遷移の直前と <see cref="OnDestroy"/>、そしてゲーム側の <c>OnStart</c> から呼んで、
    /// 「破棄済みのメニューを掴んだままポーズ中扱い」になるのを防ぐ。
    /// </summary>
    public static void ResetStaticState()
    {
        IsOpen = false;
        menuRoot = default;
        Current = null;
        inputIgnoreStamp = NoInputIgnoreStamp;
        SEED.Time.Scale = TimeScaleRunning;
    }

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 初期化。子アクタの参照を相対パスから解決し、文言を流し込み、選択を先頭へ戻す。
    ///
    /// <b>あらかじめシーンに置かれたインスタンス</b>（推奨）は、ここで自分を
    /// <see cref="menuRoot"/> として登録し、開くまで非表示にする。
    /// 実行時生成（<see cref="Open"/> のフォールバック）でも同じ経路を通る。
    /// </summary>
    public override void OnStart()
    {
        Current = this;
        selectedIndex = IndexResume;

        // シーン配置済みの本体を採用する。まだ開いていなければ隠しておく
        // （シーン上で visible=true のまま保存されていても Play 開始時に必ず隠れる）。
        menuRoot = gameObject;
        if (!IsOpen) { menuRoot.Visible = false; }

        ResolveReferences();
        ApplyLabels();
        SnapItemScales();
        RefreshVisual();
    }

    /// <summary>破棄時の後始末。ポーズを持ち越さないよう静的状態を戻す。</summary>
    public override void OnDestroy()
    {
        // 自分が現役のときだけ静的状態を戻す（別インスタンスの登録は壊さない）
        if (ReferenceEquals(Current, this)) { ResetStaticState(); }
    }

    /// <summary>
    /// 毎フレームの更新。ポーズ中だけ見た目の補間とキーボード・マウス操作を受け付ける。
    /// すべて実時間で判断する（ゲーム時間は止まっているため）。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (!IsOpen) { return; }

        // 見た目の補間は「開閉と同じ刻の入力を無視する」フレームでも進める
        // （開いた最初の 1 フレームだけアニメが止まるのを避ける）。
        UpdateItemScales();

        if (IsInputIgnoredThisFrame()) { return; }

        // 上下移動（W/S と矢印。押した瞬間のみ）
        if (SEED.Input.GetKeyDown(SEED.KeyCode.W) || SEED.Input.GetKeyDown(SEED.KeyCode.UpArrow))
        {
            MoveSelection(SelectionStepUp);
        }
        if (SEED.Input.GetKeyDown(SEED.KeyCode.S) || SEED.Input.GetKeyDown(SEED.KeyCode.DownArrow))
        {
            MoveSelection(SelectionStepDown);
        }

        // 決定（Enter / 左クリック）。行の上でのクリックは PauseMenuItem が
        // ホバー時点で選択を移してくれるので、どちらの経路でも同じ結果になる。
        if (SEED.Input.GetKeyDown(SEED.KeyCode.Enter)
         || SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left))
        {
            Confirm();
            return;
        }

        // Esc で閉じる（＝「ゲームにもどる」と同じ）
        if (SEED.Input.GetKeyDown(SEED.KeyCode.Escape)) { Close(); }
    }

    // ─── 選択・決定 ──────────────────────────────────────────

    /// <summary>
    /// 行を選択する（マウスホバー・行クリックからも呼ばれる）。
    /// </summary>
    /// <param name="index">選択する行の添字。範囲外は無視する。</param>
    public void Select(int index)
    {
        if (index < 0 || index >= ItemCount) { return; }
        if (selectedIndex == index) { return; }
        selectedIndex = index;
        RefreshVisual();
    }

    /// <summary>いま選択している行を決定する【決定処理の唯一の集約点】。</summary>
    public void Confirm()
    {
        switch (ActionOf(selectedIndex))
        {
            case MenuAction.Resume:
                Close();
                break;

            case MenuAction.OpenZukan:
                // 図鑑から「どこへ戻るか」を渡してから遷移する
                SEED.SaveData.SetString(zukanReturnKey, zukanReturnScene);
                SEED.SaveData.Save();
                TransitionTo(zukanSceneName);
                break;

            case MenuAction.BackToTitle:
                TransitionTo(titleSceneName);
                break;
        }
    }

    /// <summary>行の添字に対応する動作。</summary>
    /// <param name="index">行の添字。</param>
    private static MenuAction ActionOf(int index) => index switch
    {
        IndexZukan => MenuAction.OpenZukan,
        IndexTitle => MenuAction.BackToTitle,
        _ => MenuAction.Resume,
    };

    /// <summary>
    /// ポーズ状態を畳んでからシーンを切り替える。
    /// ゲーム時間・カーソル・静的状態を戻してから遷移するので、
    /// 遷移先が「止まったまま・カーソルが無いまま」になることはない。
    /// </summary>
    /// <param name="sceneName">遷移先のシーン名（シーンマネージャ登録名）。</param>
    private void TransitionTo(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            SEED.Debug.LogWarning("[PauseMenu] 遷移先のシーン名が未設定");
            return;
        }
        ResetStaticState();
        SEED.Input.CursorLocked = false;
        SEED.Scene.Transition(sceneName);
    }

    /// <summary>選択を上下へ動かす（端は巻き戻る）。</summary>
    /// <param name="step">移動量（-1 = 上 / +1 = 下）。</param>
    private void MoveSelection(int step)
    {
        int count = ItemCount;
        if (count <= 0) { return; }   // 行が 1 つも解決できていない（剰余の 0 除算を防ぐ）
        Select((selectedIndex + step + count) % count);
    }

    // ─── 参照の解決（相対パス → ハンドル）──────────────────────

    /// <summary>
    /// インスペクタの相対パス文字列から、子アクタのコンポーネントを解決する
    /// 【子参照を作る唯一の場所】。
    ///
    /// ハンドル型の参照フィールドは C# の初期化子で既定値を持てないため、
    /// 「シーンで結線しなくても既定で届く」をパス文字列で実現している（クラスコメント参照）。
    /// </summary>
    private void ResolveReferences()
    {
        itemTransforms.Clear();
        itemTexts.Clear();
        itemScales.Clear();

        for (int i = 0; i < itemActorPaths.Count; i++)
        {
            // 行アクタ本体（CanvasTransform はアクタのルートに直付けなのでスロット名は使わない）
            SEED.GameObject row = ResolveActor(itemActorPaths[i]);
            SEED.CanvasTransform rowTransform =
                row.IsValid ? row.GetComponent<SEED.CanvasTransform>() ?? default : default;
            if (!rowTransform.IsValid)
            {
                SEED.Debug.LogWarning($"[PauseMenu] 行アクタを解決できない: {itemActorPaths[i]}");
            }

            itemTransforms.Add(rowTransform);
            itemTexts.Add(i < itemLabelPaths.Count ? ResolveText(itemLabelPaths[i]) : null);
            itemScales.Add(normalScale);
        }

        resolvedTitleText = ResolveText(titleTextPath);
        resolvedHintText  = ResolveText(hintTextPath);
    }

    /// <summary>
    /// 参照文字列（<c>./Child/Grand</c> 形式。<c>|スロット名</c> は無視）からアクタを解決する。
    /// </summary>
    /// <param name="reference">参照文字列。空なら無効なハンドルを返す。</param>
    private SEED.GameObject ResolveActor(string reference)
    {
        string actorPath = SplitActorPath(reference);
        if (string.IsNullOrWhiteSpace(actorPath)) { return default; }
        return gameObject.FindChild(actorPath);
    }

    /// <summary>
    /// 参照文字列から Text コンポーネントを解決する（見つからなければ null）。
    /// </summary>
    /// <param name="reference">
    /// <c>./PauseTitle|Text</c> のような参照文字列。
    /// スロット名を省くと 0 番目のスロットを取る。空文字なら「未設定」として null を返す。
    /// </param>
    private SEED.Text? ResolveText(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) { return null; }

        SEED.GameObject owner = ResolveActor(reference);
        if (!owner.IsValid)
        {
            SEED.Debug.LogWarning($"[PauseMenu] テキストのアクタを解決できない: {reference}");
            return null;
        }

        string slot = SplitSlotName(reference);
        SEED.Text? text = string.IsNullOrEmpty(slot)
            ? owner.GetComponent<SEED.Text>()
            : owner.GetComponent<SEED.Text>(slot);
        if (text is null)
        {
            SEED.Debug.LogWarning($"[PauseMenu] Text スロットが見つからない: {reference}");
        }
        return text;
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

    // ─── 表示 ────────────────────────────────────────────────

    /// <summary>インスペクタの文言を各 Text へ流し込む（起動時 1 回）。</summary>
    private void ApplyLabels()
    {
        SetContent(resolvedTitleText, titleLabel);
        SetContent(resolvedHintText, hintLabel);

        for (int i = 0; i < itemTexts.Count && i < itemLabels.Count; i++)
        {
            SetContent(itemTexts[i], itemLabels[i]);
        }
    }

    /// <summary>
    /// メニューを開いた瞬間の見た目を作る。
    /// 全行をいったん通常スケールへ戻し、選択行だけがこのあと拡大していく（ポップ感）。
    /// </summary>
    private void OnMenuOpened()
    {
        selectedIndex = IndexResume;
        SnapItemScales();
        RefreshVisual();
    }

    /// <summary>
    /// 全行のスケールを通常スケールへ即時に合わせる。
    ///
    /// シーン上の scale（作業中の値が保存されていることがある）ではなく、
    /// インスペクタの <see cref="normalScale"/> を正典として明示的に上書きする。
    /// </summary>
    private void SnapItemScales()
    {
        for (int i = 0; i < itemScales.Count; i++)
        {
            itemScales[i] = normalScale;
            ApplyItemScale(i, normalScale);
        }
    }

    /// <summary>
    /// 行のスケールを目標値（選択行 = <see cref="selectedScale"/> / それ以外 = <see cref="normalScale"/>）へ
    /// <see cref="scaleLerpSeconds"/> 秒かけて近づける。
    ///
    /// 「通常↔選択の全行程にちょうど scaleLerpSeconds かかる」よう<b>一定速度</b>で動かす
    /// （指数補間だと到達しないため、秒数指定の意味が薄れる）。
    /// </summary>
    private void UpdateItemScales()
    {
        float span     = SEED.Mathf.Abs(selectedScale - normalScale);
        float seconds  = SEED.Mathf.Max(scaleLerpSeconds, MinScaleLerpSeconds);
        float stepThisFrame = span / seconds * SEED.Time.UnscaledDeltaTime;

        for (int i = 0; i < itemScales.Count; i++)
        {
            float target  = (i == selectedIndex) ? selectedScale : normalScale;
            float current = itemScales[i];
            if (current == target) { continue; }

            if (stepThisFrame <= 0f)
            {
                // 通常＝選択スケール、または秒数 0 相当のときは即時に合わせる
                current = target;
            }
            else if (current < target)
            {
                current = SEED.Mathf.Min(target, current + stepThisFrame);
            }
            else
            {
                current = SEED.Mathf.Max(target, current - stepThisFrame);
            }

            itemScales[i] = current;
            ApplyItemScale(i, current);
        }
    }

    /// <summary>行のスケールを実際の CanvasTransform へ書き込む（縦横同倍率）。</summary>
    /// <param name="index">行の添字。</param>
    /// <param name="scale">適用する倍率。</param>
    private void ApplyItemScale(int index, float scale)
    {
        if (index < 0 || index >= itemTransforms.Count) { return; }
        SEED.CanvasTransform t = itemTransforms[index];
        if (!t.IsValid) { return; }
        t.Scale = new SEED.Vector2(scale, scale);
    }

    /// <summary>選択状態を見た目へ反映する（ラベルの色）。スケールは毎フレームの補間が担う。</summary>
    private void RefreshVisual()
    {
        for (int i = 0; i < itemTexts.Count; i++)
        {
            SetTextColor(itemTexts[i], i == selectedIndex);
        }
    }

    /// <summary>テキストへ文言を設定する（未設定・破棄済みなら何もしない）。</summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="content">表示する文字列。</param>
    private static void SetContent(SEED.Text? text, string content)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Content = content;
    }

    /// <summary>行のラベル色を選択状態に応じて塗り分ける。</summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="selected">その行が選択されているか。</param>
    private void SetTextColor(SEED.Text? text, bool selected)
    {
        if (text is not { } t || !t.IsValid) { return; }
        SEED.Vector3 rgb = selected ? selectedColor : normalColor;
        t.Color = new SEED.Color(rgb.x, rgb.y, rgb.z, AlphaOpaque);
    }

    /// <summary>
    /// 今フレームの入力を無視すべきか（開閉と同じ刻の入力を二重処理しないための門）。
    /// </summary>
    private static bool IsInputIgnoredThisFrame()
        => SEED.Time.UnscaledElapsedTime <= inputIgnoreStamp;
}
