using System.Runtime.InteropServices;

namespace SEEDEditor.Scripting;

/// <summary>
/// Rust 側 RawGestureEvent（#[repr(C)]。runtime/src/engine/core/scripting/gesture_ffi.rs）と同じメモリレイアウトの
/// ジェスチャーのイベント（W2-2）。フィールドの並び・型を必ず一致させること（すべて 4 バイト・詰め物なし・19 個）。
///
/// Kind は Rust の GestureEventKind の数値 = <see cref="SEED.GestureKind"/>。
/// 位置はキャンバスの画素（画面の中央が原点・Y 下向き）、Screen* は画面の画素（左上原点）、
/// Local* はノードのローカル（見た目の矩形の左上が原点・ノードの単位）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeGestureEvent
{
    /// <summary>種類（SEED.GestureKind の数値）。</summary>
    public int Kind;
    /// <summary>指の番号（0 起点）。</summary>
    public int PointerId;
    /// <summary>届け先のアクターのエンティティ（gameObject の束縛用）。</summary>
    public uint SelfIndex;
    public uint SelfGeneration;
    /// <summary>今の位置（キャンバスの画素）。</summary>
    public float PositionX;
    public float PositionY;
    /// <summary>今の位置（画面の画素・左上原点）。</summary>
    public float ScreenX;
    public float ScreenY;
    /// <summary>今の位置（ノードのローカル・ノードの単位）。</summary>
    public float LocalX;
    public float LocalY;
    /// <summary>押した位置（キャンバスの画素）。</summary>
    public float StartX;
    public float StartY;
    /// <summary>移動量（画素）。</summary>
    public float DeltaX;
    public float DeltaY;
    /// <summary>速度（画素/秒）。</summary>
    public float VelocityX;
    public float VelocityY;
    /// <summary>1 dp の画素数。</summary>
    public float DpScale;
    /// <summary>押してからの時間（秒）。</summary>
    public float Duration;
    /// <summary>取り消しで終わったか（0 / 1）。</summary>
    public int Canceled;
}
