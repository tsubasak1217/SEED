namespace SEED;

/// <summary>
/// GameObject の位置・回転・スケールへのアクセサ。Rust ランタイムの Transform
/// コンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// プロパティへの代入は即座にゲーム世界へ反映される。回転は YXZ オイラー角（度）。
/// </summary>
public readonly struct Transform
{
    /// <summary>この Transform が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "Transform";

    internal Transform(Entity entity) { _entity = entity; }

    /// <summary>ワールド位置。</summary>
    public Vector3 Position
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "position", out var v) ? v : Vector3.Zero;
        set => ScriptHost.TrySetVec3(_entity, Comp, "position", value);
    }

    /// <summary>回転（YXZ オイラー角・度）。</summary>
    public Vector3 Rotation
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "rotation", out var v) ? v : Vector3.Zero;
        set => ScriptHost.TrySetVec3(_entity, Comp, "rotation", value);
    }

    /// <summary>スケール。</summary>
    public Vector3 Scale
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "scale", out var v) ? v : Vector3.One;
        set => ScriptHost.TrySetVec3(_entity, Comp, "scale", value);
    }
}
