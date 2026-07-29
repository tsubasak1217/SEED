using System.Runtime.InteropServices;

namespace SEEDEditor.Scripting;

/// <summary>
/// Rust 側 RawPhysicsEvent（#[repr(C)]）と同じメモリレイアウトの物理イベント。
/// フィールド順・型を runtime/.../scripting/mod.rs と必ず一致させること。
///
/// Kind: 0=CollisionEnter / 1=CollisionStay / 2=CollisionExit /
///       3=TriggerEnter / 4=TriggerExit / 5=TriggerStay（ScriptBridge の定数と一致）
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativePhysicsEvent
{
    /// <summary>イベント種別（上記コメント参照）。</summary>
    public int Kind;
    /// <summary>通知先スクリプトが乗るアクターのエンティティ（gameObject の束縛用）。</summary>
    public uint SelfIndex;
    public uint SelfGeneration;
    /// <summary>衝突相手アクターのエンティティ（uint.MaxValue = 不明）。</summary>
    public uint OtherIndex;
    public uint OtherGeneration;
}
