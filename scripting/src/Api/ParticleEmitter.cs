namespace SEED;

/// <summary>
/// GameObject のパーティクルエミッタ（ParticleEmitterComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// エディタのインスペクタで設定したエミッタを Play() / Stop() で放出制御し、
/// Burst(n) で一括放出を積む。位置・向きは同じ GameObject の Transform が決める。
/// プロパティへの代入は即座に反映される（放出中のエミッタにも有効）。
/// エミッタを持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct ParticleEmitter : IComponentHandle<ParticleEmitter>
{
    /// <summary>この ParticleEmitter が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "ParticleEmitter";

    // ── 操作種別（Rust 側 host_api.rs の PARTICLE_COMPONENT_* と一致させる）──
    private const int ActionPlay = 0;
    private const int ActionStop = 1;
    private const int ActionBurst = 2;

    internal ParticleEmitter(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<ParticleEmitter>.ComponentKindName => Comp;
    static ParticleEmitter IComponentHandle<ParticleEmitter>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 放出操作 ─────────────────────────────────────────────

    /// <summary>放出を開始する（playing = true）。</summary>
    public void Play() => ScriptHost.ParticleComponentAction(ActionPlay, _entity, 0);

    /// <summary>放出を停止する（playing = false）。既存パーティクルは寿命で消える。</summary>
    public void Stop() => ScriptHost.ParticleComponentAction(ActionStop, _entity, 0);

    /// <summary>
    /// 指定個数を即時一括放出する（emit_rate による継続放出とは独立）。
    /// リクエストは蓄積され、GPU パーティクルシステムが次フレームで消費する。
    /// count が 0 以下なら何もしない。
    /// </summary>
    public void Burst(int count) => ScriptHost.ParticleComponentAction(ActionBurst, _entity, count);

    // ── 設定プロパティ ───────────────────────────────────────

    /// <summary>放出中か（get/set。set は Play()/Stop() と同じく playing を直接切り替える）。</summary>
    public bool Playing
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "playing", out var v) && v;
        set => ScriptHost.TrySetBool(_entity, Comp, "playing", value);
    }

    /// <summary>放出中か（<see cref="Playing"/> の get 別名。AudioSource.IsPlaying と対をなす）。</summary>
    public bool IsPlaying => Playing;

    /// <summary>放出レート（1 秒あたりの放出個数）。負値は 0 にクランプされる。</summary>
    public float EmitRate
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "emit_rate", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "emit_rate", value);
    }

    /// <summary>寿命ループ放出するか（false なら一巡で停止）。</summary>
    public bool LoopEmit
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "loop_emit", out var v) && v;
        set => ScriptHost.TrySetBool(_entity, Comp, "loop_emit", value);
    }

    /// <summary>空気抵抗係数。負値は 0 にクランプされる。</summary>
    public float Drag
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "drag", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "drag", value);
    }

    /// <summary>放出円錐の半頂角（度）。0〜180 にクランプされる。</summary>
    public float SpreadAngle
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "spread_angle_deg", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "spread_angle_deg", value);
    }
}
