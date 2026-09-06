using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]（衝突しない基盤のみ）

/// <summary>
/// 魚が掛かった瞬間に一度だけ流す<b>「HIT!!!」の帯演出</b>の<b>再生窓口</b>。
///
/// <b>付ける場所</b>: 演出のフォルダ「HitBannerItems」の子アクタ「HitBanner」。
/// 見た目を持たない空の Actor2D で構わない。
///
/// <b>演出そのものはキーフレームクリップが持つ</b>
/// バー・文字の位置／回転／不透明度は、すべてキーフレームクリップ（クリップ名 "Hit"）の
/// プロパティトラックが動かす。本スクリプトは動きの数値を一切持たない。
///
/// <b>アイテムごとに Animator を 1 つ持つ</b>（2026-09-07 改定）
/// 4 つのアイテム（帯 2 本・文字 2 つ）は<b>それぞれ別のアンカー</b>を持つ
/// （帯上・Lv 文字は左上、帯下・HIT 文字は右下）。アンカーが違うと同じ親相対座標でも
/// 画面上の意味が変わるため、1 本のクリップで <c>actor_path</c> を使ってまとめて
/// 動かすことができない。そこで<b>アイテム 1 つにつき Animator スロット 1 つ ＋ 専用クリップ 1 本</b>
/// に分割し、各クリップは <c>actor_path</c> を空文字（＝ Animator を持つアクタ自身）にして
/// 自分だけを駆動する。
/// <code>
/// HitBandBlackTop    → assets://mainGame/animations/hit_banner_band_top.anim
/// HitBandBlackBottom → assets://mainGame/animations/hit_banner_band_bottom.anim
/// HitTextLevel       → assets://mainGame/animations/hit_banner_text_level.anim
/// HitTextHit         → assets://mainGame/animations/hit_banner_text_hit.anim
/// </code>
/// 本スクリプトはその 4 つの Animator を <see cref="animators"/> にまとめて持ち、
/// <b>同じクリップ名を全員へ同時に流す</b>（＝ 4 つのクリップが 1 つの演出を構成する）。
///
/// したがって本スクリプトの責務は次の 3 つだけで、<b>動きの数値は一切持たない</b>:
/// <list type="number">
///   <item>2 つの Text に文字列（「Lv◯ 魚名」「HIT!!!」）を流し込む</item>
///   <item>フォント・縁取りのようにアニメーションしない見た目を <see cref="OnStart"/> で整える</item>
///   <item><see cref="Play"/> で全 Animator にクリップの再生を依頼する</item>
/// </list>
///
/// <b>動きを直したいとき</b>: エディタのアニメーションパネルで<b>動かしたいアイテム自身</b>
/// （例: HitBandBlackTop）を選び、そのクリップ "Hit" を開いてキーを編集する
/// （＝ 対応する <c>hit_banner_*.anim</c> を直接編集してもよい）。
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
    /// 演出クリップを保持する Animator の一覧（アイテム 1 つにつき 1 個）。
    /// 帯 2 本・文字 2 つの Animator スロットを
    /// <c>HitBandBlackTop|Animator</c> / <c>HitBandBlackBottom|Animator</c> /
    /// <c>HitTextLevel|Animator</c> / <c>HitTextHit|Animator</c> の順に割り当てる
    /// （順序に意味は無く、全員へ同じクリップ名を同時に流すだけ）。
    /// 空でもロジックは成立する（演出が出ないだけで落ちない）。
    /// </summary>
    [Header("参照"), SerializeField(Label = "各アイテムのAnimator")]
    private List<SEED.Animator> animators = new();

    /// <summary>「Lv◯ 魚名」のテキスト（文字列とフォントだけを書き換える）。</summary>
    [SerializeField(Label = "レベル/魚名のText")]
    private SEED.Text? levelLabel = null;

    /// <summary>「HIT!!!」のテキスト。</summary>
    [SerializeField(Label = "HITのText")]
    private SEED.Text? hitLabel = null;

    // ─── 再生設定 ──────────────────────────────────────────

    /// <summary>再生するクリップ名（各 Animator の clips に登録した名前と一致させる。4 つとも "Hit"）。</summary>
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
    /// エンジンが自動で false にする）。
    /// 4 つのクリップは同じ尺・同じタイミングで流れるので、
    /// <b>1 つでも再生中なら演出中</b>とみなす（未割り当て・破棄済みは無視する）。
    /// </summary>
    public bool IsPlaying
    {
        get
        {
            for (int i = 0; i < animators.Count; i++)
            {
                var a = animators[i];
                if (a is { IsValid: true } && a.IsPlaying && a.CurrentClip == clipName) { return true; }
            }
            return false;
        }
    }

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

        // クリップが位置と不透明度（アルファ 1 → 末尾で 0）をすべて駆動する。
        // 4 つのアイテムへ同じクリップ名を同時に流し、1 つの演出として揃える。
        for (int i = 0; i < animators.Count; i++)
        {
            var a = animators[i];
            if (a is { IsValid: true }) { a.Play(clipName); }
        }
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
