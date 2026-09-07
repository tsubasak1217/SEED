// ============================================================================
//  Easing.cs
//  UI 演出で使う補間カーブ（イージング関数）をまとめた共通ヘルパー。
// ============================================================================

/// <summary>
/// 0→1 の進捗を「見栄えのする曲線」へ変換する純 C# の静的ヘルパー
/// 【イージング曲線の唯一の置き場】。
///
/// 【なぜ共通化するか】
/// 出現演出・退場演出・揺れの周期など、UI の気持ちよさは同じ数式の使い回しで決まる。
/// 各スクリプトが独自に「それっぽい式」を書くと、演出のノリが画面ごとにバラつき、
/// 調整のたびに全ファイルを触ることになる。曲線をここに集約しておけば、
/// 呼び出し側は「どの曲線を、何秒で」だけを決めればよくなる。
///
/// 【使い方】
/// <code>
/// // 0.35 秒かけて 0 → 1 へ、勢い余って少し行き過ぎる出現演出
/// float t     = Easing.Progress01(elapsedSeconds, 0.35f);
/// float scale = Easing.OutBack(t);
/// </code>
///
/// 【約束事】
///  - 引数 t は 0〜1 に正規化された進捗。範囲外は内部でクランプする。
///  - 戻り値は「0 で開始値・1 で終了値」に対応する係数。
///    OutBack だけは途中で 1 を超える（行き過ぎてから戻る）。
///  - すべて副作用の無い純関数なので、ゲーム時間・実時間のどちらで進めてもよい
///    （どの時間軸で計るかは呼び出し側の責務）。
/// </summary>
public static class Easing
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>進捗の下限（開始）。</summary>
    private const float ProgressMin = 0f;

    /// <summary>進捗の上限（完了）。</summary>
    private const float ProgressMax = 1f;

    /// <summary>この値以下の所要時間は「一瞬で完了」とみなす（0 除算回避）。</summary>
    private const float MinDuration = 0.0001f;

    /// <summary>
    /// OutBack の「行き過ぎ量」を決める係数。
    /// back 系イージングの標準値で、目標値を約 10% 超えてから戻る、
    /// はねるような手触りになる。
    /// </summary>
    private const float BackOvershoot = 1.70158f;

    /// <summary>三次のべき乗（Cubic 系で使う指数）。</summary>
    private const float CubicPower = 3f;

    /// <summary>半分を表す係数（往復・中点計算に使う）。</summary>
    private const float Half = 0.5f;

    // ─── 進捗の作成 ─────────────────────────────────────────

    /// <summary>
    /// 経過秒と所要秒から 0〜1 の進捗を作る【進捗計算の唯一の実装】。
    /// </summary>
    /// <param name="elapsedSeconds">開始からの経過秒（負なら 0 扱い＝まだ始まっていない）。</param>
    /// <param name="durationSeconds">演出の所要秒（0 以下なら即完了）。</param>
    /// <returns>0〜1 に収めた進捗。</returns>
    public static float Progress01(float elapsedSeconds, float durationSeconds)
    {
        if (durationSeconds <= MinDuration) { return ProgressMax; }
        return SEED.Mathf.Clamped01(elapsedSeconds / durationSeconds);
    }

    // ─── 曲線 ───────────────────────────────────────────────

    /// <summary>
    /// 直線（イージング無し）。曲線を切りたいときの明示的な選択肢として用意する。
    /// </summary>
    /// <param name="t">進捗（0〜1）。</param>
    /// <returns>そのままの進捗。</returns>
    public static float Linear(float t) => SEED.Mathf.Clamped01(t);

    /// <summary>
    /// 終わりに向かって減速し、目標を少し行き過ぎてから戻る曲線（出現の「ポンッ」に使う）。
    /// </summary>
    /// <param name="t">進捗（0〜1）。</param>
    /// <returns>0 で 0・1 で 1。途中で 1 を少し超える。</returns>
    public static float OutBack(float t)
    {
        float x = SEED.Mathf.Clamped01(t);

        // back 系の標準形: 1 + c3(x-1)^3 + c1(x-1)^2  （c3 = c1 + 1）
        float c1 = BackOvershoot;
        float c3 = c1 + ProgressMax;
        float d  = x - ProgressMax;

        return ProgressMax + c3 * d * d * d + c1 * d * d;
    }

    /// <summary>
    /// 勢いよく始まり、なめらかに減速して止まる曲線（回転の収束などに使う）。
    /// </summary>
    /// <param name="t">進捗（0〜1）。</param>
    /// <returns>0 で 0・1 で 1（行き過ぎない）。</returns>
    public static float OutCubic(float t)
    {
        float x = SEED.Mathf.Clamped01(t);
        return ProgressMax - SEED.Mathf.Pow(ProgressMax - x, CubicPower);
    }

    /// <summary>
    /// ゆっくり始まり、終わりに向かって加速する曲線（退場・吸い込みに使う）。
    /// </summary>
    /// <param name="t">進捗（0〜1）。</param>
    /// <returns>0 で 0・1 で 1。</returns>
    public static float InCubic(float t)
    {
        float x = SEED.Mathf.Clamped01(t);
        return SEED.Mathf.Pow(x, CubicPower);
    }

    /// <summary>
    /// 両端がなめらかに止まる正弦カーブ（ゆっくりした往復・揺れに使う）。
    /// </summary>
    /// <param name="t">進捗（0〜1）。</param>
    /// <returns>0 で 0・1 で 1。中央付近がいちばん速い。</returns>
    public static float InOutSine(float t)
    {
        float x = SEED.Mathf.Clamped01(t);
        return -(SEED.Mathf.Cos(SEED.Mathf.PI * x) - ProgressMax) * Half;
    }

    // ─── 往復（揺れ）────────────────────────────────────────

    /// <summary>
    /// 経過秒から「-1 → +1 → -1」となめらかに往復する値を作る
    /// 【揺れ演出の唯一の実装】。
    ///
    /// 三角波（PingPong）を <see cref="InOutSine"/> で丸めているので、
    /// 折り返し点で速度が 0 になり、機械的な往復に見えない。
    /// </summary>
    /// <param name="elapsedSeconds">開始からの経過秒（実時間で渡してよい）。</param>
    /// <param name="periodSeconds">1 往復に掛ける秒数（0 以下なら 0 を返す＝揺れない）。</param>
    /// <returns>-1〜+1 の往復値。</returns>
    public static float PingPongSigned(float elapsedSeconds, float periodSeconds)
    {
        if (periodSeconds <= MinDuration) { return ProgressMin; }

        // 1 周期＝往復なので、片道は周期の半分
        float halfPeriod = periodSeconds * Half;

        // 0→1→0 の三角波を作り、正弦で丸めてから -1〜+1 へ広げる
        float triangle = SEED.Mathf.PingPong(elapsedSeconds, halfPeriod) / halfPeriod;
        float smooth   = InOutSine(triangle);

        return SEED.Mathf.LerpUnclamped(-ProgressMax, ProgressMax, smooth);
    }
}
