// ============================================================================
//  MissionClearBanner.cs
//  ミッション達成時に画面中央へ出す「ミッションクリア！」バナー。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// ミッション達成のバナー【達成を見せる演出の唯一の置き場】。
///
/// 【責務】
/// 「出す」「一拍置く」「決定で閉じる」だけ。次に何をするかは知らない
/// （進行は <see cref="TutorialDirector"/> の責務）。
///
/// 【手触りの狙い】
/// 達成の瞬間に次の説明へ飛ばされると「何を達成したのか」が読めない。
/// EaseOutBack で勢いよく出したあと <see cref="holdSeconds"/> のあいだは
/// 決定を受け付けず、必ず一拍置いてからプレイヤーの決定で閉じる。
///
/// 【構成（シーン側）】
///  MissionClearBanner       … このスクリプト
///   MissionClearBg          … Sprite（帯。仮素材は white.png の着色）
///   MissionClearText        … Text（「ミッションクリア！」）
///
/// 【時間軸】
/// すべて実時間（Time.UnscaledDeltaTime）。バナー表示中はゲーム時間を止めるため。
/// </summary>
public class MissionClearBanner : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>隠しているときのアルファ倍率。</summary>
    private const float AlphaScaleHidden = 0f;

    /// <summary>表示しているときのアルファ倍率。</summary>
    private const float AlphaScaleVisible = 1f;

    /// <summary>出現演出の既定の秒数。</summary>
    private const float DefaultAppearSeconds = 0.38f;

    /// <summary>出し切ってから決定を受け付けるまでの既定の秒数（＝一拍）。</summary>
    private const float DefaultHoldSeconds = 0.60f;

    /// <summary>退場演出の既定の秒数。</summary>
    private const float DefaultDisappearSeconds = 0.18f;

    /// <summary>バナーに出す既定の文字列。</summary>
    private const string DefaultBannerText = "ミッションクリア！";

    /// <summary>進捗が 1 のときの値。</summary>
    private const float ProgressComplete = 1f;

    /// <summary>拡大率 0（畳んだ状態）。</summary>
    private const float ScaleZero = 0f;

    // ─── 表示段階 ────────────────────────────────────────────

    /// <summary>バナーの表示段階。</summary>
    private enum BannerState
    {
        /// <summary>隠れている。</summary>
        Hidden,

        /// <summary>出現演出の最中（決定は受け付けない）。</summary>
        Appearing,

        /// <summary>出し切って一拍置いている最中（決定は受け付けない）。</summary>
        Holding,

        /// <summary>決定待ち。</summary>
        Waiting,

        /// <summary>退場演出の最中。</summary>
        Disappearing,
    }

    // ─── 参照（インスペクタで結線）───────────────────────────

    /// <summary>帯の Sprite。</summary>
    [Header("参照"), SerializeField(Label = "帯のスプライト", Tooltip = "バナーの地（子名|Sprite）")]
    public SEED.Sprite? bgSprite;

    /// <summary>バナーの文字の Text。</summary>
    [SerializeField(Label = "文字", Tooltip = "バナーの文字の Text（子名|Text）")]
    public SEED.Text? bannerText;

    /// <summary>伸縮させるバナー本体の CanvasTransform（未設定ならこのアクタ自身）。</summary>
    [SerializeField(Label = "本体の変形", Tooltip = "伸縮させる CanvasTransform。未設定ならこのアクタ自身を使う")]
    public SEED.CanvasTransform? rootTransform;

    // ─── 演出パラメータ ─────────────────────────────────────

    /// <summary>バナーに出す文字列。</summary>
    [Header("演出"), SerializeField(Label = "文言", Tooltip = "バナーに出す文字列")]
    public string bannerLabel = DefaultBannerText;

    /// <summary>出現演出の秒数。</summary>
    [SerializeField(Label = "出現の秒数", Tooltip = "バナーが 0 倍から等倍になるまでの秒数")]
    public float appearSeconds = DefaultAppearSeconds;

    /// <summary>出し切ってから決定を受け付けるまでの秒数（一拍置く時間）。</summary>
    [SerializeField(Label = "間の秒数", Tooltip = "出し切ってから決定を受け付けるまでの秒数（一瞬で進ませないための間）")]
    public float holdSeconds = DefaultHoldSeconds;

    /// <summary>退場演出の秒数。</summary>
    [SerializeField(Label = "退場の秒数", Tooltip = "閉じるときに縮んで消えるまでの秒数")]
    public float disappearSeconds = DefaultDisappearSeconds;

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>現在の表示段階。</summary>
    private BannerState state = BannerState.Hidden;

    /// <summary>いまの段階に入ってからの経過秒（実時間）。</summary>
    private float timer;

    /// <summary>閉じ切ったことを 1 回だけ外へ知らせるためのフラグ。</summary>
    private bool finished;

    /// <summary>シーンで設定された帯の色（初回だけ控える）。</summary>
    private SEED.Color bgBaseColor;

    /// <summary>シーンで設定された文字の色（初回だけ控える）。</summary>
    private SEED.Color textBaseColor;

    /// <summary>帯の色を控え済みか。</summary>
    private bool bgColorCaptured;

    /// <summary>文字の色を控え済みか。</summary>
    private bool textColorCaptured;

    /// <summary>シーンで設定された本体の拡大率（初回だけ控える）。</summary>
    private SEED.Vector2 rootBaseScale = SEED.Vector2.One;

    /// <summary>本体の拡大率を控え済みか。</summary>
    private bool rootScaleCaptured;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>初期化。元の色・拡大率を控えてから隠す。</summary>
    public override void OnStart()
    {
        CaptureBase();
        HideImmediate();
    }

    /// <summary>
    /// 毎フレームの演出更新。隠れている間は何もしない。
    /// </summary>
    /// <param name="ctx">フレーム情報（使わず実時間を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (state == BannerState.Hidden) { return; }

        CaptureBase();
        timer += SEED.Time.UnscaledDeltaTime;

        switch (state)
        {
            case BannerState.Appearing:
                UpdateAppearing();
                break;

            case BannerState.Holding:
                UpdateHolding();
                break;

            case BannerState.Waiting:
                UpdateWaiting();
                break;

            case BannerState.Disappearing:
                UpdateDisappearing();
                break;
        }
    }

    // ─── 公開 API ────────────────────────────────────────────

    /// <summary>バナーが出ている最中か（隠れていれば false）。</summary>
    public bool IsPlaying => state != BannerState.Hidden;

    /// <summary>
    /// 閉じ切ったか【進行側が「次へ進んでよい」と判断する唯一の合図】。
    /// 一度 true を返すと自動的に降ろされるので、毎フレーム問い合わせてよい。
    /// </summary>
    /// <returns>このフレームで閉じ切っていれば true。</returns>
    public bool ConsumeFinished()
    {
        if (!finished) { return false; }
        finished = false;
        return true;
    }

    /// <summary>
    /// バナーを再生する【再生の唯一の入口】。
    /// 再生中に呼ばれた場合は最初からやり直す。
    /// </summary>
    public void Play()
    {
        CaptureBase();

        if (bannerText is { } label && label.IsValid) { label.Content = bannerLabel; }

        finished = false;
        state    = BannerState.Appearing;
        timer    = 0f;

        ApplyAlpha(AlphaScaleVisible);
        ApplyRootScale(ScaleZero);
    }

    /// <summary>
    /// バナーを演出なしで即座に畳む（チュートリアルの打ち切り・破棄時に使う）。
    /// </summary>
    public void Cancel()
    {
        finished = false;
        HideImmediate();
    }

    // ─── 内部処理: 段階ごとの更新 ───────────────────────────

    /// <summary>出現演出（勢いよく出て少し行き過ぎてから収まる）。</summary>
    private void UpdateAppearing()
    {
        ApplyRootScale(Easing.OutBack(Easing.Progress01(timer, appearSeconds)));

        if (timer < SEED.Mathf.Max(appearSeconds, 0f)) { return; }

        ApplyRootScale(ProgressComplete);
        state = BannerState.Holding;
        timer = 0f;
    }

    /// <summary>一拍置いている最中（決定は受け付けない）。</summary>
    private void UpdateHolding()
    {
        ApplyRootScale(ProgressComplete);

        if (timer < SEED.Mathf.Max(holdSeconds, 0f)) { return; }

        state = BannerState.Waiting;
        timer = 0f;
    }

    /// <summary>決定待ち（押されたら退場演出へ）。</summary>
    private void UpdateWaiting()
    {
        ApplyRootScale(ProgressComplete);

        if (!IsConfirmPressed()) { return; }

        state = BannerState.Disappearing;
        timer = 0f;
    }

    /// <summary>退場演出（縮んで消え、閉じ切ったら合図を立てる）。</summary>
    private void UpdateDisappearing()
    {
        float remain = ProgressComplete - Easing.Progress01(timer, disappearSeconds);
        ApplyRootScale(Easing.InCubic(remain));

        if (timer < SEED.Mathf.Max(disappearSeconds, 0f)) { return; }

        HideImmediate();
        finished = true;   // ConsumeFinished で 1 回だけ拾われる
    }

    // ─── 内部処理: 入力 ─────────────────────────────────────

    /// <summary>
    /// 決定入力（Enter / Space / 左クリック）が押されたか。
    /// バナーは説明窓と同じ操作感で閉じたいので、判定も同じ組み合わせにする。
    /// </summary>
    /// <returns>押されていれば true。</returns>
    private static bool IsConfirmPressed()
        => SceneFlow.IsConfirmPressed() || SEED.Input.GetMouseButtonDown(SEED.MouseButton.Left);

    // ─── 内部処理: 見た目の適用 ─────────────────────────────

    /// <summary>
    /// 即座に隠す（演出を挟まない）。
    ///
    /// 隠す前に必ず元の色・拡大率を控える。ディレクタの OnStart が
    /// このバナーの OnStart より先に走って Cancel を呼ぶことがあり、
    /// 先にアルファ 0 を書いてしまうと「透明」を元色として控えてしまい、
    /// 以後どれだけ再生してもバナーが見えなくなるため。
    /// </summary>
    private void HideImmediate()
    {
        CaptureBase();

        state = BannerState.Hidden;
        timer = 0f;

        ApplyAlpha(AlphaScaleHidden);
        ApplyRootScale(ScaleZero);
    }

    /// <summary>
    /// 帯と文字のアルファへ倍率を掛ける。
    /// </summary>
    /// <param name="alphaScale">アルファ倍率（0 で不可視、1 でシーン設定どおり）。</param>
    private void ApplyAlpha(float alphaScale)
    {
        if (bgSprite is { } bg && bg.IsValid)
        {
            bg.Color = bgBaseColor.WithAlpha(bgBaseColor.a * alphaScale);
        }
        if (bannerText is { } label && label.IsValid)
        {
            label.Color = textBaseColor.WithAlpha(textBaseColor.a * alphaScale);
        }
    }

    /// <summary>
    /// 本体の拡大率へ演出倍率を掛ける（シーンで設定された拡大率が基準）。
    /// </summary>
    /// <param name="rate">演出倍率（0 で畳む、1 で等倍）。</param>
    private void ApplyRootScale(float rate)
    {
        if (ResolveRootTransform() is not { } ct || !ct.IsValid) { return; }
        ct.Scale = new SEED.Vector2(rootBaseScale.x * rate, rootBaseScale.y * rate);
    }

    /// <summary>シーンで設定された色・拡大率を部品ごとに 1 度だけ控える。</summary>
    private void CaptureBase()
    {
        if (!bgColorCaptured && bgSprite is { } bg && bg.IsValid)
        {
            bgBaseColor = bg.Color;
            bgColorCaptured = true;
        }
        if (!textColorCaptured && bannerText is { } label && label.IsValid)
        {
            textBaseColor = label.Color;
            textColorCaptured = true;
        }
        if (!rootScaleCaptured && ResolveRootTransform() is { } ct && ct.IsValid)
        {
            rootBaseScale = ct.Scale;
            rootScaleCaptured = true;
        }
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
