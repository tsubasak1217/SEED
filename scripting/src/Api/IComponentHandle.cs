namespace SEED;

/// <summary>
/// GameObject.GetComponent&lt;T&gt;() が扱えるコンポーネントハンドル（Transform / Camera など）が
/// 実装する契約。C# 11 の static abstract interface members を使い、型 T だけから
/// 「Rust 側コンポーネント種別名」と「スロット entity からのハンドル生成」を静的に取得する。
///
/// 各ラッパー struct は明示的インターフェース実装（<c>static string IComponentHandle&lt;T&gt;.ComponentKindName</c>）
/// でメンバを実装するため、public な型表面は汚さない（呼び出しは GetComponent 経由の T.〜 でのみ行われる）。
///
/// 【重要】<see cref="ComponentKindName"/> は Rust 側 host_api.rs の解決キー
/// （"Transform"/"CanvasTransform"/"Sprite"/"Camera"/"Audio"/"Animator"/"ParticleEmitter"/"InputMap"）と
/// 大文字小文字まで完全一致させること。ずれるとコンパイルは通るのに実行時に解決できない。
/// </summary>
/// <typeparam name="TSelf">実装するハンドル型自身（CRTP）。</typeparam>
public interface IComponentHandle<TSelf> where TSelf : struct
{
    /// <summary>Rust 側 ComponentKind 判別文字列（解決キー）。</summary>
    static abstract string ComponentKindName { get; }

    /// <summary>解決済みスロット entity からハンドルを生成する。</summary>
    static abstract TSelf FromEntity(Entity slotEntity);
}
