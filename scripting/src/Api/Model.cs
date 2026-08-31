namespace SEED;

/// <summary>
/// GameObject の 3D モデル（ModelComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// 現在公開しているのは <b>描画オフセットトランスフォーム</b>（位置・回転・スケール）で、
/// アクターの <see cref="Transform"/> を動かさずに「モデルの見た目だけ」をローカルにずらす補正値。
/// 用途はモデルの原点ズレ補正や、手に持たせた道具（釣り竿など）のグリップ位置合わせ。
///
/// <para><b>描画専用</b>: オフセットは物理コライダー・レイキャスト・Transform には
/// 一切影響しない。当たり判定を動かしたい場合はコライダー側のオフセットを使うこと。</para>
///
/// プロパティへの代入は即座に描画へ反映される。
/// モデルを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct Model : IComponentHandle<Model>
{
    /// <summary>この Model が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "Model";

    internal Model(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<Model>.ComponentKindName => Comp;
    static Model IComponentHandle<Model>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し Model を保持しているか）。
    /// [SerializeField] の参照フィールドで解決済み／破棄済みを判定するために使う。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>
    /// 描画オフセットの位置（アクターのローカル空間・既定 (0,0,0)）。
    /// アクターが回転していればオフセットも一緒に回る。
    /// </summary>
    public Vector3 OffsetPosition
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "offset_position", out var v) ? v : Vector3.Zero;
        set => ScriptHost.TrySetVec3(_entity, Comp, "offset_position", value);
    }

    /// <summary>
    /// 描画オフセットの回転（YXZ オイラー角・度・既定 (0,0,0)）。
    /// 回転規約は <see cref="Transform"/>.Rotation と同一。
    /// </summary>
    public Vector3 OffsetRotation
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "offset_rotation", out var v) ? v : Vector3.Zero;
        set => ScriptHost.TrySetVec3(_entity, Comp, "offset_rotation", value);
    }

    /// <summary>描画オフセットのスケール（既定 (1,1,1)）。</summary>
    public Vector3 OffsetScale
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "offset_scale", out var v) ? v : Vector3.One;
        set => ScriptHost.TrySetVec3(_entity, Comp, "offset_scale", value);
    }
}
