using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 魚が掛かった瞬間に一度だけ流す<b>「HIT!!!」の帯演出</b>。
///
/// <b>付ける場所</b>: キャンバス（FishingUI）の子アクタ「HitBanner」。
/// 見た目を持たない空の Actor2D で構わない（本スクリプトは参照した 5 つの要素を動かすだけ）。
///
/// <b>画づくり</b>
/// 画面を斜め（<see cref="bandAngleDegrees"/> 度）に横切る<b>白い帯</b>を 1 枚置き、
/// その上下を<b>黒いベタ</b>で塗り潰す（帯の外は黒一色になる）。
/// 黒の上側に「Lv◯ 魚名」を左から、黒の下側に「HIT!!!」を右からスライドインさせる。
/// 帯・黒・文字はすべて同じ角度で傾くので、文字も帯に沿って斜めに走る。
///
/// <b>座標系</b>
/// キャンバス座標は <b>Y が下向き</b>（<c>Anchor (0,0)</c> が左上）。よって
/// 帯の長手方向 <c>d = (cosθ, sinθ)</c>、帯の法線 <c>n = (−sinθ, cosθ)</c> は
/// 「n が帯の<b>下</b>側を向く」向きになる。位置はすべてこの 2 ベクトルの線形結合で作るので、
/// 角度を変えても上下の黒・文字の並びは崩れない（シーン側には初期値だけを置く）。
///
/// <b>時間割（すべてインスペクタ指定）</b>
/// <code>
///   0                     帯が開く（Y スケール 0→1・easeOut）        bandAppearSeconds
///   ├─ 文字がスライドイン（画面外 ±textSlideOffsetPx → 定位置）      textSlideSeconds
///   ├─ 静止                                                          holdSeconds
///   └─ 文字が戻り、帯が閉じる                                        exitSeconds
/// </code>
/// 既定値の合計は 1.85 秒で、<see cref="FishingFight"/> の余白（LeadIn・100BPM で 4 拍＝2.4 秒）
/// より短い。＝ やり取りが始まる前に必ず演出が終わる。
///
/// <b>担当範囲</b>
/// 演出の再生だけを担い、いつ再生するかは持たない（<see cref="FishingController"/> が
/// ヒット成立時・わらしべ乗り換え時に <see cref="Play"/> を呼ぶ）。
/// </summary>
public class HitBanner : SEEDScript
{
    // ─── 定数（マジックナンバーを持ち込まないための名前付き） ───────────

    /// <summary>非表示（完全に透明）。</summary>
    private const float AlphaHidden = 0f;

    /// <summary>表示（完全に不透明）。</summary>
    private const float AlphaVisible = 1f;

    /// <summary>0 割りを避けるための下限（秒・ピクセルの両方に使う）。</summary>
    private const float DivideEpsilon = 0.0001f;

    /// <summary>帯が閉じ切った状態の Y スケール。</summary>
    private const float BandScaleClosed = 0f;

    /// <summary>帯が開き切った状態の Y スケール。</summary>
    private const float BandScaleOpen = 1f;

    /// <summary>帯・黒ベタの X スケール（長手方向は伸縮させない）。</summary>
    private const float BandScaleX = 1f;

    /// <summary>帯の厚みの半分を求める係数。</summary>
    private const float HalfFactor = 0.5f;

    /// <summary>白い帯のピボット（中央）。上下へ均等に開く。</summary>
    private static readonly SEED.Vector2 BandWhitePivot = new(0.5f, 0.5f);

    /// <summary>上の黒ベタのピボット（下辺の中央）。ここを基点に上へ伸びる。</summary>
    private static readonly SEED.Vector2 BandBlackTopPivot = new(0.5f, 1f);

    /// <summary>下の黒ベタのピボット（上辺の中央）。ここを基点に下へ伸びる。</summary>
    private static readonly SEED.Vector2 BandBlackBottomPivot = new(0.5f, 0f);

    /// <summary>レベル不明（<see cref="Fish.UnknownLevel"/>）のときに出す代替文字。</summary>
    private const string UnknownLevelLabel = "?";

    // ─── 参照（シーンで割り当てる） ────────────────────────────

    /// <summary>白い帯のスプライト（色と不透明度を書き換える）。</summary>
    [Header("参照"), SerializeField(Label = "白帯のSprite")]
    private SEED.Sprite? bandWhiteSprite = null;

    /// <summary>白い帯の CanvasTransform（位置・角度・Y スケールを書き換える）。</summary>
    [SerializeField(Label = "白帯のCanvasTransform")]
    private SEED.CanvasTransform? bandWhiteTransform = null;

    /// <summary>帯の上側を塗る黒ベタのスプライト。</summary>
    [SerializeField(Label = "上の黒ベタのSprite")]
    private SEED.Sprite? bandBlackTopSprite = null;

    /// <summary>帯の上側を塗る黒ベタの CanvasTransform。</summary>
    [SerializeField(Label = "上の黒ベタのCanvasTransform")]
    private SEED.CanvasTransform? bandBlackTopTransform = null;

    /// <summary>帯の下側を塗る黒ベタのスプライト。</summary>
    [SerializeField(Label = "下の黒ベタのSprite")]
    private SEED.Sprite? bandBlackBottomSprite = null;

    /// <summary>帯の下側を塗る黒ベタの CanvasTransform。</summary>
    [SerializeField(Label = "下の黒ベタのCanvasTransform")]
    private SEED.CanvasTransform? bandBlackBottomTransform = null;

    /// <summary>「Lv◯ 魚名」のテキスト（左からスライドイン）。</summary>
    [SerializeField(Label = "レベル/魚名のText")]
    private SEED.Text? levelLabel = null;

    /// <summary>「Lv◯ 魚名」の CanvasTransform。</summary>
    [SerializeField(Label = "レベル/魚名のCanvasTransform")]
    private SEED.CanvasTransform? levelLabelTransform = null;

    /// <summary>「HIT!!!」のテキスト（右からスライドイン）。</summary>
    [SerializeField(Label = "HITのText")]
    private SEED.Text? hitLabel = null;

    /// <summary>「HIT!!!」の CanvasTransform。</summary>
    [SerializeField(Label = "HITのCanvasTransform")]
    private SEED.CanvasTransform? hitLabelTransform = null;

    // ─── レイアウト ──────────────────────────────────────────

    /// <summary>
    /// 帯の傾き（度）。キャンバスの Y は下向きなので、<b>負の値で右上がり</b>になる。
    /// 帯・黒ベタ・文字のすべてにそのまま適用する。
    /// </summary>
    [Header("レイアウト"), SerializeField(Label = "帯の角度(度)")]
    private float bandAngleDegrees = -12f;

    /// <summary>白い帯の厚み（ピクセル）。黒ベタの位置はこの値の半分だけ法線方向へずらす。</summary>
    [SerializeField(Label = "帯の厚み(px)")]
    private float bandThicknessPx = 420f;

    /// <summary>帯・黒ベタの長さ（ピクセル）。画面幅より十分長くして端を見せない。</summary>
    [SerializeField(Label = "帯の長さ(px)")]
    private float bandLengthPx = 3000f;

    /// <summary>黒ベタが帯の縁から伸びる長さ（ピクセル）。画面外まで届く値にする。</summary>
    [SerializeField(Label = "黒ベタの伸び(px)")]
    private float blackExtentPx = 1400f;

    /// <summary>文字が画面外から入ってくる距離（ピクセル・帯の長手方向）。</summary>
    [SerializeField(Label = "文字のスライド距離(px)")]
    private float textSlideOffsetPx = 1200f;

    /// <summary>「Lv◯ 魚名」の定位置（帯の長手方向・負で左）。</summary>
    [SerializeField(Label = "レベル文字の位置(帯方向px)")]
    private float levelTextAlongPx = -520f;

    /// <summary>「Lv◯ 魚名」の定位置（帯の法線方向・負で帯の上側）。</summary>
    [SerializeField(Label = "レベル文字の位置(法線px)")]
    private float levelTextAcrossPx = -230f;

    /// <summary>「HIT!!!」の定位置（帯の長手方向・正で右）。</summary>
    [SerializeField(Label = "HIT文字の位置(帯方向px)")]
    private float hitTextAlongPx = 520f;

    /// <summary>「HIT!!!」の定位置（帯の法線方向・正で帯の下側）。</summary>
    [SerializeField(Label = "HIT文字の位置(法線px)")]
    private float hitTextAcrossPx = 230f;

    // ─── 時間割 ────────────────────────────────────────────

    /// <summary>帯が開くまでの秒数（Y スケール 0→1・easeOut）。</summary>
    [Header("時間割"), SerializeField(Label = "帯が開く秒数")]
    private float bandAppearSeconds = 0.2f;

    /// <summary>文字がスライドインする秒数（easeOutCubic）。</summary>
    [SerializeField(Label = "文字のスライド秒数")]
    private float textSlideSeconds = 0.35f;

    /// <summary>全部そろった状態で静止する秒数。</summary>
    [SerializeField(Label = "静止秒数")]
    private float holdSeconds = 1f;

    /// <summary>文字が戻り帯が閉じるまでの秒数。</summary>
    [SerializeField(Label = "終わりの秒数")]
    private float exitSeconds = 0.3f;

    // ─── 文字・色 ──────────────────────────────────────────

    /// <summary>
    /// 「Lv◯ 魚名」の書式。<c>{0}</c> がレベル（不明なら
    /// <see cref="UnknownLevelLabel"/>）、<c>{1}</c> が魚の表示名。
    /// </summary>
    [Header("文字・色"), SerializeField(Label = "レベル文字の書式")]
    private string levelTextFormat = "Lv{0} {1}";

    /// <summary>右下に出す固定文字列。</summary>
    [SerializeField(Label = "HITの文言")]
    private string hitText = "HIT!!!";

    /// <summary>白い帯の色（RGB・0〜1）。<see cref="showWhiteBand"/> が false なら描かない。</summary>
    [SerializeField(Label = "帯の色(RGB)")]
    private SEED.Vector3 bandColor = new(1f, 1f, 1f);

    /// <summary>
    /// 帯（黒ベタに挟まれた中央部分）を白で塗るか。false なら中央は透明のままで、
    /// 後ろの海の画面がそのまま見える（既定）。
    /// </summary>
    [SerializeField(Label = "帯を白で塗る")]
    private bool showWhiteBand = false;

    /// <summary>黒ベタの色（RGB・0〜1）。</summary>
    [SerializeField(Label = "黒ベタの色(RGB)")]
    private SEED.Vector3 blackColor = new(0f, 0f, 0f);

    /// <summary>文字の色（RGB・0〜1）。黒ベタの上に載るので既定は白。</summary>
    [SerializeField(Label = "文字の色(RGB)")]
    private SEED.Vector3 textColor = new(1f, 1f, 1f);

    /// <summary>
    /// 文字に使うフォントの assets:// 仮想パス（空文字＝組み込みフォント）。
    /// 「Lv◯ 魚名」「HIT!!!」の両方へ <see cref="OnStart"/> で一度だけ流し込む。
    /// </summary>
    [SerializeField(Label = "文字のフォント(パス)")]
    private string fontPath = "assets://mainGame/fonts/LightNovelPopV2/LightNovelPOPv2.otf";

    /// <summary>文字の縁取りの太さ（キャンバスピクセル・0＝縁取りなし）。</summary>
    [SerializeField(Label = "文字の縁取り(px)")]
    private float outlineWidthPx = 8f;

    /// <summary>文字の縁取りの色（RGB・0〜1）。既定は黒。</summary>
    [SerializeField(Label = "文字の縁取り色(RGB)")]
    private SEED.Vector3 outlineColor = new(0f, 0f, 0f);

    // ─── 実行時の状態 ────────────────────────────────────────

    /// <summary>再生中か（<see cref="Play"/> で true・総尺を過ぎると false）。</summary>
    public bool IsPlaying { get; private set; } = false;

    /// <summary>再生開始からの経過秒数。</summary>
    private float elapsed = 0f;

    // ─── 公開 API ───────────────────────────────────────────

    /// <summary>
    /// 演出を頭から再生する【再生の唯一の入口】。再生中に呼び直すと最初へ巻き戻る
    /// （わらしべで立て続けに乗り換わっても、常に最新の魚が出る）。
    /// </summary>
    /// <param name="level">魚のレベル（<see cref="Fish.UnknownLevel"/>＝0 なら "?" を出す）。</param>
    /// <param name="fishName">魚の表示名。</param>
    public void Play(int level, string fishName)
    {
        string levelPart = level == Fish.UnknownLevel
            ? UnknownLevelLabel
            : level.ToString();

        SetTextContent(levelLabel, string.Format(levelTextFormat, levelPart, fishName));
        SetTextContent(hitLabel, hitText);

        elapsed = 0f;
        IsPlaying = true;
        ApplyFrame();               // 1 フレーム目の絵をその場で作る（1 フレームの点滅を防ぐ）
    }

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>初期状態（全要素を透明にして畳んでおく）。</summary>
    public override void OnStart()
    {
        ApplyPivots();
        ApplyTextStyle();
        ApplyStaticLayout();
        Hide();
    }

    /// <summary>再生中だけ時間を進めて 1 フレーム分の絵を作る。</summary>
    /// <param name="ctx">フレーム情報（デルタタイム）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (!IsPlaying) { return; }

        elapsed += ctx.DeltaTime;
        if (elapsed >= TotalSeconds)
        {
            IsPlaying = false;
            Hide();
            return;
        }

        ApplyFrame();
    }

    // ─── 内部: 時間割 ────────────────────────────────────────

    /// <summary>演出の総尺（秒）。各区間の合計。</summary>
    private float TotalSeconds
        => SEED.Mathf.Max(bandAppearSeconds, 0f)
         + SEED.Mathf.Max(textSlideSeconds, 0f)
         + SEED.Mathf.Max(holdSeconds, 0f)
         + SEED.Mathf.Max(exitSeconds, 0f);

    /// <summary>終わりの区間が始まる時刻（秒）。</summary>
    private float ExitStartSeconds => TotalSeconds - SEED.Mathf.Max(exitSeconds, 0f);

    /// <summary>
    /// 帯の開き具合（0＝閉じ切り／1＝開き切り）。
    /// 開く区間は easeOut で開き、終わりの区間で easeOut のまま閉じる。
    /// </summary>
    private float BandOpen01()
    {
        float appear = SEED.Mathf.Max(bandAppearSeconds, 0f);

        if (elapsed < appear)
        {
            return EaseOutCubic(Progress01(elapsed, appear));
        }
        if (elapsed < ExitStartSeconds)
        {
            return BandScaleOpen;
        }
        float exitT = Progress01(elapsed - ExitStartSeconds, SEED.Mathf.Max(exitSeconds, 0f));
        // 退場は出現の逆再生: 帯は中央線へ向かって閉じる（EaseIn＝ゆっくり始まり加速して消える）
        return BandScaleOpen - EaseInCubic(exitT);
    }

    /// <summary>
    /// 文字の入り具合（0＝画面外／1＝定位置）。
    /// 帯が開き切ってからスライドインし、終わりの区間で同じ道を戻る。
    /// </summary>
    private float TextIn01()
    {
        float appear = SEED.Mathf.Max(bandAppearSeconds, 0f);
        float slide = SEED.Mathf.Max(textSlideSeconds, 0f);

        if (elapsed < appear) { return 0f; }
        if (elapsed < appear + slide)
        {
            return EaseOutCubic(Progress01(elapsed - appear, slide));
        }
        if (elapsed < ExitStartSeconds) { return 1f; }

        float exitT = Progress01(elapsed - ExitStartSeconds, SEED.Mathf.Max(exitSeconds, 0f));
        // 退場は出現の逆再生: 文字は入ってきた側（左上は左へ・右下は右へ）へ EaseIn で戻る
        return 1f - EaseInCubic(exitT);
    }

    /// <summary>区間内の進行度（0〜1）。長さが 0 以下なら即 1（区間を飛ばす）。</summary>
    /// <param name="value">区間頭からの経過。</param>
    /// <param name="length">区間の長さ。</param>
    private static float Progress01(float value, float length)
        => length <= DivideEpsilon ? 1f : SEED.Mathf.Clamped01(value / length);

    /// <summary>easeOutCubic（＝ 1 −(1 − t)³）。終わりへ向かってなめらかに減速する。</summary>
    /// <param name="t">進行度（0〜1）。</param>
    /// <summary>EaseInCubic: t^3。退場（出現の逆再生）用。ゆっくり動き出して加速する。</summary>
    private static float EaseInCubic(float t)
    {
        float c = SEED.Mathf.Clamped01(t);
        return c * c * c;
    }

    private static float EaseOutCubic(float t)
    {
        float inv = 1f - SEED.Mathf.Clamped01(t);
        return 1f - inv * inv * inv;
    }

    // ─── 内部: 描画の反映 ────────────────────────────────────

    /// <summary>
    /// いまの <see cref="elapsed"/> に対応する 1 フレーム分の配置と色を作る
    /// 【演出の見た目を決める唯一の場所】。
    /// </summary>
    private void ApplyFrame()
    {
        float rad = bandAngleDegrees * SEED.Mathf.Deg2Rad;
        // 帯の長手方向 d と法線 n（キャンバスの Y は下向きなので n は帯の下側を向く）
        SEED.Vector2 along = new(SEED.Mathf.Cos(rad), SEED.Mathf.Sin(rad));
        SEED.Vector2 across = new(-SEED.Mathf.Sin(rad), SEED.Mathf.Cos(rad));

        float open = BandOpen01();
        float halfThickness = bandThicknessPx * HalfFactor * open;

        ApplyStaticLayout();

        // 白帯は中心に置いたまま Y だけ伸縮させる
        ApplyBandPart(bandWhiteTransform, SEED.Vector2.Zero, open);
        // 黒ベタは「開いた帯の縁」に貼り付く（縁の位置も open に比例して動く）
        ApplyBandPart(bandBlackTopTransform, across * -halfThickness, open);
        ApplyBandPart(bandBlackBottomTransform, across * halfThickness, open);

        // 文字はスライド方向（帯の長手方向）だけ動かす。定位置は帯の座標系で指定する。
        float slide = (1f - TextIn01()) * textSlideOffsetPx;
        ApplyTextPart(
            levelLabelTransform,
            along * (levelTextAlongPx - slide) + across * levelTextAcrossPx);
        ApplyTextPart(
            hitLabelTransform,
            along * (hitTextAlongPx + slide) + across * hitTextAcrossPx);

        // 色（再生中は不透明）
        // 帯の白塗りは任意（既定 OFF＝中央は透明で海が見える）
        SetSpriteColor(bandWhiteSprite, bandColor, showWhiteBand ? AlphaVisible : AlphaHidden);
        SetSpriteColor(bandBlackTopSprite, blackColor, AlphaVisible);
        SetSpriteColor(bandBlackBottomSprite, blackColor, AlphaVisible);
        SetTextColor(levelLabel, textColor, AlphaVisible);
        SetTextColor(hitLabel, textColor, AlphaVisible);
    }

    /// <summary>
    /// 角度とサイズのように「時間で変わらない」設定を流し込む。
    /// インスペクタでの値いじりが即座に効くよう毎フレーム呼ぶ（代入だけなので安い）。
    /// </summary>
    private void ApplyStaticLayout()
    {
        SetRotation(bandWhiteTransform);
        SetRotation(bandBlackTopTransform);
        SetRotation(bandBlackBottomTransform);
        SetRotation(levelLabelTransform);
        SetRotation(hitLabelTransform);

        SetSpriteSize(bandWhiteSprite, bandLengthPx, bandThicknessPx);
        SetSpriteSize(bandBlackTopSprite, bandLengthPx, blackExtentPx);
        SetSpriteSize(bandBlackBottomSprite, bandLengthPx, blackExtentPx);
    }

    /// <summary>ピボットを既定値へ揃える（帯が縁から伸びる向きはピボットで決まる）。</summary>
    private void ApplyPivots()
    {
        SetPivot(bandWhiteTransform, BandWhitePivot);
        SetPivot(bandBlackTopTransform, BandBlackTopPivot);
        SetPivot(bandBlackBottomTransform, BandBlackBottomPivot);
    }

    /// <summary>
    /// 文字の書式（フォント・縁取り）を両方の Text へ流し込む。
    ///
    /// 位置や色と違い毎フレーム変わらない設定なので <see cref="OnStart"/> で一度だけ呼ぶ
    /// （フォントパスの代入は文字列の受け渡しを伴うため、毎フレーム撃つ意味がない）。
    /// </summary>
    private void ApplyTextStyle()
    {
        SetTextStyle(levelLabel);
        SetTextStyle(hitLabel);
    }

    /// <summary>1 つの Text へフォント・縁取りの太さ・縁取り色を設定する。</summary>
    /// <param name="text">対象（未設定可）。</param>
    private void SetTextStyle(SEED.Text? text)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.FontPath      = fontPath;
        t.OutlineWidth  = outlineWidthPx;
        t.OutlineColor  = ToColor(outlineColor, AlphaVisible);
    }

    /// <summary>全要素を透明にして帯を閉じる（待機状態）。</summary>
    private void Hide()
    {
        SetSpriteColor(bandWhiteSprite, bandColor, AlphaHidden);
        SetSpriteColor(bandBlackTopSprite, blackColor, AlphaHidden);
        SetSpriteColor(bandBlackBottomSprite, blackColor, AlphaHidden);
        SetTextColor(levelLabel, textColor, AlphaHidden);
        SetTextColor(hitLabel, textColor, AlphaHidden);

        ApplyBandPart(bandWhiteTransform, SEED.Vector2.Zero, BandScaleClosed);
        ApplyBandPart(bandBlackTopTransform, SEED.Vector2.Zero, BandScaleClosed);
        ApplyBandPart(bandBlackBottomTransform, SEED.Vector2.Zero, BandScaleClosed);
    }

    // ─── 内部: 小さな代入ヘルパ ──────────────────────────────

    /// <summary>帯の 1 枚（白・黒どちらも）へ位置と Y スケールを流し込む。</summary>
    /// <param name="transform">対象（未設定可）。</param>
    /// <param name="position">キャンバス座標（アンカー中央基準）。</param>
    /// <param name="openY">Y スケール（0＝閉じ切り）。</param>
    private static void ApplyBandPart(SEED.CanvasTransform? transform, SEED.Vector2 position, float openY)
    {
        if (transform is not { } tf || !tf.IsValid) { return; }
        tf.Position = position;
        tf.Scale = new SEED.Vector2(BandScaleX, openY);
    }

    /// <summary>文字の 1 つへ位置を流し込む（文字は伸縮させない）。</summary>
    /// <param name="transform">対象（未設定可）。</param>
    /// <param name="position">キャンバス座標（アンカー中央基準）。</param>
    private static void ApplyTextPart(SEED.CanvasTransform? transform, SEED.Vector2 position)
    {
        if (transform is not { } tf || !tf.IsValid) { return; }
        tf.Position = position;
    }

    /// <summary>CanvasTransform の回転を帯の角度へ揃える。</summary>
    /// <param name="transform">対象（未設定可）。</param>
    private void SetRotation(SEED.CanvasTransform? transform)
    {
        if (transform is not { } tf || !tf.IsValid) { return; }
        tf.Rotation = bandAngleDegrees;
    }

    /// <summary>CanvasTransform のピボットを設定する。</summary>
    /// <param name="transform">対象（未設定可）。</param>
    /// <param name="pivot">正規化ピボット。</param>
    private static void SetPivot(SEED.CanvasTransform? transform, SEED.Vector2 pivot)
    {
        if (transform is not { } tf || !tf.IsValid) { return; }
        tf.Pivot = pivot;
    }

    /// <summary>スプライトのサイズを設定する。</summary>
    /// <param name="sprite">対象（未設定可）。</param>
    /// <param name="width">幅（px）。</param>
    /// <param name="height">高さ（px）。</param>
    private static void SetSpriteSize(SEED.Sprite? sprite, float width, float height)
    {
        if (sprite is not { } s || !s.IsValid) { return; }
        s.Size = new SEED.Vector2(width, height);
    }

    /// <summary>スプライトの色を RGB＋不透明度で設定する。</summary>
    /// <param name="sprite">対象（未設定可）。</param>
    /// <param name="rgb">RGB（0〜1）。</param>
    /// <param name="alpha">不透明度（0〜1）。</param>
    private static void SetSpriteColor(SEED.Sprite? sprite, SEED.Vector3 rgb, float alpha)
    {
        if (sprite is not { } s || !s.IsValid) { return; }
        s.Color = ToColor(rgb, alpha);
    }

    /// <summary>テキストの色を RGB＋不透明度で設定する。</summary>
    /// <param name="text">対象（未設定可）。</param>
    /// <param name="rgb">RGB（0〜1）。</param>
    /// <param name="alpha">不透明度（0〜1）。</param>
    private static void SetTextColor(SEED.Text? text, SEED.Vector3 rgb, float alpha)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Color = ToColor(rgb, alpha);
    }

    /// <summary>テキストの文字列を差し替える（色は触らない）。</summary>
    /// <param name="text">対象（未設定可）。</param>
    /// <param name="content">表示する文字列。</param>
    private static void SetTextContent(SEED.Text? text, string content)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Content = content;
    }

    /// <summary>RGB の Vector3 と不透明度から <see cref="SEED.Color"/> を作る。</summary>
    /// <param name="rgb">RGB（0〜1）。</param>
    /// <param name="alpha">不透明度（0〜1）。</param>
    private static SEED.Color ToColor(SEED.Vector3 rgb, float alpha)
        => new SEED.Color(rgb.x, rgb.y, rgb.z, SEED.Mathf.Clamped01(alpha));
}
