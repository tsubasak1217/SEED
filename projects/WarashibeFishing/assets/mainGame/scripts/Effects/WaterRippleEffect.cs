// ============================================================================
//  WaterRippleEffect.cs
//  水面に広がる波紋 1 枚ぶんの動き（拡大 → 消滅 → 自己破棄）。
// ============================================================================

using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext

/// <summary>
/// 水面に広がる波紋の動きを司るスクリプト【波紋 1 枚の唯一の実装】。
///
/// <b>WaterRipple.actor（Ripple.obj の Model ＋ 本スクリプト）に付ける</b>。
/// 生成は <see cref="WaterSplashSpawner"/> が行い、位置・向き・大きさは
/// 生成時に <see cref="SEED.Transform"/> へ書き込まれる。本スクリプトは
/// その値を「基準」として読み、時間で変化する倍率だけを掛ける。
///
/// <b>動き</b>
/// <code>
/// 水平（X/Z） … startScale → endScale へ Easing.OutCubic で拡大（勢いよく広がって減速）
/// 消え方       … fadeStartRatio を過ぎたら、水平・垂直とも fadeEndScale へ縮めつつ
///                sinkDepth ぶん沈める
/// 寿命         … lifeSeconds 経過で自分を Destroy する
/// </code>
///
/// <b>なぜ「縮小＋沈下」で消すのか</b>
/// エンジンの Model コンポーネントには色・不透明度の API が無い
/// （<c>Visible</c> の on/off しか無く、切るとパッと消えて目立つ）。
/// そこで <b>スケールを 0 へ落として消す</b>ことで、透明度を下げたのと近い見え方にしている。
/// 水面下へ沈める <see cref="sinkDepth"/> は、水面より下へ潜り込ませて
/// 消え際をさらに目立たなくするための補助。
///
/// <b>時間軸</b>
/// 釣り上げ演出は <c>Time.Scale</c> を下げる区間を持つため、
/// 本スクリプトは<b>実時間</b>（<c>Time.UnscaledDeltaTime</c>）で進む。
/// スローの見え方は演出側が制御する（＝しぶきの速さが場面ごとにぶれない）。
/// </summary>
public class WaterRippleEffect : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>倍率の基準（等倍）。</summary>
    private const float UnitScale = 1f;

    /// <summary>スケール倍率の下限（0 だと行列が潰れるので、実質 0 の値を使う）。</summary>
    private const float MinScale = 0.0001f;

    /// <summary>秒数の下限（0 除算よけ）。</summary>
    private const float MinSeconds = 0.0001f;

    /// <summary>寿命倍率の下限（大きさに応じた寿命補正が 0 以下にならないように）。</summary>
    private const float MinDurationFactor = 0.1f;

    /// <summary>進捗の完了値。</summary>
    private const float ProgressMax = 1f;

    // ─── 見た目（インスペクタで調整）─────────────────────────

    /// <summary>
    /// 素材の実寸に合わせる倍率【OBJ の実サイズが分かるまでの調整口】。
    /// 生成時に渡されたスケールへさらに掛かるので、
    /// 「モデルが大きすぎる／小さすぎる」はここ 1 か所で直せる。
    /// </summary>
    [Header("素材"), SerializeField(Label = "素材スケール倍率")]
    private float assetScale = 1.0f;

    /// <summary>広がり始めの水平スケール倍率。</summary>
    [Header("広がり"), SerializeField(Label = "開始スケール")]
    private float startScale = 0.25f;

    /// <summary>広がり切ったときの水平スケール倍率。</summary>
    [SerializeField(Label = "終了スケール")]
    private float endScale = 1.8f;

    /// <summary>波紋が消えるまでの秒数（実時間）。</summary>
    [SerializeField(Label = "寿命(秒)")]
    private float lifeSeconds = 0.9f;

    // ─── 消え方 ───────────────────────────────────────────────

    /// <summary>
    /// 寿命のどこから消え始めるか（0〜1 の比率）。
    /// 0.5 なら後半半分をかけて縮み・沈みながら消える。
    /// </summary>
    [Header("消え方"), SerializeField(Label = "消え始める比率")]
    private float fadeStartRatio = 0.55f;

    /// <summary>消え切ったときのスケール倍率（0 で完全に消える）。</summary>
    [SerializeField(Label = "消え切ったときのスケール")]
    private float fadeEndScale = 0f;

    /// <summary>消えながら沈む深さ（メートル）。0 で沈まない。</summary>
    [SerializeField(Label = "沈む深さ(m)")]
    private float sinkDepth = 0.10f;

    /// <summary>
    /// 大きい波紋ほど長生きさせる度合い（0 で一定、1 でスケールに比例）。
    /// 生成時のスケール（＝しぶきの規模）から寿命を補正するので、
    /// 大物の大しぶきがゆったり広がる。
    /// </summary>
    [SerializeField(Label = "大きさによる寿命補正")]
    private float durationScaleInfluence = 0.5f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>生成時に与えられた基準スケール（規模の指定）。</summary>
    private SEED.Vector3 baseScale = SEED.Vector3.One;

    /// <summary>生成時の位置（沈下量はここからの相対で計算する）。</summary>
    private SEED.Vector3 basePosition = SEED.Vector3.Zero;

    /// <summary>実効の寿命（秒）。基準スケールで補正した値をここに控える。</summary>
    private float effectiveLifeSeconds = 0f;

    /// <summary>生成からの経過秒数（実時間）。</summary>
    private float elapsed = 0f;

    /// <summary>破棄要求を出したか（多重 Destroy を避けるためのガード）。</summary>
    private bool destroyRequested = false;

    // ─── ライフサイクル ───────────────────────────────────────

    /// <summary>
    /// 生成直後の初期化。生成側が書き込んだ位置・スケールを「基準」として控え、
    /// 大きさに応じた寿命を確定する。
    /// </summary>
    public override void OnStart()
    {
        if (transform.IsValid)
        {
            baseScale = transform.Scale;
            basePosition = transform.Position;
        }
        effectiveLifeSeconds = SEED.Mathf.Max(lifeSeconds, MinSeconds) * DurationFactor();
        Apply(0f);
    }

    /// <summary>毎フレームの更新。実時間で進み、寿命が尽きたら自分を破棄する。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (destroyRequested) { return; }

        elapsed += SEED.Time.UnscaledDeltaTime;
        Apply(elapsed);

        if (elapsed < effectiveLifeSeconds) { return; }

        destroyRequested = true;
        gameObject.Destroy();
    }

    // ─── 見た目の更新 ─────────────────────────────────────────

    /// <summary>
    /// 経過秒から位置とスケールを決めて書き込む【波紋の見た目を決める唯一の場所】。
    /// </summary>
    /// <param name="seconds">生成からの経過秒（実時間）。</param>
    private void Apply(float seconds)
    {
        if (!transform.IsValid) { return; }

        float life = SEED.Mathf.Max(effectiveLifeSeconds, MinSeconds);

        // 広がり: 勢いよく開いて減速する（OutCubic）
        float spread01 = Easing.OutCubic(Easing.Progress01(seconds, life));
        float horizontal = SEED.Mathf.Lerp(startScale, endScale, spread01);

        // 消え: fadeStartRatio を過ぎてから縮み始める
        float fadeStart = life * SEED.Mathf.Clamped01(fadeStartRatio);
        float fadeSpan = SEED.Mathf.Max(life - fadeStart, MinSeconds);
        float vanish01 = Easing.Progress01(seconds - fadeStart, fadeSpan);
        float vanishScale = SEED.Mathf.Lerp(UnitScale, fadeEndScale, vanish01);

        float x = SEED.Mathf.Max(baseScale.x * assetScale * horizontal * vanishScale, MinScale);
        float z = SEED.Mathf.Max(baseScale.z * assetScale * horizontal * vanishScale, MinScale);
        // 平たい板なので縦は広げない（消えるときだけ一緒に潰す）
        float y = SEED.Mathf.Max(baseScale.y * assetScale * vanishScale, MinScale);

        transform.Scale = new SEED.Vector3(x, y, z);
        transform.Position = new SEED.Vector3(
            basePosition.x,
            basePosition.y - SEED.Mathf.Max(sinkDepth, 0f) * vanish01,
            basePosition.z);
    }

    /// <summary>
    /// 基準スケールから寿命の補正倍率を作る
    /// （大きいしぶきほど長く残る）【寿命補正の唯一の式】。
    /// </summary>
    private float DurationFactor()
    {
        float influence = SEED.Mathf.Clamped01(durationScaleInfluence);
        float scale = SEED.Mathf.Max(baseScale.x, MinScale);
        return SEED.Mathf.Max(
            UnitScale + (scale - UnitScale) * influence, MinDurationFactor);
    }
}
