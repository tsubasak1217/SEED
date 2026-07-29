namespace SEED;

/// <summary>
/// GameObject のオーディオソース（AudioComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
///
/// エディタのインスペクタで設定した音源を Play() / Stop() で制御する。
/// Spatial = true にするとメインカメラとの距離で減衰し、方向に応じてパンが振られる。
/// プロパティへの代入は即座に反映される（再生中の音源にも有効）。
/// </summary>
public readonly struct AudioSource : IComponentHandle<AudioSource>
{
    /// <summary>この AudioSource が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "Audio";

    // ── 操作種別（Rust 側 host_api.rs の AUDIO_COMPONENT_* と一致させる）──
    private const int ActionPlay = 0;
    private const int ActionStop = 1;
    private const int ActionIsPlaying = 2;

    internal AudioSource(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<AudioSource>.ComponentKindName => Comp;
    static AudioSource IComponentHandle<AudioSource>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 参照の生存判定 ─────────────────────────────────

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し AudioSource を保持しているか）。
    ///
    /// [SerializeField] の参照フィールドで「解決できたか／破棄されていないか」を
    /// 判定するために使う。<b>null は「未設定」</b>（Nullable 宣言のみ）を意味し、
    /// <b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// World が公開されていない場面（ライフサイクル外）でも false になる。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    // ── 再生操作 ─────────────────────────────────────────────

    /// <summary>設定された音源を再生する（再生中なら最初から鳴らし直す）。フレーム末尾に適用。</summary>
    public void Play() => ScriptHost.AudioComponentAction(ActionPlay, _entity);

    /// <summary>再生を停止する。フレーム末尾に適用。</summary>
    public void Stop() => ScriptHost.AudioComponentAction(ActionStop, _entity);

    /// <summary>再生中か。</summary>
    public bool IsPlaying => ScriptHost.AudioComponentAction(ActionIsPlaying, _entity);

    // ── 設定プロパティ ───────────────────────────────────────

    /// <summary>音声ファイルパス（assets:// 仮想パス）。</summary>
    public string Path
    {
        get => ScriptHost.TryGetString(_entity, Comp, "audio_path", out var s) ? s : "";
        set => ScriptHost.TrySetString(_entity, Comp, "audio_path", value);
    }

    /// <summary>音量（1.0 = 等倍）。再生中でも即座に反映される。</summary>
    public float Volume
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "volume", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "volume", value);
    }

    /// <summary>ループ再生するか（次回 Play 時に反映）。</summary>
    public bool Loop
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "loop", out var v) && v;
        set => ScriptHost.TrySetBool(_entity, Comp, "loop", value);
    }

    /// <summary>Play 開始時に自動再生するか。</summary>
    public bool PlayOnStart
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "play_on_start", out var v) && v;
        set => ScriptHost.TrySetBool(_entity, Comp, "play_on_start", value);
    }

    /// <summary>3D 空間再生（メインカメラとの距離減衰 + 方向パン）を使うか。</summary>
    public bool Spatial
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "spatial", out var v) && v;
        set => ScriptHost.TrySetBool(_entity, Comp, "spatial", value);
    }

    /// <summary>減衰開始距離（これ以内は音量 100%。Spatial 時のみ有効）。</summary>
    public float MinDistance
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "min_distance", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "min_distance", value);
    }

    /// <summary>無音距離（これ以遠は聞こえない。Spatial 時のみ有効）。</summary>
    public float MaxDistance
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "max_distance", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "max_distance", value);
    }

    /// <summary>左右パン（-1.0=左 〜 0=中央 〜 1.0=右。Spatial=false 時のみ有効）。</summary>
    public float Pan
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "pan", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "pan", value);
    }
}
