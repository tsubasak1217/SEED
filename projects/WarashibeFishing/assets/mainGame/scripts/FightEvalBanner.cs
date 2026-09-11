// ============================================================================
//  FightEvalBanner.cs
//  ビートバトルの結果評価（Perfect! / Good!）を画面中央へ一瞬だけ出すバナー。
// ============================================================================

using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext

/// <summary>
/// 釣り上げが決まった瞬間に出す<b>戦いの評価バナー</b>
/// 【ビートバトルの評価表示の唯一の持ち主】。
///
/// 【責務】
/// 「渡された文言を、渡された色で、出して・見せて・引っ込める」だけ。
/// <b>Perfect か Good かは判断しない</b>（それを決めるのは
/// <see cref="FishingFight.AllExcellent"/> を読む <see cref="FishingController"/>）。
/// 責務を分けておくことで、評価の段階を増やしたくなったときに
/// 呼び出し側の文言・色を変えるだけで済む（このスクリプトは触らない）。
///
/// 【配置方式】<see cref="ResultPanel"/> / <c>PauseMenu</c> と同じ「配置済み優先・生成フォールバック」。
/// このスクリプトはプレハブ <c>assets://mainGame/actors/UI/FightEvalBanner.actor</c> の
/// ルート（Canvas を持つ Actor2D）に付く。推奨は<b>プレハブのインスタンスを
/// あらかじめシーンのルートへ置いておく</b>こと。<see cref="OnStart"/> が自分を本体として
/// 登録し、出すまで非表示にする。シーンに置かれていない場合に限り <see cref="Show"/> が
/// プレハブを <c>Instantiate</c> する（生成したアクタの <c>OnStart</c> は<b>次のフレーム</b>
/// なので、渡された内容は <see cref="pendingLabel"/> / <see cref="pendingColorHex"/> へ
/// いったん預け、<c>OnStart</c> が拾って表示する）。
///
/// 【構成（プレハブ側）】
/// <code>
/// FightEvalBanner            … このスクリプト（Canvas / auto_scale）
///  FightEvalBody             … 拡大縮小の親（ここの CanvasTransform.Scale を動かす）
///   FightEvalText            … Text（"Perfect!" / "Good!"）
///   EvalBurstGold            … ParticleEmitter（完璧のときに弾ける金の粒）
///   EvalBurstWhite           … ParticleEmitter（通常のときに弾ける白の粒）
/// </code>
/// 粒は<b>表示を始めた瞬間に一度だけ</b>一括放出する（<c>Burst</c>）。
/// どちらを弾くかは <see cref="Show"/> に渡された「完璧かどうか」で決まり、
/// 見た目（色・寿命・初速・重力・大きさ）は<b>プレハブのエミッタが全部持つ</b>
/// （このスクリプトは個数しか持たない）。
/// 拡大縮小を中間の入れ物へ掛ける理由は <see cref="ResultPanel"/> と同じ
/// （キャンバスのルートへスケールを掛けても子がまとめて拡大縮小されるとは限らない）。
///
/// 【時間軸】
/// 釣り上げ演出はスロー（<c>Time.Scale</c> を下げる）区間を含むので、
/// 出入りの計測はすべて<b>実時間</b>（<c>Time.Unscaled*</c>）で行う。
///
/// 【隠し方】
/// 待機・退場後は<b>アクタの <c>Visible</c></b> で隠す。文字の<b>影</b>は
/// 文字色のアルファとは独立に描かれる（ランタイム
/// <c>runtime/src/engine/core/font/canvas_text.rs</c> の <c>append_item</c> 参照）ため、
/// アルファ 0 だけでは影が画面に残ってしまう。
/// </summary>
public class FightEvalBanner : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>参照文字列の「アクタパス」と「スロット名」の区切り文字。</summary>
    private const char ReferenceSlotSeparator = '|';

    /// <summary>畳んだ状態の拡大率。</summary>
    private const float MinScale = 0f;

    /// <summary>原寸の拡大率。</summary>
    private const float FullScale = 1f;

    /// <summary>進捗が 1（完了）を表す値。</summary>
    private const float ProgressComplete = 1f;

    /// <summary>拡大率の下限（負のスケールを描かせない）。</summary>
    private const float ScaleFloor = 0f;

    // ─── 表示フェーズ ────────────────────────────────────────

    /// <summary>バナーの表示段階。</summary>
    private enum BannerPhase
    {
        /// <summary>隠れている（アクタごと非表示）。</summary>
        Hidden,

        /// <summary>0 倍から原寸へ勢いよく膨らんでいる最中（EaseOutBack）。</summary>
        Opening,

        /// <summary>原寸のまま読ませている最中。</summary>
        Holding,

        /// <summary>原寸から 0 倍へ引っ込んでいる最中（EaseInBack）。</summary>
        Closing,
    }

    // ─── 静的アクセサ（呼び出し側の入口が使う） ─────────────────

    /// <summary>実行中の本体（シーンに配置済み、または生成済みのインスタンス）。</summary>
    private static FightEvalBanner? Current;

    /// <summary>本体のアクタ（生成フォールバックで作った場合もここに入る）。</summary>
    /// <summary>
    /// 評価バナー本体に付ける目印の名前（<see cref="SpawnOnce"/> の照合キー）。
    /// プレハブ（FightEvalBanner.actor）のルート名と同じにしてある。
    /// </summary>
    private const string BannerActorName = "FightEvalBanner";

    private static SEED.GameObject bannerRoot;

    /// <summary>生成フォールバック時に預ける文言（実体の <c>OnStart</c> が拾う）。</summary>
    private static string pendingLabel = "";

    /// <summary>生成フォールバック時に預ける色（16 進カラーコード）。</summary>
    private static string pendingColorHex = "";

    /// <summary>生成フォールバック時に預ける「完璧かどうか」（どちらの粒を弾くか）。</summary>
    private static bool pendingPerfect;

    /// <summary>預かった内容が未消化か。</summary>
    private static bool hasPendingShow;

    // ─── インスペクタ設定（子アクタへの参照文字列）─────────────

    /// <summary>拡大縮小させる入れ物（<c>CanvasTransform</c> を持つアクタ）への相対パス。</summary>
    [Header("参照（自分からの相対パス）"), SerializeField(Label = "本体の入れ物")]
    private string bodyPath = "./FightEvalBody";

    /// <summary>評価文字（Text）への相対パス。</summary>
    [SerializeField(Label = "評価の文字")]
    private string labelPath = "./FightEvalBody/FightEvalText|Text";

    /// <summary>
    /// 完璧（Perfect）のときに弾ける金色の粒（<c>ParticleEmitter</c>）への相対パス。
    /// 粒の見た目（寿命・初速・重力・大きさ・色）は<b>すべてプレハブ側の
    /// エミッタが持つ</b>ので、詰めたくなったらエディタでこの子アクタを選んで触る。
    /// </summary>
    [SerializeField(Label = "完璧の粒(金)")]
    private string goldBurstPath = "./FightEvalBody/EvalBurstGold|ParticleEmitter";

    /// <summary>通常（Good）のときに弾ける白い粒（<c>ParticleEmitter</c>）への相対パス。</summary>
    [SerializeField(Label = "通常の粒(白)")]
    private string whiteBurstPath = "./FightEvalBody/EvalBurstWhite|ParticleEmitter";

    // ─── インスペクタ設定（演出） ────────────────────────────

    /// <summary>0 倍から原寸へ膨らむ秒数（EaseOutBack・実時間）。</summary>
    [Header("演出"), SerializeField(Label = "開く秒数")]
    private float openSeconds = 0.28f;

    /// <summary>原寸のまま読ませる秒数（実時間）。</summary>
    [SerializeField(Label = "見せる秒数")]
    private float holdSeconds = 0.9f;

    /// <summary>原寸から 0 倍へ引っ込む秒数（EaseInBack・実時間）。</summary>
    [SerializeField(Label = "閉じる秒数")]
    private float closeSeconds = 0.18f;

    /// <summary>
    /// 表示を始めた瞬間に一括放出する粒の個数（0 以下なら粒を出さない）。
    /// 金・白のどちらか片方だけに出す（両方が同時に出ることはない）。
    /// </summary>
    [SerializeField(Label = "粒の個数")]
    private int burstCount = 40;

    // ─── 実行時の状態 ────────────────────────────────────────

    /// <summary>現在の表示段階。</summary>
    private BannerPhase phase = BannerPhase.Hidden;

    /// <summary>今の段階に入ってからの経過秒（実時間）。</summary>
    private float phaseElapsed;

    /// <summary>拡大縮小させる入れ物の CanvasTransform（解決失敗なら無効ハンドル）。</summary>
    private SEED.CanvasTransform bodyTransform;

    /// <summary>評価文字の Text（解決失敗なら null）。</summary>
    private SEED.Text? labelText;

    /// <summary>完璧のときに弾く金の粒（解決失敗なら null）。</summary>
    private SEED.ParticleEmitter? goldBurst;

    /// <summary>通常のときに弾く白の粒（解決失敗なら null）。</summary>
    private SEED.ParticleEmitter? whiteBurst;

    // ─── 静的 API（呼び出し側の入口）──────────────────────────

    /// <summary>いま出ているか（呼び出し側が二重表示を避けたいときに見る）。</summary>
    public static bool IsActive { get; private set; }

    /// <summary>
    /// 評価バナーを出す【表示の唯一の入口】。
    ///
    /// シーンに配置済みのインスタンスがあればそれを表示し、無ければ
    /// <paramref name="actorPath"/> のプレハブを生成する。
    /// すでに出ているときは内容だけ差し替えて頭から出し直す。
    /// </summary>
    /// <param name="actorPath">バナーのプレハブ（<c>assets://</c> パス）。生成フォールバックにだけ使う。</param>
    /// <param name="label">出す文言（例 "Perfect!"）。</param>
    /// <param name="colorHex">文字の色（16 進カラーコード。空・書式違いならプレハブの色のまま）。</param>
    /// <param name="perfect">
    /// 完璧（Perfect）の評価か。<b>判断はしない・受け取るだけ</b>で、
    /// 弾ける粒を金（true）と白（false）のどちらにするかだけに使う。
    /// </param>
    public static void Show(string actorPath, string label, string colorHex, bool perfect)
    {
        // 実体があるなら即座に表示へ入れる（同フレームから見た目が変わる）
        if (Current is { } instance && bannerRoot.IsValid)
        {
            IsActive = true;
            instance.BeginShow(label, colorHex, perfect);
            return;
        }

        // 実体がまだ無い: プレハブを生成し、内容は次フレームの OnStart へ預ける
        if (!bannerRoot.IsValid)
        {
            if (string.IsNullOrWhiteSpace(actorPath))
            {
                SEED.Debug.LogWarning(
                    "[FightEvalBanner] シーンに本体が無く、プレハブのパスも未設定のため出せない");
                return;
            }
            // 既に同名のバナーアクタがあれば使い回す（SpawnOnce）。
            // ホットリロードで静的状態が初期化されると bannerRoot は無効へ戻るため、
            // 素の Instantiate だと評価バナーが増えていく。
            bannerRoot = SpawnOnce.GetOrInstantiate(BannerActorName, actorPath);
            if (!bannerRoot.IsValid)
            {
                SEED.Debug.LogWarning($"[FightEvalBanner] プレハブを生成できない: {actorPath}");
                return;
            }
        }

        pendingLabel = label;
        pendingColorHex = colorHex;
        pendingPerfect = perfect;
        hasPendingShow = true;
        IsActive = true;
    }

    /// <summary>
    /// 静的状態をシーン開始時の値へ戻す【取り残しを断ち切る唯一の場所】。
    /// 静的フィールドはシーン遷移で作り直されないため、<see cref="OnDestroy"/> から呼ぶ。
    /// </summary>
    public static void ResetStaticState()
    {
        IsActive = false;
        bannerRoot = default;
        Current = null;
        hasPendingShow = false;
        pendingLabel = "";
        pendingColorHex = "";
        pendingPerfect = false;
    }

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>初期化。子アクタの参照を解決し、出すまで隠しておく。</summary>
    public override void OnStart()
    {
        Current = this;
        bannerRoot = gameObject;

        ResolveReferences();

        // 出すまでは隠す（シーン上で visible=true のまま保存されていても必ず隠れる）。
        // スケールはここで 0 にする。プレハブには原寸（1）で保存しておき、
        // エディタの編集画面では原寸のまま見えるようにする。
        phase = BannerPhase.Hidden;
        phaseElapsed = 0f;
        ApplyBodyScale(MinScale);
        SetRootVisible(false);

        // 生成フォールバック経由なら、預かっていた内容でそのまま表示へ入る
        if (!hasPendingShow) { return; }
        hasPendingShow = false;
        SetRootVisible(true);
        BeginShow(pendingLabel, pendingColorHex, pendingPerfect);
    }

    /// <summary>破棄時の後始末。静的アクセサを取り消す。</summary>
    public override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) { ResetStaticState(); }
    }

    /// <summary>
    /// 毎フレームの演出更新（隠れている間は何もしない）。
    /// スロー区間があるので、進行はすべて実時間で数える。
    /// </summary>
    /// <param name="ctx">エンジンから渡されるフレーム情報（本処理では未使用）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (phase == BannerPhase.Hidden) { return; }

        phaseElapsed += SEED.Time.UnscaledDeltaTime;

        switch (phase)
        {
            case BannerPhase.Opening: UpdateOpening(); break;
            case BannerPhase.Holding: UpdateHolding(); break;
            case BannerPhase.Closing: UpdateClosing(); break;
        }
    }

    // ─── 段階ごとの更新 ──────────────────────────────────────

    /// <summary>0 倍 → 原寸（勢い余って少し行き過ぎてから収まる）。</summary>
    private void UpdateOpening()
    {
        ApplyBodyScale(Easing.OutBack(Easing.Progress01(phaseElapsed, openSeconds)));

        if (phaseElapsed < SEED.Mathf.Max(openSeconds, 0f)) { return; }
        ApplyBodyScale(FullScale);
        EnterPhase(BannerPhase.Holding);
    }

    /// <summary>原寸のまま読ませる。</summary>
    private void UpdateHolding()
    {
        if (phaseElapsed < SEED.Mathf.Max(holdSeconds, 0f)) { return; }
        EnterPhase(BannerPhase.Closing);
    }

    /// <summary>原寸 → 0 倍（いったんためてから一気に引っ込む）。</summary>
    private void UpdateClosing()
    {
        // InBack は序盤で 0 を少し下回る（＝1 を少し超えて膨らむ）ので、
        // 1 から引く形にすると「ためてから縮む」動きになる。
        ApplyBodyScale(ProgressComplete - Easing.InBack(Easing.Progress01(phaseElapsed, closeSeconds)));

        if (phaseElapsed < SEED.Mathf.Max(closeSeconds, 0f)) { return; }
        FinishClose();
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 表示を頭から始める【内容の流し込みと再生開始の唯一の場所】。
    /// 出ている最中に呼ばれても頭から出し直す。
    /// </summary>
    /// <param name="label">出す文言。</param>
    /// <param name="colorHex">文字の色（16 進カラーコード）。</param>
    /// <param name="perfect">完璧の評価か（金と白のどちらの粒を弾くか）。</param>
    private void BeginShow(string label, string colorHex, bool perfect)
    {
        SetRootVisible(true);

        if (labelText is { } t && t.IsValid)
        {
            t.Content = label;
            // 色は RGB だけ差し替える（アルファはプレハブの設定＝不透明のまま）
            UiColorUtil.ApplyRgb(t, colorHex);
        }

        // 評価に対応するほうの粒だけを一度だけ弾く。
        // Burst の要求は非表示のあいだ消費されない（GPU パーティクルは非表示の
        // サブツリーを収集しない）ので、表示へ切り替えた直後に積んでよい
        // ＝最初に描かれるフレームでまとめて放出される。
        BurstOnce(perfect ? goldBurst : whiteBurst);

        ApplyBodyScale(MinScale);
        EnterPhase(BannerPhase.Opening);
    }

    /// <summary>粒を <see cref="burstCount"/> 個だけ一括放出する（未解決・個数 0 以下なら何もしない）。</summary>
    /// <param name="emitter">放出させるエミッタ（未解決なら null）。</param>
    private void BurstOnce(SEED.ParticleEmitter? emitter)
    {
        if (burstCount <= 0) { return; }
        if (emitter is not { } e || !e.IsValid) { return; }
        e.Burst(burstCount);
    }

    /// <summary>段階を切り替えて経過秒を 0 に戻す。</summary>
    /// <param name="next">次の段階。</param>
    private void EnterPhase(BannerPhase next)
    {
        phase = next;
        phaseElapsed = 0f;
    }

    /// <summary>閉じ切ったときの後始末【終了の唯一の出口】。</summary>
    private void FinishClose()
    {
        ApplyBodyScale(MinScale);
        SetRootVisible(false);
        phase = BannerPhase.Hidden;
        phaseElapsed = 0f;
        IsActive = false;
    }

    /// <summary>本体の入れ物の拡大率を設定する（縦横同倍率・負値は 0 へ丸める）。</summary>
    /// <param name="scale">拡大率。</param>
    private void ApplyBodyScale(float scale)
    {
        if (!bodyTransform.IsValid) { return; }
        float s = SEED.Mathf.Max(scale, ScaleFloor);
        bodyTransform.Scale = new SEED.Vector2(s, s);
    }

    /// <summary>
    /// 本体のアクタを表示／非表示にする。
    /// 隠すのはアルファではなく <c>Visible</c>（文字の影がアルファと独立に描かれるため）。
    /// </summary>
    /// <param name="visible">表示するなら true。</param>
    private void SetRootVisible(bool visible)
    {
        // GameObject はプロパティ経由だと構造体のコピーへ書くことになる（CS1612）ため
        // ローカルへ受けてから設定する。
        var root = gameObject;
        if (!root.IsValid) { return; }
        root.Visible = visible;
    }

    // ─── 内部処理: 子アクタの解決 ───────────────────────────

    /// <summary>相対パスの参照文字列から子アクタ・コンポーネントを解決する。</summary>
    private void ResolveReferences()
    {
        SEED.GameObject body = ResolveActor(bodyPath);
        bodyTransform = body.IsValid
            ? body.GetComponent<SEED.CanvasTransform>() ?? default
            : default;
        if (!bodyTransform.IsValid)
        {
            SEED.Debug.LogWarning($"[FightEvalBanner] 本体の入れ物を解決できない: {bodyPath}");
        }

        labelText = ResolveText(labelPath);
        if (labelText is null)
        {
            SEED.Debug.LogWarning($"[FightEvalBanner] 評価の文字を解決できない: {labelPath}");
        }

        // 粒は「無くても演出は成立する」飾りなので、解決できなくても警告だけで先へ進む。
        goldBurst = ResolveEmitter(goldBurstPath);
        if (goldBurst is null)
        {
            SEED.Debug.LogWarning($"[FightEvalBanner] 完璧の粒を解決できない: {goldBurstPath}");
        }
        whiteBurst = ResolveEmitter(whiteBurstPath);
        if (whiteBurst is null)
        {
            SEED.Debug.LogWarning($"[FightEvalBanner] 通常の粒を解決できない: {whiteBurstPath}");
        }
    }

    /// <summary>参照文字列から ParticleEmitter を解決する（見つからなければ null）。</summary>
    /// <param name="reference">参照文字列（例 <c>./FightEvalBody/EvalBurstGold|ParticleEmitter</c>）。</param>
    private SEED.ParticleEmitter? ResolveEmitter(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) { return null; }
        SEED.GameObject owner = ResolveActor(reference);
        if (!owner.IsValid) { return null; }
        return owner.GetComponent<SEED.ParticleEmitter>();
    }

    /// <summary>参照文字列（<c>./Child/Grand</c> 形式。<c>|スロット名</c> は無視）からアクタを解決する。</summary>
    /// <param name="reference">参照文字列。空なら無効なハンドルを返す。</param>
    private SEED.GameObject ResolveActor(string reference)
    {
        string actorPath = SplitActorPath(reference);
        if (string.IsNullOrWhiteSpace(actorPath)) { return default; }
        return gameObject.FindChild(actorPath);
    }

    /// <summary>参照文字列から Text コンポーネントを解決する（見つからなければ null）。</summary>
    /// <param name="reference">参照文字列（例 <c>./FightEvalBody/FightEvalText|Text</c>）。</param>
    private SEED.Text? ResolveText(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) { return null; }
        SEED.GameObject owner = ResolveActor(reference);
        if (!owner.IsValid) { return null; }
        return owner.GetComponent<SEED.Text>();
    }

    /// <summary>参照文字列から「アクタパス」部分だけを取り出す（<c>|スロット名</c> を落とす）。</summary>
    /// <param name="reference">参照文字列。</param>
    /// <returns>アクタパス（区切りが無ければそのまま）。</returns>
    private static string SplitActorPath(string reference)
    {
        int separator = reference.IndexOf(ReferenceSlotSeparator);
        return separator < 0 ? reference : reference.Substring(0, separator);
    }
}
