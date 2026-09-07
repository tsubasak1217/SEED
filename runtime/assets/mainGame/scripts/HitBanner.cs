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
/// <b>配置は「画面の角を斜めに横切る 2 本の短い帯」</b>（2026-09-07 改定）
/// 上の帯と「Lv◯ 魚名」は<b>左上アンカー (0, 0)</b>、下の帯と「HIT!!!」は
/// <b>右下アンカー (1, 1)</b> に置き、角からの実ピクセルオフセットで配置している。
/// ルートキャンバスの実効サイズはウィンドウの実解像度そのものになり
/// <c>CanvasComponent</c> の設計解像度 (1920x1080) は効かない（＝子の座標は実ピクセル）。
/// そのため<b>角アンカーのほうが構図が保たれる</b>: 解像度が変わっても
/// 「角から何ピクセルの所を帯が横切るか」が変わらず、画面中央は常に空いたままになる。
/// 帯は 2400x220 のスプライトを <c>scale (0.5833, 0.5)</c> で約 1400x110 実 px にした
/// 短い帯で、両端は画面外へ抜ける（＝端の切り口が見えない）。
/// それでも<b>アイテムごとにクリップを分けている</b>のは、位置キーが各アイテムのローカル
/// 座標だからで、1 本のクリップで <c>actor_path</c> を使ってまとめて動かすと
/// 見通しが悪くなる。<b>アイテム 1 つにつき Animator スロット 1 つ ＋ 専用クリップ 1 本</b>
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
/// したがって本スクリプトの責務は次の 4 つだけで、<b>動きの数値は一切持たない</b>:
/// <list type="number">
///   <item>2 つの Text に文字列（「Lv◯ 魚名」「HIT!!!」）を流し込む</item>
///   <item>フォント・縁取りのようにアニメーションしない見た目を <see cref="OnStart"/> で整える</item>
///   <item><see cref="Play"/> で全 Animator にクリップの再生を依頼する</item>
///   <item>演出ルート（<see cref="bannerRoot"/>＝「HitBannerItems」）の <c>Visible</c> を
///       演出中だけ true にし、Play 開始直後や待機中に黒帯・文字が見えないようにする</item>
/// </list>
///
/// <b>待機中は演出ルートごと非表示</b>: シーンでは <c>HitBannerItems</c> に
/// <c>"visible": false</c> を持たせ、待機中は 4 アイテムの描画そのものを止める
/// （祖先が非表示の子孫は自動的に非表示になる）。<see cref="Play"/> が演出開始時に
/// <c>Visible = true</c> へ切り替え、<see cref="Update"/> が再生完了（<see cref="IsPlaying"/>
/// が false）を検知して <c>Visible = false</c> へ戻す。帯・文字のアルファを 0 にする
/// <see cref="OnStart"/> の処理はエディタでの位置確認用に不透明のまま置ける保険であり、
/// 表示/非表示の正典はあくまで <c>Visible</c> である。
///
/// <b>動きを直したいとき</b>: シーン上の各アイテムの位置が「出現したときの静止位置」。
/// 位置を並べ直したら、生成スクリプト（<c>tools/gen_hit_banner_clips.py</c>）で
/// 4 本の <c>hit_banner_*.anim</c> を作り直す（帯は法線方向、文字は帯方向に出入りする
/// キーを静止位置から展開する）。細かい調整はアニメーションパネルで
/// <b>動かしたいアイテム自身</b>を選び、クリップ "Hit" を編集する。
/// 入退場の向きも角度に依存する: 帯は<b>法線方向</b>（上帯は左上の外へ、下帯は右下の外へ）
/// に出入りし、文字は<b>帯の方向</b>に流れる（Lv は左から入って右上へ、HIT は右から
/// 入って左下へ抜ける）。
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

    /// <summary>
    /// 待機中に透明にしておく帯スプライトのアクタ名。シーンでは不透明のまま置けるよう、
    /// <see cref="OnStart"/> が名前で探してアルファを 0 にする（見つからなければ何もしない）。
    ///
    /// <b>注意</b>: これは保険の透明化であり、Play 開始直後に黒帯・文字が一瞬でも見えてしまう
    /// 問題自体は <see cref="bannerRoot"/> の <c>Visible</c> 切替（表示のオン/オフの正典）で防ぐ。
    /// </summary>
    [Header("帯"), SerializeField(Label = "帯スプライトのアクタ名")]
    private List<string> bandActorNames = new() { "HitBandBlackTop", "HitBandBlackBottom" };

    // ─── 表示制御（Play 開始前は完全に非表示にする） ─────────────────

    /// <summary>
    /// 演出 4 アイテム（帯 2 本＋文字 2 つ）をまとめて持つ親アクタ（フォルダ「HitBannerItems」）
    /// への参照。<c>GameObject.Visible</c> は祖先が非表示なら子孫も非表示になる
    /// （docs/scripting_api.md「表示 / 非表示（GameObject.Visible）」）ため、
    /// ここ 1 か所を切り替えるだけで 4 アイテムをまとめて表示/非表示にできる。
    ///
    /// シーン側は待機状態として <c>"visible": false</c> を持たせておき、
    /// <see cref="Play"/> 開始時に <c>true</c>、演出終了時に <c>false</c> へ戻す。
    /// これにより「Play 開始直後の 1 フレーム目だけ黒帯・文字が見えてしまう」問題を、
    /// アルファ操作ではなく描画そのものの ON/OFF で確実に防ぐ。
    /// </summary>
    [SerializeField(Label = "演出ルート(HitBannerItems)")]
    private SEED.GameObject? bannerRoot = null;

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

        // 演出ルートを表示状態にする。GameObject.Visible の実際の反映（描画への反映）は
        // フレーム末尾だが、Animator はこの Visible フラグと無関係に毎フレーム自動で
        // 進行する（docs/scripting_api.md「表示 / 非表示」の「止まらないもの」参照）。
        // したがって「同フレームで表示 ON → 直後に Animator.Play」の順で呼んでも、
        // このフレームの描画時点では既に表示状態が反映されており、演出の 1 フレーム目が
        // 欠けたり黒帯・文字が一瞬透明のまま出たりすることはない。
        if (bannerRoot is { IsValid: true } root) { root.Visible = true; }

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
        for (int i = 0; i < bandActorNames.Count; i++) { HideBandByName(bandActorNames[i]); }
    }

    /// <summary>
    /// 演出終了の検知と後始末（毎フレーム）。
    ///
    /// <see cref="bannerRoot"/> が「表示中」なのに <see cref="IsPlaying"/> が false
    /// （＝ 4 つのクリップが尺の末尾に達し、エンジンが自動で再生を止めた）になった
    /// 最初のフレームで、演出ルートを非表示へ戻す。<see cref="Play"/> 以外の経路で
    /// 表示 ON になることは無いため、「表示中でなければ何もしない」早期リターンで
    /// 通常時（待機中）は毎フレームの Visible 書き込みを避ける。
    /// </summary>
    /// <param name="ctx">エンジンから渡されるフレーム情報（本処理では未使用）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (bannerRoot is not { IsValid: true } root) { return; }
        if (!root.Visible) { return; }
        if (IsPlaying) { return; }
        root.Visible = false;
    }

    /// <summary>名前で帯アクタを探し、その Sprite を透明にする（無ければ何もしない）。</summary>
    /// <param name="actorName">帯スプライトを持つアクタ名。</param>
    private static void HideBandByName(string actorName)
    {
        if (string.IsNullOrEmpty(actorName)) { return; }
        var go = SEED.GameObject.Find(actorName);
        if (!go.IsValid) { return; }
        if (go.GetComponent<SEED.Sprite>() is not { } sprite) { return; }
        SEED.Color c = sprite.Color;
        sprite.Color = new SEED.Color(c.r, c.g, c.b, AlphaHidden);
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
