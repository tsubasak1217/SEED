// ============================================================================
//  TutorialWindow.cs
//  チュートリアル説明窓（ミニキャラ・吹き出し・本文・送りマーク）の「表示」だけを担当する。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// チュートリアル説明窓の表示担当コンポーネント。説明窓アクタ（Actor2D）のルートに付ける。
///
/// 【責務】
///  1. 窓全体の表示／非表示（ShowAt / ShowAboveTarget / Hide）
///  2. 表示位置とサイズの適用（画面固定・ワールド対象への追従）
///  3. 本文の 1 文字ずつの表示（SetText / CompleteText / IsTextComplete）
///  4. 送りマークの点滅（本文を出し切っている間だけ）
///
/// 「次に何を説明するか」「入力で送るか」といった進行判断は持たない。
/// それは <see cref="TutorialDirector"/> の責務（単一責任）。
///
/// 【時間軸】
/// チュートリアルはゲーム時間を止めた（Time.Scale = 0）状態でも動く必要があるため、
/// 文字送りも点滅も追従も<b>すべて Time.UnscaledDeltaTime</b> で進める。
///
/// 【表示切替の実装方針】
/// アクター単位の表示 ON/OFF を行うスクリプト API は無いので、
/// 参照している Sprite / Text のアルファを 0 にして隠す（DialogueWindow と同じ方式）。
/// 元の色はシーンで設定された値を初回に控え、表示時にそれへ戻す。
///
/// 【シーン側の設定】
/// 参照フィールドには同じ説明窓アクタ配下の子を「子名|スロット名」で指定する
/// （例: TutorialBodyText|Text）。
/// </summary>
public class TutorialWindow : SEEDScript
{
    // ─── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>完全に透明にするアルファ倍率。</summary>
    private const float AlphaScaleHidden = 0f;

    /// <summary>元の色をそのまま使うアルファ倍率。</summary>
    private const float AlphaScaleVisible = 1f;

    /// <summary>1 文字あたりの表示間隔（秒）の既定値。</summary>
    private const float DefaultCharInterval = 0.03f;

    /// <summary>送りマークの点滅周期（秒）の既定値。</summary>
    private const float DefaultArrowBlinkPeriod = 0.8f;

    /// <summary>点滅周期がこれ以下なら点滅させない（0 除算回避）。</summary>
    private const float MinBlinkPeriod = 0.0001f;

    /// <summary>送りマークの点滅で取り得るアルファ倍率の下限。</summary>
    private const float ArrowBlinkMin = 0.15f;

    /// <summary>送りマークの点滅で取り得るアルファ倍率の上限。</summary>
    private const float ArrowBlinkMax = 1f;

    /// <summary>点滅周期を往路の長さへ直す係数（1 周期 = 往復）。</summary>
    private const float BlinkHalfPeriodRatio = 0.5f;

    /// <summary>サイズ倍率が 0 以下のときに使う既定倍率。</summary>
    private const float DefaultScale = 1f;

    /// <summary>キャンバスの既定の幅（px）。画面外クランプの基準に使う。</summary>
    private const float DefaultCanvasWidth = 1280f;

    /// <summary>キャンバスの既定の高さ（px）。画面外クランプの基準に使う。</summary>
    private const float DefaultCanvasHeight = 720f;

    /// <summary>キャンバス半分を求める係数（中央原点なので幅・高さの半分が端になる）。</summary>
    private const float CanvasHalf = 0.5f;

    /// <summary>カメラ前方距離がこの値以下なら「カメラの背後」とみなす。</summary>
    private const float BehindCameraDepth = 0f;

    /// <summary>カメラ背後のとき、方向を保ったまま画面外へ押し出す倍率（クランプで端に張り付く）。</summary>
    private const float BehindPushFactor = 1000f;

    /// <summary>空文字（未設定）。</summary>
    private const string EmptyText = "";

    // ─── インスペクタ公開フィールド（参照）───────────────────

    /// <summary>ミニキャラのスプライト（素材が来るまでは白テクスチャを着色した矩形）。</summary>
    [Header("参照"), SerializeField(Label = "ミニキャラ", Tooltip = "ミニキャラの Sprite（子名|Sprite）")]
    public SEED.Sprite? charaSprite;

    /// <summary>ミニ吹き出しのスプライト。</summary>
    [SerializeField(Label = "吹き出し", Tooltip = "吹き出しの Sprite（子名|Sprite）")]
    public SEED.Sprite? balloonSprite;

    /// <summary>説明本文の Text（枠あり・自動折り返し）。</summary>
    [SerializeField(Label = "本文テキスト", Tooltip = "説明本文の Text（子名|Text）")]
    public SEED.Text? bodyText;

    /// <summary>送りマーク（本文を出し切ったときだけ点滅表示する）。</summary>
    [SerializeField(Label = "送りマーク", Tooltip = "本文を出し切ったときに点滅する Sprite（子名|Sprite）")]
    public SEED.Sprite? nextArrowSprite;

    /// <summary>ワールド座標を画面へ射影するカメラ（AboveTarget の追従に使う）。</summary>
    [SerializeField(Label = "射影カメラ", Tooltip = "対象アクタの追従に使うカメラ（MainCamera）")]
    public SEED.Camera? projectionCamera;

    // ─── インスペクタ公開フィールド（値）─────────────────────

    /// <summary>1 文字あたりの表示間隔（秒）。0 以下なら即座に全文表示する。</summary>
    [Header("文字送り"), SerializeField(Label = "文字送り間隔(秒)", Tooltip = "1 文字を表示する間隔。0 で即時全文表示")]
    public float charInterval = DefaultCharInterval;

    /// <summary>送りマークの点滅周期（秒）。</summary>
    [SerializeField(Label = "送りマーク点滅周期(秒)", Tooltip = "送りマークが 1 往復するのに掛かる秒数")]
    public float arrowBlinkPeriod = DefaultArrowBlinkPeriod;

    /// <summary>本文に使うフォント（.ttf / .otf）。空なら Text 側の設定をそのまま使う。</summary>
    [SerializeField(Label = "フォント", Tooltip = "本文に使うフォント。空なら Text の設定のまま")]
    [AssetReference("ttf", "otf")]
    public string fontPath = EmptyText;

    /// <summary>本文のインライン画像に使うアイコンセット（.icons）。空なら [icon:...] は空白になる。</summary>
    [SerializeField(Label = "アイコンセット", Tooltip = "[icon:名前] を解決する .icons ファイル")]
    [AssetReference("icons")]
    public string iconSetPath = EmptyText;

    /// <summary>本文の枠幅（px）。0 より大きいと自動折り返しとピボットが有効になる。</summary>
    [Header("本文の枠"), SerializeField(Label = "枠の幅(px)", Tooltip = "本文の折り返し幅。0 なら折り返さない")]
    public float boxWidth = 420f;

    /// <summary>本文の枠の最小高さ（px）。内容が増えれば下へ伸びる。</summary>
    [SerializeField(Label = "枠の最小高さ(px)", Tooltip = "本文の枠の最小高さ")]
    public float boxHeight = 120f;

    /// <summary>画面外クランプに使うキャンバスの幅（px）。</summary>
    [Header("画面外クランプ"), SerializeField(Label = "キャンバス幅(px)", Tooltip = "追従時の画面内クランプに使う幅")]
    public float canvasWidth = DefaultCanvasWidth;

    /// <summary>画面外クランプに使うキャンバスの高さ（px）。</summary>
    [SerializeField(Label = "キャンバス高さ(px)", Tooltip = "追従時の画面内クランプに使う高さ")]
    public float canvasHeight = DefaultCanvasHeight;

    /// <summary>画面端から内側へ確保する余白（px）。窓が画面外へはみ出さないようにする。</summary>
    [SerializeField(Label = "画面端の余白(px)", Tooltip = "追従時に画面端から内側へ確保する余白")]
    public float screenMargin = 200f;

    /// <summary>追従時に対象の何メートル上を狙うか（ワールド単位）。</summary>
    [Header("追従"), SerializeField(Label = "対象の上のオフセット(m)", Tooltip = "追従時に対象の何メートル上へ出すか")]
    public float aboveTargetWorldOffsetY = 1.5f;

    /// <summary>追従時にさらに画面上でどれだけ持ち上げるか（px・負で上）。</summary>
    [SerializeField(Label = "追従の画面オフセットY(px)", Tooltip = "追従時に画面上でさらにずらす量（負で上）")]
    public float aboveTargetCanvasOffsetY = -80f;

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>シーンで設定された元の色を控えたか（初回アクセス時に一度だけ行う）。</summary>
    private bool baseColorsCaptured;

    /// <summary>ミニキャラの元の色。</summary>
    private SEED.Color charaBaseColor;

    /// <summary>吹き出しの元の色。</summary>
    private SEED.Color balloonBaseColor;

    /// <summary>本文テキストの元の色。</summary>
    private SEED.Color bodyBaseColor;

    /// <summary>送りマークの元の色。</summary>
    private SEED.Color arrowBaseColor;

    /// <summary>窓を表示中か。</summary>
    private bool visible;

    /// <summary>現在の配置モード（表示中のみ意味を持つ）。</summary>
    private TutorialAnchorMode anchorMode = TutorialAnchorMode.ScreenFixed;

    /// <summary>画面固定時の表示位置（キャンバス座標）。</summary>
    private SEED.Vector2 fixedPosition = SEED.Vector2.Zero;

    /// <summary>追従対象（配置モードが AboveTarget のときだけ使う）。</summary>
    private SEED.Transform followTarget;

    /// <summary>文字送りの進行（インライン画像記法を 1 文字として扱う共通ヘルパー）。</summary>
    private readonly TypewriterText writer = new();

    /// <summary>点滅用の経過時間（秒・実時間）。</summary>
    private float blinkTimer;

    // ─── 公開プロパティ ──────────────────────────────────────

    /// <summary>本文をすべて表示し終えているか（送り待ちの状態か）。</summary>
    public bool IsTextComplete => writer.IsComplete;

    /// <summary>窓を表示中か。</summary>
    public bool IsVisible => visible;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 初期化。元の色を控え、フォント・アイコンセット・本文の枠を適用してから隠す。
    /// </summary>
    public override void OnStart()
    {
        CaptureBaseColors();
        ApplyTextStyle();
        Hide();
    }

    /// <summary>
    /// 毎フレーム、追従・文字送り・送りマークの点滅を進める。
    /// ゲーム時間が止まっていても動く必要があるので、必ず実時間で進める。
    /// </summary>
    /// <param name="ctx">フレーム情報（ここでは使わず Time.UnscaledDeltaTime を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 非表示中は何も進めない（隠したまま文字送りが進むのを防ぐ）
        if (!visible) { return; }

        float unscaledDelta = SEED.Time.UnscaledDeltaTime;

        UpdateFollow();
        if (writer.Advance(unscaledDelta, charInterval)) { ApplyBodyContent(); }
        UpdateArrow(unscaledDelta);
    }

    // ─── 公開メソッド: 表示・非表示 ─────────────────────────

    /// <summary>
    /// 画面上の固定位置に窓を表示する。
    /// </summary>
    /// <param name="canvasPosition">表示位置（キャンバス座標。画面中央が原点・Y 下向き）。</param>
    /// <param name="scale">窓全体の拡大率（0 以下なら 1 倍）。</param>
    public void ShowAt(SEED.Vector2 canvasPosition, float scale)
    {
        anchorMode    = TutorialAnchorMode.ScreenFixed;
        fixedPosition = canvasPosition;
        followTarget  = default;

        ApplyScale(scale);
        ApplyPosition(canvasPosition);
        Show();
    }

    /// <summary>
    /// 指定アクタの少し上へ追従する形で窓を表示する。
    ///
    /// 位置は毎フレーム <c>Camera.WorldToCanvas</c> で引き直す。
    /// 対象がカメラの背後にある場合は方向を保ったまま画面端へクランプする
    /// （背後の点は射影結果の X / Y が意味を持たないため、符号を反転して押し出す）。
    /// </summary>
    /// <param name="target">追従する対象の Transform。無効なら画面中央固定へフォールバックする。</param>
    /// <param name="scale">窓全体の拡大率（0 以下なら 1 倍）。</param>
    public void ShowAboveTarget(SEED.Transform target, float scale)
    {
        if (!target.IsValid)
        {
            SEED.Debug.LogWarning("[TutorialWindow] 追従対象が未設定・無効です。画面中央に表示します。");
            ShowAt(SEED.Vector2.Zero, scale);
            return;
        }

        anchorMode   = TutorialAnchorMode.AboveTarget;
        followTarget = target;

        ApplyScale(scale);
        UpdateFollow();       // 表示した最初のフレームから正しい位置に出す
        Show();
    }

    /// <summary>
    /// 表示する本文を差し替え、文字送りを最初からやり直す。
    /// </summary>
    /// <param name="text">説明文（インライン画像記法を含んでよい）。</param>
    public void SetText(string text)
    {
        CaptureBaseColors();
        writer.Begin(text);
        ApplyBodyContent();
        blinkTimer = 0f;
    }

    /// <summary>本文を最後まで一気に表示する（送り入力による全文表示に使う）。</summary>
    public void CompleteText()
    {
        if (writer.Complete()) { ApplyBodyContent(); }
    }

    /// <summary>窓を隠す（各パーツのアルファを 0 にする）。</summary>
    public void Hide()
    {
        CaptureBaseColors();
        visible = false;
        ApplyVisibility();
    }

    // ─── 内部処理: 表示状態 ─────────────────────────────────

    /// <summary>窓を表示する（各パーツの色を元の値へ戻す）。</summary>
    private void Show()
    {
        CaptureBaseColors();
        visible = true;
        ApplyVisibility();
    }

    /// <summary>
    /// シーンで設定された各パーツの色を初回だけ控える。
    ///
    /// OnStart だけでは足りない。参照元（TutorialDirector）の処理が自分の OnStart より
    /// 先に走って Hide() を呼ぶことがあり得るため、公開 API の入口でも呼んで
    /// 「必ず控えてから触る」ようにしている（DialogueWindow と同じ理由）。
    /// </summary>
    private void CaptureBaseColors()
    {
        if (baseColorsCaptured) { return; }
        baseColorsCaptured = true;

        if (charaSprite     is { } chara   && chara.IsValid)   { charaBaseColor   = chara.Color; }
        if (balloonSprite   is { } balloon && balloon.IsValid) { balloonBaseColor = balloon.Color; }
        if (bodyText        is { } body    && body.IsValid)    { bodyBaseColor    = body.Color; }
        if (nextArrowSprite is { } arrow   && arrow.IsValid)   { arrowBaseColor   = arrow.Color; }
    }

    /// <summary>
    /// 現在の表示状態を各パーツの色へ反映する。
    /// 送りマークは「本文完了時のみ表示」なのでここでは必ず消し、
    /// <see cref="UpdateArrow"/> が毎フレーム上書きする。
    /// </summary>
    private void ApplyVisibility()
    {
        float alphaScale = visible ? AlphaScaleVisible : AlphaScaleHidden;

        if (charaSprite     is { } chara   && chara.IsValid)
        {
            chara.Color = charaBaseColor.WithAlpha(charaBaseColor.a * alphaScale);
        }
        if (balloonSprite   is { } balloon && balloon.IsValid)
        {
            balloon.Color = balloonBaseColor.WithAlpha(balloonBaseColor.a * alphaScale);
        }
        if (bodyText        is { } body    && body.IsValid)
        {
            body.Color = bodyBaseColor.WithAlpha(bodyBaseColor.a * alphaScale);
        }
        if (nextArrowSprite is { } arrow   && arrow.IsValid)
        {
            arrow.Color = arrowBaseColor.WithAlpha(arrowBaseColor.a * AlphaScaleHidden);
        }
    }

    /// <summary>
    /// インスペクタで指定したフォント・アイコンセット・枠設定を本文 Text へ適用する。
    /// 未設定（空文字）の項目は Text 側のシーン設定を尊重して触らない。
    /// </summary>
    private void ApplyTextStyle()
    {
        if (bodyText is not { } body || !body.IsValid) { return; }

        if (!string.IsNullOrEmpty(fontPath))    { body.FontPath = fontPath; }
        if (!string.IsNullOrEmpty(iconSetPath)) { body.IconSet  = iconSetPath; }

        // 枠幅が正のときだけ「枠あり」レイアウト（自動折り返し・ピボット有効）にする
        if (boxWidth > 0f)
        {
            body.BoxWidth  = boxWidth;
            body.BoxHeight = SEED.Mathf.Max(boxHeight, 0f);
            body.Wrap      = true;
        }
    }

    // ─── 内部処理: 位置とサイズ ─────────────────────────────

    /// <summary>
    /// 窓全体の拡大率をルートの CanvasTransform へ適用する。
    /// </summary>
    /// <param name="scale">拡大率（0 以下なら 1 倍）。</param>
    private void ApplyScale(float scale)
    {
        if (gameObject.GetComponent<SEED.CanvasTransform>() is not { } ct || !ct.IsValid) { return; }

        float applied = scale > 0f ? scale : DefaultScale;
        ct.Scale = new SEED.Vector2(applied, applied);
    }

    /// <summary>
    /// 窓の表示位置をルートの CanvasTransform へ適用する。
    /// </summary>
    /// <param name="canvasPosition">キャンバス座標（画面中央が原点・Y 下向き）。</param>
    private void ApplyPosition(SEED.Vector2 canvasPosition)
    {
        if (gameObject.GetComponent<SEED.CanvasTransform>() is not { } ct || !ct.IsValid) { return; }
        ct.Position = canvasPosition;
    }

    /// <summary>
    /// 追従モードのとき、対象のワールド位置を画面へ射影して窓を置き直す。
    /// 画面固定モードでは何もしない（ShowAt で置いた位置のまま）。
    /// </summary>
    private void UpdateFollow()
    {
        if (anchorMode != TutorialAnchorMode.AboveTarget) { return; }
        if (!followTarget.IsValid) { return; }
        if (projectionCamera is not { } cam || !cam.IsValid) { return; }

        // 対象の少し上のワールド点を射影する
        var worldPoint = followTarget.Position + SEED.Vector3.Up * aboveTargetWorldOffsetY;
        var screen     = cam.WorldToScreen(worldPoint);
        var canvasPos  = cam.WorldToCanvas(worldPoint);

        // カメラ背後の点は X / Y が意味を持たないので、方向を反転して大きく押し出し、
        // このあとのクランプで「対象が居る側の画面端」へ張り付かせる。
        if (screen.z <= BehindCameraDepth)
        {
            canvasPos = new SEED.Vector2(-canvasPos.x * BehindPushFactor, -canvasPos.y * BehindPushFactor);
        }

        // 画面上でさらに持ち上げてから、画面内へクランプする
        var lifted = new SEED.Vector2(canvasPos.x, canvasPos.y + aboveTargetCanvasOffsetY);
        ApplyPosition(ClampToCanvas(lifted));
    }

    /// <summary>
    /// キャンバス座標を画面内（余白ぶん内側）へクランプする。
    /// </summary>
    /// <param name="position">クランプ前のキャンバス座標。</param>
    /// <returns>画面内に収めたキャンバス座標。</returns>
    private SEED.Vector2 ClampToCanvas(SEED.Vector2 position)
    {
        float margin  = SEED.Mathf.Max(screenMargin, 0f);
        float halfX   = SEED.Mathf.Max(canvasWidth  * CanvasHalf - margin, 0f);
        float halfY   = SEED.Mathf.Max(canvasHeight * CanvasHalf - margin, 0f);

        return new SEED.Vector2(
            SEED.Mathf.Clamped(position.x, -halfX, halfX),
            SEED.Mathf.Clamped(position.y, -halfY, halfY));
    }

    // ─── 内部処理: 本文と送りマーク ─────────────────────────

    /// <summary>現在の表示ぶんを本文 Text へ反映する。</summary>
    private void ApplyBodyContent()
    {
        if (bodyText is { } body && body.IsValid) { body.Content = writer.Shown; }
    }

    /// <summary>
    /// 送りマークの表示を更新する（本文完了時のみ点滅表示）。
    /// </summary>
    /// <param name="unscaledDelta">前フレームからの経過秒（実時間）。</param>
    private void UpdateArrow(float unscaledDelta)
    {
        if (nextArrowSprite is not { } arrow || !arrow.IsValid) { return; }

        // 本文がまだ流れている間は出さない（もう送れると誤解させないため）
        if (!writer.IsComplete)
        {
            blinkTimer  = 0f;
            arrow.Color = arrowBaseColor.WithAlpha(arrowBaseColor.a * AlphaScaleHidden);
            return;
        }

        blinkTimer += unscaledDelta;

        // 周期が実質 0 なら点滅させず出しっぱなしにする
        float blink = ArrowBlinkMax;
        if (arrowBlinkPeriod > MinBlinkPeriod)
        {
            float half = arrowBlinkPeriod * BlinkHalfPeriodRatio;
            float wave = SEED.Mathf.PingPong(blinkTimer, half) / half;
            blink = SEED.Mathf.Lerp(ArrowBlinkMin, ArrowBlinkMax, wave);
        }

        arrow.Color = arrowBaseColor.WithAlpha(arrowBaseColor.a * blink);
    }
}
