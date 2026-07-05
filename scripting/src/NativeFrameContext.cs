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
}
