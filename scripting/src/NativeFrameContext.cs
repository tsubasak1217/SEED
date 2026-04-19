using System.Runtime.InteropServices;

namespace SEED.Scripting;

/// <summary>
/// Rust 側の FrameContext と同じメモリレイアウト（#[repr(C)]）。
/// フィールド順・型を必ず一致させること。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeFrameContext
{
    public float DeltaTime;
    public float AnimTime;
}
