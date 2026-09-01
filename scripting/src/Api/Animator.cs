namespace SEED;

/// <summary>
/// GameObject のアニメーター（AnimatorComponent）へのアクセサ。
/// Rust ランタイムのキーフレームアニメーション再生状態を FFI 経由で読み書きする薄いラッパー
/// （評価・トラック適用はエンジン側の AnimationSystem が毎フレーム行う。値はエンジンが保持）。
///
/// クリップはインスペクタで登録した <c>clips</c> 一覧からロードされ、Play 時点で
/// 既にロード済みでなければならない（フレーム先頭でエンジンが自動ロードするため、
/// Update 等のスクリプトライフサイクル内から呼ぶ限り通常は問題にならない）。
/// 未登録のクリップ名を指定した場合は警告ログを出して無視する。
/// </summary>
public readonly struct Animator : IComponentHandle<Animator>
{
    /// <summary>この Animator が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキー）。</summary>
    private const string Comp = "Animator";

    // ── 操作種別（Rust 側 host_api.rs の ANIMATOR_COMPONENT_* と一致させる）──
    private const int ActionPlay   = 0;
    private const int ActionStop   = 1;
    private const int ActionPause  = 2;
    private const int ActionResume = 3;
    private const int ActionCrossFade = 4;

    internal Animator(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<Animator>.ComponentKindName => Comp;
    static Animator IComponentHandle<Animator>.FromEntity(Entity slotEntity) => new(slotEntity);

    // ── 参照の生存判定 ─────────────────────────────────

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し Animator を保持しているか）。
    ///
    /// [SerializeField] の参照フィールドで「解決できたか／破棄されていないか」を
    /// 判定するために使う。<b>null は「未設定」</b>（Nullable 宣言のみ）を意味し、
    /// <b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// World が公開されていない場面（ライフサイクル外）でも false になる。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    // ── 再生操作 ─────────────────────────────────────────────

    /// <summary>
    /// 指定クリップを先頭（time=0）から再生する。再生速度は変更しない。
    /// フェード時間は <see cref="DefaultFadeSeconds"/>（既定 0 = 即時切替）を使う。
    /// </summary>
    public void Play(string clipName)
        => ScriptHost.AnimatorComponentAction(ActionPlay, _entity, clipName, float.NaN, float.NaN);

    /// <summary>指定クリップを先頭（time=0）から再生し、再生速度も同時に設定する。</summary>
    public void Play(string clipName, float speed)
        => ScriptHost.AnimatorComponentAction(ActionPlay, _entity, clipName, speed, float.NaN);

    /// <summary>再生速度とクロスフェード時間を同時に指定して再生する。</summary>
    public void Play(string clipName, float speed, float fadeSeconds)
        => ScriptHost.AnimatorComponentAction(ActionPlay, _entity, clipName, speed, fadeSeconds);

    /// <summary>
    /// 指定クリップへ <paramref name="fadeSeconds"/> 秒かけてクロスフェードする（再生速度は変更しない）。
    /// 0 以下を渡すと即時切替。補間されるのは glTF 内蔵アニメ（モデルクリップ）同士のときだけで、
    /// .anim キーフレームクリップが絡む切替は常に即時になる。
    /// </summary>
    public void CrossFade(string clipName, float fadeSeconds)
        => ScriptHost.AnimatorComponentAction(ActionCrossFade, _entity, clipName, float.NaN, fadeSeconds);

    /// <summary>再生を停止し、再生位置を先頭（time=0）へ戻す（フェード中ならフェードも破棄）。</summary>
    public void Stop() => ScriptHost.AnimatorComponentAction(ActionStop, _entity, "", 0f, 0f);

    /// <summary>現在の再生位置とフェード状態を保持したまま一時停止する。</summary>
    public void Pause() => ScriptHost.AnimatorComponentAction(ActionPause, _entity, "", 0f, 0f);

    /// <summary>一時停止していた再生を再開する（再生対象クリップが無ければ何もしない）。</summary>
    public void Resume() => ScriptHost.AnimatorComponentAction(ActionResume, _entity, "", 0f, 0f);

    // ── 状態プロパティ ───────────────────────────────────────

    /// <summary>再生中か。</summary>
    public bool IsPlaying
        => ScriptHost.TryGetBool(_entity, Comp, "playing", out var v) && v;

    /// <summary>現在再生中のクリップ名（未再生なら空文字）。</summary>
    public string CurrentClip
        => ScriptHost.TryGetString(_entity, Comp, "current_clip", out var s) ? s : "";

    /// <summary>再生位置（秒）。書き込みでシーク可能。</summary>
    public float Time
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "time", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "time", value);
    }

    /// <summary>再生速度倍率（1.0 = 等倍。負値で逆再生）。</summary>
    public float Speed
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "speed", out var v) ? v : 1f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "speed", value);
    }

    /// <summary>
    /// <see cref="Play(string)"/> がフェード時間を明示されなかったときに使う既定クロスフェード時間（秒）。
    /// 0 = 即時切替（既定）。インスペクタからも編集できる。負値は 0 にクランプされる。
    /// </summary>
    public float DefaultFadeSeconds
    {
        get => ScriptHost.TryGetFloat(_entity, Comp, "default_fade_seconds", out var v) ? v : 0f;
        set => ScriptHost.TrySetFloat(_entity, Comp, "default_fade_seconds", value);
    }

    /// <summary>
    /// 進行中クロスフェードのブレンド率（get のみ。0 = フェード元のみ / 1 = 現在クリップのみ）。
    /// フェードしていないときは常に 1。
    /// </summary>
    public float FadeWeight
        => ScriptHost.TryGetFloat(_entity, Comp, "fade_weight", out var v) ? v : 1f;
}
