using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 魚が掛かった瞬間に一度だけ流す<b>「HIT!!!」の帯演出</b>。
///
/// <b>付ける場所</b>: キャンバス（FishingUI）の子アクタ「HitBanner」。
/// 見た目を持たない空の Actor2D で構わない（本スクリプトは参照した要素を動かすだけ）。
///
/// <b>画づくり</b>
/// 画面を斜め（<see cref="bandAngleDegrees"/> 度）に横切る<b>黒い帯（バー）を 2 本</b>、
/// 帯の中心線から法線方向へ ±<see cref="barOffsetPx"/> だけ離して平行に置く。
/// 上のバーには「Lv◯ 魚名」を、下のバーには「HIT!!!」を重ねる。
/// バーは<b>法線方向</b>（画面外→定位置）へ、文字は<b>帯の長手方向</b>へ走るので、
/// 「上下から挟み込んで、文字が左右から差し込まれる」動きになる。
/// 白帯は使わない（旧演出の名残。シーンの割り当てを壊さないよう参照だけ残し、毎フレーム透明にする）。
///
/// <b>座標系</b>
/// キャンバス座標は <b>Y が下向き</b>（<c>Anchor (0,0)</c> が左上）。よって
/// 帯の長手方向 <c>d = (cosθ, sinθ)</c>、帯の法線 <c>n = (−sinθ, cosθ)</c> は
/// 「d が右（θ が負なら右上がり）」「n が帯の<b>下</b>側」を向く。
/// 位置はすべてこの 2 ベクトルの線形結合で作るので、角度を変えても上下関係は崩れない。
///
/// <b>θ = −12°・既定値での実座標（符号確認用）</b>
/// <code>
///   d = ( 0.9781, −0.2079)  … 右へ・少し上へ（右上がり）
///   n = ( 0.2079,  0.9781)  … 右へ・下へ（＝画面の「下」側）
///
///   上バー   = −n × 280            = (− 58.22, −273.88)   … 画面の上（やや左）
///   下バー   = +n × 280            = (  58.22,  273.88)   … 画面の下（やや右）
///   上の文字 = −n×280 + d×(−200)   = (−253.84, −232.30)   … 上バーの上を左寄りに
///   下の文字 = +n×280 + d×(+200)   = ( 253.84,  232.30)   … 下バーの上を右寄りに
/// </code>
/// （シーンに置いてある初期値と一致する。＝ 上バーは必ず n の負側＝画面上側になる）
///
/// <b>時間割（すべてインスペクタ指定）</b>
/// <code>
///   0             上下のバーが法線方向にスライドイン（easeOutCubic）    barSlideSeconds
///   textDelaySeconds 後 ─ 文字が帯方向にスライドイン（easeOutCubic）    textSlideSeconds
///   両方そろってから ── 静止                                            holdSeconds
///   最後 ── バーは来た方向へ退場・文字は進行方向へ抜ける（easeInCubic） exitSeconds
/// </code>
/// 既定値の合計は 1.75 秒で、<see cref="FishingFight"/> の余白（LeadIn・100BPM で 4 拍＝2.4 秒）
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

    /// <summary>バー・文字の等倍スケール（この演出では一切伸縮させない）。</summary>
    private const float NoStretchScale = 1f;

    /// <summary>バーのピボット（中央）。位置＝バーの中心になる。</summary>
    private static readonly SEED.Vector2 BarPivot = new(0.5f, 0.5f);

    /// <summary>上のバー・上の文字が居る側（法線の負側＝画面の上）。</summary>
    private const float TopSide = -1f;

    /// <summary>下のバー・下の文字が居る側（法線の正側＝画面の下）。</summary>
    private const float BottomSide = 1f;

    /// <summary>レベル不明（<see cref="Fish.UnknownLevel"/>）のときに出す代替文字。</summary>
    private const string UnknownLevelLabel = "?";

    // ─── 参照（シーンで割り当てる） ────────────────────────────

    /// <summary>【未使用】旧演出の白帯スプライト。毎フレーム透明にするだけ。</summary>
    [Header("参照"), SerializeField(Label = "白帯のSprite(未使用)")]
    private SEED.Sprite? bandWhiteSprite = null;

    /// <summary>【未使用】旧演出の白帯 CanvasTransform。シーンの割り当てを壊さないため残す。</summary>
    [SerializeField(Label = "白帯のCanvasTransform(未使用)")]
    private SEED.CanvasTransform? bandWhiteTransform = null;

    /// <summary>上のバーのスプライト（サイズ・色を書き換える）。</summary>
    [SerializeField(Label = "上バーのSprite")]
    private SEED.Sprite? bandBlackTopSprite = null;

    /// <summary>上のバーの CanvasTransform（位置・角度を書き換える）。</summary>
    [SerializeField(Label = "上バーのCanvasTransform")]
    private SEED.CanvasTransform? bandBlackTopTransform = null;

    /// <summary>下のバーのスプライト。</summary>
    [SerializeField(Label = "下バーのSprite")]
    private SEED.Sprite? bandBlackBottomSprite = null;

    /// <summary>下のバーの CanvasTransform。</summary>
    [SerializeField(Label = "下バーのCanvasTransform")]
    private SEED.CanvasTransform? bandBlackBottomTransform = null;

    /// <summary>「Lv◯ 魚名」のテキスト（左から入り、最後は右へ抜ける）。</summary>
    [SerializeField(Label = "レベル/魚名のText")]
    private SEED.Text? levelLabel = null;

    /// <summary>「Lv◯ 魚名」の CanvasTransform。</summary>
    [SerializeField(Label = "レベル/魚名のCanvasTransform")]
    private SEED.CanvasTransform? levelLabelTransform = null;

    /// <summary>「HIT!!!」のテキスト（右から入り、最後は左へ抜ける）。</summary>
    [SerializeField(Label = "HITのText")]
    private SEED.Text? hitLabel = null;

    /// <summary>「HIT!!!」の CanvasTransform。</summary>
    [SerializeField(Label = "HITのCanvasTransform")]
    private SEED.CanvasTransform? hitLabelTransform = null;

    // ─── レイアウト ──────────────────────────────────────────

    /// <summary>
    /// 帯の傾き（度）。キャンバスの Y は下向きなので、<b>負の値で右上がり</b>になる。
    /// バー・文字のすべてにそのまま適用する。
    /// </summary>
    [Header("レイアウト"), SerializeField(Label = "帯の角度(度)")]
    private float bandAngleDegrees = -12f;

    /// <summary>バーの定位置（帯の中心線から法線方向への距離・px）。上バーは負側、下バーは正側。</summary>
    [SerializeField(Label = "バーの中心からの距離(px)")]
    private float barOffsetPx = 280f;

    /// <summary>バーの長さ（帯の長手方向・px）。画面幅より十分長くして端を見せない。</summary>
    [SerializeField(Label = "バーの長さ(px)")]
    private float barLengthPx = 2400f;

    /// <summary>バーの太さ（帯の法線方向・px）。</summary>
    [SerializeField(Label = "バーの太さ(px)")]
    private float barThicknessPx = 220f;

    /// <summary>バーが画面外から入ってくる距離（法線方向・px）。定位置からさらにこれだけ外側が開始点。</summary>
    [SerializeField(Label = "バーのスライド距離(px)")]
    private float barEnterOffsetPx = 900f;

    /// <summary>文字が画面外から入ってくる距離（帯の長手方向・px）。退場でも同じ距離だけ進む。</summary>
    [SerializeField(Label = "文字のスライド距離(px)")]
    private float textSlideOffsetPx = 1200f;

    /// <summary>「Lv◯ 魚名」の定位置（帯の長手方向・負で左）。法線方向は上バーと同じ位置。</summary>
    [SerializeField(Label = "レベル文字の位置(帯方向px)")]
    private float levelTextAlongPx = -200f;

    /// <summary>「HIT!!!」の定位置（帯の長手方向・正で右）。法線方向は下バーと同じ位置。</summary>
    [SerializeField(Label = "HIT文字の位置(帯方向px)")]
    private float hitTextAlongPx = 200f;

    // ─── 時間割 ────────────────────────────────────────────

    /// <summary>バーがスライドインする秒数（easeOutCubic）。</summary>
    [Header("時間割"), SerializeField(Label = "バーのスライド秒数")]
    private float barSlideSeconds = 0.25f;

    /// <summary>文字が動き出すまでの待ち（秒・再生開始から）。バーより少し遅らせて重なりを作る。</summary>
    [SerializeField(Label = "文字の遅延秒数")]
    private float textDelaySeconds = 0.1f;

    /// <summary>文字がスライドインする秒数（easeOutCubic）。</summary>
    [SerializeField(Label = "文字のスライド秒数")]
    private float textSlideSeconds = 0.35f;

    /// <summary>バーと文字が両方そろってから静止する秒数。</summary>
    [SerializeField(Label = "静止秒数")]
    private float holdSeconds = 1f;

    /// <summary>退場（バーは戻り・文字は進む）にかける秒数（easeInCubic）。</summary>
    [SerializeField(Label = "終わりの秒数")]
    private float exitSeconds = 0.3f;

    // ─── 文字・色 ──────────────────────────────────────────

    /// <summary>
    /// 「Lv◯ 魚名」の書式。<c>{0}</c> がレベル（不明なら
    /// <see cref="UnknownLevelLabel"/>）、<c>{1}</c> が魚の表示名。
    /// </summary>
    [Header("文字・色"), SerializeField(Label = "レベル文字の書式")]
    private string levelTextFormat = "Lv{0} {1}";

    /// <summary>下のバーに出す固定文字列。</summary>
    [SerializeField(Label = "HITの文言")]
    private string hitText = "HIT!!!";

    /// <summary>バーの色（RGB・0〜1）。</summary>
    [SerializeField(Label = "バーの色(RGB)")]
    private SEED.Vector3 blackColor = new(0f, 0f, 0f);

    /// <summary>文字の色（RGB・0〜1）。黒バーの上に載るので既定は白。</summary>
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

    /// <summary>初期状態（ピボット・書式を整え、全要素を透明にしておく）。</summary>
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

    /// <summary>バーと文字が「両方そろう」までの秒数（＝静止区間の開始時刻）。</summary>
    private float EnterSeconds
        => SEED.Mathf.Max(
               SEED.Mathf.Max(barSlideSeconds, 0f),
               SEED.Mathf.Max(textDelaySeconds, 0f) + SEED.Mathf.Max(textSlideSeconds, 0f));

    /// <summary>演出の総尺（秒）。入り＋静止＋退場。</summary>
    private float TotalSeconds
        => EnterSeconds
         + SEED.Mathf.Max(holdSeconds, 0f)
         + SEED.Mathf.Max(exitSeconds, 0f);

    /// <summary>退場区間が始まる時刻（秒）。</summary>
    private float ExitStartSeconds => TotalSeconds - SEED.Mathf.Max(exitSeconds, 0f);

    /// <summary>
    /// バーの「外側へのはみ出し量」係数（0＝定位置／1＝画面外の開始点）。
    /// 入りは easeOutCubic で 1→0、退場は easeInCubic で 0→1（＝来た道を戻る）。
    /// </summary>
    private float BarOutward01()
    {
        float slide = SEED.Mathf.Max(barSlideSeconds, 0f);

        if (elapsed < slide)
        {
            return 1f - EaseOutCubic(Progress01(elapsed, slide));
        }
        if (elapsed < ExitStartSeconds)
        {
            return 0f;
        }
        return EaseInCubic(Progress01(elapsed - ExitStartSeconds, SEED.Mathf.Max(exitSeconds, 0f)));
    }

    /// <summary>
    /// 文字の進行度（−1＝入場前の位置／0＝定位置／+1＝退場後の位置）。
    /// 符号は「上の文字（左→定位置→右）」の向きを正とする。下の文字はこれの符号反転で使う。
    /// 入りは easeOutCubic で −1→0、退場は easeInCubic で 0→+1（＝止まらず進み続ける）。
    /// </summary>
    private float TextTravel()
    {
        float delay = SEED.Mathf.Max(textDelaySeconds, 0f);
        float slide = SEED.Mathf.Max(textSlideSeconds, 0f);

        if (elapsed < delay) { return -1f; }
        if (elapsed < delay + slide)
        {
            return EaseOutCubic(Progress01(elapsed - delay, slide)) - 1f;
        }
        if (elapsed < ExitStartSeconds) { return 0f; }

        return EaseInCubic(Progress01(elapsed - ExitStartSeconds, SEED.Mathf.Max(exitSeconds, 0f)));
    }

    /// <summary>区間内の進行度（0〜1）。長さが 0 以下なら即 1（区間を飛ばす）。</summary>
    /// <param name="value">区間頭からの経過。</param>
    /// <param name="length">区間の長さ。</param>
    private static float Progress01(float value, float length)
        => length <= DivideEpsilon ? 1f : SEED.Mathf.Clamped01(value / length);

    /// <summary>EaseInCubic: t³。退場用（ゆっくり動き出して加速する）。</summary>
    /// <param name="t">進行度（0〜1）。</param>
    private static float EaseInCubic(float t)
    {
        float c = SEED.Mathf.Clamped01(t);
        return c * c * c;
    }

    /// <summary>easeOutCubic（＝ 1 −(1 − t)³）。終わりへ向かってなめらかに減速する。</summary>
    /// <param name="t">進行度（0〜1）。</param>
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

        ApplyStaticLayout();

        // バー: 法線方向のみ。定位置 ±barOffsetPx から、さらに外側へ outward×barEnterOffsetPx。
        float outward = BarOutward01();
        float barDistance = barOffsetPx + outward * barEnterOffsetPx;
        SetPosition(bandBlackTopTransform, across * (TopSide * barDistance));
        SetPosition(bandBlackBottomTransform, across * (BottomSide * barDistance));

        // 文字: 帯の長手方向のみ。法線方向はバーと同じ位置に載せる。
        // travel は上の文字基準（左→右）なので、下の文字は符号を反転して右→左に走らせる。
        float travel = TextTravel() * textSlideOffsetPx;
        SetPosition(
            levelLabelTransform,
            across * (TopSide * barOffsetPx) + along * (levelTextAlongPx + travel));
        SetPosition(
            hitLabelTransform,
            across * (BottomSide * barOffsetPx) + along * (hitTextAlongPx - travel));

        // 色（再生中は不透明）。白帯は使わないので常に透明。
        SetSpriteColor(bandWhiteSprite, blackColor, AlphaHidden);
        SetSpriteColor(bandBlackTopSprite, blackColor, AlphaVisible);
        SetSpriteColor(bandBlackBottomSprite, blackColor, AlphaVisible);
        SetTextColor(levelLabel, textColor, AlphaVisible);
        SetTextColor(hitLabel, textColor, AlphaVisible);
    }

    /// <summary>
    /// 角度・サイズ・スケールのように「時間で変わらない」設定を流し込む。
    /// インスペクタでの値いじりが即座に効くよう毎フレーム呼ぶ（代入だけなので安い）。
    /// </summary>
    private void ApplyStaticLayout()
    {
        // 白帯は未使用（常に透明）だが、角度だけは揃えておく＝参照が生きていることの確認も兼ねる
        SetRotation(bandWhiteTransform);

        SetRotation(bandBlackTopTransform);
        SetRotation(bandBlackBottomTransform);
        SetRotation(levelLabelTransform);
        SetRotation(hitLabelTransform);

        // バーは常に等倍（伸縮ではなくスライドで見せる演出）
        SetScale(bandBlackTopTransform);
        SetScale(bandBlackBottomTransform);

        SetSpriteSize(bandBlackTopSprite, barLengthPx, barThicknessPx);
        SetSpriteSize(bandBlackBottomSprite, barLengthPx, barThicknessPx);
    }

    /// <summary>バーのピボットを中央へ揃える（位置＝バーの中心として扱うため）。</summary>
    private void ApplyPivots()
    {
        SetPivot(bandBlackTopTransform, BarPivot);
        SetPivot(bandBlackBottomTransform, BarPivot);
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

    /// <summary>全要素を透明にする（待機状態）。位置は次の <see cref="Play"/> で作り直す。</summary>
    private void Hide()
    {
        SetSpriteColor(bandWhiteSprite, blackColor, AlphaHidden);
        SetSpriteColor(bandBlackTopSprite, blackColor, AlphaHidden);
        SetSpriteColor(bandBlackBottomSprite, blackColor, AlphaHidden);
        SetTextColor(levelLabel, textColor, AlphaHidden);
        SetTextColor(hitLabel, textColor, AlphaHidden);
    }

    // ─── 内部: 小さな代入ヘルパ ──────────────────────────────

    /// <summary>CanvasTransform の位置を設定する。</summary>
    /// <param name="transform">対象（未設定可）。</param>
    /// <param name="position">キャンバス座標（アンカー中央基準）。</param>
    private static void SetPosition(SEED.CanvasTransform? transform, SEED.Vector2 position)
    {
        if (transform is not { } tf || !tf.IsValid) { return; }
        tf.Position = position;
    }

    /// <summary>CanvasTransform のスケールを等倍へ固定する。</summary>
    /// <param name="transform">対象（未設定可）。</param>
    private static void SetScale(SEED.CanvasTransform? transform)
    {
        if (transform is not { } tf || !tf.IsValid) { return; }
        tf.Scale = new SEED.Vector2(NoStretchScale, NoStretchScale);
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
