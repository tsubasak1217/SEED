// ============================================================================
//  MissionPanel.cs
//  常設のミッションパネル（ミッション名・目的・サブ目標・進捗の表示）。
// ============================================================================

using System.Collections.Generic;
using SEEDEditor.Scripting;

/// <summary>
/// レーダーの下に常設するミッションパネル【ミッション情報の表示の唯一の置き場】。
///
/// 【責務】
/// 渡された文字列を並べて出す<b>それだけ</b>。何が達成条件かも、次に何をするかも知らない
/// （判定は <see cref="IMission"/>、進行は <see cref="TutorialDirector"/> の責務）。
///
/// 【構成（シーン側）】
/// 半透明の地（Sprite）と 4 つの Text を子に持つ 2D アクタへ付ける。
/// 参照はすべてインスペクタで結線するので、素材を差し替えるときも
/// 子アクタの Sprite / Text を差し替えるだけでコードは触らなくてよい。
///
///  MissionPanel             … このスクリプト
///   MissionPanelBg          … Sprite（半透明の地。仮素材は white.png の着色）
///   MissionPanelTitle       … Text（ミッション名）
///   MissionPanelObjective   … Text（目的）
///   MissionPanelChecks      … Text（サブ目標のチェック。改行で複数行）
///   MissionPanelProgress    … Text（進捗）
///
/// 【表示 / 非表示の方式】
/// アクタ単位の表示切り替えではなく<b>アルファを 0 にして隠す</b>
/// （<see cref="TutorialWindow"/> と同じ流儀）。部品ごとに元色を控えてから
/// 倍率を掛けるので、シーンで設定した色味がそのまま活きる。
///
/// 【時間軸】
/// 出現・退場の演出はすべて実時間（Time.UnscaledDeltaTime）で進める。
/// 説明中はゲーム時間が止まるので、ゲーム時間で進めると演出が固まる。
/// </summary>
public class MissionPanel : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>隠しているときのアルファ倍率。</summary>
    private const float AlphaScaleHidden = 0f;

    /// <summary>表示しているときのアルファ倍率。</summary>
    private const float AlphaScaleVisible = 1f;

    /// <summary>出現演出の既定の秒数。</summary>
    private const float DefaultAppearSeconds = 0.30f;

    /// <summary>退場演出の既定の秒数。</summary>
    private const float DefaultDisappearSeconds = 0.16f;

    /// <summary>進捗が 1 のときの値（演出の完了判定に使う）。</summary>
    private const float ProgressComplete = 1f;

    /// <summary>拡大率 0（畳んだ状態）。</summary>
    private const float ScaleZero = 0f;

    /// <summary>文字列が空であることを表す値。</summary>
    private const string EmptyText = "";

    /// <summary>サブ目標を 1 つの Text へ流し込むときの行区切り。</summary>
    private const string ObjectiveSeparator = "\n";

    // ─── 表示段階 ────────────────────────────────────────────

    /// <summary>パネルの表示段階。</summary>
    private enum PanelVisualState
    {
        /// <summary>完全に隠れている（更新も止める）。</summary>
        Hidden,

        /// <summary>出現演出の最中。</summary>
        Appearing,

        /// <summary>出ている（内容だけが差し替わる）。</summary>
        Shown,

        /// <summary>退場演出の最中。</summary>
        Disappearing,
    }

    // ─── 参照（インスペクタで結線）───────────────────────────

    /// <summary>半透明の地の Sprite。</summary>
    [Header("参照"), SerializeField(Label = "地のスプライト", Tooltip = "パネルの背景（子名|Sprite）")]
    public SEED.Sprite? bgSprite;

    /// <summary>ミッション名の Text。</summary>
    [SerializeField(Label = "ミッション名", Tooltip = "見出しの Text（子名|Text）")]
    public SEED.Text? titleText;

    /// <summary>目的文の Text。</summary>
    [SerializeField(Label = "目的", Tooltip = "目的文の Text（子名|Text）")]
    public SEED.Text? objectiveText;

    /// <summary>サブ目標のチェックを並べる Text（改行で複数行）。</summary>
    [SerializeField(Label = "サブ目標", Tooltip = "サブ目標のチェックを並べる Text（子名|Text）")]
    public SEED.Text? checksText;

    /// <summary>進捗の Text。</summary>
    [SerializeField(Label = "進捗", Tooltip = "進捗表示の Text（子名|Text）")]
    public SEED.Text? progressText;

    /// <summary>出現・退場で伸縮させるパネル本体の CanvasTransform（未設定ならこのアクタ自身）。</summary>
    [SerializeField(Label = "本体の変形", Tooltip = "伸縮させる CanvasTransform。未設定ならこのアクタ自身を使う")]
    public SEED.CanvasTransform? rootTransform;

    // ─── 演出パラメータ ─────────────────────────────────────

    /// <summary>出現演出の秒数。</summary>
    [Header("演出"), SerializeField(Label = "出現の秒数", Tooltip = "パネルが 0 倍から等倍になるまでの秒数")]
    public float appearSeconds = DefaultAppearSeconds;

    /// <summary>退場演出の秒数。</summary>
    [SerializeField(Label = "退場の秒数", Tooltip = "隠すときに縮んで消えるまでの秒数")]
    public float disappearSeconds = DefaultDisappearSeconds;

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>現在の表示段階。</summary>
    private PanelVisualState visualState = PanelVisualState.Hidden;

    /// <summary>表示要求が出ているか（演出の向きを決める）。</summary>
    private bool visible;

    /// <summary>演出の経過秒（実時間）。</summary>
    private float animTimer;

    /// <summary>シーンで設定された地の色（初回だけ控える）。</summary>
    private SEED.Color bgBaseColor;

    /// <summary>シーンで設定されたミッション名の色。</summary>
    private SEED.Color titleBaseColor;

    /// <summary>シーンで設定された目的文の色。</summary>
    private SEED.Color objectiveBaseColor;

    /// <summary>シーンで設定されたサブ目標の色。</summary>
    private SEED.Color checksBaseColor;

    /// <summary>シーンで設定された進捗の色。</summary>
    private SEED.Color progressBaseColor;

    /// <summary>地の色を控え済みか。</summary>
    private bool bgColorCaptured;

    /// <summary>ミッション名の色を控え済みか。</summary>
    private bool titleColorCaptured;

    /// <summary>目的文の色を控え済みか。</summary>
    private bool objectiveColorCaptured;

    /// <summary>サブ目標の色を控え済みか。</summary>
    private bool checksColorCaptured;

    /// <summary>進捗の色を控え済みか。</summary>
    private bool progressColorCaptured;

    /// <summary>シーンで設定された本体の拡大率（初回だけ控える）。</summary>
    private SEED.Vector2 rootBaseScale = SEED.Vector2.One;

    /// <summary>本体の拡大率を控え済みか。</summary>
    private bool rootScaleCaptured;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>初期化。元の色・拡大率を控えてから隠す。</summary>
    public override void OnStart()
    {
        CaptureBaseColors();
        CaptureBaseTransform();

        // すでに表示要求が来ている（他スクリプトの OnStart が先に走った）ならそれを尊重する
        if (visible)
        {
            ApplyVisibility();
            return;
        }

        HideImmediate();
    }

    /// <summary>
    /// 毎フレームの演出更新。隠れている間は何もしない。
    /// </summary>
    /// <param name="ctx">フレーム情報（使わず実時間を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (visualState == PanelVisualState.Hidden) { return; }

        // 参照が後から有効になった場合に備えて毎フレーム控え直しを試みる（済んでいれば何もしない）
        CaptureBaseColors();
        CaptureBaseTransform();

        animTimer += SEED.Time.UnscaledDeltaTime;
        UpdateVisual();
    }

    // ─── 公開 API ────────────────────────────────────────────

    /// <summary>表示中か（退場演出中も true）。</summary>
    public bool IsVisible => visible;

    /// <summary>
    /// パネルの内容を差し替えて表示する【表示の唯一の入口】。
    ///
    /// すでに出ているときは演出を再生し直さず、中身だけを差し替える
    /// （ミッション中に進捗が変わるたびに毎フレーム呼んでよい）。
    /// </summary>
    /// <param name="title">ミッション名。</param>
    /// <param name="objective">目的文。</param>
    /// <param name="objectives">サブ目標のチェック（null・空なら行ごと空にする）。</param>
    /// <param name="progress">進捗の 1 行（空なら行ごと空にする）。</param>
    public void Apply(string title, string objective, IReadOnlyList<MissionObjective>? objectives, string progress)
    {
        CaptureBaseColors();
        CaptureBaseTransform();

        SetContent(titleText,     title);
        SetContent(objectiveText, objective);
        SetContent(checksText,    BuildObjectiveLines(objectives));
        SetContent(progressText,  progress);

        if (!visible) { BeginAppear(); }
    }

    /// <summary>
    /// パネルを隠す【非表示要求の唯一の入口】。
    /// 出ているときは退場演出を再生し、すでに隠れていれば何もしない。
    /// </summary>
    public void Hide()
    {
        if (!visible)
        {
            HideImmediate();
            return;
        }

        visible     = false;
        visualState = PanelVisualState.Disappearing;
        animTimer   = 0f;
    }

    // ─── 内部処理: 演出 ─────────────────────────────────────

    /// <summary>出現演出を始める【出現の唯一の入口】。</summary>
    private void BeginAppear()
    {
        visible     = true;
        visualState = PanelVisualState.Appearing;
        animTimer   = 0f;

        ApplyVisibility();
        // 1 フレームだけ等倍のパネルが見えてしまう事故を防ぐため、この場で 1 回進める
        UpdateVisual();
    }

    /// <summary>
    /// 即座に隠す（演出を挟まない）。
    ///
    /// 隠す前に必ず元の色・拡大率を控える。ディレクタの OnStart が
    /// このパネルの OnStart より先に走って Hide を呼ぶことがあり、
    /// 先にアルファ 0 を書いてしまうと「透明」を元色として控えてしまい、
    /// 以後どれだけ表示要求を出しても見えなくなるため。
    /// </summary>
    private void HideImmediate()
    {
        CaptureBaseColors();
        CaptureBaseTransform();

        visible     = false;
        visualState = PanelVisualState.Hidden;
        animTimer   = 0f;

        ApplyVisibility();
        ApplyRootScale(ScaleZero);
    }

    /// <summary>現在の表示段階に応じて見た目を更新する【演出更新の唯一の集約点】。</summary>
    private void UpdateVisual()
    {
        switch (visualState)
        {
            case PanelVisualState.Appearing:
                UpdateAppearing();
                break;

            case PanelVisualState.Disappearing:
                UpdateDisappearing();
                break;

            case PanelVisualState.Shown:
                ApplyRootScale(ProgressComplete);
                break;
        }
    }

    /// <summary>出現演出（少し行き過ぎてから収まる）。</summary>
    private void UpdateAppearing()
    {
        float rate = Easing.OutBack(Easing.Progress01(animTimer, appearSeconds));
        ApplyRootScale(rate);

        if (animTimer >= SEED.Mathf.Max(appearSeconds, 0f))
        {
            visualState = PanelVisualState.Shown;
            ApplyRootScale(ProgressComplete);
        }
    }

    /// <summary>退場演出（縮んで消える）。</summary>
    private void UpdateDisappearing()
    {
        float remain = ProgressComplete - Easing.Progress01(animTimer, disappearSeconds);
        ApplyRootScale(Easing.InCubic(remain));

        if (animTimer >= SEED.Mathf.Max(disappearSeconds, 0f))
        {
            ApplyRootScale(ScaleZero);
            HideImmediate();
        }
    }

    // ─── 内部処理: 見た目の適用 ─────────────────────────────

    /// <summary>
    /// 本体の拡大率へ演出倍率を掛ける（シーンで設定された拡大率が基準）。
    /// </summary>
    /// <param name="rate">演出倍率（0 で畳む、1 で等倍）。</param>
    private void ApplyRootScale(float rate)
    {
        if (ResolveRootTransform() is not { } ct || !ct.IsValid) { return; }
        ct.Scale = new SEED.Vector2(rootBaseScale.x * rate, rootBaseScale.y * rate);
    }

    /// <summary>表示 / 非表示をアルファで適用する【可視化の唯一の実装】。</summary>
    private void ApplyVisibility()
    {
        float alphaScale = visible ? AlphaScaleVisible : AlphaScaleHidden;

        if (bgSprite is { } bg && bg.IsValid)
        {
            bg.Color = bgBaseColor.WithAlpha(bgBaseColor.a * alphaScale);
        }
        ApplyTextAlpha(titleText,     titleBaseColor,     alphaScale);
        ApplyTextAlpha(objectiveText, objectiveBaseColor, alphaScale);
        ApplyTextAlpha(checksText,    checksBaseColor,    alphaScale);
        ApplyTextAlpha(progressText,  progressBaseColor,  alphaScale);
    }

    /// <summary>
    /// Text 1 つのアルファへ倍率を掛ける。
    /// </summary>
    /// <param name="text">対象の Text（null・無効なら何もしない）。</param>
    /// <param name="baseColor">シーンで設定された色。</param>
    /// <param name="alphaScale">アルファ倍率。</param>
    private static void ApplyTextAlpha(SEED.Text? text, SEED.Color baseColor, float alphaScale)
    {
        if (text is not { } label || !label.IsValid) { return; }
        label.Color = baseColor.WithAlpha(baseColor.a * alphaScale);
    }

    /// <summary>
    /// Text へ文字列を流し込む（null・無効なら何もしない）。
    /// </summary>
    /// <param name="text">対象の Text。</param>
    /// <param name="content">流し込む文字列（null は空文字扱い）。</param>
    private static void SetContent(SEED.Text? text, string? content)
    {
        if (text is not { } label || !label.IsValid) { return; }
        label.Content = content ?? EmptyText;
    }

    /// <summary>
    /// サブ目標のリストを 1 つの複数行文字列にまとめる。
    /// </summary>
    /// <param name="objectives">サブ目標（null・空なら空文字）。</param>
    /// <returns>改行で連結した表示行。</returns>
    private static string BuildObjectiveLines(IReadOnlyList<MissionObjective>? objectives)
    {
        if (objectives is null || objectives.Count == 0) { return EmptyText; }

        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < objectives.Count; i++)
        {
            if (i > 0) { builder.Append(ObjectiveSeparator); }
            builder.Append(objectives[i].ToDisplayLine());
        }
        return builder.ToString();
    }

    // ─── 内部処理: 元の値の控え ─────────────────────────────

    /// <summary>
    /// シーンで設定された色を部品ごとに 1 度だけ控える。
    ///
    /// 参照が有効になるタイミングはスクリプト間で保証されないので、
    /// 一括のフラグではなく部品ごとに控え済みかを持つ
    /// （一括だと最初の 1 回で「まだ無効な部品」の既定色を焼き付けてしまう）。
    /// </summary>
    private void CaptureBaseColors()
    {
        if (!bgColorCaptured && bgSprite is { } bg && bg.IsValid)
        {
            bgBaseColor = bg.Color;
            bgColorCaptured = true;
        }
        if (!titleColorCaptured && titleText is { } title && title.IsValid)
        {
            titleBaseColor = title.Color;
            titleColorCaptured = true;
        }
        if (!objectiveColorCaptured && objectiveText is { } objective && objective.IsValid)
        {
            objectiveBaseColor = objective.Color;
            objectiveColorCaptured = true;
        }
        if (!checksColorCaptured && checksText is { } checks && checks.IsValid)
        {
            checksBaseColor = checks.Color;
            checksColorCaptured = true;
        }
        if (!progressColorCaptured && progressText is { } progress && progress.IsValid)
        {
            progressBaseColor = progress.Color;
            progressColorCaptured = true;
        }
    }

    /// <summary>シーンで設定された本体の拡大率を 1 度だけ控える。</summary>
    private void CaptureBaseTransform()
    {
        if (rootScaleCaptured) { return; }
        if (ResolveRootTransform() is not { } ct || !ct.IsValid) { return; }

        rootBaseScale = ct.Scale;
        rootScaleCaptured = true;
    }

    /// <summary>
    /// 伸縮させる CanvasTransform を解決する（未設定ならこのアクタ自身）。
    /// </summary>
    /// <returns>解決できた CanvasTransform。できなければ null。</returns>
    private SEED.CanvasTransform? ResolveRootTransform()
    {
        if (rootTransform is { } assigned && assigned.IsValid) { return assigned; }
        if (gameObject.GetComponent<SEED.CanvasTransform>() is { } own && own.IsValid) { return own; }
        return null;
    }
}
