using System;

namespace SEED.UI;

// ============================================================
//  MotionStep.cs — 動きの 1 フレームで進める時間の決め方（遷移の時計の直し。2026-09-30。純粋な計算。
//                  docs/ui_navigation.md §2「出入りの時計」）
//
//  画面の出入り・ダイアログ・上からの覆い・トーストの動きは、前のフレームからの経過（Time.UnscaledDeltaTime）を足して進める。
//  経過には前のフレームの仕事（プレハブの組み立て・スクリプトの開始・撮影）の時間がそのまま入るので、重いフレームの後は
//  動きが一度に大きく進み、途中が見えないまま終わる（Wake or Pay の編集画面が右から入らずパッと出た。W3-2c の報告）。
//  ここでは 2 つの規則を持つ:
//    - 上限: 1 フレームで足す時間は MaxFrameSeconds（1/30 秒）まで。重いフレームでは動きが飛ばずに、その分だけ長くかかる
//    - 始めのフレームを数えない: 動きを始めたフレームの経過は足さない（SkipNext の後の最初の Next は 0）
//  壊れた値（負・NaN・無限）は 0 として扱う（時計が戻ったり飛んだりしない）。
//  入ってくる中身が落ち着くまで待つ規則は ContentSettleGate、画面のスタックの 1 回の出入りの時計は TransitionClock。
// ============================================================

/// <summary>動きの 1 フレームで進める時間（上限と、始めのフレームを数えない）。</summary>
public struct MotionStep
{
    /// <summary>
    /// 1 フレームで動きに足す時間の上限（秒）。1/30 秒 = 30 fps の 1 フレーム。これより遅いフレームでは動きの時計が実時間より遅れ、
    /// 途中の姿を飛ばさずに見せる（60 fps の端末で 1 フレームだけ重いときは、その 1 フレームの超えた分だけ動きが長くなる。
    /// 30 fps 以上で回っている間は実時間どおり）。
    /// </summary>
    public const float MaxFrameSeconds = 1f / 30f;

    /// <summary>次の <see cref="Next"/> を 0 にするか（動きを始めたフレーム）。</summary>
    private bool _skipNext;

    /// <summary>
    /// 動きを始めた: 次の <see cref="Next"/>（このフレームの分）を数えない。動きを始めたフレームのうちに Next を呼ぶ所
    /// （OnStart で始める入る動き。スクリプトの OnStart は BeginFrame で、同じフレームの Update で最初に進める）だけで使う。
    /// 始めたフレームの後で最初に進める所（Update の中で始め、次のフレームから進める動き）では呼ばない（1 フレーム余計に止まる）。
    /// </summary>
    public void SkipNext() => _skipNext = true;

    /// <summary>このフレームで動きに足す時間（始めのフレームは 0・それ以外は上限で切った経過）。</summary>
    /// <param name="dt">前のフレームからの経過（秒。Time.UnscaledDeltaTime）。</param>
    /// <returns>足す時間（秒。0 以上 <see cref="MaxFrameSeconds"/> 以下）。</returns>
    public float Next(float dt)
    {
        // 動きを始めたフレーム: 前のフレームの仕事（組み立て・スクリプトの開始）の時間が入っているので数えない
        if (_skipNext)
        {
            _skipNext = false;
            return 0f;
        }
        return Clamp(dt);
    }

    /// <summary>経過を上限で切る（負・NaN・無限は 0）。</summary>
    /// <param name="dt">前のフレームからの経過（秒）。</param>
    /// <param name="maxFrameSeconds">上限（秒。既定は <see cref="MaxFrameSeconds"/>）。</param>
    /// <returns>足す時間（秒）。</returns>
    public static float Clamp(float dt, float maxFrameSeconds = MaxFrameSeconds)
        => float.IsFinite(dt) && dt > 0f ? Math.Min(dt, Math.Max(0f, maxFrameSeconds)) : 0f;
}
