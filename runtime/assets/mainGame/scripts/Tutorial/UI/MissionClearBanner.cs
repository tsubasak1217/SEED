// ============================================================================
//  MissionClearBanner.cs
//  ミッション達成時に画面中央へ出す「ミッションクリア！」バナー。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// ミッション達成のバナー【達成を見せる演出の唯一の置き場】。
///
/// 【責務】
/// 「出す」「一拍置く」「自動でフェードアウトして閉じる」だけ。
/// 次に何をするかは知らない（進行は <see cref="TutorialDirector"/> の責務）。
/// 閉じ切ったことは <see cref="ConsumeFinished"/> で外へ伝える。
///
/// 【手触りの狙い】
/// 達成の瞬間に次の説明へ飛ばされると「何を達成したのか」が読めない。
/// EaseOutBack で勢いよく出したあと <see cref="holdSeconds"/> のあいだは
/// じっくり読ませ、そのあとは決定入力を待たずに <see cref="fadeOutSeconds"/>
/// かけてアルファを 1 → 0 へフェードアウトさせて自動的に閉じる
/// （決定待ちのぶん次の説明が遅れるのを避け、テンポよく進めるため）。
/// フェード中は決定入力を一切読まないため、早送りはできない。
///
/// 【構成（シーン側）】
///  MissionClearBanner       … このスクリプト
///   MissionClearBg          … Sprite（帯。仮素材は white.png の着色）
///   MissionClearText        … Text（「ミッションクリア！」）
///
/// 【時間軸】
/// スクリプト側のタイマー（timer の加算）はすべて実時間（Time.UnscaledDeltaTime）
/// で進む。これはバナー表示中にゲーム時間を止める（Time.Scale = 0）ミッションでも
/// 保持〜フェードアウトの自動進行が機能するようにするため。
///
/// ただし Animator（<see cref="useAnimator"/> = true 時）はスケール後の
/// ゲーム時間で進む別系統のため、Time.Scale = 0 の間は動かない。
/// そのため <see cref="TutorialDirector"/> は <see cref="WillUseAnimator"/> を見て、
/// Animator 演出になる見込みのときはバナー表示中も時間を止めない
/// （決定操作以外の入力は InputGate 側で塞ぐ）。
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

    /// <summary>出し切ってから自動でフェードアウトを始めるまでの既定の秒数（＝一拍）。</summary>
    private const float DefaultHoldSeconds = 1.2f;

    /// <summary>自動フェードアウト演出の既定の秒数。</summary>
    private const float DefaultFadeOutSeconds = 0.4f;

    /// <summary>バナーに出す既定の文字列。</summary>
    private const string DefaultBannerText = "ミッションクリア！";

    /// <summary>Animator に再生させる既定のクリップ名。</summary>
    private const string DefaultAnimatorClipName = "mission_clear";

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

        /// <summary>出現演出の最中。</summary>
        Appearing,

        /// <summary>出し切って一拍置いている最中（この間は自動では閉じない）。</summary>
        Holding,

        /// <summary>
        /// 自動フェードアウトの最中（決定入力は読まない。早送り不可）。
        /// 帯・文字のアルファを 1 → 0 へ下げ、下げ切ったら Hidden へ戻って
        /// 完了フラグ（<see cref="finished"/>）を立てる。
        /// </summary>
        FadingOut,
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

    /// <summary>出し切ってから自動でフェードアウトを始めるまでの秒数（一拍置く時間）。</summary>
    [SerializeField(Label = "間の秒数", Tooltip = "出し切ってから自動でフェードアウトを始めるまでの秒数（一瞬で消えないための間）")]
    public float holdSeconds = DefaultHoldSeconds;

    /// <summary>自動フェードアウト演出の秒数（帯・文字のアルファを 1 から 0 へ下げる時間）。</summary>
    [SerializeField(Label = "フェードアウトの秒数", Tooltip = "保持時間が終わったあと、帯と文字のアルファを 1→0 へ下げて自動的に閉じるまでの秒数")]
    public float fadeOutSeconds = DefaultFadeOutSeconds;

    // ─── クリア効果音 ────────────────────────────────────────

    /// <summary>
    /// バナー表示開始時に 1 回だけ鳴らすクリア効果音の<b>音声辞書キー</b>（空なら鳴らさない）。
    /// 素材も音量もシーン上の音声辞書が持つので、ここはキーだけを持つ
    /// （データドリブン：音を変えるのにコード変更もシーン編集も不要）。
    /// </summary>
    [Header("効果音"), SerializeField(Label = "クリアの音（辞書キー）", Tooltip = "バナー表示開始時に 1 回だけ鳴らす効果音の辞書キー。空なら鳴らさない")]
    public string clearSeKey = "UI/mission_clear";

    // ─── Animator 演出 ──────────────────────────────────────
    // シーン側の MissionClearBanner には AnimatorComponent（クリップ mission_clear）が
    // 付いている。true のときは Show（Play）で Animator にクリップ演出を任せ、
    // スクリプトの EaseOutBack 伸縮（ApplyRootScale）は行わない
    // （同じ CanvasTransform を二重に書き換えて演出が壊れるのを避けるため）。

    /// <summary>true なら Animator にクリップ演出を任せる（false なら常にスクリプト演出）。</summary>
    [Header("Animator 演出"), SerializeField(Label = "Animator を使う", Tooltip = "true: Animator のクリップで演出（CanvasTransform はスクリプトで触らない） / false: 従来のスクリプト伸縮演出")]
    public bool useAnimator = true;

    /// <summary>再生する Animator クリップ名（Animator の clips に登録済みの名前）。</summary>
    [SerializeField(Label = "クリップ名", Tooltip = "Animator に再生させるクリップ名。見つからない場合は従来のスクリプト演出にフォールバックする")]
    public string clipName = DefaultAnimatorClipName;

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>現在の表示段階。</summary>
    private BannerState state = BannerState.Hidden;

    /// <summary>いまの段階に入ってからの経過秒（実時間）。</summary>
    private float timer;

    /// <summary>閉じ切ったことを 1 回だけ外へ知らせるためのフラグ。</summary>
    private bool finished;

    /// <summary>
    /// 今回の表示で Animator による演出が実際に有効になっているか。
    /// true の間は CanvasTransform を Animator に委ねるため、
    /// 各段階の更新ではスクリプト側の ApplyRootScale を呼ばない。
    /// </summary>
    private bool animatorActive;

    /// <summary>Animator 未検出・クリップ未検出によるフォールバック警告を出し済みか（連投防止）。</summary>
    private bool animatorFallbackWarned;

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

        // ポーズ中は帯の表示時間を進めない（実時間で進む作りなので、
        // メニューを開いている間に勝手に消えてしまうのを防ぐ）
        if (InputGate.IsSuspended) { return; }

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

            case BannerState.FadingOut:
                UpdateFadingOut();
                break;
        }
    }

    // ─── 公開 API ────────────────────────────────────────────

    /// <summary>バナーが出ている最中か（隠れていれば false）。</summary>
    public bool IsPlaying => state != BannerState.Hidden;

    /// <summary>
    /// 次に <see cref="Play"/> したとき Animator 演出になる見込みか。
    ///
    /// <see cref="TutorialDirector"/> が「バナー表示中にゲーム時間を止めてよいか」を
    /// 判断するために参照する。Animator の再生位置はスケール後のゲーム時間で進むため
    /// （engine 側 AnimationSystem は SEED.Time.DeltaTime と同源の delta で駆動する）、
    /// Time.Scale を 0 にすると Animator ごと止まってしまう。
    ///
    /// クリップが実際に見つかるかは Play を呼ぶまで確定しないため、ここでは
    /// 「Animator コンポーネントが付いていて useAnimator が有効か」だけを見る
    /// （クリップ未検出時のフォールバックは Play 内で判定する）。
    /// </summary>
    public bool WillUseAnimator => useAnimator && ResolveAnimator() is { } anim && anim.IsValid;

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

        // 表示開始のたびに 1 回だけクリア効果音を鳴らす（Play は 1 回の表示につき
        // 1 回しか呼ばれない契約のため、ここで呼べば多重再生にはならない）。
        PlayClearSe();

        // Animator 側で隠していた場合（HideImmediate で Visible=false にしたケース）に
        // 備えて、表示の入口では必ず可視へ戻す。
        // gameObject はアクセスのたびに struct を new する読み取り専用プロパティなので、
        // 戻り値へ直接プロパティを書き込めない（CS1612）。ローカル変数に受けてから書く。
        var selfObject = gameObject;
        selfObject.Visible = true;

        ApplyAlpha(AlphaScaleVisible);

        animatorActive = TryStartAnimator();
        if (!animatorActive)
        {
            // Animator を使わない・使えない場合は従来どおりスクリプトで畳んだ状態から始める。
            ApplyRootScale(ScaleZero);
        }
        // animatorActive == true の間は CanvasTransform を Animator に委ねるため、
        // ここでは触らない（Update 側の各段階でも同様にスキップする）。
    }

    /// <summary>
    /// Animator でのクリップ再生を試みる【Animator 演出の唯一の開始点】。
    /// </summary>
    /// <returns>実際に再生を開始できたら true（呼び出し側はスクリプト演出をスキップしてよい）。</returns>
    private bool TryStartAnimator()
    {
        if (!useAnimator) { return false; }

        if (ResolveAnimator() is not { } anim || !anim.IsValid)
        {
            WarnAnimatorFallbackOnce("Animator コンポーネントが見つからないため");
            return false;
        }

        anim.Play(clipName);

        // Play は未登録・未ロードのクリップ名を渡されると警告ログを出すだけで無視する
        // （例外は発生しない）ため、実際に再生が始まったかを IsPlaying / CurrentClip で
        // 確かめてからフォールバックの要否を判定する。
        if (!anim.IsPlaying || anim.CurrentClip != clipName)
        {
            WarnAnimatorFallbackOnce($"クリップ '{clipName}' が見つからないため");
            return false;
        }

        animatorFallbackWarned = false; // 次に失敗したときまた警告できるようにリセット
        return true;
    }

    /// <summary>Animator が使えずスクリプト演出へフォールバックしたことを 1 回だけログに残す。</summary>
    /// <param name="reason">フォールバックした理由（ログの前段に付ける）。</param>
    private void WarnAnimatorFallbackOnce(string reason)
    {
        if (animatorFallbackWarned) { return; }
        animatorFallbackWarned = true;
        SEED.Debug.LogWarning($"[MissionClearBanner] {reason}、従来のスクリプト演出にフォールバックします。");
    }

    /// <summary>アタッチされている Animator コンポーネントを解決する（無ければ null）。</summary>
    private SEED.Animator? ResolveAnimator()
        => gameObject.GetComponent<SEED.Animator>();

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
        // animatorActive の間は CanvasTransform を Animator が書き換えているため、
        // スクリプト側の伸縮（ApplyRootScale）は行わない（二重書き換えの競合防止）。
        if (!animatorActive)
        {
            ApplyRootScale(Easing.OutBack(Easing.Progress01(timer, appearSeconds)));
        }

        if (timer < SEED.Mathf.Max(appearSeconds, 0f)) { return; }

        if (!animatorActive) { ApplyRootScale(ProgressComplete); }
        state = BannerState.Holding;
        timer = 0f;
    }

    /// <summary>一拍置いている最中（この間は自動では閉じない）。</summary>
    private void UpdateHolding()
    {
        // Animator 使用時はクリップ側（loop_mode=loop）がここも動かし続けるので触らない。
        if (!animatorActive) { ApplyRootScale(ProgressComplete); }

        if (timer < SEED.Mathf.Max(holdSeconds, 0f)) { return; }

        // 決定入力は待たず、保持時間が経過したら自動でフェードアウトへ進む。
        state = BannerState.FadingOut;
        timer = 0f;
    }

    /// <summary>
    /// 自動フェードアウト（決定入力は読まない＝早送り不可）。
    /// 帯・文字のアルファだけを 1 → 0 へ下げる。Animator 使用時も拡大率など
    /// 他の見た目は Animator に委ねたままにし、アルファだけスクリプトで下げる
    /// （CanvasTransform の書き換えは行わないため Animator と競合しない）。
    /// </summary>
    private void UpdateFadingOut()
    {
        float progress   = Easing.Progress01(timer, fadeOutSeconds); // 0（開始）→1（下げ切り）
        float alphaScale = ProgressComplete - progress;               // 1 → 0
        ApplyAlpha(alphaScale);

        if (timer < SEED.Mathf.Max(fadeOutSeconds, 0f)) { return; }

        // 端数誤差で α が完全な 0 にならない場合があるため、隠す際に
        // HideImmediate 内の ApplyAlpha(AlphaScaleHidden) で確実に 0 へ揃える。
        HideImmediate();
        finished = true;   // ConsumeFinished で 1 回だけ拾われる
    }

    // ─── 内部処理: 効果音 ───────────────────────────────────

    /// <summary>クリア効果音を鳴らす（辞書キー未設定なら何もしない。音量は辞書の既定値）。</summary>
    private void PlayClearSe()
    {
        if (string.IsNullOrEmpty(clearSeKey)) { return; }
        SEED.Audio.PlayDict(clearSeKey);
    }

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

        if (animatorActive && ResolveAnimator() is { } anim && anim.IsValid)
        {
            // Animator を停止して再生位置を先頭（time=0）へ戻す。次回 Play で
            // 改めて頭から再生されるようにするため。CanvasTransform の値は
            // Stop 後は書き換えられなくなるが、直後に Visible=false で隠すため問題ない。
            anim.Stop();
            var selfObject = gameObject;
            selfObject.Visible = false;
        }
        else
        {
            // フォールバック演出（スクリプト伸縮）を使っていた場合は従来どおり畳んでおく。
            ApplyRootScale(ScaleZero);
        }

        animatorActive = false;
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
