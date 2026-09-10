using System.Runtime.InteropServices;

namespace SEEDEditor.Scripting;

/// <summary>
/// Rust 側の FrameContext と同じメモリレイアウト（#[repr(C)]）。
/// フィールド順・型を必ず一致させること。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeFrameContext
{
    public float DeltaTime;
    public float AnimTime;

    // ── エンジン内部用 ──────────────────────────────────────
    // このスクリプトが乗る GameObject（所有 Entity）の識別子。
    // ゲームロジックでは直接使わず、SEEDScript.gameObject / transform 経由でアクセスする。
    public uint EntityIndex;
    public uint EntityGeneration;

    // ── 時間スケール未適用の時間（Rust 側 RawFrameContext の末尾と同順）──
    // 既存フィールドのオフセットを動かさないため、必ず末尾へ追加すること。

    /// <summary>時間スケール未適用の前フレーム経過秒（SEED.Time.UnscaledDeltaTime）。</summary>
    public float UnscaledDeltaTime;
    /// <summary>時間スケール未適用のゲーム内累計秒（SEED.Time.UnscaledElapsedTime）。</summary>
    public float UnscaledElapsedTime;

    // ── フレーム実測値（Rust 側 RawFrameContext の末尾と同順）──
    // 上と同じ理由で必ず末尾へ追加すること。
    // これらは Clock（ゲーム時間）ではなくフレーム制御（frame_pacing）が集計した実測値。

    /// <summary>直近 1 秒の平均フレームレート（SEED.Time.Fps）。</summary>
    public float Fps;
    /// <summary>直近フレームの実時間（ミリ秒。SEED.Time.FrameTimeMs）。</summary>
    public float FrameTimeMs;
}
