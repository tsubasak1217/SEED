// ============================================================================
//  WaterColumnEffect.cs
//  突き上がる水柱 1 本ぶんの動き（上昇 → 落下しながら拡散 → 自己破棄）。
// ============================================================================

using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext

/// <summary>
/// 水面から突き上がる水柱の動きを司るスクリプト【水柱 1 本の唯一の実装】。
///
/// <b>WaterColumn.actor（WaterColmn.obj の Model ＋ 本スクリプト）に付ける</b>。
/// 生成は <see cref="WaterSplashSpawner"/> が行い、位置・向き・大きさは
/// 生成時に <see cref="SEED.Transform"/> へ書き込まれる。本スクリプトは
/// その値を「基準」として読み、時間で変化する倍率だけを掛ける。
///
/// <b>動き（2 段）</b>
/// <code>
/// 上昇（riseSeconds） … 縦(Y)スケールを 0 → peakHeightScale へ Easing.OutCubic で伸ばす
///                        （水面から一気に突き上がって減速する）
/// 落下（fallSeconds） … 縦(Y)スケールを Easing.InCubic で 0 へ縮めながら、
///                        水平(X/Z)スケールを spreadScale へ広げる
///                        （崩れて横へ散る＝飛沫が落ちる見え方）
/// </code>
/// 落下が終わったら自分を Destroy する。
///
/// <b>時間軸</b>
/// <see cref="WaterRippleEffect"/> と同じく<b>実時間</b>で進む
/// （釣り上げ演出の <c>Time.Scale</c> の上下でしぶきの速さが変わらないように）。
/// </summary>
public class WaterColumnEffect : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>倍率の基準（等倍）。</summary>
    private const float UnitScale = 1f;

    /// <summary>スケール倍率の下限（0 だと行列が潰れるので、実質 0 の値を使う）。</summary>
    private const float MinScale = 0.0001f;

    /// <summary>秒数の下限（0 除算よけ）。</summary>
    private const float MinSeconds = 0.0001f;

    /// <summary>寿命倍率の下限。</summary>
    private const float MinDurationFactor = 0.1f;

    // ─── 見た目（インスペクタで調整）─────────────────────────

    /// <summary>
    /// 素材の実寸に合わせる倍率【OBJ の実サイズが分かるまでの調整口】。
    /// 生成時に渡されたスケールへさらに掛かる。
    /// </summary>
    [Header("素材"), SerializeField(Label = "素材スケール倍率")]
    private float assetScale = 1.0f;

    /// <summary>突き上がりに掛ける秒数（実時間）。</summary>
    [Header("上昇"), SerializeField(Label = "上昇(秒)")]
    private float riseSeconds = 0.18f;

    /// <summary>いちばん伸びたときの縦スケール倍率。</summary>
    [SerializeField(Label = "頂点の縦スケール")]
    private float peakHeightScale = 1.0f;

    /// <summary>上昇中の水平スケール倍率（細く突き上がるなら 1 未満）。</summary>
    [SerializeField(Label = "上昇中の水平スケール")]
    private float riseWidthScale = 0.9f;

    /// <summary>崩れて落ちるまでの秒数（実時間）。</summary>
    [Header("落下"), SerializeField(Label = "落下(秒)")]
    private float fallSeconds = 0.45f;

    /// <summary>落ち切ったときの水平スケール倍率（横へ散る量）。</summary>
    [SerializeField(Label = "拡散スケール")]
    private float spreadScale = 1.7f;

    /// <summary>
    /// 大きい水柱ほど長く見せる度合い（0 で一定、1 でスケールに比例）。
    /// 生成時のスケール（＝しぶきの規模）から上昇・落下の秒数を補正する。
    /// </summary>
    [SerializeField(Label = "大きさによる時間補正")]
    private float durationScaleInfluence = 0.5f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>生成時に与えられた基準スケール（規模の指定）。</summary>
    private SEED.Vector3 baseScale = SEED.Vector3.One;

    /// <summary>実効の上昇秒数（基準スケールで補正済み）。</summary>
    private float effectiveRiseSeconds = 0f;

    /// <summary>実効の落下秒数（基準スケールで補正済み）。</summary>
    private float effectiveFallSeconds = 0f;

    /// <summary>生成からの経過秒数（実時間）。</summary>
    private float elapsed = 0f;

    /// <summary>破棄要求を出したか（多重 Destroy を避けるためのガード）。</summary>
    private bool destroyRequested = false;

    // ─── ライフサイクル ───────────────────────────────────────

    /// <summary>生成直後の初期化。基準スケールを控え、大きさに応じた秒数を確定する。</summary>
    public override void OnStart()
    {
        if (transform.IsValid) { baseScale = transform.Scale; }

        float factor = DurationFactor();
        effectiveRiseSeconds = SEED.Mathf.Max(riseSeconds, MinSeconds) * factor;
        effectiveFallSeconds = SEED.Mathf.Max(fallSeconds, MinSeconds) * factor;
        Apply(0f);
    }

    /// <summary>毎フレームの更新。実時間で進み、落ち切ったら自分を破棄する。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (destroyRequested) { return; }

        elapsed += SEED.Time.UnscaledDeltaTime;
        Apply(elapsed);

        if (elapsed < effectiveRiseSeconds + effectiveFallSeconds) { return; }

        destroyRequested = true;
        gameObject.Destroy();
    }

    // ─── 見た目の更新 ─────────────────────────────────────────

    /// <summary>
    /// 経過秒からスケールを決めて書き込む【水柱の見た目を決める唯一の場所】。
    /// 上昇区間と落下区間で別の曲線を使う。
    /// </summary>
    /// <param name="seconds">生成からの経過秒（実時間）。</param>
    private void Apply(float seconds)
    {
        if (!transform.IsValid) { return; }

        float height;
        float width;

        if (seconds < effectiveRiseSeconds)
        {
            // 上昇: 0 → peakHeightScale（勢いよく伸びて減速）
            float t = Easing.OutCubic(Easing.Progress01(seconds, effectiveRiseSeconds));
            height = peakHeightScale * t;
            width = riseWidthScale;
        }
        else
        {
            // 落下: peakHeightScale → 0（加速して縮む）／水平は spreadScale へ広がる
            float t = Easing.Progress01(seconds - effectiveRiseSeconds, effectiveFallSeconds);
            height = peakHeightScale * (UnitScale - Easing.InCubic(t));
            width = SEED.Mathf.Lerp(riseWidthScale, spreadScale, Easing.OutCubic(t));
        }

        transform.Scale = new SEED.Vector3(
            SEED.Mathf.Max(baseScale.x * assetScale * width, MinScale),
            SEED.Mathf.Max(baseScale.y * assetScale * height, MinScale),
            SEED.Mathf.Max(baseScale.z * assetScale * width, MinScale));
    }

    /// <summary>
    /// 基準スケールから秒数の補正倍率を作る（大きい水柱ほどゆっくり）
    /// 【時間補正の唯一の式】。
    /// </summary>
    private float DurationFactor()
    {
        float influence = SEED.Mathf.Clamped01(durationScaleInfluence);
        float scale = SEED.Mathf.Max(baseScale.x, MinScale);
        return SEED.Mathf.Max(
            UnitScale + (scale - UnitScale) * influence, MinDurationFactor);
    }
}
