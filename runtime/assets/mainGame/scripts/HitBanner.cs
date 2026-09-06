using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]（衝突しない基盤のみ）

/// <summary>
/// 魚が掛かった瞬間に一度だけ流す<b>「HIT!!!」の帯演出</b>の<b>再生窓口</b>。
///
/// <b>付ける場所</b>: 演出の親アクタ「HitBannerItems」の子アクタ「HitBanner」。
/// 見た目を持たない空の Actor2D で構わない。
///
/// <b>演出そのものはキーフレームクリップが持つ</b>
/// バー・文字の位置／回転／不透明度は、すべて
/// <c>assets://mainGame/animations/hit_banner.anim</c>（クリップ名 "Hit"）の
/// プロパティトラックが動かす。クリップは <b>HitBannerItems に付けた Animator スロット</b>が保持し、
/// トラックの <c>actor_path</c>（"HitBandBlackTop" / "HitTextHit" など）で
/// HitBannerItems の各子アクタを名前で指している
/// （<c>actor_path</c> は Animator 保持アクタからの<b>下向き</b>相対パスで、親や兄弟へは遡れない。
/// そのため Animator は HitBanner ではなく共通の親である HitBannerItems に置く）。
///
/// したがって本スクリプトの責務は次の 3 つだけで、<b>動きの数値は一切持たない</b>:
/// <list type="number">
///   <item>2 つの Text に文字列（「Lv◯ 魚名」「HIT!!!」）を流し込む</item>
///   <item>フォント・縁取りのようにアニメーションしない見た目を <see cref="OnStart"/> で整える</item>
///   <item><see cref="Play"/> で Animator にクリップの再生を依頼する</item>
/// </list>
///
/// <b>動きを直したいとき</b>: エディタのアニメーションパネルで HitBannerItems を選び、
/// クリップ "Hit" を開いてキーを編集する（＝ <c>hit_banner.anim</c> を直接編集してもよい）。
/// <b>帯の角度を変えるときはクリップの作り直しが要る</b>: 位置キーは角度 −12° で
/// 展開済みの実座標であり、回転トラックだけ変えても位置は追従しない。
///
/// <b>担当範囲</b>
/// 演出の再生だけを担い、いつ再生するかは持たない（<see cref="FishingController"/> が
/// ヒット成立時・わらしべ乗り換え時に <see cref="Play"/> を呼ぶ）。
/// </summary>
public class HitBanner : SEEDScript
{
    // ─── 定数（マジックナンバーを持ち込まないための名前付き） ───────────

    /// <summary>非表示（完全に透明）。再生前の待機状態に使う。</summary>
    private const float AlphaHidden = 0f;

    /// <summary>不透明（縁取り色に使う）。</summary>
    private const float AlphaVisible = 1f;

    /// <summary>レベル不明（<see cref="Fish.UnknownLevel"/>）のときに出す代替文字。</summary>
    private const string UnknownLevelLabel = "?";

    // ─── 参照（シーンで割り当てる） ────────────────────────────

    /// <summary>
    /// 演出クリップを保持する Animator（HitBannerItems の「Animator」スロット）。
    /// クリップのトラックが HitBannerItems 配下の子アクタを名前で指すため、
    /// <b>HitBannerItems 自身</b>の Animator を割り当てること。
    /// </summary>
    [Header("参照"), SerializeField(Label = "演出のAnimator(HitBannerItems)")]
    private SEED.Animator? animator = null;

    /// <summary>「Lv◯ 魚名」のテキスト（文字列とフォントだけを書き換える）。</summary>
    [SerializeField(Label = "レベル/魚名のText")]
    private SEED.Text? levelLabel = null;

    /// <summary>「HIT!!!」のテキスト。</summary>
    [SerializeField(Label = "HITのText")]
    private SEED.Text? hitLabel = null;

    // ─── 再生設定 ──────────────────────────────────────────

    /// <summary>再生するクリップ名（Animator の clips に登録した名前と一致させる）。</summary>
    [Header("再生"), SerializeField(Label = "クリップ名")]
    private string clipName = "Hit";

    // ─── 文字 ──────────────────────────────────────────────

    /// <summary>
    /// 「Lv◯ 魚名」の書式。<c>{0}</c> がレベル（不明なら
    /// <see cref="UnknownLevelLabel"/>）、<c>{1}</c> が魚の表示名。
    /// </summary>
    [Header("文字"), SerializeField(Label = "レベル文字の書式")]
    private string levelTextFormat = "Lv{0} {1}";

    /// <summary>下のバーに出す固定文字列。</summary>
    [SerializeField(Label = "HITの文言")]
    private string hitText = "HIT!!!";

    /// <summary>
    /// 文字に使うフォントの assets:// 仮想パス（空文字＝組み込みフォント）。
    /// アニメーションしない設定なので <see cref="OnStart"/> で一度だけ流し込む。
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

    /// <summary>
    /// 演出が再生中か。<b>正典は Animator の再生状態</b>（クリップが尺の末尾に達すると
    /// エンジンが自動で false にする）。Animator 未割り当て・破棄済みのときは常に false。
    /// </summary>
    public bool IsPlaying
        => animator is { } a && a.IsValid && a.IsPlaying && a.CurrentClip == clipName;

    // ─── 公開 API ───────────────────────────────────────────

    /// <summary>
    /// 演出を頭から再生する【再生の唯一の入口】。再生中に呼び直すと最初へ巻き戻る
    /// （わらしべで立て続けに乗り換わっても、常に最新の魚が出る）。
    ///
    /// 位置・不透明度はクリップが作るため、ここでは文字列の差し替えと再生依頼だけを行う。
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

        // クリップが位置と不透明度（アルファ 1 → 末尾で 0）をすべて駆動する
        if (animator is { } a && a.IsValid) { a.Play(clipName); }
    }

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// アニメーションしない見た目（フォント・縁取り）を整え、待機状態（透明）にする。
    ///
    /// 透明化は保険である。再生前の不透明度はシーンの保存値であり、
    /// 再生開始以降はクリップの色トラックが所有する。
    /// </summary>
    public override void OnStart()
    {
        ApplyTextStyle(levelLabel);
        ApplyTextStyle(hitLabel);
        HideText(levelLabel);
        HideText(hitLabel);
    }

    // ─── 内部: 小さな代入ヘルパ ──────────────────────────────

    /// <summary>1 つの Text へフォント・縁取りの太さ・縁取り色を設定する。</summary>
    /// <param name="text">対象（未設定可）。</param>
    private void ApplyTextStyle(SEED.Text? text)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.FontPath     = fontPath;
        t.OutlineWidth = outlineWidthPx;
        t.OutlineColor = new SEED.Color(outlineColor.x, outlineColor.y, outlineColor.z, AlphaVisible);
    }

    /// <summary>テキストを透明にする（色味は保ったまま不透明度だけ 0 にする）。</summary>
    /// <param name="text">対象（未設定可）。</param>
    private static void HideText(SEED.Text? text)
    {
        if (text is not { } t || !t.IsValid) { return; }
        SEED.Color c = t.Color;
        t.Color = new SEED.Color(c.r, c.g, c.b, AlphaHidden);
    }

    /// <summary>テキストの文字列を差し替える（色は触らない）。</summary>
    /// <param name="text">対象（未設定可）。</param>
    /// <param name="content">表示する文字列。</param>
    private static void SetTextContent(SEED.Text? text, string content)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Content = content;
    }
}
