// ============================================================================
//  AngleMath.cs
//  角度（度）を扱う共通ユーティリティ。最短回り補間・オイラー角補間。
// ============================================================================

/// <summary>
/// 角度（度）まわりの計算をまとめた静的ユーティリティ。
///
/// 【なぜ必要か】
/// SEED の <c>Mathf</c> には Unity の <c>LerpAngle</c> に相当する API が無く、
/// <c>Mathf.Lerp(350f, 10f, 0.5f)</c> は 180 度（逆回り）になってしまう。
/// カメラ演出・キャラクターの向き補間など、回転を補間したい場面は複数あるため
/// 「最短回りの角度補間」を 1 か所へ集約して使い回す。
///
/// 【使い方】
/// <code>
/// float y = AngleMath.LerpAngle(from.y, to.y, t);
/// SEED.Vector3 rot = AngleMath.LerpEuler(fromRot, toRot, t);
/// </code>
/// </summary>
public static class AngleMath
{
    // ── 定数（マジックナンバー排除）──────────────────────────

    /// <summary>1 周の角度（度）。</summary>
    private const float FullTurnDeg = 360f;

    /// <summary>半周の角度（度）。この値を超える差は逆回りのほうが近い。</summary>
    private const float HalfTurnDeg = 180f;

    /// <summary>スムーズステップの始点（正規化された進捗の下限）。</summary>
    private const float SmoothStepFrom = 0f;

    /// <summary>スムーズステップの終点（正規化された進捗の上限）。</summary>
    private const float SmoothStepTo = 1f;

    // ── 公開メソッド ────────────────────────────────────────

    /// <summary>
    /// 角度差を -180〜+180 度の範囲へ畳み込む（最短回りの差分）。
    /// </summary>
    /// <param name="deltaDeg">畳み込む前の角度差（度）。</param>
    /// <returns>-180〜+180 度に収まる角度差。</returns>
    public static float WrapDelta(float deltaDeg)
    {
        // Repeat で 0〜360 に落としてから、180 を超える分を負側へ折り返す
        float wrapped = SEED.Mathf.Repeat(deltaDeg, FullTurnDeg);
        return wrapped > HalfTurnDeg ? wrapped - FullTurnDeg : wrapped;
    }

    /// <summary>
    /// 2 つの角度（度）を最短回りで線形補間する（Unity の Mathf.LerpAngle 相当）。
    /// </summary>
    /// <param name="fromDeg">開始角（度）。</param>
    /// <param name="toDeg">終了角（度）。</param>
    /// <param name="t">進捗（0〜1。範囲外はクランプされる）。</param>
    /// <returns>補間後の角度（度）。fromDeg を基準に増減させた値なので連続している。</returns>
    public static float LerpAngle(float fromDeg, float toDeg, float t)
    {
        float delta = WrapDelta(toDeg - fromDeg);
        return fromDeg + delta * SEED.Mathf.Clamped01(t);
    }

    /// <summary>
    /// YXZ オイラー角（度）を成分ごとに最短回りで補間する。
    /// </summary>
    /// <param name="fromDeg">開始のオイラー角（度）。</param>
    /// <param name="toDeg">終了のオイラー角（度）。</param>
    /// <param name="t">進捗（0〜1）。</param>
    /// <returns>補間後のオイラー角（度）。</returns>
    public static SEED.Vector3 LerpEuler(SEED.Vector3 fromDeg, SEED.Vector3 toDeg, float t)
    {
        return new SEED.Vector3(
            LerpAngle(fromDeg.x, toDeg.x, t),
            LerpAngle(fromDeg.y, toDeg.y, t),
            LerpAngle(fromDeg.z, toDeg.z, t));
    }

    /// <summary>
    /// 進捗 t を滑らかに整形する（両端で速度 0 になるイージング）。
    /// カメラ移動の加減速に使う。
    /// </summary>
    /// <param name="t">生の進捗（0〜1）。</param>
    /// <returns>整形後の進捗（0〜1）。</returns>
    public static float SmoothStep01(float t)
        => SEED.Mathf.SmoothStep(SmoothStepFrom, SmoothStepTo, SEED.Mathf.Clamped01(t));
}
