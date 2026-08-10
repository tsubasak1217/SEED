namespace SEED;

/// <summary>
/// GameObject の位置・回転・スケールへのアクセサ。Rust ランタイムの Transform
/// コンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// プロパティへの代入は即座にゲーム世界へ反映される。回転は YXZ オイラー角（度）。
/// </summary>
public readonly struct Transform : IComponentHandle<Transform>
{
    /// <summary>この Transform が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "Transform";

    internal Transform(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<Transform>.ComponentKindName => Comp;
    static Transform IComponentHandle<Transform>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 参照の生存判定 ─────────────────────────────────

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し Transform を保持しているか）。
    ///
    /// [SerializeField] の参照フィールドで「解決できたか／破棄されていないか」を
    /// 判定するために使う。<b>null は「未設定」</b>（Nullable 宣言のみ）を意味し、
    /// <b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// World が公開されていない場面（ライフサイクル外）でも false になる。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    /// <summary>ワールド位置。</summary>
    public Vector3 Position
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "position", out var v) ? v : Vector3.Zero;
        set => ScriptHost.TrySetVec3(_entity, Comp, "position", value);
    }

    /// <summary>
    /// ワールド絶対座標（get のみ）。
    ///
    /// SEED の 3D Transform は常にワールド空間で保持されるため <see cref="Position"/> と
    /// 同値（親子階層はエディタ操作時のみ連動し、実行時のローカル座標系は持たない）。
    /// 「絶対座標が欲しい」意図を明示したいときにこちらを使う。
    /// </summary>
    public Vector3 WorldPosition => Position;

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

    // ── 方向ベクトル（すべて get のみ・ワールド空間・正規化済み）─────────
    //
    // 値は Rust 側 Transform::rotation_basis()（YXZ オイラーの回転規約の正典）が
    // 算出した基底をそのまま受け取る。C# 側でオイラー→行列を再実装しないこと
    // （回転規約が二重管理になり、片方だけ直したときに静かにずれる）。
    // SEED のローカル前方向は +Z なので、回転 0 のとき Forward == (0,0,1)。

    /// <summary>前方向（ワールド空間・正規化済み。回転 0 のとき +Z）。</summary>
    public Vector3 Forward
        => ScriptHost.TryGetVec3(_entity, Comp, "forward", out var v) ? v : Vector3.Forward;

    /// <summary>後方向（<see cref="Forward"/> の反転）。</summary>
    public Vector3 Back => -Forward;

    /// <summary>右方向（ワールド空間・正規化済み。回転 0 のとき +X）。</summary>
    public Vector3 Right
        => ScriptHost.TryGetVec3(_entity, Comp, "right", out var v) ? v : Vector3.Right;

    /// <summary>左方向（<see cref="Right"/> の反転）。</summary>
    public Vector3 Left => -Right;

    /// <summary>上方向（ワールド空間・正規化済み。回転 0 のとき +Y）。</summary>
    public Vector3 Up
        => ScriptHost.TryGetVec3(_entity, Comp, "up", out var v) ? v : Vector3.Up;

    /// <summary>下方向（<see cref="Up"/> の反転）。</summary>
    public Vector3 Down => -Up;

    /// <summary>
    /// キャラクターコントローラーを衝突無視で瞬間移動させる。
    ///
    /// <see cref="Position"/> への代入と異なり、地形との衝突解決（自動押し戻し）を
    /// 発生させずに位置を <paramref name="pos"/> に設定する（子アクタも追従）。
    /// 物理側の「前回位置」も同時にリセットされるため、瞬間移動先で押し戻されない。
    /// ワープ・リスポーン・シーン開始時の初期配置などに使う。
    /// </summary>
    /// <param name="pos">瞬間移動先のワールド座標</param>
    public void Teleport(Vector3 pos) => ScriptHost.TryTeleport(_entity, pos);
}
