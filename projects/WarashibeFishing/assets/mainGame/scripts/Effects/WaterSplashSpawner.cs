// ============================================================================
//  WaterSplashSpawner.cs
//  水しぶき（波紋＋水柱）をまとめて生成する静的ヘルパー。
// ============================================================================

/// <summary>
/// 水面のしぶきを一括生成する静的クラス【しぶきの「まき方」の唯一の実装】。
///
/// <b>シーンに置く必要は無い</b>。設定（<see cref="WaterSplashSettings"/>）を
/// 呼び出し側から受け取るだけなので、どのスクリプトからでも
/// <see cref="Spawn"/> を 1 行呼ぶだけでしぶきが出る。
///
/// <b>責務の分割</b>
/// - どこに何個まくか（本クラス）
/// - 1 個ずつの動き（<see cref="WaterRippleEffect"/> / <see cref="WaterColumnEffect"/>）
/// - 規模の調整値（<see cref="WaterSplashSettings"/>）
/// - いつ出すか（<see cref="CatchPresenter"/> などの呼び出し側）
///
/// <b>生成したアクタの寿命</b>
/// 各エフェクトのスクリプトが自分で Destroy するので、
/// 呼び出し側はハンドルを保持しなくてよい（撒きっぱなしで漏れない）。
///
/// <b>大きさの渡し方</b>
/// 生成したアクタの <see cref="SEED.Transform.Scale"/> に倍率を書き込む。
/// エフェクト側は OnStart でその値を「基準スケール」として読み、
/// 自分のアニメーション倍率を掛けて使う（＝規模の指定はここ 1 か所で完結する）。
/// </summary>
public static class WaterSplashSpawner
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>1 周の角度（度）。ランダムな向きを作るのに使う。</summary>
    private const float FullCircleDegrees = 360f;

    /// <summary>強度の下限。</summary>
    private const float IntensityMin = 0f;

    /// <summary>強度の上限。</summary>
    private const float IntensityMax = 1f;

    /// <summary>個数の下限（0 個なら生成しない）。</summary>
    private const int MinCount = 0;

    /// <summary>スケール倍率の下限（0 以下だと描画されないため）。</summary>
    private const float MinScale = 0.01f;

    /// <summary>中心に必ず 1 個置くときの添字。</summary>
    private const int CenterIndex = 0;

    /// <summary>正規化の分母が 0 になるのを避けるしきい値（cm）。</summary>
    private const float SizeRangeEpsilon = 0.001f;

    // ─── 強度の算出 ───────────────────────────────────────────

    /// <summary>
    /// 魚の基準サイズ（cm）から、しぶきの強度（0〜1）を作る
    /// 【サイズ→規模の唯一の正規化点】。
    ///
    /// minSizeCm 以下で 0、maxSizeCm 以上で 1 になる線形補間。
    /// 範囲が潰れている（min が max 以上）場合は 0 を返して「最小の規模」に倒す。
    /// </summary>
    /// <param name="sizeCm">魚の基準サイズ（cm）。</param>
    /// <param name="minSizeCm">強度 0 とみなすサイズ（cm）。</param>
    /// <param name="maxSizeCm">強度 1 とみなすサイズ（cm）。</param>
    /// <returns>0〜1 の強度。</returns>
    public static float Intensity01(float sizeCm, float minSizeCm, float maxSizeCm)
    {
        float range = maxSizeCm - minSizeCm;
        if (range <= SizeRangeEpsilon) { return IntensityMin; }
        return SEED.Mathf.Clamped01((sizeCm - minSizeCm) / range);
    }

    // ─── 生成 ─────────────────────────────────────────────────

    /// <summary>
    /// 水しぶきを一式まく【しぶき生成の唯一の入口】。
    ///
    /// <code>
    /// 波紋 … rippleCount(強度) 個。1 個目は中心、残りは半径 rippleRadius(強度) の
    ///        円内にランダム配置（向きもランダム）。
    /// 水柱 … columnCount(強度) 本。1 本目は中心、残りは半径 columnRadius(強度) の
    ///        円内にランダム配置。
    /// </code>
    ///
    /// 円内の一様分布にするため、半径は r = R * sqrt(u)（u は 0〜1 の一様乱数）で取る
    /// （そのまま r = R * u にすると中心に偏る）。
    /// </summary>
    /// <param name="settings">規模の設定（<see cref="WaterSplashSettings.Default"/> で既定値）。</param>
    /// <param name="center">しぶきの中心（水平位置だけを使う）。</param>
    /// <param name="surfaceY">水面の高さ（ワールド Y）。生成物はこの高さに並べる。</param>
    /// <param name="intensity01">規模の強度（0〜1。範囲外はクランプ）。</param>
    public static void Spawn(
        in WaterSplashSettings settings, SEED.Vector3 center, float surfaceY, float intensity01)
    {
        float intensity = SEED.Mathf.Clamped(intensity01, IntensityMin, IntensityMax);
        float y = surfaceY + settings.surfaceOffsetY;

        // 波紋（水面に広がる輪）
        SpawnRing(
            settings.rippleActorPath,
            LerpCount(settings.rippleCountMin, settings.rippleCountMax, intensity),
            SEED.Mathf.Lerp(settings.rippleRadiusMin, settings.rippleRadiusMax, intensity),
            SEED.Mathf.Lerp(settings.rippleScaleMin, settings.rippleScaleMax, intensity),
            settings.scaleJitter, center, y);

        // 水柱（突き上がる飛沫）
        SpawnRing(
            settings.columnActorPath,
            LerpCount(settings.columnCountMin, settings.columnCountMax, intensity),
            SEED.Mathf.Lerp(settings.columnRadiusMin, settings.columnRadiusMax, intensity),
            SEED.Mathf.Lerp(settings.columnScaleMin, settings.columnScaleMax, intensity),
            settings.scaleJitter, center, y);
    }

    // ─── 内部処理 ─────────────────────────────────────────────

    /// <summary>
    /// 同じアクタを「中心 1 個＋周囲ランダム」でまく
    /// 【波紋・水柱で共通の配置ロジック】。
    /// </summary>
    /// <param name="actorPath">生成するアクタのパス（空なら何もしない）。</param>
    /// <param name="count">個数（0 以下なら何もしない）。</param>
    /// <param name="radius">中心から散らす最大半径（メートル）。</param>
    /// <param name="scale">スケール倍率（ばらつき前の基準値）。</param>
    /// <param name="jitter">スケールのばらつき（±の割合）。</param>
    /// <param name="center">中心のワールド位置（Y は使わない）。</param>
    /// <param name="y">生成する高さ（ワールド Y）。</param>
    private static void SpawnRing(
        string actorPath, int count, float radius, float scale,
        float jitter, SEED.Vector3 center, float y)
    {
        if (string.IsNullOrWhiteSpace(actorPath)) { return; }
        if (count <= MinCount) { return; }

        for (int i = 0; i < count; i++)
        {
            // 1 個目は必ず中心（＝魚が飛び出した点）に置き、絵の芯を作る
            var position = i == CenterIndex
                ? new SEED.Vector3(center.x, y, center.z)
                : RandomPointOnDisc(center, y, radius);

            SpawnOne(actorPath, position, SEED.Random.Range(0f, FullCircleDegrees),
                     JitteredScale(scale, jitter));
        }
    }

    /// <summary>
    /// アクタを 1 個生成し、位置・向き・スケールを与える
    /// 【Instantiate を呼ぶ唯一の場所】。
    /// 生成に失敗した場合は警告だけ出して続行する（演出は止めない）。
    /// </summary>
    /// <param name="actorPath">生成するアクタのパス。</param>
    /// <param name="position">ワールド位置。</param>
    /// <param name="yawDegrees">Y 軸まわりの向き（度）。同じ模様の繰り返しを目立たなくする。</param>
    /// <param name="scale">一様スケール倍率。</param>
    private static void SpawnOne(string actorPath, SEED.Vector3 position, float yawDegrees, float scale)
    {
        var actor = SEED.GameObject.Instantiate(actorPath);
        if (!actor.IsValid)
        {
            SEED.Debug.LogWarning($"[Splash] しぶきを生成できない: {actorPath}");
            return;
        }

        // 3D アクタは生成と同じフレームに Transform を書き込める（その値が優先される）
        if (actor.GetComponent<SEED.Transform>() is not { } tf) { return; }
        tf.Position = position;
        tf.Rotation = new SEED.Vector3(0f, yawDegrees, 0f);
        tf.Scale = new SEED.Vector3(scale, scale, scale);
    }

    /// <summary>
    /// 半径 <paramref name="radius"/> の円板内の一様な 1 点（高さは <paramref name="y"/> 固定）。
    /// </summary>
    /// <param name="center">円の中心（Y は使わない）。</param>
    /// <param name="y">返す点の高さ。</param>
    /// <param name="radius">最大半径（0 以下なら中心を返す）。</param>
    private static SEED.Vector3 RandomPointOnDisc(SEED.Vector3 center, float y, float radius)
    {
        if (radius <= 0f) { return new SEED.Vector3(center.x, y, center.z); }

        float angleRad = SEED.Random.Range(0f, FullCircleDegrees) * SEED.Mathf.Deg2Rad;
        float r = radius * SEED.Mathf.Sqrt(SEED.Random.Value);   // sqrt(u) で円内一様
        return new SEED.Vector3(
            center.x + SEED.Mathf.Cos(angleRad) * r,
            y,
            center.z + SEED.Mathf.Sin(angleRad) * r);
    }

    /// <summary>
    /// スケールにばらつきを与える（scale × (1 ± jitter)）。
    /// 下限 <see cref="MinScale"/> でクランプし、潰れた個体が出ないようにする。
    /// </summary>
    /// <param name="scale">基準スケール。</param>
    /// <param name="jitter">ばらつきの割合（0 でばらつかない）。</param>
    private static float JitteredScale(float scale, float jitter)
    {
        float amount = SEED.Mathf.Max(jitter, 0f);
        float factor = 1f + SEED.Random.Range(-amount, amount);
        return SEED.Mathf.Max(scale * factor, MinScale);
    }

    /// <summary>
    /// 個数の線形補間（四捨五入して整数にし、負にならないよう下限を掛ける）。
    /// </summary>
    /// <param name="min">強度 0 のときの個数。</param>
    /// <param name="max">強度 1 のときの個数。</param>
    /// <param name="intensity01">強度（0〜1）。</param>
    private static int LerpCount(int min, int max, float intensity01)
    {
        float value = SEED.Mathf.Lerp(min, max, intensity01);
        return SEED.Mathf.Max(SEED.Mathf.RoundToInt(value), MinCount);
    }
}
