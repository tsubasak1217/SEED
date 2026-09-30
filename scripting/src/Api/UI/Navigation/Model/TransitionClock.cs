using System;

namespace SEED.UI;

// ============================================================
//  TransitionClock.cs — 画面の出入り 1 回の時計（遷移の時計の直し。2026-09-30。純粋な計算。docs/ui_navigation.md §2「出入りの時計」）
//
//  ScreenStack の出入り（積む・下ろす・置き換える・根まで・やり直し。予測型の戻るの確定の下ろしも）の進み具合を決める。規則:
//    1. 待つ: 入ってくる画面が落ち着く（ContentSettleGate。Enter を届けて 1 フレーム描いた）まで時計を進めない（姿勢は始まりのまま）
//    2. 始めたフレームは数えない: 落ち着いて時計を動かし始めたフレームの経過は足さない（前のフレームの重い仕事の時間が入るため）
//    3. 上限: 1 フレームで足す経過は MotionStep.MaxFrameSeconds まで（重いフレームで飛ばず、その分だけ長くかかる）
//  長さ 0（NavTransition.None・テーマで 0 秒）は、動かし始めたフレームで終わる（すぐ入れ替わる。落ち着くまでは待つ）。
//  以前（W2-7）は、実体ができあがったフレームから経過を足していたので、重いフレーム 2〜3 枚で 0.3 秒の動きが終わっていた。
// ============================================================

/// <summary>画面の出入り 1 回の時計。</summary>
public sealed class TransitionClock
{
    /// <summary>長さ（秒）で作る（負・NaN・無限は 0＝動きなし）。</summary>
    /// <param name="duration">動きの長さ（秒。NavMotion.Duration）。</param>
    public TransitionClock(float duration)
    {
        Duration = float.IsFinite(duration) ? Math.Max(0f, duration) : 0f;
    }

    /// <summary>動きの長さ（秒）。</summary>
    public float Duration { get; }

    /// <summary>進めた時間（秒。上限で切った経過の和。<see cref="Duration"/> を超えない）。</summary>
    public float Elapsed { get; private set; }

    /// <summary>時計が動き始めたか（入ってくる画面が落ち着いた後。動かし始めたフレームから true）。</summary>
    public bool Running { get; private set; }

    /// <summary>進み具合（0〜1。曲線を通す前。長さ 0 は 1）。</summary>
    public float Linear => Duration > 0f ? Math.Clamp(Elapsed / Duration, 0f, 1f) : 1f;

    /// <summary>終わったか（動き始めて、進めた時間が長さに届いた。長さ 0 は動かし始めたフレームで true）。</summary>
    public bool IsDone => Running && Elapsed >= Duration;

    /// <summary>残りの時間（秒。動きの時計で数える。重いフレームがあると実時間ではこれより長くかかる）。</summary>
    public float Remaining => Math.Max(0f, Duration - Elapsed);

    /// <summary>
    /// 1 フレーム進める（毎フレーム 1 回）。
    /// </summary>
    /// <param name="settled">入ってくる画面が落ち着いたか（false の間は進めない。動き始めた後は見ない）。</param>
    /// <param name="dt">前のフレームからの経過（秒。Time.UnscaledDeltaTime）。</param>
    /// <returns>時計が動いているか（false = 落ち着くのを待っている）。</returns>
    public bool Tick(bool settled, float dt)
    {
        if (!Running)
        {
            // 規則 1: 入ってくる画面が落ち着くまで待つ（姿勢は始まりのまま）
            if (!settled) return false;
            // 規則 2: 動かし始めたフレームの経過は数えない（前のフレームの組み立て・Enter の時間が入っている）
            Running = true;
            return true;
        }
        // 規則 3: 1 フレームで足す経過に上限（重いフレームで動きが飛ばない。長さを超えない）
        Elapsed = Math.Min(Duration, Elapsed + MotionStep.Clamp(dt));
        return true;
    }
}
