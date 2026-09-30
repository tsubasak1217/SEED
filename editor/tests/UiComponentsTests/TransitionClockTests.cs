using System;
using System.Collections.Generic;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// 遷移の時計の直し（2026-09-30。docs/ui_navigation.md §2「出入りの時計」）の純粋な計算のテスト:
/// 動きの 1 フレームの進め（MotionStep: 上限・始めのフレームを数えない・壊れた値）、入ってくる中身の落ち着き待ち
/// （ContentSettleGate: Enter の後 1 フレーム・Enter のフレームは数えない・待つ上限）、画面の出入り 1 回の時計
/// （TransitionClock: 落ち着くまで進まない・動かし始めたフレームは数えない・重いフレームで飛ばない・長さ 0）と、
/// Wake or Pay の編集画面を積んだときの重いフレームの列（2026-09-30 の nav,probe）で途中の姿が何枚見えるか（直す前との比べ）。
/// </summary>
public static class TransitionClockTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-5;
    /// <summary>画面の押し込みの長さ（秒。既定のテーマの motion.push）。</summary>
    private const float PushSeconds = 0.3f;
    /// <summary>60 fps の 1 フレーム（秒）。</summary>
    private const float Frame60 = 1f / 60f;
    /// <summary>重いフレーム（秒。上限の 1/30 秒より長い）。</summary>
    private const float HeavyFrame = 0.2f;
    /// <summary>落ち着くのを待たせるフレームの数（テストの中の待ち）。</summary>
    private const int WaitFrames = 5;

    /// <summary>
    /// Wake or Pay の一覧 → 編集画面の 1 回目（撮影を並べて重くした）の、積んでからのフレームの経過（ミリ秒。2026-09-30 の
    /// nav,probe の「直す前」の測定: tmp/nav_fix/probe_before_out.txt の f=14〜）。添字 2 で中身ができあがり（Ready）、添字 3 で Enter が届いた。
    /// </summary>
    private static readonly float[] EditPushFrameMs =
        { 16.9f, 16.7f, 107.2f, 47.4f, 118.7f, 16.6f, 84.7f, 97.2f, 16.6f, 85.6f, 15.1f, 96.6f, 16.6f, 81.7f, 16.6f, 81.0f, 16.8f, 16.7f, 16.9f, 16.7f, 16.9f };
    /// <summary>中身ができあがったフレームの添字（ScreenStack の BuildPhase.Ready）。</summary>
    private const int EditReadyFrame = 2;
    /// <summary>画面のスクリプトへ Enter が届いたフレームの添字。</summary>
    private const int EditEnterFrame = 3;
    /// <summary>ミリ秒 → 秒。</summary>
    private const float SecondsPerMillisecond = 0.001f;

    public static void Register(TestHarness h)
    {
        // ── 動きの 1 フレームの進め（MotionStep）──────────────────
        h.Add("遷移の時計: 1 フレームの進めは上限（1/30 秒）まで・上限より短いフレームはそのまま・壊れた値は 0", () =>
        {
            Check.Close(1.0 / 30.0, MotionStep.MaxFrameSeconds, Eps, "上限は 30 fps の 1 フレーム");
            Check.Close(MotionStep.MaxFrameSeconds, MotionStep.Clamp(HeavyFrame), Eps, "重いフレームは上限で切る");
            Check.Close(Frame60, MotionStep.Clamp(Frame60), Eps, "60 fps のフレームはそのまま（実時間どおり）");
            Check.Close(0, MotionStep.Clamp(-0.1f), Eps, "負は 0（時計が戻らない）");
            Check.Close(0, MotionStep.Clamp(float.NaN), Eps, "NaN は 0");
            Check.Close(0, MotionStep.Clamp(float.PositiveInfinity), Eps, "無限は 0（飛ばない）");
            Check.Close(0.05, MotionStep.Clamp(HeavyFrame, 0.05f), Eps, "上限を渡せる");
        });

        h.Add("遷移の時計: 動きを始めたフレームは数えない（SkipNext の後の最初の 1 回だけ 0）", () =>
        {
            var step = new MotionStep();
            Check.Close(MotionStep.MaxFrameSeconds, step.Next(HeavyFrame), Eps, "始めていなければ上限で切った経過");
            step.SkipNext();
            Check.Close(0, step.Next(HeavyFrame), Eps, "始めたフレームは 0（組み立ての時間を数えない）");
            Check.Close(Frame60, step.Next(Frame60), Eps, "次のフレームからは数える");
            Check.Close(MotionStep.MaxFrameSeconds, step.Next(HeavyFrame), Eps, "重いフレームは上限");
        });

        // ── 入ってくる中身の落ち着き待ち（ContentSettleGate）─────────
        h.Add("遷移の時計: 落ち着き待ちは Enter のフレームを数えず、その後の 1 フレームを描いてから落ち着く", () =>
        {
            var gate = new ContentSettleGate();
            Check.True(!gate.IsSettled, "できあがる前は落ち着いていない");
            gate.Frame();
            Check.True(!gate.IsSettled, "できあがっただけ（Enter 前）は落ち着いていない");
            gate.MarkEntered();
            Check.True(gate.Entered, "Enter を届けた");
            Check.True(!gate.IsSettled, "Enter を届けたフレームはまだ（そのフレームの描画が済んでいない）");
            gate.Frame();
            Check.True(gate.IsSettled, $"Enter の後 {ContentSettleGate.SettleFrames} フレームで落ち着く");
            gate.MarkEntered();
            Check.True(gate.IsSettled, "2 回目の MarkEntered で数え直さない");
            for (int i = 0; i < 1000; i++) gate.Frame();
            Check.True(gate.IsSettled, "長く生きる画面は落ち着いたまま（数は溢れない）");
        });

        h.Add("遷移の時計: 画面のスクリプトの無い中身は、待つ上限（10 フレーム）で落ち着いたとみなす", () =>
        {
            var gate = new ContentSettleGate();
            for (int i = 1; i < ContentSettleGate.MaxWaitFrames; i++)
            {
                gate.Frame();
                Check.True(!gate.IsSettled, $"{i} フレーム目はまだ待つ");
            }
            gate.Frame();
            Check.True(gate.IsSettled, $"{ContentSettleGate.MaxWaitFrames} フレーム目で待つのをやめる");
            Check.True(!gate.Entered, "Enter は届いていない");
        });

        // ── 画面の出入り 1 回の時計（TransitionClock）──────────────
        h.Add("遷移の時計: 入ってくる画面が落ち着くまで進まない（重いフレームでも）", () =>
        {
            var clock = new TransitionClock(PushSeconds);
            for (int i = 0; i < WaitFrames; i++)
            {
                Check.True(!clock.Tick(false, HeavyFrame), $"待っている間は動いていない（{i}）");
                Check.Close(0, clock.Elapsed, Eps, "待っている間は進まない");
                Check.Close(0, clock.Linear, Eps, "姿は始まりのまま");
                Check.True(!clock.IsDone, "終わらない");
            }
            Check.True(clock.Tick(true, HeavyFrame), "落ち着いたら動き始める");
            Check.True(clock.Running, "動いている");
            Check.Close(0, clock.Elapsed, Eps, "動かし始めたフレームの経過は数えない");
            clock.Tick(false, Frame60);
            Check.Close(Frame60, clock.Elapsed, Eps, "動き始めた後は落ち着きを見ない（止まらない）");
        });

        h.Add("遷移の時計: 重いフレームが続いても飛ばず、上限ずつ進んでその分だけ長くかかる", () =>
        {
            var clock = new TransitionClock(PushSeconds);
            clock.Tick(true, HeavyFrame);
            var linear = new List<float>();
            while (!clock.IsDone)
            {
                clock.Tick(true, HeavyFrame);
                linear.Add(clock.Linear);
                Check.True(linear.Count < 100, "終わる");
            }
            int expectedFrames = (int)Math.Ceiling(PushSeconds / MotionStep.MaxFrameSeconds - Eps);
            Check.Equal(expectedFrames, linear.Count, "0.3 秒 ÷ 1/30 秒 = 9 フレームで終わる（上限が無ければ 0.2 秒 × 2 で 2 フレーム）");
            Check.Close(MotionStep.MaxFrameSeconds / PushSeconds, linear[0], Eps, "最初に進む量は上限の分だけ（1/9）");
            for (int i = 1; i < linear.Count; i++) Check.True(linear[i] > linear[i - 1], $"単調に進む（{i}）");
            Check.Close(1, linear[^1], Eps, "終わりは 1");
            Check.Close(PushSeconds, clock.Elapsed, Eps, "進めた時間は長さを超えない");
        });

        h.Add("遷移の時計: 上限より短いフレーム（60 fps）では実時間どおり", () =>
        {
            var clock = new TransitionClock(PushSeconds);
            clock.Tick(true, Frame60);
            int frames = 0;
            while (!clock.IsDone && frames < 100)
            {
                clock.Tick(true, Frame60);
                frames++;
            }
            Check.Equal((int)Math.Round(PushSeconds / Frame60), frames, "0.3 秒 = 60 fps の 18 フレーム（従来どおり）");
            Check.Close(0, clock.Remaining, Eps, "残りは 0");
        });

        h.Add("遷移の時計: 長さ 0（動きなし）は落ち着くまで待ち、動かし始めたフレームで終わる・壊れた長さは 0", () =>
        {
            var none = new TransitionClock(0f);
            Check.True(!none.Tick(false, Frame60), "落ち着くまでは待つ（入れ替えない）");
            Check.True(!none.IsDone, "待っている間は終わらない");
            Check.True(none.Tick(true, HeavyFrame), "落ち着いたら動く");
            Check.True(none.IsDone, "動かし始めたフレームで終わる");
            Check.Close(1, none.Linear, Eps, "進み具合は 1");
            foreach (float broken in new[] { -1f, float.NaN, float.PositiveInfinity })
                Check.Close(0, new TransitionClock(broken).Duration, Eps, $"壊れた長さ {broken} は 0");
        });

        // ── Wake or Pay の編集画面の重いフレームの列（直す前との比べ）──────
        h.Add("遷移の時計: 編集画面を積んだときの重いフレームの列で、途中の姿が 1 割強から段階的に見える（直す前は 3 割 6 分から 4 枚）", () =>
        {
            float[] dts = Array.ConvertAll(EditPushFrameMs, ms => ms * SecondsPerMillisecond);

            // 直す前（W2-7）: 中身ができあがったフレームから経過をそのまま足す
            var before = new List<float>();
            float elapsed = 0f;
            for (int i = EditReadyFrame; i < dts.Length && elapsed < PushSeconds; i++)
            {
                elapsed += dts[i];
                if (elapsed < PushSeconds) before.Add(elapsed / PushSeconds);
            }
            Check.Close(0.357, before[0], 1e-3, "直す前は最初に見える姿で 3 割 6 分が過ぎていた（曲線を通して x = 0.24。測定と一致）");
            Check.Equal(4, before.Count, "直す前の途中の姿は 4 枚（4 枚目は曲線を通すと 0.9999 で、測定の x=0.000 と見分けられない）");

            // 直した時計: ScreenStack と同じ順（Frame → Enter → Tick）で進める
            var gate = new ContentSettleGate();
            var clock = new TransitionClock(PushSeconds);
            var after = new List<float>();
            int startFrame = -1;
            for (int i = EditReadyFrame; i < dts.Length && !clock.IsDone; i++)
            {
                gate.Frame();
                if (i == EditEnterFrame) gate.MarkEntered();
                bool wasRunning = clock.Running;
                if (!clock.Tick(gate.IsSettled, dts[i])) continue;
                if (!wasRunning) startFrame = i;
                if (!clock.IsDone && clock.Linear > 0f) after.Add(clock.Linear);
            }
            Check.Equal(EditEnterFrame + ContentSettleGate.SettleFrames, startFrame, "Enter の後 1 フレーム描いてから動き始める");
            Check.True(after[0] <= MotionStep.MaxFrameSeconds / PushSeconds + Eps, $"最初に見える姿は 1 割強まで（{after[0]:0.000}）");
            Check.True(after.Count >= 8, $"途中の姿が 8 枚以上（{after.Count} 枚）");
            for (int i = 1; i < after.Count; i++) Check.True(after[i] > after[i - 1], $"段階的に進む（{i}）");
        });
    }
}
