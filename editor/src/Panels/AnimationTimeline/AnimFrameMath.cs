// ============================================================
//  AnimFrameMath.cs — フレーム⇔秒の変換とスナップ（純ロジック）
//
//  タイムラインは「フレーム」を編集の基本単位として扱う。
//  一方、.anim（および Rust ランタイムの評価）はキー時刻を**秒**で保持する。
//  その橋渡しをここへ集約し、UI から切り離して単体テスト可能にする。
//
//  【なぜクリップごとに fps を持つのか】
//   従来はスナップ間隔が 1/60 秒固定だった。しかし作りたい絵の刻みは
//   クリップごとに違う（ドット絵 8fps、UI 演出 30fps、カメラワーク 60fps）。
//   クリップに fps を持たせ、フレーム番号で表示・スナップすることで
//   「何フレーム目に何が起きるか」で設計できるようにする。
//
//  【ランタイムへの影響】
//   fps はエディタの表示・スナップ単位にすぎず、ランタイムのサンプリングは
//   一切参照しない（秒でしか評価しない）。旧 .anim には fps が無いため、
//   読み込み時は DefaultFps を採用する（Rust 側 #[serde(default)] と同値）。
//
//  本クラスは WPF に依存しない（editor/tests から直接リンクしてテストする）。
// ============================================================

using System;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>フレーム番号と秒の相互変換・スナップを担う純粋な計算クラス。</summary>
internal static class AnimFrameMath
{
    // ── fps の許容範囲 ──────────────────────────────────────────

    /// <summary>fps 未指定（旧 .anim）のときに採用する既定フレームレート。
    /// Rust 側 clip.rs の DEFAULT_EDIT_FPS と一致させること。</summary>
    public const float DefaultFps = 30f;

    /// <summary>編集で許す最小 fps（0 除算とフレーム数爆発を防ぐ下限）。</summary>
    public const float MinFps = 1f;

    /// <summary>編集で許す最大 fps（1 フレームがピクセル未満になるのを防ぐ上限）。</summary>
    public const float MaxFps = 240f;

    // ── fps の正規化 ────────────────────────────────────────────

    /// <summary>
    /// 入力 fps を編集に使える値へ正規化する。
    /// 0・負値・NaN・無限大は既定値へ落とし、範囲外はクランプする
    /// （不正な fps がそのまま除数になるのを一点で防ぐ）。
    /// </summary>
    public static float NormalizeFps(float fps)
    {
        if (float.IsNaN(fps) || float.IsInfinity(fps) || fps <= 0f) return DefaultFps;
        return Math.Clamp(fps, MinFps, MaxFps);
    }

    // ── 変換 ────────────────────────────────────────────────────

    /// <summary>秒 → 最も近いフレーム番号。負の時刻は 0 フレームへ丸める。</summary>
    public static int TimeToFrame(float time, float fps)
    {
        if (float.IsNaN(time)) return 0;
        var f = (int)MathF.Round(time * NormalizeFps(fps));
        return f < 0 ? 0 : f;
    }

    /// <summary>フレーム番号 → 秒。負のフレームは 0 秒として扱う。</summary>
    public static float FrameToTime(int frame, float fps)
        => frame <= 0 ? 0f : frame / NormalizeFps(fps);

    /// <summary>秒をフレーム境界へスナップする（丸め → 秒へ戻す）。</summary>
    public static float SnapTime(float time, float fps)
        => FrameToTime(TimeToFrame(time, fps), fps);

    /// <summary>
    /// 秒をフレーム境界へスナップし、[0, duration] へクランプする。
    /// duration 自体はフレーム境界とは限らないため、クランプはスナップの**後**に行う
    /// （末尾のキーを duration より後ろへ置かないことを優先する）。
    /// </summary>
    public static float ClampAndSnapTime(float time, float fps, float duration)
    {
        var snapped = SnapTime(time, fps);
        var max     = duration > 0f ? duration : 0f;
        return Math.Clamp(snapped, 0f, max);
    }

    /// <summary>
    /// 指定フレームを [0, 最終フレーム] へクランプする。
    /// 最終フレームは duration をフレーム換算して切り捨てた値。
    /// </summary>
    public static int ClampFrame(int frame, float fps, float duration)
    {
        var last = LastFrame(fps, duration);
        return Math.Clamp(frame, 0, last);
    }

    /// <summary>duration に収まる最後のフレーム番号（切り捨て。duration=0 なら 0）。</summary>
    public static int LastFrame(float fps, float duration)
    {
        if (duration <= 0f) return 0;
        var f = (int)MathF.Floor(duration * NormalizeFps(fps) + FloorEpsilon);
        return f < 0 ? 0 : f;
    }

    /// <summary>
    /// duration * fps の浮動小数誤差で最終フレームが 1 つ足りなくなるのを防ぐ許容値。
    /// 例: duration=1.0, fps=30 のとき 29.999999 になっても 30 と数えたい。
    /// </summary>
    private const float FloorEpsilon = 1e-4f;

    /// <summary>2 つの秒がフレーム単位で同一位置かを判定する（キーの上書き判定に使う）。</summary>
    public static bool SameFrame(float a, float b, float fps)
        => TimeToFrame(a, fps) == TimeToFrame(b, fps);
}
