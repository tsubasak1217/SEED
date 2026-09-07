// ============================================================================
//  PauseMenu.cs
//  ゲーム中のポーズメニュー（ゲームにもどる / 図鑑 / タイトルへ）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// ポーズメニュー本体【ポーズ状態の唯一の持ち主】。
///
/// 【責務】
/// 「いまポーズ中か」を静的に答え、メニューの表示・選択・決定を行う。
/// 誰がポーズを要求したかは知らない（<c>FishingController</c> が Esc を拾って
/// <see cref="Toggle"/> を呼ぶだけ）。
///
/// 【シーンを編集せずに差し込む方式】
/// このスクリプトはプレハブ <c>assets://mainGame/actors/UI/PauseMenu.actor</c> の
/// ルート（Canvas を持つ Actor2D）に付いている。<see cref="Open"/> が
/// 初回だけそのプレハブをシーンのルートへ <c>Instantiate</c> するので、
/// MainGame.scene 側に何も置かなくてもポーズメニューが使える。
/// 2 回目以降は生成し直さず <c>Visible</c> の切り替えで出し入れする
/// （Instantiate / Destroy の繰り返しはアクタ構築コストがそのまま積み上がるため）。
///
/// 【構成（プレハブ側）】
///  PauseMenu              … このスクリプト（Canvas 1920x1080 / auto_scale）
///   PauseOverlay          … Sprite（画面全体を覆う暗幕）
///   PauseTitle            … Text（見出し）
///   PauseHighlight        … Sprite（選択行の下敷き。行の位置へ移動する）
///   PauseItem0/1/2        … Sprite（クリック判定用の透明な行。PauseMenuItem が付く）
///    PauseItem0Label/…    … Text（行のラベル）
///   PauseHint             … Text（操作案内）
///
/// 【時間軸】
/// ポーズ中はゲーム時間が止まる（<c>Time.Scale = 0</c>）ため、
/// メニュー自身の更新はすべて実時間（<c>Time.UnscaledElapsedTime</c> 系）で行う。
///
/// 【シーン遷移との関係】
/// 静的フィールドはシーン遷移では作り直されない。遷移する直前と
/// <see cref="OnDestroy"/> で必ず <see cref="ResetStaticState"/> を呼び、
/// 「ポーズ中のまま次のシーンへ持ち越す」事故を防ぐ。
/// </summary>
public class PauseMenu : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>メニュー行の数（＝<see cref="MenuAction"/> の種類数）。</summary>
    private const int ItemCount = 3;

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

    /// <summary>生成済みのメニュー本体（未生成なら <c>IsValid == false</c>）。</summary>
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

    // ─── インスペクタ設定（文言・配色はすべてここ）─────────────

    /// <summary>見出しの文言。</summary>
    [Header("文言"), SerializeField(Label = "見出し")]
    private string titleLabel = "ポーズ";

    /// <summary>「ゲームにもどる」行の文言。</summary>
    [SerializeField(Label = "行1: もどる")]
    private string resumeLabel = "ゲームにもどる";

    /// <summary>「図鑑」行の文言。</summary>
    [SerializeField(Label = "行2: 図鑑")]
    private string zukanLabel = "図鑑";

    /// <summary>「タイトルへ」行の文言。</summary>
    [SerializeField(Label = "行3: タイトル")]
    private string titleItemLabel = "タイトルへ";

    /// <summary>操作案内の文言。</summary>
    [SerializeField(Label = "操作案内")]
    private string hintLabel = "W / S で選択    Enter・左クリックで決定    Esc でもどる";

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

    /// <summary>選択されていない行のラベル色（RGB）。</summary>
    [Header("配色"), SerializeField(Label = "通常色(RGB)")]
    private SEED.Vector3 normalColor = new(0.86f, 0.92f, 1.0f);

    /// <summary>選択中の行のラベル色（RGB）。</summary>
    [SerializeField(Label = "選択色(RGB)")]
    private SEED.Vector3 selectedColor = new(1.0f, 0.92f, 0.45f);

    /// <summary>選択中の行の下敷きの色（RGB）。</summary>
    [SerializeField(Label = "下敷き色(RGB)")]
    private SEED.Vector3 highlightColor = new(0.16f, 0.36f, 0.62f);

    /// <summary>選択中の行の下敷きの不透明度。</summary>
    [SerializeField(Label = "下敷きの不透明度")]
    private float highlightAlpha = 0.85f;

    // ─── 参照（プレハブ内の子アクタ。インスペクタで結線）─────────

    /// <summary>見出しのテキスト。</summary>
    [Header("参照"), SerializeField(Label = "見出しText")]
    private SEED.Text? titleText;

    /// <summary>「ゲームにもどる」行のラベル。</summary>
    [SerializeField(Label = "行1のText")]
    private SEED.Text? item0Text;

    /// <summary>「図鑑」行のラベル。</summary>
    [SerializeField(Label = "行2のText")]
    private SEED.Text? item1Text;

    /// <summary>「タイトルへ」行のラベル。</summary>
    [SerializeField(Label = "行3のText")]
    private SEED.Text? item2Text;

    /// <summary>操作案内のテキスト。</summary>
    [SerializeField(Label = "操作案内のText")]
    private SEED.Text? hintText;

    /// <summary>選択中の行を示す下敷きのスプライト（色を塗る先）。</summary>
    [SerializeField(Label = "下敷きSprite")]
    private SEED.Sprite? highlightSprite;

    /// <summary>下敷きのトランスフォーム（選択行の座標へ動かす先）。</summary>
    [SerializeField(Label = "下敷きのTransform")]
    private SEED.CanvasTransform? highlightTransform;

    /// <summary>行 0（ゲームにもどる）のトランスフォーム。</summary>
    [SerializeField(Label = "行1のTransform")]
    private SEED.CanvasTransform? item0Transform;

    /// <summary>行 1（図鑑）のトランスフォーム。</summary>
    [SerializeField(Label = "行2のTransform")]
    private SEED.CanvasTransform? item1Transform;

    /// <summary>行 2（タイトルへ）のトランスフォーム。</summary>
    [SerializeField(Label = "行3のTransform")]
    private SEED.CanvasTransform? item2Transform;

    // ─── 実行時の状態 ────────────────────────────────────────

    /// <summary>いま選択している行の添字（0〜<see cref="ItemCount"/>-1）。</summary>
    private int selectedIndex = IndexResume;

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
            if (string.IsNullOrWhiteSpace(actorPath))
            {
                SEED.Debug.LogWarning("[PauseMenu] プレハブのパスが未設定のため開けない");
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
        Current?.RefreshVisual();
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

    /// <summary>生成直後の初期化。文言を流し込み、選択を先頭へ戻す。</summary>
    public override void OnStart()
    {
        Current = this;
        selectedIndex = IndexResume;

        SetContent(titleText, titleLabel);
        SetContent(item0Text, resumeLabel);
        SetContent(item1Text, zukanLabel);
        SetContent(item2Text, titleItemLabel);
        SetContent(hintText, hintLabel);

        RefreshVisual();
    }

    /// <summary>破棄時の後始末。ポーズを持ち越さないよう静的状態を戻す。</summary>
    public override void OnDestroy()
    {
        // 自分が現役のときだけ静的状態を戻す（別インスタンスの登録は壊さない）
        if (ReferenceEquals(Current, this)) { ResetStaticState(); }
    }

    /// <summary>
    /// 毎フレームの更新。ポーズ中だけキーボード・マウス操作を受け付ける。
    /// すべて実時間で判断する（ゲーム時間は止まっているため）。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (!IsOpen) { return; }
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

    // ─── 表示 ────────────────────────────────────────────────

    /// <summary>選択状態を見た目へ反映する（ラベルの色と下敷きの位置）。</summary>
    private void RefreshVisual()
    {
        SetTextColor(item0Text, selectedIndex == IndexResume);
        SetTextColor(item1Text, selectedIndex == IndexZukan);
        SetTextColor(item2Text, selectedIndex == IndexTitle);

        if (highlightSprite is { } bar && bar.IsValid)
        {
            bar.Color = new SEED.Color(highlightColor.x, highlightColor.y, highlightColor.z, highlightAlpha);
        }

        // 下敷きは選択行と同じ座標へ置く（行の位置はプレハブ側の値が正典）
        if (highlightTransform is { } barTransform && barTransform.IsValid
         && TransformOf(selectedIndex) is { } row && row.IsValid)
        {
            barTransform.Position = row.Position;
        }
    }

    /// <summary>行の添字に対応するトランスフォーム（未設定なら null）。</summary>
    /// <param name="index">行の添字。</param>
    private SEED.CanvasTransform? TransformOf(int index) => index switch
    {
        IndexZukan => item1Transform,
        IndexTitle => item2Transform,
        _ => item0Transform,
    };

    /// <summary>選択を上下へ動かす（端は巻き戻る）。</summary>
    /// <param name="step">移動量（-1 = 上 / +1 = 下）。</param>
    private void MoveSelection(int step)
    {
        int next = (selectedIndex + step + ItemCount) % ItemCount;
        Select(next);
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
