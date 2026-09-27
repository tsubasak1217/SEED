using System.Runtime.InteropServices;

namespace SEEDEditor.Scripting;

/// <summary>
/// Rust 側 RawScrollEvent（#[repr(C)]。runtime/src/engine/core/scripting/scroll_ffi.rs）と同じメモリレイアウトの
/// スクロールのイベント（W2-3）。フィールドの並び・型を必ず一致させること（すべて 4 バイト・詰め物なし・16 個）。
///
/// Kind は Rust の ScrollEventKind の数値 = <see cref="SEED.ScrollEventKind"/>。値はスクロールの単位（キャンバスの単位）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeScrollEvent
{
    /// <summary>種類（SEED.ScrollEventKind の数値）。</summary>
    public int Kind;
    /// <summary>指でドラッグしているか（0 / 1）。</summary>
    public int Dragging;
    /// <summary>届け先のアクターのエンティティ（gameObject の束縛用）。</summary>
    public uint SelfIndex;
    public uint SelfGeneration;
    /// <summary>今の位置。</summary>
    public float PositionX;
    public float PositionY;
    /// <summary>前に知らせた位置からの差。</summary>
    public float DeltaX;
    public float DeltaY;
    /// <summary>速度（単位/秒）。</summary>
    public float VelocityX;
    public float VelocityY;
    /// <summary>位置の最大。</summary>
    public float MaxX;
    public float MaxY;
    /// <summary>窓の大きさ。</summary>
    public float ViewportW;
    public float ViewportH;
    /// <summary>中身の大きさ。</summary>
    public float ContentW;
    public float ContentH;
}
