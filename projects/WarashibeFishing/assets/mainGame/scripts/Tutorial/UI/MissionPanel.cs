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
/// 表示要求は <see cref="Show"/>、非表示要求は <see cref="Hide"/> に一本化している
/// （<see cref="Apply"/> は内容の差し替えだけを行い、表示状態には触れない）。
/// 出現は拡大率の OutBack（0 倍 → 等倍）、非表示はアルファのフェードアウト
/// （等倍のまま <see cref="hideFadeSeconds"/> かけて 1 → 0）と、演出の質が違う。
///
/// 【時間軸】
/// 出現・非表示の演出はすべて実時間（Time.UnscaledDeltaTime）で進める。
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

    /// <summary>非表示にするときのフェードアウトの既定の秒数。</summary>
    private const float DefaultHideFadeSeconds = 0.30f;

    /// <summary>アンカーの下端とパネル上端のあいだに空ける既定の隙間（px）。</summary>
    private const float DefaultAnchorGapPx = 8f;

    /// <summary>アンカーの切り替えでパネルが移動しきるまでの既定の秒数。</summary>
    private const float DefaultMoveSeconds = 0.25f;

    /// <summary>
    /// アンカー高さの「自動計測」を表す値（この値以下なら Sprite から実寸を読む）。
    /// </summary>
    private const float AutoMeasureHeightPx = 0f;

    /// <summary>高さを計測できなかったときに使う値（＝厚みゼロとして扱う）。</summary>
    private const float UnknownHeightPx = 0f;

    /// <summary>ピボットの下端に対応する正規化値（下端＝1.0）。</summary>
    private const float PivotBottom = 1f;

    /// <summary>目標位置が「変わった」とみなす最小の差（px）。浮動小数の揺れで再始動しないための幅。</summary>
    private const float TargetChangeEpsilonPx = 0.01f;

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

        /// <summary>非表示演出（アルファのフェードアウト）の最中。</summary>
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

    /// <summary>
    /// 非表示にするときのフェードアウトの秒数（実時間・Unscaled）。
    /// 帯・文字の<b>アルファ</b>をこの秒数で 1 → 0 へ落とす（拡大率は等倍のまま動かさない）。
    /// </summary>
    [SerializeField(Label = "非表示の秒数", Tooltip = "隠すときにアルファがフェードアウトするまでの秒数（実時間）")]
    public float hideFadeSeconds = DefaultHideFadeSeconds;

    // ─── 追従配置（アンカーの下へ潜り込む）─────────────────

    /// <summary>レーダーが出ていないときに下へ付くアクタ（Esc メニューヒント）。</summary>
    [Header("追従配置"), SerializeField(Label = "アンカー(レーダー無)", Tooltip = "レーダー非表示中に、このアクタの下へパネルを置く（Esc メニューヒント）")]
    public SEED.GameObject? anchorWhenRadarHidden;

    /// <summary>
    /// <see cref="anchorWhenRadarHidden"/> の高さ（px）。
    /// 0 なら対象の Sprite から実寸を読む（Sprite を持たない対象のときだけ手で入れる）。
    /// </summary>
    [SerializeField(Label = "アンカー高さ(レーダー無/px)", Tooltip = "0 なら対象の Sprite から自動計測。Sprite を持たないアクタのときだけ実寸を入れる")]
    public float anchorWhenRadarHiddenHeightPx = AutoMeasureHeightPx;

    /// <summary>レーダーが出ているときに下へ付くアクタ（魚レーダー本体）。</summary>
    [SerializeField(Label = "アンカー(レーダー有)", Tooltip = "レーダー表示中に、このアクタの下へパネルを置く（FishRadar 本体）")]
    public SEED.GameObject? anchorWhenRadarShown;

    /// <summary>
    /// <see cref="anchorWhenRadarShown"/> の高さ（px）。
    /// レーダーはプリミティブ描画で Sprite を持たないため、
    /// 「背景の円の直径（＝FishRadar の背景の円の半径 × 2）」を手で入れる。
    /// </summary>
    [SerializeField(Label = "アンカー高さ(レーダー有/px)", Tooltip = "0 なら対象の Sprite から自動計測。レーダーは Sprite を持たないので背景の円の直径を入れる")]
    public float anchorWhenRadarShownHeightPx = AutoMeasureHeightPx;

    /// <summary>アンカーの切り替え判断に使う魚レーダー（未設定なら常に「レーダー無」側）。</summary>
    [SerializeField(Label = "魚レーダー", Tooltip = "表示状態（FishRadar.IsShown）を見てアンカーを切り替える")]
    public FishRadar? radar;

    /// <summary>アンカーの下端とパネル上端のあいだに空ける隙間（px）。</summary>
    [SerializeField(Label = "アンカーとの隙間(px)", Tooltip = "アンカーの下端からパネル上端までの距離")]
    public float anchorGapPx = DefaultAnchorGapPx;

    /// <summary>アンカーが切り替わったときに移動しきるまでの秒数（実時間）。</summary>
    [SerializeField(Label = "移動の秒数", Tooltip = "アンカーが切り替わったときにパネルが移動しきるまでの秒数（実時間）")]
    public float moveSeconds = DefaultMoveSeconds;

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

    /// <summary>追従移動の開始 Y（親相対のキャンバス座標）。</summary>
    private float moveFromY;

    /// <summary>追従移動の目標 Y（親相対のキャンバス座標）。</summary>
    private float moveToY;

    /// <summary>追従移動の経過秒（実時間）。</summary>
    private float moveTimer;

    /// <summary>追従移動の初期化が済んだか（初回だけ演出なしでその場に置くための判定）。</summary>
    private bool followInitialized;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>初期化。元の色・拡大率を控えてから隠す。</summary>
    public override void OnStart()
    {
        CaptureBaseColors();
        CaptureBaseTransform();

        // 初回は演出なしで、いま選ばれているアンカーの真下へ置く
        UpdateFollowPosition(0f);

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
        // 追従配置は隠れているあいだも進める
        // （隠れている間にレーダーが出入りしても、次に出てきたとき正しい位置にいるようにするため）
        UpdateFollowPosition(SEED.Time.UnscaledDeltaTime);

        if (visualState == PanelVisualState.Hidden) { return; }

        // 参照が後から有効になった場合に備えて毎フレーム控え直しを試みる（済んでいれば何もしない）
        CaptureBaseColors();
        CaptureBaseTransform();

        animTimer += SEED.Time.UnscaledDeltaTime;
        UpdateVisual();
    }

    // ─── 公開 API ────────────────────────────────────────────

    /// <summary>
    /// 表示要求が出ているか。
    /// <see cref="Hide"/> を呼んだ瞬間（フェードアウトの演出中）から false になる点に注意
    /// （＝「まだ画面上に見えているか」ではなく「表示指示が出ているか」を返す）。
    /// </summary>
    public bool IsVisible => visible;

    /// <summary>
    /// パネルの内容を差し替える【内容更新の唯一の入口】。
    ///
    /// 表示 / 非表示には一切触れない（隠れているあいだに呼んでも見た目は変わらない）。
    /// 表示するかどうかは呼び出し側が <see cref="Show"/> / <see cref="Hide"/> で決める
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

        // 表示 / 非表示の切り替えはここでは行わない【Show / Hide に一本化】。
        // 呼び出し側（TutorialDirector）が「いま指示を出している最中か」を判断して
        // Show / Hide を呼ぶ設計にしたため、内容の更新だけを行うこの入口が
        // 勝手に表示状態を変えると、隠しておきたい場面（Intro・クリアバナー等）で
        // 意図せず出てしまう事故になる。
    }

    /// <summary>
    /// パネルを表示する【表示要求の唯一の入口】。
    /// すでに出ている（退場演出中も含む）ときは何もしない。
    /// </summary>
    public void Show()
    {
        if (visible) { return; }
        BeginAppear();
    }

    /// <summary>
    /// パネルを隠す【非表示要求の唯一の入口】。
    /// 出ているときはフェードアウト演出を再生し、すでに隠れていれば何もしない。
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

    /// <summary>
    /// 非表示演出（帯・文字の<b>アルファ</b>を 1 → 0 へフェードする）。
    /// 拡大率は等倍のまま動かさない（縮小まで重ねると「フェード」ではなく
    /// 「縮んで消える」印象になり、要求どおりの見た目にならないため）。
    /// </summary>
    private void UpdateDisappearing()
    {
        float alphaRate = ProgressComplete - Easing.Progress01(animTimer, hideFadeSeconds);
        ApplyRootScale(ProgressComplete);
        ApplyAlphaScale(SEED.Mathf.Clamped01(alphaRate));

        if (animTimer >= SEED.Mathf.Max(hideFadeSeconds, 0f))
        {
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

    /// <summary>表示 / 非表示を二値のアルファで適用する（即時の切り替え専用）。</summary>
    private void ApplyVisibility()
        => ApplyAlphaScale(visible ? AlphaScaleVisible : AlphaScaleHidden);

    /// <summary>
    /// 帯・文字のアルファへ倍率を掛ける【アルファ適用の唯一の実装】。
    /// 二値の表示切り替え（<see cref="ApplyVisibility"/>）とフェードアウト演出
    /// （<see cref="UpdateDisappearing"/>）の両方がここを通る。
    /// </summary>
    /// <param name="alphaScale">アルファ倍率（0 で透明、1 でシーン設定どおり）。</param>
    private void ApplyAlphaScale(float alphaScale)
    {
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

    // ─── 内部処理: 追従配置 ─────────────────────────────────

    /// <summary>
    /// アンカーの真下へパネルを移動させる【配置更新の唯一の場所】。
    ///
    /// 魚レーダーが出ていなければ <see cref="anchorWhenRadarHidden"/>、
    /// 出ていれば <see cref="anchorWhenRadarShown"/> の<b>下端</b>を基準にし、
    /// そこから <see cref="anchorGapPx"/> だけ下がった位置を目標にする。
    /// 目標が変わったらその場を起点に <see cref="moveSeconds"/> かけて滑らかに移す。
    ///
    /// 【前提】アンカーとこのパネルは<b>同じ親（キャンバス）・同じアンカー設定</b>であること。
    /// <c>CanvasTransform.Position</c> は親相対なので、親が違うと座標が比較できない
    /// （MainGame.scene ではどちらも FishingUI 直下の兄弟にしてある）。
    ///
    /// 【前提】このパネルのピボット Y は 0（上端）であること。
    /// 目標 Y をそのまま <c>Position.y</c> に書くので、ピボットが上端でないと
    /// 「上端＋隙間」の意味にならない（出現演出で拡大率が変わっても位置がぶれないよう、
    ///   パネル自身のサイズは意図的に計算へ入れていない）。
    /// </summary>
    /// <param name="unscaledDeltaTime">このフレームの実時間の経過秒（0 なら即座に目標へ置く）。</param>
    private void UpdateFollowPosition(float unscaledDeltaTime)
    {
        if (ResolveRootTransform() is not { } ct || !ct.IsValid) { return; }

        // どちらのアンカーに付くかは「レーダーを出す指示が出ているか」だけで決まる
        bool radarShown = radar is { } activeRadar && activeRadar.IsShown;
        SEED.GameObject? anchor = radarShown ? anchorWhenRadarShown : anchorWhenRadarHidden;
        float anchorHeightPx = radarShown ? anchorWhenRadarShownHeightPx : anchorWhenRadarHiddenHeightPx;

        // アンカーが結線されていない・無効なら、シーンで置いた位置のまま何もしない
        if (!TryComputeTargetY(anchor, anchorHeightPx, out float targetY)) { return; }

        // 初回はその場へ置いて基準を作る（シーン開始直後に画面を横切らせないため）
        if (!followInitialized)
        {
            followInitialized = true;
            moveFromY = targetY;
            moveToY   = targetY;
            moveTimer = 0f;
            ApplyPositionY(ct, targetY);
            return;
        }

        // 目標が変わったら、いまいる位置を起点に移動をやり直す（切替の連打でも飛ばない）
        if (SEED.Mathf.Abs(targetY - moveToY) > TargetChangeEpsilonPx)
        {
            moveFromY = ct.Position.y;
            moveToY   = targetY;
            moveTimer = 0f;
        }

        moveTimer += unscaledDeltaTime;
        float rate = Easing.InOutSine(Easing.Progress01(moveTimer, moveSeconds));
        ApplyPositionY(ct, SEED.Mathf.Lerp(moveFromY, moveToY, rate));
    }

    /// <summary>
    /// アンカーの下端から、パネルを置くべき Y 座標を求める。
    /// </summary>
    /// <param name="anchor">基準にするアクタ（null・無効なら false を返す）。</param>
    /// <param name="heightOverridePx">高さの手入力値（<see cref="AutoMeasureHeightPx"/> 以下なら Sprite から計測）。</param>
    /// <param name="targetY">求まった目標 Y（親相対のキャンバス座標）。</param>
    /// <returns>求まったら true。</returns>
    private bool TryComputeTargetY(SEED.GameObject? anchor, float heightOverridePx, out float targetY)
    {
        targetY = 0f;

        if (anchor is not { } target || !target.IsValid) { return false; }
        if (target.GetComponent<SEED.CanvasTransform>() is not { } anchorCt || !anchorCt.IsValid) { return false; }

        float heightPx = heightOverridePx > AutoMeasureHeightPx
            ? heightOverridePx
            : MeasureHeightPx(target);

        // 下端 = 位置 +（ピボットから下端までの割合）× 高さ × 拡大率
        //   ピボット (0,0)   → 位置が上端なので下端は 1.0 個ぶん下
        //   ピボット (.5,.5) → 位置が中央なので下端は 0.5 個ぶん下（＝レーダーの半径）
        float bottomY = anchorCt.Position.y
                      + (PivotBottom - anchorCt.Pivot.y) * heightPx * anchorCt.Scale.y;

        targetY = bottomY + anchorGapPx;
        return true;
    }

    /// <summary>
    /// アクタの高さ（px）を Sprite から計測する。
    /// Sprite を持たない（＝プリミティブ描画の魚レーダーなど）場合は測れないので
    /// 厚みゼロを返す。その場合はインスペクタの高さ欄に実寸を入れて補う。
    /// </summary>
    /// <param name="anchor">計測するアクタ。</param>
    /// <returns>計測できた高さ（px）。測れなければ 0。</returns>
    private static float MeasureHeightPx(SEED.GameObject anchor)
    {
        if (anchor.GetComponent<SEED.Sprite>() is { } sprite && sprite.IsValid) { return sprite.Height; }
        return UnknownHeightPx;
    }

    /// <summary>
    /// パネルの Y 座標だけを書き換える（X はシーンで置いた値のまま動かさない）。
    /// </summary>
    /// <param name="ct">パネル本体の CanvasTransform。</param>
    /// <param name="y">書き込む Y 座標。</param>
    private static void ApplyPositionY(SEED.CanvasTransform ct, float y)
        => ct.Position = new SEED.Vector2(ct.Position.x, y);
}
