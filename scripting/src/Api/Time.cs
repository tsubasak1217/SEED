namespace SEED;

/// <summary>
/// フレーム時間へのアクセス。値は毎フレーム、ライフサイクル呼び出しの直前に
/// エンジン（ScriptBridge）が更新する。
///
/// <c>ctx.DeltaTime</c> と同じ値を、ctx を引き回さずどこからでも
/// <c>Time.DeltaTime</c> で参照できるようにするためのもの。
///
/// <para>
/// <b>スケール適用済み / 未適用の使い分け</b><br/>
/// <see cref="DeltaTime"/> と <see cref="ElapsedTime"/> は <see cref="Scale"/> の影響を受ける
/// 「ゲーム時間」。キャラクターの移動・アニメーション・クールダウンなど、
/// ヒットストップやスローモーションで一緒に止まってほしいものに使う。<br/>
/// <see cref="UnscaledDeltaTime"/> と <see cref="UnscaledElapsedTime"/> は影響を受けない「実時間」。
/// UI アニメーション・ポーズメニュー・演出タイマーなど、
/// ゲームが止まっていても動き続けるべきものに使う。
/// </para>
/// </summary>
public static class Time
{
    // ── 時間スケール操作種別（Rust 側 host_api.rs の TIME_SCALE_OP_* と一致させる）──
    private const int TimeScaleGet = 0;
    private const int TimeScaleSet = 1;

    /// <summary>
    /// 前フレームからの経過秒（<see cref="Scale"/> 適用後の<b>ゲーム時間</b>）。
    /// <see cref="Scale"/> が 0 のときは 0 になる。
    /// </summary>
    public static float DeltaTime { get; private set; }

    /// <summary>
    /// ゲーム内の累計時間（秒。<see cref="Scale"/> 適用後）。
    /// Play 開始時に 0 から始まり、Edit モード・ポーズ中は進まない。
    /// </summary>
    public static float ElapsedTime { get; private set; }

    /// <summary>
    /// 前フレームからの経過秒（<see cref="Scale"/> <b>未適用</b>の実時間）。
    /// ヒットストップ中（Scale = 0）でも実フレーム時間が入る。
    /// UI の演出やポーズメニューの操作はこちらで駆動すること。
    /// </summary>
    public static float UnscaledDeltaTime { get; private set; }

    /// <summary>
    /// ゲーム内の累計時間（秒。<see cref="Scale"/> <b>未適用</b>）。
    /// Edit モード・ポーズ中に進まない点は <see cref="ElapsedTime"/> と同じで、
    /// 時間スケールの影響だけを受けない。
    /// </summary>
    public static float UnscaledElapsedTime { get; private set; }

    /// <summary>
    /// ゲーム時間の進む速さ（既定 1.0）。ヒットストップ・スローモーション用。
    ///
    /// <para>
    /// 0 でゲーム時間が完全停止、0.5 で半分の速さ、2.0 で倍速。
    /// 負の値は 0 に丸められ、上限は 100。NaN は 1.0 に戻る。
    /// </para>
    /// <para>
    /// <b>止まるもの</b>: <see cref="DeltaTime"/>／<see cref="ElapsedTime"/>、
    /// ConstantUpdate の呼び出し、アニメーション（キーフレーム／モデル／クロスフェード）、
    /// 物理（3D・2D）、パーティクル、水面・水位・インタラクション場。<br/>
    /// <b>止まらないもの</b>: <see cref="UnscaledDeltaTime"/>／<see cref="UnscaledElapsedTime"/>、
    /// 入力、BGM・SE などのオーディオ再生、Update などフェーズ呼び出しそのもの
    /// （毎フレーム呼ばれ続ける）、エディタのカメラ操作。
    /// </para>
    /// <para>
    /// <b>設定値の寿命</b>: Play 開始時・Play 停止時・シーン遷移時に自動で 1.0 へ戻る。
    /// それ以外では戻らないので、ヒットストップは<b>必ず自分で戻すこと</b>
    /// （戻し忘れるとゲームが止まったままになる）。
    /// </para>
    /// <para>
    /// Play の一時停止（エディタのポーズ）とは独立で、Edit モード中の動作にも影響しない。
    /// </para>
    /// </summary>
    public static float Scale
    {
        get => ScriptHost.TimeScale(TimeScaleGet, 0f);
        set => ScriptHost.TimeScale(TimeScaleSet, value);
    }

    /// <summary>
    /// 直近 1 秒間の平均フレームレート（フレーム／秒）。
    ///
    /// <para>
    /// 実測値であり、プロジェクト設定の目標フレームレート（target_fps）とは別物。
    /// 「目標 60 に対して実測 42」のように、目標に届いているかを見るために使う。
    /// </para>
    /// <para>
    /// 平均は移動平均ではなく<b>1 秒ごとの窓</b>で確定するため、表示値は 1 秒に 1 回だけ動く
    /// （毎フレーム跳ねないので、そのまま画面に出して読める）。起動直後の 1 秒間は 0。
    /// </para>
    /// </summary>
    public static float Fps { get; private set; }

    /// <summary>
    /// 直近フレームの実時間（ミリ秒）。
    ///
    /// <para>
    /// フレーム開始から次フレーム開始までの<b>実測周期</b>で、フレームレート制限による
    /// 待ち時間も含む（60fps に制限中はおよそ 16.7）。したがって
    /// <see cref="UnscaledDeltaTime"/> × 1000 とは一致しない
    /// （あちらはポーズ・Edit モードで止まるゲーム時間側の値）。
    /// </para>
    /// <para>
    /// 描画そのものに掛かった時間の内訳を見たいときは、この値ではなく
    /// エディタのプロファイラを使うこと。
    /// </para>
    /// </summary>
    public static float FrameTimeMs { get; private set; }

    /// <summary>
    /// エンジン内部用: 現在フレームの時間を反映する。
    /// ScriptBridge が各ライフサイクル呼び出しの直前に呼ぶ。ユーザーは使わない。
    /// </summary>
    internal static void Sync(
        float deltaTime, float elapsedTime,
        float unscaledDeltaTime, float unscaledElapsedTime,
        float fps, float frameTimeMs)
    {
        DeltaTime           = deltaTime;
        ElapsedTime         = elapsedTime;
        UnscaledDeltaTime   = unscaledDeltaTime;
        UnscaledElapsedTime = unscaledElapsedTime;
        Fps                 = fps;
        FrameTimeMs         = frameTimeMs;
    }
}
