namespace SEED.UI;

// ============================================================
//  NavRepeatClock.cs — 押し続けた向きの連続移動の時計（キーリピート。2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  方向キー・D-pad・スティックを押し続けたとき、押した瞬間に 1 回、最初の遅延（InitialDelay）の後にもう 1 回、
//  以後は間隔（Interval）ごとに 1 回ずつ「動け」を出す。向きが変わったら（別の向きを押した）その場で 1 回出して数え直す。
//  離したら（None）数え直す。重いフレーム（dt が大きい）でも 1 フレームに出すのは 1 回だけ（遅れを取り戻そうとまとめて動かない）。
//  値の出典: Unity の StandaloneInputModule の repeatDelay 0.5 秒・inputActionsPerSecond 10（＝間隔 0.1 秒）（記憶による。手触りは実機で詰める）。
//  エンジンに触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>押し続けた向きの連続移動の時計（キーリピート）。</summary>
public sealed class NavRepeatClock
{
    /// <summary>押してから最初の繰り返しまでの秒（Unity の repeatDelay と同じ 0.5。記憶による）。</summary>
    public const float DefaultInitialDelay = 0.5f;

    /// <summary>以後の繰り返しの間隔の秒（Unity の inputActionsPerSecond 10 と同じ 0.1。記憶による）。</summary>
    public const float DefaultInterval = 0.1f;

    /// <summary>今押し続けている向き（None = 離している）。</summary>
    public FocusDirection Held { get; private set; }

    /// <summary>今の向きを押し始めてからの秒。</summary>
    private float _elapsed;

    /// <summary>次に「動け」を出す時刻（押し始めからの秒）。</summary>
    private float _nextAt;

    /// <summary>
    /// 1 フレーム進める。押した瞬間・向きが変わった瞬間・繰り返しの時刻に、その向きを返す（それ以外は None）。
    /// </summary>
    /// <param name="held">このフレームに押している向き（None = 離した）。</param>
    /// <param name="dt">前のフレームからの秒（負・NaN は 0 とみなす）。</param>
    /// <param name="initialDelay">最初の繰り返しまでの秒（0 以下・NaN は既定）。</param>
    /// <param name="interval">繰り返しの間隔の秒（0 以下・NaN は既定）。</param>
    /// <returns>このフレームに動く向き（動かなければ None）。</returns>
    public FocusDirection Update(FocusDirection held, float dt, float initialDelay = DefaultInitialDelay, float interval = DefaultInterval)
    {
        if (held == FocusDirection.None)
        {
            Reset();
            return FocusDirection.None;
        }
        float delay = PositiveOr(initialDelay, DefaultInitialDelay);
        float step = PositiveOr(interval, DefaultInterval);
        if (held != Held)
        {
            // 押した瞬間・別の向きへ変えた瞬間: その場で 1 回動き、最初の遅延から数え直す
            Held = held;
            _elapsed = 0f;
            _nextAt = delay;
            return held;
        }
        _elapsed += float.IsFinite(dt) && dt > 0f ? dt : 0f;
        if (_elapsed < _nextAt) return FocusDirection.None;
        // 繰り返しの時刻を過ぎた: 1 回だけ動き、次の時刻を決める（大きく遅れたら今から間隔を数える＝まとめて動かない）
        _nextAt += step;
        if (_nextAt <= _elapsed) _nextAt = _elapsed + step;
        return held;
    }

    /// <summary>離した状態へ戻す（向きなし）。</summary>
    public void Reset()
    {
        Held = FocusDirection.None;
        _elapsed = 0f;
        _nextAt = 0f;
    }

    /// <summary>正の有限の値ならそのまま、それ以外は既定。</summary>
    private static float PositiveOr(float value, float fallback) => float.IsFinite(value) && value > 0f ? value : fallback;
}
